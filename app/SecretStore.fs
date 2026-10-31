module Yession.Host.SecretStore

// The Manager's secret store (Plan 06): the pure `SecretsFile` envelope behind the
// `SecretsCodec`, values as per-entry AES-GCM ciphertext under a KEK the OS credential
// manager holds (KeyStore). One code path serves both modes: `Some path` = durable
// (encrypted file in the Manager's data dir, ManagerStore-style atomic writes);
// `None` = ephemeral (no credential manager on this host — no file I/O at all, a
// per-boot random KEK, everything dies with the Manager). `Resolve` feeds environment
// injection, and with session-owned sandboxes that injection happens at the SESSION'S
// sandbox spawn: a value crosses only the authenticated loopback control channel
// (`/control/secrets/resolve`), only at sandbox spawn, and never reaches the agent loop.
// That route reads the calling session's own scope and nothing else (`SecretResolution`
// below). The Manager's process environment is not a secret store: no reference reads it.
//
// The store holds two families, one partition each (`Partition`): generic secrets keyed by
// `SecretId`, and connection credentials keyed by `ConnectionId`. The broker holds the
// connection partition alone, and the secrets API and the `SecretRef` resolver the generic
// one alone, so neither can name the other's entries. Each family seals under its own AAD.
// A version 1 file predates the split, and `migrateV1` sorts it at open
// (docs/decisions/2026-10-26-secrets-and-connections-are-two-families.md).
//
// A third family, records, keeps what session code wants for a person across their sessions:
// a value keyed by `RecordId` that the Manager seals and never reads. It lives in a file of its
// own under the same key, so a rollback to a build without records leaves them as they are
// (docs/decisions/2026-11-01-records-are-a-third-family-of-the-store.md).

open System
open Yession.Domain
open Yession.Domain.Sandboxes
open Yession.Domain.Access
open Yession.Manager

