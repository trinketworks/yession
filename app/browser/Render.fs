module Yession.Browser.Render

// One model in, one page out: the whole of what happens between a model changing and the
// page showing it. Lit's diff of `View.view`, then everything the view cannot do for itself
// — the pinned surfaces' scroll kept across the diff, the rich editors mounted on their body
// hosts, the terminal command lines bound to their roots, the keyframes the open tabs need,
// the replays and live screens folded forward, the rail measured, the slot rules and the
// catch-up timer started and stopped, collaborators' carets overlaid.
//
// Its own module, and before `Browser.fs`, because two entry points drive it: the app, and
// the host-free shell harness the `Browser`-tier E2E and the `bench` scroll scenario run
// against. It used to be the app's `setState` alone, and the harness had a render of its own
// that ran the view, the replays, the screens and the rail and nothing else — so the harness
// measured about six sevenths of what a person waits for, and a change to the other seventh
// (the scroll restore, the editor mounts, the presence push) was invisible to every test and
// every benchmark that did not need a Session Process. What is measured has to be what runs.
//
// What this does NOT know is where the session is. Sending a draft, reporting a caret,
// relaying a resize, fetching a keyframe: each is handed in (`Links`), because the harness has
// nothing to send to and the app has a connection that is not there yet when the first render
// happens.

open Fable.Core
open Lit
open Yjs
open Yession.Domain
open Yession.Domain.Link
open Yession.Domain.Terminals
open Yession.Domain.Collab
open Yession.App

// --- What the page can do that a template cannot -------------------------------------------

// How many times the whole view has been rendered since this document loaded.
//
// A render is the unit of cost on this page. Elmish calls `setState` once per message, and
// each call re-renders the entire view and reads two scroll positions back out of layout
// (`surfaceScroll`) — so the question "is reopening a session expensive?" is really "how many
// renders does it take?", and that is a COUNT: the same number on a laptop and on a phone,
// unlike every millisecond a test could measure instead.
//
// Published rather than inferred. A test can already see the cost indirectly — hook
// `document.querySelectorAll`, watch `surfaceScroll` go past, count the calls — and that
// reads a private detail of the render below, so it goes quietly VACUOUS the day the scroll
// is preserved some other way: no calls, count zero, budget met, nothing to see. This is the
// render saying what it did, and it is wrong only if it is removed.
[<Emit("globalThis.__yessionRenders = (globalThis.__yessionRenders || 0) + 1")>]
let private countRender () : unit = jsNative

// The two surfaces that are read from their END — the chat, and a terminal's scrollback.
// Both are pinned to the bottom while the reader is at (or within a few px of) it, and both
// keep their place when they have scrolled up to read. `-1` marks "was pinned". Lit
// preserves focus/caret across its diff, but scroll is ours to manage.
//
// One selector list, taken once and put back once: the terminal used to have neither half,
// so a command whose output arrived after the render left the newest line below the fold
// with nothing to say it was there.
let [<Literal>] private PinnedSurfaces = "[data-conversation],[data-terminal-scrollback]"

// Keyed by what the surface IS, never by its position in the list: a terminal that took its
// lease between two renders removes its scrollback from the document, and an index would
// then put its scroll position into the chat.
[<Emit("""(function (selector) {
  const key = el => el.getAttribute('data-terminal-id') || 'chat'
  const taken = {}
  for (const el of document.querySelectorAll(selector)) {
    taken[key(el)] = el.scrollTop + el.clientHeight >= el.scrollHeight - 4 ? -1 : el.scrollTop
  }
  return taken
})($0)""")>]
let private surfaceScroll (selector: string) : obj = jsNative

// A surface that was NOT on screen before this render starts at its end, which is the other
// half of "content grows from the top and the viewport rides the tail": opening a terminal
// with a history behind it, or switching to one, should show the newest lines and not the
// oldest. It used to fall through to `scrollTop = 0` — invisible while the stream hugged the
// bottom of a short box with `mt-auto`, and plainly wrong the moment the history was longer
// than the box, which is exactly when the anchoring stopped applying.
[<Emit("""(function (selector, positions) {
  const key = el => el.getAttribute('data-terminal-id') || 'chat'
  for (const el of document.querySelectorAll(selector)) {
    const position = positions[key(el)]
    el.scrollTop = position === undefined || position < 0 ? el.scrollHeight : position
  }
})($0, $1)""")>]
let private restoreSurfaceScroll (selector: string) (positions: obj) : unit = jsNative

