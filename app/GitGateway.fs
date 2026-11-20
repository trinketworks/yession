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
// So the credential stays here, in the Session, and what the sandbox gets instead is
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
// The listener binds where a sandbox reaches this host, and nowhere else (`Interop.Doors`):
// loopback for the host backend and for a container on a daemon in a VM (Colima, Docker
// Desktop), whose `host.docker.internal` is the host loopback; srt's address on this
// platform (`Sandboxes.hostDoorsFrom`); and, on a native Linux daemon, the session's Docker
// network's gateway, opened when its first container starts. The route is a 128-bit
// capability minted per sandbox, so what is exposed at each is "this session's gateway, to
// whoever holds a cap for it" — the same trust a sandbox already has.
//
// WHOSE credential answers is not the sandbox's to say. A sandbox is shared — the agent's
// terminal, a person's, a repo's own setup block all run in one — so a route that named one
// lender spent that person's credential on everybody's push. The route names a sandbox; a
// LOAN names an act. Every block's line exports a per-block secret into its process tree
// (`loanConfig`: one `http.<here>.extraheader`, so git sends it on every request to this
// gateway and to nowhere else). The credential that goes up with a request is the credential
// of the loan that the request carries, and no other. A terminal returns a loan when its next
// block starts. So a `git push &` that a block left running still spends the credential of its
// own act. A request that arrives after that never goes up with the credential of a different
// act.
//
// A request with no live loan goes up with NO credential. It carries no loan, or it carries a
// loan that its terminal returned. github.com answers it as it answers anybody. A public
// repository reads. For each other request github.com answers `401`, and git prints the
// reason in words (`unlent`): why no credential went up.
//
// The gateway once refused such a request before it asked github.com. A route then took away
// what git does with no route at all. The `prepare:` command of a sandbox is not a block, so
// it holds no loan. In the first repository with such a command, nix fetched a public
// repository for an input of its lock file. That sandbox did not start for a person who
// trusted the repository.
//
// The session's own git takes the same route. `Repos`' clone and fetch run in the git
// sandbox, which is no work sandbox and has no terminal, so a route is minted for
// `Routed.SessionGit` and each verb borrows on its own key (`Borrower.Verb`): lent before the
// git process starts and returned when it exits, because two verbs can run at once. Before
// this the token rode in that git process as an `extraheader`, and a `.git/config` that any
// work sandbox can write could route it to a proxy or rewrite github.com away (rule 21 of
// docs/technical-design.md §5). One mechanism carries a credential to github.com now.
//
// What this does NOT do: every process in a sandbox is one uid, and a block can read another
// block's live loan out of `/proc` — or the block's own can print it, since it sits in the
// environment `env` dumps. That is the trust boundary a sandbox already is (docs/GAPS.md),
// stated rather than solved; what IS held is that a loan answers only on the route of the
// sandbox it was lent in, so a loan that leaks travels no further than that sandbox.
//
// A Mac has no `/proc`. The read there is the sysctl `kern.procargs2`, and a Seatbelt profile
// of version 1 that has only `(deny default)` does not refuse it for a process outside the
// sandbox. The seatbelt backend's profile refuses it (`SeatbeltProfile.head`). docs/GAPS.md
// says what srt on a Mac leaves open.

open System
open Fable.Core
open Fable.Core.JsInterop
open Fable.NodeExtras
open Node.Api
open Node.Buffer
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
      /// A push went out on it, to a repository — whether github.com then took the push is
      /// git's to print. Told so the log can say whose credential a push spent.
      Spent : RepoRef -> Async<unit> }

/// One request the gateway will carry, parsed off a path. Only the three requests git's
/// smart HTTP transport makes are requests here; everything else is not, so a route admits git
/// to a repository and admits nothing to the rest of github.com.
[<RequireQualifiedAccess>]
type GitRequest =
    { /// `owner/repo.git/info/refs` and the like — the path under the upstream origin.
      Path : string
      /// The repository, as a sentence names it — parsed by the same rule `add_repo`
      /// applies, which is what takes the `.git` a URL carries off and refuses a name no
      /// repository could have.
      Repo : RepoRef
      /// `git-upload-pack` (fetch) or `git-receive-pack` (push).
      Service : string }

