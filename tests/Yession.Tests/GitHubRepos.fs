module Yession.Tests.GitHubRepos

// Finding a repository, below the browser: the provider lookups that answer it, and the
// session route that carries them to a person choosing one.
//
// What is worth pinning is what a picker leans on:
//
//   * the name a candidate carries is the one the PROVIDER calls it — a clone follows a
//     renamed repo's old name silently, so a listing that echoed what was typed would hand
//     `add_repo` the one name it cannot notice is stale;
//   * "my repos" and "repos named like this" are two questions to two endpoints, and the
//     route asks the right one from the presence of `?q=`;
//   * a listing is answered on the CALLER's credential, so what it shows is what their
//     `add_repo` will clone — and with no credential "my repos" is a sign-in that has not
//     happened, said as one, not an empty list;
//   * the door is shut: which repos a person can see is for the people in the session.
//
// Ports for the lookups and the route, because both ARE HTTP conversations; the decoders
// alone run in the cheap tier.

open Fable.Core
open Fable.Pyxpecto
open Yession.Domain
open Yession.Domain.Repos
open Yession.App
open Yession.Host
open Yession.SessionProcess

#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

let private expect result =
    match result with
    | Ok v -> v
    | Error e -> failwithf "invariant: %A" e

let private repo (name: string) = RepoRef.create name |> expect

type private HttpReply = { status: int; body: string }

[<Emit("""(function (url, cookie) { return (
fetch(url, { headers: cookie ? { cookie: cookie } : {}, cache: 'no-store' })
  .then(async r => ({ status: r.status, body: await r.text() }))
) })($0, $1)""")>]
let private get (url: string) (cookie: string) : JS.Promise<HttpReply> = Util.jsNative

let private serving (handler: Interop.IncomingMessage -> Interop.ServerResponse -> unit) =
    async {
        let server = Interop.createServer handler
        let! listening =
            Async.FromContinuations (fun (cont, _, _) -> server.listen (0, "127.0.0.1", fun () -> cont server) |> ignore)
        return sprintf "http://127.0.0.1:%d" (Interop.serverPort listening)
    }

let private json (res: Interop.ServerResponse) (status: int) (body: string) =
    res.writeHead (status, JsInterop.createObj [ "content-type", box "application/json" ]) |> ignore
    res.``end`` body

// --- the provider's JSON -------------------------------------------------------------------

let private candidate (fullName: string) =
    sprintf
        """{"full_name":%s,"description":"a thing","default_branch":"trunk","private":true,"pushed_at":"2026-09-01T00:00:00Z"}"""
        (Encode.toString 0 (Encode.string fullName))

let private decoderTests =
    testList "the provider's json" [

        testCase "a candidate is named as the provider names it" <| fun () ->
            let decoded = Decode.fromString GitHubRepos.candidateDecoder (candidate "trinketworks/yession") |> expect
            Expect.equal decoded.Repo (repo "trinketworks/yession") "full_name is the canonical name"
            Expect.equal decoded.DefaultBranch "trunk" "the default branch is read"
            Expect.isTrue decoded.Private "so is whether the credential is what makes it visible"
            Expect.equal decoded.Description (Some "a thing") "and the description"

        testCase "a name this session cannot hold refuses the listing rather than shortening it" <| fun () ->
            // A list decoded short would show every repo but the one somebody was looking
            // for, with nothing said; refusing the reply is what makes the fault visible.
            let listing = sprintf "[%s,%s]" (candidate "octo/hello") (candidate "not a repo name at all")
            Expect.isError (Decode.fromString GitHubRepos.listingDecoder listing) "refused whole"

        testCase "search wraps its items; a listing does not" <| fun () ->
            let listing = Decode.fromString GitHubRepos.listingDecoder (sprintf "[%s]" (candidate "octo/hello")) |> expect
            let search = Decode.fromString GitHubRepos.searchDecoder (sprintf """{"items":[%s]}""" (candidate "octo/hello")) |> expect
            Expect.equal (listing |> List.map (fun c -> c.Repo)) [ repo "octo/hello" ] "listing"
            Expect.equal (search |> List.map (fun c -> c.Repo)) [ repo "octo/hello" ] "search"

        testCase "branches are read by name" <| fun () ->
            let branches = Decode.fromString GitHubRepos.branchesDecoder """[{"name":"main"},{"name":"feature/x"}]""" |> expect
            Expect.equal branches [ "main"; "feature/x" ] "names only"

        testCase "a pull request's head is the fork's repo and branch, not the repo the link named" <| fun () ->
            let pr = """{"number":42,"head":{"ref":"fix/thing","repo":{"full_name":"fork-owner/hello"}},"base":{"repo":{"full_name":"octo/hello"}}}"""
            let head = Decode.fromString GitHubPrs.pullHeadDecoder pr |> expect
            Expect.equal head.Repo (repo "fork-owner/hello") "where the branch lives"
            Expect.equal head.Branch "fix/thing" "and which branch"

        testCase "a pull request whose fork is gone has no head to check out" <| fun () ->
            let pr = """{"number":42,"head":{"ref":"fix/thing","repo":null}}"""
            Expect.isError (Decode.fromString GitHubPrs.pullHeadDecoder pr) "refused rather than pointed at nothing"
    ]

