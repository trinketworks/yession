namespace A2ui

// The one door between A2UI's JSON and its types.
//
// A decoder here refuses what the spec's schemas refuse, so a message that decodes is one
// `ajv` would pass: a property a component does not declare (`unevaluatedProperties: false`),
// a value outside an enum, a function's arguments in the wrong shape, a `returnType` the slot
// cannot take. It refuses one thing the schemas let through: a call whose function returns
// the wrong type for its slot with no `returnType` stated (`formatString` as a check's
// condition). The schema cannot see that; a renderer would have to guess, so this says no.
//
// An encoder writes `v0.9.1`. A decoder reads `v0.9` too, which the schemas also accept and
// every example in the spec still carries.

// `System` stays unopened: it would shadow `Action` with `System.Action` and the `regex`
// function's case with `Regex`.
open System.Globalization

#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

[<RequireQualifiedAccess>]
module Codec =

    /// The version an encoder writes.
    [<Literal>]
    let Version = "v0.9.1"

    /// The catalog this binding renders, as `createSurface` names it.
    [<Literal>]
    let BasicCatalogId = "https://a2ui.org/specification/v0_9/catalogs/basic/catalog.json"

    // --- decoding helpers -------------------------------------------------------------------

    let private keysOf : Decoder<string list> =
        Decode.keyValuePairs Decode.value |> Decode.map (List.map fst)

    /// An object whose keys are all in `allowed`, then `decoder` over it.
    let private only (allowed: string list) (decoder: Decoder<'a>) : Decoder<'a> =
        keysOf
        |> Decode.andThen (fun keys ->
            match keys |> List.filter (fun k -> not (List.contains k allowed)) with
            | [] -> decoder
            | extra -> Decode.fail (sprintf "unknown properties: %s" (System.String.Join (", ", extra))))

    let private has (key: string) : Decoder<bool> = keysOf |> Decode.map (List.contains key)

    let private isText : Decoder<bool> =
        Decode.oneOf [ Decode.string |> Decode.map (fun _ -> true); Decode.succeed false ]

    /// A JSON number, refusing the text of one.
    let private number : Decoder<float> =
        isText |> Decode.andThen (fun text -> if text then Decode.fail "a number, not the text of one" else Decode.float)

    /// A JSON integer at or above zero.
    let private count : Decoder<int> =
        number
        |> Decode.andThen (fun n ->
            if n < 0.0 || System.Math.Floor n <> n then Decode.fail "a whole number, zero or more" else Decode.succeed (int n))

    let private matching (pattern: string) (what: string) : Decoder<string> =
        Decode.string
        |> Decode.andThen (fun s -> if System.Text.RegularExpressions.Regex.IsMatch (s, pattern) then Decode.succeed s else Decode.fail (sprintf "%s, not %s" what s))

    let private uri = matching "^[A-Za-z][A-Za-z0-9+.-]*:\\S*$" "a URI"

    let private enumOf (cases: (string * 'a) list) : Decoder<'a> =
        Decode.string
        |> Decode.andThen (fun s ->
            match cases |> List.tryFind (fun (name, _) -> name = s) with
            | Some (_, case) -> Decode.succeed case
            | None -> Decode.fail (sprintf "one of %s, not %s" (System.String.Join (", ", List.map fst cases)) s))

    let private nameOf (cases: (string * 'a) list) (case: 'a) : string =
        cases |> List.find (fun (_, c) -> c = case) |> fst

    let rec private jsonDecoder (path: string) (value: JsonValue) : Result<Json, DecoderError> =
        Decode.oneOf
            [ Decode.nil JNull
              Decode.bool |> Decode.map JBool
              number |> Decode.map JNumber
              Decode.string |> Decode.map JString
              Decode.list jsonDecoder |> Decode.map JArray
              Decode.keyValuePairs jsonDecoder |> Decode.map JObject ]
            path
            value

    let rec private encodeJson (json: Json) : JsonValue =
        match json with
        | JNull -> Encode.nil
        | JBool b -> Encode.bool b
        | JNumber n -> Encode.float n
        | JString s -> Encode.string s
        | JArray items -> Encode.list (List.map encodeJson items)
        | JObject fields -> Encode.object (fields |> List.map (fun (k, v) -> k, encodeJson v))

    /// Any JSON value.
    let json : Decoder<Json> = jsonDecoder

    let private returnTypes =
        [ "string", ReturnType.String
          "number", ReturnType.Number
          "boolean", ReturnType.Boolean
          "array", ReturnType.Array
          "object", ReturnType.Object
          "any", ReturnType.Any
          "void", ReturnType.Void ]

    /// What a function returns, as the catalog declares it.
    let returnTypeOf (f: Function) : ReturnType =
        match f with
        | Required _ | Regex _ | Length _ | Numeric _ | Email _ | And _ | Or _ | Not _ -> ReturnType.Boolean
        | FormatString _ | FormatNumber _ | FormatCurrency _ | FormatDate _ | Pluralize _ -> ReturnType.String
        | OpenUrl _ -> ReturnType.Void

    // --- dynamic values and function calls ---------------------------------------------------

    /// How a slot takes a literal: the type it holds, and whether a plain object is a literal
    /// there (only `required`'s argument and a literal object argument take one).
    type private Slot<'a> =
        { Type : ReturnType
          Literal : Decoder<'a>
          ObjectsAreLiteral : bool }

    let rec private functionCall (slot: ReturnType) : Decoder<FunctionCall> =
        only [ "call"; "args"; "returnType" ] (
            Decode.object (fun get ->
                get.Required.Field "call" Decode.string,
                get.Optional.Field "returnType" (enumOf returnTypes))
            |> Decode.andThen (fun (name, stated) ->
                Decode.field "args" (functionArgs name)
                |> Decode.andThen (fun f ->
                    let own = returnTypeOf f
                    match stated with
                    | Some t when t <> own -> Decode.fail (sprintf "%s returns %s, not %s" name (nameOf returnTypes own) (nameOf returnTypes t))
                    | _ when slot <> ReturnType.Any && slot <> own ->
                        Decode.fail (sprintf "%s returns %s, and this takes %s" name (nameOf returnTypes own) (nameOf returnTypes slot))
                    | _ -> Decode.succeed { Function = f; ReturnType = stated })))

    and private dynamic<'a> (slot: Slot<'a>) : Decoder<Dynamic<'a>> =
        let binding = only [ "path" ] (Decode.field "path" Decode.string) |> Decode.map Bound
        Decode.oneOf [ keysOf |> Decode.map Some; Decode.succeed None ]
        |> Decode.andThen (fun keys ->
            match keys with
            | Some keys when List.contains "call" keys -> functionCall slot.Type |> Decode.map Call
            | Some [ "path" ] -> binding
            | Some _ when slot.ObjectsAreLiteral -> slot.Literal |> Decode.map Literal
            | Some _ -> Decode.fail "a literal, a {\"path\"} binding or a function call"
            | None -> slot.Literal |> Decode.map Literal)

    // The slots below are functions of the decoder's own arguments, not values, so that the
    // recursion through `functionCall` is not a recursive VALUE (which F# checks at runtime).
    and private dString path value =
        dynamic { Type = ReturnType.String; Literal = Decode.string; ObjectsAreLiteral = false } path value

    and private dNumber path value =
        dynamic { Type = ReturnType.Number; Literal = number; ObjectsAreLiteral = false } path value

    and private dBool path value =
        dynamic { Type = ReturnType.Boolean; Literal = Decode.bool; ObjectsAreLiteral = false } path value

    /// The spec's DynamicValue: a string, number, boolean or array, a binding, or a call.
    and private dValue path value =
        dynamic
            { Type = ReturnType.Any
              Literal =
                json
                |> Decode.andThen (fun j ->
                    match j with
                    | JString _ | JNumber _ | JBool _ | JArray _ -> Decode.succeed j
                    | _ -> Decode.fail "a string, number, boolean or array")
              ObjectsAreLiteral = false }
            path
            value

    and private dAny path value =
        dynamic { Type = ReturnType.Any; Literal = json; ObjectsAreLiteral = true } path value

    and private functionArgs (name: string) : Decoder<Function> =
        let oneOrMore (allowed: string list) (d: Decoder<'a>) =
            only ("value" :: allowed) d
            |> Decode.andThen (fun a ->
                keysOf
                |> Decode.andThen (fun keys ->
                    if allowed |> List.exists (fun k -> List.contains k keys) then Decode.succeed a
                    else Decode.fail (sprintf "%s needs %s" name (System.String.Join (" or ", allowed)))))
        let atLeastTwo =
            Decode.list dBool
            |> Decode.andThen (fun vs -> if List.length vs < 2 then Decode.fail (sprintf "%s needs two values or more" name) else Decode.succeed vs)
        match name with
        | "required" -> only [ "value" ] (Decode.field "value" dAny) |> Decode.map Required
        | "regex" ->
            only [ "value"; "pattern" ] (Decode.object (fun get -> Regex (get.Required.Field "value" dString, get.Required.Field "pattern" Decode.string)))
        | "length" ->
            oneOrMore [ "min"; "max" ] (
                Decode.object (fun get ->
                    Length (get.Required.Field "value" dString, get.Optional.Field "min" count, get.Optional.Field "max" count)))
        | "numeric" ->
            oneOrMore [ "min"; "max" ] (
                Decode.object (fun get ->
                    Numeric (get.Required.Field "value" dNumber, get.Optional.Field "min" number, get.Optional.Field "max" number)))
        | "email" -> only [ "value" ] (Decode.field "value" dString) |> Decode.map Email
        | "formatString" -> only [ "value" ] (Decode.field "value" dString) |> Decode.map FormatString
        | "formatNumber" ->
            only [ "value"; "decimals"; "grouping" ] (
                Decode.object (fun get ->
                    FormatNumber (
                        get.Required.Field "value" dNumber,
                        get.Optional.Field "decimals" dNumber,
                        get.Optional.Field "grouping" dBool)))
        | "formatCurrency" ->
            only [ "value"; "currency"; "decimals"; "grouping" ] (
                Decode.object (fun get ->
                    FormatCurrency (
                        get.Required.Field "value" dNumber,
                        get.Required.Field "currency" dString,
                        get.Optional.Field "decimals" dNumber,
                        get.Optional.Field "grouping" dBool)))
        | "formatDate" ->
            only [ "value"; "format" ] (
                Decode.object (fun get -> FormatDate (get.Required.Field "value" dValue, get.Required.Field "format" dString)))
        | "pluralize" ->
            only [ "value"; "zero"; "one"; "two"; "few"; "many"; "other" ] (
                Decode.object (fun get ->
                    Pluralize (
                        get.Required.Field "value" dNumber,
                        { Zero = get.Optional.Field "zero" dString
                          One = get.Optional.Field "one" dString
                          Two = get.Optional.Field "two" dString
                          Few = get.Optional.Field "few" dString
                          Many = get.Optional.Field "many" dString
                          Other = get.Required.Field "other" dString })))
        | "openUrl" -> only [ "url" ] (Decode.field "url" uri) |> Decode.map OpenUrl
        | "and" -> only [ "values" ] (Decode.field "values" atLeastTwo) |> Decode.map And
        | "or" -> only [ "values" ] (Decode.field "values" atLeastTwo) |> Decode.map Or
        | "not" -> only [ "value" ] (Decode.field "value" dBool) |> Decode.map Not
        | other -> Decode.fail (sprintf "the basic catalog has no function %s" other)

    /// A call in a slot that takes any type: how a `${name(...)}` in a `formatString` template
    /// is read, once its arguments are written out as JSON.
    let callDecoder : Decoder<FunctionCall> = functionCall ReturnType.Any

    let private dStringList =
        dynamic { Type = ReturnType.Array; Literal = Decode.list Decode.string; ObjectsAreLiteral = false }

    let rec private encodeDynamic (literal: 'a -> JsonValue) (d: Dynamic<'a>) : JsonValue =
        match d with
        | Literal v -> literal v
        | Bound path -> Encode.object [ "path", Encode.string path ]
        | Call call -> encodeCall call

    and encodeCall (call: FunctionCall) : JsonValue =
        let str = encodeDynamic Encode.string
        let num = encodeDynamic Encode.float
        let boolean = encodeDynamic Encode.bool
        let opt name enc v = v |> Option.map (fun x -> name, enc x) |> Option.toList
        let name, args =
            match call.Function with
            | Required v -> "required", [ "value", encodeDynamic encodeJson v ]
            | Regex (v, p) -> "regex", [ "value", str v; "pattern", Encode.string p ]
            | Length (v, min, max) -> "length", [ "value", str v ] @ opt "min" Encode.int min @ opt "max" Encode.int max
            | Numeric (v, min, max) -> "numeric", [ "value", num v ] @ opt "min" Encode.float min @ opt "max" Encode.float max
            | Email v -> "email", [ "value", str v ]
            | FormatString v -> "formatString", [ "value", str v ]
            | FormatNumber (v, d, g) -> "formatNumber", [ "value", num v ] @ opt "decimals" num d @ opt "grouping" boolean g
            | FormatCurrency (v, c, d, g) ->
                "formatCurrency", [ "value", num v; "currency", str c ] @ opt "decimals" num d @ opt "grouping" boolean g
            | FormatDate (v, f) -> "formatDate", [ "value", encodeDynamic encodeJson v; "format", str f ]
            | Pluralize (v, forms) ->
                "pluralize",
                [ "value", num v ]
                @ opt "zero" str forms.Zero
                @ opt "one" str forms.One
                @ opt "two" str forms.Two
                @ opt "few" str forms.Few
                @ opt "many" str forms.Many
                @ [ "other", str forms.Other ]
            | OpenUrl url -> "openUrl", [ "url", Encode.string url ]
            | And vs -> "and", [ "values", Encode.list (List.map boolean vs) ]
            | Or vs -> "or", [ "values", Encode.list (List.map boolean vs) ]
            | Not v -> "not", [ "value", boolean v ]
        Encode.object (
            [ "call", Encode.string name; "args", Encode.object args ]
            @ opt "returnType" (nameOf returnTypes >> Encode.string) call.ReturnType)

    // --- components --------------------------------------------------------------------------

    let private textVariants =
        [ "h1", TextVariant.H1; "h2", TextVariant.H2; "h3", TextVariant.H3; "h4", TextVariant.H4
          "h5", TextVariant.H5; "caption", TextVariant.Caption; "body", TextVariant.Body ]

    let private imageFits =
        [ "contain", ImageFit.Contain; "cover", ImageFit.Cover; "fill", ImageFit.Fill
          "none", ImageFit.NoScale; "scaleDown", ImageFit.ScaleDown ]

    let private imageVariants =
        [ "icon", ImageVariant.Icon; "avatar", ImageVariant.Avatar; "smallFeature", ImageVariant.SmallFeature
          "mediumFeature", ImageVariant.MediumFeature; "largeFeature", ImageVariant.LargeFeature
          "header", ImageVariant.Header ]

    let private justifies =
        [ "start", Justify.Start; "center", Justify.Center; "end", Justify.End
          "spaceBetween", Justify.SpaceBetween; "spaceAround", Justify.SpaceAround
          "spaceEvenly", Justify.SpaceEvenly; "stretch", Justify.Stretch ]

    let private aligns = [ "start", Align.Start; "center", Align.Center; "end", Align.End; "stretch", Align.Stretch ]

    let private directions = [ "vertical", Direction.Vertical; "horizontal", Direction.Horizontal ]

    let private buttonVariants =
        [ "default", ButtonVariant.Default; "primary", ButtonVariant.Primary; "borderless", ButtonVariant.Borderless ]

    let private textFieldVariants =
        [ "longText", TextFieldVariant.LongText; "number", TextFieldVariant.Number
          "shortText", TextFieldVariant.ShortText; "obscured", TextFieldVariant.Obscured ]

    let private choiceVariants =
        [ "multipleSelection", ChoiceVariant.MultipleSelection; "mutuallyExclusive", ChoiceVariant.MutuallyExclusive ]

    let private choiceDisplays = [ "checkbox", ChoiceDisplay.Checkbox; "chips", ChoiceDisplay.Chips ]

    /// The catalog's icon names.
    let iconNames =
        [ "accountCircle"; "add"; "arrowBack"; "arrowForward"; "attachFile"; "calendarToday"; "call"; "camera"
          "check"; "close"; "delete"; "download"; "edit"; "event"; "error"; "fastForward"; "favorite"
          "favoriteOff"; "folder"; "help"; "home"; "info"; "locationOn"; "lock"; "lockOpen"; "mail"; "menu"
          "moreVert"; "moreHoriz"; "notificationsOff"; "notifications"; "pause"; "payment"; "person"; "phone"
          "photo"; "play"; "print"; "refresh"; "rewind"; "search"; "send"; "settings"; "share"; "shoppingCart"
          "skipNext"; "skipPrevious"; "star"; "starHalf"; "starOff"; "stop"; "upload"; "visibility"
          "visibilityOff"; "volumeDown"; "volumeMute"; "volumeOff"; "volumeUp"; "warning" ]

    let private accessibility : Decoder<Accessibility> =
        only [ "label"; "description" ] (
            Decode.object (fun get ->
                { Label = get.Optional.Field "label" dString
                  Description = get.Optional.Field "description" dString }))

    let private checkRule : Decoder<CheckRule> =
        only [ "condition"; "message" ] (
            Decode.object (fun get ->
                { Condition = get.Required.Field "condition" dBool
                  Message = get.Required.Field "message" Decode.string }))

    let private childList : Decoder<ChildList> =
        Decode.oneOf
            [ Decode.list Decode.string |> Decode.map Static
              only [ "componentId"; "path" ] (
                  Decode.object (fun get ->
                      Template (get.Required.Field "componentId" Decode.string, get.Required.Field "path" Decode.string))) ]

    let private action : Decoder<Action> =
        Decode.oneOf
            [ only [ "event" ] (
                  Decode.field
                      "event"
                      (only [ "name"; "context" ] (
                          Decode.object (fun get ->
                              Action.Event (
                                  get.Required.Field "name" Decode.string,
                                  get.Optional.Field "context" (Decode.keyValuePairs dValue) |> Option.defaultValue [])))))
              only [ "functionCall" ] (Decode.field "functionCall" (functionCall ReturnType.Any)) |> Decode.map Action.LocalCall ]

    let private iconSource : Decoder<IconSource> =
        Decode.oneOf
            [ enumOf (iconNames |> List.map (fun n -> n, IconSource.Named n))
              only [ "svgPath" ] (Decode.field "svgPath" Decode.string) |> Decode.map IconSource.Svg
              only [ "path" ] (Decode.field "path" Decode.string) |> Decode.map IconSource.Bound ]

    /// A date, a time or a date-time, when the value is a literal.
    let private dateLiteral : Decoder<Dynamic<string>> =
        let date = "\\d{4}-\\d{2}-\\d{2}"
        let time = "\\d{2}:\\d{2}:\\d{2}(\\.\\d+)?(Z|[+-]\\d{2}:\\d{2})?"
        dString
        |> Decode.andThen (fun d ->
            match d with
            | Literal s when not (System.Text.RegularExpressions.Regex.IsMatch (s, sprintf "^(%s|%s|%s[Tt]%s)$" date time date time)) ->
                Decode.fail (sprintf "a date, a time or a date-time, not %s" s)
            | _ -> Decode.succeed d)

    let private tab : Decoder<Tab> =
        only [ "title"; "child" ] (
            Decode.object (fun get -> { Title = get.Required.Field "title" dString; Child = get.Required.Field "child" Decode.string }))

    let private choiceOption : Decoder<ChoiceOption> =
        only [ "label"; "value" ] (
            Decode.object (fun get -> { Label = get.Required.Field "label" dString; Value = get.Required.Field "value" Decode.string }))

    let private kind (name: string) : (string list * Decoder<ComponentKind>) option =
        let checks (get: Decode.IGetters) = get.Optional.Field "checks" (Decode.list checkRule) |> Option.defaultValue []
        let container case (third: string) =
            Decode.object (fun get ->
                case (get.Required.Field "children" childList) (get.Optional.Field "justify" (enumOf justifies)) (get.Optional.Field third (enumOf aligns)))
        match name with
        | "Text" ->
            Some (
                [ "text"; "variant" ],
                Decode.object (fun get ->
                    ComponentKind.Text (get.Required.Field "text" dString, get.Optional.Field "variant" (enumOf textVariants))))
        | "Image" ->
            Some (
                [ "url"; "description"; "fit"; "variant" ],
                Decode.object (fun get ->
                    ComponentKind.Image (
                        get.Required.Field "url" dString,
                        get.Optional.Field "description" dString,
                        get.Optional.Field "fit" (enumOf imageFits),
                        get.Optional.Field "variant" (enumOf imageVariants))))
        | "Icon" -> Some ([ "name" ], Decode.field "name" iconSource |> Decode.map ComponentKind.Icon)
        | "Video" -> Some ([ "url" ], Decode.field "url" dString |> Decode.map ComponentKind.Video)
        | "AudioPlayer" ->
            Some (
                [ "url"; "description" ],
                Decode.object (fun get ->
                    ComponentKind.AudioPlayer (get.Required.Field "url" dString, get.Optional.Field "description" dString)))
        | "Row" ->
            Some ([ "children"; "justify"; "align" ], container (fun c j a -> ComponentKind.Row (c, j, a)) "align")
        | "Column" ->
            Some ([ "children"; "justify"; "align" ], container (fun c j a -> ComponentKind.Column (c, j, a)) "align")
        | "List" ->
            Some (
                [ "children"; "direction"; "align" ],
                Decode.object (fun get ->
                    ComponentKind.List (
                        get.Required.Field "children" childList,
                        get.Optional.Field "direction" (enumOf directions),
                        get.Optional.Field "align" (enumOf aligns))))
        | "Card" -> Some ([ "child" ], Decode.field "child" Decode.string |> Decode.map ComponentKind.Card)
        | "Tabs" ->
            Some (
                [ "tabs" ],
                Decode.field "tabs" (Decode.list tab)
                |> Decode.andThen (fun tabs -> if List.isEmpty tabs then Decode.fail "Tabs needs a tab" else Decode.succeed (ComponentKind.Tabs tabs)))
        | "Modal" ->
            Some (
                [ "trigger"; "content" ],
                Decode.object (fun get ->
                    ComponentKind.Modal (get.Required.Field "trigger" Decode.string, get.Required.Field "content" Decode.string)))
        | "Divider" -> Some ([ "axis" ], Decode.object (fun get -> ComponentKind.Divider (get.Optional.Field "axis" (enumOf directions))))
        | "Button" ->
            Some (
                [ "child"; "variant"; "action"; "checks" ],
                Decode.object (fun get ->
                    ComponentKind.Button (
                        get.Required.Field "child" Decode.string,
                        get.Optional.Field "variant" (enumOf buttonVariants),
                        get.Required.Field "action" action,
                        checks get)))
        | "TextField" ->
            Some (
                [ "label"; "value"; "variant"; "validationRegexp"; "checks" ],
                Decode.object (fun get ->
                    ComponentKind.TextField (
                        get.Required.Field "label" dString,
                        get.Optional.Field "value" dString,
                        get.Optional.Field "variant" (enumOf textFieldVariants),
                        get.Optional.Field "validationRegexp" Decode.string,
                        checks get)))
        | "CheckBox" ->
            Some (
                [ "label"; "value"; "checks" ],
                Decode.object (fun get ->
                    ComponentKind.CheckBox (get.Required.Field "label" dString, get.Required.Field "value" dBool, checks get)))
        | "ChoicePicker" ->
            Some (
                [ "label"; "variant"; "options"; "value"; "displayStyle"; "filterable"; "checks" ],
                Decode.object (fun get ->
                    ComponentKind.ChoicePicker (
                        get.Optional.Field "label" dString,
                        get.Optional.Field "variant" (enumOf choiceVariants),
                        get.Required.Field "options" (Decode.list choiceOption),
                        get.Required.Field "value" dStringList,
                        get.Optional.Field "displayStyle" (enumOf choiceDisplays),
                        get.Optional.Field "filterable" Decode.bool,
                        checks get)))
        | "Slider" ->
            Some (
                [ "label"; "min"; "max"; "value"; "checks" ],
                Decode.object (fun get ->
                    ComponentKind.Slider (
                        get.Optional.Field "label" dString,
                        get.Optional.Field "min" number,
                        get.Required.Field "max" number,
                        get.Required.Field "value" dNumber,
                        checks get)))
        | "DateTimeInput" ->
            Some (
                [ "value"; "enableDate"; "enableTime"; "min"; "max"; "label"; "checks" ],
                Decode.object (fun get ->
                    ComponentKind.DateTimeInput (
                        get.Required.Field "value" dString,
                        get.Optional.Field "enableDate" Decode.bool,
                        get.Optional.Field "enableTime" Decode.bool,
                        get.Optional.Field "min" dateLiteral,
                        get.Optional.Field "max" dateLiteral,
                        get.Optional.Field "label" dString,
                        checks get)))
        | _ -> None

    let componentDecoder : Decoder<Component> =
        Decode.field "component" Decode.string
        |> Decode.andThen (fun name ->
            match kind name with
            | None -> Decode.fail (sprintf "the basic catalog has no component %s" name)
            | Some (props, decodeKind) ->
                only ([ "id"; "component"; "accessibility"; "weight" ] @ props) (
                    Decode.object (fun get ->
                        get.Required.Field "id" Decode.string,
                        get.Optional.Field "accessibility" accessibility,
                        get.Optional.Field "weight" number)
                    |> Decode.andThen (fun (id, a11y, weight) ->
                        decodeKind |> Decode.map (fun k -> { Id = id; Accessibility = a11y; Weight = weight; Kind = k }))))

    let encodeComponent (c: Component) : JsonValue =
        let str = encodeDynamic Encode.string
        let opt name enc v = v |> Option.map (fun x -> name, enc x) |> Option.toList
        let enum cases name v = opt name (nameOf cases >> Encode.string) v
        let checks (rules: CheckRule list) =
            if List.isEmpty rules then []
            else
                [ "checks",
                  rules
                  |> List.map (fun r -> Encode.object [ "condition", encodeDynamic Encode.bool r.Condition; "message", Encode.string r.Message ])
                  |> Encode.list ]
        let children (cl: ChildList) =
            match cl with
            | Static ids -> Encode.list (List.map Encode.string ids)
            | Template (id, path) -> Encode.object [ "componentId", Encode.string id; "path", Encode.string path ]
        let encodeAction (a: Action) =
            match a with
            | Action.Event (name, context) ->
                Encode.object
                    [ "event",
                      Encode.object (
                          [ "name", Encode.string name ]
                          @ (if List.isEmpty context then []
                             else [ "context", Encode.object (context |> List.map (fun (k, v) -> k, encodeDynamic encodeJson v)) ])) ]
            | Action.LocalCall call -> Encode.object [ "functionCall", encodeCall call ]
        let name, props =
            match c.Kind with
            | ComponentKind.Text (text, variant) -> "Text", [ "text", str text ] @ enum textVariants "variant" variant
            | ComponentKind.Image (url, description, fit, variant) ->
                "Image", [ "url", str url ] @ opt "description" str description @ enum imageFits "fit" fit @ enum imageVariants "variant" variant
            | ComponentKind.Icon source ->
                "Icon",
                [ "name",
                  match source with
                  | IconSource.Named n -> Encode.string n
                  | IconSource.Svg p -> Encode.object [ "svgPath", Encode.string p ]
                  | IconSource.Bound p -> Encode.object [ "path", Encode.string p ] ]
            | ComponentKind.Video url -> "Video", [ "url", str url ]
            | ComponentKind.AudioPlayer (url, description) -> "AudioPlayer", [ "url", str url ] @ opt "description" str description
            | ComponentKind.Row (cl, j, a) -> "Row", [ "children", children cl ] @ enum justifies "justify" j @ enum aligns "align" a
            | ComponentKind.Column (cl, j, a) -> "Column", [ "children", children cl ] @ enum justifies "justify" j @ enum aligns "align" a
            | ComponentKind.List (cl, d, a) -> "List", [ "children", children cl ] @ enum directions "direction" d @ enum aligns "align" a
            | ComponentKind.Card child -> "Card", [ "child", Encode.string child ]
            | ComponentKind.Tabs tabs ->
                "Tabs", [ "tabs", tabs |> List.map (fun t -> Encode.object [ "title", str t.Title; "child", Encode.string t.Child ]) |> Encode.list ]
            | ComponentKind.Modal (trigger, content) -> "Modal", [ "trigger", Encode.string trigger; "content", Encode.string content ]
            | ComponentKind.Divider axis -> "Divider", enum directions "axis" axis
            | ComponentKind.Button (child, variant, a, rules) ->
                "Button", [ "child", Encode.string child ] @ enum buttonVariants "variant" variant @ [ "action", encodeAction a ] @ checks rules
            | ComponentKind.TextField (label, value, variant, regexp, rules) ->
                "TextField",
                [ "label", str label ]
                @ opt "value" str value
                @ enum textFieldVariants "variant" variant
                @ opt "validationRegexp" Encode.string regexp
                @ checks rules
            | ComponentKind.CheckBox (label, value, rules) ->
                "CheckBox", [ "label", str label; "value", encodeDynamic Encode.bool value ] @ checks rules
            | ComponentKind.ChoicePicker (label, variant, options, value, display, filterable, rules) ->
                "ChoicePicker",
                opt "label" str label
                @ enum choiceVariants "variant" variant
                @ [ "options", options |> List.map (fun o -> Encode.object [ "label", str o.Label; "value", Encode.string o.Value ]) |> Encode.list
                    "value", encodeDynamic (List.map Encode.string >> Encode.list) value ]
                @ enum choiceDisplays "displayStyle" display
                @ opt "filterable" Encode.bool filterable
                @ checks rules
            | ComponentKind.Slider (label, min, max, value, rules) ->
                "Slider",
                opt "label" str label
                @ opt "min" Encode.float min
                @ [ "max", Encode.float max; "value", encodeDynamic Encode.float value ]
                @ checks rules
            | ComponentKind.DateTimeInput (value, enableDate, enableTime, min, max, label, rules) ->
                "DateTimeInput",
                [ "value", str value ]
                @ opt "enableDate" Encode.bool enableDate
                @ opt "enableTime" Encode.bool enableTime
                @ opt "min" str min
                @ opt "max" str max
                @ opt "label" str label
                @ checks rules
        Encode.object (
            [ "id", Encode.string c.Id; "component", Encode.string name ]
            @ (c.Accessibility
               |> Option.map (fun a -> "accessibility", Encode.object (opt "label" str a.Label @ opt "description" str a.Description))
               |> Option.toList)
            @ opt "weight" Encode.float c.Weight
            @ props)

    // --- messages ----------------------------------------------------------------------------

    let private version : Decoder<unit> =
        Decode.field "version" (enumOf [ "v0.9", (); "v0.9.1", () ])

    /// One message of a stream, `version` and the one property that says which.
    let private envelope (cases: (string * Decoder<'a>) list) : Decoder<'a> =
        keysOf
        |> Decode.andThen (fun keys ->
            match keys |> List.filter ((<>) "version") with
            | [ key ] when List.length keys = 2 ->
                match cases |> List.tryFind (fun (name, _) -> name = key) with
                | Some (_, d) -> version |> Decode.andThen (fun () -> Decode.field key d)
                | None -> Decode.fail (sprintf "no message is called %s" key)
            | _ -> Decode.fail "a message is an object of `version` and one message")

    let private theme : Decoder<Theme> =
        Decode.keyValuePairs json
        |> Decode.andThen (fun fields ->
            Decode.object (fun get ->
                { PrimaryColor = get.Optional.Field "primaryColor" (matching "^#[0-9a-fA-F]{6}$" "a #rrggbb colour")
                  IconUrl = get.Optional.Field "iconUrl" uri
                  AgentDisplayName = get.Optional.Field "agentDisplayName" Decode.string
                  Other =
                    fields
                    |> List.filter (fun (k, _) -> not (List.contains k [ "primaryColor"; "iconUrl"; "agentDisplayName" ])) }))

    let serverToClientDecoder : Decoder<ServerToClient> =
        envelope
            [ "createSurface",
              only [ "surfaceId"; "catalogId"; "theme"; "sendDataModel" ] (
                  Decode.object (fun get ->
                      CreateSurface (
                          get.Required.Field "surfaceId" Decode.string,
                          get.Required.Field "catalogId" Decode.string,
                          get.Optional.Field "theme" theme,
                          get.Optional.Field "sendDataModel" Decode.bool)))
              "updateComponents",
              only [ "surfaceId"; "components" ] (
                  Decode.object (fun get -> get.Required.Field "surfaceId" Decode.string, get.Required.Field "components" (Decode.list componentDecoder))
                  |> Decode.andThen (fun (id, components) ->
                      if List.isEmpty components then Decode.fail "updateComponents needs a component"
                      else Decode.succeed (UpdateComponents (id, components))))
              "updateDataModel",
              only [ "surfaceId"; "path"; "value" ] (
                  Decode.map3
                      (fun id path value -> UpdateDataModel (id, path, value))
                      (Decode.field "surfaceId" Decode.string)
                      (Decode.object (fun get -> get.Optional.Field "path" Decode.string))
                      // Present-and-null is a value (the key becomes null); absent removes it.
                      (has "value" |> Decode.andThen (fun present -> if present then Decode.field "value" json |> Decode.map Some else Decode.succeed None)))
              "deleteSurface", only [ "surfaceId" ] (Decode.field "surfaceId" Decode.string) |> Decode.map DeleteSurface ]

    let encodeServerToClient (message: ServerToClient) : JsonValue =
        let opt name enc v = v |> Option.map (fun x -> name, enc x) |> Option.toList
        let key, body =
            match message with
            | CreateSurface (id, catalog, theme, send) ->
                "createSurface",
                [ "surfaceId", Encode.string id; "catalogId", Encode.string catalog ]
                @ opt
                    "theme"
                    (fun (t: Theme) ->
                        Encode.object (
                            opt "primaryColor" Encode.string t.PrimaryColor
                            @ opt "iconUrl" Encode.string t.IconUrl
                            @ opt "agentDisplayName" Encode.string t.AgentDisplayName
                            @ (t.Other |> List.map (fun (k, v) -> k, encodeJson v))))
                    theme
                @ opt "sendDataModel" Encode.bool send
            | UpdateComponents (id, components) ->
                "updateComponents", [ "surfaceId", Encode.string id; "components", Encode.list (List.map encodeComponent components) ]
            | UpdateDataModel (id, path, value) ->
                "updateDataModel", [ "surfaceId", Encode.string id ] @ opt "path" Encode.string path @ opt "value" encodeJson value
            | DeleteSurface id -> "deleteSurface", [ "surfaceId", Encode.string id ]
        Encode.object [ "version", Encode.string Version; key, Encode.object body ]

    let private timestamp : Decoder<System.DateTimeOffset> =
        Decode.string
        |> Decode.andThen (fun s ->
            match System.DateTimeOffset.TryParse (s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) with
            | true, t -> Decode.succeed t
            | false, _ -> Decode.fail (sprintf "an ISO 8601 date-time, not %s" s))

    let private validationFailed : Decoder<ClientError> =
        only [ "code"; "surfaceId"; "path"; "message" ] (
            Decode.object (fun get ->
                ValidationFailed (
                    get.Required.Field "surfaceId" Decode.string,
                    get.Required.Field "path" Decode.string,
                    get.Required.Field "message" Decode.string)))

    let private genericError (code: string) : Decoder<ClientError> =
        let known = [ "code"; "surfaceId"; "message" ]
        Decode.keyValuePairs json
        |> Decode.andThen (fun fields ->
            let other = fields |> List.filter (fun (k, _) -> not (List.contains k known))
            Decode.object (fun get ->
                Generic (code, get.Required.Field "surfaceId" Decode.string, get.Required.Field "message" Decode.string, other)))

    let private clientError : Decoder<ClientError> =
        Decode.field "code" Decode.string
        |> Decode.andThen (fun code -> if code = "VALIDATION_FAILED" then validationFailed else genericError code)

    let private actionTaken : Decoder<ClientToServer> =
        Decode.object (fun get ->
            ActionTaken
                { Name = get.Required.Field "name" Decode.string
                  SurfaceId = get.Required.Field "surfaceId" Decode.string
                  SourceComponentId = get.Required.Field "sourceComponentId" Decode.string
                  Timestamp = get.Required.Field "timestamp" timestamp
                  Context = get.Required.Field "context" (Decode.keyValuePairs json) })

    let clientToServerDecoder : Decoder<ClientToServer> =
        envelope [ "action", actionTaken; "error", Decode.map ErrorReported clientError ]

    let encodeClientToServer (message: ClientToServer) : JsonValue =
        let key, body =
            match message with
            | ActionTaken a ->
                "action",
                Encode.object
                    [ "name", Encode.string a.Name
                      "surfaceId", Encode.string a.SurfaceId
                      "sourceComponentId", Encode.string a.SourceComponentId
                      "timestamp", Encode.string (a.Timestamp.ToString ("o", CultureInfo.InvariantCulture))
                      "context", Encode.object (a.Context |> List.map (fun (k, v) -> k, encodeJson v)) ]
            | ErrorReported (ValidationFailed (id, path, message)) ->
                "error",
                Encode.object
                    [ "code", Encode.string "VALIDATION_FAILED"
                      "surfaceId", Encode.string id
                      "path", Encode.string path
                      "message", Encode.string message ]
            | ErrorReported (Generic (code, id, message, other)) ->
                "error",
                Encode.object (
                    [ "code", Encode.string code; "surfaceId", Encode.string id; "message", Encode.string message ]
                    @ (other |> List.map (fun (k, v) -> k, encodeJson v)))
        Encode.object [ "version", Encode.string Version; key, body ]

    let encodeJsonValue : Json -> JsonValue = encodeJson

    /// Decode a JSON text, answering a decoder that raised as an `Error`, never letting it out.
    let fromString (decoder: Decoder<'a>) (text: string) : Result<'a, string> =
        try
            Decode.fromString decoder text
        with e ->
            Error (sprintf "the decoder raised rather than refused: %s" e.Message)

    let toString (encode: 'a -> JsonValue) (value: 'a) : string = encode value |> Encode.toString 0
