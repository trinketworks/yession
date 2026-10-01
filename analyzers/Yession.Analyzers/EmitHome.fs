module Yession.Analyzers.EmitHome

open System.IO
open FSharp.Analyzers.SDK

/// An `[<Emit>]` is declared in a binding project, and nowhere else.
///
/// An emit is a promise about an API this repository does not own, written in a language
/// nothing here type-checks. The repository's answer to that is not to forbid them but to
/// HOUSE them: a binding project under `src/Fable.*` declares the foreign API once, behind an
/// F# signature, and the product calls that signature like any other F# — so the string is in
/// one place, next to the other strings about the same library, where the emit rules read it
/// and the binding's own suite exercises it. Product code that writes its own macro has none of
/// that: a program in a string beside the one caller that needed it, which is how the residue
/// accumulated — a hundred-odd lines of JavaScript in `PaneShell.fs`, a 65-line program in
/// `AttachWs.fs`, twelve tool definitions inside one macro in the Domain — and why it took a
/// long sweep, one binding at a time, to drive the count outside `src/Fable.*` to zero.
///
/// Nothing kept it there. `Type safety` in CLAUDE.md says not to introduce one, and the emit
/// rules read every macro they find, but neither asks WHERE a macro is written: a new emit in
/// `app/` that names its arguments correctly and declares nothing in its body is clean to both.
/// An invariant that holds because every author read a paragraph is a convention with a good
/// reputation. This is the invariant.
///
/// The population is derived, not listed: a binding project is one with nothing Yession in its
/// reach — it is not itself a `Yession.*` project and references none. That is the structural
/// definition of "an API we do not own, bound for our use": a binding knows nothing of the
/// product it serves. It exempts the `src/Fable.*` projects, the serial example (which owns its
/// copies, as the examples rule requires, and references nothing of the product by the same
/// rule), and the analyzer fixtures, with no list for any of them to be missing from. And it
/// judges the Domain, which references nothing at all but IS the product — and is where the
/// twelve tool definitions sat in their macro.
///
/// Every attribute of the Emit family counts, not only the raw macro the other emit rules read:
/// `[<EmitMethod>]` has no string to get wrong, but it is still a binding to somebody else's
/// API, and a binding belongs in a binding project. `[<Import>]` does not — it names a module
/// and an export, carries no JavaScript, and is how product code reaches a platform module that
/// has no binding project of its own.

[<Literal>]
let Code = "YES011"

let private isProduct (assembly: string) =
    assembly = "Yession" || assembly.StartsWith "Yession."

/// Every `Yession.*` assembly in this project's reach, itself included. Empty for a binding
/// project. One answer per project, so it is computed once and kept while that project is the
/// one being analyzed — it holds names, not symbols, but `Kept` is the shape the rules share.
let private kept = Kept.Answer<string list> ()

let private reach (ctx: CliContext) =
    let project = ctx.ProjectOptions.ProjectFileName

    kept.For (
        project,
        fun () ->
            let own = Path.GetFileNameWithoutExtension project

            let referenced =
                [ for assembly in ctx.CheckProjectResults.ProjectContext.GetReferencedAssemblies () do
                      match (try Some assembly.SimpleName with _ -> None) with
                      | Some name -> yield name
                      | None -> () ]

            own :: referenced |> List.filter isProduct |> List.distinct |> List.sort
    )

let private message (project: string) (reach: string list) =
    let named = String.concat ", " reach

    $"this emit is declared in `%s{project}`, which is product code rather than a binding project "
    + $"(%s{named} in its reach). An emit is a promise about an API this repository does not own, "
    + "and it is kept in a binding project — one that references nothing Yession, like those under "
    + "src/Fable.* — behind an F# signature the product calls. Move it into one, or write it in F#."

[<CliAnalyzer("EmitHome", "An [<Emit>] is declared in a binding project", "")>]
let emitHome: Analyzer<CliContext> =
    fun ctx ->
        async {
            match ctx.TypedTree, reach ctx with
            | _, [] -> return []
            | None, _ -> return []
            | Some tree, reach ->
                let project = Path.GetFileNameWithoutExtension ctx.ProjectOptions.ProjectFileName

                let offenders =
                    [ for mfv in Emits.members tree.Declarations do
                          match Emits.emitOn mfv with
                          | Some range -> yield range
                          | None -> () ]
                    // A module-level `let` arrives twice — once as a declaration of its own,
                    // once as a member of the module that holds it (see `Emits.macroOn`).
                    |> List.distinct

                return
                    [ for range in offenders ->
                        { Type = "EmitHome"
                          Message = message project reach
                          Code = Code
                          Severity = Severity.Error
                          Range = range
                          Fixes = [] } ]
        }
