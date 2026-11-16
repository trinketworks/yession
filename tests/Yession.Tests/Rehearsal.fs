module Yession.Tests.Rehearsal

// A rehearsal is a program of the suite that a case runs as a process of its own. It is for
// an action that can end the process that makes it. A case that made such an action in the
// process of the suite would end the suite, and that red says nothing. Here the action can
// end only the rehearsal, and the case reads how the rehearsal ended and what it wrote.
//
// `KillRehearsalMain.fs` and `LaunchRehearsalMain.fs` are the programs. Each one says which
// action it makes, and why that action is a rehearsal.

open Fable.Core
open Fable.NodeExtras

/// How long a rehearsal lives at most (`endsItself`). It is less than the 30 seconds that
/// `run` waits. So a rehearsal that something holds ends with its own code and its own words,
/// before `run` ends it with none.
[<Literal>]
let private capMs = 20000

/// The code that a rehearsal leaves with when its cap ends it (`endsItself`).
[<Literal>]
let Capped = 44

/// `action` after `ms`, on a timer that does not hold the process: a process that has
/// nothing left to do ends before the timer comes.
[<Emit("setTimeout($0, $1).unref()")>]
let private later (action: unit -> unit) (ms: int) : unit = jsNative

/// For a rehearsal program, as its first step: the program ends itself after `capMs`, with
/// the code `Capped`. `name` is the program in the words that it says then.
///
/// A case runs a rehearsal as the leader of a process group of its own, with no input from
/// its runner. So a rehearsal that holds a listener stays when somebody ends the runner in
/// the middle of the case. The cap is the safety net for that. A rehearsal that makes its
/// action and leaves is gone long before it.
[<Support.Waits>]
let endsItself (name: string) : unit =
    later
        (fun () ->
            eprintfn "%s: this program is still here after %d seconds, so it ends itself" name (capMs / 1000)
            Processes.exitWith Capped)
        capMs

/// How a rehearsal ended: its code, or the signal that ended it, and what it wrote on its
/// output.
type Ending =
    { Code : int option
      Signal : string option
      Said : string }

/// Run the rehearsal `program` with `arguments`, and answer how it ended. `program` is the
/// name of its compiled file, which is beside the suite's own.
///
/// The rehearsal leads a process group of its own (`Detached`). A signal that goes to the
/// process group of its sender thus ends the rehearsal and nothing of the suite. Its error
/// stream is the suite's, so what a rehearsal says about a refusal shows beside the red of
/// its case.
///
/// A rehearsal that does not end in 30 seconds fails the case, and it must not stay when the
/// suite has gone. Its process group is one that this function made, so the function ends
/// that group.
let run (program: string) (arguments: string list) : Async<Ending> =
    async {
        let rehearsal =
            ChildProcesses.spawn
                Node.Api.``process``.execPath
                (Node.Api.path.join (StandIn.directory (), program) :: arguments)
                { Cwd = None
                  Env = ChildEnv.Replacing Map.empty
                  Streams = { Stdin = Stdio.Ignore; Stdout = Stdio.Pipe; Stderr = Stdio.Inherit }
                  Detached = true }
        let said = System.Text.StringBuilder ()
        Readables.text (ChildProcessStreams.stdout rehearsal) (fun chunk -> said.Append chunk |> ignore)
        let mutable ended = false
        let mutable refusal : string option = None
        // `close` and not `exit`: the output of the rehearsal is whole only then.
        ChildProcessStreams.onClose rehearsal (fun _ -> ended <- true)
        ChildProcessStreams.onError rehearsal (fun error -> refusal <- Some (StreamError.describe error))
        let! inTime = Support.settledWithin 30000 (fun () -> ended || refusal.IsSome)
        match refusal with
        | Some why -> return failwithf "the rehearsal %s did not run: %s" program why
        | None when not inTime ->
            ChildProcesses.signalGroup rehearsal "SIGKILL" |> ignore
            return failwithf "the rehearsal %s did not end in 30 seconds, and the case ended its process group" program
        | None ->
            return
                { Code = ChildProcesses.exitCode rehearsal
                  Signal = ChildProcesses.signalCode rehearsal
                  Said = said.ToString () }
    }
