module Yession.Tests.Browser

// The real-browser E2E, as a Pyxpecto suite (the F# replacement for scripts/browser-e2e.fsx).
// Pyxpecto is multi-runtime: this file compiles for BOTH targets, but the browser flow only
// exists on the .NET CLR, where the Microsoft.Playwright driver lives. Under Fable (JS on
// Node) there is no Playwright, so the flow is `#if`-compiled out and a single visible case
// records where it moved. Run it with:
//
//     dotnet run --project tests/Yession.Tests/Yession.Tests.fsproj
//
// It launches two Chromium peers against a real Session Process (app/out/Main.js), verifies
// Markdown typed into the rich composer renders as formatted rich text (input rules), that the
// SECOND peer's composer joins that draft rather than opening a rival, that it converges over
// native WebRTC with live carets, that the second peer can co-edit AND send it — whose durable
// body is Markdown — rendering as that same formatted rich text in both timelines; then proves
// client-side IndexedDB persistence by wiping the server and reloading
// (the draft can only return from the browser), and that the doc store is session-keyed.
// Event-driven throughout (WaitForFunctionAsync); Playwright's own per-action timeouts watch.

open Fable.Pyxpecto

#if !FABLE_COMPILER

open System
open System.IO
open System.Net
open System.Net.Http
open System.Net.Sockets
open System.Diagnostics
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.Playwright

/// The URL out of a "launched at http://127.0.0.1:PORT/ …" line. Same shape the packaged
/// composition test uses to learn both endpoints from stdout.
let private urlIn (line: string) =
    let m = System.Text.RegularExpressions.Regex.Match (line, "http://[0-9.:]+/")
    if m.Success then Some m.Value else None

// --- Chromium discovery -----------------------------------------------------------------
//
// The browser comes from `PLAYWRIGHT_BROWSERS_PATH` — nixpkgs' playwright-driver, pinned by
// the same lock as the toolchain (devenv.nix) — and its layout is Playwright's, which differs
// per platform and has changed name across builds:
//
//   x86_64-linux    chrome-linux64/chrome
//   aarch64-linux   chrome-linux/chrome
//   aarch64-darwin  chrome-mac-arm64/Google Chrome for Testing.app/Contents/MacOS/…
//
// So the executable is found by NAME, from a known set, under the `chromium-<revision>`
// directory. Nothing here may hardcode a revision: it moves with every Playwright bump, and a
// stale one would fail as "no Chromium" long after the browser arrived.
//
// There is deliberately no fallback to a system Chrome. The revision is pinned so the browser
// matches the client driving it; silently launching whatever `/usr/bin/google-chrome` happens
// to be would make the suite's meaning depend on the host, which is the opposite of what
// pinning is for. `CHROMIUM_PATH` remains as the explicit override for someone who means it.
let private chromiumExecutableNames =
    set [ "chrome"; "Google Chrome for Testing"; "Chromium" ]

/// `internal` from here down wherever the benchmark suite (`Bench.fs`) shares it: one browser
/// fixture serves both, rather than a second copy of the same launch/serve/listen scaffolding
/// free to drift from this one.
let internal chromiumPath () : string =
    let env name =
        match Environment.GetEnvironmentVariable name with
        | null | "" -> None
        | v -> Some v
    match env "CHROMIUM_PATH" with
    | Some p -> p
    | None ->
        match env "PLAYWRIGHT_BROWSERS_PATH" with
        | None ->
            failwith
                "no Chromium: PLAYWRIGHT_BROWSERS_PATH is unset (devenv.nix sets it; outside \
                 the dev shell, set CHROMIUM_PATH)"
        | Some root ->
            // `chromium-*` and not `chromium*`: `chromium_headless_shell-<rev>` sits beside it
            // and is a different, cut-down browser. The underscore is what separates them.
            let revisions =
                if Directory.Exists root then
                    try Directory.GetDirectories (root, "chromium-*") |> Array.toList |> List.sort
                    with _ -> []
                else []
            // Each revision directory is a symlink into the store, and .NET's recursive
            // enumeration does not descend one — resolve it rather than depend on that.
            let resolve (dir: string) =
                match Directory.ResolveLinkTarget (dir, true) with
                | null -> dir
                | target -> target.FullName
            let executables =
                revisions
                |> List.collect (fun dir ->
                    try
                        Directory.EnumerateFiles (resolve dir, "*", SearchOption.AllDirectories)
                        |> Seq.filter (fun f -> chromiumExecutableNames.Contains (Path.GetFileName f))
                        |> Seq.toList
                    with _ -> [])
            match executables |> List.tryFind File.Exists with
            | Some c -> c
            | None ->
                failwithf
                    "no Chromium under %s (looked in %d chromium-* revision(s) for %s); set CHROMIUM_PATH"
                    root
                    (List.length revisions)
                    (String.Join (", ", chromiumExecutableNames))

