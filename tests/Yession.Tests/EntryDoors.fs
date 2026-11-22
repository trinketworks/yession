module Yession.Tests.EntryDoors

// The door of a session: the socket through which a program on the host runs one command in
// one sandbox (`EntryDoor`), the bytes on that socket (`EntryWire`), the bin that dials it
// (`yession-enter`, `app/Enter.fs`), and the record of each entry (`SandboxEntered`,
// `SandboxLeft`, `EntryProjection`).
//
// `wireTests` start nothing. `doorTests` listen on a real socket, run the real bin as a child
// process, and run real commands on the host backend.

open System
open Fable.NodeExtras
open Fable.Pyxpecto
open Yession.Domain
open Yession.Codecs
open Yession.Domain.Sandboxes
open Yession.App.Codecs
open Yession.Session
open Yession.Host
open Yession.Tests.Support

// --- the wire ---------------------------------------------------------------------------------

/// A frame as a value that two runtimes compare in the same way: its name, its bytes and its
/// code.
let private shown (frame: EntryWire.Frame) : string * byte list * int =
    match frame with
    | EntryWire.Frame.Input bytes -> "input", List.ofArray bytes, 0
    | EntryWire.Frame.InputClosed -> "input-closed", [], 0
    | EntryWire.Frame.Output bytes -> "output", List.ofArray bytes, 0
    | EntryWire.Frame.ErrorOutput bytes -> "error-output", List.ofArray bytes, 0
    | EntryWire.Frame.Exited code -> "exited", [], code

/// Read chunks as frames, from an empty reader. The answer is the frames, and the count of
/// bytes that the reader holds at the end.
let private readAll (chunks: byte [] list) : Result<(string * byte list * int) list * int, string> =
    chunks
    |> List.fold
        (fun state chunk ->
            state
            |> Result.bind (fun (frames, reader) ->
                EntryWire.FrameReader.feed reader chunk
                |> Result.map (fun (more, next) -> frames @ List.map shown more, next)))
        (Ok ([], EntryWire.FrameReader.empty))
    |> Result.map (fun (frames, reader) -> frames, EntryWire.FrameReader.held reader)

let private someFrames : EntryWire.Frame list =
    [ EntryWire.Frame.Input bytesThatTextChanges
      EntryWire.Frame.InputClosed
      EntryWire.Frame.Output [| 0uy; 255uy; 10uy |]
      EntryWire.Frame.ErrorOutput (EntryWire.bytesOf "no such file\n")
      EntryWire.Frame.Exited 7 ]

let private aHello : EntryWire.Hello =
    { ToSandbox = "octo/hello:dev"; AsSubject = "ada"; Label = "ssh"; Argv = [ "/run/yession-ssh/serve"; "-e" ] }

let private parsedOptions (options: string list) : Cli.Parsed =
    match Cli.outcome EntryWire.enterSpec "test" (Array.ofList options) with
    | Cli.Outcome.Proceed parsed -> parsed
    | Cli.Outcome.Answered text -> failwithf "the command line was answered: %s" text
    | Cli.Outcome.Refused complaint -> failwithf "the command line was refused: %s" complaint

let private sessionId : SessionId = SessionId.create "S1" |> expect

let private entry (name: string) : EntryId = EntryId.create name |> expect

let private dev : SandboxRef = SandboxRef.parse "octo/hello:dev" |> expect

let private entered (name: string) (sandbox: SandboxRef) : SessionEvent =
    SessionEvent.SandboxEntered
        { SandboxEntered.Entry = entry name
          SandboxEntered.EnteredSandbox = sandbox
          SandboxEntered.EnteredAs = "ada"
          SandboxEntered.ViaLabel = "ssh"
          SandboxEntered.EnteredCommand = [ "/run/yession-ssh/serve" ] }

let private left (name: string) : SessionEvent =
    SessionEvent.SandboxLeft
        { SandboxLeft.LeftEntry = entry name
          SandboxLeft.ExitCode = Some 0
          SandboxLeft.EndedBecause = "the command ended" }

let private folded (events: SessionEvent list) : EntryProjection =
    events |> List.fold (fun proj event -> EntryProjection.applyEvent proj (DateTimeOffset (2026, 11, 22, 10, 30, 0, TimeSpan.Zero)) event) EntryProjection.empty

