module Yession.Tests.Browser

// The real-browser E2E, as a Pyxpecto suite (the F# replacement for scripts/browser-e2e.fsx).
// Pyxpecto is multi-runtime: this file compiles for BOTH targets, but the browser flow only
// exists on the .NET CLR, where the Microsoft.Playwright driver lives. Under Fable (JS on
// Node) there is no Playwright, so the flow is `#if`-compiled out and a single visible case
// records where it moved. Run it with:
//
//     dotnet run --project tests/Yession.Tests/Yession.Tests.fsproj
//
// It launches two Chromium peers against a real Session (app/out/Main.js), verifies
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
open System.Threading
open System.Threading.Tasks
open Microsoft.Playwright
open Yession.App.Codecs
open Yession.App.Collab

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

// --- One Chromium per process, one context per case ---------------------------------------
//
// Every case used to launch a Chromium of its own and close it again: a driver handshake, a
// browser process and its first renderer, paid seventy-odd times a run for an isolation the
// browser already offers more cheaply. A BrowserContext is Chromium's own unit of separation —
// cookies, localStorage, IndexedDB, caches, service workers, the HTTP cache and the clock
// `page.Clock` installs are all per context — so a case that opens fresh contexts in a shared
// browser starts from exactly the nothing it started from in a browser of its own. Measured on
// the editor suite, the launches were half its time.
//
// So the browser is the process's, launched on first use, and a case borrows contexts from it
// through `withContexts`, which closes every one it opened however the case ends. The two halves
// are one verb because a case that could open a context without the close is the leak — one
// case's page still running, still holding its service worker, into the next case.
//
// One set of launch options for every case. The mDNS switch is what the WebRTC cases need
// (headless sandboxes stall ICE gathering when host candidates hide behind mDNS), and it does
// nothing a case without a peer connection could observe — so it is not worth a second browser.
//
// A browser that has DIED is launched again rather than handed out: one Chromium crash would
// otherwise turn every case after it red, and every one of those reds would be a lie about
// the case it is printed under.

let private browserLaunch () =
    BrowserTypeLaunchOptions (
        ExecutablePath = chromiumPath (),
        Args = [| "--disable-features=WebRtcHideLocalIpsWithMdns" |])

/// One at a time through the launch, so two cases asking at once get one browser between them.
let private launching = new SemaphoreSlim (1, 1)
let mutable private playwright : IPlaywright option = None
let mutable private browser : IBrowser option = None

/// The process's Chromium, launched on first use and relaunched if it has gone.
let private sharedBrowser () : Async<IBrowser> =
    async {
        do! awaitU (launching.WaitAsync ())
        try
            match browser with
            | Some live when live.IsConnected -> return live
            | _ ->
                let! pw =
                    match playwright with
                    | Some pw -> async.Return pw
                    | None ->
                        async {
                            let! pw = await (Playwright.CreateAsync ())
                            playwright <- Some pw
                            // Closed with the process. Pyxpecto ends a run with an explicit exit,
                            // so nothing after `runTests` ever runs — this is the one place left
                            // to say goodbye from. Bounded: a browser refusing to close must not
                            // hold the exit code hostage.
                            AppDomain.CurrentDomain.ProcessExit.Add (fun _ ->
                                match browser with
                                | Some b -> try b.CloseAsync().Wait (TimeSpan.FromSeconds 5.0) |> ignore with _ -> ()
                                | None -> ()
                                try pw.Dispose () with _ -> ())
                            return pw
                        }
                let! launched = await (pw.Chromium.LaunchAsync (browserLaunch ()))
                browser <- Some launched
                return launched
        finally
            launching.Release () |> ignore
    }

/// A case's share of the browser: the contexts it opened, every one closed when it ends.
type internal Contexts (browser: IBrowser) =
    let opened = ResizeArray<IBrowserContext> ()

    /// A fresh context — one person, with nothing kept from anybody before them.
    member _.Open (options: BrowserNewContextOptions option) : Async<IBrowserContext> =
        async {
            let! context =
                match options with
                | None -> await (browser.NewContextAsync ())
                | Some o -> await (browser.NewContextAsync o)
            lock opened (fun () -> opened.Add context)
            return context
        }

    /// A page in a context of its own.
    member this.Page (options: BrowserNewContextOptions option) : Async<IPage> =
        async {
            let! context = this.Open options
            return! await (context.NewPageAsync ())
        }

    member internal _.CloseAll () : Async<unit> =
        async {
            for context in lock opened (fun () -> List.ofSeq opened) do
                try do! awaitU (context.CloseAsync ()) with _ -> ()
        }

/// Run a case against the shared browser, its contexts closed however it ends.
let internal withContexts (body: Contexts -> Async<'a>) : Async<'a> =
    async {
        let! br = sharedBrowser ()
        let contexts = Contexts br
        let! outcome = Async.Catch (body contexts)
        do! contexts.CloseAll ()
        return
            match outcome with
            | Choice1Of2 value -> value
            | Choice2Of2 e -> raise e
    }

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
                                         // Where the keyboard is: a keyboard case that fails
                                         // has either pressed the wrong thing or pressed into
                                         // nothing, and only this tells which.
                                         focused: document.activeElement?.outerHTML?.slice(0, 160) ?? null,
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
/// Open a terminal the way a person does (Plan 20, stage 1): press the strip's `+` — or, in an
/// empty pane, its own button, which is the one door there (P1-4) — and take the choice if one
/// is offered.
///
/// Whether one IS offered depends on the session — with one place to put a terminal the door
/// makes one, and with a repo's sandbox up it opens a menu. So this reads the answer off the
/// control rather than guessing or waiting to see: `aria-haspopup` is exactly the promise the
/// door makes to a reader, which makes it the right thing for a test to believe too, and it is
/// rendered before any press so there is no race to lose.
let private openNewTerminal (page: IPage) : Async<unit> =
    async {
        let door = "[data-pane-new], [data-terminal-new]"
        let! _ = await (page.WaitForSelectorAsync door)
        let! asks =
            await (page.EvaluateAsync<bool>
                ("door => document.querySelector(door)?.getAttribute('aria-haspopup') === 'menu'", door))
        do! awaitU (page.Locator(door).First.ClickAsync ())
        if asks then
            let! _ = await (page.WaitForSelectorAsync "[data-sandbox-new='default']")
            do! awaitU (page.Locator("[data-sandbox-new='default']").First.ClickAsync ())
    }

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
        // A wait that settles is otherwise silent about how long it took, and a case's time
        // is the sum of its waits: said here, a slow case's log names which wait it spent it
        // on, where its duration alone names nothing.
        elif sw.Elapsed.TotalSeconds >= 1.0 then
            printfn "browser: waited %.1fs for %s" sw.Elapsed.TotalSeconds what
    }

// Browser-evaluated predicate strings: JS by necessity — they run inside Chromium via CDP.

// Read off the ATTRIBUTE, never off the words. A healthy client says nothing about being
// healthy any more — "Connected" was on three surfaces at once and is now on none — so the
// state token is the only place this can come from, which is where a markup contract belongs.
let private connected = """document.querySelector('[data-connection]')?.getAttribute('data-connection') === 'Connected'"""

/// An init script that takes away the END of ICE gathering — the `complete` state and the null
/// candidate — from every peer connection the page makes, and counts the candidates instead.
/// Its listeners are registered in the constructor, before the page can set a handler of its
/// own, so they are heard first and can stop the event there.
let private gatheringNeverEnds =
    """(() => {
         const Native = globalThis.RTCPeerConnection
         globalThis.__yessionCandidates = 0
         globalThis.RTCPeerConnection = class extends Native {
           constructor (...args) {
             super(...args)
             this.addEventListener('icecandidate', e => {
               if (e.candidate) globalThis.__yessionCandidates++
               else e.stopImmediatePropagation()
             })
             this.addEventListener('icegatheringstatechange', e => e.stopImmediatePropagation())
           }
         }
       })()"""

// Says when a connection has taken its remote description, which is the moment a handshake's
// deadline stops meaning "the session did not answer".
let private answerApplied =
    """(() => {
         const Native = globalThis.RTCPeerConnection
         globalThis.RTCPeerConnection = class extends Native {
           async setRemoteDescription (...args) {
             await super.setRemoteDescription(...args)
             globalThis.__yessionAnswered = true
           }
         }
       })()"""

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
// own Manager + session on ports the OS picks, contexts of its own in the browser the process
// shares (`withContexts`), and a fresh peer per page.
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

/// The product entry as every case here boots it: `node app/out/Main.js`, its arguments, and
/// the variables the case names — and NO ambient model credential, whatever this run carries.
///
/// No case in this suite asks for `LiveAgent`, and `SessionMain`'s documented last resort is
/// the environment: on a box that has a key, a session booted with this process's environment
/// runs a real agent turn for every message a case sends. The release gate is exactly such a
/// box (`verify` carries the LiveAgent secret to every tier), so the cases that let the
/// environment decide were spending real turns there and nowhere else — one picker case failed
/// on it outright, and the offline-reopen cases paid a live turn's start inside a wait that is
/// about something else. One builder, because a spawn site that has to remember this is how
/// four of six came not to.
///
/// Empty rather than removed, which is what `ambientCredential` reads as absent and what the
/// Node suites plant for the same reason. It reaches the session because the Manager spawns
/// one with `{...process.env, ...}` (`Spawn.fs`). Set after the case's own variables, so no
/// case can put one back by naming it.
let private productStartInfo (args: string list) (env: (string * string) list) : ProcessStartInfo =
    let psi = ProcessStartInfo "node"
    psi.ArgumentList.Add "app/out/Main.js"
    args |> List.iter psi.ArgumentList.Add
    env |> List.iter (fun (name, value) -> psi.EnvironmentVariables.[name] <- value)
    psi.EnvironmentVariables.["ANTHROPIC_API_KEY"] <- ""
    psi.EnvironmentVariables.["CLAUDE_CODE_OAUTH_TOKEN"] <- ""
    psi.UseShellExecute <- false
    psi

/// Boot the real product entry on ports the OS picks, in a data dir nothing else touches.
///
/// `--port 0` is what makes a case's host its own: the shipped default is a fixed 8321, so
/// two hosts at once — and, on a runner, two hosts in a row inside the same TIME_WAIT — would
/// be fighting over one port. The session's own address comes off the readiness line, which is
/// the only place it is stated.
///
/// `env` is what the case changes about the deployment, on top of what `check` already set for
/// the whole run (a resources profile among it).
let private startHost (env: (string * string) list) : Host =
    let ordinal = System.Threading.Interlocked.Increment hostsStarted
    let dataDir = sprintf "tests/browser/.data/host-%d-%d" (Process.GetCurrentProcess().Id) ordinal
    if Directory.Exists dataDir then Directory.Delete (dataDir, true)
    let psi =
        productStartInfo
            // Single-machine loopback trust (the shipped default `none` denies everything and
            // the login bounce would 401 before any page ever connects).
            [ "--auth"; "localhost"
              "--data-dir"; dataDir
              "--port"; "0" ]
            env
    psi.RedirectStandardOutput <- true   // stderr inherits → visible in the log
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

/// A case and the world it runs in: a host, its contexts, and `peers` first visits that have
/// settled into Connected. Everything is gone when the case ends, however it ends.
///
/// One CONTEXT per peer, never two pages in one: a peer id lives in origin-partitioned
/// localStorage, so two pages in one context are one person in two tabs rather than the two
/// collaborators a convergence case is about.
let private peersCase (env: (string * string) list) (name: string) (peers: int) (body: Host -> IPage list -> Async<unit>) =
    testCaseAsync name <|
        async {
            let host = startHost env
            let! outcome =
                Async.Catch <| withContexts (fun contexts -> async {
                    let! opened =
                        [ 1 .. peers ]
                        |> List.map (fun _ ->
                            async {
                                let! page = contexts.Page None
                                page.SetDefaultTimeout 30000.0f
                                return page, watching page
                            })
                        |> Async.Sequential
                    let opened = List.ofArray opened
                    let pages = opened |> List.map fst
                    let arranged =
                        async {
                            // A first visit, through the login bounce, to a shell that has
                            // connected. Nothing may be evaluated before that: the bounce
                            // destroys the execution context, and `connected` is only true
                            // back on the shell.
                            for page in pages do
                                let! _ = await (page.GotoAsync host.Base)
                                ()
                            for i, page in List.indexed pages do
                                do! waitFor (sprintf "peer %d to connect" (i + 1)) page connected
                            do! body host pages
                        }
                    do! reportingAll name opened arranged
                })
            // Teardown that cannot strand a host: contexts that refuse to close must not stop
            // the process being killed or its data dir going. A leaked host holds a port and a
            // session nobody will ever look at again.
            stopHost host
            match outcome with
            | Choice1Of2 () -> ()
            | Choice2Of2 e -> raise e
        }

/// A case with one peer in it.
let private sessionCase (name: string) (body: IPage -> Async<unit>) =
    peersCase [] name 1 (fun _ pages -> body pages.Head)

/// A case with one peer, on a deployment the case changes (`startHost`).
let private sessionCaseOn (env: (string * string) list) (name: string) (body: IPage -> Async<unit>) =
    peersCase env name 1 (fun _ pages -> body pages.Head)

/// A command that would run for ten minutes, sent from a fresh terminal's command line and
/// waited on until it has SAID it started — before then a ^C is the line editor's, not the
/// command's — then stopped however `stop` stops it. The case holds once the command has ended
/// and the same terminal has run one more, which only a terminal that lived through the stop
/// can do: a ^C that took the shell with it ends the long command too, and the next one never.
let private stoppedAndStillThere (stop: IPage -> Async<unit>) (page: IPage) : Async<unit> =
    async {
        do! awaitU (page.Locator("[data-content-toggle='show']").First.ClickAsync ())
        do! openNewTerminal page
        let commandLine = "[data-terminal-input^='term-draft:']:not([readonly])"
        let! _ = await (page.WaitForSelectorAsync commandLine)
        do! awaitU (page.ClickAsync commandLine)
        do! awaitU (page.Keyboard.TypeAsync "echo sta''rted; sleep 600")
        do! awaitU (page.Keyboard.PressAsync "Enter")
        do!
            waitFor
                "the long command to start"
                page
                """[...document.querySelectorAll('[data-terminal-output]')].some(o => o.textContent.includes('started'))"""
        do! stop page
        do!
            waitFor
                "the long command to end"
                page
                """[...document.querySelectorAll('[data-terminal-block]')].some(b => b.textContent.includes('sleep 600')
                                                                                    && b.getAttribute('data-terminal-block-status') !== 'running')"""
        do! awaitU (page.ClickAsync commandLine)
        do! awaitU (page.Keyboard.TypeAsync "echo al''ive")
        do! awaitU (page.Keyboard.PressAsync "Enter")
        do!
            waitFor
                "the same terminal to run the next command"
                page
                """[...document.querySelectorAll('[data-terminal-block]')].some(b => b.textContent.includes("echo al''ive")
                                                                                    && b.getAttribute('data-terminal-block-status') === 'ok')"""
    }

/// A case with one peer that also reads the SESSION's own files. Everything above asserts on
/// what a browser can see, which is the right default; this is for the one thing a browser
/// cannot answer — how much transcript there actually is — where the alternative is to assume
/// it, and assuming it is what made the reopen budget below mean different things on different
/// machines.
let private hostSessionCase (name: string) (body: Host -> IPage -> Async<unit>) =
    peersCase [] name 1 (fun host pages -> body host pages.Head)

/// A case with two, which is what convergence and presence are about.
let private sessionPair (name: string) (body: IPage -> IPage -> Async<unit>) =
    peersCase [] name 2 (fun _ pages ->
        match pages with
        | [ a; b ] -> body a b
        | other -> failwithf "expected two peers, got %d" other.Length)

/// The refusal notices a person can SEE: drawn, with something at their centre that belongs
/// to them. A notice under a shut column or behind the pane is in the document and not on the
/// screen, and "said once" is a claim about the screen.
let private refusalsOnScreen =
    """[...document.querySelectorAll('[data-command-refused]')].filter(el => {
         const box = el.getBoundingClientRect()
         if (box.width === 0 || box.height === 0) return false
         const hit = document.elementFromPoint(box.left + box.width / 2, box.top + box.height / 2)
         return !!hit && el.contains(hit)
       })"""

/// A session with no resources profile has no `default` sandbox, so New terminal is refused —
/// the press this suite can make the session refuse without running anything. The profile
/// `check` sets for the whole run is taken away for this one host.
let private refusingNewTerminal = [ "YESSION_SESSION_RESOURCES", "" ]

/// Open the pane, press New terminal there, and wait for the refusal to be on screen.
let private refusedInPane (page: IPage) : Async<unit> =
    async {
        do! awaitU (page.Locator("[data-content-toggle='show']").First.ClickAsync ())
        do! openNewTerminal page
        do! waitFor "a refusal on screen" page (sprintf "%s.length > 0" refusalsOnScreen)
    }

