module Yession.Tests.GitGateway

// The git gateway: a sandbox's git reaches github.com through this session, which lends the
// credential per request, so the sandbox never holds one. The pure half (what the gateway
// admits, and what a sandbox is told) runs in the cheap tier. The [Ports] suite drives a
// REAL git at a real gateway in front of a stand-in github.com — a recording upstream for the
// credential, and `git http-backend` over a bare repo for the push — because the thing under
// test is a wire git speaks, and the only reader of that wire worth believing is git.

open System
open Fable.Core
open Fable.Core.JsInterop
open Fable.Pyxpecto
open Yession.Domain
open Yession.Domain.Sandboxes
open Yession.Host
open Yession.Host.Interop
open Yession.Tests.Support

let private sandbox (raw: string) = SandboxRef.parse raw |> expect
let private ada = UserRef (UserId.create "ada" |> expect)

// --- cheap: what is admitted, and what a sandbox is told ------------------------------------

let private routeTests =
    testList "what the gateway admits" [

        testCase "the advertisement and the two service posts, and nothing else" <| fun () ->
            let cap = "3f0a"
            let adv = GitGateway.route "GET" (sprintf "/git/%s/github.com/octo/hello.git/info/refs" cap) (Some "git-upload-pack")
            Expect.equal
                (adv |> Option.map (fun r -> r.Path, r.Service))
                (Some ("octo/hello.git/info/refs", "git-upload-pack"))
                "the fetch advertisement"
            let push = GitGateway.route "POST" (sprintf "/git/%s/github.com/octo/hello.git/git-receive-pack" cap) None
            Expect.equal
                (push |> Option.map (fun r -> r.Path, r.Service))
                (Some ("octo/hello.git/git-receive-pack", "git-receive-pack"))
                "the push"
            // A cap admits git to a repository, not a person to github.com.
            for method, path, service in
                [ "GET", sprintf "/git/%s/github.com/octo/hello.git/info/refs" cap, None
                  "GET", sprintf "/git/%s/github.com/octo/hello.git/info/refs" cap, Some "git-anything"
                  "GET", sprintf "/git/%s/github.com/octo/hello.git/HEAD" cap, None
                  "GET", sprintf "/git/%s/github.com/octo" cap, None
                  "GET", sprintf "/git/%s/api.github.com/octo/hello.git/info/refs" cap, Some "git-upload-pack"
                  "POST", sprintf "/git/%s/github.com/octo/hello.git/info/refs" cap, Some "git-upload-pack"
                  "GET", sprintf "/git/%s/github.com/../hello.git/info/refs" cap, Some "git-upload-pack"
                  "GET", "/git//github.com/octo/hello.git/info/refs", Some "git-upload-pack"
                  "GET", "/", None ] do
                Expect.isNone (GitGateway.route method path service) (sprintf "%s %s is not admitted" method path)

        testCase "a sandbox is told one insteadOf, and it names the real host" <| fun () ->
            match GitGateway.gitConfig "host.docker.internal" 4321 "3f0a" with
            | [ key, value ] ->
                Expect.equal key "url.http://host.docker.internal:4321/git/3f0a/github.com/.insteadOf" "the rewrite"
                Expect.equal value "https://github.com/" "of the address a remote is written as"
            | other -> failwithf "expected one entry, got %A" other

        // Where a sandbox reaches this process is the backend's fact, and srt's splits by
        // platform for a reason a test on either box can check: Linux loopback is 127/8 and
        // macOS configures `.1` alone, while srt's `NO_PROXY` covers `127.0.0.1` on both.
        testCase "each backend names the host by what its sandbox can reach" <| fun () ->
            Expect.equal (Sandboxes.hostAddressFrom "box.local" "darwin" DockerBackend) (Some "host.docker.internal") "docker, by the daemon's alias"
            Expect.equal (Sandboxes.hostAddressFrom "box.local" "linux" HostBackend) (Some "127.0.0.1") "host, loopback"
            Expect.equal (Sandboxes.hostAddressFrom "box.local" "darwin" SrtBackend) (Some "box.local") "srt on macOS: the box's name"
            Expect.equal (Sandboxes.hostAddressFrom "runner" "linux" SrtBackend) (Some "127.0.0.2") "srt on Linux: a loopback address NO_PROXY does not name"

        // The pkt-line header counts BYTES. A message with an em dash in it, counted in
        // characters, arrived at git one byte short and printed with its last letter gone.
        testCase "a pkt-line's length counts bytes, not characters" <| fun () ->
            Expect.equal (GitGateway.pktLine "a\n") "0006a\n" "ascii"
            Expect.equal (GitGateway.pktLine "—\n") "0008—\n" "an em dash is three bytes"

        // Git's config env is one counter shared by everything with something to tell git,
        // and the docker baseline already spends slot 0 on `safe.directory`. A forwarded
        // credential that SET the trio replaced that slot, and every mounted checkout went
        // back to "dubious ownership" the moment somebody forwarded github.
        testCase "git config is appended after whatever the env already tells git" <| fun () ->
            let baseline =
                Map.ofList
                    [ "GIT_CONFIG_COUNT", "1"
                      "GIT_CONFIG_KEY_0", "safe.directory"
                      "GIT_CONFIG_VALUE_0", "*" ]
            let env = Sandboxes.withGitConfig [ "url.x.insteadOf", "https://github.com/" ] baseline
            Expect.equal (Map.tryFind "GIT_CONFIG_COUNT" env) (Some "2") "one more"
            Expect.equal (Map.tryFind "GIT_CONFIG_KEY_0" env) (Some "safe.directory") "slot 0 kept"
            Expect.equal (Map.tryFind "GIT_CONFIG_KEY_1" env) (Some "url.x.insteadOf") "the new one after it"
            Expect.equal (Map.tryFind "GIT_CONFIG_VALUE_1" env) (Some "https://github.com/") "with its value"
            let fresh = Sandboxes.withGitConfig [ "a", "b" ] Map.empty
            Expect.equal (Map.tryFind "GIT_CONFIG_COUNT" fresh) (Some "1") "and from nothing, one"
            Expect.equal (Map.tryFind "GIT_CONFIG_KEY_0" fresh) (Some "a") "at slot 0"
            Expect.equal (Sandboxes.withGitConfig [] baseline) baseline "nothing to add changes nothing"
    ]

