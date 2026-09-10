namespace Yession.Domain

/// Which durable user a peer connection belongs to, folded from the log — shared by
/// the Session Process (which stamps chat/act authorship with it) and the client (which
/// resolves a `UserRef` author back to a name for display). One fold, one decision rule,
/// used on both sides of the wire, rather than a server copy and a client guess that can
/// drift apart — which is exactly how "you" ended up with two names on one screen.
module Attribution =

    /// Peer → user, for every join a Manager-verified authentication strategy attributed.
    /// A peer that never appears here is unattributed (trust-localhost, or simply hasn't
    /// joined yet) and is identified only by its `PeerId`.
    let peerUsers (events: SessionEvent list) : Map<PeerId, UserId> =
        events
        |> List.fold
            (fun acc event ->
                match event with
                | PeerJoined { PeerId = peer; User = Some user } -> Map.add peer user acc
                | _ -> acc)
            Map.empty

    /// Who to credit a peer's act to: their durable `UserRef` when their join was
    /// attributed, or the peer connection itself when it was not. This is the one rule —
    /// used to stamp `MessageSent.Author` and to resolve the roster's own "you" row, so
    /// the two can no longer disagree about who somebody is.
    let actorFor (peerUsers: Map<PeerId, UserId>) (peer: PeerId) : ActorRef =
        match Map.tryFind peer peerUsers with
        | Some user -> UserRef user
        | None -> PeerRef peer
