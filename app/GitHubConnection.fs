module Yession.Host.GitHubConnection

// Everything GitHub-specific about signing in (Plan 14) lives HERE, in the session —
// exactly the ClaudeConnection precedent. GitHub's token exchange for the
// authorization-code grant demands the App's client SECRET, which the Manager's
// standards-only public-client broker deliberately cannot carry — so this connection
// uses the DEVICE FLOW instead (client id only, no secret anywhere): the session asks
// github.com for a user code, the human approves it in their own browser, and the
// session polls the token endpoint until the grant lands. What that produces is a whole
// authorization rather than a bare string, so it is stored through the broker's GRANT leg
// (`PutGrant` → `BrokeredOAuth`, Plan 21): the refresh token stays Manager-side and the
// access token rotates before the turns that need it. The Manager still never learns which
// service it stored. The paste leg remains for a PAT, which genuinely is a bare string.
//
// The token is a GitHub App user-to-server token: what it can reach is the
// intersection of the USER's access and the APP's installations — which is the
// "repos must be covered by the App installation" rule, enforced by the credential
// itself rather than by any check written here. A pasted PAT bypasses that rule
// (documented in GAPS).

open System
open Fable.Core
open Fable.Core.JsInterop
open Yession.Domain
open Yession.Domain.Sandboxes
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

/// The reserved storage name for the GitHub credential, per scope. Opaque to the
/// Manager — GitHub-ness lives in this session-side choice. (A pre-existing generic
/// secret literally named `github` would be shadowed by a sign-in; accepted, as for
/// `claude-code`.)
let secretName : SecretName =
    match SecretName.create "github" with
    | Ok name -> name
    | Error e -> failwithf "github secret name invariant violated: %s" e

/// The same name as the CONNECTION a sandbox forwards and a sentence points at — one
/// spelling, beside the secret it stores under, so the two cannot drift.
let connectionName : ConnectionName =
    match ConnectionName.create "github" with
    | Ok name -> name
    | Error e -> failwithf "github connection name invariant violated: %s" e

/// GitHub's device-flow endpoints. There is no default client id: the operator
/// registers their own GitHub App (device flow enabled; user-token expiration may stay
/// on — the grant leg stores the refresh token with the Manager, which rotates the
/// access token before each turn that needs it) and names its client id in
/// `YESSION_GITHUB_CLIENT_ID`. Endpoints are overridable the way the Claude ones are,
/// which is what the stub-server tests drive.
let private deviceCodeUrl = "https://github.com/login/device/code"
let private tokenUrl = "https://github.com/login/oauth/access_token"

let private configuredClientId () : string option =
    match envOr "YESSION_GITHUB_CLIENT_ID" "" with
    | "" -> None
    | id -> Some id

/// Validate a pasted static credential: a fine-grained PAT (`github_pat_…`), a classic
/// PAT (`ghp_…`), or a user token from an App/OAuth flow run elsewhere (`ghu_…`,
/// `gho_…`). Anything else is a paste mistake worth rejecting before it is stored.
///
/// The two user-token kinds are refused where the device flow can be run instead, because
/// this leg stores `BrokeredStatic` — a credential `needsRefresh` answers `false` for,
/// unconditionally. A `ghu_`/`gho_` lives about eight hours, so pasting one here mints a
/// credential that is dead by morning and cannot rotate; `Connect GitHub` runs the same
/// authorization and lands it through `PutGrant`, refresh token and all. That is not
/// hypothetical: the credential on the author's own deployment was pasted this way 39
/// minutes before the grant leg shipped, expired, and then read as a healthy connection
/// for four days. Where no App is configured (`deviceFlowConfigured = false`) paste is the
/// only path there is, so they are still accepted.
let classifyPasted (deviceFlowConfigured: bool) (raw: string) : Result<string, string> =
    let trimmed = raw |> Option.ofObj |> Option.map (fun r -> r.Trim ()) |> Option.defaultValue ""
    let durable = [ "github_pat_"; "ghp_" ]
    let expiring = [ "ghu_"; "gho_" ]
    if durable |> List.exists trimmed.StartsWith then Ok trimmed
    elif expiring |> List.exists trimmed.StartsWith && not deviceFlowConfigured then Ok trimmed
    elif expiring |> List.exists trimmed.StartsWith then
        Error
            "a ghu_…/gho_… user token expires in a few hours and cannot be refreshed once pasted \
             — use Connect GitHub, which stores a refresh token"
    else
        // The kinds this session will actually take, which is not the same list on both
        // sides of the branch above — offering one it is about to refuse would be the
        // message sending someone back for the token it just rejected.
        let kinds =
            if deviceFlowConfigured then "github_pat_…/ghp_… personal access token"
            else "github_pat_…/ghp_… personal access token, or a ghu_…/gho_… user token"
        Error (sprintf "expected a GitHub credential (%s)" kinds)