// --- [Ports]: a real git, at a real gateway ------------------------------------------------

let private nodeFs : obj = importAll "node:fs"
let private nodeOs : obj = importAll "node:os"
let private nodeHttp : obj = importAll "node:http"
let private childProcess : obj = importAll "node:child_process"

[<Emit("$0.mkdtempSync($1.tmpdir() + '/yession-gateway-')")>]
let private mkdtemp (fs: obj) (os: obj) : string = jsNative

[<Emit("$0.mkdirSync($1, { recursive: true })")>]
let private mkdir (fs: obj) (path: string) : unit = jsNative

[<Emit("$0.writeFileSync($1, $2)")>]
let private writeFile (fs: obj) (path: string) (content: string) : unit = jsNative

[<Emit("$0.rmSync($1, { recursive: true, force: true })")>]
let private rmrf (fs: obj) (path: string) : unit = jsNative

/// What one git run said. The sandbox-side git under test is judged by its stdout and by
/// the sentence on its stderr, which is the whole point of the `ERR` channel.
type private GitRun =
    { Status : int
      Stdout : string
      Stderr : string }

/// Run git as a sandbox would: no config of this box's, no prompt, a fixed identity, and
/// whatever the gateway told it on top. Asynchronous of necessity: the gateway git is
/// talking to runs on THIS event loop, and a synchronous spawn would hold it while git
/// waited for an answer that could then never come.
[<Emit("""(function (cp, args, cwd, extra) {
  const env = { ...process.env, GIT_CONFIG_GLOBAL: '/dev/null', GIT_CONFIG_SYSTEM: '/dev/null', GIT_TERMINAL_PROMPT: '0',
                GIT_AUTHOR_NAME: 'fixture', GIT_AUTHOR_EMAIL: 'f@x', GIT_COMMITTER_NAME: 'fixture', GIT_COMMITTER_EMAIL: 'f@x' }
  for (const [k, v] of extra) env[k] = v
  return new Promise((resolve) => {
    cp.execFile('git', args, { cwd, env, encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 }, (error, stdout, stderr) => {
      resolve({ Status: error ? (typeof error.code === 'number' ? error.code : -1) : 0, Stdout: stdout ?? '', Stderr: stderr ?? '' })
    })
  })
})($0, $1, $2, $3)""")>]
let private gitRun (cp: obj) (args: string array) (cwd: string) (extra: (string * string) array) : JS.Promise<GitRun> = jsNative

