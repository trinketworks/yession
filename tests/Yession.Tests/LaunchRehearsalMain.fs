module Yession.Tests.LaunchRehearsalMain

// A launch of a session that goes wrong, as a rehearsal makes it: in a process of its own.
// Two kinds of launch once did more than fail.
//
// - A launch of a session whose program does not run ended its Manager. Node reports a spawn
//   that failed as the `error` of the child, nothing heard it, and Node ends a process for an
//   `error` that nobody hears. A case that made the launch in the process of the suite would
//   end the suite, and that red says nothing. Here the launch can end only this program.
// - A launch of a session that ended before it was ready kept its deadline. The deadline held
//   the process of the launch, and at its time it signalled a pid that was gone. Here the
//   deadline is far away, and this program ends long before it: by itself when nothing holds
//   it, and at its cap when the deadline does (`Rehearsal.endsItself`).
//
// The case reads how the program ended, and the failure of the launch from its output.
//
// Fable compiles it to its own module and nothing in the suite imports it, so its top level
// runs only in a child whose program is this file. The .NET CLR compiles none of it: there
// the suite is one program, and this top level would run first.

#if FABLE_COMPILER
open Fable.NodeExtras
open Yession.Oidc
open Yession.Host
open Yession.Tests.LaunchRehearsal

/// Leave with the reason. What happened is not a failure of the launch, so the red of the
/// case says what it was.
let private refuse (why: string) : unit =
    eprintfn "launch rehearsal: %s" why
    Processes.exitWith Refused

/// A Manager over `dataDir` whose sessions are `program`, asked for one session. This program
/// writes the failure of that launch on its output, and leaves with `Answered`.
let private byManager (program: Program) (dataDir: string) : unit =
    async {
        let! manager =
            ProcessManager.create
                { ProcessManager.Options.defaults dataDir (Program.path program) [] with
                    Strategy = Some Strategy.localhost }
        match manager.CreateSession "no-run" "No run" with
        | Error why -> refuse (sprintf "the Manager did not make the session: %s" why)
        | Ok record ->
            // A launch that throws is no answer. `Async.Catch` hears it, so the red of the
            // case says that the launch threw, and not that the rehearsal did not end.
            match! Async.Catch (manager.Launch record.SessionId) with
            | Choice1Of2 (Error reason) ->
                printfn "%s" reason
                Processes.exitWith Answered
            | Choice1Of2 (Ok port) -> refuse (sprintf "the launch gave a session on port %d" port)
            | Choice2Of2 thrown -> refuse (sprintf "the launch threw: %s" thrown.Message)
    }
    |> Async.StartImmediate

/// The deadline of the launch that `alone` makes. It is far away: this program must be gone
/// long before a deadline that stayed could come, because its steps name a pid.
[<Literal>]
let private farDeadlineMs = 600000

/// A launch with no Manager around it, of a stand-in session that ends before it is ready.
/// This program writes the failure of that launch, says the code that it leaves with, and
/// does nothing more. It does not ask for its end: with nothing of the launch left, Node
/// ends it at once.
let private alone (dataDir: string) : unit =
    async {
        let session = StandIn.command (StandIn.Session (dataDir + "/spawned", StandIn.ExitAfter (0, 3)))
        let! answer =
            Async.Catch (
                Spawn.launch Node.Api.``process``.execPath session [] (dataDir + "/launch-diagnostics") farDeadlineMs)
        match answer with
        | Choice1Of2 (Error reason) when reason.Contains "exited before ready" ->
            printfn "%s" reason
            Processes.setExitCode Answered
        | Choice1Of2 (Error reason) -> refuse (sprintf "the launch failed, but not on a session that ended: %s" reason)
        | Choice1Of2 (Ok launched) -> refuse (sprintf "the launch gave a session on port %d" launched.Port)
        | Choice2Of2 thrown -> refuse (sprintf "the launch threw: %s" thrown.Message)
    }
    |> Async.StartImmediate

Rehearsal.endsItself "launch rehearsal"

match Node.Api.``process``.argv |> Seq.skip 2 |> List.ofSeq with
| [ word; dataDir ] ->
    match Launch.parse word with
    | Some (Of program) -> byManager program dataDir
    | Some EndsBeforeReady -> alone dataDir
    | None -> refuse (sprintf "not a launch that this rehearsal knows: %s" word)
| words -> refuse (sprintf "this rehearsal takes a launch and a data directory, and not: %s" (String.concat " " words))
#endif
