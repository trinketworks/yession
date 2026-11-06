namespace Yession.Domain.Away

open Yession.Domain
open Yession.Domain.Access

/// How far a person lets the agent use their connection while they are away: the two rows of
/// the ask card. "Off" is not an answer to an ask, so it is not a case here: an ask is settled
/// by a grant or by a dismissal, never by a grant of nothing.
[<RequireQualifiedAccess>]
type OfflineAllowance =
    | ThisSession
    | AllMySessions

/// The agent asked the person whose turn it was to let this session use their own connection
/// while they are away (`ask_offline_access`).
///
/// The agent only ASKS. The grant is the person's own act, from their own client, through the
/// same route the session settings use; nothing the agent can call changes it.
[<RequireQualifiedAccess>]
type OfflineAccessAsked =
    { MessageId : MessageId
      /// Whose connection: always a signed-in user, because only a user's own connection
      /// (`UserScope`) takes an offline grant.
      Person : UserId
      Connection : ConnectedService
      /// The agent's reason, in its own words, for the person to read on the card.
      Why : string }

/// How an ask ended.
[<RequireQualifiedAccess>]
type OfflineAccessOutcome =
    /// The person's connection now reads as allowed in this session. The session saw it on
    /// the Manager's status stream, so this covers a grant from the card and one from the
    /// settings alike.
    | Allowed of OfflineAllowance
    /// The person dismissed the card. Their grant is unchanged.
    | Dismissed

/// An ask settled. Attributed to the person: allowing and dismissing are both their acts.
/// Owes the agent a turn (`WakeReason.OfflineAccessSettled`), so it learns the answer.
[<RequireQualifiedAccess>]
type OfflineAccessSettled =
    { MessageId : MessageId
      Person : UserId
      Connection : ConnectedService
      Outcome : OfflineAccessOutcome }

module OfflineAccessAsked =

    /// "asked Ada to let it use their GitHub while they are away".
    let phrase (a: OfflineAccessAsked) : Phrase =
        Phrase.deed
            "asked"
            [ Segment.Ref (EntityRef.Actor (UserRef a.Person))
              Segment.Text (sprintf " to let it use their %s while they are away" (ConnectedService.name a.Connection)) ]

    /// The agent's reason, as the card shows it.
    let particulars (a: OfflineAccessAsked) : Phrase list = [ Phrase.text a.Why ]

module OfflineAccessSettled =

    let phrase (s: OfflineAccessSettled) : Phrase =
        let connection = ConnectedService.name s.Connection
        match s.Outcome with
        | OfflineAccessOutcome.Allowed OfflineAllowance.ThisSession ->
            Phrase.deed "allowed" (Phrase.text (sprintf "the agent to use their %s in this session while they are away" connection))
        | OfflineAccessOutcome.Allowed OfflineAllowance.AllMySessions ->
            Phrase.deed "allowed" (Phrase.text (sprintf "the agent to use their %s in all their sessions while they are away" connection))
        | OfflineAccessOutcome.Dismissed ->
            Phrase.deed "dismissed" (Phrase.text (sprintf "the ask to use their %s while they are away" connection))
