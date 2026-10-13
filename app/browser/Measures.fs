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

/// What is wrong, by a verdict measure's account.
let verdict (measure: Probes.Measure) : (unit -> string list) option =
    match measure with
    | Probes.Measure.RunningMarkOnTitle -> Some runningMarkOnTitle
    | Probes.Measure.CauseHeadsOnLines -> Some causeHeadsOnLines
    | Probes.Measure.RunningMarkOnCauseLine -> Some runningMarkOnCauseLine
    | Probes.Measure.FoldArrowsOnRail
    | Probes.Measure.FoldArrowsOnAvatars -> None

/// The numbers a reading measure takes.
let reading (measure: Probes.Measure) : (unit -> float list) option =
    match measure with
    | Probes.Measure.FoldArrowsOnRail -> Some foldArrowsOnRail
    | Probes.Measure.FoldArrowsOnAvatars -> Some foldArrowsOnAvatars
    | Probes.Measure.RunningMarkOnTitle
    | Probes.Measure.CauseHeadsOnLines
    | Probes.Measure.RunningMarkOnCauseLine -> None