let private git (args: string list) (cwd: string) (env: Map<string, string>) : Async<GitRun> =
    gitRun childProcess (List.toArray args) cwd (Map.toArray env) |> awaitPromise

/// Fixture git: must succeed, or the case is not testing what it says.
let private gitOk (args: string list) (cwd: string) : Async<string> =
    async {
        let! run = git args cwd Map.empty
        if run.Status <> 0 then failwithf "fixture git %A failed: %s" args run.Stderr
        return run.Stdout
    }

/// A stand-in for github.com's git endpoint that records what it was asked and answers a
/// canned advertisement — enough for `ls-remote`, which is the shortest real conversation
/// git has, and the one every other begins with.
type private Upstream =
    { Origin : string
      /// The `authorization` header of every request, in order — `None` when a request
      /// arrived without one, which is its own finding.
      Authorizations : ResizeArray<string option>
      Paths : ResizeArray<string>
      /// The status to answer with; a 200 carries the advertisement. A cell, so a case
      /// flips what the handler reads.
      Answer : int ref
      Close : unit -> Async<unit> }

let private advertisement (sha: string) =
    GitGateway.pktLine "# service=git-upload-pack\n"
    + "0000"
    + GitGateway.pktLine (sprintf "%s refs/heads/main\000\n" sha)
    + "0000"

let private startUpstream () : Async<Upstream> =
    let authorizations = ResizeArray<string option> ()
    let paths = ResizeArray<string> ()
    let answer = ref 200
    let server =
        createServer (fun req res ->
            authorizations.Add (headerOf req "authorization")
            paths.Add req.url
            if answer.Value = 200 then
                res.writeHead (200, createObj [ "content-type", box "application/x-git-upload-pack-advertisement" ]) |> ignore
                res.``end`` (advertisement "1111111111111111111111111111111111111111")
            else
                res.writeHead (answer.Value, createObj [ "content-type", box "text/plain"; "www-authenticate", box "Basic realm=\"GitHub\"" ]) |> ignore
                res.``end`` "no")
    async {
        do! Async.FromContinuations (fun (cont, _, _) -> server.listen (0, "127.0.0.1", fun () -> cont ()) |> ignore)
        return
            { Origin = sprintf "http://127.0.0.1:%d" (serverPort server)
              Authorizations = authorizations
              Paths = paths
              Answer = answer
              Close = fun () -> Async.FromContinuations (fun (cont, _, _) -> server.close (fun _ -> cont ())) }
    }

/// A lender whose answers a case controls: the token now (read at every request, so a case
/// that sets it changes the next answer), and how many times github.com was reported to
/// have refused it.
type private Lend =
    { mutable Token : string option
      mutable Refusals : int }

let private lending (token: string option) : Lend = { Token = token; Refusals = 0 }

let private lenderOf (lend: Lend) : GitGateway.Lender =
    { Owner = ada
      Resolve = fun () -> async { return lend.Token }
      Refused = fun () -> async { lend.Refusals <- lend.Refusals + 1 } }

let private basic (token: string) =
    "Basic " + Convert.ToBase64String (Text.Encoding.UTF8.GetBytes ("x-access-token:" + token))

/// The env a sandbox would get for a gateway on this box — and NOTHING else: no token, no
/// helper, no header. What the case then asserts is that git reaches github.com anyway.
let private sandboxEnv (gateway: GitGateway.Gateway) (cap: string) : Map<string, string> =
    Sandboxes.withGitConfig (GitGateway.gitConfig "127.0.0.1" gateway.Port cap) Map.empty

