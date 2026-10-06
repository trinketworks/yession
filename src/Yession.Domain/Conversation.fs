namespace Yession.Domain.Chat

open Yession.Domain.Watching
open Yession.Domain
open Yession.Domain.Agent
open Yession.Domain.Artifacts
open Yession.Domain.Content
open Yession.Domain.Prs
open Yession.Domain.Sandboxes

/// The conversation is a *projection* of the event log — never read from Yjs/draft state.
/// The projection type and its fold live in the shared Domain library because both the
/// Session and the App derive the conversation the same way.
/// See docs/technical-design.md §1 "Reactive" and §2.2.

type ConversationItemStatus =
    | Complete
    | Streaming
    | Failed
    /// An ACT that is under way — work that takes time and has not finished, like a sandbox
    /// coming up. It is to an act what `Streaming` is to a message: the item holds its place
    /// while the work runs, and a later event with the same `MessageId` resolves it to
    /// `Complete` or `Failed`. This is the status that makes an act a TASK — the one thing a
    /// task view (a live queue, a count of what is happening now) reads it by
    /// (`Timeline.taskState`).
    | Running

/// What an item in the timeline IS (Plan 14): something someone SAID, or something someone
/// DID. A message is one body — markdown, streamed in. An act carries the facts of what was
/// done (`Act`), and nothing else: not a sentence, which is a reader's to make, and not a
/// detail string, which was the same sentence's second half stored beside its first.
///
/// One union rather than a `Body` beside a `Kind`, so a message cannot be written carrying
/// an act's facts and an act cannot be written carrying a body nobody built from its facts.
/// Distinguished by a case rather than by author or body convention, so a renderer can
/// style a note without parsing anything — and every act lands in one case, because the
/// timeline is how a human sees what was done on their behalf, and a kind per capability
/// would be a renderer per capability.
/// How a turn came to stop short of finishing. Two ways, and no third: the process could
/// not carry it on, or a person stopped it. Structured rather than a sentence because the
/// second names a person, and a person is drawn by name on a screen and spelled by
/// reference to a reader that is not one (`phrase`) — never as the id the event carries.
///
/// Qualified access, because `Failed` and `Interrupted` are also words the status vocabulary
/// beside this uses, and a bare case that resolved to whichever type was declared last is
/// how a construction quietly changes meaning.
[<RequireQualifiedAccess>]
type TurnStop =
    /// The process's account of why the turn could not go on: a model error, a credential
    /// that was not there, the session restarted under it.
    | Failed of reason: string
    /// A person stopped it.
    | Interrupted of by: PeerId

module TurnStop =

    /// What the stop says, as segments: the reason's own words, or the person who stopped
    /// it as a reference the screen resolves to a name.
    let phrase (stop: TurnStop) : Phrase =
        match stop with
        | TurnStop.Failed reason -> Phrase.text reason
        | TurnStop.Interrupted by -> [ Segment.Text "interrupted by "; Segment.Ref (EntityRef.Actor (PeerRef by)) ]

[<RequireQualifiedAccess>]
type ItemContent =
    | Message of body: string
    | Act of Act
    /// The turn this item belongs to stopped here, and how — the process's account, never
    /// the agent's words. Its own kind rather than a message carrying the reason as a body,
    /// because a reason drawn in the agent's voice was read as something the agent said,
    /// and a reader cannot be expected to know which paragraph of a reply was the machine's.
    | Stopped of TurnStop

type ConversationItem =
    { MessageId : MessageId
      Author    : ActorRef
      /// What was said, or what was done. A reader that is not a screen wants a sentence
      /// either way, and `ConversationItem.said` is the one that gives it. See it for why.
      Content   : ItemContent
      Status    : ConversationItemStatus
      /// The offset of the event at which this item first SAID something — the message that
      /// was sent, the note that was made, or the agent's first word (Plan 14, stage 1).
      /// Later deltas and the completion move the body and the status; they never move the
      /// item, so a streaming answer holds its place in the order exactly as a running
      /// command's chip does.
      ///
      /// The agent's first message of a turn opens BEFORE it speaks, so the chat shows a turn
      /// under way, and most turns then call tools for a while before saying anything. Until
      /// the first word the item sits where it opened; at the first word it moves to where
      /// that word landed. Anchoring it where it opened put every answer ABOVE the work it
      /// answered with — a reader saw the conclusion, then the twelve commands that reached
      /// it. A follower message opens on its first word already (`AgentMessageStarted` with an
      /// antecedent), so this makes the first message read like the rest.
      ///
      /// Carried so the view can interleave this with terminal work in one timeline. Both are
      /// folds of the SAME ordered log, which makes merging them a sort rather than a clock
      /// reconciliation — and this field is the only thing that was missing.
      Offset    : EventOffset
      /// Why the turn that produced this item exists, when nobody asked for it (Plan 20,
      /// stage 2). `None` on everything a person said and on every turn a person triggered.
      ///
      /// On the ITEM rather than looked up from the turn, because the timeline renders items
      /// and an item does not know its turn. It rides here for the same reason `Offset` does:
      /// the fold knows something the view needs and cannot re-derive.
      Woke      : WakeReason option
      /// What this item came from, when a ref is worth drawing (`Cause`): the message a turn
      /// was replying to, or what brought a repo's sandbox up — a repo added, the session
      /// starting, a person connecting.
      ///
      /// For a reply: `Some` on the
      /// first message of a turn a message triggered, but ONLY when that message is not the
      /// one this item lands directly below. An adjacent reply already sits under what it
      /// answers, so a ref there is noise on every ordinary turn — the value is the DETACHED
      /// case, a reply pushed away from its cause by other messages (a second person, an act
      /// note, a woken turn's output). `None` on a woken turn (no message caused it), on a
      /// follower (its cause is its antecedent), and on everything a person said.
      ///
      /// For a reply, presence is the whole decision — the view draws the ref iff this is
      /// `Some`. The detached test lives here, not in the view, because "is the trigger the
      /// item above" is a fact about the fold's order that a cheap test can reach.
      ///
      /// For an act, the cause is kept whole, and how it is drawn is `ConversationItem.causeLinks`:
      /// whether it sits directly above, or continues the chain the act above began, is read
      /// off both neighbours rather than off the one the fold had when this landed.
      CausedBy  : Cause option }

