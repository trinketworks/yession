module Yession.Tests.KillRehearsalMain

// A kill of a child that did not start, as a rehearsal makes it: in a process of its own,
// which a case starts as the leader of its own process group. Such a kill once sent SIGKILL
// to the process group of its caller. A case that made it in the process of the suite would
// end the suite, and that red says nothing. Here the kill can end only this program, and the
// case reads how the program ended: its own code after a kill that ended nothing
// (`KillRehearsal.Survived`), and a signal after a kill that ended this group.
//
// Fable compiles it to its own module and nothing in the suite imports it, so its top level
// runs only in a child started on it. The .NET CLR compiles none of it: there the suite is
// one program, and this top level would run at its start.

#if FABLE_COMPILER
open Fable.NodeExtras
open Yession.Domain
open Yession.Domain.Sandboxes
open Yession.Host
open Yession.Tests.KillRehearsal

/// A program that no file system has. A spawn of it fails, and the child has no pid.
let private missing = "/yession-no-such-program"

/// This program made the kill and is still here, so the kill ended nothing.
let private survived () : unit = Processes.exitWith Survived

/// Leave with the reason. The arrangement is not what the case names, so the red of the
/// case says why.
let private refuse (why: string) : unit =
    eprintfn "kill rehearsal: %s" why
    Processes.exitWith Refused

let private policy : SandboxPolicy =
    { ReadPaths = []
      WritePaths = []
      AllowedDomains = None
      Sockets = []
      Binds = []
      Volumes = []
      SeatbeltRules = []
      Devices = []
      Realisation = []
      Env = Map.empty
      WorkingDirectory = None
      Filesystem = Confined
      Derived = Map.empty }

/// Start `missing` as a command of the `host` backend, and give `thenKill` what a caller
/// holds of it. The backend answers with a handle: Node reports the failed spawn later.
let private command (thenKill: SandboxProcessHandle -> Async<unit>) : unit =
    async {
        match! Sandboxes.HostSandbox.create HostSpec.none policy with
        | Error why -> refuse (sprintf "the host sandbox did not start: %s" why)
        | Ok sandbox ->
            let exec =
                { Executable = missing; Arguments = []; Env = Map.empty; WorkingDirectory = None; Via = Direct }
            match! sandbox.Spawn exec ignore with
            | Error why -> refuse (sprintf "the sandbox refused the command: %s" why)
            | Ok handle -> do! thenKill handle
    }
    |> Async.StartImmediate

/// The kill comes at once, before Node reports the failed spawn. A deadline that passed
/// while the sandbox started makes this kill.
let private commandAtOnce () : unit =
    command (fun handle ->
        async {
            let mutable reported = false
            Async.StartImmediate (
                async {
                    let! _ = handle.Exited
                    reported <- true
                })
            if reported then refuse "Node reported the failed spawn before the kill"
            else
                handle.Kill ()
                survived ()
        })

/// The kill comes after the command reported that it did not start.
let private commandAfterItsEnd () : unit =
    command (fun handle ->
        async {
            match! handle.Exited with
            | SandboxRunFailed _ ->
                handle.Kill ()
                survived ()
            | other -> refuse (sprintf "the command did not fail to start: %A" other)
        })

/// The agent CLI's spawner, asked for `missing` with a signal that fired before. The spawner
/// makes its kill before it answers, so this program is still here when Node reports the
/// failed spawn only if that kill ended nothing.
let private agent () : unit =
    let controller = abortController ()
    controller.abort ()
    let spawned =
        (Sandboxes.AgentSandbox.hostClaudeSpawner ()).Invoke
            { new Fable.ClaudeAgentSdk.SpawnOptions with
                member _.command = missing
                member _.args = [||]
                member _.cwd = None
                member _.env = Fable.ClaudeAgentSdk.Environment.ofMap Map.empty
                member _.signal = Some controller.signal }
    Fable.ClaudeAgentSdk.SpawnedProcess.onError spawned (fun _ -> survived ())

let private words = Node.Api.``process``.argv |> Seq.skip 2 |> List.ofSeq

Rehearsal.endsItself "kill rehearsal"

match Kill.parse words with
| Some CommandAtOnce -> commandAtOnce ()
| Some CommandAfterItsEnd -> commandAfterItsEnd ()
| Some Agent -> agent ()
| None -> refuse (sprintf "not a kill that this program knows: %s" (String.concat " " words))
#endif
