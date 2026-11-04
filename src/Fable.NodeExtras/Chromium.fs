namespace Fable.NodeExtras

// A headless Chromium on a debugging port: found one way, started one way, and gone when the
// process that started it is.
//
// `tools/Yession.Frames` (the camera) wrote this for itself, and anything else on Node that
// wanted a real browser to speak CDP to would have written it again: the executable's
// directory layout, the flags a root container needs, the port file, the target list, and
// what to say when Chromium refuses to start. A second copy would agree with the first until
// the day one of them learned something — `--no-sandbox` as root was learned exactly that way,
// by the camera alone. So it is here, beside the protocol client it hands a page to.
//
// Here and not in a project of its own for the reason `Cdp.fs` is: what it stands on is this
// project — the spawn and stream bindings above, and `Cdp.pageOn` for the page to connect to.

open Fable.Core
open Node.ChildProcess

[<RequireQualifiedAccess>]
module Chromium =

    // --- Finding it -------------------------------------------------------------------------

    /// What finding Chromium reads, read here through Node: the rule itself is
    /// `ChromiumExecutable`, which `tasks.fsx` asks too, through System.IO.
    let private machine : ChromiumMachine =
        { Variable = fun name -> ProcessEnv.get name
          Entries = fun dir -> Node.Api.fs.readdirSync (U2.Case1 dir) |> Seq.toList
          IsDirectory = fun path -> try (Node.Api.fs.statSync (U2.Case1 path)).isDirectory () with _ -> false
          Resolve = fun path -> try Node.Api.fs.realpathSync (U2.Case1 path) with _ -> path
          Join = fun a b -> Node.Api.path.join (a, b) }

    // --- Starting and stopping it -------------------------------------------------------------

    /// A Chromium this process started.
    type Launched =
        { /// The browser's own process.
          Process: ChildProcess
          /// The address `Cdp.connect` takes for the page it opened on.
          Page: string
          /// The profile directory it runs on, made for it and removed with it.
          Profile: string
          /// Settles once the browser AND every helper it started have ended. The helpers —
          /// zygotes, renderers, GPU, network and storage services, crash handlers — are not
          /// the browser's process, and not its process group's to take with it (it has none:
          /// a detached browser would outlive a Ctrl-C to the terminal that started it). A
          /// killed browser's helpers end on their own, tens of milliseconds to a second
          /// later, and until then they write into the profile: a network service still
          /// starting up re-made the profile directory after it was removed. What they all
          /// share is the browser's stderr, which each inherits, and the OS closes that pipe
          /// only when the last process holding it has ended. So this is the child's `close`,
          /// which Node emits after its exit AND that pipe's end — and also after a spawn that
          /// failed, which has no helpers.
          Gone: JS.Promise<unit> }

    /// Whether the browser's process is over — or never was, for a spawn that failed, which
    /// has no pid and may never report an exit at all.
    let private ended (child: ChildProcess) : bool =
        ChildProcesses.exitCode child |> Option.isSome
        || ChildProcesses.signalCode child |> Option.isSome
        || isNull (box child.pid)

    /// Stop the browser at once and remove its profile, without waiting for it to end: for the
    /// `exit` listener `launch` hangs, which runs in a process that is already leaving and can
    /// wait for nothing, `Gone` included. Its removal is retried, because the browser and its
    /// helpers are still writing into the profile as it goes (`ENOTEMPTY`): ten retries 50ms
    /// apart and growing is 2.75s. That is a best effort, and only this path makes it.
    let private abandon (launched: Launched) : unit =
        // The child's own `kill`, not a signal to its pid: it does nothing to a child already
        // reaped, where a pid may since have been handed to somebody else's process.
        launched.Process.kill "SIGKILL" |> ignore
        Files.removeTreeRetrying 10 50 launched.Profile

    /// Stop the browser, wait until it and every helper it started have ended (`Gone`), and
    /// remove its profile. SIGKILL rather than a request to close: nothing in the profile is
    /// wanted afterwards, and a browser asked politely goes on writing to it while it goes.
    /// Waiting for `Gone` first is the point: a killed browser is not a dead one until the OS
    /// says so, its helpers outlive it, and a removal begun while any of them runs either
    /// walks a directory still being written into or is undone by a helper that writes into
    /// it afterwards — a removal that answered `None` for a profile that was still there.
    ///
    /// Answers why the profile could not be removed, or `None` when it was. It does not throw
    /// for that: the profile is a temporary directory, and whatever the browser was started
    /// for has already been done — a caller that throws here turns a picture it has into a
    /// failure it reports. A second call finds the process ended and the directory gone, and
    /// does nothing.
    let close (launched: Launched) : JS.Promise<string option> =
        promise {
            let child = launched.Process
            if not (ended child) then
                child.kill "SIGKILL" |> ignore
            do! launched.Gone
            try
                Files.removeTree launched.Profile
                return None
            with error ->
                return Some (sprintf "Chromium's profile %s was not removed: %s" launched.Profile error.Message)
        }

    /// How much of Chromium's stderr is kept to explain a launch that failed: its LAST bytes,
    /// because a refusal is the last thing it says, and a browser that started keeps talking
    /// on stderr for as long as it runs.
    let private stderrKept = 4096

    /// How long a Chromium that is still running may take to write its port file. Warm, it
    /// writes it in about 200ms. Cold — the first launch after the dev container started,
    /// with nothing of a 300MB browser in the page cache — one took 26s on a 4-core box, and
    /// the 10s this once allowed would have refused a browser that was only slow. The wait
    /// costs nothing for a Chromium that cannot start: that one exits, and fails the launch
    /// the moment it does.
    let private portWaitSeconds = 60

    /// A headless Chromium on a debugging port of the OS's choosing, read back from the file it
    /// writes into its profile directory — no port to collide on — and the first page it lists.
    /// `extra` is passed to it beside the flags every launch needs; `pause` is the caller's
    /// clock, which the waits for the port file and the page are spent on.
    ///
    /// A Chromium that will not start says why on stderr and exits, so both are watched: an exit
    /// before the port file appears fails the launch AT ONCE, with the code and what it said.
    /// With stderr ignored, the only sentence left was "never wrote DevToolsActivePort", ten
    /// seconds later, for every cause alike. A failed launch answers `Error` with that sentence,
    /// having stopped whatever it started.
    ///
    /// The browser does not outlive this process: an `exit` listener closes it. A caller that
    /// ended in a fault — a camera whose page never answered, a suite narrowed past its last
    /// case — would otherwise leave a headless Chromium running with nobody holding its address.
    /// It cannot help a process ended by a signal it does not handle, which skips `exit`.
    let launch
        (pause: int -> JS.Promise<unit>)
        (width: int)
        (height: int)
        (extra: string list)
        : JS.Promise<Result<Launched, string>> =
        promise {
            match ChromiumExecutable.find machine with
            | Error why -> return Error why
            | Ok executable ->
                let profile =
                    Node.Api.fs.mkdtempSync (Node.Api.path.join (Node.Api.os.tmpdir (), "yession-chromium-"))
                // `--no-sandbox` as root, as Playwright passes it: Chromium refuses to start as uid
                // 0 with its own sandbox on, and an agent's work sandbox runs as root. The suite
                // never met this because it launches through Playwright; the camera met it as a
                // browser that exited before writing its port. The sandbox it gives up is
                // Chromium's renderer confinement, inside a container that is already the boundary.
                let sandbox = if Processes.isRoot () then [ "--no-sandbox" ] else []
                let args =
                    // `--use-mock-keychain`, as Playwright passes: without it a Chromium the OS has
                    // not seen before stops in its network service, waiting on a Keychain prompt
                    // nobody can answer, and the first navigation never gets a response.
                    [ "--headless=new"; "--remote-debugging-port=0"; sprintf "--user-data-dir=%s" profile; "--no-first-run"
                      "--use-mock-keychain"; "--password-store=basic"; "--ignore-certificate-errors"
                      sprintf "--window-size=%d,%d" width height ]
                    @ sandbox @ extra @ [ "about:blank" ]
                let child =
                    ChildProcesses.spawn
                        executable
                        args
                        { Cwd = None
                          Env = ChildEnv.Adding Map.empty
                          Streams = { Stdin = Stdio.Ignore; Stdout = Stdio.Ignore; Stderr = Stdio.Pipe }
                          Detached = false }
                // `Gone` rests on the stderr pipe above, which every helper inherits: with stderr
                // ignored, `close` would hear the browser's own exit and nothing after it.
                let gone = Promise.create (fun resolve _ -> ChildProcessStreams.onClose child (fun _ -> resolve ()))
                let launched = { Process = child; Page = ""; Profile = profile; Gone = gone }
                // A process that is leaving has nobody left to tell that the profile would not
                // go, and a throw here would change the code it leaves with.
                Processes.onEnding "exit" (fun () -> try abandon launched with _ -> ())
                // Read for the browser's whole life, not only the launch: a pipe nobody drains
                // fills, and a Chromium blocked writing a log line is a browser that stops.
                let said = ref ""
                Readables.text (ChildProcessStreams.stderr child) (fun chunk ->
                    let kept = said.Value + chunk
                    said.Value <- if kept.Length > stderrKept then kept.Substring (kept.Length - stderrKept) else kept)
                // How it ended, kept apart: an exit's code (`None` when a signal took it), and the
                // spawn's own error, which is the only place a missing or non-executable binary
                // says so — Chromium never ran, so its stderr is empty and the errno is the whole
                // answer.
                let exited : int option option ref = ref None
                let unspawned : string option ref = ref None
                ChildProcessStreams.onExit child (fun code -> exited.Value <- Some code)
                ChildProcessStreams.onError child (fun error -> unspawned.Value <- Some (StreamError.describe error))
                /// Stop what was started, and say why with what Chromium said. A profile left
                /// behind is not why the launch failed, so its own reason is not added to this one.
                let failed (why: string) : JS.Promise<Result<Launched, string>> =
                    promise {
                        let stderr = said.Value.Trim ()
                        let! _ = close launched
                        return Error (sprintf "%s\n%s" why (if stderr = "" then "(Chromium wrote nothing to stderr)" else stderr))
                    }
                let portFile = Node.Api.path.join (profile, "DevToolsActivePort")
                let mutable port = ""
                let mutable waited = 0
                while port = "" && exited.Value.IsNone && unspawned.Value.IsNone && waited < portWaitSeconds * 10 do
                    if Node.Api.fs.existsSync (U2.Case1 portFile) then
                        port <- (Node.Api.fs.readFileSync (portFile, "utf8")).Split('\n').[0].Trim ()
                    if port = "" then
                        do! pause 100
                        waited <- waited + 1
                if port = "" then
                    match unspawned.Value, exited.Value with
                    | Some why, _ -> return! failed (sprintf "Chromium (%s) could not be started: %s. Its stderr:" executable why)
                    | None, Some (Some code) ->
                        return! failed (sprintf "Chromium (%s) exited with code %d before writing DevToolsActivePort. Its stderr:" executable code)
                    | None, Some None ->
                        return! failed (sprintf "Chromium (%s) was ended by a signal before writing DevToolsActivePort. Its stderr:" executable)
                    | None, None ->
                        return! failed (sprintf "Chromium (%s) never wrote DevToolsActivePort in %ds. Its stderr:" executable portWaitSeconds)
                else
                    let mutable target = ""
                    waited <- 0
                    while target = "" && waited < 50 do
                        try
                            let! page = Cdp.pageOn port
                            match page with
                            | Some address -> target <- address
                            | None -> ()
                        with _ -> ()
                        if target = "" then
                            do! pause 200
                            waited <- waited + 1
                    if target = "" then
                        return! failed (sprintf "Chromium (%s) offered no page target in 10s. Its stderr:" executable)
                    else
                        return Ok { launched with Page = target }
        }