/// How an act's cause is drawn, read off the act above it (`ConversationItem.causeLinks`).
[<RequireQualifiedAccess>]
type CauseLink =
    /// Nothing to draw: no cause, or the cause is the item directly above, by the same author.
    | Unlinked
    /// The cause, drawn above the act.
    | Drawn of Cause
    /// The act above has the same cause: drawn as the next link of that chain.
    | Chained

module ConversationItem =

    /// Everything this item says, headline and particulars, as one sentence.
    ///
    /// The split exists for a SCREEN: an eye needs a gist to land on, and a paragraph with no
    /// gist is a paragraph nobody reads. A reader that is not a screen — the agent's prompt,
    /// a digest, a log line — has no such need and must never be handed the headline alone,
    /// because the half a headline leaves out is the half that says which credential went
    /// into the sandbox, why the declaration was refused, and what the checkout is asking
    /// for. That is the half somebody is being asked to decide about.
    ///
    /// It lives here rather than in each of those readers for the ordinary reason: a rule
    /// about how an act's two halves compose is a rule about the act, and a caller that had
    /// to remember to ask for the second half is a caller that will one day not.
    /// Who an item's cause names as the one who caused it: the author of the item it points
    /// to, or the person who connected. `None` when the cause names nobody, or points at an
    /// item that is not among `items`.
    let causer (items: ConversationItem list) (item: ConversationItem) : ActorRef option =
        match item.CausedBy with
        | Some (Cause.Item target) ->
            items |> List.tryFind (fun i -> i.MessageId = target) |> Option.map (fun i -> i.Author)
        | Some (Cause.Connected principal) -> Some (Principal.toActor principal)
        | Some Cause.Booted
        | None -> None

    /// How each act's cause is drawn, keyed by the act. One pass over `items` in screen
    /// order, because a link is a fact about an act AND the one above it:
    ///
    /// - the cause is the item directly above, by the same author → nothing; the eye already
    ///   sees it. A different author puts that author's header between the two, and the
    ///   link is no longer something the eye can see, so it is drawn.
    /// - the act above has the same cause → a link in its chain, so the cause is said once
    ///   over a run of acts it produced (a repo's ask and each sandbox it starts).
    /// - otherwise → the cause, drawn.
    ///
    /// Only acts: a reply's ref is its own rule (`CausedBy`).
    let causeLinks (items: ConversationItem list) : Map<MessageId, CauseLink> =
        let isAct (i: ConversationItem) =
            match i.Content with
            | ItemContent.Act _ -> true
            | ItemContent.Message _
            | ItemContent.Stopped _ -> false
        let linkOf (above: ConversationItem option) (item: ConversationItem) =
            match item.CausedBy, above with
            | None, _ -> CauseLink.Unlinked
            | Some (Cause.Item target), Some a when a.MessageId = target && a.Author = item.Author -> CauseLink.Unlinked
            | Some cause, Some a when isAct a && a.CausedBy = Some cause -> CauseLink.Chained
            | Some cause, _ -> CauseLink.Drawn cause
        items
        |> List.fold
            (fun (above, links) item ->
                Some item, (if isAct item then Map.add item.MessageId (linkOf above item) links else links))
            (None, Map.empty)
        |> snd

    /// Who an act was for, as a screen that also draws its cause says it: `Act.forWhom`,
    /// unless the cause already names that person. "started sandbox gate for Nick" under
    /// "Nick added repo …" says Nick twice, whether that line is drawn over the act, over
    /// the chain it continues, or is the item right above. A cause that names somebody
    /// else, or nobody, leaves the clause as it was.
    let forWhom (items: ConversationItem list) (item: ConversationItem) : Phrase =
        match item.Content with
        | ItemContent.Act act ->
            match Act.onBehalfOf act, causer items item with
            | Some person, Some named when Principal.toActor person = named -> []
            | _ -> Act.forWhom act
        | ItemContent.Message _
        | ItemContent.Stopped _ -> []

    let said (item: ConversationItem) : string =
        match item.Content with
        | ItemContent.Message body -> body
        | ItemContent.Act act -> Phrase.said (Act.sentence act)
        | ItemContent.Stopped stop -> Phrase.said (TurnStop.phrase stop)

    /// The headline alone — what a message said, or the one sentence an act leads with.
    /// For a reader that has its own way of showing the particulars, or none: a chapter's
    /// default name is cut from this, not from the whole account.
    let headline (item: ConversationItem) : string =
        match item.Content with
        | ItemContent.Message body -> body
        | ItemContent.Act act -> Phrase.said (Act.phrase act)
        | ItemContent.Stopped stop -> Phrase.said (TurnStop.phrase stop)

    /// Whether this is a person's OWN words — which is the only thing a name is made from.
    ///
    /// What the agent says and what the session notes are the bulk of a busy stretch, and a
    /// name made from them is a name for the work rather than for what somebody came for:
    /// one sentence surrounded by twelve act notes reads as "running tests" whatever was
    /// actually asked. Nor is it only dilution — the reading is bounded, and the notes
    /// arrive first, so they push the person's words out of the window entirely.
    ///
    /// It lives here rather than in the naming rule because it is a fact about an item, and
    /// the naming rule is not the only reader that will want to know whose words these are.
    let personal (item: ConversationItem) : bool =
        match item.Content, item.Author with
        | ItemContent.Message _, (ActorRef.UserRef _ | ActorRef.PeerRef _) -> true
        | _ -> false

