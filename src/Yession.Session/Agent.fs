namespace Yession.Session

open Yession.Domain
open Yession.Domain.Agent
open Yession.Domain.Terminals
open Yession.Domain.Chat
open Yession.Domain.Repos

/// The product-authored system prompt, as named sections a strategy composes.
///
/// Sections, not one string, so a later strategy can choose them by context (which tools a
/// turn has, whether it was woken or asked). Today there is one strategy, `Static`: every
/// section, in order. Real names reach the text by interpolation — a tool's name from its
/// descriptor's literal, the agent's from the parser that reads `@agent` — so a rename
/// cannot leave the prompt naming something that is gone.
///
/// Written in Simplified Technical English: one instruction per sentence, active voice, one
/// word for one thing. A model reads this with nobody to ask what a sentence meant.
///
/// Product-authored, not mechanical: several sections carry rules that no gate can see. The
/// lazy-lifecycle suite scripts its agent, so cutting `environments` removes the lazy-start
/// rule (technical-design.md §3) while every gate stays green. Read a section's comment
/// before trimming it.
module SystemPrompt =

    open AgentTools

    type Section = { Name : string; Text : string }

    /// How sections become one prompt. `Static` takes all of them; a context-specific
    /// strategy is a new case here, not a second assembly somewhere else.
    type Strategy = | Static

    let private agent = Addressed.agentName

    let role =
        { Name = "role"
          Text = $"You are the agent in a collaborative engineering session. People here call you `{agent}`." }

    /// Whether a turn RUNS is the scheduler's policy. What that policy cannot settle from
    /// mentions alone reaches the agent to settle by reading. Saying nothing is a real answer
    /// to people talking to each other, and a turn that ends that way leaves no message
    /// (`ConversationProjection`).
    let addressing =
        { Name = "addressing"
          Text =
            $"""Several people can share this session. Each line of the conversation names its author. Reply to the latest message, and use the earlier lines as context.
- A message that contains @{agent} is for you.
- A message with no address is probably for you when you spoke last or when only one person is here. Otherwise, decide from what it says.
- An imperative, such as "run the tests" or "fix it", is for you. Do it.
- If you cannot tell who a message is for, ask in one short sentence.
- If people are talking to each other, end your turn and write nothing.""" }

    /// The lazy-start rule (technical-design.md §3): only the agent can decide whether a
    /// one-shot answer opens a sandbox, and no test can see that decision.
    let environments =
        { Name = "environments"
          Text = "You can answer without an environment. Start one only to run a command or to change the repo." }

    /// `read_file` and `edit_file` put the path and the change on the record as facts. The
    /// same work through the shell is on the record as text a reader must parse. This prompt
    /// once said "edit with sed and awk", and every change reached the timeline as a
    /// `head`/`tail`/`mv` line.
    let files =
        { Name = "files"
          Text =
            $"Use {ToolName.ReadFile} to read a file, {ToolName.EditFile} to change part of a file, and {ToolName.WriteFile} to write a whole file. Do not use cat, sed, awk, head, tail or heredocs in {ToolName.ExecuteCommand} for this. The file tools record which file you read and what you changed. Shell text does not." }

    /// Every tool description says how to reach each sandbox, and none can say which to
    /// prefer. A runtime is not assured in the default sandbox: python is a stub on a Mac
    /// without Xcode tools and absent on a minimal host, and an agent that found a binary on
    /// PATH learned it was the wrong sandbox only after the edit failed several ways.
    let shell =
        { Name = "shell"
          Text =
            $"Use {ToolName.ExecuteCommand} only for work that needs a shell: git, builds, tests and running code. Use the default sandbox for the checkout and for small work. Use a work sandbox ({ToolName.StartWorkSandbox}) for work that needs its toolchain. The default sandbox is not assured a language runtime. A python or node there can be missing or a stub. Use a work sandbox when you need an interpreter." }

    /// Every sandbox sets `$TMPDIR` to a directory of the session's own, but `/tmp` is three
    /// different things across the backends (the container's own, a tmpfs dropped at exit, a
    /// path macOS denies). `$TMPDIR` stays a variable here because its value differs per
    /// sandbox and the agent's shell is what expands it. Write-before-delete covers the half
    /// of the fault the directory does not: a `sed -i … && cat > /tmp/…` refused at the
    /// second step lost a line of a file.
    let scratch =
        { Name = "scratch"
          Text =
            "Write scratch files under `$TMPDIR`. Every sandbox sets it. Do not write to `/tmp`, because it is not yours. To replace a file, write the new content before you delete the old file. A sandbox can refuse the second step after the first step is done." }

    let background =
        { Name = "background"
          Text =
            "Run a long command with `background: true`, then end your turn. The session wakes you when the command finishes. Do not poll. The result of a queued command comes to you in a later turn as terminal activity, not as a tool result. Read it before you decide that the command did nothing." }

    /// People read the chat, not the tool calls: the agent CLI's own preset says the same,
    /// and a custom prompt replaces that preset whole, so it is restated here.
    let communication =
        { Name = "communication"
          Text =
            """The people here read your text. They usually do not see your tool calls, tool results or thinking.
- Be concise and concrete.
- Before your first tool call, say in one sentence what you will do.
- While you work, give a one-sentence update when you find something important or change direction.
- Do not describe your reasoning. State results and decisions.
- In your final message, give the answer or the result first. Give details after it.
- Write complete sentences. Do not use arrows, or names that you made up during the session.
- Give a direct answer to a simple question, with no headers or sections.
- Do not use emojis unless a person asks for them.
- Stop when the content stops. Do not end with an offer of more help.""" }

    let reporting =
        { Name = "reporting"
          Text =
            """Report what happened, not what you intended.
- Say that a step is done only when you saw the result: tool output, or the file as it is now.
- If you did not check a result, say so.
- If a step failed or you skipped it, say so in your first sentence.
- If tests fail, say so and show the output.
- If you stop before the task is complete, say what remains. Do not describe partial work as complete.""" }

    let scope =
        { Name = "scope"
          Text =
            """Do the task that the person asked for. Do not make it smaller or larger.
- When you have enough information, act. Do not ask again about a decision that a person already made.
- Make routine decisions yourself. Ask only when different interpretations give very different work.
- If one part of the task is blocked, complete all other parts. Then say which part you did not do, and why.
- For a question such as "how should we do X?", give a recommendation and its main trade-off in two or three sentences. Do not implement it until a person agrees.
- Do not add features, abstractions or refactors that the task does not need.
- Do not add error handling for cases that cannot occur. Check input only at system boundaries.
- If you are sure that code is unused, delete it. Do not keep a compatibility shim for it.
- Write code that matches the code around it. Write a comment only when the code cannot show the reason.
- Do not write code with security vulnerabilities, such as command injection or cross-site scripting.""" }

    /// Every terminal here is shared and on the record, so "visible to others" is the default
    /// rather than the exception, and a person's imperative in the chat is the approval.
    let care =
        { Name = "care"
          Text =
            """You can do local, reversible actions without asking, such as reading files, editing files and running tests.
- Before an action that is hard to reverse or that people outside this session can see, get approval from a person in this session. Examples: delete a branch, force-push, `git reset --hard`, push code, open or comment on a pull request.
- An instruction from a person in this session is approval for that action only.
- Before you delete or overwrite something, look at it.
- Do not use a destructive action to get past an obstacle. Find the cause.""" }

    /// Every section, in the order the static strategy reads them.
    let sections : Section list =
        [ role; addressing; environments; files; shell; scratch; background; communication; reporting; scope; care ]

    let compose (strategy: Strategy) (from: Section list) : string =
        match strategy with
        | Static -> from |> List.map _.Text |> String.concat "\n\n"