let tests =
    testList "Browser E2E" [
        // The press was made in the pane, so the answer is said there — and once. The
        // conversation column has a mount too, and a reader looking at the pane does not look
        // up at the conversation's header for news about the button under their hand.
        sessionCaseOn refusingNewTerminal "a New terminal refused in the pane is said in the pane, once" <|
            fun page ->
            async {
                do! refusedInPane page
                do! waitFor
                        "exactly one visible refusal, inside the pane, in the session's words"
                        page
                        (sprintf
                            """(() => {
                                 const shown = %s
                                 return shown.length === 1
                                   && !!shown[0].closest('[data-content-panel]')
                                   && shown[0].textContent.includes("'default'")
                               })()"""
                            refusalsOnScreen)
            }

        // The dismiss takes the notice out of the document with the keyboard inside it, and
        // focus left there falls to `body`: the person who pressed it is then nowhere.
        sessionCaseOn refusingNewTerminal "dismissing a refusal from the keyboard hands focus on, never to the page" <|
            fun page ->
            async {
                do! refusedInPane page
                // The refused press is an answer, and lands focus where the pane is (a frame
                // for the move to be asked, a frame for it to be made); let it, so the dismiss
                // below is pressed from where a person tabbing to it would be.
                do! awaitU (page.EvaluateAsync
                                "() => new Promise(r => requestAnimationFrame(() => requestAnimationFrame(() => requestAnimationFrame(r))))")
                do! awaitU (page.FocusAsync "[data-command-refused-dismiss]")
                do! awaitU (page.Keyboard.PressAsync "Enter")
                do! waitFor
                        "the refusal gone, and focus in the pane it sat in"
                        page
                        """!document.querySelector('[data-command-refused]')
                           && document.activeElement !== document.body
                           && !!document.activeElement?.closest('[data-content-panel]')"""
            }

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

        // Who is in the room is everyone CONNECTED, not everyone editing something: two people
        // who have only opened the session are each on the other's roster. It listed a peer
        // only while their caret was in a field, so a collaborator reading the timeline was
        // nowhere — and the cheap tier, which folds a log it was handed, cannot see the real
        // session write the join every other client is reading.
        sessionPair "a peer editing nothing is on the other peer's roster" <|
            fun pageA pageB ->
            async {
                let oneOther = """document.querySelectorAll('[data-peer-presence]').length === 1"""
                do! waitFor "A to list B" pageA oneOther
                do! waitFor "B to list A" pageB oneOther
            }

        // And the room empties as people go: the link ending is what writes the leave.
        sessionPair "a peer who goes leaves the other peer's roster" <|
            fun pageA pageB ->
            async {
                let others = """document.querySelectorAll('[data-peer-presence]').length"""
                do! waitFor "A to list B" pageA (others + " === 1")
                let! _ = await (pageB.GotoAsync "about:blank")
                do! waitFor "A to stop listing B" pageA (others + " === 0")
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
                do! awaitU (pageA.Locator("[data-content-toggle='show']").First.ClickAsync ())
                do! openNewTerminal pageA

                // Opening is a command; the terminal reaches BOTH peers as an event, so B
                // learns about it without having asked for anything — on its row in the
                // switcher, because B's strip holds only what B opened. Choosing the row is how
                // B goes to watch.
                let hasTab = """!!document.querySelector('[data-terminal-tab]')"""
                let! _ = await (pageA.WaitForFunctionAsync hasTab)
                do! awaitU (pageB.Locator("[data-content-toggle='show']").First.ClickAsync ())
                do! awaitU (pageB.Locator("[data-pane-switcher]").First.ClickAsync ())
                let! _ = await (pageB.WaitForSelectorAsync "[data-content-list] [data-terminal-list-row]")
                do! awaitU (pageB.Locator("[data-content-list] [data-terminal-list-row]").First.ClickAsync ())
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

                // And its OUTPUT arrived — over the terminal frames on both, through the same
                // fold either way.
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

        // While somebody else holds a terminal's keyboard the session HOLDS its queue rather
        // than refusing it, so a watcher can still say the next command and it runs the moment
        // the terminal is handed back. That only works if the watcher has somewhere to write
        // it: the lease bar used to stand in place of the command line.
        Tag.needs "a command that really runs" [ Tag.Browser; Tag.Native; Tag.Srt ] (fun () ->
        sessionPair "a watcher queues a command during a lease and it runs on hand-back" <|
            fun pageA pageB ->
            async {
                do! awaitU (pageA.Locator("[data-content-toggle='show']").First.ClickAsync ())
                do! openNewTerminal pageA
                let hasTab = """!!document.querySelector('[data-terminal-tab]')"""
                let! _ = await (pageA.WaitForFunctionAsync hasTab)
                do! awaitU (pageB.Locator("[data-content-toggle='show']").First.ClickAsync ())
                do! awaitU (pageB.Locator("[data-pane-switcher]").First.ClickAsync ())
                let! _ = await (pageB.WaitForSelectorAsync "[data-content-list] [data-terminal-list-row]")
                do! awaitU (pageB.Locator("[data-content-list] [data-terminal-list-row]").First.ClickAsync ())
                let! _ = await (pageB.WaitForFunctionAsync hasTab)

                // A takes the keyboard; B sees whose it is.
                let! _ = await (pageA.WaitForSelectorAsync "[data-terminal-take]")
                do! awaitU (pageA.Locator("[data-terminal-take]").First.ClickAsync ())
                do! waitFor "B to see the lease" pageB "!!document.querySelector('[data-terminal-lease]')"

                // B writes the next command anyway, and sends it the way a person does.
                let line = "[data-terminal-input^='term-draft:']:not([readonly])"
                let! _ = await (pageB.WaitForSelectorAsync line)
                do! awaitU (pageB.ClickAsync line)
                do! awaitU (pageB.Keyboard.TypeAsync "echo queued-behind-a-lease")
                do! awaitU (pageB.Keyboard.PressAsync "Enter")
                do! waitFor
                        "B's command held for the terminal, not run and not refused"
                        pageB
                        (sprintf "!!document.querySelector('[%s=\"%s\"]')"
                            Yession.App.Dom.Hooks.terminalQueuedStatus Yession.App.Dom.Text.queuedAwaitingTerminal)

                // A hands it back, and what B queued runs — for both of them.
                do! awaitU (pageA.ClickAsync "[data-terminal-release]")
                let ran =
                    """[...document.querySelectorAll('[data-terminal-block]')]
                         .some(b => b.textContent.includes('echo queued-behind-a-lease')
                                 && b.getAttribute('data-terminal-block-status') === 'ok')"""
                do! waitFor "the queued command to have run, on A" pageA ran
                do! waitFor "the queued command to have run, on B" pageB ran
            })

        // Reported from a live session: `+`, a command, then the command's chip in the chat —
        // and the strip went from `[TERMINAL]` to `[ECHO ONE]`, the live terminal gone from it
        // and reachable again only through the list. A chip opens a PREVIEW over the terminal
        // its command ran in (P2-1): the strip is terminals, and it does not change.
        Tag.needs "a command that really runs" [ Tag.Browser; Tag.Native; Tag.Srt ] (fun () ->
        sessionCase "the first chip tapped opens a preview, and the live terminal stays the strip" <|
            fun page ->
            async {
                // A fresh session offers its launch card. Put it away the way a person who is
                // not choosing a repo would; it stays away for the session.
                let! _ = await (page.WaitForSelectorAsync "[data-repo-picker-dismiss]")
                do! awaitU (page.ClickAsync "[data-repo-picker-dismiss]")
                let! _ = await (page.WaitForFunctionAsync "!document.querySelector('[data-repo-picker]')")
                do! awaitU (page.Locator("[data-content-toggle='show']").First.ClickAsync ())
                do! openNewTerminal page
                let composerInput = "[data-terminal-input^='term-draft:']:not([readonly])"
                let! _ = await (page.WaitForSelectorAsync composerInput)
                do! awaitU (page.ClickAsync composerInput)
                do! awaitU (page.Keyboard.TypeAsync "echo one")
                do! awaitU (page.ClickAsync "[data-terminal-send]")
                let! _ = await (page.WaitForSelectorAsync "[data-chat-block]")
                do! awaitU (page.ClickAsync "[data-chat-block]")
                let! _ = await (page.WaitForSelectorAsync "[data-pane-preview^='block:']")
                let! strip =
                    await (page.EvaluateAsync<string[]> (
                            """() => [...document.querySelectorAll('[data-pane-tab]')].map(t => t.getAttribute('data-pane-tab'))"""))
                Expect.equal
                    (strip |> Array.toList |> List.map (fun key -> key.Split(':').[0]))
                    [ "terminal" ]
                    (sprintf "the terminal the press opened, and nothing beside it; the strip holds: %s" (String.Join (", ", strip)))
            })

        // A reload brings a person back to what they had (P0-4): the same terminals in the
        // strip, the one on top, and a column with width. A preview is NOT brought back: it
        // was a glance, and the terminal it was laid over is what comes back on top.
        Tag.needs "a command that really runs" [ Tag.Browser; Tag.Native; Tag.Srt ] (fun () ->
        sessionCase "a reload brings back the strip, the selection and the open pane" <|
            fun page ->
            async {
                let! _ = await (page.WaitForSelectorAsync "[data-repo-picker-dismiss]")
                do! awaitU (page.ClickAsync "[data-repo-picker-dismiss]")
                let! _ = await (page.WaitForFunctionAsync "!document.querySelector('[data-repo-picker]')")
                do! awaitU (page.Locator("[data-content-toggle='show']").First.ClickAsync ())
                // Three terminals, and the MIDDLE one chosen. Not the first: with nothing
                // remembered the pane lands on the strip's first open terminal, so a kept
                // first tab is the answer a reload gives whether or not it remembered — which
                // is how this case stayed green over a reload that came back to the first tab
                // every time. Not the last either, which is where the last press left it.
                do! openNewTerminal page
                do! waitFor "a terminal in the strip" page "document.querySelectorAll('[data-pane-tab]').length === 1"
                do! openNewTerminal page
                do! waitFor "a second terminal in the strip" page "document.querySelectorAll('[data-pane-tab]').length === 2"
                do! openNewTerminal page
                do! waitFor "a third terminal in the strip" page "document.querySelectorAll('[data-pane-tab]').length === 3"
                let middle = "[data-pane-tab]:nth-child(2)"
                let! chosen = await (page.GetAttributeAsync (middle, "data-pane-tab"))
                do! awaitU (page.ClickAsync middle)
                let chosenOnTop =
                    sprintf "document.querySelector(\"[data-pane-tab='%s']\")?.getAttribute('aria-selected') === 'true'" chosen
                do! waitFor "the middle terminal to be on top" page chosenOnTop
                // A command in it, and that command's preview laid over it — which is not what
                // comes back; the terminal under it is.
                let composerInput = "[data-terminal-input^='term-draft:']:not([readonly])"
                let! _ = await (page.WaitForSelectorAsync composerInput)
                do! awaitU (page.ClickAsync composerInput)
                do! awaitU (page.Keyboard.TypeAsync "echo kept")
                do! awaitU (page.ClickAsync "[data-terminal-send]")
                let! _ = await (page.WaitForSelectorAsync "[data-chat-block]")
                do! awaitU (page.ClickAsync "[data-chat-block]")
                let! _ = await (page.WaitForSelectorAsync "[data-pane-preview]")

                // What was on top the FIRST moment the strip held all three again — recorded by
                // the page itself, from before the reload paints anything, because the fault
                // this pins was a window: the strip came back from the local store at once and
                // the choice only once catch-up had finished, so a reload showed the first tab
                // on top for as long as catch-up took. Waiting for the end of it, as this case
                // used to, waited the fault out.
                do!
                    awaitU (
                        page.AddInitScriptAsync
                            """new MutationObserver((_, watching) => {
                                 const tabs = document.querySelectorAll('[data-pane-tab]')
                                 if (tabs.length !== 3) return
                                 const top = [...tabs].find(t => t.getAttribute('aria-selected') === 'true')
                                 window.__firstOnTop = top ? top.getAttribute('data-pane-tab') : 'nothing'
                                 watching.disconnect()
                               }).observe(document, { subtree: true, childList: true, attributes: true })""")
                let! _ = await (page.ReloadAsync ())
                do! waitFor "the reloaded page to connect" page connected
                do! waitFor "all three terminals to be back in the strip" page "document.querySelectorAll('[data-pane-tab]').length === 3"
                do! waitFor "the chosen terminal to be on top again" page chosenOnTop
                let! firstOnTop = await (page.EvaluateAsync<string> "() => window.__firstOnTop ?? 'never recorded'")
                Expect.equal firstOnTop chosen "on top from the first moment the strip was back, not once catch-up ended"
                do! waitFor "no preview, which was a glance" page "!document.querySelector('[data-pane-preview]')"
                do! waitFor
                        "the pane to be open"
                        page
                        "(document.querySelector('[data-content-panel]')?.getBoundingClientRect().width ?? 0) > 1"
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
                //
                // A reload shuts the column, and a shut column is `inert` — its composer cannot
                // take focus at all, by keyboard or by script — so the column is shown first.
                let seed (first: int) (last: int) =
                    async {
                        let! shut =
                            await (page.EvaluateAsync<bool> "() => !!document.querySelector(\"[data-content-toggle='show']\")")
                        if shut then do! awaitU (page.Locator("[data-content-toggle='show']").First.ClickAsync ())
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

                do! awaitU (page.Locator("[data-content-toggle='show']").First.ClickAsync ())
                do! openNewTerminal page
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

        // What one render costs does not grow with the number of terminals open.
        //
        // The after-render pass measures each open terminal's box, and it used to FIND each
        // one by asking the whole document for it — a compound attribute selector, once per
        // open terminal, per render. The pane draws one terminal at a time, so every other
        // question was a MISS, and a miss is the expensive answer: neither clause is indexed,
        // so the walk visits every node and cannot stop early. Measured on a session of 20,650
        // events (54k nodes, twelve terminals open): 616 of those scans costing 5.3 seconds, a
        // third of all the CPU a cold open spent, and 20s before the page stopped changing on
        // a phone-speed CPU. One scan that finds them all costs 10s.
        //
        // What this pins is the SHAPE — scans per render, which is one however many terminals
        // are open, and was one per terminal — and deliberately not a duration: a millisecond
        // budget on a shared runner is the flaky test this repository warns about, while a
        // count is the same number on every box.
        //
        // Both counts are per DOCUMENT, so the reload is what makes them comparable: a fresh
        // document starts them at zero and a reopen is a burst of renders worth measuring.
        //
        // `Srt` because opening a terminal starts a shell in the session's work sandbox: on a
        // box that cannot host one no terminal ever appears, and this would wait out its
        // timeout rather than skip.
        Tag.needs "measuring several terminals" [ Tag.Browser; Tag.Native; Tag.Srt ] (fun () ->
        sessionCase "measuring the terminals costs one look at the document, however many are open" <|
            fun page ->
            async {
                let opened = 4
                let enough = sprintf "document.querySelectorAll('[data-terminal-tab]').length >= %d" opened
                do! awaitU (page.Locator("[data-content-toggle='show']").First.ClickAsync ())
                for _ in 1 .. opened do
                    do! openNewTerminal page
                do! await (page.WaitForFunctionAsync enough) |> Async.Ignore

                let! _ = await (page.ReloadAsync ())
                do! waitFor "the reopened session to connect" page connected
                do! waitFor (sprintf "the reopened session to offer its %d terminals" opened) page enough

                // Settled when the render count has stopped moving, which is also when the
                // scans have: they are made in the same pass.
                let! settled =
                    await (page.EvaluateAsync<string> """() => new Promise(resolve => {
                      let last = -1, still = 0, waited = 0
                      const tick = () => {
                        const n = globalThis.__yessionRenders ?? -1
                        if (n === last) still++ ; else { still = 0; last = n }
                        waited += 250
                        if (still >= 6 || waited >= 30000)
                          resolve([n, globalThis.__yessionViewportScans ?? -1, still >= 6].join(','))
                        else setTimeout(tick, 250)
                      }
                      tick()
                    })""")
                match settled.Split ',' with
                | [| r; q; s |] ->
                    let renders, scans = int r, int q
                    // Anti-vacuity, both ways this passes while measuring nothing: a counter
                    // the app stopped publishing (which reads -1, or 0 for a pass that never
                    // ran), and a page still rendering when time ran out.
                    if renders <= 0 then
                        failwithf
                            "the page reports %d renders — `app/browser/Render.fs` publishes \
                             `globalThis.__yessionRenders` and this budget means nothing without it"
                            renders
                    if scans <= 0 then
                        failwithf
                            "the page reports %d viewport scans — `app/browser/Screens.fs` publishes \
                             `globalThis.__yessionViewportScans` and this budget means nothing without it"
                            scans
                    if s <> "true" then
                        failwithf
                            "the reopened session was still rendering after 30s (%d renders, %d scans)"
                            renders scans
                    let perRender = float scans / float renders
                    printfn "  reopen with %d terminals open: %d scans over %d renders — %.2f per render"
                            opened scans renders perRender
                    // One per render, plus whatever the resize observer added — it fires on a
                    // box changing rather than on a render, and a load moves boxes. The line
                    // sits at two, which is well over what one scan a render plus a settling
                    // layout costs and well under the FOUR this case opens: a scan that goes
                    // back to being per-terminal cannot pass it, and neither can one that
                    // creeps to a second question per pass.
                    let budget = 2.0
                    if perRender > budget then
                        failwithf
                            "the after-render pass asked the document where the terminal boxes are %.2f times \
                             per render with %d terminals open (%d scans over %d renders); the budget is %.1f. \
                             Measuring is finding them ONCE — see `viewports` in `app/browser/Screens.fs`."
                            perRender opened scans renders budget
                | _ -> failwithf "the counters answered '%s', which is not two counts and a verdict" settled
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
                do! awaitU (page.Locator("[data-content-toggle='show']").First.ClickAsync ())
                do! openNewTerminal page
                do! openNewTerminal page
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

        // A press for a terminal is answered a round trip later, by an event that takes the
        // pressed control's place (the empty pane's press) or leaves it standing (`+`). Either
        // way the keyboard goes where the new terminal takes typing. `Srt` for the reason the
        // case above gives: no sandbox, no terminal.
        Tag.needs "a terminal to arrive" [ Tag.Browser; Tag.Native; Tag.Srt ] (fun () ->
        sessionCase "a new terminal takes the keyboard" <|
            fun page ->
            async {
                do! awaitU (page.Locator("[data-content-toggle='show']").First.ClickAsync ())
                do! openNewTerminal page
                do! await (page.WaitForSelectorAsync "[data-terminal-tab]") |> Async.Ignore
                let! tabs = terminalTabs page
                do!
                    await (page.WaitForFunctionAsync (
                            "sel => document.activeElement?.matches(sel) === true",
                            box (commandLine tabs.[0])))
                    |> Async.Ignore
            })

        // Stopping one command is not ending the terminal. Before there was a Stop, the only
        // way out of a `sleep 600` was killing the terminal, its shell and its working
        // directory with it.
        Tag.needs "a command to stop" [ Tag.Browser; Tag.Native; Tag.Srt ] (fun () ->
        sessionCase "Stop on a running command ends it, and the terminal runs the next one" <|
            stoppedAndStillThere (fun page ->
                awaitU (page.Locator("[data-terminal-block-stop]").First.ClickAsync ())))

        // With nothing typed, Ctrl-C at the command line has the meaning it has in a terminal.
        Tag.needs "a command to stop" [ Tag.Browser; Tag.Native; Tag.Srt ] (fun () ->
        sessionCase "Ctrl-C in an empty command line ends the running command, and the terminal runs the next one" <|
            stoppedAndStillThere (fun page ->
                async {
                    do! awaitU (page.ClickAsync "[data-terminal-input^='term-draft:']:not([readonly])")
                    do! awaitU (page.Keyboard.PressAsync "Control+c")
                }))

        // The kill is pressed on a row of the switcher, and the close that answers it takes
        // that row's kill away — so focus lands on the row that takes its place, rather than
        // on `body` with the switcher still under the reader's hand.
        Tag.needs "two terminals to kill one of" [ Tag.Browser; Tag.Native; Tag.Srt ] (fun () ->
        sessionCase "killing a terminal from the switcher lands on the next one" <|
            fun page ->
            async {
                do! awaitU (page.Locator("[data-content-toggle='show']").First.ClickAsync ())
                do! openNewTerminal page
                do! openNewTerminal page
                do!
                    await (page.WaitForFunctionAsync
                            "document.querySelectorAll('[data-terminal-tab]').length >= 2")
                    |> Async.Ignore
                let! tabs = terminalTabs page
                let one, two = tabs.[0], tabs.[1]
                do! awaitU (page.ClickAsync "[data-pane-switcher]")
                let kill = sprintf "[data-content-list] [data-terminal-close='%s']" one
                do! awaitU (page.FocusAsync kill)
                // Two presses: the first arms the kill, the second, on the same control,
                // performs it (`KillArmed`).
                do! awaitU (page.Keyboard.PressAsync "Enter")
                do!
                    await (page.WaitForSelectorAsync (sprintf "%s[data-terminal-close-armed='true']" kill))
                    |> Async.Ignore
                do! awaitU (page.Keyboard.PressAsync "Enter")
                do!
                    await (page.WaitForFunctionAsync ("sel => !document.querySelector(sel)", box kill))
                    |> Async.Ignore
                do!
                    await (page.WaitForFunctionAsync (
                            "id => document.activeElement?.closest('[data-content-list]') && document.activeElement?.getAttribute('data-terminal-list-row') === id",
                            box two))
                    |> Async.Ignore
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

        // The same browser is the same person. The peer id was always kept per browser; the
        // name was drawn fresh on every load, so a reload renamed the person and the chat then
        // attributed what they had done to somebody new. Under `--auth localhost` nothing else
        // names a peer, so the name a reload comes back with is the one this browser kept.
        sessionCase "a reload keeps the peer's name" <|
            fun page ->
            async {
                let ownName () =
                    page.EvaluateAsync<string> """() => document.querySelector('[data-display-name]')?.textContent ?? ''"""
                    |> await
                let! before = ownName ()
                Expect.isFalse (String.IsNullOrWhiteSpace before) "a connected peer shows its own name"
                let! _ = await (page.ReloadAsync ())
                do! waitFor "the reloaded page to connect" page connected
                let! after = ownName ()
                Expect.equal after before "the reloaded page wears the name it had before"
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

        // The offer does not wait for gathering to say it has finished. Headless Chromium here
        // never says so — its one host candidate arrives inside 20ms and `iceGatheringState`
        // stays `gathering` for as long as anybody waits — and waiting for it cost every
        // connect the whole cap, a second and a half, in every case in this file and for every
        // person opening a session (`Client.Gathering`).
        //
        // A browser that DOES finish gathering promptly would pass this case without the quiet
        // window ever firing, so the end of gathering is taken away first: the page's own
        // handlers never hear it, on any box. And the page's time is the case's to turn — paused,
        // and moved a window at a time to a total short of the cap — so the cap cannot be what
        // sends the offer, and a client that waits for it sends nothing here at all.
        sessionCase "the offer goes once the candidates fall quiet, without waiting for gathering to end" <|
            fun page ->
            async {
                do! awaitU (page.AddInitScriptAsync gatheringNeverEnds)
                let offered = TaskCompletionSource<unit> (TaskCreationOptions.RunContinuationsAsynchronously)
                let signal = "**" + Yession.App.RelativeUrl.under "" (Yession.App.SessionRoute.relative Yession.App.SessionRoute.Signal)
                do! awaitU (page.RouteAsync (signal, fun route ->
                        if route.Request.Method = "POST" then offered.TrySetResult () |> ignore
                        route.ContinueAsync () |> ignore))
                let start = DateTime (2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                do! awaitU (page.Clock.InstallAsync (ClockInstallOptions (TimeDate = start)))
                do! awaitU (page.Clock.PauseAtAsync (start.AddSeconds 1.0))
                let! _ = await (page.ReloadAsync ())
                // Candidates arrive on the browser's network thread, which no page clock owns;
                // until one has, there is no window for page time to close.
                do! waitFor "the reloaded page to gather a candidate" page "(globalThis.__yessionCandidates ?? 0) > 0"
                let window = int64 Yession.App.Client.Gathering.quiet.TotalMilliseconds + 1L
                let cap = int64 Yession.App.Client.Gathering.cap.TotalMilliseconds
                // A window at a time, because a later candidate restarts it: a box with several
                // interfaces may still be delivering when the first window closes.
                let mutable spent = 0L
                while not offered.Task.IsCompleted && spent + window < cap do
                    do! awaitU (page.Clock.RunForAsync window)
                    spent <- spent + window
                    do! Async.AwaitTask (Task.WhenAny (offered.Task, Task.Delay 250)) |> Async.Ignore
                // No page time passes from here, so only a send already made can still arrive.
                do! Async.AwaitTask (Task.WhenAny (offered.Task, Task.Delay 10000)) |> Async.Ignore
                Expect.isTrue
                    offered.Task.IsCompleted
                    (sprintf "an offer posted inside %dms of page time, short of the %dms cap" spent cap)
            }

        // A session that answers but cannot be reached is not a session that said nothing. The
        // route between the two is taken away on the wire: the offer reaches the session with
        // none of the browser's candidates, so the session cannot find the browser either, and
        // the answer comes back with every address of the session's moved into TEST-NET-1
        // (RFC 5737), which routes nowhere. Signalling still succeeds, so the only fault left
        // is the one the network has, and the handshake has to say THAT rather than that the
        // session was silent: the remedy is an overlay or a relay, never the session.
        //
        // The deadline is the page's, so the case moves it rather than waiting it out — once
        // the answer is in, which is the fact the verdict turns on. What is read is the
        // handshake's own line: the person sees the same fault only after the transport's
        // retries are spent, a minute of real time this case has no reason to spend.
        sessionCase "a handshake whose session answered but could not be reached settles as unrouted" <|
            fun page ->
            async {
                let signal = "**" + Yession.App.RelativeUrl.under "" (Yession.App.SessionRoute.relative Yession.App.SessionRoute.Signal)
                let candidate = System.Text.RegularExpressions.Regex @"a=candidate:[^\\""]*\\r\\n"
                let address = System.Text.RegularExpressions.Regex @"(a=candidate:\S+ \d+ \S+ \d+ )\S+"
                let settled = TaskCompletionSource<string> (TaskCreationOptions.RunContinuationsAsynchronously)
                page.Console.Add (fun message ->
                    if message.Text.StartsWith "yession/link: handshake" then settled.TrySetResult message.Text |> ignore)
                do! awaitU (page.RouteAsync (signal, fun route ->
                        task {
                            if route.Request.Method = "POST" then
                                let offer = candidate.Replace (route.Request.PostData, "")
                                let! response = route.FetchAsync (RouteFetchOptions (PostData = Text.Encoding.UTF8.GetBytes offer))
                                let! answer = response.TextAsync ()
                                do! route.FulfillAsync (
                                        RouteFulfillOptions (
                                            Response = response,
                                            Body = address.Replace (answer, "${1}192.0.2.1")))
                            else
                                do! route.ContinueAsync ()
                        } :> Task))
                do! awaitU (page.AddInitScriptAsync answerApplied)
                do! awaitU (page.Clock.InstallAsync ())
                let! _ = await (page.ReloadAsync ())
                do! waitFor "the session's answer to be applied" page "globalThis.__yessionAnswered === true"
                do! awaitU (page.Clock.FastForwardAsync 10000L)
                do! Async.AwaitTask (Task.WhenAny (settled.Task, Task.Delay 10000)) |> Async.Ignore
                Expect.isTrue settled.Task.IsCompleted "the handshake settled"
                Expect.stringContains settled.Task.Result "handshake unrouted" "an answered handshake that never opened is unrouted, not silent"
            }
    ]

// --- The host-free editor rendering E2E ([Browser], no Native) ---------------------------
// Serves the static harness (app/browser/EditorHarness.fs, esbuilt to tests/browser/out/) and
// drives one Chromium page. No Session, no WebRTC — so this runs wherever Chromium
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

/// One editor case: a served harness, a context, a page that is being LISTENED to, and the
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
    (context: BrowserNewContextOptions option)
    (name: string)
    (body: IPage -> Async<unit>)
    =
    testCaseAsync name <|
        async {
            let server = serveStatic harnessRoot
            let! outcome =
                Async.Catch <| withContexts (fun contexts -> async {
                    let! page = contexts.Page context
                    page.SetDefaultTimeout 15000.0f
                    let evidence = watching page
                    let! _ = await (page.GotoAsync (server.At "/"))
                    do! reporting name page evidence (body page)
                })
            server.Stop ()
            match outcome with
            | Choice1Of2 () -> ()
            | Choice2Of2 e -> raise e
        }

/// A case at the browser's own window size.
let private editorCase = editorCaseOn None

/// Wait for the focus move a chip's click asks for. Opening from the chat hands focus to the
/// pane (`DomMove.FocusPane`), a frame AFTER the render — so a case that goes on to put focus
/// somewhere itself, before that frame, has its focus taken back from under it by the move
/// it did not wait for. Which of the two lands last is timing, and a slower runner lost it.
let private focusReachedPane (page: IPage) : Async<unit> =
    waitFor "focus to follow the chip into the pane" page
        "document.activeElement?.hasAttribute('data-pane-panel') === true"

/// A page of events as the session sends them — one envelope a line — at the offsets given.
let private foldBody (events: (int64 * Yession.Domain.SessionEvent) list) : string =
    let expect r = Result.defaultWith failwith r
    let line (offset: int64, event: Yession.Domain.SessionEvent) =
        let envelope : Yession.Domain.EventEnvelope<Yession.Domain.SessionEvent> =
            { EventId = Yession.Domain.EventId.fresh ()
              SessionId = Yession.Domain.SessionId.create "harness" |> expect
              Offset = Yession.Domain.EventOffset.create offset |> expect
              Actor = Yession.Domain.ActorRef.Session
              Timestamp = DateTimeOffset.UtcNow
              Event = event }
        Yession.Codecs.Codec.toString Events.sessionEventEnvelope envelope
    events |> List.map line |> String.concat "\n"

/// Events folded into the harness as one page, as the session would send them
/// (`window.__fold`), at the offsets given.
let private foldHarness (page: IPage) (events: (int64 * Yession.Domain.SessionEvent) list) : Async<unit> =
    async { do! awaitU (page.EvaluateAsync ("body => window.__fold(body)", box (foldBody events))) }

let private harnessTerminal (id: string) : Yession.Domain.TerminalId =
    Yession.Domain.TerminalId.create id |> Result.defaultWith failwith

/// The harness's peer, holding `term-harness`'s keyboard with it focused, and then bob taking
/// it from them — the release-then-take a steal is, as the session would send it.
let private stolenFromHarness (page: IPage) : Async<unit> =
    async {
        let peer (name: string) =
            Yession.Domain.ActorRef.PeerRef (Yession.Domain.PeerId.create name |> Result.defaultWith failwith)
        do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
        do! awaitU (page.ClickAsync "#shell [data-terminal-take='term-harness']")
        let! _ =
            await (page.WaitForFunctionAsync
                """document.activeElement?.getAttribute('data-terminal-keys') === 'term-harness'""")
        do!
            foldHarness page [
                90L,
                Yession.Domain.SessionEvent.TerminalLeaseReleased
                    { Yession.Domain.Terminals.TerminalLeaseReleased.TerminalId = harnessTerminal "term-harness"
                      Yession.Domain.Terminals.TerminalLeaseReleased.Was = peer "ada"
                      Yession.Domain.Terminals.TerminalLeaseReleased.Reason = Yession.Domain.Terminals.LeaseStolen (peer "bob")
                      Yession.Domain.Terminals.TerminalLeaseReleased.ToSeq = 0 }
                91L,
                Yession.Domain.SessionEvent.TerminalLeaseTaken
                    { Yession.Domain.Terminals.TerminalLeaseTaken.TerminalId = harnessTerminal "term-harness"
                      Yession.Domain.Terminals.TerminalLeaseTaken.By = peer "bob"
                      Yession.Domain.Terminals.TerminalLeaseTaken.FromSeq = 0 } ]
    }

/// More terminals in the harness's strip: four opened by its own peer, folded in as the
/// session would send them, each becoming a tab as a terminal this reader pressed for does.
/// With the harness's two that is six, which is more than the strip has room for at the pane's
/// default width — the cases that need an overflowing strip assert that before anything else.
/// (These used to be chips, one tab each; a chip opens a preview now, never a tab.)
let private openManyTerminals (page: IPage) : Async<unit> =
    async {
        let opened (id: string) =
            Yession.Domain.SessionEvent.TerminalOpened
                { Yession.Domain.Terminals.TerminalOpened.TerminalId = harnessTerminal id
                  Yession.Domain.Terminals.TerminalOpened.OpenedBy =
                    Yession.Domain.ActorRef.PeerRef (Yession.Domain.PeerId.create "ada" |> Result.defaultWith failwith)
                  Yession.Domain.Terminals.TerminalOpened.Title = Yession.Domain.Terminals.TerminalTitle.fromProse id
                  Yession.Domain.Terminals.TerminalOpened.Sandbox = None
                  Yession.Domain.Terminals.TerminalOpened.Renewable = false }
        do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
        // Showing the pane puts focus on the command line a frame later; the cases after this
        // put focus somewhere themselves, and must not have it taken back by a move they did
        // not wait for.
        do! waitFor "focus to land on the command line" page
                (sprintf "document.activeElement?.matches(%s) === true" (System.Text.Json.JsonSerializer.Serialize (commandLine "term-harness")))
        do! foldHarness page [ for i in 0 .. 3 -> 80L + int64 i, opened (sprintf "term-more-%d" i) ]
        do! waitFor "six tabs in the strip" page "document.querySelectorAll('#shell [data-pane-strip] [role=tab]').length === 6"
        // The column OPENS by animating its width, and a case that measures what is on screen
        // in it measures a narrower pane until that has finished — its right-hand controls
        // clipped away by the column's own edge for the length of the transition.
        do! awaitU (
                page.EvaluateAsync
                    """() => Promise.all(
                         document.getAnimations()
                           .filter(a => a.effect && a.effect.getComputedTiming().iterations !== Infinity)
                           .map(a => a.finished.catch(() => null)))""")
    }

/// Whether the strip holds more tabs than it shows — the precondition every scrolling case
/// asserts first, because on a strip that fits, "in view" is true of everything.
let private stripOverflows =
    """() => { const s = document.querySelector('#shell [data-pane-strip]'); return s.scrollWidth > s.clientWidth + 1 }"""

/// A chip in the chat pressed, and the preview it opens settled on screen — the column's
/// opening animation finished, so what is measured is where things land, not where they pass.
let private previewSettled (page: IPage) : Async<unit> =
    async {
        do! awaitU (page.ClickAsync "#shell [data-chat-block]")
        let! _ = await (page.WaitForSelectorAsync "#shell [data-pane-preview]")
        do! awaitU (
                page.EvaluateAsync
                    """() => Promise.all(
                         document.getAnimations()
                           .filter(a => a.effect && a.effect.getComputedTiming().iterations !== Infinity)
                           .map(a => a.finished.catch(() => null)))""")
    }

/// Whether the element an expression names lies wholly inside the strip's box, sideways.
let private insideStrip (element: string) =
    sprintf
        """() => {
             const s = document.querySelector('#shell [data-pane-strip]').getBoundingClientRect()
             const t = (%s).getBoundingClientRect()
             return t.left >= s.left - 0.5 && t.right <= s.right + 0.5
           }"""
        element


/// A case at a stated viewport. `ViewportSize` alone, never `IsMobile`: that additionally asks
/// Chromium to fit the layout to a device window, which measured here lands at 648px rather
/// than 390 — the very lie the ui-exploration skill warns about, arriving through another door.
let private editorCaseIn (width: int) (height: int) =
    editorCaseOn (Some (BrowserNewContextOptions (ViewportSize = ViewportSize (Width = width, Height = height))))

/// A case on a TOUCH screen: the same honest viewport, and `HasTouch`, which is what takes
/// hover away — Chromium answers `(hover: none)` for a touch device, as a phone does. Without
/// it this browser is a phone-sized window with a mouse over it, and a rule written for a
/// device that cannot hover is a rule no case here ever sees applied.
let private editorCaseOnTouch (width: int) (height: int) =
    editorCaseOn (
        Some (
            BrowserNewContextOptions (
                ViewportSize = ViewportSize (Width = width, Height = height),
                HasTouch = true)))

/// Reading why a credential stopped working leaves the button that fixes it where it was.
/// The prompt used to be one wrapping row, so its button sat under a closed reason and beside
/// an open one — the control a person came for moved the moment they read the reason. Measured
/// against the prompt's own box, so the page scrolling under it is not a move; opened by a
/// real click on the summary, because the browser's own toggle is what grows the reason.
let private signInButtonHoldsStill (width: int) (height: int) =
    editorCaseIn width height (sprintf "opening a sign-in prompt's reason does not move its button at %dpx" width) <| fun page ->
        async {
            do! awaitU (page.EvaluateAsync "() => window.__signInLost()")
            let! _ = await (page.WaitForSelectorAsync "#shell [data-signin-required] [data-signin-again]")
            // Settled first: the side panes slide into place as the shell lays out, and the
            // column between them — the prompt's width — is still changing until they land.
            let settled () =
                awaitU (
                    page.EvaluateAsync
                        """() => Promise.all(
                             document.getAnimations()
                               .filter(a => a.effect && a.effect.getComputedTiming().iterations !== Infinity)
                               .map(a => a.finished.catch(() => null)))""")
            do! settled ()
            let at =
                """() => {
                     const prompt = document.querySelector('#shell [data-signin-required]')
                     prompt.scrollIntoView({ block: 'start' })
                     const p = prompt.getBoundingClientRect()
                     const b = prompt.querySelector('[data-signin-again]').getBoundingClientRect()
                     return [b.left - p.left, b.top - p.top, b.width, b.height].map(Math.round).join(',')
                   }"""
            let! closed = await (page.EvaluateAsync<string> at)
            do! awaitU (page.ClickAsync "#shell [data-signin-required] [data-detail] summary")
            let! _ = await (page.WaitForSelectorAsync "#shell [data-signin-required] [data-detail][open]")
            do! settled ()
            let! opened = await (page.EvaluateAsync<string> at)
            Expect.equal opened closed "the button's offset and size in the prompt (left,top,width,height)"
        }

/// Where a mounted player's terminal is against the box it was mounted into and against its
/// own control bar, as `[top, bottom, left, right]` for each — read when a fit check fails, so
/// a red case says by how much and on which side rather than only that a wait ran out.
let private replayBoxes (mount: string) =
    sprintf
        """() => {
             const host = document.querySelector(%s)
             const box = el => { const r = el.getBoundingClientRect(); return [r.top, r.bottom, r.left, r.right].map(Math.round) }
             const term = host && host.querySelector('.ap-term')
             const bar = host && host.querySelector('.ap-control-bar')
             return JSON.stringify({ host: host && box(host), term: term && box(term), bar: bar && box(bar) })
           }"""
        (System.Text.Json.JsonSerializer.Serialize mount)

/// Whether every line and every column of a mounted player's terminal is where a reader can
/// see it: inside the box it was mounted into, and its LINES clear of the player's own control
/// bar. `.ap-term` is the terminal as the player drew it, so this is the drawn size, not the one
/// the player computed — and the two part company exactly when the player measured its
/// character cell wrong, which shows as columns running off the side and the last line sliding
/// under the bar. Its lines, not its box: the box ends in a margin of the terminal's own ground
/// (its bottom border), which the bar's top rule overlaps by design.
let private replayFits (mount: string) =
    sprintf
        """() => {
             const host = document.querySelector(%s)
             const term = host && host.querySelector('.ap-term')
             if (!term) return false
             const bar = host.querySelector('.ap-control-bar')
             const h = host.getBoundingClientRect(), t = term.getBoundingClientRect()
             return t.width > 0 && t.height > 0
               && t.top >= h.top - 0.5 && t.bottom <= h.bottom + 0.5
               && t.left >= h.left - 0.5 && t.right <= h.right + 0.5
               && (!bar || t.bottom - parseFloat(getComputedStyle(term).borderBottomWidth) <= bar.getBoundingClientRect().top + 0.5)
           }"""
        (System.Text.Json.JsonSerializer.Serialize mount)

/// Wait for a mounted player to fit its box, and fail saying where it is when it never does.
let private replayFitsWithin (what: string) (page: IPage) (mount: string) : Async<unit> =
    async {
        match! waitFor what page (replayFits mount) |> Async.Catch with
        | Choice1Of2 () -> ()
        | Choice2Of2 e ->
            let! boxes = await (page.EvaluateAsync<string> (replayBoxes mount))
            failwithf "%s\n  where they are [top, bottom, left, right]: %s" e.Message boxes
    }

/// The harness's live terminal, rewound. Its recording is TALL and narrow (40x60, the shape a
/// terminal opened on a phone has), which is the one a panel runs out of HEIGHT for before
/// width — an 80x24 recording fits a pane either way, so it could not show a player sized to
/// the width alone pushing its last lines, and its control bar, out of the bottom of the panel.
let private rewoundTall (page: IPage) : Async<unit> =
    async {
        do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
        do! awaitU (page.ClickAsync "#shell [data-terminal-tab='term-live']")
        let! _ = await (page.WaitForSelectorAsync "#shell [data-terminal-screen='term-live']")
        do! awaitU (page.ClickAsync "#shell [data-terminal-watch='watch']")
        // Landed: the still at the pin is up, so the player has its recording's size.
        do! waitFor "the rewound terminal to show the screen at its pin" page
                """document.querySelector("#shell [data-pane-replay='terminal:term-live']")?.textContent.includes('earlier output') === true"""
    }

let [<Literal>] private rewoundMount = "#shell [data-pane-replay='terminal:term-live']"

/// What a rewound terminal's player has drawn, as the page's text.
let private rewoundText = sprintf "() => document.querySelector(%s)?.textContent ?? ''" (System.Text.Json.JsonSerializer.Serialize rewoundMount)

/// A rewound terminal's player draws all of its terminal inside the region it is mounted in:
/// scaled to that region's width AND its height, so no column runs off the side of the pane and
/// no line runs under the command row. The 25-rewind finding was both at once — a player scaled
/// to the width alone, by a cell measured before its stylesheet applied, 37px too wide and 60px
/// too tall for its panel.
let private rewoundReplayFits (width: int) (height: int) =
    editorCaseIn width height (sprintf "a rewound terminal plays inside its region at %dx%d" width height) <| fun page ->
        async {
            do! rewoundTall page
            do! replayFitsWithin "the rewound terminal to fit inside its region" page rewoundMount
        }

/// A rewound player's own timer and progress bar read a position that is neither the start nor
/// the end: the time elapsed is on screen (hit-tested at its centre, as the controls are) and
/// past zero, and the bar's filled part is some of it and not all of it.
let private rewoundSaysWhere (width: int) (height: int) =
    editorCaseIn width height (sprintf "a rewound player says how far in it is, against its length, at %dx%d" width height) <| fun page ->
        async {
            do! rewoundTall page
            let read =
                sprintf
                    """() => {
                         const host = document.querySelector(%s)
                         const elapsed = host?.querySelector('.ap-time-elapsed')
                         const filled = host?.querySelector('.ap-gutter-full')
                         if (!elapsed || !filled) return null
                         const box = elapsed.getBoundingClientRect()
                         const hit = document.elementFromPoint(box.left + box.width / 2, box.top + box.height / 2)
                         const fraction = Number((filled.style.transform.match(/scaleX\(([0-9.]+)\)/) ?? [])[1])
                         const [m, s] = elapsed.textContent.split(':').map(Number)
                         return { shown: !!hit && elapsed.contains(hit), seconds: m * 60 + s, fraction }
                       }"""
                    (System.Text.Json.JsonSerializer.Serialize rewoundMount)
            let held = sprintf "() => { const r = (%s)(); return !!r && r.shown && r.seconds > 0 && r.fraction > 0 && r.fraction < 1 }" read
            match! waitFor "the rewound player's timer to be on screen, past the start and short of the end" page held |> Async.Catch with
            | Choice1Of2 () -> ()
            | Choice2Of2 e ->
                let! got = await (page.EvaluateAsync<string> (sprintf "() => JSON.stringify((%s)())" read))
                failwithf "%s\n  what the timer and bar read: %s" e.Message got
        }

/// What is painted at the centre of a player's control bar and of each of its buttons, and
/// whether it is that control — `[]` when there is no bar. The predicate below is this report
/// asking "all of them?", and a failing case prints it, so a red run says WHICH control was
/// covered and by what rather than only that a wait ran out.
let private replayControlsReport (mount: string) =
    sprintf
        """() => {
             const host = document.querySelector(%s)
             const bar = host && host.querySelector('.ap-control-bar')
             if (!bar) return []
             return [bar, ...bar.querySelectorAll('button')].map(el => {
               const r = el.getBoundingClientRect()
               const hit = r.width > 0 && r.height > 0
                 ? document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2)
                 : null
               return { control: el.getAttribute('class') ?? el.tagName,
                        opacity: getComputedStyle(el).opacity,
                        box: [r.top, r.bottom, r.left, r.right].map(Math.round),
                        reached: !!hit && el.contains(hit),
                        painted: hit ? hit.outerHTML.slice(0, 80) : null }
             })
           }"""
        (System.Text.Json.JsonSerializer.Serialize mount)

/// A rewound terminal's transport — play and pause, the timeline, the player's own buttons —
/// is on show, and what is painted at the centre of the bar and of each of its buttons is that
/// control. Measured, not looked for: the bar was in the document all along, at no opacity
/// until a mouse moved over it, and under the command row where no mouse could reach.
let private rewoundReplayControlsInReach (width: int) (height: int) =
    editorCaseIn width height (sprintf "a rewound terminal's player controls are on show and in reach at %dx%d" width height) <| fun page ->
        async {
            do! rewoundTall page
            let report = replayControlsReport rewoundMount
            let all =
                sprintf
                    """async () => {
                         const controls = await (%s)()
                         return controls.length > 1 && controls.every(c => c.reached && c.opacity === '1')
                       }"""
                    report
            match! waitFor "the rewound player's controls to be on show, each painted where it stands" page all |> Async.Catch with
            | Choice1Of2 () -> ()
            | Choice2Of2 e ->
                let! controls = await (page.EvaluateAsync<string> (sprintf "async () => JSON.stringify(await (%s)())" report))
                failwithf "%s\n  the controls: %s" e.Message controls
        }

/// A side column shutting and opening again, with reduced motion asked for, starts no
/// transition. The column is toggled by the class on `<html>` that the real client sets
/// (`nav-alt` for the sidebar, `term-closed` for the terminals), so what is counted is the
/// column's own transition and nothing a handler does around it; `getAnimations()` flushes style, so a transition
/// the toggle started is in its answer at once, with no waiting on a clock.
let private columnHoldsStill (width: int) (height: int) (column: string) (rootClass: string) =
    editorCaseIn width height (sprintf "with reduced motion the %s column does not move at %dpx" column width) <| fun page ->
        async {
            do! awaitU (page.EmulateMediaAsync (PageEmulateMediaOptions (ReducedMotion = ReducedMotion.Reduce)))
            // The shell, by its composer: a shut column is zero wide, which a visibility wait
            // on the column itself would read as never having arrived.
            let! _ = await (page.WaitForSelectorAsync "#shell [data-draft-editor]")
            // Settled first, so what is counted is what the toggle started. A pulse or a blink
            // never finishes and is not waited for.
            do! awaitU (
                    page.EvaluateAsync
                        """() => Promise.all(
                             document.getAnimations()
                               .filter(a => a.effect && a.effect.getComputedTiming().iterations !== Infinity)
                               .map(a => a.finished.catch(() => null)))""")
            let! moved =
                await (page.EvaluateAsync<string[]>(
                        """(cls) => {
                             const started = () => document.getAnimations()
                               .filter(a => a instanceof CSSTransition && a.playState !== 'finished')
                               .map(a => (a.effect.target.getAttribute('class') || a.effect.target.tagName).slice(0, 60)
                                         + ' (' + a.transitionProperty + ')')
                             const root = document.documentElement.classList
                             root.toggle(cls)
                             const there = started()
                             root.toggle(cls)
                             return there.concat(started())
                           }""", rootClass))
            Expect.isEmpty
                moved
                (sprintf "toggling %s with reduced motion starts no transition; these started: %s"
                    rootClass (String.concat ", " moved))
            return ()
        }

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

/// Wait for the "jump to latest" control to be on the screen — hit-tested at its own centre,
/// so a control that is present, shown and buried under something else does not count.
let private jumpShown (page: IPage) =
    async {
        let! _ =
            await (page.WaitForFunctionAsync
                    """(() => {
                         const control = document.querySelector('#shell [data-jump-to-latest="chat"] button')
                         const box = control.getBoundingClientRect()
                         if (box.width === 0) return false
                         const hit = document.elementFromPoint(box.left + box.width / 2, box.top + box.height / 2)
                         return !!hit && control.contains(hit)
                       })()""")
        return ()
    }

/// Scroll the conversation back to its start, which is far enough from the latest message
/// for the control to be offered, and wait for it.
let private scrolledAwayFromLatest (page: IPage) =
    async {
        let! _ = await (page.WaitForSelectorAsync "#shell [data-conversation] [data-message-body]")
        // The harness page is taller than the viewport, so the conversation's own box is
        // brought into view first; then its scroll goes back to the start.
        do! awaitU (
                page.EvaluateAsync
                    """() => {
                         const conversation = document.querySelector('#shell [data-conversation]')
                         conversation.parentElement.scrollIntoView({ block: 'end' })
                         conversation.scrollTop = 0
                       }""")
        do! jumpShown page
    }

/// Every line of a message's words the "jump to latest" control lies over, and whether any
/// line of words runs level with it at all — the second half is what keeps the first from
/// being vacuous, since a control with nothing beside it covers nothing wherever it stands.
/// Lines rather than boxes: a message's box runs the column's width whatever it says, and
/// what the control must not hide is the words in it.
let [<Literal>] private wordsUnderJump =
    """() => {
         const control = document.querySelector('#shell [data-jump-to-latest="chat"] button').getBoundingClientRect()
         const covered = [], level = []
         for (const body of document.querySelectorAll('#shell [data-conversation] [data-message-body]')) {
           const walker = document.createTreeWalker(body, NodeFilter.SHOW_TEXT)
           while (walker.nextNode()) {
             const text = walker.currentNode
             if (!text.textContent.trim()) continue
             const range = document.createRange()
             range.selectNodeContents(text)
             for (const line of range.getClientRects()) {
               if (line.width === 0 || line.top >= control.bottom || line.bottom <= control.top) continue
               level.push(text.textContent.trim().slice(0, 40))
               if (line.left < control.right && line.right > control.left) covered.push(text.textContent.trim().slice(0, 40))
             }
           }
         }
         return JSON.stringify({ covered, level: level.length })
       }"""

/// The "jump to latest" control, shown, and the words beside it: none of them under it.
///
/// Where the scroll stops decides what is level with the control, and a tool run or a chip
/// there would leave nothing to ask about — so a message from the middle of the column, far
/// enough from the end that the control stays shown, is brought level with it first.
let private jumpCoversNoWords (page: IPage) =
    async {
        do! scrolledAwayFromLatest page
        do! awaitU (
                page.EvaluateAsync
                    """() => {
                         const conversation = document.querySelector('#shell [data-conversation]')
                         const control = document.querySelector('#shell [data-jump-to-latest="chat"] button').getBoundingClientRect()
                         const body = conversation.querySelector("[data-message-id='msg-filler-8'] [data-message-body]")
                         const range = document.createRange()
                         range.selectNodeContents(body)
                         const line = [...range.getClientRects()].find(r => r.width > 0 && r.height < 40)
                         conversation.scrollTop += (line.top + line.bottom) / 2 - (control.top + control.bottom) / 2
                       }""")
        do! jumpShown page
        let! report = await (page.EvaluateAsync<string> wordsUnderJump)
        use doc = System.Text.Json.JsonDocument.Parse report
        let level = doc.RootElement.GetProperty("level").GetInt32 ()
        let covered = [ for e in doc.RootElement.GetProperty("covered").EnumerateArray () -> e.GetString () ]
        Expect.isTrue (level > 0) "a message's words run level with the control, so there is something it could cover"
        Expect.isEmpty covered (sprintf "the control lies over these words: %s" (String.concat " | " covered))
        return ()
    }

// --- Surfaces read from their end (`Tail`) ---------------------------------------------------
//
// The chat, a terminal's blocks and a terminal's live screen all keep one promise: a reader at
// the end stays there as more arrives, and one who scrolled away is left there with a way back
// on screen. Every one of these is a fact about a laid-out page — where a scroll stands, what
// is painted at a point — so only a browser can keep checking it.

/// The surface the view names `key` (`TailSurface.key`).
let private tailSurface (key: string) = sprintf "#shell [data-tail='%s']" key

/// A frame and another, so a scroll this case made has been reported to the page (a scroll
/// event lands a frame after the scroll) and whatever a render left behind has settled.
let private twoFrames (page: IPage) : Async<unit> =
    awaitU (page.EvaluateAsync "() => new Promise(done => requestAnimationFrame(() => requestAnimationFrame(done)))")

/// Bring a surface's box on screen and put its reader at its end, the way a reader does it,
/// and say whether there was anywhere else to be — a surface that fits its box is at its end
/// whatever happens, and a case over one could not fail.
let private toTheEnd (page: IPage) (key: string) : Async<unit> =
    async {
        let! scrolls =
            await (
                page.EvaluateAsync<bool> (
                    """selector => {
                         const el = document.querySelector(selector)
                         el.parentElement.scrollIntoView({ block: 'end' })
                         el.scrollTop = el.scrollHeight
                         return el.scrollHeight > el.clientHeight + 100
                       }""",
                    box (tailSurface key)))
        Expect.isTrue scrolls (sprintf "the %s surface is taller than its box, so it has an end to keep" key)
        do! twoFrames page
    }

/// Put a surface's reader at its START, as a reader who scrolled back to read does.
let private toTheStart (page: IPage) (key: string) : Async<unit> =
    async {
        do! awaitU (
                page.EvaluateAsync (
                    """selector => {
                         const el = document.querySelector(selector)
                         el.parentElement.scrollIntoView({ block: 'end' })
                         el.scrollTop = 0
                       }""",
                    box (tailSurface key)))
        do! twoFrames page
    }

/// Where the newest words in a surface are: the LAST occurrence of `mark` in its text, whether
/// that line lies wholly inside the surface's box, and whether what is painted at its centre
/// belongs to the surface — so a line under the composer, a sticky header or a float reads as
/// not shown. With how far short of its end the surface stands, for a red to say by how much.
let private newestShown (page: IPage) (key: string) (mark: string) : Async<string> =
    async {
        let! _ =
            await (
                page.WaitForFunctionAsync (
                    "([selector, mark]) => document.querySelector(selector)?.textContent.includes(mark)",
                    box [| tailSurface key; mark |]))
        do! twoFrames page
        return!
            await (
                page.EvaluateAsync<string> (
                    """([selector, mark]) => {
                         const surface = document.querySelector(selector)
                         const walker = document.createTreeWalker(surface, NodeFilter.SHOW_TEXT)
                         let node = null, at = -1
                         while (walker.nextNode()) {
                           const i = walker.currentNode.textContent.lastIndexOf(mark)
                           if (i >= 0) { node = walker.currentNode; at = i }
                         }
                         const range = document.createRange()
                         range.setStart(node, at)
                         range.setEnd(node, at + mark.length)
                         const line = range.getBoundingClientRect(), box = surface.getBoundingClientRect()
                         const hit = document.elementFromPoint(line.left + line.width / 2, line.top + line.height / 2)
                         return JSON.stringify({
                           inside: line.top >= box.top - 0.5 && line.bottom <= box.bottom + 0.5,
                           painted: !!hit && surface.contains(hit),
                           short: Math.round(surface.scrollHeight - surface.clientHeight - surface.scrollTop)
                         })
                       }""",
                    box [| tailSurface key; mark |]))
    }

/// The verdict `newestShown` has to give for the newest words to be on screen.
let [<Literal>] private shownFully = "\"inside\":true,\"painted\":true"

/// A message from a collaborator, folded into the harness's conversation at `offset`.
let private messageLands (page: IPage) (offset: int64) (body: string) : Async<unit> =
    let expect r = Result.defaultWith failwith r
    foldHarness page [
        offset,
        Yession.Domain.SessionEvent.MessageSent
            { Yession.Domain.Chat.MessageSent.MessageId = Yession.Domain.MessageId.create (sprintf "msg-tail-%d" offset) |> expect
              Yession.Domain.Chat.MessageSent.QueueId = None
              Yession.Domain.Chat.MessageSent.Author = Yession.Domain.Principal.Peer (Yession.Domain.PeerId.create "brave-owl" |> expect)
              Yession.Domain.Chat.MessageSent.Body = body } ]

/// The pane open on the harness's block-mode terminal, finished opening — its width
/// transitions from nothing, and until it has, its scrollback is clipped by the column.
let private paneOnBlocks (page: IPage) : Async<unit> =
    async {
        do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
        let! _ = await (page.WaitForSelectorAsync (tailSurface "blocks:term-harness"))
        do! waitFor "the column to finish opening" page
                """(() => document.querySelector('#shell [data-content-panel]').getAnimations().length === 0)()"""
    }

/// The harness terminal's blocks made taller than their box: a running command's three hundred
/// lines, and every fold in the history open, so the lines are on the page to be scrolled past.
let private blocksFilled (page: IPage) : Async<unit> =
    async {
        do! awaitU (
                page.EvaluateAsync
                    """() => window.__record('term-harness', 2, 'o',
                               Array.from({ length: 300 }, (_, i) => 'line ' + (i + 1)).join('\r\n') + '\r\n')""")
        do! awaitU (
                page.EvaluateAsync
                    """() => document.querySelectorAll("#shell [data-tail='blocks:term-harness'] details").forEach(d => { d.open = true })""")
        do! twoFrames page
    }

/// Fifty records of live output into the harness terminal, one message each and all in one
/// turn, as a burst off the data channel lands — then the frame after them, and what `andThen`
/// reads off the page once it is there. The frame waited for is registered after every render
/// the burst asked for, so whatever the burst was owed has been drawn by the time it runs.
let private afterBurst (page: IPage) (andThen: string) : Async<string> =
    await (
        page.EvaluateAsync<string> (
            sprintf
                """async () => {
                     const before = globalThis.__yessionRenders ?? 0
                     for (let seq = 2; seq < 52; seq++)
                       window.__record('term-harness', seq, 'o', 'burst-mark ' + seq + '\r\n')
                     await new Promise(done => requestAnimationFrame(() => done()))
                     const renders = (globalThis.__yessionRenders ?? 0) - before
                     return String(%s)
                   }"""
                andThen))

/// Whether the "jump to latest" over a surface is on screen and is what is painted at its own
/// centre, so a control that is present, shown and buried does not count.
let private jumpOffered (key: string) =
    sprintf
        """(() => {
             const control = document.querySelector("#shell [data-jump-to-latest='%s'] button")
             const box = control.getBoundingClientRect()
             if (box.width === 0 || box.height === 0) return false
             const hit = document.elementFromPoint(box.left + box.width / 2, box.top + box.height / 2)
             return !!hit && control.contains(hit)
           })()"""
        key

/// Whether a surface's reader is at its end.
let private atItsEnd (key: string) =
    sprintf
        """(() => {
             const el = document.querySelector("%s")
             return el.scrollTop + el.clientHeight >= el.scrollHeight - 4
           })()"""
        (tailSurface key)

/// Show the pane and wait until it stands at the width the separator says — the column animates
/// open, and a measurement taken while it travels is of a width it was for one frame.
let private paneOpenAndSettled (page: IPage) : Async<unit> =
    async {
        do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
        do! waitFor "the pane to stand at the width its separator says" page
                """(() => {
                     const pane = document.querySelector('#shell [data-content-panel]')
                     const said = Number(document.querySelector('#shell [data-term-resize]').getAttribute('aria-valuenow'))
                     return Math.abs(pane.getBoundingClientRect().width - said) <= 1
                   })()"""
    }

/// On a desktop with room to spare, a pane nobody sized takes what the chat's reading column
/// leaves: the conversation keeps its full measure, the pane is never under its floor, and the
/// only strip between them is the chat's own gutter. Read as ONE layout — three numbers that
/// are the same promise, so a red one prints all three.
let private paneTakesTheRoom (page: IPage) : Async<unit> =
    async {
        do! paneOpenAndSettled page
        let! layout =
            await (page.EvaluateAsync<string>
                    """() => {
                         const chat = document.querySelector('#shell [data-conversation]')
                         const pane = document.querySelector('#shell [data-content-panel]').getBoundingClientRect()
                         const groups = [...chat.querySelectorAll('[data-message-author]')]
                         const right = Math.max(...groups.map(g => g.getBoundingClientRect().right))
                         const column = Math.max(...groups.map(g => g.getBoundingClientRect().width))
                         const measure = parseFloat(getComputedStyle(groups[0]).maxWidth)
                         const gutter = parseFloat(getComputedStyle(chat).paddingRight)
                         const gap = pane.left - right
                         return JSON.stringify({
                           chatAtMeasure: column >= measure - 1,
                           gapIsTheGutter: gap <= gutter + 1,
                           paneAtItsFloor: pane.width >= 420,
                           column, measure, gap, gutter, pane: pane.width })
                       }""")
        Expect.stringContains
            layout
            "\"chatAtMeasure\":true,\"gapIsTheGutter\":true,\"paneAtItsFloor\":true"
            "the chat keeps its measure, the pane takes the rest and is never under its floor"
    }

// --- A fold of earlier commands opens UPWARD (`Tail`'s `Press`) ------------------------------
//
// What a reader of a terminal's history is looking at when they press "ran N earlier commands"
// is what FOLLOWS the fold — the latest command, at the end. Opening it used to push all of that
// down the page by the height of what it opened, because the browser anchored its scroll on the
// fold's own line. Only a laid-out page has a place on screen to keep.

/// The harness terminal's newest fold — nearest the end, so the one a reader there can see — by
/// the block id it is keyed by.
let private newestRun (page: IPage) : Async<string> =
    await (
        page.EvaluateAsync<string> (
            """selector => {
                 const runs = document.querySelector(selector).querySelectorAll('[data-terminal-block-run]')
                 return runs.length ? runs[runs.length - 1].getAttribute('data-terminal-block-run') : ''
               }""",
            box (tailSurface "blocks:term-harness")))

/// A fold's line, by the id the run is keyed by.
let private runLine (run: string) =
    sprintf "%s [data-terminal-block-run='%s'] summary" (tailSurface "blocks:term-harness") run

/// Where the latest command's line stands on screen — the last one drawn, since everything a
/// fold holds came before it.
let private latestCommandTop (page: IPage) : Async<float> =
    await (
        page.EvaluateAsync<float> (
            """selector => {
                 const lines = document.querySelector(selector).querySelectorAll('[data-terminal-block-command]')
                 return lines[lines.length - 1].getBoundingClientRect().top
               }""",
            box (tailSurface "blocks:term-harness")))

/// Whether a fold's line lies wholly inside the history's box: the precondition for a case
/// about a reader who presses it from where they are, since one off screen is a press nobody
/// at the end makes (and the browser's own anchoring keeps the place for it anyway).
let private runLineOnScreen (page: IPage) (run: string) : Async<bool> =
    await (
        page.EvaluateAsync<bool> (
            """([surface, line]) => {
                 const box = document.querySelector(surface).getBoundingClientRect()
                 const at = document.querySelector(line).getBoundingClientRect()
                 return at.top >= box.top && at.bottom <= box.bottom
               }""",
            box [| tailSurface "blocks:term-harness"; runLine run |]))

/// Press a fold one way or another, and wait for the press to have been drawn — open if it was
/// shut, shut if it was open — and the frame after.
let private pressRunBy (press: IPage -> string -> Async<unit>) (page: IPage) (run: string) : Async<unit> =
    async {
        let details = sprintf "%s [data-terminal-block-run='%s'] details" (tailSurface "blocks:term-harness") run
        let! was = await (page.EvaluateAsync<bool> ("selector => document.querySelector(selector).open", box details))
        do! press page (runLine run)
        let! _ =
            await (
                page.WaitForFunctionAsync (
                    "([selector, was]) => document.querySelector(selector).open !== was",
                    box [| box details; box was |]))
        do! twoFrames page
    }

/// A pointer's press on whatever is painted at the middle of `selector`, where it stands — not
/// the locator's click, which first scrolls its target to wherever it judges clear of a sticky
/// line, and so moves the very place a case is measuring.
let private clickWhereItIs (page: IPage) (selector: string) : Async<unit> =
    async {
        let! at =
            await (
                page.EvaluateAsync<float[]> (
                    "selector => { const r = document.querySelector(selector).getBoundingClientRect(); return [r.left + r.width / 2, r.top + r.height / 2] }",
                    box selector))
        do! awaitU (page.Mouse.ClickAsync (float32 at.[0], float32 at.[1]))
    }

/// A fold pressed with a pointer, where it is on screen.
let private clickRun = pressRunBy clickWhereItIs

/// A fold pressed from the keyboard, as a reader whose focus is on its line does.
let private enterRun =
    pressRunBy (fun page line ->
        async {
            do! awaitU (page.FocusAsync line)
            do! awaitU (page.Keyboard.PressAsync "Enter")
        })

/// Scroll a surface by hand so `selector` sits near the top of its box, clear of any command line
/// held at the top while its output scrolls — up, away from the end, which is what makes its
/// reader one who is not following it.
let private scrollToTop (page: IPage) (selector: string) : Async<unit> =
    async {
        do! awaitU (
                page.EvaluateAsync (
                    """([surface, target]) => {
                         const el = document.querySelector(surface)
                         el.scrollTop += document.querySelector(target).getBoundingClientRect().top - el.getBoundingClientRect().top - 120
                       }""",
                    box [| tailSurface "blocks:term-harness"; selector |]))
        do! twoFrames page
    }

/// Where something stands on screen: the top of its box.
let private topOf (page: IPage) (selector: string) : Async<float> =
    await (page.EvaluateAsync<float> ("selector => document.querySelector(selector).getBoundingClientRect().top", box selector))

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

        // Addressing somebody is reachable from the keyboard alone: an @ greys in the rest of a
        // name, and Tab takes it.
        editorCase "Tab completes the hinted address" <| fun page ->
            async {
                let! _ = await (page.WaitForSelectorAsync ".ProseMirror")
                do! awaitU (page.ClickAsync ".ProseMirror")
                do! awaitU (page.Keyboard.TypeAsync "thanks @sw")
                let! _ = await (page.WaitForSelectorAsync "[data-address-hint]")
                do! awaitU (page.Keyboard.PressAsync "Tab")
                let! md = await (page.EvaluateAsync<string> "() => window.__md()")
                Expect.equal (md.TrimEnd ()) "thanks @swift-heron" "the hinted name was taken"
            }

        editorCase "each Tab moves to the next name on offer" <| fun page ->
            async {
                let! _ = await (page.WaitForSelectorAsync ".ProseMirror")
                do! awaitU (page.ClickAsync ".ProseMirror")
                do! awaitU (page.Keyboard.TypeAsync "@")
                let! _ = await (page.WaitForSelectorAsync "[data-address-hint]")
                do! awaitU (page.Keyboard.PressAsync "Tab")
                do! awaitU (page.Keyboard.PressAsync "Tab")
                let! md = await (page.EvaluateAsync<string> "() => window.__md()")
                Expect.equal (md.TrimEnd ()) "@swift-heron" "the second press moved past the agent"
            }

        // For a keyboard where Tab is spoken for: the right arrow takes the hint too.
        editorCase "the right arrow completes the hinted address" <| fun page ->
            async {
                let! _ = await (page.WaitForSelectorAsync ".ProseMirror")
                do! awaitU (page.ClickAsync ".ProseMirror")
                do! awaitU (page.Keyboard.TypeAsync "@ag")
                let! _ = await (page.WaitForSelectorAsync "[data-address-hint]")
                do! awaitU (page.Keyboard.PressAsync "ArrowRight")
                let! md = await (page.EvaluateAsync<string> "() => window.__md()")
                Expect.equal (md.TrimEnd ()) "@agent" "the arrow took the hint"
            }

        // Tab is taken only while there is something to complete: a composer that kept it
        // would be a keyboard trap.
        editorCase "Tab leaves the composer when nothing is hinted" <| fun page ->
            async {
                let! _ = await (page.WaitForSelectorAsync ".ProseMirror")
                do! awaitU (page.ClickAsync ".ProseMirror")
                do! awaitU (page.Keyboard.TypeAsync "hello")
                do! awaitU (page.Keyboard.PressAsync "Tab")
                let! _ =
                    await (page.WaitForFunctionAsync
                        "!document.querySelector('#host').contains(document.activeElement)")
                ()
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

        // The prompt an empty composer shows is a node decoration: an attribute on the empty
        // paragraph that the stylesheet paints from, so it is never text anyone can select,
        // copy or send. The harness host is not a `[data-rich-body]`, so nothing paints here;
        // what is read is the hook the stylesheet reads.
        editorCase "an empty composer offers its prompt without it becoming content" <| fun page ->
            async {
                let! _ = await (page.WaitForSelectorAsync "#host .ProseMirror [data-placeholder]")
                let! prompt =
                    await (page.EvaluateAsync<string>
                        "() => document.querySelector('#host .ProseMirror [data-placeholder]').getAttribute('data-placeholder')")
                let! text = await (page.EvaluateAsync<string> "() => document.querySelector('#host .ProseMirror').textContent")
                Expect.equal prompt Yession.App.Dom.Text.composerPlaceholder "the empty composer carries its prompt"
                Expect.equal text "" "and the prompt is not in the document"
            }

        editorCase "a composer written in stops offering its prompt" <| fun page ->
            async {
                let! _ = await (page.WaitForSelectorAsync "#host .ProseMirror [data-placeholder]")
                do! awaitU (page.ClickAsync ".ProseMirror")
                do! awaitU (page.Keyboard.TypeAsync "x")
                let! _ = await (page.WaitForFunctionAsync "!document.querySelector('#host .ProseMirror [data-placeholder]')")
                return ()
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

                // Anti-vacuity, first half: the storm is really running before a key is
                // pressed, so the convergence below happens under one rather than beside it.
                //
                // WAITED for, not counted afterwards. The storm pushes one per animation
                // frame, so "at least five by the end of the typing window" measures how many
                // frames the MACHINE delivered inside a fixed wall-clock window — it went red
                // twice here on a loaded box with nothing wrong with the code, and would go
                // green on a fast one with the storm broken in a way this says nothing about.
                // A wait makes the same claim about the code and none about the box: a page
                // the browser never paints times out here saying which, and a storm that
                // never dispatches never arrives.
                do! waitFor "the caret storm to push frames at all" page "window.__caretPushes >= 5"
                let! stormedBefore = await (page.EvaluateAsync<int> "() => window.__caretPushes")

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

                // Anti-vacuity, second half, and the reason a green here means anything: a
                // storm that stopped before the words arrived converges beautifully. Frames
                // landing DURING the convergence are what raced it — waited for with the storm
                // still on, so the first one settles it, rather than counted against a window
                // that was only ever a guess at how fast this machine is.
                do! waitFor
                        "the caret storm to push a frame while the content was arriving"
                        page
                        (sprintf "window.__caretPushes > %d" stormedBefore)

                do! awaitU (page.EvaluateAsync "() => window.__caretStorm(false)")
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
                // The storm is dispatching before anything is typed, and keeps dispatching
                // while the words arrive — both waited for rather than counted at the end, for
                // the reason the case above gives: a push is one animation frame, so a count
                // inside a fixed window is a measurement of the machine.
                do! waitFor "the caret storm to push frames at all" page "window.__caretPushes >= 5"
                let! stormedBefore = await (page.EvaluateAsync<int> "() => window.__caretPushes")
                do! awaitU (page.ClickAsync "#peer-a .ProseMirror")
                do! awaitU (page.Keyboard.TypeAsync "# Heading one")
                // Over CONTENT: an empty document is the case y-prosemirror short-circuits
                // anyway (`initialContentChanged`), so a storm over nothing would prove nothing.
                do! waitFor
                        "the co-editor to render the author's remote content"
                        page
                        (sprintf "%s === 'Heading one'" (ownText "#peer-b .ProseMirror h1"))
                do! waitFor
                        "the caret storm to push a frame while the content was arriving"
                        page
                        (sprintf "window.__caretPushes > %d" stormedBefore)
                do! awaitU (page.EvaluateAsync "() => window.__caretStorm(false)")

                let! pushes = await (page.EvaluateAsync<int> "() => window.__caretPushes")
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
                // wrote, encoded as asciicast, rebuilt by `TranscriptReplay.cast`,
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

        // The first player a page mounts is the one that turns the player's stylesheet on, and
        // the player measures its character cell once, as it is created. Turned on through the
        // link's `media` attribute that measurement ran before the sheet applied — in the
        // page's proportional face, with none of the sheet's borders — and every recording it
        // played came out a ninth wider than its host, its right-hand columns clipped away.
        // `#replay` is that first mount here, as the first rewind of a session is in the app.
        editorCase "the first replay on a page is measured under its own stylesheet, and fits its host" <| fun page ->
            async {
                let! _ = await (page.WaitForSelectorAsync "#replay .ap-player")
                do! replayFitsWithin "the page's first replay to fit inside its host" page "#replay"
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
                // The terminal's text, not the mount's: a chapter's label on the bar names the
                // same command from the moment the recording loads, wherever it landed.
                let! _ =
                    await (page.WaitForFunctionAsync (
                        "document.querySelector('#replay-gappy .ap-term-text').textContent.includes('second')",
                        null,
                        PageWaitForFunctionOptions (Timeout = 10_000f)))
                return ()
            }

        // A block's command is recorded as its input record — the shell's echo is not kept —
        // and the player draws only output. Handed over as recorded, a replay ran one command's
        // output into the next with nothing between them to say which was which.
        editorCase "a replay draws the command a block ran on the screen" <| fun page ->
            async {
                let! _ = await (page.WaitForSelectorAsync "#replay .ap-overlay-start")
                do! awaitU (page.ClickAsync "#replay .ap-overlay-start")
                // Its output first, so the screen has been drawn when the command is looked for;
                // the terminal's text rather than the mount's, which also holds the chapter's label.
                do! waitFor "the recording's output on the screen" page
                        "document.querySelector('#replay .ap-term-text').textContent.includes('total 0')"
                let! screen = await (page.EvaluateAsync<string> "() => document.querySelector('#replay .ap-term-text').textContent")
                Expect.stringContains screen "ls -la" "the command, on the screen above what it printed"
            }

        // Each command's chapter is a mark on the bar, labelled with the command. The player draws
        // a label on one line centred on its mark, however long the command, so a `for` loop's
        // ran off both sides of a phone and named nothing that was left on it.
        editorCaseOnTouch 390 844 "every chapter's label names its command and stays on a phone's screen" <| fun page ->
            async {
                do! awaitU (page.EvaluateAsync "() => window.__chapters()")
                do! waitFor "the three chapters on the bar" page
                        "document.querySelectorAll('#replay-chapters .ap-marker-container').length === 3"
                do! awaitU (page.EvaluateAsync "() => document.querySelector('#replay-chapters').scrollIntoView()")
                let! labels =
                    await (page.EvaluateAsync<string[]> """() =>
                        [...document.querySelectorAll('#replay-chapters .ap-marker-container .ap-tooltip')].map(label => {
                            const box = label.getBoundingClientRect()
                            const inside = box.left >= 0 && box.right <= window.innerWidth
                            const whole = label.scrollWidth <= label.clientWidth
                            return label.textContent + (inside ? '' : ' [off the screen]') + (whole ? '' : ' [cut off]')
                        })""")
                Expect.equal
                    (labels |> Array.map (fun label -> label.Substring (label.IndexOf " - " + 3)))
                    [| "echo one"; "for i in 1 2 3; do echo \"step $i\"; sleep 1; done"; "echo three" |]
                    "each label names its command, none of them off the screen or cut off"
            }

        editorCaseOnTouch 390 844 "pressing a chapter's mark moves the replay to that command" <| fun page ->
            async {
                do! awaitU (page.EvaluateAsync "() => window.__chapters()")
                do! waitFor "the three chapters on the bar" page
                        "document.querySelectorAll('#replay-chapters .ap-marker-container').length === 3"
                do! awaitU (page.EvaluateAsync "() => document.querySelector('#replay-chapters').scrollIntoView()")
                // The last mark's own centre, and only if a press there lands on it: a mark another
                // is drawn over is not one a thumb can press.
                let! centre =
                    await (page.EvaluateAsync<float[]> """() => {
                        const marks = document.querySelectorAll('#replay-chapters .ap-marker-container')
                        const mark = marks[marks.length - 1]
                        const box = mark.getBoundingClientRect()
                        const x = box.left + box.width / 2, y = box.top + box.height / 2
                        return mark.contains(document.elementFromPoint(x, y)) ? [x, y] : []
                    }""")
                Expect.equal centre.Length 2 "the last chapter's mark is the thing under its own centre"
                do! awaitU (page.Touchscreen.TapAsync (float32 centre.[0], float32 centre.[1]))
                // Resting at the start, the second command's last line is only on the screen once
                // the replay has moved past it — which, paused, only a seek can do.
                do! waitFor "the replay to move to the last command" page
                        "document.querySelector('#replay-chapters .ap-term-text').textContent.includes('step 3')"
            }

        editorCase "the keyboard moves a replay from chapter to chapter" <| fun page ->
            async {
                do! awaitU (page.EvaluateAsync "() => window.__chapters()")
                do! waitFor "the three chapters on the bar" page
                        "document.querySelectorAll('#replay-chapters .ap-marker-container').length === 3"
                // The terminal's text is what Tab reaches in a player; its keys are the player's.
                do! awaitU (page.FocusAsync "#replay-chapters .ap-term-text")
                do! awaitU (page.Keyboard.PressAsync "]")
                do! awaitU (page.Keyboard.PressAsync "]")
                do! waitFor "the replay to move on two chapters" page
                        "document.querySelector('#replay-chapters .ap-term-text').textContent.includes('step 3')"
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

        // The same question of an act still RUNNING, whose gutter holds a mark rather than
        // an arrow (the fixture's is the session's circle; the agent's diamond is measured at
        // the end of a message, by the caret case). A mark beside a title is read against that title's letters,
        // so it spans them: lower point on the title's baseline, upper at its x-height —
        // measured by an inline-block `1ex` tall dropped into the title's first line, whose
        // bottom rests on that baseline and whose top is where an `x` ends — and it stays
        // inside the gutter it marks. The first cut of this case checked the baseline alone
        // and passed a gutter set a size larger than its title, whose mark was 2.8px too
        // tall and centred onto the baseline by coincidence.
        editorCase "a running act's mark spans its title's letters, in the gutter" <| fun page ->
            async {
                do! awaitU (page.EmulateMediaAsync (PageEmulateMediaOptions (ReducedMotion = ReducedMotion.Reduce)))
                do! awaitU (page.EvaluateAsync "() => window.__acts()")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-act-status=\"running\"] [data-act-running]")
                let! misplaced =
                    await (page.EvaluateAsync<string> """() => {
                        const note = document.querySelector('#shell [data-act-status="running"]')
                        const mark = note.querySelector('[data-act-running]')
                        const gutter = mark.parentElement.getBoundingClientRect()
                        // The title, not the cause's sentence above it (which sits in the same column).
                        const title = [...note.querySelectorAll('.col-start-2')].find(e => !e.closest('[data-cause-ref]'))
                        const walk = document.createTreeWalker(title, NodeFilter.SHOW_TEXT)
                        let first = null
                        while (!first && walk.nextNode()) if (walk.currentNode.textContent.trim()) first = walk.currentNode
                        if (!first) return 'the title has no words'
                        const probe = document.createElement('span')
                        probe.style.cssText = 'display:inline-block;width:0;height:1ex'
                        first.parentNode.insertBefore(probe, first)
                        const letters = probe.getBoundingClientRect()
                        probe.remove()
                        const at = mark.getBoundingClientRect()
                        const em = parseFloat(getComputedStyle(first.parentElement).fontSize)
                        const wrong = []
                        if (Math.abs(at.bottom - letters.bottom) > 0.05 * em)
                          wrong.push('its lower point is ' + (at.bottom - letters.bottom) + 'px off the title\'s baseline')
                        if (Math.abs(at.top - letters.top) > 0.05 * em)
                          wrong.push('its upper point is ' + (at.top - letters.top) + 'px off the title\'s x-height')
                        if (at.left < gutter.left || at.right > gutter.right)
                          wrong.push('it is outside its gutter (' + at.left + '-' + at.right + ' against ' + gutter.left + '-' + gutter.right + ')')
                        return wrong.join('; ')
                    }""")
                Expect.equal misplaced "" "the running act's mark stands on its title's line"
            }

        // A cause's head is the end of its line, so it paints centred on that line: its tip
        // in the middle of the line's pixel column. Both round to the device-pixel grid by
        // their own edges — the line as a box, the head as an SVG, which a browser snaps the
        // same way — so the case rounds each as the painter does: the line's two edges, and
        // the head's box with the tip's place inside it. The tip is the middle of the head's
        // path, which is symmetric. Every head on the page, each against the line in its own
        // mark; the head stood half a pixel left of its stem before.
        editorCase "every cause's head paints centred on its line" <| fun page ->
            async {
                do! awaitU (page.EvaluateAsync "() => window.__acts()")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-cause-ref] svg")
                let! off =
                    await (page.EvaluateAsync<string> """() => {
                        const d = devicePixelRatio
                        const heads = [...document.querySelectorAll('#shell [data-cause-ref] svg, #shell [data-cause-chain] svg')]
                          .filter(svg => !svg.closest('[data-entity]'))
                        if (heads.length === 0) return 'the page draws no cause heads'
                        const wrong = []
                        for (const svg of heads) {
                          const mark = svg.parentElement.parentElement
                          const line = [...mark.querySelectorAll('*')].find(e => {
                            const b = e.getBoundingClientRect()
                            return b.width === 1 && b.height > 1
                          })
                          if (!line) { wrong.push('a head with no line'); continue }
                          const l = line.getBoundingClientRect()
                          const lineAt = (Math.round(l.left * d) + Math.round(l.right * d)) / 2
                          const box = svg.getBoundingClientRect()
                          const path = svg.querySelector('path').getBBox()
                          const scale = box.width / svg.viewBox.baseVal.width
                          const tipAt = Math.round(box.left * d) + (path.x + path.width / 2) * scale * d
                          if (Math.abs(tipAt - lineAt) > 0.01) wrong.push('a head paints at ' + tipAt + ' and its line at ' + lineAt)
                        }
                        return wrong.join('; ')
                    }""")
                Expect.equal off "" "each head sits on its line"
            }

        // A mark in the gutter under a cause's corner hangs from that corner's line, so it is
        // centred on the line AS PAINTED. Geometry alone cannot say so: a browser paints a box
        // by rounding each edge to the device-pixel grid on its own, and a one-pixel line whose
        // centre falls between two pixels lands on one of them. The disc and the line both
        // measured 328.0 at their centres while the disc painted half a pixel left of the
        // line. So the case rounds each measured edge as the painter does and compares the
        // centres that come out — and the stem is found by its shape (one pixel wide, taller
        // than that), not by a class.
        editorCase "a running act's mark paints centred on its cause's line" <| fun page ->
            async {
                do! awaitU (page.EmulateMediaAsync (PageEmulateMediaOptions (ReducedMotion = ReducedMotion.Reduce)))
                do! awaitU (page.EvaluateAsync "() => window.__acts()")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-act-status=\"running\"] [data-cause-ref]")
                let! off =
                    await (page.EvaluateAsync<string> """() => {
                        const note = document.querySelector('#shell [data-act-status="running"]')
                        const stem = [...note.querySelectorAll('[data-cause-ref] *')].find(e => {
                          const b = e.getBoundingClientRect()
                          return b.width === 1 && b.height > 1
                        })
                        if (!stem) return 'the cause draws no line'
                        const painted = e => {
                          const b = e.getBoundingClientRect(), d = devicePixelRatio
                          return (Math.round(b.left * d) + Math.round(b.right * d)) / 2
                        }
                        const mark = painted(note.querySelector('[data-act-running]'))
                        const line = painted(stem)
                        return mark === line ? '' : 'the mark paints at ' + mark + ' and the line at ' + line + ' (device pixels)'
                    }""")
                Expect.equal off "" "the mark hangs on the line"
            }

        // EVERY fold's arrow sits on the centre of its own title's line, and every arrow on
        // one rail — an act's title is prose at one step, a tool call's is mono at another,
        // and the two steps carry different line-heights (13/16 and 11/16 against a row that
        // states 20). The arrow used to place itself against a line it assumed, so a row of a
        // new kind arrived a couple of pixels off and was fixed by subtracting pixels at the
        // row; now the row is a grid that states the line once. Asked of every fold the page
        // holds, not one: the fault this guards is a NEW kind of row, and a case that looked
        // at the first would have passed while the new one sat off the line.
        //
        // Geometry, which only a rendered page can settle; pinned as a distance from the
        // centre, never as coordinates, so a wider gutter or a taller line does not move it.
        editorCase "every fold's arrow is on its own title's line, and all of them on one rail" <| fun page ->
            async {
                do! awaitU (page.EvaluateAsync "() => window.__acts()")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-act-note] [data-fold]")
                let! off =
                    await (page.EvaluateAsync<float[]> """() => {
                        const folds = [...document.querySelectorAll('#shell [data-fold]')]
                        const down = folds.map(b => {
                            const arrow = b.querySelector('svg').getBoundingClientRect()
                            // The title's FIRST line box, not its whole box: a title that
                            // wraps is two lines tall, and the arrow belongs on the first.
                            const line = b.parentElement.querySelector('.col-start-2').getClientRects()[0]
                            return (arrow.top + arrow.height / 2) - (line.top + line.height / 2)
                        })
                        const rails = folds.map(b => {
                            const arrow = b.querySelector('svg').getBoundingClientRect()
                            return arrow.left + arrow.width / 2
                        })
                        return [ folds.length,
                                 Math.max(...down.map(Math.abs)),
                                 Math.max(...rails) - Math.min(...rails) ]
                    }""")
                Expect.isTrue (off.[0] >= 2.0) (sprintf "the page must hold folds of both kinds, it held %.0f" off.[0])
                Expect.isTrue (abs off.[1] < 1.0) (sprintf "one arrow sat %.2fpx off its title's line" off.[1])
                Expect.isTrue (abs off.[2] < 1.0) (sprintf "the arrows spread %.2fpx across the rail" off.[2])
            }

        // The same promise on a PHONE, because that is where it broke in a way desktop could
        // not show: a row that bleeds to the screen's edges (`itemGround`'s `max-md:-mx-4`)
        // once spent the bleed on its gutter, so a bled row's arrow stood sixteen pixels left
        // of a nested row's — on phones only. A rail is a rail at every width.
        editorCaseIn 390 844 "and on a phone, where a bled row could spend its bleed on the gutter" <| fun page ->
            async {
                do! awaitU (page.EvaluateAsync "() => window.__acts()")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-act-note] [data-fold]")
                let! off =
                    await (page.EvaluateAsync<float[]> """() => {
                        const folds = [...document.querySelectorAll('#shell [data-fold]')]
                        const down = folds.map(b => {
                            const arrow = b.querySelector('svg').getBoundingClientRect()
                            // The title's FIRST line box, not its whole box: a title that
                            // wraps is two lines tall, and the arrow belongs on the first.
                            const line = b.parentElement.querySelector('.col-start-2').getClientRects()[0]
                            return (arrow.top + arrow.height / 2) - (line.top + line.height / 2)
                        })
                        const rails = folds.map(b => {
                            const arrow = b.querySelector('svg').getBoundingClientRect()
                            return arrow.left + arrow.width / 2
                        })
                        return [ folds.length,
                                 Math.max(...down.map(Math.abs)),
                                 Math.max(...rails) - Math.min(...rails) ]
                    }""")
                Expect.isTrue (off.[0] >= 2.0) (sprintf "the page must hold folds of both kinds, it held %.0f" off.[0])
                Expect.isTrue (abs off.[1] < 1.0) (sprintf "one arrow sat %.2fpx off its title's line" off.[1])
                Expect.isTrue (abs off.[2] < 1.0) (sprintf "the arrows spread %.2fpx across the rail" off.[2])
            }

        // The margin has ONE rail, and it is the speakers' avatars: a fold's arrow under an
        // author line stands on the column that author's avatar stands on, not centred in the
        // wider gutter beside it — where it sat six pixels right of every avatar, a second
        // rail next to the one the eye already follows down the page. Asked of the avatar
        // each fold sits under, by centre, so a larger avatar or a wider gutter keeps it true.
        editorCase "every fold's arrow stands on its author's avatar column" <| fun page ->
            async {
                do! awaitU (page.EvaluateAsync "() => window.__acts()")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-act-note] [data-fold]")
                let! off =
                    await (page.EvaluateAsync<float[]> """() => {
                        const folds = [...document.querySelectorAll('#shell section [data-fold]')]
                        const apart = folds.map(b => {
                            const avatar = b.closest('section').querySelector('header > span').getBoundingClientRect()
                            const arrow = b.querySelector('svg').getBoundingClientRect()
                            return (arrow.left + arrow.width / 2) - (avatar.left + avatar.width / 2)
                        })
                        return [ folds.length, Math.max(...apart.map(Math.abs)) ]
                    }""")
                Expect.isTrue (off.[0] >= 1.0) "the page must hold a fold under an author line"
                Expect.isTrue (off.[1] < 1.0) (sprintf "an arrow stood %.2fpx off its author's avatar" off.[1])
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

        // The arrow used to be the only thing that toggled — a square a thumb can miss on a
        // phone, on a line that is otherwise most title. A press anywhere on that line reaches
        // the same fold now (`View.foldClickRow`), and this presses the TITLE, deliberately not
        // `[data-fold]`, to prove the row itself is the control rather than the arrow alone.
        editorCase "tapping the row, not only the arrow, unfolds it and folds it back" <| fun page ->
            async {
                do! awaitU (page.EvaluateAsync "() => window.__acts()")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-act-note] [data-fold]")
                let visibility = "getComputedStyle(document.querySelector('#shell [data-act-note] [data-fold-body]')).visibility"
                do! waitFor "the particulars to start hidden" page (visibility + " === 'hidden'")
                let clickTitle =
                    "() => document.querySelector('#shell [data-act-note] [data-fold]').nextElementSibling.click()"
                do! awaitU (page.EvaluateAsync clickTitle)
                do! waitFor "a press on the title unfolds it" page (visibility + " === 'visible'")
                do! awaitU (page.EvaluateAsync clickTitle)
                do! waitFor "and a second press folds it back" page (visibility + " === 'hidden'")
            }

        // The strip scrolls sideways, and nothing about a scroll box keeps what matters in it on
        // screen: the newest tab — the one just selected — opened past the right-hand edge.
        editorCase "a newly selected tab is scrolled into view" <| fun page ->
            async {
                do! openManyTerminals page
                let! overflows = await (page.EvaluateAsync<bool> stripOverflows)
                Expect.isTrue overflows "the strip holds more tabs than it shows, or this proves nothing"
                // Chosen by the DOM's own click rather than Playwright's, which scrolls what it
                // clicks into view first and would prove nothing about the strip.
                do! awaitU (page.EvaluateAsync "() => document.querySelector('#shell [data-pane-strip] [role=tab]:last-child').click()")
                do! waitFor "the last tab to be selected" page
                        "document.querySelector('#shell [data-pane-strip] [role=tab]:last-child')?.getAttribute('aria-selected') === 'true'"
                // A frame for the reveal, which runs after the render.
                do! twoFrames page
                let! inView =
                    await (page.EvaluateAsync<bool>
                        (insideStrip "document.querySelector('#shell [data-pane-strip] [role=tab][aria-selected=true]')"))
                Expect.isTrue inView "the selected tab lies inside the strip's box"
            }

        // The arrow walk moves FOCUS, and a focused tab past the edge — or under the fade that
        // says more tabs lie past it — is a keyboard user walking blind. Walked end to end, from
        // the selected tab at the far right back Home and then right again, so every step that
        // brings a tab in from either side is asked. Over EVERY item of the walk — `all` is one
        // too, outside the strip, and asked nothing — so the order the pivot puts them in is
        // not something this case has to know; that every tab was reached is.
        editorCase "keyboard focus never rests past the strip's edge or under its fade" <| fun page ->
            async {
                do! openManyTerminals page
                let! overflows = await (page.EvaluateAsync<bool> stripOverflows)
                Expect.isTrue overflows "the strip holds more tabs than it shows, or this proves nothing"
                let! count = await (page.EvaluateAsync<int> "() => document.querySelectorAll('#shell [data-pane-strip] [role=tab]').length")
                do! awaitU (page.FocusAsync "#shell [data-pane-strip] [role=tab][aria-selected=true]")
                // Where the focused tab is, against the strip's box less a fade on each end that
                // has tabs past it — read off the scroll position, not the attribute the fade is
                // keyed on, so the measurement does not trust the thing it is checking.
                let placed =
                    sprintf
                        """() => {
                             const strip = document.querySelector('#shell [data-pane-strip]')
                             const s = strip.getBoundingClientRect()
                             const before = strip.scrollLeft > 1
                             const after = strip.scrollLeft + strip.clientWidth < strip.scrollWidth - 1
                             if (!strip.contains(document.activeElement)) return 'outside'
                             const t = document.activeElement.getBoundingClientRect()
                             const left = s.left + (before ? %f : 0), right = s.right - (after ? %f : 0)
                             return (t.left >= left - 0.5 && t.right <= right + 0.5)
                               ? '' : `${document.activeElement.getAttribute('data-pane-tab')} at ${t.left}..${t.right}, shown ${left}..${right}`
                           }"""
                        Yession.App.TabStrip.edge Yession.App.TabStrip.edge
                let! items = await (page.EvaluateAsync<int> "() => document.querySelectorAll('#shell [data-pane-pivot] [role=tab]').length")
                let faults = ResizeArray<string> ()
                let mutable reached = 0
                for key in "Home" :: List.replicate (items - 1) "ArrowRight" do
                    do! awaitU (page.Keyboard.PressAsync key)
                    match! await (page.EvaluateAsync<string> placed) with
                    | "outside" -> ()
                    | "" -> reached <- reached + 1
                    | fault -> reached <- reached + 1; faults.Add (sprintf "after %s: %s" key fault)
                Expect.equal reached count "the walk reached every tab in the strip"
                Expect.equal (String.concat "; " faults) "" "every tab focus lands on is shown clear of the strip's edges"
            }

        // Which item is selected is said by INK (the pivot, P2-2's successor): full ink for
        // the one, the faintest admitted ink for the rest. This replaced a case about the
        // strip's underline, which the pivot does not draw; the promise is the same one —
        // the selection is visible, and drawn inside the pivot rather than clipped by it.
        // Read where the colours settle: an item's colour TRANSITIONS, so the frame the
        // selection landed in shows the colour it started from.
        editorCase "the selected pivot item is told apart from the others, inside the pivot" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
                do! waitFor "an item to be selected beside one that is not" page
                        "!!document.querySelector('#shell [data-pane-strip] [role=tab][aria-selected=true]') && !!document.querySelector('#shell [data-pane-strip] [role=tab][aria-selected=false]')"
                let! faults =
                    await (page.EvaluateAsync<string>
                        """async () => {
                             const strip = document.querySelector('#shell [data-pane-strip]')
                             await Promise.all(
                               strip.getAnimations({ subtree: true })
                                 .filter(a => a.effect && a.effect.getComputedTiming().iterations !== Infinity)
                                 .map(a => a.finished.catch(() => null)))
                             const s = strip.getBoundingClientRect()
                             const on = strip.querySelector('[role=tab][aria-selected=true]')
                             const off = strip.querySelector('[role=tab][aria-selected=false]')
                             const t = on.getBoundingClientRect()
                             const faults = []
                             if (t.top < s.top - 0.5 || t.bottom > s.bottom + 0.5) faults.push('the selected item is cut off vertically')
                             if (strip.scrollHeight !== strip.clientHeight) faults.push(`the pivot overflows ${strip.scrollHeight - strip.clientHeight}px vertically`)
                             if (getComputedStyle(on).color === getComputedStyle(off).color) faults.push('the selected item is the same colour as the others')
                             return faults.join('; ')
                           }""")
                Expect.equal faults "" "the selected item is drawn whole and unlike the others"
            }

        // The focus ring is drawn outside a control's edge by default, and the strip clips at
        // its own: a tab flush with the box showed its ring as one vertical bar, which reads as
        // a separator. Reached by the KEYBOARD, because `:focus-visible` is the rule that draws
        // it, and a programmatic `focus()` alone does not fire it.
        editorCase "a focused tab's ring is inside the strip" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
                // Showing the pane moves focus to the command line a frame later; wait for it,
                // or it lands after the focus this case puts on the tab and takes it away.
                let! _ =
                    await (page.WaitForFunctionAsync (
                            "sel => document.activeElement?.matches(sel) === true",
                            box (commandLine "term-harness")))
                do! awaitU (page.FocusAsync "#shell [data-pane-strip] [role=tab][aria-selected=true]")
                do! awaitU (page.Keyboard.PressAsync "ArrowRight")
                do! waitFor "the walk to land on another tab, keyboard-focused" page
                        "document.activeElement?.matches('#shell [data-pane-strip] [role=tab][aria-selected=false]:focus-visible') === true"
                let! faults =
                    await (page.EvaluateAsync<string>
                        """() => {
                             const s = document.querySelector('#shell [data-pane-strip]').getBoundingClientRect()
                             const tab = document.activeElement
                             const ring = getComputedStyle(tab)
                             const width = parseFloat(ring.outlineWidth)
                             if (ring.outlineStyle === 'none' || width === 0) return 'no ring is drawn'
                             const reach = width + parseFloat(ring.outlineOffset)
                             const t = tab.getBoundingClientRect()
                             const faults = []
                             if (t.top - reach < s.top - 0.5) faults.push('cut off at the top')
                             if (t.bottom + reach > s.bottom + 0.5) faults.push('cut off at the bottom')
                             if (t.left - reach < s.left - 0.5) faults.push('cut off at the left')
                             if (t.right + reach > s.right + 0.5) faults.push('cut off at the right')
                             return faults.join('; ')
                           }""")
                Expect.equal faults "" "the ring is drawn wholly inside the strip's box"
            }

        // One door per state. The pane used to offer a `+` on its strip, "+ New terminal" at
        // the foot of its switcher, and an empty pane's own button — two of them on one screen
        // at once, a reader left to work out they were one act. At most one control that
        // makes a terminal is ON SCREEN in the pane, whatever it shows: more tabs than fit,
        // the `all` page, an empty pane, and `all` over an empty pane. Counted by what is
        // painted at each control's centre, so one present but covered is not a door, and one
        // a reader can see is — which is the only count that means anything to them.
        //
        // The empty pane is reached the way a reader reaches it: every terminal ends, each
        // tab stays where it stood, closed, and the reader puts them away one × at a time —
        // each landing the pane on the next, until there is nothing left to be on.
        editorCase "at most one way to make a terminal is on screen in the pane, in every state" <| fun page ->
            async {
                let doors (state: string) =
                    async {
                        let! n =
                            await (page.EvaluateAsync<int> """() => [...document.querySelectorAll(
                                '#shell [data-content-panel] [data-pane-new], #shell [data-content-panel] [data-terminal-new]')]
                                .filter(e => {
                                    const r = e.getBoundingClientRect()
                                    if (!r.width || !r.height) return false
                                    const hit = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2)
                                    return hit !== null && e.contains(hit) }).length""")
                        return sprintf "%s: %d" state n
                    }
                let closed (id: string) =
                    Yession.Domain.SessionEvent.TerminalClosed
                        { Yession.Domain.Terminals.TerminalClosed.TerminalId = harnessTerminal id
                          Yession.Domain.Terminals.TerminalClosed.Reason = "closed by a peer"
                          Yession.Domain.Terminals.TerminalClosed.By = None }
                do! openManyTerminals page
                let! many = doors "more tabs than fit"
                do! awaitU (page.ClickAsync "#shell [data-pane-switcher]")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-content-list]")
                let! listing = doors "the all page"
                do! awaitU (page.Keyboard.PressAsync "Escape")
                let! _ = await (page.WaitForFunctionAsync """!document.querySelector('#shell [data-content-list]')""")
                // Chosen, so it is the one the reader is looking at when it ends, and it stays.
                do! awaitU (page.EvaluateAsync "() => document.querySelector(\"#shell [data-pane-tab='terminal:term-live']\").click()")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-pane-tab='terminal:term-live'][aria-selected=true]")
                do! foldHarness page
                        [ for i, id in List.indexed [ "term-harness"; "term-live"; "term-more-0"; "term-more-1"; "term-more-2"; "term-more-3" ] ->
                              95L + int64 i, closed id ]
                for left in 5 .. -1 .. 0 do
                    let! _ = await (page.WaitForSelectorAsync "#shell [data-pane-tab-dismiss]")
                    do! awaitU (page.ClickAsync "#shell [data-pane-tab-dismiss]")
                    do! waitFor (sprintf "%d tabs left" left) page
                            (sprintf "document.querySelectorAll('#shell [data-terminal-closed-tab]').length === %d" left)
                let! _ = await (page.WaitForSelectorAsync "#shell [data-terminal-new]")
                let! empty = doors "an empty pane"
                do! awaitU (page.ClickAsync "#shell [data-pane-switcher]")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-content-list]")
                let! emptyListing = doors "the all page over an empty pane"
                let seen = [ many; listing; empty; emptyListing ]
                Expect.equal seen (seen |> List.map (fun said -> said.Substring (0, said.LastIndexOf ':') + ": 1")) "one door in each"
            }

        // A closed tab stays until it is put away (F2; desktop journey, finding 16): its × does
        // that. Pressed from the keyboard,
        // because the × leaves the document with its tab, and the floor says focus goes to what
        // replaced it — the tab beside it — rather than to `body`.
        editorCase "putting a closed tab away lands focus on the tab beside it" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
                // Chosen, so it is the one the reader is looking at when it ends, and it stays.
                do! awaitU (page.ClickAsync "#shell [data-pane-tab='terminal:term-harness']")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-pane-tab='terminal:term-harness'][aria-selected=true]")
                do! foldHarness page
                        [ 95L,
                          Yession.Domain.SessionEvent.TerminalClosed
                              { Yession.Domain.Terminals.TerminalClosed.TerminalId = harnessTerminal "term-harness"
                                Yession.Domain.Terminals.TerminalClosed.Reason = "closed by a peer"
                                Yession.Domain.Terminals.TerminalClosed.By = None } ]
                let dismiss = "#shell [data-pane-tab-dismiss='term-harness']"
                let! _ = await (page.WaitForSelectorAsync dismiss)
                do! awaitU (page.FocusAsync dismiss)
                do! awaitU (page.Keyboard.PressAsync "Enter")
                do! waitFor "the closed tab to be gone" page "!document.querySelector(\"#shell [data-pane-tab='terminal:term-harness']\")"
                do! waitFor "focus to be on the tab beside it" page
                        "document.activeElement?.getAttribute('data-pane-tab') === 'terminal:term-live'"
            }

        // The keyboard a dying shell dropped is caught a frame later, and only while it is still
        // dropped (`DomMove.IfDropped`). That verdict was read a frame before the move it
        // allowed: a reader who put their hand on the closed tab's × in the frame between had
        // it taken to the panel, and Enter pressed nothing — the case above went red that way.
        // The hand goes down in the first frame after the swap, which is the frame the catch
        // looks in, and two frames later it has to be where it was put.
        editorCase "a hand put down the frame after a shell died is left where it was put" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
                do! awaitU (page.ClickAsync "#shell [data-pane-tab='terminal:term-harness']")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-pane-tab='terminal:term-harness'][aria-selected=true]")
                let body =
                    foldBody
                        [ 95L,
                          Yession.Domain.SessionEvent.TerminalClosed
                              { Yession.Domain.Terminals.TerminalClosed.TerminalId = harnessTerminal "term-harness"
                                Yession.Domain.Terminals.TerminalClosed.Reason = "closed by a peer"
                                Yession.Domain.Terminals.TerminalClosed.By = None } ]
                // In the page, so no round trip decides which frame the hand lands in.
                let! held =
                    await (page.EvaluateAsync<bool> (
                        """body => new Promise(resolve => {
                             window.__fold(body)
                             requestAnimationFrame(() => {
                               const dismiss = document.querySelector("#shell [data-pane-tab-dismiss='term-harness']")
                               dismiss.focus()
                               requestAnimationFrame(() => requestAnimationFrame(() => resolve(document.activeElement === dismiss)))
                             })
                           })""", box body))
                Expect.isTrue held "focus stays on the × it was put on"
            }

        // The narrowest pane the splitter allows (desktop journey, finding 8): with the strip's
        // old `+N` beside it, the selected tab was cut off under the count, and tabs were lost
        // past the left edge. Whichever end the reader chooses from, the item they chose is
        // shown whole, inside the part of the pivot that scrolls.
        editorCaseIn 1440 900 "at the narrowest pane the selected item is shown whole, chosen from either end" <| fun page ->
            async {
                do! openManyTerminals page
                // Through the separator, as a reader narrows it: the width is the model's, and a
                // property set by hand lasts only until the next render draws the model's again.
                do! awaitU (page.FocusAsync "#shell [data-term-resize]")
                do! awaitU (page.Keyboard.PressAsync "End")
                do! waitFor "the pane to be 320px wide" page
                        "Math.round(document.querySelector('#shell [data-content-panel]').getBoundingClientRect().width) === 320"
                let faults = ResizeArray<string> ()
                for which in [ "last-child"; "first-child"; "nth-child(3)"; "last-child" ] do
                    // The DOM's own click, which scrolls nothing into view on its way.
                    do! awaitU (page.EvaluateAsync (sprintf "() => document.querySelector('#shell [data-pane-strip] [role=tab]:%s').click()" which))
                    do! waitFor (sprintf "the %s item to be selected" which) page
                            (sprintf "document.querySelector('#shell [data-pane-strip] [role=tab]:%s')?.getAttribute('aria-selected') === 'true'" which)
                    // A frame for the reveal, which runs after the render.
                    do! awaitU (page.EvaluateAsync "() => new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r)))")
                    let! inView =
                        await (page.EvaluateAsync<bool>
                            (insideStrip "document.querySelector('#shell [data-pane-strip] [role=tab][aria-selected=true]')"))
                    if not inView then faults.Add which
                Expect.isEmpty faults (sprintf "chosen and not shown whole: %s" (String.Join (", ", faults)))
            }

        // Terminal work in the chat, and the pane (Plan 14, stages 1-2; P2-1). Host-free, like
        // the editor and the replay beside it: what needs a real browser here is not the
        // Session but the DOM swaps — where FOCUS goes when a chip in the chat opens a preview
        // in the pane. Not visible to a rendered string, and the WCAG floor rather than a
        // nicety: a chip that opens a pane and leaves focus behind is exactly the failure the
        // floor names. (The strip's arrow walk, which this case used to finish with, is the
        // keyboard-focus case's: a chip no longer adds a tab to walk to.)
        editorCase "a chat chip opens a preview that plays" <| fun page ->
            async {
                // The chip the harness model's one block puts in the chat.
                let! _ = await (page.WaitForSelectorAsync "#shell [data-chat-block]")
                do! awaitU (page.ClickAsync "#shell [data-chat-block]")

                // A preview opened, showing that block.
                let! _ = await (page.WaitForSelectorAsync "#shell [data-pane-preview^='block:']")

                // Focus followed it into the pane. Asserted BEFORE anything is played,
                // because pressing play is itself a focus move.
                let! _ = await (page.WaitForFunctionAsync """document.activeElement?.hasAttribute('data-pane-panel') === true""")

                // The block reads as TEXT, and its recording is one press away — the two reads
                // of one history, with the cheap one first. Pressing play mounts the real
                // player over the ranged cast the model built, inside the preview the chip
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

        // A block's recording shows the screen the command began on before anybody presses
        // play. Without a poster the player draws nothing until it is asked to play, so the
        // preview a reader opened to watch a command was a black box with a button on it.
        editorCase "a block's replay shows its first frame before play" <| fun page ->
            async {
                let! _ = await (page.WaitForSelectorAsync "#shell [data-chat-block]")
                do! awaitU (page.ClickAsync "#shell [data-chat-block]")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-pane-block] [data-terminal-output]")
                do! awaitU (page.ClickAsync "#shell [data-pane-watch]")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-pane-replay] .ap-overlay-start")
                do! waitFor "the block's replay to show the screen its command began on" page
                        """document.querySelector('#shell [data-pane-replay]')?.textContent.includes('before the command') === true"""
            }

        // The session id is a thing people copy, so on a phone a press on it has to reach
        // IT — not fall through to the title field whose box reaches down behind it, where a
        // press could only ever edit the title. Hit-tested at the id's own centre.
        editorCaseIn 390 844 "on a phone a press on the session id reaches the id, not the title" <| fun page ->
            async {
                let! _ = await (page.WaitForSelectorAsync "#shell [data-session-id]")
                let! idPressable =
                    await (page.EvaluateAsync<bool>
                            """() => {
                                 const id = document.querySelector('#shell [data-session-id]')
                                 const r = id.getBoundingClientRect()
                                 const hit = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2)
                                 return id.contains(hit)
                               }""")
                Expect.isTrue idPressable "a press on the session id reaches the id, so it can be selected"
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
                        "document.querySelector('#shell [data-content-panel]').getBoundingClientRect().left >= window.innerWidth - 1")

                // A chip brings it on, and it takes the WHOLE column.
                do! awaitU (page.ClickAsync "#shell [data-chat-block]")
                let! _ =
                    await (page.WaitForFunctionAsync
                        """(() => {
                             const r = document.querySelector('#shell [data-content-panel]').getBoundingClientRect()
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
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='hide']")
                let! _ =
                    await (page.WaitForFunctionAsync
                        "document.querySelector('#shell [data-content-panel]').getBoundingClientRect().left >= window.innerWidth - 1")
                let! _ = await (page.WaitForFunctionAsync """document.activeElement?.hasAttribute('data-chat-block') === true""")
                return ()
            }

        // A phone's strip shows a ROW of tabs. It showed under two: `all`, a `+` and the way
        // back shared its line, and the selected tab's × spent a whole thumb's width in flow —
        // so the strip was 252px, the selected tab half of it, and each neighbour was cut to a
        // letter by the fade. Counted the way a reader sees it: tabs whose WHOLE box lies
        // inside the strip's, with more tabs than it can show, the last one selected (the
        // newest, which is where a reader usually is) and names the length a session gives
        // them (`term 4`).
        editorCaseIn 390 844 "on a phone the strip shows at least three whole tabs" <| fun page ->
            async {
                let untitled (id: string) =
                    Yession.Domain.SessionEvent.TerminalOpened
                        { Yession.Domain.Terminals.TerminalOpened.TerminalId = harnessTerminal id
                          Yession.Domain.Terminals.TerminalOpened.OpenedBy =
                            Yession.Domain.ActorRef.PeerRef (Yession.Domain.PeerId.create "ada" |> Result.defaultWith failwith)
                          Yession.Domain.Terminals.TerminalOpened.Title = Yession.Domain.Terminals.TerminalTitle.fallback
                          Yession.Domain.Terminals.TerminalOpened.Sandbox = None
                          Yession.Domain.Terminals.TerminalOpened.Renewable = false }
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
                let! before = await (page.EvaluateAsync<int> "() => document.querySelectorAll('#shell [data-pane-strip] [role=tab]').length")
                do! foldHarness page [ for i in 0 .. 5 -> 80L + int64 i, untitled (sprintf "term-phone-%d" i) ]
                do! waitFor "six more tabs in the strip" page
                        (sprintf "document.querySelectorAll('#shell [data-pane-strip] [role=tab]').length === %d" (before + 6))
                do! awaitU (page.EvaluateAsync "() => document.querySelector('#shell [data-pane-strip] [role=tab]:last-child').click()")
                do! waitFor "the last tab to be selected" page
                        "document.querySelector('#shell [data-pane-strip] [role=tab]:last-child')?.getAttribute('aria-selected') === 'true'"
                do! awaitU (
                        page.EvaluateAsync
                            """() => Promise.all(
                                 document.getAnimations()
                                   .filter(a => a.effect && a.effect.getComputedTiming().iterations !== Infinity)
                                   .map(a => a.finished.catch(() => null)))""")
                let! overflows = await (page.EvaluateAsync<bool> stripOverflows)
                Expect.isTrue overflows "the strip holds more tabs than it shows, or this proves nothing"
                let! whole =
                    await (page.EvaluateAsync<string>
                        """() => {
                             const s = document.querySelector('#shell [data-pane-strip]').getBoundingClientRect()
                             return JSON.stringify([...document.querySelectorAll('#shell [data-pane-strip] [role=tab]')]
                               .filter(t => { const r = t.getBoundingClientRect(); return r.left >= s.left - 0.5 && r.right <= s.right + 0.5 })
                               .map(t => t.textContent.trim()))
                           }""")
                let whole = System.Text.Json.JsonSerializer.Deserialize<string array> whole
                Expect.isTrue (whole.Length >= 3) (sprintf "three whole tabs or more, got %A" whole)
            }

        // The room came partly from the `+`, which a phone's row no longer carries — and making
        // a terminal is still a press away: the `all` page, the door to everything, offers it.
        // Counted by what is painted at each door's centre, so the `+` the stylesheet hides
        // is not one, and exactly one is: this is the one state the phone's pane had a door in
        // before, and it must still have one, not two.
        editorCaseIn 390 844 "on a phone the all page offers the one way to make a terminal" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
                do! awaitU (page.ClickAsync "#shell [data-pane-switcher]")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-content-list]")
                do! awaitU (
                        page.EvaluateAsync
                            """() => Promise.all(
                                 document.getAnimations()
                                   .filter(a => a.effect && a.effect.getComputedTiming().iterations !== Infinity)
                                   .map(a => a.finished.catch(() => null)))""")
                let! doors =
                    await (page.EvaluateAsync<int> """() => [...document.querySelectorAll(
                        '#shell [data-content-panel] [data-pane-new], #shell [data-content-panel] [data-terminal-new]')]
                        .filter(e => {
                            const r = e.getBoundingClientRect()
                            if (!r.width || !r.height) return false
                            const hit = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2)
                            return hit !== null && e.contains(hit) }).length""")
                Expect.equal doors 1 "one door to a new terminal on the all page"
            }

        // The phone pane's left edge says it is the way back: a mark a person can SEE on the
        // screen, and one whose press lands on the handle that closes the pane. A mark that
        // measured zero, hung off the screen, or sat under another element would render the
        // same string as one that works, so this measures it and hit-tests its centre.
        editorCaseIn 390 844 "on a phone the pane's edge shows a mark that presses as the way back" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-chat-block]")
                let! _ =
                    await (page.WaitForFunctionAsync
                        """(() => {
                             const r = document.querySelector('#shell [data-content-panel]').getBoundingClientRect()
                             return r.left <= 1 && Math.round(r.width) === window.innerWidth
                           })()""")
                let! shape =
                    await (page.EvaluateAsync<float[]> """() => {
                        const edge = document.querySelector('#shell [data-pane-grab-edge]')
                        const mark = edge.querySelector('svg').getBoundingClientRect()
                        const hit = document.elementFromPoint(mark.left + mark.width / 2, mark.top + mark.height / 2)
                        return [mark.width, mark.height, mark.left, mark.right, window.innerWidth, edge.contains(hit) ? 1 : 0, mark.top + mark.height / 2]
                    }""")
                Expect.isTrue (shape.[0] > 0.0 && shape.[1] > 0.0) "the mark has a size"
                Expect.isTrue (shape.[2] >= 0.0 && shape.[3] <= shape.[4]) "and is on the screen"
                Expect.equal shape.[5] 1.0 "and what is painted at its centre is the handle, so pressing the mark presses it"
                // Pressed where it is drawn, and the pane goes back off the screen.
                do! awaitU (page.Mouse.ClickAsync (float32 (shape.[2] + shape.[0] / 2.0), float32 shape.[6]))
                let! _ =
                    await (page.WaitForFunctionAsync
                        "document.querySelector('#shell [data-content-panel]').getBoundingClientRect().left >= window.innerWidth - 1")
                return ()
            }

        // The edge's mark hangs in the gutter, and the gutter is the column left of the rail:
        // no word in the pane may begin under it. The mark sits midway down the edge, so the
        // row it met depended on how many rows there were ("›exit"); this asks of EVERY painted
        // text run in the pane, so the answer does not depend on which one happens to be there.
        // Both faces of the pane, because the all page's rows and a terminal's lines are
        // different surfaces with different leftmost text.
        editorCaseIn 390 844 "on a phone no text in the pane begins under the edge's mark" <| fun page ->
            async {
                let underTheMark =
                    """() => {
                         const mark = document.querySelector('#shell [data-pane-grab-edge] svg').getBoundingClientRect()
                         const panel = document.querySelector('#shell [data-content-panel]')
                         const walker = document.createTreeWalker(panel, NodeFilter.SHOW_TEXT)
                         const runs = []
                         for (let n = walker.nextNode(); n; n = walker.nextNode()) {
                           if (!n.textContent.trim()) continue
                           const range = document.createRange()
                           range.selectNodeContents(n)
                           for (const r of range.getClientRects())
                             if (r.width > 1 && r.height > 1 && r.right > 0 && r.left < window.innerWidth)
                               runs.push({ text: n.textContent.trim().slice(0, 20), left: r.left, top: r.top, bottom: r.bottom })
                         }
                         return JSON.stringify({ runs: runs.length, under: runs.filter(r => r.left < mark.right) })
                       }"""
                let measure () =
                    async {
                        let! json = await (page.EvaluateAsync<string> underTheMark)
                        use doc = System.Text.Json.JsonDocument.Parse json
                        let root = doc.RootElement
                        Expect.isTrue (root.GetProperty("runs").GetInt32 () > 0) "the pane has text on it, or this proves nothing"
                        return root.GetProperty("under").ToString ()
                    }
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
                let! _ =
                    await (page.WaitForFunctionAsync
                        "document.querySelector('#shell [data-content-panel]').getBoundingClientRect().left <= 1")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-pane-switcher]")
                let! onTerminal = measure ()
                Expect.equal onTerminal "[]" "no text on the terminal face begins under the mark"
                do! awaitU (page.ClickAsync "#shell [data-pane-switcher]")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-content-list]")
                let! onAll = measure ()
                Expect.equal onAll "[]" "no text on the all page begins under the mark"
            }

        // The focus contract (where the keyboard goes after an act in the pane). Each of these
        // removes the control that was pressed or puts a surface in front of the reader, and
        // what only a browser can say is where focus actually ENDED — the model can name a
        // target, and a target that is absent, unfocusable or out of reach lands on `body`.
        //
        // The shut pane first: zero pixels wide on a desktop, and every control in it was a
        // Tab stop — a full cycle stopped eleven times on things nobody could see. Walked from
        // the last control before the pane, so the very next Tab is the one that would enter
        // it, and walked with the real keyboard, because "reachable by Tab" is the question.
        // The shut pane's edge tab (P1-4) is the one thing on the first screen that says
        // terminals exist, so it has to BE on that screen: inside the header band rather than
        // pushed out of it by the words it now carries, inside the viewport, and big enough to
        // press. On a desktop it also stands on the title's baseline, which is the band's whole
        // reason for being one band — measured as an equality of baselines, never as pixels.
        // A phone's band centres its group on the title's line instead (`Style.headerAside`),
        // so there the baseline is not the promise and is not asserted.
        let edgeTabOnTheBand (desktop: bool) (page: IPage) =
            async {
                let! _ = await (page.WaitForSelectorAsync "#shell [data-content-toggle='show']")
                let measure =
                            """(desktop) => {
                                 const wrong = []
                                 const tab = document.querySelector('#shell [data-content-toggle="show"]')
                                 // The harness mounts the shell below its editors, so it is
                                 // brought on screen first: a point off screen hits nothing.
                                 tab.scrollIntoView({ block: 'center' })
                                 const at = tab.getBoundingClientRect()
                                 const band = document.querySelector('#shell header').getBoundingClientRect()
                                 if (at.top < band.top || at.bottom > band.bottom || at.left < band.left || at.right > band.right)
                                   wrong.push('outside the header band: ' + JSON.stringify(at) + ' in ' + JSON.stringify(band))
                                 if (at.left < 0 || at.right > window.innerWidth)
                                   wrong.push('outside the viewport: ' + at.left + '..' + at.right + ' of ' + window.innerWidth)
                                 if (at.width < 24 || at.height < 24)
                                   wrong.push('too small to press: ' + at.width + 'x' + at.height)
                                 // What is painted at its centre is the tab, not something over it.
                                 const hit = document.elementFromPoint(at.left + at.width / 2, at.top + at.height / 2)
                                 if (!hit || !tab.contains(hit)) wrong.push('covered at its centre by ' + (hit && hit.outerHTML.slice(0, 80)))
                                 if (desktop) {
                                   const probe = () => {
                                     const p = document.createElement('span')
                                     p.style.cssText = 'display:inline-block;width:0;height:0;vertical-align:baseline'
                                     return p
                                   }
                                   const words = tab.querySelector('[data-terminals-count]')
                                   const mine = probe()
                                   words.appendChild(mine)
                                   const tabBaseline = mine.getBoundingClientRect().bottom
                                   mine.remove()
                                   // An input's baseline is its text's, and it lends it to a line
                                   // box only in flow — so its OFFSET inside the input is read
                                   // with the wrapper laid out as a block for a moment, and
                                   // added to where the input really is.
                                   const title = document.querySelector('#shell [data-session-title]')
                                   const top = title.getBoundingClientRect().top
                                   const wrap = title.parentElement
                                   const was = wrap.style.display
                                   wrap.style.display = 'block'
                                   const theirs = probe()
                                   title.after(theirs)
                                   const offset = theirs.getBoundingClientRect().bottom - title.getBoundingClientRect().top
                                   theirs.remove()
                                   wrap.style.display = was
                                   const drift = tabBaseline - (top + offset)
                                   if (Math.abs(drift) > 2) wrong.push('off the title baseline by ' + drift + 'px')
                                 }
                                 return wrong.join('; ')
                               }"""
                let! wrong = await (page.EvaluateAsync<string> (measure, desktop))
                Expect.equal wrong "" "the edge tab sits on the header band"
            }

        editorCaseIn 1440 900 "the edge tab is on the header's baseline and inside the viewport, on a desktop" <| edgeTabOnTheBand true
        editorCaseIn 390 844 "the edge tab is inside the header and the viewport, on a phone" <| edgeTabOnTheBand false

        editorCase "a hidden pane takes no Tab stops" <| fun page ->
            async {
                let! _ = await (page.WaitForSelectorAsync "#shell [data-content-toggle='show']")
                // What would be skipped has to be there to skip, or this passes over nothing.
                let! held =
                    await (page.EvaluateAsync<int>
                            """() => document.querySelectorAll('#shell [data-content-panel] button, #shell [data-content-panel] [tabindex="0"]').length""")
                Expect.isTrue (held > 0) "the shut pane still holds controls"
                let! started =
                    await (page.EvaluateAsync<bool>
                            """() => {
                                 const pane = document.querySelector('#shell [data-content-panel]')
                                 const before = [...document.querySelectorAll('#shell button')]
                                   .filter(b => b.tabIndex >= 0 && !b.disabled
                                             && (pane.compareDocumentPosition(b) & Node.DOCUMENT_POSITION_PRECEDING))
                                 window.__stops = []
                                 document.addEventListener('focusin', e =>
                                   window.__stops.push(e.target.closest('[data-content-panel]') ? 'pane' : 'elsewhere'))
                                 for (const b of before.reverse()) { b.focus(); if (document.activeElement === b) return true }
                                 return false
                               }""")
                Expect.isTrue started "focus starts on the last control before the pane"
                for _ in 1 .. 3 do
                    do! awaitU (page.Keyboard.PressAsync "Tab")
                let! stops = await (page.EvaluateAsync<string[]> "() => window.__stops")
                // The first stop is the start itself; at least one more means Tab moved at all.
                Expect.isTrue (stops.Length > 1) (sprintf "Tab moved focus (stops: %A)" stops)
                Expect.isFalse (Array.contains "pane" stops) (sprintf "no stop inside the shut pane (stops: %A)" stops)
            }

        editorCase "showing the pane puts focus on the command line" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
                let! _ =
                    await (page.WaitForFunctionAsync (
                            "sel => document.activeElement?.matches(sel) === true",
                            box (commandLine "term-harness")))
                return ()
            }

        // Nothing in the chat opened what was showing, so there is no chip to go back to — and
        // the fallback used to be the strip's first tab, inside the pane that had just shut.
        editorCase "hiding the pane puts focus on the way back in" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-content-toggle='hide']")
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='hide']")
                let! _ =
                    await (page.WaitForFunctionAsync
                            """document.activeElement?.getAttribute('data-content-toggle') === 'show'""")
                return ()
            }

        // The panel takes focus whenever a chip opens something that is not a terminal, and
        // the browser's own ring on it was a 1px near-black outline on a near-black column.
        // Reached from the KEYBOARD, because a ring is owed to keyboard focus (`:focus-visible`)
        // and a pointer press rightly earns none.
        editorCase "the tab panel's focus is visible" <| fun page ->
            async {
                let! _ = await (page.WaitForSelectorAsync "#shell [data-chat-block]")
                do! awaitU (page.FocusAsync "#shell [data-chat-block]")
                do! awaitU (page.Keyboard.PressAsync "Enter")
                let! _ = await (page.WaitForFunctionAsync """document.activeElement?.hasAttribute('data-pane-panel') === true""")
                let! ring =
                    await (page.EvaluateAsync<string>
                            """() => {
                                 const s = getComputedStyle(document.activeElement)
                                 return [s.outlineStyle, s.outlineWidth].join(' ')
                               }""")
                match ring.Split ' ' with
                | [| style; width |] ->
                    // `auto` is the browser's own ring — the one that did not show.
                    Expect.isFalse (style = "none" || style = "auto") (sprintf "a ring of our own paints (%s)" ring)
                    Expect.isTrue (float (width.Replace ("px", "")) >= 2.0) (sprintf "at least 2px (%s)" ring)
                | _ -> failwithf "the outline answered '%s'" ring
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
        // Typing into the composer with a phone keyboard up never scrolls the page past the
        // shell. Photographed on iOS as a band of empty page between the composer and the
        // keyboard, which stayed after the keyboard went: ProseMirror's scroll-to-caret
        // scrolled the WINDOW, measured against the visual viewport's height without its
        // offset, so a caret the platform had already brought into view was scrolled for
        // again — on a send (the cleared draft) and on typing, neither of which the update
        // loop sees.
        //
        // A keyboard is the visual viewport shrinking under a layout that does not, which a
        // page scale is here. iOS also lets the page scroll into the keyboard's height; the
        // room below the shell stands in for that, and is the room the double scroll spent.
        editorCaseIn 390 844 "typing in the composer with the keyboard up never scrolls the page past the shell" <| fun page ->
            async {
                let! _ = await (page.WaitForSelectorAsync "#shell [data-draft-editor] .ProseMirror")
                do! awaitU (
                        page.EvaluateAsync
                            """() => {
                                 const room = document.createElement('div')
                                 room.style.height = '400px'
                                 document.body.appendChild(room)
                                 document.getElementById('shell').scrollIntoView({ block: 'end' })
                               }""")
                do! awaitU (page.ClickAsync "#shell [data-draft-editor] .ProseMirror")
                let! cdp = await (page.Context.NewCDPSessionAsync page)
                let scale = Collections.Generic.Dictionary<string, obj> ()
                scale.["pageScaleFactor"] <- box 1.6
                let! _ = await (cdp.SendAsync ("Emulation.setPageScaleFactor", scale))
                let! _ = await (page.WaitForFunctionAsync "visualViewport.height < innerHeight")

                // How far the visible area runs past the shell's foot, in CSS pixels: the gap.
                let pastShell () =
                    page.EvaluateAsync<float>
                        """() => {
                             const shell = document.getElementById('shell').getBoundingClientRect()
                             return (visualViewport.offsetTop + visualViewport.height) - shell.bottom
                           }"""
                    |> await
                do! awaitU (page.Keyboard.TypeAsync "one")
                do! awaitU (page.Keyboard.PressAsync "Enter")
                do! awaitU (page.Keyboard.TypeAsync "two")
                let! typed = pastShell ()
                Expect.isTrue (typed <= 1.0) (sprintf "typing left %.0fpx of page under the shell" typed)
            }
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
        // Nothing in the conversation is ever under the card. It used to be an overlay at the
        // chat's foot, so that the chat would not reflow when the card arrived and grew — and
        // on a session whose first acts were commands, the newest of them sat beneath it for as
        // long as it stood: drawn, focusable by Tab, and impossible to scroll into view, because
        // the scrollport ran on behind the card. So the promise is about the ITEMS, each one: it
        // can be brought into view, and when it is, a press in its middle lands on it rather
        // than on the card. Only a browser settles it — the markup is the same either way.
        //
        // Every item rather than the last, because what is covered is a band at the foot, and
        // which items fall in it depends on the column's height; walking them all means the
        // case does not have to know.
        let launchCardCoversNothingCase width height =
            editorCaseIn width height
                (sprintf "at %dpx every item in the conversation can be scrolled clear of the launch card" width) <| fun page ->
                async {
                    do! awaitU (page.EvaluateAsync "() => window.__launchOver()")
                    let! _ = await (page.WaitForSelectorAsync "#shell [data-repo-picker] [data-repo-picker-start]")
                    // The card RISES in (`animate-ask-rise`), growing its height over ~240ms, and
                    // while it does the chat's room is still changing underfoot — an item
                    // hit-tested mid-rise can be under a card that has not finished arriving. The
                    // promise is about where things land once it has, so wait for the entrance to
                    // settle (finite animations only; the carets pulse forever).
                    do! awaitU (
                            page.EvaluateAsync
                                """() => Promise.all(
                                     document.getAnimations()
                                       .filter(a => a.effect && a.effect.getComputedTiming().iterations !== Infinity)
                                       .map(a => a.finished.catch(() => null)))""")
                    // Ground truth: the column has more than it can show, so some items start
                    // out of view and bringing them in is something the case has to do.
                    let! overflows =
                        await (page.EvaluateAsync<bool>
                                """() => {
                                     const c = document.querySelector('#shell [data-conversation]')
                                     return c.scrollHeight > c.clientHeight + 1
                                   }""")
                    Expect.isTrue overflows "the conversation scrolls, so where it ends is a question"
                    let! hidden =
                        await (page.EvaluateAsync<string[]>
                                """() => {
                                     const items = [...document.querySelectorAll('#shell [data-conversation] [data-message-id]')]
                                     return items.flatMap(item => {
                                       // To the middle, as far as the scroll allows: the column
                                       // pins its author line at the top, and an item scrolled
                                       // only to the nearest edge stops under that instead.
                                       item.scrollIntoView({ block: 'center' })
                                       const box = item.getBoundingClientRect()
                                       const hit = document.elementFromPoint(box.left + box.width / 2, box.top + box.height / 2)
                                       return hit && item.contains(hit)
                                         ? []
                                         : [`${item.getAttribute('data-message-id')} (a press on it lands on ${hit ? hit.outerHTML.slice(0, 60) : 'nothing'})`]
                                     })
                                   }""")
                    Expect.isEmpty
                        hidden
                        (sprintf "every item, scrolled into view, is the thing under a press in its middle; these were not: %s"
                            (String.Join (" | ", hidden)))
                }
        launchCardCoversNothingCase 390 844
        launchCardCoversNothingCase 1440 900
        // The card reserves the room its list will want, so repositories ARRIVING do not change
        // its height. The card is docked at the column's foot; sized to its content it stood only
        // as tall as a looking line and then LEAPT up off that foot the instant a page of
        // repositories folded in, taking the conversation above it up with it — the jump this
        // case exists to forbid. START, at the foot, is the thing that must not move: it is where
        // the hand is going. So the promise is that the card's height and START's top are the
        // SAME an instant before the listing arrives (`__launchLooking`) and after (`__launch`).
        //
        // Only a browser settles it: the two states fold from different listings into the same
        // flex column, and whether that column holds its height across them is a fact about
        // layout the markup cannot show — the card carries `data-repo-picker` in both.
        //
        // Under REDUCED MOTION, which is not a loss of coverage but the point: the card RISES in
        // on mount (`animate-ask-rise` grows its height from nothing), so measured mid-rise it is
        // shorter than it will stand — a race, not a regression. The invariant here is the
        // SETTLED height the card reserves, and reduced motion drops the entrance to the plain
        // appearance so what is measured is that settled height and nothing in flight. The
        // entrance itself is a flourish, not a promise, and is left to the eye (`frames`).
        let launchCardHoldsHeightCase width height =
            editorCaseOn
                (Some (BrowserNewContextOptions (
                        ViewportSize = ViewportSize (Width = width, Height = height),
                        ReducedMotion = ReducedMotion.Reduce)))
                (sprintf "at %dpx the launch card keeps its height as repositories load" width) <| fun page ->
                async {
                    let shape =
                        """() => {
                             const card = document.querySelector('#shell [data-repo-picker]').getBoundingClientRect()
                             const start = document.querySelector('#shell [data-repo-picker-start]').getBoundingClientRect()
                             return [Math.round(card.height), Math.round(start.top)]
                           }"""
                    // The instant before the repositories land: the card is up, still looking.
                    do! awaitU (page.EvaluateAsync "() => window.__launchLooking()")
                    let! _ = await (page.WaitForSelectorAsync "#shell [data-repo-picker] [data-repo-picker-start]")
                    let! looking = await (page.EvaluateAsync<int[]> shape)
                    // Ground truth that this IS the looking state and not the loaded one already:
                    // nothing to hold yet, so the case measures the step it means to.
                    let! rows = await (page.EvaluateAsync<int> "() => document.querySelectorAll('#shell [data-repo-picker] [data-repo-candidate]').length")
                    Expect.equal rows 0 "the looking state has no repositories in it yet"
                    // The repositories arrive.
                    do! awaitU (page.EvaluateAsync "() => window.__launch(true)")
                    let! _ = await (page.WaitForSelectorAsync "#shell [data-repo-picker] [data-repo-candidate]")
                    let! loaded = await (page.EvaluateAsync<int[]> shape)
                    Expect.equal loaded looking
                        (sprintf "the card held neither its height nor START's place as the listing loaded: looking was height=%d start=%d, loaded is height=%d start=%d"
                            looking.[0] looking.[1] loaded.[0] loaded.[1])
                }
        launchCardHoldsHeightCase 390 844
        launchCardHoldsHeightCase 1440 900
        // The way out is a thumb's size on a phone, and says what it does to a screen reader
        // and a pointer alike. Measured as the BOX a press lands on, not the glyph in it.
        editorCaseIn 390 844 "on a phone the launch card's way out is a 44px target with a name" <| fun page ->
            async {
                do! awaitU (page.EvaluateAsync "() => window.__launch(true)")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-repo-picker-dismiss]")
                let! size =
                    await (page.EvaluateAsync<int[]>
                            """() => {
                                 const b = document.querySelector('#shell [data-repo-picker-dismiss]').getBoundingClientRect()
                                 return [Math.round(b.width), Math.round(b.height)]
                               }""")
                Expect.isTrue (size.[0] >= 44 && size.[1] >= 44) (sprintf "44px each way, got %dx%d" size.[0] size.[1])
                let! named =
                    await (page.EvaluateAsync<bool>
                            """() => {
                                 const b = document.querySelector('#shell [data-repo-picker-dismiss]')
                                 return !!b.getAttribute('aria-label') && !!b.getAttribute('title')
                               }""")
                Expect.isTrue named "an accessible name, and a title for a pointer"
            }
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
        // A pane nobody has sized is laid out by the room the chat leaves (`PaneSplit.resolve`).
        // Only a browser can say the boxes land where that arithmetic means them to: the
        // stylesheet draws the measure and the gutter, the model sizes the pane, and the two
        // agreeing is a fact about a laid-out page. Two screens, because the strip the pane was
        // leaving grew with the screen — 100px at 1440, 580px at 1920.
        editorCaseIn 1440 900 "at 1440 a pane nobody sized takes the room the chat's reading column leaves" paneTakesTheRoom
        editorCaseIn 1920 1080 "at 1920 a pane nobody sized takes the room the chat's reading column leaves" paneTakesTheRoom
        // And a width the reader chose is theirs: the screen growing is room the default would
        // take, and a pane somebody sized does not take it.
        editorCaseIn 1440 900 "a pane the reader sized keeps its width when the window grows" <| fun page ->
            async {
                do! paneOpenAndSettled page
                do! awaitU (page.FocusAsync "#shell [data-term-resize]")
                do! awaitU (page.Keyboard.PressAsync "End")
                do! awaitU (page.Keyboard.PressAsync "ArrowLeft")
                do! twoFrames page
                let! chosen =
                    await (page.EvaluateAsync<float>
                            "() => Number(document.querySelector('#shell [data-term-resize]').getAttribute('aria-valuenow'))")
                do! awaitU (page.SetViewportSizeAsync (1920, 1080))
                do! twoFrames page
                // Waited for on the PIXELS meeting the separator again, then read: a pane that
                // grew would settle too, at the default's width rather than the reader's.
                do! waitFor "the pane to settle after the resize" page
                        """(() => {
                             const pane = document.querySelector('#shell [data-content-panel]')
                             const said = Number(document.querySelector('#shell [data-term-resize]').getAttribute('aria-valuenow'))
                             return innerWidth === 1920 && Math.abs(pane.getBoundingClientRect().width - said) <= 1
                           })()"""
                let! width =
                    await (page.EvaluateAsync<float>
                            "() => document.querySelector('#shell [data-content-panel]').getBoundingClientRect().width")
                Expect.isTrue
                    (abs (width - chosen) <= 1.0)
                    (sprintf "the width the reader chose (%.0f), not the room the screen has (now %.0f)" chosen width)
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
                let! _ = await (page.WaitForSelectorAsync "#shell [data-content-toggle='show']")
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
                                 document.querySelector('#shell [data-content-toggle="show"]').click()
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
                // The separator's value is the state the shell actually owns, drawn from the
                // model by the frame after the key that moved it (the page draws at most once a
                // frame, `Render.setState`), so each press is read a frame on rather than raced.
                // It is also the thing this test is ABOUT — what a keyboard user is told the
                // split is. That the pixels follow it is asserted once at the end, where the
                // target is known and the wait is therefore deterministic.
                let value () =
                    page.EvaluateAsync<float>
                        "() => Number(document.querySelector('#shell [data-term-resize]').getAttribute('aria-valuenow'))"

                // Showing the pane hands the keyboard to what it shows (the focus contract,
                // `ClientModel.paneLanding`), a frame AFTER the render. Waited for here, because
                // focusing the divider before that frame is a focus the landing then takes back:
                // on a quick box the frame had always come first, and on a loaded runner it did
                // not, which read as a divider that is not a separator.
                let! _ =
                    await (page.WaitForFunctionAsync
                            """() => !!document.activeElement?.closest('#shell [data-content-panel]')""")

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
                do! twoFrames page
                let! moved = await (value ())
                Expect.isTrue
                    (abs (moved - before) > 1.0)
                    (sprintf "an arrow key must move the split (was %f, still %f)" before moved)
                do! awaitU (page.Keyboard.PressAsync "ArrowRight")
                do! twoFrames page
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
                                 const pane = document.querySelector('#shell [data-content-panel]')
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
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
                // The terminal the harness holds the lease on renders its screen, and the
                // screen shows what the program drew.
                do! awaitU (page.ClickAsync "#shell [data-terminal-tab='term-live']")
                let screen = "#shell [data-terminal-screen='term-live']"
                let! _ = await (page.WaitForSelectorAsync screen)
                let! _ =
                    await (page.WaitForFunctionAsync
                        (sprintf "document.querySelector(%s).textContent.includes('vim ~/notes')" "\"#shell [data-terminal-screen='term-live']\""))

                // A press on it puts the keyboard there, because its whole purpose is having it.
                do! awaitU (page.ClickAsync screen)
                let! _ =
                    await (page.WaitForFunctionAsync
                        """document.activeElement?.getAttribute('data-terminal-keys') === 'term-live'""")

                // The screen is composed by a REAL emulator in a real browser: the
                // Session's snapshot seeds it, and the records the client already
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
                let! before = await (page.EvaluateAsync<string> "() => document.activeElement?.getAttribute('data-terminal-keys')")
                do! awaitU (page.Keyboard.PressAsync "Tab")
                let! after = await (page.EvaluateAsync<string> "() => document.activeElement?.getAttribute('data-terminal-keys')")
                Expect.equal after before "Tab types a tab; it does not leave the terminal"
            }
        // Taking the keyboard is the whole of what live mode is, and the keyboard has to
        // follow it. Only a browser can answer this: `take` removes itself in the render the
        // lease arrives on, so what is under test is where focus ends up after a DOM swap,
        // which is not a fact any rendered string holds.
        editorCase "taking a terminal puts the keyboard in it" <| fun page ->
            async {
                // `term-harness` is the pane's opening tab, and it holds no lease: a terminal
                // in block mode, which is where somebody who wants to type is standing. Not
                // clicked — activating the tab you are already on is the PIN gesture.
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-terminal-take='term-harness']")

                // The press that hands this peer the lease — and removes itself doing it.
                do! awaitU (page.ClickAsync "#shell [data-terminal-take='term-harness']")
                let! _ =
                    await (page.WaitForFunctionAsync
                        """document.activeElement?.getAttribute('data-terminal-keys') === 'term-harness'""")

                // …and it is really the keyboard, not merely a focus ring: what is typed now
                // reaches the pty rather than the composer that used to be there.
                do! awaitU (page.Keyboard.PressAsync "ArrowUp")
                let! typed = await (page.EvaluateAsync<string> "() => window.__typed || ''")
                Expect.equal typed "\u001b[A" "a key pressed after the take reaches the terminal"
            }
        // The alt-screen flip, from where it is usually set off: `vim` typed into the command
        // line and run from it, the block takes the screen, and its author gets the keyboard.
        // The command line STAYS through a lease (it queues for the hand-back), so focus is
        // not stranded on `body` — it is somewhere worse, a line under the editor, where the
        // `i` meant for vim would be typed into the queue instead.
        editorCase "a lease landing on its author's command line takes the keyboard to the screen" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
                let line = "#shell " + commandLine "term-harness"
                let! _ = await (page.WaitForSelectorAsync line)
                do! awaitU (page.FocusAsync line)
                let! _ =
                    await (page.WaitForFunctionAsync
                        """document.activeElement?.getAttribute('data-terminal-input')?.startsWith('term-draft:term-harness:') === true""")

                do! awaitU (page.EvaluateAsync "() => window.__take('term-harness')")
                do! waitFor
                        "the keyboard on the screen the lease landed on, not on the command line under it"
                        page
                        """document.activeElement?.getAttribute('data-terminal-keys') === 'term-harness'"""
            }
        // The other route in, and the reason the focus move lives in the render loop rather
        // than on the press: a block that takes the screen hands its author the keyboard with
        // nobody pressing anything. Focus that SURVIVED that render is not stranded and must
        // not be taken — a terminal going full-screen three tabs away is not a reason to yank
        // somebody's caret out of the message they are writing.
        editorCase "a terminal going live does not take the keyboard from what someone is writing" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
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
        // The way back out of live mode, and the other DOM swap of the focused element in a
        // terminal. Hand it back removes itself once the release it asked for arrives, and a
        // shell that dies takes the command line with it: either way focus fell to `body`, and
        // the next Tab started from the top of the document. It goes where the pane lands
        // instead — the command line, or the panel where there is none.
        editorCase "handing a terminal back puts the keyboard on its command line" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
                do! awaitU (page.ClickAsync "#shell [data-terminal-take='term-harness']")
                let release = "#shell [data-terminal-release='term-harness']"
                let! _ = await (page.WaitForSelectorAsync release)
                // The press, then its answer: this page has no session, so the release arrives
                // the way the session would send it.
                do! awaitU (page.ClickAsync release)
                do!
                    foldHarness page [
                        90L,
                        Yession.Domain.SessionEvent.TerminalLeaseReleased
                            { Yession.Domain.Terminals.TerminalLeaseReleased.TerminalId = harnessTerminal "term-harness"
                              Yession.Domain.Terminals.TerminalLeaseReleased.Was = Yession.Domain.ActorRef.PeerRef (Yession.Domain.PeerId.create "ada" |> Result.defaultWith failwith)
                              Yession.Domain.Terminals.TerminalLeaseReleased.Reason = Yession.Domain.Terminals.LeaseReleased
                              Yession.Domain.Terminals.TerminalLeaseReleased.ToSeq = 0 } ]
                do!
                    waitFor
                        "the keyboard on the command line"
                        page
                        "document.activeElement?.matches(\"#shell [data-terminal-input^='term-draft:']:not([readonly])\") === true"
            }
        editorCase "a shell dying under the reader leaves the keyboard in the pane" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
                let commandLine = "#shell [data-terminal-input^='term-draft:']:not([readonly])"
                let! _ = await (page.WaitForSelectorAsync commandLine)
                do! awaitU (page.FocusAsync commandLine)
                do!
                    foldHarness page [
                        90L,
                        Yession.Domain.SessionEvent.TerminalClosed
                            { Yession.Domain.Terminals.TerminalClosed.TerminalId = harnessTerminal "term-harness"
                              Yession.Domain.Terminals.TerminalClosed.Reason = "the shell exited with code -1"
                              Yession.Domain.Terminals.TerminalClosed.By = None } ]
                do!
                    waitFor
                        "the keyboard in the pane's panel"
                        page
                        "document.activeElement?.closest('#shell [data-pane-panel]') != null"
            }
        // Somebody taking the keyboard from the person typing in it (P3-2). The lease bar
        // renamed its holder, and for the person stolen from that was the whole announcement
        // — their keys simply stopped going anywhere.
        editorCase "a steal tells the person it was taken from, and offers it back" <| fun page ->
            async {
                do! stolenFromHarness page
                do!
                    waitFor
                        "take back, in a notice about the steal"
                        page
                        "!!document.querySelector(\"#shell [data-terminal-stolen='term-harness'] [data-terminal-take='term-harness']\")"
            }
        editorCase "keys typed after a steal do not reach the terminal" <| fun page ->
            async {
                do! stolenFromHarness page
                let! _ = await (page.WaitForSelectorAsync "#shell [data-terminal-stolen='term-harness']")
                do! awaitU (page.Keyboard.TypeAsync "pwd")
                do! awaitU (page.Keyboard.PressAsync "Enter")
                let! typed = await (page.EvaluateAsync<string> "() => window.__typed || ''")
                Expect.equal typed "" "nothing typed after the steal reached the pty"
            }
        // The live screen's ring, on the focus every way into it gives: a script's, which is
        // not `:focus-visible`, so the ring the rest of the page wears painted nothing here.
        editorCase "a focused live screen paints its ring" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
                do! awaitU (page.ClickAsync "#shell [data-terminal-take='term-harness']")
                let! _ =
                    await (page.WaitForFunctionAsync
                        """document.activeElement?.getAttribute('data-terminal-keys') === 'term-harness'""")
                let! ring =
                    await (page.EvaluateAsync<string> """() => {
                        const s = getComputedStyle(document.activeElement.closest('[data-terminal-screen]'));
                        return s.outlineStyle + ' ' + s.outlineWidth; }""")
                Expect.equal ring "solid 2px" "the screen with the keyboard wears the ring"
            }
        // The caret is read off the browser's own emulator — the cursor is not in the
        // serialization in any form a renderer can use, so only a real one can say where it is.
        editorCase "the live screen marks where its cursor stands" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
                do! awaitU (page.ClickAsync "#shell [data-terminal-take='term-harness']")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-terminal-screen='term-harness'] [data-terminal-keys]")
                // A line typed, then the cursor moved four cells back along it.
                do! awaitU (page.EvaluateAsync ("() => window.__snapshot('term-harness', 50, '$ echo hi\\u001b[4D', 80, 24)"))
                do!
                    waitFor
                        "the caret on the o of echo"
                        page
                        "document.querySelector(\"#shell [data-terminal-screen='term-harness'] [data-terminal-caret]\")?.textContent === 'o'"
            }
        // A phone (P3-3). A platform raises its soft keyboard for a focused text field and for
        // nothing else, and the live screen was a focusable div: a tap focused it, nothing came
        // up, and the one mode that exists to be typed into could not be typed into. What is
        // asked is what a phone needs to show a keyboard — a text field holding focus, inside
        // the screen — because no headless browser has a keyboard to show.
        editorCaseOnTouch 390 844 "tapping the live screen puts the keyboard in a text field" <| fun page ->
            async {
                do! awaitU (page.TapAsync "#shell [data-content-toggle='show']")
                do! awaitU (page.TapAsync "#shell [data-terminal-tab='term-live']")
                let screen = "#shell [data-terminal-screen='term-live']"
                let! _ = await (page.WaitForSelectorAsync screen)
                do! awaitU (page.TapAsync screen)
                do!
                    waitFor
                        "a text field inside the live screen holding focus"
                        page
                        """document.activeElement?.tagName === 'TEXTAREA'
                           && document.activeElement.closest("[data-terminal-screen='term-live']") !== null"""
            }
        // What a phone keyboard sends is mostly not keys: it INSERTS text into the focused
        // field, with a keydown that names no key. `insertText` is exactly that event and no
        // keydown at all, so this reaches the pty only through the insertion path — and the
        // second insertion arriving alone is what says the field let go of the first.
        editorCaseOnTouch 390 844 "text typed on a phone reaches the terminal as the bytes a keyboard sends" <| fun page ->
            async {
                do! awaitU (page.TapAsync "#shell [data-content-toggle='show']")
                do! awaitU (page.TapAsync "#shell [data-terminal-tab='term-live']")
                let screen = "#shell [data-terminal-screen='term-live']"
                let! _ = await (page.WaitForSelectorAsync screen)
                do! awaitU (page.TapAsync screen)
                let! _ = await (page.WaitForFunctionAsync "document.activeElement?.getAttribute('data-terminal-keys') === 'term-live'")
                do! awaitU (page.Keyboard.InsertTextAsync "bob")
                do! awaitU (page.Keyboard.InsertTextAsync "\n")
                let! typed = await (page.EvaluateAsync<string> "() => window.__typed || ''")
                Expect.equal typed "bob\r" "the word once, and Return as a carriage return"
            }
        // The other half of putting the keyboard in a field on a press: a press that ENDS a
        // selection is somebody copying output, and focusing a text field would take the
        // selection out from under them.
        editorCase "selecting output on the live screen keeps the selection" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
                do! awaitU (page.ClickAsync "#shell [data-terminal-tab='term-live']")
                let! _ =
                    await (page.WaitForFunctionAsync
                        "document.querySelector(\"#shell [data-terminal-screen='term-live']\")?.textContent.includes('vim ~/notes') === true")
                // Where the words are, so the drag runs across them rather than across padding.
                let! span =
                    await (page.EvaluateAsync<float[]>
                        """() => {
                             const screen = document.querySelector("#shell [data-terminal-screen='term-live']")
                             const walker = document.createTreeWalker(screen, NodeFilter.SHOW_TEXT)
                             let node
                             while ((node = walker.nextNode()) && !node.textContent.includes('vim ~/notes')) {}
                             const range = document.createRange()
                             const at = node.textContent.indexOf('vim ~/notes')
                             range.setStart(node, at)
                             range.setEnd(node, at + 'vim ~/notes'.length)
                             const r = range.getBoundingClientRect()
                             return [r.left + 1, r.right - 1, (r.top + r.bottom) / 2]
                           }""")
                do! awaitU (page.Mouse.MoveAsync (float32 span.[0], float32 span.[2]))
                do! awaitU (page.Mouse.DownAsync ())
                do! awaitU (page.Mouse.MoveAsync (float32 span.[1], float32 span.[2], MouseMoveOptions (Steps = 5)))
                do! awaitU (page.Mouse.UpAsync ())
                let! selected = await (page.EvaluateAsync<string> "() => getSelection().toString()")
                Expect.stringContains selected "notes" "what was dragged across is still selected"
            }
        // A phone's keyboard takes the bottom of the screen, and the command line and its Run
        // are at the bottom of the pane. The keyboard is the viewport getting shorter under the
        // page — what a keyboard that resizes the layout does — and what is measured is whether
        // the line and its button are still above where it now ends.
        editorCaseOnTouch 390 844 "the command line and its Run stay above a raised keyboard" <| fun page ->
            async {
                do! awaitU (page.TapAsync "#shell [data-content-toggle='show']")
                let line = "#shell " + commandLine "term-harness"
                let! _ = await (page.WaitForSelectorAsync line)
                do! awaitU (page.TapAsync line)
                do! awaitU (page.SetViewportSizeAsync (390, 544))
                do!
                    waitFor
                        "the command line and Run inside a viewport 300px shorter"
                        page
                        (sprintf
                            """(() => {
                                 const line = document.querySelector("%s")
                                 const run = line?.parentElement.querySelector('[data-terminal-send]')
                                 return innerHeight === 544 && !!run
                                   && line.getBoundingClientRect().bottom <= innerHeight
                                   && run.getBoundingClientRect().bottom <= innerHeight
                               })()"""
                            line)
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
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
                do! awaitU (page.ClickAsync "#shell [data-terminal-tab='term-live']")
                let screen = "#shell [data-terminal-screen='term-live']"
                let! _ = await (page.WaitForSelectorAsync screen)
                do! awaitU (page.FocusAsync (screen + " [data-terminal-keys]"))
                let! _ =
                    await (page.WaitForFunctionAsync
                        """document.activeElement?.getAttribute('data-terminal-keys') === 'term-live'""")

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
        // The page draws at most once a frame (`Render.setState`). A burst of pty output is a
        // message per read, and a render per message put seconds of drawing in front of the
        // link's heartbeat on a phone — a busy peer dropped as dead. These used to be one case
        // in the cheap tier, when the CONNECTION held records for a frame; the rule is the
        // render's now, and renders are only countable where there is a page.
        testList "The page draws at most once a frame" [
            editorCase "a burst of live records costs a render per frame, not one per record" <| fun page ->
                async {
                    do! twoFrames page
                    let! renders = afterBurst page "renders"
                    // The frame the burst landed in, drawn as the first record arrived, and the
                    // next, drawn with the other forty-nine.
                    Expect.isTrue
                        (int renders <= 2)
                        (sprintf "fifty records across one frame boundary drew %s times; at most two" renders)
                }
            editorCase "the last record of a burst is on the page by the next frame" <| fun page ->
                async {
                    do! paneOnBlocks page
                    let! shown =
                        afterBurst
                            page
                            "document.querySelector(\"#shell [data-tail='blocks:term-harness']\").textContent.includes('burst-mark 51')"
                    Expect.equal shown "true" "the record the frame's render was owed is drawn, not the one that asked first"
                }
            // A frame later is a frame of latency nobody should pay for one keystroke's worth of
            // change. So the case dispatches from inside a frame in which nothing has been drawn
            // yet, and reads the page back in the same breath: a page drawn whenever the next
            // frame comes would read "not yet".
            editorCase "a lone message is on the page before the call that dispatched it returns" <| fun page ->
                async {
                    do! paneOnBlocks page
                    let! answer =
                        await (
                            page.EvaluateAsync<string>
                                """async () => {
                                     for (let tries = 0; tries < 60; tries++) {
                                       const seen = globalThis.__yessionRenders ?? 0
                                       const answer = await new Promise(done => requestAnimationFrame(() => {
                                         // A render since the last look may still have its frame
                                         // open; look again a frame on.
                                         if ((globalThis.__yessionRenders ?? 0) !== seen) return done(null)
                                         window.__record('term-harness', 2, 'o', 'lone-mark\r\n')
                                         const surface = document.querySelector("#shell [data-tail='blocks:term-harness']")
                                         done(surface.textContent.includes('lone-mark') ? 'on the page' : 'not yet')
                                       }))
                                       if (answer !== null) return answer
                                     }
                                     return 'the page drew on every one of sixty frames, so none was quiet to ask in'
                                   }""")
                    Expect.equal answer "on the page" "a message into an undrawn frame is drawn at once"
                }
        ]
        // `ESC[500G` is how a program asks for the last column, whatever that is. The
        // serializer answers with the gap it measured — `ESC[39C` on a 40-column screen,
        // `ESC[99C` on a 100-column one — so this reads the width straight off the rendered
        // line, and would have read 79 for both back when the snapshot carried no size.
        editorCase "the live screen is the shape the process says it is" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
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
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
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
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
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
        // The drawer is opened by its chevron, because the drawer is the model's: a class set
        // by hand is one the model never heard of, and closing it would then be nothing the
        // jump had done. The case reads the cover BEFORE the tap as well as after, so an
        // arrangement that stopped covering anything would fail here rather than pass by
        // vacuity.
        editorCaseIn 390 844 "a chapter reached from the phone's contents is not left behind the drawer" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-nav-toggle='show']")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-chapters] [data-chapter-entry]")
                let! covered =
                    await (page.EvaluateAsync<bool>
                            """() => {
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
        // The contents' entries on a phone are touch targets, so each is one row at the 44px
        // floor and the stack adds nothing between them: a gap on top of a full-height entry
        // read as two chapters a hand apart. Measured rather than read from the markup, since
        // a row's height is what its padding, its line and the stack's gap add up to.
        editorCaseIn 390 844 "on a phone the contents' entries are single 44px rows with nothing between them" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-nav-toggle='show']")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-chapters] [data-chapter-entry]")
                let! shape =
                    await (page.EvaluateAsync<float[]> """() => {
                        const boxes = [...document.querySelectorAll('#shell [data-chapters] [data-chapter-entry]')]
                            .map(el => el.getBoundingClientRect())
                        const heights = boxes.map(b => b.height)
                        const gaps = boxes.slice(1).map((b, at) => b.top - boxes[at].bottom)
                        return [boxes.length, Math.min(...heights), Math.max(...heights), Math.max(...gaps)]
                    }""")
                Expect.isTrue (shape.[0] >= 2) "the harness has more than one chapter, so there is a gap to measure"
                Expect.isTrue (shape.[1] >= 44.0) (sprintf "every entry is a thumb's 44px, the shortest is %.1f" shape.[1])
                Expect.isTrue (shape.[2] < 50.0) (sprintf "and none is taller than one row, the tallest is %.1f" shape.[2])
                Expect.isTrue (shape.[3] <= 1.0) (sprintf "with no gap between neighbours, the widest is %.1f" shape.[3])
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
        // A conversation shorter than its column ends where the next message is typed. Top
        // aligned, three messages on a 1440x900 screen ended 598px above the composer, with the
        // empty space between the newest line and the field answering it. Only a laid-out
        // page knows where the last item landed; the markup is identical either way.
        //
        // The shortest conversation the harness draws (the scroll scenario's, with no filler)
        // on a tall desktop screen, which leaves it a few hundred pixels short of the column.
        // That it fits is asserted, because a conversation that fills the column ends at its
        // foot however it is aligned, and the case would pass for nothing.
        editorCaseIn 1440 1200 "a short conversation ends by the composer, not at the top of the column" <| fun page ->
            async {
                let! _ = await (page.WaitForSelectorAsync "#shell [data-conversation] [data-message-id]")
                do! awaitU (page.EvaluateAsync "() => { window.__benchScrollBegin(0, 0, 16, true); window.__benchScrollEnd() }")
                let! report =
                    await (page.EvaluateAsync<string>
                            """() => {
                                 const conversation = document.querySelector('#shell [data-conversation]')
                                 const last = conversation.lastElementChild.getBoundingClientRect()
                                 const foot = conversation.getBoundingClientRect().bottom
                                   - parseFloat(getComputedStyle(conversation).paddingBottom)
                                 return JSON.stringify({
                                   fits: conversation.scrollHeight <= conversation.clientHeight,
                                   gap: foot - last.bottom })
                               }""")
                use doc = System.Text.Json.JsonDocument.Parse report
                let fits = doc.RootElement.GetProperty("fits").GetBoolean ()
                let gap = doc.RootElement.GetProperty("gap").GetDouble ()
                Expect.isTrue fits "the conversation is shorter than its column, so where it ends is a choice"
                Expect.isTrue (abs gap < 1.0) (sprintf "the last item ends %.0fpx above the column's foot" gap)
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
                // To the end of what the field holds, so this adds rather than replacing — an
                // edit against the text the session holds is what the field is for, and
                // appending is the edit most likely to expose a diff computed against the
                // wrong side. (A chapter nobody has named holds nothing, so the first words
                // typed here ARE its name.)
                do! awaitU (page.Keyboard.PressAsync "End")
                do! awaitU (page.Keyboard.TypeAsync " — settled")
                let! kept =
                    await (page.WaitForFunctionAsync
                        """document.querySelector("#shell [data-chapter-name='msg-filler-8']")
                             ?.value.endsWith(' — settled') === true""")
                Expect.isNotNull kept "what was typed is what the rule says, after the render that followed it"
                return ()
            }
        // The divider of a chapter nobody has named shows no name — the opening message is
        // right under it — so the empty field is how it still invites one. The invitation
        // is a promise only a painted page can settle: the placeholder is a pseudo-element
        // whose ink is whatever the cascade resolves, so the markup carries the attribute
        // identically whether it shows at rest, never, or only for a pointer. It must be
        // absent at rest (a clean line), and present for the keyboard's focus, which has no
        // hover to stand in for it.
        editorCaseIn 1440 900 "an unnamed chapter's field offers a name only when focused or hovered" <| fun page ->
            async {
                let! _ = await (page.WaitForSelectorAsync "#shell [data-chapter-name='msg-filler-8']")
                let probe =
                    """() => {
                         const field = document.querySelector("#shell [data-chapter-name='msg-filler-8']")
                         const ink = getComputedStyle(field, '::placeholder').color
                         return JSON.stringify({
                           empty: field.value === '',
                           offered: field.placeholder.length > 0,
                           shown: ink !== 'rgba(0, 0, 0, 0)' && ink !== 'transparent' })
                       }"""
                let read () =
                    async {
                        let! report = await (page.EvaluateAsync<string> probe)
                        use doc = System.Text.Json.JsonDocument.Parse report
                        let flag (name: string) = doc.RootElement.GetProperty(name).GetBoolean ()
                        return flag "empty", flag "offered", flag "shown"
                    }
                // Away from the field, and not focused: the line is clean.
                do! awaitU (page.Mouse.MoveAsync (700.0f, 10.0f))
                let! empty, offered, atRest = read ()
                Expect.isTrue (empty && offered) "the field is empty and has something to offer, so the next two readings mean something"
                Expect.isFalse atRest "at rest the divider shows no placeholder"
                // The keyboard's own way in: focus with no pointer over the field.
                do! awaitU (page.FocusAsync "#shell [data-chapter-name='msg-filler-8']")
                do! awaitU (page.Mouse.MoveAsync (700.0f, 10.0f))
                let! _, _, focused = read ()
                Expect.isTrue focused "keyboard focus on the empty field shows the placeholder"
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
                // A name to be IN. The rule of a chapter nobody has named is a plain divider
                // with an empty field, and a caret at index 3 and at index 9 of nothing are
                // the same place — so somebody names it first, as a person would.
                do! awaitU (page.ClickAsync "#shell [data-chapter-name='msg-filler-8']")
                do! awaitU (page.Keyboard.TypeAsync "Where it was settled")
                let! _ =
                    await (page.WaitForFunctionAsync
                        """document.querySelector("#shell [data-chapter-name='msg-filler-8']")
                             ?.value === 'Where it was settled'""")
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
        // The "jump to latest" control is an opaque square floating over a scrolling column,
        // so wherever it stands it hides what is under it. Centred, it stood on the prose:
        // measured on a phone it covered 22px of a paragraph. Markup cannot see that — the
        // control is in the document and so are the words, whichever is on top.
        //
        // A phone only. The screen where a line of words runs the whole width is the one
        // that can fail this; at 1440 the fixture's lines stop short of where the centred
        // control stood, so a desktop case passed with the old placement as well and would
        // have been a case that cannot go red.
        editorCaseIn 390 844 "the jump to the latest message covers none of the words on a phone" <| jumpCoversNoWords
        // The surfaces read from their end, one list so `--only "read from their end"` runs them.
        testList "Surfaces read from their end" [
            // Taking the keyboard puts the reader at the CURSOR — the bottom of the screen, where
            // the prompt is and what they type appears. The live screen used to be no surface the
            // scroll rule knew, so it opened at the top of its scrollback: measured on master,
            // scrollTop 0 of 16,640px, with the prompt being answered sixteen thousand pixels down.
            editorCaseIn 1440 900 "taking the keyboard opens the live screen at its cursor, not at the top of its scrollback" <| fun page ->
                async {
                    do! paneOnBlocks page
                    // A screen whose scrollback runs far past its box, seeded past everything the
                    // fixture's feed holds so the prompt is the last thing on it.
                    do! awaitU (
                            page.EvaluateAsync
                                """() => window.__snapshot('term-harness', 50,
                                           Array.from({ length: 300 }, (_, i) => 'old ' + (i + 1)).join('\r\n') + '\r\nprompt-mark$ ')""")
                    do! awaitU (page.ClickAsync "#shell [data-terminal-take='term-harness']")
                    let! shown = newestShown page "screen:term-harness" "prompt-mark"
                    Expect.stringContains shown shownFully "the prompt is on screen as the keyboard arrives"
                }
            // A reader at the end of the conversation sees what is said next. The rule used to move
            // them only once the end was more than 200px away, so a message shorter than that
            // landed below the fold — and the next one, and the next, until the shortfall crossed
            // the line: measured on master, 80px short, the newest chip half under the composer.
            editorCaseIn 1440 900 "a reader at the end of the conversation sees the message that lands next" <| fun page ->
                async {
                    let! _ = await (page.WaitForSelectorAsync "#shell [data-conversation] [data-message-body]")
                    do! toTheEnd page "chat"
                    do! messageLands page 200L "tail-mark-chat, which has just been said"
                    let! shown = newestShown page "chat" "tail-mark-chat"
                    Expect.stringContains shown shownFully "the newest message is wholly on screen"
                }
            // The same promise over a terminal's blocks, where what arrives is output.
            editorCaseIn 1440 900 "a reader at the end of a terminal's blocks sees the output that arrives next" <| fun page ->
                async {
                    do! paneOnBlocks page
                    do! blocksFilled page
                    do! toTheEnd page "blocks:term-harness"
                    // A new command, and its first line: the newest thing a terminal can show.
                    let expect r = Result.defaultWith failwith r
                    do! foldHarness page [
                            300L,
                            Yession.Domain.SessionEvent.TerminalBlockStarted
                                { Yession.Domain.Terminals.TerminalBlockStarted.TerminalId = harnessTerminal "term-harness"
                                  Yession.Domain.Terminals.TerminalBlockStarted.BlockId = Yession.Domain.BlockId.create "block-tail" |> expect
                                  Yession.Domain.Terminals.TerminalBlockStarted.QueueId = None
                                  Yession.Domain.Terminals.TerminalBlockStarted.Authority =
                                      Yession.Domain.Authority.agentFor (Yession.Domain.Principal.Peer (Yession.Domain.PeerId.create "ada" |> expect))
                                  Yession.Domain.Terminals.TerminalBlockStarted.Command = "make"
                                  Yession.Domain.Terminals.TerminalBlockStarted.FromSeq = 3
                                  Yession.Domain.Terminals.TerminalBlockStarted.Background = false } ]
                    do! awaitU (page.EvaluateAsync "() => window.__record('term-harness', 3, 'o', 'tail-mark-blocks\\r\\n')")
                    let! shown = newestShown page "blocks:term-harness" "tail-mark-blocks"
                    Expect.stringContains shown shownFully "the newest line of output is wholly on screen"
                }
            // And over the live screen: a program printing below the cursor scrolls the screen, and
            // a reader at its end goes with it.
            editorCaseIn 1440 900 "a reader at the end of a live screen sees the output that arrives next" <| fun page ->
                async {
                    do! paneOnBlocks page
                    do! awaitU (
                            page.EvaluateAsync
                                """() => window.__snapshot('term-harness', 50,
                                           Array.from({ length: 300 }, (_, i) => 'old ' + (i + 1)).join('\r\n') + '\r\n')""")
                    do! awaitU (page.ClickAsync "#shell [data-terminal-take='term-harness']")
                    let! _ = await (page.WaitForSelectorAsync (tailSurface "screen:term-harness"))
                    do! toTheEnd page "screen:term-harness"
                    do! awaitU (page.EvaluateAsync "() => window.__record('term-harness', 51, 'o', 'one\\r\\ntwo\\r\\ntail-mark-screen\\r\\n')")
                    let! shown = newestShown page "screen:term-harness" "tail-mark-screen"
                    Expect.stringContains shown shownFully "the newest line on the screen is wholly on screen"
                }
            // The other half: a reader who scrolled back to read is LEFT there. What arrives while
            // they read is what the way back is for, not a reason to take them away from the line
            // they were on.
            editorCaseIn 1440 900 "a reader who scrolled back through the conversation stays where they are as a message lands" <| fun page ->
                async {
                    let! _ = await (page.WaitForSelectorAsync "#shell [data-conversation] [data-message-body]")
                    do! toTheEnd page "chat"
                    do! toTheStart page "chat"
                    do! messageLands page 200L "tail-mark-chat, said while nobody was at the end"
                    let! _ = await (page.WaitForFunctionAsync "document.querySelector(\"#shell [data-tail='chat']\").textContent.includes('tail-mark-chat')")
                    do! twoFrames page
                    let! top = await (page.EvaluateAsync<float> "() => document.querySelector(\"#shell [data-tail='chat']\").scrollTop")
                    Expect.equal top 0.0 "the reader is still at the line they scrolled back to"
                }
            // …and is told there is more below, over a terminal as over the chat. Output a reader
            // scrolled away from used to arrive with nothing on screen to say so.
            editorCaseIn 1440 900 "a reader who scrolled back through a terminal's blocks is offered the way to the latest" <| fun page ->
                async {
                    do! paneOnBlocks page
                    do! blocksFilled page
                    do! toTheEnd page "blocks:term-harness"
                    do! toTheStart page "blocks:term-harness"
                    do! waitFor "the way to the latest output to be on screen" page (jumpOffered "blocks:term-harness")
                }
            // Pressing the way back lands at the end, and the keyboard lands with it. The press takes
            // its own control away — a reader at the end has nowhere to jump to — and it used to
            // leave focus on `body`, so the next Tab started from the top of the page.
            editorCaseIn 1440 900 "the jump to the latest message lands at the end with the keyboard on the conversation" <| fun page ->
                async {
                    let! _ = await (page.WaitForSelectorAsync "#shell [data-conversation] [data-message-body]")
                    do! toTheEnd page "chat"
                    do! toTheStart page "chat"
                    do! waitFor "the way to the latest message to be on screen" page (jumpOffered "chat")
                    do! awaitU (page.FocusAsync "#shell [data-jump-to-latest='chat'] button")
                    do! awaitU (page.Keyboard.PressAsync "Enter")
                    do! waitFor "the conversation to reach its end" page (atItsEnd "chat")
                    let! focused = await (page.EvaluateAsync<string> "() => document.activeElement?.getAttribute('data-tail') ?? document.activeElement?.tagName ?? 'nothing'")
                    Expect.equal focused "chat" "the keyboard is on the conversation it was taken to"
                }
            // The same press over a terminal's blocks, whose scroller is no place for focus of its
            // own: it lands on the panel the blocks are shown in.
            editorCaseIn 1440 900 "the jump to the latest output lands at the end with the keyboard in the pane" <| fun page ->
                async {
                    do! paneOnBlocks page
                    do! blocksFilled page
                    do! toTheEnd page "blocks:term-harness"
                    do! toTheStart page "blocks:term-harness"
                    do! waitFor "the way to the latest output to be on screen" page (jumpOffered "blocks:term-harness")
                    do! awaitU (page.FocusAsync "#shell [data-jump-to-latest='blocks:term-harness'] button")
                    do! awaitU (page.Keyboard.PressAsync "Enter")
                    do! waitFor "the blocks to reach their end" page (atItsEnd "blocks:term-harness")
                    let! inPane =
                        await (page.EvaluateAsync<bool> "() => !!document.activeElement?.closest('#shell [data-content-panel]') && !document.activeElement.closest('[data-jump-to-latest]')")
                    Expect.isTrue inPane "the keyboard is in the pane, on what holds the blocks it was taken to"
                }
        ]
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
        // A control lifted by hover is a control a keyboard never sees lifted unless focus
        // lifts it too, and a faint rest keeps it in the tab order either way — so the failure
        // is not an unreachable control but a FAINT one that is nonetheless the focused thing.
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
        // A phone never hovers, so an item's actions are either on the screen at rest or they
        // are not on it at all — and they are also what marks where one message ends and the
        // next begins in a run from one person. Every item, not the first: the fixture's
        // sixteen one-liners under one author are exactly the dense run that needs it.
        //
        // Brought to the middle of the scrollport one at a time, so the sticky author line
        // and the foot of the column are never what the hit-test lands on. What is asked of
        // each is that it is painted (not transparent) and that its own centre belongs to it:
        // a control under something else is a control a thumb cannot reach.
        editorCaseOnTouch 390 844 "on a phone every item's actions are on the screen and under the thumb" <| fun page ->
            async {
                let! _ = await (page.WaitForSelectorAsync "#shell [data-conversation] [data-item-actions]")
                let! noHover = await (page.EvaluateAsync<bool> "() => matchMedia('(hover: none)').matches")
                Expect.isTrue noHover "the page is laid out for a device that cannot hover"
                let! asked =
                    await (page.EvaluateAsync<int>
                            "() => document.querySelectorAll('#shell [data-conversation] [data-item-actions]').length")
                Expect.isTrue (asked > 1) (sprintf "the fixture draws a run of items to ask about (drew %d)" asked)
                // Settled first. On a phone the terminals pane slides off to the side as the
                // shell lays out, and a hit-test taken while it is on its way lands on the pane
                // rather than on the column under it — the screen not having arrived yet, not a
                // control a thumb cannot reach. Every animation that ends is waited for (a pulse
                // or a blink never ends, and is not).
                do! awaitU (
                        page.EvaluateAsync
                            """() => Promise.all(
                                 document.getAnimations()
                                   .filter(a => a.effect && a.effect.getComputedTiming().iterations !== Infinity)
                                   .map(a => a.finished.catch(() => null)))""")
                let! unreachable =
                    await (page.EvaluateAsync<string[]>
                            """() => {
                                 const wrong = []
                                 for (const control of document.querySelectorAll('#shell [data-conversation] [data-item-actions]')) {
                                   control.scrollIntoView({ block: 'center' })
                                   const box = control.getBoundingClientRect()
                                   const hit = document.elementFromPoint(box.left + box.width / 2, box.top + box.height / 2)
                                   const opacity = Number(getComputedStyle(control).opacity)
                                   if (opacity === 0 || !hit || !control.contains(hit))
                                     wrong.push(control.getAttribute('data-item-actions') + ' (opacity ' + opacity
                                       + ', under ' + (hit ? hit.outerHTML.slice(0, 80) : 'nothing') + ')')
                                 }
                                 return wrong
                               }""")
                Expect.isEmpty
                    unreachable
                    (sprintf "every item's actions are painted and hit-test to themselves; these are not: %s"
                        (String.concat ", " unreachable))
                return ()
            }
        // Reduced motion is honoured by the columns at both widths: a person who asked for no
        // motion gets a drawer that appears rather than slides, and a column that shuts rather
        // than sweeps shut. The phone one used to slide anyway.
        // A credential that stopped working, at both widths the prompt lays out for.
        signInButtonHoldsStill 390 844
        signInButtonHoldsStill 1440 900
        columnHoldsStill 390 844 "terminals" "term-closed"
        columnHoldsStill 390 844 "sidebar" "nav-alt"
        columnHoldsStill 1440 900 "terminals" "term-closed"
        columnHoldsStill 1440 900 "sidebar" "nav-alt"
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
        // Where the caret stands is the whole of what it says: the next word lands THERE. It
        // was appended after the rendered body, which is after the last paragraph's box, so
        // it stood on a line of its own under the text — and a mark aligned "by eye" to a
        // baseline sits wherever its centre puts it, which on a phone is a pixel off. Both
        // are geometry a cheap tier cannot see: the markup is the same either way.
        //
        // The letters are measured, not assumed: an empty inline-block rests its bottom
        // edge on the baseline of the line it is in, so one `1ex` tall dropped after the
        // last word spans exactly where that word's lowercase stands — baseline to the top
        // of an `x` — and the diamond is held to both, within the overshoot a point is
        // carried past its line.
        editorCaseIn 390 844 "the agent's caret spans its last line's letters, just after the last word" <| fun page ->
            async {
                // Held still, so the mark is measured at rest rather than mid-turn.
                do! awaitU (page.EmulateMediaAsync (PageEmulateMediaOptions (ReducedMotion = ReducedMotion.Reduce)))
                let! _ = await (page.WaitForSelectorAsync "#shell [data-draft-editor]")
                do! awaitU (page.EvaluateAsync "() => window.__agentTurn()")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-agent-writing]")
                let! misplaced =
                    await (page.EvaluateAsync<string>
                            """() => {
                                 const caret = document.querySelector('#shell [data-agent-writing]')
                                 const body = caret.closest('[data-message-body]')
                                 const walk = document.createTreeWalker(body, NodeFilter.SHOW_TEXT)
                                 let last = null
                                 while (walk.nextNode()) if (walk.currentNode.textContent.trim()) last = walk.currentNode
                                 if (!last) return 'the body has no words to stand after'
                                 const end = last.textContent.trimEnd().length
                                 const range = document.createRange()
                                 range.setStart(last, end - 1)
                                 range.setEnd(last, end)
                                 const glyph = range.getBoundingClientRect()
                                 const probe = document.createElement('span')
                                 probe.style.cssText = 'display:inline-block;width:0;height:1ex'
                                 last.after(probe)
                                 const letters = probe.getBoundingClientRect()
                                 const baseline = letters.bottom
                                 probe.remove()
                                 const at = caret.getBoundingClientRect()
                                 const em = parseFloat(getComputedStyle(last.parentElement).fontSize)
                                 const wrong = []
                                 if (at.top < glyph.top || at.bottom > glyph.bottom)
                                   wrong.push('it is not on the last line (' + at.top + '-' + at.bottom + ' against ' + glyph.top + '-' + glyph.bottom + ')')
                                 // Within an overshoot: a point is carried a hair past its
                                 // line, as the round letters beside it are.
                                 if (Math.abs(at.bottom - baseline) > 0.05 * em)
                                   wrong.push('its lower point is ' + (at.bottom - baseline) + 'px off the baseline')
                                 if (Math.abs(at.top - letters.top) > 0.05 * em)
                                   wrong.push('its upper point is ' + (at.top - letters.top) + 'px off the x-height')
                                 if (at.left < glyph.right) wrong.push('it starts before the last word ends')
                                 if (at.left - glyph.right > em) wrong.push('it stands ' + (at.left - glyph.right) + 'px after the last word')
                                 return wrong.join('; ')
                               }""")
                Expect.equal misplaced "" "the caret stands where the next word lands"
                return ()
            }
        // The thinking mark MOVES — a cube tipped, flipped and tumbled — and a cube turned
        // toward its corner stands 1.2x taller than the diamond on top of it. So the box the
        // mark is placed by (held to its letters at rest, by the caret case above) has to hold
        // what it paints in every frame, or a move pokes out of the lowercase it stands among.
        // Measured on what paints — the leaves of the mark, each a face whose projected box a
        // browser reports through the perspective — at every 20ms of one whole cycle, its
        // animations paused and stepped together, against the mark's own box as drawn.
        editorCaseIn 390 844 "the agent's thinking mark never paints outside its box through any move" <| fun page ->
            async {
                let! _ = await (page.WaitForSelectorAsync "#shell [data-draft-editor]")
                do! awaitU (page.EvaluateAsync "() => window.__agentThinks()")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-agent-writing]")
                let! outside =
                    await (page.EvaluateAsync<string>
                            """() => {
                                 const mark = document.querySelector('#shell [data-agent-writing]')
                                 const box = mark.getBoundingClientRect()
                                 const em = parseFloat(getComputedStyle(mark).fontSize)
                                 const leaves = [...mark.querySelectorAll('*')].filter(e => !e.firstElementChild)
                                 const moves = mark.getAnimations({ subtree: true })
                                 if (moves.length === 0) return 'the thinking mark does not move'
                                 const cycle = Math.max(...moves.map(a => a.effect.getComputedTiming().duration))
                                 let worst = null
                                 for (let t = 0; t < cycle; t += 20) {
                                   moves.forEach(a => { a.pause(); a.currentTime = t })
                                   const boxes = (leaves.length ? leaves : [mark]).map(e => e.getBoundingClientRect())
                                   const over = Math.max(
                                     box.top - Math.min(...boxes.map(b => b.top)),
                                     Math.max(...boxes.map(b => b.bottom)) - box.bottom,
                                     box.left - Math.min(...boxes.map(b => b.left)),
                                     Math.max(...boxes.map(b => b.right)) - box.right) - 0.05 * em
                                   if (over > 0 && (!worst || over > worst.over)) worst = { over, t }
                                 }
                                 return worst ? 'it paints ' + worst.over.toFixed(2) + 'px outside its box at ' + worst.t + 'ms' : ''
                               }""")
                Expect.equal outside "" "the thinking mark stays inside the box it is placed by"
                return ()
            }
        // The turn opens with its mark stood larger where the reply will begin; the first word
        // puts the caret down at the end of that word. The promise is that it is the SAME mark
        // going there — it leaves from where it stood rather than vanishing in one place and
        // appearing in another. So at the first frame of its arrival the caret paints where the
        // opening mark stood, at that mark's size: centres and heights compared, each read from
        // what the browser drew, with the arrival paused at its start.
        editorCaseIn 390 844 "the first word takes the agent's mark from where the turn began" <| fun page ->
            async {
                let! _ = await (page.WaitForSelectorAsync "#shell [data-draft-editor]")
                do! awaitU (page.EvaluateAsync "() => window.__agentThinks()")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-agent-writing]")
                let! _ =
                    await (page.EvaluateAsync<bool>
                            """() => { const r = document.querySelector('#shell [data-agent-writing]').getBoundingClientRect()
                                       const scrolled = document.querySelector('#shell [data-conversation]').scrollTop
                                       window.__opening = { x: r.left + r.width / 2, y: r.top + r.height / 2 + scrolled, h: r.height }
                                       return true }""")
                // Said and measured in one evaluation, so the arrival is caught at its start
                // however slow the round trip to the page is.
                let! apart =
                    await (page.EvaluateAsync<string>
                            """async () => {
                                 window.__agentSays()
                                 let caret = null
                                 for (let i = 0; i < 60 && !caret; i++) {
                                   const m = document.querySelector('#shell [data-agent-writing]')
                                   if (m && !m.firstElementChild) caret = m
                                   else await new Promise(r => requestAnimationFrame(r))
                                 }
                                 if (!caret) return 'the first word put down no caret'
                                 await new Promise(r => requestAnimationFrame(r))
                                 const arriving = caret.getAnimations()
                                 if (arriving.length === 0) return 'the caret appears where it lands, from nowhere'
                                 arriving.forEach(a => { a.pause(); a.currentTime = 0 })
                                 // In the CONVERSATION's coordinates, not the window's: a reader at
                                 // the end is kept there as the first word lands (`Tail`), and the
                                 // column scrolling under both marks is not the caret leaving.
                                 const r = caret.getBoundingClientRect()
                                 const scrolled = document.querySelector('#shell [data-conversation]').scrollTop
                                 const at = { x: r.left + r.width / 2, y: r.top + r.height / 2 + scrolled, h: r.height }
                                 const was = window.__opening
                                 const off = Math.max(Math.abs(at.x - was.x), Math.abs(at.y - was.y), Math.abs(at.h - was.h))
                                 return off > 1 ? 'it leaves ' + off.toFixed(2) + 'px from where the turn\'s mark stood' : ''
                               }""")
                Expect.equal apart "" "the caret starts from the opening mark"
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
        // using, and on a phone the verbs that REACT TO CONTENT are the room: Send and
        // Interrupt leave the line and sit below it, standing only once there is a draft or a
        // turn for them to act on. `opacity-0` kept every pixel of that row while hiding it —
        // 44px of invisible buttons, plus the clearance meant to land under them — so two
        // thirds of an empty composer was band holding nothing.
        //
        // The model picker does NOT belong to that promise (`Style.draftLead`'s own doc
        // comment): choosing a model has nothing to do with there being a draft, so it is
        // permanently on screen and permanently claims its own height — correctly, since the
        // whole point of standing it outside `commitClass` was that a person can reach it
        // BEFORE writing a word. So this case measures the content-gated row alone (Send's
        // own parent), not the composer band as a whole, which would never collapse now.
        //
        // Only a browser can tell that from a row that is simply padded: the markup is
        // identical either way, and so is the row's own bounding box (clipping does not
        // resize a child). What separates them is the row's own measured height.
        editorCaseIn 390 844 "a composer with nothing to send spends no height on its verbs" <| fun page ->
            async {
                // Measured with motion off for the reason every geometry case here is: a
                // `max-height` mid-transition is neither of the two heights being compared.
                do! awaitU (page.EmulateMediaAsync (PageEmulateMediaOptions (ReducedMotion = ReducedMotion.Reduce)))
                let line = """#shell [data-draft-input] .ProseMirror"""
                let! _ = await (page.WaitForSelectorAsync line)
                // The content-gated row's own height — Send's direct parent (`commitClass` in
                // `View.drafts`), not the band as a whole, which also holds the permanently
                // visible model picker (`Style.draftLead`).
                let below =
                    """() => {
                         const commit = document.querySelector('#shell [data-send-draft]').parentElement
                         return commit.getBoundingClientRect().height
                       }"""
                let! empty = await (page.EvaluateAsync<float> below)
                do! awaitU (page.ClickAsync line)
                do! awaitU (page.Keyboard.TypeAsync "something to send")
                // Send's own weight moves from waiting to ready exactly while the draft
                // does, so its arrival is the rule having settled — the one signal here
                // that is not a guess at a duration.
                let! _ = await (page.WaitForSelectorAsync """#shell [data-send-draft][class~="bg-blue"]""")
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
                let! _ = await (page.WaitForSelectorAsync """#shell [data-send-draft][class~="bg-blue"]""")
                // The shell into the viewport first: this harness page stacks its mounts and
                // the shell is not the top of it, so the composer sits below the fold and a
                // hit-test in VIEWPORT coordinates answers null for a reason this case is
                // not about. (Measured: without it, `elementFromPoint` said `nothing` while
                // the row was standing exactly where it should.)
                do! awaitU (page.EvaluateAsync "() => document.querySelector('#shell').scrollIntoView()")
                // The blur IS the gesture under test: it is what a tap on a button does
                // first, on every browser that does not focus one.
                do! awaitU (page.EvaluateAsync "() => document.activeElement.blur()")
                // …and then the composer is left to finish reacting to it. The hit-test aims
                // at a point in VIEWPORT coordinates, so it is a question about stacking only
                // once the box it aims at has stopped moving: read mid-collapse it answers
                // about wherever Send was passing through, or — if the row is between renders
                // and the box is empty — about the origin of the page, which in this stacked
                // harness is another mount's field entirely. That is what the flake was: an
                // `INPUT` from the terminal command line answering for a Send nobody had
                // finished laying out. A settled box is a question about the code; an
                // unsettled one is a question about how fast this machine re-rendered.
                do! waitFor
                        "Send's box to settle after the blur"
                        page
                        """(() => {
                             const send = document.querySelector('#shell [data-send-draft]')
                             if (!send) { window.__sendBox = null; return false }
                             const b = send.getBoundingClientRect()
                             if (b.width === 0 || b.height === 0) { window.__sendBox = null; return false }
                             const now = [b.top, b.left, b.width, b.height].join(',')
                             const settled = window.__sendBox === now
                             window.__sendBox = now
                             return settled
                           })()"""
                // On failure it says WHERE both were, because "an INPUT answered" alone cannot
                // tell an overlay from a hit-test aimed at the wrong point.
                let! answered =
                    await (page.EvaluateAsync<string>
                            """() => {
                                 const send = document.querySelector('#shell [data-send-draft]')
                                 const b = send.getBoundingClientRect()
                                 const at = document.elementFromPoint(b.left + b.width / 2, b.top + b.height / 2)
                                 if (at !== null && send.contains(at)) return 'send'
                                 const box = r => `${Math.round(r.left)},${Math.round(r.top)} ${Math.round(r.width)}x${Math.round(r.height)}`
                                 const who =
                                   at === null
                                     ? 'nothing'
                                     : at.tagName + '.' + (at.getAttribute('class') ?? '') + ' at ' + box(at.getBoundingClientRect())
                                 return who + ' (send at ' + box(b) + ')'
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
        // live TV, through the same mechanism" has to mean — that it lands a moment ago rather
        // than at the recording's start or its end, that focus survives the control swap,
        // and that playing off the pinned end catches the reader back up to live by itself.
        editorCase "a live terminal rewinds to a moment ago, and playing off its pinned end catches back up" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
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

                // It lands a moment ago: the screen as it stood there, shown before anyone
                // presses play — not a blank player parked at 0:00, and not the pinned end, which
                // has nothing after it to play.
                let! _ =
                    await (page.WaitForFunctionAsync
                        """document.querySelector("#shell [data-pane-replay='terminal:term-live']")?.textContent.includes('earlier output') === true""")
                let! landed = await (page.EvaluateAsync<string> rewoundText)
                Expect.stringContains landed "line 3" "the screen as it stood at the landing"
                Expect.isFalse (landed.Contains "line 7") "which is short of the pin: there is something left to play"

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

        // What a press of play does to a rewind. The recording ends at the pin, so a rewind
        // that landed AT the pin had nothing to play and the player answered by starting over
        // from zero: the screen emptied and the reader was at the top of the terminal with
        // nothing saying how they got there. Sampled from just before the press, because the
        // fault is a frame — a screen that is gone for a moment and back — which no wait on the
        // end state can see.
        editorCase "pressing play on a rewind carries on from where it landed" <| fun page ->
            async {
                do! rewoundTall page
                let! _ = await (page.WaitForSelectorAsync (rewoundMount + " .ap-overlay-start"))
                let sample =
                    sprintf
                        """async () => {
                             const seen = []
                             const read = () => (document.querySelector(%s)?.textContent ?? '')
                             const timer = setInterval(() => seen.push(read()), 25)
                             await new Promise(r => setTimeout(r, 100))
                             document.querySelector(%s + ' .ap-overlay-start').click()
                             await new Promise(r => setTimeout(r, 700))
                             clearInterval(timer)
                             return JSON.stringify(seen)
                           }"""
                        (System.Text.Json.JsonSerializer.Serialize rewoundMount)
                        (System.Text.Json.JsonSerializer.Serialize rewoundMount)
                let! seen = await (page.EvaluateAsync<string> sample)
                let frames = System.Text.Json.JsonSerializer.Deserialize<string list> seen
                let without = frames |> List.filter (fun frame -> not (frame.Contains "line 3"))
                Expect.isEmpty without "every frame, before and after the press, holds the screen it landed on"
            }

        // Where the reader is, against how long the recording is. The player's own timer and
        // progress bar say so, but they read the position they were TOLD — a rewind that put
        // its screen at the landing with a poster left them at 00:00 over an empty bar until
        // the first press — so this asks what they read, not where the screen is.
        rewoundSaysWhere 1440 900
        rewoundSaysWhere 390 844

        // The same rewind, as a reader sees it: inside the pane rather than off its edges,
        // with its transport on show. At both anchors, because a phone's panel is narrow and a
        // desktop's is short, and a player fitted on one side only fails on the other.
        rewoundReplayFits 1440 900
        rewoundReplayFits 390 844
        rewoundReplayControlsInReach 1440 900
        rewoundReplayControlsInReach 390 844

        // A rewound reader is watching a recording, and the pane must not look live (D3): the
        // command line is not there to type into, a bar says so and offers the way back, and
        // what was half-written is waiting when they return. The draft is a `Y.Text` root and
        // the line an input bound to it after every render — so that it SURVIVES the input
        // leaving the document and coming back is a claim only a browser can check.
        editorCase "a rewound terminal swaps its command line for a way back to live, and the draft waits" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
                do! awaitU (page.ClickAsync "#shell [data-terminal-tab='term-live']")
                let line = "#shell " + commandLine "term-live"
                let! _ = await (page.WaitForSelectorAsync line)
                do! awaitU (page.ClickAsync line)
                do! awaitU (page.Keyboard.TypeAsync "git sta")
                do! waitCommandLine page line "git sta"

                do! awaitU (page.ClickAsync "#shell [data-terminal-watch='watch']")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-terminal-rewound='term-live']")
                let! onScreen = await (page.EvaluateAsync<int> ("""() => document.querySelectorAll("#shell [data-terminal-input]:not([readonly])").length"""))
                Expect.equal onScreen 0 "no command line to type into while watching a recording"

                // The bar's own press is the way back, and it hands focus on to the toggle
                // that replaces it rather than stranding it on `body`.
                do! awaitU (page.ClickAsync "#shell [data-terminal-rewound-live]")
                let! _ = await (page.WaitForFunctionAsync """!document.querySelector("#shell [data-terminal-rewound]")""")
                do! waitCommandLine page line "git sta"
                do! waitFor
                        "focus on the toggle that replaced the bar"
                        page
                        """document.activeElement?.getAttribute('data-terminal-watch') === 'watch'"""
            }

        // A long output keeps its command in view. Measured with `seq 1 300`: the block's
        // command line scrolled out of the top of the scrollback and the screen was numbers,
        // with nothing on it to say what had printed them. Only a browser can settle this —
        // the markup is the same whether or not the line holds — so it is measured off the
        // real boxes: the command row sits on the scrollback's top edge while the block's
        // own top is far above it, and it is what is PAINTED there, not merely positioned.
        editorCase "a long output keeps its command in view" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
                let! _ =
                    await (page.WaitForSelectorAsync "#shell [data-terminal-scrollback][data-terminal-id='term-harness']")
                // The column OPENS: its width transitions from nothing while the pane inside it
                // is already its full width, so for 200ms the scrollback is clipped by the column
                // and its middle is off the right edge of the window — where a hit-test finds
                // nothing at all. Measured from the click on this box, the column was 110px of
                // its 420. A quick box had always finished opening by the time the case looked;
                // a loaded runner had not, and read as a command line drawn under something.
                // So wait for the column to have opened, and for the focus it hands over on
                // landing (which scrolls the page to it), before anything is measured.
                let! _ =
                    await (
                        page.WaitForFunctionAsync
                            """(() => {
                                 const panel = document.querySelector('#shell [data-content-panel]')
                                 const scroller = panel.querySelector('[data-terminal-scrollback]')
                                 const p = panel.getBoundingClientRect(), q = scroller.getBoundingClientRect()
                                 return panel.getAnimations().length === 0
                                   && q.left >= p.left - 0.5 && q.right <= p.right + 0.5
                                   && !!document.activeElement?.closest('#shell [data-content-panel]')
                               })()""")
                // Three hundred lines into the running command — one record, so one render.
                do! awaitU (
                        page.EvaluateAsync
                            """() => window.__record('term-harness', 2, 'o',
                                       Array.from({ length: 300 }, (_, i) => 'line ' + (i + 1)).join('\r\n'))""")
                // The history draws a block's last lines until it is opened, so a block tall
                // enough to scroll through is an OPENED one: ask for all of it.
                do! awaitU (page.ClickAsync "#shell [data-terminal-block=block-burst-running] [data-terminal-output-expand]")
                // Scroll until the middle of that output is at the top of the scrollback.
                let! _ =
                    await (
                        page.WaitForFunctionAsync
                            """(() => {
                                 const scroller = document.querySelector('#shell [data-terminal-scrollback]')
                                 const block = scroller.querySelector('[data-terminal-block=block-burst-running]')
                                 if (!block || block.getBoundingClientRect().height < 1000) return false
                                 const a = scroller.getBoundingClientRect(), b = block.getBoundingClientRect()
                                 scroller.scrollTop += (b.top - a.top) + b.height / 2
                                 return true
                               })()""")
                // What is painted at the row's centre is NAMED when it is not the row, so a red
                // says what covered it rather than only that something did.
                let! held =
                    await (
                        page.EvaluateAsync<string>
                            """() => new Promise(done => requestAnimationFrame(() => {
                                 const scroller = document.querySelector('#shell [data-terminal-scrollback]')
                                 const block = scroller.querySelector('[data-terminal-block=block-burst-running]')
                                 const row = block.querySelector('[data-terminal-block-command]')
                                 const a = scroller.getBoundingClientRect(), b = block.getBoundingClientRect()
                                 const r = row.getBoundingClientRect()
                                 const hit = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2)
                                 const said = el => !el ? 'nothing' : el.tagName.toLowerCase()
                                   + [...el.attributes].filter(x => x.name.startsWith('data-')).map(x => `[${x.name}=${x.value}]`).join('')
                                 done(JSON.stringify({
                                   blockScrolledAway: b.top < a.top - 500,
                                   rowAtTop: Math.abs(r.top - a.top) <= 1,
                                   rowPainted: !!hit && row.contains(hit),
                                   measured: { scroller: a.top, block: b.top, row: r.top, rowLeft: r.left, rowRight: r.right, window: innerWidth },
                                   hit: said(hit)
                                 }))
                               }))""")
                Expect.stringContains
                    held
                    "\"blockScrolledAway\":true,\"rowAtTop\":true,\"rowPainted\":true"
                    "the command line is held at the top, and is what is drawn there"
            }

        // What a block's output costs the page. A template per line was a template instance,
        // its markers and its text — some ten nodes a line — so `seq 100000` drew a million
        // nodes, and every render after that walked them. Plain output is one run of text
        // however many lines it has, so it costs a handful of nodes; this counts them, in the
        // one place that can — the DOM a browser actually built.
        editorCase "plain output costs a block a handful of nodes, however many lines it has" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
                let! _ =
                    await (page.WaitForSelectorAsync "#shell [data-terminal-scrollback][data-terminal-id='term-harness']")
                do! awaitU (
                        page.EvaluateAsync
                            """() => window.__record('term-harness', 2, 'o',
                                       Array.from({ length: 1500 }, (_, i) => 'line ' + (i + 1)).join('\r\n'))""")
                // Drawn by the frame's end, not necessarily before the call returns: the page
                // draws at most once a frame (`Render.setState`).
                do! twoFrames page
                let! counted =
                    await (
                        page.EvaluateAsync<string>
                            """() => {
                                 const output = document.querySelector("#shell [data-terminal-block='block-burst-running'] [data-terminal-output]")
                                 if (!output) return 'no output drawn'
                                 let nodes = 0
                                 const walk = document.createTreeWalker(output, NodeFilter.SHOW_ALL)
                                 while (walk.nextNode()) nodes++
                                 // Anti-vacuity: the lines are really there to be counted.
                                 return output.textContent.includes('line 1500') ? String(nodes) : 'the last line is not drawn'
                               }""")
                match System.Int32.TryParse counted with
                | true, nodes ->
                    Expect.isTrue (nodes <= 16) (sprintf "fifteen hundred plain lines drew %d nodes, not a handful" nodes)
                | _ -> failwith counted
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

        // The same press, for a command that is behind a shut run ("ran 2 earlier commands"). It
        // landed on the terminal with everything folded: nothing scrolled to, nothing marked,
        // the promise of the button broken. Only a browser can see the run open, the command
        // inside the scroller's box and its mark playing.
        editorCase "showing a folded command in its terminal opens its run and marks it" <| fun page ->
            async {
                let! _ = await (page.WaitForSelectorAsync "#shell [data-chat-task-card]")
                do! awaitU (page.ClickAsync "#shell [data-chat-task-card] [data-fold]")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-chat-task-card] [data-chat-block='block-burst-failed']")
                do! awaitU (page.ClickAsync "#shell [data-chat-task-card] [data-chat-block='block-burst-failed']")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-pane-show-in-terminal]")
                do! awaitU (page.ClickAsync "#shell [data-pane-show-in-terminal]")

                // One reading, so a red names every part that did not hold rather than the
                // first wait that timed out.
                let landed =
                    """(() => {
                         const scroller = document.querySelector('#shell [data-terminal-scrollback]')
                         const run = scroller && scroller.querySelector("[data-terminal-block-run='block-burst-ok'] details")
                         const block = scroller && scroller.querySelector('[data-terminal-block=block-burst-failed]')
                         if (!block) return JSON.stringify({ block: false })
                         // In view AND painted: a box inside a shut `<details>` can still
                         // measure as if it were on screen, so ask what is drawn at its line.
                         const row = block.querySelector('[data-terminal-block-command]')
                         const a = scroller.getBoundingClientRect(), r = row.getBoundingClientRect()
                         const hit = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2)
                         return JSON.stringify({
                           runOpen: !!run && run.open,
                           inView: r.top >= a.top - 1 && r.bottom <= a.bottom + 1 && !!hit && block.contains(hit),
                           marked: block.getAnimations().some(x => x.animationName === 'reveal-line'),
                           focusInPane: document.activeElement?.hasAttribute('data-pane-panel') === true
                         })
                       })()"""
                let expected = "{\"runOpen\":true,\"inView\":true,\"marked\":true,\"focusInPane\":true}"
                // The mark is an animation that ENDS, so it is read while it plays: the wait
                // settles on all four at once, and only a wait that never settled reads again
                // to say which part was missing.
                match! Async.Catch (await (page.WaitForFunctionAsync (sprintf "%s === %s" landed (System.Text.Json.JsonSerializer.Serialize expected)))) with
                | Choice1Of2 _ -> ()
                | Choice2Of2 _ ->
                    let! said = await (page.EvaluateAsync<string> ("() => " + landed))
                    Expect.equal said expected "the run is open, the command in view, marked, and focus in the pane"
            }

        // A run a person opened is theirs to shut. It was the `<details>` element's own state,
        // so anything that rebuilt the terminal's history — a preview laid over it and taken
        // down again — rebuilt it shut. The open is native (a click on the summary), which is
        // the half no rendered string can see reaching the model.
        editorCase "a run opened by hand is still open after the terminal is shown again" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-terminal-block-run='block-burst-ok'] summary")
                do! awaitU (page.ClickAsync "#shell [data-terminal-block-run='block-burst-ok'] summary")
                let! _ =
                    await (page.WaitForFunctionAsync
                        """document.querySelector("#shell [data-terminal-block-run='block-burst-ok'] details")?.open === true""")

                // Away to a preview, which takes the history out of the document, and back.
                do! awaitU (page.ClickAsync "#shell [data-chat-block='block-harness']")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-pane-show-in-terminal]")
                let! _ = await (page.WaitForFunctionAsync """!document.querySelector("#shell [data-terminal-block-run='block-burst-ok']")""")
                do! awaitU (page.ClickAsync "#shell [data-pane-show-in-terminal]")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-terminal-block-run='block-burst-ok'] details")

                let! opened =
                    await (page.EvaluateAsync<bool>
                        """() => new Promise(done => requestAnimationFrame(() =>
                             done(document.querySelector("#shell [data-terminal-block-run='block-burst-ok'] details").open)))""")
                Expect.isTrue opened "open as the reader left it"
            }

        // Opening a fold of earlier commands with a pointer, from the end of the history, leaves
        // the latest command where the reader was looking at it: what the fold holds came before,
        // so it appears ABOVE. It used to push the latest command off the bottom of the pane.
        editorCaseIn 1440 900 "clicking open the earlier commands leaves the latest command where a reader at the end saw it" <| fun page ->
            async {
                do! paneOnBlocks page
                do! toTheEnd page "blocks:term-harness"
                let! run = newestRun page
                let! reachable = runLineOnScreen page run
                Expect.isTrue reachable "the fold's line is on screen at the end of the history"
                let! before = latestCommandTop page
                do! clickRun page run
                let! after = latestCommandTop page
                Expect.isTrue (abs (after - before) <= 2.0) (sprintf "the latest command stays put: it was at %.1fpx, and is at %.1fpx" before after)
            }
        // A reader who scrolled back and has the fold's line at the top of the pane is looking
        // at the commands under it, and those stay where they are too.
        editorCaseIn 1440 900 "clicking open the earlier commands leaves what a reader scrolled back to where it was" <| fun page ->
            async {
                do! paneOnBlocks page
                do! toTheEnd page "blocks:term-harness"
                let! run = newestRun page
                do! scrollToTop page (runLine run)
                let next = sprintf "%s [data-terminal-block-run='%s'] + *" (tailSurface "blocks:term-harness") run
                let! before = topOf page next
                do! clickRun page run
                let! after = topOf page next
                Expect.isTrue (abs (after - before) <= 2.0) (sprintf "the command under the fold stays put: it was at %.1fpx, and is at %.1fpx" before after)
            }
        // A block's "show all N lines" is the same kind of press: the lines it brings back came
        // before the ones on screen, so the commands under the block stay where they are.
        editorCaseIn 1440 900 "clicking to show all of a command's lines leaves the commands under it where a reader saw them" <| fun page ->
            async {
                do! paneOnBlocks page
                do! awaitU (
                        page.EvaluateAsync
                            """() => window.__record('term-harness', 2, 'o',
                                       Array.from({ length: 300 }, (_, i) => 'line ' + (i + 1)).join('\r\n'))""")
                let block = sprintf "%s [data-terminal-block='block-burst-running']" (tailSurface "blocks:term-harness")
                let expand = block + " [data-terminal-output-expand]"
                let! _ = await (page.WaitForSelectorAsync expand)
                do! toTheEnd page "blocks:term-harness"
                do! scrollToTop page expand
                let! before = topOf page (block + " + *")
                do! clickWhereItIs page expand
                let! _ = await (page.WaitForFunctionAsync ("selector => document.querySelector(selector).getAttribute('aria-expanded') === 'true'", box expand))
                do! twoFrames page
                let! after = topOf page (block + " + *")
                Expect.isTrue (abs (after - before) <= 2.0) (sprintf "the command under the block stays put: it was at %.1fpx, and is at %.1fpx" before after)
            }
        // From the KEYBOARD the reader is looking at the line their focus is on, so that is what
        // stays: the earlier commands open below it, and the line and its focus ring stay on
        // screen. Held like a pointer's press, Enter took the line off the top of the pane and
        // appeared to do nothing.
        editorCaseIn 1440 900 "Enter on the earlier commands keeps their line where it was, on screen" <| fun page ->
            async {
                do! paneOnBlocks page
                do! toTheEnd page "blocks:term-harness"
                let! run = newestRun page
                let! reachable = runLineOnScreen page run
                Expect.isTrue reachable "the fold's line is on screen at the end of the history"
                let! before = topOf page (runLine run)
                do! enterRun page run
                let! after = topOf page (runLine run)
                let! onScreen = runLineOnScreen page run
                Expect.isTrue (abs (after - before) <= 2.0 && onScreen) (sprintf "the fold's line stays put and on screen: it was at %.1fpx, and is at %.1fpx (inside the box: %b)" before after onScreen)
            }
        // …and the next Enter shuts them again with the line still where it was.
        editorCaseIn 1440 900 "Enter again shuts the earlier commands with their line where it was, on screen" <| fun page ->
            async {
                do! paneOnBlocks page
                do! toTheEnd page "blocks:term-harness"
                let! run = newestRun page
                do! enterRun page run
                let! before = topOf page (runLine run)
                do! enterRun page run
                let! after = topOf page (runLine run)
                let! onScreen = runLineOnScreen page run
                Expect.isTrue (abs (after - before) <= 2.0 && onScreen) (sprintf "the fold's line stays put and on screen: it was at %.1fpx, and is at %.1fpx (inside the box: %b)" before after onScreen)
            }
        // The press keeps the keyboard on the fold's line, so the next Enter shuts what this one
        // opened.
        editorCaseIn 1440 900 "the fold of earlier commands keeps the keyboard when it opens" <| fun page ->
            async {
                do! paneOnBlocks page
                do! toTheEnd page "blocks:term-harness"
                let! run = newestRun page
                do! enterRun page run
                let! focused =
                    await (page.EvaluateAsync<bool> ("selector => document.activeElement === document.querySelector(selector)", box (runLine run)))
                Expect.isTrue focused "the keyboard is on the fold's line"
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
                // so their keyboard was too — onto the terminal's own command line, which is
                // where a terminal takes what they type.
                let! _ =
                    await (page.WaitForFunctionAsync (
                            "sel => document.activeElement?.matches(sel) === true",
                            box (commandLine "term-harness")))
                // And what they arrived at is the card that can answer it.
                let! _ = await (page.WaitForSelectorAsync "#shell [data-terminal-queued='queue-harness']")
                return ()
            }

        // A preview (P2-1): what a chip opens is laid over the terminal, never a tab beside it,
        // and its way back hands focus to the chip — the UI floor's rule about a DOM swap that
        // removes what had focus, asked of the one surface that removes itself on Escape. These
        // replace two cases about pins, which no longer exist.
        editorCase "a chip opens a preview and Escape returns focus to the chip" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
                let! _ =
                    await (page.WaitForFunctionAsync (
                            "sel => document.activeElement?.matches(sel) === true",
                            box (commandLine "term-harness")))
                let countTabs = "() => document.querySelectorAll('#shell [data-terminal-tab], #shell [data-terminal-closed-tab]').length"
                let! before = await (page.EvaluateAsync<int> countTabs)
                do! awaitU (page.ClickAsync "#shell [data-chat-block]")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-pane-preview]")
                let! during = await (page.EvaluateAsync<int> countTabs)
                Expect.equal during before "the strip holds the same terminals, and no tab for the preview"
                do! focusReachedPane page
                do! awaitU (page.Keyboard.PressAsync "Escape")
                do! waitFor "the preview to be gone" page "!document.querySelector('#shell [data-pane-preview]')"
                do! waitFor "focus to be back on the chip" page "document.activeElement?.hasAttribute('data-chat-block') === true"
            }

        // The preview's close, in the line under the pivot that names it (F3), is what hands
        // focus back to where it was opened from — the same act as Escape.
        editorCase "a preview's close returns focus to the chip" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-chat-block]")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-pane-preview-close]")
                do! focusReachedPane page
                do! awaitU (page.Locator("#shell [data-pane-preview-close]").First.PressAsync "Enter")
                do! waitFor "the preview to be gone" page "!document.querySelector('#shell [data-pane-preview]')"
                do! waitFor "focus to be back on the chip" page "document.activeElement?.hasAttribute('data-chat-block') === true"
            }

        // A preview is a layer OF its terminal, not a tab beside it (F3). It used to have an
        // item of its own in the strip — `$ seq 1 40`, slanted, with its own × — so the strip
        // mixed two kinds of thing that looked alike, a phone's strip with room for three tabs
        // spent one on it, and it stayed there over the `all` page. At a phone's width, where
        // the room is the cost.
        editorCaseIn 390 844 "on a phone, a preview leaves the strip to the terminals and all" <| fun page ->
            async {
                do! previewSettled page
                let! items =
                    await (page.EvaluateAsync<string[]>
                            """() => [...document.querySelectorAll('#shell [data-pane-pivot] [role=tab]')]
                                 .map(t => t.hasAttribute('data-pane-switcher') ? 'all'
                                         : (t.getAttribute('data-pane-tab') ?? '').startsWith('terminal:') ? 'terminal'
                                         : t.outerHTML.slice(0, 120))""")
                Expect.isTrue (Array.contains "terminal" items) "the terminal under the preview is in the strip"
                let others = items |> Array.filter (fun item -> item <> "all" && item <> "terminal")
                Expect.isEmpty others (sprintf "every tab in the strip is a terminal or all; also: %s" (String.Join (" | ", others)))
            }

        // Measured where a thumb lands: each one's centre hit-tested, so a name clipped to
        // nothing or a control under something else is not "reachable" — and the two presses
        // a thumb's 44 each, the floor the strip's own items hold.
        editorCaseIn 390 844 "on a phone, a preview's name, its way back and its close are on the screen" <| fun page ->
            async {
                do! previewSettled page
                let! faults =
                    await (page.EvaluateAsync<string[]>
                            """() => [['name', '[data-pane-preview-name]', 0], ['back', '[data-pane-preview-back]', 44], ['close', '[data-pane-preview-close]', 44]]
                                 .flatMap(([what, sel, floor]) => {
                                   const e = document.querySelector('#shell ' + sel)
                                   if (!e) return [what + ': not rendered']
                                   const r = e.getBoundingClientRect()
                                   const hit = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2)
                                   const faults = []
                                   if (!r.width || !r.height || hit === null || !e.contains(hit)) faults.push(what + ': not on screen')
                                   if (r.right > window.innerWidth + 0.5) faults.push(what + ': past the edge')
                                   if (r.width < floor - 0.5 || r.height < floor - 0.5) faults.push(`${what}: ${Math.round(r.width)}x${Math.round(r.height)}`)
                                   return faults
                                 })""")
                Expect.isEmpty faults (sprintf "the name is visible and both presses are 44px targets: %s" (String.Join (", ", faults)))
            }

        // The `all` page is over the preview AND its terminal, so the head that names the
        // preview goes with the rest of what it covers. Asked of the pane's own text rather
        // than of a hook, because the fault was the preview's name shown by another surface (a
        // strip item) than the one that names it now.
        editorCaseIn 390 844 "on a phone, the all page does not show the preview it is over" <| fun page ->
            async {
                do! previewSettled page
                let! name = await (page.EvaluateAsync<string> "() => document.querySelector('#shell [data-pane-preview-name]').textContent.trim()")
                do! awaitU (page.ClickAsync "#shell [data-pane-switcher]")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-content-list]")
                let! shown =
                    await (page.EvaluateAsync<bool> (
                            """name => [...document.querySelectorAll('#shell [data-content-panel] *')]
                                 .filter(e => e.children.length === 0 && e.textContent.includes(name))
                                 .some(e => { const r = e.getBoundingClientRect(); return r.width > 0 && r.height > 0 })""",
                            box name))
                Expect.isFalse shown (sprintf "%s is not on the all page" name)
            }

        // The strip's door (Plan 20, stage 1), and the whole point of the shape: it opens a
        // MENU over the pane, where the list toggle beside it opens a destination that
        // replaces the strip. Two acts, two kinds of surface — which is what a heading word
        // over identical rows could not say, and what a browser is needed to confirm, because
        // in the markup a menu and a list are both elements with entries in them.
        //
        // Driven from the keyboard throughout. The door opens a surface rather than acting, so
        // it owes a reader the Escape that shuts it and the cursor back on the control that
        // opened it — the floor's rule about a DOM swap that removes what had focus.
        editorCase "the strip's door opens a menu over the pane, and gives focus back when it shuts" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
                let! _ = await (page.WaitForSelectorAsync "#shell [role='tablist']")

                do! awaitU (page.Locator("#shell [data-pane-new]").First.PressAsync "Enter")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-pane-new-menu]")

                // OVER the pane, not instead of it: the tabs are still there and still a
                // tablist, which is exactly how this differs from the list.
                let! _ = await (page.WaitForSelectorAsync "#shell [role='tablist']")
                let! _ = await (page.WaitForFunctionAsync """!document.querySelector("#shell [data-content-list]")""")

                // Its entries are real controls a keyboard can reach.
                do! awaitU (page.Locator("#shell [data-sandbox-new]").First.FocusAsync ())
                let! onEntry =
                    await (page.EvaluateAsync<bool> "() => document.activeElement?.hasAttribute('data-sandbox-new') === true")
                Expect.isTrue onEntry "a place takes focus without a pointer"

                // Escape from inside the menu shuts it and hands the cursor back, rather than
                // leaving it on `body` where the entry used to be.
                do! awaitU (page.Keyboard.PressAsync "Escape")
                let! _ = await (page.WaitForFunctionAsync """!document.querySelector("#shell [data-pane-new-menu]")""")
                let! _ =
                    await (page.WaitForFunctionAsync
                        """document.activeElement?.hasAttribute('data-pane-new') === true""")
                return ()
            }

        // The same door from the EMPTY pane (P1-4), where the strip offers no `+` and the
        // empty pane's own button is the one way to make something — so the menu hangs from
        // that button, and Escape hands the cursor back to it rather than to a `+` that is not
        // on the page. The harness's pane is emptied by its terminals ending with none of them
        // chosen: their closed tabs stay in the strip (F2), and a pane with nothing chosen
        // lands only on a terminal that still runs (`ClientModel.selectedTerminal`).
        editorCase "the empty pane's door opens the menu under itself, and gives focus back when it shuts" <| fun page ->
            async {
                let closed (id: string) =
                    Yession.Domain.SessionEvent.TerminalClosed
                        { Yession.Domain.Terminals.TerminalClosed.TerminalId = harnessTerminal id
                          Yession.Domain.Terminals.TerminalClosed.Reason = "closed by a peer"
                          Yession.Domain.Terminals.TerminalClosed.By = None }
                do! foldHarness page [ 90L, closed "term-harness"; 91L, closed "term-live" ]
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-terminal-new]")

                do! awaitU (page.Locator("#shell [data-terminal-new]").First.PressAsync "Enter")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-pane-new-menu]")
                do! awaitU (page.Locator("#shell [data-sandbox-new]").First.FocusAsync ())

                do! awaitU (page.Keyboard.PressAsync "Escape")
                let! _ = await (page.WaitForFunctionAsync """!document.querySelector("#shell [data-pane-new-menu]")""")
                let! _ =
                    await (page.WaitForFunctionAsync
                        """document.activeElement?.hasAttribute('data-terminal-new') === true""")
                return ()
            }

        // And from a phone's `all` page, whose door stands in for the `+` there. Over that page
        // the document holds both doors — the `+` hidden by the stylesheet, the page's shown —
        // so the cursor handed back has to go to the one on the screen: a `display: none`
        // control takes no focus, and a hand-back to the first door in the document left it on
        // `body`.
        editorCaseIn 390 844 "on a phone the all page's door opens the menu, and gives focus back when it shuts" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
                do! awaitU (page.ClickAsync "#shell [data-pane-switcher]")
                let door = "#shell [data-content-list] [data-terminal-new]"
                let! _ = await (page.WaitForSelectorAsync door)
                do! awaitU (page.Locator(door).PressAsync "Enter")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-content-list] [data-pane-new-menu]")
                do! awaitU (page.Locator("#shell [data-content-list] [data-sandbox-new]").First.FocusAsync ())

                do! awaitU (page.Keyboard.PressAsync "Escape")
                let! _ = await (page.WaitForFunctionAsync """!document.querySelector("#shell [data-pane-new-menu]")""")
                do! waitFor "focus back on the page's door" page
                        (sprintf "document.activeElement?.matches(%s) === true" (System.Text.Json.JsonSerializer.Serialize door))
            }

        // The menu fits the screen it is on. Only a browser can answer it and the markup is
        // innocent either way: the menu's entries carry what a repo's file says its sandbox is
        // FOR, which is a whole sentence — 170 characters in this repository's own
        // `yession.yaml` — and a box with a minimum width and no maximum grows to its widest
        // entry. Hung from `right-0`, that growth goes LEFT: measured on a 375px phone, a
        // 962px menu starting 599px off the side of the screen, its text cut off mid-word.
        //
        // What is asserted is that it fits, not what it measures: the width is design and may
        // move, where staying on the screen is the promise. The long descriptions live in the
        // harness fixture, because the version of this that shipped broken was green against
        // a fixture that said "day-to-day work".
        //
        // A phone's door is the `all` page's (the pivot's `+` is a desktop control there), so
        // the menu measured is the one hung from it — scoped to the page, because the hidden
        // `+` carries a hidden copy that measures nothing and would fit any screen.
        editorCaseIn 375 812 "the menu of things to open fits the phone it is on" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
                do! awaitU (page.ClickAsync "#shell [data-pane-switcher]")
                do! awaitU (page.ClickAsync "#shell [data-content-list] [data-terminal-new]")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-content-list] [data-pane-new-menu]")

                // The note is what can push it wide, so a fixture that stopped carrying a long
                // one would make this pass over nothing. A NAME can push it too, by about
                // 50px, and is left to: `SandboxName` caps at 40 characters, so that growth is
                // bounded and small, and a menu sized to its longest name reads better than a
                // name with its end cut off.
                let! longestNote =
                    await (page.EvaluateAsync<int> """() => Math.max(0, ...[...document.querySelectorAll(
                        "#shell [data-content-list] [data-pane-new-menu] [role='menuitem']")].map(e => e.textContent.trim().length))""")
                Expect.isTrue
                    (longestNote > 90)
                    (sprintf "an entry says what its sandbox is for, at length; longest is %d" longestNote)

                let! fits =
                    await (page.EvaluateAsync<bool> """() => {
                        const m = document.querySelector("#shell [data-content-list] [data-pane-new-menu]").getBoundingClientRect();
                        return m.left >= 0 && m.right <= window.innerWidth;
                    }""")
                let! box =
                    await (page.EvaluateAsync<string> """() => {
                        const m = document.querySelector("#shell [data-content-list] [data-pane-new-menu]").getBoundingClientRect();
                        return `left ${Math.round(m.left)}, right ${Math.round(m.right)}, in a ${window.innerWidth}px screen`;
                    }""")
                Expect.isTrue fits (sprintf "the menu is on the screen: %s" box)
                return ()
            }

        // One column of names, whatever marks the rows wear. A ragged left edge is what makes
        // a list of twenty read as twenty unrelated things, and the old list's had one: a
        // leading column of marks four different widths wide. The marks sit after the names
        // now; what is asserted is still only that the names AGREE, over rows that wear
        // different marks — so a mark moved back in front of its name goes red here.
        //
        // Only a browser can answer it, and the markup looks right either way. Measured, not
        // pixel-matched — the offset itself is the design and may move.
        editorCase "the all page's names stand in one column, whatever mark each row wears" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
                do! awaitU (page.ClickAsync "#shell [data-pane-switcher]")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-content-list]")

                let! kinds =
                    await (page.EvaluateAsync<int> """() => new Set([...document.querySelectorAll(
                        "#shell [data-content-list] [role='listitem']")]
                        .map(r => r.querySelector('[data-pane-mark]')?.getAttribute('data-pane-mark') ?? 'none')).size""")
                Expect.isTrue (kinds > 1) (sprintf "the rows wear different marks, got %d kind(s)" kinds)

                // The NAMES, by the hooks that make them names — not every button in a row,
                // which would drag the verbs at the far edge into the count.
                let names =
                    [ Yession.App.Dom.Hooks.terminalListRow
                      Yession.App.Dom.Hooks.artifactListRow ]
                    |> List.map (sprintf "#shell [data-content-list] [%s]")
                    |> String.concat ", "
                let! counted =
                    await (page.EvaluateAsync<int> (sprintf """() => document.querySelectorAll("%s").length""" names))
                Expect.isTrue (counted > 1) (sprintf "more than one name to line up, got %d" counted)
                let! offsets =
                    await (page.EvaluateAsync<int> (sprintf """() => new Set([...document.querySelectorAll("%s")]
                        .map(n => Math.round(n.getBoundingClientRect().left))).size""" names))
                Expect.equal offsets 1 "every name starts at the same place"
                return ()
            }

        // The switcher (P2-2). WHICH verbs a row offers is a fold the cheap tier already pins;
        // what only a browser can answer is the DOM swap — choosing a row shuts the switcher,
        // so the control that was pressed leaves the document, and focus has to land on what
        // the choice put on screen rather than on `body`. That is the WCAG floor, not a
        // nicety.
        editorCase "choosing a terminal in the switcher lands on its command line" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
                do! awaitU (page.ClickAsync "#shell [data-pane-switcher]")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-content-list]")

                // Every terminal the session has is reachable here, whether or not the strip
                // would have carried it.
                let! rows = await (page.EvaluateAsync<int> "() => document.querySelectorAll('#shell [data-content-list] [data-terminal-list-row]').length")
                Expect.equal rows 3 "every terminal the harness has, the closed one included"

                // Driven from the KEYBOARD: a row has to be a real control somebody can reach
                // and press without a pointer, and the focus move afterwards is what the floor
                // asks for when the pressed control leaves the document.
                do! awaitU (page.FocusAsync "#shell [data-content-list] [data-terminal-list-row='term-live']")
                do! awaitU (page.Keyboard.PressAsync "Enter")
                let! _ = await (page.WaitForFunctionAsync """!document.querySelector('#shell [data-content-list]')""")
                let! _ =
                    await (page.WaitForFunctionAsync
                        """document.querySelector('#shell [data-pane-panel]')?.getAttribute('data-pane-panel') === 'terminal:term-live'""")
                // This peer holds that terminal's keyboard, so it has no command line: what
                // takes the keystrokes is its screen, and that is where focus lands.
                let! _ =
                    await (page.WaitForFunctionAsync
                        """document.activeElement?.getAttribute('data-terminal-keys') === 'term-live'""")
                return ()
            }

        // The sidebar's environment lists what is running, and an entry is a way INTO it: the
        // same `OpenInPaneMsg` the `all` page's row sends. A hook that stopped matching, or a
        // message that stopped opening anything, leaves a list of names that quietly do
        // nothing, which no rendered string can tell from one that works.
        editorCaseIn 1440 900 "a terminal in the environment opens the pane on it" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-environment-terminals] [data-environment-terminal='term-live']")
                let! _ =
                    await (page.WaitForFunctionAsync
                        """document.querySelector('#shell [data-pane-panel]')?.getAttribute('data-pane-panel') === 'terminal:term-live'""")
                return ()
            }
        // On a phone the environment is in the DRAWER, and the pane is the other sheet over the
        // chat: an entry that opened the pane and left the drawer up would show the reader
        // nothing. Hit-tested at the panel's centre, which is the only way to ask whether
        // something is over it; read BEFORE the press as well, so a drawer that stopped
        // covering anything fails here rather than passing by vacuity.
        editorCaseIn 390 844 "a terminal pressed in the environment on a phone is shown, not left behind the drawer" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-nav-toggle='show']")
                let entry = "#shell [data-environment-terminals] [data-environment-terminal='term-live']"
                // WAITED for, not read once: the column is in the document whether or not the
                // drawer is open — shut, it is only translated off the side — so the entry's
                // selector matches at once, while the drawer is still sliding in over its
                // transition. A single read raced that slide and lost on a slower runner.
                let! covered =
                    await (page.WaitForFunctionAsync
                            """(() => {
                                 const box = document.querySelector('#shell aside').getBoundingClientRect()
                                 if (box.left < 0) return false
                                 const at = document.elementFromPoint(box.left + box.width / 2, box.top + box.height / 2)
                                 return !!at?.closest('aside')
                               })()""")
                Expect.isNotNull covered "the drawer is up, which is where the environment sits on a phone"

                do! awaitU (page.ClickAsync entry)
                let! shown =
                    await (page.WaitForFunctionAsync
                            """(() => {
                                 const panel = document.querySelector("#shell [data-pane-panel='terminal:term-live']")
                                 if (!panel) return false
                                 const box = panel.getBoundingClientRect()
                                 const at = document.elementFromPoint(box.left + box.width / 2, box.top + box.height / 2)
                                 return !!at && panel.contains(at)
                               })()""")
                Expect.isNotNull shown "the drawer stood aside, and the terminal is on the screen"
                // And it is GONE rather than under the pane: on a phone the pane is the other
                // sheet, at the same layer and later in the document, so a drawer left open
                // behind it hides nothing on screen and comes back the moment the pane goes.
                let! away =
                    await (page.WaitForFunctionAsync
                            """document.querySelector('#shell aside').getBoundingClientRect().right <= 0""")
                Expect.isNotNull away "the drawer has left the screen"
                return ()
            }

        // Escape steps back off the `all` page. It removes the element focus was on, and the
        // pivot item the reader is back on — the terminal the page was laid over — is where
        // they are.
        editorCase "Escape leaves the all page and returns focus to the item it was laid over" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
                do! awaitU (page.Locator("#shell [data-pane-switcher]").PressAsync "Enter")
                // Opened from the keyboard, focus went in with it.
                let! _ =
                    await (page.WaitForFunctionAsync
                        """!!document.activeElement?.closest('#shell [data-content-list]')""")
                do! awaitU (page.Keyboard.PressAsync "Escape")
                let! _ = await (page.WaitForFunctionAsync """!document.querySelector('#shell [data-content-list]')""")
                let! _ =
                    await (page.WaitForFunctionAsync
                        """document.activeElement?.matches('#shell [data-pane-pivot] [role=tab][aria-selected=true][data-pane-tab]') === true""")
                return ()
            }

        // F5. A row's verbs are not drawn down every row at rest, so what has to hold is that
        // none of them is out of the keyboard's reach: Tab from the first name visits every
        // verb on the page, and each is PAINTED when it is reached — a control focus lands on
        // unseen is one a keyboard user presses blind.
        editorCase "every verb of a row on the all page is reached by Tab, and shows when it is" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
                do! awaitU (page.ClickAsync "#shell [data-pane-switcher]")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-content-list] [data-terminal-list-replay]")
                let verbs = "#shell [data-content-list] [role=listitem] button:not([data-terminal-list-row])"
                let! offered =
                    await (page.EvaluateAsync<string[]> (sprintf """() => [...document.querySelectorAll("%s")]
                        .map(b => b.outerHTML.slice(0, b.outerHTML.indexOf('>')))""" verbs))
                Expect.isTrue (offered.Length >= 3) (sprintf "a page with verbs on it, or this proves nothing: %A" offered)
                do! awaitU (page.FocusAsync "#shell [data-content-list] [data-terminal-list-row]")
                let reached = System.Collections.Generic.HashSet<string> ()
                let inside = ref true
                let presses = ref 0
                while inside.Value && presses.Value < 40 do
                    do! awaitU (page.Keyboard.PressAsync "Tab")
                    presses.Value <- presses.Value + 1
                    let! here =
                        await (page.EvaluateAsync<string> (sprintf """() => {
                            const el = document.activeElement;
                            if (!el?.closest('#shell [data-content-list]')) return 'outside';
                            return el.matches("%s") ? el.outerHTML.slice(0, el.outerHTML.indexOf('>')) : 'name';
                        }""" verbs))
                    if here = "outside" then inside.Value <- false
                    elif here <> "name" then
                        // Painted: every box from it up to the page fully opaque, once the
                        // fade has run.
                        let! _ =
                            await (page.WaitForFunctionAsync """() => {
                                for (let n = document.activeElement; n; n = n.parentElement)
                                    if (getComputedStyle(n).opacity !== '1') return false;
                                return true;
                            }""")
                        reached.Add here |> ignore
                for verb in offered do
                    Expect.isTrue (reached.Contains verb) (sprintf "Tab reaches %s" verb)
            }

        // F5. A closed row's recording is one press from the list, as it is from the closed
        // tab's own footer: the replay mounts that terminal's recording in the pane.
        editorCase "a closed row on the all page offers its replay" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
                do! awaitU (page.ClickAsync "#shell [data-pane-switcher]")
                do! awaitU (page.FocusAsync "#shell [data-content-list] [data-terminal-list-replay='term-done']")
                do! awaitU (page.Keyboard.PressAsync "Enter")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-pane-replay='terminal:term-done']")
                return ()
            }

        // F5. The page is narrowed by filters rather than grouped, and a filter is a control
        // like any other: every one of them is on the keyboard's way, from the rows back up.
        editorCase "every filter on the all page is reached by Tab" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
                do! awaitU (page.ClickAsync "#shell [data-pane-switcher]")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-content-list] [data-terminal-filter='closed']")
                let! offered =
                    await (page.EvaluateAsync<string[]> """() => [...document.querySelectorAll("#shell [data-content-list] [data-terminal-filter]")]
                        .map(b => b.getAttribute('data-terminal-filter'))""")
                Expect.isTrue (offered.Length >= 3) (sprintf "all and at least two kinds, or this proves nothing: %A" offered)
                do! awaitU (page.FocusAsync "#shell [data-content-list] [data-terminal-list-row]")
                let reached = System.Collections.Generic.HashSet<string> ()
                let presses = ref 0
                while reached.Count < offered.Length && presses.Value < 12 do
                    do! awaitU (page.Keyboard.PressAsync "Shift+Tab")
                    presses.Value <- presses.Value + 1
                    let! here =
                        await (page.EvaluateAsync<string> """() => document.activeElement?.getAttribute('data-terminal-filter') ?? ''""")
                    if here <> "" then reached.Add here |> ignore
                for filter in offered do
                    Expect.isTrue (reached.Contains filter) (sprintf "Tab reaches the %s filter" filter)
            }

        // F5. Which filter the page is narrowed to is said to assistive technology, not only
        // drawn: chosen from the keyboard, that one — and only that one — is pressed.
        editorCase "a filter chosen from the keyboard is the one pressed" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
                do! awaitU (page.ClickAsync "#shell [data-pane-switcher]")
                do! awaitU (page.FocusAsync "#shell [data-content-list] [data-terminal-filter='closed']")
                do! awaitU (page.Keyboard.PressAsync "Enter")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-content-list] [data-terminal-filter='closed'][aria-pressed='true']")
                let! pressed =
                    await (page.EvaluateAsync<string[]> """() => [...document.querySelectorAll("#shell [data-content-list] [data-terminal-filter]")]
                        .filter(b => b.getAttribute('aria-pressed') === 'true')
                        .map(b => b.getAttribute('data-terminal-filter'))""")
                Expect.equal (List.ofArray pressed) [ "closed" ] "closed, and nothing else"
            }

        // F5, on a phone: every filter is a thumb's 44px, painted and on top where it is.
        editorCaseOnTouch 390 844 "on a touch screen every filter on the all page is at the touch floor" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
                do! awaitU (page.ClickAsync "#shell [data-pane-switcher]")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-content-list] [data-terminal-filter='closed']")
                let! sizes =
                    await (page.EvaluateAsync<string> """() => JSON.stringify(
                        [...document.querySelectorAll("#shell [data-content-list] [data-terminal-filter]")].map(b => {
                            b.scrollIntoView({ inline: 'nearest', block: 'nearest' });
                            const r = b.getBoundingClientRect();
                            const hit = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2);
                            return [b.getAttribute('data-terminal-filter'), Math.round(r.width), Math.round(r.height), hit !== null && b.contains(hit)];
                        }))""")
                let sizes = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement[]> sizes
                Expect.isTrue (sizes.Length >= 3) "filters to measure"
                for size in sizes do
                    Expect.isTrue
                        (size.[1].GetInt32 () >= 44 && size.[2].GetInt32 () >= 44 && size.[3].GetBoolean ())
                        (sprintf "a filter on top, at the touch floor: %O" size)
            }

        // F5, on a phone: a thumb has no hover, so the row the pane is about WEARS its verbs —
        // the way back to them is the row itself — and each is a thumb's 44px.
        editorCaseOnTouch 390 844 "on a touch screen the row the pane is about wears its verbs at the touch floor" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
                do! awaitU (page.ClickAsync "#shell [data-pane-switcher]")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-content-list] [data-terminal-list-row][aria-current='true']")
                let! sizes =
                    await (page.EvaluateAsync<string> """() => {
                        const row = document.querySelector("#shell [data-content-list] [data-terminal-list-row][aria-current='true']").closest('[role=listitem]');
                        return JSON.stringify([...row.querySelectorAll('button:not([data-terminal-list-row])')].map(b => {
                            const r = b.getBoundingClientRect();
                            const hit = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2);
                            let opacity = 1;
                            for (let n = b; n; n = n.parentElement) opacity *= Number(getComputedStyle(n).opacity);
                            return [Math.round(r.width), Math.round(r.height), hit !== null && b.contains(hit) && opacity === 1];
                        }));
                    }""")
                let sizes = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement[]> sizes
                Expect.isNonEmpty sizes "the row the pane is about has verbs to wear"
                for size in sizes do
                    Expect.isTrue
                        (size.[0].GetInt32 () >= 44 && size.[1].GetInt32 () >= 44 && size.[2].GetBoolean ())
                        (sprintf "a verb painted, on top, at the touch floor: %O" size)
            }

        // F5, on a phone: every OTHER row's verbs are out of the way, not merely see-through —
        // a see-through kill at a row's edge is one a thumb presses without seeing it.
        editorCaseOnTouch 390 844 "on a touch screen no row hides a press it does not show" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
                do! awaitU (page.ClickAsync "#shell [data-pane-switcher]")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-content-list] [data-terminal-list-row][aria-current='false']")
                let! hidden =
                    await (page.EvaluateAsync<string[]> """() => [...document.querySelectorAll("#shell [data-content-list] [data-terminal-list-row][aria-current='false']")]
                        .flatMap(name => [...name.closest('[role=listitem]').querySelectorAll('button:not([data-terminal-list-row])')])
                        .filter(b => {
                            const r = b.getBoundingClientRect();
                            const hit = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2);
                            let opacity = 1;
                            for (let n = b; n; n = n.parentElement) opacity *= Number(getComputedStyle(n).opacity);
                            return hit !== null && b.contains(hit) && opacity < 0.5;
                        })
                        .map(b => b.outerHTML.slice(0, b.outerHTML.indexOf('>')))""")
                Expect.isEmpty hidden "nothing pressable that is not painted"
            }

        // The shortcut works from anywhere on the page — the whole point of one — including
        // with the pane shut, which it opens.
        editorCase "the switcher's shortcut opens it from the chat" <| fun page ->
            async {
                do! awaitU (page.Keyboard.PressAsync "Control+Backquote")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-content-list]")
                return ()
            }

        // `all` replaced the strip's `+N` (P2-2), which said how many tabs the strip hid and
        // opened the switcher. What that door promised still has to hold with more tabs than
        // the pivot shows: the way to every terminal is ON SCREEN — not scrolled away with the
        // tabs — and what it opens lists the ones the pivot is hiding. Only a browser can
        // answer it: which tabs are hidden is a measurement of a laid-out row.
        editorCase "with more tabs than the pivot shows, all stays on screen and lists the hidden ones" <| fun page ->
            async {
                do! openManyTerminals page
                let! overflows = await (page.EvaluateAsync<bool> stripOverflows)
                Expect.isTrue overflows "the pivot holds more tabs than it shows, or this proves nothing"
                let! hidden =
                    await (page.EvaluateAsync<string> """() => {
                        const strip = document.querySelector('#shell [data-pane-strip]').getBoundingClientRect();
                        return JSON.stringify([...document.querySelectorAll('#shell [data-pane-strip] [data-pane-tab]')]
                            .filter(t => { const r = t.getBoundingClientRect(); return r.left < strip.left - 1 || r.right > strip.right + 1; })
                            .map(t => t.getAttribute('data-pane-tab').replace('terminal:', '')));
                    }""")
                let hidden = System.Text.Json.JsonSerializer.Deserialize<string array> hidden
                Expect.isNonEmpty hidden "the pivot is hiding some of its tabs"
                // On screen, by what is painted at its centre — not by its box, which a clipping
                // scroller leaves non-zero for something cut to nothing.
                let! shown =
                    await (page.EvaluateAsync<bool> """() => {
                        const all = document.querySelector('#shell [data-pane-switcher]');
                        const r = all.getBoundingClientRect();
                        const hit = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2);
                        return hit !== null && all.contains(hit);
                    }""")
                Expect.isTrue shown "all is on screen, whatever the tabs beside it"
                do! awaitU (page.EvaluateAsync "() => document.querySelector('#shell [data-pane-switcher]').click()")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-content-list]")
                let! listed =
                    await (page.EvaluateAsync<string> """() => JSON.stringify([...document.querySelectorAll(
                        '#shell [data-content-list] [data-terminal-list-row]')].map(r => r.getAttribute('data-terminal-list-row')))""")
                let listed = System.Text.Json.JsonSerializer.Deserialize<string array> listed |> Set.ofArray
                for id in hidden do
                    Expect.isTrue (listed.Contains id) (sprintf "%s, hidden by the pivot, is on the all page" id)
            }

        // A kill is two presses in one place (`KillArmed`). What only a browser can answer is
        // WHERE the second press lands: the armed face is wider than the glyph that armed it,
        // and the confirm is only a confirm if the spot that was pressed is now the confirming
        // control — not the row's name, not the next row's kill.
        editorCase "one press on kill asks, and the asking control is where the press was" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
                do! awaitU (page.ClickAsync "#shell [data-pane-switcher]")
                let kill = "#shell [data-content-list] [data-terminal-close='term-harness']"
                let! rect = await (page.Locator(kill).BoundingBoxAsync ())
                let x, y = rect.X + rect.Width / 2.0f, rect.Y + rect.Height / 2.0f
                do! awaitU (page.Mouse.ClickAsync (x, y))
                let! _ = await (page.WaitForSelectorAsync (kill + "[data-terminal-close-armed='true']"))
                // Hit-tested, not compared by rectangle: what matters is what a press at that
                // point would reach, which is whatever is painted there.
                let! under =
                    await (page.EvaluateAsync<string> (
                            sprintf
                                "() => document.elementFromPoint(%.1f, %.1f)?.closest('[data-terminal-close]')?.getAttribute('data-terminal-close-armed') ?? 'nothing'"
                                (float x) (float y)))
                Expect.equal under "true" "the spot that was pressed is the armed kill"
                let! sent = await (page.EvaluateAsync<int> "() => (window.__closed || []).length")
                Expect.equal sent 0 "the first press asked; it sent no kill"
            }

        // The fault this replaced: a double-click on a kill ended two terminals, the second
        // press landing on whatever slid under the pointer. Now the second press of the pair
        // is the confirm, so a double-click ends exactly the terminal it was made on.
        editorCase "a double-click on a kill ends that terminal and no other" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
                do! awaitU (page.ClickAsync "#shell [data-pane-switcher]")
                do! awaitU (page.Locator("#shell [data-content-list] [data-terminal-close='term-harness']").DblClickAsync ())
                let! sent = await (page.EvaluateAsync<string> "() => JSON.stringify(window.__closed || [])")
                Expect.equal sent "[\"term-harness\"]" "one kill, of the terminal the double-click was on"
            }

        // The tab's × IS that kill (P2-2) — the same control, the same two presses — so the
        // same double-click on it ends its terminal and only its terminal, and selects nothing
        // on the way: a × is not a way into the tab it sits on.
        editorCase "a double-click on a tab's × ends that terminal and no other" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
                do! awaitU (page.Locator("#shell [role='tablist'] [data-terminal-close='term-harness']").DblClickAsync ())
                let! sent = await (page.EvaluateAsync<string> "() => JSON.stringify(window.__closed || [])")
                Expect.equal sent "[\"term-harness\"]" "one kill, of the tab the double-click was on"
            }

        // Delete on a focused tab is the ×'s press from the keyboard: the first arms, and the
        // armed face shows on that tab, so a reader can see what the next Delete will do.
        editorCase "Delete on a tab arms its kill, and Delete again kills it" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
                // Opening the pane lands focus in it a frame later (`paneLanding`). Focus put on
                // the tab before then is taken back by that landing, and the Deletes go to
                // wherever it put the keyboard — so the hand moves only once the pane has landed.
                do! twoFrames page
                do! awaitU (page.FocusAsync "#shell [data-pane-tab='terminal:term-harness']")
                do! awaitU (page.Keyboard.PressAsync "Delete")
                let! _ = await (page.WaitForSelectorAsync "#shell [role='tablist'] [data-terminal-close='term-harness'][data-terminal-close-armed='true']")
                let! sent = await (page.EvaluateAsync<int> "() => (window.__closed || []).length")
                Expect.equal sent 0 "the first Delete asked"
                do! awaitU (page.Keyboard.PressAsync "Delete")
                let! sent = await (page.EvaluateAsync<string> "() => JSON.stringify(window.__closed || [])")
                Expect.equal sent "[\"term-harness\"]" "the second killed that terminal"
            }

        // The keyboard half of the same control (UI baseline: a swap must never strand
        // focus). Escape takes the arming back and the control it was on keeps focus, so the
        // next Enter asks again rather than landing somewhere else — and the switcher it is in
        // stays open, because that Escape was about the arming.
        editorCase "Escape takes an armed kill back and leaves focus where it was" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
                do! awaitU (page.ClickAsync "#shell [data-pane-switcher]")
                let kill = "#shell [data-content-list] [data-terminal-close='term-harness']"
                do! awaitU (page.FocusAsync kill)
                do! awaitU (page.Keyboard.PressAsync "Enter")
                let! _ = await (page.WaitForSelectorAsync (kill + "[data-terminal-close-armed='true']"))
                do! awaitU (page.Keyboard.PressAsync "Escape")
                let! _ = await (page.WaitForSelectorAsync (kill + "[data-terminal-close-armed='false']"))
                let! focused =
                    await (page.EvaluateAsync<string> "() => document.activeElement?.getAttribute('data-terminal-close') ?? 'nothing'")
                Expect.equal focused "term-harness" "focus stays on the kill"
                let! sent = await (page.EvaluateAsync<int> "() => (window.__closed || []).length")
                Expect.equal sent 0 "and nothing was killed"
            }

        // A list somebody is pressing in holds still. It used to put the open terminals first,
        // so a terminal dying dropped its row to the bottom and slid the next one up under the
        // pointer. Measured as each row's top AND height before and after the closes land, by
        // row: a closed row that grows — its "recording lost" wrapping in the mark's narrow
        // track did exactly that — moves every row under it as surely as a reorder does.
        //
        // Two deaths, because there are two closed faces: the harness's first terminal has a
        // recording, and one opened here has none, so it closes into "recording lost".
        editorCase "the rows of the switcher do not move when a terminal dies" <| fun page ->
            async {
                let expect r = Result.defaultWith failwith r
                let fold (offset: int64) (event: Yession.Domain.SessionEvent) =
                    let envelope : Yession.Domain.EventEnvelope<Yession.Domain.SessionEvent> =
                        { EventId = Yession.Domain.EventId.fresh ()
                          SessionId = Yession.Domain.SessionId.create "harness" |> expect
                          Offset = Yession.Domain.EventOffset.create offset |> expect
                          Actor = Yession.Domain.ActorRef.Session
                          Timestamp = DateTimeOffset.UtcNow
                          Event = event }
                    let line = Yession.Codecs.Codec.toString Events.sessionEventEnvelope envelope
                    awaitU (page.EvaluateAsync ("line => window.__fold(line)", box line))
                let terminal (id: string) = Yession.Domain.TerminalId.create id |> expect
                let closed (id: string) =
                    Yession.Domain.SessionEvent.TerminalClosed
                        { Yession.Domain.Terminals.TerminalClosed.TerminalId = terminal id
                          Yession.Domain.Terminals.TerminalClosed.Reason = "closed by a peer"
                          Yession.Domain.Terminals.TerminalClosed.By = None }
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
                do! awaitU (page.ClickAsync "#shell [data-pane-switcher]")
                // Opened from elsewhere, so the newest and last: what its close must not change
                // is its own height.
                do!
                    fold 90L (
                        Yession.Domain.SessionEvent.TerminalOpened
                            { Yession.Domain.Terminals.TerminalOpened.TerminalId = terminal "term-bare"
                              Yession.Domain.Terminals.TerminalOpened.OpenedBy = Yession.Domain.ActorRef.Session
                              Yession.Domain.Terminals.TerminalOpened.Title = Yession.Domain.Terminals.TerminalTitle.fromProse "bare"
                              Yession.Domain.Terminals.TerminalOpened.Sandbox = None
                              Yession.Domain.Terminals.TerminalOpened.Renewable = false })
                let! _ = await (page.WaitForSelectorAsync "#shell [data-content-list] [data-terminal-close='term-bare']")
                let rows =
                    """() => JSON.stringify([...document.querySelectorAll('#shell [data-content-list] [role=listitem]')]
                        .filter(r => r.querySelector('[data-terminal-list-row]'))
                        .map(r => {
                            const b = r.getBoundingClientRect();
                            return [r.querySelector('[data-terminal-list-row]').getAttribute('data-terminal-list-row'),
                                    Math.round(b.top), Math.round(b.height)];
                        })
                        .sort())"""
                let! before = await (page.EvaluateAsync<string> rows)
                do! fold 91L (closed "term-harness")
                do! fold 92L (closed "term-bare")
                // Landed: a closed terminal offers no kill, and one with nothing kept says so.
                let! _ =
                    await (page.WaitForFunctionAsync
                            """!document.querySelector("#shell [data-terminal-close='term-harness'], #shell [data-terminal-close='term-bare']")""")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-terminal-list-gone='term-bare']")
                let! after = await (page.EvaluateAsync<string> rows)
                Expect.equal after before "every row where it was, the size it was"
            }

        // Task cards (Plan 20, stage 4). WHICH commands group, in what order, and what the
        // summary counts are all folds the cheap tier pins. What only a browser can answer is
        // that grouping does not cost a person a control: a burst's commands are behind a
        // disclosure now, and a line inside it has to be the same reachable, pressable thing
        // the chip was before it was grouped. Nothing here asserts the card's layout — that
        // is the design, and the design is what a card is FOR.
        editorCase "a task card's lines stay real controls, reachable and pressable without a pointer" <| fun page ->
            async {
                // The fold's own CSS animates `grid-template-rows` (0fr -> 1fr) over 200ms
                // (`Style.Motion.unfold`), so the instant after `data-fold-open` flips to
                // "yes" the lines are still sitting in a near-zero-height, `overflow-hidden`
                // row — real, `visibility: visible`, but with no laid-out area yet. Chromium's
                // sequential focus navigation skips a target with no area at the moment Tab is
                // pressed, so it landed on the next fold's arrow further down the page instead
                // of the line just revealed. Motion turned off is what every other case that
                // measures or reaches into something mid-animation already does (see "a turn
                // in flight is stated on the screen exactly once"), and every fold already
                // carries `motion-reduce:transition-none` for it.
                do! awaitU (page.EmulateMediaAsync (PageEmulateMediaOptions (ReducedMotion = ReducedMotion.Reduce)))
                // The timeline's one fold (`data-fold`), same as every other disclosure here:
                // a real button, keyboard-operable and announced without a role of our own.
                let! _ = await (page.WaitForSelectorAsync "#shell [data-chat-task-card]")
                do! awaitU (page.FocusAsync "#shell [data-chat-task-card] [data-fold]")
                do! awaitU (page.Keyboard.PressAsync "Enter")
                let! _ =
                    await (page.WaitForFunctionAsync
                        """document.querySelector('#shell [data-chat-task-card] [data-fold-body]')?.getAttribute('data-fold-open') === 'yes'""")

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
                // Settings' own fields (the Claude/GitHub panels, the model picker moved out
                // in Plan "dock send/model/interrupt", but a token input is still exactly
                // this case) live behind settings, and settings lives behind the sidebar —
                // both off-canvas on a phone until the column is brought on and turned to its
                // settings face. Through the controls a person uses, because the column is the
                // model's and a class set by hand would last only until the next render.
                do! awaitU (page.ClickAsync "#shell [data-nav-toggle='show']")
                do! awaitU (page.ClickAsync "#shell [data-settings-toggle='open']")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-claude-panel]")

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

        // The other half of the phone's floor: a thumb is not a pointer. 24px is WCAG 2.5.8's
        // minimum and 44 is the target this product holds (UI baseline), and the pane used to
        // offer a 24px kill glyph 4px from a 24px rewind. Asserted ONCE, here, over every
        // control a person can press in the surfaces held to it — never per surface, so the
        // next control in any of them is held without anybody remembering to.
        //
        // Held: the header, the nav drawer's workspace face, the pane, and the chat's terminal
        // chips. NOT yet the rest of the chat — a message's actions, a fold's toggle, a
        // chapter's name, a reply's ref — nor the settings face, whose fields and buttons are
        // the 32px control height: each is under 44 on a phone today. They join by widening
        // `held` (or, for settings, by adding its face below), and the case says what is
        // short the day they do.
        //
        // What counts is what a thumb can REACH: a control is measured when the point at its
        // centre, once scrolled into view, is the control — so a control under a sheet, off
        // the canvas or clipped away is not a target, and nothing has to be exempted by name.
        // `inert` is the model's own word for "not reachable"; `aria-hidden` is the one other
        // exemption, and it is WCAG's own: a duplicate whose act an equivalent control on the
        // same screen offers at full size (the pane's grab edge, beside its `›`). A text field
        // is held to the height only — its width is its line's.
        //
        // The faces are the ones a phone moves between: the chat with its header, the pane
        // on a terminal, its switcher, a preview opened from a chip, and the nav drawer.
        let thumbSized (width: int) (height: int) =
            editorCaseIn width height (sprintf "no control a phone can press is smaller than a thumb at %dx%d" width height) <| fun page ->
                async {
                    let! _ = await (page.WaitForSelectorAsync "#shell [data-content-toggle='show']")
                    let undersized (face: string) =
                        async {
                            let! small =
                                await (page.EvaluateAsync<string[]>
                                            """() => {
                                                 const small = []
                                                 const held = "#shell header, #shell aside, " +
                                                   "#shell [data-chat-block], #shell [data-chat-pending], #shell [data-chat-stretch]"
                                                 const pressable = "button, [role=tab], a[href], select, input:not([type=hidden])"
                                                 const controls = new Set()
                                                 for (const region of document.querySelectorAll(held)) {
                                                   if (region.matches(pressable)) controls.add(region)
                                                   for (const el of region.querySelectorAll(pressable)) controls.add(el)
                                                 }
                                                 for (const el of controls) {
                                                   if (el.closest('[inert], [aria-hidden=true]')) continue
                                                   el.scrollIntoView({ block: 'center', inline: 'nearest' })
                                                   const box = el.getBoundingClientRect()
                                                   if (box.width === 0 || box.height === 0) continue
                                                   const hit = document.elementFromPoint(box.left + box.width / 2, box.top + box.height / 2)
                                                   if (!hit || !el.contains(hit)) continue
                                                   const field = el.matches('select, input:not([type=checkbox]):not([type=radio])')
                                                   if (box.height < 43.5 || (!field && box.width < 43.5)) {
                                                     const name = Object.values(el.attributes).map(a => a.name)
                                                       .find(n => n.startsWith('data-') && n !== 'data-pane-tab')
                                                       || el.getAttribute('aria-label') || el.tagName
                                                     small.push(name + ' ' + Math.round(box.width) + 'x' + Math.round(box.height))
                                                   }
                                                 }
                                                 return small
                                               }""")
                            return small |> Array.map (fun s -> face + ": " + s)
                        }
                    let settle () =
                        awaitU (
                            page.EvaluateAsync
                                """() => Promise.all(
                                     document.getAnimations()
                                       .filter(a => a.effect && a.effect.getComputedTiming().iterations !== Infinity)
                                       .map(a => a.finished.catch(() => null)))""")
                    let onFace (face: string) (press: string) =
                        async {
                            do! awaitU (page.Locator(press).First.ClickAsync ())
                            do! settle ()
                            return! undersized face
                        }
                    do! settle ()
                    let! chat = undersized "the chat"
                    let! pane = onFace "the pane" "#shell [data-content-toggle='show']"
                    let! list = onFace "the switcher" "#shell [data-pane-switcher]"
                    do! awaitU (page.Keyboard.PressAsync "Escape")
                    do! waitFor "the switcher to shut" page "document.querySelector('#shell [data-content-list]') === null"
                    let! _ = onFace "" "#shell [data-content-toggle='hide']"
                    let! preview = onFace "a preview" "#shell [data-chat-block]"
                    let! _ = onFace "" "#shell [data-content-toggle='hide']"
                    let! nav = onFace "the nav" "#shell [data-nav-toggle='show']"
                    let all = Array.concat [ chat; pane; list; preview; nav ]
                    Expect.isEmpty all
                        (sprintf "these controls are under 44px on a phone: %s" (String.Join (", ", all)))
                }

        // The roster's row for an agent nobody has connected. It once stacked a full-width
        // boxed button under the row, taking the roster's second slot for good; now the verb
        // rides the row it acts on. Three promises, each only a laid-out page can keep: the
        // verb stands INSIDE the agent's own row (never hanging under it), it is what opens
        // settings, and on a phone it is a thumb's height.
        let noAgentRow (page: IPage) =
            async {
                do! awaitU (page.EvaluateAsync "() => window.__noAgent()")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-agent-presence='absent'] [data-no-agent-connect]")
                return ()
            }
        editorCaseIn 1440 900 "the verb that connects an absent agent stands inside the agent's own row" <| fun page ->
            async {
                do! noAgentRow page
                let! inside =
                    await (page.EvaluateAsync<bool>
                            """() => {
                                 const row = document.querySelector("#shell [data-agent-presence='absent']").getBoundingClientRect()
                                 const verb = document.querySelector('#shell [data-no-agent-connect]')
                                 const b = verb.getBoundingClientRect()
                                 return verb.tagName === 'BUTTON' && b.width > 0
                                     && b.top >= row.top - 0.5 && b.bottom <= row.bottom + 0.5
                               }""")
                Expect.isTrue inside "the connect verb is a button whose box lies within the agent's row"
            }
        editorCaseIn 1440 900 "the verb that connects an absent agent opens settings" <| fun page ->
            async {
                do! noAgentRow page
                do! awaitU (page.ClickAsync "#shell [data-no-agent-connect]")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-claude-panel]")
                return ()
            }
        editorCaseIn 390 844 "on a phone the verb that connects an absent agent is a 44px target" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-nav-toggle='show']")
                do! noAgentRow page
                let! height =
                    await (page.EvaluateAsync<float>
                            """() => {
                                 const verb = document.querySelector('#shell [data-no-agent-connect]')
                                 verb.scrollIntoView({ block: 'center' })
                                 return verb.getBoundingClientRect().height
                               }""")
                Expect.isTrue (height >= 43.5) (sprintf "a thumb's 44px, got %.1f" height)
            }
        thumbSized 390 844
        // A phone on its side is still a phone (`phone:` in app/tailwind.css): the same
        // controls, the same floor.
        thumbSized 844 390

        // Opening the nav on a phone shows the nav. The drawer and the pane are both sheets
        // over the chat there, at one layer, and the pane is later in the document — so the
        // drawer opened while the pane was up opened UNDER it: open, holding focus, and not on
        // the screen. Opening the drawer closes the pane first (`ClientModel.columnOn`).
        //
        // Pressed from the keyboard, because that is how it is reached with the pane up: the
        // pane covers the header on a phone, and the header's chevron is still a Tab stop. What
        // is asserted is what a person sees — the point at the drawer's centre is the drawer.
        let navOverPane (width: int) (height: int) =
            editorCaseIn width height (sprintf "opening the nav over the pane shows the nav at %dx%d" width height) <| fun page ->
                async {
                    do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
                    do! waitFor "the pane to cover the screen" page
                            """(() => {
                                 const r = document.querySelector('#shell [data-content-panel]').getBoundingClientRect()
                                 return r.left <= 1 && r.right >= window.innerWidth - 1
                               })()"""
                    do! awaitU (page.FocusAsync "#shell [data-nav-toggle='show']")
                    do! awaitU (page.Keyboard.PressAsync "Enter")
                    do! waitFor "the nav to be what is on screen at its centre" page
                            """(() => {
                                 const nav = document.querySelector('#shell aside:not([data-content-panel])')
                                 const r = nav.getBoundingClientRect()
                                 if (r.right <= 0) return false
                                 const hit = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2)
                                 return hit !== null && nav.contains(hit)
                               })()"""
                }
        navOverPane 390 844
        navOverPane 844 390

        // 844x390 is wider than the desktop breakpoint, and used to get its three columns: nav
        // 280, pane 360 and a chat of 204, one word to a line. A screen that short is a phone
        // (`phone:` in app/tailwind.css), so the chat is the screen and the columns are
        // sheets. Measured as the chat's width rather than as which classes applied: what a
        // person reading it gets is the room, whatever drew it.
        editorCaseIn 844 390 "a phone on its side keeps the chat the width of the screen" <| fun page ->
            async {
                let! _ = await (page.WaitForSelectorAsync "#shell [data-conversation]")
                let! width =
                    await (page.EvaluateAsync<float>
                        "() => document.querySelector('#shell [data-conversation]').getBoundingClientRect().width")
                Expect.isTrue (width >= 600.0) (sprintf "the chat is not squeezed between two columns: %.0fpx of 844" width)
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
    let psi =
        productStartInfo
            [ "--auth"; "localhost"
              "--port"; string (Uri(managerOrigin).Port)
              "--default-session"; MOUNT_SESSION
              "--data-dir"; mountDataDir ]
            // The two ADDRESSES stay variables: a session inherits them and parses them the
            // same way, which is the whole reason they are not options.
            [ "YESSION_MANAGER_URL", managerOrigin
              "YESSION_SESSION_URL", publicOrigin + "/s/{id}" ]
    psi.RedirectStandardOutput <- true
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
/// (`Client.fs`: `TerminalRecordsMsg` / `EventsAvailable` -> fetch -> `cache.Write`), started with
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
            try
                do! withContexts (fun contexts -> async {
                    let publicUrl = mounted.PublicUrl
                    let! page = contexts.Page None
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
                })
            finally
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
                try
                    do! withContexts (fun contexts -> async {
                        let publicUrl = mounted.PublicUrl
                        let! page = contexts.Page None
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
                        let! cookies = await (page.Context.CookiesAsync ())
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
                    })
                finally
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
                try
                    do! withContexts (fun contexts -> async {
                        let managerUrl = mounted.ManagerOrigin + "/"
                        let publicUrl = mounted.PublicUrl
                        let! page = contexts.Page None
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
                    })
                finally
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
                let stalled = TaskCompletionSource<unit> (TaskCreationOptions.RunContinuationsAsynchronously)
                let mounted =
                    startMounted (fun url ->
                        let stalls = stalling && url.EndsWith "/me"
                        if stalls then stalled.TrySetResult () |> ignore
                        stalls)
                try
                    do! withContexts (fun contexts -> async {
                        let publicUrl = mounted.PublicUrl
                        let! page = contexts.Page None
                        page.SetDefaultTimeout 20000.0f
                        let evidence = watching page
                        do! reporting "a probe that never answers" page evidence <| async {
                        // Signed in and connected first, so the reload below is a client that has
                        // everything but an answer — not one that is off to log in.
                        let! _ = await (page.GotoAsync publicUrl)
                        let! _ = await (page.WaitForFunctionAsync connected)
                        stalling <- true
                        // The page's time from here is the case's to turn, so the probe's deadline
                        // passes when the case says rather than ten real seconds later. Flowing,
                        // not paused: everything else on the page keeps its ordinary pace.
                        do! awaitU (page.Clock.InstallAsync ())
                        let! _ = await (page.ReloadAsync ())
                        // The probe is out, and held: its deadline is armed before it is sent.
                        let! asked = Async.AwaitTask (Task.WhenAny (stalled.Task, Task.Delay 20000))
                        Expect.isTrue (obj.ReferenceEquals (asked, stalled.Task)) "the reloaded page asked who it is, and the question was held"
                        do! awaitU (page.Clock.FastForwardAsync (int64 Yession.App.Client.Probe.deadline.TotalMilliseconds))
                        do! waitFor "the offer to reopen, once the probe has been given up on" page
                                """document.querySelector('[data-session-reopen]') !== null"""
                        }
                    })
                finally
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
                    do! awaitU (page.Locator("[data-content-toggle='show']").First.ClickAsync ())
                    do! openNewTerminal page
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
                    // The column comes back as this browser left it — open (P0-4) — so nothing
                    // is pressed here: the replayed records are on screen with no network
                    // behind them, which is what a store read before the network buys.
                    do! waitFor "the terminal output to come back offline" page terminalPrinted
                }))
    ]

// --- Spawning a piece of a deployment ----------------------------------------------------

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
let private deployStarting (label: string) (psi: ProcessStartInfo) (ready: string -> bool) : Deployed =
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

/// A piece that is not the product — a front door, a proxy — run as `command` with `args`.
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
    deployStarting label psi ready

/// The product itself as a piece of the deployment, booted the one way this suite boots it
/// (`productStartInfo`).
let private deployProduct
    (label: string)
    (args: string list)
    (env: (string * string) list)
    (ready: string -> bool)
    : Deployed =
    deployStarting label (productStartInfo args env) ready


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
                        // Read with the registry's own codec, and a frame it refuses is said
                        // rather than swallowed: walked by hand, a frame missing a field threw
                        // into the reconnect below, so the door re-read the same snapshot every
                        // 100ms and the case timed out naming the wait, never the frame.
                        match Yession.Manager.ControlWire.fromString Yession.Manager.ControlWire.sessionRegistryFrame (line.Substring 5) with
                        | Ok frame ->
                            for entry in frame.Sessions do
                                ports.[Yession.Domain.SessionId.value entry.Id] <- entry.Port
                        | Error reason -> printfn "front door: a registry frame it could not read: %s" reason
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
    // The management UI's line, not the session's: this case drives the Manager, and that line
    // is the last thing a completed boot prints.
    let host =
        deployProduct
            "fronted host"
            [ "--auth"; "localhost"
              "--port"; string managerPort
              "--default-session"; FRONT_SESSION
              "--data-dir"; frontDataDir ]
            [ "YESSION_MANAGER_URL", publicOrigin
              "YESSION_SESSION_URL", publicOrigin + "/s/{id}" ]
            (fun line -> line.Contains "management UI at")
    frontedHost <- host.Process

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
                try
                    do! withContexts (fun contexts -> async {
                        startFrontedHost door.Origin frontManagerPort
                        // Declining motion skips the opening screen's 2.8s dwell, which is the
                        // opening page's own cases' subject and not this one's.
                        let! page = contexts.Page (Some (BrowserNewContextOptions (ReducedMotion = ReducedMotion.Reduce)))
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
                    })
                finally
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
        deployProduct
            "the Manager"
            [ "--auth"; "trusted-headers"; "--secrets"; "ephemeral"
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
                try
                    do! withContexts (fun contexts -> async {
                        // The browser IS the ingress here: what `serve` would assert about the
                        // caller rides every request, including the sign-in bounce a session sends
                        // through the Manager — which is the request the identity has to survive.
                        let! page =
                            contexts.Page (
                                Some (
                                    BrowserNewContextOptions (
                                        // Past the opening screen's dwell, which is not this case's.
                                        ReducedMotion = ReducedMotion.Reduce,
                                        ExtraHTTPHeaders =
                                            dict [ "Tailscale-User-Login", FRONTED_LOGIN
                                                   "Tailscale-User-Name", FRONTED_NAME
                                                   "Tailscale-User-Profile-Pic", "https://example.com/alice.png" ])))
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
                                            """async () => JSON.stringify({
                                                 url: location.href,
                                                 title: document.title,
                                                 // Whether the page is being RENDERED at all, which
                                                 // "Connected with the right name" does not say: a
                                                 // document whose script runs while its rendering is
                                                 // suppressed reads as a wait that simply never fires.
                                                 // Cost one whole CI round to tell apart once.
                                                 rafAlive: await Promise.race([new Promise(r => requestAnimationFrame(() => r(true))), new Promise(r => setTimeout(() => r(false), 2000))]),
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
                    })
                finally
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
                    deployProduct
                        "the Manager"
                        // `--port 0`: the OS chooses, and the readiness line says which, so
                        // this suite knows nothing about any other's address.
                        [ "--auth"; "localhost"; "--secrets"; "ephemeral"
                          "--port"; "0"; "--data-dir"; filtersDataDir ]
                        []
                        (fun line -> line.Contains "management UI at")
                try
                    do! withContexts (fun contexts -> async {
                        let! page = contexts.Page None
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
                    })
                finally
                    manager.Stop ()
            }
    ]

