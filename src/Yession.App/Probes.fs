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