let private withGateway (upstream: string) (body: GitGateway.Gateway -> Async<unit>) : Async<unit> =
    async {
        let! gateway = GitGateway.start upstream
        try
            do! body gateway
        finally
            gateway.Close () |> Async.StartImmediate
    }

let private portsTests =
    testList "a real git at the gateway" [

        // The invariant everything else rests on: a git that was told nothing but the gateway
        // config, naming github.com by its real address, reaches it — and the credential it
        // never had is on the request that arrives.
        testCaseAsync "a sandbox's git names github.com and arrives with the credential it never held" <| async {
            let! upstream = startUpstream ()
            do!
                withGateway upstream.Origin (fun gateway ->
                    async {
                        let cap = gateway.Grant (sandbox "octo/hello:dev") (lenderOf (lending (Some "ghu_lent")))
                        let env = sandboxEnv gateway cap
                        Expect.isFalse (env |> Map.exists (fun _ v -> v.Contains "ghu_lent")) "the sandbox env carries no token"
                        let! run = git [ "ls-remote"; "https://github.com/octo/hello.git" ] "." env
                        Expect.equal run.Status 0 (sprintf "ls-remote succeeded: %s" run.Stderr)
                        Expect.isTrue (run.Stdout.Contains "refs/heads/main") "and read the upstream's refs"
                        Expect.equal (List.ofSeq upstream.Authorizations) [ Some (basic "ghu_lent") ] "github.com saw the lent credential, as basic auth"
                        Expect.isTrue (upstream.Paths.[0].StartsWith "/octo/hello.git/info/refs") "at the repository's own path"
                    })
            do! upstream.Close ()
        }

        // Resolved PER REQUEST, which is what lets a refreshed credential reach a sandbox that
        // was started before the refresh — the gap the env-injected token could not close.
        testCaseAsync "the credential is the lender's answer at the time of each request" <| async {
            let! upstream = startUpstream ()
            do!
                withGateway upstream.Origin (fun gateway ->
                    async {
                        let lend = lending (Some "first")
                        let cap = gateway.Grant (sandbox "octo/hello:dev") (lenderOf lend)
                        let env = sandboxEnv gateway cap
                        let! first = git [ "ls-remote"; "https://github.com/octo/hello.git" ] "." env
                        Expect.equal first.Status 0 "first"
                        lend.Token <- Some "second"
                        let! second = git [ "ls-remote"; "https://github.com/octo/hello.git" ] "." env
                        Expect.equal second.Status 0 "second"
                        Expect.equal (List.ofSeq upstream.Authorizations) [ Some (basic "first"); Some (basic "second") ] "each request carried the credential of its moment"
                    })
            do! upstream.Close ()
        }

        // The channel git can show a person. Relaying the 401 would have made git prompt for
        // a password nobody can type, which under `GIT_TERMINAL_PROMPT=0` is "could not read
        // Username" — the sentence that cost the motivating session three rounds of guessing.
        testCaseAsync "a credential the lender no longer has is said in words git prints" <| async {
            let! upstream = startUpstream ()
            do!
                withGateway upstream.Origin (fun gateway ->
                    async {
                        let cap = gateway.Grant (sandbox "octo/hello:dev") (lenderOf (lending None))
                        let! run = git [ "ls-remote"; "https://github.com/octo/hello.git" ] "." (sandboxEnv gateway cap)
                        Expect.isTrue (run.Status <> 0) "it fails"
                        Expect.isTrue (run.Stderr.Contains "remote error:") (sprintf "on the remote-error channel: %s" run.Stderr)
                        Expect.isTrue (run.Stderr.Contains "settings panel") "and says where to fix it"
                        Expect.isFalse (run.Stderr.Contains "Username") "never a credential prompt"
                        Expect.equal upstream.Authorizations.Count 0 "and github.com was not asked"
                    })
            do! upstream.Close ()
        }

        testCaseAsync "a credential github.com rejects is said in words, and reported" <| async {
            let! upstream = startUpstream ()
            upstream.Answer.Value <- 401
            do!
                withGateway upstream.Origin (fun gateway ->
                    async {
                        let lend = lending (Some "ghu_stale")
                        let cap = gateway.Grant (sandbox "octo/hello:dev") (lenderOf lend)
                        let! run = git [ "ls-remote"; "https://github.com/octo/hello.git" ] "." (sandboxEnv gateway cap)
                        Expect.isTrue (run.Status <> 0) "it fails"
                        Expect.isTrue (run.Stderr.Contains "remote error:") (sprintf "on the remote-error channel: %s" run.Stderr)
                        Expect.isTrue (run.Stderr.Contains "rejected") "saying github refused it"
                        Expect.isFalse (run.Stderr.Contains "Username") "never a credential prompt"
                        Expect.equal lend.Refusals 1 "and whoever tracks the credential's health was told once"
                    })
            do! upstream.Close ()
        }

        // A route is a thing the session OPENED. It dies with the sandbox, or it is a route
        // anybody who copied the cap keeps.
        testCaseAsync "a revoked route admits nothing, and github.com is not asked" <| async {
            let! upstream = startUpstream ()
            do!
                withGateway upstream.Origin (fun gateway ->
                    async {
                        let cap = gateway.Grant (sandbox "octo/hello:dev") (lenderOf (lending (Some "tok")))
                        gateway.Revoke (sandbox "octo/hello:dev")
                        let! run = git [ "ls-remote"; "https://github.com/octo/hello.git" ] "." (sandboxEnv gateway cap)
                        Expect.isTrue (run.Status <> 0) "it fails"
                        Expect.equal upstream.Authorizations.Count 0 "and nothing reached github.com"
                    })
            do! upstream.Close ()
        }

        testCaseAsync "a second grant for the same sandbox retires the first" <| async {
            let! upstream = startUpstream ()
            do!
                withGateway upstream.Origin (fun gateway ->
                    async {
                        let first = gateway.Grant (sandbox "octo/hello:dev") (lenderOf (lending (Some "one")))
                        let second = gateway.Grant (sandbox "octo/hello:dev") (lenderOf (lending (Some "two")))
                        let! old = git [ "ls-remote"; "https://github.com/octo/hello.git" ] "." (sandboxEnv gateway first)
                        Expect.isTrue (old.Status <> 0) "the old cap is dead"
                        let! current = git [ "ls-remote"; "https://github.com/octo/hello.git" ] "." (sandboxEnv gateway second)
                        Expect.equal current.Status 0 "the new one answers"
                        Expect.equal (List.ofSeq upstream.Authorizations) [ Some (basic "two") ] "with its own lender"
                    })
            do! upstream.Close ()
        }
    ]

