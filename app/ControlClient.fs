module Yession.Host.ControlClient

// The Session side of the control RPC (Phase 4, Step 24): the supervision,
// secrets, and connections calls to the Manager's control endpoint, authenticated by
// this launch's secret. Failures are values — a transport error degrades to a failed
// result shape, never an exception, because the Session turns them into
// events.

open Fable.Core
open Yession.Domain
open Yession.Codecs
open Yession.Domain.Sandboxes
open Yession.Domain.Tools
open Yession.Domain.Standings
open Yession.Domain.Access
open Yession.Domain.Hooks
open Yession.Manager
open Yession.Oidc
open Yession.App

/// How a request reaches `endpoint`.
let private transportOf (endpoint: ControlEndpoint) : Http.Transport =
    match endpoint with
    | ControlEndpoint.Address _ -> Http.Transport.Tcp
    | ControlEndpoint.Socket path -> Http.Transport.UnixSocket path

/// One control POST: the per-launch secret is the whole authentication, and the body is
/// always this repository's own JSON. The endpoint decides the transport.
let private post (endpoint: ControlEndpoint) (route: ControlRoute) (secret: string) (body: string) : JS.Promise<Fetch.Types.Response> =
    Http.fetchOver
        (transportOf endpoint)
        (ControlRoute.at (ControlEndpoint.baseUrl endpoint) route)
        { Method = Fetch.Types.HttpMethod.POST
          Headers = [ "x-yession-control", secret; "content-type", "application/json" ]
          Body = Some body
          Signal = None }

/// One control call's body, or a rejection naming the status that refused it.
///
/// A status is a REFUSAL here rather than an answer — every caller below either swallows the
/// failure or turns it into an `Error`, and none of them reads a body the Manager sent with a
/// non-200 — so the promise carries the body and nothing else. The rejection stays a
/// rejection, in the tick the response arrives, because every caller is already written
/// around one.
let private postJson (endpoint: ControlEndpoint) (route: ControlRoute) (secret: string) (body: string) : JS.Promise<string> =
    post endpoint route secret body
    |> Promise.bind (fun response ->
        if response.Ok then response.text ()
        else failwithf "control rpc failed: %d" response.Status)

/// The control-leg case of `Sse.subscribe`: the per-launch secret is the whole authentication.
let private openEventStream (endpoint: ControlEndpoint) (route: ControlRoute) (secret: string) (onData: Sink<string>) : Subscription =
    Sse.subscribeOver
        (transportOf endpoint)
        (ControlRoute.at (ControlEndpoint.baseUrl endpoint) route)
        [ "x-yession-control", secret ]
        onData

/// Turn a typed sink into a frame sink: decode each frame with `codec`, and log-and-skip a
/// malformed one — a bad frame is never fatal to the stream, because the next one may be fine and
/// the stream is the only way the Manager can reach us. `what` names the stream in that log line.
let private decoding (what: string) (codec: Codec<'a>) (sink: Sink<'a>) : Sink<string> =
    fun data ->
        match ControlWire.fromString codec data with
        | Ok value -> sink value
        | Error e -> eprintfn "%s decode failed: %s" what e

/// Subscribe to the Manager's notification stream. Each decoded `SessionNotification` is
/// handed to `onNotification`; a malformed frame is logged and skipped (never fatal to the
/// stream). Returns a cancel that stops the subscription and closes the connection.
let subscribeNotifications (endpoint: ControlEndpoint) (secret: string) (onNotification: Sink<SessionNotification>) : Subscription =
    openEventStream
        endpoint ControlRoute.Notifications
        secret
        (decoding "notification" ControlWire.sessionNotification onNotification)