let private envelopeOf (event: SessionEvent) : EventEnvelope<SessionEvent> =
    { EventId = EventId.fresh ()
      SessionId = sessionId
      Offset = EventOffset.zero
      Actor = ActorRef.Session
      Timestamp = DateTimeOffset (2026, 11, 22, 10, 30, 0, TimeSpan.FromHours 10.0)
      Event = event }

let wireTests =
    testList "The entry wire" [

        testCase "each frame reads back as it was written" <| fun () ->
            let written = someFrames |> List.map EntryWire.frame
            Expect.equal (readAll written) (Ok (List.map shown someFrames, 0)) "the frames, and nothing held"

        testCase "frames that chunks cut at each byte read back whole" <| fun () ->
            let bytes = someFrames |> List.map EntryWire.frame |> Array.concat
            let chunks = bytes |> Array.chunkBySize 1 |> List.ofArray
            Expect.equal (readAll chunks) (Ok (List.map shown someFrames, 0)) "the frames, and nothing held"

        testCase "the start of a frame waits in the reader" <| fun () ->
            let bytes = EntryWire.frame (EntryWire.Frame.Output [| 1uy; 2uy; 3uy |])
            Expect.equal (readAll [ bytes.[.. 5] ]) (Ok ([], 6)) "no frame yet, and the six bytes held"

        testCase "a frame that says it is longer than the limit is refused" <| fun () ->
            let length = EntryWire.maxPayload + 1
            let header : byte [] =
                [| 2uy; byte ((length >>> 24) &&& 0xFF); byte ((length >>> 16) &&& 0xFF); byte ((length >>> 8) &&& 0xFF); byte (length &&& 0xFF) |]
            Expect.isError (readAll [ header ]) "a length past the limit"

        testCase "a frame of a type that the wire does not have is refused" <| fun () ->
            Expect.isError (readAll [ [| 9uy; 0uy; 0uy; 0uy; 0uy |] ]) "type 9"

        testCase "a chunk longer than one piece goes in more than one frame" <| fun () ->
            let chunk : byte [] = Array.zeroCreate (EntryWire.pieceSize + 1)
            let lengths = EntryWire.pieces chunk |> List.map (fun piece -> piece.Length)
            Expect.equal lengths [ EntryWire.pieceSize; 1 ] "one full piece, and the rest"

        testCase "the first line reads back as it was written" <| fun () ->
            Expect.equal (EntryWire.readHello (EntryWire.hello aHello)) (Ok aHello) "a round trip"

        testCase "a first line of a later version is refused, and the refusal names the two versions" <| fun () ->
            match EntryWire.readHello """{"v":2,"door":"x"}""" with
            | Ok read -> failwithf "version 2 was read: %A" read
            | Error reason -> Expect.isTrue (reason.Contains "version 2" && reason.Contains "version 1") reason

        testCase "the answer that admits reads back" <| fun () ->
            Expect.equal (EntryWire.readAnswer (EntryWire.answer EntryWire.Answer.Admitted)) (Ok EntryWire.Answer.Admitted) "a round trip"

        testCase "the answer that refuses reads back with its reason" <| fun () ->
            let refused = EntryWire.Answer.Refused "The sandbox 'dev' does not run now."
            Expect.equal (EntryWire.readAnswer (EntryWire.answer refused)) (Ok refused) "a round trip"

        testCase "a line that two chunks cut is one line, and the bytes after it stay" <| fun () ->
            let all = EntryWire.bytesOf "{\"ok\":true}\nrest"
            let first =
                match EntryWire.line (Array.zeroCreate 0) all.[.. 3] with
                | EntryWire.Line.Partial soFar -> soFar
                | other -> failwithf "four bytes with no line feed were %A" other
            match EntryWire.line first all.[4 ..] with
            | EntryWire.Line.Whole (text, rest) ->
                Expect.equal (text, List.ofArray rest) ("{\"ok\":true}", List.ofArray (EntryWire.bytesOf "rest")) "the line, and what came after it"
            | other -> failwithf "the line did not end: %A" other

        testCase "bytes past the limit with no line feed are not a line" <| fun () ->
            let chunk : byte [] = Array.create (EntryWire.maxLine + 1) 120uy
            match EntryWire.line (Array.zeroCreate 0) chunk with
            | EntryWire.Line.TooLong -> ()
            | other -> failwithf "a line with no end was %A" other

        testCase "the last word before the two dashes is the sandbox" <| fun () ->
            Expect.equal
                (EntryWire.words [ "--door"; "/run/d.sock"; "--as"; "ada"; "--via"; "ssh"; "dev"; "--"; "cat"; "-n" ])
                ([ "--door"; "/run/d.sock"; "--as"; "ada"; "--via"; "ssh" ], Some "dev", [ "cat"; "-n" ])
                "the options, the sandbox and the command"

        testCase "the value of an option is not the sandbox" <| fun () ->
            Expect.equal
                (EntryWire.words [ "--as"; "ada"; "--via"; "ssh"; "--door"; "/run/d.sock"; "--"; "cat" ])
                ([ "--as"; "ada"; "--via"; "ssh"; "--door"; "/run/d.sock" ], None, [ "cat" ])
                "no sandbox"

        testCase "a whole command line gives the request" <| fun () ->
            let parsed = parsedOptions [ "--door"; "/run/d.sock"; "--as"; "ada"; "--via"; "ssh" ]
            Expect.equal
                (EntryWire.enterRequest parsed (Some "octo/hello:dev") [ "/run/yession-ssh/serve"; "-e" ])
                (Ok { EntryWire.EnterRequest.DoorPath = "/run/d.sock"; EntryWire.EnterRequest.Request = aHello })
                "the door and the first line"

        testCase "a command line with no command is refused" <| fun () ->
            let parsed = parsedOptions [ "--door"; "/run/d.sock"; "--as"; "ada"; "--via"; "ssh" ]
            Expect.isError (EntryWire.enterRequest parsed (Some "dev") []) "nothing after the two dashes"

        testCase "a command line with no door is refused" <| fun () ->
            let parsed = parsedOptions [ "--as"; "ada"; "--via"; "ssh" ]
            Expect.isError (EntryWire.enterRequest parsed (Some "dev") [ "cat" ]) "no --door"

        testCase "the socket of a door is the session id in the directory" <| fun () ->
            Expect.equal (DoorPlace.socket 108 "/var/lib/yession" "/run/yession/doors/" sessionId) (Ok "/run/yession/doors/S1.sock") "the path"

        testCase "a relative directory holds no door" <| fun () ->
            Expect.isError (DoorPlace.socket 108 "/var/lib/yession" "doors" sessionId) "a relative path"

        testCase "a directory in the data directory holds no door" <| fun () ->
            Expect.isError (DoorPlace.socket 108 "/var/lib/yession" "/var/lib/yession/doors" sessionId) "in the data directory"

        testCase "a directory beside the data directory holds a door" <| fun () ->
            Expect.isOk (DoorPlace.socket 108 "/var/lib/yession" "/var/lib/yession-doors" sessionId) "the name starts the same, and the path is not in it"

        testCase "a socket path that the platform cannot bind holds no door" <| fun () ->
            // 104 bytes with the byte that ends it: 103 at most. This path is 103 + 1.
            let directory = "/" + String.replicate (104 - "/S1.sock".Length - 1) "d"
            Expect.isError (DoorPlace.socket 104 "/var/lib/yession" directory sessionId) "one byte too long"

        testCase "the longest socket path that the platform binds holds a door" <| fun () ->
            let directory = "/" + String.replicate (103 - "/S1.sock".Length - 1) "d"
            Expect.isOk (DoorPlace.socket 104 "/var/lib/yession" directory sessionId) "103 bytes"

        testCase "a first line with an empty command is refused" <| fun () ->
            Expect.isError (EntryDoor.request { aHello with Argv = [] }) "no command"

        testCase "a first line whose sandbox is not a name is refused" <| fun () ->
            Expect.isError (EntryDoor.request { aHello with ToSandbox = "a:b:c" }) "not a sandbox"

        testCase "a first line with a subject past the limit is refused" <| fun () ->
            Expect.isError (EntryDoor.request { aHello with AsSubject = String.replicate 129 "a" }) "129 characters"

        testCase "a first line starts its command behind the entrypoint of the sandbox" <| fun () ->
            Expect.equal
                (EntryDoor.request aHello)
                (Ok (dev, { Executable = "/run/yession-ssh/serve"; Arguments = [ "-e" ]; Env = Map.empty; WorkingDirectory = None; Via = Entrypoint }))
                "the sandbox and the command"

        testCase "a command that ends gives its code to the record and to the caller" <| fun () ->
            Expect.equal (EntryDoor.ending false (SandboxExited 7)) (Some 7, "the command ended", 7, None) "code 7"

        testCase "a caller that closed the door has no code in the record" <| fun () ->
            let recorded, reason, _, _ = EntryDoor.ending true (SandboxExited -1)
            Expect.equal (recorded, reason) (None, "the caller closed the door") "no code, and the reason"

        testCase "a command that a signal ended gives the caller the code for no code" <| fun () ->
            let _, _, code, _ = EntryDoor.ending false (SandboxSignalled "SIGTERM")
            Expect.equal code EntryWire.ExitCode.noCode "255"

        testCase "an entry is open in its sandbox from its start" <| fun () ->
            let proj = folded [ entered "e1" dev ]
            Expect.equal (EntryProjection.openIn dev proj |> List.map (fun held -> held.OpenSubject, held.OpenVia)) [ "ada", "ssh" ] "one entry, with who and how"

        testCase "an entry is not open after its end" <| fun () ->
            Expect.isFalse (EntryProjection.anyOpen (folded [ entered "e1" dev; left "e1" ])) "no entry"

        testCase "an entry in one sandbox is not open in another" <| fun () ->
            let proj = folded [ entered "e1" dev ]
            Expect.equal (EntryProjection.openIn SandboxRef.defaultRef proj) [] "nothing in the default sandbox"

        testCase "a start that the log holds two times is one entry" <| fun () ->
            Expect.equal (EntryProjection.count (folded [ entered "e1" dev; entered "e1" dev ])) 1 "one entry"

        testCase "one open entry keeps the session in use while a second has ended" <| fun () ->
            Expect.isTrue (EntryProjection.anyOpen (folded [ entered "e1" dev; entered "e2" dev; left "e1" ])) "e2 is open"

        testCase "a session that starts closes each entry that the log holds open" <| fun () ->
            let closing = EntryProjection.closingAll (folded [ entered "e1" dev; entered "e2" dev; left "e1" ])
            Expect.equal
                closing
                [ SessionEvent.SandboxLeft
                      { SandboxLeft.LeftEntry = entry "e2"
                        SandboxLeft.ExitCode = None
                        SandboxLeft.EndedBecause = SandboxLeft.sessionRestarted } ]
                "the end of e2, with no code"

        testCase "the start of an entry reads back from the log" <| fun () ->
            let original = envelopeOf (entered "e1" dev)
            let json = Codec.toString Events.sessionEventEnvelope original
            Expect.equal (Codec.fromString Events.sessionEventEnvelope json) (Ok original) "a round trip"

        testCase "the end of an entry reads back from the log" <| fun () ->
            let original = envelopeOf (left "e1")
            let json = Codec.toString Events.sessionEventEnvelope original
            Expect.equal (Codec.fromString Events.sessionEventEnvelope json) (Ok original) "a round trip"

        testCase "the end of an entry with no code reads back from the log" <| fun () ->
            let original =
                envelopeOf (
                    SessionEvent.SandboxLeft
                        { SandboxLeft.LeftEntry = entry "e1"
                          SandboxLeft.ExitCode = None
                          SandboxLeft.EndedBecause = "the caller closed the door" })
            let json = Codec.toString Events.sessionEventEnvelope original
            Expect.equal (Codec.fromString Events.sessionEventEnvelope json) (Ok original) "a round trip"
    ]

