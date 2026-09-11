namespace Yession.Domain

/// Which durable user a peer connection belongs to, folded from the log — shared by
/// the Session Process (which stamps chat/act authorship with it) and the client (which
/// resolves a `UserRef` author back to a name for display). One fold, one decision rule,
/// used on both sides of the wire, rather than a server copy and a client guess that can
/// drift apart — which is exactly how "you" ended up with two names on one screen.
module Attribution =

    /// The single-event step: fold one more event into an existing peer→user map. This
    /// is what a live process replays incrementally as events arrive; `peerUsers` below
    /// is just this applied to a whole replay starting from empty.
    let applyEvent (acc: Map<PeerId, UserId>) (event: SessionEvent) : Map<PeerId, UserId> =
        match event with
        | PeerJoined { PeerId = peer; User = Some user } -> Map.add peer user acc
        | _ -> acc

    /// Peer → user, for every join a Manager-verified authentication strategy attributed.
    /// A peer that never appears here is unattributed (trust-localhost, or simply hasn't
    /// joined yet) and is identified only by its `PeerId`.
    let peerUsers (events: SessionEvent list) : Map<PeerId, UserId> =
        events |> List.fold applyEvent Map.empty

    /// Who to credit a peer's act to: their durable `UserRef` when their join was
    /// attributed, or the peer connection itself when it was not. This is the one rule —
    /// used to stamp `MessageSent.Author` and to resolve the roster's own "you" row, so
    /// the two can no longer disagree about who somebody is.
    let actorFor (peerUsers: Map<PeerId, UserId>) (peer: PeerId) : ActorRef =
        match Map.tryFind peer peerUsers with
        | Some user -> UserRef user
        | None -> PeerRef peer

    /// The single-event step, the other direction: which peer to ask, RIGHT NOW, for a
    /// given user's name. A user can be attributed through more than one `PeerJoined` over
    /// the life of a session — every reconnect (a new tab, a dropped connection, simply
    /// having joined before this peer's current one) mints a new `PeerId` — and the last
    /// one to join is the one whose `DisplayName` is current. Folding in EVENT order and
    /// overwriting on each join, rather than reverse-scanning `peerUsers` (`Map<PeerId,_>`,
    /// whose key order has nothing to do with recency), is what makes "the peer to ask"
    /// deterministic instead of picking whichever stale join happens to sort first.
    let applyEventForUser (acc: Map<UserId, PeerId>) (event: SessionEvent) : Map<UserId, PeerId> =
        match event with
        | PeerJoined { PeerId = peer; User = Some user } -> Map.add user peer acc
        | _ -> acc

    /// User → their most recently joined attributed peer. This is what `Model.userName`
    /// asks for the peer to resolve a `UserRef` author's current display name from.
    let userPeers (events: SessionEvent list) : Map<UserId, PeerId> =
        events |> List.fold applyEventForUser Map.empty
