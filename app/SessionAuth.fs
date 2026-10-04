module Yession.Host.SessionAuth

// The Session as an OAuth/OIDC client (RP), over the certified `openid-client`
// library: discovery, PKCE, the code exchange, and ID-token validation are all the
// certified implementation — nothing protocol-shaped is hand-rolled here. This module
// only adapts it to the session's plain `node:http` surface: begin-login builds the
// authorize redirect, the callback redeems the code and mints an HttpOnly cookie, and
// `/me` turns a valid cookie into a peer token (see Session.Auth).
//
// Configuration is DEFERRED: the client registers with the Manager only after its
// server listens (the redirect URI needs the OS-assigned port), so the Auth value
// exists before its RP configuration does. Until `Configure` resolves, `BeginLogin`
// yields None (surfaced as a 503 — unreachable in practice, because the session
// registers before printing its readiness line).

open Fable.Core
open Fable.Core.JsInterop
open Fable.NodeExtras
open Yession.Domain
open Yession.Domain.Access
open Yession.Session
open Fable.OpenIdClient
open Yession.Oidc
open Yession.Host.Interop

type Auth =
    { /// Complete the RP configuration after dynamic client registration: run OIDC
      /// discovery against the issuer and bind the client credentials + redirect URI.
      Configure : string -> string -> string -> string -> Async<Result<unit, string>>
      /// Does the request carry a cookie this process minted?
      IsAuthenticated : IncomingMessage -> bool
      /// The authenticated identity behind the request's cookie, when any: the subject
      /// plus the attribution the validated ID token carried.
      IdentityOf : IncomingMessage -> CookieIdentity option
      /// Start a login: mint state + PKCE verifier, return the authorize URL.
      /// None until `Configure` has completed.
      BeginLogin : unit -> Async<string option>
      /// Handle the callback request URL. Ok = the `Set-Cookie` value to send with the
      /// redirect back to `/`; Error = (status, message).
      HandleCallback : string -> Async<Result<string, int * string>>
      CookieName : string }

/// `mount` is the path this session is served under (`""` at an origin root): the
/// auth cookie is scoped to it, so a path-mounted session's cookie is not sent to its
/// siblings on the same host.
let create (sessionId: SessionId) (mount: string) : Auth =
    let cookieName = Cookies.sessionCookieName sessionId
    let nowUnix () = System.DateTimeOffset.UtcNow.ToUnixTimeSeconds ()
    let pendingLogins = PendingLogins (nowUnix)
    let cookies = CookieSessions (randomSecret)
    let mutable configuration : (Configuration * string) option = None

    let identityOf (req: IncomingMessage) : CookieIdentity option =
        Cookies.tryFind cookieName (headerOf req "cookie")
        |> Option.bind cookies.IdentityOf

    { Configure =
        fun issuer clientId clientSecret redirectUri ->
            async {
                try
                    let! resolved =
                        discovery
                            (newUrl issuer)
                            clientId
                            clientSecret
                            // Loopback HTTP issuer (RFC 8252 pattern) — see the binding.
                            (jsOptions<DiscoveryOptions> (fun o -> o.execute <- [| allowInsecureRequests |]))
                        |> Interop.awaitPromise
                    configuration <- Some (resolved, redirectUri)
                    return Ok ()
                with e ->
                    return Error (sprintf "OIDC discovery against %s failed: %s" issuer e.Message)
            }
      IsAuthenticated = identityOf >> Option.isSome
      IdentityOf = identityOf
      BeginLogin =
        fun () ->
            async {
                match configuration with
                | None -> return None
                | Some (config, redirectUri) ->
                    let verifier = randomPKCECodeVerifier ()
                    let! challenge = calculatePKCECodeChallenge verifier |> Interop.awaitPromise
                    let state = randomState ()
                    pendingLogins.Add state verifier
                    let url =
                        buildAuthorizationUrl
                            config
                            (jsOptions<AuthorizationParameters> (fun p ->
                                p.redirect_uri <- redirectUri
                                p.scope <- "openid"
                                p.state <- state
                                p.code_challenge <- challenge
                                p.code_challenge_method <- "S256"))
                    return Some url.href
            }
      HandleCallback =
        fun requestUrl ->
            async {
                match configuration with
                | None -> return Error (503, "session is still registering with its manager")
                | Some (config, redirectUri) ->
                    match queryParamOf requestUrl "state" with
                    | None -> return Error (400, "unknown or expired login; reopen the session URL")
                    | Some state ->
                      match pendingLogins.Take state with
                      | None -> return Error (400, "unknown or expired login; reopen the session URL")
                      | Some verifier ->
                        try
                            // The certified client redeems the code AND validates the ID
                            // token (signature via the discovered JWKS, iss, aud, exp)
                            // before this resolves.
                            let! tokens =
                                authorizationCodeGrant
                                    config
                                    (newUrlWithBase requestUrl redirectUri)
                                    (jsOptions<AuthorizationChecks> (fun c ->
                                        c.pkceCodeVerifier <- verifier
                                        c.expectedState <- state))
                                |> Interop.awaitPromise
                            let claims = tokens.claims ()
                            // The attribution rides the validated ID token: only the
                            // provider's own claim that it was issued for a user makes
                            // this cookie a real user — never anything client-supplied.
                            match Wire.fromString Wire.idTokenAttribution (IdTokenClaims.json claims) with
                            | Error reason ->
                                return Error (401, sprintf "the ID token's attribution is unreadable: %s" reason)
                            | Ok said ->
                                let attribution, displayName =
                                    match said, UserId.create claims.sub with
                                    | IdTokenAttribution.User profile, Ok user -> AttributedUser user, profile.Name
                                    | IdTokenAttribution.User profile, Error _ -> UnattributedAccess, profile.Name
                                    | IdTokenAttribution.Unattributed, _ -> UnattributedAccess, None
                                let cookieValue =
                                    cookies.Mint
                                        { Subject = claims.sub
                                          DisplayName = displayName
                                          Attribution = attribution }
                                return Ok (Cookies.set cookieName mount cookieValue)
                        with e ->
                            return Error (401, sprintf "authorization failed: %s" e.Message)
            }
      CookieName = cookieName }
