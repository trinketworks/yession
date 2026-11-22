namespace Yession.Oidc

open Yession.Domain
open Yession.Codecs

#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

/// The provider's wire shapes: the DCR request/response riding the control channel, the
/// discovery document (OIDC Discovery §3 — snake_case on the wire), and the token
/// response (RFC 6749 §5.1). Hand-written codecs like every boundary (ControlWire
/// discipline).

type RegisterClientRequest =
    { RedirectUri : string }

type RegisterClientResponse =
    { ClientId : string
      ClientSecret : string
      /// The provider's issuer URL — everything else (endpoints, JWKS) is discovered
      /// from `<issuer>/.well-known/openid-configuration`.
      Issuer : string }

type Discovery =
    { Issuer : string
      AuthorizationEndpoint : string
      TokenEndpoint : string
      JwksUri : string }

type TokenResponse =
    { AccessToken : string
      TokenType : string
      IdToken : string
      ExpiresIn : int }

/// One key of a JWKS document (RFC 7517 §4): a public key's parameters, with the id and the
/// algorithm its publisher signs under written beside them.
///
/// Named parameter by parameter rather than spread from whatever exported the key, so an
/// entry carries exactly what is listed here: a parameter this key does not have is ABSENT
/// rather than `null`, and a private one could not be copied through even if a key ever
/// exported it.
type JwksKey =
    { Kty : string
      Crv : string option
      X : string option
      Y : string option
      N : string option
      E : string option
      Kid : string
      Alg : string
      Use : string }

/// A JWKS document (RFC 7517 §5): what `jwks_uri` serves, and what every relying party
/// verifying an ID token from this provider fetches and reads.
type Jwks =
    { Keys : JwksKey list }

/// The OIDC profile claims (OIDC Core §5.1) an ID token carries for a real user: each the
/// strategy knew, and absent when it did not.
[<RequireQualifiedAccess>]
type IdTokenProfile =
    { Name : string option
      Email : string option
      Picture : string option }

/// Who an ID token from this provider was issued for, beyond its `sub`: what the provider
/// writes and the session's relying party reads back to decide whether the cookie it mints
/// is a person or shared access.
[<RequireQualifiedAccess>]
type IdTokenAttribution =
    /// A real, durable user identity.
    | User of IdTokenProfile
    /// Shared access with no attributable user behind it (trust-localhost).
    | Unattributed

[<RequireQualifiedAccess>]
module IdTokenAttribution =

    /// What a token says about the identity the strategy authenticated.
    let ofIdentity (claims: UserClaims option) : IdTokenAttribution =
        match claims with
        | Some c ->
            IdTokenAttribution.User
                { IdTokenProfile.Name = c.DisplayName
                  IdTokenProfile.Email = c.Email
                  IdTokenProfile.Picture = c.Picture }
        | None -> IdTokenAttribution.Unattributed

