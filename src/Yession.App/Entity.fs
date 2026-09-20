namespace Yession.App

open Lit
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

    /// Which kind of thing a reference is, for the hook a test reads it by.
    let kind (entity: EntityRef) : string =
        match entity with
        | EntityRef.Actor _ -> "actor"
        | EntityRef.Repo _ -> "repo"
        | EntityRef.Connection _ -> "connection"
        | EntityRef.Sandbox _ -> "sandbox"
        | EntityRef.Pr _ -> "pr"

    /// What a reference is called on a screen, in a sentence attributed to `by`. A person by
    /// the name the roster knows; a repo by `owner/repo` — the host is the mark's to say,
    /// not the name's; a connection by its name.
    ///
    /// A sandbox by its bare name when the sentence's AUTHOR already wears its scope: a
    /// repo's file starting its own `dev` sits under an author line that says `octo/hello`,
    /// and `started sandbox octo/hello:dev` under it said the repo twice. Under any other
    /// author the scope stays — the agent starting `octo/hello:dev` beside `octo/other:dev`
    /// would otherwise read as two starts of one sandbox. The session's own carry no scope
    /// to drop. Prose keeps the whole spelling either way (`EntityRef.said`): it has no
    /// author line to lean on.
    let name (model: ClientModel) (by: ActorRef) (entity: EntityRef) : string =
        match entity with
        | EntityRef.Actor actor -> actorName model actor
        | EntityRef.Repo repo -> RepoRef.value repo
        | EntityRef.Connection connection -> ConnectionName.value connection
        | EntityRef.Sandbox sandbox ->
            match SandboxRef.scope sandbox, by with
            | RepoOwned repo, ActorRef.Configured author when repo = author -> SandboxName.value (SandboxRef.name sandbox)
            | RepoOwned _, _ -> SandboxRef.render sandbox
            | SessionOwned, _ -> SandboxName.value (SandboxRef.name sandbox)
        | EntityRef.Pr pr -> PrRef.render pr

    /// Where a reference leads, when it is somewhere a person can go. A repository is a page
    /// on its host; a person and a connection are not places. The one spelling of the URL
    /// lives with the type (`RepoRef.cloneUrl` is git's; this is the page's).
    let href (entity: EntityRef) : string option =
        match entity with
        | EntityRef.Repo repo -> Some (sprintf "https://github.com/%s" (RepoRef.value repo))
        | EntityRef.Pr pr -> Some (PrRef.url pr)
        | EntityRef.Actor _
        | EntityRef.Connection _
        | EntityRef.Sandbox _ -> None

    /// One reference, drawn: its mark and its name, inline, the same wherever a sentence
    /// points at it. `data-entity` carries the prose spelling (`EntityRef.said`), so a test
    /// can find the element for a thing without knowing what the design calls it.
    ///
    /// The mark a connection wears: its provider's own, for the providers this client knows
    /// by name, and a key for any other — what a connection IS is a credential somebody
    /// signed in for. The table is HERE, beside the other marks, rather than a field on the
    /// name: a name is a fact the log carries and a mark is a fact about this screen.
    let connectionMark (connection: ConnectionName) : TemplateResult =
        match ConnectionName.value connection with
        | "github" -> Icon.githubSm
        | _ -> Icon.keySm

    /// The mark says the KIND — a person's checker, the repository glyph, a connection's
    /// provider, the sandbox's box — and the host a repository lives on is the link's to
    /// say, not the mark's: a reference that leads somewhere is a real `<a>`,
    /// keyboard-reachable like every action on the page. `by` is whose sentence this is
    /// (see `name`); the whole spelling rides `title`, so a shortened name is still one
    /// hover from its scope.
    let private draw (model: ClientModel) (spelled: string) (entity: EntityRef) : TemplateResult =
        let mark =
            match entity with
            | EntityRef.Actor actor ->
                html $"""<span class="{Style.cls [ Style.entityAvatar; actorMark model actor ]}" aria-hidden="true"></span>"""
            | EntityRef.Repo _ -> html $"""<span class="{Style.entityMark}" aria-hidden="true">{Icon.repoSm}</span>"""
            | EntityRef.Connection connection ->
                html $"""<span class="{Style.entityMark}" aria-hidden="true">{connectionMark connection}</span>"""
            | EntityRef.Sandbox _ -> html $"""<span class="{Style.entityMark}" aria-hidden="true">{Icon.sandboxSm}</span>"""
            | EntityRef.Pr _ -> html $"""<span class="{Style.entityMark}" aria-hidden="true">{Icon.prSm}</span>"""
        let inner = html $"""{mark}<span class="{Style.entityName}">{spelled}</span>"""
        match href entity with
        | Some url ->
            html
                $"""<a class="{Style.entityLink}" href="{url}" target="_blank" rel="noopener" title="{EntityRef.said entity}" data-entity-kind="{kind entity}" data-entity="{EntityRef.said entity}">{inner}</a>"""
        | None ->
            html
                $"""<span class="{Style.entity}" title="{EntityRef.said entity}" data-entity-kind="{kind entity}" data-entity="{EntityRef.said entity}">{inner}</span>"""

    let render (model: ClientModel) (by: ActorRef) (entity: EntityRef) : TemplateResult =
        draw model (name model by entity) entity

    /// A sentence, drawn: its words as words and each reference as `render` draws it. What
    /// the agent reads as `Phrase.said` and what a person reads here are the same segments,
    /// collapsed by two readers with two opinions — which is the whole reason a phrase is
    /// segments rather than a string. `by` is whose sentence it is.
    let phrase (model: ClientModel) (by: ActorRef) (phrase: Phrase) : TemplateResult list =
        phrase
        |> List.map (fun segment ->
            match segment with
            | Segment.Text words -> html $"""{words}"""
            | Segment.Ref entity -> render model by entity)

    /// The same sentence as the AGENT read it: every reference spelled as prose spells it
    /// (`EntityRef.said` — `user:ada`, `octo/hello:dev`), wearing its mark so a reader can
    /// still see what kind of thing each is. This is what sits behind "as told to the agent":
    /// its text is `Phrase.said` to the character, which a name the screen resolved or
    /// shortened would break — that resolution is the screen's opinion, and this row is
    /// the other reader's.
    let told (model: ClientModel) (phrase: Phrase) : TemplateResult list =
        phrase
        |> List.map (fun segment ->
            match segment with
            | Segment.Text words -> html $"""{words}"""
            | Segment.Ref entity -> draw model (EntityRef.said entity) entity)
