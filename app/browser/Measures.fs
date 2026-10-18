module Yession.Browser.Measures

// The measurements individual browser cases make (`Yession.App.Probes.Measure`), in F#. Each was
// a JavaScript program a case handed the page in a string; here each is a function over the DOM
// the compiler reads, published by `Probes.fs` under the name its case asks for. Bundled by the
// test build alone, with the probes; nothing in the product imports it.

open Fable.Core
open Browser.Types
open Fable.BrowserExtras
open Yession.App

let private document = Browser.Dom.document

/// Where a measurement looks: the whole document, or under one element.
[<RequireQualifiedAccess>]
type private Root =
    | Document
    | Under of Element

let private all (root: Root) (selector: string) : Element list =
    let found =
        match root with
        | Root.Document -> document.querySelectorAll selector
        | Root.Under element -> element.querySelectorAll selector
    [ for i in 0 .. found.length - 1 -> found.[i] ]

let private first (root: Root) (selector: string) : Element option =
    match root with
    | Root.Document -> document.querySelector selector |> Option.ofObj
    | Root.Under element -> element.querySelector selector |> Option.ofObj

let private closest (element: Element) (selector: string) : Element option = element.closest selector

/// Where a box's centre lands once each edge is rounded to the device-pixel grid on its own, as
/// the painter rounds it — the only way to compare two things' painted centres.
let private paintedCentre (box: ClientRect) : float =
    let d = Browser.Dom.window.devicePixelRatio
    (round (box.left * d) + round (box.right * d)) / 2.0

/// The first descendant drawn as a line: one pixel wide, taller than that. Found by its shape,
/// not a class, so restyling the line does not lose it.
let private lineIn (root: Element) : Element option =
    all (Root.Under root) "*"
    |> List.tryFind (fun e ->
        let b = e.getBoundingClientRect ()
        b.width = 1.0 && b.height > 1.0)

/// The line a cause draws inside a note: the first of its `[data-cause-ref]` descendants drawn
/// one pixel wide.
let private lineIn' (note: Element) : Element option =
    all (Root.Under note) "[data-cause-ref] *"
    |> List.tryFind (fun e ->
        let b = e.getBoundingClientRect ()
        b.width = 1.0 && b.height > 1.0)

/// Every text node under `node`, in document order.
let rec private textsUnder (node: Node) : Node list =
    if node.nodeType = 3.0 then [ node ]
    else
        let children = node.childNodes
        [ for i in 0 .. children.length - 1 do yield! textsUnder children.[i] ]

let private runningMarkOnTitle () : string list =
    let note = first Root.Document "#shell [data-act-status=\"running\"]"
    let mark = note |> Option.bind (fun note -> first (Root.Under note) "[data-act-running]")
    // The title, not the cause's sentence above it (which sits in the same column), nor the
    // actions control placed on the title's row.
    let words =
        note
        |> Option.bind (fun note ->
            all (Root.Under note) ".col-start-2"
            |> List.tryFind (fun e -> (closest e "[data-cause-ref]").IsNone && (closest e "[data-item-actions]").IsNone))
        |> Option.bind (fun title -> textsUnder title |> List.tryFind (fun t -> t.textContent.Trim () <> ""))
    match note, mark, words with
    | None, _, _ -> [ "there is no running act" ]
    | _, None, _ -> [ "the running act draws no mark" ]
    | _, _, None -> [ "the title has no words" ]
    | Some _, Some mark, Some words ->
        let gutter = mark.parentElement.getBoundingClientRect ()
        // A zero-wide box one x-height tall, set just before the first letter: its bottom is
        // the title's baseline and its top the letters' x-height.
        let marker = document.createElement "span"
        marker.setAttribute ("style", "display:inline-block;width:0;height:1ex")
        words.parentNode.insertBefore (marker, words) |> ignore
        let letters = marker.getBoundingClientRect ()
        marker.remove ()
        let at = mark.getBoundingClientRect ()
        let size = Css.computedProperty words.parentElement "font-size"
        let em = float (size.Replace ("px", ""))
        [ if abs (at.bottom - letters.bottom) > 0.05 * em then
              sprintf "its lower point is %gpx off the title's baseline" (at.bottom - letters.bottom)
          if abs (at.top - letters.top) > 0.05 * em then
              sprintf "its upper point is %gpx off the title's x-height" (at.top - letters.top)
          if at.left < gutter.left || at.right > gutter.right then
              sprintf "it is outside its gutter (%g-%g against %g-%g)" at.left at.right gutter.left gutter.right ]

let private causeHeadsOnLines () : string list =
    let d = Browser.Dom.window.devicePixelRatio
    let heads =
        all Root.Document "#shell [data-cause-ref] svg, #shell [data-cause-chain] svg"
        |> List.filter (fun svg -> (closest svg "[data-entity]").IsNone)
    if heads.IsEmpty then [ "the page draws no cause heads" ]
    else
        heads
        |> List.collect (fun svg ->
            let mark = svg.parentElement.parentElement
            match lineIn mark with
            | None -> [ "a head with no line" ]
            | Some line ->
                let lineAt = paintedCentre (line.getBoundingClientRect ())
                let box = svg.getBoundingClientRect ()
                match first (Root.Under svg) "path" with
                | None -> [ "a head with no path" ]
                | Some path ->
                    let tip = Svg.bounds path
                    let scale = box.width / Svg.viewBoxWidth svg
                    let tipAt = round (box.left * d) + (tip.x + tip.width / 2.0) * scale * d
                    if abs (tipAt - lineAt) > 0.01 then [ sprintf "a head paints at %g and its line at %g" tipAt lineAt ] else [])

