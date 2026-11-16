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

/// Record what a session missing its readiness deadline is stuck on, then kill it.
///
/// Two views, because they answer for different hangs. The Node report (`ReportSignal`) is
/// written when the child's event loop next turns, so it covers a session waiting on I/O — and
/// says nothing for one blocked in a native call, which never turns it. `sample` reads the
/// native stacks from outside, so it covers exactly that case; it exists on macOS only.
/// Both are best effort: neither may stand between a hung child and its kill, so a sampler
/// that fails to start or overruns its cap still ends in `kill`.
///
/// A child that ended gets no part of this. It has nothing to record, and the system can
/// give its pid to a different process. The signal for a report would then go to that
/// process, and the default action of that signal ends a process.
let private captureThenKill (child: RunningChild) (diagnosticsDir: string) : unit =
    if not (child.HasExited ()) then
        let pid = child.Pid
        let mutable killed = false
        let killOnce () =
            if not killed then
                killed <- true
                child.Kill ()
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

/// Why the system did not run the program of a session, as the failure of its launch.
///
/// `said` is what Node said about the spawn, and the failure keeps each word of it. For a
/// cause that Node reports as an `error`, that is `spawn <program> <code>`. Node leaves the
/// program out when it throws from the spawn, so the failure then names the program first.
let private didNotStart (command: string) (said: string) : string =
    if said.Contains command then sprintf "session process did not start: %s" said
    else sprintf "session process did not start: %s: %s" command said

/// Wait for the readiness line of a session whose program runs, and give `answer` the
/// session, or the failure: an early exit, or no line in `timeoutMs`. The handle in the
/// answer observes the child for the rest of its life. `answer` takes the first answer of a
/// launch and no other (`launch`), so a step here that answers first has the last word.
///
/// `pid` is the pid of `child`. Node gives a pid only to a child whose program runs
/// (`launch`). So each step here that names a pid is for a process that is there: a log
/// line, the signal for a report, the stack sampler.
///
/// `diagnosticsDir` is where a child that misses `timeoutMs` leaves its account of why
/// (`captureThenKill`). A launch that succeeds writes nothing there.
let private awaitReady
    (child: Node.ChildProcess.ChildProcess)
    (pid: int)
    (diagnosticsDir: string)
    (timeoutMs: int)
    (answer: Result<LaunchedSession, string> -> unit)
    : unit =
    // Node also reports a signal that the kernel refused as the `error` of a child, and it
    // ends a process for an `error` that nobody hears. The session is still there after such
    // a refusal. So the Manager hears it, says it, and goes on.
    ChildProcessStreams.onError child (fun error -> eprintfn "[session %d] %s" pid (StreamError.describe error))

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
          // Through `ChildProcesses.signal`: a session that did not start gets no signal.
          Terminate = fun () -> ChildProcesses.signal child "SIGTERM" |> ignore
          Kill = fun () -> ChildProcesses.signal child "SIGKILL" |> ignore
          OnExit =
            fun waiter ->
                match exited with
                | Some code -> waiter code
                | None -> exitWaiters <- waiter :: exitWaiters
          HasExited = fun () -> Option.isSome exited }

    let timer =
        JS.setTimeout
            (fun () ->
                // Answer first, as the skew refusal below does: the kill fires the exit
                // handler, whose "exited before ready" would otherwise bury the reason.
                answer (
                    Error (
                        sprintf
                            "session process not ready within %dms; diagnostics in %s"
                            timeoutMs
                            diagnosticsDir
                    )
                )
                captureThenKill running diagnosticsDir)
            timeoutMs

    running.OnExit (fun code ->
        // A session that ended has no deadline to miss. Before, the deadline stayed: it
        // held the Manager's loop, and at its time it made its steps for a pid that was gone.
        JS.clearTimeout timer
        answer (Error (sprintf "session process exited before ready (code %A)" code)))

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
                    // Answer first: killing the child fires its exit handler, which
                    // would otherwise answer this launch with "exited before ready" and
                    // bury the actual reason.
                    answer (Error reason)
                    running.Kill ()
                | None -> answer (Ok { Child = running; Port = ready.Port; Build = build })
            | None ->
                if line.Trim().Length > 0 then printfn "[session %d] %s" pid line)

/// Spawn `command args` with `env` merged over the parent environment, and resolve
/// once the child prints its readiness line (or fails: no spawn / early exit / timeout). The
/// returned handle observes the child for the rest of its life.
///
/// A program that the system does not run is a failure of the launch, with the cause in its
/// words. Node says such a spawn in one of two ways. For five causes the spawn answers with
/// a child that has no pid, and Node reports the cause later as the `error` of that child:
/// ENOENT, EACCES, EAGAIN, EMFILE and ENFILE (a program that is not there, a file that the
/// user may not run, no free process, no free file handle). For each other cause Node
/// throws from the spawn (a path that goes through a file, a file whose format the system
/// cannot run, a name that Node refuses). Before, nothing heard that `error`, and Node ends
/// a process for an `error` that nobody hears. So the Manager ended, and each of its
/// sessions with it.
///
/// `diagnosticsDir` is where a child that misses `timeoutMs` leaves its account of why
/// (`captureThenKill`). A launch that succeeds writes nothing there.
let launch
    (command: string)
    (args: string list)
    (env: (string * string) list)
    (diagnosticsDir: string)
    (timeoutMs: int)
    : Async<Result<LaunchedSession, string>> =
    Async.FromContinuations (fun (cont, _, _) ->
        // A launch answers once. Each way out of it goes through `answer`, which takes the
        // first answer and no other.
        let mutable answered = false
        let answer (result: Result<LaunchedSession, string>) : unit =
            if not answered then
                answered <- true
                cont result

        // stdin is a pipe on purpose: the child watches it and exits when the Manager dies
        // (the kernel closes the pipe even on SIGKILL), so sessions never outlive their
        // Manager. `awaitReady` reads stdout for the readiness line. stderr is the parent's own
        // handle, so whatever the session says for a person to read lands where the Manager's
        // own output does rather than in a pipe this file would have to drain.
        //
        // `Adding` rather than `Replacing`: `env` carries the spawn contract's variables and
        // nothing else, so a child given only those would have lost `PATH` — and a Node bundle
        // that cannot find its own interpreter fails before it can say why.
        let spawned =
            try
                Ok (
                    ChildProcesses.spawn
                        command
                        args
                        { Cwd = None
                          Env = ChildEnv.Adding (Map.ofList (("NODE_OPTIONS", childNodeOptions diagnosticsDir) :: env))
                          Streams = { Stdin = Stdio.Pipe; Stdout = Stdio.Pipe; Stderr = Stdio.Inherit }
                          Detached = false })
            with thrown -> Error (didNotStart command (StreamError.describe (StreamError.ofThrown thrown)))

        match spawned with
        | Error reason -> answer (Error reason)
        | Ok child ->
            match ChildProcesses.pid child with
            | None ->
                ChildProcessStreams.onError child (fun error ->
                    answer (Error (didNotStart command (StreamError.describe error))))
            | Some pid -> awaitReady child pid diagnosticsDir timeoutMs answer)
