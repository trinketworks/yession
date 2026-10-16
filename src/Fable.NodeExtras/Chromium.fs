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

    /// What the browser's executable is called, by platform: `chrome` on Linux, the app
    /// bundle's binary on macOS. Matched by NAME, because the directories around it differ per
    /// platform and have changed name across Playwright builds.
    let private executableNames = set [ "chrome"; "Google Chrome for Testing"; "Chromium" ]

    /// A variable's value, with one set to nothing read as one not set.
    let private named (name: string) : string option =
        match ProcessEnv.get name with
        | Some "" -> None
        | value -> value

    /// The suite's rule (`tests/Yession.Tests/Browser.fs`): `CHROMIUM_PATH` if set, else the
    /// executable found BY NAME under a `chromium-<revision>` directory of
    /// `PLAYWRIGHT_BROWSERS_PATH`. No fallback to a system Chrome, for the reason given there —
    /// a revision is pinned so the browser driven here is the one the suite drives.
    let private executable () : Result<string, string> =
        match named "CHROMIUM_PATH" with
        | Some given -> Ok given
        | None ->
            match named "PLAYWRIGHT_BROWSERS_PATH" with
            | None ->
                Error "no Chromium: PLAYWRIGHT_BROWSERS_PATH is unset (the dev shell sets it); outside it, set CHROMIUM_PATH"
            | Some root ->
                let join (a: string) (b: string) : string = Node.Api.path.join (a, b)
                let entries (dir: string) : string list = Node.Api.fs.readdirSync (U2.Case1 dir) |> Seq.toList

                let isDirectory (path: string) : bool =
                    try (Node.Api.fs.statSync (U2.Case1 path)).isDirectory () with _ -> false

                let realPath (path: string) : string =
                    try Node.Api.fs.realpathSync (U2.Case1 path) with _ -> path

                let rec walk (dir: string) : string list =
                    entries dir
                    |> List.collect (fun name ->
                        let path = join dir name
                        if isDirectory path then walk path
                        elif executableNames.Contains name then [ path ]
                        else [])

                // `chromium-*` and not `chromium*`: `chromium_headless_shell-<rev>` sits beside it
                // and is a different, cut-down browser.
                let revisions =
                    if isDirectory root then
                        entries root |> List.filter (fun d -> d.StartsWith "chromium-") |> List.sort
                    else []

                match revisions |> List.collect (fun d -> try walk (realPath (join root d)) with _ -> []) with
                | found :: _ -> Ok found
                | [] ->
                    Error (sprintf "no Chromium under %s (%d chromium-* revision(s)); set CHROMIUM_PATH" root (List.length revisions))

    // --- Starting and stopping it -------------------------------------------------------------

    /// A Chromium this process started.
    type Launched =
        { /// The browser's own process.
          Process: ChildProcess
          /// The address `Cdp.connect` takes for the page it opened on.
          Page: string
          /// The profile directory it runs on, made for it and removed with it.
          Profile: string }

    /// Stop the browser at once, and remove its profile. SIGKILL rather than a request to
    /// close: nothing in the profile is wanted afterwards, and a browser asked politely goes on
    /// writing to it while it goes, under a removal that is already walking it. Synchronous, so
    /// the exit listener `launch` hangs can call it from a process that is already leaving. A
    /// second call finds the process gone and the directory gone, and does nothing.
    let close (launched: Launched) : unit =
        // The child's own `kill`, not a signal to its pid: it does nothing to a child already
        // reaped, where a pid may since have been handed to somebody else's process.
        launched.Process.kill "SIGKILL" |> ignore
        Files.removeTree launched.Profile

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
            match executable () with
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
                let launched = { Process = child; Page = ""; Profile = profile }
                // A process that is leaving has nobody left to tell that the profile would not
                // go, and a throw here would change the code it leaves with.
                Processes.onEnding "exit" (fun () -> try close launched with _ -> ())
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
                /// Stop what was started, and say why with what Chromium said.
                let failed (why: string) : Result<Launched, string> =
                    let stderr = said.Value.Trim ()
                    close launched
                    Error (sprintf "%s\n%s" why (if stderr = "" then "(Chromium wrote nothing to stderr)" else stderr))
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
                    | Some why, _ -> return failed (sprintf "Chromium (%s) could not be started: %s. Its stderr:" executable why)
                    | None, Some (Some code) ->
                        return failed (sprintf "Chromium (%s) exited with code %d before writing DevToolsActivePort. Its stderr:" executable code)
                    | None, Some None ->
                        return failed (sprintf "Chromium (%s) was ended by a signal before writing DevToolsActivePort. Its stderr:" executable)
                    | None, None ->
                        return failed (sprintf "Chromium (%s) never wrote DevToolsActivePort in %ds. Its stderr:" executable portWaitSeconds)
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
                        return failed (sprintf "Chromium (%s) offered no page target in 10s. Its stderr:" executable)
                    else
                        return Ok { launched with Page = target }
        }