/// Subscribe to this session's MCP server set (Plan 17). The current set arrives
/// immediately, then a fresh WHOLE set on every change; each is handed to `onSet`. A
/// malformed frame is logged and skipped. Returns a cancel that stops the subscription and
/// closes the connection.
///
/// The secret names the launch, and the Manager resolves the session from it — so this
/// stream carries the servers THIS session may reach, never the Manager's whole
/// configuration.
let subscribeMcp (endpoint: ControlEndpoint) (secret: string) (onSet: Sink<McpServerSet>) : Subscription =
    openEventStream
        endpoint ControlRoute.McpServers
        secret
        (decoding "mcp server set" McpWire.serverSet onSet)

/// Subscribe to the Manager's session registry stream (`/sessions/stream`) — the
/// management surface, not a control leg, so no secret rides the
/// request; `headers` carry whatever the Manager's authentication strategy wants (none
/// under `localhost`, an `x-yession-user` assertion under `trusted-headers`). The
/// current Running set arrives immediately, then a fresh full frame on every change;
/// what an operator's serving binding (and the Ports-tier tests) consume. Returns a
/// cancel that stops the subscription and closes the connection.
let subscribeSessionsAs (headers: (string * string) list) (baseUrl: string) (onFrame: Sink<ControlWire.SessionRegistryFrame>) : Subscription =
    Sse.subscribe
        (ManagerRoute.at baseUrl ManagerRoute.SessionRegistry)
        headers
        (decoding "session registry" ControlWire.sessionRegistryFrame onFrame)

/// `subscribeSessionsAs` with no headers — the `localhost`-strategy case.
let subscribeSessions (baseUrl: string) (onFrame: Sink<ControlWire.SessionRegistryFrame>) : Subscription =
    subscribeSessionsAs [] baseUrl onFrame

/// Report the session's display name (its collaborative title) to the Manager, so the
/// session list reflects it. Best-effort: a transport failure is swallowed — a title that
/// fails to reach the list is cosmetic, never a reason to disturb the session.
let nameReporter (endpoint: ControlEndpoint) (secret: string) : string -> Async<unit> =
    fun name ->
        async {
            try
                let! _ =
                    postJson
                        endpoint ControlRoute.Name
                        secret
                        (ControlWire.toString ControlWire.sessionNameReport name)
                    |> Interop.awaitPromise
                return ()
            with _ -> return ()
        }

/// Report what this session says about itself, so the roster can show it. Best-effort like
/// `nameReporter` and for the same reason: standings that fail to arrive leave the roster
/// showing the last ones, which is stale rather than wrong, and never a reason to disturb
/// the session.
let statusReporter (endpoint: ControlEndpoint) (secret: string) : Standing list -> Async<unit> =
    fun standings ->
        async {
            try
                let! _ =
                    postJson
                        endpoint ControlRoute.Status
                        secret
                        (ControlWire.toString ControlWire.sessionStatusReport standings)
                    |> Interop.awaitPromise
                return ()
            with _ -> return ()
        }

/// The same call, keeping the status beside the body rather than rejecting on it.
let private postJsonReply (endpoint: ControlEndpoint) (route: ControlRoute) (secret: string) (body: string) : JS.Promise<{| status: int; body: string |}> =
    post endpoint route secret body
    |> Promise.bind (fun response ->
        response.text ()
        |> Promise.map (fun said -> {| status = response.Status; body = said |}))