/// The two sign-in scopes the panel offers — identical to the Claude mapping.
let targetFor (sessionId: SessionId) (owner: CredentialOwner) (scopeChoice: string) : Result<SecretId, string> =
    match scopeChoice with
    | "session" -> Ok { Scope = SessionScope sessionId; Name = secretName }
    | "mine" -> Ok { Scope = CredentialOwner.scope owner; Name = secretName }
    | other -> Error (sprintf "unknown scope choice '%s' (expected 'session' or 'mine')" other)

/// The per-operation credential targets, most specific first: the session's own explicit
/// credential, then the acting human's, then the deployment's. Mirrors
/// `ClaudeConnection.turnTargets` — including why `LocalScope` is named unconditionally.
let turnTargets (sessionId: SessionId) (credential: CredentialFor) : SecretId list =
    [ Some { SecretId.Scope = SessionScope sessionId; SecretId.Name = secretName }
      CredentialFor.person credential
      |> Option.bind CredentialOwner.ofPrincipal
      |> Option.map (fun owner -> { SecretId.Scope = CredentialOwner.scope owner; SecretId.Name = secretName })
      Some { SecretId.Scope = LocalScope; SecretId.Name = secretName } ]
    |> List.choose id

// --- the device flow, as data ----------------------------------------------------------

/// What `POST /login/device/code` answered: the code pair and how to pace the polling.
type DeviceCodeGrant =
    { DeviceCode : string
      UserCode : string
      VerificationUri : string
      /// Seconds between polls, per GitHub. The browser paces itself by this; the
      /// server does not enforce it — GitHub answers `slow_down` if it is ignored.
      Interval : int }

let deviceCodeDecoder : Decoder<DeviceCodeGrant> =
    Decode.object (fun get ->
        { DeviceCode = get.Required.Field "device_code" Decode.string
          UserCode = get.Required.Field "user_code" Decode.string
          VerificationUri = get.Required.Field "verification_uri" Decode.string
          Interval = get.Optional.Field "interval" Decode.int |> Option.defaultValue 5 })

/// One poll of the token endpoint, folded to what the panel needs to know. The
/// device-flow spec (RFC 8628) answers 200 for BOTH outcomes and the in-between, so
/// everything decodes from the body, never the status code.
/// What the token endpoint handed back when the flow succeeded, kept whole.
///
/// It used to be the access token alone, which threw away the two facts that decide
/// whether the credential can ever rotate — and made the App's "user-token expiration"
/// setting something an operator had to turn OFF for this to work at all. A grant that
/// states no lifetimes is still a grant; it simply never comes due.
type PollGrant =
    { AccessToken : string
      RefreshToken : string option
      ExpiresIn : int option
      RefreshTokenExpiresIn : int option }

type PollOutcome =
    /// The human has not approved yet — keep polling at `interval` seconds.
    | PollPending of interval: int
    /// The grant landed, with whatever lifetimes the provider stated.
    | PollGranted of PollGrant
    /// The flow is dead (expired, denied) — start again. The reason is shown as-is.
    | PollFailed of reason: string

