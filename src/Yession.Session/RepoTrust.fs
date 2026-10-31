namespace Yession.Session

open Yession.Domain
open Yession.Codecs
open Yession.Domain.Access
open Yession.Domain.Sandboxes

#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

// Repo trust: whether a person trusts a repository's code to run with their GitHub. It is kept
// as their record in the Manager (`RecordId`), so a second session with the same repository
// does not ask them again
// (docs/decisions/2026-11-01-a-repo-runs-with-the-github-of-somebody-who-trusts-it.md).
//
// The Manager stores the text this module writes and never reads it. What a record means is
// decided here and nowhere else: a card asks about `RepoTrust.unanswered`, and a loan into a
// repository's sandbox holds `RepoTrust.granted`. Both read the same record, so what a person
// was asked and what their GitHub is spent on cannot become two answers.

/// A person's trust in one repository.
[<RequireQualifiedAccess>]
type RepoTrust =
    { /// The repository's name when the person last answered. For a person reading their own
      /// records, never for matching: the record's NAME carries the repository's GitHub id,
      /// which a rename or a transfer keeps and a new repository under the old name does not.
      Repo : string
      /// The routes the repository's code may run with their GitHub by.
      Granted : Set<ConnectionRoute>
      /// Every route a card has asked them about, granted or not. Only a route outside this set
      /// asks them again.
      Seen : Set<ConnectionRoute> }

/// The routes one person's trust lets one repository's sandboxes spend that person's GitHub by.
///
/// Private, and built only from that person's record (`RepoTrust.granted`). So a loan into a
/// repository's sandbox cannot be made from anything else: not from what the repository's file
/// asks for, and not from somebody else's yes. The owner travels with the routes, so the token
/// a loan resolves and the record that allowed it belong to one person.
type GrantedRoutes =
    private
        { Owner : CredentialOwner
          Routes : Set<ConnectionRoute> }

module GrantedRoutes =

    /// Whose GitHub these routes are: the owner whose record granted them.
    let owner (granted: GrantedRoutes) : CredentialOwner = granted.Owner

    let routes (granted: GrantedRoutes) : Set<ConnectionRoute> = granted.Routes

    let allows (route: ConnectionRoute) (granted: GrantedRoutes) : bool = Set.contains route granted.Routes

[<RequireQualifiedAccess>]
module RepoTrust =

    /// The name a person's trust in the repository with this GitHub id is kept under. The
    /// schema's version is in it, so a build that encodes trust differently never writes over
    /// this one's record.
    let recordName (githubId: int64) : RecordName =
        match RecordName.create (sprintf "repo-trust/v1/github/%d" githubId) with
        | Ok name -> name
        | Error e -> failwithf "repo trust record name invariant violated: %s" e

    /// Whose records a person's trust is kept in: their own when the Manager verified them,
    /// and otherwise the deployment's, which is whose GitHub a person nobody verified spends
    /// (`GitHubConnection.turnTargets`). Where the deployment attributes its users, the Manager
    /// refuses that owner, so an unverified person trusts nothing.
    let ownerOf (person: Principal) : CredentialOwner =
        CredentialOwner.ofPrincipal person |> Option.defaultValue LocalOwner

    let recordOf (owner: CredentialOwner) (githubId: int64) : RecordId =
        { RecordId.Owner = owner; RecordId.Name = recordName githubId }

    /// What a card asks this person about this repository now: the routes it asks for that
    /// they have not answered. Empty means their record already says.
    let unanswered (asked: Set<ConnectionRoute>) (trust: RepoTrust option) : Set<ConnectionRoute> =
        match trust with
        | None -> asked
        | Some trust -> Set.difference asked trust.Seen

    /// Whether this person's record answers everything the repository asks for.
    let answers (asked: Set<ConnectionRoute>) (trust: RepoTrust option) : bool =
        Set.isEmpty (unanswered asked trust)

    /// A person's answer to a card that showed `shown`, with `ticked` ticked. Every route it
    /// showed is answered, and a route left unticked is answered no, so it does not ask again.
    /// A route it did not show stays as it was.
    let answer
        (repo: RepoRef)
        (shown: Set<ConnectionRoute>)
        (ticked: Set<ConnectionRoute>)
        (trust: RepoTrust option)
        : RepoTrust =
        let before =
            trust
            |> Option.defaultValue
                { RepoTrust.Repo = RepoRef.value repo
                  RepoTrust.Granted = Set.empty
                  RepoTrust.Seen = Set.empty }
        { RepoTrust.Repo = RepoRef.value repo
          RepoTrust.Granted = Set.union (Set.difference before.Granted shown) (Set.intersect ticked shown)
          RepoTrust.Seen = Set.union before.Seen shown }

    /// The routes a loan into this repository's sandbox may carry of `owner`'s GitHub: the ones
    /// the sandbox forwards that the owner's record grants. No record grants nothing.
    let granted (owner: CredentialOwner) (forwarded: ConnectionRoute list) (trust: RepoTrust option) : GrantedRoutes =
        { Owner = owner
          Routes =
            match trust with
            | None -> Set.empty
            | Some trust -> Set.intersect (Set.ofList forwarded) trust.Granted }

    let private routes : Codec<Set<ConnectionRoute>> =
        { Encode = fun routes -> routes |> Set.toList |> List.map (ConnectionRoute.name >> Encode.string) |> Encode.list
          Decode = Decode.list (Codec.viaSmartCtor ConnectionRoute.parse Decode.string) |> Decode.map Set.ofList }

    /// `{"repo": …, "granted": ["git", …], "seen": [...]}`. A route this build does not know
    /// fails the decode, and a record that does not decode is no trust at all: the person is
    /// asked again rather than lent by a reading of half their answer.
    let codec : Codec<RepoTrust> =
        { Encode =
            fun (trust: RepoTrust) ->
                Encode.object
                    [ "repo", Encode.string trust.Repo
                      "granted", routes.Encode trust.Granted
                      "seen", routes.Encode trust.Seen ]
          Decode =
            Decode.object (fun get ->
                { RepoTrust.Repo = get.Required.Field "repo" Decode.string
                  RepoTrust.Granted = get.Required.Field "granted" routes.Decode
                  RepoTrust.Seen = get.Required.Field "seen" routes.Decode }) }

    let toText (trust: RepoTrust) : string = Codec.toString codec trust

    let ofText (text: string) : Result<RepoTrust, string> = Codec.fromString codec text

