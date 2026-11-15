module Yession.Host.Supervisor

// The supervisor of one Seatbelt sandbox: one process INSIDE the sandbox. It starts each
// command of the sandbox, and it ends each process of the sandbox when the sandbox stops.
// `SandboxSupervisor.fs` is the entry that runs it.
//
// Why a process inside. To Seatbelt, each `sandbox-exec` is a sandbox of its own, and a
// profile lets a process signal only its own sandbox (`same-sandbox`). The backend once
// started each command as its own `sandbox-exec` and ended the process GROUPS that still ran
// when the sandbox stopped. A process that made a session of its own was in no group that the
// backend knew, and an Android emulator ran on after its sandbox stopped. Measured on macOS
// 26.5: a second `sandbox-exec` with the same profile cannot signal such a process, and a
// variable that marks the sandbox does not find it, because the kernel shows no environment
// for a program that Apple signed. Inside ONE `sandbox-exec`, a process does signal a child
// that made a session of its own, and a child that launchd adopted. So one `sandbox-exec`
// holds the sandbox, the supervisor is the first process in it, and the kernel decides which
// processes are the sandbox's.
//
// How the stop ends them. The supervisor sends the ending signal to each pid number from 2
// to `lastPid`, its own excepted. The kernel refuses the signal for each process outside the
// sandbox, and takes it for each process inside. Two passes in a row that find nothing end
// the stop. There is no signal to `-1`: measured, such a broadcast also reaches its sender,
// and what it returns does not say which processes still run.
//
// The pass is only as safe as the profile. Under no profile, the same pass ends each process
// of the user. So two proofs stand in front of it, and the pass takes both as values
// (`Confinement`):
//
// - At start, the kernel refuses a signal from the supervisor to the process that started
//   it. That process is the session, which is outside the sandbox and runs as the same user.
// - The kernel refuses a second profile. A process that no profile confines enters a second
//   profile, and a process in a sandbox of ours does not (`Nesting`). The supervisor asks at
//   start, and again before each pass. The question starts a process. When the system can
//   make no process, the answer from the start stands: a profile does not leave a process,
//   and a sandbox that is full of processes is the one that most needs its stop.
//
// A `Target` is a pid of 2 or more. `kill` reads a smaller number as a process group or as
// each process, so the ending signal cannot name one of those.
//
// The end of the supervisor's input is the stop. The session closes the input when the
// sandbox stops, and the kernel closes it when the session dies, so one path ends the
// processes in both cases. A fault of the supervisor itself that nothing caught is a stop
// too: a supervisor that ended with its processes alive would leave nothing that can end
// them. A supervisor that something ends from outside (a signal that it cannot catch, the
// system's memory limit) makes no stop, and the session says so.
//
// This file takes nothing from the backends. The bundle of the entry thus holds this file
// and its codec, and a supervisor starts in the time that Node starts.

open Fable.Core
open Fable.NodeExtras
#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

/// The title that a supervisor gives its own process.
///
/// A block that restarts a server often runs `pkill node`. Measured in a sandbox with the
/// process list: `pkill node` and `pkill -f node` match a process by the name in its
/// arguments, and they did not match a Node process that had this title.
let title : string = "yession-supervisor"

// --- The wire ---------------------------------------------------------------------------------

