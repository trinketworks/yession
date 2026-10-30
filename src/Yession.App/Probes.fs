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

    /// Raise an error nothing handles, with this message, from a timer: the way an error in
    /// the page's own code reaches the page. Answers once it is set to go.
    let unhandled : Probe<string, bool> =
        { Name = "__probeUnhandled"
          EncodeAsk = Encode.string
          DecodeAsk = Decode.string
          EncodeAnswer = Encode.bool
          DecodeAnswer = Decode.bool }

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
        /// How far a reference's name sits off its line's baseline.
        | ReferenceBaseline
        /// The phone pane's grab edge: its left, its width, the gap to the pane, whether it is
        /// painted at its centre, and the height of that centre.
        | GrabEdgeShape
        /// The shut pane's grab edge: invisible, and not what is painted just inside the
        /// screen's left edge.
        | GrabEdgeClear
        /// The shut pane's edge tab inside the header band and the viewport, big enough to
        /// press, painted where it stands — and, asked with 1, its mark on the title's line.
        | EdgeTabOnBand
        /// An arrangement rather than a judgement, answering nothing: room this many pixels
        /// tall below the shell, and the shell's foot brought to the bottom of the screen.
        | RoomBelowShell
        /// How far the visible area runs past the shell's foot, in CSS pixels.
        | PastShell
        /// Each of the ask card's lines that does not start on the given column.
        | AskCardColumn
        /// Each conversation item that, scrolled to the middle, is not what a press in its
        /// middle lands on.
        | ItemsClearOfCard
        /// An arrangement: every focus from here on recorded as `pane` or `elsewhere`
        /// (`focusStops`), and focus put on the last shell control before the pane. Its verdict
        /// is empty when there was such a control to start from.
        | FocusBeforePane
        /// Fifty records of live output into the harness terminal in one turn, as a burst off
        /// the data channel lands, then the frame after: how many renders that cost, and 1 if
        /// the burst's last record (`burst-mark 51`) is on the page, else 0.
        | Burst
        /// A lone record dispatched from inside a frame nothing has been drawn in yet, and the
        /// page read back in the same breath: empty when it is already on the page.
        | LoneRecordInQuietFrame
        /// The conversation put at its end, then sent smoothly to its top, and read once it has
        /// stood still three frames: where it stopped.
        | SmoothScrollToTop
        /// The chapter name field for `msg-filler-8`: 1 or 0 for whether it is empty, whether
        /// it has a placeholder to offer, and whether that placeholder is painted in any ink.
        | ChapterPlaceholder
        /// Every conversation item's actions control, brought to the middle of the scrollport:
        /// each that is transparent, or is not what a press at its own centre lands on.
        | ItemActionsUnderThumb
        /// The agent's caret against its message's last line: on it, its points on the
        /// baseline and the x-height, just after the last word.
        | AgentCaretOnLetters
        /// The agent's thinking mark, its animations paused and stepped every 20ms through one
        /// cycle: the furthest it paints outside its own box, if it does.
        | ThinkingMarkInsideBox
        /// Where the turn's opening mark stands is remembered; the first word is said
        /// (`HarnessPage.agentSays`); and at the first frame of the caret's arrival, how far it
        /// stands from that place and size, if more than a pixel.
        | FirstWordFromOpening
        /// The composer's draft line, its model picker and its own top edge, in the document's
        /// coordinates (reaching the line can scroll the page).
        | ComposerTops
        /// Empty when a press at Send's centre reaches Send; otherwise what it reaches, and
        /// where both stand.
        | SendAnswers
        /// The rewound replay's screen sampled every 25ms for 100ms, play pressed, and sampled
        /// for 700ms more: each sample that does not hold the landing (`line 3`).
        | PlayCarriesOn
        /// What has not yet settled of the content column opening: its animation, the
        /// scrollback inside it, and focus handed into it.
        | ColumnOpened
        /// An arrangement: once the opened `block-burst-running` is over 1000px tall, its middle
        /// scrolled to the top of the scrollback. Empty once it has been.
        | BurstMiddleToTop
        /// After a frame, what of `block-burst-running`'s command row is not held at the top of
        /// the scrollback, painted, while the block itself is far above.
        | CommandRowHeld
        /// `block-burst-running`'s output: how many nodes it is drawn in, and 1 if `line 1500`
        /// is among them, else 0. Nothing drawn reads as no numbers.
        | OutputNodes
        /// What of a folded command shown in its terminal has not landed: its run open, the
        /// command in view and painted, its mark playing, and focus back on the chip.
        | FoldedCommandLanded
        /// An arrangement and a verdict: every cell of the `work_sandboxes` panel given a value
        /// longer than the lane, then each part of the panel that overflows itself or the panel.
        | LongValueInLane
        /// An arrangement and a verdict: every value of the `work_sandboxes` panel given one
        /// long value, measured, then every label given one longer than the lane: each value
        /// whose width the label changed.
        | LabelCostsValue

    [<RequireQualifiedAccess>]
    module Measure =
        let name (measure: Measure) : string =
            match measure with
            | Measure.RunningMarkOnTitle -> "running-mark-on-title"
            | Measure.CauseHeadsOnLines -> "cause-heads-on-lines"
            | Measure.RunningMarkOnCauseLine -> "running-mark-on-cause-line"
            | Measure.FoldArrowsOnRail -> "fold-arrows-on-rail"
            | Measure.FoldArrowsOnAvatars -> "fold-arrows-on-avatars"
            | Measure.ReferenceBaseline -> "reference-baseline"
            | Measure.GrabEdgeShape -> "grab-edge-shape"
            | Measure.GrabEdgeClear -> "grab-edge-clear"
            | Measure.EdgeTabOnBand -> "edge-tab-on-band"
            | Measure.RoomBelowShell -> "room-below-shell"
            | Measure.PastShell -> "past-shell"
            | Measure.AskCardColumn -> "ask-card-column"
            | Measure.ItemsClearOfCard -> "items-clear-of-card"
            | Measure.FocusBeforePane -> "focus-before-pane"
            | Measure.Burst -> "burst"
            | Measure.LoneRecordInQuietFrame -> "lone-record-in-quiet-frame"
            | Measure.SmoothScrollToTop -> "smooth-scroll-to-top"
            | Measure.ChapterPlaceholder -> "chapter-placeholder"
            | Measure.ItemActionsUnderThumb -> "item-actions-under-thumb"
            | Measure.AgentCaretOnLetters -> "agent-caret-on-letters"
            | Measure.ThinkingMarkInsideBox -> "thinking-mark-inside-box"
            | Measure.FirstWordFromOpening -> "first-word-from-opening"
            | Measure.ComposerTops -> "composer-tops"
            | Measure.SendAnswers -> "send-answers"
            | Measure.PlayCarriesOn -> "play-carries-on"
            | Measure.ColumnOpened -> "column-opened"
            | Measure.BurstMiddleToTop -> "burst-middle-to-top"
            | Measure.CommandRowHeld -> "command-row-held"
            | Measure.OutputNodes -> "output-nodes"
            | Measure.FoldedCommandLanded -> "folded-command-landed"
            | Measure.LongValueInLane -> "long-value-in-lane"
            | Measure.LabelCostsValue -> "label-costs-value"

        let all =
            [ Measure.RunningMarkOnTitle
              Measure.CauseHeadsOnLines
              Measure.RunningMarkOnCauseLine
              Measure.FoldArrowsOnRail
              Measure.FoldArrowsOnAvatars
              Measure.ReferenceBaseline
              Measure.GrabEdgeShape
              Measure.GrabEdgeClear
              Measure.EdgeTabOnBand
              Measure.RoomBelowShell
              Measure.PastShell
              Measure.AskCardColumn
              Measure.ItemsClearOfCard
              Measure.FocusBeforePane
              Measure.Burst
              Measure.LoneRecordInQuietFrame
              Measure.SmoothScrollToTop
              Measure.ChapterPlaceholder
              Measure.ItemActionsUnderThumb
              Measure.AgentCaretOnLetters
              Measure.ThinkingMarkInsideBox
              Measure.FirstWordFromOpening
              Measure.ComposerTops
              Measure.SendAnswers
              Measure.PlayCarriesOn
              Measure.ColumnOpened
              Measure.BurstMiddleToTop
              Measure.CommandRowHeld
              Measure.OutputNodes
              Measure.FoldedCommandLanded
              Measure.LongValueInLane
              Measure.LabelCostsValue ]

    /// How many times this document has rendered — published by `app/browser/Render.fs`,
    /// absent until the first render.
    let renders : Fable.BrowserExtras.PageGlobal<int> = Fable.BrowserExtras.PageGlobal.named "__yessionRenders"

    /// How many times this document's view has scanned its viewports for what to draw —
    /// published by `app/browser/Screens.fs`, absent until the first scan.
    let scans : Fable.BrowserExtras.PageGlobal<int> = Fable.BrowserExtras.PageGlobal.named "__yessionViewportScans"

    /// Where focus has landed since `FocusBeforePane` began recording, in order: `pane` for a
    /// stop inside the content pane, `elsewhere` for any other.
    let focusStops : Fable.BrowserExtras.PageGlobal<string array> = Fable.BrowserExtras.PageGlobal.named "__focusStops"

    /// What is wrong, by the measure's own account; empty when its promise holds. Asked with
    /// a number the measure may read as a parameter (`EdgeTabOnBand` reads 1 as "on a desktop").
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

    /// Where the FIRST occurrence of some words in an element is drawn; `None` where the
    /// element is not there or does not hold them.
    let textAt : Probe<string * string, Box option> =
        { Name = "__probeTextAt"
          EncodeAsk = fun (selector, words) -> Encode.list [ Encode.string selector; Encode.string words ]
          DecodeAsk = Decode.tuple2 Decode.string Decode.string
          EncodeAnswer = Encode.option Box.encode
          DecodeAnswer = Decode.option Box.decode }

    /// What the document's selection holds, as text.
    let selection : Probe<int, string> =
        { Name = "__probeSelection"
          EncodeAsk = Encode.int
          DecodeAsk = Decode.int
          EncodeAnswer = Encode.string
          DecodeAnswer = Decode.string }

    /// Whether the first match is what is painted at its horizontal centre, this many pixels
    /// below its top — for an element a sticky line or a drawer may stand over at its head.
    let hitAt : Probe<string * float, bool> =
        { Name = "__probeHitAt"
          EncodeAsk = fun (selector, below) -> Encode.list [ Encode.string selector; Encode.float below ]
          DecodeAsk = Decode.tuple2 Decode.string Decode.float
          EncodeAnswer = Encode.bool
          DecodeAnswer = Decode.bool }

    /// Whether some words are SHOWN under the first match: an element with no element children
    /// whose text holds them, with an area, and painted at its own centre — so words laid out
    /// under something that covers them are not shown.
    let shows : Probe<string * string, bool> =
        { Name = "__probeShows"
          EncodeAsk = fun (selector, words) -> Encode.list [ Encode.string selector; Encode.string words ]
          DecodeAsk = Decode.tuple2 Decode.string Decode.string
          EncodeAnswer = Encode.bool
          DecodeAnswer = Decode.bool }

    /// One control as a thumb or a keyboard meets it: where it is and whether it is what is
    /// painted at its centre, how opaque it is all the way up to the page, its computed font
    /// size, whether it is a field (held to a height only), whether `inert` or `aria-hidden`
    /// takes it out of reach, a short name (its first `data-*` attribute other than
    /// `data-pane-tab`, else its label, else its tag) and its opening tag, which tells two
    /// controls with the same name apart.
    [<RequireQualifiedAccess>]
    type Control =
        { Seen: Seen
          Opacity: float
          FontSize: float
          Field: bool
          Exempt: bool
          Name: string
          Opening: string }

    [<RequireQualifiedAccess>]
    module Control =
        let encode (control: Control) =
            Encode.object
                [ "seen", Seen.encode control.Seen
                  "opacity", Encode.float control.Opacity
                  "fontSize", Encode.float control.FontSize
                  "field", Encode.bool control.Field
                  "exempt", Encode.bool control.Exempt
                  "name", Encode.string control.Name
                  "opening", Encode.string control.Opening ]

        let decode : Decoder<Control> =
            Decode.object (fun get ->
                { Control.Seen = get.Required.Field "seen" Seen.decode
                  Control.Opacity = get.Required.Field "opacity" Decode.float
                  Control.FontSize = get.Required.Field "fontSize" Decode.float
                  Control.Field = get.Required.Field "field" Decode.bool
                  Control.Exempt = get.Required.Field "exempt" Decode.bool
                  Control.Name = get.Required.Field "name" Decode.string
                  Control.Opening = get.Required.Field "opening" Decode.string })

    /// Every match of a selector as a control, in document order — each first brought to the
    /// middle of the screen when asked with `true`, so what is measured is what a thumb that
    /// scrolled to it would meet.
    let controls : Probe<string * bool, Control list> =
        { Name = "__probeControls"
          EncodeAsk = fun (selector, reveal) -> Encode.list [ Encode.string selector; Encode.bool reveal ]
          DecodeAsk = Decode.tuple2 Decode.string Decode.bool
          EncodeAnswer = List.map Control.encode >> Encode.list
          DecodeAnswer = Decode.list Control.decode }

    /// The value of an attribute on the closest match of a selector to what is painted at a
    /// point in the viewport; `None` where nothing is painted there or nothing above it matches.
    let attrAt : Probe<(float * float) * (string * string), string option> =
        { Name = "__probeAttrAt"
          EncodeAsk =
            fun ((x, y), (selector, attribute)) ->
                Encode.list [ Encode.float x; Encode.float y; Encode.string selector; Encode.string attribute ]
          DecodeAsk =
            Decode.tuple4 Decode.float Decode.float Decode.string Decode.string
            |> Decode.map (fun (x, y, selector, attribute) -> (x, y), (selector, attribute))
          EncodeAnswer = Encode.option Encode.string
          DecodeAnswer = Decode.option Decode.string }

    /// A page's render count once it has stood still: polled every 250ms until it has not
    /// moved for six polls running, or 30s have passed. The render and scan counts as they then
    /// stand (`None` where the page never published one), and whether it did stand still.
    [<RequireQualifiedAccess>]
    type Quiet = { Renders: int option; Scans: int option; Still: bool }

    [<RequireQualifiedAccess>]
    module Quiet =
        let encode (quiet: Quiet) =
            Encode.object
                [ "renders", Encode.option Encode.int quiet.Renders
                  "scans", Encode.option Encode.int quiet.Scans
                  "still", Encode.bool quiet.Still ]

        let decode : Decoder<Quiet> =
            Decode.object (fun get ->
                { Quiet.Renders = get.Optional.Field "renders" Decode.int
                  Quiet.Scans = get.Optional.Field "scans" Decode.int
                  Quiet.Still = get.Required.Field "still" Decode.bool })

    let quiet : Probe<int, Quiet> =
        { Name = "__probeQuiet"
          EncodeAsk = Encode.int
          DecodeAsk = Decode.int
          EncodeAnswer = Quiet.encode
          DecodeAnswer = Quiet.decode }

    /// What the page is, for a failing case to print beside its error: one line per fact —
    /// where it is, its title, its connection and degraded tokens, the start of its
    /// conversation, where the keyboard is, and what this client KEPT, store by store and
    /// address by address with the start of each body.
    /// Every refusal this page's Content-Security-Policy has made since the probes were
    /// installed, before the page's own first script: `<directive> <what was refused>`.
    let violations : Probe<int, string list> =
        { Name = "__probeViolations"
          EncodeAsk = Encode.int
          DecodeAsk = Decode.int
          EncodeAnswer = List.map Encode.string >> Encode.list
          DecodeAnswer = Decode.list Decode.string }

    let pageState : Probe<int, string list> =
        { Name = "__probePageState"
          EncodeAsk = Encode.int
          DecodeAsk = Decode.int
          EncodeAnswer = List.map Encode.string >> Encode.list
          DecodeAnswer = Decode.list Decode.string }

    /// Whether this client KEPT some words: a store whose name holds the first part, with an
    /// entry whose body holds the second — or, asked with `true`, a LINE of a body holding them
    /// beside an asciicast `"o"`, which is what a terminal printed rather than the `"i"` input
    /// line of the command that caused it. A store that cannot be read has kept nothing.
    let kept : Probe<(string * string) * bool, bool> =
        { Name = "__probeKept"
          EncodeAsk =
            fun ((store, words), printed) ->
                Encode.list [ Encode.string store; Encode.string words; Encode.bool printed ]
          DecodeAsk =
            Decode.tuple3 Decode.string Decode.string Decode.bool
            |> Decode.map (fun (store, words, printed) -> (store, words), printed)
          EncodeAnswer = Encode.bool
          DecodeAnswer = Decode.bool }

    /// Whether the page has a service worker registration with an ACTIVE worker. Asked of
    /// `getRegistration`, which answers at once, rather than `ready`, which never settles where
    /// no worker will activate.
    let workerActive : Probe<int, bool> =
        { Name = "__probeWorkerActive"
          EncodeAsk = Encode.int
          DecodeAsk = Decode.int
          EncodeAnswer = Encode.bool
          DecodeAnswer = Decode.bool }

    /// The installed app as the browser resolves it from the page's manifest link: the path its
    /// start URL resolves to, and its first icon fetched — the path it was served from, the
    /// status and the content type.
    [<RequireQualifiedAccess>]
    type Installed = { Start: string; Icon: string; Status: int; ContentType: string option }

    [<RequireQualifiedAccess>]
    module Installed =
        let encode (installed: Installed) =
            Encode.object
                [ "start", Encode.string installed.Start
                  "icon", Encode.string installed.Icon
                  "status", Encode.int installed.Status
                  "contentType", Encode.option Encode.string installed.ContentType ]

        let decode : Decoder<Installed> =
            Decode.object (fun get ->
                { Installed.Start = get.Required.Field "start" Decode.string
                  Installed.Icon = get.Required.Field "icon" Decode.string
                  Installed.Status = get.Required.Field "status" Decode.int
                  Installed.ContentType = get.Optional.Field "contentType" Decode.string })

    /// `None` where the page links no manifest, or the manifest names no start or icon.
    let installed : Probe<int, Installed option> =
        { Name = "__probeInstalled"
          EncodeAsk = Encode.int
          DecodeAsk = Decode.int
          EncodeAnswer = Encode.option Installed.encode
          DecodeAnswer = Decode.option Installed.decode }

    /// How many matches a person can SEE: each hit-tested at its centre, clamped inside the
    /// window, and seen when what is painted there is it, inside it, or holds it. Neither
    /// `offsetParent` (null for anything fixed) nor a box (non-zero for contents clipped to
    /// nothing by an `overflow: hidden` ancestor) can say so.
    let visible : Probe<string, int> =
        { Name = "__probeVisible"
          EncodeAsk = Encode.string
          DecodeAsk = Decode.string
          EncodeAnswer = Encode.int
          DecodeAnswer = Decode.int }

    /// The first match's scroll box once it has stood still — its top unmoved for three frames
    /// running, wherever that is. For a scroll still going after the gesture that started it:
    /// a thumb lifted is a scroll still going. `None` where nothing matches.
    let stood : Probe<string, Scroll option> =
        { Name = "__probeStood"
          EncodeAsk = Encode.string
          DecodeAsk = Decode.string
          EncodeAnswer = Encode.option Scroll.encode
          DecodeAnswer = Decode.option Scroll.decode }

    /// Whether the page's media query matches, as `matchMedia` answers it.
    let media : Probe<string, bool> =
        { Name = "__probeMedia"
          EncodeAsk = Encode.string
          DecodeAsk = Decode.string
          EncodeAnswer = Encode.bool
          DecodeAnswer = Decode.bool }