/// Fold a token-endpoint response body into an outcome. `interval` is the pace the
/// flow already had; `slow_down` widens it by the spec's fixed 5 seconds.
let pollOutcome (currentInterval: int) (body: string) : PollOutcome =
    let field name = Decode.fromString (Decode.field name Decode.string) body |> Result.toOption
    // The lifetimes are numbers on the wire, and GitHub omits them entirely when the App
    // has token expiration disabled — so a missing one means "not stated", never zero.
    let seconds name = Decode.fromString (Decode.field name Decode.int) body |> Result.toOption
    match field "access_token" with
    | Some token ->
        PollGranted
            { AccessToken = token
              RefreshToken = field "refresh_token"
              ExpiresIn = seconds "expires_in"
              RefreshTokenExpiresIn = seconds "refresh_token_expires_in" }
    | None ->
        match field "error" with
        | Some "authorization_pending" -> PollPending currentInterval
        | Some "slow_down" -> PollPending (currentInterval + 5)
        | Some "expired_token" -> PollFailed "the device code expired — start the sign-in again"
        | Some "access_denied" -> PollFailed "the sign-in was denied on github.com"
        | Some other ->
            field "error_description"
            |> Option.defaultValue other
            |> PollFailed
        | None -> PollFailed "unrecognised reply from the token endpoint"

/// What the Manager needs to refresh this grant later, said once here because the SESSION
/// is the only party that knows it: the endpoint the token came from, the client id it was
/// minted for, and the dialect that endpoint speaks.
///
/// `FormEncoded` because GitHub's token endpoint takes a form body (the JSON answer is a
/// matter of the `accept` header, which the broker already sends). And the refresh needs no
/// client secret precisely BECAUSE this was the device flow — GitHub requires one for every
/// other way of getting a user token, which is what made the broker's public-client shape
/// fit this provider at all.
let grantRequest (target: SecretId) (granted: PollGrant) : ControlWire.ConnectionPutGrantRequest =
    { Target = target
      AccessToken = granted.AccessToken
      RefreshToken = granted.RefreshToken
      ExpiresIn = granted.ExpiresIn
      RefreshTokenExpiresIn = granted.RefreshTokenExpiresIn
      TokenUrl = envOr "YESSION_GITHUB_TOKEN_URL" tokenUrl
      ClientId = configuredClientId () |> Option.defaultWith (fun () -> failwith "the GitHub device-flow grant requires a configured client id")
      TokenDialect = FormEncoded }

// --- the browser-facing /github* routes -------------------------------------------------
// Thin proxies, gated by the same cookie identity as /me — the ClaudeConnection shape,
// with `Poll` where Claude's pasted-code `Complete` stands. The device code never
// leaves the session: the browser is told the USER code and where to type it, and each
// `Poll` from the panel drives one session→github.com poll of the pending flow.

type private GitHubRequestBody =
    { Scope : string
      Token : string option }

let private bodyDecoder : Decoder<GitHubRequestBody> =
    Decode.object (fun get ->
        { Scope = get.Optional.Field "scope" Decode.string |> Option.defaultValue "mine"
          Token = get.Optional.Field "token" Decode.string })

let private respondJson (res: ServerResponse) (status: int) (json: string) =
    res.writeHead (status, createObj [ "content-type", box "application/json"; "cache-control", box "no-store" ]) |> ignore
    res.``end`` json

let private respondText (res: ServerResponse) (status: int) (text: string) =
    res.writeHead (status, createObj [ "content-type", box "text/plain"; "cache-control", box "no-store" ]) |> ignore
    res.``end`` text

let private jsonString (raw: string) : string = Encode.toString 0 (Encode.string raw)

/// The credential owner behind a browser request — identical to the Claude rule.
let ownerOf (identity: CookieIdentity) : CredentialOwner =
    match identity.Attribution with
    | AttributedUser user -> UserOwner user
    | UnattributedAccess -> LocalOwner

/// POST a JSON body to github.com. GitHub's OAuth endpoints answer form-encoded unless asked
/// for JSON, so the accept header is load-bearing.
///
/// `reached` is reported apart from `status`, and that separation is the point. Folded
/// together — as this port used to, with one `ok` for both — a phone changing radios and
/// github.com refusing a client id are the same value, so neither the classifier below nor
/// the person reading the panel can tell "we could not ask" from "we asked and were told no".
let deviceFlowHeaders : (string * string) list =
    [ "content-type", "application/json"
      "accept", "application/json" ]