let private runningMarkOnCauseLine () : string list =
    match first Root.Document "#shell [data-act-status=\"running\"]" with
    | None -> [ "there is no running act" ]
    | Some note ->
        match lineIn' note, first (Root.Under note) "[data-act-running]" with
        | None, _ -> [ "the cause draws no line" ]
        | _, None -> [ "the running act draws no mark" ]
        | Some stem, Some mark ->
            let markAt = paintedCentre (mark.getBoundingClientRect ())
            let lineAt = paintedCentre (stem.getBoundingClientRect ())
            if markAt = lineAt then [] else [ sprintf "the mark paints at %g and the line at %g (device pixels)" markAt lineAt ]

let private foldArrowsOnRail () : float list =
    let folds = all Root.Document "#shell [data-fold]"
    let arrows = folds |> List.choose (fun fold -> first (Root.Under fold) "svg" |> Option.map (fun svg -> fold, svg.getBoundingClientRect ()))
    let down =
        arrows
        |> List.choose (fun (fold, arrow) ->
            // The title's FIRST line box, not its whole box: a title that wraps is two lines
            // tall, and the arrow belongs on the first.
            first (Root.Under fold.parentElement) ".col-start-2"
            |> Option.bind (fun title ->
                let lines = title.getClientRects ()
                if lines.Length = 0 then None
                else
                    let line = lines.[0]
                    Some ((arrow.top + arrow.height / 2.0) - (line.top + line.height / 2.0))))
    let rails = arrows |> List.map (fun (_, arrow) -> arrow.left + arrow.width / 2.0)
    [ float folds.Length
      (if down.IsEmpty then 0.0 else down |> List.map abs |> List.max)
      (if rails.IsEmpty then 0.0 else List.max rails - List.min rails) ]

let private foldArrowsOnAvatars () : float list =
    let folds = all Root.Document "#shell section [data-fold]"
    let apart =
        folds
        |> List.choose (fun fold ->
            let avatar = closest fold "section" |> Option.bind (fun section -> first (Root.Under section) "header > span")
            match avatar, first (Root.Under fold) "svg" with
            | Some avatar, Some svg ->
                let a = avatar.getBoundingClientRect ()
                let arrow = svg.getBoundingClientRect ()
                Some ((arrow.left + arrow.width / 2.0) - (a.left + a.width / 2.0))
            | _ -> None)
    [ float folds.Length; (if apart.IsEmpty then 0.0 else apart |> List.map abs |> List.max) ]

/// A zero-sized inline box on the baseline, for reading where a line's baseline is: its bottom.
let private baselineMarker () : HTMLElement =
    let marker = document.createElement "span"
    marker.setAttribute ("style", "display:inline-block;width:0;height:0;vertical-align:baseline")
    marker

/// The baseline of the line `marker` is put at the end of.
let private baselineIn (parent: Element) : float =
    let marker = baselineMarker ()
    parent.appendChild marker |> ignore
    let bottom = marker.getBoundingClientRect().bottom
    marker.remove ()
    bottom

let private referenceBaseline () : float list =
    match first Root.Document "#shell [data-act-note]" with
    | None -> []
    | Some note ->
        match first (Root.Under note) "[data-entity]", first (Root.Under note) "[data-entity] > span:last-child" with
        | Some entity, Some name -> [ abs (baselineIn name - baselineIn entity.parentElement) ]
        | _ -> []

let private paintedAt (element: Element) (x: float) (y: float) : bool =
    Browser.Dom.document.elementFromPoint (x, y) |> Option.ofObj |> Option.exists element.contains

let private grabEdgeShape () : float list =
    match first Root.Document "#shell [data-pane-grab-edge]", first Root.Document "#shell [data-content-panel]" with
    | Some edge, Some pane ->
        let e = edge.getBoundingClientRect ()
        let p = pane.getBoundingClientRect ()
        [ e.left
          e.width
          p.left - e.right
          (if paintedAt edge (e.left + e.width / 2.0) (e.top + e.height / 2.0) then 1.0 else 0.0)
          e.top + e.height / 2.0 ]
    | _ -> []

let private grabEdgeClear () : string list =
    match first Root.Document "#shell [data-pane-grab-edge]" with
    | None -> [ "the pane has no grab edge" ]
    | Some edge ->
        let e = edge.getBoundingClientRect ()
        [ if Css.computedProperty (edge :?> HTMLElement) "opacity" <> "0" then "the edge is still drawn"
          if paintedAt edge (e.left + 2.0) (e.top + e.height / 2.0) then "the edge is still what a press at the screen's edge lands on" ]

