module Yession.Frames

// A session's first load, frame by frame, against a real deployment.
//
//   dotnet fsi tasks.fsx frames --manager https://home.example:8321            # a fresh session
//   dotnet fsi tasks.fsx frames --manager … --session https://…/s/<id>/       # one that exists
//   dotnet fsi tasks.fsx frames --manager … --seconds 20 --desktop --out ./frames
//
// Or one honest picture of any page — `--still`, which films nothing and writes `still.png`:
//
//   dotnet fsi tasks.fsx frames --boot --still                                # a fresh session, phone
//   dotnet fsi tasks.fsx frames --boot --still --page / --size 1440x900       # the Manager's page
//   dotnet fsi tasks.fsx frames --manager … --still --page … --measure "<js>" # and a measurement
//
// `--boot` is the verb's, not this module's: it builds the tree, starts a scratch Manager on it
// and passes `--manager` here (`tasks.fsx`). A still is what `.agents/skills/ui-exploration`
// once did with a script of its own — a true device viewport, the hover and focus a desktop
// has, the `{vw, docW, overflowX}` ground truth printed beside the picture — moved here so
// there is one camera. The browser under it is `Fable.NodeExtras.Chromium`'s, so there is one
// launcher and one way of finding the browser for everything on Node that drives one.
//
// It answers the question a person asks when a page "jumps": WHAT was on the screen at each
// moment, and WHICH render put it there. A render counter and a DOM diff say what the client
// did; a screenshot says what the person saw; neither alone explains a jump, because the
// compositor paints frames the main thread never saw (a CSS transition), and the main thread
// renders frames the compositor folds together (twelve renders in one 70ms task). So this
// records both sides and labels each with the other:
//
//   - Every painted frame, from Chromium's screencast, as PNG.
//   - Every client render (`globalThis.__yessionRenders`, counted in `Render.fs`), with a
//     layout summary taken in a microtask after the render task — connection state, whether
//     the terminals column is open, the on-screen rects of the big regions, scroll positions,
//     counts — and the diff against the previous render's.
//   - A STAMP the page writes into its own corner every animation frame and after every
//     render: `rN tM` — renders landed, milliseconds since this document began. It is in the
//     picture, so the picture says which render it is a picture of, and a frame whose clock
//     runs backwards is a frame from an EARLIER DOCUMENT. That is how the sign-in bounce was
//     found: a fresh session's shell loads, paints, is bounced through `/login` and loads
//     again, and every jump before the bounce happens twice.
//   - Per animation frame: the terminals pane's element identity (numbered as it is
//     replaced), its `translate`, and the CSS transitions running on it — so a slide can be
//     told from a snap, and "the pane closed twice" from "one 200ms transition".
//   - Document, `/me` and `/login` requests, in order, with redirect statuses.
//
// Then a pixel diff of consecutive frames (done by the same Chrome, on a canvas: changed-pixel
// count and bounding box), so a blinking caret (tens of pixels) is told from a jump
// (thousands), and a report:
//
//   <out>/report.html    every frame worth looking at, in order, with its stamp, its diff box
//                        drawn in red, the renders that landed before it and what each changed;
//                        the navigation timeline above; every render below.
//   <out>/sheet-N.png    the same frames as contact sheets, eight to a page, for reading with
//                        an image viewer — or the Read tool, which is the point.
//   <out>/frames/        the frames the report shows.
//   <out>/log.json       everything, for a question the report did not anticipate.
//
// What it found on the first run (phone viewport, a fresh session, Chromium): SIX jumps
// before a repo was even chosen, and two more found since. The server-rendered shell painted
// the terminals pane OPEN full-width and the first client render slid it shut over 200ms;
// `/me` answered 401, the shell bounced through `/login` and LOADED AGAIN, so both happened
// twice; the client started `Disconnected`, so the first paint wore the "not connected" strip
// for ~75ms and then reflowed the panes up by its height when the transport was asked; and the
// repo picker was a flow block at the foot of the conversation, so the conversation jumped up
// when it appeared and again as the list loaded into it and it grew. All fixed — the shell
// carries `term-closed` from the model (`Ssr.page`), the Manager's `/open` page enters a
// session through `/login` so the shell is painted once, `ClientModel.init` starts `Connecting`
// (silent) rather than `Disconnected`. The picker's jump was answered with an overlay that
// covered the conversation's lower edge, and then given back: an overlay hid whatever the chat
// held under it, unscrollably, and a session whose first acts were commands had its newest ones
// there. The picker takes its own room again (`Style.ask`) — a FIXED share of the column,
// reserved the instant the card is offered, so a listing loading in fills a list that already
// has its height rather than growing the card off its pinned foot. That was the SECOND picker
// jump (the list arriving and the card growing), and it is gone. What a clean run looks like
// NOW: the `/open` page, one shell document with the pane off-canvas from its first frame, the
// connection silent on first paint, then the picker appearing at connect — already at its full
// height — and the chat above it giving it that room once. The one jump left is the picker
// APPEARING at all: it is not in the first paint (`Launch.anchor` waits for the connection), so
// it arrives at connect rather than with the shell. Reserving its room from the first frame
// would want the shell to know, before it connects, that this session will offer a card.
//
// How to read what it makes. Start at the sheets: a jump is two adjacent frames that differ
// by a lot, and the red box says where. Read the stamps. Same stamp on both frames — the
// compositor moved something without the main thread (a transition; the per-frame log names
// it). Different stamps — the renders between them are listed under the frame, with the
// layout diff each made; the one that changed the region in the box is the one. A stamp that
// went backwards is a second document; look at the navigation timeline for why.
//
// Chromium only, because the screencast is a CDP feature. WebKit needs a different camera
// (timed screenshots through Playwright), and the stamps make that comparable when it exists.
// The browser is the one the suite uses (`PLAYWRIGHT_BROWSERS_PATH`, found as `Browser.fs` finds
// it, by `Fable.NodeExtras.Chromium`), so run this inside the dev shell; `CHROMIUM_PATH`
// overrides it, as there.
//
// Caveats that shape what it can say. The stamp changes every animation frame, so it FORCES
// a paint per frame — frame counts are the stamp's, and a frame whose only change is the
// stamp is dropped by the pixel threshold (`--min-px`). A run whose shell never lands films
// for a full minute, some thousands of frames; they are diffed in batches, because handing
// the analyzer every frame in one call is a request the browser never answers. Screencast timestamps and the page's
// clock are different clocks; the stamp is the truth and the timestamps are for ordering.
// The session is created the way the Create button creates one (`POST /sessions`, then the
// `/open` page that launches it and hands the browser over) and STOPPED afterwards unless
// `--keep`; a `--session` given is left as it was found. And it measures one load on one
// machine: what it says is where the jumps come from, never how long they take.

