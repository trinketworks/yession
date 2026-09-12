module Yession.Browser.EditorHarness

// A host-free browser harness for the rich-editor E2E. Mounts a ProseMirror editor on a fresh
// Yjs fragment into `#host` and exposes its serialized Markdown as `window.__md`. There is NO
// Session Process, NO WebRTC and NO native addon here — just the editor and a doc — so the
// editor-rendering E2E (`Tag.needs [Browser]`) runs wherever Chromium exists, decoupled from
// `node-datachannel`. Pure F# (the no-authored-JS invariant holds); Fable-compiles alongside
// the app browser entry, and the `Browser`-cap test build esbuilds it into the served bundle.
//
// It also exercises presence cursors headlessly-but-in-a-browser: the editor reports its own
// selection (base64 relative anchor/head) through `reportFocus`; `window.__pushRemote(name)`
// feeds that same selection back in as a *remote* peer's cursor, so the on-screen decorations
// (the caret widget + label and the selection highlight) render for the E2E to assert on.
//
// Module-level `do` runs on import — module scripts are deferred, so `#host` already exists.

open Fable.Core
open Lit
open Yjs
open Yession.Domain
open Yession.Domain.Agent
open Yession.Domain.Link
open Yession.Domain.Terminals
open Yession.Domain.Collab
open Yession.Domain.Chat
open Yession.App

[<Emit("document.getElementById('host')")>]
let private host : obj = jsNative

/// The replay mount (Plan 13, stage 3e). It shares this page rather than getting one of its
/// own because it is the same KIND of thing — a host-free surface that needs a real browser
/// and nothing else — and a second harness would be a second bundle, a second page and a
/// second static server for one `create` call.
[<Emit("document.getElementById('replay')")>]
let private replayHost : Browser.Types.Element = jsNative

[<Emit("document.getElementById('replay-gappy')")>]
let private gappyReplayHost : Browser.Types.Element = jsNative

[<Emit("(function(f){ window.__md = f; })($0)")>]
let private exposeMd (f: unit -> string) : unit = jsNative

[<Emit("(function(f){ window.__pushRemote = f; })($0)")>]
let private exposePush (f: string -> unit) : unit = jsNative

/// How many times Enter has asked to send. The harness mounts the editor exactly as the
/// COMPOSER does (`onSubmit` supplied), so the E2E drives the real binding: Enter sends and
/// inserts nothing, Alt+Enter is the new line. A counter rather than a callback because what
/// the test needs to know is "did it fire", and the send itself belongs to the app.
[<Emit("(function(n){ window.__sends = n; })($0)")>]
let private exposeSends (n: int) : unit = jsNative

let private doc = Y.Doc.Create ()
let private fragment = doc.getXmlFragment "body"

do
    // The editor reports its local selection here; keep the latest so the harness can replay it
    // as a remote peer's cursor on demand.
    let mutable lastSelection : (string * string) option = None
    let mutable sends = 0
    exposeSends 0
    let handle =
        Editor.mountEditor
            host
            fragment
            false
            (fun sel -> lastSelection <- sel)
            (Some (fun () ->
                sends <- sends + 1
                exposeSends sends))
            // The harness IS the composer, so it wears the composer's prompt: the browser
            // tier can then read the placeholder where the editor really draws it.
            Dom.Text.composerPlaceholder
    exposeMd (fun () -> Markdown.ofFragment fragment)
    exposePush (fun name ->
        match lastSelection with
        | Some (anchor, head) ->
            handle.PushPresences
                [ ({ Colour = "hsl(200, 70%, 55%)"
                     Selection = "hsla(200, 70%, 55%, 0.25)"
                     Name = name
                     Anchor = anchor
                     Head = head } : Editor.RemoteBodyCursor) ]
        | None -> ())

    // The replay, mounted from a `.cast` rebuilt by the very function the client uses. This
    // is the one part of stage 3e no DOM-free test can reach: whether `asciinema-player`'s
    // named export actually resolves through the bundle and renders the recording.
    let cast =
        TranscriptReplay.castWithMarkers
            { Width = 80; Height = 24; Timestamp = 0L }
            [ 0, { At = 0.0; Kind = TranscriptInput; Data = "ls -la\r\n" }
              1, { At = 0.1; Kind = TranscriptOutput; Data = "total 0\r\n" } ]
            [ 0.0, "ls -la" ]
    // The chapter rides IN the cast (Plan 25, stage 1) — `castWithMarkers` above — because
    // that is how the client builds one, and whether chapters survive the bundle is the same
    // question the import itself is. A `startAt` is deliberately absent here: it would skip
    // past the very frame the replay assertion waits for.
    Replay.mount
        replayHost
        { Cast = cast
          StartAt = None
          Poster = None
          BehindLive = None }
        None
    |> ignore

    // The same machinery over a recording with DEAD AIR in it, which is the shape the pane
    // actually replays and the one the mount above cannot fail in: thirty seconds of nothing
    // between the first command and the second, a chapter on the far side of the gap, and a
    // start position naming it.
    //
    // Under the player's `markers` option all three disagree — the option list stays on the
    // recording's raw clock while the events are idle-compressed as they load, so the chapter
    // past the gap becomes the last event (and is dropped from the chapter list by its
    // `time < duration` filter), the duration reads uncompressed, and `startAt` lands short.
    // With the chapters written into the cast they are all one clock, and a reader reaches
    // the second command in about as long as it takes to press play.
    let gappy =
        TranscriptReplay.castWithMarkers
            { Width = 80; Height = 24; Timestamp = 0L }
            [ 0, { At = 0.0; Kind = TranscriptInput; Data = "echo first\r\n" }
              1, { At = 0.1; Kind = TranscriptOutput; Data = "first\r\n" }
              2, { At = 30.0; Kind = TranscriptInput; Data = "echo second\r\n" }
              3, { At = 30.1; Kind = TranscriptOutput; Data = "second\r\n" } ]
            [ 0.0, "echo first"
              30.0, "echo second" ]
    Replay.mount
        gappyReplayHost
        { Cast = gappy
          StartAt = Some 30.0
          Poster = None
          BehindLive = None }
        None
    |> ignore

// --- Two peers converging on one body (the caret write-back) -----------------------------
//
// What only a browser can answer about remote carets, and what no cheap test could reach
// before this: whether pushing presence DECORATIONS while content is still arriving stops the
// content arriving at all.
//
// The reason it needs saying at all. `PushPresences` dispatches a ProseMirror transaction
// carrying no steps — but ProseMirror runs every plugin's `view.update` on every state update,
// and `ySyncPlugin`'s hook is not gated on `docChanged`: it calls `_prosemirrorChanged`, which
// walks the WHOLE document and reconciles it back into Yjs. The binding exempts its own
// dispatches by holding a mutex across them; an unsolicited one holds nothing. So a caret push
// is a write, and a caret push during convergence is a write that races the content.
//
// This surface is that race, reduced: two docs, two editors, relayed to each other IN THIS
// PAGE. No WebRTC, no Session Process, no native addon — so it runs in the `Browser` tier
// wherever Chromium exists, in seconds. The two-peer WebRTC E2E can also see this bug, but
// only under enough load to lose the race, which cost two runs of the gate to learn once.

[<Emit("document.getElementById('peer-a')")>]
let private peerAHost : obj = jsNative

[<Emit("document.getElementById('peer-b')")>]
let private peerBHost : obj = jsNative

/// Start or stop pushing presence decorations into the MIRROR on every animation frame.
[<Emit("(function(f){ window.__caretStorm = f; })($0)")>]
let private exposeStorm (f: bool -> unit) : unit = jsNative

/// How many frames the storm has actually pushed on. Anti-vacuity: a convergence assertion
/// passes trivially if the storm never ran, and "never ran" and "ran and was harmless" look
/// identical from the outside.
[<Emit("(function(n){ window.__caretPushes = n; })($0)")>]
let private exposeCaretPushes (n: int) : unit = jsNative

[<Emit("requestAnimationFrame(() => $0())")>]
let private onFrame (f: unit -> unit) : unit = jsNative

/// Count the Yjs updates a doc takes from `ySyncPlugin`'s OWN write-back — the ones whose
/// origin is `ySyncPluginKey`, which is what `_prosemirrorChanged` tags its transaction with.
///
/// This is the instrument the caret question turns on, and it exists because the write-back
/// is otherwise completely silent: it walks the whole document, it can emit CRDT operations,
/// and it leaves no trace anywhere that a test could read. Counting from OUR side rather than
/// patching the library keeps it honest — the number is real doc updates, not a hook we hoped
/// was called.
/// Counts BOTH, and the second one is what makes the first believable: `__docUpdates` is every
/// update this doc took, `__writebacks` only those the write-back produced. A write-back count
/// of zero means "drawing a caret wrote nothing" only if the doc was moving at all — otherwise
/// it means the observer was never wired up, and the two look identical from a test.
[<Emit("""(function (doc, key) {
  let all = 0, back = 0
  window.__docUpdates = 0
  window.__writebacks = 0
  doc.on('update', function (_update, origin) {
    all++; window.__docUpdates = all
    if (origin === key) { back++; window.__writebacks = back }
  })
})($0, $1)""")>]
let private countWritebacks (doc: Y.Doc) (syncKey: obj) : unit = jsNative

