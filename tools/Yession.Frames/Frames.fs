module Yession.Frames

// A session's first load, frame by frame, against a real deployment.
//
//   dotnet fsi tasks.fsx frames --manager https://home.example:8321            # a fresh session
//   dotnet fsi tasks.fsx frames --manager … --session https://…/s/<id>/       # one that exists
//   dotnet fsi tasks.fsx frames --manager … --seconds 20 --desktop --out ./frames
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
// What it found on the first run, so the next reader knows what a clean run looks like
// (phone viewport, a fresh session, Chromium): SIX jumps before a repo is even chosen. The
// server-rendered shell paints the terminals pane OPEN full-width (the `<html>` element
// carries no `term-closed`; only the first client render adds it), so the pane then slides
// shut over 200ms; `/me` answers 401, the shell bounces through `/login` and LOADS AGAIN, so
// both happen twice; then the repo picker appears at the foot of the conversation, and grows
// to 982px when the list arrives, scrolling the pinned conversation by 360px to keep its end.
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
// The browser is the one the suite uses (`PLAYWRIGHT_BROWSERS_PATH`, see `Browser.fs`), so
// run this inside the dev shell; `CHROMIUM_PATH` overrides it, as there.
//
// Caveats that shape what it can say. The stamp changes every animation frame, so it FORCES
// a paint per frame — frame counts are the stamp's, and a frame whose only change is the
// stamp is dropped by the pixel threshold (`--min-px`). Screencast timestamps and the page's
// clock are different clocks; the stamp is the truth and the timestamps are for ordering.
// The session is created the way the Create button creates one (`POST /sessions`, then the
// `/open` page that launches it and hands the browser over) and STOPPED afterwards unless
// `--keep`; a `--session` given is left as it was found. And it measures one load on one
// machine: what it says is where the jumps come from, never how long they take.

open Fable.Core
open Fable.Core.JsInterop
open Yession.Domain

// --- Node --------------------------------------------------------------------------------------

let private fs : obj = importAll "node:fs"
let private os : obj = importAll "node:os"
let private nodePath : obj = importAll "node:path"
let private childProcess : obj = importAll "node:child_process"
let private crypto : obj = importAll "node:crypto"

[<Emit("fetch($0, $1)")>]
let private fetch (url: string) (init: obj) : JS.Promise<obj> = jsNative

[<Emit("Buffer.from($0, 'base64')")>]
let private bytesOfBase64 (data: string) : obj = jsNative

[<Emit("new WebSocket($0)")>]
let private webSocket (url: string) : obj = jsNative

[<Emit("new Promise(resolve => setTimeout(resolve, $0))")>]
let private delay (ms: int) : JS.Promise<unit> = jsNative

[<Emit("process.env[$0] ?? ''")>]
let private env (name: string) : string = jsNative

[<Emit("process.exit($0)")>]
let private exitWith (code: int) : unit = jsNative

[<Emit("JSON.stringify($0, null, 1)")>]
let private pretty (value: obj) : string = jsNative

[<Emit("console.log($0)")>]
let private say (line: string) : unit = jsNative

[<Emit("Date.now()")>]
let private now () : float = jsNative

[<Emit("String($0).padStart($1, '0')")>]
let private padded (n: int) (width: int) : string = jsNative

let private joinTwo (a: string) (b: string) : string = nodePath?join (a, b) |> unbox
let private writeFile (path: string) (content: obj) : unit = fs?writeFileSync (path, content) |> ignore
let private readDir (path: string) : string array = fs?readdirSync path |> unbox
let private exists (path: string) : bool = fs?existsSync path |> unbox
let private isDirectory (path: string) : bool = try (fs?statSync path)?isDirectory () |> unbox with _ -> false
let private realPath (path: string) : string = try fs?realpathSync path |> unbox with _ -> path
let private escapeHtml (s: string) = s.Replace("&", "&amp;").Replace ("<", "&lt;")

// --- Chromium ----------------------------------------------------------------------------------

/// The same rule as the suite's (`Browser.fs`): `CHROMIUM_PATH` if set, else the executable
/// found BY NAME under a `chromium-<revision>` directory of `PLAYWRIGHT_BROWSERS_PATH`. No
/// fallback to a system Chrome, for the reason given there — a revision is pinned so the
/// picture matches what the suite drives.
let private chromiumExecutableNames = set [ "chrome"; "Google Chrome for Testing"; "Chromium" ]

