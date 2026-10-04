module Yession.Tests.Timeline

// The chat as a PERSON reads it (Plan 14, stage 1): what was said and what was run, merged
// into one order. Cheap tier throughout — the whole thing is a pure fold over envelopes and
// a sort, so nothing here needs a port, a process, or a browser.

open System
open Fable.Pyxpecto
open Yession.Domain
open Yession.Codecs
open Yession.Domain.Agent
open Yession.Domain.Tools
open Yession.Domain.Terminals
open Yession.Domain.Collab
open Yession.Domain.Chat
open Yession.Domain.Sandboxes
open Yession.Domain.Files
open Yession.Domain.Content
open Yession.App
open Yession.App.Codecs
open Hedgehog

let private expect =
    function
    | Ok v -> v
    | Error e -> failwith e

let private sessionId = SessionId.create "timeline-tests" |> expect
let private terminalA = TerminalId.create "term-a" |> expect
let private terminalB = TerminalId.create "term-b" |> expect
let private terminalC = TerminalId.create "term-c" |> expect
let private ada = PeerId.create "ada" |> expect
let private bob = PeerId.create "bob" |> expect

/// The authorities the fixtures below act under: a person for themselves, and the agent on
/// Ada's turn — the only agent-shaped authority there is.
let private byAda = Authority.ofAuthor (Principal.Peer ada)
let private byBob = Authority.ofAuthor (Principal.Peer bob)
let private agentForAda = Authority.agentFor (Principal.Peer ada)

let private block (n: string) = BlockId.create ("b-" + n) |> expect
let private message (n: string) = MessageId.create ("m-" + n) |> expect

let private epoch = DateTimeOffset (2026, 8, 8, 0, 0, 0, TimeSpan.Zero)

/// One envelope at `offset`, stamped `seconds` after the epoch. The timestamp matters: a
/// stretch's item says how long the holder had the terminal, and only the envelopes can
/// answer that — the transcript's clock is per-terminal.
let private at (offset: int64) (seconds: float) (event: SessionEvent) : EventEnvelope<SessionEvent> =
    { EventId = EventId.fresh ()
      SessionId = sessionId
      Offset = EventOffset.create offset |> expect
      Actor = ActorRef.Session
      Timestamp = epoch.AddSeconds seconds
      Event = event }

/// The two projections the timeline merges, folded from one page — exactly as a client does.
let private merge (events: EventEnvelope<SessionEvent> list) : TimelineItem list =
    let conversation, _ = ConversationProjection.applyEvents None events ConversationProjection.empty
    let timeline, _ = TimelineProjection.applyEvents None events TimelineProjection.empty
    TimelineProjection.items conversation timeline

let private openedBy (by: ActorRef) (id: TerminalId) (title: string) =
    SessionEvent.TerminalOpened { TerminalId = id; OpenedBy = by; Title = (TerminalTitle.fromProse title); Sandbox = Some SandboxRef.defaultRef; Renewable = false }

let private opened (id: TerminalId) (title: string) = openedBy (PeerRef ada) id title

/// The users a Manager-verified deployment attributes these peers to, and the join that
/// records it. Under `--auth localhost` no join carries one and every actor stays a peer;
/// these are the other deployment, which is the one the pane's ownership rules were never
/// tested against.
let private nick = UserId.create "nick@example.com" |> expect
let private bobsUser = UserId.create "bob@example.com" |> expect

let private attributed (peer: PeerId) =
    let user = if peer = ada then nick else bobsUser
    SessionEvent.PeerJoined { PeerId = peer; DisplayName = "swift-heron"; User = Some user }

let private sent (n: string) (body: string) =
    MessageSent { MessageId = message n; QueueId = None; Author = Principal.Peer ada; Body = body }

let private started (id: TerminalId) (n: string) (authority: Authority) (command: string) (fromSeq: int) =
    SessionEvent.TerminalBlockStarted
        { TerminalId = id
          BlockId = block n
          QueueId = None
          Authority = authority
          Command = command
          FromSeq = fromSeq
          Background = false }

let private completed (id: TerminalId) (n: string) (result: CommandResult) (toSeq: int) =
    SessionEvent.TerminalBlockCompleted { TerminalId = id; BlockId = block n; Result = result; ToSeq = toSeq }

let private took (id: TerminalId) (by: ActorRef) (fromSeq: int) =
    SessionEvent.TerminalLeaseTaken { TerminalId = id; By = by; FromSeq = fromSeq }

let private released (id: TerminalId) (was: ActorRef) (reason: TerminalLeaseEnd) (toSeq: int) =
    SessionEvent.TerminalLeaseReleased { TerminalId = id; Was = was; Reason = reason; ToSeq = toSeq }

/// What each item IS, for an ordering assertion that does not have to spell out a record.
let private shapes (items: TimelineItem list) : string list =
    items
    |> List.map (function
        | TimelineMessage item -> "said:" + MessageId.value item.MessageId
        | TimelineBlock (_, _, blockId) -> "ran:" + BlockId.value blockId
        | TimelineStretch stretch -> "held:" + TerminalId.value stretch.TerminalId
        | TimelineToolUse (_, id) -> "used:" + ToolUseId.value id
        | TimelineThought (_, thought) -> "thought:" + thought.Thought)

let private stretchesOf (items: TimelineItem list) : TerminalStretch list =
    items |> List.choose (function TimelineStretch s -> Some s | _ -> None)

// --- Ordering -------------------------------------------------------------------------------

let private orderTests =
    testList "The merged order" [
        testCase "a block that starts before a message and finishes after it sits BEFORE it" <| fun () ->
            // The whole point of anchoring a chip at its start: a four-minute build's result
            // lands above the messages sent while it ran, rather than jumping to the bottom
            // when it happens to finish. Appearing only on completion would make long work
            // invisible while it is the only thing happening.
            let items =
                merge
                    [ at 1L 0.0 (opened terminalA "build")
                      at 2L 1.0 (started terminalA "1" byAda "make" 1)
                      at 3L 2.0 (sent "1" "how's it going?")
                      at 4L 3.0 (completed terminalA "1" (CommandSucceeded 0) 40) ]
            Expect.equal (shapes items) [ "ran:b-1"; "said:m-1" ] "the chip holds the place it started at"

        // Reasoning is two decisions, and they are pinned separately because they break
        // separately: it is IN the order things happened, and it is OFF the screen.
        testCase "what the model reasoned sits in the order it happened" <| fun () ->
            let items =
                merge
                    [ at 1L 0.0 (sent "1" "run the tests")
                      at 2L 1.0 (
                          SessionEvent.AgentThought
                              { AgentTurnId = AgentTurnId.create "turn-t1" |> expect; Thought = "dev, not gate" })
                      at 3L 2.0 (sent "2" "on it") ]
            Expect.equal
                (shapes items)
                [ "said:m-1"; "thought:dev, not gate"; "said:m-2" ]
                "between the acts it explains, not appended to a list of its own"

        testCase "everything is ordered by ONE key: the offset it was anchored at" <| fun () ->
            let items =
                merge
                    [ at 1L 0.0 (opened terminalA "build")
                      at 2L 0.0 (sent "1" "first")
                      at 3L 1.0 (started terminalA "1" agentForAda "ls" 1)
                      at 4L 2.0 (took terminalA (PeerRef bob) 5)
                      at 5L 9.0 (released terminalA (PeerRef bob) LeaseReleased 30)
                      at 6L 10.0 (sent "2" "done?") ]
            Expect.equal
                (shapes items)
                [ "said:m-1"; "ran:b-1"; "held:term-a"; "said:m-2" ]
                "said, ran, held, said — in log order"

        testCase "re-applying an overlapping page adds nothing twice" <| fun () ->
            // The same offset gate the other projections carry, for the same reason: pages
            // overlap, and a chip that appeared twice would be a bug a reload could not fix.
            let page =
                [ at 1L 0.0 (opened terminalA "build")
                  at 2L 1.0 (started terminalA "1" byAda "make" 1) ]
            let first, highWater = TimelineProjection.applyEvents None page TimelineProjection.empty
            let second, _ = TimelineProjection.applyEvents highWater page first
            Expect.equal (List.length second.TerminalItems) 1 "one chip, however many times the page arrives"
    ]

// --- Chips ------------------------------------------------------------------------------------

let private chipTests =
    testList "Block chips" [
        testCase "a chip carries NO status of its own — it is the block's, read live" <| fun () ->
            // What makes a chip mutate in place for free: the timeline holds where it goes,
            // `Projection` holds what it currently says. A chip that copied the
            // status in would need its own update path, and would be free to disagree.
            let running =
                [ at 1L 0.0 (opened terminalA "build")
                  at 2L 1.0 (started terminalA "1" byAda "make" 1) ]
            let finished = running @ [ at 3L 9.0 (completed terminalA "1" (CommandFailed 2) 40) ]
            let itemsOf events = (TimelineProjection.applyEvents None events TimelineProjection.empty |> fst).TerminalItems
            Expect.equal (itemsOf running) (itemsOf finished) "the timeline entry does not move or change"
            let statusOf events =
                let proj = events |> List.fold (fun p (e: EventEnvelope<SessionEvent>) -> Projection.applyEvent p e.Event) Projection.empty
                Projection.tryFind terminalA proj
                |> Option.bind (fun t -> t.Blocks |> List.tryFind (fun b -> b.BlockId = block "1"))
                |> Option.map (fun b -> b.Status)
            Expect.equal (statusOf running) (Some BlockRunning) "running, at first"
            Expect.equal (statusOf finished) (Some (BlockFinished (CommandFailed 2))) "and the exit code afterwards"

        testCase "a REJECTED command gets a chip too" <| fun () ->
            // "The agent proposed this and a human said no" is the more interesting half of
            // the two, and a refusal that appears nowhere is indistinguishable from a bug.
            let items =
                merge
                    [ at 1L 0.0 (opened terminalA "build")
                      at 2L 1.0 (
                          SessionEvent.TerminalCommandRejected
                              { TerminalId = terminalA
                                QueueId = QueueId.create "q-1" |> expect
                                BlockId = block "no"
                                Authority = agentForAda
                                RejectedBy = PeerRef ada
                                Command = "rm -rf /"
                                Reason = Some "no" }) ]
            Expect.equal (shapes items) [ "ran:b-no" ] "the refusal is in the chat where it was proposed"
    ]

// --- Stretches ---------------------------------------------------------------------------------

