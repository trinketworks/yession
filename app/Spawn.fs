module Yession.Host.Spawn

// Child-process interop for the Manager (Phase 4, Step 23): spawn a Session,
// await its readiness line, observe its exit, stop it. The spawn contract is
// environment variables in and exactly one JSON readiness line out on stdout
// (Plan 02 § Topology); everything else the child prints is
// passed through as logs.

open Fable.Core
open Fable.NodeExtras
open Yession.Manager

#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

/// Refuse a session from a different MAJOR version — the one difference that says their
/// control protocol may genuinely disagree.
///
/// This used to be a warning that launched anyway, which was defensible while the session
/// binary was whatever shipped beside the Manager. It is not defensible once a deployment
/// points `--spawn-bin` at a floating path (Plan 11), because then a major bump
/// upstream silently pairs two processes that no longer agree, and the symptom surfaces
/// later as something else entirely.
///
/// Refusing is also self-correcting where it matters: no session can start, the running set
/// drains to empty, and an operator whose promotion rule waits for quiescence restarts the
/// Manager on its own. A build that cannot state a release version (`dev`, `test`) is never
/// compared — those are the local and test paths, where the two halves are built together.
/// Both versions explicit, so the rule is testable: `Version.current` is `test` under the
/// suite, whose major is `None`, and a comparison against it could never fire.
let majorSkewBetween (managerVersion: string) (sessionVersion: string option) : string option =
    match sessionVersion with
    | None -> None
    | Some session ->
        match Version.majorOf managerVersion, Version.majorOf session with
        | Some ours, Some theirs when ours <> theirs ->
            Some (
                sprintf
                    "version skew: this manager is %s and the session binary is %s. Their control protocol may disagree, so the launch was refused. Restart the manager on the matching build."
                    managerVersion
                    session
            )
        | _ -> None

let majorSkew (sessionVersion: string option) : string option =
    majorSkewBetween Version.current sessionVersion

/// A running (or exited) child session process.
type RunningChild =
    { Pid : int
      /// SIGTERM now; the caller escalates if needed.
      Terminate : unit -> unit
      Kill : unit -> unit
      /// Register for the child's exit (fires once, with the exit code if any).
      OnExit : (int option -> unit) -> unit
      /// Has the child exited?
      HasExited : unit -> bool }

/// What a launch produced: the handle, the port it serves on, and the BUILD it reported.
///
/// The build was already on the readiness line and already read — to refuse a major skew —
/// and then dropped on the floor. Which meant the one process that knows what every live
/// session is running answered no question about it, and a session that outlived a
/// promotion kept executing an old image with nothing anywhere saying so.
///
/// `None` for a bundle older than the field, which must still launch: what the Manager does
/// not know it says nothing about, rather than inventing a version-shaped placeholder.
type LaunchedSession =
    { Child : RunningChild
      Port : int
      Build : string option }

/// The signal that asks a session for a Node diagnostic report. Node's own default, named so
/// that the flag handed to the child and the signal sent to it cannot drift apart.
[<Literal>]
let private ReportSignal = "SIGUSR2"

/// macOS's stack sampler. Present on every macOS install and absent everywhere else, so its
/// presence is the whole platform question this file needs answered.
[<Literal>]
let private Sampler = "/usr/bin/sample"

/// `NODE_OPTIONS` for the child: whatever this process was given (the deployment's own
/// `--import`, say), plus a report on `ReportSignal` written into `diagnosticsDir`.
///
/// Why this exists: a session that misses its readiness deadline has, so far, been killed
/// without a word. It prints nothing, the Manager logged nothing, and by the time anyone
/// looked the hang no longer reproduced — the process that knew why was the one the timeout
/// had just SIGKILLed. A report asked for before the kill holds its JS stack and every
/// handle and request libuv is waiting on, which is the question a hang asks.
///
/// Set by the Manager rather than by the session at boot, because a session that hangs
/// before its own code runs never reaches the line that would have turned it on. The quotes
/// are Node's own escaping for a path with a space in it.
let private childNodeOptions (diagnosticsDir: string) : string =
    let report =
        sprintf "--report-on-signal --report-signal=%s --report-directory=\"%s\"" ReportSignal diagnosticsDir
    match ProcessEnv.get "NODE_OPTIONS" with
    | Some inherited when inherited.Trim().Length > 0 -> inherited + " " + report
    | _ -> report

/// Record what a session missing its readiness deadline is stuck on, then call `kill`.
///
/// Two views, because they answer for different hangs. The Node report (`ReportSignal`) is
/// written when the child's event loop next turns, so it covers a session waiting on I/O — and
/// says nothing for one blocked in a native call, which never turns it. `sample` reads the
/// native stacks from outside, so it covers exactly that case; it exists on macOS only.
/// Both are best effort: neither may stand between a hung child and its kill, so a sampler
/// that fails to start or overruns its cap still ends in `kill`.
let private captureThenKill (pid: int) (diagnosticsDir: string) (kill: unit -> unit) : unit =
    let mutable killed = false
    let killOnce () =
        if not killed then
            killed <- true
            kill ()
    try
        Files.mkdirp diagnosticsDir
        Processes.kill pid ReportSignal
    with _ ->
        ()
    if Fs.exists Sampler then
        let sampler =
            ChildProcesses.spawn
                Sampler
                [ string pid; "2"; "-file"; sprintf "%s/sample-%d.txt" diagnosticsDir pid ]
                { Cwd = None
                  Env = ChildEnv.Adding Map.empty
                  Streams = { Stdin = Stdio.Ignore; Stdout = Stdio.Ignore; Stderr = Stdio.Ignore }
                  Detached = false }
        ChildProcessStreams.onError sampler (fun _ -> killOnce ())
        ChildProcessStreams.onExit sampler (fun _ -> killOnce ())
        // Two seconds of sampling plus its own start-up; past this it is not coming back.
        JS.setTimeout killOnce 5000 |> ignore
    else
        // Time for the report to be written: it needs one turn of the child's loop.
        JS.setTimeout killOnce 1000 |> ignore