/// The repository two path segments name, or none: `RepoRef.create` is the one rule for
/// what a repository may be called, and a path that fails it is not a git request here.
let private repoOf (owner: string) (repo: string) : RepoRef option =
    match RepoRef.create (owner + "/" + repo) with
    | Ok r -> Some r
    | Error _ -> None

let private segmentOk (segment: string) =
    let ok (c: char) =
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
        || c = '.' || c = '-' || c = '_'
    segment <> "" && segment <> "." && segment <> ".." && Seq.forall ok segment

/// The git request a method, the path segments under github.com's origin and `?service=`
/// name, or none: the advertisement (`GET <owner>/<repo>/info/refs?service=…`) and the two
/// service posts.
let private requestOf (method: string) (segments: string list) (service: string option) : GitRequest option =
    let service' (name: string) =
        if name = "git-upload-pack" || name = "git-receive-pack" then Some name else None
    match segments with
    | [ owner; repo; "info"; "refs" ] when method = "GET" && segmentOk owner && segmentOk repo ->
        match repoOf owner repo, service |> Option.bind service' with
        | Some r, Some s -> Some { GitRequest.Path = sprintf "%s/%s/info/refs" owner repo; Repo = r; Service = s }
        | _ -> None
    | [ owner; repo; posted ] when method = "POST" && segmentOk owner && segmentOk repo ->
        match repoOf owner repo, service' posted with
        | Some r, Some s -> Some { GitRequest.Path = sprintf "%s/%s/%s" owner repo s; Repo = r; Service = s }
        | _ -> None
    | _ -> None

let private segments (path: string) : string list = path.Trim('/').Split '/' |> Array.toList

/// The cap and the git request a method, path and `?service=` name at the gateway's own
/// listener, or none. The path is `/git/<cap>/github.com/<owner>/<repo>/…`.
let route (method: string) (path: string) (service: string option) : (string * GitRequest) option =
    match segments path with
    | p :: cap :: host :: rest when p = prefix && host = remoteHost && segmentOk cap ->
        requestOf method rest service |> Option.map (fun request -> cap, request)
    | _ -> None

/// The git request a method, path and `?service=` name on github.com itself, as a request
/// the credential proxy decrypted arrives (`GitHubAccess.gitRoute`). The path is
/// `/<owner>/<repo>/…`.
let proxiedRoute (method: string) (path: string) (service: string option) : GitRequest option =
    requestOf method (segments path) service

/// Where a sandbox's git reaches this gateway's handler. Two doors to one handler.
[<RequireQualifiedAccess>]
type Door =
    /// The gateway's own listener, at a host door, under a cap (`gitConfig`). For a sandbox
    /// the credential proxy does not admit (srt, seatbelt) and for the session's own git.
    | Listener of Sandboxes.HostDoor
    /// The credential proxy, which hands git's three requests on github.com to this handler
    /// (`GitHubAccess.gitRoute`) and names the sandbox by its admission (`proxiedGitConfig`).
    /// For a sandbox the proxy admits.
    | Proxy

/// The git config a sandbox is given so that its git reaches github.com through the gateway's
/// own listener at `door` under `cap`. One `insteadOf`, which is what makes it full git: every
/// transport operation rewrites its URL through this, and nothing else about git changes.
let gitConfig (door: Sandboxes.HostDoor) (cap: string) : (string * string) list =
    [ sprintf "url.http://%s:%d/%s/%s/%s/.insteadOf" door.Host door.Port prefix cap remoteHost, sprintf "https://%s/" remoteHost ]

/// The git config a sandbox the credential proxy admits is given instead: git keeps
/// `https://github.com/`, and reaches it through `proxy` (the sandbox's proxy URL, its
/// admission in the credentials) trusting what `trust` names (the proxy's bundle). No
/// `insteadOf`, so a client that refuses a rewrite of github.com to another host (apm) sees
/// none. Both are scoped to github.com, so nothing about git's other hosts changes. The trust
/// file is named to git itself: libcurl as Ubuntu builds it reads its compiled-in bundle and
/// ignores `SSL_CERT_FILE`.
let proxiedGitConfig (proxy: string) (trust: string) : (string * string) list =
    [ sprintf "http.https://%s/.proxy" remoteHost, proxy
      sprintf "http.https://%s/.sslCAInfo" remoteHost, trust ]

