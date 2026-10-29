module Yession.Analyzers.EmDashes

open System.IO
open FSharp.Analyzers.SDK
open FSharp.Compiler.Syntax
open FSharp.Compiler.Text
open FSharp.Compiler.Tokenization

/// Product copy holds no em dash.
///
/// An em dash joins two clauses into one sentence and leaves the reader to find where the first
/// one stops. Copy is read once, often on a phone, often by a reader whose first language is not
/// English, and a sentence that has to be parsed twice is a sentence half of them misread. A
/// sweep took the em dashes out of the product's copy one surface at a time (PRs #1480, #1482,
/// #1483, #1485, #1491 and #1492) and rewrote each sentence in Simplified Technical English:
/// short sentences, active voice, a colon or parentheses where two parts belong together. Nothing
/// kept them out. A new string is written by someone who has not read the sweep, and the dash is
/// one keystroke away. This is the invariant.
///
/// What is reported is a string literal that holds U+2014 (em dash), or U+2013 (en dash) with a
/// space on each side, which is an em dash set in the other character. An en dash between two
/// numbers (`1–3`) is a range and is left alone. Every spelling of a literal counts: plain,
/// verbatim, triple-quoted and interpolated, in an expression, a pattern or an attribute's
/// arguments, on both sides of an `#if FABLE_COMPILER`. It is read off the lexer's tokens (`scan`
/// says why not the syntax tree), so a comment is never a literal and never reported.
///
/// A template literal can hold text for a reader of the SOURCE rather than of the page, and that
/// is a comment in all but the F# sense. Two shapes are left alone: an HTML comment (`<!-- … -->`,
/// across the template's holes), which `View.fs` and `ManagerUi.fs` use to say why a piece of
/// markup is the way it is; and a line of the template that starts with `//`, which is a comment
/// in the inline script the template carries. Neither reaches anyone who uses the product.
///
/// The population is product projects: `app/` and `src/Yession.*`. It is `Population.reach`
/// (shared with YES011, YES013, YES015-YES017) narrowed to those two directories, so a binding
/// project under `src/Fable.*` is not judged, and neither are `tests/`, `tools/`, `analyzers/` and
/// `examples/`, whose strings are fixtures, expectations and messages for a developer. The
/// analyzer fixtures are judged as though they were product, because they are what proves the rule
/// still sees.
///
/// A module of a product project can be a test harness rather than product:
/// `app/browser/EditorHarness.fs` is bundled into the page the browser suites drive
/// (`tests/browser/out/harness.js`, `Target.Harness` in `tasks.fsx`) and into nothing that ships,
/// and its strings are test data a case asserts on. Such a module says so with `[<Harness>]`
/// (`Yession.App.HarnessAttribute`), the way a test helper says it waits with `[<Waits>]` (YES014):
/// in plain sight on the module, matched by name, and the whole exemption. A list of exempt files
/// kept here would be a second place to look.

[<Literal>]
let Code = "YES018"

/// The exemption's name. Matched by name, as YES014 matches `WaitsAttribute`, so a fixture
/// declares its own and is judged exactly as the product is.
let private isMarker (attribute: SynAttribute) =
    match List.tryLast attribute.TypeName.LongIdent with
    | Some ident -> ident.idText = "Harness" || ident.idText = "HarnessAttribute"
    | None -> false

let private marked (attributes: SynAttributes) =
    attributes |> List.exists (fun list -> list.Attributes |> List.exists isMarker)

/// Where the modules marked `[<Harness>]` are, at the top of the file or nested.
let private harnesses (input: ParsedInput) =
    let rec within (decls: SynModuleDecl list) =
        [ for decl in decls do
              match decl with
              | SynModuleDecl.NestedModule (moduleInfo = SynComponentInfo (attributes = attributes); decls = inner; range = range) ->
                  if marked attributes then yield range else yield! within inner
              | _ -> () ]

    match input with
    | ParsedInput.ImplFile (ParsedImplFileInput (contents = modules)) ->
        [ for SynModuleOrNamespace (attribs = attributes; decls = decls; range = range) in modules do
              if marked attributes then yield range else yield! within decls ]
    | ParsedInput.SigFile _ -> []

let private dash = '—'
let private enDash = '–'

/// One literal, read a character at a time, and where in it a dash is copy: not inside an HTML
/// comment, and not on a line of it that starts with `//`.
type private Literal () =
    let recent = System.Text.StringBuilder ()
    let line = System.Text.StringBuilder ()
    let mutable inComment = false
    let mutable inLineComment = false
    let mutable spaced: pos option = None

    let endsWith (s: string) =
        recent.Length >= s.Length && recent.ToString (recent.Length - s.Length, s.Length) = s

    member _.Feed (c: char, where: pos) : pos list =
        recent.Append c |> ignore
        if recent.Length > 4 then recent.Remove (0, recent.Length - 4) |> ignore

        if c = '\n' then
            line.Clear () |> ignore
            inLineComment <- false
            spaced <- None
            []
        elif inComment then
            if endsWith "-->" then inComment <- false
            []
        elif inLineComment then
            []
        else
            line.Append c |> ignore

            if endsWith "<!--" then
                inComment <- true
                spaced <- None
                []
            elif line.ToString().TrimStart () = "//" then
                inLineComment <- true
                []
            else
                let closed =
                    match spaced with
                    | Some at when c = ' ' -> [ at ]
                    | _ -> []

                spaced <- if c = enDash && endsWith (" " + string enDash) then Some where else None
                if c = dash then where :: closed else closed

