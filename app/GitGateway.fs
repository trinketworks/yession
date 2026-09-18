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
//
// WHOSE credential answers is not the sandbox's to say. A sandbox is shared — the agent's
// terminal, a person's, a repo's own setup block all run in one — so a route that named one
// lender spent that person's credential on everybody's push. The route names a sandbox; a
// LOAN names an act. Every block's line exports a per-block secret into its process tree
// (`loanConfig`: one `http.<here>.extraheader`, so git sends it on every request to this
// gateway and to nowhere else), and a request is answered from the loan it carries and from
// nothing else — no loan, no credential, said in words. A loan is returned when the next
// block on that terminal starts, so a `git push &` a block left running still spends its own
// act's credential, and a request arriving after that is refused in words rather than
// answered with somebody else's.
//
// What this does NOT do: every process in a sandbox is one uid, and a block can read another
// block's live loan out of `/proc` — or the block's own can print it, since it sits in the
// environment `env` dumps. That is the trust boundary a sandbox already is (docs/GAPS.md),
// stated rather than solved; what IS held is that a loan answers only on the route of the
// sandbox it was lent in, so a loan that leaks travels no further than that sandbox.

open System
open Fable.Core
open Fable.Core.JsInterop
open Fable.NodeExtras
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
      Owner : CredentialFor
      /// The credential now. `None` = the owner has none (any more).
      Resolve : unit -> Async<string option>
      /// github.com refused it: tell whoever tracks the credential's health.
      Refused : unit -> Async<unit>
      /// A push went out on it, to `owner/repo` — whether github.com then took the push is
      /// git's to print. Told so the log can say whose credential a push spent.
      Spent : string -> Async<unit> }

/// One request the gateway will carry, parsed off a path. Only the three requests git's
/// smart HTTP transport makes are requests here; everything else 404s, so a cap admits git to
/// a repository and admits nothing to the rest of github.com.
[<RequireQualifiedAccess>]
type GitRequest =
    { Cap : string
      /// `owner/repo.git/info/refs` and the like — the path under the upstream origin.
      Path : string
      /// `owner/repo`, as a sentence names it — the `.git` a URL carries taken off.
      Repo : string
      /// `git-upload-pack` (fetch) or `git-receive-pack` (push).
      Service : string }

let private repoOf (owner: string) (repo: string) : string =
    owner + "/" + (if repo.EndsWith ".git" then repo.Substring (0, repo.Length - 4) else repo)

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
        |> Option.map (fun s ->
            { GitRequest.Cap = cap; Path = sprintf "%s/%s/info/refs" owner repo; Repo = repoOf owner repo; Service = s })
    | [ p; cap; host; owner; repo; posted ] when
        p = prefix && method = "POST" && host = remoteHost && segmentOk cap && segmentOk owner && segmentOk repo
        ->
        service' posted
        |> Option.map (fun s ->
            { GitRequest.Cap = cap; Path = sprintf "%s/%s/%s" owner repo s; Repo = repoOf owner repo; Service = s })
    | _ -> None

/// The git config a sandbox is given so that its git reaches github.com through the gateway
/// at `host:port` under `cap`. One `insteadOf`, which is what makes it full git: every
/// transport operation rewrites its URL through this, and nothing else about git changes.
let gitConfig (host: string) (port: int) (cap: string) : (string * string) list =
    [ sprintf "url.http://%s:%d/%s/%s/%s/.insteadOf" host port prefix cap remoteHost, sprintf "https://%s/" remoteHost ]

/// The header a block's git carries its loan in. Dropped on the way up with the rest of what
/// described this hop (`droppedUpstream`): it names a loan at THIS gateway and means nothing
/// at github.com.
let loanHeader = "x-yession-loan"

/// The git config a block's line exports so that its git carries `secret` to this gateway on
/// every request — scoped to the gateway's origin (git matches `http.<url>.*` against the URL
/// it connects to, which after the `insteadOf` above is this one), so it goes to no other
/// host. One pair, which is what `__y_env` has a slot for.
let loanConfig (host: string) (port: int) (secret: string) : string * string =
    sprintf "http.http://%s:%d/.extraheader" host port, sprintf "X-Yession-Loan: %s" secret

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

// --- carrying one request ---------------------------------------------------------------------

