namespace Yession.Domain

/// Which durable user a peer connection belongs to, folded from the log — shared by
/// the Session (which stamps chat/act authorship with it) and the client (which
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
          Creator : UserId option
          /// The name each peer joined under — what everybody in the session sees it called.
          /// Kept beside attribution because "who is this" has two halves, which user and
          /// what to call them, and a reader that is not a screen (the agent's transcript)
          /// needs both from the log alone: the client's live presence is not there to ask.
          Names : Map<PeerId, string>
          /// Where each person sits: the order they first joined in, from 0.
          ///
          /// What a person's colour is read from. It was a hash of their id into the
          /// palette, so two people shared a colour whenever their ids collided — a room of
          /// three about one time in four. A seat is handed out once, in log order, so
          /// every client folding the same log seats everybody the same way, and no two
          /// people share one until there are more people than colours.
          ///
          /// Keyed by principal, so a user keeps one seat across every device and rejoin; a
          /// peer nobody attributed is seated by the only identity it has.
          Seats : Map<Principal, int> }

    let empty : State =
        { PeerUsers = Map.empty; UserPeers = Map.empty; Creator = None; Names = Map.empty; Seats = Map.empty }

    /// A principal's seat, kept if they have one, or the next free one if not.
    let private seat (principal: Principal) (seats: Map<Principal, int>) : Map<Principal, int> =
        if Map.containsKey principal seats then seats else Map.add principal seats.Count seats

    /// The single-event step: fold one more event into an existing state, updating both
    /// directions from the one match arm. This is what a live process replays
    /// incrementally as events arrive; `ofEvents` below is just this applied to a whole
    /// replay starting from empty.
    let applyEvent (acc: State) (event: SessionEvent) : State =
        match event with
        | PeerJoined joined ->
            let named =
                if joined.DisplayName.Trim () = "" then acc.Names
                else Map.add joined.PeerId joined.DisplayName acc.Names
            match joined.User with
            | Some user ->
                { PeerUsers = Map.add joined.PeerId user acc.PeerUsers
                  UserPeers = Map.add user joined.PeerId acc.UserPeers
                  // First wins, for ever. A session's creator is not its most recent visitor,
                  // and a rule that let the newest join take it would hand the session to
                  // whoever opened the tab last.
                  Creator = acc.Creator |> Option.orElse (Some user)
                  Names = named
                  Seats = seat (Principal.User user) acc.Seats }
            | None -> { acc with Names = named; Seats = seat (Principal.Peer joined.PeerId) acc.Seats }
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

    /// What a person is called, by the log: a peer by the name it joined under, a user by the
    /// name of the peer they most recently joined as — the rule the client's `userName`
    /// follows, so the agent and the screen call one person one thing. `None` for anybody
    /// the log never named, and for what is not a person (the agent, the process).
    let nameOf (state: State) (actor: ActorRef) : string option =
        match actor with
        | PeerRef peer -> Map.tryFind peer state.Names
        | UserRef user -> Map.tryFind user state.UserPeers |> Option.bind (fun peer -> Map.tryFind peer state.Names)
        | ActorRef.Agent | ActorRef.Session | ActorRef.System | ActorRef.Configured _ -> None

    /// Where a principal sits: the seat the log gave them, or — for someone it has not
    /// seated yet, such as this tab in the moment before its own join is folded back — a
    /// seat their identity hashes to, so nobody is ever drawn without one. The hash is the
    /// old rule and collides; it only stands in until the join arrives.
    let seatOf (state: State) (principal: Principal) : int =
        match Map.tryFind principal state.Seats with
        | Some seat -> seat
        | None ->
            let id =
                match principal with
                | Principal.User user -> UserId.value user
                | Principal.Peer peer -> PeerId.value peer
            id |> Seq.fold (fun acc c -> (acc * 31 + int c) &&& 0x7fffffff) 7
