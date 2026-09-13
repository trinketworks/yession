module Yession.Host.ClaudeConnection

// Everything Claude-specific lives HERE, in the session — the Manager's broker is
// standards-only and never learns which service it brokered, and nothing above this file
// knows which provider answered. This module owns: the Anthropic OAuth endpoints and
// Claude Code's public client id (sent to the broker as data), the reserved storage name,
// pasted-token classification, the credential→env-var mapping the Agent SDK consumes, the
// models lookup behind the session's provider-neutral catalogue, and the browser-facing
// /claude* routes the client panel drives.

open Fable.Core
open Fable.Core.JsInterop
open Yession.Domain
open Yession.Domain.Sandboxes
open Yession.Domain.Agent
open Yession.Domain.Access
open Yession.Manager
open Yession.SessionProcess
open Yession.App
open Yession.Host.Interop

#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

/// The reserved storage name for the Claude credential, per scope. Opaque to the
/// Manager — Claude-ness lives in this session-side choice.
let secretName : SecretName =
    match SecretName.create "claude-code" with
    | Ok name -> name
    | Error e -> failwithf "claude secret name invariant violated: %s" e

/// Claude Code's public OAuth client against claude.ai — the same flow `claude /login`
/// drives. Its registered redirect URIs are Anthropic's own (this Manager's callback
/// cannot be registered, and the client rejects unregistered URIs), so the flow
/// redirects to Anthropic's code-display page and completion arrives as a pasted
/// `code#state`. `code=true` asks the consent page to display the code.
///
/// Anthropic's terms restrict a subscription OAuth token to Claude Code and claude.ai, so
/// driving the Agent SDK on one is the operator's call, made when they click Connect — not
/// something this repo asserts on their behalf. A Console API key through the same paste
/// surface is the sanctioned path, which is why `classifyPasted` accepts both kinds rather
/// than steering to the OAuth flow.
let private authorizeUrl = "https://claude.ai/oauth/authorize?code=true"
let private tokenUrl = "https://console.anthropic.com/v1/oauth/token"
let private clientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e"
let private redirectUri = "https://console.anthropic.com/oauth/code/callback"
let private scopes = "org:create_api_key user:profile user:inference"

/// The broker request for one sign-in flow: Claude's endpoints as data.
///
/// `JsonEncoded` is one of those facts, not a broker default: Anthropic's token endpoint
/// answers a standards-correct form body with
/// `invalid_request_error: "Invalid request format"`, and its own clients (the Anthropic
/// SDK's `userOAuthProvider`, `claude /login`) post JSON — with `state` replayed in the
/// body, which the dialect carries.
let beginRequest (target: SecretId) : ControlWire.ConnectionBeginRequest =
    { Target = target
      AuthorizeUrl = envOr "YESSION_CLAUDE_AUTHORIZE_URL" authorizeUrl
      TokenUrl = envOr "YESSION_CLAUDE_TOKEN_URL" tokenUrl
      ClientId = envOr "YESSION_CLAUDE_CLIENT_ID" clientId
      Scopes = scopes
      RedirectUri = Some (envOr "YESSION_CLAUDE_REDIRECT_URI" redirectUri)
      TokenDialect = JsonEncoded }

/// Validate a pasted static credential: a `claude setup-token` token
/// (`sk-ant-oat01-…`) or a Console API key (`sk-ant-…`). Anything else is a paste
/// mistake worth rejecting before it is stored.
let classifyPasted (raw: string) : Result<string, string> =
    let trimmed = raw |> Option.ofObj |> Option.map (fun r -> r.Trim ()) |> Option.defaultValue ""
    if trimmed.StartsWith "sk-ant-" then Ok trimmed
    else Error "expected a Claude credential (sk-ant-oat01-… setup token or sk-ant-… API key)"

/// The environment variable a resolved credential rides into the Agent SDK's spawned
/// CLI: brokered OAuth access tokens and setup tokens go in `CLAUDE_CODE_OAUTH_TOKEN`;
/// Console API keys in `ANTHROPIC_API_KEY`.
let envVarFor (kind: ConnectionKind) (value: string) : string * string =
    match kind with
    | OAuthConnection -> "CLAUDE_CODE_OAUTH_TOKEN", value
    | StaticConnection ->
        if value.StartsWith "sk-ant-oat" then "CLAUDE_CODE_OAUTH_TOKEN", value
        else "ANTHROPIC_API_KEY", value

