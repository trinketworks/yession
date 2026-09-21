namespace Yession.App

open Yession.Domain
open Yession.Domain.Terminals

#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

/// The HTTP contract of a Session Process: every path it serves — and, for the two write
/// surfaces that carry one, the body it is served with — declared once. The server matches
/// on these, the shell emits them, and the browser client fetches them — the same role
/// `Dom` plays for markup hooks, one level up. Before this, `/client.js`
/// was spelled independently in the route match, in the emitted `<script src>`, and in
/// the browser's fetch, three strings that agreed only by inspection.
///
/// The property that matters: `relative` never emits a leading slash, and it is the only
/// way to render a route. A session does not necessarily own the root of its origin — an
/// operator's proxy may mount it under a path (Plan 09) — so a root-anchored URL is not
/// something a caller should be able to write by accident. What it renders is a
/// `RelativeUrl`, not a string: spelling one means saying what it resolves against — the
/// base a document declared (`RelativeUrl.inDocument`, the browser resolving it against the
/// shell's `<base href>`) or a mount named outright (`RelativeUrl.under`).

/// The Claude connection panel's write actions (Plan 08). Separate from the status read
/// so the two cannot be confused for one another. Qualified access is required because
/// these names are reused elsewhere in the domain (`Complete` is also a
/// `ConversationItemStatus`), which is exactly the ambiguity that makes an unqualified
/// case a hazard here.
[<RequireQualifiedAccess>]
type ClaudeAction =
    | Begin
    | Complete
    | Token
    | Disconnect

/// The GitHub connection panel's write actions (Plan 14). Same shape as `ClaudeAction`
/// with one difference of flow: GitHub signs in by DEVICE CODE (the panel shows a code,
/// the user approves on github.com, and this session polls the token endpoint), so
/// `Poll` stands where Claude's pasted-code `Complete` stands.
[<RequireQualifiedAccess>]
type GitHubAction =
    | Begin
    | Poll
    | Token
    | Disconnect

/// What a Claude panel write says, and the one place it is written.
///
/// Here rather than at either end because it HAS two ends, and they are in different
/// projects: the browser client posts this, `Yession.Host.ClaudeConnection` reads it. The
/// reading end declared the shape; the writing end stringified an anonymous record beside
/// it, and nothing compared them. A field renamed on one side is a field silently never
/// sent — which is the same class of fault `Route` was written to end for paths, one line
/// up from here.
///
/// `Scope` stays a string. The route answers an unknown one with its own 400 naming the
/// two it accepts, which is a better sentence than a decoder's, and the suites that pin
/// that refusal post the scope as text.
type ClaudeRequest =
    { Scope : string
      /// The authorization code a human pasted back: `Complete` carries one and nothing
      /// else does.
      Code : string option
      /// A pasted token or key: `Token` carries one and nothing else does.
      Token : string option }

/// What a GitHub panel write says: the same contract with no pasted code, because the
/// device flow never shows the browser one — `Poll` asks the session to go and look.
type GitHubRequest =
    { Scope : string
      Token : string option }

/// What the two bodies above share, in one place because they are one contract seen from
/// two panels: a scope a body may leave unsaid, and a field a body may leave out.
module private ConnectionBody =

    /// The scope a body that names none is read as: the signing actor's own credential,
    /// which is what the panel offers first and what a GET of a status route means.
    [<Literal>]
    let defaultScope = "mine"

    /// A field with no value is OMITTED, never sent blank.
    ///
    /// That used to be arranged in the browser, by handing `JSON.stringify` a `None` and
    /// relying on it to drop the `undefined` Fable compiles one to — a serialiser's quirk
    /// steered by hand, through a helper whose whole job was to turn `""` into it. The
    /// rule lives in the encoder now, where the optional field is declared optional.
    let optionalField (name: string) (value: string option) =
        value |> Option.map (fun text -> name, Encode.string text) |> Option.toList

module ClaudeRequest =

    open ConnectionBody


    let codec : Codec<ClaudeRequest> =
        { Encode =
            fun (request: ClaudeRequest) ->
                Encode.object
                    [ yield "scope", Encode.string request.Scope
                      yield! optionalField "code" request.Code
                      yield! optionalField "token" request.Token ]
          Decode =
            Decode.object (fun get ->
                { Scope = get.Optional.Field "scope" Decode.string |> Option.defaultValue defaultScope
                  Code = get.Optional.Field "code" Decode.string
                  Token = get.Optional.Field "token" Decode.string }) }

    /// A write that carries nothing but the scope it is about — `Begin`, `Disconnect`, and
    /// the GET of the status route, which asks about a scope without writing anything.
    let scoped (scope: string) : ClaudeRequest =
        { Scope = scope; Code = None; Token = None }

