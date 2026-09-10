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
open Yjs
open Yession.Domain
open Yession.Domain.Link
open Yession.Domain.Terminals
open Yession.Domain.Collab
open Yession.App
open Lit

// --- Native WebRTC (non-trickle, mirroring app/WebRtc.fs) -----------------------------

// Opening the data channel, as a TOTAL function: it settles with the channel, or with why it
// could not be had. It used to resolve only on `dc.onopen`, so a signalling POST that failed
// — or a session that simply was not there — left this promise pending forever and the shell
// stuck on "connecting" with nothing to say and nothing to do.
//
// `timeoutMs` bounds the whole handshake (offer, gathering, answer, channel open); it is the
// difference between "not connected, the session did not answer" and an eternal wait.
[<Emit("""(function (signalUrl, timeoutMs) { return (
new Promise((resolve) => {
  const t0 = performance.now()
  const took = () => Math.round(performance.now() - t0)
  const pc = new RTCPeerConnection({ iceServers: [] })
  const dc = pc.createDataChannel('session')
  let settled = false
  const succeed = () => { if (!settled) { settled = true; resolve({ ok: true, channel: dc, connection: pc, timedOut: false, detail: '', tookMs: took() }) } }
  const fail = (timedOut, detail) => {
    if (settled) return
    settled = true
    try { pc.close() } catch {}
    resolve({ ok: false, channel: null, connection: null, timedOut, detail: String(detail), tookMs: took() })
  }
  let sent = false
  const send = async () => {
    if (sent || settled) return
    sent = true
    try {
      const reply = await fetch(signalUrl, {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ type: pc.localDescription.type, sdp: pc.localDescription.sdp })
      })
      if (!reply.ok) return fail(false, 'signalling refused: ' + reply.status)
      await pc.setRemoteDescription(await reply.json())
    } catch (e) { fail(false, e) }
  }
  // Non-trickle: send once gathering completes — with a settle fallback, because some
  // browsers/sandboxes never report 'complete' (mDNS candidate obfuscation can stall).
  pc.onicegatheringstatechange = () => { if (pc.iceGatheringState === 'complete') send() }
  pc.onicecandidate = (e) => { if (e.candidate === null) send() }
  setTimeout(send, 1500)
  setTimeout(() => fail(true, ''), timeoutMs)
  dc.onopen = succeed
  pc.createOffer().then(o => pc.setLocalDescription(o), e => fail(false, e))
})
) })($0, $1)""")>]
let private openDataChannel (signalUrl: string) (timeoutMs: int) : JS.Promise<{| ok: bool; channel: obj; connection: obj; timedOut: bool; detail: string; tookMs: int |}> = jsNative

/// How long a whole handshake gets before it counts as "the session did not answer". Long
/// enough for ICE gathering on a slow machine, short enough that a dead session is reported
/// rather than waited on.
let private channelOpenTimeoutMs = 10000

[<Emit("$0.onmessage = (e) => $1(String(e.data))")>]
let private onMessage (dc: obj) (handler: string -> unit) : unit = jsNative

[<Emit("$0.onclose = $1")>]
let private onClose (dc: obj) (handler: unit -> unit) : unit = jsNative

[<Emit("(function (dc, text) { return dc.readyState === 'open' && (dc.send(text), true) })($0, $1)")>]
let private sendMessage (dc: obj) (text: string) : bool = jsNative

/// Both of the peer connection's state machines, as one "this transport is finished" signal.
///
/// This is why the connection is kept at all. The promise above used to resolve with the data
/// channel ALONE, so nothing could observe either state, nothing could close a dead connection,
/// and the only way a client learned its transport had died was `dc.onclose` — an event a
/// half-open channel never fires.
///
/// `disconnected` is deliberately NOT here. It is a maybe, not a verdict, and the honest answer
/// to a maybe already exists: the heartbeat asks, and gets an answer or does not, inside about
/// three seconds. A grace timer here would be a second clock measuring the same doubt.
//
// The local names here are deliberately NOT `pc`/`dc`: `$0` is substituted TEXTUALLY with the
// caller's identifier, so `const pc = $0` at a call site whose argument is itself named `pc`
// emits `const pc = pc` — a temporal dead zone error that takes the whole shell down at load.
[<Emit("""(function (pc, handler) {
  const peer = pc, onDead = handler
  const finished = () =>
    peer.connectionState === 'failed' || peer.connectionState === 'closed' ||
    peer.iceConnectionState === 'failed' || peer.iceConnectionState === 'closed'
  const check = () => { if (finished()) onDead() }
  peer.addEventListener('connectionstatechange', check)
  peer.addEventListener('iceconnectionstatechange', check)
  check()
})($0, $1)""")>]
let private onPeerFinished (pc: obj) (handler: unit -> unit) : unit = jsNative

/// Look again the moment the page comes back — a phone returning from the background, a
/// network coming back, a tab being switched to. Returns the way to stop looking.
///
/// Not a second mechanism: it asks exactly the question `onPeerFinished` answers, at the one
/// moment a browser is most likely to have torn the transport down while no script was running
/// to hear about it. That moment is where the reported bug lived.
[<Emit("""(function (pc, dc, handler) {
  const peer = pc, chan = dc, onDead = handler
  const look = () => {
    if (document.visibilityState === 'hidden') return
    if (peer.connectionState === 'failed' || peer.connectionState === 'closed' ||
        peer.iceConnectionState === 'failed' || peer.iceConnectionState === 'closed' ||
        chan.readyState !== 'open') onDead()
  }
  window.addEventListener('pageshow', look)
  window.addEventListener('online', look)
  document.addEventListener('visibilitychange', look)
  return () => {
    window.removeEventListener('pageshow', look)
    window.removeEventListener('online', look)
    document.removeEventListener('visibilitychange', look)
  }
})($0, $1, $2)""")>]
let private onResume (pc: obj) (dc: obj) (handler: unit -> unit) : (unit -> unit) = jsNative

