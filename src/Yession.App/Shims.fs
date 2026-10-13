namespace Yession.App

open Fable.BrowserExtras

#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

/// The instruments a browser case can install into a page before the page's own scripts run,
/// to change or watch what the page's APIs do — and what each publishes for the case to read.
/// The installing half is `app/browser/Shims.fs`; the asking half is the suite's
/// `BrowserShims`. Which shims a page gets crosses as JSON these codecs wrote, never as a
/// program somebody spelled.
[<RequireQualifiedAccess>]
module Shims =

    [<RequireQualifiedAccess>]
    type Shim =
        /// Take the END of ICE gathering — the `complete` state and the null candidate — away
        /// from every peer connection the page makes, and count the candidates instead
        /// (`candidates`).
        | GatheringNeverEnds
        /// Say when a connection has taken its remote description (`answered`): the moment a
        /// handshake's deadline stops meaning "the session did not answer".
        | AnswerApplied
        /// Record which pane tab is selected the instant the strip first holds this many tabs
        /// (`firstOnTop`), before any frame paints it.
        | FirstOnTop of tabs: int
        /// Answer this media query as matching, and every other as the browser would. Chromium
        /// cannot be MADE an installed app, so the page is told it is one by answering its own
        /// question.
        | Matching of query: string

    [<RequireQualifiedAccess>]
    module Shim =
        let encode (shim: Shim) : JsonValue =
            match shim with
            | Shim.GatheringNeverEnds -> Encode.object [ "shim", Encode.string "gathering-never-ends" ]
            | Shim.AnswerApplied -> Encode.object [ "shim", Encode.string "answer-applied" ]
            | Shim.FirstOnTop tabs -> Encode.object [ "shim", Encode.string "first-on-top"; "tabs", Encode.int tabs ]
            | Shim.Matching query -> Encode.object [ "shim", Encode.string "matching"; "query", Encode.string query ]

        let decode : Decoder<Shim> =
            Decode.field "shim" Decode.string
            |> Decode.andThen (fun shim ->
                match shim with
                | "gathering-never-ends" -> Decode.succeed Shim.GatheringNeverEnds
                | "answer-applied" -> Decode.succeed Shim.AnswerApplied
                | "first-on-top" -> Decode.field "tabs" Decode.int |> Decode.map Shim.FirstOnTop
                | "matching" -> Decode.field "query" Decode.string |> Decode.map Shim.Matching
                | other -> Decode.fail (sprintf "no shim is called %s" other))

    /// Which shims this page is to have, as JSON (`Shim.encode`, a list) — published before the
    /// shims bundle runs, which reads it.
    let asked : PageGlobal<string> = PageGlobal.named "__yessionShims"

    /// How many ICE candidates the page's connections have gathered (`GatheringNeverEnds`).
    let candidates : PageGlobal<int> = PageGlobal.named "__yessionCandidates"

    /// Whether a connection has taken its remote description (`AnswerApplied`).
    let answered : PageGlobal<bool> = PageGlobal.named "__yessionAnswered"

    /// The pane tab on top when the strip first held its tabs, or `"nothing"` when none was
    /// selected (`FirstOnTop`).
    let firstOnTop : PageGlobal<string> = PageGlobal.named "__firstOnTop"