/// The header a block's git carries its loan in. Dropped on the way up with the rest of what
/// described this hop (`droppedUpstream`): it names a loan at THIS gateway and means nothing
/// at github.com.
let loanHeader = "x-yession-loan"

/// The git config a block's line exports so that its git carries `secret` to this gateway's
/// handler on every request — scoped to the URL git connects to through `door` (git matches
/// `http.<url>.*` against it): the gateway's origin behind the `insteadOf`, or github.com
/// itself behind the proxy, which withholds the header from every request it does not hand
/// here (`GitHubAccess.gitRoute`). One pair, which is what `__y_env` has a slot for.
let loanConfig (door: Door) (secret: string) : string * string =
    let scope =
        match door with
        | Door.Listener door -> sprintf "http://%s:%d/" door.Host door.Port
        | Door.Proxy -> sprintf "https://%s/" remoteHost
    sprintf "http.%s.extraheader" scope, sprintf "X-Yession-Loan: %s" secret

// --- the wire ------------------------------------------------------------------------------

/// The UTF-8 byte length, which a pkt-line's header counts — not the string length, which
/// a message with an em dash in it taught the difference of.
let private byteLength (s: string) : int = buffer.Buffer.byteLength (s, BufferEncoding.Utf8)

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
/// `None` is a request that no live loan answers for. It goes up with no `authorization`,
/// and not with the one that arrived: the sandbox holds no credential, so that one is its
/// own invention.
///
/// Node lowercases a header name on the way in, so the comparison is against lowercase and
/// does no folding of its own. A value passes through as it arrived: said once, or repeated.
let upstreamHeaders (sent: (string * HeaderValue)[]) (authorization: string option) : (string * HeaderValue)[] =
    Array.append
        (sent |> Array.filter (fun (name, _) -> not (Set.contains name droppedUpstream)))
        (authorization |> Option.map (fun value -> "authorization", HeaderValue.Single value) |> Option.toArray)

/// The headers it carries back DOWN: github.com's, as they came, less the three above.
let downstreamHeaders (received: (string * HeaderValue)[]) : (string * HeaderValue)[] =
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

/// Carry one request to the upstream and its answer back, adding the credential on the way
/// when a loan gives one (`upstreamHeaders`).
///
/// The bytes are PIPED, never read: git gzips its posts and expects its responses untouched,
/// so a `fetch` — which decodes bodies for you — is exactly the wrong tool. Which headers go
/// each way is `upstreamHeaders`/`downstreamHeaders`, and which statuses come back as they
/// are is `relayed`; all three are stated above, where a test can reach them without a socket.
let private forward
    (req: IncomingMessage)
    (res: ServerResponse)
    (url: string)
    (authorization: string option)
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
                    res.relayHead (answer.statusCode, downstreamHeaders (answer.headerEntries ()))
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

/// What git prints when github.com wants a credential for a request that went up with none:
/// that fact, and then `why` no credential went up. The first sentence is there because a
/// public repository reads with no credential. A reader of the second sentence alone asks
/// why this request needed one.
let private unlent (why: string) : string =
    sprintf "%s wants a credential for this request. %s" remoteHost why

// --- the gateway ---------------------------------------------------------------------------

/// Whose git a route is minted for: a work sandbox's, or the session's own git verbs (`clone`
/// and `fetch` in `Repos`), which run in the git sandbox and have no `SandboxRef` of their own.
[<RequireQualifiedAccess>]
type Routed =
    | Sandbox of SandboxRef
    | SessionGit

/// What a loan is lent to, and what returns it. A terminal runs one block at a time, so its
/// next loan returns its last. A session verb holds its loan for the one git process it spawns,
/// and two verbs can run at once, so each verb is a borrower of its own.
[<RequireQualifiedAccess>]
type Borrower =
    | Terminal of TerminalId
    | Verb of Guid