[<Import("ySyncPluginKey", "y-prosemirror")>]
let private ySyncPluginKey : obj = jsNative

[<Emit("(function(f){ window.__convState = f; })($0)")>]
let private exposeConvState (f: unit -> string) : unit = jsNative

/// The two docs' content and the two editors' rendered text, side by side. `docA`/`docB` are
/// what the CRDT holds; `pmA`/`pmB` are what each editor actually put on screen. A gap between
/// a doc and its own editor is a binding that stopped rendering; a gap between the two docs is
/// a relay that stopped carrying.
[<Emit("""JSON.stringify({
  docA: $0, docB: $1, caretPushes: $2,
  writebacks: window.__writebacks,
  pmA: document.querySelector('#peer-a .ProseMirror')?.textContent ?? null,
  pmB: document.querySelector('#peer-b .ProseMirror')?.textContent ?? null
})""")>]
let private convStateJson (docA: string) (docB: string) (pushes: int) : string = jsNative

// --- The performance surface's interop ---------------------------------------------------

[<Emit("performance.now()")>]
let private now () : float = jsNative

/// Resolve on the next animation frame — the browser saying "I have painted". Every latency
/// here is measured against this and nothing else.
[<Emit("new Promise(r => requestAnimationFrame(() => r()))")>]
let private nextFrame () : JS.Promise<unit> = jsNative

/// Every keystroke reaching `host`, timed from the browser's OWN event timestamp to the frame
/// it paints on. `event.timeStamp` shares `performance.now()`'s time origin, so the difference
/// is real input-to-paint including the browser's dispatch — which is what a person means by
/// "does typing feel instant".
///
/// Deliberately NOT the Event Timing API: `PerformanceEventTiming.duration` is rounded to 8ms
/// for privacy. That is a fine threshold for judging one interaction and useless for watching
/// a trend move, which is this suite's whole job.
///
/// Capturing (`true`), so a keystroke is timed from before the editor sees it.
///
/// It also keeps a per-burst DIAGNOSTIC (`window.__benchDiagState`), because the `type` series
/// can only come up short two ways and a bare `collected 3 samples` says neither: a keydown that
/// never reached the co-editor (focus elsewhere), or a sample frame that had not fired by the
/// time the driver read the series. `docKeydowns` counts every keydown the page saw (a
/// document-level capture, which fires even when focus left the editor); `hostKeydowns` counts
/// only those that reached this host; `rafs` counts the sample frames that had fired at read
/// time; and `focus` records where `activeElement` sat for each key, so a burst that started in
/// the editor and drifted out says exactly when. This is the instrument the size-200 flake
/// turned on: it showed the keystrokes always landed (`hostKeydowns` 32, focus never leaving)
/// while `rafs` swung from 32 down to 0 — the frames were pending, not the keys missing.
[<Emit("""(function (host, take) {
  window.__benchDiagState = { hostKeydowns: 0, docKeydowns: 0, rafs: 0, focus: [] }
  document.addEventListener('keydown', function () {
    var d = window.__benchDiagState, a = document.activeElement
    d.docKeydowns++
    if (d.focus.length < 60)
      d.focus.push(!a ? 'none' : (a.closest && a.closest('#peer-b')) ? 'peer-b' : a.id ? '#' + a.id : a.tagName.toLowerCase())
  }, true)
  host.addEventListener('keydown', function (e) {
    window.__benchDiagState.hostKeydowns++
    requestAnimationFrame(function () {
      window.__benchDiagState.rafs++
      take(performance.now() - e.timeStamp)
    })
  }, true)
})($0, $1)""")>]
let private onKeystrokePainted (host: obj) (take: float -> unit) : unit = jsNative

/// Clear the typing diagnostic for a fresh burst — reset in place so the listeners above keep
/// counting into the same object. Called by `__benchReset`, beside the sample arrays.
[<Emit("""(function () {
  var d = window.__benchDiagState
  if (d) { d.hostKeydowns = 0; d.docKeydowns = 0; d.rafs = 0; d.focus = [] }
})()""")>]
let private resetTypingDiagnostics () : unit = jsNative

/// The typing diagnostic as JSON, for the driver to print each burst and to quote when a series
/// comes up short.
[<Emit("JSON.stringify(window.__benchDiagState || null)")>]
let private typingDiagnostics () : string = jsNative

[<Emit("(function(f){ window.__benchDiag = f; })($0)")>]
let private exposeTypingDiag (f: unit -> string) : unit = jsNative

/// Two named number series as one JSON object — the shape every scenario returns.
[<Emit("JSON.stringify({ [$0]: $1, [$2]: $3 })")>]
let private twoSeries (a: string) (xs: float[]) (b: string) (ys: float[]) : string = jsNative

[<Emit("(function(f){ window.__benchSeed = f; })($0)")>]
let private exposeSeed (f: int -> JS.Promise<int>) : unit = jsNative

[<Emit("(function(f){ window.__benchCarets = f; })($0)")>]
let private exposeCarets (f: int -> JS.Promise<string>) : unit = jsNative

[<Emit("(function(f){ window.__benchReset = f; })($0)")>]
let private exposeBenchReset (f: unit -> unit) : unit = jsNative

[<Emit("(function(f){ window.__benchTyping = f; })($0)")>]
let private exposeTyping (f: unit -> string) : unit = jsNative

/// One named number series as JSON — `twoSeries` for a scenario that measures one thing.
[<Emit("JSON.stringify({ [$0]: $1 })")>]
let private oneSeries (a: string) (xs: float[]) : string = jsNative

[<Emit("(function(f){ window.__benchTranscript = f; })($0)")>]
let private exposeTranscript (f: int -> int -> int -> string) : unit = jsNative

[<Emit("(function(f){ window.__benchSettle = f; })($0)")>]
let private exposeSettle (f: unit -> JS.Promise<unit>) : unit = jsNative

/// Begin the scroll scenario: a conversation of `items`, `records` transcript records arriving
/// one every `everyMs`, and the frame clock and render clock running. The FLING is the driver's
/// to make, with real touch input, once this returns.
[<Emit("(function(f){ window.__benchScrollBegin = f; })($0)")>]
let private exposeScrollBegin (f: int -> int -> int -> unit) : unit = jsNative

/// How many records the stream has sent so far. The driver flings until the stream is spent,
/// so every size is measured over the same records rather than over however long one fling
/// through it happened to take.
[<Emit("(function(f){ window.__benchScrollSent = f; })($0)")>]
let private exposeScrollSent (f: unit -> int) : unit = jsNative

/// End it: stop the stream and the clocks, and hand back what they recorded.
[<Emit("(function(f){ window.__benchScrollEnd = f; })($0)")>]
let private exposeScrollEnd (f: unit -> string) : unit = jsNative

/// What the scroll scenario recorded, in the series shape the driver reads everywhere else,
/// plus the counts that say whether it measured anything: renders against records sent, and
/// where the scroll started and ended.
[<Emit("JSON.stringify({ frame: $0, render: $1, renders: $2, records: $3, scrolledFrom: $4, scrolledTo: $5 })")>]
let private scrollReport
    (frames: float[]) (renders: float[]) (rendersN: int) (recordsN: int) (scrolledFrom: float) (scrolledTo: float)
    : string = jsNative

/// Open a session the way the app opens one it has been to before — from what it kept — and
/// say what the page did between its first paint and its connection: `items` conversation
/// items' worth of events in the kept store, `perAnswer` events to each kept answer.
[<Emit("(function(f){ window.__benchOpen = f; })($0)")>]
let private exposeOpen (f: int -> int -> JS.Promise<string>) : unit = jsNative

/// Open a session the way the app opens one it has never seen — everything over the
/// network, `pageSize` events to a page, a page every `everyMs` — and say what the page did.
[<Emit("(function(f){ window.__benchOpenCold = f; })($0)")>]
let private exposeOpenCold (f: int -> int -> int -> JS.Promise<string>) : unit = jsNative

/// How many times the app has rendered, ever — `Render.countRender`'s own count, read back so
/// the open scenario counts the renders the APP made rather than a count of its own.
[<Emit("globalThis.__yessionRenders || 0")>]
let private appRenders () : int = jsNative

/// Whether an element is still in the document — the item under the eye last frame may have
/// been replaced by this one, and measuring a detached node's box says nothing.
[<Emit("$0.isConnected")>]
let private isConnected (el: Browser.Types.Element) : bool = jsNative

/// One frame's look at the conversation, as far as an eye can tell two frames apart: where
/// its box is, what is scrolled into it, how much is in it, and which item is under the
/// middle of it.
[<RequireQualifiedAccess>]
type private Look =
    { Top : float
      ScrollTop : float
      ScrollHeight : float
      Items : int
      Chars : int
      /// Everything on the page, not just the conversation: a chip changing from
      /// "connecting" to "connected" is a picture a person sees too.
      PageChars : int
      Anchor : Browser.Types.Element option }

