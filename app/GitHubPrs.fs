module Yession.Host.GitHubPrs

// Everything GitHub-specific about a session's pull requests, and nothing else — the
// `GitHubConnection.fs` precedent, for the same reason: the Manager brokers the credential
// and never learns which service it brokered, so a REST endpoint has no business above
// this file. What is left here is the endpoints and their JSON — the two a look reads
// (`fetchOver`, the whole of the `FetchPr` seam this side owns), the two that open one
// (`openOver`: the list that keeps a repeated ask a question, then the create), the query
// and three mutations that merge one (`mergeOver`) and the two that take it back
// (`unmergeOver`) — and the one field path a delivery names its repo at. The cadence, the ETag bookkeeping, the verbs and
// the query are provider-neutral and live in `PrWatches.fs`; a second forge is a second copy
// of this file, not a second poller.

open System
open Fable.Core
open Yession.Domain
open Yession.Domain.Hooks
open Yession.Domain.Prs
open Yession.Host.PrWatches

#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

/// What the neutral watcher calls this provider when it has to say so in a sentence a
/// person reads — "github rejected this credential". Lower case, because it appears
/// mid-sentence far more often than it starts one.
let provider = "github"

// --- the provider's JSON, decoded ------------------------------------------------------

/// Everything the pull request resource itself contributes to a snapshot — which is all
/// of it but the checks rollup, whose endpoint is the other half of a look.
type PrFields =
    { State : PrState
      Title : string
      HeadSha : string
      Queued : bool
      Mergeable : bool option }

/// What `GET /repos/{o}/{r}/pulls/{n}` says, reduced to what a snapshot carries.
///
/// `merged` rather than `state` decides a merge: GitHub reports a merged pull request as
/// `state: "closed"` with `merged: true`, so reading state alone would file every merge
/// as a close — which is the one distinction the whole feature exists to draw.
let prDecoder : Decoder<PrFields> =
    Decode.object (fun get ->
        let merged = get.Optional.Field "merged" Decode.bool |> Option.defaultValue false
        let state = get.Required.Field "state" Decode.string
        { State =
            if merged then PrMerged
            elif state = "closed" then PrClosed
            else PrOpen
          Title = get.Required.Field "title" Decode.string
          HeadSha = get.Required.At [ "head"; "sha" ] Decode.string
          // `auto_merge` is an OBJECT when auto merge is armed and null when it is not, so
          // its presence is the whole fact and none of its contents are read. Decoded as
          // a raw value for exactly that reason: what is inside it (who armed it, which
          // method, what commit message) would date this decoder against a shape nobody
          // here depends on.
          Queued = get.Optional.Field "auto_merge" Decode.value |> Option.exists (fun v -> not (Decode.Helpers.isNullValue v))
          // Null until GitHub has computed it, which it does lazily. Carried for display
          // and never for a transition — see `PrSnapshot.Mergeable`.
          Mergeable = get.Optional.Field "mergeable" (Decode.option Decode.bool) |> Option.flatten })

/// The same resource, read for where the pull request comes FROM: `head.repo.full_name`
/// rather than the repository the link named, because a pull request from a fork has its
/// branch in the fork, and checking out the base repository at that branch name finds
/// nothing. `head.repo` is null when the fork has since been deleted — GitHub keeps the
/// pull request and loses its source — which is a head nobody can check out.
let pullHeadDecoder : Decoder<Yession.Domain.Repos.PullHead> =
    Decode.at [ "head"; "repo"; "full_name" ] Decode.string
    |> Decode.andThen (fun name ->
        match RepoRef.create name with
        | Error e -> Decode.fail (sprintf "github named a repository this session cannot: %s" e)
        | Ok repo ->
            Decode.at [ "head"; "ref" ] Decode.string
            |> Decode.map (fun branch -> { Yession.Domain.Repos.PullHead.Repo = repo; Yession.Domain.Repos.PullHead.Branch = branch }))

/// `GET /repos/{o}/{r}/commits/{sha}/check-runs` — each run's status and conclusion.
let checkRunsDecoder : Decoder<(string * string option) list> =
    Decode.field
        "check_runs"
        (Decode.list (
            Decode.object (fun get ->
                get.Required.Field "status" Decode.string,
                get.Optional.Field "conclusion" (Decode.option Decode.string) |> Option.flatten)))

/// Fold every check run on a commit into the one word a watcher acts on.
///
/// Pending wins over red, deliberately: a suite still running may yet turn the answer
/// around, and announcing red while jobs are in flight is how a watcher learns to
/// distrust the announcement. `skipped` and `neutral` count as green — they are how a
/// conditional job reports "not my turn", and a PR whose docs job skipped is not a PR
/// with a problem.
let rollupOf (runs: (string * string option) list) : ChecksRollup =
    let failed =
        [ "failure"; "timed_out"; "cancelled"; "action_required"; "startup_failure"; "stale" ]
    if List.isEmpty runs then ChecksNone
    elif runs |> List.exists (fun (status, _) -> status <> "completed") then ChecksPending
    elif runs |> List.exists (fun (_, conclusion) -> conclusion |> Option.exists (fun c -> List.contains c failed)) then
        ChecksRed
    else ChecksGreen

// --- the two conditional GETs -----------------------------------------------------------

type private FetchReply =
    { Reachable : bool
      Status : int
      Etag : string
      Reset : string
      /// `x-ratelimit-remaining` and `x-ratelimit-resource`, read from EVERY reply — a
      /// conditional request answering 304 costs nothing and still carries the counter, so a
      /// cadence that spends nothing keeps the reading current.
      Remaining : string
      Resource : string
      Body : string }

/// How every request in this file presents itself to GitHub: the versioned accept header, a
/// user agent (GitHub refuses requests without one), and a bearer token when there is one.
///
/// `GitHubRepos.fs` carries its own copy of these three, deliberately: each of these files
/// is the whole of one endpoint family and owns what it knows about the provider outright,
/// so a second forge is a second copy of a file rather than a shared GitHub layer that
/// neither of them owns.
let sentHeaders (token: string) : (string * string) list =
    [ yield "accept", "application/vnd.github+json"
      yield "user-agent", "yession"
      yield "x-github-api-version", "2022-11-28"
      if not (String.IsNullOrEmpty token) then yield "authorization", "Bearer " + token ]