let private edgeTabOnBand (desktop: bool) : string list =
    match first Root.Document "#shell [data-content-toggle=\"show\"]", first Root.Document "#shell header" with
    | Some tab, Some header ->
        // The harness mounts the shell below its editors, so it is brought on screen first: a
        // point off screen hits nothing.
        Scrolling.intoView tab "center"
        let at = tab.getBoundingClientRect ()
        let band = header.getBoundingClientRect ()
        let width = Browser.Dom.window.innerWidth
        [ if at.top < band.top || at.bottom > band.bottom || at.left < band.left || at.right > band.right then
              sprintf "outside the header band: %g..%g x %g..%g in %g..%g x %g..%g" at.left at.right at.top at.bottom band.left band.right band.top band.bottom
          if at.left < 0.0 || at.right > width then
              sprintf "outside the viewport: %g..%g of %g" at.left at.right width
          if at.width < 24.0 || at.height < 24.0 then
              sprintf "too small to press: %gx%g" at.width at.height
          if not (paintedAt tab (at.left + at.width / 2.0) (at.top + at.height / 2.0)) then
              "covered at its centre"
          if desktop then
              match first (Root.Under tab) "[data-terminals-count]", first Root.Document "#shell [data-session-title]" with
              | Some words, Some title ->
                  let tabBaseline = baselineIn words
                  // An input's baseline is its text's, and it lends it to a line box only in
                  // flow — so its OFFSET inside the input is read with the wrapper laid out as a
                  // block for a moment, and added to where the input really is.
                  let top = title.getBoundingClientRect().top
                  let wrap = title.parentElement
                  let was = wrap.getAttribute "style"
                  wrap.setAttribute ("style", (match was with null -> "" | was -> was + ";") + "display:block")
                  let theirs = baselineMarker ()
                  title.parentElement.insertBefore (theirs, title.nextSibling) |> ignore
                  let offset = theirs.getBoundingClientRect().bottom - title.getBoundingClientRect().top
                  theirs.remove ()
                  (match was with
                   | null -> wrap.removeAttribute "style"
                   | was -> wrap.setAttribute ("style", was))
                  let drift = tabBaseline - (top + offset)
                  if abs drift > 2.0 then sprintf "off the title baseline by %gpx" drift
              | _ -> "the tab's words or the title are missing" ]
    | _ -> [ "the edge tab or the header is missing" ]

let private roomBelowShell (height: int) : string list =
    let room = document.createElement "div"
    room.setAttribute ("style", sprintf "height:%dpx" height)
    document.body.appendChild room |> ignore
    match first Root.Document "#shell" with
    | Some shell ->
        Scrolling.intoView shell "end"
        []
    | None -> [ "there is no shell" ]

let private pastShell () : float list =
    match first Root.Document "#shell" with
    | Some shell -> [ Viewport.visualOffsetTop () + Viewport.visualHeight () - shell.getBoundingClientRect().bottom ]
    | None -> []

let private paddingLeft (element: Element) : float =
    let padding = Css.computedProperty (element :?> HTMLElement) "padding-left"
    if padding.EndsWith "px" then float (padding.Substring (0, padding.Length - 2)) else 0.0

/// Columns are compared as whole pixels, rounded as JavaScript's `Math.round` rounds — half up
/// — which is how the suite rounds the column it asks about.
let private askCardColumn (column: int) : string list =
    match first Root.Document "#shell [data-repo-picker]" with
    | None -> [ "there is no ask card" ]
    | Some card ->
        let box selector = first (Root.Under card) selector |> Option.map (fun e -> int (floor (e.getBoundingClientRect().left + 0.5)))
        let inside selector =
            first (Root.Under card) selector
            |> Option.map (fun e -> int (floor (e.getBoundingClientRect().left + paddingLeft e + 0.5)))
        [ "the question", box "#repo-picker-title"
          "the search field", inside "[data-repo-picker-search]"
          "a row’s name", box "[data-repo-candidate-name]" ]
        |> List.choose (fun (what, left) ->
            match left with
            | Some left when left = column -> None
            | Some left -> Some (sprintf "%s starts at %dpx, the conversation at %dpx" what left column)
            | None -> Some (sprintf "%s is missing" what))

