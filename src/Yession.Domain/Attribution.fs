namespace Yession.Domain

/// Which durable user a peer connection belongs to, folded from the log — shared by
/// the Session Process (which stamps chat/act authorship with it) and the client (which
/// resolves a `UserRef` author back to a name for display). One fold, one decision rule,
/// used on both sides of the wire, rather than a server copy and a client guess that can
/// drift apart — which is exactly how "you" ended up with two names on one screen.
module Attribution =

    /// Both directions of the one decision, carried together. A `PeerJoined` that
    /// attributes a peer to a user is a single fact; keeping its two readings — "who is
    /// this peer" and "which peer is this user's current one" — in one record folded by
    /// one function means there is only one place that fact gets recorded, instead of two
    /// folds over the same events that could (and did, once) drift out of step.
    type State =
        { /// Peer → user, for every join a Manager-verified authentication strategy
          /// attributed. A peer that never appears here is unattributed (trust-localhost,
          /// or simply hasn't joined yet) and is identified only by its `PeerId`.
          PeerUsers : Map<PeerId, UserId>
          /// User → their most recently joined attributed peer. A user can be attributed
          /// through more than one `PeerJoined` over the life of a session — every
          /// reconnect (a new tab, a dropped connection, simply having joined before)
          /// mints a new `PeerId` — and the last one to join is the one whose
          /// `DisplayName` is current. `PeerUsers` alone cannot answer "which one is
          /// current": its keys are peers, so several can map to the same user with
          /// nothing to say which is newest. This is that answer, kept up to date by the
          /// same fold.
          UserPeers : Map<UserId, PeerId>
          /// The first user this session ever attributed — whose session it is.
          ///
          /// Folded here rather than derived, because it cannot be derived: the maps above
          /// are keyed, and a map has no first. It is a fact about the ORDER of the log, and
          /// the fold is the only thing that sees that order.
          ///
          /// `None` under an unattributed deployment, which is the honest answer rather than
          /// a missing one: `--auth localhost` grants one principal and verifies nobody, so
          /// there is no user whose session this is. What reads this for a credential turns
          /// that into `CredentialFor.Deployment`, which is the scope such a launch holds.
          Creator : UserId option }

    let empty : State = { PeerUsers = Map.empty; UserPeers = Map.empty; Creator = None }

    /// The single-event step: fold one more event into an existing state, updating both
    /// directions from the one match arm. This is what a live process replays
    /// incrementally as events arrive; `ofEvents` below is just this applied to a whole
    /// replay starting from empty.
    let applyEvent (acc: State) (event: SessionEvent) : State =
        match event with
        | PeerJoined { PeerId = peer; User = Some user } ->
            { PeerUsers = Map.add peer user acc.PeerUsers
              UserPeers = Map.add user peer acc.UserPeers
              // First wins, for ever. A session's creator is not its most recent visitor, and
              // a rule that let the newest join take it would hand the session to whoever
              // opened the tab last.
              Creator = acc.Creator |> Option.orElse (Some user) }
        | _ -> acc

    /// A whole event log, folded from empty. Same result as replaying `applyEvent` one
    /// event at a time from `empty`.
    let ofEvents (events: SessionEvent list) : State =
        events |> List.fold applyEvent empty

    /// Who a peer IS: their durable user when their join was attributed, or the peer
    /// connection itself when it was not. This is the one rule — used to stamp
    /// `MessageSent.Author` and to resolve the roster's own "you" row, so the two can no
    /// longer disagree about who somebody is. A `Principal`, because a peer is always
    /// somebody a turn can run as; `actorFor` is the same answer where an actor is what is
    /// being written.
    let principalFor (peerUsers: Map<PeerId, UserId>) (peer: PeerId) : Principal =
        match Map.tryFind peer peerUsers with
        | Some user -> Principal.User user
        | None -> Principal.Peer peer

    let actorFor (peerUsers: Map<PeerId, UserId>) (peer: PeerId) : ActorRef =
        Principal.toActor (principalFor peerUsers peer)

    /// Whose session this is: the first person it ever attributed, as a principal.
    ///
    /// A `Principal` rather than a `UserId` because what asks is asking whose authority
    /// something runs on, and that is the vocabulary the rest of that question is written in.
    /// Never a peer: a peer is by definition somebody nobody verified, so an unattributed
    /// session has no creator rather than an anonymous one.
    let creator (state: State) : Principal option =
        state.Creator |> Option.map Principal.User