// Task -> Async adapters (this whole file is CLR-only, so Async.AwaitTask is available).
let internal await (t: Task<'a>) : Async<'a> = Async.AwaitTask t
let internal awaitU (t: Task) : Async<unit> = Async.AwaitTask t

// --- Loopback servers, on ports nobody chose ----------------------------------------------
//
// Nothing in this file names a port, and no case can ask for one. Hand-picked numbers are what
// that replaces, and they had collided: the editor family took `EDITOR_PORT + n` per case with
// five offsets used twice over, and every other constant here sat inside the block those 48
// cases occupied. Two suites that bind one port cannot run at once, and the loser does not fail
// legibly — it dies at `Start` with EADDRINUSE, or waits out a readiness line that never comes
// and reports thirty seconds later as a timeout naming the wait rather than the fault. That is
// this tier's worst failure mode, and it cost four separate runs an hour of diagnosis apiece.
//
// Renumbering would only have restocked the hat the next case picks from. So the rule instead:
// whatever STARTS a server hands back the origin it came up on, and nothing else can name one.
// There is then no number for two cases to share, and adding a case requires knowing nothing
// about what any other case bound.

/// A loopback port nothing is listening on: taken at `:0`, so the OS chooses it, and released.
///
/// Wanted only where a port has to be known BEFORE the thing that binds it has started — a
/// Manager whose own origin is the OIDC issuer a booting session fetches discovery against, a
/// Caddyfile that has to name its upstream. Everything else is told `0` and says where it
/// landed. The race the release leaves is the narrowest one available, and it cannot produce a
/// passing-but-wrong run: a collision fails the bind loudly.
let private freeLoopbackPort () : int =
    let probe = new TcpListener (IPAddress.Loopback, 0)
    probe.Start ()
    let port = (probe.LocalEndpoint :?> IPEndPoint).Port
    probe.Stop ()
    port

/// A server this file started, and the origin it answers on — `http://127.0.0.1:<port>`, with
/// no trailing slash. The two travel together because a caller holding one without the other is
/// a caller that picked a port.
type internal Serving =
    { Listener : HttpListener
      Origin : string }
    /// This server's address for an absolute path (`"/"`, `"/s/mounted/"`).
    member this.At (path: string) : string = this.Origin + path
    member this.Stop () : unit = this.Listener.Stop ()

/// Start an `HttpListener` on a loopback origin of its own.
///
/// There is no `:0` to hand it — a prefix names a port — so one is taken and released, and the
/// window that leaves is closed by trying again rather than by anybody picking a number.
let internal listenOnLoopback () : Serving =
    let rec attempt (triesLeft: int) =
        let origin = sprintf "http://127.0.0.1:%d" (freeLoopbackPort ())
        let listener = new HttpListener ()
        listener.Prefixes.Add (origin + "/")
        try
            listener.Start ()
            { Listener = listener; Origin = origin }
        with :? HttpListenerException when triesLeft > 1 ->
            listener.Close ()
            attempt (triesLeft - 1)
    attempt 5

// --- What the page saw (so a failure can say more than "timed out") ----------------------
//
// A browser case can only fail one way: a wait that never settles. That failure names the WAIT
// and never the reason, so three separate faults in the service worker — a registration
// eliminated as dead code, an opaque redirect from the sign-in bounce, a precache that could
// not have happened — all presented as the same thirty-second timeout, and each cost a full
// run of the gate to tell apart. The page had been saying which was which the whole time.
//
// So: keep what it says, and print it when a case fails. It costs nothing on the green path
// and it is the only way to read a red one in CI, where nothing can be attached afterwards.

type internal Evidence () =
    let lines = ResizeArray<string> ()
    /// Bounded: a page that is failing tends to say the same thing very fast, and a thousand
    /// identical lines is not more evidence than fifty.
    member _.Note (text: string) =
        lock lines (fun () -> if lines.Count < 100 then lines.Add text)
    member _.Lines = lock lines (fun () -> List.ofSeq lines)

/// Listen to everything the page can tell us.
let internal watching (page: IPage) =
    let ev = Evidence ()
    page.Console.Add (fun m ->
        if m.Type = "error" || m.Type = "warning" then ev.Note (sprintf "console %s: %s" m.Type m.Text))
    page.PageError.Add (fun e -> ev.Note ("pageerror: " + e))
    page.RequestFailed.Add (fun r -> ev.Note (sprintf "requestfailed: %s %s" r.Url r.Failure))
    // NOT the service worker's own console: Playwright .NET 1.61 exposes `IWorker.Console`,
    // but only for web workers (`page.Workers`) — there is no `IBrowserContext.ServiceWorkers`
    // to reach a service worker through. A worker that wants to be heard here has to say it
    // somewhere the page can see; `console.debug` in it is for a human with devtools open.
    ev

/// Run a case; if it throws, print what the page saw before letting the failure through.
let internal reporting (label: string) (page: IPage) (ev: Evidence) (body: Async<unit>) : Async<unit> =
    async {
        try
            do! body
        with e ->
            printfn "=== %s failed — what the page saw ===" label
            for line in ev.Lines do printfn "  %s" line
            // The page's own state, best-effort: on a navigation failure there is no page left
            // to ask, and that answer is itself worth printing.
            let! state =
                async {
                    try
                        return!
                            await (page.EvaluateAsync<string>
                                    """async () => JSON.stringify({
                                         url: location.href,
                                         title: document.title,
                                         connection: document.querySelector('[data-connection]')?.getAttribute('data-connection') ?? null,
                                         conversation: document.querySelector('[data-conversation]')?.textContent?.slice(0, 200) ?? null,
                                         degraded: document.querySelector('[data-degraded]')?.getAttribute('data-degraded') ?? null,
                                         // What this client KEPT, by store and entry count. An
                                         // offline page renders out of these, so a store that is
                                         // absent or empty is the difference between "the replay
                                         // is broken" and "there was nothing to replay" — which
                                         // a bare timeout cannot tell you and which cost a full
                                         // run of the gate to tell apart once already.
                                         kept: await (async () => {
                                           try {
                                             const out = {}
                                             for (const n of await caches.keys()) {
                                               const c = await caches.open(n)
                                               const entries = {}
                                               for (const req of await c.keys()) {
                                                 // The ADDRESS keyed to its BODY. The address says
                                                 // which line the entry starts on — a replay with
                                                 // entries but none starting at 0 folds nothing —
                                                 // and the body says WHICH record it is: a store
                                                 // that kept the `"i"` input line but not yet the
                                                 // `"o"` output reads the same by address as one
                                                 // that kept both, and only the bytes tell them
                                                 // apart. Truncated; a recording is small here.
                                                 const addr = req.url.replace(location.origin, '').replace(/\?token=[^&]*/, '')
                                                 const resp = await c.match(req)
                                                 entries[addr] = resp ? (await resp.text()).slice(0, 300) : null
                                               }
                                               out[n] = entries
                                             }
                                             return out
                                           } catch (e) { return 'unreadable: ' + e.message }
                                         })()
                                       })""")
                    with pe -> return "unreadable: " + pe.Message
                }
            printfn "  page: %s" state
            printfn "=== end %s ===" label
            raise e
    }


/// The text an element really carries, with remote-caret widgets subtracted.
///
/// A ProseMirror widget decoration's DOM lives INSIDE the node it is anchored in, so a
/// co-editor's caret label sitting in a heading makes that heading's `textContent` read
/// `"Heading oneada"` rather than `"Heading one"`. An assertion comparing `textContent`
/// exactly is therefore not testing the content — it is testing the content AND whether
/// anybody's caret happens to be parked in it, which is a race nothing in the test controls.
///
/// It cost a full CI run of #210 to learn that, twice, because the failure looks exactly like
/// lost content: the heading never equals the string, the wait never settles, and the page is
/// rendering the words perfectly the whole time.
let private ownTextFn =
    """((el) => el ? [...el.childNodes]
          .filter(n => !(n.nodeType === 1 && n.classList.contains('pm-caret')))
          .map(n => n.textContent).join('') : null)"""

/// The own text of the first match, for a strict comparison.
let private ownText (selector: string) =
    sprintf "%s(document.querySelector('%s'))" ownTextFn selector

/// Does any match carry exactly this text, carets aside? The `.some` form, because a page can
/// hold several editors and the assertion is about one of them saying the words.
let private someOwnText (selector: string) (text: string) =
    sprintf "[...document.querySelectorAll('%s')].some(el => %s(el) === '%s')" selector ownTextFn text

/// A page-function wait that says WHICH wait it was. Playwright reports only "Timeout 30000ms
/// exceeded", and a case with a dozen waits is a case whose red names none of them — telling
/// them apart then costs a full run of the gate per hypothesis, which is how guessing comes to
/// cost the same as looking and wins every time. The label is the whole instrument.
///
/// Poll until the predicate holds, awaiting it whether it is a plain boolean expression or an
/// async one.
///
/// It does NOT use `page.WaitForFunctionAsync`, which is unusable for a predicate that resolves a
/// Promise: that call checks the truthiness of the expression's VALUE and never awaits it, so an
/// `(async () => …)()` (or any `.then`-chained expression) is truthy the instant it is evaluated —
/// the Promise object itself — and the wait settles while the condition it names has never held. A
/// store-kept gate written that way is a silent no-op: `transcriptKept`/`conversationKept` returned
/// at once, `make` killed the session before the record was ever written, and the reload replayed
/// nothing — green on an idle box, red on a loaded runner, which is the whole of this bug. This was
/// verified in place: `WaitForFunctionAsync "(async () => false)()"` settles in ~30ms, and even the
/// function form `() => (async () => false)()` settles at once, while `() => false` correctly times
/// out. `EvaluateAsync` awaits a returned Promise (confirmed: `(async () => false)()` -> false), so
/// polling it is correct for both predicate shapes.
let private waitTimeoutMs = 30000.0
let private waitFor (what: string) (page: IPage) (predicate: string) : Async<unit> =
    async {
        let sw = Stopwatch.StartNew ()
        let mutable held = false
        let mutable lastError = ""
        while not held && sw.Elapsed.TotalMilliseconds < waitTimeoutMs do
            // `EvaluateAsync` awaits a Promise the expression returns, so an async predicate yields
            // its real boolean here. A transient throw (an execution context torn down by a reload
            // mid-poll) is just "not yet" — kept, so the final timeout can name it.
            try
                let! v = await (page.EvaluateAsync<bool> predicate)
                held <- v
            with e -> lastError <- e.Message
            if not held then do! Async.Sleep 100
        if not held then
            let tail = if lastError = "" then "" else sprintf " (last error: %s)" lastError
            failwithf "waiting for %s — timed out after %gms%s\n  predicate: %s"
                what waitTimeoutMs tail predicate
    }

// Browser-evaluated predicate strings: JS by necessity — they run inside Chromium via CDP.

// Read off the ATTRIBUTE, never off the words. A healthy client says nothing about being
// healthy any more — "Connected" was on three surfaces at once and is now on none — so the
// state token is the only place this can come from, which is where a markup contract belongs.
let private connected = """document.querySelector('[data-connection]')?.getAttribute('data-connection') === 'Connected'"""

// The open draft is a ProseMirror editable (`.ProseMirror`) inside the editable
// (`data-rich-readonly="false"`) body-mount host — and it is whichever draft this peer has open,
// which may be someone else's: the composer joins the message already being written. Collapsed
// drafts are read-only one-line summaries.
let private composer = """[data-rich-readonly="false"] .ProseMirror"""

// --- Terminal command lines --------------------------------------------------------------
//
// The composer of the terminal a tab strip is showing: its key names the terminal, so asking
// for one by id also asserts that the pane really moved. `:not([readonly])` picks the line
// this peer may type into rather than a collaborator's mirror of theirs.
let private commandLine (terminal: string) =
    sprintf "[data-terminal-input^='term-draft:%s:']:not([readonly])" terminal

/// What a command line currently says, once it exists.
let private commandLineValue (page: IPage) (selector: string) : Async<string> =
    async {
        let! _ = await (page.WaitForSelectorAsync selector)
        return! await (page.EvaluateAsync<string> ("sel => document.querySelector(sel)?.value ?? ''", selector))
    }

/// Wait for a command line to say something, and when it never does, FAIL SAYING WHAT IT
/// SAYS. The interesting failures here are lines holding the wrong terminal's text or
/// wiping themselves as they are typed into, and a bare timeout hides exactly that.
let private waitCommandLine (page: IPage) (selector: string) (expected: string) : Async<unit> =
    async {
        try
            do!
                await (page.WaitForFunctionAsync (
                        "([sel, want]) => document.querySelector(sel)?.value === want",
                        [| box selector; box expected |]))
                |> Async.Ignore
        with _ ->
            let! actual = commandLineValue page selector
            failwithf "expected the command line %s to say %A, it says %A" selector expected actual
    }

/// Which terminals have a command line ON SCREEN, in the order they are in the document.
///
/// The pane shows one terminal at a time, so this is one entry deep in practice — and that is
/// exactly the fact worth reading when a case cannot find the line it expected: "the pane is
/// showing a different terminal" and "the line is not there yet" are different faults, and a
/// locator timeout says neither.
let private commandLinesOn (page: IPage) : Async<string[]> =
    await (page.EvaluateAsync<string[]> ("""() =>
        [...document.querySelectorAll("[data-terminal-input^='term-draft:']")]
          .map(i => i.getAttribute('data-terminal-input') + (i.readOnly ? ' (readonly)' : ''))"""))

/// Show a terminal in the pane, and wait until its own command line is the one on screen.
///
/// Two facts a case may not assume, both of which cost a red run to find. Clicking a tab that
/// is ALREADY selected PINS it rather than selecting it (`activate` in the view: a selected,
/// pinnable tab toggles its pin) — so a case that clicks blind can silently pin a terminal and
/// then wait out its timeout on a pane that never moved. And the pane follows the terminal most
/// recently OPENED, which arrives on an event: "the terminal I just asked for is the one
/// showing" is not a state to assume, it is one to wait for.
let private showTerminal (page: IPage) (terminal: string) : Async<unit> =
    async {
        let! selected =
            await (page.EvaluateAsync<bool> (
                    "id => document.querySelector(`[data-terminal-tab='${id}']`)?.getAttribute('aria-selected') === 'true'",
                    box terminal))
        if not selected then do! awaitU (page.ClickAsync (sprintf "[data-terminal-tab='%s']" terminal))
        try
            do! await (page.WaitForSelectorAsync (commandLine terminal)) |> Async.Ignore
        with _ ->
            let! lines = commandLinesOn page
            failwithf
                "expected terminal %s to be showing its own command line, the pane offers: %s"
                terminal
                (String.Join (", ", lines))
    }

/// The terminals the tab strip is offering, in the order it offers them.
let private terminalTabs (page: IPage) : Async<string[]> =
    await (page.EvaluateAsync<string[]> (
            """() => [...document.querySelectorAll('[data-terminal-tab]')].map(t => t.getAttribute('data-terminal-tab'))"""))

// --- One case, one world -----------------------------------------------------------------
//
// Every case below arranges what it asserts on and takes it away again: its own data dir, its
// own Manager + session on ports the OS picks, its own browser, and a fresh peer per page.
//
// It used to be one host and one pair of pages threaded through module-level mutables, set up
// by the FIRST case and torn down by a last one that asserted nothing. Three things came with
// that. `--only` could not name a case here: narrowing to one filtered out the case that
// assigned the pages, so the run died on a null reference that named nothing about why.
// Order was load-bearing and unstated — the two-terminals case had to ask
// whether something before it had left the terminal column open. And an arrangement leaked —
// a credential connected in one case was still connected in the next, so that case had to
// disconnect it by hand on behalf of the ones after it.
//
// Independence is also what running these in parallel needs, which the runner cannot do yet:
// Pyxpecto executes its flat tests in a `for` loop and never reads the `sequenced` field its
// own model carries. When it can, nothing here has to change.

/// A Manager + session of one case's own.
type private Host =
    { /// The session's URL, off the readiness line.
      Base : string
      Process : Process
      DataDir : string }

let private hostsStarted = ref 0

/// Boot the real product entry on ports the OS picks, in a data dir nothing else touches.
///
/// `--port 0` is what makes a case's host its own: the shipped default is a fixed 8321, so
/// two hosts at once — and, on a runner, two hosts in a row inside the same TIME_WAIT — would
/// be fighting over one port. The session's own address comes off the readiness line, which is
/// the only place it is stated.
let private startHost () : Host =
    let ordinal = System.Threading.Interlocked.Increment hostsStarted
    let dataDir = sprintf "tests/browser/.data/host-%d-%d" (Process.GetCurrentProcess().Id) ordinal
    if Directory.Exists dataDir then Directory.Delete (dataDir, true)
    let psi = ProcessStartInfo "node"
    psi.ArgumentList.Add "app/out/Main.js"
    // Single-machine loopback trust (the shipped default `none` denies everything and
    // the login bounce would 401 before any page ever connects).
    psi.ArgumentList.Add "--auth"
    psi.ArgumentList.Add "localhost"
    psi.ArgumentList.Add "--data-dir"
    psi.ArgumentList.Add dataDir
    psi.ArgumentList.Add "--port"
    psi.ArgumentList.Add "0"
    psi.UseShellExecute <- false
    psi.RedirectStandardOutput <- true   // stderr inherits → visible in the log
    // No ambient credential for any session this suite boots. One case is about what a
    // session with NOTHING connected offers, and `SessionMain`'s documented last resort is
    // the environment — so on a box that has a key, the picker is filled from it and the
    // refusal that case begins from never appears. The release gate is exactly such a box
    // (`verify` carries the LiveAgent secret), which is how a case that let the environment
    // decide passed on every laptop and failed there.
    //
    // Empty rather than removed, which is what `ambientCredential` reads as absent and what
    // the Node suites plant for the same reason. It reaches the session because the Manager
    // spawns one with `{...process.env, ...}` (`Spawn.fs`).
    psi.EnvironmentVariables.["ANTHROPIC_API_KEY"] <- ""
    psi.EnvironmentVariables.["CLAUDE_CODE_OAUTH_TOKEN"] <- ""
    let p = new Process (StartInfo = psi)
    let ready = TaskCompletionSource<string> ()
    // Keep draining stdout (like the JS 'data' handler) so the pipe never blocks the host;
    // resolve readiness on the "launched at" line.
    p.OutputDataReceived.Add (fun e ->
        if e.Data <> null && e.Data.Contains "launched at" then
            match urlIn e.Data with
            | Some url -> ready.TrySetResult url |> ignore
            | None -> ())
    p.Start () |> ignore
    p.BeginOutputReadLine ()
    if not (ready.Task.Wait 30000) then
        try p.Kill true with _ -> ()
        failwith "host never reported readiness"
    { Base = ready.Task.Result; Process = p; DataDir = dataDir }

let private stopHost (host: Host) : unit =
    try host.Process.Kill true with _ -> ()
    if Directory.Exists host.DataDir then
        try Directory.Delete (host.DataDir, true) with _ -> ()

/// Print what every page saw, then let the failure through — `reporting` for as many pages as
/// the case asked for, so a two-peer case says what BOTH of them said.
let private reportingAll (name: string) (pages: (IPage * Evidence) list) (body: Async<unit>) : Async<unit> =
    pages
    |> List.mapi (fun i (page, ev) -> (fun inner -> reporting (sprintf "%s [peer %d]" name (i + 1)) page ev inner))
    |> List.fold (fun inner wrap -> wrap inner) body

/// A case and the world it runs in: a host, a browser, and `peers` first visits that have
/// settled into Connected. Everything is gone when the case ends, however it ends.
///
/// One CONTEXT per peer, never two pages in one: a peer id lives in origin-partitioned
/// localStorage, so two pages in one context are one person in two tabs rather than the two
/// collaborators a convergence case is about.
let private peersCase (name: string) (peers: int) (body: Host -> IPage list -> Async<unit>) =
    testCaseAsync name <|
        async {
            let host = startHost ()
            let! pw = await (Playwright.CreateAsync ())
            let! br =
                await (pw.Chromium.LaunchAsync (
                    BrowserTypeLaunchOptions (
                        ExecutablePath = chromiumPath (),
                        // Headless sandboxes stall ICE gathering when host candidates hide behind mDNS.
                        Args = [| "--disable-features=WebRtcHideLocalIpsWithMdns" |])))
            let! opened =
                [ 1 .. peers ]
                |> List.map (fun _ ->
                    async {
                        let! ctx = await (br.NewContextAsync ())
                        let! page = await (ctx.NewPageAsync ())
                        page.SetDefaultTimeout 30000.0f
                        return page, watching page
                    })
                |> Async.Sequential
            let opened = List.ofArray opened
            let pages = opened |> List.map fst
            let arranged =
                async {
                    // A first visit, through the login bounce, to a shell that has connected.
                    // Nothing may be evaluated before that: the bounce destroys the execution
                    // context, and `connected` is only true back on the shell.
                    for page in pages do
                        let! _ = await (page.GotoAsync host.Base)
                        ()
                    for i, page in List.indexed pages do
                        do! waitFor (sprintf "peer %d to connect" (i + 1)) page connected
                    do! body host pages
                }
            let! outcome = Async.Catch (reportingAll name opened arranged)
            // Teardown that cannot strand a host: a browser refusing to close must not stop
            // the process being killed or its data dir going. A leaked Chromium costs memory;
            // a leaked host holds a port and a session nobody will ever look at again.
            try do! awaitU (br.CloseAsync ()) with _ -> ()
            try pw.Dispose () with _ -> ()
            stopHost host
            match outcome with
            | Choice1Of2 () -> ()
            | Choice2Of2 e -> raise e
        }

/// A case with one peer in it.
let private sessionCase (name: string) (body: IPage -> Async<unit>) =
    peersCase name 1 (fun _ pages -> body pages.Head)

/// A case with one peer that also reads the SESSION's own files. Everything above asserts on
/// what a browser can see, which is the right default; this is for the one thing a browser
/// cannot answer — how much transcript there actually is — where the alternative is to assume
/// it, and assuming it is what made the reopen budget below mean different things on different
/// machines.
let private hostSessionCase (name: string) (body: Host -> IPage -> Async<unit>) =
    peersCase name 1 (fun host pages -> body host pages.Head)

/// A case with two, which is what convergence and presence are about.
let private sessionPair (name: string) (body: IPage -> IPage -> Async<unit>) =
    peersCase name 2 (fun _ pages ->
        match pages with
        | [ a; b ] -> body a b
        | other -> failwithf "expected two peers, got %d" other.Length)

let tests =
    testList "Browser E2E" [
        sessionPair "markdown typed in the rich composer renders formatted, converges, and sends as markdown" <|
            fun pageA pageB ->
            async {
                // A types Markdown into its rich composer with REAL key events, so the input
                // rules fire: "# " turns the block into a heading rendered live as an <h1> —
                // the syntax itself is never left as literal text (Linear-style WYSIWYG).
                let! _ = await (pageA.WaitForSelectorAsync composer)
                do! awaitU (pageA.ClickAsync composer)
                do! awaitU (pageA.Keyboard.TypeAsync "# Heading one")
                let renderedHeading =
                    sprintf "%s === 'Heading one'" (ownText "[data-rich-readonly=\"false\"] .ProseMirror h1")
                do! waitFor "A's own typing to render as a heading" pageA renderedHeading

                // B converges: it renders A's draft as the same formatted heading.
                // (Regression guard: pushing presence decorations on every render used to starve
                // y-prosemirror's rendering of REMOTE content here, so B's mirror stayed blank.)
                do! waitFor
                        "B's mirror to render A's remote content"
                        pageB
                        (someOwnText ".ProseMirror h1" "Heading one")

                // And B JOINED it rather than opening a rival blank: the composer B is in is A's
                // draft, which is why the "new message" way out is offered at all.
                do! waitFor "B to be offered a way out of A's draft" pageB """!!document.querySelector('[data-draft-new]')"""

                // B overlays A's live caret in it: A's presence (a base64 relative position over
                // the draft body) decodes to a caret widget + name label. This lands just after
                // the content settles (the decoration push is debounced off the active-convergence
                // window). Guards remote BODY cursors end-to-end.
                do! waitFor "B to overlay A's caret" pageB """!!document.querySelector('.pm-caret')"""

                // B CO-EDITS A's draft — the collaboration the read-only mirror used to forbid —
                // and A sees the words appear in the draft it started.
                do! awaitU (pageB.ClickAsync composer)
                do! awaitU (pageB.Keyboard.PressAsync "End")
                do! awaitU (pageB.Keyboard.TypeAsync " and two")
                let coEdited = someOwnText ".ProseMirror h1" "Heading one and two"
                do! waitFor "A to see B's co-edit" pageA coEdited

                // B sends A's draft: any co-editor may. Both timelines show the immutable message.
                // The durable body is MARKDOWN (`# Heading one and two`, from events not Yjs), but
                // the timeline RENDERS it as formatted rich text — the same heading the composer
                // showed — so the sent view mirrors the input: an <h1>, no literal `#`.
                do! awaitU (pageB.ClickAsync "[data-send-draft]")
                let inTimeline = """[...document.querySelectorAll('[data-conversation] [data-message-body] h1')].some(h => h.textContent.trim() === 'Heading one and two')"""
                do! waitFor "A's timeline to show the sent message" pageA inTimeline
                do! waitFor "B's timeline to show the sent message" pageB inTimeline
            }

        // Terminals (Plan 13) in a real browser: the one part of the panel that only a
        // browser can exercise — the `<input>` bound to a `Y.Text` root. Everything under it
        // (the slot rule, the queue, the approval gate, the drain, the transcript) is covered
        // in the cheap tier; what is under test here is the binding itself, and that the
        // command really runs in the session's sandbox.
        //
        // `Srt` because of that last clause, and it is not a formality: the product's default
        // work sandbox IS srt (`SessionMain.fs`, `YESSION_SESSION_WORK_BACKEND`), so on a box that
        // cannot host one the command never runs, the block never reaches `ok`, and this waits
        // out its timeout — thirty seconds to report, in effect, "this machine is not a
        // machine this test can run on", which is precisely what a capability is for. It cost
        // an agent a stash-and-re-run to discover that once.
        Tag.needs "a command that really runs" [ Tag.Browser; Tag.Native; Tag.Srt ] (fun () ->
        sessionPair "a command typed in the terminal composer converges, runs in the sandbox, and both peers see the block" <|
            fun pageA pageB ->
            async {
                // The column starts shut, so the header control is the way back in — and
                // that this can find it is the test that one exists at all.
                do! awaitU (pageA.Locator("[data-terminal-toggle='show']").First.ClickAsync ())
                // `.First`: a session with no terminal open offers "new" twice — in the tab
                // strip and in the empty state — and either will do.
                do! awaitU (pageA.Locator("[data-terminal-new]").First.ClickAsync ())

                // Opening is a command; the terminal reaches BOTH peers as an event, so B
                // learns about it without having asked for anything.
                let hasTab = """!!document.querySelector('[data-terminal-tab]')"""
                let! _ = await (pageA.WaitForFunctionAsync hasTab)
                let! _ = await (pageB.WaitForFunctionAsync hasTab)

                // A types a command with REAL key events, so the input's binding is what
                // writes the CRDT — one minimal edit per keystroke, not a wholesale replace.
                let composerInput = "[data-terminal-input^='term-draft:']:not([readonly])"
                let! _ = await (pageA.WaitForSelectorAsync composerInput)
                do! awaitU (pageA.ClickAsync composerInput)
                do! awaitU (pageA.Keyboard.TypeAsync "echo hello-terminal")

                // B sees A writing it — the terminal's version of watching a draft, and the
                // proof that the binding pushes a remote edit back into the input's value.
                let mirrored =
                    """[...document.querySelectorAll('[data-terminal-input]')].some(i => i.value === 'echo hello-terminal')"""
                let! _ = await (pageB.WaitForFunctionAsync mirrored)

                // Sending runs it: a human's command needs no approval under the default
                // mode, so the drain takes it straight away and the sandbox really runs it.
                do! awaitU (pageA.ClickAsync "[data-terminal-send]")
                let blockRan =
                    """[...document.querySelectorAll('[data-terminal-block]')]
                         .some(b => b.textContent.includes('echo hello-terminal')
                                 && b.getAttribute('data-terminal-block-status') === 'ok')"""
                let! _ = await (pageA.WaitForFunctionAsync blockRan)
                let! _ = await (pageB.WaitForFunctionAsync blockRan)

                // And its OUTPUT arrived — over the terminal frames on A, and (for B, whose
                // panel was never opened) through the same fold either way.
                let hasOutput =
                    """[...document.querySelectorAll('[data-terminal-output]')].some(o => o.textContent.includes('hello-terminal'))"""
                let! _ = await (pageA.WaitForFunctionAsync hasOutput)
                do! await (pageB.WaitForFunctionAsync hasOutput) |> Async.Ignore

                // The composer emptied on send, so the next command starts from a clean line.
                do!
                    await (pageA.WaitForFunctionAsync
                            "document.querySelector(\"[data-terminal-input^='term-draft:']:not([readonly])\")?.value === ''")
                    |> Async.Ignore
            })

        // --- Reopening a session, and what it costs -------------------------------------
        //
        // Reopening a tab on a session with a long terminal behind it is the slowest thing
        // this product does, and nothing here could see it. Measured on a real deployment
        // against a session holding 230 events and 1,469 terminal records: a cold open spent
        // 10,087 full re-renders and ~22 seconds with the main thread never yielding — on an
        // M-series laptop. A phone is several times slower again, which is where it was
        // reported from: inputs dead, and the top of the conversation never painting because
        // no frame ever completed.
        //
        // The cause is one render per replayed record. `Client.fs`'s transcript replay
        // dispatches a message per record, Elmish calls `setState` per message, and
        // `setState` re-renders the whole view and reads layout back twice. So the cost is
        // records × the whole page, and the number that says so is a COUNT of renders.
        //
        // WHAT THIS PINS, and what it deliberately does not. It pins the MARGINAL cost —
        // how many extra renders each extra transcript record adds to a reopen — and not a
        // duration, because a millisecond budget on a shared runner is the flaky test this
        // repository warns about while a count is the same number on every box. Marginal
        // rather than total, because a cold open costs a fixed number of renders whatever the
        // transcript holds, and that constant is not what got anybody's phone stuck: the
        // slope is. Two transcripts, one session, and the difference between them is the
        // measurement.
        //
        // The budget is set well above what the code does today (see `perRecord`), so this is
        // a guard against getting worse rather than a description of good — the storm above is
        // still there. Tightening this constant is how the fix that removes it gets proved.
        //
        // `Srt` because the seed is a command that really runs: on a box that cannot host a
        // sandbox it never would, and this would wait out its timeout rather than skip.
        Tag.needs "what reopening a session costs" [ Tag.Browser; Tag.Native; Tag.Srt ] (fun () ->
        hostSessionCase "reopening a session renders a bounded number of times per transcript record" <|
            fun host page ->
            async {
                // How much transcript there IS, counted off the session's own recordings. A
                // record is one line of a `.cast`, and one line is one write the pty handed
                // over — which is emphatically NOT one echo. This started out assuming it was,
                // dividing renders by the number of lines the seed asked for, and the two
                // numbers disagreed by a factor of four: 2.02 renders per line on a laptop
                // where the pty coalesced several echoes into each read, 7.60 on a CI runner
                // where it did not. Same code, same seed, different machine, and a budget in
                // between would have failed for whoever ran it on the wrong one.
                //
                // So the denominator is measured rather than assumed, and measured from the
                // artifact rather than from the client: this counts what the SESSION wrote,
                // which is the thing the reopen has to fold, and it cannot be moved by a
                // change to how the browser is instrumented.
                let records () =
                    Directory.GetFiles (host.DataDir, "*.cast", SearchOption.AllDirectories)
                    |> Array.sumBy (fun f -> File.ReadAllLines(f).Length)
                let composerInput = "[data-terminal-input^='term-draft:']:not([readonly])"
                let printed (n: int) =
                    sprintf
                        "[...document.querySelectorAll('[data-terminal-output]')].some(o => o.textContent.includes('line-%d'))"
                        n

                // Output in MANY small writes, because a record is a write and not a line:
                // `seq 1 300` arrives in a handful of chunks and would seed a handful of
                // records, which is a transcript this cost cannot be measured on. The pause is
                // what separates them — how WELL it separates them is the machine's business,
                // which is why `records` counts the result rather than trusting it — and it
                // arranges the fixture rather than timing an assertion: everything below waits
                // on content.
                // Focused and sent WITHOUT the pointer. A reload leaves the terminal column
                // laid out with its composer out of the viewport and the chat composer over
                // the top of it, so a real click retries until it times out — and reports that
                // as "element is outside of the viewport", which is a fact about this layout
                // and nothing about what is being measured here. Whether that composer is
                // clickable is a promise, and it is the two cases above that make it; this one
                // is about what a reopen costs, so it takes the shortest honest route to a
                // command having run. The KEYSTROKES stay real — the input's binding is what
                // writes the CRDT, and typing is the only thing that exercises it.
                let seed (first: int) (last: int) =
                    async {
                        let! _ = await (page.WaitForSelectorAsync composerInput)
                        do! awaitU (page.EvalOnSelectorAsync (composerInput, "el => el.focus()"))
                        do!
                            awaitU (page.Keyboard.TypeAsync (
                                sprintf "for i in $(seq %d %d); do echo line-$i; sleep 0.01; done" first last))
                        do! awaitU (page.Locator("[data-terminal-send]").First.DispatchEventAsync "click")
                        // Seeded when the LAST line is on screen: the command finishing says it
                        // ran, this says the transcript holds what a reopen has to replay.
                        do! waitFor (sprintf "the terminal to print line-%d" last) page (printed last)
                    }

                // Reopening. A reload keeps this origin's storage, so the client replays the
                // transcript out of its OWN store — the path a person reopening a closed tab
                // takes, and the expensive one. A fresh context would fetch it back over HTTP
                // and fold it somewhere else.
                //
                // Settled when the render count has stopped moving. Reported rather than waited
                // on: a page still rendering after this long has not failed to settle in some
                // incidental way, it IS the regression, and it should say so in a number rather
                // than in a timeout that names nothing.
                let reopen (upTo: int) =
                    async {
                        let! _ = await (page.ReloadAsync ())
                        do! waitFor "the reopened session to connect" page connected
                        do! waitFor "the reopened session to have replayed its terminal" page (printed upTo)
                        let! settled =
                            await (page.EvaluateAsync<string> """() => new Promise(resolve => {
                              let last = -1, still = 0, waited = 0
                              const tick = () => {
                                const n = globalThis.__yessionRenders ?? -1
                                if (n === last) still++ ; else { still = 0; last = n }
                                waited += 250
                                if (still >= 6 || waited >= 30000) resolve(n + ',' + (still >= 6))
                                else setTimeout(tick, 250)
                              }
                              tick()
                            })""")
                        match settled.Split ',' with
                        | [| n; s |] ->
                            let renders = int n
                            // Anti-vacuity, both ways this passes a budget while measuring
                            // nothing: a counter that was never incremented (the app stopped
                            // publishing one, so this reads -1 or 0), and a page still
                            // rendering when time ran out. The third — a transcript too short
                            // to cost anything — is the wait above, which does not settle
                            // until the last seeded line is back on screen.
                            if renders <= 0 then
                                failwithf
                                    "the page reports %d renders — `Browser.fs` publishes \
                                     `globalThis.__yessionRenders` and this budget means nothing without it"
                                    renders
                            if s <> "true" then
                                failwithf
                                    "the reopened session was still rendering after 30s (%d renders so far, %d \
                                     seeded lines) — the replay storm this budget exists for, not a slow box"
                                    renders upTo
                            return renders
                        | _ -> return failwithf "the render counter answered '%s', which is not a count" settled
                    }

                do! awaitU (page.Locator("[data-terminal-toggle='show']").First.ClickAsync ())
                do! awaitU (page.Locator("[data-terminal-new]").First.ClickAsync ())
                let! _ = await (page.WaitForFunctionAsync "!!document.querySelector('[data-terminal-tab]')")

                let small = 100
                let large = 400
                do! seed 1 small
                let! rendersSmall = reopen small
                let recordsSmall = records ()
                do! seed (small + 1) large
                let! rendersLarge = reopen large
                let recordsLarge = records ()

                let added = recordsLarge - recordsSmall
                // A seed that added no records measures nothing, and would divide by zero
                // saying so. It means the echoes all landed in writes this session had already
                // made, which no pty does — so it is a broken fixture, not a fast one.
                if added <= 0 then
                    failwithf
                        "seeding %d more lines added %d transcript records (%d then %d) — there is nothing here to                          measure the cost of"
                        (large - small) added recordsSmall recordsLarge
                let perRecord = float (rendersLarge - rendersSmall) / float added
                printfn
                    "  reopen: %d renders over %d records, then %d over %d — %.2f renders per added record"
                    rendersSmall recordsSmall rendersLarge recordsLarge perRecord

                // The budget, and why it is loose rather than tight.
                //
                // Both ends are counted now — renders the page reports, records the session
                // wrote — and a count has no jitter, so this wanted to be 50% over the measured
                // value. It cannot be, yet, because the measured value is not one number: a
                // reopen replays whatever this device has KEPT and refetches the rest, the live
                // leg writes to the store asynchronously, and how that splits depends on how
                // fast the machine got there. Same code and same seed: 2.01 renders per record
                // on a laptop, 5.64 on a CI runner — and the DENOMINATORS agreed there (103
                // then ~405 records on both), so the spread is renders and nothing else.
                //
                // So the line sits above the worst seen, and this catches a gross regression
                // rather than a subtle one. That is worth having and it is not what was wanted:
                // making the reopen deterministic — waiting until the transcript is in the
                // store, so the fold under measurement is the replay and only the replay — is
                // what lets this come down to about 3.0, and it is its own piece of work
                // (the wait for it never settled inside 30s on the runner, which is a fact
                // about the store's write path and not about this budget).
                //
                // The replay now folds a terminal's whole kept run as ONE message
                // (`TerminalPageMsg`), and a fetched chunk likewise, so a record adds no render
                // of its own: this case measures 0.01 per record (30 renders for a reopen over
                // 404 records, against 431 before). The line sits at half a render per record
                // — fifty times what the fix spends, and under every value the storm ever
                // read: 2.01 on a laptop, 5.64 on the runner, and 1.01 with the fetch batched
                // but the replay still folding per kept answer (a terminal watched live keeps
                // one answer per record). Measured before the fix on the home deployment, with
                // a session's real 2,138 lines kept: 2,176 renders and 9.9s of main-thread long
                // tasks on an M-series laptop, the reopen offer painting at 11.2s — which on a
                // phone is the minute or two of dead taps reported as "the PWA does not reopen".
                let budget = 0.5
                if perRecord > budget then
                    failwithf
                        "reopening this session cost %.2f renders per added transcript record (%d renders over %d \
                         records, then %d over %d); the budget is %.1f. Reopening got more expensive per record — \
                         see the replay in `Client.fs` and `setState` in `app/browser/Browser.fs`."
                        perRecord rendersSmall recordsSmall rendersLarge recordsLarge budget
            })

        // A command line belongs to ONE terminal. The domain says so — a draft is keyed by
        // terminal AND author precisely so a person can be mid-command in two at once
        // (`BodyKey.terminalDraft`) — and the browser is the only place that promise can
        // break: the pane shows one terminal at a time, so switching tabs hands the SAME
        // `<input>` to a different terminal, and what it writes to has to be the terminal it
        // is showing rather than the one it was first rendered for.
        //
        // The report this comes from: with two terminals open, the older one could not be
        // typed into, and switching away and back left only the last character — a line
        // writing into its neighbour, wiped by every render that pushed its own text back in.
        //
        // `Srt` because opening a terminal starts a shell in the session's work sandbox: on a
        // box that cannot host one, no terminal ever appears and this waits out its timeout
        // instead of failing.
        Tag.needs "two terminals at once" [ Tag.Browser; Tag.Native; Tag.Srt ] (fun () ->
        sessionCase "each terminal's composer types into that terminal's command line" <|
            fun page ->
            async {
                // The column starts shut in a session of this case's own, so the reopen control
                // is there and there are no terminals yet. (It used to ask whether something
                // before it had left the column open, which is a question a case that arranges
                // its own session does not have.)
                do! awaitU (page.Locator("[data-terminal-toggle='show']").First.ClickAsync ())
                do! awaitU (page.Locator("[data-terminal-new]").First.ClickAsync ())
                do! awaitU (page.Locator("[data-terminal-new]").First.ClickAsync ())
                do!
                    await (page.WaitForFunctionAsync
                            "document.querySelectorAll('[data-terminal-tab]').length >= 2")
                    |> Async.Ignore
                let! tabs = terminalTabs page
                Expect.isTrue (tabs.Length >= 2) (sprintf "expected two terminals, the strip offers: %s" (String.Join (", ", tabs)))
                let one, two = tabs.[0], tabs.[1]
                // Settle first: opening is an event, and the pane follows the newest terminal
                // when it lands. Until that has happened there is no telling which terminal a
                // click is acting on.
                do! showTerminal page two

                // One command, half-written, in the first terminal.
                do! showTerminal page one
                do! awaitU (page.ClickAsync (commandLine one))
                do! awaitU (page.Keyboard.TypeAsync "echo one")
                do! waitCommandLine page (commandLine one) "echo one"

                // The second terminal is a second command line, not the first one's: it opens
                // empty, and typing into it stays in it.
                do! showTerminal page two
                let! fresh = commandLineValue page (commandLine two)
                Expect.equal fresh "" "a terminal opens with its own empty command line"
                do! awaitU (page.ClickAsync (commandLine two))
                do! awaitU (page.Keyboard.TypeAsync "echo two")
                do! waitCommandLine page (commandLine two) "echo two"

                // And the half-written command is still where it was written. Both directions,
                // because a line that writes into its neighbour breaks whichever of the two
                // the input was bound to first.
                do! showTerminal page one
                let! kept = commandLineValue page (commandLine one)
                Expect.equal kept "echo one" "the first terminal kept the command written in it"
                do! showTerminal page two
                let! keptToo = commandLineValue page (commandLine two)
                Expect.equal keptToo "echo two" "and the second kept its own"
            })

        // Plan 11. THE discriminating check for the manager origin: this fixture sets no
        // YESSION_MANAGER_URL, so `PublicAccess.managerUrl` alone answers None here and an
        // implementation that used it would emit no tag and silently drop the client's
        // offer to reopen a stopped session on every single-machine deployment. Only the
        // fallback to the Manager's own endpoint makes this pass — and it has to be a real
        // origin, so the test fetches it.
        sessionCase "the shell carries a manager origin that actually answers" <|
            fun pageA ->
            async {
                let! origin =
                    await (pageA.EvaluateAsync<string> ("""() => document.querySelector('meta[name="yession-manager"]')?.getAttribute('content')"""))
                Expect.isFalse (String.IsNullOrEmpty origin) "the bootstrap page must embed the Manager's origin"
                Expect.isTrue (origin.StartsWith "http") (sprintf "expected an origin, got: %s" origin)
                // The client appends `/sessions/{id}/open` to this, so it must be an origin
                // root with no trailing slash — otherwise the URL it builds has a double one.
                Expect.isFalse (origin.EndsWith "/") "no trailing slash: the client concatenates a path onto it"
                let! sessionId =
                    await (pageA.EvaluateAsync<string> ("""() => document.querySelector('meta[name="yession-session"]')?.getAttribute('content')"""))
                // And it is the Manager, not something else that happens to answer: its
                // management page lists this very session.
                let! page = await (pageA.Context.APIRequest.GetAsync origin)
                Expect.equal page.Status 200 "the embedded origin serves the management UI"
                let! body = await (page.TextAsync ())
                Expect.isTrue (body.Contains sessionId) "and it knows the session whose shell pointed here"
            }

        // The generated read surface (Plan 15) renders into a 280px lane — the settings face
        // of a fixed column on desktop, that same column as a drawer on a phone. Whether an
        // answer FITS that lane is a question no cheap test can ask: the markup carries every
        // `data-query-cell` either way, and the tier that reads it cannot see that six of the
        // nine sat past a horizontal scroll with a scrollbar over the last row. Which is what
        // they did, for as long as a row was a table row.
        //
        // The long value is ARRANGED, not waited for. A live session's sandbox says `srt` and
        // `not started`, and nothing that short overflows anything — the first version of this
        // case asserted on whatever the session happened to answer, and stayed green with the
        // old non-wrapping cell put back. What has to hold is that a value LONGER than the
        // lane stays inside it, so the test writes one and then measures the real layout.
        //
        // Measured against each PANEL's own box rather than the pane's, so the settings face's
        // slide-in (`Style.settingsLane1` translates the section 24px) cannot read as an
        // overflow while it is still arriving.
        sessionCase "a value longer than the lane stays inside it" <|
            fun pageA ->
            async {
                do! awaitU (pageA.ClickAsync "[data-settings-toggle='open']")
                // A session always has its default work sandbox, so this panel always has a
                // record to write into — the arrangement cannot silently find nothing.
                let! _ = await (pageA.WaitForSelectorAsync "[data-query-panel='work_sandboxes'] [data-query-row]")
                let! overflowing =
                    await (pageA.EvaluateAsync<string[]> ("""() => {
                        const panel = document.querySelector('[data-query-panel="work_sandboxes"]')
                        const long = 'a value far longer than two hundred and eighty pixels of column'
                        panel.querySelectorAll('[data-query-cell]').forEach(el => { el.textContent = long })
                        const edge = panel.getBoundingClientRect().right
                        return [...panel.querySelectorAll('*')]
                            .filter(el => el.scrollWidth > el.clientWidth + 1
                                       || el.getBoundingClientRect().right > edge + 1)
                            .map(el => `${el.tagName}: ${el.textContent.trim().slice(0, 40)}`)
                    }"""))
                Expect.isEmpty
                    overflowing
                    (sprintf "every part of an answer must stay inside its panel, these do not: %s"
                        (String.Join (" | ", overflowing)))
                do! awaitU (pageA.ClickAsync "[data-settings-toggle='close']")
            }

        // The other end of the same lane, and the case above cannot see it: a value that
        // wraps into a seventeen-pixel column overflows NOTHING, so "it stayed inside the
        // panel" stays green while the answer is unreadable. What starved it was the LABEL
        // — a grid maximizes its intrinsic tracks before it expands a flexible one, so a
        // bare `auto` label track took its whole max-content and left the value what was
        // over. On a phone the `resources` query's longest label did exactly that:
        // `/private/etc/ssl/cert.pem, read-only; …` read one character per line.
        //
        // Both halves are ARRANGED, for the reason the case above arranges its value: a
        // live session's own labels are short enough that nothing here would ever bite.
        //
        // What is pinned is that a value's width does not DEPEND on its label — measured
        // with the labels a query wrote, then again with a label longer than the lane, and
        // the two are the same. It was "a value keeps a third of the lane", and a third was
        // a floor the fault could sit above: capped at 7rem of a 217px pane the label took
        // 112px and left the value 93px, which is 43% of the lane, green, and four lines of
        // ten characters for one socket path. Independence is the promise the share was
        // reaching for, and it holds however the pair is laid out — labels above values, or
        // beside them on a track no label can widen.
        sessionCase "a label never costs its value a pixel" <|
            fun pageA ->
            async {
                do! awaitU (pageA.ClickAsync "[data-settings-toggle='open']")
                let! _ = await (pageA.WaitForSelectorAsync "[data-query-panel='work_sandboxes'] [data-query-row]")
                let! starved =
                    await (pageA.EvaluateAsync<string[]> ("""() => {
                        const panel = document.querySelector('[data-query-panel="work_sandboxes"]')
                        const values = () => [...panel.querySelectorAll('dl [data-query-cell]')]
                        // The same long value in every cell, so what is compared across the
                        // two measurements is the LABEL and nothing else.
                        values().forEach(el => {
                            el.textContent = '/private/etc/ssl/cert.pem, read-only; NIX_SSL_CERT_FILE=/private/etc/ssl/cert.pem'
                        })
                        const before = values().map(el => el.getBoundingClientRect().width)
                        panel.querySelectorAll('dl dt').forEach(el => {
                            el.textContent = 'a label longer than the lane it is asked to share'
                        })
                        return values()
                            .map((el, at) => [el, before[at], el.getBoundingClientRect().width])
                            .filter(([_, was, now]) => Math.abs(was - now) > 1)
                            .map(([el, was, now]) =>
                                `${el.dataset.queryCell}: ${Math.round(was)}px became ${Math.round(now)}px`)
                    }"""))
                Expect.isEmpty
                    starved
                    (sprintf "a longer label must not narrow the value beside it, these lost room: %s"
                        (String.Join (" | ", starved)))
                do! awaitU (pageA.ClickAsync "[data-settings-toggle='close']")
            }

        // The picker's note and the connection panel above it are two halves of one answer
        // — what can a turn run on here — and this is the only tier that can watch them
        // disagree. They did: the catalogue had a probe of its own, fired only by opening
        // the drawer, while the sign-in flow (which runs with the drawer ALREADY open)
        // re-probed the status alone. So the panel went green and the note went on naming
        // an account that was by then connected. No cheap tier can see it: the markup is
        // right in both states, and each half is right on its own.
        sessionCase "connecting an account with the drawer open clears the picker's refusal" <|
            fun pageA ->
            async {
                do! awaitU (pageA.ClickAsync "[data-settings-toggle='open']")
                let! _ = await (pageA.WaitForSelectorAsync "[data-model-note='unavailable']")
                let noteText = "() => document.querySelector('[data-model-note]')?.textContent ?? ''"
                // The precondition, and the sentence a person actually read: this session
                // has no credential, so the note says so and points at the panel above it.
                let! before = await (pageA.EvaluateAsync<string> noteText)
                Expect.isTrue
                    (before.Contains "no Claude account connected")
                    (sprintf "with nothing connected the picker says so, it said: %s" before)

                // Connect one WITHOUT closing the drawer — the flow a person is actually in
                // when they sign in, and the one that used to leave the note behind.
                do! awaitU (pageA.FillAsync ("[data-claude-token]", "sk-ant-oat01-browser-case"))
                do! awaitU (pageA.ClickAsync "[data-claude-save-token]")
                let! _ = await (pageA.WaitForSelectorAsync "[data-claude-connected='mine']")

                // Whatever the picker says now, it cannot still be that: an account IS
                // connected. What it says instead is whatever the provider answered this
                // box — a list, or why there is none — which is the design rather than
                // something for a test to pin.
                let! _ =
                    await (pageA.WaitForFunctionAsync
                            """!(document.querySelector('[data-model-note]')?.textContent ?? '')
                                 .includes('no Claude account connected')""")
                ()
            }

        sessionCase "the doc store is keyed by session" <|
            fun pageA ->
            async {
                // The store is keyed by SESSION (embedded in the served page), not by address.
                let! sessionId =
                    await (pageA.EvaluateAsync<string> ("""() => document.querySelector('meta[name="yession-session"]')?.getAttribute('content')"""))
                Expect.isFalse (String.IsNullOrEmpty sessionId) "the bootstrap page must embed the session id"
                let! dbNames =
                    await (pageA.EvaluateAsync<string[]> ("""() => indexedDB.databases().then(dbs => dbs.map(d => d.name))"""))
                Expect.isTrue
                    (Array.contains (sprintf "yession/session/%s" sessionId) dbNames)
                    (sprintf "expected a session-keyed doc store, found: %s" (String.Join (", ", dbNames)))
            }

        sessionCase "a first-visit browser can begin connecting a credential" <|
            fun page ->
            async {
                // A browser that has been nowhere — no stored peer id, no cookie until the
                // bounce it just rode — is entitled to connect a credential the moment it is
                // signed in: ownership comes off the cookie's identity, never off anything the
                // browser kept. The HTTP tests pass an identity through by hand; this needs a
                // FIRST VISIT in a real browser, which is what every case here gets — the
                // fixture opens a context of its own and the page it hands over has been
                // nowhere.

                // Sign a credential in from settings, exactly as a human does. The control is the sidebar's `settings`
                // pivot: its own accessible name is the word it shows, so the hook — which is
                // the contract — is what to click. (`data-settings-toggle="prompt"` marks the
                // calls to action that also lead there; `open` is the pivot alone.)
                do! awaitU (page.ClickAsync "[data-settings-toggle='open']")
                let! _ = await (page.WaitForSelectorAsync "[data-claude-connect]")
                do! awaitU (page.ClickAsync "[data-claude-connect]")

                // The flow settles either into the paste-the-code step (the broker minted a
                // provider authorize URL — no network involved) or into a legible error.
                let! _ =
                    await (page.WaitForFunctionAsync
                            """!!document.querySelector('[data-claude-authorize]')
                               || !!document.querySelector('[data-claude-error]')""")
                let! error =
                    await (page.EvaluateAsync<string>
                            "() => document.querySelector('[data-claude-error]')?.textContent ?? ''")
                Expect.equal error "" "connecting must not be refused on a first visit"
            }
    ]

// --- The host-free editor rendering E2E ([Browser], no Native) ---------------------------
// Serves the static harness (app/browser/EditorHarness.fs, esbuilt to tests/browser/out/) and
// drives one Chromium page. No Session Process, no WebRTC — so this runs wherever Chromium
// exists, decoupled from the native node-datachannel addon. It guards exactly what the DOM-free
// cheap tests cannot: the input-rule → live formatting → Markdown round-trip in a real browser.

let internal harnessRoot = "tests/browser"

/// A tiny read-only static file server over `HttpListener` (the harness page + its bundle).
/// Returns the server — the listener to stop, and the origin it came up on, which is the only
/// place that origin is stated; requests are served on a background loop.
let internal serveStatic (root: string) : Serving =
    let served = listenOnLoopback ()
    let listener = served.Listener
    let rec loop () =
        async {
            match! Async.Catch (listener.GetContextAsync () |> Async.AwaitTask) with
            | Choice1Of2 ctx ->
                let rel = ctx.Request.Url.AbsolutePath.TrimStart '/'
                let rel = if rel = "" then "editor-harness.html" else rel
                let path = Path.Combine (root, rel)
                if File.Exists path then
                    let bytes = File.ReadAllBytes path
                    ctx.Response.ContentType <-
                        if path.EndsWith ".js" then "text/javascript"
                        elif path.EndsWith ".html" then "text/html"
                        // A stylesheet served as `application/octet-stream` is ignored by
                        // the browser, silently — which makes every layout measured on this
                        // page a fiction, and looks exactly like CSS that does not work.
                        elif path.EndsWith ".css" then "text/css"
                        else "application/octet-stream"
                    ctx.Response.OutputStream.Write (bytes, 0, bytes.Length)
                else ctx.Response.StatusCode <- 404
                ctx.Response.Close ()
                return! loop ()
            | Choice2Of2 _ -> ()   // listener stopped
        }
    Async.Start (loop ())
    served

/// One editor case: a served harness, a browser, a page that is being LISTENED to, and the
/// teardown — so a case is its body and nothing else.
///
/// The listening is the point. `watching`/`reporting` had exactly one call site, in the Native
/// suites, so every case in THIS suite — the one that runs on every pull request — failed
/// saying only that a wait had not settled. A shell that died at load reported as eight
/// anonymous timeouts, and the page had been naming the fault the whole time.
///
/// The harness is served by the case, on an origin the case never names: a port is what the
/// OS answers with here, never something a case knows. Cases used to take one by hand, `+ n`
/// off a base, and five of those offsets were used twice over — so two of them could not run
/// at once and the loser failed as an anonymous timeout.
///
/// Teardown runs whether the body throws or not, which is a fix rather than tidying: a
/// listener left bound by a failing case used to take the NEXT case with it — one failure,
/// two red cases, and the second one a lie.
let private editorCaseOn
    (viewport: (int * int) option)
    (name: string)
    (body: IPage -> Async<unit>)
    =
    testCaseAsync name <|
        async {
            let server = serveStatic harnessRoot
            let! pw = await (Playwright.CreateAsync ())
            let! br =
                await (pw.Chromium.LaunchAsync (
                    BrowserTypeLaunchOptions (ExecutablePath = chromiumPath ())))
            let! page =
                match viewport with
                | None -> await (br.NewPageAsync ())
                | Some (width, height) ->
                    async {
                        let! ctx =
                            await (br.NewContextAsync (
                                BrowserNewContextOptions (
                                    ViewportSize = ViewportSize (Width = width, Height = height))))
                        return! await (ctx.NewPageAsync ())
                    }
            page.SetDefaultTimeout 15000.0f
            let evidence = watching page
            let! _ = await (page.GotoAsync (server.At "/"))
            let! outcome = Async.Catch (reporting name page evidence (body page))
            do! awaitU (br.CloseAsync ())
            pw.Dispose ()
            server.Stop ()
            match outcome with
            | Choice1Of2 () -> ()
            | Choice2Of2 e -> raise e
        }

/// A case at the browser's own window size.
let private editorCase = editorCaseOn None

/// A case at a stated viewport. `ViewportSize` alone, never `IsMobile`: that additionally asks
/// Chromium to fit the layout to a device window, which measured here lands at 648px rather
/// than 390 — the very lie the ui-exploration skill warns about, arriving through another door.
let private editorCaseIn (width: int) (height: int) = editorCaseOn (Some (width, height))

/// How much narrower a message's ground is than the scrollport holding it — 0 when it runs
/// edge to edge, and the width of the margins either side when it does not.
///
/// Measured against the scrollport's CLIENT width rather than its border box, so a classic
/// scrollbar does not read as a message that stopped short of the screen.
let [<Literal>] private groundSpare =
    """() => {
         const item = document.querySelector("#shell [data-conversation] [data-message-id='msg-filler-8']")
         const port = document.querySelector('#shell [data-conversation]')
         return port.clientWidth - item.getBoundingClientRect().width
       }"""

let editorTests =
    testList "Editor rendering (browser)" [
        editorCase "Markdown typed in the rich editor renders formatted and round-trips to Markdown" <| fun page ->
            async {
                let! _ = await (page.WaitForSelectorAsync ".ProseMirror")

                // Type Markdown with REAL key events so the input rules fire: "# " turns the
                // block into a heading rendered live as <h1> — the syntax is never left literal.
                do! awaitU (page.ClickAsync ".ProseMirror")
                do! awaitU (page.Keyboard.TypeAsync "# Heading one")
                let! _ = await (page.WaitForFunctionAsync "document.querySelector('.ProseMirror h1')?.textContent === 'Heading one'")
                // `**bold**` -> a <strong> mark; `- ` -> a bullet list <ul><li>. The new line is
                // plain Enter here because the harness mounts the editor as the COMPOSER does,
                // where Enter is the paragraph key and Ctrl+Enter sends (asserted below).
                do! awaitU (page.Keyboard.PressAsync "Enter")
                do! awaitU (page.Keyboard.TypeAsync "text with **bold** now")
                let! _ = await (page.WaitForFunctionAsync "!!document.querySelector('.ProseMirror strong')")
                do! awaitU (page.Keyboard.PressAsync "Enter")
                do! awaitU (page.Keyboard.TypeAsync "- item one")
                let! _ = await (page.WaitForFunctionAsync "!!document.querySelector('.ProseMirror ul li')")

                // The document serializes back to Markdown (the durable form the drain snapshots).
                let! md = await (page.EvaluateAsync<string> "() => window.__md()")
                Expect.stringContains md "# Heading one" "heading serialized to markdown"
                Expect.stringContains md "**bold**" "bold serialized to markdown"
                Expect.stringContains md "* item one" "bullet serialized to markdown"
            }

        editorCase "Ctrl+Enter sends, Shift+Enter breaks the line, Enter opens a paragraph" <| fun page ->
            async {
                let! _ = await (page.WaitForSelectorAsync ".ProseMirror")

                do! awaitU (page.ClickAsync ".ProseMirror")
                do! awaitU (page.Keyboard.TypeAsync "first line")
                // Enter is a prose key now, not a send: it opens the PARAGRAPH a phone's
                // return key can reach with no modifier, and asks nothing to send. A binding
                // that both split the block AND sent would look right in a screenshot and
                // fire a half-written message on every return keypress.
                do! awaitU (page.Keyboard.PressAsync "Enter")
                do! awaitU (page.Keyboard.TypeAsync "second block")
                let! _ =
                    await (page.WaitForFunctionAsync
                        "document.querySelectorAll('#host .ProseMirror > p').length === 2")
                let! afterEnter = await (page.EvaluateAsync<string> "() => window.__md()")
                Expect.stringContains afterEnter "first line" "the first block survived"
                Expect.stringContains afterEnter "second block" "Enter opened a second block"
                let! sendsAfterEnter = await (page.EvaluateAsync<int> "() => window.__sends")
                Expect.equal sendsAfterEnter 0 "plain Enter did not send"

                // Shift+Enter breaks the LINE: a <br> inside the block it was already in, so
                // the paragraph count does not move. This is the half a single Enter could
                // never express, and it has to survive Markdown to be worth anything — the
                // serializer writes a trailing backslash and the parser reads it back.
                do! awaitU (page.Keyboard.PressAsync "Shift+Enter")
                do! awaitU (page.Keyboard.TypeAsync "same paragraph")
                let! _ = await (page.WaitForFunctionAsync "!!document.querySelector('.ProseMirror br')")
                let! _ =
                    await (page.WaitForFunctionAsync
                        "document.querySelectorAll('#host .ProseMirror > p').length === 2")
                let! broken = await (page.EvaluateAsync<string> "() => window.__md()")
                Expect.stringContains broken "second block" "the text before the break survived"
                Expect.stringContains broken "same paragraph" "and the text after it"

                // Ctrl+Enter (Cmd+Enter on macOS — `ControlOrMeta` picks the right one) is
                // where SEND went, and it leaves the document exactly as it was: a send that
                // also touched the text would look right in a screenshot and corrupt the
                // draft every time it fired.
                do! awaitU (page.Keyboard.PressAsync "ControlOrMeta+Enter")
                let! _ = await (page.WaitForFunctionAsync "window.__sends === 1")
                let! afterSend = await (page.EvaluateAsync<string> "() => window.__md()")
                Expect.equal afterSend broken "Ctrl+Enter sent without touching the document"
            }

        editorCase "a remote peer's selection renders as a caret widget, label, and highlight" <| fun page ->
            async {
                let! _ = await (page.WaitForSelectorAsync ".ProseMirror")

                // Give the editor some content, then select a RANGE (not a bare caret) so the
                // reported selection has distinct anchor/head — the editor relays it via
                // `reportFocus`, which the harness stashes.
                do! awaitU (page.ClickAsync ".ProseMirror")
                do! awaitU (page.Keyboard.TypeAsync "hello world")
                // `ControlOrMeta`, not `Control`: select-all is Cmd+A on macOS, where Ctrl+A is
                // the emacs "start of line" binding instead — so this selected nothing, the
                // range stayed empty, and the highlight this test waits for never rendered.
                // Invisible until the Browser tier could run on a Mac at all.
                do! awaitU (page.Keyboard.PressAsync "ControlOrMeta+a")

                // Replay that selection as a REMOTE peer's cursor. The decorations are built from
                // its relative positions: a caret widget + name label at `head`, and a translucent
                // highlight across the (non-empty) range.
                do! awaitU (page.EvaluateAsync "() => window.__pushRemote('remote-peer')")
                let! _ = await (page.WaitForFunctionAsync "!!document.querySelector('.ProseMirror .pm-caret')")
                let! _ =
                    await (page.WaitForFunctionAsync
                        "[...document.querySelectorAll('.ProseMirror .pm-caret-label')].some(l => l.textContent === 'remote-peer')")
                // The selection highlight is an inline decoration carrying our translucent colour.
                let! _ =
                    await (page.WaitForFunctionAsync
                        "[...document.querySelectorAll('.ProseMirror [style*=\"background-color\"]')].length > 0")
                return ()
            }

        // The invariant a caret has to keep out of the way of. Pinned in the CHEAP browser
        // tier because the only thing that could see it before was the two-peer WebRTC E2E,
        // and only under enough CI load to lose the race — a thirty-second wait that says
        // "timed out" and nothing else, twice, before the labels went on.
        //
        // What it pins is not the caret. It is that drawing one cannot cost a co-editor the
        // words: `PushPresences` dispatches a stepless ProseMirror transaction, ProseMirror
        // runs `ySyncPlugin`'s `view.update` on every state update regardless, and that hook
        // reconciles the WHOLE document back into Yjs. Do it while content is still arriving
        // and the push races the words it is drawing over.
        editorCase "a caret drawn on every frame never costs the mirror its content" <| fun page ->
            async {
                do! waitFor "both peers to mount" page "!!document.querySelector('#peer-a .ProseMirror') && !!document.querySelector('#peer-b .ProseMirror')"

                // Carets first, and left running for the whole case: the push has to be in
                // flight WHILE the content arrives, which is the only arrangement in which it
                // can race anything. Started before a single keystroke so no frame of the
                // convergence happens un-stormed.
                do! awaitU (page.EvaluateAsync "() => window.__caretStorm(true)")

                // A types into its own composer. Real key events, so every keystroke is its own
                // doc update and its own relay — the drip a collaborator actually produces,
                // rather than one paste the mirror could absorb in a single frame.
                do! awaitU (page.ClickAsync "#peer-a .ProseMirror")
                do! awaitU (page.Keyboard.TypeAsync "# Heading one")

                // The co-editor shows what was typed. This is the assertion the whole surface
                // exists for: it goes red when a caret push costs the content it draws over.
                //
                // On failure it says WHICH of the four stories happened, because the DOM alone
                // cannot: what each doc holds and what each editor rendered, side by side.
                try
                    do! waitFor
                            "the co-editor to render the author's remote content"
                            page
                            (sprintf "%s === 'Heading one'" (ownText "#peer-b .ProseMirror h1"))
                with e ->
                    let! state = await (page.EvaluateAsync<string> "() => window.__convState()")
                    return failwithf "%s\n  state: %s" e.Message state

                // ...and the caret really is drawn there, so this is a test of a caret over
                // content and not of an editor nobody decorated.
                do! waitFor "the author's caret to be drawn in the mirror" page "!!document.querySelector('#peer-b .pm-caret')"

                do! awaitU (page.EvaluateAsync "() => window.__caretStorm(false)")

                // Anti-vacuity, and the reason a green here means anything: a storm that never
                // ran converges beautifully. Frames are not free to assume — a page the browser
                // decided not to paint would push none of them.
                let! pushes = await (page.EvaluateAsync<int> "() => window.__caretPushes")
                if pushes < 5 then
                    failwithf
                        "the caret storm pushed %d times — too few for convergence to have been raced at all, so this case proved nothing"
                        pushes
            }

        // The other half of the same story, and the one the whole `pushPresences` debate turned
        // on: drawing a remote caret must not WRITE to the shared document.
        //
        // It is not obvious that it doesn't. `PushPresences` dispatches a stepless ProseMirror
        // transaction, and `ySyncPlugin`'s `view.update` hook — which runs on every state update,
        // `docChanged` or not — reconciles the whole document back into Yjs through
        // `_prosemirrorChanged`. The reconciliation is real and it is O(document); what this
        // pins is that it emits nothing, so a caret is a read of the doc and never a write to it.
        //
        // Counted from OUR side rather than by patching the library: Yjs updates on the
        // co-editor's doc whose origin is `ySyncPluginKey`, which is what that write-back tags
        // its transaction with. Real doc updates, not a hook we hoped was called.
        editorCase "drawing a remote caret writes nothing to the shared document" <| fun page ->
            async {
                do! waitFor "both peers to mount" page "!!document.querySelector('#peer-a .ProseMirror') && !!document.querySelector('#peer-b .ProseMirror')"
                do! awaitU (page.EvaluateAsync "() => window.__caretStorm(true)")
                do! awaitU (page.ClickAsync "#peer-a .ProseMirror")
                do! awaitU (page.Keyboard.TypeAsync "# Heading one")
                // Over CONTENT: an empty document is the case y-prosemirror short-circuits
                // anyway (`initialContentChanged`), so a storm over nothing would prove nothing.
                do! waitFor
                        "the co-editor to render the author's remote content"
                        page
                        (sprintf "%s === 'Heading one'" (ownText "#peer-b .ProseMirror h1"))
                do! awaitU (page.EvaluateAsync "() => window.__caretStorm(false)")

                let! pushes = await (page.EvaluateAsync<int> "() => window.__caretPushes")
                if pushes < 5 then
                    failwithf "the caret storm pushed %d times — too few to have exercised the write-back at all" pushes
                // The doc really moved under the storm — otherwise a write-back count of zero
                // says the observer was never wired, not that nothing was written.
                let! updates = await (page.EvaluateAsync<int> "() => window.__docUpdates")
                if updates < 1 then
                    failwith "the co-editor's doc took no updates at all, so a zero write-back count means the counter is broken, not that a caret is harmless"
                let! writebacks = await (page.EvaluateAsync<int> "() => window.__writebacks")
                Expect.equal
                    writebacks
                    0
                    (sprintf
                        "%d caret pushes produced %d Yjs updates from y-prosemirror's own write-back — drawing a caret is mutating the shared document, which is a co-editor's words at risk"
                        pushes writebacks)
            }

        // The replay (Plan 13, stage 3e). Everything else about it is pinned DOM-free — the
        // `.cast` rebuild against the real file, the closed tab, the retention gap — but not
        // this: whether `asciinema-player`'s named export resolves through the bundle and
        // actually plays what was recorded. An import that silently failed would leave every
        // other test green and the feature dead in the browser.
        editorCase "a recorded terminal replays in a real player, and prints what it printed" <| fun page ->
            async {
                // The player took the mount and built its own DOM there.
                let! _ = await (page.WaitForSelectorAsync "#replay .ap-player")
                // …with the transport controls that ARE the audit-read affordance: a replay
                // you cannot pause or seek is a video of a terminal, not a record of one.
                let! _ = await (page.WaitForSelectorAsync "#replay .ap-control-bar")
                // …and the chapter marks (Plan 14, stage 4), which are what make a
                // whole-terminal recording navigable by what ran in it. Asserted here
                // because a marker option the player silently ignored would leave every
                // DOM-free test green and the chapters absent.

                // Then play it, and wait for the recording's own output to appear on the
                // screen. This is the assertion that spans the whole stage: bytes the Session
                // Process wrote, encoded as asciicast, rebuilt by `TranscriptReplay.cast`,
                // and rendered by the player.
                let! _ = await (page.WaitForSelectorAsync "#replay .ap-overlay-start")
                do! awaitU (page.ClickAsync "#replay .ap-overlay-start")
                let! _ =
                    await (page.WaitForFunctionAsync
                        "document.querySelector('#replay').textContent.includes('total 0')")

                // …and the chapter marks (Plan 14, stage 4), which are what make a
                // whole-terminal recording navigable by what ran in it. Asserted after play
                // rather than before, because the recording's metadata — its duration, and
                // therefore where a marker sits on the bar — is not known until it loads.
                let! _ =
                    await (page.WaitForFunctionAsync
                        "document.querySelectorAll('#replay .ap-marker').length === 1")
                return ()
            }

        // The same player over a recording with DEAD AIR in it (Plan 25, stage 1) — the shape
        // the pane actually replays, and the one the case above cannot fail in: its recording
        // has no gap to compress and its single chapter sits at t=0, so chapters on the wrong
        // clock still look right there.
        //
        // The arrangement is shared by the two cases below and lives in the harness
        // (`#replay-gappy`): thirty seconds of nothing between two commands, a chapter on each,
        // and a start position naming the far one. What each case asserts is its own.
        editorCase "a chapter past a long idle gap still reaches the chapter list" <| fun page ->
            async {
                let! _ = await (page.WaitForSelectorAsync "#replay-gappy .ap-player")
                let! _ = await (page.WaitForSelectorAsync "#replay-gappy .ap-overlay-start")
                do! awaitU (page.ClickAsync "#replay-gappy .ap-overlay-start")
                // Both of them. The player idle-compresses the EVENTS it loads, so a chapter
                // list built on the recording's raw clock disagrees with them: the far chapter
                // becomes the last event and the list's own `time < duration` filter drops it,
                // leaving one mark where the recording has two. Chapters written into the cast
                // as `"m"` events ride the same compression as the records around them.
                //
                // Asserted after play, like the case above: where a mark sits on the bar is
                // not known until the recording's duration is.
                let! _ =
                    await (page.WaitForFunctionAsync
                        "document.querySelectorAll('#replay-gappy .ap-marker').length === 2")
                return ()
            }

        editorCase "a watch that starts past a long idle gap lands there, not before it" <| fun page ->
            async {
                let! _ = await (page.WaitForSelectorAsync "#replay-gappy .ap-overlay-start")
                do! awaitU (page.ClickAsync "#replay-gappy .ap-overlay-start")
                // The WAIT is the assertion, and the timeout is what makes it one: a start
                // position the player honours puts this text on screen as fast as it can
                // start, while one that lands short of the gap could only reach it after the
                // gap has played out in real time.
                let! _ =
                    await (page.WaitForFunctionAsync (
                        "document.querySelector('#replay-gappy').textContent.includes('second')",
                        null,
                        PageWaitForFunctionOptions (Timeout = 10_000f)))
                return ()
            }

        // A reference is part of the sentence it sits in, so its name shares the line's
        // baseline with the words either side. It did not: an inline-flex box lends the
        // line its first item's baseline, the mark has none, and every `dev` on a phone
        // floated a descender above its `started sandbox`. Geometry, which is what only a
        // rendered page can measure — pinned as an equality of baselines, never as a pixel
        // image, so a font or a mark redrawn does not move it.
        editorCase "a reference's name sits on the line's baseline" <| fun page ->
            async {
                do! awaitU (page.EvaluateAsync "() => window.__acts()")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-act-note] [data-entity]")
                let! drift =
                    await (page.EvaluateAsync<float> """() => {
                        const note = document.querySelector('#shell [data-act-note]')
                        const line = note.querySelector('[data-entity]').parentElement
                        const probe = document.createElement('span')
                        probe.style.cssText = 'display:inline-block;width:0;height:0;vertical-align:baseline'
                        line.appendChild(probe)
                        const baseline = probe.getBoundingClientRect().bottom
                        probe.remove()
                        const name = note.querySelector('[data-entity] > span:last-child')
                        const namesProbe = document.createElement('span')
                        namesProbe.style.cssText = 'display:inline-block;width:0;height:0;vertical-align:baseline'
                        name.appendChild(namesProbe)
                        const nameBaseline = namesProbe.getBoundingClientRect().bottom
                        namesProbe.remove()
                        return Math.abs(nameBaseline - baseline)
                    }""")
                Expect.isTrue (drift < 0.5) (sprintf "the name's baseline is the line's, it was %.2fpx off" drift)
            }

        // The fold's arrow sits on the dead centre of the act's gutter — the margin the title
        // clears — and of the title's own line, whatever width the platform gives the gutter.
        // Geometry, which only a rendered page can settle; pinned as a distance from the
        // centre, never as coordinates, so a wider gutter or a taller line does not move it.
        editorCase "a fold's arrow is centred in the act's gutter and on its title line" <| fun page ->
            async {
                do! awaitU (page.EvaluateAsync "() => window.__acts()")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-act-note] [data-fold]")
                let! off =
                    await (page.EvaluateAsync<float[]> """() => {
                        const note = document.querySelector('#shell [data-act-note]')
                        const arrow = note.querySelector('[data-fold] svg').getBoundingClientRect()
                        const box = note.getBoundingClientRect()
                        const gutter = parseFloat(getComputedStyle(note).paddingLeft)
                        // The title's FIRST line box, not its whole box: a title that wraps
                        // is two lines tall, and the arrow belongs on the first.
                        const line = note.querySelector('[data-fold] ~ span').getClientRects()[0]
                        return [ (arrow.left + arrow.width / 2) - (box.left + gutter / 2),
                                 (arrow.top + arrow.height / 2) - (line.top + line.height / 2) ]
                    }""")
                Expect.isTrue (abs off.[0] < 1.0) (sprintf "across the gutter, it was %.2fpx off centre" off.[0])
                Expect.isTrue (abs off.[1] < 1.0) (sprintf "down the title's line, it was %.2fpx off centre" off.[1])
            }

        // Unfolding shows the particulars and folding takes them off the page — not merely
        // out of sight but out of the accessibility tree and the tab order, which is what
        // `visibility` settles and a rendered string cannot see. Waited for, because the
        // fold MOVES: the settled state is the promise, the motion is the design.
        editorCase "unfolding an act shows its particulars, and folding hides them" <| fun page ->
            async {
                do! awaitU (page.EvaluateAsync "() => window.__acts()")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-act-note] [data-fold]")
                let visibility = "getComputedStyle(document.querySelector('#shell [data-act-note] [data-fold-body]')).visibility"
                do! waitFor "the particulars to start hidden" page (visibility + " === 'hidden'")
                do! awaitU (page.ClickAsync "#shell [data-act-note] [data-fold]")
                do! waitFor "the particulars to show once unfolded" page (visibility + " === 'visible'")
                let! shown =
                    await (page.EvaluateAsync<float> "() => document.querySelector('#shell [data-act-note] [data-act-said]').getBoundingClientRect().height")
                Expect.isTrue (shown > 0.0) "and what the agent was told has height on the page"
                do! awaitU (page.ClickAsync "#shell [data-act-note] [data-fold]")
                do! waitFor "the particulars to hide once folded" page (visibility + " === 'hidden'")
            }

        // Terminal work in the chat, and the pane's tabs (Plan 14, stages 1-2). Host-free,
        // like the editor and the replay beside it: what needs a real browser here is not the
        // Session Process but the DOM swaps — where FOCUS goes when a chip in the chat opens
        // a tab in the pane, and whether the tab strip is a tablist the arrow keys walk.
        // Neither is visible to a rendered string, and both are the WCAG floor rather than a
        // nicety: a chip that opens a pane and leaves focus behind, or a close that strands
        // focus on a control it just removed, is exactly the failure the floor names.
        editorCase "a chat chip opens a pane tab that plays, and the strip walks" <| fun page ->
            async {
                // The chip the harness model's one block puts in the chat.
                let! _ = await (page.WaitForSelectorAsync "#shell [data-chat-block]")
                do! awaitU (page.ClickAsync "#shell [data-chat-block]")

                // A tab opened, showing that block.
                let showingBlock =
                    """document.querySelector('#shell [data-pane-panel]')?.getAttribute('data-pane-panel')?.startsWith('block:') === true"""
                let! _ = await (page.WaitForFunctionAsync showingBlock)

                // Focus followed it into the pane. Asserted BEFORE anything is played,
                // because pressing play is itself a focus move.
                let! _ = await (page.WaitForFunctionAsync """document.activeElement?.hasAttribute('data-pane-panel') === true""")

                // The strip is a real tablist: an arrow key walks it. MANUAL activation, so
                // walking does not swap the panel under the reader per keypress.
                do! awaitU (page.FocusAsync "#shell [data-pane-tab^='block:']")
                do! awaitU (page.Keyboard.PressAsync "ArrowLeft")
                let! _ =
                    await (page.WaitForFunctionAsync
                        """document.activeElement?.getAttribute('data-pane-tab')?.startsWith('terminal:') === true""")
                let! _ = await (page.WaitForFunctionAsync showingBlock)

                // The block reads as TEXT, and its recording is one press away — the two reads
                // of one history, with the cheap one first. Pressing play mounts the real
                // player over the ranged cast the model built, inside the tab the chip
                // opened: a stream renderer would show a cursor-moving program as garbage,
                // which is the whole reason the transcript was written as asciicast.
                let! _ = await (page.WaitForSelectorAsync "#shell [data-pane-block] [data-terminal-output]")
                do! awaitU (page.ClickAsync "#shell [data-pane-watch]")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-pane-replay] .ap-overlay-start")
                do! awaitU (page.ClickAsync "#shell [data-pane-replay] .ap-overlay-start")
                let! _ =
                    await (page.WaitForFunctionAsync
                        """document.querySelector('#shell [data-pane-block]')?.textContent.includes('total 0') === true""")
                return ()
            }
        // The phone (Plan 14, stage 5). Headless Chromium clamps its WINDOW to ~500px, which
        // is why a naive narrow screenshot lies; Playwright's viewport is a real CDP device
        // metrics override, so 390 here is 390. The two things this asserts are the two the
        // plan is about: the pane takes the whole column rather than sitting over the chat
        // as a dismissible overlay, and nothing overflows sideways — an overflow a phone
        // user cannot scroll away is a reachability bug, not a cosmetic one.
        editorCaseIn 390 844 "on a phone the pane IS the column, the strip stays, and the chat is one control away" <| fun page ->
            async {
                // Ground truth first: the viewport really is the width we asked for.
                let! width = await (page.EvaluateAsync<int> "() => window.innerWidth")
                Expect.equal width 390 "a true phone viewport, not a clamped window"

                // The pane starts off screen, as it does for a fresh client.
                let! _ =
                    await (page.WaitForFunctionAsync
                        "document.querySelector('#shell [data-terminal-panel]').getBoundingClientRect().left >= window.innerWidth - 1")

                // A chip brings it on, and it takes the WHOLE column.
                do! awaitU (page.ClickAsync "#shell [data-chat-block]")
                let! _ =
                    await (page.WaitForFunctionAsync
                        """(() => {
                             const r = document.querySelector('#shell [data-terminal-panel]').getBoundingClientRect()
                             return r.left <= 1 && Math.round(r.width) === window.innerWidth
                           })()""")
                // …with the tab strip retained, which is what keeps phone and desktop one
                // mental model rather than two surfaces that happen to share a codebase.
                let! _ = await (page.WaitForSelectorAsync "#shell [role='tablist'] [data-pane-tab]")

                // Nothing overflows sideways.
                let! overflows =
                    await (page.EvaluateAsync<bool> "() => document.documentElement.scrollWidth > window.innerWidth + 1")
                Expect.isFalse overflows "no horizontal overflow a phone user cannot scroll away"

                // Nor is the header cut off vertically. The phone's band is a compressed one
                // and it carries something the desktop's does not — the session id, in flow
                // below the title — so it is the one place the heading can outgrow its band.
                // At 64px it did: the title's box started ON the band's top edge, which on a
                // phone reads as a heading sliced off by the browser. What is asserted is the
                // containment, not the number: whatever the band becomes, what it holds has
                // to fit inside it.
                let! headerFits =
                    await (page.EvaluateAsync<bool>
                            """() => {
                                 const band = document.querySelector('#shell header').getBoundingClientRect()
                                 const title = document.querySelector('#shell [data-session-title]').getBoundingClientRect()
                                 const id = document.querySelector('#shell [data-session-id]').getBoundingClientRect()
                                 return title.top > band.top && id.bottom <= band.bottom
                               }""")
                Expect.isTrue headerFits "the header's title and id sit inside the band, not on its edges"

                // And the way back to the chat is a control, not a dismissal: it returns
                // focus to the chip that opened the pane.
                do! awaitU (page.ClickAsync "#shell [data-terminal-toggle='hide']")
                let! _ =
                    await (page.WaitForFunctionAsync
                        "document.querySelector('#shell [data-terminal-panel]').getBoundingClientRect().left >= window.innerWidth - 1")
                let! _ = await (page.WaitForFunctionAsync """document.activeElement?.hasAttribute('data-chat-block') === true""")
                return ()
            }
        // What an agent says does not fit a phone: paths, URLs and fenced commands are all
        // longer than a 326px column and none of them has a space where the break has to go.
        // The timeline is a scroller on the vertical axis and therefore on both, so anything
        // that hangs out of the column slides the whole conversation sideways under a header
        // that stays put — which is what it looked like on iOS: every message shifted a
        // character or two off the left edge, with no way to put it back.
        //
        // The document-level check the case above makes cannot see this: the timeline's own
        // scrollbox absorbs the overflow, so `documentElement.scrollWidth` stays honest while
        // the conversation is unreadable. What is asserted is the column, and only the column.
        editorCaseIn 390 844 "a message no line break fits inside never scrolls the timeline sideways" <| fun page ->
            async {
                let! width = await (page.EvaluateAsync<int> "() => window.innerWidth")
                Expect.equal width 390 "a true phone viewport, not a clamped window"

                // The fixture's wide message is present — otherwise this passes by rendering
                // nothing that could have overflowed.
                let! _ = await (page.WaitForSelectorAsync "#shell [data-conversation] [data-message-body] pre")
                let! sideways =
                    await (page.EvaluateAsync<bool>
                            """() => {
                                 const timeline = document.querySelector('#shell [data-conversation]')
                                 return timeline.scrollWidth > timeline.clientWidth + 1
                               }""")
                Expect.isFalse sideways "the conversation column does not scroll sideways"
            }
        // The same promise, broken from the other side: not a body's text but a CHIP's. A
        // queued command names the terminal it waits in, and an agent's terminal is titled
        // `[sandbox] reason…` — sixty characters of tracked caps, wider than the column on
        // its own. Photographed on iOS as the whole conversation shifted left under a header
        // that stayed put, with the chip's status cut off at the right edge. The body case
        // above cannot see it: its fixture is prose, and prose is where the fix for prose is.
        editorCaseIn 390 844 "a queued command's terminal name never scrolls the timeline sideways" <| fun page ->
            async {
                let! width = await (page.EvaluateAsync<int> "() => window.innerWidth")
                Expect.equal width 390 "a true phone viewport, not a clamped window"

                // The chip is on screen and carries the name — otherwise this passes by
                // rendering nothing that could have overflowed.
                let! _ = await (page.WaitForSelectorAsync "#shell [data-conversation] [data-chat-pending] [data-pending-subject]")
                let! sideways =
                    await (page.EvaluateAsync<bool>
                            """() => {
                                 const timeline = document.querySelector('#shell [data-conversation]')
                                 return timeline.scrollWidth > timeline.clientWidth + 1
                               }""")
                Expect.isFalse sideways "the conversation column does not scroll sideways"
            }
        // The ask card stands where the conversation will, so it reads on the conversation's
        // leading line — the one the header's title and every message body start on. It did
        // not: the card spent its own gutter, three quarters of the transcript's, and the
        // question, the search field and four bordered rows all began a centimetre to the
        // left of every other word on the screen.
        //
        // Only a rendered page can settle it. The two columns are built from different tokens
        // in different files — the band's padding and the transcript's, plus the avatar gutter
        // the bodies carry — and a change to any of them leaves markup that still reads right
        // everywhere the cheap tier looks. And the two states cannot be on screen at once (the
        // card stands only over a session that has not begun, a body only over one that has),
        // which is what `window.__launch` is for.
        //
        // What is asserted is the COLUMN and nothing else: not the rules, not the tick's berth
        // in the gutter, not what the rows look like. Those are the design, and the design
        // changing is not a regression.
        let askCardColumnCase width height =
            editorCaseIn width height
                (sprintf "at %dpx the ask card's lines start where the conversation's words do" width) <| fun page ->
                async {
                    let! measured = await (page.EvaluateAsync<int> "() => window.innerWidth")
                    Expect.equal measured width "a true viewport, not a clamped window"

                    // Where the conversation's words start, read off the page the harness
                    // loads with: the body's own box plus the avatar gutter it carries.
                    let! _ = await (page.WaitForSelectorAsync "#shell [data-conversation] [data-message-body]")
                    let! column =
                        await (page.EvaluateAsync<int>
                                """() => {
                                     const body = document.querySelector('#shell [data-conversation] [data-message-body]')
                                     const box = body.getBoundingClientRect()
                                     return Math.round(box.left + parseFloat(getComputedStyle(body).paddingLeft))
                                   }""")

                    // The same session before it began, with a row held — so the branch line
                    // inside a row is on screen as well as the lines outside them.
                    do! awaitU (page.EvaluateAsync "() => window.__launch(true)")
                    let! _ = await (page.WaitForSelectorAsync "#shell [data-repo-picker] [data-repo-candidate]")
                    do! awaitU (page.ClickAsync "#shell [data-repo-picker] [data-repo-candidate]")
                    let! _ = await (page.WaitForSelectorAsync "#shell [data-repo-picker] [data-repo-candidate-branch]")

                    // Two ways to ask where a line starts, because the card's blocks run edge
                    // to edge (so a row's ground can) and carry the reading inset as PADDING.
                    // An element a block positions starts at its own box; an element carrying
                    // the inset itself — the search field — starts inside its padding.
                    //
                    // START is not among them, and was: the rail is where a LINE starts, and a
                    // button is not a line. It has a rim, and a rim on the rail is held 48px
                    // off one edge of the card and 16 off the other, which reads as a slab
                    // pushed sideways rather than as a margin. What holds for it instead is
                    // the case below.
                    let! adrift =
                        await (page.EvaluateAsync<string[]> (sprintf """() => {
                            const column = %d
                            const card = document.querySelector('#shell [data-repo-picker]')
                            const box = sel => Math.round(card.querySelector(sel).getBoundingClientRect().left)
                            const inside = sel => {
                              const el = card.querySelector(sel)
                              return Math.round(el.getBoundingClientRect().left
                                                + (parseFloat(getComputedStyle(el).paddingLeft) || 0))
                            }
                            return [
                                    ['the question', box('#repo-picker-title')],
                                    ['the search field', inside('[data-repo-picker-search]')],
                                    ['a row’s name', box('[data-repo-candidate-name]')]
                                  ]
                                .filter(([_, left]) => left !== column)
                                .map(([what, left]) => `${what} starts at ${left}px, the conversation at ${column}px`)
                          }""" column))
                    Expect.isEmpty
                        adrift
                        (sprintf "every line of the ask card starts on the conversation's own column, these did not: %s"
                            (String.Join (" | ", adrift)))
                }
        askCardColumnCase 390 844
        askCardColumnCase 1440 900
        // What a control owes the card's edges, where every line owes the reading rail.
        //
        // Full width on a phone is what makes this visible and what makes it worth pinning:
        // a button that spans the band shows both its margins at once, so an inset spent on
        // one edge and not the other is the whole shape of the thing. Symmetry rather than a
        // number — the margin is the card's to choose and a redesign may choose again, but
        // whatever it chooses is owed to both sides.
        editorCaseIn 390 844 "the start button is centred in the card, not shoved along the reading rail" <| fun page ->
            async {
                do! awaitU (page.EvaluateAsync "() => window.__launch(true)")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-repo-picker] [data-repo-picker-start]")
                let! margins =
                    await (page.EvaluateAsync<int[]>
                            """() => {
                                 const card = document.querySelector('#shell [data-repo-picker]')
                                 const start = card.querySelector('[data-repo-picker-start]')
                                 const outer = card.getBoundingClientRect()
                                 const inner = start.getBoundingClientRect()
                                 return [Math.round(inner.left - outer.left), Math.round(outer.right - inner.right)]
                               }""")
                Expect.equal
                    margins.[0]
                    margins.[1]
                    (sprintf "start stands the same distance from both of the card's edges, got %dpx and %dpx"
                        margins.[0] margins.[1])
            }
        // The card asks one thing at a time, and the second is to the RIGHT of the first.
        //
        // Both panes are in the document at once — they have to be, or the one arriving would
        // arrive on an empty stage mid-slide — so what says which is showing is not what is
        // RENDERED but where it IS, and only a browser knows that. Two halves, and either
        // alone passes on a bug: a pane off screen that is still reachable is a keyboard trap
        // in a surface that looks fine, and a pane that never moved is a slide that did not
        // happen behind markup that says it did.
        //
        // Not asserted: how long it takes, what it eases on, whether the rows stagger. Those
        // are the design.
        editorCaseIn 390 844 "the branch pane is off to the right until it is asked for, and out of reach until then" <| fun page ->
            async {
                do! awaitU (page.EvaluateAsync "() => window.__launch(true)")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-repo-picker] [data-repo-candidate]")
                // Holding a row is what puts a branch on it to go and change.
                do! awaitU (page.ClickAsync "#shell [data-repo-picker] [data-repo-candidate]")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-repo-candidate-branch]")

                let! away =
                    await (page.EvaluateAsync<bool>
                            """() => {
                                 const card = document.querySelector('#shell [data-repo-picker]')
                                 const branch = document.querySelector('#shell [data-repo-picker-pane="branch"]')
                                 return branch.getBoundingClientRect().left >= card.getBoundingClientRect().right - 1
                               }""")
                Expect.isTrue away "the branch pane starts off the card's right edge"
                let! reachable =
                    await (page.EvaluateAsync<bool> """() => !document.querySelector('#shell [data-repo-picker-pane="branch"]').inert""")
                Expect.isFalse reachable "and a keyboard cannot get into it while it is there"

                do! awaitU (page.ClickAsync "#shell [data-repo-candidate-branch]")

                // It arrives, and the pane it came from leaves by the other edge.
                let! _ =
                    await (page.WaitForFunctionAsync
                            """(() => {
                                 const card = document.querySelector('#shell [data-repo-picker]').getBoundingClientRect()
                                 const branch = document.querySelector('#shell [data-repo-picker-pane="branch"]').getBoundingClientRect()
                                 const repo = document.querySelector('#shell [data-repo-picker-pane="repo"]').getBoundingClientRect()
                                 return Math.abs(branch.left - card.left) <= 1 && repo.right <= card.left + 1
                               })()""")
                let! trapped =
                    await (page.EvaluateAsync<bool> """() => !document.querySelector('#shell [data-repo-picker-pane="repo"]').inert""")
                Expect.isFalse trapped "and the one that left is now the one out of reach"
            }
        // The listing pages as it is read, and READ is the word: no press, no button, and
        // nothing on screen that says there is more except the foot standing where the rows
        // to come will be.
        //
        // Only a browser can settle it. What decides is whether the foot is on screen, which
        // is a fact about layout inside a scroller the card owns — the model knows only that
        // a cursor exists, and every cheap tier reads markup that is right either way. And
        // the thing that watches is an `IntersectionObserver` bound after a render to a node
        // Lit drew, which is three things a rendered string does not have.
        editorCaseIn 390 844 "the listing pages as the reader reaches its foot, without a press" <| fun page ->
            async {
                do! awaitU (page.EvaluateAsync "() => window.__launch(true)")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-repo-picker] [data-repo-candidate]")

                // Ground truth, both halves: the foot is below the card, and the page it
                // stands for has not arrived. Without the first, this case passes on a foot
                // that was visible from the start and proves nothing about reaching it.
                let! below =
                    await (page.EvaluateAsync<bool>
                            """() => {
                                 const body = document.querySelector('#shell [data-repo-picker-body]')
                                 const foot = document.querySelector('#shell [data-repo-picker-foot]')
                                 return foot.getBoundingClientRect().top > body.getBoundingClientRect().bottom
                               }""")
                Expect.isTrue below "the foot starts below the card, so reaching it is something the reader does"
                let! arrivedEarly =
                    await (page.EvaluateAsync<bool> """() => !!document.querySelector('#shell [data-repo-candidate="octo/next-one"]')""")
                Expect.isFalse arrivedEarly "and the page it stands for has not been asked for"

                // Reaching it. A scroll of the card, which is what a thumb does — never a
                // call to whatever fetches, which would test the harness rather than the
                // page.
                do! awaitU (page.EvaluateAsync
                                """() => {
                                     const body = document.querySelector('#shell [data-repo-picker-body]')
                                     body.scrollTop = body.scrollHeight
                                   }""")

                let! _ = await (page.WaitForSelectorAsync "#shell [data-repo-candidate='octo/next-one']")
                let! ended =
                    await (page.EvaluateAsync<bool> """() => !document.querySelector('#shell [data-repo-picker-foot]')""")
                Expect.isTrue ended "and a page that carried no cursor is the end of the list, so the foot goes"
            }
        // The title is written per keystroke, so Enter has nothing to save — and that is
        // exactly why it has to DO something: a phone holds its keyboard open for as long as
        // the field holds focus, and a return key that answers nothing reads as an edit the
        // app declined to take. The promise is that Enter finishes with the field, and that
        // finishing keeps what was typed.
        //
        // Both halves, because either alone is satisfied by a bug: a field that let go and
        // reverted would pass the focus check, and one that kept the text with the keyboard
        // still over it would pass the value check. Only a browser can see either — focus and
        // a key event are not things a rendered string has.
        editorCaseIn 390 844 "Enter in the session title lets go of the field and keeps what was typed" <| fun page ->
            async {
                let! _ = await (page.WaitForSelectorAsync "#shell [data-session-title]")
                do! awaitU (page.FocusAsync "#shell [data-session-title]")
                do! awaitU (page.Keyboard.TypeAsync " on a phone")
                // Ground truth: the field really is where the typing went, and it really is
                // the focused element for Enter to release.
                let! _ =
                    await (page.WaitForFunctionAsync
                        """document.activeElement?.hasAttribute('data-session-title') === true""")

                do! awaitU (page.Keyboard.PressAsync "Enter")

                let! _ =
                    await (page.WaitForFunctionAsync
                        """document.activeElement?.hasAttribute('data-session-title') !== true""")
                let! title =
                    await (page.EvaluateAsync<string>
                            "() => document.querySelector('#shell [data-session-title]').value")
                Expect.stringContains title "on a phone" "the committed title is the one that was typed"
            }
        // The split between the two columns is the reader's to set. What is pinned is the
        // PROMISE, not the geometry: that the divider can be moved without a pointer at all.
        // A splitter that only answers a drag is a control a keyboard user cannot reach, and
        // nothing else in this suite would notice — the column would still render, still
        // scroll, and still be exactly the width somebody else chose for them.
        //
        // Deliberately not asserted: the default width, the step size, the bounds. Those are
        // the design, and the design changing is not a regression.
        editorCaseIn 1440 900 "the column divider moves from the keyboard, not only from a drag" <| fun page ->
            async {
                // Waited for on the CONTROL, never on the panel: a shut pane is `w-0`, which
                // Playwright reports as hidden, so waiting for the panel to be visible before
                // opening it waits for something that only happens afterwards.
                let! _ = await (page.WaitForSelectorAsync "#shell [data-terminal-toggle='show']")
                // This page carries two other fixtures ABOVE the shell — the editor host and
                // the player — so the shell starts a viewport and a half down. Every control
                // in it is reachable by scroll, which is fine for a person and a trap for a
                // test: the assertions here are about a WIDTH, and a click that has to scroll
                // first is one more thing that can be the reason a width did not change.
                let! _ =
                    await (page.EvaluateAsync<bool>
                            """() => {
                                 for (const id of ['host', 'replay']) {
                                   const el = document.getElementById(id)
                                   if (el) el.style.display = 'none'
                                 }
                                 document.querySelector('#shell [data-terminal-toggle="show"]').click()
                                 return true
                               }""")
                // Read on `aria-valuenow`, not on the rendered width.
                //
                // Not a convenience: the column animates, so its rendered width spends 200ms
                // being neither the old value nor the new one, and every way of asking "has it
                // settled" from the outside is a heuristic. Sampling twice and comparing was
                // the one tried here, and it accepted a 1px panel — a shut pane is its own left
                // border — as "open and settled" because two polls happened to agree before the
                // transition started. It passed twice and failed the third time, which is worse
                // than not having been written.
                //
                // The separator's value is the state the shell actually owns: the keydown
                // handler sets it synchronously, so there is nothing to wait for and nothing to
                // race. It is also the thing this test is ABOUT — what a keyboard user is told
                // the split is. That the pixels follow it is asserted once at the end, where
                // the target is known and the wait is therefore deterministic.
                let value () =
                    page.EvaluateAsync<float>
                        "() => Number(document.querySelector('#shell [data-term-resize]').getAttribute('aria-valuenow'))"

                // Focusable, and it says what it is: a separator with a value is the one
                // shape assistive technology can report and move.
                let! _ =
                    await (page.EvaluateAsync<bool>
                            "() => { document.querySelector('#shell [data-term-resize]').focus(); return true }")
                let! isSeparator =
                    await (page.EvaluateAsync<bool>
                            """() => {
                                 const h = document.activeElement
                                 return h?.getAttribute('role') === 'separator'
                                     && h.hasAttribute('aria-valuenow')
                               }""")
                Expect.isTrue isSeparator "the focused divider is a separator carrying its value"
                let! before = await (value ())

                // The two arrows move it, and they are opposites. Which one GROWS the column
                // is deliberately not asserted: this one is on the right, so left-grows reads
                // as "drag its edge", but a design that put the pane elsewhere or read the
                // keys the other way round would be a different choice rather than a broken
                // one — and a test that failed for it would be reporting taste as a
                // regression. What has to hold is that a keyboard can move the split at all,
                // and that the second press undoes the first.
                do! awaitU (page.Keyboard.PressAsync "ArrowLeft")
                let! moved = await (value ())
                Expect.isTrue
                    (abs (moved - before) > 1.0)
                    (sprintf "an arrow key must move the split (was %f, still %f)" before moved)
                do! awaitU (page.Keyboard.PressAsync "ArrowRight")
                let! back = await (value ())
                Expect.isTrue
                    (abs (back - before) < abs (moved - before))
                    (sprintf "the opposite arrow must move it back (started %f, went %f, now %f)"
                        before moved back)

                // And the column is really that wide, once it has finished travelling there.
                // Deterministic because the destination is declared: what is waited for is the
                // pixels catching up to the value, not a guess about when a transition ended.
                // Without this the test would be happy with a separator that narrates a resize
                // nothing performed.
                let! _ =
                    await (page.WaitForFunctionAsync
                            """() => {
                                 const h = document.querySelector('#shell [data-term-resize]')
                                 const pane = document.querySelector('#shell [data-terminal-panel]')
                                 const said = Number(h.getAttribute('aria-valuenow'))
                                 return Math.abs(pane.getBoundingClientRect().width - said) <= 1
                               }""")
                return ()
            }
        // The live viewport (Plan 14, stage 6). What only a browser can answer here is the
        // KEYSTROKE TRANSLATION: a `KeyboardEvent` is not a byte stream, and turning one
        // into what a pty expects — printable characters as themselves, Ctrl-<key> as the
        // control code, the keys with no character at all as their escape sequences — is the
        // whole of what a terminal front end does with a keyboard.
        editorCase "the holder types into the live screen, and the keys reach it as a pty expects" <| fun page ->
            async {
                // The column starts shut, as it does for a fresh client.
                do! awaitU (page.ClickAsync "#shell [data-terminal-toggle='show']")
                // The terminal the harness holds the lease on renders its screen, and the
                // screen shows what the program drew.
                do! awaitU (page.ClickAsync "#shell [data-terminal-tab='term-live']")
                let screen = "#shell [data-terminal-screen='term-live']"
                let! _ = await (page.WaitForSelectorAsync screen)
                let! _ =
                    await (page.WaitForFunctionAsync
                        (sprintf "document.querySelector(%s).textContent.includes('vim ~/notes')" "\"#shell [data-terminal-screen='term-live']\""))

                // It is a Tab stop, because its whole purpose is having the keyboard.
                do! awaitU (page.FocusAsync screen)
                let! _ =
                    await (page.WaitForFunctionAsync
                        """document.activeElement?.getAttribute('data-terminal-screen') === 'term-live'""")

                // The screen is composed by a REAL emulator in a real browser: the
                // Session Process's snapshot seeds it, and the records the client already
                // holds are folded on top. This is the only tier that runs xterm in the
                // browser at all — and it exists because a browser-only module resolution
                // failure in exactly this path reached a release job while the cheap tier
                // and this one were both green.
                // Seeded with something the assertion below does NOT look for: what is
                // being proven is the FOLD — "earlier output" exists only in the transcript
                // records, never in the screen the model was built with — so a client that
                // rendered the snapshot and folded nothing would fail here.
                do! awaitU (page.EvaluateAsync ("() => window.__snapshot('term-live', 0, 'session start\\r\\n')"))
                let! _ =
                    await (page.WaitForFunctionAsync
                        (sprintf "document.querySelector(%s).textContent.includes('earlier output')" "\"#shell [data-terminal-screen='term-live']\""))

                do! awaitU (page.Keyboard.TypeAsync "ls")
                do! awaitU (page.Keyboard.PressAsync "ArrowUp")
                do! awaitU (page.Keyboard.PressAsync "Control+c")
                do! awaitU (page.Keyboard.PressAsync "Backspace")
                do! awaitU (page.Keyboard.PressAsync "Enter")
                let! typed = await (page.EvaluateAsync<string> "() => window.__typed || ''")
                Expect.equal typed "ls\u001b[A\u0003\u007f\r" "printable, escape, control code, delete, carriage return"

                // Tab is SENT rather than moving focus out of the terminal mid-session, and
                // the shift is that `preventDefault` fires for everything the terminal takes.
                let! before = await (page.EvaluateAsync<string> "() => document.activeElement?.getAttribute('data-terminal-screen')")
                do! awaitU (page.Keyboard.PressAsync "Tab")
                let! after = await (page.EvaluateAsync<string> "() => document.activeElement?.getAttribute('data-terminal-screen')")
                Expect.equal after before "Tab types a tab; it does not leave the terminal"
            }
        // Taking the keyboard is the whole of what live mode is, and the keyboard has to
        // follow it. Only a browser can answer this: both routes into live mode remove the
        // element that had focus in the render they arrive on — `take` removes itself, and the
        // lease landing replaces the command line with the lease bar — so what is under test
        // is where focus ends up after a DOM swap, which is not a fact any rendered string
        // holds.
        editorCase "taking a terminal puts the keyboard in it" <| fun page ->
            async {
                // `term-harness` is the pane's opening tab, and it holds no lease: a terminal
                // in block mode, which is where somebody who wants to type is standing. Not
                // clicked — activating the tab you are already on is the PIN gesture.
                do! awaitU (page.ClickAsync "#shell [data-terminal-toggle='show']")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-terminal-take='term-harness']")

                // The press that hands this peer the lease — and removes itself doing it.
                do! awaitU (page.ClickAsync "#shell [data-terminal-take='term-harness']")
                let! _ =
                    await (page.WaitForFunctionAsync
                        """document.activeElement?.getAttribute('data-terminal-screen') === 'term-harness'""")

                // …and it is really the keyboard, not merely a focus ring: what is typed now
                // reaches the pty rather than the composer that used to be there.
                do! awaitU (page.Keyboard.PressAsync "ArrowUp")
                let! typed = await (page.EvaluateAsync<string> "() => window.__typed || ''")
                Expect.equal typed "\u001b[A" "a key pressed after the take reaches the terminal"
            }
        // The other route in, and the reason the focus move lives in the render loop rather
        // than on the press: a block that takes the screen hands its author the keyboard with
        // nobody pressing anything. Focus that SURVIVED that render is not stranded and must
        // not be taken — a terminal going full-screen three tabs away is not a reason to yank
        // somebody's caret out of the message they are writing.
        editorCase "a terminal going live does not take the keyboard from what someone is writing" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-terminal-toggle='show']")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-terminal-take='term-harness']")

                // Somebody's keyboard is somewhere else in the pane — on the splitter, which
                // is rendered whatever the terminal is doing and survives this render.
                do! awaitU (page.FocusAsync "#shell [data-term-resize]")
                let! _ =
                    await (page.WaitForFunctionAsync
                        """document.activeElement?.hasAttribute('data-term-resize') === true""")

                // The lease arrives on its own, as the alt-screen flip delivers it.
                do! awaitU (page.EvaluateAsync "() => window.__take('term-harness')")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-terminal-screen='term-harness']")
                let! kept =
                    await (page.EvaluateAsync<string> "() => document.activeElement?.outerHTML?.slice(0, 60) ?? 'NOTHING'")
                Expect.stringContains kept "data-term-resize" "a lease landing leaves focus that survived the render where it was"
            }
        // Moving by WORD, which is most of what navigating a line you have already typed
        // means. Its own case rather than an extra assertion on the one above: that pins the
        // printable/control/escape table and should keep failing for only that reason.
        //
        // A `KeyboardEvent` with modifiers on it is the part only a real browser has — the
        // combination is what carries the meaning, and there is no rendered string that holds
        // whether Alt was down when a key went by.
        editorCase "word-navigation keys reach the pty as the escape sequences they are" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-terminal-toggle='show']")
                do! awaitU (page.ClickAsync "#shell [data-terminal-tab='term-live']")
                let screen = "#shell [data-terminal-screen='term-live']"
                let! _ = await (page.WaitForSelectorAsync screen)
                do! awaitU (page.FocusAsync screen)
                let! _ =
                    await (page.WaitForFunctionAsync
                        """document.activeElement?.getAttribute('data-terminal-screen') === 'term-live'""")

                do! awaitU (page.Keyboard.PressAsync "Control+ArrowLeft")
                do! awaitU (page.Keyboard.PressAsync "Alt+b")
                do! awaitU (page.Keyboard.PressAsync "Alt+Backspace")
                do! awaitU (page.Keyboard.PressAsync "Control+Backspace")
                do! awaitU (page.Keyboard.PressAsync "Shift+ArrowRight")
                let! typed = await (page.EvaluateAsync<string> "() => window.__typed || ''")
                Expect.equal
                    typed
                    "\u001b[1;5D\u001bb\u001b\u007f\b\u001b[1;2C"
                    "a word left, a word back, rub a word out twice, and a selection right"
            }
        // A screen is a paint at a GEOMETRY, and the client's emulator has to be the geometry
        // the pty is or everything on it lands in the wrong column. Only a browser can answer
        // it: this is the real `@xterm/headless` composing a real screen, and what is asserted
        // is where the text ended up — which no rendered string holds.
        //
        // `ESC[500G` is how a program asks for the last column, whatever that is. The
        // serializer answers with the gap it measured — `ESC[39C` on a 40-column screen,
        // `ESC[99C` on a 100-column one — so this reads the width straight off the rendered
        // line, and would have read 79 for both back when the snapshot carried no size.
        editorCase "the live screen is the shape the process says it is" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-terminal-toggle='show']")
                do! awaitU (page.ClickAsync "#shell [data-terminal-tab='term-live']")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-terminal-screen='term-live']")

                // Seeded at a seq past everything the fixture's feed holds, so what ends up on
                // this screen is only what this case put there.
                do! awaitU (page.EvaluateAsync "() => window.__snapshot('term-live', 10, '', 40, 24)")
                do! awaitU (page.EvaluateAsync "() => window.__record('term-live', 10, 'o', '\\u001b[500GX')")
                // Waited for, then MEASURED. A wait that was also the assertion would fail as
                // a timeout naming neither number, and this tier can only fail by a wait
                // never settling.
                let lineEndingIn (mark: string) =
                    sprintf
                        """(() => {
                             const el = document.querySelector("[data-terminal-screen='term-live']")
                             const line = el.textContent.split('\n').find(l => l.trimEnd().endsWith('%s'))
                             return line === undefined ? -1 : line.trimEnd().length
                           })()"""
                        mark
                let widthOfLineEndingIn (mark: string) =
                    async {
                        let! _ = await (page.WaitForFunctionAsync (lineEndingIn mark + " > 0"))
                        return! await (page.EvaluateAsync<int> ("() => " + lineEndingIn mark))
                    }
                let! atForty = widthOfLineEndingIn "X"
                Expect.equal atForty 40 "the last column of a 40-column screen"

                // A resize is a record like any other, and the emulator follows it — the pty
                // was told the same thing at the same point in the same stream.
                do! awaitU (page.EvaluateAsync "() => window.__record('term-live', 11, 'r', '100x24')")
                do! awaitU (page.EvaluateAsync "() => window.__record('term-live', 12, 'o', '\\r\\n\\u001b[500GY')")
                let! atHundred = widthOfLineEndingIn "Y"
                Expect.equal atHundred 100 "and of a 100-column one, after the resize"
            }
        // A box can change without the model changing — the splitter is dragged, the window is
        // resized, the phone is turned — and the pty has to be told, or the program inside it
        // lays its screen out to a width that stopped being true. Only a browser can answer
        // this: what is under test is a measurement of a real box, taken because the box moved
        // rather than because anything was dispatched.
        editorCaseIn 1440 900 "a pane the reader resized tells the pty its new width" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-terminal-toggle='show']")
                do! awaitU (page.ClickAsync "#shell [data-terminal-tab='term-live']")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-terminal-screen='term-live']")

                // The size this client reported for the pane as it stands.
                let reported () = page.EvaluateAsync<string> "() => window.__resized || ''"
                let! _ = await (page.WaitForFunctionAsync "window.__resized")
                let! before = await (reported ())

                // Widen the column from the keyboard, which dispatches nothing at all: the
                // splitter writes a CSS custom property on the shell root.
                do! awaitU (page.FocusAsync "#shell [data-term-resize]")
                for _ in 1 .. 12 do
                    do! awaitU (page.Keyboard.PressAsync "ArrowLeft")
                let! _ =
                    await (page.WaitForFunctionAsync (sprintf "window.__resized !== '%s'" before))
                let! after = await (reported ())
                Expect.notEqual after before "the pty is told the width the reader chose"
            }
        // The same measurement, in the mode that CANNOT report it over the wire. A terminal
        // running commands as blocks has no lease, so nothing is sent to a pty — but the width
        // is exactly what the next command will claim (`PendingAct.Size`), and a block that ran
        // at eighty columns is eighty-column text in the transcript for ever. Only a browser
        // can answer it: what is under test is that a pane showing BLOCKS is a measurable box
        // at all, and that the number follows the reader's own splitter.
        editorCaseIn 1440 900 "a pane showing blocks measures itself, with no lease to report through" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-terminal-toggle='show']")
                let! _ =
                    await (
                        page.WaitForSelectorAsync
                            "#shell [data-terminal-scrollback][data-terminal-id='term-harness']")

                // Nobody holds this terminal, so the holder's report never fires: this is the
                // measurement the model kept, which is the one a command reads.
                let viewport () = page.EvaluateAsync<string> "() => window.__viewport || ''"
                let! _ = await (page.WaitForFunctionAsync "window.__viewport")
                let! before = await (viewport ())

                // Widen the column from the keyboard, which dispatches nothing at all.
                do! awaitU (page.FocusAsync "#shell [data-term-resize]")
                for _ in 1 .. 12 do
                    do! awaitU (page.Keyboard.PressAsync "ArrowLeft")
                let! _ = await (page.WaitForFunctionAsync (sprintf "window.__viewport !== '%s'" before))
                let! after = await (viewport ())
                Expect.notEqual after before "the width the next command would claim follows the pane"
            }
        // A chapter's rule is in the FLOW, which is the whole of what replaced the rail: no
        // measurement places it, so what a browser has to settle is not where it was put but
        // that it is where the document says — above the message it opens at, across the
        // reading column, and painted. Every cheap tier reads markup, and markup cannot tell a
        // rule standing over its message from one collapsed to nothing behind it.
        editorCaseIn 1440 900 "a chapter's rule stands above the message it opens, across the column" <| fun page ->
            async {
                let! _ = await (page.WaitForSelectorAsync "#shell [data-chapter-rule='msg-filler-8']")
                let! above =
                    await (page.EvaluateAsync<bool>
                            """() => {
                                 const rule = document.querySelector("#shell [data-chapter-rule='msg-filler-8']")
                                 const item = document.querySelector("#shell [data-conversation] [data-message-id='msg-filler-8']")
                                 const r = rule.getBoundingClientRect()
                                 const i = item.getBoundingClientRect()
                                 return r.height > 0 && r.width > 0 && r.bottom <= i.top
                               }""")
                Expect.isTrue above "the rule stands above its message, with a box of its own"

                // And it is a line somebody can SEE. A border that resolved to nothing, or
                // painted the ground onto the ground, measures exactly as one that works.
                let! painted =
                    await (page.EvaluateAsync<bool>
                            """() => {
                                 const rule = document.querySelector("#shell [data-chapter-rule='msg-filler-8']")
                                 const style = getComputedStyle(rule)
                                 const ground = getComputedStyle(document.querySelector('#shell [data-conversation]')).backgroundColor
                                 return parseFloat(style.borderTopWidth) > 0 && style.borderTopColor !== ground
                               }""")
                Expect.isTrue painted "the rule is a line on the screen, not a border the ground swallowed"

                // It divides the COLUMN, so it is exactly as wide as the column. Narrower and
                // it reads as a mark on one message; wider — which is what a rule with no
                // measure of its own does on a desktop — and it reads as a line drawn on the
                // page, with the conversation happening to sit inside it.
                let! spans =
                    await (page.EvaluateAsync<bool>
                            """() => {
                                 const rule = document.querySelector("#shell [data-chapter-rule='msg-filler-8']")
                                 const item = document.querySelector("#shell [data-conversation] [data-message-id='msg-filler-8']")
                                 return Math.abs(rule.getBoundingClientRect().width
                                                 - item.getBoundingClientRect().width) <= 1
                               }""")
                Expect.isTrue spans "the rule runs the width of the column it divides"
                return ()
            }
        // The reply ref's jump — the same `revealMessage` the rail drives, reached from the
        // other end. A detached reply sits at the bottom of the harness; its source is the very
        // first message, off the top. Tapping the ref must bring that message on screen AND put
        // the cursor on it, or a keyboard reader is shown the message and stranded on the
        // control that scrolled away. Only a browser settles focus: `activeElement` is empty in
        // every cheap tier that reads markup.
        editorCaseIn 1440 900 "the reply ref takes you to the message it answers, and lands the cursor on it" <| fun page ->
            async {
                let! _ = await (page.WaitForSelectorAsync "#shell [data-reply-jump][data-reply-ref='msg-harness']")
                // To the bottom, where the reply sits, so its source is off the top and the jump is real.
                let! _ =
                    await (page.EvaluateAsync<bool>
                            """() => { const t = document.querySelector('#shell [data-conversation]')
                                       t.scrollTop = t.scrollHeight
                                       return t.scrollTop > 0 }""")
                do! awaitU (page.ClickAsync "#shell [data-reply-jump][data-reply-ref='msg-harness']")
                let! _ =
                    await (page.WaitForFunctionAsync
                        """document.querySelector("#shell [data-conversation] [data-message-id='msg-harness']")
                             ?.classList.contains('animate-reveal') === true""")
                let! landed =
                    await (page.EvaluateAsync<bool>
                            """() => {
                                 const item = document.querySelector("#shell [data-conversation] [data-message-id='msg-harness']")
                                 const box = item.getBoundingClientRect()
                                 const at = document.elementFromPoint(box.left + box.width / 2, box.top + 4)
                                 return item.contains(at) && item === document.activeElement
                               }""")
                Expect.isTrue landed "the message the ref answers is on the screen and holds the cursor"
                return ()
            }
        // The contents' promise, and the reason it is the phone's whole navigation: tapping a
        // chapter takes you to it. `revealMessage` finds an element by id, scrolls it, flashes
        // it and moves the cursor there — a hook that stopped matching would leave a list of
        // buttons that quietly do nothing, which no rendered string can tell from one that
        // works.
        editorCaseIn 1440 900 "a chapter in the contents takes you to it" <| fun page ->
            async {
                let! _ = await (page.WaitForSelectorAsync "#shell [data-chapters] [data-chapter-entry]")
                // Away from it first, so the jump has a real scroll to make.
                let! _ =
                    await (page.EvaluateAsync<bool>
                            """() => { const t = document.querySelector('#shell [data-conversation]')
                                       t.scrollTop = t.scrollHeight
                                       return t.scrollTop > 0 }""")
                do! awaitU (page.ClickAsync "#shell [data-chapter-entry='msg-harness']")
                let! _ =
                    await (page.WaitForFunctionAsync
                        """document.querySelector("#shell [data-conversation] [data-message-id='msg-harness']")
                             ?.classList.contains('animate-reveal') === true""")
                let! focused =
                    await (page.EvaluateAsync<bool>
                            """() => document.querySelector("#shell [data-conversation] [data-message-id='msg-harness']") === document.activeElement""")
                Expect.isTrue focused "the contents moves the cursor to the chapter, through the same reveal the ref uses"
                return ()
            }
        // On a phone the contents live in a DRAWER over the conversation, so the jump has one
        // more thing to get right than it does on a desktop: the sheet the tap came from has
        // to stand aside, or the reader is taken to a message they cannot see. Nothing in the
        // markup says whether a drawer is over the words — the message scrolls, flashes and
        // takes the cursor either way.
        //
        // The drawer is opened by the class the shell itself uses for it, because the harness
        // wires no nav toggle; what is under test is the jump, not the chevron. The case reads
        // the cover BEFORE the tap as well as after, so an arrangement that stopped covering
        // anything would fail here rather than pass by vacuity.
        editorCaseIn 390 844 "a chapter reached from the phone's contents is not left behind the drawer" <| fun page ->
            async {
                let! _ = await (page.WaitForSelectorAsync "#shell [data-chapters] [data-chapter-entry]")
                let! covered =
                    await (page.EvaluateAsync<bool>
                            """() => {
                                 document.documentElement.classList.add('nav-alt')
                                 const item = document.querySelector("#shell [data-conversation] [data-message-id='msg-harness']")
                                 const box = item.getBoundingClientRect()
                                 const at = document.elementFromPoint(box.left + box.width / 2, box.top + 4)
                                 return !item.contains(at)
                               }""")
                Expect.isTrue covered "the drawer is over the conversation, which is what the phone's contents sit in"

                do! awaitU (page.ClickAsync "#shell [data-chapter-entry='msg-harness']")
                let! _ =
                    await (page.WaitForFunctionAsync
                        """document.querySelector("#shell [data-conversation] [data-message-id='msg-harness']")
                             ?.classList.contains('animate-reveal') === true""")
                // Hit-tested at its own centre, which is the only way to ask whether something
                // is over it: a drawer left open leaves the message's box exactly where it was,
                // and every cheap visibility check answers yes.
                let! uncovered =
                    await (page.WaitForFunctionAsync
                            """(() => {
                                 const item = document.querySelector("#shell [data-conversation] [data-message-id='msg-harness']")
                                 const box = item.getBoundingClientRect()
                                 const at = document.elementFromPoint(box.left + box.width / 2, box.top + 4)
                                 return item.contains(at)
                               })()""")
                Expect.isNotNull uncovered "the drawer stood aside, so the chapter is on the screen"
                return ()
            }
        // A page that lands with the client still behind is not a picture anybody asked for:
        // a cold open reads the log from the oldest a page per round trip, pinned to its
        // foot, and every page rendered was history scrolling past under the eye (116 on a
        // session of 97 items). Only a browser can say whether the page RENDERED — the model
        // folds every page either way, and the render is the app's own count — so this
        // drives the harness's cold open, fifteen pages a round trip apart, well inside the
        // window a render is held for, and asks how many times the app drew.
        editorCaseIn 390 844 "pages that leave the client behind are folded but not drawn" <| fun page ->
            async {
                let! _ = await (page.WaitForSelectorAsync "#shell [data-conversation]")
                // 60 items of streamed replies is ~1,500 events: fifteen pages of a hundred,
                // twenty-five milliseconds apart — three hundred and seventy-five of the five
                // hundred a render is held for.
                let! report = await (page.EvaluateAsync<string> "() => window.__benchOpenCold(60, 100, 25)")
                use doc = System.Text.Json.JsonDocument.Parse report
                let renders = doc.RootElement.GetProperty("renders").GetInt32 ()
                let items = doc.RootElement.GetProperty("items").GetInt32 ()
                Expect.isTrue (items >= 60) "every page was folded: the whole conversation is on the page"
                // The shell, the connection, and the catch-up's end — never one per page. A
                // ceiling with room in it, because what it pins is the pages NOT drawing;
                // one per page is fifteen more than this.
                Expect.isTrue (renders <= 5) (sprintf "the pages in between were folded without a render each — %d renders for fifteen pages" renders)
                return ()
            }
        // A scroll in progress survives the renders that land during it. A render puts each
        // pinned surface's scroll back where the reader had it, and a write to `scrollTop`
        // — even of the value it already holds — ends whatever scroll the browser has in
        // flight. Records landing in a terminal the conversation does not draw are a render
        // each and move nothing on it; under the unconditional write, a scroll started from
        // the end stayed at the end for as long as they kept coming (`Render.fs`,
        // `restoreSurfaceScroll`). Only a browser has a scroll in flight to take away, so
        // only a browser can watch it survive. Smooth rather than flung: the same in-flight
        // scroll, and one with a stated destination to check against.
        editorCaseIn 390 844 "a scroll in progress is not taken away by the renders that land during it" <| fun page ->
            async {
                let! _ = await (page.WaitForSelectorAsync "#shell [data-conversation]")
                // Two hundred items, a record a frame for four hundred frames — the scroll has
                // to reach the top well inside that, so every frame of it has a render in it.
                do! awaitU (page.EvaluateAsync "() => window.__benchScrollBegin(200, 400, 16, true)")
                let! reached =
                    await (page.EvaluateAsync<float>
                            """() => new Promise((done) => {
                                 const el = document.querySelector('#shell [data-conversation]')
                                 el.scrollIntoView()
                                 el.scrollTop = el.scrollHeight
                                 requestAnimationFrame(() => {
                                   el.scrollTo({ top: 0, behavior: 'smooth' })
                                   // Until it stops moving for three frames, wherever that is.
                                   let last = el.scrollTop, still = 0
                                   const tick = () => {
                                     if (el.scrollTop === last) still++; else { still = 0; last = el.scrollTop }
                                     if (still >= 3) done(el.scrollTop); else requestAnimationFrame(tick) }
                                   requestAnimationFrame(tick)
                                 })
                               })""")
                let! report = await (page.EvaluateAsync<string> "() => window.__benchScrollEnd()")
                use doc = System.Text.Json.JsonDocument.Parse report
                let renders = doc.RootElement.GetProperty("renders").GetInt32 ()
                let startedAt = doc.RootElement.GetProperty("scrolledFrom").GetDouble ()
                Expect.equal reached 0.0 "the scroll reached the top it was sent to, through every render on the way"
                // Anti-vacuity: a conversation that fit the screen had no scroll to lose, and
                // a stream that never rendered had nothing to lose it to.
                Expect.isTrue (startedAt > 0.0) "the conversation scrolls, so there was a scroll to take away"
                Expect.isTrue (renders >= 10) (sprintf "records landed while the scroll ran — %d renders" renders)
                return ()
            }
        // The name on a rule is an INPUT at rest, which is a promise no markup test can
        // settle: a field that renders but never takes a keystroke, or one whose value the
        // next render puts back, reads in the DOM exactly like one that works. So this types
        // into it and waits for what was typed to survive a render — which is the whole
        // round trip, from the field through `EditChapterNameMsg` and the session's own text
        // back to the value the view writes.
        editorCaseIn 1440 900 "what you type on a chapter's rule is what the session calls it" <| fun page ->
            async {
                let! _ = await (page.WaitForSelectorAsync "#shell [data-chapter-name='msg-filler-8']")
                do! awaitU (page.ClickAsync "#shell [data-chapter-name='msg-filler-8']")
                // To the end of whatever the heuristic guessed, so this adds rather than
                // replacing — an edit against the text the session holds is what the field is
                // for, and appending is the edit most likely to expose a diff computed against
                // the wrong side.
                do! awaitU (page.Keyboard.PressAsync "End")
                do! awaitU (page.Keyboard.TypeAsync " — settled")
                let! kept =
                    await (page.WaitForFunctionAsync
                        """document.querySelector("#shell [data-chapter-name='msg-filler-8']")
                             ?.value.endsWith(' — settled') === true""")
                Expect.isNotNull kept "what was typed is what the rule says, after the render that followed it"
                return ()
            }
        // A collaborator's caret in that same name. Nothing in the markup can settle where a
        // marker LANDS: it is absolutely positioned by measurement after the render, so a
        // marker placed against the wrong box, or against a stylesheet's idea of the field,
        // renders exactly the same string as one placed right — and lands on the message
        // below, or on the dot, or nowhere at all.
        //
        // What is pinned is the promise rather than the pixels: the caret is inside the name
        // it is in, at a non-zero height, and further right for a later index than an earlier
        // one. A reference image would fail on a font tweak, which is the coupling this tier
        // exists to avoid.
        editorCaseIn 1440 900 "a collaborator's caret in a chapter's name stands in that name" <| fun page ->
            async {
                let! _ = await (page.WaitForSelectorAsync "#shell [data-chapter-name='msg-filler-8']")
                do! awaitU (page.EvaluateAsync "() => window.__chapterCaret('msg-filler-8', 3, 3)")
                // Waited for by EXISTENCE, not visibility: a bare caret is a zero-width
                // highlight with the caret bar inside it, which every "is it visible" check
                // in a driver calls hidden.
                let! _ =
                    await (page.WaitForFunctionAsync
                            """!!document.querySelector("#shell [data-chapter-rule='msg-filler-8'] [data-cursor-peer='peer:brave-owl']")""")
                let! inside =
                    await (page.EvaluateAsync<bool>
                            """() => {
                                 const name = document.querySelector("#shell [data-chapter-name='msg-filler-8']")
                                 const mark = document.querySelector("#shell [data-chapter-rule='msg-filler-8'] [data-cursor-peer='peer:brave-owl']")
                                 const n = name.getBoundingClientRect(), m = mark.getBoundingClientRect()
                                 return m.height > 0
                                     && m.top >= n.top - 1 && m.bottom <= n.bottom + 1
                                     && m.left >= n.left - 1 && m.left <= n.right + 1
                               }""")
                Expect.isTrue inside "the caret is drawn inside the field it is a caret in"

                // The same caret further along the name is further along the SCREEN. This is
                // what says the offset is being measured rather than the marker parked at the
                // start of the field, which every check above would pass.
                let! at3 =
                    await (page.EvaluateAsync<float>
                            """() => document.querySelector("#shell [data-chapter-rule='msg-filler-8'] [data-cursor-peer='peer:brave-owl']").getBoundingClientRect().left""")
                do! awaitU (page.EvaluateAsync "() => window.__chapterCaret('msg-filler-8', 9, 9)")
                let! moved =
                    await (page.WaitForFunctionAsync
                            (sprintf
                                """document.querySelector("#shell [data-chapter-rule='msg-filler-8'] [data-cursor-peer='peer:brave-owl']").getBoundingClientRect().left > %f"""
                                at3))
                Expect.isNotNull moved "a caret later in the name is drawn further into it"
                return ()
            }
        // Where a peer is can now be as long as a chapter's name — up to the heuristic's own
        // 48 characters, where "renaming" and "in build" were a word or two. The roster row is
        // a name and a right-aligned slot, and the slot never shrank, so a long one ran off
        // the side of the sidebar taking the peer's name with it. Markup cannot see that: the
        // words are all present and correct in a string, and only a laid-out column knows they
        // did not fit in it.
        editorCaseIn 1440 900 "where a peer is never runs off the side of the roster" <| fun page ->
            async {
                let! _ = await (page.WaitForSelectorAsync "#shell [data-chapter-name='msg-filler-8']")
                // The chapter whose name nobody has written, so what the roster says is the
                // heuristic's guess at a whole line of somebody's message — the longest thing
                // this slot can be asked to hold.
                do! awaitU (page.EvaluateAsync "() => window.__chapterCaret('msg-filler-8', 0, 0)")
                let! _ =
                    await (page.WaitForFunctionAsync
                            """!!document.querySelector("#shell [data-peer-presence='peer:brave-owl'] [data-peer-at]")""")
                let! fits =
                    await (page.EvaluateAsync<bool>
                            """() => {
                                 const row = document.querySelector("#shell [data-peer-presence='peer:brave-owl']")
                                 const at = row.querySelector('[data-peer-at]')
                                 return at.getBoundingClientRect().right <= row.getBoundingClientRect().right + 1
                               }""")
                Expect.isTrue fits "the words stop inside the row rather than running past it"
                return ()
            }
        // The ground a message stands on, and the three promises it makes that only a laid-out
        // page can settle. Every cheap tier reads markup, and markup with a control overlapping
        // its own text reads exactly like markup where it does not.
        //
        // First: the control has a BERTH. It is absolutely positioned over the item, so
        // nothing in the flow knows it is there — without a reserved strip the last words of a
        // wrapping line run underneath the dots, which is what shipped until this case existed.
        editorCaseIn 1440 900 "an item's actions never sit on the words" <| fun page ->
            async {
                let! _ = await (page.WaitForSelectorAsync "#shell [data-conversation] [data-item-actions]")
                do! awaitU (page.HoverAsync "#shell [data-conversation] [data-message-id='msg-filler-8']")
                let! clear =
                    await (page.EvaluateAsync<bool>
                            """() => {
                                 const item = document.querySelector("#shell [data-conversation] [data-message-id='msg-filler-8']")
                                 const dots = item.querySelector('[data-item-actions]').getBoundingClientRect()
                                 const body = item.querySelector('[data-message-body]').getBoundingClientRect()
                                 return dots.left >= body.right
                               }""")
                Expect.isTrue clear "the words stop before the actions control begins"
                return ()
            }
        // Second: on a phone the ground is FULL BLEED. A 390px screen has no margin to spend
        // on making a surface look like a card, and a ground inset from both edges of a narrow
        // screen reads as a card rather than as the row it is.
        editorCaseIn 390 844 "a message's ground reaches both edges of a phone screen" <| fun page ->
            async {
                let! _ = await (page.WaitForSelectorAsync "#shell [data-conversation] [data-message-id]")
                let! spare =
                    await (page.EvaluateAsync<float> groundSpare)
                Expect.isTrue
                    (abs spare < 1.0)
                    (sprintf "the ground stops %fpx short of the screen" spare)
                return ()
            }
        // Third: on a desktop it does NOT. The same measurement the other way round — what
        // bounds a message here is the reading column, and a ground that ran the width of a
        // 1440px window would be announcing a line of text nobody could read back.
        editorCaseIn 1440 900 "a message's ground stops at the reading column, not the window" <| fun page ->
            async {
                let! _ = await (page.WaitForSelectorAsync "#shell [data-conversation] [data-message-id]")
                let! spare = await (page.EvaluateAsync<float> groundSpare)
                Expect.isTrue
                    (spare > 100.0)
                    (sprintf "the ground runs to within %fpx of a 1440px window" spare)
                return ()
            }
        // Dismissing a menu with the keyboard is where focus goes to `body` if nobody puts it
        // back — the failure the WCAG floor names, hit on the very first Escape, and one no
        // rendered string can see: the markup after a close is identical whether the cursor
        // landed on the control or nowhere at all.
        editorCaseIn 1440 900 "Escape closes an item's menu and hands focus back to what opened it" <| fun page ->
            async {
                let! _ = await (page.WaitForSelectorAsync "#shell [data-conversation] [data-item-actions]")
                do! awaitU (page.ClickAsync "#shell [data-conversation] [data-item-actions]")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-item-menu]")

                // INTO the menu first. A press leaves focus on the control itself, so Escape
                // from there has nowhere to strand it and the case passes with nothing
                // putting focus back — which is what the first version of this did. The
                // hazard is the entry being removed out from under the cursor.
                do! awaitU (page.Keyboard.PressAsync "Tab")
                let! _ =
                    await (page.WaitForFunctionAsync
                        """document.activeElement?.hasAttribute('data-item-chapter') === true""")

                do! awaitU (page.Keyboard.PressAsync "Escape")
                let! _ = await (page.WaitForFunctionAsync """!document.querySelector('#shell [data-item-menu]')""")
                let! _ =
                    await (page.WaitForFunctionAsync
                        """document.activeElement?.hasAttribute('data-item-actions') === true""")
                return ()
            }
        // The other door out of the menu, and it strands focus the same way: choosing removes
        // the entry that was pressed. Escape is not this case — a menu can be left by either,
        // and only one of them was putting the cursor back.
        editorCaseIn 1440 900 "choosing from an item's menu hands focus back to what opened it" <| fun page ->
            async {
                let! _ = await (page.WaitForSelectorAsync "#shell [data-conversation] [data-item-actions]")
                do! awaitU (page.ClickAsync "#shell [data-conversation] [data-item-actions]")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-item-chapter]")

                do! awaitU (page.Keyboard.PressAsync "Tab")
                let! _ =
                    await (page.WaitForFunctionAsync
                        """document.activeElement?.hasAttribute('data-item-chapter') === true""")
                do! awaitU (page.Keyboard.PressAsync "Enter")

                let! _ = await (page.WaitForFunctionAsync """!document.querySelector('#shell [data-item-menu]')""")
                let! _ =
                    await (page.WaitForFunctionAsync
                        """document.activeElement?.hasAttribute('data-item-actions') === true""")
                return ()
            }
        // A control revealed by hover is a control a keyboard cannot find unless focus reveals
        // it too, and `opacity-0` keeps it in the tab order either way — so the failure is not
        // an unreachable control but an INVISIBLE one that is nonetheless the focused thing.
        // Tabbed to for real, because `:focus-visible` is exactly the rule that does not fire
        // for a programmatic `focus()`.
        editorCaseIn 1440 900 "an item's actions show themselves to a keyboard that reaches them" <| fun page ->
            async {
                let! _ = await (page.WaitForSelectorAsync "#shell [data-conversation] [data-item-actions]")
                let mutable reached = false
                let mutable steps = 0
                while not reached && steps < 60 do
                    do! awaitU (page.Keyboard.PressAsync "Tab")
                    steps <- steps + 1
                    let! here =
                        await (page.EvaluateAsync<bool>
                                "() => document.activeElement?.hasAttribute('data-item-actions') === true")
                    reached <- here
                Expect.isTrue reached (sprintf "a keyboard reaches an item's actions (gave up after %d tabs)" steps)

                // WAITED for, never read once: the control fades in, and a computed opacity
                // sampled on the frame focus landed is the value the transition STARTED from.
                // Read that way this passed nothing and failed everything — the first version
                // of this case reported `0` against a control that was on its way to 1.
                let! _ =
                    await (page.WaitForFunctionAsync
                        """getComputedStyle(document.querySelector('#shell [data-item-actions]:focus-visible'))
                             ?.opacity === '1'""")
                return ()
            }
        // A turn in flight, on the screen that had the least room for it. This is the phone
        // photographed in the report that started this: an author line, the word `streaming`
        // under it, an empty body, and a 48px band below saying "agent is responding" a third
        // time — one fact, three animated marks.
        //
        // Only a browser can settle it. Every cheap tier reads markup, and markup with a
        // visually-hidden sentence in it looks exactly like markup that says the thing twice:
        // what separates them is whether the pixels are painted. So the count here is of marks
        // a person can SEE — hit-tested at their own centre, never `offsetParent` (null for
        // anything fixed) and never a non-zero rect (a clipped element keeps one).
        editorCaseIn 390 844 "a turn in flight is stated on the screen exactly once" <| fun page ->
            async {
                // Measured with motion turned off, which is a real setting rather than a trick:
                // a mark that ANIMATES is on the screen at one opacity or another depending on
                // the frame the camera caught, and "how many marks are there" is only a
                // well-posed question once nothing is mid-cycle. `motion-reduce:animate-none`
                // is what every mark here already carries.
                do! awaitU (page.EmulateMediaAsync (PageEmulateMediaOptions (ReducedMotion = ReducedMotion.Reduce)))
                let! _ = await (page.WaitForSelectorAsync "#shell [data-draft-editor]")
                do! awaitU (page.EvaluateAsync "() => window.__agentTurn()")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-agent-writing]")
                // The shell into the viewport, then the timeline to its end — where a turn that
                // has just started is. Both, because this harness page stacks its mounts and
                // the shell is not the top of it: a mark below the fold is invisible for a
                // reason this case is not about, and would fail it while the design was right.
                // (Measured: without the first scroll the caret sits at y=1433 on an 844px
                // screen, and every hit-test answers null.)
                let! _ =
                    await (page.EvaluateAsync<bool>
                            """() => { document.querySelector('#shell').scrollIntoView()
                                       const t = document.querySelector('#shell [data-conversation]')
                                       t.scrollTop = t.scrollHeight
                                       return t.scrollTop > 0 }""")
                let! marks =
                    await (page.EvaluateAsync<int>
                            """() => [...document.querySelectorAll('#shell [data-agent-writing]')].filter(m => {
                                 const b = m.getBoundingClientRect()
                                 if (b.width === 0 || b.height === 0) return false
                                 const at = document.elementFromPoint(b.left + b.width / 2, b.top + b.height / 2)
                                 return at !== null && m.contains(at)
                               }).length""")
                Expect.equal marks 1 "the agent's turn is marked once on the screen"

                // The other half of "once": the sentence a screen reader is told must not also
                // be a thing anybody sees, or the count above is 1 and the screen still says it
                // twice. A visually-hidden region occupies no readable area — which is what
                // `sr-only` MEANS, and the one property every correct spelling of it shares.
                let! shown =
                    await (page.EvaluateAsync<float>
                            """() => { const r = document.querySelector('#shell [data-agent-stream]').getBoundingClientRect()
                                       return r.width * r.height }""")
                Expect.isTrue (shown < 25.0) (sprintf "the spoken sentence is not painted for anyone (%f px²)" shown)
                return ()
            }
        // What the band it replaced could not take away, and what a control arriving in a band
        // can: the composer. A turn starting must not push what a person types with off the
        // screen, or the one thing to do while the agent writes — queue the next message — is
        // gone exactly when it is wanted.
        editorCaseIn 390 844 "a turn starting never costs the composer its line or its send" <| fun page ->
            async {
                let! _ = await (page.WaitForSelectorAsync "#shell [data-draft-editor]")
                do! awaitU (page.EvaluateAsync "() => window.__agentTurn()")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-interrupt-turn]")
                let! whole =
                    await (page.EvaluateAsync<bool>
                            """() => {
                                 const inside = sel => {
                                   const el = document.querySelector('#shell ' + sel)
                                   if (!el) return false
                                   const b = el.getBoundingClientRect()
                                   return b.width > 0 && b.height > 0 && b.left >= 0 && b.right <= window.innerWidth
                                 }
                                 return inside('[data-draft-input]') && inside('[data-send-draft]') && inside('[data-interrupt-turn]')
                               }""")
                Expect.isTrue whole "the line, its send, and the new stop control all fit the phone at once"
                return ()
            }
        // The composer's promise is that it gives the conversation back the room it is not
        // using, and on a phone the verbs are the room: they leave the line and sit below it,
        // standing only once there is a draft for them to act on. `opacity-0` kept every
        // pixel of that row while hiding it — 44px of invisible buttons, plus the clearance
        // meant to land under them — so two thirds of an empty composer was band holding
        // nothing.
        //
        // Only a browser can tell that from a composer that is simply padded: the markup is
        // identical either way, and so is the row's own bounding box (clipping does not
        // resize a child). What separates them is what stands between the line and the
        // bottom of the band, which is a measurement.
        editorCaseIn 390 844 "a composer with nothing to send spends no height on its verbs" <| fun page ->
            async {
                // Measured with motion off for the reason every geometry case here is: a
                // `max-height` mid-transition is neither of the two heights being compared.
                do! awaitU (page.EmulateMediaAsync (PageEmulateMediaOptions (ReducedMotion = ReducedMotion.Reduce)))
                let line = """#shell [data-draft-input] .ProseMirror"""
                let! _ = await (page.WaitForSelectorAsync line)
                // What is below the line, in both states. The band's bottom rather than the
                // row's own box: a clipped child keeps its rect, so the row cannot be asked
                // whether it is taking room — only the band it is inside can.
                let below =
                    """() => {
                         const band = document.querySelector('#shell [data-draft-editor]')
                         const line = document.querySelector('#shell [data-draft-input]')
                         return band.getBoundingClientRect().bottom - line.getBoundingClientRect().bottom
                       }"""
                let! empty = await (page.EvaluateAsync<float> below)
                do! awaitU (page.ClickAsync line)
                do! awaitU (page.Keyboard.TypeAsync "something to send")
                // Clear exists exactly while the draft does, so its arrival is the rule
                // having settled — the one signal here that is not a guess at a duration.
                let! _ = await (page.WaitForSelectorAsync "#shell [data-discard-draft]")
                let! drafted = await (page.EvaluateAsync<float> below)
                Expect.isTrue
                    (empty < drafted && empty <= 24.0)
                    (sprintf "an empty composer ends under its line (%fpx below it, %fpx with a draft)" empty drafted)
                return ()
            }
        // THE bug, and the reason the row above follows the draft rather than the composer's
        // focus. `focus-within` is false the moment focus leaves the composer, and pressing a
        // button is how focus leaves: where a button takes no focus from a tap — iOS Safari,
        // and any press the editor's blur beats — the row went `pointer-events-none` under
        // the finger and the press landed on the timeline behind it. What a person saw was
        // Send doing nothing and the composer collapsing.
        //
        // No markup test can see this: the button is rendered, it has a hook, it has a
        // non-zero box, and its `@click` is bound. What decides it is which element answers
        // at the point the finger is on, once focus has gone — and only a browser can be
        // asked that.
        editorCaseIn 390 844 "the composer's verbs survive the press that reaches for them" <| fun page ->
            async {
                do! awaitU (page.EmulateMediaAsync (PageEmulateMediaOptions (ReducedMotion = ReducedMotion.Reduce)))
                let line = """#shell [data-draft-input] .ProseMirror"""
                let! _ = await (page.WaitForSelectorAsync line)
                do! awaitU (page.ClickAsync line)
                do! awaitU (page.Keyboard.TypeAsync "something to send")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-discard-draft]")
                // The shell into the viewport first: this harness page stacks its mounts and
                // the shell is not the top of it, so the composer sits below the fold and a
                // hit-test in VIEWPORT coordinates answers null for a reason this case is
                // not about. (Measured: without it, `elementFromPoint` said `nothing` while
                // the row was standing exactly where it should.)
                do! awaitU (page.EvaluateAsync "() => document.querySelector('#shell').scrollIntoView()")
                // The blur IS the gesture under test: it is what a tap on a button does
                // first, on every browser that does not focus one.
                do! awaitU (page.EvaluateAsync "() => document.activeElement.blur()")
                let! answered =
                    await (page.EvaluateAsync<string>
                            """() => {
                                 const send = document.querySelector('#shell [data-send-draft]')
                                 const b = send.getBoundingClientRect()
                                 const at = document.elementFromPoint(b.left + b.width / 2, b.top + b.height / 2)
                                 if (at === null) return 'nothing'
                                 return send.contains(at) ? 'send' : at.tagName + '.' + (at.getAttribute('class') ?? '')
                               }""")
                Expect.equal answered "send" "a press at Send's own centre reaches Send once the editor has let focus go"
                return ()
            }
        // A collapsed composer holding a long draft used to stop dead at its padding: a hard
        // cut through the second line, which reads as a rendering fault rather than as text
        // that carries on. It hints instead — a little of the next line, faded out — and the
        // hint is a HEIGHT, so it is a thing a browser can settle and no markup test can.
        //
        // Both directions, because each alone has a wrong way to pass: a composer that never
        // grew would satisfy "still collapsed", and one that simply opened would satisfy
        // "shows there is more".
        editorCaseIn 390 844 "a collapsed composer hints at the draft it cannot fit, without opening" <| fun page ->
            async {
                do! awaitU (page.EmulateMediaAsync (PageEmulateMediaOptions (ReducedMotion = ReducedMotion.Reduce)))
                let line = """#shell [data-draft-input] .ProseMirror"""
                let! _ = await (page.WaitForSelectorAsync line)
                let height =
                    """() => document.querySelector('#shell [data-draft-input]').getBoundingClientRect().height"""
                let! oneLine = await (page.EvaluateAsync<float> height)
                do! awaitU (page.ClickAsync line)
                // Three paragraphs, typed with real keys: what a person writes when they say
                // more than fits, and more than the open composer's own cap so the two states
                // are genuinely different heights.
                for word in [ "first"; "second"; "third"; "fourth" ] do
                    do! awaitU (page.Keyboard.TypeAsync word)
                    do! awaitU (page.Keyboard.PressAsync "Enter")
                let! opened = await (page.EvaluateAsync<float> height)
                do! awaitU (page.EvaluateAsync "() => document.activeElement.blur()")
                let! collapsed = await (page.EvaluateAsync<float> height)
                Expect.isTrue
                    (collapsed > oneLine && collapsed < opened)
                    (sprintf "collapsed over a long draft: %fpx (one line %fpx, open %fpx)" collapsed oneLine opened)
                return ()
            }
        // The DVR (Plan 14, stage 7). What only a browser can answer: that rewinding a LIVE
        // terminal really mounts a player over what it has recorded so far — the same player
        // and the same cast a finished terminal's replay uses, which is what "rewound like
        // live TV, through the same mechanism" has to mean — that it lands ON the pinned
        // edge rather than at the recording's start, that focus survives the control swap,
        // and that playing off the pinned end catches the reader back up to live by itself.
        editorCase "a live terminal rewinds to its pinned edge, and playing off it catches back up" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-terminal-toggle='show']")
                do! awaitU (page.ClickAsync "#shell [data-terminal-tab='term-live']")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-terminal-screen='term-live']")

                // Watching replaces the live screen with the recording, in a real player.
                do! awaitU (page.ClickAsync "#shell [data-terminal-watch='watch']")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-pane-replay='terminal:term-live'] .ap-player")
                let! _ = await (page.WaitForFunctionAsync """!document.querySelector("#shell [data-terminal-screen='term-live']")""")

                // One control in one slot, relabelled — so the press KEEPS its own focus.
                // There used to be four controls swapping each other out of the document, and
                // every press had to hand focus on after itself or strand it on `body`.
                let! _ =
                    await (page.WaitForFunctionAsync
                        """document.activeElement?.getAttribute('data-terminal-watch') === 'live'""")

                // It lands AT the pinned edge: the poster is the screen as it stood at the
                // pin, shown before anyone presses play — not a blank player parked at 0:00.
                let! _ =
                    await (page.WaitForFunctionAsync
                        """document.querySelector("#shell [data-pane-replay='terminal:term-live']")?.textContent.includes('earlier output') === true""")

                // Playing off the pinned end IS catching up: the player's `ended` drops the
                // rewind by itself. Nobody pressed anything, so this is the one case that
                // still hands focus on — the player being read is unmounted under the reader.
                do! awaitU (page.ClickAsync "#shell [data-pane-replay='terminal:term-live'] .ap-overlay-start")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-terminal-screen='term-live']")
                let! _ = await (page.WaitForFunctionAsync """!document.querySelector("#shell [data-pane-replay='terminal:term-live']")""")
                let! _ =
                    await (page.WaitForFunctionAsync
                        """document.activeElement?.getAttribute('data-terminal-watch') === 'watch'""")

                // And by hand too: watch again, live again — the same control both times.
                do! awaitU (page.ClickAsync "#shell [data-terminal-watch='watch']")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-pane-replay='terminal:term-live'] .ap-player")
                do! awaitU (page.ClickAsync "#shell [data-terminal-watch='live']")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-terminal-screen='term-live']")
                let! _ = await (page.WaitForFunctionAsync """!document.querySelector("#shell [data-pane-replay='terminal:term-live']")""")
                return ()
            }

        // "Show in terminal" (Plan 25, stage 3). The reader's context question, answered with
        // text: the terminal's own history, scrolled to the command they came from. Only a
        // browser can say whether it actually SCROLLED — and whether that scroll survives the
        // render which returns a freshly rendered scrollback to its end, the one thing this
        // could quietly lose to.
        editorCase "showing a command in its terminal scrolls the history to it" <| fun page ->
            async {
                let! _ = await (page.WaitForSelectorAsync "#shell [data-chat-block]")
                do! awaitU (page.ClickAsync "#shell [data-chat-block]")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-pane-show-in-terminal]")
                do! awaitU (page.ClickAsync "#shell [data-pane-show-in-terminal]")

                // The pane moved to the terminal's own text.
                let! _ =
                    await (page.WaitForFunctionAsync
                        """document.querySelector('#shell [data-pane-panel]')?.getAttribute('data-pane-panel')?.startsWith('terminal:') === true""")

                // …and the command is inside the scroller's viewport rather than somewhere
                // off it. A position promise, measured off the real boxes — not a style, and
                // not a claim about which pixel it landed on.
                let! _ =
                    await (page.WaitForFunctionAsync
                        """(() => {
                             const scroller = document.querySelector('#shell [data-terminal-scrollback]')
                             const block = scroller && scroller.querySelector('[data-terminal-block=block-harness]')
                             if (!block) return false
                             const a = scroller.getBoundingClientRect(), b = block.getBoundingClientRect()
                             return b.top >= a.top - 1 && b.top < a.bottom
                           })()""")

                // Focus followed into the pane, as it does for a chip: the control that was
                // pressed left the document with the tab it was in.
                let! _ = await (page.WaitForFunctionAsync """document.activeElement?.hasAttribute('data-pane-panel') === true""")
                return ()
            }

        // A command the agent has queued, in the chat. Two promises here that no rendered
        // string can settle, and the first is the reason this chip exists in the shape it
        // does: what it is about to run comes out of the DOC — a `Y.Text` root every peer may
        // edit until it drains — so the markup carries an empty element and only a browser
        // that has bound it says the command. The second is the tap: the chip is the read,
        // the terminal's own card is where it is answered, and a read that could not reach
        // the answer would be the half of this that quietly does not work.
        editorCase "a queued command says what it will run, and opens the terminal it waits in" <| fun page ->
            async {
                let! _ = await (page.WaitForSelectorAsync "#shell [data-chat-pending]")
                let! _ =
                    await (page.WaitForFunctionAsync
                        """document.querySelector('#shell [data-chat-pending]').textContent.includes('npm run lint')""")

                do! awaitU (page.ClickAsync "#shell [data-chat-pending]")

                // The pane is on the terminal it is queued in — not some terminal, that one.
                let! _ =
                    await (page.WaitForFunctionAsync
                        """document.querySelector('#shell [data-pane-panel]')?.getAttribute('data-pane-panel') === 'terminal:term-harness'""")
                // Focus followed it in, as it does for a block's chip: the reader was moved,
                // so their keyboard was too.
                let! _ = await (page.WaitForFunctionAsync """document.activeElement?.hasAttribute('data-pane-panel') === true""")
                // And what they arrived at is the card that can answer it.
                let! _ = await (page.WaitForSelectorAsync "#shell [data-terminal-queued='queue-harness']")
                return ()
            }

        // Pins (Plan 20, stage 1). The pin's STATE is a rendered attribute the cheap tier can
        // read; what needs a browser is the keyboard release — Delete on a focused tab
        // removes that tab from the document, and focus has to land on what took its place
        // rather than on `body`. Same floor the DVR's control swap answers, in the surface a
        // keyboard user actually walks.
        editorCase "a tab is kept by its pin and released from the keyboard, without stranding focus" <| fun page ->
            async {
                // A chip's tab arrives previewed — kept by nothing — and selected, since
                // tapping the chip is what put it there.
                do! awaitU (page.ClickAsync "#shell [data-chat-block]")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-pane-tab^='block:']")
                let! _ =
                    await (page.WaitForFunctionAsync
                        """document.querySelector("#shell [data-pane-tab^='block:']")?.getAttribute('data-pane-tab-pinned') === 'false'""")

                // Activating the tab you are already on is the pin. There is no second
                // control to hit — which is the point on a touch screen, where the second
                // control was a 24px target beside a 30px one.
                do! awaitU (page.ClickAsync "#shell [data-pane-tab^='block:']")
                let! _ =
                    await (page.WaitForFunctionAsync
                        """document.querySelector("#shell [data-pane-tab^='block:']")?.getAttribute('data-pane-tab-pinned') === 'true'""")
                // And it says so where anything that cannot see a blue glyph can read it.
                let! _ =
                    await (page.WaitForFunctionAsync
                        """document.querySelector("#shell [data-pane-tab^='block:'] [role='img']")?.getAttribute('aria-label') === 'pinned'""")

                // Move on to something else. The pinned tab stays, which is what a pin is
                // for — a preview would have been replaced here.
                do! awaitU (page.ClickAsync "#shell [data-terminal-tab='term-harness']")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-pane-tab^='block:']")

                // Delete on the focused tab releases it. The tab leaves the strip, and focus
                // lands on whatever took its position — never nowhere.
                do! awaitU (page.FocusAsync "#shell [data-pane-tab^='block:']")
                do! awaitU (page.Keyboard.PressAsync "Delete")
                let! _ =
                    await (page.WaitForFunctionAsync
                        """document.querySelectorAll("#shell [data-pane-tab^='block:']").length === 0""")
                let! _ =
                    await (page.WaitForFunctionAsync
                        """document.activeElement?.closest('[role="tab"]') !== null""")
                return ()
            }

        // The terminal list (Plan 20, stage 0). WHICH verbs a row offers is a fold the cheap
        // tier already pins; what only a browser can answer is the DOM swap — the list
        // replaces the strip and the pane's body at once, so choosing a row removes the
        // control that was pressed, and focus has to land on what replaced it rather than
        // on `body`. That is the WCAG floor, not a nicety.
        editorCase "the list opens a terminal and hands focus to the pane it replaced itself with" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-terminal-toggle='show']")
                do! awaitU (page.ClickAsync "#shell [data-terminal-list-toggle='list']")

                // One surface at a time: the tablist promises a panel showing one of its
                // tabs, and it must not be left standing over a list that replaced it.
                let! _ = await (page.WaitForSelectorAsync "#shell [data-terminal-list]")
                let! _ = await (page.WaitForFunctionAsync """!document.querySelector("#shell [role='tablist']")""")

                // Every terminal the session has is reachable here, whether or not the strip
                // would have carried it.
                let! rows = await (page.EvaluateAsync<int> "() => document.querySelectorAll('#shell [data-terminal-list-row]').length")
                Expect.equal rows 3 "every terminal the harness has, the closed one included"

                // Choosing a row shows that terminal AND leaves the list — one act — so the
                // row that was pressed is gone from the document by the time focus moves.
                // Driven from the KEYBOARD, because that is the half of this a click cannot
                // answer: a row has to be a real control somebody can reach and press
                // without a pointer, and the focus move afterwards is what the floor asks
                // for when the pressed control leaves the document.
                do! awaitU (page.FocusAsync "#shell [data-terminal-list-row='term-live']")
                do! awaitU (page.Keyboard.PressAsync "Enter")
                let! _ = await (page.WaitForFunctionAsync """!document.querySelector('#shell [data-terminal-list]')""")
                let! _ =
                    await (page.WaitForFunctionAsync
                        """document.querySelector('#shell [data-pane-panel]')?.getAttribute('data-pane-panel') === 'terminal:term-live'""")
                let! _ = await (page.WaitForFunctionAsync """document.activeElement?.hasAttribute('data-pane-panel') === true""")
                return ()
            }

        // Task cards (Plan 20, stage 4). WHICH commands group, in what order, and what the
        // summary counts are all folds the cheap tier pins. What only a browser can answer is
        // that grouping does not cost a person a control: a burst's commands are behind a
        // disclosure now, and a line inside it has to be the same reachable, pressable thing
        // the chip was before it was grouped. Nothing here asserts the card's layout — that
        // is the design, and the design is what a card is FOR.
        editorCase "a task card's lines stay real controls, reachable and pressable without a pointer" <| fun page ->
            async {
                // A real `<details>`, so the disclosure is the browser's: keyboard-operable
                // and announced without a handler or an ARIA role of our own.
                let! _ = await (page.WaitForSelectorAsync "#shell [data-chat-task-card]")
                do! awaitU (page.FocusAsync "#shell [data-chat-task-card] summary")
                do! awaitU (page.Keyboard.PressAsync "Enter")
                let! _ =
                    await (page.WaitForFunctionAsync
                        """document.querySelector('#shell [data-chat-task-card]')?.open === true""")

                // The failed command leads, which is the one thing the ordering promises —
                // and it is a BUTTON, not a div someone hung a click on.
                let! first =
                    await (page.EvaluateAsync<string>
                        """() => {
                             const line = document.querySelector('#shell [data-chat-task-card] [data-chat-block]')
                             return line.tagName + ':' + line.getAttribute('data-chat-block')
                           }""")
                Expect.equal first "BUTTON:block-burst-failed" "the failure leads, as a real button"

                // And pressing it does what an ungrouped chip does: opens that block.
                do! awaitU (page.Keyboard.PressAsync "Tab")
                do! awaitU (page.Keyboard.PressAsync "Enter")
                let! _ =
                    await (page.WaitForFunctionAsync
                        """document.querySelector('#shell [data-pane-panel]')?.getAttribute('data-pane-panel') === 'block:term-harness:block-burst-failed'""")
                return ()
            }

        // Guardrail for the whole `Style.fieldType` family (Plan: `fieldSelect` zoomed the
        // page in on iOS Safari the moment the model picker was tapped, and never zoomed back
        // out — Safari zooms in on ANY focused text control under 16px. `touchType` is folded
        // into `fieldType` now precisely so nothing built on it can forget it again, but that
        // fold only covers ONE family; the mono/message fields (`fieldMonoBare`, the terminal
        // command line, a queued command, a chapter's name) each hand-spell their own font
        // class and still have to append `touchType` themselves. This is the net under both:
        // whatever a person can focus on a phone, at whatever class got there, has to compute
        // to 16px or more, or this fails HERE rather than on somebody's phone.
        editorCaseIn 390 844 "no field a phone can focus renders under 16px" <| fun page ->
            async {
                let! _ = await (page.WaitForSelectorAsync "#shell")
                // The model picker lives behind settings, and settings lives behind the
                // sidebar — both off-canvas on a phone until `nav-alt`/`settings-open` land
                // on <html> (Style.fs: "Two presentation bits live on the root <html>
                // element, outside `#app`... toggled by `[data-nav-toggle]`"/`[data-settings-
                // toggle]`"). This harness mounts `View.view` over a fixed model with no
                // Session Process behind it (`ToggleNav`/`ToggleSettings` are `ignore` here,
                // deliberately — see `EditorHarness.fs`), so the buttons that ask for those
                // classes in the real client do nothing here. Setting them directly is
                // asking the same question `Browser.fs`'s handlers answer by setting them:
                // whether the settings face, once ON screen, holds a field under 16px.
                do! awaitU (page.EvaluateAsync "() => document.documentElement.classList.add('nav-alt', 'settings-open')")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-model-select]")

                let! undersized =
                    await (page.EvaluateAsync<string[]>
                        """() => {
                             const els = document.querySelectorAll(
                               "#shell input, #shell select, #shell textarea, #shell [contenteditable='true']")
                             const small = []
                             els.forEach(el => {
                               const box = el.getBoundingClientRect()
                               if (box.width === 0 && box.height === 0) return   // not on screen
                               const size = parseFloat(getComputedStyle(el).fontSize)
                               if (size < 16) {
                                 const name = el.id || Object.values(el.attributes)
                                   .map(a => a.name).find(n => n.startsWith('data-')) || el.tagName
                                 small.push(name + ': ' + size + 'px')
                               }
                             })
                             return small
                           }""")
                Expect.isEmpty undersized
                    (sprintf "these focusable fields render under 16px on a phone and will zoom iOS in on focus: %s"
                        (String.Join (", ", undersized)))
            }
    ]

