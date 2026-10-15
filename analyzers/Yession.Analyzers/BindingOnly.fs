module Yession.Analyzers.BindingOnly

open FSharp.Analyzers.SDK
open FSharp.Compiler.Symbols
open FSharp.Compiler.Text
open Yession.Analyzers.Expressions

/// A JavaScript object is built by hand in a binding project, and nowhere else.
///
/// Two constructs, and one reason for both. `createObj` and `==>` (Fable.Core.JsInterop) build
/// an object whose keys are strings: `createObj [ "recusive" ==> true ]` compiles, runs, and
/// hands the other side an option it never reads. `[<AllowNullLiteral>]` is how F# says that a
/// type is a JavaScript interface whose value may be `null`. Each is what you write when you
/// are binding an API this repository does not own — a bag of options Node reads by name, an
/// object the platform may or may not hand back — and each, written in product code, is a
/// binding that never moved into a binding project.
///
/// The repository's answer to a foreign API is to HOUSE it (see `EmitHome`, which says the same
/// of `[<Emit>]`): a binding project under `src/Fable.*` declares the shape once, behind a type,
/// and the product calls that type like any other F#. For an options bag that is an interface
/// whose members are the keys, filled through `jsOptions`, so a key is spelled by the compiler
/// and a wrong one fails to build. For a nullable interface it is the binding's own declaration,
/// answered to the product as an `option`, so no `null` crosses into code that has no business
/// holding one. A bag that crosses a wire as JSON is a codec's (`Encode.object`), which is the
/// same answer in different clothes: a declared shape, not a string in a list.
///
/// Nothing kept it there. `Type safety` in CLAUDE.md says not to introduce either, and its
/// author is the only thing that stops the next one: `tools/Yession.Frames` made twenty-eight
/// `createObj` calls — the commands it sent a Chromium, the options of the process it spawned,
/// the log it wrote — each a key list nothing read back, each written beside its one caller.
///
/// The population is `EmitHome`'s, and for its reason: a binding project is one with nothing
/// Yession in its reach (`Population.reach`), which exempts the `src/Fable.*` projects, the
/// serial example and the fixtures without a list, and judges the Domain.
///
/// Reported from the project's last source file, as every rule that judges a project as a
/// whole is (`Population.reportsHere`): both halves are read off the whole project, so each
/// file saying so would say it once per file.

[<Literal>]
let Code = "YES013"

let private isCreateObj = isFunction "Fable.Core.JsInterop" "createObj"

/// `==>`, whose compiled name is the spelling of its characters.
let private isArrow = isFunction "Fable.Core.JsInterop" "op_EqualsEqualsGreater"

/// `[<AllowNullLiteral>]`, F#'s own.
let private isAllowNull (attribute: FSharpAttribute) =
    let name = try attribute.AttributeType.TryFullName with _ -> None

    name = Some "Microsoft.FSharp.Core.AllowNullLiteralAttribute"

/// Every object built by hand. A `==>` written inside a `createObj` is the same object, and it
/// is said once, at the `createObj`; one written alone (a pair handed on to something else) is
/// its own site.
let private byHand (ctx: CliContext) =
    let calls =
        [ for binding in Expressions.of' ctx do
              for call in binding.Calls do
                  yield call ]

    let whole = [ for call in calls do if isCreateObj call.Callee then yield call.Where ]

    let alone =
        [ for call in calls do
              if isArrow call.Callee && not (whole |> List.exists (fun outer -> Range.rangeContainsRange outer call.Where)) then
                  yield call.Where ]

    // A call reaches a walk more than once when it is a piped function's body.
    whole @ alone |> List.distinct

/// Every type in this project's own files that may be `null`, at the attribute that says so.
let private nullable (ctx: CliContext) =
    [ for declaration in Population.of' ctx do
          if declaration.Own then
              match (try Some (List.ofSeq declaration.Entity.Attributes) with _ -> None) with
              | Some attributes ->
                  for attribute in attributes do
                      if isAllowNull attribute then
                          yield attribute.Range
              | None -> () ]
    |> List.distinct

let private placed (project: string) (reach: string list) =
    let named = String.concat ", " reach

    $"`%s{project}` is product code rather than a binding project (%s{named} in its reach)"

let private byHandMessage (project: string) (reach: string list) =
    "this builds a JavaScript object by hand, with `createObj`/`==>`: every key is a string "
    + "nothing checks, and a misspelt one is an option the other side never reads. "
    + placed project reach
    + ". A shape Node, the browser or a package reads by name is bound once, in a binding project "
    + "(src/Fable.*, which references nothing Yession): declare an interface there whose members are "
    + "the keys, and fill it with `jsOptions`, so the compiler spells them. If the object is JSON "
    + "that crosses a wire, build it with a codec (`Encode.object`) instead."

let private nullableMessage (project: string) (reach: string list) =
    "this `[<AllowNullLiteral>]` makes a type that may be `null`, which is how a JavaScript "
    + "interface is declared: a binding that never moved into a binding project. "
    + placed project reach
    + ". Move the type into the binding project that owns the API (src/Fable.*, which "
    + "references nothing Yession), and have it answer the product an `option`, so no `null` "
    + "reaches code that has no business holding one."

[<CliAnalyzer("BindingOnly", "A hand-built JavaScript object is declared in a binding project", "")>]
let bindingOnly: Analyzer<CliContext> =
    fun ctx ->
        async {
            match Population.reach ctx with
            | [] -> return []
            | reach ->
                if not (Population.reportsHere ctx) then
                    return []
                else
                    let project = System.IO.Path.GetFileNameWithoutExtension ctx.ProjectOptions.ProjectFileName

                    return
                        [ for range in byHand ctx ->
                              { Type = "BindingOnly"
                                Message = byHandMessage project reach
                                Code = Code
                                Severity = Severity.Error
                                Range = range
                                Fixes = [] }
                          for range in nullable ctx ->
                              { Type = "BindingOnly"
                                Message = nullableMessage project reach
                                Code = Code
                                Severity = Severity.Error
                                Range = range
                                Fixes = [] } ]
        }