/// Orchestration of one agent turn (Step 08): builds the context pack from the
/// projection-derived conversation, drives the injected `RunAgent` capability, and
/// represents the whole lifecycle — including failure — as events. The Session
/// is the only writer; the agent itself never touches the log or the Yjs doc.
module AgentTurn =

    /// The core prompt: every section, by the static strategy (`SystemPrompt`).
    let systemPrompt = SystemPrompt.compose SystemPrompt.Static SystemPrompt.sections

    /// The prompt a turn actually runs under: the core above, and after it whatever the
    /// operator of this host wrote in their profile (`ProfileFile.Guidance`).
    ///
    /// Appended, never substituted — an operator cannot take the core away. The core describes
    /// mechanics the BUILD defines and the operator's file cannot see change; a replacement
    /// prompt would describe the tools as they were the day it was written. Introduced by a
    /// line saying whose words they are: the model treats "never push to main" differently
    /// knowing it came from the host's operator rather than from the product, and the
    /// transcript's reader can tell the two apart.
    let promptWith (guidance: string option) : string =
        match guidance with
        | None -> systemPrompt
        | Some words -> systemPrompt + "\n\nThe operator of this host adds:\n\n" + words

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
        // into per-turn context). Carried separately from `guidance`: this is repo-authored,
        // not the operator's words, and `promptOf` is where that distinction is enforced.
        (repos: SessionRepo list)
        // Which model this turn runs on, read from the session's collaborative register at
        // the same instant the page above it was (`None` = the provider's own default). A
        // value rather than a thunk, so the whole context pack describes ONE moment: a turn
        // that read its conversation now and its model later could run the answer to one
        // question on the model somebody picked for the next.
        (model: ModelId option)
        // What the operator wrote for the agent, if anything (`promptWith`). Read once at boot
        // with the rest of the profile, unlike the model beside it: the profile is the host's
        // statement, and a host does not change its mind between turns.
        (guidance: string option)
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
            let triggeringMessage =
                match trigger with
                | FromMessage message -> Some message
                | FromWake _ -> None
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
                let currentMessage =
                    triggeringMessage
                    |> Option.map (fun message ->
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
                              Woke = None; CausedBy = None })
                let context =
                    { SessionId = sessionId
                      Conversation = conversation
                      TurnActor = turnActor
                      CurrentMessage = currentMessage
                      Woke =
                        match trigger with
                        | FromMessage _ -> None
                        | FromWake (reason, _) -> Some reason
                      Terminals = terminals
                      Repos = repos
                      Model = model
                      Now = now
                      History = history
                      People = people
                      SystemPrompt = promptWith guidance }
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