type ConversationProjection =
    { /// The transcript, NEWEST FIRST — which is the order it is written in and the order
      /// the hot edit reaches for, and neither is the order it is read in. `Items` below is
      /// the read.
      ///
      /// An agent message is appended when it starts and then edited once per word that
      /// arrives, and an immutable list can only be edited cheaply at its HEAD: everything
      /// before the match is copied. Oldest-first put the item being written at the far
      /// end, so each word rebuilt the whole transcript — 13,020 deltas over 780 items on
      /// one real session, about ten million item copies, which measured as 373ms of the
      /// 402ms that folding its conversation cost at all, and several seconds of a phone's
      /// cold open.
      ///
      /// Nothing outside this module reads it. The field is the write order; `Items` is the
      /// read order, and keeping them apart is the whole point.
      Recent : ConversationItem list
      /// Agent messages currently streaming, by turn — so a turn failure (which carries
      /// only the turn id) can mark its item `Failed`. Projection-internal bookkeeping.
      ActiveAgentMessages : Map<AgentTurnId, MessageId>
      /// The turn nobody asked for, while it is the current one (Plan 20, stage 2) — so the
      /// items it goes on to produce can say why they exist. Projection-internal bookkeeping.
      ///
      /// One turn rather than a map, because turns are serial: the scheduler runs one at a
      /// time, and `AgentWake.pending` folds on that same fact — it resets at every
      /// `AgentTurnStarted`. So does this, which is also what keeps it from growing: an
      /// ordinary turn clears it, and there is no turn-completed event that could.
      WokenTurn : (AgentTurnId * WakeReason) option
      /// The current turn's triggering message, while it is the current one — the mirror of
      /// `WokenTurn` for the other arm of `TurnCause`. The first message of the turn reads it
      /// to decide whether to carry a reply ref, and it resets at every `AgentTurnStarted`
      /// for the same reason `WokenTurn` does.
      TriggeredTurn : (AgentTurnId * MessageId) option }

    /// The transcript in reading order, oldest first.
    ///
    /// O(n), and a view rather than a field: it is computed from `Recent` on every access,
    /// so BIND IT ONCE and read the binding. A caller that reaches for it inside a loop over
    /// the items has written the quadratic this type exists to avoid, one level up.
    member this.Items : ConversationItem list = List.rev this.Recent