/// The two sign-in scopes the panel offers: this session only, or the signing actor's
/// own scope (usable from every session that actor is signed into).
let targetFor (sessionId: SessionId) (owner: CredentialOwner) (scopeChoice: string) : Result<SecretId, string> =
    match scopeChoice with
    | "session" -> Ok { Scope = SessionScope sessionId; Name = secretName }
    | "mine" -> Ok { Scope = CredentialOwner.scope owner; Name = secretName }
    | other -> Error (sprintf "unknown scope choice '%s' (expected 'session' or 'mine')" other)

/// The per-turn credential targets, most specific first: the session's own explicit
/// credential, then the turn actor's own, then the deployment's. Mirrors secret-injection
/// precedence.
///
/// `LocalScope` is named on EVERY turn, with no test of how this deployment authenticates
/// — the session does not know and does not need to. A launch only ever sees a target in
/// the Manager's status stream if the Manager holds it readable, and an attributed launch
/// is never granted local access, so the candidate filter drops it before anything is
/// resolved. The Manager's readable set is the single authority; a second copy of that
/// judgement here could only drift from it.
let turnTargets (sessionId: SessionId) (credential: CredentialFor) : SecretId list =
    [ Some { SecretId.Scope = SessionScope sessionId; SecretId.Name = secretName }
      CredentialFor.person credential
      |> Option.bind CredentialOwner.ofPrincipal
      |> Option.map (fun owner -> { SecretId.Scope = CredentialOwner.scope owner; SecretId.Name = secretName })
      Some { SecretId.Scope = LocalScope; SecretId.Name = secretName } ]
    |> List.choose id

// --- the models lookup ------------------------------------------------------------------
// The one Claude-shaped thing behind the session's provider-neutral catalogue: an endpoint,
// two header dialects, and a paged reply. `AgentModel` is what comes out, so the route, the
// synced register and the picker never learn any of it.

/// Anthropic's model listing. Overridable for the same reason the OAuth endpoints are: a
/// test needs somewhere to point it that is not the live provider.
let private modelsUrl = "https://api.anthropic.com/v1/models"

type private ModelsOutcome =
    { Ok : bool
      Reason : string
      /// The provider's status, or 0 when it never answered. Kept apart from `Reason`
      /// because one number decides something no prose can: whether the CREDENTIAL was
      /// refused, or this lookup merely failed.
      Status : int
      /// Each row as the provider gave it: its id, and the name it displays under.
      Models : (string * string) list }

/// One page of the models endpoint's reply, and one row of it, as F# reads the JSON.
/// Everything is nullable because everything is optional: the reply is somebody else's.
type private ModelRow =
    abstract id : string
    abstract display_name : string

type private ModelsPage =
    abstract data : ModelRow array
    abstract has_more : bool
    abstract last_id : string

/// Why a catalogue lookup produced nothing, and the one distinction its caller acts on.
///
/// `Refused` is a fact about the credential and belongs back at the Manager; everything else
/// is a fact about this request and belongs nowhere but the picker's note.
type ModelsFailure = { Message : string; Refused : bool }

/// How a credential presents itself to this provider.
///
/// The credential PAIR decides the dialect, which is why this takes the same
/// `(envVar, value)` `envVarFor` produces rather than a bare string: a Console API key
/// authenticates with `x-api-key`, and an OAuth access token with a bearer header plus the
/// beta opt-in Claude Code's own client sends. One value, one rule, no guessing at the
/// shape of a secret.
let modelsHeaders (envVar: string) (value: string) : (string * string) list =
    [ yield "anthropic-version", "2023-06-01"
      if envVar = "ANTHROPIC_API_KEY" then
          yield "x-api-key", value
      else
          yield "authorization", "Bearer " + value
          yield "anthropic-beta", "oauth-2025-04-20" ]

/// How long one page of the catalogue may take.
///
/// Bounded, because the connection panel's status reply waits on this: a provider that
/// accepts a socket and never answers would otherwise take the panel with it, and a lookup
/// that cannot finish IS a lookup that failed.
let private pageDeadlineMs = 10000.0

/// The rows of one page. A reply with no `data` is a page with no rows, not a failure —
/// the reply is somebody else's and this side reads what it can.
let private rowsOf (page: ModelsPage) : ModelRow array =
    if isNull (box page.data) then [||] else page.data