module GitHubRequest =

    open ConnectionBody

    let codec : Codec<GitHubRequest> =
        { Encode =
            fun (request: GitHubRequest) ->
                Encode.object
                    [ yield "scope", Encode.string request.Scope
                      yield! optionalField "token" request.Token ]
          Decode =
            Decode.object (fun get ->
                { Scope = get.Optional.Field "scope" Decode.string |> Option.defaultValue defaultScope
                  Token = get.Optional.Field "token" Decode.string }) }

    /// A write that carries nothing but the scope: every GitHub action but `Token`.
    let scoped (scope: string) : GitHubRequest =
        { Scope = scope; Token = None }

/// A route rendered as an address relative to the session's mount — and nothing else, which
/// is the point of it being a type rather than the string it wraps. The string was correct in
/// exactly one place, a document that had declared what it resolves against, and `string`
/// let it travel anywhere: the Manager's `/open` page copied the Manager page's relative
/// stylesheet link and lived at `/sessions/{id}/open`, so the link resolved to
/// `/sessions/{id}/assets/…`, a 404, and the page was white with a stylesheet link in it.
/// Three other places had already patched the same fact back by hand (`"/" + …`,
/// `mount + "/" + …`), which is the tell that the fact belongs on the value.
///
/// Private, so the string cannot be interpolated, `sprintf`'d or concatenated: it has to be
/// RESOLVED first (`RelativeUrl`), and resolving is where a caller says what it resolves
/// against — a document that has declared its base, or a mount named outright.
type RelativeUrl = private RelativeUrl of string

/// Proof that a document has declared its base, held by whoever renders inside it. Minted by
/// the code that WRITES the `<base href>` (`DocumentBase.declare`), or recovered in a browser
/// from a page that carries one (`DocumentBase.declared`) — and by the two documents that
/// resolve against their own address at the mount root, each named here with its reason.
/// Nothing else can produce one, so a relative address cannot be rendered into a document
/// that has not said where it is.
type DocumentBase = private DocumentBase of mount: string