// --- [Ports]: the push, end to end ---------------------------------------------------------
//
// `ls-remote` is one GET. A push is the advertisement, then a POST whose body git streams
// (chunked, gzipped for a fetch, behind an `Expect: 100-continue` past a size) and whose
// answer git streams back — every way a proxy that READS bodies rather than piping them
// breaks. So the upstream here is git's own smart-HTTP server over a real bare repository,
// and the assertion is that the commit landed.

/// github.com, played by `git http-backend`: CGI over a directory of bare repositories,
/// which is exactly what github.com's git endpoint is to a client. Pushes are enabled the
/// way a server enables them (`http.receivepack`), and the authorization header is recorded
/// so the case can see the credential arrived.
[<Emit("""(function (cp, http, root, seen) {
  const server = http.createServer((req, res) => {
    seen.push(req.headers['authorization'] || '')
    const u = new URL(req.url, 'http://x')
    const env = { ...process.env,
      GIT_PROJECT_ROOT: root, GIT_HTTP_EXPORT_ALL: '1', PATH_INFO: u.pathname, QUERY_STRING: u.search.slice(1),
      REQUEST_METHOD: req.method, CONTENT_TYPE: req.headers['content-type'] || '', REMOTE_USER: 'fixture', REMOTE_ADDR: '127.0.0.1',
      GIT_CONFIG_GLOBAL: '/dev/null', GIT_CONFIG_SYSTEM: '/dev/null',
      GIT_CONFIG_COUNT: '1', GIT_CONFIG_KEY_0: 'http.receivepack', GIT_CONFIG_VALUE_0: 'true' }
    if (req.headers['content-length']) env.CONTENT_LENGTH = req.headers['content-length']
    if (req.headers['content-encoding']) env.HTTP_CONTENT_ENCODING = req.headers['content-encoding']
    if (req.headers['git-protocol']) env.HTTP_GIT_PROTOCOL = req.headers['git-protocol']
    const child = cp.spawn('git', ['http-backend'], { env, stdio: ['pipe', 'pipe', 'inherit'] })
    req.pipe(child.stdin)
    let head = Buffer.alloc(0)
    let headed = false
    child.stdout.on('data', (chunk) => {
      if (headed) { res.write(chunk); return }
      head = Buffer.concat([head, chunk])
      const i = head.indexOf('\r\n\r\n')
      if (i < 0) return
      let status = 200
      const headers = {}
      for (const line of head.subarray(0, i).toString().split('\r\n')) {
        const j = line.indexOf(':')
        const k = line.slice(0, j).trim().toLowerCase()
        const v = line.slice(j + 1).trim()
        if (k === 'status') status = parseInt(v, 10)
        else headers[k] = v
      }
      res.writeHead(status, headers)
      headed = true
      const rest = head.subarray(i + 4)
      if (rest.length) res.write(rest)
    })
    child.stdout.on('end', () => { if (!headed) res.writeHead(500); res.end() })
  })
  return server
})($0, $1, $2, $3)""")>]
let private gitHttpBackend (cp: obj) (http: obj) (root: string) (seen: ResizeArray<string>) : HttpServer = jsNative

