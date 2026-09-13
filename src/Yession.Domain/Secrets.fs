namespace Yession.Domain.Access

open Yession.Domain
open Yession.Domain.Sandboxes
open System

/// Secrets vocabulary (Plan 06). A secret's identity is scope + name; its value never
/// appears in this module — `SecretMetadata` is what listings serve, and by construction
/// it cannot carry a value. Still no AMBIENT scope: every scope here names an owner the
/// Manager can authorize a caller against (docs/GAPS.md).

/// Who a secret belongs to. `UserId` lives in Identity.fs — the same identity events
/// attribute to (`ActorRef`), not a secrets-only concept.
///
/// There is deliberately no PEER scope. One existed (Plan 07): a secret owned by the
/// browser's peer id, readable by a launch the Manager had witnessed that peer into. A
/// peer id lives in origin-partitioned localStorage, so it changed under the person holding
/// it and stranded the credential behind every new one; ownership moved to the cookie's
/// user, and to `LocalScope` where no user exists. The case, the witnessing that served it,
/// and the `peer_id` that rode the sign-in bounce for it are gone together.
type SecretScope =
    | SessionScope of SessionId
    | UserScope of UserId
    /// Unattributed access to this deployment, as one principal — what `--auth localhost`
    /// grants and the only owner it can honestly name (every loopback caller is the same
    /// subject `local`, and no user exists to attribute to).
    ///
    /// It is NOT the ambient/global scope this module refuses. An ambient secret would
    /// have no owner to authorize against; this one does — the Manager permits it only to
    /// a launch it granted unattributed access to, recorded at ID-token issuance exactly
    /// like a bound user, and revoked with the launch.
    ///
    /// Its reach IS the whole deployment, and that is the honest reading of the trust rule
    /// behind it: anyone who can reach a `localhost` Manager can already manage every
    /// session on it. Under an attributed strategy no launch is ever granted this, so
    /// nothing can read it. See `docs/deployment.md` on what `--auth localhost` costs.
    | LocalScope

module SecretScope =
    /// A stable one-line rendering for logs and cipher AAD
    /// ("session:<id>" / "user:<sub>" / "local").
    let describe (scope: SecretScope) : string =
        match scope with
        | SessionScope sessionId -> "session:" + SessionId.value sessionId
        | UserScope user -> "user:" + UserId.value user
        | LocalScope -> "local"

/// A secret's identity: which scope owns it, and its name within that scope.
[<RequireQualifiedAccess>]
type SecretId = { Scope : SecretScope; Name : SecretName }

/// Everything a listing may reveal. There is no value field — a listing cannot leak a
/// value because the type cannot carry one.
type SecretMetadata =
    { Id : SecretId
      CreatedAt : DateTimeOffset
      UpdatedAt : DateTimeOffset }
