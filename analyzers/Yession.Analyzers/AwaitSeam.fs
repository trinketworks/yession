module Yession.Analyzers.AwaitSeam

open FSharp.Compiler.Symbols
open FSharp.Analyzers.SDK
open Yession.Analyzers.Expressions

/// Where a project has an answer to the trampoline hazard, every await goes through it.
///
/// Fable's async trampoline hijacks a workflow onto a `setTimeout` every 2000 steps, and
/// `Async.AwaitPromise` attaches its rejection handler only once the workflow REACHES the
/// await. A promise that rejects inside that window has no handler when Node checks at the end
/// of the turn, so Node kills the process — and a `try/with` or an `Async.Catch` around the
/// await cannot help, because the handler is on the side of the gap that has not run yet. That
/// is how a routine "no such container" 404, caught and ignored on every other run, killed a
/// whole suite.
///
/// `Interop.awaitPromise` closes it by settling the promise in the tick that created it, so
/// both outcomes are a value the workflow may take as long as it likes to read. Every await in
/// the Node host and the suite now goes through it — 176 of them, converted across two sweeps.
///
/// This is what stops the 177th being written raw. Nothing else could: the seam is not a type
/// anything is forced through, it is a function somebody has to remember, and which call the
/// trampoline happens to expose is not a property of the call. The counter carries ACROSS test
/// cases, so the same await is fine for a year and fatal the day an unrelated case lands ahead
/// of it. An invariant that holds only because every caller remembered is a convention with a
/// good reputation, and the next caller has not read it.
///
/// The population is derived rather than named, and the derivation is the rule's own statement:
/// a project is judged when it can NAME a seam. `Yession.Host` declares one and the suite
/// references it; the serial example owns its own copy, as the examples rule requires, and is
/// judged against that. The browser client references none of them and is not judged — it is
/// not Node, where an unhandled rejection is a console warning rather than a dead process, and
/// `Yession.Host.Interop` is Node-only, so the exemption is the same fact as the absence.
/// Give the browser a seam and this rule starts asking it to use one, which is the correct
/// order: the remedy has to exist before the rule can demand it.

[<Literal>]
let Code = "YES010"

/// Fable's `Async.AwaitPromise`. Matched by name alone: it is the only function so called, the
/// extension lives in an assembly whose entity names FCS spells differently depending on how it
/// was opened, and a rule that missed it would report a clean product — which is the failure
/// this exists to prevent. The fixture is what keeps the match honest.
let private isAwaitPromise (mfv: FSharpMemberOrFunctionOrValue) =
    try mfv.LogicalName = "AwaitPromise" with _ -> false

/// The seam's own name. A project that can name one of these has an answer to the hazard.
let private isSeam (mfv: FSharpMemberOrFunctionOrValue) =
    try mfv.LogicalName = "awaitPromise" with _ -> false

/// Every awaiting seam this project could reach — its own and its references', bounded to the
/// repository by `Population`, so `Fable.Promise`'s own combinators are not mistaken for one.
let private seams (ctx: CliContext) =
    [ for declaration in Population.of' ctx do
        match (try Some (List.ofSeq declaration.Entity.MembersFunctionsAndValues) with _ -> None) with
        | Some members -> yield! members |> List.filter isSeam
        | None -> () ]

let private message (seam: FSharpMemberOrFunctionOrValue) =
    let name = try seam.FullName with _ -> "Interop.awaitPromise"

    $"this awaits a promise raw. Fable's trampoline hijacks a workflow onto a `setTimeout` "
    + "every 2000 steps, and `Async.AwaitPromise` attaches its rejection handler only once the "
    + "workflow reaches the await — so a promise that rejects inside that window has no handler "
    + "when Node checks, and Node kills the process. A `try/with` or an `Async.Catch` here "
    + "cannot help: the handler is on the side of the gap that has not run yet. Which call the "
    + "trampoline exposes is not a property of the call, either — the step counter carries "
    + $"across test cases. Await through `%s{name}` instead, which settles the promise in the "
    + "tick that created it."

[<CliAnalyzer("AwaitSeam", "A promise is awaited through the seam that settles it", "")>]
let awaitSeam: Analyzer<CliContext> =
    fun ctx ->
        async {
            if not (Population.reportsHere ctx) then
                return []
            else
                match seams ctx with
                // No seam in reach, no remedy to point at, nothing to say. The browser.
                | [] -> return []
                | seam :: _ ->
                    let offenders =
                        [ for binding in Expressions.of' ctx do
                              // The seam's own body holds the one legal await: it is what the
                              // settling is FOR, and it awaits a promise that cannot reject.
                              if not (binding.Owner |> Option.exists isSeam) then
                                  for call in binding.Calls do
                                      if isAwaitPromise call.Callee then
                                          yield call.Where ]
                        |> List.distinct

                    return
                        [ for where in offenders ->
                            { Type = "AwaitSeam"
                              Message = message seam
                              Code = Code
                              Severity = Severity.Error
                              Range = where
                              Fixes = [] } ]
        }