// --- a stand-in for api.github.com ------------------------------------------------------------

/// The three endpoints, recording how each was asked. `/user/repos` and `/search` both
/// answer one candidate, named so a case can tell which endpoint it came from.
type private StubApi =
    { Url : string
      Requests : ResizeArray<string * string option> }

let private startStubApi () : Async<StubApi> =
    async {
        let requests = ResizeArray<string * string option> ()
        let handler (req: Interop.IncomingMessage) (res: Interop.ServerResponse) =
            let url : string = req.url
            let bearer = Interop.headerOf req "authorization"
            requests.Add (url, bearer)
            if url.StartsWith "/user/repos" then
                if bearer.IsNone || bearer = Some "Bearer dead" then json res 401 """{"message":"Requires authentication"}"""
                else json res 200 (sprintf "[%s]" (candidate "mine/recent"))
            elif url.StartsWith "/search/repositories" then json res 200 (sprintf """{"items":[%s]}""" (candidate "found/by-name"))
            elif url.StartsWith "/repos/octo/hello/branches" then json res 200 """[{"name":"main"},{"name":"next"}]"""
            elif url.StartsWith "/repos/octo/hello/pulls/42" then
                json res 200 """{"number":42,"head":{"ref":"fix/thing","repo":{"full_name":"fork-owner/hello"}}}"""
            elif url.StartsWith "/repos/" then json res 404 """{"message":"Not Found"}"""
            else json res 500 "{}"
        let! url = serving handler
        return { Url = url; Requests = requests }
    }

let private lookupTests =
    testList "the provider lookups" [

        testCaseAsync "my repos needs a credential, and says so as a sign-in rather than an empty list" <|
            async {
                let! api = startStubApi ()
                let! anonymous = GitHubRepos.recentOver api.Url None
                Expect.equal anonymous (Error GitHubRepos.NoCredential) "no credential is not an empty answer"
                Expect.equal api.Requests.Count 0 "and nothing was asked of the provider"
                let! mine = GitHubRepos.recentOver api.Url (Some "ghp_x")
                Expect.equal (expect mine |> List.map (fun c -> c.Repo)) [ repo "mine/recent" ] "with one, the listing"
                let asked, bearer = api.Requests.[0]
                Expect.isTrue (asked.Contains "sort=pushed") "most recently pushed first"
                Expect.isTrue (asked.Contains "affiliation=owner,collaborator,organization_member") "every affiliation, not just owned"
                Expect.equal bearer (Some "Bearer ghp_x") "on the credential it was given"
            }

        testCaseAsync "search works anonymously, and an empty search asks nothing" <|
            async {
                let! api = startStubApi ()
                let! nothing = GitHubRepos.searchOver api.Url None "   "
                Expect.equal (expect nothing) [] "blank is no question"
                Expect.equal api.Requests.Count 0 "and reaches no endpoint"
                let! found = GitHubRepos.searchOver api.Url None "hello"
                Expect.equal (expect found |> List.map (fun c -> c.Repo)) [ repo "found/by-name" ] "search answers"
                let asked, _ = api.Requests.[0]
                Expect.isTrue (asked.Contains "in%3Aname") "matched on the name"
            }

        testCaseAsync "a dead credential, a missing repo and a spent allowance are told apart" <|
            async {
                let! api = startStubApi ()
                let! missing = GitHubRepos.branchesOver api.Url None (repo "octo/gone")
                Expect.equal missing (Error GitHubRepos.NotFound) "404"
                let! branches = GitHubRepos.branchesOver api.Url None (repo "octo/hello")
                Expect.equal (expect branches) [ "main"; "next" ] "the branches of one that is there"
                let! refused = GitHubRepos.recentOver api.Url (Some "dead")
                Expect.equal refused (Error GitHubRepos.Refused) "a 401 is a refusal"
            }
    ]

// --- the route ---------------------------------------------------------------------------------

let private stubAuth () : SessionAuth.Auth =
    { Configure = fun _ _ _ _ -> async { return Ok () }
      IsAuthenticated = fun req -> (Interop.headerOf req "cookie").IsSome
      IdentityOf =
        fun req ->
            match Interop.headerOf req "cookie" with
            | Some cookie when cookie.StartsWith "who=" ->
                Some
                    ({ Subject = cookie.Substring 4
                       DisplayName = None
                       Attribution = AttributedUser (UserId.create (cookie.Substring 4) |> expect) } : CookieIdentity)
            | _ -> None
      BeginLogin = fun _ -> async { return None }
      HandleCallback = fun _ -> async { return Error (500, "not under test") }
      CookieName = "who" }

