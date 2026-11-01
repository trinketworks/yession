namespace A2ui

/// A surface said as plain text, for a reader that is not a screen: a transcript line, a
/// prompt. What a person answered reads as what they saw (an option's label, not its value)
/// and never as JSON.
[<RequireQualifiedAccess>]
module Project =

    /// A string property as text, or nothing while it is blank or cannot be read.
    let private said (scope: Scope) (d: Dynamic<string>) : string option =
        match Evaluate.textOf scope d with
        | Ok text when text.Trim () <> "" -> Some (text.Trim ())
        | Ok _
        | Error _ -> None

    /// A value as the answers it holds: each item of a list, a value on its own, nothing for
    /// nothing. An object is the one shape with no plainer spelling, so it stays JSON.
    let rec answers (value: Json) : string list =
        match value with
        | JNull -> []
        | JArray items -> items |> List.collect answers
        | JString s when s.Trim () = "" -> []
        | other -> [ Evaluate.text (Some other) ]

    let private label (scope: Scope) (options: ChoiceOption list) (value: string) : string =
        match options |> List.tryFind (fun o -> o.Value = value) |> Option.bind (fun o -> said scope o.Label) with
        | Some text -> text
        | None -> value

    let private choices (scope: Scope) (options: ChoiceOption list) (value: Dynamic<string list>) : string list =
        match Evaluate.selectionOf scope value with
        | Ok chosen -> chosen |> List.map (label scope options)
        | Error _ -> []

    let private field (scope: Scope) (title: Dynamic<string>) (value: Dynamic<string>) : string list =
        match said scope value, said scope title with
        | None, _ -> []
        | Some text, Some name -> [ sprintf "%s: %s" name text ]
        | Some text, None -> [ text ]

    let private ticked (scope: Scope) (title: Dynamic<string>) (value: Dynamic<bool>) : string list =
        match Evaluate.conditionOf scope value, said scope title with
        | Ok true, Some name -> [ name ]
        | Ok true, None
        | Ok false, _
        | Error _, _ -> []

    /// What the input bound to `pointer` answers, as the person saw it: a choice's labels, a
    /// field's label and text, a ticked box's label. None when no input binds it.
    let private input (surface: Surface) (scope: Scope) (pointer: string) : string list option =
        let binds (path: string) = Pointer.resolve scope.Base path = pointer
        surface.Components
        |> Map.toList
        |> List.tryPick (fun (_, c) ->
            match c.Kind with
            | ComponentKind.ChoicePicker (_, _, options, (Bound path as value), _, _, _) when binds path ->
                Some (choices scope options value)
            | ComponentKind.TextField (title, Some (Bound path as value), _, _, _) when binds path ->
                Some (field scope title value)
            | ComponentKind.CheckBox (title, (Bound path as value), _) when binds path -> Some (ticked scope title value)
            | _ -> None)

    /// The text on a button: its child, when that is a Text.
    let private caption (surface: Surface) (scope: Scope) (child: string) : string option =
        match Map.tryFind child surface.Components with
        | Some { Kind = ComponentKind.Text (text, _) } -> said scope text
        | Some _
        | None -> None

    /// One context value as answers: what the input bound there shows, or the value itself
    /// when no input holds it.
    let private answered (surface: Surface) (at: Scope) (d: Dynamic<Json>) : string list =
        match d with
        | Literal _
        | Call _ -> []
        | Bound path ->
            match input surface at (Pointer.resolve at.Base path) with
            | Some said -> said
            | None -> Evaluate.read at path |> Option.map answers |> Option.defaultValue []

    /// What a surface is about, as a person read it: its first line of text in drawing order
    /// from `root`, not counting a button's caption, which names an answer rather than the
    /// question. None for a surface that says nothing.
    let title (surfaces: Map<string, Surface>) (surfaceId: string) : string option =
        match Map.tryFind surfaceId surfaces with
        | None -> None
        | Some surface ->
            let rec first (seen: Set<string>) (id: string) (at: Scope) : string option =
                match Map.tryFind id surface.Components with
                | Some c when not (Set.contains id seen) ->
                    let seen = Set.add id seen
                    let within list = Evaluate.children at list |> List.tryPick (fun (child, scope) -> first seen child scope)
                    match c.Kind with
                    | ComponentKind.Text (text, _) -> said at text
                    | ComponentKind.Card child -> first seen child at
                    | ComponentKind.Row (list, _, _)
                    | ComponentKind.Column (list, _, _)
                    | ComponentKind.List (list, _, _) -> within list
                    | _ -> None
                | Some _
                | None -> None
            first Set.empty "root" (Evaluate.root surface.DataModel)

    /// What pressing button `componentId` answered, as a person reads it: one line per answer.
    /// An answer is a value the button's context reads from the data model, said the way the
    /// input that holds it showed it. A context value written into the button itself is the
    /// agent's, not the person's, so it is not an answer. A button that carries no answer
    /// answers with its own caption ("Not now").
    let responses (surfaces: Map<string, Surface>) (surfaceId: string) (componentId: string) (scope: string) : string list =
        match Map.tryFind surfaceId surfaces with
        | None -> []
        | Some surface ->
            let at = { Model = surface.DataModel; Base = scope }
            match Map.tryFind componentId surface.Components with
            | Some { Kind = ComponentKind.Button (child, _, Action.Event (_, context), _) } ->
                let read = context |> List.collect (fun (_, d) -> answered surface at d)
                match read, caption surface at child with
                | [], Some text -> [ text ]
                | read, _ -> read
            | Some _
            | None -> []
