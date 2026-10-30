module Yession.Analyzers.TemplateHoles

open FSharp.Analyzers.SDK
open FSharp.Compiler.Symbols

/// What a Lit template hole is allowed to render.
///
/// `html $"""<b>{x}</b>"""` types as `FormattableString -> TemplateResult`, so every hole
/// is boxed before Lit sees it and NOTHING downstream can say what went in. Put a record
/// in one and Lit prints its ToString; put a union in one and Fable prints the case name.
/// Both compile, both render, and the page is wrong in a way no test of the model can see.
///
/// The compiler cannot object — the box is an explicit `box`, not a conversion the
/// implicit-conversion warnings model — and `%s` cannot help either: FS3579 fires on every
/// hole and offers `%s` as its only remedy, but F# rejects a format specifier in a
/// `FormattableString` outright (FS3376), which is exactly the type Lit's tag takes. So the
/// hole is the one place in a template where the type is known to the compiler and to
/// nobody else.
///
/// The typed tree still has it. A template compiles to
/// `FormattableStringFactory.Create(fmt, [| box<'T> hole; ... |])`, and `'T` there is the
/// type whose stringification a reader of the page actually gets. This asks what each one
/// is, and admits only the six things Lit renders on purpose.
///
/// The six are a string, a `TemplateResult`, a sequence of renderable values, a listener, an
/// `int` and a `bool`. The list is closed, and widening it is deliberate: a hole holding
/// anything else is an error until somebody adds the type here and says in the fixture what
/// the widening means. The failure mode of this rule is silence, and a closed list fails
/// loudly.
module private Renderable =

    // Lit binds a hole in an `@event=` position to a listener. Nothing else in a template
    // is a function, so the shape is unambiguous and the argument type is free to grow
    // (an Event today, a KeyboardEvent tomorrow) without this rule having an opinion.
    let private isListener (t: FSharpType) = t.IsFunctionType

    let rec private isTemplateSequence (t: FSharpType) =
        let element =
            if t.IsGenericParameter then None
            elif t.HasTypeDefinition then
                match t.TypeDefinition.TryFullName with
                | Some ("Microsoft.FSharp.Collections.FSharpList`1"
                       | "System.Collections.Generic.IEnumerable`1"
                       | "Microsoft.FSharp.Collections.seq`1") -> Seq.tryHead t.GenericArguments
                | _ -> None
            else None

        match element with
        | Some e -> isRenderable e
        | None -> false

    and isRenderable (t: FSharpType) =
        let t = t.StripAbbreviations ()

        if isListener t then true
        elif isTemplateSequence t then true
        elif t.HasTypeDefinition then
            match t.TypeDefinition.TryFullName with
            // A string is the whole point: whatever the value was, someone decided how it
            // reads before it reached the template.
            | Some "System.String" -> true
            // A nested template, and a number or a flag, which have one obvious rendering
            // each (a count; a boolean attribute's `?disabled=${b}`).
            | Some "Lit.TemplateResult" -> true
            | Some "System.Int32" -> true
            | Some "System.Boolean" -> true
            | _ -> false
        else false

/// Where a Lit template hole is allowed to sit.
///
/// Lit binds a hole in child content, or as an attribute's VALUE (`a={x}`, `a="b {x}"`,
/// `@click={f}`, `?disabled={b}`). A hole anywhere else inside a tag (an attribute's NAME,
/// `<details {hook}="v">`) is not a part Lit knows. The production build does not throw: it
/// counts one part fewer than there are values, and every value after the hole lands one part
/// late. The settings drawer's expired credential rendered its `<summary>` with no class and the
/// class string as its text, because `credentialReason` wrote its hook's name as a hole.
///
/// Nothing on the CLR sees it. The suite renders a template to a string by substitution, which
/// puts the name exactly where it was meant to go; only Lit's own parse of the static strings
/// goes wrong, and only in a browser. The format string is in the typed tree, so the position of
/// every hole is decidable here.
module private Position =

    /// The holes of a `FormattableString` format, by index, that sit inside a tag and are not an
    /// attribute's value (outside quotes, and not straight after `=`), or that name the tag.
    /// `{{` and `}}` are the format's escaped braces. A `<` opens a tag only before a letter, `/` or `!`, as the HTML
    /// tokenizer reads it, so `a < b` in text is text.
    let misplaced (format: string) : int list =
        let n = format.Length
        let rec go i inTag (quote: char option) (prev: char) acc =
            if i >= n then List.rev acc
            else
                let c = format[i]
                if c = '{' && i + 1 < n && format[i + 1] = '{' then go (i + 2) inTag quote '{' acc
                elif c = '}' && i + 1 < n && format[i + 1] = '}' then go (i + 2) inTag quote '}' acc
                elif c = '{' then
                    let close = format.IndexOf ('}', i)
                    let index = int (format.Substring (i + 1, close - i - 1))
                    let bad = (inTag && quote.IsNone && prev <> '=') || (not inTag && prev = '<')
                    go (close + 1) inTag quote '}' (if bad then index :: acc else acc)
                elif not inTag then
                    let opens = c = '<' && i + 1 < n && (System.Char.IsLetter format[i + 1] || format[i + 1] = '/' || format[i + 1] = '!')
                    go (i + 1) opens None c acc
                else
                    match quote with
                    | Some q when c = q -> go (i + 1) true None c acc
                    | Some _ -> go (i + 1) true quote c acc
                    | None when c = '"' || c = '\'' -> go (i + 1) true (Some c) c acc
                    | None when c = '>' -> go (i + 1) false None c acc
                    | None -> go (i + 1) true None c acc
        go 0 false None ' ' []

