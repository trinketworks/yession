module Yession.Tests.Supervisor

// The supervisor of a Seatbelt sandbox (`app/Supervisor.fs`).
//
// The supervisor ends processes, so what stands in front of that is what this suite pins:
// the two proofs, the pass that takes them, and a supervisor that starts no command and
// makes no pass without the first one. Each of those is pure logic over ports, so the cases
// need no Seatbelt and run on both platforms. The ending signal here is a function that
// records its targets. No case in the first list sends a signal.
//
// The second list starts real processes, with the capability for that. Its supervisor runs
// in this process, with the real port that starts a command and a proof that the case gives
// it. Three cases start an entry as a process of its own, with nothing that confines it.
// One starts the production entry, and never ends its input. Two start the rehearsal entry
// (`SupervisorRehearsalMain.fs`), whose ending signal is one that a process ignores, so
// those cases can end its input and see that it made no pass.
//
// What only a Mac can show is with the backend that uses the supervisor: that the kernel
// keeps a pass to one sandbox.

open Fable.NodeExtras
open Fable.Pyxpecto
open Hedgehog
open Yession.Host
open Yession.Host.Supervisor
open Yession.Tests.Support

let private expect = function Ok v -> v | Error e -> failwithf "%A" e

let private check (name: string) (body: unit -> Property<unit>) = testCase name <| fun () -> Property.check (body ())

// --- generators -------------------------------------------------------------------------------

/// Text that a line protocol must carry as it is: line ends, quotes, a backslash, a letter
/// that is two bytes, and the empty text.
let private genText : Gen<string> =
    Gen.list (Range.linear 0 12) (Gen.item [ "a"; " "; "\n"; "\r"; "\""; "\\"; "{"; "é"; "$HOME"; "\t" ])
    |> Gen.map (String.concat "")

let private genId : Gen<int> = Gen.int32 (Range.linear 0 50000)

let private genEnvironment : Gen<Map<string, string>> =
    Gen.list (Range.linear 0 4) (Gen.item [ "PATH", "/usr/bin:/bin"; "HOME", "/h"; "EMPTY", ""; "GIT_CONFIG_VALUE_0", "X-Yession-Loan: s\"1" ])
    |> Gen.map Map.ofList

let private genRequest : Gen<Wire.Request> =
    gen {
        let! kind = Gen.int32 (Range.linear 0 6)
        let! id = genId
        let! text = genText
        let! words = Gen.list (Range.linear 0 3) genText
        let! directory = Gen.item [ None; Some "/work/dir"; Some "/a b/c" ]
        let! environment = genEnvironment
        let! uid = Gen.int32 (Range.linear 0 100000)
        return
            match kind with
            | 0 -> Wire.Hello (id, uid)
            | 1 -> Wire.Run (id, "/bin/sh", words, directory, environment)
            | 2 -> Wire.Input (id, text)
            | 3 -> Wire.EndInput id
            | 4 -> Wire.RunBytes (id, "/bin/sh", words, directory, environment)
            // The wire carries base64 as text, and reads back each text.
            | 5 -> Wire.InputBytes (id, text)
            | _ -> Wire.Cancel id
    }

let private genEvent : Gen<Wire.Event> =
    gen {
        let! kind = Gen.int32 (Range.linear 0 8)
        let! id = genId
        let! text = genText
        let! stream = Gen.item [ Wire.Out; Wire.Err ]
        let! code = Gen.item [ None; Some 0; Some 1; Some 137 ]
        let! pids = Gen.list (Range.linear 0 4) (Gen.int32 (Range.linear 2 99999))
        return
            match kind with
            | 0 -> Wire.Ready
            | 1 -> Wire.Refused text
            | 2 -> Wire.Output (id, stream, text)
            | 3 -> Wire.Ended (id, code)
            | 4 -> Wire.NotStarted (id, text)
            | 5 -> Wire.Stopped pids
            | 6 -> Wire.OutputBytes (id, stream, text)
            | _ -> Wire.NotStopped text
    }

/// The lines that `Wire.lines` gives when `text` arrives in the pieces between `places`.
let private readInPieces (text: string) (places: int list) : string list =
    let read = ResizeArray<string> ()
    let feed = Wire.lines (fun line -> read.Add line) ignore
    for from, upTo in List.pairwise places do
        feed (text.Substring (from, upTo - from))
    List.ofSeq read

// --- the proofs, for the cases that need one ----------------------------------------------------

/// The proof at start, from values that give one: the session is process 700 and user 501,
/// this process finds the same, the kernel refused the signal, and it refused a second profile.
let private provenAtStart () : ProofAtStart =
    Confinement.atStart 700 501 700 501 Answer.Refused Nesting.Refused |> expect

let private provenBeforePass () : ProofBeforePass = Confinement.beforePass (provenAtStart ()) Nesting.Refused |> expect

/// What `sandbox-exec` prints when the kernel refuses a second profile.
let private refusalOfTheKernel = "sandbox-exec: sandbox_apply: Operation not permitted\n"

// --- a stop over ports that a case scripts -----------------------------------------------------

/// What a scripted stop did.
type private StopRecord =
    { /// Each pid that was given the ending signal, in order.
      Sent : ResizeArray<int>
      /// How many times something asked for the second proof.
      mutable Asked : int
      /// How many passes started.
      mutable Passes : int }

/// Ports for a stop. `taking` says which pids take the signal in each pass, by the number of
/// the pass from 1, and each of them is alive. `nesting` is the answer to each ask for the
/// second proof, by the number of the ask from 1. `tick` is how far the clock moves at each
/// look.
let private stopPorts (taking: int -> Set<int>) (nesting: int -> Nesting) (tick: float) : StopPorts * StopRecord =
    let record = { Sent = ResizeArray<int> (); Asked = 0; Passes = 0 }
    let mutable now = 0.0
    { Self = 4000
      End =
        fun target ->
            // A pass starts at pid 2.
            if Target.pid target = 2 then record.Passes <- record.Passes + 1
            record.Sent.Add (Target.pid target)
            if Set.contains (Target.pid target) (taking record.Passes) then Answer.Taken else Answer.NoProcess
      Alive = fun _ -> true
      Nested =
        fun () ->
            async {
                record.Asked <- record.Asked + 1
                return nesting record.Asked
            }
      Pause = fun () -> async.Return ()
      Now =
        fun () ->
            now <- now + tick
            now },
    record