// --- the door, the bin and real commands -------------------------------------------------------

let private policy : SandboxPolicy =
    { ReadPaths = []
      WritePaths = []
      AllowedDomains = None
      Sockets = []
      Binds = []
      Volumes = []
      SeatbeltRules = []
      Devices = []
      Realisation = []
      Env = Sandboxes.hostBaseline (Sandboxes.ambientEnv ())
      WorkingDirectory = None
      Filesystem = Confined
      Derived = Map.empty }

let private node : string = Node.Api.``process``.execPath

/// The compiled `app/Enter.fs`: the program that the `yession-enter` bin runs.
let private enterProgram () : string = Node.Api.path.join (StandIn.directory (), "app", "Enter.js")

/// One `yession-enter` that runs: the process, what it wrote, and how it ended.
[<NoEquality; NoComparison>]
type private Caller =
    { Process : Node.ChildProcess.ChildProcess
      Out : ResizeArray<byte>
      Err : Text.StringBuilder
      /// `None` while the process runs. `Some None` for a process that a signal ended.
      mutable Code : int option option }

/// Start `yession-enter` with these arguments. The pieces go to its input, and then the
/// input ends.
let private call (arguments: string list) (pieces: byte [] list) : Caller =
    let child =
        ChildProcesses.spawn
            node
            (enterProgram () :: arguments)
            { Cwd = None
              Env = ChildEnv.Replacing Map.empty
              Streams = { Stdin = Stdio.Pipe; Stdout = Stdio.Pipe; Stderr = Stdio.Pipe }
              Detached = false }
    let caller : Caller = { Process = child; Out = ResizeArray<byte> (); Err = Text.StringBuilder (); Code = None }
    Readables.bytes (ChildProcessStreams.stdout child) (fun chunk -> caller.Out.AddRange chunk)
    Readables.text (ChildProcessStreams.stderr child) (fun text -> caller.Err.Append text |> ignore)
    ChildProcessStreams.onClose child (fun code -> caller.Code <- Some code)
    let input = ChildProcessStreams.stdin child
    input.onError ignore
    pieces |> List.iter (fun piece -> input.writeByteArray piece |> ignore)
    input.finish ()
    caller