[<Emit("$0.close()")>]
let private closePeer (pc: obj) : unit = jsNative

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
let private frameChannel (dc: obj) (pc: obj) : FrameChannel<string> =
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
    onMessage dc (fun text ->
        match Codec.fromString frameCodec text with
        | Ok frame -> deliver (Some frame)
        | Error e -> JS.console.error ("frame decode failed: " + e))
    onClose dc finish
    onPeerFinished pc finish
    stopLooking <- onResume pc dc finish
    { Send = fun frame -> async { sendMessage dc (Codec.toString frameCodec frame) |> ignore }
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
                emitJsExpr dc "$0.close()"
                closePeer pc
            } }

/// One attempt at the transport, shaped as the resilience policy consumes it. What settles is
/// a CHANNEL, not the WebRTC objects behind it: the peer connection never leaves this module,
/// which is what lets everything above hold one idea of a transport.
let private connectChannel (signalUrl: string) : Async<Result<FrameChannel<string>, Client.ChannelFault>> =
    async {
        let! reply = openDataChannel signalUrl channelOpenTimeoutMs |> Async.AwaitPromise
        // How long the handshake took, said out loud. Open latency is a property this repo has
        // already traded a whole ICE backend to protect (docs/decisions/2026-07-26), and it is
        // invisible from the outside: a slow session and a slow handshake look identical from
        // the shell. Free on success, and the one number worth having when they do not.
        JS.console.debug (
            sprintf "yession/link: handshake %s in %dms" (if reply.ok then "opened" else "failed") reply.tookMs)
        return
            if reply.ok then Ok (frameChannel reply.channel reply.connection)
            elif reply.timedOut then Error Client.ChannelTimedOut
            else Error (Client.ChannelUnreachable reply.detail)
    }

// --- DOM shell -------------------------------------------------------------------------

[<Emit("document.getElementById('app')")>]
let private appRoot () : obj = jsNative

// lit-html's `render` inserts its content AFTER a container's existing children rather
// than replacing them, so the server-rendered shell (first paint) would linger beside the
// live one. Clear it once before the client's first render so Lit owns `#app` outright.
[<Emit("$0.replaceChildren()")>]
let private clearChildren (el: obj) : unit = jsNative

/// Put text on the system clipboard, and say whether the browser let us. Asynchronous
/// because the write may be a permission prompt, and refusable for reasons the page cannot
/// see coming — an insecure context has no `navigator.clipboard` at all, which is the
/// common one: a session reached over plain HTTP at a LAN address.
///
/// The refusal is written to the console rather than swallowed, because the only symptom
/// it has otherwise is a button that appears to do nothing — the same shape as a broken
/// binding, and nothing on the page tells the two apart.
[<Emit("""(function (text, settled) {
  const clip = navigator.clipboard
  if (!clip) { console.debug('yession/copy: no clipboard in this context'); settled(false); return }
  clip.writeText(text).then(
    () => settled(true),
    (err) => { console.debug('yession/copy: refused', String(err)); settled(false) })
})($0, $1)""")>]
let private writeClipboard (text: string) (settled: bool -> unit) : unit = jsNative

/// How long a copy says so for. Long enough to be read as an answer to the press, short
/// enough that the code it stands in front of comes back before anybody needs it again.
let private copiedShownMs = 1500

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
[<Emit("""(() => {
  const root = document.documentElement
  const desktop = window.matchMedia('(min-width: 768px)').matches
  root.classList.toggle('nav-alt')
  // The nav control always returns the column to its workspace face — a column that reopened
  // on settings would be a surprise, and `settings-open` is what chooses the face.
  root.classList.remove('settings-open')
  const shown = desktop !== root.classList.contains('nav-alt')
  if (desktop) { try { localStorage.setItem('yession.nav', shown ? 'open' : 'collapsed') } catch (e) {} }
  requestAnimationFrame(() => {
    const next = document.querySelector(shown ? 'button[data-nav-toggle="hide"]' : '[data-nav-toggle="show"]')
    if (next) next.focus()
  })
})()""")>]
let private toggleNav () : unit = jsNative