/// The route over a stub API, with a credential table keyed by who is asking: what the
/// route must do is resolve the CALLER's token, and this is what shows it did.
let private startRoutes (api: StubApi) (tokens: (CredentialFor * string) list) =
    async {
        let tokenFor (actor: CredentialFor) =
            async { return tokens |> List.tryFind (fun (who, _) -> who = actor) |> Option.map snd }
        let route = GitHubRepos.routes (stubAuth ()) tokenFor api.Url ""
        return!
            serving (fun req res ->
                if not (route req res) then
                    res.writeHead (404, JsInterop.createObj [ "content-type", box "text/plain" ]) |> ignore
                    res.``end`` "not found")
    }

let private alice = CredentialFor.Person (Principal.User (UserId.create "alice" |> expect))

let private routeTests =
    testList "the repo routes" [

        testCaseAsync "the door: no cookie reaches nothing" <|
            async {
                let! api = startStubApi ()
                let! url = startRoutes api [ alice, "ghp_alice" ]
                let! repos = get (url + "/github/repos") "" |> Async.AwaitPromise
                Expect.equal repos.status 401 "the listing is gated"
                let! branches = get (url + "/github/repos/octo/hello/branches") "" |> Async.AwaitPromise
                Expect.equal branches.status 401 "so are the branches"
                Expect.equal api.Requests.Count 0 "nothing reached the provider"
            }

        testCaseAsync "a listing is answered on the caller's credential, as the provider names it" <|
            async {
                let! api = startStubApi ()
                let! url = startRoutes api [ alice, "ghp_alice" ]
                let! reply = get (url + "/github/repos") "who=alice" |> Async.AwaitPromise
                Expect.equal reply.status 200 "answered"
                let listing = Codec.fromString Codec.repoCandidates reply.body |> expect
                Expect.equal (listing |> List.map (fun c -> c.Repo)) [ repo "mine/recent" ] "the provider's name for it, in the codec the picker reads"
                Expect.equal (listing |> List.map (fun c -> c.DefaultBranch)) [ "trunk" ] "and its default branch"
                let _, bearer = api.Requests.[0]
                Expect.equal bearer (Some "Bearer ghp_alice") "alice's token, not anyone else's"
            }

        testCaseAsync "with no credential, my repos is a sign-in that has not happened" <|
            async {
                let! api = startStubApi ()
                let! url = startRoutes api []
                let! reply = get (url + "/github/repos") "who=alice" |> Async.AwaitPromise
                Expect.equal reply.status 401 "said as a sign-in"
                Expect.isTrue (reply.body.Contains "connect GitHub") "in words that name the way out"
                Expect.equal api.Requests.Count 0 "without asking the provider what it cannot answer"
            }

        testCaseAsync "?q= asks by name instead, and works with no credential" <|
            async {
                let! api = startStubApi ()
                let! url = startRoutes api []
                let! reply = get (url + "/github/repos?q=hello") "who=alice" |> Async.AwaitPromise
                Expect.equal reply.status 200 "answered anonymously"
                let listing = Codec.fromString Codec.repoCandidates reply.body |> expect
                Expect.equal (listing |> List.map (fun c -> c.Repo)) [ repo "found/by-name" ] "from the search endpoint"
            }

        testCaseAsync "branches are read for the repo the path names" <|
            async {
                let! api = startStubApi ()
                let! url = startRoutes api [ alice, "ghp_alice" ]
                let! reply = get (url + "/github/repos/octo/hello/branches") "who=alice" |> Async.AwaitPromise
                Expect.equal reply.status 200 "answered"
                Expect.equal (Codec.fromString Codec.branchNames reply.body) (Ok [ "main"; "next" ]) "the names, in the codec the picker reads"
                let! gone = get (url + "/github/repos/octo/gone/branches") "who=alice" |> Async.AwaitPromise
                Expect.equal gone.status 404 "and a repo the credential cannot see is a 404 with words"
            }

        testCaseAsync "a pull request's head is read for the number the path names" <|
            async {
                let! api = startStubApi ()
                let! url = startRoutes api [ alice, "ghp_alice" ]
                let! reply = get (url + "/github/repos/octo/hello/pulls/42") "who=alice" |> Async.AwaitPromise
                Expect.equal reply.status 200 "answered"
                Expect.equal
                    (Codec.fromString Codec.pullHead reply.body)
                    (Ok { PullHead.Repo = repo "fork-owner/hello"; PullHead.Branch = "fix/thing" })
                    "the fork and its branch, in the codec the picker reads"
                let! notNumber = get (url + "/github/repos/octo/hello/pulls/latest") "who=alice" |> Async.AwaitPromise
                Expect.equal notNumber.status 400 "a number is what the path takes"
                let! gone = get (url + "/github/repos/octo/hello/pulls/7") "who=alice" |> Async.AwaitPromise
                Expect.equal gone.status 404 "and one the credential cannot see is a 404 with words"
            }
    ]

let tests =
    testList "GitHubRepos" [ decoderTests ]

let portsTests =
    testList "GitHubRepos over HTTP" [ lookupTests; routeTests ]
