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
open Fable.NodeExtras
open Fable.Pyxpecto
open Node.Api
open Node.Buffer
open Yession.Domain
open Yession.Domain.Sandboxes
open Yession.Host
open Yession.Host.Interop
open Yession.Tests.Support

let private sandbox (raw: string) = SandboxRef.parse raw |> expect
let private ada = Principal.User (UserId.create "ada" |> expect)

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

// --- cheap: what the gateway carries, each way ----------------------------------------------
//
// The proxy itself needs a socket at both ends, but the three decisions it makes do not: which
// headers go up, which come back down, and which statuses are relayed as they stand. Those are
// the parts a wire test can only observe indirectly, so they are pinned here, where a red says
// which one moved.

let private headers (pairs: (string * string) list) : (string * obj)[] =
    pairs |> List.map (fun (name, value) -> name, box value) |> List.toArray

let private names (pairs: (string * obj)[]) = pairs |> Array.map fst |> List.ofArray

let private valueOf (name: string) (pairs: (string * obj)[]) =
    pairs |> Array.tryPick (fun (key, value) -> if key = name then Some (unbox<string> value) else None)

let private carryTests =
    testList "what the gateway carries" [

        // Hop-by-hop headers describe a connection this process is not forwarding, and `host`
        // and `expect` describe the request as it arrived HERE — Node's server has already
        // answered the `100 Continue`, so repeating the ask upstream would wait for a second.
        testCase "nothing that described the hop the request arrived on goes up" <| fun () ->
            let sent =
                headers
                    [ "host", "127.0.0.1:4321"
                      "user-agent", "git/2.45.0"
                      "content-type", "application/x-git-upload-pack-request"
                      "content-encoding", "gzip"
                      "expect", "100-continue"
                      "connection", "keep-alive"
                      "keep-alive", "timeout=5"
                      "proxy-authorization", "Basic bm90aGluZw=="
                      "proxy-connection", "keep-alive"
                      "te", "trailers"
                      "trailer", "x-checksum"
                      "transfer-encoding", "chunked"
                      "upgrade", "h2c" ]
            Expect.equal
                (names (GitGateway.upstreamHeaders sent "Basic lent"))
                [ "user-agent"; "content-type"; "content-encoding"; "authorization" ]
                "what git said about its BODY goes up; what it said about its connection does not"

        // The sandbox holds no credential, so anything it managed to put in `authorization`
        // is its own invention — and would be what github.com judged if it were carried.
        testCase "the credential that goes up is the lender's, whatever arrived" <| fun () ->
            let carried = GitGateway.upstreamHeaders (headers [ "authorization", "Basic c2FuZGJveA==" ]) "Basic lent"
            Expect.equal (names carried) [ "authorization" ] "one authorization, not two"
            Expect.equal (valueOf "authorization" carried) (Some "Basic lent") "and it is the lender's"

        // Only the three that describe the upstream connection this process terminated. The
        // rest is github.com talking to git, including a `www-authenticate` that rides the
        // 403 it refuses a push with.
        testCase "the answer keeps every header but the three that described the upstream hop" <| fun () ->
            let received =
                headers
                    [ "content-type", "application/x-git-upload-pack-advertisement"
                      "cache-control", "no-cache, max-age=0, must-revalidate"
                      "www-authenticate", "Basic realm=\"GitHub\""
                      "upgrade", "h2"
                      "connection", "keep-alive"
                      "keep-alive", "timeout=5"
                      "transfer-encoding", "chunked" ]
            Expect.equal
                (names (GitGateway.downstreamHeaders received))
                [ "content-type"; "cache-control"; "www-authenticate"; "upgrade" ]
                "the connection's three are dropped and nothing else is"

        // Relaying it would make the sandbox's git prompt for a password nobody can type,
        // which under `GIT_TERMINAL_PROMPT=0` is "could not read Username" — the sentence the
        // whole `ERR` channel exists to replace.
        testCase "a 401 is not relayed" <| fun () ->
            Expect.isFalse (GitGateway.relayed 401) "it comes back unanswered, to be said in words"

        // Git already knows how to read these, and a gateway with an opinion about them would
        // be a gateway that has to be taught each new one.
        testCase "every other status is relayed as it stands" <| fun () ->
            for status in [ 200; 206; 304; 403; 404; 410; 500; 503 ] do
                Expect.isTrue (GitGateway.relayed status) (sprintf "a %d is git's to read" status)
    ]