/// Each ask for the second proof gets the kernel's refusal.
let private confined (_: int) : Nesting = Nesting.Refused

// --- a supervisor over ports that a case scripts ------------------------------------------------

/// What a scripted supervisor did.
type private Record =
    { Said : ResizeArray<Wire.Event>
      Left : ResizeArray<int>
      Started : ResizeArray<string * string list * string option * Map<string, string>>
      /// What the streams of each started command carry, in the order of `Started`.
      Carried : ResizeArray<Carries>
      /// What each started command was told, by its place in `Started`.
      Told : ResizeArray<int * string>
      Stop : StopRecord
      /// The words for a person that the supervisor wrote.
      Warned : ResizeArray<string>
      /// The handlers of each started command, so a case can make it print and end.
      Handlers : ResizeArray<(Wire.Stream * string -> unit) * (CommandEnd -> unit)>
      /// What a case calls to say that the leader of a started command ended.
      LeaderEnded : ResizeArray<unit -> unit> }

/// Ports for a supervisor. This process finds `parent` as the process that started it, and
/// the kernel answers `answer` to a signal for that process. `nesting` is the answer to each
/// ask for the second proof, and the first ask is the one at start. Nothing here starts a
/// process or sends a signal. A command whose executable is `/throws` does not start: its
/// port throws, as Node does for a directory that is a file.
let private supervisorPorts (parent: int) (answer: Answer) (nesting: int -> Nesting) : Ports * Record =
    let stop, stopRecord = stopPorts (fun _ -> Set.empty) nesting 1.0
    let record =
        { Said = ResizeArray<Wire.Event> ()
          Left = ResizeArray<int> ()
          Started = ResizeArray<string * string list * string option * Map<string, string>> ()
          Carried = ResizeArray<Carries> ()
          Told = ResizeArray<int * string> ()
          Stop = stopRecord
          Warned = ResizeArray<string> ()
          Handlers = ResizeArray<(Wire.Stream * string -> unit) * (CommandEnd -> unit)> ()
          LeaderEnded = ResizeArray<unit -> unit> () }
    { Parent = fun () -> parent
      Uid = 501
      MaySignal = fun _ -> answer
      Start =
        fun carries executable arguments directory environment onOutput onLeaderEnded onEnd ->
            if executable = "/throws" then failwith "spawn ENOTDIR"
            let place = record.Started.Count
            record.Started.Add ((executable, arguments, directory, environment))
            record.Carried.Add carries
            record.Handlers.Add ((onOutput, onEnd))
            record.LeaderEnded.Add onLeaderEnded
            { Write = fun text -> record.Told.Add ((place, "input " + text))
              EndInput = fun () -> record.Told.Add ((place, "end of input"))
              Cancel = fun () -> record.Told.Add ((place, "cancel")) }
      Say = fun event -> record.Said.Add event
      Warn = fun words -> record.Warned.Add words
      Leave = fun code -> record.Left.Add code
      Stop = stop },
    record

/// A supervisor that has its proof: the session is process 700, the kernel refused the
/// signal, and the supervisor took the first line.
let private readySupervisor () : Supervisor * Record =
    let ports, record = supervisorPorts 700 Answer.Refused confined
    let supervisor = create ports
    supervisor.Take (Wire.Hello (700, 501))
    supervisor, record

let private aCommand (id: int) : Wire.Request =
    Wire.Run (id, "/bin/tool", [ "one"; "two words" ], Some "/work", Map.ofList [ "ONLY", "this" ])

