module Yession.Browser.Measures

// The measurements individual browser cases make (`Yession.App.Probes.Measure`), in F#. Each was
// a JavaScript program a case handed the page in a string; here each is a function over the DOM
// the compiler reads, published by `Probes.fs` under the name its case asks for. Bundled by the
// test build alone, with the probes; nothing in the product imports it.

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

let private itemsClearOfCard () : string list =
    all Root.Document "#shell [data-conversation] [data-message-id]"
    |> List.choose (fun item ->
        // To the middle, as far as the scroll allows: the column pins its author line at the
        // top, and an item scrolled only to the nearest edge stops under that instead.
        Scrolling.intoView item "center"
        let box = item.getBoundingClientRect ()
        if paintedAt item (box.left + box.width / 2.0) (box.top + box.height / 2.0) then None
        else Some (sprintf "%s is not what a press in its middle lands on" (item.getAttribute "data-message-id")))

/// What is wrong, by a verdict measure's account, asked with its parameter.
let verdict (measure: Probes.Measure) : (int -> string list) option =
    match measure with
    | Probes.Measure.RunningMarkOnTitle -> Some (fun _ -> runningMarkOnTitle ())
    | Probes.Measure.CauseHeadsOnLines -> Some (fun _ -> causeHeadsOnLines ())
    | Probes.Measure.RunningMarkOnCauseLine -> Some (fun _ -> runningMarkOnCauseLine ())
    | Probes.Measure.GrabEdgeClear -> Some (fun _ -> grabEdgeClear ())
    | Probes.Measure.EdgeTabOnBand -> Some (fun desktop -> edgeTabOnBand (desktop = 1))
    | Probes.Measure.RoomBelowShell -> Some roomBelowShell
    | Probes.Measure.AskCardColumn -> Some askCardColumn
    | Probes.Measure.ItemsClearOfCard -> Some (fun _ -> itemsClearOfCard ())
    | Probes.Measure.PastShell
    | Probes.Measure.FoldArrowsOnRail
    | Probes.Measure.FoldArrowsOnAvatars
    | Probes.Measure.ReferenceBaseline
    | Probes.Measure.GrabEdgeShape -> None

/// The numbers a reading measure takes.
let reading (measure: Probes.Measure) : (int -> float list) option =
    match measure with
    | Probes.Measure.FoldArrowsOnRail -> Some (fun _ -> foldArrowsOnRail ())
    | Probes.Measure.FoldArrowsOnAvatars -> Some (fun _ -> foldArrowsOnAvatars ())
    | Probes.Measure.ReferenceBaseline -> Some (fun _ -> referenceBaseline ())
    | Probes.Measure.GrabEdgeShape -> Some (fun _ -> grabEdgeShape ())
    | Probes.Measure.PastShell -> Some (fun _ -> pastShell ())
    | Probes.Measure.RoomBelowShell
    | Probes.Measure.AskCardColumn
    | Probes.Measure.ItemsClearOfCard
    | Probes.Measure.RunningMarkOnTitle
    | Probes.Measure.CauseHeadsOnLines
    | Probes.Measure.RunningMarkOnCauseLine
    | Probes.Measure.GrabEdgeClear
    | Probes.Measure.EdgeTabOnBand -> None
