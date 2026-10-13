module Yession.Browser.Probes

// The measuring half of `Yession.App.Probes`: what the browser cases ask a page that a locator
// cannot answer, answered in the page. Bundled by the test build alone (`tests/browser/out/
// probes.js`) and installed into every context the suite opens before any page script runs;
// nothing in the product imports it, so it does not ship.
//
// Each probe is published under its contract name as a function of one JSON string to one JSON
// string, which is the whole interface: the suite calls it by that name and decodes the answer
// with the same codec that encoded it here.

open Fable.Core
open Browser.Types
open Fable.BrowserExtras
open Yession.App
#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

let private boxOf (element: Element) : Probes.Box =
    let rect = element.getBoundingClientRect ()
    Probes.Box.create rect.top rect.bottom rect.left rect.right

/// Whether `element` is what a press at its own centre would land on.
let private paintedAtCentre (element: Element) (box: Probes.Box) : bool =
    if Probes.Box.isEmpty box then false
    else
        let x = box.Left + Probes.Box.width box / 2.0
        let y = box.Top + Probes.Box.height box / 2.0
        match Browser.Dom.document.elementFromPoint (x, y) |> Option.ofObj with
        | Some hit -> element.contains hit
        | None -> false

let private all (selector: string) : Element list =
    let found = Browser.Dom.document.querySelectorAll selector
    [ for i in 0 .. found.length - 1 -> found.[i] ]

let private first (selector: string) : Element option =
    Browser.Dom.document.querySelector selector |> Option.ofObj

let private seen (selector: string) : Probes.Seen list =
    all selector
    |> List.map (fun element ->
        let box = boxOf element
        // An element's `textContent` is never null — only a document's or a doctype's is.
        Probes.Seen.create box (paintedAtCentre element box) element.textContent)

let private boxes (selectors: string list) : Probes.Box option list =
    selectors |> List.map (fun selector -> first selector |> Option.map boxOf)

let private computed (selector: string, property: string) : string option =
    first selector
    // The binding takes an HTMLElement; the cascade answers for any element, and the cast says
    // nothing more than that the selectors asked about are elements a stylesheet styles.
    |> Option.map (fun element -> Css.computedProperty (element :?> HTMLElement) property)

let private framing (withinMs: int) : JS.Promise<bool> =
    // Whichever comes first settles it; a promise settles once, so the other is a no-op.
    Promise.create (fun resolve _ ->
        Browser.Dom.window.requestAnimationFrame (fun _ -> resolve true) |> ignore
        Browser.Dom.window.setTimeout ((fun () -> resolve false), withinMs) |> ignore)

let private frames (count: int) : JS.Promise<int> =
    let rec after (ran: int) : JS.Promise<int> =
        if ran >= count then Promise.lift ran
        else
            Promise.create (fun resolve _ -> Browser.Dom.window.requestAnimationFrame (fun _ -> resolve ()) |> ignore)
            |> Promise.bind (fun () -> after (ran + 1))
    after 0

let private ownTexts (selector: string) : string list =
    all selector
    |> List.map (fun element ->
        let children = element.childNodes
        [ for i in 0 .. children.length - 1 -> children.[i] ]
        |> List.filter (fun child ->
            not (child.nodeType = 1.0 && (child :?> Element).classList.contains "pm-caret"))
        |> List.map (fun child -> child.textContent)
        |> String.concat "")

let private databases (_: int) : JS.Promise<string list> =
    IndexedDb.names () |> Promise.map List.ofArray

let private scrollOf (element: Element) : Probes.Scroll =
    Probes.Scroll.create
        element.scrollTop element.scrollLeft element.scrollHeight element.scrollWidth
        element.clientHeight element.clientWidth

let private scroll (selector: string) : Probes.Scroll option = first selector |> Option.map scrollOf

let private scrollTo (selector: string, top: float) : Probes.Scroll option =
    first selector
    |> Option.map (fun element ->
        element.scrollTop <- top
        scrollOf element)

let private scrolls (selector: string) : Probes.Scroll list = all selector |> List.map scrollOf

let private reveal (selector: string, block: string) : bool =
    match first selector with
    | Some element ->
        Scrolling.intoView element block
        true
    | None -> false

/// Every text node under `node`, in document order.
let rec private textsUnder (node: Node) : Node list =
    if node.nodeType = 3.0 then [ node ]
    else
        let children = node.childNodes
        [ for i in 0 .. children.length - 1 do yield! textsUnder children.[i] ]

let private textAt (selector: string, words: string) : Probes.Box option =
    first selector
    |> Option.bind (fun element ->
        textsUnder element
        |> List.tryPick (fun text ->
            let at = text.textContent.IndexOf words
            if at >= 0 then Some (text, at) else None))
    |> Option.map (fun (text, at) ->
        let r = TextRange.boxOf text at (at + words.Length)
        Probes.Box.create r.top r.bottom r.left r.right)

let private selection (_: int) : string = Selection.text ()

