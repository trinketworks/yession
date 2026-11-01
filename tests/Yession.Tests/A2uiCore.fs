module Yession.Tests.A2uiCore

// The pure core of the A2UI binding (`src/A2ui`: Pointer, Expression, Evaluate, Surface).
//
//   * The spec's conformance suites, converted from YAML into `tests/fixtures/a2ui/conformance`
//     (that directory's README says how, and from which commit), run case by case with the
//     suite's own descriptions as the names. A case runs when it is about v0.9 or names no
//     version, and is about something this binding has. What is left out, and why:
//       - every v1.0 case (a v1.0 `createSurface` carries its data model and components, which
//         v0.9.1 does not);
//       - the data model's observer steps (`watch`, `expect_notified`): this model is a value,
//         and a surface's live data is a Yjs map, whose observers are Yjs's;
//       - the cases in `outOfScope` below, by name;
//       - the four functions that need a locale (`Evaluate` says why), by function.
//   * Every basic-catalog example and the contact-form stream apply through the fold.
//   * The rest pins what the suites do not: the fold's refusals, templates, checks, an event's
//     context, and the multiple-choice question the agent's tool will ask.
//
// Pure: files in, verdicts out. The cheap tier.

open Fable.Pyxpecto
open A2ui

#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

let private fixtures = "tests/fixtures/a2ui"

let private field (name: string) (j: Json) : Json option =
    match j with
    | JObject fields -> fields |> List.tryFind (fun (k, _) -> k = name) |> Option.map snd
    | _ -> None

let private text (name: string) (j: Json) : string option =
    match field name j with
    | Some (JString s) -> Some s
    | _ -> None

/// A field the case cannot be run without.
let private required (name: string) (j: Json) : string =
    match text name j with
    | Some s -> s
    | None -> failwithf "a case or step without %s: %s" name (Codec.toString Codec.encodeJsonValue j)

/// Object keys in order, so two values compare by content and not by how they were written.
let rec private canonical (j: Json) : Json =
    match j with
    | JObject fields -> JObject (fields |> List.map (fun (k, v) -> k, canonical v) |> List.sortBy fst)
    | JArray items -> JArray (List.map canonical items)
    | other -> other

let private suite (file: string) : Json list =
    match Codec.fromString (Decode.list Codec.json) (TestFiles.read (sprintf "%s/conformance/%s" fixtures file)) with
    | Ok cases -> cases
    | Error e -> failwithf "%s: %s" file e

/// The cases left out by name, each with why. A test below keeps every name here one the
/// suites still have, so the list cannot outlive what it excuses.
let private outOfScope =
    Map.ofList
        [ "test_data_model_rejects_forbidden_path_segments",
          "guards a JavaScript object's prototype; this data model is a list of pairs, where `__proto__` is a key like any other"
          "test_evaluate_function_numeric_unparseable_string",
          "calls numeric with neither min nor max, which the v0.9.1 catalog refuses (`anyOf` required); the same value with a max is a case below" ]

/// The version a case is about: its own, else its catalog's.
let private aboutV09 (case: Json) =
    let version = text "protocolVersion" case |> Option.orElse (field "catalog" case |> Option.bind (text "protocolVersion"))
    match version with
    | None
    | Some "v0.9"
    | Some "v0.9.1" -> true
    | Some _ -> false

let private cases (file: string) (action: string) =
    suite file
    |> List.filter (fun c -> text "action" c = Some action && aboutV09 c && not (text "name" c |> Option.exists (fun n -> Map.containsKey n outOfScope)))

let private named (case: Json) = defaultArg (text "description" case) (defaultArg (text "name" case) "?")

let private expectRefusal (expected: Json option) (actual: string) =
    match expected |> Option.bind (text "message") with
    | Some pattern -> Expect.isTrue (System.Text.RegularExpressions.Regex.IsMatch (actual, pattern)) (sprintf "the refusal (%s) says %s" actual pattern)
    | None -> ()

// --- conformance: the data model ------------------------------------------------------------