module ConversationProjection =

    let empty : ConversationProjection =
        { Recent = []; ActiveAgentMessages = Map.empty; WokenTurn = None; TriggeredTurn = None }

    /// A projection holding exactly these items, given in READING order — which is how a
    /// fixture writes a transcript down, and the opposite of how `Recent` stores one.
    let ofItems (items: ConversationItem list) : ConversationProjection =
        { empty with Recent = List.rev items }

    /// Replace one item, copying only as far as it.
    ///
    /// Two differences from the `List.map` this replaces, and both are about what it does
    /// NOT touch: the tail past the match is shared rather than copied, and an id that is
    /// not there gives back the very list it was handed. Over a newest-first transcript the
    /// item being edited is at or near the head, so this is the difference between copying
    /// a couple of cells and copying the session.
    let private updateItem (messageId: MessageId) (f: ConversationItem -> ConversationItem) (items: ConversationItem list) =
        // Accumulated and reversed rather than written as a plain recursion: the prefix is
        // short in the case this is written for, and unbounded in the case it is not, and a
        // transcript is not a thing to put on the stack.
        let rec walk (seen: ConversationItem list) (rest: ConversationItem list) =
            match rest with
            | [] -> items
            | item :: tail when item.MessageId = messageId -> List.rev seen @ (f item :: tail)
            | item :: tail -> walk (item :: seen) tail
        walk [] items

    /// Drop one item, copying only as far as it — `updateItem`'s shape, for the same reason.
    let private removeItem (messageId: MessageId) (items: ConversationItem list) =
        let rec walk (seen: ConversationItem list) (rest: ConversationItem list) =
            match rest with
            | [] -> items
            | item :: tail when item.MessageId = messageId -> List.rev seen @ tail
            | item :: tail -> walk (item :: seen) tail
        walk [] items

    /// Whether the given message is there and has said nothing yet.
    let private unspoken (messageId: MessageId) (items: ConversationItem list) : bool =
        items
        |> List.exists (fun item ->
            item.MessageId = messageId
            && (match item.Content with
                | ItemContent.Message body -> body.Trim () = ""
                | ItemContent.Act _ | ItemContent.Stopped _ -> false))

    /// Why the given turn exists, if nobody asked for it. Matched on the turn id rather than
    /// taken on trust: a late event from a turn the wake did not start must not inherit the
    /// current one's reason.
    /// A reply's cause worth drawing: an item that is not the one this lands directly below.
    /// The detachment is read off `proj.Items` as it stands BEFORE the new item is appended,
    /// so its last entry is exactly what will render above: adjacent means it already sits
    /// under its cause, and the ref would say what the eye can see. An act's cause is kept
    /// whole instead (`ConversationItem.causeLinks`).
    ///
    /// The newest item is the HEAD of `Recent`, and asking it that way rather than through
    /// `Items` is the difference between a glance and reversing the transcript — which this
    /// does once per item folded, so reading it the other way was quadratic on its own.
    let private detached (cause: Cause option) (proj: ConversationProjection) : Cause option =
        match cause, List.tryHead proj.Recent with
        | Some (Cause.Item item), Some last when last.MessageId = item -> None
        | _ -> cause

    /// One act, appended where it happened. What it says is the act's own (`Act.phrase`),
    /// and every arm that notes something hands over the facts and nothing else — its cause
    /// among them, when it has one. `noted` is this with none.
    let private causedNote
        (messageId: MessageId)
        (causedBy: Cause option)
        (actor: ActorRef)
        (act: Act)
        (envelope: EventEnvelope<SessionEvent>)
        (proj: ConversationProjection)
        : ConversationProjection =
        { proj with
            Recent =
                { MessageId = messageId
                  Author = actor
                  Content = ItemContent.Act act
                  Status = Complete
                  Offset = envelope.Offset
                  Woke = None; CausedBy = causedBy }
                :: proj.Recent }

    let private noted messageId actor act envelope proj = causedNote messageId None actor act envelope proj

    /// An act that reports a watched change, wrapped to say it was noticed late when it was
    /// (`Lateness`). Keyed off the event's `WatchChanged` contract rather than its kind, so a
    /// watch of a new kind is late the same way the moment it keeps the contract.
    let private noticed (envelope: EventEnvelope<SessionEvent>) (act: Act) : Act =
        match Lateness.ofEnvelope envelope with
        | Some late -> Act.Noticed (late, act)
        | None -> act

    /// An act that RESOLVES a running one in place — the same id, a settled status and the
    /// facts of how it settled. A log written before the running half existed has no such
    /// item, so the act is appended as it always was; an id is either there or not, so the
    /// two readings never both fire.
    let private resolved
        (messageId: MessageId)
        (causedBy: Cause option)
        (actor: ActorRef)
        (act: Act)
        (status: ConversationItemStatus)
        (envelope: EventEnvelope<SessionEvent>)
        (proj: ConversationProjection)
        : ConversationProjection =
        if proj.Recent |> List.exists (fun i -> i.MessageId = messageId) then
            { proj with
                Recent =
                    proj.Recent
                    |> updateItem messageId (fun item -> { item with Content = ItemContent.Act act; Status = status }) }
        else
            { proj with
                Recent =
                    { MessageId = messageId
                      Author = actor
                      Content = ItemContent.Act act
                      Status = status
                      Offset = envelope.Offset
                      Woke = None; CausedBy = causedBy }
                    :: proj.Recent }

    let private wokeBy (turnId: AgentTurnId) (proj: ConversationProjection) : WakeReason option =
        match proj.WokenTurn with
        | Some (woken, reason) when woken = turnId -> Some reason
        | _ -> None

    /// The message the given turn was replying to, IF a ref is worth drawing — matched on
    /// the turn id like `wokeBy`, then suppressed when the trigger is the item this one lands
    /// directly below (`detached`).
    let private replyingTo (turnId: AgentTurnId) (proj: ConversationProjection) : Cause option =
        match proj.TriggeredTurn with
        | Some (triggered, trigger) when triggered = turnId -> detached (Some (Cause.Item trigger)) proj
        | _ -> None

    /// A turn stopping short — failed, or interrupted by a person — is an item of its own,
    /// ANCHORED WHERE IT STOPPED: after every command and call the turn made, because that
    /// is where it stopped, and whatever the turn had said stays where it said it.
    ///
    /// It used to be a status on what the turn had said, and for a failure a paragraph
    /// under it. That put the account of the ending ABOVE the work the ending ended — an
    /// agent message is created when the turn starts, and most turns then call tools for a
    /// while — so a reader saw "the session was restarted while this turn was running" as
    /// the agent's own closing sentence, three commands before anything went wrong; and a
    /// turn interrupted while it was calling tools rather than speaking left no trace at
    /// all. The message is left as what it said, complete: the turn ending is not a fact
    /// about those words, and late deltas still cannot reach an item that is not streaming.
    ///
    /// A turn's first message opens BEFORE the model has spoken, so a tool-only turn holds
    /// an empty item at the top of its own work. That placeholder is dropped rather than
    /// left standing empty over the stop — and the stop then carries the reply ref and the
    /// wake reason the placeholder would have, because a turn that stopped before saying
    /// anything is still a reply to what asked for it, and still a turn nobody asked for if
    /// it was woken. One that spoke carries both on what it said.
    let private stopTurn
        (envelope: EventEnvelope<SessionEvent>)
        (turnId: AgentTurnId)
        (stop: TurnStop)
        (status: ConversationItemStatus)
        (proj: ConversationProjection)
        : ConversationProjection =
        let stopped (attributed: bool) =
            let messageId =
                match MessageId.create (sprintf "agent-turn-%s-stopped" (AgentTurnId.value turnId)) with
                | Ok id -> id
                | Error e -> failwithf "derived message id invariant violated: %s" e
            { MessageId = messageId
              Author = ActorRef.Agent
              Content = ItemContent.Stopped stop
              Status = status
              Offset = envelope.Offset
              Woke = (if attributed then wokeBy turnId proj else None)
              CausedBy = (if attributed then replyingTo turnId proj else None) }
        let closed = Map.remove turnId proj.ActiveAgentMessages
        let spoke =
            Map.tryFind turnId proj.ActiveAgentMessages
            |> Option.bind (fun messageId ->
                // `Recent`, not `Items`: a search does not care which end it starts from,
                // and the streaming message this is looking for is at the near one.
                proj.Recent
                |> List.tryFind (fun item -> item.MessageId = messageId)
                |> Option.map (fun item -> messageId, (ConversationItem.said item).Trim () <> ""))
        match spoke with
        | Some (messageId, true) ->
            { proj with
                Recent = stopped false :: (proj.Recent |> updateItem messageId (fun item -> { item with Status = Complete }))
                ActiveAgentMessages = closed }
        | Some (messageId, false) ->
            { proj with
                Recent = stopped true :: (proj.Recent |> List.filter (fun item -> item.MessageId <> messageId))
                ActiveAgentMessages = closed }
        | None ->
            // The turn stopped before its message started: same item, same derivation —
            // there was simply never a placeholder to drop.
            { proj with Recent = stopped true :: proj.Recent; ActiveAgentMessages = closed }


    /// Fold one event into the projection. The match is total over `SessionEvent`, so
    /// adding a case forces this projection to account for it.
    let private applyEvent (proj: ConversationProjection) (envelope: EventEnvelope<SessionEvent>) : ConversationProjection =
        match envelope.Event with
        | PeerJoined _ -> proj     // presence, not a conversation item
        | PeerLeft _ -> proj       // presence, not a conversation item
        | SessionNamed _ -> proj   // what a chapter is CALLED, not something said in one
        | MessageSent m ->
            { proj with
                Recent =
                    { MessageId = m.MessageId
                      Author = Principal.toActor m.Author
                      Content = ItemContent.Message m.Body
                      Status = Complete
                      Offset = envelope.Offset
                      Woke = None; CausedBy = None }
                    :: proj.Recent }
        // Lifecycle; the item appears at `AgentMessageStarted`. What is remembered here is
        // only the turn's REASON for existing, which that item cannot re-derive: by the time
        // it arrives, the event that carried the reason is pages behind it.
        | AgentTurnStarted a ->
            { proj with
                WokenTurn =
                    match a.Cause with
                    | TurnCause.Woke reason -> Some (a.AgentTurnId, reason)
                    | TurnCause.TriggeredBy _ -> None
                TriggeredTurn =
                    match a.Cause with
                    | TurnCause.TriggeredBy trigger -> Some (a.AgentTurnId, trigger)
                    | TurnCause.Woke _ -> None }
        | AgentContextBuilt _ -> proj  // lifecycle
        // Environment lifecycle (Step 12) is session state, not conversation content.
        | EnvironmentNeedIdentified _
        | EnvironmentStartRequested _
        | EnvironmentStarted _
        | EnvironmentStartFailed _
        | EnvironmentStopRequested _
        | EnvironmentStopped _ -> proj
        // Command lifecycle (Step 13) projects into the command log, not the conversation.
        | CommandRequested _
        | CommandStarted _
        | CommandOutputReceived _
        | CommandCompleted _ -> proj
        // Terminals (Plan 13) project into `Projection`, and STILL do not fold here
        // (Plan 14, stage 1). This projection is what builds the agent's context, and the
        // agent already receives block outcomes through `Digest` — folding them in
        // here would double-feed the model and silently change what every turn reads.
        //
        // What Plan 14 reverses is the SCREEN, not the fold: a command someone ran does
        // appear in the chat now, interleaved by offset in `TimelineProjection`, which is a
        // view-level merge of this projection with the terminal one. The consequence is
        // deliberate and stated there — the human's chat and the agent's chat diverge.
        | SessionEvent.TerminalOpened _
        | SessionEvent.TerminalClosed _
        | SessionEvent.TerminalBlockStarted _
        | SessionEvent.TerminalBlockCompleted _
        | SessionEvent.TerminalBlockInterrupted _
        | SessionEvent.TerminalLeaseTaken _
        | SessionEvent.TerminalLeaseReleased _
        | SessionEvent.TerminalCommandRejected _
        | SessionEvent.TerminalIntegrationLost _
        | SessionEvent.TerminalIntegrationRestored _
        | SessionEvent.TerminalMarkedLate _
        | SessionEvent.TerminalTranscriptTruncated _ -> proj
        // Tool use (Plan 16, part C) does not fold here either, and for the same hazard in
        // a sharper form: the agent MADE the call and already has the result in its own
        // transcript, so feeding it back would be pure duplication. It folds into
        // `TimelineProjection` — the screen — and nowhere else.
        | SessionEvent.ToolUseStarted _
        | SessionEvent.ToolUseFinished _ -> proj
        // Repos (Plan 14) DO fold into the timeline — unlike terminals, a repo change is
        // a session-shaping act ("we are now working on X, on branch Y") that reads like
        // a sentence, carries no output stream, and is exactly what a joining human or
        // the agent's next turn needs to know. Each note rides the Process-minted
        // MessageId its event carries, and carries the event's FACTS: what it says is
        // `Act.phrase`'s, beside the event, and how a screen lays it out is the screen's.
        | SessionEvent.RepoAdded r -> proj |> noted r.MessageId r.Actor (Act.RepoAdded r) envelope
        | SessionEvent.RepoRemoved r -> proj |> noted r.MessageId r.Actor (Act.RepoRemoved r) envelope
        | SessionEvent.RepoBranchSwitched r -> proj |> noted r.MessageId r.Actor (Act.RepoBranchSwitched r) envelope
        // Named WorkSandboxes (Plan 15, stage 2) fold in for the repo notes' reason and
        // one more: forwarding a credential into a sandbox is the most consequential thing
        // a command here does, and the timeline is where the person whose credential it is
        // finds out. The line names WHAT was forwarded and WHOSE — never a value; the
        // event cannot carry one.
        // A sandbox COMING UP opens a running act — the same shape a streaming message has:
        // it holds its place while the work runs, and the `Started`/`StartFailed` below,
        // carrying this same MessageId, resolve it in place. This is the item that fills the
        // dead air a person used to see between "asks for" and "started sandbox".
        | SessionEvent.WorkSandboxStarting s ->
            { proj with
                Recent =
                    { MessageId = s.MessageId
                      Author = s.Actor
                      Content = ItemContent.Act (Act.SandboxStarting s)
                      Status = Running
                      Offset = envelope.Offset
                      Woke = None; CausedBy = s.CausedBy }
                    :: proj.Recent }
        // Resolve the running item this start's `WorkSandboxStarting` opened, in place. A
        // start from a log written before `Starting` existed has no such item — so it is
        // appended, exactly as it was before, and the two readings never both fire because
        // an id is either already there or not.
        | SessionEvent.WorkSandboxStarted s -> proj |> resolved s.MessageId s.CausedBy s.Actor (Act.SandboxStarted s) Complete envelope
        // The sandbox could not come up: resolve its running item to a failure in place. Like
        // the start above, an id already present is updated and an absent one appended, so a
        // failure whose `Starting` predates this code still reads.
        | SessionEvent.WorkSandboxStartFailed s ->
            proj |> resolved s.MessageId s.CausedBy s.Actor (Act.SandboxStartFailed s) Failed envelope
        // The other outcome of a declaration, beside the start above. What a repo asks for,
        // when it changed; a person's yes to it; and the file that could not be honoured.
        | SessionEvent.RepoCapabilitiesChanged c ->
            proj |> causedNote c.MessageId c.CausedBy c.Actor (Act.RepoCapabilitiesChanged c) envelope
        | SessionEvent.RepoCapabilitiesApproved a -> proj |> noted a.MessageId a.Actor (Act.RepoCapabilitiesApproved a) envelope
        | SessionEvent.RepoConfigRefused r ->
            proj |> causedNote r.MessageId r.CausedBy r.Actor (Act.RepoConfigRefused r) envelope
        | SessionEvent.RepoConfigWarned w ->
            proj |> causedNote w.MessageId w.CausedBy w.Actor (Act.RepoConfigWarned w) envelope
        | SessionEvent.WorkSandboxStopped s -> proj |> noted s.MessageId s.Actor (Act.SandboxStopped s) envelope
        // Where new terminals start (Plan 25) folds in for the repo notes' reason: it is a
        // session-shaping act everyone is affected by — the next terminal a PERSON opens
        // lands there too — and the timeline is the only place they would learn it.
        | SessionEvent.ShellProfileSet p -> proj |> noted p.MessageId p.Actor (Act.ShellProfileSet p) envelope
        // A file changed (the file verbs): the act the whole feature exists to put here — what
        // the agent used to leave as a `head`/`tail`/`mv` line, as a fact with a diff.
        | SessionEvent.FileChanged f -> proj |> noted f.MessageId f.Actor (Act.FileChanged f) envelope
        // An artifact shared: the act that invites everyone to LOOK at something. It folds into
        // the conversation rather than the timeline alone, because a later turn asking "what did
        // I show them" reads the same record a person does — and the address in the sentence is
        // what an agent quotes back to serve or supersede it.
        | SessionEvent.ArtifactShared a -> proj |> noted a.MessageId a.Actor (Act.ArtifactShared a) envelope
        // A tab opened or closed is not something SAID. It is a thing that happened to one
        // column of one screen, and the chat is the record of the conversation — an item
        // reading "opened artifacts/chart.png" under the message that shared it would be the
        // same fact twice, once as what the agent did and once as what the agent said about
        // what it did.
        | SessionEvent.TabOpened _
        | SessionEvent.TabClosed _ -> proj
        // A refusal reads in the timeline beside the acts that happened, attributed to the
        // person who said no rather than to the agent that asked (Plan 15, stage 3). Same
        // reason `BlockRejected` renders in the terminal: an act that simply vanishes is
        // indistinguishable from a bug.
        | SessionEvent.CommandRefused c -> proj |> noted c.MessageId c.RejectedBy (Act.CommandRefused c) envelope
        // Its sibling, said by the process: nobody refused it; it ran and did not succeed.
        | SessionEvent.GatedCommandFailed c -> proj |> noted c.MessageId ActorRef.System (Act.GatedCommandFailed c) envelope
        // A repo's `setup:`, said because nobody in the session asked for it. Every other
        // block on this timeline is somebody here running something; this one appears in a
        // terminal they will find busy, holding it until it finishes.
        | SessionEvent.SandboxSetupQueued q -> proj |> noted q.MessageId q.Actor (Act.SandboxSetupQueued q) envelope
        // A push spent somebody's credential. The person whose it was finds out HERE, which
        // is the reason the event exists: the block that pushed is on the timeline already,
        // but a block says what ran, not whose key went out on it.
        | SessionEvent.GitCredentialSpent g -> proj |> noted g.MessageId g.Actor (Act.CredentialSpent g) envelope
        // Reasoning is recorded and shown to NOBODY, and this case exists to say that is a
        // decision rather than an omission. It is a summary of what a model thought before it
        // acted: useful for asking why a turn did what it did, and not the same kind of thing
        // as anything else on this timeline — it was never said to anyone, nobody is
        // answerable for it, and a reader who met it beside speech would take it for speech.
        // The event is in the log for whoever goes looking; putting it on a screen is a
        // separate decision, with a person to make it.
        | SessionEvent.AgentThought _ -> proj
        // The MCP set changing (Plan 17). `ActorRef.System`, because nobody in the session
        // did it, and the DELTA only — the Process compares what it was last told, from
        // its own events, against the newly resolved set, so a boot, a reconnect and a
        // restart all emit nothing and only a genuine change by the operator is loud.
        | SessionEvent.McpServerAvailable m -> proj |> noted m.MessageId ActorRef.System (Act.McpServerAvailable m) envelope
        // That the session began is an item, for its pair's reason and one more: what the
        // first boot brings up (a repo's sandboxes) names it as its cause, and a cause is an
        // item a reader can be pointed at. It draws as a rule, not a line anybody said.
        | SessionEvent.SessionStarted s ->
            proj |> noted s.MessageId ActorRef.Session (Act.SessionStarted (s, envelope.Timestamp)) envelope
        // Coming BACK is a different matter: it says a stretch passed in which nothing ran,
        // which nothing else on the screen says.
        | SessionEvent.SessionResumed r ->
            proj |> noted r.MessageId ActorRef.Session (Act.SessionResumed (r, envelope.Timestamp)) envelope
        | SessionEvent.McpServerUnavailable m -> proj |> noted m.MessageId ActorRef.System (Act.McpServerUnavailable m) envelope
        // Watched pull requests fold in for the repo notes' reason: a watch is a
        // session-shaping act, and a transition is exactly what a joining human or the
        // agent's next turn needs to be told — the news arrived through no other door.
        | SessionEvent.PrWatched p -> proj |> noted (PrWatched.messageId p) (PrWatched.actor p) (Act.PrWatched p) envelope
        | SessionEvent.PrUnwatched p -> proj |> noted p.MessageId p.Actor (Act.PrUnwatched p) envelope
        // Attributed to the WATCHER rather than the envelope's System: the person whose
        // watch noticed is who the news is for, and whose name it should wear.
        | SessionEvent.PrTransitioned p ->
            proj |> noted p.MessageId (Principal.toActor p.Watcher) (noticed envelope (Act.PrTransitioned p)) envelope
        // Kept for whoever diagnoses a watch; the `pull_requests` query says it live.
        | SessionEvent.PrWatchReadability _ -> proj
        | AgentMessageStarted a ->
            // A message that follows another is that other one's close: the model has moved
            // on, so what the antecedent streamed is what it said. Only a streaming item
            // closes this way — one already failed or interrupted keeps its ending.
            let closed =
                match a.Antecedent with
                | Some previous ->
                    proj.Recent
                    |> updateItem previous (fun item ->
                        if item.Status = Streaming then { item with Status = Complete } else item)
                | None -> proj.Recent
            { proj with
                Recent =
                    { MessageId = a.MessageId
                      Author = ActorRef.Agent
                      Content = ItemContent.Message ""
                      Status = Streaming
                      Offset = envelope.Offset
                      // Why the turn ran is attribution for the TURN, said once where it
                      // begins; a follower's antecedent already wears it.
                      Woke = (match a.Antecedent with None -> wokeBy a.AgentTurnId proj | Some _ -> None)
                      // The reply ref sits on the turn's first message for the same
                      // reason — a follower answers its antecedent, not the trigger.
                      CausedBy = (match a.Antecedent with None -> replyingTo a.AgentTurnId proj | Some _ -> None) }
                    :: closed
                ActiveAgentMessages = Map.add a.AgentTurnId a.MessageId proj.ActiveAgentMessages }
        // The first word anchors the item (see `Offset`); every later one only lengthens it.
        // A completion that carries a body nobody streamed — a turn whose only words arrived
        // whole — is that message's first word too, and anchors it the same way.
        | AgentMessageDelta a ->
            { proj with
                Recent =
                    proj.Recent
                    |> updateItem a.MessageId (fun item ->
                        match item.Content with
                        | ItemContent.Message body when item.Status = Streaming ->
                            { item with
                                Content = ItemContent.Message (body + a.Delta)
                                Offset = if body = "" then envelope.Offset else item.Offset }
                        | ItemContent.Message _
                        | ItemContent.Act _
                        | ItemContent.Stopped _ -> item) }
        // A message that ends having said nothing is not drawn as an empty bubble: among
        // several people, saying nothing is how the agent answers a conversation that was not
        // for it, and a blank line under its name would read as a reply that went missing.
        // Only a message that never spoke goes — anything streamed stands as what was said.
        | AgentMessageCompleted a when a.Body.Trim () = "" && unspoken a.MessageId proj.Recent ->
            { proj with
                Recent = proj.Recent |> removeItem a.MessageId
                ActiveAgentMessages = Map.remove a.AgentTurnId proj.ActiveAgentMessages }
        | AgentMessageCompleted a ->
            { proj with
                Recent =
                    proj.Recent
                    |> updateItem a.MessageId (fun item ->
                        let spoken =
                            match item.Content with
                            | ItemContent.Message body -> body <> ""
                            | ItemContent.Act _
                            | ItemContent.Stopped _ -> true
                        { item with
                            Content = ItemContent.Message a.Body
                            Status = Complete
                            Offset = if not spoken && a.Body <> "" then envelope.Offset else item.Offset })
                ActiveAgentMessages = Map.remove a.AgentTurnId proj.ActiveAgentMessages }
        | AgentTurnInterrupted a -> stopTurn envelope a.AgentTurnId (TurnStop.Interrupted a.RequestedBy) Complete proj
        | AgentTurnFailed a -> stopTurn envelope a.AgentTurnId (TurnStop.Failed a.Reason) Failed proj

    /// Fold ordered event envelopes into a conversation projection.
    ///
    /// `appliedThrough` is the highest offset already folded in; events at or below it are
    /// skipped, so re-applying overlapping pages is idempotent on offset. Returns the
    /// updated projection together with the new high-water offset.
    ///
    /// The signature deliberately takes only events — never synced/draft state — so the
    /// conversation can never depend on collaborative editing state.
    let applyEvents
        (appliedThrough: EventOffset option)
        (events: EventEnvelope<SessionEvent> list)
        (projection: ConversationProjection)
        : ConversationProjection * EventOffset option =
        events
        |> List.fold
            (fun (proj, highWater) envelope ->
                let beyondApplied =
                    match highWater with
                    | Some o -> EventOffset.value envelope.Offset > EventOffset.value o
                    | None -> true
                if beyondApplied then
                    applyEvent proj envelope, Some envelope.Offset
                else
                    proj, highWater)
            (projection, appliedThrough)

    /// Every artifact this session holds, at its latest version, most recently shared first.
    ///
    /// DERIVED rather than kept: the share is already an act in `Items`, and a second copy in
    /// the projection would be a list that could disagree with the timeline it was folded from.
    /// A reader wants the newest version of each name — the older ones are still addressable,
    /// and the way to ask for one is the act that put it there, which is on the timeline where
    /// it happened.
    ///
    /// It lives here rather than in the view because it is the answer to "what has been shared",
    /// which is a question about the log — and a cheap test can ask it without a browser.
    let artifacts (proj: ConversationProjection) : ArtifactShared list =
        proj.Items
        |> List.choose (fun item ->
            match item.Content with
            | ItemContent.Act (Act.ArtifactShared a) -> Some (EventOffset.value item.Offset, a)
            | _ -> None)
        // Later in the log wins: shares of one name arrive in the order they were made, so the
        // last mention is the newest version without comparing sequence numbers here.
        |> List.fold (fun byName (at, a) -> Map.add (ArtifactRef.name a.Ref) (at, a) byName) Map.empty
        |> Map.toList
        |> List.map snd
        // Newest first, because the list is read to find the thing just shared far more often
        // than to find one from an hour ago.
        |> List.sortByDescending fst
        |> List.map snd

    /// Every sandbox this session has STARTED and not since stopped, in the order the starts
    /// arrived — which is the order the repo's own file declared them in, because that is the
    /// order they are brought up.
    ///
    /// Derived rather than kept, for `artifacts`' reason: the start is already an act in
    /// `Items`, and a second copy would be a list that could disagree with the timeline it was
    /// folded from. A reader asking "where could something run here" and a reader scrolling
    /// past "started sandbox octo/hello:dev" are reading one log.
    ///
    /// `default` is NOT in it, and that is the distinction the name carries: every session has
    /// `default` from boot, so nothing ever started it and no event says so. A surface that
    /// offers somewhere to run offers `default` because the session exists, and offers these
    /// because they came up.
    ///
    /// Only `SandboxStarted` admits one. A sandbox still coming up (`SandboxStarting`) has no
    /// container yet, and one that failed resolves that same item to the failure — so a
    /// started sandbox is one that reported being up, never one that was asked for.
    let startedSandboxes (proj: ConversationProjection) : WorkSandboxStarted list =
        proj.Items
        |> List.choose (fun item ->
            match item.Content with
            | ItemContent.Act (Act.SandboxStarted s) -> Some (Choice1Of2 s)
            | ItemContent.Act (Act.SandboxStopped s) -> Some (Choice2Of2 s)
            | _ -> None)
        // A stop takes the sandbox out and a later start puts it back, so the fold has to see
        // both in log order: the last word about a sandbox is what it is.
        |> List.fold
            (fun running ->
                function
                | Choice1Of2 (s: WorkSandboxStarted) when SandboxRef.defaultRef = s.Sandbox -> running
                | Choice1Of2 s -> (running |> List.filter (fun r -> r.Sandbox <> s.Sandbox)) @ [ s ]
                | Choice2Of2 (s: WorkSandboxStopped) -> running |> List.filter (fun r -> r.Sandbox <> s.Sandbox))
            []