let private postJson (url: string) (body: string) : Async<{| reached: bool; status: int; body: string |}> =
    async {
        let! attempt =
            Http.text
                url
                [ Fetch.Types.RequestProperties.Method Fetch.Types.HttpMethod.POST
                  Http.headers deviceFlowHeaders
                  Fetch.Types.RequestProperties.Body (U3.Case3 body) ]
        match attempt with
        | Http.Answered (response, said) -> return {| reached = true; status = response.Status; body = said |}
        | Http.Unreachable reason -> return {| reached = false; status = 0; body = reason |}
    }

/// Why a call to github.com produced nothing this session can use. The cases exist to be
/// told apart: one is this box's network, one is github.com's answer, one is neither side
/// having said anything yet.
type GitHubFault =
    | GitHubUnreachable of detail: string
    | GitHubRefused of status: int * body: string
    | GitHubTimedOut

module GitHubFault =

    /// What a person is shown. Every one of these NAMES THE LEG, because a bare
    /// `TypeError: Failed to fetch` on a panel is a sentence that fits a browser that could
    /// not reach the session and a session that could not reach github.com equally well, and
    /// the two have different cures.
    let describe (fault: GitHubFault) : string =
        match fault with
        | GitHubUnreachable detail ->
            sprintf "this session could not reach github.com: %s" (detail.Trim ())
        | GitHubRefused (status, body) ->
            let said = body.Trim ()
            if said = "" then sprintf "github.com answered %d" status
            else sprintf "github.com answered %d: %s" status said
        | GitHubTimedOut -> "github.com did not answer in time"

    /// Whether asking again can help — the shared HTTP rule, with nothing peculiar to add:
    /// every fault here is about reaching a provider rather than about a credential, which is
    /// the one thing this leg cannot fix by waiting.
    let verdict (fault: GitHubFault) : Resilience.Verdict =
        match fault with
        | GitHubUnreachable _ -> Resilience.Http.verdict Resilience.Http.Unreached
        | GitHubTimedOut -> Resilience.Http.verdict Resilience.Http.Unreached
        | GitHubRefused (status, _) -> Resilience.Http.verdict (Resilience.Http.Answered (status, None))

/// Posting to github.com as the routes see it: a settled body or a fault, with whatever
/// resilience the composition root put behind it. Takes its pair as a tuple because that is
/// the shape `Resilience.Policy.guard` decorates.
type GitHubPost = string * string -> Async<Result<string, GitHubFault>>

/// The unguarded leg: one POST, its outcome as a value. What SessionMain wraps.
let posting : GitHubPost =
    fun (url, body) ->
        async {
            let! reply = postJson url body
            if not reply.reached then return Error (GitHubUnreachable reply.body)
            elif reply.status >= 200 && reply.status < 300 then return Ok reply.body
            else return Error (GitHubRefused (reply.status, reply.body))
        }

/// How long one call to github.com may take before it counts as no answer.
///
/// Ten seconds because this is a human waiting on a panel: long enough that a slow mobile
/// leg still lands, short enough that `working…` cannot become the end of the story. Without
/// it there is no end of the story — a fetch that never settles leaves a flow that never
/// moves and a button that is no longer on screen to press again.
let callDeadline = TimeSpan.FromSeconds 10.0

/// The shipped policy for a call to github.com: three retries, exponentially backed off from
/// 400ms with a 5s ceiling, jittered. The ceiling is low on purpose — every one of these
/// calls has somebody watching it, so the budget is spent inside the time a person will wait
/// rather than stretched to survive an outage they would rather be told about.
let policy
    (sleep: TimeSpan -> Async<unit>)
    (random: unit -> float)
    : Resilience.Policy<GitHubFault> =
    { Schedule =
        Resilience.Schedule.exponential (TimeSpan.FromMilliseconds 400.0) 2.0 (TimeSpan.FromSeconds 5.0) 3
        |> Resilience.Schedule.jittered 0.5 random
      Classify = GitHubFault.verdict
      Sleep = sleep
      Observe = ignore }