// --- A path-mounted session in a real browser --------------------------------------------

/// The session's own loopback port, learned from the readiness line. The PUBLIC address is
/// `/s/<id>` on the proxy and does not contain it — which is the whole point of the shape
/// under test, and why nothing here may pin it.
let mutable private mountSessionPort = 0
let private MOUNT_SESSION = "mounted"
let private mountDataDir = "tests/browser/.data-mounted"

/// The operator's proxy in miniature: whatever arrives at the public port is forwarded to
/// the session's loopback port with the PATH UNCHANGED, so the session sees — and strips —
/// its own `/s/<id>` prefix. That is exactly the contract Plan 10 states, and the reason
/// this test can exist without depending on any proxy's rewriting behaviour.
/// `sessionPort` is a THUNK, read per request rather than captured: a session that is
/// killed and relaunched keeps its public path and gets a new loopback port, and the whole
/// point of path-mounting is that the address does not move when that happens. A proxy that
/// captured the port would forward to a dead one, which is the operator's reconciler bug in
/// miniature.
/// `stalls` names the requests this front door accepts and never answers — a session whose
/// upstream took the connection and went quiet, which is a shape a proxy produces and a
/// killed host does not (that one answers 502 at once). Held until the listener stops.
let private startStallingMountProxy (sessionPort: unit -> int) (stalls: string -> bool) : Serving =
    let served = listenOnLoopback ()
    let listener = served.Listener
    // No auto-redirect: a `Location` must reach the browser untouched, which is the
    // half of the flow that proves the session's redirects resolve against its mount.
    let client = new HttpClient (new HttpClientHandler (AllowAutoRedirect = false, UseCookies = false))
    let copyHeader (reply: HttpResponseMessage) (ctx: HttpListenerContext) (name: string) =
        let values =
            match reply.Headers.TryGetValues name with
            | true, vs -> List.ofSeq vs
            | _ ->
                match reply.Content.Headers.TryGetValues name with
                | true, vs -> List.ofSeq vs
                | _ -> []
        for v in values do ctx.Response.Headers.Add (name, v)
    let rec loop () =
        async {
            match! Async.Catch (listener.GetContextAsync () |> Async.AwaitTask) with
            | Choice1Of2 ctx when stalls ctx.Request.RawUrl -> return! loop ()
            | Choice1Of2 ctx ->
                Async.Start (
                    async {
                        try
                            let target = sprintf "http://127.0.0.1:%d%s" (sessionPort ()) ctx.Request.RawUrl
                            use request = new HttpRequestMessage (HttpMethod ctx.Request.HttpMethod, target)
                            if ctx.Request.HasEntityBody then
                                use buffer = new MemoryStream ()
                                ctx.Request.InputStream.CopyTo buffer
                                let content = new ByteArrayContent (buffer.ToArray ())
                                match ctx.Request.ContentType with
                                | null | "" -> ()
                                | contentType -> content.Headers.TryAddWithoutValidation ("content-type", contentType) |> ignore
                                request.Content <- content
                            match ctx.Request.Headers.["Cookie"] with
                            | null | "" -> ()
                            | cookie -> request.Headers.TryAddWithoutValidation ("cookie", cookie) |> ignore
                            let! reply = client.SendAsync request |> Async.AwaitTask
                            ctx.Response.StatusCode <- int reply.StatusCode
                            copyHeader reply ctx "Location"
                            copyHeader reply ctx "Set-Cookie"
                            copyHeader reply ctx "Cache-Control"
                            match reply.Content.Headers.ContentType with
                            | null -> ()
                            | contentType -> ctx.Response.ContentType <- string contentType
                            let! bytes = reply.Content.ReadAsByteArrayAsync () |> Async.AwaitTask
                            ctx.Response.OutputStream.Write (bytes, 0, bytes.Length)
                        with _ -> ctx.Response.StatusCode <- 502
                        ctx.Response.Close ()
                    })
                return! loop ()
            | Choice2Of2 _ -> ()   // listener stopped
        }
    Async.Start (loop ())
    served

