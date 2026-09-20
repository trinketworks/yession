namespace Yession.Domain.Terminals

open System
open Yession.Domain

/// The facts a terminal records — opening and closing, who holds its stdin, the blocks it ran, and what its transcript could not keep.
///
/// These sit BELOW `SessionEvent`, because the union names them, while the
/// projections that fold that union sit above it — so Terminals spans the event
/// spine rather than living on one side of it.
/// The human label a terminal carries so a session with four of them is navigable. Never
/// unique, never an identifier — `TerminalId` is what names a terminal.
///
/// It is a type rather than a `string` because a wrong pick between two `string` fields
/// type-checks and says nothing: `.Title` is carried by records in four namespaces and
/// `.Reason`, `.Command` and `.Label` by others, so wherever a receiver's type is not
/// inferred, F# resolves the label from whatever is in scope and compiles either way. A
/// label that has its own type makes the wrong pick fail to compile, which is the only
/// reason the one wrong pick this codebase HAS found was ever found.
///
/// The rules live here rather than at the caller that used to hold them. Opening a terminal
/// normalised its title inline — trim, and an empty one becomes `terminal` — which is a rule
/// every other way of opening one had to remember separately.
type TerminalTitle = private TerminalTitle of string

module TerminalTitle =

    /// Long enough for any label a person would write, short enough to bound what a peer can
    /// put in a durable event. A title is appended to the log on `TerminalOpened` and
    /// replayed for ever, and it is rendered in a tab strip; neither wants a novel.
    [<Literal>]
    let MaxLength = 120

    /// What an unlabelled terminal is called. Offering no title is not an error — most
    /// terminals are opened by a person who did not stop to name one.
    let fallback : TerminalTitle = TerminalTitle "terminal"

    /// Parse a title off a command or the wire. Trims; an empty or whitespace title becomes
    /// `fallback` rather than an error, because a peer pressing New Terminal has not done
    /// anything wrong.
    ///
    /// Too long IS an error, and is rejected rather than truncated: a title is what somebody
    /// typed, and silently keeping a prefix of it in a durable event would be inventing a
    /// fact. The one caller that can be handed an over-long title is a peer command, which
    /// already has `CommandRejected` to say so with.
    let create (raw: string) : Result<TerminalTitle, string> =
        let trimmed = if isNull (box raw) then "" else raw.Trim ()
        if trimmed = "" then Ok fallback
        elif trimmed.Length > MaxLength then
            Error (sprintf "a terminal title is at most %d characters" MaxLength)
        else Ok (TerminalTitle trimmed)

    let value (TerminalTitle t) = t

    /// The longest title worth deriving from prose we wrote ourselves. Shorter than
    /// `MaxLength` on purpose: an agent names a terminal after the reason it opened one, and
    /// a sentence makes a bad tab.
    [<Literal>]
    let ProseLength = 60

    /// A title derived from prose the agent wrote, rather than a label a person chose.
    ///
    /// Total, and truncating — the opposite of `create` on both counts, deliberately. The
    /// agent's own sentence being long is not a mistake anyone can correct, so refusing the
    /// terminal over it would fail a tool call for something the caller cannot fix; and
    /// keeping a readable prefix loses nothing, because the reason is recorded in full on
    /// the act that opened the terminal. A person's deliberate title gets the opposite
    /// treatment for the same reason read the other way: it is theirs, so it is refused
    /// rather than quietly shortened.
    let fromProse (raw: string) : TerminalTitle =
        let trimmed = if isNull (box raw) then "" else raw.Trim ()
        if trimmed = "" then fallback
        elif trimmed.Length > ProseLength then TerminalTitle (trimmed.Substring (0, ProseLength - 3) + "...")
        else TerminalTitle trimmed

