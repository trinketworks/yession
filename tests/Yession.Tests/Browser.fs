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
// Waits are typed reads polled until they hold (`BrowserWait`); Playwright's own per-action
// timeouts watch.

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
open Yession.Tests.BrowserWait
open Yession.Tests.Loopback
open Yession.Tests.BrowserProbe
open Yession.Tests.BrowserHarness
open Yession.Tests.BrowserShims

module HarnessPage = Yession.App.HarnessPage

/// The URL out of a "launched at http://127.0.0.1:PORT/ …" line. Same shape the packaged
/// composition test uses to learn both endpoints from stdout.
let private urlIn (line: string) =
    let m = System.Text.RegularExpressions.Regex.Match (line, "http://[0-9.:]+/")
    if m.Success then Some m.Value else None

// --- Chromium discovery -----------------------------------------------------------------
//
// The rule is `Fable.NodeExtras.ChromiumExecutable`'s, which says where the browser comes from
// and why there is no fallback to a system Chrome. The Node launcher and `check Chromium`'s
// probe ask the same rule, so this suite and those cannot come to disagree about which browser
// is the pinned one.

/// `internal` from here down wherever the benchmark suite (`Bench.fs`) shares it: one browser
/// fixture serves both, rather than a second copy of the same launch/serve/listen scaffolding
/// free to drift from this one.
let internal chromiumPath () : string =
    match Fable.NodeExtras.ChromiumExecutable.find Fable.NodeExtras.ChromiumExecutable.dotnetMachine with
    | Ok executable -> executable
    | Error why -> failwith why

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

let private launchArgs = [| "--disable-features=WebRtcHideLocalIpsWithMdns" |]

let private browserLaunch () =
    BrowserTypeLaunchOptions (
        ExecutablePath = chromiumPath (),
        Args = launchArgs)

/// One at a time through the launch, so two cases asking at once get one browser between them.
let private launching = new SemaphoreSlim (1, 1)
let mutable private playwright : IPlaywright option = None
let mutable private browser : IBrowser option = None

/// The process's Playwright driver, started on first use. The caller holds `launching`.
let private driver () : Async<IPlaywright> =
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

/// The process's Chromium, launched on first use and relaunched if it has gone.
let private sharedBrowser () : Async<IBrowser> =
    async {
        do! awaitU (launching.WaitAsync ())
        try
            match browser with
            | Some live when live.IsConnected -> return live
            | _ ->
                let! pw = driver ()
                let! launched = await (pw.Chromium.LaunchAsync (browserLaunch ()))
                browser <- Some launched
                return launched
        finally
            launching.Release () |> ignore
    }

/// A case's share of the browser: the contexts it opened, every one closed when it ends — and the
/// pages it opened in them, which are what the case's waits ask whether the work sandbox behind
/// them did not start (`Wait.refusing`, installed by `withContexts`).
type internal Contexts (browser: IBrowser) =
    let opened = ResizeArray<IBrowserContext> ()
    let pages = ResizeArray<IPage> ()

    /// A fresh context — one person, with nothing kept from anybody before them.
    member _.Open (options: BrowserNewContextOptions option) : Async<IBrowserContext> =
        async {
            let! context =
                match options with
                | None -> await (browser.NewContextAsync ())
                | Some o -> await (browser.NewContextAsync o)
            lock opened (fun () -> opened.Add context)
            do! Probe.install context
            return context
        }

    /// A page in a context of its own.
    member this.Page (options: BrowserNewContextOptions option) : Async<IPage> =
        async {
            let! context = this.Open options
            let! page = await (context.NewPageAsync ())
            lock pages (fun () -> pages.Add page)
            return page
        }

    /// Why a wait in this case will not be met, if the session behind any of its pages has said
    /// its work sandbox did not start.
    member internal _.Refusal : Refusal =
        Refusal.sandboxDidNotStart (fun () -> lock pages (fun () -> List.ofSeq pages))

    member internal _.CloseAll () : Async<unit> =
        async {
            for context in lock opened (fun () -> List.ofSeq opened) do
                try do! awaitU (context.CloseAsync ()) with _ -> ()
        }

/// Run a case against the shared browser, its contexts closed however it ends, and every wait in
/// it ended at once by a session whose work sandbox did not start.
///
/// The refusal is installed HERE, with the contexts, because this is the one door every page of
/// every case comes through: a case that could open a page without the refusal is a case whose
/// first wait on a box with no sandbox sits out its timeout and names nothing about the box.
let internal withContexts (body: Contexts -> Async<'a>) : Async<'a> =
    async {
        let! br = sharedBrowser ()
        let contexts = Contexts br
        let! outcome = Async.Catch (Wait.refusing contexts.Refusal (body contexts))
        do! contexts.CloseAll ()
        return
            match outcome with
            | Choice1Of2 value -> value
            | Choice2Of2 e -> raise e
    }

/// Run a case in a browser profile on DISK, as a phone's is, closed and deleted however the
/// case ends.
///
/// A Chromium of its own rather than a context of the shared one. A context of the shared one
/// keeps its storage in memory, and a case about what a browser KEPT between visits should not
/// rest on how an engine treats storage it never meant to keep: WebKit's in-memory context
/// loses Cache API entries when the page navigates.
let internal withPersistentContext
    (options: BrowserTypeLaunchPersistentContextOptions)
    (body: IBrowserContext -> Async<'a>)
    : Async<'a> =
    async {
        do! awaitU (launching.WaitAsync ())
        let! pw = async { try return! driver () finally launching.Release () |> ignore }
        let profile = Path.Combine (Path.GetTempPath (), sprintf "yession-profile-%s" (Guid.NewGuid().ToString "N"))
        options.ExecutablePath <- chromiumPath ()
        options.Args <- launchArgs
        let! context = await (pw.Chromium.LaunchPersistentContextAsync (profile, options))
        let! outcome = Async.Catch (async {
            do! Probe.install context
            return! body context })
        try do! awaitU (context.CloseAsync ()) with _ -> ()
        try Directory.Delete (profile, true) with _ -> ()
        return
            match outcome with
            | Choice1Of2 value -> value
            | Choice2Of2 e -> raise e
    }

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

/// What the page says of itself, for a red case's report, or why it cannot say. Bounded at 5s,
/// because a page with a navigation that never commits never answers, and the failure must still
/// reach the run. Real time on purpose: the bound is raced against a page that may never answer,
/// and a page has no clock of its own for the case to turn.
[<Yession.Tests.Support.Waits>]
let private pageStateOrWhy (page: IPage) : Async<string list> =
    async {
        try
            let asked = Async.StartAsTask (Probe.ask page Yession.App.Probes.pageState 0)
            let! first = Async.AwaitTask (Task.WhenAny (asked :> Task, Task.Delay 5000))
            if obj.ReferenceEquals (first, asked) then return! Async.AwaitTask asked
            else return [ "unreadable: no answer in 5s" ]
        with pe -> return [ "unreadable: " + pe.Message ]
    }

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
            let! state = pageStateOrWhy page
            for fact in state do printfn "  page %s" fact
            printfn "=== end %s ===" label
            raise e
    }



/// Wait until some element matches a selector.
let private waitPresent (what: string) (page: IPage) (selector: string) : Async<unit> =
    Wait.until what (fun () -> Read.count page selector) (fun n -> n > 0)

/// Wait until the first match is visible: what Playwright's own `WaitForSelectorAsync` waits for,
/// but through `Wait`, which a session whose work sandbox did not start ends at once with its
/// reason. Every wait for an element to come on screen is this and not Playwright's, which sits
/// out its thirty seconds beside a page that already says why, and names the selector at best
/// (the "Playwright's own waits" guard holds every file under `tests/` to it). Internal, because
/// `Bench.fs` waits for its pages the same way.
let internal waitVisible (what: string) (page: IPage) (selector: string) : Async<unit> =
    Wait.until what (fun () -> Read.visible page selector) id

/// Wait until nothing matches a selector.
let private waitAbsent (what: string) (page: IPage) (selector: string) : Async<unit> =
    Wait.until what (fun () -> Read.count page selector) ((=) 0)

/// Wait until exactly this many elements match a selector.
let private waitCount (what: string) (page: IPage) (selector: string) (count: int) : Async<unit> =
    Wait.until what (fun () -> Read.count page selector) ((=) count)

/// Wait until some match's text contains this.
let private waitTextIn (what: string) (page: IPage) (selector: string) (text: string) : Async<unit> =
    Wait.until what (fun () -> Read.texts page selector) (List.exists (fun t -> t.Contains text))

/// Wait until some match's own text — carets aside — is exactly this: the text an element
/// really carries, with remote-caret widgets subtracted.
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
let private waitOwnText (what: string) (page: IPage) (selector: string) (text: string) : Async<unit> =
    Wait.until what (fun () -> Probe.ownTexts page selector) (List.contains text)

/// A block that ran and finished, whatever it ran.
let private finishedBlock = "[data-terminal-block][data-terminal-block-status='ok']"

/// Press the pane's switcher only while its list is not down: its door toggles, and the pane may
/// already have landed on the list. A frame or two first, for the pane to settle where it lands.
let private switcherDown (page: IPage) : Async<unit> =
    async {
        do! Probe.frames page 2
        let shut = "[data-pane-switcher][aria-expanded='false']"
        let! n = Read.count page shut
        if n > 0 then do! awaitU (page.Locator(shut).First.DispatchEventAsync "click")
    }

/// Open a terminal the way a person does: bring the list's sheet down and press New terminal
/// on its Terminals heading, which starts one in the session's own sandbox unless somebody
/// chose another place.
let private openNewTerminal (page: IPage) : Async<unit> =
    async {
        do! switcherDown page
        let door = "[data-terminal-new='default']"
        do! waitVisible "New terminal on the sheet" page door
        do! awaitU (page.Locator(door).First.ClickAsync ())
    }

/// The session this shell says it is, off its `<meta>`.
let private sessionMeta (page: IPage) : Async<string> =
    async {
        match! Read.attr page ("meta" + Sel.hookIs "name" Yession.App.Dom.sessionMetaName) "content" with
        | Some id -> return id
        | None -> return failwith "the shell carries no session id"
    }


// Read off the ATTRIBUTE, never off the words. A healthy client says nothing about being
// healthy any more — "Connected" was on three surfaces at once and is now on none — so the
// state token is the only place this can come from, which is where a markup contract belongs.
/// The connection state token, if the page carries one yet.
let private connectionState (page: IPage) () : Async<string option> =
    Read.attr page (Sel.hook Yession.App.Dom.Hooks.connection) Yession.App.Dom.Hooks.connection

/// Wait for the client to say it is connected.
let private waitConnectedAs (what: string) (page: IPage) : Async<unit> =
    Wait.until what (connectionState page) ((=) (Some Yession.App.Dom.Text.connected))

let private waitConnected (page: IPage) : Async<unit> = waitConnectedAs "the client to be connected" page

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
        do! waitVisible (sprintf "the command line %s to be on screen" selector) page selector
        match! Read.values page selector with
        | value :: _ -> return value
        | [] -> return failwithf "the command line %s went as it was read" selector
    }

/// Wait for a command line to say something, and when it never does, FAIL SAYING WHAT IT
/// SAYS. The interesting failures here are lines holding the wrong terminal's text or
/// wiping themselves as they are typed into, and a bare timeout hides exactly that.
let private waitCommandLine (page: IPage) (selector: string) (expected: string) : Async<unit> =
    Wait.until
        (sprintf "the command line %s to say %A" selector expected)
        (fun () -> Read.values page selector)
        (List.tryHead >> (=) (Some expected))

/// Which terminals have a command line ON SCREEN, in the order they are in the document.
///
/// The pane shows one terminal at a time, so this is one entry deep in practice — and that is
/// exactly the fact worth reading when a case cannot find the line it expected: "the pane is
/// showing a different terminal" and "the line is not there yet" are different faults, and a
/// locator timeout says neither.
let private commandLinesOn (page: IPage) : Async<string[]> =
    async {
        let lines = "[data-terminal-input^='term-draft:']"
        let! keys = Read.attrs page lines "data-terminal-input"
        let! readonly = Read.attrs page lines "readonly"
        return
            // Two looks, so a line that came or went between them is read without its flag
            // rather than paired with another's.
            keys
            |> List.mapi (fun i key ->
                (key |> Option.defaultWith (fun () -> "(no key)"))
                + (if List.tryItem i readonly |> Option.flatten |> Option.isSome then " (readonly)" else ""))
            |> Array.ofList
    }

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
        let! tab = Read.attr page (sprintf "[data-terminal-tab='%s']" terminal) "aria-selected"
        let selected = tab = Some "true"
        if not selected then do! awaitU (page.ClickAsync (sprintf "[data-terminal-tab='%s']" terminal))
        // The pane's offer is read beside the line, so the red of a wait that never settles says
        // which fault it was.
        do! Wait.until
                (sprintf "terminal %s to be showing its own command line" terminal)
                (fun () ->
                    async {
                        let! shown = Read.visible page (commandLine terminal)
                        let! offered = commandLinesOn page
                        return shown, offered
                    })
                fst
    }

/// The terminals the tab strip is offering, in the order it offers them.
let private terminalTabs (page: IPage) : Async<string[]> =
    async {
        let! keys = Read.attrs page "[data-terminal-tab]" "data-terminal-tab"
        return keys |> List.choose id |> Array.ofList
    }

/// The keys of the pane's tabs, in the strip's order.
let private terminalTabKeys (page: IPage) : Async<string list> =
    async {
        let! keys = Read.attrs page "[data-pane-tab]" "data-pane-tab"
        return List.choose id keys
    }

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
// Independence is also what lets these run several at once (`BrowserRunner`). A suite whose
// cases DO share something — a data dir by name, a host in a module-level mutable — is
// `testSequenced`, and says beside it what its cases share.

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
                                do! waitConnectedAs (sprintf "peer %d to connect" (i + 1)) page
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
        do! waitVisible "the new terminal's command line" page commandLine
        do! awaitU (page.ClickAsync commandLine)
        do! awaitU (page.Keyboard.TypeAsync "echo sta''rted; sleep 600")
        do! awaitU (page.Keyboard.PressAsync "Enter")
        do! waitTextIn "the long command to start" page "[data-terminal-output]" "started"
        do! stop page
        do! waitTextIn "the long command to end" page "[data-terminal-block]:not([data-terminal-block-status='running'])" "sleep 600"
        do! awaitU (page.ClickAsync commandLine)
        do! awaitU (page.Keyboard.TypeAsync "echo al''ive")
        do! awaitU (page.Keyboard.PressAsync "Enter")
        do! waitTextIn "the same terminal to run the next command" page "[data-terminal-block][data-terminal-block-status='ok']" "echo al''ive"
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
let private refusal = "[data-command-refused]"

/// The refusals a reader can see: painted, not merely present.
let private refusalsOnScreen (page: IPage) () : Async<Yession.App.Probes.Seen list> =
    async {
        let! all = Probe.seen page refusal
        return all |> List.filter (fun r -> r.Painted)
    }

/// A session with no resources profile has no `default` sandbox, so New terminal is refused —
/// the press this suite can make the session refuse without running anything. The profile
/// `check` sets for the whole run is taken away for this one host.
let private refusingNewTerminal = [ "YESSION_SESSION_RESOURCES", "" ]

/// Open the pane, press New terminal there, and wait for the refusal to be on screen.
let private refusedInPane (page: IPage) : Async<unit> =
    async {
        do! awaitU (page.Locator("[data-content-toggle='show']").First.ClickAsync ())
        do! openNewTerminal page
        do! Wait.until "a refusal on screen" (refusalsOnScreen page) (not << List.isEmpty)
    }

// --- Waits that end when the work sandbox did not start -----------------------------------
//
// A suite that runs a command on a box with no sandbox used to wait out its timeout: thirty
// seconds of "waiting for X — timed out" while the page already said why. The cases below are the
// page as the view renders its environment (`environmentSection`: the status, and the words on
// the same element), put in front of the fixture every case runs in (`withContexts`) — so what
// is pinned is the wait a real case makes, ended by what a real page says, and not a stand-in
// for either. The Node tier's clients have the same cases (`Sync.fs`, "Test harness").

/// The environment's section as the view renders it: its status, and the words it carries.
let private environmentSectionOf (status: string) (reason: string) : string =
    sprintf "<section %s %s></section>"
        (Yession.App.Dom.attr Yession.App.Dom.Hooks.environment status)
        (Yession.App.Dom.attr Yession.App.Dom.Hooks.environmentReason reason)

/// What a wait for something that never comes says when it ends, on a page whose environment
/// says this. `limitMs` is the deadline, so a wait that is NOT refused ends by it quickly.
let private neverComes (status: string) (reason: string) (limitMs: float) : Async<string> =
    withContexts (fun contexts ->
        async {
            let! page = contexts.Page None
            do! awaitU (page.SetContentAsync (environmentSectionOf status reason))
            let! outcome =
                Async.Catch (
                    Wait.within limitMs "a thing that never comes" (fun () -> Read.count page "[data-never]") (fun n -> n > 0))
            match outcome with
            | Choice1Of2 () -> return failwith "a wait for a thing that never comes must not resolve"
            | Choice2Of2 error -> return error.Message
        })

/// A server that is asked for a document and says nothing. The first request it gets is
/// kept, so the connection stays open for as long as the case holds the task.
let private silentServer () : Serving * Task<HttpListenerContext> =
    let server = listenOnLoopback ()
    let asked = TaskCompletionSource<HttpListenerContext> ()
    Async.Start (
        async {
            match! Async.Catch (server.Listener.GetContextAsync () |> Async.AwaitTask) with
            | Choice1Of2 ctx -> asked.TrySetResult ctx |> ignore
            | Choice2Of2 _ -> ()
        })
    server, asked.Task

/// A page of this case that is stuck mid-navigation: the browser has asked a server for the
/// page's next document and been told nothing, as a session that is starved, hung or half gone
/// tells a page reloading onto it. Every question put to the page now waits on that navigation,
/// and `Locator.CountAsync` is one of them — it has no deadline of its own.
let private pageStuckOnNavigation (contexts: Contexts) (server: Serving, asked: Task<HttpListenerContext>) : Async<IPage> =
    async {
        let! page = contexts.Page None
        do! awaitU (page.SetContentAsync (environmentSectionOf Yession.App.Dom.Text.envStarting ""))
        Async.Start (
            async {
                try do! awaitU (page.GotoAsync (server.At "/never", PageGotoOptions (Timeout = 120000.0f)) :> Task)
                with _ -> ()
            })
        // Certain it is mid-navigation, not about to be.
        let! _ = await asked
        return page
    }

/// How a wait ended, or the failure of a wait that had not ended `graceMs` after its deadline —
/// so a wait that outlives its deadline is a red case and not a run that never finishes. Real
/// time on purpose: the wait under test keeps a real-time deadline, and only the same clock can
/// say that it overran it.
[<Yession.Tests.Support.Waits>]
let private endedBy (limitMs: float) (waiting: Async<unit>) : Async<string> =
    async {
        let graceMs = 8000
        let running = Async.StartAsTask (Async.Catch waiting)
        let! first = Async.AwaitTask (Task.WhenAny [| running :> Task; Task.Delay (int limitMs + graceMs) |])
        if not (obj.ReferenceEquals (first, running)) then
            return
                failwithf
                    "the wait outlived its %gms deadline by %dms: something it was waiting on never returned, and nothing held it to its deadline"
                    limitMs
                    graceMs
        else
            match running.Result with
            | Choice1Of2 () -> return failwith "a wait for a thing that never comes must not resolve"
            | Choice2Of2 error -> return error.Message
    }

/// `body` in a case beside a server that never answers, stopped however the case ends.
let private withSilentServer (body: Contexts -> Serving * Task<HttpListenerContext> -> Async<string>) : Async<string> =
    async {
        let silent = silentServer ()
        let! outcome = Async.Catch (withContexts (fun contexts -> body contexts silent))
        (fst silent).Stop ()
        match outcome with
        | Choice1Of2 said -> return said
        | Choice2Of2 e -> return raise e
    }

let waitTests =
    testList "Browser waits" [
        testCaseAsync "a wait ends with the product's reason when the work sandbox failed to start" <|
            async {
                let! said = neverComes Yession.App.Dom.Text.envFailed "srt sandbox failed: no user namespace" 2000.0
                Expect.stringContains
                    said
                    "the work sandbox did not start: srt sandbox failed: no user namespace"
                    "the failure quotes the reason the session recorded"
            }

        // The message is read by whoever's suite just went red, on a box they may not have.
        testCaseAsync "the refusal says what a suite does about a box with no sandbox" <|
            async {
                let! said = neverComes Yession.App.Dom.Text.envFailed "boom" 2000.0
                Expect.stringContains
                    said
                    "the suite needs `Sandbox` in its `needs`"
                    "the refusal names the capability a command-running suite declares"
            }

        // The order that lets a suite expect a failure: the predicate is asked before the
        // refusal, so a wait FOR the failed environment is answered and not refused by it.
        testCaseAsync "a wait for the failed environment itself is answered, not refused" <|
            async {
                do! withContexts (fun contexts ->
                        async {
                            let! page = contexts.Page None
                            do! awaitU (page.SetContentAsync (environmentSectionOf Yession.App.Dom.Text.envFailed "boom"))
                            do! Wait.until
                                    "the environment to have failed"
                                    (fun () -> Read.attr page (Sel.hook Yession.App.Dom.Hooks.environment) Yession.App.Dom.Hooks.environment)
                                    ((=) (Some Yession.App.Dom.Text.envFailed))
                        })
            }

        // A wait that began before the failure is ended by it, and not left to its deadline.
        testCaseAsync "a wait pending when the work sandbox fails to start ends with the product's reason" <|
            async {
                let! said =
                    withContexts (fun contexts ->
                        async {
                            let! page = contexts.Page None
                            do! awaitU (page.SetContentAsync (environmentSectionOf Yession.App.Dom.Text.envStarting ""))
                            let waiting =
                                Async.StartAsTask (
                                    Async.Catch (
                                        Wait.within 20000.0 "a thing that never comes" (fun () -> Read.count page "[data-never]") (fun n -> n > 0)))
                            do! awaitU (page.SetContentAsync (environmentSectionOf Yession.App.Dom.Text.envFailed "srt sandbox failed: late"))
                            match! Async.AwaitTask waiting with
                            | Choice1Of2 () -> return failwith "a wait for a thing that never comes must not resolve"
                            | Choice2Of2 error -> return error.Message
                        })
                Expect.stringContains said "the work sandbox did not start: srt sandbox failed: late" "the failure quotes the reason"
            }

        // Only a failed start is a reason to stop waiting. An environment that is starting,
        // running, down or never asked for is one a wait may still outlast.
        testCaseAsync "every state of the environment but a failed start leaves a wait to wait" <|
            async {
                let! outcomes =
                    [ Yession.App.Dom.Text.envNotStarted
                      Yession.App.Dom.Text.envStarting
                      Yession.App.Dom.Text.envRunning
                      Yession.App.Dom.Text.envStopped ]
                    |> List.map (fun status ->
                        async {
                            let! said = neverComes status "" 300.0
                            return status, said
                        })
                    |> Async.Sequential
                let refused = outcomes |> Array.filter (fun (_, said) -> not (said.Contains "timed out after"))
                Expect.isEmpty refused "no state but a failed start refuses a wait; the rest ran to their deadline"
            }

        // A case opens pages as it goes, and one may be gone — a peer that left — while another
        // still says why.
        testCaseAsync "a page that cannot be asked leaves the others to say why" <|
            async {
                let! said =
                    withContexts (fun contexts ->
                        async {
                            let! gone = contexts.Page None
                            let! staying = contexts.Page None
                            do! awaitU (staying.SetContentAsync (environmentSectionOf Yession.App.Dom.Text.envFailed "srt sandbox failed: kept"))
                            do! awaitU (gone.CloseAsync ())
                            let! outcome =
                                Async.Catch (
                                    Wait.within 2000.0 "a thing that never comes" (fun () -> Read.count staying "[data-never]") (fun n -> n > 0))
                            match outcome with
                            | Choice1Of2 () -> return failwith "a wait for a thing that never comes must not resolve"
                            | Choice2Of2 error -> return error.Message
                        })
                Expect.stringContains said "srt sandbox failed: kept" "the page still open said it"
            }

        // The refusal belongs to the case it was installed for, and cases run several at once
        // (`BrowserRunner`). A page that says its sandbox failed ends the waits of its own case;
        // a case beside it, whose page says nothing of the kind, waits out its own deadline.
        testCaseAsync "a refusal ends the waits of its own case, and not of a case running beside it" <|
            async {
                // Held in force until the case beside it is waiting, so the two overlap by
                // arrangement rather than by how quickly two pages happen to open.
                let besideWaiting = TaskCompletionSource<unit> ()
                let failing =
                    withContexts (fun contexts ->
                        async {
                            let! page = contexts.Page None
                            do! awaitU (page.SetContentAsync (environmentSectionOf Yession.App.Dom.Text.envFailed "srt sandbox failed: next door"))
                            do! awaitU (besideWaiting.Task :> Task)
                        })
                let beside =
                    withContexts (fun contexts ->
                        async {
                            let! page = contexts.Page None
                            do! awaitU (page.SetContentAsync (environmentSectionOf Yession.App.Dom.Text.envStarting ""))
                            besideWaiting.TrySetResult () |> ignore
                            let! outcome =
                                Async.Catch (
                                    Wait.within 2000.0 "a thing that never comes" (fun () -> Read.count page "[data-never]") (fun n -> n > 0))
                            match outcome with
                            | Choice1Of2 () -> return failwith "a wait for a thing that never comes must not resolve"
                            | Choice2Of2 error -> return error.Message
                        })
                let! failed = Async.StartChild failing
                // However the case beside ends, the failing one is let go: a refusal that is never
                // released would hold this case until the run's budget kills it.
                let! said = async { try return! beside finally besideWaiting.TrySetResult () |> ignore }
                do! failed
                Expect.stringContains said "timed out after 2000ms" "the case beside the failed one waited out its own deadline"
            }

        // Within one case, one refusal. Two in force at once would end the case's waits by
        // whichever of their pages failed first.
        testCaseAsync "a second refusal installed while one is in force is refused" <|
            async {
                let refusal : Refusal = fun () -> async.Return None
                let! outcome = Async.Catch (Wait.refusing refusal (Wait.refusing refusal (async.Return ())))
                match outcome with
                | Choice1Of2 () -> failwith "two refusals in force at once must not be allowed"
                | Choice2Of2 error -> Expect.stringContains error.Message "already in force" "it says why"
            }

        testCaseAsync "a refusal is released when its case fails, so the next case can install its own" <|
            async {
                let refusal : Refusal = fun () -> async.Return None
                let! failed = Async.Catch (Wait.refusing refusal (async { return failwith "the case failed" }))
                Expect.isTrue (match failed with Choice2Of2 _ -> true | Choice1Of2 () -> false) "the case failed"
                do! Wait.refusing refusal (async.Return ())
            }

        // The wait is judged by the clock, and a look into a page can take no time that is
        // bounded: a call with no deadline of its own is a call that never returns. So the
        // deadline is the wait's to keep, whatever the calls inside it do.
        testCaseAsync "a wait ends by its deadline when the read it was given never returns" <|
            async {
                // Never resumes, and no timer stands behind it: it is a call into a page that
                // never returns, which is what this case is about.
                let never : Async<int> = Async.FromContinuations (fun _ -> ())
                let! said = endedBy 500.0 (Wait.within 500.0 "a read that never returns" (fun () -> never) (fun n -> n > 0))
                Expect.stringContains said "timed out after 500ms" "the wait ended by its deadline"
            }

        // The refusal asks every page of the case, the ones a wait is not about as well as the
        // one it is. A page stuck on a navigation holds its questions for as long as it is
        // stuck, and a wait beside it must not be as patient as it is.
        testCaseAsync "a wait ends by its deadline though a page of the case is stuck on a navigation" <|
            async {
                let! said =
                    withSilentServer (fun contexts silent ->
                        async {
                            let! reading = contexts.Page None
                            do! awaitU (reading.SetContentAsync (environmentSectionOf Yession.App.Dom.Text.envStarting ""))
                            let! _ = pageStuckOnNavigation contexts silent
                            return!
                                endedBy 1500.0 (Wait.within 1500.0 "a thing that never comes" (fun () -> Read.count reading "[data-never]") (fun n -> n > 0))
                        })
                Expect.stringContains said "timed out after 1500ms" "the wait ended by its deadline, as it does beside pages that answer"
            }

        // A stuck page has not said, which leaves the others to: the reason a sandbox did not
        // start is still told, and told at once, when another page of the case is wedged.
        testCaseAsync "a sandbox that did not start is still said beside a page stuck on a navigation" <|
            async {
                let! said =
                    withSilentServer (fun contexts silent ->
                        async {
                            // Opened first: the refusal asks pages in the order the case opened them,
                            // and one that stopped at the stuck page would never reach this one.
                            let! _ = pageStuckOnNavigation contexts silent
                            let! failed = contexts.Page None
                            do! awaitU (failed.SetContentAsync (environmentSectionOf Yession.App.Dom.Text.envFailed "srt sandbox failed: beside a stuck page"))
                            return!
                                endedBy 20000.0 (Wait.within 20000.0 "a thing that never comes" (fun () -> Read.count failed "[data-never]") (fun n -> n > 0))
                        })
                Expect.stringContains said "srt sandbox failed: beside a stuck page" "the page that answered said why"
            }
    ]

/// A moment of the real clock for something the page's own time released to arrive: a send goes
/// out on the browser's network thread, which page time does not move, so turning the page clock
/// cannot deliver it. Not arriving in the moment is no failure — the case goes on turning page
/// time — so this answers nothing, and spends 250ms of real time at most.
[<Yession.Tests.Support.Waits>]
let private networkMoment (arrived: Task) : Async<unit> =
    Async.AwaitTask (Task.WhenAny (arrived, Task.Delay 250)) |> Async.Ignore

let tests =
    testList "Browser E2E" [
        // The press was made in the pane, so the answer is said there — and once. The
        // conversation column has a mount too, and a reader looking at the pane does not look
        // up at the conversation's header for news about the button under their hand.
        sessionCaseOn refusingNewTerminal "a New terminal refused in the pane is said in the pane, once" <|
            fun page ->
            async {
                do! refusedInPane page
                let inPane = Sel.hook Yession.App.Dom.Hooks.contentPanel + " " + refusal
                do! Wait.until
                        "exactly one visible refusal, inside the pane, in the session's words"
                        (fun () ->
                            async {
                                let! shown = refusalsOnScreen page ()
                                let! shownInPane = Probe.seen page inPane
                                return shown, shownInPane |> List.filter (fun r -> r.Painted)
                            })
                        (fun (shown, shownInPane) ->
                            match shown, shownInPane with
                            | [ one ], [ _ ] -> one.Text.Contains "'default'"
                            | _ -> false)
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
                do! Probe.frames page 3
                do! awaitU (page.FocusAsync "[data-command-refused-dismiss]")
                do! awaitU (page.Keyboard.PressAsync "Enter")
                do! Wait.until
                        "the refusal gone, and focus in the pane it sat in"
                        (fun () ->
                            async {
                                let! refusals = Read.count page refusal
                                let! inPane = Read.count page (Sel.hook Yession.App.Dom.Hooks.contentPanel + " :focus")
                                return refusals, inPane
                            })
                        (fun (refusals, inPane) -> refusals = 0 && inPane > 0)
            }

        sessionPair "markdown typed in the rich composer renders formatted, converges, and sends as markdown" <|
            fun pageA pageB ->
            async {
                // A types Markdown into its rich composer with REAL key events, so the input
                // rules fire: "# " turns the block into a heading rendered live as an <h1> —
                // the syntax itself is never left as literal text (Linear-style WYSIWYG).
                do! waitVisible "A's composer" pageA composer
                do! awaitU (pageA.ClickAsync composer)
                do! awaitU (pageA.Keyboard.TypeAsync "# Heading one")
                do! Wait.until "A's own typing to render as a heading"
                        (fun () -> Probe.ownTexts pageA "[data-rich-readonly=\"false\"] .ProseMirror h1")
                        (List.tryHead >> (=) (Some "Heading one"))

                // B converges: it renders A's draft as the same formatted heading.
                // (Regression guard: pushing presence decorations on every render used to starve
                // y-prosemirror's rendering of REMOTE content here, so B's mirror stayed blank.)
                do! waitOwnText "B's mirror to render A's remote content" pageB ".ProseMirror h1" "Heading one"

                // And B JOINED it rather than opening a rival blank: the composer B is in is A's
                // draft, which is why the "new message" way out is offered at all.
                do! waitPresent "B to be offered a way out of A's draft" pageB "[data-draft-new]"

                // B overlays A's live caret in it: A's presence (a base64 relative position over
                // the draft body) decodes to a caret widget + name label. This lands just after
                // the content settles (the decoration push is debounced off the active-convergence
                // window). Guards remote BODY cursors end-to-end.
                do! waitPresent "B to overlay A's caret" pageB ".pm-caret"

                // B CO-EDITS A's draft — the collaboration the read-only mirror used to forbid —
                // and A sees the words appear in the draft it started.
                do! awaitU (pageB.ClickAsync composer)
                do! awaitU (pageB.Keyboard.PressAsync "End")
                do! awaitU (pageB.Keyboard.TypeAsync " and two")
                do! waitOwnText "A to see B's co-edit" pageA ".ProseMirror h1" "Heading one and two"

                // B sends A's draft: any co-editor may. Both timelines show the immutable message.
                // The durable body is MARKDOWN (`# Heading one and two`, from events not Yjs), but
                // the timeline RENDERS it as formatted rich text — the same heading the composer
                // showed — so the sent view mirrors the input: an <h1>, no literal `#`.
                do! awaitU (pageB.ClickAsync "[data-send-draft]")
                let sent (what: string) (page: IPage) =
                    Wait.until what
                        (fun () -> Read.texts page "[data-conversation] [data-message-body] h1")
                        (List.exists (fun h -> h.Trim () = "Heading one and two"))
                do! sent "A's timeline to show the sent message" pageA
                do! sent "B's timeline to show the sent message" pageB
            }

        // A page whose doc store closes under it (the browser cleared the site's data; mobile
        // Safari also loses the connection by itself) still collaborates. The store wrote each
        // change from inside the doc's `update` listener, and the throw there wedged the doc
        // for the rest of the page load (`Fable.YIndexeddb.catchWrites`): the first keystroke
        // went out, and nothing after it.
        sessionPair "a page whose doc store closed under it still sends what is typed" <|
            fun pageA pageB ->
            async {
                let! cdp = await (pageA.Context.NewCDPSessionAsync pageA)
                let clear = Collections.Generic.Dictionary<string, obj> ()
                clear.["origin"] <- box (Uri(pageA.Url).GetLeftPart UriPartial.Authority)
                clear.["storageTypes"] <- box "indexeddb"
                let! _ = await (cdp.SendAsync ("Storage.clearDataForOrigin", clear))
                do! waitVisible "A's composer" pageA composer
                do! awaitU (pageA.ClickAsync composer)
                do! awaitU (pageA.Keyboard.TypeAsync "kept nowhere")
                do! waitOwnText "B to see all of what A typed" pageB ".ProseMirror p" "kept nowhere"
            }

        // Who is in the room is everyone CONNECTED, not everyone editing something: two people
        // who have only opened the session are each on the other's roster. It listed a peer
        // only while their caret was in a field, so a collaborator reading the timeline was
        // nowhere — and the cheap tier, which folds a log it was handed, cannot see the real
        // session write the join every other client is reading.
        sessionPair "a peer editing nothing is on the other peer's roster" <|
            fun pageA pageB ->
            async {
                let others = Sel.hook Yession.App.Dom.Hooks.peerPresence
                do! waitCount "A to list B" pageA others 1
                do! waitCount "B to list A" pageB others 1
            }

        // And the room empties as people go: the link ending is what writes the leave.
        sessionPair "a peer who goes leaves the other peer's roster" <|
            fun pageA pageB ->
            async {
                let others = Sel.hook Yession.App.Dom.Hooks.peerPresence
                do! waitCount "A to list B" pageA others 1
                let! _ = await (pageB.GotoAsync "about:blank")
                do! waitCount "A to stop listing B" pageA others 0
            }

        // Terminals (Plan 13) in a real browser: the one part of the panel that only a
        // browser can exercise — the `<input>` bound to a `Y.Text` root. Everything under it
        // (the slot rule, the queue, the approval gate, the drain, the transcript) is covered
        // in the cheap tier; what is under test here is the binding itself, and that the
        // command really runs in the session's sandbox.
        //
        // `Sandbox` because of that last clause, and it is not a formality: the product's default
        // work sandbox IS srt (`SessionMain.fs`, `YESSION_SESSION_WORK_BACKEND`), so on a box that
        // cannot host one the command never runs. Untagged, the first wait ends at once and quotes
        // the sandbox's own reason (`Refusal.sandboxDidNotStart`) — a red that says "this machine
        // is not a machine this test can run on", where the tag makes it a skip, which is
        // precisely what a capability is for. It used to be thirty seconds of timeout naming
        // nothing about the box, and cost an agent a stash-and-re-run to discover that once.
        Tag.needs "a command that really runs" [ Tag.Browser; Tag.Native; Tag.Sandbox ] (fun () ->
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
                do! waitPresent "A's terminal in its strip" pageA "[data-terminal-tab]"
                do! awaitU (pageB.Locator("[data-content-toggle='show']").First.ClickAsync ())
                // The pane may already land on the list (a strip with nothing of B's in it), and
                // its door toggles, so it is pressed only while the list is not down.
                do! switcherDown pageB
                do! waitVisible "A's terminal in B's list" pageB "[data-content-list] [data-terminal-list-row]"
                do! awaitU (pageB.Locator("[data-content-list] [data-terminal-list-row]").First.ClickAsync ())
                do! waitPresent "the terminal in B's strip" pageB "[data-terminal-tab]"

                // A types a command with REAL key events, so the input's binding is what
                // writes the CRDT — one minimal edit per keystroke, not a wholesale replace.
                let composerInput = "[data-terminal-input^='term-draft:']:not([readonly])"
                do! waitVisible "A's command line" pageA composerInput
                do! awaitU (pageA.ClickAsync composerInput)
                do! awaitU (pageA.Keyboard.TypeAsync "echo hello-terminal")

                // B sees A writing it — the terminal's version of watching a draft, and the
                // proof that the binding pushes a remote edit back into the input's value.
                do! Wait.until "B to see A's command being written"
                        (fun () -> Read.values pageB "[data-terminal-input]") (List.contains "echo hello-terminal")

                // Sending runs it: a human's command needs no approval under the default
                // mode, so the drain takes it straight away and the sandbox really runs it.
                do! awaitU (pageA.ClickAsync "[data-terminal-send]")
                do! waitTextIn "the block to have run, on A" pageA finishedBlock "echo hello-terminal"
                do! waitTextIn "the block to have run, on B" pageB finishedBlock "echo hello-terminal"

                // And its OUTPUT arrived — over the terminal frames on both, through the same
                // fold either way.
                let output = Sel.hook Yession.App.Dom.Hooks.terminalOutput
                do! waitTextIn "the output, on A" pageA output "hello-terminal"
                do! waitTextIn "the output, on B" pageB output "hello-terminal"

                // The composer emptied on send, so the next command starts from a clean line.
                do! Wait.until "the composer to empty"
                        (fun () -> Read.values pageA composerInput) (List.tryHead >> (=) (Some ""))
            })

        // While somebody else holds a terminal's keyboard the session HOLDS its queue rather
        // than refusing it, so a watcher can still say the next command and it runs the moment
        // the terminal is handed back. That only works if the watcher has somewhere to write
        // it: the lease bar used to stand in place of the command line.
        Tag.needs "a command that really runs" [ Tag.Browser; Tag.Native; Tag.Sandbox ] (fun () ->
        sessionPair "a watcher queues a command during a lease and it runs on hand-back" <|
            fun pageA pageB ->
            async {
                do! awaitU (pageA.Locator("[data-content-toggle='show']").First.ClickAsync ())
                do! openNewTerminal pageA
                do! waitPresent "A's terminal in its strip" pageA "[data-terminal-tab]"
                do! awaitU (pageB.Locator("[data-content-toggle='show']").First.ClickAsync ())
                // The pane may already land on the list (a strip with nothing of B's in it), and
                // its door toggles, so it is pressed only while the list is not down.
                do! switcherDown pageB
                do! waitVisible "A's terminal in B's list" pageB "[data-content-list] [data-terminal-list-row]"
                do! awaitU (pageB.Locator("[data-content-list] [data-terminal-list-row]").First.ClickAsync ())
                do! waitPresent "the terminal in B's strip" pageB "[data-terminal-tab]"

                // A takes the keyboard; B sees whose it is.
                do! waitVisible "A's control to take the keyboard" pageA "[data-terminal-take]"
                do! awaitU (pageA.Locator("[data-terminal-take]").First.ClickAsync ())
                do! waitPresent "B to see the lease" pageB "[data-terminal-lease]"

                // B writes the next command anyway, and sends it the way a person does.
                let line = "[data-terminal-input^='term-draft:']:not([readonly])"
                do! waitVisible "B's command line" pageB line
                do! awaitU (pageB.ClickAsync line)
                do! awaitU (pageB.Keyboard.TypeAsync "echo queued-behind-a-lease")
                do! awaitU (pageB.Keyboard.PressAsync "Enter")
                do! waitPresent
                        "B's command held for the terminal, not run and not refused"
                        pageB
                        (Sel.hookIs Yession.App.Dom.Hooks.terminalQueuedStatus Yession.App.Dom.Text.queuedAwaitingTerminal)

                // A hands it back, and what B queued runs — for both of them.
                do! awaitU (pageA.ClickAsync "[data-terminal-release]")
                do! waitTextIn "the queued command to have run, on A" pageA finishedBlock "echo queued-behind-a-lease"
                do! waitTextIn "the queued command to have run, on B" pageB finishedBlock "echo queued-behind-a-lease"
            })

        // Reported from a live session: `+`, a command, then the command's chip in the chat —
        // and the strip went from `[TERMINAL]` to `[ECHO ONE]`, the live terminal gone from it
        // and reachable again only through the list. A chip opens a PREVIEW over the terminal
        // its command ran in (P2-1): the strip is terminals, and it does not change.
        Tag.needs "a command that really runs" [ Tag.Browser; Tag.Native; Tag.Sandbox ] (fun () ->
        sessionCase "the first chip tapped opens a preview, and the live terminal stays the strip" <|
            fun page ->
            async {
                // A fresh session offers its launch card. Put it away the way a person who is
                // not choosing a repo would; it stays away for the session.
                do! waitVisible "the launch card's way to put it away" page "[data-repo-picker-dismiss]"
                do! awaitU (page.ClickAsync "[data-repo-picker-dismiss]")
                do! waitAbsent "the launch card put away" page "[data-repo-picker]"
                do! awaitU (page.Locator("[data-content-toggle='show']").First.ClickAsync ())
                do! openNewTerminal page
                let composerInput = "[data-terminal-input^='term-draft:']:not([readonly])"
                do! waitVisible "the new terminal's command line" page composerInput
                do! awaitU (page.ClickAsync composerInput)
                do! awaitU (page.Keyboard.TypeAsync "echo one")
                do! awaitU (page.ClickAsync "[data-terminal-send]")
                do! waitVisible "the command's chip in the chat" page "[data-chat-block]"
                do! awaitU (page.ClickAsync "[data-chat-block]")
                do! waitVisible "the command's preview in the pane" page "[data-pane-preview^='block:']"
                let! strip = terminalTabKeys page
                Expect.equal
                    (strip |> List.map (fun key -> key.Split(':').[0]))
                    [ "terminal" ]
                    (sprintf "the terminal the press opened, and nothing beside it; the strip holds: %s" (String.Join (", ", strip)))
            })

        // Reported from a live session: Start on the launch card, and the transcript stayed
        // blank for the whole clone — "added repo" and "on branch" arrived together, six
        // seconds later. The session had written `RepoAdded` at once; the repo service appended
        // it through a log that told no browser, so a page heard of it only when the clone's
        // own event was announced. Only the real session process wires its services to its
        // log, so only a case that boots one can see this.
        //
        // The clone is held open by a git standing in for the session's (`YESSION_BIN_GIT`)
        // until the case lets it go, and the name the card looks up is answered by a server of
        // the case's own (`YESSION_GITHUB_API_URL`). Nothing here reaches github.com.
        Tag.needs "a real session cloning a repo" [ Tag.Browser; Tag.Native ] (fun () ->
        let dir = Path.GetFullPath (sprintf "tests/browser/.data/held-clone-%d" (Process.GetCurrentProcess().Id))
        let fixture = Path.Combine (dir, "fixture")
        let release = Path.Combine (dir, "release")
        let wrapper = Path.Combine (dir, "git")
        // Known before the session boots, which is when it reads the variable; bound in the body.
        let api = sprintf "http://127.0.0.1:%d" (freeLoopbackPort ())
        sessionCaseOn [ "YESSION_BIN_GIT", wrapper; "YESSION_GITHUB_API_URL", api ] "an added repo is in the transcript while its clone is still running" <|
            fun page ->
            async {
                // From PATH, not /bin or /usr/bin: a Nix devshell has neither git nor sleep there.
                let onPath name =
                    Environment.GetEnvironmentVariable("PATH").Split Path.PathSeparator
                    |> Array.map (fun d -> Path.Combine (d, name))
                    |> Array.find File.Exists
                let realGit = onPath "git"
                let sleep = onPath "sleep"
                let git (args: string list) =
                    let psi = ProcessStartInfo realGit
                    args |> List.iter psi.ArgumentList.Add
                    psi.WorkingDirectory <- fixture
                    use p = Process.Start psi
                    p.WaitForExit ()
                    if p.ExitCode <> 0 then failwithf "git %s exited %d" (String.Join (" ", args)) p.ExitCode
                if Directory.Exists dir then Directory.Delete (dir, true)
                Directory.CreateDirectory fixture |> ignore
                File.WriteAllText (Path.Combine (fixture, "README"), "fixture\n")
                git [ "init"; "-q"; "-b"; "main" ]
                git [ "add"; "README" ]
                git [ "-c"; "user.name=fixture"; "-c"; "user.email=fixture@example.invalid"; "commit"; "-q"; "-m"; "fixture" ]
                // Every verb but the clone is the real git. The clone waits for the case, then
                // clones the fixture into the place it was asked to, with none of the session's
                // config: that config allows https alone, and the fixture is a local path. A
                // minute at most, so a case that fails before letting it go strands nothing.
                File.WriteAllText (
                    wrapper,
                    String.concat "\n"
                        [ "#!/bin/sh"
                          "case \" $* \" in"
                          "  *\" clone \"*)"
                          "    for dest; do :; done"
                          "    i=0"
                          sprintf "    while [ ! -e '%s' ] && [ $i -lt 600 ]; do '%s' 0.1; i=$((i+1)); done" release sleep
                          sprintf "    exec /usr/bin/env -u GIT_CONFIG_COUNT -u GIT_CONFIG_PARAMETERS -u GIT_ALLOW_PROTOCOL '%s' clone -q --template= '%s' \"$dest\"" realGit fixture
                          "    ;;"
                          "esac"
                          sprintf "exec '%s' \"$@\"" realGit
                          "" ])
                File.SetUnixFileMode (wrapper, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)

                let listener = new HttpListener ()
                listener.Prefixes.Add (api + "/")
                listener.Start ()
                Async.Start (
                    async {
                        while listener.IsListening do
                            match! Async.Catch (listener.GetContextAsync () |> Async.AwaitTask) with
                            | Choice1Of2 ctx ->
                                let status, body =
                                    match ctx.Request.Url.AbsolutePath with
                                    | "/repos/octo/fixture" -> 200, """{"full_name":"octo/fixture","default_branch":"main","private":false}"""
                                    | "/repos/octo/fixture/branches" -> 200, """[{"name":"main"}]"""
                                    | _ -> 404, """{"message":"Not Found"}"""
                                let bytes = Text.Encoding.UTF8.GetBytes body
                                ctx.Response.StatusCode <- status
                                ctx.Response.ContentType <- "application/json"
                                ctx.Response.OutputStream.Write (bytes, 0, bytes.Length)
                                ctx.Response.Close ()
                            | Choice2Of2 _ -> ()
                    })
                try
                    do! waitVisible "the launch card's search" page "[data-repo-picker-search]"
                    // The card searches on a submit, not per keystroke (`LaunchSubmitted`).
                    do! awaitU (page.FillAsync ("[data-repo-picker-search]", "octo/fixture"))
                    do! awaitU (page.PressAsync ("[data-repo-picker-search]", "Enter"))
                    do! waitVisible "the fixture on the launch card" page "[data-repo-candidate='octo/fixture']"
                    do! awaitU (page.ClickAsync "[data-repo-candidate='octo/fixture']")
                    do! awaitU (page.ClickAsync "[data-repo-picker-start]")
                    // The clone cannot have finished: the case has not let it go.
                    do! waitTextIn
                            "the added repo, still cloning, in the transcript"
                            page
                            (sprintf "[data-act-note][data-act-status='%s']" Yession.App.Dom.Text.running)
                            "added repo"
                    File.WriteAllText (release, "")
                    do! waitTextIn "the clone's outcome on the same item" page "[data-act-note]" "on branch main"
                    let! items = Read.texts page "[data-act-note]"
                    let added = items |> List.filter (fun t -> t.Contains "added repo") |> List.length
                    Expect.equal added 1 "the clone fills the item the add drew, rather than drawing another"
                finally
                    File.WriteAllText (release, "")
                    listener.Stop ()
            })

        // A reload brings a person back to what they had (P0-4): the same terminals in the
        // strip, the one on top, and a column with width. A preview is NOT brought back: it
        // was a glance, and the terminal it was laid over is what comes back on top.
        Tag.needs "a command that really runs" [ Tag.Browser; Tag.Native; Tag.Sandbox ] (fun () ->
        sessionCase "a reload brings back the strip, the selection and the open pane" <|
            fun page ->
            async {
                do! waitVisible "the launch card's way to put it away" page "[data-repo-picker-dismiss]"
                do! awaitU (page.ClickAsync "[data-repo-picker-dismiss]")
                do! waitAbsent "the launch card put away" page "[data-repo-picker]"
                do! awaitU (page.Locator("[data-content-toggle='show']").First.ClickAsync ())
                // Three terminals, and the MIDDLE one chosen. Not the first: with nothing
                // remembered the pane lands on the strip's first open terminal, so a kept
                // first tab is the answer a reload gives whether or not it remembered — which
                // is how this case stayed green over a reload that came back to the first tab
                // every time. Not the last either, which is where the last press left it.
                do! openNewTerminal page
                do! waitCount "a terminal in the strip" page "[data-pane-tab]" 1
                do! openNewTerminal page
                do! waitCount "a second terminal in the strip" page "[data-pane-tab]" 2
                do! openNewTerminal page
                do! waitCount "a third terminal in the strip" page "[data-pane-tab]" 3
                let middle = "[data-pane-tab]:nth-child(2)"
                let! chosen = await (page.GetAttributeAsync (middle, "data-pane-tab"))
                do! awaitU (page.ClickAsync middle)
                let chosenOnTop (what: string) =
                    Wait.until what (fun () -> Read.attr page (sprintf "[data-pane-tab='%s']" chosen) "aria-selected") ((=) (Some "true"))
                do! chosenOnTop "the middle terminal to be on top"
                // A command in it, and that command's preview laid over it — which is not what
                // comes back; the terminal under it is.
                let composerInput = "[data-terminal-input^='term-draft:']:not([readonly])"
                do! waitVisible "the middle terminal's command line" page composerInput
                do! awaitU (page.ClickAsync composerInput)
                do! awaitU (page.Keyboard.TypeAsync "echo kept")
                do! awaitU (page.ClickAsync "[data-terminal-send]")
                do! waitVisible "the command's chip in the chat" page "[data-chat-block]"
                do! awaitU (page.ClickAsync "[data-chat-block]")
                do! waitVisible "the command's preview, laid over the terminal" page "[data-pane-preview]"

                // What was on top the FIRST moment the strip held all three again — recorded by
                // the page itself, from before the reload paints anything, because the fault
                // this pins was a window: the strip came back from the local store at once and
                // the choice only once catch-up had finished, so a reload showed the first tab
                // on top for as long as catch-up took. Waiting for the end of it, as this case
                // used to, waited the fault out.
                do! Shim.install page [ Yession.App.Shims.Shim.FirstOnTop 3 ]
                let! _ = await (page.ReloadAsync ())
                do! waitConnectedAs "the reloaded page to connect" page
                do! waitCount "all three terminals to be back in the strip" page "[data-pane-tab]" 3
                do! chosenOnTop "the chosen terminal to be on top again"
                let! firstOnTop = Harness.read page Yession.App.Shims.firstOnTop
                Expect.equal firstOnTop (Some chosen) "on top from the first moment the strip was back, not once catch-up ended"
                do! waitAbsent "no preview, which was a glance" page "[data-pane-preview]"
                do! Wait.until
                        "the pane to be open"
                        (fun () -> Probe.boxes page [ "[data-content-panel]" ])
                        (function
                         | [ Some pane ] -> Yession.App.Probes.Box.width pane > 1.0
                         | _ -> false)
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
        // `Sandbox` because the seed is a command that really runs: on a box that cannot host a
        // sandbox it never would, and untagged this would fail at once, quoting the sandbox's
        // reason, rather than skip.
        Tag.needs "what reopening a session costs" [ Tag.Browser; Tag.Native; Tag.Sandbox ] (fun () ->
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
                let printed (what: string) (n: int) =
                    waitTextIn what page (Sel.hook Yession.App.Dom.Hooks.terminalOutput) (sprintf "line-%d" n)

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
                // The column is open at every seed, and nothing here presses for it. The first
                // follows the show below; a reload brings the column back as this browser left
                // it (`PaneMemory`), or, with nothing remembered, it opens itself onto the
                // terminal the session has (`openOfItself`). It used to count the show toggle
                // and press it when found, and the column opening itself between the count and
                // the press took the toggle away from under it.
                let seed (first: int) (last: int) =
                    async {
                        do! waitVisible "the terminal's command line" page composerInput
                        do! awaitU (page.FocusAsync composerInput)
                        do!
                            awaitU (page.Keyboard.TypeAsync (
                                sprintf "for i in $(seq %d %d); do echo line-$i; sleep 0.01; done" first last))
                        do! awaitU (page.Locator("[data-terminal-send]").First.DispatchEventAsync "click")
                        // Seeded when the LAST line is on screen: the command finishing says it
                        // ran, this says the transcript holds what a reopen has to replay.
                        do! printed (sprintf "the terminal to print line-%d" last) last
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
                        do! waitConnectedAs "the reopened session to connect" page
                        do! printed "the reopened session to have replayed its terminal" upTo
                        let! settled = Probe.ask page Yession.App.Probes.quiet 0
                        let renders = settled.Renders |> Option.defaultValue -1
                        // Anti-vacuity, both ways this passes a budget while measuring
                        // nothing: a counter that was never incremented (the app stopped
                        // publishing one, so this reads -1 or 0), and a page still
                        // rendering when time ran out. The third — a transcript too short
                        // to cost anything — is the wait above, which does not settle
                        // until the last seeded line is back on screen.
                        if renders <= 0 then
                            failwithf
                                "the page reports %d renders — `Render.fs` publishes \
                                 `Probes.renders` and this budget means nothing without it"
                                renders
                        if not settled.Still then
                            failwithf
                                "the reopened session was still rendering after 30s (%d renders so far, %d \
                                 seeded lines) — the replay storm this budget exists for, not a slow box"
                                renders upTo
                        return renders
                    }

                do! awaitU (page.Locator("[data-content-toggle='show']").First.ClickAsync ())
                do! openNewTerminal page
                do! waitPresent "a terminal in the strip" page "[data-terminal-tab]"

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
        // `Sandbox` because opening a terminal starts a shell in the session's work sandbox: on a
        // box that cannot host one no terminal ever appears, and untagged this would fail at once,
        // quoting the sandbox's reason, rather than skip.
        Tag.needs "measuring several terminals" [ Tag.Browser; Tag.Native; Tag.Sandbox ] (fun () ->
        sessionCase "measuring the terminals costs one look at the document, however many are open" <|
            fun page ->
            async {
                let opened = 4
                let enough (what: string) =
                    Wait.until what (fun () -> Read.count page "[data-terminal-tab]") (fun n -> n >= opened)
                do! awaitU (page.Locator("[data-content-toggle='show']").First.ClickAsync ())
                for _ in 1 .. opened do
                    do! openNewTerminal page
                do! enough (sprintf "%d terminals in the strip" opened)

                let! _ = await (page.ReloadAsync ())
                do! waitConnectedAs "the reopened session to connect" page
                do! enough (sprintf "the reopened session to offer its %d terminals" opened)

                // Settled when the render count has stopped moving, which is also when the
                // scans have: they are made in the same pass.
                let! settled = Probe.ask page Yession.App.Probes.quiet 0
                let renders = settled.Renders |> Option.defaultValue -1
                let scans = settled.Scans |> Option.defaultValue -1
                // Anti-vacuity, both ways this passes while measuring nothing: a counter
                // the app stopped publishing (which reads -1, or 0 for a pass that never
                // ran), and a page still rendering when time ran out.
                if renders <= 0 then
                    failwithf
                        "the page reports %d renders — `app/browser/Render.fs` publishes \
                         `Probes.renders` and this budget means nothing without it"
                        renders
                if scans <= 0 then
                    failwithf
                        "the page reports %d viewport scans — `app/browser/Screens.fs` publishes \
                         `Probes.scans` and this budget means nothing without it"
                        scans
                if not settled.Still then
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
        // `Sandbox` because opening a terminal starts a shell in the session's work sandbox: on a
        // box that cannot host one, no terminal ever appears, and untagged this would fail at
        // once, quoting the sandbox's reason, rather than skip.
        Tag.needs "two terminals at once" [ Tag.Browser; Tag.Native; Tag.Sandbox ] (fun () ->
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
                do! Wait.until "two terminals in the strip" (fun () -> Read.count page "[data-terminal-tab]") (fun n -> n >= 2)
                let! tabs = terminalTabs page
                Expect.isTrue (tabs.Length >= 2) (sprintf "expected two terminals, the strip offers: %s" (String.Join (", ", tabs)))
                let one, two = tabs.[0], tabs.[1]
                // Settle first: opening is an event, and the pane follows the newest terminal
                // when it lands. Until that has happened there is no telling which terminal a
                // click is acting on — so the settling is WAITED for, not pressed for. Pressing
                // `two` read it unselected, and the pane could follow it between the read and the
                // press, which then landed on a selected tab and pinned it (`showTerminal`).
                do! Wait.until
                        (sprintf "the pane to follow %s, the newest terminal" two)
                        (fun () -> Read.attr page (sprintf "[data-terminal-tab='%s']" two) "aria-selected")
                        ((=) (Some "true"))

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
        // way the keyboard goes where the new terminal takes typing. `Sandbox` for the reason the
        // case above gives: no sandbox, no terminal.
        Tag.needs "a terminal to arrive" [ Tag.Browser; Tag.Native; Tag.Sandbox ] (fun () ->
        sessionCase "a new terminal takes the keyboard" <|
            fun page ->
            async {
                do! awaitU (page.Locator("[data-content-toggle='show']").First.ClickAsync ())
                do! openNewTerminal page
                do! waitVisible "the new terminal in the strip" page "[data-terminal-tab]"
                let! tabs = terminalTabs page
                do! Wait.until "the first terminal's command line to have focus"
                        (fun () -> Read.focused page (commandLine tabs.[0])) id
            })

        // Stopping one command is not ending the terminal. Before there was a Stop, the only
        // way out of a `sleep 600` was killing the terminal, its shell and its working
        // directory with it.
        Tag.needs "a command to stop" [ Tag.Browser; Tag.Native; Tag.Sandbox ] (fun () ->
        sessionCase "Stop on a running command ends it, and the terminal runs the next one" <|
            stoppedAndStillThere (fun page ->
                awaitU (page.Locator("[data-terminal-stop]").First.ClickAsync ())))

        // With nothing typed, Ctrl-C at the command line has the meaning it has in a terminal.
        Tag.needs "a command to stop" [ Tag.Browser; Tag.Native; Tag.Sandbox ] (fun () ->
        sessionCase "Ctrl-C in an empty command line ends the running command, and the terminal runs the next one" <|
            stoppedAndStillThere (fun page ->
                async {
                    do! awaitU (page.ClickAsync "[data-terminal-input^='term-draft:']:not([readonly])")
                    do! awaitU (page.Keyboard.PressAsync "Control+c")
                }))

        // The kill is pressed on a row of the switcher, and the close that answers it takes
        // that row's kill away — so focus lands on the row that takes its place, rather than
        // on `body` with the switcher still under the reader's hand.
        Tag.needs "two terminals to kill one of" [ Tag.Browser; Tag.Native; Tag.Sandbox ] (fun () ->
        sessionCase "killing a terminal from the switcher lands on the next one" <|
            fun page ->
            async {
                do! awaitU (page.Locator("[data-content-toggle='show']").First.ClickAsync ())
                do! openNewTerminal page
                do! openNewTerminal page
                do! Wait.until "two terminals in the strip" (fun () -> Read.count page "[data-terminal-tab]") (fun n -> n >= 2)
                let! tabs = terminalTabs page
                let one, two = tabs.[0], tabs.[1]
                do! awaitU (page.ClickAsync "[data-pane-switcher]")
                let kill = sprintf "[data-content-list] [data-terminal-close='%s']" one
                do! awaitU (page.FocusAsync kill)
                // Two presses: the first arms the kill, the second, on the same control,
                // performs it (`KillArmed`).
                do! awaitU (page.Keyboard.PressAsync "Enter")
                do! waitVisible "the kill, armed" page (sprintf "%s[data-terminal-close-armed='true']" kill)
                do! awaitU (page.Keyboard.PressAsync "Enter")
                do! waitAbsent "the killed terminal's row to go" page kill
                do! Wait.until "focus on the next row in the list"
                        (fun () -> Read.focused page (sprintf "[data-content-list] %s" (Sel.hookIs "data-terminal-list-row" two))) id
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
                let! embedded = Read.attr pageA ("meta" + Sel.hookIs "name" Yession.App.Dom.managerMetaName) "content"
                let origin =
                    match embedded with
                    | Some origin when origin <> "" -> origin
                    | _ -> failwith "the bootstrap page must embed the Manager's origin"
                Expect.isTrue (origin.StartsWith "http") (sprintf "expected an origin, got: %s" origin)
                // The client appends `/sessions/{id}/open` to this, so it must be an origin
                // root with no trailing slash — otherwise the URL it builds has a double one.
                Expect.isFalse (origin.EndsWith "/") "no trailing slash: the client concatenates a path onto it"
                let! sessionId =
                    sessionMeta pageA
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
                do! waitVisible "the default work sandbox's row in its panel" pageA "[data-query-panel='work_sandboxes'] [data-query-row]"
                let! overflowing = Probe.verdict pageA Yession.App.Probes.Measure.LongValueInLane
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
                do! waitVisible "the default work sandbox's row in its panel" pageA "[data-query-panel='work_sandboxes'] [data-query-row]"
                let! starved = Probe.verdict pageA Yession.App.Probes.Measure.LabelCostsValue
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
                do! waitVisible "the model picker to say no model is available" pageA "[data-model-note='unavailable']"
                let note = "[data-model-note]"
                // The precondition, and the sentence a person actually read: this session
                // has no credential, so the note says so and points at the panel above it.
                let! before = Read.text pageA note
                Expect.isTrue
                    (before |> Option.exists (fun said -> said.Contains "no Claude account connected"))
                    (sprintf "with nothing connected the picker says so, it said: %A" before)

                // Connect one WITHOUT closing the drawer — the flow a person is actually in
                // when they sign in, and the one that used to leave the note behind.
                do! awaitU (pageA.ClickAsync "[data-claude-use-token]")
                do! awaitU (pageA.FillAsync ("[data-claude-token]", "sk-ant-oat01-browser-case"))
                do! awaitU (pageA.ClickAsync "[data-claude-save-token]")
                do! waitVisible "the panel to say the account is connected" pageA "[data-claude-connected='mine']"

                // Whatever the picker says now, it cannot still be that: an account IS
                // connected. What it says instead is whatever the provider answered this
                // box — a list, or why there is none — which is the design rather than
                // something for a test to pin.
                do! Wait.until "the note to stop saying nothing is connected"
                        (fun () -> Read.text pageA note)
                        (Option.forall (fun said -> not (said.Contains "no Claude account connected")))
            }

        sessionCase "the doc store is keyed by session" <|
            fun pageA ->
            async {
                // The store is keyed by SESSION (embedded in the served page), not by address.
                let! sessionId =
                    sessionMeta pageA
                Expect.isFalse (String.IsNullOrEmpty sessionId) "the bootstrap page must embed the session id"
                let! dbNames = Probe.databases pageA
                Expect.isTrue
                    (List.contains (sprintf "yession/session/%s" sessionId) dbNames)
                    (sprintf "expected a session-keyed doc store, found: %s" (String.Join (", ", dbNames)))
            }

        // The same browser is the same person. The peer id was always kept per browser; the
        // name was drawn fresh on every load, so a reload renamed the person and the chat then
        // attributed what they had done to somebody new. Under `--auth localhost` nothing else
        // names a peer, so the name a reload comes back with is the one this browser kept.
        sessionCase "a reload keeps the peer's name" <|
            fun page ->
            async {
                let ownName () = Read.text page (Sel.hook Yession.App.Dom.Hooks.displayName)
                let! before = ownName ()
                Expect.isTrue (before |> Option.exists (not << String.IsNullOrWhiteSpace)) "a connected peer shows its own name"
                let! _ = await (page.ReloadAsync ())
                do! waitConnectedAs "the reloaded page to connect" page
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
                do! waitVisible "the control that connects an account" page "[data-claude-connect]"
                do! awaitU (page.ClickAsync "[data-claude-connect]")

                // The flow settles either into the paste-the-code step (the broker minted a
                // provider authorize URL — no network involved) or into a legible error.
                do! waitPresent "the paste-the-code step, or an error" page "[data-claude-authorize], [data-claude-error]"
                let! error = Read.text page "[data-claude-error]"
                Expect.isTrue (error |> Option.forall String.IsNullOrEmpty) (sprintf "connecting must not be refused on a first visit, it said: %A" error)
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
                do! Shim.install page [ Yession.App.Shims.Shim.GatheringNeverEnds ]
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
                do! Wait.until "the reloaded page to gather a candidate"
                        (fun () -> Harness.read page Yession.App.Shims.candidates) (Option.exists (fun n -> n > 0))
                let window = int64 Yession.App.Client.Gathering.quiet.TotalMilliseconds + 1L
                let cap = int64 Yession.App.Client.Gathering.cap.TotalMilliseconds
                // A window at a time, because a later candidate restarts it: a box with several
                // interfaces may still be delivering when the first window closes.
                let mutable spent = 0L
                while not offered.Task.IsCompleted && spent + window < cap do
                    do! awaitU (page.Clock.RunForAsync window)
                    spent <- spent + window
                    // Each window is given a moment of the real clock to deliver what it released
                    // before the next one is spent; not arriving in it is no failure, and the loop
                    // goes on to the cap.
                    do! networkMoment offered.Task
                // No page time passes from here, so only a send already made can still arrive.
                do! Wait.within 10000.0
                        (sprintf "an offer posted inside %dms of page time, short of the %dms cap" spent cap)
                        (fun () -> async.Return offered.Task.IsCompleted)
                        id
            }

        // A connect that lasts is said, and said where a phone reader can see it. It used to be
        // said nowhere: `Connecting` put no words on the page at any width, so a cold session
        // whose first attempt did not land was a page with nothing on it for as long as that
        // took. The session's answer is held at the browser's edge, so the first attempt runs
        // for as long as the case looks; the page's own handshake deadline is far beyond the
        // moment the report is due.
        sessionCase "a connect that lasts is on screen at phone width" <|
            fun page ->
            async {
                let signal = "**" + Yession.App.RelativeUrl.under "" (Yession.App.SessionRoute.relative Yession.App.SessionRoute.Signal)
                do! awaitU (page.RouteAsync (signal, fun route ->
                        // The offer is never answered: a session that has not answered yet.
                        if route.Request.Method <> "POST" then route.ContinueAsync () |> ignore))
                do! awaitU (page.SetViewportSizeAsync (390, 844))
                let! _ = await (page.ReloadAsync ())
                let report = "[" + Yession.App.Dom.attr Yession.App.Dom.Hooks.degraded Yession.App.Dom.Text.degradedConnecting + "]"
                do! Wait.until "the connect to be reported where a phone reader can see it"
                        (fun () -> Probe.seen page report)
                        (List.exists (fun seen -> seen.Painted))
            }

        // An error nothing handled, raised while the page is on its way to its session, can
        // stop it there, and the page had said "connecting" for as long as it was open with
        // nothing to press. The handshake is held as above, so the page is still on its way
        // when the error comes (`Probes.unhandled`); the way to start again is on screen where
        // a phone reader can see it.
        sessionCase "an error on a page's way to its session puts a reload on screen" <|
            fun page ->
            async {
                let signal = "**" + Yession.App.RelativeUrl.under "" (Yession.App.SessionRoute.relative Yession.App.SessionRoute.Signal)
                do! awaitU (page.RouteAsync (signal, fun route ->
                        if route.Request.Method <> "POST" then route.ContinueAsync () |> ignore))
                do! awaitU (page.SetViewportSizeAsync (390, 844))
                let! _ = await (page.ReloadAsync ())
                do! Probe.unhandled page "stopped on purpose"
                do! Wait.until "a reload on screen where a phone reader can see it"
                        (fun () -> Probe.seen page "[data-page-reload]")
                        (List.exists (fun seen -> seen.Painted))
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
                do! Shim.install page [ Yession.App.Shims.Shim.AnswerApplied ]
                do! awaitU (page.Clock.InstallAsync ())
                let! _ = await (page.ReloadAsync ())
                do! Wait.until "the session's answer to be applied"
                        (fun () -> Harness.read page Yession.App.Shims.answered) ((=) (Some true))
                do! awaitU (page.Clock.FastForwardAsync 10000L)
                do! Wait.within 10000.0 "the handshake to settle" (fun () -> async.Return settled.Task.IsCompleted) id
                Expect.stringContains settled.Task.Result "handshake unrouted" "an answered handshake that never opened is unrouted, not silent"
            }
    ]

// --- The host-free editor rendering E2E ([Browser], no Native) ---------------------------
// Serves the static harness (app/browser/EditorHarness.fs, esbuilt to tests/browser/out/) with
// `Loopback.serveStatic`, and
// drives one Chromium page. No Session, no WebRTC — so this runs wherever Chromium
// exists, decoupled from the native node-datachannel addon. It guards exactly what the DOM-free
// cheap tests cannot: the input-rule → live formatting → Markdown round-trip in a real browser.

/// Which of the harness's two pages a case is served (`tests/browser/editor-harness.html` says
/// why it is two). A case's geometry is the page's, and not whatever ran before it.
[<RequireQualifiedAccess>]
type internal HarnessOn =
    /// The shell alone: a viewport tall, at the top of a document that cannot scroll, so a box
    /// the shell draws is where it is measured whatever a click, a focus or a retry did first.
    | Shell
    /// The editor, the players and the two peers in the page's flow, above the shell.
    | Fixtures
    /// The page's address on the harness's origin.
    member this.Path =
        match this with
        | HarnessOn.Shell -> "/"
        | HarnessOn.Fixtures -> "/?fixtures"

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
    (on: HarnessOn)
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
                    let! _ = await (page.GotoAsync (server.At on.Path))
                    do! reporting name page evidence (body page)
                })
            server.Stop ()
            match outcome with
            | Choice1Of2 () -> ()
            | Choice2Of2 e -> raise e
        }

/// A case at the browser's own window size, on the shell's page.
let private editorCase = editorCaseOn HarnessOn.Shell None

/// A case about one of the fixtures — the editor, a player, the two peers — on their page.
let private fixtureCase = editorCaseOn HarnessOn.Fixtures None

/// Wait for the focus move a chip's click asks for. Opening from the chat hands focus to the
/// pane (`DomMove.FocusPane`), a frame AFTER the render — so a case that goes on to put focus
/// somewhere itself, before that frame, has its focus taken back from under it by the move
/// it did not wait for. Which of the two lands last is timing, and a slower runner lost it.
let private focusReachedPane (page: IPage) : Async<unit> =
    Wait.until "focus to follow the chip into the pane" (fun () -> Read.focused page "[data-pane-panel]") id

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
    Harness.run1 page HarnessPage.fold (foldBody events)

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
        do! Wait.until "the keyboard to be in the harness terminal"
                (fun () -> Read.focused page (Sel.hookIs Yession.App.Dom.Hooks.terminalKeys "term-harness")) id
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
        do! Wait.until "focus to land on the command line" (fun () -> Read.focused page (commandLine "term-harness")) id
        do! foldHarness page [ for i in 0 .. 3 -> 80L + int64 i, opened (sprintf "term-more-%d" i) ]
        do! waitCount "six tabs in the strip" page "#shell [data-pane-strip] [role=tab]" 6
        // The column OPENS by animating its width, and a case that measures what is on screen
        // in it measures a narrower pane until that has finished — its right-hand controls
        // clipped away by the column's own edge for the length of the transition.
        do! Probe.settled page
    }

/// Whether the strip holds more tabs than it shows — the precondition every scrolling case
/// asserts first, because on a strip that fits, "in view" is true of everything.
let private stripOverflows (page: IPage) : Async<bool> =
    async {
        match! Probe.scroll page "#shell [data-pane-strip]" with
        | Some strip -> return Yession.App.Probes.Scroll.overflowsSideways strip
        | None -> return failwith "there is no strip to overflow"
    }

/// A chip in the chat pressed, and the preview it opens settled on screen — the column's
/// opening animation finished, so what is measured is where things land, not where they pass.
let private previewSettled (page: IPage) : Async<unit> =
    async {
        do! awaitU (page.ClickAsync "#shell [data-chat-block]")
        do! waitVisible "the chip's preview in the pane" page "#shell [data-pane-preview]"
        do! Probe.settled page
    }

/// Whether the selected tab lies wholly inside the strip's box, sideways — both measured in one
/// frame.
let private selectedTabInsideStrip (page: IPage) : Async<bool> =
    async {
        match! Probe.boxes page [ "#shell [data-pane-strip]"; "#shell [data-pane-strip] [role=tab][aria-selected=true]" ] with
        | [ Some s; Some t ] -> return t.Left >= s.Left - 0.5 && t.Right <= s.Right + 0.5
        | measured -> return failwithf "the strip and its selected tab were not both there: %A" measured
    }


/// A case at a stated viewport. `ViewportSize` alone, never `IsMobile`: that additionally asks
/// Chromium to fit the layout to a device window, which measured here lands at 648px rather
/// than 390 — the very lie the ui-exploration skill warns about, arriving through another door.
let private editorCaseIn (width: int) (height: int) =
    editorCaseOn HarnessOn.Shell (Some (BrowserNewContextOptions (ViewportSize = ViewportSize (Width = width, Height = height))))

/// A TOUCH screen: the same honest viewport, and `HasTouch`, which is what takes hover away —
/// Chromium answers `(hover: none)` for a touch device, as a phone does. Without it this
/// browser is a phone-sized window with a mouse over it, and a rule written for a device that
/// cannot hover is a rule no case here ever sees applied.
let private touchScreen (width: int) (height: int) : BrowserNewContextOptions option =
    Some (BrowserNewContextOptions (ViewportSize = ViewportSize (Width = width, Height = height), HasTouch = true))

/// A case on a touch screen, on the shell's page.
let private editorCaseOnTouch (width: int) (height: int) = editorCaseOn HarnessOn.Shell (touchScreen width height)

/// A case about a fixture, on a touch screen.
let private fixtureCaseOnTouch (width: int) (height: int) = editorCaseOn HarnessOn.Fixtures (touchScreen width height)

/// Reading why a credential stopped working leaves the button that fixes it where it was.
/// The prompt used to be one wrapping row, so its button sat under a closed reason and beside
/// an open one — the control a person came for moved the moment they read the reason. Measured
/// against the prompt's own box, so the page scrolling under it is not a move; opened by a
/// real click on the summary, because the browser's own toggle is what grows the reason.
let private signInButtonHoldsStill (width: int) (height: int) =
    editorCaseIn width height (sprintf "opening a sign-in prompt's reason does not move its button at %dpx" width) <| fun page ->
        async {
            do! Harness.run page HarnessPage.signInLost
            do! waitVisible "the sign-in prompt's button" page "#shell [data-signin-required] [data-signin-again]"
            // Settled first: the side panes slide into place as the shell lays out, and the
            // column between them — the prompt's width — is still changing until they land.
            do! Probe.settled page
            let prompt = "#shell [data-signin-required]"
            // The button's offset and size in the prompt, scrolled to the prompt's top so the
            // page moving under it is not a move.
            let at () =
                async {
                    do! Probe.reveal page prompt
                    match! Probe.boxes page [ prompt; prompt + " [data-signin-again]" ] with
                    | [ Some p; Some b ] ->
                        return
                            [ b.Left - p.Left; b.Top - p.Top; Yession.App.Probes.Box.width b; Yession.App.Probes.Box.height b ]
                            |> List.map (fun v -> int (Math.Round v))
                    | measured -> return failwithf "the prompt and its button were not both there: %A" measured
                }
            let! closed = at ()
            do! awaitU (page.ClickAsync "#shell [data-signin-required] [data-detail] summary")
            do! waitVisible "the prompt's reason, opened" page "#shell [data-signin-required] [data-detail][open]"
            do! Probe.settled page
            let! opened = at ()
            Expect.equal opened closed "the button's offset and size in the prompt (left,top,width,height)"
        }

/// Where a mounted player's terminal is against the box it was mounted into and against its own
/// control bar — the host, `.ap-term` (the terminal as the player drew it) and `.ap-control-bar`,
/// measured in one frame — and the terminal's bottom border, which is its own ground.
type private ReplayBoxes =
    { Host: Yession.App.Probes.Box option
      Term: Yession.App.Probes.Box option
      Bar: Yession.App.Probes.Box option
      /// `None` where there is no terminal, or its border is not a length in pixels — which no
      /// fit can be judged against.
      TermBorderBottom: float option }

let private replayBoxes (page: IPage) (mount: string) () : Async<ReplayBoxes> =
    async {
        let term = mount + " .ap-term"
        let! measured = Probe.boxes page [ mount; term; mount + " .ap-control-bar" ]
        let! border = Probe.computed page term "border-bottom-width"
        let border =
            border
            |> Option.filter (fun b -> b.EndsWith "px")
            |> Option.bind (fun b ->
                match Double.TryParse (b.Substring (0, b.Length - 2), Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture) with
                | true, px -> Some px
                | false, _ -> None)
        return
            match measured with
            | [ host; term; bar ] -> { Host = host; Term = term; Bar = bar; TermBorderBottom = border }
            | other -> failwithf "asked for three boxes and was given %d" (List.length other)
    }

/// Whether every line and every column of a mounted player's terminal is where a reader can
/// see it: inside the box it was mounted into, and its LINES clear of the player's own control
/// bar. `.ap-term` is the terminal as the player drew it, so this is the drawn size, not the one
/// the player computed — and the two part company exactly when the player measured its
/// character cell wrong, which shows as columns running off the side and the last line sliding
/// under the bar. Its lines, not its box: the box ends in a margin of the terminal's own ground
/// (its bottom border), which the bar's top rule overlaps by design.
let private replayFits (boxes: ReplayBoxes) : bool =
    match boxes.Host, boxes.Term with
    | Some h, Some t ->
        not (Yession.App.Probes.Box.isEmpty t)
        && t.Top >= h.Top - 0.5 && t.Bottom <= h.Bottom + 0.5
        && t.Left >= h.Left - 0.5 && t.Right <= h.Right + 0.5
        && (match boxes.Bar with
            | Some bar ->
                boxes.TermBorderBottom |> Option.exists (fun border -> t.Bottom - border <= bar.Top + 0.5)
            | None -> true)
    | _ -> false

/// Wait for a mounted player to fit its box. A red one says where each of them was.
let private replayFitsWithin (what: string) (page: IPage) (mount: string) : Async<unit> =
    Wait.until what (replayBoxes page mount) replayFits

/// The harness's live terminal, rewound. Its recording is TALL and narrow (40x60, the shape a
/// terminal opened on a phone has), which is the one a panel runs out of HEIGHT for before
/// width — an 80x24 recording fits a pane either way, so it could not show a player sized to
/// the width alone pushing its last lines, and its control bar, out of the bottom of the panel.
let private rewoundTall (page: IPage) : Async<unit> =
    async {
        do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
        do! awaitU (page.ClickAsync "#shell [data-terminal-tab='term-live']")
        do! waitVisible "the live terminal's screen" page "#shell [data-terminal-screen='term-live']"
        do! awaitU (page.ClickAsync "#shell [data-terminal-watch='watch']")
        // Landed: the still at the pin is up, so the player has its recording's size.
        do! waitTextIn "the rewound terminal to show the screen at its pin" page "#shell [data-pane-replay='terminal:term-live']" "earlier output"
    }

let [<Literal>] private rewoundMount = "#shell [data-pane-replay='terminal:term-live']"

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
            // The time elapsed, as said and as painted, and how much of the bar is filled —
            // read off the transform the player scales it by.
            let read () =
                async {
                    let! elapsed = Probe.seen page (rewoundMount + " .ap-time-elapsed")
                    let! style = Read.attr page (rewoundMount + " .ap-gutter-full") "style"
                    let fraction =
                        style
                        |> Option.bind (fun style ->
                            let scaled = Text.RegularExpressions.Regex.Match (style, @"scaleX\(([0-9.]+)\)")
                            if scaled.Success then Some (float scaled.Groups.[1].Value) else None)
                    let seconds =
                        elapsed
                        |> List.tryHead
                        |> Option.bind (fun e ->
                            match e.Text.Split ':' |> Array.map Int32.TryParse with
                            | [| true, m; true, s |] -> Some (m * 60 + s)
                            | _ -> None)
                    return {| Shown = elapsed |> List.exists (fun e -> e.Painted); Seconds = seconds; Fraction = fraction |}
                }
            do! Wait.until "the rewound player's timer to be on screen, past the start and short of the end"
                    read
                    (fun r ->
                        r.Shown
                        && r.Seconds |> Option.exists (fun s -> s > 0)
                        && r.Fraction |> Option.exists (fun f -> f > 0.0 && f < 1.0))
        }

/// What is painted at the centre of a player's control bar and of each of its buttons, and
/// how opaque each is — `[]` when there is no bar. A failing case prints it, so a red run says
/// WHICH control was covered rather than only that a wait ran out.
let private replayControls (page: IPage) (mount: string) () : Async<(Yession.App.Probes.Seen * string) list> =
    async {
        let controls = sprintf "%s .ap-control-bar, %s .ap-control-bar button" mount mount
        let! seen = Probe.seen page controls
        let! opacity = Probe.computedAll page controls "opacity"
        // Two looks; a control that came or went between them is read as not there yet.
        return if List.length seen = List.length opacity then List.zip seen opacity else []
    }

/// A rewound terminal's transport — play and pause, the timeline, the player's own buttons —
/// is on show, and what is painted at the centre of the bar and of each of its buttons is that
/// control. Measured, not looked for: the bar was in the document all along, at no opacity
/// until a mouse moved over it, and under the command row where no mouse could reach.
let private rewoundReplayControlsInReach (width: int) (height: int) =
    editorCaseIn width height (sprintf "a rewound terminal's player controls are on show and in reach at %dx%d" width height) <| fun page ->
        async {
            do! rewoundTall page
            do! Wait.until "the rewound player's controls to be on show, each painted where it stands"
                    (replayControls page rewoundMount)
                    (fun controls ->
                        List.length controls > 1
                        && controls |> List.forall (fun (seen, opacity) -> seen.Painted && opacity = "1"))
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
            do! waitVisible "the shell's composer" page "#shell [data-draft-editor]"
            // Settled first, so what is counted is what the toggle started. A pulse or a blink
            // never finishes and is not waited for.
            do! Probe.settled page
            let! moved = Probe.transitionsOnToggle page rootClass
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
let private groundSpare (page: IPage) : Async<float> =
    async {
        let port = "#shell [data-conversation]"
        let! scroll = Probe.scroll page port
        let! item = Probe.boxes page [ port + " [data-message-id='msg-filler-8']" ]
        match scroll, item with
        | Some port, [ Some item ] -> return port.ClientWidth - Yession.App.Probes.Box.width item
        | _ -> return failwith "the conversation or its eighth message is not there to measure"
    }

/// Wait for the "jump to latest" control to be on the screen — hit-tested at its own centre,
/// so a control that is present, shown and buried under something else does not count. The
/// chat's is not in the document at all until it is offered.
let private jumpShown (page: IPage) =
    Wait.until "the way back to the latest message to be on screen"
        (fun () -> Probe.seen page "#shell [data-jump-to-latest=\"chat\"] button")
        (List.tryHead >> Option.exists (fun control -> control.Painted))

/// Scroll the conversation back to its start, which is far enough from the latest message
/// for the control to be offered, and wait for it.
let private scrolledAwayFromLatest (page: IPage) =
    async {
        do! waitVisible "a message in the conversation" page "#shell [data-conversation] [data-message-body]"
        // The harness page is taller than the viewport, so the composer — and the tray on its
        // top edge, where the control is offered — is brought into view first; then the
        // conversation's scroll goes back to the start.
        let! _ = Probe.scrollTo page "#shell [data-conversation]" 0.0
        do! waitPresent "the way back to the latest message to be offered" page "#shell [data-jump-to-latest=\"chat\"] button"
        let! _ = Probe.revealEnd page "#shell [data-draft-editor]"
        do! jumpShown page
    }

/// The way to stop a turn and the way back to the latest message, each that is missing or
/// whose box overlaps the conversation's — what neither may do, since the conversation is
/// where the words are and a control drawn over it hides whatever line scrolls under it.
/// Found by their own hooks, wherever a redesign puts them; a missing one is reported, so
/// the case cannot pass by measuring nothing.
let private controlsOverConversation (page: IPage) : Async<string> =
    async {
        let controls = [ "interrupt", "#shell [data-interrupt-turn]"; "jump", "#shell [data-jump-to-latest=\"chat\"] button" ]
        match! Probe.boxes page ("#shell [data-conversation]" :: List.map snd controls) with
        | Some c :: placed ->
            return
                List.zip (List.map fst controls) placed
                |> List.collect (fun (name, box) ->
                    match box with
                    | None -> [ name + " missing" ]
                    | Some r when r.Top < c.Bottom - 0.5 && r.Bottom > c.Top + 0.5 && r.Left < c.Right && r.Right > c.Left ->
                        [ sprintf "%s spans %g..%g over the conversation ending at %g" name r.Top r.Bottom c.Bottom ]
                    | Some _ -> [])
                |> String.concat "; "
        | _ -> return "the conversation missing"
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
    Probe.frames page 2

/// Bring a surface's box on screen and put its reader at its end, the way a reader does it,
/// and say whether there was anywhere else to be — a surface that fits its box is at its end
/// whatever happens, and a case over one could not fail.
let private toTheEnd (page: IPage) (key: string) : Async<unit> =
    async {
        let! parked = Probe.park page (tailSurface key) true
        let scrolls = parked |> Option.exists (fun s -> s.Height > s.ClientHeight + 100.0)
        Expect.isTrue scrolls (sprintf "the %s surface is taller than its box, so it has an end to keep" key)
        do! twoFrames page
    }

/// Put a surface's reader at its START, as a reader who scrolled back to read does.
let private toTheStart (page: IPage) (key: string) : Async<unit> =
    async {
        let! _ = Probe.park page (tailSurface key) false
        do! twoFrames page
    }

/// Where the newest words in a surface are: the LAST occurrence of `mark` in its text, whether
/// that line lies wholly inside the surface's box, and whether what is painted at its centre
/// belongs to the surface — so a line under the composer, a sticky header or a float reads as
/// not shown. With how far short of its end the surface stands, for a red to say by how much.
let private newestShown (page: IPage) (key: string) (mark: string) : Async<Yession.App.Probes.Newest> =
    async {
        do! waitTextIn (sprintf "%s in the %s surface" mark key) page (tailSurface key) mark
        do! twoFrames page
        match! Probe.newest page (tailSurface key) mark with
        | Some newest -> return newest
        | None -> return failwithf "%s went from the %s surface as it was read" mark key
    }

/// Whether the newest words are wholly on screen, as `newestShown` read them.
let private shownFully (newest: Yession.App.Probes.Newest) = newest.Inside && newest.Painted

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

/// The agent asks for a command at the end of the conversation, and the command then starts:
/// the call's chip, then its block's, measured in the same box both times.
let private commandChipHolds (page: IPage) : Async<unit> =
    async {
        let expect r = Result.defaultWith failwith r
        let call = Yession.Domain.ToolUseId.create "tool-command" |> expect
        let callChip = "#shell [data-chat-command-call='tool-command']"
        let blockChip = "#shell [data-chat-block='block-command']"
        let rectOf (selector: string) =
            async {
                match! Probe.boxes page [ selector ] with
                | [ Some b ] ->
                    return
                        [ b.Left; b.Top; Yession.App.Probes.Box.width b; Yession.App.Probes.Box.height b ]
                        |> List.map System.Math.Round
                | _ -> return failwithf "nothing matches %s to measure" selector
            }
        do! waitVisible "a message in the conversation" page "#shell [data-conversation] [data-message-body]"
        do! foldHarness page [
                400L,
                Yession.Domain.SessionEvent.ToolUseStarted
                    { Yession.Domain.Tools.ToolUseStarted.ToolUseId = call
                      Yession.Domain.Tools.ToolUseStarted.AgentTurnId = Yession.Domain.AgentTurnId.create "turn-command" |> expect
                      Yession.Domain.Tools.ToolUseStarted.Namespace = Yession.Domain.Tools.SessionTool.Namespace
                      Yession.Domain.Tools.ToolUseStarted.Name = Yession.Domain.Tools.SessionTool.ExecuteCommand
                      Yession.Domain.Tools.ToolUseStarted.Arguments =
                        Some """{"command":"dotnet fsi tasks.fsx build","description":"built the app"}""" } ]
        do! waitVisible "the call's chip in the chat" page callChip
        let! revealed = Probe.ask page Yession.App.Probes.reveal (callChip, "center")
        Expect.isTrue revealed "the call's chip is there to bring into view"
        let! before = rectOf callChip
        do! foldHarness page [
                401L,
                Yession.Domain.SessionEvent.TerminalBlockStarted
                    { Yession.Domain.Terminals.TerminalBlockStarted.TerminalId = harnessTerminal "term-harness"
                      Yession.Domain.Terminals.TerminalBlockStarted.BlockId = Yession.Domain.BlockId.create "block-command" |> expect
                      Yession.Domain.Terminals.TerminalBlockStarted.QueueId = None
                      Yession.Domain.Terminals.TerminalBlockStarted.Authority =
                          Yession.Domain.Authority.agentFor (Yession.Domain.Principal.Peer (Yession.Domain.PeerId.create "ada" |> expect))
                      Yession.Domain.Terminals.TerminalBlockStarted.Command = "dotnet fsi tasks.fsx build"
                      Yession.Domain.Terminals.TerminalBlockStarted.FromSeq = 3
                      Yession.Domain.Terminals.TerminalBlockStarted.Background = false
                      Yession.Domain.Terminals.TerminalBlockStarted.Description = Some "built the app"
                      Yession.Domain.Terminals.TerminalBlockStarted.ToolUseId = Some call } ]
        do! waitVisible "the block's chip in the chat, in place of the call's" page blockChip
        do! waitAbsent "the call's own chip to be gone" page callChip
        let! after = rectOf blockChip
        Expect.equal after before "the block's chip is the call's chip's box, not one moved below it"
    }

/// The pane open on the harness's block-mode terminal, finished opening — its width
/// transitions from nothing, and until it has, its scrollback is clipped by the column.
let private paneOnBlocks (page: IPage) : Async<unit> =
    async {
        do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
        do! waitVisible "the harness terminal's blocks" page (tailSurface "blocks:term-harness")
        do! Wait.until "the column to finish opening"
                (fun () -> Probe.animating page "#shell [data-content-panel]") ((=) (Some 0))
    }

/// `<prefix> 1` to `<prefix> n`, one to a line.
let private numberedLines (prefix: string) (count: int) : string =
    [ for i in 1 .. count -> sprintf "%s %d" prefix i ] |> String.concat "\r\n"

/// `term-harness` stops being able to run commands while a person's build runs there and the
/// agent's command waits in its queue: the build's start, the loss, and the session's refusal
/// of the queued command, as the session records them — folded in as they would arrive — and
/// the queue entry the session takes out of the doc with that refusal.
let private harnessReplaced (page: IPage) : Async<unit> =
    let at = DateTimeOffset (2026, 10, 12, 11, 8, 0, TimeSpan.Zero)
    let expect r = Result.defaultWith failwith r
    let queued = Yession.Domain.QueueId.create "queue-harness" |> expect
    let agent =
        Yession.Domain.Authority.agentFor (Yession.Domain.Principal.Peer (Yession.Domain.PeerId.create "ada" |> expect))
    let openBlock = Yession.Domain.BlockId.create "block-open" |> expect
    async {
      do!
        foldHarness
          page
          [ 899L,
            Yession.Domain.SessionEvent.TerminalBlockStarted
                { TerminalId = harnessTerminal "term-harness"
                  BlockId = openBlock
                  QueueId = None
                  Authority = Yession.Domain.Authority.ofAuthor (Yession.Domain.Principal.Peer (Yession.Domain.PeerId.create "ada" |> expect))
                  Command = "dotnet fsi tasks.fsx build 2>&1 | tail -30"
                  FromSeq = 0
                  Background = false
                  Description = None
                  ToolUseId = None }
            900L,
            Yession.Domain.SessionEvent.TerminalIntegrationLost
                { TerminalId = harnessTerminal "term-harness"
                  BlockId = Some openBlock
                  Evidence = Some { WrittenAt = at; Due = at.AddSeconds 2.0; Said = "sh-5.2$ " }
                  Refused = [ { QueueId = queued; Authority = agent } ] }
            901L,
            Yession.Domain.SessionEvent.TerminalCommandRejected
                { TerminalId = harnessTerminal "term-harness"
                  QueueId = queued
                  BlockId = Yession.Domain.BlockId.create "block-refused" |> expect
                  Authority = agent
                  RejectedBy = Yession.Domain.ActorRef.Session
                  Command = "npm run lint"
                  Description = Some "Inspect lint and check logs"
                  Reason = Some Yession.Session.TerminalQueueDrain.cannotRunReason
                  ToolUseId = None } ]
      do! Harness.run1 page HarnessPage.withdraw "queue-harness"
    }

/// How many elements matching `selector` a person can SEE: on the page, scrolled to, and the
/// thing painted at their centre theirs. Not the count in the markup — a mount clipped to
/// nothing still measures, and a duplicate in a shut column still matches.
let private visibleCount (page: IPage) (selector: string) : Async<int> =
    async {
        // Brought to the middle of what scrolls it first: one clipped below the fold is still
        // something a person would see by scrolling, and `visible` only looks at the window.
        let! _ = Probe.ask page Yession.App.Probes.reveal (selector, "center")
        return! Probe.visible page selector
    }

/// The harness terminal's blocks made taller than their box: a running command's three hundred
/// lines, and every fold in the history open, so the lines are on the page to be scrolled past.
let private blocksFilled (page: IPage) : Async<unit> =
    async {
        do! Harness.run4 page HarnessPage.record "term-harness" 2 "o" (numberedLines "line" 300 + "\r\n")
        let! _ = Probe.openAll page "#shell [data-tail='blocks:term-harness'] details"
        do! twoFrames page
    }

/// Fifty records of live output into the harness terminal, one message each and all in one
/// turn, as a burst off the data channel lands — then the frame after them, and what `andThen`
/// reads off the page once it is there. The frame waited for is registered after every render
/// the burst asked for, so whatever the burst was owed has been drawn by the time it runs.
let private afterBurst (page: IPage) : Async<int * bool> =
    async {
        match! Probe.reading page Yession.App.Probes.Measure.Burst with
        | [ renders; shown ] -> return int renders, shown = 1.0
        | other -> return failwithf "the burst read %A, not a count and a verdict" other
    }

/// Whether the "jump to latest" over a surface is on screen and is what is painted at its own
/// centre, so a control that is present, shown and buried does not count.
let private jumpOffered (page: IPage) (key: string) : Async<bool> =
    async {
        let! control = Probe.seen page (sprintf "#shell [data-jump-to-latest='%s'] button" key)
        return control |> List.tryHead |> Option.exists (fun c -> c.Painted)
    }

/// Whether a surface's reader is at its end.
let private atItsEnd (page: IPage) (key: string) : Async<bool> =
    async {
        let! scroll = Probe.scroll page (tailSurface key)
        return scroll |> Option.exists (fun s -> s.Top + s.ClientHeight >= s.Height - 4.0)
    }

/// A long conversation (the scroll scenario's, so the harness counts the renders from here)
/// with its reader more than a screen back from the end — the way to the latest offered in the
/// composer's tray — and then the viewport shortened, as a keyboard shortens it, until the
/// column has less room than one item and is not drawn (`too-short`, app/tailwind.css).
/// Answers where the reader's scroll stood before the room went. Not the start: the start is
/// 0, and 0 is also where a lost scroll lands.
let private columnTooShort (page: IPage) : Async<float> =
    async {
        do! Harness.run4 page HarnessPage.benchScrollBegin 80 0 16 true
        do! twoFrames page
        let chat = tailSurface "chat"
        let! scroll = Probe.scroll page chat
        // Two screens short of the end, which is more than the one that counts as far.
        let top =
            match scroll with
            | Some s -> s.Height - 3.0 * s.ClientHeight
            | None -> failwith "there is no conversation to scroll"
        Expect.isTrue (top > 0.0) "the conversation is long enough to stand more than a screen back in"
        let! _ = Probe.scrollTo page chat top
        do! twoFrames page
        do! waitPresent "the way back to the latest message to be offered" page "#shell [data-jump-to-latest=\"chat\"] button"
        let! was = Probe.scroll page chat
        let was =
            match was with
            | Some s -> s.Top
            | None -> failwith "the conversation went as it was read"
        // On a phone the header, the composer and the tray leave the column under one item
        // (76px) at this height.
        do! awaitU (page.SetViewportSizeAsync (390, 240))
        do! Wait.until "the column to have less room than one item" (fun () -> Probe.scroll page chat) (Option.exists (fun s -> s.ClientHeight < 76.0))
        do! twoFrames page
        return was
    }

/// Show the pane and wait until it stands at the width the separator says — the column animates
/// open, and a measurement taken while it travels is of a width it was for one frame.
let private paneOpenAndSettled (page: IPage) : Async<unit> =
    async {
        do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
        do! Wait.until "the pane to stand at the width its separator says"
                (fun () ->
                    async {
                        let! pane = Probe.boxes page [ "#shell [data-content-panel]" ]
                        let! said = Read.attr page "#shell [data-term-resize]" "aria-valuenow"
                        return pane, said
                    })
                (fun (pane, said) ->
                    match pane, said |> Option.map Double.TryParse with
                    | [ Some pane ], Some (true, said) -> abs (Yession.App.Probes.Box.width pane - said) <= 1.0
                    | _ -> false)
    }

/// On a desktop with room to spare, a pane nobody sized takes what the chat's reading column
/// leaves: the conversation keeps its full measure, the pane is never under its floor, and the
/// only strip between them is the chat's own gutter. Read as ONE layout — three numbers that
/// are the same promise, so a red one prints all three.
let private paneTakesTheRoom (page: IPage) : Async<unit> =
    async {
        do! paneOpenAndSettled page
        let chat = "#shell [data-conversation]"
        let groups = chat + " [data-message-author]"
        let! pane = Probe.boxes page [ "#shell [data-content-panel]" ]
        let! grouped = Probe.seen page groups
        let! measure = Probe.computed page groups "max-width"
        let! gutter = Probe.computed page chat "padding-right"
        let px (value: string option) =
            value
            |> Option.filter (fun v -> v.EndsWith "px")
            |> Option.map (fun v -> float (v.Substring (0, v.Length - 2)))
        match pane, grouped, px measure, px gutter with
        | [ Some pane ], (_ :: _), Some measure, Some gutter ->
            let right = grouped |> List.map (fun g -> g.Box.Right) |> List.max
            let column = grouped |> List.map (fun g -> Yession.App.Probes.Box.width g.Box) |> List.max
            let gap = pane.Left - right
            let layout = {| Column = column; Measure = measure; Gap = gap; Gutter = gutter; Pane = Yession.App.Probes.Box.width pane |}
            Expect.isTrue
                (column >= measure - 1.0 && gap <= gutter + 1.0 && layout.Pane >= 420.0)
                (sprintf "the chat keeps its measure, the pane takes the rest and is never under its floor: %A" layout)
        | _ -> failwithf "the pane, the messages, their measure and the gutter were not all there: %A" (pane, grouped, measure, gutter)
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
    async {
        let! runs = Read.attrs page (tailSurface "blocks:term-harness" + " [data-terminal-block-run]") "data-terminal-block-run"
        return runs |> List.choose id |> List.tryLast |> Option.defaultWith (fun () -> failwith "the history holds no fold")
    }

/// A fold's line, by the id the run is keyed by.
let private runLine (run: string) =
    sprintf "%s [data-terminal-block-run='%s'] summary" (tailSurface "blocks:term-harness") run

/// Where the latest command's line stands on screen — the last one drawn, since everything a
/// fold holds came before it.
let private latestCommandTop (page: IPage) : Async<float> =
    async {
        let! lines = Probe.seen page (tailSurface "blocks:term-harness" + " [data-terminal-block-command]")
        match List.tryLast lines with
        | Some line -> return line.Box.Top
        | None -> return failwith "the history draws no command"
    }

/// Whether a fold's line lies wholly inside the history's box: the precondition for a case
/// about a reader who presses it from where they are, since one off screen is a press nobody
/// at the end makes (and the browser's own anchoring keeps the place for it anyway).
let private runLineOnScreen (page: IPage) (run: string) : Async<bool> =
    async {
        match! Probe.boxes page [ tailSurface "blocks:term-harness"; runLine run ] with
        | [ Some box; Some at ] -> return at.Top >= box.Top && at.Bottom <= box.Bottom
        | measured -> return failwithf "the history and the fold's line were not both there: %A" measured
    }

/// A reader at the end of the harness terminal's history, on a page tall enough that the newest
/// fold's line is on screen there — the cases about pressing it from the end mean nothing with it
/// off screen, and how far above the end it sits is the history's styling (each command's byline,
/// the gaps between commands, a Stop strip), not what those cases are about. Grown by what it
/// lacks, once, so a case reads the same at whatever height the history's rhythm asks for.
let private atTheEndWithRunOnScreen (page: IPage) : Async<string> =
    async {
        do! toTheEnd page "blocks:term-harness"
        let! run = newestRun page
        match! Probe.boxes page [ tailSurface "blocks:term-harness"; runLine run ] with
        | [ Some box; Some at ] when at.Top < box.Top ->
            let size = page.ViewportSize
            do! awaitU (page.SetViewportSizeAsync (size.Width, size.Height + int (ceil (box.Top - at.Top)) + 48))
            do! toTheEnd page "blocks:term-harness"
        | _ -> ()
        return run
    }

/// Press a fold one way or another, and wait for the press to have been drawn — open if it was
/// shut, shut if it was open — and the frame after.
let private pressRunBy (press: IPage -> string -> Async<unit>) (page: IPage) (run: string) : Async<unit> =
    async {
        let details = sprintf "%s [data-terminal-block-run='%s'] details" (tailSurface "blocks:term-harness") run
        let isOpen () =
            async {
                let! opened = Read.attr page details "open"
                return Option.isSome opened
            }
        let! was = isOpen ()
        do! press page (runLine run)
        do! Wait.until "the fold to have turned" isOpen ((<>) was)
        do! twoFrames page
    }

/// A pointer's press on whatever is painted at the middle of `selector`, where it stands — not
/// the locator's click, which first scrolls its target to wherever it judges clear of a sticky
/// line, and so moves the very place a case is measuring.
///
/// Refused when the target is not what is painted at its middle, which is the one check of the
/// locator's that this keeps: a middle below the screen, or under something else, is a press
/// that lands nowhere and reads as a press the page ignored (the sheet's case, "pressing the
/// pane under the list's sheet", says how a point measured from a box came to be off screen).
let private clickWhereItIs (page: IPage) (selector: string) : Async<unit> =
    async {
        match! Probe.seen page selector with
        | seen :: _ when seen.Painted ->
            let r = seen.Box
            do! awaitU (page.Mouse.ClickAsync (float32 (r.Left + Yession.App.Probes.Box.width r / 2.0), float32 (r.Top + Yession.App.Probes.Box.height r / 2.0)))
        | seen :: _ -> return failwithf "%s is not what is painted at its middle (%A), so a press there would land on something else" selector seen.Box
        | [] -> return failwithf "nothing at %s to press" selector
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
        let surface = tailSurface "blocks:term-harness"
        let! scroll = Probe.scroll page surface
        match scroll with
        | None -> return failwith "the history is not there to scroll"
        | Some scroll ->
            match! Probe.boxes page [ surface; selector ] with
            | [ Some el; Some target ] ->
                let! _ = Probe.scrollTo page surface (scroll.Top + target.Top - el.Top - 120.0)
                ()
            | measured -> return failwithf "the history and %s were not both there: %A" selector measured
        do! twoFrames page
    }

/// Where something stands on screen: the top of its box.
let private topOf (page: IPage) (selector: string) : Async<float> =
    async {
        match! Probe.boxes page [ selector ] with
        | [ Some r ] -> return r.Top
        | _ -> return failwithf "nothing at %s to measure" selector
    }

/// A box as whole pixels, rounded half up as JavaScript's `Math.round` rounds.
let private px (value: float) : int = int (floor (value + 0.5))

/// The first match of each selector, measured in one frame, every one of them required.
let private boxesOf (page: IPage) (selectors: string list) : Async<Yession.App.Probes.Box list> =
    async {
        let! measured = Probe.boxes page selectors
        return
            List.zip selectors measured
            |> List.map (fun (selector, box) ->
                box |> Option.defaultWith (fun () -> failwithf "nothing matches %s to measure" selector))
    }

/// The width the pane's separator says the pane is (`aria-valuenow`).
let private separatorValue (page: IPage) : Async<float> =
    async {
        match! Read.attr page "#shell [data-term-resize]" "aria-valuenow" with
        | Some said -> return float said
        | None -> return failwith "the separator says no width"
    }

/// The pane's width as drawn.
let private paneWidth (page: IPage) : Async<float> =
    async {
        match! boxesOf page [ "#shell [data-content-panel]" ] with
        | [ pane ] -> return Yession.App.Probes.Box.width pane
        | _ -> return failwith "unreachable: one selector, one box"
    }

/// Whether the pane's drawn width has caught up with what its separator says.
let private paneMeetsSeparator (page: IPage) () : Async<bool> =
    async {
        let! said = separatorValue page
        let! width = paneWidth page
        return abs (width - said) <= 1.0
    }

/// An attribute of whatever has focus; `None` when nothing does, or it lacks the attribute.
let private focusedAttr (page: IPage) (name: string) : Async<string option> = Read.attr page ":focus" name

/// Whether an element is `inert`: out of the keyboard's and the pointer's reach.
let private inert (page: IPage) (selector: string) : Async<bool> =
    async {
        let! held = Read.attr page selector "inert"
        return Option.isSome held
    }

/// The pane's box and the window it is laid out in, read together.
let private paneAndWindow (page: IPage) () : Async<Yession.App.Probes.Box option * Yession.App.Probes.Window> =
    async {
        let! pane = Probe.boxes page [ "#shell [data-content-panel]" ]
        let! window = Probe.window page
        return List.tryHead pane |> Option.flatten, window
    }

/// Wait for the pane to stand off the right of the screen, as a shut one does on a phone.
let private paneOffScreenAs (what: string) (page: IPage) : Async<unit> =
    Wait.until what (paneAndWindow page) (fun (pane, window) ->
        pane |> Option.exists (fun pane -> pane.Left >= window.InnerWidth - 1.0))

let private paneOffScreen (page: IPage) = paneOffScreenAs "the pane to stand off the screen" page

/// Wait for the pane to take the column: flush with the screen's right edge, over its left half.
let private paneCovers (what: string) (page: IPage) : Async<unit> =
    Wait.until what (paneAndWindow page) (fun (pane, window) ->
        pane |> Option.exists (fun pane -> abs (pane.Right - window.InnerWidth) <= 1.0 && pane.Left < window.InnerWidth / 2.0))

/// Whether the document is wider than its window: a sideways overflow a phone cannot scroll away.
let private overflowsSideways (page: IPage) : Async<bool> =
    async {
        let! window = Probe.window page
        return window.DocumentWidth > window.InnerWidth + 1.0
    }

/// How wide the page's window is, as the page itself measures it.
let private windowWidth (page: IPage) : Async<int> =
    async {
        let! window = Probe.window page
        return int window.InnerWidth
    }

let editorTests =
    testList "Editor rendering (browser)" [
        fixtureCase "Markdown typed in the rich editor renders formatted and round-trips to Markdown" <| fun page ->
            async {
                do! waitVisible "the editor" page ".ProseMirror"

                // Type Markdown with REAL key events so the input rules fire: "# " turns the
                // block into a heading rendered live as <h1> — the syntax is never left literal.
                do! awaitU (page.ClickAsync ".ProseMirror")
                do! awaitU (page.Keyboard.TypeAsync "# Heading one")
                do! Wait.until ".ProseMirror h1 to say exactly Heading one" (fun () -> Read.text page ".ProseMirror h1") ((=) (Some "Heading one"))
                // `**bold**` -> a <strong> mark; `- ` -> a bullet list <ul><li>. The new line is
                // plain Enter here because the harness mounts the editor as the COMPOSER does,
                // where Enter is the paragraph key and Ctrl+Enter sends (asserted below).
                do! awaitU (page.Keyboard.PressAsync "Enter")
                do! awaitU (page.Keyboard.TypeAsync "text with **bold** now")
                do! waitPresent ".ProseMirror strong to be on the page" page ".ProseMirror strong"
                do! awaitU (page.Keyboard.PressAsync "Enter")
                do! awaitU (page.Keyboard.TypeAsync "- item one")
                do! waitPresent ".ProseMirror ul li to be on the page" page ".ProseMirror ul li"

                // The document serializes back to Markdown (the durable form the drain snapshots).
                let! md = Harness.ask page HarnessPage.md
                Expect.stringContains md "# Heading one" "heading serialized to markdown"
                Expect.stringContains md "**bold**" "bold serialized to markdown"
                Expect.stringContains md "* item one" "bullet serialized to markdown"
            }

        // A block an input rule makes has to LOOK like that block while it is typed. The reset
        // strips every default a bare `<blockquote>`, `<pre>` or `<ul>` had, so a composer that
        // drew them bare turned `> ` into a quote nobody could see.
        fixtureCase "a quote typed in the composer draws its rule" <| fun page ->
            async {
                do! waitVisible "the editor" page ".ProseMirror"
                do! awaitU (page.ClickAsync ".ProseMirror")
                do! awaitU (page.Keyboard.TypeAsync "> quoted")
                do! waitPresent ".ProseMirror blockquote to be on the page" page "#host .ProseMirror blockquote"
                let! rule = Probe.computed page "#host .ProseMirror blockquote" "border-left-width"
                Expect.notEqual rule (Some "0px") "the quote draws a rule beside it"
            }

        // An outline and not a fill: the focused composer is already on the top grey, so a
        // filled block drew on its own colour and could not be seen.
        fixtureCase "a code block typed in the composer draws its outline" <| fun page ->
            async {
                do! waitVisible "the editor" page ".ProseMirror"
                do! awaitU (page.ClickAsync ".ProseMirror")
                do! awaitU (page.Keyboard.TypeAsync "```")
                do! awaitU (page.Keyboard.PressAsync "Enter")
                do! waitPresent ".ProseMirror pre to be on the page" page "#host .ProseMirror pre"
                let! outline = Probe.computed page "#host .ProseMirror pre" "border-top-width"
                Expect.equal outline (Some "1px") "the code block draws a hairline round it"
            }

        // A fence opens on Enter, as Markdown reads one, so the language after the backticks
        // can be typed and is kept.
        fixtureCase "a fence naming a language opens a code block in that language" <| fun page ->
            async {
                do! waitVisible "the editor" page ".ProseMirror"
                do! awaitU (page.ClickAsync ".ProseMirror")
                do! awaitU (page.Keyboard.TypeAsync "```fsharp")
                do! awaitU (page.Keyboard.PressAsync "Enter")
                do! waitPresent ".ProseMirror pre to be on the page" page "#host .ProseMirror pre"
                do! awaitU (page.Keyboard.TypeAsync "let x = 1")
                let! md = Harness.ask page HarnessPage.md
                Expect.equal (md.TrimEnd ()) "```fsharp\nlet x = 1\n```" "the block keeps its language"
            }

        // A code block at the end of a draft is not a place the caret can be stuck in.
        fixtureCase "Enter on the empty last line of a code block leaves it" <| fun page ->
            async {
                do! waitVisible "the editor" page ".ProseMirror"
                do! awaitU (page.ClickAsync ".ProseMirror")
                do! awaitU (page.Keyboard.TypeAsync "```")
                do! awaitU (page.Keyboard.PressAsync "Enter")
                do! waitPresent ".ProseMirror pre to be on the page" page "#host .ProseMirror pre"
                do! awaitU (page.Keyboard.TypeAsync "let x = 1")
                do! awaitU (page.Keyboard.PressAsync "Enter")
                do! awaitU (page.Keyboard.PressAsync "Enter")
                do! awaitU (page.Keyboard.TypeAsync "after")
                let! md = Harness.ask page HarnessPage.md
                Expect.equal (md.TrimEnd ()) "```\nlet x = 1\n```\n\nafter" "the line after is a paragraph, and the block lost the empty line"
            }

        fixtureCase "a bullet list typed in the composer draws its bullets" <| fun page ->
            async {
                do! waitVisible "the editor" page ".ProseMirror"
                do! awaitU (page.ClickAsync ".ProseMirror")
                do! awaitU (page.Keyboard.TypeAsync "- item")
                do! waitPresent ".ProseMirror ul to be on the page" page "#host .ProseMirror ul"
                let! marker = Probe.computed page "#host .ProseMirror ul" "list-style-type"
                Expect.equal marker (Some "disc") "the list draws a bullet per item"
            }

        // A rule that fires on a `>` nobody meant as a quote is one key to take back, and the
        // key gives the `>` back rather than deleting it with the quote.
        fixtureCase "Backspace right after a quote opens gives back the > typed" <| fun page ->
            async {
                do! waitVisible "the editor" page ".ProseMirror"
                do! awaitU (page.ClickAsync ".ProseMirror")
                do! awaitU (page.Keyboard.TypeAsync "> ")
                do! waitPresent ".ProseMirror blockquote to be on the page" page "#host .ProseMirror blockquote"
                do! awaitU (page.Keyboard.PressAsync "Backspace")
                do! waitAbsent ".ProseMirror blockquote to be gone" page "#host .ProseMirror blockquote"
                let! text = Read.text page "#host .ProseMirror p"
                Expect.equal (text |> Option.map (fun t -> t.Trim ())) (Some ">") "the > is back as text"
            }

        // Addressing somebody is reachable from the keyboard alone: an @ greys in the rest of a
        // name, and Tab takes it.
        fixtureCase "Tab completes the hinted address" <| fun page ->
            async {
                do! waitVisible "the editor" page ".ProseMirror"
                do! awaitU (page.ClickAsync ".ProseMirror")
                do! awaitU (page.Keyboard.TypeAsync "thanks @sw")
                do! waitVisible "the address the editor hints" page "[data-address-hint]"
                do! awaitU (page.Keyboard.PressAsync "Tab")
                let! md = Harness.ask page HarnessPage.md
                Expect.equal (md.TrimEnd ()) "thanks @swift-heron" "the hinted name was taken"
            }

        fixtureCase "each Tab moves to the next name on offer" <| fun page ->
            async {
                do! waitVisible "the editor" page ".ProseMirror"
                do! awaitU (page.ClickAsync ".ProseMirror")
                do! awaitU (page.Keyboard.TypeAsync "@")
                do! waitVisible "the address the editor hints" page "[data-address-hint]"
                do! awaitU (page.Keyboard.PressAsync "Tab")
                do! awaitU (page.Keyboard.PressAsync "Tab")
                let! md = Harness.ask page HarnessPage.md
                Expect.equal (md.TrimEnd ()) "@swift-heron" "the second press moved past the agent"
            }

        // For a keyboard where Tab is spoken for: the right arrow takes the hint too.
        fixtureCase "the right arrow completes the hinted address" <| fun page ->
            async {
                do! waitVisible "the editor" page ".ProseMirror"
                do! awaitU (page.ClickAsync ".ProseMirror")
                do! awaitU (page.Keyboard.TypeAsync "@ag")
                do! waitVisible "the address the editor hints" page "[data-address-hint]"
                do! awaitU (page.Keyboard.PressAsync "ArrowRight")
                let! md = Harness.ask page HarnessPage.md
                Expect.equal (md.TrimEnd ()) "@agent" "the arrow took the hint"
            }

        // Tab is taken only while there is something to complete: a composer that kept it
        // would be a keyboard trap.
        fixtureCase "Tab leaves the composer when nothing is hinted" <| fun page ->
            async {
                do! waitVisible "the editor" page ".ProseMirror"
                do! awaitU (page.ClickAsync ".ProseMirror")
                do! awaitU (page.Keyboard.TypeAsync "hello")
                do! awaitU (page.Keyboard.PressAsync "Tab")
                do! Wait.until "focus to have left the editor" (fun () -> Read.focused page "#host, #host *") not
            }

        fixtureCase "Ctrl+Enter sends, Shift+Enter breaks the line, Enter opens a paragraph" <| fun page ->
            async {
                do! waitVisible "the editor" page ".ProseMirror"

                do! awaitU (page.ClickAsync ".ProseMirror")
                do! awaitU (page.Keyboard.TypeAsync "first line")
                // Enter is a prose key now, not a send: it opens the PARAGRAPH a phone's
                // return key can reach with no modifier, and asks nothing to send. A binding
                // that both split the block AND sent would look right in a screenshot and
                // fire a half-written message on every return keypress.
                do! awaitU (page.Keyboard.PressAsync "Enter")
                do! awaitU (page.Keyboard.TypeAsync "second block")
                do! waitCount "exactly 2 of #host .ProseMirror > p" page "#host .ProseMirror > p" 2
                let! afterEnter = Harness.ask page HarnessPage.md
                Expect.stringContains afterEnter "first line" "the first block survived"
                Expect.stringContains afterEnter "second block" "Enter opened a second block"
                let! sendsAfterEnter = Harness.held page HarnessPage.sends
                Expect.equal sendsAfterEnter 0 "plain Enter did not send"

                // Shift+Enter breaks the LINE: a <br> inside the block it was already in, so
                // the paragraph count does not move. This is the half a single Enter could
                // never express, and it has to survive Markdown to be worth anything — the
                // serializer writes a trailing backslash and the parser reads it back.
                do! awaitU (page.Keyboard.PressAsync "Shift+Enter")
                do! awaitU (page.Keyboard.TypeAsync "same paragraph")
                do! waitPresent ".ProseMirror br to be on the page" page ".ProseMirror br"
                do! waitCount "exactly 2 of #host .ProseMirror > p" page "#host .ProseMirror > p" 2
                let! broken = Harness.ask page HarnessPage.md
                Expect.stringContains broken "second block" "the text before the break survived"
                Expect.stringContains broken "same paragraph" "and the text after it"

                // Ctrl+Enter (Cmd+Enter on macOS — `ControlOrMeta` picks the right one) is
                // where SEND went, and it leaves the document exactly as it was: a send that
                // also touched the text would look right in a screenshot and corrupt the
                // draft every time it fired.
                do! awaitU (page.Keyboard.PressAsync "ControlOrMeta+Enter")
                do! Wait.until "the send to have fired once" (fun () -> Harness.read page HarnessPage.sends) ((=) (Some 1))
                let! afterSend = Harness.ask page HarnessPage.md
                Expect.equal afterSend broken "Ctrl+Enter sent without touching the document"
            }

        // The prompt an empty composer shows is a node decoration: an attribute on the empty
        // paragraph that the stylesheet paints from, so it is never text anyone can select,
        // copy or send. The harness host is not a `[data-rich-body]`, so nothing paints here;
        // what is read is the hook the stylesheet reads.
        fixtureCase "an empty composer offers its prompt without it becoming content" <| fun page ->
            async {
                do! waitVisible "the empty composer's prompt" page "#host .ProseMirror [data-placeholder]"
                let! prompt = Read.attr page "#host .ProseMirror [data-placeholder]" "data-placeholder"
                let! text = Read.text page "#host .ProseMirror"
                Expect.equal prompt (Some Yession.App.Dom.Text.composerPlaceholder) "the empty composer carries its prompt"
                Expect.equal text (Some "") "and the prompt is not in the document"
            }

        fixtureCase "a composer written in stops offering its prompt" <| fun page ->
            async {
                do! waitVisible "the empty composer's prompt" page "#host .ProseMirror [data-placeholder]"
                do! awaitU (page.ClickAsync ".ProseMirror")
                do! awaitU (page.Keyboard.TypeAsync "x")
                do! waitAbsent "#host .ProseMirror [data-placeholder] to be gone" page "#host .ProseMirror [data-placeholder]"
                return ()
            }

        fixtureCase "a remote peer's selection renders as a caret widget, label, and highlight" <| fun page ->
            async {
                do! waitVisible "the editor" page ".ProseMirror"

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
                do! Harness.run1 page HarnessPage.pushRemote "remote-peer"
                do! waitPresent ".ProseMirror .pm-caret to be on the page" page ".ProseMirror .pm-caret"
                do! Wait.until "some .ProseMirror .pm-caret-label to say remote-peer" (fun () -> Read.texts page ".ProseMirror .pm-caret-label") (List.contains "remote-peer")
                // The selection highlight is an inline decoration carrying our translucent colour.
                do! waitPresent ".ProseMirror [style*='background-color'] to be on the page" page ".ProseMirror [style*=\"background-color\"]"
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
        fixtureCase "a caret drawn on every frame never costs the mirror its content" <| fun page ->
            async {
                do! waitPresent "both peers to mount" page "#peer-a .ProseMirror"
                do! waitPresent "both peers to mount" page "#peer-b .ProseMirror"

                // Carets first, and left running for the whole case: the push has to be in
                // flight WHILE the content arrives, which is the only arrangement in which it
                // can race anything. Started before a single keystroke so no frame of the
                // convergence happens un-stormed.
                do! Harness.run1 page HarnessPage.caretStorm true

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
                do! Wait.until "the caret storm to push frames at all" (fun () -> Harness.read page HarnessPage.caretPushes) (Option.exists (fun n -> n >= 5))
                let! stormedBefore = Harness.held page HarnessPage.caretPushes

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
                    do! waitOwnText "the co-editor to render the author's remote content" page "#peer-b .ProseMirror h1" "Heading one"
                with e ->
                    let! state = Harness.ask page HarnessPage.convState
                    return failwithf "%s\n  state: %s" e.Message state

                // ...and the caret really is drawn there, so this is a test of a caret over
                // content and not of an editor nobody decorated.
                do! waitPresent "the author's caret to be drawn in the mirror" page "#peer-b .pm-caret"

                // Anti-vacuity, second half, and the reason a green here means anything: a
                // storm that stopped before the words arrived converges beautifully. Frames
                // landing DURING the convergence are what raced it — waited for with the storm
                // still on, so the first one settles it, rather than counted against a window
                // that was only ever a guess at how fast this machine is.
                do! Wait.until
                        "the caret storm to push a frame while the content was arriving"
                        (fun () -> Harness.read page HarnessPage.caretPushes)
                        (Option.exists (fun n -> n > stormedBefore))

                do! Harness.run1 page HarnessPage.caretStorm false
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
        fixtureCase "drawing a remote caret writes nothing to the shared document" <| fun page ->
            async {
                do! waitPresent "both peers to mount" page "#peer-a .ProseMirror"
                do! waitPresent "both peers to mount" page "#peer-b .ProseMirror"
                do! Harness.run1 page HarnessPage.caretStorm true
                // The storm is dispatching before anything is typed, and keeps dispatching
                // while the words arrive — both waited for rather than counted at the end, for
                // the reason the case above gives: a push is one animation frame, so a count
                // inside a fixed window is a measurement of the machine.
                do! Wait.until "the caret storm to push frames at all" (fun () -> Harness.read page HarnessPage.caretPushes) (Option.exists (fun n -> n >= 5))
                let! stormedBefore = Harness.held page HarnessPage.caretPushes
                do! awaitU (page.ClickAsync "#peer-a .ProseMirror")
                do! awaitU (page.Keyboard.TypeAsync "# Heading one")
                // Over CONTENT: an empty document is the case y-prosemirror short-circuits
                // anyway (`initialContentChanged`), so a storm over nothing would prove nothing.
                do! waitOwnText "the co-editor to render the author's remote content" page "#peer-b .ProseMirror h1" "Heading one"
                do! Wait.until
                        "the caret storm to push a frame while the content was arriving"
                        (fun () -> Harness.read page HarnessPage.caretPushes)
                        (Option.exists (fun n -> n > stormedBefore))
                do! Harness.run1 page HarnessPage.caretStorm false

                let! pushes = Harness.held page HarnessPage.caretPushes
                // The doc really moved under the storm — otherwise a write-back count of zero
                // says the observer was never wired, not that nothing was written.
                let! updates = Harness.held page HarnessPage.docUpdates
                if updates < 1 then
                    failwith "the co-editor's doc took no updates at all, so a zero write-back count means the counter is broken, not that a caret is harmless"
                let! writebacks = Harness.held page HarnessPage.writebacks
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
        fixtureCase "a recorded terminal replays in a real player, and prints what it printed" <| fun page ->
            async {
                // The player took the mount and built its own DOM there.
                do! waitVisible "the player to take its mount" page "#replay .ap-player"
                // …with the transport controls that ARE the audit-read affordance: a replay
                // you cannot pause or seek is a video of a terminal, not a record of one.
                do! waitVisible "the player's transport controls" page "#replay .ap-control-bar"
                // …and the chapter marks (Plan 14, stage 4), which are what make a
                // whole-terminal recording navigable by what ran in it. Asserted here
                // because a marker option the player silently ignored would leave every
                // DOM-free test green and the chapters absent.

                // Then play it, and wait for the recording's own output to appear on the
                // screen. This is the assertion that spans the whole stage: bytes the Session
                // wrote, encoded as asciicast, rebuilt by `TranscriptReplay.cast`,
                // and rendered by the player.
                do! waitVisible "the player's start button" page "#replay .ap-overlay-start"
                do! awaitU (page.ClickAsync "#replay .ap-overlay-start")
                do! Wait.until "#replay to say total 0" (fun () -> Read.text page "#replay") (Option.exists (fun t -> t.Contains "total 0"))

                // …and the chapter marks (Plan 14, stage 4), which are what make a
                // whole-terminal recording navigable by what ran in it. Asserted after play
                // rather than before, because the recording's metadata — its duration, and
                // therefore where a marker sits on the bar — is not known until it loads.
                do! waitCount "exactly 1 of #replay .ap-marker" page "#replay .ap-marker" 1
                return ()
            }

        // The first player a page mounts is the one that turns the player's stylesheet on, and
        // the player measures its character cell once, as it is created. Turned on through the
        // link's `media` attribute that measurement ran before the sheet applied — in the
        // page's proportional face, with none of the sheet's borders — and every recording it
        // played came out a ninth wider than its host, its right-hand columns clipped away.
        // `#replay` is that first mount here, as the first rewind of a session is in the app.
        fixtureCase "the first replay on a page is measured under its own stylesheet, and fits its host" <| fun page ->
            async {
                do! waitVisible "the player to take its mount" page "#replay .ap-player"
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
        fixtureCase "a chapter past a long idle gap still reaches the chapter list" <| fun page ->
            async {
                do! waitVisible "the player to take the gappy recording's mount" page "#replay-gappy .ap-player"
                do! waitVisible "the gappy recording's start button" page "#replay-gappy .ap-overlay-start"
                do! awaitU (page.ClickAsync "#replay-gappy .ap-overlay-start")
                // Both of them. The player idle-compresses the EVENTS it loads, so a chapter
                // list built on the recording's raw clock disagrees with them: the far chapter
                // becomes the last event and the list's own `time < duration` filter drops it,
                // leaving one mark where the recording has two. Chapters written into the cast
                // as `"m"` events ride the same compression as the records around them.
                //
                // Asserted after play, like the case above: where a mark sits on the bar is
                // not known until the recording's duration is.
                do! waitCount "exactly 2 of #replay-gappy .ap-marker" page "#replay-gappy .ap-marker" 2
                return ()
            }

        fixtureCase "a watch that starts past a long idle gap lands there, not before it" <| fun page ->
            async {
                do! waitVisible "the gappy recording's start button" page "#replay-gappy .ap-overlay-start"
                do! awaitU (page.ClickAsync "#replay-gappy .ap-overlay-start")
                // The WAIT is the assertion, and the timeout is what makes it one: a start
                // position the player honours puts this text on screen as fast as it can
                // start, while one that lands short of the gap could only reach it after the
                // gap has played out in real time.
                // The terminal's text, not the mount's: a chapter's label on the bar names the
                // same command from the moment the recording loads, wherever it landed.
                do! Wait.within 10_000.0 "the gap played out, and the second command on the screen"
                        (fun () -> Read.text page "#replay-gappy .ap-term-text")
                        (Option.exists (fun t -> t.Contains "second"))
            }

        // A block's command is recorded as its input record — the shell's echo is not kept —
        // and the player draws only output. Handed over as recorded, a replay ran one command's
        // output into the next with nothing between them to say which was which.
        fixtureCase "a replay draws the command a block ran on the screen" <| fun page ->
            async {
                do! waitVisible "the player's start button" page "#replay .ap-overlay-start"
                do! awaitU (page.ClickAsync "#replay .ap-overlay-start")
                // Its output first, so the screen has been drawn when the command is looked for;
                // the terminal's text rather than the mount's, which also holds the chapter's label.
                do! Wait.until "the recording's output on the screen" (fun () -> Read.text page "#replay .ap-term-text") (Option.exists (fun t -> t.Contains "total 0"))
                let! screen = Read.text page "#replay .ap-term-text"
                Expect.isTrue (screen |> Option.exists (fun s -> s.Contains "ls -la")) (sprintf "the command, on the screen above what it printed: %A" screen)
            }

        // Each command's chapter is a mark on the bar, labelled with the command. The player draws
        // a label on one line centred on its mark, however long the command, so a `for` loop's
        // ran off both sides of a phone and named nothing that was left on it.
        fixtureCaseOnTouch 390 844 "every chapter's label names its command and stays on a phone's screen" <| fun page ->
            async {
                do! Harness.run page HarnessPage.chapters
                do! waitCount "the three chapters on the bar" page "#replay-chapters .ap-marker-container" 3
                do! Probe.reveal page "#replay-chapters"
                let tooltips = "#replay-chapters .ap-marker-container .ap-tooltip"
                let! seen = Probe.seen page tooltips
                let! scrolls = Probe.scrolls page tooltips
                let width = float page.ViewportSize.Width
                let labels =
                    List.zip seen scrolls
                    |> List.map (fun (label, scroll) ->
                        let inside = label.Box.Left >= 0.0 && label.Box.Right <= width
                        let whole = scroll.Width <= scroll.ClientWidth
                        label.Text + (if inside then "" else " [off the screen]") + (if whole then "" else " [cut off]"))
                Expect.equal
                    (labels |> List.map (fun label -> label.Substring (label.IndexOf " - " + 3)))
                    [ "echo one"; "for i in 1 2 3; do echo \"step $i\"; sleep 1; done"; "echo three" ]
                    "each label names its command, none of them off the screen or cut off"
            }

        fixtureCaseOnTouch 390 844 "pressing a chapter's mark moves the replay to that command" <| fun page ->
            async {
                do! Harness.run page HarnessPage.chapters
                do! waitCount "the three chapters on the bar" page "#replay-chapters .ap-marker-container" 3
                do! Probe.reveal page "#replay-chapters"
                // The last mark's own centre, and only if a press there lands on it: a mark another
                // is drawn over is not one a thumb can press.
                let! marks = Probe.seen page "#replay-chapters .ap-marker-container"
                let mark = List.last marks
                Expect.isTrue mark.Painted "the last chapter's mark is the thing under its own centre"
                do! awaitU (
                        page.Touchscreen.TapAsync (
                            float32 (mark.Box.Left + Yession.App.Probes.Box.width mark.Box / 2.0),
                            float32 (mark.Box.Top + Yession.App.Probes.Box.height mark.Box / 2.0)))
                // Resting at the start, the second command's last line is only on the screen once
                // the replay has moved past it — which, paused, only a seek can do.
                do! Wait.until "the replay to move to the last command" (fun () -> Read.text page "#replay-chapters .ap-term-text") (Option.exists (fun t -> t.Contains "step 3"))
            }

        fixtureCase "the keyboard moves a replay from chapter to chapter" <| fun page ->
            async {
                do! Harness.run page HarnessPage.chapters
                do! waitCount "the three chapters on the bar" page "#replay-chapters .ap-marker-container" 3
                // The terminal's text is what Tab reaches in a player; its keys are the player's.
                do! awaitU (page.FocusAsync "#replay-chapters .ap-term-text")
                do! awaitU (page.Keyboard.PressAsync "]")
                do! awaitU (page.Keyboard.PressAsync "]")
                do! Wait.until "the replay to move on two chapters" (fun () -> Read.text page "#replay-chapters .ap-term-text") (Option.exists (fun t -> t.Contains "step 3"))
            }

        // The camera's switch names every state it can picture, and a name it offers that does
        // not render is a picture of the state before it with the wrong caption under it. So
        // each one is swapped in and must draw exactly one shell — a conversation, once — with
        // nothing thrown on the way, and a failure names the state that went wrong rather than
        // the wait that gave up on it.
        //
        // Two kinds of wrong, caught two ways. A swap that throws throws back through the call
        // that made it, so `Harness.run1` carries it. A swap whose RENDER throws does not: Lit
        // finishes an update a microtask later, and what it threw arrives as an unhandled
        // rejection — a page error on this side — so those are kept as they land and read
        // after each state, never inferred from a timeout.
        editorCase "every state the harness names draws one shell without an error" <| fun page ->
            async {
                do! waitVisible "the shell's conversation" page "#shell [data-conversation]"
                let errors = ResizeArray<string> ()
                page.PageError.Add (fun e -> lock errors (fun () -> errors.Add e))
                let thrown () = lock errors (fun () -> String.concat "; " errors)
                let! names = Harness.held page HarnessPage.states
                // Anti-vacuity: an empty list walks nothing and passes.
                Expect.isNonEmpty names "the harness names its states"
                for name in names do
                    do! Harness.run1 page HarnessPage.state name
                    // Two frames: one for the render the swap asks for, one for it to have
                    // landed — and for whatever it threw on the way to have been reported.
                    do! Probe.frames page 2
                    Expect.equal (thrown ()) "" (sprintf "__state('%s') drew without an error" name)
                    let! shells = Read.count page "#shell [data-conversation]"
                    Expect.equal shells 1 (sprintf "__state('%s') draws one conversation" name)
            }

        // A reference is part of the sentence it sits in, so its name shares the line's
        // baseline with the words either side. It did not: an inline-flex box lends the
        // line its first item's baseline, the mark has none, and every `dev` on a phone
        // floated a descender above its `started sandbox`. Geometry, which is what only a
        // rendered page can measure — pinned as an equality of baselines, never as a pixel
        // image, so a font or a mark redrawn does not move it.
        editorCase "a reference's name sits on the line's baseline" <| fun page ->
            async {
                do! Harness.run page HarnessPage.acts
                do! waitVisible "a reference in an act's note" page "#shell [data-act-note] [data-entity]"
                let! reading = Probe.reading page Yession.App.Probes.Measure.ReferenceBaseline
                let drift = match reading with [ drift ] -> drift | _ -> infinity
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
                do! Harness.run page HarnessPage.acts
                do! waitVisible "a running act's mark" page "#shell [data-act-status=\"running\"] [data-act-running]"
                let! misplaced = Probe.verdict page Yession.App.Probes.Measure.RunningMarkOnTitle
                Expect.isEmpty misplaced "the running act's mark stands on its title's line"
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
                do! Harness.run page HarnessPage.acts
                do! waitVisible "a cause's head" page "#shell [data-cause-ref] svg"
                let! off = Probe.verdict page Yession.App.Probes.Measure.CauseHeadsOnLines
                Expect.isEmpty off "each head sits on its line"
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
                do! Harness.run page HarnessPage.acts
                do! waitVisible "a running act's cause" page "#shell [data-act-status=\"running\"] [data-cause-ref]"
                let! off = Probe.verdict page Yession.App.Probes.Measure.RunningMarkOnCauseLine
                Expect.isEmpty off "the mark hangs on the line"
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
                do! Harness.run page HarnessPage.acts
                do! waitVisible "a fold in an act's note" page "#shell [data-act-note] [data-fold]"
                let! reading = Probe.reading page Yession.App.Probes.Measure.FoldArrowsOnRail
                let off = Array.ofList reading
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
                do! Harness.run page HarnessPage.acts
                do! waitVisible "a fold in an act's note" page "#shell [data-act-note] [data-fold]"
                let! reading = Probe.reading page Yession.App.Probes.Measure.FoldArrowsOnRail
                let off = Array.ofList reading
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
                do! Harness.run page HarnessPage.acts
                do! waitVisible "a fold in an act's note" page "#shell [data-act-note] [data-fold]"
                let! reading = Probe.reading page Yession.App.Probes.Measure.FoldArrowsOnAvatars
                let off = Array.ofList reading
                Expect.isTrue (off.[0] >= 1.0) "the page must hold a fold under an author line"
                Expect.isTrue (off.[1] < 1.0) (sprintf "an arrow stood %.2fpx off its author's avatar" off.[1])
            }

        // Unfolding shows the particulars and folding takes them off the page — not merely
        // out of sight but out of the accessibility tree and the tab order, which is what
        // `visibility` settles and a rendered string cannot see. Waited for, because the
        // fold MOVES: the settled state is the promise, the motion is the design.
        editorCase "unfolding an act shows its particulars, and folding hides them" <| fun page ->
            async {
                do! Harness.run page HarnessPage.acts
                do! waitVisible "a fold in an act's note" page "#shell [data-act-note] [data-fold]"
                let visibility (what: string) (wanted: string) =
                    Wait.until what (fun () -> Probe.computed page "#shell [data-act-note] [data-fold-body]" "visibility") ((=) (Some wanted))
                do! visibility "the particulars to start hidden" "hidden"
                do! awaitU (page.ClickAsync "#shell [data-act-note] [data-fold]")
                do! visibility "the particulars to show once unfolded" "visible"
                let! said = Probe.boxes page [ "#shell [data-act-note] [data-act-said]" ]
                Expect.isTrue
                    (said |> List.exists (Option.exists (fun box -> Yession.App.Probes.Box.height box > 0.0)))
                    "and what the agent was told has height on the page"
                do! awaitU (page.ClickAsync "#shell [data-act-note] [data-fold]")
                do! visibility "the particulars to hide once folded" "hidden"
            }

        // The arrow used to be the only thing that toggled — a square a thumb can miss on a
        // phone, on a line that is otherwise most title. A press anywhere on that line reaches
        // the same fold now (`View.foldClickRow`), and this presses the TITLE, deliberately not
        // `[data-fold]`, to prove the row itself is the control rather than the arrow alone.
        editorCase "tapping the row, not only the arrow, unfolds it and folds it back" <| fun page ->
            async {
                do! Harness.run page HarnessPage.acts
                do! waitVisible "a fold in an act's note" page "#shell [data-act-note] [data-fold]"
                let visibility (what: string) (wanted: string) =
                    Wait.until what (fun () -> Probe.computed page "#shell [data-act-note] [data-fold-body]" "visibility") ((=) (Some wanted))
                do! visibility "the particulars to start hidden" "hidden"
                // The DOM's own click, on what follows the arrow: the title, not the arrow.
                let clickTitle () = awaitU (page.Locator("#shell [data-act-note] [data-fold] + *").First.DispatchEventAsync "click")
                do! clickTitle ()
                do! visibility "a press on the title unfolds it" "visible"
                do! clickTitle ()
                do! visibility "and a second press folds it back" "hidden"
            }

        // The strip scrolls sideways, and nothing about a scroll box keeps what matters in it on
        // screen: the newest tab — the one just selected — opened past the right-hand edge.
        editorCase "a newly selected tab is scrolled into view" <| fun page ->
            async {
                do! openManyTerminals page
                let! overflows = stripOverflows page
                Expect.isTrue overflows "the strip holds more tabs than it shows, or this proves nothing"
                // Chosen by the DOM's own click rather than Playwright's, which scrolls what it
                // clicks into view first and would prove nothing about the strip.
                do! awaitU (page.Locator("#shell [data-pane-strip] [role=tab]:last-child").DispatchEventAsync "click")
                do! Wait.until "the last tab to be selected" (fun () -> Read.attr page "#shell [data-pane-strip] [role=tab]:last-child" "aria-selected") ((=) (Some "true"))
                // A frame for the reveal, which runs after the render.
                do! twoFrames page
                let! inView = selectedTabInsideStrip page
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
                let! overflows = stripOverflows page
                Expect.isTrue overflows "the strip holds more tabs than it shows, or this proves nothing"
                let! count = Read.count page "#shell [data-pane-strip] [role=tab]"
                do! awaitU (page.FocusAsync "#shell [data-pane-strip] [role=tab][aria-selected=true]")
                // Where the focused tab is, against the strip's box less a fade on each end that
                // has tabs past it — read off the scroll position, not the attribute the fade is
                // keyed on, so the measurement does not trust the thing it is checking.
                let strip = "#shell [data-pane-strip]"
                let placed () =
                    async {
                        let! scroll = Probe.scroll page strip
                        let! measured = Probe.boxes page [ strip; strip + " :focus" ]
                        let! focused = Read.attr page (strip + " :focus") "data-pane-tab"
                        match scroll, measured with
                        | Some scroll, [ Some s; Some t ] ->
                            let before = scroll.Left > 1.0
                            let after = scroll.Left + scroll.ClientWidth < scroll.Width - 1.0
                            let left = s.Left + (if before then Yession.App.TabStrip.edge else 0.0)
                            let right = s.Right - (if after then Yession.App.TabStrip.edge else 0.0)
                            return
                                if t.Left >= left - 0.5 && t.Right <= right + 0.5 then ""
                                else sprintf "%A at %g..%g, shown %g..%g" focused t.Left t.Right left right
                        | _ -> return "outside"
                    }
                let! items = Read.count page "#shell [data-pane-pivot] [role=tab]"
                let faults = ResizeArray<string> ()
                let mutable reached = 0
                for key in "Home" :: List.replicate (items - 1) "ArrowRight" do
                    do! awaitU (page.Keyboard.PressAsync key)
                    // Where the walk goes is `update`'s, and its focus move is a `DomMove`,
                    // made a frame on. Read before that frame, the scroll and the box below
                    // are two reads either side of it, and disagree.
                    do! twoFrames page
                    match! placed () with
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
                do! waitPresent "an item to be selected beside one that is not" page "#shell [data-pane-strip] [role=tab][aria-selected=true]"
                do! waitPresent "an item to be selected beside one that is not" page "#shell [data-pane-strip] [role=tab][aria-selected=false]"
                // Where the colours settle: an item's colour transitions.
                do! Probe.settled page
                let strip = "#shell [data-pane-strip]"
                let on = strip + " [role=tab][aria-selected=true]"
                let off = strip + " [role=tab][aria-selected=false]"
                let! measured = Probe.boxes page [ strip; on ]
                let! scroll = Probe.scroll page strip
                let! onColour = Probe.computed page on "color"
                let! offColour = Probe.computed page off "color"
                let faults =
                    [ match measured with
                      | [ Some s; Some t ] when t.Top < s.Top - 0.5 || t.Bottom > s.Bottom + 0.5 -> "the selected item is cut off vertically"
                      | [ Some _; Some _ ] -> ()
                      | _ -> "the pivot or its selected item is missing"
                      match scroll with
                      | Some scroll when scroll.Height <> scroll.ClientHeight ->
                          sprintf "the pivot overflows %gpx vertically" (scroll.Height - scroll.ClientHeight)
                      | _ -> ()
                      if onColour = offColour then "the selected item is the same colour as the others" ]
                Expect.isEmpty faults "the selected item is drawn whole and unlike the others"
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
                do! Wait.until "focus to land on the command line" (fun () -> Read.focused page (commandLine "term-harness")) id
                do! awaitU (page.FocusAsync "#shell [data-pane-strip] [role=tab][aria-selected=true]")
                do! awaitU (page.Keyboard.PressAsync "ArrowRight")
                do! Wait.until "the walk to land on another tab, keyboard-focused" (fun () -> Read.focused page "#shell [data-pane-strip] [role=tab][aria-selected=false]:focus-visible") id
                let strip = "#shell [data-pane-strip]"
                let tab = strip + " :focus"
                let! measured = Probe.boxes page [ strip; tab ]
                let! style = Probe.computed page tab "outline-style"
                let! width = Probe.computed page tab "outline-width"
                let! offset = Probe.computed page tab "outline-offset"
                let px (value: string option) =
                    value
                    |> Option.filter (fun v -> v.EndsWith "px")
                    |> Option.map (fun v -> float (v.Substring (0, v.Length - 2)))
                let faults =
                    match measured, style, px width, px offset with
                    | _, Some "none", _, _
                    | _, _, Some 0.0, _ -> [ "no ring is drawn" ]
                    | [ Some s; Some t ], Some _, Some width, Some offset ->
                        let reach = width + offset
                        [ if t.Top - reach < s.Top - 0.5 then "cut off at the top"
                          if t.Bottom + reach > s.Bottom + 0.5 then "cut off at the bottom"
                          if t.Left - reach < s.Left - 0.5 then "cut off at the left"
                          if t.Right + reach > s.Right + 0.5 then "cut off at the right" ]
                    | _ -> [ sprintf "the strip, the focused tab or its ring could not be read: %A" (measured, style, width, offset) ]
                Expect.isEmpty faults "the ring is drawn wholly inside the strip's box"
            }

        // One door. The pane has offered a `+` on its strip, "+ New terminal" at the foot of its
        // switcher, and an empty pane's own button — two of them on one screen at once, a
        // reader left to work out they were one act. Now the list's New terminal is the
        // only one: on screen exactly while the list is — dropped as the sheet, or docked as
        // an empty pane's body — and nowhere else. Counted at more tabs than fit, the `all`
        // page, and an empty pane, which is the list docked. Counted by
        // what is painted at each control's centre, so one present but covered is not a door,
        // and one a reader can see is — which is the only count that means anything to them.
        //
        // The empty pane is reached the way a reader reaches it: every terminal ends, each
        // tab stays where it stood, closed, and the reader puts them away one × at a time —
        // each landing the pane on the next, until there is nothing left to be on.
        editorCase "the sheet's New terminal is the one way to make a terminal on screen" <| fun page ->
            async {
                let doors (state: string) =
                    async {
                        let! n = Probe.paintedCount page "#shell [data-content-panel] [data-terminal-new]"
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
                do! waitVisible "the all page" page "#shell [data-content-list]"
                let! listing = doors "the all page"
                do! awaitU (page.Keyboard.PressAsync "Escape")
                do! waitAbsent "#shell [data-content-list] to be gone" page "#shell [data-content-list]"
                // Chosen, so it is the one the reader is looking at when it ends, and it stays.
                do! awaitU (page.Locator("#shell [data-pane-tab='terminal:term-live']").DispatchEventAsync "click")
                do! waitVisible "the live terminal's tab, chosen" page "#shell [data-pane-tab='terminal:term-live'][aria-selected=true]"
                do! foldHarness page
                        [ for i, id in List.indexed [ "term-harness"; "term-live"; "term-more-0"; "term-more-1"; "term-more-2"; "term-more-3" ] ->
                              95L + int64 i, closed id ]
                for left in 5 .. -1 .. 0 do
                    do! waitVisible "a closed tab's way to put it away" page "#shell [data-pane-tab-dismiss]"
                    do! awaitU (page.ClickAsync "#shell [data-pane-tab-dismiss]")
                    do! waitCount (sprintf "%d tabs left" left) page "#shell [data-terminal-closed-tab]" left
                do! waitVisible "the list, docked in the empty pane" page "#shell [data-content-list]"
                do! waitAbsent "no scrim over a docked list" page "#shell [data-pane-sheet-scrim]"
                let! empty = doors "an empty pane"
                Expect.equal
                    [ many; listing; empty ]
                    [ "more tabs than fit: 0"; "the all page: 1"; "an empty pane: 1" ]
                    "one door, on the list"
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
                do! waitVisible "the harness terminal's tab, chosen" page "#shell [data-pane-tab='terminal:term-harness'][aria-selected=true]"
                do! foldHarness page
                        [ 95L,
                          Yession.Domain.SessionEvent.TerminalClosed
                              { Yession.Domain.Terminals.TerminalClosed.TerminalId = harnessTerminal "term-harness"
                                Yession.Domain.Terminals.TerminalClosed.Reason = "closed by a peer"
                                Yession.Domain.Terminals.TerminalClosed.By = None } ]
                let dismiss = "#shell [data-pane-tab-dismiss='term-harness']"
                do! waitVisible "the closed tab's way to put it away" page dismiss
                do! awaitU (page.FocusAsync dismiss)
                do! awaitU (page.Keyboard.PressAsync "Enter")
                do! waitAbsent "the closed tab to be gone" page "#shell [data-pane-tab='terminal:term-harness']"
                do! Wait.until "focus to be on the tab beside it" (fun () -> Read.focused page "[data-pane-tab='terminal:term-live']") id
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
                do! waitVisible "the harness terminal's tab, chosen" page "#shell [data-pane-tab='terminal:term-harness'][aria-selected=true]"
                let body =
                    foldBody
                        [ 95L,
                          Yession.Domain.SessionEvent.TerminalClosed
                              { Yession.Domain.Terminals.TerminalClosed.TerminalId = harnessTerminal "term-harness"
                                Yession.Domain.Terminals.TerminalClosed.Reason = "closed by a peer"
                                Yession.Domain.Terminals.TerminalClosed.By = None } ]
                // In the page, so no round trip decides which frame the hand lands in.
                let! held = Probe.heldAfterFold page body "#shell [data-pane-tab-dismiss='term-harness']"
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
                do! Wait.until "the pane to be 320px wide"
                        (fun () -> Probe.boxes page [ "#shell [data-content-panel]" ])
                        (function
                         | [ Some pane ] -> Math.Round (Yession.App.Probes.Box.width pane, MidpointRounding.AwayFromZero) = 320.0
                         | _ -> false)
                let faults = ResizeArray<string> ()
                for which in [ "last-child"; "first-child"; "nth-child(3)"; "last-child" ] do
                    // The DOM's own click, which scrolls nothing into view on its way.
                    let item = sprintf "#shell [data-pane-strip] [role=tab]:%s" which
                    do! awaitU (page.Locator(item).DispatchEventAsync "click")
                    do! Wait.until (sprintf "the %s item to be selected" which) (fun () -> Read.attr page item "aria-selected") ((=) (Some "true"))
                    // A frame for the reveal, which runs after the render.
                    do! twoFrames page
                    let! inView = selectedTabInsideStrip page
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
        editorCase "a chat chip opens a preview, and focus follows it" <| fun page ->
            async {
                // The chip the harness model's one block puts in the chat.
                do! waitVisible "the block's chip in the chat" page "#shell [data-chat-block]"
                do! awaitU (page.ClickAsync "#shell [data-chat-block]")

                // A preview opened, showing that block.
                do! waitVisible "the block's preview in the pane" page "#shell [data-pane-preview^='block:']"

                // Focus followed it into the pane.
                do! Wait.until "focus on [data-pane-panel]" (fun () -> Read.focused page "[data-pane-panel]") id

                // The block reads as TEXT. (Its recording was one press away here too; a
                // command's recording is its terminal's to play now, from its toolbar.)
                do! waitVisible "the block's output in its preview" page "#shell [data-pane-block] [data-terminal-output]"
                return ()
            }

        // (A block's replay used to show its first frame before play, here. A command's
        // preview no longer plays it: the recording is the terminal's, from its toolbar.)

        // The session id is a thing people copy, so on a phone a press on it has to reach
        // IT — not fall through to the title field whose box reaches down behind it, where a
        // press could only ever edit the title. Hit-tested at the id's own centre.
        editorCaseIn 390 844 "on a phone a press on the session id reaches the id, not the title" <| fun page ->
            async {
                do! waitVisible "the session id" page "#shell [data-session-id]"
                let! id = Probe.seen page "#shell [data-session-id]"
                let idPressable = id |> List.tryHead |> Option.exists (fun id -> id.Painted)
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
                let! width = windowWidth page
                Expect.equal width 390 "a true phone viewport, not a clamped window"

                // The pane starts off screen, as it does for a fresh client.
                do! paneOffScreen page

                // A chip brings it on, and it takes the column, short of the edge strip the chat
                // shows dimmed through (`Style.paneGrabEdge`).
                do! awaitU (page.ClickAsync "#shell [data-chat-block]")
                do! paneCovers "the pane to take the column" page
                // …with the tab strip retained, which is what keeps phone and desktop one
                // mental model rather than two surfaces that happen to share a codebase.
                do! waitVisible "the tab strip, kept over the pane" page "#shell [role='tablist'] [data-pane-tab]"

                // Nothing overflows sideways.
                let! overflows =
                    overflowsSideways page
                Expect.isFalse overflows "no horizontal overflow a phone user cannot scroll away"

                // Nor is the header cut off vertically. The phone's band is a compressed one
                // and it carries something the desktop's does not — the session id, in flow
                // below the title — so it is the one place the heading can outgrow its band.
                // At 64px it did: the title's box started ON the band's top edge, which on a
                // phone reads as a heading sliced off by the browser. What is asserted is the
                // containment, not the number: whatever the band becomes, what it holds has
                // to fit inside it.
                let! header = Probe.boxes page [ "#shell header"; "#shell [data-session-title]"; "#shell [data-session-id]" ]
                let headerFits =
                    match header with
                    | [ Some band; Some title; Some id ] -> title.Top > band.Top && id.Bottom <= band.Bottom
                    | _ -> false
                Expect.isTrue headerFits "the header's title and id sit inside the band, not on its edges"

                // And the way back to the chat is a control, not a dismissal: it returns
                // focus to the chip that opened the pane.
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='hide']")
                do! paneOffScreen page
                do! Wait.until "focus on [data-chat-block]" (fun () -> Read.focused page "[data-chat-block]") id
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
                let! before = Read.count page "#shell [data-pane-strip] [role=tab]"
                do! foldHarness page [ for i in 0 .. 5 -> 80L + int64 i, untitled (sprintf "term-phone-%d" i) ]
                do! waitCount "six more tabs in the strip" page "#shell [data-pane-strip] [role=tab]" (before + 6)
                do! awaitU (page.Locator("#shell [data-pane-strip] [role=tab]:last-child").DispatchEventAsync "click")
                do! Wait.until "the last tab to be selected" (fun () -> Read.attr page "#shell [data-pane-strip] [role=tab]:last-child" "aria-selected") ((=) (Some "true"))
                do! Probe.settled page
                let! overflows = stripOverflows page
                Expect.isTrue overflows "the strip holds more tabs than it shows, or this proves nothing"
                let! strip = Probe.boxes page [ "#shell [data-pane-strip]" ]
                let! tabs = Probe.seen page "#shell [data-pane-strip] [role=tab]"
                // "Whole" tolerates a couple of px either edge: text shaping sub-pixel widths
                // differ across engines and architectures (measured: ~1px between an x86 and
                // an arm64 Chromium for the same strings), and a tab overlapping the edge by
                // that little reads as whole to an actual reader. What the bug this guards
                // against produced was not a 1px miss — the selected tab ate half the strip
                // and its neighbours were cut to a single letter, tens of px short — so this
                // margin cannot hide that regression returning.
                let whole =
                    match strip with
                    | [ Some s ] ->
                        tabs
                        |> List.filter (fun t -> t.Box.Left >= s.Left - 2.0 && t.Box.Right <= s.Right + 2.0)
                        |> List.map (fun t -> t.Text.Trim ())
                    | _ -> []
                let shape = (strip, tabs |> List.map (fun t -> t.Text.Trim (), round t.Box.Left, round t.Box.Right))
                Expect.isTrue (whole.Length >= 3) (sprintf "three whole tabs or more, got %A in %A" whole shape)
            }

        // Making a terminal is a press away on a phone too: the `all` page, the door to
        // everything, offers it. Counted by what is painted at each door's centre, so exactly
        // one is, not none and not two.
        editorCaseIn 390 844 "on a phone the all page offers the one way to make a terminal" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
                do! awaitU (page.ClickAsync "#shell [data-pane-switcher]")
                do! waitVisible "the all page" page "#shell [data-content-list]"
                do! Probe.settled page
                let! doors = Probe.paintedCount page "#shell [data-content-panel] [data-terminal-new]"
                Expect.equal doors 1 "one door to a new terminal on the all page"
            }

        // A fresh viewer's strip holds nothing — a strip holds only what its reader opened — and
        // the pane brought up over a session with terminals open lands on the page that lists
        // them, never on the empty pane's New terminal over them. The edge tab is a phone's only
        // way into the pane, and it is the way that landed there. Counted by the rows a person
        // can SEE, hit-tested at their centres, so a list mounted under another face, or
        // clipped off the screen, is not one.
        editorCaseOnTouch 390 844 "on a phone the pane brought up by a fresh viewer lands on the session's open terminals" <| fun page ->
            async {
                do! Harness.run page HarnessPage.freshViewer
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
                do! paneCovers "the pane to cover the phone's screen" page
                do! Probe.settled page
                let listed = "#shell [data-content-panel] [data-terminal-list-row]"
                let! seen = Probe.seen page listed
                let! ids = Read.attrs page listed "data-terminal-list-row"
                let rows =
                    if List.length seen = List.length ids then
                        List.zip seen ids |> List.filter (fun (row, _) -> row.Painted) |> List.choose snd |> Array.ofList
                    else [||]
                Expect.containsAll rows [| "term-harness"; "term-live" |] "both open terminals are rows on the screen"
            }

        // On a phone the pane stops short of the screen's left edge, and what shows there is
        // the chat under a scrim — pressable, as the way back. Measured rather than read from
        // the markup: a strip that measured zero, sat under the pane, or left a gap the chat
        // showed through undimmed would render the same string as one that works.
        editorCaseIn 390 844 "on a phone the chat shows dimmed beside the pane and pressing it is the way back" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-chat-block]")
                do! paneCovers "the pane to take the column" page
                do! Wait.until "the edge to be drawn" (fun () -> Probe.computed page "#shell [data-pane-grab-edge]" "opacity") ((=) (Some "1"))
                let! reading = Probe.reading page Yession.App.Probes.Measure.GrabEdgeShape
                let shape = Array.ofList reading
                Expect.equal shape.Length 5 "the edge and the pane are both there to measure"
                Expect.equal shape.[0] 0.0 "the strip starts at the screen's edge"
                Expect.isTrue (shape.[1] >= 20.0) "and is a thumb's width, not a hairline"
                Expect.isTrue (abs shape.[2] <= 1.0) "and the pane begins where it ends, so no chat shows undimmed"
                Expect.equal shape.[3] 1.0 "and what is painted at its centre is the strip, so pressing there presses it"
                do! awaitU (page.Mouse.ClickAsync (float32 (shape.[1] / 2.0), float32 shape.[4]))
                do! paneOffScreen page
                return ()
            }

        // Shut, the strip is gone with the pane: a scrim left over the chat would dim the
        // thing the reader came back to, and swallow the first press at the screen's edge.
        editorCaseIn 390 844 "on a phone the pane's edge leaves nothing over the chat once it is shut" <| fun page ->
            async {
                do! paneOffScreen page
                do! Wait.until "the edge to be gone from over the chat"
                        (fun () -> Probe.verdict page Yession.App.Probes.Measure.GrabEdgeClear) List.isEmpty
                return ()
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
        // terminals exist, so it has to BE on that screen: inside the header band, inside the
        // viewport, and big enough to press. On a desktop its mark also stands on the title's
        // line, as the sidebar chevron across the band does — measured as an equality of
        // middles, never as pixels. A phone's band centres its whole row on one line
        // (`Style.headerAside`), so there it is not asserted separately.
        let edgeTabOnTheBand (desktop: bool) (page: IPage) =
            async {
                do! waitVisible "the shut pane's edge tab" page "#shell [data-content-toggle='show']"
                let! wrong = Probe.verdictWith page Yession.App.Probes.Measure.EdgeTabOnBand (if desktop then 1 else 0)
                Expect.isEmpty wrong "the edge tab sits on the header band"
            }

        editorCaseIn 1440 900 "the edge tab is on the title's line and inside the viewport, on a desktop" <| edgeTabOnTheBand true
        editorCaseIn 390 844 "the edge tab is inside the header and the viewport, on a phone" <| edgeTabOnTheBand false

        editorCase "a hidden pane takes no Tab stops" <| fun page ->
            async {
                do! waitVisible "the shut pane's edge tab" page "#shell [data-content-toggle='show']"
                // What would be skipped has to be there to skip, or this passes over nothing.
                let! held = Read.count page "#shell [data-content-panel] button, #shell [data-content-panel] [tabindex=\"0\"]"
                Expect.isTrue (held > 0) "the shut pane still holds controls"
                let! notStarted = Probe.verdict page Yession.App.Probes.Measure.FocusBeforePane
                let started = List.isEmpty notStarted
                Expect.isTrue started "focus starts on the last control before the pane"
                for _ in 1 .. 3 do
                    do! awaitU (page.Keyboard.PressAsync "Tab")
                let! recorded = Harness.read page Yession.App.Probes.focusStops
                let stops = recorded |> Option.defaultValue [||]
                // The first stop is the start itself; at least one more means Tab moved at all.
                Expect.isTrue (stops.Length > 1) (sprintf "Tab moved focus (stops: %A)" stops)
                Expect.isFalse (Array.contains "pane" stops) (sprintf "no stop inside the shut pane (stops: %A)" stops)
            }

        editorCase "showing the pane puts focus on the command line" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
                do! Wait.until "focus to land on the command line" (fun () -> Read.focused page (commandLine "term-harness")) id
            }

        // Nothing in the chat opened what was showing, so there is no chip to go back to — and
        // the fallback used to be the strip's first tab, inside the pane that had just shut.
        editorCase "hiding the pane puts focus on the way back in" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
                do! waitVisible "the control that hides the pane" page "#shell [data-content-toggle='hide']"
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='hide']")
                do! Wait.until "focus on [data-content-toggle='show']" (fun () -> Read.focused page "[data-content-toggle='show']") id
                return ()
            }

        // The panel takes focus whenever a chip opens something that is not a terminal, and
        // the browser's own ring on it was a 1px near-black outline on a near-black column.
        // Reached from the KEYBOARD, because a ring is owed to keyboard focus (`:focus-visible`)
        // and a pointer press rightly earns none.
        editorCase "the tab panel's focus is visible" <| fun page ->
            async {
                do! waitVisible "the block's chip in the chat" page "#shell [data-chat-block]"
                do! awaitU (page.FocusAsync "#shell [data-chat-block]")
                do! awaitU (page.Keyboard.PressAsync "Enter")
                do! Wait.until "focus on [data-pane-panel]" (fun () -> Read.focused page "[data-pane-panel]") id
                let! style = Probe.computed page ":focus" "outline-style"
                let! width = Probe.computed page ":focus" "outline-width"
                match style, width with
                | Some style, Some width ->
                    let ring = sprintf "%s %s" style width
                    // `auto` is the browser's own ring — the one that did not show.
                    Expect.isFalse (style = "none" || style = "auto") (sprintf "a ring of our own paints (%s)" ring)
                    Expect.isTrue (float (width.Replace ("px", "")) >= 2.0) (sprintf "at least 2px (%s)" ring)
                | _ -> failwithf "the focused panel's outline could not be read: %A" (style, width)
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
                do! waitVisible "the composer" page "#shell [data-draft-editor] .ProseMirror"
                // Focused BEFORE the room is made, while the shell is the whole document. A
                // click Playwright retries scrolls again with its next alignment, and with the
                // room already there that scroll took the page 342px (centre) or 400px (start)
                // down before a key was pressed: the gap below was the click's, not typing's.
                do! awaitU (page.ClickAsync "#shell [data-draft-editor] .ProseMirror")
                let! room = Probe.verdictWith page Yession.App.Probes.Measure.RoomBelowShell 400
                Expect.isEmpty room "the page has room to scroll below the shell"
                let! cdp = await (page.Context.NewCDPSessionAsync page)
                let scale = Collections.Generic.Dictionary<string, obj> ()
                scale.["pageScaleFactor"] <- box 1.6
                let! _ = await (cdp.SendAsync ("Emulation.setPageScaleFactor", scale))
                do! Wait.until "the visual viewport to shrink under the layout" (fun () -> Probe.window page) (fun w -> w.VisualHeight < w.InnerHeight)

                // How far the visible area runs past the shell's foot, in CSS pixels: the gap.
                let pastShell () =
                    async {
                        match! Probe.reading page Yession.App.Probes.Measure.PastShell with
                        | [ gap ] -> return gap
                        | _ -> return failwith "there is no shell to measure past"
                    }
                // The arrangement's premise, read before the action: nothing past the shell is
                // on screen yet. A page the arrangement already moved fails HERE, naming the
                // arrangement, and is never reported as typing's double scroll.
                let! arranged = pastShell ()
                if arranged > 1.0 then
                    failwithf "the arrangement left %.0fpx of page under the shell before any typing" arranged
                do! awaitU (page.Keyboard.TypeAsync "one")
                do! awaitU (page.Keyboard.PressAsync "Enter")
                do! awaitU (page.Keyboard.TypeAsync "two")
                let! typed = pastShell ()
                Expect.isTrue (typed <= 1.0) (sprintf "typing left %.0fpx of page under the shell" typed)
            }
        editorCaseIn 390 844 "a message no line break fits inside never scrolls the timeline sideways" <| fun page ->
            async {
                let! width = windowWidth page
                Expect.equal width 390 "a true phone viewport, not a clamped window"

                // The fixture's wide message is present — otherwise this passes by rendering
                // nothing that could have overflowed.
                do! waitVisible "the fixture's wide message" page "#shell [data-conversation] [data-message-body] pre"
                let! timeline = Probe.scroll page "#shell [data-conversation]"
                let sideways = timeline |> Option.exists Yession.App.Probes.Scroll.overflowsSideways
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
                let! width = windowWidth page
                Expect.equal width 390 "a true phone viewport, not a clamped window"

                // The chip is on screen and carries the name — otherwise this passes by
                // rendering nothing that could have overflowed.
                do! waitVisible "the queued command's terminal name" page "#shell [data-conversation] [data-chat-pending] [data-pending-subject]"
                let! timeline = Probe.scroll page "#shell [data-conversation]"
                let sideways = timeline |> Option.exists Yession.App.Probes.Scroll.overflowsSideways
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
                    let! measured = windowWidth page
                    Expect.equal measured width "a true viewport, not a clamped window"

                    // Where the conversation's words start, read off the page the harness
                    // loads with: the body's own box plus the avatar gutter it carries.
                    do! waitVisible "a message in the conversation" page "#shell [data-conversation] [data-message-body]"
                    let body = "#shell [data-conversation] [data-message-body]"
                    let! bodyBox = Probe.boxes page [ body ]
                    let! padding = Probe.computed page body "padding-left"
                    let column =
                        match bodyBox, padding |> Option.filter (fun p -> p.EndsWith "px") with
                        | [ Some box ], Some padding -> int (floor (box.Left + float (padding.Substring (0, padding.Length - 2)) + 0.5))
                        | _ -> failwith "the conversation's first body, or its padding, could not be read"

                    // The same session before it began, with a row held — so the branch line
                    // inside a row is on screen as well as the lines outside them.
                    do! Harness.run1 page HarnessPage.launch true
                    do! waitVisible "a repository on the launch card" page "#shell [data-repo-picker] [data-repo-candidate]"
                    do! awaitU (page.ClickAsync "#shell [data-repo-picker] [data-repo-candidate]")
                    do! waitVisible "the held row's branch" page "#shell [data-repo-picker] [data-repo-candidate-branch]"

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
                    let! adrift = Probe.verdictWith page Yession.App.Probes.Measure.AskCardColumn column
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
        //
        // The SIDEWAYS phone is the anchor the rest of the rule was written for. There the
        // card's floor leaves the column less room than one item, and what the doctrine asks
        // for is absence: the conversation is not drawn, and the card's way out brings it back
        // (docs/visual-design.md, Space). Measured before that rule landed, the column was a
        // 24px strip with 608px of extent under it and all fourteen of its items pressed
        // through to the author line pinned over them — a column that scrolled and could not be
        // touched. The two legs are one promise, and the probe holds whichever of them this
        // viewport is in; a case per leg would be two names for one thing nobody can choose
        // between.
        let launchCardCoversNothingCase width height =
            editorCaseIn width height
                (sprintf "at %dx%d nothing in the conversation is drawn that a press cannot reach" width height) <| fun page ->
                async {
                    do! Harness.run page HarnessPage.launchOver
                    do! waitVisible "the launch card's start button" page "#shell [data-repo-picker] [data-repo-picker-start]"
                    // The card RISES in (`animate-ask-rise`), growing its height over ~240ms, and
                    // while it does the chat's room is still changing underfoot — an item
                    // hit-tested mid-rise can be under a card that has not finished arriving. The
                    // promise is about where things land once it has, so wait for the entrance to
                    // settle (finite animations only; the carets pulse forever).
                    do! Probe.settled page
                    let! hidden = Probe.verdict page Yession.App.Probes.Measure.ItemsClearOfCard
                    Expect.isEmpty
                        hidden
                        (sprintf "a conversation the card leaves room for is one every press reaches, and one it does not is not drawn; this was neither: %s"
                            (String.Join (" | ", hidden)))
                }
        launchCardCoversNothingCase 390 844
        launchCardCoversNothingCase 844 390
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
                HarnessOn.Shell
                (Some (BrowserNewContextOptions (
                        ViewportSize = ViewportSize (Width = width, Height = height),
                        ReducedMotion = ReducedMotion.Reduce)))
                (sprintf "at %dpx the launch card keeps its height as repositories load" width) <| fun page ->
                async {
                    let shape () =
                        async {
                            match! boxesOf page [ "#shell [data-repo-picker]"; "#shell [data-repo-picker-start]" ] with
                            | [ card; start ] -> return [| px (Yession.App.Probes.Box.height card); px start.Top |]
                            | _ -> return failwith "unreachable: two selectors, two boxes"
                        }
                    // The instant before the repositories land: the card is up, still looking.
                    do! Harness.run page HarnessPage.launchLooking
                    do! waitVisible "the launch card's start button" page "#shell [data-repo-picker] [data-repo-picker-start]"
                    let! looking = shape ()
                    // Ground truth that this IS the looking state and not the loaded one already:
                    // nothing to hold yet, so the case measures the step it means to.
                    let! rows = Read.count page "#shell [data-repo-picker] [data-repo-candidate]"
                    Expect.equal rows 0 "the looking state has no repositories in it yet"
                    // The repositories arrive.
                    do! Harness.run1 page HarnessPage.launch true
                    do! waitVisible "a repository on the launch card" page "#shell [data-repo-picker] [data-repo-candidate]"
                    let! loaded = shape ()
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
                do! Harness.run1 page HarnessPage.launch true
                do! waitVisible "the launch card's way out" page "#shell [data-repo-picker-dismiss]"
                let! dismiss = boxesOf page [ "#shell [data-repo-picker-dismiss]" ]
                let size = dismiss |> List.map (fun b -> [| px (Yession.App.Probes.Box.width b); px (Yession.App.Probes.Box.height b) |]) |> List.head
                Expect.isTrue (size.[0] >= 44 && size.[1] >= 44) (sprintf "44px each way, got %dx%d" size.[0] size.[1])
                let! label = Read.attr page "#shell [data-repo-picker-dismiss]" "aria-label"
                let! title = Read.attr page "#shell [data-repo-picker-dismiss]" "title"
                let named = (label |> Option.exists ((<>) "")) && (title |> Option.exists ((<>) ""))
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
                do! Harness.run1 page HarnessPage.launch true
                do! waitVisible "the launch card's start button" page "#shell [data-repo-picker] [data-repo-picker-start]"
                let! measured = boxesOf page [ "#shell [data-repo-picker]"; "#shell [data-repo-picker] [data-repo-picker-start]" ]
                let margins =
                    match measured with
                    | [ outer; inner ] -> [| px (inner.Left - outer.Left); px (outer.Right - inner.Right) |]
                    | _ -> failwith "unreachable: two selectors, two boxes"
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
                do! Harness.run1 page HarnessPage.launch true
                do! waitVisible "a repository on the launch card" page "#shell [data-repo-picker] [data-repo-candidate]"
                // Holding a row is what puts a branch on it to go and change.
                do! awaitU (page.ClickAsync "#shell [data-repo-picker] [data-repo-candidate]")
                do! waitVisible "the held row's branch" page "#shell [data-repo-candidate-branch]"

                let! cardAndBranch = boxesOf page [ "#shell [data-repo-picker]"; "#shell [data-repo-picker-pane=\"branch\"]" ]
                let away =
                    match cardAndBranch with
                    | [ card; branch ] -> branch.Left >= card.Right - 1.0
                    | _ -> false
                Expect.isTrue away "the branch pane starts off the card's right edge"
                let! held = inert page "#shell [data-repo-picker-pane=\"branch\"]"
                let reachable = not held
                Expect.isFalse reachable "and a keyboard cannot get into it while it is there"

                do! awaitU (page.ClickAsync "#shell [data-repo-candidate-branch]")

                // It arrives, and the pane it came from leaves by the other edge.
                do! Wait.until "the branch pane to arrive, and the repo pane to leave by the other edge"
                        (fun () -> boxesOf page [ "#shell [data-repo-picker]"; "#shell [data-repo-picker-pane=\"branch\"]"; "#shell [data-repo-picker-pane=\"repo\"]" ])
                        (function
                         | [ card; branch; repo ] -> abs (branch.Left - card.Left) <= 1.0 && repo.Right <= card.Left + 1.0
                         | _ -> false)
                let! left = inert page "#shell [data-repo-picker-pane=\"repo\"]"
                let trapped = not left
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
                do! Harness.run1 page HarnessPage.launch true
                do! waitVisible "a repository on the launch card" page "#shell [data-repo-picker] [data-repo-candidate]"

                // Ground truth, both halves: the foot is below the card, and the page it
                // stands for has not arrived. Without the first, this case passes on a foot
                // that was visible from the start and proves nothing about reaching it.
                let! bodyAndFoot = boxesOf page [ "#shell [data-repo-picker-body]"; "#shell [data-repo-picker-foot]" ]
                let below =
                    match bodyAndFoot with
                    | [ body; foot ] -> foot.Top > body.Bottom
                    | _ -> false
                Expect.isTrue below "the foot starts below the card, so reaching it is something the reader does"
                let! early = Read.count page "#shell [data-repo-candidate=\"octo/next-one\"]"
                let arrivedEarly = early > 0
                Expect.isFalse arrivedEarly "and the page it stands for has not been asked for"

                // Reaching it. A scroll of the card, which is what a thumb does — never a
                // call to whatever fetches, which would test the harness rather than the
                // page.
                match! Probe.scroll page "#shell [data-repo-picker-body]" with
                | Some body ->
                    let! _ = Probe.scrollTo page "#shell [data-repo-picker-body]" body.Height
                    ()
                | None -> failwith "the card has no body to scroll"

                do! waitVisible "the next page of repositories" page "#shell [data-repo-candidate='octo/next-one']"
                let! feet = Read.count page "#shell [data-repo-picker-foot]"
                let ended = feet = 0
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
                do! waitVisible "the session title" page "#shell [data-session-title]"
                do! awaitU (page.FocusAsync "#shell [data-session-title]")
                do! awaitU (page.Keyboard.TypeAsync " on a phone")
                // Ground truth: the field really is where the typing went, and it really is
                // the focused element for Enter to release.
                do! Wait.until "focus on [data-session-title]" (fun () -> Read.focused page "[data-session-title]") id

                do! awaitU (page.Keyboard.PressAsync "Enter")

                do! Wait.until "focus to leave the title" (fun () -> Read.focused page "[data-session-title]") not
                let! title = Read.values page "#shell [data-session-title]"
                Expect.isTrue (title |> List.exists (fun t -> t.Contains "on a phone")) (sprintf "the committed title is the one that was typed: %A" title)
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
                let! chosen = separatorValue page
                do! awaitU (page.SetViewportSizeAsync (1920, 1080))
                do! twoFrames page
                // Waited for on the PIXELS meeting the separator again, then read: a pane that
                // grew would settle too, at the default's width rather than the reader's.
                do! Wait.until "the pane to settle after the resize"
                        (fun () ->
                            async {
                                let! window = Probe.window page
                                let! met = paneMeetsSeparator page ()
                                return window.InnerWidth = 1920.0 && met
                            })
                        id
                let! width = paneWidth page
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
                do! waitVisible "the control that shows the pane" page "#shell [data-content-toggle='show']"
                do! awaitU (page.Locator("#shell [data-content-toggle=\"show\"]").First.DispatchEventAsync "click")
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
                let value () = separatorValue page

                // Showing the pane hands the keyboard to what it shows (the focus contract,
                // `ClientModel.paneLanding`), a frame AFTER the render. Waited for here, because
                // focusing the divider before that frame is a focus the landing then takes back:
                // on a quick box the frame had always come first, and on a loaded runner it did
                // not, which read as a divider that is not a separator.
                do! Wait.until "the keyboard to land in the pane" (fun () -> Read.focused page "#shell [data-content-panel] *") id

                // Focusable, and it says what it is: a separator with a value is the one
                // shape assistive technology can report and move.
                do! awaitU (page.FocusAsync "#shell [data-term-resize]")
                let! role = focusedAttr page "role"
                let! said = focusedAttr page "aria-valuenow"
                let isSeparator = role = Some "separator" && Option.isSome said
                Expect.isTrue isSeparator "the focused divider is a separator carrying its value"
                let! before = value ()

                // The two arrows move it, and they are opposites. Which one GROWS the column
                // is deliberately not asserted: this one is on the right, so left-grows reads
                // as "drag its edge", but a design that put the pane elsewhere or read the
                // keys the other way round would be a different choice rather than a broken
                // one — and a test that failed for it would be reporting taste as a
                // regression. What has to hold is that a keyboard can move the split at all,
                // and that the second press undoes the first.
                do! awaitU (page.Keyboard.PressAsync "ArrowLeft")
                do! twoFrames page
                let! moved = value ()
                Expect.isTrue
                    (abs (moved - before) > 1.0)
                    (sprintf "an arrow key must move the split (was %f, still %f)" before moved)
                do! awaitU (page.Keyboard.PressAsync "ArrowRight")
                do! twoFrames page
                let! back = value ()
                Expect.isTrue
                    (abs (back - before) < abs (moved - before))
                    (sprintf "the opposite arrow must move it back (started %f, went %f, now %f)"
                        before moved back)

                // And the column is really that wide, once it has finished travelling there.
                // Deterministic because the destination is declared: what is waited for is the
                // pixels catching up to the value, not a guess about when a transition ended.
                // Without this the test would be happy with a separator that narrates a resize
                // nothing performed.
                do! Wait.until "the pane's width to catch up with its separator" (paneMeetsSeparator page) id
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
                do! waitVisible "the live terminal's screen" page screen
                do! waitTextIn "the screen to say vim ~/notes" page "#shell [data-terminal-screen='term-live']" "vim ~/notes"

                // A press on it puts the keyboard there, because its whole purpose is having it.
                do! awaitU (page.ClickAsync screen)
                do! Wait.until "focus on [data-terminal-keys='term-live']" (fun () -> Read.focused page "[data-terminal-keys='term-live']") id

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
                do! Harness.run5 page HarnessPage.snapshot "term-live" 0 "session start\r\n" None None
                do! waitTextIn "the screen to say earlier output" page "#shell [data-terminal-screen='term-live']" "earlier output"

                do! awaitU (page.Keyboard.TypeAsync "ls")
                do! awaitU (page.Keyboard.PressAsync "ArrowUp")
                do! awaitU (page.Keyboard.PressAsync "Control+c")
                do! awaitU (page.Keyboard.PressAsync "Backspace")
                do! awaitU (page.Keyboard.PressAsync "Enter")
                let! typed = Harness.read page HarnessPage.typed
                Expect.equal typed (Some "ls\u001b[A\u0003\u007f\r") "printable, escape, control code, delete, carriage return"

                // Tab is SENT rather than moving focus out of the terminal mid-session, and
                // the shift is that `preventDefault` fires for everything the terminal takes.
                let! before = focusedAttr page "data-terminal-keys"
                do! awaitU (page.Keyboard.PressAsync "Tab")
                let! after = focusedAttr page "data-terminal-keys"
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
                do! waitVisible "the control that takes the harness terminal" page "#shell [data-terminal-take='term-harness']"

                // The press that hands this peer the lease — and removes itself doing it.
                do! awaitU (page.ClickAsync "#shell [data-terminal-take='term-harness']")
                do! Wait.until "focus on [data-terminal-keys='term-harness']" (fun () -> Read.focused page "[data-terminal-keys='term-harness']") id

                // …and it is really the keyboard, not merely a focus ring: what is typed now
                // reaches the pty rather than the composer that used to be there.
                do! awaitU (page.Keyboard.PressAsync "ArrowUp")
                let! typed = Harness.read page HarnessPage.typed
                Expect.equal typed (Some "\u001b[A") "a key pressed after the take reaches the terminal"
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
                do! waitVisible "the harness terminal's command line" page line
                do! awaitU (page.FocusAsync line)
                do! Wait.until "focus on the harness terminal's command line" (fun () -> Read.focused page "[data-terminal-input^='term-draft:term-harness:']") id

                do! Harness.run1 page HarnessPage.take "term-harness"
                do! Wait.until "the keyboard on the screen the lease landed on, not on the command line under it"
                        (fun () -> Read.focused page "[data-terminal-keys='term-harness']") id
            }
        // The other route in, and the reason the focus move lives in the render loop rather
        // than on the press: a block that takes the screen hands its author the keyboard with
        // nobody pressing anything. Focus that SURVIVED that render is not stranded and must
        // not be taken — a terminal going full-screen three tabs away is not a reason to yank
        // somebody's caret out of the message they are writing.
        editorCase "a terminal going live does not take the keyboard from what someone is writing" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
                do! waitVisible "the control that takes the harness terminal" page "#shell [data-terminal-take='term-harness']"

                // Somebody's keyboard is somewhere else in the pane — on the splitter, which
                // is rendered whatever the terminal is doing and survives this render.
                do! awaitU (page.FocusAsync "#shell [data-term-resize]")
                do! Wait.until "focus on [data-term-resize]" (fun () -> Read.focused page "[data-term-resize]") id

                // The lease arrives on its own, as the alt-screen flip delivers it.
                do! Harness.run1 page HarnessPage.take "term-harness"
                do! waitVisible "the harness terminal to go live" page "#shell [data-terminal-screen='term-harness']"
                let! kept = Read.focused page "[data-term-resize]"
                Expect.isTrue kept "a lease landing leaves focus that survived the render where it was"
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
                do! waitVisible "the control that hands the terminal back" page release
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
                do! Wait.until "the keyboard on the command line"
                        (fun () -> Read.focused page "#shell [data-terminal-input^='term-draft:']:not([readonly])") id
            }
        editorCase "a shell dying under the reader leaves the keyboard in the pane" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
                let commandLine = "#shell [data-terminal-input^='term-draft:']:not([readonly])"
                do! waitVisible "the command line" page commandLine
                do! awaitU (page.FocusAsync commandLine)
                do!
                    foldHarness page [
                        90L,
                        Yession.Domain.SessionEvent.TerminalClosed
                            { Yession.Domain.Terminals.TerminalClosed.TerminalId = harnessTerminal "term-harness"
                              Yession.Domain.Terminals.TerminalClosed.Reason = "the shell exited with code -1"
                              Yession.Domain.Terminals.TerminalClosed.By = None } ]
                do! Wait.until "the keyboard in the pane's panel" (fun () -> Read.focused page "#shell [data-pane-panel], #shell [data-pane-panel] *") id
            }
        // Somebody taking the keyboard from the person typing in it (P3-2). The lease bar
        // renamed its holder, and for the person stolen from that was the whole announcement
        // — their keys simply stopped going anywhere.
        editorCase "a steal tells the person it was taken from, and offers it back" <| fun page ->
            async {
                do! stolenFromHarness page
                do! waitPresent "take back, in a notice about the steal" page "#shell [data-terminal-stolen='term-harness'] [data-terminal-take='term-harness']"
            }
        editorCase "keys typed after a steal do not reach the terminal" <| fun page ->
            async {
                do! stolenFromHarness page
                do! waitVisible "the notice that the terminal was taken" page "#shell [data-terminal-stolen='term-harness']"
                do! awaitU (page.Keyboard.TypeAsync "pwd")
                do! awaitU (page.Keyboard.PressAsync "Enter")
                let! typed = Harness.read page HarnessPage.typed
                Expect.equal typed None "nothing typed after the steal reached the pty"
            }
        // The live screen's ring, on the focus every way into it gives: a script's, which is
        // not `:focus-visible`, so the ring the rest of the page wears painted nothing here.
        editorCase "a focused live screen paints its ring" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
                do! awaitU (page.ClickAsync "#shell [data-terminal-take='term-harness']")
                do! Wait.until "focus on [data-terminal-keys='term-harness']" (fun () -> Read.focused page "[data-terminal-keys='term-harness']") id
                let held = "[data-terminal-screen]:has(:focus)"
                let! style = Probe.computed page held "outline-style"
                let! width = Probe.computed page held "outline-width"
                Expect.equal (style, width) (Some "solid", Some "2px") "the screen with the keyboard wears the ring"
            }
        // The caret is read off the browser's own emulator — the cursor is not in the
        // serialization in any form a renderer can use, so only a real one can say where it is.
        editorCase "the live screen marks where its cursor stands" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
                do! awaitU (page.ClickAsync "#shell [data-terminal-take='term-harness']")
                do! waitVisible "the keyboard in the harness terminal's live screen" page "#shell [data-terminal-screen='term-harness'] [data-terminal-keys]"
                // A line typed, then the cursor moved four cells back along it.
                do! Harness.run5 page HarnessPage.snapshot "term-harness" 50 "$ echo hi\u001b[4D" (Some 80) (Some 24)
                do! Wait.until "the caret on the o of echo"
                        (fun () -> Read.text page "#shell [data-terminal-screen='term-harness'] [data-terminal-caret]") ((=) (Some "o"))
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
                do! waitVisible "the live terminal's screen" page screen
                do! awaitU (page.TapAsync screen)
                do! Wait.until "a text field inside the screen to hold focus"
                        (fun () -> Read.focused page "[data-terminal-screen='term-live'] textarea") id
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
                do! waitVisible "the live terminal's screen" page screen
                do! awaitU (page.TapAsync screen)
                do! Wait.until "focus on [data-terminal-keys='term-live']" (fun () -> Read.focused page "[data-terminal-keys='term-live']") id
                do! awaitU (page.Keyboard.InsertTextAsync "bob")
                do! awaitU (page.Keyboard.InsertTextAsync "\n")
                let! typed = Harness.read page HarnessPage.typed
                Expect.equal typed (Some "bob\r") "the word once, and Return as a carriage return"
            }
        // The other half of putting the keyboard in a field on a press: a press that ENDS a
        // selection is somebody copying output, and focusing a text field would take the
        // selection out from under them.
        editorCase "selecting output on the live screen keeps the selection" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
                do! awaitU (page.ClickAsync "#shell [data-terminal-tab='term-live']")
                let screen = "#shell [data-terminal-screen='term-live']"
                do! waitTextIn "the screen to say vim ~/notes" page screen "vim ~/notes"
                // Where the words are, so the drag runs across them rather than across padding.
                let words () =
                    async {
                        match! Probe.textAt page screen "vim ~/notes" with
                        | Some r -> return r
                        | None -> return failwith "the screen stopped saying vim ~/notes"
                    }
                let! r = words ()
                let! box = await (page.Locator(screen).BoundingBoxAsync ())
                // The drag starts at a place IN the screen, reached through its locator: Playwright
                // brings it onto the page's screen, waits for it to stand still, and refuses when
                // something else would take the press. A point worked out from the boxes alone can
                // be below the screen (the sheet's case, "pressing the pane under the list's
                // sheet", says how). Where it ends is measured again, after any scroll the hover made.
                let start = Position (X = float32 (r.Left + 1.0) - box.X, Y = float32 ((r.Top + r.Bottom) / 2.0) - box.Y)
                do! awaitU (page.Locator(screen).HoverAsync (LocatorHoverOptions (Position = start)))
                let! r = words ()
                do! awaitU (page.Mouse.DownAsync ())
                do! awaitU (page.Mouse.MoveAsync (float32 (r.Right - 1.0), float32 ((r.Top + r.Bottom) / 2.0), MouseMoveOptions (Steps = 5)))
                do! awaitU (page.Mouse.UpAsync ())
                let! selected = Probe.selection page
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
                do! waitVisible "the harness terminal's command line" page line
                do! awaitU (page.TapAsync line)
                do! awaitU (page.SetViewportSizeAsync (390, 544))
                let run = sprintf "#shell :has(> %s) [data-terminal-send]" (commandLine "term-harness")
                do! Wait.until "the command line and Run inside a viewport 300px shorter"
                        (fun () ->
                            async {
                                let! window = Probe.window page
                                let! placed = Probe.boxes page [ line; run ]
                                return window, placed
                            })
                        (fun (window, placed) ->
                            match placed with
                            | [ Some l; Some r ] -> window.InnerHeight = 544.0 && l.Bottom <= 544.0 && r.Bottom <= 544.0
                            | _ -> false)
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
                do! waitVisible "the live terminal's screen" page screen
                do! awaitU (page.FocusAsync (screen + " [data-terminal-keys]"))
                do! Wait.until "focus on [data-terminal-keys='term-live']" (fun () -> Read.focused page "[data-terminal-keys='term-live']") id

                do! awaitU (page.Keyboard.PressAsync "Control+ArrowLeft")
                do! awaitU (page.Keyboard.PressAsync "Alt+b")
                do! awaitU (page.Keyboard.PressAsync "Alt+Backspace")
                do! awaitU (page.Keyboard.PressAsync "Control+Backspace")
                do! awaitU (page.Keyboard.PressAsync "Shift+ArrowRight")
                let! typed = Harness.read page HarnessPage.typed
                Expect.equal
                    typed
                    (Some "\u001b[1;5D\u001bb\u001b\u007f\b\u001b[1;2C")
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
        // A terminal whose shell was replaced (Plan 13, stage 2f): what a person must be able
        // to READ off the screen, and the way on they must be able to REACH. Hooks and hit
        // tests, never the words — the copy is the design's to change.
        testList "A terminal that cannot run commands, on screen" [
            editorCaseIn 1440 900 "its banner names the command it stopped after" <| fun page ->
                async {
                    do! paneOnBlocks page
                    do! harnessReplaced page
                    let! said = await (page.Locator("#shell [data-terminal-lost='term-harness']").InnerTextAsync ())
                    Expect.stringContains said "dotnet fsi tasks.fsx build" "the open command, by its head"
                }

            editorCaseIn 390 844 "once refused, the agent's command is not shown as queued anywhere" <| fun page ->
                async {
                    do! harnessReplaced page
                    do! waitVisible "the chat to say the agent's command was refused" page "#shell [data-chat-refused='term-harness']"
                    let! chips = visibleCount page "#shell [data-chat-pending='queue-harness'], #shell [data-terminal-queued='queue-harness']"
                    Expect.equal chips 0 "no queued chip or card for a refused command"
                }

            editorCaseIn 390 844 "a refusal by the session names no internal actor" <| fun page ->
                async {
                    do! harnessReplaced page
                    do! waitVisible "the chat to say the agent's command was refused" page "#shell [data-chat-refused='term-harness']"
                    let! shown = await (page.Locator("#shell").InnerTextAsync ())
                    let text = shown.ToLowerInvariant ()
                    Expect.isFalse (text.Contains "session-process") "the session's token is not a name"
                }
            editorCaseIn 1440 900 "its banner lists what the session refused, where a person can see it" <| fun page ->
                async {
                    do! paneOnBlocks page
                    do! harnessReplaced page
                    do! waitVisible "the banner over the lost terminal" page "#shell [data-terminal-lost='term-harness']"
                    let! listed = visibleCount page "#shell [data-terminal-lost='term-harness'] [data-terminal-refused='block-refused']"
                    Expect.equal listed 1 "the agent's refused command is on the banner, once"
                }

            editorCaseIn 1440 900 "its banner offers a new terminal, in the same sandbox" <| fun page ->
                async {
                    do! paneOnBlocks page
                    do! harnessReplaced page
                    do! awaitU (page.ClickAsync "#shell [data-terminal-lost='term-harness'] button[data-terminal-lost-new='term-harness']")
                    let! opened = Harness.read page HarnessPage.opened
                    Expect.equal (opened |> Option.map Array.length) (Some 1) "one press, one terminal asked for"
                }

            // The person who never opens the pane is the one the incident was about: the agent
            // blocked for hours behind a terminal nobody was looking at.
            editorCaseIn 390 844 "with the terminal out of sight, the chat says the agent's command was refused" <| fun page ->
                async {
                    do! harnessReplaced page
                    do! waitVisible "the chat to say the agent's command was refused" page "#shell [data-chat-refused='term-harness']"
                    let! said = visibleCount page "#shell [data-chat-refused='term-harness']"
                    Expect.equal said 1 "the conversation carries it, once"
                }

            editorCaseIn 390 844 "the chat's row reaches the terminal" <| fun page ->
                async {
                    do! harnessReplaced page
                    do! awaitU (page.ClickAsync "#shell [data-chat-refused-open='term-harness']")
                    do! waitPresent "the pane to show the terminal" page "#shell [data-terminal-lost='term-harness']"
                }

            // The word a person never needs: the marks are the session's mechanism, and a queue
            // chip that said "not marking" named it to someone with nothing to do about it.
            editorCaseIn 390 844 "nothing on screen says marking or re-arm" <| fun page ->
                async {
                    do! harnessReplaced page
                    do! waitVisible "the chat to say the agent's command was refused" page "#shell [data-chat-refused='term-harness']"
                    let! shown = await (page.Locator("#shell").InnerTextAsync ())
                    let text = shown.ToLowerInvariant ()
                    Expect.isFalse (text.Contains "marking") "no marking"
                    Expect.isFalse (text.Contains "re-arm") "no re-arm"
                }
        ]

        testList "The page draws at most once a frame" [
            editorCase "a burst of live records costs a render per frame, not one per record" <| fun page ->
                async {
                    do! twoFrames page
                    let! renders, _ = afterBurst page
                    // The frame the burst landed in, drawn as the first record arrived, and the
                    // next, drawn with the other forty-nine.
                    Expect.isTrue
                        (renders <= 2)
                        (sprintf "fifty records across one frame boundary drew %d times; at most two" renders)
                }
            editorCase "the last record of a burst is on the page by the next frame" <| fun page ->
                async {
                    do! paneOnBlocks page
                    let! _, shown = afterBurst page
                    Expect.isTrue shown "the record the frame's render was owed is drawn, not the one that asked first"
                }
            // A frame later is a frame of latency nobody should pay for one keystroke's worth of
            // change. So the case dispatches from inside a frame in which nothing has been drawn
            // yet, and reads the page back in the same breath: a page drawn whenever the next
            // frame comes would read "not yet".
            editorCase "a lone message is on the page before the call that dispatched it returns" <| fun page ->
                async {
                    do! paneOnBlocks page
                    let! wrong = Probe.verdict page Yession.App.Probes.Measure.LoneRecordInQuietFrame
                    Expect.isEmpty wrong "a message into an undrawn frame is drawn at once"
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
                do! waitVisible "the live terminal's screen" page "#shell [data-terminal-screen='term-live']"

                // Seeded at a seq past everything the fixture's feed holds, so what ends up on
                // this screen is only what this case put there.
                do! Harness.run5 page HarnessPage.snapshot "term-live" 10 "" (Some 40) (Some 24)
                do! Harness.run4 page HarnessPage.record "term-live" 10 "o" "\u001b[500GX"
                // Waited for, then MEASURED. A wait that was also the assertion would fail as
                // a timeout naming neither number, and this tier can only fail by a wait
                // never settling.
                // The width of the screen's line that ends in `mark`, once there is one.
                let lineEndingIn (mark: string) () =
                    async {
                        let! drawn = Read.text page "[data-terminal-screen='term-live']"
                        return
                            drawn
                            |> Option.bind (fun text ->
                                text.Split '\n' |> Array.tryFind (fun line -> line.TrimEnd().EndsWith mark))
                            |> Option.map (fun line -> line.TrimEnd().Length)
                    }
                let widthOfLineEndingIn (mark: string) =
                    async {
                        do! Wait.until (sprintf "a line ending in %s" mark) (lineEndingIn mark) Option.isSome
                        let! width = lineEndingIn mark ()
                        return width |> Option.defaultValue -1
                    }
                let! atForty = widthOfLineEndingIn "X"
                Expect.equal atForty 40 "the last column of a 40-column screen"

                // A resize is a record like any other, and the emulator follows it — the pty
                // was told the same thing at the same point in the same stream.
                do! Harness.run4 page HarnessPage.record "term-live" 11 "r" "100x24"
                do! Harness.run4 page HarnessPage.record "term-live" 12 "o" "\r\n\u001b[500GY"
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
                do! waitVisible "the live terminal's screen" page "#shell [data-terminal-screen='term-live']"

                // The size this client reported for the pane as it stands.
                let reported () = Harness.read page HarnessPage.resized
                do! Wait.until "the client to report a size" reported Option.isSome
                let! before = reported ()

                // Widen the column from the keyboard, which dispatches nothing at all: the
                // splitter writes a CSS custom property on the shell root.
                do! awaitU (page.FocusAsync "#shell [data-term-resize]")
                for _ in 1 .. 12 do
                    do! awaitU (page.Keyboard.PressAsync "ArrowLeft")
                do! Wait.until "the reported size to change" reported ((<>) before)
                let! after = reported ()
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
                do! waitVisible "the harness terminal's scrollback" page "#shell [data-terminal-scrollback][data-terminal-id='term-harness']"

                // Nobody holds this terminal, so the holder's report never fires: this is the
                // measurement the model kept, which is the one a command reads.
                let viewport () = Harness.read page HarnessPage.viewport
                do! Wait.until "the pane's size to be measured" viewport Option.isSome
                let! before = viewport ()

                // Widen the column from the keyboard, which dispatches nothing at all.
                do! awaitU (page.FocusAsync "#shell [data-term-resize]")
                for _ in 1 .. 12 do
                    do! awaitU (page.Keyboard.PressAsync "ArrowLeft")
                do! Wait.until "the measured size to change" viewport ((<>) before)
                let! after = viewport ()
                Expect.notEqual after before "the width the next command would claim follows the pane"
            }
        // A chapter's rule is in the FLOW, which is the whole of what replaced the rail: no
        // measurement places it, so what a browser has to settle is not where it was put but
        // that it is where the document says — above the message it opens at, across the
        // reading column, and painted. Every cheap tier reads markup, and markup cannot tell a
        // rule standing over its message from one collapsed to nothing behind it.
        editorCaseIn 1440 900 "a chapter's rule stands above the message it opens, across the column" <| fun page ->
            async {
                do! waitVisible "the chapter's rule" page "#shell [data-chapter-rule='msg-filler-8']"
                let rule = "#shell [data-chapter-rule='msg-filler-8']"
                let item = "#shell [data-conversation] [data-message-id='msg-filler-8']"
                let! ruleAndItem = boxesOf page [ rule; item ]
                let above =
                    match ruleAndItem with
                    | [ r; i ] -> Yession.App.Probes.Box.height r > 0.0 && Yession.App.Probes.Box.width r > 0.0 && r.Bottom <= i.Top
                    | _ -> false
                Expect.isTrue above "the rule stands above its message, with a box of its own"

                // And it is a line somebody can SEE. A border that resolved to nothing, or
                // painted the ground onto the ground, measures exactly as one that works.
                let! borderWidth = Probe.computed page rule "border-top-width"
                let! borderColour = Probe.computed page rule "border-top-color"
                let! ground = Probe.computed page "#shell [data-conversation]" "background-color"
                let painted =
                    (borderWidth |> Option.exists (fun w -> w.EndsWith "px" && float (w.Substring (0, w.Length - 2)) > 0.0))
                    && borderColour <> ground
                Expect.isTrue painted "the rule is a line on the screen, not a border the ground swallowed"

                // It divides the COLUMN, so it is exactly as wide as the column. Narrower and
                // it reads as a mark on one message; wider — which is what a rule with no
                // measure of its own does on a desktop — and it reads as a line drawn on the
                // page, with the conversation happening to sit inside it.
                let! widths = boxesOf page [ rule; item ]
                let spans =
                    match widths with
                    | [ r; i ] -> abs (Yession.App.Probes.Box.width r - Yession.App.Probes.Box.width i) <= 1.0
                    | _ -> false
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
                do! waitVisible "the reply's way to the message it answers" page "#shell [data-reply-jump][data-reply-ref='msg-harness']"
                // To the bottom, where the reply sits, so its source is off the top and the jump is real.
                match! Probe.scroll page "#shell [data-conversation]" with
                | Some conversation ->
                    let! _ = Probe.scrollTo page "#shell [data-conversation]" conversation.Height
                    ()
                | None -> failwith "there is no conversation to scroll"
                do! awaitU (page.ClickAsync "#shell [data-reply-jump][data-reply-ref='msg-harness']")
                do! Wait.until "the message to flash as it is revealed"
                        (fun () -> Read.attr page "#shell [data-conversation] [data-message-id='msg-harness']" "class")
                        (Option.exists (fun classes -> classes.Split ' ' |> Array.contains "animate-reveal"))
                let! onScreen = Probe.hitAt page "#shell [data-conversation] [data-message-id='msg-harness']" 4.0
                let! holds = Read.focused page "#shell [data-conversation] [data-message-id='msg-harness']"
                let landed = onScreen && holds
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
                do! waitVisible "a chapter in the contents" page "#shell [data-chapters] [data-chapter-entry]"
                // Away from it first, so the jump has a real scroll to make.
                match! Probe.scroll page "#shell [data-conversation]" with
                | Some conversation ->
                    let! _ = Probe.scrollTo page "#shell [data-conversation]" conversation.Height
                    ()
                | None -> failwith "there is no conversation to scroll"
                do! awaitU (page.ClickAsync "#shell [data-chapter-entry='msg-harness']")
                do! Wait.until "the message to flash as it is revealed"
                        (fun () -> Read.attr page "#shell [data-conversation] [data-message-id='msg-harness']" "class")
                        (Option.exists (fun classes -> classes.Split ' ' |> Array.contains "animate-reveal"))
                let! focused = Read.focused page "#shell [data-conversation] [data-message-id='msg-harness']"
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
                do! waitVisible "a chapter in the contents" page "#shell [data-chapters] [data-chapter-entry]"
                let! reachable = Probe.hitAt page "#shell [data-conversation] [data-message-id='msg-harness']" 4.0
                let covered = not reachable
                Expect.isTrue covered "the drawer is over the conversation, which is what the phone's contents sit in"

                do! awaitU (page.ClickAsync "#shell [data-chapter-entry='msg-harness']")
                do! Wait.until "the message to flash as it is revealed"
                        (fun () -> Read.attr page "#shell [data-conversation] [data-message-id='msg-harness']" "class")
                        (Option.exists (fun classes -> classes.Split ' ' |> Array.contains "animate-reveal"))
                // Hit-tested at its own centre, which is the only way to ask whether something
                // is over it: a drawer left open leaves the message's box exactly where it was,
                // and every cheap visibility check answers yes.
                do! Wait.until "the drawer to stand aside, so the chapter is on the screen"
                        (fun () -> Probe.hitAt page "#shell [data-conversation] [data-message-id='msg-harness']" 4.0) id
                return ()
            }
        // The contents' entries on a phone are touch targets, so each is one row at the 44px
        // floor and the stack adds nothing between them: a gap on top of a full-height entry
        // read as two chapters a hand apart. Measured rather than read from the markup, since
        // a row's height is what its padding, its line and the stack's gap add up to.
        editorCaseIn 390 844 "on a phone the contents' entries are single 44px rows with nothing between them" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-nav-toggle='show']")
                do! waitVisible "a chapter in the contents" page "#shell [data-chapters] [data-chapter-entry]"
                let! entries = Probe.seen page "#shell [data-chapters] [data-chapter-entry]"
                let boxes = entries |> List.map (fun e -> e.Box)
                let heights = boxes |> List.map Yession.App.Probes.Box.height
                let gaps = List.pairwise boxes |> List.map (fun (a, b) -> b.Top - a.Bottom)
                let shape =
                    [| float boxes.Length
                       (if heights.IsEmpty then 0.0 else List.min heights)
                       (if heights.IsEmpty then 0.0 else List.max heights)
                       (if gaps.IsEmpty then 0.0 else List.max gaps) |]
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
                do! waitVisible "the shell's conversation" page "#shell [data-conversation]"
                // 60 items of streamed replies is ~1,500 events: fifteen pages of a hundred,
                // twenty-five milliseconds apart — three hundred and seventy-five of the five
                // hundred a render is held for.
                let! report = Harness.await3 page HarnessPage.benchOpenCold 60 100 25
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
                do! waitVisible "the shell's conversation" page "#shell [data-conversation]"
                // Two hundred items, a record a frame for four hundred frames — the scroll has
                // to reach the top well inside that, so every frame of it has a render in it.
                do! Harness.run4 page HarnessPage.benchScrollBegin 200 400 16 true
                let! stopped = Probe.reading page Yession.App.Probes.Measure.SmoothScrollToTop
                let reached = match stopped with [ at ] -> at | _ -> failwith "there is no conversation to scroll"
                let! report = Harness.ask page HarnessPage.benchScrollEnd
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
                do! waitVisible "a message in the conversation" page "#shell [data-conversation] [data-message-id]"
                do! Harness.run4 page HarnessPage.benchScrollBegin 0 0 16 true
                let! _ = Harness.ask page HarnessPage.benchScrollEnd
                // Two frames before anything is measured: one for the render the empty
                // scenario asks for, one for it to have landed — the same wait the states walk
                // takes, for the same reason. Without it this reads the conversation the
                // harness opened WITH, which is long enough to fill the column, so `fits`
                // fails and says the layout chose wrongly when nothing had been laid out yet.
                // It only ever lost that race when the suite was busy, which is the worst way
                // for a case to be wrong: green alone, red beside the cases it ships with.
                do! Probe.frames page 2
                let conversation = "#shell [data-conversation]"
                let! measured = boxesOf page [ conversation; conversation + " > :last-child" ]
                let! padding = Probe.computed page conversation "padding-bottom"
                let! scroll = Probe.scroll page conversation
                let over, top =
                    match scroll with
                    | Some s -> s.Height - s.ClientHeight, s.Top
                    | None -> failwith "the conversation's scroll box could not be read"
                // One pixel over, and only one: the timeline always overflows by that much so
                // iOS bounces it even when short (`Style.timeline`). Being scrolled to the end
                // takes `scrollTop` to that pixel, which is why `gap` is measured in the
                // column's content rather than on screen.
                let gap =
                    match measured, padding |> Option.filter (fun p -> p.EndsWith "px") with
                    | [ c; last ], Some padding -> c.Bottom - float (padding.Substring (0, padding.Length - 2)) - top - last.Bottom
                    | _ -> failwith "the conversation, its last item or its padding could not be read"
                Expect.equal over 1.0 "the conversation is shorter than its column but for the pixel it bounces on, so where it ends is a choice"
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
                do! waitVisible "the chapter's name on its rule" page "#shell [data-chapter-name='msg-filler-8']"
                do! awaitU (page.ClickAsync "#shell [data-chapter-name='msg-filler-8']")
                // To the end of what the field holds, so this adds rather than replacing — an
                // edit against the text the session holds is what the field is for, and
                // appending is the edit most likely to expose a diff computed against the
                // wrong side. (A chapter nobody has named holds nothing, so the first words
                // typed here ARE its name.)
                do! awaitU (page.Keyboard.PressAsync "End")
                do! awaitU (page.Keyboard.TypeAsync " — settled")
                do! Wait.until "what was typed to be what the rule says, after the render that followed it"
                        (fun () -> Read.values page "#shell [data-chapter-name='msg-filler-8']")
                        (List.tryHead >> Option.exists (fun v -> v.EndsWith " — settled"))
                return ()
            }
        // The divider of a chapter nobody has named shows no name — the opening message is
        // right under it — so the empty field is how it invites one. The invitation is a
        // promise only a painted page can settle: the placeholder is a pseudo-element whose
        // ink is whatever the cascade resolves, so the markup carries the attribute
        // identically whether it shows at rest, never, or only for a pointer.
        //
        // It must show AT REST. This case used to pin the opposite — absent until a pointer
        // or the keyboard arrived, for a clean divider — and that was the old look pinned as
        // if it were an invariant. The doctrine's principle 4 is the invariant: what a
        // control is, is visible at rest, because a thumb has no hover. An invitation that
        // only a mouse can find is one every touch reader is simply never offered, so what
        // is checked here is that the placeholder is painted with no pointer over the field
        // and no focus on it, and is still painted once focus lands.
        editorCaseIn 1440 900 "an unnamed chapter's field offers a name at rest, before any pointer" <| fun page ->
            async {
                do! waitVisible "the chapter's name on its rule" page "#shell [data-chapter-name='msg-filler-8']"
                let read () =
                    async {
                        match! Probe.reading page Yession.App.Probes.Measure.ChapterPlaceholder with
                        | [ empty; offered; shown ] -> return empty = 1.0, offered = 1.0, shown = 1.0
                        | _ -> return failwith "the chapter's name field is not there"
                    }
                // Away from the field, and not focused: what a thumb and a first glance get.
                do! awaitU (page.Mouse.MoveAsync (700.0f, 10.0f))
                let! empty, offered, atRest = read ()
                Expect.isTrue (empty && offered) "the field is empty and has something to offer, so the next two readings mean something"
                Expect.isTrue atRest "at rest the divider offers the name, where a thumb can see it"
                // And it does not go away once the keyboard arrives — the invitation is still
                // what the empty field is for while you are standing in it.
                do! awaitU (page.FocusAsync "#shell [data-chapter-name='msg-filler-8']")
                do! awaitU (page.Mouse.MoveAsync (700.0f, 10.0f))
                let! _, _, focused = read ()
                Expect.isTrue focused "focus on the empty field keeps the placeholder"
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
                do! waitVisible "the chapter's name on its rule" page "#shell [data-chapter-name='msg-filler-8']"
                // A name to be IN. The rule of a chapter nobody has named is a plain divider
                // with an empty field, and a caret at index 3 and at index 9 of nothing are
                // the same place — so somebody names it first, as a person would.
                do! awaitU (page.ClickAsync "#shell [data-chapter-name='msg-filler-8']")
                do! awaitU (page.Keyboard.TypeAsync "Where it was settled")
                do! Wait.until "the name to be what was typed" (fun () -> Read.values page "#shell [data-chapter-name='msg-filler-8']") ((=) [ "Where it was settled" ])
                do! Harness.run3 page HarnessPage.chapterCaret "msg-filler-8" 3 3
                // Waited for by EXISTENCE, not visibility: a bare caret is a zero-width
                // highlight with the caret bar inside it, which every "is it visible" check
                // in a driver calls hidden.
                do! waitPresent "#shell [data-chapter-rule='msg-filler-8'] [data-cursor-peer='peer:brave-owl'] to be on the page" page "#shell [data-chapter-rule='msg-filler-8'] [data-cursor-peer='peer:brave-owl']"
                let! nameAndMark = boxesOf page [ "#shell [data-chapter-name='msg-filler-8']"; "#shell [data-chapter-rule='msg-filler-8'] [data-cursor-peer='peer:brave-owl']" ]
                let inside =
                    match nameAndMark with
                    | [ n; m ] ->
                        Yession.App.Probes.Box.height m > 0.0
                        && m.Top >= n.Top - 1.0 && m.Bottom <= n.Bottom + 1.0
                        && m.Left >= n.Left - 1.0 && m.Left <= n.Right + 1.0
                    | _ -> false
                Expect.isTrue inside "the caret is drawn inside the field it is a caret in"

                // The same caret further along the name is further along the SCREEN. This is
                // what says the offset is being measured rather than the marker parked at the
                // start of the field, which every check above would pass.
                let caretLeft () =
                    async {
                        let! mark = boxesOf page [ "#shell [data-chapter-rule='msg-filler-8'] [data-cursor-peer='peer:brave-owl']" ]
                        return (List.head mark).Left
                    }
                let! at3 = caretLeft ()
                do! Harness.run3 page HarnessPage.chapterCaret "msg-filler-8" 9 9
                do! Wait.until "a caret later in the name to be drawn further into it" caretLeft (fun left -> left > at3)
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
                do! waitVisible "the chapter's name on its rule" page "#shell [data-chapter-name='msg-filler-8']"
                // The chapter whose name nobody has written, so what the roster says is the
                // heuristic's guess at a whole line of somebody's message — the longest thing
                // this slot can be asked to hold.
                do! Harness.run3 page HarnessPage.chapterCaret "msg-filler-8" 0 0
                do! waitPresent "#shell [data-peer-presence='peer:brave-owl'] [data-peer-at] to be on the page" page "#shell [data-peer-presence='peer:brave-owl'] [data-peer-at]"
                let row = "#shell [data-peer-presence='peer:brave-owl']"
                let! rowAndAt = boxesOf page [ row; row + " [data-peer-at]" ]
                let fits =
                    match rowAndAt with
                    | [ r; at ] -> at.Right <= r.Right + 1.0
                    | _ -> false
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
                do! waitVisible "an item's actions" page "#shell [data-conversation] [data-item-actions]"
                do! awaitU (page.HoverAsync "#shell [data-conversation] [data-message-id='msg-filler-8']")
                let item = "#shell [data-conversation] [data-message-id='msg-filler-8']"
                let! dotsAndBody = boxesOf page [ item + " [data-item-actions]"; item + " [data-message-body]" ]
                let clear =
                    match dotsAndBody with
                    | [ dots; body ] -> dots.Left >= body.Right
                    | _ -> false
                Expect.isTrue clear "the words stop before the actions control begins"
                return ()
            }
        // Second: on a phone the ground is FULL BLEED. A 390px screen has no margin to spend
        // on making a surface look like a card, and a ground inset from both edges of a narrow
        // screen reads as a card rather than as the row it is.
        editorCaseIn 390 844 "a message's ground reaches both edges of a phone screen" <| fun page ->
            async {
                do! waitVisible "a message in the conversation" page "#shell [data-conversation] [data-message-id]"
                let! spare =
                    groundSpare page
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
                do! waitVisible "a message in the conversation" page "#shell [data-conversation] [data-message-id]"
                // The chat column's own width settles over the harness's first animation
                // frames (the pane's `wide:transition-[width]` easing shut on mount, same as
                // every later close): a measurement taken before it ends catches a column
                // narrower than its final, centred width, and `spare` reads short.
                do! Probe.settled page
                let! spare = groundSpare page
                Expect.isTrue
                    (spare > 100.0)
                    (sprintf "the ground runs to within %fpx of a 1440px window" spare)
                return ()
            }
        // A control laid over the timeline lies over words the moment a long line scrolls
        // under it, wherever it is put: centred, the jump covered 22px of a paragraph on a
        // phone, and a stop word at the trailing edge sat on every line that ran the width.
        // So both stand in their own strip under the conversation, and neither reaches into
        // it — measured with both shown at once, a turn running and the reader scrolled back.
        editorCaseIn 390 844 "on a phone the ways to stop a turn and to jump to the latest lie over none of the conversation" <| fun page ->
            async {
                do! Harness.run page HarnessPage.agentTurn
                do! waitVisible "the way to stop the turn" page "#shell [data-interrupt-turn]"
                do! scrolledAwayFromLatest page
                let! over = controlsOverConversation page
                Expect.equal over "" "both controls are on screen and neither lies over the conversation"
            }
        // Interrupt's word stands on the reading rail every message body starts on, its glyph
        // hanging in the gutter left of it (docs/visual-design.md, "Space"). It started on the
        // composer's gutter for a while, with the glyph pushing the word in: 12px short of
        // the line a reader draws down the conversation on a phone.
        let interruptRailCase width height =
            editorCaseIn width height (sprintf "at %dpx interrupt's word starts where the conversation's words do" width) <| fun page ->
                async {
                    do! Harness.run page HarnessPage.agentTurn
                    let body = "#shell [data-conversation] [data-message-body]"
                    let mark = "#shell [data-interrupt-turn] > span"
                    do! waitVisible "interrupt's glyph" page mark
                    let px (v: string option) =
                        match v |> Option.filter (fun p -> p.EndsWith "px") with
                        | Some p -> float (p.Substring (0, p.Length - 2))
                        | None -> failwith "a length could not be read"
                    let! boxes = Probe.boxes page [ body; mark ]
                    let! padding = Probe.computed page body "padding-left"
                    let! gap = Probe.computed page "#shell [data-interrupt-turn]" "column-gap"
                    match boxes with
                    | [ Some bodyBox; Some markBox ] ->
                        let column = bodyBox.Left + px padding
                        let word = markBox.Right + px gap
                        Expect.floatClose Accuracy.low word column
                            (sprintf "interrupt's word starts at %g, the conversation's words at %g" word column)
                    | _ -> failwith "the conversation's first body, or interrupt's glyph, could not be read"
                }
        interruptRailCase 390 844
        interruptRailCase 1440 900
        // The tray takes its own height when a turn starts, and the conversation's box gives
        // it up. A reader who was at the latest message must still be at it, not left with
        // the newest line under the strip.
        editorCaseIn 390 844 "on a phone a turn starting leaves a reader at the latest message at it" <| fun page ->
            async {
                do! waitVisible "a message in the conversation" page "#shell [data-conversation] [data-message-body]"
                do! toTheEnd page "chat"
                do! twoFrames page
                do! Harness.run page HarnessPage.agentTurn
                do! waitVisible "the way to stop the turn" page "#shell [data-interrupt-turn]"
                do! twoFrames page
                let! there = atItsEnd page "chat"
                Expect.isTrue there "the reader is still at the end of the conversation"
            }
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
                    do! Harness.run5 page HarnessPage.snapshot "term-harness" 50 (numberedLines "old" 300 + "\r\nprompt-mark$ ") None None
                    do! awaitU (page.ClickAsync "#shell [data-terminal-take='term-harness']")
                    let! shown = newestShown page "screen:term-harness" "prompt-mark"
                    Expect.isTrue (shownFully shown) (sprintf "the prompt is on screen as the keyboard arrives: %A" shown)
                }
            // A reader at the end of the conversation sees what is said next. The rule used to move
            // them only once the end was more than 200px away, so a message shorter than that
            // landed below the fold — and the next one, and the next, until the shortfall crossed
            // the line: measured on master, 80px short, the newest chip half under the composer.
            editorCaseIn 1440 900 "a reader at the end of the conversation sees the message that lands next" <| fun page ->
                async {
                    do! waitVisible "a message in the conversation" page "#shell [data-conversation] [data-message-body]"
                    do! toTheEnd page "chat"
                    do! messageLands page 200L "tail-mark-chat, which has just been said"
                    let! shown = newestShown page "chat" "tail-mark-chat"
                    Expect.isTrue (shownFully shown) (sprintf "the newest message is wholly on screen: %A" shown)
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
                                  Yession.Domain.Terminals.TerminalBlockStarted.Background = false
                                  Yession.Domain.Terminals.TerminalBlockStarted.Description = None
                                  Yession.Domain.Terminals.TerminalBlockStarted.ToolUseId = None } ]
                    do! Harness.run4 page HarnessPage.record "term-harness" 3 "o" "tail-mark-blocks\r\n"
                    let! shown = newestShown page "blocks:term-harness" "tail-mark-blocks"
                    Expect.isTrue (shownFully shown) (sprintf "the newest line of output is wholly on screen: %A" shown)
                }
            // The agent's command call is drawn as the command from the moment the call starts,
            // and when the command starts its block's chip takes the call's chip's place. Only a
            // rendered page can say the two occupy the same box: the markup of each is right
            // whether or not the swap moves the line under a reader watching it.
            editorCaseIn 1440 900 "a command's chip stays where it stood when its command starts, on a desktop" <| fun page ->
                commandChipHolds page
            editorCaseOnTouch 390 844 "a command's chip stays where it stood when its command starts, on a phone" <| fun page ->
                commandChipHolds page
            // And over the live screen: a program printing below the cursor scrolls the screen, and
            // a reader at its end goes with it.
            editorCaseIn 1440 900 "a reader at the end of a live screen sees the output that arrives next" <| fun page ->
                async {
                    do! paneOnBlocks page
                    do! Harness.run5 page HarnessPage.snapshot "term-harness" 50 (numberedLines "old" 300 + "\r\n") None None
                    do! awaitU (page.ClickAsync "#shell [data-terminal-take='term-harness']")
                    do! waitVisible "the harness terminal's live screen" page (tailSurface "screen:term-harness")
                    do! toTheEnd page "screen:term-harness"
                    do! Harness.run4 page HarnessPage.record "term-harness" 51 "o" "one\r\ntwo\r\ntail-mark-screen\r\n"
                    let! shown = newestShown page "screen:term-harness" "tail-mark-screen"
                    Expect.isTrue (shownFully shown) (sprintf "the newest line on the screen is wholly on screen: %A" shown)
                }
            // The other half: a reader who scrolled back to read is LEFT there. What arrives while
            // they read is what the way back is for, not a reason to take them away from the line
            // they were on.
            editorCaseIn 1440 900 "a reader who scrolled back through the conversation stays where they are as a message lands" <| fun page ->
                async {
                    do! waitVisible "a message in the conversation" page "#shell [data-conversation] [data-message-body]"
                    do! toTheEnd page "chat"
                    do! toTheStart page "chat"
                    do! messageLands page 200L "tail-mark-chat, said while nobody was at the end"
                    do! Wait.until "#shell [data-tail='chat'] to say tail-mark-chat" (fun () -> Read.text page "#shell [data-tail='chat']") (Option.exists (fun t -> t.Contains "tail-mark-chat"))
                    do! twoFrames page
                    let! top = Probe.scroll page "#shell [data-tail='chat']"
                    Expect.equal (top |> Option.map (fun s -> s.Top)) (Some 0.0) "the reader is still at the line they scrolled back to"
                }
            // Leaving the end is not reason enough to be offered the way back to it: a reader a
            // few lines up can see it from where they are. A control appearing for that appears
            // for every nudge of a thumb.
            editorCaseIn 1440 900 "a reader a few lines back from the latest message is not offered the way to it" <| fun page ->
                async {
                    do! waitVisible "a message in the conversation" page "#shell [data-conversation] [data-message-body]"
                    do! toTheEnd page "chat"
                    match! Probe.scroll page "#shell [data-tail='chat']" with
                    | Some chat ->
                        let! _ = Probe.scrollTo page "#shell [data-tail='chat']" (chat.Top - 120.0)
                        ()
                    | None -> failwith "there is no chat surface to scroll"
                    do! twoFrames page
                    do! twoFrames page
                    let! offered = jumpOffered page "chat"
                    Expect.isFalse offered "no way back while the end is a few lines away"
                }
            // …and is told there is more below, over a terminal as over the chat. Output a reader
            // scrolled away from used to arrive with nothing on screen to say so.
            editorCaseIn 1440 900 "a reader who scrolled back through a terminal's blocks is offered the way to the latest" <| fun page ->
                async {
                    do! paneOnBlocks page
                    do! blocksFilled page
                    do! toTheEnd page "blocks:term-harness"
                    do! toTheStart page "blocks:term-harness"
                    do! Wait.until "the way to the latest output to be on screen" (fun () -> jumpOffered page "blocks:term-harness") id
                }
            // Pressing the way back lands at the end, and the keyboard lands with it. The press takes
            // its own control away — a reader at the end has nowhere to jump to — and it used to
            // leave focus on `body`, so the next Tab started from the top of the page.
            editorCaseIn 1440 900 "the jump to the latest message lands at the end with the keyboard on the conversation" <| fun page ->
                async {
                    do! waitVisible "a message in the conversation" page "#shell [data-conversation] [data-message-body]"
                    do! toTheEnd page "chat"
                    do! toTheStart page "chat"
                    let jump = "#shell [data-jump-to-latest='chat'] button"
                    do! waitVisible "the way to the latest message" page jump
                    do! Wait.until "the way to the latest message to be on screen" (fun () -> jumpOffered page "chat") id
                    do! awaitU (page.FocusAsync jump)
                    do! awaitU (page.Keyboard.PressAsync "Enter")
                    do! Wait.until "the conversation to reach its end" (fun () -> atItsEnd page "chat") id
                    let! focused = focusedAttr page "data-tail"
                    Expect.equal focused (Some "chat") "the keyboard is on the conversation it was taken to"
                }
            // The same press over a terminal's blocks, whose scroller is no place for focus of its
            // own: it lands on the panel the blocks are shown in.
            editorCaseIn 1440 900 "the jump to the latest output lands at the end with the keyboard in the pane" <| fun page ->
                async {
                    do! paneOnBlocks page
                    do! blocksFilled page
                    do! toTheEnd page "blocks:term-harness"
                    do! toTheStart page "blocks:term-harness"
                    do! Wait.until "the way to the latest output to be on screen" (fun () -> jumpOffered page "blocks:term-harness") id
                    do! awaitU (page.FocusAsync "#shell [data-jump-to-latest='blocks:term-harness'] button")
                    do! awaitU (page.Keyboard.PressAsync "Enter")
                    do! Wait.until "the blocks to reach their end" (fun () -> atItsEnd page "blocks:term-harness") id
                    let! inPanel = Read.focused page "#shell [data-content-panel], #shell [data-content-panel] *"
                    let! onJump = Read.focused page "[data-jump-to-latest], [data-jump-to-latest] *"
                    let inPane = inPanel && not onJump
                    Expect.isTrue inPane "the keyboard is in the pane, on what holds the blocks it was taken to"
                }
            // A column with less room than one item is not drawn (`Style.timeline`), and when the
            // room comes back its reader is where they left it. Hidden by `display: none` the
            // scroller lost its scroll and came back at its top: on a phone with the keyboard up,
            // the sixth line of a draft took the column under one item and a reader deep in a long
            // conversation to its first message. Only a laid-out page has a scroll to lose; the
            // markup is the same throughout.
            editorCaseIn 390 844 "a conversation given less room than one item keeps its reader's place for when the room comes back" <| fun page ->
                async {
                    do! waitVisible "a message in the conversation" page "#shell [data-conversation] [data-message-body]"
                    let! was = columnTooShort page
                    do! awaitU (page.SetViewportSizeAsync (390, 844))
                    do! Wait.until "the column to have its room back" (fun () -> Probe.scroll page (tailSurface "chat")) (Option.exists (fun s -> s.ClientHeight >= 72.0))
                    do! twoFrames page
                    let! _ = Harness.ask page HarnessPage.benchScrollEnd
                    let! now = Probe.scroll page (tailSurface "chat")
                    Expect.equal (now |> Option.map (fun s -> s.Top)) (Some was) "the reader is where they were before the column went"
                }
            // …and nothing happens to it, frame after frame, while it has no room. With the scroll
            // lost, a column with no box read as at its end, so the model stopped offering the way
            // back, the tray gave its 44px back, the column returned at its top and was measured far
            // back, and the tray took the room again: a render a frame, each laying the whole
            // conversation out, for as long as the draft stayed deep — the "flickers and gets slow"
            // on a phone. The harness counts the app's renders from the scenario's start; a frame's
            // worth of them against a column nothing is happening to is that loop.
            editorCaseIn 390 844 "a conversation given less room than one item is not rendered frame after frame" <| fun page ->
                async {
                    do! waitVisible "a message in the conversation" page "#shell [data-conversation] [data-message-body]"
                    let! _ = columnTooShort page
                    do! Probe.frames page 30
                    let! report = Harness.ask page HarnessPage.benchScrollEnd
                    use doc = System.Text.Json.JsonDocument.Parse report
                    let renders = doc.RootElement.GetProperty("renders").GetInt32 ()
                    // The scroll away and the tray arriving are a render or two. A render a frame
                    // over thirty frames is thirty.
                    Expect.isTrue (renders <= 6) (sprintf "the conversation was rendered %d times while its column had no room" renders)
                }
        ]
        // Dismissing a menu with the keyboard is where focus goes to `body` if nobody puts it
        // back — the failure the WCAG floor names, hit on the very first Escape, and one no
        // rendered string can see: the markup after a close is identical whether the cursor
        // landed on the control or nowhere at all.
        editorCaseIn 1440 900 "Escape closes an item's menu and hands focus back to what opened it" <| fun page ->
            async {
                do! waitVisible "an item's actions" page "#shell [data-conversation] [data-item-actions]"
                do! awaitU (page.ClickAsync "#shell [data-conversation] [data-item-actions]")
                do! waitVisible "the item's menu" page "#shell [data-item-menu]"

                // INTO the menu first. A press leaves focus on the control itself, so Escape
                // from there has nowhere to strand it and the case passes with nothing
                // putting focus back — which is what the first version of this did. The
                // hazard is the entry being removed out from under the cursor.
                do! awaitU (page.Keyboard.PressAsync "Tab")
                do! Wait.until "focus on [data-item-chapter]" (fun () -> Read.focused page "[data-item-chapter]") id

                do! awaitU (page.Keyboard.PressAsync "Escape")
                do! waitAbsent "#shell [data-item-menu] to be gone" page "#shell [data-item-menu]"
                do! Wait.until "focus on [data-item-actions]" (fun () -> Read.focused page "[data-item-actions]") id
                return ()
            }
        // The other door out of the menu, and it strands focus the same way: choosing removes
        // the entry that was pressed. Escape is not this case — a menu can be left by either,
        // and only one of them was putting the cursor back.
        editorCaseIn 1440 900 "choosing from an item's menu hands focus back to what opened it" <| fun page ->
            async {
                do! waitVisible "an item's actions" page "#shell [data-conversation] [data-item-actions]"
                do! awaitU (page.ClickAsync "#shell [data-conversation] [data-item-actions]")
                do! waitVisible "the menu's way to open a chapter here" page "#shell [data-item-chapter]"

                do! awaitU (page.Keyboard.PressAsync "Tab")
                do! Wait.until "focus on [data-item-chapter]" (fun () -> Read.focused page "[data-item-chapter]") id
                do! awaitU (page.Keyboard.PressAsync "Enter")

                do! waitAbsent "#shell [data-item-menu] to be gone" page "#shell [data-item-menu]"
                do! Wait.until "focus on [data-item-actions]" (fun () -> Read.focused page "[data-item-actions]") id
                return ()
            }
        // A control lifted by hover is a control a keyboard never sees lifted unless focus
        // lifts it too, and a faint rest keeps it in the tab order either way — so the failure
        // is not an unreachable control but a FAINT one that is nonetheless the focused thing.
        // Tabbed to for real, because `:focus-visible` is exactly the rule that does not fire
        // for a programmatic `focus()`.
        editorCaseIn 1440 900 "an item's actions show themselves to a keyboard that reaches them" <| fun page ->
            async {
                do! waitVisible "an item's actions" page "#shell [data-conversation] [data-item-actions]"
                let mutable reached = false
                let mutable steps = 0
                while not reached && steps < 60 do
                    do! awaitU (page.Keyboard.PressAsync "Tab")
                    steps <- steps + 1
                    let! here = Read.focused page "[data-item-actions]"
                    reached <- here
                Expect.isTrue reached (sprintf "a keyboard reaches an item's actions (gave up after %d tabs)" steps)

                // WAITED for, never read once: the control fades in, and a computed opacity
                // sampled on the frame focus landed is the value the transition STARTED from.
                // Read that way this passed nothing and failed everything — the first version
                // of this case reported `0` against a control that was on its way to 1.
                do! Wait.until "the focused actions control to fade fully in"
                        (fun () -> Probe.computed page "#shell [data-item-actions]:focus-visible" "opacity") ((=) (Some "1"))
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
                do! waitVisible "an item's actions" page "#shell [data-conversation] [data-item-actions]"
                let! noHover = Probe.media page "(hover: none)"
                Expect.isTrue noHover "the page is laid out for a device that cannot hover"
                let! asked = Read.count page "#shell [data-conversation] [data-item-actions]"
                Expect.isTrue (asked > 1) (sprintf "the fixture draws a run of items to ask about (drew %d)" asked)
                // Settled first. On a phone the terminals pane slides off to the side as the
                // shell lays out, and a hit-test taken while it is on its way lands on the pane
                // rather than on the column under it — the screen not having arrived yet, not a
                // control a thumb cannot reach. Every animation that ends is waited for (a pulse
                // or a blink never ends, and is not).
                do! Probe.settled page
                let! unreachable = Probe.verdict page Yession.App.Probes.Measure.ItemActionsUnderThumb
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
                do! waitVisible "the composer" page "#shell [data-draft-editor]"
                do! Harness.run page HarnessPage.agentTurn
                do! waitVisible "the agent's turn, in flight" page "#shell [data-agent-writing]"
                // The shell into the viewport, then the timeline to its end — where a turn that
                // has just started is. Both, because this harness page stacks its mounts and
                // the shell is not the top of it: a mark below the fold is invisible for a
                // reason this case is not about, and would fail it while the design was right.
                // (Measured: without the first scroll the caret sits at y=1433 on an 844px
                // screen, and every hit-test answers null.)
                do! Probe.reveal page "#shell"
                match! Probe.scroll page "#shell [data-conversation]" with
                | Some conversation ->
                    let! _ = Probe.scrollTo page "#shell [data-conversation]" conversation.Height
                    ()
                | None -> failwith "there is no conversation to scroll"
                let! marks = Probe.paintedCount page "#shell [data-agent-writing]"
                Expect.equal marks 1 "the agent's turn is marked once on the screen"

                // The other half of "once": the sentence a screen reader is told must not also
                // be a thing anybody sees, or the count above is 1 and the screen still says it
                // twice. A visually-hidden region occupies no readable area — which is what
                // `sr-only` MEANS, and the one property every correct spelling of it shares.
                let! spoken = boxesOf page [ "#shell [data-agent-stream]" ]
                let shown = spoken |> List.sumBy (fun r -> Yession.App.Probes.Box.width r * Yession.App.Probes.Box.height r)
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
                do! waitVisible "the composer" page "#shell [data-draft-editor]"
                do! Harness.run page HarnessPage.agentTurn
                do! waitVisible "the agent's turn, in flight" page "#shell [data-agent-writing]"
                let! misplaced = Probe.verdict page Yession.App.Probes.Measure.AgentCaretOnLetters
                Expect.isEmpty misplaced "the caret stands where the next word lands"
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
                do! waitVisible "the composer" page "#shell [data-draft-editor]"
                do! Harness.run page HarnessPage.agentThinks
                do! waitVisible "the agent to start thinking" page "#shell [data-agent-writing]"
                let! outside = Probe.verdict page Yession.App.Probes.Measure.ThinkingMarkInsideBox
                Expect.isEmpty outside "the thinking mark stays inside the box it is placed by"
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
                do! waitVisible "the composer" page "#shell [data-draft-editor]"
                do! Harness.run page HarnessPage.agentThinks
                do! waitVisible "the agent to start thinking" page "#shell [data-agent-writing]"
                // Remembered, said and measured in one evaluation, so the arrival is caught at its
                // start however slow the round trip to the page is.
                let! apart = Probe.verdict page Yession.App.Probes.Measure.FirstWordFromOpening
                Expect.isEmpty apart "the caret starts from the opening mark"
                return ()
            }
        // What the band it replaced could not take away, and what a control arriving in a band
        // can: the composer. A turn starting must not push what a person types with off the
        // screen, or the one thing to do while the agent writes — queue the next message — is
        // gone exactly when it is wanted.
        editorCaseIn 390 844 "a turn starting never costs the composer its line or its send" <| fun page ->
            async {
                do! waitVisible "the composer" page "#shell [data-draft-editor]"
                do! Harness.run page HarnessPage.agentTurn
                do! waitVisible "the way to stop the turn" page "#shell [data-interrupt-turn]"
                let! window = Probe.window page
                let! controls = Probe.boxes page [ "#shell [data-draft-input]"; "#shell [data-send-draft]"; "#shell [data-interrupt-turn]" ]
                let whole =
                    controls
                    |> List.forall (function
                        | Some b -> not (Yession.App.Probes.Box.isEmpty b) && b.Left >= 0.0 && b.Right <= window.InnerWidth
                        | None -> false)
                Expect.isTrue whole "the line, its send, and the new stop control all fit the phone at once"
                return ()
            }
        // On a phone Send shares its line with the model picker, which holds that line's
        // height whether Send shows or not — so the first character of a draft must move
        // nothing. It used to: Send's row grew from nothing and gained a pad as it came, and
        // measured frame by frame the draft line rose 4px, the picker 2px, and Send dropped
        // 2px before rising, all of it undone in one frame when the draft was emptied. What
        // is measured is where the line, the picker and the band's top edge stand, before
        // and after: a restyle may put them anywhere, but a draft beginning may not move them.
        editorCaseIn 390 844 "on a phone the composer stands still as a draft begins" <| fun page ->
            async {
                do! awaitU (page.EmulateMediaAsync (PageEmulateMediaOptions (ReducedMotion = ReducedMotion.Reduce)))
                let line = """#shell [data-draft-input] .ProseMirror"""
                do! waitVisible "the composer's draft line" page line
                // In the document's coordinates: reaching the line can scroll the page.
                let where () =
                    async {
                        let! tops = Probe.reading page Yession.App.Probes.Measure.ComposerTops
                        return tops |> List.map (fun top -> floor (top * 10.0 + 0.5) / 10.0)
                    }
                let! empty = where ()
                do! awaitU (page.ClickAsync line)
                do! awaitU (page.Keyboard.TypeAsync "s")
                do! waitVisible "Send, ready for the draft" page """#shell [data-send-draft][data-send-ready="yes"]"""
                let! drafted = where ()
                Expect.equal drafted empty "the draft line, the picker and the composer's edge stand where they stood"
            }
        // With nothing to send, Send still stands where a thumb reaches it: the one verb the
        // composer is for, at the weight of a thing with nothing to do (`btnComposerSendWaiting`).
        // It used to leave with an empty draft, and on a phone arrived only once there was
        // already something to send.
        //
        // This case said the opposite — no Send to press — for as long as the harness kept its
        // fixtures above the shell: at 390x844 the composer stood below the screen, so Send was
        // never painted there, and the old promise held whatever the composer did.
        editorCaseIn 390 844 "on a phone an empty composer's Send stands where a press reaches it" <| fun page ->
            async {
                do! waitVisible "Send, with nothing to send" page """#shell [data-send-draft][data-send-ready="no"]"""
                let! send = Probe.seen page "#shell [data-send-draft]"
                Expect.isTrue (send |> List.exists (fun s -> s.Painted)) "a press where Send stands reaches it"
            }
        // Attaching a file is open to the keyboard: Tab reaches the way to attach, and Enter
        // opens the browser's chooser for it. The chooser used to be opened by a button that
        // reached into the DOM for a `hidden` input and clicked it; the input is now the
        // control, inside the label that is its face. An input out of the focus order
        // (`hidden`, `display: none`) is one Tab steps over, and no markup test can see that.
        editorCaseIn 1440 900 "the keyboard reaches the way to attach a file and opens its chooser" <| fun page ->
            async {
                let attach = "#shell " + Sel.hook Yession.App.Dom.Hooks.draftAttachInput
                // Waited for from the start. Asking for the chooser is what tells Chromium to
                // hand it to Playwright rather than open its own, and that request travels on
                // its own: asked for in the same breath as Enter, under a loaded runner the
                // press landed first and the chooser never reached the case.
                let chooser = page.WaitForFileChooserAsync ()
                do! waitVisible "the model picker, just before the way to attach" page "#shell [data-model-select]"
                do! awaitU (page.FocusAsync "#shell [data-model-select]")
                do! awaitU (page.Keyboard.PressAsync "Tab")
                do! Wait.until "Tab to reach the way to attach a file" (fun () -> Read.focused page attach) id
                do! awaitU (page.Keyboard.PressAsync "Enter")
                let! chooser = Async.AwaitTask chooser
                let! opener = Async.AwaitTask (chooser.Element.GetAttributeAsync Yession.App.Dom.Hooks.draftAttachInput)
                Expect.isNotNull opener "Enter opened the chooser of the way to attach a file"
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
                do! waitVisible "the composer's draft line" page line
                do! awaitU (page.ClickAsync line)
                do! awaitU (page.Keyboard.TypeAsync "something to send")
                do! waitVisible "Send, ready for the draft" page """#shell [data-send-draft][data-send-ready="yes"]"""
                // The shell into the viewport first: this harness page stacks its mounts and
                // the shell is not the top of it, so the composer sits below the fold and a
                // hit-test in VIEWPORT coordinates answers null for a reason this case is
                // not about. (Measured: without it, `elementFromPoint` said `nothing` while
                // the row was standing exactly where it should.)
                do! Probe.reveal page "#shell"
                // The blur IS the gesture under test: it is what a tap on a button does
                // first, on every browser that does not focus one.
                do! awaitU (page.Locator(":focus").BlurAsync ())
                // …and then the composer is left to finish reacting to it. The hit-test aims
                // at a point in VIEWPORT coordinates, so it is a question about stacking only
                // once the box it aims at has stopped moving: read mid-collapse it answers
                // about wherever Send was passing through, or — if the row is between renders
                // and the box is empty — about the origin of the page, which in this stacked
                // harness is another mount's field entirely. That is what the flake was: an
                // `INPUT` from the terminal command line answering for a Send nobody had
                // finished laying out. A settled box is a question about the code; an
                // unsettled one is a question about how fast this machine re-rendered.
                let sendBox () =
                    async {
                        let! box = Probe.boxes page [ "#shell [data-send-draft]" ]
                        return box |> List.head |> Option.filter (Yession.App.Probes.Box.isEmpty >> not)
                    }
                let last = ref None
                do! Wait.until "Send's box to settle after the blur" sendBox (fun now ->
                        let settled = now.IsSome && now = last.Value
                        last.Value <- now
                        settled)
                // On failure it says WHERE both were, because "an INPUT answered" alone cannot
                // tell an overlay from a hit-test aimed at the wrong point.
                let! answered = Probe.verdict page Yession.App.Probes.Measure.SendAnswers
                Expect.isEmpty answered "a press at Send's own centre reaches Send once the editor has let focus go"
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
                do! waitVisible "the composer's draft line" page line
                let height () =
                    async {
                        let! box = boxesOf page [ "#shell [data-draft-input]" ]
                        return Yession.App.Probes.Box.height box.Head
                    }
                let! oneLine = height ()
                do! awaitU (page.ClickAsync line)
                // Three paragraphs, typed with real keys: what a person writes when they say
                // more than fits, and more than the open composer's own cap so the two states
                // are genuinely different heights.
                for word in [ "first"; "second"; "third"; "fourth" ] do
                    do! awaitU (page.Keyboard.TypeAsync word)
                    do! awaitU (page.Keyboard.PressAsync "Enter")
                let! opened = height ()
                do! awaitU (page.Locator(":focus").BlurAsync ())
                let! collapsed = height ()
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
                do! waitVisible "the live terminal's screen" page "#shell [data-terminal-screen='term-live']"

                // Watching replaces the live screen with the recording, in a real player.
                do! awaitU (page.ClickAsync "#shell [data-terminal-watch='watch']")
                do! waitVisible "the recording's player, in place of the live screen" page "#shell [data-pane-replay='terminal:term-live'] .ap-player"
                do! waitAbsent "#shell [data-terminal-screen='term-live'] to be gone" page "#shell [data-terminal-screen='term-live']"

                // One control in one slot, relabelled — so the press KEEPS its own focus.
                // There used to be four controls swapping each other out of the document, and
                // every press had to hand focus on after itself or strand it on `body`.
                do! Wait.until "focus on [data-terminal-watch='live']" (fun () -> Read.focused page "[data-terminal-watch='live']") id

                // It lands a moment ago: the screen as it stood there, shown before anyone
                // presses play — not a blank player parked at 0:00, and not the pinned end, which
                // has nothing after it to play.
                do! waitTextIn "the rewound screen to land" page rewoundMount "earlier output"
                let! drawn = Read.text page rewoundMount
                let landed = drawn |> Option.defaultWith (fun () -> failwith "the rewound player went as it was read")
                Expect.stringContains landed "line 3" "the screen as it stood at the landing"
                Expect.isFalse (landed.Contains "line 7") "which is short of the pin: there is something left to play"

                // Playing off the pinned end IS catching up: the player's `ended` drops the
                // rewind by itself. Nobody pressed anything, so this is the one case that
                // still hands focus on — the player being read is unmounted under the reader.
                do! awaitU (page.ClickAsync "#shell [data-pane-replay='terminal:term-live'] .ap-overlay-start")
                do! waitVisible "the live screen, back" page "#shell [data-terminal-screen='term-live']"
                do! waitAbsent "#shell [data-pane-replay='terminal:term-live'] to be gone" page "#shell [data-pane-replay='terminal:term-live']"
                do! Wait.until "focus on [data-terminal-watch='watch']" (fun () -> Read.focused page "[data-terminal-watch='watch']") id

                // And by hand too: watch again, live again — the same control both times.
                do! awaitU (page.ClickAsync "#shell [data-terminal-watch='watch']")
                do! waitVisible "the recording's player, in place of the live screen" page "#shell [data-pane-replay='terminal:term-live'] .ap-player"
                do! awaitU (page.ClickAsync "#shell [data-terminal-watch='live']")
                do! waitVisible "the live screen, back" page "#shell [data-terminal-screen='term-live']"
                do! waitAbsent "#shell [data-pane-replay='terminal:term-live'] to be gone" page "#shell [data-pane-replay='terminal:term-live']"
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
                do! waitVisible "the rewound player's start button" page (rewoundMount + " .ap-overlay-start")
                let! without = Probe.verdict page Yession.App.Probes.Measure.PlayCarriesOn
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
                do! waitVisible "the live terminal's command line" page line
                do! awaitU (page.ClickAsync line)
                do! awaitU (page.Keyboard.TypeAsync "git sta")
                do! waitCommandLine page line "git sta"

                do! awaitU (page.ClickAsync "#shell [data-terminal-watch='watch']")
                do! waitVisible "the bar that says the terminal is rewound" page "#shell [data-terminal-rewound='term-live']"
                let! onScreen = Read.count page "#shell [data-terminal-input]:not([readonly])"
                Expect.equal onScreen 0 "no command line to type into while watching a recording"

                // The bar's own press is the way back, and it hands focus on to the toggle
                // that replaces it rather than stranding it on `body`.
                do! awaitU (page.ClickAsync "#shell [data-terminal-rewound-live]")
                do! waitAbsent "#shell [data-terminal-rewound] to be gone" page "#shell [data-terminal-rewound]"
                do! waitCommandLine page line "git sta"
                do! Wait.until "focus on the toggle that replaced the bar" (fun () -> Read.focused page "[data-terminal-watch='watch']") id
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
                do! waitVisible "the harness terminal's scrollback" page "#shell [data-terminal-scrollback][data-terminal-id='term-harness']"
                // The column OPENS: its width transitions from nothing while the pane inside it
                // is already its full width, so for 200ms the scrollback is clipped by the column
                // and its middle is off the right edge of the window — where a hit-test finds
                // nothing at all. Measured from the click on this box, the column was 110px of
                // its 420. A quick box had always finished opening by the time the case looked;
                // a loaded runner had not, and read as a command line drawn under something.
                // So wait for the column to have opened, and for the focus it hands over on
                // landing (which scrolls the page to it), before anything is measured.
                do! Wait.until "the content column to have opened" (fun () -> Probe.verdict page Yession.App.Probes.Measure.ColumnOpened) List.isEmpty
                // Three hundred lines into the running command — one record, so one render.
                do! Harness.run4 page HarnessPage.record "term-harness" 2 "o" (numberedLines "line" 300)
                // The history draws a block's last lines until it is opened, so a block tall
                // enough to scroll through is an OPENED one: ask for all of it.
                do! awaitU (page.ClickAsync "#shell [data-terminal-block=block-burst-running] [data-terminal-output-expand]")
                // Scroll until the middle of that output is at the top of the scrollback.
                do! Wait.until "the burst's middle at the top of the scrollback" (fun () -> Probe.verdict page Yession.App.Probes.Measure.BurstMiddleToTop) List.isEmpty
                // What is painted at the row's centre is NAMED when it is not the row, so a red
                // says what covered it rather than only that something did.
                let! held = Probe.verdict page Yession.App.Probes.Measure.CommandRowHeld
                Expect.isEmpty held "the command line is held at the top, and is what is drawn there"
            }

        // What a block's output costs the page. A template per line was a template instance,
        // its markers and its text — some ten nodes a line — so `seq 100000` drew a million
        // nodes, and every render after that walked them. Plain output is one run of text
        // however many lines it has, so it costs a handful of nodes; this counts them, in the
        // one place that can — the DOM a browser actually built.
        editorCase "plain output costs a block a handful of nodes, however many lines it has" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
                do! waitVisible "the harness terminal's scrollback" page "#shell [data-terminal-scrollback][data-terminal-id='term-harness']"
                do! Harness.run4 page HarnessPage.record "term-harness" 2 "o" (numberedLines "line" 1500)
                // Drawn by the frame's end, not necessarily before the call returns: the page
                // draws at most once a frame (`Render.setState`).
                do! twoFrames page
                match! Probe.reading page Yession.App.Probes.Measure.OutputNodes with
                | [] -> failwith "no output drawn"
                // Anti-vacuity: the lines are really there to be counted.
                | [ _; 0.0 ] -> failwith "the last line is not drawn"
                | nodes :: _ ->
                    Expect.isTrue (nodes <= 16.0) (sprintf "fifteen hundred plain lines drew %.0f nodes, not a handful" nodes)
            }

        // "Show in terminal" (Plan 25, stage 3). The reader's context question, answered with
        // text: the terminal's own history, scrolled to the command they came from. Only a
        // browser can say whether it actually SCROLLED — and whether that scroll survives the
        // render which returns a freshly rendered scrollback to its end, the one thing this
        // could quietly lose to.
        editorCase "showing a command in its terminal scrolls the history to it" <| fun page ->
            async {
                do! waitVisible "the block's chip in the chat" page "#shell [data-chat-block]"
                do! awaitU (page.ClickAsync "#shell [data-chat-block]")
                do! waitVisible "the preview's way back to its terminal" page "#shell [data-pane-preview-close]"
                do! awaitU (page.ClickAsync "#shell [data-pane-preview-close]")

                // The pane moved to the terminal's own text.
                do! Wait.until "the pane on a terminal" (fun () -> Read.attr page "#shell [data-pane-panel]" "data-pane-panel") (Option.exists (fun p -> p.StartsWith "terminal:"))

                // …and the command is inside the scroller's viewport rather than somewhere
                // off it. A position promise, measured off the real boxes — not a style, and
                // not a claim about which pixel it landed on.
                do! Wait.until
                        "the command inside the scroller's viewport"
                        (fun () -> Probe.boxes page [ "#shell [data-terminal-scrollback]"; "#shell [data-terminal-scrollback] [data-terminal-block=block-harness]" ])
                        (function
                         | [ Some a; Some b ] -> b.Top >= a.Top - 1.0 && b.Top < a.Bottom
                         | _ -> false)

                // Focus went back to the chip the preview was opened from, as a preview's close
                // gives it: the pane beside the chat now shows where that chip came from.
                do! Wait.until "focus on [data-chat-block]" (fun () -> Read.focused page "[data-chat-block]") id
                return ()
            }

        // The same press, for a command that is behind a shut run ("ran 2 earlier commands"). It
        // landed on the terminal with everything folded: nothing scrolled to, nothing marked,
        // the promise of the button broken. Only a browser can see the run open, the command
        // inside the scroller's box and its mark playing.
        editorCase "showing a folded command in its terminal opens its run and marks it" <| fun page ->
            async {
                do! waitVisible "the task card in the chat" page "#shell [data-chat-task-card]"
                do! awaitU (page.ClickAsync "#shell [data-chat-task-card] [data-fold]")
                do! waitVisible "the folded command's chip, its run opened" page "#shell [data-chat-task-card] [data-chat-block='block-burst-failed']"
                do! awaitU (page.ClickAsync "#shell [data-chat-task-card] [data-chat-block='block-burst-failed']")
                do! waitVisible "the preview's way back to its terminal" page "#shell [data-pane-preview-close]"
                do! awaitU (page.ClickAsync "#shell [data-pane-preview-close]")

                // One reading, so a red names every part that did not hold rather than the
                // first wait that timed out. The mark is an animation that ENDS, so it is read
                // while it plays: the wait settles on all four at once.
                do! Wait.until
                        "the run open, the command in view, marked, and focus back on the chip"
                        (fun () -> Probe.verdict page Yession.App.Probes.Measure.FoldedCommandLanded)
                        List.isEmpty
            }

        // A command the kernel killed for memory wears the cause, not a bare 137, wherever a
        // block's status is drawn. The chip says it in the chat.
        editorCase "a block killed for memory says so on its chip" <| fun page ->
            async {
                let chip = "#shell [data-chat-task-card] [data-chat-block='block-killed-memory']"
                do! waitVisible "the task card in the chat" page "#shell [data-chat-task-card]"
                do! awaitU (page.ClickAsync "#shell [data-chat-task-card] [data-fold]")
                do! waitVisible "the killed block's chip, its run opened" page chip
                do! Wait.until "the chip to name the memory kill" (fun () -> Read.text page chip) (Option.exists (fun t -> t.Contains "killed: out of memory"))
            }

        // And the block in its terminal says which of the two causes it was. Reached the way
        // a reader reaches it: the chip opens a preview, and closing the preview shows the
        // command in its terminal, its run opened.
        editorCase "a block killed for memory says in its terminal that the machine ran out" <| fun page ->
            async {
                do! waitVisible "the task card in the chat" page "#shell [data-chat-task-card]"
                do! awaitU (page.ClickAsync "#shell [data-chat-task-card] [data-fold]")
                do! waitVisible "the killed block's chip, its run opened" page "#shell [data-chat-task-card] [data-chat-block='block-killed-memory']"
                do! awaitU (page.ClickAsync "#shell [data-chat-task-card] [data-chat-block='block-killed-memory']")
                do! waitVisible "the preview's way back to its terminal" page "#shell [data-pane-preview-close]"
                do! awaitU (page.ClickAsync "#shell [data-pane-preview-close]")
                let fact = "#shell [data-terminal-block='block-killed-memory'] [data-terminal-block-memory='machine']"
                do! waitVisible "the block's word on what ran out of memory" page fact
                do! Wait.until "the block to name the cause" (fun () -> Read.text page fact) (Option.exists (fun t -> t.Contains "the machine had no free memory"))
            }

        // A command and its status share a line only while the command keeps the line. With
        // "refused by <name>" beside it the command had a third of a phone's column and broke
        // mid-word in it; the status now goes under, and the command takes the width. What is
        // measured is the command's share of its row and that nothing leaves the page sideways
        // — not where the status is drawn, which is the design's to move.
        editorCaseIn 390 844 "a refused block's command keeps its row's width on a phone" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
                do! waitVisible "the refused block's mark" page "#shell [data-terminal-block='block-refused-long'] [data-block-mark]"
                let row = "#shell [data-terminal-block=block-refused-long] [data-terminal-block-command]"
                let! measured = boxesOf page [ row; row + " code" ]
                let r, c = measured.[0], measured.[1]
                let! sideways = overflowsSideways page
                Expect.isTrue (c.Left >= r.Left - 1.0 && c.Right <= r.Right + 1.0) "the command stays inside its row"
                Expect.isFalse sideways "the page does not scroll sideways"
                let share = px (100.0 * Yession.App.Probes.Box.width c / Yession.App.Probes.Box.width r)
                Expect.isTrue (share >= 60) (sprintf "the command has most of its row, not what a long status leaves (%d%%)" share)
            }

        // A run a person opened is theirs to shut. It was the `<details>` element's own state,
        // so anything that rebuilt the terminal's history — a preview laid over it and taken
        // down again — rebuilt it shut. The open is native (a click on the summary), which is
        // the half no rendered string can see reaching the model.
        editorCase "a run opened by hand is still open after the terminal is shown again" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
                do! waitVisible "the finished run's summary" page "#shell [data-terminal-block-run='block-burst-ok'] summary"
                do! awaitU (page.ClickAsync "#shell [data-terminal-block-run='block-burst-ok'] summary")
                do! waitPresent "the run to open" page "#shell [data-terminal-block-run='block-burst-ok'] details[open]"

                // Away to a preview, which takes the history out of the document, and back.
                do! awaitU (page.ClickAsync "#shell [data-chat-block='block-harness']")
                do! waitVisible "the preview's way back to its terminal" page "#shell [data-pane-preview-close]"
                do! waitAbsent "#shell [data-terminal-block-run='block-burst-ok'] to be gone" page "#shell [data-terminal-block-run='block-burst-ok']"
                do! awaitU (page.ClickAsync "#shell [data-pane-preview-close]")
                do! waitVisible "the run, back in the terminal's history" page "#shell [data-terminal-block-run='block-burst-ok'] details"

                do! Probe.frames page 1
                let! opened = Read.count page "#shell [data-terminal-block-run='block-burst-ok'] details[open]"
                Expect.equal opened 1 "open as the reader left it"
            }

        // Opening a fold of earlier commands with a pointer, from the end of the history, leaves
        // the latest command where the reader was looking at it: what the fold holds came before,
        // so it appears ABOVE. It used to push the latest command off the bottom of the pane.

        editorCaseIn 1440 900 "clicking open the earlier commands leaves the latest command where a reader at the end saw it" <| fun page ->
            async {
                do! paneOnBlocks page
                let! run = atTheEndWithRunOnScreen page
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
                do! Harness.run4 page HarnessPage.record "term-harness" 2 "o" (numberedLines "line" 300)
                let block = sprintf "%s [data-terminal-block='block-burst-running']" (tailSurface "blocks:term-harness")
                let expand = block + " [data-terminal-output-expand]"
                do! waitVisible "the block's way to show all its lines" page expand
                do! toTheEnd page "blocks:term-harness"
                do! scrollToTop page expand
                let! before = topOf page (block + " + *")
                do! clickWhereItIs page expand
                do! Wait.until "the output to open" (fun () -> Read.attr page expand "aria-expanded") ((=) (Some "true"))
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
                let! run = atTheEndWithRunOnScreen page
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
                let! focused = Read.focused page (runLine run)
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
                do! waitVisible "the queued command's chip" page "#shell [data-chat-pending]"
                do! Wait.until "#shell [data-chat-pending] to say npm run lint" (fun () -> Read.text page "#shell [data-chat-pending]") (Option.exists (fun t -> t.Contains "npm run lint"))

                do! awaitU (page.ClickAsync "#shell [data-chat-pending]")

                // The pane is on the terminal it is queued in — not some terminal, that one.
                do! Wait.until "#shell [data-pane-panel] to have data-pane-panel terminal:term-harness" (fun () -> Read.attr page "#shell [data-pane-panel]" "data-pane-panel") ((=) (Some "terminal:term-harness"))
                // Focus followed it in, as it does for a block's chip: the reader was moved,
                // so their keyboard was too — onto the terminal's own command line, which is
                // where a terminal takes what they type.
                do! Wait.until "focus on the harness's command line" (fun () -> Read.focused page (commandLine "term-harness")) id
                // And what they arrived at is the card that can answer it.
                do! waitVisible "the queued command's card in its terminal" page "#shell [data-terminal-queued='queue-harness']"
                return ()
            }

        // A preview (P2-1): what a chip opens is laid over the terminal, never a tab beside it,
        // and its way back hands focus to the chip — the UI floor's rule about a DOM swap that
        // removes what had focus, asked of the one surface that removes itself on Escape. These
        // replace two cases about pins, which no longer exist.
        editorCase "a chip opens a preview and Escape returns focus to the chip" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
                do! Wait.until "focus on the harness's command line" (fun () -> Read.focused page (commandLine "term-harness")) id
                let countTabs () = Read.count page "#shell [data-terminal-tab], #shell [data-terminal-closed-tab]"
                let! before = countTabs ()
                do! awaitU (page.ClickAsync "#shell [data-chat-block]")
                do! waitVisible "the chip's preview in the pane" page "#shell [data-pane-preview]"
                let! during = countTabs ()
                Expect.equal during before "the strip holds the same terminals, and no tab for the preview"
                do! focusReachedPane page
                do! awaitU (page.Keyboard.PressAsync "Escape")
                do! waitAbsent "the preview to be gone" page "#shell [data-pane-preview]"
                do! Wait.until "focus to be back on the chip" (fun () -> Read.focused page "[data-chat-block]") id
            }

        // The preview's close, in the line under the pivot that names it (F3), is what hands
        // focus back to where it was opened from — the same act as Escape.
        editorCase "a preview's close returns focus to the chip" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-chat-block]")
                do! waitVisible "the preview's close" page "#shell [data-pane-preview-close]"
                do! focusReachedPane page
                do! awaitU (page.Locator("#shell [data-pane-preview-close]").First.PressAsync "Enter")
                do! waitAbsent "the preview to be gone" page "#shell [data-pane-preview]"
                do! Wait.until "focus to be back on the chip" (fun () -> Read.focused page "[data-chat-block]") id
            }

        // A preview is a layer OF its terminal, not a tab beside it (F3). It used to have an
        // item of its own in the strip — `$ seq 1 40`, slanted, with its own × — so the strip
        // mixed two kinds of thing that looked alike, a phone's strip with room for three tabs
        // spent one on it, and it stayed there over the `all` page. At a phone's width, where
        // the room is the cost.
        editorCaseIn 390 844 "on a phone, a preview leaves the strip to the terminals and all" <| fun page ->
            async {
                do! previewSettled page
                let tabs = "#shell [data-pane-pivot] [role=tab]"
                let! switchers = Read.attrs page tabs "data-pane-switcher"
                let! panes = Read.attrs page tabs "data-pane-tab"
                let items =
                    List.zip switchers panes
                    |> List.map (function
                        | Some _, _ -> "all"
                        | None, Some pane when pane.StartsWith "terminal:" -> "terminal"
                        | None, pane -> sprintf "a tab for %A" pane)
                Expect.isTrue (List.contains "terminal" items) "the terminal under the preview is in the strip"
                let others = items |> List.filter (fun item -> item <> "all" && item <> "terminal")
                Expect.isEmpty others (sprintf "every tab in the strip is a terminal or all; also: %s" (String.Join (" | ", others)))
            }

        // Measured where a thumb lands: each one's centre hit-tested, so a name clipped to
        // nothing or a control under something else is not "reachable" — and the two presses
        // a thumb's 44, the floor the strip's own items hold. The close is a command preview's
        // one exit, and it is also the way to the command in its terminal's history.
        editorCaseIn 390 844 "on a phone, a preview's name and its close are on the screen" <| fun page ->
            async {
                do! previewSettled page
                let! window = Probe.window page
                let faultsOf (what: string) (selector: string) (floor: float) =
                    async {
                        match! Probe.seen page ("#shell " + selector) with
                        | [] -> return [ what + ": not rendered" ]
                        | seen :: _ ->
                            let width = Yession.App.Probes.Box.width seen.Box
                            let height = Yession.App.Probes.Box.height seen.Box
                            return
                                [ if not seen.Painted then what + ": not on screen"
                                  if seen.Box.Right > window.InnerWidth + 0.5 then what + ": past the edge"
                                  if width < floor - 0.5 || height < floor - 0.5 then sprintf "%s: %dx%d" what (px width) (px height) ]
                    }
                let! name = faultsOf "name" "[data-pane-preview-name]" 0.0
                let! close = faultsOf "close" "[data-pane-preview-close]" 44.0
                let faults = name @ close
                Expect.isEmpty faults (sprintf "the name is visible and the close is a 44px target: %s" (String.Join (", ", faults)))
            }

        // Nothing in a block preview pushes the column wider than the phone. The harness's
        // terminal carries the longest title an agent gives one, and the action row used to
        // put it on a control (`show in <title>`) that does not wrap — 557px of content in a
        // 390px pane, the control's end past the screen's edge, where no thumb can scroll to
        // it. Measured as what overflows, the page and the pane alike: the pane clips at its
        // own edge, so the page never scrolls and only the pane's scroll width says anything
        // is past it.
        editorCaseIn 390 844 "a block preview does not scroll sideways on a phone" <| fun page ->
            async {
                do! previewSettled page
                let faultsOf (what: string) (selector: string) =
                    async {
                        match! Probe.scroll page selector with
                        | None -> return [ what + ": not rendered" ]
                        | Some scroll when scroll.Width > scroll.ClientWidth + 1.0 ->
                            return [ sprintf "%s: %.0fpx of content in %.0fpx" what scroll.Width scroll.ClientWidth ]
                        | Some _ -> return []
                    }
                let! wholePage = faultsOf "the page" "html"
                let! pane = faultsOf "the pane" "#shell [data-content-panel]"
                let faults = wholePage @ pane
                Expect.isEmpty faults (sprintf "nothing wider than the screen: %s" (String.Join (", ", faults)))
            }

        // The list's sheet is over the preview AND its terminal, so the head that names the
        // preview is covered with the rest of what it lies over. Asked of the pane's own text
        // rather than of a hook, because the fault was the preview's name shown by another
        // surface (a strip item) than the one that names it now — and asked by hit-test,
        // because the covered head is still laid out under the sheet.
        editorCaseIn 390 844 "on a phone, the all page does not show the preview it is over" <| fun page ->
            async {
                do! previewSettled page
                let! named = Read.text page "#shell [data-pane-preview-name]"
                let name = named |> Option.map (fun n -> n.Trim ()) |> Option.defaultWith (fun () -> failwith "the preview has no name")
                do! awaitU (page.ClickAsync "#shell [data-pane-switcher]")
                do! waitVisible "the all page" page "#shell [data-content-list]"
                let! shown = Probe.shows page "#shell [data-content-panel]" name
                Expect.isFalse shown (sprintf "%s is not on the all page" name)
            }

        // Where New terminal starts one is the repo picker's branch link. On a wide screen it
        // unfolds the places under the Terminals heading, with the rest of the sheet still
        // round them, and pressed again it folds them. Driven from the keyboard, because the
        // floor's rule about a DOM swap says the cursor goes somewhere real: onto the ticked
        // place, and back onto the link when Escape folds the list.
        editorCase "on a wide screen the start-in link unfolds the places under the heading, and focus follows" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
                do! switcherDown page
                do! waitVisible "where New terminal starts one" page "#shell [data-start-in]"

                do! awaitU (page.Locator("#shell [data-start-in]").First.PressAsync "Enter")
                do! waitVisible "the list of places" page "#shell [data-start-in-choice]"
                // Under the heading, not in place of it: New terminal is still there.
                do! waitVisible "New terminal beside the places" page "#shell [data-terminal-new]"
                do! Wait.until
                        "focus on the ticked place"
                        (fun () -> Read.focused page "[data-start-in-choice][aria-pressed='true']")
                        id

                // Escape folds the list, not the sheet.
                do! awaitU (page.Keyboard.PressAsync "Escape")
                do! waitAbsent "the places folded" page "#shell [data-start-in-choice]"
                do! waitVisible "the sheet still up" page "#shell [data-terminal-new]"
                do! Wait.until "focus on [data-start-in]" (fun () -> Read.focused page "[data-start-in]") id

                // The link is the fold's own control: pressed twice, it opens and shuts.
                do! awaitU (page.ClickAsync "#shell [data-start-in]")
                do! waitVisible "the places again" page "#shell [data-start-in-choice]"
                do! awaitU (page.ClickAsync "#shell [data-start-in]")
                do! waitAbsent "the places folded by the link" page "#shell [data-start-in-choice]"
                return ()
            }

        // On a phone the places are a PAGE: nothing of the sheet beside them, and the sheet
        // run to the pane's foot, so the terminal it is laid over does not show under it. A
        // sheet as tall as its rows left the terminal on screen below a list that, on a phone,
        // is somewhere you went.
        editorCaseIn 390 844 "on a phone the start-in link opens the places as a page over the whole pane" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
                do! switcherDown page
                do! awaitU (page.ClickAsync "#shell [data-start-in]")
                do! waitVisible "the list of places" page "#shell [data-start-in-choice]"
                do! Wait.until "New terminal out of sight" (fun () -> Read.visible page "#shell [data-terminal-new]") not

                let! boxes = boxesOf page [ "#shell #pane-sheet"; "#shell [data-content-panel]" ]
                match boxes with
                | [ sheet; pane ] ->
                    Expect.isTrue
                        (abs (sheet.Bottom - pane.Bottom) <= 1.0)
                        (sprintf "the page reaches the pane's foot: sheet ends at %d, pane at %d" (px sheet.Bottom) (px pane.Bottom))
                | _ -> failwith "two boxes measured"

                // Its back arrow is the way back to the sheet's list.
                do! awaitU (page.ClickAsync "#shell [data-start-in-back]")
                do! waitVisible "the sheet's list again" page "#shell [data-terminal-new]"
                return ()
            }

        // The list of places fits the screen it is on. Only a browser can answer it and the markup is
        // innocent either way: a place's row carries what a repo's file says its sandbox is
        // FOR, which is a whole sentence — 170 characters in this repository's own
        // `yession.yaml` — and the menu that listed them once grew to its widest entry: measured
        // on a 375px phone, 962px, starting 599px off the side of the screen.
        //
        // What is asserted is that every row fits, not what it measures. The long descriptions
        // live in the harness fixture, because the version of this that shipped broken was
        // green against a fixture that said "day-to-day work".
        editorCaseIn 375 812 "the list of places fits the phone it is on" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
                do! switcherDown page
                do! awaitU (page.ClickAsync "#shell [data-start-in]")
                do! waitVisible "the list of places" page "#shell [data-start-in-choice]"

                // The note is what can push it wide, so a fixture that stopped carrying a long
                // one would make this pass over nothing.
                let! entries = Read.texts page "#shell [data-start-in-choice]"
                let longestNote = entries |> List.map (fun e -> e.Trim().Length) |> List.fold max 0
                Expect.isTrue
                    (longestNote > 90)
                    (sprintf "a place says what its sandbox is for, at length; longest is %d" longestNote)

                let! rows = boxesOf page [ "#shell [data-start-in-choice]" ]
                let! window = Probe.window page
                for r in rows do
                    Expect.isTrue
                        (r.Left >= 0.0 && r.Right <= window.InnerWidth)
                        (sprintf "the place is on the screen: left %d, right %d, in a %.0fpx screen" (px r.Left) (px r.Right) window.InnerWidth)
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
                do! waitVisible "the all page" page "#shell [data-content-list]"

                let rows = "#shell [data-content-list] [role='listitem']"
                let! marks = Read.attrs page (rows + " [data-pane-mark]") "data-pane-mark"
                let! unmarked = Read.count page (rows + ":not(:has([data-pane-mark]))")
                let kinds = (marks |> List.distinct |> List.length) + (if unmarked > 0 then 1 else 0)
                Expect.isTrue (kinds > 1) (sprintf "the rows wear different marks, got %d kind(s)" kinds)

                // The NAMES, by the hooks that make them names — not every button in a row,
                // which would drag the verbs at the far edge into the count.
                let names =
                    [ Yession.App.Dom.Hooks.terminalListRow
                      Yession.App.Dom.Hooks.artifactListRow ]
                    |> List.map (sprintf "#shell [data-content-list] [%s]")
                    |> String.concat ", "
                let! seen = Probe.seen page names
                let counted = seen.Length
                Expect.isTrue (counted > 1) (sprintf "more than one name to line up, got %d" counted)
                let offsets = seen |> List.map (fun n -> px n.Box.Left) |> List.distinct |> List.length
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
                do! waitVisible "the all page" page "#shell [data-content-list]"

                // Every terminal the session has is reachable here, whether or not the strip
                // would have carried it — the closed ones one press away, under their fold.
                do! awaitU (page.ClickAsync "#shell [data-list-closed-fold][aria-expanded='false']")
                do! waitVisible "the closed terminals' fold, opened" page "#shell [data-list-closed-fold][aria-expanded='true']"
                let! rows = Read.count page "#shell [data-content-list] [data-terminal-list-row]"
                Expect.equal rows 3 "every terminal the harness has, the closed one included"

                // Driven from the KEYBOARD: a row has to be a real control somebody can reach
                // and press without a pointer, and the focus move afterwards is what the floor
                // asks for when the pressed control leaves the document.
                do! awaitU (page.FocusAsync "#shell [data-content-list] [data-terminal-list-row='term-live']")
                do! awaitU (page.Keyboard.PressAsync "Enter")
                do! waitAbsent "#shell [data-content-list] to be gone" page "#shell [data-content-list]"
                do! Wait.until "#shell [data-pane-panel] to have data-pane-panel terminal:term-live" (fun () -> Read.attr page "#shell [data-pane-panel]" "data-pane-panel") ((=) (Some "terminal:term-live"))
                // This peer holds that terminal's keyboard, so it has no command line: what
                // takes the keystrokes is its screen, and that is where focus lands.
                do! Wait.until "focus on [data-terminal-keys='term-live']" (fun () -> Read.focused page "[data-terminal-keys='term-live']") id
                return ()
            }

        // The arrow walk down the rows is decided by the model (`ClientModel.walkList`), and
        // the focus it lands is the document's (`DomMove.FocusSwitcherRow`). Only a browser
        // has a focused element to ask whether the two met on the row the key names.
        editorCase "the arrows walk the switcher's rows" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
                do! awaitU (page.ClickAsync "#shell [data-pane-switcher]")
                do! waitVisible "the all page" page "#shell [data-content-list]"
                let rows = "#shell [data-content-list] [data-terminal-list-row]"
                let! ids = Read.attrs page rows "data-terminal-list-row"
                let ids = ids |> List.choose id
                Expect.isTrue (List.length ids >= 2) (sprintf "two rows to walk between: %A" ids)
                let row (id: string) = "#shell [data-content-list] " + Sel.hookIs "data-terminal-list-row" id
                let first, second, last = List.head ids, List.item 1 ids, List.last ids

                do! awaitU (page.FocusAsync (row first))
                do! awaitU (page.Keyboard.PressAsync "ArrowDown")
                do! Wait.until (sprintf "focus on the row after %s" first) (fun () -> Read.focused page (row second)) id
                do! awaitU (page.Keyboard.PressAsync "ArrowUp")
                do! Wait.until (sprintf "focus back on %s" first) (fun () -> Read.focused page (row first)) id
                do! awaitU (page.Keyboard.PressAsync "End")
                do! Wait.until (sprintf "focus on the last row, %s" last) (fun () -> Read.focused page (row last)) id
            }

        // The sidebar's environment lists what is running, and an entry is a way INTO it: the
        // same `OpenInPaneMsg` the `all` page's row sends. A hook that stopped matching, or a
        // message that stopped opening anything, leaves a list of names that quietly do
        // nothing, which no rendered string can tell from one that works.
        editorCaseIn 1440 900 "a terminal in the environment opens the pane on it" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-environment-terminals] [data-environment-terminal='term-live']")
                do! Wait.until "#shell [data-pane-panel] to have data-pane-panel terminal:term-live" (fun () -> Read.attr page "#shell [data-pane-panel]" "data-pane-panel") ((=) (Some "terminal:term-live"))
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
                do! Wait.until
                        "the drawer up, which is where the environment sits on a phone"
                        (fun () -> Probe.seen page "#shell aside")
                        (function
                         | drawer :: _ -> drawer.Box.Left >= 0.0 && drawer.Painted
                         | [] -> false)

                do! awaitU (page.ClickAsync entry)
                do! Wait.until
                        "the drawer to stand aside, and the terminal to be on the screen"
                        (fun () -> Probe.seen page "#shell [data-pane-panel='terminal:term-live']")
                        (List.exists (fun panel -> panel.Painted))
                // And it is GONE rather than under the pane: on a phone the pane is the other
                // sheet, at the same layer and later in the document, so a drawer left open
                // behind it hides nothing on screen and comes back the moment the pane goes.
                do! Wait.until
                        "the drawer to leave the screen"
                        (fun () -> Probe.boxes page [ "#shell aside" ])
                        (function
                         | [ Some drawer ] -> drawer.Right <= 0.0
                         | _ -> false)
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
                do! Wait.until "focus on #shell [data-content-list], #shell [data-content-list] *" (fun () -> Read.focused page "#shell [data-content-list], #shell [data-content-list] *") id
                do! awaitU (page.Keyboard.PressAsync "Escape")
                do! waitAbsent "#shell [data-content-list] to be gone" page "#shell [data-content-list]"
                do! Wait.until "focus on #shell [data-pane-pivot] [role=tab][aria-selected=true][data-pane-tab]" (fun () -> Read.focused page "#shell [data-pane-pivot] [role=tab][aria-selected=true][data-pane-tab]") id
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
                do! waitVisible "a row's close on the all page" page "#shell [data-content-list] [data-terminal-close]"
                let verbs = "#shell [data-content-list] [role=listitem] button:not([data-terminal-list-row])"
                let! verbsOffered = Probe.controls page verbs
                let offered = verbsOffered |> List.map (fun verb -> verb.Opening)
                Expect.isTrue (offered.Length >= 2) (sprintf "a page with verbs on it, or this proves nothing: %A" offered)
                do! awaitU (page.FocusAsync "#shell [data-content-list] [data-terminal-list-row]")
                let reached = System.Collections.Generic.HashSet<string> ()
                let inside = ref true
                let presses = ref 0
                while inside.Value && presses.Value < 40 do
                    do! awaitU (page.Keyboard.PressAsync "Tab")
                    presses.Value <- presses.Value + 1
                    let! inList = Read.focused page "#shell [data-content-list], #shell [data-content-list] *"
                    let! onVerb = Probe.controls page (verbs + ":focus")
                    match inList, onVerb with
                    | false, _ -> inside.Value <- false
                    | true, [] -> ()
                    | true, verb :: _ ->
                        // Painted: every box from it up to the page fully opaque, once the
                        // fade has run.
                        do! Wait.until
                                (sprintf "%s to be opaque once focused" verb.Opening)
                                (fun () -> Probe.controls page (verbs + ":focus"))
                                (List.exists (fun v -> v.Opacity = 1.0))
                        reached.Add verb.Opening |> ignore
                for verb in offered do
                    Expect.isTrue (reached.Contains verb) (sprintf "Tab reaches %s" verb)
            }

        // F5. A closed row's recording is reachable from the list: the row opens its terminal,
        // whose toolbar plays the recording.
        editorCase "a closed row on the all page leads to its replay" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
                // The pane opens on the terminal it was on, with its list shut, so one press
                // opens the list. Pressed, not first asked whether to: a read taken before the
                // press it decides is one the page can change under (as below).
                do! awaitU (page.ClickAsync "#shell [data-pane-switcher]")
                do! awaitU (page.ClickAsync "#shell [data-list-closed-fold][aria-expanded='false']")
                do! awaitU (page.FocusAsync "#shell [data-content-list] [data-terminal-list-row='term-done']")
                do! awaitU (page.Keyboard.PressAsync "Enter")
                // Opened, it is either playing already — a closed terminal whose recording is its
                // only read plays by itself and offers no watch (`ReplayIsTheRead`) — or one press
                // of its OWN watch from it. Both ends are asked of term-done's own hooks. The
                // toolbar of the terminal the list was laid over stays in the document under the
                // list, watch and all, until the pane draws the Enter, and a render owed inside a
                // frame waits for the frame's end (`Render.fs`). A watch asked of the pane at large
                // was that terminal's: the wait ended on it, the count found it, and the render
                // took it away before the click could, which then waited 15s for nothing.
                let replay = "#shell [data-pane-replay='terminal:term-done']"
                let watch = "#shell [data-pane-actions='terminal:term-done'] [data-terminal-watch='watch']"
                do! waitVisible "the closed terminal's replay, or its own way to watch it" page (replay + ", " + watch)
                let! playing = Read.visible page replay
                if not playing then
                    // A press that is fine with either end: a recording found to be the only
                    // read while the press waits plays by itself and takes its watch away, and
                    // that is the replay this case is after, not a press that failed.
                    match! Async.Catch (awaitU (page.Locator(watch).ClickAsync ())) with
                    | Choice1Of2 () -> ()
                    | Choice2Of2 refused ->
                        let! nowPlaying = Read.visible page replay
                        if not nowPlaying then raise refused
                do! waitVisible "the closed terminal's replay" page replay
                return ()
            }

        // The sheet is a layer over the pane, not a place: the pane under it is the way out,
        // pressed anywhere it shows. Only a browser can say what is on top where it shows —
        // a scrim under the sheet, or under the pane's own layers, presses nothing.
        editorCase "pressing the pane under the list's sheet puts the sheet away" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
                do! awaitU (page.ClickAsync "#shell [data-pane-switcher]")
                let sheet = "#shell #pane-sheet"
                let panel = "#shell [data-content-panel]"
                do! waitVisible "the list's sheet" page sheet
                let! box = await (page.Locator(sheet).BoundingBoxAsync ())
                let! pane = await (page.Locator(panel).BoundingBoxAsync ())
                Expect.isTrue (box.Y + box.Height < pane.Y + pane.Height - 20.0f) "pane showing under the sheet, or this proves nothing"
                // Pressed through the pane's locator, at a place IN the pane, never at a point
                // worked out from where the pane was drawn: the locator waits for the pane to
                // stand still, brings the place onto the screen, and refuses when something
                // outside the pane would take the press. A point taken from the box was below the
                // screen whenever the window had been scrolled off the shell — which the harness
                // allowed until the shell had a page of its own (`editor-harness.html` says how
                // the clicks above used to scroll it), and pressed nothing.
                do! awaitU (page.Locator(panel).ClickAsync (LocatorClickOptions (Position = Position (X = pane.Width / 2.0f, Y = pane.Height - 10.0f))))
                do! waitVisible "the switcher, shut" page "#shell [data-pane-switcher][aria-expanded='false']"
                return ()
            }

        // F5, on a phone: a thumb has no hover, so the row the pane is about WEARS its verbs —
        // the way back to them is the row itself — and each is a thumb's 44px.
        editorCaseOnTouch 390 844 "on a touch screen the row the pane is about wears its verbs at the touch floor" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
                do! awaitU (page.ClickAsync "#shell [data-pane-switcher]")
                do! waitVisible "the row the pane is about" page "#shell [data-content-list] [data-terminal-list-row][aria-current='true']"
                let! sizes =
                    Probe.controls
                        page
                        "#shell [data-content-list] [role=listitem]:has([data-terminal-list-row][aria-current='true']) button:not([data-terminal-list-row])"
                Expect.isNonEmpty sizes "the row the pane is about has verbs to wear"
                for size in sizes do
                    let width, height = Yession.App.Probes.Box.width size.Seen.Box, Yession.App.Probes.Box.height size.Seen.Box
                    Expect.isTrue
                        (px width >= 44 && px height >= 44 && size.Seen.Painted && size.Opacity = 1.0)
                        (sprintf "a verb painted, on top, at the touch floor: %s %dx%d painted %b opacity %.2f" size.Name (px width) (px height) size.Seen.Painted size.Opacity)
            }

        // F5, on a phone: every OTHER row's verbs are out of the way, not merely see-through —
        // a see-through kill at a row's edge is one a thumb presses without seeing it.
        editorCaseOnTouch 390 844 "on a touch screen no row hides a press it does not show" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
                do! awaitU (page.ClickAsync "#shell [data-pane-switcher]")
                do! waitVisible "a row the pane is not about" page "#shell [data-content-list] [data-terminal-list-row][aria-current='false']"
                let! others =
                    Probe.controls
                        page
                        "#shell [data-content-list] [role=listitem]:has([data-terminal-list-row][aria-current='false']) button:not([data-terminal-list-row])"
                let hidden = others |> List.filter (fun b -> b.Seen.Painted && b.Opacity < 0.5) |> List.map (fun b -> b.Opening)
                Expect.isEmpty hidden "nothing pressable that is not painted"
            }

        // The shortcut works from anywhere on the page — the whole point of one — including
        // with the pane shut, which it opens.
        editorCase "the switcher's shortcut opens it from the chat" <| fun page ->
            async {
                do! awaitU (page.Keyboard.PressAsync "Control+Backquote")
                do! waitVisible "the all page" page "#shell [data-content-list]"
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
                let! overflows = stripOverflows page
                Expect.isTrue overflows "the pivot holds more tabs than it shows, or this proves nothing"
                let! strip = boxesOf page [ "#shell [data-pane-strip]" ]
                let strip = strip.Head
                let tabs = "#shell [data-pane-strip] [data-pane-tab]"
                let! tabBoxes = Probe.seen page tabs
                let! tabNames = Read.attrs page tabs "data-pane-tab"
                let hidden =
                    List.zip tabBoxes tabNames
                    |> List.filter (fun (t, _) -> t.Box.Left < strip.Left - 1.0 || t.Box.Right > strip.Right + 1.0)
                    |> List.choose (fun (_, name) -> name |> Option.map (fun n -> n.Replace ("terminal:", "")))
                Expect.isNonEmpty hidden "the pivot is hiding some of its tabs"
                // On screen, by what is painted at its centre — not by its box, which a clipping
                // scroller leaves non-zero for something cut to nothing.
                let! all = Probe.seen page "#shell [data-pane-switcher]"
                Expect.isTrue (all |> List.exists (fun a -> a.Painted)) "all is on screen, whatever the tabs beside it"
                do! awaitU (page.Locator("#shell [data-pane-switcher]").DispatchEventAsync "click")
                do! waitVisible "the all page" page "#shell [data-content-list]"
                let! rows = Read.attrs page "#shell [data-content-list] [data-terminal-list-row]" "data-terminal-list-row"
                let listed = rows |> List.choose id |> Set.ofList
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
                // The press has to be at a point the case knows, because that point is what is
                // asked about afterwards — so it cannot be the locator's own click. The point is
                // measured only once Playwright has brought the kill onto the screen and found it
                // standing still and pressable (a trial click, which presses nothing): measured
                // before then, it is wherever the column's opening and the window's scroll had
                // left the kill, which a press can miss (see the sheet's case above).
                do! awaitU (page.Locator(kill).ClickAsync (LocatorClickOptions (Trial = true)))
                let! rect = await (page.Locator(kill).BoundingBoxAsync ())
                let x, y = rect.X + rect.Width / 2.0f, rect.Y + rect.Height / 2.0f
                do! awaitU (page.Mouse.ClickAsync (x, y))
                do! waitVisible "the kill, armed" page (kill + "[data-terminal-close-armed='true']")
                // Hit-tested, not compared by rectangle: what matters is what a press at that
                // point would reach, which is whatever is painted there.
                let! under = Probe.attrAt page (float x) (float y) "[data-terminal-close]" "data-terminal-close-armed"
                Expect.equal under (Some "true") "the spot that was pressed is the armed kill"
                let! sent = Harness.read page HarnessPage.closed
                Expect.equal sent None "the first press asked; it sent no kill"
            }

        // The fault this replaced: a double-click on a kill ended two terminals, the second
        // press landing on whatever slid under the pointer. Now the second press of the pair
        // is the confirm, so a double-click ends exactly the terminal it was made on.
        editorCase "a double-click on a kill ends that terminal and no other" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
                do! awaitU (page.ClickAsync "#shell [data-pane-switcher]")
                do! awaitU (page.Locator("#shell [data-content-list] [data-terminal-close='term-harness']").DblClickAsync ())
                let! sent = Harness.read page HarnessPage.closed
                Expect.equal sent (Some [| "term-harness" |]) "one kill, of the terminal the double-click was on"
            }

        // A tab's × closes the tab and ends nothing: the terminal under it goes on running,
        // in the list, which is where it is stopped.
        editorCase "pressing a tab's × closes that tab and sends no kill" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
                do! awaitU (page.ClickAsync "#shell [role='tablist'] [data-pane-tab-dismiss='term-harness']")
                do! waitAbsent "the tab to be gone" page "#shell [data-pane-tab='terminal:term-harness']"
                let! sent = Harness.read page HarnessPage.closed
                Expect.equal sent None "nothing ended"
            }

        // Delete on a focused tab is the ×'s press from the keyboard.
        editorCase "Delete on a tab closes it and sends no kill" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-content-toggle='show']")
                // Opening the pane lands focus in it a frame later (`paneLanding`). Focus put on
                // the tab before then is taken back by that landing, and the Delete goes to
                // wherever it put the keyboard — so the hand moves only once the pane has landed.
                do! twoFrames page
                do! awaitU (page.FocusAsync "#shell [data-pane-tab='terminal:term-harness']")
                do! awaitU (page.Keyboard.PressAsync "Delete")
                do! waitAbsent "the tab to be gone" page "#shell [data-pane-tab='terminal:term-harness']"
                let! sent = Harness.read page HarnessPage.closed
                Expect.equal sent None "nothing ended"
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
                do! waitVisible "the kill, armed" page (kill + "[data-terminal-close-armed='true']")
                do! awaitU (page.Keyboard.PressAsync "Escape")
                do! waitVisible "the kill, taken back" page (kill + "[data-terminal-close-armed='false']")
                let! focused = Read.focused page "[data-terminal-close='term-harness']"
                Expect.isTrue focused "focus stays on the kill"
                let! sent = Harness.read page HarnessPage.closed
                Expect.equal sent None "and nothing was killed"
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
                    Harness.run1 page HarnessPage.fold line
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
                do! waitVisible "the bare terminal's kill on the all page" page "#shell [data-content-list] [data-terminal-close='term-bare']"
                let rows () =
                    async {
                        let items = "#shell [data-content-list] [role=listitem]:has([data-terminal-list-row])"
                        let! boxes = Probe.seen page items
                        let! ids = Read.attrs page (items + " [data-terminal-list-row]") "data-terminal-list-row"
                        return
                            List.zip ids boxes
                            |> List.map (fun (id, row) -> id, px row.Box.Top, px (Yession.App.Probes.Box.height row.Box))
                            |> List.sort
                    }
                let! before = rows ()
                do! fold 91L (closed "term-harness")
                do! fold 92L (closed "term-bare")
                // Landed: a closed terminal offers no kill, and one with nothing kept says so.
                do! waitAbsent "#shell [data-terminal-close='term-harness'], #shell [data-terminal-close='term-bare'] to be gone" page "#shell [data-terminal-close='term-harness'], #shell [data-terminal-close='term-bare']"
                do! waitVisible "the bare terminal's row to say nothing was kept" page "#shell [data-terminal-list-gone='term-bare']"
                let! after = rows ()
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
                do! waitVisible "the task card in the chat" page "#shell [data-chat-task-card]"
                do! awaitU (page.FocusAsync "#shell [data-chat-task-card] [data-fold]")
                do! awaitU (page.Keyboard.PressAsync "Enter")
                do! Wait.until "#shell [data-chat-task-card] [data-fold-body] to have data-fold-open yes" (fun () -> Read.attr page "#shell [data-chat-task-card] [data-fold-body]" "data-fold-open") ((=) (Some "yes"))

                // The failed command leads, which is the one thing the ordering promises —
                // and it is a BUTTON, not a div someone hung a click on.
                let! first = Read.attr page "#shell [data-chat-task-card] [data-chat-block]" "data-chat-block"
                Expect.equal first (Some "block-burst-failed") "the failure leads"
                let! asButton = Read.count page "#shell [data-chat-task-card] [data-chat-block='block-burst-failed']:is(button)"
                Expect.equal asButton 1 "as a real button"

                // And pressing it does what an ungrouped chip does: opens that block.
                do! awaitU (page.Keyboard.PressAsync "Tab")
                do! awaitU (page.Keyboard.PressAsync "Enter")
                do! Wait.until "#shell [data-pane-panel] to have data-pane-panel block:term-harness:block-burst-failed" (fun () -> Read.attr page "#shell [data-pane-panel]" "data-pane-panel") ((=) (Some "block:term-harness:block-burst-failed"))
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
                do! waitVisible "the shell" page "#shell"
                // Settings' own fields (the Claude/GitHub panels, the model picker moved out
                // in Plan "dock send/model/interrupt", but a token input is still exactly
                // this case) live behind settings, and settings lives behind the sidebar —
                // both off-canvas on a phone until the column is brought on and turned to its
                // settings face. Through the controls a person uses, because the column is the
                // model's and a class set by hand would last only until the next render.
                do! awaitU (page.ClickAsync "#shell [data-nav-toggle='show']")
                do! awaitU (page.ClickAsync "#shell [data-settings-toggle='open']")
                do! waitVisible "the Claude panel in settings" page "#shell [data-claude-panel]"

                let! fields = Probe.controls page "#shell input, #shell select, #shell textarea, #shell [contenteditable='true']"
                let undersized =
                    fields
                    // Not on screen.
                    |> List.filter (fun f -> not (Yession.App.Probes.Box.width f.Seen.Box = 0.0 && Yession.App.Probes.Box.height f.Seen.Box = 0.0))
                    |> List.filter (fun f -> f.FontSize < 16.0)
                    |> List.map (fun f -> sprintf "%s: %gpx" f.Name f.FontSize)
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
        // Held: the header, the nav drawer's BOTH faces — the workspace one and settings — the
        // pane, and the chat's terminal chips. Settings joined when `fieldFace` grew to
        // `spacing-touch` on a phone the way `btnFace` already had: until then every input and
        // select in that drawer stood 32px under a thumb beside a 44px button, two heights in
        // one column and the shorter one below the floor. NOT yet the rest of the chat — a
        // message's actions, a fold's toggle, a chapter's name, a reply's ref — each still
        // under 44 on a phone today. They join by widening `held`, and the case says what is
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
                    do! waitVisible "the control that shows the pane" page "#shell [data-content-toggle='show']"
                    let undersized (face: string) =
                        async {
                            let pressable = [ "button"; "[role=tab]"; "a[href]"; "select"; "input:not([type=hidden])" ]
                            let regions = [ "#shell header"; "#shell aside" ]
                            let chips = [ "#shell [data-chat-block]"; "#shell [data-chat-pending]"; "#shell [data-chat-stretch]" ]
                            let held =
                                [ for p in pressable do
                                      for r in regions @ chips -> r + " " + p
                                  for c in chips do
                                      for p in pressable -> c + ":is(" + p + ")" ]
                                |> String.concat ", "
                            let! controls = Probe.controlsReached page held
                            return
                                controls
                                |> List.filter (fun c -> not c.Exempt && not (Yession.App.Probes.Box.isEmpty c.Seen.Box) && c.Seen.Painted)
                                |> List.filter (fun c ->
                                    let w, h = Yession.App.Probes.Box.width c.Seen.Box, Yession.App.Probes.Box.height c.Seen.Box
                                    h < 43.5 || (not c.Field && w < 43.5))
                                |> List.map (fun c ->
                                    sprintf "%s: %s %dx%d" face c.Name (px (Yession.App.Probes.Box.width c.Seen.Box)) (px (Yession.App.Probes.Box.height c.Seen.Box)))
                        }
                    let settle () = Probe.settled page
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
                    do! waitAbsent "the switcher to shut" page "#shell [data-content-list]"
                    let! _ = onFace "" "#shell [data-content-toggle='hide']"
                    let! preview = onFace "a preview" "#shell [data-chat-block]"
                    let! _ = onFace "" "#shell [data-content-toggle='hide']"
                    let! nav = onFace "the nav" "#shell [data-nav-toggle='show']"
                    // The column's other face, reached the way a person reaches it — the nav
                    // is already open, and settings is one press of its own pivot away.
                    let! settings = onFace "settings" "#shell [data-settings-toggle='open']"
                    let all = List.concat [ chat; pane; list; preview; nav; settings ]
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
                do! Harness.run page HarnessPage.noAgent
                do! waitVisible "the verb that connects an absent agent" page "#shell [data-agent-presence='absent'] [data-no-agent-connect]"
                return ()
            }
        editorCaseIn 1440 900 "the verb that connects an absent agent stands inside the agent's own row" <| fun page ->
            async {
                do! noAgentRow page
                let! measured = boxesOf page [ "#shell [data-agent-presence='absent']"; "#shell [data-no-agent-connect]" ]
                let row, b = measured.[0], measured.[1]
                let! asButton = Read.count page "#shell button[data-no-agent-connect]"
                let inside =
                    asButton = 1 && Yession.App.Probes.Box.width b > 0.0
                    && b.Top >= row.Top - 0.5 && b.Bottom <= row.Bottom + 0.5
                Expect.isTrue inside "the connect verb is a button whose box lies within the agent's row"
            }
        editorCaseIn 1440 900 "the verb that connects an absent agent opens settings" <| fun page ->
            async {
                do! noAgentRow page
                do! awaitU (page.ClickAsync "#shell [data-no-agent-connect]")
                do! waitVisible "the Claude panel in settings" page "#shell [data-claude-panel]"
                return ()
            }
        editorCaseIn 390 844 "on a phone the verb that connects an absent agent is a 44px target" <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-nav-toggle='show']")
                do! noAgentRow page
                let! verb = Probe.controlsReached page "#shell [data-no-agent-connect]"
                let height = verb |> List.tryHead |> Option.map (fun v -> Yession.App.Probes.Box.height v.Seen.Box) |> Option.defaultWith (fun () -> failwith "no connect verb")
                Expect.isTrue (height >= 43.5) (sprintf "a thumb's 44px, got %.1f" height)
            }
        thumbSized 390 844
        // A phone on its side is still a phone (`phone:` in app/tailwind.css): the same
        // controls, the same floor.
        thumbSized 844 390

        // The same floor for the one control the case above cannot see: a `<summary>` is not
        // in its `pressable` list, and deliberately — a bare verb has no rectangle, so what it
        // grows on a phone is its HIT AREA and not its box (docs/visual-design.md, Space). The
        // box has to stay put: the notice's height is a number the panes reserve
        // (`degradedBarHeight`), so a disclosure that grew would move the header under it.
        //
        // Worth a case of its own because of what this particular word is: the ONLY way to
        // read why a connection dropped, on the one mount a phone reader gets, at 13/16 — a
        // 16px target on the surface a phone meets first. An area that paints nothing can only
        // be asked about by hit-testing, so that is what this does, above and below the word,
        // at the extremes the 44px floor demands around whatever line the ramp gives it.
        editorCaseIn 390 844 "on a phone the notice's disclosure presses as a thumb's target" <| fun page ->
            async {
                do! waitVisible "the shell's conversation" page "#shell [data-conversation]"
                do! Harness.run1 page HarnessPage.state "degraded"
                let summary = "#shell [data-detail='degraded'] summary"
                do! waitVisible "the notice's disclosure" page summary
                // The bar unfolds as it arrives: what is measured is where it lands.
                do! Probe.settled page
                let! boxes = Probe.boxes page [ summary ]
                let word =
                    match boxes with
                    | [ Some box ] -> box
                    | _ -> failwith "the degraded notice draws no disclosure"
                let line = Yession.App.Probes.Box.height word
                // Half a pixel inside each extreme of the 44px the floor asks for, so the
                // reading is a hit rather than a boundary.
                let reach = (43.5 - line) / 2.0
                Expect.isTrue (reach > 0.0) (sprintf "the word is already %.1fpx, so there is nothing to grow" line)
                let! above = Probe.hitAt page summary -reach
                let! below = Probe.hitAt page summary (line + reach)
                Expect.isTrue (above && below)
                    (sprintf "a press %.1fpx above and below a %.1fpx word reaches the disclosure (above: %b, below: %b)"
                        reach line above below)
            }

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
                    do! paneCovers "the pane to cover the screen" page
                    do! awaitU (page.FocusAsync "#shell [data-nav-toggle='show']")
                    do! awaitU (page.Keyboard.PressAsync "Enter")
                    do! Wait.until
                            "the nav to be what is on screen at its centre"
                            (fun () -> Probe.seen page "#shell aside:not([data-content-panel])")
                            (function
                             | nav :: _ -> nav.Box.Right > 0.0 && nav.Painted
                             | [] -> false)
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
                do! waitVisible "the shell's conversation" page "#shell [data-conversation]"
                let! conversation = boxesOf page [ "#shell [data-conversation]" ]
                let width = Yession.App.Probes.Box.width conversation.Head
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

/// The conversation's text, if the page has drawn one yet.
let private timelineText (page: IPage) () : Async<string option> =
    Read.text page (Sel.hook Yession.App.Dom.Hooks.conversation)

let private inTimeline (page: IPage) : Async<unit> =
    Wait.until "the message in the timeline" (timelineText page) (Option.exists (fun t -> t.Contains saidInTimeline))

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
///
/// What the conversation's store must hold.
let private conversationKept (page: IPage) : Async<unit> =
    Wait.until "the conversation to be kept" (fun () -> Probe.kept page "/events" saidInTimeline) id

/// The transcript store holding what the terminal PRINTED — an asciicast `"o"` output record
/// carrying the shell's answer, not the `"i"` input record of the command that caused it.
///
/// The distinction is the precondition. `echo printed-before-the-session-died` is kept as an
/// `"i"` record the instant it is sent — the command line CONTAINS the marker — so a plain search
/// is satisfied before the shell has answered, let alone before the answer has been fetched and
/// written. Narrowing to `"o"` excludes that input record and leaves only the shell's real output
/// (verified: the recording holds an `"i"` line for the command and one
/// `[t,"o","printed-before-the-session-died\r\n"]` for its result — no separate echo record).
///
/// The wait that consumed it once never actually ran — Playwright's `WaitForFunctionAsync`
/// does not await a predicate that returns a Promise — so `make` proceeded on the first poll
/// regardless of what the store held, killed the session before the output record was written,
/// and the reload replayed a recording with no output. Green on an idle box, red on a loaded
/// runner, twice on the release gate. Asked of the page as a typed probe, the answer is awaited.
let private transcriptKept (page: IPage) : Async<unit> =
    Wait.until "the transcript to be kept" (fun () -> Probe.keptPrinted page "/terminals/" printedInTerminal) id

let private terminalPrinted (what: string) (page: IPage) : Async<unit> =
    Wait.until what
        (fun () -> Read.texts page (Sel.hook Yession.App.Dom.Hooks.terminalOutput))
        (List.exists (fun o -> o.Contains printedInTerminal))

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
                    do! waitConnected page

                    do! make page

                    // The worker has to be RUNNING before the session goes, or the reload has
                    // nothing serving it. Waiting on the registration is the difference between
                    // testing this and testing a race.
                    do! Wait.until "the service worker to be active" (fun () -> Probe.ask page Yession.App.Probes.workerActive 0) id

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
                    do! waitVisible "the reloaded page's conversation" page "[data-conversation]"

                    do! check page
                    }
                })
            finally
                mounted.Stop ()
        }

// One case at a time (`BrowserRunner`): every case boots its deployment in `mountDataDir` and
// keeps its host in `mountedHost` and `mountSessionPort`, so two at once would boot into one
// directory and kill, or forward to, each other's host.
let mountedTests =
    testSequenced <| testList "Path-mounted session (browser)" [
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
                        // `<mount>/callback` -> `./` -> the shell), and a page evaluation racing
                        // that navigation dies with "Execution context was destroyed" — which is
                        // exactly how this test passed locally and broke master. `connected` is only
                        // true on the shell after the bounce, and a typed wait reads again across
                        // navigations, so it is the one safe thing to await first.
                        do! waitConnected page

                        let! baseHref = Read.attr page "base" "href"
                        Expect.equal baseHref (Some (sprintf "/s/%s/" MOUNT_SESSION)) "the shell declares its mount"

                        // Installable, and installable AS ITSELF. The manifest is addressed
                        // relatively like every other route here, and the URLs INSIDE it resolve
                        // against its own address — so the app a person adds to their home screen
                        // launches at this session rather than at whatever sits on the origin's
                        // root. Resolved by the browser rather than compared as text, because the
                        // resolution is the property; the icon is fetched for the same reason a
                        // manifest naming an icon nobody serves would still parse.
                        let! installed = Probe.ask page Yession.App.Probes.installed 0
                        Expect.equal
                            installed
                            (Some
                                { Yession.App.Probes.Installed.Start = sprintf "/s/%s/" MOUNT_SESSION
                                  Yession.App.Probes.Installed.Icon = sprintf "/s/%s/icon.png" MOUNT_SESSION
                                  Yession.App.Probes.Installed.Status = 200
                                  Yession.App.Probes.Installed.ContentType = Some "image/png" })
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
                        do! waitVisible "the composer" page composerSel
                        do! awaitU (page.ClickAsync composerSel)
                        do! awaitU (page.Keyboard.TypeAsync "persisted across the wipe")
                        let hasDraft (what: string) =
                            Wait.until what (fun () -> Read.texts page ".ProseMirror") (List.contains "persisted across the wipe")
                        do! hasDraft "the draft in an editor"

                        mountedHost.Kill true
                        mountedHost.WaitForExit ()
                        if Directory.Exists mountDataDir then Directory.Delete (mountDataDir, true)
                        mounted.Restart ()

                        // The SAME url — the session came back on a different loopback port and
                        // the proxy followed it, which the browser never saw. So the origin is
                        // unchanged, its IndexedDB is still this session's, and the draft can
                        // only have come from there: the server's copy was deleted.
                        let! _ = await (page.ReloadAsync ())
                        do! waitConnected page
                        do! hasDraft "the draft back from the browser's own store"
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
                    do! waitVisible "the composer" page composerSel
                    do! awaitU (page.ClickAsync composerSel)
                    do! awaitU (page.Keyboard.TypeAsync saidInTimeline)
                    do! awaitU (page.Locator("[data-send-draft]").First.ClickAsync ())
                    do! inTimeline page
                    do! conversationKept page
                })
            (fun page ->
                async {
                    // It has what this client had been reading. Scoped to the conversation
                    // element: a page-level match is satisfied by the roster and by the
                    // composer this text was typed into.
                    do! Wait.until "the conversation to come back offline" (timelineText page) (Option.exists (fun t -> t.Contains saidInTimeline))
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
                        Wait.until
                            "the client to stop claiming it is connected"
                            (connectionState page)
                            ((<>) (Some Yession.App.Dom.Text.connected))
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
                    let atMostOne (what: string) =
                        Wait.until what (fun () -> Probe.visible page (Sel.hook Yession.App.Dom.Hooks.degraded)) (fun n -> n <= 1)
                    // And not zero everywhere: what must always hold is that something on screen
                    // says the session is gone. Without this the case above passes on a client
                    // that reports nothing at all.
                    let saidSomewhere (what: string) =
                        Wait.until
                            what
                            (fun () -> Probe.visible page (Sel.hook Yession.App.Dom.Hooks.degraded))
                            (fun n -> n >= 1)
                    do! Wait.until "the client to notice the session is gone"
                            (fun () -> Read.count page (Sel.hook Yession.App.Dom.Hooks.degraded)) (fun n -> n > 0)
                    for width, height, what in [ 1440, 900, "a desktop"; 390, 844, "a phone" ] do
                        do! awaitU (page.SetViewportSizeAsync (width, height))
                        do! atMostOne (sprintf "one report at most on %s" what)
                        do! saidSomewhere (sprintf "and something saying it on %s" what)
                    // The pane a phone reader can be on when it happens. The bar is fixed above
                    // all three, so bringing the nav out over it must not reveal a second copy.
                    do! awaitU (page.Locator("[data-nav-toggle='show']").First.ClickAsync ())
                    do! atMostOne "still one report with the nav open over it"
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
                    do! Wait.until "the offer to reopen" (fun () -> Read.count page (Sel.hook Yession.App.Dom.Hooks.sessionReopen)) (fun n -> n > 0)
                    let asked = ResizeArray<string> ()
                    page.Request.Add (fun r -> if r.Url.EndsWith "/open" then asked.Add r.Url)
                    do! awaitU (page.RouteAsync ("**/sessions/*/open", fun route ->
                            route.FulfillAsync (RouteFulfillOptions (Status = 200, ContentType = "text/html", Body = "<title>opening</title>"))
                            |> ignore))
                    // Whichever mount is on screen; a hidden one cannot be pressed. The way back is
                    // behind the line's disclosure, beside the reason it answers.
                    do! awaitU (page.Locator("[data-degraded]:visible [data-detail] summary").First.ClickAsync ())
                    do! awaitU (page.Locator("[data-session-reopen]:visible").First.ClickAsync ())
                    do! Wait.until "the browser to have left for the Manager" (fun () -> Read.url page) (fun url -> url.AbsolutePath.EndsWith "/open")
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
                        do! waitConnected page
                        let! _ = await (page.GoBackAsync ())
                        do! Wait.until "the Manager, one step back" (fun () -> Read.url page) (fun url -> url = Uri managerUrl)
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
                        do! waitConnected page
                        stalling <- true
                        // The page's time from here is the case's to turn, so the probe's deadline
                        // passes when the case says rather than ten real seconds later. Flowing,
                        // not paused: everything else on the page keeps its ordinary pace.
                        do! awaitU (page.Clock.InstallAsync ())
                        let! _ = await (page.ReloadAsync ())
                        // The probe is out, and held: its deadline is armed before it is sent.
                        do! Wait.within 20000.0 "the reloaded page to ask who it is, and the question to be held"
                                (fun () -> async.Return stalled.Task.IsCompleted) id
                        do! awaitU (page.Clock.FastForwardAsync (int64 Yession.App.Client.Probe.deadline.TotalMilliseconds))
                        do! Wait.until "the offer to reopen, once the probe has been given up on"
                                (fun () -> Read.count page (Sel.hook Yession.App.Dom.Hooks.sessionReopen)) (fun n -> n > 0)
                        }
                    })
                finally
                    mounted.Stop ()
            }

        // Plan 22, and the other half of the bug report: the conversation came back offline
        // and the terminal under it did not. `Sandbox` because this really runs a command — on a
        // box that cannot host a sandbox the block never reaches `ok`, and untagged this would
        // fail at once, quoting the sandbox's reason, rather than skip.
        Tag.needs "a terminal that really ran something" [ Tag.Browser; Tag.Native; Tag.Sandbox ] (fun () ->
        offlineReopen
            "the session is gone, the page is still there, and so is what its terminal printed"
            (fun page ->
                async {
                    do! awaitU (page.Locator("[data-content-toggle='show']").First.ClickAsync ())
                    do! openNewTerminal page
                    let composerInput = "[data-terminal-input^='term-draft:']:not([readonly])"
                    do! waitVisible "the new terminal's command line" page composerInput
                    do! awaitU (page.ClickAsync composerInput)
                    do! awaitU (page.Keyboard.TypeAsync (sprintf "echo %s" printedInTerminal))
                    do! awaitU (page.ClickAsync "[data-terminal-send]")
                    do! terminalPrinted "the printed line on screen" page
                    do! transcriptKept page
                })
            (fun page ->
                async {
                    // The column comes back as this browser left it — open (P0-4) — so nothing
                    // is pressed here: the replayed records are on screen with no network
                    // behind them, which is what a store read before the network buys.
                    do! terminalPrinted "the terminal output to come back offline" page
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

/// What the front door does with a request for a session it does not route.
[<RequireQualifiedAccess>]
type private DoorMiss =
    /// Answers `404 not found` itself.
    | Answers404
    /// Hands it to the Manager, as the proxy example's Caddyfile does with every path no
    /// session route claims. The Manager has no such route, so it answers 404.
    | ToManager
    /// Takes the connection and never answers: an upstream that went quiet.
    | Stalls

/// The front door's pause before it reads the registry again. A reconnect backoff in the
/// fixture's own plumbing, on the real clock because what it reconnects to is a real socket with
/// no clock of its own; nothing a case asserts waits on it.
[<Yession.Tests.Support.Waits>]
let private registryBackoff () : Async<unit> = Async.Sleep 100

/// The operator's front door. `admits` says from which request on a session's route is live,
/// given the session's id and how many requests for it the door has seen (this one included);
/// `miss` says what the door does before then. A session is routed only while the registry
/// says it runs, as behind a real reconciler. Where each session actually listens is read off
/// the Manager's registry stream, the same subscription an operator's proxy holds open,
/// because a fixture that was TOLD the port would not be standing where the lag is.
let private startFrontDoorWith (managerPort: int) (admits: string -> int -> bool) (miss: DoorMiss) : Serving =
    let served = listenOnLoopback ()
    let listener = served.Listener
    let client = new HttpClient (new HttpClientHandler (AllowAutoRedirect = false, UseCookies = false))
    let ports = System.Collections.Concurrent.ConcurrentDictionary<string, int> ()
    let asked = System.Collections.Concurrent.ConcurrentDictionary<string, int> ()
    // The registry, read the way a reconciler reads it. Retried because the door has to be up
    // BEFORE the Manager — the Manager's public origin is this port, and its first session
    // fetches OIDC discovery against it while booting.
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
                            // A frame is the whole running set, so a session missing from it
                            // has stopped, and its route goes with it.
                            let running = frame.Sessions |> List.map (fun entry -> Yession.Domain.SessionId.value entry.Id)
                            for gone in ports.Keys |> Seq.filter (fun id -> not (List.contains id running)) |> List.ofSeq do
                                ports.TryRemove gone |> ignore
                            for entry in frame.Sessions do
                                ports.[Yession.Domain.SessionId.value entry.Id] <- entry.Port
                        | Error reason -> printfn "front door: a registry frame it could not read: %s" reason
            with _ -> ()
            do! registryBackoff ()
            return! watching ()
        }
    Async.Start (watching ())
    let sessionIn (path: string) =
        let m = Text.RegularExpressions.Regex.Match (path, "^/s/([^/]+)(/.*)?$")
        if m.Success then Some m.Groups.[1].Value else None
    let routes (id: string) =
        let asks = asked.AddOrUpdate (id, 1, fun _ n -> n + 1)
        admits id asks && ports.ContainsKey id
    // Where one request goes, decided once: `Some port` is forwarded there, `None` is a miss.
    let upstream (raw: string) : int option =
        match sessionIn (raw.Split('?').[0]) with
        | None -> Some managerPort
        | Some id when routes id -> Some ports.[id]
        | Some _ when miss = DoorMiss.ToManager -> Some managerPort
        | Some _ -> None
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
                let raw = ctx.Request.RawUrl
                let goes = upstream raw
                // A stalled request is taken and never answered: held until the listener stops.
                if not (goes.IsNone && miss = DoorMiss.Stalls) then
                    Async.Start (
                        async {
                            try
                                match goes with
                                | None ->
                                    // What a front door says about an address it has no route
                                    // for. Verbatim, because being indistinguishable from a
                                    // session's own answer is the point.
                                    ctx.Response.StatusCode <- 404
                                    ctx.Response.ContentType <- "text/plain"
                                    let bytes = Text.Encoding.UTF8.GetBytes "not found"
                                    ctx.Response.OutputStream.Write (bytes, 0, bytes.Length)
                                | Some port ->
                                    // The path goes through UNCHANGED: a session strips its own
                                    // `/s/<id>` mount, and the Manager is what
                                    // this origin's root is.
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

/// The operator's front door, with its reconciler's lag made into something a test can
/// depend on: a session is routed only from its `lag`-th request onward, and a miss is the
/// door's own 404.
///
/// Counted in REQUESTS, not milliseconds, so no sleep anywhere decides whether this passes —
/// and sticky once it catches up, like a real reconciler: a mapping that has appeared does not
/// go away again while the session runs.
let private startFrontDoor (managerPort: int) (lag: int) : Serving =
    startFrontDoorWith managerPort (fun _ asks -> asks > lag) DoorMiss.Answers404

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

// One case at a time (`BrowserRunner`): a case boots in `frontDataDir` and keeps its host in
// `frontedHost`, which a second case would share.
let frontDoorTests =
    testSequenced <| testList "Creating a session behind a front door (browser)" [
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
                        do! waitVisible "Create, on the Manager's page" page create
                        do! awaitU (page.ClickAsync create)

                        // THE promise: pressing Create puts you in the session it created. One
                        // fact, read off the page rather than off its words — the shell says which
                        // session it is, and the address says which session was asked for, and
                        // they have to be the same one.
                        let landed () =
                            async {
                                let! at = Read.url page
                                let! shell = Read.attr page ("meta" + Sel.hookIs "name" Yession.App.Dom.sessionMetaName) "content"
                                return at, shell
                            }
                        let inSession (at: Uri, shell: string option) =
                            // `/s/<id>/…`: the session the address asked for.
                            match List.ofArray (at.AbsolutePath.Split '/') with
                            | "" :: "s" :: asked :: _ :: _ when asked <> "" -> shell = Some asked
                            | _ -> false
                        // Launching a real child and waiting out the door's lag is the slow part;
                        // the failure this guards is instant, so a long wait only ever costs a
                        // green run time. A red one says where the browser is and what the shell
                        // there says it is, which is the whole of the answer.
                        do! Wait.within 90000.0 "creating a session to land in that session" landed inSession
                        }
                    })
                finally
                    door.Stop ()
                    try if frontedHost <> null then frontedHost.Kill true with _ -> ()
            }
    ]

// --- Opening a session this browser keeps (browser) -----------------------------------------
//
// A phone opened a stopped session from the Manager's list and looked at a blank page, or the
// opening screen, for 92 seconds. The browser had the session's shell and history all along:
// the session's service worker keeps the shell, and the shell keeps the history. But the
// screen waited for the session to answer and then went through sign-in, and nothing on that
// path is the shell.
//
// So the screen sends a browser that keeps the shell straight to it. The cases below hold the
// door's route for the session off while they open it: what the browser shows then is what it
// kept, and nothing else.

let private KEPT_SESSION = "kept-open"
let private keptDataDir = "tests/browser/.data-kept"

/// One public origin with the Manager at its root and the session under `/s/<id>`, behind a
/// door whose route for the session the case can hold off (`hold true`). A held route is a
/// miss, and `miss` says what the door does with one. The session runs from boot.
let private withKeptDeployment (miss: DoorMiss) (body: Serving -> (bool -> unit) -> Async<unit>) : Async<unit> =
    async {
        if Directory.Exists keptDataDir then Directory.Delete (keptDataDir, true)
        let managerPort = freeLoopbackPort ()
        let held = ref false
        let door = startFrontDoorWith managerPort (fun _ _ -> not held.Value) miss
        try
            let manager =
                deployProduct
                    "the Manager"
                    [ "--auth"; "localhost"
                      "--port"; string managerPort
                      "--default-session"; KEPT_SESSION
                      "--data-dir"; keptDataDir ]
                    [ "YESSION_MANAGER_URL", door.Origin
                      "YESSION_SESSION_URL", door.Origin + "/s/{id}" ]
                    (fun line -> line.Contains "management UI at")
            try
                // Routed once the door has read the session's port off the registry. Asked with
                // a bound, because a door that stalls its misses never answers one.
                use http = new HttpClient (Timeout = TimeSpan.FromSeconds 2.0)
                let routed () =
                    async {
                        try
                            let! answer = await (http.GetAsync (door.At (sprintf "/s/%s/me" KEPT_SESSION)))
                            return int answer.StatusCode
                        with _ -> return 0
                    }
                do! Wait.until "the door to route the session" routed (fun status -> status <> 0 && status <> 404)
                do! body door (fun hold -> held.Value <- hold)
            finally
                manager.Stop ()
        finally
            door.Stop ()
    }

let private keptShell = sprintf "/s/%s/" KEPT_SESSION
let private keptOpen = sprintf "/sessions/%s/open" KEPT_SESSION

/// Signed in, connected, a message said and kept, and the worker running. Then the session is
/// stopped and the door's route to it held off, so nothing after this can reach it.
let private keepThenStop (door: Serving) (hold: bool -> unit) (page: IPage) : Async<unit> =
    async {
        let! _ = await (page.GotoAsync (door.At keptShell))
        do! waitConnected page
        let composerSel = """[data-rich-readonly="false"] .ProseMirror"""
        do! waitVisible "the composer" page composerSel
        do! awaitU (page.ClickAsync composerSel)
        do! awaitU (page.Keyboard.TypeAsync saidInTimeline)
        do! awaitU (page.Locator("[data-send-draft]").First.ClickAsync ())
        do! inTimeline page
        do! conversationKept page
        do! Wait.until "the service worker to be active" (fun () -> Probe.ask page Yession.App.Probes.workerActive 0) id
        hold true
        use http = new HttpClient ()
        let! stopped = await (http.PostAsync (door.At (sprintf "/sessions/%s/stop" KEPT_SESSION), new StringContent ""))
        Expect.isTrue stopped.IsSuccessStatusCode "the session stops"
    }

/// Open the session the way its row in the Manager does. Waited on only until the screen
/// commits, because the screen may leave at once.
let private openKept (door: Serving) (page: IPage) : Async<unit> =
    async {
        let! _ = await (page.GotoAsync (door.At keptOpen, PageGotoOptions (WaitUntil = WaitUntilState.Commit)))
        return ()
    }

/// At the session's own address, with the kept conversation on the screen.
///
/// The page is asked for its text only once it is at that address. Until a navigation there
/// commits, a read of the page waits for it, and a navigation that never commits would hold
/// the read, and this wait, for ever instead of failing it.
let private onKeptHistory (page: IPage) () : Async<string * string option> =
    async {
        let! at = Read.url page
        if at.AbsolutePath <> keptShell then return at.AbsolutePath, None
        else
            let! text = timelineText page ()
            return at.AbsolutePath, text
    }

let private showsKeptHistory (path: string, text: string option) =
    path = keptShell && text |> Option.exists (fun t -> t.Contains saidInTimeline)

/// A profile per case, on disk. `body` gets its one page.
let private inProfile (name: string) (body: IPage -> Async<unit>) : Async<unit> =
    withPersistentContext (BrowserTypeLaunchPersistentContextOptions ()) (fun context -> async {
        let! page =
            match List.ofSeq context.Pages with
            | first :: _ -> async.Return first
            | [] -> await (context.NewPageAsync ())
        page.SetDefaultTimeout 30000.0f
        let evidence = watching page
        do! reporting name page evidence (body page)
    })

// One case at a time (`BrowserRunner`): every case's deployment starts by emptying `keptDataDir`.
let keptOpenTests =
    testSequenced <| testList "Opening a session this browser keeps (browser)" [
        // The route is held off for the whole case, and the door hands its misses to the
        // Manager, which answers 404, as behind the proxy example's Caddyfile. So the session
        // cannot answer anything: the history on the screen is what this browser kept.
        testCaseAsync "a browser that keeps the session opens on its history before the session answers" <|
            withKeptDeployment DoorMiss.ToManager (fun door hold ->
                inProfile "kept: before the session answers" (fun page -> async {
                    do! keepThenStop door hold page
                    do! openKept door page
                    do! Wait.within 15000.0 "the kept history, at the session's own address" (onKeptHistory page) showsKeptHistory
                }))

        // The same, with the route a door that takes the request and never answers. The worker
        // gives the network a bound and then serves the shell it kept.
        testCaseAsync "a session whose address never answers still opens on its kept history" <|
            withKeptDeployment DoorMiss.Stalls (fun door hold ->
                inProfile "kept: an address that never answers" (fun page -> async {
                    do! keepThenStop door hold page
                    do! openKept door page
                    do! Wait.within 20000.0 "the kept history, at the session's own address" (onKeptHistory page) showsKeptHistory
                }))

        // The shell that opened early signs in by itself. The cookie is cleared, so the
        // session's `/me` answers 401 once it is reachable, and the shell has to go through
        // sign-in on its own to connect.
        testCaseAsync "a kept session that opened early signs in once the session answers" <|
            withKeptDeployment DoorMiss.ToManager (fun door hold ->
                inProfile "kept: signs in later" (fun page -> async {
                    do! keepThenStop door hold page
                    do! awaitU (page.Context.ClearCookiesAsync ())
                    do! openKept door page
                    do! Wait.until "the kept history, at the session's own address" (onKeptHistory page) showsKeptHistory
                    hold false
                    do! Wait.within 60000.0 "the shell to sign in and connect" (connectionState page) ((=) (Some Yession.App.Dom.Text.connected))
                }))

        // A browser that has never opened the session keeps nothing, and goes the way it always
        // did: the screen polls the Manager. A poll is the proof, because the screen polls only
        // once it has found nothing kept.
        testCaseAsync "a browser that keeps nothing waits on the opening screen" <|
            withKeptDeployment DoorMiss.ToManager (fun door hold ->
                inProfile "kept: nothing kept" (fun page -> async {
                    hold true
                    use http = new HttpClient ()
                    let! stopped = await (http.PostAsync (door.At (sprintf "/sessions/%s/stop" KEPT_SESSION), new StringContent ""))
                    Expect.isTrue stopped.IsSuccessStatusCode "the session stops"
                    let polled = page.WaitForRequestAsync (fun (request: IRequest) -> request.Url.EndsWith "/ready")
                    do! openKept door page
                    let! _ = await polled
                    let! at = Read.url page
                    Expect.equal at.AbsolutePath keptOpen "the screen is still showing, and polling"
                }))
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


/// `tailscale serve`, as far as this suite needs one: a TCP port on loopback whose every
/// connection is carried, byte for byte, to the proxy's unix socket. What `serve` adds (TLS,
/// the `Tailscale-User-*` headers) the browser context adds itself, so this is only the
/// door: the one thing that can open the proxy's socket.
type private Ingress =
    { Port : int
      Stop : unit -> unit }

let private ingressTo (socketPath: string) : Ingress =
    let listener = new TcpListener (IPAddress.Loopback, 0)
    listener.Start ()
    let stopping = new CancellationTokenSource ()
    let carry (client: TcpClient) =
        task {
            use client = client
            use upstream = new Socket (AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)
            try
                do! upstream.ConnectAsync (UnixDomainSocketEndPoint socketPath)
                use toProxy = new NetworkStream (upstream, false)
                let fromBrowser = client.GetStream ()
                let up = fromBrowser.CopyToAsync (toProxy, stopping.Token)
                let down = toProxy.CopyToAsync (fromBrowser, stopping.Token)
                let! _ = Task.WhenAny (up, down)
                ()
            with _ -> ()
        }
        |> ignore
    let rec accept () =
        task {
            try
                let! client = listener.AcceptTcpClientAsync stopping.Token
                carry client
                return! accept ()
            with _ -> ()
        }
    accept () |> ignore
    { Port = (listener.LocalEndpoint :?> IPEndPoint).Port
      Stop =
        fun () ->
            stopping.Cancel ()
            listener.Stop () }

/// The deployment, and the one address a person on the tailnet ever sees of it.
type private Fronted =
    { /// The public origin — the ingress's, which is the Manager's origin too.
      Origin : string
      Ingress : Ingress
      Pieces : Deployed list }

/// The three processes, in the order a cold boot needs them: the proxy first, because the
/// Manager's public origin IS the proxy and its default session runs OIDC discovery against
/// it while booting; the Manager; then the map, which needs the Manager's stream to follow.
/// Everything here is what the README says to run, with the README's own template.
///
/// Both doors are unix sockets, as the README says to run them: the proxy's, which only the
/// ingress opens, and the Manager's, which only the proxy and the map open. The ingress's
/// port is taken at `:0` before anything starts, because the Manager is handed it as its
/// public origin.
let private deployFronted () : Fronted =
    if Directory.Exists frontedDataDir then Directory.Delete (frontedDataDir, true)
    Directory.CreateDirectory frontedMapDir |> ignore
    // Absolute: Caddy and the map run from the repository root, and a socket path has to
    // name the same file for all three.
    let proxySocket = Path.GetFullPath (frontedDataDir + "/proxy.sock")
    let managerSocket = Path.GetFullPath (frontedDataDir + "/manager.sock")
    let ingress = ingressTo proxySocket
    let origin = sprintf "http://127.0.0.1:%d" ingress.Port
    let proxy =
        deploy
            "caddy"
            "caddy"
            [ "run"; "--config"; "examples/proxy/caddy/Caddyfile"; "--adapter"; "caddyfile"; "--watch" ]
            [ "YESSION_PROXY_SOCKET", proxySocket
              "YESSION_PROXY_MANAGER", "unix/" + managerSocket
              // Absolute: an `import` glob is resolved against the Caddyfile's own directory,
              // and the map is written under this suite's, not the example's.
              "YESSION_PROXY_SESSIONS", Path.GetFullPath frontedMapDir + "/sessions*.caddy" ]
            (fun line -> line.Contains "serving initial configuration")
    let manager =
        deployProduct
            "the Manager"
            [ "--auth"; "trusted-headers"; "--secrets"; "ephemeral"
              "--socket"; managerSocket; "--data-dir"; frontedDataDir ]
            [ "YESSION_MANAGER_URL", origin
              "YESSION_SESSION_URL", origin + "/s/{id}" ]
            (fun line -> line.Contains "management UI at")
    let map =
        deploy
            "sessions-map"
            "node"
            [ "examples/proxy/main.mjs"
              "--manager"; "unix:" + managerSocket
              "--as"; "proxy-map"
              "--out"; frontedMapDir + "/sessions.caddy"
              "--empty"; "# no running sessions"
              "--template"; "@s_{id} path /s/{id} /s/{id}/*\nhandle @s_{id} {\n\treverse_proxy 127.0.0.1:{port}\n}" ]
            []
            (fun line -> line.Contains " follows ")
    { Origin = origin; Ingress = ingress; Pieces = [ proxy; manager; map ] }

/// Stop a deployment: the map and the Manager before the proxy they sit behind, and the
/// ingress last.
let private stopFronted (deployed: Fronted) =
    for d in List.rev deployed.Pieces do d.Stop ()
    deployed.Ingress.Stop ()

// One case at a time (`BrowserRunner`): a case's deployment starts by emptying `frontedDataDir`.
let frontedTests =
    testSequenced <| testList "A fronted deployment, for real (browser)" [
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
                        do! waitVisible "Create, on the Manager's page" page create
                        do! awaitU (page.ClickAsync create)

                        // One promise, read off the page: the address names a session, the shell
                        // says it is that one, the client is connected to it (so the sign-in went
                        // through the Manager as issuer, at the proxy's origin, and came back),
                        // and the name on the roster is the one the ingress asserted — through
                        // caddy's translation, the Manager's ID token and the session's cookie.
                        let landing () =
                            async {
                                let! at = Read.url page
                                let! shell = Read.attr page ("meta" + Sel.hookIs "name" Yession.App.Dom.sessionMetaName) "content"
                                let! connection = connectionState page ()
                                let! name = Read.text page (Sel.hook Yession.App.Dom.Hooks.displayName)
                                return at, shell, connection, name |> Option.map (fun n -> n.Trim ())
                            }
                        let landedAsYourself (at: Uri, shell: string option, connection: string option, name: string option) =
                            // `/s/<id>/…`: the session the address asked for.
                            match List.ofArray (at.AbsolutePath.Split '/') with
                            | "" :: "s" :: asked :: _ :: _ when asked <> "" ->
                                shell = Some asked
                                && connection = Some Yession.App.Dom.Text.connected
                                && name = Some FRONTED_NAME
                            | _ -> false
                        // A real child launches, the map catches up, caddy re-adapts within a
                        // second, and the sign-in round-trips — slow on a cold runner, and the
                        // faults this guards are all instant, so a long wait only costs green time.
                        match! Wait.within 90000.0 "pressing Create to land in that session as the asserted user" landing landedAsYourself |> Async.Catch with
                        | Choice1Of2 () -> ()
                        | Choice2Of2 e ->
                            // Whether the page is being RENDERED at all, which "connected with the
                            // right name" does not say: a document whose script runs while its
                            // rendering is suppressed reads as a wait that simply never fires.
                            // Cost one whole CI round to tell apart once.
                            // Each read is one more look at a page that may be mid-navigation, so
                            // none of them may cost the message the wait already wrote.
                            let orUnreadable (read: Async<string>) =
                                async {
                                    match! Async.Catch read with
                                    | Choice1Of2 value -> return value
                                    | Choice2Of2 failed -> return sprintf "(unreadable: %s)" failed.Message
                                }
                            let! rendering =
                                orUnreadable (async {
                                    let! painting = Probe.framing page 2000
                                    return string painting })
                            let! title = orUnreadable (await (page.TitleAsync ()))
                            let! text =
                                orUnreadable (async {
                                    let! t = await (page.Locator("body").InnerTextAsync ())
                                    return t.Substring (0, min 200 t.Length) })
                            let said =
                                deployed.Pieces
                                |> List.map (fun d -> sprintf "--- %s said ---\n%s" d.Label (lock d.Said (fun () -> string d.Said)))
                                |> String.concat "\n"
                            failwithf
                                "%s\n  rendering: %s, title: %s, text: %s\n%s"
                                e.Message
                                rendering
                                title
                                text
                                said
                        }
                    })
                finally
                    stopFronted deployed
            }

        // The forgery the audit found, from the side the Manager can see: an identity header
        // that the proxy did not set. A client that names itself in `x-yession-user` and
        // carries no `Tailscale-User-Login` reaches the Manager with the identity the proxy
        // set from the ingress — none — and is refused. The Manager has no TCP port to send
        // it to instead (`Listener.admits`), so this door is the only one there is.
        testCaseAsync "an x-yession-user the proxy did not set is refused with 401" <|
            async {
                let deployed = deployFronted ()
                try
                    use client = new HttpClient ()
                    use request = new HttpRequestMessage (HttpMethod.Get, deployed.Origin + "/")
                    request.Headers.Add ("x-yession-user", FRONTED_LOGIN)
                    let! answer = client.SendAsync request |> Async.AwaitTask
                    Expect.equal (int answer.StatusCode) 401 "a header from a peer that is not the proxy names nobody"
                finally
                    stopFronted deployed
            }

        // `/control/*` is the Manager's API for its sessions, which dial its socket and never
        // the door. Behind the door a launch secret alone gates it, so the proxy answers it
        // itself. A Manager reached would answer 401 for the missing secret, not 404.
        testCaseAsync "the door answers the Manager's control routes with 404" <|
            async {
                let deployed = deployFronted ()
                try
                    use client = new HttpClient ()
                    use request = new HttpRequestMessage (HttpMethod.Get, deployed.Origin + "/control/status")
                    request.Headers.Add ("Tailscale-User-Login", FRONTED_LOGIN)
                    let! answer = client.SendAsync request |> Async.AwaitTask
                    Expect.equal (int answer.StatusCode) 404 "a tailnet member is not sent to the control API"
                finally
                    stopFronted deployed
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

// One case at a time (`BrowserRunner`): a case's Manager starts by emptying `filtersDataDir`.
let filterTests =
    testSequenced <| testList "The management page's filters (browser)" [
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
                        do! waitVisible "the filter that shows archived sessions" page archived
                        do! awaitU (page.ClickAsync archived)

                        // Both halves of one click, read off the page: the address carries the
                        // filter, and the rows stream has answered for it — which shows as the chip
                        // re-rendered for the NEW query, linking back to the one without it.
                        let chip = Sel.hookIs Yession.App.Dom.Manager.filter "show-archived"
                        let addressAndChip () =
                            async {
                                let! at = Read.url page
                                let! href = Read.attr page chip "href"
                                return at, href
                            }
                        let offers (href: string option) = href |> Option.exists (fun h -> h.Contains "show=archived")
                        do! Wait.until "the filter in the address, and the chip offering its removal"
                                addressAndChip
                                (fun (at, href) -> at.Query.Contains "show=archived" && not (offers href))

                        // Back is the promise. It must stay on this page, take the filter out of
                        // the address, and move the rows with it — the chip links to adding it again.
                        let! _ = await (page.GoBackAsync ())
                        do! Wait.until "the filter gone from the address, and the chip offering it again"
                                addressAndChip
                                (fun (at, href) -> at.AbsolutePath = "/" && not (at.Query.Contains "show=archived") && offers href)
                        ()
                        }
                    })
                finally
                    manager.Stop ()
            }
    ]

// --- A display name on the management page (browser) -------------------------------------
//
// A display name is the session's to choose and every peer's to edit, and the Manager page
// puts it in the accessible name of each verb on the row. The renderer once read that hole as
// text, so a `"` in the name closed the attribute and the rest of it was markup, in every
// operator's browser. The row that carries it here ARRIVES, over the rows stream, because the
// page's own parse of a fragment is the second place the name could become markup.

let private hostileDataDir = "tests/browser/.data-hostile-name"

let private HOSTILE_ID = "hostile-name"

let private HOSTILE_NAME = "<script>window.pwned=1</script>\" onanimationstart=\"window.pwned=1"

/// A Manager whose registry already holds the session, named by its worst-case peer.
let private seedHostile () =
    if Directory.Exists hostileDataDir then Directory.Delete (hostileDataDir, true)
    Directory.CreateDirectory hostileDataDir |> ignore
    let id = Yession.Domain.SessionId.create HOSTILE_ID |> Result.defaultWith failwith
    let record : Yession.Manager.SessionRecord =
        { SessionId = id
          DisplayName = HOSTILE_NAME
          CreatedAt = DateTimeOffset.UtcNow
          DataDir = sprintf "sessions/%s" HOSTILE_ID
          ArchivedAt = None }
    let state =
        Yession.Manager.ManagerState.addSession record Yession.Manager.ManagerState.empty
        |> Result.defaultWith failwith
    File.WriteAllText (Path.Combine (hostileDataDir, "manager.json"), Yession.Manager.ManagerCodec.toString state)

// One case at a time (`BrowserRunner`): a case's Manager starts by emptying `hostileDataDir`.
let hostileNameTests =
    testSequenced <| testList "A display name on the management page (browser)" [
        testCaseAsync "a row that arrives keeps a hostile display name inside its accessible name" <|
            async {
                seedHostile ()
                let manager =
                    deployProduct
                        "the Manager"
                        [ "--auth"; "localhost"; "--secrets"; "ephemeral"
                          "--port"; "0"; "--data-dir"; hostileDataDir ]
                        []
                        (fun line -> line.Contains "management UI at")
                try
                    do! withContexts (fun contexts -> async {
                        let! page = contexts.Page None
                        page.SetDefaultTimeout 30000.0f
                        let evidence = watching page
                        do! reporting "a hostile display name" page evidence <| async {
                        // Both filters lit, so the row stays on the page once it is archived.
                        let! _ = await (page.GotoAsync (manager.At "/?show=active&show=archived"))
                        let archive = Sel.hookIs Yession.App.Dom.Manager.archive HOSTILE_ID
                        do! Wait.until "the session's row on the first paint" (fun () -> Read.count page archive) (fun n -> n = 1)
                        // Archived from outside the page, so the row the page then shows is one
                        // the rows stream carried and the page parsed.
                        use client = new HttpClient ()
                        let! archived =
                            client.PostAsync (manager.At (sprintf "/sessions/%s/archive" HOSTILE_ID), new StringContent "")
                            |> Async.AwaitTask
                        Expect.isTrue archived.IsSuccessStatusCode "the session is archived"
                        let unarchive = Sel.hookIs Yession.App.Dom.Manager.unarchive HOSTILE_ID
                        do! Wait.until "the archived row, from the stream"
                                (fun () -> Read.attr page unarchive "aria-label")
                                (fun label -> label = Some ("Unarchive " + HOSTILE_NAME))
                        let! handler = Read.attr page unarchive "onanimationstart"
                        Expect.isNone handler "the name added no attribute to the control"
                        let! pwned = Yession.Tests.PageScript.PageScript.held<System.Text.Json.JsonElement> page "pwned"
                        Expect.equal pwned.ValueKind System.Text.Json.JsonValueKind.Null "nothing in the name ran"
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
//
// One case at a time (`BrowserRunner`): a case's Manager starts by emptying `installedDataDir`.
let installedAppTests =
    testSequenced <| testList "Opening a session from an installed Manager (browser)" [
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
                        do! Shim.install page [ Yession.App.Shims.Shim.Matching Yession.App.Dom.Manager.installedApp ]
                        do! awaitU (page.RouteAsync ("**/sessions/*/open", fun route ->
                                route.FulfillAsync (RouteFulfillOptions (Status = 200, ContentType = "text/html", Body = "<title>opening</title>"))
                                |> ignore))
                        let! _ = await (page.GotoAsync (manager.At "/"))
                        let row = sprintf "[%s]" Yession.App.Dom.Manager.openLink
                        do! waitVisible "a session's row on the Manager" page row
                        do! awaitU (page.Locator(row).First.ClickAsync ())
                        do! Wait.until "this window to have gone to the session" (fun () -> Read.url page) (fun url -> url.AbsolutePath.EndsWith "/open")
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

/// Where a finger pushing Create lands. The pushes are the pointer's own (a second one at the
/// same place, under a held Create, is what these cases are about), so the point is measured —
/// but only once Playwright has brought Create onto the screen and found it standing still and
/// pressable (a trial click, which presses nothing). Measured before then, a point can be one
/// a press misses (the sheet's case, "pressing the pane under the list's sheet", says how).
let private whereCreateIs (page: IPage) : Async<float32 * float32> =
    async {
        do! awaitU (page.Locator(createButton).ClickAsync (LocatorClickOptions (Trial = true)))
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
                    do! waitVisible "Create, on the Manager's page" page createButton
                    try
                        do! body manager contexts page hold
                    finally
                        hold.LetGo ()
                }
            })
        finally
            manager.Stop ()
    }

// One case at a time (`BrowserRunner`): every case's Manager starts by emptying `pressDataDir`.
let pressTests =
    testSequenced <| testList "Pressing Create (browser)" [
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
                do! Wait.until "the browser to have left for the new session" (fun () -> Read.url page) (fun url -> url.Port <> manager.Port)
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
                // Archive is in the row's unfolding, so the other reader opens the row first.
                do! awaitU (other.ClickAsync (sprintf "[%s]" Yession.App.Dom.Manager.rowMark))
                do! awaitU (other.ClickAsync (sprintf "[%s]" Yession.App.Dom.Manager.archive))
                hold.LetGo ()
                do! Wait.until "the frame to have landed here" (fun () -> Read.count page (Sel.hook Yession.App.Dom.Manager.archive)) ((=) 0)
                let! busy = Read.attr page createButton "aria-busy"
                let! underTheFinger = Read.focused page createButton
                Expect.equal (busy, underTheFinger) (Some "true", true) "the Create that was pushed is still there, still down, and still the one under the finger"
            })
    ]

// --- The opening page: what a browser looks at while a session launches ---------------------
// Two promises only a browser can settle. The DWELL — the page goes once the Manager says
// the session answers, and not before the intro has landed — is a wait the page's own clock
// keeps, so it is measured from the outside with the answer faked to arrive at once: a page
// without the dwell would leave inside a second. And the switch for a reader who declined
// motion is CSS over SMIL, which only a rendered page can show applied.

let private openingDataDir = "tests/browser/.data-opening"

/// A second origin the case owns, answering every request with `html`. A real listener on
/// loopback, not a routed fake: Chromium refuses a page on a public address space the
/// requests it makes to loopback (Private Network Access), and a refusal there would leave
/// the Manager's own refusal untested.
let private elsewhere (html: string) : Serving =
    let served = listenOnLoopback ()
    let rec loop () =
        async {
            match! Async.Catch (served.Listener.GetContextAsync () |> Async.AwaitTask) with
            | Choice1Of2 ctx ->
                let bytes = Text.Encoding.UTF8.GetBytes html
                ctx.Response.ContentType <- "text/html"
                ctx.Response.OutputStream.Write (bytes, 0, bytes.Length)
                ctx.Response.Close ()
                return! loop ()
            | Choice2Of2 _ -> ()
        }
    Async.Start (loop ())
    served

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

/// Every console error the page says from now on, but an answered request's status. A 401
/// from the session's GitHub routes while nobody has connected GitHub is the product's
/// answer, said in the console by the browser, and no policy refused anything to cause it.
/// A refused script, style, font or worker is an error of its own, which stays.
let private consoleErrors (page: IPage) : ResizeArray<string> =
    let errors = ResizeArray<string> ()
    page.Console.Add (fun m ->
        if m.Type = "error" && not (m.Text.StartsWith "Failed to load resource: the server responded with a status of") then
            errors.Add m.Text)
    errors

/// The Content-Security-Policy an answer carried, or a failure naming the answer.
let private policyOf (answer: IResponse) : Async<string> =
    async {
        let! policy = await (answer.HeaderValueAsync "content-security-policy")
        return
            match Option.ofObj policy with
            | Some policy -> policy
            | None -> failwithf "%s carried no Content-Security-Policy" answer.Url
    }

/// How the intro mark and the still mark are displayed, in that order.
let private marksShown (page: IPage) : Async<string option * string option> =
    async {
        let! intro = Probe.computed page "[data-mark-intro]" "display"
        let! still = Probe.computed page "[data-mark-static]" "display"
        return intro, still
    }

// One case at a time (`BrowserRunner`): every case's Manager starts by emptying `openingDataDir`.
let openingTests =
    testSequenced <| testList "The opening page (browser)" [
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
                // The readiness has answered, so the hand-over is scheduled — and not yet due.
                do! Wait.until "the page to have heard the session answer"
                        (fun () -> Read.text page (Sel.hook Yession.App.Dom.Manager.openingWord)) ((=) (Some "ready"))
                // The intro is 2.4s and the dwell 2.8s from the page's first script: a beat
                // short of it, the page is still showing.
                do! awaitU (page.Clock.RunForAsync 2700L)
                let! at = Read.url page
                let held = at.Port = manager.Port
                Expect.isTrue held "the page held until the intro had landed"
                do! awaitU (page.Clock.RunForAsync 200L)
                do! Wait.until "the browser to have left for the session" (fun () -> Read.url page) (fun at -> at.Port <> manager.Port)
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
                do! Wait.until "the reason on the screen"
                        (fun () -> Read.text page "#status") (Option.exists (fun t -> t.Contains "exited before ready (code 3)"))
            })

        // `/open` changes nothing, so the launch is the screen's to ask for, by POST, under the
        // Manager's request-origin rule. The real launch, the real readiness and the real
        // session: the browser lands in it only if the screen asked.
        testCaseAsync "the screen launches the session by POST, and the browser lands in it" <|
            withOpening "launch by POST" (fun _ -> async { return () }) (fun manager page opening -> async {
                let launches = ResizeArray<string> ()
                page.Request.Add (fun r -> if r.Method = "POST" && r.Url.EndsWith "/launch" then launches.Add r.Url)
                let! _ = await (page.GotoAsync opening)
                do! Wait.until "the browser to have left for the session" (fun () -> Read.url page) (fun at -> at.Port <> manager.Port)
                Expect.equal (List.ofSeq launches) [ opening.Replace ("/open", "/launch") ] "one launch, sent by the screen"
            })

        // A page on another origin, in the same browser that carries the operator's identity: its form
        // cannot make a session, and its image cannot start one.
        testCaseAsync "a form on another origin cannot create a session" <|
            withOpening "a foreign form" (fun _ -> async { return () }) (fun manager page _ -> async {
                let sessionsNow () =
                    async {
                        use http = new HttpClient ()
                        let! body = await (http.GetStringAsync (manager.At "/"))
                        return Text.RegularExpressions.Regex.Matches(body, sprintf "%s=\"" Yession.App.Dom.Manager.session).Count
                    }
                let! before = sessionsNow ()
                let answers = ResizeArray<int> ()
                page.Response.Add (fun r -> if r.Request.Method = "POST" && r.Url.EndsWith "/sessions" then answers.Add r.Status)
                let foreign =
                    elsewhere (
                        sprintf "<form method=\"post\" action=\"%s\"><input name=\"name\" value=\"theirs\"><button>go</button></form>" (manager.At "/sessions"))
                try
                    let! _ = await (page.GotoAsync (foreign.At "/form"))
                    do! awaitU (page.ClickAsync "button")
                    do! Wait.until "the Manager to have answered the form" (fun () -> async { return List.ofSeq answers }) (List.isEmpty >> not)
                finally
                    foreign.Stop ()
                let! after = sessionsNow ()
                Expect.equal (List.ofSeq answers, after) ([ 403 ], before) "refused, and no session made"
            })

        testCaseAsync "an image on another origin cannot start a session" <|
            withOpening "a foreign image" (fun _ -> async { return () }) (fun _ page opening -> async {
                // Settled either way: the browser's own blocking of a cross-origin HTML answer
                // (ORB) fails the request only after the Manager has answered it.
                let settled = ResizeArray<string> ()
                page.RequestFinished.Add (fun r -> if r.Url = opening then settled.Add "finished")
                page.RequestFailed.Add (fun r -> if r.Url = opening then settled.Add r.Failure)
                let foreign = elsewhere (sprintf "<img src=\"%s\" alt=\"\">" opening)
                try
                    let! _ = await (page.GotoAsync (foreign.At "/image"))
                    do! Wait.until "the image's request to have settled" (fun () -> async { return List.ofSeq settled }) (List.isEmpty >> not)
                finally
                    foreign.Stop ()
                use http = new HttpClient ()
                let! ready = await (http.GetAsync (opening.Replace ("/open", "/ready")))
                Expect.equal (int ready.StatusCode) 503 "the session is not running, and nothing is launching it"
            })

        // Every document the Manager and a session serve runs under a Content-Security-Policy.
        // Two promises per surface: the page does what it does with nothing refused and nothing
        // said in error (so the policy admits the inline scripts by their nonce, the modules,
        // the fonts, the worker), and the document's answer forbids framing it.
        testCaseAsync "the Manager page runs with nothing refused by its policy" <|
            withOpening "the Manager page's policy" (fun _ -> async { return () }) (fun manager page _ -> async {
                let errors = consoleErrors page
                // The page's module program opens the rows stream: asked for, it ran.
                let streamed = ResizeArray<string> ()
                page.Request.Add (fun r -> if r.Url.Contains (Yession.App.ManagerRoute.path Yession.App.ManagerRoute.SessionRows) then streamed.Add r.Url)
                let! _ = await (page.GotoAsync (manager.At "/"))
                do! Wait.until "the page's program to have opened the rows stream" (fun () -> async { return streamed.Count }) ((<) 0)
                let! refused = Probe.violations page
                Expect.equal (refused, List.ofSeq errors) ([], []) "no refusal, and no error"
            })

        testCaseAsync "the Manager page cannot be framed" <|
            withOpening "the Manager page's framing" (fun _ -> async { return () }) (fun manager page _ -> async {
                let! answer = await (page.GotoAsync (manager.At "/"))
                let! policy = policyOf answer
                Expect.stringContains policy "frame-ancestors 'none'" "the Manager page's answer forbids framing it"
            })

        testCaseAsync "the opening screen runs its inline program with nothing refused" <|
            withOpening "the opening screen's policy" (readyAnswers 503) (fun _ page opening -> async {
                let errors = consoleErrors page
                let launched = ResizeArray<string> ()
                page.Request.Add (fun r -> if r.Method = "POST" && r.Url.EndsWith "/launch" then launched.Add r.Url)
                let! _ = await (page.GotoAsync opening)
                // The program is the screen's only inline script, and the launch is its first act.
                do! Wait.until "the screen's program to have asked for the launch" (fun () -> async { return launched.Count }) ((<) 0)
                let! refused = Probe.violations page
                Expect.equal (refused, List.ofSeq errors) ([], []) "no refusal, and no error"
            })

        testCaseAsync "the opening screen cannot be framed" <|
            withOpening "the opening screen's framing" (readyAnswers 503) (fun _ page opening -> async {
                let! answer = await (page.GotoAsync opening)
                let! policy = policyOf answer
                Expect.stringContains policy "frame-ancestors 'none'" "the opening screen's answer forbids framing it"
            })

        testCaseAsync "a session's page runs with nothing refused by its policy" <|
            withOpening "the session page's policy" (fun _ -> async { return () }) (fun manager page opening -> async {
                let errors = consoleErrors page
                let! _ = await (page.GotoAsync opening)
                do! Wait.until "the browser to be in the session" (fun () -> Read.url page) (fun at -> at.Port <> manager.Port)
                // The worker is the last thing the shell sets up, and `worker-src` governs it.
                do! Wait.until "the service worker to be active" (fun () -> Probe.ask page Yession.App.Probes.workerActive 0) id
                let! refused = Probe.violations page
                Expect.equal (refused, List.ofSeq errors) ([], []) "no refusal, and no error"
            })

        testCaseAsync "a session's page cannot be framed" <|
            withOpening "the session page's framing" (fun _ -> async { return () }) (fun manager page opening -> async {
                let shells = ResizeArray<IResponse> ()
                page.Response.Add (fun r ->
                    if r.Request.ResourceType = "document" && r.Status = 200 && Uri(r.Url).Port <> manager.Port then shells.Add r)
                let! _ = await (page.GotoAsync opening)
                do! Wait.until "the session's shell to have arrived" (fun () -> async { return shells.Count }) ((<) 0)
                let! policy = policyOf shells.[shells.Count - 1]
                Expect.stringContains policy "frame-ancestors 'none'" "the session shell's answer forbids framing it"
            })

        testCaseAsync "a reader who declined motion is shown the still mark, and only it" <|
            withOpening "reduced motion" (fun page -> async {
                do! awaitU (page.EmulateMediaAsync (PageEmulateMediaOptions (ReducedMotion = ReducedMotion.Reduce)))
                do! readyAnswers 503 page
            }) (fun _ page opening -> async {
                let! _ = await (page.GotoAsync opening)
                let! shown = marksShown page
                Expect.equal shown (Some "none", Some "block") "the intro is off and the still mark is on"
            })

        testCaseAsync "otherwise the intro is shown, and the still mark is not" <|
            withOpening "motion" (readyAnswers 503) (fun _ page opening -> async {
                let! _ = await (page.GotoAsync opening)
                let! shown = marksShown page
                Expect.equal shown (Some "block", Some "none") "the intro is on and the still mark is off"
            })
    ]

#else

// Fable (JS on Node): Playwright is a .NET driver and does not exist here, so the flows above
// are compiled out. These stubs only exist so the module compiles under Fable; they are never
// forced — the `[Browser]` need fails on Node and reports the skip itself.
let tests : Fable.Pyxpecto.Model.TestCase = testList "Browser E2E" []
let waitTests : Fable.Pyxpecto.Model.TestCase = testList "Browser waits" []
let editorTests : Fable.Pyxpecto.Model.TestCase = testList "Editor rendering (browser)" []
let mountedTests : Fable.Pyxpecto.Model.TestCase = testList "Path-mounted session (browser)" []
let frontDoorTests : Fable.Pyxpecto.Model.TestCase = testList "Creating a session behind a front door (browser)" []
let frontedTests : Fable.Pyxpecto.Model.TestCase = testList "A fronted deployment, for real (browser)" []
let keptOpenTests : Fable.Pyxpecto.Model.TestCase = testList "Opening a session this browser keeps (browser)" []
let filterTests : Fable.Pyxpecto.Model.TestCase = testList "The management page's filters (browser)" []
let installedAppTests : Fable.Pyxpecto.Model.TestCase = testList "Opening a session from an installed Manager (browser)" []
let pressTests : Fable.Pyxpecto.Model.TestCase = testList "Pressing Create (browser)" []
let hostileNameTests : Fable.Pyxpecto.Model.TestCase = testList "A display name on the management page (browser)" []
let openingTests : Fable.Pyxpecto.Model.TestCase = testList "The opening page (browser)" []

#endif