let private newest (selector: string, mark: string) : Probes.Newest option =
    first selector
    |> Option.bind (fun surface ->
        textsUnder surface
        |> List.choose (fun text ->
            let at = text.textContent.LastIndexOf mark
            if at >= 0 then Some (text, at) else None)
        |> List.tryLast
        |> Option.map (fun (text, at) ->
            let line = TextRange.boxOf text at (at + mark.Length)
            let box = surface.getBoundingClientRect ()
            let painted =
                Browser.Dom.document.elementFromPoint (line.left + line.width / 2.0, line.top + line.height / 2.0)
                |> Option.ofObj
                |> Option.exists surface.contains
            Probes.Newest.create
                (line.top >= box.top - 0.5 && line.bottom <= box.bottom + 0.5)
                painted
                (round (surface.scrollHeight - surface.clientHeight - surface.scrollTop))))

let private openAll (selector: string) : int =
    let found = all selector
    found |> List.iter (fun element -> element.setAttribute ("open", ""))
    found.Length

let private animating (selector: string) : int option =
    first selector |> Option.map (fun element -> (Animations.on element).Length)

let private park (selector: string, atEnd: bool) : Probes.Scroll option =
    first selector
    |> Option.map (fun surface ->
        surface.parentElement |> Option.ofObj |> Option.iter (fun parent -> Scrolling.intoView parent "end")
        surface.scrollTop <- (if atEnd then surface.scrollHeight else 0.0)
        scrollOf surface)

let private settled (_: int) : JS.Promise<int> =
    let finite = Animations.finite ()
    finite |> Array.map Animations.whenFinished |> Promise.all |> Promise.map (fun _ -> finite.Length)

let private computedAll (selector: string, property: string) : string list =
    all selector |> List.map (fun element -> Css.computedProperty (element :?> HTMLElement) property)

let private transitionsOnToggle (rootClass: string) : string list =
    let started () =
        Transitions.unfinished ()
        |> Array.map (fun transition ->
            let target = transition.target
            let named =
                match target.getAttribute "class" |> Option.ofObj with
                | Some cls when cls <> "" -> cls
                | _ -> target.tagName
            sprintf "%s (%s)" (named.Substring (0, min 60 named.Length)) transition.transitionProperty)
        |> List.ofArray
    let root = Browser.Dom.document.documentElement.classList
    root.toggle rootClass |> ignore
    let there = started ()
    root.toggle rootClass |> ignore
    there @ started ()

let private nextFrame () : JS.Promise<unit> =
    Promise.create (fun resolve _ -> Browser.Dom.window.requestAnimationFrame (fun _ -> resolve ()) |> ignore)

let private heldAfterFold (body: string, selector: string) : JS.Promise<bool> =
    match PageGlobal.tryGet HarnessPage.fold with
    | None -> Promise.reject (exn "this page publishes no fold to hand events to")
    | Some fold ->
        fold body
        nextFrame ()
        |> Promise.bind (fun () ->
            match first selector with
            | None -> Promise.lift false
            | Some target ->
                (target :?> HTMLElement).focus ()
                nextFrame ()
                |> Promise.bind nextFrame
                |> Promise.map (fun () -> obj.ReferenceEquals (Browser.Dom.document.activeElement, target)))

let private window (_: int) : Probes.Window =
    let w = Browser.Dom.window
    Probes.Window.create w.innerWidth w.innerHeight Browser.Dom.document.documentElement.scrollWidth (Viewport.visualHeight ())

/// Publish a probe that answers at once.
let private answering (probe: Probes.Probe<'Ask, 'Answer>) (answer: 'Ask -> 'Answer) : unit =
    PageGlobal.set
        (PageGlobal.named<string -> string> probe.Name)
        (fun asked ->
            match Decode.fromString probe.DecodeAsk asked with
            | Ok ask -> Encode.toString 0 (probe.EncodeAnswer (answer ask))
            | Error e -> failwithf "%s was asked something it cannot read: %s" probe.Name e)

/// Publish a probe whose answer takes time.
let private awaiting (probe: Probes.Probe<'Ask, 'Answer>) (answer: 'Ask -> JS.Promise<'Answer>) : unit =
    PageGlobal.set
        (PageGlobal.named<string -> JS.Promise<string>> probe.Name)
        (fun asked ->
            match Decode.fromString probe.DecodeAsk asked with
            | Ok ask -> answer ask |> Promise.map (fun a -> Encode.toString 0 (probe.EncodeAnswer a))
            | Error e -> Promise.reject (exn (sprintf "%s was asked something it cannot read: %s" probe.Name e)))

answering Probes.seen seen
answering Probes.boxes boxes
answering Probes.computed computed
awaiting Probes.framing framing
awaiting Probes.frames frames
answering Probes.ownTexts ownTexts
awaiting Probes.databases databases
answering Probes.scroll scroll
answering Probes.scrollTo scrollTo
answering Probes.reveal reveal
awaiting Probes.settled settled
answering Probes.computedAll computedAll
answering Probes.transitionsOnToggle transitionsOnToggle
answering Probes.newest newest
answering Probes.openAll openAll
answering Probes.animating animating
answering Probes.park park
answering Probes.scrolls scrolls
awaiting Probes.heldAfterFold heldAfterFold
answering Probes.window window
answering Probes.textAt textAt
answering Probes.selection selection

for measure in Probes.Measure.all do
    Measures.verdict measure |> Option.iter (awaiting (Probes.verdict measure))
    Measures.reading measure |> Option.iter (awaiting (Probes.reading measure))
