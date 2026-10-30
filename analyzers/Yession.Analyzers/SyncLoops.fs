module Yession.Analyzers.SyncLoops

open FSharp.Compiler.Symbols
open FSharp.Analyzers.SDK

/// A loop inside an `async` block waits on something, or it is not in the block.
///
/// A `for` or `while` written inside `async { }` is not a loop. The compiler hands it to the
/// builder's `For` and `While`, which run it as one `Bind` per element. A body that waits on
/// nothing completes each of those on the stack of the one before, so a loop over N elements is
/// N nested continuations — a plain loop's work, at the stack's cost. Fable's trampoline moves a
/// workflow onto a fresh stack only every 2000 binds, and iOS Safari's stack runs out first: a
/// session with a few thousand saved events stopped at "Connecting" on a phone, with
/// `RangeError: Maximum call stack size exceeded` thrown from inside `Async.Catch`, while every
/// desktop browser and the whole suite loaded it fine. Which loop crosses the line is a fact
/// about the data on one device, not the code, so no test finds the next one.
///
/// The remedy is the code it should have been: fold in a plain function, outside the block, and
/// bind its answer. A loop whose body waits (`let!`, `do!`, `match!`, `return!`, `use!`) is
/// left alone — each iteration hands the stack back to the thing it awaits.
///
/// The suite is not judged. Its loops run over what the case arranged, a handful of elements on
/// the CLR or on Node, never on a phone, and the hazard is data nobody bounded. Product code
/// runs on what a device kept or a provider answered, and is judged wherever it compiles, the
/// Node host included, whose stack is larger only by a constant.

[<Literal>]
let Code = "YES019"

/// A member of the `async` builder, by the entity FCS names it: `AsyncBuilder` compiles to
/// `FSharpAsyncBuilder`, which is what its members' declaring entity answers to.
let private ofAsyncBuilder (names: Set<string>) (mfv: FSharpMemberOrFunctionOrValue) =
    (try names.Contains mfv.LogicalName with _ -> false)
    && (try mfv.DeclaringEntity |> Option.bind (fun e -> e.TryFullName) with _ -> None) = Some "Microsoft.FSharp.Control.FSharpAsyncBuilder"

let private isLoop = ofAsyncBuilder (set [ "For"; "While" ])

/// What `let!`, `do!`, `match!`, `use!` and `return!` compile to.
let private isWait = ofAsyncBuilder (set [ "Bind"; "ReturnFrom"; "Using" ])

/// The suite, by its project's name: the one project whose loops are over data it wrote itself.
let private judged (ctx: CliContext) =
    System.IO.Path.GetFileNameWithoutExtension ctx.ProjectOptions.ProjectFileName <> "Yession.Tests"

let private message =
    "this loop is inside an `async` block and its body waits on nothing. The builder runs it as "
    + "one `Bind` per element, and a body that does not wait completes each on the stack of the "
    + "last: N elements are N nested continuations. Fable's trampoline resets the stack only "
    + "every 2000 binds, and iOS Safari's stack overflows first — a page with a long saved "
    + "history stopped at \"Connecting\" with `RangeError: Maximum call stack size exceeded`. "
    + "Fold in a plain function outside the `async` block and bind its answer, as "
    + "`EventFetch.contiguous` does."

[<CliAnalyzer("SyncLoops", "A loop inside an async block waits, or it is not there", "")>]
let syncLoops: Analyzer<CliContext> =
    fun ctx ->
        async {
            if not (Population.reportsHere ctx) || not (judged ctx) then
                return []
            else
                let offenders =
                    [ for binding in Expressions.of' ctx do
                          for call in binding.Calls do
                              if isLoop call.Callee then
                                  // The body is the last argument: `For (source, body)` and
                                  // `While (guard, body)`.
                                  match List.tryLast call.Args with
                                  | Some body when not (Expressions.callsIn body |> List.exists (fun c -> isWait c.Callee)) ->
                                      yield call.Where
                                  | _ -> () ]
                    |> List.distinct

                return
                    [ for where in offenders ->
                        { Type = "SyncLoops"
                          Message = message
                          Code = Code
                          Severity = Severity.Error
                          Range = where
                          Fixes = [] } ]
        }
