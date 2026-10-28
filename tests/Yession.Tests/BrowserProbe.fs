module Yession.Tests.BrowserProbe

// The asking half of `Yession.App.Probes`. The measuring half is installed into every context
// the suite opens (`Probe.install`), so any page a case visits — the harness, a session's shell,
// the Manager's — can be asked; a case asks through the typed functions here and gets typed
// answers back, decoded by the same codecs the page encoded them with.

#if !FABLE_COMPILER

open System.IO
open Microsoft.Playwright
open Thoth.Json.Net
open Yession.App
open Yession.Tests.PageScript

[<RequireQualifiedAccess>]
module Probe =
    /// Built by the `Harness` target beside the harness it serves.
    let private bundle = Path.GetFullPath "tests/browser/out/probes.js"

    /// Give every page this context opens the probes, before any script of its own runs.
    let install (context: IBrowserContext) : Async<unit> =
        PageScript.everyPage context bundle

    /// Ask the page one question of its contract.
    let ask (page: IPage) (probe: Probes.Probe<'Ask, 'Answer>) (asked: 'Ask) : Async<'Answer> =
        async {
            let question = Encode.toString 0 (probe.EncodeAsk asked)
            // The question crosses as JSON the shared codec wrote, and so does the answer.
            let! answer = PageScript.call<string> page probe.Name [| box question |]
            return
                match Decode.fromString probe.DecodeAnswer answer with
                | Ok value -> value
                | Error e -> failwithf "%s answered something its contract cannot read: %s" probe.Name e
        }

    /// Every match of a selector, where it is drawn and whether it is painted at its centre.
    let seen (page: IPage) (selector: string) : Async<Probes.Seen list> = ask page Probes.seen selector

    /// How many matches of a selector a reader can actually see.
    let paintedCount (page: IPage) (selector: string) : Async<int> =
        async {
            let! all = seen page selector
            return all |> List.filter (fun s -> s.Painted) |> List.length
        }

    /// The first match of each selector, measured in one frame.
    let boxes (page: IPage) (selectors: string list) : Async<Probes.Box option list> =
        ask page Probes.boxes selectors

    /// One computed style property of the first match.
    let computed (page: IPage) (selector: string) (property: string) : Async<string option> =
        ask page Probes.computed (selector, property)

    /// Whether the page runs an animation frame within the given milliseconds.
    let framing (page: IPage) (withinMs: int) : Async<bool> = ask page Probes.framing withinMs

    /// Let the page run this many animation frames.
    let frames (page: IPage) (count: int) : Async<unit> = ask page Probes.frames count |> Async.Ignore

    /// Every match's own text, the remote carets' widgets left out.
    let ownTexts (page: IPage) (selector: string) : Async<string list> = ask page Probes.ownTexts selector

    /// The first match's scroll box.
    let scroll (page: IPage) (selector: string) : Async<Probes.Scroll option> = ask page Probes.scroll selector

    /// What is wrong, by a measure's own account; empty when its promise holds.
    let verdict (page: IPage) (measure: Probes.Measure) : Async<string list> = ask page (Probes.verdict measure) 0

    /// A verdict measure asked with its parameter.
    let verdictWith (page: IPage) (measure: Probes.Measure) (parameter: int) : Async<string list> =
        ask page (Probes.verdict measure) parameter

    /// The numbers a measure reads.
    let reading (page: IPage) (measure: Probes.Measure) : Async<float list> = ask page (Probes.reading measure) 0

    /// Fold events into the harness, focus a selector the frame after, and say whether focus
    /// is still there two frames on.
    let heldAfterFold (page: IPage) (body: string) (selector: string) : Async<bool> =
        ask page Probes.heldAfterFold (body, selector)

    /// The window as the page lays out in it.
    let window (page: IPage) : Async<Probes.Window> = ask page Probes.window 0

    /// What the page's Content-Security-Policy has refused since it loaded.
    let violations (page: IPage) : Async<string list> = ask page Probes.violations 0

    /// Where the first occurrence of some words in an element is drawn.
    let textAt (page: IPage) (selector: string) (words: string) : Async<Probes.Box option> =
        ask page Probes.textAt (selector, words)

    /// What the document's selection holds, as text.
    let selection (page: IPage) : Async<string> = ask page Probes.selection 0

    /// Whether the first match is what is painted at its horizontal centre, `below` pixels
    /// down from its top.
    let hitAt (page: IPage) (selector: string) (below: float) : Async<bool> = ask page Probes.hitAt (selector, below)

    /// Whether some words are shown under the first match: in a leaf, painted where it stands.
    let shows (page: IPage) (selector: string) (words: string) : Async<bool> = ask page Probes.shows (selector, words)

    /// Every match as a control, measured where it stands.
    let controls (page: IPage) (selector: string) : Async<Probes.Control list> = ask page Probes.controls (selector, false)

    /// Every match as a control, each brought to the middle of the screen before it is measured.
    let controlsReached (page: IPage) (selector: string) : Async<Probes.Control list> =
        ask page Probes.controls (selector, true)

    /// An attribute of the closest match of a selector to what is painted at a point.
    let attrAt (page: IPage) (x: float) (y: float) (selector: string) (attribute: string) : Async<string option> =
        ask page Probes.attrAt ((x, y), (selector, attribute))

    /// Whether this client kept some words in a store whose name holds `store`.
    let kept (page: IPage) (store: string) (words: string) : Async<bool> = ask page Probes.kept ((store, words), false)

    /// Whether this client kept some words as a terminal PRINTED them, in a store whose name
    /// holds `store`.
    let keptPrinted (page: IPage) (store: string) (words: string) : Async<bool> = ask page Probes.kept ((store, words), true)

    /// The first match's scroll box once it has stood still for three frames.
    let stood (page: IPage) (selector: string) : Async<Probes.Scroll option> = ask page Probes.stood selector

    /// How many matches a person can see.
    let visible (page: IPage) (selector: string) : Async<int> = ask page Probes.visible selector

    /// Whether a media query matches on the page.
    let media (page: IPage) (query: string) : Async<bool> = ask page Probes.media query

    /// Every match's scroll box.
    let scrolls (page: IPage) (selector: string) : Async<Probes.Scroll list> = ask page Probes.scrolls selector

    /// Scroll the first match to this many pixels from its top.
    let scrollTo (page: IPage) (selector: string) (top: float) : Async<Probes.Scroll option> =
        ask page Probes.scrollTo (selector, top)

    /// Bring the first match into view, its top at the top of what scrolls it.
    let reveal (page: IPage) (selector: string) : Async<unit> =
        async {
            match! ask page Probes.reveal (selector, "start") with
            | true -> ()
            | false -> return failwithf "nothing matches %s to bring into view" selector
        }

    /// One computed style property of every match.
    let computedAll (page: IPage) (selector: string) (property: string) : Async<string list> =
        ask page Probes.computedAll (selector, property)

    /// The transitions toggling this class on `<html>` and back starts.
    let transitionsOnToggle (page: IPage) (rootClass: string) : Async<string list> =
        ask page Probes.transitionsOnToggle rootClass

    /// Bring the first match into view, its bottom at the bottom of what scrolls it.
    let revealEnd (page: IPage) (selector: string) : Async<bool> = ask page Probes.reveal (selector, "end")

    /// Where the newest occurrence of `mark` in a surface is drawn.
    let newest (page: IPage) (selector: string) (mark: string) : Async<Probes.Newest option> =
        ask page Probes.newest (selector, mark)

    /// Open every `<details>` a selector names.
    let openAll (page: IPage) (selector: string) : Async<int> = ask page Probes.openAll selector

    /// How many animations the first match is running itself.
    let animating (page: IPage) (selector: string) : Async<int option> = ask page Probes.animating selector

    /// Put a surface's reader at its end or its start.
    let park (page: IPage) (selector: string) (atEnd: bool) : Async<Probes.Scroll option> =
        ask page Probes.park (selector, atEnd)

    /// Wait for every animation on the page that will end to have ended.
    let settled (page: IPage) : Async<unit> = ask page Probes.settled 0 |> Async.Ignore

    /// The IndexedDB databases this page's origin holds, by name.
    let databases (page: IPage) : Async<string list> = ask page Probes.databases 0

    /// Raise an error nothing in the page handles, with `message`.
    let unhandled (page: IPage) (message: string) : Async<unit> = ask page Probes.unhandled message |> Async.Ignore

#endif