let tests =
    testList "the Seatbelt supervisor" [

        // --- the wire -----------------------------------------------------------------------

        check "a request reads back as the request it was" <| fun () ->
            property {
                let! request = genRequest
                Expect.equal (Wire.parseRequest (Wire.request request)) (Some request) "the supervisor reads what the session wrote"
            }

        check "an event reads back as the event it was" <| fun () ->
            property {
                let! event = genEvent
                Expect.equal (Wire.parseEvent (Wire.event event)) (Some event) "the session reads what the supervisor wrote"
            }

        check "a request is one line, whatever text it carries" <| fun () ->
            property {
                let! request = genRequest
                Expect.isFalse ((Wire.request request).Contains "\n") "a line end in the text is not a line end of the protocol"
            }

        check "an event is one line, whatever text it carries" <| fun () ->
            property {
                let! event = genEvent
                Expect.isFalse ((Wire.event event).Contains "\n") "a line end in the text is not a line end of the protocol"
            }

        check "lines come out whole, wherever the text was cut" <| fun () ->
            property {
                let! requests = Gen.list (Range.linear 1 5) genRequest
                let! cuts = Gen.list (Range.linear 0 6) (Gen.int32 (Range.linear 0 400))
                let text = requests |> List.map (fun request -> Wire.request request + "\n") |> String.concat ""
                let places = (0 :: cuts @ [ text.Length ]) |> List.map (min text.Length) |> List.distinct |> List.sort
                Expect.equal (readInPieces text places |> List.map Wire.parseRequest) (requests |> List.map Some) "each request arrived once, as one line"
            }

        testCase "a line that is longer than the limit is dropped, and the next line is read" <| fun () ->
            let read = ResizeArray<string> ()
            let mutable dropped = 0
            let feed = Wire.lines (fun line -> read.Add line) (fun () -> dropped <- dropped + 1)
            let piece = String.replicate (1024 * 1024) "x"
            for _ in 0 .. Wire.lineLimit / piece.Length do
                feed piece
            feed ("\n" + Wire.request (Wire.Cancel 3) + "\n")
            Expect.equal (List.ofSeq read |> List.map Wire.parseRequest, dropped) ([ Some (Wire.Cancel 3) ], 1) "the reader kept no part of the long line"

        check "an input goes to a command as requests of a bounded size, which join back to the input" <| fun () ->
            property {
                let! length = Gen.int32 (Range.linear 0 (3 * Wire.inputLimit))
                let text = String.replicate (length / 2) "aé"
                let pieces =
                    Wire.inputs 5 text
                    |> List.choose (fun request ->
                        match request with
                        | Wire.Input (5, piece) -> Some piece
                        | _ -> None)
                let bounded = pieces |> List.forall (fun piece -> piece.Length <= Wire.inputLimit)
                Expect.equal (String.concat "" pieces, bounded) (text, true) "each character once, in order"
            }

        testCase "no request for an input ends between the two halves of one character" <| fun () ->
            // The two halves of the last character stand at the place of the first cut.
            let text = String.replicate (Wire.inputLimit - 1) "a" + "\uD83D\uDE00" + "b"
            let halved =
                Wire.inputs 5 text
                |> List.exists (fun request ->
                    match request with
                    | Wire.Input (_, piece) -> piece.Length > 0 && System.Char.IsHighSurrogate piece.[piece.Length - 1]
                    | _ -> false)
            Expect.isFalse halved "a half alone is not a character, and a command would read two wrong ones"

        // --- what the ending signal can name --------------------------------------------------

        testCase "no pid below 2 is a target" <| fun () ->
            let named = [ -99999; -2; -1; 0; 1 ] |> List.choose Target.ofPid
            Expect.equal named [] "a group, each process and launchd cannot be named"

        // --- the proof at start ---------------------------------------------------------------

        testCase "the refusal of a signal for the session is the proof at start" <| fun () ->
            Expect.isOk (Confinement.atStart 700 501 700 501 Answer.Refused Nesting.Refused) "the kernel had no other reason to refuse"

        testCase "a signal that the kernel takes for the session is no proof" <| fun () ->
            Expect.isError (Confinement.atStart 700 501 700 501 Answer.Taken Nesting.Refused) "nothing confines a process that signals its parent"

        testCase "a session that names a different process is no proof" <| fun () ->
            Expect.isError (Confinement.atStart 800 501 700 501 Answer.Refused Nesting.Refused) "the process that refused is not the one that spoke"

        testCase "a session of a different user is no proof" <| fun () ->
            Expect.isError (Confinement.atStart 700 0 700 501 Answer.Refused Nesting.Refused) "the kernel refuses a signal between two users"

        testCase "a supervisor that launchd adopted has no proof" <| fun () ->
            Expect.isError (Confinement.atStart 1 501 1 501 Answer.Refused Nesting.Refused) "the kernel refuses a signal for launchd from each process"

        testCase "a session that has gone is no proof" <| fun () ->
            Expect.isError (Confinement.atStart 700 501 700 501 Answer.NoProcess Nesting.Refused) "a pid that no process has refuses nothing"

        testCase "a second profile that applied at start is no proof" <| fun () ->
            Expect.isError (Confinement.atStart 700 501 700 501 Answer.Refused Nesting.Applied) "no profile confines a process that enters one"

        testCase "a question that the system could not start is no proof at start" <| fun () ->
            Expect.isError
                (Confinement.atStart 700 501 700 501 Answer.Refused (Nesting.NotMade "spawn EAGAIN"))
                "nothing has yet shown a profile"

        testCase "a question that got no answer is no proof at start" <| fun () ->
            Expect.isError
                (Confinement.atStart 700 501 700 501 Answer.Refused (Nesting.Unanswered "the probe is not there"))
                "nothing has shown a profile"

        // --- the proof before a pass -----------------------------------------------------------

        testCase "a second profile that the kernel refused shows a profile" <| fun () ->
            Expect.equal (Nesting.read (Some 71) (Some refusalOfTheKernel)) Nesting.Refused "the sentence is the kernel's"

        testCase "a second profile that applied shows no profile" <| fun () ->
            Expect.equal (Nesting.read (Some 0) (Some "")) Nesting.Applied "a process outside each sandbox enters one"

        testCase "the code of a refusal with different words is no answer" <| fun () ->
            let said = "sandbox-exec: execvp() of '/usr/bin/true' failed: No such file or directory\n"
            Expect.isError
                (Confinement.beforePass (provenAtStart ()) (Nesting.read (Some 71) (Some said)))
                "sandbox-exec ends with the same code when it does not find its program"

        testCase "a probe that did not end is no answer" <| fun () ->
            Expect.isError (Confinement.beforePass (provenAtStart ()) (Nesting.read None None)) "a probe that did not run to its end says nothing of this process"

        testCase "a second profile that applied is no proof" <| fun () ->
            Expect.isError (Confinement.beforePass (provenAtStart ()) Nesting.Applied) "no profile confines this process"

        // A fork bomb fills the limit of processes for the user. The question for the second
        // proof starts a process, so it cannot run there, and that sandbox most needs its stop.
        testCase "a question that the system could not start lets the proof from the start stand" <| fun () ->
            Expect.isOk
                (Confinement.beforePass (provenAtStart ()) (Nesting.NotMade "spawn EAGAIN"))
                "a profile does not leave a process"

        testCase "a probe that the system could not make is a question that did not start" <| fun () ->
            Expect.equal (Nesting.notStarted (Some "EAGAIN") "spawn EAGAIN") (Nesting.NotMade "spawn EAGAIN") "the limit of processes is full"

        testCase "a probe that is not there is no answer" <| fun () ->
            Expect.isError
                (Confinement.beforePass (provenAtStart ()) (Nesting.notStarted (Some "ENOENT") "spawn ENOENT"))
                "a host with no sandbox-exec shows no profile"

        // --- a pass ----------------------------------------------------------------------------

        testCase "a pass names each pid from 2 to the last, and not its own" <| fun () ->
            let sent = ResizeArray<int> ()
            let send (target: Target) =
                sent.Add (Target.pid target)
                Answer.NoProcess
            pass (provenAtStart ()) (provenBeforePass ()) 4000 send (fun _ -> true) |> ignore
            Expect.equal (List.ofSeq sent) ([ 2 .. lastPid ] |> List.filter (fun pid -> pid <> 4000)) "once each, in order"

        testCase "a pass answers the pids that took the signal" <| fun () ->
            let send (target: Target) =
                match Target.pid target with
                | 5000
                | 60000 -> Answer.Taken
                | 7000 -> Answer.Refused
                | _ -> Answer.NoProcess
            Expect.equal
                (pass (provenAtStart ()) (provenBeforePass ()) 4000 send (fun _ -> true))
                [ 5000; 60000 ]
                "a refusal is a process outside the sandbox"

        testCase "a pass does not count a zombie" <| fun () ->
            let send (target: Target) = if Target.pid target = 5000 then Answer.Taken else Answer.NoProcess
            Expect.equal
                (pass (provenAtStart ()) (provenBeforePass ()) 4000 send (fun _ -> false))
                []
                "the kernel takes a signal for a zombie, and no process is there to end"

        // --- a stop ----------------------------------------------------------------------------

        testCaseAsync "two passes in a row that find nothing end the stop" (async {
            let taking (pass: int) = if pass = 1 then Set.ofList [ 5000; 6000 ] else Set.empty
            let ports, record = stopPorts taking confined 1.0
            let! ended = stop ports (provenAtStart ())
            Expect.equal (ended, record.Passes) (Stop.Ended, 3) "the pass that found two, and two that found none"
        })

        testCaseAsync "a pass that finds a process starts the count again" (async {
            let taking (pass: int) = if pass = 2 then Set.ofList [ 5000 ] else Set.empty
            let ports, record = stopPorts taking confined 1.0
            let! ended = stop ports (provenAtStart ())
            Expect.equal (ended, record.Passes) (Stop.Ended, 4) "one empty pass before the process showed is not the end"
        })

        testCaseAsync "a stop that runs out of time names the pids that still take the signal" (async {
            // The clock passes the limit of the stop between the first pass and the second question.
            let ports, _ = stopPorts (fun _ -> Set.ofList [ 5000 ]) confined (stopBudgetMs / 2.0 + 1.0)
            let! ended = stop ports (provenAtStart ())
            Expect.equal ended (Stop.Left [ 5000 ]) "the stop gave up, and said what was left"
        })

        testCaseAsync "a stop whose time ran out starts no question and no pass" (async {
            let ports, record = stopPorts (fun _ -> Set.ofList [ 5000 ]) confined (stopBudgetMs + 1.0)
            let! _ = stop ports (provenAtStart ())
            Expect.equal (record.Asked, record.Passes) (0, 0) "the stop looks at the clock before each question"
        })

        testCaseAsync "a stop makes no pass when a second profile applied" (async {
            let ports, record = stopPorts (fun _ -> Set.empty) (fun _ -> Nesting.Applied) 1.0
            let! _ = stop ports (provenAtStart ())
            Expect.equal record.Sent.Count 0 "no pid was given the ending signal"
        })

        testCaseAsync "a stop makes no pass when the question for the second proof got no answer" (async {
            let ports, record = stopPorts (fun _ -> Set.empty) (fun _ -> Nesting.Unanswered "the probe said something else") 1.0
            let! _ = stop ports (provenAtStart ())
            Expect.equal record.Sent.Count 0 "no pid was given the ending signal"
        })

        testCaseAsync "a stop makes its passes when the system can make no process to ask" (async {
            let taking (pass: int) = if pass = 1 then Set.ofList [ 5000 ] else Set.empty
            let ports, record = stopPorts taking (fun _ -> Nesting.NotMade "spawn EAGAIN") 1.0
            let! ended = stop ports (provenAtStart ())
            Expect.equal (ended, record.Passes) (Stop.Ended, 3) "the proof from the start stood for each pass"
        })

        testCaseAsync "a stop asks for the second proof again before each pass" (async {
            // The first pass has its proof. The second ask finds no profile.
            let ports, record = stopPorts (fun _ -> Set.empty) (fun ask -> if ask = 1 then Nesting.Refused else Nesting.Applied) 1.0
            let! ended = stop ports (provenAtStart ())
            Expect.equal (record.Passes, (match ended with Stop.Unproven _ -> true | _ -> false)) (1, true) "one pass, and no second one"
        })

        // --- a supervisor ----------------------------------------------------------------------

        testCase "a supervisor is ready when the kernel refuses its signal for the session" <| fun () ->
            let _, record = readySupervisor ()
            Expect.equal (List.ofSeq record.Said) [ Wire.Ready ] "the first line gave the proof"

        testCase "a supervisor that can signal the session refuses" <| fun () ->
            let ports, record = supervisorPorts 700 Answer.Taken confined
            (create ports).Take (Wire.Hello (700, 501))
            let refused = record.Said |> Seq.exists (fun event -> match event with Wire.Refused _ -> true | _ -> false)
            Expect.isTrue refused "nothing confines it, and it says so"

        testCase "a supervisor that enters a second profile refuses" <| fun () ->
            let ports, record = supervisorPorts 700 Answer.Refused (fun _ -> Nesting.Applied)
            (create ports).Take (Wire.Hello (700, 501))
            let refused = record.Said |> Seq.exists (fun event -> match event with Wire.Refused _ -> true | _ -> false)
            Expect.isTrue refused "ready needs both proofs"

        testCase "a supervisor that refused leaves" <| fun () ->
            let ports, record = supervisorPorts 700 Answer.Taken confined
            (create ports).Take (Wire.Hello (700, 501))
            Expect.equal (List.ofSeq record.Left) [ ExitCode.refused ] "it has nothing to supervise"

        testCase "a supervisor that has no first line starts no command" <| fun () ->
            let ports, record = supervisorPorts 700 Answer.Refused confined
            (create ports).Take (aCommand 1)
            Expect.equal record.Started.Count 0 "a command needs the proof first"

        testCase "a supervisor that refused starts no command" <| fun () ->
            let ports, record = supervisorPorts 700 Answer.Taken confined
            let supervisor = create ports
            supervisor.Take (Wire.Hello (700, 501))
            supervisor.Take (aCommand 1)
            Expect.equal record.Started.Count 0 "a command needs the proof first"

        testCase "a command that did not start for want of a proof is answered" <| fun () ->
            let ports, record = supervisorPorts 700 Answer.Refused confined
            (create ports).Take (aCommand 9)
            let answered = record.Said |> Seq.exists (fun event -> match event with Wire.NotStarted (9, _) -> true | _ -> false)
            Expect.isTrue answered "the session is told, under the id of the command"

        testCaseAsync "a supervisor that refused makes no pass when its input ends" (async {
            let ports, record = supervisorPorts 700 Answer.Taken confined
            let supervisor = create ports
            supervisor.Take (Wire.Hello (700, 501))
            supervisor.InputEnded ()
            let! _ = settledWithin 5000 (fun () -> record.Left.Count >= 2)
            Expect.equal record.Stop.Sent.Count 0 "no pid was given the ending signal"
        })

        testCaseAsync "a supervisor that has no first line makes no pass when its input ends" (async {
            let ports, record = supervisorPorts 700 Answer.Refused confined
            (create ports).InputEnded ()
            let! _ = settledWithin 5000 (fun () -> record.Left.Count >= 1)
            Expect.equal record.Stop.Sent.Count 0 "no pid was given the ending signal"
        })

        testCase "a command starts with what the session gave it" <| fun () ->
            let supervisor, record = readySupervisor ()
            supervisor.Take (aCommand 1)
            Expect.equal
                (List.ofSeq record.Started)
                [ "/bin/tool", [ "one"; "two words" ], Some "/work", Map.ofList [ "ONLY", "this" ] ]
                "its executable, its arguments, its directory and the whole of its environment"

        testCase "the output of a command goes to the session under the id of the command" <| fun () ->
            let supervisor, record = readySupervisor ()
            supervisor.Take (aCommand 7)
            let onOutput, _ = record.Handlers.[0]
            onOutput (Wire.Err, "warned")
            Expect.equal (List.ofSeq record.Said) [ Wire.Ready; Wire.Output (7, Wire.Err, "warned") ] "with the stream that it came from"

        testCase "a command of bytes starts as the same process, and its port carries bytes" <| fun () ->
            let supervisor, record = readySupervisor ()
            supervisor.Take (Wire.RunBytes (1, "/bin/tool", [ "one" ], Some "/work", Map.ofList [ "ONLY", "this" ]))
            supervisor.Take (aCommand 2)
            Expect.equal
                (List.ofSeq record.Started |> List.head, List.ofSeq record.Carried)
                (("/bin/tool", [ "one" ], Some "/work", Map.ofList [ "ONLY", "this" ]), [ Carries.Bytes; Carries.Text ])
                "what the session gave it, and bytes only for the command that asked"

        testCase "the output of a command of bytes goes to the session as bytes" <| fun () ->
            let supervisor, record = readySupervisor ()
            supervisor.Take (Wire.RunBytes (7, "/bin/tool", [], None, Map.empty))
            let onOutput, _ = record.Handlers.[0]
            onOutput (Wire.Out, "AAEC")
            Expect.equal (List.ofSeq record.Said) [ Wire.Ready; Wire.OutputBytes (7, Wire.Out, "AAEC") ] "base64, under the name for bytes"

        testCase "an input goes only to a command that carries its type" <| fun () ->
            let supervisor, record = readySupervisor ()
            supervisor.Take (Wire.RunBytes (1, "/bin/tool", [], None, Map.empty))
            supervisor.Take (aCommand 2)
            supervisor.Take (Wire.Input (1, "text for bytes"))
            supervisor.Take (Wire.InputBytes (2, "AAEC"))
            supervisor.Take (Wire.InputBytes (1, "AAEC"))
            supervisor.Take (Wire.Input (2, "typed"))
            Expect.equal (List.ofSeq record.Told) [ 0, "input AAEC"; 1, "input typed" ] "the two that named the right type"

        testCase "a command of bytes that ended carries text again under its id" <| fun () ->
            let supervisor, record = readySupervisor ()
            supervisor.Take (Wire.RunBytes (1, "/bin/tool", [], None, Map.empty))
            let _, onEnd = record.Handlers.[0]
            onEnd (CommandEnd.Ended (Some 0))
            supervisor.Take (aCommand 1)
            supervisor.Take (Wire.Input (1, "typed"))
            Expect.equal (List.ofSeq record.Told) [ 1, "input typed" ] "the id is that of a command of text now"

        testCase "bytes for an input go as pieces that read back as the bytes" <| fun () ->
            for count in [ 0; 1; Wire.inputBytesLimit - 1; Wire.inputBytesLimit; Wire.inputBytesLimit + 1; 3 * Wire.inputBytesLimit + 5 ] do
                let bytes = Array.init count (fun at -> byte (at * 7))
                let pieces =
                    Wire.inputsOfBytes 4 bytes
                    |> List.map (fun request ->
                        match request with
                        | Wire.InputBytes (4, base64) -> base64
                        | other -> failwithf "not an input of bytes for command 4: %A" other)
                Expect.isTrue (pieces |> List.forall (fun piece -> piece.Length <= Wire.inputLimit)) "no piece is longer than an input of text"
                Expect.equal
                    (pieces |> List.collect (fun piece -> List.ofArray (System.Convert.FromBase64String piece)))
                    (List.ofArray bytes)
                    (sprintf "%d bytes, in order" count)

        testCase "the end of a command goes to the session with its code" <| fun () ->
            let supervisor, record = readySupervisor ()
            supervisor.Take (aCommand 7)
            let _, onEnd = record.Handlers.[0]
            onEnd (CommandEnd.Ended (Some 3))
            Expect.equal (List.ofSeq record.Said) [ Wire.Ready; Wire.Ended (7, Some 3) ] "the code that the command chose"

        testCase "a command that its port ends twice is answered once" <| fun () ->
            let supervisor, record = readySupervisor ()
            supervisor.Take (aCommand 7)
            let _, onEnd = record.Handlers.[0]
            // Node reports a start that fails late as an error, and then as a close.
            onEnd (CommandEnd.NotStarted "spawn EACCES")
            onEnd (CommandEnd.Ended (Some -2))
            Expect.equal (List.ofSeq record.Said) [ Wire.Ready; Wire.NotStarted (7, "spawn EACCES") ] "one end for one command"

        testCase "a start that throws is answered as a command that did not start" <| fun () ->
            let supervisor, record = readySupervisor ()
            supervisor.Take (Wire.Run (7, "/throws", [], None, Map.empty))
            Expect.equal (List.ofSeq record.Said) [ Wire.Ready; Wire.NotStarted (7, "spawn ENOTDIR") ] "in the words that Node threw"

        testCase "a supervisor whose port threw at a start still starts the next command" <| fun () ->
            let supervisor, record = readySupervisor ()
            supervisor.Take (Wire.Run (7, "/throws", [], None, Map.empty))
            supervisor.Take (aCommand 8)
            Expect.equal record.Started.Count 1 "the throw was the end of one command, and not of the supervisor"

        testCaseAsync "a fault that nothing caught makes the stop" (async {
            let supervisor, record = readySupervisor ()
            supervisor.Fault "something failed"
            let! _ = settledWithin 5000 (fun () -> record.Left.Count >= 1)
            Expect.equal (record.Stop.Passes > 0, List.ofSeq record.Left) (true, [ ExitCode.ended ]) "the processes of the sandbox do not stay without their supervisor"
        })

        testCaseAsync "a fault of a supervisor that has no proof makes no pass" (async {
            let ports, record = supervisorPorts 700 Answer.Taken confined
            let supervisor = create ports
            supervisor.Take (Wire.Hello (700, 501))
            supervisor.Fault "something failed"
            let! _ = settledWithin 5000 (fun () -> record.Left.Count >= 2)
            Expect.equal record.Stop.Sent.Count 0 "no pid was given the ending signal"
        })

        testCase "a command that did not start is answered with the reason" <| fun () ->
            let supervisor, record = readySupervisor ()
            supervisor.Take (aCommand 7)
            let _, onEnd = record.Handlers.[0]
            onEnd (CommandEnd.NotStarted "no such program")
            Expect.equal (List.ofSeq record.Said) [ Wire.Ready; Wire.NotStarted (7, "no such program") ] "in the words of the failure"

        testCase "input goes to the command that the session named" <| fun () ->
            let supervisor, record = readySupervisor ()
            supervisor.Take (aCommand 1)
            supervisor.Take (aCommand 2)
            supervisor.Take (Wire.Input (2, "typed"))
            Expect.equal (List.ofSeq record.Told) [ 1, "input typed" ] "the second command, and not the first"

        testCase "a cancel goes to the command that the session named" <| fun () ->
            let supervisor, record = readySupervisor ()
            supervisor.Take (aCommand 1)
            supervisor.Take (aCommand 2)
            supervisor.Take (Wire.Cancel 1)
            Expect.equal (List.ofSeq record.Told) [ 0, "cancel" ] "the first command, and not the second"

        // A command can end its leader while a child of the leader still holds an output of
        // the command. The command has no end then, and its process group is not its own any
        // more: the system can give the number of the group to a different process.
        testCase "a cancel for a command whose leader ended sends nothing" <| fun () ->
            let supervisor, record = readySupervisor ()
            supervisor.Take (aCommand 1)
            record.LeaderEnded.[0] ()
            supervisor.Take (Wire.Cancel 1)
            Expect.equal (List.ofSeq record.Told) [] "the stop ends what such a command left"

        testCase "a command with the id of a running command does not start" <| fun () ->
            let supervisor, record = readySupervisor ()
            supervisor.Take (aCommand 1)
            supervisor.Take (aCommand 1)
            Expect.equal record.Started.Count 1 "one id is one command"

        testCaseAsync "the end of the input makes the stop, and the supervisor says how it ended" (async {
            let supervisor, record = readySupervisor ()
            supervisor.InputEnded ()
            let! _ = settledWithin 5000 (fun () -> record.Left.Count >= 1)
            Expect.equal (List.ofSeq record.Said, List.ofSeq record.Left) ([ Wire.Ready; Wire.Stopped [] ], [ ExitCode.ended ]) "each process ended"
        })

        testCaseAsync "a supervisor whose stop failed leaves with the code of a stop that left processes" (async {
            let ports, record = supervisorPorts 700 Answer.Refused confined
            // The ask at start answers. Each ask after it fails, as a port that threw.
            let asked = ports.Stop.Nested
            let mutable asks = 0
            let failing () : Async<Nesting> =
                asks <- asks + 1
                if asks = 1 then asked () else async { return failwith "the port failed" }
            let supervisor = create { ports with Stop = { ports.Stop with Nested = failing } }
            supervisor.Take (Wire.Hello (700, 501))
            supervisor.InputEnded ()
            let! _ = settledWithin 5000 (fun () -> record.Left.Count >= 1)
            Expect.equal (List.ofSeq record.Left) [ ExitCode.left ] "it does not wait, and its code says that processes can still run"
        })

        testCaseAsync "a stop that had no second proof is said in words" (async {
            // The ask at start finds a profile. The ask before the first pass does not.
            let ports, record = supervisorPorts 700 Answer.Refused (fun ask -> if ask = 1 then Nesting.Refused else Nesting.Applied)
            let supervisor = create ports
            supervisor.Take (Wire.Hello (700, 501))
            supervisor.InputEnded ()
            let! _ = settledWithin 5000 (fun () -> record.Left.Count >= 1)
            let said = record.Said |> Seq.exists (fun event -> match event with Wire.NotStopped _ -> true | _ -> false)
            Expect.equal (said, List.ofSeq record.Left) (true, [ ExitCode.unproven ]) "the session is told why no pass ran"
        })

        testCase "a request after the input ended starts nothing" <| fun () ->
            let supervisor, record = readySupervisor ()
            supervisor.InputEnded ()
            supervisor.Take (aCommand 1)
            Expect.equal record.Started.Count 0 "a supervisor that stops starts no command"
    ]