/// One call to a route that answers a body to decode, or the reason it could not: a refusal
/// names the route and the status (policy 403s carry the Manager's reason), and an
/// unreachable Manager is a value like any other failure. Shared by the secrets and
/// connections families, whose calls are this and a codec each.
let private postRoute
    (endpoint: ControlEndpoint)
    (secret: string)
    (route: ControlRoute)
    (body: string)
    (decode: string -> Result<'a, string>)
    : Async<Result<'a, string>> =
    async {
        try
            let! reply = postJsonReply endpoint route secret body |> Interop.awaitPromise
            if reply.status = 200 then return decode reply.body
            else return Error (sprintf "%s refused (%d): %s" (ControlRoute.path route) reply.status reply.body)
        with e ->
            return Error (sprintf "control unreachable: %s" e.Message)
    }

/// Report whether this session is in use (Plan 11).
///
/// Deliberately NOT `nameReporter`'s error handling, and it uses the reply-carrying post
/// for the same reason. A title that fails to arrive is cosmetic; a beat that fails to
/// arrive gets this session STOPPED once the Manager's idle window elapses. That outcome is
/// correct — a session which cannot reach its supervisor is already unsupervised, and the
/// repeat-while-busy cadence means no single delivery has to succeed — but it must never be
/// SILENT, or the reap minutes later has no visible cause.
let activityReporter (endpoint: ControlEndpoint) (secret: string) : bool -> Async<unit> =
    fun busy ->
        async {
            try
                let! reply =
                    postJsonReply
                        endpoint ControlRoute.Activity
                        secret
                        (ControlWire.toString ControlWire.sessionActivityReport busy)
                    |> Interop.awaitPromise
                if reply.status <> 200 then
                    eprintfn
                        "[activity] the manager refused an activity report (HTTP %d: %s). The manager will stop this session when its idle window elapses."
                        reply.status
                        (reply.body.Trim ())
            with e ->
                eprintfn
                    "[activity] could not reach the manager to report activity (%s). The manager will stop this session when its idle window elapses."
                    e.Message
        }

/// The session's secrets capability (Plan 06), pre-bound to ITS OWN session scope —
/// "capabilities are scoped, not ambient": this surface cannot even express another
/// scope. Write/list/delete only; there is no read — a stored secret is USED by
/// referencing its name in an EnvironmentSpec env var (SecretRef), resolved at sandbox
/// spawn straight into the sandbox env (`resolveSecret` below, which is
/// session-internal and never surfaces as an agent tool). Failures (including policy
/// 403s, with the Manager's reason) are values, never exceptions.
type SessionSecretsCapabilities =
    { SetSecret : SecretName -> string -> Async<Result<SecretMetadata, string>>
      ListSecrets : unit -> Async<Result<SecretMetadata list, string>>
      DeleteSecret : SecretName -> Async<Result<bool, string>> }

let secretsCapabilities (endpoint: ControlEndpoint) (secret: string) (sessionId: SessionId) : SessionSecretsCapabilities =
    let scope = SessionScope sessionId
    let post (route: ControlRoute) (body: string) (decode: string -> Result<'a, string>) : Async<Result<'a, string>> =
        postRoute endpoint secret route body decode
    { SetSecret =
        fun name value ->
            post ControlRoute.SetSecret
                (ControlWire.toString ControlWire.setSecretRequest { Scope = scope; Name = name; Value = value })
                (ControlWire.fromString ControlWire.secretMetadata)
      ListSecrets =
        fun () ->
            post ControlRoute.ListSecrets
                (ControlWire.toString ControlWire.listSecretsRequest { Scope = scope })
                (ControlWire.fromString ControlWire.listSecretsResponse >> Result.map (fun r -> r.Secrets))
      DeleteSecret =
        fun name ->
            post ControlRoute.DeleteSecret
                (ControlWire.toString ControlWire.deleteSecretRequest { Scope = scope; Name = name })
                (ControlWire.fromString ControlWire.deleteSecretResponse >> Result.map (fun r -> r.Deleted)) }

/// Resolve one referenced secret value at sandbox spawn — the one value-returning
/// secrets call. Session-internal: the value goes into the sandbox's policy env and is
/// dropped; it never reaches the agent loop (no agent tool wraps this).
let resolveSecret (endpoint: ControlEndpoint) (secret: string) : SecretName -> Async<Result<string, string>> =
    fun name ->
        postRoute
            endpoint
            secret
            ControlRoute.ResolveSecret
            (ControlWire.toString ControlWire.resolveSecretRequest { Name = name })
            (ControlWire.fromString ControlWire.resolveSecretResponse >> Result.map (fun r -> r.Value))

/// The session side of the Manager's connection broker (Plan 08). Service-agnostic like
/// the wire: callers supply targets and provider endpoints as data. `Resolve` is the one
/// value-returning call — an agent turn fetches the credential it runs on. Failures
/// (including policy 403s, with the Manager's reason) are values, never exceptions.
type SessionConnections =
    { Begin : ControlWire.ConnectionBeginRequest -> Async<Result<ControlWire.ConnectionBeginResponse, string>>
      Complete : ConnectionId -> string -> Async<Result<unit, string>>
      Put : ConnectionId -> string -> Async<Result<unit, string>>
      /// Hand over a grant this session obtained itself, so the Manager can refresh it
      /// later. The session never keeps the refresh token: it goes over this leg once.
      PutGrant : ControlWire.ConnectionPutGrantRequest -> Async<Result<unit, string>>
      Disconnect : ConnectionId -> Async<Result<bool, string>>
      /// Tell the Manager the PROVIDER refused this credential. The one fact about a
      /// credential's health that only the side SPENDING it can know — a static token has no
      /// expiry to read, so nothing Manager-side can work this out. Answers whether it was
      /// news, so a verb that retries does not report the same fault three times.
      Reject : ConnectionId -> string -> Async<Result<bool, string>>
      Resolve : ConnectionId -> Async<Result<ConnectionKind * string, string>> }

let connections (endpoint: ControlEndpoint) (secret: string) : SessionConnections =
    let post (route: ControlRoute) (body: string) (decode: string -> Result<'a, string>) : Async<Result<'a, string>> =
        postRoute endpoint secret route body decode
    { Begin =
        fun request ->
            post ControlRoute.BeginConnection
                (ControlWire.toString ControlWire.connectionBeginRequest request)
                (ControlWire.fromString ControlWire.connectionBeginResponse)
      Complete =
        fun target code ->
            post ControlRoute.CompleteConnection
                (ControlWire.toString ControlWire.connectionCompleteRequest { Target = target; Code = code })
                (fun _ -> Ok ())
      Put =
        fun target value ->
            post ControlRoute.PutConnection
                (ControlWire.toString ControlWire.connectionPutRequest { Target = target; Value = value })
                (fun _ -> Ok ())
      PutGrant =
        fun request ->
            post ControlRoute.PutConnectionGrant
                (ControlWire.toString ControlWire.connectionPutGrantRequest request)
                (fun _ -> Ok ())
      Disconnect =
        fun target ->
            post ControlRoute.DisconnectConnection
                (ControlWire.toString ControlWire.connectionDisconnectRequest { Target = target })
                (ControlWire.fromString ControlWire.connectionDisconnectResponse >> Result.map (fun r -> r.Disconnected))
      Reject =
        fun target reason ->
            post ControlRoute.RejectConnection
                (ControlWire.toString ControlWire.connectionRejectRequest { Target = target; Reason = reason })
                (ControlWire.fromString ControlWire.connectionRejectResponse >> Result.map (fun r -> r.Recorded))
      Resolve =
        fun target ->
            post ControlRoute.ResolveConnection
                (ControlWire.toString ControlWire.connectionResolveRequest { Target = target })
                (ControlWire.fromString ControlWire.connectionResolveResponse >> Result.map (fun r -> r.Kind, r.Value)) }

/// The session side of records: values this session's code keeps for a person across their
/// sessions, which the Manager stores and never reads. The code that owns a record's schema
/// encodes and decodes it; this carries text. Failures (including policy 403s, with the
/// Manager's reason) are values, never exceptions.
[<RequireQualifiedAccess>]
type SessionRecords =
    { Read : RecordId -> Async<Result<StoredRecord option, string>>
      /// Replace a record at the revision the caller read (`None`: it found none). `Stale`
      /// means another write landed first: read again, merge, write again.
      Write : RecordId -> int option -> string -> Async<Result<RecordWrite, string>> }

let records (endpoint: ControlEndpoint) (secret: string) : SessionRecords =
    { SessionRecords.Read =
        fun target ->
            postRoute
                endpoint
                secret
                ControlRoute.ReadRecord
                (ControlWire.toString ControlWire.readRecordRequest { ControlWire.ReadRecordRequest.Target = target })
                (ControlWire.fromString ControlWire.readRecordResponse >> Result.map (fun r -> r.Record))
      SessionRecords.Write =
        fun target expected value ->
            postRoute
                endpoint
                secret
                ControlRoute.WriteRecord
                (ControlWire.toString
                    ControlWire.writeRecordRequest
                    { ControlWire.WriteRecordRequest.Target = target
                      ControlWire.WriteRecordRequest.Expected = expected
                      ControlWire.WriteRecordRequest.Value = value })
                (ControlWire.fromString ControlWire.recordWrite) }

/// Subscribe to the Manager's connection-status stream (the third reverse leg). The
/// caller's current readable statuses arrive immediately, then a fresh list on every
/// change; metadata only, never values. A malformed frame is logged and skipped.
/// Returns a cancel that stops the subscription and closes the connection.
let subscribeConnections (endpoint: ControlEndpoint) (secret: string) (onList: Sink<ConnectionStatusList>) : Subscription =
    openEventStream
        endpoint ControlRoute.ConnectionStatuses
        secret
        (decoding "connection status" ControlWire.connectionStatusList onList)

/// Declare what this session wants forwarded from the Manager's hook endpoints, and stop.
///
/// The filter is DATA — the Manager stores it and compares, never interprets it — so what
/// this session knows about its provider stays in this session. Answers the subscription
/// id, which is what a delivery names when it arrives on the notification leg.
let subscribeHook (endpoint: ControlEndpoint) (secret: string) (filter: DeliveryFilter) : Async<Result<string, string>> =
    async {
        try
            let! text =
                postJson
                    endpoint ControlRoute.SubscribeHook
                    secret
                    (ControlWire.toString ControlWire.subscribeHookRequest { Filter = filter })
                |> Interop.awaitPromise
            return ControlWire.fromString ControlWire.subscribeHookResponse text |> Result.map (fun r -> r.Id)
        with e ->
            return Error (sprintf "control unreachable: %s" e.Message)
    }

/// Stop one of this session's subscriptions. `false` when the Manager had no such
/// subscription for this launch — which a restart makes ordinary, since a launch's
/// subscriptions die with it.
let unsubscribeHook (endpoint: ControlEndpoint) (secret: string) (id: string) : Async<Result<bool, string>> =
    async {
        try
            let! text =
                postJson
                    endpoint ControlRoute.UnsubscribeHook
                    secret
                    (ControlWire.toString ControlWire.unsubscribeHookRequest { ControlWire.UnsubscribeHookRequest.Id = id })
                |> Interop.awaitPromise
            return ControlWire.fromString ControlWire.unsubscribeHookResponse text |> Result.map (fun r -> r.Dropped)
        with e ->
            return Error (sprintf "control unreachable: %s" e.Message)
    }

/// Dynamic client registration: bind this launch's OAuth client to its control secret.
/// Called after the session's server listens — the redirect URI needs the OS-assigned
/// port. NOT best-effort: a session that cannot register cannot authorize users, so the
/// failure is returned for the caller to treat as fatal.
let registerClient (endpoint: ControlEndpoint) (secret: string) (redirectUri: string) : Async<Result<RegisterClientResponse, string>> =
    async {
        try
            let! text =
                postJson
                    endpoint ControlRoute.RegisterClient
                    secret
                    (Wire.toString Wire.registerClientRequest { RedirectUri = redirectUri })
                |> Interop.awaitPromise
            return Wire.fromString Wire.registerClientResponse text
        with e ->
            return Error (sprintf "control unreachable: %s" e.Message)
    }

