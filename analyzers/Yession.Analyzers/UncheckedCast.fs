module Yession.Analyzers.UncheckedCast

open System.IO
open FSharp.Analyzers.SDK
open FSharp.Compiler.Symbols
open FSharp.Compiler.Syntax
open Yession.Analyzers.Expressions

/// A value is read as the type it is, and not asserted into one.
///
/// `unbox`, Fable.Core.JsInterop's `!!`, and a downcast `:?>` from `obj` all say the same thing:
/// this value is a `T`, take my word for it. On .NET the runtime checks the word and throws at the
/// cast. Under Fable, which is what the product runs as, it checks nothing: the cast compiles to
/// the value itself, so a `Y.Text` unboxed as a `string` does not fail where it was unboxed. It is
/// carried on as a string, and fails later, somewhere that has no idea why.
///
/// The repository's answer is to read the value as the type it is, in one of three places. A
/// value that crossed a wire as JSON is decoded with a codec, which refuses a shape it was not
/// promised. A value an API this repository does not own hands over is typed by that API's
/// binding, in a binding project under `src/Fable.*` (see `EmitHome`). A bag of options is an
/// interface filled through `jsOptions`. Where none applies — the case of an erased union whose
/// type is an interface, which Fable cannot test for — the cast stays on the line that needs it,
/// with the type it asserts written there, and the line says it is deliberate:
/// `// fsharpanalyzer: ignore-line YES016` at its end, which the analyzer CLI honours, with the
/// reason in a comment above it.
///
/// What is reported, precisely:
///
/// - every `unbox` and every `!!`, whatever they are applied to, matched as the library's
///   functions and not by their spelling (a test's own `!!` is not Fable's);
/// - a `:?>` or `downcast` WRITTEN in the source whose operand is typed `obj`. That is `unbox`
///   spelt differently. A downcast from a declared type (an `Event` to the `KeyboardEvent` it is,
///   an `Element` to an `HTMLElement`) is the DOM's own shape, where a declared hierarchy is the
///   only one available, and it is left alone. A type test (`:? string as s`) checks, and the
///   compiler's own downcasts (structural equality, a `for` loop's enumerator) are nobody's to
///   fix: both reach the typed tree as the same downcast, which is why a downcast is reported
///   only where the syntax tree says one was written.
///
/// Except on a value built in the same expression — `unbox (ResponseDouble reply)`, a record, a
/// union case, a literal: there the author is holding the thing whose type is being asserted.
///
/// The population is `EmitHome`'s (`Population.reach`): a binding project is where a value of no
/// declared type is turned into one, so it may cast. Reported per file, because whether a
/// downcast was written is a question for that file's syntax tree.

[<Literal>]
let Code = "YES016"

let private isUnbox = isFunction "Microsoft.FSharp.Core.Operators" "unbox"

let private isBang = isFunction "Fable.Core.JsInterop" "op_BangBang"

/// What a `:?>` reaches the typed tree as, and what the compiler's own downcasts reach it as too.
let private isDowncast =
    isFunction "Microsoft.FSharp.Core.LanguagePrimitives.IntrinsicFunctions" "UnboxGeneric"

/// What a cast is applied to, seen through the upcast to `obj` F# inserts to hand it over.
let rec private operand (e: FSharpExpr) =
    match e with
    | FSharpExprPatterns.Coerce (_, inner) -> operand inner
    | _ -> e

/// A value built right there, in the expression being cast.
let private constructed (e: FSharpExpr) =
    match operand e with
    | FSharpExprPatterns.NewObject _
    | FSharpExprPatterns.NewRecord _
    | FSharpExprPatterns.NewAnonRecord _
    | FSharpExprPatterns.NewUnionCase _
    | FSharpExprPatterns.ObjectExpr _
    | FSharpExprPatterns.Const _ -> true
    | _ -> false

let private isObj (t: FSharpType) =
    try
        let t = t.StripAbbreviations ()
        t.HasTypeDefinition && t.TypeDefinition.TryFullName = Some "System.Object"
    with _ ->
        false

/// Where this file's source writes a downcast, by the line and column each starts at.
let private writtenDowncasts (input: ParsedInput) =
    let found = ResizeArray<int * int> ()

    let collector =
        { new ASTCollecting.SyntaxCollectorBase() with
            override _.WalkExpr(_, expr) =
                match expr with
                | SynExpr.Downcast (_, _, range)
                | SynExpr.InferredDowncast (_, range) -> found.Add (range.StartLine, range.StartColumn)
                | _ -> () }

    ASTCollecting.walkAst collector input
    Set.ofSeq found

let private sameFile (a: string) (b: string) =
    try Path.GetFullPath a = Path.GetFullPath b with _ -> false

let private message (project: string) (reach: string list) =
    let named = String.concat ", " reach

    "this cast asserts a type and checks nothing (`unbox`, `!!`, or `:?>` from `obj`): under Fable "
    + "it compiles to the value itself, so a value of another shape is not refused here but carried "
    + "on, to fail somewhere else. "
    + $"`%s{project}` is product code rather than a binding project (%s{named} in its reach). "
    + "Read the value as the type it is instead: JSON that crossed a wire is decoded with a codec "
    + "(Thoth `Decode`); what an API this repository does not own hands over is typed by its "
    + "binding (src/Fable.*, which references nothing Yession); a bag of options is an interface "
    + "filled with `jsOptions`. Where none applies, keep the cast on this line with its type "
    + "written (`unbox<T>`), end the line with `// fsharpanalyzer: ignore-line YES016`, and say "
    + "why in a comment above it."

[<CliAnalyzer("UncheckedCast", "A value is read as the type it is, not asserted into one", "")>]
let uncheckedCast: Analyzer<CliContext> =
    fun ctx ->
        async {
            match Population.reach ctx with
            | [] -> return []
            | reach ->
                let project = Path.GetFileNameWithoutExtension ctx.ProjectOptions.ProjectFileName
                let written = writtenDowncasts ctx.ParseFileResults.ParseTree

                let reported (call: Call) =
                    match call.Args with
                    | [ value ] when not (constructed value) ->
                        isUnbox call.Callee
                        || isBang call.Callee
                        || (isDowncast call.Callee
                            && written.Contains (call.Where.StartLine, call.Where.StartColumn)
                            && isObj (operand value).Type)
                    | _ -> false

                let offenders =
                    [ for binding in Expressions.of' ctx do
                          for call in binding.Calls do
                              if sameFile call.Where.FileName ctx.FileName && reported call then
                                  yield call.Where ]
                    // A call reaches the walk more than once when it is a piped function's body.
                    |> List.distinct

                return
                    [ for range in offenders ->
                          { Type = "UncheckedCast"
                            Message = message project reach
                            Code = Code
                            Severity = Severity.Error
                            Range = range
                            Fixes = [] } ]
        }
