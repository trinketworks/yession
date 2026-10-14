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

    /// What a command call (`ToolUse.runsCommand`) asked to run, and what it said the command
    /// is for, read from its recorded arguments — so the call's chip can say what its block
    /// will say before there is a block. `None` when nothing readable was recorded; the
    /// description is `None` when the call gave none, as a block's is.
    let command (use': ToolUse) : (string * string option) option =
        let fields =
            Decode.object (fun get ->
                get.Required.Field "command" Decode.string, get.Optional.Field "description" Decode.string)
        use'.Arguments |> Option.bind (fun raw -> Decode.fromString fields raw |> Result.toOption)
