module Yession.Tests.BrowserRunner

// How the .NET CLR run executes the browser suites: several cases at once, where Pyxpecto's own
// runner takes one at a time.
//
// Pyxpecto runs its flat tests in a `for` loop and never reads the `sequenced` field its own
// model carries, so on Node and on the CLR alike a case starts only when the one before it has
// ended. A browser case spends most of its time waiting — on a Manager to boot, a session to
// connect, a page to draw — and one at a time, the 270 of them took seven and a half minutes of
// a pull request's `test` job on a 4-core runner less than half busy. The cases were made
// independent for this (`Browser.fs`, "every case below arranges what it asserts on and takes
// it away again"); what was missing was a runner.
//
// This one is Pyxpecto's model run by a pool. It reads the same tree, honours focus and pending
// the way Pyxpecto does, and prints Pyxpecto's verdict lines, tally and exit codes, so whatever
// reads a run's verdicts reads this one's the same. It adds one line per case, `▶ <name>` as the
// case starts (`runCase` says why). And it reads the half of the model Pyxpecto left unread:
// `testSequenced` means "these cases, one at a time and in order", and a suite whose cases share
// something says so with it. A sequenced list is one unit of work; every other case is a unit of
// its own.
//
// Node keeps Pyxpecto's runner. Its suites share one process, one event loop and a great deal
// of module state, and nobody has made them independent.

#if !FABLE_COMPILER

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.Threading
open Fable.Pyxpecto
open Fable.Pyxpecto.Model

/// How many cases run at once when the run does not say.
///
/// Measured on a 4-core box, alone under a build lock, against the whole browser tier (`check
/// Browser Native Srt Caddy --runtime clr`, 270 cases): one at a time took 469-500s with the
/// CPU 44% busy, two 282s, three 216-233s at 86%, four 204-205s at 93%. Chromium draws and the
/// hosts boot on the same four cores, so a case slows as more run beside it — their own times
/// summed to 483s one at a time, about 660s three at a time and 810s four — and a case that is
/// slower is one whose waits are nearer their deadlines. The fourth worker buys 7% for a box
/// with nothing left over; three is most of the gain with some room to spare.
let private defaultWorkers = 3

/// How many cases this run executes at once: `YESSION_TEST_WORKERS`, or `defaultWorkers`. One
/// starts a case only when the one before it has ended, which is what to ask for when a run must
/// be read top to bottom, or a flake told apart from load.
let private workers : int =
    match Environment.GetEnvironmentVariable "YESSION_TEST_WORKERS" with
    | null | "" -> defaultWorkers
    | text ->
        match Int32.TryParse text with
        | true, n when n >= 1 -> n
        | _ -> failwithf "YESSION_TEST_WORKERS is '%s'; it takes a whole number of cases to run at once, 1 or more" text

/// One case as the pool sees it: its full name (`Yession - <suite> - <case>`, as Pyxpecto
/// joins it), its body, and its focus after its lists have had their say.
type private Case =
    { Name: string
      Code: TestCode
      Focus: FocusState }

/// A list that is focused or pending makes everything under it so; a normal one leaves its
/// cases their own.
let private prioritised (own: FocusState) (list: FocusState) : FocusState =
    match list with
    | Focused | Pending -> list
    | Normal -> own

/// Every case under `test`, in declaration order.
let rec private casesOf (path: string list) (listFocus: FocusState) (test: TestCase) : Case list =
    let named (name: string) = String.concat " - " (path @ [ name ])
    match test with
    | SyncTest (name, body, focus) -> [ { Name = named name; Code = Sync body; Focus = prioritised focus listFocus } ]
    | AsyncTest (name, body, focus) -> [ { Name = named name; Code = Async body; Focus = prioritised focus listFocus } ]
    | TestList (name, tests, focus)
    | TestListSequential (name, tests, focus) ->
        tests |> List.collect (casesOf (path @ [ name ]) (prioritised focus listFocus))

/// The units of work under `test`: a sequenced list is ONE, its cases in order, and every
/// other case is one alone. A sequenced list inside a sequenced list is part of the outer one.
let rec private unitsOf (path: string list) (listFocus: FocusState) (test: TestCase) : Case list list =
    match test with
    | SyncTest _
    | AsyncTest _ -> [ casesOf path listFocus test ]
    | TestList (name, tests, focus) -> tests |> List.collect (unitsOf (path @ [ name ]) (prioritised focus listFocus))
    | TestListSequential (name, tests, focus) ->
        [ tests |> List.collect (casesOf (path @ [ name ]) (prioritised focus listFocus)) ]