type Gateway =
    { /// The port the door at an address answers on (`Interop.Doors`): ask it with
      /// `Sandboxes.hostDoorFrom`, which knows which door a sandbox's name arrives at.
      PortAt : string -> int option
      /// Answer at one more address: where a session's containers reach this host on its
      /// Docker network (`DockerSandbox.Network.Reached`).
      Open : string -> Async<Result<unit, string>>
      /// Mint the route for one sandbox, replacing any it had: the cap its git will name. A
      /// route adds no credential by itself. The credential that goes up is the one of the loan
      /// that a request carries.
      Grant : Routed -> string
      /// Open a sandbox's route with no cap: for a sandbox whose git reaches this handler
      /// through the credential proxy (`Door.Proxy`), which names the sandbox itself.
      Route : Routed -> unit
      /// Answer one request the credential proxy decrypted on github.com, from the sandbox
      /// `routed` names: `true` when it is one of git's three requests on an open route, and
      /// answered here as the gateway's own listener answers it; `false` for anything else,
      /// which the proxy carries up with no credential.
      Serve : Routed -> IncomingMessage -> ServerResponse -> bool
      /// Lend a borrower's requests a credential: the secret its git carries, live until the
      /// borrower's next loan or `Retire`. One live loan per borrower — lending the next
      /// returns the last.
      Lend : Routed -> Borrower -> Lender -> string
      /// The loan a borrower holds is returned. A request that still carries the loan of a
      /// terminal goes up with no credential. When github.com then wants one, git prints a
      /// sentence that names whose loan it was. The gateway keeps the last returned loan of
      /// each terminal for that sentence, and no more. A verb's is dropped: its git has exited.
      Retire : Borrower -> unit
      /// Take a sandbox's route, and every loan under it, away — what makes the cap die
      /// with the sandbox.
      Revoke : SandboxRef -> unit
      Close : unit -> Async<unit> }

/// One loan: where it was lent, to whom, and who answers on it.
type private Loan =
    { Routed : Routed
      Borrower : Borrower
      Lender : Lender }