/// A field the provider left out, as the empty string. Every row is read this way, so a
/// half-filled one costs its own name rather than the whole lookup.
let private textOf (value: string) : string = if isNull (box value) then "" else value

/// One page's JSON, or why it could not be read. A provider that answers 200 with
/// something that is not JSON has failed this lookup without failing the request, which is
/// why the reason comes back here rather than as a status.
let private pageOf (body: string) : Result<ModelsPage, string> =
    try Ok (unbox<ModelsPage> (JS.JSON.parse body))
    with error -> Error (Http.reasonOf error)

/// GET the catalogue on one credential, following the API's paging.
///
/// The page bound is a runaway guard, not a coverage cap: the API's own maximum page is
/// 1000, so ten pages is ten thousand models and no provider is near it.
let private fetchModels (envVar: string) (value: string) (url: string) : Async<ModelsOutcome> =
    async {
        let request = [ Http.headers (modelsHeaders envVar value); Http.deadline pageDeadlineMs ]
        let models = ResizeArray<string * string> ()
        let mutable next = url + "?limit=1000"
        let mutable page = 0
        let mutable settled : ModelsOutcome option = None
        while settled.IsNone && page < 10 do
            page <- page + 1
            let! attempt = Http.text next request
            match attempt with
            | Http.Unreachable reason -> settled <- Some { Ok = false; Reason = reason; Status = 0; Models = [] }
            | Http.Answered (response, body) when not response.Ok ->
                let detail = body.Substring (0, min 200 body.Length)
                settled <-
                    Some
                        { Ok = false
                          Reason = sprintf "the provider answered %d: %s" response.Status detail
                          Status = response.Status
                          Models = [] }
            | Http.Answered (_, body) ->
                match pageOf body with
                | Error reason -> settled <- Some { Ok = false; Reason = reason; Status = 0; Models = [] }
                | Ok read ->
                    for row in rowsOf read do
                        models.Add (textOf row.id, textOf row.display_name)
                    if not read.has_more || System.String.IsNullOrEmpty read.last_id then
                        settled <- Some { Ok = true; Reason = ""; Status = 200; Models = List.ofSeq models }
                    else
                        next <- url + "?limit=1000&after_id=" + Http.urlPart read.last_id
        return settled |> Option.defaultValue { Ok = true; Reason = ""; Status = 200; Models = List.ofSeq models }
    }

/// The models one credential can see at one endpoint, as the provider-neutral pair the
/// rest of the session speaks. An id the smart constructor refuses is DROPPED rather than
/// failing the whole lookup: one malformed row in a provider's reply is not a reason to
/// leave somebody without a picker.
///
/// The endpoint is a parameter so a test can point it at a provider it wrote — which is
/// the only way the paging and the header dialects get exercised without a live account,
/// and the only way to do it without a suite writing the process environment.
let modelsAt (url: string) (credential: string * string) : Async<Result<AgentModel list, ModelsFailure>> =
    async {
        let envVar, value = credential
        let! outcome = fetchModels envVar value url
        if not outcome.Ok then
            // Only 401. A 403 here is an org policy or a scope this key does not carry, both
            // of which happen to a credential that is otherwise perfectly alive, and a 5xx or
            // an unreachable host says nothing about the credential at all.
            return Error { Message = outcome.Reason; Refused = outcome.Status = 401 }
        else
            return
                outcome.Models
                |> List.choose (fun (id, name) ->
                    match ModelId.create id with
                    | Ok created -> Some (AgentModel.create created name)
                    | Error _ -> None)
                |> Ok
    }

/// The lookup as the session composes it: this provider's endpoint, overridable the way
/// its OAuth endpoints are.
let models (credential: string * string) : Async<Result<AgentModel list, ModelsFailure>> =
    modelsAt (envOr "YESSION_CLAUDE_MODELS_URL" modelsUrl) credential

/// A human label for whose credential a call ran on, for the "not connected" failure
/// message. `None` is a call on nobody's — a deployment that attributes nobody, acting as
/// itself — and "the system" is what that is called in the log, not what a person reading
/// "no Claude account connected for …" needs to be told.
let actorLabel (credential: CredentialFor) : string =
    match credential with
    | CredentialFor.Person (Principal.User u) -> UserId.value u
    | CredentialFor.Person (Principal.Peer p) -> sprintf "peer %s" (PeerId.value p)
    | CredentialFor.Deployment -> "this deployment"

