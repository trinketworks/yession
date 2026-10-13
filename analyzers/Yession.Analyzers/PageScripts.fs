module Yession.Analyzers.PageScripts

open FSharp.Analyzers.SDK
open FSharp.Compiler.Symbols
open Yession.Analyzers.Expressions

/// A page is handed JavaScript from one place.
///
/// Playwright takes JavaScript as a string in several places — `EvaluateAsync` and its
/// relatives, `WaitForFunctionAsync`, `AddInitScriptAsync` — and nothing checks that string:
/// not the compiler, not this analyzer's other rules, not even a parser until Chromium gets it.
/// The browser suite once held some five hundred of them, each a program somebody had to
/// reread to trust: a selector typo answered `null`, a misspelt global answered `undefined`,
/// and the wait that tested it timed out naming nothing. They went one case at a time, into
/// typed probes the page publishes (`Yession.App.Probes`), the harness's typed keys
/// (`Yession.App.HarnessPage`) and installable shims (`Yession.App.Shims`) — F# on both
/// sides, with a codec between.
///
/// What is left is the handful of fixed lines that CALL those: invoke what the page published
/// under a name, read a global, install a bundle. They take their names and arguments as
/// arguments and splice nothing, and they live in one file so there is one file to read. This
/// rule keeps it that way, the way `EnvWrites` keeps the environment's writes: by the file, per
/// assembly, rather than by a list of the files that may — a second file is reported with the
/// first, because which of them should hold the handing is a decision and both are where it
/// could be taken.
///
/// Every overload counts, `AddInitScriptAsync(scriptPath = …)` included: a file of JavaScript is
/// still JavaScript the page is handed.

[<Literal>]
let Code = "YES012"

/// The Playwright members whose argument is JavaScript source, on whichever of its types
/// declares them — a page, a frame, a locator, an element handle, a context.
let private handing =
    set
        [ "EvaluateAsync"
          "EvaluateHandleAsync"
          "EvaluateAllAsync"
          "EvalOnSelectorAsync"
          "EvalOnSelectorAllAsync"
          "WaitForFunctionAsync"
          "AddInitScriptAsync" ]

let private handsScript (callee: FSharpMemberOrFunctionOrValue) =
    let declaring =
        try callee.DeclaringEntity |> Option.bind (fun e -> e.TryFullName) with _ -> None

    match declaring with
    | Some owner when owner.StartsWith "Microsoft.Playwright." -> handing.Contains callee.LogicalName
    | _ -> false

let private describe (files: string list) =
    let named = files |> List.distinct |> List.sort |> String.concat " and "

    $"a page is handed JavaScript from %s{named}. A program in a string is checked by nothing — "
    + "not the compiler, not a parser — until Chromium runs it, and a mistake in it answers null "
    + "or undefined rather than failing. Ask the page through a typed probe (`Yession.App.Probes`), "
    + "the harness's keys (`Yession.App.HarnessPage`) or a shim (`Yession.App.Shims`), and keep the "
    + "fixed lines that call those in one place: in the test suite, `PageScript`."

let private offenders (ctx: CliContext) =
    let sites =
        [ for binding in Expressions.of' ctx do
              for call in binding.Calls do
                  if handsScript call.Callee then
                      yield fileOf ctx call.Where, call.Where ]

    match sites |> List.map fst |> List.distinct with
    | _ :: _ :: _ as files -> [ for (_, where) in sites -> where, describe files ]
    | _ -> []

[<CliAnalyzer("PageScripts", "A page is handed JavaScript from one place", "")>]
let pageScripts: Analyzer<CliContext> =
    fun ctx ->
        async {
            if not (Population.reportsHere ctx) then
                return []
            else
                return
                    [ for (where, message) in List.distinct (offenders ctx) ->
                        { Type = "PageScripts"
                          Message = message
                          Code = Code
                          Severity = Severity.Error
                          Range = where
                          Fixes = [] } ]
        }
