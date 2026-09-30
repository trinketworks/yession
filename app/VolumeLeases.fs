/// Keeping a sandbox's lease on a shared volume current.
///
/// An operator who declares `maintain` on a volume (`VolumeMaintenance`) is asking every
/// sandbox holding it to say, from inside, what of the volume it still uses — because only
/// the sandbox can: what it uses is rooted in its own checkout and held open by its own
/// processes, neither of which a container outside can see. This runs the operator's `pin`
/// in the sandbox with a directory of its own to say it in — its LEASE — when the sandbox
/// comes up, every `Every` while it lives, and once more before it goes. What `pin` writes
/// is never read here; collecting is the operator's sweep, reading every lease.
///
/// A decoration of `CreateSandbox` rather than a feature of a backend: all it needs is the
/// sandbox's own `Spawn` and `Dispose`, so it is the same on every backend that can hold a
/// volume, and a test drives it with a fake sandbox and a clock turned by hand. Why the
/// product does this at all, and does no more, is
/// docs/decisions/2026-09-30-a-sandbox-leases-what-it-uses-of-a-shared-volume.md.
module Yession.Host.VolumeLeases

open System
open Yession.Domain
open Yession.Domain.Sandboxes

/// Where a sandbox's lease lives on a volume mounted at `at`: under `.yession`, which the
/// product owns on every volume it maintains, one directory per sandbox. The object name
/// is what already tells two sandboxes' containers apart (`SandboxRef.objectName`), so it
/// tells their leases apart too — including two sessions' same-named sandboxes, whose
/// roots are exactly the ones that collided on the volume before this existed.
let leaseDir (at: string) (objectName: string) : string =
    sprintf "%s/.yession/leases/%s" (at.TrimEnd '/') objectName

/// One volume this sandbox holds that the operator asked to have leased.
type Lease =
    { Volume : string
      Dir : string
      Maintenance : VolumeMaintenance }

/// The leases a sandbox's granted volumes call for — only the maintained ones.
let leasesFor (maintenance: Map<string, VolumeMaintenance>) (objectName: string) (volumes: (string * string) list) : Lease list =
    volumes
    |> List.distinct
    |> List.choose (fun (volume, at) ->
        maintenance
        |> Map.tryFind volume
        |> Option.map (fun m -> { Volume = volume; Dir = leaseDir at objectName; Maintenance = m }))

/// How long one pin may run before it is killed. A pin asks a question of the sandbox and
/// writes the answer down; one that has not finished in this long is stuck, and a stuck one
/// must neither hold the next renewal back forever nor keep a sandbox from being removed.
let pinLimit = TimeSpan.FromMinutes 2.0

/// `handle`'s end, or `None` once `limit` has passed first — killing it then.
let private within (clock: Clock) (limit: TimeSpan) (handle: SandboxProcessHandle) : Async<SandboxRun option> =
    Async.FromContinuations (fun (resolve, _, _) ->
        let gate = obj ()
        let mutable settled = false
        let settle (value: SandboxRun option) =
            let first = lock gate (fun () -> if settled then false else settled <- true; true)
            if first then resolve value
        Async.StartImmediate (async { let! run = handle.Exited in settle (Some run) })
        Async.StartImmediate (
            async {
                do! clock.After limit
                if not (lock gate (fun () -> settled)) then
                    handle.Kill ()
                    settle None
            }))