/// Spawn `command args` with `env` merged over the parent environment, and resolve
/// once the child prints its readiness line (or fails: early exit / timeout). The
/// returned handle observes the child for the rest of its life.
///
/// `diagnosticsDir` is where a child that misses `timeoutMs` leaves its account of why
/// (`captureThenKill`). Nothing is written there for a launch that succeeds.
let launch
    (command: string)
    (args: string list)
    (env: (string * string) list)
    (diagnosticsDir: string)
    (timeoutMs: int)
    : Async<Result<LaunchedSession, string>> =
    Async.FromContinuations (fun (cont, _, _) ->
        // stdin is a pipe on purpose: the child watches it and exits when the Manager dies
        // (the kernel closes the pipe even on SIGKILL), so sessions never outlive their
        // Manager. stdout is parsed for the readiness line below; stderr is the parent's own
        // handle, so whatever the session says for a person to read lands where the Manager's
        // own output does rather than in a pipe this file would have to drain.
        //
        // `Adding` rather than `Replacing`: `env` carries the spawn contract's variables and
        // nothing else, so a child given only those would have lost `PATH` — and a Node bundle
        // that cannot find its own interpreter fails before it can say why.
        let child =
            ChildProcesses.spawn
                command
                args
                { Cwd = None
                  Env = ChildEnv.Adding (Map.ofList (("NODE_OPTIONS", childNodeOptions diagnosticsDir) :: env))
                  Streams = { Stdin = Stdio.Pipe; Stdout = Stdio.Pipe; Stderr = Stdio.Inherit }
                  Detached = false }

        let pid = int child.pid

        let mutable exited : int option option = None // Some code = exited (code option)
        let mutable exitWaiters : (int option -> unit) list = []
        // `exit` rather than `close`: a launch has to fail the moment the child is gone, and
        // `close` waits for its stdout to drain as well — a stream this launch is reading and
        // a crashing session may never have written to.
        ChildProcessStreams.onExit child (fun code ->
            exited <- Some code
            let waiters = exitWaiters
            exitWaiters <- []
            // Registration order: the Manager's bookkeeping (registered at launch)
            // must observe the exit before any stop/wait continuation resumes.
            waiters |> List.rev |> List.iter (fun w -> w code))

        let running =
            { Pid = pid
              Terminate = fun () -> child.kill "SIGTERM"
              Kill = fun () -> child.kill "SIGKILL"
              OnExit =
                fun waiter ->
                    match exited with
                    | Some code -> waiter code
                    | None -> exitWaiters <- waiter :: exitWaiters
              HasExited = fun () -> Option.isSome exited }

        let mutable settled = false
        let settle (result: Result<LaunchedSession, string>) =
            if not settled then
                settled <- true
                cont result

        let timer =
            JS.setTimeout
                (fun () ->
                    // Settle first, as the skew refusal below does: the kill fires the exit
                    // handler, whose "exited before ready" would otherwise bury the reason.
                    settle (
                        Error (
                            sprintf
                                "session process not ready within %dms; diagnostics in %s"
                                timeoutMs
                                diagnosticsDir
                        )
                    )
                    captureThenKill pid diagnosticsDir running.Kill)
                timeoutMs

        running.OnExit (fun code ->
            settle (Error (sprintf "session process exited before ready (code %A)" code)))

        // Accumulate stdout and scan complete lines for the readiness JSON; anything
        // else is a log line and passes through.
        let mutable buffer = ""
        Readables.text (ChildProcessStreams.stdout child) (fun chunk ->
            buffer <- buffer + chunk
            let parts = buffer.Split '\n'
            buffer <- parts.[parts.Length - 1]
            for line in parts.[0 .. parts.Length - 2] do
                match ReadyLine.parse line with
                | Some ready ->
                    JS.clearTimeout timer
                    // The version arrives on the readiness line, so this is the first
                    // moment the pairing can be checked — and the last moment before the
                    // Manager starts treating the child as a working session.
                    let build = ready.Version
                    match majorSkew build with
                    | Some reason ->
                        // Settle first: killing the child fires its exit handler, which
                        // would otherwise settle this launch with "exited before ready" and
                        // bury the actual reason.
                        settle (Error reason)
                        running.Kill ()
                    | None -> settle (Ok { Child = running; Port = ready.Port; Build = build })
                | None ->
                    if line.Trim().Length > 0 then printfn "[session %d] %s" pid line))