/// The whole leg as it ships: each attempt bounded, the settled faults retried.
let resilient (sleep: TimeSpan -> Async<unit>) (random: unit -> float) (post: GitHubPost) : GitHubPost =
    post
    |> Resilience.Policy.deadline sleep callDeadline GitHubTimedOut
    |> Resilience.Policy.guard (policy sleep random)

/// Ask GitHub whether a token is still good, as GitHub itself.
///
/// The alternative was reading git's stderr, and that is not an answer: `Repository not
/// found` is what github.com says for a private repo you cannot see AND for a repo that is
/// not there, so a dead credential and a typo are the same sentence. One authenticated
/// request to the API distinguishes them definitively, and it is only ever made on a path
/// that has already failed.
///
/// The endpoint is a parameter for the same reason the OAuth ones are: a suite needs
/// somewhere to point it that is not the live provider.
///
/// The credential is the point of both calls below, so the bearer is never conditional;
/// `user-agent` is there because GitHub refuses a request without one.
let userHeaders (token: string) : (string * string) list =
    [ "authorization", "Bearer " + token
      "accept", "application/vnd.github+json"
      "user-agent", "yession" ]

/// This one turns on the status alone; the body is read and discarded, which is what
/// releases the connection rather than leaving it for the garbage collector.
let private getUser (url: string) (token: string) : Async<{| reachable: bool; status: int |}> =
    async {
        let! attempt = Http.text url [ Http.headers (userHeaders token) ]
        match attempt with
        | Http.Answered (response, _) -> return {| reachable = true; status = response.Status |}
        | Http.Unreachable _ -> return {| reachable = false; status = 0 |}
    }

let private userUrl = "https://api.github.com/user"

/// Whether GitHub still accepts this token: `Some reason` when it definitively does not.
///
/// Only 401 counts. A 403 is GitHub's answer for rate limiting and for scope refusals, both
/// of which happen to a perfectly good credential, and telling somebody to sign in again
/// because they made too many requests would be worse than saying nothing. Unreachable is
/// not a verdict either — that is this box's network, not the token.
let refusedAt (url: string) (token: string) : Async<string option> =
    async {
        let! reply = getUser url token
        if reply.reachable && reply.status = 401 then
            return Some "github rejected this credential"
        else return None
    }

/// The check as the session composes it, against GitHub's own endpoint.
let refused (token: string) : Async<string option> =
    refusedAt (envOr "YESSION_GITHUB_USER_URL" userUrl) token

/// Who a credential is, as GitHub says: what a commit made with it is authored as.
[<RequireQualifiedAccess>]
type Profile =
    { Login : string
      Id : int64
      /// The display name, when the account has one; `login` stands in when it does not.
      Name : string option
      /// The PUBLIC email, when the account shows one. Most do not.
      Email : string option }

/// What `GET /user` says about the account behind a token, read straight into the type the
/// caller wants. A second record carrying the same four labels is the same type twice, which
/// is what YES004 says about `Profile` — and the intermediate one earned its keep only while
/// the body was unboxed and read field by field.
///
/// `login` and `id` are REQUIRED: a reply carrying neither is not a profile whatever its
/// status said, and there is no honest stand-in for either. The other two stay options all
/// the way into the domain, which is what they already are there — an account with no display
/// name states `null`, and that is the absence rather than a name of no characters.
let private gitHubProfile : Decoder<Profile> =
    Decode.object (fun get ->
        { Profile.Login = get.Required.Field "login" Decode.string
          Id = get.Required.Field "id" Decode.int64
          Name = get.Optional.Field "name" Decode.string
          Email = get.Optional.Field "email" Decode.string })

/// The same endpoint, read for its body this time. Four outcomes, because the caller says
/// something different about each — and the one that used to be missing is the third: a reply
/// that arrived, with a status of its own, carrying no profile. That was reported as a host
/// nobody could reach, which sends a person to look at their network over somebody else's
/// answer.
type private ProfileReply =
    | Profiled of Profile
    | Refused of status: int
    | Unreachable
    | Unreadable

let private getProfile (url: string) (token: string) : Async<ProfileReply> =
    async {
        let! attempt = Http.text url [ Http.headers (userHeaders token) ]
        match attempt with
        | Http.Unreachable _ -> return Unreachable
        | Http.Answered (response, _) when not response.Ok -> return Refused response.Status
        | Http.Answered (_, body) ->
            match Decode.fromString gitHubProfile body with
            | Ok profile -> return Profiled profile
            | Error _ -> return Unreadable
    }

