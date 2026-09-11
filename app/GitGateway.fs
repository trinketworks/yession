module Yession.Host.GitGateway

// The git gateway: how a work sandbox reaches github.com WITHOUT holding a credential.
//
// A forwarded `github` credential used to be the token itself, put in the sandbox's env as
// `GITHUB_TOKEN`. Two things were wrong with that, and the first hid the second. Nothing in a
// sandbox reads `GITHUB_TOKEN` — git does not, and `gh` is not there — so every agent that
// wanted to push hand-rolled auth from the variable: a token pasted into a remote URL, an
// `http.extraheader`, and once an `insteadOf` written into `.git/config`, where it outlived
// the sandbox. Meanwhile the value sat in the env of every process in the sandbox for its
// whole life, printable by `env`, and frozen at the moment of the start — a refreshed token
// never reached a sandbox already running (docs/GAPS.md).
//
// So the credential stays here, in the Session Process, and what the sandbox gets instead is
// a ROUTE: this process listens for git's smart-HTTP requests, adds the credential on the
// way to github.com, and streams the answer back. The sandbox's git is told, through one
// `url.<here>.insteadOf` config, that `https://github.com/` is spelled `http://<this
// process>/git/<cap>/github.com/` — so `push`, `fetch`, `ls-remote`, submodules and anything
// else git does over HTTP work unchanged, `remote -v` still shows the real address, and the
// only thing in the sandbox is a capability that names this session's gateway and dies with
// the sandbox. Full git, no token.
//
// What that buys beyond hiding a value: the credential is resolved on EVERY request, so a
// refresh reaches a running sandbox; and a refusal is written in words. Git cannot show a
// person an HTTP error body — a 401 from github.com becomes a credential prompt, and under
// `GIT_TERMINAL_PROMPT=0` that is "could not read Username", which is the sentence that cost
// the session that motivated this three rounds of guessing. The wire has a better channel:
// an `ERR` packet in the advertisement, which git prints as `remote error: <message>`. So a
// missing or rejected credential is answered on THAT channel, and the person reads what
// happened and where to fix it.
//
// The listener binds every interface, and that is not carelessness: it is how a container
// reaches it. Docker's `host-gateway` alias is the host's loopback under Colima and Docker
// Desktop but the bridge address under a native Linux daemon, and a loopback-only listener
// would answer on one and not the other. The route is a 128-bit capability minted per
// sandbox, so what is exposed is "this session's gateway, to whoever holds a cap for it" —
// the same trust a sandbox already has.

open System
open Fable.Core
open Fable.Core.JsInterop
open Yession.Domain
open Yession.Host.Interop

/// The one host this gateway speaks for. A sandbox's git names it in the rewritten URL, and
/// nothing else is admitted — the gateway is a route to github.com, not a proxy.
let remoteHost = "github.com"

/// The path prefix a sandbox's git rewrites `https://github.com/` to, under this gateway.
let private prefix = "git"

/// Whose credential answers a route, resolved PER REQUEST — never captured.
type Lender =
    { /// Whose it is, for the sentences git prints.
      Owner : ActorRef
      /// The credential now. `None` = the owner has none (any more).
      Resolve : unit -> Async<string option>
      /// github.com refused it: tell whoever tracks the credential's health.
      Refused : unit -> Async<unit> }

/// One request the gateway will carry, parsed off a path. Only the three requests git's
/// smart HTTP transport makes are requests here; everything else 404s, so a cap admits git to
/// a repository and admits nothing to the rest of github.com.
[<RequireQualifiedAccess>]
type GitRequest =
    { Cap : string
      /// `owner/repo.git/info/refs` and the like — the path under the upstream origin.
      Path : string
      /// `git-upload-pack` (fetch) or `git-receive-pack` (push).
      Service : string }

let private segmentOk (segment: string) =
    let ok (c: char) =
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
        || c = '.' || c = '-' || c = '_'
    segment <> "" && segment <> "." && segment <> ".." && Seq.forall ok segment

