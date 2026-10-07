module Yession.Browser.Render

// One model in, one page out: the whole of what happens between a model changing and the
// page showing it. Lit's diff of `View.view`, then everything the view cannot do for itself
// — the pinned surfaces' scroll kept across the diff, the rich editors mounted on their body
// hosts, the terminal command lines bound to their roots, the keyframes the open tabs need,
// the replays and live screens folded forward, the rail measured, the slot rules and the
// catch-up timer started and stopped, collaborators' carets overlaid. And WHEN: at most once a
// frame however many models arrive in it, and less often than that while the client is
// catching up (`setState`).
//
// Its own module, and before `Browser.fs`, because two entry points drive it: the app, and
// the host-free shell harness the `Browser`-tier E2E and the `bench` scroll scenario run
// against. It used to be the app's `setState` alone, and the harness had a render of its own
// that ran the view, the replays, the screens and the rail and nothing else — so the harness
// measured about six sevenths of what a person waits for, and a change to the other seventh
// (the scroll restore, the editor mounts, the presence push) was invisible to every test and
// every benchmark that did not need a Session. What is measured has to be what runs.
//
// What this does NOT know is where the session is. Sending a draft, reporting a caret,
// relaying a resize, fetching a keyframe: each is handed in (`Links`), because the harness has
// nothing to send to and the app has a connection that is not there yet when the first render
// happens.

open Fable.Core
open Fable.BrowserExtras
open Lit
open Yjs
open Yession.Domain
open Yession.Codecs
open Yession.Domain.Link
open Yession.Domain.Terminals
open Yession.Domain.Collab
open Yession.App.Collab
open Fable.ProseMirror
open Yession.App
open Yession.App.Codecs

// --- What the page can do that a template cannot -------------------------------------------

// How many times the whole view has been rendered since this document loaded.
//
// A render is the unit of cost on this page. Each one re-renders the entire view and reads
// the scroll positions back out of layout (`Tail.before`) — so the question "is reopening a
// session expensive?" is really "how many renders does it take?", and that is a COUNT: the
// same number on a laptop and on a phone, unlike every millisecond a test could measure
// instead.
//
// Published rather than inferred. A test can already see the cost indirectly — hook
// `document.querySelectorAll`, watch `Tail.before` go past, count the calls — and that
// reads a private detail of the render below, so it goes quietly VACUOUS the day the scroll
// is preserved some other way: no calls, count zero, budget met, nothing to see. This is the
// render saying what it did, and it is wrong only if it is removed.
//
// It is `globalThis.__yessionRenders`, and the two readers outside this project — the
// render-budget case in `Browser.fs`, and the frames tool's recorder — read it by that
// name in the page. The harness reads it through `renders` below. Absent until the first
// render of this document.
let private rendersPublished : PageGlobal<int> = PageGlobal.named "__yessionRenders"

let private countRender () : unit =
    PageGlobal.set
        rendersPublished
        (match PageGlobal.tryGet rendersPublished with
         | Some n -> n + 1
         | None -> 1)

/// How many times the whole view has been rendered since this document loaded — the count
/// published above, read back so a scenario counts the renders the APP made rather than a
/// count of its own. Zero before the first.
let renders () : int =
    match PageGlobal.tryGet rendersPublished with
    | Some n -> n
    | None -> 0

/// Watch the foot of a paged listing inside the card's own scroller, so the next page arrives
/// as the reader reaches it rather than on a press. The caller stops the observer when the
/// foot goes.
///
/// `IntersectionObserver` rather than a scroll handler, because it answers the question being
/// asked — is the foot on screen — including the case a scroll handler never sees at all: a
/// first page that did not fill the card, where the foot is visible and nobody has scrolled.
/// Observing fires once immediately for exactly that.
///
/// `rootMargin` is what makes it feel like there is no paging: the page is asked for while the
/// foot is still a screenful below, so the rows are usually there before the reader is.
///
/// `wanted` is asked WHETHER to fetch, every time, rather than being handed a cursor when the
/// observer was made: one observer outlives many renders, and a cursor captured at the first
/// would go on asking for the same page. Doing nothing is how the caller says "not now" —
/// already in flight, or nothing more to ask for.
let private watchListingFoot
    (root: Browser.Types.Element)
    (foot: Browser.Types.Element)
    (wanted: unit -> unit)
    : IntersectionObserver =
    let observer =
        IntersectionObserver.create
            (fun entries -> if entries |> Array.exists (fun entry -> entry.isIntersecting) then wanted ())
            root
            "400px 0px"
    observer.observe foot
    observer

/// The canvas a collaborator's caret is measured on, made once and kept. A canvas measures
/// text without laying any out, which is the only way to ask a font how wide a run of
/// characters is; making one per caret per render would be a DOM node per frame for an answer
/// that depends on nothing the canvas holds.
let mutable private measuringCanvas : Browser.Types.HTMLCanvasElement option = None

let private measuringContext () : Browser.Types.CanvasRenderingContext2D =
    let canvas =
        match measuringCanvas with
        | Some canvas -> canvas
        | None ->
            let made = Browser.Dom.document.createElement "canvas" :?> Browser.Types.HTMLCanvasElement
            measuringCanvas <- Some made
            made
    canvas.getContext_2d ()

/// A CSS length as pixels, or zero. `getPropertyValue` answers `"12px"` for a resolved length
/// and `""` for anything it cannot resolve, and only the first is a number to add.
let private pixels (el: Browser.Types.HTMLElement) (property: string) : float =
    match System.Double.TryParse ((computedProperty el property).Trim().Replace ("px", "")) with
    | true, value -> value
    | _ -> 0.0