let private stretchTests =
    testList "Lease stretches" [
        testCase "a stretch appears when it CONCLUDED, not when it began" <| fun () ->
            // The difference between a stretch and a chip, and the one the user asked for: a
            // long interactive session is a thing you read about afterwards.
            let open' = [ at 1L 0.0 (opened terminalA "shell"); at 2L 1.0 (took terminalA (PeerRef bob) 5) ]
            Expect.isEmpty (shapes (merge open')) "a lease still held is not an item yet"
            let ended = open' @ [ at 3L 61.0 (released terminalA (PeerRef bob) LeaseReleased 300) ]
            Expect.equal (shapes (merge ended)) [ "held:term-a" ] "and one when it ends"

        testCase "the item says who, how long, where, and how it ended" <| fun () ->
            let items =
                merge
                    [ at 1L 0.0 (opened terminalA "shell")
                      at 2L 10.0 (took terminalA (PeerRef bob) 5)
                      at 3L 130.0 (released terminalA (PeerRef bob) LeaseReleased 300) ]
            match stretchesOf items with
            | [ stretch ] ->
                Expect.equal stretch.Holder (PeerRef bob) "who held it"
                Expect.equal stretch.Title "shell" "named by the terminal's title, not its id"
                Expect.equal (TerminalStretch.duration stretch) (TimeSpan.FromSeconds 120.0) "for two minutes"
                Expect.equal stretch.End LeaseReleased "and handed back"
                Expect.equal stretch.Range (Some (5, 300)) "with the range its replay needs"
            | other -> failwithf "expected exactly one stretch, got %d" (List.length other)

        testCase "each of the four endings is its own answer" <| fun () ->
            // "Did nick finish, get taken over, drop out, or just wander off?" has four
            // different answers, and collapsing any two would say someone decided something
            // they did not.
            let endings =
                [ LeaseReleased; LeaseStolen (PeerRef ada); LeaseHolderGone; LeaseIdle ]
                |> List.map (fun ending ->
                    merge
                        [ at 1L 0.0 (opened terminalA "shell")
                          at 2L 1.0 (took terminalA (PeerRef bob) 5)
                          at 3L 9.0 (released terminalA (PeerRef bob) ending 30) ]
                    |> stretchesOf
                    |> List.map (fun s -> s.End))
            Expect.equal
                endings
                [ [ LeaseReleased ]; [ LeaseStolen (PeerRef ada) ]; [ LeaseHolderGone ]; [ LeaseIdle ] ]
                "each ending survives to the item that reports it"

        testCase "a steal closes one stretch and opens the next, abutting exactly" <| fun () ->
            // The Process writes both at ONE transcript position, so the two ranges meet with
            // no overlap and no gap — a replay of either shows only that holder's bytes.
            let items =
                merge
                    [ at 1L 0.0 (opened terminalA "shell")
                      at 2L 1.0 (took terminalA (PeerRef ada) 5)
                      at 3L 20.0 (released terminalA (PeerRef ada) (LeaseStolen (PeerRef bob)) 40)
                      at 4L 20.0 (took terminalA (PeerRef bob) 40)
                      at 5L 50.0 (released terminalA (PeerRef bob) LeaseReleased 90) ]
            Expect.equal
                (stretchesOf items |> List.map (fun s -> s.Holder, s.Range))
                [ PeerRef ada, Some (5, 40); PeerRef bob, Some (40, 90) ]
                "ada's ends where bob's begins"

        testCase "a release naming someone who no longer holds it closes nothing" <| fun () ->
            // The same staleness guard the terminal fold applies: a steal is two events, and
            // acting on the release out of order would close the stretch the take just opened.
            let items =
                merge
                    [ at 1L 0.0 (opened terminalA "shell")
                      at 2L 1.0 (took terminalA (PeerRef bob) 5)
                      at 3L 9.0 (released terminalA (PeerRef ada) (LeaseStolen (PeerRef bob)) 30) ]
            Expect.isEmpty (shapes items) "bob still holds it, so there is no stretch to show"

        testCase "closing a terminal under a live holder still yields a stretch" <| fun () ->
            // `TerminalClosed` clears the lease WITHOUT a release event — that is the
            // Process's rule — so without this the stretch would never appear at all.
            let items =
                merge
                    [ at 1L 0.0 (opened terminalA "shell")
                      at 2L 1.0 (took terminalA (PeerRef bob) 5)
                      at 3L 9.0 (SessionEvent.TerminalClosed { TerminalId = terminalA; Reason = "closed by a peer"; By = None }) ]
            match stretchesOf items with
            | [ stretch ] ->
                Expect.equal stretch.End LeaseHolderGone "nobody decided anything; the terminal went away"
                Expect.equal stretch.Range None "and no end was recorded, so there is nothing to replay"
            | other -> failwithf "expected exactly one stretch, got %d" (List.length other)

        testCase "a stretch with no recorded range has NOTHING to replay, not the whole file" <| fun () ->
            // What a log written before Plan 14 decodes to. `[0, 0)` and a range that happens
            // to be empty mean the same thing to a reader; a default that guessed a real
            // range instead would replay the wrong bytes and look right.
            let items =
                merge
                    [ at 1L 0.0 (opened terminalA "shell")
                      at 2L 1.0 (took terminalA (PeerRef bob) 0)
                      at 3L 9.0 (released terminalA (PeerRef bob) LeaseReleased 0) ]
            Expect.equal (stretchesOf items |> List.map (fun s -> s.Range)) [ None ] "no range, rather than [0, ∞)"

        testCase "two stretches on one terminal have distinct handles" <| fun () ->
            // Leases are not minted with ids, so a tab keyed on the terminal alone could not
            // tell this morning's `vim` session from this afternoon's.
            let items =
                merge
                    [ at 1L 0.0 (opened terminalA "shell")
                      at 2L 1.0 (took terminalA (PeerRef bob) 5)
                      at 3L 9.0 (released terminalA (PeerRef bob) LeaseReleased 30)
                      at 4L 20.0 (took terminalA (PeerRef bob) 30)
                      at 5L 30.0 (released terminalA (PeerRef bob) LeaseReleased 60) ]
            let keys = stretchesOf items |> List.map TerminalStretch.key
            Expect.equal (List.length (List.distinct keys)) 2 "two stretches, two keys"

        testCase "leases on different terminals do not close each other" <| fun () ->
            let items =
                merge
                    [ at 1L 0.0 (opened terminalA "shell")
                      at 2L 0.0 (opened terminalB "logs")
                      at 3L 1.0 (took terminalA (PeerRef ada) 5)
                      at 4L 2.0 (took terminalB (PeerRef bob) 7)
                      at 5L 9.0 (released terminalA (PeerRef ada) LeaseReleased 30) ]
            Expect.equal (shapes items) [ "held:term-a" ] "only the one that ended"
    ]

// --- What did NOT change ------------------------------------------------------------------------

let private unchangedTests =
    testList "The agent's conversation is untouched" [
        testCase "terminal events still contribute NO conversation items" <| fun () ->
            // The load-bearing property of this whole stage. `ConversationProjection` is what
            // builds the agent's context, and the agent already receives block outcomes
            // through `Digest` — folding terminal events in here would double-feed
            // the model and silently change what every turn reads.
            let terminalEvents =
                [ at 1L 0.0 (opened terminalA "build")
                  at 2L 1.0 (started terminalA "1" agentForAda "ls" 1)
                  at 3L 2.0 (completed terminalA "1" (CommandSucceeded 0) 9)
                  at 4L 3.0 (took terminalA (PeerRef bob) 9)
                  at 5L 4.0 (released terminalA (PeerRef bob) LeaseReleased 20)
                  at 6L 5.0 (SessionEvent.TerminalClosed { TerminalId = terminalA; Reason = "done"; By = None }) ]
            let said = [ at 7L 6.0 (sent "1" "ship it") ]
            let withTerminals, _ = ConversationProjection.applyEvents None (terminalEvents @ said) ConversationProjection.empty
            let without, _ = ConversationProjection.applyEvents None said ConversationProjection.empty
            Expect.equal
                (withTerminals.Items |> List.map (fun i -> i.MessageId, i.Author, (ConversationItem.said i), i.Status))
                (without.Items |> List.map (fun i -> i.MessageId, i.Author, (ConversationItem.said i), i.Status))
                "the same items, in the same order, whatever the terminals did"

        testCase "an agent's message anchors at its FIRST WORD, and later words do not move it" <| fun () ->
            // The item opens when the turn does, before the model has spoken, so the chat
            // shows a turn under way. It takes its place at the first delta; every later delta
            // and the completion move the body and the status only, so a streaming answer
            // holds its place exactly as a running command's chip does.
            let turnId = AgentTurnId.create "turn-1" |> expect
            let messageId = message "agent"
            let events =
                [ at 1L 0.0 (AgentTurnStarted { AgentTurnId = turnId; Cause = TurnCause.TriggeredBy (message "1") })
                  at 2L 1.0 (AgentMessageStarted { AgentTurnId = turnId; MessageId = messageId; Antecedent = None })
                  at 3L 2.0 (AgentMessageDelta { AgentTurnId = turnId; MessageId = messageId; Delta = "hel" })
                  at 4L 3.0 (AgentMessageDelta { AgentTurnId = turnId; MessageId = messageId; Delta = "lo" })
                  at 5L 4.0 (AgentMessageCompleted { AgentTurnId = turnId; MessageId = messageId; Body = "hello" }) ]
            let opened, _ = ConversationProjection.applyEvents None (List.take 2 events) ConversationProjection.empty
            let proj, _ = ConversationProjection.applyEvents None events ConversationProjection.empty
            match opened.Items, proj.Items with
            | [ silent ], [ item ] ->
                Expect.equal (EventOffset.value silent.Offset) 2L "until it speaks, it sits where it opened"
                Expect.equal (EventOffset.value item.Offset) 3L "anchored at the first word"
                Expect.equal (ConversationItem.said item) "hello" "even though the rest of the body arrived later"
            | opened, items -> failwithf "expected one item each, got %d and %d" (List.length opened) (List.length items)

        testCase "a completion that carries the only words anchors the message too" <| fun () ->
            // A body that arrives whole, with no delta before it, is still the first word.
            let turnId = AgentTurnId.create "turn-1" |> expect
            let messageId = message "agent"
            let events =
                [ at 1L 0.0 (AgentMessageStarted { AgentTurnId = turnId; MessageId = messageId; Antecedent = None })
                  at 2L 1.0 (AgentMessageCompleted { AgentTurnId = turnId; MessageId = messageId; Body = "done" }) ]
            let proj, _ = ConversationProjection.applyEvents None events ConversationProjection.empty
            match proj.Items with
            | [ item ] -> Expect.equal (EventOffset.value item.Offset) 2L "anchored where the body landed"
            | other -> failwithf "expected one item, got %d" (List.length other)

        testCase "an answer reads AFTER the work that reached it" <| fun () ->
            // The case that showed the old rule wrong: a turn that opens, calls two tools, and
            // only then speaks. Anchored where it opened, the answer sat above the calls — the
            // conclusion, then the twelve commands that reached it. The stream puts the
            // conclusion last, and so does the chat.
            let turnId = AgentTurnId.create "turn-a" |> expect
            let messageId = message "agent"
            let items =
                merge
                    [ at 1L 0.0 (sent "1" "which test command?")
                      at 2L 1.0 (AgentTurnStarted { AgentTurnId = turnId; Cause = TurnCause.TriggeredBy (message "1") })
                      at 3L 2.0 (AgentMessageStarted { AgentTurnId = turnId; MessageId = messageId; Antecedent = None })
                      at 4L 3.0 (SessionEvent.ToolUseStarted { ToolUseId = ToolUseId.create "t-1" |> expect; AgentTurnId = turnId; Namespace = "yession"; Name = "add_repo"; Arguments = None })
                      at 5L 4.0 (SessionEvent.ToolUseStarted { ToolUseId = ToolUseId.create "t-2" |> expect; AgentTurnId = turnId; Namespace = "yession"; Name = "execute_command"; Arguments = None })
                      at 6L 5.0 (AgentMessageDelta { AgentTurnId = turnId; MessageId = messageId; Delta = "`check`" })
                      at 7L 6.0 (AgentMessageCompleted { AgentTurnId = turnId; MessageId = messageId; Body = "`check`" }) ]
            Expect.equal
                (shapes items)
                [ "said:m-1"; "used:t-1"; "used:t-2"; "said:m-agent" ]
                "the question, the work, then the answer"
    ]

// --- The pane's tabs (stage 2) -------------------------------------------------------------

/// A client that has folded these events — the real path a browser takes, so the tab tests
/// run against the model a session actually produces rather than a hand-built one.
let private clientOf (events: EventEnvelope<SessionEvent> list) : ClientModel =
    Support.step
        (EventsPageMsg { Events = events; LastOffset = events |> List.tryLast |> Option.map (fun e -> e.Offset); IsEnd = true })
        (ClientModel.init { PeerId = ada; DisplayName = "swift-heron" })

/// One MORE page, into a client that has already folded some. The live path, and the only
/// one these cases can be written on: a terminal you press for arrives after the pane
/// already has a choice on it, which is the whole of what went wrong.
let private withPage (events: EventEnvelope<SessionEvent> list) (model: ClientModel) : ClientModel =
    Support.step
        (EventsPageMsg { Events = events; LastOffset = events |> List.tryLast |> Option.map (fun e -> e.Offset); IsEnd = true })
        model

/// A client that has read the log through — booted, its local store read, connected to a
/// session whose log ends at the last of `events`, and caught up — so that what arrives next
/// is NEWS (`ClientModel.HeardThrough`) rather than history being replayed.
let private heardOf (events: EventEnvelope<SessionEvent> list) : ClientModel =
    ClientModel.init { PeerId = ada; DisplayName = "swift-heron" }
    |> Support.step HistoryReadMsg
    |> Support.step
        (ConnectedMsg
            { SessionId = sessionId
              AssignedDisplayName = "swift-heron"
              LatestOffset = events |> List.tryLast |> Option.map (fun e -> e.Offset) })
    |> withPage events

let private oneBlock =
    [ at 1L 0.0 (opened terminalA "build")
      at 2L 1.0 (started terminalA "1" byAda "ls -la" 1)
      at 3L 2.0 (completed terminalA "1" (CommandSucceeded 0) 3) ]

let private stripKeys (model: ClientModel) = model.Tabs |> List.map ClientModel.tabKey

/// More events, onto a client that has already folded some — the same page message a browser
/// takes, so "what happened next" is folded by the path that folds everything else.
let private thenFolded (events: EventEnvelope<SessionEvent> list) (model: ClientModel) : ClientModel =
    Support.step
        (EventsPageMsg { Events = events; LastOffset = events |> List.tryLast |> Option.map (fun e -> e.Offset); IsEnd = true })
        model

/// Tapping a command's chip in the chat — what `blockChip` dispatches.
let private chip (terminal: TerminalId) (n: string) : ClientMsg =
    OpenPreviewMsg (Preview.ofSubject (PreviewSubject.Block (terminal, block n)))

/// The subject of the preview on screen, if one is.
let private previewing (model: ClientModel) : PreviewSubject option =
    ClientModel.preview model |> Option.map (fun preview -> preview.Subject)

let private paneTests =
    testList "The pane: terminals, and a preview over one (P2-1)" [
        testCase "a chip opens a preview, and opens the column it is in" <| fun () ->
            let model = clientOf oneBlock
            Expect.isFalse model.TerminalsOpen "the column starts shut"
            let opened' = Support.step (chip terminalA "1") model
            Expect.equal (previewing opened') (Some (PreviewSubject.Block (terminalA, block "1"))) "the command is what is up"
            Expect.isTrue opened'.TerminalsOpen "and the column came with it"

        // A preview is not a tab. A block opened from the chat used to be a fourth kind of
        // tab beside the terminals, kept by a pin, and a reader could not tell it from the
        // terminal it came from: six chips tapped left six tabs and no terminal.
        testCase "a chip opens a preview over its terminal, and the strip still shows the terminal selected" <| fun () ->
            let model = clientOf oneBlock |> Support.step (chip terminalA "1")
            Expect.equal
                (stripKeys model, ClientModel.selectedTerminal model)
                ([ "terminal:term-a" ], Some terminalA)
                "the strip is the terminal, selected, under the preview"

        testCase "a second chip replaces the preview; the strip is unchanged" <| fun () ->
            let before = clientOf oneBlock
            let model =
                [ "1"; "2"; "3"; "4"; "5"; "6" ]
                |> List.fold (fun m n -> Support.step (chip terminalA n) m) before
            Expect.equal
                (stripKeys model, previewing model)
                (stripKeys before, Some (PreviewSubject.Block (terminalA, block "6")))
                "six chips: the strip as it was, and one preview — the last"

        testCase "a chip from a terminal not in my strip opens that terminal under the preview" <| fun () ->
            // Back has to go somewhere, and the strip has to show the terminal the preview is
            // laid over: tapping a command is asking about the terminal it ran in, the same
            // as choosing that terminal from the list. Nothing the session DID put it there.
            let agents =
                [ at 1L 0.0 (openedBy ActorRef.Agent terminalA "running the tests")
                  at 2L 1.0 (started terminalA "1" byAda "ls -la" 1)
                  at 3L 2.0 (completed terminalA "1" (CommandSucceeded 0) 3) ]
            let model = clientOf agents |> Support.step (chip terminalA "1")
            Expect.equal (stripKeys model) [ "terminal:term-a" ] "the terminal the command ran in"

        testCase "back from a preview returns to the terminal's text read, positioned where it was" <| fun () ->
            let model =
                clientOf oneBlock
                |> Support.step (ShowInPaneMsg (ReadingAt (terminalA, block "1")))
                |> Support.step (chip terminalA "1")
                |> Support.step ClosePreviewMsg
            Expect.equal model.Pane (Some (OnTerminal (ReadingAt (terminalA, block "1")))) "where the reader left it"

        testCase "back from a preview returns focus to the chip that opened it" <| fun () ->
            let _, effects = ClientModel.update ClosePreviewMsg (clientOf oneBlock |> Support.step (chip terminalA "1"))
            Expect.equal
                effects
                [ ClientEffect.Move (DomMove.FocusChat (PreviewSubject.Block (terminalA, block "1"))) ]
                "the preview is leaving the document with focus in it, and the chip is where it came from"

        testCase "a preview's key and its terminal's tab key are different, drawn from the same ids" <| fun () ->
            // A block id and a terminal id come from the same alphabet, and a collision would
            // silently answer for the wrong thing.
            let sameName = TerminalId.create "xy" |> expect
            let asBlock = BlockId.create "xy" |> expect
            Expect.notEqual
                (ClientModel.tabKey sameName)
                (PreviewSubject.key (PreviewSubject.Block (sameName, asBlock)))
                "one name, two keys"

        testCase "a preview is about a terminal only when it is a terminal's" <| fun () ->
            // What it is laid over and what a replay of it reads — and a file has none, so the
            // answer is an option rather than a terminal nobody chose.
            let stretch =
                { Offset = EventOffset.create 9L |> expect
                  TerminalId = terminalB
                  Title = "shell"
                  Holder = PeerRef bob
                  End = LeaseReleased
                  Range = Some (1, 9)
                  StartedAt = epoch
                  EndedAt = epoch.AddMinutes 1.0 }
            let picture = Content.ContentRef.create "artifacts/chart.png/0000-ab12cd" |> expect
            Expect.equal
                [ PreviewSubject.terminal (PreviewSubject.Block (terminalA, block "1"))
                  PreviewSubject.terminal (PreviewSubject.Stretch stretch)
                  PreviewSubject.terminal (PreviewSubject.Content picture) ]
                [ Some terminalA; Some terminalB; None ]
                "a block's, a stretch's, and a file's — there is none"

        testCase "a block preview renders the command and its output, read-only" <| fun () ->
            // From the chunks the client already has, through the very renderer the
            // terminal's own history uses — a block read from the chat must not be a second
            // rendering free to drift from the first.
            let model =
                clientOf oneBlock
                |> Support.step (TerminalRecordsMsg (terminalA, [ 1, { At = 0.0; Kind = TranscriptOutput; Data = "total 0\n" } ]))
                |> Support.step (chip terminalA "1")
            let html = Support.render model
            let required =
                [ "the preview", Dom.attr Dom.Hooks.panePreview "block:term-a:b-1"
                  // Its close rides its pivot item; the way back to the terminal is that
                  // terminal's own item, beside it.
                  "its close", Dom.Hooks.panePreviewClose
                  "the block's read-only view", Dom.attr Dom.Hooks.paneBlock "b-1"
                  "the command", "ls -la"
                  // What it printed, as TEXT — the cheap read of the same bytes, through the
                  // same renderer the terminal's own history uses. Whether the OTHER read is
                  // offered is a different question, and `readsTests` is where it is asked.
                  "what it printed", "total 0" ]
            for label, marker in required do
                Expect.isTrue (html.Contains marker) (sprintf "%s (`%s`) must render" label marker)

        testCase "a preview has no command line" <| fun () ->
            // The reader is reading, not typing; back restores the terminal and its composer.
            let html = Support.render (clientOf oneBlock |> Support.step (chip terminalA "1"))
            Expect.isFalse
                (html.Contains (Dom.attr Dom.Hooks.terminalInput (BodyKey.terminalDraft terminalA ada)))
                "no composer under a preview"

        // A preview has a pivot item, slanted and with its own close — but of its OWN kind,
        // never a terminal's tab, which is the fault the last preview was removed for.
        testCase "a preview is never one of the terminals' tabs" <| fun () ->
            let html = Support.render (clientOf oneBlock |> Support.step (chip terminalA "1"))
            Expect.equal
                (Text.RegularExpressions.Regex.Matches (html, Dom.Hooks.paneTab + "=\"([^\"]*)\"")
                 |> Seq.map (fun m -> m.Groups.[1].Value)
                 |> List.ofSeq)
                [ "terminal:term-a" ]
                "the strip holds the terminal, and only the terminal"

        testCase "the strip is one tablist, and every tab in it is a real tab" <| fun () ->
            let model =
                clientOf [ at 1L 0.0 (opened terminalA "build"); at 2L 1.0 (opened terminalB "logs") ]
                |> Support.step (ShowInPaneMsg (Reading terminalA))
            let html = Support.render model
            Expect.isTrue (html.Contains "role=\"tablist\"") "one tablist"
            // Roving tabindex, ARIA's manual-activation variant: exactly one tab is a Tab
            // stop, and the arrow keys move between them without mounting a panel per keypress.
            let selectedStops =
                html.Split ([| "role=\"tab\"" |], System.StringSplitOptions.None)
                |> Array.skip 1
                |> Array.filter (fun after -> (after.Split '>').[0].Contains "tabindex=\"0\"")
            Expect.equal (Array.length selectedStops) 1 "exactly one tab is a Tab stop"
            Expect.isTrue (html.Contains "aria-selected=\"true\"") "and it is the selected one"
    ]

// --- A terminal's name, on every surface -----------------------------------------------------

/// The inside of the one element carrying `hook` — scoped to that element, so another surface
/// saying the same words cannot satisfy an assertion about this one. The element's own
/// subtree, balanced on its tag name rather than cut at the first close.
let private markupAt (hook: string) (html: string) : string =
    match html.IndexOf hook with
    | -1 -> failwithf "nothing carries %s" hook
    | at ->
        let opensAt = html.LastIndexOf ('<', at)
        let tag = (html.Substring (opensAt + 1)).Split([| ' '; '>' |]).[0]
        let bodyFrom = html.IndexOf ('>', at) + 1
        let opener = "<" + tag
        let closer = "</" + tag + ">"
        let mutable depth = 1
        let mutable i = bodyFrom
        let mutable endsAt = -1
        while endsAt < 0 && i < html.Length do
            let startsHere (what: string) = i + what.Length <= html.Length && html.Substring (i, what.Length) = what
            if startsHere closer then
                depth <- depth - 1
                if depth = 0 then endsAt <- i
            elif startsHere (opener + " ") || startsHere (opener + ">") then
                depth <- depth + 1
            i <- i + 1
        if endsAt < 0 then html.Substring bodyFrom else html.Substring (bodyFrom, endsAt - bodyFrom)

/// The same element's words, as a person reads them.
let private textAt (hook: string) (html: string) : string =
    Text.RegularExpressions.Regex.Replace(Support.readable (markupAt hook html), @"\s+", " ").Trim ()

/// Every value `attribute` takes on the elements carrying `hook`, in document order.
let private attributeOf (hook: string) (attribute: string) (html: string) : string list =
    html.Split ([| hook |], StringSplitOptions.None)
    |> Array.skip 1
    |> Array.toList
    |> List.choose (fun after ->
        let tag = (after.Split '>').[0]
        let marker = attribute + "=\""
        match tag.IndexOf marker with
        | -1 -> None
        | from ->
            let start = from + marker.Length
            Some (tag.Substring (start, tag.IndexOf ('"', start) - start)))

/// Three terminals nobody named, opened in order, each shown in the pane and the middle one
/// last — so it is the selected tab, and the head is naming it.
let private threeUntitled =
    [ at 1L 0.0 (opened terminalA "")
      at 2L 1.0 (opened terminalB "")
      at 3L 2.0 (opened terminalC "")
      at 4L 3.0 (started terminalB "1" byAda "make" 1) ]

let private showingAll (model: ClientModel) =
    model
    |> Support.step (ShowInPaneMsg (Reading terminalA))
    |> Support.step (ShowInPaneMsg (Reading terminalC))
    |> Support.step (ShowInPaneMsg (Reading terminalB))

let private namingTests =
    testList "A terminal's name, on every surface" [
        // The head that also named the selected terminal is gone with the pivot: the pivot's
        // selected item is where the pane says what is on screen.
        testCase "the pivot and the all page agree on a terminal's name" <| fun () ->
            let model = clientOf threeUntitled |> showingAll
            let strip = Support.render model
            let list = Support.render (Support.step ToggleSwitcherMsg model)
            Expect.equal
                [ textAt Dom.Hooks.terminalTabName (markupAt (Dom.attr Dom.Hooks.terminalTab "term-b") strip)
                  textAt (Dom.attr Dom.Hooks.terminalListRow "term-b") list ]
                [ "term 2"; "term 2" ]
                "one terminal, one name, wherever it is named"

        testCase "a chat chip names the terminal its command ran in" <| fun () ->
            let html = Support.render (clientOf threeUntitled)
            Expect.equal
                (textAt (Dom.attr Dom.Hooks.chatBlockTerminal "term-b") (markupAt (Dom.attr Dom.Hooks.chatBlock "b-1") html))
                "term 2"
                "where it ran, on the chip"

        testCase "a chat chip's accessible name says which terminal its command ran in" <| fun () ->
            // The chip's own words are replaced by its label for a screen reader, so the label
            // has to carry the terminal itself rather than lean on the visible name.
            let html = Support.render (clientOf threeUntitled)
            let label = attributeOf (Dom.attr Dom.Hooks.chatBlock "b-1") "aria-label" html |> List.exactlyOne
            Expect.stringContains label "term 2" "where it ran, heard"

        testCase "three untitled terminals' kill controls have three accessible names" <| fun () ->
            // Nine kill buttons that all said "Kill terminal" were one control as far as
            // anybody listening could tell.
            let list = Support.render (Support.step ToggleSwitcherMsg (clientOf threeUntitled |> showingAll))
            let labels = attributeOf "data-terminal-close=" "aria-label" (markupAt Dom.Hooks.contentList list)
            Expect.equal (List.length labels) 3 "one kill per open terminal"
            Expect.equal (List.length (List.distinct labels)) 3 "and no two named alike"

        testCase "a terminal somebody killed names them on its closed band" <| fun () ->
            // The sentence it was recorded with says "a peer" — to the very person who
            // pressed kill. Who did it is the event's party, drawn as the name they wear.
            let model =
                clientOf
                    [ at 1L 0.0 (SessionEvent.PeerJoined { PeerId = bob; DisplayName = "brave-owl"; User = None })
                      at 2L 1.0 (opened terminalA "build")
                      at 3L 2.0 (SessionEvent.TerminalClosed { TerminalId = terminalA; Reason = "closed by a peer"; By = Some (PeerRef bob) }) ]
                |> Support.step (ShowInPaneMsg (Reading terminalA))
            Expect.stringContains
                (textAt (Dom.attr Dom.Hooks.terminalClosedBand (TerminalId.value terminalA)) (Support.render model))
                "brave-owl"
                "the band says who killed it"
    ]

// --- What the pane had, across a reload (P0-4) ----------------------------------------------

/// A terminal id, drawn from the alphabet one allows.
let private genTerminal : Gen<TerminalId> =
    let chars = Gen.item ([ 'a' .. 'z' ] @ [ 'A' .. 'Z' ] @ [ '0' .. '9' ] @ [ '-' ])
    Gen.string (Range.linear 2 12) chars |> Gen.map (fun raw -> TerminalId.create raw |> expect)

/// A browser coming back to this session with `memory` kept: booted with it, its local store
/// read, connected to a session whose log ends at the last of `pages`, and each page folded
/// in turn — the order a reload takes.
let private reloaded (memory: PaneMemory) (pages: EventEnvelope<SessionEvent> list list) : ClientModel =
    let latest = pages |> List.concat |> List.tryLast |> Option.map (fun e -> e.Offset)
    let booted =
        ClientModel.init { PeerId = ada; DisplayName = "swift-heron" }
        |> ClientModel.remembered (Some memory)
        |> Support.step HistoryReadMsg
        |> Support.step (ConnectedMsg { SessionId = sessionId; AssignedDisplayName = "swift-heron"; LatestOffset = latest })
    pages |> List.fold (fun model page -> withPage page model) booted

let private remembering (tabs: TerminalId list) : PaneMemory =
    { PaneMemory.untouched with PaneMemory.Tabs = tabs; PaneMemory.Open = true }

let private reloadTests =
    testList "What the pane had, across a reload (P0-4)" [
        testCase "a remembered pane round-trips through its codec" <| fun () ->
            Property.check (property {
                let! tabs = Gen.list (Range.linear 0 5) genTerminal
                let! selected = Gen.option genTerminal
                let! isOpen = Gen.bool
                let memory = { PaneMemory.Tabs = tabs; PaneMemory.Selected = selected; PaneMemory.Open = isOpen }
                Expect.equal
                    (Codec.fromString PaneMemory.codec (Codec.toString PaneMemory.codec memory))
                    (Ok memory)
                    "what is written is what is read"
            })

        // The one migration the stored shape has had (P2-1): the strip held blocks, stretches
        // and files as tabs, and a `pinned` list. Those are previews now, and a preview is
        // exactly what a reload does not bring back — but the terminals beside them in the
        // same stored list still do, so the memory is read rather than refused.
        testCase "a memory an older build wrote still reads, its previews dropped" <| fun () ->
            let older =
                """{"tabs":["terminal:term-a","block:term-a:b-1","stretch:term-a@3","content:artifacts/chart.png/0000-ab12cd","terminal:term-b"],"pinned":["terminal:term-a","block:term-a:b-1"],"selected":"block:term-a:b-1","open":true}"""
            Expect.equal
                (Codec.fromString PaneMemory.codec older)
                (Ok { PaneMemory.Tabs = [ terminalA; terminalB ]; PaneMemory.Selected = None; PaneMemory.Open = true })
                "the terminals, in order; the selection was a preview, so there is none"

        testCase "a remembered pane comes back open before anything has arrived" <| fun () ->
            let model = ClientModel.init { PeerId = ada; DisplayName = "swift-heron" } |> ClientModel.remembered (Some (remembering []))
            Expect.isTrue model.TerminalsOpen "the column is open from the first paint"

        testCase "a session this browser has never seen starts as it always did" <| fun () ->
            let fresh = ClientModel.init { PeerId = ada; DisplayName = "swift-heron" }
            Expect.equal (ClientModel.paneMemory (ClientModel.remembered None fresh)) PaneMemory.untouched "nothing remembered, nothing changed"

        testCase "the remembered strip replaces the one the log rebuilt" <| fun () ->
            // Replaying the log reopens every terminal this person ever opened; the strip
            // they had says which of those they still wanted.
            let events = [ at 1L 0.0 (opened terminalA "build"); at 2L 1.0 (opened terminalB "shell") ]
            let model = reloaded (remembering [ terminalB ]) [ events ]
            Expect.equal (stripKeys model) [ "terminal:term-b" ] "only the tab that was open"

        testCase "a restored tab for a terminal the session does not have is dropped after the first page" <| fun () ->
            let model = reloaded (remembering [ terminalA; TerminalId.create "term-gone" |> expect ]) [ oneBlock ]
            Expect.equal (stripKeys model) [ "terminal:term-a" ] "the tab onto nothing is gone"

        testCase "a restored tab onto a terminal from the last page of catch-up survives the first" <| fun () ->
            // The strip is checked once the log is read THROUGH, not at its first page: a
            // check made between pages would drop a terminal the session does have.
            let model =
                reloaded
                    (remembering [ terminalB ])
                    [ [ at 1L 0.0 (opened terminalA "build") ]; [ at 2L 1.0 (opened terminalB "shell") ] ]
            Expect.equal (stripKeys model) [ "terminal:term-b" ] "the later terminal's tab is back"

        testCase "a restored selection that names a tab in the strip is the selection" <| fun () ->
            let events = [ at 1L 0.0 (opened terminalA "build"); at 2L 1.0 (opened terminalB "shell") ]
            let memory = { remembering [ terminalA; terminalB ] with PaneMemory.Selected = Some terminalB }
            let model = reloaded memory [ events ]
            Expect.equal (ClientModel.selectedTerminal model) (Some terminalB) "the terminal that was on top is again"

        testCase "a preview is not remembered; the terminal under it is" <| fun () ->
            let model = clientOf oneBlock |> Support.step (chip terminalA "1")
            Expect.equal
                (ClientModel.paneMemory model)
                { PaneMemory.Tabs = [ terminalA ]; PaneMemory.Selected = Some terminalA; PaneMemory.Open = true }
                "the strip and the terminal the preview was over"
    ]

// --- The hidden pane is an edge tab; the pane opens itself (P1-4) ---------------------------

/// The shut pane's edge tab, and nothing else of the page: its opening tag through its closing
/// one. A whole page carries the strip, the list and the chat, each of which can say
/// "terminal" or carry a running dot of its own, so an unscoped assertion would be answered
/// by whichever surface happened to be right.
let private edgeTab (model: ClientModel) : string =
    let html = Support.render model
    let hook = Dom.attr Dom.Hooks.contentToggle "show"
    match html.IndexOf hook with
    | -1 -> failwith "the shut pane offers no way back in"
    | at ->
        let opens = html.LastIndexOf ("<button", at)
        html.Substring (opens, html.IndexOf ("</button>", at) - opens)

/// A browser's first look at this session, with nothing remembered for it: booted, its local
/// store read, connected to a session whose log ends at the last of `pages`, and each page
/// folded in turn — `reloaded`'s order, with no memory. `wide` is the browser's answer to
/// whether the pane would sit beside the chat (`Browser.fs` seeds it from the breakpoint).
let private firstLook (wide: bool) (pages: EventEnvelope<SessionEvent> list list) : ClientModel =
    let latest = pages |> List.concat |> List.tryLast |> Option.map (fun e -> e.Offset)
    let booted =
        { ClientModel.init { PeerId = ada; DisplayName = "swift-heron" } with PaneOpensItself = wide }
        |> ClientModel.remembered None
        |> Support.step HistoryReadMsg
        |> Support.step (ConnectedMsg { SessionId = sessionId; AssignedDisplayName = "swift-heron"; LatestOffset = latest })
    pages |> List.fold (fun model page -> withPage page model) booted

/// Two terminals open and one ended, so a count of what is OPEN and a count of what ever was
/// give different answers.
let private twoOpenOneEnded =
    [ at 1L 0.0 (opened terminalA "build")
      at 2L 1.0 (opened terminalB "shell")
      at 3L 2.0 (opened terminalC "old")
      at 4L 3.0 (SessionEvent.TerminalClosed { TerminalId = terminalC; Reason = "exited"; By = None }) ]

let private edgeTabTests =
    testList "The hidden pane is an edge tab (P1-4)" [
        testCase "the hidden pane's edge tab counts the open terminals" <| fun () ->
            let tab = edgeTab (clientOf twoOpenOneEnded)
            Expect.stringContains tab (Dom.attr Dom.Hooks.terminalsOpen "2") "two open, the ended one not counted"
            Expect.stringContains tab (Dom.Text.terminalsCount 2) "and says so in words"

        testCase "the edge tab marks a terminal running a command" <| fun () ->
            let running = clientOf (twoOpenOneEnded @ [ at 5L 4.0 (started terminalA "1" byAda "make" 1) ])
            Expect.stringContains (edgeTab running) Dom.Hooks.terminalsRunning "the running mark is on the tab"

        testCase "the edge tab says which terminal is running, by the name the pane gives it" <| fun () ->
            let running = clientOf (twoOpenOneEnded @ [ at 5L 4.0 (started terminalA "1" byAda "make" 1) ])
            let name =
                Projection.tryFind terminalA running.Terminals
                |> Option.map (TerminalName.display running.Terminals)
                |> Option.defaultWith (fun () -> failwith "terminal A is in the projection")
            Expect.stringContains (edgeTab running) (Dom.Text.showTerminalsNamed 2 [ name ]) "its accessible name names it"

        testCase "the edge tab carries no running mark when nothing runs" <| fun () ->
            let finished =
                clientOf
                    (twoOpenOneEnded
                     @ [ at 5L 4.0 (started terminalA "1" byAda "make" 1)
                         at 6L 5.0 (completed terminalA "1" (CommandSucceeded 0) 9) ])
            Expect.isFalse ((edgeTab finished).Contains Dom.Hooks.terminalsRunning) "a finished command is not running"

        testCase "a desktop client with an open terminal and no memory opens the pane once the log is read" <| fun () ->
            let model = firstLook true [ [ at 1L 0.0 (opened terminalA "build") ] ]
            Expect.isTrue model.TerminalsOpen "the pane is open on the first look"

        testCase "a phone client does not open the pane by itself" <| fun () ->
            // There the pane is the whole screen, and opening it would hide the chat.
            let model = firstLook false [ [ at 1L 0.0 (opened terminalA "build") ] ]
            Expect.isFalse model.TerminalsOpen "the chat stays on screen"

        testCase "a session with no open terminal leaves the pane shut" <| fun () ->
            let model = firstLook true [ [ at 1L 0.0 (sent "1" "hello") ] ]
            Expect.isFalse model.TerminalsOpen "nothing to see, nothing opened"

        testCase "the pane does not open itself before the log has been read through" <| fun () ->
            // One page of two: a session that ends later in the log has not been heard yet.
            let latest = Some (EventOffset.create 9L |> expect)
            let partial =
                { ClientModel.init { PeerId = ada; DisplayName = "swift-heron" } with PaneOpensItself = true }
                |> Support.step HistoryReadMsg
                |> Support.step (ConnectedMsg { SessionId = sessionId; AssignedDisplayName = "swift-heron"; LatestOffset = latest })
                |> withPage [ at 1L 0.0 (opened terminalA "build") ]
            Expect.isFalse partial.TerminalsOpen "still catching up"

        testCase "a pane remembered shut stays shut" <| fun () ->
            let latest = Some (EventOffset.create 1L |> expect)
            let model =
                { ClientModel.init { PeerId = ada; DisplayName = "swift-heron" } with PaneOpensItself = true }
                |> ClientModel.remembered (Some PaneMemory.untouched)
                |> Support.step HistoryReadMsg
                |> Support.step (ConnectedMsg { SessionId = sessionId; AssignedDisplayName = "swift-heron"; LatestOffset = latest })
                |> withPage [ at 1L 0.0 (opened terminalA "build") ]
            Expect.isFalse model.TerminalsOpen "this browser's own answer wins"

        testCase "a pane shut before the log arrived is not opened over that" <| fun () ->
            let latest = Some (EventOffset.create 1L |> expect)
            let model =
                { ClientModel.init { PeerId = ada; DisplayName = "swift-heron" } with PaneOpensItself = true }
                |> Support.step ToggleContentMsg
                |> Support.step ToggleContentMsg
                |> Support.step HistoryReadMsg
                |> Support.step (ConnectedMsg { SessionId = sessionId; AssignedDisplayName = "swift-heron"; LatestOffset = latest })
                |> withPage [ at 1L 0.0 (opened terminalA "build") ]
            Expect.isFalse model.TerminalsOpen "somebody already answered"

        testCase "a pane shut after it opened itself stays shut when more arrives" <| fun () ->
            let model =
                firstLook true [ [ at 1L 0.0 (opened terminalA "build") ] ]
                |> Support.step ToggleContentMsg
                |> withPage [ at 2L 1.0 (opened terminalB "shell") ]
            Expect.isFalse model.TerminalsOpen "it opens itself once, and is not argued with"

        testCase "a pane that opens itself shows the reader's own terminal" <| fun () ->
            let model = firstLook true [ [ at 1L 0.0 (opened terminalA "build") ] ]
            Expect.equal (ClientModel.selectedTerminal model) (Some terminalA) "the terminal this reader opened"

        testCase "a pane that opens itself with nothing of this reader's in the strip opens the switcher" <| fun () ->
            // The strip holds only what this reader opened, so a terminal somebody else opened
            // gives it nothing to show; an empty pane over a running build is the wrong answer.
            let model = firstLook true [ [ at 1L 0.0 (openedBy (PeerRef bob) terminalA "build") ] ]
            Expect.isTrue model.Switcher "the switcher, which says what is here"

        testCase "an empty pane offers one way to make a terminal" <| fun () ->
            let html = Support.render (clientOf [] |> Support.step ToggleContentMsg)
            let doors =
                System.Text.RegularExpressions.Regex.Matches(html, "(data-pane-new|data-terminal-new)[\\s=>]").Count
            Expect.equal doors 1 "one New terminal, not two"
    ]

// --- How a block ended, on every surface ----------------------------------------------------

/// One command in terminal A that ended as `result`, with terminal A on screen — so its block
/// is in the pane's scrollback and its chip in the chat, both on one page.
let private endedAs (result: CommandResult) : string =
    clientOf
        [ at 1L 0.0 (opened terminalA "build")
          at 2L 1.0 (started terminalA "1" byAda "false" 1)
          at 3L 2.0 (completed terminalA "1" result 3) ]
    |> Support.step (ShowInPaneMsg (Reading terminalA))
    |> Support.render

/// Every status mark drawn inside `html`, by the token it carries.
let private marksIn (html: string) : string list =
    Text.RegularExpressions.Regex.Matches (html, Dom.Hooks.blockMark + "=\"([^\"]*)\"")
    |> Seq.map (fun m -> m.Groups.[1].Value)
    |> List.ofSeq

let private statusTests =
    testList "A block says how it ended, the same way everywhere" [
        testCase "a succeeded block says so in the pane and in the chat, with the same token" <| fun () ->
            // The pane drew nothing for a success, so a finished command and one still waiting
            // on its first byte looked alike there. The article's own status attribute was
            // right all along — and is not what anybody reads.
            let html = endedAs (CommandSucceeded 0)
            Expect.equal
                [ marksIn (markupAt (Dom.attr Dom.Hooks.terminalBlock "b-1") html)
                  marksIn (markupAt (Dom.attr Dom.Hooks.chatBlock "b-1") html) ]
                [ [ Dom.Text.blockOk ]; [ Dom.Text.blockOk ] ]
                "one mark on each surface, saying the same thing"

        testCase "a failed chip has an accessible word" <| fun () ->
            // Its name was "false, failed…" only by the luck of the token being a word; the
            // glyph is decoration, and the exit code alone is a number nobody can place.
            let label =
                attributeOf (Dom.attr Dom.Hooks.chatBlock "b-1") "aria-label" (endedAs (CommandFailed 1))
                |> List.exactlyOne
            Expect.stringContains label "failed" "a screen reader hears that it failed"

        testCase "a failed block's mark is heard in words in the pane, not as a number" <| fun () ->
            let mark = textAt Dom.Hooks.blockMark (markupAt (Dom.attr Dom.Hooks.terminalBlock "b-1") (endedAs (CommandFailed 1)))
            Expect.stringContains mark "failed" "the glyph is a picture; the words are for whoever cannot see it"

        testCase "a block's facts are on screen without a press" <| fun () ->
            // They were behind a `…` that read as a menu. A command that did not get to exit
            // carries the one fact its mark cannot: why.
            let block =
                markupAt
                    (Dom.attr Dom.Hooks.terminalBlock "b-1")
                    (endedAs (CommandExecutionFailed "the session stopped while it was running"))
            Expect.isTrue (block.Contains Dom.Hooks.terminalBlockFacts) "the facts are rendered"
            Expect.isFalse (block.Contains "<details") "and nothing has to be opened to read them"

        testCase "a terminal running a command marks its tab, and an idle one does not" <| fun () ->
            let strip = Support.render (clientOf threeUntitled |> showingAll)
            Expect.equal
                ([ terminalA; terminalB; terminalC ]
                 |> List.map (fun id ->
                     (markupAt (Dom.attr Dom.Hooks.terminalTab (TerminalId.value id)) strip).Contains Dom.Hooks.terminalTabRunning))
                [ false; true; false ]
                "only term-b has a command running"

        // A command that failed in a terminal nobody was looking at used to leave no trace on
        // its tab once it finished — a running pulse, then nothing (desktop journey, finding
        // 7). Its state outlives the run, on the pivot and on the all page alike.
        testCase "a terminal whose last command failed says so on its tab and its row" <| fun () ->
            let model =
                clientOf (threeUntitled @ [ at 5L 4.0 (completed terminalB "1" (CommandFailed 2) 3) ])
                |> showingAll
                |> Support.step (ShowInPaneMsg (Reading terminalA))
            let failedMark = Dom.attr Dom.Hooks.paneMark "failed"
            // The row's mark sits beside its name button, not in it: what follows term-b's
            // name up to the next row is term-b's.
            let rowOf (html: string) =
                let list = markupAt Dom.Hooks.contentList html
                let from = list.IndexOf (Dom.attr Dom.Hooks.terminalListRow "term-b")
                let upto = list.IndexOf ("role=\"listitem\"", from)
                list.Substring (from, (if upto < 0 then list.Length else upto) - from)
            Expect.equal
                ((markupAt (Dom.attr Dom.Hooks.terminalTab "term-b") (Support.render model)).Contains failedMark,
                 (rowOf (Support.render (Support.step ToggleSwitcherMsg model))).Contains failedMark)
                (true, true)
                "failed, on both"
    ]

// --- What just ran is never folded away ------------------------------------------------------

/// One block for a grouping case, by Ada unless said otherwise. Its transcript range is
/// nothing real: grouping reads the author, the status and the order, and nothing else.
let private blockOf (authority: Authority) (n: string) (status: BlockStatus) : Block =
    { BlockId = block n
      QueueId = None
      Authority = authority
      Command = "echo " + n
      Background = false
      FromSeq = 0
      ToSeq = Some 1
      Status = status }

let private doneBy (authority: Authority) (n: string) = blockOf authority n (BlockFinished (CommandSucceeded 0))

/// Each group as the blocks in it — a run joined with `+` — so a whole layout is one line.
let private groupsOf (blocks: Block list) : string list =
    BlockGroup.ofBlocks blocks
    |> List.map (function
        | BlockGroup.Alone b -> BlockId.value b.BlockId
        | BlockGroup.Run (leader, rest) -> leader :: rest |> List.map (fun b -> BlockId.value b.BlockId) |> String.concat "+")

/// Ada running `count` commands one after another in term-a, each finished before the next,
/// from transcript offset `from` — and the pane on that terminal.
let private ranInA (first: int) (count: int) (from: int64) : EventEnvelope<SessionEvent> list =
    [ for i in 0 .. count - 1 do
        let n = first + i
        let offset = from + int64 (2 * i)
        yield at offset (float offset) (started terminalA (string n) byAda (sprintf "echo %d" n) (10 * n))
        yield at (offset + 1L) (float offset + 0.5) (completed terminalA (string n) (CommandSucceeded 0) (10 * n + 5)) ]

/// Three commands in term-a, the pane on it: the first two are a run, the third stands alone.
let private threeRan : ClientModel =
    clientOf (at 1L 0.0 (opened terminalA "shell") :: ranInA 1 3 2L)
    |> Support.step (ShowInPaneMsg (Reading terminalA))

let private firstRun = FoldKey.Commands (terminalA, block "1")

/// Whether the run that starts at `leader` is drawn open, read off the rendered pane.
let private runShownOpen (leader: string) (model: ClientModel) : bool =
    (markupAt (Dom.attr Dom.Hooks.terminalBlockRun ("b-" + leader)) (Support.render model)).Contains "data-fold-open=\"yes\""

let private latestTests =
    testList "What just ran is never folded away" [
        // Three real-browser journeys found it at once: `pwd`, then `echo second`, and both
        // went behind "ran 2 commands" — the answer just asked for, a click away, every time.
        testCase "the newest command is never in a run" <| fun () ->
            Expect.equal
                (groupsOf [ doneBy byAda "1"; doneBy byAda "2" ])
                [ "b-1"; "b-2" ]
                "two commands, each on screen"

        testCase "a running command is never in a run, and its run-mates still fold either side" <| fun () ->
            Expect.equal
                (groupsOf
                    [ doneBy byAda "1"; doneBy byAda "2"; blockOf byAda "3" BlockRunning
                      doneBy byAda "4"; doneBy byAda "5"; doneBy byAda "6" ])
                [ "b-1+b-2"; "b-3"; "b-4+b-5"; "b-6" ]
                "the loop still running is drawn whole"

        testCase "a run keeps its key as it grows" <| fun () ->
            // The key a fold's open state is kept under: if the next command re-keyed the run,
            // whatever the reader opened would shut under them.
            let before = [ doneBy byAda "1"; doneBy byAda "2"; doneBy byAda "3" ]
            Expect.equal
                (BlockGroup.holding terminalA (block "1") (before @ [ doneBy byAda "4" ]))
                (BlockGroup.holding terminalA (block "1") before)
                "the same fold before and after"

        // The cases below assert a run is OPEN; this is what makes that mean something.
        testCase "an older run nobody opened is shut" <| fun () ->
            Expect.isFalse (runShownOpen "1" threeRan) "folded to its line"

        testCase "a run someone opened stays open when the next command runs" <| fun () ->
            let model =
                threeRan
                |> Support.step (FoldSetMsg (firstRun, true))
                |> thenFolded (ranInA 4 1 8L)
            Expect.isTrue (runShownOpen "1" model) "still open, one command longer"

        testCase "a run someone opened stays open across a hand-back" <| fun () ->
            // Live mode swaps the history for the screen and back, which rebuilt the
            // `<details>` and shut it.
            let model =
                threeRan
                |> Support.step (FoldSetMsg (firstRun, true))
                |> thenFolded [ at 8L 8.0 (took terminalA (PeerRef ada) 40); at 9L 9.0 (released terminalA (PeerRef ada) LeaseReleased 50) ]
            Expect.isTrue (runShownOpen "1" model) "open as it was left"

        testCase "a run someone opened stays open across a preview and show in terminal" <| fun () ->
            let model =
                threeRan
                |> Support.step (FoldSetMsg (firstRun, true))
                |> Support.step (chip terminalA "3")
                |> Support.step (ShowInTerminalMsg (terminalA, block "3"))
            Expect.isTrue (runShownOpen "1" model) "open as it was left"

        testCase "a run someone shut stays shut" <| fun () ->
            let model =
                threeRan
                |> Support.step (FoldSetMsg (firstRun, true))
                |> Support.step (FoldSetMsg (firstRun, false))
            Expect.isFalse (runShownOpen "1" model) "shut, as they asked"

        testCase "show in terminal opens the run its command is in" <| fun () ->
            // Scrolling to a command inside a shut run scrolls to nothing, and its mark plays
            // on an element nobody can see.
            let model =
                threeRan
                |> Support.step (chip terminalA "2")
                |> Support.step (ShowInTerminalMsg (terminalA, block "2"))
            Expect.isTrue (runShownOpen "1" model) "the run holding it is open"
    ]

// --- Keyframes and the ranged cast (stage 3) --------------------------------------------------

/// The output records of a `.cast`, in order — what a player would feed the emulator.
let private outputsOf (cast: string) : string list =
    cast.Split '\n'
    |> Array.filter (fun l -> l.Trim().Length > 0)
    |> Array.toList
    |> List.choose (fun line ->
        match Codec.fromString Transcripts.line line with
        | Ok (TranscriptRecordLine r) when r.Kind = TranscriptOutput || r.Kind = TranscriptStderr -> Some r.Data
        | _ -> None)

let private timesOf (cast: string) : float list =
    cast.Split '\n'
    |> Array.filter (fun l -> l.Trim().Length > 0)
    |> Array.toList
    |> List.choose (fun line ->
        match Codec.fromString Transcripts.line line with
        | Ok (TranscriptRecordLine r) -> Some r.At
        | _ -> None)

let private headerOf (cast: string) : TranscriptHeader =
    match Codec.fromString Transcripts.line ((cast.Split '\n').[0]) with
    | Ok (TranscriptHeaderLine h) -> h
    | _ -> failwith "the first line of a cast is its header"

/// Feed an emulator and read the screen back.
let private screenOf (cols: int) (rows: int) (chunks: string list) : Async<string> =
    async {
        let emulator = Yession.Host.Emulator.openEmulator cols rows
        for chunk in chunks do emulator.Write chunk
        let! screen = emulator.Serialize ()
        emulator.Dispose ()
        return screen
    }

let private baseHeader : TranscriptHeader = { Width = 80; Height = 24; Timestamp = 0L }

let private keyframeTests =
    testList "Keyframes and the ranged cast (Plan 14, stage 3)" [
        testCase "a ranged cast REBASES its times to the range's first record" <| fun () ->
            // asciicast times are relative to the start of the FILE. Slice a block that ran
            // forty minutes in and the player sits idle for forty minutes before the first
            // frame — broken in a way that looks exactly like a hang.
            let records =
                [ 1, { At = 0.5; Kind = TranscriptOutput; Data = "early\r\n" }
                  2, { At = 2400.0; Kind = TranscriptOutput; Data = "the block\r\n" }
                  3, { At = 2400.25; Kind = TranscriptOutput; Data = "more\r\n" } ]
            let cast = TranscriptReplay.range baseHeader None 2 4 records
            Expect.equal (timesOf cast) [ 0.0; 0.25 ] "the range starts at zero and keeps its own spacing"
            Expect.equal (outputsOf cast) [ "the block\r\n"; "more\r\n" ] "and holds only the range"

        testCase "a keyframe paints the screen FIRST, and overrides the header's geometry" <| fun () ->
            // The header records the size the terminal OPENED at; a resize before the range
            // changed it, and a recording replayed under the wrong geometry rewraps every
            // line in it.
            let keyframe = { Seq = 2; Cols = 120; Rows = 40; Screen = "PAINTED" }
            let records = [ 2, { At = 9.0; Kind = TranscriptOutput; Data = "after\r\n" } ]
            let cast = TranscriptReplay.range baseHeader (Some keyframe) 2 3 records
            Expect.equal (outputsOf cast) [ "PAINTED"; "after\r\n" ] "the screen, then the range"
            Expect.equal (timesOf cast) [ 0.0; 0.0 ] "both at zero: the paint is instantaneous"
            let header = headerOf cast
            Expect.equal (header.Width, header.Height) (120, 40) "the size the range actually ran at"

        testCase "an EMPTY keyframe screen paints nothing rather than an empty frame" <| fun () ->
            // What a degraded terminal's serializer returns. A zero-length output record is
            // a frame in the recording that the terminal never printed.
            let keyframe = { Seq = 2; Cols = 80; Rows = 24; Screen = "" }
            let cast = TranscriptReplay.range baseHeader (Some keyframe) 2 3 [ 2, { At = 1.0; Kind = TranscriptOutput; Data = "x" } ]
            Expect.equal (outputsOf cast) [ "x" ] "just the range"

        testCase "an empty range is still a VALID cast — a header and no frames" <| fun () ->
            // What a rejected command carries, and what a stretch with no recorded bounds
            // resolves to. An empty file is one the player reports as broken.
            let cast = TranscriptReplay.range baseHeader None 0 0 []
            Expect.equal ((cast.Split '\n' |> Array.filter (fun l -> l.Trim() <> "")).Length) 1 "the header, alone"

        testCaseAsync "the keyframe is what makes a ranged replay CORRECT, not merely faster" <|
            async {
                // The assertion the naive slice fails. The prefix sets colour and moves the
                // cursor; the range then prints under that state. Replayed into a fresh VT
                // the slice is *approximately* right — and wrong exactly where the screen
                // carried state in, which for an audit trail is the whole point.
                let prefix = [ "\u001b[31mred prefix\r\n"; "\u001b[44m"; "\u001b[10;5H" ]
                let ranged = [ "printed under that state\r\n" ]

                // The truth: one emulator fed the whole stream, exactly as the Session
                // 's own emulator was.
                let! truth = screenOf 80 24 (prefix @ ranged)
                // The keyframe: the same serializer, at the range's start.
                let! keyScreen = screenOf 80 24 prefix

                let records =
                    ranged |> List.mapi (fun i data -> 4 + i, { At = 60.0 + float i; Kind = TranscriptOutput; Data = data })
                let withKey =
                    TranscriptReplay.range baseHeader (Some { Seq = 4; Cols = 80; Rows = 24; Screen = keyScreen }) 4 9 records
                let without = TranscriptReplay.range baseHeader None 4 9 records

                let! replayed = screenOf 80 24 (outputsOf withKey)
                let! naive = screenOf 80 24 (outputsOf without)

                // Asserted non-empty first, because the interesting way for this to fail is
                // to pass: comparing one blank screen to another proves nothing.
                Expect.isTrue (truth.Contains "printed under that state") "the screen was actually drawn"
                Expect.equal replayed truth "the ranged replay reproduces the screen the emulator had"
                Expect.notEqual naive truth "and the naive slice does not — which is why keyframes exist"
            }

        testCase "the keyframe a range wants is the one at its FIRST line" <| fun () ->
            // Selection, stated as the property the writer relies on: keyframes are written
            // at range STARTS and nowhere else, so a range whose `from` has no keyframe has
            // no keyframe at all — there is nothing else to fall back to.
            let model =
                clientOf oneBlock
                |> Support.step (TerminalHeaderMsg (terminalA, baseHeader))
                |> Support.step (TerminalRecordsMsg (terminalA, [ 1, { At = 3.0; Kind = TranscriptOutput; Data = "out\r\n" } ]))
                |> Support.step (TerminalKeyframeMsg (terminalA, { Seq = 1; Cols = 80; Rows = 24; Screen = "SCREEN" }))
            match ClientModel.rangedCast terminalA 1 2 model with
            | Some cast -> Expect.equal (outputsOf cast) [ "SCREEN"; "out\r\n" ] "painted from the keyframe at line 1"
            | None -> failwith "the header is known, so there is a cast"
            // A different range on the same terminal has no keyframe of its own, and plays
            // without one rather than refusing.
            match ClientModel.rangedCast terminalA 0 2 model with
            | Some cast -> Expect.equal (outputsOf cast) [ "out\r\n" ] "no paint, just the range"
            | None -> failwith "a missing keyframe is not a missing cast"

        testCase "no header means no cast: a guessed geometry rewraps every line" <| fun () ->
            let model = clientOf oneBlock
            Expect.isNone (ClientModel.rangedCast terminalA 0 2 model) "nothing to render until line 0 arrives"
    ]

// --- The video item (stage 4) -----------------------------------------------------------------

/// A closed terminal with two blocks and the records they produced, which is what a
/// whole-terminal recording is made of.
let private recordedTerminal =
    [ at 1L 0.0 (opened terminalA "build")
      at 2L 1.0 (started terminalA "1" byAda "make" 1)
      at 3L 2.0 (completed terminalA "1" (CommandSucceeded 0) 3)
      at 4L 3.0 (started terminalA "2" byAda "make test" 3)
      at 5L 4.0 (completed terminalA "2" (CommandFailed 1) 5)
      at 6L 5.0 (SessionEvent.TerminalClosed { TerminalId = terminalA; Reason = "closed by a peer"; By = None }) ]

let private withRecords (model: ClientModel) =
    [ 1, { At = 10.0; Kind = TranscriptOutput; Data = "building\r\n" }
      2, { At = 11.0; Kind = TranscriptOutput; Data = "done\r\n" }
      3, { At = 40.0; Kind = TranscriptOutput; Data = "testing\r\n" }
      4, { At = 43.5; Kind = TranscriptOutput; Data = "FAILED\r\n" } ]
    |> List.fold (fun m (seq, record) -> Support.step (TerminalRecordsMsg (terminalA, [ seq, record ])) m) model
    |> Support.step (TerminalHeaderMsg (terminalA, baseHeader))

/// A page of transcript is one message and one render (`TerminalPageMsg`); what has to hold
/// is that it leaves the model exactly where the same lines arriving one at a time would.
let private pageTests =
    testList "A transcript page (one message, one render)" [
        testCase "a page folds to the same feed as its lines one at a time, header and read position included" <| fun () ->
            let fresh = ClientModel.init { PeerId = ada; DisplayName = "swift-heron" }
            let records =
                [ 1, { At = 10.0; Kind = TranscriptOutput; Data = "building\r\n" }
                  2, { At = 11.0; Kind = TranscriptOutput; Data = "done\r\n" }
                  4, { At = 43.5; Kind = TranscriptOutput; Data = "FAILED\r\n" } ]
            let oneAtATime =
                records
                |> List.fold (fun m (seq, record) -> Support.step (TerminalRecordsMsg (terminalA, [ seq, record ])) m) fresh
                |> Support.step (TerminalHeaderMsg (terminalA, baseHeader))
                |> Support.step (TerminalReadThroughMsg (terminalA, 5))
            let asPage = Support.step (TerminalPageMsg (terminalA, records, Some baseHeader, 5)) fresh
            Expect.equal asPage.TerminalFeeds oneAtATime.TerminalFeeds "the same feed, however it arrived"

        testCase "a page with no header leaves the header it had" <| fun () ->
            let fresh =
                ClientModel.init { PeerId = ada; DisplayName = "swift-heron" }
                |> Support.step (TerminalHeaderMsg (terminalA, baseHeader))
            let later = Support.step (TerminalPageMsg (terminalA, [ 7, { At = 1.0; Kind = TranscriptOutput; Data = "x" } ], None, 8)) fresh
            Expect.equal (Map.find terminalA later.TerminalFeeds).Header (Some baseHeader) "line 0 came earlier and stays"
    ]

/// What a block draws of a long output. The window itself is `TerminalFeed.lastLines`'s, and
/// tested there; this is the render saying so, which is the half a reader sees.
let private outputWindowTests =
    testList "A block's output, drawn" [
        testCase "a block that printed more than it draws says how many lines the recording holds" <| fun () ->
            let printed = TerminalFeed.shownLines + 1000
            let model =
                clientOf oneBlock
                |> Support.step (
                    TerminalRecordsMsg (
                        terminalA,
                        [ 1,
                          { At = 0.0
                            Kind = TranscriptOutput
                            Data = String.concat "" [ for n in 1 .. printed -> sprintf "line-%d\r\n" n ] } ]))
                |> Support.step (chip terminalA "1")
            Expect.isTrue
                ((Support.render model).Contains (Dom.attr Dom.Hooks.terminalOutputElided "1000"))
                "the thousand lines above the window are counted where the block is drawn"

        testCase "a block that printed within the window draws it all and counts nothing out" <| fun () ->
            let model =
                clientOf oneBlock
                |> Support.step (TerminalRecordsMsg (terminalA, [ 1, { At = 0.0; Kind = TranscriptOutput; Data = "total 0\n" } ]))
                |> Support.step (chip terminalA "1")
            Expect.isFalse
                ((Support.render model).Contains Dom.Hooks.terminalOutputElided)
                "nothing was left out, so nothing says it was"
    ]

let private videoTests =
    testList "The video item (Plan 14, stage 4)" [
        testCase "a whole recording is chaptered by the commands that ran in it" <| fun () ->
            // Chapters ride IN the cast as `"m"` events (Plan 25, stage 1), which is what puts
            // them on the same idle-compressed clock the player runs the records on. As the
            // player's own option they stayed on the raw clock and landed in the dead air the
            // compression had just removed.
            let model = withRecords (clientOf recordedTerminal)
            match ClientModel.terminalReplay terminalA model with
            | Some replay ->
                Expect.stringContains replay.Cast "[10,\"m\",\"make\"]" "a chapter at the first block's first line"
                Expect.stringContains replay.Cast "[40,\"m\",\"make test\"]" "and one at the second's"
                Expect.isNone replay.StartAt "and it starts at the start until somebody asks for a command"
            | None -> failwith "the header is known, so there is a recording"

        // Position and fidelity are two axes, and the toggle only ever moves ONE of them
        // (Plan 25, stage 3). These pin that, because it is the whole reason the reader
        // cannot lose their place any more.
        testCase "the toggle swaps the read and leaves the position alone" <| fun () ->
            Expect.equal (TerminalMode.toggled (Reading terminalA)) (Watching terminalA) "text to recording"
            Expect.equal (TerminalMode.toggled (Watching terminalA)) (Reading terminalA) "and back"

        testCase "a read positioned at a command watches from that command, and back" <| fun () ->
            // The round trip the old step-out could not make: it replaced the block tab, so
            // there was nothing to come back to. Here the position is the same fact on both
            // sides of the flip.
            let anchored = ReadingAt (terminalA, block "2")
            Expect.equal (TerminalMode.toggled anchored) (WatchingFrom (terminalA, block "2")) "watching from where they were"
            Expect.equal (TerminalMode.toggled (WatchingFrom (terminalA, block "2"))) anchored "and back to the same command"

        testCase "coming back to live drops the pin that only watching had" <| fun () ->
            // A pin is a fact about watching from behind an edge. Carried into a read it
            // would be a rewind nothing is showing.
            Expect.equal
                (TerminalMode.toggled (WatchingBehind (terminalA, 7)))
                (Reading terminalA)
                "the live text, with no pin left over"

        testCase "a watch entered from a command starts at that command" <| fun () ->
            // The anchor IS the start position: nothing rides a message, and the line is
            // resolved against the blocks the projection actually has.
            let model =
                withRecords (clientOf recordedTerminal)
                |> Support.step (ShowInPaneMsg (ReadingAt (terminalA, block "2")))
            Expect.equal (ClientModel.paneAnchor model) (Some (terminalA, block "2")) "positioned at the command"
            let watching = Support.step (ShowInPaneMsg (TerminalMode.toggled (ReadingAt (terminalA, block "2")))) model
            match ClientModel.terminalReplay terminalA watching with
            | Some replay -> Expect.equal replay.StartAt (Some 40.0) "and the recording starts where it did"
            | None -> failwith "the header is known, so there is a recording"

        testCase "a chapter is written before the record it names" <| fun () ->
            // The order the player's own multiplex picks, and the one a reader means: a
            // chapter names the command whose first byte follows it, never the silence before.
            let model = withRecords (clientOf recordedTerminal)
            match ClientModel.terminalReplay terminalA model with
            | Some replay ->
                let marker = replay.Cast.IndexOf "[10,\"m\",\"make\"]"
                let record = replay.Cast.IndexOf "building"
                Expect.isTrue (marker >= 0 && marker < record) "the chapter line comes first"
            | None -> failwith "the header is known, so there is a recording"

        testCase "'play whole terminal' lands on the block it stepped out from" <| fun () ->
            // The two paths answer different questions: the slice is "what did this command
            // print", the whole is "what was going on around it".
            let model =
                withRecords (clientOf recordedTerminal)
                |> Support.step (ShowInPaneMsg (WatchingFrom (terminalA, block "2")))
            Expect.equal model.Pane (Some (OnTerminal (WatchingFrom (terminalA, block "2")))) "the pane moved to the terminal's own recording"
            match ClientModel.terminalReplay terminalA model with
            | Some replay -> Expect.equal replay.StartAt (Some 40.0) "starting where the second block did"
            | None -> failwith "the header is known, so there is a recording"

        testCase "a start hint belongs to the step-out that set it, and dies with it" <| fun () ->
            let model =
                withRecords (clientOf recordedTerminal)
                |> Support.step (ShowInPaneMsg (WatchingFrom (terminalA, block "2")))
                |> Support.step (ShowInPaneMsg (Reading terminalA))
            match ClientModel.terminalReplay terminalA model with
            | Some replay -> Expect.isNone replay.StartAt "choosing the tab again starts it from the start"
            | None -> failwith "the header is known, so there is a recording"

        testCase "a stretch's item carries a poster: the still of its final screen" <| fun () ->
            // It costs nothing extra — the player builds the still by replaying to that point
            // internally — and the time is in the RANGE's own clock, because the cast it is
            // shown over has been rebased.
            let stretch =
                { Offset = EventOffset.create 9L |> expect
                  TerminalId = terminalA
                  Title = "build"
                  Holder = PeerRef bob
                  End = LeaseReleased
                  Range = Some (1, 4)
                  StartedAt = epoch
                  EndedAt = epoch.AddMinutes 1.0 }
            let model = withRecords (clientOf recordedTerminal)
            match ClientModel.previewReplay (PreviewSubject.Stretch stretch) model with
            | Some replay ->
                // Nudged past the record rather than landing on it: the player feeds events
                // while `time < poster`, so asking for exactly 30.0 would show the screen as
                // it stood BEFORE the frame this still exists to show.
                Expect.equal replay.Poster (Some 30.001) "the last frame of the range, from its own zero"
                Expect.isTrue (replay.Cast.Contains "testing") "and the recording is the range"
            | None -> failwith "the header is known, so there is a recording"

        testCase "a stretch with no recorded range has no player at all" <| fun () ->
            // An empty player is indistinguishable from a quiet session, and the item says
            // which one it is instead.
            let stretch =
                { Offset = EventOffset.create 9L |> expect
                  TerminalId = terminalA
                  Title = "build"
                  Holder = PeerRef bob
                  End = LeaseHolderGone
                  Range = None
                  StartedAt = epoch
                  EndedAt = epoch.AddMinutes 1.0 }
            Expect.isNone
                (ClientModel.previewReplay (PreviewSubject.Stretch stretch) (withRecords (clientOf recordedTerminal)))
                "nothing to play"

        testCase "a RUNNING block has no range yet, so nothing is mounted over it" <| fun () ->
            // Its recording grows on every record, and a player rebuilt on each one would
            // thrash through a streaming build. The terminal's own tab is where you watch it.
            let running =
                [ at 1L 0.0 (opened terminalA "build")
                  at 2L 1.0 (started terminalA "1" byAda "make" 1) ]
            let model = withRecords (clientOf running)
            Expect.isNone (ClientModel.previewReplay (PreviewSubject.Block (terminalA, block "1")) model) "not yet"
            let finished = withRecords (clientOf (running @ [ at 3L 9.0 (completed terminalA "1" (CommandSucceeded 0) 3) ]))
            Expect.isSome (ClientModel.previewReplay (PreviewSubject.Block (terminalA, block "1")) finished) "and now"

        testCase "a refused command is reported, never played" <| fun () ->
            let rejected =
                [ at 1L 0.0 (opened terminalA "build")
                  at 2L 1.0 (
                      SessionEvent.TerminalCommandRejected
                          { TerminalId = terminalA
                            QueueId = QueueId.create "q-1" |> expect
                            BlockId = block "no"
                            Authority = agentForAda
                            RejectedBy = PeerRef ada
                            Command = "rm -rf /"
                            Reason = Some "no" }) ]
            let model = withRecords (clientOf rejected)
            Expect.isNone (ClientModel.previewReplay (PreviewSubject.Block (terminalA, block "no")) model) "it never ran"
            Expect.isNone (ClientModel.missingKeyframe (Support.step (chip terminalA "no") model)) "so there is no screen to fetch"
            let html = Support.render (Support.step (OpenPreviewMsg (Preview.ofSubject (PreviewSubject.Block (terminalA, block "no")))) model)
            // By NAME — `ada` is this fixture's local peer, so the refuser is called what
            // every other surface calls them rather than by the id underneath.
            //
            // The phrase is carried deliberately: `swift-heron` alone also appears in the
            // roster, so an assertion decoupled from the copy passes even when this pane
            // prints the bare id. Coupling to the refusal's own word (`Dom.Text.blockRefused`)
            // is what SCOPES it to the refusal — and the refuser is a REFERENCE after it, drawn
            // as they are drawn everywhere.
            let at = html.IndexOf (Dom.Text.blockRefused + " ")
            Expect.isTrue (at >= 0) "the tab says who refused it"
            let refuser = html.Substring (at, html.IndexOf ("</span></span>", at) + "</span></span>".Length - at)
            Expect.isTrue (refuser.Contains (Dom.attr "data-entity-kind" "actor")) "as a reference to the person"
            Expect.isTrue (refuser.Contains ">swift-heron<") "by name"
            Expect.isFalse (refuser.Contains ">ada<") "never the id"
            Expect.isFalse (html.Contains (Dom.attr Dom.Hooks.paneReplay "block:term-a:b-no")) "and mounts no player"

        testCase "a block offers the way to its command in the terminal's own history" <| fun () ->
            // The reader's other question — what was going on around this — is about POSITION,
            // and its answer is more of the same text. Offered wherever there is a history to
            // be positioned in, open or closed: the question is as real on a running terminal.
            let closed =
                withRecords (clientOf recordedTerminal)
                |> Support.step (OpenPreviewMsg (Preview.ofSubject (PreviewSubject.Block (terminalA, block "1"))))
            Expect.isTrue
                ((Support.render closed).Contains (Dom.attr Dom.Hooks.paneShowInTerminal "b-1"))
                "a closed terminal's block can be shown where it ran"
            let stillOpen =
                withRecords (clientOf (recordedTerminal |> List.filter (fun e -> match e.Event with SessionEvent.TerminalClosed _ -> false | _ -> true)))
                |> Support.step (OpenPreviewMsg (Preview.ofSubject (PreviewSubject.Block (terminalA, block "1"))))
            Expect.isTrue
                ((Support.render stillOpen).Contains (Dom.attr Dom.Hooks.paneShowInTerminal "b-1"))
                "and so can a running one's"

        testCase "the keyframe a preview needs is the one at its range's first line" <| fun () ->
            let model = withRecords (clientOf recordedTerminal) |> Support.step (chip terminalA "2")
            Expect.equal (ClientModel.missingKeyframe model) (Some (terminalA, 3)) "the block's first line"

        testCase "a keyframe already held is not asked for again" <| fun () ->
            let fetched =
                withRecords (clientOf recordedTerminal)
                |> Support.step (chip terminalA "2")
                |> Support.step (TerminalKeyframeMsg (terminalA, { Seq = 3; Cols = 80; Rows = 24; Screen = "S" }))
            Expect.isNone (ClientModel.missingKeyframe fetched) "and not again"

        testCase "a whole recording needs no keyframe" <| fun () ->
            // It starts at the start; its header is its keyframe.
            let model = withRecords (clientOf recordedTerminal) |> Support.step (ShowInPaneMsg (Watching terminalA))
            Expect.isNone (ClientModel.missingKeyframe model) "nothing to fetch"
    ]

// --- The DVR (stage 7) -------------------------------------------------------------------------

// --- The two reads of one history -------------------------------------------------------------

/// A terminal that was only ever typed in: somebody took the lease, bytes were recorded, and
/// it closed without a command ever resolving into a block. What a device attached over a
/// stream that cannot be instrumented also looks like from here.
let private liveOnlyTerminal =
    [ at 1L 0.0 (opened terminalA "shell")
      at 2L 1.0 (took terminalA (PeerRef bob) 1)
      at 3L 5.0 (SessionEvent.TerminalClosed { TerminalId = terminalA; Reason = "closed by a peer"; By = None }) ]

let private readsTests =
    testList "Which read a surface shows" [

        // Text and recording are two reads of the same bytes, and the rule is the same on
        // every surface that has both: the text is the read, the recording is somewhere you
        // go. Each of these is a BICONDITIONAL — the swap happening when it is asked for, and
        // not happening when it is not — because a predicate that answered `true` always
        // would satisfy either half alone.

        testCase "a closed terminal that ran commands reads as its commands" <| fun () ->
            // What the player under the blocks was: a recording of the same two lines the
            // block above it had already printed.
            let model = withRecords (clientOf recordedTerminal)
            Expect.isFalse (ClientModel.terminalPlays terminalA model) "the blocks are the read"

        testCase "asking for the recording swaps the read" <| fun () ->
            let model =
                withRecords (clientOf recordedTerminal)
                |> Support.step (ShowInPaneMsg (Watching terminalA))
            Expect.isTrue (ClientModel.terminalPlays terminalA model) "now it plays"
            Expect.isFalse
                (ClientModel.terminalPlays terminalB model)
                "and only the terminal that was asked for"

        testCase "a closed terminal with nothing but a recording plays without being asked" <| fun () ->
            // There is no cheaper read to default to: an empty block list is not a read, it
            // is a `$`. Making a reader press play to see the only thing there is would be a
            // control whose answer is never no.
            let model = withRecords (clientOf liveOnlyTerminal)
            Expect.isTrue (ClientModel.terminalPlays terminalA model) "the recording IS the surface"

        testCase "the way back is offered only where there is something behind the player" <| fun () ->
            let played =
                withRecords (clientOf recordedTerminal)
                |> Support.step (ShowInPaneMsg (Watching terminalA))
            Expect.isTrue
                ((Support.render played).Contains (Dom.attr Dom.Hooks.terminalWatch "output"))
                "text to go back to"
            Expect.isFalse
                ((Support.render (withRecords (clientOf liveOnlyTerminal))).Contains (Dom.attr Dom.Hooks.terminalWatch "output"))
                "and none where the recording is the only read — that control undoes itself"

        testCase "a rewind that outlives its live edge is still a reader watching a recording" <| fun () ->
            // The pin dies with the live edge; the watching does not. Dropping a reader back
            // into the blocks because the terminal they were watching finished would answer
            // a question they never asked.
            let live = recordedTerminal |> List.filter (fun e -> match e.Event with SessionEvent.TerminalClosed _ -> false | _ -> true)
            let model =
                withRecords (clientOf live)
                |> Support.step (RewindTerminalMsg terminalA)
                |> Support.step
                    (EventsPageMsg
                        { Events = [ at 6L 61.0 (SessionEvent.TerminalClosed { TerminalId = terminalA; Reason = "done"; By = None }) ]
                          LastOffset = Some (EventOffset.create 6L |> expect)
                          IsEnd = true })
            Expect.isFalse (ClientModel.isRewound terminalA model) "no live edge, no rewind"
            Expect.isTrue (ClientModel.terminalPlays terminalA model) "and still the recording"

        testCase "a recording the cap ate is never offered" <| fun () ->
            // The stated gap. A control that opens an empty player is indistinguishable from
            // a terminal that printed nothing, which is the fact the drop is recorded to say.
            let model = clientOf recordedTerminal
            Expect.isFalse (ClientModel.terminalPlayable terminalA model) "nothing kept, nothing to play"
            Expect.isFalse ((Support.render model).Contains Dom.Hooks.terminalWatch) "so nothing offers it"

        testCase "a block's output is text until somebody asks for the recording" <| fun () ->
            // The case that made the rule: a command and its result, printed, needed no
            // player of the same two lines under it.
            let model =
                withRecords (clientOf recordedTerminal)
                |> Support.step (OpenPreviewMsg (Preview.ofSubject (PreviewSubject.Block (terminalA, block "1"))))
            Expect.isFalse
                ((Support.render model).Contains (Dom.attr Dom.Hooks.paneReplay "block:term-a:b-1"))
                "read as text"
            let played = Support.step (ShowPreviewMsg { Preview.Subject = PreviewSubject.Block (terminalA, block "1"); Preview.Plays = true }) model
            Expect.isTrue
                ((Support.render played).Contains (Dom.attr Dom.Hooks.paneReplay "block:term-a:b-1"))
                "and played when asked"
    ]

let private dvrTests =
    testList "Rewinding a live terminal (Plan 14, stage 7)" [
        testCase "rewinding plays what has been recorded SO FAR, and pins that length" <| fun () ->
            // A recording that grew under a reader would move the scrub bar out from under
            // them, which is the one thing rewinding exists to avoid. The terminal keeps
            // running and its records keep arriving — that is what makes this a DVR rather
            // than a replay of something finished.
            let live =
                [ at 1L 0.0 (opened terminalA "shell")
                  at 2L 1.0 (took terminalA (PeerRef bob) 1) ]
            let model = withRecords (clientOf live) |> Support.step (RewindTerminalMsg terminalA)
            Expect.isTrue (ClientModel.isRewound terminalA model) "the pane is behind live"
            let castAt (m: ClientModel) =
                match ClientModel.terminalReplay terminalA m with
                | Some replay -> outputsOf replay.Cast
                | None -> failwith "the header is known, so there is a recording"
            Expect.equal
                (castAt model)
                [ "building\r\n"; "done\r\n"; "testing\r\n"; "FAILED\r\n" ]
                "everything recorded when the rewind began"
            // The terminal keeps printing. What is being watched does not move.
            let stillGrowing =
                Support.step
                    (TerminalRecordsMsg (terminalA, [ 5, { At = 60.0; Kind = TranscriptOutput; Data = "after\r\n" } ]))
                    model
            Expect.equal (castAt stillGrowing) (castAt model) "the recording under the reader is unchanged"

        testCase "rewinding lands AT the pinned edge, not at the recording's start" <| fun () ->
            // "Rewind" on an hour-old terminal must not mean "restart from the beginning".
            // Like live TV it lands on the moment the reader left — the still of the pinned
            // screen, visually the live screen they were just watching — and the scrub bar
            // is how they go back from there.
            let before = withRecords (clientOf [ at 1L 0.0 (opened terminalA "shell"); at 2L 1.0 (took terminalA (PeerRef bob) 1) ])
            (match ClientModel.terminalReplay terminalA before with
             | Some replay -> Expect.isNone replay.BehindLive "an un-rewound cast's end really is the end"
             | None -> failwith "the header is known, so there is a recording")
            match ClientModel.terminalReplay terminalA (Support.step (RewindTerminalMsg terminalA) before) with
            | Some replay ->
                Expect.equal replay.StartAt (Some 43.5) "starts at the last pinned record's time"
                // Nudged past that record: the still is the screen the reader was just
                // watching, and a poster landing ON the pinned time paints the one before it.
                Expect.equal replay.Poster (Some 43.501) "whose frame is the still shown before play"
                Expect.equal replay.BehindLive (Some terminalA) "and playing off this end means the reader caught up"
            | None -> failwith "the header is known, so there is a recording"

        testCase "the surface says how far behind the reader is, and it grows" <| fun () ->
            let model =
                withRecords (clientOf [ at 1L 0.0 (opened terminalA "shell"); at 2L 1.0 (took terminalA (PeerRef bob) 1) ])
                |> Support.step (RewindTerminalMsg terminalA)
            Expect.equal (ClientModel.behindLive terminalA model) (Some 0.0) "nothing has accrued yet"
            let grown =
                Support.step
                    (TerminalRecordsMsg (terminalA, [ 5, { At = 103.5; Kind = TranscriptOutput; Data = "after\r\n" } ]))
                    model
            Expect.equal (ClientModel.behindLive terminalA grown) (Some 60.0) "a minute of recording arrived behind the pin"
            Expect.isTrue ((Support.render grown).Contains (Dom.Text.behindLive (Some "1m 0s"))) "and the pane says so"

        testCase "a live terminal with NOTHING recorded offers no rewind" <| fun () ->
            // A DVR with nothing behind it is a control with nothing to do.
            let bare = clientOf [ at 1L 0.0 (opened terminalA "shell") ]
            Expect.isFalse ((Support.render bare).Contains Dom.Hooks.terminalWatch) "no recording, no control"

        testCase "a terminal that CLOSES under a rewound reader is simply its recording again" <| fun () ->
            // The pin outlived its live edge, so it is no rewind any more. Left unresolved
            // this rendered TWO players over one recording — the rewound region and the
            // closed-terminal replay — with the final output missing from both and no
            // "jump to live" to escape by.
            let model =
                withRecords (clientOf [ at 1L 0.0 (opened terminalA "shell"); at 2L 1.0 (took terminalA (PeerRef bob) 1) ])
                |> Support.step (RewindTerminalMsg terminalA)
                |> Support.step
                    (TerminalRecordsMsg (terminalA, [ 5, { At = 60.0; Kind = TranscriptOutput; Data = "after\r\n" } ]))
                |> Support.step
                    (EventsPageMsg
                        { Events = [ at 3L 61.0 (SessionEvent.TerminalClosed { TerminalId = terminalA; Reason = "done"; By = None }) ]
                          LastOffset = Some (EventOffset.create 3L |> expect)
                          IsEnd = true })
            Expect.isFalse (ClientModel.isRewound terminalA model) "no live edge, no rewind"
            (match ClientModel.terminalReplay terminalA model with
             | Some replay ->
                 Expect.isTrue ((outputsOf replay.Cast) |> List.contains "after\r\n") "the recording is whole again, pin ignored"
                 Expect.isNone replay.BehindLive "and its end really is the end"
             | None -> failwith "the header is known, so there is a recording")
            let html = Support.render model
            let mountAttr = Dom.attr Dom.Hooks.paneReplay "terminal:term-a"
            Expect.equal
                ((html.Length - html.Replace(mountAttr, "").Length) / mountAttr.Length)
                1
                "ONE player over the recording, not two"
            Expect.isFalse (html.Contains (Dom.attr Dom.Hooks.terminalWatch "live")) "and no way-back-to-live for a terminal with no live"

        testCase "jumping to live drops the rewind, and the newest bytes are back" <| fun () ->
            let live = [ at 1L 0.0 (opened terminalA "shell"); at 2L 1.0 (took terminalA (PeerRef bob) 1) ]
            let model =
                withRecords (clientOf live)
                |> Support.step (RewindTerminalMsg terminalA)
                |> Support.step
                    (TerminalRecordsMsg (terminalA, [ 5, { At = 60.0; Kind = TranscriptOutput; Data = "after\r\n" } ]))
                |> Support.step (ShowInPaneMsg (Reading terminalA))
            Expect.isFalse (ClientModel.isRewound terminalA model) "caught back up"
            match ClientModel.terminalReplay terminalA model with
            | Some replay -> Expect.isTrue ((outputsOf replay.Cast) |> List.contains "after\r\n") "including what arrived while behind"
            | None -> failwith "the header is known, so there is a recording"

        testCase "choosing anything else in the pane ends the rewind" <| fun () ->
            // A pane that was still behind live because of a rewind somebody started ten
            // minutes ago would be a surprise with no cause on screen.
            let live = [ at 1L 0.0 (opened terminalA "shell"); at 2L 1.0 (took terminalA (PeerRef bob) 1) ]
            let model =
                withRecords (clientOf live)
                |> Support.step (RewindTerminalMsg terminalA)
                |> Support.step (ShowInPaneMsg (Reading terminalA))
            Expect.isFalse (ClientModel.isRewound terminalA model) "the rewind went with the choice"

        testCase "rewind is offered on ANY live terminal, and the screen gives way to it" <| fun () ->
            // The mechanism does not care which MODE the terminal is in: a running build and
            // a `vim` session are one growing byte stream, and a rule that offered this for
            // one and not the other would be a special case to explain rather than a feature.
            let inBlockMode = withRecords (clientOf [ at 1L 0.0 (opened terminalA "shell") ])
            Expect.isTrue
                ((Support.render inBlockMode).Contains (Dom.attr Dom.Hooks.terminalWatch "watch"))
                "a terminal in block mode is rewindable"
            let inLiveMode =
                withRecords (clientOf [ at 1L 0.0 (opened terminalA "shell"); at 2L 1.0 (took terminalA (PeerRef ada) 1) ])
            Expect.isTrue
                ((Support.render inLiveMode).Contains (Dom.attr Dom.Hooks.terminalWatch "watch"))
                "and so is one in live mode"
            let rewound = Support.step (RewindTerminalMsg terminalA) inLiveMode
            let html = Support.render rewound
            Expect.isTrue (html.Contains (Dom.attr Dom.Hooks.terminalWatch "live")) "the way back to the edge"
            Expect.isTrue
                (html.Contains (Dom.attr Dom.Hooks.paneReplay "terminal:term-a"))
                "the recording mounts through the same player a finished terminal uses"
            Expect.isFalse
                (html.Contains (Dom.attr Dom.Hooks.terminalScreen "term-a"))
                "and the live screen gives way to it while you are behind"

        testCase "a CLOSED terminal is not rewindable — it is simply a recording" <| fun () ->
            let closed =
                withRecords
                    (clientOf
                        [ at 1L 0.0 (opened terminalA "shell")
                          at 2L 1.0 (SessionEvent.TerminalClosed { TerminalId = terminalA; Reason = "done"; By = None }) ])
            Expect.isFalse
                ((Support.render closed).Contains (Dom.attr Dom.Hooks.terminalWatch "live"))
                "there is no live edge to be behind"
    ]

// --- Tool use (Plan 16, part C) ---------------------------------------------------------

let private toolUse (n: string) = ToolUseId.create ("t-" + n) |> expect
let private turn (n: string) = AgentTurnId.create ("turn-" + n) |> expect

let private used (n: string) (t: string) (name: string) =
    SessionEvent.ToolUseStarted
        { ToolUseId = toolUse n
          AgentTurnId = turn t
          Namespace = "yession"
          Name = name
          Arguments = Some "{}" }

let private toolDone (n: string) (outcome: ToolOutcome) (blk: BlockId option) =
    SessionEvent.ToolUseFinished { ToolUseId = toolUse n; Outcome = outcome; Block = blk; Result = None }

let private toolDoneWith (n: string) (outcome: ToolOutcome) (blk: BlockId option) (result: string option) =
    SessionEvent.ToolUseFinished { ToolUseId = toolUse n; Outcome = outcome; Block = blk; Result = result }

let private drawn (events: EventEnvelope<SessionEvent> list) : string list =
    let conversation, _ = ConversationProjection.applyEvents None events ConversationProjection.empty
    let timeline, _ = TimelineProjection.applyEvents None events TimelineProjection.empty
    TimelineProjection.rows conversation timeline
    |> List.map (function
        | RowItem item -> List.head (shapes [ item ])
        | RowWorkRun (t, items) -> sprintf "run:%s:%d" (AgentTurnId.value t) (List.length items)
        | RowTaskCard (t, items) -> sprintf "card:%s:%d" (AgentTurnId.value t) (List.length items))

let private toolTests =
    testList "Tool use in the chat" [
        testCase "a call anchors where it STARTED, like a block and unlike a stretch" <| fun () ->
            // Same reason: a four-minute call must be visible while it is the only thing
            // happening, rather than appearing from nowhere when it finishes.
            let items =
                merge
                    [ at 1L 0.0 (used "1" "a" "repo_status")
                      at 2L 1.0 (sent "1" "any luck?")
                      at 3L 2.0 (toolDone "1" ToolCallOk None) ]
            Expect.equal (shapes items) [ "used:t-1"; "said:m-1" ] "the item holds the place it started at"

        testCase "the outcome moves what it SAYS, not where it sits" <| fun () ->
            // Carried by id, resolved against the projection — the same property that makes a
            // block chip mutate in place for free.
            let events =
                [ at 1L 0.0 (used "1" "a" "repo_status")
                  at 2L 1.0 (toolDone "1" (ToolCallFailed "no such tool") None) ]
            let running, _ = TimelineProjection.applyEvents None [ List.head events ] TimelineProjection.empty
            let finished, _ = TimelineProjection.applyEvents None events TimelineProjection.empty
            Expect.equal running.TerminalItems finished.TerminalItems "the entry does not move"
            Expect.equal
                (TimelineProjection.toolUse (toolUse "1") running |> Option.bind (fun u -> u.Outcome))
                None
                "a running call says it is running"
            Expect.equal
                (TimelineProjection.toolUse (toolUse "1") finished |> Option.bind (fun u -> u.Outcome))
                (Some (ToolCallFailed "no such tool"))
                "and the finish is what changes it"

        testCase "a non-block call carries its answer for the chip to disclose" <| fun () ->
            // The finish moves what the chip SAYS (above); this is the other thing it now
            // moves — what the chip can OPEN. A block call carries none (its own chip shows
            // the output); a plain call carries the capped answer.
            let events =
                [ at 1L 0.0 (used "1" "a" "repo_status")
                  at 2L 1.0 (toolDoneWith "1" ToolCallOk None (Some "On branch master")) ]
            let finished, _ = TimelineProjection.applyEvents None events TimelineProjection.empty
            Expect.equal
                (TimelineProjection.toolUse (toolUse "1") finished |> Option.bind (fun u -> u.Result))
                (Some "On branch master")
                "the answer travelled to the chip"

        testCase "a call that became a block draws no second chip" <| fun () ->
            // The block chip already says who ran what and how it went. Two renderings of one
            // fact are free to disagree; the RECORD still exists, it just does not draw twice.
            let events =
                [ at 1L 0.0 (opened terminalA "agent")
                  at 2L 1.0 (used "1" "a" "execute_command")
                  at 3L 2.0 (started terminalA "1" agentForAda "make" 1)
                  at 4L 3.0 (toolDone "1" ToolCallOk (Some (block "1"))) ]
            Expect.equal (drawn events) [ "ran:b-1" ] "only the block's chip is drawn"
            let timeline, _ = TimelineProjection.applyEvents None events TimelineProjection.empty
            Expect.isTrue
                (Map.containsKey (ToolUseId.value (toolUse "1")) timeline.ToolUses)
                "the audit record is still there — the audit wants every call"

        // What a call was given is under its line, as its own element in the same shape its
        // answer takes — not on the line beside the name, truncated. A two-hundred-character
        // `old_string` on the line was a row that said nothing; under it, opened on tap, it is
        // the edit a reader can check. What is pinned is the split: the input is its own
        // element in the call's fold, and the line does not carry it.
        testCase "a call's input is in its fold, as its own element, not on its line" <| fun () ->
            let args = """{"path":"src/A.fs","old_string":"let x = 1"}"""
            let events =
                [ at 1L 0.0 (SessionEvent.ToolUseStarted { ToolUseId = toolUse "1"; AgentTurnId = turn "a"; Namespace = "yession"; Name = "edit_file"; Arguments = Some args })
                  at 2L 1.0 (toolDoneWith "1" ToolCallOk None (Some "edited src/A.fs: −1 +1 lines")) ]
            let model = clientOf events
            let html = Support.render model
            let callAt = html.IndexOf (Dom.attr Dom.Hooks.chatTool "t-1")
            Expect.isTrue (callAt >= 0) "the call is drawn"
            let bodyAt = html.IndexOf (Dom.attr "data-fold-body" "call-t-1", callAt)
            Expect.isTrue (bodyAt > callAt) "the call has a fold of its own"
            let line = html.Substring (callAt, bodyAt - callAt)
            Expect.isTrue (line.Contains "edit_file") "the line names the tool"
            Expect.isFalse (line.Contains "old_string") "and carries its outcome, not its arguments"
            // Behind the fold, which is the half this case is about.
            let behind = Support.behindFold (FoldKey.ToolCall (toolUse "1")) model
            let inputAt = behind.IndexOf "data-chat-tool-input=\"t-1\""
            Expect.isTrue (inputAt >= 0) "the input is its own element, in the fold"
            let input = behind.Substring (inputAt, behind.IndexOf ("</pre>", inputAt) - inputAt)
            Expect.isTrue (input.Contains "old_string") "carrying what the call was given"
            Expect.isTrue (input.Contains "\n") "laid out a field per line, not as one row of JSON"
            let outputAt = behind.IndexOf ("data-chat-tool-result=\"t-1\"", inputAt)
            Expect.isTrue (outputAt > inputAt) "and the answer follows it, in the same place"

        // A run is a fold like an act's — the same control, the same state — and each run is
        // its OWN: a turn that spoke between two runs of calls has two rows, and opening one
        // must not open the other. Keyed by the run's first call for exactly that reason.
        testCase "two runs of one turn fold independently" <| fun () ->
            let events =
                [ at 1L 0.0 (used "1" "a" "read_file")
                  at 2L 1.0 (used "2" "a" "read_file")
                  at 3L 2.0 (sent "1" "reading…")
                  at 4L 3.0 (used "3" "a" "edit_file")
                  at 5L 4.0 (used "4" "a" "read_file") ]
            let shut = clientOf events
            let html = Support.render shut
            Expect.isTrue (html.Contains (Dom.attr "data-fold-body" "run-t-1")) "the first run has a fold"
            Expect.isTrue (html.Contains (Dom.attr "data-fold-body" "run-t-3")) "and the second its own"
            Expect.isFalse (html.Contains (Dom.attr "data-fold-open" "yes")) "both folded to their line"
            let opened = Support.render (Support.step (ToggleFoldMsg (FoldKey.ToolRun (toolUse "3"))) shut)
            let at (key: string) = opened.IndexOf (Dom.attr "data-fold-body" key)
            let openState (key: string) =
                let from = at key
                opened.Substring (from, opened.IndexOf (">", from) - from)
            Expect.isTrue ((openState "run-t-3").Contains "data-fold-open=\"yes\"") "the one pressed unfolds"
            Expect.isTrue ((openState "run-t-1").Contains "data-fold-open=\"no\"") "the other stays folded"

        // The aggregate is for aggregates. A run of ONE call is that call on the rail — its
        // own fold opening straight onto input and output — not a fold over a fold that took
        // two presses to reach one thing.
        testCase "a run of one call is the call, with no aggregate around it" <| fun () ->
            let events = [ at 1L 0.0 (used "1" "a" "read_file") ]
            let html = Support.render (clientOf events)
            Expect.isTrue (html.Contains (Dom.attr "data-fold-body" "call-t-1")) "the call has its fold"
            Expect.isFalse (html.Contains "data-fold-body=\"run-") "and nothing folds around it"

        testCase "arguments are laid out for reading, and the record is untouched" <| fun () ->
            let use' : ToolUse =
                { ToolUseId = toolUse "1"; AgentTurnId = turn "a"; Namespace = "yession"; Name = "read_file"
                  Arguments = Some """{"path":"a.fs","offset":2}"""; Outcome = None; Block = None; Result = None }
            Expect.equal (ToolUseText.arguments use') (Some "{\n  \"path\": \"a.fs\",\n  \"offset\": 2\n}") "one field per line"
            Expect.equal (ToolUseText.arguments { use' with Arguments = Some "not json" }) (Some "not json") "what is not JSON is shown as it is"
            Expect.equal (ToolUseText.arguments { use' with Arguments = None }) None "a foreign tool's arguments were never recorded"

        testCase "consecutive calls from one turn collapse into a single row" <| fun () ->
            // Tool use is the first item a SINGLE turn can emit a dozen of, so a chatty turn
            // costs one line rather than twenty.
            let events =
                [ at 1L 0.0 (used "1" "a" "repo_status")
                  at 2L 1.0 (used "2" "a" "repo_log")
                  at 3L 2.0 (used "3" "a" "repo_diff") ]
            Expect.equal (drawn events) [ "run:turn-a:3" ] "three calls, one row"

        testCase "…but only CONSECUTIVE ones, and only within one turn" <| fun () ->
            // A message between two calls means the turn said something in the middle, and
            // hiding that inside one line would tell a reader the wrong story about the order.
            let events =
                [ at 1L 0.0 (used "1" "a" "repo_status")
                  at 2L 1.0 (sent "1" "hold on")
                  at 3L 2.0 (used "2" "a" "repo_log")
                  at 4L 3.0 (used "3" "b" "repo_diff") ]
            // Each lone call is its own row rather than a run of one: a run forms on the
            // second item, like a task card, because a fold around one thing is a fold over
            // a fold.
            Expect.equal
                (drawn events)
                [ "used:t-1"; "said:m-1"; "used:t-2"; "used:t-3" ]
                "the message splits the run, and a new turn starts another"

        // A run is a turn's WORK, not only its calls: the act a call made — the write behind
        // `write_file`, the sandbox behind `start_work_sandbox` — lands in the run beside the
        // call, in order, and the line counts what the run holds by kind. An act joins a
        // run; it never starts one, because alone it cannot say whose it is. A person's act
        // ends the run, as a message does.
        testCase "the agent's acts between its calls join the run, in order, and are counted by kind" <| fun () ->
            let wrote (n: string) (path: string) =
                SessionEvent.FileChanged
                    { FileChanged.MessageId = MessageId.create ("f-" + n) |> expect
                      FileChanged.Sandbox = SandboxRef.defaultRef
                      FileChanged.Path = path
                      FileChanged.Change = FileChange.Written 3
                      FileChanged.Diff = None
                      FileChanged.Actor = ActorRef.Agent }
            let edited (n: string) (path: string) =
                SessionEvent.FileChanged
                    { FileChanged.MessageId = MessageId.create ("f-" + n) |> expect
                      FileChanged.Sandbox = SandboxRef.defaultRef
                      FileChanged.Path = path
                      FileChanged.Change = FileChange.Edited (1, 1, 1)
                      FileChanged.Diff = Some "-a\n+b"
                      FileChanged.Actor = ActorRef.Agent }
            let events =
                [ at 1L 0.0 (used "1" "a" "write_file")
                  at 2L 1.0 (wrote "1" "a.txt")
                  at 3L 2.0 (used "2" "a" "read_file")
                  at 4L 3.0 (used "3" "a" "edit_file")
                  at 5L 4.0 (edited "2" "a.txt") ]
            Expect.equal (drawn events) [ "run:turn-a:5" ] "one row, the call and the act it made both in it"
            let conversation, _ = ConversationProjection.applyEvents None events ConversationProjection.empty
            let timeline, _ = TimelineProjection.applyEvents None events TimelineProjection.empty
            match TimelineProjection.rows conversation timeline with
            | [ RowWorkRun (_, items) ] ->
                Expect.equal (WorkRun.summary items) "used 3 tools, wrote 1 file, edited 1 file" "counted by kind, each where it first appeared"
                Expect.equal (WorkRun.summary (List.truncate 2 items)) "used 1 tool, wrote 1 file" "and singular when it is one"
            | rows -> failwithf "expected one run, got %A" rows

        testCase "an act alone does not start a run, and a person's act ends one" <| fun () ->
            let agentWrote =
                SessionEvent.FileChanged
                    { FileChanged.MessageId = MessageId.create "f-1" |> expect
                      FileChanged.Sandbox = SandboxRef.defaultRef
                      FileChanged.Path = "a.txt"
                      FileChanged.Change = FileChange.Written 1
                      FileChanged.Diff = None
                      FileChanged.Actor = ActorRef.Agent }
            let personSet =
                SessionEvent.ShellProfileSet
                    { ShellProfileSet.MessageId = MessageId.create "p-1" |> expect
                      ShellProfileSet.Sandbox = SandboxRef.defaultRef
                      ShellProfileSet.WorkingDirectory = Some "/repos/x"
                      ShellProfileSet.Actor = PeerRef ada; OnBehalfOf = None }
            let events =
                [ at 1L 0.0 agentWrote
                  at 2L 1.0 (used "1" "a" "read_file")
                  at 3L 2.0 (used "2" "a" "read_file")
                  at 4L 3.0 personSet
                  at 5L 4.0 (used "3" "a" "read_file") ]
            Expect.equal
                (drawn events)
                [ "said:f-1"; "run:turn-a:2"; "said:p-1"; "used:t-3" ]
                "the lone act stands; the person's act closes the run; the call after it stands alone"

        testCase "an id minted for a call is a handle a link can carry" <| fun () ->
            // Why it is MINTED rather than derived: a fact that will be addressed must not be
            // identified by a rule that lives nowhere in the data (Plan 13, stage 2a).
            let timeline, _ =
                TimelineProjection.applyEvents None [ at 1L 0.0 (used "1" "a" "set_secret") ] TimelineProjection.empty
            match timeline.TerminalItems with
            | [ TimelineToolUse (_, id) ] ->
                Expect.equal (ToolUseId.value id) "t-1" "the item carries the minted id, not its position"
                Expect.equal
                    (TimelineProjection.toolUse id timeline |> Option.map ToolUse.label)
                    (Some "yession/set_secret")
                    "and it resolves to the call it names"
            | other -> failwithf "expected one tool-use item, got %A" other
    ]

// --- The terminal list (Plan 20, stage 0) --------------------------------------------------

let private closedNow (id: TerminalId) =
    SessionEvent.TerminalClosed { TerminalId = id; Reason = "closed by a peer"; By = None }

/// A sandbox a repo declared and this session brought up — what the chooser offers beside
/// `default`. Scoped, because the scope is what makes two repos' `dev` two places.
let private devInHello = SandboxRef.parse "octo/hello:dev" |> expect

let private sandboxStarted (sandbox: SandboxRef) (description: string option) =
    SessionEvent.WorkSandboxStarted
        { MessageId = message ("started-" + SandboxRef.render sandbox)
          Sandbox = sandbox
          Backend = "docker"
          Description = description
          Checkout = Some "/repos/octo/hello"
          Forwarded = []
          Realisation = []
          Actor = ActorRef.Configured (RepoRef.create "octo/hello" |> expect)
          OnBehalfOf = None
          CausedBy = None }

// --- A refusal is said once, where it was asked (P0-6) -------------------------------------

let private noSandbox = "there is no sandbox named 'default' in this session, and none at all"
let private newTerminal = Link.OpenTerminal ("", SandboxRef.defaultRef)

/// A command this client sent, refused — the two halves the connection dispatches: the record
/// of what was sent (`Client.Connection.Ask`), then the session's answer.
let private refusedAs (command: Link.SessionCommand) (reason: string) (model: ClientModel) : ClientModel =
    let request = RequestId.fresh ()
    model
    |> Support.step (CommandSentMsg (request, command))
    |> Support.step (CommandAnsweredMsg (request, Link.CommandRejected reason))

/// Which mounts the rendered page draws a refusal in, read off the page rather than off the
/// decision, so "exactly one" is a claim about the screen. The content pane is rendered after
/// the conversation column, so a notice past the pane's opening tag is the pane's.
let private refusalsDrawn (model: ClientModel) : RefusalMount list =
    let page = Support.render model
    let pane = page.IndexOf "data-content-panel"
    System.Text.RegularExpressions.Regex.Matches (page, "data-command-refused(?!-)")
    |> Seq.map (fun found -> if pane >= 0 && found.Index > pane then RefusalMount.Pane else RefusalMount.Chat)
    |> List.ofSeq

let private listTests =
    testList "The terminal list (Plan 20, stage 0)" [

        testCase "the list reads in open order, as the strip does" <| fun () ->
            // The list and the strip hold the same running terminals, and two surfaces
            // listing them in two orders is a difference a reader has to hold in their head.
            let model = clientOf [ at 1L 0.0 (opened terminalA "build"); at 2L 1.0 (opened terminalB "logs") ]
            Expect.equal
                (ClientModel.terminalRows model |> List.map (fun t -> TerminalId.value t.TerminalId))
                [ "term-a"; "term-b" ]
                "open order, exactly as the strip"

        // This replaced "closed terminals follow the open ones, most recently opened first".
        // That order made every close a reorder: the killed row dropped to the bottom and the
        // next live terminal's kill slid up under the pointer that had just pressed one, so a
        // double-click ended two terminals. A list somebody is pressing in holds still; a
        // closed row says it is closed by its mark, not by where it went.
        testCase "a terminal that closes keeps its place in the list" <| fun () ->
            let rows (model: ClientModel) = ClientModel.terminalRows model |> List.map (fun t -> TerminalId.value t.TerminalId)
            let before = clientOf [ at 1L 0.0 (opened terminalA "build"); at 2L 1.0 (opened terminalB "logs") ]
            let after = before |> thenFolded [ at 3L 2.0 (closedNow terminalA) ]
            Expect.equal (rows after) (rows before) "the same rows in the same places"

        testCase "arming a kill asks nothing of the session" <| fun () ->
            // The first press is a question to the person, not a request: nothing leaves.
            let _, effects =
                ClientModel.update (ArmKillMsg (Some terminalA)) (clientOf [ at 1L 0.0 (opened terminalA "build") ])
            Expect.equal effects [] "no kill sent on the first press"

        testCase "confirming an armed kill ends the terminal" <| fun () ->
            let armed =
                clientOf [ at 1L 0.0 (opened terminalA "build") ] |> Support.step (ArmKillMsg (Some terminalA))
            let _, effects = ClientModel.update (CloseTerminalMsg terminalA) armed
            Expect.isTrue (List.contains (ClientEffect.CloseTerminal terminalA) effects) "the second press is the kill"

        testCase "a kill armed over a terminal that closes is taken back" <| fun () ->
            // Somebody else ended it, or it exited: the armed control has nothing left to end,
            // and must not stand primed over the recording.
            let armed =
                clientOf [ at 1L 0.0 (opened terminalA "build") ] |> Support.step (ArmKillMsg (Some terminalA))
            let closed = armed |> thenFolded [ at 2L 1.0 (closedNow terminalA) ]
            Expect.isNone closed.KillArmed "nothing armed over a closed terminal"

        testCase "a terminal is recorded for this reader whichever way its transcript arrived" <| fun () ->
            // A LIVE terminal's length arrives as a catch-up hint before any chunk is
            // fetched; a CLOSED one's records arrive as chunks with no live hint behind
            // them. Asking only one of the two would refuse the verb the other one earns.
            let model = clientOf [ at 1L 0.0 (opened terminalA "build") ]
            Expect.isFalse (ClientModel.hasRecording terminalA model) "nothing has arrived yet"
            let byHint = Support.step (TerminalAvailableMsg (terminalA, 12)) model
            Expect.isTrue (ClientModel.hasRecording terminalA byHint) "a live terminal's length"
            let byRecord =
                Support.step
                    (TerminalRecordsMsg (terminalA, [ 0, { At = 0.0; Kind = TranscriptOutput; Data = "hi" } ]))
                    model
            Expect.isTrue (ClientModel.hasRecording terminalA byRecord) "a fetched record"

        testCase "choosing a terminal in the switcher selects its tab" <| fun () ->
            let model =
                clientOf [ at 1L 0.0 (opened terminalA "build"); at 2L 1.0 (opened terminalB "logs") ]
                |> Support.step ToggleSwitcherMsg
                |> Support.step (OpenInPaneMsg (Reading terminalB))
            Expect.equal (ClientModel.selectedTerminal model) (Some terminalB) "showing what was chosen"

        testCase "choosing a terminal in the switcher shuts it" <| fun () ->
            // One act, not two: a switcher that selected a terminal and stayed standing over
            // it would have the reader press twice for one intention.
            let model =
                clientOf [ at 1L 0.0 (opened terminalA "build"); at 2L 1.0 (opened terminalB "logs") ]
                |> Support.step ToggleSwitcherMsg
                |> Support.step (OpenInPaneMsg (Reading terminalB))
            Expect.isFalse model.Switcher "shut by the choosing"

        testCase "choosing a terminal in the switcher lands on its command line" <| fun () ->
            let switching =
                clientOf [ at 1L 0.0 (opened terminalA "build"); at 2L 1.0 (opened terminalB "logs") ]
                |> Support.step ToggleSwitcherMsg
            let _, effects = ClientModel.update (OpenInPaneMsg (Reading terminalB)) switching
            Expect.equal effects [ ClientEffect.Move (DomMove.FocusCommandLine terminalB) ] "onto what a terminal is for"

        testCase "Escape leaves the all page for the item it was laid over" <| fun () ->
            // Escape is `CloseSwitcherMsg` (the pane's keydown): the page leaves the document
            // with focus inside it, and the pivot item the reader is back on is where it goes.
            let switching = clientOf [ at 1L 0.0 (opened terminalA "build") ] |> Support.step ToggleSwitcherMsg
            let model, effects = ClientModel.update CloseSwitcherMsg switching
            Expect.isFalse model.Switcher "left"
            Expect.equal effects [ ClientEffect.Move DomMove.FocusPivot ] "onto the pivot's selected item"

        testCase "opening the switcher takes focus into it" <| fun () ->
            let _, effects = ClientModel.update ToggleSwitcherMsg (clientOf [ at 1L 0.0 (opened terminalA "build") ])
            Expect.equal effects [ ClientEffect.Move DomMove.FocusSwitcher ] "into the switcher"

        testCase "going to the all page shuts the menu of new things" <| fun () ->
            // A popover does not outlive the page it hung over.
            let model = clientOf [] |> Support.step TogglePaneMenuMsg |> Support.step ToggleSwitcherMsg
            Expect.isFalse model.PaneMenu "the menu went"

        // This replaced "opening the strip's menu shuts the switcher", from when the two were
        // popovers over one pane: `all` is a page now, the `+` is on screen over it, and its
        // menu opens over the page the way it opens over a terminal.
        testCase "the menu of new things opens over the all page and leaves it up" <| fun () ->
            let model = clientOf [] |> Support.step ToggleSwitcherMsg |> Support.step TogglePaneMenuMsg
            Expect.isTrue model.Switcher "the page stayed"

        // These are the tombstones of the states four agreeing fields allowed (Plan 25, stage
        // 2). Each was a real defect, watched happening in a browser; each is now unwritable
        // rather than merely unwritten.
        testCase "a chip tapped over the switcher shows the block it names" <| fun () ->
            // It used to retitle the pane and show the census: opening a tab cleared the
            // playing and rewound fields and left the list flag alone, so the reader tapped a
            // command and got nothing. A face cannot survive the choice that replaces it.
            let model =
                clientOf [ at 1L 0.0 (opened terminalA "build")
                           at 2L 1.0 (started terminalA "1" byAda "make" 1)
                           at 3L 2.0 (completed terminalA "1" (CommandSucceeded 0) 3) ]
                |> Support.step ToggleSwitcherMsg
                |> Support.step (OpenPreviewMsg (Preview.ofSubject (PreviewSubject.Block (terminalA, block "1"))))
            Expect.isFalse model.Switcher "the switcher stepped aside"
            Expect.equal (previewing model) (Some (PreviewSubject.Block (terminalA, block "1"))) "and the block is what is showing"

        testCase "the switcher's rewind is one act, and it watches behind live" <| fun () ->
            // It used to be a rewind and a select, and the select cleared the pin the rewind
            // had just taken — a verb whose whole effect was to leave the list.
            let model =
                withRecords (clientOf [ at 1L 0.0 (opened terminalA "shell") ])
                |> Support.step ToggleSwitcherMsg
                |> Support.step (RewindTerminalMsg terminalA)
            Expect.isFalse model.Switcher "the switcher stepped aside"
            Expect.isTrue (ClientModel.isRewound terminalA model) "and the reader is behind live"
            Expect.isTrue (ClientModel.terminalPlays terminalA model) "watching the recording"

        testCase "showing anything else replaces the whole read, pin and all" <| fun () ->
            // No entry clears a subset and trusts the rest: a rewind on one terminal cannot
            // survive a reader going somewhere else, whatever they go to.
            let rewound =
                withRecords (clientOf [ at 1L 0.0 (opened terminalA "shell"); at 2L 1.0 (opened terminalB "logs") ])
                |> Support.step (RewindTerminalMsg terminalA)
            Expect.isTrue (ClientModel.isRewound terminalA rewound) "arranged behind live"
            let moved = Support.step (ShowInPaneMsg (Reading terminalB)) rewound
            Expect.isFalse (ClientModel.isRewound terminalA moved) "the pin died with the read that held it"

        // This replaced "leaving the list resumes the read it covered" (and the preview it
        // covered): the list was a face the pane went to and had to come back from. The
        // switcher is laid over the pane, so it covers nothing — opening and shutting it is
        // not a way to lose your place, a rewind included.
        testCase "opening and closing the switcher does not move the pane's read" <| fun () ->
            let rewound =
                withRecords (clientOf [ at 1L 0.0 (opened terminalA "shell") ])
                |> Support.step (RewindTerminalMsg terminalA)
            let glanced = rewound |> Support.step ToggleSwitcherMsg |> Support.step CloseSwitcherMsg
            Expect.equal glanced.Pane rewound.Pane "the same read, pin and all"

        testCase "opening the switcher opens the pane it hangs in" <| fun () ->
            // Looking for a terminal you cannot see is exactly the case where the pane is
            // shut, so the switcher brings it with it.
            let model = clientOf [ at 1L 0.0 (opened terminalA "build") ]
            Expect.isFalse model.TerminalsOpen "the pane starts shut"
            let switching = Support.step ToggleSwitcherMsg model
            Expect.isTrue switching.TerminalsOpen "and the pane came with it"

        testCase "hiding the pane leaves the all page" <| fun () ->
            // Shown again, the pane is on what it holds rather than on the list of it.
            let model = clientOf [ at 1L 0.0 (opened terminalA "build") ] |> Support.step ToggleSwitcherMsg
            Expect.isFalse (Support.step ToggleContentMsg model).Switcher "shut with the pane"

        // --- The strip's × (P2-2) ------------------------------------------------------------

        testCase "the strip's × arms a kill on its first press" <| fun () ->
            let model = clientOf [ at 1L 0.0 (opened terminalA "build") ] |> Support.step (ShowInPaneMsg (Reading terminalA))
            Expect.equal (ClientModel.killPress terminalA model) (Some (ArmKillMsg (Some terminalA))) "the first press asks"

        testCase "the strip's × kills on the press after the arming" <| fun () ->
            let armed =
                clientOf [ at 1L 0.0 (opened terminalA "build") ]
                |> Support.step (ShowInPaneMsg (Reading terminalA))
                |> Support.step (ArmKillMsg (Some terminalA))
            Expect.equal (ClientModel.killPress terminalA armed) (Some (CloseTerminalMsg terminalA)) "the second press kills"

        testCase "a press on another terminal's kill arms that one instead" <| fun () ->
            // One slot: a press elsewhere is a new question, never the confirm of the first.
            let armed =
                clientOf [ at 1L 0.0 (opened terminalA "build"); at 2L 1.0 (opened terminalB "logs") ]
                |> Support.step (ArmKillMsg (Some terminalA))
            Expect.equal (ClientModel.killPress terminalB armed) (Some (ArmKillMsg (Some terminalB))) "B asked, A not killed"

        testCase "a closed terminal has no kill to press" <| fun () ->
            let model = clientOf [ at 1L 0.0 (opened terminalA "build"); at 2L 1.0 (closedNow terminalA) ]
            Expect.isNone (ClientModel.killPress terminalA model) "nothing left to end"

        // A closed tab stays while it is the one on screen (`settle`), and had no way to be
        // put away short of choosing another (desktop journey, finding 16). Its × does that.
        testCase "a closed terminal's tab can be put away, and the pane moves to the tab beside it" <| fun () ->
            let model =
                clientOf [ at 1L 0.0 (opened terminalA "build"); at 2L 1.0 (opened terminalB "logs") ]
                |> Support.step (ShowInPaneMsg (Reading terminalB))
                |> Support.step (ShowInPaneMsg (Reading terminalA))
                |> fun model -> withPage [ at 3L 2.0 (closedNow terminalA) ] model
                |> Support.step (DismissTabMsg terminalA)
            Expect.equal (model.Tabs, ClientModel.selectedTerminal model) ([ terminalB ], Some terminalB) "gone, and on its neighbour"

        testCase "a running terminal's tab cannot be put away" <| fun () ->
            // Only a kill ends a tab that holds something running (P2-2); the refusal is the
            // reducer's, so no control can get round it.
            let model =
                clientOf [ at 1L 0.0 (opened terminalA "build") ] |> Support.step (ShowInPaneMsg (Reading terminalA))
            Expect.equal (Support.step (DismissTabMsg terminalA) model).Tabs [ terminalA ] "still there"

        testCase "the selected closed tab wears a way to put it away, and no kill" <| fun () ->
            let html =
                clientOf [ at 1L 0.0 (opened terminalA "build") ]
                |> Support.step (ShowInPaneMsg (Reading terminalA))
                |> fun model -> withPage [ at 2L 1.0 (closedNow terminalA) ] model
                |> Support.render
            let pivot = markupAt Dom.Hooks.panePivot html
            Expect.equal
                (pivot.Contains (Dom.attr Dom.Hooks.paneTabDismiss (TerminalId.value terminalA)),
                 pivot.Contains (Dom.attr Dom.Hooks.terminalClose (TerminalId.value terminalA)))
                (true, false)
                "put away, not killed"

        testCase "the selected tab wears its terminal's kill" <| fun () ->
            // The acceptance: there is no control that drops a tab and leaves its terminal
            // running. The tab's one destructive control IS the kill.
            let html = Support.render (clientOf [ at 1L 0.0 (opened terminalA "build") ] |> Support.step (ShowInPaneMsg (Reading terminalA)))
            Expect.stringContains
                (markupAt "role=\"tablist\"" html)
                (Dom.attr Dom.Hooks.terminalClose (TerminalId.value terminalA))
                "the × on the tab is the kill"

        // --- The switcher's doors (P2-2) -------------------------------------------------------

        // These replaced the strip's `+N` overflow count, which opened the switcher only while
        // tabs were scrolled past the strip's edge, and the head's name, which opened it
        // always: `all` is both doors, as the pivot's last item — outside the part of the
        // pivot that scrolls, so no number of tabs can carry it off the edge.
        testCase "the pivot ends with all, outside the part that scrolls" <| fun () ->
            let model = clientOf [ at 1L 0.0 (opened terminalA "build") ] |> Support.step (ShowInPaneMsg (Reading terminalA))
            let pivot = markupAt Dom.Hooks.panePivot (Support.render model)
            Expect.isTrue (pivot.Contains Dom.Hooks.paneSwitcher) "all is a pivot item"
            Expect.isFalse ((markupAt Dom.Hooks.paneStrip pivot).Contains Dom.Hooks.paneSwitcher) "and does not scroll"

        testCase "on the all page, all is the one selected pivot item" <| fun () ->
            let model =
                clientOf [ at 1L 0.0 (opened terminalA "build"); at 2L 1.0 (opened terminalB "logs") ]
                |> Support.step (ShowInPaneMsg (Reading terminalA))
                |> Support.step ToggleSwitcherMsg
            let pivot = markupAt Dom.Hooks.panePivot (Support.render model)
            Expect.equal
                (attributeOf "role=\"tab\"" "aria-selected" pivot |> List.filter ((=) "true") |> List.length,
                 attributeOf Dom.Hooks.paneSwitcher "aria-selected" pivot)
                (1, [ "true" ])
                "one item selected, and it is all"

        testCase "the switcher's shortcut is Ctrl or Cmd with the backquote key" <| fun () ->
            Expect.equal
                [ ClientModel.opensSwitcher "Backquote" true false
                  ClientModel.opensSwitcher "Backquote" false true
                  ClientModel.opensSwitcher "Backquote" false false
                  ClientModel.opensSwitcher "KeyA" true false ]
                [ true; true; false; false ]
                "a modifier and the key, and nothing else"

        // --- Somewhere to open one (Plan 20, stage 1) ---------------------------------------

        testCase "the chooser always offers this session's own sandbox" <| fun () ->
            // Every session has `default` from boot, so nothing started it and no event says
            // so. It is offered because the session exists.
            Expect.equal
                (ClientModel.sandboxRows (clientOf []) |> List.map SandboxRef.render)
                [ "default" ]
                "one place, always"

        testCase "a sandbox a repo started is offered after it" <| fun () ->
            // `default` leads because it is the plain answer, and a reader scanning for one
            // place to put a shell should not have to read past three repos to find it.
            let model = clientOf [ at 1L 0.0 (sandboxStarted devInHello (Some "the work sandbox")) ]
            Expect.equal
                (ClientModel.sandboxRows model |> List.map SandboxRef.render)
                [ "default"; "octo/hello:dev" ]
                "the session's own, then what came up"

        testCase "a chooser row says what its sandbox is for, as the declaration said it" <| fun () ->
            // From the start that brought it up, which is the only place it is recorded —
            // never today's file read onto last week's event.
            let model = clientOf [ at 1L 0.0 (sandboxStarted devInHello (Some "where the tests run")) ]
            Expect.equal
                (ClientModel.sandboxPurpose devInHello model)
                (Some "where the tests run")
                "the words the file used"

        testCase "a sandbox whose declaration said nothing has nothing to say" <| fun () ->
            // Absent is not empty: a row with no note is a row, and inventing one would be
            // this surface writing the file's prose for it.
            let model = clientOf [ at 1L 0.0 (sandboxStarted devInHello None) ]
            Expect.equal (ClientModel.sandboxPurpose devInHello model) None "nothing to say"

        testCase "the switcher is shut until somebody opens it" <| fun () ->
            // It is reached by asking — the head's name, the strip's count, the shortcut —
            // and never put in front of a reader who did not.
            Expect.isFalse (clientOf []).Switcher "nothing open is not a reason to show it"
            Expect.isTrue (Support.step ToggleSwitcherMsg (clientOf [])).Switcher "asked for, shown"

        testCase "asking for a terminal shuts the menu that asked" <| fun () ->
            // The entry pressed is about to leave the document, and a menu left standing over
            // a terminal on its way is a surface the reader has to dismiss before they can see
            // what they asked for.
            let opened =
                clientOf []
                |> Support.step TogglePaneMenuMsg
                |> Support.step (OpenTerminalMsg ("", devInHello))
            Expect.isFalse opened.PaneMenu "shut by the asking"

        // What the session REFUSED. Every command answers, and until this the launch surface
        // was the only caller that read the answer — so a press the session would not honour
        // did nothing and said nothing. It cost an afternoon of diagnosing why terminals
        // would not open on one machine; the refusal had been saying why the whole time, to
        // a client that threw it away.
        testCase "a command the session refused is kept, in the words it refused with" <| fun () ->
            let refused =
                clientOf []
                |> Support.step (CommandAnsweredMsg (RequestId.fresh (), Link.CommandRejected "there is no sandbox named 'default' in this session"))
            Expect.equal
                (refused.Refused |> Option.map (fun refusal -> refusal.Reason))
                (Some "there is no sandbox named 'default' in this session")
                "the session's own sentence, which is written to be read"

        testCase "a command the session accepted clears the last refusal" <| fun () ->
            // A notice about something that did not work, left standing beside something that
            // did, is a screen arguing with itself.
            let model =
                clientOf []
                |> Support.step (CommandAnsweredMsg (RequestId.fresh (), Link.CommandRejected "no"))
                |> Support.step (CommandAnsweredMsg (RequestId.fresh (), Link.CommandAccepted))
            Expect.equal model.Refused None "nothing left to say"

        testCase "a refusal read is a refusal done with" <| fun () ->
            // News, not a state: nothing recovers it and nothing re-raises it, so it has to be
            // dismissible or it is a notice that outlives its own subject.
            let model =
                clientOf []
                |> Support.step (CommandAnsweredMsg (RequestId.fresh (), Link.CommandRejected "no"))
                |> Support.step DismissRefusalMsg
            Expect.equal model.Refused None "put away"

        // A refused New terminal is still the ANSWER to the press that asked for it. Left
        // owed, the next terminal this person opened anywhere — from another tab, which the
        // log cannot tell from this one — was taken for the one this press asked for, and
        // pulled the pane over to it long after the press had been told no.
        testCase "a refused New terminal is owed nothing: the next terminal of mine leaves the pane where it is" <| fun () ->
            let model =
                clientOf [ at 1L 0.0 (opened terminalA "build") ]
                |> Support.step (ShowInPaneMsg (Reading terminalA))
                |> Support.step (OpenTerminalMsg ("", SandboxRef.defaultRef))
                |> refusedAs newTerminal noSandbox
                |> withPage [ at 2L 1.0 (opened terminalB "mine, from another tab") ]
            Expect.equal (ClientModel.selectedTerminal model) (Some terminalA) "still where I was"

        // Where the refusal is SAID. The press for a terminal verb was made in the pane, so
        // the answer is drawn there — a reader looking at the pane does not look up at the
        // conversation's header for it — and ONE mount draws it: the same refusal in both
        // places is a screen saying one thing twice.
        testCase "a terminal verb's refusal is drawn in the pane, and only there, while it is open" <| fun () ->
            let model =
                clientOf []
                |> Support.step ToggleContentMsg
                |> refusedAs newTerminal noSandbox
            Expect.equal (refusalsDrawn model) [ RefusalMount.Pane ] "where the press was"

        testCase "with the pane shut, a terminal verb's refusal is drawn in the conversation, and only there" <| fun () ->
            let model = clientOf [] |> refusedAs newTerminal noSandbox
            Expect.equal (refusalsDrawn model) [ RefusalMount.Chat ] "never behind a shut column"

        testCase "a refusal of something pressed in the conversation is drawn there, even with the pane open" <| fun () ->
            let turn = AgentTurnId.create "turn-1" |> expect
            let model =
                clientOf []
                |> Support.step ToggleContentMsg
                |> refusedAs (Link.InterruptAgentTurn turn) "that turn is not running"
            Expect.equal (refusalsDrawn model) [ RefusalMount.Chat ] "where the interrupt was"

        // The notice leaving the document takes whatever was focused in it along, and focus
        // left there falls to `body` — the keyboard user who pressed its dismiss is then
        // nowhere. It goes on to the surface the notice sat over.
        testCase "a refusal holding focus in the pane hands it to the pane when it goes" <| fun () ->
            let model =
                clientOf []
                |> Support.step ToggleContentMsg
                |> refusedAs newTerminal noSandbox
                |> Support.step (RefusalFocusMsg (Some RefusalMount.Pane))
            Expect.equal
                (ClientModel.update DismissRefusalMsg model |> snd)
                [ ClientEffect.Move DomMove.FocusPaneEmpty ]
                "onto what the pane offers — here, its New terminal"

        testCase "a refusal holding focus in the conversation hands it to the composer when it goes" <| fun () ->
            let model =
                clientOf []
                |> refusedAs newTerminal noSandbox
                |> Support.step (RefusalFocusMsg (Some RefusalMount.Chat))
            Expect.equal
                (ClientModel.update (CommandAnsweredMsg (RequestId.fresh (), Link.CommandAccepted)) model |> snd)
                [ ClientEffect.Move DomMove.FocusComposer ]
                "taken away by an acceptance, and focus goes on all the same"

        // An acceptance can clear the notice under somebody typing somewhere else entirely.
        testCase "a refusal nobody is in leaves focus where it is when it goes" <| fun () ->
            let model =
                clientOf []
                |> refusedAs newTerminal noSandbox
                |> Support.step (RefusalFocusMsg (Some RefusalMount.Chat))
                |> Support.step (RefusalFocusMsg None)
            Expect.isEmpty (ClientModel.update DismissRefusalMsg model |> snd) "nothing to hand on"

        testCase "the menu opens and shuts on the one control" <| fun () ->
            // A toggle rather than a pair, so the control that opened it is the control that
            // shuts it and focus never has to go looking for a replacement.
            let model = clientOf [] |> Support.step TogglePaneMenuMsg
            Expect.isTrue model.PaneMenu "open"
            Expect.isFalse (Support.step TogglePaneMenuMsg model).PaneMenu "and shut by the same press"

        testCase "choosing a place asks for a terminal there, named by nothing" <| fun () ->
            // The only thing the press says is WHERE, so the session names the terminal after
            // it (`TerminalTitle.inSandbox`). A title invented here would be this surface
            // guessing at a rule the session already holds.
            let _, effects = ClientModel.update (OpenTerminalMsg ("", devInHello)) (clientOf [])
            Expect.equal
                effects
                [ ClientEffect.OpenTerminal ("", devInHello) ]
                "where they pressed, and no name of our own"
    ]

// --- Tabs, pins, and the preview slot (Plan 20, stage 1) ------------------------------------

let private tabTests =
    testList "Tabs are terminals; the chat opens previews (P2-1)" [

        testCase "nothing a session DOES puts a tab in my strip" <| fun () ->
            // Terminals opened — by me, by the agent — are things that happened, and the strip
            // is not a record of what happened: it holds the terminals I asked for.
            let mine =
                SessionEvent.TerminalOpened
                    { TerminalId = terminalA; OpenedBy = PeerRef ada; Title = (TerminalTitle.fromProse "mine")
                      Sandbox = Some SandboxRef.defaultRef; Renewable = false }
            let theirs =
                SessionEvent.TerminalOpened
                    { TerminalId = terminalB; OpenedBy = ActorRef.Agent; Title = (TerminalTitle.fromProse "running the tests")
                      Sandbox = Some SandboxRef.defaultRef; Renewable = false }
            let model = clientOf [ at 1L 0.0 mine; at 2L 1.0 theirs ]
            Expect.equal (stripKeys model) [ "terminal:term-a" ] "a tab for the one I asked for, and none for the agent's"

        // These three replace "a terminal I kept stays when it closes" and "a terminal that
        // ends takes its tab with it, when nobody kept it". A pin does not exist: a terminal
        // in the strip is always kept while it runs, and once it has closed the rule is
        // whether the reader is looking at it.
        testCase "a terminal that closes while selected stays in the strip" <| fun () ->
            let model =
                clientOf [ at 1L 0.0 (opened terminalA "build"); at 2L 1.0 (opened terminalB "logs") ]
                |> Support.step (ShowInPaneMsg (Reading terminalA))
                |> thenFolded [ at 3L 2.0 (closedNow terminalA) ]
            Expect.equal (stripKeys model) [ "terminal:term-a"; "terminal:term-b" ] "not taken from under the reader"

        testCase "a closed terminal leaves the strip when another is chosen" <| fun () ->
            // Which is also what a recording opened from the list does: the list is the door
            // to every recording, and the strip is what is running plus what is on screen.
            let model =
                clientOf [ at 1L 0.0 (opened terminalA "build"); at 2L 1.0 (opened terminalB "logs") ]
                |> Support.step (ShowInPaneMsg (Reading terminalA))
                |> thenFolded [ at 3L 2.0 (closedNow terminalA) ]
                |> Support.step (ShowInPaneMsg (Reading terminalB))
            Expect.equal (stripKeys model) [ "terminal:term-b" ] "gone once the reader looked elsewhere"

        testCase "a terminal that closes unselected leaves the strip at once" <| fun () ->
            let model =
                clientOf [ at 1L 0.0 (opened terminalA "build"); at 2L 1.0 (opened terminalB "logs") ]
                |> Support.step (ShowInPaneMsg (Reading terminalB))
                |> thenFolded [ at 3L 2.0 (closedNow terminalA) ]
            Expect.equal (stripKeys model) [ "terminal:term-b" ] "only what is running, and what is on screen"

        testCase "a closed terminal is still in the list" <| fun () ->
            let model =
                clientOf [ at 1L 0.0 (opened terminalA "build"); at 2L 1.0 (opened terminalB "logs") ]
                |> thenFolded [ at 3L 2.0 (closedNow terminalA) ]
            Expect.equal
                (ClientModel.terminalRows model |> List.map (fun t -> TerminalId.value t.TerminalId))
                [ "term-a"; "term-b" ]
                "the list is nobody's working set"

        testCase "showing a terminal reached from the chat opens its tab" <| fun () ->
            // "Show in terminal" from a preview of the agent's command: the reader asked for
            // that terminal, so it is in their strip afterwards.
            let agentsBlock =
                [ at 1L 0.0 (openedBy ActorRef.Agent terminalA "running the tests")
                  at 2L 1.0 (started terminalA "1" byAda "ls -la" 1)
                  at 3L 2.0 (completed terminalA "1" (CommandSucceeded 0) 3) ]
            let reached =
                clientOf agentsBlock
                |> Support.step (chip terminalA "1")
                |> Support.step (ShowInTerminalMsg (terminalA, block "1"))
            Expect.equal (stripKeys reached, previewing reached) ([ "terminal:term-a" ], None) "the terminal, and the preview taken down"

        // "no tab offers a close" was here, while a tab had no × at all (P2-1). The × is back
        // as the terminal's kill (P2-2), pinned by "the selected tab wears its terminal's
        // kill": there is still no control that drops a tab and leaves its terminal running.

        // What the agent can do to a strip (`open_tab` / `close_tab` / `focus_tab`), which
        // reaches every client as events on the log rather than as synced state.
        testCase "a terminal the agent opened is in my strip, and does not take my screen" <| fun () ->
            let model =
                clientOf [ at 1L 0.0 (opened terminalA "build"); at 2L 1.0 (openedBy ActorRef.Agent terminalB "tests") ]
                |> Support.step (ShowInPaneMsg (Reading terminalA))
                |> thenFolded [ at 3L 2.0 (SessionEvent.TabOpened { TabOpened.Ref = ViewingTerminal terminalB; TabOpened.Focus = false }) ]
            Expect.equal
                (stripKeys model, ClientModel.selectedTerminal model)
                ([ "terminal:term-a"; "terminal:term-b" ], Some terminalA)
                "in reach, and I am still reading what I was reading"

        // Replaces "a tab the agent opened is in my strip": a file is not a tab, and a
        // preview nobody asked to see is the screen taken by somebody else's act.
        testCase "an agent's unfocused open_tab of a file opens nothing" <| fun () ->
            let chart = Content.ContentRef.create "artifacts/chart.png/0000-ab12cd" |> expect
            let before = clientOf [ at 1L 0.0 (opened terminalA "build") ] |> Support.step (ShowInPaneMsg (Reading terminalA))
            let model =
                before |> thenFolded [ at 2L 1.0 (SessionEvent.TabOpened { TabOpened.Ref = ViewingFile chart; TabOpened.Focus = false }) ]
            Expect.equal (model.Pane, stripKeys model) (before.Pane, stripKeys before) "the pane exactly as it was"

        testCase "an agent's focused open_tab of a file opens a preview over my terminal" <| fun () ->
            // `focus_tab` is somebody saying "show me", not an agent deciding.
            let chart = Content.ContentRef.create "artifacts/chart.png/0000-ab12cd" |> expect
            let model =
                clientOf [ at 1L 0.0 (opened terminalA "build") ]
                |> Support.step (ShowInPaneMsg (Reading terminalA))
                |> thenFolded [ at 2L 1.0 (SessionEvent.TabOpened { TabOpened.Ref = ViewingFile chart; TabOpened.Focus = true }) ]
            Expect.equal
                (model.Pane)
                (Some (Previewing (Preview.ofSubject (PreviewSubject.Content chart), Some (Reading terminalA))))
                "the file, laid over the terminal I was on"

        testCase "a focus that is already history does not move a reader who just arrived" <| fun () ->
            // The log keeps a focus for ever; a reader opening the session an hour later must
            // not be landed on what somebody was being shown then, ahead of everything since.
            let chart = Content.ContentRef.create "artifacts/chart.png/0000-ab12cd" |> expect
            let model =
                clientOf
                    [ at 1L 0.0 (SessionEvent.TabOpened { TabOpened.Ref = ViewingFile chart; TabOpened.Focus = true })
                      at 2L 1.0 (opened terminalA "build") ]
            Expect.equal (previewing model) None "arriving history is not a command"

        testCase "a preview is closed by its subject's TabClosed" <| fun () ->
            let chart = Content.ContentRef.create "artifacts/chart.png/0000-ab12cd" |> expect
            let model =
                clientOf [ at 1L 0.0 (opened terminalA "build") ]
                |> Support.step (ShowInPaneMsg (Reading terminalA))
                |> thenFolded [ at 2L 1.0 (SessionEvent.TabOpened { TabOpened.Ref = ViewingFile chart; TabOpened.Focus = true }) ]
                |> thenFolded [ at 3L 2.0 (SessionEvent.TabClosed { TabClosed.Ref = ViewingFile chart }) ]
            Expect.equal model.Pane (Some (OnTerminal (Reading terminalA))) "back to the terminal it was over"

        // Replaces "a tab the agent closed goes, unless I kept it": nothing is kept against
        // the agent any more, except what the reader is looking at.
        testCase "a terminal the agent closed goes from my strip" <| fun () ->
            let model =
                clientOf [ at 1L 0.0 (opened terminalA "build"); at 2L 1.0 (openedBy ActorRef.Agent terminalB "tests") ]
                |> Support.step (ShowInPaneMsg (Reading terminalA))
                |> thenFolded [ at 3L 2.0 (SessionEvent.TabOpened { TabOpened.Ref = ViewingTerminal terminalB; TabOpened.Focus = false }) ]
                |> thenFolded [ at 4L 3.0 (SessionEvent.TabClosed { TabClosed.Ref = ViewingTerminal terminalB }) ]
            Expect.equal (stripKeys model) [ "terminal:term-a" ] "taken back"

        testCase "a terminal the agent closed stays while I am looking at it" <| fun () ->
            let model =
                clientOf [ at 1L 0.0 (opened terminalA "build") ]
                |> Support.step (ShowInPaneMsg (Reading terminalA))
                |> thenFolded [ at 2L 1.0 (SessionEvent.TabClosed { TabClosed.Ref = ViewingTerminal terminalA }) ]
            Expect.equal (stripKeys model) [ "terminal:term-a" ] "not taken from under the reader"

        // The case whose absence let the strip go wrong for a year, asked of the rule that
        // still answers it. `Principal.toActor (principalFor peer)` is what stamps a peer's
        // command, so the actor in the log is the peer only while nobody verified it; once
        // the Manager attributes a user, everything this same connection asks for is written
        // `UserRef`. A client that answers "is this mine" by building `PeerRef` out of its own
        // peer id therefore matches nothing it did — and a lease is what decides whether the
        // live screen takes your keystrokes, says "you" over it, and reports your viewport to
        // the pty, so the holder's own terminal read as somebody else's, read-only, at the
        // wrong size.
        testCase "a lease stamped with my verified user is mine" <| fun () ->
            let model = clientOf [ at 1L 0.0 (attributed ada) ]
            Expect.isTrue (ClientModel.isMine (UserRef nick) model) "the user I joined as is me"
            Expect.isFalse (ClientModel.isMine (UserRef bobsUser) model) "another user is not"
            Expect.isFalse (ClientModel.isMine ActorRef.Agent model) "the agent is not"

        // Pressing "new terminal" TAKES YOU THERE. What it did instead was mint a terminal,
        // add a faint word to the strip and leave the pane on whatever was already showing.
        // On a phone the strip scrolls, so the new tab could be off screen and the press
        // changed nothing a person could see at all. It read, correctly, as a dead button.
        testCase "the terminal I asked for is the one I am shown" <| fun () ->
            let model =
                clientOf [ at 1L 0.0 (opened terminalA "build") ]
                |> Support.step (ShowInPaneMsg (Reading terminalA))
                |> Support.step (OpenTerminalMsg ("terminal", SandboxRef.defaultRef))
                |> withPage [ at 2L 1.0 (opened terminalB "the one I asked for") ]
            Expect.equal (ClientModel.selectedTerminal model) (Some terminalB) "the pane went to the terminal the press asked for"

        testCase "the press is what asks the session for the terminal" <| fun () ->
            // The other half of the one act: remembering that I asked (above) and asking are
            // one message, so a button cannot do either without the other.
            let _, effects = ClientModel.update (OpenTerminalMsg ("build", SandboxRef.defaultRef)) (clientOf [])
            Expect.equal
                effects
                [ ClientEffect.OpenTerminal ("build", SandboxRef.defaultRef) ]
                "one request, under the title and in the sandbox pressed for"

        testCase "a chip that opens a preview takes the reader to the pane" <| fun () ->
            // One message for both halves, so no chip can open a pane and leave focus behind it.
            let _, effects = ClientModel.update (chip terminalA "1") (clientOf oneBlock)
            Expect.equal effects [ ClientEffect.Move DomMove.FocusPane ] "focus is asked to follow it"

        // Where focus lands after each act in the pane (the focus contract). Every one of these
        // takes away the control that was pressed, or puts a new surface in front of the
        // reader, and each says where the keyboard goes next — so that it never falls to
        // `body`, and never onto something in a pane that is not on screen.
        testCase "a row of the list opening a terminal lands on its command line" <| fun () ->
            // A terminal is a thing you type into: being taken to one is being taken to the
            // line that takes the typing, not to the region around it.
            let _, effects =
                ClientModel.update
                    (OpenInPaneMsg (Reading terminalA))
                    (clientOf [ at 1L 0.0 (opened terminalA "build") ])
            Expect.equal effects [ ClientEffect.Move (DomMove.FocusCommandLine terminalA) ] "onto the command line"

        testCase "showing the pane lands on the shown terminal's command line" <| fun () ->
            let shut = clientOf [ at 1L 0.0 (opened terminalA "build") ]
            Expect.isFalse shut.TerminalsOpen "the pane starts shut"
            let _, effects = ClientModel.update ToggleContentMsg shut
            Expect.equal effects [ ClientEffect.Move (DomMove.FocusCommandLine terminalA) ] "onto the command line"

        testCase "showing an empty pane lands on the press that fills it" <| fun () ->
            let _, effects = ClientModel.update ToggleContentMsg (clientOf [])
            Expect.equal effects [ ClientEffect.Move DomMove.FocusPaneEmpty ] "onto the empty pane's press"

        testCase "hiding the pane returns focus to the chip that opened the preview it showed" <| fun () ->
            let showing = clientOf oneBlock |> Support.step (chip terminalA "1")
            let _, effects = ClientModel.update ToggleContentMsg showing
            Expect.equal
                effects
                [ ClientEffect.Move (DomMove.FocusChat (PreviewSubject.Block (terminalA, block "1"))) ]
                "back to the chip"

        testCase "hiding a pane with no preview sends focus to the way back in" <| fun () ->
            // No chip opened what is showing — and everything in the pane is about to be out
            // of reach.
            let showing = clientOf [ at 1L 0.0 (opened terminalA "build") ] |> Support.step ToggleContentMsg
            let _, effects = ClientModel.update ToggleContentMsg showing
            Expect.equal effects [ ClientEffect.Move DomMove.FocusPaneReopen ] "onto the reopen control"

        // The nav drawer's arrival on a phone (`PaneShell.bringColumnOn`): one sheet over the
        // chat at a time. It SHUTS, it never opens — the drawer arriving over a pane that was
        // already shut must not bring the pane back — and it moves no focus, because the
        // drawer arriving is what says where focus goes.
        testCase "hiding the pane shuts an open one" <| fun () ->
            let showing = clientOf [ at 1L 0.0 (opened terminalA "build") ] |> Support.step ToggleContentMsg
            Expect.isFalse (showing |> Support.step HideContentMsg).TerminalsOpen "shut"

        testCase "hiding a shut pane leaves it shut" <| fun () ->
            let shut = clientOf [ at 1L 0.0 (opened terminalA "build") ]
            Expect.isFalse (shut |> Support.step HideContentMsg).TerminalsOpen "still shut, not toggled open"

        testCase "hiding the pane moves no focus of its own" <| fun () ->
            let showing = clientOf [ at 1L 0.0 (opened terminalA "build") ] |> Support.step ToggleContentMsg
            let _, effects = ClientModel.update HideContentMsg showing
            Expect.isEmpty effects "the drawer arriving says where focus goes"

        // "closing the last tab sends focus to the way back in" and "closing a tab that leaves
        // others moves no focus of its own" were here: closing a tab is not an act this strip
        // has until P2-2 makes it the kill, and the kill's own landing is below.

        testCase "a terminal pressed for lands on its command line when it arrives" <| fun () ->
            let asked =
                clientOf [ at 1L 0.0 (opened terminalA "build") ]
                |> Support.step (ShowInPaneMsg (Reading terminalA))
                |> Support.step (OpenTerminalMsg ("", SandboxRef.defaultRef))
            let _, effects =
                ClientModel.update
                    (EventsPageMsg { Events = [ at 2L 1.0 (opened terminalB "new") ]; LastOffset = Some (EventOffset.create 2L |> expect); IsEnd = true })
                    asked
            Expect.equal
                effects
                [ ClientEffect.Move (DomMove.OnArrival (DomMove.FocusCommandLine terminalB)) ]
                "onto the new terminal's command line, if the hand is still in the pane"

        testCase "a terminal nobody here pressed for moves no focus" <| fun () ->
            let showing =
                clientOf [ at 1L 0.0 (opened terminalA "build") ]
                |> Support.step (ShowInPaneMsg (Reading terminalA))
            let _, effects =
                ClientModel.update
                    (EventsPageMsg { Events = [ at 2L 1.0 (opened terminalB "elsewhere") ]; LastOffset = Some (EventOffset.create 2L |> expect); IsEnd = true })
                    showing
            Expect.equal effects [] "the reader's cursor stays where they put it"

        testCase "a kill pressed in the switcher lands on the row that takes the killed one's place" <| fun () ->
            let rows = [ at 1L 0.0 (opened terminalA "build"); at 2L 1.0 (opened terminalB "test") ]
            let pressed = clientOf rows |> Support.step ToggleSwitcherMsg |> Support.step (CloseTerminalMsg terminalA)
            let _, effects =
                ClientModel.update
                    (EventsPageMsg
                        { Events = [ at 3L 2.0 (SessionEvent.TerminalClosed { TerminalId = terminalA; Reason = "closed by a peer"; By = None }) ]
                          LastOffset = Some (EventOffset.create 3L |> expect)
                          IsEnd = true })
                    pressed
            Expect.equal
                effects
                [ ClientEffect.Move (DomMove.OnArrival (DomMove.FocusSwitcherRow terminalB)) ]
                "onto the next terminal's row"

        testCase "a kill of the switcher's last row lands on the row before it" <| fun () ->
            let rows = [ at 1L 0.0 (opened terminalA "build"); at 2L 1.0 (opened terminalB "test") ]
            let pressed = clientOf rows |> Support.step ToggleSwitcherMsg |> Support.step (CloseTerminalMsg terminalB)
            let _, effects =
                ClientModel.update
                    (EventsPageMsg
                        { Events = [ at 3L 2.0 (SessionEvent.TerminalClosed { TerminalId = terminalB; Reason = "closed by a peer"; By = None }) ]
                          LastOffset = Some (EventOffset.create 3L |> expect)
                          IsEnd = true })
                    pressed
            Expect.equal effects [ ClientEffect.Move (DomMove.OnArrival (DomMove.FocusSwitcherRow terminalA)) ] "onto the row above"

        testCase "a kill of the selected tab lands on its own tab" <| fun () ->
            // The selected tab stays, closed, until the reader chooses another (`settle`), and
            // the hand that pressed its × is already there.
            let rows = [ at 1L 0.0 (opened terminalA "build"); at 2L 1.0 (opened terminalB "test") ]
            let pressed =
                clientOf rows
                |> Support.step (ShowInPaneMsg (Reading terminalA))
                |> Support.step (ShowInPaneMsg (Reading terminalB))
                |> Support.step (CloseTerminalMsg terminalB)
            let _, effects =
                ClientModel.update
                    (EventsPageMsg
                        { Events = [ at 3L 2.0 (SessionEvent.TerminalClosed { TerminalId = terminalB; Reason = "closed by a peer"; By = None }) ]
                          LastOffset = Some (EventOffset.create 3L |> expect)
                          IsEnd = true })
                    pressed
            Expect.equal effects [ ClientEffect.Move (DomMove.OnArrival (DomMove.FocusTab terminalB)) ] "onto its own tab"

        testCase "a kill of a tab that is not selected lands on its neighbour" <| fun () ->
            // Delete on a focused tab the reader had walked to: it leaves the strip at once, and
            // the tab that takes its place takes the focus.
            let rows = [ at 1L 0.0 (opened terminalA "build"); at 2L 1.0 (opened terminalB "test") ]
            let pressed =
                clientOf rows
                |> Support.step (ShowInPaneMsg (Reading terminalA))
                |> Support.step (ShowInPaneMsg (Reading terminalB))
                |> Support.step (CloseTerminalMsg terminalA)
            let _, effects =
                ClientModel.update
                    (EventsPageMsg
                        { Events = [ at 3L 2.0 (SessionEvent.TerminalClosed { TerminalId = terminalA; Reason = "closed by a peer"; By = None }) ]
                          LastOffset = Some (EventOffset.create 3L |> expect)
                          IsEnd = true })
                    pressed
            Expect.equal effects [ ClientEffect.Move (DomMove.OnArrival (DomMove.FocusTab terminalB)) ] "onto the tab beside it"

        testCase "a terminal somebody else ends moves no focus" <| fun () ->
            let rows = [ at 1L 0.0 (opened terminalA "build"); at 2L 1.0 (opened terminalB "test") ]
            let listing = clientOf rows |> Support.step ToggleSwitcherMsg
            let _, effects =
                ClientModel.update
                    (EventsPageMsg
                        { Events = [ at 3L 2.0 (SessionEvent.TerminalClosed { TerminalId = terminalA; Reason = "closed by a peer"; By = None }) ]
                          LastOffset = Some (EventOffset.create 3L |> expect)
                          IsEnd = true })
                    listing
            Expect.equal effects [] "no press here, so no move"

        // The terminal on screen changing what it offers a keyboard swaps the control under the
        // hand for another, and focus on the one that went falls to `body` — Hand it back,
        // removed by the release it asked for, did exactly that. It lands where the pane
        // lands, if it was dropped. Each case below is a client that has read the log through
        // (`heardOf`), so what arrives next is news.
        testCase "a terminal handed back lands a dropped keyboard on its command line" <| fun () ->
            let live =
                heardOf [ at 1L 0.0 (opened terminalA "build"); at 2L 1.0 (took terminalA (PeerRef ada) 0) ]
                |> Support.step (ShowInPaneMsg (Reading terminalA))
            let _, effects =
                ClientModel.update
                    (EventsPageMsg
                        { Events = [ at 3L 2.0 (released terminalA (PeerRef ada) LeaseReleased 4) ]
                          LastOffset = Some (EventOffset.create 3L |> expect)
                          IsEnd = true })
                    live
            Expect.equal effects [ ClientEffect.Move (DomMove.IfDropped (DomMove.FocusCommandLine terminalA)) ] "onto the command line"

        testCase "a shell dying under the reader lands a dropped keyboard on what its pane offers" <| fun () ->
            let reading =
                heardOf [ at 1L 0.0 (opened terminalA "build") ] |> Support.step (ShowInPaneMsg (Reading terminalA))
            let _, effects =
                ClientModel.update
                    (EventsPageMsg
                        { Events = [ at 2L 1.0 (SessionEvent.TerminalClosed { TerminalId = terminalA; Reason = "exited"; By = None }) ]
                          LastOffset = Some (EventOffset.create 2L |> expect)
                          IsEnd = true })
                    reading
            Expect.equal effects [ ClientEffect.Move (DomMove.IfDropped (DomMove.FocusCommandLine terminalA)) ] "where the pane still is"

        testCase "a lease taken by somebody else lands a dropped keyboard where the pane lands" <| fun () ->
            // The command line goes, for their bar: there is nothing to type into, and the
            // pane's own landing says what is left.
            let reading =
                heardOf [ at 1L 0.0 (opened terminalA "build") ] |> Support.step (ShowInPaneMsg (Reading terminalA))
            let _, effects =
                ClientModel.update
                    (EventsPageMsg
                        { Events = [ at 2L 1.0 (took terminalA (PeerRef bob) 0) ]
                          LastOffset = Some (EventOffset.create 2L |> expect)
                          IsEnd = true })
                    reading
            Expect.equal effects [ ClientEffect.Move (DomMove.IfDropped (DomMove.FocusCommandLine terminalA)) ] "no stranding"

        testCase "a lease of mine arriving is not this move's: the live screen takes it" <| fun () ->
            // `Screens.Sync` focuses the live screen once there is a screen to focus.
            let reading =
                heardOf [ at 1L 0.0 (opened terminalA "build") ] |> Support.step (ShowInPaneMsg (Reading terminalA))
            let _, effects =
                ClientModel.update
                    (EventsPageMsg
                        { Events = [ at 2L 1.0 (took terminalA (PeerRef ada) 0) ]
                          LastOffset = Some (EventOffset.create 2L |> expect)
                          IsEnd = true })
                    reading
            Expect.equal effects [] "nothing from the fold"

        testCase "a terminal changing under a preview moves nothing" <| fun () ->
            // The preview covers it; nothing under the hand went.
            let previewing = heardOf oneBlock |> Support.step (chip terminalA "1")
            let _, effects =
                ClientModel.update
                    (EventsPageMsg
                        { Events = [ at 4L 3.0 (SessionEvent.TerminalClosed { TerminalId = terminalA; Reason = "exited"; By = None }) ]
                          LastOffset = Some (EventOffset.create 4L |> expect)
                          IsEnd = true })
                    previewing
            Expect.equal effects [] "the preview is still there"

        testCase "a terminal's face changing in a log being replayed moves nothing" <| fun () ->
            // On load, focus rests on `body` because the page has just loaded, not because a
            // control went; a lease that ended an hour ago is history, not a swap.
            let replayed =
                ClientModel.init { PeerId = ada; DisplayName = "swift-heron" }
                |> Support.step HistoryReadMsg
                |> Support.step (ConnectedMsg { SessionId = sessionId; AssignedDisplayName = "swift-heron"; LatestOffset = Some (EventOffset.create 3L |> expect) })
                |> withPage [ at 1L 0.0 (opened terminalA "build"); at 2L 1.0 (took terminalA (PeerRef ada) 0) ]
            let _, effects =
                ClientModel.update
                    (EventsPageMsg
                        { Events = [ at 3L 2.0 (released terminalA (PeerRef ada) LeaseReleased 4) ]
                          LastOffset = Some (EventOffset.create 3L |> expect)
                          IsEnd = true })
                    replayed
            // Only the moves: the same page may anchor the launch card, which asks for its
            // listing, and that is not what this is about.
            let moves = effects |> List.filter (function ClientEffect.Move _ -> true | _ -> false)
            Expect.equal moves [] "history is not a swap"

        testCase "a shut pane is inert from the first paint" <| fun () ->
            // Zero pixels wide on a desktop, off the screen on a phone — and, without this,
            // every control in it a Tab stop. The served page carries it, so it holds before
            // the bundle has run.
            let aside (model: ClientModel) =
                System.Text.RegularExpressions.Regex.Match(Support.render model, "<aside[^>]*data-content-panel[^>]*>").Value
            let shut = clientOf [ at 1L 0.0 (opened terminalA "build") ]
            Expect.stringContains (aside shut) " inert" "shut, and out of reach"
            let shown = shut |> Support.step ToggleContentMsg
            Expect.isFalse ((aside shown).Contains " inert") "shown, and reachable"

        testCase "every tab names the panel, and the panel names the tab that is showing" <| fun () ->
            // `aria-controls` and `aria-labelledby` are id references, so what is asserted is
            // that each reference RESOLVES to the element it promises — not what the ids are.
            let html =
                clientOf [ at 1L 0.0 (opened terminalA "build"); at 2L 1.0 (opened terminalB "logs") ]
                |> Support.step (ShowInPaneMsg (Reading terminalB))
                |> Support.render
            let attr (name: string) (element: string) =
                System.Text.RegularExpressions.Regex.Match(element, sprintf " %s=\"([^\"]*)\"" name).Groups.[1].Value
            let panel = System.Text.RegularExpressions.Regex.Match(html, "<div[^>]*role=\"tabpanel\"[^>]*>").Value
            let tabs =
                System.Text.RegularExpressions.Regex.Matches(html, "<div[^>]*role=\"tab\"[^>]*>")
                |> Seq.map (fun m -> m.Value)
                |> List.ofSeq
            Expect.isTrue (tabs.Length > 1) "a strip of more than one tab"
            for tab in tabs do
                Expect.equal (attr "aria-controls" tab) (attr "id" panel) "the tab names the panel"
            let selected = tabs |> List.filter (fun tab -> tab.Contains "aria-selected=\"true\"")
            Expect.equal
                (selected |> List.map (attr "id"))
                [ attr "aria-labelledby" panel ]
                "the panel is labelled by the one selected tab"

        testCase "two tab keys never share an id" <| fun () ->
            // Keys carry `:` and `/`; an id spells them out, and the spelling must not let two
            // keys collide or leave a character a selector would have to escape.
            let keys = [ "a:b"; "a/b"; "a_b"; "a_003ab"; "a_003a"; "block:t:b-1"; "content:artifacts/x.png/0001" ]
            let ids = keys |> List.map Dom.paneTabId
            Expect.equal (List.distinct ids).Length keys.Length "one id per key"
            for id in ids do
                Expect.isTrue (System.Text.RegularExpressions.Regex.IsMatch (id, "^[A-Za-z0-9_-]+$")) (sprintf "%s needs no escaping" id)

        testCase "show in terminal scrolls the history to the command and focuses the pane" <| fun () ->
            let _, effects = ClientModel.update (ShowInTerminalMsg (terminalA, block "1")) (clientOf [ at 1L 0.0 (opened terminalA "build") ])
            Expect.equal
                effects
                [ ClientEffect.Move (DomMove.RevealBlock (terminalA, block "1")); ClientEffect.Move DomMove.FocusPane ]
                "scrolled to, then focused, in that order"

        testCase "a terminal I did not ask for leaves my pane where it is" <| fun () ->
            // The reason this is a REQUEST and not "any terminal that is mine": under a
            // verified login the log cannot tell my tabs apart, so the phone in my pocket
            // opening one would otherwise yank the pane I am working in.
            let model =
                clientOf [ at 1L 0.0 (opened terminalA "build") ]
                |> Support.step (ShowInPaneMsg (Reading terminalA))
                |> withPage [ at 2L 1.0 (opened terminalB "somewhere else") ]
            Expect.equal (ClientModel.selectedTerminal model) (Some terminalA) "still where I was"

        testCase "the agent opening one never moves my pane" <| fun () ->
            // Even mid-press. What a press is owed is the terminal it asked for, and the
            // agent's is not that one.
            let model =
                clientOf [ at 1L 0.0 (opened terminalA "build") ]
                |> Support.step (ShowInPaneMsg (Reading terminalA))
                |> Support.step (OpenTerminalMsg ("terminal", SandboxRef.defaultRef))
                |> withPage [ at 2L 1.0 (openedBy ActorRef.Agent terminalB "running the tests") ]
            Expect.equal (ClientModel.selectedTerminal model) (Some terminalA) "the agent's terminal is not what I pressed for"

        testCase "one press is spent once" <| fun () ->
            // The press is consumed by the terminal that answers it, so the NEXT one to
            // arrive — the agent's, a collaborator's, my own from another tab — finds nothing
            // owed and leaves the pane alone.
            let model =
                clientOf [ at 1L 0.0 (opened terminalA "build") ]
                |> Support.step (ShowInPaneMsg (Reading terminalA))
                |> Support.step (OpenTerminalMsg ("terminal", SandboxRef.defaultRef))
                |> withPage [ at 2L 1.0 (opened terminalB "the one I asked for") ]
                |> withPage [ at 3L 2.0 (opened terminalC "one I did not") ]
            Expect.equal (ClientModel.selectedTerminal model) (Some terminalB) "still the one the press bought"

        // The other deployment, asked of the press rather than of a lease: under a verified
        // login the session stamps what this connection opens with the USER it was attributed
        // to, and the fold has to know that user as itself — or the press is owed a terminal
        // that never arrives, and the pane sits on whatever it was showing.
        testCase "a terminal I pressed for is in the strip when the log says my user opened it" <| fun () ->
            let model =
                clientOf [ at 1L 0.0 (attributed ada) ]
                |> Support.step (OpenTerminalMsg ("", SandboxRef.defaultRef))
                |> withPage [ at 2L 1.0 (openedBy (UserRef nick) terminalA "terminal") ]
            Expect.equal (stripKeys model) [ "terminal:term-a" ] "the press bought a tab"

        // Reported from a live session: the strip read `[TERMINAL ×]`, a chip was tapped, and
        // it read `[LS /ETC | HEAD -5]` — the terminal gone, reachable again only through the
        // list. A chip's command is a preview over its terminal now, never a tab beside it.
        testCase "a chip opened after a terminal leaves the strip as it was" <| fun () ->
            let model =
                clientOf []
                |> Support.step (OpenTerminalMsg ("", SandboxRef.defaultRef))
                |> withPage oneBlock
                |> Support.step (chip terminalA "1")
            Expect.equal (stripKeys model) [ "terminal:term-a" ] "the terminal, and only the terminal"

        testCase "pressing + three times opens three tabs" <| fun () ->
            let press = Support.step (OpenTerminalMsg ("", SandboxRef.defaultRef))
            let model =
                clientOf []
                |> press
                |> withPage [ at 1L 0.0 (opened terminalA "one") ]
                |> press
                |> withPage [ at 2L 1.0 (opened terminalB "two") ]
                |> press
                |> withPage [ at 3L 2.0 (opened terminalC "three") ]
            Expect.equal
                (stripKeys model)
                [ "terminal:term-a"; "terminal:term-b"; "terminal:term-c" ]
                "one tab a press"

        // The half of the old default that drew a tab for what this client never opened.
        // An agent-only session shows the empty pane and its New terminal; the agent's
        // terminal is one row away, in the list.
        testCase "a terminal I never opened is not on my screen" <| fun () ->
            let model = clientOf [ at 1L 0.0 (openedBy ActorRef.Agent terminalA "running the tests") ]
            Expect.equal (ClientModel.selectedTerminal model) None "nothing shown, because nothing opened"

        // The terminal I am watching ends: it stays where I am looking, as its recording —
        // never swapped for something else under my eyes.
        testCase "the terminal I am watching stays on screen when it ends" <| fun () ->
            let model =
                clientOf [ at 1L 0.0 (opened terminalA "build"); at 2L 1.0 (opened terminalB "logs") ]
                |> Support.step (ShowInPaneMsg (Reading terminalA))
                |> withPage [ at 3L 2.0 (closedNow terminalA) ]
            Expect.equal (ClientModel.selectedTerminal model) (Some terminalA) "still the one on screen"

        // "A recording opened from the list stays in the strip until it is closed" was here.
        // A recording opened from the list is a closed terminal like any other: it stays while
        // it is on screen and leaves when the reader chooses another (above), and the list is
        // where it is opened again.

        // The strip's whole contract, asked of every history of the acts that move a pane,
        // exhaustively to a small depth, and after every step: it holds terminals the session
        // has, each still running or the one on screen, each once — and a preview, which is
        // what used to break it, can be up through all of it without ever being one of them.
        testCase "the strip holds only terminals" <| fun () ->
            let chart = Content.ContentRef.create "artifacts/chart.png/0000-ab12cd" |> expect
            let ids = [ terminalA; terminalB; terminalC ]
            // State beside the model: the next offset, and how many terminals have opened, so
            // each step can name a real one.
            let steps : (string * (ClientModel * int64 * int -> ClientModel * int64 * int)) list =
                let page (events: SessionEvent list) (model, next, count) =
                    let envelopes = events |> List.mapi (fun i e -> at (next + int64 i) (float next) e)
                    withPage envelopes model, next + int64 (List.length events), count
                let latest count = ids.[max 0 (min (count - 1) 2)]
                [ "press", (fun (m, n, c) -> Support.step (OpenTerminalMsg ("", SandboxRef.defaultRef)) m, n, c)
                  "mine arrives", (fun (m, n, c) ->
                      if c >= 3 then m, n, c
                      else
                          let m', n', _ = page [ opened ids.[c] "mine"; started ids.[c] "1" byAda "ls" 1 ] (m, n, c)
                          m', n', c + 1)
                  "the agent's arrives", (fun (m, n, c) ->
                      if c >= 3 then m, n, c
                      else
                          let m', n', _ = page [ openedBy ActorRef.Agent ids.[c] "theirs" ] (m, n, c)
                          m', n', c + 1)
                  "latest ends", (fun (m, n, c) -> if c = 0 then m, n, c else page [ closedNow (latest c) ] (m, n, c))
                  "chip", (fun (m, n, c) -> Support.step (chip (latest c) "1") m, n, c)
                  "back", (fun (m, n, c) -> Support.step ClosePreviewMsg m, n, c)
                  "switcher row", (fun (m, n, c) -> Support.step (OpenInPaneMsg (Reading terminalA)) m, n, c)
                  "rewind", (fun (m, n, c) -> Support.step (RewindTerminalMsg (latest c)) m, n, c)
                  "the agent opens the latest", (fun s ->
                      let _, _, c = s
                      page [ SessionEvent.TabOpened { TabOpened.Ref = ViewingTerminal (latest c); TabOpened.Focus = false } ] s)
                  "the agent shows a file", (fun s ->
                      page [ SessionEvent.TabOpened { TabOpened.Ref = ViewingFile chart; TabOpened.Focus = true } ] s)
                  "the agent takes it back", (fun s -> page [ SessionEvent.TabClosed { TabClosed.Ref = ViewingFile chart } ] s)
                  "switcher", (fun (m, n, c) -> Support.step ToggleSwitcherMsg m, n, c) ]
            let rec walk (depth: int) (path: string list) (state: ClientModel * int64 * int) =
                let model, _, _ = state
                let selected = ClientModel.selectedTerminal model
                let fault =
                    if List.distinct model.Tabs <> model.Tabs then Some "holds a terminal twice"
                    else
                        model.Tabs
                        |> List.tryPick (fun terminal ->
                            match Projection.tryFind terminal model.Terminals with
                            | None -> Some (sprintf "holds %s, which the session does not have" (TerminalId.value terminal))
                            | Some view when not view.IsOpen && selected <> Some terminal ->
                                Some (sprintf "holds %s, closed and not on screen" (TerminalId.value terminal))
                            | Some _ -> None)
                match fault with
                | Some said ->
                    failwithf "after [%s] the strip [%s] %s" (String.Join ("; ", List.rev path)) (String.Join ("; ", stripKeys model)) said
                | None -> ()
                if depth > 0 then
                    for name, step in steps do
                        walk (depth - 1) (name :: path) (step state)
            walk 4 [] (clientOf [ at 1L 0.0 (sent "1" "hello") ], 2L, 0)

        testCase "unattributed, I am still my peer" <| fun () ->
            // `--auth localhost` verifies nobody, so the log says `PeerRef` and the answer has
            // to follow it. One rule reading the log, rather than a deployment-shaped branch.
            let model = clientOf [ at 1L 0.0 (opened terminalA "mine") ]
            Expect.isTrue (ClientModel.isMine (PeerRef ada) model) "my peer is me"
            Expect.isFalse (ClientModel.isMine (UserRef nick) model) "a user nothing attributed to me is not"

        // "typing in a terminal does not keep it", "keeping something previewed opens it as a
        // tab", "pinning what is previewed keeps it…", and the two unpinning cases were here.
        // A pin does not exist: a terminal in the strip is kept while it runs, and a preview
        // is one glance that the next replaces. "Reading one recording after another leaves a
        // tab each" is now its opposite — six chips leave one preview (`paneTests`).
    ]

// --- Task cards (Plan 20, stage 4) --------------------------------------------------------

let private turnStarted (t: string) =
    AgentTurnStarted { AgentTurnId = turn t; Cause = TurnCause.TriggeredBy (message "1") }

let private rejected (id: TerminalId) (n: string) (authority: Authority) (command: string) =
    SessionEvent.TerminalCommandRejected
        { TerminalId = id
          QueueId = QueueId.create ("q-" + n) |> expect
          BlockId = block n
          Authority = authority
          RejectedBy = PeerRef ada
          Command = command
          Reason = Some "not that one" }

let private cardTests =
    testList "Task cards (Plan 20, stage 4)" [

        testCase "consecutive commands from one turn are one card" <| fun () ->
            // An agent working across several of its terminals is the second item a single
            // turn can emit a dozen of, after tool use — and it costs one row, not twelve.
            let events =
                [ at 1L 0.0 (turnStarted "a")
                  at 2L 1.0 (opened terminalA "agent 1")
                  at 3L 2.0 (opened terminalB "agent 2")
                  at 4L 3.0 (started terminalA "1" agentForAda "make" 1)
                  at 5L 4.0 (started terminalB "2" agentForAda "npm test" 1)
                  at 6L 5.0 (started terminalA "3" agentForAda "git status" 40) ]
            Expect.equal (drawn events) [ "card:turn-a:3" ] "three commands, one card"

        testCase "a card forms on the SECOND command, never the first" <| fun () ->
            // A disclosure around one chip hides the only thing the row has to say behind a
            // click, and buys nothing back.
            let events =
                [ at 1L 0.0 (turnStarted "a")
                  at 2L 1.0 (opened terminalA "agent 1")
                  at 3L 2.0 (started terminalA "1" agentForAda "make" 1) ]
            Expect.equal (drawn events) [ "ran:b-1" ] "one command is a chip"

        testCase "a message between two commands splits the card" <| fun () ->
            // The same boundary a tool run stops at, for the same reason: swallowing what was
            // said in the middle would tell a reader the wrong story about the order.
            let events =
                [ at 1L 0.0 (turnStarted "a")
                  at 2L 1.0 (opened terminalA "agent 1")
                  at 3L 2.0 (started terminalA "1" agentForAda "make" 1)
                  at 4L 3.0 (started terminalA "2" agentForAda "npm test" 40)
                  at 5L 4.0 (sent "1" "how's it going?")
                  at 6L 5.0 (started terminalA "3" agentForAda "git status" 80)
                  at 7L 6.0 (started terminalA "4" agentForAda "git diff" 120) ]
            Expect.equal
                (drawn events)
                [ "card:turn-a:2"; "said:m-1"; "card:turn-a:2" ]
                "two cards, and the message stays between them"

        testCase "commands from two turns never share a card" <| fun () ->
            // A burst is one turn's work. Two turns' commands adjacent in the log are two
            // pieces of work that happened to touch, and a card saying otherwise invents a
            // task nobody ran.
            let events =
                [ at 1L 0.0 (turnStarted "a")
                  at 2L 1.0 (opened terminalA "agent 1")
                  at 3L 2.0 (started terminalA "1" agentForAda "make" 1)
                  at 4L 3.0 (started terminalA "2" agentForAda "npm test" 40)
                  at 5L 4.0 (turnStarted "b")
                  at 6L 5.0 (started terminalA "3" agentForAda "git status" 80)
                  at 7L 6.0 (started terminalA "4" agentForAda "git diff" 120) ]
            Expect.equal (drawn events) [ "card:turn-a:2"; "card:turn-b:2" ] "one card per turn"

        testCase "a person's commands never group, even during a turn" <| fun () ->
            // Grouping is for work nobody is hand-driving. The clock says a command happened
            // DURING a turn; only the authority says whose it was, and a person typing while
            // the agent works is still a person typing.
            let events =
                [ at 1L 0.0 (turnStarted "a")
                  at 2L 1.0 (opened terminalA "build")
                  at 3L 2.0 (started terminalA "1" byAda "make" 1)
                  at 4L 3.0 (started terminalA "2" byAda "npm test" 40) ]
            Expect.equal (drawn events) [ "ran:b-1"; "ran:b-2" ] "two chips, no card"

        testCase "a command nobody's turn started never groups" <| fun () ->
            // A block the Session ran on its own behalf, before any turn: no turn to
            // attribute it to, and inventing one would be a task nobody asked for.
            let events =
                [ at 1L 0.0 (opened terminalA "boot")
                  at 2L 1.0 (started terminalA "1" agentForAda "make" 1)
                  at 3L 2.0 (started terminalA "2" agentForAda "npm test" 40) ]
            Expect.equal (drawn events) [ "ran:b-1"; "ran:b-2" ] "two chips, no card"

        testCase "a refused command joins the card of the turn that proposed it" <| fun () ->
            // The refusal is the more interesting half of the pair, and a card that left it
            // out would say the turn ran fewer commands than it asked to.
            let events =
                [ at 1L 0.0 (turnStarted "a")
                  at 2L 1.0 (opened terminalA "agent 1")
                  at 3L 2.0 (started terminalA "1" agentForAda "make" 1)
                  at 4L 3.0 (rejected terminalA "2" agentForAda "rm -rf /") ]
            Expect.equal (drawn events) [ "card:turn-a:2" ] "the proposal counts, whether or not it ran"

        testCase "a card anchors where its FIRST command started" <| fun () ->
            // The chip's anchoring rule, unchanged: a burst that takes four minutes stays
            // above the messages sent while it ran rather than jumping to the bottom.
            let events =
                [ at 1L 0.0 (turnStarted "a")
                  at 2L 1.0 (opened terminalA "agent 1")
                  at 3L 2.0 (started terminalA "1" agentForAda "make" 1)
                  at 4L 3.0 (started terminalA "2" agentForAda "npm test" 40)
                  at 5L 4.0 (sent "1" "how's it going?")
                  at 6L 9.0 (completed terminalA "1" (CommandSucceeded 0) 40) ]
            let conversation, _ = ConversationProjection.applyEvents None events ConversationProjection.empty
            let timeline, _ = TimelineProjection.applyEvents None events TimelineProjection.empty
            match TimelineProjection.rows conversation timeline with
            | (RowTaskCard _ as card) :: _ ->
                Expect.equal (EventOffset.value (TimelineRow.offset card)) 3L "the offset of the first command"
            | other -> failwithf "expected the card first, got %A" other

        // The other half of "in the order, off the screen". Its pair sits in `The merged
        // order`, where the fold is; this is about what is DRAWN, which is a different rule
        // in a different function and goes red for a different reason.
        testCase "what the model reasoned is not drawn" <| fun () ->
            Expect.equal
                (drawn
                    [ at 1L 0.0 (sent "1" "run the tests")
                      at 2L 1.0 (
                          SessionEvent.AgentThought
                              { AgentTurnId = turn "t1"; Thought = "dev, not gate" }) ])
                [ "said:m-1" ]
                "it was never said to anyone, so a screen does not say it"

        testCase "a card carries NO status of its own — the row is the same either way" <| fun () ->
            // What makes a card's lines mutate in place for free, exactly as chips do: the
            // row holds which blocks and where, `Projection` holds what they say.
            let running =
                [ at 1L 0.0 (turnStarted "a")
                  at 2L 1.0 (opened terminalA "agent 1")
                  at 3L 2.0 (started terminalA "1" agentForAda "make" 1)
                  at 4L 3.0 (started terminalA "2" agentForAda "npm test" 40) ]
            let finished = running @ [ at 5L 9.0 (completed terminalA "1" (CommandFailed 2) 40) ]
            let rowsOf events =
                let conversation, _ = ConversationProjection.applyEvents None events ConversationProjection.empty
                let timeline, _ = TimelineProjection.applyEvents None events TimelineProjection.empty
                TimelineProjection.rows conversation timeline
            Expect.equal (rowsOf running) (rowsOf finished) "the card does not move or change as its work does"
    ]

let private tallyTests =
    testList "What a task card counts (Plan 20, stage 4)" [

        testCase "a refusal counts as a failure" <| fun () ->
            // Red on every other surface for the same reason: a command the agent proposed
            // and did not get to run is what a person scanning for trouble is scanning for.
            Expect.equal (TaskCard.stateOf (BlockRejected (PeerRef ada, Some "no"))) TaskFailed "refused reads as failed"

        testCase "a non-zero exit and a timeout are one bucket" <| fun () ->
            // The exact code is on the line and in the block behind it. A summary that
            // counted `exit 2` apart from `timed out` would be longer and say less.
            Expect.equal (TaskCard.stateOf (BlockFinished (CommandFailed 2))) TaskFailed "exit 2 failed"
            Expect.equal (TaskCard.stateOf (BlockFinished CommandTimedOut)) TaskFailed "so did the timeout"

        testCase "the summary counts every command once" <| fun () ->
            let states = [ TaskDone; TaskFailed; TaskDone; TaskRunning; TaskDone ]
            Expect.equal
                (TaskCard.tally states)
                { Commands = 5; Failed = 1; Running = 1; Done = 3 }
                "5 commands, 3 done, 1 failed, 1 running"

        testCase "failures sort first, then what is still going" <| fun () ->
            // A burst of twenty commands with one failure buried at line fourteen makes a
            // person hunt for the one thing the card exists to show them.
            let lines = [ "a", TaskDone; "b", TaskRunning; "c", TaskFailed; "d", TaskDone ]
            Expect.equal
                (TaskCard.ordered lines |> List.map fst)
                [ "c"; "b"; "a"; "d" ]
                "failed, running, then done"

        testCase "lines keep their order WITHIN a group" <| fun () ->
            // So the only thing a finishing command changes is which group it is in: a line
            // never jumps a place inside one, which is what lets the card be read twice.
            let lines = [ "a", TaskFailed; "b", TaskFailed; "c", TaskFailed ]
            Expect.equal (TaskCard.ordered lines |> List.map fst) [ "a"; "b"; "c" ] "chronological within the group"
    ]

// The reply ref, rendered — the projection decides whether one is due (Agent.fs pins that);
// here the rendered page is what settles that a due ref actually reaches the screen quoting
// its cause, and that an undue one draws nothing. Only a render can see this: the markup of a
// message with `CausedBy = None` and one whose ref quote silently failed to resolve read the
// same everywhere the DATA is checked.
let private replyRefRenderTests =
    let conversation (trigger: string) (triggerBody: string) (interleaved: (string * string) list) =
        let turnId = turn "reply"
        let agentMsg = message "agent"
        let mutable n = 0L
        let stamp e = n <- n + 1L; at n (float n) e
        [ yield stamp (sent trigger triggerBody)
          for id, body in interleaved -> stamp (sent id body)
          yield stamp (AgentTurnStarted { AgentTurnId = turnId; Cause = TurnCause.TriggeredBy (message trigger) })
          yield stamp (AgentMessageStarted { AgentTurnId = turnId; MessageId = agentMsg; Antecedent = None })
          yield stamp (AgentMessageDelta { AgentTurnId = turnId; MessageId = agentMsg; Delta = "on it" })
          yield stamp (AgentMessageCompleted { AgentTurnId = turnId; MessageId = agentMsg; Body = "on it" }) ]

    testList "The reply ref, rendered" [
        testCase "a detached agent reply draws a ref that resolves to what it answers" <| fun () ->
            let html = Support.render (clientOf (conversation "1" "please rebase onto master" [ "2", "and squash the fixups" ]))
            Expect.isTrue
                (html.Contains (Dom.attr Dom.Hooks.replyRef (MessageId.value (message "1"))))
                "the ref points at the message the turn answered"
            // Resolved, not the past-the-page fallback — the parent's own bubble also carries
            // its text, so absence of the fallback is what proves the QUOTE resolved.
            Expect.isFalse (html.Contains Dom.Text.replyRefMissing) "the quote resolved to the real message, not the fallback"

        testCase "an adjacent agent reply draws no ref" <| fun () ->
            let html = Support.render (clientOf (conversation "1" "please rebase onto master" []))
            Expect.isFalse (html.Contains Dom.Hooks.replyRef) "a reply under its cause says nothing the eye cannot see"

        // The jump's two halves that only the markup settles: the ref whose source is loaded is
        // a live control, and the article it will land on is focusable so the cursor can.
        testCase "a ref whose source is loaded is a jump control, and its target can hold focus" <| fun () ->
            let html = Support.render (clientOf (conversation "1" "please rebase onto master" [ "2", "and squash the fixups" ]))
            Expect.isTrue (html.Contains Dom.Hooks.replyJump) "the ref is the jump control, not an inert line"
            // The reveal lands the cursor on the target article, which a plain <article> cannot
            // hold — so every message is `tabindex=-1`, focusable on purpose and never a Tab stop.
            Expect.isTrue (html.Contains "tabindex=\"-1\"") "the message a jump lands on can take focus"

        // The other side of the model-membership gate: a ref whose source has paged out of the
        // loaded conversation is inert — it still SAYS what it answered, but offers no jump to a
        // message that is not there to jump to.
        testCase "a ref whose source is not loaded is inert, not a dead jump" <| fun () ->
            let turnId = turn "reply"
            let agentMsg = message "agent"
            // The turn was triggered by `m-gone`, but that message is not in this page; another
            // message sits last, so the reply is detached and the ref is due — yet unresolvable.
            let events =
                [ at 1L 0.0 (sent "here" "a line that is loaded")
                  at 2L 1.0 (AgentTurnStarted { AgentTurnId = turnId; Cause = TurnCause.TriggeredBy (message "gone") })
                  at 3L 2.0 (AgentMessageStarted { AgentTurnId = turnId; MessageId = agentMsg; Antecedent = None })
                  at 4L 3.0 (AgentMessageDelta { AgentTurnId = turnId; MessageId = agentMsg; Delta = "on it" })
                  at 5L 4.0 (AgentMessageCompleted { AgentTurnId = turnId; MessageId = agentMsg; Body = "on it" }) ]
            let html = Support.render (clientOf events)
            Expect.isTrue
                (html.Contains (Dom.attr Dom.Hooks.replyRef (MessageId.value (message "gone"))))
                "the ref still names what it answered"
            Expect.isFalse (html.Contains Dom.Hooks.replyJump) "but it offers no jump to a message that is not loaded"
            Expect.isTrue (html.Contains Dom.Text.replyRefMissing) "and says so, standing where the quote would be"
    ]

// A sandbox coming up (Plan 15+): the act that used to appear only once it was already over
// now opens RUNNING and resolves in place — the seam that makes an act a task.
let private sandboxRef = SandboxRef.parse "octo/hello:dev" |> expect
let private repoActor = ActorRef.Configured (RepoRef.create "octo/hello" |> expect)

let private starting (n: string) =
    SessionEvent.WorkSandboxStarting
        { MessageId = message n; Sandbox = sandboxRef; Backend = "docker"; Description = Some "day-to-day work"; Actor = repoActor; OnBehalfOf = None; CausedBy = None }

let private startedSandbox (n: string) =
    SessionEvent.WorkSandboxStarted
        { MessageId = message n
          Sandbox = sandboxRef
          Backend = "docker"
          Description = Some "day-to-day work"
          Checkout = Some "/repos/octo/hello"
          Forwarded = []
          Realisation = []
          Actor = repoActor; OnBehalfOf = None; CausedBy = None }

let private startFailed (n: string) (reason: string) =
    SessionEvent.WorkSandboxStartFailed { MessageId = message n; Sandbox = sandboxRef; Reason = reason; Actor = repoActor; OnBehalfOf = None; CausedBy = None }

let private conversationOf events =
    (ConversationProjection.applyEvents None events ConversationProjection.empty |> fst).Items

let private noBlocks : BlockId -> BlockStatus option = fun _ -> None

/// Every timeline item's live task state, in order — the shape a queue would read.
let private liveTasks (blockStatus: BlockId -> BlockStatus option) events =
    let conversation, _ = ConversationProjection.applyEvents None events ConversationProjection.empty
    let timeline, _ = TimelineProjection.applyEvents None events TimelineProjection.empty
    TimelineProjection.items conversation timeline
    |> List.choose (TimelineProjection.taskState blockStatus timeline)

let private sandboxTaskTests =
    testList "a sandbox coming up is a running task" [
        testCase "starting opens a running act" <| fun () ->
            match conversationOf [ at 1L 0.0 (starting "1") ] with
            | [ item ] ->
                Expect.equal item.Status ConversationItemStatus.Running "the act is running while the sandbox comes up"
                Expect.isTrue ((ConversationItem.headline item).StartsWith "starting sandbox") "and says the sandbox is starting"
                match item.Content with
                | ItemContent.Act _ -> ()
                | ItemContent.Message _
                | ItemContent.Stopped _ -> failwith "a sandbox coming up is an act, not a message"
            | other -> failwithf "expected one running act, got %d items" (List.length other)

        testCase "the start resolves that same item in place, not a second" <| fun () ->
            match conversationOf [ at 1L 0.0 (starting "1"); at 2L 1.0 (startedSandbox "1") ] with
            | [ item ] ->
                Expect.equal item.Status ConversationItemStatus.Complete "the running act became complete"
                Expect.isTrue ((ConversationItem.headline item).StartsWith "started sandbox") "and reads as started, not starting"
            | other -> failwithf "the start must resolve the running act, not add a second — got %d items" (List.length other)

        testCase "a failure resolves that same item to failed, carrying why" <| fun () ->
            match conversationOf [ at 1L 0.0 (starting "1"); at 2L 1.0 (startFailed "1" "the docker daemon is not reachable") ] with
            | [ item ] ->
                Expect.equal item.Status ConversationItemStatus.Failed "the running act became failed"
                match item.Content with
                | ItemContent.Act act ->
                    Expect.equal
                        (Act.particulars act |> List.map Phrase.said)
                        [ "the docker daemon is not reachable" ]
                        "and carries why it could not start"
                | ItemContent.Message _
                | ItemContent.Stopped _ -> failwith "still an act"
            | other -> failwithf "the failure must resolve the running act, not add a second — got %d items" (List.length other)

        testCase "a start with no preceding starting still appears (a log written before starting existed)" <| fun () ->
            match conversationOf [ at 1L 0.0 (startedSandbox "1") ] with
            | [ item ] -> Expect.equal item.Status ConversationItemStatus.Complete "an unpaired start is a complete act, exactly as before"
            | other -> failwithf "expected one complete act, got %d items" (List.length other)

        testCase "a running sandbox act is a running task; once started it has left the queue" <| fun () ->
            Expect.equal (liveTasks noBlocks [ at 1L 0.0 (starting "1") ]) [ TaskRunning ] "a sandbox coming up is one running task"
            Expect.equal (liveTasks noBlocks [ at 1L 0.0 (starting "1"); at 2L 1.0 (startedSandbox "1") ]) [] "a started sandbox is done, not a queue member"
            Expect.equal (liveTasks noBlocks [ at 1L 0.0 (starting "1"); at 2L 1.0 (startFailed "1" "nope") ]) [ TaskFailed ] "a failed start still wants attention"

        testCase "what someone SAID is never a task" <| fun () ->
            Expect.equal (liveTasks noBlocks [ at 1L 0.0 (sent "1" "hello") ]) [] "a message is not work the session is doing"

        testCase "a running block is a running task; a finished one has left the queue" <| fun () ->
            let stateOf events =
                let blocks = events |> List.fold (fun p (e: EventEnvelope<SessionEvent>) -> Projection.applyEvent p e.Event) Projection.empty
                let blockStatus id =
                    Projection.tryFind terminalA blocks
                    |> Option.bind (fun t -> t.Blocks |> List.tryFind (fun b -> b.BlockId = id))
                    |> Option.map (fun b -> b.Status)
                liveTasks blockStatus events
            let running = [ at 1L 0.0 (opened terminalA "sh"); at 2L 1.0 (started terminalA "1" byAda "make" 0) ]
            Expect.equal (stateOf running) [ TaskRunning ] "a running command is running work"
            Expect.equal
                (stateOf (running @ [ at 3L 2.0 (completed terminalA "1" (CommandSucceeded 0) 4) ]))
                []
                "a finished command has left the queue"

        testCase "a running tool call is a running task; ok leaves, failed wants attention" <| fun () ->
            Expect.equal (liveTasks noBlocks [ at 1L 0.0 (used "1" "a" "set_secret") ]) [ TaskRunning ] "a call still running is running work"
            Expect.equal
                (liveTasks noBlocks [ at 1L 0.0 (used "1" "a" "set_secret"); at 2L 1.0 (toolDone "1" ToolCallOk None) ])
                []
                "a call that succeeded has left the queue"
            Expect.equal
                (liveTasks noBlocks [ at 1L 0.0 (used "1" "a" "set_secret"); at 2L 1.0 (toolDone "1" (ToolCallFailed "denied") None) ])
                [ TaskFailed ]
                "a call that failed still wants attention"

        // The visible half: a running act carries its state where a person (and a test) can
        // see it. The hook is asserted, not the design — a redesign may move the pulse, but a
        // running act must always SAY it is running, and a started one must stop saying so.
        testCase "a running sandbox act renders as running; the start clears it" <| fun () ->
            let running = Support.render (clientOf [ at 1L 0.0 (starting "1") ])
            Expect.isTrue (running.Contains "data-act-status=\"running\"") "the running act says it is running"
            let started = Support.render (clientOf [ at 1L 0.0 (starting "1"); at 2L 1.0 (startedSandbox "1") ])
            Expect.isFalse (started.Contains "data-act-status=\"running\"") "once started, nothing still says running"
            Expect.isTrue (started.Contains "data-act-status=\"complete\"") "the resolved act says it is complete"
    ]

// Why a repo's sandbox came up (`CausedBy`): the repo added, the session starting, a person
// connecting. The fold keeps a ref only where one is worth drawing; the screen draws it as a
// sentence with its references, and jumps to it when it is loaded.
let private hello = RepoRef.create "octo/hello" |> expect

let private repoAdded (n: string) (repo: string) =
    SessionEvent.RepoAdded
        { MessageId = message n; Repo = RepoRef.create repo |> expect; Branch = "main"; Actor = PeerRef ada; AgentsMd = None }

let private startingBecause (n: string) (cause: Cause) =
    match starting n with
    | SessionEvent.WorkSandboxStarting s -> SessionEvent.WorkSandboxStarting { s with CausedBy = Some cause }
    | other -> other

/// A start for `person`, with what caused it when anything did.
let private startingFor (n: string) (person: PeerId) (cause: Cause option) =
    match starting n with
    | SessionEvent.WorkSandboxStarting s ->
        SessionEvent.WorkSandboxStarting { s with OnBehalfOf = Some (Principal.Peer person); CausedBy = cause }
    | other -> other

/// Who the note for start `n` says it was for, as a screen that draws its cause says it.
let private forWhomOf (n: string) events =
    let items = conversationOf events
    items |> List.find (fun i -> i.MessageId = message n) |> ConversationItem.forWhom items |> Phrase.said

let private causeOf (n: string) events =
    conversationOf events
    |> List.tryFind (fun i -> i.MessageId = message n)
    |> Option.bind (fun i -> i.CausedBy)

/// How the note for act `n` draws its cause.
let private linkOf (n: string) events =
    ConversationItem.causeLinks (conversationOf events) |> Map.tryFind (message n)

/// The cause line of the note for sandbox start `n`, as rendered.
let private causeLineOf (n: string) (html: string) =
    let note = html.IndexOf (Dom.attr "data-message-id" (MessageId.value (message n)))
    let at = html.IndexOf ("data-cause-ref", note)
    Expect.isTrue (note >= 0 && at >= 0) "the note has a cause line"
    html.Substring (at, html.IndexOf ("</div>", at) - at)

let private sandboxCauseTests =
    testList "why a repo's sandbox came up" [
        testCase "an act directly under what caused it, by the same author, draws no ref" <| fun () ->
            let events = [ at 1L 0.0 (starting "a"); at 2L 1.0 (startingBecause "s" (Cause.Item (message "a"))) ]
            Expect.equal (linkOf "s" events) (Some CauseLink.Unlinked) "the cause is the item right above"

        testCase "an act directly under what caused it, by another author, draws its cause" <| fun () ->
            let events = [ at 1L 0.0 (repoAdded "a" "octo/hello"); at 2L 1.0 (startingBecause "s" (Cause.Item (message "a"))) ]
            Expect.equal (linkOf "s" events) (Some (CauseLink.Drawn (Cause.Item (message "a")))) "a header sits between them"

        testCase "a start pushed away from what caused it points back to it" <| fun () ->
            let events =
                [ at 1L 0.0 (repoAdded "a" "octo/hello")
                  at 2L 1.0 (repoAdded "b" "octo/other")
                  at 3L 2.0 (startingBecause "s" (Cause.Item (message "a"))) ]
            Expect.equal (linkOf "s" events) (Some (CauseLink.Drawn (Cause.Item (message "a")))) "the ref points at the add"

        testCase "a start the session brought up says so, whatever sits above it" <| fun () ->
            let events = [ at 1L 0.0 (repoAdded "a" "octo/hello"); at 2L 1.0 (startingBecause "s" Cause.Booted) ]
            Expect.equal (linkOf "s" events) (Some (CauseLink.Drawn Cause.Booted)) "the session starting is never on screen to sit under"

        testCase "a start keeps its cause once it has come up" <| fun () ->
            let events =
                [ at 1L 0.0 (repoAdded "a" "octo/hello")
                  at 2L 1.0 (repoAdded "b" "octo/other")
                  at 3L 2.0 (startingBecause "s" (Cause.Item (message "a")))
                  at 4L 3.0 (startedSandbox "s") ]
            Expect.equal (causeOf "s" events) (Some (Cause.Item (message "a"))) "the started act is the same item"

        testCase "a start under an act with the same cause continues its chain" <| fun () ->
            let events =
                [ at 1L 0.0 (repoAdded "a" "octo/hello")
                  at 2L 1.0 (repoAdded "b" "octo/other")
                  at 3L 2.0 (startingBecause "s" (Cause.Item (message "a")))
                  at 4L 3.0 (startingBecause "t" (Cause.Item (message "a"))) ]
            Expect.equal (linkOf "t" events) (Some CauseLink.Chained) "one cause, said once"

        testCase "a chain continues from an act that sits right under the cause" <| fun () ->
            let events =
                [ at 1L 0.0 (repoAdded "a" "octo/hello")
                  at 2L 1.0 (startingBecause "s" (Cause.Item (message "a")))
                  at 3L 2.0 (startingBecause "t" (Cause.Item (message "a"))) ]
            Expect.equal (linkOf "t" events) (Some CauseLink.Chained) "the second start is the chain's next link"

        testCase "a start under an act with a different cause draws its own" <| fun () ->
            let events =
                [ at 1L 0.0 (repoAdded "a" "octo/hello")
                  at 2L 1.0 (startingBecause "s" Cause.Booted)
                  at 3L 2.0 (startingBecause "t" (Cause.Item (message "a"))) ]
            Expect.equal (linkOf "t" events) (Some (CauseLink.Drawn (Cause.Item (message "a")))) "a new chain"

        testCase "a chain says its cause once" <| fun () ->
            let html =
                Support.render (
                    clientOf
                        [ at 1L 0.0 (repoAdded "a" "octo/hello")
                          at 2L 1.0 (repoAdded "b" "octo/other")
                          at 3L 2.0 (startingBecause "s" (Cause.Item (message "a")))
                          at 4L 3.0 (startingBecause "t" (Cause.Item (message "a"))) ])
            let lines = html.Split "data-cause-ref=" |> Array.length |> fun n -> n - 1
            Expect.equal lines 1 "one cause line over the two starts"

        testCase "a loaded cause is named as who did what, with references" <| fun () ->
            let html =
                Support.render (
                    clientOf
                        [ at 1L 0.0 (repoAdded "a" "octo/hello")
                          at 2L 1.0 (repoAdded "b" "octo/other")
                          at 3L 2.0 (startingBecause "s" (Cause.Item (message "a"))) ])
            let line = causeLineOf "s" html
            Expect.isTrue
                (line.Contains (Dom.attr "data-entity" (EntityRef.said (EntityRef.Actor (PeerRef ada))))
                 && line.Contains (Dom.attr "data-entity" (EntityRef.said (EntityRef.Repo hello))))
                "Ada and the repo she added are references"

        testCase "a loaded cause offers a jump to it" <| fun () ->
            let html =
                Support.render (
                    clientOf
                        [ at 1L 0.0 (repoAdded "a" "octo/hello")
                          at 2L 1.0 (repoAdded "b" "octo/other")
                          at 3L 2.0 (startingBecause "s" (Cause.Item (message "a"))) ])
            Expect.isTrue ((causeLineOf "s" html).Contains "data-cause-jump") "the mark is a jump"

        testCase "who a start was for is not said again when its cause names them" <| fun () ->
            let events =
                [ at 1L 0.0 (repoAdded "a" "octo/hello")
                  at 2L 1.0 (repoAdded "b" "octo/other")
                  at 3L 2.0 (startingFor "s" ada (Some (Cause.Item (message "a")))) ]
            Expect.equal (forWhomOf "s" events) "" "Ada added the repo, so the cause already names her"

        testCase "who a start was for stays when its cause names somebody else" <| fun () ->
            let events =
                [ at 1L 0.0 (repoAdded "a" "octo/hello")
                  at 2L 1.0 (repoAdded "b" "octo/other")
                  at 3L 2.0 (startingFor "s" (PeerId.create "bob" |> expect) (Some (Cause.Item (message "a")))) ]
            Expect.equal (forWhomOf "s" events) " for peer:bob" "Ada caused it; it ran for Bob"

        testCase "who a start was for is not said when the item right above caused it" <| fun () ->
            let events = [ at 1L 0.0 (repoAdded "a" "octo/hello"); at 2L 1.0 (startingFor "s" ada (Some (Cause.Item (message "a")))) ]
            Expect.equal (forWhomOf "s" events) "" "Ada is named on the add right above"

        testCase "who a start was for stays when nothing names them" <| fun () ->
            let events = [ at 1L 0.0 (repoAdded "a" "octo/hello"); at 2L 1.0 (startingFor "s" ada (Some Cause.Booted)) ]
            Expect.equal (forWhomOf "s" events) " for peer:ada" "the session starting names nobody"

        testCase "what a repo asks for, pushed away from what caused it, points back to it" <| fun () ->
            let asks =
                SessionEvent.RepoCapabilitiesChanged
                    { Repos.RepoCapabilitiesChanged.MessageId = message "c"
                      Repos.RepoCapabilitiesChanged.Repo = hello
                      Repos.RepoCapabilitiesChanged.Granted = [ "path:/nix:ro" ]
                      Repos.RepoCapabilitiesChanged.Sensitive = false
                      Repos.RepoCapabilitiesChanged.Actor = ActorRef.Configured hello
                      Repos.RepoCapabilitiesChanged.CausedBy = Some (Cause.Item (message "a")) }
            let events = [ at 1L 0.0 (repoAdded "a" "octo/hello"); at 2L 1.0 (repoAdded "b" "octo/other"); at 3L 2.0 asks ]
            Expect.equal (causeOf "c" events) (Some (Cause.Item (message "a"))) "the ref points at the add"

        testCase "a start the boot brought up names the boot" <| fun () ->
            let html =
                Support.render (
                    clientOf
                        [ at 1L 0.0 (SessionEvent.SessionStarted { MessageId = message "boot" })
                          at 2L 1.0 (repoAdded "a" "octo/hello")
                          at 3L 2.0 (startingBecause "s" (Cause.Item (message "boot"))) ])
            Expect.isTrue ((causeLineOf "s" html).Contains Dom.Text.causeBooted) "the session's own sentence"

        testCase "a start the first boot brought up offers no jump, with nothing drawn to jump to" <| fun () ->
            let html =
                Support.render (
                    clientOf
                        [ at 1L 0.0 (SessionEvent.SessionStarted { MessageId = message "boot" })
                          at 2L 1.0 (repoAdded "a" "octo/hello")
                          at 3L 2.0 (startingBecause "s" (Cause.Item (message "boot"))) ])
            Expect.isFalse ((causeLineOf "s" html).Contains "data-cause-jump") "the session starting is not on screen"

        testCase "a start after a resume opens its own chain" <| fun () ->
            let events =
                [ at 1L 0.0 (SessionEvent.SessionStarted { MessageId = message "boot" })
                  at 2L 1.0 (startingBecause "s" (Cause.Item (message "boot")))
                  at 3L 2.0 (SessionEvent.SessionResumed { MessageId = message "again"; LastHeardAt = epoch.AddSeconds 1.5 })
                  at 4L 3.0 (startingBecause "t" (Cause.Item (message "again"))) ]
            Expect.equal (linkOf "t" events) (Some (CauseLink.Drawn (Cause.Item (message "again")))) "its own boot, not the first one's chain"

        testCase "a cause that is not an item offers no jump" <| fun () ->
            let html = Support.render (clientOf [ at 1L 0.0 (startingBecause "s" Cause.Booted) ])
            Expect.isFalse ((causeLineOf "s" html).Contains "data-cause-jump") "nothing to jump to"
    ]

// --- A file, referenced (Plan 26) ---------------------------------------------------------

let private chart (seq: int) =
    ArtifactRef.create "chart.png" seq (ArtifactStamp.ofActor ActorRef.Agent) |> expect

let private sharedArtifact (n: string) (ref: ArtifactRef) =
    SessionEvent.ArtifactShared
        { Artifacts.ArtifactShared.MessageId = message n
          Artifacts.ArtifactShared.Ref = ref
          Artifacts.ArtifactShared.MediaType = Some "image/png"
          Artifacts.ArtifactShared.Bytes = 2_048L
          Artifacts.ArtifactShared.Digest = ContentDigest.create (String.replicate 64 "a") |> expect
          Artifacts.ArtifactShared.Actor = ActorRef.Agent }

/// The chip the chat passes in — the real one, because the point of the exercise is that a
/// link in prose and a reference in a fold arrive at the SAME drawing.
let private markerChip (ref: ContentRef) =
    Entity.render (clientOf []) ActorRef.Agent (EntityRef.Content ref)

let private contentChipTests =
    testList "A file, referenced (Plan 26)" [

        testCase "the act of sharing one draws the chip, linked to the version's own bytes" <| fun () ->
            let html = Support.render (clientOf [ at 1L 0.0 (sharedArtifact "a" (chart 0)) ])
            let hook = html.IndexOf (Dom.attr "data-entity-kind" "content")
            Expect.isTrue (hook >= 0) "the artifact is a reference, drawn as one"
            // From the anchor's own start, because the href is written before the hook: a
            // window that began at the hook would assert about everything except the link.
            let opens = (html.Substring (0, hook)).LastIndexOf "<a "
            let chip = html.Substring (opens, html.IndexOf ("</a>", opens) + "</a>".Length - opens)
            // The PINNED path, both as the href and as the hook: an act names the version it
            // shared, so the bytes a reader opens from the timeline are the bytes it is about,
            // whatever has been shared since.
            Expect.isTrue (chip.Contains (Dom.attr Dom.Hooks.content (ContentRef.value (ArtifactRef.content (chart 0)))))
                "carrying the content path, which is what the pane opens on"
            Expect.isTrue (chip.Contains ("content/" + ContentRef.value (ArtifactRef.content (chart 0))))
                "and leading to the route that serves it, so it works with no script at all"
            Expect.isTrue (chip.Contains ">chart.png<") "called what a person called it, not by its version leaf"
            Expect.isFalse (chip.Contains "target=\"_blank\"") "it stays in this session rather than opening a tab"

        testCase "a file: link in a message body becomes the same reference" <| fun () ->
            let rendered = Support.renderTemplate (RichText.render markerChip "see [the chart](file:///artifacts/chart.png) for it")
            Expect.isTrue (rendered.Contains (Dom.attr Dom.Hooks.content "artifacts/chart.png")) "the link is drawn as a reference"
            Expect.isFalse (rendered.Contains "href=\"file:") "and never as a link to somebody's disk"
            // The NAME form, unresolved: a body written once goes on saying "the latest", and
            // which version that is stays the route's answer rather than a fact frozen into
            // prose at the moment it was typed.
            Expect.isTrue (rendered.Contains "see ") "the words around it are still the sentence"

        // How agents actually write one: `share_artifact` answers with the address and says to
        // put it in a message, and they paste it straight into a sentence — which CommonMark
        // parses as plain text, so every artifact named in prose drew as a raw URL (S2AJDBFB).
        testCase "an address written bare in a sentence becomes the same reference" <| fun () ->
            let rendered =
                Support.renderTemplate (RichText.render markerChip "- file:///artifacts/chart.png/0000-e7f1a6 — the empty draft")
            Expect.isTrue
                (rendered.Contains (Dom.attr Dom.Hooks.content "artifacts/chart.png/0000-e7f1a6"))
                "the bare address is drawn as a reference"

        testCase "the punctuation after a bare address stays the sentence's" <| fun () ->
            let rendered = Support.renderTemplate (RichText.render markerChip "see file:///artifacts/chart.png.")
            Expect.isTrue (rendered.Contains (Dom.attr Dom.Hooks.content "artifacts/chart.png")) "the address without its full stop"

        test "an address in code is the characters the writer asked for" {
            let rendered = Support.renderTemplate (RichText.render markerChip "run `file:///artifacts/chart.png`")
            Expect.isFalse (rendered.Contains (Dom.attr Dom.Hooks.content "artifacts/chart.png")) "code is literal"
        }

        testCase "a bare file: address to nothing this session serves is words" <| fun () ->
            let rendered = Support.renderTemplate (RichText.render markerChip "see file:///etc/passwd")
            Expect.isTrue (rendered.Contains "file:///etc/passwd") "what was written still reads, and leads nowhere"

        testCase "a file: link to nothing this session serves is words, not a link" <| fun () ->
            let rendered = Support.renderTemplate (RichText.render markerChip "[passwords](file:///etc/passwd)")
            Expect.isFalse (rendered.Contains "href") "a path on somebody's disk is nowhere this page can send a reader"
            Expect.isTrue (rendered.Contains "passwords") "what was written still reads"

        testCase "an ordinary link is untouched" <| fun () ->
            let rendered = Support.renderTemplate (RichText.render markerChip "[the repo](https://github.com/octo/hello)")
            Expect.isTrue (rendered.Contains "href=\"https://github.com/octo/hello\"") "still a link out"
            Expect.isTrue (rendered.Contains "target=\"_blank\"") "opened the way a link out of the session is"
    ]

// --- A markdown table, rendered (RichText) -------------------------------------------------
// Reuses `markerChip` from the content-chip tests above purely as a `chip` callback: none of
// the markdown below names a `file:///` link, so it is never actually invoked.

let private tableMarkdown =
    "| Name | Qty | Price |\n\
     | :--- | :---: | ---: |\n\
     | Widget | 3 | $9.00 |"

let private richTableTests =
    testList "A markdown table, rendered" [

        testCase "a GFM table renders as real table markup, not literal pipes" <| fun () ->
            let rendered = Support.renderTemplate (RichText.render markerChip tableMarkdown)
            Expect.isTrue (rendered.Contains "<table") "a table element"
            Expect.isTrue (rendered.Contains ">Name<") "the header row's words read"
            Expect.isTrue (rendered.Contains ">Widget<") "the body row's words read"
            Expect.isFalse (rendered.Contains "| Name |") "the pipe syntax itself is transformed away"
            Expect.isFalse (rendered.Contains ":---") "and so is the alignment row"

        testCase "the header row carries column scope, the body row does not" <| fun () ->
            let rendered = Support.renderTemplate (RichText.render markerChip tableMarkdown)
            Expect.isTrue (rendered.Contains "<th scope=\"col\"") "a header cell names its column"
            Expect.isFalse (rendered.Contains "<td scope") "a data cell claims no scope"

        testCase "a column's alignment carries onto its cells, header and body alike" <| fun () ->
            let rendered = Support.renderTemplate (RichText.render markerChip tableMarkdown)
            let cellOf (needle: string) =
                let at = rendered.IndexOf (">" + needle + "<")
                let opens = (rendered.Substring (0, at)).LastIndexOf "<t"
                rendered.Substring (opens, at - opens)
            Expect.isTrue ((cellOf "Qty").Contains "text-center") "the centered column, header cell"
            Expect.isTrue ((cellOf "3").Contains "text-center") "the centered column, body cell"
            Expect.isTrue ((cellOf "Price").Contains "text-right") "the right-aligned column, header cell"
            Expect.isTrue ((cellOf "$9.00").Contains "text-right") "the right-aligned column, body cell"
            Expect.isFalse ((cellOf "Name").Contains "text-right") "the left column carries no alignment override"

        testCase "a cell carries the row-edge rule that drops its own padding at the wrapper's edge" <| fun () ->
            // `first:pl-0`/`last:pr-0` are CSS variants (`:first-child`/`:last-child`): every
            // cell's class attribute carries them literally, and the browser decides which
            // cell they fire on from its actual position in the row — a server-rendered
            // string has no row to check that against. What this can assert is that the
            // rule reaches every cell, header and body alike, so the CSS is there to fire.
            let rendered = Support.renderTemplate (RichText.render markerChip tableMarkdown)
            let cellOf (needle: string) =
                let at = rendered.IndexOf (">" + needle + "<")
                let opens = (rendered.Substring (0, at)).LastIndexOf "<t"
                rendered.Substring (opens, at - opens)
            for needle in [ "Name"; "Qty"; "Price"; "Widget" ] do
                Expect.isTrue ((cellOf needle).Contains "first:pl-0") $"{needle}'s cell carries the leftmost rule"
                Expect.isTrue ((cellOf needle).Contains "last:pr-0") $"{needle}'s cell carries the rightmost rule"

        testCase "a cell still formats the words inside it" <| fun () ->
            let rendered =
                Support.renderTemplate (RichText.render markerChip "| A |\n| --- |\n| **bold** and `code` |")
            Expect.isTrue (rendered.Contains ">bold</strong>") "a mark inside a cell still renders"
            Expect.isTrue (rendered.Contains ">code</code>") "so does inline code"

        testCase "prose around a table is untouched" <| fun () ->
            let rendered =
                Support.renderTemplate (RichText.render markerChip ("before\n\n" + tableMarkdown + "\n\nafter"))
            Expect.isTrue (rendered.Contains "before") "the paragraph before the table still reads"
            Expect.isTrue (rendered.Contains "after") "and the one after it"
    ]

/// A stretch in which nothing was running, drawn as a break in the page. What has to hold
/// however the break is styled: it is a break and not something somebody said, it offers BOTH
/// readings of its time, the reading is a press away and reversible, and the control says what
/// it does even though its visible words are only a duration.
let private sessionBreakTests =
    let cameBack = DateTimeOffset (2026, 9, 25, 13, 0, 0, TimeSpan.Zero)
    /// A resumption stamped `at'`, `hours` after the previous process was last heard from. The
    /// envelope's time is one end of the gap, so it cannot be left to a fixture default.
    let resumption (id: string) (offset: int64) (hours: float) (at': DateTimeOffset) =
        { EventId = EventId.fresh ()
          SessionId = sessionId
          Offset = EventOffset.create offset |> expect
          Actor = ActorRef.Session
          Timestamp = at'
          Event = SessionResumed { MessageId = message id; LastHeardAt = at'.AddHours -hours } }
    let showing (reading: string) = Dom.attr Dom.Hooks.sessionBreak reading
    let press (id: string) (model: ClientModel) = Support.step (ToggleBreakTimeMsg (message id)) model
    let oneBreak () = clientOf [ resumption "r" 1L 7.0 cameBack ]

    testList "A break where the session was away" [
        testCase "a resumption draws a break saying how long, in words" <| fun () ->
            let html = Support.render (oneBreak ())
            Expect.isTrue (html.Contains (showing Dom.Text.breakElapsed)) "a break, showing how long it was"
            Expect.stringContains html "7 hours later" "spelled out, because the label is only the duration"

        // It is drawn as a break INSTEAD of an act note, not as well as: the sentence the agent
        // is given still says it, and the same words in a line of the chat would say it twice.
        testCase "the break replaces the act note rather than joining it" <| fun () ->
            let html = Support.render (oneBreak ())
            Expect.isFalse (html.Contains "session resumed after being stopped for") "not also a line in the chat"

        // Hovering must answer without changing anything, so the moment is on the control from
        // the start rather than swapped in with the label.
        testCase "the break carries the moment before anything is pressed" <| fun () ->
            Expect.stringContains
                (Support.render (oneBreak ()))
                (Dom.attr "title" (Moment.stamp cameBack))
                "the moment is there to hover"

        testCase "pressing it shows the moment, and pressing again puts the duration back" <| fun () ->
            let pressed = Support.render (press "r" (oneBreak ()))
            Expect.isTrue (pressed.Contains (showing Dom.Text.breakMoment)) "now reading as a moment"
            Expect.stringContains pressed (Moment.stamp cameBack) "and the moment is what it says"
            let again = Support.render (press "r" (press "r" (oneBreak ())))
            Expect.isTrue (again.Contains (showing Dom.Text.breakElapsed)) "and back to how long"

        // The visible words are "7 hours later", which announces neither that it is a control
        // nor what pressing it does. Both have to be said where a keyboard and a screen reader
        // can reach them.
        testCase "its label is a control that says what pressing it does" <| fun () ->
            let html = Support.render (oneBreak ())
            Expect.stringContains html (Dom.attr "aria-label" Dom.Text.sessionBreakShowMoment) "what a press will do"
            Expect.stringContains html "<button" "a real button, so Tab and Enter reach it"
            Expect.stringContains
                (Support.render (press "r" (oneBreak ())))
                (Dom.attr "aria-label" Dom.Text.sessionBreakShowElapsed)
                "and what the next press will"

        // Two breaks are two questions. This is what the set in the model buys over one slot,
        // and the only case that can tell the two apart.
        testCase "answering one break leaves the other alone" <| fun () ->
            let model = clientOf [ resumption "first" 1L 7.0 cameBack; resumption "second" 2L 2.0 (cameBack.AddHours 9.0) ]
            let html = Support.render (press "first" model)
            Expect.stringContains html "2 hours later" "the break nobody pressed still reads as a duration"
            Expect.stringContains html (Moment.stamp cameBack) "and the pressed one as a moment"
    ]

let tests =
    testList "Timeline and the pane (Plan 14)" [
        contentChipTests
        richTableTests
        listTests
        sandboxTaskTests
        sandboxCauseTests
        replyRefRenderTests
        sessionBreakTests
        tabTests
        orderTests
        toolTests
        cardTests
        tallyTests
        chipTests
        stretchTests
        unchangedTests
        paneTests
        namingTests
        reloadTests
        edgeTabTests
        statusTests
        latestTests
        pageTests
        outputWindowTests
        keyframeTests
        videoTests
        readsTests
        dvrTests
    ]
