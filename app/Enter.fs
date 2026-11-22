module Yession.Host.Enter

// The `yession-enter` bin: run one command in one sandbox of one session, from the host.
//
//     yession-enter --door <socket> --as <subject> --via <label> [--bare] <sandbox> -- <command> [argument ...]
//
// The input, the output and the error output of this process are those of the command, and
// no byte changes on the way. This process ends with the exit code of the command. It dials
// the door of the session (`EntryDoor`), which starts the command behind the entrypoint of
// the sandbox, or with no entrypoint for `--bare`. When this process ends, the session ends
// the command.
//
// With `--offer <address>` this bin runs no command. It tells the session the address that
// a person uses to come into the sandbox, and stays until something ends it. The session
// shows the address while this process holds the door open.
//
// This bin is the contract of the door. The bytes on the socket are private (`EntryWire`).
// A deployment that gives a person a way into a sandbox calls this bin: for SSH, it runs
// this bin with `sshd -i` as the command, one time for each connection.
//
// It takes nothing from the session's own modules but the wire, so its bundle stays small.

open Fable.NodeExtras
open Node.Api
open Yession.Domain
open Yession.Session
open Yession.Host

let private say (message: string) : unit = (ProcessStreams.stderr ()).writeText (message + "\n") |> ignore

/// Dial the door, ask for the command, and connect the three streams of this process to it.
let private enter (request: EntryWire.EnterRequest) : unit =
    let door = connectPath request.DoorPath
    let input = ProcessStreams.stdin ()
    let output = ProcessStreams.stdout ()
    let errorOutput = ProcessStreams.stderr ()
    let mutable answerSoFar : byte [] = Array.zeroCreate 0
    let mutable admitted = false
    let mutable reader = EntryWire.FrameReader.empty
    let mutable over = false
    let mutable doorFault : string option = None
    let mutable inputHeld = false
    let mutable doorHeld = false

    /// End this process with `code`, after what it wrote has gone. The code is set and the
    /// streams are let go, and the process then ends by itself: an exit call does not wait
    /// for a write to a pipe.
    let finish (code: int) : unit =
        if not over then
            over <- true
            Processes.setExitCode code
            input.destroy ()
            door.destroy ()

    /// Write to the door. When the door is behind, the input of this process waits.
    let toDoor (bytes: byte []) : unit =
        if not over then
            let room = door.sink.writeByteArray bytes
            if not room && not inputHeld then
                inputHeld <- true
                input.pause ()
                door.sink.onceDrain (fun () ->
                    inputHeld <- false
                    if not over then input.resume ())

    /// Write to an output of this process. When the reader of it is behind, the door waits.
    let toOutput (stream: Writable) (bytes: byte []) : unit =
        let room = stream.writeByteArray bytes
        if not room && not doorHeld then
            doorHeld <- true
            door.pause ()
            stream.onceDrain (fun () ->
                doorHeld <- false
                door.resume ())

    let take (frame: EntryWire.Frame) : unit =
        match frame with
        | EntryWire.Frame.Output bytes -> toOutput output bytes
        | EntryWire.Frame.ErrorOutput bytes -> toOutput errorOutput bytes
        | EntryWire.Frame.Exited code -> finish code
        | EntryWire.Frame.Input _
        | EntryWire.Frame.InputClosed ->
            say "yession-enter: the door sent a frame that only a caller sends. The session can run a different build."
            finish EntryWire.ExitCode.unreachable

    let frames (chunk: byte []) : unit =
        match EntryWire.FrameReader.feed reader chunk with
        | Ok (whole, next) ->
            reader <- next
            whole |> List.iter (fun frame -> if not over then take frame)
        | Error reason ->
            say (sprintf "yession-enter: %s" reason)
            finish EntryWire.ExitCode.unreachable

    /// The command runs: give it the input of this process.
    let connectInput () : unit =
        input.onError ignore
        input.onEnd (fun () -> toDoor (EntryWire.frame EntryWire.Frame.InputClosed))
        Readables.bytes input (fun chunk -> EntryWire.framesOf EntryWire.Frame.Input chunk |> List.iter toDoor)

    let answered (text: string) (rest: byte []) : unit =
        match EntryWire.readAnswer text with
        | Error reason ->
            say (sprintf "yession-enter: %s" reason)
            finish EntryWire.ExitCode.unreachable
        | Ok (EntryWire.Answer.Refused reason) ->
            say (sprintf "yession-enter: the session refused the entry. %s" reason)
            finish EntryWire.ExitCode.refused
        | Ok EntryWire.Answer.Admitted ->
            admitted <- true
            connectInput ()
            if rest.Length > 0 then frames rest

    let fromDoor (chunk: byte []) : unit =
        if over then ()
        elif admitted then frames chunk
        else
            match EntryWire.line answerSoFar chunk with
            | EntryWire.Line.Partial more -> answerSoFar <- more
            | EntryWire.Line.TooLong ->
                say "yession-enter: the answer of the door is too long. The socket is not the door of a session."
                finish EntryWire.ExitCode.unreachable
            | EntryWire.Line.Whole (text, rest) -> answered text rest

    // A reader of the output that went away ends this process, and the session then ends
    // the command.
    output.onError (fun _ -> finish EntryWire.ExitCode.unreachable)
    errorOutput.onError ignore
    door.onError (fun error -> doorFault <- Some (StreamError.describe error))
    onceConnectionClosed door (fun () ->
        if not over then
            match doorFault, admitted with
            | Some fault, false ->
                say (sprintf "yession-enter: cannot reach the door at '%s': %s" request.DoorPath fault)
            | None, false ->
                say (sprintf "yession-enter: the door at '%s' closed with no answer." request.DoorPath)
            | Some fault, true -> say (sprintf "yession-enter: the door closed before the command ended: %s" fault)
            | None, true -> say "yession-enter: the door closed before the command ended."
            finish EntryWire.ExitCode.unreachable)
    Readables.bytes (door :> Readable) fromDoor
    door.writeText (EntryWire.hello request.Request)

/// The command line, with the program and its script taken off.
let private commandLine () : string list = ``process``.argv |> Seq.skip 2 |> Seq.toList

let private main () : unit =
    let options, sandbox, command = EntryWire.words (commandLine ())
    match Cli.outcome EntryWire.enterSpec Version.current (Array.ofList options) with
    | Cli.Outcome.Answered text ->
        printfn "%s" text
        Processes.exitWith 0
    | Cli.Outcome.Refused complaint ->
        say complaint
        Processes.exitWith 2
    | Cli.Outcome.Proceed parsed ->
        match EntryWire.enterRequest parsed sandbox command with
        | Error message ->
            say (Cli.complaint EntryWire.enterSpec message)
            Processes.exitWith 2
        | Ok request -> enter request

main ()
