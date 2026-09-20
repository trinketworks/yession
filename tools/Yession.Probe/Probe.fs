module Yession.Probe

// A headless peer with a command line: join a real session, say one or more things, and
// report what the agent does about each — watching a whole TURN at a time.
//
// Everything here is the shipped path. The peer is the one the browser uses — the same
// WebRTC channel, the same Yjs document, the same draft published by the same verb — because
// a probe that drove a second, simpler client would be measuring the second client. There is
// no HTTP route that accepts a message; a message IS a write to the shared document, so this
// is not a convenience wrapper over an API, it is the only door.
//
// What it decides is what to SAY and when a turn is DONE — and done is `ActiveTurn` back to
// `None`, the agent finished working and waiting for input again, NOT the first message it
// completes. An agent narrates as it works ("on it…", "added the repo…"), and each of those
// is a mid-turn message the next one closes; a probe that stopped at the first stopped
// mid-turn, and then took the session down before the work it had asked for ran. So the loop
// waits for the turn. Everything else it reads back out of the peer's own view of the log.
//
// Give `--say` more than once to hold a conversation: each message goes after the last turn
// settled. Only a session this probe MINTED is stopped on the way out — a named one belongs
// to whoever lent it.

open Fable.Core
open Yession.Domain
open Yession.Domain.Chat
open Yession.Domain.Tools
open Yession.App
open Yession.Peer

[<Emit("fetch($0, $1)")>]
let private fetch (url: string) (init: obj) : JS.Promise<obj> = jsNative

[<Emit("$0.status")>]
let private statusOf (response: obj) : int = jsNative

[<Emit("$0.text()")>]
let private textOf (response: obj) : JS.Promise<string> = jsNative

[<Emit("!!$0.headers.getSetCookie")>]
let private hasGetSetCookie (response: obj) : bool = jsNative

[<Emit("$0.headers.getSetCookie()")>]
let private getSetCookie (response: obj) : string array = jsNative

/// Every `set-cookie` the response carried, kept apart. `Headers.getSetCookie` is the only
/// thing that can tell several of them apart, and a runtime old enough not to have it has
/// nothing to offer instead — so the jar stays empty rather than half-filled from a joined
/// header. A real function, so the response is read once: the macro this replaced named
/// `$0` twice and would have re-evaluated whatever expression the caller passed.
let private setCookies (response: obj) : string array =
    if hasGetSetCookie response then getSetCookie response else [||]

[<Emit("$0.headers.get('location')")>]
let private locationOf (response: obj) : string = jsNative

[<Emit("new URL($1, $0).toString()")>]
let private resolveUrl (baseUrl: string) (relative: string) : string = jsNative

[<Emit("({ redirect: $1, headers: $0 })")>]
let private getInit (headers: obj) (redirect: string) : obj = jsNative

[<Emit("({ method: 'POST', redirect: 'manual', headers: $0, body: new URLSearchParams($1) })")>]
let private postInit (headers: obj) (body: obj) : obj = jsNative

/// The headers a hop carries. WHICH headers there are is the decision here: a jar with
/// nothing in it sends no `cookie:` at all, the way a browser's first request does, rather
/// than a header whose value is blank.
let private headersFor (cookie: string option) : obj =
    cookie |> Option.map (fun jar -> "cookie", box jar) |> Option.toList |> JsInterop.createObj

let private request (cookie: string option) (redirect: string) : obj = getInit (headersFor cookie) redirect

let private post (cookie: string option) (body: obj) : obj = postInit (headersFor cookie) body

[<Emit("({ id: $0 })")>]
let private idBody (id: string) : obj = jsNative

/// `||` rather than a plain field read: a `peerToken` spelled `null` or left empty is no
/// token, which is the same nothing as a field that is not there.
[<Emit("($0.peerToken || undefined)")>]
let private peerTokenField (parsed: obj) : string option = jsNative

let private parsedPeerToken (json: string) : string option =
    peerTokenField (JS.JSON.parse json)

/// The token in a `/me` body, if there is one. That route answers JSON once the session is
/// reachable and an error page while it is not, so a body that will not parse is a session
/// still coming up rather than a failure — and so is one that parsed with no token in it.
/// Absent all the way to the caller: the retry loop is what decides what no token means.
let private peerTokenIn (json: string) : string option =
    try parsedPeerToken json with _ -> None

[<Emit("$0.match(new RegExp($1))")>]
let private matchesOf (text: string) (pattern: string) : string array option = jsNative