/// What the session and the supervisor say to each other: one JSON object on one line.
/// The session writes requests to the supervisor's input, and reads events from its output.
[<RequireQualifiedAccess>]
module Wire =

    /// Which of the two streams of a command some output came from.
    type Stream =
        | Out
        | Err

    type Request =
        /// The first line. The session says which process it is and which user it runs as.
        /// The supervisor proves from those two that a profile confines it
        /// (`Confinement.atStart`).
        | Hello of parent: int * uid: int
        /// Start one command, with the whole of its environment. The session gives each
        /// command an id, and each later line about the command carries it.
        | Run of
            id: int *
            executable: string *
            arguments: string list *
            directory: string option *
            environment: Map<string, string>
        /// Text for the input of a command.
        | Input of id: int * text: string
        /// No more input comes for a command.
        | EndInput of id: int
        /// End a command, and each process in its process group.
        | Cancel of id: int

    type Event =
        /// The supervisor holds the proof of `Confinement.atStart`, and starts commands.
        | Ready
        /// The supervisor has no proof, and why. It starts no command and ends no process.
        | Refused of reason: string
        | Output of id: int * stream: Stream * text: string
        /// A command ended, and each of its two streams closed. `None` is a command that a
        /// signal ended.
        | Ended of id: int * code: int option
        /// A command did not start, and why.
        | NotStarted of id: int * reason: string
        /// The stop made its passes. The list is the pids that still took the ending signal
        /// when the stop gave up, and it is empty when each process ended.
        | Stopped of left: int list
        /// The stop made no pass, because a proof did not hold, and why.
        | NotStopped of reason: string

    let private streamName (stream: Stream) : string =
        match stream with
        | Out -> "out"
        | Err -> "err"

    let private decodeStream : Decoder<Stream> =
        Decode.string
        |> Decode.andThen (fun name ->
            match name with
            | "out" -> Decode.succeed Out
            | "err" -> Decode.succeed Err
            | other -> Decode.fail (sprintf "no stream is named %s" other))

    let request (request: Request) : string =
        match request with
        | Hello (parent, uid) -> Encode.object [ "hello", Encode.object [ "parent", Encode.int parent; "uid", Encode.int uid ] ]
        | Run (id, executable, arguments, directory, environment) ->
            Encode.object [
                "run",
                Encode.object (
                    [ "id", Encode.int id
                      "executable", Encode.string executable
                      "arguments", arguments |> List.map Encode.string |> Encode.list
                      "environment", environment |> Map.map (fun _ value -> Encode.string value) |> Encode.dict ]
                    @ (directory |> Option.map (fun directory -> "directory", Encode.string directory) |> Option.toList)
                )
            ]
        | Input (id, text) -> Encode.object [ "input", Encode.object [ "id", Encode.int id; "text", Encode.string text ] ]
        | EndInput id -> Encode.object [ "endInput", Encode.int id ]
        | Cancel id -> Encode.object [ "cancel", Encode.int id ]
        |> Encode.toString 0

    let private decodeRequest : Decoder<Request> =
        Decode.oneOf [
            Decode.field
                "hello"
                (Decode.object (fun get -> Hello (get.Required.Field "parent" Decode.int, get.Required.Field "uid" Decode.int)))
            Decode.field
                "run"
                (Decode.object (fun get ->
                    Run (
                        get.Required.Field "id" Decode.int,
                        get.Required.Field "executable" Decode.string,
                        get.Required.Field "arguments" (Decode.list Decode.string),
                        get.Optional.Field "directory" Decode.string,
                        get.Required.Field "environment" (Decode.dict Decode.string)
                    )))
            Decode.field
                "input"
                (Decode.object (fun get -> Input (get.Required.Field "id" Decode.int, get.Required.Field "text" Decode.string)))
            Decode.field "endInput" Decode.int |> Decode.map EndInput
            Decode.field "cancel" Decode.int |> Decode.map Cancel
        ]

    let event (event: Event) : string =
        match event with
        | Ready -> Encode.object [ "ready", Encode.object [] ]
        | Refused reason -> Encode.object [ "refused", Encode.string reason ]
        | Output (id, stream, text) ->
            Encode.object [
                "output",
                Encode.object [ "id", Encode.int id; "stream", Encode.string (streamName stream); "text", Encode.string text ]
            ]
        | Ended (id, code) ->
            Encode.object [
                "ended",
                Encode.object ([ "id", Encode.int id ] @ (code |> Option.map (fun code -> "code", Encode.int code) |> Option.toList))
            ]
        | NotStarted (id, reason) ->
            Encode.object [ "notStarted", Encode.object [ "id", Encode.int id; "reason", Encode.string reason ] ]
        | Stopped left -> Encode.object [ "stopped", left |> List.map Encode.int |> Encode.list ]
        | NotStopped reason -> Encode.object [ "notStopped", Encode.string reason ]
        |> Encode.toString 0

    let private decodeEvent : Decoder<Event> =
        Decode.oneOf [
            Decode.field "ready" (Decode.succeed Ready)
            Decode.field "refused" Decode.string |> Decode.map Refused
            Decode.field
                "output"
                (Decode.object (fun get ->
                    Output (
                        get.Required.Field "id" Decode.int,
                        get.Required.Field "stream" decodeStream,
                        get.Required.Field "text" Decode.string
                    )))
            Decode.field
                "ended"
                (Decode.object (fun get -> Ended (get.Required.Field "id" Decode.int, get.Optional.Field "code" Decode.int)))
            Decode.field
                "notStarted"
                (Decode.object (fun get -> NotStarted (get.Required.Field "id" Decode.int, get.Required.Field "reason" Decode.string)))
            Decode.field "stopped" (Decode.list Decode.int) |> Decode.map Stopped
            Decode.field "notStopped" Decode.string |> Decode.map NotStopped
        ]

    /// A line that the session sent to the supervisor. `None` for a line that is not a request.
    let parseRequest (line: string) : Request option = Decode.fromString decodeRequest line |> Result.toOption

    /// A line that the supervisor printed. `None` for a line that is not an event: a warning
    /// from Node is a line for a person.
    let parseEvent (line: string) : Event option = Decode.fromString decodeEvent line |> Result.toOption

    /// The most text that one `Input` request carries, in characters.
    let inputLimit : int = 64 * 1024

    /// The requests that carry `text` to the input of command `id`: pieces of `inputLimit`
    /// characters at most, in order.
    ///
    /// One request for a large input was one line of that size, and a reader holds a line
    /// until its end. A pair of surrogates is one character, so no piece ends between the
    /// two: each half alone is not a character, and the command would get two wrong ones.
    let inputs (id: int) (text: string) : Request list =
        let rec cut (from: int) (pieces: Request list) : Request list =
            if from >= text.Length then
                List.rev pieces
            else
                let wanted = min text.Length (from + inputLimit)
                let upTo =
                    if wanted < text.Length && wanted - from > 1 && System.Char.IsHighSurrogate text.[wanted - 1] then wanted - 1
                    else wanted
                cut upTo (Input (id, text.Substring (from, upTo - from)) :: pieces)
        cut 0 []

    /// The longest line that a reader takes, in characters. A request that starts a command
    /// holds its arguments and its environment, which the system limits to one mebibyte, and
    /// JSON can make that text longer.
    let lineLimit : int = 8 * 1024 * 1024

    /// Complete lines out of a stream of text chunks: each whole line goes to `onLine`, and
    /// the tail that has no end yet stays for the next chunk.
    ///
    /// The reader looks at each character once. It keeps the pieces of the line that has no
    /// end yet, and joins them when the end comes. A reader that joined the text at each
    /// chunk, and looked for the end from the start, read a line of 20 MB many times over.
    ///
    /// A line that is longer than `lineLimit` is not a line of this protocol. The reader
    /// drops it up to its end, calls `onOverlong` once for it, and reads the next line.
    let lines (onLine: string -> unit) (onOverlong: unit -> unit) : string -> unit =
        let pieces = ResizeArray<string> ()
        let mutable held = 0
        let mutable dropping = false
        let take (piece: string) =
            if not dropping then
                if held + piece.Length > lineLimit then
                    pieces.Clear ()
                    held <- 0
                    dropping <- true
                    onOverlong ()
                else
                    pieces.Add piece
                    held <- held + piece.Length
        let finish () =
            if dropping then
                dropping <- false
            else
                let line = String.concat "" pieces
                pieces.Clear ()
                held <- 0
                if line.Trim().Length > 0 then onLine line
        fun chunk ->
            let mutable from = 0
            let mutable at = chunk.IndexOf ('\n', from)
            while at >= 0 do
                take (chunk.Substring (from, at - from))
                finish ()
                from <- at + 1
                at <- chunk.IndexOf ('\n', from)
            if from < chunk.Length then take (chunk.Substring from)