let private runDataModelCase (case: Json) =
    let steps =
        match field "steps" case with
        | Some (JArray steps) -> steps
        | _ -> []
    let mutable model = defaultArg (field "initial" case) (JObject [])
    for step in steps do
        let refusal = field "expect_error" step
        match text "op" step with
        | Some "get" ->
            let path = required "path" step
            let got = Pointer.get path model
            match field "expect" step, field "expect_absent" step, text "expect_type" step with
            | Some expected, _, _ -> Expect.equal (Option.map canonical got) (Some (canonical expected)) (sprintf "get %s" path)
            // `Json` has no `undefined`: a padded or removed list slot holds null (Pointer.fs).
            | _, Some (JBool true), _ -> Expect.isTrue (got = None || got = Some JNull) (sprintf "get %s finds nothing" path)
            | _, _, Some "list" -> Expect.isTrue (match got with Some (JArray _) -> true | _ -> false) (sprintf "get %s is a list" path)
            | _, _, Some "object" -> Expect.isTrue (match got with Some (JObject _) -> true | _ -> false) (sprintf "get %s is an object" path)
            | _ -> failwithf "a get step this harness does not read: %s" (Codec.toString Codec.encodeJsonValue step)
        | Some "set" ->
            let path = required "path" step
            match Pointer.set path (defaultArg (field "value" step) JNull) model, refusal with
            | Ok written, None -> model <- written
            | Error reason, Some _ -> expectRefusal refusal reason
            | Ok _, Some _ -> failwithf "set %s is refused" path
            | Error reason, None -> failwithf "set %s was refused: %s" path reason
        | Some "delete" -> model <- Pointer.remove (required "path" step) model
        | Some "dispose" -> ()
        | other -> failwithf "a step this harness does not run: %A" other
        match field "expect_values" step with
        | Some (JObject values) ->
            for (at, expected) in values do
                Expect.equal (Pointer.get at model |> Option.map canonical) (Some (canonical expected)) (sprintf "after the step, %s holds" at)
        | _ -> ()

let private dataModelTests =
    testList "data model conformance" [
        for case in cases "data_model.json" "data_model" do
            testCase (named case) <| fun () -> runDataModelCase case
    ]

// --- conformance: the catalog's functions ----------------------------------------------------

let private needsLocale = set [ "formatNumber"; "formatCurrency"; "formatDate"; "pluralize" ]

/// The arguments the catalog types as plain values, never bound.
let private plainArgs = [ "pattern"; "min"; "max"; "url" ]

/// A case's call, each dynamic argument moved into the data model and bound from there. The
/// suite hands an implementation values its argument's literal type cannot hold (`length` of a
/// list, `and` of numbers); in this binding the codec refuses such a literal before a function
/// sees it, so the value reaches the function the way it does on a surface: through a binding.
let private boundCall (case: Json) : Json * Json =
    let binding path = JObject [ "path", JString path ]
    let args =
        match field "args" case with
        | Some (JObject args) -> args
        | _ -> []
    let rewritten =
        args
        |> List.map (fun (name, v) ->
            match v with
            | _ when List.contains name plainArgs -> name, v
            | JArray items when name = "values" -> name, JArray (items |> List.mapi (fun i _ -> binding (sprintf "/values/%d" i)))
            | _ -> name, binding ("/" + name))
    let model = JObject (args |> List.filter (fun (name, _) -> not (List.contains name plainArgs)))
    JObject [ "call", JString (required "function" case); "args", JObject rewritten ], model

let private runFunctionCase (case: Json) =
    let call, model = boundCall case
    let result =
        Decode.fromValue "$" Codec.callDecoder (Codec.encodeJsonValue call)
        |> Result.bind (Evaluate.invoke (Evaluate.root model))
        |> Result.map (fun v -> canonical (defaultArg v JNull))
    match field "expect" case, field "expectError" case with
    | Some expected, _ -> Expect.equal result (Ok (canonical expected)) "the function returns"
    | None, Some _ -> Expect.isError result "the function refuses"
    | None, None -> failwith "a case with neither expect nor expectError"

let private functionTests =
    testList "function conformance" [
        for case in cases "functions.json" "evaluate_function" do
            if field "surface" case = None && not (text "function" case |> Option.exists needsLocale.Contains) then
                testCase (named case) <| fun () -> runFunctionCase case
        testCase "numeric is false for a string that is no number" <| fun () ->
            runFunctionCase (
                JObject
                    [ "function", JString "numeric"
                      "args", JObject [ "value", JString "not_a_number"; "max", JNumber 100.0 ]
                      "expect", JBool false ])
    ]

// --- conformance: formatString's expressions -------------------------------------------------

let rec private exprJson (e: Expr) : Json =
    match e with
    | Expr.Text s -> JString s
    | Expr.Number n -> JNumber n
    | Expr.Boolean b -> JBool b
    | Expr.Path p -> JObject [ "path", JString p ]
    | Expr.Call (name, args) ->
        JObject [ "call", JString name; "args", JObject (args |> List.map (fun (k, v) -> k, exprJson v)); "returnType", JString "any" ]

