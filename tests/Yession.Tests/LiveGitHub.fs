module Yession.Tests.LiveGitHub

// The GitHub conversations, against GitHub itself.
//
// Every other suite that talks to GitHub talks to a stub on a loopback port, which proves
// what this host SENDS and what it makes of a reply it was handed — and nothing about
// whether GitHub still answers that way. A renamed field, a header GitHub stopped honouring,
// a conditional request it started counting: each of those is green against a stub forever.
// These cases ask the real API the questions the product asks, through the same functions.
//
// Read-only, and against this repository alone, because the credential a run has is scoped
// to it (a GitHub Actions token, or a sandbox proxy's) and can write to it. Nothing here
// opens, pushes or edits anything.
//
// `LiveGitHub` is a GitHub token GitHub accepts. `check` probes for one before a case runs,
// through `HTTPS_PROXY` where there is one — which is where a sandbox keeps the real
// credential behind a placeholder, and why these go through `Http` like the product does.

open Fable.Pyxpecto
open Yession.Domain
open Yession.Domain.Prs
open Yession.Host
open Yession.Host.PrWatches

let private api = "https://api.github.com"

let private expect =
    function
    | Ok v -> v
    | Error e -> failwith (string e)

let private here = RepoRef.create "trinketworks/yession" |> expect

/// The token the run was given. `check` refused to start without one GitHub accepts.
let private token = Some (Interop.envOr "GITHUB_TOKEN" "")

/// A pull request that merged long ago and will not move again.
let private settled = { PrRef.Repo = here; Number = 903 }

let private fetch = GitHubPrs.fetchOver api GitHubPrs.Spending.unmetered

let tests =
    testList "GitHub, for real" [

        testCaseAsync "names this repository as GitHub does" <|
            async {
                let! canonical = GitHubRepos.canonicalOver api token here
                Expect.equal canonical (Ok here) "the canonical name is the one asked"
            }

        // Paged, because GitHub lists branches by name and a busy repository has a page of
        // `claude/...` ahead of `master` — which is the listing the branch picker pages too.
        testCaseAsync "finds this repository's default branch by paging its branches" <|
            async {
                let rec seek (page: int) =
                    async {
                        match! GitHubRepos.branchesOver api token here page with
                        | Error failure -> return failwithf "page %d did not list: %A" page failure
                        | Ok [] -> return false
                        | Ok names when List.contains "master" names -> return true
                        | Ok _ when page >= 50 -> return false
                        | Ok _ -> return! seek (page + 1)
                    }
                let! found = seek 1
                Expect.isTrue found "the default branch is on one of the pages"
            }

        testCaseAsync "reads a merged pull request as merged" <|
            async {
                match! fetch token settled PrEtags.none None with
                | PrChanged (snapshot, _) -> Expect.equal snapshot.State PrMerged "it went in"
                | other -> failwithf "the pull request did not read: %A" other
            }

        // What a watch leans on to poll cheaply: GitHub answers a look asked with the ETags
        // of the last one with 304, which it does not count against the rate limit. A stub
        // answers 304 because it was written to; this is GitHub saying it still does.
        testCaseAsync "a second look at an unmoved pull request comes back unchanged" <|
            async {
                match! fetch token settled PrEtags.none None with
                | PrChanged (snapshot, etags) ->
                    let! again = fetch token settled etags (Some snapshot)
                    Expect.equal again PrUnchanged "nothing moved, so nothing is said"
                | other -> failwithf "the first look did not read: %A" other
            }
    ]