let mutable private mountedHost : Process = null

/// The product entry, told it is fronted: the Manager at `managerOrigin` (which is also the
/// OIDC issuer a session fetches discovery against, so it must resolve HERE), and sessions
/// mounted under a path at `publicOrigin`, the proxy standing in front of it.
///
/// Both addresses are the deployment's, never this host's to choose — which is why they
/// arrive as arguments and why a restart keeps them (`Mounted.Restart`).
let private startMountedHost (publicOrigin: string) (managerOrigin: string) : unit =
    let psi = ProcessStartInfo "node"
    psi.ArgumentList.Add "app/out/Main.js"
    psi.ArgumentList.Add "--auth"
    psi.ArgumentList.Add "localhost"
    psi.ArgumentList.Add "--port"
    psi.ArgumentList.Add (string (Uri(managerOrigin).Port))
    psi.ArgumentList.Add "--default-session"
    psi.ArgumentList.Add MOUNT_SESSION
    psi.ArgumentList.Add "--data-dir"
    psi.ArgumentList.Add mountDataDir
    psi.UseShellExecute <- false
    psi.RedirectStandardOutput <- true
    // The two ADDRESSES stay variables: a session inherits them and parses them the same way,
    // which is the whole reason they are not options.
    psi.EnvironmentVariables.["YESSION_MANAGER_URL"] <- managerOrigin
    psi.EnvironmentVariables.["YESSION_SESSION_URL"] <- publicOrigin + "/s/{id}"
    let p = new Process (StartInfo = psi)
    let ready = TaskCompletionSource<bool> ()
    p.OutputDataReceived.Add (fun e ->
        if e.Data <> null && e.Data.Contains "launched at" then
            // The Manager reports the session's LOOPBACK address here, which is what the
            // proxy must forward to; the browser never sees it.
            match urlIn e.Data |> Option.map Uri with
            | Some uri ->
                mountSessionPort <- uri.Port
                ready.TrySetResult true |> ignore
            | None -> ())
    p.Start () |> ignore
    p.BeginOutputReadLine ()
    mountedHost <- p
    if not (ready.Task.Wait 30000) then failwith "mounted host never reported readiness"
    if mountSessionPort = 0 then failwith "the readiness line carried no session port"