/// Nothing the conversation DRAWS is out of a press's reach, and a column with less room than
/// one item is not drawn at all (docs/visual-design.md, Space).
///
/// Two legs, because at 844x390 the ask card's floor leaves the column less room than one of
/// its items and the rule's answer there is absence. Either leg alone passes over a bug: a
/// column drawn over items no press reaches is the lie the rule exists to forbid, and a column
/// absent with nothing to bring it back is a conversation nobody can return to.
///
/// What is NOT drawn is skipped rather than failed, which is the whole of the first leg: an
/// item inside a column the browser is not drawing has a zero box at the origin, and hit-testing
/// that would report every item in an honestly absent column as unreachable.
let private itemsClearOfCard () : string list =
    let items = all Root.Document "#shell [data-conversation] [data-message-id]"
    let drawn =
        items
        |> List.filter (fun item ->
            let box = item.getBoundingClientRect ()
            box.width > 0.0 && box.height > 0.0)
    match items, drawn with
    // Anti-vacuity: a card standing over nothing cannot say where the conversation went.
    | [], _ -> [ "there is no conversation behind the card, so nothing here is a question" ]
    | _, [] ->
        // The column had less room than one item, so it is not drawn. What took the room owes
        // the way back, and the way back has to be a press that lands.
        match first Root.Document "#shell [data-repo-picker-dismiss]" with
        | None -> [ "the conversation is not drawn and nothing offers a way back to it" ]
        | Some way ->
            let box = way.getBoundingClientRect ()
            if paintedAt way (box.left + box.width / 2.0) (box.top + box.height / 2.0) then []
            else [ "the conversation is not drawn and a press does not reach the way back to it" ]
    | _ ->
        // Anti-vacuity for the other leg: a column showing everything it holds is one no card
        // could have covered the end of.
        let shows =
            first Root.Document "#shell [data-conversation]"
            |> Option.filter (fun column -> column.scrollHeight <= column.clientHeight + 1.0)
            |> Option.map (fun _ -> "the conversation shows all it holds, so where it ends is not a question")
            |> Option.toList
        let unreached =
            drawn
            |> List.choose (fun item ->
                // To the middle, as far as the scroll allows: the column pins its author line at
                // the top, and an item scrolled only to the nearest edge stops under that instead.
                Scrolling.intoView item "center"
                let box = item.getBoundingClientRect ()
                if paintedAt item (box.left + box.width / 2.0) (box.top + box.height / 2.0) then None
                else Some (sprintf "%s is not what a press in its middle lands on" (item.getAttribute "data-message-id")))
        shows @ unreached

let private focusBeforePane () : string list =
    match first Root.Document "#shell [data-content-panel]" with
    | None -> [ "there is no pane" ]
    | Some pane ->
        let before =
            all Root.Document "#shell button"
            |> List.filter (fun b ->
                let button = b :?> HTMLButtonElement
                button.tabIndex >= 0.0 && not button.disabled
                // DOCUMENT_POSITION_PRECEDING: the button comes before the pane.
                && (int (pane.compareDocumentPosition b) &&& 2) <> 0)
        PageGlobal.set Probes.focusStops [||]
        document.addEventListener ("focusin", fun e ->
            let target = e.target :?> Element
            let stop = if (closest target "[data-content-panel]").IsSome then "pane" else "elsewhere"
            let stops = PageGlobal.tryGet Probes.focusStops |> Option.defaultValue [||]
            PageGlobal.set Probes.focusStops (Array.append stops [| stop |]))
        let landed =
            before
            |> List.rev
            |> List.exists (fun b ->
                (b :?> HTMLElement).focus ()
                obj.ReferenceEquals (document.activeElement, b))
        if landed then [] else [ "no control before the pane would take focus" ]

let private nextFrame () : JS.Promise<unit> =
    Promise.create (fun resolve _ -> Browser.Dom.window.requestAnimationFrame (fun _ -> resolve ()) |> ignore)

let private rendered () = PageGlobal.tryGet Probes.renders |> Option.defaultValue 0

let private record (seq: int) (data: string) : unit =
    match PageGlobal.tryGet HarnessPage.record with
    | Some record -> record.Invoke ("term-harness", seq, "o", data)
    | None -> failwith "this page publishes no record to hand output to"

let private blocksSay (words: string) : bool =
    first Root.Document "#shell [data-tail='blocks:term-harness']"
    |> Option.exists (fun surface -> surface.textContent.Contains words)

let private burst () : JS.Promise<float list> =
    let before = rendered ()
    for seq in 2 .. 51 do
        record seq (sprintf "burst-mark %d\r\n" seq)
    nextFrame ()
    |> Promise.map (fun () -> [ float (rendered () - before); (if blocksSay "burst-mark 51" then 1.0 else 0.0) ])

let private loneRecordInQuietFrame () : JS.Promise<string list> =
    let rec attempt (tries: int) : JS.Promise<string list> =
        if tries >= 60 then Promise.lift [ "the page drew on every one of sixty frames, so none was quiet to ask in" ]
        else
            let seen = rendered ()
            Promise.create (fun resolve _ ->
                Browser.Dom.window.requestAnimationFrame (fun _ ->
                    // A render since the last look may still have its frame open; look again a
                    // frame on.
                    if rendered () <> seen then resolve None
                    else
                        record 2 "lone-mark\r\n"
                        resolve (Some (if blocksSay "lone-mark" then [] else [ "not yet" ])))
                |> ignore)
            |> Promise.bind (function
                | Some answer -> Promise.lift answer
                | None -> attempt (tries + 1))
    attempt 0

let private smoothScrollToTop () : JS.Promise<float list> =
    match first Root.Document "#shell [data-conversation]" with
    | None -> Promise.lift []
    | Some el ->
        Scrolling.intoView el "start"
        el.scrollTop <- el.scrollHeight
        nextFrame ()
        |> Promise.bind (fun () ->
            Scrolling.smoothlyTo el 0.0
            // Until it stops moving for three frames, wherever that is.
            let rec settle (last: float) (still: int) : JS.Promise<float list> =
                nextFrame ()
                |> Promise.bind (fun () ->
                    let now = el.scrollTop
                    let still = if now = last then still + 1 else 0
                    if still >= 3 then Promise.lift [ now ] else settle now still)
            settle el.scrollTop 0)