let private pushTests =
    testList "a push through the gateway" [

        testCaseAsync "lands in the repository on the other side, with the credential on every request" <| async {
            let root = mkdtemp nodeFs nodeOs
            try
                // github.com's side: `octo/hello.git`, bare, on `main`, empty.
                let served = sprintf "%s/served" root
                mkdir nodeFs (sprintf "%s/octo" served)
                let bare = sprintf "%s/octo/hello.git" served
                do! gitOk [ "init"; "--bare"; "-b"; "main"; bare ] root |> Async.Ignore
                let seen = ResizeArray<string> ()
                let upstream = gitHttpBackend childProcess nodeHttp served seen
                do! Async.FromContinuations (fun (cont, _, _) -> upstream.listen (0, "127.0.0.1", fun () -> cont ()) |> ignore)
                try
                    do!
                        withGateway (sprintf "http://127.0.0.1:%d" (serverPort upstream)) (fun gateway ->
                            async {
                                let cap = gateway.Grant (sandbox "octo/hello:dev") (lenderOf (lending (Some "ghu_lent")))
                                let env = sandboxEnv gateway cap
                                // The sandbox's side: a checkout with one commit, whose remote
                                // is written the way a person writes it.
                                let work = sprintf "%s/work" root
                                mkdir nodeFs work
                                do! gitOk [ "init"; "-b"; "main" ] work |> Async.Ignore
                                writeFile nodeFs (sprintf "%s/README.md" work) "pushed through the gateway\n"
                                do! gitOk [ "add"; "." ] work |> Async.Ignore
                                do! gitOk [ "commit"; "-m"; "seed" ] work |> Async.Ignore
                                do! gitOk [ "remote"; "add"; "origin"; "https://github.com/octo/hello.git" ] work |> Async.Ignore
                                let! pushed = git [ "push"; "origin"; "main" ] work env
                                Expect.equal pushed.Status 0 (sprintf "the push succeeded: %s" pushed.Stderr)
                                let! expected = gitOk [ "rev-parse"; "main" ] work
                                let expected = expected.Trim ()
                                let! landed = gitOk [ "rev-parse"; "main" ] bare
                                let landed = landed.Trim ()
                                Expect.equal landed expected "the commit is on the other side"
                                Expect.isTrue (seen.Count >= 2) "an advertisement and a receive-pack, at least"
                                Expect.isTrue (seen |> Seq.forall ((=) (basic "ghu_lent"))) "every request carried the lent credential"
                                // And the way back: a fetch through the same route.
                                let! fetched = git [ "ls-remote"; "origin" ] work env
                                Expect.equal fetched.Status 0 (sprintf "ls-remote succeeded: %s" fetched.Stderr)
                                Expect.isTrue (fetched.Stdout.Contains expected) "reads what was pushed"
                            })
                finally
                    upstream.close ignore
            finally
                rmrf nodeFs root
        }
    ]