/// The git request a method, path and `?service=` name, or none. The path is
/// `/git/<cap>/github.com/<owner>/<repo>/…`, where `…` is what git's transport asks for:
/// the advertisement (`GET info/refs?service=…`) and the two service posts.
let route (method: string) (path: string) (service: string option) : GitRequest option =
    let service' (name: string) =
        if name = "git-upload-pack" || name = "git-receive-pack" then Some name else None
    match path.Trim('/').Split '/' |> Array.toList with
    | [ p; cap; host; owner; repo; "info"; "refs" ] when
        p = prefix && method = "GET" && host = remoteHost && segmentOk cap && segmentOk owner && segmentOk repo
        ->
        service
        |> Option.bind service'
        |> Option.map (fun s -> { GitRequest.Cap = cap; Path = sprintf "%s/%s/info/refs" owner repo; Service = s })
    | [ p; cap; host; owner; repo; posted ] when
        p = prefix && method = "POST" && host = remoteHost && segmentOk cap && segmentOk owner && segmentOk repo
        ->
        service' posted
        |> Option.map (fun s -> { GitRequest.Cap = cap; Path = sprintf "%s/%s/%s" owner repo s; Service = s })
    | _ -> None

/// The git config a sandbox is given so that its git reaches github.com through the gateway
/// at `host:port` under `cap`. One `insteadOf`, which is what makes it full git: every
/// transport operation rewrites its URL through this, and nothing else about git changes.
let gitConfig (host: string) (port: int) (cap: string) : (string * string) list =
    [ sprintf "url.http://%s:%d/%s/%s/%s/.insteadOf" host port prefix cap remoteHost, sprintf "https://%s/" remoteHost ]

// --- the wire ------------------------------------------------------------------------------

/// The UTF-8 byte length, which a pkt-line's header counts — not the string length, which
/// a message with an em dash in it taught the difference of.
[<Emit("Buffer.byteLength($0, 'utf8')")>]
let private byteLength (s: string) : int = jsNative

/// One pkt-line: four hex digits of total length, then the payload.
let pktLine (payload: string) : string =
    sprintf "%04x%s" (byteLength payload + 4) payload

let private flush = "0000"

/// What git is told when this gateway will not carry the request, on the channel git can
/// show a person: an `ERR` packet, which it prints as `remote error: <message>` and stops.
/// The advertisement's service header comes first so git accepts the response as a smart
/// one, and the same body answers a service post — git reads the first packet either way.
let errorBody (request: GitRequest) (isAdvertisement: bool) (message: string) : string =
    (if isAdvertisement then pktLine (sprintf "# service=%s\n" request.Service) + flush else "")
    + pktLine (sprintf "ERR %s\n" message)
    + flush

[<Emit("new URL($0, 'http://local').pathname")>]
let private pathnameOf (url: string) : string = jsNative

[<Emit("new URL($0, 'http://local').search")>]
let private searchOf (url: string) : string = jsNative

[<Emit("new URL($0, 'http://local').searchParams.get($1) ?? null")>]
let private queryOf (url: string) (name: string) : string option = jsNative

let private nodeHttp : obj = importAll "node:http"
let private nodeHttps : obj = importAll "node:https"

/// Carry one request to the upstream and its answer back, adding the credential on the way.
///
/// The bytes are piped, never read: git gzips its posts and expects its responses untouched,
/// so a `fetch` — which decodes bodies for you — is exactly the wrong tool. The request's
/// `expect` is dropped because Node's server has already answered the `100 Continue` it
/// asked for, and the hop-by-hop headers are dropped because they describe a connection this
/// process is not forwarding. A `401` is NOT relayed: relaying it would make the sandbox's
/// git prompt for a password nobody can type, so it comes back to the caller unanswered, to
/// be said in words (`errorBody`). Everything else — 200s, the 403 github.com uses to refuse
/// a push, a 404 — passes through as it is, because git already knows how to read those.
[<Emit("""(function (http, https, req, res, url, authorization) {
  return new Promise((resolve) => {
    const dropUp = new Set(['host', 'authorization', 'connection', 'expect', 'keep-alive', 'proxy-authorization', 'proxy-connection', 'te', 'trailer', 'transfer-encoding', 'upgrade'])
    const dropDown = new Set(['connection', 'keep-alive', 'transfer-encoding'])
    const headers = {}
    for (const [k, v] of Object.entries(req.headers)) if (!dropUp.has(k)) headers[k] = v
    headers['authorization'] = authorization
    const mod = url.startsWith('https:') ? https : http
    let settled = false
    const done = (outcome) => { if (!settled) { settled = true; resolve(outcome) } }
    const up = mod.request(url, { method: req.method, headers }, (r) => {
      if (r.statusCode === 401) { r.resume(); done({ status: 401, error: '', answered: false }); return }
      const out = {}
      for (const [k, v] of Object.entries(r.headers)) if (!dropDown.has(k)) out[k] = v
      res.writeHead(r.statusCode, out)
      r.pipe(res)
      r.on('end', () => done({ status: r.statusCode, error: '', answered: true }))
      r.on('error', (e) => { res.destroy(); done({ status: 0, error: String(e && e.message || e), answered: true }) })
    })
    up.on('error', (e) => {
      if (res.headersSent) res.destroy()
      done({ status: 0, error: String(e && e.message || e), answered: res.headersSent })
    })
    req.on('error', () => up.destroy())
    req.pipe(up)
  })
})($0, $1, $2, $3, $4, $5)""")>]
let private forwardWith
    (http: obj)
    (https: obj)
    (req: IncomingMessage)
    (res: ServerResponse)
    (url: string)
    (authorization: string)
    : JS.Promise<{| status: int; error: string; answered: bool |}> =
    jsNative

