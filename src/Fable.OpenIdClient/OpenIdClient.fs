module Fable.OpenIdClient

// Fable bindings to the `openid-client` npm package (v6, functional API) — the
// OpenID-Foundation-CERTIFIED relying-party implementation. The Session uses it
// for the whole client side of the flow: discovery, PKCE, the authorization-code
// exchange, and ID-token validation against the provider's JWKS. Using the certified
// client instead of hand-rolled protocol code means every full-flow test doubles as a
// conformance check of our provider.
//
// Binding layer only (Fable.Dockerode pattern): the slice the Session uses.
// Fable-only: `dotnet build` type-checks it; Fable emits the JS that runs on Node.

open Fable.Core

/// Opaque client configuration: issuer metadata + client credentials, from `discovery`.
type [<AllowNullLiteral>] Configuration =
    interface end

/// A WHATWG `URL` — what openid-client takes a location as, and answers one with.
type [<AllowNullLiteral>] Url =
    abstract href : string

/// Validated ID-token claims, exposed by the token response's `claims()` helper.
/// The optional members are the profile claims Yession's provider adds when its
/// strategy attributed a real user; absent otherwise.
type [<AllowNullLiteral>] IdTokenClaims =
    abstract iss : string
    abstract sub : string
    /// One audience or several (RFC 7519 §4.1.3).
    abstract aud : U2<string, string[]>
    abstract exp : float
    abstract iat : float
    abstract name : string option
    abstract yession_attribution : string option

type [<AllowNullLiteral>] TokenEndpointResponse =
    abstract access_token : string
    abstract token_type : string
    abstract id_token : string
    /// The ID-token claims — already validated (signature via the discovered JWKS,
    /// iss/aud/exp) by `authorizationCodeGrant` before the promise resolves.
    abstract claims : unit -> IdTokenClaims

/// A hook `discovery` runs over the configuration it builds — `allowInsecureRequests`
/// is the one this repository uses. Opaque: only openid-client makes one.
type ConfigurationHook =
    interface end

/// `discovery`'s options: the slice used.
type DiscoveryOptions =
    abstract execute : ConfigurationHook[] with get, set

/// How the client authenticates at the token endpoint. Opaque, and never passed: absent,
/// openid-client derives `client_secret_post` from a string secret.
type ClientAuthentication =
    interface end

/// The authorization-request parameters `buildAuthorizationUrl` puts on the query.
type AuthorizationParameters =
    abstract redirect_uri : string with get, set
    abstract scope : string with get, set
    abstract state : string with get, set
    abstract code_challenge : string with get, set
    abstract code_challenge_method : string with get, set

/// The checks `authorizationCodeGrant` holds the callback to.
type AuthorizationChecks =
    abstract pkceCodeVerifier : string with get, set
    abstract expectedState : string with get, set

// The real signature is (server, clientId, metadata, clientAuthentication, options) —
// options is the FIFTH argument, and a string `metadata` is the client secret.
[<Import("discovery", "openid-client")>]
let private discoveryWith
    (server: Url)
    (clientId: string)
    (clientSecret: string)
    (clientAuthentication: ClientAuthentication option)
    (options: DiscoveryOptions)
    : JS.Promise<Configuration> =
    jsNative

/// Fetch `<server>/.well-known/openid-configuration` and bind client credentials.
/// `clientSecret` as a string selects the default `client_secret_post` authentication.
/// `options.execute = [| allowInsecureRequests |]` permits a plain-HTTP issuer.
let discovery (server: Url) (clientId: string) (clientSecret: string) (options: DiscoveryOptions) : JS.Promise<Configuration> =
    discoveryWith server clientId clientSecret None options

/// Execute-option for `discovery`: allow `http://` (non-TLS) requests. Required here
/// because the issuer is loopback HTTP (`http://127.0.0.1:<port>`) — the RFC 8252
/// native-app pattern; the whole deployment is single-machine loopback by design.
[<Import("allowInsecureRequests", "openid-client")>]
let allowInsecureRequests : ConfigurationHook = jsNative

[<Import("randomPKCECodeVerifier", "openid-client")>]
let randomPKCECodeVerifier () : string = jsNative

/// S256 challenge for a verifier (async: WebCrypto digest).
[<Import("calculatePKCECodeChallenge", "openid-client")>]
let calculatePKCECodeChallenge (verifier: string) : JS.Promise<string> = jsNative

[<Import("randomState", "openid-client")>]
let randomState () : string = jsNative

/// Build the authorization-endpoint URL from the request's parameters.
[<Import("buildAuthorizationUrl", "openid-client")>]
let buildAuthorizationUrl (config: Configuration) (parameters: AuthorizationParameters) : Url = jsNative

/// Redeem the code carried by `currentUrl` (the callback URL) under `checks`. Performs the
/// token-endpoint exchange AND validates the ID token; rejects on any protocol or
/// validation failure.
[<Import("authorizationCodeGrant", "openid-client")>]
let authorizationCodeGrant (config: Configuration) (currentUrl: Url) (checks: AuthorizationChecks) : JS.Promise<TokenEndpointResponse> = jsNative

/// A WHATWG `URL` from an absolute href (openid-client's URL-typed inputs).
[<Emit("new URL($0)")>]
let newUrl (href: string) : Url = jsNative

/// A WHATWG `URL` from a possibly-relative href resolved against a base — how the
/// session rebuilds its absolute callback URL from Node's request-relative `req.url`.
[<Emit("new URL($0, $1)")>]
let newUrlWithBase (href: string) (baseHref: string) : Url = jsNative
