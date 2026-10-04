namespace Yession.App

open Fable.Core.JsInterop
open Yjs
open Fable.ProseMirror.ProseMirror
open Fable.BrowserExtras
open Yession.Domain.Chat

/// The Linear-style rich-text editor: type or paste Markdown, rendered live as formatted
/// rich text. Pure F# over the `ProseMirror` bindings (no authored JS). The document lives
/// in a `Y.XmlFragment` (via `ySyncPlugin`), so edits flow straight into the CRDT and merge.
/// Exposed to the browser host as `mountEditor`; fragment↔markdown lives in `Domain.Markdown`.
module Editor =

    /// One remote peer's caret+selection to overlay on a body editor. Positions are base64 Yjs
    /// relative positions over this body's fragment; colours are precomputed (`Entity.presenceColour`).
    type RemoteBodyCursor =
        { Colour : string      // solid — the caret bar and name label
          Selection : string   // translucent — the selection highlight
          Name : string
          Anchor : string      // base64 relative position
          Head : string }      // base64 relative position

    /// A mounted editor: dispose it, and push the current set of remote cursors into it (which
    /// re-derives the on-screen decorations from their relative positions).
    type EditorHandle =
        { Dispose : unit -> unit
          PushPresences : RemoteBodyCursor list -> unit }

    // The attribute and predicate callbacks the block input rules below hand to ProseMirror,
    // which invokes them with the regex match that fired the rule. They used to be JavaScript
    // lambdas in `[<Emit>]` strings, and that was never a binding — a level counted off a
    // match group and a predicate doing arithmetic over a node are LOGIC, and logic written
    // where the compiler reads it as a literal is logic no type-check and no test can reach.
    // The `getAttrs`/`join` shapes are typed next to the rules themselves, in
    // `Fable.ProseMirror`, which is where a statement about somebody else's library belongs.

    /// The number an ordered-list marker names — `7. ` opens a list counting from seven — and
    /// nothing when those digits do not fit an `int`. The regex bounds the run to digits but
    /// not to a length, and the two languages disagree about what a long one means: JavaScript
    /// widens to a float, while an F# parse THROWS, and a throw inside a callback ProseMirror
    /// runs on a keystroke would take the editor down mid-sentence. A number nobody can count
    /// to is no list start, so it is an absence here and the schema's own default stands.
    let private markerNumber (m: string[]) : int option =
        match System.Int32.TryParse m.[1] with
        | true, number -> Some number
        | _ -> None

    /// The list a typed marker opens starts at the number typed.
    let private orderedListAttrs =
        System.Func<string[], NodeAttrs>(fun m ->
            match markerNumber m with
            | Some order -> NodeAttrs.orderedList order
            | None -> null)

    /// Whether the marker just typed CONTINUES the list above it rather than opening a second
    /// one below it: it does exactly when the number typed is the one that list has reached —
    /// where it started, plus the items it already holds. A list whose start is missing
    /// entirely joins nothing, which is what the JavaScript this replaced said too, though it
    /// said it by arriving at `NaN` and comparing that to a number.
    let private orderedListJoin =
        System.Func<string[], Node, bool>(fun m node ->
            match (nodeAttrs node).order, markerNumber m with
            | Some order, Some typed -> nodeChildCount node + order = typed
            | _ -> false)

    /// A heading's level is how many hashes were typed, which the rule's own regex has already
    /// bounded to the six the schema declares.
    let private headingAttrs =
        System.Func<string[], NodeAttrs>(fun m -> NodeAttrs.heading m.[1].Length)

    /// `event.clipboardData.getData(fmt)`, or `None` when the event carries no clipboard at
    /// all — answered as an absence: an event with no clipboard and a clipboard holding
    /// nothing for this format are two different facts, and an empty string would be the last
    /// place they could still be told apart.
    let private clipboard (event: Browser.Types.ClipboardEvent) (fmt: string) : string option =
        let data = event.clipboardData
        if isNullOrUndefined data then None else Some (data.getData fmt)

    /// Inline mark rule: when `**b**` / `*i*` / `` `c` `` is completed at the cursor, replace
    /// the delimited text with the marked text (deleting the delimiters). Later positions are
    /// deleted first so the earlier offsets stay valid.
    let private markRule (pattern: string) (mark: MarkType) : InputRule =
        let handler =
            System.Func<EditorState, string[], int, int, Transaction>(fun state m start endPos ->
                let full = m.[0]
                let inner = if m.Length > 1 then m.[1] else null
                if isNull (box inner) || inner = "" then null
                else
                    let tr = state.tr
                    let textStart = start + full.IndexOf inner
                    let textEnd = textStart + inner.Length
                    let tr = if textEnd < endPos then tr.delete (textEnd, endPos) else tr
                    let tr = if textStart > start then tr.delete (start, textStart) else tr
                    (tr.addMark(start, start + inner.Length, markCreate mark)).removeStoredMark mark)
        makeInputRule (regex pattern) handler

    /// Markdown-typing input rules (block via prosemirror-inputrules helpers, inline via
    /// `markRule`). Each is guarded on the schema actually having the node/mark.
    let private markdownInputRules () : Plugin =
        let n = nodeType schema
        let m = markType schema
        let rules = ResizeArray<InputRule> ()
        rules.AddRange smartQuotes
        rules.Add ellipsis
        rules.Add emDash
        n "blockquote" |> Option.iter (fun t -> rules.Add (wrappingInputRule (regex "^\\s*>\\s$") t))
        n "ordered_list"
        |> Option.iter (fun t -> rules.Add (wrappingInputRuleAttrs (regex "^(\\d+)\\.\\s$") t orderedListAttrs orderedListJoin))
        n "bullet_list" |> Option.iter (fun t -> rules.Add (wrappingInputRule (regex "^\\s*([-+*])\\s$") t))
        n "code_block" |> Option.iter (fun t -> rules.Add (textblockTypeInputRule (regex "^```$") t))
        n "heading" |> Option.iter (fun t -> rules.Add (textblockTypeInputRuleAttrs (regex "^(#{1,6})\\s$") t headingAttrs))
        m "strong" |> Option.iter (fun t -> rules.Add (markRule "(?:\\*\\*|__)([^*_]+)(?:\\*\\*|__)$" t))
        m "em" |> Option.iter (fun t -> rules.Add (markRule "(?:^|[^*_])(?:\\*|_)([^*_]+)(?:\\*|_)$" t))
        m "code" |> Option.iter (fun t -> rules.Add (markRule "`([^`]+)`$" t))
        inputRules (rules.ToArray ())

    /// A LINE BREAK inside the current block: a `hard_break`, which Markdown serializes as a
    /// trailing backslash and parses straight back — so the break survives the round trip into
    /// the timeline (`RichText` renders it as `<br>`) rather than being a thing you can only
    /// see while you type it. `None` when the schema has no such node, because an unbound key
    /// is honest and a key that silently does nothing is not.
    ///
    /// Chained after `exitCode` so the same keystroke steps OUT of a code block, whose `text*`
    /// content cannot hold a break at all.
    let private lineBreak () : Command option =
        nodeType schema "hard_break"
        |> Option.map (fun br ->
            chain
                exitCode
                (editCommand (fun state ->
                    trScrollIntoView ((state.tr).replaceSelectionWith (nodeCreate br, false)))))

    /// Base editing keys + list handling + Yjs-aware undo/redo, and Enter's jobs.
    ///
    /// Enter is a PROSE key here, not a send: it does what Enter always did in text, splitting
    /// the list item when in one, else splitting the block, because a phone's return key has
    /// no modifier to reach for, and a person who presses it is writing, not asking to send
    /// half a thought. Sending is a deliberate second key or the visible Send control:
    ///
    ///   Enter        — a new PARAGRAPH. Plain prose, the same as a plain textarea.
    ///   Mod-Enter    — SEND, in a COMPOSER: Ctrl-Enter or Cmd-Enter, reachable without
    ///                  letting go of the line just written, and never fired by a return key
    ///                  alone.
    ///   Shift-Enter  — a LINE BREAK within the block. Bound in every body, composer or not:
    ///                  it is an editing key, not part of the send bargain — and stays even
    ///                  though plain Enter now reaches the same result, because a shortcut a
    ///                  person already has muscle memory for should not stop working under
    ///                  them.
    ///
    /// A body with nothing to send (a queued message being edited in place) passes `None` for
    /// `onSubmit` and binds no send key: an action that does not exist gets no shortcut.
    let private editorKeymap (onSubmit: (unit -> unit) option) : KeyBindings =
        // What a plain Enter always meant in prose: split the list item when in one, else
        // whatever ProseMirror's own Enter does.
        let listItem = nodeType schema "list_item"
        let newParagraph =
            match listItem with
            | Some li -> chain (splitListItem li) baseKeymap.Enter
            | None -> baseKeymap.Enter
        KeyBindings.ofList [
            Chord.withMod (Key.Char 'z'), yUndo
            Chord.withMod (Key.Char 'y'), yRedo
            Chord.withModShift (Key.Char 'z'), yRedo
            // A mark the schema does not declare gets no key: an unbound key is honest, and
            // one toggling a mark that is not there is not.
            match markType schema "strong" with
            | Some strong -> Chord.withMod (Key.Char 'b'), toggleMark strong
            | None -> ()
            match markType schema "em" with
            | Some em -> Chord.withMod (Key.Char 'i'), toggleMark em
            | None -> ()
            match listItem with
            | Some li ->
                Chord.plain Key.Tab, sinkListItem li
                Chord.withShift Key.Tab, liftListItem li
                Chord.withMod (Key.Char '['), liftListItem li
                Chord.withMod (Key.Char ']'), sinkListItem li
            | None -> ()
            match lineBreak () with
            | Some command -> Chord.withShift Key.Enter, command
            | None -> ()
            Chord.plain Key.Enter, newParagraph
            match onSubmit with
            | Some submit -> Chord.withMod Key.Enter, effectCommand submit
            | None -> () ]

    // --- Presence: report the local selection, overlay remote ones -------------------------

    /// Reports the local caret+selection (as base64 relative anchor/head) whenever it moves
    /// while focused, on focus, and clears (`None`) on blur/destroy. Added only to editable
    /// editors. The Browser tags the report with this body's field and rAF-throttles it.
    let private presenceReportPlugin (report: (string * string) option -> unit) : Plugin =
        makePlugin (jsOptions<PluginSpec<unit, unit>> (fun spec ->
            spec.props <- jsOptions<PluginProps> (fun props ->
                props.handleDOMEvents <- jsOptions<DomEventHandlers> (fun events ->
                    events.focus <- System.Func<EditorView, Browser.Types.FocusEvent, bool>(fun v _ -> report (relSelectionOf v.state); false)
                    events.blur <- System.Func<EditorView, Browser.Types.FocusEvent, bool>(fun _ _ -> report None; false)))
            spec.view <- System.Func<EditorView, PluginView>(fun _ ->
                let mutable last : (string * string) option = None
                jsOptions<PluginView> (fun pluginView ->
                    pluginView.update <- System.Func<EditorView, EditorState, unit>(fun v _prev ->
                        if viewHasFocus v then
                            let cur = relSelectionOf v.state
                            if cur <> last then
                                last <- cur
                                report cur)
                    pluginView.destroy <- System.Func<unit, unit>(fun () -> report None)))))

    /// The decoration plugin's key: it keeps the decorations drawn, and a transaction carries
    /// it the remote cursors to draw them from. Private so remote cursors can only be pushed
    /// through `EditorHandle.PushPresences`, never by reaching into plugin state.
    let private presenceDecoKey : PluginKey<DecorationSet, RemoteBodyCursor[]> =
        pluginKey "yession-presence-cursors"

    /// Build the `DecorationSet` for a set of remote cursors: a translucent selection span
    /// `min..max` (when non-empty) and a caret widget + name label at `head`, per peer. A
    /// position that no longer resolves (its content was deleted) is skipped.
    let private buildBodyDecorations (state: EditorState) (remotes: RemoteBodyCursor[]) : DecorationSet =
        let decos = ResizeArray<Decoration> ()
        for r in remotes do
            match absPosInBody state r.Anchor, absPosInBody state r.Head with
            | Some a, Some h ->
                let lo, hi = (min a h), (max a h)
                if lo <> hi then
                    decos.Add (decoInline lo hi (jsOptions<DecorationAttrs> (fun attrs ->
                        attrs.style <- sprintf "background-color:%s" r.Selection)))
                decos.Add (decoWidget h (caretDom r.Colour r.Name) (jsOptions<WidgetSpec> (fun spec -> spec.side <- 10)))
            | _ -> ()
        decoSetCreate (stateDoc state) (decos.ToArray ())

    /// Holds a `DecorationSet` in plugin state: a `setMeta` push rebuilds it from the remote
    /// cursors; any other transaction remaps the existing set through the doc change.
    let private presenceDecorationsPlugin () : Plugin =
        makePlugin (jsOptions<PluginSpec<DecorationSet, RemoteBodyCursor[]>> (fun spec ->
            spec.key <- presenceDecoKey
            spec.state <- jsOptions<StateField<DecorationSet>> (fun field ->
                field.init <- System.Func<EditorStateConfig, EditorState, DecorationSet>(fun _ _ -> decoSetEmpty)
                field.apply <- System.Func<Transaction, DecorationSet, EditorState, EditorState, DecorationSet>(fun tr old _oldS newS ->
                    match trGetMeta tr presenceDecoKey with
                    | Some remotes -> buildBodyDecorations newS remotes
                    | None -> if trDocChanged tr then decoSetMap old (trMapping tr) (trDoc tr) else old))
            spec.props <- jsOptions<PluginProps> (fun props ->
                props.decorations <- System.Func<EditorState, DecorationSet>(fun s -> pluginKeyGetState presenceDecoKey s))))

    /// The attribute the placeholder decoration puts on the empty paragraph, which `Style`
    /// draws the prompt from.
    type private PlaceholderAttrs =
        inherit DecorationAttrs
        abstract ``data-placeholder`` : string with get, set

    /// What an empty composer says it is for. A `contenteditable` has no `placeholder`
    /// attribute — the field's own `placeholder:` Tailwind variant has never once applied to a
    /// mounted editor — so an unwritten-in composer was a thin unmarked bar with an arrow
    /// beside it and nothing saying it took words. Especially on a phone, where the bar is most
    /// of what there is.
    ///
    /// A node decoration rather than a rendered node: the paragraph carries an attribute while
    /// the doc is empty, and `Style` draws the text from it. So the placeholder is never
    /// CONTENT — it cannot be selected, copied, sent, or synced to a peer as an empty message,
    /// which is exactly what putting the words in the document would risk.
    let private placeholderPlugin (text: string) : Plugin =
        let attrs = jsOptions<PlaceholderAttrs> (fun attrs -> attrs.``data-placeholder`` <- text)
        makePlugin (jsOptions<PluginSpec<unit, unit>> (fun spec ->
            spec.props <- jsOptions<PluginProps> (fun props ->
                props.decorations <- System.Func<EditorState, DecorationSet>(fun state ->
                    let doc = stateDoc state
                    if docIsEmpty doc then decoSetCreate doc [| decoNode 0 (docContentSize doc) attrs |]
                    else decoSetEmpty))))

    // --- Addressing: the @ picker ----------------------------------------------------------

    /// An address being typed: what follows its `@`, where the `@` sits, which offer the
    /// keyboard is on, and whether Escape put it away. What COUNTS as an address is
    /// `Addressed.typing`, the same module that reads one off a sent message — so the picker
    /// can only ever offer to complete something the turn policy will read.
    type private Picking =
        { Partial : string
          At : int
          Highlight : int
          Dismissed : bool }

    [<RequireQualifiedAccess>]
    type private PickerMove =
        | By of int
        | Dismiss

    let private pickerKey : PluginKey<Picking option, PickerMove> = pluginKey "yession-mention-picker"

    let private pickerIds = ref 0

    /// The picker over `addressable`, read at every keystroke so a person who joins mid-word is
    /// offered. A listbox the editor points at with `aria-activedescendant`: the focus never
    /// leaves the text, the arrows move through the offers, Enter or Tab takes one, Escape puts
    /// the list away until the address is started again. A press on an offer takes it too,
    /// without taking the focus.
    let private mentionPlugin (addressable: unit -> string list) : Plugin =
        pickerIds.Value <- pickerIds.Value + 1
        let listId = sprintf "mention-picker-%d" pickerIds.Value
        let offersOf (picking: Picking) = Addressed.offer (addressable ()) picking.Partial
        let wrap (n: int) (i: int) = ((i % n) + n) % n
        let showing (state: EditorState) : (Picking * string list) option =
            match pluginKeyGetState pickerKey state with
            | Some picking when not picking.Dismissed ->
                match offersOf picking with
                | [] -> None
                | offers -> Some (picking, offers)
            | _ -> None
        let take (view: EditorView) (picking: Picking) (name: string) =
            let head = selHead (selection view.state)
            view.dispatch ((view.state.tr).insertText ("@" + name + " ", picking.At, head))
        let list : Browser.Types.HTMLElement = Browser.Dom.document.createElement "ul"
        list.id <- listId
        list.setAttribute ("role", "listbox")
        list.setAttribute ("aria-label", Dom.Text.mentionPickerLabel)
        list.className <- Style.mentionPicker
        // Where the list hangs: off the caret, re-measured every frame while it is open. Once
        // is not enough — the composer moves after the keystroke that opened the list (its
        // draft slot is published, the band re-renders), and a list placed against where the
        // caret WAS covers the words it is completing.
        let mutable placing : (EditorView * int * int) option = None
        let place () =
            match placing with
            | Some (view, at, count) ->
                let caret = view.coordsAtPos at
                let height = Browser.Dom.window.innerHeight
                // Above the caret where there is room: the composer sits at the foot of the
                // screen, and a list hung below it would open off the bottom. Below it where
                // there is not — a message edited in place near the top of the timeline.
                let room = float count * 32.0 + 16.0
                let at =
                    if caret.top > room then sprintf "left:%.0fpx;bottom:%.0fpx" caret.left (height - caret.top + 4.0)
                    else sprintf "left:%.0fpx;top:%.0fpx" caret.left (caret.bottom + 4.0)
                // Written only when it moved: this runs every frame the list is open.
                if list.getAttribute "style" <> at then list.setAttribute ("style", at)
            | None -> ()
        let mutable following = false
        let rec follow () =
            if not following then
                following <- true
                Browser.Dom.window.requestAnimationFrame (fun _ ->
                    following <- false
                    if Option.isSome placing then
                        place ()
                        follow ())
                |> ignore
        let hide (view: EditorView) =
            placing <- None
            list.remove ()
            view.dom.removeAttribute "aria-activedescendant"
            view.dom.setAttribute ("aria-expanded", "false")
        let draw (view: EditorView) =
            match showing view.state with
            | Some (picking, offers) when viewHasFocus view ->
                list.innerHTML <- ""
                let current = wrap offers.Length picking.Highlight
                offers
                |> List.iteri (fun i name ->
                    let option = Browser.Dom.document.createElement "li"
                    option.id <- sprintf "%s-%d" listId i
                    option.setAttribute ("role", "option")
                    option.setAttribute ("aria-selected", (if i = current then "true" else "false"))
                    option.className <- (if i = current then Style.mentionOptionActive else Style.mentionOption)
                    option.textContent <- "@" + name
                    option.addEventListener ("mousedown", fun event ->
                        event.preventDefault ()
                        take view picking name)
                    list.appendChild option |> ignore)
                placing <- Some (view, picking.At, offers.Length)
                place ()
                follow ()
                if isNull list.parentElement then Browser.Dom.document.body.appendChild list |> ignore
                view.dom.setAttribute ("aria-expanded", "true")
                view.dom.setAttribute ("aria-activedescendant", sprintf "%s-%d" listId current)
            | _ -> hide view
        makePlugin (jsOptions<PluginSpec<Picking option, PickerMove>> (fun spec ->
            spec.key <- pickerKey
            spec.state <- jsOptions<StateField<Picking option>> (fun field ->
                field.init <- System.Func<EditorStateConfig, EditorState, Picking option>(fun _ _ -> None)
                field.apply <- System.Func<Transaction, Picking option, EditorState, EditorState, Picking option>(fun tr old _ next ->
                    match trGetMeta tr pickerKey, old with
                    | Some (PickerMove.By step), Some picking -> Some { picking with Highlight = picking.Highlight + step }
                    | Some PickerMove.Dismiss, Some picking -> Some { picking with Dismissed = true }
                    | _ ->
                        let sel = selection next
                        if not (selEmpty sel) then None
                        else
                            match Addressed.typing (textBeforeCaret next) with
                            | None -> None
                            | Some partial ->
                                let at = selHead sel - partial.Length - 1
                                match old with
                                // The same address, one more letter: keep the place in the
                                // list, and keep it put away if it was.
                                | Some picking when picking.At = at -> Some { picking with Partial = partial }
                                | _ -> Some { Partial = partial; At = at; Highlight = 0; Dismissed = false }))
            spec.props <- jsOptions<PluginProps> (fun props ->
                props.handleKeyDown <- System.Func<EditorView, Browser.Types.KeyboardEvent, bool>(fun view event ->
                    let plain = not (event.ctrlKey || event.metaKey || event.altKey || event.shiftKey)
                    match showing view.state with
                    | Some (picking, offers) when plain ->
                        let move m = view.dispatch (trSetMeta view.state.tr pickerKey m); true
                        match event.key with
                        | "ArrowDown" -> move (PickerMove.By 1)
                        | "ArrowUp" -> move (PickerMove.By -1)
                        | "Enter" | "Tab" ->
                            take view picking offers.[wrap offers.Length picking.Highlight]
                            true
                        | "Escape" -> move PickerMove.Dismiss
                        | _ -> false
                    | _ -> false)
                props.handleDOMEvents <- jsOptions<DomEventHandlers> (fun events ->
                    events.blur <- System.Func<EditorView, Browser.Types.FocusEvent, bool>(fun view _ -> hide view; false)))
            spec.view <- System.Func<EditorView, PluginView>(fun view ->
                view.dom.setAttribute ("aria-autocomplete", "list")
                view.dom.setAttribute ("aria-controls", listId)
                view.dom.setAttribute ("aria-expanded", "false")
                jsOptions<PluginView> (fun pluginView ->
                    pluginView.update <- System.Func<EditorView, EditorState, unit>(fun v _ -> draw v)
                    pluginView.destroy <- System.Func<unit, unit>(fun () -> list.remove ())))))

    let private plugins
        (fragment: Y.XmlFragment)
        (report: ((string * string) option -> unit) option)
        (onSubmit: (unit -> unit) option)
        (placeholder: string)
        (addressable: (unit -> string list) option)
        : Plugin[] =
        let ps =
            ResizeArray<Plugin> [
                ySyncPlugin fragment
                yUndoPlugin ()
                markdownInputRules ()
                // Ahead of the keymaps, so Enter takes an offer while the list is open rather
                // than opening a paragraph under it.
                match addressable with
                | Some names -> mentionPlugin names
                | None -> ()
                keymap (editorKeymap onSubmit)
                keymap baseKeymap
                presenceDecorationsPlugin () ]
        report |> Option.iter (fun r -> ps.Add (presenceReportPlugin r))
        if placeholder <> "" then ps.Add (placeholderPlugin placeholder)
        ps.ToArray ()

    /// Plain-text (Markdown) paste -> parsed as Markdown; HTML paste falls through to
    /// ProseMirror's normal clipboard handling.
    let private handlePaste =
        System.Func<EditorView, Browser.Types.ClipboardEvent, bool>(fun view event ->
            if clipboard event "text/html" |> Option.exists (fun html -> html <> "") then false
            else
                match clipboard event "text/plain" with
                | None | Some "" -> false
                | Some text ->
                    let doc = mdParser.parse text
                    if isNull (box doc) then false
                    else
                        view.dispatch ((view.state.tr).replaceSelectionWith (doc, false))
                        true)

    /// Bring the caret into view by scrolling the boxes a reader can scroll — the composer's
    /// own, the conversation around a body edited in place — and never the document.
    ///
    /// ProseMirror's default walk scrolls EVERY ancestor up to `body`, and at `body` it scrolls
    /// the window, measured against `visualViewport.height` without its `offsetTop`. With a
    /// phone keyboard up the visual viewport is short and already panned to the composer by
    /// the platform, so a caret sitting at the foot of the layout reads as below the fold and
    /// the window is scrolled a second time. On iOS that scroll outlives the keyboard: the
    /// shell is `h-dvh overflow-hidden`, nothing in it can scroll the document back, and a
    /// gap the height of the double-count stays under the composer with the header gone off
    /// the top. It fired on a send (y-prosemirror scrolls to the caret when the cleared
    /// fragment lands) and on typing at the bottom line — neither of which passes through
    /// the update loop, which is why nothing the model did ever explained it.
    ///
    /// The shell is sized to the visible viewport and nothing in it is placed by scrolling the
    /// page, so there is no case where the page is the right thing to move. `overflow-hidden`
    /// ancestors are skipped for the same reason: script can scroll them, a reader cannot,
    /// and a clipped box moved by the caret stays moved.
    let private scrollCaretIntoScrollers =
        System.Func<EditorView, bool>(fun view ->
            let margin = 5.0
            let caret = view.coordsAtPos (selHead (selection view.state))
            let document = Browser.Dom.document
            let rec walk (element: Browser.Types.HTMLElement) (top: float) (bottom: float) =
                let atDocument =
                    isNull element
                    || System.Object.ReferenceEquals (element, document.body)
                    || System.Object.ReferenceEquals (element, document.documentElement)
                if not atDocument then
                    let overflowY = Css.computedProperty element "overflow-y"
                    let position = Css.computedProperty element "position"
                    let scrollable = overflowY = "auto" || overflowY = "scroll"
                    let top, bottom =
                        if not scrollable then top, bottom
                        else
                            let box = element.getBoundingClientRect ()
                            let move =
                                if top < box.top then top - box.top - margin
                                elif bottom > box.bottom then
                                    if bottom - top > box.bottom - box.top then top - box.top + margin
                                    else bottom - box.bottom + margin
                                else 0.0
                            if move = 0.0 then top, bottom
                            else
                                let before = element.scrollTop
                                element.scrollTop <- before + move
                                let moved = element.scrollTop - before
                                top - moved, bottom - moved
                    // A fixed or sticky box does not move with anything above it, so nothing
                    // above it can bring the caret any nearer — ProseMirror's own stop.
                    if position <> "fixed" && position <> "sticky" then
                        walk element.parentElement top bottom
            walk view.dom caret.top caret.bottom
            true)

    /// Mount a ProseMirror editor onto `host`, bound to the live `fragment`. The fragment is
    /// the synced, doc-backed body, so edits flow straight into the CRDT and to peers through
    /// the doc — no change callback. `readOnly` renders another peer's content without an edit
    /// surface (and without reporting presence, and with no Enter binding: there is nothing to
    /// send from a body you cannot type in). `reportFocus` receives the local selection as
    /// base64 relative anchor/head (or `None`); `onSubmit` is what Enter does, when this body
    /// has something to send. The returned handle pushes remote cursors in. Serialization for
    /// the drain lives in `Domain.Markdown`.
    let mountEditor
        (host: Browser.Types.Element)
        (fragment: Y.XmlFragment)
        (readOnly: bool)
        (reportFocus: (string * string) option -> unit)
        (onSubmit: (unit -> unit) option)
        (placeholder: string)
        // Who an @ can name here, read at each keystroke; the agent is always offered.
        (addressable: unit -> string list)
        : EditorHandle =
        let report = if readOnly then None else Some reportFocus
        let submit = if readOnly then None else onSubmit
        // Nobody is addressed from a body you cannot type in.
        let addressing = if readOnly then None else Some addressable
        // Nothing to prompt in a body you cannot type in, on the same rule that drops the
        // send binding there: a read-only editor is a rendering, not an invitation.
        let prompt = if readOnly then "" else placeholder
        let state = createState schema (plugins fragment report submit prompt addressing)
        let view =
            createView host (jsOptions<EditorProps> (fun props ->
                props.state <- state
                props.editable <- System.Func<EditorState, bool>(fun _ -> not readOnly)
                props.handlePaste <- handlePaste
                props.handleScrollToSelection <- scrollCaretIntoScrollers))
        { Dispose = fun () -> view.destroy ()
          PushPresences =
            fun remotes ->
                view.dispatch (trSetMeta view.state.tr presenceDecoKey (List.toArray remotes)) }
