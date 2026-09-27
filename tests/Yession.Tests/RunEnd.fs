module Yession.Tests.RunEnd

// How a run ENDS, which is a different question from what it found.
//
// A Node process exits when it runs out of work. An async case that stops being scheduled —
// one waiting on a latch nothing will fire, a promise nothing will settle — is not work: the
// runner is parked on it, the event loop empties, and Node leaves with code 0 having printed
// no verdict at all. The run reads as a pass. It cost a real diagnosis to notice: a terminal
// fix's regression test was rewritten to poll rather than wait on a latch, and only then did
// reverting the fix turn the case red instead of making the whole run disappear.
//
// Pyxpecto never ends that way on its own: every verdict it reaches — success, failure,
// error — exits the process explicitly (`CommandLine.exitWith`). So the loop emptying is
// already proof that no verdict was reached, and a case still in flight when the process
// leaves is proof of which one. Both are recorded here and said at the ending.
//
// This does NOT bound how long a case may take: a run that overruns is the other end of the
// same worry, and `runNodeSuite` in tasks.fsx kills it and fails. What has no other reader is
// a run that stops EARLY, quietly, and answers 0.

open Fable.Core
open Fable.Pyxpecto.Model
open Fable.NodeExtras

/// The async cases that have started and not finished, by full name. A `HashSet` because a
/// name is either in flight or it is not, and Pyxpecto runs one case at a time — the set is
/// the honest shape either way.
let private running = System.Collections.Generic.HashSet<string> ()

/// What is in flight right now, in a stable order so a report reads the same twice.
let stillRunning () : string list = running |> List.ofSeq |> List.sort

/// Why this run must be called failed, or `None` when its ending is ordinary.
///
/// Pure, and apart from the listeners that call it, because it is the whole rule: the
/// listeners are two lines of platform each and can only be exercised by ending a process,
/// while this is the sentence a person reads at three in the morning.
///
/// `drained` says the event loop ran out of work. On its own that is already a verdict
/// nobody reached, because a run that finished would have exited on its own verdict. A case
/// still in flight is the better answer where there is one — it names what to go and look
/// at — so it wins, and the drain is what is left to say when nothing was running at all.
let ending (drained: bool) (stuck: string list) : string option =
    match stuck, drained with
    | [], false -> None
    | [], true ->
        Some
            "tests: the run ended without reporting a verdict, and no case was in flight — the \
             event loop emptied while the runner was waiting on something that is not work. \
             Nothing here passed; the run did not happen."
    | names, _ ->
        Some (
            sprintf
                "tests: the run ended with %d case(s) still running, so they were never judged:\n%s\n\
                 A case that never settles is not a case that passed. If it waits on a latch or a \
                 promise, wait on the CONDITION instead (`Support.waitUntilWithin`), which keeps a \
                 timer on the loop and fails by name."
                (List.length names)
                (names |> List.map (sprintf "  - %s") |> String.concat "\n"))

/// Said once, however many of the two endings arrive — `beforeExit` is followed by `exit`,
/// and one fault deserves one sentence.
let mutable private said = false

let private report (drained: bool) =
    if not said then
        match ending drained (stillRunning ()) with
        | None -> ()
        | Some complaint ->
            said <- true
            eprintfn "%s" complaint
            // Raised, never lowered: a run that already failed for its own reasons keeps the
            // code it earned, and `2` (errored) outranks this.
            if Processes.exitCode () = 0 then Processes.setExitCode 1

/// One async case, bracketed so the set knows it started and — however it ends — that it
/// finished. A case that never finishes never reaches the `finally`, which is the whole
/// signal: it is still named when the process leaves.
let private bracketed (name: string) (body: Async<unit>) : Async<unit> =
    async {
        running.Add name |> ignore
        try
            do! body
        finally
            running.Remove name |> ignore
    }

/// Only ASYNC cases are bracketed. A synchronous one cannot end a run early and quietly: it
/// holds the thread, so the process does not leave — it overruns instead, which is the
/// budget's fault to report and already is.
let rec private bracketing (path: string) (test: TestCase) : TestCase =
    let full (name: string) = if path = "" then name else path + " - " + name
    match test with
    | SyncTest _ -> test
    | AsyncTest (name, body, focus) -> AsyncTest (name, bracketed (full name) body, focus)
    | TestList (name, cases, focus) -> TestList (name, cases |> List.map (bracketing (full name)), focus)
    | TestListSequential (name, cases, focus) ->
        TestListSequential (name, cases |> List.map (bracketing (full name)), focus)

/// The suite to run, watched: every async case bracketed, and both endings listened for.
///
/// ONE verb, because the two halves are useless apart — listeners over a tree nothing
/// bracketed can only ever say "no case was in flight", and bracketing nobody listens to
/// records the answer where nothing asks for it.
///
/// Node only. On the .NET CLR a case that never settles blocks the runner's thread, so the
/// process does not leave quietly — there is nothing here for it to catch, and the platform
/// bindings are not its platform's.
let mutable private listening = false

let guarded (suite: TestCase) : TestCase =
    if Compiler.isDotnet then suite
    else
        // Armed once, however many trees are wrapped. A second tree is not a second process,
        // and Node counts listeners.
        if not listening then
            listening <- true
            Processes.onEnding "beforeExit" (fun () -> report true)
            Processes.onEnding "exit" (fun () -> report false)
        bracketing "" suite