/// The profile behind a token, or why there is none — unreachable, refused, or an answer
/// with no profile in it, which is not a profile whatever the status said.
let profileAt (url: string) (token: string) : Async<Result<Profile, string>> =
    async {
        match! getProfile url token with
        | Profiled profile -> return Ok profile
        | Refused status -> return Error (sprintf "github answered %d" status)
        | Unreachable -> return Error "github could not be reached"
        | Unreadable -> return Error "github answered with no profile in it"
    }

/// As the session composes it.
let profile (token: string) : Async<Result<Profile, string>> =
    profileAt (envOr "YESSION_GITHUB_USER_URL" userUrl) token

/// The author a commit made with this credential carries: the account's name, and the
/// email GitHub itself attributes to it. The public email when the account shows one;
/// otherwise the noreply address GitHub mints for the account (`<id>+<login>@…`), which
/// is what its own web commits use and what its contribution graph credits — so a commit
/// pushed from a sandbox is attributed the way one made on github.com would be.
let commitIdentity (profile: Profile) : string * string =
    let name =
        match profile.Name with
        | Some name when name.Trim () <> "" -> name.Trim ()
        | _ -> profile.Login
    let email =
        match profile.Email with
        | Some email when email.Trim () <> "" -> email.Trim ()
        | _ -> sprintf "%d+%s@users.noreply.github.com" profile.Id profile.Login
    name, email

