namespace Yession.App

#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

/// The measurement contract between the browser cases and the page they look at: what a test
/// can ask a page that a locator cannot answer — where a thing is drawn, whether it is the
/// thing painted at its own centre, what a frame does — named once, with the shape of what is
/// asked and of what comes back.
///
/// The half that MEASURES is F# compiled for the browser (`app/browser/Probes.fs`, bundled by
/// the test build and installed into every context the suite opens, never into the product).
/// The half that ASKS is the suite (`tests/Yession.Tests/BrowserProbe.fs`). Neither writes
/// JavaScript: a question crosses as JSON these codecs wrote and the answer comes back as JSON
/// they read, so a probe that changes shape fails to compile on both sides at once rather than
/// arriving as an `undefined` somewhere inside a predicate string.
///
/// Measurements are taken in ONE call per question. A comparison between two boxes read in two
/// round trips compares two frames, and a layout that moves between them is a flake that looks
/// like a fault.
[<RequireQualifiedAccess>]
module Probes =

    /// An element's border box in viewport pixels, as `getBoundingClientRect` reports it.
    [<RequireQualifiedAccess>]
    type Box =
        { Top: float
          Bottom: float
          Left: float
          Right: float }

    [<RequireQualifiedAccess>]
    module Box =
        /// A box from its four edges. A function rather than a record expression at the call
        /// site, because outside this module `Box.Top` names this module before the field.
        let create (top: float) (bottom: float) (left: float) (right: float) : Box =
            { Box.Top = top; Box.Bottom = bottom; Box.Left = left; Box.Right = right }

        let width (box: Box) = box.Right - box.Left
        let height (box: Box) = box.Bottom - box.Top

        /// A box with no area is one nothing can be seen in.
        let isEmpty (box: Box) = width box <= 0.0 || height box <= 0.0

        let encode (box: Box) =
            Encode.object
                [ "top", Encode.float box.Top
                  "bottom", Encode.float box.Bottom
                  "left", Encode.float box.Left
                  "right", Encode.float box.Right ]

        let decode : Decoder<Box> =
            Decode.object (fun get ->
                { Box.Top = get.Required.Field "top" Decode.float
                  Box.Bottom = get.Required.Field "bottom" Decode.float
                  Box.Left = get.Required.Field "left" Decode.float
                  Box.Right = get.Required.Field "right" Decode.float })

    /// One element matching a selector, as a reader would find it: where it is drawn, and
    /// whether it is what is PAINTED at its own centre — the hit test that tells an element on
    /// screen from one with a non-zero box that something else is covering, or that an
    /// `overflow: hidden` ancestor has clipped to nothing. And what it says, as `textContent`.
    [<RequireQualifiedAccess>]
    type Seen = { Box: Box; Painted: bool; Text: string }

    [<RequireQualifiedAccess>]
    module Seen =
        let create (box: Box) (painted: bool) (text: string) : Seen =
            { Seen.Box = box; Seen.Painted = painted; Seen.Text = text }

        let encode (seen: Seen) =
            Encode.object
                [ "box", Box.encode seen.Box
                  "painted", Encode.bool seen.Painted
                  "text", Encode.string seen.Text ]

        let decode : Decoder<Seen> =
            Decode.object (fun get ->
                { Seen.Box = get.Required.Field "box" Box.decode
                  Seen.Painted = get.Required.Field "painted" Decode.bool
                  Seen.Text = get.Required.Field "text" Decode.string })

    /// A question a page can be asked: its name on the page, and the codecs for what is asked
    /// and what comes back. The page publishes a function of one JSON string to a JSON string
    /// (or a promise of one) under `Name`.
    type Probe<'Ask, 'Answer> =
        { Name: string
          EncodeAsk: 'Ask -> JsonValue
          DecodeAsk: Decoder<'Ask>
          EncodeAnswer: 'Answer -> JsonValue
          DecodeAnswer: Decoder<'Answer> }

    /// Every element matching a selector, in document order.
    let seen : Probe<string, Seen list> =
        { Name = "__probeSeen"
          EncodeAsk = Encode.string
          DecodeAsk = Decode.string
          EncodeAnswer = List.map Seen.encode >> Encode.list
          DecodeAnswer = Decode.list Seen.decode }

    /// The first match of each selector, all measured in one frame; `None` where nothing
    /// matches.
    let boxes : Probe<string list, Box option list> =
        { Name = "__probeBoxes"
          EncodeAsk = List.map Encode.string >> Encode.list
          DecodeAsk = Decode.list Decode.string
          EncodeAnswer = List.map (Encode.option Box.encode) >> Encode.list
          DecodeAnswer = Decode.list (Decode.option Box.decode) }

    /// One computed style property of the first match; `None` where nothing matches.
    let computed : Probe<string * string, string option> =
        { Name = "__probeComputed"
          EncodeAsk = fun (selector, property) -> Encode.list [ Encode.string selector; Encode.string property ]
          DecodeAsk = Decode.tuple2 Decode.string Decode.string
          EncodeAnswer = Encode.option Encode.string
          DecodeAnswer = Decode.option Decode.string }

    /// Whether the page is being RENDERED: true once an animation frame runs, false if none
    /// does within the given milliseconds. A document whose scripts run while its rendering is
    /// suppressed answers every DOM question and never paints, which reads from outside as a
    /// wait that simply never fires.
    let framing : Probe<int, bool> =
        { Name = "__probeFraming"
          EncodeAsk = Encode.int
          DecodeAsk = Decode.int
          EncodeAnswer = Encode.bool
          DecodeAnswer = Decode.bool }

    /// Wait this many animation frames, answering how many ran. For a case that has to let the
    /// page draw what it was just handed — a frame for a move to be asked, a frame for it to be
    /// made — before it acts again.
    let frames : Probe<int, int> =
        { Name = "__probeFrames"
          EncodeAsk = Encode.int
          DecodeAsk = Decode.int
          EncodeAnswer = Encode.int
          DecodeAnswer = Decode.int }

    /// The text of every match of a selector with the remote carets' widgets left out — a
    /// collaborator's caret is drawn INSIDE the text it sits in, label and all, so the words a
    /// reader is shown are the element's own text without them.
    let ownTexts : Probe<string, string list> =
        { Name = "__probeOwnTexts"
          EncodeAsk = Encode.string
          DecodeAsk = Decode.string
          EncodeAnswer = List.map Encode.string >> Encode.list
          DecodeAnswer = Decode.list Decode.string }

    /// The names of the IndexedDB databases this origin holds.
    let databases : Probe<int, string list> =
        { Name = "__probeDatabases"
          EncodeAsk = Encode.int
          DecodeAsk = Decode.int
          EncodeAnswer = List.map Encode.string >> Encode.list
          DecodeAnswer = Decode.list Decode.string }

    /// Where an element is scrolled to and how far it could be — its own scroll box, as
    /// `scrollTop`/`scrollLeft` and the scroll and client extents report them.
    [<RequireQualifiedAccess>]
    type Scroll =
        { Top: float
          Left: float
          Height: float
          Width: float
          ClientHeight: float
          ClientWidth: float }

    [<RequireQualifiedAccess>]
    module Scroll =
        let create top left height width clientHeight clientWidth : Scroll =
            { Scroll.Top = top
              Scroll.Left = left
              Scroll.Height = height
              Scroll.Width = width
              Scroll.ClientHeight = clientHeight
              Scroll.ClientWidth = clientWidth }

        /// More content than box, sideways.
        let overflowsSideways (scroll: Scroll) = scroll.Width > scroll.ClientWidth + 1.0

        /// Scrolled to its end, within a pixel.
        let atEnd (scroll: Scroll) = scroll.Top + scroll.ClientHeight >= scroll.Height - 1.0

        let encode (scroll: Scroll) =
            Encode.object
                [ "top", Encode.float scroll.Top
                  "left", Encode.float scroll.Left
                  "height", Encode.float scroll.Height
                  "width", Encode.float scroll.Width
                  "clientHeight", Encode.float scroll.ClientHeight
                  "clientWidth", Encode.float scroll.ClientWidth ]

        let decode : Decoder<Scroll> =
            Decode.object (fun get ->
                create
                    (get.Required.Field "top" Decode.float)
                    (get.Required.Field "left" Decode.float)
                    (get.Required.Field "height" Decode.float)
                    (get.Required.Field "width" Decode.float)
                    (get.Required.Field "clientHeight" Decode.float)
                    (get.Required.Field "clientWidth" Decode.float))

    /// The first match's scroll box; `None` where nothing matches.
    let scroll : Probe<string, Scroll option> =
        { Name = "__probeScroll"
          EncodeAsk = Encode.string
          DecodeAsk = Decode.string
          EncodeAnswer = Encode.option Scroll.encode
          DecodeAnswer = Decode.option Scroll.decode }

    /// Scroll the first match to this many pixels from its top, answering where it landed.
    let scrollTo : Probe<string * float, Scroll option> =
        { Name = "__probeScrollTo"
          EncodeAsk = fun (selector, top) -> Encode.list [ Encode.string selector; Encode.float top ]
          DecodeAsk = Decode.tuple2 Decode.string Decode.float
          EncodeAnswer = Encode.option Scroll.encode
          DecodeAnswer = Decode.option Scroll.decode }

    /// Bring the first match into view, aligned as `block` says (`start` or `end`, as
    /// `scrollIntoView` takes it), answering whether there was one.
    let reveal : Probe<string * string, bool> =
        { Name = "__probeReveal"
          EncodeAsk = fun (selector, block) -> Encode.list [ Encode.string selector; Encode.string block ]
          DecodeAsk = Decode.tuple2 Decode.string Decode.string
          EncodeAnswer = Encode.bool
          DecodeAnswer = Decode.bool }

    /// Wait for every animation on the page that will END to have ended, answering how many
    /// there were. One that repeats forever — a pulse — is not waited for: it never finishes.
    let settled : Probe<int, int> =
        { Name = "__probeSettled"
          EncodeAsk = Encode.int
          DecodeAsk = Decode.int
          EncodeAnswer = Encode.int
          DecodeAnswer = Decode.int }

    /// One computed style property of every match, in document order.
    let computedAll : Probe<string * string, string list> =
        { Name = "__probeComputedAll"
          EncodeAsk = fun (selector, property) -> Encode.list [ Encode.string selector; Encode.string property ]
          DecodeAsk = Decode.tuple2 Decode.string Decode.string
          EncodeAnswer = List.map Encode.string >> Encode.list
          DecodeAnswer = Decode.list Decode.string }

    /// Toggle this class on `<html>` and back, answering every CSS transition the two toggles
    /// started — as `<class or tag> (<property>)` — on a page that should start none. Asked
    /// straight after each toggle, because reading the animations flushes style: a transition a
    /// toggle starts is in the answer at once, with no clock to wait on.
    let transitionsOnToggle : Probe<string, string list> =
        { Name = "__probeTransitionsOnToggle"
          EncodeAsk = Encode.string
          DecodeAsk = Decode.string
          EncodeAnswer = List.map Encode.string >> Encode.list
          DecodeAnswer = Decode.list Decode.string }

    /// Where the newest occurrence of some words in a surface is drawn: the LAST occurrence of
    /// the text in the surface, whether that line lies wholly inside the surface's box, whether
    /// what is painted at its centre belongs to the surface — so a line under the composer, a
    /// sticky header or a float reads as not shown — and how far short of its end the surface
    /// stands.
    [<RequireQualifiedAccess>]
    type Newest = { Inside: bool; Painted: bool; Short: float }

    [<RequireQualifiedAccess>]
    module Newest =
        let create (inside: bool) (painted: bool) (short: float) : Newest =
            { Newest.Inside = inside; Newest.Painted = painted; Newest.Short = short }

        let encode (newest: Newest) =
            Encode.object
                [ "inside", Encode.bool newest.Inside
                  "painted", Encode.bool newest.Painted
                  "short", Encode.float newest.Short ]

        let decode : Decoder<Newest> =
            Decode.object (fun get ->
                create
                    (get.Required.Field "inside" Decode.bool)
                    (get.Required.Field "painted" Decode.bool)
                    (get.Required.Field "short" Decode.float))

    /// `None` where the surface is not there or does not hold the words.
    let newest : Probe<string * string, Newest option> =
        { Name = "__probeNewest"
          EncodeAsk = fun (selector, mark) -> Encode.list [ Encode.string selector; Encode.string mark ]
          DecodeAsk = Decode.tuple2 Decode.string Decode.string
          EncodeAnswer = Encode.option Newest.encode
          DecodeAnswer = Decode.option Newest.decode }

    /// Open every `<details>` matching a selector, answering how many.
    let openAll : Probe<string, int> =
        { Name = "__probeOpenAll"
          EncodeAsk = Encode.string
          DecodeAsk = Decode.string
          EncodeAnswer = Encode.int
          DecodeAnswer = Decode.int }

    /// How many animations the first match itself is running; `None` where nothing matches.
    let animating : Probe<string, int option> =
        { Name = "__probeAnimating"
          EncodeAsk = Encode.string
          DecodeAsk = Decode.string
          EncodeAnswer = Encode.option Encode.int
          DecodeAnswer = Decode.option Decode.int }

    /// Put a surface's reader at its end (`true`) or its start, the way a reader does it: its
    /// box brought on screen, then scrolled. Answers where it then stands.
    let park : Probe<string * bool, Scroll option> =
        { Name = "__probePark"
          EncodeAsk = fun (selector, atEnd) -> Encode.list [ Encode.string selector; Encode.bool atEnd ]
          DecodeAsk = Decode.tuple2 Decode.string Decode.bool
          EncodeAnswer = Encode.option Scroll.encode
          DecodeAnswer = Decode.option Scroll.decode }

    /// Every match's scroll box, in document order.
    let scrolls : Probe<string, Scroll list> =
        { Name = "__probeScrolls"
          EncodeAsk = Encode.string
          DecodeAsk = Decode.string
          EncodeAnswer = List.map Scroll.encode >> Encode.list
          DecodeAnswer = Decode.list Scroll.decode }

    /// A measurement one case makes of the page that no general probe answers — a geometry its
    /// own promise is about. Each is F# in `app/browser/Measures.fs`, named here so a case asks
    /// for it by a case of this union rather than by a program in a string. A verdict is the
    /// list of what is wrong, empty when the promise holds; a reading is the numbers the case
    /// judges itself.
    [<RequireQualifiedAccess>]
    type Measure =
        /// A running act's mark against its title's letters and its gutter.
        | RunningMarkOnTitle
        /// Every cause's arrow head against the line it ends, in device pixels.
        | CauseHeadsOnLines
        /// A running act's mark against its cause's line, in device pixels.
        | RunningMarkOnCauseLine
        /// Every fold: how many, the furthest an arrow sits off its title's first line, and how
        /// far the arrows spread across the rail.
        | FoldArrowsOnRail
        /// Every fold under an author line: how many, and the furthest an arrow stands off its
        /// author's avatar column.
        | FoldArrowsOnAvatars

    [<RequireQualifiedAccess>]
    module Measure =
        let name (measure: Measure) : string =
            match measure with
            | Measure.RunningMarkOnTitle -> "running-mark-on-title"
            | Measure.CauseHeadsOnLines -> "cause-heads-on-lines"
            | Measure.RunningMarkOnCauseLine -> "running-mark-on-cause-line"
            | Measure.FoldArrowsOnRail -> "fold-arrows-on-rail"
            | Measure.FoldArrowsOnAvatars -> "fold-arrows-on-avatars"

        let all =
            [ Measure.RunningMarkOnTitle
              Measure.CauseHeadsOnLines
              Measure.RunningMarkOnCauseLine
              Measure.FoldArrowsOnRail
              Measure.FoldArrowsOnAvatars ]

    /// What is wrong, by the measure's own account; empty when its promise holds.
    let verdict (measure: Measure) : Probe<int, string list> =
        { Name = "__probeVerdict-" + Measure.name measure
          EncodeAsk = Encode.int
          DecodeAsk = Decode.int
          EncodeAnswer = List.map Encode.string >> Encode.list
          DecodeAnswer = Decode.list Decode.string }

    /// The numbers a measure reads, for the case to judge.
    let reading (measure: Measure) : Probe<int, float list> =
        { Name = "__probeReading-" + Measure.name measure
          EncodeAsk = Encode.int
          DecodeAsk = Decode.int
          EncodeAnswer = List.map Encode.float >> Encode.list
          DecodeAnswer = Decode.list Decode.float }

    /// Fold a page of events into the harness (`HarnessPage.fold`), put focus on the selector's
    /// first match in the first frame after, and answer whether it is still there two frames
    /// later — all in the page, so no round trip decides which frame the hand lands in.
    let heldAfterFold : Probe<string * string, bool> =
        { Name = "__probeHeldAfterFold"
          EncodeAsk = fun (body, selector) -> Encode.list [ Encode.string body; Encode.string selector ]
          DecodeAsk = Decode.tuple2 Decode.string Decode.string
          EncodeAnswer = Encode.bool
          DecodeAnswer = Decode.bool }

    /// The window as the page lays out in it: its inner size, how wide the document is, and how
    /// tall the visual viewport is (shorter than the inner height while a keyboard is up).
    [<RequireQualifiedAccess>]
    type Window = { InnerWidth: float; InnerHeight: float; DocumentWidth: float; VisualHeight: float }

    [<RequireQualifiedAccess>]
    module Window =
        let create innerWidth innerHeight documentWidth visualHeight : Window =
            { Window.InnerWidth = innerWidth
              Window.InnerHeight = innerHeight
              Window.DocumentWidth = documentWidth
              Window.VisualHeight = visualHeight }

        let encode (window: Window) =
            Encode.object
                [ "innerWidth", Encode.float window.InnerWidth
                  "innerHeight", Encode.float window.InnerHeight
                  "documentWidth", Encode.float window.DocumentWidth
                  "visualHeight", Encode.float window.VisualHeight ]

        let decode : Decoder<Window> =
            Decode.object (fun get ->
                create
                    (get.Required.Field "innerWidth" Decode.float)
                    (get.Required.Field "innerHeight" Decode.float)
                    (get.Required.Field "documentWidth" Decode.float)
                    (get.Required.Field "visualHeight" Decode.float))

    let window : Probe<int, Window> =
        { Name = "__probeWindow"
          EncodeAsk = Encode.int
          DecodeAsk = Decode.int
          EncodeAnswer = Window.encode
          DecodeAnswer = Window.decode }