// --- What the kernel answers --------------------------------------------------------------------

/// What the kernel answered when this process sent one signal to one process.
[<RequireQualifiedAccess>]
type Answer =
    /// The kernel took the signal for that process.
    | Taken
    /// The kernel refused: this process may not signal that one (`EPERM`).
    | Refused
    /// No process has that pid (`ESRCH`).
    | NoProcess
    /// A different error, in Node's word for it.
    | Fault of string

module Answer =

    /// An answer from the error code that Node gave. `None` is a signal that the kernel took.
    let ofCode (code: string option) : Answer =
        match code with
        | None -> Answer.Taken
        | Some "EPERM" -> Answer.Refused
        | Some "ESRCH" -> Answer.NoProcess
        | Some other -> Answer.Fault other

/// One process that the ending signal can name: a pid of 2 or more.
///
/// `kill` reads a pid of 0 as the process group of the caller, a negative pid as a group or
/// as each process, and pid 1 is launchd. A `Target` is none of those. Private, so `ofPid`
/// is the only way to hold one.
type Target = private Target of int

module Target =

    let ofPid (pid: int) : Target option = if pid >= 2 then Some (Target pid) else None

    let pid (Target pid) : int = pid

// --- The two proofs -----------------------------------------------------------------------------

/// What happened when a child of this process tried to enter a second profile.
[<RequireQualifiedAccess>]
type Nesting =
    /// The kernel refused the second profile, so a profile confines this process.
    | Refused
    /// The second profile applied, so no profile confines this process.
    | Applied
    /// The question did not start, because the system could make no process, and the error.
    /// It says nothing of this process, for or against.
    | NotMade of string
    /// The question ran and gave no answer, or it could not run here, and why.
    | Unanswered of string