// Settings is the sidebar column's other FACE (Style.settingsPane), not a drawer over the
// conversation — so opening it has to bring that column on screen, and `nav-alt` means the
// opposite thing on each side of the breakpoint: uncollapse on desktop, slide the drawer in on
// mobile. Focus follows the same rule as the nav toggle.
[<Emit("""(() => {
  const root = document.documentElement
  const desktop = window.matchMedia('(min-width: 768px)').matches
  const opening = !root.classList.contains('settings-open')
  root.classList.toggle('settings-open', opening)
  if (desktop) { if (opening) root.classList.remove('nav-alt') }
  else root.classList.toggle('nav-alt', opening)
  // TWO frames: the face that is arriving is `visibility: hidden` until the transition it
  // just started reaches its first style flush, and `focus()` on a hidden element is a no-op
  // (measured — one frame left focus on <body>).
  requestAnimationFrame(() => requestAnimationFrame(() => {
    const next = document.querySelector(opening ? '[data-settings-toggle="close"]' : '[data-settings-toggle="open"]')
    if (next) next.focus()
  }))
})()""")>]
let private toggleSettings () : unit = jsNative

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
[<Emit("""(() => {
  const root = document.documentElement
  const desktop = window.matchMedia('(min-width: 768px)').matches
  const wasOpen = root.classList.contains('settings-open')
  root.classList.add('settings-open')
  // Bring the column on screen: `nav-alt` means the opposite thing on each side of the
  // breakpoint — collapsed on desktop, drawer-open on mobile.
  if (desktop) root.classList.remove('nav-alt')
  else root.classList.add('nav-alt')
  // Focus moves only when the face actually ARRIVED. Stealing it from whatever the reader
  // was doing, to a control that was already on screen, would be the prompt reaching into a
  // panel they are already reading.
  if (!wasOpen) {
    // TWO frames, for the reason the toggle needs them: the arriving face is
    // `visibility: hidden` until the transition it just started reaches its first style
    // flush, and `focus()` on a hidden element is a no-op.
    requestAnimationFrame(() => requestAnimationFrame(() => {
      const next = document.querySelector('[data-settings-toggle="close"]')
      if (next) next.focus()
    }))
  }
})()""")>]
let private revealSettings () : unit = jsNative

// The auth probe: `me` answers with a peer token when the browser's cookie (or an
// auth-less session) allows it — total in BOTH axes it can fail on, because the two need
// opposite remedies: `authorized = false` means log in (the shell renavigates), while
// `reachable = false` means the session is not there at all (the shell stays local-first
// on its cached stores and says so). Collapsing them — which a thrown fetch did — turns
// "offline" into "log in", and a login bounce against an unreachable session goes nowhere.
//
// The URL is a PARAMETER, not baked into the Emit: a string literal inside an Emit is
// outside F#'s reach, so a path embedded here could not be checked against
// `SessionRoute`. Every fetch below takes its URL from `SessionRoute.relative`, and the
// browser resolves it against the shell's `<base href>`.
//
// A REFUSAL is 401/403 and nothing else. Every other error status — a 502 from the
// operator's proxy standing in front of a session that is gone, a 503 from one still
// starting — is the session not being there, which is the other axis entirely. Reading them
// as "log in" sent a client whose session had stopped off to a login bounce that could only
// fail, and (once the shell was served from a worker) replaced a perfectly good offline
// session with a browser error page. The thrown case was already right; this is the same
// distinction for the answers that arrive.
//
// And an answer that does NOT arrive is the same axis again (`Client.Probe.deadline`): the
// abort rejects, and the rejection is the thrown case with the timeout as its reason. The
// deadline is a parameter for the reason the URL is — a number inside an Emit is outside
// F#'s reach, and this one is the domain's to state.
[<Emit("""fetch($0, { cache: 'no-store', signal: AbortSignal.timeout($1) }).then(
  r => r.ok ? r.json().then(me => ({ reachable: true, authorized: true, token: me.peerToken, detail: '' }))
      : (r.status === 401 || r.status === 403)
        ? { reachable: true, authorized: false, token: '', detail: 'HTTP ' + r.status }
        : { reachable: false, authorized: false, token: '', detail: 'HTTP ' + r.status },
  e => ({ reachable: false, authorized: false, token: '', detail: String(e) }))""")>]
let private fetchMe (url: string) (deadlineMs: float) : JS.Promise<{| reachable: bool; authorized: bool; token: string; detail: string |}> = jsNative

// `location.assign` resolves against the DOCUMENT's URL, not `<base href>` — the one
// place relative resolution does not follow the base — so resolve explicitly against
// `document.baseURI` here, once, rather than at each call site.
[<Emit("window.location.assign(new URL($0, document.baseURI).href)")>]
let private navigateTo (url: string) : unit = jsNative


// --- Client-side doc persistence (Step 20): IndexedDB via y-indexeddb ------------------

[<Import("IndexeddbPersistence", "y-indexeddb")>]
let private indexeddbPersistence : obj = jsNative

[<Emit("new $0($1, $2)")>]
let private newPersistence (ctor: obj) (name: string) (doc: Y.Doc) : obj = jsNative

[<Emit("new Promise((resolve) => $0.once('synced', resolve))")>]
let private whenSynced (persistence: obj) : JS.Promise<unit> = jsNative

// The store is keyed by SESSION: the serving Session Process embeds its session id in the
// bootstrap page (a synchronous, pre-connection identity), so two sessions served from one
// address never share a store. The KEY is stable wherever the session is served from; the
// STORE is not. IndexedDB is partitioned by origin and a port is part of one, so a deployment
// addressing sessions as `127.0.0.1:{port}` returns to an empty database after every relaunch
// — which is what `PublicAccess.sessionAddressIsStable` marks on the shell, and why the
// client's local-first copy is qualified there rather than promised.
[<Emit("""(() => {
  const meta = document.querySelector('meta[name="yession-session"]')
  const session = meta && meta.getAttribute('content')
  return session ? 'yession/session/' + session : 'yession/' + window.location.host + window.location.pathname
})()""")>]
let private persistenceKey () : string = jsNative

/// A `<meta name>`'s content, or None when the tag is absent. `|| null` so a missing tag
/// and a missing attribute both arrive as `None` rather than as `undefined` masquerading
/// as a string.
[<Emit("document.querySelector('meta[name=\"' + $0 + '\"]')?.getAttribute('content') || null")>]
let private metaContent (name: string) : string option = jsNative

