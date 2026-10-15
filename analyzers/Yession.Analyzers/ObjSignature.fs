module Yession.Analyzers.ObjSignature

open System.IO
open FSharp.Analyzers.SDK
open FSharp.Compiler.Symbols

/// A signature says what crosses it, and `obj` says nothing.
///
/// A function that takes or answers `obj` is a contract with no terms: the caller hands over
/// whatever it has, the body reads it as whatever it needs, and nothing says when the two have
/// stopped agreeing. Under Fable, where a cast checks nothing (`UncheckedCast`), the disagreement
/// is not even an exception — it is a value of one shape read as another, somewhere downstream.
///
/// The repository's answer is a type, from one of three places. What crosses a wire is decoded
/// with a codec, and the signature takes or answers the decoded type. What configures a binding
/// is that binding's options type. What an API this repository does not own hands over is typed
/// in a binding project under `src/Fable.*` (see `EmitHome`), which is also where `obj` may
/// still be written: it is the edge where a value of no declared type is given one. A value whose
/// type genuinely does not matter to the function is a type parameter, not `obj`. Where none of
/// those applies — the one call into an API that takes `object` — the line says it is
/// deliberate: `// fsharpanalyzer: ignore-line YES017` at its end, with the reason above it.
///
/// What counts as a signature, precisely: the parameter and return types of a function — a
/// `let` that takes arguments, in a module at any depth — and of a member: a method, a property,
/// a constructor, an abstract slot. `obj` counts anywhere inside one of those types: a tuple's
/// element, a function's argument, a generic's argument (`obj array`, `Map<string, obj>`). It is
/// read as written, so a library's or a binding's own name for a type it owns (Thoth's
/// `JsonValue`, a binding's `Plugin`) is that name, whatever it abbreviates; F#'s own spellings
/// (`obj` is one) and the product's own abbreviations are what they stand for, so an alias does
/// not hide one.
///
/// What does not: a value (`let private gate = obj ()` is a lock object, not a contract), a
/// local, a record or union field, and an override or an interface implementation, whose
/// signature is the base type's and is judged where that is declared.
///
/// The population is `EmitHome`'s (`Population.reach`). Reported per file, at the declaration's
/// name: the signature is declared in one file, and the typed tree of that file says it whole.

[<Literal>]
let Code = "YES017"

let private isSystemObject (t: FSharpType) =
    t.HasTypeDefinition && t.TypeDefinition.TryFullName = Some "System.Object"

/// Whether `obj` is written anywhere in `t`, reading an abbreviation the way its home decides:
/// F#'s own (`obj`, `string list`) and the product's are expanded, and anyone else's is a name.
let rec private mentionsObj (reach: string list) (t: FSharpType) : bool =
    try
        if t.IsGenericParameter then
            false
        elif t.IsAbbreviation then
            let home = try t.TypeDefinition.Assembly.SimpleName with _ -> ""
            let core = try t.TypeDefinition.AccessPath = "Microsoft.FSharp.Core" with _ -> false

            if core || home = "FSharp.Core" || List.contains home reach then
                mentionsObj reach t.AbbreviatedType
            else
                t.GenericArguments |> Seq.exists (mentionsObj reach)
        else
            isSystemObject t || (t.GenericArguments |> Seq.exists (mentionsObj reach))
    with _ ->
        false

/// A function or a member: something a caller hands arguments to or reads an answer from. A
/// value is neither, and an override's signature belongs to the type it overrides.
let private isSignature (mfv: FSharpMemberOrFunctionOrValue) =
    not mfv.IsCompilerGenerated
    && not mfv.IsOverrideOrExplicitInterfaceImplementation
    && (mfv.IsMember || mfv.IsProperty || not (Seq.isEmpty mfv.CurriedParameterGroups))

let private judged (reach: string list) (mfv: FSharpMemberOrFunctionOrValue) =
    try
        if not (isSignature mfv) then
            None
        else
            let parameters = [ for group in mfv.CurriedParameterGroups do for p in group -> p.Type ]
            let answers = mfv.ReturnParameter.Type

            if (answers :: parameters) |> List.exists (mentionsObj reach) then
                Some (mfv.DisplayName, mfv.DeclarationLocation)
            else
                None
    with _ ->
        // A symbol FCS declines to describe — no location, no parameters it will give — is not
        // one this rule can judge, and the rest of the file still is.
        None

let rec private declarations (ds: FSharpImplementationFileDeclaration list) =
    seq {
        for d in ds do
            match d with
            | FSharpImplementationFileDeclaration.Entity (e, nested) ->
                // Abstract slots have no implementation, so they reach this only through the
                // entity that declares them.
                yield! e.MembersFunctionsAndValues
                yield! declarations nested
            | FSharpImplementationFileDeclaration.MemberOrFunctionOrValue (mfv, _, _) -> yield mfv
            | FSharpImplementationFileDeclaration.InitAction _ -> ()
    }

let private message (name: string) (project: string) (reach: string list) =
    let named = String.concat ", " reach

    $"`%s{name}` has `obj` in its signature: a caller and the body are free to disagree about what "
    + "crosses it, and nothing says when they do. "
    + $"`%s{project}` is product code rather than a binding project (%s{named} in its reach). "
    + "Give it a type instead: take or answer what a codec decodes (Thoth `Decode`) when it crosses "
    + "a wire; take a binding's options type when it configures one; type what an API this "
    + "repository does not own hands over in its binding (src/Fable.*, which references nothing "
    + "Yession); make it a type parameter when its type does not matter here. Where none applies, "
    + "end the line with `// fsharpanalyzer: ignore-line YES017` and say why in a comment above it."

[<CliAnalyzer("ObjSignature", "A signature says what crosses it, and obj says nothing", "")>]
let objSignature: Analyzer<CliContext> =
    fun ctx ->
        async {
            match ctx.TypedTree, Population.reach ctx with
            | _, [] -> return []
            | None, _ -> return []
            | Some tree, reach ->
                let project = Path.GetFileNameWithoutExtension ctx.ProjectOptions.ProjectFileName

                let offenders =
                    declarations tree.Declarations
                    |> Seq.choose (judged reach)
                    // A module-level `let` arrives twice — once as a declaration of its own, once
                    // as a member of the module that holds it.
                    |> Seq.distinctBy snd
                    |> List.ofSeq

                return
                    [ for name, range in offenders ->
                          { Type = "ObjSignature"
                            Message = message name project reach
                            Code = Code
                            Severity = Severity.Error
                            Range = range
                            Fixes = [] } ]
        }
