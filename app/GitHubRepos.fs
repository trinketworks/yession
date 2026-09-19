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
open Fable.NodeExtras
open Node.Api
open Node.Buffer
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
    /// GitHub answered, and this session could not read what it said. Not `Unreachable`:
    /// the request went, the reply came back, and it was a 2xx — what failed is the
    /// decoding. Told apart because the remedy is not the same one: "could not be reached"
    /// sends a person to look at their network for a fault that is in the payload.
    | Unreadable of string
    | Unreachable of string

module LookupFailure =

    let describe (failure: LookupFailure) : string =
        match failure with
        | NoCredential -> "connect GitHub to list your repositories"
        | Refused -> "github rejected this credential — sign in again"
        | NotFound -> "github does not show that repository to this credential"
        | RateLimited -> "github is rate limiting this credential — try again shortly"
        | Unreadable said -> sprintf "github answered with something this session could not read: %s" said
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
    { Reachable : bool
      Status : int
      Body : string }

/// How every request in this file presents itself to GitHub: the versioned accept header, a
/// user agent (GitHub refuses requests without one), and a bearer token when there is one.
///
/// `GitHubPrs.fs` carries its own copy of these three, deliberately: each of these files is
/// the whole of one endpoint family and owns what it knows about the provider outright, so
/// a second forge is a second copy of a file rather than a shared GitHub layer that neither
/// of them owns.
let sentHeaders (token: string) : (string * string) list =
    [ yield "accept", "application/vnd.github+json"
      yield "user-agent", "yession"
      yield "x-github-api-version", "2022-11-28"
      if not (String.IsNullOrEmpty token) then yield "authorization", "Bearer " + token ]

/// One GET, as GitHub wants it asked.
let private getJson (url: string) (token: string) : Async<Reply> =
    async {
        let! attempt = Http.text url [ Http.headers (sentHeaders token) ]
        match attempt with
        | Http.Answered (response, body) -> return { Reachable = true; Status = response.Status; Body = body }
        | Http.Unreachable reason -> return { Reachable = false; Status = 0; Body = reason }
    }

/// What a status GitHub answered with means for a look.
let failureAt (status: int) : LookupFailure =
    if status = 401 then Refused
    elif status = 404 then NotFound
    elif status = 403 || status = 429 then RateLimited
    else Unreachable (sprintf "github answered %d" status)

/// A reply that never arrived carries why in place of a body; everything else is a status.
let private failureOf (reply: Reply) : LookupFailure =
    if not reply.Reachable then Unreachable reply.Body else failureAt reply.Status

