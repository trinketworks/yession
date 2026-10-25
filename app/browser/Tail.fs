module Yession.Browser.Tail

// The surfaces that are read from their END — the chat, a terminal's blocks, a terminal's live
// screen — and the one rule all three keep: a reader who is at the end stays at the end as
// more arrives, and a reader who has scrolled away is left exactly where they are, with a
// "jump to latest" on screen to say there is more below and to take them back.
//
// The rule is about INTENT, and only a reader moves it. "Following" changes when the reader
// scrolls — up and away from the end turns it off, arriving back at the end turns it on — and
// when they open or shut a fold inside the surface, or jump back to the latest. Nothing else
// does: not a render growing the surface, not the box changing size, not a font arriving.
// Each of those moves the end AWAY from a reader who has not moved, and the answer to all of
// them is to put the reader back.
//
// The intent is the MODEL's (`ClientModel.Readers`), and so is every decision about it
// (`TailReader.next`), where a cheap test reaches them. What is here is the document's half:
// it measures where a reader stands, tells the model what moved under them — a fact, and only
// when the fact changes what the model holds, because every message is a render — and after
// each render puts every surface where the intent it was handed says.
//
// It used to be a distance instead. Before a render, a reader within 200px of the end counted
// as "pinned", and after it a pinned reader was moved only if the end was now MORE than 200px
// away — so every command, every chip, every line of a screen that grew a surface by less than
// that left the reader that much short of it, and the shortfall added up render by render until
// it crossed the line. Measured on master: a conversation 80px short with its newest chip half
// under the composer and the jump control hidden, because 80 is less than 200; a terminal's
// blocks 112px short after thirty commands. A wide slack was the right answer to a different
// question (a reader who has only started to scroll away must not be dragged back mid-gesture)
// asked the wrong way: the gesture is visible as the reader MOVING, and a distance cannot tell a
// reader moving away from content arriving.
//
// The live screen had neither half. It was not one of the pinned surfaces at all, so taking the
// keyboard opened it at the top of 16,000px of scrollback, with the prompt being typed at
// nowhere on screen.

open Fable.Core
open Browser.Dom
open Browser.Types
open Fable.BrowserExtras
open Yession.App

/// Every surface read from its end, by the name the view gives it.
let private selector = "[" + Dom.Hooks.tail + "]"

/// The surfaces on the page now. `querySelectorAll` answers a list indexed by number and every
/// caller here walks it.
let private surfaces () : HTMLElement list =
    let found = document.querySelectorAll selector
    [ for i in 0 .. found.length - 1 -> found.[i] :?> HTMLElement ]

/// What a surface IS (`TailSurface.key`), never where it sits in the list: a terminal whose
/// blocks gave way to its screen between two renders has changed which surfaces there are, and
/// an index would put one surface's reader into another.
let private keyOf (el: HTMLElement) : string = el.getAttribute Dom.Hooks.tail

/// Whether the reader is at the end — within a few pixels of it, because a fractional scroll
/// offset over sub-pixel line heights never lands on the bottom exactly. NARROW, and that is
/// the point: "at the end" is where the newest line is fully on screen, and a reader a line
/// short of it is not there.
///
/// The slack also absorbs the pixel the chat's timeline always overflows by, so it bounces
/// on iOS (`Style.timeline`): a short conversation at `scrollTop` 0 is at its end, and is not
/// written down to 1. Narrow the slack below 1px and that has to be answered here.
let private atEnd (el: HTMLElement) : bool =
    el.scrollTop + el.clientHeight >= el.scrollHeight - 4.0

/// Where the reader stands against the end, measured now: there, or short of it by less than
/// its box, or by more (`TailSurface.farBehind`).
let private placeOf (el: HTMLElement) : TailPlace =
    if atEnd el then TailPlace.AtEnd
    elif TailSurface.farBehind (el.scrollHeight - el.scrollTop - el.clientHeight) el.clientHeight then TailPlace.Far
    else TailPlace.Near

/// Put a following reader back at the end, and only when they are not there already. A write
/// to `scrollTop` — of the value it already holds included — ends whatever scroll the browser
/// has in flight, in Chromium and WebKit alike, so a write nobody needed is a gesture taken away.
let private toEnd (el: HTMLElement) : unit =
    if not (atEnd el) then el.scrollTop <- el.scrollHeight