// --- Pressing Create (browser) ---------------------------------------------------------------
//
let private installedDataDir = "tests/browser/.data-installed"

// A row opens its session in a new tab — except in an installed app, which has no tabs: there a
// new browsing context is a window with nothing painted in it, and on iOS that window was white
// in front of the opening screen whenever a stopped session was opened from its row. Create,
// which navigates in place, never was. So in an installed app a row is followed in place.
//
// Chromium cannot be MADE an installed app (`display-mode` is not an emulated media feature),
// so the page is told it is one by answering its own question — the query, by name — and
// nothing else about `matchMedia` changes. The destination is answered here: the promise is
// where the browser goes, not what the session does when it gets there.
let installedAppTests =
    testList "Opening a session from an installed Manager (browser)" [
        testCaseAsync "a row is followed in place, not into a new window" <|
            async {
                if Directory.Exists installedDataDir then Directory.Delete (installedDataDir, true)
                let manager =
                    deployProduct
                        "the Manager"
                        [ "--auth"; "localhost"; "--secrets"; "ephemeral"
                          "--port"; "0"; "--data-dir"; installedDataDir ]
                        []
                        (fun line -> line.Contains "management UI at")
                try
                    do! withContexts (fun contexts -> async {
                        let! page = contexts.Page None
                        page.SetDefaultTimeout 30000.0f
                        let evidence = watching page
                        do! reporting "installed app row" page evidence <| async {
                        do! awaitU (page.AddInitScriptAsync (
                                sprintf
                                    """(() => {
                                         const installed = %s
                                         const asked = window.matchMedia.bind(window)
                                         const answer = query => ({
                                           matches: true, media: query, onchange: null,
                                           addListener () {}, removeListener () {},
                                           addEventListener () {}, removeEventListener () {},
                                           dispatchEvent () { return false } })
                                         window.matchMedia = query => query === installed ? answer(query) : asked(query)
                                       })()"""
                                    (System.Text.Json.JsonSerializer.Serialize Yession.App.Dom.Manager.installedApp)))
                        do! awaitU (page.RouteAsync ("**/sessions/*/open", fun route ->
                                route.FulfillAsync (RouteFulfillOptions (Status = 200, ContentType = "text/html", Body = "<title>opening</title>"))
                                |> ignore))
                        let! _ = await (page.GotoAsync (manager.At "/"))
                        let row = sprintf "[%s]" Yession.App.Dom.Manager.openLink
                        let! _ = await (page.WaitForSelectorAsync row)
                        do! awaitU (page.Locator(row).First.ClickAsync ())
                        do! waitFor "this window to have gone to the session" page """location.pathname.endsWith('/open')"""
                        Expect.equal page.Context.Pages.Count 1 "and no second window was opened"
                        }
                    })
                finally
                    manager.Stop ()
            }
    ]

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
    (body: Deployed -> Contexts -> IPage -> Hold -> Async<unit>)
    : Async<unit> =
    async {
        if Directory.Exists pressDataDir then Directory.Delete (pressDataDir, true)
        let manager =
            deployProduct
                "the Manager"
                // `--port 0`: the OS chooses, and the readiness line says which. The Manager is
                // handed to the body because two of its cases have to address it — one of them
                // about the browser having LEFT this port, which is the one thing here that is
                // genuinely about a number.
                [ "--auth"; "localhost"; "--secrets"; "ephemeral"
                  "--port"; "0"; "--data-dir"; pressDataDir ]
                []
                (fun line -> line.Contains "management UI at")
        try
            do! withContexts (fun contexts -> async {
                // Declining motion skips the opening screen's 2.8s dwell on the way to the
                // session, which is the opening page's own cases' subject and not these.
                let! page = contexts.Page (Some (BrowserNewContextOptions (ReducedMotion = ReducedMotion.Reduce)))
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
                        do! body manager contexts page hold
                    finally
                        hold.LetGo ()
                }
            })
        finally
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
            withHeldCreate "held through a frame" AnswerNothing (fun manager contexts page hold -> async {
                let! at = whereCreateIs page
                do! push page at
                do! hold.Held
                // A frame, from elsewhere, while the Create is held: another reader archives
                // the seeded session, and the stream this page holds answers with the whole
                // table. (Nothing can be read off this page until the hold ends, so the hold
                // is let go — answered with nothing, which leaves the page where it was — and
                // the frame confirmed afterwards.) A context of its own, because another
                // reader is another person, and one that closes with the case.
                let! other = contexts.Page None
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

// --- The opening page: what a browser looks at while a session launches ---------------------
// Two promises only a browser can settle. The DWELL — the page goes once the Manager says
// the session answers, and not before the intro has landed — is a wait the page's own clock
// keeps, so it is measured from the outside with the answer faked to arrive at once: a page
// without the dwell would leave inside a second. And the switch for a reader who declined
// motion is CSS over SMIL, which only a rendered page can show applied.

let private openingDataDir = "tests/browser/.data-opening"

let private withOpening (name: string) (prepare: IPage -> Async<unit>) (body: Deployed -> IPage -> string -> Async<unit>) : Async<unit> =
    async {
        if Directory.Exists openingDataDir then Directory.Delete (openingDataDir, true)
        let manager =
            deployProduct
                "the Manager"
                [ "--auth"; "localhost"; "--secrets"; "ephemeral"
                  "--port"; "0"; "--data-dir"; openingDataDir ]
                []
                (fun line -> line.Contains "management UI at")
        try
            do! withContexts (fun contexts -> async {
                let! page = contexts.Page None
                page.SetDefaultTimeout 30000.0f
                let evidence = watching page
                do! reporting name page evidence <| async {
                    do! prepare page
                    // A session to open, created the way the button creates one; the answer to
                    // that is the address of the page under test.
                    use handler = new HttpClientHandler (AllowAutoRedirect = false)
                    use http = new HttpClient (handler)
                    let! created = await (http.PostAsync (manager.At "/sessions", new FormUrlEncodedContent (dict [ "name", "opening" ])))
                    let location = created.Headers.Location
                    Expect.isNotNull (box location) "Create answers with where the session now is"
                    let opening = if location.IsAbsoluteUri then location.ToString () else manager.At location.OriginalString
                    do! body manager page opening
                }
            })
        finally
            manager.Stop ()
    }

/// The Manager's readiness answer, faked: `ok` at once, or never. An `ok` carries where to go,
/// as the real one does — a session's sign-in entry, on an address the cases answer themselves.
let private readyAnswers (status: int) (page: IPage) : Async<unit> =
    awaitU (page.RouteAsync ("**/ready", fun route ->
        route.FulfillAsync (RouteFulfillOptions (Status = status, ContentType = "text/plain", Body = "http://127.0.0.1:1/login")) |> ignore))

let private marksShown =
    """() => JSON.stringify({
        intro: getComputedStyle(document.querySelector('[data-mark-intro]')).display,
        still: getComputedStyle(document.querySelector('[data-mark-static]')).display })"""

let openingTests =
    testList "The opening page (browser)" [
        testCaseAsync "it goes once the session answers, and not before the intro has landed" <|
            withOpening "the dwell" (fun page -> async {
                do! readyAnswers 200 page
                // The page's time is the case's to turn: its dwell is measured on
                // `performance.now` and waited out on `setTimeout`, both of which this clock
                // owns, and it stands still until the case moves it. Paused before the page
                // exists, so no timer of the page's can fire on the way.
                let start = DateTime (2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                do! awaitU (page.Clock.InstallAsync (ClockInstallOptions (TimeDate = start)))
                do! awaitU (page.Clock.PauseAtAsync (start.AddSeconds 1.0))
            }) (fun manager page opening -> async {
                // Wherever the page goes, it must not need the session to be up: the
                // destination is answered here, so what is under test is the page.
                do! awaitU (page.RouteAsync ("**/login*", fun route ->
                        route.FulfillAsync (RouteFulfillOptions (Status = 200, ContentType = "text/html", Body = "<title>session</title>")) |> ignore))
                let! _ = await (page.GotoAsync opening)
                let stillHere = sprintf "location.port === '%d'" manager.Port
                // The readiness has answered, so the hand-over is scheduled — and not yet due.
                do! waitFor "the page to have heard the session answer" page
                        (sprintf "document.querySelector('[%s]')?.textContent === 'ready'" Yession.App.Dom.Manager.openingWord)
                // The intro is 2.4s and the dwell 2.8s from the page's first script: a beat
                // short of it, the page is still showing.
                do! awaitU (page.Clock.RunForAsync 2700L)
                // A context torn down under the question is the page having navigated away,
                // which is the answer rather than an error.
                let! held =
                    async {
                        try return! await (page.EvaluateAsync<bool> (sprintf "() => %s" stillHere))
                        with _ -> return false
                    }
                Expect.isTrue held "the page held until the intro had landed"
                do! awaitU (page.Clock.RunForAsync 200L)
                do! waitFor "the browser to have left for the session" page (sprintf "location.port !== '%d'" manager.Port)
            })

        // The screen is sent before the launch it covers, so a launch that fails can only reach
        // whoever is looking through the screen's poll. Answered with the Manager's reason, the
        // screen says it then — rather than waiting out a bound written for a different fault
        // and reporting THAT one instead.
        testCaseAsync "a launch the Manager says has failed is on the screen, with its reason" <|
            withOpening "launch failed" (fun page ->
                awaitU (page.RouteAsync ("**/ready", fun route ->
                    route.FulfillAsync (
                        RouteFulfillOptions (Status = 500, ContentType = "text/plain", Body = "session process exited before ready (code 3)"))
                    |> ignore))) (fun _ page opening -> async {
                let! _ = await (page.GotoAsync opening)
                do! waitFor "the reason on the screen" page
                        "document.getElementById('status')?.textContent.includes('exited before ready (code 3)')"
            })

        testCaseAsync "a reader who declined motion is shown the still mark, and only it" <|
            withOpening "reduced motion" (fun page -> async {
                do! awaitU (page.EmulateMediaAsync (PageEmulateMediaOptions (ReducedMotion = ReducedMotion.Reduce)))
                do! readyAnswers 503 page
            }) (fun _ page opening -> async {
                let! _ = await (page.GotoAsync opening)
                let! shown = await (page.EvaluateAsync<string> marksShown)
                Expect.equal shown """{"intro":"none","still":"block"}""" "the intro is off and the still mark is on"
            })

        testCaseAsync "otherwise the intro is shown, and the still mark is not" <|
            withOpening "motion" (readyAnswers 503) (fun _ page opening -> async {
                let! _ = await (page.GotoAsync opening)
                let! shown = await (page.EvaluateAsync<string> marksShown)
                Expect.equal shown """{"intro":"block","still":"none"}""" "the intro is on and the still mark is off"
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
let installedAppTests : Fable.Pyxpecto.Model.TestCase = testList "Opening a session from an installed Manager (browser)" []
let pressTests : Fable.Pyxpecto.Model.TestCase = testList "Pressing Create (browser)" []
let openingTests : Fable.Pyxpecto.Model.TestCase = testList "The opening page (browser)" []

#endif