/// Adjacent text joined and empty text dropped, as the suite's README asks of a harness.
let private joined (parts: Json list) : Json list =
    ([], parts)
    ||> List.fold (fun acc part ->
        match acc, part with
        | _, JString "" -> acc
        | JString a :: rest, JString b -> JString (a + b) :: rest
        | _ -> part :: acc)
    |> List.rev
    |> List.map canonical

let private runExpressionCase (case: Json) =
    let parsed = Expression.parse (required "input" case) |> Result.map (List.map exprJson >> joined)
    match field "expect" case, parsed with
    | Some (JArray expected), _ -> Expect.equal parsed (Ok (joined expected)) "the template's parts"
    | _, Ok parts -> failwithf "parsed, and should be refused: %A" parts
    | _, Error reason -> expectRefusal (field "expect_error" case) reason

let private expressionTests =
    testList "expression conformance" [
        for case in cases "expressions.json" "parse_expression_template" do
            testCase (named case) <| fun () -> runExpressionCase case
    ]

let private scopeTests =
    // The suite's `resolve_path` cases (`conformance/core/message_processor_v0_9.yaml`).
    testList "path resolution" [
        testCase "an absolute path ignores the scope" <| fun () -> Expect.equal (Pointer.resolve "/bar" "/foo") "/foo" "absolute"
        testCase "a relative path is read under the scope" <| fun () -> Expect.equal (Pointer.resolve "/bar" "foo") "/bar/foo" "relative"
        testCase "a scope's trailing slash is not doubled" <| fun () -> Expect.equal (Pointer.resolve "/bar/" "foo") "/bar/foo" "one slash"
        testCase "a relative path at the root is read from the root" <| fun () -> Expect.equal (Pointer.resolve "" "foo") "/foo" "root"
    ]

let private skipListTests =
    testList "left out" [
        testCase "every case left out by name is one the suites still have" <| fun () ->
            let names =
                [ "data_model.json"; "functions.json"; "expressions.json" ] |> List.collect suite |> List.choose (text "name")
            let stale = outOfScope |> Map.toList |> List.map fst |> List.filter (fun n -> not (List.contains n names))
            Expect.equal stale [] "no stale names"
    ]

// --- the fold ---------------------------------------------------------------------------------

let private decodeMessages (text: string) : ServerToClient list =
    match Codec.fromString (Decode.field "messages" (Decode.list Codec.serverToClientDecoder)) text with
    | Ok messages -> messages
    | Error e -> failwith e

let private create id = CreateSurface (id, Codec.BasicCatalogId, None, None)

let private textComponent id words =
    { Id = id; Accessibility = None; Weight = None; Kind = ComponentKind.Text (Literal words, None) }

let private modelOf id (result: Result<Map<string, Surface>, SurfaceError>) =
    result |> Result.map (fun surfaces -> surfaces.[id].DataModel)