/// Start the gateway at `doors` (`Sandboxes.hostDoorsFrom`), a port per group. `upstream` is the origin github.com's git endpoints live under — a
/// parameter for the reason every provider endpoint here is: a suite needs somewhere to
/// point it that is not the live provider. `report` is where a fault goes that git can no
/// longer be told about: once the answer's headers are out there is no `ERR` channel left,
/// and a fault swallowed there is a fault nobody hears — which for a push's audit record is
/// a push that went out on somebody's credential with no line saying so.
let start (doors: string list list) (upstream: string) (report: string -> unit) : Async<Gateway> =
    let mutable grants : Map<string, Routed> = Map.empty
    /// Every route open, through either door: the caps above, and the routes of sandboxes
    /// whose git arrives through the credential proxy (`Route`).
    let mutable routes : Set<Routed> = Set.empty
    /// Live loans, by secret.
    let mutable live : Map<string, Loan> = Map.empty
    /// The last loan each terminal returned, by terminal — so a request still carrying it
    /// is told so, rather than 404'd like a secret nobody ever minted.
    let mutable returned : Map<string, string * Loan> = Map.empty

    let retire (borrower: Borrower) =
        match borrower with
        | Borrower.Terminal terminal ->
            for KeyValue (secret, loan) in live do
                if loan.Borrower = borrower then returned <- Map.add (TerminalId.value terminal) (secret, loan) returned
        | Borrower.Verb _ -> ()
        live <- live |> Map.filter (fun _ loan -> loan.Borrower <> borrower)

    let answer (res: ServerResponse) (status: int) (contentType: string) (body: string) =
        res.writeHead (status, [ ResponseHeader.ContentType contentType; ResponseHeader.CacheControl "no-store" ])
        res.``end`` body

    let notFound (res: ServerResponse) = answer res 404 "text/plain" "not found"

    /// The loan a request carries, if git sent one.
    let loanOf (req: IncomingMessage) : string option = headerOf req loanHeader

    /// One git request on `routed`'s route, through whichever door it arrived at.
    let carry (routed: Routed) (request: GitRequest) (req: IncomingMessage) (res: ServerResponse) =
        let url = req.url
        let isAdvertisement = req.``method`` = "GET"
        let contentType =
            sprintf "application/x-%s-%s" request.Service (if isAdvertisement then "advertisement" else "result")
        let refuse (message: string) =
            answer res 200 contentType (errorBody request isAdvertisement message)
        let target () = upstream.TrimEnd '/' + "/" + request.Path + (requestUrl url).search
        let unreachable (error: string) =
            refuse (sprintf "%s could not be reached from this session: %s" remoteHost error)
        // One carry. A fault before the head of the answer goes to git in words. A
        // fault after it goes to `report`, because no `ERR` channel remains by then.
        let run (work: Async<unit>) =
            Async.StartImmediate (
                async {
                    try
                        do! work
                    with e ->
                        if not (res.headersSent) then refuse (sprintf "the git gateway failed: %s" e.Message)
                        else report (sprintf "the git gateway failed after answering a request for %s: %s" (RepoRef.value request.Repo) e.Message)
                })
        // A request that no live loan answers for: up with no credential, and
        // github.com answers it as it answers anybody. `why` is what git prints when
        // github.com then wants a credential (`unlent`).
        let carryBare (why: string) =
            run (
                async {
                    match! forward req res (target ()) None with
                    | Forwarded.Unauthorized -> refuse (unlent why)
                    | Forwarded.Unreachable error -> unreachable error
                    | Forwarded.Answered -> ()
                })
        match loanOf req with
        | None ->
            carryBare
                "Nothing lends one to it. Git here spends the credential of the command it runs in. Run git as a command in this terminal."
        | Some secret ->
            // A loan answers on the route of the sandbox it was lent in, and on no
            // other. Carried down another sandbox's route it is a secret THAT route
            // never minted, and is answered like one: every process in a sandbox
            // shares one uid, so what this bounds is how far a loan read out of a
            // neighbour's environment can travel — one sandbox, not the session.
            let onThisRoute (loan: Loan) = loan.Routed = routed
            match Map.tryFind secret live |> Option.filter onThisRoute with
            | None ->
                match
                    returned
                    |> Map.tryPick (fun _ (held, loan) -> if held = secret && onThisRoute loan then Some loan else None)
                with
                | Some loan ->
                    carryBare (
                        sprintf
                            "The github credential lent to %s for that command has been returned. Run the push as its own command."
                            (ownerLabel loan.Lender.Owner))
                | None -> notFound res
            | Some loan ->
                let lender = loan.Lender
                run (
                    async {
                        match! lender.Resolve () with
                        | None ->
                            refuse (
                                sprintf
                                    "%s has not connected github. Connect it on the settings panel, then run the command again."
                                    (ownerLabel lender.Owner))
                        | Some token ->
                            match! forward req res (target ()) (Some (basicAuthorization token)) with
                            | Forwarded.Unauthorized ->
                                do! lender.Refused ()
                                refuse (
                                    sprintf
                                        "github rejected %s's credential. Sign in again on the settings panel."
                                        (ownerLabel lender.Owner))
                            | Forwarded.Unreachable error -> unreachable error
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
                                                "a push to %s used %s's github credential. The record of that push could not be written: %s"
                                                (RepoRef.value request.Repo)
                                                (ownerLabel lender.Owner)
                                                e.Message)
                    })

    /// The gateway's own listener: the `insteadOf` door, which names its route by a cap.
    let handler (req: IncomingMessage) (res: ServerResponse) =
        let url = req.url
        match route req.``method`` (pathnameOf url) (queryParamOf url "service") with
        | None -> notFound res
        | Some (cap, request) ->
            match Map.tryFind cap grants with
            | None -> notFound res
            | Some routed -> carry routed request req res

    async {
        let! doors = openDoors "the git gateway" (fun () -> createServer handler) doors
        return
            { PortAt = doors.PortAt
              Open = doors.Open
              Grant =
                fun routed ->
                    let cap = randomSecret ()
                    grants <- grants |> Map.filter (fun _ held -> held <> routed) |> Map.add cap routed
                    routes <- Set.add routed routes
                    cap
              Route = fun routed -> routes <- Set.add routed routes
              Serve =
                fun routed req res ->
                    let url = req.url
                    match Set.contains routed routes, proxiedRoute req.``method`` (pathnameOf url) (queryParamOf url "service") with
                    | true, Some request ->
                        carry routed request req res
                        true
                    | _ -> false
              Lend =
                fun routed borrower lender ->
                    retire borrower
                    let secret = randomSecret ()
                    live <- Map.add secret { Routed = routed; Borrower = borrower; Lender = lender } live
                    secret
              Retire = retire
              Revoke =
                fun sandbox ->
                    let routed = Routed.Sandbox sandbox
                    grants <- grants |> Map.filter (fun _ held -> held <> routed)
                    routes <- Set.remove routed routes
                    live <- live |> Map.filter (fun _ loan -> loan.Routed <> routed)
                    returned <- returned |> Map.filter (fun _ (_, loan) -> loan.Routed <> routed)
              Close = doors.Close }
    }