/// The arguments that enter `sandbox` through `socket` as ada, over ssh.
let private through (socket: string) (sandbox: string) (command: string list) : string list =
    [ "--door"; socket; "--as"; "ada"; "--via"; "ssh"; sandbox; "--" ] @ command

/// Wait for a caller to end. The answer is its exit code.
let private ended (caller: Caller) : Async<int option> =
    async {
        do! waitUntilWithin 20_000 "yession-enter to end" (fun () -> Option.isSome caller.Code)
        return Option.flatten caller.Code
    }

/// A door on a real socket, over one sandbox of the host backend that has the name
/// `default`. The body gets the path of the socket and the events that the door recorded.
let private withDoor (limit: int) (body: string -> ResizeArray<SessionEvent> -> Async<unit>) : Async<unit> =
    async {
        let! sandbox =
            async {
                match! Sandboxes.HostSandbox.create HostSpec.none policy with
                | Error reason -> return failwithf "host sandbox failed: %s" reason
                | Ok sandbox -> return sandbox
            }
        let recorded = ResizeArray<SessionEvent> ()
        let mutable minted = 0
        let mint () : EntryId =
            minted <- minted + 1
            EntryId.create (sprintf "entry-%d" minted) |> expect
        let spawnIn (asked: SandboxRef) (exec: SandboxExec) (onChunk: OutputStream * byte [] -> unit) : Async<Result<SandboxByteHandle, string>> =
            if asked = SandboxRef.defaultRef then sandbox.SpawnBytes exec onChunk
            else async { return Error "There is no sandbox of that name in this session." }
        let config : EntryDoor.Config =
            { SocketPath = TestFiles.tempDir "yd-" + "/d.sock"
              Limit = limit
              SpawnIn = spawnIn
              Record = fun event -> async { recorded.Add event }
              MintEntry = mint }
        match! EntryDoor.start config with
        | Error reason -> failwithf "the door did not open: %s" reason
        | Ok door ->
            do! body door.ListensAt recorded
            door.Close ()
        do! sandbox.Dispose ()
    }