// --- What a press opens, it opens UPWARD ------------------------------------------------------
//
// A terminal's history is read from its end, and everything in it is EARLIER than what follows
// it: the fold "ran N earlier commands" holds what came before the commands under it, and a
// block's hidden lines came before the ones it shows. So when a reader presses one of those
// open, what they were looking at is what FOLLOWS it — the latest command, for a reader at the
// end — and the press must not move it. What it opens appears above, in the space the scroll
// makes for it.
//
// The browser's own scroll anchoring cannot be relied on to say that. It keeps the FIRST fully
// visible box where it was, so with the fold's own line on screen it anchored on the fold and
// let everything under it fall down the page — the latest command pushed off the bottom by
// the history it had just asked to see (472px, measured in the browser tier, from the end
// with the fold's line at the top of the pane). Nor does every browser anchor at all. So the
// place is kept here, from the press: the entry pressed in, and where it ENDED.
//
// Which surfaces read this way is the view's to say, on the element whose children are the
// entries (`Dom.Hooks.tailEntries`). The chat does not: its folds open downward, under the
// line pressed, and nothing here touches them.
//
// That is a POINTER's press. A keyboard's is different, because a keyboard reader is looking at
// the control their focus is on, and opening upward takes that control — and the ring that says
// where focus is — off the top of the pane, so Enter appears to do nothing at all. So a press
// from the keyboard keeps the CONTROL where it was instead: what it opens appears below it, and
// the reader stays on the line they pressed. One mechanism either way; what differs is only
// which element's edge is held.

/// A press inside one entry of a surface: the surface, the element whose place is kept, where
/// that element ended in the surface's CONTENT when it was pressed, and the surface's scroll
/// then. `Held` is the entry for a pointer and the pressed control for a keyboard; `ByKeyboard`
/// says which, because a keyboard's press is kept for a reader at the end too, who is otherwise
/// put back at the end.
type private Press =
    { Surface : string
      Held : Element
      Ended : float
      Top : float
      ByKeyboard : bool }

/// Where an element ends in a surface's content: its bottom edge measured from the top of
/// everything the surface scrolls rather than from its box, so the reader's own scrolling does
/// not move it and only a change of what is ABOVE that edge does.
let private endIn (surface: HTMLElement) (el: Element) : float =
    el.getBoundingClientRect().bottom - surface.getBoundingClientRect().top + surface.scrollTop

/// The surface and the entry a press landed in, when it landed inside a list of entries
/// (`Dom.Hooks.tailEntries`) that is inside a surface.
let private pressedIn (target: Node) : (HTMLElement * Element) option =
    let rec listOf (node: Node) : HTMLElement option =
        match node.parentElement with
        | null -> None
        | parent when parent.hasAttribute Dom.Hooks.tailEntries -> Some parent
        | parent -> listOf parent
    listOf target
    |> Option.bind (fun list ->
        let entries = list.children
        let entry = [ for i in 0 .. entries.length - 1 -> entries.[i] ] |> List.tryFind (fun e -> e.contains target)
        match entry, list.closest selector with
        | Some entry, Some surface -> Some (surface :?> HTMLElement, entry)
        | _ -> None)

/// Where one surface's scroll stood as a render began, and whether it had gone UP since
/// anything here last looked — the reader leaving, seen before the scroll event that will
/// report it (`following`).
type private Stood = { Top : float; Leaving : bool }

/// Where each surface's scroll stood, taken before a render moves anything.
type Before = private Before of Map<string, Stood>

/// The document's half of the tail rule, for one page.
type Tail =
    { /// Take where each surface stands, before a render moves anything.
      Before : unit -> Before
      /// Every surface put where the intent of the model just rendered says, once the render
      /// and everything after it has finished moving them.
      Restore : Before -> ClientModel -> unit
      /// The listeners that hear a reader move, bound once per page.
      Attach : unit -> unit }