let private foldTests =
    testList "the fold" [
        for file in TestFiles.entries (fixtures + "/v0_9_1/examples") |> List.filter (fun f -> f.EndsWith ".json") |> List.sort do
            testCase (sprintf "%s applies to a client with no surfaces" file) <| fun () ->
                let messages = decodeMessages (TestFiles.read (sprintf "%s/v0_9_1/examples/%s" fixtures file))
                Expect.isOk (Surfaces.applyAll Surfaces.empty messages) "every message applies in order"

        testCase "the contact form stream applies line by line and ends with no surface" <| fun () ->
            let messages =
                (TestFiles.read (fixtures + "/v0_9_1/cases/contact_form_example.jsonl")).Split '\n'
                |> Array.filter (fun l -> l.Trim () <> "")
                |> Array.map (fun l -> match Codec.fromString Codec.serverToClientDecoder l with Ok m -> m | Error e -> failwith e)
                |> List.ofArray
            Expect.equal (Surfaces.applyAll Surfaces.empty messages) (Ok Map.empty) "created, filled, deleted"

        testCase "a second createSurface for one id is refused" <| fun () ->
            Expect.equal (Surfaces.applyAll Surfaces.empty [ create "s"; create "s" ]) (Error (AlreadyExists "s")) "it exists"

        testCase "a surface on a catalog this binding does not render is refused" <| fun () ->
            let other = "https://example.com/catalog.json"
            Expect.equal (Surfaces.apply Surfaces.empty (CreateSurface ("s", other, None, None))) (Error (UnknownCatalog other)) "unknown"

        testCase "a message to a surface never created is refused" <| fun () ->
            Expect.equal (Surfaces.apply Surfaces.empty (UpdateDataModel ("s", None, Some JNull))) (Error (NotFound "s")) "no surface"

        testCase "a component sent again replaces the one before" <| fun () ->
            let result =
                Surfaces.applyAll Surfaces.empty [ create "s"; UpdateComponents ("s", [ textComponent "root" "a" ]); UpdateComponents ("s", [ textComponent "root" "b" ]) ]
            Expect.equal (result |> Result.map (fun s -> s.["s"].Components.["root"])) (Ok (textComponent "root" "b")) "the later one"

        testCase "an update with no path replaces the whole model" <| fun () ->
            let result =
                Surfaces.applyAll Surfaces.empty [ create "s"; UpdateDataModel ("s", Some "/a", Some (JNumber 1.0)); UpdateDataModel ("s", None, Some (JObject [ "b", JNumber 2.0 ])) ]
            Expect.equal (modelOf "s" result) (Ok (JObject [ "b", JNumber 2.0 ])) "replaced"

        testCase "an update with no value removes the key" <| fun () ->
            let result =
                Surfaces.applyAll Surfaces.empty [ create "s"; UpdateDataModel ("s", Some "/a", Some (JNumber 1.0)); UpdateDataModel ("s", Some "/a", None) ]
            Expect.equal (modelOf "s" result) (Ok (JObject [])) "removed"

        testCase "an update reaches only the surface it names" <| fun () ->
            let result =
                Surfaces.applyAll Surfaces.empty [ create "s1"; create "s2"; UpdateDataModel ("s1", Some "/v", Some (JString "one")) ]
            Expect.equal (modelOf "s2" result) (Ok (JObject [])) "s2 untouched"

        testCase "a write through a value is refused, naming the surface and the path" <| fun () ->
            let result =
                Surfaces.applyAll Surfaces.empty [ create "s"; UpdateDataModel ("s", Some "/a", Some (JString "x")); UpdateDataModel ("s", Some "/a/b", Some JNull) ]
            Expect.isTrue (match result with Error (DataRefused ("s", "/a/b", _)) -> true | _ -> false) (sprintf "refused: %A" result)

        testCase "deleteSurface forgets the surface" <| fun () ->
            Expect.equal (Surfaces.applyAll Surfaces.empty [ create "s"; DeleteSurface "s" ]) (Ok Map.empty) "gone"
    ]

// --- reading a surface ------------------------------------------------------------------------

let private employees =
    JObject
        [ "company", JString "Acme Corp"
          "employees", JArray [ JObject [ "name", JString "Alice" ]; JObject [ "name", JString "Bob" ] ] ]

let private requiredCheck path message =
    { Condition = Call { Function = Required (Bound path); ReturnType = None }; Message = message }

/// The multiple-choice question: a picker bound to `/choice`, and a submit button that needs a
/// choice made.
let private pickOne =
    { Condition = Call { Function = Length (Bound "/choice", Some 1, None); ReturnType = None }; Message = "Pick one." }

/// The `regex` function over `value`, bound at `/v`.
let private regexOn (value: string) (pattern: string) =
    Evaluate.invoke (Evaluate.root (JObject [ "v", JString value ])) { Function = Regex (Bound "/v", pattern); ReturnType = None }

