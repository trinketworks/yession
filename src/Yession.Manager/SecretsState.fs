namespace Yession.Manager

open System
open Yession.Domain
open Yession.Codecs
open Yession.Domain.Sandboxes
open Yession.Domain.Access

#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

/// The secrets envelope (Plan 06): the pure state behind the Manager's secret store,
/// and its explicit codec — the ONLY path between the store and bytes (the
/// `ManagerState`/`ManagerCodec` discipline). Values exist here only as per-entry
/// AES-256-GCM ciphertext, AAD-bound to the entry's scope+name, so the codec cannot
/// leak what it never holds and a ciphertext transplanted onto another entry fails
/// authentication. Metadata (scopes, names, timestamps) is cleartext BY DESIGN — it is
/// exactly what the list surface serves.
type SecretEntry =
    { Scope : SecretScope
      Name : SecretName
      CreatedAt : DateTimeOffset
      UpdatedAt : DateTimeOffset
      /// base64url, 12 random bytes — fresh per encryption, never reused under the KEK.
      Iv : string
      /// base64url AES-256-GCM output (ciphertext ‖ 16-byte tag, the WebCrypto layout).
      Ciphertext : string }

/// The store's two families, kept in two lists so that no key of one can name an entry of
/// the other (`SecretId` vs `ConnectionId`). A file at `Version` 1 predates the split and
/// holds everything in `Entries`; `SecretStore.openStore` sorts it, because sorting needs
/// the values and only the store can decrypt them.
type SecretsFile =
    { /// Schema version — the migration hook (ManagerState discipline).
      Version : int
      /// Names WHICH KEK sealed these entries, so a decrypt failure distinguishes
      /// "sealed by a different key" from corruption — both loud, differently worded.
      KekId : string
      /// Generic secrets, in first-set order; (Scope, Name) is the key.
      Entries : SecretEntry list
      /// Connection credentials, in first-set order; (Scope, Name) is the key.
      Connections : SecretEntry list }

/// Insert/find/remove over one family's list, keyed by (Scope, Name). Private to this file:
/// callers go through `SecretsFile`, whose functions name the family by the key's type.
module private EntryList =

    let tryFind (scope: SecretScope) (name: SecretName) (entries: SecretEntry list) : SecretEntry option =
        entries |> List.tryFind (fun e -> e.Scope = scope && e.Name = name)

    /// Insert or replace by (Scope, Name). A replaced entry keeps its CreatedAt.
    let upsert (entry: SecretEntry) (entries: SecretEntry list) : SecretEntry list =
        match tryFind entry.Scope entry.Name entries with
        | Some existing ->
            entries
            |> List.map (fun e ->
                if e.Scope = entry.Scope && e.Name = entry.Name then { entry with CreatedAt = existing.CreatedAt } else e)
        | None -> entries @ [ entry ]

    let remove (scope: SecretScope) (name: SecretName) (entries: SecretEntry list) : SecretEntry list =
        entries |> List.filter (fun e -> not (e.Scope = scope && e.Name = name))

module SecretsFile =

    let currentVersion = 2

    let empty (kekId: string) : SecretsFile =
        { Version = currentVersion; KekId = kekId; Entries = []; Connections = [] }

    let tryFind (id: SecretId) (file: SecretsFile) : SecretEntry option =
        EntryList.tryFind id.Scope id.Name file.Entries

    let upsert (entry: SecretEntry) (file: SecretsFile) : SecretsFile =
        { file with Entries = EntryList.upsert entry file.Entries }

    let remove (id: SecretId) (file: SecretsFile) : SecretsFile =
        { file with Entries = EntryList.remove id.Scope id.Name file.Entries }

    /// The metadata listing for one scope. Returns `SecretMetadata` — a type that
    /// cannot carry a value.
    let list (scope: SecretScope) (file: SecretsFile) : SecretMetadata list =
        file.Entries
        |> List.filter (fun e -> e.Scope = scope)
        |> List.map (fun e ->
            { SecretMetadata.Id = { SecretId.Scope = e.Scope; SecretId.Name = e.Name }
              SecretMetadata.CreatedAt = e.CreatedAt
              SecretMetadata.UpdatedAt = e.UpdatedAt })

    let tryFindConnection (id: ConnectionId) (file: SecretsFile) : SecretEntry option =
        EntryList.tryFind id.Scope id.Name file.Connections

    let upsertConnection (entry: SecretEntry) (file: SecretsFile) : SecretsFile =
        { file with Connections = EntryList.upsert entry file.Connections }

    let removeConnection (id: ConnectionId) (file: SecretsFile) : SecretsFile =
        { file with Connections = EntryList.remove id.Scope id.Name file.Connections }

    let listConnections (scope: SecretScope) (file: SecretsFile) : ConnectionMetadata list =
        file.Connections
        |> List.filter (fun e -> e.Scope = scope)
        |> List.map (fun e ->
            { ConnectionMetadata.Id = { ConnectionId.Scope = e.Scope; ConnectionId.Name = e.Name }
              ConnectionMetadata.CreatedAt = e.CreatedAt
              ConnectionMetadata.UpdatedAt = e.UpdatedAt })