/// The mounted deployment a case runs against: a proxy on a public origin of its own, the
/// product entry behind it, and the session's public address — which is all a case ever sees
/// of any of it.
///
/// The Manager's origin is taken once and KEPT across a restart. A restart here is the session
/// moving to a new loopback port behind an address that does not move, which is the whole point
/// of path-mounting; a Manager that moved with it would be testing something else.
type private Mounted =
    { Proxy : Serving
      /// The Manager's own origin — the OIDC issuer, and the page a session is opened from.
      ManagerOrigin : string
      /// The session's PUBLIC address, `/s/<id>` on the proxy. The browser knows no other.
      PublicUrl : string }
    /// Boot the host again on the same two addresses, after a case has killed it.
    member this.Restart () : unit = startMountedHost this.Proxy.Origin this.ManagerOrigin
    member this.Stop () : unit =
        this.Proxy.Stop ()
        try if mountedHost <> null then mountedHost.Kill true with _ -> ()

/// A mounted deployment in a clean data dir, with `stalls` naming the requests its proxy
/// accepts and never answers.
///
/// The proxy comes up FIRST, because the host is told where the deployment answers and a
/// listener that is already bound is an address nothing can take in between.
let private startMounted (stalls: string -> bool) : Mounted =
    if Directory.Exists mountDataDir then Directory.Delete (mountDataDir, true)
    let proxy = startStallingMountProxy (fun () -> mountSessionPort) stalls
    // The one host here that cannot be told `--port 0`: its own origin is what it hands a
    // session as the issuer, so it has to be known one line before it boots. Hence a port taken
    // at `:0` and released, rather than a number somebody chose.
    let managerOrigin = sprintf "http://127.0.0.1:%d" (freeLoopbackPort ())
    startMountedHost proxy.Origin managerOrigin
    { Proxy = proxy
      ManagerOrigin = managerOrigin
      PublicUrl = proxy.At (sprintf "/s/%s/" MOUNT_SESSION) }

