namespace Yession.App

open Yession.Domain.Chat

#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

/// A tool call's recorded arguments, as a reader opens them. Here rather than beside
/// `ToolUse` in the domain because it is a rendering, and laying JSON out is the App's
/// business: the domain records the arguments and never reads them.
[<RequireQualifiedAccess>]
module ToolUseText =

    /// The recorded JSON laid out one field per line, so a two-hundred-character
    /// `old_string` reads as text rather than as a run of `\n`s on one row. The RECORD is
    /// untouched — this is a rendering of it — and a record that is not JSON (there are
    /// none, but a renderer does not get to assume) is shown as it is. `None` is a foreign
    /// tool, whose arguments were never recorded.
    let arguments (use': ToolUse) : string option =
        use'.Arguments
        |> Option.map (fun raw ->
            match Decode.fromString Decode.value raw with
            | Ok value -> Encode.toString 2 value
            | Error _ -> raw)
