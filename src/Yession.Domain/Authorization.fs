namespace Yession.Domain.Access

open Yession.Domain

/// The ABAC vocabulary (Plan 06): authorization decisions as one pure, total,
/// default-deny function over attributes of the subject, the action, and the resource.
/// Subjects are built ONLY by the Manager from state it verified itself (the per-launch
/// control secret, and the users it bound at ID-token issuance) — never from request
/// content, so the composite session+user identity is never self-asserted. Actions and
/// resources are unions: the next Manager-owned resource adds cases, not mechanisms.

/// Manager-verified caller attributes.
type Subject =
    { /// The launch making the call. None for future non-session callers (e.g. a
      /// logged-in management UI acting directly for a user).
      Session : SessionId option
      /// Users the Manager bound to that launch at ID-token issuance.
      Users : Set<UserId>
      /// Did the Manager grant this launch UNATTRIBUTED access — an ID token whose
      /// strategy named no user behind the subject (`--auth localhost`)? Like Users:
      /// recorded by the Manager at issuance, never from request content, and gone
      /// when the launch is. False under every attributed strategy, which is what keeps
      /// `LocalScope` unreadable there.
      Local : bool }

type SecretAction =
    /// Metadata only — names, scopes, timestamps. Never values.
    | ListSecrets
    | SetSecret
    | DeleteSecret
    /// Resolve a value into a launched environment's env vars, at the session's own
    /// sandbox spawn. Permitted on the session's own scope only. This is the ONLY generic
    /// read the policy knows.
    | InjectSecret

/// What a generic secret action acts on. Generic secrets only: a connection credential is
/// a `ConnectionId`, and no case here carries one.
type Resource =
    | SecretResource of SecretId
    | SecretCollection of SecretScope

/// Actions on connection credentials (Plan 08) — the Manager-brokered, owner- or
/// session-scoped external-service credentials. A separate family from `SecretAction`
/// because its rules differ on purpose: sessions may WRITE owner-scoped connection
/// credentials for identities bound to them (the sign-in flow is exactly that write),
/// and `ResolveCredential` RETURNS a value to the session (an agent turn needs the
/// token in-process, unlike container env injection). Generic secrets stay write-only
/// and list-only on user scope; nothing here widens their rules.
type ConnectionAction =
    /// Begin/complete a brokered flow or store a pasted token — the narrow write.
    | ConnectCredential
    /// Metadata only — kind and timestamps, never values.
    | ReadConnectionStatus
    /// Release the credential's current value to the caller for one agent turn.
    | ResolveCredential
    | DisconnectCredential

/// Actions on records: values session code keeps for a person across their sessions, which
/// the Manager stores and never reads (`RecordId`). Read and write, and nothing that lists
/// or removes: a session reaches a record by the name its own code chose.
type RecordAction =
    | ReadRecord
    /// Replace a record, at the revision the caller read (`RecordWrite`).
    | WriteRecord

/// What a request asks to do, to what. Each family's actions pair with that family's own
/// identity, so the type cannot express a generic action on a connection credential (an
/// `InjectSecret` that names a refresh token, a `SetSecret` that overwrites one) or a
/// connection action on a generic secret. No policy row has to refuse those: they are not
/// requests.
type Access =
    | SecretAccess of SecretAction * Resource
    | ConnectionAccess of ConnectionAction * ConnectionId
    | RecordAccess of RecordAction * RecordId

type Request =
    { Subject : Subject
      Access : Access }

type Decision =
    | Permit
    /// Operator-facing reason (403 body, Manager log). Never echoes values.
    | Deny of reason: string

module Policy =

    /// The v1 policy. A session owns its session-scoped secrets outright; user-scoped
    /// secrets are listable by a session a bound user signed into, and never writable or
    /// injectable by sessions. A `SecretRef` comes from a repository's file, not from the
    /// user, so injecting a user's secret needs a consent surface that does not exist yet
    /// (docs/GAPS.md); until then the default deny covers it. Local-scoped CONNECTION credentials belong to a launch the Manager granted
    /// unattributed access; generic secrets have no local rule at all, so they deny.
    /// Anything not explicitly permitted is denied.
    let authorize (request: Request) : Decision =
        let ownSession (owner: SessionId) =
            match request.Subject.Session with
            | Some caller when caller = owner -> Permit
            | _ -> Deny "not the owning session"
        let boundUser (user: UserId) =
            if Set.contains user request.Subject.Users then Permit
            else Deny "user is not signed in to this session"
        let localAccess () =
            if request.Subject.Local then Permit
            else Deny "this deployment attributes its users. It has no local credential"
        match request.Access with
        // Connection credentials (Plan 08): every action — including the write — is
        // permitted exactly where the caller IS the scope's owner: its own session
        // scope, a user the Manager bound to it. That makes an
        // owner-scoped sign-in usable (and replaceable) from any session that owner is
        // signed into, and a session-scoped one from only that session.
        | ConnectionAccess (_, { ConnectionId.Scope = SessionScope owner }) ->
            ownSession owner
        | ConnectionAccess (_, { ConnectionId.Scope = UserScope user }) ->
            boundUser user
        // The unattributed deployment's own credential. Deliberately CONNECTION-only:
        // `LocalScope` exists so a shared-access deployment can name the one principal it
        // has, not as a Manager-wide secret drawer — generic secret actions on it fall
        // through to the default deny below.
        | ConnectionAccess (_, { ConnectionId.Scope = LocalScope }) ->
            localAccess ()
        // Records: a person's are reachable from exactly where their connection credentials
        // are, a session they signed in to, and the deployment's from a launch the Manager
        // granted unattributed access. The Manager sees the launch and who signed in to it,
        // never which of them is acting, so what a session does with a record for whom is
        // the session's own rule.
        | RecordAccess (_, { RecordId.Owner = UserOwner user }) ->
            boundUser user
        | RecordAccess (_, { RecordId.Owner = LocalOwner }) ->
            localAccess ()
        | SecretAccess ((SetSecret | DeleteSecret | InjectSecret), SecretResource { SecretId.Scope = SessionScope owner }) ->
            ownSession owner
        | SecretAccess (ListSecrets, SecretCollection (SessionScope owner)) ->
            ownSession owner
        | SecretAccess (ListSecrets, SecretCollection (UserScope user)) ->
            boundUser user
        | SecretAccess ((SetSecret | DeleteSecret), SecretResource { SecretId.Scope = UserScope _ }) ->
            Deny "user-scoped secrets are managed by the user, not sessions"
        | _ ->
            Deny "no rule permits this request"