module Nesting =

    /// The command that asks: `sandbox-exec`, by its path, with a profile that allows
    /// everything, around a program that does nothing.
    let probe : string * string list =
        "/usr/bin/sandbox-exec", [ "-p"; "(version 1)(allow default)"; "/usr/bin/true" ]

    /// What the end of the probe says.
    ///
    /// Measured on macOS 26.5. Under no profile, the probe ends with code 0. Under a profile
    /// of ours, `sandbox-exec` prints `sandbox_apply: Operation not permitted` and ends with
    /// code 71. Code 71 alone is not that answer: `sandbox-exec` also ends with 71 when it
    /// does not find the program. So only the sentence of the kernel's refusal is `Refused`.
    ///
    /// Measured too: the kernel refuses a second profile under each profile that denies
    /// something, and applies one only under no profile or under a profile that denies
    /// nothing. So a process under a profile of ours does not get a different profile.
    let read (status: int option) (said: string option) : Nesting =
        match status, said with
        | Some 0, _ -> Nesting.Applied
        | Some _, Some text when text.Contains "sandbox_apply: Operation not permitted" -> Nesting.Refused
        | Some code, Some text -> Nesting.Unanswered (sprintf "sandbox-exec ended with code %d and said: %s" code (text.Trim ()))
        | Some code, None -> Nesting.Unanswered (sprintf "sandbox-exec ended with code %d and said nothing" code)
        | None, _ -> Nesting.Unanswered "sandbox-exec did not run to its end"

    /// What a probe that did not start says, from the error code that Node gave.
    ///
    /// Four codes mean that the system could make no process, or no pipe for one: the limit
    /// of processes for the user (`EAGAIN`), memory (`ENOMEM`), and the two limits of open
    /// files (`EMFILE`, `ENFILE`). A sandbox that a fork bomb filled gives the first. Each
    /// other code is a probe that is not there or that may not run, which is no answer.
    let notStarted (code: string option) (said: string) : Nesting =
        match code with
        | Some "EAGAIN"
        | Some "ENOMEM"
        | Some "EMFILE"
        | Some "ENFILE" -> Nesting.NotMade said
        | Some _
        | None -> Nesting.Unanswered (sprintf "sandbox-exec did not start: %s" said)

/// The proof, at start, that a profile confines this process: the kernel refuses a signal
/// from it to the process that started it, and the kernel refuses a second profile for it.
/// Private, so `Confinement.atStart` is the only way to hold one.
type ProofAtStart = private | ProofAtStart

/// The proof, for one pass, that a profile confines this process now. Private, so
/// `Confinement.beforePass` is the only way to hold one.
type ProofBeforePass = private | ProofBeforePass

module Confinement =

    /// The first proof, or why there is none.
    ///
    /// `saidParent` and `saidUid` are what the session said of itself in its first line.
    /// `parent` and `uid` are what this process finds: the process that started it, and
    /// the user that it runs as. `answer` is what the kernel said to signal 0 for `parent`.
    ///
    /// A refusal proves confinement only when the kernel had no other reason to refuse. The
    /// kernel also refuses a signal to a process of a different user, and launchd is one. So
    /// the parent must be the process that the session says it is, it must not be launchd,
    /// and the session must say the user that this process runs as.
    ///
    /// `nesting` is what a second profile did at start. Only a refusal by the kernel is a
    /// proof here: a question that did not start proves nothing yet.
    let atStart
        (saidParent: int)
        (saidUid: int)
        (parent: int)
        (uid: int)
        (answer: Answer)
        (nesting: Nesting)
        : Result<ProofAtStart, string> =
        if parent < 2 then
            Error "the process that started the supervisor has gone, so nothing proves that a profile confines the supervisor"
        elif saidParent <> parent then
            Error (
                sprintf
                    "the session says that it is process %d, and process %d started the supervisor. Nothing proves that a profile confines the supervisor"
                    saidParent
                    parent)
        elif saidUid <> uid then
            Error (
                sprintf
                    "the session says that it runs as user %d, and the supervisor runs as user %d. The kernel refuses a signal between two users, so its refusal proves nothing here"
                    saidUid
                    uid)
        else
            match answer with
            | Answer.Refused ->
                match nesting with
                | Nesting.Refused -> Ok ProofAtStart
                | Nesting.Applied ->
                    Error "no profile confines the supervisor: a second profile applied. A stop would end processes outside the sandbox"
                | Nesting.NotMade why ->
                    Error (sprintf "nothing proves that a profile confines the supervisor: the system could make no process to ask (%s)" why)
                | Nesting.Unanswered why -> Error (sprintf "nothing proves that a profile confines the supervisor: %s" why)
            | Answer.Taken ->
                Error
                    "the kernel lets the supervisor signal the process that started it. No profile confines the supervisor, or a rule of the profile lets a signal out of the sandbox. A stop would end processes outside the sandbox"
            | Answer.NoProcess ->
                Error "the process that started the supervisor has gone, so nothing proves that a profile confines the supervisor"
            | Answer.Fault fault ->
                Error (sprintf "the kernel answered %s to a signal for the process that started the supervisor, which proves nothing" fault)

    /// The proof for one pass, or why there is none.
    ///
    /// The kernel refused a second profile at start (`ProofAtStart`), and a profile does not
    /// leave a process. So when the system can make no process to ask again, that proof
    /// stands: a sandbox that a fork bomb filled is the one that most needs its stop. A
    /// second profile that applied is no proof, and neither is a question that ran and
    /// answered something else.
    let beforePass (_: ProofAtStart) (nesting: Nesting) : Result<ProofBeforePass, string> =
        match nesting with
        | Nesting.Refused
        | Nesting.NotMade _ -> Ok ProofBeforePass
        | Nesting.Applied -> Error "no profile confines the supervisor now: a second profile applied. A pass would end processes outside the sandbox"
        | Nesting.Unanswered why -> Error (sprintf "nothing proves that a profile confines the supervisor now: %s" why)