// --- Reopening a session that is gone (Plan 20, Plan 22) ---------------------------------
// The arrangement every offline-reopen case shares: a mounted session behind a proxy, a
// browser on it, something done that makes history, the worker confirmed running, the session
// killed, and a reload. Hoisted because it is the SETUP that repeats — what each case then
// asserts about the page that came back is its own, and its red names it alone.

let private saidInTimeline = "still here when the session is not"

let private inTimeline =
    sprintf
        """document.querySelector('[data-conversation]')?.textContent?.includes('%s') === true"""
        saidInTimeline

let private printedInTerminal = "printed-before-the-session-died"

/// Has this client actually KEPT the thing the reload will replay?
///
/// On screen is not kept. The live leg puts a record in the model the moment it arrives, and
/// the SAME record triggers a separate HTTP read that is what writes it to the store
/// (`Client.fs`: `TerminalRecordMsg` / `EventsAvailable` -> fetch -> `cache.Write`), started with
/// `Async.StartImmediate` and awaited by nothing. So there is a window in which the assertion
/// these cases make about the LIVE page is already true and the store behind the reload is
/// still empty — measured at 1 run in 3 on an idle box, and wider on a loaded CI runner, where
/// killing the session inside it left the reload nothing to replay and the case timed out
/// naming no cause. Waiting on the store is the difference between testing this and testing
/// that race, exactly as waiting on the worker's registration is below.
/// The store search `keptIn` is, with the body test spelled out — for a case where "the text
/// is in there somewhere" is not the question being asked.
let private keptWhere (cachePart: string) (bodyTest: string) =
    sprintf
        """(async () => {
             try {
               const names = (await caches.keys()).filter(n => n.includes('%s'))
               for (const n of names) {
                 const c = await caches.open(n)
                 for (const req of await c.keys()) {
                   const r = await c.match(req)
                   if (r) {
                     const body = await r.text()
                     if (%s) return true
                   }
                 }
               }
             } catch (e) { return false }
             return false
           })()"""
        cachePart
        bodyTest