/// What a look adds to those: the caller's ETag, so an unchanged resource costs a 304
/// rather than a body. Nothing is sent when there is no ETag to send — a first look has
/// none, and `if-none-match: ` would be a condition on the empty string.
let conditionalHeaders (token: string) (etag: string) : (string * string) list =
    [ yield! sentHeaders token
      if not (String.IsNullOrEmpty etag) then yield "if-none-match", etag ]

/// The reply as this file reads one, whichever request made it: the status, the four
/// headers a look acts on, and the body.
let private replyOf (attempt: Http.Attempt<string>) (etagOf: Fetch.Types.Response -> string) : FetchReply =
    match attempt with
    | Http.Answered (response, body) ->
        { Reachable = true
          Status = response.Status
          Etag = etagOf response
          Reset = Http.headerOf "x-ratelimit-reset" response
          Remaining = Http.headerOf "x-ratelimit-remaining" response
          Resource = Http.headerOf "x-ratelimit-resource" response
          Body = body }
    | Http.Unreachable reason ->
        { Reachable = false; Status = 0; Etag = ""; Reset = ""; Remaining = ""; Resource = ""; Body = reason }

/// One conditional GET.
let private getConditional (url: string) (token: string) (etag: string) : Async<FetchReply> =
    async {
        let! attempt = Http.text url [ Http.headers (conditionalHeaders token etag) ]
        return replyOf attempt (Http.headerOf "etag")
    }

/// What a reply said about the budget behind this credential.
///
/// Gated on `x-ratelimit-resource`: GitHub prices several buckets separately (`core`,
/// `search`, `graphql` and more), and only `core` governs the pull request and check-run
/// endpoints this file asks. A reply about another bucket is not news about this one, and
/// folding it in would be a ledger describing a budget nobody here spends.
///
/// Both numbers or neither, which is what `Allowance` requires: a remaining with no window
/// to wait for is a hold nobody can end.
let private allowanceIn (reply: FetchReply) : Resilience.Allowance =
    if not reply.Reachable || reply.Resource <> "core" then Resilience.Unknown
    else
        match Int32.TryParse reply.Remaining, Int64.TryParse reply.Reset with
        | (true, remaining), (true, resetEpoch) ->
            Resilience.Seen (remaining, DateTimeOffset.FromUnixTimeSeconds resetEpoch)
        | _ -> Resilience.Unknown

/// What a status GitHub answered with means for a look, and the one number that comes with
/// it — `x-ratelimit-reset`, which is the only thing a caller can do something with.
let failureAt (status: int) (reset: string) : PrFetchFailure =
    if status = 401 then PrUnauthorized
    elif status = 404 then PrNotFound
    // 403 and 429 are both how GitHub says "too many"; a 403 for any other reason
    // (scopes, a blocked App) is also not something a retry sooner would fix, so the
    // wait it implies is the safe reading either way.
    elif status = 403 || status = 429 then
        // `Int64`, as `allowanceIn` above already reads the same header: a unix second is
        // past `Int32.MaxValue` from January 2038, and an `Int32.TryParse` of one answers
        // `false` — so the window the provider named was dropped and the caller fell back
        // to a fixed wait, with nothing anywhere saying the reading had stopped working.
        PrRateLimited (match Int64.TryParse reset with | true, epoch -> Some epoch | _ -> None)
    else PrUnreachable (sprintf "github answered %d" status)

/// A reply that never arrived carries why in place of a body; everything else is a status.
let private failureOf (reply: FetchReply) : PrFetchFailure =
    if not reply.Reachable then PrUnreachable reply.Body else failureAt reply.Status reply.Reset

/// What a look may spend, and where what it learns is kept.
///
/// Both are functions rather than a ledger, because the ledger is one cell shared by
/// several callers with different rights: the poller asks as `Background` and a verb a
/// person is waiting on asks as `Foreground`, over the same reading. Partially applying the
/// class at the composition root is what lets a look hold no notion of either.
type Spending =
    { /// May a look go now, or is the budget down to what is held back?
      Permit : unit -> Resilience.Permit
      /// What a reply said, folded into whatever the ledger holds.
      Learned : Resilience.Allowance -> unit }

/// What background work leaves behind for everything else.
///
/// A GitHub budget belongs to a USER — 5,000 requests an hour, pooled across every app
/// acting on their behalf and every session holding their credential — so the poller and
/// the person draw on one number, and the poller draws on it every few seconds while nobody
/// watches. 250 is five percent of the hour, which is a hundred-odd looks: enough for the
/// verbs somebody is waiting on to keep working through a window the watches have spent,
/// and small enough that the watches get essentially all of it when nobody is asking.
let budget : Resilience.Limits = { Reserve = 250 }

module Spending =

    /// Never refuses and remembers nothing. What a suite takes, and the only honest shape
    /// for a caller with no ledger behind it.
    let unmetered : Spending = { Permit = (fun () -> Resilience.Go); Learned = ignore }

    /// One class of spend against one ledger. This is the partial application the design
    /// rests on: the LEDGER is made once at the composition root and the CLASS is fixed
    /// here, so what reaches a look is two functions and no state it could get wrong.
    let over (ledger: Resilience.Ledger) (now: unit -> DateTimeOffset) (spend: Resilience.Spend) : Spending =
        { Permit = fun () -> Resilience.Ledger.permit ledger (now ()) budget spend
          Learned = Resilience.Ledger.observed ledger }