/// The credential as github.com's git endpoint takes it: HTTP basic, `x-access-token` as
/// the user. (A bearer header is what the API takes and what the git endpoint answers 401
/// to — another thing the motivating session learnt by trying.)
let private basicAuthorization (token: string) : string =
    "Basic " + Convert.ToBase64String (Text.Encoding.UTF8.GetBytes ("x-access-token:" + token))

// --- the gateway ---------------------------------------------------------------------------

type Gateway =
    { /// The bound port, on every interface.
      Port : int
      /// Mint the route for one sandbox, replacing any it had: the cap its git will name.
      Grant : SandboxRef -> Lender -> string
      /// Take a sandbox's route away — what makes the cap die with the sandbox.
      Revoke : SandboxRef -> unit
      Close : unit -> Async<unit> }

/// Start the gateway. `upstream` is the origin github.com's git endpoints live under — a
/// parameter for the reason every provider endpoint here is: a suite needs somewhere to
/// point it that is not the live provider.
let start (upstream: string) : Async<Gateway> =
    let mutable grants : Map<string, SandboxRef * Lender> = Map.empty

    let answer (res: ServerResponse) (status: int) (contentType: string) (body: string) =
        res.writeHead (status, createObj [ "content-type", box contentType; "cache-control", box "no-store" ]) |> ignore
        res.``end`` body

    let notFound (res: ServerResponse) = answer res 404 "text/plain" "not found"

    let handler (req: IncomingMessage) (res: ServerResponse) =
        let url = req.url
        match route req.``method`` (pathnameOf url) (queryOf url "service") with
        | None -> notFound res
        | Some request ->
            match Map.tryFind request.Cap grants with
            | None -> notFound res
            | Some (_, lender) ->
                let isAdvertisement = req.``method`` = "GET"
                let contentType =
                    sprintf "application/x-%s-%s" request.Service (if isAdvertisement then "advertisement" else "result")
                let refuse (message: string) =
                    answer res 200 contentType (errorBody request isAdvertisement message)
                Async.StartImmediate (
                    async {
                        try
                            match! lender.Resolve () with
                            | None ->
                                refuse (
                                    sprintf
                                        "the github credential this sandbox was lent (%s's) is gone — connect one on the settings panel and start the sandbox again"
                                        (ActorRef.token lender.Owner))
                            | Some token ->
                                let target = upstream.TrimEnd '/' + "/" + request.Path + searchOf url
                                let! outcome =
                                    forwardWith nodeHttp nodeHttps req res target (basicAuthorization token)
                                    |> awaitPromise
                                match outcome.status with
                                | 401 ->
                                    do! lender.Refused ()
                                    refuse (
                                        sprintf
                                            "github rejected %s's credential — sign in again on the settings panel"
                                            (ActorRef.token lender.Owner))
                                | 0 when not outcome.answered ->
                                    refuse (sprintf "%s could not be reached from this session: %s" remoteHost outcome.error)
                                | _ -> ()
                        with e ->
                            if not (res.headersSent) then refuse (sprintf "the git gateway failed: %s" e.Message)
                    })

    let server = createServer handler
    async {
        do!
            Async.FromContinuations (fun (cont, _, _) ->
                server.listen (0, "0.0.0.0", fun () -> cont ()) |> ignore)
        return
            { Port = serverPort server
              Grant =
                fun sandbox lender ->
                    let cap = randomSecret ()
                    grants <-
                        grants
                        |> Map.filter (fun _ (held, _) -> held <> sandbox)
                        |> Map.add cap (sandbox, lender)
                    cap
              Revoke = fun sandbox -> grants <- grants |> Map.filter (fun _ (held, _) -> held <> sandbox)
              Close =
                fun () ->
                    Async.FromContinuations (fun (cont, _, _) -> server.close (fun _ -> cont ())) }
    }
