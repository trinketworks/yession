namespace Yession.App

open A2ui
open Lit

/// An agent's A2UI surface, drawn from the synced state (`SyncedSessionState.Surfaces`).
///
/// The agent says what is on the surface in the basic catalog's words; this module says how
/// each word looks, from `Style`'s vocabulary, so a surface wears this product and nothing of
/// the agent's choosing. It draws a subset of the catalog: text, the containers, a card, a
/// divider, a button and the three inputs. Anything else is SAID rather than dropped
/// (`Dom.Hooks.surfaceUnsupported`), because a question with a hole in it is worse than one
/// that admits the hole.
///
/// An input writes its answer into the shared data model (`EditSurfaceMsg`), which is the
/// spec's two-way binding with everybody in the session on the other end of it. A button
/// shows whether its checks pass, and a ready one asks the session to record its answer
/// (`PressSurfaceMsg`), which wakes the agent.
[<RequireQualifiedAccess>]
module SurfaceView =

    /// The catalog's name for a component, as the agent wrote it.
    let kindName (kind: ComponentKind) : string =
        match kind with
        | ComponentKind.Text _ -> "Text"
        | ComponentKind.Image _ -> "Image"
        | ComponentKind.Icon _ -> "Icon"
        | ComponentKind.Video _ -> "Video"
        | ComponentKind.AudioPlayer _ -> "AudioPlayer"
        | ComponentKind.Row _ -> "Row"
        | ComponentKind.Column _ -> "Column"
        | ComponentKind.List _ -> "List"
        | ComponentKind.Card _ -> "Card"
        | ComponentKind.Tabs _ -> "Tabs"
        | ComponentKind.Modal _ -> "Modal"
        | ComponentKind.Divider _ -> "Divider"
        | ComponentKind.Button _ -> "Button"
        | ComponentKind.TextField _ -> "TextField"
        | ComponentKind.CheckBox _ -> "CheckBox"
        | ComponentKind.ChoicePicker _ -> "ChoicePicker"
        | ComponentKind.Slider _ -> "Slider"
        | ComponentKind.DateTimeInput _ -> "DateTimeInput"

    /// Whether this module draws a component, rather than saying it cannot. What the agent's
    /// tool refuses by (`SurfaceTool`), so the agent is never told a component works that
    /// people would see as a hole.
    let draws (kind: ComponentKind) : bool =
        match kind with
        | ComponentKind.Text _
        | ComponentKind.Row _
        | ComponentKind.Column _
        | ComponentKind.List _
        | ComponentKind.Card _
        | ComponentKind.Divider _
        | ComponentKind.Button _
        | ComponentKind.TextField _
        | ComponentKind.CheckBox _
        | ComponentKind.ChoicePicker _ -> true
        | ComponentKind.Image _
        | ComponentKind.Icon _
        | ComponentKind.Video _
        | ComponentKind.AudioPlayer _
        | ComponentKind.Tabs _
        | ComponentKind.Modal _
        | ComponentKind.Slider _
        | ComponentKind.DateTimeInput _ -> false

    /// What one surface's components draw against.
    type private Canvas =
        { SurfaceId : string
          Components : Map<string, Component>
          Dispatch : ClientMsg -> unit }

    /// The absolute pointer a binding writes to, read in `scope`; `None` for a literal, which
    /// has nowhere in the data model to write.
    let private boundAt (scope: Scope) (value: Dynamic<'a>) : string option =
        match value with
        | Bound path -> Some (Pointer.resolve scope.Base path)
        | Literal _
        | Call _ -> None

    /// A string property as text. An error is said in its place, so a surface that asked for
    /// something this renderer cannot do shows why rather than a blank.
    let private textIn (scope: Scope) (value: Dynamic<string>) : string =
        match Evaluate.textOf scope value with
        | Ok text -> text
        | Error reason -> reason

    /// The messages of the checks that fail; a check that cannot be evaluated fails with its
    /// reason, because a rule nobody can run is not a rule that passed.
    let private failing (scope: Scope) (checks: CheckRule list) : string list =
        match Evaluate.failing scope checks with
        | Ok messages -> messages
        | Error reason -> [ reason ]

    let private reasons (messages: string list) : TemplateResult list =
        messages |> List.map (fun message -> html $"""<p class="{Style.surfaceReason}">{message}</p>""")

    /// Whether `text` matches the field's `validationRegexp`. A pattern that does not compile
    /// matches nothing, so the field says it cannot be checked rather than the page failing.
    let private matches (pattern: string) (text: string) : bool =
        try
            System.Text.RegularExpressions.Regex.IsMatch (text, pattern)
        // Any exception: .NET throws `ArgumentException`, a browser's `RegExp` a `SyntaxError`.
        with _ ->
            false

    let private weight (c: Component) : string =
        c.Weight |> Option.map (sprintf "flex-grow:%g") |> Option.defaultValue ""

    let rec private draw (canvas: Canvas) (ancestors: Set<string>) (id: string) (scope: Scope) : TemplateResult =
        // A component that has not arrived is progressive rendering, and one that contains
        // itself would never finish drawing: both draw nothing.
        if Set.contains id ancestors then Lit.nothing
        else
            match Map.tryFind id canvas.Components with
            | Some c -> drawComponent canvas (Set.add id ancestors) c scope
            | None -> Lit.nothing

    and private children (canvas: Canvas) (ancestors: Set<string>) (scope: Scope) (list: ChildList) : TemplateResult list =
        Evaluate.children scope list |> List.map (fun (id, at) -> draw canvas ancestors id at)

    and private container (canvas: Canvas) ancestors (c: Component) scope (face: string) list justify align : TemplateResult =
        let face =
            Style.cls
                [ face
                  justify |> Option.map Style.surfaceJustify |> Option.defaultValue ""
                  align |> Option.map Style.surfaceAlign |> Option.defaultValue "" ]
        html $"""
            <div class="{face}" style="{weight c}" data-surface-component="{c.Id}">{children canvas ancestors scope list}</div>"""

    and private drawComponent (canvas: Canvas) (ancestors: Set<string>) (c: Component) (scope: Scope) : TemplateResult =
        let edit (path: string) (value: Json) = canvas.Dispatch (EditSurfaceMsg (canvas.SurfaceId, path, value))
        match c.Kind with
        | ComponentKind.Text (text, variant) ->
            let said = textIn scope text
            match variant with
            | Some TextVariant.H1
            | Some TextVariant.H2 ->
                html $"""<p class="{Style.surfaceHeading}" role="heading" aria-level="2" style="{weight c}" data-surface-component="{c.Id}">{said}</p>"""
            | Some TextVariant.H3
            | Some TextVariant.H4
            | Some TextVariant.H5 ->
                html $"""<p class="{Style.surfaceSubheading}" role="heading" aria-level="3" style="{weight c}" data-surface-component="{c.Id}">{said}</p>"""
            | Some TextVariant.Caption ->
                html $"""<p class="{Style.surfaceCaption}" style="{weight c}" data-surface-component="{c.Id}">{said}</p>"""
            | Some TextVariant.Body
            | None -> html $"""<p class="{Style.surfaceText}" style="{weight c}" data-surface-component="{c.Id}">{said}</p>"""
        | ComponentKind.Row (list, justify, align) -> container canvas ancestors c scope Style.surfaceRow list justify align
        | ComponentKind.Column (list, justify, align) -> container canvas ancestors c scope Style.surfaceColumn list justify align
        | ComponentKind.List (list, direction, align) ->
            let face = if direction = Some Direction.Horizontal then Style.surfaceRow else Style.surfaceColumn
            container canvas ancestors c scope face list None align
        | ComponentKind.Card child ->
            html $"""
                <div class="{Style.surfaceCard}" style="{weight c}" data-surface-component="{c.Id}">{draw canvas ancestors child scope}</div>"""
        | ComponentKind.Divider axis ->
            let face = if axis = Some Direction.Vertical then Style.surfaceDividerVertical else Style.surfaceDivider
            html $"""<div class="{face}" role="separator" data-surface-component="{c.Id}"></div>"""
        | ComponentKind.Button (child, variant, action, checks) ->
            let unmet = failing scope checks
            let ready = List.isEmpty unmet
            // The press names the button and where it was drawn; the session reads the answer
            // from the doc. A button that is not ready, or that calls a function here rather
            // than telling the agent, sends nothing.
            let press _ =
                match action with
                | Action.Event _ when ready -> canvas.Dispatch (PressSurfaceMsg (canvas.SurfaceId, c.Id, scope.Base))
                | Action.Event _
                | Action.LocalCall _ -> ()
            let face =
                match variant, ready with
                | Some ButtonVariant.Primary, true -> Style.btnPrimary
                | Some ButtonVariant.Borderless, true -> Style.btnBare
                | Some ButtonVariant.Borderless, false -> Style.btnBareWaiting
                | (Some ButtonVariant.Default | None), true -> Style.btn
                | _, false -> Style.btnWaiting
            html $"""
                <button type="button" class="{face}" style="{weight c}" data-surface-component="{c.Id}"
                        aria-disabled="{if ready then "false" else "true"}" @click={Ev press}>{draw canvas ancestors child scope}</button>{reasons unmet}"""
        | ComponentKind.TextField (label, value, variant, pattern, checks) ->
            let current = value |> Option.map (textIn scope) |> Option.defaultValue ""
            let target = value |> Option.bind (boundAt scope)
            let typed (v: string) = target |> Option.iter (fun path -> edit path (JString v))
            let mismatch =
                match pattern with
                | Some p when current <> "" && not (matches p current) ->
                    [ "This does not match the expected format." ]
                | _ -> []
            let unmet = failing scope checks @ mismatch
            let input =
                match variant with
                | Some TextFieldVariant.LongText ->
                    html $"""
                        <textarea class="{Style.surfaceFieldLong}" .value={current}
                                  aria-invalid="{if List.isEmpty unmet then "false" else "true"}"
                                  @input={EvVal typed}></textarea>"""
                | _ ->
                    let kind =
                        match variant with
                        | Some TextFieldVariant.Number -> "number"
                        | Some TextFieldVariant.Obscured -> "password"
                        | _ -> "text"
                    html $"""
                        <input class="{Style.surfaceField}" type="{kind}" .value={current}
                               aria-invalid="{if List.isEmpty unmet then "false" else "true"}"
                               @input={EvVal typed} />"""
            html $"""
                <label class="{Style.surfaceFieldLabel}" style="{weight c}" data-surface-component="{c.Id}">
                  <span>{textIn scope label}</span>
                  {input}
                  {reasons unmet}
                </label>"""
        | ComponentKind.CheckBox (label, value, checks) ->
            let on = Evaluate.conditionOf scope value |> Result.defaultValue false
            let target = boundAt scope value
            html $"""
                <div class="{Style.surfaceChoices}" style="{weight c}" data-surface-component="{c.Id}">
                  <button type="button" role="checkbox" aria-checked="{if on then "true" else "false"}"
                          class="{if on then Style.surfaceChoiceChosen else Style.surfaceChoice}"
                          @click={Ev(fun _ -> target |> Option.iter (fun path -> edit path (JBool (not on))))}>
                    <span class="{Style.surfaceChoiceMark}" aria-hidden="true">{if on then Icon.check else Lit.nothing}</span>
                    <span class="{Style.surfaceChoiceLabel}">{textIn scope label}</span>
                  </button>
                </div>{reasons (failing scope checks)}"""
        | ComponentKind.ChoicePicker (label, variant, options, value, _, _, checks) ->
            // The spec's default is one answer (`mutuallyExclusive`).
            let many = variant = Some ChoiceVariant.MultipleSelection
            let chosen = Evaluate.selectionOf scope value |> Result.defaultValue [] |> Set.ofList
            let target = boundAt scope value
            let heading = label |> Option.map (textIn scope) |> Option.defaultValue ""
            // In the options' own order, so the answer reads the way the question listed it.
            let toggle (picked: string) =
                let next =
                    if not many then [ picked ]
                    else
                        let flipped = if Set.contains picked chosen then Set.remove picked chosen else Set.add picked chosen
                        options |> List.map (fun o -> o.Value) |> List.filter (fun v -> Set.contains v flipped)
                target |> Option.iter (fun path -> edit path (JArray (List.map JString next)))
            let role = if many then "checkbox" else "radio"
            let option (o: ChoiceOption) =
                let on = Set.contains o.Value chosen
                html $"""
                    <button type="button" role="{role}" aria-checked="{if on then "true" else "false"}"
                            class="{if on then Style.surfaceChoiceChosen else Style.surfaceChoice}"
                            data-surface-choice="{o.Value}" @click={Ev(fun _ -> toggle o.Value)}>
                      <span class="{Style.surfaceChoiceMark}" aria-hidden="true">{if on then Icon.check else Lit.nothing}</span>
                      <span class="{Style.surfaceChoiceLabel}">{textIn scope o.Label}</span>
                    </button>"""
            let caption =
                if heading = "" then Lit.nothing
                else html $"""<p class="{Style.label}">{heading}</p>"""
            html $"""
                <div class="{Style.surfaceColumn}" style="{weight c}" data-surface-component="{c.Id}">
                  {caption}
                  <div class="{Style.surfaceChoices}" role="{if many then "group" else "radiogroup"}" aria-label="{heading}">
                    {options |> List.map option}
                  </div>
                  {reasons (failing scope checks)}
                </div>"""
        | ComponentKind.Image _
        | ComponentKind.Icon _
        | ComponentKind.Video _
        | ComponentKind.AudioPlayer _
        | ComponentKind.Tabs _
        | ComponentKind.Modal _
        | ComponentKind.Slider _
        | ComponentKind.DateTimeInput _ ->
            let name = kindName c.Kind
            html $"""
                <p class="{Style.surfaceUnsupported}" data-surface-component="{c.Id}" data-surface-unsupported="{name}">{Dom.Text.surfaceUnsupported name}</p>"""

    /// One surface, from its `root` component, beside the app's own ×: every
    /// surface can be taken down unanswered, whatever buttons the agent drew
    /// (`DismissSurfaceMsg`). A surface whose root has not arrived yet is not drawn: there is
    /// nothing to answer.
    let view (dispatch: ClientMsg -> unit) (surfaceId: string) (surface: Surface) : TemplateResult =
        if not (Map.containsKey "root" surface.Components) then Lit.nothing
        else
            let canvas = { SurfaceId = surfaceId; Components = surface.Components; Dispatch = dispatch }
            html $"""
                <section class="{Style.agentSurface}" data-surface="{surfaceId}" aria-label="{Dom.Text.agentSurface}">
                  <div class="{Style.surfaceBody}">
                    <div class="{Style.surfaceContent}">{draw canvas Set.empty "root" (Evaluate.root surface.DataModel)}</div>
                    <button type="button" class="{Style.btnIcon}" data-surface-dismiss
                            aria-label="{Dom.Text.surfaceDismissLabel}" title="{Dom.Text.surfaceDismissLabel}"
                            @click={Ev(fun _ -> dispatch (DismissSurfaceMsg surfaceId))}>{Icon.close}</button>
                  </div>
                </section>"""
