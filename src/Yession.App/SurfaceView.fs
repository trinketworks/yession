namespace Yession.App

open A2ui
open Lit

/// An agent's A2UI surface, drawn from the synced state (`SyncedSessionState.Surfaces`).
///
/// The agent says what is on the surface in the basic catalog's words; this module says how
/// each word looks, from `Style`'s vocabulary, so a surface wears this product and nothing of
/// the agent's choosing. It wears the launch card's anatomy, because it is the same act — the
/// session asking a person something, docked above the composer: the band with the blue lead,
/// the way out hung in the gutter on the question's line, the question at the card's step,
/// choices as the card's rows with the tick in the gutter, and the one filled verb.
///
/// It draws a subset of the catalog: text, the containers, a card, a divider, a button and the
/// three inputs. Anything else is SAID rather than dropped (`Dom.Hooks.surfaceUnsupported`),
/// because a question with a hole in it is worse than one that admits the hole.
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

    /// What this client has asked of one surface and not yet heard back about: the launch
    /// card's `Sent`, for a surface. Read from the commands in flight (`ClientModel.Asked`)
    /// rather than kept beside them, so it ends exactly when the session answers, either way.
    [<RequireQualifiedAccess>]
    type Sending =
        | Nothing
        /// A button's answer, by the button and the template item it was drawn under.
        | Answer of componentId: string * scope: string
        | Dismissal

    let sending (asked: Yession.Domain.Link.SessionCommand list) (surfaceId: string) : Sending =
        asked
        |> List.tryPick (fun command ->
            match command with
            | Yession.Domain.Link.PressSurfaceButton (id, componentId, scope) when id = surfaceId ->
                Some (Sending.Answer (componentId, scope))
            | Yession.Domain.Link.DismissSurface id when id = surfaceId -> Some Sending.Dismissal
            | _ -> None)
        |> Option.defaultValue Sending.Nothing

    /// What one surface's components draw against.
    type private Canvas =
        { SurfaceId : string
          Components : Map<string, Component>
          /// Whether an answer or a dismissal is on its way. While one is, nothing on the
          /// surface takes a press or an edit — as the launch card's rows and field stand
          /// still while its clone runs — and every control says so by its face.
          Sending : Sending
          Dispatch : ClientMsg -> unit }

    let private frozen (canvas: Canvas) = canvas.Sending <> Sending.Nothing

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

    /// What is wrong with an answer, under the input that holds it — and only once it holds
    /// one (`Style.surfaceProblem` says why).
    let private problems (answered: bool) (messages: string list) : TemplateResult list =
        if not answered then []
        else messages |> List.map (fun message -> html $"""<p class="{Style.surfaceProblem}" role="status">{message}</p>""")

    /// Whether `text` matches the field's `validationRegexp`. A pattern that does not compile
    /// matches nothing, so the field says it cannot be checked rather than the page failing.
    let private matches (pattern: string) (text: string) : bool =
        Evaluate.matches pattern text |> Result.defaultValue false

    let private weight (c: Component) : string =
        c.Weight |> Option.map (sprintf "flex-grow:%g") |> Option.defaultValue ""

    let private flag (on: bool) = if on then "true" else "false"

    /// One choice row, the card's held-row anatomy: the tick's cell on every row and filled
    /// on the chosen ones, so choosing moves no word.
    let private choiceRow (canvas: Canvas) (role: string) (on: bool) (choice: string option) (label: string) (toggle: unit -> unit) =
        let still = frozen canvas
        let face = if on then Style.surfaceChoiceChosen else Style.surfaceChoice
        let press = Ev(fun _ -> if not still then toggle ())
        let line =
            html $"""
                <span class="{Style.surfaceChoiceLine}">
                  <span class="{Style.surfaceChoiceMark}" aria-hidden="true">{if on then Icon.check else Lit.nothing}</span>
                  <span class="{Style.surfaceChoiceLabel}">{label}</span>
                </span>"""
        // Only an option has a value to name; a CheckBox's row carries no choice hook.
        match choice with
        | Some value ->
            html $"""
                <button type="button" role="{role}" aria-checked="{flag on}" aria-disabled="{flag still}"
                        class="{face}" data-surface-choice="{value}" @click={press}>{line}</button>"""
        | None ->
            html $"""
                <button type="button" role="{role}" aria-checked="{flag on}" aria-disabled="{flag still}"
                        class="{face}" @click={press}>{line}</button>"""

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
        let edit (path: string) (value: Json) =
            if not (frozen canvas) then canvas.Dispatch (EditSurfaceMsg (canvas.SurfaceId, path, value))
        match c.Kind with
        | ComponentKind.Text (text, variant) ->
            let said = textIn scope text
            match variant with
            | Some TextVariant.H1
            | Some TextVariant.H2
            | Some TextVariant.H3 ->
                html $"""<p class="{Style.surfaceHeading}" role="heading" aria-level="2" style="{weight c}" data-surface-component="{c.Id}">{said}</p>"""
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
            // This button's answer is the one on its way: it wears the HELD face (`pressed:`,
            // through `aria-busy`) until the session answers, as the card's START holds
            // through its clone. Every other button on the surface waits meanwhile.
            let held =
                match canvas.Sending with
                | Sending.Answer (componentId, at) -> componentId = c.Id && at = scope.Base
                | Sending.Nothing
                | Sending.Dismissal -> false
            let ready = List.isEmpty unmet && not (frozen canvas)
            // The press names the button and where it was drawn; the session reads the answer
            // from the doc. A button that is not ready, or that calls a function here rather
            // than telling the agent, sends nothing.
            let press _ =
                match action with
                | Action.Event _ when ready -> canvas.Dispatch (PressSurfaceMsg (canvas.SurfaceId, c.Id, scope.Base))
                | Action.Event _
                | Action.LocalCall _ -> ()
            let live = ready || held
            // The fill is the surface's one verb, and it says `sending…` in its own box while
            // it holds (`whenReady`/`whenBusy`), so the press does not resize it. The other
            // weights keep their label and wear the hold as their press face.
            let face, label =
                let label = draw canvas ancestors child scope
                match variant with
                | Some ButtonVariant.Primary ->
                    (if live then Style.btnPrimarySwap else Style.btnWaitingSwap),
                    html $"""<span class="{Style.whenReady}">{label}</span><span class="{Style.whenBusy}">{Dom.Text.surfaceSending}</span>"""
                | Some ButtonVariant.Borderless -> (if live then Style.btnBare else Style.btnBareWaiting), label
                | Some ButtonVariant.Default
                | None -> (if live then Style.btn else Style.btnWaiting), label
            // Why it waits is told to a screen reader with the button rather than printed under
            // it: what it waits on is the input above it, which says so itself once it holds
            // something wrong, as the card's START waits on a held row and says nothing.
            let why =
                if ready || held || List.isEmpty unmet then Lit.nothing
                else html $"""<span class="{Style.srOnly}">{String.concat " " unmet}</span>"""
            html $"""
                <button type="button" class="{face}" style="{weight c}" data-surface-component="{c.Id}"
                        aria-disabled="{flag (not live)}" aria-busy="{flag held}" @click={Ev press}>{label}{why}</button>"""
        | ComponentKind.TextField (label, value, variant, pattern, checks) ->
            let current = value |> Option.map (textIn scope) |> Option.defaultValue ""
            let target = value |> Option.bind (boundAt scope)
            let typed (v: string) = target |> Option.iter (fun path -> edit path (JString v))
            let mismatch =
                match pattern with
                | Some p when current <> "" && not (matches p current) ->
                    [ "This does not match the expected format." ]
                | _ -> []
            let answered = current <> ""
            let wrong = failing scope checks @ mismatch
            let invalid = flag (answered && not (List.isEmpty wrong))
            // `readonly` and not `disabled` while an answer is on its way, as the card's search
            // is while its clone runs: still focusable, still legible.
            let still = frozen canvas
            let input =
                match variant with
                | Some TextFieldVariant.LongText ->
                    html $"""
                        <textarea class="{Style.surfaceFieldLong}" .value={current} ?readonly={still}
                                  aria-invalid="{invalid}" @input={EvVal typed}></textarea>"""
                | _ ->
                    let kind =
                        match variant with
                        | Some TextFieldVariant.Number -> "number"
                        | Some TextFieldVariant.Obscured -> "password"
                        | _ -> "text"
                    html $"""
                        <input class="{Style.surfaceField}" type="{kind}" .value={current} ?readonly={still}
                               aria-invalid="{invalid}" @input={EvVal typed} />"""
            html $"""
                <label class="{Style.surfaceFieldLabel}" style="{weight c}" data-surface-component="{c.Id}">
                  <span>{textIn scope label}</span>
                  {input}
                  {problems answered wrong}
                </label>"""
        | ComponentKind.CheckBox (label, value, checks) ->
            let on = Evaluate.conditionOf scope value |> Result.defaultValue false
            let target = boundAt scope value
            let toggle () = target |> Option.iter (fun path -> edit path (JBool (not on)))
            html $"""
                <div class="{Style.surfaceChoices}" style="{weight c}" data-surface-component="{c.Id}">
                  {choiceRow canvas "checkbox" on None (textIn scope label) toggle}
                </div>{problems on (failing scope checks)}"""
        | ComponentKind.ChoicePicker (label, variant, options, value, _, _, checks) ->
            // The spec's default is one answer (`mutuallyExclusive`).
            let many = variant = Some ChoiceVariant.MultipleSelection
            let chosen = Evaluate.selectionOf scope value |> Result.defaultValue [] |> Set.ofList
            let target = boundAt scope value
            let heading = label |> Option.map (textIn scope) |> Option.defaultValue ""
            // In the options' own order, so the answer reads the way the question listed it.
            let toggle (picked: string) () =
                let next =
                    if not many then [ picked ]
                    else
                        let flipped = if Set.contains picked chosen then Set.remove picked chosen else Set.add picked chosen
                        options |> List.map (fun o -> o.Value) |> List.filter (fun v -> Set.contains v flipped)
                target |> Option.iter (fun path -> edit path (JArray (List.map JString next)))
            let role = if many then "checkbox" else "radio"
            let option (o: ChoiceOption) =
                choiceRow canvas role (Set.contains o.Value chosen) (Some o.Value) (textIn scope o.Label) (toggle o.Value)
            let caption =
                if heading = "" then Lit.nothing
                else html $"""<p class="{Style.surfaceFieldLabel}">{heading}</p>"""
            html $"""
                <div class="{Style.surfaceColumn}" style="{weight c}" data-surface-component="{c.Id}">
                  {caption}
                  <div class="{Style.surfaceChoices}" role="{if many then "group" else "radiogroup"}" aria-label="{heading}">
                    {options |> List.map option}
                  </div>
                  {problems (not (Set.isEmpty chosen)) (failing scope checks)}
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

    /// One surface, from its `root` component, with the app's × hung in the gutter on its
    /// first line: every surface can be taken down unanswered, whatever buttons the agent drew
    /// (`DismissSurfaceMsg`). A surface whose root has not arrived yet is not drawn: there is
    /// nothing to answer.
    ///
    /// `asked` is every command this client has in flight (`ClientModel.Asked`), from which the
    /// surface reads whether an answer or a dismissal of it is on its way (`sending`).
    let view (dispatch: ClientMsg -> unit) (asked: Yession.Domain.Link.SessionCommand list) (surfaceId: string) (surface: Surface) : TemplateResult =
        match Map.tryFind "root" surface.Components with
        | None -> Lit.nothing
        | Some root ->
            let state = sending asked surfaceId
            let canvas = { SurfaceId = surfaceId; Components = surface.Components; Sending = state; Dispatch = dispatch }
            let scope = Evaluate.root surface.DataModel
            // A card at the root is the band itself, so its child is drawn in its place.
            let bodyId, ancestors =
                match root.Kind with
                | ComponentKind.Card child -> child, Set.singleton "root"
                | _ -> "root", Set.empty
            let isButton id =
                match Map.tryFind id surface.Components with
                | Some { Kind = ComponentKind.Button _ } -> true
                | _ -> false
            // A column that ends in a row of buttons ends in the surface's ACTIONS, and they
            // are pinned under what scrolls, as the card's START is (`askActions`): the
            // question can run past the band's cap, but what answers it stays on screen.
            let content, actions =
                match Map.tryFind bodyId surface.Components with
                | Some ({ Kind = ComponentKind.Column (ChildList.Static ids, justify, align) } as column) when List.length ids > 1 ->
                    let last = List.last ids
                    match Map.tryFind last surface.Components with
                    | Some ({ Kind = ComponentKind.Row (ChildList.Static buttons, _, _) } as row) when
                        not (List.isEmpty buttons) && List.forall isButton buttons ->
                        let inside = Set.add bodyId ancestors
                        let above = { column with Kind = ComponentKind.Column (ChildList.Static (List.truncate (List.length ids - 1) ids), justify, align) }
                        drawComponent canvas inside above scope,
                        html $"""
                            <div class="{Style.surfaceActions}" data-surface-actions>
                              <div class="{Style.surfaceActionsLine}">{drawComponent canvas (Set.add last inside) row scope}</div>
                            </div>"""
                    | _ -> draw canvas ancestors bodyId scope, Lit.nothing
                | _ -> draw canvas ancestors bodyId scope, Lit.nothing
            let leaving = state = Sending.Dismissal
            html $"""
                <section class="{Style.agentSurface}" data-surface="{surfaceId}" aria-label="{Dom.Text.agentSurface}"
                         aria-busy="{flag (state <> Sending.Nothing)}">
                  <div class="{Style.surfaceLeadBar}" aria-hidden="true"></div>
                  <div class="{Style.surfaceScroll}">
                    <div class="{Style.surfaceFrame}">
                      <button type="button" class="{Style.surfaceWay}" data-surface-dismiss
                              aria-label="{Dom.Text.surfaceDismissLabel}" title="{Dom.Text.surfaceDismissLabel}"
                              aria-busy="{flag leaving}" aria-disabled="{flag (state <> Sending.Nothing)}"
                              @click={Ev(fun _ -> if state = Sending.Nothing then dispatch (DismissSurfaceMsg surfaceId))}>{Icon.close}</button>
                      {content}
                    </div>
                  </div>
                  {actions}
                </section>"""