/// The first thing in `text` the pattern matches. `String.match` answers null when nothing
/// did, which is an absence rather than an empty match, and it stays one all the way to the
/// caller for the same reason `peerTokenIn` does.
let private firstMatch (text: string) (pattern: string) : string option =
    matchesOf text pattern |> Option.bind Array.tryHead

[<Emit("new Promise(resolve => setTimeout(resolve, $0))")>]
let private delay (ms: int) : JS.Promise<unit> = jsNative

let private say (line: string) : unit = JS.console.log line

// The peer connection keeps Node's event loop alive after the watch is over, so a probe that
// merely returned would sit there until killed — and did, three of them, until this.
let private exitWith (code: int) : unit = Node.Api.``process``.exit code

// Wall-clock, for deadlines a caller sets in seconds rather than in polling ticks.
[<Emit("Date.now()")>]
let private now () : float = jsNative

// --- what it accepts ----------------------------------------------------------------------

let private managerOption = Cli.value "manager" "url" "the Manager to join a session on"
let private sayOption = Cli.values "say" "text" "what to send; repeat to hold a conversation, one message per turn"
let private sessionOption = Cli.value "session" "id" "the session to join; a fresh one is minted (and stopped on exit) when absent"
let private timeoutOption = Cli.value "timeout" "seconds" "how long to wait for each turn to finish (default 900)"
let private stopOption = Cli.value "stop-after" "n" "stop the probe after this many tool calls (default: no cap)"
let private keepOption = Cli.flag "keep" None "leave the session running on exit, even one this probe minted"
let private tokenOption =
    Cli.value "peer-token" "token" "join with this instead of signing in — for a front door a CLI cannot bounce through"

let spec =
    Cli.spec "yession-probe"
    |> Cli.accepts managerOption
    |> Cli.accepts sayOption
    |> Cli.accepts sessionOption
    |> Cli.accepts timeoutOption
    |> Cli.accepts stopOption
    |> Cli.accepts keepOption
    |> Cli.accepts tokenOption

// --- the browser's own three steps ----------------------------------------------------------

/// Cookies, kept the way a browser keeps them, because signing in is a redirect chain and
/// each hop has to carry what the last one set.
let private jar = System.Collections.Generic.Dictionary<string, string> ()

/// Nothing rather than a blank line when the jar is empty: before anything has been set
/// there is no cookie header to send, which is a different thing from sending an empty one.
let private cookie () : string option =
    match jar.Count with
    | 0 -> None
    | _ -> jar |> Seq.map (fun kv -> sprintf "%s=%s" kv.Key kv.Value) |> String.concat "; " |> Some

let private keep (response: obj) =
    for raw in setCookies response do
        let pair = raw.Split ';' |> Array.head
        match pair.IndexOf '=' with
        | -1 -> ()
        | at -> jar.[pair.Substring (0, at)] <- pair.Substring (at + 1)

/// Follow redirects by hand, so the jar rides every hop. `fetch` on its own would follow them
/// and drop the cookies the bounce depends on.
let rec private get (url: string) (hops: int) : JS.Promise<string> =
    promise {
        let! response = fetch url (request (cookie ()) "manual")
        keep response
        let status = statusOf response
        if status >= 300 && status < 400 && hops > 0 then
            return! get (resolveUrl url (locationOf response)) (hops - 1)
        else
            return! textOf response
    }

// --- the trace ------------------------------------------------------------------------------

/// One timeline item, as a line. This is the probe's actual product: what the agent did, in
/// the order it did it, with the reasoning between the acts it explains — which is why the
/// items come from `TimelineProjection.items` rather than from `rows`, the drawn view that
/// deliberately hides thinking.
let private lineFor (timeline: TimelineProjection) (item: TimelineItem) : string option =
    match item with
    | TimelineMessage said ->
        match said.Status with
        | Complete -> Some (sprintf "%-9s %s" (ActorRef.token said.Author) (ConversationItem.said said))
        | _ -> None
    | TimelineToolUse (_, id) ->
        TimelineProjection.toolUse id timeline
        |> Option.map (fun call ->
            // Mapped before it is defaulted, so the empty string is the render of "no
            // arguments" and never stands in for arguments nobody recorded.
            let args =
                call.Arguments
                |> Option.map (fun raw -> if raw.Length > 160 then raw.Substring (0, 160) + "…" else raw)
                |> Option.defaultValue ""
            sprintf "%-9s %s %s" "call" call.Name args)
    | TimelineThought (_, thought) -> Some (sprintf "%-9s %s" "thought" (thought.Thought.Trim ()))
    | TimelineBlock (_, terminal, block) ->
        Some (sprintf "%-9s %s in %s" "ran" (BlockId.value block) (TerminalId.value terminal))
    | TimelineStretch _ -> None