/// A turn of the event loop — a task, not a microtask, so the page may paint in between.
/// Fixture stores answer through this because the Cache API answers that way, and the
/// asynchrony is not incidental: it is what separates the pictures a person sees on opening
/// (the empty shell, then the conversation, then the transcripts) — a store that answered
/// synchronously would fold everything into one frame and measure a page that never moved.
/// A message port rather than `setTimeout`, which the browser clamps to 4ms once nested.
[<Emit("new Promise(r => { const c = new MessageChannel(); c.port1.onmessage = () => r(); c.port2.postMessage(0) })")>]
let private nextTask () : JS.Promise<unit> = jsNative

/// What the open scenario recorded. Counts and distances, mostly, because what a person sees
/// on opening a session is not a latency: how many different pictures the page showed, how
/// far the words under their eye moved, and how long the page sat frozen.
[<Emit("JSON.stringify({ renders: $0, paints: $1, jumps: $2, jump: $3, blocked: $4, time: $5, items: $6, connection: $7, replaced: $8 })")>]
let private openReport
    (renders: int) (paints: int) (jumps: int) (jump: float) (blocked: float) (time: float)
    (items: int) (connection: string) (replaced: int)
    : string = jsNative

/// Markdown of roughly `chars` characters, as paragraphs rather than one enormous line: what
/// the reconciliation walks is NODES, so a document's structure is part of what is being
/// measured and a single block would flatter it.
let private filler (chars: int) : string =
    let sentence = "The quick brown fox jumps over the lazy dog. "
    let perPara = 200
    let one = (String.replicate (perPara / sentence.Length + 1) sentence).Substring (0, perPara)
    String.concat "\n\n" (List.replicate (max 1 (chars / perPara)) one)

/// Set by the bench wiring below, called by the relay. A mutable seam rather than a second
/// relay registration, because two listeners on one doc would make the ORDER of "carry it" and
/// "start the clock" something the reader has to guess at.
let mutable private relayObserved : unit -> unit = ignore

let private docA = Y.Doc.Create ()
let private docB = Y.Doc.Create ()

do
    // The relay: each doc's local updates become the other's remote ones, exactly as the
    // Session Process relays them between two browsers — minus the transport.
    DocSync.onLocalUpdate docA (fun payload -> DocSync.applyRemote docB payload) |> ignore
    DocSync.onLocalUpdate docB (fun payload ->
        relayObserved ()
        DocSync.applyRemote docA payload)
    |> ignore

    // Watch the MIRROR: it is the one whose carets are pushed, so it is the one whose
    // write-back would race the content arriving into it.
    countWritebacks docB ySyncPluginKey

    let fragmentA = docA.getXmlFragment "shared"
    let fragmentB = docB.getXmlFragment "shared"

    // BOTH editable, because that is the arrangement the bug appears in: a co-editor JOINS the
    // draft rather than watching it, so the second editor is a composer bound to the same body,
    // with its own binding writing back into its own doc. A read-only mount is the easier case
    // and proves less — it was the first thing this surface tried, and it converged happily.
    let mutable selectionA : (string * string) option = None
    let mutable selectionB : (string * string) option = None
    Editor.mountEditor peerAHost fragmentA false (fun sel -> selectionA <- sel) None "" |> ignore
    let mirror = Editor.mountEditor peerBHost fragmentB false (fun sel -> selectionB <- sel) None ""

    let mutable storming = false
    let mutable pushes = 0
    exposeCaretPushes 0
    // A's caret, drawn in B — decoded against B's own doc, so the positions are real rather
    // than replayed constants. Before A has reported a selection there is nothing to draw and
    // the frame still counts as a push: what is under test is the dispatch, not the geometry.
    let rec storm () =
        if storming then
            (match selectionA with
             | Some (anchor, head) ->
                 mirror.PushPresences
                     [ ({ Colour = "hsl(200, 70%, 55%)"
                          Selection = "hsla(200, 70%, 55%, 0.25)"
                          Name = "ada"
                          Anchor = anchor
                          Head = head } : Editor.RemoteBodyCursor) ]
             | None -> mirror.PushPresences [])
            pushes <- pushes + 1
            exposeCaretPushes pushes
            onFrame storm
    // What each side holds, as one JSON blob. A convergence failure has four candidate
    // stories — the author never wrote it, the relay never carried it, the co-editor's doc has
    // it but its editor never rendered it, or something wrote over it — and they are
    // indistinguishable from the DOM alone. This tells them apart in the failure message
    // instead of in a debugging session.
    exposeConvState (fun () ->
        convStateJson
            (Markdown.ofFragment fragmentA)
            (Markdown.ofFragment fragmentB)
            pushes)

    exposeStorm (fun on ->
        if on && not storming then
            storming <- true
            onFrame storm
        else storming <- on)

    // --- What a person waits for ----------------------------------------------------------
    //
    // Three latencies and one budget, over the same two peers. `check` proves behaviour and
    // says nothing about time, so a cost like y-prosemirror's whole-document reconciliation on
    // every caret push (`view.update` is not gated on `docChanged`) degrades silently until
    // somebody notices typing feels bad on a long message.
    //
    // Every measurement is the same primitive: mark a start, and let the browser say when it
    // painted. Three uses, one thing to get right.
    //
    // Sampled at several document SIZES by the driver, which is the whole point — the concern
    // is O(document), and a number taken at one size cannot show a complexity regression at
    // all. The size sweep is what turns four latencies into a slope.

    let typeSamples = ResizeArray<float> ()
    let receiveSamples = ResizeArray<float> ()

    // Local echo: the author's own keystroke, from the browser's event timestamp to the frame
    // it paints on. Peer B is the one the driver types into, so B is the one listened to.
    onKeystrokePainted peerBHost typeSamples.Add

    // The other half of the same keystroke: the co-editor's screen. Marked at the relay, which
    // is where an update "arrives" — everything after it (apply, render, paint) is what the
    // person on the other end is waiting through.
    relayObserved <- fun () ->
        let t0 = now ()
        onFrame (fun () -> receiveSamples.Add (now () - t0))

    exposeBenchReset (fun () ->
        typeSamples.Clear ()
        receiveSamples.Clear ()
        resetTypingDiagnostics ())

    exposeTyping (fun () ->
        twoSeries "type" (typeSamples.ToArray ()) "receive" (receiveSamples.ToArray ()))
    exposeTypingDiag typingDiagnostics

    // Drain this burst's pending sample frames before the driver reads the series. Every `type`
    // and `receive` sample lands in a `requestAnimationFrame` callback, and the driver reads the
    // instant `TypeAsync` returns — so on a busy frame (a large doc reconciling each keystroke,
    // or the cold first size) few or none have fired yet, and the series comes back short or
    // empty (`type@2000 collected 0 samples`, `rafs` anywhere from 0 to 32 across runs while the
    // keystrokes all landed). This resolves once the sample count has held steady across two
    // frames — every scheduled frame has fired — and it also stops a size's late frames leaking
    // into the next size's reset window. Bounded so it can never hang: a genuinely empty series
    // settles at zero and the driver's `< 5` guard still refuses it.
    exposeSettle (fun () ->
        async {
            let mutable last = -1
            let mutable stable = 0
            let mutable frames = 0
            while stable < 2 && frames < 180 do
                do! nextFrame () |> Async.AwaitPromise
                frames <- frames + 1
                let n = typeSamples.Count + receiveSamples.Count
                if n = last then
                    stable <- stable + 1
                else
                    stable <- 0
                    last <- n
        }
        |> Async.StartAsPromise)

    // What one render's worth of transcript reading costs.
    //
    // A pane draws every block of the terminal it is showing, and each block asks the feed for
    // its OWN range (`terminalBlockView` -> `TerminalFeed.outputText`). So the thing a person
    // waits through is the sum over blocks, and what matters about it is not any single call —
    // no single call ever looks slow — but how that sum grows with the transcript. `slice` used
    // to answer a range by materialising every record the terminal held and filtering it down,
    // which made the sum the transcript times the number of blocks: measured through this very
    // hook, 0.4ms at 400 records, 4.6ms at 1,500 and 70.2ms at 6,000, against 0.5 / 0.8 / 2.5
    // once the walk was bounded by the range — a slope of 175x where there is now one of 5x.
    //
    // Hence a SWEEP rather than a number, and hence measured here rather than on the .NET side
    // where the same F# is far easier to call: `Map` is Fable's implementation in the browser
    // and .NET's in a test, and it is the browser's cost that a person pays.
    //
    // Nothing is rendered — this is the read the render does, isolated, so a change in what the
    // pane draws cannot be mistaken for a change in what the transcript costs to read.
    exposeTranscript (fun records blockSize samples ->
        let feed =
            Seq.fold
                (fun f seq ->
                    TerminalFeed.withRecord seq { At = float seq; Kind = TranscriptOutput; Data = "line\n" } f)
                TerminalFeed.empty
                (seq { 0 .. records - 1 })
        let blocks = [ for b in 0 .. records / blockSize - 1 -> b * blockSize, (b + 1) * blockSize ]
        let taken = ResizeArray<float> ()
        for _ in 1 .. samples do
            let t0 = now ()
            let mutable read = 0
            for (fromSeq, toSeq) in blocks do
                read <- read + (TerminalFeed.outputText fromSeq toSeq feed).Length
            let elapsed = now () - t0
            // Anti-vacuity, and the one thing this scenario can silently get wrong: a feed that
            // came out empty, or ranges that miss it, measure a loop over nothing at whatever
            // speed nothing takes — and report it as a beautifully flat line.
            if read = 0 then
                failwith
                    "the transcript sweep read no output at all — the feed or the block ranges are \
                     empty, and this series would be a measurement of nothing"
            taken.Add elapsed
        oneSeries "transcript.read" (taken.ToArray ()))

    // Fill BOTH docs to roughly `chars`, through the real relay, and settle. One write rather
    // than a keystroke drip: this is setup, and nothing here is timed.
    exposeSeed (fun chars ->
        async {
            Markdown.intoFragment (filler chars) fragmentA
            do! nextFrame () |> Async.AwaitPromise
            do! nextFrame () |> Async.AwaitPromise
            return (Markdown.ofFragment fragmentB).Length
        }
        |> Async.StartAsPromise)

    // The caret push, timed twice: the WORK (everything `view.dispatch` sets off, which is the
    // reconciliation this suite exists to watch) and the WAIT (until it is on screen).
    //
    // A frame is yielded between samples so the browser can actually paint. A tight loop would
    // measure a hot cache and a starved compositor, which is nobody's experience.
    exposeCarets (fun samples ->
        async {
            let push = ResizeArray<float> ()
            let paint = ResizeArray<float> ()
            for _ in 1 .. samples do
                // The decoration set is rebuilt on every `setMeta` regardless of whether the
                // position moved (`presenceDecorationsPlugin`'s `apply`), so one position is
                // enough to make the work real. It is B's OWN reported selection, which means
                // it is a position that resolves in B's doc rather than a replayed constant.
                let cursors =
                    match selectionB with
                    | Some (anchor, head) ->
                        [ ({ Colour = "hsl(200, 70%, 55%)"
                             Selection = "hsla(200, 70%, 55%, 0.25)"
                             Name = "ada"
                             Anchor = anchor
                             Head = head } : Editor.RemoteBodyCursor) ]
                    | None -> []
                let t0 = now ()
                mirror.PushPresences cursors
                push.Add (now () - t0)
                do! nextFrame () |> Async.AwaitPromise
                paint.Add (now () - t0)
            return twoSeries "push" (push.ToArray ()) "paint" (paint.ToArray ())
        }
        |> Async.StartAsPromise)