// --- The stop -----------------------------------------------------------------------------------

/// The highest pid that macOS gives a process: `PID_MAX` in XNU's `sys/proc_internal.h`.
let lastPid : int = 99999

/// One pass: the ending signal to each process but this one, by its pid. Answers the pids
/// that took the signal and were alive.
///
/// The pass takes both proofs, so no caller makes one without them. `send` is the ending
/// signal for one process, and it is an argument: the entry gives the signal that ends a
/// process, and a rehearsal gives one that a process ignores.
///
/// The pass asks `alive` only about a pid that took the signal. The kernel answers a signal for a
/// zombie as it does for a process of this sandbox, whoever the zombie belonged to, and a
/// zombie that its parent does not collect stays. Without the question, such a zombie would
/// be in the answer of each pass.
let pass
    (_: ProofAtStart)
    (_: ProofBeforePass)
    (self: int)
    (send: Target -> Answer)
    (alive: int -> bool)
    : int list =
    [ for pid in 2 .. lastPid do
          if pid <> self then
              match Target.ofPid pid with
              | Some target ->
                  match send target with
                  | Answer.Taken when alive pid -> yield pid
                  | Answer.Taken
                  | Answer.Refused
                  | Answer.NoProcess
                  | Answer.Fault _ -> ()
              | None -> () ]

/// How a stop ended.
[<RequireQualifiedAccess>]
type Stop =
    /// Two passes in a row found no process to end.
    | Ended
    /// The time for the stop ran out. These pids took the signal in the last pass.
    | Left of pids: int list
    /// A proof did not hold before a pass, so that pass and each pass after it did not run.
    | Unproven of reason: string

/// The time in which a stop starts a question or a pass, in milliseconds.
let stopBudgetMs : float = 10_000.0

/// The longest time that one question for the second proof takes, in milliseconds. The
/// port that asks ends a question that takes longer, and that is no answer.
let probeLimitMs : int = 2_000

/// The longest time from the end of a supervisor's input to the end of the supervisor, in
/// milliseconds: the time in which it starts a question, that last question and its pass,
/// and a margin. The session waits this long before it gives up on a stop. It is one
/// definition, so the session does not end a supervisor in the middle of its stop.
let longestStopMs : int = int stopBudgetMs + probeLimitMs + 3_000

/// What a stop needs from the world.
type StopPorts =
    { /// The pid of this process.
      Self : int
      /// The ending signal, for one process.
      End : Target -> Answer
      /// Whether a live process has this pid. A zombie is not alive.
      Alive : int -> bool
      /// Whether a profile confines this process now, asked of the kernel.
      Nested : unit -> Async<Nesting>
      /// The time between two passes: long enough for a process that took the signal to go.
      Pause : unit -> Async<unit>
      /// The time now, in milliseconds.
      Now : unit -> float }

/// End each process of the sandbox but this one.
///
/// The stop asks for the second proof again before each pass. Two passes in a row that find
/// nothing end the stop: one empty pass is not enough, because a process can start a child
/// with a lower pid and end itself while a pass goes by.
///
/// The stop looks at the clock before each question. After `stopBudgetMs` it starts no
/// question and no pass, and answers the pids that the last pass found.
let stop (ports: StopPorts) (atStart: ProofAtStart) : Async<Stop> =
    let deadline = ports.Now () + stopBudgetMs
    let rec next (empty: int) (last: int list) : Async<Stop> =
        async {
            if ports.Now () >= deadline then
                return Stop.Left last
            else
                let! nesting = ports.Nested ()
                match Confinement.beforePass atStart nesting with
                | Error reason -> return Stop.Unproven reason
                | Ok beforePass ->
                    match pass atStart beforePass ports.Self ports.End ports.Alive with
                    | [] when empty = 1 -> return Stop.Ended
                    | [] ->
                        do! ports.Pause ()
                        return! next 1 []
                    | found ->
                        do! ports.Pause ()
                        return! next 0 found
        }
    next 0 []

// --- The supervisor -----------------------------------------------------------------------------

/// One command that a supervisor started, as the supervisor holds it.
type Command =
    { /// Text for the input of the command.
      Write : string -> unit
      /// No more input comes.
      EndInput : unit -> unit
      /// End the command, and each process in its process group. The group is the command's
      /// own only until its leader ends.
      Cancel : unit -> unit }

