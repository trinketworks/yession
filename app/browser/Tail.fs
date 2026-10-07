module Yession.Browser.Tail

// The surfaces that are read from their END — the chat, a terminal's blocks, a terminal's live
// screen — and the one rule all three keep: a reader who is at the end stays at the end as
// more arrives, and a reader who has scrolled away is left exactly where they are, with a
// "jump to latest" on screen to say there is more below and to take them back.
//
// The rule is about INTENT, and only a reader moves it. "Following" changes when the reader
// scrolls — up and away from the end turns it off, arriving back at the end turns it on — and
// when they open or shut a fold inside the surface. Nothing else does: not a render growing the
// surface, not the box changing size, not a font arriving. Each of those moves the end AWAY from
// a reader who has not moved, and the answer to all of them is to put the reader back.
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
let private atEnd (el: HTMLElement) : bool =
    el.scrollTop + el.clientHeight >= el.scrollHeight - 4.0

/// One surface's reader: whether they are following its end, and where its scroll stood the
/// last time anything here looked — so that a scroll UP, the one thing that means the reader
/// is leaving, can be told apart from a scroll down or none.
type private Reader = { Following : bool; Top : float }

/// By name rather than by element, because Lit replaces elements: a reader who was following
/// the chat is following the chat whichever node draws it this render.
let private readers = System.Collections.Generic.Dictionary<string, Reader> ()

/// Whether this surface's reader is following its end, asked NOW.
///
/// The stored answer, unless the scroll has gone up since it was stored: a scroll event arrives
/// a frame after the scroll it reports, and a render landing inside that frame would otherwise
/// see a reader who has just started to leave as one who is still following, and put them
/// back — ending the gesture they were in the middle of. At the end counts whatever the stored
/// answer, because a surface that SHRANK under its reader moves their scroll up too.
let private following (el: HTMLElement) : bool =
    match readers.TryGetValue (keyOf el) with
    | true, reader -> reader.Following && (el.scrollTop >= reader.Top - 1.0 || atEnd el)
    // A surface nobody has looked at yet is one somebody is arriving at, and arriving is at
    // the end: the newest thing, not the oldest.
    | false, _ -> true

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

/// The press the next render is owed, if there is one. Rendering takes it (`restore`), so a
/// press nothing re-rendered for is not kept past the render after it.
let mutable private pressed : Press option = None

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

/// Where whether each reader is following goes: the model, which draws each surface's "jump
/// to latest" from it (`ClientModel.Away`). Set once, by `attach`.
let mutable private tell : ClientMsg -> unit = ignore

/// What the model was last told about each surface. A surface it has not been told about is
/// one it takes to be followed, which is where every reader arrives.
let private told = System.Collections.Generic.Dictionary<string, bool> ()

/// Tell the model about every reader whose following has changed since it was last told —
/// the same answer the scroll is kept by, so the control is on screen exactly when the surface
/// is not being kept at its end. Gathered first and told after, because telling renders, and
/// a render rebuilds `readers`.
let private report () : unit =
    let changed =
        [ for KeyValue (key, reader) in readers do
              let was = match told.TryGetValue key with | true, following -> following | false, _ -> true
              if was <> reader.Following then yield key, reader.Following ]
    for key, following in changed do
        told.[key] <- following
        TailSurface.ofKey key |> Option.iter (fun surface -> tell (ReaderMovedMsg (surface, following)))

/// Where each surface's reader stands, taken before a render moves anything.
type Before = private Before of Map<string, float option>

/// Take it: per surface, `None` for a reader following the end, `Some top` for one who is not.
let before () : Before =
    surfaces ()
    |> List.map (fun el -> keyOf el, (if following el then None else Some el.scrollTop))
    |> Map.ofList
    |> Before

/// The ONE observer, of every surface's box. A box changes without a render — a phone's
/// toolbars come and go, the device turns, the pane opens beside the chat and narrows it, the
/// splitter is dragged — and each of those moves the end away from a reader whose `scrollTop`
/// stayed exactly where it was: the last thing said, cut in half above the composer.
let private observer : ResizeObserver option =
    if ResizeObserver.isSupported () then
        Some (
            ResizeObserver.create (fun () ->
                for el in surfaces () do
                    if following el then
                        toEnd el
                        readers.[keyOf el] <- { Following = true; Top = el.scrollTop }))
    else None

/// What the observer is watching, so a render that drew the same surfaces does not start it
/// again (observing fires once immediately, and that would be a pass per render for nothing),
/// and one that dropped a surface does not leave the observer holding it.
let mutable private observed : HTMLElement list = []