module SecretsCodec =

    let private secretName : Codec<SecretName> =
        { Encode = SecretName.value >> Encode.string
          Decode =
            Decode.string
            |> Decode.andThen (fun raw ->
                match SecretName.create raw with
                | Ok v -> Decode.succeed v
                | Error e -> Decode.fail e) }

    let private userSubject : Codec<UserId> =
        { Encode = UserId.value >> Encode.string
          Decode =
            Decode.string
            |> Decode.andThen (fun raw ->
                match UserId.create raw with
                | Ok v -> Decode.succeed v
                | Error e -> Decode.fail e) }

    /// Shared with the control wire:
    /// {"kind":"session",...} | {"kind":"user",...} | {"kind":"local"}. A stored `peer` entry
    /// is a scope this build no longer has (see `SecretScope`), and the user said no
    /// deployment holds one — so it is refused as unknown rather than read into anything.
    /// `local` carries no key — the deployment IS the owner, so there is nothing to name.
    let secretScope : Codec<SecretScope> =
        { Encode =
            (fun scope ->
                match scope with
                | SessionScope sessionId ->
                    Encode.object [ "kind", Encode.string "session"; "sessionId", Codec.sessionId.Encode sessionId ]
                | UserScope user ->
                    Encode.object [ "kind", Encode.string "user"; "sub", userSubject.Encode user ]
                | LocalScope ->
                    Encode.object [ "kind", Encode.string "local" ])
          Decode =
            Decode.field "kind" Decode.string
            |> Decode.andThen (function
                | "session" -> Decode.field "sessionId" Codec.sessionId.Decode |> Decode.map SessionScope
                | "user" -> Decode.field "sub" userSubject.Decode |> Decode.map UserScope
                | "local" -> Decode.succeed LocalScope
                | other -> Decode.fail (sprintf "Unknown secret scope: %s" other)) }

    let secretMetadata : Codec<SecretMetadata> =
        { Encode =
            fun (m: SecretMetadata) ->
                Encode.object
                    [ "scope", secretScope.Encode m.Id.Scope
                      "name", secretName.Encode m.Id.Name
                      "createdAt", Codec.timestamp.Encode m.CreatedAt
                      "updatedAt", Codec.timestamp.Encode m.UpdatedAt ]
          Decode =
            Decode.object (fun get ->
                { SecretMetadata.Id =
                    { Scope = get.Required.Field "scope" secretScope.Decode
                      Name = get.Required.Field "name" secretName.Decode }
                  SecretMetadata.CreatedAt = get.Required.Field "createdAt" Codec.timestamp.Decode
                  SecretMetadata.UpdatedAt = get.Required.Field "updatedAt" Codec.timestamp.Decode }) }

    let private secretEntry : Codec<SecretEntry> =
        { Encode =
            fun (e: SecretEntry) ->
                Encode.object
                    [ "scope", secretScope.Encode e.Scope
                      "name", secretName.Encode e.Name
                      "createdAt", Codec.timestamp.Encode e.CreatedAt
                      "updatedAt", Codec.timestamp.Encode e.UpdatedAt
                      "iv", Encode.string e.Iv
                      "ciphertext", Encode.string e.Ciphertext ]
          Decode =
            Decode.object (fun get ->
                { SecretEntry.Scope = get.Required.Field "scope" secretScope.Decode
                  SecretEntry.Name = get.Required.Field "name" secretName.Decode
                  SecretEntry.CreatedAt = get.Required.Field "createdAt" Codec.timestamp.Decode
                  SecretEntry.UpdatedAt = get.Required.Field "updatedAt" Codec.timestamp.Decode
                  SecretEntry.Iv = get.Required.Field "iv" Decode.string
                  SecretEntry.Ciphertext = get.Required.Field "ciphertext" Decode.string }) }

    /// The refusal of a file a newer build wrote, in `ManagerCodec`'s wording.
    let newerBuild (version: int) : string =
        sprintf "secrets store schema version %d was written by a newer build (this one reads %d)" version SecretsFile.currentVersion

    let secretsFile : Codec<SecretsFile> =
        { Encode =
            fun (f: SecretsFile) ->
                Encode.object
                    [ "version", Encode.int f.Version
                      "kekId", Encode.string f.KekId
                      "entries", f.Entries |> List.map secretEntry.Encode |> Encode.list
                      "connections", f.Connections |> List.map secretEntry.Encode |> Encode.list ]
          // Version-DISPATCHED, as `ManagerCodec` is. A version 1 file has no `connections`:
          // it predates the split, and everything it holds is in `entries` until the store
          // sorts it. A version this build has never heard of is refused rather than read as
          // the newest one it knows: read that way, a downgrade would drop what it does not
          // recognise and save the loss back over the original.
          Decode =
            Decode.field "version" Decode.int
            |> Decode.andThen (fun version ->
                let file (connections: Decode.IGetters -> SecretEntry list) =
                    Decode.object (fun get ->
                        { SecretsFile.Version = version
                          SecretsFile.KekId = get.Required.Field "kekId" Decode.string
                          SecretsFile.Entries = get.Required.Field "entries" (Decode.list secretEntry.Decode)
                          SecretsFile.Connections = connections get })
                match version with
                | 1 -> file (fun _ -> [])
                | 2 -> file (fun get -> get.Required.Field "connections" (Decode.list secretEntry.Decode))
                | _ -> Decode.fail (newerBuild version)) }

    let toString (file: SecretsFile) : string =
        secretsFile.Encode file |> Encode.toString 2

    let fromString (json: string) : Result<SecretsFile, string> =
        Decode.fromString secretsFile.Decode json

    /// The schema version a file names, when it names one this build cannot read. Read
    /// before the whole file, so a newer build's file is refused as that and not as corrupt.
    let newerVersionOf (json: string) : int option =
        match Decode.fromString (Decode.field "version" Decode.int) json with
        | Ok version when version > SecretsFile.currentVersion -> Some version
        | _ -> None