let private keptIn (cachePart: string) (text: string) =
    sprintf
        """(async () => {
             try {
               const names = (await caches.keys()).filter(n => n.includes('%s'))
               for (const n of names) {
                 const c = await caches.open(n)
                 for (const req of await c.keys()) {
                   const r = await c.match(req)
                   if (r && (await r.text()).includes('%s')) return true
                 }
               }
             } catch (e) { return false }
             return false
           })()"""
        cachePart
        text

/// The transcript store holding what the terminal PRINTED — an asciicast `"o"` output record
/// carrying the shell's answer, not the `"i"` input record of the command that caused it.
///
/// The distinction is the precondition. `echo printed-before-the-session-died` is kept as an
/// `"i"` record the instant it is sent — the command line CONTAINS the marker — so a plain search
/// (`keptIn`) is satisfied before the shell has answered, let alone before the answer has been
/// fetched and written. Narrowing to `"o"` excludes that input record and leaves only the shell's
/// real output (verified: the recording holds an `"i"` line for the command and one
/// `[t,"o","printed-before-the-session-died\r\n"]` for its result — no separate echo record).
///
/// This narrowing was necessary but not sufficient: the wait that consumed it never actually ran
/// (see `waitFor` — `WaitForFunctionAsync` does not await an async predicate), so `make` proceeded
/// on the first poll regardless of what the store held, killed the session before the output
/// record was written, and the reload replayed a recording with no output — `[data-terminal-output]`
/// never carried the text and the case timed out naming a wait rather than a cause. Green on an
/// idle box, red on a loaded runner, twice on the release gate.
let private transcriptKept =
    keptWhere
        "/terminals/"
        (sprintf
            """body.split('\n').some(l => l.includes('"o"') && l.includes('%s'))"""
            printedInTerminal)
let private conversationKept = keptIn "/events" saidInTimeline

let private terminalPrinted =
    sprintf
        """[...document.querySelectorAll('[data-terminal-output]')].some(o => o.textContent.includes('%s'))"""
        printedInTerminal

/// `make` runs against a live session and leaves history behind; `check` runs against the
/// page that came back with the session dead.
let private offlineReopen (name: string) (make: IPage -> Async<unit>) (check: IPage -> Async<unit>) =
    testCaseAsync name <|
        async {
            let mounted = startMounted (fun _ -> false)
            let mutable browserToClose : IBrowser option = None
            let mutable playwrightToDispose : IPlaywright option = None
            try
                let publicUrl = mounted.PublicUrl
                let! pw = await (Playwright.CreateAsync ())
                playwrightToDispose <- Some pw
                let! br =
                    await (pw.Chromium.LaunchAsync (
                        BrowserTypeLaunchOptions (
                            ExecutablePath = chromiumPath (),
                            Args = [| "--disable-features=WebRtcHideLocalIpsWithMdns" |])))
                browserToClose <- Some br
                let! context = await (br.NewContextAsync ())
                let! page = await (context.NewPageAsync ())
                page.SetDefaultTimeout 20000.0f
                let evidence = watching page
                do! reporting name page evidence <| async {
                let! _ = await (page.GotoAsync publicUrl)
                let! _ = await (page.WaitForFunctionAsync connected)

                do! make page

                // The worker has to be RUNNING before the session goes, or the reload has
                // nothing serving it. Waiting on the registration is the difference between
                // testing this and testing a race — but only through `waitFor`, which awaits: a
                // bare `WaitForFunctionAsync` of this `.then`-chained Promise settles at once (see
                // `waitFor`), so it was not waiting on the worker at all. `getRegistration`
                // resolves promptly each poll (unlike `.ready`, which never settles with no active
                // worker and would hang a single evaluation), and the loop supplies the timeout.
                do! waitFor "the service worker to be active" page
                        "navigator.serviceWorker.getRegistration().then(r => !!(r && r.active))"

                // Gone, and staying gone. The proxy stays up, which is the honest shape of a
                // reaped session behind an operator's front door — and it means the shell
                // request comes back 502 rather than failing outright, which the worker has
                // to treat as the failure it is.
                mountedHost.Kill true
                mountedHost.WaitForExit ()

                let! _ = await (page.ReloadAsync ())

                // The page loaded at all — the whole of what the worker adds, and the
                // precondition every case here is really about rather than the thing it
                // asserts.
                let! _ = await (page.WaitForSelectorAsync "[data-conversation]")

                do! check page
                }
            finally
                browserToClose |> Option.iter (fun b -> b.CloseAsync () |> ignore)
                playwrightToDispose |> Option.iter (fun p -> p.Dispose ())
                mounted.Stop ()
        }

let mountedTests =
    testList "Path-mounted session (browser)" [
        testCaseAsync "a session served under a path boots, signs in, and connects over WebRTC" <|
            async {
                let mounted = startMounted (fun _ -> false)
                // Teardown in `finally`: a failing assertion used to skip it and leave the
                // Manager, its session child and the proxy holding their ports, so one red run
                // could poison whatever ran next (the failing CI run showed exactly that, as
                // "Terminate orphan process" lines).
                let mutable browserToClose : IBrowser option = None
                let mutable playwrightToDispose : IPlaywright option = None
                try
                    let publicUrl = mounted.PublicUrl
                    let! pw = await (Playwright.CreateAsync ())
                    playwrightToDispose <- Some pw
                    let! br =
                        await (pw.Chromium.LaunchAsync (
                            BrowserTypeLaunchOptions (
                                ExecutablePath = chromiumPath (),
                                Args = [| "--disable-features=WebRtcHideLocalIpsWithMdns" |])))
                    browserToClose <- Some br
                    let! context = await (br.NewContextAsync ())
                    let! page = await (context.NewPageAsync ())
                    page.SetDefaultTimeout 20000.0f

                    // Everything below happens at the PUBLIC path. Nothing in the browser was
                    // told about a prefix: the shell's `<base href>` is the only thing making
                    // its relative routes resolve under the mount.
                    let! _ = await (page.GotoAsync publicUrl)

                    // NOTHING may be evaluated until the page has settled. On a 401 from `me`
                    // the client RENAVIGATES through the login bounce (session -> manager ->
                    // `<mount>/callback` -> `./` -> the shell), and an `EvaluateAsync` racing
                    // that navigation dies with "Execution context was destroyed" — which is
                    // exactly how this test passed locally and broke master. `connected` is only
                    // true on the shell after the bounce, and `WaitForFunctionAsync` re-arms
                    // across navigations, so it is the one safe thing to await first.
                    let! _ = await (page.WaitForFunctionAsync connected)

                    let! baseHref = await (page.EvaluateAsync<string> "() => document.querySelector('base')?.getAttribute('href')")
                    Expect.equal baseHref (sprintf "/s/%s/" MOUNT_SESSION) "the shell declares its mount"

                    // Installable, and installable AS ITSELF. The manifest is addressed
                    // relatively like every other route here, and the URLs INSIDE it resolve
                    // against its own address — so the app a person adds to their home screen
                    // launches at this session rather than at whatever sits on the origin's
                    // root. Resolved by the browser rather than compared as text, because the
                    // resolution is the property; the icon is fetched for the same reason a
                    // manifest naming an icon nobody serves would still parse.
                    let! installed =
                        await (page.EvaluateAsync<string>
                                """async () => {
                                     const href = document.querySelector('link[rel=manifest]').href
                                     const manifest = await (await fetch(href)).json()
                                     const icon = await fetch(new URL(manifest.icons[0].src, href))
                                     return [new URL(manifest.start_url, href).pathname,
                                             new URL(icon.url).pathname,
                                             icon.status,
                                             icon.headers.get('content-type')].join(' ')
                                   }""")
                    Expect.equal
                        installed
                        (sprintf "/s/%s/ /s/%s/icon.png 200 image/png" MOUNT_SESSION MOUNT_SESSION)
                        "the installed app starts at this session, and its mark is served under the same mount"

                    // No assertion here that the bundle was fetched under the mount: reaching
                    // `connected` above already required it. The bundle IS the client, and a
                    // root-anchored URL would have hit the proxy's root and 404'd, so nothing
                    // would have run to set the flag. Scraping `performance` entries for the
                    // bundle's path restated that, and only added a second place that had to
                    // know how the bundle is addressed — which is what broke when it became
                    // `client.<digest>.js`.

                    // The auth cookie is scoped to this session's mount, not the whole origin.
                    let! cookies = await (context.CookiesAsync ())
                    let sessionCookie =
                        cookies |> Seq.tryFind (fun c -> c.Name.StartsWith "yession_auth_")
                    match sessionCookie with
                    | None -> failwith "no session auth cookie was set"
                    | Some cookie ->
                        Expect.equal cookie.Path (sprintf "/s/%s/" MOUNT_SESSION) "scoped to the mount, not shared with siblings"

                    // Client-side persistence across a full server wipe (Step 20), which is
                    // only observable where the ADDRESS survives the restart. This used to
                    // live in the unmounted fixture and passed because that fixture pinned
                    // the session's port; Plan 13 deleted the pinning, so the property now
                    // belongs where it actually holds — and proving it here is the point of
                    // path-mounting rather than an accident of it.
                    let composerSel = """[data-rich-readonly="false"] .ProseMirror"""
                    let! _ = await (page.WaitForSelectorAsync composerSel)
                    do! awaitU (page.ClickAsync composerSel)
                    do! awaitU (page.Keyboard.TypeAsync "persisted across the wipe")
                    let hasDraft =
                        """[...document.querySelectorAll('.ProseMirror')].some(p => p.textContent === 'persisted across the wipe')"""
                    let! _ = await (page.WaitForFunctionAsync hasDraft)

                    mountedHost.Kill true
                    mountedHost.WaitForExit ()
                    if Directory.Exists mountDataDir then Directory.Delete (mountDataDir, true)
                    mounted.Restart ()

                    // The SAME url — the session came back on a different loopback port and
                    // the proxy followed it, which the browser never saw. So the origin is
                    // unchanged, its IndexedDB is still this session's, and the draft can
                    // only have come from there: the server's copy was deleted.
                    let! _ = await (page.ReloadAsync ())
                    let! _ = await (page.WaitForFunctionAsync connected)
                    do! await (page.WaitForFunctionAsync hasDraft) |> Async.Ignore
                finally
                    browserToClose |> Option.iter (fun b -> b.CloseAsync () |> ignore)
                    playwrightToDispose |> Option.iter (fun p -> p.Dispose ())
                    mounted.Stop ()
            }

        // THE bug report, and the last thing plan 20 owed: open a session you have read
        // before, with the session gone, and read it. Plan 22 adds the terminal.
        //
        // Everything it needs was built in the four steps before this one — the log addressed
        // so its tail can be kept, the client keeping it, the replay that runs before the
        // client knows whether it may connect, and a timeline that does not claim emptiness
        // it has not checked. What was missing was a PAGE: the shell is `no-cache`, and a dead
        // host cannot answer a revalidation, so until the worker there was nothing to render
        // any of it into. That is why this case could not go green before now, and why it
        // merges with the worker rather than sitting red beside it.
        offlineReopen
            "the session is gone, the page is still there, and so is its conversation"
            (fun page ->
                async {
                    // Say something, so there is history to lose.
                    let composerSel = """[data-rich-readonly="false"] .ProseMirror"""
                    let! _ = await (page.WaitForSelectorAsync composerSel)
                    do! awaitU (page.ClickAsync composerSel)
                    do! awaitU (page.Keyboard.TypeAsync saidInTimeline)
                    do! awaitU (page.Locator("[data-send-draft]").First.ClickAsync ())
                    do! waitFor "the message in the timeline" page inTimeline
                    do! waitFor "the conversation to be kept" page conversationKept
                })
            (fun page ->
                async {
                    // It has what this client had been reading. Scoped to the conversation
                    // element: a page-level match is satisfied by the roster and by the
                    // composer this text was typed into.
                    do! waitFor "the conversation to come back offline" page inTimeline
                })

        // The connection state is its own promise, and its own way to break: a client that
        // replayed its history out of its own store and then claimed to be CONNECTED to the
        // session it replayed without would be lying about the one leg that is down.
        offlineReopen
            "a client reading its own history does not claim to be connected"
            (fun _ -> async { return () })
            (fun page ->
                async {
                    do!
                        waitFor
                            "the client to stop claiming it is connected"
                            page
                            """document.querySelector('[data-connection]')?.getAttribute('data-connection') !== 'Connected'"""
                })

        // The report has two mounts — the nav column's, and the bar for where the column
        // cannot be seen — and what keeps them ONE report is a visibility rule. No markup test
        // can settle it: both are in the document at once by design, so a `Contains` sees the
        // intended thing and a person sees a screen saying it twice (which is what a phone
        // with its nav open did, before `connectionInColumn`).
        //
        // Counted by HOOK, never by the words in it. The first version of this test counted
        // occurrences of "not connected" and "reconnecting" and went red against a surface
        // that says "session stopped" — the invariant was right and the assertion had been
        // written against the copy.
        offlineReopen
            "the connection is never reported by two surfaces at once"
            (fun _ -> async { return () })
            (fun page ->
                async {
                    // Visible means a person can SEE it, which neither `offsetParent` (null for
                    // anything fixed — the bar is) nor a bounding rect (non-zero for the
                    // collapsed nav's contents, clipped to nothing by `overflow-hidden`) can
                    // tell you. Hit-test the centre and ask what is painted there.
                    let counted =
                        """(() => {
                             const seen = el => {
                               const r = el.getBoundingClientRect()
                               if (r.width < 1 || r.height < 1) return false
                               const x = Math.min(Math.max(r.left + r.width / 2, 1), innerWidth - 1)
                               const y = Math.min(Math.max(r.top + r.height / 2, 1), innerHeight - 1)
                               const hit = document.elementFromPoint(x, y)
                               return !!hit && (el.contains(hit) || hit.contains(el))
                             }
                             const reports = [...document.querySelectorAll('[data-degraded]')].filter(seen)
                             const offer = [...document.querySelectorAll('[data-session-gone]')].filter(seen)
                             return { reports: reports.length, offer: offer.length }
                           })()"""
                    let atMostOne = sprintf "%s.reports <= 1" counted
                    // And not zero everywhere: the column may be showing the reconnect card
                    // INSTEAD of the status, so what must always hold is that something on
                    // screen says the session is gone. Without this the case above passes on a
                    // client that reports nothing at all.
                    let saidSomewhere = sprintf "(%s.reports + %s.offer) >= 1" counted counted
                    do! waitFor "the client to notice the session is gone" page
                            """document.querySelector('[data-degraded]') !== null"""
                    for width, height, what in [ 1440, 900, "a desktop"; 390, 844, "a phone" ] do
                        do! awaitU (page.SetViewportSizeAsync (width, height))
                        do! waitFor (sprintf "one report at most on %s" what) page atMostOne
                        do! waitFor (sprintf "and something saying it on %s" what) page saidSomewhere
                    // The pane a phone reader can be on when it happens. The bar is fixed above
                    // all three, so bringing the nav out over it must not reveal a second copy.
                    do! awaitU (page.Locator("[data-nav-toggle='show']").First.ClickAsync ())
                    do! waitFor "still one report with the nav open over it" page atMostOne
                })

        // The way back is a link to the Manager, and a link is followed ONCE. It used to carry
        // a click handler that navigated to the same URL — an "enhancement" that fired beside
        // the default — so one press sent two `GET …/open`, each of which is a launch: two
        // children for one session, one of them forgotten, and a login bounce that redeemed
        // its code against the wrong one's registration. Counted where the Manager would
        // count it — at the request — by standing in for a Manager the host took down with
        // it: a stub that answers, so the browser commits ONE navigation and asks nothing
        // again (a refused connection is an error page, and error pages get retried).
        offlineReopen
            "pressing reopen asks the Manager once"
            (fun _ -> async { return () })
            (fun page ->
                async {
                    do! waitFor "the offer to reopen" page """document.querySelector('[data-session-reopen]') !== null"""
                    let asked = ResizeArray<string> ()
                    page.Request.Add (fun r -> if r.Url.EndsWith "/open" then asked.Add r.Url)
                    do! awaitU (page.RouteAsync ("**/sessions/*/open", fun route ->
                            route.FulfillAsync (RouteFulfillOptions (Status = 200, ContentType = "text/html", Body = "<title>opening</title>"))
                            |> ignore))
                    // Whichever mount is on screen; a hidden one cannot be pressed.
                    do! awaitU (page.Locator("[data-session-reopen]:visible").First.ClickAsync ())
                    do! waitFor "the browser to have left for the Manager" page """location.pathname.endsWith('/open')"""
                    Expect.equal asked.Count 1 (sprintf "one press, one request: %A" (List.ofSeq asked))
                })

        // The probe settles four ways and each has a remedy; the fifth, never, had none. A
        // A session is ONE step from wherever it was opened. Its first visit signs in by
        // renavigation (session -> Manager -> callback -> session), and that bounce used to be
        // a pushed entry: Back landed on a shell with no cookie, which bounced forward again,
        // and the Manager was a second press away — every new session sat two entries deep.
        // Pinned from the Manager's side, since that is the promise: open a session from the
        // Manager, press Back, and it is the Manager you are looking at. Only a browser can
        // answer this; the history stack is nothing the markup knows.
        testCaseAsync "back from a freshly opened session is the Manager, not a sign-in bounce" <|
            async {
                let mounted = startMounted (fun _ -> false)
                let mutable browserToClose : IBrowser option = None
                let mutable playwrightToDispose : IPlaywright option = None
                try
                    let managerUrl = mounted.ManagerOrigin + "/"
                    let publicUrl = mounted.PublicUrl
                    let! pw = await (Playwright.CreateAsync ())
                    playwrightToDispose <- Some pw
                    let! br =
                        await (pw.Chromium.LaunchAsync (
                            BrowserTypeLaunchOptions (
                                ExecutablePath = chromiumPath (),
                                Args = [| "--disable-features=WebRtcHideLocalIpsWithMdns" |])))
                    browserToClose <- Some br
                    let! context = await (br.NewContextAsync ())
                    let! page = await (context.NewPageAsync ())
                    page.SetDefaultTimeout 20000.0f
                    let evidence = watching page
                    do! reporting "back from a freshly opened session" page evidence <| async {
                    let! _ = await (page.GotoAsync managerUrl)
                    // The session, as a person reaches it from the Manager: the first visit,
                    // with no cookie, so the sign-in bounce runs. `connected` is only true on
                    // the shell after it, and re-arms across the navigations in between.
                    let! _ = await (page.GotoAsync publicUrl)
                    let! _ = await (page.WaitForFunctionAsync connected)
                    let! _ = await (page.GoBackAsync ())
                    do! waitFor "the Manager, one step back" page
                            (sprintf "location.href === %s" (System.Text.Json.JsonSerializer.Serialize managerUrl))
                    }
                finally
                    browserToClose |> Option.iter (fun b -> b.CloseAsync () |> ignore)
                    playwrightToDispose |> Option.iter (fun p -> p.Dispose ())
                    mounted.Stop ()
            }

        // phone with its tunnel not yet up, a proxy whose upstream accepted and stalled, a
        // page suspended with the fetch in flight — each left the client wearing the state it
        // started in: "not connected", no reason, and nothing to press, for as long as that
        // took. Now the probe is answered FOR (`Client.Probe.deadline`), and the way back is
        // on the screen. Its own fixture rather than `offlineReopen`, because a killed host
        // answers 502 at once and the case is precisely an answer that never comes.
        testCaseAsync "a probe that never answers is answered for it, and the way back is offered" <|
            async {
                let mutable stalling = false
                let mounted = startMounted (fun url -> stalling && url.EndsWith "/me")
                let mutable browserToClose : IBrowser option = None
                let mutable playwrightToDispose : IPlaywright option = None
                try
                    let publicUrl = mounted.PublicUrl
                    let! pw = await (Playwright.CreateAsync ())
                    playwrightToDispose <- Some pw
                    let! br =
                        await (pw.Chromium.LaunchAsync (
                            BrowserTypeLaunchOptions (
                                ExecutablePath = chromiumPath (),
                                Args = [| "--disable-features=WebRtcHideLocalIpsWithMdns" |])))
                    browserToClose <- Some br
                    let! context = await (br.NewContextAsync ())
                    let! page = await (context.NewPageAsync ())
                    page.SetDefaultTimeout 20000.0f
                    let evidence = watching page
                    do! reporting "a probe that never answers" page evidence <| async {
                    // Signed in and connected first, so the reload below is a client that has
                    // everything but an answer — not one that is off to log in.
                    let! _ = await (page.GotoAsync publicUrl)
                    let! _ = await (page.WaitForFunctionAsync connected)
                    stalling <- true
                    let! _ = await (page.ReloadAsync ())
                    do! waitFor "the offer to reopen, once the probe has been given up on" page
                            """document.querySelector('[data-session-reopen]') !== null"""
                    }
                finally
                    browserToClose |> Option.iter (fun b -> b.CloseAsync () |> ignore)
                    playwrightToDispose |> Option.iter (fun p -> p.Dispose ())
                    mounted.Stop ()
            }

        // Plan 22, and the other half of the bug report: the conversation came back offline
        // and the terminal under it did not. `Srt` because this really runs a command — on a
        // box that cannot host a sandbox the block never reaches `ok` and this would HANG
        // rather than fail.
        Tag.needs "a terminal that really ran something" [ Tag.Browser; Tag.Native; Tag.Srt ] (fun () ->
        offlineReopen
            "the session is gone, the page is still there, and so is what its terminal printed"
            (fun page ->
                async {
                    do! awaitU (page.Locator("[data-terminal-toggle='show']").First.ClickAsync ())
                    do! awaitU (page.Locator("[data-terminal-new]").First.ClickAsync ())
                    let composerInput = "[data-terminal-input^='term-draft:']:not([readonly])"
                    let! _ = await (page.WaitForSelectorAsync composerInput)
                    do! awaitU (page.ClickAsync composerInput)
                    do! awaitU (page.Keyboard.TypeAsync (sprintf "echo %s" printedInTerminal))
                    do! awaitU (page.ClickAsync "[data-terminal-send]")
                    do! waitFor "the printed line on screen" page terminalPrinted
                    do! waitFor "the transcript to be kept" page transcriptKept
                })
            (fun page ->
                async {
                    // The column starts shut on a fresh load, so this also says the replayed
                    // records are there to be shown BEFORE anyone opens it — which is what a
                    // store read before the network buys.
                    do! awaitU (page.Locator("[data-terminal-toggle='show']").First.ClickAsync ())
                    do! waitFor "the terminal output to come back offline" page terminalPrinted
                }))
    ]

// --- Creating a session behind a front door (browser) -------------------------------------
//
// The deployment nothing else here has: ONE public origin, with the Manager at its root and
// every session under `/s/<id>`, fronted by a door that learns where those sessions really
// are from the Manager's registry stream. That door is BEHIND
// — a session exists and is running for a moment before a mapping for it appears — and what
// it answers in that window is `404 not found`.
//
// Which is the whole hazard `/open` exists for, and the one it used to get wrong: "the
// address answered" and "the session answered" are different facts, and a page that cannot
// tell them apart hands whoever pressed Create straight into the front door's miss. As a
// `text/plain` body, that is a message on a laptop and a file called `document.txt` on a
// phone.

let private FRONT_SESSION = "front-default"
let private frontDataDir = "tests/browser/.data-front"

/// The operator's front door, with its reconciler's lag made into something a test can
/// depend on: a session is routed only from its `lag`-th request onward.
///
/// Counted in REQUESTS, not milliseconds, so no sleep anywhere decides whether this passes —
/// and sticky once it catches up, like a real reconciler: a mapping that has appeared does not
/// go away again. Where each session actually listens is read off the Manager's registry
/// stream, the same subscription an operator's proxy holds open, because a fixture that was
/// TOLD the port would not be standing where the lag is.
let private startFrontDoor (managerPort: int) (lag: int) : Serving =
    let served = listenOnLoopback ()
    let listener = served.Listener
    let client = new HttpClient (new HttpClientHandler (AllowAutoRedirect = false, UseCookies = false))
    let ports = System.Collections.Concurrent.ConcurrentDictionary<string, int> ()
    let asked = System.Collections.Concurrent.ConcurrentDictionary<string, int> ()
    // The registry, read the way a reconciler reads it. Retried because the door has to be up
    // BEFORE the Manager — the Manager's public origin is this port, and its first session
    // fetches OIDC discovery against it while booting. (This delay is a reconnect backoff in
    // the fixture's plumbing; nothing the case asserts waits on a clock.)
    let rec watching () =
        async {
            try
                use registry = new HttpClient (Timeout = Timeout.InfiniteTimeSpan)
                let! stream =
                    registry.GetStreamAsync (sprintf "http://127.0.0.1:%d/sessions/stream" managerPort)
                    |> Async.AwaitTask
                use reader = new StreamReader (stream)
                let mutable live = true
                while live do
                    let! line = reader.ReadLineAsync () |> Async.AwaitTask
                    if isNull line then live <- false
                    elif line.StartsWith "data:" then
                        use frame = JsonDocument.Parse (line.Substring 5)
                        for entry in frame.RootElement.GetProperty("sessions").EnumerateArray () do
                            ports.[entry.GetProperty("id").GetString ()] <- entry.GetProperty("port").GetInt32 ()
            with _ -> ()
            do! Async.Sleep 100
            return! watching ()
        }
    Async.Start (watching ())
    let sessionIn (path: string) =
        let m = Text.RegularExpressions.Regex.Match (path, "^/s/([^/]+)(/.*)?$")
        if m.Success then Some m.Groups.[1].Value else None
    let routes (id: string) =
        let asks = asked.AddOrUpdate (id, 1, fun _ n -> n + 1)
        asks > lag && ports.ContainsKey id
    let copyHeader (reply: HttpResponseMessage) (ctx: HttpListenerContext) (name: string) =
        let values =
            match reply.Headers.TryGetValues name with
            | true, vs -> List.ofSeq vs
            | _ ->
                match reply.Content.Headers.TryGetValues name with
                | true, vs -> List.ofSeq vs
                | _ -> []
        for v in values do ctx.Response.Headers.Add (name, v)
    let rec loop () =
        async {
            match! Async.Catch (listener.GetContextAsync () |> Async.AwaitTask) with
            | Choice1Of2 ctx ->
                Async.Start (
                    async {
                        try
                            let raw = ctx.Request.RawUrl
                            let session = sessionIn (raw.Split('?').[0])
                            match session with
                            | Some id when not (routes id) ->
                                // What a front door says about an address it has no route
                                // for. Verbatim, because being indistinguishable from a
                                // session's own answer is the point.
                                ctx.Response.StatusCode <- 404
                                ctx.Response.ContentType <- "text/plain"
                                let bytes = Text.Encoding.UTF8.GetBytes "not found"
                                ctx.Response.OutputStream.Write (bytes, 0, bytes.Length)
                            | _ ->
                                // The path goes through UNCHANGED: a session strips its own
                                // `/s/<id>` mount, and the Manager is what
                                // this origin's root is.
                                let port =
                                    match session with
                                    | Some id -> ports.[id]
                                    | None -> managerPort
                                let target = sprintf "http://127.0.0.1:%d%s" port raw
                                use request = new HttpRequestMessage (HttpMethod ctx.Request.HttpMethod, target)
                                if ctx.Request.HasEntityBody then
                                    use buffer = new MemoryStream ()
                                    ctx.Request.InputStream.CopyTo buffer
                                    let content = new ByteArrayContent (buffer.ToArray ())
                                    match ctx.Request.ContentType with
                                    | null | "" -> ()
                                    | contentType -> content.Headers.TryAddWithoutValidation ("content-type", contentType) |> ignore
                                    request.Content <- content
                                match ctx.Request.Headers.["Cookie"] with
                                | null | "" -> ()
                                | cookie -> request.Headers.TryAddWithoutValidation ("cookie", cookie) |> ignore
                                let! reply = client.SendAsync request |> Async.AwaitTask
                                ctx.Response.StatusCode <- int reply.StatusCode
                                copyHeader reply ctx "Location"
                                copyHeader reply ctx "Set-Cookie"
                                copyHeader reply ctx "Cache-Control"
                                match reply.Content.Headers.ContentType with
                                | null -> ()
                                | contentType -> ctx.Response.ContentType <- string contentType
                                let! bytes = reply.Content.ReadAsByteArrayAsync () |> Async.AwaitTask
                                ctx.Response.OutputStream.Write (bytes, 0, bytes.Length)
                        with _ -> ctx.Response.StatusCode <- 502
                        ctx.Response.Close ()
                    })
                return! loop ()
            | Choice2Of2 _ -> ()   // listener stopped
        }
    Async.Start (loop ())
    served