/// One family of the store: generic secrets keyed by `SecretId`, or connection credentials
/// keyed by `ConnectionId`. The two partitions share a file and a key, and nothing else: a
/// key of one family cannot name an entry of the other.
[<RequireQualifiedAccess>]
type Partition<'Id, 'Metadata> =
    { List : SecretScope -> 'Metadata list
      Set : 'Id -> string -> Async<Result<'Metadata, string>>
      /// false = did not exist.
      Delete : 'Id -> Async<Result<bool, string>>
      /// Decrypt-on-demand. Never exposed over the control channel as itself: generic
      /// values feed env injection (`SecretResolution`), connection values the broker.
      Resolve : 'Id -> Async<Result<string option, string>> }

/// The third family (`RecordId`): values session code keeps for a person across their
/// sessions, sealed here and never read. Read, and a write at the revision its caller read
/// (`RecordWrite`). Nothing lists or removes one: a session reaches a record by the name its
/// own code chose.
[<RequireQualifiedAccess>]
type RecordStore =
    { Read : RecordId -> Async<Result<StoredRecord option, string>>
      Write : RecordId -> int option -> string -> Async<Result<RecordWrite, string>> }

/// How much one owner may keep. Any session the owner signed in to can write their records,
/// and the file is rewritten whole at every write, so both are bounded here, where the
/// records are, rather than trusted to every session that writes one.
module RecordLimits =
    /// Characters in one record's value.
    let maxValueLength = 8192
    /// Records one owner may hold.
    let maxRecordsPerOwner = 512

/// Where a durable store keeps its files: the secrets envelope (generic secrets and connection
/// credentials) and the records envelope. Two files, so a Manager rolled back to a build
/// without records leaves the records as they are instead of refusing every secret.
[<RequireQualifiedAccess>]
type StoreFiles = { Secrets : string; Records : string }

module StoreFiles =
    /// The files a Manager keeps in its data directory.
    let inDirectory (dataDir: string) : StoreFiles =
        { Secrets = sprintf "%s/secrets.json" dataDir
          Records = sprintf "%s/records.json" dataDir }

type SecretStore =
    { /// What sessions write (`set_secret`) and a `SecretRef` reads.
      Secrets : Partition<SecretId, SecretMetadata>
      /// What the connection broker writes and resolves. Nothing else holds this partition.
      Connections : Partition<ConnectionId, ConnectionMetadata>
      /// What the records routes read and write. Nothing else holds this partition.
      Records : RecordStore
      /// Why the records file could not be opened, when it could not: then every records call
      /// answers this, and the file is left as it is. The credential families are unaffected.
      RecordsRefused : string option
      /// True when backed by the encrypted file; false when in-memory only.
      Durable : bool
      /// True when the KEK was minted at this open (first run); false when loaded.
      KekMinted : bool
      /// Entries in the envelope at open, both families (0 for a fresh or ephemeral store).
      EntriesAtOpen : int
      /// Records at open (0 for a fresh, ephemeral or refused records file).
      RecordsAtOpen : int }

/// Why an open failed — structural, so the boot path can audit the KIND without
/// string-sniffing. `describe` renders exactly the wording the failures always had.
type OpenError =
    | KeyStoreFailure of detail: string
    | CorruptStore of path: string * detail: string
    | SealedByDifferentKey of path: string * fileKekId: string * hostKekId: string
    /// A file a newer build wrote. Read as the newest version this build knows, it would
    /// lose what it does not recognise at the first write (every connection credential,
    /// for a build that predates the split), so it is refused.
    | WrittenByNewerBuild of path: string * version: int

module OpenError =
    let describe (error: OpenError) : string =
        match error with
        | KeyStoreFailure detail -> detail
        | CorruptStore (path, detail) -> sprintf "corrupt secrets store %s: %s" path detail
        | SealedByDifferentKey (path, fileKekId, hostKekId) ->
            sprintf "secrets store %s was sealed by a different key (%s, this host's is %s). Restore that key, or delete the file." path fileKekId hostKekId
        | WrittenByNewerBuild (path, version) ->
            sprintf "secrets store %s: %s. Run that build, or restore the copy of the file taken before it." path (SecretsCodec.newerBuild version)

    let kind (error: OpenError) : string =
        match error with
        | KeyStoreFailure _ -> "keystore"
        | CorruptStore _ -> "corrupt"
        | SealedByDifferentKey _ -> "sealed"
        | WrittenByNewerBuild _ -> "newer"

let private now () : DateTimeOffset = DateTimeOffset.UtcNow

/// Sort a version 1 file, written before the store split its families, into the two lists.
///
/// A version 1 entry is a connection credential exactly when its value is a broker envelope:
/// that is what the broker could read back, and so what it treated as one. Everything else is
/// a generic secret, and keeps its ciphertext, because the generic AAD is the one every
/// version 1 entry was sealed under. Each connection credential is opened under that AAD and
/// sealed again under the connection AAD, so every entry in the result opens under its own
/// family's AAD alone. An entry that does not open stays generic and untouched: it was
/// unreadable before, and a later read still says so.
///
/// Sorted by value and not by name on purpose. A name check would be a list of the names
/// connections happen to use today; the envelope is what made an entry a connection.
let private migrateV1 (cipher: SecretsCipher.Cipher) (file: SecretsFile) : Async<SecretsFile> =
    async {
        let mutable generic = []
        let mutable connections = []
        for entry in file.Entries do
            let! opened = cipher.Decrypt (SecretsCipher.aadFor entry.Scope entry.Name) entry.Iv entry.Ciphertext
            match opened with
            | Ok value when Result.isOk (BrokeredCredentialCodec.fromString value) ->
                let id : ConnectionId = { Scope = entry.Scope; Name = entry.Name }
                let! iv, ciphertext = cipher.Encrypt (SecretsCipher.connectionAadFor id) value
                connections <- connections @ [ { entry with Iv = iv; Ciphertext = ciphertext } ]
            | _ -> generic <- generic @ [ entry ]
        return
            { file with
                Version = SecretsFile.currentVersion
                Entries = generic
                Connections = connections }
    }

/// How a WorkSandbox's `SecretRef` env vars resolve to values, scoped to the session
/// whose sandbox is spawning. The ONLY consumer of `SecretStore.Resolve`; served to
/// sessions by the `/control/secrets/resolve` route.
type ResolveSecret = SessionId -> SecretName -> Async<Result<string, string>>

/// Open the store. Durable when `files` is set: KEK through the KeyStore
/// (created on first run, durable in the credential manager BEFORE first use), file
/// loaded through the codec (missing = empty; corrupt = loud Error; KekId mismatch =
/// distinct loud Error). Ephemeral when `None`: same semantics, zero file I/O.
///
/// The records file is read beside the secrets file, and what it costs to be unreadable is
/// the records alone: they answer every call with why (`RecordsRefused`), the file is never
/// written over, and the credentials open as they would without it.
///
/// A KEK is minted ONLY when there is no durable file. A key store that answers "no
/// key" while a sealed file exists is a failure, never a first run: a locked macOS
/// keychain reads exactly like an empty one, and minting there would overwrite the real
/// key the moment the keychain unlocked, leaving every entry in the file unreadable.
let openStore (files: StoreFiles option) (keyStore: KeyStore.KeyStore) : Async<Result<SecretStore, OpenError>> =
    async {
        // The durable files are read first: whether a missing key may be minted depends on them.
        let existing : Result<(string * SecretsFile) option, OpenError> =
            match files with
            | Some files when Fs.exists files.Secrets ->
                let path = files.Secrets
                let json = Fs.readText path
                match SecretsCodec.newerVersionOf json with
                | Some version -> Error (WrittenByNewerBuild (path, version))
                | None ->
                    match SecretsCodec.fromString json with
                    | Error e -> Error (CorruptStore (path, e))
                    | Ok file -> Ok (Some (path, file))
            | _ -> Ok None
        let existingRecords : Result<(string * RecordsFile) option, string> =
            match files with
            | Some files when Fs.exists files.Records ->
                let path = files.Records
                let json = Fs.readText path
                match RecordsCodec.newerVersionOf json with
                | Some version -> Error (sprintf "records store %s: %s" path (RecordsCodec.newerBuild version))
                | None ->
                    match RecordsCodec.fromString json with
                    | Error e -> Error (sprintf "corrupt records store %s: %s" path e)
                    | Ok file -> Ok (Some (path, file))
            | _ -> Ok None
        match existing with
        | Error e -> return Error e
        | Ok existing ->

        // Resolve (or mint) the KEK — the only moment the raw key bytes are in scope. Either
        // file sealed under a key means there IS a key: a record can be written before any
        // secret, so the secrets file alone cannot answer.
        let sealedOnDisk : (string * string) option =
            match existing, existingRecords with
            | Some (path, file), _ -> Some (path, file.KekId)
            | None, Ok (Some (path, file)) -> Some (path, file.KekId)
            | None, _ -> None
        let! stored = keyStore.Get ()
        let keyStoreFailure detail = KeyStoreFailure (sprintf "secrets key store (%s): %s" keyStore.Name detail)
        let resolved =
            match stored, sealedOnDisk with
            | Error e, _ -> Error (keyStoreFailure e)
            | Ok (Some raw), _ -> KeyStore.splitPayload raw |> Result.map (fun p -> p, false) |> Result.mapError keyStoreFailure
            | Ok None, Some (path, kekId) ->
                Error (
                    keyStoreFailure (
                        sprintf
                            "the credential manager has no key, but %s is sealed under key %s. This Manager does not mint a new key over it. A locked keychain reads as empty. Unlock it (on macOS, log in to this Mac)."
                            path
                            kekId
                    )
                )
            | Ok None, None -> Ok ((Interop.randomSecret (), SecretsCipher.generateKek ()), true)
        match resolved with
        | Error e -> return Error e
        | Ok ((kekId, kekB64u), minted) ->
            let persist (result: Result<unit, string>) =
                async {
                    if minted then
                        // Durable in the credential manager BEFORE any entry is sealed
                        // under it — a crash between the two loses nothing.
                        let! set = keyStore.Set (KeyStore.payload kekId kekB64u)
                        return set |> Result.bind (fun () -> result)
                    else
                        return result
                }
            let! persisted = persist (Ok ())
            match persisted with
            | Error e -> return Error (keyStoreFailure (sprintf "could not persist the key: %s" e))
            | Ok () ->

            let! cipher = SecretsCipher.importKey kekB64u

            // The envelope: the file read above, or a fresh one.
            let initial : Result<SecretsFile, OpenError> =
                match existing with
                | None -> Ok (SecretsFile.empty kekId)
                | Some (path, file) when file.KekId <> kekId -> Error (SealedByDifferentKey (path, file.KekId, kekId))
                | Some (_, file) -> Ok file

            match initial with
            | Error e -> return Error e
            | Ok loaded ->
                let! loaded =
                    if loaded.Version = 1 then migrateV1 cipher loaded else async { return loaded }
                // The single mutable envelope. Mutations are read-modify-write-save on
                // the Node loop AFTER their (async) encryption completes, so
                // interleaved Sets compose instead of losing entries.
                let mutable file = loaded
                let save () =
                    files |> Option.iter (fun files -> Fs.writeTextAtomic files.Secrets (SecretsCodec.toString file))
                // A sorted version 1 file is written back at once, so the file on disk opens
                // under the rules this build reads by, and is sorted only once.
                match existing with
                | Some (_, onDisk) when onDisk.Version = 1 -> save ()
                | _ -> ()

                let sealedEntry (scope: SecretScope) (name: SecretName) (aad: string) (value: string) =
                    async {
                        let! iv, ciphertext = cipher.Encrypt aad value
                        let timestamp = now ()
                        let entry : SecretEntry =
                            { Scope = scope
                              Name = name
                              CreatedAt = timestamp
                              UpdatedAt = timestamp
                              Iv = iv
                              Ciphertext = ciphertext }
                        return entry
                    }
                let opened (aad: string) (entry: SecretEntry option) =
                    async {
                        match entry with
                        | None -> return Ok None
                        | Some entry ->
                            let! decrypted = cipher.Decrypt aad entry.Iv entry.Ciphertext
                            return decrypted |> Result.map Some
                    }

                let secrets : Partition<SecretId, SecretMetadata> =
                    { Partition.List = fun scope -> SecretsFile.list scope file
                      Set =
                        fun id value ->
                            async {
                                let! entry = sealedEntry id.Scope id.Name (SecretsCipher.aadFor id.Scope id.Name) value
                                file <- SecretsFile.upsert entry file
                                save ()
                                match SecretsFile.list id.Scope file |> List.tryFind (fun m -> m.Id = id) with
                                | Some metadata -> return Ok metadata
                                | None -> return Error "secret vanished during set"
                            }
                      Delete =
                        fun id ->
                            async {
                                let existed = SecretsFile.tryFind id file |> Option.isSome
                                if existed then
                                    file <- SecretsFile.remove id file
                                    save ()
                                return Ok existed
                            }
                      Resolve = fun id -> opened (SecretsCipher.aadFor id.Scope id.Name) (SecretsFile.tryFind id file) }

                let connections : Partition<ConnectionId, ConnectionMetadata> =
                    { Partition.List = fun scope -> SecretsFile.listConnections scope file
                      Set =
                        fun id value ->
                            async {
                                let! entry = sealedEntry id.Scope id.Name (SecretsCipher.connectionAadFor id) value
                                file <- SecretsFile.upsertConnection entry file
                                save ()
                                match SecretsFile.listConnections id.Scope file |> List.tryFind (fun m -> m.Id = id) with
                                | Some metadata -> return Ok metadata
                                | None -> return Error "connection credential vanished during set"
                            }
                      Delete =
                        fun id ->
                            async {
                                let existed = SecretsFile.tryFindConnection id file |> Option.isSome
                                if existed then
                                    file <- SecretsFile.removeConnection id file
                                    save ()
                                return Ok existed
                            }
                      Resolve = fun id -> opened (SecretsCipher.connectionAadFor id) (SecretsFile.tryFindConnection id file) }

                // The records envelope: the file read above, a fresh one, or the reason it
                // cannot be had. A refused file is never written: it may be a newer build's,
                // and the build after this one may want it back.
                let recordsLoaded : Result<RecordsFile, string> =
                    match existingRecords with
                    | Error reason -> Error reason
                    | Ok None -> Ok (RecordsFile.empty kekId)
                    | Ok (Some (path, onDisk)) when onDisk.KekId <> kekId ->
                        Error (sprintf "records store %s was sealed by a different key (%s, this host's is %s)" path onDisk.KekId kekId)
                    | Ok (Some (_, onDisk)) -> Ok onDisk

                let records : RecordStore =
                    match recordsLoaded with
                    | Error reason ->
                        { RecordStore.Read = fun _ -> async { return Error reason }
                          RecordStore.Write = fun _ _ _ -> async { return Error reason } }
                    | Ok loadedRecords ->
                        // Its own envelope and its own save, beside the credentials' and in the
                        // same discipline: each write is decided on the loop AFTER its
                        // encryption completes, so no other write lands between the revision
                        // check and the commit.
                        let mutable recordsFile = loadedRecords
                        let saveRecords () =
                            files |> Option.iter (fun files -> Fs.writeTextAtomic files.Records (RecordsCodec.toString recordsFile))
                        { RecordStore.Read =
                            fun id ->
                                async {
                                    match RecordsFile.tryFind id recordsFile with
                                    | None -> return Ok None
                                    | Some entry ->
                                        let! decrypted = cipher.Decrypt (SecretsCipher.recordAadFor id) entry.Iv entry.Ciphertext
                                        return
                                            decrypted
                                            |> Result.map (fun value ->
                                                Some
                                                    { StoredRecord.Value = value
                                                      StoredRecord.Revision = entry.Revision
                                                      StoredRecord.UpdatedAt = entry.UpdatedAt })
                                }
                          RecordStore.Write =
                            fun id expected value ->
                                async {
                                    if value.Length > RecordLimits.maxValueLength then
                                        return
                                            Error (
                                                sprintf
                                                    "record %s is %d characters. A record holds at most %d."
                                                    (RecordName.value id.Name)
                                                    value.Length
                                                    RecordLimits.maxValueLength
                                            )
                                    else
                                        let! iv, ciphertext = cipher.Encrypt (SecretsCipher.recordAadFor id) value
                                        let current = RecordsFile.tryFind id recordsFile |> Option.map (fun e -> e.Revision)
                                        if current <> expected then
                                            return Ok (RecordWrite.Stale current)
                                        elif current.IsNone && RecordsFile.countOf id.Owner recordsFile >= RecordLimits.maxRecordsPerOwner then
                                            return
                                                Error (
                                                    sprintf
                                                        "%s already holds %d records, the most one owner may keep"
                                                        (CredentialOwner.describe id.Owner)
                                                        RecordLimits.maxRecordsPerOwner
                                                )
                                        else
                                            let revision = (defaultArg current 0) + 1
                                            let entry : RecordEntry =
                                                { Owner = id.Owner
                                                  Name = id.Name
                                                  Revision = revision
                                                  UpdatedAt = now ()
                                                  Iv = iv
                                                  Ciphertext = ciphertext }
                                            recordsFile <- RecordsFile.upsert entry recordsFile
                                            saveRecords ()
                                            return Ok (RecordWrite.Written revision)
                                } }

                return
                    Ok
                        { Secrets = secrets
                          Connections = connections
                          Records = records
                          RecordsRefused =
                            match recordsLoaded with
                            | Error reason -> Some reason
                            | Ok _ -> None
                          Durable = Option.isSome files
                          KekMinted = minted
                          EntriesAtOpen = loaded.Entries.Length + loaded.Connections.Length
                          RecordsAtOpen =
                            match recordsLoaded with
                            | Ok loadedRecords -> loadedRecords.Records.Length
                            | Error _ -> 0 }
    }

/// `openStore`, waiting out a credential manager that cannot answer yet instead of
/// failing. Every `KeyStoreFailure` is retried every `retryEveryMs` — above all a locked
/// keychain, which unlocks by itself when the person logs in — while a corrupt or
/// differently-sealed file still fails at once: no amount of waiting fixes those.
/// `onWait` hears each distinct failure once, not every attempt.
let openStoreWaiting (retryEveryMs: int) (onWait: OpenError -> unit) (files: StoreFiles option) (keyStore: KeyStore.KeyStore) : Async<Result<SecretStore, OpenError>> =
    let rec attempt (lastSaid: OpenError option) =
        async {
            match! openStore files keyStore with
            | Error (KeyStoreFailure _ as e) ->
                if lastSaid <> Some e then onWait e
                do! Async.Sleep retryEveryMs
                return! attempt (Some e)
            | result -> return result
        }
    attempt None

/// Secret resolution for environment injection (Plan 06): which value a `SecretRef` means
/// for THIS session. Exactly one place: the session's own scope.
///
/// A `SecretRef` is written by a repository's `yession.yaml`, and whoever can push to the
/// repo is neither the operator nor anybody in the session (docs/technical-design.md §4).
/// So a reference may reach only what a principal gave to this session on purpose: its own
/// secrets, which `set_secret` writes. It never reads a bound user's scope (no consent
/// surface lets a user hand a secret to a session yet), the deployment's `LocalScope`, or
/// the Manager's own process environment, which is the operator's and not a secret store.
module SecretResolution =

    /// What one SecretRef resolution came to — the feature's own observation type; the
    /// `Audit` submodule below adapts it to a log record (`Audit.injectObserver`).
    type InjectOutcome =
        | Injected
        | InjectMissed of reason: string

    type Observe = SessionId -> SecretName -> InjectOutcome -> unit

    /// The scopes a launch may read connection credentials from, most specific first: the
    /// session's own, then its bound users', then — where the Manager granted it
    /// unattributed access — the deployment's own. The connection status listing derives a
    /// caller's readable set from this list. `SecretRef` injection does NOT walk it: a
    /// reference resolves in the session's own scope alone (`compose`).
    let scopesFor (sessionId: SessionId) (users: Set<UserId>) (local: bool) : SecretScope list =
        SessionScope sessionId
        :: (users |> Set.toList |> List.map UserScope)
        @ (if local then [ LocalScope ] else [])

    /// The resolver the `/control/secrets/resolve` route serves. It holds the generic
    /// partition alone, so no name a reference writes can reach a connection credential. It
    /// builds the session's own `SecretId` from the verified caller, passes the same pure policy the control routes
    /// use (InjectSecret), and reads the store. A miss is an error that names the secret.
    let compose (observe: Observe) (secrets: Partition<SecretId, SecretMetadata>) : ResolveSecret =
        fun sessionId name ->
            async {
                let id : SecretId = { Scope = SessionScope sessionId; Name = name }
                // A reduced subject, on purpose: the resource is the caller's own scope, and
                // the only rule that permits it reads the session alone. The users bound to
                // the launch and its local access cannot change this decision, so the
                // resolver is not handed them.
                let request =
                    { Subject = { Session = Some sessionId; Users = Set.empty; Local = false }
                      Access = SecretAccess (InjectSecret, SecretResource id) }
                let missed reason =
                    observe sessionId name (InjectMissed reason)
                    Error reason
                match Policy.authorize request with
                | Deny reason -> return missed reason
                | Permit ->
                    match! secrets.Resolve id with
                    | Ok (Some value) ->
                        observe sessionId name Injected
                        return Ok value
                    | Ok None ->
                        return
                            missed (
                                sprintf
                                    "secret '%s' is not set for this session. A reference reads only the session's own secrets (set_secret)."
                                    (SecretName.value name)
                            )
                    | Error e -> return missed e
            }

/// The secrets/ABAC feature's audit telemetry (Plan 06). Audit is a cross-cutting concern,
/// but its records are a vertical feature concern: the Manager emits OTel-log-shaped records
/// about its own authority decisions here, in the feature that owns them — secrets operations,
/// policy denies, SecretRef injection, KEK/store lifecycle, user↔launch bindings, and
/// control-channel 401s. Identifiers and names only — no constructor accepts a secret value,
/// KEK material, or control secret, so content cannot leak by type. Records carry `event.name`
/// (`yession.*`) and `service.name = yession-manager`.
///
/// The cross-cutting *convention* is small: a `Record` shape and a `Sink` (a record consumer)
/// any feature could reuse. It lives with secrets — the only feature emitting audit today —
/// rather than in a horizontal audit module; extract it when a second feature needs it.
module Audit =

    /// A log attribute value — string or int (the two the audit records use).
    type LogValue =
        | StringValue of string
        | IntValue of int

    /// An OTel-log-shaped audit record: a human `Body`, a severity number
    /// (9 INFO / 13 WARN / 17 ERROR), and identifier/name attributes. Never a secret value.
    type Record =
        { Body : string
          Severity : int
          Attributes : Map<string, LogValue> }

    /// An audit record consumer — the cross-cutting seam. Built ONCE per Manager (createWithUi).
    type Sink = Record -> unit

    module Severity =
        let info = 9
        let warn = 13
        let error = 17

    let private make (name: string) (severity: int) (attributes: (string * LogValue) list) (body: string) : Record =
        { Body = body
          Severity = severity
          Attributes =
            Map.ofList
                ([ "event.name", StringValue name
                   "service.name", StringValue "yession-manager" ]
                 @ attributes) }

    let private scopeAttrs (scope: SecretScope) : (string * LogValue) list =
        match scope with
        | SessionScope sessionId ->
            [ "yession.secret.scope", StringValue "session"
              "yession.secret.scope_key", StringValue (SessionId.value sessionId) ]
        | UserScope user ->
            [ "yession.secret.scope", StringValue "user"
              "yession.secret.scope_key", StringValue (UserId.value user) ]
        // No scope_key: the deployment is the owner, so there is no id to name — and an
        // empty key would read as a missing one.
        | LocalScope ->
            [ "yession.secret.scope", StringValue "local" ]

    let private session (sessionId: SessionId) = "yession.session.id", StringValue (SessionId.value sessionId)
    let private outcome ok = "yession.outcome", StringValue (if ok then "ok" else "failed")

    let private secretOp (op: string) (sessionId: SessionId) (id: SecretId) (ok: bool) : Record =
        make
            (sprintf "yession.secret.%s" op)
            (if ok then Severity.info else Severity.warn)
            ([ session sessionId
               "yession.secret.name", StringValue (SecretName.value id.Name)
               outcome ok ]
             @ scopeAttrs id.Scope)
            (sprintf "secret %s %s" op (if ok then "ok" else "failed"))

    let secretSet (sessionId: SessionId) (id: SecretId) (ok: bool) : Record = secretOp "set" sessionId id ok
    let secretDelete (sessionId: SessionId) (id: SecretId) (ok: bool) : Record = secretOp "delete" sessionId id ok

    let secretList (sessionId: SessionId) (scope: SecretScope) (count: int) : Record =
        make "yession.secret.list" Severity.info
            ([ session sessionId; "yession.secret.count", IntValue count ] @ scopeAttrs scope)
            "secret list"

    // Render the inner case name only, so existing secrets log lines keep their wording
    // now that connection actions deny through the same record.
    let private actionLabel (access: Access) : string =
        match access with
        | SecretAccess (a, _) -> sprintf "%A" a
        | ConnectionAccess (a, _) -> sprintf "%A" a
        | RecordAccess (a, _) -> sprintf "%A" a

    /// A record's owner, in the scope attributes its owner's credentials are logged with, and
    /// its name. Never its value: the Manager does not read one.
    let private recordAttrs (id: RecordId) : (string * LogValue) list =
        ("yession.record.name", StringValue (RecordName.value id.Name)) :: scopeAttrs (CredentialOwner.scope id.Owner)

    let authzDeny (sessionId: SessionId) (access: Access) (reason: string) : Record =
        let resourceAttrs =
            match access with
            | SecretAccess (_, SecretResource id) ->
                ("yession.secret.name", StringValue (SecretName.value id.Name)) :: scopeAttrs id.Scope
            | SecretAccess (_, SecretCollection scope) -> scopeAttrs scope
            | ConnectionAccess (_, id) ->
                ("yession.secret.name", StringValue (SecretName.value id.Name)) :: scopeAttrs id.Scope
            | RecordAccess (_, id) -> recordAttrs id
        make "yession.authz.deny" Severity.warn
            ([ session sessionId
               "yession.authz.action", StringValue (actionLabel access)
               "yession.authz.reason", StringValue reason ]
             @ resourceAttrs)
            (sprintf "secrets: DENY %s for session %s: %s" (actionLabel access) (SessionId.value sessionId) reason)

    // Connection-broker records (Plan 08): lifecycle of Manager-brokered external-service
    // credentials. Identifiers, scopes, and kinds only — the observation type these adapt
    // (`Broker.BrokerObservation`) cannot carry token material.
    let private connectionAttrs (id: ConnectionId) : (string * LogValue) list =
        ("yession.secret.name", StringValue (SecretName.value id.Name)) :: scopeAttrs id.Scope

    let connectionConnected (id: ConnectionId) (kind: string) : Record =
        make "yession.connection.connected" Severity.info
            (("yession.connection.kind", StringValue kind) :: connectionAttrs id)
            "connection credential stored"

    let connectionDisconnected (id: ConnectionId) : Record =
        make "yession.connection.disconnected" Severity.info (connectionAttrs id) "connection credential removed"

    let connectionResolved (id: ConnectionId) (kind: string) (refreshed: bool) : Record =
        make "yession.connection.resolved" Severity.info
            ([ "yession.connection.kind", StringValue kind
               "yession.connection.refreshed", StringValue (if refreshed then "true" else "false") ]
             @ connectionAttrs id)
            "connection credential resolved for an agent turn"

    let connectionRefreshFailed (id: ConnectionId) (reason: string) : Record =
        make "yession.connection.refresh_failed" Severity.warn
            (("yession.connection.reason", StringValue reason) :: connectionAttrs id)
            "connection credential refresh failed"

    /// The provider refused a credential we hold, as reported by whoever spent it. `warn`
    /// like a failed refresh, and for the same reason: nothing is broken here, but a person
    /// has to do something before the next turn that needs this can run.
    let connectionRejected (id: ConnectionId) (reason: string) : Record =
        make "yession.connection.rejected" Severity.warn
            (("yession.connection.reason", StringValue reason) :: connectionAttrs id)
            "connection credential refused by the provider"

    let inject (sessionId: SessionId) (name: SecretName) (source: string) : Record =
        make "yession.secret.inject" Severity.info
            [ session sessionId
              "yession.secret.name", StringValue (SecretName.value name)
              "yession.inject.source", StringValue source ]
            "secret injected into environment"

    let injectMiss (sessionId: SessionId) (name: SecretName) (reason: string) : Record =
        make "yession.secret.inject" Severity.warn
            [ session sessionId
              "yession.secret.name", StringValue (SecretName.value name)
              "yession.inject.source", StringValue "none"
              "yession.authz.reason", StringValue reason ]
            "secret injection missed"

    /// A write to a record, and what it came to: `written`, `stale` (another write landed
    /// since the caller read it), or `failed`. Reads are not audited: a session reads a record
    /// as often as it acts on one, and a read changes nothing.
    let recordWrite (sessionId: SessionId) (id: RecordId) (outcome: string) : Record =
        make "yession.record.write" (if outcome = "failed" then Severity.warn else Severity.info)
            ([ session sessionId; "yession.outcome", StringValue outcome ] @ recordAttrs id)
            (sprintf "record write %s" outcome)

    let storeOpen (mode: string) (keystore: string) (kekMinted: bool) (entries: int) (records: int) : Record =
        make "yession.secrets.store_open" Severity.info
            [ "yession.secrets.mode", StringValue mode
              "yession.secrets.keystore", StringValue keystore
              "yession.secrets.kek", StringValue (if kekMinted then "created" else "loaded")
              "yession.secrets.entries", IntValue entries
              "yession.secrets.records", IntValue records ]
            (sprintf "secrets store open (%s)" mode)

    /// A records file this run will not read or write, and why. The credentials opened without
    /// it; every records call answers the same reason until the file is fixed or restored.
    let recordsRefused (reason: string) : Record =
        make "yession.secrets.records_refused" Severity.error
            [ "yession.secrets.reason", StringValue reason ]
            reason

    /// An in-memory store, and WHY. The severity is the point: a store the operator asked
    /// to be ephemeral (`--secrets ephemeral`) is the deliberate, safer posture and reads
    /// as INFO; one that fell back because this host offered no credential manager is a
    /// degraded deployment and reads as WARN. Logging both as warnings would invert the
    /// signal and train an operator to ignore the line that matters.
    let storeEphemeral (chosen: bool) : Record =
        make "yession.secrets.store_open" (if chosen then Severity.info else Severity.warn)
            [ "yession.secrets.mode", StringValue "ephemeral"
              "yession.secrets.reason", StringValue (if chosen then "operator" else "no-credential-manager") ]
            (if chosen then
                "secrets: --secrets ephemeral is set. Secrets are IN-MEMORY ONLY. They die with this Manager."
             else
                "secrets: no OS credential manager is available. Secrets are IN-MEMORY ONLY. They die with this Manager.")

    /// A durable file this run will not open. Says only what is true either way — the
    /// CAUSE is carried by the `storeEphemeral` record printed immediately before it, and
    /// the pair reads as one story.
    let storeInaccessible (path: string) : Record =
        make "yession.secrets.store_open" Severity.warn
            [ "yession.secrets.mode", StringValue "ephemeral"
              "yession.secrets.path", StringValue path ]
            (sprintf "secrets: %s exists, but this run's store is in-memory. This run does not read its entries (or change them)." path)

    let storeOpenFailed (kind: string) (detail: string) : Record =
        make "yession.secrets.store_open_failed" Severity.error
            [ "yession.secrets.reason", StringValue kind ]
            detail

    let bindingRecorded (sessionId: SessionId) (subject: UserId) : Record =
        make "yession.auth.binding_recorded" Severity.info
            [ session sessionId; "yession.auth.sub", StringValue (UserId.value subject) ]
            "user verified into launch"

    let bindingRevoked (sessionId: SessionId) : Record =
        make "yession.auth.binding_revoked" Severity.info
            [ session sessionId ]
            "user bindings revoked with launch"

    let controlUnauthorized (path: string) : Record =
        make "yession.control.unauthorized" Severity.warn
            [ "yession.http.path", StringValue path ]
            "invalid control secret"

    let private severityLabel (severity: int) : string =
        if severity = Severity.info then "INFO"
        elif severity = Severity.warn then "WARN"
        elif severity = Severity.error then "ERROR"
        else string severity

    /// One greppable line, deterministic (Map iterates key-sorted): severity, event
    /// name, attributes (minus the two that lead the line), then the human body.
    let format (r: Record) : string =
        let name =
            match Map.tryFind "event.name" r.Attributes with
            | Some (StringValue n) -> n
            | _ -> "-"
        let attrs =
            r.Attributes
            |> Map.toList
            |> List.filter (fun (k, _) -> k <> "event.name" && k <> "service.name")
            |> List.map (fun (k, v) ->
                match v with
                | StringValue s -> sprintf "%s=%s" k s
                | IntValue i -> sprintf "%s=%d" k i)
            |> String.concat " "
        sprintf "audit %s %s %s :: %s" (severityLabel r.Severity) name attrs r.Body

    /// The prod sink: one greppable audit line to stdout. (Forwarding audit to an OTel
    /// collector alongside session telemetry is a documented follow-up.)
    let stdout : Sink = fun r -> printfn "%s" (format r)

    /// Map an injection's outcome (SecretResolution.InjectOutcome) to a record — the
    /// feature's own observation type, adapted to an audit record right here beside it.
    let injectObserver (sink: Sink) : SecretResolution.Observe =
        fun sessionId name outcome ->
            match outcome with
            | SecretResolution.Injected -> sink (inject sessionId name "session")
            | SecretResolution.InjectMissed reason -> sink (injectMiss sessionId name reason)