open System
open Fable.Core
open Yession.Domain
open Yession.App
#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

// --- Node --------------------------------------------------------------------------------------

/// A screencast frame's bytes. CDP hands them over base64 in the JSON, and what `writeFileSync`
/// wants is a Buffer — `Fable.Node`'s own `Buffer.from`, rather than the global spelled again.
let private bytesOfBase64 (data: string) : Node.Buffer.Buffer =
    Node.Api.buffer.Buffer.from (data, Node.Buffer.BufferEncoding.Base64)

/// Time, through the Domain's one port for it (`Clock.fs`): the waits between polls and the
/// wall-clock the landing window is measured against are the same question asked twice, and a
/// camera that answered one with `setTimeout` and the other with `Date.now` would have two
/// clocks, only one of which anything can turn. The launcher's waits are among them: it is
/// handed `delay` rather than keeping a clock of its own.
let private clock = Clock.system

let private delay (ms: int) : JS.Promise<unit> =
    clock.After (TimeSpan.FromMilliseconds (float ms)) |> Async.StartAsPromise

/// Stop. Typed as returning ANYTHING because it does not return, which is what an `abort`
/// deep in a promise needs: there is no value to answer with.
let private exitWith (code: int) : 'a = Fable.NodeExtras.Processes.exitWith code

/// Say what happened and stop, having done nothing. The Host's `Interop` says this for the
/// bins; an instrument that deliberately does not reference the Host (it wants the command
/// line, not a session) says it here, over the same binding.
let private abort (message: string) : 'a =
    JS.console.error message
    exitWith 2

/// This process's arguments, its executable and script dropped — what `Cli` parses.
let private argv () : string array = Node.Api.``process``.argv |> Seq.skip 2 |> Seq.toArray

let private say (line: string) : unit = JS.console.log line

// The files and paths, through `Fable.Node`'s own bindings to `node:fs` and `node:path`, which
// type every call made here. `writeFileSync` takes text or bytes alike, and these are the two
// this camera writes, so each has its own name rather than one over anything.
let private joinTwo (a: string) (b: string) : string = Node.Api.path.join (a, b)
let private writeText (path: string) (text: string) : unit = Node.Api.fs.writeFileSync (path, box text)
let private writeBytes (path: string) (bytes: Node.Buffer.Buffer) : unit = Node.Api.fs.writeFileSync (path, box bytes)

/// A frame's picture as a digest, which is how a repeated picture is told from a new one.
let private sha1Of (text: string) : string =
    let digester = Fable.NodeExtras.Digests.hash "sha1"
    digester.update(text, Node.Buffer.BufferEncoding.Utf8).digest Fable.NodeExtras.BinaryToTextEncoding.Hex

let private escapeHtml (s: string) = s.Replace("&", "&amp;").Replace ("<", "&lt;")

// --- CDP ---------------------------------------------------------------------------------------

// The protocol is `Fable.NodeExtras.Cdp`'s: every command this camera sends is declared there with
// the arguments it means, and every answer and event comes back typed.
module Cdp = Fable.NodeExtras.Cdp