let private chapterPlaceholder () : float list =
    match first Root.Document "#shell [data-chapter-name='msg-filler-8']" with
    | None -> []
    | Some field ->
        let input = field :?> HTMLInputElement
        let ink = Css.placeholderColor field
        let flag (holds: bool) = if holds then 1.0 else 0.0
        [ flag (input.value = "")
          flag (input.placeholder.Length > 0)
          flag (ink <> "rgba(0, 0, 0, 0)" && ink <> "transparent") ]

let private itemActionsUnderThumb () : string list =
    all Root.Document "#shell [data-conversation] [data-item-actions]"
    |> List.choose (fun control ->
        Scrolling.intoView control "center"
        let box = control.getBoundingClientRect ()
        let opacity = float (Css.computedProperty (control :?> HTMLElement) "opacity")
        if opacity = 0.0 || not (paintedAt control (box.left + box.width / 2.0) (box.top + box.height / 2.0)) then
            Some (sprintf "%s (opacity %g)" (control.getAttribute "data-item-actions") opacity)
        else None)

let private fontSize (element: Element) : float =
    let size = Css.computedProperty (element :?> HTMLElement) "font-size"
    float (size.Replace ("px", ""))

let private agentCaretOnLetters () : string list =
    match first Root.Document "#shell [data-agent-writing]" with
    | None -> [ "there is no caret" ]
    | Some caret ->
        let last =
            closest caret "[data-message-body]"
            |> Option.bind (fun body -> textsUnder body |> List.filter (fun t -> t.textContent.Trim () <> "") |> List.tryLast)
        match last with
        | None -> [ "the body has no words to stand after" ]
        | Some last ->
            let ending = last.textContent.TrimEnd().Length
            let glyph = TextRange.boxOf last (ending - 1) ending
            // A zero-wide box one x-height tall, just after the last word: its bottom is the
            // baseline and its top where an `x` ends.
            let marker = document.createElement "span"
            marker.setAttribute ("style", "display:inline-block;width:0;height:1ex")
            last.parentNode.insertBefore (marker, last.nextSibling) |> ignore
            let letters = marker.getBoundingClientRect ()
            marker.remove ()
            let at = caret.getBoundingClientRect ()
            let em = fontSize last.parentElement
            [ if at.top < glyph.top || at.bottom > glyph.bottom then
                  sprintf "it is not on the last line (%g-%g against %g-%g)" at.top at.bottom glyph.top glyph.bottom
              // Within an overshoot: a point is carried a hair past its line, as the round
              // letters beside it are.
              if abs (at.bottom - letters.bottom) > 0.05 * em then
                  sprintf "its lower point is %gpx off the baseline" (at.bottom - letters.bottom)
              if abs (at.top - letters.top) > 0.05 * em then
                  sprintf "its upper point is %gpx off the x-height" (at.top - letters.top)
              if at.left < glyph.right then "it starts before the last word ends"
              if at.left - glyph.right > em then sprintf "it stands %gpx after the last word" (at.left - glyph.right) ]

let private thinkingMarkInsideBox () : string list =
    match first Root.Document "#shell [data-agent-writing]" with
    | None -> [ "there is no thinking mark" ]
    | Some mark ->
        let box = mark.getBoundingClientRect ()
        let em = fontSize mark
        let leaves = all (Root.Under mark) "*" |> List.filter (fun e -> e.children.length = 0)
        let moves = Animations.under mark
        if moves.Length = 0 then [ "the thinking mark does not move" ]
        else
            let cycle = moves |> Array.map Animations.duration |> Array.max
            let painted = if leaves.IsEmpty then [ mark ] else leaves
            let worst =
                [ for t in 0.0 .. 20.0 .. cycle - 1.0 do
                      for move in moves do Animations.pauseAt move t
                      let boxes = painted |> List.map (fun e -> e.getBoundingClientRect ())
                      let over =
                          List.max
                              [ box.top - (boxes |> List.map (fun b -> b.top) |> List.min)
                                (boxes |> List.map (fun b -> b.bottom) |> List.max) - box.bottom
                                box.left - (boxes |> List.map (fun b -> b.left) |> List.min)
                                (boxes |> List.map (fun b -> b.right) |> List.max) - box.right ]
                          - 0.05 * em
                      if over > 0.0 then yield over, t ]
                |> List.sortByDescending fst
                |> List.tryHead
            match worst with
            | Some (over, t) -> [ sprintf "it paints %.2fpx outside its box at %gms" over t ]
            | None -> []