/// What a request does NOT carry up to github.com. `host` and `expect` because they describe
/// the request as it arrived HERE — the upstream's host is its own, and Node's server has
/// already answered the `100 Continue` that `expect` asked for; `authorization` because the
/// one that goes out is the lender's, not whatever came in; and the rest because they are
/// hop-by-hop, which is to say they describe a connection this process is not forwarding;
/// and the loan, which named a lender here and is spent here.
let private droppedUpstream =
    set
        [ "host"
          "authorization"
          loanHeader
          "connection"
          "expect"
          "keep-alive"
          "proxy-authorization"
          "proxy-connection"
          "te"
          "trailer"
          "transfer-encoding"
          "upgrade" ]

/// What the answer does NOT carry back down: the three that describe the upstream connection
/// this process terminated, and that Node decides for itself on the response it is writing.
/// Deliberately NOT the whole hop-by-hop set above — everything else github.com says about
/// its answer is git's to read.
let private droppedDownstream = set [ "connection"; "keep-alive"; "transfer-encoding" ]

/// The headers this gateway carries UP: what the sandbox's git sent, less the set above, and
/// then the lender's credential — appended rather than merged, because `authorization` is in
/// that set and so cannot already be there.
///
/// Node lowercases a header name on the way in, so the comparison is against lowercase and
/// does no folding of its own. A value passes through as it arrived: a string, or an array of
/// them for a header that repeated.
let upstreamHeaders (sent: (string * obj)[]) (authorization: string) : (string * obj)[] =
    Array.append
        (sent |> Array.filter (fun (name, _) -> not (Set.contains name droppedUpstream)))
        [| "authorization", box authorization |]

/// The headers it carries back DOWN: github.com's, as they came, less the three above.
let downstreamHeaders (received: (string * obj)[]) : (string * obj)[] =
    received |> Array.filter (fun (name, _) -> not (Set.contains name droppedDownstream))

/// Whether an upstream status is relayed to the sandbox's git as it stands. Nearly every one
/// is — a 200, the `403` github.com refuses a push with, a 404 — because git already knows
/// how to read those. A `401` is the exception: relaying it would make the sandbox's git
/// prompt for a password nobody can type, so it comes back to the caller unanswered, to be
/// said in words (`errorBody`).
let relayed (status: int) : bool = status <> 401

/// What became of one forwarded request — the three outcomes the caller acts on, and no
/// more. What separates them is whether anything has gone out on the sandbox's response yet,
/// because that is what decides whether a refusal can still be SAID.
[<RequireQualifiedAccess>]
type Forwarded =
    /// github.com answered `401`, which `relayed` says is not carried down. Nothing has been
    /// written, so the caller can still say what happened.
    | Unauthorized
    /// The request never got an answer out of github.com, and nothing has been written.
    | Unreachable of string
    /// The head went out. Whether the body then ran to its end or the connection broke
    /// partway — in which case the response is destroyed rather than finished — there is no
    /// channel left to say anything else on.
    | Answered

/// Carry one request to the upstream and its answer back, adding the credential on the way.
///
/// The bytes are PIPED, never read: git gzips its posts and expects its responses untouched,
/// so a `fetch` — which decodes bodies for you — is exactly the wrong tool. Which headers go
/// each way is `upstreamHeaders`/`downstreamHeaders`, and which statuses come back as they
/// are is `relayed`; all three are stated above, where a test can reach them without a socket.
let private forward
    (req: IncomingMessage)
    (res: ServerResponse)
    (url: string)
    (authorization: string)
    : Async<Forwarded> =
    Async.FromContinuations (fun (cont, _, _) ->
        // ONE answer, whatever arrives first. Every path below can be reached after another
        // already has — an upstream that errors after its response ended, a request body that
        // fails while the answer is streaming — and a second continuation would resume the
        // caller twice, on a response it has already finished with.
        let mutable settled = false

        let finish (outcome: Forwarded) =
            if not settled then
                settled <- true
                cont outcome

        let up =
            httpRequest url req.``method`` (upstreamHeaders (req.headerEntries ()) authorization) (fun answer ->
                if relayed answer.statusCode then
                    res.writeHead (answer.statusCode, createObj (downstreamHeaders (answer.headerEntries ()))) |> ignore
                    answer.pipe res
                    answer.onEnd (fun () -> finish Forwarded.Answered)
                    answer.onError (fun _ ->
                        res.destroy ()
                        finish Forwarded.Answered)
                else
                    // Drained rather than relayed: nothing here wants the body, and a
                    // response left paused holds its socket open.
                    answer.resume ()
                    finish Forwarded.Unauthorized)

        up.onError (fun error ->
            // A head already out is a promise this gateway can no longer keep: destroy the
            // response rather than end it, so git reads a broken stream instead of a
            // truncated answer it would believe.
            if res.headersSent then
                res.destroy ()
                finish Forwarded.Answered
            else
                finish (Forwarded.Unreachable (StreamError.describe error)))

        req.onError (fun _ -> up.destroy ())
        req.pipe up)