// --- What a case prints ---------------------------------------------------------------------
//
// A case's log is read by position: the lines above its verdict are what it said — the waits it
// spent its time on (`Wait.until`), what its pages saw when it failed (`reporting`). With several
// cases in flight, their lines would arrive shuffled together and each case's report would be
// split by its neighbours'. So what a case prints is held until it ends and goes out with its
// verdict, in one piece: the log reads, case by case, the way a run of one at a time read.

/// What one case printed, until its verdict takes it. After that a straggler (a task the case
/// started and did not wait for) is printed as it comes, rather than kept where nothing reads it.
type private Transcript (out: TextWriter) =
    let text = Text.StringBuilder ()
    let mutable taken = false
    member _.Write (s: string) =
        lock text (fun () -> if taken then out.Write s else text.Append s |> ignore)
    /// Everything said so far; the case is over.
    member _.Take () : string =
        lock text (fun () ->
            taken <- true
            string text)

/// The transcript of the case whose flow of control this is, or `None` outside every case.
let private speaking = AsyncLocal<Transcript option> ()

/// The console while the pool runs: what a case writes goes to its transcript, anything else
/// straight out. `Console.SetOut` serialises every call into it.
type private Routed (out: TextWriter) =
    inherit TextWriter ()
    override _.Encoding = out.Encoding
    override _.Write (value: char) =
        match speaking.Value with
        | Some transcript -> transcript.Write (string value)
        | None -> out.Write value
    override _.Write (value: string) =
        match speaking.Value with
        | Some transcript -> transcript.Write value
        | None -> out.Write value
    override this.Write (buffer: char[], index: int, count: int) = this.Write (String (buffer, index, count))
    override _.Flush () = out.Flush ()

/// What the run has found so far, and the one door to the console for a verdict: a case's
/// verdict and what it printed go out together, and two verdicts must not share a line.
type private Tally () =
    let gate = obj ()
    let mutable passed = 0
    let mutable failed = 0
    let mutable errored = 0
    let mutable ignored = 0
    let failures = ResizeArray<string * string> ()

    member _.Say (line: string) = lock gate (fun () -> Console.WriteLine line)

    member _.Passed (name: string) (printed: string) (took: TimeSpan option) =
        lock gate (fun () ->
            passed <- passed + 1
            let timespan = match took with Some t -> sprintf " (%O)" t | None -> ""
            Console.Write (sprintf "%s✔️ %s%s\n" printed name timespan))

    /// `asserted` is Pyxpecto's line between a failure and an error: an `AssertException` is
    /// the case saying no, anything else is the case not managing to say.
    member _.Failed (name: string) (printed: string) (message: string) (asserted: bool) =
        lock gate (fun () ->
            failures.Add (name, message)
            if asserted then failed <- failed + 1 else errored <- errored + 1
            Console.Write (sprintf "%s%s %s\n\b%s\n" printed (if asserted then "🚫" else "❌") name message))

    member _.Skipped (name: string) =
        lock gate (fun () ->
            ignored <- ignored + 1
            Console.WriteLine (sprintf "🚧 skipping '%s' ... pending" name))

    member _.Unfocused () = lock gate (fun () -> ignored <- ignored + 1)

    /// Pyxpecto's closing block, word for word, and the exit code it would have given.
    member _.Close () : int =
        lock gate (fun () ->
            let sb = Text.StringBuilder ()
            sb.AppendLine () |> ignore
            failures |> Seq.iteri (fun i (name, message) -> sb.AppendLine (sprintf "%s%d) %s%s\n\b%s" BColors.FAIL (i + 1) name BColors.ENDC message) |> ignore)
            sb.AppendLine () |> ignore
            let sep = "-------------------------------------------------------------------------------"
            let n (value: int) = sprintf "%s%d%s" BColors.INFOBLUE value BColors.ENDC
            let summary =
                sprintf "%s tests run - %s passed, %s ignored, %s failed, %s errored"
                    (n (passed + failed + errored)) (n passed) (n ignored) (n failed) (n errored)
            Console.WriteLine (sb.AppendLine(sep).AppendLine(summary).AppendLine(sep).ToString ())
            if errored > 0 then
                Console.WriteLine (sprintf "%s❌ Exited with error code 2%s" BColors.FAIL BColors.ENDC)
                2
            elif failed > 0 then
                Console.WriteLine (sprintf "%s❌ Exited with error code 1%s" BColors.FAIL BColors.ENDC)
                1
            else
                Console.WriteLine (sprintf "%sSuccess!%s" BColors.OKGREEN BColors.ENDC)
                0)

