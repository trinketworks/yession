module Yession.Browser.Main

// The browser client entry: the same Elmish/Ylmish program as everywhere else, wired to
// the Session Process over a *native* WebRTC data channel (the Node tests use
// libdatachannel; the protocol and signalling are identical). The shell is rendered by
// Fable.Lit — `View.view` into `#app` on every model change. Lit diffs the DOM, so focus,
// caret, and the collaborative textareas survive re-renders with no manual bookkeeping;
// interactive controls are inline template handlers, not attribute delegation.

open System
open Elmish
open Fable.Core
open Fable.Core.JsInterop
open Fable.BrowserExtras
open Yjs
open Yession.Domain
open Yession.Domain.Link
open Yession.Domain.Repos
open Yession.Domain.Terminals
open Yession.Domain.Collab
open Fable.ProseMirror
open Yession.App
open Lit

#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

// --- Native WebRTC (non-trickle, mirroring app/WebRtc.fs) -----------------------------

/// The transport one handshake settles with: the channel frames ride on, and the peer
/// connection that carries it.
///
/// The connection is kept because this client has to be able to READ it. The handshake used to
/// settle with the data channel ALONE, so nothing could observe either state machine, nothing
/// could close a dead connection, and the only way a client learned its transport had died was
/// `dc.onclose` — an event a half-open channel never fires.
type private Transport =
    { Channel : Browser.Types.RTCDataChannel
      Peer : Browser.Types.RTCPeerConnection }

/// What one attempt at the handshake settled as. Three outcomes, three remedies: a transport to
/// use, a session that answered with a refusal, and a session that did not answer at all.
type private Handshake =
    | Opened of Transport
    | Refused of detail: string
    | TimedOut

/// How long a whole handshake gets before it counts as "the session did not answer". Long
/// enough for ICE gathering on a slow machine, short enough that a dead session is reported
/// rather than waited on.
let private channelOpenTimeoutMs = 10000

/// Both of the peer connection's state machines, read as the one signal that matters: this
/// transport is finished. BOTH, because either can reach a terminal state without the other
/// following it there, and a client watching only `connectionState` goes on waiting for a
/// connection whose ICE agent has already given up.
///
/// `disconnected` is deliberately NOT here. It is a maybe, not a verdict, and the honest answer
/// to a maybe already exists: the heartbeat asks, and gets an answer or does not, inside about
/// three seconds. A grace timer here would be a second clock measuring the same doubt.
let private peerFinished (peer: Browser.Types.RTCPeerConnection) : bool =
    peer.connectionState = Browser.Types.RTCPeerConnectionState.Failed
    || peer.connectionState = Browser.Types.RTCPeerConnectionState.Closed
    || peer.iceConnectionState = Browser.Types.RTCIceConnectionState.Failed
    || peer.iceConnectionState = Browser.Types.RTCIceConnectionState.Closed

