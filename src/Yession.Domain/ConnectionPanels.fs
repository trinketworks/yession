namespace Yession.Domain.Access

open Yession.Domain.Agent

/// What a session tells a BROWSER about its connections — the settings drawer's two
/// panels, as they cross the wire.
///
/// Separate from `ConnectionStatus` above, which is what the Manager streams to a session:
/// different audience, different facts. A panel has no `Id` (it names its scope by the slot
/// it renders) and no `UpdatedAt` (nothing on screen says when a credential was written),
/// and it carries things the Manager has no opinion about — who the shared scope belongs
/// to here, whether this session has an agent at all.
///
/// It lives in the domain because BOTH sides of that wire are this repository's, and until
/// now neither could name the shape: the session built the JSON with `sprintf` and the
/// browser read it with a hand-written decoder, two lists of field names that nothing
/// checked against each other. Both panels had their own copy of the same encoder, too.

/// One connected credential as a panel row reads it.
///
/// `SignInRequired` carries the REASON rather than a flag, because a row that says only
/// "broken" sends somebody to guess. "the refresh token has expired" and "github rejected
/// this credential" lead a person to the same button but tell them different things about
/// why they are pressing it. `None` means nothing has established otherwise — not a promise
/// that it works, which is a thing no side of this can honestly make about a static token.
type CredentialRow =
    { Kind : ConnectionKind
      SignInRequired : string option }

module CredentialRow =

    /// The row a stored connection shows, or `None` for a scope with nothing connected.
    /// The ONE place a status becomes a row, so the two panels cannot drift — they each
    /// had a copy of this, and a copy is a thing that stops being the same.
    let ofStatus (status: ConnectionStatus option) : CredentialRow option =
        status
        |> Option.map (fun status ->
            { Kind = status.Kind
              SignInRequired =
                match status.Health with
                | ConnectionUsable -> None
                | SignInRequired reason -> Some reason })

/// Who the shared sign-in scope belongs to where this session runs.
///
/// A panel offers two scopes, and on a deployment that attributes nobody (`--auth
/// localhost`) the shared one is EVERYONE who can reach this Manager. Calling that "mine"
/// is the panel promising something the deployment cannot keep, so which it is rides the
/// wire as a case rather than being the renderer's to assume.
///
/// It was `string option` — `"user"` or `"local"`, computed identically in two route
/// handlers, and read by neither panel under any obligation. The GitHub section wrote
/// "All my sessions" outright and never asked, which under `--auth localhost` was exactly
/// the promise this comment forbids. A DU makes the match exhaustive, and an exhaustive
/// match is what a label cannot be wrong through.
type SharedOwner =
    /// One signed-in human's own credential, and nobody else's.
    | OwnedByUser
    /// The whole deployment's: everyone who can reach this Manager.
    | OwnedByDeployment

module SharedOwner =

    /// The scope's owner as a panel states it. One place, because it was two — each route
    /// handler spelling the same `match` for its own panel.
    let ofCredentialOwner (owner: CredentialOwner) : SharedOwner =
        match owner with
        | UserOwner _ -> OwnedByUser
        | LocalOwner -> OwnedByDeployment

/// What the picker knows about the models it can offer. Three states and no fourth,
/// because a picker has exactly three honest things to say: I have not looked yet, here is
/// the list, or here is why there is no list. A single `AgentModel list` could not tell the
/// first from a provider that genuinely offers nothing, and the difference is what decides
/// whether a person waits or goes and connects an account.
type ModelCatalogueState =
    /// Nothing has been asked for yet, or an answer is in flight.
    | ModelsUnknown
    | ModelsLoaded of AgentModel list
    /// The lookup answered, and what it said was why it could not.
    | ModelsUnavailable of reason: string

/// The Claude panel (Plan 08), per sign-in scope.
///
/// `Owner` and `AgentAvailable` are not options, and that is the point: the session always
/// knows both, and a panel only exists once it has been said. Their `None` used to mean
/// "the client has not been told yet" — one fact, spelled twice, in the type of the answer
/// rather than in whether there IS one. A client holds the panel itself as an option now,
/// so "not told yet" is one case in one place and the view matches it once. What stays
/// optional is a credential, whose `None` means something else entirely: nothing connected.
type ClaudePanel =
    { SessionCredential : CredentialRow option
      MineCredential : CredentialRow option
      /// Who the shared scope belongs to here. The panel must not promise "mine" for a
      /// credential everybody shares.
      Owner : SharedOwner
      /// Whether THIS session has an agent at all: any connected credential, or the host's
      /// ambient one.
      AgentAvailable : bool
      /// What the picker has to choose from — IN the panel, because it is a fact about
      /// this credential and not a fact beside it.
      Models : ModelCatalogueState }

/// The GitHub panel (Plan 14). The same two scopes, and none of the things this provider
/// has no answer for — no catalogue to offer, no agent to be available — so the type does
/// not carry fields that would be `None` forever.
type GitHubPanel =
    { SessionCredential : CredentialRow option
      MineCredential : CredentialRow option
      /// Who the shared scope belongs to, exactly as the Claude panel states it — and now
      /// read the same way, by the same label.
      Owner : SharedOwner }
