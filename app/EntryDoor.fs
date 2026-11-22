module Yession.Host.EntryDoor

// The door of a session: a socket on the host through which a program on the host runs one
// command in one sandbox of this session, with the bytes of the command connected.
//
// The Session owns the door, because the Session owns the sandbox. It knows the backend, the
// entrypoint, the directory and the environment of each sandbox, so a command that enters
// here runs as a terminal of the session runs. The permissions of the socket decide who can
// dial it, which is the rule of the Manager's own socket.
//
// Each connection is one entry. The Session records its start and its end
// (`SandboxEntered`, `SandboxLeft`), and does not record its bytes. An entry never starts a
// sandbox.
//
// `EntryWire` holds the bytes of the socket, and the `yession-enter` bin (`Enter.fs`) is the
// far end. See docs/decisions/2026-11-22-a-sandbox-has-a-door-on-a-socket.md.

open Fable.Core
open Fable.NodeExtras
open Node.Api
open Yession.Domain
open Yession.Domain.Sandboxes
open Yession.Session

/// The open entries that one session permits. A person's editor opens more than one
/// connection, and this limit is above what one person uses.
let entryLimit = 16

/// The longest subject, label and command that an entry can record. The record is durable,
/// and a caller of the door chooses these words.
let private longestSubject = 128
let private longestLabel = 32
let private longestCommand = 8192
let private longestAddress = 256

/// The ways into its sandboxes that one session holds at one time. A deployment offers one
/// way for each sandbox and label.
let wayLimit = 16

/// What a door needs from the session that it serves.
[<NoEquality; NoComparison>]
type Config =
    { /// The path of the socket (`DoorPlace.socket`).
      SocketPath : string
      /// The open entries that the door permits at one time.
      Limit : int
      /// Start a command in a sandbox, with bytes on its streams. An error is a refusal in
      /// words: the sandbox is not there, or it does not run.
      SpawnIn : SandboxRef -> SandboxExec -> (OutputStream * byte [] -> unit) -> Async<Result<SandboxByteHandle, string>>
      /// Record one event of an entry in the log of the session.
      Record : SessionEvent -> Async<unit>
      /// A new id for an entry.
      MintEntry : unit -> EntryId }

/// A door that listens.
[<NoEquality; NoComparison>]
type Door =
    { /// The path of the socket.
      ListensAt : string
      /// Stop listening and remove the socket. An open entry stays until its caller goes.
      Close : unit -> unit }

/// The spawn that a door uses for the sandboxes of a session.
///
/// A sandbox that the session has and that does not run is refused here, with the reason.
/// `EnvironmentFor` answers a sandbox that the session does not have, and names the ones
/// that it has.
let spawnIn
    (sandboxes: unit -> WorkSandboxes.WorkSandboxes)
    (sandbox: SandboxRef)
    (exec: SandboxExec)
    (onChunk: OutputStream * byte [] -> unit)
    : Async<Result<SandboxByteHandle, string>> =
    async {
        let all = sandboxes ()
        let environment = all.EnvironmentFor sandbox
        let has = all.Listed () |> List.exists (fun running -> running.Ref = sandbox)
        if has && Option.isNone (environment.CurrentRef ()) then
            return
                Error (
                    sprintf
                        "The sandbox '%s' does not run now. An entry does not start a sandbox. Start the sandbox in the session, then try again."
                        (SandboxRef.render sandbox))
        else
            return! environment.SpawnBytes exec onChunk
    }

