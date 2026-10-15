module Yession.Analyzers.DynamicAccess

open FSharp.Analyzers.SDK
open Yession.Analyzers.Expressions

/// A member of a JavaScript object is reached through a declared type, and not by name.
///
/// `fs?readdirSync path` — Fable.Core.JsInterop's `?` — compiles to `fs.readdirSync(path)` and
/// asks nothing of `fs` on the way: the member is a string, its arguments are whatever was handed
/// to it, and its answer is whatever type the caller asks for. A misspelt member is not a compile
/// error but a call to `undefined`, which throws somewhere else, or answers `undefined` to a
/// reader that carries it on as a value. `?<-` is the same promise made from the other side: a
/// property set by a name nothing checks, on an object nothing says has it.
///
/// Each is what you write when the shape you are reaching into is declared nowhere, and the
/// repository's answer is to declare it, in one of three places. An API this repository does not
/// own is bound once in a binding project under `src/Fable.*`, behind an F# signature (see
/// `EmitHome`, which says the same of `[<Emit>]`). JSON that crossed a wire is read with a codec.
/// A bag of options is an interface whose members are its keys, filled through `jsOptions`. The
/// binding project is also where `?` may still be written: it is how a binding reaches the thing
/// it binds.
///
/// Nothing kept it there but a paragraph asking authors not to: `tools/Yession.Frames` reached
/// `node:fs`, `node:path` and `node:crypto` through `?` on an `obj` long after `Fable.Node` typed
/// every call it made, and read its own instrument's log as `e?n` and `r?after`.
///
/// Only the operators, matched as Fable.Core's functions and not by their spelling: `?` in a
/// signature (`?name: string`) is an optional parameter, and a `?` inside a string is text.
///
/// The population is `EmitHome`'s, and for its reason: a binding project is one with nothing
/// Yession in its reach (`Population.reach`), which exempts the `src/Fable.*` projects, the serial
/// example and the fixtures without a list, and judges the Domain. Reported from the project's
/// last source file, as every rule that reads the project's expressions whole is
/// (`Population.reportsHere`).

[<Literal>]
let Code = "YES015"

let private isGet = isFunction "Fable.Core.JsInterop" "op_Dynamic"

let private isSet = isFunction "Fable.Core.JsInterop" "op_DynamicAssignment"

let private message (project: string) (reach: string list) =
    let named = String.concat ", " reach

    "this reaches a member by a name nothing checks (`?` or `?<-`): a misspelt one is `undefined` "
    + "rather than a compile error, and what it answers is whatever the caller says it is. "
    + $"`%s{project}` is product code rather than a binding project (%s{named} in its reach). "
    + "Reach it through a declared type instead. An API this repository does not own is bound "
    + "once in a binding project (src/Fable.*, which references nothing Yession) behind an F# "
    + "signature, or is already typed by the package that binds it; JSON that crossed a wire is "
    + "read with a codec (Thoth `Decode`); a bag of options is an interface filled with `jsOptions`."

[<CliAnalyzer("DynamicAccess", "A JavaScript member is reached through a declared type, not by name", "")>]
let dynamicAccess: Analyzer<CliContext> =
    fun ctx ->
        async {
            match Population.reach ctx with
            | [] -> return []
            | reach ->
                if not (Population.reportsHere ctx) then
                    return []
                else
                    let project = System.IO.Path.GetFileNameWithoutExtension ctx.ProjectOptions.ProjectFileName

                    let offenders =
                        [ for binding in Expressions.of' ctx do
                              for call in binding.Calls do
                                  if isGet call.Callee || isSet call.Callee then
                                      yield call.Where ]
                        // A call reaches the walk more than once when it is a piped function's body.
                        |> List.distinct

                    return
                        [ for range in offenders ->
                              { Type = "DynamicAccess"
                                Message = message project reach
                                Code = Code
                                Severity = Severity.Error
                                Range = range
                                Fixes = [] } ]
        }