/// Register a listener and hand back the ONE way to unregister it. The target, the event name
/// and the handler are said once, so a teardown cannot drift from what it undoes — which is the
/// only thing three listeners and three removals can get wrong.
let private listening (target: #Browser.Types.EventTarget) (event: string) (handler: Browser.Types.Event -> unit) : unit -> unit =
    target.addEventListener (event, handler)
    fun () -> target.removeEventListener (event, handler)

/// Opening the data channel, as a TOTAL function: it settles with the transport, or with why it
/// could not be had. It used to resolve only on `dc.onopen`, so a signalling POST that failed
/// — or a session that simply was not there — left this promise pending forever and the shell
/// stuck on "connecting" with nothing to say and nothing to do.
///
/// Non-trickle, the way the Session Process's own side does it: gather first, then send ONE
/// complete SDP, so there are no candidate-timing races and nothing depends on a sleep. Two
/// events say gathering is done (`iceGatheringState` reaching `complete`, and the null
/// candidate) and a browser may fire either — but some browsers and sandboxes fire NEITHER,
/// because mDNS candidate obfuscation can leave gathering stalled indefinitely. So the offer
/// also goes at 1500ms regardless: without it, a handshake waits on an event that is never
/// coming and can only ever time out.
///
/// `timeoutMs` bounds the whole thing (offer, gathering, answer, channel open); it is the
/// difference between "not connected, the session did not answer" and an eternal wait.
let private openDataChannel (signalUrl: string) (timeoutMs: int) : JS.Promise<Handshake> =
    Promise.create (fun resolve _ ->
        let peer = Browser.WebRTC.RTCPeerConnection.Create (Browser.WebRTC.RTCConfiguration.Create [||])
        let channel = peer.createDataChannel "session"
        // Five things try to end this handshake and exactly one of them is heard. The flag and
        // the closing live INSIDE the one function that can end it, rather than beside each
        // caller: the timeout comes due whether or not the channel opened, and closing a live
        // connection because a timer fired is the fault a once-only settle exists to prevent.
        let mutable settled = false
        let settle (outcome: Handshake) =
            if not settled then
                settled <- true
                match outcome with
                | Opened _ -> ()
                | Refused _ | TimedOut -> try peer.close () with _ -> ()
                resolve outcome
        // The offer goes once. `sent` is what makes three triggers for one send idempotent;
        // `settled` is what keeps the 1500ms fallback from posting an offer for a handshake
        // that is already over.
        let mutable sent = false
        let send () =
            if not sent && not settled then
                sent <- true
                promise {
                    match peer.localDescription with
                    | None ->
                        // Nothing gathered yet, and nothing to offer. Only the fallback timer
                        // can arrive here — the two gathering events cannot fire before the
                        // description is local.
                        settle (Refused "no local description to offer")
                    | Some local ->
                        let offer =
                            JS.JSON.stringify (
                                Browser.WebRTC.RTCSessionDescriptionInit.Create (local.``type``, local.sdp))
                        let! reply =
                            Fetch.fetchUnsafe
                                signalUrl
                                [ Fetch.Types.RequestProperties.Method Fetch.Types.HttpMethod.POST
                                  Fetch.requestHeaders [ Fetch.Types.HttpRequestHeaders.ContentType "application/json" ]
                                  Fetch.Types.RequestProperties.Body (U3.Case3 offer) ]
                        if reply.Ok then
                            let! answer = reply.json<Browser.Types.RTCSessionDescriptionInit> ()
                            do! peer.setRemoteDescription answer
                        else
                            settle (Refused (sprintf "signalling refused: %d" reply.Status))
                }
                |> Promise.catchEnd (fun error -> settle (Refused error.Message))
        peer.onicegatheringstatechange <-
            fun _ -> if peer.iceGatheringState = Browser.Types.RTCIceGatheringState.Complete then send ()
        peer.onicecandidate <-
            fun ice ->
                match ice.candidate with
                | None -> send ()
                | Some _ -> ()
        JS.setTimeout send 1500 |> ignore
        JS.setTimeout (fun () -> settle TimedOut) timeoutMs |> ignore
        channel.onopen <- fun _ -> settle (Opened { Channel = channel; Peer = peer })
        // One catch for both steps: a rejected `setLocalDescription` used to fall outside the
        // handler `createOffer` carried, and reached the page as an unhandled rejection with
        // the handshake still pending behind it.
        promise {
            let! offer = peer.createOffer ()
            do! peer.setLocalDescription offer
        }
        |> Promise.catchEnd (fun error -> settle (Refused error.Message)))

/// Look again the moment the page comes back — a phone returning from the background, a
/// network coming back, a tab being switched to. Returns the way to stop looking.
///
/// Not a second mechanism: it asks exactly the question `peerFinished` answers, at the one
/// moment a browser is most likely to have torn the transport down while no script was running
/// to hear about it. That moment is where the reported bug lived. It reads the CHANNEL too: a
/// channel can be closed under a connection that still reports itself connected, which is the
/// half-open case `onclose` never fires for.
///
/// A hidden page is not back yet — `visibilitychange` fires on the way out as well as the way
/// in, and answering while hidden reports a teardown the person cannot see and has not
/// returned to.
let private onResume (transport: Transport) (onFinished: unit -> unit) : unit -> unit =
    let look (_: Browser.Types.Event) =
        if Browser.Dom.document.visibilityState <> "hidden" then
            if peerFinished transport.Peer
               || transport.Channel.readyState <> Browser.Types.RTCDataChannelState.Open then
                onFinished ()
    let stops =
        [ listening Browser.Dom.window "pageshow" look
          listening Browser.Dom.window "online" look
          listening Browser.Dom.document "visibilitychange" look ]
    fun () -> for stop in stops do stop ()

/// The same question, asked by the connection itself whenever either state machine moves — and
/// once up front, because a connection can already be finished by the time anybody subscribes.
let private onPeerFinished (peer: Browser.Types.RTCPeerConnection) (onFinished: unit -> unit) : unit =
    let check (_: Browser.Types.Event) = if peerFinished peer then onFinished ()
    peer.addEventListener ("connectionstatechange", check)
    peer.addEventListener ("iceconnectionstatechange", check)
    if peerFinished peer then onFinished ()

let private frameCodec : Codec<SessionFrame<string>> = Codec.sessionFrame Codec.string

/// Bridge the push-based browser data channel into the pull-based `FrameChannel`, and hold the
/// peer connection that carries it for as long as it lasts.
///
/// The channel ends exactly once, however the news arrives — the data channel closing, either
/// state machine reaching a terminal state, or a resumed page finding the transport already
/// gone. Three triggers, one mechanism: end of stream. Whoever is pumping learns it the way
/// they always did.
///
/// Closing closes the CONNECTION too. It used to close only the channel, which left a peer
/// connection (and its ICE agent) alive behind every reconnect for the life of the page.
let private frameChannel (transport: Transport) : FrameChannel<string> =
    let channel = transport.Channel
    let peer = transport.Peer
    let queue = System.Collections.Generic.Queue<SessionFrame<string> option> ()
    let mutable pending : (SessionFrame<string> option -> unit) option = None
    let mutable closed = false
    let mutable stopLooking : unit -> unit = ignore
    let deliver item =
        match pending with
        | Some cont -> pending <- None; cont item
        | None -> queue.Enqueue item
    let finish () =
        if not closed then
            closed <- true
            stopLooking ()
            deliver None
    channel.onmessage <-
        fun message ->
            match Codec.fromString frameCodec (string message.data) with
            | Ok frame -> deliver (Some frame)
            | Error detail -> JS.console.error ("frame decode failed: " + detail)
    channel.onclose <- fun _ -> finish ()
    onPeerFinished peer finish
    stopLooking <- onResume transport finish
    { Send =
        fun frame ->
            async {
                // A peer can vanish between frames; sending into a channel that is no longer
                // open is a no-op, not a throw.
                if channel.readyState = Browser.Types.RTCDataChannelState.Open then
                    channel.send (U4.Case1 (Codec.toString frameCodec frame))
            }
      Receive =
        fun () ->
            Async.FromContinuations (fun (cont, _, _) ->
                if queue.Count > 0 then cont (queue.Dequeue ())
                elif closed then cont None
                else pending <- Some cont)
      Close =
        fun () ->
            async {
                finish ()
                channel.close ()
                peer.close ()
            } }

/// One attempt at the transport, shaped as the resilience policy consumes it. What settles is
/// a CHANNEL, not the WebRTC objects behind it: the peer connection never leaves this module,
/// which is what lets everything above hold one idea of a transport.
let private connectChannel (signalUrl: string) : Async<Result<FrameChannel<string>, Client.ChannelFault>> =
    async {
        let startedAt = Browser.Performance.performance.now ()
        let! outcome = openDataChannel signalUrl channelOpenTimeoutMs |> Async.AwaitPromise
        // How long the handshake took, said out loud. Open latency is a property this repo has
        // already traded a whole ICE backend to protect (docs/decisions/2026-07-26), and it is
        // invisible from the outside: a slow session and a slow handshake look identical from
        // the shell. Free on success, and the one number worth having when they do not.
        JS.console.debug (
            sprintf
                "yession/link: handshake %s in %dms"
                (match outcome with Opened _ -> "opened" | Refused _ | TimedOut -> "failed")
                (int (Math.Round (Browser.Performance.performance.now () - startedAt))))
        return
            match outcome with
            | Opened transport -> Ok (frameChannel transport)
            | TimedOut -> Error Client.ChannelTimedOut
            | Refused detail -> Error (Client.ChannelUnreachable detail)
    }

// --- DOM shell -------------------------------------------------------------------------

let private appRoot () : Browser.Types.HTMLElement = Browser.Dom.document.getElementById "app"

/// Put text on the system clipboard, and say whether the browser let us. Asynchronous
/// because the write may be a permission prompt, and refusable for reasons the page cannot
/// see coming — an insecure context has no `navigator.clipboard` at all, which is the
/// common one: a session reached over plain HTTP at a LAN address.
///
/// The refusal is written to the console rather than swallowed, because the only symptom
/// it has otherwise is a button that appears to do nothing — the same shape as a broken
/// binding, and nothing on the page tells the two apart.
let private writeClipboard (text: string) (settled: bool -> unit) : unit =
    if not (hasClipboard ()) then
        JS.console.debug "yession/copy: no clipboard in this context"
        settled false
    else
        Async.StartImmediate (
            async {
                match! writeClipboardText text |> Async.AwaitPromise |> Async.Catch with
                | Choice1Of2 () -> settled true
                | Choice2Of2 refusal ->
                    JS.console.debug (sprintf "yession/copy: refused %s" refusal.Message)
                    settled false
            })

/// How long a copy says so for. Long enough to be read as an answer to the press, short
/// enough that the code it stands in front of comes back before anybody needs it again.
let private copiedShownMs = 1500

/// A frame later — which is when the render that had to happen, has, and when a class just
/// written has reached the style flush that acts on it.
let private nextFrame (act: unit -> unit) : unit =
    Browser.Dom.window.requestAnimationFrame (fun _ -> act ()) |> ignore

/// The first control matching, focused. Nothing to do when there is none: every selector here
/// names the control the view mounts OPPOSITE the one that just went, so a miss is a face that
/// has not arrived rather than a state to repair.
let private focusFirst (selector: string) : unit =
    match Browser.Dom.document.querySelector selector with
    | null -> ()
    | control -> (control :?> Browser.Types.HTMLElement).focus ()

/// The shell's layout bits, which live on the ROOT element — outside `#app`, which is what
/// makes them survive every re-render.
let private rootClasses () = Browser.Dom.document.documentElement.classList

/// Whether the stylesheet's own desktop breakpoint matches right now — ASKED, never decided
/// again here. `mediaMatches` carries the reason: a script comparing `innerWidth` to a number
/// of its own is a second definition of the breakpoint, and it disagrees with the first
/// whenever a scrollbar, a zoom or a rounded viewport gets between them.
let private onDesktop () : bool = mediaMatches "(min-width: 768px)"

/// Move focus onto the settings face's counterpart control, TWO frames on.
///
/// Two, because the face that is arriving is `visibility: hidden` until the transition it just
/// started reaches its first style flush, and `focus()` on a hidden element is a no-op — which
/// was measured: one frame left focus on `<body>`.
let private focusSettingsFace (selector: string) : unit =
    nextFrame (fun () -> nextFrame (fun () -> focusFirst selector))

// The sidebar/drawer state is one bit on the root element, outside `#app`, so it survives
// every re-render: default = sidebar visible on desktop, off-canvas on mobile; `nav-alt`
// = the inverse (see Style.sidebar).
//
// Collapsing is a PREFERENCE on desktop, so it is remembered; on mobile the same bit means
// "the drawer is open", which is not a preference and is never stored. The stored value is
// re-applied before first paint by the shell document's one inline script (`Ssr.page`) — here,
// only written.
//
// Focus is moved deliberately: the control that was pressed is the one about to disappear, so
// it hands focus to whichever control replaces it (the header's reopen chevron, or the nav
// head's collapse button). Skipping that strands focus on a hidden element.
let private toggleNav () : unit =
    let classes = rootClasses ()
    let desktop = onDesktop ()
    let alt = not (classes.contains "nav-alt")
    if alt then classes.add "nav-alt" else classes.remove "nav-alt"
    // The nav control always returns the column to its workspace face — a column that
    // reopened on settings would be a surprise, and `settings-open` is what chooses the face.
    classes.remove "settings-open"
    // What that bit says about the column being SHOWN is the one read against the other,
    // because `nav-alt` means the opposite thing on each side of the breakpoint.
    let shown = desktop <> alt
    if desktop then
        // Storage is denied in a private window, and a collapse that cannot be remembered is
        // still a collapse that works.
        try
            Browser.WebStorage.localStorage.setItem ("yession.nav", (if shown then "open" else "collapsed"))
        with _ ->
            ()
    nextFrame (fun () ->
        focusFirst (if shown then "button[data-nav-toggle=\"hide\"]" else "[data-nav-toggle=\"show\"]"))

// Settings is the sidebar column's other FACE (Style.settingsPane), not a drawer over the
// conversation — so opening it has to bring that column on screen, and `nav-alt` means the
// opposite thing on each side of the breakpoint: uncollapse on desktop, slide the drawer in on
// mobile. Focus follows the same rule as the nav toggle.
let private toggleSettings () : unit =
    let classes = rootClasses ()
    let opening = not (classes.contains "settings-open")
    if opening then classes.add "settings-open" else classes.remove "settings-open"
    if opening then
        // Bringing the column on screen is the opposite instruction on each side of the
        // breakpoint.
        if onDesktop () then classes.remove "nav-alt" else classes.add "nav-alt"
    elif not (onDesktop ()) then
        // Closing the face on a phone closes the drawer with it. On a desktop the column
        // stays exactly where it was: what changed is which face it shows, not whether it
        // is there.
        classes.remove "nav-alt"
    focusSettingsFace (if opening then "[data-settings-toggle=\"close\"]" else "[data-settings-toggle=\"open\"]")

// The same move, in one direction only.
//
// A call to action that leads to settings must never TAKE somebody there and back: the
// prompt over the timeline is on screen whenever a credential needs signing in, including
// while the settings face is already open, and a toggle there would shut the very panel it
// is pointing at. The nav pivots stay toggles because a pivot is a two-way control and this
// is not one.
//
// Idempotent by construction rather than by the caller checking first — `settings-open` is
// SET, not flipped, so pressing it twice is pressing it once.
let private revealSettings () : unit =
    let classes = rootClasses ()
    let wasOpen = classes.contains "settings-open"
    classes.add "settings-open"
    // Bring the column on screen: `nav-alt` means the opposite thing on each side of the
    // breakpoint — collapsed on desktop, drawer-open on mobile.
    if onDesktop () then classes.remove "nav-alt" else classes.add "nav-alt"
    // Focus moves only when the face actually ARRIVED. Stealing it from whatever the reader
    // was doing, to a control that was already on screen, would be the prompt reaching into a
    // panel they are already reading.
    if not wasOpen then focusSettingsFace "[data-settings-toggle=\"close\"]"

// The auth probe: `me` answers with a peer token when the browser's cookie (or an
// auth-less session) allows it — total in BOTH axes it can fail on, because the two need
// opposite remedies: `authorized = false` means log in (the shell renavigates), while
// `reachable = false` means the session is not there at all (the shell stays local-first
// on its cached stores and says so). Collapsing them — which a thrown fetch did — turns
// "offline" into "log in", and a login bounce against an unreachable session goes nowhere.
//
// Expressed as an ordinary F# `async` pipeline over `MeProbe.Response` — the SAME codec
// (`Yession.Domain.MeProbe`) the Session Process encodes its answer with — rather than one
// JS `Emit` string that encoded the branching itself. The fetch call itself goes through
// `Fable.Fetch` (https://github.com/fable-compiler/fable-fetch), a typed binding, not a
// hand-rolled Emit; `AbortSignal.timeout` is the one piece it does not cover and comes from
// `Fable.FetchExtras`, which is a binding too — and one the host shares, because that signal
// is a global in Node as well as here. What the answer MEANS is this file's `ProbeOutcome`
// and the match below, both type-checked.
//
// The URL is a PARAMETER: every fetch below takes its URL from `Page.href`, so it stays
// checked against the route table — and resolved against the base this page declared —
// rather than living as a literal only the runtime can see.
//
// A REFUSAL is 401/403 and nothing else. Every other error status — a 502 from the
// operator's proxy standing in front of a session that is gone, a 503 from one still
// starting — is the session not being there, which is the other axis entirely. Reading them
// as "log in" sent a client whose session had stopped off to a login bounce that could only
// fail, and (once the shell was served from a worker) replaced a perfectly good offline
// session with a browser error page.
//
// And an answer that does NOT arrive is the same axis again (`Client.Probe.deadline`): the
// fetch rejects — network failure, or the abort signal firing — which `Async.Catch` below
// turns into data a `match` must cover, rather than an exception a caller must remember to
// catch.

/// The two axes `fetchMe` resolves to. A record of two independent bools (the shape this
/// replaced) let a caller ask whether `reachable = false, authorized = true` — a
/// combination that cannot actually happen; a case per real outcome makes it
/// unrepresentable instead of merely undocumented.
type private ProbeOutcome =
    | ProbeUnreachable of detail: string
    | ProbeUnauthorized
    | ProbeAuthorized of MeProbe.Response

let private fetchMe (url: string) (deadlineMs: float) : Async<ProbeOutcome> =
    async {
        let init =
            [ Fetch.Types.RequestProperties.Cache Fetch.Types.RequestCache.Nostore
              Fetch.Types.RequestProperties.Signal(Fable.FetchExtras.timeoutSignal deadlineMs) ]
        // `fetchUnsafe`, not `fetch`: the plain binding throws on a non-2xx status, which
        // would fold the "refused" and "not there" axes back into one exception to
        // re-inspect. This wants the raw response so it can tell 401/403 (refused) apart
        // from everything else (not there) below.
        let! attempt = Fetch.fetchUnsafe url init |> Async.AwaitPromise |> Async.Catch
        match attempt with
        | Choice2Of2 exn -> return ProbeUnreachable (string exn.Message)
        | Choice1Of2 response when response.Ok ->
            let! body = response.text () |> Async.AwaitPromise
            match MeProbe.ofJson body with
            | Ok me -> return ProbeAuthorized me
            | Error err -> return ProbeUnreachable err
        | Choice1Of2 response when response.Status = 401 || response.Status = 403 -> return ProbeUnauthorized
        | Choice1Of2 response -> return ProbeUnreachable (sprintf "HTTP %d" response.Status)
    }

// `location.replace`, not `assign`: the one navigation this shell performs on its own is the
// sign-in bounce, which leaves THIS page and comes back to it — so it is a renavigation, not
// a place a person can return to. `assign` pushed it, and a new session sat two entries deep:
// Back landed on a shell with no cookie, which bounced forward again, and the Manager the
// session was opened from was a second press away. Replaced, the session is the one entry
// after wherever it was opened from.
//

// --- Client-side doc persistence (Step 20): IndexedDB via y-indexeddb ------------------

/// A `<meta name>`'s content, or None when the tag is absent. A missing tag, a missing
/// attribute and a BLANK one are all None — the last one deliberately, because a meta that
/// names nothing names nothing.
let private metaContent (name: string) : string option =
    Browser.Dom.document.querySelector (sprintf "meta[name=\"%s\"]" name)
    |> Option.ofObj
    |> Option.bind (fun tag -> tag.getAttribute "content" |> Option.ofObj)
    |> Option.filter (String.IsNullOrEmpty >> not)

// The store is keyed by SESSION: the serving Session Process embeds its session id in the
// bootstrap page (a synchronous, pre-connection identity), so two sessions served from one
// address never share a store. The KEY is stable wherever the session is served from; the
// STORE is not. IndexedDB is partitioned by origin and a port is part of one, so a deployment
// addressing sessions as `127.0.0.1:{port}` returns to an empty database after every relaunch
// — which is what `PublicAccess.sessionAddressIsStable` marks on the shell, and why the
// client's local-first copy is qualified there rather than promised.
//
// A page carrying no session meta — or one carrying it blank, which `metaContent` answers None
// for, and which names no session either — falls back to the address it was served from.
let private persistenceKey () : string =
    match metaContent Dom.sessionMetaName with
    | Some session -> "yession/session/" + session
    | None -> "yession/" + Browser.Dom.window.location.host + Browser.Dom.window.location.pathname

// Resolved against the shell's `<base href>`, so a session mounted under a path signals
// to its own prefix rather than the origin root. `document.baseURI` IS that base, or the
// document's own address where the shell declared none — and naming it here rather than
// inside the binding is the point: which base an address resolves against is this client's
// decision about its own document, and `Urls.resolve` resolves against whichever it is told.
let private absolute (relative: string) : string =
    Urls.resolve relative Browser.Dom.document.baseURI

// `location.replace` resolves against the DOCUMENT's URL, not `<base href>` — the one place
// relative resolution does not follow the base — so it is handed an address already resolved
// against `document.baseURI`, once, rather than resolved again at each call site.
let private renavigateTo (url: string) : unit = Browser.Dom.window.location.replace (absolute url)

// Every round trip this client makes below is the same program: ask, and hand back what came
// of it as DATA. It never rejects, because the information a rejection destroys is exactly the
// information the caller needs — whether retrying could help (`Client.HttpFailure`, the
// resilience policy), and whether a sign-in flow has ended or merely had a bad moment
// (`GitHubFlow.ended`).
//
// It was three `[<Emit>]` macros — a chunk GET, a JSON POST, a no-store GET — which is one
// JavaScript program written three times, in three spellings of the same outcome, each with
// its own chance to bury a decision where nothing but the runtime could read it. Written once
// in F# it is the shape `fetchMe` above already has, and the one
// `tests/Yession.Tests/TestHttp.fs` gives the suites.

/// What one round trip answered. `Status` rides beside `Ok` because the 2xx question and WHICH
/// status decide different things: a caller that knows only THAT a request failed cannot tell a
/// refusal from a session it could not reach, and a 401 is the one answer with a button
/// (connect GitHub) rather than a retry. `0` is a fetch that never answered — offline, refused,
/// DNS, TLS.
///
/// `Url` is the address the answer came back FROM, which after a redirect is not the one that
/// was asked for — and it is the one worth keeping, because a range's bounds never move while a
/// cursor's answer does (Plan 20). A request that never answered carries no address, so it
/// carries the empty string.
type private Answered =
    { Ok : bool
      Status : int
      Url : string
      Body : string }

/// A fetch as a TOTAL function: what the far end said, or the reason it said nothing.
///
/// `fetchUnsafe`, not `fetch`: the plain binding throws on a non-2xx status, which would fold a
/// refusal back into the same exception a dead network raises, and the status is the thing
/// every caller here is deciding on (`fetchMe` above carries the rest of why). Reading the body
/// sits INSIDE the caught region with the request, because a body that fails to arrive is the
/// same "no answer" as a request that never did, and there is one case for both. `Message` is
/// what the `String(e.message || e)` in the macros this replaces was reaching for.
let private answered (props: Fetch.Types.RequestProperties list) (url: string) : Async<Answered> =
    async {
        let! arrived =
            async {
                let! response = Fetch.fetchUnsafe url props |> Async.AwaitPromise
                let! body = response.text () |> Async.AwaitPromise
                return response, body
            }
            |> Async.Catch
        match arrived with
        | Choice1Of2 (response, body) ->
            return { Ok = response.Ok; Status = response.Status; Url = response.Url; Body = body }
        | Choice2Of2 exn -> return { Ok = false; Status = 0; Url = ""; Body = exn.Message }
    }

/// The event-chunk GET. A plain one, with no cache directive: a chunk is asked for by the
/// address its answer came back from, and the bytes at that address never move — which is the
/// same property the history store below keeps them on.
let private httpGet : Client.HttpGet =
    fun url ->
        async {
            let! reply = answered [] url
            return
                if reply.Ok then Ok { Url = reply.Url; Body = reply.Body }
                elif reply.Status = 0 then Error (Client.HttpUnreachable reply.Body)
                else Error (Client.HttpStatus reply.Status)
        }

// --- The history store (Plan 20, step 2): the Cache API, not the HTTP cache ---------------
// A cache header can say "reuse this without asking me"; it cannot say "keep this". The HTTP
// cache is a bounded pool shared with every other site, reclaimed under pressure and wiped by
// the browsing-data checkbox people tick casually — and it can be neither enumerated nor asked
// to persist. The Cache API is all three, and it needs no service worker: `window.caches` is
// reachable from the page in any secure context.
//
// Whole `Response` objects, keyed by the address they came back from — which after the
// cursor's redirect is the range, whose bounds never move. That is what makes an answer
// keepable, and the client never has to construct one.

/// What a kept answer is, and where the line it starts on rides. Named here because a writer
/// and the reader below must agree on both, and a header spelled twice is a store the next
/// build cannot read.
let private ndjson = "application/x-ndjson; charset=utf-8"

let private firstSeqHeader = "x-yession-first-seq"

/// Whether this client can keep anything at all: a secure context WITH a cache storage. Both
/// halves are asked defensively in the binding, because a bundle that evaluates where there is
/// no window must be able to ask without the asking being what fails.
let private canKeepHistory () : bool = isSecureContext () && (caches ()).IsSome

// The cache is named for the SESSION, which closes inside the client what a URL-keyed cache
// could not: the zero-config deployment addresses sessions as `127.0.0.1:{port}` and ports are
// recycled, so a shared key would hand one session the previous one's history. Derived from the
// doc store's key rather than spelled again — one rule for what identifies a session's storage.
let private historyCacheName () = persistenceKey () + "/events"

/// The named store. Only ever called behind `canKeepHistory`, which is what says there is a
/// storage to open one in — so a page with none is a case that does not reach here, rather
/// than one answered with a store that cannot keep anything.
let private openCache (name: string) : JS.Promise<Cache> =
    match caches () with
    | Some stores -> stores.openStore name
    | None -> failwith "this page has no cache storage, so there is no store to open"

// `keys()` answers in insertion order, and insertion order is NOT log order: `put` of an
// address already kept deletes the entry and appends the new one, so an answer two tabs both
// fetched moves to the end of the enumeration. The replay orders by what the answers hold
// (`Client.EventFetch.replay`); this is a bag of addresses and promises nothing about their order.
let private cacheKeys (cache: Cache) : JS.Promise<string array> =
    cache.keys () |> Promise.map (Array.map (fun request -> request.url))

/// What the store kept for `url`, or nothing for an address it never held — which is an
/// answer rather than a fault, and is why `cacheMatch` is typed nullable.
let private cacheRead (cache: Cache) (url: string) : JS.Promise<string option> =
    cacheMatch cache url
    |> Promise.bind (fun kept ->
        if isNull kept then Promise.lift None else kept.text () |> Promise.map Some)

// A FRESH Response, never the one that came off the network: a response carrying
// `redirected = true` is a known trap in the Cache API, and re-wrapping also keeps the store
// free of anything about how the bytes were obtained.
let private cacheWrite (cache: Cache) (url: string) (body: string) : JS.Promise<unit> =
    // Swallowed on purpose: a store that refuses a write (out of quota, evicted mid-flight)
    // costs this client a cold open and nothing else, and there is no caller to tell.
    cache.put (url, keptResponse body [ "content-type", ndjson ])
    |> Promise.catch (fun _ -> ())

/// Register the worker that makes a cold open possible with no network (Plan 20).
///
/// Best-effort and deliberately unawaited-for-correctness: a client whose registration fails
/// (an insecure context, a browser that refuses) is exactly today's client — it just cannot
/// open cold. Nothing above this waits on it, and nothing breaks if it never resolves.
/// Returns `unit`, and HOW it gets there is load-bearing rather than stylistic. This was a
/// promise-returning emit whose result was discarded (`|> ignore`), which made the whole call
/// dead code to the compiler: it never reached the bundle at all, so the registration silently
/// did not ship — indistinguishable, from the outside, from a worker that will not take
/// control. `Promise.catchEnd` is what keeps that from coming back: it ends the chain in a
/// statement (`void (p.catch(f))`) over a real method call, rather than in a value nobody
/// reads.
let private registerWorker (url: string) : unit =
    match Browser.Navigator.navigator.serviceWorker with
    // No container at all: an insecure context, or a browser without service workers. An
    // answer rather than a fault, and the same one a refused registration ends at.
    | None -> ()
    // Swallowed on purpose. A refusal arrives as a rejection, and there is no caller to tell:
    // whatever the browser decided, this client carries on and loses only the offline open.
    | Some workers -> workers.register url |> Promise.catchEnd ignore

/// Ask for the store to be kept. A request, not a guarantee — granted for an engaged site on
/// Chrome, essentially only for an installed app on Safari — and best-effort by design: the
/// answer changes nothing this client does, it only changes how long what it kept survives.
///
/// Safari additionally caps script-writable storage at seven days without user interaction, and
/// that reaches the Cache API — so a granted request is not the end of it, and the session
/// nobody has opened in a week is the one this store is most likely to have lost.
let private requestPersistence () : JS.Promise<bool> =
    match PersistentStorage.storage () with
    // No storage manager to ask. Not kept, which is what a refusal says too — and the same
    // thing follows from either, so the two are not worth telling apart here.
    | None -> Promise.lift false
    // Swallowed for the reason the request is made at all: nothing above reads this, so a
    // request the browser rejected rather than decided answers what a refusal answers.
    | Some storage -> storage.persist () |> Promise.catch (fun _ -> false)

/// The history store for this session, or the one that keeps nothing when this context cannot
/// have one. Total either way: a client with no store is exactly today's client, asking the
/// network from its cursor.
let private openHistoryCache () : Async<Client.HistoryCache> =
    async {
        if not (canKeepHistory ()) then return Client.HistoryCache.none
        else
            let! cache = openCache (historyCacheName ()) |> Async.AwaitPromise
            let! _ = requestPersistence () |> Async.AwaitPromise
            return
                { Stored = fun () -> async { let! keys = cacheKeys cache |> Async.AwaitPromise in return List.ofArray keys }
                  Read = fun url -> cacheRead cache url |> Async.AwaitPromise
                  Write = fun url body -> cacheWrite cache url body |> Async.AwaitPromise }
    }

// --- One store per terminal (Plan 22) -----------------------------------------------------
// Same Cache API, one cache per terminal, so a replay can walk one terminal's answers without
// asking whose each entry is. The terminal's id is in the cache's NAME, which is what makes
// that question already answered when the walk starts — and what keeps a hole in one
// terminal's history from stopping another's.

let private transcriptCachePrefix () = persistenceKey () + "/terminals/"

let private transcriptCacheName (terminal: TerminalId) = transcriptCachePrefix () + TerminalId.value terminal

/// Every store this page holds. Behind `canKeepHistory` like `openCache`, for the same
/// reason: a page with no storage has no names to walk rather than an empty walk.
let private cacheNames () : JS.Promise<string array> =
    match caches () with
    | Some stores -> stores.names ()
    | None -> failwith "this page has no cache storage, so there are no stores to name"

// The line an answer starts on, kept BESIDE the bytes rather than parsed back out of the
// address: a transcript line cannot carry its own index, and the address is the one thing this
// client is never allowed to read meaning out of. It rides a header on the stored `Response`,
// which the Cache API round-trips for nothing.
let private transcriptWrite (cache: Cache) (url: string) (firstSeq: int) (body: string) : JS.Promise<unit> =
    // Swallowed for the reason `cacheWrite` gives: a refused write costs a cold open.
    cache.put (url, keptResponse body [ "content-type", ndjson; firstSeqHeader, string firstSeq ])
    |> Promise.catch (fun _ -> ())

/// One cached transcript window: the sequence its first line carries, and the lines
/// themselves.
///
/// Nothing for an entry that is gone, and nothing for one written without the header — which
/// no build that shipped this ever wrote, but a store outlives the build that filled it, and
/// lines whose first sequence is unknown cannot be folded into anything. A header that is
/// present and not a number is that same store, read by a build that cannot understand what
/// wrote it.
let private transcriptRead (cache: Cache) (url: string) : Async<(int * string) option> =
    async {
        let! kept = cacheMatch cache url |> Async.AwaitPromise
        if isNullOrUndefined kept then
            return None
        else
            let first = cachedHeader kept firstSeqHeader
            if isNullOrUndefined first then
                return None
            else
                match Int32.TryParse first with
                | false, _ -> return None
                | true, firstSeq ->
                    let! body = kept.text () |> Async.AwaitPromise
                    return Some (firstSeq, body)
    }

/// Every terminal's store for this session, or the one that keeps nothing.
let private openTranscriptCaches () : Async<Client.TranscriptCaches> =
    async {
        if not (canKeepHistory ()) then return Client.TranscriptCaches.none
        else
            return
                { For =
                    fun terminal ->
                        async {
                            // Opening is idempotent and cheap, and doing it per call is what
                            // keeps this a lookup rather than a registry something has to
                            // remember to populate before a terminal is first written to.
                            let! cache = openCache (transcriptCacheName terminal) |> Async.AwaitPromise
                            return
                                { Stored =
                                    fun () ->
                                        async {
                                            let! keys = cacheKeys cache |> Async.AwaitPromise
                                            return List.ofArray keys
                                        }
                                  Read = fun url -> transcriptRead cache url
                                  Write =
                                    fun url first body -> transcriptWrite cache url first body |> Async.AwaitPromise }
                        }
                  Kept =
                    fun () ->
                        async {
                            let! names = cacheNames () |> Async.AwaitPromise
                            let prefix = transcriptCachePrefix ()
                            return
                                names
                                |> Array.filter (fun name -> name.StartsWith prefix)
                                |> Array.map (fun name -> name.Substring prefix.Length)
                                // An id the store held but this build cannot parse is a
                                // terminal this client cannot fold records for anyway.
                                |> Array.choose (fun id ->
                                    match TerminalId.create id with
                                    | Ok terminal -> Some terminal
                                    | Error _ -> None)
                                |> List.ofArray
                        } }
    }

// --- Waiting to try again (Plan 20, step 3) ----------------------------------------------
// ONE wait, poked by two triggers. "Keep trying", "the network came back" and "someone pressed
// retry" are three ways to want the same thing, and building them as three schedules would put
// three of them in a race. So: the lifecycle decides WHETHER and HOW LONG to wait, and these
// only ever cut a wait short.
//
// `ms < 0` is the park a refused peer gets — no timer at all, because no amount of waiting
// fixes a token. It ends when the network returns or a person asks, which are the two things
// that can.

/// Cut the current wait short, if one is running. Replaced each time a wait begins.
let mutable private pokeRetry : unit -> unit = ignore

/// One wait, ended by whichever comes first — the timer, the network returning, or the poke
/// the caller is handed. `ms < 0` is the park a refused peer gets: no timer at all.
///
/// Settling once is structural rather than a flag each path remembers: the clean-up IS the
/// token. Whoever reaches the cell takes what is in it and leaves `ignore` behind, so the two
/// paths that did not win run something that does nothing, and there is no state to read
/// before acting on.
let private waitOrPoke (ms: float) (register: (unit -> unit) -> unit) : Async<bool> =
    Async.FromContinuations (fun (resume, _, _) ->
        let ending = ref ignore
        let stopListening = listening Browser.Dom.window "online" (fun _ -> ending.Value ())
        let timer = if ms >= 0.0 then Some (JS.setTimeout (fun () -> ending.Value ()) (int ms)) else None
        ending.Value <-
            fun () ->
                ending.Value <- ignore
                stopListening ()
                timer |> Option.iter JS.clearTimeout
                resume true
        register (fun () -> ending.Value ()))

let private waitBeforeRetry (delay: System.TimeSpan option) : Async<bool> =
    let ms =
        match delay with
        | Some d -> d.TotalMilliseconds
        | None -> -1.0
    waitOrPoke ms (fun finish -> pokeRetry <- finish)

let private jsRandom () : float = JS.Math.random ()

let private mintId (prefix: string) =
    sprintf "%s-%d" prefix (int (jsRandom () * 1000000000.0))

// The peer id is STABLE per browser profile (Plan 07): minted once, kept in
// localStorage under a browser-wide key (not per session — it names the browser, the
// same human across sessions), so colours and draft slots survive reloads. Storage
// denied (private mode) falls back to the per-load mint.
//
// `minted` is a VALUE, evaluated once by whoever called this, so the fresh id stored and the
// one answered are the same id however many branches read it.
let private persistentPeerId (minted: string) : string =
    let key = "yession/peer-id"
    try
        match Browser.WebStorage.localStorage.getItem key with
        | null | "" ->
            Browser.WebStorage.localStorage.setItem (key, minted)
            minted
        | existing -> existing
    with _ ->
        minted

let private urlEncode (value: string) : string = JS.encodeURIComponent value

// --- Claude connection panel round-trips (Plan 08) --------------------------------------
// Thin fetches against the session's /claude* routes; the same-origin auth cookie rides
// each one, and IS the whole identity — the browser asserts nothing about who it is.
// An ACTION that fails lands as `ok: false` with the response text, and the panel shows it;
// a status probe that could not answer says nothing at all (`fetchStatusAt`).
//
// These used to carry the peer id, and the credential was owned by it. A peer id lives in
// origin-partitioned localStorage, so it changed under the person holding it and stranded
// the credential behind every new one; ownership now comes off the cookie, Manager-side.

/// The clock the connection panels' waits are measured against. Milliseconds since the
/// epoch, because `Pending`'s deadline is an elapsed time and nothing here needs a date.
let private nowMillis () : int64 = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds ()

/// How often a panel re-asks the status while a command of ours is still on its way into
/// it. Short, because the frame it is waiting for is milliseconds behind the command and
/// the whole point is not to miss it by having asked once, too early; bounded by
/// `Pending.deadlineMillis`, which is what ends the asking.
[<Literal>]
let private pollLandedMillis = 200

/// A status round-trip that answers only when it HAS an answer. A fetch that never arrived, a
/// session that refused, and a reply this build cannot read are one outcome with one remedy:
/// say nothing, so the panel keeps showing what it last knew rather than blanking on a blip.
let private fetchStatusAt (decoder: Decoder<'reply>) (url: string) : Async<'reply option> =
    async {
        let init = [ Fetch.Types.RequestProperties.Cache Fetch.Types.RequestCache.Nostore ]
        // `fetchUnsafe`, not `fetch`: a refusal is a response to read the status off, not an
        // exception to catch (`fetchMe` above carries the rest of why).
        let! attempt = Fetch.fetchUnsafe url init |> Async.AwaitPromise |> Async.Catch
        match attempt with
        | Choice2Of2 _ -> return None
        | Choice1Of2 response when not response.Ok -> return None
        | Choice1Of2 response ->
            let! body = response.text () |> Async.AwaitPromise
            match Decode.fromString decoder body with
            | Ok reply -> return Some reply
            | Error _ -> return None
    }

let private fetchClaudeStatus () =
    fetchStatusAt Codec.claudePanel.Decode (Page.href ClaudeStatus)

/// A JSON POST: the one write shape both connection panels use. Every route they post to
/// decodes its body as JSON, so the content-type is stated once here rather than at each of
/// the six call sites — one of which would eventually be written without it.
let private postJson (url: string) (body: string) : Async<Answered> =
    answered
        [ Fetch.Types.RequestProperties.Method Fetch.Types.HttpMethod.POST
          Fetch.requestHeaders [ Fetch.Types.HttpRequestHeaders.ContentType "application/json" ]
          Fetch.Types.RequestProperties.Body (Fetch.Types.BodyInit.Case3 body) ]
        url

/// A field left empty is OMITTED rather than sent blank, which is what the `|| undefined`
/// this replaces was doing: `JSON.stringify` drops a key whose value is `undefined`, and a
/// `None` reaches it as exactly that.
let private sentIfGiven (value: string) : string option =
    if String.IsNullOrEmpty value then None else Some value

let private claudeBody (scope: string) (code: string) (token: string) : string =
    JS.JSON.stringify {| scope = scope; code = sentIfGiven code; token = sentIfGiven token |}

/// A field off an already-parsed JSON value, or None wherever JavaScript's `||` default fell
/// through — absent, `null`, `''` and `0` alike. That falsiness is not incidental: a poll reply
/// A field that is present but EMPTY is absent here: a blank url ends the flow exactly as a
/// missing one does, and a stated `interval: 0` has to take the default rather than ask this
/// tab to poll flat out. `|| undefined` used to say both inside a macro; `Option.filter` says
/// it in F#, where the rule can be read without reconstructing JavaScript truthiness.
let private stated (value: string option) : string option =
    value |> Option.filter (fun text -> text <> "")

let private statedSeconds (value: int option) : int option =
    value |> Option.filter (fun seconds -> seconds <> 0)

/// The authorize url the session answered with, or None for a body that is not JSON, carries
/// no url, or carries a blank one — all three of which the caller ends the flow on.
let private parseAuthorizeUrl (body: string) : string option =
    Decode.fromString (Decode.field "authorizeUrl" Decode.string) body
    |> Result.toOption
    |> stated

/// What a panel's field holds. A selector that matches nothing — a panel that is not on
/// screen — reads as the empty string, which is what the caller acts on anyway.
let private panelInput (selector: string) : string =
    match Browser.Dom.document.querySelector selector with
    | null -> ""
    | field -> (field :?> Browser.Types.HTMLInputElement).value

// --- GitHub connection panel round-trips (Plan 14) ---------------------------------------
// Same fetch shapes as the Claude panel's; the flow differs (device code) so the two
// extra parsers below read the begin/poll replies.

let private fetchGitHubStatus () =
    fetchStatusAt Codec.githubPanel.Decode (Page.href GitHubStatus)

let private githubBody (scope: string) (token: string) : string =
    JS.JSON.stringify {| scope = scope; token = sentIfGiven token |}

/// The begin reply: the code to type, where to type it, and the seconds GitHub asks this tab to
/// leave between polls. A reply that states no interval — or states `0` — gets 5, which is the
/// device flow's own floor and a number that means something.
///
/// The code and the uri are both REQUIRED, and that is a change this file used to say it could
/// not make. `($0.verificationUri || '')` answered a missing uri with `""` inside a macro —
/// the fault YES009 names, kept where the rule could not see it, and rendered as an Approve
/// button linking to this very page. It does not need the model to learn a "no uri" case after
/// all: a begin that says nowhere to approve is no more a flow than one that says no code, and
/// the caller already had an answer for that. So the absence is unrepresentable here, and
/// `GitHubAwaitingApproval` goes on holding two strings that mean what they say.
type private DeviceBegin =
    { UserCode : string
      VerificationUri : string
      Interval : int }

let private deviceBegin : Decoder<DeviceBegin> =
    Decode.object (fun get ->
        { UserCode = get.Required.Field "userCode" Decode.string
          VerificationUri = get.Required.Field "verificationUri" Decode.string
          Interval = statedSeconds (get.Optional.Field "interval" Decode.int) |> Option.defaultValue 5 })

/// A body that is not JSON, one carrying neither half, and one carrying a blank half are the
/// same nothing to the caller.
let private parseDeviceBegin (body: string) : DeviceBegin option =
    Decode.fromString deviceBegin body
    |> Result.toOption
    |> Option.filter (fun began -> began.UserCode <> "" && began.VerificationUri <> "")

/// The poll reply: where the grant has got to, and a revised interval when GitHub asks to be
/// asked less often. No status is not a status — the caller reads anything that is not
/// `connected` as "still waiting", and an unreadable reply is still waiting too. `0` is no
/// revision, which is what the caller compares against the interval it is already leaving.
let private devicePoll : Decoder<{| status: string option; interval: int |}> =
    Decode.object (fun get ->
        {| status = stated (get.Optional.Field "status" Decode.string)
           interval = statedSeconds (get.Optional.Field "interval" Decode.int) |> Option.defaultValue 0 |})

let private parseDevicePoll (body: string) : {| status: string option; interval: int |} =
    Decode.fromString devicePoll body
    |> Result.toOption
    |> Option.defaultValue {| status = None; interval = 0 |}

// --- The launch surface's reads ---------------------------------------------------------------
// Three GETs, answered on this person's own credential by the session, read in the codec the
// session encoded them with. A failure carries its status, because a 401 is the one answer
// with a button (connect GitHub) rather than a retry.

/// `no-store`, because each of these is asked at the moment somebody looks: a listing served
/// from a kept copy would answer with the repositories this person could reach when the tab
/// was opened rather than the ones they can reach now.
let private getText (url: string) : Async<Answered> =
    answered [ Fetch.Types.RequestProperties.Cache Fetch.Types.RequestCache.Nostore ] url

/// One page of the listing. The URL is composed here only for the FIRST page, from what was
/// typed; every page after it is asked for with the cursor the page before it carried, and
/// this browser never reads that cursor or builds a page url of its own — which is what
/// makes the cursor the session's to define (`Repos.RepoPage`).
let private fetchRepoPage (url: string) : Async<Result<RepoPage, string * bool>> =
    async {
        let! reply = getText url
        if not reply.Ok then
            return
                Error (
                    (if reply.Body = "" then sprintf "the session answered %d" reply.Status else reply.Body),
                    reply.Status = 401)
        else
            match Codec.fromString Codec.repoPage reply.Body with
            | Ok page -> return Ok page
            | Error reason -> return Error (reason, false)
    }

let private fetchRepoListing (query: string) : Async<LaunchListing> =
    async {
        let url =
            match query.Trim () with
            | "" -> Page.href GitHubRepos
            | text -> Page.href GitHubRepos + "?q=" + urlEncode text
        match! fetchRepoPage url with
        | Ok page -> return ListingLoaded page
        | Error (reason, signIn) -> return ListingUnavailable (reason, signIn)
    }

/// One page of a repo's branches, from a url the SESSION composed — the listing's rule, for
/// the listing's reason (`fetchRepoPage`).
let private fetchBranchPage (url: string) : Async<Result<BranchPage, string>> =
    async {
        let! reply = getText url
        if not reply.Ok then
            return Error (if reply.Body = "" then sprintf "the session answered %d" reply.Status else reply.Body)
        else
            match Codec.fromString Codec.branchPage reply.Body with
            | Ok page -> return Ok page
            | Error reason -> return Error reason
    }

let private fetchRepoBranches (repo: RepoRef) : Async<LaunchBranches> =
    async {
        let owner, name = RepoRef.owner repo, RepoRef.repo repo
        match! fetchBranchPage (Page.href (GitHubBranches (owner, name))) with
        | Ok page -> return BranchesLoaded page
        | Error reason -> return BranchesUnavailable reason
    }

/// Where a pull request comes from, so a pasted link to one can be launched: the one link
/// that has to be asked about before it can be sent.
let private fetchPullHead (repo: RepoRef) (number: int) : Async<Result<PullHead, string>> =
    async {
        let owner, name = RepoRef.owner repo, RepoRef.repo repo
        let! reply = getText (Page.href (GitHubPullHead (owner, name, string number)))
        if not reply.Ok then
            return Error (if reply.Body = "" then sprintf "the session answered %d" reply.Status else reply.Body)
        else
            return Codec.fromString Codec.pullHead reply.Body
    }

// --- The read surface's stream (Plan 15) --------------------------------------------------
// `EventSource` rather than the repo's fetch-based SSE reader: it is the browser's own SSE
// client, it reconnects on its own, and it carries the session cookie same-origin — which
// is the whole authentication story for a route that is cookie-gated.
//
// ONE connection carries every query. It is opened once at start and never closed: there
// is nothing to re-probe on, because a value arrives when it changes rather than when
// somebody looks.

/// The stream, with the handler already on it: opened, subscribed, handed back. The caller
/// keeps nothing — it never closes this, per the paragraph above — so what comes back is the
/// source itself rather than anything this client would have to remember how to undo.
let private openQueryStream (url: string) (onFrame: string -> unit) : EventSource =
    let source = EventSource.create url
    source.onmessage <- fun message -> onFrame (string message.data)
    source

// --- Entry -----------------------------------------------------------------------------

let private start () =
    async {
        let peerId =
            match PeerId.create (persistentPeerId (mintId "peer")) with
            | Ok id -> id
            | Error e -> failwith e
        let displayName = PeerName.random (Random ())
        let doc = Y.Doc.Create ()
        // Seed from the shell. The session id was already SSR'd into the model
        // (`Signalling.bootstrapHtml`) and then dropped on hydration, so `data-session-id`
        // rendered on the server, blanked, and only came back once `PeerAccepted` landed.
        // Plan 11 makes that load-bearing rather than cosmetic: the reconnect offer names
        // the session to reopen, and it appears precisely when no `PeerAccepted` has
        // happened.
        let initial =
            { ClientModel.init { PeerId = peerId; DisplayName = displayName } with
                Session =
                    metaContent Dom.sessionMetaName
                    |> Option.bind (fun value ->
                        match SessionId.create value with
                        | Ok id -> Some id
                        | Error _ -> None)
                Manager = metaContent Dom.managerMetaName
                EphemeralStorage = (metaContent Dom.ephemeralStorageMetaName).IsSome
                // The one place that can answer this: the model defaults to true because the
                // SERVER renders this shell too and has no idea what the browser can do.
                CanKeepHistory = canKeepHistory () }

        // The connection is wired later (after persistence and signalling); the interrupt
        // control holds this ref so everything else works before — and without — the
        // network (local first). `dispatchRef` lets the connection driver feed inbound
        // frames into the same Elmish loop the view dispatches into.
        let mutable connectionRef : Client.Connection option = None
        let mutable dispatchRef : (ClientMsg -> unit) = ignore

        // The doc's roots by name: each rich body's live Y.XmlFragment (a top-level root keyed
        // by BodyKey, so the editor and the Session Process bind the same fragment), and the
        // plain-text roots the terminal composers live in (Plan 13), resolved the same way.
        let registry = BodyRegistry doc
        let texts = TextRegistry doc
        // Kept current by `setState`: the read positions and the connection panels' flows
        // are read off it rather than out of a message.
        let mutable latestModel = initial
        // A caret moved: reported once per frame, to whichever connection there is by then.
        let sendFocus = Render.focusReporter (fun focus -> connectionRef |> Option.iter (fun c -> c.ReportPresence focus))

        // The collaborative text behind a field somebody's caret is in, for the fields worn by
        // a plain `<input>` — which report char offsets and so need the type to measure them
        // against. A body reports its own relative selection from inside its editor
        // (`Links.ReportFocus`) and never asks here.
        //
        // Neither path is reconstructed locally: the title is a named root and a chapter's
        // name rides its chapter's entry, and where each lives is the codec's answer
        // (`SyncedStateSync`), not a second one kept in step by hand.
        let textOfField (field: FocusField) : obj option =
            match field with
            | Title -> Some (box (doc.getText "title"))
            | ChapterName messageId ->
                SyncedStateSync.chapterNameText doc (MessageId.value messageId) |> Option.map box
            | DraftBody _ | QueueBody _ | TerminalDraftBody _ | TerminalQueuedBody _ -> None

        // The Claude connection panel's round-trips (Plan 08). Status is polled: once
        // after connect-probe, after every action, and every few seconds while a flow
        // awaits its callback (completion happens in the claude.ai tab, landing at the
        // Manager — this tab learns of it only by asking).
        let refreshClaude () =
            Async.StartImmediate (
                async {
                    match! fetchClaudeStatus () with
                    | None -> ()
                    // One message, because it is one answer: the picker's supply arrives
                    // inside the status it is a fact about, and what to do with a reply that
                    // does not mention it is `ClaudeStatus.keeping`'s to say.
                    | Some status -> dispatchRef (ClaudeStatusMsg status)
                })
        let rec pollClaudeWhileAwaiting () =
            Async.StartImmediate (
                async {
                    do! Async.Sleep 3000
                    match latestModel.Claude.Flow with
                    | ClaudeAwaitingCode _ ->
                        refreshClaude ()
                        pollClaudeWhileAwaiting ()
                    | _ -> ()
                })
        // A wait for a status this panel has not been shown yet, kept asking until the
        // status shows it or `Pending` gives up. The probe fired the moment the command
        // answered is the one that almost always settles it; this is what makes "almost"
        // stop mattering, because the status the command moved arrives on a stream of its
        // own and nothing orders the two (`Pending`'s own doc has the whole account).
        let rec pollClaudeUntilLanded () =
            Async.StartImmediate (
                async {
                    do! Async.Sleep pollLandedMillis
                    match latestModel.Claude.Pending with
                    | Pending.Awaiting _ ->
                        // The deadline is the model's rule, not this loop's: the loop only
                        // says what time it is.
                        dispatchRef (PendingWaitedMsg (nowMillis ()))
                        match latestModel.Claude.Pending with
                        | Pending.Awaiting _ ->
                            refreshClaude ()
                            pollClaudeUntilLanded ()
                        | _ -> ()
                    | _ -> ()
                })
        /// One shape for every panel action: sending → the command answers → either it is
        /// refused, or it opened an authorize tab (nothing for the status to show yet), or
        /// it was ACCEPTED and `expect` names what the status has to show before this panel
        /// calls it done.
        let claudeAction
            (run: unit -> Async<Result<string option, string>>)
            (scope: string)
            (expect: ConnectionExpectation option)
            =
            dispatchRef (ClaudePendingMsg Pending.Sending)
            Async.StartImmediate (
                async {
                    match! run () with
                    | Error reason -> dispatchRef (ClaudePendingMsg (Pending.Refused reason))
                    | Ok (Some authorizeUrl) ->
                        dispatchRef (ClaudePendingMsg Pending.Ready)
                        dispatchRef (ClaudeFlowMsg (ClaudeAwaitingCode (authorizeUrl, scope)))
                        pollClaudeWhileAwaiting ()
                    | Ok None ->
                        match expect with
                        | Some expect ->
                            dispatchRef (ClaudePendingMsg (Pending.Awaiting (expect, nowMillis ())))
                            pollClaudeUntilLanded ()
                        | None -> dispatchRef (ClaudePendingMsg Pending.Ready)
                        refreshClaude ()
                })
        let postClaudeAction
            (route: string)
            (scope: string)
            (code: string)
            (token: string)
            (expectUrl: bool)
            (expect: ConnectionExpectation option)
            =
            claudeAction
                (fun () ->
                    async {
                        let! reply = postJson route (claudeBody scope code token)
                        if not reply.Ok then return Error reply.Body
                        elif expectUrl then
                            match parseAuthorizeUrl reply.Body with
                            | None -> return Error "no authorize url in the reply"
                            | Some url -> return Ok (Some url)
                        else return Ok None
                    })
                scope
                expect

        // The GitHub panel's round-trips (Plan 14). Device flow: begin puts the user
        // code on screen, then this tab drives the session's poll at GitHub's stated
        // interval until the grant lands (a status probe then flips the flow to idle),
        // the human cancels, or the flow dies.
        let refreshGitHub () =
            Async.StartImmediate (
                async {
                    match! fetchGitHubStatus () with
                    | None -> ()
                    | Some status -> dispatchRef (GitHubStatusMsg status)
                })
        let rec pollGitHubWhileAwaiting () =
            Async.StartImmediate (
                async {
                    let interval =
                        match latestModel.GitHub.Flow with
                        | GitHubAwaitingApproval (_, _, _, interval) -> max 1 interval
                        | _ -> 0
                    do! Async.Sleep (interval * 1000)
                    match latestModel.GitHub.Flow with
                    | GitHubAwaitingApproval (userCode, verificationUri, scope, interval) ->
                        let! reply =
                            postJson
                                (Page.href (GitHub GitHubAction.Poll))
                                (githubBody scope "")
                        if not reply.Ok then
                            // A poll that failed is not necessarily a flow that ended. Only the
                            // session's own 4xx says this one is over; a 5xx or a fetch that
                            // never answered is a bad moment, and the code on screen — which the
                            // human may already have approved — is still good.
                            if GitHubFlow.ended reply.Status then
                                // The flow is over as well as refused, and both have to be
                                // said: the code on screen is dead, so it goes with the
                                // reason it died. (When the two were one state, saying the
                                // error did this by accident.)
                                dispatchRef (GitHubFlowMsg GitHubIdle)
                                dispatchRef (GitHubPendingMsg (Pending.Refused reply.Body))
                            else pollGitHubWhileAwaiting ()
                        else
                            let outcome = parseDevicePoll reply.Body
                            match outcome.status with
                            | Some "connected" -> refreshGitHub ()
                            | _ ->
                                if outcome.interval > interval then
                                    dispatchRef (GitHubFlowMsg (GitHubAwaitingApproval (userCode, verificationUri, scope, outcome.interval)))
                                pollGitHubWhileAwaiting ()
                    | _ -> ()
                })
        // The read surface (Plan 15): subscribe once, fold every frame. A malformed frame
        // is dropped rather than thrown — this is a best-effort push leg, and a stream
        // that dies on one bad line takes the whole surface down with it.
        //
        // Opened by the `/me` probe's authorized branch below, not here. This stream carries
        // the same cookie every other fetch does, so on a cold unauthenticated open it can
        // only 401 — and an EventSource reconnects on its own, so it 401s again and again in
        // the seconds before the login bounce navigates away. Nobody is waiting on a frame
        // that cannot arrive, and the console it was filling is the one anybody debugging a
        // real authorization fault has to read.
        let subscribeQueries () =
            openQueryStream
                (Page.href SessionRoute.Queries)
                (fun data ->
                    match Codec.fromString Codec.queryFrame data with
                    | Ok frame -> dispatchRef (QueryFrameMsg frame)
                    | Error _ -> ())
            |> ignore

        // The GitHub panel's own wait, for the Claude panel's reason and by the same rule.
        let rec pollGitHubUntilLanded () =
            Async.StartImmediate (
                async {
                    do! Async.Sleep pollLandedMillis
                    match latestModel.GitHub.Pending with
                    | Pending.Awaiting _ ->
                        dispatchRef (PendingWaitedMsg (nowMillis ()))
                        match latestModel.GitHub.Pending with
                        | Pending.Awaiting _ ->
                            refreshGitHub ()
                            pollGitHubUntilLanded ()
                        | _ -> ()
                    | _ -> ()
                })
        let githubAction
            (run: unit -> Async<Result<GitHubFlowState option, string>>)
            (expect: ConnectionExpectation option)
            =
            dispatchRef (GitHubPendingMsg Pending.Sending)
            Async.StartImmediate (
                async {
                    match! run () with
                    | Error reason -> dispatchRef (GitHubPendingMsg (Pending.Refused reason))
                    | Ok (Some flow) ->
                        dispatchRef (GitHubPendingMsg Pending.Ready)
                        dispatchRef (GitHubFlowMsg flow)
                        pollGitHubWhileAwaiting ()
                    | Ok None ->
                        match expect with
                        | Some expect ->
                            dispatchRef (GitHubPendingMsg (Pending.Awaiting (expect, nowMillis ())))
                            pollGitHubUntilLanded ()
                        | None -> dispatchRef (GitHubPendingMsg Pending.Ready)
                        refreshGitHub ()
                })

        // The copied mark is a moment, so it is one deadline: re-armed by each copy, and the
        // one it replaces is cleared. Two live timers over one slot would let the first
        // copy's deadline take the second copy's confirmation off the screen.
        let mutable copiedTimer = 0

        // The side effects a template can't derive from the model. Send routes to the one
        // implementation in `Client.connect` (capture markdown, enqueue, seed the queue fragment).
        let actions : ViewActions =
            { SendDraft = fun peer -> connectionRef |> Option.iter (fun c -> c.SendDraft peer)
              DiscardDraft = fun peer -> connectionRef |> Option.iter (fun c -> c.DiscardDraft peer)
              Interrupt = fun turn -> connectionRef |> Option.iter (fun c -> c.InterruptTurn turn)
              ToggleNav = toggleNav
              ToggleSettings =
                fun () ->
                    // Open (or close) the drawer AND re-probe, so it always shows the
                    // current truth the moment it appears.
                    // The connection panels are PROBED on open (they have no push leg);
                    // the query surface needs no re-probe, because its stream has been
                    // pushing since start.
                    toggleSettings ()
                    refreshClaude ()
                    refreshGitHub ()
              RevealSettings =
                fun () ->
                    revealSettings ()
                    refreshClaude ()
                    refreshGitHub ()
              ReportFieldSelection =
                fun field sel ->
                    // A collaborative input's caret, turned into relative positions over the
                    // `Y.Text` behind that field, so it survives concurrent edits exactly like
                    // a body one. rAF-throttled through the same path as bodies.
                    //
                    // No text, no report: a chapter whose entry has not reached this doc yet
                    // has nothing to measure against, and a position taken against the wrong
                    // type would encode, relay and decode into a caret somewhere else.
                    let focus =
                        sel
                        |> Option.bind (fun (anchor, head) ->
                            textOfField field
                            |> Option.map (fun text ->
                                let enc i = ProseMirror.relPosFromTypeIndex text i |> ProseMirror.encodeRel
                                { Field = field; Pos = { Anchor = enc anchor; Head = enc head } }))
                    sendFocus focus
              ClaudeConnect =
                fun () ->
                    let scope = match panelInput "[data-claude-scope]" with "" -> "mine" | s -> s
                    // Nothing for the status to show: what this returns is an authorize URL,
                    // and the credential arrives when the human finishes in that tab.
                    postClaudeAction (Page.href (Claude ClaudeAction.Begin)) scope "" "" true None
              ClaudeComplete =
                fun () ->
                    // The scope selector is unmounted while awaiting; the flow carries it.
                    let scope =
                        match latestModel.Claude.Flow with
                        | ClaudeAwaitingCode (_, scope) -> scope
                        | _ -> "mine"
                    match panelInput "[data-claude-code]" with
                    | "" -> dispatchRef (ClaudePendingMsg (Pending.Refused "paste the code first"))
                    | code ->
                        postClaudeAction
                            (Page.href (Claude ClaudeAction.Complete))
                            scope
                            code
                            ""
                            false
                            (Some { Scope = scope; Connected = true })
              ClaudePasteToken =
                fun () ->
                    match panelInput "[data-claude-token]" with
                    | "" -> dispatchRef (ClaudePendingMsg (Pending.Refused "paste a token first"))
                    | token ->
                        let scope = match panelInput "[data-claude-scope]" with "" -> "mine" | s -> s
                        postClaudeAction
                            (Page.href (Claude ClaudeAction.Token))
                            scope
                            ""
                            token
                            false
                            (Some { Scope = scope; Connected = true })
              ClaudeDisconnect =
                fun scope ->
                    postClaudeAction
                        (Page.href (Claude ClaudeAction.Disconnect))
                        scope
                        ""
                        ""
                        false
                        (Some { Scope = scope; Connected = false })
              GitHubConnect =
                fun () ->
                    let scope = match panelInput "[data-github-scope]" with "" -> "mine" | s -> s
                    // Nothing for the status to show yet: the grant lands when the human
                    // approves the code this puts on screen.
                    githubAction
                        (fun () ->
                        async {
                            let! reply =
                                postJson
                                    (Page.href (GitHub GitHubAction.Begin))
                                    (githubBody scope "")
                            if not reply.Ok then return Error reply.Body
                            else
                                match parseDeviceBegin reply.Body with
                                | None -> return Error "the reply began no device flow"
                                | Some began ->
                                    return
                                        Ok (Some (GitHubAwaitingApproval (began.UserCode, began.VerificationUri, scope, began.Interval)))
                        })
                        None
              GitHubPasteToken =
                fun () ->
                    match panelInput "[data-github-token]" with
                    | "" -> dispatchRef (GitHubPendingMsg (Pending.Refused "paste a token first"))
                    | token ->
                        let scope = match panelInput "[data-github-scope]" with "" -> "mine" | s -> s
                        githubAction
                            (fun () ->
                            async {
                                let! reply =
                                    postJson
                                        (Page.href (GitHub GitHubAction.Token))
                                        (githubBody scope token)
                                if not reply.Ok then return Error reply.Body else return Ok None
                            })
                            (Some { Scope = scope; Connected = true })
              Copy =
                fun key text ->
                    writeClipboard text (fun written ->
                        // Only a write that HAPPENED is confirmed. A refused clipboard leaves
                        // the box showing the value, which is what a person falls back to
                        // reading — a "copied" over an empty clipboard would send them to the
                        // other tab with nothing to paste.
                        if written then
                            if copiedTimer <> 0 then JS.clearTimeout copiedTimer
                            dispatchRef (CopiedMsg (Some key))
                            copiedTimer <-
                                JS.setTimeout
                                    (fun () ->
                                        copiedTimer <- 0
                                        dispatchRef (CopiedMsg None))
                                    copiedShownMs)
              GitHubDisconnect =
                fun scope ->
                    githubAction
                        (fun () ->
                        async {
                            let! reply =
                                postJson
                                    (Page.href (GitHub GitHubAction.Disconnect))
                                    (githubBody scope "")
                            if not reply.Ok then return Error reply.Body else return Ok None
                        })
                        (Some { Scope = scope; Connected = false })
              OpenTerminal = fun title -> connectionRef |> Option.iter (fun c -> c.OpenTerminal title)
              ApproveRepoCapabilities =
                fun repo granted -> connectionRef |> Option.iter (fun c -> c.ApproveRepoCapabilities repo granted)
              LaunchSearch =
                fun query ->
                    dispatchRef (LaunchMsg (LaunchListingArrived ListingUnknown))
                    Async.StartImmediate (
                        async {
                            let! listing = fetchRepoListing query
                            dispatchRef (LaunchMsg (LaunchListingArrived listing))
                        })
              LaunchMore =
                fun cursor ->
                    dispatchRef (LaunchMsg LaunchMoreStarted)
                    Async.StartImmediate (
                        async {
                            match! fetchRepoPage (Page.href GitHubRepos + "?page=" + urlEncode cursor) with
                            | Ok page -> dispatchRef (LaunchMsg (LaunchMoreArrived page))
                            | Error (reason, _) -> dispatchRef (LaunchMsg (LaunchMoreFailed reason))
                        })
              LaunchBranchesMore =
                fun repo cursor ->
                    dispatchRef (LaunchMsg LaunchBranchMoreStarted)
                    Async.StartImmediate (
                        async {
                            let owner, name = RepoRef.owner repo, RepoRef.repo repo
                            match! fetchBranchPage (Page.href (GitHubBranches (owner, name)) + "?page=" + urlEncode cursor) with
                            | Ok page -> dispatchRef (LaunchMsg (LaunchBranchMoreArrived (repo, page)))
                            | Error reason -> dispatchRef (LaunchMsg (LaunchBranchMoreFailed reason))
                        })
              LaunchBranches =
                fun repo ->
                    Async.StartImmediate (
                        async {
                            let! branches = fetchRepoBranches repo
                            dispatchRef (LaunchMsg (LaunchBranchesArrived (repo, branches)))
                        })
              LaunchStart =
                fun target ->
                    connectionRef
                    |> Option.iter (fun c -> dispatchRef (LaunchMsg (LaunchSent (c.AddRepo target.Repo target.Branch, target))))
              LaunchLink =
                fun link ->
                    // A link becomes a ROW, held — never a send: the same listing lookup a
                    // search makes, asked for the one name, answers the candidate under the
                    // provider's current name with its real default branch. A pull request is
                    // asked about first, since which fork its branch lives in only the
                    // provider knows.
                    dispatchRef (LaunchMsg (LaunchResolving link))
                    Async.StartImmediate (
                        async {
                            let! resolved =
                                match link with
                                | RepoLink.Repo repo -> async { return Ok (repo, None) }
                                | RepoLink.Branch (repo, branch) -> async { return Ok (repo, Some branch) }
                                | RepoLink.PullRequest (repo, number) ->
                                    async {
                                        match! fetchPullHead repo number with
                                        | Ok head -> return Ok (head.Repo, Some head.Branch)
                                        | Error reason -> return Error reason
                                    }
                            match resolved with
                            | Error reason -> dispatchRef (LaunchMsg (LaunchFailed reason))
                            | Ok (repo, branch) ->
                                match! fetchRepoListing (RepoRef.value repo) with
                                | ListingLoaded page when not (List.isEmpty page.Candidates) ->
                                    let candidate = List.head page.Candidates
                                    dispatchRef (LaunchMsg (LaunchLinked (candidate, branch)))
                                    Async.StartImmediate (
                                        async {
                                            let! branches = fetchRepoBranches candidate.Repo
                                            dispatchRef (LaunchMsg (LaunchBranchesArrived (candidate.Repo, branches)))
                                        })
                                | ListingLoaded _ ->
                                    dispatchRef (LaunchMsg (LaunchFailed (sprintf "github does not show %s to this credential" (RepoRef.value repo))))
                                | ListingUnavailable (reason, _) -> dispatchRef (LaunchMsg (LaunchFailed reason))
                                | ListingUnknown -> ()
                        })
              CloseTerminal = fun id -> connectionRef |> Option.iter (fun c -> c.CloseTerminal id)
              TakeTerminal = fun id -> connectionRef |> Option.iter (fun c -> c.TakeTerminal id)
              ReleaseTerminal = fun id -> connectionRef |> Option.iter (fun c -> c.ReleaseTerminal id)
              RearmTerminal = fun id -> connectionRef |> Option.iter (fun c -> c.RearmTerminal id)
              ReattachTerminal = fun id -> connectionRef |> Option.iter (fun c -> c.ReattachTerminal id)
              TypeIntoTerminal =
                fun id data -> connectionRef |> Option.iter (fun c -> c.TypeIntoTerminal id data)
              ResizeTerminal =
                fun id cols rows -> connectionRef |> Option.iter (fun c -> c.ResizeTerminal id cols rows)
              SendTerminalDraft =
                fun terminal author -> connectionRef |> Option.iter (fun c -> c.SendTerminalDraft terminal author)
              RetryNow =
                // Cut short whatever wait the lifecycle is in. On a refused peer that wait is
                // indefinite by design, so this is its only way back short of a reload.
                fun () -> pokeRetry ()
              FocusPane = PaneShell.toPane
              FocusChat = PaneShell.toChatItem
              FocusWatch = PaneShell.toWatchToggle
              RevealBlock = fun id blockId -> PaneShell.revealBlock (TerminalId.value id) (BlockId.value blockId)
              RevealMessage = fun id -> PaneShell.revealMessage (MessageId.value id)
              ScrollToLatest = PaneShell.scrollToLatest
              FocusItemActions = fun id -> PaneShell.toItemActions (MessageId.value id) }

        let el = appRoot ()
        // Take over the server-rendered shell: from here Lit owns it. lit-html's `render`
        // inserts its content AFTER a container's existing children rather than replacing
        // them, so the first paint the server rendered would linger beside the live one —
        // clearing once, before the client's first render, is what stops that.
        Elements.clearChildren el

        // The render, composed here and run by Elmish on every model change. What it needs of
        // the session it is handed as links, because the connection is wired later — and
        // because the shell harness runs the same render with no session at all.
        let renderer =
            Render.create
                { Doc = doc
                  Registry = registry
                  Texts = texts
                  PeerId = peerId
                  Root = el
                  Actions = actions
                  Dispatch = fun msg -> dispatchRef msg
                  Links =
                    { SendDraft = fun author -> connectionRef |> Option.iter (fun c -> c.SendDraft author)
                      SendTerminalDraft =
                        fun terminal author -> connectionRef |> Option.iter (fun c -> c.SendTerminalDraft terminal author)
                      ReportFocus = sendFocus
                      ResizeTerminal = fun id cols rows -> connectionRef |> Option.iter (fun c -> c.ResizeTerminal id cols rows)
                      Http = httpGet } }
        // The launch surface's listing is asked for ONCE, the first time the surface is
        // offered: it has no mount hook of its own, and asking on every render would ask on
        // every keystroke.
        let mutable launchListingAsked = false
        let setState (model: ClientModel) (dispatch: Ylmish.Program.Message<ClientMsg> -> unit) =
            dispatchRef <- fun msg -> dispatch (Ylmish.Program.Message.User msg)
            latestModel <- model
            renderer.SetState model
            if not launchListingAsked && ClientModel.launchOffered model then
                launchListingAsked <- true
                actions.LaunchSearch model.Launch.Query

        Client.makeProgram doc initial
        |> Program.withSetState setState
        |> Program.run

        Render.attach ()

        // The local peer's draft slot follows its body: published on the first keystroke,
        // retracted when the composer empties. Watches the body itself, so a keystroke and a
        // merged remote edit settle it and nothing else does.
        DraftSlot.follow doc registry peerId (fun msg -> dispatchRef msg) |> ignore

        // Local-first: the doc persists in IndexedDB keyed by the session's address. Cold
        // loads render local state (drafts, queued messages) before — and without — the
        // network; on reconnect the full-state exchange reconciles.
        let persistence = Fable.YIndexeddb.create (persistenceKey ()) doc
        do! persistence.whenSynced () |> Async.AwaitPromise

        // The replayed doc is state that did not arrive as a body change, so settle the rule
        // against it explicitly: a doc stored before publication followed the body can hold an
        // empty-bodied slot, and this is where it goes.
        DraftSlot.settle doc registry peerId (fun msg -> dispatchRef msg)

        // Durable history, out of this client's own store, BEFORE anything asks the network
        // and regardless of what `/me` is about to say (Plan 20). The probe decides whether
        // this client may CONNECT; it has never had any business deciding whether a client may
        // read what it was already given. That it did is why an offline open rendered an empty
        // conversation rather than the one it had been reading.
        // The worker first, because it is what makes the NEXT cold open work; this one is
        // already served. Fire and forget: nothing here depends on it, and a client that
        // cannot have one loses only the offline open.
        registerWorker (Page.href ServiceWorker)

        let! historyCache = openHistoryCache ()
        let! transcriptCaches = openTranscriptCaches ()
        // `Connecting`, then the events, then the transcripts — the sequence a person watches
        // between the first paint and the connection, and the one the harness measures
        // (`Client.LocalOpen`, which says why the order is that).
        do! Client.LocalOpen.replay historyCache transcriptCaches (fun msg -> dispatchRef msg)

        // Authorization by renavigation: probe `/me` for a peer token. 401 -> bounce
        // through `/login` (code + PKCE via the Manager) and land back on this shell,
        // where the probe succeeds. The common first visit never takes this branch: the
        // Manager's `/open` page enters a session through `/login`, so the cookie is
        // already there by the time this shell loads, and the shell is painted once. The
        // bounce from here is for a shell reached any other way — a bookmark, a home-screen
        // icon, an expired cookie. A NETWORK failure (offline, session down) is a
        // `Disconnected` with its reason, not silence: the local-first shell — IndexedDB doc
        // plus the event ranges in this client's own store — stays fully usable, and the model
        // says why it is alone.
        let! outcome = fetchMe (Page.href Me) Client.Probe.deadline.TotalMilliseconds
        match outcome with
        | ProbeUnreachable detail ->
            dispatchRef (ConnectFailedMsg (Client.ChannelFault.describe (Client.ChannelUnreachable detail)))
        | ProbeUnauthorized -> renavigateTo (Page.href Login)
        | ProbeAuthorized me ->
            // Authenticated: the Claude panel's status is knowable now, and the read
            // surface's stream has a cookie that will be accepted.
            refreshClaude ()
            subscribeQueries ()
            // `me.DisplayName` is the attributed user's real name, when `/me` had one
            // (see `Signalling.fs`) — carried into OUR OWN `PeerHello` instead of the
            // random one so the durable `PeerJoined` this join appends records the name a
            // person actually goes by. Falling back to the random `displayName` when the
            // probe had none (unattributed access) keeps that case exactly as it was.
            let effectiveDisplayName =
                me.DisplayName |> Option.filter (fun name -> name <> "") |> Option.defaultValue displayName
            let hello =
                { PeerId = peerId
                  DisplayName = effectiveDisplayName
                  Token = me.PeerToken }
            // Events come over HTTP by CURSOR: a client asks from the position it has folded
            // through and is answered with a range whose bounds never move, so history is
            // served out of this client's own Cache API store and only what is past its
            // position reaches the Session Process. The HTTP cache holds none of it — every
            // response on that surface is `no-store`, because a second copy there would be a
            // spare nobody reads. Availability hints still arrive over the data channel. The
            // same-origin auth cookie rides each fetch, so no token in the URL (history stays
            // clean).
            //
            // Both resilience policies are composed HERE, at the transport, and nowhere else:
            // `Client.connect` is handed a feed that has already spent its retries, so the read
            // loop only ever sees a settled outcome and the application code holds no notion of
            // retrying, backoff, or attempt counts. Interim progress is the policy's to report,
            // which is the one thing a settled outcome cannot carry.
            let feed =
                // `storing` sits UNDER the policy, so only a settled answer is kept — a
                // retried fetch stores once, and a failed one stores nothing.
                Client.EventFetch.overHttp (Client.EventFetch.storing historyCache httpGet) Page.href None
                |> Resilience.Policy.guard
                    (Client.EventFetch.policy Resilience.Policy.sleep jsRandom (fun attempt ->
                        Client.EventFetch.retrying attempt
                        |> Option.iter (fun health -> dispatchRef (EventFeedMsg health))))
            // Terminal history rides the same HTTP leg, by the same cursor, for the same
            // payoff: a reload replays a terminal out of this client's own store and only what
            // happened since crosses the network. No resilience policy on it — unlike the event
            // feed, a failed read here is re-armed by the next record or availability hint
            // that arrives, so there is nothing for a retry schedule to add.
            let transcripts =
                Client.TranscriptFetch.overHttp transcriptCaches httpGet Page.href None
            let options =
                { Client.ConnectOptions.defaults with
                    FetchEvents = Some feed
                    FetchTranscripts = Some transcripts
                    // A terminal's screen seeds this client's emulator. The transcript stays
                    // the record; this is the view, and a peer that arrives mid-session gets
                    // one frame instead of every byte the terminal ever printed.
                    OnTerminalSnapshot = fun id keyframe -> renderer.Screens.Snapshot id keyframe
                    // The model is the read position (see `ConnectOptions.ReadPosition`):
                    // `latestModel` is kept current by `setState`, so a fold rolled back by
                    // a racing doc update is visibly behind and gets re-read.
                    ReadPosition = Some (fun () -> latestModel.EventConsumer.LastProcessedOffset)
                    // Same rule one feed over: a client that just replayed a terminal out of
                    // its own store must resume where that got to, not at line 0.
                    TranscriptReadPosition =
                        Some (fun terminal ->
                            latestModel.TerminalFeeds
                            |> Map.tryFind terminal
                            |> Option.map (fun feed -> feed.ReadThrough)
                            |> Option.defaultValue 0) }
            let openChannel =
                Resilience.Policy.guard
                    (Client.SessionChannel.policy Resilience.Policy.sleep jsRandom)
                    (fun () -> connectChannel (absolute (Page.href Signal)))

            // The session leg. The RULES — announce, open, serve, and come back only for a
            // session that was accepted — are `Client.SessionLifecycle`; this supplies the
            // browser's four ports and nothing else.
            do!
                Client.SessionLifecycle.run
                    (Client.SessionLifecycle.supervision jsRandom)
                    { Open = openChannel
                      Serve =
                        fun resumeAfter dispatch carrier ->
                            async {
                                // Supervised at the transport boundary, exactly as the event
                                // feed's resilience policy is composed here and nowhere else:
                                // `Client.connect` receives a channel that already knows how to
                                // notice its own death, and holds no notion of heartbeats.
                                let channel = Link.supervise Link.LinkPolicy.shipped carrier
                                let connection =
                                    Client.connect
                                        { options with ResumeAfter = resumeAfter }
                                        doc
                                        registry
                                        texts
                                        hello
                                        dispatch
                                        channel
                                connectionRef <- Some connection
                                do! connection.Run
                                connectionRef <- None
                            }
                      ReadPosition = fun () -> latestModel.EventConsumer.LastProcessedOffset
                      // Always `true`: a page that is still open is a client that still wants
                      // its session. The lifecycle ends when the page does.
                      WaitBeforeRetry = waitBeforeRetry
                      Dispatch = fun msg -> dispatchRef msg }
    }

Async.StartImmediate (start ())