/// How one entry ended: the code for the record, the reason for the record, the code for the
/// caller, and words for the caller when the command has no code of its own.
let ending (callerClosed: bool) (run: SandboxRun) : int option * string * int * string option =
    if callerClosed then None, "the caller closed the door", EntryWire.ExitCode.noCode, None
    else
        match run with
        | SandboxExited code when code >= 0 -> Some code, "the command ended", code, None
        | SandboxExited _ ->
            None, "the command ended with no exit code", EntryWire.ExitCode.noCode, Some "The command ended with no exit code."
        | SandboxSignalled signal ->
            let reason = sprintf "the signal %s ended the command" signal
            None, reason, EntryWire.ExitCode.noCode, Some (sprintf "The signal %s ended the command." signal)
        | SandboxRunFailed reason ->
            None, sprintf "the command could not run: %s" reason, EntryWire.ExitCode.noCode, Some (sprintf "The command could not run: %s" reason)
        | SandboxKilledForMemory _ ->
            Some 137,
            "the kernel ended the command, because the sandbox had no memory left",
            137,
            Some "The kernel ended the command, because the sandbox had no memory left."

/// Read the first line of a caller as a request that this door can serve. The error is the
/// refusal that the caller gets.
let request (hello: EntryWire.Hello) : Result<SandboxRef * SandboxExec, string> =
    match SandboxRef.parse hello.ToSandbox with
    | Error reason -> Error (sprintf "'%s' does not name a sandbox: %s" hello.ToSandbox reason)
    | Ok sandbox ->
        match hello.Argv with
        | [] -> Error "The command is empty. Give a command after the sandbox."
        | executable :: arguments ->
            if System.String.IsNullOrWhiteSpace hello.AsSubject || hello.AsSubject.Length > longestSubject then
                Error (sprintf "The subject must have between 1 and %d characters." longestSubject)
            elif System.String.IsNullOrWhiteSpace hello.Label || hello.Label.Length > longestLabel then
                Error (sprintf "The label must have between 1 and %d characters." longestLabel)
            elif (hello.Argv |> List.sumBy (fun word -> word.Length + 1)) > longestCommand then
                Error (sprintf "The command must have %d characters at most." longestCommand)
            else
                Ok (
                    sandbox,
                    { Executable = executable
                      Arguments = arguments
                      Env = Map.empty
                      WorkingDirectory = None
                      Via = if hello.Bare then Direct else Entrypoint }
                )

/// Read the first line of a caller that offers a way into a sandbox. The answer is the
/// sandbox, the label and the address, as the session records them. The error is the refusal
/// that the caller gets.
let offer (hello: EntryWire.Hello) (address: string) : Result<SandboxRef * string * string, string> =
    match SandboxRef.parse hello.ToSandbox with
    | Error reason -> Error (sprintf "'%s' does not name a sandbox: %s" hello.ToSandbox reason)
    | Ok sandbox ->
        if not (List.isEmpty hello.Argv) then Error "An offer of a way runs no command. Give --offer with no command."
        elif System.String.IsNullOrWhiteSpace hello.Label || hello.Label.Length > longestLabel then
            Error (sprintf "The label must have between 1 and %d characters." longestLabel)
        elif System.String.IsNullOrWhiteSpace address || address.Length > longestAddress then
            Error (sprintf "The address must have between 1 and %d characters." longestAddress)
        elif address |> Seq.exists (fun letter -> letter < ' ') then
            Error "The address must be one line of text."
        else Ok (sandbox, hello.Label.Trim (), address.Trim ())

/// The ways that the connections of one door hold now. The key is the sandbox and the label.
/// The value says which connection holds the way: a second offer of the same way replaces
/// the first, and the end of the first connection then withdraws nothing.
[<NoEquality; NoComparison>]
type private Ways =
    { Holders : System.Collections.Generic.Dictionary<string, int>
      mutable LastHolder : int }

/// Where one connection is.
[<RequireQualifiedAccess; NoEquality; NoComparison>]
type private Phase =
    /// The first line did not come yet. These bytes are its start.
    | Greeting of soFar: byte []
    /// The command starts. Bytes that come now wait for it.
    | Starting
    /// The command runs.
    | Running of SandboxByteHandle
    /// The caller offers a way, and runs no command. The way holds while the connection is
    /// open.
    | Offering
    /// The connection is of no more use.
    | Over