// A RENDER is not the only thing that moves the end of one of those surfaces away from the
// reader — a RESIZE does it too, and on a phone the viewport is not a constant: the
// browser's toolbars come and go, the device turns. The shell is the visible viewport's
// height (`Style.app`), so each of those shortens the timeline's box while its `scrollTop`
// stays exactly where it was, and somebody who was at the end of the conversation is left a
// line and a half short of it — the last thing said, cut in half, just above the composer.
//
// Whether they were at the end has to be sampled BEFORE the box changes (by the time the
// resize handler runs the measurement would always say "no"), so it rides the scroll event —
// captured, because scroll does not bubble, and the element is Lit's to replace.
[<Emit("""(function (selector) {
  const sel = selector
  const atEnd = el => el.scrollTop + el.clientHeight >= el.scrollHeight - 4
  const pinned = new WeakMap()
  document.addEventListener('scroll', e => {
    const el = e.target
    if (el instanceof Element && el.matches(sel)) pinned.set(el, atEnd(el))
  }, true)
  window.addEventListener('resize', () => {
    for (const el of document.querySelectorAll(sel)) {
      if (pinned.get(el) !== false) el.scrollTop = el.scrollHeight
    }
  })
})($0)""")>]
let private keepSurfacesPinned (selector: string) : unit = jsNative

// A native <input> has no per-character DOM geometry, so we measure the pixel offset of a
// substring with a canvas using the input's own font. Given a peer's decoded selection
// (`anchor`,`head` indices), size its highlight span to `lo..hi` and offset the caret bar to
// `head`. Colour is set by the view (`PeerColour`); this only positions. Called per Title peer
// after every render — the DOM is up to date synchronously.
//
// Everything the marker needs is READ OFF THE FIELD, never assumed from the stylesheet: the
// marker is a sibling of the input inside the title block, and where the input's text sits in
// that block is a function of the input's own offset, padding and content box. The title is a
// 28/32 heading at one width and a 19/24 pivot at the other, and its padding is spent outward
// so a fill can appear without moving a glyph — a marker placed from constants would be right
// at exactly one of those and silently wrong at the rest.
[<Emit("""(function(peer, a, h){
  const input = document.querySelector('input[data-session-title]')
  const marker = document.querySelector('[data-cursor-peer="' + peer + '"]')
  if (!input || !marker) return
  const cs = getComputedStyle(input)
  const canvas = (window.__yTitleCanvas || (window.__yTitleCanvas = document.createElement('canvas')))
  const ctx = canvas.getContext('2d')
  ctx.font = cs.font && cs.font.trim() ? cs.font : (cs.fontStyle + ' ' + cs.fontWeight + ' ' + cs.fontSize + ' ' + cs.fontFamily)
  const value = input.value || ''
  const clamp = (i) => Math.max(0, Math.min(value.length, i | 0))
  const lo = Math.min(clamp(a), clamp(h)), up = Math.max(clamp(a), clamp(h)), head = clamp(h)
  const px = (v) => parseFloat(v) || 0
  const padLeft = px(cs.paddingLeft), padTop = px(cs.paddingTop), scroll = input.scrollLeft || 0
  const left = input.offsetLeft + px(cs.borderLeftWidth) + padLeft
  const top = input.offsetTop + px(cs.borderTopWidth) + padTop
  const height = input.clientHeight - padTop - px(cs.paddingBottom)
  const xOf = (i) => left + ctx.measureText(value.slice(0, i)).width - scroll
  const loX = xOf(lo)
  marker.style.left = loX + 'px'
  marker.style.top = top + 'px'
  marker.style.height = height + 'px'
  marker.style.width = Math.max(0, xOf(up) - loX) + 'px'
  if (marker.firstElementChild) marker.firstElementChild.style.left = (xOf(head) - loX) + 'px'
})($0, $1, $2)""")>]
let private placeTitleCursor (peer: string) (anchor: int) (head: int) : unit = jsNative

[<Emit("requestAnimationFrame(() => $0())")>]
let internal raf (f: unit -> unit) : unit = jsNative

// An armed deadline: something is true NOW and only worth saying if it is still true then
// (see `syncCatchUpTimer`). Nothing debounces on it any more — what needs pacing is paced by
// the frame (`raf`).
[<Emit("setTimeout($0, $1)")>]
let internal setTimeoutJs (f: unit -> unit) (ms: int) : float = jsNative
[<Emit("clearTimeout($0)")>]
let internal clearTimeoutJs (handle: float) : unit = jsNative