/// A measured length as CSS. Fixed to three decimals rather than written out in full: the
/// measurement is sub-pixel and a caret has to land on the glyph, but no layout can use what
/// a float's whole expansion says past that.
let private px (value: float) : string = sprintf "%.3fpx" value

/// The font a field draws its text in, as a canvas `font` string. The `font` shorthand is the
/// answer wherever the browser resolves one, and the four longhands it is composed of where it
/// does not — a canvas measures in whatever font it is told, and in a default one otherwise,
/// which is a plausible wrong answer rather than a failure.
let private fieldFont (el: Browser.Types.HTMLElement) : string =
    let shorthand = computedProperty el "font"
    if not (System.String.IsNullOrWhiteSpace shorthand) then
        shorthand
    else
        sprintf
            "%s %s %s %s"
            (computedProperty el "font-style")
            (computedProperty el "font-weight")
            (computedProperty el "font-size")
            (computedProperty el "font-family")

/// The span a peer's selection covers in a field of `length` characters: its low end, its high
/// end, and where the caret itself is. Clamped at both ends, because a position decoded
/// against a document that has since shrunk is a real offset into text that is no longer
/// there.
let private selectionSpan (length: int) (anchor: int) (head: int) : int * int * int =
    let clamp i = max 0 (min length i)
    let anchor, head = clamp anchor, clamp head
    min anchor head, max anchor head, head

/// A native <input> has no per-character DOM geometry, so the pixel offset of a substring is
/// measured on a canvas in the input's own font. Given a peer's decoded selection
/// (`anchor`,`head` indices), size its highlight span to `lo..hi` and offset the caret bar to
/// `head`. Colour is set by the view (`Entity.presenceColour`); this only positions. Called per peer
/// whose caret is in a collaborative input after every render — the DOM is up to date
/// synchronously.
///
/// Everything the marker needs is READ OFF THE FIELD, never assumed from the stylesheet: the
/// marker is a sibling of the input, and where the input's text sits in the block they share is
/// a function of the input's own offset, padding and content box. The title alone is a 28/32
/// heading at one width and a 19/24 pivot at the other, its padding spent outward so a fill can
/// appear without moving a glyph — and a chapter's name is a third type at a fourth size. A
/// marker placed from constants would be right at exactly one of them and silently wrong at the
/// rest, which is why the field is named by a SELECTOR here and nothing else about it is.
///
/// The marker is found INSIDE the input's own block rather than on the page: the offsets it is
/// positioned by are its offset parent's, so a marker taken from somewhere else on the page
/// would be laid out against a box it does not live in.
let private placeInputCursor (field: string) (peer: string) (anchor: int) (head: int) : unit =
    match Browser.Dom.document.querySelector field with
    | null -> ()
    | found ->
        let input = found :?> Browser.Types.HTMLInputElement
        match input.parentElement with
        | null -> ()
        | block ->
            match block.querySelector (sprintf "[data-cursor-peer=\"%s\"]" peer) with
            | null -> ()
            | marker ->
                let marker = marker :?> Browser.Types.HTMLElement
                let context = measuringContext ()
                context.font <- fieldFont input
                let value = input.value
                let lo, up, head = selectionSpan value.Length anchor head
                let padLeft = pixels input "padding-left"
                let padTop = pixels input "padding-top"
                let left = input.offsetLeft + pixels input "border-left-width" + padLeft
                let top = input.offsetTop + pixels input "border-top-width" + padTop
                let height = input.clientHeight - padTop - pixels input "padding-bottom"
                // Where the i'th character starts, in the block the marker is laid out in: the
                // text's own origin, plus what the font says the run before it takes, less how
                // far the field has been scrolled under its own box.
                let xOf i = left + context.measureText(value.Substring (0, i)).width - input.scrollLeft
                let loX = xOf lo
                setStyleProperty marker "left" (px loX)
                setStyleProperty marker "top" (px top)
                setStyleProperty marker "height" (px height)
                setStyleProperty marker "width" (px (max 0.0 (xOf up - loX)))
                // The caret bar is the marker's only element child. `:scope > *` is that
                // child; the marker's first NODE is the template's own indentation.
                match marker.querySelector ":scope > *" with
                | null -> ()
                | bar -> setStyleProperty (bar :?> Browser.Types.HTMLElement) "left" (px (xOf head - loX))

let private raf (f: unit -> unit) : unit =
    Browser.Dom.window.requestAnimationFrame (fun _ -> f ()) |> ignore

// The render hold's clock: how long since the last render (see `setState`). What waits on the
// model's own state is the model's to declare (`ClientModel.timers`); what needs pacing is
// paced by the frame (`raf`).
let private now () : float = Browser.Performance.performance.now ()

// How long a render waits for an animation frame before it stops waiting (see `setState`). A
// page that is on screen draws sixty times a second, so this never fires there; it is the
// frame of a page in a background tab, which draws none, and a browser that throttles a
// hidden page's timers to once a second makes it that anyway.
let private hiddenFrameMs = 1000

// --- Rich-text editor mount ------------------------------------------------------------
// The view renders empty `[data-rich-body="<key>"]` hosts; the editor is mounted imperatively
// into each, bound to the body's live Y.XmlFragment (resolved from the BodyRegistry).
// ProseMirror owns that DOM; Lit leaves the static host's children alone across re-renders
// (as it preserved the textareas).
let private richBodyHosts () : Browser.Types.Element list =
    let found = Browser.Dom.document.querySelectorAll "[data-rich-body]"
    [ for i in 0 .. found.length - 1 -> found.[i] ]