/// How a command ended, as the port that started it reports it.
[<RequireQualifiedAccess>]
type CommandEnd =
    /// The command ran and ended. `None` is a command that a signal ended.
    | Ended of code: int option
    /// The command did not start, and why.
    | NotStarted of reason: string

/// Start one command (`Ports.Start`): its executable, its arguments, its directory, the
/// whole of its environment, and three handlers. The first takes its output. The port
/// calls the second when the leader of the command ends, which can be before the command
/// ends: a child of the leader can still hold an output. The third takes its end.
type StartCommand =
    string
        -> string list
        -> string option
        -> Map<string, string>
        -> (Wire.Stream * string -> unit)
        -> (unit -> unit)
        -> (CommandEnd -> unit)
        -> Command

/// What a supervisor needs from the world.
type Ports =
    { /// The pid of the process that started this one, now.
      Parent : unit -> int
      /// The user that this process runs as.
      Uid : int
      /// What the kernel answers to signal 0 for one pid. Signal 0 ends nothing.
      MaySignal : int -> Answer
      /// Start a command (`StartCommand`).
      Start : StartCommand
      /// One event for the session.
      Say : Wire.Event -> unit
      /// Words for a person who reads the log of the session.
      Warn : string -> unit
      /// End this process with a code, after the session has each event.
      Leave : int -> unit
      Stop : StopPorts }

/// A supervisor: what it does with a request, and what it does when its input ends.
type Supervisor =
    { Take : Wire.Request -> unit
      /// The input ended: the session stopped the sandbox, or the session died.
      InputEnded : unit -> unit
      /// Something in this process failed, and nothing caught it. The supervisor makes its
      /// stop, as for the end of its input: its processes must not stay without it.
      Fault : string -> unit }

/// The code that a supervisor leaves with.
module ExitCode =
    /// The stop ended each process.
    let ended : int = 0
    /// The supervisor had no proof at start. It started no command and ended no process.
    let refused : int = 2
    /// The stop gave up, and some processes still took the signal.
    let left : int = 3
    /// A proof did not hold before a pass.
    let unproven : int = 4