// --- The shell, host-free (Plan 14, stage 2) --------------------------------------------
//
// The same page, for the same reason the replay shares it: this is the same KIND of thing —
// a surface that needs a real browser and nothing else. What only a browser can answer here
// is where FOCUS goes when a chip in the chat opens a tab in the pane, and whether the tab
// strip is a tablist the arrow keys actually walk. Both are DOM-swap behaviours a rendered
// string cannot show, and neither needs a Session Process, a channel or a native addon.
//
// A minimal Elmish: the app's own render (`Render.create`) over a `ClientModel`, re-run on
// dispatch. The reducer, the view, the syncs after it and the focus moves are the app's own —
// only the loop is local, because Program would want a doc and a connection this page
// deliberately does not have, and what the render would send to a session goes nowhere.

[<Emit("document.getElementById('shell')")>]
let private shellHost : obj = jsNative

/// The shell's own container class, taken from `Style.app` rather than written into the
/// harness page — the served document sets exactly this on `<main id="app">`, and a second
/// copy in HTML would be a layout free to drift from the one people get.
[<Emit("document.getElementById('shell').className = $0")>]
let private dressShell (className: string) : unit = jsNative

let private expect = function Ok v -> v | Error e -> failwith e

/// What the shell model's conversation is filled with: a column of one-liners, or a person and
/// an agent taking turns, the agent in paragraphs with a list and a fence. Every render parses
/// every body's Markdown again (`RichText.render`), so what a render costs is the prose on the
/// page — and a column of one-liners measured at a fifth of what a working session did.
type private Filler =
    | Lines
    | Replies

/// The block-mode terminal the shell model opens. Module-level because two things name it:
/// the model, and the scroll scenario below that streams records into it.
let private harnessTerminal : TerminalId = TerminalId.create "term-harness" |> expect

/// What the agent says in reply number `i`: paragraphs, a list and a fence — the prose a
/// working session's replies are made of, and so what a render of one costs. Shared by the
/// shell model's `Replies` filler and the open scenario's event fixture, so the two sweeps
/// draw the same conversation and their numbers can be read against each other.
let private replyBody (i: int) : string =
    String.concat
        "\n"
        [ sprintf "Looked at line %d. The fold runs once per record, and the render after it reads the layout back twice — once to keep the reader's place and once to put it back." i
          ""
          "- `Client.fs` dispatches a message per record"
          "- `setState` renders the whole view for each of them"
          "- the conversation and the scrollback both restore their scroll"
          ""
          "```"
          "for i in $(seq 1 300); do echo line-$i; sleep 0.01; done"
          "```"
          ""
          "So the cost is records × the page, and the number that says so is a count of renders." ]