let private hole (e: FSharpExpr) =
    match e with
    | FSharpExprPatterns.Call (_, mfv, _, [ held ], [ arg ]) when mfv.DisplayName = "box" -> Some (arg.Range, held)
    | _ -> None

let private isTemplateFormat (mfv: FSharpMemberOrFunctionOrValue) =
    mfv.CompiledName = "Create"
    && mfv.DeclaringEntity
       |> Option.bind (fun e -> e.TryFullName)
       |> Option.contains "System.Runtime.CompilerServices.FormattableStringFactory"

/// Every template in a body: its format, when the compiler left it a constant, and its holes.
let rec private walk report (e: FSharpExpr) =
    match e with
    | FSharpExprPatterns.Call (_, mfv, _, _, [ format; FSharpExprPatterns.NewArray (_, holes) ]) when isTemplateFormat mfv ->
        let format =
            match format with
            | FSharpExprPatterns.Const ((:? string as f), _) -> Some f
            | _ -> None
        report (format, holes)
    | _ -> ()

    for sub in e.ImmediateSubExpressions do
        walk report sub

let rec private declarations report (ds: FSharpImplementationFileDeclaration list) =
    for d in ds do
        match d with
        | FSharpImplementationFileDeclaration.Entity (_, nested) -> declarations report nested
        | FSharpImplementationFileDeclaration.MemberOrFunctionOrValue (_, _, body) -> walk report body
        | FSharpImplementationFileDeclaration.InitAction e -> walk report e

[<Literal>]
let Code = "YES001"

[<CliAnalyzer("TemplateHole", "A Lit template hole renders something Lit can render", "")>]
let templateHole: Analyzer<CliContext> =
    fun ctx ->
        async {
            let offenders = ResizeArray ()

            match ctx.TypedTree with
            | Some tree ->
                declarations
                    (fun (_, holes) ->
                        for (range, held) in List.choose hole holes do
                            if not (Renderable.isRenderable held) then
                                offenders.Add (range, held))
                    tree.Declarations
            | None -> ()

            return
                [ for (range, held: FSharpType) in offenders ->
                    { Type = "TemplateHole"
                      Message =
                        $"this template hole renders %s{held.Format FSharpDisplayContext.Empty}, which Lit stringifies "
                        + "however it happens to stringify. Render it to a string here."
                      Code = Code
                      Severity = Severity.Error
                      Range = range
                      Fixes = [] } ]
        }

[<Literal>]
let PositionCode = "YES020"

[<CliAnalyzer("TemplateHolePosition", "A Lit template hole is content or an attribute value", "")>]
let templateHolePosition: Analyzer<CliContext> =
    fun ctx ->
        async {
            let offenders = ResizeArray ()

            match ctx.TypedTree with
            | Some tree ->
                declarations
                    (fun (format, holes) ->
                        let holes = List.toArray holes
                        for index in format |> Option.map Position.misplaced |> Option.defaultValue [] do
                            if index < holes.Length then offenders.Add holes[index].Range)
                    tree.Declarations
            | None -> ()

            return
                [ for range in offenders ->
                    { Type = "TemplateHolePosition"
                      Message =
                        "this template hole sits inside a tag but is not an attribute's value. Lit binds no part "
                        + "there, and in a production build every later value lands one part late. Write the "
                        + "attribute's name literally and put only its value in the hole."
                      Code = PositionCode
                      Severity = Severity.Error
                      Range = range
                      Fixes = [] } ]
        }