type TerminalOpened =
    { TerminalId : TerminalId
      /// Who asked for it. A terminal is opened by a peer or by the agent, and which one
      /// decides nothing about how it behaves — it is attribution, for the audit.
      OpenedBy : ActorRef
      /// A human label, so a session with four terminals is navigable. Never unique.
      Title : TerminalTitle
      /// Which of the session's WorkSandboxes it runs in (Plan 15, stage 2). Named on the
      /// OPEN event because it is fixed for the terminal's life, and because a replayed
      /// log has to be able to bring the terminal back up in the same sandbox it was in.
      /// A log written before named sandboxes decodes to `default`, which is where those
      /// terminals were.
      ///
      /// `None` means the terminal runs in NO sandbox: its bytes come from a stream
      /// somebody else produces (Plan 16, part D). Optional rather than defaulted, because
      /// saying an attached serial port is in `default` would be inventing a fact — and the
      /// two consumers both need the difference: the panel says where a terminal is, and
      /// the block runner picks an environment by it.
      Sandbox : SandboxRef option
      /// Can this terminal's stream be asked for again (Plan 19, step 4)?
      ///
      /// The provider's claim about its own tool, recorded here because a person meets this
      /// question at the WORST moment to go looking for the answer: the stream has ended,
      /// the terminal is closed, and what they want to know is whether there is a way back.
      /// False for a shell, which has no provider to ask, and for a log written before this
      /// field existed.
      Renewable : bool }

and TerminalClosed =
    { TerminalId : TerminalId
      Reason : string }
/// A peer took the terminal's stdin (Plan 13, stage 2e) — live mode entered, or STOLEN from
/// whoever held it before. One event for both, because they are the same fact: from this
/// moment these keystrokes are that peer's. Collaborators are trusted, so a steal needs no
/// permission; what it needs is to be on the record, which is this.

and TerminalLeaseTaken =
    { TerminalId : TerminalId
      By : ActorRef
      /// The transcript line index at which this stretch of live mode begins (Plan 14,
      /// stage 1). A block records the range it produced and a lease stretch did not, so
      /// an interactive stretch had no replay bounds at all — and nothing can derive them
      /// afterwards, because only the Process knows where the transcript stood when the
      /// lease changed hands.
      FromSeq : int }
/// The terminal is back in block mode. Appended when the holder releases it, when a peer
/// steals it (the previous holder's lease ends), and when the holder's CONNECTION drops —
/// a lease held by someone who is gone is the one hold nobody should have to clear by hand.

and TerminalLeaseReleased =
    { TerminalId : TerminalId
      /// Who held it. Kept because the interesting question afterwards is whose keystrokes
      /// the bracketed transcript range belongs to, and an empty release cannot answer it.
      Was : ActorRef
      Reason : TerminalLeaseEnd
      /// One past the last transcript line of the stretch that just ended — the other half
      /// of the range `TerminalLeaseTaken.FromSeq` opened.
      ToSeq : int }
/// Why a lease ended. Distinguished because they read differently in a log: a release is a
/// person finishing, a steal is another person taking over, a drop is nobody deciding
/// anything at all, and an idle reclaim is a person who is still here and has stopped.

and TerminalLeaseEnd =
    | LeaseReleased
    | LeaseStolen of by: ActorRef
    | LeaseHolderGone
    /// Reclaimed by the idle timeout (Plan 13, stage 3c): the holder is still connected and
    /// simply stopped typing while something was queued behind them. Its own case because the
    /// question a reader asks afterwards — "did nick finish, drop out, or just wander off?" —
    /// has three different answers, and answering it as `LeaseReleased` would say the holder
    /// decided something they did not.
    | LeaseIdle