let private hostBodyKey (el: Browser.Types.Element) : string = el.getAttribute "data-rich-body"

let private hostReadOnly (el: Browser.Types.Element) : bool =
    el.getAttribute "data-rich-readonly" = "true"

// --- Terminal command lines (Plan 13) --------------------------------------------------
// The view renders `<input data-terminal-input="<key>">` for each terminal composer slot and
// each queued command; the value is bound imperatively to that key's `Y.Text` root, the same
// arrangement the rich bodies use one level up. An `<input>` rather than an editor because a
// command is characters, and the CRDT merge happens per character either way.

/// Every command line on the page, in document order.
let private terminalInputs () : Browser.Types.HTMLInputElement list =
    let found = Browser.Dom.document.querySelectorAll "[data-terminal-input]"
    [ for i in 0 .. found.length - 1 -> found.[i] :?> Browser.Types.HTMLInputElement ]

// The READ of the same roots: a surface that SHOWS a command without offering to change it —
// the chat's chip for a queued command, which is a `<button>` and so cannot hold an input.

let private terminalTexts () : Browser.Types.HTMLElement list =
    let found = Browser.Dom.document.querySelectorAll "[data-terminal-text]"
    [ for i in 0 .. found.length - 1 -> found.[i] :?> Browser.Types.HTMLElement ]

/// Show a command on a surface that cannot be typed into. Only on a change: assigning
/// `textContent` replaces the node's children, which is a write the browser need not be
/// asked to make for text it is already showing.
let private setTextContent (el: Browser.Types.HTMLElement) (value: string) : unit =
    if el.textContent <> value then el.textContent <- value

/// Where this input's selection is, and `None` when it has none to give: an `<input>` whose
/// type carries no text selection answers `null` rather than an offset.
let private inputSelection (el: Browser.Types.HTMLInputElement) : (int * int) option =
    if isNull (box el.selectionStart) then None else Some (el.selectionStart, el.selectionEnd)

/// Set an input's value while keeping the caret where the person left it. A remote edit
/// re-renders the value under a focused input, and `el.value <- …` resets the selection to
/// the end — which is a collaborator's keystroke throwing your cursor across the line.
/// Offsets are clamped, so a shorter value cannot leave the caret past the end.
///
/// Both halves of that are `TerminalText.lineWrite`, which decides them together; this only
/// carries the decision out on the element.
let private setInputValue (el: Browser.Types.HTMLInputElement) (value: string) : unit =
    let caret =
        if System.Object.ReferenceEquals (Browser.Dom.document.activeElement, el) then inputSelection el
        else None
    match TerminalText.lineWrite el.value value caret with
    | TerminalText.LineWrite.Unchanged -> ()
    | TerminalText.LineWrite.Value -> el.value <- value
    | TerminalText.LineWrite.ValueAndCaret (first, last) ->
        el.value <- value
        el.setSelectionRange (first, last)

/// The inputs `bindTerminalInput` has already attached its listeners to. Keyed by the ELEMENT,
/// weakly, because Lit replaces elements, and one a
/// render dropped must be collectable rather than held here for the life of the page.
let private boundTerminalInputs = JS.Constructors.WeakSet.Create<Browser.Types.HTMLInputElement> ()

/// Attach a listener once. Membership in `boundTerminalInputs` is per element, so a Lit
/// re-render that reuses the same element does not stack a second handler on it — and one
/// that creates a fresh element gets its own.
///
/// Because it is once, the handlers passed here must decide from the ELEMENT what they are
/// acting on: an input Lit hands to a second terminal is this same element with a new
/// `data-terminal-input`, and these listeners are the ones it keeps.
///
/// Enter RUNS the command, the same bargain the message composer strikes (`Editor`'s keymap).
/// A command line is one line, so there is no new line for Alt-Enter to insert and none is
/// bound. `isComposing` guards the IME: mid-composition Enter commits the candidate word, and
/// running a half-typed command because someone accepted a suggestion is not a thing to do.
///
/// Ctrl-C in an EMPTY line is ^C to whatever the terminal is running — the one meaning the
/// key has in a terminal, and the one it is free to have here: with nothing in the line and
/// nothing selected on the page there is nothing for it to copy. With either, it is left to
/// the platform. Whether there is anything running to interrupt is the model's to decide
/// (`InterruptTerminalMsg`), not this listener's.
let private bindTerminalInput
    (el: Browser.Types.HTMLInputElement)
    (onInput: unit -> unit)
    (onSelect: unit -> unit)
    (onBlur: unit -> unit)
    (onEnter: unit -> unit)
    (onInterrupt: unit -> unit)
    : unit =
    if not (boundTerminalInputs.has el) then
        boundTerminalInputs.add el |> ignore
        el.addEventListener ("input", fun _ -> onInput ())
        el.addEventListener ("keyup", fun _ -> onSelect ())
        el.addEventListener ("click", fun _ -> onSelect ())
        el.addEventListener ("select", fun _ -> onSelect ())
        el.addEventListener ("focus", fun _ -> onSelect ())
        el.addEventListener ("blur", fun _ -> onBlur ())
        el.addEventListener (
            "keydown",
            fun event ->
                let event = event :?> Browser.Types.KeyboardEvent
                if event.key = "Enter" && not (isComposing event) then
                    event.preventDefault ()
                    onEnter ()
                elif
                    event.ctrlKey && not (event.altKey || event.metaKey || event.shiftKey)
                    && (event.key = "c" || event.key = "C")
                    && not (isComposing event)
                    && el.value = ""
                    && Browser.Dom.window.getSelection().toString () = ""
                then
                    event.preventDefault ()
                    onInterrupt ())