/// Build the /github* route handler. `statusOf` reads the session's live status cache
/// (the same Manager connection stream that feeds /claude — a stored `github` entry
/// appears there with no Manager changes, because status is envelope-shape detection).
/// Composes into `Signalling.start` extra routes beside the Claude handler.
let routes
    (sessionId: SessionId)
    (auth: SessionAuth.Auth)
    (connections: ControlClient.SessionConnections)
    (statusOf: SecretId -> ConnectionStatus option)
    (post: GitHubPost)
    (mount: string)
    : IncomingMessage -> ServerResponse -> bool =
    // The pending device flow per target, held HERE and only here: the device code is
    // the half of the grant that must not leave the session, and a flow is pending for
    // minutes at most (GitHub expires the code), so process memory is its whole life.
    let mutable pending : Map<SecretId, DeviceCodeGrant> = Map.empty
    fun req res ->
        let routeOf () = SessionRoute.parseUnder mount req.``method`` (req.url.Split('?').[0])
        match routeOf () with
        | Some GitHubStatus
        | Some (GitHub _) ->
            match auth.IdentityOf req with
            | None -> respondText res 401 "unauthorized"
            | Some identity ->
                let handle (body: GitHubRequestBody) : unit =
                    let owner = ownerOf identity
                    match routeOf () with
                    | Some GitHubStatus ->
                        // The same two rows the Claude panel shows, written by the same
                        // codec the browser reads (`Codec.githubPanel`). This file used to
                        // carry its own copy of the row encoder.
                        let sessionTarget : SecretId = { Scope = SessionScope sessionId; Name = secretName }
                        let mineTarget : SecretId = { Scope = CredentialOwner.scope owner; Name = secretName }
                        let panel : GitHubPanel =
                            { SessionCredential = CredentialRow.ofStatus (statusOf sessionTarget)
                              MineCredential = CredentialRow.ofStatus (statusOf mineTarget)
                              Owner = Some (match owner with UserOwner _ -> "user" | LocalOwner -> "local") }
                        respondJson res 200 (Codec.toString Codec.githubPanel panel)
                    | Some (GitHub action) ->
                        match targetFor sessionId owner body.Scope with
                        | Error e -> respondText res 400 e
                        | Ok target ->
                            Async.StartImmediate (
                                async {
                                    match action with
                                    | GitHubAction.Begin ->
                                        match configuredClientId () with
                                        | None ->
                                            respondText res 400
                                                "no GitHub App is configured (set YESSION_GITHUB_CLIENT_ID) — paste a token instead"
                                        | Some clientId ->
                                            let url = envOr "YESSION_GITHUB_DEVICE_URL" deviceCodeUrl
                                            let request = sprintf """{"client_id":%s}""" (jsonString clientId)
                                            match! post (url, request) with
                                            | Error fault -> respondText res 502 (GitHubFault.describe fault)
                                            // Not `body`: that name is the REQUEST body in this
                                            // scope, and one binding with two types under one
                                            // name is the shadow no analyzer sees and CI reports
                                            // as a mismatch in another file entirely.
                                            | Ok answer ->
                                                match Decode.fromString deviceCodeDecoder answer with
                                                | Error e -> respondText res 502 (sprintf "unrecognised device-code reply: %s" e)
                                                | Ok grant ->
                                                    pending <- Map.add target grant pending
                                                    respondJson res 200
                                                        (sprintf """{"userCode":%s,"verificationUri":%s,"interval":%d}"""
                                                            (jsonString grant.UserCode) (jsonString grant.VerificationUri) grant.Interval)
                                    | GitHubAction.Poll ->
                                        match Map.tryFind target pending, configuredClientId () with
                                        | None, _ -> respondText res 400 "no sign-in in progress for that scope — begin again"
                                        | Some _, None -> respondText res 400 "no GitHub App is configured (set YESSION_GITHUB_CLIENT_ID)"
                                        | Some grant, Some clientId ->
                                            let url = envOr "YESSION_GITHUB_TOKEN_URL" tokenUrl
                                            let request =
                                                sprintf """{"client_id":%s,"device_code":%s,"grant_type":"urn:ietf:params:oauth:grant-type:device_code"}"""
                                                    (jsonString clientId) (jsonString grant.DeviceCode)
                                            match! post (url, request) with
                                            // A poll that could not ASK is not a flow that has
                                            // ended. The device code is good for minutes and
                                            // the human may already have approved it, so a leg
                                            // that failed after its retries leaves the flow
                                            // exactly where it was and tells the panel to keep
                                            // waiting. Folding this into `pollOutcome` — which
                                            // is what a body-only reply forced — read a
                                            // `TypeError` as an unrecognised protocol answer
                                            // and threw the pending code away on one dropped
                                            // packet.
                                            | Error _ ->
                                                respondJson res 200 (sprintf """{"status":"pending","interval":%d}""" grant.Interval)
                                            | Ok answer ->
                                                match pollOutcome grant.Interval answer with
                                                | PollPending interval ->
                                                    if interval <> grant.Interval then
                                                        pending <- Map.add target { grant with Interval = interval } pending
                                                    respondJson res 200 (sprintf """{"status":"pending","interval":%d}""" interval)
                                                | PollGranted granted ->
                                                    pending <- Map.remove target pending
                                                    // Over the grant leg, not the paste leg: this
                                                    // is an authorization this session ran, and the
                                                    // Manager is the only place a refresh token may
                                                    // live (Plan 08).
                                                    match! connections.PutGrant (grantRequest target granted) with
                                                    | Ok () -> respondJson res 200 """{"status":"connected"}"""
                                                    | Error e -> respondText res 502 e
                                                | PollFailed reason ->
                                                    pending <- Map.remove target pending
                                                    respondText res 400 reason
                                    | GitHubAction.Token ->
                                        match body.Token |> Option.map (classifyPasted (configuredClientId ()).IsSome) with
                                        | None -> respondText res 400 "missing token"
                                        | Some (Error e) -> respondText res 400 e
                                        | Some (Ok token) ->
                                            match! connections.Put target token with
                                            | Ok () -> respondJson res 200 """{"ok":true}"""
                                            | Error e -> respondText res 400 e
                                    | GitHubAction.Disconnect ->
                                        pending <- Map.remove target pending
                                        match! connections.Disconnect target with
                                        | Ok existed -> respondJson res 200 (sprintf """{"disconnected":%b}""" existed)
                                        | Error e -> respondText res 400 e
                                })
                    // Unreachable: this handler only runs for the two cases above.
                    | Some _
                    | None -> respondText res 404 "not found"
                match req.``method`` with
                | "GET" ->
                    handle
                        { Scope = "mine"
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