let mutable private frontedHost : Process = null

/// The product entry, told that its ONE public origin is the front door at `publicOrigin`: the
/// Manager at its root (which is also the OIDC issuer, so it must resolve there) and sessions
/// under `/s/{id}` on the same origin.
///
/// Both addresses arrive as arguments because neither is this host's to choose, and its own
/// is the one a Manager behind a proxy cannot be told `0` for: it launches its default session
/// while booting, that session fetches discovery through the door, and the door has to know
/// where to forward that before the Manager has said anything at all. (It was tried the other
/// way, and the session exited on a 502 from a door forwarding to port 0.)
let private startFrontedHost (publicOrigin: string) (managerPort: int) : unit =
    let psi = ProcessStartInfo "node"
    psi.ArgumentList.Add "app/out/Main.js"
    psi.ArgumentList.Add "--auth"
    psi.ArgumentList.Add "localhost"
    psi.ArgumentList.Add "--port"
    psi.ArgumentList.Add (string managerPort)
    psi.ArgumentList.Add "--default-session"
    psi.ArgumentList.Add FRONT_SESSION
    psi.ArgumentList.Add "--data-dir"
    psi.ArgumentList.Add frontDataDir
    psi.UseShellExecute <- false
    psi.RedirectStandardOutput <- true
    psi.EnvironmentVariables.["YESSION_MANAGER_URL"] <- publicOrigin
    psi.EnvironmentVariables.["YESSION_SESSION_URL"] <- publicOrigin + "/s/{id}"
    let p = new Process (StartInfo = psi)
    let ready = TaskCompletionSource<bool> ()
    // The management UI's line, not the session's: this case drives the Manager, and that line
    // is the last thing a completed boot prints.
    p.OutputDataReceived.Add (fun e ->
        if e.Data <> null && e.Data.Contains "management UI at" then ready.TrySetResult true |> ignore)
    p.Start () |> ignore
    p.BeginOutputReadLine ()
    frontedHost <- p
    if not (ready.Task.Wait 60000) then failwith "fronted host never reported readiness"

let frontDoorTests =
    testList "Creating a session behind a front door (browser)" [
        testCaseAsync "creating a session lands in that session, never on the front door's miss" <|
            async {
                if Directory.Exists frontDataDir then Directory.Delete (frontDataDir, true)
                // The door comes up first: the Manager's public origin IS this port, and its
                // default session fetches OIDC discovery against it while booting.
                // The door first, and on a port of its own: the Manager's public origin IS
                // this port, and its default session fetches OIDC discovery against it while
                // booting. The Manager's own port is taken at `:0` and released, because the
                // door is told where to forward before the Manager exists to be asked.
                let frontManagerPort = freeLoopbackPort ()
                let door = startFrontDoor frontManagerPort 2
                let mutable browserToClose : IBrowser option = None
                let mutable playwrightToDispose : IPlaywright option = None
                try
                    startFrontedHost door.Origin frontManagerPort
                    let! pw = await (Playwright.CreateAsync ())
                    playwrightToDispose <- Some pw
                    let! br = await (pw.Chromium.LaunchAsync (BrowserTypeLaunchOptions (ExecutablePath = chromiumPath ())))
                    browserToClose <- Some br
                    let! context = await (br.NewContextAsync ())
                    let! page = await (context.NewPageAsync ())
                    page.SetDefaultTimeout 30000.0f
                    let evidence = watching page
                    do! reporting "create behind a front door" page evidence <| async {
                    let! _ = await (page.GotoAsync (door.At "/"))

                    // Pressed, not POSTed: the whole fault lives in what the browser does with
                    // the answer, so the browser has to be the thing that asks.
                    let create = sprintf "[%s] button[type=submit]" Yession.App.Dom.Manager.createSession
                    let! _ = await (page.WaitForSelectorAsync create)
                    do! awaitU (page.ClickAsync create)

                    // THE promise: pressing Create puts you in the session it created. One
                    // fact, read off the page rather than off its words — the shell says which
                    // session it is, and the address says which session was asked for, and
                    // they have to be the same one.
                    let landed =
                        sprintf
                            """() => {
                                 const at = /^\/s\/([^/]+)\//.exec(location.pathname)
                                 const shell = document.querySelector('meta[name="%s"]')?.getAttribute('content')
                                 return !!at && !!shell && shell === at[1]
                               }"""
                            Yession.App.Dom.sessionMetaName
                    // Launching a real child and waiting out the door's lag is the slow part;
                    // the failure this guards is instant, so a long wait only ever costs a
                    // green run time.
                    try
                        do!
                            await (page.WaitForFunctionAsync (landed, null, PageWaitForFunctionOptions (Timeout = 90000.0f)))
                            |> Async.Ignore
                    with _ ->
                        // A browser case can only fail by a wait not settling, and that failure
                        // names the wait rather than the fault. The page has been holding the
                        // answer the whole time: say what it is showing.
                        let! showing =
                            await (page.EvaluateAsync<string>
                                    """() => JSON.stringify({
                                         url: location.href,
                                         title: document.title,
                                         text: document.body?.innerText?.slice(0, 200) ?? null
                                       })""")
                        failwithf
                            "creating a session must land in that session; the browser is showing %s"
                            showing
                    }
                finally
                    browserToClose |> Option.iter (fun b -> b.CloseAsync () |> ignore)
                    playwrightToDispose |> Option.iter (fun p -> p.Dispose ())
                    door.Stop ()
                    try if frontedHost <> null then frontedHost.Kill true with _ -> ()
            }
    ]

// --- A fronted deployment, for real (browser) ---------------------------------------------
//
// The door above is a fixture: a stand-in reconciler with a lag the case can count. This is
// the deployment the proxy example's README tells an operator to run — its own Caddyfile
// (examples/proxy/caddy) in front of a Manager under `--auth trusted-headers`, `main.mjs`
// following the registry stream into the session map Caddy imports, and Chromium playing
// `tailscale serve`: TLS terminated somewhere else, and the calling tailnet user asserted in
// `Tailscale-User-*` headers on every request.
//
// Nothing between the browser and the session is a double, so what this pins is the promise
// all of those pieces make together and none of them makes alone: a person on the tailnet
// who presses Create is in the session that made, as themselves. The Caddyfile was checked
// by hand before this case existed, and its identity translation has an ordering trap (the
// README) that reads fine and strips the user — which only a run can see.

let private frontedDataDir = "tests/browser/.data-fronted"
let private frontedMapDir = frontedDataDir + "/proxy"

/// Who the ingress says is calling. `serve` asserts these on every request and overwrites
/// anything the client sent, which is what the Caddyfile is entitled to trust.
let private FRONTED_LOGIN = "alice@example.com"
let private FRONTED_NAME = "Alice Example"

/// A child process of the deployment, kept with what it has said so the failure report can
/// say which of three processes went wrong, in its own words.
type private Deployed =
    { Label: string
      Process: Process
      /// The origin its readiness line carried, where it carried one. A piece told `--port 0`
      /// states its address there and nowhere else, so this is the only thing that knows it.
      Origin: string option
      Said: Text.StringBuilder }
    /// Where it came up, for a caller that has to address it. A piece whose readiness line
    /// named no address cannot be addressed, and says so rather than answering with a guess.
    member this.At (path: string) : string =
        match this.Origin with
        | Some origin -> origin + path
        | None -> failwithf "%s never said where it came up" this.Label
    /// The port it came up on, for the rare assertion that is about the port itself.
    member this.Port : int = Uri(this.At "/").Port
    member this.Stop () =
        try if not this.Process.HasExited then this.Process.Kill true with _ -> ()

/// Spawn one piece of the deployment and wait for the line that says it is up. A piece that
/// dies on its arguments fails here, naming itself, rather than as a wait downstream that
/// never settles.
///
/// The URL in that line, where there is one, is the address it really came up on — which is
/// what lets a piece be told `--port 0` and asked afterwards rather than assigned a number.
let private deploy
    (label: string)
    (command: string)
    (args: string list)
    (env: (string * string) list)
    (ready: string -> bool)
    : Deployed =
    let psi = ProcessStartInfo command
    args |> List.iter psi.ArgumentList.Add
    env |> List.iter (fun (name, value) -> psi.EnvironmentVariables.[name] <- value)
    psi.UseShellExecute <- false
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    let p = new Process (StartInfo = psi)
    let said = Text.StringBuilder ()
    let up = TaskCompletionSource<bool> ()
    // A `ref` rather than a `let mutable`, because `heard` is a closure and F# will not let one
    // capture a mutable local.
    let origin = ref None
    let heard (line: string) =
        if line <> null then
            lock said (fun () -> said.AppendLine line |> ignore)
            if ready line then
                origin.Value <- urlIn line |> Option.map (fun url -> url.TrimEnd '/')
                up.TrySetResult true |> ignore
    p.OutputDataReceived.Add (fun e -> heard e.Data)
    p.ErrorDataReceived.Add (fun e -> heard e.Data)
    p.EnableRaisingEvents <- true
    p.Exited.Add (fun _ -> up.TrySetResult false |> ignore)
    p.Start () |> ignore
    p.BeginOutputReadLine ()
    p.BeginErrorReadLine ()
    if not (up.Task.Wait 60000) || not up.Task.Result then
        try if not p.HasExited then p.Kill true with _ -> ()
        failwithf "%s never came up; it said:\n%s" label (string said)
    // Read AFTER the wait: the readiness line is where the address is stated, so there is
    // nothing to read until it has arrived.
    { Label = label; Process = p; Origin = origin.Value; Said = said }

/// The deployment, and the one address a person on the tailnet ever sees of it.
type private Fronted =
    { /// The public origin — caddy's, which is the Manager's origin too.
      Origin : string
      Pieces : Deployed list }

/// The three processes, in the order a cold boot needs them: the proxy first, because the
/// Manager's public origin IS the proxy and its default session runs OIDC discovery against
/// it while booting; the Manager; then the map, which needs the Manager's stream to follow.
/// Everything here is what the README says to run, with the README's own template.
///
/// Both ports are taken at `:0` and released rather than chosen. Neither piece can be told
/// `0` and asked afterwards: caddy is handed the Manager's address to reverse-proxy to, and
/// the Manager is handed caddy's as its public origin, so each has to be known before either
/// starts.
let private deployFronted () : Fronted =
    if Directory.Exists frontedDataDir then Directory.Delete (frontedDataDir, true)
    Directory.CreateDirectory frontedMapDir |> ignore
    let frontedPort = freeLoopbackPort ()
    let frontedManagerPort = freeLoopbackPort ()
    let origin = sprintf "http://127.0.0.1:%d" frontedPort
    let proxy =
        deploy
            "caddy"
            "caddy"
            [ "run"; "--config"; "examples/proxy/caddy/Caddyfile"; "--adapter"; "caddyfile"; "--watch" ]
            [ "YESSION_PROXY_PORT", string frontedPort
              "YESSION_PROXY_MANAGER", sprintf "127.0.0.1:%d" frontedManagerPort
              // Absolute: an `import` glob is resolved against the Caddyfile's own directory,
              // and the map is written under this suite's, not the example's.
              "YESSION_PROXY_SESSIONS", Path.GetFullPath frontedMapDir + "/sessions*.caddy" ]
            (fun line -> line.Contains "serving initial configuration")
    let manager =
        deploy
            "the Manager"
            "node"
            [ "app/out/Main.js"; "--auth"; "trusted-headers"; "--secrets"; "ephemeral"
              "--port"; string frontedManagerPort; "--data-dir"; frontedDataDir ]
            [ "YESSION_MANAGER_URL", origin
              "YESSION_SESSION_URL", origin + "/s/{id}" ]
            (fun line -> line.Contains "management UI at")
    let map =
        deploy
            "sessions-map"
            "node"
            [ "examples/proxy/main.mjs"
              "--manager"; sprintf "http://127.0.0.1:%d" frontedManagerPort
              "--as"; "proxy-map"
              "--out"; frontedMapDir + "/sessions.caddy"
              "--empty"; "# no running sessions"
              "--template"; "@s_{id} path /s/{id} /s/{id}/*\nhandle @s_{id} {\n\treverse_proxy 127.0.0.1:{port}\n}" ]
            []
            (fun line -> line.Contains " follows ")
    { Origin = origin; Pieces = [ proxy; manager; map ] }

let frontedTests =
    testList "A fronted deployment, for real (browser)" [
        testCaseAsync "pressing Create on the tailnet lands you in that session, as yourself" <|
            async {
                let deployed = deployFronted ()
                let mutable browserToClose : IBrowser option = None
                let mutable playwrightToDispose : IPlaywright option = None
                try
                    let! pw = await (Playwright.CreateAsync ())
                    playwrightToDispose <- Some pw
                    let! br = await (pw.Chromium.LaunchAsync (BrowserTypeLaunchOptions (ExecutablePath = chromiumPath ())))
                    browserToClose <- Some br
                    // The browser IS the ingress here: what `serve` would assert about the
                    // caller rides every request, including the sign-in bounce a session sends
                    // through the Manager — which is the request the identity has to survive.
                    let! context =
                        await (br.NewContextAsync (
                            BrowserNewContextOptions (
                                ExtraHTTPHeaders =
                                    dict [ "Tailscale-User-Login", FRONTED_LOGIN
                                           "Tailscale-User-Name", FRONTED_NAME
                                           "Tailscale-User-Profile-Pic", "https://example.com/alice.png" ])))
                    let! page = await (context.NewPageAsync ())
                    page.SetDefaultTimeout 30000.0f
                    let evidence = watching page
                    do! reporting "a fronted deployment" page evidence <| async {
                    // The first answer is the Manager's own verdict on the identity the proxy
                    // handed it, so read it rather than wait thirty seconds for a Create
                    // button a 401 page will never show. (The deletion-ordering trap in the
                    // Caddyfile fails exactly here: the Manager sees nobody.)
                    let! landing = await (page.GotoAsync (deployed.Origin + "/"))
                    if landing.Status <> 200 then
                        let! body = await (landing.TextAsync ())
                        failwithf
                            "the Manager answered %d through the proxy — the identity the ingress asserted did not survive it: %s"
                            landing.Status
                            (body.Trim ())
                    let create = sprintf "[%s] button[type=submit]" Yession.App.Dom.Manager.createSession
                    let! _ = await (page.WaitForSelectorAsync create)
                    do! awaitU (page.ClickAsync create)

                    // One promise, read off the page: the address names a session, the shell
                    // says it is that one, the client is connected to it (so the sign-in went
                    // through the Manager as issuer, at the proxy's origin, and came back),
                    // and the name on the roster is the one the ingress asserted — through
                    // caddy's translation, the Manager's ID token and the session's cookie.
                    let landedAsYourself =
                        sprintf
                            """() => {
                                 const at = /^\/s\/([^/]+)\//.exec(location.pathname)
                                 const shell = document.querySelector('meta[name="%s"]')?.getAttribute('content')
                                 const connection = document.querySelector('[%s]')?.getAttribute('%s')
                                 const name = document.querySelector('[%s]')?.textContent?.trim()
                                 return !!at && shell === at[1] && connection === 'Connected' && name === '%s'
                               }"""
                            Yession.App.Dom.sessionMetaName
                            Yession.App.Dom.Hooks.connection
                            Yession.App.Dom.Hooks.connection
                            Yession.App.Dom.Hooks.displayName
                            FRONTED_NAME
                    // A real child launches, the map catches up, caddy re-adapts within a
                    // second, and the sign-in round-trips — slow on a cold runner, and the
                    // faults this guards are all instant, so a long wait only costs green time.
                    try
                        do!
                            await (page.WaitForFunctionAsync (landedAsYourself, null, PageWaitForFunctionOptions (Timeout = 90000.0f)))
                            |> Async.Ignore
                    with _ ->
                        let! showing =
                            await (page.EvaluateAsync<string>
                                    (sprintf
                                        """() => JSON.stringify({
                                             url: location.href,
                                             title: document.title,
                                             connection: document.querySelector('[%s]')?.getAttribute('%s') ?? null,
                                             name: document.querySelector('[%s]')?.textContent ?? null,
                                             text: document.body?.innerText?.slice(0, 200) ?? null
                                           })"""
                                        Yession.App.Dom.Hooks.connection
                                        Yession.App.Dom.Hooks.connection
                                        Yession.App.Dom.Hooks.displayName))
                        let said =
                            deployed.Pieces
                            |> List.map (fun d -> sprintf "--- %s said ---\n%s" d.Label (lock d.Said (fun () -> string d.Said)))
                            |> String.concat "\n"
                        failwithf
                            "pressing Create must land in that session as the asserted user; the browser is showing %s\n%s"
                            showing
                            said
                    }
                finally
                    browserToClose |> Option.iter (fun b -> b.CloseAsync () |> ignore)
                    playwrightToDispose |> Option.iter (fun p -> p.Dispose ())
                    // Reverse order: the map and the Manager before the proxy they sit behind.
                    for d in List.rev deployed.Pieces do d.Stop ()
            }
    ]

// --- The management page's filters (browser) ----------------------------------------------
//
// A filter chip is a link because the filter is the page's LOCATION: a bookmark restores it
// and the back button undoes it. The first half is the server's (the URL is parsed on every
// render) and the cheap tier pins it; the second is the page script's, and only a browser
// with a history can observe it — which is how the script came to `replaceState` on a chip
// click, leaving Back to exit the page, while a `popstate` handler waited for an event no
// click could produce.

let private filtersDataDir = "tests/browser/.data-filters"

let filterTests =
    testList "The management page's filters (browser)" [
        testCaseAsync "a filter click is a history entry: Back undoes it, and the rows follow" <|
            async {
                if Directory.Exists filtersDataDir then Directory.Delete (filtersDataDir, true)
                let manager =
                    deploy
                        "the Manager"
                        "node"
                        // `--port 0`: the OS chooses, and the readiness line says which, so
                        // this suite knows nothing about any other's address.
                        [ "app/out/Main.js"; "--auth"; "localhost"; "--secrets"; "ephemeral"
                          "--port"; "0"; "--data-dir"; filtersDataDir ]
                        []
                        (fun line -> line.Contains "management UI at")
                let mutable browserToClose : IBrowser option = None
                let mutable playwrightToDispose : IPlaywright option = None
                try
                    let! pw = await (Playwright.CreateAsync ())
                    playwrightToDispose <- Some pw
                    let! br = await (pw.Chromium.LaunchAsync (BrowserTypeLaunchOptions (ExecutablePath = chromiumPath ())))
                    browserToClose <- Some br
                    let! page = await (br.NewPageAsync ())
                    page.SetDefaultTimeout 30000.0f
                    let evidence = watching page
                    do! reporting "filter history" page evidence <| async {
                    let! _ = await (page.GotoAsync (manager.At "/"))
                    let archived = sprintf "[%s=\"show-archived\"]" Yession.App.Dom.Manager.filter
                    let! _ = await (page.WaitForSelectorAsync archived)
                    do! awaitU (page.ClickAsync archived)

                    // Both halves of one click, read off the page: the address carries the
                    // filter, and the rows stream has answered for it — which shows as the chip
                    // re-rendered for the NEW query, linking back to the one without it.
                    let lit =
                        sprintf
                            """() => location.search.includes('show=archived')
                                  && !document.querySelector('[%s="show-archived"]')?.getAttribute('href')?.includes('show=archived')"""
                            Yession.App.Dom.Manager.filter
                    let! _ = await (page.WaitForFunctionAsync lit)

                    // Back is the promise. It must stay on this page, take the filter out of
                    // the address, and move the rows with it — the chip links to adding it again.
                    let! _ = await (page.GoBackAsync ())
                    let unlit =
                        sprintf
                            """() => location.pathname === '/'
                                  && !location.search.includes('show=archived')
                                  && !!document.querySelector('[%s="show-archived"]')?.getAttribute('href')?.includes('show=archived')"""
                            Yession.App.Dom.Manager.filter
                    let! _ = await (page.WaitForFunctionAsync unlit)
                    ()
                    }
                finally
                    browserToClose |> Option.iter (fun b -> b.CloseAsync () |> ignore)
                    playwrightToDispose |> Option.iter (fun p -> p.Dispose ())
                    manager.Stop ()
            }
    ]

// --- Pressing Create (browser) ---------------------------------------------------------------
//
// Create is a real form and a real POST, and the browser follows the answer into the new
// session. Between the push and the arrival this page has nothing to show for it, so the
// button is HELD (`aria-busy`) — and two things have to hold true of a held Create that only a
// browser with the page script running can observe. One: a second push in that window is
// refused, because two Creates are two sessions. Two: a rows frame landing under it — the
// stream announces the new session before the redirect is followed — does not put a fresh,
// released button under the reader's finger.
//
// The window is opened by holding the POST at the browser's edge (`RouteAsync`), and two
// things about a page with a navigation in flight shape how the cases are written. Every
// locator action, measuring included, waits for that navigation to commit — so the button is
// measured before the push and pushed by the mouse, by coordinates, after. And `evaluate`
// does not answer until the navigation settles either — the page is running (its stream
// delivers, its swaps happen, verified by hand with a server that answered late) but nothing
// can be READ off it until then. So the case that reads answers the POST with a 204, which
// is the one response a navigation can get that leaves the page where it was.

let private pressDataDir = "tests/browser/.data-press"
let private createButton = sprintf "[%s] button" Yession.App.Dom.Manager.createSession

/// How a held create POST is let go: on to the Manager, so the browser leaves for the new
/// session; or answered with nothing, so the page stays and can be read.
type private Release =
    | LetThrough
    | AnswerNothing

/// The hold a case is handed: `Held` settles once the create POST has reached the route and
/// is being held there, and `LetGo` releases it. Nothing on the page can be READ while it is
/// held — a Create is a top-level navigation, and `EvaluateAsync` on a page whose navigation
/// is pending does not answer until it lands — so a case observes the hold here, at the
/// route, and reads the page only after it has let go.
type private Hold =
    { Held: Async<unit>
      LetGo: unit -> unit }

let private whereCreateIs (page: IPage) : Async<float32 * float32> =
    async {
        let! box = await (page.Locator(createButton).BoundingBoxAsync ())
        return box.X + box.Width / 2.0f, box.Y + box.Height / 2.0f
    }

let private push (page: IPage) ((x, y): float32 * float32) : Async<unit> =
    async {
        do! awaitU (page.Mouse.MoveAsync (x, y))
        do! awaitU (page.Mouse.DownAsync ())
        do! awaitU (page.Mouse.UpAsync ())
    }

/// One Manager and one page on it, with the create POST held until the BODY lets it go (or
/// until the body ends, so a route is never left hanging) and then released as asked — wide
/// enough that what happens under a held Create can be arranged and observed.
///
/// Held on a gate the case opens, never on a timer: this used to release after a fixed
/// `holdMs`, which made every read of the page a race against the clock — the wait that
/// asked `aria-busy` only ever answered once the timer had released the navigation, and on a
/// slow runner it answered "execution context was destroyed" instead, which is how master's
/// release run for #736 went red on a case the product had passed.
let private withHeldCreate
    (name: string)
    (release: Release)
    (body: Deployed -> IBrowser -> IPage -> Hold -> Async<unit>)
    : Async<unit> =
    async {
        if Directory.Exists pressDataDir then Directory.Delete (pressDataDir, true)
        let manager =
            deploy
                "the Manager"
                "node"
                // `--port 0`: the OS chooses, and the readiness line says which. The Manager is
                // handed to the body because two of its cases have to address it — one of them
                // about the browser having LEFT this port, which is the one thing here that is
                // genuinely about a number.
                [ "app/out/Main.js"; "--auth"; "localhost"; "--secrets"; "ephemeral"
                  "--port"; "0"; "--data-dir"; pressDataDir ]
                []
                (fun line -> line.Contains "management UI at")
        let mutable browserToClose : IBrowser option = None
        let mutable playwrightToDispose : IPlaywright option = None
        try
            let! pw = await (Playwright.CreateAsync ())
            playwrightToDispose <- Some pw
            let! br = await (pw.Chromium.LaunchAsync (BrowserTypeLaunchOptions (ExecutablePath = chromiumPath ())))
            browserToClose <- Some br
            let! page = await (br.NewPageAsync ())
            page.SetDefaultTimeout 30000.0f
            let evidence = watching page
            do! reporting name page evidence <| async {
                // Continuations on their own threads, never on the one that settles a source:
                // that thread is Playwright's, and a route call issued from inside the driver's
                // own dispatch is a deadlock rather than a call.
                let held = TaskCompletionSource<unit> (TaskCreationOptions.RunContinuationsAsynchronously)
                let gate = TaskCompletionSource<unit> (TaskCreationOptions.RunContinuationsAsynchronously)
                let hold =
                    { Held = Async.AwaitTask held.Task
                      LetGo = fun () -> gate.TrySetResult () |> ignore }
                let holding (route: IRoute) =
                    async {
                        held.TrySetResult () |> ignore
                        do! Async.AwaitTask gate.Task
                        match release with
                        | LetThrough -> do! awaitU (route.ContinueAsync ())
                        | AnswerNothing -> do! awaitU (route.FulfillAsync (RouteFulfillOptions (Status = 204)))
                    }
                do! awaitU (page.RouteAsync ("**/sessions", fun route ->
                        if route.Request.Method = "POST" then Async.Start (holding route)
                        else route.ContinueAsync () |> ignore))
                let! _ = await (page.GotoAsync (manager.At "/"))
                let! _ = await (page.WaitForSelectorAsync createButton)
                try
                    do! body manager br page hold
                finally
                    hold.LetGo ()
            }
        finally
            browserToClose |> Option.iter (fun b -> b.CloseAsync () |> ignore)
            playwrightToDispose |> Option.iter (fun p -> p.Dispose ())
            manager.Stop ()
    }

let pressTests =
    testList "Pressing Create (browser)" [
        testCaseAsync "a second push before the first lands is refused: one session" <|
            withHeldCreate "double create" LetThrough (fun manager _ page hold -> async {
                let posted = ResizeArray<string> ()
                page.Request.Add (fun r -> if r.Method = "POST" && r.Url.EndsWith "/sessions" then posted.Add r.Url)
                let! at = whereCreateIs page
                do! push page at
                // The first push's POST is at the route now, held: the state the second push
                // arrives into, and the one thing this case is about.
                do! hold.Held
                do! push page at
                hold.LetGo ()
                do! waitFor "the browser to have left for the new session" page (sprintf "location.port !== '%d'" manager.Port)
                Expect.equal posted.Count 1 (sprintf "two pushes, one session: %A" (List.ofSeq posted))
            })

        testCaseAsync "a rows frame under a held Create leaves it held, and under the same finger" <|
            withHeldCreate "held through a frame" AnswerNothing (fun manager br page hold -> async {
                let! at = whereCreateIs page
                do! push page at
                do! hold.Held
                // A frame, from elsewhere, while the Create is held: another reader archives
                // the seeded session, and the stream this page holds answers with the whole
                // table. (Nothing can be read off this page until the hold ends, so the hold
                // is let go — answered with nothing, which leaves the page where it was — and
                // the frame confirmed afterwards.)
                let! other = await (br.NewPageAsync ())
                let! _ = await (other.GotoAsync (manager.At "/"))
                do! awaitU (other.ClickAsync (sprintf "[%s]" Yession.App.Dom.Manager.archive))
                hold.LetGo ()
                do! waitFor "the frame to have landed here" page (sprintf "document.querySelector('[%s]') === null" Yession.App.Dom.Manager.archive)
                let! seen =
                    await (page.EvaluateAsync<string>
                            (sprintf "() => { const b = document.querySelector('%s'); return JSON.stringify({ there: !!b, held: b?.getAttribute('aria-busy') === 'true', underTheFinger: document.activeElement === b }) }" createButton))
                Expect.equal seen """{"there":true,"held":true,"underTheFinger":true}""" "the Create that was pushed is still down, and still the one under the finger"
            })
    ]

#else

// Fable (JS on Node): Playwright is a .NET driver and does not exist here, so the flows above
// are compiled out. These stubs only exist so the module compiles under Fable; they are never
// forced — the `[Browser]` need fails on Node and reports the skip itself.
let tests : Fable.Pyxpecto.Model.TestCase = testList "Browser E2E" []
let editorTests : Fable.Pyxpecto.Model.TestCase = testList "Editor rendering (browser)" []
let mountedTests : Fable.Pyxpecto.Model.TestCase = testList "Path-mounted session (browser)" []
let frontDoorTests : Fable.Pyxpecto.Model.TestCase = testList "Creating a session behind a front door (browser)" []
let frontedTests : Fable.Pyxpecto.Model.TestCase = testList "A fronted deployment, for real (browser)" []
let filterTests : Fable.Pyxpecto.Model.TestCase = testList "The management page's filters (browser)" []
let pressTests : Fable.Pyxpecto.Model.TestCase = testList "Pressing Create (browser)" []

#endif