let private firstWordFromOpening () : JS.Promise<string list> =
    let conversation () = first Root.Document "#shell [data-conversation]"
    let place (mark: Element) =
        let r = mark.getBoundingClientRect ()
        let scrolled = conversation () |> Option.map (fun c -> c.scrollTop) |> Option.defaultValue 0.0
        r.left + r.width / 2.0, r.top + r.height / 2.0 + scrolled, r.height
    match first Root.Document "#shell [data-agent-writing]", PageGlobal.tryGet HarnessPage.agentSays with
    | None, _ -> Promise.lift [ "there is no opening mark" ]
    | _, None -> Promise.lift [ "this page publishes no first word to say" ]
    | Some opening, Some says ->
        let wasX, wasY, wasH = place opening
        says ()
        let rec caret (tries: int) : JS.Promise<Element option> =
            match first Root.Document "#shell [data-agent-writing]" with
            | Some m when m.children.length = 0 -> Promise.lift (Some m)
            | _ when tries >= 60 -> Promise.lift None
            | _ -> nextFrame () |> Promise.bind (fun () -> caret (tries + 1))
        caret 0
        |> Promise.bind (function
            | None -> Promise.lift [ "the first word put down no caret" ]
            | Some caret ->
                nextFrame ()
                |> Promise.map (fun () ->
                    let arriving = Animations.on caret
                    if arriving.Length = 0 then [ "the caret appears where it lands, from nowhere" ]
                    else
                        for animation in arriving do Animations.pauseAt animation 0.0
                        // In the CONVERSATION's coordinates, not the window's: a reader at the
                        // end is kept there as the first word lands, and the column scrolling
                        // under both marks is not the caret leaving.
                        let x, y, h = place caret
                        let off = List.max [ abs (x - wasX); abs (y - wasY); abs (h - wasH) ]
                        if off > 1.0 then [ sprintf "it leaves %.2fpx from where the turn's mark stood" off ] else []))

/// A box's edges, rounded, for a verdict to say where something stood.
let private described (box: ClientRect) : string =
    sprintf "%.0f,%.0f %.0fx%.0f" box.left box.top box.width box.height

/// An element as a verdict names it: its tag and its `data-*` attributes.
let private named (element: Element) : string =
    let attributes = element.attributes
    let hooks =
        [ for i in 0 .. int attributes.length - 1 do
              let attribute = attributes.[i]
              if attribute.name.StartsWith "data-" then yield sprintf "[%s=%s]" attribute.name attribute.value ]
    element.tagName.ToLowerInvariant () + String.concat "" hooks

let private composerTops () : float list =
    let top selector =
        match first Root.Document selector with
        | Some element -> (element.getBoundingClientRect ()).top + Browser.Dom.window.scrollY
        | None -> nan
    [ top "#shell [data-draft-input] .ProseMirror"
      top "#shell [data-model-panel]"
      top "#shell [data-draft-input]" ]

let private sendAnswers () : string list =
    match first Root.Document "#shell [data-send-draft]" with
    | None -> [ "there is no Send" ]
    | Some send ->
        let b = send.getBoundingClientRect ()
        match document.elementFromPoint (b.left + b.width / 2.0, b.top + b.height / 2.0) |> Option.ofObj with
        | Some at when send.contains at -> []
        | Some at -> [ sprintf "%s at %s (send at %s)" (named at) (described (at.getBoundingClientRect ())) (described b) ]
        | None -> [ sprintf "nothing (send at %s)" (described b) ]

let private wait (ms: int) : JS.Promise<unit> =
    Promise.create (fun resolve _ -> Browser.Dom.window.setTimeout ((fun () -> resolve ()), ms) |> ignore)

let private playCarriesOn () : JS.Promise<string list> =
    let mount = "#shell [data-pane-replay='terminal:term-live']"
    let seen = ResizeArray<string> ()
    let read () =
        seen.Add (first Root.Document mount |> Option.map (fun m -> m.textContent) |> Option.defaultValue "(no replay)")
    let timer = Browser.Dom.window.setInterval (read, 25)
    wait 100
    |> Promise.bind (fun () ->
        match first Root.Document (mount + " .ap-overlay-start") with
        | Some start -> (start :?> HTMLElement).click ()
        | None -> seen.Add "(no play to press)"
        wait 700)
    |> Promise.map (fun () ->
        Browser.Dom.window.clearInterval timer
        seen |> Seq.filter (fun frame -> not (frame.Contains "line 3")) |> List.ofSeq)

let private columnOpened () : string list =
    match first Root.Document "#shell [data-content-panel]" with
    | None -> [ "no content column" ]
    | Some panel ->
        let p = panel.getBoundingClientRect ()
        [ if (Animations.on panel).Length > 0 then "the column is still animating"
          match first (Root.Under panel) "[data-terminal-scrollback]" with
          | None -> "no scrollback in the column"
          | Some scroller ->
              let q = scroller.getBoundingClientRect ()
              if q.left < p.left - 0.5 || q.right > p.right + 0.5 then "the scrollback is wider than the column"
          let inside =
              document.activeElement |> Option.ofObj |> Option.bind (fun e -> closest e "#shell [data-content-panel]")
          if inside.IsNone then "focus is not in the column" ]