/// Serve one connection: one entry, one way, or one refusal.
let private serve (config: Config) (openEntries: int ref) (ways: Ways) (connection: Duplex) : unit =
    let mutable phase = Phase.Greeting (Array.zeroCreate 0)
    let mutable reader = EntryWire.FrameReader.empty
    let mutable callerClosed = false
    let mutable counted = false
    // The way that this connection holds: its key, its holder number, and what to record
    // when it goes.
    let mutable held : (string * int * SandboxRef * string) option = None
    // What the caller sent while the command started, in order.
    let waiting = ResizeArray<byte []> ()
    let mutable waitingBytes = 0

    let send (bytes: byte []) : unit =
        if not callerClosed then connection.sink.writeByteArray bytes |> ignore

    let release () : unit =
        if counted then
            counted <- false
            openEntries.Value <- openEntries.Value - 1

    let refuse (reason: string) : unit =
        phase <- Phase.Over
        release ()
        send (EntryWire.bytesOf (EntryWire.answer (EntryWire.Answer.Refused reason)))
        if not callerClosed then connection.finish ()

    /// Give the frames of the caller to the command. A frame that only the Session sends
    /// is a caller that does not speak this wire, and the entry ends.
    let give (handle: SandboxByteHandle) (frames: EntryWire.Frame list) : unit =
        frames
        |> List.iter (fun frame ->
            match frame with
            | EntryWire.Frame.Input bytes -> handle.WriteBytes bytes
            | EntryWire.Frame.InputClosed -> handle.CloseInput ()
            | EntryWire.Frame.Output _
            | EntryWire.Frame.ErrorOutput _
            | EntryWire.Frame.Exited _ ->
                phase <- Phase.Over
                handle.Kill ()
                connection.destroy ())

    /// Add the bytes of the caller to the reader. The answer is the frames that are whole.
    let framesIn (chunk: byte []) : EntryWire.Frame list =
        match EntryWire.FrameReader.feed reader chunk with
        | Ok (frames, next) ->
            reader <- next
            frames
        | Error _ ->
            // Not this wire. The close of the connection ends the command.
            phase <- Phase.Over
            connection.destroy ()
            []

    /// One chunk of the caller, for a command that runs. A chunk that ends the entry makes
    /// the chunks after it of no use.
    let takeWhileRunning (handle: SandboxByteHandle) (chunk: byte []) : unit =
        match phase with
        | Phase.Running _ -> give handle (framesIn chunk)
        | Phase.Greeting _
        | Phase.Starting
        | Phase.Offering
        | Phase.Over -> ()

    /// One chunk of the caller, for a command that did not start yet. A caller that sends
    /// more than one frame can hold before it has its answer does not speak this wire.
    let keep (chunk: byte []) : unit =
        waitingBytes <- waitingBytes + chunk.Length
        if waitingBytes > EntryWire.maxPayload then
            phase <- Phase.Over
            connection.destroy ()
        elif chunk.Length > 0 then waiting.Add chunk

    let start (hello: EntryWire.Hello) (sandbox: SandboxRef) (exec: SandboxExec) : unit =
        // Output can come before the answer is on the wire. It waits here until then.
        let early = ResizeArray<byte []> ()
        let mutable admitted = false
        let onChunk (stream: OutputStream, bytes: byte []) : unit =
            let wrap =
                match stream with
                | Stdout -> EntryWire.Frame.Output
                | Stderr -> EntryWire.Frame.ErrorOutput
            EntryWire.framesOf wrap bytes
            |> List.iter (fun framed -> if admitted then send framed else early.Add framed)
        Async.StartImmediate (
            async {
                let! spawned = config.SpawnIn sandbox exec onChunk
                match spawned with
                | Error reason -> refuse reason
                | Ok handle ->
                    let entry = config.MintEntry ()
                    send (EntryWire.bytesOf (EntryWire.answer EntryWire.Answer.Admitted))
                    admitted <- true
                    early |> Seq.iter send
                    early.Clear ()
                    if callerClosed then handle.Kill ()
                    else
                        phase <- Phase.Running handle
                        let sooner = List.ofSeq waiting
                        waiting.Clear ()
                        sooner |> List.iter (takeWhileRunning handle)
                    do!
                        config.Record (
                            SessionEvent.SandboxEntered
                                { SandboxEntered.Entry = entry
                                  SandboxEntered.EnteredSandbox = sandbox
                                  SandboxEntered.EnteredAs = hello.AsSubject.Trim ()
                                  SandboxEntered.ViaLabel = hello.Label.Trim ()
                                  SandboxEntered.EnteredCommand = hello.Argv })
                    let! run = handle.Exited
                    let recorded, reason, code, said = ending callerClosed run
                    match said with
                    | Some words -> send (EntryWire.frame (EntryWire.Frame.ErrorOutput (EntryWire.bytesOf (words + "\n"))))
                    | None -> ()
                    send (EntryWire.frame (EntryWire.Frame.Exited code))
                    phase <- Phase.Over
                    if not callerClosed then connection.finish ()
                    release ()
                    do!
                        config.Record (
                            SessionEvent.SandboxLeft
                                { SandboxLeft.LeftEntry = entry
                                  SandboxLeft.ExitCode = recorded
                                  SandboxLeft.EndedBecause = reason })
            })

    /// Take back the way that this connection holds, when it holds one and no later
    /// connection took it over.
    let withdraw () : unit =
        match held with
        | None -> ()
        | Some (key, mine, sandbox, label) ->
            held <- None
            if ways.Holders.ContainsKey key && ways.Holders.[key] = mine then
                ways.Holders.Remove key |> ignore
                Async.StartImmediate (
                    config.Record (
                        SessionEvent.SandboxWayWithdrawn
                            { SandboxWayWithdrawn.WithdrawnTo = sandbox
                              SandboxWayWithdrawn.WithdrawnVia = label }))

    /// A caller that offers a way. The session records the address and holds the way until
    /// the connection closes.
    let offered (hello: EntryWire.Hello) (address: string) : unit =
        match offer hello address with
        | Error reason -> refuse reason
        | Ok (sandbox, label, said) ->
            let key = SandboxRef.render sandbox + "\n" + label
            if not (ways.Holders.ContainsKey key) && ways.Holders.Count >= wayLimit then
                refuse (sprintf "This session has %d ways, which is its limit. Take one back, then try again." wayLimit)
            else
                ways.LastHolder <- ways.LastHolder + 1
                let mine = ways.LastHolder
                ways.Holders.[key] <- mine
                held <- Some (key, mine, sandbox, label)
                phase <- Phase.Offering
                send (EntryWire.bytesOf (EntryWire.answer EntryWire.Answer.Admitted))
                Async.StartImmediate (
                    config.Record (
                        SessionEvent.SandboxWayOffered
                            { SandboxWayOffered.WayTo = sandbox
                              SandboxWayOffered.WayVia = label
                              SandboxWayOffered.WayAddress = said }))
                if callerClosed then withdraw ()

    let entered (hello: EntryWire.Hello) (rest: byte []) : unit =
        match request hello with
        | Error reason -> refuse reason
        | Ok (sandbox, exec) ->
            if openEntries.Value >= config.Limit then
                refuse (
                    sprintf
                        "This session has %d open entries, which is its limit. Close one, then try again."
                        config.Limit)
            else
                counted <- true
                openEntries.Value <- openEntries.Value + 1
                // Bytes that came with the first line are the start of the first frame.
                keep rest
                start hello sandbox exec

    let greeted (text: string) (rest: byte []) : unit =
        phase <- Phase.Starting
        match EntryWire.readHello text with
        | Error reason -> refuse reason
        | Ok hello ->
            match hello.Offer with
            | Some address -> offered hello address
            | None -> entered hello rest

    // An error on a connection that nobody listens for ends the process. The close that
    // follows it does the work.
    connection.onError ignore
    onceConnectionClosed connection (fun () ->
        callerClosed <- true
        match phase with
        | Phase.Running handle ->
            phase <- Phase.Over
            handle.Kill ()
        // `start` ends the command when it has one, because the caller is gone.
        | Phase.Starting -> ()
        | Phase.Offering ->
            phase <- Phase.Over
            withdraw ()
        | Phase.Greeting _
        | Phase.Over -> phase <- Phase.Over)
    let greet (soFar: byte []) (chunk: byte []) : unit =
        match EntryWire.line soFar chunk with
        | EntryWire.Line.Partial more -> phase <- Phase.Greeting more
        | EntryWire.Line.TooLong -> refuse "The first line is too long. The caller does not speak the entry wire."
        | EntryWire.Line.Whole (text, rest) -> greeted text rest
    Readables.bytes (connection :> Readable) (fun chunk ->
        match phase with
        | Phase.Greeting soFar -> greet soFar chunk
        | Phase.Starting -> keep chunk
        | Phase.Running handle -> takeWhileRunning handle chunk
        // A caller that offers a way sends nothing that the session reads.
        | Phase.Offering
        | Phase.Over -> ())

