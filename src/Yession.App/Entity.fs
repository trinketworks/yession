namespace Yession.App

open Yession.Domain

/// How a thing the session names is shown, wherever it is shown.
///
/// A person appears on a dozen surfaces — the group line over their messages, the mark on
/// a block they ran, the lease bar, a refusal's "refused by", the roster — and every one of
/// them has to agree on three answers: what token a test reads, what name a person reads,
/// and what mark the eye reads. Those three were private helpers in `View.fs`, which meant
/// one file's opinion applied to the surfaces that file drew and nowhere else; the mark
/// helper also seeded a `UserRef` by its subject and a `PeerRef` by its peer id, so ONE
/// person wore two marks depending on which reference an event happened to record.
///
/// So the answers live here, once, on state a cheap test can build (`ClientModel` and
/// nothing above it), and every surface asks rather than deciding. The next kind of thing
/// a sentence names — a repo, a connection — gets its answers beside these.
[<RequireQualifiedAccess>]
module Entity =

    /// An actor as a TOKEN: stable, model-free, and what every `data-*` hook carries — which
    /// is why it stays a total function of the actor alone and why the tests can assert it.
    let actorToken (actor: ActorRef) : string =
        match actor with
        | UserRef u -> UserId.value u
        | PeerRef p -> PeerId.value p
        | ActorRef.Agent -> Dom.Text.agent
        | ActorRef.SessionProcess -> Dom.Text.sessionProcess
        | ActorRef.System -> Dom.Text.system
        | ActorRef.Configured repo -> RepoRef.value repo

    /// The same actor, said to a person.
    ///
    /// A peer id is a fine token and a poor name — `PEER-129755065` is nobody — and the
    /// roster, the draft summaries and the lease bar all resolve one through `nameOf`
    /// already. The chat did not, so one human appeared under two identities on the one
    /// screen: the roster showed a peer's rolled name while chat printed a `UserRef`'s raw
    /// subject, and neither was the person's real name. Both resolve through
    /// `Yession.App.ClientModel`, which folds `UserRef` back to a peer's name through the
    /// same `Yession.Domain.Attribution` rule the Session Process used to decide the author
    /// was a `UserRef` in the first place — so chat and the sidebar can no longer show two
    /// names for one person. `Agent`/`System`/etc. are already a word, so only a peer or a
    /// user resolves.
    let actorName (model: ClientModel) (actor: ActorRef) : string =
        match actor with
        | PeerRef peer -> ClientModel.nameOf peer model
        | UserRef user -> ClientModel.userName user model
        | ActorRef.Agent | ActorRef.SessionProcess | ActorRef.System | ActorRef.Configured _ ->
            actorToken actor

    /// The mark an actor wears: the class that draws it.
    ///
    /// A person's mark is seeded by the PERSON, not by the reference. A `UserRef` and a
    /// `PeerRef` are two ways an event can point at one human — the Session Process records
    /// the user when attribution knows one and the peer when it does not — and a mark
    /// seeded by whichever was recorded gave that human two checkers on one screen. The
    /// durable identity wins when attribution has it: a user keeps one mark across every
    /// device and rejoin, and a peer nobody has attributed is seeded by the only id it has.
    let actorMark (model: ClientModel) (actor: ActorRef) : string =
        match actor with
        | UserRef u -> Style.humanAvatar (UserId.value u)
        | PeerRef p ->
            match Map.tryFind p model.Attribution.PeerUsers with
            | Some user -> Style.humanAvatar (UserId.value user)
            | None -> Style.humanAvatar (PeerId.value p)
        | ActorRef.Agent -> Style.agentAvatar
        | ActorRef.SessionProcess | ActorRef.System -> Style.humanAvatar "session"
        // A repo's file is not a person and not the agent. Its own avatar, seeded by the
        // repo, so two repos configuring one session are told apart on sight.
        | ActorRef.Configured repo -> Style.humanAvatar (RepoRef.value repo)