/// The fetch as it is composed against a real API base. The base is a PARAMETER for the
/// reason `GitHubConnection.refusedAt` takes one: a suite needs somewhere to point it
/// that is not the live provider.
let fetchOver (apiBase: string) (spending: Spending) : FetchPr =
    fun token pr etags last ->
        async {
            match spending.Permit () with
            // Held back rather than refused, and reported as the hold it is: the watcher's
            // answer to both is the same — wait for the window the provider named — and the
            // poller already schedules around exactly this value. The difference is that
            // this one costs no request to discover.
            | Resilience.Hold until ->
                return PrFetchFailed (PrRateLimited (Some (until.ToUnixTimeSeconds ())))
            | Resilience.Go ->
                let bearer = Option.toObj token
                let repo = RepoRef.value pr.Repo
                let succeeded (reply: FetchReply) = reply.Reachable && reply.Status >= 200 && reply.Status < 300
                let notModified (reply: FetchReply) = reply.Reachable && reply.Status = 304
                let prUrl = sprintf "%s/repos/%s/pulls/%d" (apiBase.TrimEnd '/') repo pr.Number
                let! prReply = getConditional prUrl bearer etags.Pr
                spending.Learned (allowanceIn prReply)
                // The pull request's own fields, decoded when it answered with a body and
                // carried over from the last look when it answered 304.
                //
                // BOTH halves are always asked, and that is the point. The check runs on a
                // commit go queued -> in_progress -> completed without the pull request
                // resource moving at all, so returning early on its 304 is how a watch sits
                // on `pending` for the whole life of a build that has already gone green.
                // Asking twice is free in the only currency that binds: GitHub does not count
                // a 304 against the primary rate limit.
                let fields =
                    if notModified prReply then
                        // Nothing to carry means nothing to say. Unreachable in practice — an
                        // ETag only exists because a body came back once — but total here
                        // rather than a guess.
                        Ok (
                            last
                            |> Option.map (fun s ->
                                { State = s.State
                                  Title = s.Title
                                  HeadSha = s.HeadSha
                                  Queued = s.Queued
                                  Mergeable = s.Mergeable }))
                    elif succeeded prReply then
                        Decode.fromString prDecoder prReply.Body
                        |> Result.map Some
                        |> Result.mapError (sprintf "unrecognised pull request reply: %s")
                    else Ok None
                match fields with
                | Error e -> return PrFetchFailed (PrUnreadable e)
                | Ok None when not (notModified prReply) -> return PrFetchFailed (failureOf prReply)
                | Ok None -> return PrUnchanged
                | Ok (Some fields) ->
                    let checksUrl =
                        sprintf
                            "%s/repos/%s/commits/%s/check-runs?per_page=100"
                            (apiBase.TrimEnd '/')
                            repo
                            fields.HeadSha
                    let! checksReply = getConditional checksUrl bearer etags.Checks
                    spending.Learned (allowanceIn checksReply)
                    if notModified prReply && notModified checksReply then
                        // Both halves unchanged: there is nothing to fold and nothing to say.
                        return PrUnchanged
                    else
                        // A checks endpoint that says nothing readable does not fail the whole
                        // look — the pull request's own state is the more important half and
                        // is already in hand. On the SAME head sha the last rollup still
                        // stands (a 304 can only mean that, because the checks URL is keyed by
                        // the sha); on a new one it does not, and pending is the honest answer
                        // for a commit whose runs have not been read. Never `ChecksNone`,
                        // which would claim there are none.
                        let unread =
                            match last with
                            | Some s when s.HeadSha = fields.HeadSha -> s.Checks
                            | _ -> ChecksPending
                        let checks =
                            if succeeded checksReply then
                                match Decode.fromString checkRunsDecoder checksReply.Body with
                                | Ok runs -> rollupOf runs
                                | Error _ -> unread
                            else unread
                        let snapshot =
                            { State = fields.State
                              Title = fields.Title
                              HeadSha = fields.HeadSha
                              Checks = checks
                              Queued = fields.Queued
                              Mergeable = fields.Mergeable }
                        // An ETag is replaced only by a half that actually answered with one.
                        // A 304 carries back the ETag we sent, so keeping the old one says the
                        // same thing without depending on the provider echoing it.
                        let nextEtags =
                            { Pr = (if succeeded prReply then prReply.Etag else etags.Pr)
                              Checks = (if succeeded checksReply then checksReply.Etag else etags.Checks) }
                        return PrChanged (snapshot, nextEtags)
        }

// --- opening one -------------------------------------------------------------------------

/// `POST /repos/{o}/{r}/pulls`, with the headers a look sends plus a body. Its own verb
/// rather than a mode flag on the one above: the method, the body and the absent conditional
/// header are all of what the two requests differ by, and a flag would hide that in a branch.
///
/// No ETag comes back from it — a create has nothing to be conditional about — which is why
/// the reply's is read as "" rather than off the response.
let private postJson (url: string) (token: string) (payload: string) : Async<FetchReply> =
    async {
        let! attempt =
            Http.text
                url
                [ Fetch.Types.RequestProperties.Method Fetch.Types.HttpMethod.POST
                  Http.headers (("content-type", "application/json") :: sentHeaders token)
                  Fetch.Types.RequestProperties.Body (U3.Case3 payload) ]
        return replyOf attempt (fun _ -> "")
    }

// A value on its way into a query string goes through `Http.urlPart`. Branch names carry
// `/` and, on a fork's head, `:`.

/// How GitHub names the branch a pull request comes FROM: `owner:branch`, which is what the
/// list endpoint requires and what the create endpoint accepts. A head a caller already
/// qualified passes through untouched — that spelling is the provider's own, and qualifying
/// it twice would name an owner called `owner:branch`.
let headRef (draft: PrDraft) : string =
    if draft.Head.Contains ":" then draft.Head
    else sprintf "%s:%s" (RepoRef.owner draft.Repo) draft.Head

/// The draft as `POST /pulls` takes it. `draft` is sent whichever way it was answered — a
/// field GitHub defaults for us is a decision taken at the provider, and this verb has been
/// given the answer — while a description nobody wrote is a field that is not there, rather
/// than an empty one that reads as a description of nothing.
let createBody (draft: PrDraft) : string =
    Encode.object
        [ yield "title", Encode.string draft.Title
          yield "head", Encode.string (headRef draft)
          yield "base", Encode.string draft.Base
          match draft.Body with
          | Some said -> yield "body", Encode.string said
          | None -> ()
          yield "draft", Encode.bool draft.Draft ]
    |> Encode.toString 0

/// The number the create endpoint answers with — the whole of what this side needs from a
/// pull request it just made, because every other fact about one is what a look reports.
let private numberDecoder : Decoder<int> = Decode.field "number" Decode.int

/// `GET /pulls?state=open&head=…&base=…` — the numbers already open from that head onto that
/// base. At most one can be, but a list is what the endpoint returns and reading the first is
/// honest about that.
let private openNumbersDecoder : Decoder<int list> = Decode.list numberDecoder