/// Remove a socket that a process of this session left, when one is there. A session has one
/// process, so a socket at this path is of a process that ended.
let private removeStale (path: string) : unit =
    try fs.unlinkSync (U2.Case1 path) with _ -> ()

/// Listen on the socket. The answer is the door, or the reason that the session has none.
///
/// The directory is the operator's. The session does not make it, because its owner and its
/// mode are the trust of the door.
let start (config: Config) : Async<Result<Door, string>> =
    Async.FromContinuations (fun (resolve, _, _) ->
        let openEntries = ref 0
        let mutable settled = false
        let ways : Ways = { Holders = System.Collections.Generic.Dictionary<string, int> (); LastHolder = 0 }
        let server = createNetServerServing (serve config openEntries ways)
        let close () : unit =
            try server.close () with _ -> ()
            removeStale config.SocketPath
        server.onError (fun error ->
            if not settled then
                settled <- true
                resolve (
                    Error (
                        sprintf
                            "The door of this session cannot listen at '%s': %s. YESSION_SESSION_DOORS names a directory that this session can write to."
                            config.SocketPath
                            (StreamError.describe error)
                    )
                ))
        server.onceListening (fun () ->
            if not settled then
                settled <- true
                // The owner and the group of the socket can dial it, and nobody else.
                try fs.chmodSync (U2.Case1 config.SocketPath, 0o660) with _ -> ()
                resolve (Ok { ListensAt = config.SocketPath; Close = close }))
        removeStale config.SocketPath
        server.listen config.SocketPath)

/// Close each entry and withdraw each way that the log holds open. A session calls this when it starts: the
/// process that held those entries is gone, and their commands went with it. An entry that
/// stays open in the log keeps the session in use for all time.
let closeLeftOpen (events: SessionEvent list) (record: SessionEvent -> Async<unit>) : Async<unit> =
    let rec closeEach (closing: SessionEvent list) : Async<unit> =
        async {
            match closing with
            | [] -> return ()
            | next :: rest ->
                do! record next
                return! closeEach rest
        }
    let entries =
        events
        |> List.fold (fun proj event -> EntryProjection.applyEvent proj System.DateTimeOffset.MinValue event) EntryProjection.empty
        |> EntryProjection.closingAll
    // A way goes with the process too. The deployment offers it again to the new process.
    let ways = events |> List.fold WayProjection.applyEvent WayProjection.empty |> WayProjection.withdrawingAll
    closeEach (entries @ ways)