/// What an evaluated expression gave, as the kind it was asked for. A throw in the expression
/// is printed rather than swallowed, because a probe that silently evaluates to nothing is a
/// probe that reports a clean page; an answer of another kind is no answer.
let private settled (answer: Cdp.Evaluated<'T>) : 'T option =
    match answer with
    | Cdp.Evaluated.Returned value -> Some value
    | Cdp.Evaluated.Unexpected _ -> None
    | Cdp.Evaluated.Threw details ->
        say (sprintf "evaluate: %s" (if details.Length > 300 then details.Substring (0, 300) else details))
        None

let private evaluateText (cdp: Cdp.Session) (expression: string) : JS.Promise<string option> =
    Cdp.Runtime.evaluateText cdp expression |> Promise.map settled

let private evaluateNumber (cdp: Cdp.Session) (expression: string) : JS.Promise<float option> =
    Cdp.Runtime.evaluateNumber cdp expression |> Promise.map settled

let private evaluateJson (cdp: Cdp.Session) (expression: string) : JS.Promise<string option> =
    Cdp.Runtime.evaluateJson cdp expression |> Promise.map settled

/// What the page must have answered for the run to mean anything.
let private answered (what: string) (answer: 'T option) : 'T =
    match answer with
    | Some value -> value
    | None -> failwithf "the page did not answer %s" what

/// The address the page is at once `probe` says it has landed, and nothing while it has not.
/// An expression that answers with no string is a page that has not landed.
let private landedAt (cdp: Cdp.Session) (probe: string) : JS.Promise<string> =
    promise {
        let! href = evaluateText cdp probe
        match href with
        | Some href -> return href
        | None -> return ""
    }

// --- the instrument in the page ----------------------------------------------------------------

/// Installed before any script of the document runs (`Page.addScriptToEvaluateOnNewDocument`),
/// in every document the tab loads — the `/open` page, the shell, and the shell again after the
/// sign-in bounce. One block, so nothing here shares the page's global lexical scope: the `/open`
/// page declares a `poll` of its own, and a top-level `const poll` here was a SyntaxError that
/// stopped that page's script and left the tab on "Opening session…" forever.
///
/// What it leaves for the run to read back: `__log` (renders with their after-render layout,
/// html class changes, pane add/remove), `__frames` (one row per animation frame), `__origin`
/// (this document's `performance.timeOrigin`, to put screencast frames on its clock).
let private instrument = """{
  globalThis.__log = []
  globalThis.__origin = performance.timeOrigin
  const t = () => Math.round(performance.now())
  const rect = (sel) => { const el = document.querySelector(sel); if (!el) return null; const r = el.getBoundingClientRect(); return [Math.round(r.left), Math.round(r.top), Math.round(r.width), Math.round(r.height)] }
  const look = () => {
    const c = document.querySelector('[data-conversation]')
    return {
      conn: document.querySelector('[data-connection]')?.getAttribute('data-connection') ?? null,
      termClosed: document.documentElement.classList.contains('term-closed'),
      title: document.title,
      pane: rect('[data-content-panel]'), chat: rect('[data-conversation]'), composer: rect('[data-draft-editor]'),
      picker: rect('[data-repo-picker]'), catchUp: rect('[data-catch-up-bar]'), loading: rect('[data-history-loading]'),
      chatScroll: c ? [c.scrollTop | 0, c.scrollHeight, c.clientHeight] : null,
      items: document.querySelectorAll('[data-conversation] > *').length,
      tabs: document.querySelectorAll('[data-terminal-tab]').length,
      blocks: document.querySelectorAll('[data-chat-block]').length,
      bodyH: document.body ? document.body.scrollHeight : -1, vv: [innerWidth, innerHeight],
    }
  }
  const ids = new WeakMap(); let nextEl = 1
  const idOf = (el) => { if (!el) return 0; if (!ids.has(el)) ids.set(el, nextEl++); return ids.get(el) }
  // The stamp: in the picture, so the picture says which render and which frame it is a
  // picture of. A compositor-only frame (a transition) repeats the last stamp, which is
  // itself the fact worth reading.
  let stampEl = null
  const stamp = (text) => {
    if (!stampEl || !stampEl.isConnected) {
      if (!document.body) return
      stampEl = document.createElement('div'); stampEl.id = '__stamp'
      stampEl.style.cssText = 'position:fixed;left:0;top:0;z-index:2147483647;background:#000;color:#0f0;font:bold 22px/1 monospace;padding:2px 4px;pointer-events:none'
      document.body.appendChild(stampEl)
    }
    stampEl.textContent = text
  }
  let renders = 0
  Object.defineProperty(globalThis, '__yessionRenders', { configurable: true, get() { return renders }, set(v) {
    renders = v
    const e = { t: t(), n: v }
    __log.push(e)
    queueMicrotask(() => { e.after = look(); stamp('r' + v + ' t' + t()) })
  } })
  window.addEventListener('resize', () => __log.push({ t: t(), resize: [innerWidth, innerHeight] }))
  new MutationObserver((ms) => { for (const m of ms) if (m.target === document.documentElement) __log.push({ t: t(), attr: m.attributeName, was: m.oldValue, now: m.target.getAttribute(m.attributeName) }) })
    .observe(document, { subtree: true, attributes: true, attributeOldValue: true, attributeFilter: ['class'] })
  const panesIn = (n) => n.nodeType !== 1 ? [] : n.matches('[data-content-panel]') ? [n] : [...n.querySelectorAll('[data-content-panel]')]
  new MutationObserver((ms) => { for (const m of ms) { for (const n of m.addedNodes) for (const p of panesIn(n)) __log.push({ t: t(), pane: 'added', id: idOf(p) }); for (const n of m.removedNodes) for (const p of panesIn(n)) __log.push({ t: t(), pane: 'removed', id: idOf(p) }) } })
    .observe(document, { subtree: true, childList: true })
  globalThis.__frames = []
  const tick = () => {
    const pane = document.querySelector('[data-content-panel]'); const app = document.querySelector('#app')
    const cs = pane ? getComputedStyle(pane) : null
    stamp('r' + renders + ' t' + t())
    __frames.push([t(), document.documentElement ? document.documentElement.className : '', idOf(pane), pane ? Math.round(pane.getBoundingClientRect().left) : -1, cs ? cs.translate : '',
      pane ? pane.getAnimations().map((a) => (a.transitionProperty || a.animationName) + '@' + Math.round(a.currentTime) + (a.playState === 'running' ? '' : '/' + a.playState)).join(',') : '',
      idOf(app), app ? app.children.length : -1, renders])
    requestAnimationFrame(tick)
  }
  requestAnimationFrame(tick)
}"""

/// The pixel diff, done by the same Chrome on a page of its own: each frame against the one
/// before, the count of changed pixels and their bounding box (in CSS px). Frames arrive as
/// data URLs, because a `file://` image taints the canvas and `getImageData` then refuses.
let private analyzer = """<!doctype html><meta charset=utf-8><script>
  // Called once per BATCH of frames, in order; the previous batch's last frame is kept so
  // the first of the next is diffed against it, and the very first frame of the run against
  // nothing (`changed: -1`).
  let prev = null
  globalThis.__analyze = async (urls) => {
    const load = (u) => new Promise((res, rej) => { const im = new Image(); im.onload = () => res(im); im.onerror = rej; im.src = u })
    const imgs = await Promise.all(urls.map(load))
    const cv = document.createElement('canvas'); cv.width = imgs[0].width; cv.height = imgs[0].height
    const ctx = cv.getContext('2d', { willReadFrequently: true })
    const px = (im) => { ctx.drawImage(im, 0, 0); return ctx.getImageData(0, 0, cv.width, cv.height).data }
    const out = []
    for (const im of imgs) {
      const d = px(im)
      if (!prev) { out.push({ changed: -1, box: null }); prev = d; continue }
      let n = 0, x0 = 1e9, y0 = 1e9, x1 = -1, y1 = -1
      for (let i = 0; i < d.length; i += 4) {
        if (Math.abs(d[i] - prev[i]) + Math.abs(d[i + 1] - prev[i + 1]) + Math.abs(d[i + 2] - prev[i + 2]) > 30) {
          n++; const p = i >> 2, x = p % cv.width, y = (p / cv.width) | 0
          if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y
        }
      }
      out.push({ changed: n, box: n ? [x0 >> 1, y0 >> 1, (x1 - x0) >> 1, (y1 - y0) >> 1] : null })
      prev = d
    }
    return JSON.stringify(out)
  }
</script>"""

// --- what it accepts ---------------------------------------------------------------------------

let private managerOption = Cli.value "manager" "url" "the Manager to create the session on"
let private sessionOption = Cli.value "session" "url" "an existing session's address (or its /open page) to load instead of creating one"
let private secondsOption = Cli.value "seconds" "n" "how long to keep recording after the shell lands (default 8)"
let private outOption = Cli.value "out" "dir" "where the report goes (default ./frames)"
let private minPxOption = Cli.value "min-px" "n" "changed pixels a frame needs to be shown (default 400)"
let private desktopOption = Cli.flag "desktop" None "1280x800 instead of a 390x844 phone"
let private keepOption = Cli.flag "keep" None "leave the created session running"
let private warmOption =
    Cli.flag "warm" None "load once unfilmed, then film a reload: the cookie and kept history a returning reader has"
let private pageOption = Cli.value "page" "url" "any page to load instead of a session — a path is taken from --manager"
let private stillOption = Cli.flag "still" None "one picture once it lands, instead of a film"
let private sizeOption = Cli.value "size" "WxH" "the viewport, e.g. 390x844 or 1440x900 (default: --desktop's or a phone's)"
let private measureOption = Cli.value "measure" "expression" "with --still: JavaScript evaluated in the page before the picture, awaited, printed"

let private spec =
    Cli.spec "yession-frames"
    |> Cli.accepts managerOption
    |> Cli.accepts sessionOption
    |> Cli.accepts secondsOption
    |> Cli.accepts outOption
    |> Cli.accepts minPxOption
    |> Cli.accepts desktopOption
    |> Cli.accepts keepOption
    |> Cli.accepts warmOption
    |> Cli.accepts pageOption
    |> Cli.accepts stillOption
    |> Cli.accepts sizeOption
    |> Cli.accepts measureOption

// --- what the page answers ---------------------------------------------------------------------

// What the instrument and the analyzer leave in the page comes back as JSON text, and is read
// here with a codec: the shape each is written in is above, and this is the one place that
// says what it is read as.

/// The layout summary the instrument takes after a render (`look`): each key, in the order it
/// wrote them, with the value as it came. Open rather than declared field by field, because the
/// instrument decides what is worth reading, and this side only diffs it and passes it on.
type private Layout = (string * JsonValue) list

/// A client render the instrument logged: its number, when it landed on the document's clock,
/// and the layout it left — absent when the log was read before the microtask that takes it.
type private Render = { N: int; T: int; After: Layout option }

/// One line of the instrument's `__log`, by what it recorded. The class changes and the panes
/// added or removed are only passed on into `log.json`, so they are kept as they came.
type private Logged =
    | Rendered of Render
    | ClassChanged of JsonValue
    | PaneChanged of JsonValue
    | Resized

let private logged: Decoder<Logged> =
    // Present only on its own kind of line, so a field's presence says which line it is.
    let marked (field: string) (kind: JsonValue -> Logged) =
        Decode.field field Decode.string |> Decode.andThen (fun _ -> Decode.value) |> Decode.map kind

    Decode.oneOf
        [ Decode.object (fun get ->
              Rendered
                  { N = get.Required.Field "n" Decode.int
                    T = get.Required.Field "t" Decode.int
                    After = get.Optional.Field "after" (Decode.keyValuePairs Decode.value) })
          marked "attr" ClassChanged
          marked "pane" PaneChanged
          Decode.field "resize" (Decode.array Decode.int) |> Decode.map (fun _ -> Resized) ]

/// What the pixel diff says of one frame: how many pixels changed from the frame before (`-1`
/// for the first of the run), and the box around them in CSS px when any did.
type private Diff = { Changed: int; Box: int array option }

let private diff: Decoder<Diff> =
    Decode.object (fun get ->
        { Changed = get.Required.Field "changed" Decode.int
          Box = get.Optional.Field "box" (Decode.array Decode.int) })

/// What the page answered, read as `decoder` says. The JSON is this file's own instrument's,
/// so an answer it cannot read is a fault in this file, and the run stops on it.
let private decoded (what: string) (decoder: Decoder<'T>) (json: string) : 'T =
    match Decode.fromString decoder json with
    | Ok value -> value
    | Error why -> failwithf "%s did not decode: %s" what why

// --- the run -----------------------------------------------------------------------------------

type private Frame =
    { Index: int
      EpochMs: float
      Hash: string
      Data: string
      /// On the final document's clock; negative for frames painted before it began.
      T: int
      Renders: (Render * string list) list
      Changed: int
      Box: int array option }

let private frameName (f: Frame) = sprintf "frames/f%04d.png" f.Index

/// The layout summary's diff against the previous render's, one line per key that moved.
let private changes (before: Layout option) (after: Layout) : string list =
    match before with
    | None -> [ "(first)" ]
    | Some before ->
        after
        |> List.choose (fun (key, value) ->
            let was = before |> List.tryPick (fun (k, v) -> if k = key then Some (Encode.toString 0 v) else None)
            let now = Encode.toString 0 value

            match was with
            | Some was when was = now -> None
            | Some was -> Some (sprintf "%s: %s → %s" key was now)
            | None -> Some (sprintf "%s: (absent) → %s" key now))

let private run () =
    promise {
        // Never bundled, so the version an unbundled run of the Fable output reports (Version.fs).
        let args =
            match Cli.outcome spec "dev" (argv ()) with
            | Cli.Outcome.Proceed parsed -> parsed
            | Cli.Outcome.Answered text -> say text; exitWith 0
            | Cli.Outcome.Refused complaint -> abort complaint
        let manager = Cli.valueOf managerOption args |> Option.map (fun m -> m.TrimEnd '/')
        let needManager () =
            match manager with
            | Some m -> m
            | None -> abort "yession-frames needs --manager: the Manager the session lives on (or --page with a full address)"
        let seconds = Cli.valueOf secondsOption args |> Option.map int |> Option.defaultValue 8
        let minPx = Cli.valueOf minPxOption args |> Option.map int |> Option.defaultValue 400
        let out = Node.Api.path.resolve (Cli.valueOf outOption args |> Option.defaultValue "frames")
        let still = Cli.isSet stillOption args
        let page = Cli.valueOf pageOption args
        // A desktop is the stylesheet's `wide` screen: not a phone. The question is asked once,
        // where `@custom-variant phone` in app/tailwind.css asks it (width < 48rem = 768px, OR
        // height <= 500px), so a phone turned on its side (844x390) gets a finger, as the real
        // device does, and not the mouse a width rule gave it. `--desktop` is just 1280x800.
        let width, height =
            match Cli.valueOf sizeOption args with
            | Some size ->
                match size.Split 'x' |> Array.map Int32.TryParse with
                | [| (true, w); (true, h) |] -> w, h
                | _ -> abort (sprintf "--size %s: want WIDTHxHEIGHT, e.g. 390x844" size)
            | None -> if Cli.isSet desktopOption args then 1280, 800 else 390, 844
        let desktop = not (width < 768 || height <= 500)
        Fable.NodeExtras.Files.removeTree out
        Fable.NodeExtras.Files.mkdirp (if still then out else joinTwo out "frames")

        // The page: one named outright, an existing session, or a session made the way the
        // form makes one — a POST, answered with the `/open` page that launches it and hands
        // the browser over once its address answers.
        let! openUrl, created =
            promise {
                match page, Cli.valueOf sessionOption args with
                | Some page, _ -> return (if page.StartsWith "/" then needManager () + page else page), false
                | None, Some given -> return given, false
                | None, None ->
                    let manager = needManager ()
                    // The form the Create button submits, with nothing in it: the Manager
                    // mints the id when a submission names none, and what comes back is the
                    // `/open` page's address in a `Location` this does NOT follow — the camera
                    // wants to navigate Chromium there, not fetch it here.
                    let! r =
                        Fetch.fetchUnsafe
                            (ManagerRoute.at manager ManagerRoute.CreateSession)
                            [ Fetch.Types.RequestProperties.Method Fetch.Types.HttpMethod.POST
                              Fetch.Types.RequestProperties.Redirect Fetch.Types.RedirectMode.Manual
                              Fetch.requestHeaders
                                  [ Fetch.Types.HttpRequestHeaders.ContentType "application/x-www-form-urlencoded" ]
                              Fetch.Types.RequestProperties.Body (Fetch.Types.BodyInit.Case3 "") ]
                    match r.Status, r.Headers.Location with
                    | 303, Some location -> return (sprintf "%s%s" manager location), true
                    | status, _ -> return failwithf "creating a session: %d" status
            }
        say (sprintf "open %s" openUrl)

        // Stopped, like the probe's: a session made for a camera has nobody coming back to it.
        let stopCreated () =
            promise {
                if created && not (Cli.isSet keepOption args) then
                    // Which session, read back off the `/open` address the Manager answered
                    // with — through the same parser the Manager routes it by, so a route that
                    // moves moves both ends of this at once.
                    let id =
                        match ManagerRoute.parse "GET" (Fable.BrowserExtras.Urls.pathname openUrl) with
                        | Ok (ManagerRoute.OpenSession id) -> id
                        | _ -> failwithf "not an /open address: %s" openUrl
                    let! stopped =
                        Fetch.fetchUnsafe
                            (ManagerRoute.at (needManager ()) (ManagerRoute.Session (id, SessionVerb.Stop)))
                            [ Fetch.Types.RequestProperties.Method Fetch.Types.HttpMethod.POST ]
                    say (sprintf "stop %s %d" (SessionId.value id) stopped.Status)
            }

        // The document is the session shell, wherever the deployment mounts it (a path under
        // the Manager, a port of its own): the shell is the one document with the client's
        // mount in it, and the Manager's opening page has none.
        let shellProbe = sprintf "document.getElementById('%s') ? location.href : ''" Yession.App.Dom.appId
        // Landed: a named page once it has loaded; otherwise once the document is the shell.
        let landedProbe =
            match page with
            | Some _ -> "document.readyState === 'complete' ? location.href : ''"
            | None -> shellProbe

        // Headless Chromium reports no pointing device, so `(hover: hover)` is false and every
        // Tailwind `hover:` utility — wrapped in that query — silently does nothing: a desktop
        // picture of a hovered control would be its rest state. A desktop width is given the
        // pointer a desktop has. (`Emulation.setEmulatedMedia` cannot set these, and
        // `setTouchEmulationEnabled` switches them back off.) A phone keeps the default, which
        // is what a phone is.
        let pointer =
            if desktop then
                [ "--blink-settings=primaryHoverType=2,availableHoverTypes=2,primaryPointerType=4,availablePointerTypes=4" ]
            else []
        let! launched = Fable.NodeExtras.Chromium.launch delay width height pointer
        let browser =
            match launched with
            | Ok browser -> browser
            | Error why -> abort why
        let! cdp = Cdp.connect browser.Page
        do! Cdp.Page.enable cdp
        do! Cdp.Runtime.enable cdp
        do! Cdp.Network.enable cdp

        if still then
            // A TRUE device viewport. Headless Chromium clamps its window to ~500px wide, so a
            // `--window-size` phone is a 500px layout cropped to 390 — overflow hides and wrapping
            // never happens. The metrics override is what a phone-width picture means.
            do! Cdp.Emulation.setDeviceMetricsOverride cdp width height 2.0 (not desktop)
            // A headless page is never the focused window, so `:focus` matches nothing and a
            // "focused" picture is the rest state. This makes the page believe it is frontmost.
            do! Cdp.Emulation.setFocusEmulationEnabled cdp true
            // And a phone is a finger: no hover, a coarse pointer. Without this a phone-width
            // picture on a desktop host keeps the host's mouse (`hover: true` on a Mac), and a
            // hover-only affordance photographs as present where a phone never shows it.
            if not desktop then
                do! Cdp.Emulation.setTouchEmulationEnabled cdp true 5
            do! Cdp.Page.navigate cdp openUrl
            let mutable landed = ""
            let startedAt = clock.Now ()
            while landed = "" && (clock.Now () - startedAt).TotalSeconds < 60.0 do
                do! delay 250
                let! href = landedAt cdp landedProbe
                landed <- href
            // Settled, not merely landed: a client connects and renders after its document
            // loads, and the first frame of a transition is not where it arrives.
            do! delay 1500
            // The ground truth beside the picture: trust it only when `vw` is the width asked
            // for, and `overflowX` is a page a phone cannot scroll back from.
            let! truth = evaluateText cdp "JSON.stringify({ vw: innerWidth, docW: document.documentElement.scrollWidth, overflowX: document.documentElement.scrollWidth > innerWidth, hover: matchMedia('(hover: hover)').matches })"
            say (answered "the viewport's measurements" truth)
            match Cli.valueOf measureOption args with
            | Some expression ->
                let! measured = evaluateJson cdp expression
                say (answered "--measure" measured)
            | None -> ()
            let! shot = Cdp.Page.captureScreenshot cdp
            let picture = joinTwo out "still.png"
            writeBytes picture (bytesOfBase64 shot)
            say (sprintf "wrote %s (%dx%d)" picture width height)
            cdp.Close ()
            Fable.NodeExtras.Chromium.close browser
            do! stopCreated ()
            // A page that never landed is a failed picture of SOMETHING, kept because what the
            // tab showed is usually why — and said with a code, so a caller cannot read it as a
            // picture of the page it asked for.
            if landed = "" then
                say (sprintf "never landed in 60s: %s shows whatever the tab had" picture)
                exitWith 1
            exitWith 0

        if not desktop then
            do! Cdp.Emulation.setDeviceMetricsOverride cdp width height 2.0 true
        // A returning reader: signed in, and holding the history its last visit kept. One
        // unfilmed load, left `--seconds` to read the log through and keep it, and then the
        // filmed load is the shell's own address, as a reload is — not the `/open` page a new
        // reader starts from. What the warm-up said is dropped, so the report is the reload's.
        let! openUrl =
            promise {
                if not (Cli.isSet warmOption args) then return openUrl
                else
                    do! Cdp.Page.navigate cdp openUrl
                    let mutable shell = ""
                    let startedAt = clock.Now ()
                    while shell = "" && (clock.Now () - startedAt).TotalSeconds < 60.0 do
                        do! delay 250
                        let! href = landedAt cdp shellProbe
                        shell <- href
                    if shell = "" then abort "--warm: the shell never landed on the unfilmed load"
                    do! delay (seconds * 1000)
                    do! Cdp.Page.navigate cdp "about:blank"
                    do! delay 500
                    cdp.Events.Clear ()
                    say (sprintf "warmed on %s" shell)
                    return shell
            }
        let! installed = Cdp.Page.addScriptToEvaluateOnNewDocument cdp instrument
        do! Cdp.Page.startScreencast cdp 1 (width * 2) (height * 2)
        let navigatedAt = clock.Now ()
        do! Cdp.Page.navigate cdp openUrl
        let mutable landed = false
        let mutable since = clock.Now ()
        while (clock.Now () - since).TotalMilliseconds < float (if landed then seconds else 60) * 1000.0 do
            do! delay 250
            let! href = landedAt cdp shellProbe
            if not landed && href <> "" then
                landed <- true
                since <- clock.Now ()
                say (sprintf "landed %s after %.0f ms" href (clock.Now () - navigatedAt).TotalMilliseconds)
        if not landed then say "the shell never landed; the frames are of whatever the tab showed"
        do! Cdp.Page.stopScreencast cdp
        let! originAnswer = evaluateNumber cdp "__origin"
        let origin = answered "__origin, the instrument's clock" originAnswer
        let! logAnswer = evaluateText cdp "JSON.stringify(__log)"
        let log = decoded "__log, the instrument's renders" (Decode.list logged) (answered "__log, the instrument's renders" logAnswer)
        let! rafsAnswer = evaluateText cdp "JSON.stringify(__frames)"
        let rafs = decoded "__frames, the instrument's frames" Decode.value (answered "__frames, the instrument's frames" rafsAnswer)
        let! sessionUrlAnswer = evaluateText cdp "location.href"
        let sessionUrl = answered "location.href" sessionUrlAnswer

        // The browser's side: frames, on the final document's clock, deduplicated by picture.
        let frames =
            cdp.Events
            |> Seq.choose (function
                | Cdp.Event.ScreencastFrame (data, Some epoch) -> Some (data, epoch)
                // A frame that says nothing of when it was painted cannot go on the page's clock,
                // and every number the report derives from it would be a plausible wrong one.
                | Cdp.Event.ScreencastFrame (_, None) -> failwith "a screencast frame came without a timestamp"
                | _ -> None)
            |> Seq.mapi (fun i (data, epoch) ->
                { Index = i; EpochMs = epoch * 1000.0
                  Hash = sha1Of data
                  Data = data; T = int (System.Math.Round (epoch * 1000.0 - origin)); Renders = []; Changed = -1; Box = None })
            |> Seq.toList
        let distinct = frames |> List.fold (fun acc f -> match acc with | prev :: _ when prev.Hash = f.Hash -> acc | _ -> f :: acc) [] |> List.rev
        let requests =
            cdp.Events
            |> Seq.choose (fun event ->
                let document (resourceType: string option) = (resourceType = Some "Document")
                let named (url: string) = url.Contains "/me" || url.Contains "/login"
                match event with
                | Cdp.Event.RequestSent (resourceType, url, redirectedWith) when document resourceType || named url ->
                    let via = match redirectedWith with | Some status -> sprintf " (after %d)" status | None -> ""
                    Some (sprintf "request  %s%s" url via)
                | Cdp.Event.ResponseReceived (resourceType, url, status) when document resourceType || named url ->
                    Some (sprintf "response %s %d" url status)
                | Cdp.Event.FrameNavigated (url, true) -> Some (sprintf "navigated %s" url)
                | _ -> None)
            |> Seq.toList

        // The client's side: renders, each with what it changed.
        let renders = log |> List.choose (function Rendered r -> Some r | _ -> None)
        let rendersWithChanges =
            renders
            |> List.fold (fun (acc, prev) r ->
                match r.After with
                | Some after -> ((r, changes prev after) :: acc, Some after)
                | None -> ((r, []) :: acc, prev)) ([], None)
            |> fst |> List.rev

        // Each frame carries the renders that landed since the previous distinct frame; a
        // render after the last frame is listed on its own.
        let attributedRev, trailing =
            distinct
            |> List.fold (fun (done_, pending: (Render * string list) list) f ->
                let mine, later = pending |> List.partition (fun (r, _) -> r.T <= f.T)
                ({ f with Renders = mine } :: done_, later)) ([], rendersWithChanges)
        let attributed = List.rev attributedRev

        // The pixel diff, and the cut: shown when the picture changed by more than the stamp
        // does, or when a render landed in it. The instrument comes out first, or the analyzer
        // and the sheets would wear a stamp of their own.
        do! Cdp.Page.removeScriptToEvaluateOnNewDocument cdp installed
        writeText (joinTwo out "analyze.html") analyzer
        do! Cdp.Page.navigate cdp (sprintf "file://%s" (joinTwo out "analyze.html"))
        do! delay 500
        // In batches: one `Runtime.evaluate` carrying every frame of a long run as data URLs
        // is tens of megabytes of expression, and the browser never answers it.
        let batches = attributed |> List.chunkBySize 32
        let diffs = ResizeArray<Diff> ()
        for batch in batches do
            let urls = batch |> List.map (fun f -> "data:image/png;base64," + f.Data) |> List.toArray
            let! diffsAnswer = evaluateText cdp (sprintf "__analyze(%s)" (JS.JSON.stringify urls))
            diffs.AddRange (decoded "__analyze, the pixel diff" (Decode.array diff) (answered "__analyze, the pixel diff" diffsAnswer))
        let analyzed =
            attributed
            |> List.mapi (fun i f ->
                let d = diffs.[i]
                { f with Changed = d.Changed; Box = d.Box })
        let shown = analyzed |> List.filter (fun f -> f.Index = 0 || f.Changed >= minPx || not (List.isEmpty f.Renders))
        for f in shown do writeBytes (joinTwo out (frameName f)) (bytesOfBase64 f.Data)

        // The report.
        let renderLines (r: Render, lines: string list) =
            lines |> List.map (fun c -> sprintf "<code>#%d %s</code>" r.N (escapeHtml c)) |> String.concat ""
        let card (f: Frame) =
            let renderList =
                if List.isEmpty f.Renders then "<i>no render</i>"
                else sprintf "renders %s" (f.Renders |> List.map (fun (r, _) -> sprintf "#%d" r.N) |> String.concat " ")
            let boxHtml =
                match f.Box with
                | Some b -> sprintf "<div class=box style=\"left:%dpx;top:%dpx;width:%dpx;height:%dpx\"></div>" (b.[0] / 2) (b.[1] / 2) (b.[2] / 2) (b.[3] / 2)
                | None -> ""
            let delta = if f.Changed >= 0 then sprintf " Δ%dpx" f.Changed else ""
            sprintf "<figure><div class=im style=\"width:%dpx;height:%dpx\"><img src=\"%s\" width=\"%d\">%s</div><figcaption><b>f%d</b> t=%dms%s<br>%s%s</figcaption></figure>"
                (width / 2) (height / 2) (frameName f) (width / 2) boxHtml f.Index f.T delta renderList
                (f.Renders |> List.map renderLines |> String.concat "")
        let style =
            sprintf "<style>body{font:11px system-ui;margin:8px;background:#fff;color:#000}figure{display:inline-block;vertical-align:top;margin:4px;width:%dpx}.im{position:relative;border:1px solid #999}.im img{display:block}.box{position:absolute;border:2px solid red;box-sizing:border-box}figcaption{word-break:break-all}code{font-size:9px;display:block}h2{margin:16px 0 4px}</style>"
                (width / 2 + 4)
        let allRenders =
            rendersWithChanges
            |> List.map (fun (r, lines) -> sprintf "<code>#%d t=%d %s</code>" r.N r.T (escapeHtml (if List.isEmpty lines then "(no layout change)" else String.concat " ; " lines)))
            |> String.concat ""
        let report =
            String.concat "\n"
                [ sprintf "<!doctype html><meta charset=utf-8><title>first load</title>%s" style
                  sprintf "<h1>%s</h1><p>%d frames painted, %d distinct, %d shown (Δ ≥ %dpx or carrying a render); %d renders</p>" (escapeHtml sessionUrl) (List.length frames) (List.length distinct) (List.length shown) minPx (List.length renders)
                  sprintf "<h2>Navigations and auth</h2><pre>%s</pre>" (requests |> List.map escapeHtml |> String.concat "\n")
                  "<p>The stamp in each frame's corner is written by the page itself: rN = renders landed, tN = ms since that document started. A stamp whose clock runs backwards belongs to an earlier document; the navigation timeline above says why there was one.</p>"
                  sprintf "<h2>Frames</h2>%s" (shown |> List.map card |> String.concat "\n")
                  (if List.isEmpty trailing then "" else sprintf "<h2>Renders after the last frame</h2>%s" (trailing |> List.map renderLines |> String.concat ""))
                  sprintf "<h2>All renders</h2>%s" allRenders ]
        writeText (joinTwo out "report.html") report
        // Written with the codec that read it, keys in alphabetical order, which is the order the
        // file has always had. A key with nothing to say is left out rather than written empty:
        // a frame with no box, a render the log has no layout for.
        let logOut =
            Encode.object
                [ "frames",
                  analyzed
                  |> List.map (fun f ->
                      Encode.object
                          [ match f.Box with
                            | Some b -> yield "box", Encode.array (Array.map Encode.int b)
                            | None -> ()
                            yield "changed", Encode.int f.Changed
                            yield "i", Encode.int f.Index
                            yield "renders", Encode.list (f.Renders |> List.map (fun (r, _) -> Encode.int r.N))
                            yield "t", Encode.int f.T ])
                  |> Encode.list
                  "html", Encode.list (log |> List.choose (function ClassChanged line -> Some line | _ -> None))
                  "origin", Encode.float origin
                  "panes", Encode.list (log |> List.choose (function PaneChanged line -> Some line | _ -> None))
                  "rafs", rafs
                  "renders",
                  rendersWithChanges
                  |> List.map (fun (r, lines) ->
                      Encode.object
                          [ match r.After with
                            | Some after -> yield "after", Encode.object after
                            | None -> ()
                            yield "changes", Encode.list (List.map Encode.string lines)
                            yield "n", Encode.int r.N
                            yield "t", Encode.int r.T ])
                  |> Encode.list
                  "requests", Encode.list (List.map Encode.string requests)
                  "sessionUrl", Encode.string sessionUrl ]
        writeText (joinTwo out "log.json") (Encode.toString 1 logOut)

        // Contact sheets, by the same Chrome: eight frames a page, at a size the Read tool reads.
        let cols, rows = 4, 2
        let cw, ch = width / 2 + 16, height / 2 + 170
        do! Cdp.Emulation.clearDeviceMetricsOverride cdp
        do! Cdp.Emulation.setDeviceMetricsOverride cdp (cols * cw + 24) (rows * ch + 24) 1.0 false
        let pages = shown |> List.chunkBySize (cols * rows)
        for (p, page) in List.indexed pages do
            let html =
                sprintf "<!doctype html><meta charset=utf-8>%s<style>body{display:grid;grid-template-columns:repeat(%d,%dpx);grid-auto-rows:%dpx}figure{margin:0;overflow:hidden}</style>%s"
                    style cols cw ch (page |> List.map card |> String.concat "\n")
            let sheet = joinTwo out (sprintf "sheet-%d.html" p)
            writeText sheet html
            do! Cdp.Page.navigate cdp (sprintf "file://%s" sheet)
            do! delay 800
            let! shot = Cdp.Page.captureScreenshot cdp
            writeBytes (joinTwo out (sprintf "sheet-%d.png" p)) (bytesOfBase64 shot)

        say (sprintf "frames %d distinct %d shown %d renders %d; %s" (List.length frames) (List.length distinct) (List.length shown) (List.length renders) (joinTwo out "report.html"))
        for r in requests do say r
        for (r, lines) in rendersWithChanges do
            if not (List.isEmpty lines) then say (sprintf "#%d t=%d %s" r.N r.T (String.concat " ; " lines))

        cdp.Close ()
        Fable.NodeExtras.Chromium.close browser
        do! stopCreated ()
        exitWith 0
    }

run () |> Promise.start