/// One case, start to verdict. It says when it STARTS, as the one line not held for its verdict,
/// because with several in flight the line after the last verdict no longer names what is
/// running: a case that never ends is one with a `▶` line and no verdict.
///
/// The body runs as a task of its own, started with its transcript in place and the transcript
/// taken away again at once on this side — the shape `Wait.refusing` has, for the same reason:
/// an F# async does not put back what it set in an `AsyncLocal`, and the next case on this
/// worker would otherwise speak into this one's transcript.
let private runCase (tally: Tally) (out: TextWriter) (case: Case) : Async<unit> =
    async {
        tally.Say (sprintf "▶ %s" case.Name)
        let transcript = Transcript out
        let clock = Stopwatch.StartNew ()
        let body =
            match case.Code with
            | Sync body -> async { body () }
            | Async body -> body
        let started =
            speaking.Value <- Some transcript
            try Async.StartAsTask (Async.Catch body)
            finally speaking.Value <- None
        let! outcome = Async.AwaitTask started
        clock.Stop ()
        let printed = transcript.Take ()
        match outcome with
        | Choice1Of2 () ->
            tally.Passed case.Name printed (match case.Code with Async _ -> Some clock.Elapsed | Sync _ -> None)
        | Choice2Of2 (:? AssertException as e) -> tally.Failed case.Name printed e.Message true
        | Choice2Of2 e -> tally.Failed case.Name printed e.Message false
    }

/// Run `suite` with `workers` cases at once, print what Pyxpecto prints, and exit as it exits.
let run (suite: TestCase) : int =
    Console.OutputEncoding <- Text.Encoding.UTF8
    let out = Console.Out
    Console.SetOut (new Routed (out))
    let units = unitsOf [] Normal suite
    let all = List.concat units
    let focused = all |> List.exists (fun case -> case.Focus = Focused)
    let runs (case: Case) = if focused then case.Focus = Focused else case.Focus = Normal
    let tally = Tally ()
    // A case that boots a host waits for it on the thread it was given (`startHost`), and the
    // readiness line it waits for is delivered on another pool thread. With every worker parked
    // that way at once, the pool has to grow before anything moves, and it grows slowly.
    let minimum, ports = ThreadPool.GetMinThreads ()
    ThreadPool.SetMinThreads (max minimum (workers * 4), ports) |> ignore
    tally.Say (
        sprintf "%s🚀 start running net tests ... [%s] (%d at a time)"
            BColors.ENDC (DateTime.Now.ToString "HH:mm:ss yyyy.MM.dd") workers)
    // A sequenced list is the longest unit there is — several cases end to end, each booting a
    // deployment of its own — and one taken last is the run's tail, run by one worker while the
    // others stand idle beside it (measured: the path-mounted list alone for the last 14s of a
    // run four at a time). So the lists are taken first; everything else keeps its order. Taken
    // first, they took a run three at a time from 241s to 216-233s, and four at a time from 225s
    // to 204s.
    let queue =
        units
        |> List.map (List.filter runs)
        |> List.filter (not << List.isEmpty)
        |> List.sortBy (fun cases -> if List.length cases > 1 then 0 else 1)
        |> ConcurrentQueue<Case list>
    let rec work () =
        async {
            match queue.TryDequeue () with
            | true, cases ->
                for case in cases do
                    do! runCase tally out case
                return! work ()
            | false, _ -> ()
        }
    Array.init workers (fun _ -> work ()) |> Async.Parallel |> Async.RunSynchronously |> ignore
    for case in all do
        if not (runs case) then
            if focused then tally.Unfocused () else tally.Skipped case.Name
    let code = tally.Close ()
    exit code

#endif