/// A session that has run one command: one open terminal, one finished block, and the two
/// transcript records it produced. Enough for a chip to render in the chat and for its tab
/// to have something to show.
///
/// `fillerItems` is how long the conversation is, and `filler` what it is made of. Sixteen
/// lines is what the browser-tier cases were written against (`msg-filler-8` is a chapter, a
/// jump target, and the middle of a column those cases scroll to); the scroll scenario sweeps
/// the length over replies, because what a render costs while a person scrolls grows with
/// what is on the page, and one length of one-liners cannot show that growing.
let private shellModelOf (filler: Filler) (fillerItems: int) : ClientModel =
    let terminalId = harnessTerminal
    /// A second terminal, in LIVE mode and held by this peer — the screen that takes
    /// keystrokes (Plan 14, stage 6). Its own terminal rather than the first one's, so the
    /// block-mode flows above keep a block-mode terminal to run in.
    let liveId : TerminalId = TerminalId.create "term-live" |> expect
    /// A CLOSED terminal, for the list's other half — a recording rather than a place to type.
    let doneId : TerminalId = TerminalId.create "term-done" |> expect
    let blockId : BlockId = BlockId.create "block-harness" |> expect
    /// A burst: three commands ONE agent turn ran, so the chat has a task card to draw
    /// (Plan 20, stage 4). Three rather than two, and in three different states, because
    /// what a card does that a chip cannot is count and order them — a burst that was all
    /// one state would exercise the disclosure and nothing else.
    let agentTurn : AgentTurnId = AgentTurnId.create "turn-harness" |> expect
    let burstOk : BlockId = BlockId.create "block-burst-ok" |> expect
    let burstFailed : BlockId = BlockId.create "block-burst-failed" |> expect
    let burstRunning : BlockId = BlockId.create "block-burst-running" |> expect
    let peerId : PeerId = PeerId.create "ada" |> expect
    let messageId : MessageId = MessageId.create "msg-harness" |> expect
    /// What the agent actually says, which is the hard case for a phone: a fenced block whose
    /// lines are far wider than the screen, and prose carrying tokens no line break fits
    /// inside — a path, a URL. Nothing here may be allowed to size the timeline, or the whole
    /// conversation slides sideways under a header that stays put (photographed on iOS).
    let wideId : MessageId = MessageId.create "msg-wide" |> expect
    let wideBody =
        String.concat
            "\n"
            [ "Cleared the broken checkout at /home/user/.yession/sessions/AAZFRYD11S65Q4P64KHATP8YYG/workspace/repos/NickDarvey/yession"
              "and retried, see https://github.com/NickDarvey/yession/actions/runs/1234567890123/job/9876543210987."
              ""
              "```"
              "git -C /home/user/.yession/sessions/AAZFRYD11S65Q4P64KHATP8YYG/workspace/repos clone --depth 1 --filter=blob:none https://github.com/NickDarvey/yession.git"
              "```" ]
    let offset (n: int64) : EventOffset = EventOffset.create n |> expect
    /// Enough said, under ONE author, that the column genuinely scrolls and the pinned author
    /// line has something riding under it. Both are preconditions for the thing the rail's
    /// jump has to get right — landing its target where a person can READ it, rather than
    /// beneath the line saying who spoke — and a two-message fixture can show neither, so a
    /// test written against one passes whatever the jump does.
    let filler : ConversationItem list =
        [ for i in 1 .. fillerItems ->
            let person = (match filler with Lines -> true | Replies -> i % 2 = 1)
            { MessageId = MessageId.create (sprintf "msg-filler-%d" i) |> expect
              Author = if person then PeerRef peerId else ActorRef.Agent
              Body =
                if person then sprintf "and then line %d, which is here to make the column long" i
                else replyBody i
              Status = Complete
              Kind = ConversationItemKind.Message
              Offset = offset (int64 (10 + i))
              Woke = None; Replying = None } ]
    { ClientModel.init { PeerId = peerId; DisplayName = "swift-heron" } with
        Connection = Connected
        Session = Some (SessionId.create "harness" |> expect)
        // Two chapters, so the chapter rail has strokes to draw. Where a stroke
        // LANDS is arithmetic a model test settles; whether it lands beside the reading
        // column or on top of it is geometry, and only a rendered page knows that.
        //
        // The second one is in the MIDDLE of the conversation, and that is what a case about
        // a stroke standing level with its message needs: the first item cannot be scrolled
        // to the middle of a scrollport it is already at the top of, so a rail measured
        // against it is only ever measured outside the zone where the placement is exact.
        Synced =
            { SyncedSessionState.empty with
                // One chapter somebody has named and one nobody has, which is what a session
                // holds: a name is written over the heuristic's guess, never instead of it.
                Chapters =
                    Map.ofList
                        [ messageId, { Opens = true; Name = Ylmish.Text.ofString "Where it was settled" }
                          MessageId.create "msg-filler-8" |> expect,
                          { Opens = true; Name = Ylmish.Text.empty } ] }
        Conversation =
            { Items =
                [ { MessageId = messageId
                    Author = PeerRef peerId
                    Body = "ship it"
                    Status = Complete
                    Kind = ConversationItemKind.Message
                    Offset = offset 1L
                    Woke = None; Replying = None }
                  { MessageId = wideId
                    Author = ActorRef.Agent
                    Body = wideBody
                    Status = Complete
                    Kind = ConversationItemKind.Message
                    Offset = offset 3L
                    Woke = None; Replying = None } ]
                @ filler
                // A detached reply at the BOTTOM whose source is the very first message — the
                // long column between them is what makes the ref's jump a real scroll, the same
                // precondition the rail's own jump case needs. `Replying = Some` because it is
                // pushed far from what it answers; the ref renders as a live jump control.
                @ [ { MessageId = MessageId.create "msg-reply" |> expect
                      Author = ActorRef.Agent
                      Body = "Rebased and pushed, as you asked up top."
                      Status = Complete
                      Kind = ConversationItemKind.Message
                      Offset = offset 30L
                      Woke = None; Replying = Some messageId } ]
              ActiveAgentMessages = Map.empty; WokenTurn = None; TriggeredTurn = None }
        Timeline =
            { TimelineProjection.empty with
                TerminalItems =
                    [ TimelineBlock (offset 2L, terminalId, blockId)
                      TimelineBlock (offset 3L, terminalId, burstOk)
                      TimelineBlock (offset 4L, terminalId, burstFailed)
                      TimelineBlock (offset 5L, terminalId, burstRunning) ]
                BlockTurns =
                    Map.ofList
                        [ BlockId.value burstOk, agentTurn
                          BlockId.value burstFailed, agentTurn
                          BlockId.value burstRunning, agentTurn ] }
        Terminals =
            { Terminals =
                [ { TerminalId = terminalId
                    Title = TerminalTitle.fromProse "build"
                    OpenedBy = PeerRef peerId
                    Sandbox = Some SandboxRef.defaultRef
                    Renewable = false
                    IsOpen = true
                    ClosedReason = None
                    Lease = None
                    IntegrationLost = false
                    Blocks =
                      [ { BlockId = blockId
                          QueueId = None
                          Authority = Authority.ofAuthor (Principal.Peer peerId)
                          Command = "ls -la"
                          Background = false
                          FromSeq = 0
                          ToSeq = Some 2
                          Status = BlockFinished (CommandSucceeded 0) }
                        { BlockId = burstOk
                          QueueId = None
                          Authority = Authority.agentFor (Principal.Peer peerId)
                          Command = "npm run build"
                          Background = false
                          FromSeq = 2
                          ToSeq = Some 2
                          Status = BlockFinished (CommandSucceeded 0) }
                        { BlockId = burstFailed
                          QueueId = None
                          Authority = Authority.agentFor (Principal.Peer peerId)
                          Command = "npm test"
                          Background = false
                          FromSeq = 2
                          ToSeq = Some 2
                          Status = BlockFinished (CommandFailed 1) }
                        { BlockId = burstRunning
                          QueueId = None
                          Authority = Authority.agentFor (Principal.Peer peerId)
                          Command = "git status"
                          Background = true
                          FromSeq = 2
                          ToSeq = None
                          Status = BlockRunning }
                        // Enough history that the scrollback actually OVERFLOWS its box.
                        // "Show in terminal" (Plan 25, stage 3) scrolls to a command, and a
                        // history that fits on screen is one where every scroll is a no-op —
                        // a surface on which that case could not fail.
                        yield!
                          [ for i in 1 .. 24 ->
                              { BlockId = BlockId.create (sprintf "block-filler-%d" i) |> expect
                                QueueId = None
                                Authority = Authority.ofAuthor (Principal.Peer peerId)
                                Command = sprintf "echo line %d" i
                                Background = false
                                FromSeq = 2
                                ToSeq = Some 2
                                Status = BlockFinished (CommandSucceeded 0) } ] ]
                    DroppedBytes = 0 }
                  { TerminalId = liveId
                    Title = TerminalTitle.fromProse "shell"
                    OpenedBy = PeerRef peerId
                    Sandbox = Some SandboxRef.defaultRef
                    Renewable = false
                    IsOpen = true
                    ClosedReason = None
                    Lease = Some (PeerRef peerId)
                    IntegrationLost = false
                    Blocks = []
                    DroppedBytes = 0 }
                  // A finished one, so the terminal LIST (Plan 20, stage 0) has both its
                  // halves here: the working set, and the history it is a census of. Without
                  // it the list would be exercised over open terminals only, which is the
                  // half that was never the problem.
                  { TerminalId = doneId
                    Title = TerminalTitle.fromProse "install"
                    OpenedBy = PeerRef peerId
                    Sandbox = Some SandboxRef.defaultRef
                    Renewable = false
                    IsOpen = false
                    ClosedReason = Some "exit 0"
                    Lease = None
                    IntegrationLost = false
                    Blocks = []
                    DroppedBytes = 0 } ] }
        // The live terminal has a recording behind it too — that is what makes it
        // rewindable (Plan 14, stage 7), and a DVR with nothing recorded is a control with
        // nothing to do.
        TerminalFeeds =
            Map.ofList
                [ terminalId,
                  { Records =
                      Map.ofList
                          [ 0, { At = 0.0; Kind = TranscriptInput; Data = "ls -la\n" }
                            1, { At = 0.1; Kind = TranscriptOutput; Data = "total 0\n" } ]
                    KnownLength = 2
                    ReadThrough = 2
                    Header = Some { Width = 80; Height = 24; Timestamp = 0L } }
                  liveId,
                  { Records =
                      Map.ofList
                          [ 0, { At = 0.0; Kind = TranscriptOutput; Data = "earlier output\r\n" }
                            1, { At = 0.2; Kind = TranscriptOutput; Data = "vim ~/notes\r\n" } ]
                    KnownLength = 2
                    ReadThrough = 2
                    Header = Some { Width = 80; Height = 24; Timestamp = 0L } }
                  doneId,
                  { Records = Map.ofList [ 0, { At = 0.0; Kind = TranscriptOutput; Data = "installed\r\n" } ]
                    KnownLength = 1
                    ReadThrough = 1
                    Header = Some { Width = 80; Height = 24; Timestamp = 0L } } ]
        // SHUT to begin with, like a fresh client: the phone case is about what happens when
        // a chip brings the pane on screen, which is nothing to watch if it is already there.
        TerminalScreens = Map.ofList [ liveId, "\u001b[32mvim ~/notes\u001b[0m" ]
        // The two terminals this peer opened, pinned as the events fold would have pinned
        // them (Plan 20, stage 1). Set by hand because this model is BUILT rather than folded
        // — and without them the strip would hold only whatever is being previewed, which is
        // a fresh client's state rather than a working one.
        Pins = [ TerminalTab terminalId; TerminalTab liveId ]
        TerminalsOpen = false }

let private shellModel : ClientModel = shellModelOf Lines 16

/// What a client that has been to this session before holds when it opens it again: the
/// event log as the kept answers of its own history store, and the one terminal's transcript
/// as the kept answers of its transcript store. Built as EVENTS rather than as a model,
/// because what the open scenario measures is the fold — every kept answer is a message, and
/// every message is a render — and a model built by hand has no fold to measure.
///
/// The conversation is the `Replies` shape: a person and the agent taking turns, the agent
/// in the same prose `replyBody` gives the shell model — STREAMED, a delta every few words,
/// because that is what the log holds (the session this was measured on had 97 items and
/// eleven thousand events) — with a command run every second reply so a task card, a
/// terminal chip and a transcript are all part of what opens. `perAnswer` is how many
/// events each kept answer holds; a session somebody watched live keeps one answer per
/// poll, and a poll answers with the few events that arrived since.
[<RequireQualifiedAccess>]
type private OpenFixture =
    { History : Client.HistoryCache
      Transcripts : Client.TranscriptCaches
      /// The log itself, in order — what the kept answers were cut from, and what a cold
      /// open reads over the network a page at a time.
      Log : EventEnvelope<SessionEvent> list }

