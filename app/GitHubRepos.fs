module Yession.Host.GitHubRepos

// Everything GitHub-specific about FINDING a repository, and nothing else — the
// `GitHubPrs.fs` precedent, for the same reason: which repos a credential reaches, which
// ones match a name somebody typed, and which branches one has are three REST endpoints
// and their JSON, and a second forge is a second copy of this file. What is left once the
// answers are read is provider-neutral: a repo name, a description, a default branch.
//
// The name every answer carries is the one GitHub calls CANONICAL (`full_name`), which is
// the fact `add_repo` cannot get from a clone: github.com follows a renamed repository's
// old name with a redirect, so a checkout made from one clones fine and keeps an `origin`
// that the provider no longer answers to by that name. A repo chosen from here was named
// by the provider, and that is the one name it will not redirect.

open System
open Fable.Core
open Fable.Core.JsInterop
open Yession.Domain
open Yession.Domain.Repos
open Yession.SessionProcess
open Yession.Host.Interop

#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

/// Why a look could not answer. Words a person acts on rather than statuses, and one case
/// per distinct thing to do about it.
type LookupFailure =
    /// `/user/repos` asked with no credential at all. GitHub has no anonymous answer to
    /// "my repos", so this is not a refusal — it is a sign-in that has not happened.
    | NoCredential
    /// 401: the credential is dead. The sign-in panel's "sign in again".
    | Refused
    /// 404: not a repo this credential can see, which GitHub says identically for one
    /// that does not exist.
    | NotFound
    /// 403/429: the credential's hourly allowance is spent.
    | RateLimited
    | Unreachable of string

module LookupFailure =

    let describe (failure: LookupFailure) : string =
        match failure with
        | NoCredential -> "connect GitHub to list your repositories"
        | Refused -> "github rejected this credential — sign in again"
        | NotFound -> "github does not show that repository to this credential"
        | RateLimited -> "github is rate limiting this credential — try again shortly"
        | Unreachable said -> sprintf "github could not be reached: %s" said

// --- the provider's JSON, decoded ------------------------------------------------------

/// A name GitHub says and this session cannot hold is a decode failure, not a crash: the
/// listing it came in is refused whole rather than answered short.
let candidateDecoder : Decoder<RepoCandidate> =
    Decode.field "full_name" Decode.string
    |> Decode.andThen (fun name ->
        match RepoRef.create name with
        | Error e -> Decode.fail (sprintf "github named a repository this session cannot: %s" e)
        | Ok repo ->
            Decode.object (fun get ->
                { RepoCandidate.Repo = repo
                  Description = get.Optional.Field "description" Decode.string
                  DefaultBranch = get.Optional.Field "default_branch" Decode.string |> Option.defaultValue "main"
                  Private = get.Optional.Field "private" Decode.bool |> Option.defaultValue false
                  PushedAt = get.Optional.Field "pushed_at" Decode.string }))

/// `GET /user/repos` answers a bare list; `GET /search/repositories` wraps one in `items`.
let listingDecoder : Decoder<RepoCandidate list> = Decode.list candidateDecoder

let searchDecoder : Decoder<RepoCandidate list> = Decode.field "items" (Decode.list candidateDecoder)

/// `GET /repos/{owner}/{repo}/branches`: only the names are wanted.
let branchesDecoder : Decoder<string list> = Decode.list (Decode.field "name" Decode.string)

// --- the three GETs ----------------------------------------------------------------------

type private Reply =
    abstract reachable : bool
    abstract status : int
    abstract body : string

/// One GET, as GitHub wants it asked: a bearer token when there is one, the versioned
/// accept header, and a user agent (GitHub refuses requests without one).
[<Emit("""(function (url, token) {
  const headers = { 'accept': 'application/vnd.github+json', 'user-agent': 'yession',
                    'x-github-api-version': '2022-11-28' }
  if (token) headers['authorization'] = 'Bearer ' + token
  return fetch(url, { headers })
    .then(async r => ({ reachable: true, status: r.status, body: await r.text() }))
    .catch(e => ({ reachable: false, status: 0, body: String((e && e.message) || e) }))
})($0, $1)""")>]
let private getJson (url: string) (token: string) : JS.Promise<Reply> = jsNative

/// A value on its way into a query string.
[<Emit("encodeURIComponent($0)")>]
let private urlPart (value: string) : string = jsNative

let private failureOf (reply: Reply) : LookupFailure =
    if not reply.reachable then Unreachable reply.body
    elif reply.status = 401 then Refused
    elif reply.status = 404 then NotFound
    elif reply.status = 403 || reply.status = 429 then RateLimited
    else Unreachable (sprintf "github answered %d" reply.status)