// --- [Ports]: a real git, at a real gateway ------------------------------------------------

let private nodeFs : obj = importAll "node:fs"
let private nodeOs : obj = importAll "node:os"

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

/// What `execFile` reports of a child that did not exit 0. `code` is the exit STATUS where
/// the child ran and chose it, a string (`ENOENT`) where it could never be started, and
/// neither where a signal ended it — which is how a run killed at its deadline arrives.
type [<AllowNullLiteral>] private ExecFileError =
    abstract code : obj

[<Import("execFile", "node:child_process")>]
let private execFile
    (file: string)
    (arguments: string array)
    (options: obj)
    (completed: Action<ExecFileError, string, string>)
    : obj =
    jsNative

/// The status a run ended on: the child's own, or `-1` for one that never got to choose.
let private statusOf (error: ExecFileError) : int =
    if isNull error then 0
    elif jsTypeof error.code = "number" then unbox<int> error.code
    else -1

/// What keeps this box out of a fixture git: no configuration of the operator's, no identity
/// of theirs, and no prompt for a credential nobody is there to type. Data rather than an
/// object literal, so what the gateway told a sandbox goes on top of it by the same rule that
/// put it there.
let private fixtureGitEnv =
    [ "GIT_CONFIG_GLOBAL", "/dev/null"
      "GIT_CONFIG_SYSTEM", "/dev/null"
      "GIT_TERMINAL_PROMPT", "0"
      "GIT_AUTHOR_NAME", "fixture"
      "GIT_AUTHOR_EMAIL", "f@x"
      "GIT_COMMITTER_NAME", "fixture"
      "GIT_COMMITTER_EMAIL", "f@x" ]

let private gitEnvironment (told: Map<string, string>) : Map<string, string> =
    let added env pairs = (env, pairs) ||> List.fold (fun env (name, value) -> Map.add name value env)
    added (added (Sandboxes.ambientEnv ()) fixtureGitEnv) (Map.toList told)

