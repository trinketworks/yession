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

let private media (query: string) : bool = Media.mediaMatches query

let private hitAt (selector: string, below: float) : bool =
    match first selector with
    | None -> false
    | Some element ->
        let box = element.getBoundingClientRect ()
        Browser.Dom.document.elementFromPoint (box.left + box.width / 2.0, box.top + below)
        |> Option.ofObj
        |> Option.exists element.contains

let private shows (selector: string, words: string) : bool =
    match first selector with
    | None -> false
    | Some root ->
        let found = root.querySelectorAll "*"
        [ for i in 0 .. found.length - 1 -> found.[i] ]
        |> List.exists (fun e ->
            e.children.length = 0
            && e.textContent.Contains words
            && paintedAtCentre e (boxOf e))

let rec private opacityUp (element: Element) : float =
    let own = float (Css.computedProperty (element :?> HTMLElement) "opacity")
    match element.parentElement |> Option.ofObj with
    | Some parent -> own * opacityUp parent
    | None -> own

let private controlNamed (element: Element) : string =
    let attributes = element.attributes
    let names = [ for i in 0 .. int attributes.length - 1 -> attributes.[i].name ]
    match names |> List.tryFind (fun n -> n.StartsWith "data-" && n <> "data-pane-tab") with
    | Some name -> name
    | None ->
        match element.getAttribute "aria-label" |> Option.ofObj with
        | Some label -> label
        | None -> element.tagName

let private opening (element: Element) : string =
    let attributes = element.attributes
    let said =
        [ for i in 0 .. int attributes.length - 1 ->
              let a = attributes.[i]
              sprintf " %s=\"%s\"" a.name a.value ]
    "<" + element.tagName.ToLowerInvariant () + String.concat "" said + ">"

let private controls (selector: string, reveal: bool) : Probes.Control list =
    all selector
    |> List.map (fun element ->
        if reveal then Scrolling.intoView element "center"
        let box = boxOf element
        { Probes.Control.Seen = Probes.Seen.create box (paintedAtCentre element box) element.textContent
          Probes.Control.Opacity = opacityUp element
          Probes.Control.FontSize = float ((Css.computedProperty (element :?> HTMLElement) "font-size").Replace ("px", ""))
          Probes.Control.Field = element.matches "select, input:not([type=checkbox]):not([type=radio])"
          Probes.Control.Exempt = (element.closest "[inert], [aria-hidden=true]").IsSome
          Probes.Control.Name = controlNamed element
          Probes.Control.Opening = opening element })

let private attrAt ((x: float, y: float), (selector: string, attribute: string)) : string option =
    Browser.Dom.document.elementFromPoint (x, y)
    |> Option.ofObj
    |> Option.bind (fun hit -> hit.closest selector)
    |> Option.bind (fun found -> found.getAttribute attribute |> Option.ofObj)

let private after (ms: int) : JS.Promise<unit> =
    Promise.create (fun resolve _ -> Browser.Dom.window.setTimeout ((fun () -> resolve ()), ms) |> ignore)

let private quiet (_: int) : JS.Promise<Probes.Quiet> =
    let rec poll (last: int option) (still: int) (waited: int) : JS.Promise<Probes.Quiet> =
        let now = PageGlobal.tryGet Probes.renders
        let still = if now = last then still + 1 else 0
        let waited = waited + 250
        if still >= 6 || waited >= 30000 then
            Promise.lift
                { Probes.Quiet.Renders = now
                  Probes.Quiet.Scans = PageGlobal.tryGet Probes.scans
                  Probes.Quiet.Still = still >= 6 }
        else after 250 |> Promise.bind (fun () -> poll now still waited)
    // `-1` is no count a page publishes, so the first poll is never already still.
    poll (Some -1) 0 0

/// The start of a text, for a line a person reads.
let private clipped (length: int) (text: string) =
    if text.Length > length then text.Substring (0, length) else text

/// What this client KEPT, by store and address. The ADDRESS keyed to its BODY: the address
/// says which line the entry starts on — a replay with entries but none starting at 0 folds
/// nothing — and the body says WHICH record it is: a store that kept the `"i"` input line but
/// not yet the `"o"` output reads the same by address as one that kept both, and only the bytes
/// tell them apart. Truncated; a recording is small here.
let private kept () : JS.Promise<string list> =
    match caches () with
    | None -> Promise.lift [ "kept: no stores on this page" ]
    | Some stores ->
        let origin = Browser.Dom.window.location.origin
        let address (url: string) =
            let path = url.Replace (origin, "")
            match path.IndexOf "?token=" with
            | -1 -> path
            | at -> path.Substring (0, at)
        stores.names ()
        |> Promise.bind (fun names ->
            names
            |> Array.map (fun name ->
                stores.openStore name
                |> Promise.bind (fun store ->
                    store.keys ()
                    |> Promise.bind (fun requests ->
                        requests
                        |> Array.map (fun request ->
                            cacheMatch store request.url
                            |> Promise.bind (fun response ->
                                match Option.ofObj response with
                                | None -> Promise.lift (sprintf "kept %s %s: (gone)" name (address request.url))
                                | Some response ->
                                    response.text ()
                                    |> Promise.map (fun body -> sprintf "kept %s %s: %s" name (address request.url) (clipped 300 body))))
                        |> Promise.all)))
            |> Promise.all)
        |> Promise.map (Array.concat >> List.ofArray)
        |> Promise.catch (fun e -> [ "kept: unreadable: " + e.Message ])

let private pageState (_: int) : JS.Promise<string list> =
    let attribute (selector: string) (name: string) =
        first selector |> Option.bind (fun e -> e.getAttribute name |> Option.ofObj) |> Option.defaultValue "(none)"
    let facts =
        [ "url: " + Browser.Dom.window.location.href
          "title: " + Browser.Dom.document.title
          "connection: " + attribute "[data-connection]" "data-connection"
          "conversation: " + (first "[data-conversation]" |> Option.map (fun e -> clipped 200 e.textContent) |> Option.defaultValue "(none)")
          "degraded: " + attribute "[data-degraded]" "data-degraded"
          // Where the keyboard is: a keyboard case that fails has either pressed the wrong thing
          // or pressed into nothing, and only this tells which.
          "focused: "
          + (Browser.Dom.document.activeElement |> Option.ofObj |> Option.map opening |> Option.defaultValue "(none)") ]
    kept () |> Promise.map (fun kept -> facts @ kept)

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
answering Probes.media media
answering Probes.hitAt hitAt
answering Probes.shows shows
awaiting Probes.quiet quiet
awaiting Probes.pageState pageState
answering Probes.controls controls
answering Probes.attrAt attrAt

for measure in Probes.Measure.all do
    Measures.verdict measure |> Option.iter (awaiting (Probes.verdict measure))
    Measures.reading measure |> Option.iter (awaiting (Probes.reading measure))