let private read (decoder: Decoder<'a>) (url: string) (token: string option) : Async<Result<'a, LookupFailure>> =
    async {
        let! reply = getJson url (Option.toObj token) |> awaitPromise
        if not (reply.reachable && reply.status >= 200 && reply.status < 300) then return Error (failureOf reply)
        else
            match Decode.fromString decoder reply.body with
            | Ok value -> return Ok value
            | Error e -> return Error (Unreachable (sprintf "unrecognised reply: %s" e))
    }

/// How many a listing carries. One page, deliberately: a person choosing a repo reads the
/// top of a list ordered by recency, and anything further down is what search is for.
let pageSize = 30

/// The repositories a credential reaches, most recently pushed first. Every affiliation
/// GitHub knows — owned, collaborated on, and through an organisation — because "the
/// repos I work on" is all three and the default (`owner`) leaves out most of a working
/// developer's.
let recentOver (apiBase: string) (token: string option) : Async<Result<RepoCandidate list, LookupFailure>> =
    match token with
    | None -> async { return Error NoCredential }
    | Some _ ->
        read
            listingDecoder
            (sprintf
                "%s/user/repos?sort=pushed&per_page=%d&affiliation=owner,collaborator,organization_member"
                (apiBase.TrimEnd '/')
                pageSize)
            token

/// Repositories whose name matches, anywhere on GitHub the credential can see — so a
/// public repo somebody else owns is one search away, without the session having to be
/// told its exact name. Anonymous works, and sees only what anyone can.
let searchOver (apiBase: string) (token: string option) (text: string) : Async<Result<RepoCandidate list, LookupFailure>> =
    let query = text.Trim ()
    if query = "" then async { return Ok [] }
    else
        read
            searchDecoder
            (sprintf
                "%s/search/repositories?q=%s&per_page=%d"
                (apiBase.TrimEnd '/')
                (urlPart (query + " in:name"))
                pageSize)
            token

/// One repository as GitHub names it NOW, or why it could not say.
///
/// The one question `add_repo` cannot answer from a clone: github.com follows a renamed
/// repository's old name with a redirect, so a clone under the old name lands and keeps an
/// `origin` the provider no longer answers to by that name. Asked before cloning, so a
/// stale name is refused with the current one rather than kept.
let canonicalOver (apiBase: string) (token: string option) (repo: RepoRef) : Async<Result<RepoRef, LookupFailure>> =
    async {
        match! read candidateDecoder (sprintf "%s/repos/%s" (apiBase.TrimEnd '/') (RepoRef.value repo)) token with
        | Ok candidate -> return Ok candidate.Repo
        | Error failure -> return Error failure
    }

/// The branches a repository has. One page of a hundred, which is every branch of nearly
/// every repository and the first hundred of the rest.
let branchesOver (apiBase: string) (token: string option) (repo: RepoRef) : Async<Result<string list, LookupFailure>> =
    read
        branchesDecoder
        (sprintf "%s/repos/%s/branches?per_page=100" (apiBase.TrimEnd '/') (RepoRef.value repo))
        token

// --- the browser-facing routes ------------------------------------------------------------
// Two reads a person makes while choosing: which repos, and which branches of one. Cookie-
// gated like the connection panels, and answered on the CALLER's credential by the same
// precedence a repo verb spends — so what the list shows is what `add_repo` will be able
// to clone, and not a repo some other credential in the session can see.

open Yession.App

let private respondJson (res: ServerResponse) (status: int) (json: string) =
    res.writeHead (status, createObj [ "content-type", box "application/json"; "cache-control", box "no-store" ]) |> ignore
    res.``end`` json

let private respondText (res: ServerResponse) (status: int) (text: string) =
    res.writeHead (status, createObj [ "content-type", box "text/plain"; "cache-control", box "no-store" ]) |> ignore
    res.``end`` text

/// The JSON the browser reads a listing as: the codec the picker decodes with, so the
/// browser reads one wire shape rather than two.
let encodeListing (candidates: RepoCandidate list) : string = Codec.toString Codec.repoCandidates candidates

let encodeBranches (branches: string list) : string = Codec.toString Codec.branchNames branches

/// Which HTTP status a failure is said with. A missing credential and a dead one are both
/// 401 — the browser's answer to either is the sign-in panel — but with different words.
let private statusOf (failure: LookupFailure) : int =
    match failure with
    | NoCredential
    | Refused -> 401
    | NotFound -> 404
    | RateLimited -> 429
    | Unreachable _ -> 502

/// Build the `/github/repos*` route handler. `tokenFor` is the session's own resolution of
/// an actor's GitHub credential — the one every repo verb spends — so this file holds no
/// notion of scopes or precedence.
let routes
    (auth: SessionAuth.Auth)
    (tokenFor: Principal option -> Async<string option>)
    (apiBase: string)
    (mount: string)
    : IncomingMessage -> ServerResponse -> bool =
    fun req res ->
        let path = req.url.Split('?').[0]
        match SessionRoute.parseUnder mount req.``method`` path with
        | Some GitHubRepos
        | Some (GitHubBranches _) as route ->
            match auth.IdentityOf req with
            | None -> respondText res 401 "unauthorized"
            | Some identity ->
                let actor = PeerAttribution.principal identity.Attribution
                Async.StartImmediate (
                    async {
                        let! token = tokenFor actor
                        match route with
                        | Some GitHubRepos ->
                            let! answer =
                                match queryParamOf req.url "q" with
                                | Some text when text.Trim () <> "" -> searchOver apiBase token text
                                | _ -> recentOver apiBase token
                            match answer with
                            | Ok candidates -> respondJson res 200 (encodeListing candidates)
                            | Error failure -> respondText res (statusOf failure) (LookupFailure.describe failure)
                        | Some (GitHubBranches (owner, name)) ->
                            match RepoRef.create (owner + "/" + name) with
                            | Error e -> respondText res 400 (sprintf "not a repo name: %s" e)
                            | Ok repo ->
                                match! branchesOver apiBase token repo with
                                | Ok branches -> respondJson res 200 (encodeBranches branches)
                                | Error failure -> respondText res (statusOf failure) (LookupFailure.describe failure)
                        | _ -> ()
                    })
            true
        | Some _
        | None -> false