/// Run git as a sandbox would: no config of this box's, no prompt, a fixed identity, and
/// whatever the gateway told it on top. Asynchronous of necessity: the gateway git is
/// talking to runs on THIS event loop, and a synchronous spawn would hold it while git
/// waited for an answer that could then never come.
///
/// And BOUNDED, because that is the shape every fault in a gateway takes: a gateway that
/// stops answering does not fail, it says nothing, and git waits on it for as long as it is
/// allowed to. Unbounded, the first such regression kills the whole run on its budget and
/// names no case; bounded, git is killed and the case that was waiting fails as itself, on
/// the assertion it was actually making.
let private git (args: string list) (cwd: string) (told: Map<string, string>) : Async<GitRun> =
    let options : obj =
        !!{| cwd = cwd
             env = gitEnvironment told |> Map.toList |> List.map (fun (name, value) -> name ==> value) |> createObj
             // Text back rather than buffers, which is what `GitRun` says it holds.
             encoding = "utf8"
             // Far past `execFile`'s 1MB default: what a fetch answers with is a packfile,
             // and a run that outgrew the default would be killed and read as a gateway that
             // stopped answering.
             maxBuffer = 64 * 1024 * 1024
             timeout = 20000
             // The deadline has to END the run rather than ask it to stop: what is bounded
             // here is a git waiting on an answer, and one that took the signal as a chance
             // to tidy up would spend the budget anyway.
             killSignal = "SIGKILL" |}

    Async.FromContinuations (fun (cont, _, _) ->
        execFile
            "git"
            (List.toArray args)
            options
            (Action<ExecFileError, string, string> (fun error out err ->
                cont
                    { Status = statusOf error
                      // Strings under an `encoding`, and nothing at all where the child never
                      // ran — which a case reads as a sentence git printed.
                      Stdout = if isNull out then "" else out
                      Stderr = if isNull err then "" else err }))
        |> ignore)

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
    { Owner = CredentialFor.Person ada
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

        // The same channel, for the other thing git cannot be shown: a github.com that did not
        // answer at all. Nothing has been written to git yet when the upstream fails, so the
        // reason is still sayable — and it has to BE the reason, not the word `undefined`,
        // which is what reading a message off something that is not an `Error` produces.
        testCaseAsync "an upstream this session cannot reach is said in words git prints" <| async {
            // A port that WAS listening and is not any more: the connection is refused at
            // once, rather than hanging against an address nothing ever answers on.
            let! upstream = startUpstream ()
            do! upstream.Close ()
            do!
                withGateway upstream.Origin (fun gateway ->
                    async {
                        let cap = gateway.Grant (sandbox "octo/hello:dev") (lenderOf (lending (Some "ghu_lent")))
                        let! run = git [ "ls-remote"; "https://github.com/octo/hello.git" ] "." (sandboxEnv gateway cap)
                        Expect.isTrue (run.Status <> 0) "it fails"
                        Expect.isTrue (run.Stderr.Contains "remote error:") (sprintf "on the remote-error channel: %s" run.Stderr)
                        Expect.isTrue (run.Stderr.Contains "could not be reached") "saying github.com was not reached"
                        Expect.isFalse (run.Stderr.Contains "undefined") "with the reason, not the word undefined"
                        Expect.isFalse (run.Stderr.Contains "Username") "never a credential prompt"
                    })
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

// --- cheap: reading what a CGI backend wrote -------------------------------------------------
//
// Beside the fixture that reads it, because it is the half of that fixture a wire cannot ask
// about: git only ever says whether the answer was good.

[<Literal>]
let private cgiHeadEnd = "\r\n\r\n"

/// `parseInt`'s reading of a CGI `Status:` line, which carries a code and then a reason
/// (`403 Forbidden`): the digits it starts with, and nothing when it starts with none.
let private statusFrom (value: string) : int option =
    let digits = value |> Seq.takeWhile Char.IsDigit |> Seq.map string |> String.concat ""
    if digits = "" then None else Some (int digits)

/// Split what a CGI program wrote: a header block, `\r\n\r\n`, then the answer itself.
/// `None` until the terminator is in hand, because the block arrives in whatever pieces the
/// pipe hands over and a header can be cut in half by a chunk boundary.
///
/// Three things a reader of this has to get right, and every one of them is here rather than
/// inside a stream callback no test could reach:
///
///   * `Status:` is not a header. It is the status LINE, and relayed as a header it sends git
///     a `Status: 403` on a 200. Absent, CGI's own default is 200.
///   * A value may itself contain a colon (`WWW-Authenticate: Basic realm="x:y"`), so a line
///     splits at its FIRST one — and a line carrying none is not a header at all.
///   * What follows the terminator is already the body, and has to come back as the bytes it
///     is.
///
/// The text is latin1, where one byte is one character and decoding is neither lossy nor
/// stateful: `rest` re-encodes to exactly the bytes that followed, and two chunks decoded
/// apart concatenate to what the two of them said together.
let private splitCgiHead (written: string) : (int * (string * string) list * string) option =
    match written.IndexOf cgiHeadEnd with
    | -1 -> None
    | terminator ->
        let fields =
            written.Substring(0, terminator).Split ([| "\r\n" |], StringSplitOptions.None)
            |> Array.toList
            |> List.choose (fun line ->
                match line.IndexOf ':' with
                | -1 -> None
                | colon ->
                    Some (line.Substring(0, colon).Trim().ToLowerInvariant (), line.Substring(colon + 1).Trim ()))

        Some (
            fields
            |> List.tryPick (fun (name, value) -> if name = "status" then statusFrom value else None)
            |> Option.defaultValue 200,
            fields |> List.filter (fun (name, _) -> name <> "status"),
            written.Substring (terminator + cgiHeadEnd.Length))

let private cgiTests =
    testList "the answer a CGI backend writes" [

        testCase "nothing is read until the terminator has arrived" <| fun () ->
            Expect.isNone
                (splitCgiHead "Content-Type: application/x-git-upload-pack-advertisement\r\nExpires: Fri")
                "a block the pipe has only half handed over"

        testCase "a header cut in half by a chunk boundary is one header once both halves are in" <| fun () ->
            Expect.equal
                (splitCgiHead ("Content-Type: text/plain\r\nExpi" + "res: Fri\r\n\r\nbody"))
                (Some (200, [ "content-type", "text/plain"; "expires", "Fri" ], "body"))
                "what the two chunks said together"

        testCase "a Status line is the status, and is not relayed as a header" <| fun () ->
            Expect.equal
                (splitCgiHead "Status: 403 Forbidden\r\nContent-Type: text/plain\r\n\r\n")
                (Some (403, [ "content-type", "text/plain" ], ""))
                "the code off the line, and nothing named status left behind"

        testCase "a backend that named no status wrote a 200" <| fun () ->
            Expect.equal
                (splitCgiHead "Content-Type: text/plain\r\n\r\n" |> Option.map (fun (status, _, _) -> status))
                (Some 200)
                "CGI's own default"

        testCase "a header's value may contain a colon" <| fun () ->
            Expect.equal
                (splitCgiHead "WWW-Authenticate: Basic realm=\"GitHub:git\"\r\n\r\n"
                 |> Option.map (fun (_, headers, _) -> headers))
                (Some [ "www-authenticate", "Basic realm=\"GitHub:git\"" ])
                "split at the first colon, not at every one"

        testCase "what followed the terminator comes back as the bytes it is" <| fun () ->
            let body = "\u0000\u00ff\u0080PACK"
            Expect.equal
                (splitCgiHead ("Content-Type: application/x-git-receive-pack-result" + cgiHeadEnd + body)
                 |> Option.map (fun (_, _, rest) -> rest))
                (Some body)
                "including the ones no text encoding would survive"
    ]

// --- [Ports]: github.com, played by `git http-backend` ---------------------------------------

/// One byte, one character, both ways: how the header block above is read out of the bytes a
/// backend wrote and how what followed it is put back, without any encoding getting an
/// opinion about a packfile.
let private latin1 = BufferEncoding.Latin1

/// And utf8 for the one thing on these streams that is genuinely text.
let private utf8 = BufferEncoding.Utf8

/// One optional CGI variable: passed on where the request carried the header, and absent
/// where it did not — which is not the same as empty, since `CONTENT_LENGTH=` is a length.
let private carrying (name: string) (value: string option) (env: Map<string, string>) =
    match value with
    | Some value -> Map.add name value env
    | None -> env

/// What CGI calls `PATH_INFO` and `QUERY_STRING`: a request target split at its first `?`.
/// Git writes neither a fragment nor a relative segment into one, and the gateway in front of
/// this refuses a `..` before it could ever arrive here.
let private pathAndQuery (target: string) : string * string =
    match target.IndexOf '?' with
    | -1 -> target, ""
    | mark -> target.Substring (0, mark), target.Substring (mark + 1)

/// github.com, played by `git http-backend`: CGI over a directory of bare repositories,
/// which is exactly what github.com's git endpoint is to a client. Pushes are enabled the
/// way a server enables them (`http.receivepack`), and the authorization header is recorded
/// so the case can see the credential arrived.
///
/// The answer is relayed AS IT ARRIVES rather than collected: what git reads back from a push
/// or a fetch is a stream, and a stand-in that held the whole of one would be standing in for
/// a github.com nobody talks to.
let private gitHttpBackend (root: string) (seen: ResizeArray<string option>) : HttpServer =
    createServer (fun req res ->
        // The option is kept rather than flattened to "": a request that carried NO credential
        // and one that carried the wrong one are different failures, and a case that cannot
        // tell them apart reports the second when it means the first.
        seen.Add (headerOf req "authorization")
        let path, query = pathAndQuery req.url

        let env =
            Sandboxes.ambientEnv ()
            |> Map.add "GIT_PROJECT_ROOT" root
            |> Map.add "GIT_HTTP_EXPORT_ALL" "1"
            |> Map.add "PATH_INFO" path
            |> Map.add "QUERY_STRING" query
            |> Map.add "REQUEST_METHOD" req.``method``
            |> Map.add "REMOTE_USER" "fixture"
            |> Map.add "REMOTE_ADDR" "127.0.0.1"
            |> Map.add "GIT_CONFIG_GLOBAL" "/dev/null"
            |> Map.add "GIT_CONFIG_SYSTEM" "/dev/null"
            |> carrying "CONTENT_TYPE" (headerOf req "content-type")
            |> carrying "CONTENT_LENGTH" (headerOf req "content-length")
            |> carrying "HTTP_CONTENT_ENCODING" (headerOf req "content-encoding")
            |> carrying "HTTP_GIT_PROTOCOL" (headerOf req "git-protocol")
            |> Sandboxes.withGitConfig [ "http.receivepack", "true" ]

        let backend =
            spawn
                "git"
                [ "http-backend" ]
                { Cwd = None
                  Env = env
                  Stdio = Pipe
                  Detached = false }

        let answer : Readable = !!backend.stdout
        req.pipe (!!backend.stdin)

        // `stdio` is one setting for all three streams, so the backend's own account of
        // itself arrives on a pipe rather than this process's stderr — and an unread pipe is
        // one a child eventually blocks on. Said out loud instead, because what
        // `http-backend` complains about is the only account a refused request ever gives.
        (!!backend.stderr : Readable)
            .onData (fun chunk -> eprintfn "git http-backend: %s" ((chunk.toString utf8).TrimEnd ()))

        let mutable written = ""
        let mutable headed = false

        answer.onData (fun chunk ->
            if headed then
                res.writeBytes chunk |> ignore
            else
                written <- written + chunk.toString latin1

                match splitCgiHead written with
                | None -> ()
                | Some (status, headers, rest) ->
                    res.writeHead (status, createObj (headers |> List.map (fun (name, value) -> name ==> value)))
                    |> ignore

                    headed <- true
                    written <- ""
                    if rest <> "" then res.writeBytes (buffer.Buffer.from (rest, latin1)) |> ignore)

        answer.onEnd (fun () ->
            // A backend that said nothing at all is this fixture failing, not an answer git
            // should be asked to read.
            if not headed then res.writeHead (500, createObj []) |> ignore
            res.``end`` ""))

// --- [Ports]: the push, end to end ---------------------------------------------------------
//
// `ls-remote` is one GET. A push is the advertisement, then a POST whose body git streams
// (chunked, gzipped for a fetch, behind an `Expect: 100-continue` past a size) and whose
// answer git streams back — every way a proxy that READS bodies rather than piping them
// breaks. So the upstream here is git's own smart-HTTP server over a real bare repository,
// and the assertion is that the commit landed.

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
                let seen = ResizeArray<string option> ()
                let upstream = gitHttpBackend served seen
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
                                Expect.isTrue (seen |> Seq.forall ((=) (Some (basic "ghu_lent")))) "every request carried the lent credential"
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
// loopback and every private range — so the host address the backend hands out
// (`hostAddressFrom`: `127.0.0.2` on Linux, the box's name on macOS) is the one that goes
// THROUGH the proxy rather than being dialled directly into a namespace with no route. The
// gateway's every-interface listener answers it on the parent side. This is the seam the
// Ports suite cannot reach: it drives git directly; only a real confined sandbox proves the
// route survives srt's egress.

let private srtTools () =
    match Sandboxes.SrtSandbox.toolsFrom (Sandboxes.ambientEnv ()) with
    | Ok tools -> tools
    | Error reason -> failwithf "srt tools: %s" reason

let private srtTests =
    testList "from an srt sandbox" [

        testCaseAsync "a confined git reaches the gateway, through srt's proxy, by the backend's host" <| async {
            let! upstream = startUpstream ()
            do!
                withGateway upstream.Origin (fun gateway ->
                    async {
                        let host =
                            match Sandboxes.hostAddressHere (Interop.hostname ()) SrtBackend with
                            | Some host -> host
                            | None -> failwith "srt is a backend with a route to the host"
                        let cap = gateway.Grant (sandbox "dev") (lenderOf (lending (Some "ghu_lent")))
                        // Canonical, because seatbelt matches the path as written and `/tmp`
                        // is a symlink here (the note in GitIntegration.fs).
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
                            let! run, out, err =
                                runInSandbox confined "git" [ "ls-remote"; "https://github.com/octo/hello.git" ] (Map.ofList [ "GIT_TERMINAL_PROMPT", "0" ]) None
                            Expect.equal run (SandboxExited 0) (sprintf "ls-remote succeeded from inside: %s" err)
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
        carryTests
        cgiTests
        Tag.needs "The git gateway, driven by git" [ Tag.Ports ] (fun () -> testList "with a real git" [ portsTests; pushTests ])
        Tag.needs "The git gateway, from srt" [ Tag.Srt ] (fun () -> srtTests)
    ]
