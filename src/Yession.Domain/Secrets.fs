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

/// A GENERIC secret's identity: which scope owns it, and its name within that scope. A
/// session writes these (`set_secret`), and a `SecretRef` reads them. A connection
/// credential is never one: it is a `ConnectionId`, a different type, so no generic action
/// (set, delete, list, inject) can name it, and no connection action can name a secret.
[<RequireQualifiedAccess>]
type SecretId = { Scope : SecretScope; Name : SecretName }

/// A connection credential's identity (Plan 08): the scope that owns it, and the storage
/// name the session chose for it. The same two parts as `SecretId`, and deliberately not the
/// same type: the store keeps the two families apart (`SecretStore`), and the policy pairs
/// each family's actions with its own identity (`Access`). A generic secret and a connection
/// may share a scope and a name and still never meet.
[<RequireQualifiedAccess>]
type ConnectionId = { Scope : SecretScope; Name : SecretName }

/// Everything a listing may reveal. There is no value field — a listing cannot leak a
/// value because the type cannot carry one.
[<RequireQualifiedAccess>]
type SecretMetadata =
    { Id : SecretId
      CreatedAt : DateTimeOffset
      UpdatedAt : DateTimeOffset }

/// A person's standing permission for the Manager to bind ONE of their connections into a
/// session while they are not signed in to it — after a relaunch nobody is bound, and a
/// poller that runs on their GitHub would otherwise go anonymous. Stored WITH the credential,
/// so it lives exactly as long as the credential does: a disconnect drops it, and a refresh
/// or a re-paste keeps it.
///
/// Meaningful on a `UserScope` connection only. A session-scoped credential is already its
/// session's alone, and the deployment's is reachable wherever unattributed access is: there
/// is nobody absent to stand in for.
[<RequireQualifiedAccess>]
type OfflineGrant =
    /// Every session the owner has signed in to at least once (`SessionRecord.Members`). A
    /// session they never joined is never one of "theirs".
    | AllMySessions
    /// These sessions, named one by one from inside each.
    | Sessions of Set<SessionId>

/// How a connection's offline grant reads from ONE session, and what a person in that session
/// chooses it to be: off here, here, or everywhere they have been.
[<RequireQualifiedAccess>]
type OfflineChoice =
    | Off
    | ThisSession
    | AllMySessions

module OfflineGrant =

    let private ownerOf (id: ConnectionId) : UserId option =
        match id.Scope with
        | UserScope user -> Some user
        | SessionScope _ | LocalScope -> None

    /// Does this grant cover the session? `members` are the users who have signed in to it at
    /// least once. A grant on a connection that is not a person's covers nothing.
    let covers (sessionId: SessionId) (members: Set<UserId>) (id: ConnectionId) (grant: OfflineGrant option) : bool =
        match ownerOf id, grant with
        | Some owner, Some OfflineGrant.AllMySessions -> Set.contains owner members
        | Some _, Some (OfflineGrant.Sessions sessions) -> Set.contains sessionId sessions
        | Some _, None
        | None, _ -> false

    /// The connections a launch of `sessionId` may resolve on an owner's standing grant rather
    /// than on their sign-in. Computed by the Manager from what it stores — the session's
    /// members and each connection's grant — and never from what a request says. A user who is
    /// signed in needs nothing from here: their whole scope is readable already.
    let grantedHere
        (sessionId: SessionId)
        (members: Set<UserId>)
        (connections: (ConnectionId * OfflineGrant option) list)
        : Set<ConnectionId> =
        connections
        |> List.filter (fun (id, grant) -> covers sessionId members id grant)
        |> List.map fst
        |> Set.ofList

    /// How the grant reads from one session.
    let here (sessionId: SessionId) (members: Set<UserId>) (id: ConnectionId) (grant: OfflineGrant option) : OfflineChoice =
        match grant with
        | Some OfflineGrant.AllMySessions when covers sessionId members id grant -> OfflineChoice.AllMySessions
        | Some (OfflineGrant.Sessions _) when covers sessionId members id grant -> OfflineChoice.ThisSession
        | _ -> OfflineChoice.Off

    /// The grant after a person in `sessionId` chooses. "This session" ADDS the session to the
    /// ones named so far, and narrows "all my sessions" to it; "off" takes this session out and
    /// switches "all my sessions" off entirely, because a session cannot be left out of "all".
    /// A grant that names no session is no grant.
    let choose (sessionId: SessionId) (choice: OfflineChoice) (current: OfflineGrant option) : OfflineGrant option =
        match choice, current with
        | OfflineChoice.AllMySessions, _ -> Some OfflineGrant.AllMySessions
        | OfflineChoice.ThisSession, Some (OfflineGrant.Sessions sessions) ->
            Some (OfflineGrant.Sessions (Set.add sessionId sessions))
        | OfflineChoice.ThisSession, (None | Some OfflineGrant.AllMySessions) ->
            Some (OfflineGrant.Sessions (Set.singleton sessionId))
        | OfflineChoice.Off, Some (OfflineGrant.Sessions sessions) ->
            let rest = Set.remove sessionId sessions
            if Set.isEmpty rest then None else Some (OfflineGrant.Sessions rest)
        | OfflineChoice.Off, (None | Some OfflineGrant.AllMySessions) -> None

/// A stored connection credential as the store lists it. No value field, as above.
[<RequireQualifiedAccess>]
type ConnectionMetadata =
    { Id : ConnectionId
      CreatedAt : DateTimeOffset
      UpdatedAt : DateTimeOffset
      /// The owner's standing grant to bind this connection where they are not signed in.
      Offline : OfflineGrant option }