/// How long catch-up must run before it is worth SAYING (see `EventConsumerState.CatchUpIsSlow`).
/// Long enough that a send — which puts this client one event behind itself for a round trip —
/// never lights it; short enough that a real wait is reported rather than sat through in
/// silence.
let private catchUpQuietMs = 500

// --- Rich-text editor mount ------------------------------------------------------------
// The view renders empty `[data-rich-body="<key>"]` hosts; the editor is mounted imperatively
// into each, bound to the body's live Y.XmlFragment (resolved from the BodyRegistry).
// ProseMirror owns that DOM; Lit leaves the static host's children alone across re-renders
// (as it preserved the textareas).
[<Emit("Array.from(document.querySelectorAll('[data-rich-body]'))")>]
let private richBodyHosts () : obj[] = jsNative

[<Emit("$0.getAttribute('data-rich-body')")>]
let private hostBodyKey (el: obj) : string = jsNative

[<Emit("$0.getAttribute('data-rich-readonly') === 'true'")>]
let private hostReadOnly (el: obj) : bool = jsNative

// --- Terminal command lines (Plan 13) --------------------------------------------------
// The view renders `<input data-terminal-input="<key>">` for each terminal composer slot and
// each queued command; the value is bound imperatively to that key's `Y.Text` root, the same
// arrangement the rich bodies use one level up. An `<input>` rather than an editor because a
// command is characters, and the CRDT merge happens per character either way.

[<Emit("Array.from(document.querySelectorAll('[data-terminal-input]'))")>]
let private terminalInputs () : obj[] = jsNative

[<Emit("$0.getAttribute('data-terminal-input')")>]
let private terminalInputKey (el: obj) : string = jsNative

[<Emit("$0.readOnly === true")>]
let private terminalInputReadOnly (el: obj) : bool = jsNative

[<Emit("$0.value")>]
let private inputValue (el: obj) : string = jsNative

/// Set an input's value while keeping the caret where the person left it. A remote edit
/// re-renders the value under a focused input, and `el.value = …` resets the selection to
/// the end — which is a collaborator's keystroke throwing your cursor across the line.
/// Offsets are clamped, so a shorter value cannot leave the caret past the end.
// The locals are `__y`-prefixed for a reason that cost an afternoon: Fable substitutes
// `$0` with the ARGUMENT'S OWN IDENTIFIER, so a template that declares `const el = $0`
// against an F# value also called `el` emits `let el = el` — a temporal-dead-zone
// self-reference that throws at the first call. Names that no F# binding will ever have
// make the substitution safe whatever the call site is called.
[<Emit("""(function (el, value) {
  const __yInput = el, __yNext = value;
  if (__yInput.value === __yNext) return;
  const __yFocused = document.activeElement === __yInput;
  const __yStart = __yFocused ? __yInput.selectionStart : null;
  const __yEnd = __yFocused ? __yInput.selectionEnd : null;
  __yInput.value = __yNext;
  if (__yFocused && __yStart !== null) {
    const __yLimit = __yNext.length;
    __yInput.setSelectionRange(Math.min(__yStart, __yLimit), Math.min(__yEnd, __yLimit));
  }
})($0, $1)""")>]
let private setInputValue (el: obj) (value: string) : unit = jsNative

/// Attach a listener once. The flag lives on the element, so a Lit re-render that reuses the
/// same element does not stack a second handler on it — and one that creates a fresh element
/// gets its own.
///
/// Because it is once, the handlers passed here must decide from the ELEMENT what they are
/// acting on: an input Lit hands to a second terminal is this same element with a new
/// `data-terminal-input`, and these listeners are the ones it keeps.
///
/// Enter RUNS the command, the same bargain the message composer strikes (`Editor`'s keymap).
/// A command line is one line, so there is no new line for Alt-Enter to insert and none is
/// bound. `isComposing` guards the IME: mid-composition Enter commits the candidate word, and
/// running a half-typed command because someone accepted a suggestion is not a thing to do.
[<Emit("""(function (el, onInput, onSelect, onBlur, onEnter) {
  const __yBind = el;
  if (__yBind.__yessionBound) return false;
  __yBind.__yessionBound = true;
  __yBind.addEventListener('input', onInput);
  __yBind.addEventListener('keyup', onSelect);
  __yBind.addEventListener('click', onSelect);
  __yBind.addEventListener('select', onSelect);
  __yBind.addEventListener('focus', onSelect);
  __yBind.addEventListener('blur', onBlur);
  __yBind.addEventListener('keydown', (e) => {
    if (e.key === 'Enter' && !e.isComposing) { e.preventDefault(); onEnter() }
  });
  return true;
})($0, $1, $2, $3, $4)""")>]
let private bindTerminalInput
    (el: obj)
    (onInput: unit -> unit)
    (onSelect: unit -> unit)
    (onBlur: unit -> unit)
    (onEnter: unit -> unit)
    : bool = jsNative

