namespace Yession.Session

open Yession.Domain
open Yession.Domain.Agent
open Yession.Domain.Terminals
open Yession.Domain.Chat
open Yession.Domain.Repos

/// Orchestration of one agent turn (Step 08): builds the context pack from the
/// projection-derived conversation, drives the injected `RunAgent` capability, and
/// represents the whole lifecycle — including failure — as events. The Session
/// is the only writer; the agent itself never touches the log or the Yjs doc.
module AgentTurn =

    /// Why a turn is running (Plan 20, stage 2). A turn has exactly ONE reason to exist,
    /// which is what makes this a choice rather than two optional arguments free to be both
    /// set or neither.
    ///
    /// Both arms name a `Principal`, and it is the same question in both: whoever the turn
    /// runs for. A message's is its author; a wake's is the party whose earlier turn queued
    /// the work that finished. The agent is the acting party either way and has no scope of
    /// its own (Plan 08), so a turn that could not name one would be a turn with no
    /// credentials — which is why it is a `Principal` and not an actor, and why the wake
    /// resolves it from the log before the turn starts rather than after.
    type TurnTrigger =
        | FromMessage of MessageSent
        | FromWake of WakeReason * Principal

    module TurnTrigger =

        let actor (trigger: TurnTrigger) : Principal =
            match trigger with
            | FromMessage message -> message.Author
            | FromWake (_, owner) -> owner

    /// Run one agent turn, appending the lifecycle events:
    ///
    ///   AgentTurnStarted -> AgentContextBuilt -> AgentMessageStarted
    ///     -> (AgentMessageDelta* -> AgentMessageStarted[antecedent])* -> AgentMessageDelta*
    ///     -> AgentMessageCompleted | AgentTurnFailed
    ///
    /// A turn that calls tools speaks in several messages, one per stretch of text the
    /// runner streams between boundaries; each names the one before it, which is how the
    /// projection closes it. `AgentMessageCompleted` is appended once, for the last, and is
    /// what the process and the client read as the turn ending.
    ///
    /// Failures — result-level and thrown — become `AgentTurnFailed`, never exceptions
    /// surfaced to callers. Id minting is injected so tests are deterministic.
    ///
    /// If `signal` fires (Step 17), the Session has already appended the
    /// terminal `AgentTurnInterrupted`: from that point this orchestrator appends
    /// nothing more — late chunks are dropped and the runner's eventual result is
    /// discarded. The deltas appended before the interrupt stand as the partial body.
    let run
        (log: EventLog<SessionEvent>)
        (runAgent: RunAgent)
        (signal: AgentAbortSignal)
        // Takes the turn's ACTOR as well as its id (Plan 20, stage 2): the credentials a
        // turn runs on are bound per turn, and a woken turn has no message to read them off.
        (capabilitiesFor: AgentTurnId -> Principal -> AgentCapabilities)
        // Telemetry (Plan 04): fired with the turn's usage on completion. Injected (default
        // `ignore` off the Host) so this module stays OTel-free. Never throws into the turn.
        (emitUsage: AgentTurnId -> AgentUsage -> unit)
        (mintTurnId: unit -> AgentTurnId)
        (mintMessageId: unit -> MessageId)
        (sessionId: SessionId)
        // When the session began and last came back, off the same page as the conversation.
        (history: SessionHistory)
        (conversation: ConversationItem list)
        // Who is who, off the same page as the conversation, so the transcript can call
        // each author by the name the people here see.
        (people: Attribution.State)
        // What the terminals did since the previous turn (Plan 13, stage 3a). Built by the
        // caller from the same log page the conversation came from, so the two describe
        // the same instant.
        (terminals: BlockDigest list)
        // The session's repos, off the same page terminals came off (Plan: repo AGENTS.md
        // into per-turn context). Carried apart from the operator's guidance, which the runner
        // holds: this is repo-authored, not the operator's words, and `Prompting` is where that
        // distinction is enforced.
        (repos: SessionRepo list)
        // Which model this turn runs on, read from the session's collaborative register at
        // the same instant the page above it was (`None` = the provider's own default). A
        // value rather than a thunk, so the whole context pack describes ONE moment: a turn
        // that read its conversation now and its model later could run the answer to one
        // question on the model somebody picked for the next.
        (model: ModelId option)
        (trigger: TurnTrigger)
        : Async<unit> =
        async {
            let turnId = mintTurnId ()
            let append event =
                async {
                    let! _ = log.Append ActorRef.Agent event
                    return ()
                }
            let turnActor = TurnTrigger.actor trigger
            // Kept, not discarded like the rest: the envelope it was written in is this turn's
            // "now", on the session's own clock — read back by the offset the append answered.
            let! appended =
                log.Append
                    ActorRef.Agent
                    (AgentTurnStarted
                        { AgentTurnId = turnId
                          // 1:1 with the trigger the scheduler handed in — the cause is the
                          // trigger, said durably, with no second field to keep consistent.
                          Cause =
                            match trigger with
                            | FromMessage message -> TurnCause.TriggeredBy message.MessageId
                            | FromWake (reason, _) -> TurnCause.Woke reason })
            let! written =
                let before = EventOffset.value appended.Offset - 1L
                log.Read (if before < 0L then None else EventOffset.create before |> Result.toOption) 1
            let now =
                match written.Events with
                | envelope :: _ -> envelope.Timestamp
                // Unreachable: the log just said it holds this offset. Failing the turn over a
                // clock reading would be the worse answer, so it falls to the host's.
                | [] -> System.DateTimeOffset.UtcNow
            try
                // The agent's context is the event-log-derived projection — by
                // construction it can never include Yjs/draft state.
                let occasion =
                    match trigger with
                    | FromMessage message ->
                        conversation
                        |> List.tryFind (fun item -> item.MessageId = message.MessageId)
                        |> Option.defaultValue
                            { MessageId = message.MessageId
                              Author = Principal.toActor message.Author
                              Content = ItemContent.Message message.Body
                              Status = Complete
                              // Synthesized from the trigger rather than folded, so it has no
                              // offset of its own. Nothing here sorts — the agent's context is
                              // built in the projection's order, and this stands in for an item
                              // that projection has not caught up to yet.
                              Offset = EventOffset.zero
                              // A turn with a triggering message is by definition a turn
                              // somebody asked for, so there is nothing here to explain.
                              Woke = None; CausedBy = None }
                        |> Occasion.Asked
                    | FromWake (reason, _) -> Occasion.Woken reason
                let context =
                    { SessionId = sessionId
                      Conversation = conversation
                      TurnActor = turnActor
                      Occasion = occasion
                      Terminals = terminals
                      Repos = repos
                      Model = model
                      Now = now
                      History = history
                      People = people }
                do! append (AgentContextBuilt { AgentTurnId = turnId; MessageCount = List.length conversation })

                // Where one message ends and the next begins, decided from the stream alone.
                // The first message opens before the model has said anything, so the chat
                // shows a turn under way; every later one opens on the first TEXT after a
                // boundary, and only if the current message has spoken — a message that was
                // all tool calls is not a message, and a boundary the model never speaks
                // after opens nothing. What the current message streamed is kept so a
                // follower can be closed at exactly that: the runner's body is the SDK's
                // account of the turn as one message, and once the turn has split, only the
                // stream says which words were the last message's.
                let current = ref (mintMessageId ())
                let spoken = ref ""
                let followsAntecedent = ref false
                let boundary = ref false
                do! append (AgentMessageStarted { AgentTurnId = turnId; MessageId = current.Value; Antecedent = None })

                let onChunk (chunk: AgentResponseChunk) =
                    if not (signal.IsAborted ()) then
                        match chunk with
                        | AgentResponseChunk.MessageBoundary -> boundary.Value <- true
                        // Recorded, and deliberately touching NONE of the message state above:
                        // reasoning is not the model speaking, so it opens no message, closes
                        // none, and does not make a boundary into a message that has spoken.
                        // A turn whose whole output was thinking and tool calls still said
                        // nothing, and the transcript should go on reading that way.
                        | AgentResponseChunk.Thinking thought ->
                            Async.StartImmediate (append (AgentThought { AgentTurnId = turnId; Thought = thought }))
                        | AgentResponseChunk.Text text ->
                            if boundary.Value && spoken.Value <> "" then
                                let next = mintMessageId ()
                                Async.StartImmediate (
                                    append (AgentMessageStarted { AgentTurnId = turnId; MessageId = next; Antecedent = Some current.Value }))
                                current.Value <- next
                                spoken.Value <- ""
                                followsAntecedent.Value <- true
                            boundary.Value <- false
                            spoken.Value <- spoken.Value + text
                            Async.StartImmediate (
                                append (AgentMessageDelta { AgentTurnId = turnId; MessageId = current.Value; Delta = text }))

                let! result = runAgent context (capabilitiesFor turnId turnActor) signal onChunk
                if not (signal.IsAborted ()) then
                    match result with
                    | AgentCompleted (body, usage) ->
                        let said = if followsAntecedent.Value then spoken.Value else body
                        do! append (AgentMessageCompleted { AgentTurnId = turnId; MessageId = current.Value; Body = said })
                        // Telemetry after the durable event: the body is the fact, usage is
                        // observability. `emitUsage` never throws (guarded at the sink).
                        match usage with
                        | Some u -> emitUsage turnId u
                        | None -> ()
                    | AgentFailed (reason, usage) ->
                        do! append (AgentTurnFailed { AgentTurnId = turnId; Reason = reason; ProcessEnded = None })
                        // Usage after the durable event, exactly as the completion above:
                        // the turn that reaches this branch is typically the one that ran
                        // longest, so reporting nothing for it is where a session's cost
                        // goes missing.
                        match usage with
                        | Some u -> emitUsage turnId u
                        | None -> ()
            with e ->
                if not (signal.IsAborted ()) then
                    do! append (AgentTurnFailed { AgentTurnId = turnId; Reason = e.Message; ProcessEnded = None })
        }