// --- real processes -----------------------------------------------------------------------------

let private node : string = Node.Api.``process``.execPath

/// A supervisor in this process that starts real commands. The case gives it its proof:
/// its ports say that the kernel refuses a signal for process 700. The case scripts its
/// stop, which sends no signal. `Cancel` is the real one. It ends the process group of a
/// command that this supervisor started, and nothing else.
let private supervisorOfRealCommands () : Supervisor * ResizeArray<Wire.Event> =
    let said = ResizeArray<Wire.Event> ()
    let stop, _ = stopPorts (fun _ -> Set.empty) confined 1.0
    let supervisor =
        create
            { ports "SIGKILL" with
                Parent = fun () -> 700
                Uid = 501
                MaySignal = fun _ -> Answer.Refused
                Say = said.Add
                Warn = ignore
                Leave = ignore
                Stop = stop }
    supervisor.Take (Wire.Hello (700, 501))
    supervisor, said

let private endOf (id: int) (said: ResizeArray<Wire.Event>) : int option option =
    said |> Seq.tryPick (fun event -> match event with Wire.Ended (ended, code) when ended = id -> Some code | _ -> None)

let private printedBy (id: int) (stream: Wire.Stream) (said: ResizeArray<Wire.Event>) : string =
    said
    |> Seq.choose (fun event ->
        match event with
        | Wire.Output (from, on, text) when from = id && on = stream -> Some text
        | _ -> None)
    |> String.concat ""