// --- run ------------------------------------------------------------------------------------

let private run () =
    promise {
        let args = Yession.Host.Interop.parseOrExit spec (Yession.Host.Version.current)
        // Answered where they are absent rather than defaulted to a blank that reads like a
        // value: a probe with no Manager has nothing to do, and saying so beats connecting to
        // the empty string. `--say` is repeatable, so this is a list — an empty one is the
        // same nothing as a missing Manager.
        let manager, says =
            match Cli.valueOf managerOption args, Cli.valueOf sayOption args with
            | Some manager, (_ :: _ as says) -> manager.TrimEnd '/', says
            | None, _ -> Yession.Host.Interop.abort "yession-probe needs --manager: which Manager to join a session on"
            | _, [] -> Yession.Host.Interop.abort "yession-probe needs --say: what to send once it is connected"
        // Real seconds, not the tick count this once hid behind: a turn that builds a package
        // or runs a suite is minutes of few or no tool calls, and a ceiling counted in ticks
        // gave up on exactly the work worth watching.
        let turnTimeoutMs =
            Cli.valueOf timeoutOption args
            |> Option.bind (fun raw -> match System.Int32.TryParse raw with | true, n when n > 0 -> Some n | _ -> None)
            |> Option.defaultValue 900
            |> fun seconds -> float seconds * 1000.0
        // A cap on tool calls, off unless asked for — the safety valve for a run left alone,
        // no longer the thing that decides a turn is over.
        let stopAfter =
            Cli.valueOf stopOption args
            |> Option.bind (fun raw -> match System.Int32.TryParse raw with | true, n when n > 0 -> Some n | _ -> None)
        // A named session is one the caller owns and the probe is only visiting; an absent one
        // is minted here and is the probe's to clean up. That difference IS the stop policy at
        // the end, and `--keep` holds even a minted one open — for a run whose point was to
        // leave a live session to look at.
        let minted = (Cli.valueOf sessionOption args).IsNone
        let keep = Cli.isSet keepOption args
        // A session id is Crockford base32. Minted here when none was named, so two runs of
        // the same probe do not land in one another's session; a named one is checked here,
        // because the Manager answers a malformed id with 404 and no explanation.
        let sessionId =
            match Cli.valueOf sessionOption args with
            | Some named ->
                match SessionId.create named with
                | Ok id -> id
                | Error reason -> Yession.Host.Interop.abort (sprintf "--session %s: %s" named reason)
            | None -> SessionId.mint ()
        let id = SessionId.value sessionId

        // Retried, because a Manager restarted under a promotion is the ordinary case for
        // anything measuring one build against the next, and the address is the first thing
        // that proves it is back.
        let mutable session = None
        let mutable attempts = 0
        while session.IsNone && attempts < 30 do
            attempts <- attempts + 1
            try
                let! _ = fetch (ManagerRoute.at manager ManagerRoute.CreateSession) (post (cookie ()) (idBody id))
                let! opened = get (ManagerRoute.at manager (ManagerRoute.OpenSession sessionId)) 10
                session <- firstMatch opened "https?://[^\"'<>\\s]*/s/[0-9A-Z]+/"
            with _ -> ()
            if session.IsNone then do! delay 2000
        let session =
            match session with
            | Some address -> address
            | None -> failwith "the Manager never answered with a session address"

        // The front door routes a session only once it has registered its port, so the
        // sign-in bounce is retried rather than assumed.
        // An option all the way through: a token given on the command line is one the caller
        // already holds, and absence is what makes this sign in rather than a blank to compare.
        let mutable token = Cli.valueOf tokenOption args
        let mutable signIns = 0
        while token.IsNone && signIns < 40 do
            signIns <- signIns + 1
            let! _ = get (sprintf "%slogin" session) 10
            let! me = get (sprintf "%sme" session) 10
            token <- peerTokenIn me
            if token.IsNone then do! delay 1000
        let token =
            match token with
            | Some token -> token
            | None -> failwith "the session never became reachable"

        say (sprintf "# %s on %s" id session)

        let! client = connectClient (sprintf "%ssignal" session) token "probe" "probe" |> Async.StartAsPromise

        // The cursor and the counter live across the whole conversation, so several messages
        // read as one ordered trace and one tool-call cap counts them all. Everything is read
        // off the peer's own view of the log — no filesystem, no second fetch — which is what
        // lets this run from anywhere the Manager is reachable.
        let mutable shown = 0
        let mutable calls = 0
        let mutable stopped = false
        let mutable failed = false
        // Long enough that a cold session — its process and work sandbox still starting — has
        // begun a turn; short enough that a message nothing answers fails in minutes, not the
        // whole timeout. Never longer than the timeout itself, for a caller who set a small one.
        let startGraceMs = min 180_000.0 turnTimeoutMs

        // Let the shared log sync, then start the trace at its end: a session joined by name
        // arrives with its past turns already on the log, and those are neither this run's to
        // replay nor — the trap the first cut fell into — a false sign that THIS message's turn
        // is already under way. A freshly minted session has none, so this costs one poll.
        do! delay 1500
        let existing = client.Runner.Model ()
        shown <- TimelineProjection.items existing.Conversation existing.Timeline |> List.length

        let mutable remaining = says
        while not stopped && not (List.isEmpty remaining) do
            let message = List.head remaining
            remaining <- List.tail remaining
            // The turn already running when we speak, if any — so a DIFFERENT id afterwards is
            // how we tell our own turn from it. An idle session reads `None` here, and any
            // `Some` that follows is ours.
            let turnAtSend = (client.Runner.Model ()).Agent.ActiveTurn
            do! compose client client.Hello.PeerId message |> Async.StartAsPromise
            client.Connection.SendDraft client.Hello.PeerId
            let sentAt = now ()
            // A turn is watched by its state, not its messages: `ActiveTurn` rising to a new id
            // is ours starting, and its fall back to `None` is that turn ending. A completed
            // message is NOT the signal — most complete mid-turn, closed by the next message,
            // and a probe that stopped at the first stopped the agent in the middle of the work.
            let mutable sawOurTurn = false
            let mutable settled = false
            while not settled do
                do! delay 1500
                let model = client.Runner.Model ()
                let items = TimelineProjection.items model.Conversation model.Timeline |> List.toArray
                // The cursor stops at the first item that cannot be said YET rather than
                // stepping over it. An agent's message arrives Streaming and only later
                // completes, so a cursor that counted it as seen would consume it in silence
                // and then wait for an ending that had already gone past.
                let mutable at = shown
                let mutable waiting = false
                while not waiting && at < items.Length do
                    match lineFor model.Timeline items.[at] with
                    | Some line ->
                        say line
                        let counted =
                            match items.[at] with
                            | TimelineToolUse _ -> calls <- calls + 1
                            | _ -> ()
                        ignore counted
                        at <- at + 1
                    | None -> waiting <- true
                shown <- at
                let active = model.Agent.ActiveTurn
                if Option.isSome active && active <> turnAtSend then sawOurTurn <- true
                let elapsed = now () - sentAt
                match stopAfter with
                | Some cap when calls >= cap ->
                    say (sprintf "# stop-after %d tool calls reached" cap)
                    stopped <- true
                    settled <- true
                | _ ->
                    if sawOurTurn && Option.isNone active then
                        settled <- true
                    elif not sawOurTurn && elapsed > startGraceMs then
                        say "# the message reached the session but raised no turn"
                        failed <- true
                        stopped <- true
                        settled <- true
                    elif elapsed > turnTimeoutMs then
                        say (sprintf "# gave up waiting %gs for the turn to finish" (turnTimeoutMs / 1000.0))
                        failed <- true
                        stopped <- true
                        settled <- true
        say (sprintf "# %d tool calls" calls)
        // Only a session this probe MINTED is stopped here: a named one was borrowed and its
        // owner may be coming back to it, and `--keep` holds even a minted one open.
        let! _ =
            if minted && not keep then
                fetch (ManagerRoute.at manager (ManagerRoute.Session (sessionId, SessionVerb.Stop))) (post (cookie ()) (idBody id))
            else
                promise { return box () }
        // Non-zero only when a turn did not finish in time, so a caller can tell "the agent
        // answered" from "I stopped waiting"; a stop-after cap or a clean finish is a 0.
        exitWith (if failed then 1 else 0)
    }

run () |> Promise.start