// --- The render ---------------------------------------------------------------------------

/// The collaborative field a body host names — parsed back from its `BodyKey` so a body
/// editor's presence report (and the remote cursors pushed into it) carry the right field.
let private fieldOfKey (key: string) : FocusField option =
    if key.StartsWith "draft:" then
        match PeerId.create (key.Substring 6) with
        | Ok p -> Some (DraftBody p)
        | Error _ -> None
    elif key.StartsWith "queue:" then
        match QueueId.create (key.Substring 6) with
        | Ok q -> Some (QueueBody q)
        | Error _ -> None
    elif key.StartsWith "term-draft:" then
        // `term-draft:<terminal>:<peer>` — split on the FIRST colon after the
        // prefix, because a terminal id is Crockford base32 and never contains one.
        let rest = key.Substring 11
        let idx = rest.IndexOf ':'
        if idx <= 0 then None
        else
            match TerminalId.create (rest.Substring (0, idx)), PeerId.create (rest.Substring (idx + 1)) with
            | Ok terminal, Ok author -> Some (TerminalDraftBody (terminal, author))
            | _ -> None
    elif key.StartsWith "term-queue:" then
        match QueueId.create (key.Substring 11) with
        | Ok q -> Some (TerminalQueuedBody q)
        | Error _ -> None
    else None

/// Presence reported at most once per animation frame: a caret sweep or drag fires many
/// selection events, but the peer only needs the latest. The latest is coalesced; the rAF
/// callback ships whatever it is at paint time — and only if it differs from what was last
/// shipped.
///
/// The dedup lives HERE, with the one presence slot it governs, rather than in each
/// reporter. Three of them share that slot — the rich editor's plugin, the title input,
/// and terminal command lines — and a reporter that compared against its OWN last value
/// would be answering a question about somebody else's write: a command line re-reporting
/// `None` after the editor had claimed the caret would suppress a clear that was needed.
/// Compared on the encoded focus, which is precisely what goes on the wire, so a report is
/// dropped only when it would tell a collaborator nothing.
///
/// Safe because presence is relayed live and last-write-wins (`Host.broadcastPresenceExcept`)
/// — no TTL, so a repeat is never a keepalive. It costs a stationary caret nothing that the
/// reporters were not already costing it: the editor plugin has always dropped an unmoved
/// selection, so a peer arriving late has never been shown one.
///
/// Made separately from the render because the title input reports through it too, and that
/// reporter is an action the view is built with before there is a render to ask.
let focusReporter (report: Focus option -> unit) : Focus option -> unit =
    let mutable scheduled = false
    let mutable latest : Focus option = None
    let mutable sent : Focus option option = None
    fun focus ->
        latest <- focus
        if not scheduled then
            scheduled <- true
            raf (fun () ->
                scheduled <- false
                if sent <> Some latest then
                    sent <- Some latest
                    report latest)

/// What the render reaches for that is not on the page. Every one of these is the session
/// behind the shell, which the harness has none of and the app has only after its first
/// render — so they are handed in rather than known.
type Links =
    { /// Enter in a draft body: send it.
      SendDraft : PeerId -> unit
      /// Enter on a terminal command line: run it.
      SendTerminalDraft : TerminalId -> PeerId -> unit
      /// A caret moved in a body or a command line (already paced by `focusReporter`).
      ReportFocus : Focus option -> unit }

/// Everything the render is composed of.
type Deps =
    { Doc : Y.Doc
      Registry : BodyRegistry
      Texts : TextRegistry
      PeerId : PeerId
      /// Where the view goes. Lit diffs into it; whatever is there on the first render stays.
      Root : Browser.Types.Element
      Actions : ViewActions
      Dispatch : ClientMsg -> unit
      Links : Links }

/// The render, and the two things beside it that need what it keeps.
type Renderer =
    { /// The page for this model: drawn now when this frame has not been drawn yet, and
      /// otherwise at the frame's end, as whichever model is latest by then (`setState`).
      /// A render is everything above, in order, synchronously.
      SetState : ClientModel -> unit
      /// The terminal command lines re-bound and re-valued — on a doc update as well as a
      /// render, because a command line is a root the Ylmish codec does not carry.
      SyncTerminalInputs : unit -> unit
      /// The live screens, for the arrival of a snapshot from the session.
      Screens : Screens.Screens }