/// Run one Node program as command 1 of a supervisor, and answer what the supervisor said
/// once the command ended.
let private ran (program: string) (environment: Map<string, string>) : Async<ResizeArray<Wire.Event>> =
    async {
        let supervisor, said = supervisorOfRealCommands ()
        supervisor.Take (Wire.Run (1, node, [ "-e"; program ], None, environment))
        let! ended = settledWithin 20000 (fun () -> Option.isSome (endOf 1 said))
        if not ended then failwithf "the command did not end, and the supervisor said: %A" (List.ofSeq said)
        return said
    }

/// An entry of the supervisor, started as a child of this process with nothing that
/// confines it. Answers the child, and what it has said so far.
let private entryProcess (program: string) : Node.ChildProcess.ChildProcess * ResizeArray<Wire.Event> =
    let entry =
        ChildProcesses.spawn
            node
            [ program ]
            { Cwd = None
              Env = ChildEnv.Replacing Map.empty
              Streams = { Stdin = Stdio.Pipe; Stdout = Stdio.Pipe; Stderr = Stdio.Inherit }
              Detached = false }
    let said = ResizeArray<Wire.Event> ()
    Readables.text (ChildProcessStreams.stdout entry) (Wire.lines (fun line -> Wire.parseEvent line |> Option.iter said.Add) ignore)
    (ChildProcessStreams.stdin entry).onError ignore
    entry, said