module Wire =

    /// A parameter written only when it has a value: OMITTED otherwise rather than written
    /// `null`, which a reader is entitled to treat as a value.
    let private optional (name: string) (value: string option) =
        match value with
        | Some v -> [ name, Encode.string v ]
        | None -> []

    let registerClientRequest : Codec<RegisterClientRequest> =
        { Encode = fun (r: RegisterClientRequest) -> Encode.object [ "redirectUri", Encode.string r.RedirectUri ]
          Decode =
            Decode.object (fun get ->
                { RegisterClientRequest.RedirectUri = get.Required.Field "redirectUri" Decode.string }) }

    let registerClientResponse : Codec<RegisterClientResponse> =
        { Encode =
            fun (r: RegisterClientResponse) ->
                Encode.object
                    [ "clientId", Encode.string r.ClientId
                      "clientSecret", Encode.string r.ClientSecret
                      "issuer", Encode.string r.Issuer ]
          Decode =
            Decode.object (fun get ->
                { RegisterClientResponse.ClientId = get.Required.Field "clientId" Decode.string
                  RegisterClientResponse.ClientSecret = get.Required.Field "clientSecret" Decode.string
                  RegisterClientResponse.Issuer = get.Required.Field "issuer" Decode.string }) }

    /// The discovery document. Encoding also asserts what the provider supports —
    /// `code` + PKCE `S256` + `EdDSA`, nothing else — per OIDC Discovery §3; the decoder
    /// reads only what the RP config needs.
    let discovery : Codec<Discovery> =
        { Encode =
            fun (d: Discovery) ->
                Encode.object
                    [ "issuer", Encode.string d.Issuer
                      "authorization_endpoint", Encode.string d.AuthorizationEndpoint
                      "token_endpoint", Encode.string d.TokenEndpoint
                      "jwks_uri", Encode.string d.JwksUri
                      "response_types_supported", Encode.list [ Encode.string "code" ]
                      "grant_types_supported", Encode.list [ Encode.string "authorization_code" ]
                      "code_challenge_methods_supported", Encode.list [ Encode.string "S256" ]
                      "id_token_signing_alg_values_supported", Encode.list [ Encode.string "EdDSA" ]
                      "subject_types_supported", Encode.list [ Encode.string "public" ]
                      "scopes_supported", Encode.list [ Encode.string "openid" ]
                      "token_endpoint_auth_methods_supported", Encode.list [ Encode.string "client_secret_post" ] ]
          Decode =
            Decode.object (fun get ->
                { Discovery.Issuer = get.Required.Field "issuer" Decode.string
                  Discovery.AuthorizationEndpoint = get.Required.Field "authorization_endpoint" Decode.string
                  Discovery.TokenEndpoint = get.Required.Field "token_endpoint" Decode.string
                  Discovery.JwksUri = get.Required.Field "jwks_uri" Decode.string }) }

    let tokenResponse : Codec<TokenResponse> =
        { Encode =
            fun (t: TokenResponse) ->
                Encode.object
                    [ "access_token", Encode.string t.AccessToken
                      "token_type", Encode.string t.TokenType
                      "id_token", Encode.string t.IdToken
                      "expires_in", Encode.int t.ExpiresIn ]
          Decode =
            Decode.object (fun get ->
                { TokenResponse.AccessToken = get.Required.Field "access_token" Decode.string
                  TokenResponse.TokenType = get.Required.Field "token_type" Decode.string
                  TokenResponse.IdToken = get.Required.Field "id_token" Decode.string
                  TokenResponse.ExpiresIn = get.Required.Field "expires_in" Decode.int }) }

    /// One JWKS entry. A parameter the key does not have is OMITTED rather than written
    /// `null`: RFC 7517 §4 makes each one optional per key type, and a reader is entitled to
    /// treat a `null` that is present as a value.
    let private jwksKey : Codec<JwksKey> =
        { Encode =
            fun (k: JwksKey) ->
                Encode.object
                    [ yield "kty", Encode.string k.Kty
                      yield! optional "crv" k.Crv
                      yield! optional "x" k.X
                      yield! optional "y" k.Y
                      yield! optional "n" k.N
                      yield! optional "e" k.E
                      yield "kid", Encode.string k.Kid
                      yield "alg", Encode.string k.Alg
                      yield "use", Encode.string k.Use ]
          Decode =
            Decode.object (fun get ->
                { JwksKey.Kty = get.Required.Field "kty" Decode.string
                  JwksKey.Crv = get.Optional.Field "crv" Decode.string
                  JwksKey.X = get.Optional.Field "x" Decode.string
                  JwksKey.Y = get.Optional.Field "y" Decode.string
                  JwksKey.N = get.Optional.Field "n" Decode.string
                  JwksKey.E = get.Optional.Field "e" Decode.string
                  JwksKey.Kid = get.Required.Field "kid" Decode.string
                  JwksKey.Alg = get.Required.Field "alg" Decode.string
                  JwksKey.Use = get.Required.Field "use" Decode.string }) }

    /// The key set (RFC 7517 §5). These BYTES are a published contract — software this
    /// repository will never see fetches them from `jwks_uri` — so the parameters are
    /// written in one fixed order and the suite pins the document rather than describing
    /// it.
    ///
    /// The decoder reads what THIS provider publishes: `kid`, `alg` and `use` are optional
    /// to RFC 7517 and required here, because a key set of ours that has lost one of them
    /// has lost what a relying party picks a key by.
    let jwks : Codec<Jwks> =
        { Encode = fun (d: Jwks) -> Encode.object [ "keys", d.Keys |> List.map jwksKey.Encode |> Encode.list ]
          Decode = Decode.object (fun get -> { Jwks.Keys = get.Required.Field "keys" (Decode.list jwksKey.Decode) }) }

    /// The claims an ID token carries beyond the registered ones (`iss`, `sub`, `aud`, `iat`,
    /// `exp`, which the signing builder sets): the profile, and `yession_attribution`, the
    /// discriminator between a person and shared access. The provider writes them and the
    /// session reads them back, through this codec both times, so this is the one place
    /// their names are spelled.
    ///
    /// The attribution is REQUIRED, and only the two this build names are read. Absent or
    /// unknown is refused rather than read as unattributed: that reading would demote every
    /// user to shared access the day the two ends stopped agreeing, and say nothing.
    let idTokenAttribution : Codec<IdTokenAttribution> =
        let attribution = "yession_attribution"
        let user = "user"
        let unattributed = "unattributed"
        { Encode =
            function
            | IdTokenAttribution.User profile ->
                Encode.object
                    [ yield attribution, Encode.string user
                      yield! optional "name" profile.Name
                      yield! optional "email" profile.Email
                      yield! optional "picture" profile.Picture ]
            | IdTokenAttribution.Unattributed -> Encode.object [ attribution, Encode.string unattributed ]
          Decode =
            Decode.field attribution Decode.string
            |> Decode.andThen (fun raw ->
                if raw = user then
                    Decode.object (fun get ->
                        IdTokenAttribution.User
                            { IdTokenProfile.Name = get.Optional.Field "name" Decode.string
                              IdTokenProfile.Email = get.Optional.Field "email" Decode.string
                              IdTokenProfile.Picture = get.Optional.Field "picture" Decode.string })
                elif raw = unattributed then
                    Decode.succeed IdTokenAttribution.Unattributed
                else
                    Decode.fail (sprintf "unknown attribution '%s' (expected %s or %s)" raw user unattributed)) }

    /// OAuth error bodies: `{"error": "..."}` (RFC 6749 §5.2).
    let tokenError : Codec<string> =
        { Encode = fun error -> Encode.object [ "error", Encode.string error ]
          Decode = Decode.field "error" Decode.string }

    let toString (codec: Codec<'a>) (value: 'a) : string = codec.Encode value |> Encode.toString 0

    let fromString (codec: Codec<'a>) (json: string) : Result<'a, string> = Decode.fromString codec.Decode json
