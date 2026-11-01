namespace Yession.Domain.Access

open Yession.Domain
open System

/// Records vocabulary: what session code keeps for a person ACROSS their sessions, as a value
/// the Manager stores and never reads. The third family of the store, beside generic secrets
/// and connection credentials
/// (docs/decisions/2026-11-01-records-are-a-third-family-of-the-store.md).
///
/// The session owns each record's schema. It encodes its own type as text on the way in and
/// decodes it on the way out, so the Manager learns nothing when a feature starts keeping one:
/// not what it is about, not which service, not which repository.
///
/// A record belongs to an OWNER, never to a session. What one session knows about itself is
/// its own event log. The owner is the principal a connection credential belongs to — a user
/// the Manager verified, or the deployment's one unattributed principal — so the policy answers
/// for a record exactly where it answers for that owner's credentials (`Policy.authorize`).

/// A record's name among its owner's records: lowercase letters, digits, `.`, `_`, `-` and `/`,
/// at most 200 characters (`repo-trust/v1/github/42`). The code that owns a schema chooses the
/// name and puts the schema's version in it, so two builds that encode differently never write
/// over each other's record. The characters are narrow because the name is the one part of a
/// record the Manager shows: it is an attribute of every audit line about the record, and
/// those are `key=value` pairs separated by spaces.
type RecordName = private RecordName of string

module RecordName =

    let maxLength = 200

    let private allowed (c: char) =
        (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c = '.' || c = '_' || c = '-' || c = '/'

    let create (raw: string) : Result<RecordName, string> =
        if String.IsNullOrEmpty raw then Error "a record name cannot be empty"
        elif raw.Length > maxLength then Error (sprintf "a record name is at most %d characters" maxLength)
        elif raw |> Seq.forall allowed |> not then
            Error (sprintf "record name '%s' holds a character other than a-z, 0-9, '.', '_', '-' and '/'" raw)
        else Ok (RecordName raw)

    let value (RecordName name) = name

/// A record's identity: whose it is, and its name among theirs.
[<RequireQualifiedAccess>]
type RecordId = { Owner : CredentialOwner; Name : RecordName }

module RecordId =

    /// A stable one-line rendering for logs and cipher AAD ("user:<sub>:<name>" /
    /// "local:<name>"). The value never appears in it.
    let describe (id: RecordId) : string =
        sprintf "%s:%s" (CredentialOwner.describe id.Owner) (RecordName.value id.Name)

/// A stored record as a read answers it: the value it was given, and the revision a write
/// names to replace it (`RecordWrite`).
[<RequireQualifiedAccess>]
type StoredRecord =
    { Value : string
      Revision : int
      UpdatedAt : DateTimeOffset }

/// What a write came to. A write names the revision it read (`None`: there was no record), and
/// replaces the record only while that is still the stored one. So two sessions that change one
/// person's record at the same time cannot lose either change: the second is told `Stale`,
/// reads again, merges, and writes again. Qualified, because `Written` is already a file
/// tool's outcome (`FilesFacts`).
[<RequireQualifiedAccess>]
type RecordWrite =
    | Written of revision: int
    /// The record is not at the revision the write named. It is at `current`, or there is no
    /// record at all when `current` is `None`.
    | Stale of current: int option