/// The credential as github.com's git endpoint takes it: HTTP basic, `x-access-token` as
/// the user. (A bearer header is what the API takes and what the git endpoint answers 401
/// to — another thing the motivating session learnt by trying.)
/// Whose credential a sentence git prints is about.
let private ownerLabel (owner: CredentialFor) : string = CredentialFor.token owner

let private basicAuthorization (token: string) : string =
    "Basic " + Convert.ToBase64String (Text.Encoding.UTF8.GetBytes ("x-access-token:" + token))

// --- the gateway ---------------------------------------------------------------------------

type Gateway =
    { /// The bound port, on every interface.
      Port : int
      /// Mint the route for one sandbox, replacing any it had: the cap its git will name. A
      /// route answers nothing by itself — what answers on it is the loan a request carries.
      Grant : SandboxRef -> string
      /// Lend a block's requests a credential: the secret the block's line exports, live
      /// until the terminal's next loan or `Retire`. One live loan per terminal, because a
      /// terminal runs one block at a time — lending the next returns the last.
      Lend : SandboxRef -> TerminalId -> Lender -> string
      /// The loan on a terminal is returned. A request still carrying it is refused in
      /// words; the last returned loan per terminal is kept for that sentence, and no more.
      Retire : TerminalId -> unit
      /// Take a sandbox's route, and every loan under it, away — what makes the cap die
      /// with the sandbox.
      Revoke : SandboxRef -> unit
      Close : unit -> Async<unit> }

/// One block's loan: where it was lent, and who answers on it.
type private Loan =
    { Sandbox : SandboxRef
      Terminal : TerminalId
      Lender : Lender }

