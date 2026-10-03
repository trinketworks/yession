namespace Yession.App.Codecs

open Yession.Domain
open Yession.Codecs

open Yession.Domain.Chat
open Yession.Domain.Sandboxes
open Yession.Domain.Files
open Yession.Domain.Artifacts
open Yession.Domain.Content
open Yession.Domain.Repos
open Yession.Domain.Prs
open System
open Yession.Domain.Agent
open Yession.Domain.Link
open Yession.Domain.Terminals
open Yession.Domain.Tools
open System.Globalization
#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

/// The session's events as they cross to the App: one event, its envelope, and a page of
/// them. The App reads the log (live over the data channel, and in pages over HTTP), so the
/// App owns how an event is written; the Session references this project to write its log in
/// the same shape it serves it. Identities stay in the domain's `Codec`, which every
/// surface shares.
[<RequireQualifiedAccess>]
module Events =

    let private sessionStarted : Codec<SessionStarted> =
        { Encode = fun (p: SessionStarted) -> Encode.object [ "messageId", Codec.messageId.Encode p.MessageId ]
          Decode =
            Decode.object (fun get ->
                { SessionStarted.MessageId = get.Required.Field "messageId" Codec.messageId.Decode }) }

    let private peerJoined : Codec<PeerJoined> =
        { Encode =
            fun (p: PeerJoined) ->
                Encode.object
                    [ "peerId", Codec.peerId.Encode p.PeerId
                      "displayName", Encode.string p.DisplayName
                      "user", Encode.option Codec.userId.Encode p.User ]
          Decode =
            Decode.object (fun get ->
                { PeerJoined.PeerId = get.Required.Field "peerId" Codec.peerId.Decode
                  PeerJoined.DisplayName = get.Required.Field "displayName" Decode.string
                  PeerJoined.User = get.Optional.Field "user" Codec.userId.Decode }) }

    let private peerLeft : Codec<PeerLeft> =
        { Encode = fun (p: PeerLeft) -> Encode.object [ "peerId", Codec.peerId.Encode p.PeerId ]
          Decode =
            Decode.object (fun get ->
                { PeerLeft.PeerId = get.Required.Field "peerId" Codec.peerId.Decode }) }

    let private messageSent : Codec<MessageSent> =
        { Encode =
            fun (p: MessageSent) ->
                Encode.object
                    [ "messageId", Codec.messageId.Encode p.MessageId
                      "queueId", Encode.option Codec.queueId.Encode p.QueueId
                      "author", Codec.principal.Encode p.Author
                      "body", Encode.string p.Body ]
          Decode =
            Decode.object (fun get ->
                { MessageSent.MessageId = get.Required.Field "messageId" Codec.messageId.Decode
                  MessageSent.QueueId = get.Optional.Field "queueId" Codec.queueId.Decode
                  MessageSent.Author = get.Required.Field "author" Codec.principal.Decode
                  MessageSent.Body = get.Required.Field "body" Decode.string }) }

    let private namingSubject : Codec<NamingSubject> =
        { Encode =
            fun (subject: NamingSubject) ->
                match subject with
                | NamingSubject.Chapter id ->
                    Encode.object [ "kind", Encode.string "chapter"; "messageId", Codec.messageId.Encode id ]
                | NamingSubject.Title -> Encode.object [ "kind", Encode.string "title" ]
          Decode =
            Decode.field "kind" Decode.string
            |> Decode.andThen (fun kind ->
                match kind with
                | "chapter" -> Decode.field "messageId" Codec.messageId.Decode |> Decode.map NamingSubject.Chapter
                | "title" -> Decode.succeed NamingSubject.Title
                | other -> Decode.fail (sprintf "Not a naming subject: %s" other)) }

    let private sessionResumed : Codec<SessionResumed> =
        { Encode =
            fun (p: SessionResumed) ->
                Encode.object [ "messageId", Codec.messageId.Encode p.MessageId; "lastHeardAt", Codec.timestamp.Encode p.LastHeardAt ]
          Decode =
            Decode.object (fun get ->
                { SessionResumed.MessageId = get.Required.Field "messageId" Codec.messageId.Decode
                  SessionResumed.LastHeardAt = get.Required.Field "lastHeardAt" Codec.timestamp.Decode }) }

    let private sessionNamed : Codec<SessionNamed> =
        { Encode =
            fun (p: SessionNamed) ->
                Encode.object
                    [ "subject", namingSubject.Encode p.Subject
                      "name", Encode.string p.Name
                      "read", Encode.int p.Read
                      "onBehalfOf", Encode.option Codec.principal.Encode p.OnBehalfOf ]
          Decode =
            Decode.object (fun get ->
                { SessionNamed.Subject = get.Required.Field "subject" namingSubject.Decode
                  SessionNamed.Name = get.Required.Field "name" Decode.string
                  SessionNamed.Read = get.Required.Field "read" Decode.int
                  SessionNamed.OnBehalfOf = get.Optional.Field "onBehalfOf" Codec.principal.Decode }) }

    /// Why a turn ran with nobody speaking (Plan 20, stage 2). A tagged object rather than a
    /// bare string, because the reasons are a vocabulary that grows — a roster change, a
    /// stream ending — and each may come to carry what it is about.
    let private wakeReason : Codec<WakeReason> =
        { Encode =
            (fun r ->
                match r with
                | CommandFinished -> Encode.object [ "kind", Encode.string "commandFinished" ]
                | StreamEnded id ->
                    Encode.object [ "kind", Encode.string "streamEnded"; "terminalId", Codec.terminalId.Encode id ]
                | IntegrationLost id ->
                    Encode.object [ "kind", Encode.string "integrationLost"; "terminalId", Codec.terminalId.Encode id ]
                | PrChanged pr -> Encode.object [ "kind", Encode.string "prChanged"; "pr", Codec.prRef.Encode pr ]
                | CutOff turn -> Encode.object [ "kind", Encode.string "cutOff"; "agentTurnId", Codec.agentTurnId.Encode turn ])
          Decode =
            Decode.field "kind" Decode.string
            |> Decode.andThen (function
                | "commandFinished" -> Decode.succeed CommandFinished
                | "streamEnded" -> Decode.field "terminalId" Codec.terminalId.Decode |> Decode.map StreamEnded
                | "integrationLost" -> Decode.field "terminalId" Codec.terminalId.Decode |> Decode.map IntegrationLost
                | "prChanged" -> Decode.field "pr" Codec.prRef.Decode |> Decode.map PrChanged
                | "cutOff" -> Decode.field "agentTurnId" Codec.agentTurnId.Decode |> Decode.map CutOff
                | other -> Decode.fail (sprintf "Unknown wake reason: %s" other)) }

    let private agentTurnStarted : Codec<AgentTurnStarted> =
        { Encode =
            fun (p: AgentTurnStarted) ->
                // The sum is in-memory; the WIRE stays the two optional keys it always was,
                // exactly one populated. This reads every log ever written (pre-Plan-20 turns
                // carry only `triggeredByMessageId`; woken turns carry `woke`) without a
                // second format to migrate — the collapse is a fact about the domain, not the
                // bytes.
                let triggeredBy, woke =
                    match p.Cause with
                    | TriggeredBy m -> Some m, None
                    | Woke r -> None, Some r
                Encode.object
                    [ "agentTurnId", Codec.agentTurnId.Encode p.AgentTurnId
                      "triggeredByMessageId", Encode.option Codec.messageId.Encode triggeredBy
                      "woke", Encode.option wakeReason.Encode woke ]
          Decode =
            Decode.object (fun get ->
                // `woke` wins where present; otherwise the trigger message is Required, so a
                // line carrying neither cause fails to decode rather than yielding a turn with
                // none — which is the whole point of the sum, held at the wire boundary too.
                let cause =
                    match get.Optional.Field "woke" (Decode.option wakeReason.Decode) |> Option.flatten with
                    | Some reason -> Woke reason
                    | None -> TriggeredBy (get.Required.Field "triggeredByMessageId" Codec.messageId.Decode)
                { AgentTurnStarted.AgentTurnId = get.Required.Field "agentTurnId" Codec.agentTurnId.Decode
                  AgentTurnStarted.Cause = cause }) }

    let private agentContextBuilt : Codec<AgentContextBuilt> =
        { Encode =
            fun (p: AgentContextBuilt) ->
                Encode.object
                    [ "agentTurnId", Codec.agentTurnId.Encode p.AgentTurnId
                      "messageCount", Encode.int p.MessageCount ]
          Decode =
            Decode.object (fun get ->
                { AgentContextBuilt.AgentTurnId = get.Required.Field "agentTurnId" Codec.agentTurnId.Decode
                  AgentContextBuilt.MessageCount = get.Required.Field "messageCount" Decode.int }) }

    let private agentMessageStarted : Codec<AgentMessageStarted> =
        { Encode =
            fun (p: AgentMessageStarted) ->
                Encode.object
                    [ "agentTurnId", Codec.agentTurnId.Encode p.AgentTurnId
                      "messageId", Codec.messageId.Encode p.MessageId
                      "antecedent", Encode.option Codec.messageId.Encode p.Antecedent ]
          Decode =
            Decode.object (fun get ->
                { AgentMessageStarted.AgentTurnId = get.Required.Field "agentTurnId" Codec.agentTurnId.Decode
                  AgentMessageStarted.MessageId = get.Required.Field "messageId" Codec.messageId.Decode
                  // Optional on the way in: every message started before this key existed
                  // was its turn's only one, which is exactly what `None` says.
                  AgentMessageStarted.Antecedent = get.Optional.Field "antecedent" Codec.messageId.Decode }) }

    let private agentMessageDelta : Codec<AgentMessageDelta> =
        { Encode =
            fun (p: AgentMessageDelta) ->
                Encode.object
                    [ "agentTurnId", Codec.agentTurnId.Encode p.AgentTurnId
                      "messageId", Codec.messageId.Encode p.MessageId
                      "delta", Encode.string p.Delta ]
          Decode =
            Decode.object (fun get ->
                { AgentMessageDelta.AgentTurnId = get.Required.Field "agentTurnId" Codec.agentTurnId.Decode
                  AgentMessageDelta.MessageId = get.Required.Field "messageId" Codec.messageId.Decode
                  AgentMessageDelta.Delta = get.Required.Field "delta" Decode.string }) }

    let private agentThought : Codec<AgentThought> =
        { Encode =
            fun (p: AgentThought) ->
                Encode.object
                    [ "agentTurnId", Codec.agentTurnId.Encode p.AgentTurnId
                      "thought", Encode.string p.Thought ]
          Decode =
            Decode.object (fun get ->
                { AgentThought.AgentTurnId = get.Required.Field "agentTurnId" Codec.agentTurnId.Decode
                  AgentThought.Thought = get.Required.Field "thought" Decode.string }) }

    let private agentMessageCompleted : Codec<AgentMessageCompleted> =
        { Encode =
            fun (p: AgentMessageCompleted) ->
                Encode.object
                    [ "agentTurnId", Codec.agentTurnId.Encode p.AgentTurnId
                      "messageId", Codec.messageId.Encode p.MessageId
                      "body", Encode.string p.Body ]
          Decode =
            Decode.object (fun get ->
                { AgentMessageCompleted.AgentTurnId = get.Required.Field "agentTurnId" Codec.agentTurnId.Decode
                  AgentMessageCompleted.MessageId = get.Required.Field "messageId" Codec.messageId.Decode
                  AgentMessageCompleted.Body = get.Required.Field "body" Decode.string }) }

    let private agentTurnFailed : Codec<AgentTurnFailed> =
        { Encode =
            fun (p: AgentTurnFailed) ->
                Encode.object (
                    [ "agentTurnId", Codec.agentTurnId.Encode p.AgentTurnId
                      "reason", Encode.string p.Reason ]
                    @ (match p.ProcessEnded with
                       | Some ended -> [ "processEnded", Encode.object [ "lastHeardAt", Codec.timestamp.Encode ended.LastHeardAt ] ]
                       | None -> []))
          Decode =
            Decode.object (fun get ->
                { AgentTurnFailed.AgentTurnId = get.Required.Field "agentTurnId" Codec.agentTurnId.Decode
                  AgentTurnFailed.Reason = get.Required.Field "reason" Decode.string
                  // Optional on the wire: every failure written before this carries none, and
                  // reads as one the turn came to on its own.
                  AgentTurnFailed.ProcessEnded =
                    get.Optional.Field "processEnded" (Decode.object (fun p -> { LastHeardAt = p.Required.Field "lastHeardAt" Codec.timestamp.Decode })) }) }

    let private agentTurnInterrupted : Codec<AgentTurnInterrupted> =
        { Encode =
            fun (p: AgentTurnInterrupted) ->
                Encode.object
                    [ "agentTurnId", Codec.agentTurnId.Encode p.AgentTurnId
                      "requestedBy", Codec.peerId.Encode p.RequestedBy ]
          Decode =
            Decode.object (fun get ->
                { AgentTurnInterrupted.AgentTurnId = get.Required.Field "agentTurnId" Codec.agentTurnId.Decode
                  AgentTurnInterrupted.RequestedBy = get.Required.Field "requestedBy" Codec.peerId.Decode }) }

    let private environmentNeedIdentified : Codec<EnvironmentNeedIdentified> =
        { Encode =
            fun (p: EnvironmentNeedIdentified) ->
                Encode.object
                    [ "reason", Encode.string p.Reason
                      "agentTurnId", Encode.option Codec.agentTurnId.Encode p.AgentTurnId ]
          Decode =
            Decode.object (fun get ->
                { EnvironmentNeedIdentified.Reason = get.Required.Field "reason" Decode.string
                  EnvironmentNeedIdentified.AgentTurnId = get.Required.Field "agentTurnId" (Decode.option Codec.agentTurnId.Decode) }) }

    let private environmentStartRequested : Codec<EnvironmentStartRequested> =
        { Encode =
            fun (p: EnvironmentStartRequested) ->
                Encode.object
                    [ "environmentId", Encode.string p.EnvironmentId
                      "specSummary", Encode.string p.SpecSummary ]
          Decode =
            Decode.object (fun get ->
                { EnvironmentStartRequested.EnvironmentId = get.Required.Field "environmentId" Decode.string
                  EnvironmentStartRequested.SpecSummary = get.Required.Field "specSummary" Decode.string }) }

    let private environmentStarted : Codec<EnvironmentStarted> =
        { Encode =
            fun (p: EnvironmentStarted) ->
                Encode.object
                    [ "environmentId", Encode.string p.EnvironmentId
                      "containerRef", Encode.string p.ContainerRef ]
          Decode =
            Decode.object (fun get ->
                { EnvironmentStarted.EnvironmentId = get.Required.Field "environmentId" Decode.string
                  EnvironmentStarted.ContainerRef = get.Required.Field "containerRef" Decode.string }) }

    let private environmentStartFailed : Codec<EnvironmentStartFailed> =
        { Encode =
            fun (p: EnvironmentStartFailed) ->
                Encode.object
                    [ "environmentId", Encode.string p.EnvironmentId
                      "reason", Encode.string p.Reason ]
          Decode =
            Decode.object (fun get ->
                { EnvironmentStartFailed.EnvironmentId = get.Required.Field "environmentId" Decode.string
                  EnvironmentStartFailed.Reason = get.Required.Field "reason" Decode.string }) }

    let private environmentStopRequested : Codec<EnvironmentStopRequested> =
        { Encode =
            fun (p: EnvironmentStopRequested) ->
                Encode.object [ "environmentId", Encode.string p.EnvironmentId ]
          Decode =
            Decode.object (fun get ->
                { EnvironmentStopRequested.EnvironmentId = get.Required.Field "environmentId" Decode.string }) }

    let private environmentStopped : Codec<EnvironmentStopped> =
        { Encode =
            fun (p: EnvironmentStopped) ->
                Encode.object [ "environmentId", Encode.string p.EnvironmentId ]
          Decode =
            Decode.object (fun get ->
                { EnvironmentStopped.EnvironmentId = get.Required.Field "environmentId" Decode.string }) }

    let outputStream : Codec<OutputStream> =
        { Encode =
            (fun s -> Encode.string (match s with Stdout -> "stdout" | Stderr -> "stderr"))
          Decode =
            Decode.string
            |> Decode.andThen (function
                | "stdout" -> Decode.succeed Stdout
                | "stderr" -> Decode.succeed Stderr
                | other -> Decode.fail (sprintf "Unknown output stream: %s" other)) }

    let commandResult : Codec<CommandResult> =
        { Encode =
            (fun r ->
                match r with
                | CommandSucceeded code -> Encode.object [ "kind", Encode.string "succeeded"; "exitCode", Encode.int code ]
                | CommandFailed code -> Encode.object [ "kind", Encode.string "failed"; "exitCode", Encode.int code ]
                | CommandTimedOut -> Encode.object [ "kind", Encode.string "timedOut" ]
                | CommandExecutionFailed reason ->
                    Encode.object [ "kind", Encode.string "executionFailed"; "reason", Encode.string reason ])
          Decode =
            Decode.field "kind" Decode.string
            |> Decode.andThen (function
                | "succeeded" -> Decode.field "exitCode" Decode.int |> Decode.map CommandSucceeded
                | "failed" -> Decode.field "exitCode" Decode.int |> Decode.map CommandFailed
                | "timedOut" -> Decode.succeed CommandTimedOut
                | "executionFailed" -> Decode.field "reason" Decode.string |> Decode.map CommandExecutionFailed
                | other -> Decode.fail (sprintf "Unknown command result: %s" other)) }

    let private commandRequested : Codec<CommandRequested> =
        { Encode =
            fun (p: CommandRequested) ->
                Encode.object
                    [ "commandId", Codec.commandId.Encode p.CommandId
                      "executable", Encode.string p.Executable
                      "arguments", p.Arguments |> List.map Encode.string |> Encode.list ]
          Decode =
            Decode.object (fun get ->
                { CommandRequested.CommandId = get.Required.Field "commandId" Codec.commandId.Decode
                  CommandRequested.Executable = get.Required.Field "executable" Decode.string
                  CommandRequested.Arguments = get.Required.Field "arguments" (Decode.list Decode.string) }) }

    let private commandStarted : Codec<CommandStarted> =
        { Encode = fun (p: CommandStarted) -> Encode.object [ "commandId", Codec.commandId.Encode p.CommandId ]
          Decode =
            Decode.object (fun get ->
                { CommandStarted.CommandId = get.Required.Field "commandId" Codec.commandId.Decode }) }

    let private commandOutputReceived : Codec<CommandOutputReceived> =
        { Encode =
            fun (p: CommandOutputReceived) ->
                Encode.object
                    [ "commandId", Codec.commandId.Encode p.CommandId
                      "stream", outputStream.Encode p.Stream
                      "text", Encode.string p.Text ]
          Decode =
            Decode.object (fun get ->
                { CommandOutputReceived.CommandId = get.Required.Field "commandId" Codec.commandId.Decode
                  CommandOutputReceived.Stream = get.Required.Field "stream" outputStream.Decode
                  CommandOutputReceived.Text = get.Required.Field "text" Decode.string }) }

    let private commandCompleted : Codec<CommandCompleted> =
        { Encode =
            fun (p: CommandCompleted) ->
                Encode.object
                    [ "commandId", Codec.commandId.Encode p.CommandId
                      "result", commandResult.Encode p.Result ]
          Decode =
            Decode.object (fun get ->
                { CommandCompleted.CommandId = get.Required.Field "commandId" Codec.commandId.Decode
                  CommandCompleted.Result = get.Required.Field "result" commandResult.Decode }) }

    let mcpServerNoted : Codec<McpServerNoted> =
        { Encode =
            fun (p: McpServerNoted) ->
                Encode.object
                    [ "messageId", Codec.messageId.Encode p.MessageId; "name", Codec.mcpServerName.Encode p.Name ]
          Decode =
            Decode.object (fun get ->
                { McpServerNoted.MessageId = get.Required.Field "messageId" Codec.messageId.Decode
                  McpServerNoted.Name = get.Required.Field "name" Codec.mcpServerName.Decode }) }

    let private terminalOpened : Codec<TerminalOpened> =
        { Encode =
            fun (p: TerminalOpened) ->
                Encode.object
                    [ "terminalId", Codec.terminalId.Encode p.TerminalId
                      "openedBy", Codec.actor.Encode p.OpenedBy
                      "title", Codec.terminalTitle.Encode p.Title
                      "sandbox", Encode.option Codec.sandboxRef.Encode p.Sandbox
                      "renewable", Encode.bool p.Renewable ]
          Decode =
            Decode.object (fun get ->
                // Three states in one field, and they are three different facts, so the
                // key's PRESENCE is read rather than just its value: absent is a log
                // written before named sandboxes (those terminals ran in the one sandbox
                // the session had, so `default` is where they were, not a guess), null is
                // an attached source that runs in no sandbox at all, and a name is a name.
                let written = get.Required.Raw Decode.keys |> List.contains "sandbox"
                { TerminalOpened.TerminalId = get.Required.Field "terminalId" Codec.terminalId.Decode
                  TerminalOpened.OpenedBy = get.Required.Field "openedBy" Codec.actor.Decode
                  TerminalOpened.Title = get.Required.Field "title" Codec.terminalTitle.Decode
                  // Absent in a log written before Plan 19, and false is the honest reading:
                  // nothing recorded a way back, so there is not one.
                  TerminalOpened.Renewable =
                    get.Optional.Field "renewable" Decode.bool |> Option.defaultValue false
                  TerminalOpened.Sandbox =
                    if written then get.Optional.Field "sandbox" Codec.sandboxRef.Decode
                    else Some SandboxRef.defaultRef }) }

    let private terminalClosed : Codec<TerminalClosed> =
        { Encode =
            fun (p: TerminalClosed) ->
                Encode.object
                    [ "terminalId", Codec.terminalId.Encode p.TerminalId
                      "reason", Encode.string p.Reason ]
          Decode =
            Decode.object (fun get ->
                { TerminalClosed.TerminalId = get.Required.Field "terminalId" Codec.terminalId.Decode
                  TerminalClosed.Reason = get.Required.Field "reason" Decode.string }) }

    let private terminalBlockStarted : Codec<TerminalBlockStarted> =
        { Encode =
            fun (p: TerminalBlockStarted) ->
                Encode.object (
                    [ "terminalId", Codec.terminalId.Encode p.TerminalId
                      "blockId", Codec.blockId.Encode p.BlockId
                      "queueId", Encode.option Codec.queueId.Encode p.QueueId
                      "command", Encode.string p.Command
                      "fromSeq", Encode.int p.FromSeq
                      "background", Encode.bool p.Background ]
                    @ Codec.authorityFields p.Authority)
          Decode =
            Decode.object (fun get ->
                { TerminalBlockStarted.TerminalId = get.Required.Field "terminalId" Codec.terminalId.Decode
                  TerminalBlockStarted.BlockId = get.Required.Field "blockId" Codec.blockId.Decode
                  TerminalBlockStarted.QueueId = get.Required.Field "queueId" (Decode.option Codec.queueId.Decode)
                  TerminalBlockStarted.Authority = get.Required.Raw Codec.authorityOf
                  TerminalBlockStarted.Command = get.Required.Field "command" Decode.string
                  TerminalBlockStarted.FromSeq = get.Required.Field "fromSeq" Decode.int
                  // Optional on the way IN and required on the way out: every block written
                  // before Plan 20 ran in the foreground, and an event log is read back for
                  // the life of its session. A `Required` field here would make those pages
                  // undecodable — which is a session that will not open, to record a bool
                  // whose absence already means `false`.
                  TerminalBlockStarted.Background =
                    get.Optional.Field "background" Decode.bool |> Option.defaultValue false }) }

    let private terminalBlockCompleted : Codec<TerminalBlockCompleted> =
        { Encode =
            fun (p: TerminalBlockCompleted) ->
                Encode.object
                    [ "terminalId", Codec.terminalId.Encode p.TerminalId
                      "blockId", Codec.blockId.Encode p.BlockId
                      "result", commandResult.Encode p.Result
                      "toSeq", Encode.int p.ToSeq ]
          Decode =
            Decode.object (fun get ->
                { TerminalBlockCompleted.TerminalId = get.Required.Field "terminalId" Codec.terminalId.Decode
                  TerminalBlockCompleted.BlockId = get.Required.Field "blockId" Codec.blockId.Decode
                  TerminalBlockCompleted.Result = get.Required.Field "result" commandResult.Decode
                  TerminalBlockCompleted.ToSeq = get.Required.Field "toSeq" Decode.int }) }

    let private leaseEnd : Codec<TerminalLeaseEnd> =
        { Encode =
            (fun e ->
                match e with
                | LeaseReleased -> Encode.object [ "kind", Encode.string "released" ]
                | LeaseStolen by -> Encode.object [ "kind", Encode.string "stolen"; "by", Codec.actor.Encode by ]
                | LeaseHolderGone -> Encode.object [ "kind", Encode.string "holderGone" ]
                | LeaseIdle -> Encode.object [ "kind", Encode.string "idle" ])
          Decode =
            Decode.field "kind" Decode.string
            |> Decode.andThen (function
                | "released" -> Decode.succeed LeaseReleased
                | "stolen" -> Decode.field "by" Codec.actor.Decode |> Decode.map LeaseStolen
                | "holderGone" -> Decode.succeed LeaseHolderGone
                | "idle" -> Decode.succeed LeaseIdle
                | other -> Decode.fail (sprintf "Unknown lease end: %s" other)) }

    let private lostEvidence : Codec<LostEvidence> =
        { Encode =
            fun (e: LostEvidence) ->
                Encode.object
                    [ "writtenAt", Codec.timestamp.Encode e.WrittenAt
                      "due", Codec.timestamp.Encode e.Due
                      "said", Encode.string e.Said ]
          Decode =
            Decode.object (fun get ->
                { LostEvidence.WrittenAt = get.Required.Field "writtenAt" Codec.timestamp.Decode
                  LostEvidence.Due = get.Required.Field "due" Codec.timestamp.Decode
                  LostEvidence.Said = get.Required.Field "said" Decode.string }) }

    let private terminalIntegrationLost : Codec<TerminalIntegrationLost> =
        { Encode =
            fun (p: TerminalIntegrationLost) ->
                Encode.object
                    [ "terminalId", Codec.terminalId.Encode p.TerminalId
                      "blockId", Encode.option Codec.blockId.Encode p.BlockId
                      "evidence", Encode.option lostEvidence.Encode p.Evidence ]
          Decode =
            Decode.object (fun get ->
                { TerminalIntegrationLost.TerminalId = get.Required.Field "terminalId" Codec.terminalId.Decode
                  TerminalIntegrationLost.BlockId = get.Optional.Field "blockId" Codec.blockId.Decode
                  // Optional on the wire because a log written before the detector said
                  // what it saw has no evidence to read, not because a new one may omit it.
                  TerminalIntegrationLost.Evidence = get.Optional.Field "evidence" lostEvidence.Decode }) }

    let private terminalMarkedLate : Codec<TerminalMarkedLate> =
        { Encode =
            fun (p: TerminalMarkedLate) ->
                Encode.object
                    [ "terminalId", Codec.terminalId.Encode p.TerminalId
                      "blockId", Codec.blockId.Encode p.BlockId
                      "writtenAt", Codec.timestamp.Encode p.WrittenAt ]
          Decode =
            Decode.object (fun get ->
                { TerminalMarkedLate.TerminalId = get.Required.Field "terminalId" Codec.terminalId.Decode
                  TerminalMarkedLate.BlockId = get.Required.Field "blockId" Codec.blockId.Decode
                  TerminalMarkedLate.WrittenAt = get.Required.Field "writtenAt" Codec.timestamp.Decode }) }

    let private terminalIntegrationRestored : Codec<TerminalIntegrationRestored> =
        { Encode = fun (p: TerminalIntegrationRestored) -> Encode.object [ "terminalId", Codec.terminalId.Encode p.TerminalId ]
          Decode =
            Decode.object (fun get ->
                { TerminalIntegrationRestored.TerminalId = get.Required.Field "terminalId" Codec.terminalId.Decode }) }

    let private terminalLeaseTaken : Codec<TerminalLeaseTaken> =
        { Encode =
            fun (p: TerminalLeaseTaken) ->
                Encode.object
                    [ "terminalId", Codec.terminalId.Encode p.TerminalId
                      "by", Codec.actor.Encode p.By
                      "fromSeq", Encode.int p.FromSeq ]
          Decode =
            Decode.object (fun get ->
                { TerminalLeaseTaken.TerminalId = get.Required.Field "terminalId" Codec.terminalId.Decode
                  TerminalLeaseTaken.By = get.Required.Field "by" Codec.actor.Decode
                  TerminalLeaseTaken.FromSeq = Codec.leaseSeq get "fromSeq" }) }

    let private terminalLeaseReleased : Codec<TerminalLeaseReleased> =
        { Encode =
            fun (p: TerminalLeaseReleased) ->
                Encode.object
                    [ "terminalId", Codec.terminalId.Encode p.TerminalId
                      "was", Codec.actor.Encode p.Was
                      "reason", leaseEnd.Encode p.Reason
                      "toSeq", Encode.int p.ToSeq ]
          Decode =
            Decode.object (fun get ->
                { TerminalLeaseReleased.TerminalId = get.Required.Field "terminalId" Codec.terminalId.Decode
                  TerminalLeaseReleased.Was = get.Required.Field "was" Codec.actor.Decode
                  TerminalLeaseReleased.Reason = get.Required.Field "reason" leaseEnd.Decode
                  TerminalLeaseReleased.ToSeq = Codec.leaseSeq get "toSeq" }) }

    let private terminalCommandRejected : Codec<TerminalCommandRejected> =
        { Encode =
            fun (p: TerminalCommandRejected) ->
                Encode.object (
                    [ "terminalId", Codec.terminalId.Encode p.TerminalId
                      "queueId", Codec.queueId.Encode p.QueueId
                      "blockId", Codec.blockId.Encode p.BlockId
                      "rejectedBy", Codec.actor.Encode p.RejectedBy
                      "command", Encode.string p.Command
                      "reason", Encode.option Encode.string p.Reason ]
                    @ Codec.authorityFields p.Authority)
          Decode =
            Decode.object (fun get ->
                { TerminalCommandRejected.TerminalId = get.Required.Field "terminalId" Codec.terminalId.Decode
                  TerminalCommandRejected.QueueId = get.Required.Field "queueId" Codec.queueId.Decode
                  TerminalCommandRejected.BlockId = get.Required.Field "blockId" Codec.blockId.Decode
                  TerminalCommandRejected.Authority = get.Required.Raw Codec.authorityOf
                  TerminalCommandRejected.RejectedBy = get.Required.Field "rejectedBy" Codec.actor.Decode
                  TerminalCommandRejected.Command = get.Required.Field "command" Decode.string
                  TerminalCommandRejected.Reason = get.Required.Field "reason" (Decode.option Decode.string) }) }

    let private terminalTranscriptTruncated : Codec<TerminalTranscriptTruncated> =
        { Encode =
            fun (p: TerminalTranscriptTruncated) ->
                Encode.object
                    [ "terminalId", Codec.terminalId.Encode p.TerminalId
                      "blockId", Encode.option Codec.blockId.Encode p.BlockId
                      "droppedBytes", Encode.int p.DroppedBytes ]
          Decode =
            Decode.object (fun get ->
                { TerminalTranscriptTruncated.TerminalId = get.Required.Field "terminalId" Codec.terminalId.Decode
                  TerminalTranscriptTruncated.BlockId = get.Required.Field "blockId" (Decode.option Codec.blockId.Decode)
                  TerminalTranscriptTruncated.DroppedBytes = get.Required.Field "droppedBytes" Decode.int }) }

    let private repoAdded : Codec<RepoAdded> =
        { Encode =
            fun (p: RepoAdded) ->
                Encode.object
                    [ "messageId", Codec.messageId.Encode p.MessageId
                      "repo", Codec.repoRef.Encode p.Repo
                      "branch", Encode.string p.Branch
                      "actor", Codec.actor.Encode p.Actor
                      "agentsMd", Encode.option Encode.string p.AgentsMd ]
          Decode =
            Decode.object (fun get ->
                { RepoAdded.MessageId = get.Required.Field "messageId" Codec.messageId.Decode
                  RepoAdded.Repo = get.Required.Field "repo" Codec.repoRef.Decode
                  RepoAdded.Branch = get.Required.Field "branch" Decode.string
                  RepoAdded.Actor = get.Required.Field "actor" Codec.actor.Decode
                  RepoAdded.AgentsMd = get.Optional.Field "agentsMd" Decode.string }) }

    let private repoRemoved : Codec<RepoRemoved> =
        { Encode =
            fun (p: RepoRemoved) ->
                Encode.object
                    [ "messageId", Codec.messageId.Encode p.MessageId
                      "repo", Codec.repoRef.Encode p.Repo
                      "actor", Codec.actor.Encode p.Actor ]
          Decode =
            Decode.object (fun get ->
                { RepoRemoved.MessageId = get.Required.Field "messageId" Codec.messageId.Decode
                  RepoRemoved.Repo = get.Required.Field "repo" Codec.repoRef.Decode
                  RepoRemoved.Actor = get.Required.Field "actor" Codec.actor.Decode }) }

    let private repoBranchSwitched : Codec<RepoBranchSwitched> =
        { Encode =
            fun (p: RepoBranchSwitched) ->
                Encode.object
                    [ "messageId", Codec.messageId.Encode p.MessageId
                      "repo", Codec.repoRef.Encode p.Repo
                      "branch", Encode.string p.Branch
                      "created", Encode.bool p.Created
                      "actor", Codec.actor.Encode p.Actor ]
          Decode =
            Decode.object (fun get ->
                { RepoBranchSwitched.MessageId = get.Required.Field "messageId" Codec.messageId.Decode
                  RepoBranchSwitched.Repo = get.Required.Field "repo" Codec.repoRef.Decode
                  RepoBranchSwitched.Branch = get.Required.Field "branch" Decode.string
                  RepoBranchSwitched.Created = get.Required.Field "created" Decode.bool
                  RepoBranchSwitched.Actor = get.Required.Field "actor" Codec.actor.Decode }) }

    let private prState : Codec<PrState> =
        { Encode = PrState.describe >> Encode.string
          Decode =
            Decode.string
            |> Decode.andThen (function
                | "open" -> Decode.succeed PrOpen
                | "merged" -> Decode.succeed PrMerged
                | "closed" -> Decode.succeed PrClosed
                | other -> Decode.fail (sprintf "Unknown pull request state: %s" other)) }

    let private checksRollup : Codec<ChecksRollup> =
        { Encode =
            (fun (rollup: ChecksRollup) ->
                match rollup with
                | ChecksNone -> Encode.string "none"
                | ChecksPending -> Encode.string "pending"
                | ChecksGreen -> Encode.string "green"
                | ChecksRed -> Encode.string "red")
          Decode =
            Decode.string
            |> Decode.andThen (function
                | "none" -> Decode.succeed ChecksNone
                | "pending" -> Decode.succeed ChecksPending
                | "green" -> Decode.succeed ChecksGreen
                | "red" -> Decode.succeed ChecksRed
                | other -> Decode.fail (sprintf "Unknown checks rollup: %s" other)) }

    let private gitHubQueueState : Codec<GitHubQueueState> =
        { Encode =
            (fun (state: GitHubQueueState) ->
                match state with
                | GitHubQueueState.Queued -> Encode.string "queued"
                | GitHubQueueState.AwaitingChecks -> Encode.string "awaitingChecks"
                | GitHubQueueState.Mergeable -> Encode.string "mergeable"
                | GitHubQueueState.Unmergeable -> Encode.string "unmergeable"
                | GitHubQueueState.Locked -> Encode.string "locked")
          Decode =
            Decode.string
            |> Decode.andThen (function
                | "queued" -> Decode.succeed GitHubQueueState.Queued
                | "awaitingChecks" -> Decode.succeed GitHubQueueState.AwaitingChecks
                | "mergeable" -> Decode.succeed GitHubQueueState.Mergeable
                | "unmergeable" -> Decode.succeed GitHubQueueState.Unmergeable
                | "locked" -> Decode.succeed GitHubQueueState.Locked
                | other -> Decode.fail (sprintf "Unknown merge queue state: %s" other)) }

    let private prRoute : Codec<PrRoute> =
        { Encode =
            (fun (route: PrRoute) ->
                match route with
                | PrRoute.GitHubAutoMerge -> Encode.object [ "kind", Encode.string "githubAutoMerge" ]
                | PrRoute.GitHubMergeQueue (position, state) ->
                    Encode.object
                        [ "kind", Encode.string "githubMergeQueue"
                          "position", Encode.int position
                          "state", gitHubQueueState.Encode state ])
          Decode =
            Decode.field "kind" Decode.string
            |> Decode.andThen (function
                | "githubAutoMerge" -> Decode.succeed PrRoute.GitHubAutoMerge
                | "githubMergeQueue" ->
                    Decode.map2
                        (fun position state -> PrRoute.GitHubMergeQueue (position, state))
                        (Decode.field "position" Decode.int)
                        (Decode.field "state" gitHubQueueState.Decode)
                | other -> Decode.fail (sprintf "Unknown pull request route: %s" other)) }

    let private prReview : Codec<PrReview> =
        { Encode =
            (fun (review: PrReview) ->
                match review with
                | PrReview.Approved -> Encode.string "approved"
                | PrReview.ChangesRequested -> Encode.string "changesRequested"
                | PrReview.Required -> Encode.string "required")
          Decode =
            Decode.string
            |> Decode.andThen (function
                | "approved" -> Decode.succeed PrReview.Approved
                | "changesRequested" -> Decode.succeed PrReview.ChangesRequested
                | "required" -> Decode.succeed PrReview.Required
                | other -> Decode.fail (sprintf "Unknown review decision: %s" other)) }

    let private prSnapshot : Codec<PrSnapshot> =
        { Encode =
            fun (s: PrSnapshot) ->
                Encode.object
                    [ "state", prState.Encode s.State
                      "title", Encode.string s.Title
                      "headSha", Encode.string s.HeadSha
                      "checks", checksRollup.Encode s.Checks
                      "route", Encode.option prRoute.Encode s.Route
                      "mergeable", Encode.option Encode.bool s.Mergeable
                      "review", Encode.option prReview.Encode s.Review
                      "behind", Encode.bool s.Behind
                      "draft", Encode.bool s.Draft
                      "times",
                      Encode.object
                          [ "mergedAt", Encode.option Codec.timestamp.Encode s.Times.MergedAt
                            "closedAt", Encode.option Codec.timestamp.Encode s.Times.ClosedAt
                            "checksSettledAt", Encode.option Codec.timestamp.Encode s.Times.ChecksSettledAt ] ]
          Decode =
            Decode.object (fun get ->
                { PrSnapshot.State = get.Required.Field "state" prState.Decode
                  PrSnapshot.Title = get.Required.Field "title" Decode.string
                  PrSnapshot.HeadSha = get.Required.Field "headSha" Decode.string
                  PrSnapshot.Checks = get.Required.Field "checks" checksRollup.Decode
                  // A snapshot recorded before routes were read carries `queued`, which was
                  // GitHub's REST `auto_merge` and nothing else — so true is auto merge
                  // armed, and a watch begun on a queued pull request then reads as armed
                  // until its next look says which.
                  PrSnapshot.Route =
                      match get.Optional.Field "route" prRoute.Decode with
                      | Some route -> Some route
                      | None ->
                          if get.Optional.Field "queued" Decode.bool = Some true then Some PrRoute.GitHubAutoMerge
                          else None
                  PrSnapshot.Mergeable = get.Required.Field "mergeable" (Decode.option Decode.bool)
                  // Both optional, because a watch recorded before readiness was read has
                  // neither: no decision reported, and not known to be behind.
                  PrSnapshot.Review = get.Optional.Field "review" prReview.Decode
                  PrSnapshot.Behind = get.Optional.Field "behind" Decode.bool |> Option.defaultValue false
                  // Optional because a watch recorded before drafts were read has none: it
                  // reads as not a draft, so an undrafting it began over goes unannounced —
                  // the honest `Stalled` rule, since nobody watching saw it as a draft.
                  PrSnapshot.Draft = get.Optional.Field "draft" Decode.bool |> Option.defaultValue false
                  // Optional throughout: a baseline recorded before times were read has none,
                  // and they only ever date a change — they never decide one.
                  PrSnapshot.Times =
                      get.Optional.Field
                          "times"
                          (Decode.object (fun t ->
                              { MergedAt = t.Optional.Field "mergedAt" Codec.timestamp.Decode
                                ClosedAt = t.Optional.Field "closedAt" Codec.timestamp.Decode
                                ChecksSettledAt = t.Optional.Field "checksSettledAt" Codec.timestamp.Decode }))
                      |> Option.defaultValue PrTimes.none }) }

    let private prTransition : Codec<PrTransition> =
        { Encode =
            (fun (t: PrTransition) ->
                match t with
                | PrTransition.Merged -> Encode.string "merged"
                | PrTransition.Closed -> Encode.string "closed"
                | PrTransition.Reopened -> Encode.string "reopened"
                | PrTransition.ChecksPassed -> Encode.string "checksGreen"
                | PrTransition.ChecksFailed -> Encode.string "checksRed"
                | PrTransition.Armed -> Encode.string "armed"
                | PrTransition.Enqueued -> Encode.string "enqueued"
                | PrTransition.Stalled -> Encode.string "stalled"
                | PrTransition.Conflicted -> Encode.string "conflicted"
                | PrTransition.Resolved -> Encode.string "resolved"
                | PrTransition.ReadyForReview -> Encode.string "readyForReview"
                | PrTransition.Drafted -> Encode.string "drafted")
          Decode =
            Decode.string
            |> Decode.andThen (function
                | "merged" -> Decode.succeed PrTransition.Merged
                | "closed" -> Decode.succeed PrTransition.Closed
                | "reopened" -> Decode.succeed PrTransition.Reopened
                | "checksGreen" -> Decode.succeed PrTransition.ChecksPassed
                | "checksRed" -> Decode.succeed PrTransition.ChecksFailed
                | "armed" -> Decode.succeed PrTransition.Armed
                // What `armed` was written as before a merge queue was told apart from auto
                // merge: it was only ever GitHub's REST `auto_merge` arriving.
                | "queued" -> Decode.succeed PrTransition.Armed
                | "enqueued" -> Decode.succeed PrTransition.Enqueued
                | "stalled" -> Decode.succeed PrTransition.Stalled
                | "conflicted" -> Decode.succeed PrTransition.Conflicted
                | "resolved" -> Decode.succeed PrTransition.Resolved
                | "readyForReview" -> Decode.succeed PrTransition.ReadyForReview
                | "drafted" -> Decode.succeed PrTransition.Drafted
                | other -> Decode.fail (sprintf "Unknown pull request transition: %s" other)) }

    /// The watcher is not on the wire: it is derived from the authority by the one rule
    /// `PrWatched.create` applies, and a second key could only agree with it or contradict
    /// it. The parties ride the same two top-level keys a terminal block's do.
    let private prWatched : Codec<PrWatched> =
        { Encode =
            fun (p: PrWatched) ->
                Encode.object (
                    [ "messageId", Codec.messageId.Encode (PrWatched.messageId p)
                      "pr", Codec.prRef.Encode (PrWatched.pr p)
                      "initial", prSnapshot.Encode (PrWatched.initial p) ]
                    @ Codec.authorityFields (PrWatched.authority p))
          Decode =
            Decode.object (fun get ->
                get.Required.Field "messageId" Codec.messageId.Decode,
                get.Required.Raw Codec.authorityOf,
                get.Required.Field "pr" Codec.prRef.Decode,
                get.Required.Field "initial" prSnapshot.Decode)
            |> Decode.andThen (fun (messageId, authority, pr, initial) ->
                match PrWatched.create messageId authority pr initial with
                | Ok watched -> Decode.succeed watched
                | Error reason -> Decode.fail reason) }

    let private prUnwatched : Codec<PrUnwatched> =
        { Encode =
            fun (p: PrUnwatched) ->
                Encode.object
                    [ "messageId", Codec.messageId.Encode p.MessageId
                      "pr", Codec.prRef.Encode p.Pr
                      "actor", Codec.actor.Encode p.Actor ]
          Decode =
            Decode.object (fun get ->
                { PrUnwatched.MessageId = get.Required.Field "messageId" Codec.messageId.Decode
                  PrUnwatched.Pr = get.Required.Field "pr" Codec.prRef.Decode
                  PrUnwatched.Actor = get.Required.Field "actor" Codec.actor.Decode }) }

    let private prTransitioned : Codec<PrTransitioned> =
        { Encode =
            fun (p: PrTransitioned) ->
                Encode.object
                    [ "messageId", Codec.messageId.Encode p.MessageId
                      "pr", Codec.prRef.Encode p.Pr
                      "transition", prTransition.Encode p.Transition
                      "state", prState.Encode p.State
                      "checks", checksRollup.Encode p.Checks
                      "watcher", Codec.principal.Encode p.Watcher
                      "occurredAt", Encode.option Codec.timestamp.Encode p.OccurredAt ]
          Decode =
            Decode.object (fun get ->
                { PrTransitioned.MessageId = get.Required.Field "messageId" Codec.messageId.Decode
                  PrTransitioned.Pr = get.Required.Field "pr" Codec.prRef.Decode
                  PrTransitioned.Transition = get.Required.Field "transition" prTransition.Decode
                  PrTransitioned.State = get.Required.Field "state" prState.Decode
                  PrTransitioned.Checks = get.Required.Field "checks" checksRollup.Decode
                  PrTransitioned.Watcher = get.Required.Field "watcher" Codec.principal.Decode
                  // Absent on every change recorded before the source's time was read, which
                  // reads as not knowing it — never as having happened when it was written.
                  PrTransitioned.OccurredAt = get.Optional.Field "occurredAt" Codec.timestamp.Decode }) }

    let private prWatchReadability : Codec<PrWatchReadability> =
        { Encode =
            fun (p: PrWatchReadability) ->
                Encode.object
                    [ "messageId", Codec.messageId.Encode p.MessageId
                      "pr", Codec.prRef.Encode p.Pr
                      "watcher", Codec.principal.Encode p.Watcher
                      "unreadable", Encode.option Encode.string p.Unreadable ]
          Decode =
            Decode.object (fun get ->
                { PrWatchReadability.MessageId = get.Required.Field "messageId" Codec.messageId.Decode
                  PrWatchReadability.Pr = get.Required.Field "pr" Codec.prRef.Decode
                  PrWatchReadability.Watcher = get.Required.Field "watcher" Codec.principal.Decode
                  PrWatchReadability.Unreadable = get.Optional.Field "unreadable" Decode.string }) }

    let private sandboxSetupQueued : Codec<SandboxSetupQueued> =
        { Encode =
            fun (p: SandboxSetupQueued) ->
                Encode.object
                    [ "messageId", Codec.messageId.Encode p.MessageId
                      "sandbox", Codec.sandboxRef.Encode p.Sandbox
                      "command", Encode.string p.Command
                      "handle", Encode.option (QueueId.value >> Encode.string) p.Handle
                      "problem", Encode.option Encode.string p.Problem
                      "actor", Codec.actor.Encode p.Actor ]
          Decode =
            Decode.object (fun get ->
                { SandboxSetupQueued.MessageId = get.Required.Field "messageId" Codec.messageId.Decode
                  SandboxSetupQueued.Sandbox = get.Required.Field "sandbox" Codec.sandboxRef.Decode
                  SandboxSetupQueued.Command = get.Required.Field "command" Decode.string
                  SandboxSetupQueued.Handle =
                    get.Optional.Field "handle" (Decode.option Codec.queueId.Decode) |> Option.flatten
                  SandboxSetupQueued.Problem =
                    get.Optional.Field "problem" (Decode.option Decode.string) |> Option.flatten
                  SandboxSetupQueued.Actor = get.Required.Field "actor" Codec.actor.Decode }) }

    let private workSandboxStarted : Codec<WorkSandboxStarted> =
        { Encode =
            fun (p: WorkSandboxStarted) ->
                Encode.object
                    [ "messageId", Codec.messageId.Encode p.MessageId
                      "sandbox", Codec.sandboxRef.Encode p.Sandbox
                      "backend", Encode.string p.Backend
                      "description", Encode.option Encode.string p.Description
                      "checkout", Encode.option Encode.string p.Checkout
                      // Names only. There is no branch of this codec that can carry a
                      // credential VALUE, which is the point: the log is replicated to
                      // every peer, and a shape that could hold a token eventually does.
                      "forwarded", Encode.list (p.Forwarded |> List.map (ConnectionName.value >> Encode.string))
                      "realisation", Encode.list (p.Realisation |> List.map Encode.string)
                      "actor", Codec.actor.Encode p.Actor
                      "onBehalfOf", Encode.option Codec.principal.Encode p.OnBehalfOf
                      "causedBy", Encode.option Codec.cause.Encode p.CausedBy ]
          Decode =
            Decode.object (fun get ->
                { WorkSandboxStarted.MessageId = get.Required.Field "messageId" Codec.messageId.Decode
                  WorkSandboxStarted.Sandbox = get.Required.Field "sandbox" Codec.sandboxRef.Decode
                  WorkSandboxStarted.Backend = get.Required.Field "backend" Decode.string
                  // Optional on the way in: every start recorded before a repo could describe
                  // its sandboxes has no such field, and those logs are still read.
                  WorkSandboxStarted.Description =
                    get.Optional.Field "description" (Decode.option Decode.string) |> Option.flatten
                  // Optional in, like the description beside it: logs written before a start
                  // could say where its checkout was are still read.
                  WorkSandboxStarted.Checkout =
                    get.Optional.Field "checkout" (Decode.option Decode.string) |> Option.flatten
                  // A `credentialOwner` beside it in an older log is left unread: a start
                  // no longer has one, and what it said is not a fact this version can act on.
                  WorkSandboxStarted.Forwarded =
                    get.Required.Field "forwarded" (Decode.list (Codec.viaSmartCtor ConnectionName.create Decode.string))
                  // Optional on the way in, and this is the only backward-compatible reading
                  // available: a start written before this field existed has no answer, and
                  // absent is the right one — nothing was measured, so nothing is claimed.
                  // Encoded always, so every start written from here on says either what
                  // differed or that nothing did.
                  WorkSandboxStarted.Realisation =
                    get.Optional.Field "realisation" (Decode.list Decode.string) |> Option.defaultValue []
                  WorkSandboxStarted.Actor = get.Required.Field "actor" Codec.actor.Decode
                  // Optional in: a start written before this field existed named nobody
                  // behind its actor, and nobody is what it reads back as.
                  WorkSandboxStarted.OnBehalfOf =
                    get.Optional.Field "onBehalfOf" (Decode.option Codec.principal.Decode) |> Option.flatten
                  // Optional in: a start written before causes were recorded names none.
                  WorkSandboxStarted.CausedBy =
                    get.Optional.Field "causedBy" (Decode.option Codec.cause.Decode) |> Option.flatten }) }

    let private gitCredentialSpent : Codec<GitCredentialSpent> =
        { Encode =
            fun (p: GitCredentialSpent) ->
                Encode.object
                    [ "messageId", Codec.messageId.Encode p.MessageId
                      "sandbox", Codec.sandboxRef.Encode p.Sandbox
                      "terminal", Codec.terminalId.Encode p.Terminal
                      "block", Encode.option Codec.blockId.Encode p.Block
                      // Whose, by name — the same register the rest of the log names people
                      // in, and never a value.
                      "owner", Codec.credentialFor.Encode p.Owner
                      "repo", Codec.repoRef.Encode p.Repo
                      "actor", Codec.actor.Encode p.Actor ]
          Decode =
            Decode.object (fun get ->
                { GitCredentialSpent.MessageId = get.Required.Field "messageId" Codec.messageId.Decode
                  GitCredentialSpent.Sandbox = get.Required.Field "sandbox" Codec.sandboxRef.Decode
                  GitCredentialSpent.Terminal = get.Required.Field "terminal" Codec.terminalId.Decode
                  GitCredentialSpent.Block = get.Required.Field "block" (Decode.option Codec.blockId.Decode)
                  GitCredentialSpent.Owner = get.Required.Field "owner" Codec.credentialFor.Decode
                  GitCredentialSpent.Repo = get.Required.Field "repo" Codec.repoRef.Decode
                  GitCredentialSpent.Actor = get.Required.Field "actor" Codec.actor.Decode }) }

    let private workSandboxStarting : Codec<WorkSandboxStarting> =
        { Encode =
            fun (p: WorkSandboxStarting) ->
                Encode.object
                    [ "messageId", Codec.messageId.Encode p.MessageId
                      "sandbox", Codec.sandboxRef.Encode p.Sandbox
                      "backend", Encode.string p.Backend
                      "description", Encode.option Encode.string p.Description
                      "actor", Codec.actor.Encode p.Actor
                      "onBehalfOf", Encode.option Codec.principal.Encode p.OnBehalfOf
                      "causedBy", Encode.option Codec.cause.Encode p.CausedBy ]
          Decode =
            Decode.object (fun get ->
                { WorkSandboxStarting.MessageId = get.Required.Field "messageId" Codec.messageId.Decode
                  WorkSandboxStarting.Sandbox = get.Required.Field "sandbox" Codec.sandboxRef.Decode
                  WorkSandboxStarting.Backend = get.Required.Field "backend" Decode.string
                  WorkSandboxStarting.Description =
                    get.Optional.Field "description" (Decode.option Decode.string) |> Option.flatten
                  WorkSandboxStarting.Actor = get.Required.Field "actor" Codec.actor.Decode
                  WorkSandboxStarting.OnBehalfOf =
                    get.Optional.Field "onBehalfOf" (Decode.option Codec.principal.Decode) |> Option.flatten
                  // Optional in: a start written before causes were recorded names none.
                  WorkSandboxStarting.CausedBy =
                    get.Optional.Field "causedBy" (Decode.option Codec.cause.Decode) |> Option.flatten }) }

    let private workSandboxStartFailed : Codec<WorkSandboxStartFailed> =
        { Encode =
            fun (p: WorkSandboxStartFailed) ->
                Encode.object
                    [ "messageId", Codec.messageId.Encode p.MessageId
                      "sandbox", Codec.sandboxRef.Encode p.Sandbox
                      "reason", Encode.string p.Reason
                      "actor", Codec.actor.Encode p.Actor
                      "onBehalfOf", Encode.option Codec.principal.Encode p.OnBehalfOf
                      "causedBy", Encode.option Codec.cause.Encode p.CausedBy ]
          Decode =
            Decode.object (fun get ->
                { WorkSandboxStartFailed.MessageId = get.Required.Field "messageId" Codec.messageId.Decode
                  WorkSandboxStartFailed.Sandbox = get.Required.Field "sandbox" Codec.sandboxRef.Decode
                  WorkSandboxStartFailed.Reason = get.Required.Field "reason" Decode.string
                  WorkSandboxStartFailed.Actor = get.Required.Field "actor" Codec.actor.Decode
                  WorkSandboxStartFailed.OnBehalfOf =
                    get.Optional.Field "onBehalfOf" (Decode.option Codec.principal.Decode) |> Option.flatten
                  // Optional in: a start written before causes were recorded names none.
                  WorkSandboxStartFailed.CausedBy =
                    get.Optional.Field "causedBy" (Decode.option Codec.cause.Decode) |> Option.flatten }) }

    let private repoCapabilitiesChanged : Codec<RepoCapabilitiesChanged> =
        { Encode =
            fun (p: RepoCapabilitiesChanged) ->
                Encode.object
                    [ "messageId", Codec.messageId.Encode p.MessageId
                      "repo", Codec.repoRef.Encode p.Repo
                      // The grants themselves, not a digest: a log a person cannot read is a
                      // log nobody audits, and a short digest could be collided by whoever
                      // authors the file this watches.
                      "granted", Encode.list (p.Granted |> List.map Encode.string)
                      "sensitive", Encode.bool p.Sensitive
                      "actor", Codec.actor.Encode p.Actor
                      "causedBy", Encode.option Codec.cause.Encode p.CausedBy ]
          Decode =
            Decode.object (fun get ->
                { RepoCapabilitiesChanged.MessageId = get.Required.Field "messageId" Codec.messageId.Decode
                  RepoCapabilitiesChanged.Repo = get.Required.Field "repo" Codec.repoRef.Decode
                  RepoCapabilitiesChanged.Granted = get.Required.Field "granted" (Decode.list Decode.string)
                  // Absent in a log written before a set could be sensitive, and false is what
                  // that log meant: nothing then waited on anybody.
                  RepoCapabilitiesChanged.Sensitive =
                    get.Optional.Field "sensitive" Decode.bool |> Option.defaultValue false
                  RepoCapabilitiesChanged.Actor = get.Required.Field "actor" Codec.actor.Decode
                  // Optional in: a set said before causes were recorded names none.
                  RepoCapabilitiesChanged.CausedBy =
                    get.Optional.Field "causedBy" (Decode.option Codec.cause.Decode) |> Option.flatten }) }

    let private repoCapabilitiesApproved : Codec<RepoCapabilitiesApproved> =
        { Encode =
            fun (p: RepoCapabilitiesApproved) ->
                Encode.object
                    [ "messageId", Codec.messageId.Encode p.MessageId
                      "repo", Codec.repoRef.Encode p.Repo
                      "granted", Encode.list (p.Granted |> List.map Encode.string)
                      "actor", Codec.actor.Encode p.Actor ]
          Decode =
            Decode.object (fun get ->
                { RepoCapabilitiesApproved.MessageId = get.Required.Field "messageId" Codec.messageId.Decode
                  RepoCapabilitiesApproved.Repo = get.Required.Field "repo" Codec.repoRef.Decode
                  RepoCapabilitiesApproved.Granted = get.Required.Field "granted" (Decode.list Decode.string)
                  RepoCapabilitiesApproved.Actor = get.Required.Field "actor" Codec.actor.Decode }) }

    let private repoConfigRefused : Codec<RepoConfigRefused> =
        { Encode =
            fun (p: RepoConfigRefused) ->
                Encode.object
                    [ "messageId", Codec.messageId.Encode p.MessageId
                      "repo", Codec.repoRef.Encode p.Repo
                      // Absent rather than null when the FILE is what could not be read:
                      // there is no declaration to name, and a reader that finds no sandbox
                      // here knows the fix is in the YAML rather than in what it asked for.
                      "sandbox", Encode.option Codec.sandboxRef.Encode p.Sandbox
                      "reason", Encode.string p.Reason
                      "actor", Codec.actor.Encode p.Actor
                      "causedBy", Encode.option Codec.cause.Encode p.CausedBy ]
          Decode =
            Decode.object (fun get ->
                { RepoConfigRefused.MessageId = get.Required.Field "messageId" Codec.messageId.Decode
                  RepoConfigRefused.Repo = get.Required.Field "repo" Codec.repoRef.Decode
                  RepoConfigRefused.Sandbox = get.Required.Field "sandbox" (Decode.option Codec.sandboxRef.Decode)
                  RepoConfigRefused.Reason = get.Required.Field "reason" Decode.string
                  RepoConfigRefused.Actor = get.Required.Field "actor" Codec.actor.Decode
                  // Optional in: a refusal said before causes were recorded names none.
                  RepoConfigRefused.CausedBy =
                    get.Optional.Field "causedBy" (Decode.option Codec.cause.Decode) |> Option.flatten }) }

    let private repoConfigWarned : Codec<RepoConfigWarned> =
        { Encode =
            fun (p: RepoConfigWarned) ->
                Encode.object
                    [ "messageId", Codec.messageId.Encode p.MessageId
                      "repo", Codec.repoRef.Encode p.Repo
                      "sandbox", Encode.option Codec.sandboxRef.Encode p.Sandbox
                      "where", Encode.string p.Where
                      "warning", Encode.string p.Warning
                      "actor", Codec.actor.Encode p.Actor
                      "causedBy", Encode.option Codec.cause.Encode p.CausedBy ]
          Decode =
            Decode.object (fun get ->
                { RepoConfigWarned.MessageId = get.Required.Field "messageId" Codec.messageId.Decode
                  RepoConfigWarned.Repo = get.Required.Field "repo" Codec.repoRef.Decode
                  RepoConfigWarned.Sandbox = get.Required.Field "sandbox" (Decode.option Codec.sandboxRef.Decode)
                  RepoConfigWarned.Where = get.Required.Field "where" Decode.string
                  RepoConfigWarned.Warning = get.Required.Field "warning" Decode.string
                  RepoConfigWarned.Actor = get.Required.Field "actor" Codec.actor.Decode
                  RepoConfigWarned.CausedBy = get.Required.Field "causedBy" (Decode.option Codec.cause.Decode) }) }

    let private workSandboxStopped : Codec<WorkSandboxStopped> =
        { Encode =
            fun (p: WorkSandboxStopped) ->
                Encode.object
                    [ "messageId", Codec.messageId.Encode p.MessageId
                      "sandbox", Codec.sandboxRef.Encode p.Sandbox
                      "actor", Codec.actor.Encode p.Actor ]
          Decode =
            Decode.object (fun get ->
                { WorkSandboxStopped.MessageId = get.Required.Field "messageId" Codec.messageId.Decode
                  WorkSandboxStopped.Sandbox = get.Required.Field "sandbox" Codec.sandboxRef.Decode
                  WorkSandboxStopped.Actor = get.Required.Field "actor" Codec.actor.Decode }) }

    let private shellProfileSet : Codec<ShellProfileSet> =
        { Encode =
            fun (p: ShellProfileSet) ->
                Encode.object
                    [ "messageId", Codec.messageId.Encode p.MessageId
                      "sandbox", Codec.sandboxRef.Encode p.Sandbox
                      // The CLEAR rides as an absent value, which is what makes it
                      // indistinguishable from a line an older build wrote — and that is the
                      // right answer for both: no profile.
                      "workingDirectory", Encode.option Encode.string p.WorkingDirectory
                      "actor", Codec.actor.Encode p.Actor
                      "onBehalfOf", Encode.option Codec.principal.Encode p.OnBehalfOf ]
          Decode =
            Decode.object (fun get ->
                { ShellProfileSet.MessageId = get.Required.Field "messageId" Codec.messageId.Decode
                  ShellProfileSet.Sandbox = get.Required.Field "sandbox" Codec.sandboxRef.Decode
                  ShellProfileSet.WorkingDirectory = get.Optional.Field "workingDirectory" Decode.string
                  ShellProfileSet.Actor = get.Required.Field "actor" Codec.actor.Decode
                  // Optional in, as on a start: an older line named nobody behind its actor.
                  ShellProfileSet.OnBehalfOf =
                    get.Optional.Field "onBehalfOf" (Decode.option Codec.principal.Decode) |> Option.flatten }) }

    let private fileChanged : Codec<FileChanged> =
        let change : Codec<FileChange> =
            { Encode =
                fun (c: FileChange) ->
                    match c with
                    | FileChange.Edited (replaced, removed, added) ->
                        Encode.object
                            [ "kind", Encode.string "edited"
                              "replaced", Encode.int replaced
                              "linesRemoved", Encode.int removed
                              "linesAdded", Encode.int added ]
                    | FileChange.Written lines -> Encode.object [ "kind", Encode.string "written"; "lines", Encode.int lines ]
              Decode =
                Decode.field "kind" Decode.string
                |> Decode.andThen (fun kind ->
                    match kind with
                    | "edited" ->
                        Decode.object (fun get ->
                            FileChange.Edited (
                                get.Required.Field "replaced" Decode.int,
                                get.Required.Field "linesRemoved" Decode.int,
                                get.Required.Field "linesAdded" Decode.int
                            ))
                    | "written" -> Decode.object (fun get -> FileChange.Written (get.Required.Field "lines" Decode.int))
                    | other -> Decode.fail (sprintf "unknown file change kind '%s'" other)) }
        { Encode =
            fun (f: FileChanged) ->
                Encode.object
                    [ "messageId", Codec.messageId.Encode f.MessageId
                      "sandbox", Codec.sandboxRef.Encode f.Sandbox
                      "path", Encode.string f.Path
                      "change", change.Encode f.Change
                      "diff", Encode.option Encode.string f.Diff
                      "actor", Codec.actor.Encode f.Actor ]
          Decode =
            Decode.object (fun get ->
                { FileChanged.MessageId = get.Required.Field "messageId" Codec.messageId.Decode
                  FileChanged.Sandbox = get.Required.Field "sandbox" Codec.sandboxRef.Decode
                  FileChanged.Path = get.Required.Field "path" Decode.string
                  FileChanged.Change = get.Required.Field "change" change.Decode
                  FileChanged.Diff = get.Optional.Field "diff" Decode.string
                  FileChanged.Actor = get.Required.Field "actor" Codec.actor.Decode }) }

    let private tabOpened : Codec<TabOpened> =
        { Encode =
            fun (t: TabOpened) -> Encode.object [ "ref", Codec.viewRef.Encode t.Ref; "focus", Encode.bool t.Focus ]
          Decode =
            Decode.object (fun get ->
                { TabOpened.Ref = get.Required.Field "ref" Codec.viewRef.Decode
                  // Absent reads as `false`: an opening that did not say it takes the screen
                  // did not take it, which is the answer that leaves a reader where they are.
                  TabOpened.Focus = get.Optional.Field "focus" Decode.bool |> Option.defaultValue false }) }

    let private tabClosed : Codec<TabClosed> =
        { Encode = fun (t: TabClosed) -> Encode.object [ "ref", Codec.viewRef.Encode t.Ref ]
          Decode = Decode.object (fun get -> { TabClosed.Ref = get.Required.Field "ref" Codec.viewRef.Decode }) }

    let private artifactShared : Codec<ArtifactShared> =
        { Encode =
            fun (a: ArtifactShared) ->
                Encode.object
                    [ "messageId", Codec.messageId.Encode a.MessageId
                      "ref", Codec.artifactRef.Encode a.Ref
                      "mediaType", Encode.option Encode.string a.MediaType
                      "bytes", Encode.int64 a.Bytes
                      "digest", Encode.string (ContentDigest.value a.Digest)
                      "actor", Codec.actor.Encode a.Actor ]
          Decode =
            Decode.object (fun get ->
                { ArtifactShared.MessageId = get.Required.Field "messageId" Codec.messageId.Decode
                  ArtifactShared.Ref = get.Required.Field "ref" Codec.artifactRef.Decode
                  ArtifactShared.MediaType = get.Optional.Field "mediaType" Decode.string
                  ArtifactShared.Bytes = get.Required.Field "bytes" Decode.int64
                  ArtifactShared.Digest = get.Required.Field "digest" (Codec.viaSmartCtor ContentDigest.create Decode.string)
                  ArtifactShared.Actor = get.Required.Field "actor" Codec.actor.Decode }) }

    let private commandRefused : Codec<CommandRefused> =
        { Encode =
            fun (p: CommandRefused) ->
                Encode.object
                    [ "messageId", Codec.messageId.Encode p.MessageId
                      "queueId", Codec.queueId.Encode p.QueueId
                      "tool", Encode.string p.Tool
                      "summary", Encode.string p.Summary
                      "author", Codec.actor.Encode p.Author
                      "rejectedBy", Codec.actor.Encode p.RejectedBy
                      "reason", Encode.option Encode.string p.Reason ]
          Decode =
            Decode.object (fun get ->
                { CommandRefused.MessageId = get.Required.Field "messageId" Codec.messageId.Decode
                  CommandRefused.QueueId = get.Required.Field "queueId" Codec.queueId.Decode
                  CommandRefused.Tool = get.Required.Field "tool" Decode.string
                  CommandRefused.Summary = get.Required.Field "summary" Decode.string
                  CommandRefused.Author = get.Required.Field "author" Codec.actor.Decode
                  CommandRefused.RejectedBy = get.Required.Field "rejectedBy" Codec.actor.Decode
                  CommandRefused.Reason = get.Required.Field "reason" (Decode.option Decode.string) }) }

    let private gatedCommandFailed : Codec<GatedCommandFailed> =
        { Encode =
            fun (p: GatedCommandFailed) ->
                Encode.object
                    [ "messageId", Codec.messageId.Encode p.MessageId
                      "tool", Encode.string p.Tool
                      "summary", Encode.string p.Summary
                      "author", Codec.actor.Encode p.Author
                      "reason", Encode.string p.Reason ]
          Decode =
            Decode.object (fun get ->
                { GatedCommandFailed.MessageId = get.Required.Field "messageId" Codec.messageId.Decode
                  GatedCommandFailed.Tool = get.Required.Field "tool" Decode.string
                  GatedCommandFailed.Summary = get.Required.Field "summary" Decode.string
                  GatedCommandFailed.Author = get.Required.Field "author" Codec.actor.Decode
                  GatedCommandFailed.Reason = get.Required.Field "reason" Decode.string }) }

    /// Whether the CALL happened. Tagged rather than a nullable reason, so "it went fine"
    /// and "it failed with an empty message" stay distinguishable on the wire.
    let private toolOutcome : Codec<ToolOutcome> =
        { Encode =
            fun (outcome: ToolOutcome) ->
                match outcome with
                | ToolCallOk -> Encode.object [ "type", Encode.string "ok" ]
                | ToolCallFailed reason ->
                    Encode.object [ "type", Encode.string "failed"; "reason", Encode.string reason ]
          Decode =
            Decode.field "type" Decode.string
            |> Decode.andThen (fun t ->
                match t with
                | "ok" -> Decode.succeed ToolCallOk
                | "failed" -> Decode.field "reason" Decode.string |> Decode.map ToolCallFailed
                | other -> Decode.fail (sprintf "Unknown tool outcome: %s" other)) }

    let private toolUseStarted : Codec<ToolUseStarted> =
        { Encode =
            fun (p: ToolUseStarted) ->
                Encode.object
                    [ "toolUseId", Codec.toolUseId.Encode p.ToolUseId
                      "agentTurnId", Codec.agentTurnId.Encode p.AgentTurnId
                      "namespace", Encode.string p.Namespace
                      "name", Encode.string p.Name
                      // `null` and a recorded object are different facts: nothing of this
                      // call's arguments may be recorded, versus these are its arguments
                      // with the secrets already gone.
                      "arguments", (match p.Arguments with Some a -> Encode.string a | None -> Encode.nil) ]
          Decode =
            Decode.object (fun get ->
                { ToolUseStarted.ToolUseId = get.Required.Field "toolUseId" Codec.toolUseId.Decode
                  ToolUseStarted.AgentTurnId = get.Required.Field "agentTurnId" Codec.agentTurnId.Decode
                  ToolUseStarted.Namespace = get.Required.Field "namespace" Decode.string
                  ToolUseStarted.Name = get.Required.Field "name" Decode.string
                  ToolUseStarted.Arguments = get.Required.Field "arguments" (Decode.option Decode.string) }) }

    let private toolUseFinished : Codec<ToolUseFinished> =
        { Encode =
            fun (p: ToolUseFinished) ->
                Encode.object
                    [ "toolUseId", Codec.toolUseId.Encode p.ToolUseId
                      "outcome", toolOutcome.Encode p.Outcome
                      "block", (match p.Block with Some b -> Codec.blockId.Encode b | None -> Encode.nil)
                      // Written only when there is one, so an event that carries no disclosable
                      // result is byte-identical to one from before the field existed.
                      "result", (match p.Result with Some r -> Encode.string r | None -> Encode.nil) ]
          Decode =
            Decode.object (fun get ->
                { ToolUseFinished.ToolUseId = get.Required.Field "toolUseId" Codec.toolUseId.Decode
                  ToolUseFinished.Outcome = get.Required.Field "outcome" toolOutcome.Decode
                  ToolUseFinished.Block = get.Required.Field "block" (Decode.option Codec.blockId.Decode)
                  // Optional so every tool-use event written before this field decodes: an
                  // absent `result` is a call with nothing to disclose, same as an explicit null.
                  ToolUseFinished.Result =
                    get.Optional.Field "result" (Decode.option Decode.string) |> Option.flatten }) }

    let sessionEvent : Codec<SessionEvent> =
        { Encode =
            (fun e ->
                match e with
                | SessionStarted p ->
                    Encode.object [ "type", Encode.string "sessionStarted"; "payload", sessionStarted.Encode p ]
                | PeerJoined p ->
                    Encode.object [ "type", Encode.string "peerJoined"; "payload", peerJoined.Encode p ]
                | PeerLeft p ->
                    Encode.object [ "type", Encode.string "peerLeft"; "payload", peerLeft.Encode p ]
                | MessageSent p ->
                    Encode.object [ "type", Encode.string "messageSent"; "payload", messageSent.Encode p ]
                | SessionNamed p ->
                    Encode.object [ "type", Encode.string "sessionNamed"; "payload", sessionNamed.Encode p ]
                | AgentTurnStarted p ->
                    Encode.object [ "type", Encode.string "agentTurnStarted"; "payload", agentTurnStarted.Encode p ]
                | AgentContextBuilt p ->
                    Encode.object [ "type", Encode.string "agentContextBuilt"; "payload", agentContextBuilt.Encode p ]
                | AgentMessageStarted p ->
                    Encode.object [ "type", Encode.string "agentMessageStarted"; "payload", agentMessageStarted.Encode p ]
                | AgentMessageDelta p ->
                    Encode.object [ "type", Encode.string "agentMessageDelta"; "payload", agentMessageDelta.Encode p ]
                | AgentThought p ->
                    Encode.object [ "type", Encode.string "agentThought"; "payload", agentThought.Encode p ]
                | AgentMessageCompleted p ->
                    Encode.object [ "type", Encode.string "agentMessageCompleted"; "payload", agentMessageCompleted.Encode p ]
                | AgentTurnFailed p ->
                    Encode.object [ "type", Encode.string "agentTurnFailed"; "payload", agentTurnFailed.Encode p ]
                | AgentTurnInterrupted p ->
                    Encode.object [ "type", Encode.string "agentTurnInterrupted"; "payload", agentTurnInterrupted.Encode p ]
                | EnvironmentNeedIdentified p ->
                    Encode.object [ "type", Encode.string "environmentNeedIdentified"; "payload", environmentNeedIdentified.Encode p ]
                | EnvironmentStartRequested p ->
                    Encode.object [ "type", Encode.string "environmentStartRequested"; "payload", environmentStartRequested.Encode p ]
                | EnvironmentStarted p ->
                    Encode.object [ "type", Encode.string "environmentStarted"; "payload", environmentStarted.Encode p ]
                | EnvironmentStartFailed p ->
                    Encode.object [ "type", Encode.string "environmentStartFailed"; "payload", environmentStartFailed.Encode p ]
                | EnvironmentStopRequested p ->
                    Encode.object [ "type", Encode.string "environmentStopRequested"; "payload", environmentStopRequested.Encode p ]
                | EnvironmentStopped p ->
                    Encode.object [ "type", Encode.string "environmentStopped"; "payload", environmentStopped.Encode p ]
                | CommandRequested p ->
                    Encode.object [ "type", Encode.string "commandRequested"; "payload", commandRequested.Encode p ]
                | CommandStarted p ->
                    Encode.object [ "type", Encode.string "commandStarted"; "payload", commandStarted.Encode p ]
                | CommandOutputReceived p ->
                    Encode.object [ "type", Encode.string "commandOutputReceived"; "payload", commandOutputReceived.Encode p ]
                | CommandCompleted p ->
                    Encode.object [ "type", Encode.string "commandCompleted"; "payload", commandCompleted.Encode p ]
                | TerminalOpened p ->
                    Encode.object [ "type", Encode.string "terminalOpened"; "payload", terminalOpened.Encode p ]
                | TerminalClosed p ->
                    Encode.object [ "type", Encode.string "terminalClosed"; "payload", terminalClosed.Encode p ]
                | TerminalBlockStarted p ->
                    Encode.object [ "type", Encode.string "terminalBlockStarted"; "payload", terminalBlockStarted.Encode p ]
                | TerminalBlockCompleted p ->
                    Encode.object [ "type", Encode.string "terminalBlockCompleted"; "payload", terminalBlockCompleted.Encode p ]
                | TerminalCommandRejected p ->
                    Encode.object [ "type", Encode.string "terminalCommandRejected"; "payload", terminalCommandRejected.Encode p ]
                | SessionEvent.TerminalIntegrationLost p ->
                    Encode.object
                        [ "type", Encode.string "terminalIntegrationLost"
                          "payload", terminalIntegrationLost.Encode p ]
                | SessionEvent.TerminalIntegrationRestored p ->
                    Encode.object
                        [ "type", Encode.string "terminalIntegrationRestored"
                          "payload", terminalIntegrationRestored.Encode p ]
                | SessionEvent.TerminalMarkedLate p ->
                    Encode.object [ "type", Encode.string "terminalMarkedLate"; "payload", terminalMarkedLate.Encode p ]
                | TerminalLeaseTaken p ->
                    Encode.object [ "type", Encode.string "terminalLeaseTaken"; "payload", terminalLeaseTaken.Encode p ]
                | TerminalLeaseReleased p ->
                    Encode.object [ "type", Encode.string "terminalLeaseReleased"; "payload", terminalLeaseReleased.Encode p ]
                | TerminalTranscriptTruncated p ->
                    Encode.object [ "type", Encode.string "terminalTranscriptTruncated"; "payload", terminalTranscriptTruncated.Encode p ]
                | RepoAdded p ->
                    Encode.object [ "type", Encode.string "repoAdded"; "payload", repoAdded.Encode p ]
                | RepoRemoved p ->
                    Encode.object [ "type", Encode.string "repoRemoved"; "payload", repoRemoved.Encode p ]
                | RepoBranchSwitched p ->
                    Encode.object [ "type", Encode.string "repoBranchSwitched"; "payload", repoBranchSwitched.Encode p ]
                | WorkSandboxStarting p ->
                    Encode.object [ "type", Encode.string "workSandboxStarting"; "payload", workSandboxStarting.Encode p ]
                | WorkSandboxStarted p ->
                    Encode.object [ "type", Encode.string "workSandboxStarted"; "payload", workSandboxStarted.Encode p ]
                | WorkSandboxStartFailed p ->
                    Encode.object [ "type", Encode.string "workSandboxStartFailed"; "payload", workSandboxStartFailed.Encode p ]
                | SandboxSetupQueued p ->
                    Encode.object [ "type", Encode.string "sandboxSetupQueued"; "payload", sandboxSetupQueued.Encode p ]
                | GitCredentialSpent p ->
                    Encode.object [ "type", Encode.string "gitCredentialSpent"; "payload", gitCredentialSpent.Encode p ]
                | WorkSandboxStopped p ->
                    Encode.object [ "type", Encode.string "workSandboxStopped"; "payload", workSandboxStopped.Encode p ]
                | RepoConfigRefused p ->
                    Encode.object [ "type", Encode.string "repoConfigRefused"; "payload", repoConfigRefused.Encode p ]
                | RepoConfigWarned p ->
                    Encode.object [ "type", Encode.string "repoConfigWarned"; "payload", repoConfigWarned.Encode p ]
                | RepoCapabilitiesChanged p ->
                    Encode.object
                        [ "type", Encode.string "repoCapabilitiesChanged"
                          "payload", repoCapabilitiesChanged.Encode p ]
                | RepoCapabilitiesApproved p ->
                    Encode.object
                        [ "type", Encode.string "repoCapabilitiesApproved"
                          "payload", repoCapabilitiesApproved.Encode p ]
                | ShellProfileSet p ->
                    Encode.object [ "type", Encode.string "shellProfileSet"; "payload", shellProfileSet.Encode p ]
                | SessionEvent.FileChanged p ->
                    Encode.object [ "type", Encode.string "fileChanged"; "payload", fileChanged.Encode p ]
                | SessionEvent.ArtifactShared p ->
                    Encode.object [ "type", Encode.string "artifactShared"; "payload", artifactShared.Encode p ]
                | SessionEvent.TabOpened p ->
                    Encode.object [ "type", Encode.string "tabOpened"; "payload", tabOpened.Encode p ]
                | SessionEvent.TabClosed p ->
                    Encode.object [ "type", Encode.string "tabClosed"; "payload", tabClosed.Encode p ]
                | SessionEvent.CommandRefused p ->
                    Encode.object [ "type", Encode.string "commandRefused"; "payload", commandRefused.Encode p ]
                | SessionEvent.GatedCommandFailed p ->
                    Encode.object [ "type", Encode.string "gatedCommandFailed"; "payload", gatedCommandFailed.Encode p ]
                | ToolUseStarted p ->
                    Encode.object [ "type", Encode.string "toolUseStarted"; "payload", toolUseStarted.Encode p ]
                | ToolUseFinished p ->
                    Encode.object [ "type", Encode.string "toolUseFinished"; "payload", toolUseFinished.Encode p ]
                | McpServerAvailable p ->
                    Encode.object [ "type", Encode.string "mcpServerAvailable"; "payload", mcpServerNoted.Encode p ]
                | SessionResumed p ->
                    Encode.object [ "type", Encode.string "sessionResumed"; "payload", sessionResumed.Encode p ]
                | McpServerUnavailable p ->
                    Encode.object [ "type", Encode.string "mcpServerUnavailable"; "payload", mcpServerNoted.Encode p ]
                | SessionEvent.PrWatched p ->
                    Encode.object [ "type", Encode.string "prWatched"; "payload", prWatched.Encode p ]
                | SessionEvent.PrUnwatched p ->
                    Encode.object [ "type", Encode.string "prUnwatched"; "payload", prUnwatched.Encode p ]
                | SessionEvent.PrTransitioned p ->
                    Encode.object [ "type", Encode.string "prTransitioned"; "payload", prTransitioned.Encode p ]
                | SessionEvent.PrWatchReadability p ->
                    Encode.object [ "type", Encode.string "prWatchReadability"; "payload", prWatchReadability.Encode p ])
          Decode =
            Decode.field "type" Decode.string
            |> Decode.andThen (fun t ->
                match t with
                | "sessionStarted" -> Decode.field "payload" sessionStarted.Decode |> Decode.map SessionStarted
                | "peerJoined" -> Decode.field "payload" peerJoined.Decode |> Decode.map PeerJoined
                | "peerLeft" -> Decode.field "payload" peerLeft.Decode |> Decode.map PeerLeft
                | "messageSent" -> Decode.field "payload" messageSent.Decode |> Decode.map MessageSent
                | "sessionNamed" -> Decode.field "payload" sessionNamed.Decode |> Decode.map SessionNamed
                | "agentTurnStarted" -> Decode.field "payload" agentTurnStarted.Decode |> Decode.map AgentTurnStarted
                | "agentContextBuilt" -> Decode.field "payload" agentContextBuilt.Decode |> Decode.map AgentContextBuilt
                | "agentMessageStarted" -> Decode.field "payload" agentMessageStarted.Decode |> Decode.map AgentMessageStarted
                | "agentMessageDelta" -> Decode.field "payload" agentMessageDelta.Decode |> Decode.map AgentMessageDelta
                | "agentThought" -> Decode.field "payload" agentThought.Decode |> Decode.map AgentThought
                | "agentMessageCompleted" -> Decode.field "payload" agentMessageCompleted.Decode |> Decode.map AgentMessageCompleted
                | "agentTurnFailed" -> Decode.field "payload" agentTurnFailed.Decode |> Decode.map AgentTurnFailed
                | "agentTurnInterrupted" -> Decode.field "payload" agentTurnInterrupted.Decode |> Decode.map AgentTurnInterrupted
                | "environmentNeedIdentified" -> Decode.field "payload" environmentNeedIdentified.Decode |> Decode.map EnvironmentNeedIdentified
                | "environmentStartRequested" -> Decode.field "payload" environmentStartRequested.Decode |> Decode.map EnvironmentStartRequested
                | "environmentStarted" -> Decode.field "payload" environmentStarted.Decode |> Decode.map EnvironmentStarted
                | "environmentStartFailed" -> Decode.field "payload" environmentStartFailed.Decode |> Decode.map EnvironmentStartFailed
                | "environmentStopRequested" -> Decode.field "payload" environmentStopRequested.Decode |> Decode.map EnvironmentStopRequested
                | "environmentStopped" -> Decode.field "payload" environmentStopped.Decode |> Decode.map EnvironmentStopped
                | "commandRequested" -> Decode.field "payload" commandRequested.Decode |> Decode.map CommandRequested
                | "commandStarted" -> Decode.field "payload" commandStarted.Decode |> Decode.map CommandStarted
                | "commandOutputReceived" -> Decode.field "payload" commandOutputReceived.Decode |> Decode.map CommandOutputReceived
                | "commandCompleted" -> Decode.field "payload" commandCompleted.Decode |> Decode.map CommandCompleted
                | "terminalOpened" -> Decode.field "payload" terminalOpened.Decode |> Decode.map TerminalOpened
                | "terminalClosed" -> Decode.field "payload" terminalClosed.Decode |> Decode.map TerminalClosed
                | "terminalBlockStarted" -> Decode.field "payload" terminalBlockStarted.Decode |> Decode.map TerminalBlockStarted
                | "terminalBlockCompleted" -> Decode.field "payload" terminalBlockCompleted.Decode |> Decode.map TerminalBlockCompleted
                | "terminalCommandRejected" -> Decode.field "payload" terminalCommandRejected.Decode |> Decode.map TerminalCommandRejected
                | "terminalIntegrationLost" ->
                    Decode.field "payload" terminalIntegrationLost.Decode |> Decode.map TerminalIntegrationLost
                | "terminalIntegrationRestored" ->
                    Decode.field "payload" terminalIntegrationRestored.Decode |> Decode.map TerminalIntegrationRestored
                | "terminalMarkedLate" -> Decode.field "payload" terminalMarkedLate.Decode |> Decode.map TerminalMarkedLate
                | "terminalLeaseTaken" -> Decode.field "payload" terminalLeaseTaken.Decode |> Decode.map TerminalLeaseTaken
                | "terminalLeaseReleased" -> Decode.field "payload" terminalLeaseReleased.Decode |> Decode.map TerminalLeaseReleased
                | "terminalTranscriptTruncated" ->
                    Decode.field "payload" terminalTranscriptTruncated.Decode |> Decode.map TerminalTranscriptTruncated
                | "repoAdded" -> Decode.field "payload" repoAdded.Decode |> Decode.map RepoAdded
                | "repoRemoved" -> Decode.field "payload" repoRemoved.Decode |> Decode.map RepoRemoved
                | "repoBranchSwitched" -> Decode.field "payload" repoBranchSwitched.Decode |> Decode.map RepoBranchSwitched
                | "workSandboxStarting" -> Decode.field "payload" workSandboxStarting.Decode |> Decode.map WorkSandboxStarting
                | "workSandboxStarted" -> Decode.field "payload" workSandboxStarted.Decode |> Decode.map WorkSandboxStarted
                | "workSandboxStartFailed" ->
                    Decode.field "payload" workSandboxStartFailed.Decode |> Decode.map WorkSandboxStartFailed
                | "sandboxSetupQueued" -> Decode.field "payload" sandboxSetupQueued.Decode |> Decode.map SandboxSetupQueued
                | "gitCredentialSpent" -> Decode.field "payload" gitCredentialSpent.Decode |> Decode.map GitCredentialSpent
                | "repoConfigRefused" -> Decode.field "payload" repoConfigRefused.Decode |> Decode.map RepoConfigRefused
                | "repoConfigWarned" -> Decode.field "payload" repoConfigWarned.Decode |> Decode.map RepoConfigWarned
                | "repoCapabilitiesChanged" ->
                    Decode.field "payload" repoCapabilitiesChanged.Decode |> Decode.map RepoCapabilitiesChanged
                | "repoCapabilitiesApproved" ->
                    Decode.field "payload" repoCapabilitiesApproved.Decode |> Decode.map RepoCapabilitiesApproved
                | "workSandboxStopped" -> Decode.field "payload" workSandboxStopped.Decode |> Decode.map WorkSandboxStopped
                | "shellProfileSet" -> Decode.field "payload" shellProfileSet.Decode |> Decode.map ShellProfileSet
                | "fileChanged" -> Decode.field "payload" fileChanged.Decode |> Decode.map SessionEvent.FileChanged
                | "tabOpened" -> Decode.field "payload" tabOpened.Decode |> Decode.map SessionEvent.TabOpened
                | "tabClosed" -> Decode.field "payload" tabClosed.Decode |> Decode.map SessionEvent.TabClosed
                | "artifactShared" ->
                    Decode.field "payload" artifactShared.Decode |> Decode.map SessionEvent.ArtifactShared
                | "commandRefused" -> Decode.field "payload" commandRefused.Decode |> Decode.map SessionEvent.CommandRefused
                | "gatedCommandFailed" -> Decode.field "payload" gatedCommandFailed.Decode |> Decode.map SessionEvent.GatedCommandFailed
                | "toolUseStarted" -> Decode.field "payload" toolUseStarted.Decode |> Decode.map ToolUseStarted
                | "toolUseFinished" -> Decode.field "payload" toolUseFinished.Decode |> Decode.map ToolUseFinished
                | "sessionResumed" -> Decode.field "payload" sessionResumed.Decode |> Decode.map SessionResumed
                | "mcpServerAvailable" ->
                    Decode.field "payload" mcpServerNoted.Decode |> Decode.map McpServerAvailable
                | "mcpServerUnavailable" ->
                    Decode.field "payload" mcpServerNoted.Decode |> Decode.map McpServerUnavailable
                | "prWatched" ->
                    Decode.field "payload" prWatched.Decode |> Decode.map SessionEvent.PrWatched
                | "prUnwatched" ->
                    Decode.field "payload" prUnwatched.Decode |> Decode.map SessionEvent.PrUnwatched
                | "prTransitioned" ->
                    Decode.field "payload" prTransitioned.Decode |> Decode.map SessionEvent.PrTransitioned
                | "prWatchReadability" ->
                    Decode.field "payload" prWatchReadability.Decode |> Decode.map SessionEvent.PrWatchReadability
                | other -> Decode.fail (sprintf "Unknown session event type: %s" other)) }

    /// Wrap any event codec into a codec for its envelope.
    let envelope (eventCodec: Codec<'event>) : Codec<EventEnvelope<'event>> =
        { Encode =
            (fun env ->
                Encode.object
                    [ "eventId", Codec.eventId.Encode env.EventId
                      "sessionId", Codec.sessionId.Encode env.SessionId
                      "offset", Codec.eventOffset.Encode env.Offset
                      "actor", Codec.actor.Encode env.Actor
                      "timestamp", Codec.timestamp.Encode env.Timestamp
                      "event", eventCodec.Encode env.Event ])
          Decode =
            Decode.object (fun get ->
                { EventId   = get.Required.Field "eventId" Codec.eventId.Decode
                  SessionId = get.Required.Field "sessionId" Codec.sessionId.Decode
                  Offset    = get.Required.Field "offset" Codec.eventOffset.Decode
                  Actor     = get.Required.Field "actor" Codec.actor.Decode
                  Timestamp = get.Required.Field "timestamp" Codec.timestamp.Decode
                  Event     = get.Required.Field "event" eventCodec.Decode }) }

    /// The canonical codec for a persisted session-event envelope.
    let sessionEventEnvelope : Codec<EventEnvelope<SessionEvent>> = envelope sessionEvent

    /// An event page codec for any event codec.
    let eventPage (eventCodec: Codec<'event>) : Codec<EventPage<'event>> =
        let env = envelope eventCodec
        { Encode =
            (fun (p: EventPage<'event>) ->
                Encode.object
                    [ "events", p.Events |> List.map env.Encode |> Encode.list
                      "lastOffset", Encode.option Codec.eventOffset.Encode p.LastOffset
                      "isEnd", Encode.bool p.IsEnd ])
          Decode =
            Decode.object (fun get ->
                { Events = get.Required.Field "events" (Decode.list env.Decode)
                  LastOffset = get.Required.Field "lastOffset" (Decode.option Codec.eventOffset.Decode)
                  IsEnd = get.Required.Field "isEnd" Decode.bool }) }

    let sessionEventPage : Codec<EventPage<SessionEvent>> = eventPage sessionEvent