let private read (decoder: Decoder<'a>) (url: string) (token: string option) : Async<Result<'a, LookupFailure>> =
    async {
        let! reply = getJson url (Option.toObj token)
        if not (reply.Reachable && reply.Status >= 200 && reply.Status < 300) then return Error (failureOf reply)
        else
            match Decode.fromString decoder reply.Body with
            | Ok value -> return Ok value
            | Error e -> return Error (Unreadable e)
    }

/// How many a page carries.
let pageSize = 30

/// How many pages are ever offered. GitHub's search refuses past the thousandth result, so
/// a listing that kept offering another page would end in a 502 at the foot of a scroll
/// rather than in a stop. One number for both listings, because the ceiling that bites is
/// the lower one and thirty-three pages is far more repositories than anyone scrolls.
let pageLimit = 33

// --- the cursor ---------------------------------------------------------------------------
// What the browser carries back to ask for the next page. It holds what this file needs to
// re-ask its OWN question — the text searched, and which page — and never a URL.
//
// Never a URL is the point rather than a detail: a cursor this session dereferenced would
// make the browser the one choosing what this session fetches, which is the shape of every
// server-side request forgery there has ever been. What comes back is read as two values and
// a URL is composed here, from `apiBase`, exactly as page one's was.
//
// Unsigned, deliberately, and safe for exactly one reason: it says nothing a browser could
// not have put in the query string itself. If a cursor ever carries something a caller is
// not otherwise entitled to ask for, it needs a signature and this comment is the warning.

/// `Text` is what was searched for, and NOTHING is "my repos" — not the empty string. The
/// two are different questions to different endpoints, so a cursor that spelled the absence
/// as `""` would be a cursor whose reader could not tell them apart.
type private Cursor = { Text : string option; Page : int }

/// A cursor as the browser carries it: this session's own JSON, base64url so it survives a
/// query string. `q` is spelled `null` where there was no search text, which is what the
/// reader tells apart from an empty one.
let private mintCursor (text: string option) (page: int) : string =
    // `null` rather than an absent key, which is what the JSON this replaced wrote and what
    // every cursor already in a browser's hands carries.
    let payload =
        createObj
            [ "q", (match text with Some searched -> box searched | None -> box null)
              "page", box page ]

    (buffer.Buffer.from (JS.JSON.stringify payload, BufferEncoding.Utf8)).toString base64url

let private fromBase64Url (token: string) : string =
    (buffer.Buffer.from (token, base64url)).toString BufferEncoding.Utf8

/// The token's JSON, or nothing when it is not base64 at all. Total on purpose: the browser
/// can send anything, and a token this session did not mint is not an error — it is a
/// request it will not honour.
let private cursorJson (token: string) : string option =
    try Some (fromBase64Url token) with _ -> None

let private cursorDecoder : Decoder<Cursor> =
    Decode.object (fun get ->
        { Text = get.Optional.Field "q" Decode.string
          Page = get.Required.Field "page" Decode.int })

/// The page a cursor asks for, and the text it asks within — or nothing, for a token that
/// is not one of ours or that names a page outside the offer. Page one is not addressable
/// by cursor: it is what a request with no cursor answers.
let readCursor (token: string) : (string option * int) option =
    match cursorJson token with
    | None -> None
    | Some json ->
        match Decode.fromString cursorDecoder json with
        | Ok cursor when cursor.Page >= 2 && cursor.Page <= pageLimit -> Some (cursor.Text, cursor.Page)
        | Ok _
        | Error _ -> None

/// The cursor for what follows this page, if anything does. A FULL page is the only evidence
/// there is more without reading GitHub's `Link` header, so a listing whose last page is
/// exactly full offers one more and that one comes back empty. The alternative — teaching
/// every read in this file to carry response headers — buys one avoided request at the end
/// of a scroll nobody reaches.
let nextCursor (text: string option) (page: int) (got: 'row list) : string option =
    if List.length got < pageSize || page >= pageLimit then None else Some (mintCursor text (page + 1))

/// The repositories a credential reaches, most recently pushed first. Every affiliation
/// GitHub knows — owned, collaborated on, and through an organisation — because "the
/// repos I work on" is all three and the default (`owner`) leaves out most of a working
/// developer's.
let recentOver (apiBase: string) (token: string option) (page: int) : Async<Result<RepoCandidate list, LookupFailure>> =
    match token with
    | None -> async { return Error NoCredential }
    | Some _ ->
        read
            listingDecoder
            (sprintf
                "%s/user/repos?sort=pushed&per_page=%d&page=%d&affiliation=owner,collaborator,organization_member"
                (apiBase.TrimEnd '/')
                pageSize
                page)
            token

/// Repositories whose name matches, anywhere on GitHub the credential can see — so a
/// public repo somebody else owns is one search away, without the session having to be
/// told its exact name. Anonymous works, and sees only what anyone can.
///
/// A whole `owner/name` is looked up rather than searched: GitHub's search does not read
/// a slash as an owner, so the one repo somebody typed in full is the one it would not
/// find. Looked up, it answers as one row under the name the provider calls it NOW — so
/// a stale name typed here shows its current one, and a name nobody can see shows
/// nothing, which is what a search says too.
/// A whole `owner/name` is ONE row however far a reader scrolls, so pages past the first
/// are the search's alone.
let searchOver (apiBase: string) (token: string option) (text: string) (page: int) : Async<Result<RepoCandidate list, LookupFailure>> =
    let query = text.Trim ()
    if query = "" then async { return Ok [] }
    else
        match RepoRef.create query with
        | Ok repo when page = 1 ->
            async {
                match! read candidateDecoder (sprintf "%s/repos/%s" (apiBase.TrimEnd '/') (RepoRef.value repo)) token with
                | Ok candidate -> return Ok [ candidate ]
                | Error NotFound -> return Ok []
                | Error failure -> return Error failure
            }
        | Ok _ -> async { return Ok [] }
        | Error _ ->
            read
                searchDecoder
                (sprintf
                    "%s/search/repositories?q=%s&per_page=%d&page=%d"
                    (apiBase.TrimEnd '/')
                    (Http.urlPart (query + " in:name"))
                    pageSize
                    page)
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

/// The branches a repository has, a page at a time — read the way the repo listing is, and
/// with the same cursor, since "which page" is the whole of what either needs to carry.
///
/// A repo-listing cursor replayed here is harmless and deliberately not guarded against: the
/// repository is in the PATH, so all a cursor can move is which page of it, which is what a
/// cursor is for.
let branchesOver (apiBase: string) (token: string option) (repo: RepoRef) (page: int) : Async<Result<string list, LookupFailure>> =
    read
        branchesDecoder
        (sprintf "%s/repos/%s/branches?per_page=%d&page=%d" (apiBase.TrimEnd '/') (RepoRef.value repo) pageSize page)
        token

/// Where a pull request comes from — the repository holding its head, and the branch — so
/// a link to one is enough to check it out. The one link that has to be asked about: a
/// repository link is already what `add_repo` takes and a branch link is that plus a
/// switch, but which fork a pull request's branch lives in only the provider knows.
let pullHeadOver (apiBase: string) (token: string option) (repo: RepoRef) (number: int) : Async<Result<PullHead, LookupFailure>> =
    read
        GitHubPrs.pullHeadDecoder
        (sprintf "%s/repos/%s/pulls/%d" (apiBase.TrimEnd '/') (RepoRef.value repo) number)
        token

// --- the browser-facing routes ------------------------------------------------------------
// Three reads a person makes while choosing: which repos, which branches of one, and where
// a pull request they pasted a link to comes from. Cookie-
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
let encodeListing (page: RepoPage) : string = Codec.toString Codec.repoPage page

let encodeBranches (page: BranchPage) : string = Codec.toString Codec.branchPage page

let encodePullHead (head: PullHead) : string = Codec.toString Codec.pullHead head

/// Which HTTP status a failure is said with. A missing credential and a dead one are both
/// 401 — the browser's answer to either is the sign-in panel — but with different words.
let private statusOf (failure: LookupFailure) : int =
    match failure with
    | NoCredential
    | Refused -> 401
    | NotFound -> 404
    | RateLimited -> 429
    // Both 502: a gateway that cannot reach the upstream and one whose upstream said
    // something it cannot pass on are the same answer to the browser. The words differ.
    | Unreadable _
    | Unreachable _ -> 502

/// Build the `/github/repos*` route handler. `tokenFor` is the session's own resolution of
/// an actor's GitHub credential — the one every repo verb spends — so this file holds no
/// notion of scopes or precedence.
let routes
    (auth: SessionAuth.Auth)
    (tokenFor: CredentialFor -> Async<string option>)
    (apiBase: string)
    (mount: string)
    : IncomingMessage -> ServerResponse -> bool =
    fun req res ->
        let path = req.url.Split('?').[0]
        match SessionRoute.parseUnder mount req.``method`` path with
        | Some GitHubRepos
        | Some (GitHubBranches _)
        | Some (GitHubPullHead _) as route ->
            match auth.IdentityOf req with
            | None -> respondText res 401 "unauthorized"
            | Some identity ->
                let actor = PeerAttribution.credential identity.Attribution
                Async.StartImmediate (
                    async {
                        let! token = tokenFor actor
                        match route with
                        | Some GitHubRepos ->
                            // What is asked for comes from the CURSOR when there is one, and
                            // from `q` only on the first page — so a page and the text it is
                            // a page of can never disagree, whatever the browser sends beside
                            // the cursor. A cursor this session did not mint is page one.
                            let text, page =
                                match queryParamOf req.url "page" |> Option.bind readCursor with
                                | Some (text, page) -> text, page
                                // A `?q=` with nothing in it is not a search for nothing, it
                                // is no search — the same question as no `?q=` at all.
                                | None ->
                                    queryParamOf req.url "q"
                                    |> Option.map (fun typed -> typed.Trim ())
                                    |> Option.filter (fun typed -> typed <> ""),
                                    1
                            let! answer =
                                match text with
                                | Some text -> searchOver apiBase token text page
                                | None -> recentOver apiBase token page
                            match answer with
                            | Ok candidates ->
                                respondJson
                                    res
                                    200
                                    (encodeListing
                                        { RepoPage.Candidates = candidates
                                          RepoPage.Next = nextCursor text page candidates })
                            | Error failure -> respondText res (statusOf failure) (LookupFailure.describe failure)
                        | Some (GitHubBranches (owner, name)) ->
                            match RepoRef.create (owner + "/" + name) with
                            | Error e -> respondText res 400 (sprintf "not a repo name: %s" e)
                            | Ok repo ->
                                let page =
                                    queryParamOf req.url "page"
                                    |> Option.bind readCursor
                                    |> Option.map snd
                                    |> Option.defaultValue 1
                                match! branchesOver apiBase token repo page with
                                | Ok branches ->
                                    respondJson
                                        res
                                        200
                                        (encodeBranches
                                            { BranchPage.Names = branches
                                              BranchPage.Next = nextCursor None page branches })
                                | Error failure -> respondText res (statusOf failure) (LookupFailure.describe failure)
                        | Some (GitHubPullHead (owner, name, number)) ->
                            match RepoRef.create (owner + "/" + name), Int32.TryParse number with
                            | Error e, _ -> respondText res 400 (sprintf "not a repo name: %s" e)
                            | Ok _, (false, _)
                            | Ok _, (true, 0) -> respondText res 400 (sprintf "not a pull request number: %s" number)
                            | Ok repo, (true, n) ->
                                match! pullHeadOver apiBase token repo n with
                                | Ok head -> respondJson res 200 (encodePullHead head)
                                | Error failure -> respondText res (statusOf failure) (LookupFailure.describe failure)
                        | _ -> ()
                    })
            true
        | Some _
        | None -> false