/// A session's way to the trust records of the people in it. Composed over the Manager's
/// records and GitHub's repository ids (`RepoTrusts.over`), and handed both to what asks and to
/// what lends, so the two read one record through one reader.
[<RequireQualifiedAccess>]
type RepoTrusts =
    { /// A person's trust in a repository, as their record says now. `Error` is a record that
      /// could not be read — the Manager refused it, or GitHub could not identify the
      /// repository — and every caller reads it as no trust.
      Read : CredentialOwner -> RepoRef -> Async<Result<RepoTrust option, string>>
      /// A person's answer to a card (what it showed, what they ticked), merged into their
      /// record and written. A write that lands first is read and merged again.
      Answer : CredentialOwner -> RepoRef -> Set<ConnectionRoute> -> Set<ConnectionRoute> -> Async<Result<RepoTrust, string>>
      /// What a block in this repository's sandbox may be lent of the owner's GitHub, by the
      /// routes the sandbox forwards.
      Granted : CredentialOwner -> RepoRef -> ConnectionRoute list -> Async<GrantedRoutes> }

[<RequireQualifiedAccess>]
module RepoTrusts =

    /// A session with nowhere to keep trust: nobody here trusts anything, and an answer says why.
    let none (why: string) : RepoTrusts =
        { RepoTrusts.Read = fun _ _ -> async { return Ok None }
          RepoTrusts.Answer = fun _ _ _ _ -> async { return Error why }
          RepoTrusts.Granted = fun owner _ forwarded -> async { return RepoTrust.granted owner forwarded None } }

    /// How many times an answer is merged again over writes that landed first, before it says so.
    let private attempts = 3

    let over
        (read: RecordId -> Async<Result<StoredRecord option, string>>)
        (write: RecordId -> int option -> string -> Async<Result<RecordWrite, string>>)
        (identify: CredentialOwner -> RepoRef -> Async<Result<int64, string>>)
        : RepoTrusts =
        // A repository's id is asked once per session. It is the same answer every time, and a
        // loan asks at every command.
        let mutable ids : Map<string, int64> = Map.empty
        let idOf (owner: CredentialOwner) (repo: RepoRef) : Async<Result<int64, string>> =
            async {
                match Map.tryFind (RepoRef.value repo) ids with
                | Some id -> return Ok id
                | None ->
                    match! identify owner repo with
                    | Ok id ->
                        ids <- Map.add (RepoRef.value repo) id ids
                        return Ok id
                    | Error reason -> return Error reason
            }
        // A record that does not decode is no trust: the person is asked again, and their answer
        // is written over it at the revision it was read at.
        let decoded (stored: StoredRecord option) : RepoTrust option =
            stored |> Option.bind (fun stored -> RepoTrust.ofText stored.Value |> Result.toOption)
        let readTrust (owner: CredentialOwner) (repo: RepoRef) : Async<Result<RepoTrust option, string>> =
            async {
                match! idOf owner repo with
                | Error reason -> return Error reason
                | Ok id ->
                    match! read (RepoTrust.recordOf owner id) with
                    | Error reason -> return Error reason
                    | Ok stored -> return Ok (decoded stored)
            }
        let answer
            (owner: CredentialOwner)
            (repo: RepoRef)
            (shown: Set<ConnectionRoute>)
            (ticked: Set<ConnectionRoute>)
            : Async<Result<RepoTrust, string>> =
            async {
                match! idOf owner repo with
                | Error reason -> return Error reason
                | Ok id ->
                    let target = RepoTrust.recordOf owner id
                    let rec attempt (left: int) =
                        async {
                            match! read target with
                            | Error reason -> return Error reason
                            | Ok stored ->
                                let next = RepoTrust.answer repo shown ticked (decoded stored)
                                match! write target (stored |> Option.map (fun s -> s.Revision)) (RepoTrust.toText next) with
                                | Error reason -> return Error reason
                                | Ok (RecordWrite.Written _) -> return Ok next
                                | Ok (RecordWrite.Stale _) when left > 1 -> return! attempt (left - 1)
                                | Ok (RecordWrite.Stale _) ->
                                    return
                                        Error (
                                            sprintf
                                                "your trust in %s kept changing in another session while this one wrote it. Answer again."
                                                (RepoRef.value repo)
                                        )
                        }
                    return! attempt attempts
            }
        { RepoTrusts.Read = readTrust
          RepoTrusts.Answer = answer
          RepoTrusts.Granted =
            fun owner repo forwarded ->
                async {
                    match! readTrust owner repo with
                    | Ok trust -> return RepoTrust.granted owner forwarded trust
                    | Error _ -> return RepoTrust.granted owner forwarded None
                } }
