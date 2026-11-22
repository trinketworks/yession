namespace Yession.Session

open Yession.Domain

#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

/// The bytes on the door of a session: the socket through which a program on the host runs
/// one command in one sandbox (`EntryDoor` listens, and the `yession-enter` bin dials).
///
/// The wire is private. `yession-enter` is the contract, and a deployment calls that bin.
/// The two ends can be different builds, because a session runs its old build until it
/// stops. The first line thus carries a version, and each end refuses a version that it does
/// not know.
///
/// The wire has two parts.
///
/// 1. One line of JSON from the caller, and one line of JSON back. A line ends with a line
///    feed.
/// 2. Frames in both directions, when the answer admitted the caller. A frame is one byte
///    for its type, four bytes for the length of its payload (the first byte is the most
///    significant), and the payload.
///
/// See docs/decisions/2026-11-22-a-sandbox-has-a-door-on-a-socket.md.
module EntryWire =

    /// The version of this wire.
    let version = 1

    /// The longest first line or answer, in bytes. A longer one is not a line of this wire.
    let maxLine = 65536

    /// The longest payload of one frame, in bytes.
    let maxPayload = 1048576

    /// The longest payload that `framesOf` puts in one frame.
    let pieceSize = 65536

    // --- the first line and its answer -------------------------------------------------------

    /// The first line: what the caller asks for.
    type Hello =
        { /// The sandbox, as `SandboxRef.render` writes it.
          ToSandbox : string
          /// Who enters, as the caller says it. The Session records it and does not check it.
          AsSubject : string
          /// How the caller came, as the deployment labels it.
          Label : string
          /// The command and its arguments.
          Argv : string list }

    /// The answer to the first line.
    [<RequireQualifiedAccess>]
    type Answer =
        /// The command started. Frames follow.
        | Admitted
        /// The Session refused, for this reason. Nothing follows.
        | Refused of reason: string

    /// The first line of a caller, with its line feed.
    let hello (hello: Hello) : string =
        let value : JsonValue =
            Encode.object
                [ "v", Encode.int version
                  "sandbox", Encode.string hello.ToSandbox
                  "as", Encode.string hello.AsSubject
                  "via", Encode.string hello.Label
                  "command", hello.Argv |> List.map Encode.string |> Encode.list ]
        Encode.toString 0 value + "\n"

    /// Read the first line of a caller. The error is a sentence for the caller.
    ///
    /// The version is read first and alone. A line of a later version can have a different
    /// shape, and the refusal must name the version, not a field.
    let readHello (line: string) : Result<Hello, string> =
        match Decode.fromString (Decode.field "v" Decode.int) line with
        | Error _ -> Error "The first line is not a line of the entry wire. Use the yession-enter bin of this build."
        | Ok given when given <> version ->
            Error (
                sprintf
                    "The caller speaks version %d of the entry wire, and this session speaks version %d. Use the yession-enter bin of the build that this session runs."
                    given
                    version)
        | Ok _ ->
            let decoder : Decoder<Hello> =
                Decode.object (fun get ->
                    { ToSandbox = get.Required.Field "sandbox" Decode.string
                      AsSubject = get.Required.Field "as" Decode.string
                      Label = get.Required.Field "via" Decode.string
                      Argv = get.Required.Field "command" (Decode.list Decode.string) })
            match Decode.fromString decoder line with
            | Ok read -> Ok read
            | Error _ -> Error "The first line does not name a sandbox, a subject, a label and a command."

    /// The answer of the Session, with its line feed.
    let answer (answer: Answer) : string =
        let value : JsonValue =
            match answer with
            | Answer.Admitted -> Encode.object [ "ok", Encode.bool true ]
            | Answer.Refused reason -> Encode.object [ "ok", Encode.bool false; "reason", Encode.string reason ]
        Encode.toString 0 value + "\n"

    /// Read the answer of the Session. The error is a sentence for the person who ran the
    /// caller.
    let readAnswer (line: string) : Result<Answer, string> =
        let decoder : Decoder<Answer> =
            Decode.field "ok" Decode.bool
            |> Decode.andThen (fun admitted ->
                if admitted then Decode.succeed Answer.Admitted
                else Decode.field "reason" Decode.string |> Decode.map Answer.Refused)
        match Decode.fromString decoder line with
        | Ok read -> Ok read
        | Error _ -> Error "The answer of the door is not a line of the entry wire. The session can run a different build."

    /// What a reader of the first line or of the answer has after one more chunk.
    [<RequireQualifiedAccess>]
    type Line =
        /// The line, without its line feed, and the bytes that came after it.
        | Whole of text: string * rest: byte []
        /// No line feed yet. These bytes are the start of the line.
        | Partial of soFar: byte []
        /// More than `maxLine` bytes came with no line feed.
        | TooLong

    /// Add one chunk to the start of a line.
    let line (soFar: byte []) (chunk: byte []) : Line =
        let all : byte [] = if soFar.Length = 0 then chunk else Array.append soFar chunk
        match all |> Array.tryFindIndex (fun value -> value = 10uy) with
        | Some at when at <= maxLine ->
            Line.Whole (System.Text.Encoding.UTF8.GetString (Array.sub all 0 at), Array.sub all (at + 1) (all.Length - at - 1))
        | Some _ -> Line.TooLong
        | None when all.Length > maxLine -> Line.TooLong
        | None -> Line.Partial all

    /// The bytes of a line of text.
    let bytesOf (text: string) : byte [] = System.Text.Encoding.UTF8.GetBytes text

    // --- frames ------------------------------------------------------------------------------

    /// One frame.
    [<RequireQualifiedAccess>]
    type Frame =
        /// Caller to Session: bytes for the input of the command.
        | Input of byte []
        /// Caller to Session: no more input comes.
        | InputClosed
        /// Session to caller: bytes that the command wrote to its output.
        | Output of byte []
        /// Session to caller: bytes that the command wrote to its error output.
        | ErrorOutput of byte []
        /// Session to caller: the command ended with this code. No frame follows.
        | Exited of code: int

    let private inputTag = 0uy

    let private inputClosedTag = 1uy

    let private outputTag = 2uy

    let private errorOutputTag = 3uy

    let private exitedTag = 4uy

    let private framed (tag: byte) (payload: byte []) : byte [] =
        let length = payload.Length
        let header : byte [] =
            [| tag
               byte ((length >>> 24) &&& 0xFF)
               byte ((length >>> 16) &&& 0xFF)
               byte ((length >>> 8) &&& 0xFF)
               byte (length &&& 0xFF) |]
        if length = 0 then header else Array.append header payload

    let private noBytes : byte [] = Array.zeroCreate 0

    /// The bytes of one frame. The caller keeps a payload to `maxPayload` (`framesOf`).
    let frame (frame: Frame) : byte [] =
        match frame with
        | Frame.Input bytes -> framed inputTag bytes
        | Frame.InputClosed -> framed inputClosedTag noBytes
        | Frame.Output bytes -> framed outputTag bytes
        | Frame.ErrorOutput bytes -> framed errorOutputTag bytes
        | Frame.Exited code ->
            let payload : byte [] =
                [| byte ((code >>> 24) &&& 0xFF)
                   byte ((code >>> 16) &&& 0xFF)
                   byte ((code >>> 8) &&& 0xFF)
                   byte (code &&& 0xFF) |]
            framed exitedTag payload

    /// The pieces of one chunk, each no longer than `pieceSize`, in order. A chunk with no
    /// bytes has no pieces.
    let pieces (chunk: byte []) : byte [] list =
        if chunk.Length <= pieceSize then (if chunk.Length = 0 then [] else [ chunk ])
        else
            [ for start in 0 .. pieceSize .. chunk.Length - 1 ->
                  Array.sub chunk start (min pieceSize (chunk.Length - start)) ]

    /// The frames that carry one chunk, as bytes. `wrap` names the direction: `Frame.Input`,
    /// `Frame.Output` or `Frame.ErrorOutput`.
    let framesOf (wrap: byte [] -> Frame) (chunk: byte []) : byte [] list =
        pieces chunk |> List.map (fun piece -> frame (wrap piece))

    /// The bytes that a reader of frames holds between two chunks: the start of a frame whose
    /// end did not come yet.
    type FrameReader = private { Held : byte [] }

    module FrameReader =

        /// A reader that holds nothing.
        let empty : FrameReader = { Held = noBytes }

        /// How many bytes the reader holds.
        let held (reader: FrameReader) : int = reader.Held.Length

        let private read (tag: byte) (payload: byte []) : Result<Frame, string> =
            if tag = inputTag then Ok (Frame.Input payload)
            elif tag = inputClosedTag then Ok Frame.InputClosed
            elif tag = outputTag then Ok (Frame.Output payload)
            elif tag = errorOutputTag then Ok (Frame.ErrorOutput payload)
            elif tag = exitedTag then
                if payload.Length <> 4 then Error "An exit frame has four bytes, and this one does not."
                else
                    Ok (
                        Frame.Exited (
                            (int payload.[0] <<< 24) ||| (int payload.[1] <<< 16) ||| (int payload.[2] <<< 8) ||| int payload.[3]
                        )
                    )
            else Error (sprintf "The entry wire has no frame of type %d." (int tag))

        /// Add one chunk. The answer is each frame that is now whole, in order, and the reader
        /// for the next chunk. An error is a stream that is not this wire, and the stream is
        /// of no more use.
        let feed (reader: FrameReader) (chunk: byte []) : Result<Frame list * FrameReader, string> =
            let all : byte [] = if reader.Held.Length = 0 then chunk else Array.append reader.Held chunk
            let rest (from: int) : FrameReader = { Held = Array.sub all from (all.Length - from) }
            let rec go (at: int) (frames: Frame list) : Result<Frame list * FrameReader, string> =
                if all.Length - at < 5 then Ok (List.rev frames, rest at)
                else
                    let length =
                        (int all.[at + 1] <<< 24) ||| (int all.[at + 2] <<< 16) ||| (int all.[at + 3] <<< 8) ||| int all.[at + 4]
                    if length < 0 || length > maxPayload then
                        Error (sprintf "A frame of the entry wire holds %d bytes at most, and this one says that it is longer." maxPayload)
                    elif all.Length - at - 5 < length then Ok (List.rev frames, rest at)
                    else
                        match read all.[at] (Array.sub all (at + 5) length) with
                        | Ok frame -> go (at + 5 + length) (frame :: frames)
                        | Error reason -> Error reason
            go 0 []

    // --- the command line of yession-enter ---------------------------------------------------

    /// The exit codes that `yession-enter` gives for its own failures. Each other code is the
    /// code of the command.
    module ExitCode =

        /// `yession-enter` could not reach the door, or the door closed before the command
        /// ended.
        let unreachable = 125

        /// The Session refused the entry.
        let refused = 126

        /// The code that a caller gets for a command that did not end with a code of its own:
        /// a signal ended it, or it could not run.
        let noCode = 255

    /// What `yession-enter` was asked to do.
    type EnterRequest =
        { /// The socket of the door.
          DoorPath : string
          Request : Hello }

    let private doorOption : Cli.Opt<string option> =
        Cli.value "door" "socket" "the socket of the session's door (the sandbox announcement gives it)"

    let private asOption : Cli.Opt<string option> =
        Cli.value "as" "subject" "who enters, for the record of the session"

    let private viaOption : Cli.Opt<string option> =
        Cli.value "via" "label" "how the caller came, for the record of the session (for example ssh)"

    /// The options of `yession-enter`. The sandbox and the command are bare words, which
    /// `Cli` does not read: `words` takes them off first.
    let enterSpec : Cli.Spec =
        Cli.spec "yession-enter" |> Cli.accepts doorOption |> Cli.accepts asOption |> Cli.accepts viaOption

    /// The form of the command line, for a refusal.
    let enterForm = "yession-enter --door <socket> --as <subject> --via <label> <sandbox> -- <command> [argument ...]"

    /// The three parts of a command line: the options, the sandbox, and the command after
    /// `--`. The sandbox is the last word before `--`, when that word is not an option and is
    /// not the value of one.
    let words (args: string list) : string list * string option * string list =
        let before, command =
            match args |> List.tryFindIndex (fun word -> word = "--") with
            | Some at -> List.take at args, List.skip (at + 1) args
            | None -> args, []
        let takesValue (word: string) : bool = word = "--door" || word = "--as" || word = "--via"
        match List.rev before with
        | last :: earlier when not (last.StartsWith "-") ->
            let isValue =
                match earlier with
                | previous :: _ -> takesValue previous
                | [] -> false
            if isValue then before, None, command else List.rev earlier, Some last, command
        | _ -> before, None, command

    /// The request of a parsed command line. The error is a sentence that names what is
    /// missing.
    let enterRequest (parsed: Cli.Parsed) (sandbox: string option) (command: string list) : Result<EnterRequest, string> =
        match Cli.valueOf doorOption parsed, Cli.valueOf asOption parsed, Cli.valueOf viaOption parsed, sandbox, command with
        | None, _, _, _, _ -> Error (sprintf "--door is missing. The form is: %s" enterForm)
        | _, None, _, _, _ -> Error (sprintf "--as is missing. The form is: %s" enterForm)
        | _, _, None, _, _ -> Error (sprintf "--via is missing. The form is: %s" enterForm)
        | _, _, _, None, _ -> Error (sprintf "The sandbox is missing. The form is: %s" enterForm)
        | _, _, _, _, [] -> Error (sprintf "The command after -- is missing. The form is: %s" enterForm)
        | Some door, Some subject, Some label, Some sandbox, command ->
            Ok
                { DoorPath = door
                  Request = { ToSandbox = sandbox; AsSubject = subject; Label = label; Argv = command } }

