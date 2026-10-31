module Yession.Tests.RepoTrust

// A person's trust in a repository: the record schema, what a card asks, what a loan may hold,
// and the reader that keeps both on one record. Pure, over an in-memory stand-in for the
// Manager's records and for GitHub's repository ids — the cheap tier, every environment.

open Fable.Pyxpecto
open Yession.Domain
open Yession.Domain.Access
open Yession.Domain.Sandboxes
open Yession.Session

let private expect =
    function
    | Ok v -> v
    | Error e -> failwith e

let private repo = RepoRef.create "trinketworks/yession" |> expect
let private alice = UserOwner (UserId.create "alice" |> expect)
let private bob = UserOwner (UserId.create "bob" |> expect)
let private both = Set.ofList [ ConnectionRoute.Git; ConnectionRoute.Api ]
let private git = Set.singleton ConnectionRoute.Git

/// A trust that granted `granted` after being asked about `seen`.
let private trusting (granted: Set<ConnectionRoute>) (seen: Set<ConnectionRoute>) : RepoTrust option =
    Some { RepoTrust.Repo = RepoRef.value repo; RepoTrust.Granted = granted; RepoTrust.Seen = seen }

/// The Manager's records, in memory: a revision per record, and a write at a stale revision
/// refused as the store refuses it. `interfere` runs once before the first write, so a case can
/// land another session's write in between a read and a write.
let private records () =
    let mutable kept : Map<string, StoredRecord> = Map.empty
    let mutable interfere : (unit -> unit) option = None
    let key (id: RecordId) = RecordId.describe id
    let put (id: RecordId) (value: string) =
        let revision = Map.tryFind (key id) kept |> Option.map (fun r -> r.Revision + 1) |> Option.defaultValue 1
        kept <-
            Map.add
                (key id)
                { StoredRecord.Value = value
                  StoredRecord.Revision = revision
                  StoredRecord.UpdatedAt = System.DateTimeOffset.UnixEpoch }
                kept
    let read (id: RecordId) = async { return Ok (Map.tryFind (key id) kept) }
    let write (id: RecordId) (expected: int option) (value: string) =
        async {
            interfere |> Option.iter (fun run -> interfere <- None; run ())
            let current = Map.tryFind (key id) kept |> Option.map (fun r -> r.Revision)
            if current <> expected then return Ok (RecordWrite.Stale current)
            else
                put id value
                return Ok (RecordWrite.Written (defaultArg current 0 + 1))
        }
    read, write, put, (fun run -> interfere <- Some run)

let private identified (id: int64) = fun (_: CredentialOwner) (_: RepoRef) -> async { return Ok id }

let private trustsOver () =
    let read, write, put, interfere = records ()
    RepoTrusts.over read write (identified 42L), put, interfere

let tests =
    testList "Repo trust" [
        testCase "a person with no record is asked about every route the repository asks for" <| fun () ->
            Expect.equal (RepoTrust.unanswered both None) both "everything is asked"

        testCase "a route the person answered no to does not ask again" <| fun () ->
            Expect.equal (RepoTrust.unanswered both (trusting git both)) Set.empty "nothing is asked"

        testCase "a route the repository newly asks for asks again" <| fun () ->
            Expect.equal (RepoTrust.unanswered both (trusting git git)) (Set.singleton ConnectionRoute.Api) "only the new route"

        testCase "an answer grants what was ticked of what was shown" <| fun () ->
            let answered = RepoTrust.answer repo both git None
            Expect.equal answered.Granted git "git granted, api not"

        testCase "an answer leaves a route it did not show as it was" <| fun () ->
            let answered = RepoTrust.answer repo (Set.singleton ConnectionRoute.Api) Set.empty (trusting git git)
            Expect.equal answered.Granted git "git still granted"

        testCase "an answer that unticks a granted route takes it back" <| fun () ->
            let answered = RepoTrust.answer repo both git (trusting both both)
            Expect.equal answered.Granted git "api taken back"

        testCase "a loan holds only routes the sandbox forwards and the record grants" <| fun () ->
            let granted = RepoTrust.granted alice [ ConnectionRoute.Git ] (trusting both both)
            Expect.equal (GrantedRoutes.routes granted) git "api is granted but not forwarded"

        testCase "no record grants nothing" <| fun () ->
            Expect.equal (GrantedRoutes.routes (RepoTrust.granted alice [ ConnectionRoute.Git; ConnectionRoute.Api ] None)) Set.empty "nothing"

        testCase "a granted loan names the owner whose record granted it" <| fun () ->
            Expect.equal (GrantedRoutes.owner (RepoTrust.granted bob [ ConnectionRoute.Git ] None)) bob "bob's"

        testCase "a record round-trips through its text" <| fun () ->
            let trust = (trusting git both).Value
            Expect.equal (RepoTrust.ofText (RepoTrust.toText trust)) (Ok trust) "round trip"

        testCase "a record naming a route this build does not know does not decode" <| fun () ->
            Expect.isError (RepoTrust.ofText """{"repo":"a/b","granted":["ssh"],"seen":["ssh"]}""") "refused"

        testCase "the record is named by the repository's GitHub id, with the schema's version" <| fun () ->
            Expect.equal (RecordName.value (RepoTrust.recordName 42L)) "repo-trust/v1/github/42" "named"

        testCaseAsync "an answer is read back as the person's trust" <|
            async {
                let trusts, _, _ = trustsOver ()
                let! _ = trusts.Answer alice repo both git
                let! read = trusts.Read alice repo
                Expect.equal (expect read |> Option.map (fun t -> t.Granted)) (Some git) "read back"
            }

        testCaseAsync "one person's answer is not another's trust" <|
            async {
                let trusts, _, _ = trustsOver ()
                let! _ = trusts.Answer alice repo both both
                let! read = trusts.Read bob repo
                Expect.equal (expect read) None "bob has not answered"
            }

        testCaseAsync "an answer over another session's write keeps both" <|
            async {
                let trusts, put, interfere = trustsOver ()
                // Another session answers about git while this one is answering about api.
                interfere (fun () ->
                    put (RepoTrust.recordOf alice 42L) (RepoTrust.toText (RepoTrust.answer repo git git None)))
                let! _ = trusts.Answer alice repo (Set.singleton ConnectionRoute.Api) (Set.singleton ConnectionRoute.Api)
                let! read = trusts.Read alice repo
                Expect.equal (expect read |> Option.map (fun t -> t.Granted)) (Some both) "both answers kept"
            }

        testCaseAsync "a record that does not decode is no trust" <|
            async {
                let trusts, put, _ = trustsOver ()
                put (RepoTrust.recordOf alice 42L) "{ not trust"
                let! granted = trusts.Granted alice repo [ ConnectionRoute.Git ]
                Expect.equal (GrantedRoutes.routes granted) Set.empty "nothing lent"
            }

        testCaseAsync "a repository GitHub cannot identify lends nothing" <|
            async {
                let read, write, _, _ = records ()
                let trusts = RepoTrusts.over read write (fun _ _ -> async { return Error "not found" })
                let! granted = trusts.Granted alice repo [ ConnectionRoute.Git ]
                Expect.equal (GrantedRoutes.routes granted) Set.empty "nothing lent"
            }
    ]