and TerminalBlockStarted =
    { TerminalId : TerminalId
      BlockId : BlockId
      /// The queue entry this block was drained from, when it came through the composer.
      /// `None` for a block the Session Process ran on its own behalf.
      QueueId : QueueId option
      /// The three parties behind the command: who wrote it, whose credential it ran on when
      /// that was not their own, and who released it when the terminal's mode required an
      /// approval. One value rather than three fields, because they are one question — and
      /// because the answer to its middle third went missing here once (Plan 20).
      ///
      /// The owner matters beyond the audit: a WOKEN turn has no triggering message to
      /// resolve its authority from, and the log is the only thing it can read. Absent means
      /// no turn can be woken by this block — an unresolvable owner runs on NOTHING rather
      /// than on somebody else's credential.
      Authority : Authority
      /// The command line, snapshotted from the collaborative draft at drain time and
      /// immutable thereafter — exactly as `MessageSent` snapshots a message body.
      Command : string
      /// The transcript line index at which this block's output begins.
      FromSeq : int
      /// Whether the agent asked for this one to run in the BACKGROUND (Plan 20, stage 2):
      /// it did not hold the turn open, and its completion is something the agent wants to
      /// be told about.
      ///
      /// On the block rather than only in the queue entry it came from, because that is what
      /// makes "is a wake due" a pure fold over the log: the doc's entry is gone the moment
      /// the block starts, and a scheduling decision that depended on it would be a decision
      /// a restart could not re-derive.
      Background : bool }
/// A queued command a peer refused (Plan 13, stage 2a). The other half of the approval
/// gate: a log that records every yes and no no is the weaker thing wearing the stronger
/// thing's face, and "the agent proposed this and a human said no" is the more interesting
/// half of the two.
///
/// Deliberately NOT a `SessionCommand`. A command frame from a peer that drops mid-flight
/// is lost, and the log stays the Session Process's alone to write — so a peer writes
/// `RejectedBy` on the doc entry and the drain, which is already the queue's single
/// consumer, observes it and appends this.

and TerminalCommandRejected =
    { TerminalId : TerminalId
      QueueId : QueueId
      /// Minted here, exactly as `TerminalBlockStarted` mints one, rather than derived by
      /// each client's fold from the `QueueId`. A `BlockId` names a proposed command and
      /// its outcome, not a process — so a refusal has one, and a handle that is
      /// addressable later does not depend on a derivation rule living nowhere in the data.
      BlockId : BlockId
      /// Whose command it was, and on whose authority. Usually the agent's on a turn
      /// human's; that is the point of recording this — and recording the whole authority
      /// rather than the author alone is what lets the projection put a refused block
      /// beside a started one without inventing an agent act that names nobody.
      Authority : Authority
      RejectedBy : ActorRef
      /// The command line, snapshotted because the doc entry is deleted immediately after.
      /// A record saying *something* was rejected is not a record.
      Command : string
      Reason : string option }
/// The shell stopped emitting marks (Plan 13, stage 2f). `exec sh`, or an image whose shell
/// drops into another, replaces the process we instrumented while the pty stays open — so
/// `Exited` never fires and the marks simply stop.
///
/// Durable rather than runtime-only state, for the same reason `TerminalTranscriptTruncated`
/// is: this is a GAP in what the record can say. From here the Process cannot tell when a
/// command started or finished, so "we no longer know when this finished" is a fact about the
/// audit trail and belongs in it — not merely on a screen somebody may not be looking at.

and TerminalIntegrationLost =
    { TerminalId : TerminalId
      /// The block that was open when it happened, if one was. It stays open — its `ToSeq`
      /// and exit code are exactly what was lost — and naming it here is what lets a reader
      /// tell an unbounded block from a running one.
      BlockId : BlockId option
      /// What the detector had in hand when it fired. `None` only for a log written before
      /// it said — there is no honest value to invent for a moment nobody recorded.
      Evidence : LostEvidence option }

/// What was known at the moment a terminal was declared lost — enough to say, off the log
/// alone, which side was silent. The fact without it read the same for a shell that had
/// been replaced and one that answered ten seconds late over a container's stream while
/// this process was itself late to look: both said *lost*, one of them wrongly, and the
/// diagnosis took the cast file, the event log and a stopwatch.

and LostEvidence =
    { /// When the block's line was handed to the pty.
      WrittenAt : DateTimeOffset
      /// When the start mark was due — the write plus the window. The envelope's own
      /// timestamp is when the detector actually ran, so the gap between the two is THIS
      /// process's lateness, and it is the one number that separates a shell that was slow
      /// from a process that was slow to notice.
      Due : DateTimeOffset
      /// What the shell printed between the write and the detector firing, control bytes
      /// escaped and the whole bounded. Empty says the shell had not answered at all; the
      /// line's echo with the command's output under it says it ran the command and marked
      /// nothing — which is the shell that needs re-arming.
      Said : string }

