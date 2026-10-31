namespace Yession.Manager

open System
open Yession.Domain
open Yession.Codecs
open Yession.Domain.Access

#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

/// The records envelope: the pure state behind the store's third family, and its explicit
/// codec — the ONLY path between the records and bytes, as `SecretsFile` is for the other two.
/// A value exists here only as per-entry AES-256-GCM ciphertext under the store's key,
/// AAD-bound to its owner and name, so an entry moved to another owner or name does not open.
/// Owners, names, revisions and timestamps are cleartext by design.
///
/// Its own file beside `secrets.json`, with its own version, so that a Manager rolled back to a
/// build that has no records leaves them as they are, rather than refusing every secret
/// (docs/decisions/2026-11-01-records-are-a-third-family-of-the-store.md).
type RecordEntry =
    { Owner : CredentialOwner
      Name : RecordName
      /// 1 at the first write, and one more at every write after it.
      Revision : int
      UpdatedAt : DateTimeOffset
      /// base64url, 12 random bytes, fresh per encryption.
      Iv : string
      /// base64url AES-256-GCM output (ciphertext ‖ 16-byte tag).
      Ciphertext : string }

type RecordsFile =
    { Version : int
      /// Which key sealed these entries. The store refuses a file another key sealed instead
      /// of reading every entry as corrupt.
      KekId : string
      /// In first-write order; (Owner, Name) is the key.
      Records : RecordEntry list }

module RecordsFile =

    let currentVersion = 1

    let empty (kekId: string) : RecordsFile =
        { Version = currentVersion; KekId = kekId; Records = [] }

    let tryFind (id: RecordId) (file: RecordsFile) : RecordEntry option =
        file.Records |> List.tryFind (fun e -> e.Owner = id.Owner && e.Name = id.Name)

    /// Insert or replace by (Owner, Name).
    let upsert (entry: RecordEntry) (file: RecordsFile) : RecordsFile =
        let same (e: RecordEntry) = e.Owner = entry.Owner && e.Name = entry.Name
        if file.Records |> List.exists same then
            { file with Records = file.Records |> List.map (fun e -> if same e then entry else e) }
        else
            { file with Records = file.Records @ [ entry ] }

    /// How many records one owner holds.
    let countOf (owner: CredentialOwner) (file: RecordsFile) : int =
        file.Records |> List.filter (fun e -> e.Owner = owner) |> List.length

module RecordsCodec =

    let private recordName : Codec<RecordName> =
        { Encode = RecordName.value >> Encode.string
          Decode =
            Decode.string
            |> Decode.andThen (fun raw ->
                match RecordName.create raw with
                | Ok v -> Decode.succeed v
                | Error e -> Decode.fail e) }

    /// `{"kind":"user","sub":…}` | `{"kind":"local"}`: the shape a scope has on the secrets
    /// wire, without its session case, because a record has no session owner.
    let credentialOwner : Codec<CredentialOwner> =
        { Encode =
            fun owner ->
                match owner with
                | UserOwner user -> Encode.object [ "kind", Encode.string "user"; "sub", Encode.string (UserId.value user) ]
                | LocalOwner -> Encode.object [ "kind", Encode.string "local" ]
          Decode =
            Decode.field "kind" Decode.string
            |> Decode.andThen (function
                | "user" ->
                    Decode.field "sub" Decode.string
                    |> Decode.andThen (fun raw ->
                        match UserId.create raw with
                        | Ok user -> Decode.succeed (UserOwner user)
                        | Error e -> Decode.fail e)
                | "local" -> Decode.succeed LocalOwner
                | other -> Decode.fail (sprintf "Unknown record owner: %s" other)) }

    /// `{owner, name}`, on the control wire as in the file.
    let recordId : Codec<RecordId> =
        { Encode =
            fun (id: RecordId) ->
                Encode.object [ "owner", credentialOwner.Encode id.Owner; "name", recordName.Encode id.Name ]
          Decode =
            Decode.object (fun get ->
                { RecordId.Owner = get.Required.Field "owner" credentialOwner.Decode
                  RecordId.Name = get.Required.Field "name" recordName.Decode }) }

    let private recordEntry : Codec<RecordEntry> =
        { Encode =
            fun (e: RecordEntry) ->
                Encode.object
                    [ "owner", credentialOwner.Encode e.Owner
                      "name", recordName.Encode e.Name
                      "revision", Encode.int e.Revision
                      "updatedAt", Codec.timestamp.Encode e.UpdatedAt
                      "iv", Encode.string e.Iv
                      "ciphertext", Encode.string e.Ciphertext ]
          Decode =
            Decode.object (fun get ->
                { RecordEntry.Owner = get.Required.Field "owner" credentialOwner.Decode
                  RecordEntry.Name = get.Required.Field "name" recordName.Decode
                  RecordEntry.Revision = get.Required.Field "revision" Decode.int
                  RecordEntry.UpdatedAt = get.Required.Field "updatedAt" Codec.timestamp.Decode
                  RecordEntry.Iv = get.Required.Field "iv" Decode.string
                  RecordEntry.Ciphertext = get.Required.Field "ciphertext" Decode.string }) }

    /// The refusal of a file a newer build wrote.
    let newerBuild (version: int) : string =
        sprintf "records schema version %d was written by a newer build (this one reads %d)" version RecordsFile.currentVersion

    /// Version-dispatched like `SecretsCodec.secretsFile`: a version this build has never heard
    /// of is refused rather than read as the newest one it knows, because read that way the
    /// first write would save back only what this build recognised.
    let recordsFile : Codec<RecordsFile> =
        { Encode =
            fun (f: RecordsFile) ->
                Encode.object
                    [ "version", Encode.int f.Version
                      "kekId", Encode.string f.KekId
                      "records", f.Records |> List.map recordEntry.Encode |> Encode.list ]
          Decode =
            Decode.field "version" Decode.int
            |> Decode.andThen (fun version ->
                match version with
                | 1 ->
                    Decode.object (fun get ->
                        { RecordsFile.Version = version
                          RecordsFile.KekId = get.Required.Field "kekId" Decode.string
                          RecordsFile.Records = get.Required.Field "records" (Decode.list recordEntry.Decode) })
                | _ -> Decode.fail (newerBuild version)) }

    let toString (file: RecordsFile) : string =
        recordsFile.Encode file |> Encode.toString 2

    let fromString (json: string) : Result<RecordsFile, string> =
        Decode.fromString recordsFile.Decode json

    /// The schema version a file names, when it names one this build cannot read.
    let newerVersionOf (json: string) : int option =
        match Decode.fromString (Decode.field "version" Decode.int) json with
        | Ok version when version > RecordsFile.currentVersion -> Some version
        | _ -> None
