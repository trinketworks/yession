namespace A2ui

/// One part of a `formatString` template: literal text, or what one `${...}` holds.
[<RequireQualifiedAccess>]
type Expr =
    | Text of string
    | Number of float
    | Boolean of bool
    /// A data model pointer, relative when it has no leading `/`.
    | Path of string
    /// A catalog function by name, with named arguments.
    | Call of name: string * args: (string * Expr) list

// The expression grammar behind `formatString`, as the spec's conformance suite
// (`conformance/core/expressions.yaml`) and the reference parser in
// `typescript/core/web_core/src/expressions/expression_parser.ts` define it. The scanner here
// follows that parser step for step, because the suite pins its edges (what is a number and
// what is a path, which errors say what), not a grammar written down apart from it.
//
// One difference: an identifier character is a letter, a digit or `_` (UTF-16, as .NET and
// JavaScript strings both count), where the reference uses Unicode's XID_Continue.

[<RequireQualifiedAccess>]
module Expression =

    /// How deep `${...}` and call arguments may nest. Every reference engine uses 100.
    [<Literal>]
    let MaxDepth = 100

    /// The longest template, in UTF-16 code units, as in the reference engines.
    [<Literal>]
    let MaxLength = 10000

    /// The most parts one template may have, as in the reference engines.
    [<Literal>]
    let MaxParts = 1000

    exception private Refused of string

    let private refuse message = raise (Refused message)

    let private numberLiteral =
        System.Text.RegularExpressions.Regex "^[+-]?(?:[0-9]+\\.?[0-9]*|\\.[0-9]+)(?:[eE][+-]?[0-9]+)?$"

    let private isDigit (c: char) = c >= '0' && c <= '9'

    let private isIdContinue (c: char) = System.Char.IsLetterOrDigit c || c = '_'

    type private Scanner (input: string) =
        member val Pos = 0 with get, set
        member s.AtEnd = s.Pos >= input.Length
        /// The character `offset` ahead, or NUL past the end (as the reference scanner).
        member s.Peek (offset: int) =
            let i = s.Pos + offset
            if i < input.Length then input.[i] else '\000'
        member s.Advance (count: int) = s.Pos <- s.Pos + count
        member s.Matches (text: string) = input.Length - s.Pos >= text.Length && input.Substring (s.Pos, text.Length) = text
        member s.From (start: int) = input.Substring (start, s.Pos - start)
        member s.Rest = input.Substring s.Pos
        member s.SkipWhitespace () =
            while not s.AtEnd && System.Char.IsWhiteSpace (s.Peek 0) do
                s.Advance 1
        /// `word`, when no identifier character follows it.
        member s.Keyword (word: string) =
            if s.Matches word && not (isIdContinue (s.Peek word.Length)) then
                s.Advance word.Length
                true
            else
                false

    /// The text of one `${...}`, the scanner just past its `${`, leaving it past the `}`.
    let private interpolation (s: Scanner) : string =
        let start = s.Pos
        let mutable balance = 1
        while not s.AtEnd && balance > 0 do
            let c = s.Peek 0
            s.Advance 1
            if c = '{' then
                balance <- balance + 1
            elif c = '}' then
                balance <- balance - 1
            elif c = '\'' || c = '"' then
                let mutable closed = false
                while not s.AtEnd && not closed do
                    let q = s.Peek 0
                    s.Advance 1
                    if q = '\\' then s.Advance 1
                    elif q = c then closed <- true
        if balance > 0 then refuse "Unclosed interpolation: missing '}'"
        let content = s.From start
        content.Substring (0, content.Length - 1)

    let private stringLiteral (s: Scanner) : Expr =
        let quote = s.Peek 0
        s.Advance 1
        let out = System.Text.StringBuilder ()
        let mutable closed = false
        while not s.AtEnd && not closed do
            let c = s.Peek 0
            s.Advance 1
            if c = '\\' then
                if not s.AtEnd then
                    let next = s.Peek 0
                    s.Advance 1
                    out.Append (
                        match next with
                        | 'n' -> '\n'
                        | 't' -> '\t'
                        | 'r' -> '\r'
                        | other -> other
                    )
                    |> ignore
            elif c = quote then
                closed <- true
            else
                out.Append c |> ignore
        Expr.Text (out.ToString ())

    let private numberStart (s: Scanner) =
        let o = if s.Peek 0 = '-' || s.Peek 0 = '+' then 1 else 0
        isDigit (s.Peek o) || (s.Peek o = '.' && isDigit (s.Peek (o + 1)))

    /// A literal the pattern passed, as a float. Written out to the one shape both runtimes'
    /// parsers take: no `+`, a digit before the point, none after a bare point.
    let private parseNumber (text: string) : float =
        let negative = text.StartsWith "-"
        let unsigned = text.TrimStart ('+', '-')
        let exponentAt = unsigned.IndexOfAny [| 'e'; 'E' |]
        let mantissa, exponent =
            if exponentAt < 0 then unsigned, "" else unsigned.Substring (0, exponentAt), unsigned.Substring exponentAt
        let mantissa = (if mantissa.StartsWith "." then "0" + mantissa else mantissa).TrimEnd '.'
        let value = float (mantissa + exponent)
        if negative then -value else value

    /// `text` as a number literal of this grammar, if it is one that a float holds.
    let number (text: string) : float option =
        if numberLiteral.IsMatch text then
            Some (parseNumber text) |> Option.filter (fun v -> not (System.Double.IsInfinity v))
        else
            None

    let private numberLit (s: Scanner) : Expr =
        let start = s.Pos
        if s.Peek 0 = '-' || s.Peek 0 = '+' then s.Advance 1
        while not s.AtEnd && (isDigit (s.Peek 0) || s.Peek 0 = '.') do
            s.Advance 1
        // A malformed exponent (`1e`, `1e+`) is still taken, so it is refused as a literal
        // rather than left behind as trailing characters.
        if not s.AtEnd && (s.Peek 0 = 'e' || s.Peek 0 = 'E') then
            s.Advance 1
            if not s.AtEnd && (s.Peek 0 = '+' || s.Peek 0 = '-') then s.Advance 1
            while not s.AtEnd && isDigit (s.Peek 0) do
                s.Advance 1
        let text = s.From start
        if not (numberLiteral.IsMatch text) then refuse (sprintf "Invalid number literal: '%s'" text)
        let value = parseNumber text
        if System.Double.IsInfinity value || System.Double.IsNaN value then
            refuse (sprintf "Number literal is out of range: '%s'" text)
        Expr.Number (if value = 0.0 then 0.0 else value)

    let private scanWhile (allowed: char -> bool) (s: Scanner) : string =
        let start = s.Pos
        while not s.AtEnd && allowed (s.Peek 0) do
            s.Advance 1
        s.From start

    let private pathOrIdentifier = scanWhile (fun c -> isIdContinue c || c = '/' || c = '.' || c = '-')

    let private identifier = scanWhile isIdContinue

    /// The whole of one `${...}`'s text as one expression. Empty is the empty string.
    let rec private expression (text: string) (depth: int) : Expr =
        match text.Trim () with
        | "" -> Expr.Text ""
        | text ->
            let s = Scanner text
            let result = value s depth
            if not s.AtEnd then refuse (sprintf "Unexpected characters at end of expression: '%s'" s.Rest)
            result

    and private value (s: Scanner) (depth: int) : Expr =
        // Both ways down pass here, a `${` inside a `${` and a call's argument, so both count.
        if depth > MaxDepth then refuse "Max recursion depth reached in parse"
        s.SkipWhitespace ()
        if s.AtEnd then
            Expr.Text ""
        elif s.Matches "${" then
            s.Advance 2
            expression (interpolation s) (depth + 1)
        elif s.Peek 0 = '\'' || s.Peek 0 = '"' then
            stringLiteral s
        elif numberStart s then
            numberLit s
        elif s.Keyword "true" then
            Expr.Boolean true
        elif s.Keyword "false" then
            Expr.Boolean false
        elif s.Keyword "null" then
            Expr.Text ""
        else
            let token = pathOrIdentifier s
            s.SkipWhitespace ()
            if s.Peek 0 = '(' then call token s depth
            elif token = "" then Expr.Text ""
            else Expr.Path token

    and private call (name: string) (s: Scanner) (depth: int) : Expr =
        s.Advance 1
        s.SkipWhitespace ()
        let args = ResizeArray<string * Expr> ()
        while not s.AtEnd && s.Peek 0 <> ')' do
            let arg = identifier s
            s.SkipWhitespace ()
            if s.Peek 0 <> ':' then refuse (sprintf "Expected ':' after argument name '%s' in function '%s'" arg name)
            s.Advance 1
            s.SkipWhitespace ()
            args.Add (arg, value s (depth + 1))
            s.SkipWhitespace ()
            if s.Peek 0 = ',' then
                s.Advance 1
                s.SkipWhitespace ()
        if s.Peek 0 <> ')' then refuse (sprintf "Expected ')' after function arguments for '%s'" name)
        s.Advance 1
        Expr.Call (name, List.ofSeq args)

    let private parts (input: string) : Expr list =
        if input.Length > MaxLength then refuse (sprintf "A template is %d characters at most" MaxLength)
        let s = Scanner input
        let found = ResizeArray<Expr> ()
        while not s.AtEnd do
            if found.Count >= MaxParts then refuse (sprintf "A template has %d parts at most" MaxParts)
            if s.Matches "${" then
                s.Advance 2
                found.Add (expression (interpolation s) 1)
            elif s.Matches "\\${" then
                s.Advance 3
                found.Add (Expr.Text "${")
            else
                let start = s.Pos
                while not s.AtEnd && not (s.Matches "${") && not (s.Matches "\\${") do
                    s.Advance 1
                found.Add (Expr.Text (s.From start))
        found |> Seq.filter (fun p -> p <> Expr.Text "") |> List.ofSeq

    /// The parts of a `formatString` template, or why it is not one.
    let parse (template: string) : Result<Expr list, string> =
        try
            Ok (parts template)
        with Refused message ->
            Error message
