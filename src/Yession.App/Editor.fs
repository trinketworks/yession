namespace Yession.App

open Fable.Core.JsInterop
open Yjs
open Fable.ProseMirror.ProseMirror
open Fable.BrowserExtras

/// The Linear-style rich-text editor: type or paste Markdown, rendered live as formatted
/// rich text. Pure F# over the `ProseMirror` bindings (no authored JS). The document lives
/// in a `Y.XmlFragment` (via `ySyncPlugin`), so edits flow straight into the CRDT and merge.
/// Exposed to the browser host as `mountEditor`; fragment↔markdown lives in `Domain.Markdown`.
module Editor =

    /// One remote peer's caret+selection to overlay on a body editor. Positions are base64 Yjs
    /// relative positions over this body's fragment; colours are precomputed (`EditorColour`).
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
    /// all. ProseMirror hands `handlePaste` an untyped event; `Browser.Types` already says what
    /// one is, so the absence is answered in F# rather than by a ternary in a string — and it is
    /// answered as an absence: an event with no clipboard and a clipboard holding nothing for
    /// this format are two different facts, and an empty string would be the last place they
    /// could still be told apart.
    let private clipboard (event: obj) (fmt: string) : string option =
        let data = (unbox<Browser.Types.ClipboardEvent> event).clipboardData
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
        inputRules (createObj [ "rules" ==> rules.ToArray () ])

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
    let private editorKeymap (onSubmit: (unit -> unit) option) : obj =
        let keys = createObj []
        keys?("Mod-z") <- yUndo
        keys?("Mod-y") <- yRedo
        keys?("Mod-Shift-z") <- yRedo
        // A mark the schema does not declare gets no key: an unbound key is honest, and one
        // toggling a mark that is not there is not.
        markType schema "strong" |> Option.iter (fun strong -> keys?("Mod-b") <- toggleMark strong)
        markType schema "em" |> Option.iter (fun em -> keys?("Mod-i") <- toggleMark em)
        // What a plain Enter always meant in prose: split the list item when in one, else
        // whatever ProseMirror's own Enter does.
        let listItem = nodeType schema "list_item"
        let newParagraph =
            match listItem with
            | Some li -> chain (splitListItem li) (baseEnter baseKeymap)
            | None -> baseEnter baseKeymap
        listItem
        |> Option.iter (fun li ->
            keys?("Tab") <- sinkListItem li
            keys?("Shift-Tab") <- liftListItem li
            keys?("Mod-[") <- liftListItem li
            keys?("Mod-]") <- sinkListItem li)
        lineBreak () |> Option.iter (fun command -> keys?("Shift-Enter") <- command)
        keys?("Enter") <- newParagraph
        onSubmit |> Option.iter (fun submit -> keys?("Mod-Enter") <- effectCommand submit)
        keys

    // --- Presence: report the local selection, overlay remote ones -------------------------

    /// Reports the local caret+selection (as base64 relative anchor/head) whenever it moves
    /// while focused, on focus, and clears (`None`) on blur/destroy. Added only to editable
    /// editors. The Browser tags the report with this body's field and rAF-throttles it.
    let private presenceReportPlugin (report: (string * string) option -> unit) : Plugin =
        makePlugin (createObj [
            "props" ==> createObj [
                "handleDOMEvents" ==> createObj [
                    "focus" ==> System.Func<EditorView, obj, bool>(fun v _ -> report (relSelectionOf v.state); false)
                    "blur" ==> System.Func<EditorView, obj, bool>(fun _ _ -> report None; false) ] ]
            "view" ==> System.Func<EditorView, obj>(fun _ ->
                let mutable last : (string * string) option = None
                createObj [
                    "update" ==> System.Func<EditorView, EditorState, unit>(fun v _prev ->
                        if viewHasFocus v then
                            let cur = relSelectionOf v.state
                            if cur <> last then
                                last <- cur
                                report cur)
                    "destroy" ==> System.Func<unit, unit>(fun () -> report None) ]) ])

    /// The decoration plugin's key — private so remote cursors can only be pushed through
    /// `EditorHandle.PushPresences`, never by reaching into plugin state.
    let private presenceDecoKey = pluginKey "yession-presence-cursors"

    /// Build the `DecorationSet` for a set of remote cursors: a translucent selection span
    /// `min..max` (when non-empty) and a caret widget + name label at `head`, per peer. A
    /// position that no longer resolves (its content was deleted) is skipped.
    let private buildBodyDecorations (state: EditorState) (remotes: RemoteBodyCursor[]) : obj =
        let decos = ResizeArray<obj> ()
        for r in remotes do
            match absPosInBody state r.Anchor, absPosInBody state r.Head with
            | Some a, Some h ->
                let lo, hi = (min a h), (max a h)
                if lo <> hi then
                    decos.Add (decoInline lo hi (createObj [ "style" ==> sprintf "background-color:%s" r.Selection ]))
                decos.Add (decoWidget h (caretDom r.Colour r.Name) (createObj [ "side" ==> 10 ]))
            | _ -> ()
        decoSetCreate (stateDoc state) (decos.ToArray ())

    /// Holds a `DecorationSet` in plugin state: a `setMeta` push rebuilds it from the remote
    /// cursors; any other transaction remaps the existing set through the doc change.
    let private presenceDecorationsPlugin () : Plugin =
        makePlugin (createObj [
            "key" ==> presenceDecoKey
            "state" ==> createObj [
                "init" ==> System.Func<obj, EditorState, obj>(fun _ _ -> decoSetEmpty)
                "apply" ==> System.Func<Transaction, obj, EditorState, EditorState, obj>(fun tr old _oldS newS ->
                    match trGetMeta tr presenceDecoKey with
                    | null -> if trDocChanged tr then decoSetMap old (trMapping tr) (trDoc tr) else old
                    | remotes -> buildBodyDecorations newS (unbox<RemoteBodyCursor[]> remotes)) ]
            "props" ==> createObj [
                "decorations" ==> System.Func<EditorState, obj>(fun s -> pluginKeyGetState presenceDecoKey s) ] ])

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
        makePlugin (createObj [
            "props" ==> createObj [
                "decorations" ==> System.Func<EditorState, obj>(fun state ->
                    let doc = stateDoc state
                    if docIsEmpty doc then
                        decoSetCreate doc [| decoNode 0 (docContentSize doc) (createObj [ "data-placeholder" ==> text ]) |]
                    else decoSetEmpty) ] ])

    let private plugins
        (fragment: Y.XmlFragment)
        (report: ((string * string) option -> unit) option)
        (onSubmit: (unit -> unit) option)
        (placeholder: string)
        : Plugin[] =
        let ps =
            ResizeArray<Plugin> [
                ySyncPlugin fragment
                yUndoPlugin ()
                markdownInputRules ()
                keymap (editorKeymap onSubmit)
                keymap baseKeymap
                presenceDecorationsPlugin () ]
        report |> Option.iter (fun r -> ps.Add (presenceReportPlugin r))
        if placeholder <> "" then ps.Add (placeholderPlugin placeholder)
        ps.ToArray ()

    /// Plain-text (Markdown) paste -> parsed as Markdown; HTML paste falls through to
    /// ProseMirror's normal clipboard handling.
    let private handlePaste =
        System.Func<EditorView, obj, bool>(fun view event ->
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
        (host: obj)
        (fragment: Y.XmlFragment)
        (readOnly: bool)
        (reportFocus: (string * string) option -> unit)
        (onSubmit: (unit -> unit) option)
        (placeholder: string)
        : EditorHandle =
        let report = if readOnly then None else Some reportFocus
        let submit = if readOnly then None else onSubmit
        // Nothing to prompt in a body you cannot type in, on the same rule that drops the
        // send binding there: a read-only editor is a rendering, not an invitation.
        let prompt = if readOnly then "" else placeholder
        let state = createState (createObj [ "schema" ==> schema; "plugins" ==> plugins fragment report submit prompt ])
        let view =
            createView host (jsOptions<EditorProps> (fun props ->
                props.state <- state
                props.editable <- System.Func<EditorState, bool>(fun _ -> not readOnly)
                props.handlePaste <- handlePaste
                props.handleScrollToSelection <- scrollCaretIntoScrollers))
        { Dispose = fun () -> view.destroy ()
          PushPresences =
            fun remotes ->
                view.dispatch (trSetMeta view.state.tr presenceDecoKey (box (List.toArray remotes))) }