/// A start mark that arrived AFTER its terminal was declared lost. The shell does mark; it
/// — or this process — was slow, and the block it starts will end with a `D` the drain can
/// act on. Recorded because the log otherwise shows a lost terminal completing a block with
/// an exit code and says nothing about the contradiction.
///
/// It does not clear the loss. Only a person re-arming does (`TerminalIntegrationRestored`),
/// and whether a late mark should is a decision this fact exists to inform, not to take.

and TerminalMarkedLate =
    { TerminalId : TerminalId
      BlockId : BlockId
      /// When the block's line was handed to the pty; the envelope's timestamp is when the
      /// mark arrived, so the difference is how late the shell answered.
      WrittenAt : DateTimeOffset }
/// Marking is back (Plan 13, stage 2f): a peer used the re-arm control and the shell that is
/// actually there now answered our instrumentation.

and TerminalIntegrationRestored =
    { TerminalId : TerminalId }

and TerminalBlockCompleted =
    { TerminalId : TerminalId
      BlockId : BlockId
      Result : CommandResult
      /// The transcript line index one past this block's last output line.
      ToSeq : int }

and TerminalTranscriptTruncated =
    { TerminalId : TerminalId
      BlockId : BlockId option
      /// Output this terminal produced and the transcript did NOT keep. Recorded so a
      /// gap in an audit trail is a stated fact, never a silent one.
      DroppedBytes : int }

/// A push from a block went out on somebody's github credential — the last hop of the chain
/// from a person's message to a request at github.com, and the one hop nothing above it
/// records: `TerminalBlockStarted` says whose act a block was, but a block that pushes in
/// the background pushes after it completed, and a block says what ran, not whose key went
/// out on it. One per push, on the request that carried the pack; whether github.com then
/// took it is git's to print. Never a value: the same register the log names people in.
and GitCredentialSpent =
    { MessageId : MessageId
      Sandbox : SandboxRef
      Terminal : TerminalId
      /// The block whose loan it was — or none, for a push a lease holder typed: their
      /// keystrokes are no block, and the loan was lent to the lease itself.
      Block : BlockId option
      /// Whose credential — the block's act resolved by Plan 08 precedence, which is a
      /// person's or the deployment's own.
      Owner : CredentialFor
      /// The repository the push named.
      Repo : RepoRef
      /// Who acted: the block's author, so an agent's push on Ada's turn reads as the
      /// agent's act spending Ada's credential, which is what happened.
      Actor : ActorRef }

module GitCredentialSpent =

    /// A push spent somebody's credential. The person whose it was finds out HERE, which is
    /// the reason the event exists: the block that pushed is on the timeline already, but a
    /// block says what ran, not whose key went out on it. The sentence leads with the act
    /// and names the person it was done for, the way the actor column reads — "agent pushed
    /// … on behalf of user:ada" — rather than with the credential, which is the mechanism.
    /// "Pushed to" is the request that went out, not github.com's answer to it: a branch
    /// protection or a rejected ref is git's to print, in the block.
    ///
    /// The repository and the person are REFERENCES, not words: the prose reader spells them
    /// (`github:octo/hello`, `user:ada`) and a screen draws them as it draws that repository
    /// and that person everywhere else. The deployment is not a party a screen draws, so it
    /// stays a word.
    let phrase (g: GitCredentialSpent) : Phrase =
        let owner =
            match g.Owner with
            | CredentialFor.Person person -> Segment.Ref (EntityRef.Actor (Principal.toActor person))
            | CredentialFor.Deployment -> Segment.Text (CredentialFor.token CredentialFor.Deployment)
        // Typed under a lease: no block on the timeline says what ran, so this line says
        // where it was typed.
        let typed =
            match g.Block with
            | Some _ -> []
            | None -> [ Segment.Text ", holding the terminal" ]
        [ Segment.Text "pushed to "; Segment.Ref (EntityRef.Repo g.Repo); Segment.Text " on behalf of "; owner ] @ typed