// --- the browser-facing /claude* routes -----------------------------------------------
// Thin proxies over the Manager's broker, gated by the same cookie identity as /me.
// The browser's scope choice becomes a target; who owns it comes from the COOKIE, and the
// Manager's policy is the authority (a launch that was never granted local access, or a
// user never bound to it, is denied there).
//
// The browser asserts no identity here at all any more. It used to send its own peer id
// and have the credential owned by it — see `ownerOf`.

type private ClaudeRequestBody =
    { Scope : string
      Code : string option
      Token : string option }

let private bodyDecoder : Decoder<ClaudeRequestBody> =
    Decode.object (fun get ->
        { Scope = get.Optional.Field "scope" Decode.string |> Option.defaultValue "mine"
          Code = get.Optional.Field "code" Decode.string
          Token = get.Optional.Field "token" Decode.string })

let private readBody (req: IncomingMessage) (cont: string -> unit) =
    let mutable acc = ""
    req.on ("data", fun chunk -> acc <- acc + bufferToString chunk) |> ignore
    req.on ("end", fun _ -> cont acc) |> ignore

let private respondJson (res: ServerResponse) (status: int) (json: string) =
    res.writeHead (status, createObj [ "content-type", box "application/json"; "cache-control", box "no-store" ]) |> ignore
    res.``end`` json

let private respondText (res: ServerResponse) (status: int) (text: string) =
    res.writeHead (status, createObj [ "content-type", box "text/plain"; "cache-control", box "no-store" ]) |> ignore
    res.``end`` text

let private jsonString (raw: string) : string = Encode.toString 0 (Encode.string raw)

/// The credential owner behind a browser request: the cookie's Manager-verified user, or
/// — where this deployment attributes nobody — the deployment itself.
///
/// Total, and it asks the browser for nothing. It used to take the browser's self-asserted
/// peer id and own the credential by that; a peer id lives in origin-partitioned
/// localStorage, so it changed under the person holding it and stranded the credential
/// behind every new one. The cookie's attribution is Manager-minted and is the whole input.
let ownerOf (identity: CookieIdentity) : CredentialOwner =
    match identity.Attribution with
    | AttributedUser user -> UserOwner user
    | UnattributedAccess -> LocalOwner

/// The party a browser request's provider calls run on (`PeerAttribution.credential`), so
/// the catalogue it is answered with is the one this person's credential can actually see.
let private actorOf (identity: CookieIdentity) : CredentialFor = PeerAttribution.credential identity.Attribution

