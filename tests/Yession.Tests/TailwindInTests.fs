module Yession.Tests.TailwindInTests

// A test asserts through data hooks (`Dom.Hooks`), never through what a style token's Tailwind
// string contains (tests/AGENTS.md, "UI tests pin an invariant, never a design"). That rule was a
// sentence, and a sentence is read on the day it is written. This reads every string literal in
// the suite and refuses one that quotes a class the product composes, so the next test that
// reaches for one is red where it is written.
//
// The vocabulary is the product's own, not a guess at what Tailwind spells: every token of every
// string literal in `src/Yession.App/Style.fs`, where the classes the views wear are composed. A
// guess (a pattern of utility prefixes) matches English and misses the theme's own names
// (`text-term-green`); the composed set is exactly what a test could have copied out of a render.
//
// Only a SHAPED class is looked for: one with `-`, `:`, `[` or `/` in it, as `first:pl-0` has. No
// sentence holds one by accident, so it is found wherever it stands, bare or as a selector
// (`.text-center`). A bare word (`hidden`, `block`) is English as often as it is a class, and
// even its selector spelling is a dotfile's name (`.hidden`), so a bare word is not looked for:
// it would cost a false alarm per sentence for the rare test that quotes one.
//
// A name the stylesheets declare as a CSS PROPERTY is not a class, and is left out:
// `Style.ansiInline` writes an inline `style` out of `Style.fs`'s literals, and a browser case
// that reads a computed `background-color` is measuring paint, which is what one should do.
//
// The floor suites read class lists, and that is a different thing (see `floorSuites`).
//
// No capability: it reads files.

open System
open System.Text.RegularExpressions
open Fable.Pyxpecto
open Yession.App

// --- reading F# source --------------------------------------------------------------------

/// Whether `prefix` stands in `source` at `at`.
let private startsAt (source: string) (at: int) (prefix: string) : bool =
    at + prefix.Length <= source.Length
    && source.[at] = prefix.[0]
    && source.Substring (at, prefix.Length) = prefix

/// Every string literal in F# source, as its line and its body: the text between the quotes,
/// escapes left as written. Comments are skipped, so prose about a class in a `//` line is not a
/// quote of it, and so is a character literal, so `'"'` opens nothing. Every form F# writes a
/// string in is read: plain, verbatim (`@"…"`), triple-quoted, and interpolated (`$"…"`,
/// `$"""…"""`), whose holes are read as part of the body.
let private literalsIn (source: string) : (int * string) list =
    let n = source.Length
    // Where each literal opens, and its body. Lines are counted afterwards, in one pass.
    let found = ResizeArray<int * string> ()
    let mutable i = 0
    while i < n do
        let c = source.[i]
        // How many `$` and `@` stand here: a string's prefix, when a quote follows them.
        let mutable prefix = 0
        while i + prefix < n && (source.[i + prefix] = '$' || source.[i + prefix] = '@') do
            prefix <- prefix + 1
        let quote = i + prefix
        if startsAt source i "//" then
            i <- (match source.IndexOf ('\n', i) with -1 -> n | eol -> eol)
        elif startsAt source i "(*" && not (startsAt source i "(*)") then
            let mutable depth = 1
            i <- i + 2
            while i < n && depth > 0 do
                if startsAt source i "(*" then
                    depth <- depth + 1
                    i <- i + 2
                elif startsAt source i "*)" then
                    depth <- depth - 1
                    i <- i + 2
                else
                    i <- i + 1
        elif c = '\'' && i + 2 < n && source.[i + 2] = '\'' then
            i <- i + 3
        elif c = '\'' && i + 3 < n && source.[i + 1] = '\\' && source.[i + 3] = '\'' then
            i <- i + 4
        elif quote < n && source.[quote] = '"' then
            if startsAt source quote "\"\"\"" then
                let close = match source.IndexOf ("\"\"\"", quote + 3) with -1 -> n | at -> at
                found.Add (i, source.Substring (quote + 3, close - quote - 3))
                i <- close + 3
            else
                let verbatim = source.Substring(i, prefix).Contains "@"
                let mutable close = quote + 1
                let mutable closed = false
                while close < n && not closed do
                    if verbatim && startsAt source close "\"\"" then close <- close + 2
                    elif not verbatim && source.[close] = '\\' then close <- close + 2
                    elif source.[close] = '"' then closed <- true
                    else close <- close + 1
                let close = min close n
                found.Add (i, source.Substring (quote + 1, close - quote - 1))
                i <- close + 1
        else
            i <- i + max 1 prefix
    let numbered = ResizeArray<int * string> ()
    let mutable line = 1
    let mutable counted = 0
    for opens, body in found do
        for k in counted .. opens - 1 do
            if source.[k] = '\n' then line <- line + 1
        counted <- opens
        numbered.Add (line, body)
    List.ofSeq numbered

// --- the vocabulary -----------------------------------------------------------------------

/// A class no sentence contains by accident (see the head of the file).
let private shaped (token: string) : bool =
    token |> Seq.exists (fun ch -> ch = '-' || ch = ':' || ch = '[' || ch = '/')

