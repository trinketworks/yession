namespace A2ui

// Reading a surface's bound values and running the basic catalog's functions, as the spec
// (`a2ui_protocol.md`, "Data model representation" and "Client-side logic") and its
// conformance suite (`conformance/core/functions.yaml`) define them. Pure: a scope in, a value
// out. Where the spec leaves a rule open (truthiness, a number's text), the reference engines'
// rule is taken, so a surface reads the same here as in theirs.
//
// The four functions that need a locale's data (`formatNumber`, `formatCurrency`, `formatDate`,
// `pluralize`) decode but do not run: a call to one is an error, which a renderer shows and an
// agent's tool refuses, rather than text in a format nobody chose.

#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

/// Where a binding is read: the surface's data model, and the pointer a relative path is read
/// under.
type Scope =
    { Model : Json
      /// `""` at the root; the item's pointer (`/items/2`) inside a template.
      Base : string }

[<RequireQualifiedAccess>]
module Evaluate =

    /// The scope of a surface's root component.
    let root (model: Json) : Scope = { Model = model; Base = "" }

    /// The value at `path`, read from `scope`.
    let read (scope: Scope) (path: string) : Json option = Pointer.get (Pointer.resolve scope.Base path) scope.Model

    let private numberText (n: float) =
        if System.Math.Floor n = n && abs n < 1e15 then string (int64 n) else string n

    /// A value as text, by the spec's conversion: nothing and `null` are empty, a number or a
    /// boolean as written, an object or a list as compact JSON.
    let text (value: Json option) : string =
        match value with
        | None
        | Some JNull -> ""
        | Some (JString s) -> s
        | Some (JBool b) -> if b then "true" else "false"
        | Some (JNumber n) -> numberText n
        | Some other -> Codec.toString Codec.encodeJsonValue other

    /// A value as a condition: nothing, `null`, `false`, `0` and `""` are false, as in
    /// JavaScript and the reference engines. An empty object or list is true.
    let truthy (value: Json option) : bool =
        match value with
        | None
        | Some JNull
        | Some (JBool false)
        | Some (JString "") -> false
        | Some (JNumber n) -> n <> 0.0 && not (System.Double.IsNaN n)
        | Some _ -> true

    /// A value as a number: a number, or text that is a number literal.
    let number (value: Json option) : float option =
        match value with
        | Some (JNumber n) -> Some n
        | Some (JString s) -> Expression.number (s.Trim ())
        | _ -> None

    /// Whether `text` matches an agent's `pattern`, anchored to the whole text as a `RegExp`
    /// is. Run by the domain's matcher, which cannot backtrack, so no pattern hangs the page
    /// or the Session; a pattern outside its subset is an error that names the construct.
    let matches (pattern: string) (text: string) : Result<bool, string> =
        Yession.Domain.Terminals.Pattern.compileAnchored Yession.Domain.Terminals.TextAnchors pattern
        |> Result.mapError (sprintf "Invalid regex pattern: %s")
        |> Result.bind (fun compiled -> Yession.Domain.Terminals.Pattern.matches compiled text)

    let private emailPattern =
        System.Text.RegularExpressions.Regex "^[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\\.[a-zA-Z]{2,}$"

    /// The schemes `openUrl` opens, as in the reference engines.
    let private schemes = [ "http"; "https"; "mailto"; "tel" ]

    let private within (min: float option) (max: float option) (n: float) =
        (min |> Option.forall (fun m -> n >= m)) && (max |> Option.forall (fun m -> n <= m))

    let private all (f: 'a -> Result<'b, string>) (items: 'a list) : Result<'b list, string> =
        (Ok [], items)
        ||> List.fold (fun acc item -> acc |> Result.bind (fun found -> f item |> Result.map (fun y -> y :: found)))
        |> Result.map List.rev

    let private unsupported (name: string) : Result<Json option, string> =
        Error (sprintf "%s needs a locale, and this renderer formats in none yet" name)

    let rec private asJson (e: Expr) : Json =
        match e with
        | Expr.Text s -> JString s
        | Expr.Number n -> JNumber n
        | Expr.Boolean b -> JBool b
        | Expr.Path p -> JObject [ "path", JString p ]
        | Expr.Call (name, args) -> JObject [ "call", JString name; "args", JObject (args |> List.map (fun (k, v) -> k, asJson v)) ]

    /// A dynamic value as JSON: the literal, what its binding holds (nothing if nothing), or
    /// what its call returns (nothing for `openUrl`, which returns nothing).
    let rec value<'a> (scope: Scope) (literal: 'a -> Json) (d: Dynamic<'a>) : Result<Json option, string> =
        match d with
        | Literal v -> Ok (Some (literal v))
        | Bound path -> Ok (read scope path)
        | Call call -> invoke scope call

    /// What a call returns. `openUrl` only checks its URL here: opening it is the renderer's
    /// act, once this says yes.
    and invoke (scope: Scope) (call: FunctionCall) : Result<Json option, string> =
        let str = value scope JString
        let verdict (b: bool) = Some (JBool b)
        match call.Function with
        | Required v ->
            value scope id v
            |> Result.map (fun j ->
                match j with
                | None
                | Some JNull
                | Some (JString "")
                | Some (JArray []) -> verdict false
                | Some _ -> verdict true)
        | Regex (v, pattern) -> str v |> Result.bind (fun j -> matches pattern (text j) |> Result.map verdict)
        | Length (v, min, max) ->
            str v
            |> Result.map (fun j ->
                let n =
                    match j with
                    | Some (JString s) -> s.Length
                    | Some (JArray items) -> List.length items
                    | _ -> 0
                verdict (within (Option.map float min) (Option.map float max) (float n)))
        | Numeric (v, min, max) ->
            value scope JNumber v |> Result.map (fun j -> verdict (number j |> Option.exists (within min max)))
        | Email v ->
            str v
            |> Result.map (fun j ->
                match j with
                | Some (JString s) -> verdict (emailPattern.IsMatch s)
                | _ -> verdict false)
        | FormatString v -> str v |> Result.bind (text >> format scope) |> Result.map (JString >> Some)
        | FormatNumber _ -> unsupported "formatNumber"
        | FormatCurrency _ -> unsupported "formatCurrency"
        | FormatDate _ -> unsupported "formatDate"
        | Pluralize _ -> unsupported "pluralize"
        | OpenUrl url ->
            let scheme = url.Substring(0, max 0 (url.IndexOf ':')).ToLowerInvariant ()
            if List.contains scheme schemes then Ok None else Error (sprintf "Unsupported URL scheme: %s:" scheme)
        | And values -> all (value scope JBool) values |> Result.map (List.forall truthy >> verdict)
        | Or values -> all (value scope JBool) values |> Result.map (List.exists truthy >> verdict)
        | Not v -> value scope JBool v |> Result.map (truthy >> not >> verdict)

    /// A `formatString` template with each `${...}` replaced by its value as text.
    and format (scope: Scope) (template: string) : Result<string, string> =
        Expression.parse template |> Result.bind (all (part scope)) |> Result.map (String.concat "")

    and private part (scope: Scope) (e: Expr) : Result<string, string> =
        match e with
        | Expr.Text s -> Ok s
        | Expr.Number n -> Ok (numberText n)
        | Expr.Boolean b -> Ok (text (Some (JBool b)))
        | Expr.Path p -> Ok (text (read scope p))
        | Expr.Call _ ->
            // Written out as the JSON call it means, so the codec checks its function and
            // arguments as it checks any other call.
            Decode.fromValue "$" Codec.callDecoder (Codec.encodeJsonValue (asJson e))
            |> Result.bind (invoke scope)
            |> Result.map text

    /// A string property's value: empty while its binding has no value.
    let textOf (scope: Scope) (d: Dynamic<string>) : Result<string, string> = value scope JString d |> Result.map text

    /// A boolean property's value, by truthiness, so a binding to text or a number still reads.
    let conditionOf (scope: Scope) (d: Dynamic<bool>) : Result<bool, string> = value scope JBool d |> Result.map truthy

    /// A number property's value: none while its binding has no number.
    let numberOf (scope: Scope) (d: Dynamic<float>) : Result<float option, string> = value scope JNumber d |> Result.map number

    /// A choice picker's selection: empty while its binding has no value; one value if it holds
    /// one rather than a list.
    let selectionOf (scope: Scope) (d: Dynamic<string list>) : Result<string list, string> =
        value scope (List.map JString >> JArray) d
        |> Result.map (fun j ->
            match j with
            | Some (JArray items) -> items |> List.map (Some >> text)
            | None
            | Some JNull -> []
            | other -> [ text other ])

    /// The messages of the rules in `checks` that fail, in order.
    let failing (scope: Scope) (checks: CheckRule list) : Result<string list, string> =
        checks
        |> all (fun rule -> conditionOf scope rule.Condition |> Result.map (fun ok -> if ok then None else Some rule.Message))
        |> Result.map (List.choose id)

    /// An event's context as it goes to the server, each value read now. A binding with no
    /// value is left out, as JSON leaves out `undefined`.
    let context (scope: Scope) (entries: (string * Dynamic<Json>) list) : Result<(string * Json) list, string> =
        entries
        |> all (fun (key, d) -> value scope id d |> Result.map (Option.map (fun j -> key, j)))
        |> Result.map (List.choose id)

    /// The components a container shows, each with the scope it reads under. A template is
    /// stamped once per item of the list at its path, each reading relative paths under its
    /// item; no list there, no children.
    let children (scope: Scope) (list: ChildList) : (string * Scope) list =
        match list with
        | Static ids -> ids |> List.map (fun id -> id, scope)
        | Template (id, path) ->
            let at = Pointer.resolve scope.Base path
            match Pointer.get at scope.Model with
            | Some (JArray items) -> items |> List.mapi (fun i _ -> id, { scope with Base = sprintf "%s/%d" (at.TrimEnd '/') i })
            | _ -> []