// Resolved against the shell's `<base href>`, so a session mounted under a path signals
// to its own prefix rather than the origin root.
[<Emit("new URL($0, document.baseURI).href")>]
let private absolute (relative: string) : string = jsNative

// The event-chunk GET as a TOTAL function: the body, the status it refused with, or the
// transport error it never got past (`status: 0` — offline, refused, DNS, TLS). It never
// rejects, because the information a rejection destroys is exactly the information the
// resilience policy needs to decide whether retrying could help.
// `r.url` is the address the answer came back FROM, which after a redirect is not the one
// that was asked for — and it is the one worth keeping, because a range's bounds never move
// while a cursor's answer does (Plan 20).
[<Emit("""fetch($0).then(
  async r => r.ok ? { ok: true, status: r.status, url: r.url, detail: await r.text() } : { ok: false, status: r.status, url: r.url, detail: '' },
  e => ({ ok: false, status: 0, url: '', detail: String(e) }))""")>]
let private fetchChunk (url: string) : JS.Promise<{| ok: bool; status: int; url: string; detail: string |}> = jsNative

let private httpGet : Client.HttpGet =
    fun url ->
        async {
            let! reply = fetchChunk url |> Async.AwaitPromise
            return
                if reply.ok then Ok { Url = reply.url; Body = reply.detail }
                elif reply.status = 0 then Error (Client.HttpUnreachable reply.detail)
                else Error (Client.HttpStatus reply.status)
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

[<Emit("(typeof window !== 'undefined' && window.isSecureContext === true && !!window.caches)")>]
let private canKeepHistory () : bool = jsNative

// The cache is named for the SESSION, which closes inside the client what a URL-keyed cache
// could not: the zero-config deployment addresses sessions as `127.0.0.1:{port}` and ports are
// recycled, so a shared key would hand one session the previous one's history. Derived from the
// doc store's key rather than spelled again — one rule for what identifies a session's storage.
let private historyCacheName () = persistenceKey () + "/events"

[<Emit("window.caches.open($0)")>]
let private openCache (name: string) : JS.Promise<obj> = jsNative

// `keys()` answers in insertion order, and insertion order is NOT log order: `put` of an
// address already kept deletes the entry and appends the new one, so an answer two tabs both
// fetched moves to the end of the enumeration. The replay orders by what the answers hold
// (`Client.EventFetch.replay`); this is a bag of addresses and promises nothing about their order.
[<Emit("$0.keys().then(rs => rs.map(r => r.url))")>]
let private cacheKeys (cache: obj) : JS.Promise<string array> = jsNative

[<Emit("$0.match($1).then(r => r ? r.text() : null)")>]
let private cacheRead (cache: obj) (url: string) : JS.Promise<string option> = jsNative

// A FRESH Response, never the one that came off the network: a response carrying
// `redirected = true` is a known trap in the Cache API, and re-wrapping also keeps the store
// free of anything about how the bytes were obtained.
[<Emit("$0.put($1, new Response($2, { headers: { 'content-type': 'application/x-ndjson; charset=utf-8' } })).catch(() => undefined)")>]
let private cacheWrite (cache: obj) (url: string) (body: string) : JS.Promise<unit> = jsNative

/// Register the worker that makes a cold open possible with no network (Plan 20).
///
/// Best-effort and deliberately unawaited-for-correctness: a client whose registration fails
/// (an insecure context, a browser that refuses) is exactly today's client — it just cannot
/// open cold. Nothing above this waits on it, and nothing breaks if it never resolves.
/// Returns `unit`, and that is load-bearing rather than stylistic. As a promise-returning
/// emit whose result was discarded (`|> ignore`), the whole call was dead code to the
/// compiler and never reached the bundle at all — the registration silently did not ship,
/// which looks exactly like a worker that will not take control. A unit-returning emit is a
/// statement, and statements survive.
[<Emit("""void (navigator.serviceWorker && navigator.serviceWorker.register($0).catch(() => undefined))""")>]
let private registerWorker (url: string) : unit = jsNative

/// Ask for the store to be kept. A request, not a guarantee — granted for an engaged site on
/// Chrome, essentially only for an installed app on Safari — and best-effort by design: the
/// answer changes nothing this client does, it only changes how long what it kept survives.
///
/// Safari additionally caps script-writable storage at seven days without user interaction, and
/// that reaches the Cache API — so a granted request is not the end of it, and the session
/// nobody has opened in a week is the one this store is most likely to have lost.
[<Emit("(navigator.storage && navigator.storage.persist) ? navigator.storage.persist().catch(() => false) : Promise.resolve(false)")>]
let private requestPersistence () : JS.Promise<bool> = jsNative

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

[<Emit("window.caches.keys()")>]
let private cacheNames () : JS.Promise<string array> = jsNative

// The line an answer starts on, kept BESIDE the bytes rather than parsed back out of the
// address: a transcript line cannot carry its own index, and the address is the one thing this
// client is never allowed to read meaning out of. It rides a header on the stored `Response`,
// which the Cache API round-trips for nothing.
[<Emit("""$0.put($1, new Response($3, { headers: { 'content-type': 'application/x-ndjson; charset=utf-8', 'x-yession-first-seq': String($2) } })).catch(() => undefined)""")>]
let private transcriptWrite (cache: obj) (url: string) (firstSeq: int) (body: string) : JS.Promise<unit> = jsNative

// `null` for an entry that is gone, and for one written without the header — which no build
// that shipped this ever wrote, but a store outlives the build that filled it.
[<Emit("""(function (cache, url) { return (
cache.match(url).then(async r => {
  if (!r) return null
  const first = r.headers.get('x-yession-first-seq')
  if (first === null) return null
  return [parseInt(first, 10), await r.text()]
})
) })($0, $1)""")>]
let private transcriptRead (cache: obj) (url: string) : JS.Promise<(int * string) option> = jsNative

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
                                  Read = fun url -> transcriptRead cache url |> Async.AwaitPromise
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

[<Emit("""(function (ms, register) { return (
new Promise(resolve => {
  let settled = false
  const finish = () => {
    if (settled) return
    settled = true
    window.removeEventListener('online', finish)
    if (timer !== null) clearTimeout(timer)
    resolve(true)
  }
  const timer = ms >= 0 ? setTimeout(finish, ms) : null
  window.addEventListener('online', finish)
  register(finish)
})
) })($0, $1)""")>]
let private waitOrPoke (ms: float) (register: (unit -> unit) -> unit) : JS.Promise<bool> = jsNative

let private waitBeforeRetry (delay: System.TimeSpan option) : Async<bool> =
    async {
        let ms =
            match delay with
            | Some d -> d.TotalMilliseconds
            | None -> -1.0
        return!
            waitOrPoke ms (fun finish -> pokeRetry <- finish)
            |> Async.AwaitPromise
    }

[<Emit("Math.random()")>]
let private jsRandom () : float = jsNative

let private mintId (prefix: string) =
    sprintf "%s-%d" prefix (int (jsRandom () * 1000000000.0))

// The peer id is STABLE per browser profile (Plan 07): minted once, kept in
// localStorage under a browser-wide key (not per session — it names the browser, the
// same human across sessions), so colours, draft slots, and peer-scoped secrets survive
// reloads. Storage denied (private mode) falls back to the per-load mint.
//
// `$0` is substituted TEXTUALLY, so the argument expression must be bound to a const
// once: with `$0` written three times, the argument (a fresh random mint) evaluated
// three times, and a first visit stored one id while returning a different one. The
// returned id rode the login bounce and was witnessed; the stored id — the one every
// later load reads — was not, so every peer-scoped call (the whole connections surface)
// was denied for the life of the launch.
[<Emit("""(function (minted) {
  try {
    const key = 'yession/peer-id'
    const existing = window.localStorage.getItem(key)
    if (existing) return existing
    window.localStorage.setItem(key, minted)
    return minted
  } catch { return minted }
})($0)""")>]
let private persistentPeerId (minted: string) : string = jsNative

[<Emit("encodeURIComponent($0)")>]
let private urlEncode (value: string) : string = jsNative

// --- Claude connection panel round-trips (Plan 08) --------------------------------------
// Thin fetches against the session's /claude* routes; the same-origin auth cookie rides
// each one, and IS the whole identity — the browser asserts nothing about who it is.
// Failures land as `ok: false` with the response text — the panel shows it.
//
// These used to carry the peer id, and the credential was owned by it. A peer id lives in
// origin-partitioned localStorage, so it changed under the person holding it and stranded
// the credential behind every new one; ownership now comes off the cookie, Manager-side.

// A connection arrives as `{kind, signInRequired}` or null, and is flattened to primitives
// HERE rather than carried across as an object. Fable's mapping of an option-of-record onto
// a JS value is the kind of thing that misbehaves quietly, and a status that silently
// decodes to "nothing connected" is indistinguishable on screen from the truth. Two nullable
// strings per scope cannot go wrong, and `ConnectionView` is assembled in F#.
//
// The catalogue on the same reply crosses as the JSON TEXT of the list, for the same
// reason and one more: it is decoded by the codec the server encoded it with, so the
// browser reads one wire shape rather than two, and a row it could not decode is a
// reason to show rather than a silently shorter menu.
[<Emit("""fetch($0, { cache: 'no-store' })
  .then(r => r.ok ? r.json().then(s => ({ ok: true,
    sessionKind: s.session ? String(s.session.kind || '') : null,
    sessionSignIn: (s.session && s.session.signInRequired) || null,
    mineKind: s.mine ? String(s.mine.kind || '') : null,
    mineSignIn: (s.mine && s.mine.signInRequired) || null,
    owner: s.owner, agent: !!s.agent,
    models: s.models ? JSON.stringify(s.models) : null,
    modelsUnavailable: s.modelsUnavailable || null }))
    : Promise.resolve({ ok: false, sessionKind: null, sessionSignIn: null, mineKind: null, mineSignIn: null, owner: null, agent: false, models: null, modelsUnavailable: null }))
  .catch(() => ({ ok: false, sessionKind: null, sessionSignIn: null, mineKind: null, mineSignIn: null, owner: null, agent: false, models: null, modelsUnavailable: null }))""")>]
let private fetchClaudeStatusAt (url: string) : JS.Promise<{| ok: bool; sessionKind: string option; sessionSignIn: string option; mineKind: string option; mineSignIn: string option; owner: string option; agent: bool; models: string option; modelsUnavailable: string option |}> = jsNative

/// One scope's pair of nullable strings, as the panel's row reads it.
let private viewOf (kind: string option) (signInRequired: string option) : ConnectionView option =
    kind |> Option.map (fun kind -> { Kind = kind; SignInRequired = signInRequired })

let private fetchClaudeStatus () =
    fetchClaudeStatusAt (SessionRoute.relative ClaudeStatus)

/// `status` rides beside `ok` because a panel that only knows THAT a post failed cannot tell
/// a refusal from a session it could not reach, and those end a sign-in flow differently
/// (`GitHubFlow.ended`). `0` is a fetch that never answered.
[<Emit("""fetch($0, { method: 'POST', headers: { 'content-type': 'application/json' }, body: $1 })
  .then(async r => ({ ok: r.ok, status: r.status, body: await r.text() }))
  .catch(e => ({ ok: false, status: 0, body: String((e && e.message) || e) }))""")>]
let private postClaude (url: string) (body: string) : JS.Promise<{| ok: bool; status: int; body: string |}> = jsNative

[<Emit("JSON.stringify({ scope: $0, code: $1 || undefined, token: $2 || undefined })")>]
let private claudeBody (scope: string) (code: string) (token: string) : string = jsNative

[<Emit("(() => { try { return JSON.parse($0).authorizeUrl || '' } catch { return '' } })()")>]
let private parseAuthorizeUrl (body: string) : string = jsNative

[<Emit("(document.querySelector($0)?.value || '')")>]
let private panelInput (selector: string) : string = jsNative

// --- GitHub connection panel round-trips (Plan 14) ---------------------------------------
// Same fetch shapes as the Claude panel's; the flow differs (device code) so the two
// extra parsers below read the begin/poll replies.

[<Emit("""fetch($0, { cache: 'no-store' })
  .then(r => r.ok ? r.json().then(s => ({ ok: true,
    sessionKind: s.session ? String(s.session.kind || '') : null,
    sessionSignIn: (s.session && s.session.signInRequired) || null,
    mineKind: s.mine ? String(s.mine.kind || '') : null,
    mineSignIn: (s.mine && s.mine.signInRequired) || null }))
    : Promise.resolve({ ok: false, sessionKind: null, sessionSignIn: null, mineKind: null, mineSignIn: null }))
  .catch(() => ({ ok: false, sessionKind: null, sessionSignIn: null, mineKind: null, mineSignIn: null }))""")>]
let private fetchGitHubStatusAt (url: string) : JS.Promise<{| ok: bool; sessionKind: string option; sessionSignIn: string option; mineKind: string option; mineSignIn: string option |}> = jsNative

let private fetchGitHubStatus () =
    fetchGitHubStatusAt (SessionRoute.relative GitHubStatus)

[<Emit("JSON.stringify({ scope: $0, token: $1 || undefined })")>]
let private githubBody (scope: string) (token: string) : string = jsNative

[<Emit("(function (body) { try { const o = JSON.parse(body); return { userCode: o.userCode || '', verificationUri: o.verificationUri || '', interval: o.interval || 5 } } catch { return { userCode: '', verificationUri: '', interval: 5 } } })($0)")>]
let private parseDeviceBegin (body: string) : {| userCode: string; verificationUri: string; interval: int |} = jsNative

[<Emit("(function (body) { try { const o = JSON.parse(body); return { status: o.status || '', interval: o.interval || 0 } } catch { return { status: '', interval: 0 } } })($0)")>]
let private parseDevicePoll (body: string) : {| status: string; interval: int |} = jsNative

// --- The read surface's stream (Plan 15) --------------------------------------------------
// `EventSource` rather than the repo's fetch-based SSE reader: it is the browser's own SSE
// client, it reconnects on its own, and it carries the session cookie same-origin — which
// is the whole authentication story for a route that is cookie-gated.
//
// ONE connection carries every query. It is opened once at start and never closed: there
// is nothing to re-probe on, because a value arrives when it changes rather than when
// somebody looks.

[<Emit("(function (url, onFrame) { const es = new EventSource(url); es.onmessage = e => onFrame(e.data); return es })($0, $1)")>]
let private openQueryStream (url: string) (onFrame: string -> unit) : obj = jsNative

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

        // The Claude connection panel's round-trips (Plan 08). Status is polled: once
        // after connect-probe, after every action, and every few seconds while a flow
        // awaits its callback (completion happens in the claude.ai tab, landing at the
        // Manager — this tab learns of it only by asking).
        let refreshClaude () =
            Async.StartImmediate (
                async {
                    let! status = fetchClaudeStatus () |> Async.AwaitPromise
                    if status.ok then
                        dispatchRef (
                            ClaudeStatusMsg
                                { SessionCredential = viewOf status.sessionKind status.sessionSignIn
                                  MineCredential = viewOf status.mineKind status.mineSignIn
                                  Owner = status.owner
                                  AgentAvailable = Some status.agent })
                        // The picker's supply, off the same reply — so it can never be a
                        // statement about a credential the panel beside it has moved on
                        // from. It had a probe of its own with one trigger against this
                        // one's four, and the sign-in flow (which runs with the drawer
                        // already open) fired the four.
                        match status.models, status.modelsUnavailable with
                        | Some raw, _ ->
                            match Codec.fromString Codec.modelCatalogue raw with
                            | Ok models -> dispatchRef (ModelCatalogueMsg (ModelsLoaded models))
                            | Error reason -> dispatchRef (ModelCatalogueMsg (ModelsUnavailable reason))
                        | None, Some reason -> dispatchRef (ModelCatalogueMsg (ModelsUnavailable reason))
                        // Neither: an older session process, answering the status alone.
                        // What the picker already knows is better than blanking it.
                        | None, None -> ()
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
        let claudeAction (run: unit -> Async<Result<string option, string>>) (scope: string) =
            // One shape for every panel action: busy → run → error or refreshed status
            // (and into awaiting-code when the action returned an authorize URL).
            dispatchRef (ClaudeFlowMsg ClaudeBusy)
            Async.StartImmediate (
                async {
                    match! run () with
                    | Error reason -> dispatchRef (ClaudeFlowMsg (ClaudeError reason))
                    | Ok (Some authorizeUrl) ->
                        dispatchRef (ClaudeFlowMsg (ClaudeAwaitingCode (authorizeUrl, scope)))
                        pollClaudeWhileAwaiting ()
                    | Ok None ->
                        dispatchRef (ClaudeFlowMsg ClaudeIdle)
                        refreshClaude ()
                })
        let postClaudeAction (route: string) (scope: string) (code: string) (token: string) (expectUrl: bool) =
            claudeAction
                (fun () ->
                    async {
                        let! reply = postClaude route (claudeBody scope code token) |> Async.AwaitPromise
                        if not reply.ok then return Error reply.body
                        elif expectUrl then
                            match parseAuthorizeUrl reply.body with
                            | "" -> return Error "no authorize url in the reply"
                            | url -> return Ok (Some url)
                        else return Ok None
                    })
                scope

        // The GitHub panel's round-trips (Plan 14). Device flow: begin puts the user
        // code on screen, then this tab drives the session's poll at GitHub's stated
        // interval until the grant lands (a status probe then flips the flow to idle),
        // the human cancels, or the flow dies.
        let refreshGitHub () =
            Async.StartImmediate (
                async {
                    let! status = fetchGitHubStatus () |> Async.AwaitPromise
                    if status.ok then
                        dispatchRef (
                            GitHubStatusMsg
                                { SessionCredential = viewOf status.sessionKind status.sessionSignIn
                                  MineCredential = viewOf status.mineKind status.mineSignIn })
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
                            postClaude
                                (SessionRoute.relative (GitHub GitHubAction.Poll))
                                (githubBody scope "")
                            |> Async.AwaitPromise
                        if not reply.ok then
                            // A poll that failed is not necessarily a flow that ended. Only the
                            // session's own 4xx says this one is over; a 5xx or a fetch that
                            // never answered is a bad moment, and the code on screen — which the
                            // human may already have approved — is still good.
                            if GitHubFlow.ended reply.status then
                                dispatchRef (GitHubFlowMsg (GitHubError reply.body))
                            else pollGitHubWhileAwaiting ()
                        else
                            let outcome = parseDevicePoll reply.body
                            match outcome.status with
                            | "connected" -> refreshGitHub ()
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
                (SessionRoute.relative SessionRoute.Queries)
                (fun data ->
                    match Codec.fromString Codec.queryFrame data with
                    | Ok frame -> dispatchRef (QueryFrameMsg frame)
                    | Error _ -> ())
            |> ignore

        let githubAction (run: unit -> Async<Result<GitHubFlowState option, string>>) =
            dispatchRef (GitHubFlowMsg GitHubBusy)
            Async.StartImmediate (
                async {
                    match! run () with
                    | Error reason -> dispatchRef (GitHubFlowMsg (GitHubError reason))
                    | Ok (Some flow) ->
                        dispatchRef (GitHubFlowMsg flow)
                        pollGitHubWhileAwaiting ()
                    | Ok None ->
                        dispatchRef (GitHubFlowMsg GitHubIdle)
                        refreshGitHub ()
                })

        // The copied mark is a moment, so it is one deadline: re-armed by each copy, and the
        // one it replaces is cleared. Two live timers over one slot would let the first
        // copy's deadline take the second copy's confirmation off the screen.
        let mutable copiedTimer = 0.0

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
              ReportTitleSelection =
                fun sel ->
                    // The title lives in the `title` Y.Text root; turn the input's char offsets
                    // into relative positions over it, so a title caret survives concurrent edits
                    // exactly like a body one. rAF-throttled through the same path as bodies.
                    let focus =
                        sel |> Option.map (fun (anchor, head) ->
                            let title = box (doc.getText "title")
                            let enc i = ProseMirror.relPosFromTypeIndex title i |> ProseMirror.encodeRel
                            { Field = Title; Pos = { Anchor = enc anchor; Head = enc head } })
                    sendFocus focus
              ClaudeConnect =
                fun () ->
                    let scope = match panelInput "[data-claude-scope]" with "" -> "mine" | s -> s
                    postClaudeAction (SessionRoute.relative (Claude ClaudeAction.Begin)) scope "" "" true
              ClaudeComplete =
                fun () ->
                    // The scope selector is unmounted while awaiting; the flow carries it.
                    let scope =
                        match latestModel.Claude.Flow with
                        | ClaudeAwaitingCode (_, scope) -> scope
                        | _ -> "mine"
                    match panelInput "[data-claude-code]" with
                    | "" -> dispatchRef (ClaudeFlowMsg (ClaudeError "paste the code first"))
                    | code -> postClaudeAction (SessionRoute.relative (Claude ClaudeAction.Complete)) scope code "" false
              ClaudePasteToken =
                fun () ->
                    match panelInput "[data-claude-token]" with
                    | "" -> dispatchRef (ClaudeFlowMsg (ClaudeError "paste a token first"))
                    | token ->
                        postClaudeAction
                            (SessionRoute.relative (Claude ClaudeAction.Token))
                            (match panelInput "[data-claude-scope]" with "" -> "mine" | s -> s)
                            ""
                            token
                            false
              ClaudeDisconnect =
                fun scope -> postClaudeAction (SessionRoute.relative (Claude ClaudeAction.Disconnect)) scope "" "" false
              GitHubConnect =
                fun () ->
                    let scope = match panelInput "[data-github-scope]" with "" -> "mine" | s -> s
                    githubAction (fun () ->
                        async {
                            let! reply =
                                postClaude
                                    (SessionRoute.relative (GitHub GitHubAction.Begin))
                                    (githubBody scope "")
                                |> Async.AwaitPromise
                            if not reply.ok then return Error reply.body
                            else
                                let began = parseDeviceBegin reply.body
                                match began.userCode with
                                | "" -> return Error "no device code in the reply"
                                | _ -> return Ok (Some (GitHubAwaitingApproval (began.userCode, began.verificationUri, scope, began.interval)))
                        })
              GitHubPasteToken =
                fun () ->
                    match panelInput "[data-github-token]" with
                    | "" -> dispatchRef (GitHubFlowMsg (GitHubError "paste a token first"))
                    | token ->
                        let scope = match panelInput "[data-github-scope]" with "" -> "mine" | s -> s
                        githubAction (fun () ->
                            async {
                                let! reply =
                                    postClaude
                                        (SessionRoute.relative (GitHub GitHubAction.Token))
                                        (githubBody scope token)
                                    |> Async.AwaitPromise
                                if not reply.ok then return Error reply.body else return Ok None
                            })
              Copy =
                fun key text ->
                    writeClipboard text (fun written ->
                        // Only a write that HAPPENED is confirmed. A refused clipboard leaves
                        // the box showing the value, which is what a person falls back to
                        // reading — a "copied" over an empty clipboard would send them to the
                        // other tab with nothing to paste.
                        if written then
                            if copiedTimer <> 0.0 then Render.clearTimeoutJs copiedTimer
                            dispatchRef (CopiedMsg (Some key))
                            copiedTimer <-
                                Render.setTimeoutJs
                                    (fun () ->
                                        copiedTimer <- 0.0
                                        dispatchRef (CopiedMsg None))
                                    copiedShownMs)
              GitHubDisconnect =
                fun scope ->
                    githubAction (fun () ->
                        async {
                            let! reply =
                                postClaude
                                    (SessionRoute.relative (GitHub GitHubAction.Disconnect))
                                    (githubBody scope "")
                                |> Async.AwaitPromise
                            if not reply.ok then return Error reply.body else return Ok None
                        })
              OpenTerminal = fun title -> connectionRef |> Option.iter (fun c -> c.OpenTerminal title)
              ApproveRepoCapabilities =
                fun repo granted -> connectionRef |> Option.iter (fun c -> c.ApproveRepoCapabilities repo granted)
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
              FocusItemActions = fun id -> PaneShell.toItemActions (MessageId.value id) }

        let el = appRoot ()
        // Take over the server-rendered shell (see `clearChildren`): from here Lit owns it.
        clearChildren el

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
        let setState (model: ClientModel) (dispatch: Ylmish.Program.Message<ClientMsg> -> unit) =
            dispatchRef <- fun msg -> dispatch (Ylmish.Program.Message.User msg)
            latestModel <- model
            renderer.SetState model

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
        let persistence = newPersistence indexeddbPersistence (persistenceKey ()) doc
        do! whenSynced persistence |> Async.AwaitPromise

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
        registerWorker (SessionRoute.relative ServiceWorker)

        let! historyCache = openHistoryCache ()
        let! transcriptCaches = openTranscriptCaches ()
        do! Client.EventFetch.replay historyCache (fun msg -> dispatchRef msg)
        // After the events, never beside them: a terminal exists because an event said so, and
        // records folded before that event has been folded have nowhere to land.
        do! Client.TranscriptFetch.replay transcriptCaches (fun msg -> dispatchRef msg)

        // Authorization by renavigation: probe `/me` for a peer token. 401 -> bounce
        // through `/login` (code + PKCE via the Manager) and land back on this shell,
        // where the probe succeeds. A NETWORK failure (offline, session down) is a
        // `Disconnected` with its reason, not silence: the local-first shell — IndexedDB doc
        // plus the event ranges in this client's own store — stays fully usable, and the model
        // says why it is alone.
        // Said before it is asked: the model starts `Disconnected None`, which renders as
        // "not connected" with no reason and nothing to press, and until the probe settled
        // that is what a page wore — for a hundred milliseconds on a laptop, and for as long
        // as a hung fetch took on a phone. `Connecting` is the truth of the interval (it is
        // what the channel's own retries wear, `Client.SessionChannel.policy`), and the
        // deadline is what bounds it.
        dispatchRef ConnectingMsg
        let! probe = fetchMe (SessionRoute.relative Me) Client.Probe.deadline.TotalMilliseconds |> Async.AwaitPromise
        if not probe.reachable then
            dispatchRef (ConnectFailedMsg (Client.ChannelFault.describe (Client.ChannelUnreachable probe.detail)))
        elif not probe.authorized then
            // The peer id rides the login bounce so the Manager can witness which peer
            // signed in for this session (Plan 07 — peer-scoped secrets).
            navigateTo (SessionRoute.relative Login + "?peer_id=" + urlEncode (PeerId.value peerId))
        else
            // Authenticated: the Claude panel's status is knowable now, and the read
            // surface's stream has a cookie that will be accepted.
            refreshClaude ()
            subscribeQueries ()
            let hello =
                { PeerId = peerId
                  DisplayName = displayName
                  Token = probe.token }
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
                Client.EventFetch.overHttp (Client.EventFetch.storing historyCache httpGet) SessionRoute.relative None
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
                Client.TranscriptFetch.overHttp transcriptCaches httpGet SessionRoute.relative None
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
                    (fun () -> connectChannel (absolute (SessionRoute.relative Signal)))

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