let private observe (current: HTMLElement list) : unit =
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
/// Last rather than straight after the diff because those moves change heights: a restore that
/// ran before them measured an end that was not the end any more by the time anybody looked.
///
/// A reader who was following is put at the end. That covers the surface that was NOT on the
/// page before this render too, which is the other half of "the newest thing, not the oldest":
/// switching to a terminal, or taking its keyboard so its screen replaces its blocks, opens at
/// the end. A reader who was not following is put back where they were, written only when the
/// render moved them (Lit replaced the element and the new one starts at zero), for `toEnd`'s
/// reason.
///
/// Except by what a press of theirs opened (`Press`): the element held keeps its END where it
/// was on screen. Measured in the surface's content and added to where the scroll stood at the
/// press, so whatever the browser's own anchoring did during the render is neither counted nor
/// fought. A pointer's press at the end needs none of that — the end is what they were looking
/// at, and the end is where they are put. A keyboard's press at the end is held like any other,
/// and leaves the reader following only if that is still the end.
let restore (Before positions) : unit =
    let current = surfaces ()
    let press = pressed
    pressed <- None
    readers.Clear ()
    for el in current do
        let key = keyOf el
        let kept =
            match press with
            | Some press when press.Surface = key && el.contains press.Held ->
                Some (press, press.Top + (endIn el press.Held - press.Ended))
            | Some _
            | None -> None
        match Map.tryFind key positions |> Option.flatten, kept with
        | Some top, _ ->
            let kept = kept |> Option.map snd |> Option.defaultValue top
            if el.scrollTop <> kept then el.scrollTop <- kept
            readers.[key] <- { Following = false; Top = el.scrollTop }
        | None, Some (press, kept) when press.ByKeyboard ->
            if el.scrollTop <> kept then el.scrollTop <- kept
            readers.[key] <- { Following = atEnd el; Top = el.scrollTop }
        | None, _ ->
            toEnd el
            readers.[key] <- { Following = true; Top = el.scrollTop }
    observe current
    // A frame on rather than now: this runs at the end of a render, and telling is another.
    window.requestAnimationFrame (fun _ -> report ()) |> ignore

/// The surface a `TailSurface` names, if it is on the page.
let private find (surface: TailSurface) : HTMLElement option =
    match document.querySelector (sprintf "[%s=\"%s\"]" Dom.Hooks.tail (TailSurface.key surface)) with
    | null -> None
    | el -> Some (el :?> HTMLElement)

/// Take the reader to the end of a surface and follow it from there — a sent message, and the
/// "jump to latest" press.
///
/// Following is set BEFORE the scroll gets there, which is what lets the animation survive
/// what arrives while it runs: every frame of it moves down, which never reads as leaving, and
/// a render landing mid-way finds a follower and finishes the trip at once rather than leaving
/// the glide to stop short of an end that has moved. Animated unless the reader has asked for
/// less motion.
let follow (surface: TailSurface) : HTMLElement option =
    let found = find surface
    found
    |> Option.iter (fun el ->
        readers.[keyOf el] <- { Following = true; Top = el.scrollTop }
        if mediaMatches "(prefers-reduced-motion: reduce)" then el.scrollTop <- el.scrollHeight
        else scrollToBottomSmooth el
        report ())
    found

/// The listeners that hear a READER move, bound once per page.
///
/// Scroll: captured, because scroll does not bubble and the surfaces are Lit's to replace. Up
/// and away from the end is leaving; reaching the end is coming back; anything else keeps what
/// was there — including a scroll down that stops short, which is a reader on their way back,
/// or the glide `follow` started.
///
/// Toggle: a fold opened or shut inside a surface moves everything below it with no scroll at
/// all. A reader who opened one is reading it, and is following the end afterwards only if
/// the end is still where they are.
///
/// Click: captured, so it is heard BEFORE the control pressed dispatches what it opens — the
/// render that opens it has to find the place already taken (`Press`). A key that presses a
/// control is a click too, and the click says which it was: one a key made counts no clicks
/// (`detail` is 0), one a pointer made counts at least one.
let attach (dispatch: ClientMsg -> unit) : unit =
    tell <- dispatch
    document.addEventListener (
        "click",
        (fun event ->
            match EventTargets.asNode event.target |> Option.bind pressedIn with
            | Some (surface, entry) ->
                // A click event is a `MouseEvent`: the one cast, on the line that reads it.
                let byKeyboard = (event :?> MouseEvent).detail = 0.0
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
                let key = keyOf el
                let top = el.scrollTop
                let was =
                    match readers.TryGetValue key with
                    | true, reader -> reader
                    | false, _ -> { Following = true; Top = top }
                let now =
                    if atEnd el then true
                    elif top < was.Top - 1.0 then false
                    else was.Following
                readers.[key] <- { Following = now; Top = top }
                if now <> was.Following then report ()
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
                    readers.[keyOf el] <- { Following = atEnd el; Top = el.scrollTop }
                    report ()
                | None -> ()
            | None -> ()),
        true)
