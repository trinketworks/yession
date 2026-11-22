namespace Yession.Session

open Yession.Domain
open Yession.Domain.Access
open Yession.Codecs

#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

// Pure per-session authorization state (the RP side's bookkeeping): pending logins
// (OAuth `state` → PKCE verifier), established cookie sessions, and minted peer tokens.
// Randomness, the clock and the place sign-ins are kept are injected, so the cheap test tier
// covers the lifecycle (single-use, expiry, a relaunch) deterministically.
//
// Pending logins and peer tokens are in-memory and die with the session process. Cookie
// sessions do not: a session process stops when it is idle, and a browser that was signed in
// before the stop is signed in after the next launch (`SignInStore`).

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

/// One sign-in as the session keeps it between launches.
///
/// `Digest` is a digest of the cookie's value, never the value. The file that holds these
/// lines is then not a list of bearer tokens: whoever reads it cannot sign in with it.
type KeptSignIn =
    { Digest : string
      Identity : CookieIdentity
      /// Unix seconds. The sign-in is not honoured at or after this time.
      ExpiresAt : int64 }

/// Where a session keeps its sign-ins between launches, as two functions. `Kept` runs once,
/// when the session starts. `Keep` receives every sign-in that is still good, each time one
/// is added.
type SignInStore =
    { Kept : unit -> KeptSignIn list
      Keep : KeptSignIn list -> unit }

[<RequireQualifiedAccess>]
module SignInStore =

    /// Keeps nothing: every sign-in ends with the process. For a session that has no data
    /// directory of its own to write to, and for tests that do not relaunch.
    let none : SignInStore = { Kept = (fun () -> []); Keep = ignore }

    let private attributionCodec : Codec<PeerAttribution> =
        { Encode =
            function
            | AttributedUser user -> Encode.object [ "kind", Encode.string "user"; "user", Codec.userId.Encode user ]
            | UnattributedAccess -> Encode.object [ "kind", Encode.string "unattributed" ]
          Decode =
            Decode.field "kind" Decode.string
            |> Decode.andThen (fun kind ->
                match kind with
                | "user" -> Decode.field "user" Codec.userId.Decode |> Decode.map AttributedUser
                | "unattributed" -> Decode.succeed UnattributedAccess
                // Refused, not read as unattributed: that reading would turn a user into
                // shared access without a word.
                | other -> Decode.fail (sprintf "unknown attribution '%s'" other)) }

    let private encodeSignIn (kept: KeptSignIn) =
        Encode.object
            [ yield "digest", Encode.string kept.Digest
              yield "subject", Encode.string kept.Identity.Subject
              match kept.Identity.DisplayName with
              | Some name -> yield "displayName", Encode.string name
              | None -> ()
              yield "attribution", attributionCodec.Encode kept.Identity.Attribution
              yield "expiresAt", Encode.int64 kept.ExpiresAt ]

    let private decodeSignIn : Decoder<KeptSignIn> =
        Decode.object (fun get ->
            { Digest = get.Required.Field "digest" Decode.string
              Identity =
                { Subject = get.Required.Field "subject" Decode.string
                  DisplayName = get.Optional.Field "displayName" Decode.string
                  Attribution = get.Required.Field "attribution" attributionCodec.Decode }
              ExpiresAt = get.Required.Field "expiresAt" Decode.int64 })

    /// The kept sign-ins as the text of their file.
    let toJson (kept: KeptSignIn list) : string =
        Encode.object [ "version", Encode.int 1; "signIns", kept |> List.map encodeSignIn |> Encode.list ]
        |> Encode.toString 0

    /// The sign-ins a file's text holds. A text this build cannot read is an `Error`, and the
    /// caller then keeps none: each browser signs in one more time, and nobody is let in on
    /// a guess.
    let ofJson (json: string) : Result<KeptSignIn list, string> =
        Decode.fromString (Decode.field "signIns" (Decode.list decodeSignIn)) json

/// The kept sign-ins as they are on disk: the text of `SignInStore.toJson`, sealed with
/// AES-256-GCM under the key the Manager gives this session (`RegisterClientResponse`). The
/// file holds no name, no address and no digest in the clear, and a file that somebody
/// changed does not open.
type SealedSignIns =
    { /// base64url, 12 random bytes, new at each write.
      Iv : string
      /// base64url, the ciphertext and its 16-byte tag.
      Ciphertext : string }

[<RequireQualifiedAccess>]
module SealedSignIns =

    /// Version 1 was the sign-ins in the clear, which no build reads now.
    let version = 2

    let create (iv: string) (ciphertext: string) : SealedSignIns = { Iv = iv; Ciphertext = ciphertext }

    let toJson (envelope: SealedSignIns) : string =
        Encode.object
            [ "version", Encode.int version
              "iv", Encode.string envelope.Iv
              "ciphertext", Encode.string envelope.Ciphertext ]
        |> Encode.toString 0

    /// The sealed sign-ins a file's text holds. A file of another version is an `Error`:
    /// the caller removes it, and each browser signs in one more time.
    let ofJson (json: string) : Result<SealedSignIns, string> =
        let decoder =
            Decode.field "version" Decode.int
            |> Decode.andThen (fun found ->
                if found <> version then
                    Decode.fail (sprintf "sign-ins file version %d, and this build reads %d" found version)
                else
                    Decode.object (fun get ->
                        { Iv = get.Required.Field "iv" Decode.string
                          Ciphertext = get.Required.Field "ciphertext" Decode.string }))
        Decode.fromString decoder json

/// Established browser sessions: opaque cookie value → authenticated identity.
///
/// A sign-in lasts `Cookies.signInLifetimeSeconds`, across launches of the session. The
/// sign-ins the last process left are read from `store` once, here, and the ones still good
/// are written back each time a browser signs in. Only `digest` of a cookie's value is held,
/// in memory and in the store.
type CookieSessions
    (mint: unit -> string, digest: string -> string, nowUnix: unit -> int64, store: SignInStore) =

    let good (now: int64) (kept: KeptSignIn) = kept.ExpiresAt > now

    let mutable sessions : Map<string, KeptSignIn> =
        let now = nowUnix ()
        store.Kept ()
        |> List.filter (good now)
        |> List.map (fun kept -> kept.Digest, kept)
        |> Map.ofList

    member _.Mint (identity: CookieIdentity) : string =
        let value = mint ()
        let now = nowUnix ()
        let kept =
            { Digest = digest value
              Identity = identity
              ExpiresAt = now + Cookies.signInLifetimeSeconds }
        // The sign-ins that ran out leave here, so neither the map nor the file grows with
        // every browser that ever signed in.
        sessions <- sessions |> Map.filter (fun _ existing -> good now existing) |> Map.add kept.Digest kept
        store.Keep (sessions |> Map.toList |> List.map snd)
        value

    member _.IdentityOf (value: string) : CookieIdentity option =
        Map.tryFind (digest value) sessions
        |> Option.filter (good (nowUnix ()))
        |> Option.map (fun kept -> kept.Identity)

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
