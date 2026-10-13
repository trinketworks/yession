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