type SessionRoute =
    /// The client shell itself — the served page IS the app.
    | Shell
    /// Any static file this build ships — `assets/<build>/<path>`, where `<build>` is a
    /// digest over the WHOLE asset set and `<path>` is the file's place inside it.
    ///
    /// One case, deliberately. This used to be four — the bundle, the stylesheet, the replay
    /// player's stylesheet, a typeface — and every asset added to the product cost a case
    /// here, a rendering, a parse, a case in each of the two servers, and a line in `stage`.
    /// That is a build serving what the ROUTER knows about, and it is backwards: a session
    /// upgraded on its own can bring a typeface, a sheet, an image that this Manager's
    /// binary has never heard of, and it must be able to serve them. So the route names a
    /// path and nothing else, and the server answers from whatever its own build left in the
    /// asset directory (`Assets`).
    ///
    /// The digest is on the DIRECTORY rather than on each file, which is what lets a
    /// stylesheet reference a face by a plain relative name: `url(fonts/x.woff2)` inside
    /// `assets/<build>/app.css` resolves to `assets/<build>/fonts/x.woff2` with no build-time
    /// rewriting, and immutability still holds because any change to any file changes
    /// `<build>` and so changes every address in the set.
    | Asset of build: string * path: string
    /// The service worker that makes a COLD open possible with no network (Plan 20). Served
    /// at the mount root and nowhere else: a worker's scope is its own path, so the same file
    /// under `assets/<build>/` would control the asset directory and nothing else — which is
    /// why this is not a fingerprinted asset. It carries the build digest in its BODY instead,
    /// so a new build is a byte-different worker and the browser updates it.
    | ServiceWorker
    /// The web manifest, which is what makes the shell installable — and, once installed,
    /// what makes it launch without the browser's chrome (`WebApp`). NOT fingerprinted: a
    /// browser re-reads it by the address the document names, and the document names this.
    | Manifest
    /// The app icon the manifest and the head both point at. Constant bytes in the binary,
    /// so a session with no assets directory beside it still has a mark.
    | Icon
    /// WebRTC offer in, answer out. The only interactive HTTP surface.
    | Signal
    /// The auth probe: mints the peer token a data channel's `PeerHello` needs.
    | Me
    /// Begin the authorization-code + PKCE bounce through the Manager.
    | Login
    /// Land the bounce back here.
    | Callback
    /// The event log's cursor: "what has happened after this offset?", `None` meaning
    /// from the beginning — the same `EventOffset option` the client's feed already takes.
    /// It carries no events and is never cached; it answers with a redirect to the
    /// `Events` range that does, or `204` when the caller is already current. This is the
    /// ONLY thing a client has to know how to build (Plan 20).
    | EventsAfter of after: EventOffset option
    /// The events at offsets `[first, last]`, which is the same answer for ever: the log
    /// is append-only and these bounds do not move. Named `Events` after the path, not
    /// after `EventChunk` — that module already exists in the domain, and one identifier
    /// meaning two things is what makes F# symbols hard to find.
    ///
    /// Only the server ever names one of these. A client receives the address in a
    /// redirect and keeps what came back under it, so it never has to know that a range
    /// has a size, a boundary, or an alignment.
    | Events of first: int64 * last: int64
    /// A terminal transcript's cursor (Plan 22): "what has this terminal printed after line
    /// `after`?", `None` meaning from the beginning. The event log's `EventsAfter` for the
    /// history leg of the terminal feed, and the only transcript address a client builds.
    /// The terminal is carried as a raw string because a route is a PATH, and validating it
    /// into a `TerminalId` is the server's job at dispatch, not the router's at parse.
    | TerminalTranscriptAfter of terminal: string * after: int option
    /// A terminal's transcript lines `[first, last]` — the same answer for ever, because a
    /// transcript is append-only and line index IS sequence number. Named for the range it
    /// carries, and reached only through the cursor's redirect.
    | TerminalTranscriptRange of terminal: string * first: int * last: int
    /// The keyframe for transcript line `seq` of a terminal (Plan 14, stage 3) — the screen
    /// a ranged replay starts from. Immutable on the same argument the chunks are: a
    /// keyframe is written once, at a position that never moves.
    | TerminalKeyframe of terminal: string * seq: int
    /// The Claude panel's current credential status, and — on the same reply — the models
    /// this session can run a turn on.
    ///
    /// One route, because it is one question: what can a turn run on here. The catalogue
    /// had a `/models` of its own, and the two answers drifted the moment their refresh
    /// triggers did — the picker kept a refusal naming an account that had since been
    /// connected, because signing in re-probed the status and nothing re-asked for the
    /// models. Which provider answers is still nothing the browser learns: the catalogue
    /// crosses as the same provider-neutral pair it always did.
    | ClaudeStatus
    /// One of the Claude panel's write actions.
    | Claude of action: ClaudeAction
    /// The GitHub panel's current credential status (Plan 14).
    | GitHubStatus
    /// One of the GitHub panel's write actions.
    | GitHub of action: GitHubAction
    /// The repositories the caller's GitHub credential reaches, most recently pushed
    /// first — or, with `?q=`, the ones whose name matches. What a person chooses a repo
    /// FROM, answered on their own credential so the list is what `add_repo` can clone.
    | GitHubRepos
    /// The branches of one repository, so a choice can name one. The two segments are
    /// carried raw for the terminal routes' reason: a route is a path, and making a
    /// `RepoRef` of them is the server's job at dispatch.
    | GitHubBranches of owner: string * repo: string
    /// Where one pull request comes from — the repository holding its head and the
    /// branch — so a pasted link to one can be checked out. The number is carried raw
    /// with the segments, for the same reason.
    | GitHubPullHead of owner: string * repo: string * number: string
    /// The session's read-only query surface (Plan 15): one multiplexed SSE stream
    /// carrying every registered query's declaration and value. It is a STREAM rather
    /// than a fetch-plus-stream pair because its opening burst already is the snapshot,
    /// so a second route would only be a second thing to keep correct.
    ///
    /// This is where the Repos panel's `/repos*` routes went. Their listing is now a
    /// query, and their three write actions were retired outright: a human asks the agent
    /// to add a repo, and the mutation lands in the timeline attributed (Plan 15).
    | Queries