/// Which lexer states are inside a literal at the end of a token.
let private inString (state: FSharpTokenizerLexState) =
    match FSharpLineTokenizer.ColorStateOfLexState state with
    | FSharpTokenizerColorState.String
    | FSharpTokenizerColorState.VerbatimString
    | FSharpTokenizerColorState.TripleQuoteString -> true
    | _ -> false

/// Every dash in copy in one file, as the lexer sees it with one set of conditional defines.
///
/// Read off the tokens, not the syntax tree. The SDK's syntax walker steps over a `match!`, an
/// anonymous record and an assignment without looking inside, and a parse tree holds only the
/// `#if` branch its defines chose, while the product's own branch is `FABLE_COMPILER`. The lexer
/// sees every literal, and a token says what it is: a comment is never a string token.
let private scan (source: ISourceText) (fileName: string) (defines: string list) =
    let tokenizer = FSharpSourceTokenizer (defines, Some fileName, None, None)
    let mutable state = FSharpTokenizerLexState.Initial
    let mutable current: Literal option = None
    // A literal whose hole is open: `{` began an expression, and `}` resumes the literal.
    let holes = System.Collections.Generic.Stack<Literal> ()
    let found = ResizeArray<pos> ()

    for index in 0 .. source.GetLineCount () - 1 do
        let text = source.GetLineString index
        let lineTokens = tokenizer.CreateLineTokenizer text
        let mutable reading = true

        while reading do
            match lineTokens.ScanToken state with
            | Some token, next ->
                state <- next

                if token.CharClass = FSharpTokenCharKind.String then
                    // The text after a hole starts at its `}`, whatever the token is named: the
                    // lexer names the rest of a triple-quoted line plain string text.
                    let resumes = text[token.LeftColumn] = '}'

                    let literal =
                        match current with
                        | Some literal -> literal
                        | None when resumes && holes.Count > 0 ->
                            let literal = holes.Pop ()
                            // The hole is not text, so nothing reads across it.
                            literal.Feed ('\000', Position.mkPos (index + 1) token.LeftColumn) |> ignore
                            literal
                        | None -> Literal ()

                    for column in token.LeftColumn .. min token.RightColumn (text.Length - 1) do
                        found.AddRange (literal.Feed (text[column], Position.mkPos (index + 1) column))

                    if text[min token.RightColumn (text.Length - 1)] = '{' && not (inString next) then
                        holes.Push literal
                        current <- None
                    elif inString next then
                        current <- Some literal
                    else
                        current <- None
            | None, next ->
                state <- next
                reading <- false

        match current with
        | Some literal -> literal.Feed ('\n', Position.mkPos (index + 1) text.Length) |> ignore
        | None -> ()

    List.ofSeq found

/// Whether this project is product: in `Population.reach`, and in `app/` or `src/`, or a fixture.
let private judged (ctx: CliContext) =
    match Population.reach ctx with
    | [] -> false
    | _ ->
        let project = ctx.ProjectOptions.ProjectFileName

        match Population.repositoryOf (Path.GetDirectoryName project) with
        | None -> false
        | Some root ->
            let relative = (Path.GetRelativePath (root, project)).Replace ('\\', '/')
            [ "app/"; "src/"; "analyzers/fixtures/" ] |> List.exists relative.StartsWith

let private message =
    "this string holds an em dash (or an en dash with a space on each side), and product copy "
    + "holds none. A dash joins two clauses into one sentence, and the reader has to find where "
    + "the first one stops. Split the sentence in two. Where the two parts belong together, use "
    + "a colon or parentheses."

[<CliAnalyzer("EmDashes", "Product copy holds no em dash", "")>]
let emDashes: Analyzer<CliContext> =
    fun ctx ->
        async {
            if not (judged ctx) then
                return []
            else
                let exempt = harnesses ctx.ParseFileResults.ParseTree

                let offenders =
                    // Both sides of every `#if FABLE_COMPILER`: the product runs as the first.
                    [ for defines in [ []; [ "FABLE_COMPILER" ] ] do
                          yield! scan ctx.SourceText ctx.FileName defines ]
                    |> List.distinct
                    |> List.filter (fun at -> not (exempt |> List.exists (fun r -> Range.rangeContainsPos r at)))
                    |> List.sortBy (fun at -> at.Line, at.Column)

                return
                    [ for at in offenders ->
                        { Type = "EmDashes"
                          Message = message
                          Code = Code
                          Severity = Severity.Error
                          Range = Range.mkRange ctx.FileName at (Position.mkPos at.Line (at.Column + 1))
                          Fixes = [] } ]
        }