/// The property names a stylesheet declares (`background-color: …`).
let private declaredProperties (css: string) : Set<string> =
    Regex.Matches (css, @"^\s*([a-z][a-z-]*)\s*:", RegexOptions.Multiline)
    |> Seq.map (fun m -> m.Groups.[1].Value)
    |> Set.ofSeq

/// The classes the product composes: every token of every literal in Style.fs, less the names
/// the stylesheets declare as properties.
let private vocabulary : Lazy<Set<string>> =
    lazy
        (let properties = declaredProperties (TestFiles.read "app/tailwind.css" + "\n" + TestFiles.read "app/tokens.css")
         literalsIn (TestFiles.read "src/Yession.App/Style.fs")
         |> List.collect (fun (_, body) -> body.Split ([| ' '; '\n'; '\t' |], StringSplitOptions.RemoveEmptyEntries) |> List.ofArray)
         |> Set.ofList
         |> Set.filter (fun token -> not (properties.Contains token)))

// --- what a test may quote ----------------------------------------------------------------

/// The suites that pin the accessibility floor ONCE and centrally (tests/AGENTS.md). The floor is
/// stated in classes: a focus ring is `focus-visible:outline…`, a field's face goes blue on focus.
/// So these read the class list of EVERY control of a kind and hold a product rule over all of
/// them, which is not a test knowing how one surface is built. They are the one place a class
/// name may stand.
let private floorSuites = [ "Chrome consistency"; "Theme contrast (WCAG 2.0 AA floor)" ]

let private indentOf (text: string) : int = text.Length - text.TrimStart().Length

/// The lines (first and last, from 1) each floor suite spans in `source`: the `testList` line and
/// every line after it indented deeper, which is the list's body, through its closing bracket.
let private floorSpans (source: string) : (int * int) list =
    let lines = source.Split '\n'
    [ for index in 0 .. lines.Length - 1 do
          if floorSuites |> List.exists (fun name -> lines.[index].Contains (sprintf "testList \"%s\"" name)) then
              let indent = indentOf lines.[index]
              let close =
                  seq { index + 1 .. lines.Length - 1 }
                  |> Seq.tryFind (fun at -> lines.[at].Trim () <> "" && indentOf lines.[at] <= indent)
                  |> Option.defaultValue (lines.Length - 1)
              yield index + 1, close + 1 ]

/// The words of a literal, as a selector or an attribute would separate them: `class=\"x\"` and
/// `[class*='x']` both give `x`.
let private wordsOf (body: string) : string list =
    body.Replace("\\n", " ").Replace("\\t", " ").Replace("\\r", " ").Replace ("\\\"", " ")
    |> fun text -> text.Split ([| ' '; '\n'; '\r'; '\t'; '"'; '\''; '='; '\\' |], StringSplitOptions.RemoveEmptyEntries)
    |> List.ofArray

/// The class a word quotes, if it quotes one: bare, or as a selector.
let private quoted (vocabulary: Set<string>) (word: string) : string option =
    let name = if word.StartsWith "." then word.Substring 1 else word
    if shaped name && vocabulary.Contains name then Some name else None

let private suite = "tests/Yession.Tests"

let private testSources () : string list =
    TestFiles.entries suite |> List.filter (fun name -> name.EndsWith ".fs") |> List.sort |> List.map (fun name -> suite + "/" + name)

let tests =
    testList "Tailwind in tests" [

        testCase "a comment is not a literal, and every form a string is written in is one" <| fun () ->
            let source =
                "let a = \"plain\" // \"commented\"\n(* \"block\" (* \"nested\" *) *)\n"
                + "let b = @\"verbatim \"\"x\"\"\" + $\"held {a}\" + \"\"\"triple\"\"\" + string '\"'\n"
            Expect.equal
                (literalsIn source |> List.map snd)
                [ "plain"; "verbatim \"\"x\"\""; "held {a}"; "triple" ]
                "the four literals, and nothing from a comment or a character"

        testCase "the vocabulary holds every class a composed Style value wears" <| fun () ->
            let missing = Style.btnPrimary.Split ' ' |> Array.filter (fun token -> token <> "" && not (vocabulary.Force().Contains token))
            Expect.isEmpty missing "a class `Style.btnPrimary` wears that the reading of Style.fs did not find"

        testCase "no test outside the floor suites quotes a class Style.fs composes" <| fun () ->
            let vocabulary = vocabulary.Force ()
            let quotes =
                [ for file in testSources () do
                      let source = TestFiles.read file
                      let spared = floorSpans source
                      for line, body in literalsIn source do
                          if not (spared |> List.exists (fun (first, last) -> line >= first && line <= last)) then
                              for word in wordsOf body do
                                  match quoted vocabulary word with
                                  | Some name -> yield sprintf "%s:%d quotes `%s`" file line name
                                  | None -> () ]
            Expect.isEmpty
                quotes
                (sprintf
                    "a test quotes a class that src/Yession.App/Style.fs composes:\n  %s\nAssert through a data hook (`Dom.Hooks`), or name the `Style` value the surface wears. A class name stands only in the floor suites (%s), which hold a rule over every control of a kind"
                    (String.concat "\n  " quotes)
                    (String.concat ", " floorSuites))
    ]