module SessionRoute =

    /// Where every static file sits, relative to whatever the session is mounted at.
    /// Public because the service worker keeps everything under it and nothing else
    /// (`WebApp.serviceWorker`) — the worker and the router agreeing by inspection is
    /// exactly what this type exists to prevent.
    let assetsPrefix = "assets/"

    /// A path inside the asset set, as a build emits it. Segments are ordinary file names, so
    /// anything outside that shape is simply not a route — which is also what keeps a path
    /// the server may look up from carrying a dot-segment.
    let private isAssetPath (segments: string list) =
        let ok (c: char) =
            (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
            || c = '.' || c = '-' || c = '_'
        not segments.IsEmpty
        && segments |> List.forall (fun s -> s <> "" && not (s.Contains "..") && Seq.forall ok s)

    let private claudeSegment (action: ClaudeAction) =
        match action with
        | ClaudeAction.Begin -> "begin"
        | ClaudeAction.Complete -> "complete"
        | ClaudeAction.Token -> "token"
        | ClaudeAction.Disconnect -> "disconnect"

    let private githubSegment (action: GitHubAction) =
        match action with
        | GitHubAction.Begin -> "begin"
        | GitHubAction.Poll -> "poll"
        | GitHubAction.Token -> "token"
        | GitHubAction.Disconnect -> "disconnect"

    /// A route as an address relative to whatever the session is mounted at. Never begins
    /// with `/` — that is the whole point (see the type's remarks) — and comes back as a
    /// `RelativeUrl`, so the only way to spell it is to say what it resolves against.
    /// `Shell` is the empty string, which resolves to the mount point itself.
    let relative (route: SessionRoute) : RelativeUrl =
        RelativeUrl (
          match route with
          | Shell -> ""
          | Asset (build, path) -> assetsPrefix + build + "/" + path
          | ServiceWorker -> "sw.js"
          | Manifest -> "manifest.webmanifest"
          | Icon -> "icon.png"
          | Signal -> "signal"
          | Me -> "me"
          | Login -> "login"
          | Callback -> "callback"
          | EventsAfter None -> "events"
          | EventsAfter (Some after) -> sprintf "events/after/%d" (EventOffset.value after)
          | Events (first, last) -> sprintf "events/%d-%d" first last
          | TerminalTranscriptAfter (terminal, None) -> sprintf "terminals/%s" terminal
          | TerminalTranscriptAfter (terminal, Some after) -> sprintf "terminals/%s/after/%d" terminal after
          | TerminalTranscriptRange (terminal, first, last) -> sprintf "terminals/%s/%d-%d" terminal first last
          | TerminalKeyframe (terminal, seq) -> sprintf "terminals/%s/keyframes/%d" terminal seq
          | ClaudeStatus -> "claude"
          | Claude action -> "claude/" + claudeSegment action
          | GitHubStatus -> "github"
          | GitHub action -> "github/" + githubSegment action
          | GitHubRepos -> "github/repos"
          | GitHubBranches (owner, repo) -> sprintf "github/repos/%s/%s/branches" owner repo
          | GitHubPullHead (owner, repo, number) -> sprintf "github/repos/%s/%s/pulls/%s" owner repo number
          | Queries -> "queries")

    /// A route as an absolute URL under a session's address — what a client outside a
    /// browser needs, having no document base to resolve against. The single `/` between
    /// the two halves lives here, so no caller writes a leading slash of its own.
    let at (sessionUrl: string) (route: SessionRoute) : string =
        let (RelativeUrl path) = relative route
        sessionUrl.TrimEnd '/' + "/" + path

    /// The route a request is for, or None when the session serves nothing there — which
    /// includes a known path reached with the wrong method, so a mismatch 404s exactly as
    /// an unknown one does. Taking the method here is what lets the server dispatch with a
    /// single match over this type: a new case then fails the build until every consumer
    /// handles it, which is the reason the contract is a union at all.
    let parse (method: string) (path: string) : SessionRoute option =
        let segments = path.Trim('/').Split '/' |> Array.toList
        match method, segments with
        | "GET", [ "" ] -> Some Shell
        | "POST", [ "signal" ] -> Some Signal
        | "GET", [ "me" ] -> Some Me
        | "GET", [ "sw.js" ] -> Some ServiceWorker
        | "GET", [ "manifest.webmanifest" ] -> Some Manifest
        | "GET", [ "icon.png" ] -> Some Icon
        // Everything static, by path, with no opinion about what is in there.
        | "GET", "assets" :: build :: path when isAssetPath (build :: path) ->
            Some (Asset (build, String.concat "/" path))
        | "GET", [ "login" ] -> Some Login
        | "GET", [ "callback" ] -> Some Callback
        | "GET", [ "events" ] -> Some (EventsAfter None)
        | "GET", [ "events"; "after"; offset ] ->
            match System.Int64.TryParse offset with
            | true, parsed ->
                // An unparseable offset is not this route, and a negative one is not an
                // offset at all — `EventOffset.create` owns that rule, so it is not
                // restated here.
                match EventOffset.create parsed with
                | Ok o -> Some (EventsAfter (Some o))
                | Error _ -> None
            | _ -> None
        | "GET", [ "events"; range ] ->
            // `{first}-{last}`. Both bounds are required and the pair is validated here,
            // because an inverted or over-long range is not an address this server has —
            // and a request for one must 404 rather than be answered with something
            // shorter, which a client would then keep for ever as if it were the whole
            // range. (`Assets.serve` 404s a build that is not ours for the same reason.)
            match range.Split '-' with
            | [| first; last |] ->
                match System.Int64.TryParse first, System.Int64.TryParse last with
                | (true, f), (true, l) when f >= 0L && l >= f && l - f < int64 EventChunk.size ->
                    Some (Events (f, l))
                | _ -> None
            | _ -> None
        | "GET", [ "terminals"; terminal; "keyframes"; seq ] ->
            match System.Int32.TryParse seq with
            | true, parsed when parsed >= 0 && terminal <> "" -> Some (TerminalKeyframe (terminal, parsed))
            | _ -> None
        | "GET", [ "terminals"; terminal ] when terminal <> "" -> Some (TerminalTranscriptAfter (terminal, None))
        | "GET", [ "terminals"; terminal; "after"; seq ] ->
            match System.Int32.TryParse seq with
            | true, parsed when parsed >= 0 && terminal <> "" ->
                Some (TerminalTranscriptAfter (terminal, Some parsed))
            | _ -> None
        | "GET", [ "terminals"; terminal; range ] ->
            // `{first}-{last}`, validated here on exactly the argument the event ranges are:
            // an inverted or over-long range is not an address this server has, and must 404
            // rather than be answered with something shorter than the address promised.
            match range.Split '-' with
            | [| first; last |] ->
                match System.Int32.TryParse first, System.Int32.TryParse last with
                | (true, f), (true, l) when terminal <> "" && f >= 0 && l >= f && l - f < TranscriptChunk.size ->
                    Some (TerminalTranscriptRange (terminal, f, l))
                | _ -> None
            | _ -> None
        | "GET", [ "claude" ] -> Some ClaudeStatus
        | "POST", [ "claude"; "begin" ] -> Some (Claude ClaudeAction.Begin)
        | "POST", [ "claude"; "complete" ] -> Some (Claude ClaudeAction.Complete)
        | "POST", [ "claude"; "token" ] -> Some (Claude ClaudeAction.Token)
        | "POST", [ "claude"; "disconnect" ] -> Some (Claude ClaudeAction.Disconnect)
        | "GET", [ "github" ] -> Some GitHubStatus
        | "POST", [ "github"; "begin" ] -> Some (GitHub GitHubAction.Begin)
        | "POST", [ "github"; "poll" ] -> Some (GitHub GitHubAction.Poll)
        | "POST", [ "github"; "token" ] -> Some (GitHub GitHubAction.Token)
        | "POST", [ "github"; "disconnect" ] -> Some (GitHub GitHubAction.Disconnect)
        | "GET", [ "github"; "repos" ] -> Some GitHubRepos
        | "GET", [ "github"; "repos"; owner; repo; "branches" ] when owner <> "" && repo <> "" ->
            Some (GitHubBranches (owner, repo))
        | "GET", [ "github"; "repos"; owner; repo; "pulls"; number ] when owner <> "" && repo <> "" && number <> "" ->
            Some (GitHubPullHead (owner, repo, number))
        | "GET", [ "queries" ] -> Some Queries
        | _ -> None

    /// The route a request is for when this session is served under `mount` (`""` at an
    /// origin root). The operator's proxy forwards the PUBLIC path unchanged, so a
    /// path-mounted session sees its own prefix and strips it here — one place, the same
    /// string the shell's `<base href>` and the cookie's `Path` are built from. A request
    /// that does not carry the prefix is not this session's and gets the ordinary 404.
    let parseUnder (mount: string) (method: string) (path: string) : SessionRoute option =
        if mount = "" then parse method path
        elif path = mount then parse method "/"
        elif path.StartsWith (mount + "/") then parse method (path.Substring mount.Length)
        else None

module DocumentBase =

    let private escapeAttr (s: string) =
        s.Replace("&", "&amp;").Replace("\"", "&quot;").Replace("<", "&lt;").Replace(">", "&gt;")

    /// Declare a document's base — the `<base href>` tag to write, and the proof of having
    /// written it. ONE value with two halves, so the tag and the witness cannot come apart:
    /// a shell that takes the witness has emitted the tag.
    ///
    /// `mount` is the path the document is served under (`""` at an origin root). The
    /// trailing `/` is what makes `/s/{id}` and `/s/{id}/` resolve the same relative
    /// address, which is why the session shell needs a base at all.
    let declare (mount: string) : DocumentBase * string =
        DocumentBase mount, sprintf "<base href=\"%s/\">" (escapeAttr mount)

    /// The base a running page has, from what the page carries. `None` is a page that
    /// declared none — a shell this client cannot resolve anything from, which is an error
    /// to raise at boot rather than a 404 to meet on the first fetch.
    let declared (baseHref: string option) : Result<DocumentBase, string> =
        match baseHref with
        | Some href -> Ok (DocumentBase (href.TrimEnd '/'))
        | None -> Error "this page declares no <base href>; relative routes have nothing to resolve against"

    /// The web manifest resolves its own contents against ITS address, and it is served at
    /// the mount root beside the shell (`SessionRoute.Manifest` has no path segments) — so
    /// inside it, the bare relative form is right without a tag.
    let manifest : DocumentBase = DocumentBase ""

    /// The service worker resolves what it precaches against `self.registration.scope`,
    /// and its scope is where it is served — the mount root (`SessionRoute.ServiceWorker`),
    /// for the same reason as the manifest.
    let serviceWorker : DocumentBase = DocumentBase ""

module RelativeUrl =

    /// Inside a document that has declared its base: the bare relative form, resolved by
    /// the browser against that base. The witness is the whole argument — it is not read,
    /// it is the proof that there is something to resolve against.
    let inDocument (DocumentBase _) (RelativeUrl path) : string = path

    /// Anchored under a mount (`""` for an origin root): always begins with `/`. For a
    /// document that has declared no base — every page the Manager serves, which lives at
    /// its origin root by `ManagerOrigin`'s own rule — and for a server recognising or
    /// redirecting to its own paths. The single `/` between the halves lives here, so no
    /// caller writes one of its own.
    let under (mount: string) (RelativeUrl path) : string = mount + "/" + path

/// Which build's asset set a document should name: a digest over every static file this
/// process ships. It used to be one digest per asset, which meant a renderer had to be handed
/// each of them and a new asset changed the type. A set has one address, so a document that
/// has this can name ANY file in it — including one this code has never heard of.
type AssetBuild =
    | AssetBuild of digest: string

module AssetBuild =

    /// Where `file` is served for this build — the address a document should name. Relative,
    /// like every route, and typed like one: the document resolves it against its declared
    /// base or anchors it under its mount, and the stylesheet's own relative `url()`s then
    /// resolve against IT. Takes the declared file rather than a path, so a document cannot
    /// name one this build does not ship.
    let url (AssetBuild digest) (file: AssetFile) : RelativeUrl =
        SessionRoute.relative (Asset (digest, AssetFile.path file))

    /// The set's own address, for the one consumer that names no file: the service worker,
    /// which keeps a cache PER BUILD and drops the others (Plan 20). It is the digest and
    /// nothing else, so a new build is a new cache name and a byte-different worker.
    let digest (AssetBuild d) = d

/// What the two static surfaces may be cached for, stated once because the session server and
/// the Manager UI both serve them and the pair only works together: the shell is the document
/// that NAMES the fingerprinted assets, so caching it would pin the whole UI to the build it
/// was rendered against — which is exactly the bug that produced these values (a 24-hour
/// window on stable URLs made every release invisible for a day).
///
/// The event log and the transcripts are deliberately NOT here. Their answers are kept by
/// the client itself, in a store it can enumerate and ask to persist, so every response on
/// those surfaces is `no-store` — a header inviting a second copy into the HTTP cache would
/// be the redundant spare, not a belt (Plan 20, Plan 22).
module CachePolicy =

    /// A fingerprinted asset: the address changes whenever the bytes do, so a cache entry can
    /// never be stale — only unused. `public`, unlike the session's own surfaces: these are
    /// ungated static bytes, identical for every user.
    let asset = "public, max-age=31536000, immutable"

    /// The shell. `no-cache` is "revalidate before every use", NOT "do not store" — the
    /// browser keeps the copy and asks; an `ETag` turns the usual answer into a 304.
    let shell = "no-cache"

    /// A transcript keyframe: written once, at a line that never moves, so its bytes can
    /// never change. `private`, because it sits behind the session's per-user authorization
    /// and no shared cache may hold a screen from someone's terminal.
    ///
    /// The one session surface still served out of the HTTP cache, and it stays there for
    /// the reason the ranges left: a keyframe is fetched only by a replay a person opened,
    /// so there is nothing to read back offline that they did not just ask for.
    let keyframe = "private, max-age=259200, immutable"

/// The lifecycle acts the management page performs on ONE session, each a POST to
/// `/sessions/{id}/<verb>`. Named apart from the routes so a row's control and the route it
/// posts to are one value rather than a string the page builds and the server re-parses.
[<RequireQualifiedAccess>]
type SessionVerb =
    | Launch
    | Stop
    | Archive
    | Unarchive

/// The HTTP contract of the Manager's management surface (`ManagerUi`): every path it
/// claims, declared once — the same role `SessionRoute` plays for a Session Process. The
/// server dispatches over this, the page emits these, and the session client's reconnect
/// link and the registry subscriber address the Manager through them. Before this, the
/// page's inline script spelled `/sessions/{id}/launch` on its own, the router matched
/// `[| id; "launch" |]`, and the create redirect `sprintf`'d `/sessions/%s/open` — one
/// contract in three hand-kept copies.
///
/// Root-anchored, unlike a session's routes, and deliberately so: the Manager lives at its
/// origin root by `ManagerOrigin`'s own rule (a public address with a path is refused), and
/// its pages sit at different depths — `/` and `/sessions/{id}/open` — so the root-anchored
/// form is the one that is true from every one of them, and a relative form is the bug
/// `RelativeUrl` exists to prevent. Hence `path`, a string that always begins with `/`,
/// and no `RelativeUrl` here at all.
///
/// The control routes (`ControlServer`, secret-bearing, spoken by a session rather than a
/// browser) are a different contract and are not here.
[<RequireQualifiedAccess>]
type ManagerRoute =
    /// The management page.
    | Home
    /// A static file of THIS process's build — the same shape a session serves
    /// (`SessionRoute.Asset`), because the Manager's page links the same stylesheet.
    | Asset of build: string * path: string
    /// The mark the page wears, from the same constant a session shell wears.
    | Icon
    /// Register a session; answers with a redirect to `OpenSession`.
    | CreateSession
    /// The registry stream: the Running set as wire frames, for an operator's proxy.
    | SessionRegistry
    /// The page's live table, rendered server-side and pushed.
    | SessionRows
    /// A lifecycle act on one session.
    | Session of SessionId * SessionVerb
    /// The stable way into a session: launch it if stopped, then hand the browser over.
    | OpenSession of SessionId
    /// Whether this deployment's front door reaches the session yet — what `OpenSession`'s
    /// page polls before it goes.
    | SessionReady of SessionId
    /// Declare an MCP server (Plan 17).
    | DeclareMcpServer
    /// Withdraw one.
    | WithdrawMcpServer

/// Why a request is for no Manager route. Two answers, because they are two different
/// things to tell a caller: nothing is there, or something is and the caller misspelled it.
[<RequireQualifiedAccess>]
type ManagerMiss =
    /// The management surface claims nothing there — an unknown path, or a known one reached
    /// with the wrong method. The caller's next question is whoever else serves this origin.
    | Unclaimed
    /// A session path whose id could not be a session id. The shape is the Manager's, so the
    /// Manager answers — and answers 400 with the reason, not 404: "no such session" is what
    /// 404 says, and a person who mistyped an id can only fix it if told what a session id is.
    | MalformedSessionId of raw: string * reason: string

module ManagerRoute =

    let private verbSegment (verb: SessionVerb) =
        match verb with
        | SessionVerb.Launch -> "launch"
        | SessionVerb.Stop -> "stop"
        | SessionVerb.Archive -> "archive"
        | SessionVerb.Unarchive -> "unarchive"

    /// The file `file` of `build`, as the page should link it.
    let asset (AssetBuild digest) (file: AssetFile) : ManagerRoute =
        ManagerRoute.Asset (digest, AssetFile.path file)

    /// A route as the root-anchored path the Manager serves it at: always begins with `/`,
    /// which is right from every page the Manager serves (see the type's remarks). The two
    /// static shapes render through `SessionRoute`, so the Manager and a session cannot
    /// disagree about where a build's files sit.
    let path (route: ManagerRoute) : string =
        match route with
        | ManagerRoute.Home -> "/"
        | ManagerRoute.Asset (build, file) -> RelativeUrl.under "" (SessionRoute.relative (Asset (build, file)))
        | ManagerRoute.Icon -> RelativeUrl.under "" (SessionRoute.relative Icon)
        | ManagerRoute.CreateSession -> "/sessions"
        | ManagerRoute.SessionRegistry -> "/sessions/stream"
        | ManagerRoute.SessionRows -> "/sessions/rows"
        | ManagerRoute.Session (id, verb) -> sprintf "/sessions/%s/%s" (SessionId.value id) (verbSegment verb)
        | ManagerRoute.OpenSession id -> sprintf "/sessions/%s/open" (SessionId.value id)
        | ManagerRoute.SessionReady id -> sprintf "/sessions/%s/ready" (SessionId.value id)
        | ManagerRoute.DeclareMcpServer -> "/mcp/servers"
        | ManagerRoute.WithdrawMcpServer -> "/mcp/servers/withdraw"

    /// A route as an absolute URL at a Manager's origin — what a session client's reconnect
    /// link and a registry subscriber need. The join lives here, so an origin given with or
    /// without its trailing slash reads the same.
    let at (origin: string) (route: ManagerRoute) : string =
        origin.TrimEnd '/' + path route

    /// The route a request is for, or why it is for none (`ManagerMiss`).
    let parse (method: string) (path: string) : Result<ManagerRoute, ManagerMiss> =
        let session (id: string) (make: SessionId -> ManagerRoute) =
            match SessionId.create id with
            | Ok sessionId -> Ok (make sessionId)
            | Error reason -> Error (ManagerMiss.MalformedSessionId (id, reason))
        match method, path.Trim('/').Split '/' |> Array.toList with
        | "GET", [ "" ] -> Ok ManagerRoute.Home
        | "POST", [ "sessions" ] -> Ok ManagerRoute.CreateSession
        | "GET", [ "sessions"; "stream" ] -> Ok ManagerRoute.SessionRegistry
        | "GET", [ "sessions"; "rows" ] -> Ok ManagerRoute.SessionRows
        | "POST", [ "sessions"; id; "launch" ] -> session id (fun s -> ManagerRoute.Session (s, SessionVerb.Launch))
        | "POST", [ "sessions"; id; "stop" ] -> session id (fun s -> ManagerRoute.Session (s, SessionVerb.Stop))
        | "POST", [ "sessions"; id; "archive" ] -> session id (fun s -> ManagerRoute.Session (s, SessionVerb.Archive))
        | "POST", [ "sessions"; id; "unarchive" ] -> session id (fun s -> ManagerRoute.Session (s, SessionVerb.Unarchive))
        | "GET", [ "sessions"; id; "open" ] -> session id ManagerRoute.OpenSession
        | "GET", [ "sessions"; id; "ready" ] -> session id ManagerRoute.SessionReady
        | "POST", [ "mcp"; "servers" ] -> Ok ManagerRoute.DeclareMcpServer
        | "POST", [ "mcp"; "servers"; "withdraw" ] -> Ok ManagerRoute.WithdrawMcpServer
        | "GET", _ ->
            // The static shapes, recognised by the same parse a session runs so the two
            // servers agree about them by construction — and ONLY those two: a session's
            // other routes are not the Manager's.
            match SessionRoute.parse method path with
            | Some (Asset (build, file)) -> Ok (ManagerRoute.Asset (build, file))
            | Some Icon -> Ok ManagerRoute.Icon
            | _ -> Error ManagerMiss.Unclaimed
        | _ -> Error ManagerMiss.Unclaimed