let private burstMiddleToTop () : string list =
    match first Root.Document "#shell [data-terminal-scrollback]" with
    | None -> [ "no scrollback" ]
    | Some scroller ->
        match first (Root.Under scroller) "[data-terminal-block=block-burst-running]" with
        | None -> [ "no burst block" ]
        | Some block ->
            let a = scroller.getBoundingClientRect ()
            let b = block.getBoundingClientRect ()
            if b.height < 1000.0 then [ sprintf "the burst block is %.0fpx tall" b.height ]
            else
                scroller.scrollTop <- scroller.scrollTop + (b.top - a.top) + b.height / 2.0
                []

let private commandRowHeld () : JS.Promise<string list> =
    nextFrame ()
    |> Promise.map (fun () ->
        let scroller = first Root.Document "#shell [data-terminal-scrollback]"
        let block = scroller |> Option.bind (fun s -> first (Root.Under s) "[data-terminal-block=block-burst-running]")
        let row = block |> Option.bind (fun b -> first (Root.Under b) "[data-terminal-block-command]")
        match scroller, block, row with
        | Some scroller, Some block, Some row ->
            let a = scroller.getBoundingClientRect ()
            let b = block.getBoundingClientRect ()
            let r = row.getBoundingClientRect ()
            let hit = document.elementFromPoint (r.left + r.width / 2.0, r.top + r.height / 2.0) |> Option.ofObj
            let where =
                sprintf "scroller %.1f, block %.1f, row %.1f (%.1f to %.1f), window %.0f"
                    a.top b.top r.top r.left r.right Browser.Dom.window.innerWidth
            [ if b.top >= a.top - 500.0 then "the block has not scrolled away: " + where
              if abs (r.top - a.top) > 1.0 then "the row is not at the top: " + where
              match hit with
              | Some hit when row.contains hit -> ()
              | Some hit -> "the row is under " + named hit
              | None -> "nothing is painted at the row" ]
        | _ -> [ "the burst block's command row is not there" ])

let rec private nodesUnder (node: Node) : int =
    let children = node.childNodes
    children.length + List.sum [ for i in 0 .. children.length - 1 -> nodesUnder children.[i] ]

let private outputNodes () : float list =
    match first Root.Document "#shell [data-terminal-block='block-burst-running'] [data-terminal-output]" with
    | None -> []
    | Some output -> [ float (nodesUnder output); (if output.textContent.Contains "line 1500" then 1.0 else 0.0) ]

let private foldedCommandLanded () : string list =
    let scroller = first Root.Document "#shell [data-terminal-scrollback]"
    let run = scroller |> Option.bind (fun s -> first (Root.Under s) "[data-terminal-block-run='block-burst-ok'] details")
    let block = scroller |> Option.bind (fun s -> first (Root.Under s) "[data-terminal-block=block-burst-failed]")
    match scroller, block with
    | Some scroller, Some block ->
        // In view AND painted: a box inside a shut `<details>` can still measure as if it were
        // on screen, so ask what is drawn at its line.
        let inView =
            match first (Root.Under block) "[data-terminal-block-command]" with
            | None -> false
            | Some row ->
                let a = scroller.getBoundingClientRect ()
                let r = row.getBoundingClientRect ()
                r.top >= a.top - 1.0 && r.bottom <= a.bottom + 1.0 && paintedAt block (r.left + r.width / 2.0) (r.top + r.height / 2.0)
        let focusOnChip =
            document.activeElement |> Option.ofObj |> Option.exists (fun e -> e.hasAttribute "data-chat-block")
        [ if not (run |> Option.exists (fun r -> r.hasAttribute "open")) then "the run is shut"
          if not inView then "the command is not in view"
          if not (Animations.on block |> Array.exists (fun a -> Animations.name a = "reveal-line")) then "the command is not marked"
          if not focusOnChip then "focus is not back on the chip" ]
    | _ -> [ "the folded command is not in the scrollback" ]

let private longValueInLane () : string list =
    match first Root.Document "[data-query-panel=\"work_sandboxes\"]" with
    | None -> [ "no work_sandboxes panel" ]
    | Some panel ->
        for cell in all (Root.Under panel) "[data-query-cell]" do
            cell.textContent <- "a value far longer than two hundred and eighty pixels of column"
        let edge = (panel.getBoundingClientRect ()).right
        all (Root.Under panel) "*"
        |> List.filter (fun e -> e.scrollWidth > e.clientWidth + 1.0 || (e.getBoundingClientRect ()).right > edge + 1.0)
        |> List.map (fun e ->
            let text = e.textContent.Trim ()
            sprintf "%s: %s" e.tagName (if text.Length > 40 then text.Substring (0, 40) else text))

let private labelCostsValue () : string list =
    match first Root.Document "[data-query-panel=\"work_sandboxes\"]" with
    | None -> [ "no work_sandboxes panel" ]
    | Some panel ->
        let values () = all (Root.Under panel) "dl [data-query-cell]"
        // The same long value in every cell, so what is compared across the two measurements is
        // the LABEL and nothing else.
        for value in values () do
            value.textContent <- "/private/etc/ssl/cert.pem, read-only; NIX_SSL_CERT_FILE=/private/etc/ssl/cert.pem"
        let before = values () |> List.map (fun v -> (v.getBoundingClientRect ()).width)
        for label in all (Root.Under panel) "dl dt" do
            label.textContent <- "a label longer than the lane it is asked to share"
        List.zip (values ()) before
        |> List.choose (fun (value, was) ->
            let now = (value.getBoundingClientRect ()).width
            if abs (was - now) > 1.0 then
                Some (sprintf "%s: %.0fpx became %.0fpx" (value.getAttribute "data-query-cell") was now)
            else None)

