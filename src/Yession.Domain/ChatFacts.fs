namespace Yession.Domain.Chat

open Yession.Domain

/// The facts a conversation records — a message drained from the queue and sent, and a command the gate refused.
///
/// These sit BELOW `SessionEvent`, because the union names them, while the
/// projections that fold that union sit above it — so Chat spans the event
/// spine rather than living on one side of it.
type MessageSent =
    { MessageId : MessageId
      /// The queue entry this message was consumed from (Phase 3): the durable link
      /// from doc-world to event-world, and the drain's exactly-once dedup key.
      /// `None` for messages that predate the queue.
      QueueId : QueueId option
      /// Who said it. A `Principal`, because a message is drained from the queue a peer
      /// wrote to and every peer is one — and because the turn a message starts runs on its
      /// author's credential, which the type then guarantees is somebody's.
      Author : Principal
      Body : string }
/// A command refused at its gate (Plan 15, stage 3; Plan 23: the gate is the classifier).
/// The mirror of `TerminalCommandRejected`, and it exists for that event's reason: a refusal
/// that simply vanishes is indistinguishable from a bug — to anyone reading the record and
/// to the model, which will otherwise try the same thing another way.

and CommandRefused =
    { MessageId : MessageId
      /// The pending act's id — the handle the agent was given, so the refusal it reads
      /// back joins the request it made.
      QueueId : QueueId
      /// The MCP tool name, which is both what the model called and what the gate was
      /// configured against.
      Tool : string
      /// The arguments as they were shown to the person who refused them. Rendered, not
      /// raw: what the log should record is what was on the screen.
      Summary : string
      /// Who proposed it. Always the agent today (commands are agent-only), and carried
      /// anyway because `yession.yaml` will propose them too.
      Author : ActorRef
      RejectedBy : ActorRef
      Reason : string option }

/// A command its gate released that then RAN AND FAILED, for an author with no other way to
/// hear so. The agent reads a failure back in its tool result and everyone else sees it on
/// the `ToolUseFinished` line, which is why the gate records nothing for its commands; a
/// person's command has no tool result, so without this its failure went nowhere — the
/// launch surface's clone that could not reach the repo would simply not have happened.
/// `CommandRefused`'s sibling, and distinct from it on purpose: refused is somebody saying
/// no, failed is the act itself not succeeding, and a reader of the record acts differently
/// on each.
and GatedCommandFailed =
    { MessageId : MessageId
      Tool : string
      /// The arguments as a person read them (`add_repo octo/hello`), the same rendering the
      /// refusal records.
      Summary : string
      Author : ActorRef
      Reason : string }

/// What a naming pass is about (Plan 25). One case today; the session title is the other one
/// coming, and it is a case here rather than a second feature because the question — is what
/// this is called still the best short name for it — is the same question about both.
and [<RequireQualifiedAccess>] NamingSubject =
    | Chapter of MessageId
    /// What the whole session is called. The same question as a chapter's, asked of
    /// everything rather than of a stretch — which is why it is a case here and not a
    /// second feature with a second set of rules to keep in step.
    | Title

/// What the session settled a subject's name to, and how much it had read to settle it.
///
/// A durable fact rather than a set held in memory, because the two questions the next pass
/// asks are both about the past: may this still be written over, and has enough been said
/// since to be worth asking again. A process that kept those in a field would forget both on
/// restart — and forgetting the first is the dangerous half, since it is the only thing
/// standing between a model and a name somebody wrote themselves.
and SessionNamed =
    { Subject : NamingSubject
      /// The name that stands after this pass: what was written, or what was already there
      /// when the write lost its race or the answer was unusable. It is exactly what the
      /// session may write over next time — anything else the doc holds is somebody's own
      /// words, and those end the matter for good.
      Name : string
      /// How many conversation items the ask read. What makes the next pass a re-reading
      /// rather than a repetition: the material has to have DOUBLED before it is worth
      /// asking again, so a long session costs a handful of calls rather than one a message,
      /// and the second message of a session that opened with "run tests" still counts.
      Read : int
      /// Whose authority it ran on — the session's creator, or nobody on an unattributed
      /// deployment. `Authority.AgentFor` cannot say "nobody", and that is the honest gap
      /// rather than a case to invent: an unattributed launch has no person behind it.
      OnBehalfOf : Principal option }
