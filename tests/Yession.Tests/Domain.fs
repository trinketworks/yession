module Yession.Tests.Domain

open System
open Fable.Pyxpecto
open Yession.Domain
open Yession.Domain.Tools
open Yession.Domain.Sandboxes
open Yession.Domain.Agent
open Yession.Domain.Link
open Yession.Domain.Repos
open Yession.Domain.Prs
open Yession.Domain.Chat
open Yession.Domain.Hooks

let private expect =
    function
    | Ok v -> v
    | Error e -> failwith e

let private identityTests =
    testList "Identity smart constructors" [
        testCase "SessionId trims surrounding whitespace" <| fun () ->
            let id = SessionId.create "  session-1  " |> expect
            Expect.equal (SessionId.value id) "session-1" "should be trimmed"

        testCase "SessionId rejects blank input" <| fun () ->
            Expect.isError (SessionId.create "   ") "blank should be rejected"

        testCase "SessionId rejects non-Docker-safe characters" <| fun () ->
            // The id names a container and volume verbatim, so it must be a legal Docker
            // object name: no spaces, slashes, or a leading punctuation char.
            Expect.isError (SessionId.create "bad id") "spaces should be rejected"
            Expect.isError (SessionId.create "bad/id") "slashes should be rejected"
            Expect.isError (SessionId.create "-lead") "leading '-' should be rejected"
            Expect.isError (SessionId.create "x") "a single char is too short for a Docker name"

        testCase "SessionId.mint produces a Docker-safe id that create accepts" <| fun () ->
            let minted = SessionId.mint ()
            let raw = SessionId.value minted
            Expect.equal raw.Length 26 "128 bits Crockford base32-encode to 26 chars"
            let isSafe c =
                (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
                || c = '_' || c = '.' || c = '-'
            Expect.isTrue (String.forall isSafe raw) "every char is a legal Docker name char"
            // Round-trips through the parser (mint and create agree on the invariant).
            Expect.equal (SessionId.create raw |> expect) minted "minted id parses back"

        testCase "SessionId.mint is unique per call" <| fun () ->
            Expect.isFalse (SessionId.mint () = SessionId.mint ()) "two mints differ"

        testCase "PeerId rejects empty input" <| fun () ->
            Expect.isError (PeerId.create "") "empty should be rejected"

        testCase "EventOffset rejects negative values" <| fun () ->
            Expect.isError (EventOffset.create -1L) "negative should be rejected"

        testCase "EventOffset accepts zero" <| fun () ->
            let offset = EventOffset.create 0L |> expect
            Expect.equal (EventOffset.value offset) 0L "zero is valid"

        testCase "EventId rejects the empty guid" <| fun () ->
            Expect.isError (EventId.create Guid.Empty) "empty guid should be rejected"
    ]

let private envelopeSerializationTests =
    let sampleEnvelope () : EventEnvelope<SessionEvent> =
        let sessionId = SessionId.create "session-42" |> expect
        let peerId = PeerId.create "peer-7" |> expect
        { EventId = EventId.fresh ()
          SessionId = sessionId
          Offset = EventOffset.zero
          Actor = PeerRef peerId
          Timestamp = DateTimeOffset(2026, 6, 14, 10, 30, 0, TimeSpan.FromHours 10.0)
          Event = SessionCreated { SessionCreated.SessionId = sessionId } }

    testList "Envelope serialization" [
        testCase "EventEnvelope<SessionEvent> round-trips through serialization unchanged" <| fun () ->
            let original = sampleEnvelope ()
            let json = Codec.toString Codec.sessionEventEnvelope original
            let roundTripped = Codec.fromString Codec.sessionEventEnvelope json |> expect
            Expect.equal roundTripped original "round-trip should be identical"

        testCase "Decoding malformed JSON yields an Error" <| fun () ->
            Expect.isError (Codec.fromString Codec.sessionEventEnvelope "{ not valid json ") "malformed JSON should fail"

        testCase "a gated command's failure round-trips through the envelope codec" <| fun () ->
            let original =
                { sampleEnvelope () with
                    Event =
                        GatedCommandFailed
                            { MessageId = MessageId.create "f1" |> expect
                              Tool = "add_repo"
                              Summary = "add_repo octo/hello"
                              Author = UserRef (UserId.create "ada" |> expect)
                              Reason = "github says not found" } }
            let json = Codec.toString Codec.sessionEventEnvelope original
            Expect.equal (Codec.fromString Codec.sessionEventEnvelope json) (Ok original) "round-trip should be identical"

        testCase "a title the session settled round-trips through the envelope codec" <| fun () ->
            let original =
                { sampleEnvelope () with
                    Actor = SessionProcess
                    Event =
                        SessionNamed
                            { Subject = NamingSubject.Title
                              Name = "The refresh-token bug"
                              Read = 9
                              OnBehalfOf = None } }
            let json = Codec.toString Codec.sessionEventEnvelope original
            Expect.equal (Codec.fromString Codec.sessionEventEnvelope json) (Ok original) "round-trip should be identical"

        testCase "a name the session settled round-trips through the envelope codec" <| fun () ->
            let original =
                { sampleEnvelope () with
                    Actor = SessionProcess
                    Event =
                        SessionNamed
                            { Subject = NamingSubject.Chapter (MessageId.create "m1" |> expect)
                              Name = "The refresh-token bug"
                              Read = 4
                              OnBehalfOf = Some (Principal.User (UserId.create "ada" |> expect)) } }
            let json = Codec.toString Codec.sessionEventEnvelope original
            Expect.equal (Codec.fromString Codec.sessionEventEnvelope json) (Ok original) "round-trip should be identical"

        testCase "UserRef actor round-trips through the envelope codec" <| fun () ->
            let user = UserId.create "nick@example.com" |> expect
            let original = { sampleEnvelope () with Actor = UserRef user }
            let json = Codec.toString Codec.sessionEventEnvelope original
            let roundTripped = Codec.fromString Codec.sessionEventEnvelope json |> expect
            Expect.equal roundTripped original "round-trip should be identical"
    ]

/// What an act note said beyond its headline. A reader rather than a match at every call
/// site: the split is the thing under test in several cases here, and a case that has to
/// destructure a union to ask its question reads as being about the union.
let private noteDetail (item: ConversationItem) : string option =
    match item.Kind with
    | ConversationItemKind.ActNote facts -> facts.Detail
    | ConversationItemKind.Message -> None

let private conversationProjectionTests =
    let sessionId = SessionId.create "session-proj" |> expect

    /// Ordered envelopes with the given offsets, all SessionCreated.
    let envelopes (offsets: int64 list) : EventEnvelope<SessionEvent> list =
        offsets
        |> List.map (fun n ->
            { EventId = EventId.fresh ()
              SessionId = sessionId
              Offset = EventOffset.create n |> expect
              Actor = SessionProcess
              Timestamp = DateTimeOffset(2026, 6, 14, 0, 0, 0, TimeSpan.Zero)
              Event = SessionCreated { SessionCreated.SessionId = sessionId } })

    testList "Conversation projection" [
        testCase "folding a fixed ordered sequence is deterministic" <| fun () ->
            let events = envelopes [ 0L; 1L; 2L; 3L ]
            let first = ConversationProjection.applyEvents None events ConversationProjection.empty
            let second = ConversationProjection.applyEvents None events ConversationProjection.empty
            Expect.equal first second "same input yields same projection"

        testCase "high-water offset advances to the last applied offset" <| fun () ->
            let events = envelopes [ 0L; 1L; 2L ]
            let _, highWater = ConversationProjection.applyEvents None events ConversationProjection.empty
            Expect.equal (highWater |> Option.map EventOffset.value) (Some 2L) "high-water is the last offset"

        testCase "re-applying an overlapping page does not advance past the tail or duplicate items" <| fun () ->
            let firstPage = envelopes [ 0L; 1L; 2L ]
            let proj1, hw1 = ConversationProjection.applyEvents None firstPage ConversationProjection.empty
            let overlapping = envelopes [ 1L; 2L; 3L ]
            let proj2, hw2 = ConversationProjection.applyEvents hw1 overlapping proj1
            Expect.equal (hw2 |> Option.map EventOffset.value) (Some 3L) "advances only to 3"
            Expect.equal proj2 proj1 "projection unchanged (no conversation items)"
            Expect.isEmpty proj2.Items "no items contributed"

        testCase "re-applying the identical page is a no-op" <| fun () ->
            let page = envelopes [ 0L; 1L; 2L ]
            let proj1, hw1 = ConversationProjection.applyEvents None page ConversationProjection.empty
            let proj2, hw2 = ConversationProjection.applyEvents hw1 page proj1
            Expect.equal proj2 proj1 "projection unchanged"
            Expect.equal hw2 hw1 "high-water unchanged"
    ]

let private frameSerializationTests =
    let sessionId = SessionId.create "session-frames" |> expect
    let peerId = PeerId.create "peer-1" |> expect
    let requestId = RequestId.fresh ()
    let offset = EventOffset.create 7L |> expect

    let sampleEnvelope : EventEnvelope<SessionEvent> =
        { EventId = EventId.fresh ()
          SessionId = sessionId
          Offset = offset
          Actor = PeerRef peerId
          Timestamp = DateTimeOffset(2026, 6, 14, 0, 0, 0, TimeSpan.Zero)
          Event = SessionCreated { SessionCreated.SessionId = sessionId } }

    let samplePage : EventPage<SessionEvent> =
        { Events = [ sampleEnvelope ]; LastOffset = Some offset; IsEnd = true }

    let everyVariant : SessionFrame<string> list =
        [ State (StateSync "opaque-sync-payload")
          Command (Request(requestId, InterruptAgentTurn (AgentTurnId.create "turn-1" |> expect)))
          Command (Response(requestId, CommandAccepted))
          Command (Response(requestId, CommandRejected "nope"))
          EventLog (EventsAvailable offset)
          EventLog (ReadEventsAfter(requestId, Some offset, 50))
          EventLog (ReadEventsAfter(requestId, None, 10))
          EventLog (EventsPage(requestId, samplePage))
          Control (PeerHello { PeerId = peerId; DisplayName = "Ada"; Token = "tok" })
          Control (PeerAccepted { SessionId = sessionId; AssignedDisplayName = "Ada"; LatestOffset = Some offset })
          Control (PeerRejected "bad token")
          Control Ping
          Control Pong
          Presence { Who = PeerRef peerId; DisplayName = "Ada"; Focus = Some { Field = Title; Pos = { Anchor = "AQI="; Head = "AwQ=" } } }
          Presence { Who = PeerRef peerId; DisplayName = "Ada"; Focus = Some { Field = DraftBody peerId; Pos = { Anchor = "AQI="; Head = "AQI=" } } }
          Presence { Who = PeerRef peerId; DisplayName = "Ada"; Focus = Some { Field = QueueBody (QueueId.create "q-1" |> expect); Pos = { Anchor = "AQI="; Head = "AwQ=" } } }
          Presence
            { Who = PeerRef peerId
              DisplayName = "Ada"
              Focus =
                Some
                    { Field = ChapterName (MessageId.create "msg-1" |> expect)
                      Pos = { Anchor = "AQI="; Head = "AwQ=" } } }
          Presence { Who = PeerRef peerId; DisplayName = "Ada"; Focus = None } ]

    testList "Session frame serialization" [
        testCase "every session frame variant round-trips unchanged" <| fun () ->
            let codec = Codec.sessionFrame Codec.string
            for frame in everyVariant do
                let roundTripped = Codec.toString codec frame |> Codec.fromString codec |> expect
                Expect.equal roundTripped frame "frame round-trip"

        testCase "PeerJoined and PeerLeft events round-trip through the envelope codec" <| fun () ->
            let joined = { sampleEnvelope with Event = PeerJoined { PeerId = peerId; DisplayName = "Ada"; User = None } }
            let left = { sampleEnvelope with Event = PeerLeft { PeerId = peerId } }
            for env in [ joined; left ] do
                let roundTripped =
                    Codec.toString Codec.sessionEventEnvelope env
                    |> Codec.fromString Codec.sessionEventEnvelope
                    |> expect
                Expect.equal roundTripped env "event round-trip"

        testCase "every SessionEvent case round-trips through the envelope codec" <| fun () ->
            let messageId = MessageId.create "msg-1" |> expect
            let turnId = AgentTurnId.create "turn-1" |> expect
            let toolUseId = ToolUseId.create "tool-1" |> expect
            let blockId = BlockId.create "blk-1" |> expect
            // One value per union case; extending SessionEvent without extending this
            // list is caught by the exhaustive-match warning in the projection instead,
            // so keep the two in step when adding events.
            let everyCase : SessionEvent list =
                [ SessionCreated { SessionCreated.SessionId = sessionId }
                  PeerJoined { PeerId = peerId; DisplayName = "Ada"; User = None }
                  PeerLeft { PeerId = peerId }
                  MessageSent { MessageId = messageId; QueueId = None; Author = Principal.Peer peerId; Body = "hi" }
                  MessageSent { MessageId = messageId; QueueId = Some (QueueId.create "q-1" |> expect); Author = Principal.User (UserId.create "alice" |> expect); Body = "" }
                  AgentTurnStarted { AgentTurnId = turnId; Cause = TurnCause.TriggeredBy messageId }
                  // Every wake reason (Plan 20, stages 2 and 5). A turn nobody asked for is
                  // the one whose attribution a reader most needs, and a reason that failed
                  // to decode would take the whole page with it — so each rides this list.
                  AgentTurnStarted { AgentTurnId = turnId; Cause = TurnCause.Woke CommandFinished }
                  AgentTurnStarted { AgentTurnId = turnId; Cause = TurnCause.Woke (StreamEnded (TerminalId.create "term-1" |> expect)) }
                  AgentTurnStarted { AgentTurnId = turnId; Cause = TurnCause.Woke (IntegrationLost (TerminalId.create "term-1" |> expect)) }
                  AgentContextBuilt { AgentTurnId = turnId; MessageCount = 3 }
                  AgentMessageStarted { AgentTurnId = turnId; MessageId = messageId; Antecedent = None }
                  AgentMessageStarted { AgentTurnId = turnId; MessageId = messageId; Antecedent = Some (MessageId.create "msg-0" |> expect) }
                  AgentMessageDelta { AgentTurnId = turnId; MessageId = messageId; Delta = "d" }
                  AgentMessageCompleted { AgentTurnId = turnId; MessageId = messageId; Body = "done" }
                  AgentTurnFailed { AgentTurnId = turnId; Reason = "overloaded" }
                  AgentTurnInterrupted { AgentTurnId = turnId; RequestedBy = peerId }
                  EnvironmentNeedIdentified { Reason = "task"; AgentTurnId = Some turnId }
                  EnvironmentNeedIdentified { Reason = "task"; AgentTurnId = None }
                  EnvironmentStartRequested { EnvironmentId = "env-1"; SpecSummary = "local-process" }
                  EnvironmentStarted { EnvironmentId = "env-1"; ContainerRef = "ctr-1" }
                  EnvironmentStartFailed { EnvironmentId = "env-1"; Reason = "no image" }
                  EnvironmentStopRequested { EnvironmentId = "env-1" }
                  EnvironmentStopped { EnvironmentId = "env-1" }
                  CommandRequested { CommandId = CommandId.create "cmd-1" |> expect; Executable = "node"; Arguments = [ "-e"; "1" ] }
                  CommandStarted { CommandId = CommandId.create "cmd-1" |> expect }
                  CommandOutputReceived { CommandId = CommandId.create "cmd-1" |> expect; Stream = Stdout; Text = "hi" }
                  CommandOutputReceived { CommandId = CommandId.create "cmd-1" |> expect; Stream = Stderr; Text = "err" }
                  CommandCompleted { CommandId = CommandId.create "cmd-1" |> expect; Result = CommandSucceeded 0 }
                  CommandCompleted { CommandId = CommandId.create "cmd-1" |> expect; Result = CommandFailed 3 }
                  CommandCompleted { CommandId = CommandId.create "cmd-1" |> expect; Result = CommandTimedOut }
                  CommandCompleted { CommandId = CommandId.create "cmd-1" |> expect; Result = CommandExecutionFailed "denied" }
                  RepoAdded { MessageId = messageId; Repo = RepoRef.create "octo/hello" |> expect; Branch = "main"; Actor = PeerRef peerId }
                  RepoRemoved { MessageId = messageId; Repo = RepoRef.create "octo/hello" |> expect; Actor = ActorRef.Agent }
                  RepoBranchSwitched { MessageId = messageId; Repo = RepoRef.create "octo/hello" |> expect; Branch = "feature/x"; Created = true; Actor = UserRef (UserId.create "alice" |> expect) }
                  WorkSandboxStarted
                    { MessageId = messageId
                      Sandbox = SandboxRef.parse "test" |> expect
                      Backend = "srt"
                      Description = None
                      Checkout = None
                      Forwarded = [ "github" ]
                      CredentialOwner = Some (CredentialFor.Person (Principal.User (UserId.create "alice" |> expect)))
                      Realisation = [ "the socket at /run/docker.sock — this host cannot scope that" ]
                      Actor = ActorRef.Agent }
                  // A repo-declared start, carrying both the things only a sandbox settles:
                  // what it is for, and where it sees the checkout — forwarding the
                  // deployment's own credential, which the boot fold does with nobody named.
                  WorkSandboxStarted
                    { MessageId = messageId
                      Sandbox = SandboxRef.parse "octo/hello:dev" |> expect
                      Backend = "docker"
                      Description = Some "day-to-day work"
                      Checkout = Some "/repos/octo/hello"
                      Forwarded = [ "github" ]
                      CredentialOwner = Some CredentialFor.Deployment
                      Realisation = []
                      Actor = ActorRef.Configured (RepoRef.create "octo/hello" |> expect) }
                  WorkSandboxStarted
                    { MessageId = messageId
                      Sandbox = SandboxRef.defaultRef
                      Backend = "host"
                      Description = None
                      Checkout = None
                      Forwarded = []
                      CredentialOwner = None
                      Realisation = []
                      Actor = PeerRef peerId }
                  WorkSandboxStopped { MessageId = messageId; Sandbox = SandboxRef.parse "test" |> expect; Actor = ActorRef.Agent }
                  // The shell profile (Plan 25): both cases, because a set and a clear are
                  // one event and the difference between them is the whole payload.
                  ShellProfileSet
                    { MessageId = messageId
                      Sandbox = SandboxRef.defaultRef
                      WorkingDirectory = Some "/repos/octo/hello"
                      Actor = ActorRef.Agent }
                  ShellProfileSet
                    { MessageId = messageId
                      Sandbox = SandboxRef.parse "test" |> expect
                      WorkingDirectory = None
                      Actor = PeerRef peerId }
                  // Tool use (Plan 16): both argument cases, because they are different
                  // facts — recorded-with-secrets-gone, and a foreign tool whose arguments
                  // are not recorded at all.
                  ToolUseStarted { ToolUseId = toolUseId; AgentTurnId = turnId; Namespace = "yession"; Name = "set_secret"; Arguments = Some """{"name":"DEPLOY_TOKEN","value":null}""" }
                  ToolUseStarted { ToolUseId = toolUseId; AgentTurnId = turnId; Namespace = "serial"; Name = "acquire_device"; Arguments = None }
                  ToolUseFinished { ToolUseId = toolUseId; Outcome = ToolCallOk; Block = None }
                  ToolUseFinished { ToolUseId = toolUseId; Outcome = ToolCallOk; Block = Some blockId }
                  ToolUseFinished { ToolUseId = toolUseId; Outcome = ToolCallFailed "no such tool"; Block = None }
                  // Plan 17: the two the operator's declarations produce.
                  McpServerAvailable { MessageId = messageId; Name = McpServerName.create "serial" |> expect }
                  McpServerUnavailable { MessageId = messageId; Name = McpServerName.create "printer" |> expect }
                  // Watched pull requests: a start (with its baseline snapshot), a stop,
                  // and a transition — including the optional-mergeable both ways.
                  PrWatched.create
                      messageId
                      (Authority.ofAuthor (Principal.Peer peerId))
                      { Repo = RepoRef.create "octo/hello" |> expect; Number = 12 }
                      { State = PrOpen
                        Title = "Add feature"
                        HeadSha = "abc123"
                        Checks = ChecksPending
                        Queued = true
                        Mergeable = Some true }
                  |> expect
                  |> PrWatched
                  // The agent's watch, on the turn human's credential: the two halves
                  // differ, and the wire carries the authority they are both read off.
                  PrWatched.create
                      messageId
                      (Authority.agentFor (Principal.Peer peerId))
                      { Repo = RepoRef.create "octo/hello" |> expect; Number = 13 }
                      { State = PrClosed
                        Title = "Old"
                        HeadSha = "def456"
                        Checks = ChecksNone
                        Queued = false
                        Mergeable = None }
                  |> expect
                  |> PrWatched
                  PrUnwatched
                    { MessageId = messageId
                      Pr = { Repo = RepoRef.create "octo/hello" |> expect; Number = 12 }
                      Actor = PeerRef peerId }
                  PrTransitioned
                    { MessageId = messageId
                      Pr = { Repo = RepoRef.create "octo/hello" |> expect; Number = 12 }
                      Transition = PrTransition.ChecksFailed
                      State = PrOpen
                      Checks = ChecksRed
                      Watcher = Principal.Peer peerId } ]
            for event in everyCase do
                let env = { sampleEnvelope with Event = event }
                let roundTripped =
                    Codec.toString Codec.sessionEventEnvelope env
                    |> Codec.fromString Codec.sessionEventEnvelope
                    |> expect
                Expect.equal roundTripped env "event round-trip"

        testCase "a watcher that is not a person does not decode as one" <| fun () ->
            // The wire shape of a principal is an actor's, so a stored person reads back
            // unchanged — and a stored agent, which older logs DO carry as the watcher of
            // an agent-started watch, is refused rather than resolved into something. The
            // fault this type closed was exactly a watcher the agent; letting one back in
            // through the decoder would reopen it one restart later.
            let stored (watcher: string) =
                sprintf
                    """{"type":"prTransitioned","payload":{"messageId":"t1","pr":{"repo":"octo/hello","number":12},"transition":"merged","state":"merged","checks":"green","watcher":%s}}"""
                    watcher
            Expect.isOk
                (Codec.fromString Codec.sessionEvent (stored """{"kind":"peer","peerId":"ada"}"""))
                "a person reads back"
            Expect.isError
                (Codec.fromString Codec.sessionEvent (stored """{"kind":"agent"}"""))
                "the agent is not a watcher this version can represent"

        testCase "a MessageSent persisted before Phase 3 (no queueId field) still decodes" <| fun () ->
            // Wire compatibility: event-log lines written by earlier versions carry no
            // queueId; they must decode to None, not fail the whole log open.
            let legacy =
                """{"type":"messageSent","payload":{"messageId":"msg-legacy","author":{"kind":"peer","peerId":"ada"},"body":"old line"}}"""
            let decoded = Codec.fromString Codec.sessionEvent legacy |> expect
            Expect.equal
                decoded
                (MessageSent
                    { MessageId = MessageId.create "msg-legacy" |> expect
                      QueueId = None
                      Author = Principal.Peer (PeerId.create "ada" |> expect)
                      Body = "old line" })
                "a line without queueId decodes with QueueId = None"

        testCase "an agent message started before messages had antecedents still decodes" <| fun () ->
            // Every message started before the key existed was its turn's only one, which is
            // what `None` says — and a Required field here would refuse to open every session
            // recorded before it.
            let legacy =
                """{"type":"agentMessageStarted","payload":{"agentTurnId":"turn-legacy","messageId":"msg-legacy"}}"""
            let decoded = Codec.fromString Codec.sessionEvent legacy |> expect
            Expect.equal
                decoded
                (AgentMessageStarted
                    { AgentTurnId = AgentTurnId.create "turn-legacy" |> expect
                      MessageId = MessageId.create "msg-legacy" |> expect
                      Antecedent = None })
                "a line without antecedent decodes with Antecedent = None"

        testCase "a turn started before wakes existed decodes to a message cause" <| fun () ->
            // Pre-Plan-20 turns carried only `triggeredByMessageId` — every one was
            // message-triggered, wakes not yet a thing. The sum reads that back as
            // `TriggeredBy`, so the whole history of every session opens.
            let legacy =
                """{"type":"agentTurnStarted","payload":{"agentTurnId":"turn-legacy","triggeredByMessageId":"msg-legacy"}}"""
            let decoded = Codec.fromString Codec.sessionEvent legacy |> expect
            Expect.equal
                decoded
                (AgentTurnStarted
                    { AgentTurnId = AgentTurnId.create "turn-legacy" |> expect
                      Cause = TurnCause.TriggeredBy (MessageId.create "msg-legacy" |> expect) })
                "a bare trigger id is a TriggeredBy cause"

        testCase "a turn line carrying neither cause fails to decode rather than yielding one with none" <| fun () ->
            // The sum's promise, held at the wire: a turn with no recorded cause is not a
            // thing this event can be. No real log holds such a line — this pins that a
            // corrupt one is refused, not silently admitted as an unauditable turn.
            let malformed = """{"type":"agentTurnStarted","payload":{"agentTurnId":"turn-x"}}"""
            Expect.isError (Codec.fromString Codec.sessionEvent malformed) "neither cause present is not decodable"

        testCase "a sandbox persisted before repos could declare one still decodes" <| fun () ->
            // The scope rides in the SAME string rather than a new field, so a log written
            // when every sandbox was the session's own reads back as session-owned — the
            // compatibility is a property of the wire form, not a branch in the decoder.
            let legacy =
                """{"type":"workSandboxStarted","payload":{"messageId":"msg-legacy","sandbox":"build","backend":"srt","forwarded":[],"credentialOwner":null,"actor":{"kind":"agent"}}}"""
            let decoded = Codec.fromString Codec.sessionEvent legacy |> expect
            Expect.equal
                decoded
                (WorkSandboxStarted
                    { MessageId = MessageId.create "msg-legacy" |> expect
                      Sandbox = SandboxRef.create SessionOwned (SandboxName.create "build" |> expect)
                      Backend = "srt"
                      Description = None
                      Checkout = None
                      Forwarded = []
                      CredentialOwner = None
                      // A start recorded before the host became an author of a grant. Absent
                      // reads as "nothing was measured", which is the only honest answer for
                      // a sandbox nobody asked the question about.
                      Realisation = []
                      Actor = ActorRef.Agent })
                "a bare name is the sandbox the session itself owns"
    ]

let private shellProfileTests =
    let sandboxNamed name = SandboxRef.create SessionOwned (SandboxName.create name |> expect)
    let profileSet sandbox cwd : SessionEvent =
        ShellProfileSet
            { MessageId = MessageId.create "msg-1" |> expect
              Sandbox = sandbox
              WorkingDirectory = cwd
              Actor = ActorRef.Agent }
    let fold events =
        events |> List.fold ShellProfileProjection.applyEvent ShellProfileProjection.empty
    testList "Shell profile (Plan 25)" [
        testCase "the newest set for a sandbox wins" <| fun () ->
            let folded =
                fold
                    [ profileSet SandboxRef.defaultRef (Some "/repos/one")
                      profileSet SandboxRef.defaultRef (Some "/repos/two") ]
            Expect.equal
                (ShellProfileProjection.workingDirectory SandboxRef.defaultRef folded)
                (Some "/repos/two")
                "a profile is replaced, never accumulated"

        testCase "a set leaves the other sandboxes alone" <| fun () ->
            let folded =
                fold
                    [ profileSet SandboxRef.defaultRef (Some "/repos/one")
                      profileSet (sandboxNamed "test") (Some "/repos/two") ]
            Expect.equal
                (ShellProfileProjection.workingDirectory SandboxRef.defaultRef folded)
                (Some "/repos/one")
                "a path is only a path inside the filesystem that has it"

        testCase "a clear returns its sandbox to no profile" <| fun () ->
            let folded =
                fold
                    [ profileSet SandboxRef.defaultRef (Some "/repos/one")
                      profileSet SandboxRef.defaultRef None ]
            Expect.equal
                (ShellProfileProjection.workingDirectory SandboxRef.defaultRef folded)
                None
                "back to wherever the sandbox puts them"

        testCase "a cleared sandbox is not listed at all" <| fun () ->
            let folded =
                fold
                    [ profileSet SandboxRef.defaultRef (Some "/repos/one")
                      profileSet (sandboxNamed "test") (Some "/repos/two")
                      profileSet SandboxRef.defaultRef None ]
            Expect.equal
                (ShellProfileProjection.listed folded |> List.map (fst >> SandboxRef.render))
                [ "test" ]
                "\"has a profile\" and \"starts somewhere\" are one question"

        testCase "a ShellProfileSet on the wire is the shape it has always been" <| fun () ->
            // Pinned as a literal, not round-tripped: a round-trip agrees with whatever the
            // codec currently does, and what a durable log needs is that the codec has not
            // changed under the lines already written.
            let pinned =
                """{"type":"shellProfileSet","payload":{"messageId":"msg-1","sandbox":"default","workingDirectory":"/repos/octo/hello","actor":{"kind":"agent"}}}"""
            Expect.equal
                (Codec.fromString Codec.sessionEvent pinned |> expect)
                (profileSet SandboxRef.defaultRef (Some "/repos/octo/hello"))
                "the durable form decodes to the event"

        testCase "a directory under a tree is inside it, and so is the tree itself" <| fun () ->
            // What a caller deleting a checkout asks about every profile it might invalidate.
            Expect.isTrue (ShellProfile.isInside "/repos/octo/hello" "/repos/octo/hello") "the tree itself"
            Expect.isTrue (ShellProfile.isInside "/repos/octo/hello" "/repos/octo/hello/src") "and anything under it"
            Expect.isTrue (ShellProfile.isInside "/repos/octo/hello/" "/repos/octo/hello") "however either side spells a directory"

        testCase "a sibling that merely shares a prefix is not inside it" <| fun () ->
            // The bug a bare `StartsWith` has, and the reason this is a function rather than
            // an inline test at the caller: `/repos/octo/hello-world` is a different checkout,
            // and clearing its profile because `/repos/octo/hello` went would be silent.
            Expect.isFalse
                (ShellProfile.isInside "/repos/octo/hello" "/repos/octo/hello-world")
                "a prefix is not a parent unless it ends on a directory boundary"
            Expect.isFalse (ShellProfile.isInside "/repos/octo/hello" "/repos/octo") "nor is the parent inside the child"

        testCase "a profile change reads in the timeline as a sentence" <| fun () ->
            // Everyone in the session is affected — the next terminal a PERSON opens lands
            // there too — and the timeline is the only place they would learn it.
            let envelope : EventEnvelope<SessionEvent> =
                { EventId = EventId.fresh ()
                  SessionId = SessionId.create "session-1" |> expect
                  Offset = EventOffset.create 1L |> expect
                  Actor = ActorRef.Agent
                  Timestamp = DateTimeOffset (2026, 8, 22, 0, 0, 0, TimeSpan.Zero)
                  Event = profileSet SandboxRef.defaultRef (Some "/repos/octo/hello") }
            let proj, _ =
                ConversationProjection.applyEvents None [ envelope ] ConversationProjection.empty
            match proj.Items with
            | [ item ] ->
                Expect.equal item.Body "new terminals in default start in /repos/octo/hello" "the line says where"
                Expect.equal item.Author ActorRef.Agent "attributed to whoever set it"
            | other -> failwithf "expected one act-note, got %A" other

        testCase "a ShellProfileSet with no directory is the clear" <| fun () ->
            let pinned =
                """{"type":"shellProfileSet","payload":{"messageId":"msg-1","sandbox":"default","actor":{"kind":"agent"}}}"""
            Expect.equal
                (Codec.fromString Codec.sessionEvent pinned |> expect)
                (profileSet SandboxRef.defaultRef None)
                "an absent directory decodes as None rather than failing the log open"
    ]

let private repoTests =
    testList "Repos (Plan 14)" [
        testCase "RepoRef parses owner/repo, trims, and strips a pasted .git" <| fun () ->
            let r = RepoRef.create "  NickDarvey/yession.git  " |> expect
            Expect.equal (RepoRef.owner r) "NickDarvey" "owner"
            Expect.equal (RepoRef.repo r) "yession" ".git stripped"
            Expect.equal (RepoRef.value r) "NickDarvey/yession" "canonical form"
            Expect.equal (RepoRef.cloneUrl r) "https://github.com/NickDarvey/yession.git" "constructed clone url"
            Expect.equal (RepoRef.relativePath r) "NickDarvey/yession" "checkout path"

        testCase "RepoRef refuses everything that is not owner/repo" <| fun () ->
            Expect.isError (RepoRef.create "no-slash") "no slash"
            Expect.isError (RepoRef.create "a/b/c") "two slashes"
            Expect.isError (RepoRef.create "/repo") "empty owner"
            Expect.isError (RepoRef.create "owner/") "empty repo"
            Expect.isError (RepoRef.create "owner/re po") "whitespace inside"
            Expect.isError (RepoRef.create "https://github.com/o/r") "a URL is not a name — the url is constructed, never accepted"
            Expect.isError (RepoRef.create "owner/..") "dot-dot cannot traverse"
            Expect.isError (RepoRef.create (String.replicate 40 "a" + "/repo")) "owner over GitHub's cap"

        testCase "a repo link reads what a person copied: a name, a page, a git url" <| fun () ->
            let hello = RepoRef.create "octo/hello" |> expect
            for text in
                [ "octo/hello"
                  "  octo/hello.git "
                  "github.com/octo/hello"
                  "https://github.com/octo/hello"
                  "https://www.github.com/octo/hello/"
                  "http://github.com/octo/hello.git"
                  "git@github.com:octo/hello.git" ] do
                Expect.equal (RepoLink.parse text) (Some (RepoLink.Repo hello)) text

        testCase "a branch link keeps every segment of the branch, and drops what follows the page" <| fun () ->
            let hello = RepoRef.create "octo/hello" |> expect
            Expect.equal (RepoLink.parse "https://github.com/octo/hello/tree/main") (Some (RepoLink.Branch (hello, "main"))) "one segment"
            Expect.equal
                (RepoLink.parse "https://github.com/octo/hello/tree/feature/x?search=1#readme")
                (Some (RepoLink.Branch (hello, "feature/x")))
                "a branch with a slash, and the query and fragment gone"
            Expect.equal (RepoLink.parse "https://github.com/octo/hello/tree/") None "a tree page with no branch is not a branch link"

        testCase "a pull request link is the number, whichever tab it was copied from" <| fun () ->
            let hello = RepoRef.create "octo/hello" |> expect
            Expect.equal (RepoLink.parse "https://github.com/octo/hello/pull/42") (Some (RepoLink.PullRequest (hello, 42))) "the conversation"
            Expect.equal (RepoLink.parse "https://github.com/octo/hello/pull/42/files#diff-1") (Some (RepoLink.PullRequest (hello, 42))) "the files tab"
            Expect.equal (RepoLink.parse "https://github.com/octo/hello/pull/x") None "not a number"

        testCase "what is not a repo link is nothing, not a guess" <| fun () ->
            for text in
                [ ""
                  "hello there"
                  "https://gitlab.com/octo/hello"
                  "https://github.com/octo"
                  "https://github.com/octo/hello/blob/main/README.md"
                  "https://github.com/octo/hello/issues/3" ] do
                Expect.equal (RepoLink.parse text) None text

        testCase "the repos projection folds add, re-add, switch, and remove" <| fun () ->
            let msg n = MessageId.create n |> expect
            let repo = RepoRef.create "octo/hello" |> expect
            let ada = PeerId.create "ada" |> expect
            let folded =
                [ RepoAdded { MessageId = msg "r1"; Repo = repo; Branch = "main"; Actor = PeerRef ada }
                  RepoBranchSwitched { MessageId = msg "r2"; Repo = repo; Branch = "feature/x"; Created = true; Actor = ActorRef.Agent }
                  MessageSent { MessageId = msg "m"; QueueId = None; Author = Principal.Peer ada; Body = "hi" } ]
                |> List.fold ReposProjection.applyEvent ReposProjection.empty
            Expect.equal folded.Repos [ { Repo = repo; Branch = "feature/x"; AddedBy = PeerRef ada } ] "one repo, on the switched branch"
            let readded = ReposProjection.applyEvent folded (RepoAdded { MessageId = msg "r3"; Repo = repo; Branch = "main"; Actor = ActorRef.Agent })
            Expect.equal readded.Repos [ { Repo = repo; Branch = "main"; AddedBy = ActorRef.Agent } ] "re-add replaces in place"
            let removed = ReposProjection.applyEvent readded (RepoRemoved { MessageId = msg "r4"; Repo = repo; Actor = PeerRef ada })
            Expect.equal removed.Repos [] "removed"

        testCase "repo events fold into the timeline as attributed notes" <| fun () ->
            let msg n = MessageId.create n |> expect
            let repo = RepoRef.create "octo/hello" |> expect
            let sessionId = SessionId.create "repo-session" |> expect
            let ada = PeerId.create "ada" |> expect
            let envelopes =
                [ RepoAdded { MessageId = msg "r1"; Repo = repo; Branch = "main"; Actor = PeerRef ada }
                  RepoBranchSwitched { MessageId = msg "r2"; Repo = repo; Branch = "fix/y"; Created = false; Actor = ActorRef.Agent }
                  RepoRemoved { MessageId = msg "r3"; Repo = repo; Actor = PeerRef ada } ]
                |> List.mapi (fun i event ->
                    { EventId = EventId.fresh ()
                      SessionId = sessionId
                      Offset = EventOffset.create (int64 (i + 1)) |> expect
                      Actor = ActorRef.SessionProcess
                      Timestamp = DateTimeOffset(2026, 8, 8, 10, 0, 0, TimeSpan.Zero)
                      Event = event })
            let proj, _ = ConversationProjection.applyEvents None envelopes ConversationProjection.empty
            Expect.isTrue
                (proj.Items |> List.forall (fun i -> match i.Kind with ConversationItemKind.ActNote _ -> true | _ -> false))
                "all notes"
            Expect.equal (proj.Items |> List.map (fun i -> i.Body))
                [ "added repo octo/hello"; "switched octo/hello to branch fix/y"; "removed repo octo/hello" ]
                "the notes read as sentences"
            Expect.equal (proj.Items |> List.map noteDetail)
                [ Some "on branch main"; None; None ]
                "and the particulars a headline left out are still on the note"
            Expect.equal (proj.Items |> List.map (fun i -> i.Author)) [ PeerRef ada; ActorRef.Agent; PeerRef ada ] "attributed to the acting party"

        // A person's command has no tool result for its failure to come back in, so the
        // record is the only place it is said — and it is said by the process, as a failure,
        // not as anybody's refusal.
        testCase "a gated command that ran and failed folds in as a note saying which, and why" <| fun () ->
            let sessionId = SessionId.create "repo-session" |> expect
            let ada = UserRef (UserId.create "ada" |> expect)
            let envelope =
                { EventId = EventId.fresh ()
                  SessionId = sessionId
                  Offset = EventOffset.create 1L |> expect
                  Actor = ActorRef.SessionProcess
                  Timestamp = DateTimeOffset(2026, 8, 8, 10, 0, 0, TimeSpan.Zero)
                  Event =
                    GatedCommandFailed
                        { MessageId = MessageId.create "f1" |> expect
                          Tool = "add_repo"
                          Summary = "add_repo octo/hello"
                          Author = ada
                          Reason = "github says not found" } }
            let proj, _ = ConversationProjection.applyEvents None [ envelope ] ConversationProjection.empty
            match proj.Items with
            | [ item ] ->
                Expect.equal item.Body "failed add_repo octo/hello" "the headline names the act"
                Expect.equal (noteDetail item) (Some "github says not found") "the particulars say why"
                Expect.equal item.Author ActorRef.System "said by the process: nobody refused it"
            | other -> failwithf "one note expected, got %A" other

        // The split is for a screen. Every other reader — the agent's prompt above all — has
        // to be handed both halves, because the half a headline holds back is which
        // credential went into the sandbox and why a declaration was refused. A transcript
        // built from headlines would tell the agent a sandbox started and not whose key it
        // is holding.
        testCase "what an act says is its headline and its particulars together" <| fun () ->
            let note : ConversationItem =
                { MessageId = MessageId.create "n1" |> expect
                  Author = ActorRef.Agent
                  Body = "started sandbox work (srt)"
                  Status = Complete
                  Kind = ConversationItemKind.ActNote { Detail = Some "forwarding github from user:ada"; Notable = false }
                  Offset = EventOffset.create 1L |> expect
                  Woke = None; Replying = None }
            Expect.equal
                (ConversationItem.said note)
                "started sandbox work (srt) — forwarding github from user:ada"
                "both halves, in one sentence"

        // And nothing invented where there is no second half: a seam printed over an item
        // that has one clause is punctuation standing for content that does not exist.
        testCase "an item holding nothing back says exactly its body" <| fun () ->
            let item kind : ConversationItem =
                { MessageId = MessageId.create "n1" |> expect
                  Author = ActorRef.Agent
                  Body = "removed repo octo/hello"
                  Status = Complete
                  Kind = kind
                  Offset = EventOffset.create 1L |> expect
                  Woke = None; Replying = None }
            Expect.equal
                (ConversationItem.said (item (ConversationItemKind.ActNote { Detail = None; Notable = false })))
                "removed repo octo/hello"
                "an act with one clause"
            Expect.equal
                (ConversationItem.said (item ConversationItemKind.Message))
                "removed repo octo/hello"
                "and a message, which never has a second half at all"

        // What a checkout asks for is the one act on this timeline a person has to DECIDE
        // about, and the decision needs the whole set rather than a count of it. So the
        // headline may count, and the detail may not summarise — it carries every grant, in
        // the words the operator's own surface uses.
        testCase "a set of capabilities is counted in the headline and named whole underneath" <| fun () ->
            let asked (granted: string list) =
                let envelope : EventEnvelope<SessionEvent> =
                    { EventId = EventId.fresh ()
                      SessionId = SessionId.create "caps-session" |> expect
                      Offset = EventOffset.create 1L |> expect
                      Actor = ActorRef.SessionProcess
                      Timestamp = DateTimeOffset (2026, 8, 8, 10, 0, 0, TimeSpan.Zero)
                      Event =
                        SessionEvent.RepoCapabilitiesChanged
                            { RepoCapabilitiesChanged.MessageId = MessageId.create "c1" |> expect
                              RepoCapabilitiesChanged.Repo = RepoRef.create "octo/hello" |> expect
                              RepoCapabilitiesChanged.Granted = granted
                              RepoCapabilitiesChanged.Sensitive = false
                              RepoCapabilitiesChanged.Actor = ActorRef.SessionProcess } }
                let proj, _ = ConversationProjection.applyEvents None [ envelope ] ConversationProjection.empty
                match proj.Items with
                | [ item ] -> item.Body, noteDetail item
                | other -> failwithf "expected one note, got %A" other
            Expect.equal
                (asked [ "/nix, read-only"; "reaches cache.nixos.org"; "reaches anywhere (sensitive)" ])
                ("asks for 3 capabilities", Some "/nix, read-only; reaches cache.nixos.org; reaches anywhere (sensitive)")
                "counted, then named — every one of them"

        // The counterpart, and the reason the count is not unconditional: "asks for 1
        // capability" over a line naming it is a headline that says less than the thing
        // under it.
        testCase "one capability is the headline, with nothing left to say underneath" <| fun () ->
            let envelope : EventEnvelope<SessionEvent> =
                { EventId = EventId.fresh ()
                  SessionId = SessionId.create "caps-session" |> expect
                  Offset = EventOffset.create 1L |> expect
                  Actor = ActorRef.SessionProcess
                  Timestamp = DateTimeOffset (2026, 8, 8, 10, 0, 0, TimeSpan.Zero)
                  Event =
                    SessionEvent.RepoCapabilitiesChanged
                        { RepoCapabilitiesChanged.MessageId = MessageId.create "c1" |> expect
                          RepoCapabilitiesChanged.Repo = RepoRef.create "octo/hello" |> expect
                          RepoCapabilitiesChanged.Granted = [ "/nix, read-only" ]
                          RepoCapabilitiesChanged.Sensitive = false
                          RepoCapabilitiesChanged.Actor = ActorRef.SessionProcess } }
            let proj, _ = ConversationProjection.applyEvents None [ envelope ] ConversationProjection.empty
            match proj.Items with
            | [ item ] ->
                Expect.equal item.Body "asks for /nix, read-only" "the grant itself"
                Expect.equal (noteDetail item) None "and no second line restating it"
            | other -> failwithf "expected one note, got %A" other
    ]

let private chapterTests =
    let itemSaying id body kind : ConversationItem =
        { MessageId = MessageId.create id |> expect
          Author = ActorRef.Agent
          Body = body
          Status = Complete
          Kind = kind
          Offset = EventOffset.create 1L |> expect
          Woke = None; Replying = None }
    let item id kind = itemSaying id "something happened" kind
    let notable = item "n" (ConversationItemKind.ActNote { Detail = None; Notable = true })
    let ordinary = item "o" (ConversationItemKind.ActNote { Detail = None; Notable = false })
    let said = item "s" ConversationItemKind.Message
    /// What somebody's verdict alone looks like, without a name over it — the shape a doc
    /// written before chapters had names decodes to, and the one an auto-chapter keeps.
    let verdict (item: ConversationItem) (opens: bool) =
        Map.ofList [ item.MessageId, { Opens = opens; Name = Ylmish.Text.empty } ]
    let opensOf (item: ConversationItem) (chapters: Map<MessageId, ChapterMark>) =
        chapters |> Map.tryFind item.MessageId |> Option.map (fun mark -> mark.Opens)
    let nameIn (item: ConversationItem) (chapters: Map<MessageId, ChapterMark>) =
        chapters |> Map.tryFind item.MessageId |> Option.map (fun mark -> Ylmish.Text.toString mark.Name)
    testList "Chapters (where the session divides)" [
        // The default half. A watch is the reason somebody is waiting, so its news opens a
        // chapter without anybody having asked for one.
        testCase "an act that is notable by nature opens one with nobody having said anything" <| fun () ->
            Expect.isTrue (Chapters.opens Map.empty notable) "a chapter opens here"
            Expect.isFalse (Chapters.opens Map.empty ordinary) "and an ordinary act is not"

        // The half that makes the default affordable. A default nobody can refuse becomes
        // noise the first time it is wrong.
        testCase "a person's no closes a chapter an act opens by nature" <| fun () ->
            Expect.isFalse (Chapters.opens (verdict notable false) notable) "their answer, not the act's"

        // And the other direction: a chapter can open anywhere something was said, which is
        // what makes these the reader's own divisions rather than a feed of what this
        // repository thinks is important.
        testCase "a person's yes opens one where nothing would have" <| fun () ->
            Expect.isTrue (Chapters.opens (verdict said true) said) "a message somebody chose"

        // `toggle` takes the ITEM, so the caller never has to know what it defaulted to —
        // which is the whole reason the default and the verdict are read in one place.
        testCase "toggling an act that is notable by nature records the no" <| fun () ->
            Expect.equal (opensOf notable (Chapters.toggle notable Map.empty)) (Some false) "recorded, not merely absent"

        // Absence and no are different answers, so coming back from a no is a yes rather
        // than a delete — and a later change to what is notable by nature cannot silently
        // reverse a decision somebody has already made.
        testCase "toggling it back records the yes, rather than forgetting the answer" <| fun () ->
            let chapters = Map.empty |> Chapters.toggle notable |> Chapters.toggle notable
            Expect.equal (opensOf notable chapters) (Some true) "an answer either way"

        testCase "the chapters keep the order the conversation holds them in" <| fun () ->
            Expect.equal
                (Chapters.over (verdict said true) [ said; ordinary; notable ] |> List.map (fun i -> i.MessageId))
                [ said.MessageId; notable.MessageId ]
                "both chapters, in timeline order"

        // --- What it is called ----------------------------------------------------------

        // A message is markdown, and a chapter is a line. The first line is the part of a
        // message a person wrote as its subject, whether or not they meant to.
        testCase "a message is named by its first line" <| fun () ->
            let message = itemSaying "m" "Do both ends.\nUpstream so it fails loudly." ConversationItemKind.Message
            Expect.equal (Chapters.defaultName message) "Do both ends." "the first line, and only it"

        testCase "a long first line is cut on a word boundary, and says it was cut" <| fun () ->
            let message =
                itemSaying "m" "Upstream: capture under pipefail and refuse an empty result" ConversationItemKind.Message
            let name = Chapters.defaultName message
            Expect.isTrue (name.EndsWith "…") (sprintf "a cut name says so, got %s" name)
            Expect.isFalse (name.Contains "resul…") "and the cut falls between words, not inside one"

        // An act note arrives as a sentence somebody already wrote short. Cutting it would be
        // taking a headline and making a worse headline.
        testCase "a short act headline is its whole name" <| fun () ->
            let act =
                itemSaying "a" "PR octo/hello#12 merged" (ConversationItemKind.ActNote { Detail = None; Notable = true })
            Expect.equal (Chapters.defaultName act) "PR octo/hello#12 merged" "nothing to cut"

        // The guess reads the line's WORDS. A line that opens with markdown opens with
        // punctuation that says how it is set, not what it says.
        testCase "a line that opens with markdown is named by what it says" <| fun () ->
            let bulleted = itemSaying "b" "- ship the guard first" ConversationItemKind.Message
            Expect.equal (Chapters.defaultName bulleted) "ship the guard first" "the bullet is not the name"

        // The name belongs to the SESSION from the moment the chapter does, so every peer
        // reads the same words — and the person who wants to change them has something to
        // change rather than an empty field.
        testCase "opening a chapter writes the guess down" <| fun () ->
            let message = itemSaying "m" "Do both ends." ConversationItemKind.Message
            Expect.equal (nameIn message (Chapters.toggle message Map.empty)) (Some "Do both ends.") "seeded, not left empty"

        // A mis-tap costs a chapter, never a sentence.
        testCase "closing a chapter keeps the name somebody wrote" <| fun () ->
            let chapters =
                Map.empty
                |> Chapters.toggle said
                |> Chapters.rename said (Ylmish.Text.ofString "Where it was settled")
                |> Chapters.toggle said
            Expect.equal (opensOf said chapters) (Some false) "the chapter is closed"
            Expect.equal (nameIn said chapters) (Some "Where it was settled") "and the words are still there"

        testCase "a name somebody wrote is what the chapter is called" <| fun () ->
            let chapters = Map.ofList [ said.MessageId, { Opens = true; Name = Ylmish.Text.ofString "The decision" } ]
            Expect.equal (Chapters.name chapters said) "The decision" "theirs, not the guess"

        // The case the fallback exists for: an act that opens a chapter by nature has no
        // entry at all until somebody touches it, and a decoded doc written before names
        // has an entry with nothing in it. Both read as the guess.
        testCase "a chapter nobody has named is called what the message says" <| fun () ->
            Expect.equal (Chapters.name Map.empty notable) "something happened" "no entry, still a name"
            Expect.equal (Chapters.name (verdict notable true) notable) "something happened" "an empty name, still a name"

        // Renaming is not a way to divide the session: what it writes down is the verdict the
        // item already carried, so naming a chapter that opened by itself leaves it open.
        testCase "renaming a chapter nobody opened keeps the verdict it had" <| fun () ->
            let chapters = Chapters.rename notable (Ylmish.Text.ofString "The watch begins") Map.empty
            Expect.equal (opensOf notable chapters) (Some true) "still open, by nature"
            Expect.equal (nameIn notable chapters) (Some "The watch begins") "and now it is called something"

        // --- Whether anything may write over it ------------------------------------------

        // The promise that lets something else write names at all, and the only thing
        // standing behind it. `toggle` seeds the guess, so a chapter has words from the
        // moment it exists — "has it got a name yet" is not a question about emptiness.
        testCase "a chapter still wearing the guess is unwritten" <| fun () ->
            Expect.isTrue (Chapters.unwritten (Chapters.toggle said Map.empty) said) "the guess is not a name"

        testCase "a chapter somebody named is not" <| fun () ->
            let chapters = Chapters.rename said (Ylmish.Text.ofString "Where it was settled") Map.empty
            Expect.isFalse (Chapters.unwritten chapters said) "theirs, and nothing may type over it"

        // An act that opens a chapter by nature has no entry at all until somebody touches
        // it. That is the commonest unwritten chapter there is, and a test of the map alone
        // would miss every one of them.
        testCase "a chapter nobody has touched is unwritten" <| fun () ->
            Expect.isTrue (Chapters.unwritten Map.empty notable) "no entry, no name"

        // --- What a chapter covers --------------------------------------------------------

        // What a reader takes a chapter to mean: this, and everything after it, until the
        // next chapter starts. Which is therefore what naming one has to read.
        testCase "a chapter covers its own stretch, up to where the next one begins" <| fun () ->
            let a = itemSaying "a" "first" ConversationItemKind.Message
            let b = itemSaying "b" "second" ConversationItemKind.Message
            let c = itemSaying "c" "third" ConversationItemKind.Message
            let d = itemSaying "d" "fourth" ConversationItemKind.Message
            let chapters =
                Map.ofList
                    [ a.MessageId, { Opens = true; Name = Ylmish.Text.empty }
                      c.MessageId, { Opens = true; Name = Ylmish.Text.empty } ]
            Expect.equal
                (Chapters.covers chapters [ a; b; c; d ] a |> List.map (fun i -> i.Body))
                [ "first"; "second" ]
                "up to the next chapter, and not past it"

        testCase "the last chapter covers the rest of the session" <| fun () ->
            let a = itemSaying "a" "first" ConversationItemKind.Message
            let b = itemSaying "b" "second" ConversationItemKind.Message
            let chapters = Map.ofList [ a.MessageId, { Opens = true; Name = Ylmish.Text.empty } ]
            Expect.equal
                (Chapters.covers chapters [ a; b ] a |> List.map (fun i -> i.Body))
                [ "first"; "second" ]
                "nothing after it to stop at"

        // --- What to ask, and what to do with the answer -----------------------------------

        testCase "the ask carries the chapter's own stretch" <| fun () ->
            let a = itemSaying "a" "first" ConversationItemKind.Message
            let b = itemSaying "b" "second" ConversationItemKind.Message
            let c = itemSaying "c" "third" ConversationItemKind.Message
            let chapters =
                Map.ofList
                    [ a.MessageId, { Opens = true; Name = Ylmish.Text.empty }
                      c.MessageId, { Opens = true; Name = Ylmish.Text.empty } ]
            Expect.equal (Chapters.summaryAsk chapters [ a; b; c ] a None).Lines [ "first"; "second" ] "its own, and no more"

        // A session can hold a stack trace, a diff, or forty messages. A name is made from
        // the shape of a chapter, and an ask that sent all of it would spend a model's
        // context on the part that moves a name least.
        testCase "a chapter longer than anybody reads is not sent whole" <| fun () ->
            let long = itemSaying "l" (String.replicate 500 "word ") ConversationItemKind.Message
            let ask = Chapters.summaryAsk (Map.ofList [ long.MessageId, { Opens = true; Name = Ylmish.Text.empty } ]) [ long ] long None
            let sent = ask.Lines |> List.sumBy (fun line -> line.Length)
            Expect.isTrue (sent < long.Body.Length) "bounded, rather than the whole of it"

        // A model writes prose. Every shape it writes that a name cannot hold is something
        // WRAPPING the words, so what comes back is unwrapped rather than rejected.
        testCase "an answer in quotes is a name without them" <| fun () ->
            Expect.equal (Chapters.shaped "\"The rollback\"") (Some "The rollback") "the words, not the quoting"

        testCase "an answer with more than one line is named by the first" <| fun () ->
            Expect.equal
                (Chapters.shaped "The rollback\nand why we took it")
                (Some "The rollback")
                "a rule holds one line"

        // The provider is told the budget, and a provider that ignores it cannot put a name
        // on the rule that the rule will not hold — because the cut is the guess's own.
        testCase "an answer longer than a rule holds is cut like the guess is" <| fun () ->
            let long = "Upstream: capture under pipefail and refuse an empty result before publishing"
            match Chapters.shaped long with
            | None -> failwith "a long answer is still an answer"
            | Some name ->
                Expect.isTrue (name.EndsWith "…") (sprintf "a cut name says so, got %s" name)
                Expect.isTrue (name.Length < long.Length) "and it is shorter than what came back"

        // So a caller has one case for "no words this time" rather than a name that is an
        // empty string, which every surface would then have to notice.
        testCase "an answer with nothing in it is no name at all" <| fun () ->
            Expect.isNone (Chapters.shaped "   \n  ") "nothing usable, nothing returned"
    ]

let private prWatchTests =
    let msg n = MessageId.create n |> expect
    let repo = RepoRef.create "octo/hello" |> expect
    let pr = PrRef.create repo 12 |> expect
    let ada = PeerId.create "ada" |> expect
    let bob = PeerId.create "bob" |> expect
    let snapshotOf state checks queued : PrSnapshot =
        { State = state; Title = "Add feature"; HeadSha = "abc123"; Checks = checks; Queued = queued; Mergeable = None }
    let snapshot state checks : PrSnapshot = snapshotOf state checks false
    /// The baseline as a watch that has never seen a queue reads it.
    let known state checks : PrKnown = { State = state; Checks = checks; Queue = NotQueued }
    /// ...and as one that has: auto merge armed, the last thing anybody was told.
    let queued state checks : PrKnown = { State = state; Checks = checks; Queue = Queued }
    let started state checks : SessionEvent =
        PrWatched.create (msg "w1") (Authority.ofAuthor (Principal.Peer ada)) pr (snapshot state checks)
        |> expect
        |> PrWatched
    let transitioned transition state checks : SessionEvent =
        PrTransitioned
            { MessageId = msg "t1"; Pr = pr; Transition = transition; State = state; Checks = checks; Watcher = Principal.Peer ada }
    /// The projection folds ENVELOPES, because when a watch last moved is the envelope's
    /// timestamp and nothing in a payload says it. Minute-apart stamps, so a test can tell
    /// which event a `Since` came from.
    let at (minute: int) (event: SessionEvent) : EventEnvelope<SessionEvent> =
        { EventId = EventId.fresh ()
          SessionId = SessionId.create "pr-session" |> expect
          Offset = EventOffset.create 1L |> expect
          Actor = ActorRef.System
          Timestamp = DateTimeOffset (2026, 8, 27, 10, minute, 0, TimeSpan.Zero)
          Event = event }
    let fold envelopes = envelopes |> List.fold PrWatchesProjection.applyEvent PrWatchesProjection.empty

    testList "Watched pull requests" [
        testCase "PrRef parses a number and renders canonically" <| fun () ->
            Expect.equal (PrRef.render pr) "octo/hello#12" "canonical rendering"
            Expect.isError (PrRef.create repo 0) "zero is not a PR number"
            Expect.isError (PrRef.create repo -3) "nor is a negative"

        // A draft is what a pull request is before a provider has numbered it, and these
        // are the refusals that are true of a pull request rather than of GitHub — each one
        // a round trip an agent does not spend to be told the same thing in someone else's
        // words.
        testCase "a draft keeps what it was given, trimmed" <| fun () ->
            let drafted = PrDraft.create repo "  topic  " " master " "  Add feature  " (Some "why") false |> expect
            Expect.equal drafted.Head "topic" "the head, trimmed"
            Expect.equal drafted.Base "master" "the base, trimmed"
            Expect.equal drafted.Title "Add feature" "the title, trimmed"
            Expect.equal drafted.Body (Some "why") "and the body as written"
            Expect.equal (PrDraft.render drafted) "octo/hello topic -> master" "named for a reader"

        // A description that is only whitespace is no description, and must not reach a
        // provider as one: what a squash-merge would make of it is a commit body of spaces.
        testCase "a draft whose body is nothing has no body" <| fun () ->
            let drafted = PrDraft.create repo "topic" "master" "Add feature" (Some "  \n ") false |> expect
            Expect.equal drafted.Body None "whitespace is all a body has to be, so that is not one"

        testCase "a draft with no title is refused" <| fun () ->
            Expect.isError (PrDraft.create repo "topic" "master" "   " None false) "a title nobody wrote"

        testCase "a draft with a branch nobody named is refused" <| fun () ->
            Expect.isError (PrDraft.create repo "" "master" "Add feature" None false) "no head"
            Expect.isError (PrDraft.create repo "topic" "  " "Add feature" None false) "no base"

        // A title in the wrong argument, which is what a branch with a space in it always is.
        testCase "a branch that cannot be one is refused" <| fun () ->
            Expect.isError (PrDraft.create repo "add a feature" "master" "Add feature" None false) "not a ref name"

        testCase "a head that is its own base is refused" <| fun () ->
            Expect.isError (PrDraft.create repo "master" "master" "Add feature" None false) "nothing to merge"

        testCase "a merge, a close and a reopen are each one transition" <| fun () ->
            Expect.equal
                (PrTransitions.detect (known PrOpen ChecksGreen) (snapshot PrMerged ChecksGreen))
                [ PrTransition.Merged ] "open to merged"
            Expect.equal
                (PrTransitions.detect (known PrOpen ChecksNone) (snapshot PrClosed ChecksNone))
                [ PrTransition.Closed ] "open to closed"
            Expect.equal
                (PrTransitions.detect (known PrClosed ChecksNone) (snapshot PrOpen ChecksNone))
                [ PrTransition.Reopened ] "closed to open"
            Expect.equal
                (PrTransitions.detect (known PrClosed ChecksNone) (snapshot PrMerged ChecksNone))
                [ PrTransition.Merged ] "a closed baseline learning of a merge is a merge"

        testCase "checks arriving at green or red are news; entering pending is not" <| fun () ->
            Expect.equal
                (PrTransitions.detect (known PrOpen ChecksPending) (snapshot PrOpen ChecksGreen))
                [ PrTransition.ChecksPassed ] "pending to green"
            Expect.equal
                (PrTransitions.detect (known PrOpen ChecksGreen) (snapshot PrOpen ChecksRed))
                [ PrTransition.ChecksFailed ] "green to red"
            Expect.equal
                (PrTransitions.detect (known PrOpen ChecksGreen) (snapshot PrOpen ChecksPending))
                [] "a new push resetting checks is the rhythm of work, not news"
            Expect.equal
                (PrTransitions.detect (known PrOpen ChecksGreen) (snapshot PrOpen ChecksGreen))
                [] "no movement, no news"

        testCase "a merge and a green arriving together announce both, state first" <| fun () ->
            Expect.equal
                (PrTransitions.detect (known PrOpen ChecksPending) (snapshot PrMerged ChecksGreen))
                [ PrTransition.Merged ] "checks on a PR that just left open are not announced"
            Expect.equal
                (PrTransitions.detect (known PrClosed ChecksPending) (snapshot PrOpen ChecksGreen))
                [ PrTransition.Reopened; PrTransition.ChecksPassed ] "a reopen makes its checks news again, state first"

        testCase "checks movement on a merged baseline is suppressed" <| fun () ->
            Expect.equal
                (PrTransitions.detect (known PrMerged ChecksGreen) (snapshot PrMerged ChecksRed))
                [] "CI going red on a merged PR is not actionable from here"

        testCase "auto merge arming is announced once" <| fun () ->
            Expect.equal
                (PrTransitions.detect (known PrOpen ChecksGreen) (snapshotOf PrOpen ChecksGreen true))
                [ PrTransition.Queued ] "it is on its way in with nobody needed"
            Expect.equal
                (PrTransitions.detect (queued PrOpen ChecksGreen) (snapshotOf PrOpen ChecksGreen true))
                [] "and saying so again on every poll would be noise"

        testCase "auto merge disarming on an open pull request is a stall" <| fun () ->
            // What a merge queue ejecting an entry looks like from outside: the state does
            // not move, the checks do not move, it just stops being on its way in.
            Expect.equal
                (PrTransitions.detect (queued PrOpen ChecksGreen) (snapshot PrOpen ChecksGreen))
                [ PrTransition.Stalled ] "somebody has to re-arm it"
            Expect.equal
                (PrTransitions.detect (known PrOpen ChecksGreen) (snapshot PrOpen ChecksGreen))
                [] "a pull request that was never queued has not stalled"

        testCase "a re-armed pull request is queued again" <| fun () ->
            let stalled = PrTransitions.advance (queued PrOpen ChecksGreen) PrTransition.Stalled
            Expect.equal
                (PrTransitions.detect stalled (snapshotOf PrOpen ChecksGreen true))
                [ PrTransition.Queued ] "it is again true that nobody is needed"

        testCase "a queued pull request that merges is not also reported stalled" <| fun () ->
            // It left the queue by going through it. Reporting that as a stall would file
            // the success as a failure.
            Expect.equal
                (PrTransitions.detect (queued PrOpen ChecksGreen) (snapshot PrMerged ChecksGreen))
                [ PrTransition.Merged ] "the merge is the whole news"

        testCase "a status word is the last thing that happened, worst first" <| fun () ->
            Expect.equal (PrStatus.word Queued PrOpen) "queued" "armed and waiting on machines"
            Expect.equal (PrStatus.word Stalled PrOpen) "stalled" "nobody driving"
            Expect.equal (PrStatus.word NotQueued PrOpen) "open" "the ordinary state"
            Expect.equal (PrStatus.word Queued PrMerged) "merged" "a merged PR has stopped caring what a queue thought"
            Expect.equal (PrStatus.word Queued PrClosed) "closed" "and so has a closed one"
            Expect.equal (PrStatus.worse "queued" "stalled") "stalled" "stalled wants a person more than queued"
            Expect.equal (PrStatus.worse "merged" "open") "open" "an open PR is still owed; a merged one is not"
            Expect.equal (PrStatus.worse "merged" "a word from the future") "merged" "an unknown word does not shout"

        testCase "a summary of no live pull requests says nothing at all" <| fun () ->
            // Silence is the feature. A roster line that is THERE is worth reading only
            // because a session with nothing owed does not print one.
            let merged n = PrStatus.label (PrRef.create repo n |> expect), "merged"
            Expect.equal (PrStatus.summarize []) "" "nothing watched"
            Expect.equal (PrStatus.summarize [ merged 1; merged 2 ]) "" "and nothing still owed"

        testCase "a summary names a lone pull request and counts a crowd, worst first" <| fun () ->
            let at n word = PrStatus.label (PrRef.create repo n |> expect), word
            Expect.equal (PrStatus.summarize [ at 377 "queued" ]) "#377 queued" "one is named"
            Expect.equal
                (PrStatus.summarize [ at 1 "queued"; at 2 "stalled"; at 3 "queued" ])
                "3 PRs · 1 stalled"
                "several are counted, and the count that follows is of the worst"
            Expect.equal
                (PrStatus.summarize [ at 1 "queued"; at 2 "merged"; at 3 "open" ])
                "2 PRs · 1 open"
                "a merged one is history and is not among them"
            Expect.equal
                (PrStatus.summarize [ at 1 "stalled"; at 2 PrStatus.unreachable ])
                "2 PRs · 1 unreachable"
                "a watch that cannot be read is worse news than one that stalled"

        // Both surfaces name a pull request the same way, from opposite ends: the session
        // holds the watch, the browser holds only what the query rendered.
        testCase "a pull request is labelled identically from a watch and from a rendering" <| fun () ->
            let pr = PrRef.create repo 377 |> expect
            Expect.equal (PrStatus.label pr) "#377" "the session labels it from the reference"
            Expect.equal (PrStatus.labelOf (PrRef.render pr)) "#377" "and the browser from the rendering"
            Expect.equal (PrStatus.labelOf "nothing like a pull request") "nothing like a pull request" "and passes through what it cannot read"

        // The rule both surfaces obey about a watch nobody can read, in the one place either
        // of them gets it from.
        testCase "a watch that cannot be read outranks whatever it last said" <| fun () ->
            Expect.equal
                (PrStatus.standing "#12" (Some "queued") false)
                (Some ("#12", PrStatus.unreachable))
                "unreadable wins over the state it was in"
            Expect.equal (PrStatus.standing "#12" (Some "queued") true) (Some ("#12", "queued")) "and otherwise the state stands"
            Expect.equal (PrStatus.standing "#12" None true) None "a watch nobody has looked at yet is not a standing"
            Expect.equal
                (PrStatus.standing "#12" None false)
                (Some ("#12", PrStatus.unreachable))
                "but one that cannot be read is, whether or not it was ever read"

        testCase "a watch cannot be built on the deployment's own credential" <| fun () ->
            // The rule lives on the event, where every construction goes through it: the
            // verb refuses before it looks, and the decoder refuses a stored line that says
            // it, for the same reason from the same function. A watch keeps looking as
            // somebody and wakes them; nobody is not a somebody.
            Expect.isError
                (PrWatched.create (msg "w1") (Authority.configuredBy repo CredentialFor.Deployment) pr (snapshot PrOpen ChecksNone))
                "a boot fold's authority starts no watch"
            match PrWatched.create (msg "w1") (Authority.agentFor (Principal.Peer ada)) pr (snapshot PrOpen ChecksNone) with
            | Ok watched ->
                Expect.equal (PrWatched.actor watched) ActorRef.Agent "the agent asked"
                Expect.equal (PrWatched.watcher watched) (Principal.Peer ada) "on Ada's credential"
            | Error e -> failwithf "the agent on a person's authority is exactly a watch: %s" e

        testCase "the watches projection folds start, re-watch, transition and stop" <| fun () ->
            let folded = fold [ at 0 (started PrOpen ChecksPending) ]
            Expect.equal
                folded.Watches
                [ { Pr = pr
                    Watcher = Principal.Peer ada
                    Known = (known PrOpen ChecksPending)
                    Since = DateTimeOffset (2026, 8, 27, 10, 0, 0, TimeSpan.Zero) } ]
                "a watch starts from its Initial baseline"
            let advanced =
                PrWatchesProjection.applyEvent folded (at 5 (transitioned PrTransition.ChecksPassed PrOpen ChecksGreen))
            Expect.equal
                (PrWatchesProjection.tryFind pr advanced |> Option.map (fun w -> w.Known))
                (Some (known PrOpen ChecksGreen))
                "a recorded transition advances the baseline"
            let rewatched =
                PrWatchesProjection.applyEvent
                    advanced
                    (at
                        9
                        (PrWatched.create (msg "w2") (Authority.agentFor (Principal.Peer bob)) pr (snapshot PrOpen ChecksNone)
                         |> expect
                         |> PrWatched))
            Expect.equal
                rewatched.Watches
                [ { Pr = pr
                    Watcher = Principal.Peer bob
                    Known = (known PrOpen ChecksNone)
                    Since = DateTimeOffset (2026, 8, 27, 10, 9, 0, TimeSpan.Zero) } ]
                "re-watch replaces in place, newest baseline and watcher win"
            let stopped =
                PrWatchesProjection.applyEvent
                    rewatched
                    (at 12 (PrUnwatched { MessageId = msg "w3"; Pr = pr; Actor = PeerRef ada }))
            Expect.equal stopped.Watches [] "stopped"

        testCase "a watch dates itself from the last thing that was recorded about it" <| fun () ->
            // What separates a suite still working from one that died. It moves on an
            // EVENT and never on a look: a poll that found nothing new has learned nothing
            // about when this pull request became what it is.
            let started = fold [ at 0 (started PrOpen ChecksPending) ]
            let since (proj: PrWatchesProjection) =
                PrWatchesProjection.tryFind pr proj |> Option.map (fun w -> w.Since)
            Expect.equal
                (since started)
                (Some (DateTimeOffset (2026, 8, 27, 10, 0, 0, TimeSpan.Zero)))
                "a fresh watch dates from when it began"
            let moved =
                PrWatchesProjection.applyEvent started (at 7 (transitioned PrTransition.ChecksPassed PrOpen ChecksGreen))
            Expect.equal
                (since moved)
                (Some (DateTimeOffset (2026, 8, 27, 10, 7, 0, TimeSpan.Zero)))
                "and re-dates from each transition after it"

        testCase "a restart re-announces nothing: the folded baseline already knows what was said" <| fun () ->
            // The dedupe property the durable baseline exists for. After a green was
            // recorded, folding the same log and comparing against the same green
            // snapshot detects nothing — however many times the process restarts.
            let folded =
                fold
                    [ at 0 (started PrOpen ChecksPending)
                      at 5 (transitioned PrTransition.ChecksPassed PrOpen ChecksGreen) ]
            let known = (PrWatchesProjection.tryFind pr folded |> Option.get).Known
            Expect.equal (PrTransitions.detect known (snapshot PrOpen ChecksGreen)) [] "already announced"
            Expect.equal
                (PrTransitions.detect known (snapshot PrMerged ChecksGreen))
                [ PrTransition.Merged ]
                "while a change that happened during the downtime is still detected"

        testCase "watch events read in the timeline as attributed notes" <| fun () ->
            let sessionId = SessionId.create "pr-session" |> expect
            let envelopes =
                [ PeerRef ada, started PrOpen ChecksPending
                  ActorRef.System, transitioned PrTransition.Merged PrMerged ChecksGreen
                  PeerRef ada, PrUnwatched { MessageId = msg "w9"; Pr = pr; Actor = PeerRef ada } ]
                |> List.mapi (fun i (actor, event) ->
                    { EventId = EventId.fresh ()
                      SessionId = sessionId
                      Offset = EventOffset.create (int64 (i + 1)) |> expect
                      Actor = actor
                      Timestamp = DateTimeOffset (2026, 8, 27, 10, 0, 0, TimeSpan.Zero)
                      Event = event })
            let proj, _ = ConversationProjection.applyEvents None envelopes ConversationProjection.empty
            Expect.equal
                (proj.Items |> List.map (fun i -> i.Body))
                [ "PR octo/hello#12 watched"
                  "PR octo/hello#12 merged"
                  "PR octo/hello#12 unwatched" ]
                "the notes read as sentences"
            Expect.equal
                (proj.Items |> List.map noteDetail)
                [ Some "open, checks pending"; None; None ]
                "and what the watch found is on the note, under the headline"
            Expect.equal
                (proj.Items |> List.map (fun i -> i.Author))
                [ PeerRef ada; PeerRef ada; PeerRef ada ]
                "a transition wears the watcher's name, not System's"
            Expect.isTrue
                (proj.Items |> List.forall (fun i -> match i.Kind with ConversationItemKind.ActNote _ -> true | _ -> false))
                "all notes"

        // Which acts arrive on the rail without anybody asking. Deliberately a short list:
        // a transcript where everything opens a chapter has none. A watch and its news do because
        // a watch is the reason somebody is waiting; the unwatch is not, because it is where
        // the story stops being told rather than a place worth coming back to.
        testCase "a watch and its news are chapters; letting it go is not" <| fun () ->
            let notable (item: ConversationItem) =
                match item.Kind with
                | ConversationItemKind.ActNote facts -> facts.Notable
                | ConversationItemKind.Message -> false
            let envelopes =
                [ PrWatched.create (msg "w1") (Authority.ofAuthor (Principal.Peer ada)) pr (snapshotOf PrOpen ChecksPending false)
                  |> expect
                  |> SessionEvent.PrWatched
                  SessionEvent.PrTransitioned
                    { MessageId = msg "w2"
                      Pr = pr
                      Transition = PrTransition.Merged
                      State = PrMerged
                      Checks = ChecksGreen
                      Watcher = Principal.Peer ada }
                  SessionEvent.PrUnwatched { MessageId = msg "w3"; Pr = pr; Actor = PeerRef ada } ]
                |> List.mapi (fun i event ->
                    { EventId = EventId.fresh ()
                      SessionId = SessionId.create "pr-session" |> expect
                      Offset = EventOffset.create (int64 (i + 1)) |> expect
                      Actor = ActorRef.SessionProcess
                      Timestamp = DateTimeOffset (2026, 8, 27, 10, 0, 0, TimeSpan.Zero)
                      Event = event })
            let proj, _ = ConversationProjection.applyEvents None envelopes ConversationProjection.empty
            Expect.equal (proj.Items |> List.map notable) [ true; true; false ] "watched, its news, then let go"

        testCase "a PrTransitioned on the wire is the shape it will always be" <| fun () ->
            // Pinned as a literal, not round-tripped: what a durable log needs is that the
            // codec has not changed under the lines already written.
            let pinned =
                """{"type":"prTransitioned","payload":{"messageId":"t1","pr":{"repo":"octo/hello","number":12},"transition":"merged","state":"merged","checks":"green","watcher":{"kind":"peer","peerId":"ada"}}}"""
            Expect.equal
                (Codec.fromString Codec.sessionEvent pinned |> expect)
                (transitioned PrTransition.Merged PrMerged ChecksGreen)
                "the durable form decodes to the event"
    ]

// Who is behind an act (Plan 20). The type exists because these three were loose fields
// every site re-spelled, and one site drifted: agent terminal commands recorded no owner at
// all, so Plan 08's no-borrowing rule held in two places and was absent in a third.
let private authorityTests =
    let ada = PeerId.create "ada" |> expect
    let bob = PeerId.create "bob" |> expect
    testList "Authority (Plan 20)" [

        testCase "a person's act borrows nothing, so it resolves to themselves" <| fun () ->
            let authority = Authority.ofAuthor (Principal.Peer ada)
            Expect.equal (Authority.onBehalfOf authority) None "there is no authority to state"
            Expect.equal (Authority.credential authority) (CredentialFor.Person (Principal.Peer ada)) "and it runs as its own author"

        testCase "an agent's act resolves to the authority it was built with, never to itself" <| fun () ->
            // The rule that went missing, as the only thing `agentFor` can produce: the agent
            // is the acting party and the credential is the turn human's. There is no
            // agent-authored act without one, so the omission would not compile.
            let authority = Authority.agentFor (Principal.Peer ada)
            Expect.equal (Authority.author authority) ActorRef.Agent "the agent is who acted"
            Expect.equal (Authority.credential authority) (CredentialFor.Person (Principal.Peer ada)) "on the turn human's credential"

        testCase "a stored act by the agent that names nobody is refused, not recovered" <| fun () ->
            // The decode path's one refusal, and why it is one: an agent act with no owner is
            // not a value `Authority` can hold any more. It used to be "recovered" as the
            // agent on nobody's credential, and every reader then had a degraded state to
            // answer for — the wake, the dispatch, the forward. A stored line that says it
            // now fails the line, which is the honest outcome for an act this version could
            // never have run.
            Expect.isError (Authority.recover ActorRef.Agent None) "nobody is not an owner"
            Expect.equal
                (Authority.recover ActorRef.Agent (Some (Principal.Peer ada)))
                (Ok (Authority.agentFor (Principal.Peer ada)))
                "and with one named, it is the agent act it says"

        testCase "a stored act by a person recovers as their own, and cannot borrow" <| fun () ->
            Expect.equal
                (Authority.recover (PeerRef ada) None)
                (Ok (Authority.ofAuthor (Principal.Peer ada)))
                "a person's act is their own"
            Expect.isError
                (Authority.recover (PeerRef ada) (Some (Principal.Peer bob)))
                "a person acting on somebody else's authority is not a thing the log can say"

        testCase "a repo file's act resolves to whoever triggered the fold, or the deployment" <| fun () ->
            // A repo's file at boot acts on nobody's authority, which is a real state: the
            // deployment's own credentials, the session's and the local one, and nothing
            // else — never the file standing in as if it held one.
            let repo = RepoRef.create "octo/hello" |> expect
            Expect.equal
                (Authority.credential (Authority.configuredBy repo CredentialFor.Deployment))
                CredentialFor.Deployment
                "the boot fold"
            Expect.equal
                (Authority.recover (ActorRef.Configured repo) (Some (Principal.Peer ada)))
                (Ok (Authority.configuredBy repo (CredentialFor.Person (Principal.Peer ada))))
                "and a triggered one, on the person who triggered it"
            for actor in [ ActorRef.System; ActorRef.SessionProcess ] do
                Expect.isError (Authority.recover actor None) (sprintf "%s authors no acts" (ActorRef.token actor))
    ]

/// A catalogue cache over a stub provider: a frozen clock, a ten-minute window, and one
/// model. Hoisted because three of the cases below differ only in what they MOVE — the
/// credential, the clock, or the kept answer itself — and a setup written out three times
/// hides which line is the case.
let private keeping (onAsk: unit -> unit) (keyOf: CredentialFor -> string option) : ModelCatalogueCache =
    ModelCatalogue.keyed
        (fun () -> DateTimeOffset (2026, 1, 1, 0, 0, 0, TimeSpan.Zero))
        (TimeSpan.FromMinutes 10.0)
        keyOf
        (fun _ ->
            async {
                onAsk ()
                return Ok [ AgentModel.create (ModelId.create "a-model" |> expect) "A" ]
            })

/// The model vocabulary: the id's invariant, and the rules the session's catalogue keeps
/// — an answer belongs to the credential it was fetched on, it does not outlive the
/// provider's own list for long, and a failure is never kept at all.
let private modelTests =
    testList "Models" [
        testCase "a model id is trimmed, and refuses what no provider could have named" <| fun () ->
            // This value is handed to a spawned process as an option and arrives from a
            // register any peer may write, so the refusals are the point rather than tidiness.
            Expect.equal
                (ModelId.value (ModelId.create "  a-model  " |> expect))
                "a-model"
                "surrounding whitespace is trimmed"
            Expect.isError (ModelId.create "  ") "blank is not a model"
            Expect.isError (ModelId.create "two words") "inner whitespace is not a model id"
            Expect.isError (ModelId.create "a\nmodel") "nor is a control character"
            Expect.isError (ModelId.create (String.replicate 300 "x")) "nor is a document"

        testCase "a model without a label is offered by its id, never blank" <| fun () ->
            // A picker row with nothing in it reads as a control that failed to load.
            let id = ModelId.create "a-model" |> expect
            Expect.equal (AgentModel.create id "").Name "a-model" "the id stands in for a name"

        testCase "the catalogue is ordered for a person, not for the provider" <| fun () ->
            let of' id name = AgentModel.create (ModelId.create id |> expect) name
            let ordered = ModelCatalogue.ordered [ of' "z" "Beta"; of' "a" "alpha" ]
            Expect.equal
                (ordered |> List.map (fun m -> m.Name))
                [ "alpha"; "Beta" ]
                "by name, case-insensitively"

        // The kept catalogue. Each case moves ONE of the three things that can make a kept
        // answer stop being one, and asserts that the lookup notices exactly that.
        testCaseAsync "an answer is kept while the credential and the clock both stand still" <|
            async {
                let mutable asked = 0
                let cache = keeping (fun () -> asked <- asked + 1) (fun _ -> Some "alice")
                let! first = cache.List CredentialFor.Deployment
                let! second = cache.List CredentialFor.Deployment
                Expect.equal asked 1 "the provider is asked once"
                Expect.equal second first "and every later reader gets the same answer"
            }

        testCaseAsync "a failed lookup is not kept, so signing in later fills the picker" <|
            async {
                // The failure a session actually hits is "nothing is connected yet", and the
                // remedy happens in the panel above the picker. Caching that would leave the
                // picker permanently empty for a session that fixes it a minute later.
                let mutable asked = 0
                let cache =
                    ModelCatalogue.keyed
                        (fun () -> DateTimeOffset (2026, 1, 1, 0, 0, 0, TimeSpan.Zero))
                        (TimeSpan.FromMinutes 10.0)
                        (fun _ -> Some "alice")
                        (fun _ ->
                            async {
                                asked <- asked + 1
                                if asked = 1 then return Error "not connected"
                                else return Ok [ AgentModel.create (ModelId.create "a-model" |> expect) "A" ]
                            })
                let! failed = cache.List CredentialFor.Deployment
                Expect.isError failed "the first ask reports why it could not"
                let! second = cache.List CredentialFor.Deployment
                Expect.isOk second "and the next ask tries again"
            }

        testCaseAsync "an answer kept for one credential is never served to another" <|
            async {
                // A catalogue is a fact about a credential. Kept as a fact about the session,
                // the first person to open the picker filled it for everybody — and the next
                // person read a list their own credential had never been asked for.
                let mutable asked = 0
                let mutable who = "alice"
                let cache = keeping (fun () -> asked <- asked + 1) (fun _ -> Some who)
                let! _ = cache.List CredentialFor.Deployment
                who <- "bob"
                let! _ = cache.List CredentialFor.Deployment
                Expect.equal asked 2 "a different credential is a different question"
            }

        testCaseAsync "a kept answer expires, so a model released mid-session is offered" <|
            async {
                // Nothing tells a session that a provider shipped a model, and a session here
                // can be up for days. Kept for ever, the only cure was a restart.
                let mutable asked = 0
                let mutable at = DateTimeOffset (2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
                let cache =
                    ModelCatalogue.keyed
                        (fun () -> at)
                        (TimeSpan.FromMinutes 10.0)
                        (fun _ -> Some "alice")
                        (fun _ ->
                            async {
                                asked <- asked + 1
                                return Ok [ AgentModel.create (ModelId.create "a-model" |> expect) "A" ]
                            })
                let! _ = cache.List CredentialFor.Deployment
                at <- at.AddMinutes 9.0
                let! _ = cache.List CredentialFor.Deployment
                Expect.equal asked 1 "inside the window the kept answer stands"
                at <- at.AddMinutes 2.0
                let! _ = cache.List CredentialFor.Deployment
                Expect.equal asked 2 "past it the provider is asked again"
            }

        testCaseAsync "forgetting drops the answer, so a re-sign-in is not served the old one" <|
            async {
                // The one move the key cannot see: the same target signed in again, behind a
                // different account. Whoever holds that state says so.
                let mutable asked = 0
                let cache = keeping (fun () -> asked <- asked + 1) (fun _ -> Some "alice")
                let! _ = cache.List CredentialFor.Deployment
                cache.Forget ()
                let! _ = cache.List CredentialFor.Deployment
                Expect.equal asked 2 "what was forgotten is asked for again"
            }
    ]

/// The spawn contract (Plan 27): one envelope, minted by the Manager, decoded once by the
/// session. Each case pins one promise of that contract.
let private launchTests =
    testList "Launch envelope (Plan 27)" [
        testCase "a launch round-trips through the variable" <| fun () ->
            let launch =
                { Session = SessionId.create "sess-1" |> expect
                  DataDir = "/data/sess-1"
                  Port = 0
                  Control = Some { Url = "http://127.0.0.1:8321"; Secret = "s3cret" }
                  ParentGuard = true }
            Expect.equal (Launch.parse (Launch.encode launch)) (Ok launch) "what the Manager mints is what the session reads"

        testCase "a launch with no Manager round-trips too" <| fun () ->
            // The unsupervised shape has to survive the wire, not just the default: a
            // session spawned without a control leg is an ordinary session.
            let launch = { Launch.unlaunched with Session = SessionId.create "sess-2" |> expect }
            Expect.equal (Launch.parse (Launch.encode launch)) (Ok launch) "an absent control leg decodes as None"

        testCase "an absent variable is the unlaunched session, not an error" <| fun () ->
            // `yession-session` run by hand still runs.
            Expect.equal (Launch.parse "") (Ok Launch.unlaunched) "blank means nobody launched us"

        testCase "an unlaunched session has no Manager to report to" <| fun () ->
            Expect.equal Launch.unlaunched.Control None "there is nothing to authenticate against"

        testCase "an unlaunched session does not die with a parent it never had" <| fun () ->
            Expect.isFalse Launch.unlaunched.ParentGuard "nothing closes its stdin"

        testCase "a malformed envelope fails the boot" <| fun () ->
            // The defect this whole shape exists to stop: the old code fabricated a session
            // id when the variable was missing a field, so a launch that forgot to say who
            // it was booted anyway, as somebody else.
            Expect.isError (Launch.parse "{\"dataDir\":\"/d\",\"port\":0}") "a launch with no session id is a contract disagreement"

        testCase "an envelope naming an unusable session id fails the boot" <| fun () ->
            // The id names a container and a volume verbatim, so the decoder holds
            // `SessionId`'s rule rather than deferring it to a later, less legible failure.
            Expect.isError
                (Launch.parse "{\"session\":\"bad id\",\"dataDir\":\"/d\",\"port\":0}")
                "a non-Docker-safe id is refused where it arrives"

        testCase "the launch variable is not something a repo may author" <| fun () ->
            // It carries the control secret. `Sandboxes.hostBaseline` is an allowlist, so a
            // sandboxed command never sees it (Phase2); this pins the other half — the name
            // is under the reserved prefix `yession.yaml` refuses.
            Expect.isTrue (Launch.Variable.StartsWith "YESSION_") "it lives under the reserved prefix"
    ]

// -----------------------------------------------------------------------------
// `yession.yaml` (Plan 27): the vocabulary, the decoder and the algebra.
//
// Driven from JSON literals rather than YAML, because YAML is a superset of JSON and the
// decoder is parser-free — which is what lets every one of these run on BOTH runtimes in
// the cheap tier, with no dependency on whatever reads the file off disk.
// -----------------------------------------------------------------------------

let private repo (raw: string) = RepoRef.create raw |> expect
let private sandboxName (raw: string) = SandboxName.create raw |> expect

/// The two views DIFFER on purpose: a resolution against the wrong one then fails the
/// assertion instead of passing by coincidence of equal strings.
let private checkout (inSandbox: string) : CheckoutViews option =
    Some { InSandbox = inSandbox; OnHost = "/on-host" + inSandbox }
let private configTests =
    testList "yession.yaml (Plan 27)" [

        testCase "a declaration carries what start_work_sandbox can already be told" <| fun () ->
            let file =
                ConfigFile.parse """
                    { "version": 2,
                      "sandboxes": {
                        "dev": {
                          "container": { "image": "node:24", "cmd": "npm start" },
                          "workdir": "./app",
                          "env": { "NODE_ENV": "development" },
                          "uses": [ "npm" ],
                          "files": { ".config/tool/first-run": "" },
                          "forward": [ "github" ] } } }"""
                |> expect
            let dev = file.Sandboxes |> Map.find (sandboxName "dev")
            let container = dev.Container |> Option.get
            Expect.equal container.Image (Some { Name = "node"; Tag = Some "24" }) "the image splits on its tag"
            Expect.equal dev.WorkingDirectory (Some "./app") "the workdir is the repo's own"
            Expect.equal container.Command (Some "npm start") "the sandbox's process"
            Expect.equal (dev.Uses |> List.map ResourceName.value) [ "npm" ] "the resources it selects"
            Expect.equal dev.Forward [ "github" ] "the credentials by name"

        // `setup:` is a repo MAKING its environment ready rather than describing it and
        // hoping. Deliberately not `container.cmd`, which is beside it in the same file and
        // means something else: the container's own process, one that takes the sandbox
        // down when it exits.
        testCase "a setup command reaches the spec the sandbox is built from" <| fun () ->
            let file =
                ConfigFile.parse """
                    { "version": 2,
                      "sandboxes": {
                        "dev": {
                          "container": { "image": "nixos/nix" },
                          "setup": "nix develop --impure --command true" } } }"""
                |> expect
            let dev = file.Sandboxes |> Map.find (sandboxName "dev")
            Expect.equal dev.Setup (Some "nix develop --impure --command true") "the command as written"
            let request = SandboxDecl.toRequest None dev |> expect
            Expect.equal request.Spec.Setup (Some "nix develop --impure --command true") "and it survives to the spec"

        // A name cannot say what a sandbox is for, and two sandboxes identical on every field
        // a machine reads are told apart only by what a person meant. Measured: shown `dev`
        // and `gate` and asked to run tests, an agent picked `gate`, which holds a terminal
        // for minutes.
        testCase "a sandbox can say what it is for" <| fun () ->
            let file =
                ConfigFile.parse """
                    { "version": 2,
                      "sandboxes": {
                        "dev": {
                          "description": "day-to-day work — the full toolchain" } } }"""
                |> expect
            let dev = file.Sandboxes |> Map.find (sandboxName "dev")
            Expect.equal dev.Description (Some "day-to-day work — the full toolchain") "the words as written"

        // A repo saying where the session's checkouts should appear in its own container.
        testCase "a sandbox can say where it wants the checkouts" <| fun () ->
            let file =
                ConfigFile.parse """{ "version": 2, "sandboxes": { "dev": { "repos": "/src" } } }"""
                |> expect
            let dev = file.Sandboxes |> Map.find (sandboxName "dev")
            Expect.equal dev.Repos (Some "/src") "the target as written"

        // The mirror of `workdir:`, which refuses an absolute path because it names somewhere
        // inside a checkout. This names somewhere inside a CONTAINER, where a relative path
        // has no root to be relative to — so the refusal runs the other way, and says so.
        testCase "a relative checkouts path is refused, because it would have no root" <| fun () ->
            match ConfigFile.parse """{ "version": 2, "sandboxes": { "dev": { "repos": "src" } } }""" with
            | Ok _ -> failwith "a relative target inside a container resolves against nothing"
            | Error reason -> Expect.stringContains reason "absolute" "it says which way it must be written"

        testCase "a checkouts path that climbs is refused" <| fun () ->
            match ConfigFile.parse """{ "version": 2, "sandboxes": { "dev": { "repos": "/src/../etc" } } }""" with
            | Ok _ -> failwith "a climb means one thing to its author and another to what resolves it"
            | Error reason -> Expect.stringContains reason "climb" "it says what is wrong with it"

        // Absent and blank are one state. A repo that wrote `description:` and left it empty
        // has said nothing, and a note reading "started sandbox dev (docker) — " would be
        // this file's punctuation leaking onto a timeline.
        testCase "a description of nothing but space is no description" <| fun () ->
            let file =
                ConfigFile.parse """{ "version": 2, "sandboxes": { "dev": { "description": "   " } } }"""
                |> expect
            let dev = file.Sandboxes |> Map.find (sandboxName "dev")
            Expect.isNone dev.Description "nothing said is nothing carried"

        testCase "a sandbox that declares no setup asks for none" <| fun () ->
            let file = ConfigFile.parse """{ "version": 2, "sandboxes": { "dev": {} } }""" |> expect
            let dev = file.Sandboxes |> Map.find (sandboxName "dev")
            Expect.equal dev.Setup None "saying nothing is not saying to run nothing"
            Expect.equal ((SandboxDecl.toRequest None dev |> expect).Spec.Setup) None "and the spec agrees"

        // A file is authored by whoever can push to the repo, so a path it writes is that
        // author naming a place on somebody else's machine. Refused where it is WRITTEN,
        // because that is where the person who can fix it is standing.
        testCase "a workdir that leaves the checkout is refused, and says which way" <| fun () ->
            let refusal workdir =
                match ConfigFile.parse (sprintf """{ "version": 2, "sandboxes": { "dev": { "workdir": "%s" } } }""" workdir) with
                | Ok _ -> failwithf "expected a refusal for '%s'" workdir
                | Error e -> e
            Expect.isTrue ((refusal "/etc").Contains "absolute") "an absolute path names another machine's tree"
            Expect.isTrue ((refusal "../elsewhere").Contains "climbs out") "and a relative one can mean the same thing"
            Expect.isTrue ((refusal "app/../../elsewhere").Contains "climbs out") "wherever the segment sits"

        // A seeded file is the one thing a repo writes that is not a name, and it is safe
        // ONLY while it cannot leave the home this session made for that sandbox. So
        // leaving is what is refused, in every spelling that means it.
        testCase "a seeded file that leaves the sandbox's home is refused, in every spelling" <| fun () ->
            let refusal path =
                match ConfigFile.parse (sprintf """{ "version": 2, "sandboxes": { "dev": { "files": { "%s": "x" } } } }""" path) with
                | Ok _ -> failwithf "expected a refusal for '%s'" path
                | Error e -> e
            Expect.isTrue ((refusal "/etc/passwd").Contains "absolute") "an absolute path is another tree"
            Expect.isTrue ((refusal "../escape").Contains "outside") "and so is climbing out"
            Expect.isTrue ((refusal "a/../../escape").Contains "outside") "wherever the segment sits"
            Expect.isTrue ((refusal "a//b").Contains "empty") "an empty segment could mean either"
            Expect.isTrue ((refusal "dir/").Contains "file name") "and a directory is not a file"

        // The other half: what a repo legitimately asks for survives to the spec that
        // materialises it. Without this the refusals above could pass with the feature
        // decoding to nothing at all.
        testCase "a seeded file reaches the spec the sandbox is built from" <| fun () ->
            let file =
                ConfigFile.parse """
                    { "version": 2,
                      "sandboxes": { "dev": { "files": { ".local/share/NuGet/Migrations/1": "" } } } }"""
                |> expect
            let decl = file.Sandboxes |> Map.find (sandboxName "dev")
            let request = SandboxDecl.toRequest (checkout "/repos/octo/hello") decl |> expect
            Expect.equal
                (request.Spec.Files |> Map.toList |> List.map (fun (path, content) -> HomePath.value path, content))
                [ ".local/share/NuGet/Migrations/1", "" ]
                "the path and its content, as written"

        // `HostPath` exists and the SESSION uses it — that is how the repos directory reaches
        // a container. `NamedVolume` exists and the OPERATOR grants it — a docker volume is
        // host-global, so the same name is the same volume in every session's containers,
        // and a file that could name one could read and seed another session's state. What
        // a file may say is only `workspace`: the one volume that is genuinely its own.
        testCase "a volume may name only the workspace — not a host path, not a shared name" <| fun () ->
            let volume source =
                ConfigFile.parse (
                    sprintf
                        """{ "version": 2, "sandboxes": { "dev": { "container": { "volumes": [ { "source": "%s", "target": "/w" } ] } } } }"""
                        source)
            for source in [ "/var/run/docker.sock"; "cache" ] do
                match volume source with
                | Ok _ -> failwithf "'%s' was accepted, and a file may not reach it" source
                | Error e ->
                    Expect.isTrue (e.Contains "workspace") "it says what a file may say instead"
            match volume "cache" with
            | Ok _ -> failwith "unreachable"
            | Error e -> Expect.isTrue (e.Contains "operator") "a shared volume is pointed at the operator's file"
            let mountsOf file =
                (file |> expect : ConfigFile).Sandboxes
                |> Map.find (sandboxName "dev")
                |> fun decl -> (decl.Container |> Option.get).Mounts |> List.map (fun m -> m.Source)
            Expect.equal (mountsOf (volume "workspace")) [ SessionWorkspace ] "its own checkout, by name"

        // The two postures a selection has. `uses` is a need — a missing name refuses,
        // that refusal is the ceiling — and `wants` is an optimisation by declaration:
        // selected where the host offers it, silently absent where not, so one file
        // works cold anywhere and warm where the operator chose.
        testCase "a wants selection decodes and rides the request beside uses" <| fun () ->
            let file =
                ConfigFile.parse
                    """{ "version": 2,
                         "sandboxes": { "dev": { "uses": [ "npm" ], "wants": [ "warm-store" ] } } }"""
                |> expect
            let decl = file.Sandboxes |> Map.find (sandboxName "dev")
            let request = SandboxDecl.toRequest (checkout "/repos/octo/hello") decl |> expect
            Expect.equal (request.Spec.Uses |> List.map ResourceName.value) [ "npm" ] "the need"
            Expect.equal (request.Spec.Wants |> List.map ResourceName.value) [ "warm-store" ] "and the wish, apart"

        testCase "a want is selected where offered and absent where not, uses untouched" <| fun () ->
            // Built through the real loader so `selected` is exercised against the one
            // shape it ever sees in production.
            let profile =
                OperatorProfile.parse
                    """{ "version": 1, "resources": { "offered": { "endpoint": "example.com" } } }"""
                |> expect
            let name raw = ResourceName.create raw |> expect
            Expect.equal
                (ResourceProfile.selected profile.Resources [ name "needed" ] [ name "offered"; name "absent" ])
                [ name "needed"; name "offered" ]
                "the offered want joins the selection; the absent one vanishes; the need stays whether or not it resolves later"

        // `grants` is the whole answer — leaves plus the wants-only subset the policy may
        // drop where the HOST cannot realise it. Which leaf is droppable is decided here,
        // in the domain, which is what makes the posture's second "absent" half testable
        // without a composition root.
        testCase "a want's leaves are marked droppable; anything uses reaches is not" <| fun () ->
            let profile =
                OperatorProfile.parse
                    """{ "version": 1,
                         "resources": {
                           "tools": { "mount": { "from": "/opt/tools" } },
                           "warm-store": { "volume": { "name": "warm", "at": "/nix" } } } }"""
                |> expect
            let name raw = ResourceName.create raw |> expect
            let leaves, optional =
                ResourceProfile.grants profile.Resources [] [ name "tools" ] [ name "warm-store" ] |> expect
            Expect.isTrue (leaves |> List.contains (Volume ("warm", "/nix"))) "the offered want is held"
            Expect.equal optional (Set.ofList [ Volume ("warm", "/nix") ]) "and only the want's leaf is droppable"

        testCase "a leaf both used and wanted is not droppable" <| fun () ->
            let profile =
                OperatorProfile.parse
                    """{ "version": 1,
                         "resources": { "warm-store": { "volume": { "name": "warm", "at": "/nix" } } } }"""
                |> expect
            let name raw = ResourceName.create raw |> expect
            let _, optional =
                ResourceProfile.grants profile.Resources [] [ name "warm-store" ] [ name "warm-store" ] |> expect
            Expect.equal optional Set.empty "something needs it, so withholding it must still refuse"

        testCase "a conflict between a use and a want still refuses" <| fun () ->
            let profile =
                OperatorProfile.parse
                    """{ "version": 1,
                         "resources": {
                           "cache-ro": { "mount": { "from": "/cache" } },
                           "cache-rw": { "mount": { "from": "/cache", "mode": "write" } } } }"""
                |> expect
            let name raw = ResourceName.create raw |> expect
            match ResourceProfile.grants profile.Resources [] [ name "cache-ro" ] [ name "cache-rw" ] with
            | Ok _ -> failwith "expected the conflicting pair to refuse"
            | Error e -> Expect.isTrue (e.Contains "/cache") (sprintf "the refusal names the colliding path, said: %s" e)

        // The file's whole claim: it says nothing a command could not be told. So what a
        // declaration becomes is the ask itself, with the one thing a file cannot know —
        // where the session put the checkout — filled in.
        testCase "a declaration becomes the ask, against this session's checkout" <| fun () ->
            let decl =
                (ConfigFile.parse """
                    { "version": 2,
                      "sandboxes": {
                        "dev": {
                          "container": { "image": "node:24" },
                          "workdir": "./app",
                          "env": { "NODE_ENV": "development" },
                          "uses": [ "npm" ],
                          "uses": [ "npm" ],
                          "forward": [ "github" ] } } }"""
                 |> expect).Sandboxes
                |> Map.find (sandboxName "dev")
            let request = SandboxDecl.toRequest (checkout "/data/repos/octo/hello") decl |> expect
            Expect.equal request.Spec.WorkingDirectory (Some "/data/repos/octo/hello/app") "the workdir is under the checkout"
            Expect.equal (request.Spec.Uses |> List.map ResourceName.value) [ "npm" ] "the resources it selects"
            Expect.equal request.Forward [ "github" ] "the credentials by name"
            Expect.equal
                request.Spec.Runtime
                (Container { ContainerSpec.defaults with Image = Some { Name = "node"; Tag = Some "24" } })
                "and the container it named"

        testCase "the checkout itself is a workdir, however it is written" <| fun () ->
            for written in [ "."; "./"; "app/.." ] do
                let decl = { SandboxDecl.empty with WorkingDirectory = Some written }
                Expect.equal
                    (SandboxDecl.toRequest (checkout "/data/repos/octo/hello") decl |> expect).Spec.WorkingDirectory
                    (Some "/data/repos/octo/hello")
                    (sprintf "'%s' is the checkout" written)

        // The downstream half of the workdir rule. The decoder refuses a climbing path where
        // a person can fix it; this is what a declaration arriving some other way still
        // cannot do, because the arithmetic here has no answer above the checkout: climbing
        // is CLAMPED there, so `../../etc` names the same directory `etc` does.
        testCase "no declaration can name a directory above its checkout" <| fun () ->
            let resolved written =
                (SandboxDecl.toRequest (checkout "/data/repos/octo/hello") { SandboxDecl.empty with WorkingDirectory = Some written } |> expect)
                    .Spec.WorkingDirectory
            Expect.equal (resolved "../..") (Some "/data/repos/octo/hello") "climbing runs out at the checkout"
            Expect.equal (resolved "../../etc") (Some "/data/repos/octo/hello/etc") "and what follows lands inside it"
            Expect.equal (resolved "a/../../../etc") (Some "/data/repos/octo/hello/etc") "however far it climbed first"

        // The build context is read on the machine running the session — the daemon client
        // streams it from this filesystem — so a context a repo could point anywhere is
        // arbitrary host-file read, lifted into an image the repo's own sandbox then opens.
        // Same rule as `workdir`, refused where the person who can fix it is standing.
        testCase "a build context may not leave the checkout, however it is written" <| fun () ->
            let parsed build =
                ConfigFile.parse (
                    sprintf
                        """{ "version": 2, "sandboxes": { "dev": { "container": { "build": %s } } } }"""
                        build)
            match parsed "\"/etc\"" with
            | Ok _ -> failwith "an absolute context was accepted"
            | Error e -> Expect.isTrue (e.Contains "absolute") (sprintf "refused as absolute, said: %s" e)
            match parsed """{ "context": "../../home" }""" with
            | Ok _ -> failwith "a climbing context was accepted"
            | Error e -> Expect.isTrue (e.Contains "climbs") (sprintf "refused as climbing, said: %s" e)
            match parsed """{ "context": ".", "dockerfile": "../Dockerfile" }""" with
            | Ok _ -> failwith "a climbing dockerfile was accepted"
            | Error e -> Expect.isTrue (e.Contains "climbs") (sprintf "the dockerfile is under the same rule, said: %s" e)

        // One declaration, two addresses: the workdir is acted on INSIDE the sandbox and
        // the build context is read on the HOST, so each resolves against its reader's
        // view of the same checkout — which is what `CheckoutViews` carries both for.
        testCase "a build context resolves against the host's view; the workdir against the sandbox's" <| fun () ->
            let decl =
                (ConfigFile.parse """
                    { "version": 2,
                      "sandboxes": {
                        "dev": { "container": { "build": { "context": "infra", "dockerfile": "Dockerfile.dev" } },
                                 "workdir": "app" } } }"""
                 |> expect).Sandboxes
                |> Map.find (sandboxName "dev")
            let request = SandboxDecl.toRequest (checkout "/repos/octo/hello") decl |> expect
            Expect.equal request.Spec.WorkingDirectory (Some "/repos/octo/hello/app") "the sandbox's address"
            match request.Spec.Runtime with
            | Container { Build = Some build } ->
                Expect.equal build.ContextPath "/on-host/repos/octo/hello/infra" "the host's address"
                Expect.equal build.DockerfilePath (Some "Dockerfile.dev") "the dockerfile stays relative — the daemon resolves it inside the streamed context"
            | other -> failwithf "expected a build, got %A" other

        // The downstream half, mirroring the workdir clamp: a build spec arriving some
        // other way still cannot name a directory above the checkout.
        testCase "no build context can name a directory above its checkout" <| fun () ->
            let decl =
                { SandboxDecl.empty with
                    Container =
                        Some { ContainerSpec.defaults with Build = Some { ContextPath = "../../../etc"; DockerfilePath = None } } }
            match (SandboxDecl.toRequest (checkout "/repos/octo/hello") decl |> expect).Spec.Runtime with
            | Container { Build = Some build } ->
                Expect.equal build.ContextPath "/on-host/repos/octo/hello/etc" "climbing is clamped at the checkout"
            | other -> failwithf "expected a build, got %A" other

        testCase "a build on a sandbox no repo declared is refused" <| fun () ->
            let decl =
                { SandboxDecl.empty with
                    Container = Some { ContainerSpec.defaults with Build = Some { ContextPath = "."; DockerfilePath = None } } }
            match SandboxDecl.toRequest None decl with
            | Ok _ -> failwith "expected a refusal"
            | Error e -> Expect.isTrue (e.Contains "checkout") (sprintf "it says what the context had nothing to be relative to, said: %s" e)

        // A session's own sandbox has no checkout for a relative path to be relative to, so
        // there is nothing to resolve against and nothing honest to invent.
        testCase "a workdir on a sandbox no repo declared is refused, naming the verb that does move one" <| fun () ->
            match SandboxDecl.toRequest None { SandboxDecl.empty with WorkingDirectory = Some "./app" } with
            | Ok _ -> failwith "expected a refusal"
            | Error e ->
                Expect.isTrue (e.Contains "./app") "it names the path"
                Expect.isTrue (e.Contains "set_shell_profile") "and what does move where terminals start"

        testCase "a declaration with no workdir needs no checkout" <| fun () ->
            Expect.equal
                (SandboxDecl.toRequest None { SandboxDecl.empty with Forward = [ "github" ] } |> expect)
                { SandboxRequest.defaults with Forward = [ "github" ] }
                "which is every ask the agent's own tool can make"

        // What crosses the command gate is a declaration, so the gate's args are bounded by
        // the FILE's schema rather than by a second one: whatever is written here, the file
        // must be willing to read back.
        testCase "a declaration written for the gate reads back as itself" <| fun () ->
            let declared =
                (ConfigFile.parse """
                    { "version": 2,
                      "sandboxes": {
                        "dev": {
                          "container":
                            { "image": "node:24",
                              "cmd": "npm start",
                              "volumes": [ { "source": "workspace", "target": "/w", "mode": "ro" } ] },
                          "workdir": "app",
                          "env": { "NODE_ENV": "development", "DB": { "secret": "db-url" } },
                          "uses": [ "npm" ],
                          "uses": [ "npm" ],
                          "setup": "npm ci",
                          "forward": [ "github" ] } } }"""
                 |> expect).Sandboxes
                |> Map.find (sandboxName "dev")
            Expect.equal
                (ConfigFile.parseSandbox (SandboxDecl.encode declared) |> expect)
                declared
                "every field survives the trip"

        // The point of the round trip being through the FILE's schema: a host path is
        // refused there, so it cannot cross the gate either — one rule, not two.
        testCase "a host-path volume cannot be written back through the gate" <| fun () ->
            let smuggled =
                { SandboxDecl.empty with
                    Container =
                        Some
                            { ContainerSpec.defaults with
                                Mounts = [ { Source = HostPath "/var/run/docker.sock"; Target = "/s"; Mode = ReadWrite } ] } }
            Expect.isError
                (ConfigFile.parseSandbox (SandboxDecl.encode smuggled))
                "the schema refuses on the way back in, wherever the declaration came from"

        // Saying nothing is not asking to be confined — what nothing means is the backend's
        // answer, and `forBackend` is where the two authors meet.
        testCase "a declaration with no container asks for no container, not for confinement" <| fun () ->
            Expect.equal
                (SandboxDecl.toRequest (checkout "/checkout") SandboxDecl.empty |> expect)
                SandboxRequest.defaults
                "an empty declaration is the ask that names nothing in particular"

        testCase "a sandbox cannot ask for a process without a container to run it in" <| fun () ->
            // The point of the runtime union. `cmd` is not a sandbox key at all — it lives
            // inside `container`, so "a confined sandbox with a process" is not a state the
            // file can express and not one any decoder has to refuse.
            match ConfigFile.parse """{ "version": 2, "sandboxes": { "dev": { "cmd": "npm start" } } }""" with
            | Ok _ -> failwith "expected a refusal"
            | Error e ->
                Expect.isTrue (e.Contains "cmd") "it names the key that has no home here"
                Expect.isTrue (e.Contains "container") "and lists the one that does"

        testCase "a sandbox with no container block asks for no container" <| fun () ->
            // `None` is the repo saying nothing, not the repo asking to be confined — what
            // that means is the backend's answer, not this file's.
            let file = ConfigFile.parse """{ "version": 2, "sandboxes": { "dev": { "uses": [ "npm" ] } } }""" |> expect
            Expect.equal (file.Sandboxes |> Map.find (sandboxName "dev")).Container None "nothing was asked for"

        testCase "a secret is named, never carried" <| fun () ->
            // The whole reason `env` has two forms. A file that could hold a value would be
            // a file somebody commits a password into.
            let file =
                ConfigFile.parse """
                    { "version": 2,
                      "sandboxes": { "dev": { "env": { "DATABASE_URL": { "secret": "db-url" } } } } }"""
                |> expect
            let dev = file.Sandboxes |> Map.find (sandboxName "dev")
            let expected = SecretName.create "db-url" |> expect
            Expect.equal (dev.EnvironmentVariables |> Map.tryFind "DATABASE_URL") (Some (SecretRef expected))
                "it decodes to a reference the type cannot put a value in"

        testCase "a file may not set anything under the reserved prefix" <| fun () ->
            // YESSION_LAUNCH is custody of the session's secrets; YESSION_BIN_* names a
            // binary this host executes. Refused as a prefix so the list never has to be
            // re-decided.
            let refused =
                ConfigFile.parse """
                    { "version": 2, "sandboxes": { "dev": { "env": { "YESSION_BIN_BWRAP": "/tmp/evil" } } } }"""
            Expect.isError refused "a repo cannot name the host's bubblewrap"

        testCase "the reserved refusal says which variable" <| fun () ->
            // A refusal nobody can act on gets worked around rather than fixed.
            match ConfigFile.parse """
                    { "version": 2, "sandboxes": { "dev": { "env": { "YESSION_LAUNCH": "x" } } } }""" with
            | Ok _ -> failwith "expected a refusal"
            | Error e -> Expect.isTrue (e.Contains "YESSION_LAUNCH") "it names the variable it refused"

        // A file with one sandbox in it can only mean one thing by `$.sandboxes.env`. A file
        // with ten cannot, and the address was the same for all of them — so the refusal
        // named a key without naming which sandbox wrote it.
        testCase "a refusal is addressed to the sandbox that caused it" <| fun () ->
            match ConfigFile.parse """
                    { "version": 2,
                      "sandboxes": { "dev": { "workdir": "." },
                                     "gate": { "env": { "YESSION_LAUNCH": "x" } } } }""" with
            | Ok _ -> failwith "expected a refusal"
            | Error e ->
                // The refusal itself goes in the message: a bare `expected true` about a
                // string nobody can see is the failure that costs a second run to read.
                Expect.isTrue (e.Contains "sandboxes.gate") (sprintf "the path names the sandbox at fault, said: %s" e)
                Expect.isFalse (e.Contains "sandboxes.dev") (sprintf "and not the one that was fine, said: %s" e)

        testCase "an ordinary variable still passes" <| fun () ->
            // The guard above must not have made `env` useless.
            let file =
                ConfigFile.parse """
                    { "version": 2, "sandboxes": { "dev": { "env": { "NODE_ENV": "test" } } } }"""
                |> expect
            let dev = file.Sandboxes |> Map.find (sandboxName "dev")
            Expect.equal (dev.EnvironmentVariables |> Map.tryFind "NODE_ENV") (Some (PlainValue "test"))
                "a plain value is a plain value"

        testCase "an unknown key is refused, not skipped" <| fun () ->
            // A typo that decodes to "nothing was asked for" reads as configuration and
            // behaves as none.
            Expect.isError
                (ConfigFile.parse """{ "version": 2, "sandboxes": { "dev": { "workdirr": "./app" } } }""")
                "a misspelled key fails the file"

        testCase "an unknown top-level key is refused too" <| fun () ->
            Expect.isError
                (ConfigFile.parse """{ "version": 2, "sandboxes": {}, "mcp": { "serial": "http://x" } }""")
                "a key this schema does not define is not silently ignored"

        testCase "a version this build does not speak is refused" <| fun () ->
            // A file from the future says so, rather than losing half its meaning to a
            // decoder that skips what it cannot read.
            Expect.isError (ConfigFile.parse """{ "version": 3, "sandboxes": {} }""")
                "version 3 is not decoded as a lossy version 2"

        testCase "a file with no version is refused" <| fun () ->
            Expect.isError (ConfigFile.parse """{ "sandboxes": {} }""") "the version is how the refusal above stays possible"

        testCase "a name a sandbox cannot have is refused where it is written" <| fun () ->
            // The name survives as a Docker object-name component and a directory.
            Expect.isError (ConfigFile.parse """{ "version": 2, "sandboxes": { "Dev Box": {} } }""")
                "an unusable name fails the file rather than a container start much later"

        testCase "two sandboxes with one name inside one file are refused" <| fun () ->
            // The ONLY place a clash can happen — across files the scope keeps them apart —
            // and it is refused where the person who wrote both can pick another name.
            // JSON's own duplicate-key handling would hide this, so the name is what repeats.
            let a = ConfigFile.parse """{ "version": 2, "sandboxes": { "dev": {}, "dev": {} } }"""
            match a with
            | Error _ -> ()
            | Ok file ->
                // A parser that folded the duplicate away must at least not invent two.
                Expect.equal (Map.count file.Sandboxes) 1 "a folded duplicate is one sandbox, never two"

        testCase "the union of two repos' files is total" <| fun () ->
            // The whole algebra. The keys are (repo, name) pairs and the repos are disjoint,
            // so both repos' `dev` survive and neither shadows the other.
            let file = ConfigFile.parse """{ "version": 2, "sandboxes": { "dev": {} } }""" |> expect
            let one, two = repo "octo/hello", repo "octo/other"
            let union = ConfigFile.union [ one, file; two, file ]
            Expect.equal (Map.count union) 2 "two repos asking for `dev` get two sandboxes"
            Expect.isTrue (union |> Map.containsKey (SandboxRef.inScope one (sandboxName "dev"))) "the first repo's"
            Expect.isTrue (union |> Map.containsKey (SandboxRef.inScope two (sandboxName "dev"))) "the second repo's"

        testCase "the union does not depend on the order the repos are folded" <| fun () ->
            // A union with an order dependence is a precedence rule nobody wrote down.
            //
            // The two files share a NAME and differ in content, which is what gives this
            // teeth: keyed on the name alone, the last fold would win and the answer would
            // depend on the order. Keyed by scope, there is nothing to win.
            let mine =
                ConfigFile.parse """{ "version": 2, "sandboxes": { "dev": { "uses": [ "npm" ] } } }""" |> expect
            let theirs =
                ConfigFile.parse """{ "version": 2, "sandboxes": { "dev": { "uses": [ "npm" ] } } }""" |> expect
            let one, two = repo "octo/hello", repo "octo/other"
            Expect.equal
                (ConfigFile.union [ one, mine; two, theirs ])
                (ConfigFile.union [ two, theirs; one, mine ])
                "folding two files either way gives the same session"
            Expect.equal (Map.count (ConfigFile.union [ one, mine; two, theirs ])) 2
                "and neither repo's `dev` was lost to the other's"

        testCase "no file can declare the sandbox a terminal lands in by default" <| fun () ->
            // `default` is the session's, and a repo naming it gets its OWN, scoped.
            let file = ConfigFile.parse """{ "version": 2, "sandboxes": { "default": {} } }""" |> expect
            let union = ConfigFile.union [ repo "octo/hello", file ]
            Expect.isFalse (union |> Map.containsKey SandboxRef.defaultRef)
                "the session's own default is not something a checkout can take over"

        testCase "a sandbox is written as its scope and its name" <| fun () ->
            Expect.equal (SandboxRef.render SandboxRef.defaultRef) "default" "the session's own is just a name"
            Expect.equal
                (SandboxRef.render (SandboxRef.inScope (repo "octo/hello") (sandboxName "dev")))
                "octo/hello:dev"
                "a repo's carries the repo"

        testCase "a written sandbox reads back as the same one" <| fun () ->
            // The agent addresses a sandbox by this string, so the round trip is a contract.
            let refs =
                [ SandboxRef.defaultRef
                  SandboxRef.inScope (repo "octo/hello") (sandboxName "dev") ]
            for r in refs do
                Expect.equal (SandboxRef.parse (SandboxRef.render r)) (Ok r) (sprintf "%s round-trips" (SandboxRef.render r))

        testCase "the session's own default keeps the bare session id it has always had" <| fun () ->
            // The whole behaviour-preservation claim of moving this derivation out of the
            // composition root. If this moves, every existing session's container, volume and
            // workspace moves with it.
            let session = SessionId.create "S0PZABC" |> expect
            Expect.equal (SandboxRef.objectName session SandboxRef.defaultRef) "S0PZABC"
                "the default sandbox's objects are named by the session, exactly as before"

        testCase "a session's named sandbox is the session id and the name" <| fun () ->
            let session = SessionId.create "S0PZABC" |> expect
            let review = SandboxRef.create SessionOwned (sandboxName "review")
            Expect.equal (SandboxRef.objectName session review) "S0PZABC-review" "unchanged too"

        testCase "two repos' same-named sandboxes name different objects" <| fun () ->
            // The reason this derivation exists at all. Two repos both declaring `dev` must
            // not end up sharing one container.
            let session = SessionId.create "S0PZABC" |> expect
            let mine = SandboxRef.inScope (repo "octo/hello") (sandboxName "dev")
            let theirs = SandboxRef.inScope (repo "octo/other") (sandboxName "dev")
            Expect.notEqual (SandboxRef.objectName session mine) (SandboxRef.objectName session theirs)
                "one container each"

        testCase "a repo's sandbox does not collide with a session's of the same name" <| fun () ->
            let session = SessionId.create "S0PZABC" |> expect
            Expect.notEqual
                (SandboxRef.objectName session (SandboxRef.create SessionOwned (sandboxName "dev")))
                (SandboxRef.objectName session (SandboxRef.inScope (repo "octo/hello") (sandboxName "dev")))
                "the scope is part of what names the object"

        testCase "the slug survives being a container name and a directory" <| fun () ->
            // `SandboxName`'s charset is narrow because these two consumers are; a slug that
            // carried the scope literally would put a '/' through both.
            let slug = SandboxRef.slug (SandboxRef.inScope (repo "octo/hello") (sandboxName "dev"))
            let safe c =
                (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c = '-' || c = '_'
            Expect.isTrue (String.forall safe slug) (sprintf "'%s' is nameable" slug)

        testCase "the derived name is stable across calls" <| fun () ->
            // It names a container that outlives the process that made it, so a name that
            // varied would orphan the previous one on every boot.
            let session = SessionId.create "S0PZABC" |> expect
            let ref = SandboxRef.inScope (repo "octo/hello") (sandboxName "dev")
            Expect.equal (SandboxRef.objectName session ref) (SandboxRef.objectName session ref)
                "the same ref names the same object"

        testCase "a sandbox nobody could name is refused rather than parsed" <| fun () ->
            Expect.isError (SandboxRef.parse "octo/hello:dev:extra") "two colons name nothing"
            Expect.isError (SandboxRef.parse "not-a-repo:dev") "the scope has to be an owner/repo"
    ]

// -----------------------------------------------------------------------------
// What "the same sandbox" means (Plan 27, step 5b). The registry hands back a running
// sandbox when the ask matches and refuses when it does not, so this comparison decides
// between two bad outcomes: killing somebody's build, or handing back a sandbox that is
// not what was asked for. It is pure, so it is pinned here rather than through a registry.
// -----------------------------------------------------------------------------

/// Every way one ask can differ from the default, one per field the comparison looks at.
/// A list rather than separate cases because the property under test is about ALL of them
/// at once: whichever field moved, the refusal has something to say.
let private variants : (string * SandboxRequest) list =
    [ "forwarding", { SandboxRequest.defaults with Forward = [ "github" ] }
      "workdir",
        { SandboxRequest.defaults with
            Spec = { EnvironmentSpec.defaults with WorkingDirectory = Some "/somewhere" } }
      "environment",
        { SandboxRequest.defaults with
            Spec =
                { EnvironmentSpec.defaults with
                    EnvironmentVariables = Map.ofList [ "NODE_ENV", PlainValue "development" ] } }
      "runtime", { SandboxRequest.defaults with Spec = EnvironmentSpec.container }
      "image",
        { SandboxRequest.defaults with
            Spec =
                { EnvironmentSpec.defaults with
                    Runtime = Container { ContainerSpec.defaults with Image = Some { Name = "node"; Tag = Some "24" } } } }
      "command",
        { SandboxRequest.defaults with
            Spec =
                { EnvironmentSpec.defaults with
                    Runtime = Container { ContainerSpec.defaults with Command = Some "postgres" } } }
      "mounts",
        { SandboxRequest.defaults with
            Spec =
                { EnvironmentSpec.defaults with
                    Runtime =
                        Container
                            { ContainerSpec.defaults with
                                Mounts = [ { Source = SessionWorkspace; Target = "/work"; Mode = ReadWrite } ] } } }
      // The one no clause looks at, and the reason there is a backstop: two builds of the
      // same context differing only in which Dockerfile they use describe identically.
      "dockerfile",
        { SandboxRequest.defaults with
            Spec =
                { EnvironmentSpec.defaults with
                    Runtime =
                        Container
                            { ContainerSpec.defaults with
                                Build = Some { ContextPath = "."; DockerfilePath = Some "Dockerfile.ci" } } } } ]

let private sandboxRequestTests =
    testList "the same sandbox, or a different one (Plan 27)" [

        testCase "an ask that matches has nothing to say about itself" <| fun () ->
            for name, variant in ("default", SandboxRequest.defaults) :: variants do
                Expect.equal
                    (SandboxRequest.differences variant variant)
                    []
                    (sprintf "'%s' against itself is the idempotent case" name)

        // THE property, and the reason `differences` is a function rather than a sentence
        // assembled at the refusal: whenever the registry declines to hand a sandbox back,
        // it must be able to say why. A clause list that could come back empty would make
        // the refusal claim a difference and then name none of it.
        testCase "whatever moved, the refusal has a clause for it" <| fun () ->
            for name, variant in variants do
                Expect.isNonEmpty
                    (SandboxRequest.differences SandboxRequest.defaults variant)
                    (sprintf "a '%s' difference is describable" name)
                Expect.isNonEmpty
                    (SandboxRequest.differences variant SandboxRequest.defaults)
                    (sprintf "and so is a '%s' difference the other way round" name)

        // Names, never values — but two asks naming the SAME variables used to refuse
        // with "sets NIX_CONFIG, not NIX_CONFIG", a sentence that reads as a bug in the
        // refusal rather than a difference in the ask (measured on a live session, on
        // exactly that variable). The clause now says WHICH variable moved, and still
        // never what either side set it to.
        testCase "a value-only environment difference names the variable, not the value" <| fun () ->
            let asking value =
                { SandboxRequest.defaults with
                    Spec =
                        { EnvironmentSpec.defaults with
                            EnvironmentVariables = Map.ofList [ "NIX_CONFIG", PlainValue value ] } }
            match SandboxRequest.differences (asking "a") (asking "b") with
            | [ clause ] ->
                Expect.isTrue (clause.Contains "NIX_CONFIG") (sprintf "names the variable, said: %s" clause)
                Expect.isTrue (clause.Contains "different value") (sprintf "and says what kind of difference, said: %s" clause)
                Expect.isFalse (clause.Contains "not NIX_CONFIG") (sprintf "never the name against itself, said: %s" clause)
                Expect.isFalse (clause.Contains "\"a\"" || clause.Contains "= a") (sprintf "and never a value, said: %s" clause)
            | other -> failwithf "expected one clause, got %A" other

        // The backstop, on the case that reaches it: two asks that describe the same way and
        // still are not the same ask. It says less, and that is the point — a refusal that
        // said nothing here would let the registry treat them as one sandbox.
        testCase "a difference no clause looks at is still a difference" <| fun () ->
            let buildOf dockerfile =
                { SandboxRequest.defaults with
                    Spec =
                        { EnvironmentSpec.defaults with
                            Runtime =
                                Container
                                    { ContainerSpec.defaults with
                                        Build = Some { ContextPath = "."; DockerfilePath = Some dockerfile } } } }
            Expect.isNonEmpty
                (SandboxRequest.differences (buildOf "Dockerfile") (buildOf "Dockerfile.ci"))
                "two builds of one context are not interchangeable"

        // Names, never values. The registry's refusal reaches the model and the timeline,
        // and an environment variable's value is the one thing neither may carry.
        testCase "a differing environment is described by name, never by value" <| fun () ->
            let secret =
                { SandboxRequest.defaults with
                    Spec =
                        { EnvironmentSpec.defaults with
                            EnvironmentVariables = Map.ofList [ "DATABASE_URL", PlainValue "postgres://hunter2" ] } }
            let said = SandboxRequest.differences SandboxRequest.defaults secret |> String.concat "; "
            Expect.isTrue (said.Contains "DATABASE_URL") "it names the variable"
            Expect.isFalse (said.Contains "hunter2") "and not what is in it"
    ]

let private deliveryFilterTests =
    let path raw = FieldPath.create raw |> expect
    /// A delivery as the relay presents one: whatever the caller asks for, by path.
    let delivery (fields: (string * string) list) =
        fun p -> fields |> List.tryFind (fun (k, _) -> k = FieldPath.render p) |> Option.map snd

    testList "Delivery filters (the hook relay)" [
        testCase "a path is dotted segments, lowercased so a header's case cannot matter" <| fun () ->
            Expect.equal
                (FieldPath.render (path "Body.Repository.Full_Name"))
                "body.repository.full_name"
                "the rendering is what travels, and it is one canonical form"

        testCase "a path with an empty segment is not a path" <| fun () ->
            Expect.isError (FieldPath.create "body..name") "an empty segment addresses nothing"

        testCase "a blank path is not a path" <| fun () ->
            Expect.isError (FieldPath.create "   ") "there is nothing to address"

        testCase "every constraint must hold" <| fun () ->
            let filter =
                { Where =
                    [ path "body.repository.full_name", "trinketworks/yession"
                      path "headers.x-github-event", "pull_request" ] }
            let matching =
                delivery
                    [ "body.repository.full_name", "trinketworks/yession"
                      "headers.x-github-event", "pull_request" ]
            Expect.isTrue (DeliveryFilter.matches filter matching) "both hold, so it matches"

        testCase "one constraint that does not hold is enough to refuse" <| fun () ->
            let filter =
                { Where =
                    [ path "body.repository.full_name", "trinketworks/yession"
                      path "headers.x-github-event", "pull_request" ] }
            let other =
                delivery
                    [ "body.repository.full_name", "trinketworks/yession"
                      "headers.x-github-event", "issues" ]
            Expect.isFalse (DeliveryFilter.matches filter other) "a conjunction is refused by any one of its parts"

        testCase "a path the delivery does not carry fails its constraint" <| fun () ->
            // The direction that matters: an absent field must never match by accident,
            // because that is how one session would start receiving another's deliveries.
            let filter = { Where = [ path "body.repository.full_name", "trinketworks/yession" ] }
            Expect.isFalse (DeliveryFilter.matches filter (delivery [])) "absent is not equal"

        testCase "a filter with no constraints accepts everything on its endpoint" <| fun () ->
            Expect.isTrue
                (DeliveryFilter.matches DeliveryFilter.everything (delivery []))
                "no constraints is not a special case, it is an empty conjunction"
    ]

/// A delivery is ONE document, and these are the rules that make it one: which half a path
/// reaches, what a segment matches, and which values a constraint can name. They run on both
/// runtimes because the rule is the domain's, not the relay's.
let private deliveryDocumentTests =
    let path raw = FieldPath.create raw |> expect
    let resolving (headers: (string * string) list) (body: string) (raw: string) =
        match Delivery.create headers body with
        | Some delivery -> Delivery.resolve delivery (path raw)
        | None -> failwithf "expected %s to be a readable delivery" body

    testList "A delivery as one document" [
        testCase "a segment matches a key case-insensitively" <| fun () ->
            // A path is lowercased when it is parsed, so this is the only direction there is:
            // a provider's casing must not be something a session has to know.
            Expect.equal
                (resolving [] """{"Repository":{"Full_Name":"trinketworks/yession"}}""" "body.repository.full_name")
                (Some "trinketworks/yession")
                "the key's case is not part of the address"

        testCase "a path that lands on an object addresses nothing" <| fun () ->
            Expect.equal
                (resolving [] """{"repository":{"full_name":"trinketworks/yession"}}""" "body.repository")
                None
                "a container is not a value a constraint can equal"

        testCase "a path that lands on an array addresses nothing" <| fun () ->
            Expect.equal
                (resolving [] """{"labels":["bug"]}""" "body.labels")
                None
                "an array is a container too, however few things are in it"

        testCase "a path that lands on nothing addresses nothing" <| fun () ->
            Expect.equal
                (resolving [] """{"action":"opened"}""" "body.merged")
                None
                "an absent field must never match by accident"

        testCase "a string answers itself" <| fun () ->
            Expect.equal (resolving [] """{"action":"opened"}""" "body.action") (Some "opened") "unchanged"

        testCase "a number answers the string it renders as" <| fun () ->
            Expect.equal
                (resolving [] """{"number":7}""" "body.number")
                (Some "7")
                "a constraint is an equality between strings, so a number has to have one"

        testCase "a boolean answers the string it renders as" <| fun () ->
            Expect.equal (resolving [] """{"draft":true}""" "body.draft") (Some "true") "as a session would write it"

        testCase "a json null addresses nothing" <| fun () ->
            Expect.equal
                (resolving [] """{"merged_by":null}""" "body.merged_by")
                None
                "a field that is present and empty is not a value either"

        testCase "a first segment of headers reads the header half" <| fun () ->
            Expect.equal
                (resolving [ "X-GitHub-Event", "pull_request" ] """{"action":"opened"}""" "headers.x-github-event")
                (Some "pull_request")
                "a header is addressed the same way a body field is"

        testCase "a first segment of body reads the body half" <| fun () ->
            Expect.equal
                (resolving [ "x-github-event", "pull_request" ] """{"action":"opened"}""" "body.action")
                (Some "opened")
                "the halves are told apart by the first segment and nothing else"

        testCase "a first segment naming neither half addresses nothing" <| fun () ->
            Expect.equal
                (resolving [] """{"action":"opened"}""" "payload.action")
                None
                "there are two halves, and a path that names a third reaches no document"

        testCase "a body that is not json is not a delivery" <| fun () ->
            Expect.equal (Delivery.create [] "not json") None "there is nothing to address"

        testCase "a top-level json array is not a delivery" <| fun () ->
            Expect.equal (Delivery.create [] """["opened"]""") None "a document has fields; an array has places"

        testCase "a bare top-level json value is not a delivery" <| fun () ->
            Expect.equal (Delivery.create [] "42") None "a value on its own carries no field to name"
    ]

// Whose session it is (Plan 25). The fold already answered "who is this peer" and "which
// peer is this user's"; the creator is the third reading of the same joins, and the only
// one that is about their ORDER — which is why it is folded rather than derived.
let private attributionTests =
    let ada = PeerId.create "ada" |> expect
    let bob = PeerId.create "bob" |> expect
    let carol = UserId.create "carol" |> expect
    let dan = UserId.create "dan" |> expect
    let joined (peer: PeerId) (user: UserId option) =
        PeerJoined { PeerJoined.PeerId = peer; PeerJoined.DisplayName = "peer"; PeerJoined.User = user }
    testList "Whose session it is" [

        testCase "the creator is the first user the session attributed" <| fun () ->
            let state = Attribution.ofEvents [ joined ada (Some carol) ]
            Expect.equal (Attribution.creator state) (Some (Principal.User carol)) "the first attributed join names the creator"

        testCase "a later arrival does not take the session from whoever started it" <| fun () ->
            let state = Attribution.ofEvents [ joined ada (Some carol); joined bob (Some dan) ]
            Expect.equal (Attribution.creator state) (Some (Principal.User carol)) "the creator is the first, not the latest"

        testCase "the creator survives their own reconnection" <| fun () ->
            // Every reconnect mints a new peer, so the same person joins several times. The
            // rule is about the USER, not the connection: a creator who refreshes the tab is
            // still the creator, and is not replaced by themselves.
            let state = Attribution.ofEvents [ joined ada (Some carol); joined bob (Some carol) ]
            Expect.equal (Attribution.creator state) (Some (Principal.User carol)) "rejoining does not restart the question"

        testCase "an unattributed session has no creator" <| fun () ->
            // `--auth localhost` verifies nobody, so there is no user whose session this is.
            // What reads this for a credential turns that into the deployment's own scope,
            // which is exactly what such a launch holds.
            let state = Attribution.ofEvents [ joined ada None; joined bob None ]
            Expect.equal (Attribution.creator state) None "nobody verified is nobody, not an anonymous somebody"

        testCase "an unattributed peer before an attributed one does not deny them the session" <| fun () ->
            let state = Attribution.ofEvents [ joined ada None; joined bob (Some carol) ]
            Expect.equal (Attribution.creator state) (Some (Principal.User carol)) "the first ATTRIBUTED join is the one that counts"
    ]

// What the session owes in the way of names (Plan 25). The rule that decides which subjects
// want naming, and the guard that is the only thing between a model and a name somebody
// typed themselves.
let private namingTests =
    let saying id body : ConversationItem =
        { MessageId = MessageId.create id |> expect
          Author = ActorRef.Agent
          Body = body
          Status = Complete
          Kind = ConversationItemKind.Message
          Offset = EventOffset.create 1L |> expect
          Woke = None; Replying = None }
    /// A chapter opening at this item, wearing whatever is written on it.
    let opened (item: ConversationItem) (written: string) =
        item.MessageId, { Opens = true; Name = Ylmish.Text.ofString written }
    let subjectOf (item: ConversationItem) = NamingSubject.Chapter item.MessageId
    /// A title somebody set by hand, so a title is never what these answer with.
    let titled = "Titled by hand"
    /// What is owed about CHAPTERS. Scoped, so a chapter case goes red when the chapter rule
    /// breaks and not when the title rule does — the two are asked of the same fold, and a
    /// case that answered for both would name neither.
    let chaptersOwed settled chapters items =
        Naming.owed settled titled chapters items
        |> List.filter (fun (job: Naming.Job) -> job.Subject <> NamingSubject.Title)
    let settledAs (item: ConversationItem) (name: string) (read: int) =
        Map.ofList
            [ subjectOf item,
              { Subject = subjectOf item; Name = name; Read = read; OnBehalfOf = None } ]

    testList "What the session owes a name" [

        // The ordinary case: a chapter made by hand wears the guess, and the guess is what
        // nobody chose.
        testCase "a chapter still wearing its guess is owed a name" <| fun () ->
            let item = saying "m" "run tests"
            let chapters = Map.ofList [ opened item (Chapters.defaultName item) ]
            Expect.equal
                (chaptersOwed Map.empty chapters [ item ] |> List.map (fun job -> job.Subject))
                [ subjectOf item ]
                "the guess is nobody's words"

        // The promise the whole feature rests on.
        testCase "a chapter somebody named themselves is owed nothing, ever" <| fun () ->
            let item = saying "m" "run tests"
            let chapters = Map.ofList [ opened item "The Friday deploy" ]
            Expect.equal (chaptersOwed Map.empty chapters [ item ]) [] "their words end the question"

        // ...and it holds against the session's own record, not just against the guess: a
        // person who types over a name the session wrote has the same last word.
        testCase "a name the session wrote and a person then changed is owed nothing" <| fun () ->
            // Material enough to be worth asking again, so the ONLY thing that can stop this
            // is whose words are on it.
            let item = saying "m" "run tests"
            let items = [ item; saying "n" "and the rest" ]
            let chapters = Map.ofList [ opened item "Their better name" ]
            Expect.equal
                (chaptersOwed (settledAs item "What the session wrote" 1) chapters items)
                []
                "the doc no longer reads what the session left there"

        // The other half of the same fact: what the session wrote is its own to revisit.
        testCase "a name the session wrote is its own to reconsider" <| fun () ->
            let first = saying "a" "run tests"
            let second = saying "b" "the auth middleware drops the refresh token"
            let chapters = Map.ofList [ opened first "Running the tests" ]
            Expect.equal
                (chaptersOwed (settledAs first "Running the tests" 1) chapters [ first; second ]
                 |> List.map (fun job -> job.Subject))
                [ subjectOf first ]
                "its own words are not somebody else's"

        // The case the doubling rule exists for: a session that opens with "run tests" and
        // puts the actual work in the second message.
        testCase "a second message doubles the material, so the name is asked again" <| fun () ->
            let first = saying "a" "run tests"
            let second = saying "b" "the auth middleware drops the refresh token"
            let chapters = Map.ofList [ opened first "Running the tests" ]
            let jobs = chaptersOwed (settledAs first "Running the tests" 1) chapters [ first; second ]
            Expect.equal (jobs |> List.map (fun job -> job.Read)) [ 2 ] "both messages, and the fact will say so"

        // ...and the bound that keeps it from being one model call per message.
        testCase "material that has grown without doubling is not asked again" <| fun () ->
            let items = [ saying "a" "one"; saying "b" "two"; saying "c" "three" ]
            let first = List.head items
            let chapters = Map.ofList [ opened first "Two of them" ]
            Expect.equal (chaptersOwed (settledAs first "Two of them" 2) chapters items) [] "three is not six"

        // A chapter an act opens by nature has no entry until somebody touches it, and an act
        // note is a sentence somebody already wrote short.
        testCase "a chapter no doc entry opens is not a subject" <| fun () ->
            let act =
                { saying "n" "PR octo/hello#12 merged" with
                    Kind = ConversationItemKind.ActNote { Detail = None; Notable = true } }
            Expect.isTrue (Chapters.opens Map.empty act) "it does open a chapter"
            Expect.equal (chaptersOwed Map.empty Map.empty [ act ]) [] "and it is still not named"

        // A closed chapter is not a chapter.
        testCase "a chapter somebody closed is owed nothing" <| fun () ->
            let item = saying "m" "run tests"
            let chapters = Map.ofList [ item.MessageId, { Opens = false; Name = Ylmish.Text.empty } ]
            Expect.equal (chaptersOwed Map.empty chapters [ item ]) [] "there is no chapter here to name"

        // The doc and the log are two stores, and a mark can name a message the log has not
        // reached yet.
        testCase "a mark with no message behind it is not a subject" <| fun () ->
            let item = saying "m" "run tests"
            let chapters = Map.ofList [ opened item (Chapters.defaultName item) ]
            Expect.equal (chaptersOwed Map.empty chapters []) [] "nothing to read, so nothing to ask"

        // A second ask exists to let the model KEEP the name, and it cannot keep a name it
        // was never told. What is pinned is that the name reaches the ask, not the prose
        // around it — the wording is a design and will be revised.
        testCase "a re-reading tells the model what it is called already" <| fun () ->
            let first = saying "a" "run tests"
            let second = saying "b" "the auth middleware drops the refresh token"
            let chapters = Map.ofList [ opened first "Running the tests" ]
            match chaptersOwed (settledAs first "Running the tests" 1) chapters [ first; second ] with
            | [ job ] -> Expect.isTrue (job.Ask.Task.Contains "Running the tests") "the name it has is in the ask"
            | other -> failwithf "expected one job, got %A" other

        // ...and a FIRST ask is not, because the guess is the first line of a message. Handing
        // it over would be asking for a rewording of that line rather than a name for what
        // the part is about.
        testCase "a first ask is not anchored to the guess" <| fun () ->
            let item = saying "m" "run tests"
            let chapters = Map.ofList [ opened item (Chapters.defaultName item) ]
            match chaptersOwed Map.empty chapters [ item ] with
            | [ job ] -> Expect.isFalse (job.Ask.Task.Contains "run tests") "nothing to keep, so nothing to anchor to"
            | other -> failwithf "expected one job, got %A" other

        // The job carries what the write must still find, so the compare-and-set is against
        // what this pass actually read.
        testCase "a job carries the name it read, for the write to compare against" <| fun () ->
            let item = saying "m" "run tests"
            let chapters = Map.ofList [ opened item "Running the tests" ]
            match chaptersOwed (settledAs item "Running the tests" 1) chapters [ item; saying "n" "and the rest" ] with
            | [ job ] -> Expect.equal job.Held "Running the tests" "what stood when this pass looked"
            | other -> failwithf "expected one job, got %A" other

        // --- the session's own name -------------------------------------------------------

        // A title has no guess, so the state nobody chose is the empty one.
        testCase "an untitled session with something said in it is owed a title" <| fun () ->
            let item = saying "m" "the auth middleware drops the refresh token"
            Expect.equal
                (Naming.owed Map.empty "" Map.empty [ item ] |> List.map (fun job -> job.Subject))
                [ NamingSubject.Title ]
                "empty is nobody's words, so it is the session's to write"

        testCase "a session nobody has said anything in is owed no title" <| fun () ->
            Expect.equal (Naming.owed Map.empty "" Map.empty []) [] "there is nothing to name it after"

        testCase "a title somebody typed is owed nothing, ever" <| fun () ->
            let item = saying "m" "the auth middleware drops the refresh token"
            Expect.equal (Naming.owed Map.empty "Friday deploy" Map.empty [ item ]) [] "their words end the question"

        // The same doubling rule as a chapter's, over the whole conversation rather than a
        // stretch — which is the "run tests" case at the session's own scale.
        testCase "a session whose material doubles is titled again" <| fun () ->
            let items = [ saying "a" "run tests"; saying "b" "the auth middleware drops the refresh token" ]
            let settled =
                Map.ofList
                    [ NamingSubject.Title,
                      { Subject = NamingSubject.Title; Name = "Running the tests"; Read = 1; OnBehalfOf = None } ]
            match Naming.owed settled "Running the tests" Map.empty items with
            | [ job ] ->
                Expect.equal job.Read 2 "and the fact will say it read both"
                Expect.isTrue (job.Ask.Task.Contains "Running the tests") "with the name it has, so it can keep it"
            | other -> failwithf "expected one job, got %A" other

        testCase "a session that has not doubled since its title is not asked again" <| fun () ->
            let items = [ saying "a" "one"; saying "b" "two"; saying "c" "three" ]
            let settled =
                Map.ofList
                    [ NamingSubject.Title,
                      { Subject = NamingSubject.Title; Name = "Two of them"; Read = 2; OnBehalfOf = None } ]
            Expect.equal (Naming.owed settled "Two of them" Map.empty items) [] "three is not six"

        // --- what the pass records ------------------------------------------------------

        // Recording the LOSS is what tells the next pass the subject is somebody else's now.
        testCase "a write that lost its race is settled to what stands, not to what was said" <| fun () ->
            let item = saying "m" "run tests"
            let chapters = Map.ofList [ opened item (Chapters.defaultName item) ]
            match chaptersOwed Map.empty chapters [ item ] with
            | [ job ] ->
                let fact = Naming.settle job None "What they typed instead"
                Expect.equal fact.Name "What they typed instead" "the fact records the doc, not the answer"
            | other -> failwithf "expected one job, got %A" other

        testCase "the fold keeps the latest fact for a subject" <| fun () ->
            let item = saying "m" "run tests"
            let earlier = { Subject = subjectOf item; Name = "First"; Read = 1; OnBehalfOf = None }
            let later = { earlier with Name = "Second"; Read = 4 }
            let settled = Naming.ofEvents [ SessionNamed earlier; SessionNamed later ]
            Expect.equal (Map.tryFind (subjectOf item) settled) (Some later) "the last word is the current one"
    ]

let tests =
    testList "Domain" [
        identityTests
        launchTests
        configTests
        sandboxRequestTests
        modelTests
        authorityTests
        attributionTests
        envelopeSerializationTests
        conversationProjectionTests
        repoTests
        chapterTests
        namingTests
        prWatchTests
        deliveryFilterTests
        deliveryDocumentTests
        shellProfileTests
        frameSerializationTests
    ]