/// How a measure answers: a verdict (what is wrong) or a reading (numbers), each asked with
/// its parameter. One match, so a new measure does not compile until it says which it is.
[<RequireQualifiedAccess>]
type private Implementation =
    | Verdict of (int -> JS.Promise<string list>)
    | Reading of (int -> JS.Promise<float list>)

let private implement (measure: Probes.Measure) : Implementation =
    let verdictNow (read: int -> string list) = Implementation.Verdict (fun parameter -> Promise.lift (read parameter))
    let readingNow (read: int -> float list) = Implementation.Reading (fun parameter -> Promise.lift (read parameter))
    match measure with
    | Probes.Measure.RunningMarkOnTitle -> verdictNow (fun _ -> runningMarkOnTitle ())
    | Probes.Measure.CauseHeadsOnLines -> verdictNow (fun _ -> causeHeadsOnLines ())
    | Probes.Measure.RunningMarkOnCauseLine -> verdictNow (fun _ -> runningMarkOnCauseLine ())
    | Probes.Measure.FoldArrowsOnRail -> readingNow (fun _ -> foldArrowsOnRail ())
    | Probes.Measure.FoldArrowsOnAvatars -> readingNow (fun _ -> foldArrowsOnAvatars ())
    | Probes.Measure.ReferenceBaseline -> readingNow (fun _ -> referenceBaseline ())
    | Probes.Measure.GrabEdgeShape -> readingNow (fun _ -> grabEdgeShape ())
    | Probes.Measure.GrabEdgeClear -> verdictNow (fun _ -> grabEdgeClear ())
    | Probes.Measure.EdgeTabOnBand -> verdictNow (fun desktop -> edgeTabOnBand (desktop = 1))
    | Probes.Measure.RoomBelowShell -> verdictNow roomBelowShell
    | Probes.Measure.PastShell -> readingNow (fun _ -> pastShell ())
    | Probes.Measure.AskCardColumn -> verdictNow askCardColumn
    | Probes.Measure.ItemsClearOfCard -> verdictNow (fun _ -> itemsClearOfCard ())
    | Probes.Measure.FocusBeforePane -> verdictNow (fun _ -> focusBeforePane ())
    | Probes.Measure.Burst -> Implementation.Reading (fun _ -> burst ())
    | Probes.Measure.LoneRecordInQuietFrame -> Implementation.Verdict (fun _ -> loneRecordInQuietFrame ())
    | Probes.Measure.SmoothScrollToTop -> Implementation.Reading (fun _ -> smoothScrollToTop ())
    | Probes.Measure.ChapterPlaceholder -> readingNow (fun _ -> chapterPlaceholder ())
    | Probes.Measure.ItemActionsUnderThumb -> verdictNow (fun _ -> itemActionsUnderThumb ())
    | Probes.Measure.AgentCaretOnLetters -> verdictNow (fun _ -> agentCaretOnLetters ())
    | Probes.Measure.ThinkingMarkInsideBox -> verdictNow (fun _ -> thinkingMarkInsideBox ())
    | Probes.Measure.FirstWordFromOpening -> Implementation.Verdict (fun _ -> firstWordFromOpening ())
    | Probes.Measure.ComposerTops -> readingNow (fun _ -> composerTops ())
    | Probes.Measure.SendAnswers -> verdictNow (fun _ -> sendAnswers ())
    | Probes.Measure.PlayCarriesOn -> Implementation.Verdict (fun _ -> playCarriesOn ())
    | Probes.Measure.ColumnOpened -> verdictNow (fun _ -> columnOpened ())
    | Probes.Measure.BurstMiddleToTop -> verdictNow (fun _ -> burstMiddleToTop ())
    | Probes.Measure.CommandRowHeld -> Implementation.Verdict (fun _ -> commandRowHeld ())
    | Probes.Measure.OutputNodes -> readingNow (fun _ -> outputNodes ())
    | Probes.Measure.FoldedCommandLanded -> verdictNow (fun _ -> foldedCommandLanded ())
    | Probes.Measure.LongValueInLane -> verdictNow (fun _ -> longValueInLane ())
    | Probes.Measure.LabelCostsValue -> verdictNow (fun _ -> labelCostsValue ())

/// What is wrong, by a verdict measure's account, asked with its parameter.
let verdict (measure: Probes.Measure) : (int -> JS.Promise<string list>) option =
    match implement measure with
    | Implementation.Verdict answer -> Some answer
    | Implementation.Reading _ -> None

/// The numbers a reading measure takes.
let reading (measure: Probes.Measure) : (int -> JS.Promise<float list>) option =
    match implement measure with
    | Implementation.Reading answer -> Some answer
    | Implementation.Verdict _ -> None