/// Build the /claude* route handler. `statusOf` reads the session's live status cache
/// (fed by the Manager's connection stream); `agentAvailable` is the agent gate's own
/// truth (any relevant credential OR the ambient env) — served so the client can say
/// "no agent in this session" honestly; `list` is the model catalogue, on the reply for
/// the reason below; `connections` is the control-channel broker client. Composes into
/// `Signalling.start` extra routes.
let routes
    (sessionId: SessionId)
    (auth: SessionAuth.Auth)
    (connections: ControlClient.SessionConnections)
    (statusOf: SecretId -> ConnectionStatus option)
    (agentAvailable: unit -> bool)
    (list: ListModels)
    /// The path this session is served under (`""` at an origin root), stripped off the
    /// request the same way the rest of the session's surface strips it.
    (mount: string)
    : IncomingMessage -> ServerResponse -> bool =
    fun req res ->
        let routeOf () = SessionRoute.parseUnder mount req.``method`` (req.url.Split('?').[0])
        // The session's Claude routes, claimed through the same `SessionRoute` contract the
        // rest of its surface uses — so a route added there is unhandled here until this
        // match accounts for it.
        match routeOf () with
        | Some ClaudeStatus
        | Some (Claude _) ->
            match auth.IdentityOf req with
            | None -> respondText res 401 "unauthorized"
            | Some identity ->
                let handle (body: ClaudeRequestBody) : unit =
                    let owner = ownerOf identity
                    let kindLabel kind = match kind with OAuthConnection -> "oauth" | StaticConnection -> "static"
                    match routeOf () with
                    | Some ClaudeStatus ->
                        // One connection as the panel reads it: which kind of credential it
                        // is, and — when something has established that it no longer works —
                        // why a person has to sign in again. `null` for a scope with nothing
                        // connected. The GitHub panel reads the same shape from its own route;
                        // both are pinned by their route suites.
                        let statusJson (target: SecretId) =
                            match statusOf target with
                            | None -> "null"
                            | Some (status: ConnectionStatus) ->
                                let signInRequired =
                                    match status.Health with
                                    | ConnectionUsable -> "null"
                                    | SignInRequired reason -> jsonString reason
                                sprintf
                                    """{"kind":%s,"signInRequired":%s}"""
                                    (jsonString (kindLabel status.Kind))
                                    signInRequired
                        let sessionTarget : SecretId = { Scope = SessionScope sessionId; Name = secretName }
                        let mineTarget : SecretId = { Scope = CredentialOwner.scope owner; Name = secretName }
                        // What "mine" MEANS here, so the panel can say it honestly:
                        // one person's credential, or this whole deployment's.
                        let ownerLabel =
                            match owner with
                            | UserOwner _ -> "user"
                            | LocalOwner -> "local"
                        // The catalogue rides the status rather than answering on a route
                        // of its own. It is the same question one line further on — what
                        // can a turn run on here — and `agent` beside it is already the
                        // first line of that answer. Split across two routes with two
                        // refresh triggers, the second one drifted: the picker sat on a
                        // refusal computed before the account it named existed, because
                        // signing in re-probed the status and nothing re-asked for the
                        // models. One reply cannot disagree with itself.
                        //
                        // `models` is the list or null, and `modelsUnavailable` the reason
                        // it is null — the same null-or-reason shape as `signInRequired`
                        // above, and never both. "This provider offers nothing" and
                        // "nobody has connected an account" are different facts, and a
                        // picker that could not tell them apart would show an empty menu
                        // with no way to fix it.
                        Async.StartImmediate (
                            async {
                                let! catalogue = list (actorOf identity)
                                let models, unavailable =
                                    match catalogue with
                                    | Ok models -> Codec.toString Codec.modelCatalogue models, "null"
                                    | Error reason -> "null", jsonString reason
                                respondJson res 200
                                    (sprintf
                                        """{"session":%s,"mine":%s,"owner":"%s","agent":%b,"models":%s,"modelsUnavailable":%s}"""
                                        (statusJson sessionTarget)
                                        (statusJson mineTarget)
                                        ownerLabel
                                        (agentAvailable ())
                                        models
                                        unavailable)
                            })
                    | Some (Claude action) ->
                        match targetFor sessionId owner body.Scope with
                        | Error e -> respondText res 400 e
                        | Ok target ->
                            let respondOutcome (outcome: Result<string, string>) =
                                match outcome with
                                | Ok json -> respondJson res 200 json
                                | Error e -> respondText res 400 e
                            Async.StartImmediate (
                                async {
                                    match action with
                                    | ClaudeAction.Begin ->
                                        let! outcome = connections.Begin (beginRequest target)
                                        respondOutcome (
                                            outcome
                                            |> Result.map (fun r ->
                                                sprintf """{"authorizeUrl":%s,"state":%s}"""
                                                    (jsonString r.AuthorizeUrl) (jsonString r.State)))
                                    | ClaudeAction.Complete ->
                                        match body.Code with
                                        | None -> respondText res 400 "missing code"
                                        | Some code ->
                                            let! outcome = connections.Complete target code
                                            respondOutcome (outcome |> Result.map (fun () -> """{"ok":true}"""))
                                    | ClaudeAction.Token ->
                                        match body.Token |> Option.map classifyPasted with
                                        | None -> respondText res 400 "missing token"
                                        | Some (Error e) -> respondText res 400 e
                                        | Some (Ok token) ->
                                            let! outcome = connections.Put target token
                                            respondOutcome (outcome |> Result.map (fun () -> """{"ok":true}"""))
                                    | ClaudeAction.Disconnect ->
                                        let! outcome = connections.Disconnect target
                                        respondOutcome (
                                            outcome
                                            |> Result.map (fun existed ->
                                                sprintf """{"disconnected":%b}""" existed))
                                })
                    // Unreachable: this handler only runs for the two cases above.
                    | Some _
                    | None -> respondText res 404 "not found"
                match req.``method`` with
                | "GET" ->
                    handle
                        { Scope = "mine"
                          Code = None
                          Token = None }
                | _ ->
                    readBody req (fun raw ->
                        match Decode.fromString bodyDecoder (if raw.Trim () = "" then "{}" else raw) with
                        | Ok body -> handle body
                        | Error e -> respondText res 400 (sprintf "malformed request: %s" e))
            true
        // Not this handler's path: the composing server falls through (to its 404).
        | Some _
        | None -> false
