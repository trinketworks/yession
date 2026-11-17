module Yession.Analyzers.Signals

open FSharp.Analyzers.SDK
open FSharp.Compiler.Text
open Yession.Analyzers.Expressions

/// A signal goes to a process that is there.
///
/// A child that did not start has no pid. Node leaves `pid` undefined after a spawn that
/// failed, and `Fable.Node` types it as a number that is always there. So `int child.pid` is 0
/// for such a child, and `kill` reads pid 0 as the process group of the caller. Node's own
/// `child.kill` reaches that group too, from the failed spawn until Node reports it.
/// `process.kill` takes each number that it gets: 0 names that group, and -1 names each process
/// that the caller may signal.
///
/// A session does not lead a process group: its group is the group of its Manager. So a
/// session that killed a command that did not start sent SIGKILL to its Manager and to each
/// session of that Manager. The call did not throw, and a process that gets SIGKILL says
/// nothing.
///
/// `Fable.NodeExtras` has the verbs that ask first. `ChildProcesses.pid` answers `None` for a
/// child that did not start. `ChildProcesses.signal` and `ChildProcesses.signalGroup` send
/// nothing to such a child. `Processes.kill` and `Processes.trySignal` refuse 0, 1 and -1. The
/// fix moved each kill to those verbs, and nothing held the next one there. A new
/// `child.kill ()` compiles and passes each case of the suite. The fault needs a spawn that
/// fails, and no case makes one at a new call.
///
/// `Fable.Node` declares the members, so this repository cannot remove them from the type. A
/// type of its own for a child would hide them from a caller of `ChildProcesses.spawn`, and
/// Node's own `spawn` would still answer Node's type. So the guard is a rule on the members.
///
/// The rule reports a call of one of three members of `Fable.Node`. It matches each one as the
/// member of the library, and not by its spelling.
///
/// - the getter of `pid` on `Node.ChildProcess.ChildProcess`
/// - `kill` on `Node.ChildProcess.ChildProcess`
/// - `kill` on `Node.Process.Process`, which is `process.kill`
///
/// The rule does not report the `pid` of `Node.Process.Process`, because this process has a
/// pid. It does not report a `pid` or a `kill` that a different type declares: the terminal of
/// `Fable.NodePty`, a type of the product.
///
/// The rule judges each project, and a binding project too. A binding project needs an emit or
/// a cast to give an API a type, so the rules on those constructs leave it alone
/// (`Population.reach`). No project needs these three members, because the verbs of
/// `Fable.NodeExtras` are emits. The launcher of Chromium is in that binding project, and two
/// of the kills that the fix moved were there.
///
/// The rule does not see three things.
///
/// - A file that only Fable compiles (`#if FABLE_COMPILER`), because `lint` reads what .NET
///   compiles. The programs that the suite runs as processes of their own are such files.
/// - The `kill` of `SpawnedProcess` (`Fable.ClaudeAgentSdk`). For a child that `ofChild` made,
///   that member is Node's own `kill` under the type of the agent SDK. `ofChild` says what
///   holds there.
/// - A member that a project imports or emits for itself.
///
/// The rule reports per file. The CLI reads the marker of a justified exception
/// (`// fsharpanalyzer: ignore-line YES021`) from the file that it judges. It compares only the
/// number of the line. A rule that reports from a different file loses the marker.

[<Literal>]
let Code = "YES021"

/// Which of the three members a call is.
type private Member =
    | ChildPid
    | ChildKill
    | ProcessKill

/// The getter of a property has the name of the property behind `get_`.
let private isChildPid = isFunction "Node.ChildProcess.ChildProcess" "get_pid"

let private isChildKill = isFunction "Node.ChildProcess.ChildProcess" "kill"

let private isProcessKill = isFunction "Node.Process.Process" "kill"

let private memberOf (call: Call) : Member option =
    if isChildPid call.Callee then Some ChildPid
    elif isChildKill call.Callee then Some ChildKill
    elif isProcessKill call.Callee then Some ProcessKill
    else None

let private kept = Kept.Answer<(range * Member) list> ()

/// Each call of the three members in the project, and where it is. The walk is over each call
/// of the project, so one file makes it and each other file of the project reads its answer
/// (`Kept`). The answer holds no symbol.
let private calls (ctx: CliContext) : (range * Member) list =
    kept.For (
        ctx.CheckProjectResults,
        fun () ->
            [ for binding in Expressions.of' ctx do
                  for call in binding.Calls do
                      match memberOf call with
                      | Some which -> yield call.Where, which
                      | None -> () ]
            // A call reaches the walk more than once when it is a piped function's body.
            |> List.distinctBy fst
    )

let private message (which: Member) =
    match which with
    | ChildPid ->
        "this reads the pid of a child from Node's own `pid`. A child that did not start has no "
        + "pid, and `Fable.Node` types the member as a number that is always there. So "
        + "`int child.pid` is 0 for such a child, and `kill` reads pid 0 as the process group of "
        + "the caller. Read the pid with `ChildProcesses.pid` (Fable.NodeExtras). It answers "
        + "`None` for a child that did not start."
    | ChildKill ->
        "this sends a signal with Node's own `kill` of a child. For a child that did not start, "
        + "that signal goes to the process group of the caller, from the failed spawn until Node "
        + "reports it. Send the signal with `ChildProcesses.signal`, or with "
        + "`ChildProcesses.signalGroup` for the process group that a detached child leads "
        + "(Fable.NodeExtras). Each sends nothing to a child that did not start, and answers "
        + "whether the kernel took the signal."
    | ProcessKill ->
        "this sends a signal with Node's own `process.kill`, which takes each number that it "
        + "gets. The number 0 names the process group of the caller, and -1 names each process "
        + "that the caller may signal. Send the signal with `Processes.kill`, or with "
        + "`Processes.trySignal` to read the answer of the kernel (Fable.NodeExtras). Each "
        + "refuses 0, 1 and -1. `Processes.tryProbe` asks with signal 0, which the kernel sends "
        + "to nobody, and `Processes.signalSelf` sends a signal to this process."

[<CliAnalyzer("Signals", "A signal goes to a process that is there", "")>]
let signals: Analyzer<CliContext> =
    fun ctx ->
        async {
            return
                [ for where, which in calls ctx do
                      if sameFile where.FileName ctx.FileName then
                          { Type = "Signals"
                            Message = message which
                            Code = Code
                            Severity = Severity.Error
                            Range = where
                            Fixes = [] } ]
        }