/// What GitHub says when it has READ the draft and will not open it (422). The specific
/// reason lives in `errors[].message` — "No commits between master and topic" — and the
/// envelope's own `message` is "Validation Failed", which says nothing; so the errors win and
/// the envelope is the fallback. `None` when a reply says nothing readable at all, which is a
/// different answer from one that gave a reason and is reported as one.
let refusalOf : Decoder<string option> =
    Decode.object (fun get ->
        let envelope = get.Optional.Field "message" Decode.string
        let said =
            get.Optional.Field
                "errors"
                (Decode.list (Decode.object (fun each -> each.Optional.Field "message" Decode.string)))
            |> Option.defaultValue []
            |> List.choose id
            |> List.filter (fun m -> m <> "")
        if List.isEmpty said then envelope else Some (String.concat "; " said))

/// Opening one, composed against a real API base like the look above.
///
/// It ASKS FIRST, and that is the whole reason this is two requests. GitHub answers a second
/// pull request from the same head onto the same base with a 422 whose TEXT is the only thing
/// separating it from "no commits between them" — and a verb whose meaning turns on matching
/// somebody else's prose is a verb that breaks when they reword it. One cheap GET makes the
/// repeated ask a question with an answer (the number) instead of a refusal a caller parses.
let openOver (apiBase: string) (spending: Spending) : OpenPr =
    fun token draft ->
        async {
            match spending.Permit () with
            | Resilience.Hold until ->
                return PrOpenFailed (PrRateLimited (Some (until.ToUnixTimeSeconds ())))
            | Resilience.Go ->
                let bearer = Option.toObj token
                let repo = RepoRef.value draft.Repo
                let root = apiBase.TrimEnd '/'
                let succeeded (reply: FetchReply) = reply.Reachable && reply.Status >= 200 && reply.Status < 300
                let listUrl =
                    sprintf
                        "%s/repos/%s/pulls?state=open&head=%s&base=%s"
                        root
                        repo
                        (Http.urlPart (headRef draft))
                        (Http.urlPart draft.Base)
                let! listing = getConditional listUrl bearer ""
                spending.Learned (allowanceIn listing)
                if not (succeeded listing) then return PrOpenFailed (failureOf listing)
                else
                    match Decode.fromString openNumbersDecoder listing.Body with
                    | Error e -> return PrOpenFailed (PrUnreadable (sprintf "unrecognised pull request list: %s" e))
                    | Ok (number :: _) ->
                        match PrRef.create draft.Repo number with
                        | Ok pr -> return PrAlreadyOpen pr
                        | Error e -> return PrOpenFailed (PrUnreadable e)
                    | Ok [] ->
                        let! created =
                            postJson (sprintf "%s/repos/%s/pulls" root repo) bearer (createBody draft)
                        spending.Learned (allowanceIn created)
                        // A refusal is not a failure: GitHub read the draft and said no, which
                        // is an answer somebody can act on. Every other non-2xx is the same
                        // four facts a look classifies.
                        if created.Reachable && created.Status = 422 then
                            match Decode.fromString refusalOf created.Body with
                            | Ok (Some said) -> return PrOpenRefused said
                            | _ -> return PrOpenRefused "it would not say why"
                        elif not (succeeded created) then return PrOpenFailed (failureOf created)
                        else
                            match Decode.fromString numberDecoder created.Body with
                            | Error e ->
                                return PrOpenFailed (PrUnreadable (sprintf "unrecognised pull request reply: %s" e))
                            | Ok number ->
                                match PrRef.create draft.Repo number with
                                | Ok pr -> return PrOpened pr
                                | Error e -> return PrOpenFailed (PrUnreadable e)
        }

// --- merging one -------------------------------------------------------------------------
// GraphQL, because that is where GitHub keeps this: auto merge and the merge queue have no
// REST, and a merge that goes through the queue is a mutation on the pull request's node id.
// One query says where the pull request stands, then one of three mutations does what that
// standing calls for — which is the shape `gh pr merge` has, and for the same reason: "merge
// this" is one intent, and which mechanism carries it out is the provider's fact, not the
// caller's choice.

/// What a GraphQL request sends: the REST headers with the merge-info preview in place of
/// the versioned `accept`, which is what makes `mergeStateStatus` readable.
let private graphqlHeaders (token: string) : (string * string) list =
    sentHeaders token
    |> List.map (fun (name, value) ->
        if name = "accept" then name, "application/vnd.github.merge-info-preview+json" else name, value)

/// One GraphQL error as GitHub reports it: a `type` (`NOT_FOUND`, `UNPROCESSABLE`, …) when it
/// has one, and the sentence.
type private GraphqlError = { Type : string option; Message : string }

let private graphqlErrors : Decoder<GraphqlError list> =
    Decode.field
        "errors"
        (Decode.list (
            Decode.object (fun get ->
                { Type = get.Optional.Field "type" Decode.string
                  Message = get.Required.Field "message" Decode.string })))

/// `POST /graphql` with one document and its variables, and the `data` read by `decoder`.
///
/// A GraphQL reply is 200 whether or not it did anything, and says no in `errors` — so the
/// classification a REST status carries is read off the body here. A `NOT_FOUND` is the same
/// fact a REST 404 is (gone, or a credential that cannot see it); every other error is the
/// provider having READ the request and declined, which is an answer in its own words.
/// How a GraphQL request came to nothing: the provider read it and declined, in its words,
/// or the same four facts a REST failure carries. Its own shape rather than either verb's
/// outcome, because both the merge and its undoing ask the same way and each says no in its
/// own vocabulary.
type private GraphqlRefusal =
    | Declined of string
    | Failed of PrFetchFailure