let doorTests =
    testList "The door of a session" [

        testCaseAsync "each byte goes through yession-enter and comes back as it went in" (withDoor 16 (fun socket _ ->
            async {
                let caller = call (through socket "default" [ "/bin/cat" ]) [ bytesThatTextChanges ]
                let! code = ended caller
                Expect.equal (code, List.ofSeq caller.Out) (Some 0, List.ofArray bytesThatTextChanges) "the bytes, and the code of cat"
            }))

        testCaseAsync "yession-enter ends with the exit code of the command" (withDoor 16 (fun socket _ ->
            async {
                let! code = ended (call (through socket "default" [ "/bin/sh"; "-c"; "exit 7" ]) [])
                Expect.equal code (Some 7) "the code that the command chose"
            }))

        testCaseAsync "the error output of the command is the error output of yession-enter" (withDoor 16 (fun socket _ ->
            async {
                let caller = call (through socket "default" [ "/bin/sh"; "-c"; "printf oops >&2" ]) []
                let! _ = ended caller
                Expect.equal (caller.Err.ToString (), List.ofSeq caller.Out) ("oops", []) "on the error output, and not on the output"
            }))

        testCaseAsync "the session records who entered, and how the entry ended" (withDoor 16 (fun socket recorded ->
            async {
                let! _ = ended (call (through socket "default" [ "/bin/sh"; "-c"; "exit 7" ]) [])
                do! waitUntil "the end of the entry to be recorded" (fun () -> recorded.Count = 2)
                Expect.equal
                    (List.ofSeq recorded)
                    [ SessionEvent.SandboxEntered
                          { SandboxEntered.Entry = entry "entry-1"
                            SandboxEntered.EnteredSandbox = SandboxRef.defaultRef
                            SandboxEntered.EnteredAs = "ada"
                            SandboxEntered.ViaLabel = "ssh"
                            SandboxEntered.EnteredCommand = [ "/bin/sh"; "-c"; "exit 7" ] }
                      SessionEvent.SandboxLeft
                          { SandboxLeft.LeftEntry = entry "entry-1"
                            SandboxLeft.ExitCode = Some 7
                            SandboxLeft.EndedBecause = "the command ended" } ]
                    "the start, and then the end with its code"
            }))

        testCaseAsync "a sandbox that the session does not have is refused, with the code of a refusal" (withDoor 16 (fun socket _ ->
            async {
                let! code = ended (call (through socket "other" [ "/bin/cat" ]) [])
                Expect.equal code (Some EntryWire.ExitCode.refused) "126"
            }))

        testCaseAsync "a refusal says why on the error output of yession-enter" (withDoor 16 (fun socket _ ->
            async {
                let caller = call (through socket "other" [ "/bin/cat" ]) []
                let! _ = ended caller
                Expect.stringContains (caller.Err.ToString ()) "There is no sandbox of that name in this session." "the reason of the session"
            }))

        testCaseAsync "a refusal records no entry" (withDoor 16 (fun socket recorded ->
            async {
                let! _ = ended (call (through socket "other" [ "/bin/cat" ]) [])
                Expect.equal recorded.Count 0 "no event"
            }))

        testCaseAsync "a path with no door ends yession-enter with the code of no door" (async {
            let socket = TestFiles.tempDir "yd-" + "/none.sock"
            let! code = ended (call (through socket "default" [ "/bin/cat" ]) [])
            Expect.equal code (Some EntryWire.ExitCode.unreachable) "125"
        })

        testCaseAsync "an entry past the limit is refused" (withDoor 1 (fun socket recorded ->
            async {
                // `sleep` reads no input, so the first entry stays open.
                let first = call (through socket "default" [ "/bin/sleep"; "30" ]) []
                do! waitUntil "the first entry to start" (fun () -> recorded.Count = 1)
                let! code = ended (call (through socket "default" [ "/bin/cat" ]) [])
                ChildProcesses.signal first.Process "SIGKILL" |> ignore
                let! _ = ended first
                Expect.equal code (Some EntryWire.ExitCode.refused) "the second entry, with one open and a limit of one"
            }))

        testCaseAsync "the session ends the command of a caller that goes away" (withDoor 16 (fun socket recorded ->
            async {
                let caller = call (through socket "default" [ "/bin/sleep"; "30" ]) []
                do! waitUntil "the entry to start" (fun () -> recorded.Count = 1)
                ChildProcesses.signal caller.Process "SIGKILL" |> ignore
                // The end is recorded when the command has ended, and not after 30 seconds.
                do! waitUntil "the end of the entry to be recorded" (fun () -> recorded.Count = 2)
                Expect.equal
                    recorded.[1]
                    (SessionEvent.SandboxLeft
                        { SandboxLeft.LeftEntry = entry "entry-1"
                          SandboxLeft.ExitCode = None
                          SandboxLeft.EndedBecause = "the caller closed the door" })
                    "the end, with no code, and the reason"
            }))

        testCaseAsync "a socket that a process left does not stop the next door" (async {
            let socket = TestFiles.tempDir "yd-" + "/d.sock"
            TestFiles.write socket "left by a process that ended"
            let config : EntryDoor.Config =
                { SocketPath = socket
                  Limit = 1
                  SpawnIn = fun _ _ _ -> async { return Error "no sandbox" }
                  Record = fun _ -> async { return () }
                  MintEntry = fun () -> entry "entry-1" }
            match! EntryDoor.start config with
            | Error reason -> failwithf "the door did not open: %s" reason
            | Ok door -> door.Close ()
        })
    ]