let private readTests =
    testList "reading a surface" [
        testCase "a template stamps its component once per item, each reading its own item" <| fun () ->
            let scope = Evaluate.root employees
            let names = Evaluate.children scope (Template ("card", "/employees")) |> List.map (fun (_, s) -> Evaluate.textOf s (Bound "name"))
            Expect.equal names [ Ok "Alice"; Ok "Bob" ] "relative paths read the item"

        testCase "an absolute path inside a template still reads from the root" <| fun () ->
            let scope = Evaluate.children (Evaluate.root employees) (Template ("card", "/employees")) |> List.head |> snd
            Expect.equal (Evaluate.textOf scope (Bound "/company")) (Ok "Acme Corp") "the root's value"

        testCase "a template over no list has no children" <| fun () ->
            Expect.equal (Evaluate.children (Evaluate.root employees) (Template ("card", "/nobody"))) [] "none"

        testCase "formatString writes values by the spec's conversions" <| fun () ->
            // `test_expression_parser_data_type_coercion_matrix`, a v1.0 case whose rule v0.9.1 shares.
            let model = JObject [ "isActive", JBool true; "score", JNumber 98.5 ]
            Expect.equal
                (Evaluate.format (Evaluate.root model) "Active: ${/isActive}, Score: ${/score}, Missing: ${/nonexistent}")
                (Ok "Active: true, Score: 98.5, Missing: ")
                "true, 98.5, and nothing"

        testCase "formatString leaves an escaped marker as text" <| fun () ->
            // `test_expression_parser_escaped_interpolation_sequence`, likewise.
            Expect.equal (Evaluate.format (Evaluate.root (JObject [ "amount", JNumber 100.0 ])) "Price is \\${/amount} USD") (Ok "Price is ${/amount} USD") "literal"

        testCase "formatString runs a call inside a template" <| fun () ->
            Expect.equal (Evaluate.format (Evaluate.root (JObject [ "pending", JBool false ])) "Done: ${not(value: /pending)}") (Ok "Done: true") "not false"

        testCase "a function that needs a locale is an error, not text" <| fun () ->
            let call = { Function = FormatNumber (Literal 1.0, None, None); ReturnType = None }
            Expect.isError (Evaluate.invoke (Evaluate.root (JObject [])) call) "no locale yet"

        testCase "regex answers a pattern that would hang a backtracking engine" <| fun () ->
            Expect.equal (regexOn (String.replicate 5000 "a" + "!") "^(a+)+$") (Ok (Some (JBool false))) "no match, and promptly"

        testCase "regex takes the spec's counted patterns" <| fun () ->
            Expect.equal (regexOn "0412345678" @"^\d{10}$") (Ok (Some (JBool true))) "ten digits"
            Expect.equal (regexOn "12345" @"^[0-9]{10,15}$") (Ok (Some (JBool false))) "too short"

        testCase "regex anchors to the whole value, not a line of it" <| fun () ->
            Expect.equal (regexOn "hello\n12345" @"^\d{5}$") (Ok (Some (JBool false))) "the second line is not the value"

        testCase "regex refuses a construct outside the matcher's subset, by name" <| fun () ->
            let result = regexOn "abc" "(?=a)abc"
            Expect.isTrue (match result with Error reason -> reason.Contains "not something this matcher supports" | Ok _ -> false) (sprintf "refused by name: %A" result)

        testCase "the failing checks give their messages, in order" <| fun () ->
            let checks = [ requiredCheck "/a" "A is required."; requiredCheck "/b" "B is required."; requiredCheck "/c" "C is required." ]
            Expect.equal (Evaluate.failing (Evaluate.root (JObject [ "b", JString "x" ])) checks) (Ok [ "A is required."; "C is required." ]) "A and C"

        testCase "an event's context reads its bindings now and leaves out what is unset" <| fun () ->
            let model = JObject [ "choice", JArray [ JString "b" ] ]
            let context = [ "choice", Bound "/choice"; "missing", Bound "/nope"; "kind", Literal (JString "pick") ]
            Expect.equal (Evaluate.context (Evaluate.root model) context) (Ok [ "choice", JArray [ JString "b" ]; "kind", JString "pick" ]) "read, minus the unset"

        testCase "a choice picker's selection is the list at its binding" <| fun () ->
            let model = JObject [ "choice", JArray [ JString "a"; JString "c" ] ]
            Expect.equal (Evaluate.selectionOf (Evaluate.root model) (Bound "/choice")) (Ok [ "a"; "c" ]) "a and c"

        testCase "a choice picker with nothing chosen yet has an empty selection" <| fun () ->
            Expect.equal (Evaluate.selectionOf (Evaluate.root (JObject [])) (Bound "/choice")) (Ok []) "none"

        testCase "the submit check fails while nothing is chosen" <| fun () ->
            Expect.equal (Evaluate.failing (Evaluate.root (JObject [ "choice", JArray [] ])) [ pickOne ]) (Ok [ "Pick one." ]) "blocked"

        testCase "the submit check passes once an option is chosen" <| fun () ->
            Expect.equal (Evaluate.failing (Evaluate.root (JObject [ "choice", JArray [ JString "a" ] ])) [ pickOne ]) (Ok []) "clear"
    ]

let tests =
    testList "A2ui core" [ dataModelTests; functionTests; expressionTests; scopeTests; skipListTests; foldTests; readTests ]