/// Start the gateway. `upstream` is the origin github.com's git endpoints live under — a
/// parameter for the reason every provider endpoint here is: a suite needs somewhere to
/// point it that is not the live provider. `report` is where a fault goes that git can no
/// longer be told about: once the answer's headers are out there is no `ERR` channel left,
/// and a fault swallowed there is a fault nobody hears — which for a push's audit record is
/// a push that went out on somebody's credential with no line saying so.
let start (upstream: string) (report: string -> unit) : Async<Gateway> =
    let mutable grants : Map<string, SandboxRef> = Map.empty
    /// Live loans, by secret.
    let mutable live : Map<string, Loan> = Map.empty
    /// The last loan each terminal returned, by terminal — so a request still carrying it
    /// is told so, rather than 404'd like a secret nobody ever minted.
    let mutable returned : Map<string, string * Loan> = Map.empty

    let retire (terminal: TerminalId) =
        for KeyValue (secret, loan) in live do
            if loan.Terminal = terminal then returned <- Map.add (TerminalId.value terminal) (secret, loan) returned
        live <- live |> Map.filter (fun _ loan -> loan.Terminal <> terminal)

    let answer (res: ServerResponse) (status: int) (contentType: string) (body: string) =
        res.writeHead (status, createObj [ "content-type", box contentType; "cache-control", box "no-store" ]) |> ignore
        res.``end`` body

    let notFound (res: ServerResponse) = answer res 404 "text/plain" "not found"

    /// The loan a request carries, if git sent one.
    let loanOf (req: IncomingMessage) : string option =
        req.headerEntries ()
        |> Array.tryPick (fun (name, value) -> if name = loanHeader then Some (unbox<string> value) else None)

    let handler (req: IncomingMessage) (res: ServerResponse) =
        let url = req.url
        match route req.``method`` (pathnameOf url) (queryOf url "service") with
        | None -> notFound res
        | Some request ->
            match Map.tryFind request.Cap grants with
            | None -> notFound res
            | Some routed ->
                let isAdvertisement = req.``method`` = "GET"
                let contentType =
                    sprintf "application/x-%s-%s" request.Service (if isAdvertisement then "advertisement" else "result")
                let refuse (message: string) =
                    answer res 200 contentType (errorBody request isAdvertisement message)
                match loanOf req with
                | None ->
                    refuse
                        "nothing is lending a github credential to this request — git here spends the credential of the command it runs in; run it as a command in this terminal"
                | Some secret ->
                    // A loan answers on the route of the sandbox it was lent in, and on no
                    // other. Carried down another sandbox's route it is a secret THAT route
                    // never minted, and is answered like one: every process in a sandbox
                    // shares one uid, so what this bounds is how far a loan read out of a
                    // neighbour's environment can travel — one sandbox, not the session.
                    let onThisRoute (loan: Loan) = loan.Sandbox = routed
                    match Map.tryFind secret live |> Option.filter onThisRoute with
                    | None ->
                        match
                            returned
                            |> Map.tryPick (fun _ (held, loan) -> if held = secret && onThisRoute loan then Some loan else None)
                        with
                        | Some loan ->
                            refuse (
                                sprintf
                                    "the github credential lent to %s for that command has been returned — run the push as its own command"
                                    (ownerLabel loan.Lender.Owner))
                        | None -> notFound res
                    | Some loan ->
                        let lender = loan.Lender
                        Async.StartImmediate (
                            async {
                                try
                                    match! lender.Resolve () with
                                    | None ->
                                        refuse (
                                            sprintf
                                                "%s has not connected github — connect it on the settings panel and run the command again"
                                                (ownerLabel lender.Owner))
                                    | Some token ->
                                        let target = upstream.TrimEnd '/' + "/" + request.Path + searchOf url
                                        match! forward req res target (basicAuthorization token) with
                                        | Forwarded.Unauthorized ->
                                            do! lender.Refused ()
                                            refuse (
                                                sprintf
                                                    "github rejected %s's credential — sign in again on the settings panel"
                                                    (ownerLabel lender.Owner))
                                        | Forwarded.Unreachable error ->
                                            refuse (sprintf "%s could not be reached from this session: %s" remoteHost error)
                                        | Forwarded.Answered ->
                                            // The push itself, not its advertisement: one
                                            // sentence per push, however many requests it took.
                                            // The push has already gone out, so a record that
                                            // cannot be written is reported rather than allowed
                                            // to read as a gateway fault git never sees.
                                            if request.Service = "git-receive-pack" && not isAdvertisement then
                                                try
                                                    do! lender.Spent request.Repo
                                                with e ->
                                                    report (
                                                        sprintf
                                                            "a push to %s went out on %s's github credential and the record of it could not be written: %s"
                                                            request.Repo
                                                            (ownerLabel lender.Owner)
                                                            e.Message)
                                with e ->
                                    if not (res.headersSent) then refuse (sprintf "the git gateway failed: %s" e.Message)
                                    else report (sprintf "the git gateway failed after answering a request for %s: %s" request.Repo e.Message)
                            })

    let server = createServer handler
    async {
        do!
            Async.FromContinuations (fun (cont, _, _) ->
                server.listen (0, "0.0.0.0", fun () -> cont ()) |> ignore)
        return
            { Port = serverPort server
              Grant =
                fun sandbox ->
                    let cap = randomSecret ()
                    grants <- grants |> Map.filter (fun _ held -> held <> sandbox) |> Map.add cap sandbox
                    cap
              Lend =
                fun sandbox terminal lender ->
                    retire terminal
                    let secret = randomSecret ()
                    live <- Map.add secret { Sandbox = sandbox; Terminal = terminal; Lender = lender } live
                    secret
              Retire = retire
              Revoke =
                fun sandbox ->
                    grants <- grants |> Map.filter (fun _ held -> held <> sandbox)
                    live <- live |> Map.filter (fun _ loan -> loan.Sandbox <> sandbox)
                    returned <- returned |> Map.filter (fun _ (_, loan) -> loan.Sandbox <> sandbox)
              Close =
                fun () ->
                    Async.FromContinuations (fun (cont, _, _) -> server.close (fun _ -> cont ())) }
    }