// --- [Srt]: from inside a confined sandbox -------------------------------------------------
//
// An srt sandbox's only way out is srt's filtering proxy, and the proxy's `NO_PROXY` covers
// loopback and every private range — so `127.0.0.1` and `host.docker.internal` are names
// its git would try to dial DIRECTLY, into a namespace with no route (Linux) or a seatbelt
// deny (macOS). The name that works is this box's own hostname: not in `NO_PROXY`, carried
// by the proxy when the sandbox's egress allows it, and resolved on the PARENT side to an
// address the every-interface listener answers on.

let private srtTools () =
    match Sandboxes.SrtSandbox.toolsFrom (Sandboxes.ambientEnv ()) with
    | Ok tools -> tools
    | Error reason -> failwithf "srt tools: %s" reason

let private srtTests =
    testList "from an srt sandbox" [

        testCaseAsync "a confined git reaches the gateway by this box's name, through srt's proxy" <| async {
            let! upstream = startUpstream ()
            do!
                withGateway upstream.Origin (fun gateway ->
                    async {
                        let host =
                            match Sandboxes.hostAddressHere (Interop.hostname ()) SrtBackend with
                            | Some host -> host
                            | None -> failwith "srt is a backend with a route to the host"
                        let cap = gateway.Grant (sandbox "dev") (lenderOf (lending (Some "ghu_lent")))
                        // Canonical, because seatbelt matches the path as written and
                        // `/tmp` is a symlink here (the note in GitIntegration.fs).
                        let workspace =
                            match Fs.canonical (mkdtemp nodeFs nodeOs) with
                            | Some path -> path
                            | None -> failwith "the workspace does not resolve"
                        let policy : SandboxPolicy =
                            { ReadPaths = [ workspace ]
                              WritePaths = [ workspace ]
                              AllowedDomains = Some [ host ]
                              Sockets = []
                              Binds = []
                              Volumes = []
                              Realisation = []
                              // A home of its own, as a session gives every sandbox: git
                              // reads `$HOME`'s config, and the operator's is denied.
                              Env =
                                Sandboxes.hostBaseline (Sandboxes.ambientEnv ())
                                |> Map.add "HOME" workspace
                                |> Sandboxes.withGitConfig (GitGateway.gitConfig host gateway.Port cap)
                              WorkingDirectory = Some workspace
                              Filesystem = Confined }
                        match! Sandboxes.SrtSandbox.create (srtTools ()) policy with
                        | Error reason -> failwithf "srt sandbox failed: %s" reason
                        | Ok confined ->
                            // Verbose on purpose: this case can only fail by a wire it does
                            // not control, and curl's trace is the one account of which hop
                            // ("Empty reply from server" on the Linux runner said nothing).
                            let! run, out, err =
                                runInSandbox
                                    confined
                                    "git"
                                    [ "ls-remote"; "https://github.com/octo/hello.git" ]
                                    (Map.ofList [ "GIT_TERMINAL_PROMPT", "0"; "GIT_CURL_VERBOSE", "1" ])
                                    None
                            let! _, proxyEnv, _ = runInSandbox confined "sh" [ "-c"; "env | grep -i proxy" ] Map.empty None
                            Expect.equal run (SandboxExited 0) (sprintf "ls-remote succeeded from inside (proxy env: %s): %s" proxyEnv err)
                            Expect.isTrue (out.Contains "refs/heads/main") "and read the upstream's refs"
                            Expect.equal (List.ofSeq upstream.Authorizations) [ Some (basic "ghu_lent") ] "github.com saw the lent credential"
                            do! confined.Dispose ()
                        rmrf nodeFs workspace
                    })
            do! upstream.Close ()
        }
    ]

let tests =
    testList "The git gateway" [
        routeTests
        Tag.needs "The git gateway, driven by git" [ Tag.Ports ] (fun () -> testList "with a real git" [ portsTests; pushTests ])
        Tag.needs "The git gateway, from srt" [ Tag.Srt ] (fun () -> srtTests)
    ]