/// A supervisor over `ports`.
///
/// A supervisor with no proof starts no command. So when its input ends it has nothing to
/// end, and it makes no pass: the pass needs the proof as a value.
let create (ports: Ports) : Supervisor =
    let mutable proof : Result<ProofAtStart, string> option = None
    let mutable greeted = false
    let mutable stopping = false
    let commands = System.Collections.Generic.Dictionary<int, Command> ()
    // The ids of the commands that have no end yet. A command has one end, whatever its
    // port reports: Node reports a start that fails late as an error, and then as a close.
    let running = System.Collections.Generic.HashSet<int> ()
    // The ids of the commands whose leader ended and that have no end yet: a child of
    // the leader still holds an output of the command. The group of such a command is not
    // the command's own any more. The system can give its number to a different process, so
    // a cancel for it sends nothing, and the stop ends what the command left.
    let leaderless = System.Collections.Generic.HashSet<int> ()
    let ended (id: int) (how: CommandEnd) =
        if running.Remove id then
            commands.Remove id |> ignore
            leaderless.Remove id |> ignore
            match how with
            | CommandEnd.Ended code -> ports.Say (Wire.Ended (id, code))
            | CommandEnd.NotStarted reason -> ports.Say (Wire.NotStarted (id, reason))
    let hello (saidParent: int) (saidUid: int) =
        greeted <- true
        let parent = ports.Parent ()
        // Signal 0 for a pid below 2 would ask about a process group, or about launchd.
        let answer = if parent >= 2 then ports.MaySignal parent else Answer.NoProcess
        async {
            let! nesting = ports.Stop.Nested ()
            // The input can end while the question runs. A supervisor that stops says no more.
            if not stopping then
                let result = Confinement.atStart saidParent saidUid parent ports.Uid answer nesting
                proof <- Some result
                match result with
                | Ok _ -> ports.Say Wire.Ready
                | Error reason ->
                    ports.Say (Wire.Refused reason)
                    ports.Leave ExitCode.refused
        }
        |> Async.StartImmediate
    let run (id: int) (executable: string) (arguments: string list) (directory: string option) (environment: Map<string, string>) =
        if running.Contains id then
            ports.Say (Wire.NotStarted (id, sprintf "the supervisor already runs a command with the id %d" id))
        else
            running.Add id |> ignore
            // Node throws at once for some starts: a directory that is a file, a NUL in an
            // argument, an environment that is too long. That is one command that did not
            // start. It is not a fault of the supervisor, which has other commands to hold.
            let started =
                try
                    Ok (
                        ports.Start
                            executable
                            arguments
                            directory
                            environment
                            (fun (stream, text) -> ports.Say (Wire.Output (id, stream, text)))
                            (fun () -> if running.Contains id then leaderless.Add id |> ignore)
                            (ended id)
                    )
                with thrown -> Error thrown.Message
            match started with
            | Ok command -> if running.Contains id then commands.[id] <- command
            | Error reason -> ended id (CommandEnd.NotStarted reason)
    let find (id: int) (act: Command -> unit) =
        match commands.TryGetValue id with
        | true, command -> act command
        | _ -> ()
    let inputEnded () =
        if not stopping then
            stopping <- true
            match proof with
            | Some (Ok atStart) ->
                async {
                    try
                        match! stop ports.Stop atStart with
                        | Stop.Ended ->
                            ports.Say (Wire.Stopped [])
                            ports.Leave ExitCode.ended
                        | Stop.Left pids ->
                            ports.Say (Wire.Stopped pids)
                            ports.Leave ExitCode.left
                        | Stop.Unproven reason ->
                            ports.Say (Wire.NotStopped reason)
                            ports.Leave ExitCode.unproven
                    with thrown ->
                        // A stop that failed cannot be made again by this process. The
                        // supervisor leaves with the code of a stop that left processes, so
                        // the session says that they can still run. A supervisor that
                        // waited here would hold a session that has gone for all time.
                        ports.Warn (sprintf "the stop failed: %s" thrown.Message)
                        ports.Leave ExitCode.left
                }
                |> Async.StartImmediate
            | Some (Error _)
            | None -> ports.Leave ExitCode.refused
    let fault (what: string) =
        if not stopping then
            ports.Warn (sprintf "%s. The supervisor stops the sandbox" what)
            inputEnded ()
    let take (request: Wire.Request) =
        if not stopping then
            try
                match request, proof with
                | Wire.Hello (saidParent, saidUid), None when not greeted -> hello saidParent saidUid
                // A second first line changes nothing: the proof is of the start.
                | Wire.Hello _, _ -> ()
                | Wire.Run (id, executable, arguments, directory, environment), Some (Ok _) ->
                    run id executable arguments directory environment
                | Wire.Run (id, _, _, _, _), Some (Error _)
                | Wire.Run (id, _, _, _, _), None ->
                    ports.Say (Wire.NotStarted (id, "the supervisor has no proof that a profile confines it, so it starts no command"))
                | Wire.Input (id, text), _ -> find id (fun command -> command.Write text)
                | Wire.EndInput id, _ -> find id (fun command -> command.EndInput ())
                | Wire.Cancel id, _ -> if not (leaderless.Contains id) then find id (fun command -> command.Cancel ())
            with thrown -> fault (sprintf "a request failed: %s" thrown.Message)
    { Take = take; InputEnded = inputEnded; Fault = fault }

// --- The real ports -----------------------------------------------------------------------------