let private askGraphql
    (root: string)
    (spending: Spending)
    (token: string)
    (document: string)
    (variables: (string * JsonValue) list)
    (decoder: Decoder<'a>)
    : Async<Result<'a, GraphqlRefusal>> =
    async {
        let payload =
            Encode.object [ "query", Encode.string document; "variables", Encode.object variables ]
            |> Encode.toString 0
        let! attempt =
            Http.text
                (root + "/graphql")
                [ Fetch.Types.RequestProperties.Method Fetch.Types.HttpMethod.POST
                  Http.headers (("content-type", "application/json") :: graphqlHeaders token)
                  Fetch.Types.RequestProperties.Body (U3.Case3 payload) ]
        let reply = replyOf attempt (fun _ -> "")
        spending.Learned (allowanceIn reply)
        if not (reply.Reachable && reply.Status >= 200 && reply.Status < 300) then
            return Error (Failed (failureOf reply))
        else
            match Decode.fromString graphqlErrors reply.Body with
            | Ok errors when not (List.isEmpty errors) ->
                if errors |> List.exists (fun e -> e.Type = Some "NOT_FOUND") then
                    return Error (Failed PrNotFound)
                else
                    return Error (Declined (errors |> List.map (fun e -> e.Message) |> String.concat "; "))
            | _ ->
                match Decode.fromString (Decode.field "data" decoder) reply.Body with
                | Ok value -> return Ok value
                | Error e -> return Error (Failed (PrUnreadable (sprintf "unrecognised graphql reply: %s" e)))
    }

/// Where a pull request stands, as far as merging it is concerned.
type private MergeStanding =
    { /// The node id every mutation names it by.
      Id : string
      Merged : bool
      Closed : bool
      /// `mergeStateStatus`: `CLEAN`, `BLOCKED`, `UNSTABLE`, … — whether it could go in now.
      MergeState : string
      /// The base branch has a merge queue, so "now" means "into the queue".
      QueueEnabled : bool
      AutoMergeArmed : bool
      InQueue : bool }

let private standingQuery =
    "query($owner:String!,$name:String!,$number:Int!){repository(owner:$owner,name:$name){pullRequest(number:$number){id state merged mergeStateStatus isMergeQueueEnabled autoMergeRequest{mergeMethod} mergeQueueEntry{id}}}}"

/// `data.repository.pullRequest`, null when the number names nothing this credential can see —
/// which GitHub also reports as a `NOT_FOUND` error, read first by `askGraphql`.
let private standingDecoder : Decoder<MergeStanding option> =
    Decode.at
        [ "repository"; "pullRequest" ]
        (Decode.option (
            Decode.object (fun get ->
                { Id = get.Required.Field "id" Decode.string
                  Merged = get.Required.Field "merged" Decode.bool
                  Closed = get.Required.Field "state" Decode.string = "CLOSED"
                  MergeState = get.Required.Field "mergeStateStatus" Decode.string
                  QueueEnabled = get.Required.Field "isMergeQueueEnabled" Decode.bool
                  AutoMergeArmed = get.Optional.Field "autoMergeRequest" Decode.value |> Option.exists (fun v -> not (Decode.Helpers.isNullValue v))
                  InQueue = get.Optional.Field "mergeQueueEntry" Decode.value |> Option.exists (fun v -> not (Decode.Helpers.isNullValue v)) })))

/// One pull request's standing, asked with `ask`: the query, and a null answer read as the
/// not-found it is.
let private standingOf
    (ask: string -> (string * JsonValue) list -> Decoder<MergeStanding option> -> Async<Result<MergeStanding option, GraphqlRefusal>>)
    (pr: PrRef)
    : Async<Result<MergeStanding, GraphqlRefusal>> =
    async {
        match!
            ask
                standingQuery
                [ "owner", Encode.string (RepoRef.owner pr.Repo)
                  "name", Encode.string (RepoRef.repo pr.Repo)
                  "number", Encode.int pr.Number ]
                standingDecoder
        with
        | Error refusal -> return Error refusal
        | Ok None -> return Error (Failed PrNotFound)
        | Ok (Some standing) -> return Ok standing
    }

/// A mutation's reply carries nothing this side reads but the absence of errors.
let private done' : Decoder<unit> = Decode.value |> Decode.map ignore

/// Could it go in right now? The three statuses `gh pr merge` treats as mergeable: clean,
/// clean but for hooks that have yet to run, and mergeable with a non-required status failing.
/// Everything else — `BLOCKED` by a review or a running check, `BEHIND`, `DIRTY`, `DRAFT`,
/// `UNKNOWN` while GitHub is still computing — is a pull request to ARM rather than merge.
let private mergeableNow (status: string) : bool =
    status = "CLEAN" || status = "HAS_HOOKS" || status = "UNSTABLE"

/// The enum GitHub's mutations take.
let private methodName (method: PrMergeMethod) : string =
    match method with
    | Squash -> "SQUASH"
    | MergeCommit -> "MERGE"
    | Rebase -> "REBASE"

let private enableAutoMerge =
    "mutation($id:ID!,$method:PullRequestMergeMethod!){enablePullRequestAutoMerge(input:{pullRequestId:$id,mergeMethod:$method}){clientMutationId}}"

let private enqueue = "mutation($id:ID!){enqueuePullRequest(input:{pullRequestId:$id}){clientMutationId}}"

let private mergeNow =
    "mutation($id:ID!,$method:PullRequestMergeMethod!){mergePullRequest(input:{pullRequestId:$id,mergeMethod:$method}){clientMutationId}}"

/// Merging one, composed against a real API base like the two above.
///
/// It ASKS FIRST, for `openOver`'s reason: GitHub answers a mutation on a pull request that
/// is already armed, already queued, or already merged with a 200 carrying an error whose
/// TEXT is the only thing that separates it from a real refusal — and a verb whose meaning
/// turns on somebody else's prose breaks when they reword it. One query makes a repeated ask
/// a question with the standing for an answer, and it is also what says WHICH mutation the
/// intent calls for.
let mergeOver (apiBase: string) (spending: Spending) : MergePr =
    fun token pr method ->
        async {
            match spending.Permit () with
            | Resilience.Hold until -> return PrMergeFailed (PrRateLimited (Some (until.ToUnixTimeSeconds ())))
            | Resilience.Go ->
                let bearer = Option.toObj token
                let root = apiBase.TrimEnd '/'
                let ask (document: string) (variables: (string * JsonValue) list) (decoder: Decoder<'a>) =
                    askGraphql root spending bearer document variables decoder
                let refused (refusal: GraphqlRefusal) =
                    match refusal with
                    | Declined said -> PrMergeRefused said
                    | Failed failure -> PrMergeFailed failure
                match! standingOf ask pr with
                | Error refusal -> return refused refusal
                | Ok standing ->
                    let withId = [ "id", Encode.string standing.Id ]
                    let withMethod = ("method", Encode.string (methodName method)) :: withId
                    let after (outcome: PrMergeOutcome) (mutated: Result<unit, GraphqlRefusal>) =
                        match mutated with
                        | Ok () -> outcome
                        | Error refusal -> refused refusal
                    if standing.Merged then return PrMergeUnneeded (pr, "merged")
                    // Closed is GitHub's own state, read a moment ago; asking it to merge one
                    // would cost a request to be told the same thing in its words.
                    elif standing.Closed then return PrMergeRefused "it is closed — reopen it first"
                    elif standing.InQueue then return PrMergeUnneeded (pr, "in the merge queue")
                    elif standing.AutoMergeArmed then return PrMergeUnneeded (pr, "armed to merge when its checks pass")
                    elif mergeableNow standing.MergeState && standing.QueueEnabled then
                        let! mutated = ask enqueue withId done'
                        return after (PrMergeQueued pr) mutated
                    elif mergeableNow standing.MergeState then
                        let! mutated = ask mergeNow withMethod done'
                        return after (PrMergedNow pr) mutated
                    else
                        let! mutated = ask enableAutoMerge withMethod done'
                        return after (PrMergeArmed pr) mutated
        }

let private disableAutoMerge =
    "mutation($id:ID!){disablePullRequestAutoMerge(input:{pullRequestId:$id}){clientMutationId}}"

let private dequeue = "mutation($id:ID!){dequeuePullRequest(input:{pullRequestId:$id}){clientMutationId}}"

/// Taking one back off its way in — the undoing of `mergeOver`, and it asks first for the
/// same reason: the standing says whether there is auto merge to disarm, a queue entry to
/// pull, or nothing to do. One in the queue AND armed (auto merge that has since enqueued)
/// is dequeued; GitHub drops the arming with the entry.
let unmergeOver (apiBase: string) (spending: Spending) : UnmergePr =
    fun token pr ->
        async {
            match spending.Permit () with
            | Resilience.Hold until -> return PrUnmergeFailed (PrRateLimited (Some (until.ToUnixTimeSeconds ())))
            | Resilience.Go ->
                let bearer = Option.toObj token
                let root = apiBase.TrimEnd '/'
                let ask (document: string) (variables: (string * JsonValue) list) (decoder: Decoder<'a>) =
                    askGraphql root spending bearer document variables decoder
                let refused (refusal: GraphqlRefusal) =
                    match refusal with
                    | Declined said -> PrUnmergeRefused said
                    | Failed failure -> PrUnmergeFailed failure
                match! standingOf ask pr with
                | Error refusal -> return refused refusal
                | Ok standing ->
                    let withId = [ "id", Encode.string standing.Id ]
                    let after (outcome: PrUnmergeOutcome) (mutated: Result<unit, GraphqlRefusal>) =
                        match mutated with
                        | Ok () -> outcome
                        | Error refusal -> refused refusal
                    // Merged is past undoing, and it is not a refusal: nothing was asked of
                    // GitHub, and the answer is the state.
                    if standing.Merged then return PrUnmergeUnneeded (pr, "merged")
                    elif standing.InQueue then
                        let! mutated = ask dequeue withId done'
                        return after (PrMergeDequeued pr) mutated
                    elif standing.AutoMergeArmed then
                        let! mutated = ask disableAutoMerge withId done'
                        return after (PrMergeDisarmed pr) mutated
                    else return PrUnmergeUnneeded (pr, "not on its way in")
        }

// --- the hook subscription -------------------------------------------------------------------
// Push, where the deployment can take it. A delivery does not tell this session anything —
// it tells it to LOOK, and the poll above is still what produces every fact. So this is an
// accelerator with no second code path behind it: where hooks are configured a transition
// lands in seconds, and where they are not the interval above is unchanged.

/// Where a delivery carries the repository it concerns.
///
/// THE one provider-shaped string this feature puts in front of the Manager — and it goes
/// there as DATA, inside a filter the Manager stores and compares without ever knowing what
/// it means. That is the whole trade: the Manager relays, this file knows.
let repoPath : FieldPath =
    match FieldPath.create "body.repository.full_name" with
    | Ok path -> path
    | Error e -> failwithf "github repo path: %s" e

/// What this session asks to be forwarded: deliveries naming this repo. Per REPO and not
/// per pull request, because that is what a delivery names — the poke is repo-wide and the
/// poller decides which of its watches moved.
let filterFor (repo: RepoRef) : DeliveryFilter = { Where = [ repoPath, RepoRef.value repo ] }

/// The hook subscriptions this session holds, one per watched repo.
type PrHooks =
    { /// Reconcile against the repos currently watched — at boot, and after every watch or
      /// unwatch. The same shape as `PrWatchers.Apply`, for the same reason: the log's
      /// watches are the one source of what is subscribed, so the two cannot drift.
      Apply : RepoRef list -> unit
      /// Which repo a delivery concerns, from the subscription it names.
      ///
      /// Read from THIS session's own records, never from the delivery. That is what makes
      /// "a delivery is a poke" true rather than aspirational: the body is never parsed, so
      /// there is nothing in it to be wrong about or to lie with.
      RepoOf : string -> RepoRef option }

module PrHooks =

    /// A session that subscribes to nothing — the composition default, and what a session
    /// with no control channel gets. Not an error state: polling is the mechanism.
    let none : PrHooks =
        { Apply = fun _ -> ()
          RepoOf = fun _ -> None }

/// Build the reconciler over the Manager's hook control leg.
///
/// Every failure here is logged and dropped, deliberately: a subscription that could not be
/// made costs latency and nothing else, because the poll still runs. Failing the watch over
/// it would make an optional accelerator a required dependency.
let hooks
    (subscribe: DeliveryFilter -> Async<Result<string, string>>)
    (unsubscribe: string -> Async<Result<bool, string>>)
    : PrHooks =

    // `None` is claimed-but-not-yet-acknowledged: the slot is taken synchronously so a
    // second reconcile arriving before the Manager answers cannot subscribe twice.
    let mutable held : (RepoRef * string option) list = []

    { Apply =
        fun repos ->
            let wanted = List.distinct repos
            for repo in wanted do
                if held |> List.exists (fun (r, _) -> r = repo) |> not then
                    held <- held @ [ repo, None ]
                    Async.StartImmediate (
                        async {
                            match! subscribe (filterFor repo) with
                            | Ok id ->
                                held <- held |> List.map (fun (r, current) -> if r = repo then r, Some id else r, current)
                            | Error e ->
                                eprintfn "hook subscription for %s failed, falling back to polling: %s" (RepoRef.value repo) e
                                held <- held |> List.filter (fun (r, _) -> r <> repo)
                        })
            for repo, id in held |> List.filter (fun (r, _) -> not (List.contains r wanted)) do
                held <- held |> List.filter (fun (r, _) -> r <> repo)
                match id with
                | Some subscriptionId ->
                    Async.StartImmediate (
                        async {
                            match! unsubscribe subscriptionId with
                            | Ok _ -> ()
                            | Error e -> eprintfn "dropping hook subscription for %s failed: %s" (RepoRef.value repo) e
                        })
                // Unwatched before the Manager answered: the id to drop does not exist yet,
                // so the subscription outlives the watch until the launch ends and the
                // Manager drops everything under its secret. Rare, and bounded by that.
                | None -> ()
      RepoOf =
        fun id ->
            held
            |> List.tryPick (fun (repo, current) -> if current = Some id then Some repo else None) }

// --- the agent tools: create_pr, merge_pr, unmerge_pr, watch_pr, unwatch_pr --------------
//
// The GitHub-flavoured entries `AgentTools.fs`'s registry used to declare directly,
// moved here for the reason this file's own header states: everything GitHub-specific
// about a session's pull requests belongs in exactly one place, and "auto merge armed" and
// "what a merge queue ejecting an entry looks like" are GitHub-specific sentences. The
// `pull_requests` query just above (`PrWatches.fs`) is the precedent this follows for a
// QUERY — declared by the file that knows the provider, merged in by a registry that does
// not. This is the same move for three COMMANDS: contributed through
// `AgentCapabilities.Repos.ProviderTools`, which `AgentTools.fs` merges into `yession`'s
// tool list without knowing this file, or GitHub, exists. Wire names do not move — still
// `mcp__yession__create_pr` and so on, because `yession` is the tool's namespace regardless
// of who wrote its prose. A GitLab adapter contributes its own list the same way, through
// the same field, and never touches this one.

open Yession.Domain.Agent
open Yession.Domain.Tools

/// A body that always answers, the way every tool in `AgentTools.fs` does: `Error` is
/// reserved for the call never happening (arguments that could not be read), not for a call
/// that ran and went badly.
let private ok (body: Async<string>) : Async<Result<ToolAnswer, string>> =
    async {
        let! text = body
        return Ok (ToolAnswer.text text)
    }

let private withRepo (raw: string) (inner: RepoRef -> Async<string>) : Async<string> =
    async {
        match RepoRef.create raw with
        | Error e -> return sprintf "not a repo name: %s" e
        | Ok repo -> return! inner repo
    }

let private watchPr (capabilities: AgentCapabilities) (raw: string) (number: int) : Async<string> =
    withRepo raw (fun repo ->
        async {
            match! capabilities.Repos.WatchPr repo number with
            | Ok outcome -> return AgentTools.renderCommandOutcome outcome
            | Error e -> return sprintf "could not watch that pull request: %s" e
        })

let private unwatchPr (capabilities: AgentCapabilities) (raw: string) (number: int) : Async<string> =
    withRepo raw (fun repo ->
        async {
            match! capabilities.Repos.UnwatchPr repo number with
            | Ok outcome -> return AgentTools.renderCommandOutcome outcome
            | Error e -> return sprintf "could not stop watching that pull request: %s" e
        })

let private createPr
    (capabilities: AgentCapabilities)
    (raw: string)
    (head: string)
    (onto: string)
    (title: string)
    (body: string option)
    (draft: bool)
    : Async<string> =
    withRepo raw (fun repo ->
        async {
            match PrDraft.create repo head onto title body draft with
            // A draft the domain refused never reaches the gate: nothing was proposed,
            // nobody was asked, and the sentence names the argument to fix.
            | Error e -> return e
            | Ok drafted ->
                match! capabilities.Repos.CreatePr drafted with
                | Ok outcome -> return AgentTools.renderCommandOutcome outcome
                | Error e -> return sprintf "could not open the pull request: %s" e
        })

let private mergePr (capabilities: AgentCapabilities) (raw: string) (number: int) (method: string) : Async<string> =
    withRepo raw (fun repo ->
        async {
            match PrMergeMethod.create method with
            // A method the domain refuses never reaches the gate, like a draft it refuses.
            | Error e -> return e
            | Ok method ->
                match! capabilities.Repos.MergePr repo number method with
                | Ok outcome -> return AgentTools.renderCommandOutcome outcome
                | Error e -> return sprintf "could not merge the pull request: %s" e
        })

let private unmergePr (capabilities: AgentCapabilities) (raw: string) (number: int) : Async<string> =
    withRepo raw (fun repo ->
        async {
            match! capabilities.Repos.UnmergePr repo number with
            | Ok outcome -> return AgentTools.renderCommandOutcome outcome
            | Error e -> return sprintf "could not take the pull request back: %s" e
        })

/// Reading a call's arguments, the way `AgentTools.fs`'s own `ToolArgs` does for every other
/// tool: every body reads its own JSON, so a decode that lived elsewhere would have to know
/// every tool's shape to do the same job.
let private readArgs (decoder: Decoder<'a>) (json: string) : Result<'a, string> =
    let json = if String.IsNullOrWhiteSpace json then "{}" else json
    match Decode.fromString decoder json with
    | Ok value -> Ok value
    | Error e -> Error (sprintf "could not read the arguments: %s" e)

/// `watch_pr`/`unwatch_pr`'s pair: which repo, and which pull request on it.
let private repoNumberArgs (json: string) : Result<string * int, string> =
    readArgs
        (Decode.object (fun get ->
            get.Required.Field "repo" Decode.string,
            get.Required.Field "number" Decode.int))
        json

/// `create_pr`'s six: which repo, the branch the work is on, the branch it is for, what to
/// call it, what to say about it, and whether it is a draft. The two branches are read as
/// they were written and turned into a draft by the domain, which is where the refusals
/// live (`PrDraft.create`).
let private prDraftArgs (json: string) : Result<string * string * string * string * string option * bool, string> =
    readArgs
        (Decode.object (fun get ->
            get.Required.Field "repo" Decode.string,
            get.Required.Field "head" Decode.string,
            get.Required.Field "base" Decode.string,
            get.Required.Field "title" Decode.string,
            get.Optional.Field "body" Decode.string |> Option.filter (fun s -> s <> ""),
            get.Optional.Field "draft" Decode.bool |> Option.defaultValue false))
        json

/// `merge_pr`'s three: the pair above, and how the commits should land — `squash` unless
/// said otherwise, for `PrMergeMethod.create`'s reason.
let private mergeArgs (json: string) : Result<string * int * string, string> =
    readArgs
        (Decode.object (fun get ->
            get.Required.Field "repo" Decode.string,
            get.Required.Field "number" Decode.int,
            get.Optional.Field "method" Decode.string |> Option.defaultValue "squash"))
        json

/// The four tools, built from a turn's capabilities exactly the way `AgentTools.fs`'s
/// `verbs` builds every other one — descriptor paired with body, so a tool cannot be
/// declared without being callable. Merged into the `yession` registry through
/// `AgentCapabilities.Repos.ProviderTools`.
let providerTools (capabilities: AgentCapabilities) : (ToolDescriptor * (string -> Async<Result<ToolAnswer, string>>)) list =
    let tool name description fields body : ToolDescriptor * (string -> Async<Result<ToolAnswer, string>>) =
        ToolDescriptor.create AgentTools.Namespace name description (ToolSchema.ofFields fields), body
    [ tool
          "create_pr"
          "Open a pull request on GitHub, from a branch that is already pushed. The commits have to be up there first — push from a terminal with execute_command; this opens the pull request and nothing else. It answers with the number, as `owner/repo#n`, which is what watch_pr takes: this session says nothing further about a pull request nobody watches. Opening one that is already open from the same branch onto the same base changes nothing and reports the one that exists, so calling it twice is safe. It spends the GitHub credential of whoever's turn this is, so a \"cannot see it\" on a repo that exists means their credential cannot reach that repo — say so rather than retrying; everyone in the session sees the pull request open in the timeline. What GitHub will not open it says why in its own words — no commits between the two branches, a head branch it cannot find — and that sentence is what comes back."
          [ ToolField.required "repo" "string" "owner/name"
            ToolField.required
                "head"
                "string"
                "the branch the work is on, e.g. \"claude/fix-the-thing\"; \"owner:branch\" for a branch on a fork"
            ToolField.required "base" "string" "the branch it is for, e.g. \"master\" — there is no default, name it"
            ToolField.required "title" "string" "the pull request title, e.g. \"fix: a closed terminal ends its block\""
            ToolField.optional "body" "string" "the description, in markdown; omit for none"
            ToolField.optional
                "draft"
                "boolean"
                "true to open it as a draft — on the record, and explicitly not asking for review yet" ]
          (fun args ->
              async {
                  match prDraftArgs args with
                  | Error e -> return Error e
                  | Ok (repo, head, onto, title, body, draft) ->
                      return! ok (createPr capabilities repo head onto title body draft)
              })
      tool
          "merge_pr"
          "Merge a pull request on GitHub, by whichever route its state allows: if its checks are still running it is set to merge automatically when they pass (auto merge, which watch_pr then reports as queued); if it is mergeable now and the base branch has a merge queue it goes into the queue; if it is mergeable now with no queue it is merged at once. The answer says which happened, and the session starts watching it (as watch_pr would) so the timeline says when it lands — or when a merge queue ejects it, which reads as stalled. One already armed, queued or merged is reported as such and nothing is changed, so calling it twice is safe. It spends the GitHub credential of whoever's turn this is: what lands on the base branch is theirs, and everyone in the session sees the act in the timeline. What GitHub will not do it says why in its own words — auto merge not allowed on the repository, a review still required, the method not allowed — and that sentence is what comes back."
          [ ToolField.required "repo" "string" "owner/name"
            ToolField.required "number" "integer" "the pull request number"
            ToolField.optional
                "method"
                "string"
                "how the commits land: \"squash\" (the default), \"merge\" for a merge commit, or \"rebase\"" ]
          (fun args ->
              async {
                  match mergeArgs args with
                  | Error e -> return Error e
                  | Ok (repo, number, method) -> return! ok (mergePr capabilities repo number method)
              })
      tool
          "unmerge_pr"
          "Take a pull request on GitHub back off its way in: if auto merge is armed it is disarmed, and if it sits in the merge queue it is pulled out. The undoing of merge_pr — and the only one there is, since what has merged has merged: one already merged, or never on its way in, is reported as such and nothing is changed. A watch on it then reports stalled, which is what it is. It spends the GitHub credential of whoever's turn this is, and everyone in the session sees the act in the timeline."
          [ ToolField.required "repo" "string" "owner/name"
            ToolField.required "number" "integer" "the pull request number" ]
          (fun args ->
              async {
                  match repoNumberArgs args with
                  | Error e -> return Error e
                  | Ok (repo, number) -> return! ok (unmergePr capabilities repo number)
              })
      tool
          "watch_pr"
          "Watch a pull request on GitHub. The session polls it and announces on the timeline when it merges, closes, reopens, when its checks pass or fail, and when auto merge is armed (queued) or stops being armed while it is still open (stalled — what a merge queue ejecting an entry looks like, which nothing else reports); the current state of every watched pull request is the pull_requests query. Reads it with the credential of whoever's turn this is, so a \"cannot see it\" on a pull request that exists means their GitHub credential cannot reach that repo. Watching one already watched reports its state and changes nothing."
          [ ToolField.required "repo" "string" "owner/name"
            ToolField.required "number" "integer" "the pull request number" ]
          (fun args ->
              async {
                  match repoNumberArgs args with
                  | Error e -> return Error e
                  | Ok (repo, number) -> return! ok (watchPr capabilities repo number)
              })
      tool
          "unwatch_pr"
          "Stop watching a pull request. The session stops polling it and says nothing further about it; everyone sees the stop in the timeline."
          [ ToolField.required "repo" "string" "owner/name"
            ToolField.required "number" "integer" "the pull request number" ]
          (fun args ->
              async {
                  match repoNumberArgs args with
                  | Error e -> return Error e
                  | Ok (repo, number) -> return! ok (unwatchPr capabilities repo number)
              }) ]