let private firstLine (parent: int) : string =
    Wire.request (Wire.Hello (parent, int (Node.Api.``process``.getuid ()))) + "\n"

/// The PRODUCTION entry, with a first line that names pid 2 as the session. This reaches one
/// check, and only that one: the process that started the entry is not the process that the
/// first line names.
///
/// The case ends the entry with a signal, and never closes its input. The end of the input
/// is what starts a stop, and this entry holds the signal that ends a process. Answers what
/// the entry said.
let private productionEntryWithAWrongFirstLine () : Async<Wire.Event list> =
    async {
        let entry, said = entryProcess (Node.Api.path.join (StandIn.directory (), "app", "SandboxSupervisor.js"))
        (ChildProcessStreams.stdin entry).writeText (firstLine 2) |> ignore
        let! _ = settledWithin 20000 (fun () -> said.Count > 0)
        // With a signal, and not by the end of its input.
        ChildProcesses.signal entry "SIGKILL" |> ignore
        return List.ofSeq said
    }

/// The REHEARSAL entry (`SupervisorRehearsalMain.fs`), with a TRUE first line: this process
/// started it, and says so. Nothing confines it, so the kernel lets it signal this process,
/// and that is what refuses it. Its ending signal is SIGURG, which a process ignores, so
/// this case can end its input.
///
/// A witness runs beside it and writes a line for each SIGURG. A pass, if the entry made
/// one at any time, would reach the witness: the two have one user, and no sandbox is
/// between them. Answers what the entry said, and what the witness recorded after the
/// entry ended.
let private rehearsalEntryWithATrueFirstLine () : Async<Wire.Event list * string> =
    async {
        let record = TestFiles.tempDir "yession-supervisor-" + "/witness"
        TestFiles.write record ""
        let witness =
            ChildProcesses.spawn
                node
                [ "-e"
                  "process.on('SIGURG', () => require('fs').appendFileSync(process.argv[1], 'signal\\n')); console.log('up'); setTimeout(() => {}, 600000)"
                  record ]
                { Cwd = None
                  Env = ChildEnv.Replacing Map.empty
                  Streams = { Stdin = Stdio.Ignore; Stdout = Stdio.Pipe; Stderr = Stdio.Ignore }
                  Detached = false }
        let mutable listening = false
        Readables.text (ChildProcessStreams.stdout witness) (fun _ -> listening <- true)
        let! _ = settledWithin 20000 (fun () -> listening)
        let entry, said = entryProcess (Node.Api.path.join (StandIn.directory (), "SupervisorRehearsalMain.js"))
        let mutable ended = false
        ChildProcessStreams.onClose entry (fun _ -> ended <- true)
        let input = ChildProcessStreams.stdin entry
        input.writeText (firstLine (int Node.Api.``process``.pid)) |> ignore
        let! _ = settledWithin 20000 (fun () -> said.Count > 0)
        // The entry leaves when it refuses. The end of the input is for an entry that did
        // not: it is what starts a stop, and a stop of this entry sends SIGURG.
        input.finish ()
        let! _ = settledWithin 20000 (fun () -> ended)
        // A signal that the entry sent before it ended needs a moment to reach the record.
        do! quietFor 500
        let recorded = TestFiles.read record
        ChildProcesses.signal witness "SIGKILL" |> ignore
        return List.ofSeq said, recorded
    }