let private chromiumPath () : string =
    match env "CHROMIUM_PATH" with
    | "" ->
        match env "PLAYWRIGHT_BROWSERS_PATH" with
        | "" -> Cli.abort "no Chromium: PLAYWRIGHT_BROWSERS_PATH is unset (the dev shell sets it); outside it, set CHROMIUM_PATH"
        | root ->
            let rec walk (dir: string) : string list =
                readDir dir
                |> Array.toList
                |> List.collect (fun name ->
                    let path = joinTwo dir name
                    if isDirectory path then walk path
                    elif chromiumExecutableNames.Contains name then [ path ]
                    else [])
            let revisions =
                if isDirectory root then
                    readDir root |> Array.filter (fun d -> d.StartsWith "chromium-") |> Array.sort |> Array.toList
                else []
            match revisions |> List.collect (fun d -> try walk (realPath (joinTwo root d)) with _ -> []) with
            | found :: _ -> found
            | [] -> Cli.abort (sprintf "no Chromium under %s (%d chromium-* revision(s)); set CHROMIUM_PATH" root (List.length revisions))
    | given -> given

// --- CDP ---------------------------------------------------------------------------------------

type private Cdp =
    { Send: string -> obj -> JS.Promise<obj>
      /// Every event the browser sends, as it arrives: method and params.
      Events: ResizeArray<string * obj>
      /// Screencast frames are ACKED here as they arrive, or the browser stops sending them.
      Close: unit -> unit }

let private connect (url: string) : JS.Promise<Cdp> =
    Promise.create (fun resolve _ ->
        let ws = webSocket url
        let pending = System.Collections.Generic.Dictionary<int, obj -> unit> ()
        let events = ResizeArray<string * obj> ()
        let mutable nextId = 1
        let send (methodName: string) (parameters: obj) : JS.Promise<obj> =
            Promise.create (fun ok _ ->
                let id = nextId
                nextId <- nextId + 1
                pending.[id] <- ok
                ws?send (JS.JSON.stringify (createObj [ "id" ==> id; "method" ==> methodName; "params" ==> parameters ])) |> ignore)
        ws?onmessage <- fun (e: obj) ->
            let msg = JS.JSON.parse (e?data |> unbox)
            match msg?id |> unbox<int option> with
            | Some id when pending.ContainsKey id ->
                let ok = pending.[id]
                pending.Remove id |> ignore
                ok (msg?result)
            | _ ->
                match msg?``method`` |> unbox<string option> with
                | Some name ->
                    if name = "Page.screencastFrame" then
                        send "Page.screencastFrameAck" (createObj [ "sessionId" ==> msg?``params``?sessionId ]) |> ignore
                    events.Add (name, msg?``params``)
                | None -> ()
        ws?onopen <- fun () -> resolve { Send = send; Events = events; Close = fun () -> ws?close () |> ignore })

/// A headless Chromium on a debugging port of the OS's choosing, read back from the file it
/// writes into its profile directory — no port to collide on.
let private launch (executable: string) (width: int) (height: int) : JS.Promise<obj * string> =
    promise {
        let profile : string = fs?mkdtempSync (joinTwo (os?tmpdir () |> unbox) "yession-frames-") |> unbox
        let args =
            // `--use-mock-keychain`, as Playwright passes: without it a Chromium the OS has not
            // seen before stops in its network service, waiting on a Keychain prompt nobody
            // can answer, and the first navigation never gets a response.
            [| "--headless=new"; "--remote-debugging-port=0"; sprintf "--user-data-dir=%s" profile; "--no-first-run"
               "--use-mock-keychain"; "--password-store=basic"; "--ignore-certificate-errors"
               sprintf "--window-size=%d,%d" width height; "about:blank" |]
        let child = childProcess?spawn (executable, args, createObj [ "stdio" ==> "ignore" ])
        let portFile = joinTwo profile "DevToolsActivePort"
        let mutable port = ""
        let mutable waited = 0
        while port = "" && waited < 100 do
            if exists portFile then
                let lines : string = fs?readFileSync (portFile, "utf8") |> unbox
                port <- lines.Split('\n').[0].Trim ()
            if port = "" then
                do! delay 100
                waited <- waited + 1
        if port = "" then failwith "Chromium never wrote DevToolsActivePort"
        let mutable target = ""
        waited <- 0
        while target = "" && waited < 50 do
            try
                let! listed = fetch (sprintf "http://127.0.0.1:%s/json/list" port) (createObj [])
                let! json = listed?json () |> unbox<JS.Promise<obj array>>
                match json |> Array.tryFind (fun t -> t?``type`` = "page") with
                | Some page -> target <- page?webSocketDebuggerUrl |> unbox
                | None -> ()
            with _ -> ()
            if target = "" then
                do! delay 200
                waited <- waited + 1
        if target = "" then failwith "Chromium offered no page target"
        return child, target
    }