let create (deps: Deps) : Renderer =
    let doc = deps.Doc
    let registry = deps.Registry
    let texts = deps.Texts
    let dispatch = deps.Dispatch
    let sendFocus = deps.Links.ReportFocus

    // Rich-text editor mounts. `registry` resolves each body's live Y.XmlFragment (a
    // top-level doc root keyed by BodyKey, so the editor and the Session bind the
    // same fragment); `latest` lets the mount see the current draft slots. Each mount
    // records the fragment it bound so a fragment swap (a sent draft's slot recreated)
    // triggers a remount.
    let mutable latest : ClientModel option = None
    // Keyed by body id; the mount records the fragment AND whether it bound read-only, because
    // both can change under one key: a sent draft's slot is recreated (new fragment), and a
    // draft collapsing to a summary rebinds the same fragment read-only. Either needs a remount
    // — an editable editor left on a collapsed summary would let you type into a one-line
    // preview.
    let mountedBodies = System.Collections.Generic.Dictionary<string, Y.XmlFragment * bool * Editor.EditorHandle> ()
    // The last cursor set pushed into each editor, so a render only dispatches a decoration
    // transaction when it actually changed. Dispatching one every render competes with
    // y-prosemirror's ySync applying REMOTE edits and starves the read-only mirrors' rendering
    // of incoming content — so an unchanged (typically empty) set must never re-dispatch.
    let lastPushed = System.Collections.Generic.Dictionary<string, Editor.RemoteBodyCursor list> ()

    /// Mount an editor on each `[data-rich-body]` host bound to its live fragment; remount when
    /// a host's fragment identity changes; and dispose editors whose host has left the DOM.
    /// Body edits sync through the doc, so the editor needs no change callback — but an editable
    /// body reports its local selection (tagged with the host's field) as rAF-throttled
    /// presence. Mounting publishes no draft slot: the slot follows the body's content
    /// (`DraftSlot.follow`), so a peer that never types shows no draft box on any peer.
    let syncRichBodies () =
        let seen = System.Collections.Generic.HashSet<string> ()
        for host in richBodyHosts () do
            let key = hostBodyKey host
            seen.Add key |> ignore
            let fragment = registry.Fragment key
            let mount () =
                let reportFocus (sel: (string * string) option) =
                    match fieldOfKey key, sel with
                    | Some field, Some (a, h) -> sendFocus (Some { Field = field; Pos = { Anchor = a; Head = h } })
                    | _ -> sendFocus None
                // Ctrl+Enter sends — but only from a DRAFT, which is the only body with a send.
                // A queued message is edited in place and has nothing to commit, so it
                // keeps plain Enter for the paragraph (and Mod-Enter never has to be learned there).
                let onSubmit =
                    match fieldOfKey key with
                    | Some (DraftBody author) -> Some (fun () -> deps.Links.SendDraft author)
                    | _ -> None
                let readOnly = hostReadOnly host
                // A prompt where there is a message to write — the same body the send
                // binding above belongs to. A queued message being edited already holds
                // words, and someone else's draft is not yours to be invited into.
                let placeholder =
                    match fieldOfKey key with
                    | Some (DraftBody _) -> Dom.Text.composerPlaceholder
                    | _ -> ""
                // Who an @ can name, off whatever model is latest at the keystroke.
                let addressable () = latest |> Option.map ClientModel.addressable |> Option.defaultValue []
                mountedBodies.[key] <-
                    (fragment, readOnly, Editor.mountEditor host fragment readOnly reportFocus onSubmit placeholder addressable)
            match mountedBodies.TryGetValue key with
            | true, (bound, readOnly, handle) when
                not (System.Object.ReferenceEquals (bound, fragment)) || readOnly <> hostReadOnly host ->
                handle.Dispose (); mount ()
            | true, _ -> ()
            | _ -> mount ()
        for stale in mountedBodies.Keys |> Seq.filter (seen.Contains >> not) |> Seq.toList do
            let _, _, handle = mountedBodies.[stale]
            handle.Dispose ()
            mountedBodies.Remove stale |> ignore
            lastPushed.Remove stale |> ignore

    /// Bind every rendered terminal command line to its `Y.Text` root: push the CRDT's
    /// value in, send the input's edits back out as the MINIMUM edit that gets there
    /// (`TerminalText.setTo` — anything coarser would clobber a collaborator rather than
    /// merge with them), and report the caret as presence.
    ///
    /// The read-only mounts (`data-terminal-text`) are filled in the same pass rather than
    /// in one of their own, because they read the roots these lines write: two passes is two
    /// callers to keep in step, and the one that forgot would show a command nobody is
    /// typing any more.
    ///
    /// Called after every render AND on every doc update, because a terminal command
    /// line is a root the Ylmish codec does not carry (it holds only the slot's
    /// identity), so a remote keystroke in one does not necessarily reach the model.
    let syncTerminalInputs () =
        for el in terminalInputs () do
            // WHICH line an input is, and whether it may be written to, are read off the
            // element every time a handler runs — never captured when it was bound.
            //
            // Lit reuses one `<input>` across a tab switch (same template, same position,
            // a different terminal's key) and the handlers are attached once per element,
            // so a captured key outlives the terminal it named: keystrokes went into the
            // terminal the input was FIRST rendered for while its value was pushed from
            // the one it now shows, which wiped the line being typed into on every render
            // and left the command in the other terminal, last character only. Same for
            // read-only: a collaborator's slot and your own composer are the same
            // position in that template, so "bind only the editable one" bound whichever
            // it was first and got the other wrong ever after.
            let lineOf () =
                let key = el.getAttribute "data-terminal-input"
                if isNull (box key) || key = "" then None
                // A read-only line (a collaborator's slot) still shows live text; it just
                // never writes back, and never claims a caret.
                elif el.readOnly then None
                else Some key
            // Four events report the caret here and most report it unmoved — a keyup for
            // every key that types rather than navigates, a click landing where the caret
            // already was, the focus that precedes both. They are dropped by `sendFocus`,
            // which is where the slot they all write to lives.
            let reportFocus () =
                match lineOf () with
                | Some key ->
                    match fieldOfKey key, inputSelection el with
                    | Some field, Some (anchor, head) ->
                        let enc i = ProseMirror.relPosFromTypeIndex (texts.Text key) i |> ProseMirror.encodeRel
                        sendFocus (Some { Field = field; Pos = { Anchor = enc anchor; Head = enc head } })
                    | _ -> sendFocus None
                | None -> sendFocus None
            // Enter runs a command from a composer SLOT — the line you are writing.
            // A queued command's line has already been sent; Enter there does
            // nothing rather than queueing it twice.
            let onEnter () =
                match lineOf () |> Option.bind fieldOfKey with
                | Some (TerminalDraftBody (terminal, author)) -> deps.Links.SendTerminalDraft terminal author
                | _ -> ()
            // ^C from a composer slot is for the terminal the slot is in, whoever's slot it is.
            let onInterrupt () =
                match lineOf () |> Option.bind fieldOfKey with
                | Some (TerminalDraftBody (terminal, _)) -> dispatch (InterruptTerminalMsg terminal)
                | _ -> ()
            bindTerminalInput
                el
                (fun () -> lineOf () |> Option.iter (fun key -> TerminalText.setTo texts key el.value))
                reportFocus
                (fun () -> sendFocus None)
                onEnter
                onInterrupt
            let key = el.getAttribute "data-terminal-input"
            if not (isNull (box key)) && key <> "" then setInputValue el (TerminalText.read texts key)
        for el in terminalTexts () do
            let key = el.getAttribute "data-terminal-text"
            if not (isNull (box key)) && key <> "" then setTextContent el (TerminalText.read texts key)

    let replays = PaneReplays.create dispatch

    /// The live screens (Plan 14, stage 6): one emulator per terminal this client has a
    /// snapshot for, folded forward from the records the model already holds — and the
    /// size of each terminal's box, measured into the model (which decides what reaches a pty).
    let screens = Screens.create dispatch

    // The publication rule, one subscription per open terminal. Started when a terminal
    // appears and stopped when it goes, so a closed terminal's rule cannot republish a
    // slot into a terminal that no longer exists.
    let terminalSlots = System.Collections.Generic.Dictionary<string, Subscription> ()

    let syncTerminalSlots (model: ClientModel) =
        let openIds =
            Projection.openTerminals model.Terminals |> List.map (fun t -> TerminalId.value t.TerminalId)
        for terminal in Projection.openTerminals model.Terminals do
            let key = TerminalId.value terminal.TerminalId
            if not (terminalSlots.ContainsKey key) then
                terminalSlots.[key] <- TerminalDraftSlot.follow doc texts terminal.TerminalId deps.PeerId dispatch
        for stale in terminalSlots.Keys |> Seq.filter (fun k -> not (List.contains k openIds)) |> Seq.toList do
            terminalSlots.[stale].Stop ()
            terminalSlots.Remove stale |> ignore

    // The remote cursors currently in a given body, coloured per peer.
    let cursorsFor (key: string) : Editor.RemoteBodyCursor list =
        match fieldOfKey key, latest with
        | Some field, Some model ->
            // A peer with no caret (viewing but not typing) is in `Presence` and belongs in no
            // body — the option is the difference, so choose rather than filter-then-read.
            model.Presence
            |> Map.toList
            |> List.choose (fun (peerId, p) ->
                match p.Focus with
                | Some focus when focus.Field = field ->
                    Some ({ Colour = Entity.presenceColour model peerId
                            Selection = Entity.presenceSelection model peerId
                            Name = p.DisplayName
                            Anchor = focus.Pos.Anchor
                            Head = focus.Pos.Head } : Editor.RemoteBodyCursor)
                | _ -> None)
        | _ -> []

    // Overlay each body's remote cursors, PACED BY THE FRAME: a render marks the push wanted
    // and the next animation frame performs it, at most once per frame however many renders
    // asked. Only editors whose cursor set changed are dispatched (idle empty→empty is
    // skipped), so a settled editor with no cursors is never disturbed; an editor that HAS
    // cursors is re-pushed regardless, because decorations are built from absolute positions
    // and the content moving underneath them invalidates those.
    //
    // This was a 150ms trailing debounce, waiting for the doc to go QUIET — a condition a
    // typing collaborator never meets, so their caret froze for as long as they typed and
    // jumped when they stopped. The comment above it said the wait kept decoration
    // transactions out of y-prosemirror's active-convergence window, where they "starve" its
    // rendering of remote content, and pointed at a two-peer E2E where a co-editor's mirror
    // had stayed blank.
    //
    // That is not what was happening, and it is worth writing down because the evidence
    // looked exactly like it. A ProseMirror widget decoration's DOM lives INSIDE the node it
    // is anchored in, so a co-editor's caret label parked in a heading makes that heading's
    // `textContent` read "Heading oneada". The E2E compared `textContent` to the words
    // exactly — so it was asserting the content AND that nobody's caret was in it. Pushing
    // carets sooner put one there sooner, and the wait then never settled: not late, never,
    // which reads precisely like lost content.
    //
    // The cheap tier now pins the real invariant (`EditorHarness`, two editors on two docs
    // relayed in-page): with a caret pushed on every frame throughout, both docs converge,
    // both editors render, and y-prosemirror's own write-back emits ZERO Yjs updates. The
    // decoration push is not a write, and it never was.
    let mutable pushQueued = false
    let pushPresences () =
        if not pushQueued then
            pushQueued <- true
            raf (fun () ->
                pushQueued <- false
                for kv in mountedBodies do
                    let key = kv.Key
                    let _, _, handle = kv.Value
                    let cursors = cursorsFor key
                    let prev = match lastPushed.TryGetValue key with | true, v -> v | _ -> []
                    if not (List.isEmpty cursors) || prev <> cursors then
                        lastPushed.[key] <- cursors
                        handle.PushPresences cursors)

    /// Place collaborators' carets in the collaborative INPUTS by measurement (a native input
    /// has no per-character geometry): decode each such peer's relative anchor/head against the
    /// doc, then size and offset its marker over the field it is in. A no-op when no remote
    /// caret is in one.
    ///
    /// The field says which input, and the field is the only thing that does. A peer's position
    /// is relative to the text it was taken in, so a caret in one chapter's name measured over
    /// another's would land at a real-looking offset in the wrong name — the one way this can
    /// be wrong that still looks right.
    let placeInputCursorsAll (model: ClientModel) =
        let selectorOf (field: FocusField) : string option =
            match field with
            | Title -> Some "input[data-session-title]"
            | ChapterName messageId ->
                Some (sprintf "input[data-chapter-name=\"%s\"]" (MessageId.value messageId))
            | DraftBody _ | QueueBody _ | TerminalDraftBody _ | TerminalQueuedBody _ -> None
        for (who, p) in Map.toList model.Presence do
            match p.Focus |> Option.bind (fun f -> selectorOf f.Field |> Option.map (fun s -> s, f)) with
            | Some (selector, focus) ->
                match ProseMirror.absIndexInDoc doc focus.Pos.Anchor, ProseMirror.absIndexInDoc doc focus.Pos.Head with
                | Some a, Some h -> placeInputCursor selector (ActorRef.token who) a h
                | _ -> ()
            | None -> ()

    // The picker's foot, and the watch on it. Re-bound when the ELEMENT changes — Lit keeps
    // the same node across renders while the foot is drawn, so that is once when the listing
    // gains a page to come and once when it runs out.
    //
    // What the watch sends is a sighting, not a cursor from the render that made it: one
    // observer outlives many renders, and `Launch.wanting`, read by the reducer, is where
    // "should I ask" lives (a page to come, nothing in flight, no attempt under way).
    // Two feet, one per pane, watched the same way and independently — the branch pane's list
    // pages exactly as the repo pane's does, and both are in the document at once because the
    // track slides rather than swapping.
    let mutable feetSeen : Map<string, Browser.Types.Element> = Map.empty
    let mutable feetWatched : Map<string, IntersectionObserver> = Map.empty
    // Each foot is watched inside ITS OWN pane's scroller: the panes scroll independently, so
    // a watch rooted in the other one would be asking whether the foot is visible in a box it
    // is not in.
    let watchFoot (rootHook: string) (hook: string) (wanted: unit -> unit) =
        let foot = Browser.Dom.document.querySelector ("[" + hook + "]")
        if not (obj.ReferenceEquals (box foot, feetSeen |> Map.tryFind hook |> Option.defaultValue null)) then
            feetWatched |> Map.tryFind hook |> Option.iter (fun watch -> watch.disconnect ())
            feetWatched <- feetWatched |> Map.remove hook
            feetSeen <- feetSeen |> Map.add hook foot
            if not (isNull (box foot)) then
                feetWatched <-
                    feetWatched
                    |> Map.add
                        hook
                        (watchListingFoot
                            (Browser.Dom.document.querySelector ("[" + rootHook + "]"))
                            foot
                            wanted)
    let syncListingFoot () =
        // Every sighting is sent: whether it asks for a page is the reducer's rule
        // (`Launch.wanting`), and the watcher fires many times for one scroll.
        watchFoot Dom.Hooks.repoPickerBody Dom.Hooks.repoPickerFoot (fun () -> dispatch (LaunchMsg LaunchMoreAsked))
        watchFoot Dom.Hooks.repoBranchBody Dom.Hooks.repoBranchFoot (fun () -> dispatch (LaunchMsg LaunchBranchMoreAsked))

    // The `all` page's shortcut (P2-2), from anywhere on the page — which is the point of a
    // shortcut, and why it is the document's rather than the pane's.
    Browser.Dom.document.addEventListener (
        "keydown",
        fun event ->
            let pressed = event :?> Browser.Types.KeyboardEvent
            if ClientModel.opensSwitcher pressed.code pressed.ctrlKey pressed.metaKey then
                pressed.preventDefault ()
                dispatch ToggleSwitcherMsg)

    // Render the Lit view on a model change. Lit diffs into the root, so the focused
    // textarea and its caret survive; only the timeline scroll is restored by hand.
    //
    // Not on EVERY model change, for two reasons, and both are decided here rather than by
    // whoever dispatches: Elmish calls `setState` once per message, so a rule about how often
    // the page is drawn that lived with a sender would be one the next sender has not heard of.
    //
    // At most once a frame. The first model in a frame renders at once, so a keystroke, a
    // click, a lone message is on the page before the call that dispatched it returns, as it
    // always was. Any model after it in the same frame is only REMEMBERED (`latest`), and the
    // frame's end renders whichever is latest by then — one render, however many models came.
    // A render nobody sees is not drawn: a burst of pty output is many reads a second, a
    // message each, and a render per message put seconds of rendering in front of whatever
    // was queued behind them. The link's heartbeat was. The Session asks "are you there" as a
    // frame like any other, answered when the pump reaches it, and on a phone a burst put more
    // than the three seconds the Session waits in front of the answer — a peer that was only
    // busy, dropped as dead. Folded here, a record costs its fold and nothing more, and the
    // answer goes out in the turn the probe arrived. (The connection used to hold live records
    // for a frame itself, which put a rule about drawing in the one module that draws nothing,
    // and left every other burst — a page of events, a storm of presence — a render apiece.)
    //
    // A frame ends at the next animation frame, or after `hiddenFrameMs`, whichever is first.
    // A page in a background tab has no animation frames at all, and a render owed to one
    // would never come — but a tab nobody is looking at still has a TITLE somebody is, and
    // the title is how a pull request that merged or stalled reaches them (`tabTitle`). On a
    // page anybody can see, the animation frame always wins.
    //
    // And while the client is still CATCHING UP, less often than that. A cold open reads the
    // whole log a page per round trip from the oldest, and the conversation is pinned to its
    // foot, so every page rendered was a picture of history the reader never asked for,
    // scrolling past under their eye — 116 of them on a session of 97 items, forty-nine
    // thousand pixels of words moving. None of them is the tail, and the tail is what an open
    // is for. So a render that would show a client still behind is HELD, and one render is
    // made at most every `ClientModel.catchUpQuietMs` while that lasts — a long catch-up still
    // shows its progress and its indicator — and the render that shows the client caught up
    // is the next one the frame allows, whatever the hold. A send puts a client one event
    // behind itself for a round trip, and the page that answers it lands caught up, so live
    // traffic renders as it did; what is paced is a client that STAYS behind.
    //
    // The two never both answer for one model. A model the hold takes cancels whatever the
    // frame was owed (the hold's own timer will draw `latest`, which is newer); a model the
    // hold lets through cancels the hold's timer. Either way what is drawn is `latest`, never
    // the model that happened to ask, so a render that arrives late cannot draw a page older
    // than one already asked for.
    //
    // A focus move (`PaneShell.move`) waits a frame for the render it needs, and still gets
    // it: an owed render is drawn by the frame callback the PREVIOUS render registered, which
    // is ahead of any callback registered since, a move's included.
    let mutable renderedAt = -infinity
    let mutable held = 0
    // The frame the last render is waiting out, by number, or 0 once it is over. By number
    // because each frame is ended by whichever of two callbacks comes first, and the other
    // must not end a frame that began after it was asked for.
    let mutable frames = 0
    let mutable frameOpen = 0
    // A model came during the open frame and was not drawn: the frame's end draws `latest`.
    let mutable owed = false
    let rec setState (model: ClientModel) =
        latest <- Some model
        let since = now () - renderedAt
        if model.EventConsumer.IsCatchingUp && since < float ClientModel.catchUpQuietMs then
            owed <- false
            if held = 0 then
                held <-
                    JS.setTimeout
                        (fun () ->
                            held <- 0
                            draw ())
                        (ClientModel.catchUpQuietMs - int since)
        else
            if held <> 0 then
                JS.clearTimeout held
                held <- 0
            draw ()
    // `latest`, now if this frame has not been drawn yet, at its end if it has.
    and draw () =
        if frameOpen <> 0 then owed <- true
        else latest |> Option.iter render
    and frameEnds (frame: int) =
        if frameOpen = frame then
            frameOpen <- 0
            if owed then
                owed <- false
                latest |> Option.iter render
    and render (model: ClientModel) =
        renderedAt <- now ()
        countRender ()
        latest <- Some model
        frames <- frames + 1
        let frame = frames
        frameOpen <- frame
        raf (fun () -> frameEnds frame)
        JS.setTimeout (fun () -> frameEnds frame) hiddenFrameMs |> ignore
        // Where each reader of a surface read from its end stands, before anything moves
        // them; put back at the END of this render (`Tail`), after every sync below has
        // finished changing heights.
        let tails = Tail.before ()
        Lit.render deps.Root (View.view deps.Actions model dispatch)
        // Mount/dispose the rich editors on their body hosts (bound to live fragments), then
        // overlay collaborators' cursors: remote carets in each body editor, and title carets
        // measured against the just-rendered input.
        syncRichBodies ()
        syncTerminalInputs ()
        replays.Sync model
        // The live screens, and the size the holder's viewport actually is. AFTER the
        // render: the fold feeds an emulator whose serialization the next render draws,
        // and the measurement needs a box that exists.
        screens.Sync model
        // The terminals column's open state is a class on the shell root, like the
        // sidebar's — presentation, so a re-render never fights it — but driven FROM the
        // model, because unlike the sidebar this column's visibility is something the app
        // itself changes (selecting a terminal opens it).
        PaneShell.setOpen model.TerminalsOpen
        PaneShell.setColumn model.Column
        PaneShell.setPaneSplit (ClientModel.paneSplit model)
        // The strip's selected tab in view when the selection moved — read off the DOM rather
        // than the model, so a tab a collaborator's `TabOpened` selected is covered too — and
        // its fade on whichever ends have tabs past them.
        PaneShell.syncStrip ()
        // Keep a slot rule running for every open terminal: a person may be mid-command
        // in more than one, and each slot follows its own command line.
        syncTerminalSlots model
        // After the render, because the foot it watches is a node this render just drew.
        syncListingFoot ()
        pushPresences ()
        placeInputCursorsAll model
        // The tab's name, which lives outside the root and so is the model's to push rather
        // than Lit's to render. The NAME is computed in the model (`tabTitle`); this only
        // applies it, and only on a change — assigning `document.title` every render is a
        // write the browser need not be asked to make.
        let tab = ClientModel.tabTitle model
        if Browser.Dom.document.title <> tab then Browser.Dom.document.title <- tab
        // Last, because everything above can move an end: an editor mounted into a message,
        // the pane opened beside the chat, a screen folded forward. A reader put back at the
        // end before those ran was put back at an end that had moved by the time anybody
        // looked.
        Tail.restore tails

    { SetState = setState
      SyncTerminalInputs = syncTerminalInputs
      Screens = screens }

/// The listeners that belong with the render and are bound once per page: renders keep the
/// reader's place (`setState`), and this keeps it across the other thing that moves it, a
/// viewport that changed size under a laid-out surface; and the split between the two columns
/// is the reader's to set, not the theme's.
let attach (dispatch: ClientMsg -> unit) : unit =
    Tail.attach dispatch
    PaneShell.installPaneResize dispatch
    PaneShell.installStrip ()