let private openFixture (items: int) (perAnswer: int) : OpenFixture =
    let peerId : PeerId = PeerId.create "ada" |> expect
    let session : SessionId = SessionId.create "harness" |> expect
    let terminal : TerminalId = TerminalId.create "term-open" |> expect
    let events = ResizeArray<SessionEvent> ()
    events.Add (PeerJoined { PeerId = peerId; DisplayName = "ada"; User = None })
    events.Add (
        TerminalOpened
            { TerminalId = terminal
              OpenedBy = ActorRef.Agent
              Title = TerminalTitle.fromProse "build"
              Sandbox = Some SandboxRef.defaultRef
              Renewable = false })
    // The transcript's records, in line order; line 0 is the header, so the first record is
    // line 1 and a block's `FromSeq`/`ToSeq` count from there.
    let records = ResizeArray<TranscriptRecord> ()
    let mutable seq = 1
    for i in 1 .. items do
        let messageId : MessageId = MessageId.create (sprintf "msg-open-%d" i) |> expect
        if i % 2 = 1 then
            events.Add (
                MessageSent
                    { MessageId = messageId
                      QueueId = None
                      Author = Principal.Peer peerId
                      Body = sprintf "and then line %d, which is here to make the column long" i })
        else
            let turn : AgentTurnId = AgentTurnId.create (sprintf "turn-open-%d" i) |> expect
            let asked : MessageId = MessageId.create (sprintf "msg-open-%d" (i - 1)) |> expect
            events.Add (AgentTurnStarted { AgentTurnId = turn; Cause = TriggeredBy asked })
            events.Add (AgentMessageStarted { AgentTurnId = turn; MessageId = messageId; Antecedent = None })
            if i % 4 = 0 then
                let block : BlockId = BlockId.create (sprintf "block-open-%d" i) |> expect
                events.Add (
                    TerminalBlockStarted
                        { TerminalId = terminal
                          BlockId = block
                          QueueId = None
                          Authority = Authority.agentFor (Principal.Peer peerId)
                          Command = sprintf "echo build %d" i
                          FromSeq = seq
                          Background = false })
                for line in 1 .. 3 do
                    records.Add { At = float seq; Kind = TranscriptOutput; Data = sprintf "build %d line %d\r\n" i line }
                    seq <- seq + 1
                events.Add (
                    TerminalBlockCompleted
                        { TerminalId = terminal; BlockId = block; Result = CommandSucceeded 0; ToSeq = seq })
            let body = replyBody i
            for delta in body.Split ' ' |> Array.chunkBySize 3 do
                events.Add (AgentMessageDelta { AgentTurnId = turn; MessageId = messageId; Delta = String.concat " " delta + " " })
            events.Add (AgentMessageCompleted { AgentTurnId = turn; MessageId = messageId; Body = body })
    let log =
        [ for offset in 0 .. events.Count - 1 ->
            { EventId = EventId.fresh ()
              SessionId = session
              Offset = EventOffset.create (int64 offset) |> expect
              Actor = ActorRef.SessionProcess
              Timestamp = System.DateTimeOffset.UtcNow
              Event = events.[offset] } ]
    // The kept answers: the log cut every `perAnswer` events, each encoded the way the server
    // serves it, because a kept answer IS what the server returned (`EventFetch.decodeLines`).
    let answers =
        [ for chunk in log |> List.chunkBySize perAnswer ->
            let first = EventOffset.value (List.head chunk).Offset
            let last = EventOffset.value (List.last chunk).Offset
            sprintf "events/%d-%d" first last,
            chunk |> List.map (Codec.toString Codec.sessionEventEnvelope) |> String.concat "\n" ]
    let answered (value: 'a) : Async<'a> =
        async {
            do! Async.AwaitPromise (nextTask ())
            return value
        }
    let history : Client.HistoryCache =
        { Client.HistoryCache.Stored = fun () -> answered (List.map fst answers)
          Client.HistoryCache.Read = fun url -> answered (answers |> List.tryPick (fun (u, body) -> if u = url then Some body else None))
          Client.HistoryCache.Write = fun _ _ -> async.Return () }
    // The transcript's kept answers, ten lines to each, the header on line 0 of the first.
    let linesPerAnswer = 10
    let lines =
        (Codec.toString Codec.transcriptLine (TranscriptHeaderLine { Width = 80; Height = 24; Timestamp = 0L }))
        :: [ for r in records -> Codec.toString Codec.transcriptLine (TranscriptRecordLine r) ]
    let transcriptAnswers =
        [ for first in 0 .. linesPerAnswer .. List.length lines - 1 ->
            sprintf "transcript/%d" first,
            (first, lines |> List.skip first |> List.truncate linesPerAnswer |> String.concat "\n") ]
    let transcript : Client.TranscriptCache =
        { Client.TranscriptCache.Stored = fun () -> answered (List.map fst transcriptAnswers)
          Client.TranscriptCache.Read = fun url -> answered (transcriptAnswers |> List.tryPick (fun (u, answer) -> if u = url then Some answer else None))
          Client.TranscriptCache.Write = fun _ _ _ -> async.Return () }
    let transcripts : Client.TranscriptCaches =
        { For = fun _ -> answered transcript
          Kept = fun () -> answered [ terminal ] }
    { OpenFixture.History = history; OpenFixture.Transcripts = transcripts; OpenFixture.Log = log }

/// Every byte the live screen decided to send, for the E2E to read back. The keystroke
/// translation is the whole of what a terminal front end does with a keyboard event, and it
/// is the one part of it that only a real browser can exercise: `KeyboardEvent` is not
/// something a rendered string has.
[<Emit("(function(d){ window.__typed = (window.__typed || '') + d })($1)")>]
let private recordTyped (_terminal: TerminalId) (data: string) : unit = jsNative

/// Hand the shell a terminal SCREEN, as the Session Process does over the data channel
/// (Plan 14, stage 6). Exposed so the E2E can drive the one path that puts a real emulator
/// in a real browser: without it this bundle contains no xterm at all, and the browser tier
/// silently proved nothing about the client's live screen — which is how a browser-only
/// module-resolution failure got past it and into a release job.
[<Emit("(function(f){ window.__snapshot = (id, seq, screen, cols, rows) => f(id, seq, screen, cols ?? 80, rows ?? 24) })($0)")>]
let private exposeSnapshot (f: string -> int -> string -> int -> int -> unit) : unit = jsNative

/// Hand the shell one transcript record, as the Session Process does as a terminal speaks.
/// The companion to the snapshot: a snapshot is where a screen STARTS and records are what
/// move it, and composing the two — including a resize reshaping the emulator mid-stream —
/// is the client's own fold.
[<Emit("(function(f){ window.__record = f })($0)")>]
let private exposeRecord (f: string -> int -> string -> string -> unit) : unit = jsNative

/// The size this client last told the Session Process its screen is. Read back by the E2E,
/// because the question there is whether a box that changed without the model changing — a
/// splitter dragged, a window resized — reached the pty at all.
[<Emit("(function(c, r){ window.__resized = c + 'x' + r })($1, $2)")>]
let private recordResized (_terminal: TerminalId) (cols: int) (rows: int) : unit = jsNative

/// The size this client last measured its OWN view of a terminal at — the width a command
/// queued from that pane would claim (`PendingAct.Size`). A second hook rather than a reading
/// of `window.__resized`, because the two are different facts: that one is what was sent to the
/// pty for a lease this peer holds, and this one is measured in BLOCK mode, where nobody holds
/// anything and nothing is sent at all.
[<Emit("(function(c, r){ window.__viewport = c + 'x' + r })($1, $2)")>]
let private recordViewport (_terminal: TerminalId) (cols: int) (rows: int) : unit = jsNative

/// Start an agent turn in the shell, as the Session Process does when the model begins to
/// answer: the turn, the message it opens, and the first words of it, folded through the same
/// page the real client reads. Exposed because the browser tier boots its session with NO
/// model credential (deliberately — see `Browser.fs`), so a turn in flight is a state no
/// amount of typing on this page can reach, and how many marks a person sees while one is
/// running is a question only a laid-out page can answer.
[<Emit("(function(f){ window.__agentTurn = f })($0)")>]
let private exposeAgentTurn (f: unit -> unit) : unit = jsNative

/// Hand a terminal's lease to this peer WITHOUT a press, as the alt-screen flip does: a block
/// takes the screen and the Session Process gives its author the keyboard. Exposed for the
/// same reason the snapshot is — it is the arrival of a fact from elsewhere, and a test that
/// could only reach live mode by pressing `take` could never exercise the route that has no
/// press to make.
[<Emit("(function(f){ window.__take = f })($0)")>]
let private exposeTake (f: string -> unit) : unit = jsNative

/// A collaborator's caret in a chapter's NAME, with no session to relay one from. The
/// positions handed over are real relative positions over a real `Y.Text` on this page's doc,
/// which is the whole of what the placement reads: it resolves them against the doc and
/// measures the input's own value and box. So the question the browser is asked here is
/// exactly the one the app asks it — given a caret that resolves, is the marker painted over
/// the field it is in, at the offset it claims.
///
/// Where a name lives IN the doc is the codec's answer and is pinned where it can be tested
/// for a penny (`SyncedStateSync.chapterNameText`), not restated here: a fixture that wrote
/// the layout out by hand would be a second copy of it, and the wrong one the day it moved.
[<Emit("(function(f){ window.__chapterCaret = f })($0)")>]
let private exposeChapterCaret (f: string -> int -> int -> unit) : unit = jsNative

do
    dressShell Style.app
    // Taking the keyboard is answered by the Session Process, which appends the lease event
    // and sends it back — so here the harness appends it to the projection itself, through
    // the same fold the real page uses. Without this the one act that puts a screen in front
    // of a keyboard is `ignore` in the harness, and the browser tier cannot reach live mode
    // except for the terminal that was born holding a lease.
    let mutable takeRef : TerminalId -> unit = ignore
    let actions =
        { ViewActions.ssr with
            FocusPane = PaneShell.toPane
            FocusChat = PaneShell.toChatItem
            FocusWatch = PaneShell.toWatchToggle
            RevealBlock = fun id blockId -> PaneShell.revealBlock (TerminalId.value id) (BlockId.value blockId)
            RevealMessage = fun id -> PaneShell.revealMessage (MessageId.value id)
            FocusItemActions = fun id -> PaneShell.toItemActions (MessageId.value id)
            TakeTerminal = fun id -> takeRef id
            TypeIntoTerminal = recordTyped }
    // The forward reference is the same shape `Browser.fs` uses: the render needs dispatch
    // (a rewound cast that plays off its end jumps back to live) and dispatch's render needs
    // the render.
    let mutable dispatchRef : ClientMsg -> unit = ignore
    let shellDoc = Y.Doc.Create ()
    let renderer =
        Render.create
            { Doc = shellDoc
              Registry = BodyRegistry shellDoc
              Texts = TextRegistry shellDoc
              PeerId = shellModel.Peer.PeerId
              Root = shellHost
              Actions = actions
              Dispatch = fun msg -> dispatchRef msg
              // No session behind this page: a draft sent, a caret reported, a keyframe
              // asked for go nowhere. A resize is the one thing read back, because whether
              // a box that changed reached the pty at all is a question the E2E asks.
              Links =
                { SendDraft = ignore
                  SendTerminalDraft = fun _ _ -> ()
                  ReportFocus = ignore
                  ResizeTerminal = recordResized
                  Http = fun _ -> async { return Error (Client.HttpUnreachable "the harness serves no session") } } }
    let mutable model = shellModel
    /// Where a render's cost goes while the scroll scenario below is running, and nowhere
    /// otherwise. Timed around the whole of `render` — the view, Lit's diff, and the syncs
    /// after it — because that is the task a frame waits on when a record lands mid-scroll.
    let mutable renderTimes : ResizeArray<float> option = None
    let rec dispatch (msg: ClientMsg) : unit =
        model <- ClientModel.update msg model
        // Read back off the MODEL rather than out of the message: a measurement the reducer
        // refused is not a width anything would claim, and a hook that reported it anyway
        // would say the opposite of what happened.
        match msg with
        | TerminalViewportMsg (terminal, _) ->
            Map.tryFind terminal model.TerminalViewports
            |> Option.iter (fun size -> recordViewport terminal size.Cols size.Rows)
        | _ -> ()
        let started = now ()
        render ()
        renderTimes |> Option.iter (fun times -> times.Add (now () - started))
    and render () = renderer.SetState model
    dispatchRef <- dispatch
    takeRef <-
        fun id ->
            let taken =
                SessionEvent.TerminalLeaseTaken
                    { TerminalId = id; By = ActorRef.PeerRef model.Peer.PeerId; FromSeq = 0 }
            model <- { model with Terminals = Projection.applyEvent model.Terminals taken }
            render ()
    exposeSnapshot (fun id seq screen cols rows ->
        match TerminalId.create id with
        | Ok terminal -> renderer.Screens.Snapshot terminal { Seq = seq; Cols = cols; Rows = rows; Screen = screen }
        | Error _ -> ())
    exposeAgentTurn (fun () ->
        let expect = function Ok v -> v | Error e -> failwith e
        let turn : AgentTurnId = AgentTurnId.create "turn-live" |> expect
        let messageId : MessageId = MessageId.create "msg-live" |> expect
        // The turn answers something a PERSON said — the fixture's first message. Naming the
        // agent's own message here instead makes the reply detached from itself, and the
        // timeline dutifully quotes the message above its own body.
        let asked : MessageId = MessageId.create "msg-harness" |> expect
        let envelope (offset: int64) (event: SessionEvent) : EventEnvelope<SessionEvent> =
            { EventId = EventId.fresh ()
              SessionId = model.Session |> Option.defaultWith (fun () -> SessionId.create "harness" |> expect)
              Offset = EventOffset.create offset |> expect
              Actor = ActorRef.Agent
              Timestamp = System.DateTimeOffset.UtcNow
              Event = event }
        dispatch (
            EventsPageMsg
                { Events =
                    [ envelope 40L (SessionEvent.AgentTurnStarted { AgentTurnId = turn; Cause = TurnCause.TriggeredBy asked })
                      envelope 41L (SessionEvent.AgentMessageStarted { AgentTurnId = turn; MessageId = messageId; Antecedent = None })
                      envelope 42L (SessionEvent.AgentMessageDelta { AgentTurnId = turn; MessageId = messageId; Delta = "Looking at it" }) ]
                  LastOffset = EventOffset.create 42L |> expect |> Some
                  IsEnd = true }))
    exposeTake (fun id ->
        match TerminalId.create id with
        | Ok terminal -> takeRef terminal
        | Error _ -> ())
    exposeChapterCaret (fun id anchor head ->
        match MessageId.create id, PeerId.create "brave-owl" with
        | Ok messageId, Ok peerId ->
            let text = shellDoc.getText ("harness-chapter-name-" + id)
            // Only a chapter this page HAS gets seeded: a name nobody here holds is not an
            // empty one, and a caret taken over an empty text is a caret at index nothing.
            if text.length = 0 then
                ClientModel.chapterNameAt messageId model |> Option.iter (fun named -> text.insert (0, named))
            let at (index: int) = ProseMirror.relPosFromTypeIndex (box text) index |> ProseMirror.encodeRel
            dispatch (
                RemotePresenceMsg
                    { PeerId = peerId
                      DisplayName = "brave-owl"
                      Focus = Some { Field = ChapterName messageId; Pos = { Anchor = at anchor; Head = at head } } })
        | _ -> ())
    exposeRecord (fun id seq kind data ->
        match TerminalId.create id, TranscriptKind.parse kind with
        | Ok terminal, Some kind ->
            dispatch (TerminalRecordMsg (terminal, seq, { At = 0.0; Kind = kind; Data = data }))
        | _ -> ())
    render ()
    // The shell harness drives the real render, so it gets the real page listeners too — a
    // splitter, a pinned surface or a rail that only worked in the app is one no browser-tier
    // test could reach.
    Render.attach ()

    // --- Scrolling while records arrive (the `bench` scroll scenario) ------------------------
    //
    // A person flinging back through a conversation while the agent is working. Measured on
    // the home deployment against a session with 57 items and a turn in progress: every frame
    // over 50ms during the fling held exactly one render, and a fling with no render in it
    // dropped no frame at all. A record arriving is a full render — the view, the diff, the
    // syncs — and one landing mid-fling is the stutter, so what this records is two things: how
    // long a render takes while the conversation is scrolled, and how far apart the frames were
    // while records were landing in them.
    //
    // The stream goes into the block-mode terminal as transcript records, which is what the
    // app's own record path dispatches (`TerminalRecordMsg`); the running block in the burst
    // card grows with them, so the conversation redraws too. The fling itself is NOT made here:
    // a synthetic scroll would be a scroll the browser's input pipeline never saw, and the
    // driver has real touch input. So it is two calls — begin, fling, end — and the report says
    // how far the scroll went, so a driver whose fling never moved cannot mistake a quiet page
    // for a smooth one.
    //
    // What is timed is the app's own render (`Render.create`), the whole of it: a change
    // anywhere between a model and the page shows here.
    let conversation () =
        Browser.Dom.document.querySelector "#shell [data-conversation]" :?> Browser.Types.HTMLElement
    let mutable finish : (unit -> string) option = None
    let mutable sentSoFar : unit -> int = fun () -> 0
    exposeScrollBegin (fun items records everyMs ->
        model <- shellModelOf Replies items
        render ()
        let surface = conversation ()
        surface.scrollTop <- surface.scrollHeight
        let scrolledFrom = surface.scrollTop
        let times = ResizeArray<float> ()
        renderTimes <- Some times
        let frames = ResizeArray<float> ()
        let mutable running = true
        let mutable lastFrame = now ()
        let rec frame () =
            if running then
                let t = now ()
                frames.Add (t - lastFrame)
                lastFrame <- t
                onFrame frame
        onFrame frame
        let mutable sent = 0
        sentSoFar <- fun () -> sent
        let interval =
            Browser.Dom.window.setInterval (
                (fun () ->
                    if sent < records then
                        let seq = 2 + sent
                        sent <- sent + 1
                        dispatch (
                            TerminalRecordMsg (
                                harnessTerminal,
                                seq,
                                { At = float seq; Kind = TranscriptOutput; Data = sprintf "line %d\r\n" seq }))),
                everyMs)
        finish <-
            Some (fun () ->
                running <- false
                Browser.Dom.window.clearInterval interval
                renderTimes <- None
                scrollReport (frames.ToArray ()) (times.ToArray ()) times.Count sent scrolledFrom (conversation ()).scrollTop))
    exposeScrollSent (fun () -> sentSoFar ())
    exposeScrollEnd (fun () ->
        match finish with
        | Some report ->
            finish <- None
            report ()
        | None -> failwith "__benchScrollEnd without a __benchScrollBegin — nothing was being measured")

    // --- Opening a session from what was kept (the `bench` open scenario) ---------------------
    //
    // A person opening a session they have been in before. Measured on the home deployment
    // against a session of 97 items: the app folded 179 kept answers, one message and one
    // render each, in a single 1.15s task (3s at a phone's pace) during which the page showed
    // an empty conversation under a "not connected" banner; then the conversation; then the
    // banner left and the conversation's box grew into its place; then the connection landed.
    // "It renders a couple of times before settling" is those pictures, and the freeze between
    // the first two.
    //
    // So what is recorded is what a person can see. Every frame, the conversation is looked
    // at the way an eye would — where its box is, what is scrolled into it, and which item is
    // under the middle of it — and a frame whose look changed is a PAINT. The item that was
    // under the middle last frame is measured again this frame, and how far it moved is the
    // JUMP: words a person was reading, somewhere else now. The longest gap between two
    // frames is how long the page sat frozen. And the renders are the app's own count.
    //
    // The open itself is the app's (`Client.LocalOpen.replay` — the kept events, the kept
    // transcripts, `Connecting`), over fixture stores, followed by the accepted connection.
    // What is NOT here is the network: the probe, the handshake, the sync that follows it.
    let anchorTop (el: Browser.Types.Element) = el.getBoundingClientRect().top
    let sameElement (a: Browser.Types.Element option) (b: Browser.Types.Element option) =
        match a, b with
        | Some a, Some b -> obj.ReferenceEquals (a, b)
        | None, None -> true
        | _ -> false
    let look () =
        let surface = conversation ()
        let rect = surface.getBoundingClientRect ()
        // The middle of what is ON SCREEN of the conversation, not of its box: the eye is
        // somewhere in the visible part, wherever the box's own middle has gone.
        let visibleTop = max rect.top 0.0
        let visibleBottom = min rect.bottom Browser.Dom.window.innerHeight
        let anchor =
            Browser.Dom.document.elementFromPoint (rect.left + rect.width / 2.0, (visibleTop + visibleBottom) / 2.0)
            |> Option.ofObj
            |> Option.bind (fun el -> el.closest "[data-conversation] > *")
        { Look.Top = rect.top
          Look.ScrollTop = surface.scrollTop
          Look.ScrollHeight = surface.scrollHeight
          Look.Items = int surface.children.length
          Look.Chars = surface.textContent.Length
          Look.PageChars = Browser.Dom.document.body.textContent.Length
          Look.Anchor = anchor }
    /// One open, watched: the sampler above the shell from the first render until ten still
    /// frames after `opening` has finished, then the report. `opening` is the open itself —
    /// what arrives, in what order, a task apart where the network or a store would put one.
    let measureOpen (opening: Async<unit>) : JS.Promise<string> =
        JS.Constructors.Promise.Create (fun resolve reject ->
            let peer : PeerState = { PeerId = PeerId.create "ada" |> expect; DisplayName = "swift-heron" }
            model <- ClientModel.init peer
            // The shell on screen, as the app's is: the harness page keeps other fixtures
            // above it, and a conversation below the fold has nothing under the eye.
            (shellHost :?> Browser.Types.HTMLElement).scrollIntoView ()
            let rendersBefore = appRenders ()
            let started = now ()
            let mutable paints = 0
            let mutable jumps = 0
            let mutable jump = 0.0
            let mutable replaced = 0
            let mutable blocked = 0.0
            let mutable lastFrame = started
            let mutable lastLook : Look option = None
            let mutable lastAnchor : (Browser.Types.Element * float) option = None
            let mutable lastRenders = rendersBefore
            let mutable opened = false
            let mutable quiet = 0
            let rec frame () =
                let t = now ()
                blocked <- max blocked (t - lastFrame)
                lastFrame <- t
                let seen = look ()
                let changed =
                    match lastLook with
                    | None -> true
                    | Some before ->
                        before.Top <> seen.Top || before.ScrollTop <> seen.ScrollTop || before.ScrollHeight <> seen.ScrollHeight
                        || before.Items <> seen.Items || before.Chars <> seen.Chars || before.PageChars <> seen.PageChars
                        || not (sameElement before.Anchor seen.Anchor)
                if changed then paints <- paints + 1
                match lastAnchor with
                | Some (el, top) when isConnected el ->
                    let moved = abs (anchorTop el - top)
                    if moved > 1.0 then
                        jumps <- jumps + 1
                        jump <- jump + moved
                | Some _ -> replaced <- replaced + 1
                | None -> ()
                lastLook <- Some seen
                lastAnchor <- seen.Anchor |> Option.map (fun a -> a, anchorTop a)
                let renders = appRenders ()
                if opened && renders = lastRenders && not changed then quiet <- quiet + 1 else quiet <- 0
                lastRenders <- renders
                // Ten still frames after the open has finished: settled.
                if quiet >= 10 then
                    resolve (
                        openReport
                            (renders - rendersBefore) paints jumps jump blocked (t - started)
                            (int (conversation ()).children.length)
                            (match model.Connection with
                             | Connected -> "Connected"
                             | Connecting -> "Connecting"
                             | Reconnecting -> "Reconnecting"
                             | Disconnected _ -> "Disconnected")
                            replaced)
                else onFrame frame
            onFrame frame
            // The first paint: the shell with nothing in it, as a fresh page shows before its
            // stores have answered.
            render ()
            async {
                try
                    do! opening
                    opened <- true
                with e -> reject e
            }
            |> Async.StartImmediate)
    let accepted (fixture: OpenFixture) =
        ConnectedMsg
            { SessionId = SessionId.create "harness" |> expect
              AssignedDisplayName = "swift-heron"
              LatestOffset = fixture.Log |> List.tryLast |> Option.map (fun e -> e.Offset) }
    exposeOpen (fun items perAnswer ->
        let fixture = openFixture items perAnswer
        measureOpen (
            async {
                do! Client.LocalOpen.replay fixture.History fixture.Transcripts dispatch
                // The accepted connection lands from the network, never in the task that
                // asked for it — a task later at the very least.
                do! Async.AwaitPromise (nextTask ())
                dispatch (accepted fixture)
            }))

    // --- Opening a session from nothing (the `bench` cold-open scenario) ---------------------
    //
    // The same person on a phone whose store holds none of this session — a first visit, an
    // evicted store, a hole the replay stopped at. Everything comes over the network, a page
    // at a time from the oldest: the read loop (`Client.connect`) asks from its cursor, folds
    // the answer, and asks again until the page it folded was the end. Measured on the home
    // deployment against that same session of 97 items: eleven thousand events, 116 pages,
    // 116 renders, and the conversation pinned to its foot the whole way — 65 frames on
    // which the words under the eye were somewhere else, forty-nine thousand pixels of it.
    //
    // The loop itself needs a channel; what is here is what it dispatches, in its order, a
    // round trip apart (`everyMs` — the driver says what a round trip is): the connection
    // accepted (which is what tells the model how far behind it is), then the pages. Pacing
    // a render while the client is catching up is the change this would show, and it lives
    // in `Render`, which this runs.
    exposeOpenCold (fun items pageSize everyMs ->
        let fixture = openFixture items pageSize
        let pages = fixture.Log |> List.chunkBySize pageSize
        let last = List.length pages - 1
        measureOpen (
            async {
                dispatch ConnectingMsg
                do! Async.AwaitPromise (nextTask ())
                dispatch (accepted fixture)
                for (i, page) in List.indexed pages do
                    do! Async.Sleep everyMs
                    dispatch (
                        EventsPageMsg
                            { Events = page
                              LastOffset = Some (List.last page).Offset
                              IsEnd = i = last })
            }))