/// The ports of a supervisor in this process.
///
/// `ending` is the signal that ends a process. It is an argument and not a constant: the
/// entry gives `SIGKILL`, and a rehearsal gives a signal that a process ignores, so the first
/// runs of a new build end nothing.
let ports (ending: string) : Ports =
    let out = ProcessStreams.stdout ()
    // The session has gone when a write to it fails. The supervisor still has its stop to
    // make, so the failure must not end this process before the stop does.
    let mutable broken = false
    let mutable leaving : int option = None
    out.onError (fun _ ->
        broken <- true
        leaving |> Option.iter Processes.exitWith)
    // A warning from Node goes to the error output, which the session also holds.
    (ProcessStreams.stderr ()).onError ignore
    // The output of a command stops while the session does not read. Node does not wait for
    // a write to a pipe, so without this a command that prints faster than the session reads
    // would fill the memory of the supervisor, where it once filled a pipe and stopped.
    let flowing = ResizeArray<Readable> ()
    let mutable held = false
    let say (event: Wire.Event) : unit =
        if not broken then
            if not (out.writeText (Wire.event event + "\n")) && not held then
                held <- true
                for stream in flowing do
                    stream.pause ()
                out.onceDrain (fun () ->
                    held <- false
                    for stream in flowing do
                        stream.resume ())
    let reading (stream: Readable) : unit =
        flowing.Add stream
        if held then stream.pause ()
    let start
        (executable: string)
        (arguments: string list)
        (directory: string option)
        (environment: Map<string, string>)
        (onOutput: Wire.Stream * string -> unit)
        (onLeaderEnded: unit -> unit)
        (onEnd: CommandEnd -> unit)
        : Command =
        let child =
            ChildProcesses.spawn
                executable
                arguments
                { Cwd = directory
                  Env = ChildEnv.Replacing environment
                  Streams = { Stdin = Stdio.Pipe; Stdout = Stdio.Pipe; Stderr = Stdio.Pipe }
                  Detached = true }
        let input = ChildProcessStreams.stdin child
        let output = ChildProcessStreams.stdout child
        let errors = ChildProcessStreams.stderr child
        // A write to a command that has gone is an EPIPE on this stream. The end of the
        // command is what reports it.
        input.onError ignore
        // Node reports the end of the leader here, before the end of the command where a
        // child of the leader still holds an output.
        ChildProcessStreams.onExit child (fun _ -> onLeaderEnded ())
        Readables.text output (fun text -> onOutput (Wire.Out, text))
        Readables.text errors (fun text -> onOutput (Wire.Err, text))
        reading output
        reading errors
        let ended (how: CommandEnd) =
            flowing.Remove output |> ignore
            flowing.Remove errors |> ignore
            onEnd how
        ChildProcessStreams.onError child (fun error -> ended (CommandEnd.NotStarted (StreamError.describe error)))
        ChildProcessStreams.onClose child (fun code -> ended (CommandEnd.Ended code))
        { Write = fun text -> try input.writeText text |> ignore with _ -> ()
          EndInput = fun () -> try input.finish () with _ -> ()
          // The group is the command's own: its leader is the child that started here. A
          // child that did not start has no pid, and thus no group to signal
          // (`ChildProcesses.signalGroup`). The supervisor asks for no cancel after the leader
          // ended (`create`).
          Cancel = fun () -> ChildProcesses.signalGroup child ending |> ignore }
    // The question for the second proof: one child that tries to enter a second profile.
    // It ends in `probeLimitMs`, or this ends it, and that is no answer.
    let nested () : Async<Nesting> =
        Async.FromContinuations (fun (cont, _, _) ->
            let mutable answered = false
            let answer (nesting: Nesting) =
                if not answered then
                    answered <- true
                    cont nesting
            let program, arguments = Nesting.probe
            try
                let probe =
                    ChildProcesses.spawn
                        program
                        arguments
                        { Cwd = None
                          Env = ChildEnv.Replacing Map.empty
                          Streams = { Stdin = Stdio.Ignore; Stdout = Stdio.Ignore; Stderr = Stdio.Pipe }
                          Detached = false }
                let said = System.Text.StringBuilder ()
                let limit =
                    JS.setTimeout
                        (fun () ->
                            // Node signals this child only while it is this process's child.
                            ChildProcesses.signal probe "SIGKILL" |> ignore
                            answer (Nesting.Unanswered (sprintf "sandbox-exec did not end in %d ms" probeLimitMs)))
                        probeLimitMs
                ChildProcessStreams.onError probe (fun error ->
                    JS.clearTimeout limit
                    answer (Nesting.notStarted (StreamError.errno error) (StreamError.describe error)))
                ChildProcessStreams.onClose probe (fun code ->
                    JS.clearTimeout limit
                    answer (Nesting.read code (Some (said.ToString ()))))
                Readables.text (ChildProcessStreams.stderr probe) (fun text -> said.Append text |> ignore)
            with thrown -> answer (Nesting.notStarted (Errors.errno thrown) thrown.Message))
    { Parent = Processes.parent
      Uid = int (Node.Api.``process``.getuid ())
      MaySignal = fun pid -> Answer.ofCode (Processes.tryProbe pid)
      Start = start
      Say = say
      Warn = fun words -> eprintfn "%s: %s" title words
      Leave =
        fun code ->
            leaving <- Some code
            if broken then Processes.exitWith code
            else ProcessStreams.writeThen "" (fun () -> Processes.exitWith code) |> ignore
      Stop =
        { Self = int Node.Api.``process``.pid
          End = fun target -> Answer.ofCode (Processes.trySignal (Target.pid target) ending)
          Alive = Processes.isAlive
          Nested = nested
          Pause = fun () -> Async.Sleep 20
          Now = fun () -> float (System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds ()) } }

/// Run a supervisor in this process: requests from its input, events to its output.
///
/// The end of the input is the stop. The session closes the input when the sandbox stops,
/// and the kernel closes it when the session dies. A fault that nothing caught is a stop
/// too (`Supervisor.Fault`): Node would end this process for it, and the processes of the
/// sandbox would stay with nothing that can end them.
let run (ending: string) : unit =
    Node.Api.``process``.title <- title
    let held = ports ending
    let supervisor = create held
    Processes.onFault (fun what -> supervisor.Fault (sprintf "a fault that nothing caught (%s)" what))
    let input = ProcessStreams.stdin ()
    input.onEnd supervisor.InputEnded
    Readables.text
        input
        (Wire.lines
            (fun line ->
                match Wire.parseRequest line with
                | Some request -> supervisor.Take request
                // The line is not shown: a request holds the environment of a command.
                | None -> held.Warn (sprintf "a line of %d characters is not a request, and was skipped" line.Length))
            (fun () -> held.Warn (sprintf "a line of more than %d characters is not a request, and was dropped" Wire.lineLimit)))