/// Where the door of a session is: the rules for the directory that an operator gives
/// (`YESSION_SESSION_DOORS`), and the socket of one session in it.
module DoorPlace =

    /// The longest path of a socket, in bytes with the byte that ends it, as the platform
    /// has it: 104 on macOS and 108 on Linux.
    let longestPath (onMac: bool) : int = if onMac then 104 else 108

    /// The socket of one session's door: `<directory>/<session id>.sock`.
    ///
    /// The error is a sentence for the operator. A relative directory is refused, because
    /// two processes can read it from two places. A directory in the data directory is
    /// refused, because a sandbox can hold a grant of a part of that tree, and a sandbox
    /// that can see a socket can dial it. A path that the platform cannot bind is refused
    /// here, with its length, and not by the system call.
    let socket (longest: int) (dataDirectory: string) (directory: string) (session: SessionId) : Result<string, string> =
        let trimmed (path: string) : string = if path.Length > 1 then path.TrimEnd '/' else path
        let doors = trimmed directory
        let data = trimmed dataDirectory
        let path = sprintf "%s/%s.sock" doors (SessionId.value session)
        let bytes = (System.Text.Encoding.UTF8.GetBytes path).Length
        if not (doors.StartsWith "/") then
            Error (sprintf "YESSION_SESSION_DOORS is '%s', which is not an absolute path. Give the full path of a directory." directory)
        elif doors = data || doors.StartsWith (data + "/") then
            Error (
                sprintf
                    "YESSION_SESSION_DOORS is '%s', which is in the data directory '%s'. A sandbox can see a part of that tree. Give a directory outside it."
                    directory
                    dataDirectory)
        elif bytes >= longest then
            Error (
                sprintf
                    "The socket of this session's door is '%s', which is %d bytes. This platform binds a socket path of %d bytes at most. Give YESSION_SESSION_DOORS a shorter directory."
                    path
                    bytes
                    (longest - 1))
        else Ok path