[<Emit("(function (el) { return (el && typeof el.selectionStart === 'number') ? [el.selectionStart, el.selectionEnd] : null })($0)")>]
let private inputSelection (el: obj) : (int * int) option = jsNative

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
      ReportFocus : Focus option -> unit
      /// The screen this peer is typing into changed size.
      ResizeTerminal : TerminalId -> int -> int -> unit
      /// A keyframe an open tab needs, by address.
      Http : Client.HttpGet }

/// Everything the render is composed of.
type Deps =
    { Doc : Y.Doc
      Registry : BodyRegistry
      Texts : TextRegistry
      PeerId : PeerId
      /// Where the view goes. Lit diffs into it; whatever is there on the first render stays.
      Root : obj
      Actions : ViewActions
      Dispatch : ClientMsg -> unit
      Links : Links }

/// The render, and the two things beside it that need what it keeps.
type Renderer =
    { /// The page for this model. Everything above happens, in order, synchronously.
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
    // top-level doc root keyed by BodyKey, so the editor and the Session Process bind the
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
                // Enter sends — but only from a DRAFT, which is the only body with a send.
                // A queued message is edited in place and has nothing to commit, so it
                // keeps plain Enter (and Alt-Enter never has to be learned there).
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
                mountedBodies.[key] <-
                    (fragment, readOnly, Editor.mountEditor host fragment readOnly reportFocus onSubmit placeholder)
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
                let key = terminalInputKey el
                if isNull (box key) || key = "" then None
                // A read-only line (a collaborator's slot) still shows live text; it just
                // never writes back, and never claims a caret.
                elif terminalInputReadOnly el then None
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
                        let root = box (texts.Text key)
                        let enc i = ProseMirror.relPosFromTypeIndex root i |> ProseMirror.encodeRel
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
            bindTerminalInput
                el
                (fun () -> lineOf () |> Option.iter (fun key -> TerminalText.setTo texts key (inputValue el)))
                reportFocus
                (fun () -> sendFocus None)
                onEnter
            |> ignore
            let key = terminalInputKey el
            if not (isNull (box key)) && key <> "" then setInputValue el (TerminalText.read texts key)

    /// Fetch the keyframes the open tabs need, once each (Plan 14, stage 4). A keyframe
    /// is immutable at a position that never moves, so the browser cache serves the
    /// second read — this set only stops a burst of identical in-flight requests while
    /// the first one is still out.
    let keyframesAsked = System.Collections.Generic.HashSet<string> ()

    let syncKeyframes (model: ClientModel) =
        for tab in ClientModel.paneTabs model do
            match ClientModel.missingKeyframe tab model with
            | None -> ()
            | Some (terminal, seq) ->
                let key = sprintf "%s@%d" (TerminalId.value terminal) seq
                if keyframesAsked.Add key then
                    Async.StartImmediate (
                        async {
                            let url = SessionRoute.relative (TerminalKeyframe (TerminalId.value terminal, seq))
                            match! deps.Links.Http url with
                            // A keyframe that does not answer is not a failure: the range
                            // still rebases and still plays, as the naive slice. Asking
                            // again on every render would be a spin with nothing to gain.
                            | Error _ -> ()
                            | Ok answer ->
                                match Codec.fromString Codec.transcriptKeyframe answer.Body with
                                | Ok keyframe -> dispatch (TerminalKeyframeMsg (terminal, keyframe))
                                | Error _ -> ()
                        })

    let replays = PaneReplays.create dispatch

    /// The live screens (Plan 14, stage 6): one emulator per terminal this client has a
    /// snapshot for, folded forward from the records the model already holds — and the
    /// size of the one this peer is typing into, relayed to the pty.
    let screens = Screens.create dispatch deps.Links.ResizeTerminal

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
            model.Presence
            |> Map.toList
            |> List.filter (fun (_, p) -> p.Focus.Field = field)
            |> List.map (fun (peerId, p) ->
                ({ Colour = PeerColour.ofPeer peerId
                   Selection = PeerColour.translucent peerId
                   Name = p.DisplayName
                   Anchor = p.Focus.Pos.Anchor
                   Head = p.Focus.Pos.Head } : Editor.RemoteBodyCursor))
        | _ -> []

    // Catch-up is the normal state for a moment after anything happens — your own send
    // puts you behind your own event until the page comes back — so the status is armed
    // rather than mirrored: a timer starts when catch-up begins and only if it is STILL
    // running when the timer fires does the UI say so. Without this the header flickered
    // "up to date" → "catching up" → "up to date" on every message sent, which reads as a
    // fault. Disarmed the moment catch-up ends, and the reducer refuses a late `true`
    // anyway (`CatchUpSlowMsg`), so a fire that races a landing page changes nothing.
    let mutable catchUpTimer = 0.0
    let syncCatchUpTimer (model: ClientModel) =
        let consumer = model.EventConsumer
        if consumer.IsCatchingUp && not consumer.CatchUpIsSlow then
            // Idempotent: an armed timer is left to run, or a stream of pages would keep
            // pushing the deadline out and it would never fire.
            if catchUpTimer = 0.0 then
                catchUpTimer <-
                    setTimeoutJs
                        (fun () ->
                            catchUpTimer <- 0.0
                            dispatch (CatchUpSlowMsg true))
                        catchUpQuietMs
        elif catchUpTimer <> 0.0 then
            clearTimeoutJs catchUpTimer
            catchUpTimer <- 0.0

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

    /// Place collaborators' title carets by measurement (native inputs have no per-character
    /// geometry): decode each title-focused peer's relative anchor/head against the title
    /// `Y.Text`, then size/offset its marker. A no-op when no remote caret is in the title.
    let placeTitleCursorsAll (model: ClientModel) =
        for (peerId, p) in Map.toList model.Presence do
            if p.Focus.Field = Title then
                match ProseMirror.absIndexInDoc doc p.Focus.Pos.Anchor, ProseMirror.absIndexInDoc doc p.Focus.Pos.Head with
                | Some a, Some h -> placeTitleCursor (PeerId.value peerId) a h
                | _ -> ()

    // Render the Lit view on every model change. Lit diffs into the root, so the focused
    // textarea and its caret survive; only the timeline scroll is restored by hand.
    let setState (model: ClientModel) =
        countRender ()
        latest <- Some model
        let scroll = surfaceScroll PinnedSurfaces
        Lit.render (unbox deps.Root) (View.view deps.Actions model dispatch)
        restoreSurfaceScroll PinnedSurfaces scroll
        // Mount/dispose the rich editors on their body hosts (bound to live fragments), then
        // overlay collaborators' cursors: remote carets in each body editor, and title carets
        // measured against the just-rendered input.
        syncRichBodies ()
        syncTerminalInputs ()
        syncKeyframes model
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
        // Where the chapter rail's strokes stand, measured against the conversation this
        // render just wrote. Here rather than a frame later: a stroke reads its position
        // from a custom property, and a frame with none written is a frame of hairlines
        // stacked on the rail's foot.
        RailSync.sync ()
        // Keep a slot rule running for every open terminal: a person may be mid-command
        // in more than one, and each slot follows its own command line.
        syncTerminalSlots model
        syncCatchUpTimer model
        pushPresences ()
        placeTitleCursorsAll model
        // The tab's name, which lives outside the root and so is the model's to push rather
        // than Lit's to render. The NAME is computed in the model (`tabTitle`); this only
        // applies it, and only on a change — assigning `document.title` every render is a
        // write the browser need not be asked to make.
        let tab = ClientModel.tabTitle model
        if Browser.Dom.document.title <> tab then Browser.Dom.document.title <- tab

    { SetState = setState
      SyncTerminalInputs = syncTerminalInputs
      Screens = screens }

/// The listeners that belong with the render and are bound once per page: renders keep the
/// reader's place (`setState`), and this keeps it across the other thing that moves it, a
/// viewport that changed size under a laid-out surface; the split between the two columns is
/// the reader's to set, not the theme's; and the rail follows the conversation while it moves
/// under it — which scrolling and a resized window both do without changing a thing in the
/// model.
let attach () : unit =
    keepSurfacesPinned PinnedSurfaces
    PaneShell.installPaneResize ()
    RailSync.watch ()