let processTests =
    testList "the Seatbelt supervisor, with real processes" [

        testCaseAsync "a command gives back what it printed on each stream" (async {
            let! said = ran "console.log('to out'); console.error('to err')" Map.empty
            Expect.equal
                ((printedBy 1 Wire.Out said).Trim (), (printedBy 1 Wire.Err said).Trim ())
                ("to out", "to err")
                "each stream under its own name"
        })

        testCaseAsync "a command of bytes gives back each byte that it got" (async {
            let supervisor, said = supervisorOfRealCommands ()
            supervisor.Take (Wire.RunBytes (1, node, [ "-e"; "process.stdin.pipe(process.stdout)" ], None, Map.empty))
            Wire.inputsOfBytes 1 bytesThatTextChanges |> List.iter supervisor.Take
            supervisor.Take (Wire.EndInput 1)
            let! ended = settledWithin 20000 (fun () -> Option.isSome (endOf 1 said))
            if not ended then failwithf "the command did not end, and the supervisor said: %A" (List.ofSeq said)
            let back =
                said
                |> Seq.collect (fun event ->
                    match event with
                    | Wire.OutputBytes (1, Wire.Out, base64) -> List.ofArray (System.Convert.FromBase64String base64)
                    | _ -> [])
                |> List.ofSeq
            Expect.equal back (List.ofArray bytesThatTextChanges) "each byte value, and bytes that are not text"
        })

        testCaseAsync "a command gives back the code that it ended with" (async {
            let! said = ran "process.exit(7)" Map.empty
            Expect.equal (endOf 1 said) (Some (Some 7)) "the code that the command chose"
        })

        testCaseAsync "the end of a command follows the whole of its output" (async {
            let! said = ran "process.stdout.write('x'.repeat(300000))" Map.empty
            let last = said.[said.Count - 1]
            Expect.equal
                ((printedBy 1 Wire.Out said).Length, (match last with Wire.Ended (1, _) -> true | _ -> false))
                (300000, true)
                "each character, and then the end"
        })

        testCaseAsync "a command has the environment that the session gave, and nothing of the supervisor's" (async {
            let! said = ran "console.log(Object.keys(process.env).filter(name => !name.startsWith('__')).join(','))" (Map.ofList [ "ONLY", "this" ])
            Expect.equal ((printedBy 1 Wire.Out said).Trim ()) "ONLY" "one name, which is the one that was given"
        })

        testCaseAsync "a command that is not there is answered as not started" (async {
            let supervisor, said = supervisorOfRealCommands ()
            supervisor.Take (Wire.Run (1, "/no/such/program", [], None, Map.empty))
            let! answered =
                settledWithin 20000 (fun () ->
                    said |> Seq.exists (fun event -> match event with Wire.NotStarted (1, _) -> true | _ -> false))
            Expect.isTrue answered "the session is told, and does not wait for an end"
        })

        testCaseAsync "input reaches a command" (async {
            let supervisor, said = supervisorOfRealCommands ()
            supervisor.Take (Wire.Run (1, node, [ "-e"; "process.stdin.on('data', text => process.stdout.write('got ' + text))" ], None, Map.empty))
            supervisor.Take (Wire.Input (1, "typed"))
            supervisor.Take (Wire.EndInput 1)
            let! _ = settledWithin 20000 (fun () -> Option.isSome (endOf 1 said))
            Expect.equal (printedBy 1 Wire.Out said) "got typed" "what the session wrote, and then the end of the input"
        })

        testCaseAsync "a cancel ends a command" (async {
            let supervisor, said = supervisorOfRealCommands ()
            supervisor.Take (Wire.Run (1, node, [ "-e"; "console.log('up'); setTimeout(() => {}, 600000)" ], None, Map.empty))
            let! _ = settledWithin 20000 (fun () -> (printedBy 1 Wire.Out said).Contains "up")
            supervisor.Take (Wire.Cancel 1)
            let! ended = settledWithin 20000 (fun () -> Option.isSome (endOf 1 said))
            Expect.isTrue ended "the command that would run for ten minutes ended"
        })

        testCaseAsync "a cancel of one command leaves a second one running" (async {
            let supervisor, said = supervisorOfRealCommands ()
            let waiting = "console.log('up'); process.stdin.on('data', () => process.exit(5))"
            supervisor.Take (Wire.Run (1, node, [ "-e"; waiting ], None, Map.empty))
            supervisor.Take (Wire.Run (2, node, [ "-e"; waiting ], None, Map.empty))
            let! _ = settledWithin 20000 (fun () -> (printedBy 1 Wire.Out said).Contains "up" && (printedBy 2 Wire.Out said).Contains "up")
            supervisor.Take (Wire.Cancel 1)
            let! _ = settledWithin 20000 (fun () -> Option.isSome (endOf 1 said))
            // The second command ends when it is told to, with its own code.
            supervisor.Take (Wire.Input (2, "go"))
            let! _ = settledWithin 20000 (fun () -> Option.isSome (endOf 2 said))
            Expect.equal (endOf 2 said) (Some (Some 5)) "it was alive to read its input, and it chose its code"
        })

        testCaseAsync "the production entry refuses a first line that names a different process" (async {
            let! said = productionEntryWithAWrongFirstLine ()
            let refused = said |> List.exists (fun event -> match event with Wire.Refused _ -> true | _ -> false)
            Expect.isTrue refused (sprintf "the entry said: %A" said)
        })

        testCaseAsync "an entry that nothing confines refuses a true first line" (async {
            let! said, _ = rehearsalEntryWithATrueFirstLine ()
            let refused = said |> List.exists (fun event -> match event with Wire.Refused _ -> true | _ -> false)
            Expect.isTrue refused (sprintf "the entry said: %A" said)
        })

        testCaseAsync "an entry that nothing confines sends no signal before it leaves" (async {
            let! _, recorded = rehearsalEntryWithATrueFirstLine ()
            Expect.equal recorded "" "the witness beside the entry recorded no signal"
        })


    ]
