module Yession.Probe

// A headless peer with a command line: join a real session, say one thing, and report what
// the agent did about it.
//
// Everything here is the shipped path. The peer is the one the browser uses — the same
// WebRTC channel, the same Yjs document, the same draft published by the same verb — because
// a probe that drove a second, simpler client would be measuring the second client. There is
// no HTTP route that accepts a message; a message IS a write to the shared document, so this
// is not a convenience wrapper over an API, it is the only door.
//
// What it decides is what to SAY and when to stop watching. Everything else it reads back
// out of the peer's own view of the log.

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

// A real function, so the response is evaluated once — the macro read `$0` twice and would
// have re-evaluated whatever expression the caller passed.
[<Emit("(function (response) { return response.headers.getSetCookie ? response.headers.getSetCookie() : [] })($0)")>]
let private setCookies (response: obj) : string array = jsNative

[<Emit("$0.headers.get('location')")>]
let private locationOf (response: obj) : string = jsNative

[<Emit("new URL($1, $0).toString()")>]
let private resolveUrl (baseUrl: string) (relative: string) : string = jsNative

[<Emit("(function (cookie, redirect) { return { redirect: redirect, headers: cookie === '' ? {} : { cookie: cookie } } })($0, $1)")>]
let private request (cookie: string) (redirect: string) : obj = jsNative

[<Emit("(function (cookie, body) { return { method: 'POST', redirect: 'manual', headers: cookie === '' ? {} : { cookie: cookie }, body: new URLSearchParams(body) } })($0, $1)")>]
let private post (cookie: string) (body: obj) : obj = jsNative

[<Emit("({ id: $0 })")>]
let private idBody (id: string) : obj = jsNative

[<Emit("(function (text) { try { return JSON.parse(text).peerToken } catch { return '' } })($0)")>]
let private peerTokenIn (json: string) : string = jsNative

[<Emit("(function (text, pattern) { const m = text.match(new RegExp(pattern)); return m ? m[0] : '' })($0, $1)")>]
let private firstMatch (text: string) (pattern: string) : string = jsNative

[<Emit("new Promise(resolve => setTimeout(resolve, $0))")>]
let private delay (ms: int) : JS.Promise<unit> = jsNative

[<Emit("console.log($0)")>]
let private say (line: string) : unit = jsNative

// The peer connection keeps Node's event loop alive after the watch is over, so a probe that
// merely returned would sit there until killed — and did, three of them, until this.
[<Emit("process.exit($0)")>]
let private exitWith (code: int) : unit = jsNative

// --- what it accepts ----------------------------------------------------------------------

let private managerOption = Cli.value "manager" "url" "the Manager to join a session on"
let private sayOption = Cli.value "say" "text" "what to send, once connected"
let private sessionOption = Cli.value "session" "id" "the session to use; a fresh one is made when absent"
let private stopOption = Cli.value "stop-after" "n" "give up after this many tool calls (default 40)"
let private tokenOption =
    Cli.value "peer-token" "token" "join with this instead of signing in — for a front door a CLI cannot bounce through"

let spec =
    Cli.spec
        "yession-probe"
        [ managerOption; sayOption; sessionOption; stopOption; tokenOption ]

// --- the browser's own three steps ----------------------------------------------------------

/// Cookies, kept the way a browser keeps them, because signing in is a redirect chain and
/// each hop has to carry what the last one set.
let private jar = System.Collections.Generic.Dictionary<string, string> ()

let private cookie () =
    jar |> Seq.map (fun kv -> sprintf "%s=%s" kv.Key kv.Value) |> String.concat "; "

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
        let args = Cli.parseOrExit spec (Yession.Host.Version.current)
        // Answered where they are absent rather than defaulted to a blank that reads like a
        // value: a probe with no Manager has nothing to do, and saying so beats connecting to
        // the empty string.
        let manager, message =
            match Cli.valueOf managerOption args, Cli.valueOf sayOption args with
            | Some manager, Some message -> manager.TrimEnd '/', message
            | None, _ -> Cli.abort "yession-probe needs --manager: which Manager to join a session on"
            | _, None -> Cli.abort "yession-probe needs --say: what to send once it is connected"
        let stopAfter =
            Cli.valueOf stopOption args
            |> Option.bind (fun raw -> match System.Int32.TryParse raw with | true, n -> Some n | _ -> None)
            |> Option.defaultValue 40
        // A session id is Crockford base32. Minted here when none was named, so two runs of
        // the same probe do not land in one another's session.
        let id =
            match Cli.valueOf sessionOption args with
            | Some named -> named
            | None -> SessionId.value (SessionId.mint ())

        // Retried, because a Manager restarted under a promotion is the ordinary case for
        // anything measuring one build against the next, and the address is the first thing
        // that proves it is back.
        let mutable session = ""
        let mutable attempts = 0
        while session = "" && attempts < 30 do
            attempts <- attempts + 1
            try
                let! _ = fetch (sprintf "%s/sessions" manager) (post (cookie ()) (idBody id))
                let! opened = get (sprintf "%s/sessions/%s/open" manager id) 10
                session <- firstMatch opened "https?://[^\"'<>\\s]*/s/[0-9A-Z]+/"
            with _ -> ()
            if session = "" then do! delay 2000
        if session = "" then failwith "the Manager never answered with a session address"

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
            token <- match peerTokenIn me with | "" -> None | minted -> Some minted
            if token.IsNone then do! delay 1000
        let token =
            match token with
            | Some token -> token
            | None -> failwith "the session never became reachable"

        say (sprintf "# %s on %s" id session)

        let! client = connectClient (sprintf "%ssignal" session) token "probe" "probe" |> Async.StartAsPromise
        do! compose client client.Hello.PeerId message |> Async.StartAsPromise
        client.Connection.SendDraft client.Hello.PeerId

        // Everything after this is read off the peer's own view of the log — no filesystem,
        // no second fetch. That is what lets this run from anywhere the Manager is reachable.
        let mutable shown = 0
        let mutable calls = 0
        let mutable finished = false
        let mutable ticks = 0
        while not finished && calls < stopAfter && ticks < 800 do
            ticks <- ticks + 1
            do! delay 1500
            let model = client.Runner.Model ()
            let items = TimelineProjection.items model.Conversation model.Timeline |> List.toArray
            // The cursor stops at the first item that cannot be said YET, rather than stepping
            // over it. An agent's message arrives Streaming and only later completes, so a
            // cursor that counted it as seen would consume it in silence and then wait for an
            // ending that had already gone past — which is what this did, and why a turn that
            // answered in four seconds ran until the tick ceiling.
            let mutable at = shown
            let mutable waiting = false
            while not waiting && at < items.Length do
                match lineFor model.Timeline items.[at] with
                | Some line ->
                    say line
                    let counted =
                        match items.[at] with
                        | TimelineToolUse _ -> calls <- calls + 1
                        | TimelineMessage said when said.Author = ActorRef.Agent -> finished <- true
                        | _ -> ()
                    ignore counted
                    at <- at + 1
                | None -> waiting <- true
            shown <- at
        say (sprintf "# %d tool calls" calls)
        // Stopped rather than left running: a probe's session has nobody coming back to it.
        let! _ = fetch (sprintf "%s/sessions/%s/stop" manager id) (post (cookie ()) (idBody id))
        exitWith 0
    }

run () |> Promise.start