/// What a person in this session still has to decide about.
///
/// Folded from the events by BOTH sides — the Process to know what to gate, a client to know
/// what to offer — so the prompt somebody sees and the sandbox that is waiting are two
/// readings of one log rather than two answers that can disagree.
///
/// A repo is pending when the last thing it was recorded as asking for is sensitive, and no
/// approval since names exactly that set. "Exactly" is the whole rule: a repo that widens
/// what it asks for is a new decision, not one the old yes silently covers.
module RepoApprovals =

    /// What each repo was last recorded as asking for, and whether anybody still has to
    /// decide about it. A fold state rather than a function over the whole log, because a
    /// client sees the log in PAGES and re-reading all of it per page is the cost this
    /// projection exists to avoid.
    type Pending = private Pending of Map<string, RepoRef * string list * bool>

    let empty : Pending = Pending Map.empty

    let apply (Pending state) (events: SessionEvent list) : Pending =
        events
        |> List.fold
            (fun state event ->
                match event with
                | SessionEvent.RepoCapabilitiesChanged c ->
                    Map.add (RepoRef.value c.Repo) (c.Repo, c.Granted, c.Sensitive) state
                | SessionEvent.RepoCapabilitiesApproved a ->
                    match Map.tryFind (RepoRef.value a.Repo) state with
                    // Approval settles the set it NAMES. An approval of something else leaves
                    // the ask standing, which is what makes a widening a fresh decision rather
                    // than one an old yes silently covers.
                    | Some (repo, granted, _) when granted = a.Granted ->
                        Map.add (RepoRef.value a.Repo) (repo, granted, false) state
                    | _ -> state
                | _ -> state)
            state
        |> Pending

    /// Who is still waiting on somebody, in a stable order.
    let waiting (Pending state) : (RepoRef * string list) list =
        state
        |> Map.toList
        |> List.choose (fun (_, (repo, granted, pending)) -> if pending then Some (repo, granted) else None)
        |> List.sortBy (fun (repo, _) -> RepoRef.value repo)

    /// The whole log at once — the Process's reading, where there are no pages.
    let pending (events: SessionEvent list) : (RepoRef * string list) list = apply empty events |> waiting