/// `Runtime.evaluate`, awaited, by value. An exception in the expression is printed rather
/// than swallowed, because a probe that silently evaluates to `undefined` is a probe that
/// reports a clean page.
let private evaluate (cdp: Cdp) (expression: string) : JS.Promise<obj> =
    promise {
        let! r = cdp.Send "Runtime.evaluate" (createObj [ "expression" ==> expression; "awaitPromise" ==> true; "returnByValue" ==> true ])
        match r?exceptionDetails |> unbox<obj option> with
        | Some details -> say (sprintf "evaluate: %s" ((JS.JSON.stringify details).Substring (0, 300)))
        | None -> ()
        return r?result?value
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
      pane: rect('[data-terminal-panel]'), chat: rect('[data-conversation]'), composer: rect('[data-draft-editor]'),
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
  const panesIn = (n) => n.nodeType !== 1 ? [] : n.matches('[data-terminal-panel]') ? [n] : [...n.querySelectorAll('[data-terminal-panel]')]
  new MutationObserver((ms) => { for (const m of ms) { for (const n of m.addedNodes) for (const p of panesIn(n)) __log.push({ t: t(), pane: 'added', id: idOf(p) }); for (const n of m.removedNodes) for (const p of panesIn(n)) __log.push({ t: t(), pane: 'removed', id: idOf(p) }) } })
    .observe(document, { subtree: true, childList: true })
  globalThis.__frames = []
  const tick = () => {
    const pane = document.querySelector('[data-terminal-panel]'); const app = document.querySelector('#app')
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
  globalThis.__analyze = async (urls) => {
    const load = (u) => new Promise((res, rej) => { const im = new Image(); im.onload = () => res(im); im.onerror = rej; im.src = u })
    const imgs = await Promise.all(urls.map(load))
    const cv = document.createElement('canvas'); cv.width = imgs[0].width; cv.height = imgs[0].height
    const ctx = cv.getContext('2d', { willReadFrequently: true })
    const px = (im) => { ctx.drawImage(im, 0, 0); return ctx.getImageData(0, 0, cv.width, cv.height).data }
    const out = []; let prev = null
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

let private spec =
    Cli.spec "yession-frames" [ managerOption; sessionOption; secondsOption; outOption; minPxOption; desktopOption; keepOption ]

// --- the run -----------------------------------------------------------------------------------

type private Frame =
    { Index: int
      EpochMs: float
      Hash: string
      Data: string
      /// On the final document's clock; negative for frames painted before it began.
      T: int
      Renders: (obj * string list) list
      Changed: int
      Box: int array option }

let private frameName (f: Frame) = sprintf "frames/f%s.png" (padded f.Index 4)

/// The layout summary's diff against the previous render's, one line per key that moved.
let private changes (before: obj option) (after: obj) : string list =
    match before with
    | None -> [ "(first)" ]
    | Some before ->
        JS.Constructors.Object.keys after
        |> Seq.choose (fun key ->
            let x = JS.JSON.stringify (before?(key))
            let y = JS.JSON.stringify (after?(key))
            if x <> y then Some (sprintf "%s: %s → %s" key x y) else None)
        |> Seq.toList

let private run () =
    promise {
        // Never bundled, so the version an unbundled run of the Fable output reports (Version.fs).
        let args = Cli.parseOrExit spec "dev"
        let manager =
            match Cli.valueOf managerOption args with
            | Some m -> m.TrimEnd '/'
            | None -> Cli.abort "yession-frames needs --manager: the Manager the session lives on"
        let seconds = Cli.valueOf secondsOption args |> Option.map int |> Option.defaultValue 8
        let minPx = Cli.valueOf minPxOption args |> Option.map int |> Option.defaultValue 400
        let out : string = nodePath?resolve (Cli.valueOf outOption args |> Option.defaultValue "frames") |> unbox
        let desktop = Cli.isSet desktopOption args
        let width, height = if desktop then 1280, 800 else 390, 844
        fs?rmSync (out, createObj [ "recursive" ==> true; "force" ==> true ]) |> ignore
        fs?mkdirSync (joinTwo out "frames", createObj [ "recursive" ==> true ]) |> ignore

        // The session, made the way the form makes one: a POST, answered with the `/open` page
        // that launches it and hands the browser over once its address answers.
        let! openUrl, created =
            promise {
                match Cli.valueOf sessionOption args with
                | Some given -> return given, false
                | None ->
                    let! r =
                        fetch (sprintf "%s/sessions" manager)
                            (createObj [ "method" ==> "POST"; "redirect" ==> "manual"; "headers" ==> createObj [ "content-type" ==> "application/x-www-form-urlencoded" ]; "body" ==> "" ])
                    let status : int = r?status |> unbox
                    let location : string = r?headers?get "location" |> unbox
                    if status <> 303 || isNull location then failwithf "creating a session: %d" status
                    return (sprintf "%s%s" manager location), true
            }
        say (sprintf "open %s" openUrl)

        let! child, target = launch (chromiumPath ()) width height
        let! cdp = connect target
        let! _ = cdp.Send "Page.enable" (createObj [])
        let! _ = cdp.Send "Runtime.enable" (createObj [])
        let! _ = cdp.Send "Network.enable" (createObj [])
        if not desktop then
            let! _ = cdp.Send "Emulation.setDeviceMetricsOverride" (createObj [ "width" ==> width; "height" ==> height; "deviceScaleFactor" ==> 2; "mobile" ==> true ])
            ()
        let! installed = cdp.Send "Page.addScriptToEvaluateOnNewDocument" (createObj [ "source" ==> instrument ])
        let! _ = cdp.Send "Page.startScreencast" (createObj [ "format" ==> "png"; "everyNthFrame" ==> 1; "maxWidth" ==> width * 2; "maxHeight" ==> height * 2 ])
        let navigatedAt = now ()
        let! _ = cdp.Send "Page.navigate" (createObj [ "url" ==> openUrl ])
        let mutable landed = false
        let mutable since = now ()
        while now () - since < float (if landed then seconds else 60) * 1000.0 do
            do! delay 250
            let! href = evaluate cdp "location.href"
            let href : string = match unbox href with | null -> "" | s -> s
            if not landed && href.Contains "/s/" then
                landed <- true
                since <- now ()
                say (sprintf "landed %s after %.0f ms" href (now () - navigatedAt))
        if not landed then say "the shell never landed; the frames are of whatever the tab showed"
        let! _ = cdp.Send "Page.stopScreencast" (createObj [])
        let! origin = evaluate cdp "__origin"
        let origin : float = unbox origin
        let! logJson = evaluate cdp "JSON.stringify(__log)"
        let log : obj array = JS.JSON.parse (unbox logJson) |> unbox
        let! rafsJson = evaluate cdp "JSON.stringify(__frames)"
        let! sessionUrl = evaluate cdp "location.href"
        let sessionUrl : string = unbox sessionUrl

        // The browser's side: frames, on the final document's clock, deduplicated by picture.
        let frames =
            cdp.Events
            |> Seq.filter (fun (name, _) -> name = "Page.screencastFrame")
            |> Seq.mapi (fun i (_, p) ->
                let data : string = p?data |> unbox
                let epoch : float = p?metadata?timestamp |> unbox
                { Index = i; EpochMs = epoch * 1000.0
                  Hash = (crypto?createHash "sha1")?update(data)?digest("hex") |> unbox
                  Data = data; T = int (System.Math.Round (epoch * 1000.0 - origin)); Renders = []; Changed = -1; Box = None })
            |> Seq.toList
        let distinct = frames |> List.fold (fun acc f -> match acc with | prev :: _ when prev.Hash = f.Hash -> acc | _ -> f :: acc) [] |> List.rev
        let requests =
            cdp.Events
            |> Seq.choose (fun (name, p) ->
                let document (t: obj) = (unbox<string> t) = "Document"
                let named (url: string) = url.Contains "/me" || url.Contains "/login"
                match name with
                | "Network.requestWillBeSent" when document p?``type`` || named (unbox p?request?url) ->
                    let via = match p?redirectResponse |> unbox<obj option> with | Some r -> sprintf " (after %d)" (unbox<int> r?status) | None -> ""
                    Some (sprintf "request  %s%s" (unbox<string> p?request?url) via)
                | "Network.responseReceived" when document p?``type`` || named (unbox p?response?url) ->
                    Some (sprintf "response %s %d" (unbox<string> p?response?url) (unbox<int> p?response?status))
                | "Page.frameNavigated" when isNull (p?frame?parentId) -> Some (sprintf "navigated %s" (unbox<string> p?frame?url))
                | _ -> None)
            |> Seq.toList

        // The client's side: renders, each with what it changed.
        let renders = log |> Array.filter (fun e -> not (isNull e?n)) |> Array.toList
        let rendersWithChanges =
            renders
            |> List.fold (fun (acc, prev) r ->
                let after = r?after
                let lines = if isNull after then [] else changes prev after
                let prev' = if isNull after then prev else Some after
                ((r, lines) :: acc, prev')) ([], None)
            |> fst |> List.rev
        let renderTime (r: obj) : int = unbox r?t
        let renderNo (r: obj) : int = unbox r?n

        // Each frame carries the renders that landed since the previous distinct frame; a
        // render after the last frame is listed on its own.
        let attributedRev, trailing =
            distinct
            |> List.fold (fun (done_, pending: (obj * string list) list) f ->
                let mine, later = pending |> List.partition (fun (r, _) -> renderTime r <= f.T)
                ({ f with Renders = mine } :: done_, later)) ([], rendersWithChanges)
        let attributed = List.rev attributedRev

        // The pixel diff, and the cut: shown when the picture changed by more than the stamp
        // does, or when a render landed in it. The instrument comes out first, or the analyzer
        // and the sheets would wear a stamp of their own.
        let! _ = cdp.Send "Page.removeScriptToEvaluateOnNewDocument" (createObj [ "identifier" ==> installed?identifier ])
        writeFile (joinTwo out "analyze.html") (box analyzer)
        let! _ = cdp.Send "Page.navigate" (createObj [ "url" ==> sprintf "file://%s" (joinTwo out "analyze.html") ])
        do! delay 500
        let urls = attributed |> List.map (fun f -> "data:image/png;base64," + f.Data) |> List.toArray
        let! diffsJson = evaluate cdp (sprintf "__analyze(%s)" (JS.JSON.stringify urls))
        let diffs : obj array = JS.JSON.parse (unbox diffsJson) |> unbox
        let analyzed =
            attributed
            |> List.mapi (fun i f ->
                let d = diffs.[i]
                { f with Changed = unbox d?changed; Box = (match d?box |> unbox<int array option> with | Some b -> Some b | None -> None) })
        let shown = analyzed |> List.filter (fun f -> f.Index = 0 || f.Changed >= minPx || not (List.isEmpty f.Renders))
        for f in shown do writeFile (joinTwo out (frameName f)) (bytesOfBase64 f.Data)

        // The report.
        let renderLines (r: obj, lines: string list) =
            lines |> List.map (fun c -> sprintf "<code>#%d %s</code>" (renderNo r) (escapeHtml c)) |> String.concat ""
        let card (f: Frame) =
            let renderList =
                if List.isEmpty f.Renders then "<i>no render</i>"
                else sprintf "renders %s" (f.Renders |> List.map (fun (r, _) -> sprintf "#%d" (renderNo r)) |> String.concat " ")
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
            |> List.map (fun (r, lines) -> sprintf "<code>#%d t=%d %s</code>" (renderNo r) (renderTime r) (escapeHtml (if List.isEmpty lines then "(no layout change)" else String.concat " ; " lines)))
            |> String.concat ""
        let report =
            String.concat "\n"
                [ sprintf "<!doctype html><meta charset=utf-8><title>first load</title>%s" style
                  sprintf "<h1>%s</h1><p>%d frames painted, %d distinct, %d shown (Δ ≥ %dpx or carrying a render); %d renders</p>" (escapeHtml sessionUrl) (List.length frames) (List.length distinct) (List.length shown) minPx (List.length renders)
                  sprintf "<h2>Navigations and auth</h2><pre>%s</pre>" (requests |> List.map escapeHtml |> String.concat "\n")
                  "<p>The stamp in each frame's corner is written by the page itself: rN = renders landed, tN = ms since that document started. A stamp whose clock runs backwards belongs to an earlier document — a fresh session's shell loads twice (the sign-in bounce).</p>"
                  sprintf "<h2>Frames</h2>%s" (shown |> List.map card |> String.concat "\n")
                  (if List.isEmpty trailing then "" else sprintf "<h2>Renders after the last frame</h2>%s" (trailing |> List.map renderLines |> String.concat ""))
                  sprintf "<h2>All renders</h2>%s" allRenders ]
        writeFile (joinTwo out "report.html") (box report)
        let logOut =
            createObj
                [ "sessionUrl" ==> sessionUrl; "origin" ==> origin; "requests" ==> List.toArray requests
                  "renders" ==> (rendersWithChanges |> List.map (fun (r, lines) -> createObj [ "n" ==> renderNo r; "t" ==> renderTime r; "after" ==> r?after; "changes" ==> List.toArray lines ]) |> List.toArray)
                  "html" ==> (log |> Array.filter (fun e -> not (isNull e?attr)))
                  "panes" ==> (log |> Array.filter (fun e -> not (isNull e?pane)))
                  "rafs" ==> JS.JSON.parse (unbox rafsJson)
                  "frames" ==> (analyzed |> List.map (fun f -> createObj [ "i" ==> f.Index; "t" ==> f.T; "changed" ==> f.Changed; "box" ==> f.Box; "renders" ==> (f.Renders |> List.map (fun (r, _) -> renderNo r) |> List.toArray) ]) |> List.toArray) ]
        writeFile (joinTwo out "log.json") (box (pretty logOut))

        // Contact sheets, by the same Chrome: eight frames a page, at a size the Read tool reads.
        let cols, rows = 4, 2
        let cw, ch = width / 2 + 16, height / 2 + 170
        let! _ = cdp.Send "Emulation.clearDeviceMetricsOverride" (createObj [])
        let! _ = cdp.Send "Emulation.setDeviceMetricsOverride" (createObj [ "width" ==> cols * cw + 24; "height" ==> rows * ch + 24; "deviceScaleFactor" ==> 1; "mobile" ==> false ])
        let pages = shown |> List.chunkBySize (cols * rows)
        for (p, page) in List.indexed pages do
            let html =
                sprintf "<!doctype html><meta charset=utf-8>%s<style>body{display:grid;grid-template-columns:repeat(%d,%dpx);grid-auto-rows:%dpx}figure{margin:0;overflow:hidden}</style>%s"
                    style cols cw ch (page |> List.map card |> String.concat "\n")
            let sheet = joinTwo out (sprintf "sheet-%d.html" p)
            writeFile sheet (box html)
            let! _ = cdp.Send "Page.navigate" (createObj [ "url" ==> sprintf "file://%s" sheet ])
            do! delay 800
            let! shot = cdp.Send "Page.captureScreenshot" (createObj [ "format" ==> "png" ])
            writeFile (joinTwo out (sprintf "sheet-%d.png" p)) (bytesOfBase64 (unbox shot?data))

        say (sprintf "frames %d distinct %d shown %d renders %d; %s" (List.length frames) (List.length distinct) (List.length shown) (List.length renders) (joinTwo out "report.html"))
        for r in requests do say r
        for (r, lines) in rendersWithChanges do
            if not (List.isEmpty lines) then say (sprintf "#%d t=%d %s" (renderNo r) (renderTime r) (String.concat " ; " lines))

        cdp.Close ()
        child?kill () |> ignore
        // Stopped, like the probe's: a session made for a camera has nobody coming back to it.
        if created && not (Cli.isSet keepOption args) then
            let id = System.Text.RegularExpressions.Regex.Match(openUrl, "/sessions/([A-Z0-9]+)").Groups.[1].Value
            let! stopped = fetch (sprintf "%s/sessions/%s/stop" manager id) (createObj [ "method" ==> "POST" ])
            say (sprintf "stop %s %d" id (unbox<int> stopped?status))
        exitWith 0
    }

run () |> Promise.start
