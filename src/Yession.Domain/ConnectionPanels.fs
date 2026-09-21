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
type ClaudePanel =
    { SessionCredential : CredentialRow option
      MineCredential : CredentialRow option
      /// Who the "all my sessions" scope would belong to here: `"user"` (this signed-in
      /// human alone) or `"local"` (the whole deployment — everyone who can reach this
      /// Manager, under `--auth localhost`). The panel must not promise "mine" for a
      /// credential everybody shares. `None` until the first answer arrives.
      Owner : string option
      /// Whether THIS session currently has an agent at all (any connected credential
      /// or the host's ambient one). `None` until the first answer arrives — the
      /// "no agent" prompt must never flash before the client actually knows.
      AgentAvailable : bool option
      /// What the picker has to choose from — IN the panel, because it is a fact about
      /// this credential and not a fact beside it.
      Models : ModelCatalogueState }

/// The GitHub panel (Plan 14). The same two scopes, and none of the things this provider
/// has no answer for — no catalogue to offer, no agent to be available — so the type does
/// not carry fields that would be `None` forever.
type GitHubPanel =
    { SessionCredential : CredentialRow option
      MineCredential : CredentialRow option
      /// Who the shared scope belongs to, exactly as the Claude panel states it.
      ///
      /// The session has always sent it and the panel has never read it: the GitHub section
      /// labels that row "all my sessions" outright, where Claude's asks this. Under
      /// `--auth localhost` that label is wrong in the way `ClaudePanel.Owner`'s comment
      /// says a panel must not be — the credential is the whole deployment's. Carried
      /// rather than dropped because the fix is to USE it, and that is a change to what a
      /// person reads rather than to how it is written.
      Owner : string option }
