namespace Yession.SessionProcess

open Yession.Domain

// Pure per-session authorization state (the RP side's bookkeeping): pending logins
// (OAuth `state` → PKCE verifier), established cookie sessions, and minted peer tokens.
// Randomness and the clock are injected, so the cheap test tier covers the lifecycle
// (single-use, expiry) deterministically. Everything is in-memory and dies with the
// session process — matching the cookies (no Max-Age) and the Manager's own state.

/// Logins that have been redirected to the provider and not yet called back.
/// Single-use and short-lived: a state is consumed on first take, and an abandoned
/// login expires after five minutes.
type PendingLogins (nowUnix: unit -> int64) =
    let lifetimeSeconds = 300L
    let mutable pending : Map<string, string * int64> = Map.empty

    member _.Add (state: string) (verifier: string) : unit =
        pending <- Map.add state (verifier, nowUnix ()) pending

    member _.Take (state: string) : string option =
        match Map.tryFind state pending with
        | None -> None
        | Some (verifier, issuedAt) ->
            pending <- Map.remove state pending
            if nowUnix () - issuedAt > lifetimeSeconds then None else Some verifier

/// What a connection is attributed to: a Manager-verified user when the
/// strategy attributed one, or shared access with no user behind it (trust-localhost).
type PeerAttribution =
    | AttributedUser of UserId
    | UnattributedAccess

module PeerAttribution =
    /// The user behind the attribution, when one exists.
    let userOf (attribution: PeerAttribution) : UserId option =
        match attribution with
        | AttributedUser user -> Some user
        | UnattributedAccess -> None

    /// The party a browser request's provider calls run on, so what it is answered with is
    /// what this person's credential can actually see.
    ///
    /// The deployment's own where it attributes nobody: an unattributed browser IS the
    /// deployment asking, and it reaches exactly the credentials a deployment may — the
    /// session's own and the local one — because that is what the turn-target precedence
    /// resolves the deployment to. There is no separate rule here to keep in step.
    let credential (attribution: PeerAttribution) : CredentialFor =
        match attribution with
        | AttributedUser user -> CredentialFor.Person (Principal.User user)
        | UnattributedAccess -> CredentialFor.Deployment

/// The identity behind an established browser session: the validated ID token's subject
/// plus the claims the session needs downstream. `Attribution` distinguishes a real user
/// (the token said `yession_attribution = "user"`) from shared unattributed access.
type CookieIdentity =
    { Subject : string
      DisplayName : string option
      Attribution : PeerAttribution }

/// Established browser sessions: opaque cookie value → authenticated identity.
type CookieSessions (mint: unit -> string) =
    let mutable sessions : Map<string, CookieIdentity> = Map.empty

    member _.Mint (identity: CookieIdentity) : string =
        let value = mint ()
        sessions <- Map.add value identity sessions
        value

    member _.IdentityOf (value: string) : CookieIdentity option =
        Map.tryFind value sessions

    member this.SubjectOf (value: string) : string option =
        this.IdentityOf value |> Option.map (fun identity -> identity.Subject)

/// Peer tokens: minted for an authenticated browser (via `/me`) and presented in
/// `PeerHello` over the data channel — cookies cannot ride WebRTC, so the session
/// mints its own bearer for that hop. A token is valid iff this process minted it, and
/// it carries the attribution the cookie held at mint time, so user identity reaches
/// the event log without ever riding a peer-controlled frame.
type PeerTokens (mint: unit -> string) =
    let mutable tokens : Map<string, PeerAttribution> = Map.empty

    member _.Mint (attribution: PeerAttribution) : string =
        let token = mint ()
        tokens <- Map.add token attribution tokens
        token

    /// The attribution behind a token; None = this process never minted it.
    member _.Validate (token: string) : PeerAttribution option =
        Map.tryFind token tokens