/// Run `lease`'s pin once, to its end or its limit, and say what went wrong if anything
/// did. Output is kept only to report a failure with: a pin that succeeds says nothing.
let pinOnce (clock: Clock) (log: string -> unit) (sandbox: Sandbox) (lease: Lease) : Async<unit> =
    async {
        let output = Text.StringBuilder ()
        let exec =
            { Executable = lease.Maintenance.Pin
              Arguments = [ lease.Dir ]
              Env = Map.empty
              WorkingDirectory = None
              // Bare: an entrypoint may be a whole devshell assembled per process, and a
              // renewal is meant to be cheap enough to run every few minutes.
              Via = Direct }
        match! sandbox.Spawn exec (fun (_, chunk) -> if output.Length < 4096 then output.Append chunk |> ignore) with
        | Error e -> log (sprintf "volume %s: pin %s could not start: %s" lease.Volume lease.Maintenance.Pin e)
        | Ok handle ->
            let said () =
                let text = output.ToString().Trim ()
                if text = "" then "" else sprintf ": %s" text
            match! within clock pinLimit handle with
            | Some (SandboxExited 0) -> ()
            | Some (SandboxExited code) ->
                log (sprintf "volume %s: pin %s exited %d%s" lease.Volume lease.Maintenance.Pin code (said ()))
            | Some (SandboxRunFailed reason) ->
                log (sprintf "volume %s: pin %s failed: %s%s" lease.Volume lease.Maintenance.Pin reason (said ()))
            | None ->
                log (sprintf "volume %s: pin %s was still running after %gs and was killed" lease.Volume lease.Maintenance.Pin pinLimit.TotalSeconds)
    }

/// One lease kept current: pinned now, every interval after, and once more at the end.
/// A renewal that comes due while the last one still runs is skipped rather than queued —
/// two pins of one lease at once would race each other's writes, and the next renewal is
/// only an interval away.
type private Keeper (clock: Clock, log: string -> unit, sandbox: Sandbox, lease: Lease) =
    let gate = obj ()
    let mutable running = false
    let mutable waiters : (unit -> unit) list = []
    let mutable stopTimer : unit -> unit = ignore

    let finished () =
        let toWake =
            lock gate (fun () ->
                running <- false
                let woken = waiters
                waiters <- []
                woken)
        toWake |> List.iter (fun wake -> wake ())

    /// Start a pin, unless one is already running.
    member _.Pin () =
        let start = lock gate (fun () -> if running then false else (running <- true; true))
        if start then
            Async.StartImmediate (
                async {
                    try
                        try
                            do! pinOnce clock log sandbox lease
                        with e ->
                            log (sprintf "volume %s: pin %s: %s" lease.Volume lease.Maintenance.Pin e.Message)
                    finally
                        finished ()
                })

    /// Resolves once no pin is running.
    member _.Idle () : Async<unit> =
        Async.FromContinuations (fun (resolve, _, _) ->
            let now = lock gate (fun () -> if running then (waiters <- (fun () -> resolve ()) :: waiters; false) else true)
            if now then resolve ())

    member this.Start () =
        this.Pin ()
        stopTimer <- Clock.every clock lease.Maintenance.Every (fun () -> this.Pin ())

    /// Stop renewing, let a renewal in flight finish, then pin the last time.
    member this.Finish () : Async<unit> =
        async {
            stopTimer ()
            do! this.Idle ()
            this.Pin ()
            do! this.Idle ()
        }

/// `create`, with every maintained volume the sandbox holds leased for as long as it lives.
/// A sandbox holding none comes back exactly as `create` made it. A pin that fails is said
/// and never stops anything: the sandbox is the session's work, and a lease is a courtesy
/// to a sweep that has to allow for a stale one anyway.
let around
    (clock: Clock)
    (log: string -> unit)
    (maintenance: Map<string, VolumeMaintenance>)
    (objectName: string)
    (create: CreateSandbox)
    : CreateSandbox =
    fun policy ->
        async {
            match! create policy with
            | Error e -> return Error e
            | Ok sandbox ->
                match leasesFor maintenance objectName policy.Volumes with
                | [] -> return Ok sandbox
                | leases ->
                    let keepers = leases |> List.map (fun lease -> Keeper (clock, log, sandbox, lease))
                    keepers |> List.iter (fun keeper -> keeper.Start ())
                    return
                        Ok
                            { sandbox with
                                Dispose =
                                    fun () ->
                                        async {
                                            for keeper in keepers do
                                                do! keeper.Finish ()
                                            do! sandbox.Dispose ()
                                        } }
        }