/// The tail rule's document half: it tells the model through `dispatch`, and asks `current`
/// for the LATEST model, not the one a render was handed, both to say whether a reader is
/// following between renders (a scroll, a box resized) and so that a fact the model already
/// holds is not told again.
let create (dispatch: ClientMsg -> unit) (current: unit -> ClientModel option) : Tail =

    /// Where each surface's scroll stood the last time anything here looked, by name rather
    /// than by element, because Lit replaces elements. A MEASUREMENT, kept so that a scroll
    /// UP, the one thing that means the reader is leaving, can be told apart from a scroll
    /// down or none; whether the reader is following is the model's.
    let tops = System.Collections.Generic.Dictionary<string, float> ()

    /// The readers of the model the last render was handed, and only to tell a reader the
    /// model has just made follow from one who already was (`Restore`).
    let mutable shown : Map<TailSurface, Behind> = Map.empty

    /// The press the next render is owed, if there is one. Rendering takes it (`Restore`), so
    /// a press nothing re-rendered for is not kept past the render after it. Elements rather
    /// than a fact for the model: what it holds is the press's place on the page, and the
    /// render that opens what was pressed is what it is spent on.
    let mutable pressed : Press option = None

    let surfaceOf (el: HTMLElement) : TailSurface option = TailSurface.ofKey (keyOf el)

    /// Whether the scroll has gone up since it was last seen, and not to the end: a surface
    /// that SHRANK under its reader moves their scroll up too, and that is not leaving.
    let leaving (el: HTMLElement) : bool =
        match tops.TryGetValue (keyOf el) with
        | true, top -> el.scrollTop < top - 1.0 && not (atEnd el)
        | false, _ -> false

    /// Whether this surface's reader is following its end, asked NOW.
    ///
    /// The model's answer, unless the scroll has gone up since it was last seen: a scroll event
    /// arrives a frame after the scroll it reports, and a render or a resize landing inside
    /// that frame would otherwise see a reader who has just started to leave as one who is
    /// still following, and put them back — ending the gesture they were in the middle of. A
    /// surface the model holds nothing for is followed: one nobody has looked at yet is one
    /// somebody is arriving at, and arriving is at the end — the newest thing, not the oldest.
    let following (model: ClientModel) (el: HTMLElement) : bool =
        let intends =
            match surfaceOf el with
            | Some surface -> ClientModel.following surface model
            | None -> true
        intends && not (leaving el)

    /// Tell the model what moved under a surface's reader — only when that moves what it
    /// holds, the reader's intent or how far back they are (`TailReader.next`), so a reader
    /// scrolling within the near region or the far one renders nothing.
    ///
    /// A surface with no box says nothing. An element the browser is not laying out
    /// (`display: none`, or a column with no height) reads `scrollTop`, `scrollHeight` and
    /// `clientHeight` as zero, and zero short of zero is "at the end" — so measuring one told
    /// the model a reader two hundred messages back had arrived. Chromium fires a scroll event
    /// as the box goes, so this was not a corner `restore` alone could guard. The reader's
    /// place is where they left it, and a surface that comes back to its box reports from
    /// there.
    let tell (el: HTMLElement) (move: ReaderMove) : unit =
        match surfaceOf el, current () with
        | Some surface, Some model when el.clientHeight > 0.0 ->
            let now = Map.tryFind surface model.Readers
            if TailReader.next move now <> now then dispatch (ReaderMovedMsg (surface, move))
        | _ -> ()

    /// The ONE observer, of every surface's box. A box changes without a render — a phone's
    /// toolbars come and go, the device turns, the pane opens beside the chat and narrows it,
    /// the splitter is dragged — and each of those moves the end away from a reader whose
    /// `scrollTop` stayed exactly where it was: the last thing said, cut in half above the
    /// composer.
    let observer : ResizeObserver option =
        if ResizeObserver.isSupported () then
            Some (
                ResizeObserver.create (fun () ->
                    match current () with
                    | Some model ->
                        for el in surfaces () do
                            if following model el then
                                toEnd el
                                tops.[keyOf el] <- el.scrollTop
                    | None -> ()))
        else None

    /// What the observer is watching, so a render that drew the same surfaces does not start
    /// it again (observing fires once immediately, and that would be a pass per render for
    /// nothing), and one that dropped a surface does not leave the observer holding it.
    let mutable observed : HTMLElement list = []

    let observe (current: HTMLElement list) : unit =
        let same =
            List.length current = List.length observed
            && List.forall2 (fun a b -> System.Object.ReferenceEquals (a, b)) current observed
        if not same then
            observer
            |> Option.iter (fun observer ->
                observer.disconnect ()
                for el in current do
                    observer.observe (el :> Element))
            observed <- current

    /// Every surface put back where its reader was, once the render and everything after it —
    /// the editors mounted, the pane opened, the screens folded — has finished moving them.
    ///
    /// Last rather than straight after the diff because those moves change heights: a restore
    /// that ran before them measured an end that was not the end any more by the time anybody
    /// looked.
    ///
    /// A reader the model holds as following is put at the end. So is a surface that was NOT
    /// on the page before this render, which is the other half of "the newest thing, not the
    /// oldest": switching to a terminal, or taking its keyboard so its screen replaces its
    /// blocks, opens at the end — and the model is told it arrived. A reader who was not
    /// following is put back where they were, written only when the render moved them (Lit
    /// replaced the element and the new one starts at zero), for `toEnd`'s reason.
    ///
    /// A reader the model has only now made follow — a jump back, a send — from less than a
    /// screen away is GLIDED there, so they see which way they went, unless they have asked
    /// for less motion. From further back they are put there at once: a glide across many
    /// screens is motion for nothing. A render landing mid-glide finds a follower and finishes
    /// the trip at once, rather than leaving the glide to stop short of an end that has moved.
    ///
    /// Except by what a press of theirs opened (`Press`): the element held keeps its END where
    /// it was on screen. Measured in the surface's content and added to where the scroll stood
    /// at the press, so whatever the browser's own anchoring did during the render is neither
    /// counted nor fought. A pointer's press at the end needs none of that — the end is what
    /// they were looking at, and the end is where they are put. A keyboard's press at the end
    /// is held like any other, and leaves the reader following only if that is still the end.
    ///
    /// What moved is told after every surface is placed: telling renders, and a render
    /// rebuilds what this walks.
    let restore (Before positions) (model: ClientModel) : unit =
        let present = surfaces ()
        let press = pressed
        pressed <- None
        tops.Clear ()
        let moves =
            [ for el in present do
                  let key = keyOf el
                  let kept =
                      match press with
                      | Some press when press.Surface = key && el.contains press.Held ->
                          Some (press, press.Top + (endIn el press.Held - press.Ended))
                      | Some _
                      | None -> None
                  let intends =
                      match surfaceOf el with
                      | Some surface -> ClientModel.following surface model
                      | None -> true
                  match Map.tryFind key positions, kept with
                  | Some stood, _ when not intends || stood.Leaving ->
                      let kept = kept |> Option.map snd |> Option.defaultValue stood.Top
                      if el.scrollTop <> kept then el.scrollTop <- kept
                      tops.[key] <- el.scrollTop
                      // Seen leaving before its scroll event said so: that is the reader's
                      // scroll, and the event that follows will find nothing left to report.
                      yield el, (if stood.Leaving then ReaderMove.Scrolled (true, placeOf el) else ReaderMove.Measured (placeOf el))
                  | _, Some (press, kept) when press.ByKeyboard ->
                      if el.scrollTop <> kept then el.scrollTop <- kept
                      tops.[key] <- el.scrollTop
                      yield el, ReaderMove.Opened (placeOf el)
                  | Some _, _ ->
                      let returning =
                          surfaceOf el |> Option.bind (fun surface -> Map.tryFind surface shown) = Some Behind.Near
                      if returning && not (atEnd el) && not (mediaMatches "(prefers-reduced-motion: reduce)") then
                          scrollToBottomSmooth el
                      else toEnd el
                      tops.[key] <- el.scrollTop
                  | None, _ ->
                      toEnd el
                      tops.[key] <- el.scrollTop
                      yield el, ReaderMove.Arrived ]
        observe present
        shown <- model.Readers
        for el, move in moves do
            tell el move

    /// Bound once per page.
    ///
    /// Scroll: captured, because scroll does not bubble and the surfaces are Lit's to replace.
    /// Up and away from the end is leaving; reaching the end is coming back; anything else
    /// keeps what was there — including a scroll down that stops short, which is a reader on
    /// their way back, or a glide `Restore` started (`TailReader.next` decides).
    ///
    /// Toggle: a fold opened or shut inside a surface moves everything below it with no scroll
    /// at all. A reader who opened one is reading it, and is following the end afterwards only
    /// if the end is still where they are.
    ///
    /// Click: captured, so it is heard BEFORE the control pressed dispatches what it opens —
    /// the render that opens it has to find the place already taken (`Press`). A key that
    /// presses a control is a click too, and the click says which it was: one a key made
    /// counts no clicks (`detail` is 0), one a pointer made counts at least one.
    let attach () : unit =
        document.addEventListener (
            "click",
            Events.mouse (fun event ->
                match EventTargets.asNode event.target |> Option.bind pressedIn with
                | Some (surface, entry) ->
                    let byKeyboard = event.detail = 0.0
                    let held =
                        match EventTargets.asHTMLElement event.target with
                        | Some control when byKeyboard -> control :> Element
                        | Some _
                        | None -> entry
                    pressed <-
                        Some
                            { Surface = keyOf surface
                              Held = held
                              Ended = endIn surface held
                              Top = surface.scrollTop
                              ByKeyboard = byKeyboard }
                | None -> ()),
            true)
        document.addEventListener (
            "scroll",
            (fun event ->
                match EventTargets.asHTMLElement event.target with
                | Some el when el.matches selector ->
                    let up = leaving el
                    tops.[keyOf el] <- el.scrollTop
                    tell el (ReaderMove.Scrolled (up, placeOf el))
                | _ -> ()),
            true)
        document.addEventListener (
            "toggle",
            (fun event ->
                match EventTargets.asHTMLElement event.target with
                | Some folded ->
                    match folded.closest selector with
                    | Some surface ->
                        let el = surface :?> HTMLElement
                        tops.[keyOf el] <- el.scrollTop
                        tell el (ReaderMove.Opened (placeOf el))
                    | None -> ()
                | None -> ()),
            true)

    { Before =
        fun () ->
            surfaces ()
            |> List.map (fun el -> keyOf el, { Top = el.scrollTop; Leaving = leaving el })
            |> Map.ofList
            |> Before
      Restore = restore
      Attach = attach }
