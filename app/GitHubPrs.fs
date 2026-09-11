module Yession.Host.GitHubPrs

// Everything GitHub-specific about a session's pull requests, and nothing else — the
// `GitHubConnection.fs` precedent, for the same reason: the Manager brokers the credential
// and never learns which service it brokered, so a REST endpoint has no business above
// this file. What is left here is the endpoints and their JSON — the two a look reads
// (`fetchOver`, the whole of the `FetchPr` seam this side owns), and the two that open one
// (`openOver`: the list that keeps a repeated ask a question, then the create) — and the one
// field path a delivery names its repo at. The cadence, the ETag bookkeeping, the verbs and
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
    abstract reachable : bool
    abstract status : int
    abstract etag : string
    abstract reset : string
    /// `x-ratelimit-remaining` and `x-ratelimit-resource`, read from EVERY reply — a
    /// conditional request answering 304 costs nothing and still carries the counter, so a
    /// cadence that spends nothing keeps the reading current.
    abstract remaining : string
    abstract resource : string
    abstract body : string

/// One conditional GET, as GitHub wants it asked: a bearer token when there is one, the
/// versioned accept header, a user agent (GitHub refuses requests without one), and the
/// caller's ETag so an unchanged resource costs a 304 rather than a body.
[<Emit("""(function (url, token, etag) {
  const headers = { 'accept': 'application/vnd.github+json', 'user-agent': 'yession',
                    'x-github-api-version': '2022-11-28' }
  if (token) headers['authorization'] = 'Bearer ' + token
  if (etag) headers['if-none-match'] = etag
  return fetch(url, { headers })
    .then(async r => ({ reachable: true, status: r.status, etag: r.headers.get('etag') || '',
                        reset: r.headers.get('x-ratelimit-reset') || '',
                        remaining: r.headers.get('x-ratelimit-remaining') || '',
                        resource: r.headers.get('x-ratelimit-resource') || '', body: await r.text() }))
    .catch(e => ({ reachable: false, status: 0, etag: '', reset: '', remaining: '', resource: '',
                   body: String((e && e.message) || e) }))
})($0, $1, $2)""")>]
let private getConditional (url: string) (token: string) (etag: string) : JS.Promise<FetchReply> = jsNative

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
    if not reply.reachable || reply.resource <> "core" then Resilience.Unknown
    else
        match Int32.TryParse reply.remaining, Int64.TryParse reply.reset with
        | (true, remaining), (true, resetEpoch) ->
            Resilience.Seen (remaining, DateTimeOffset.FromUnixTimeSeconds resetEpoch)
        | _ -> Resilience.Unknown

let private failureOf (reply: FetchReply) : PrFetchFailure =
    if not reply.reachable then PrUnreachable reply.body
    elif reply.status = 401 then PrUnauthorized
    elif reply.status = 404 then PrNotFound
    // 403 and 429 are both how GitHub says "too many"; a 403 for any other reason
    // (scopes, a blocked App) is also not something a retry sooner would fix, so the
    // wait it implies is the safe reading either way.
    elif reply.status = 403 || reply.status = 429 then
        PrRateLimited (match Int32.TryParse reply.reset with | true, epoch -> Some epoch | _ -> None)
    else PrUnreachable (sprintf "github answered %d" reply.status)

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
                return PrFetchFailed (PrRateLimited (Some (int (until.ToUnixTimeSeconds ()))))
            | Resilience.Go ->
                let bearer = Option.toObj token
                let repo = RepoRef.value pr.Repo
                let succeeded (reply: FetchReply) = reply.reachable && reply.status >= 200 && reply.status < 300
                let notModified (reply: FetchReply) = reply.reachable && reply.status = 304
                let prUrl = sprintf "%s/repos/%s/pulls/%d" (apiBase.TrimEnd '/') repo pr.Number
                let! prReply = getConditional prUrl bearer etags.Pr |> Interop.awaitPromise
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
                        Decode.fromString prDecoder prReply.body
                        |> Result.map Some
                        |> Result.mapError (sprintf "unrecognised pull request reply: %s")
                    else Ok None
                match fields with
                | Error e -> return PrFetchFailed (PrUnreachable e)
                | Ok None when not (notModified prReply) -> return PrFetchFailed (failureOf prReply)
                | Ok None -> return PrUnchanged
                | Ok (Some fields) ->
                    let checksUrl =
                        sprintf
                            "%s/repos/%s/commits/%s/check-runs?per_page=100"
                            (apiBase.TrimEnd '/')
                            repo
                            fields.HeadSha
                    let! checksReply = getConditional checksUrl bearer etags.Checks |> Interop.awaitPromise
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
                                match Decode.fromString checkRunsDecoder checksReply.body with
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
                            { Pr = (if succeeded prReply then prReply.etag else etags.Pr)
                              Checks = (if succeeded checksReply then checksReply.etag else etags.Checks) }
                        return PrChanged (snapshot, nextEtags)
        }

// --- opening one -------------------------------------------------------------------------

/// `POST /repos/{o}/{r}/pulls`, with the headers a look sends plus a body. Its own macro
/// rather than a mode flag on the one above: the method, the body and the absent conditional
/// header are all of what the two requests differ by, and a flag would hide that in a branch.
[<Emit("""(function (url, token, payload) {
  const headers = { 'accept': 'application/vnd.github+json', 'content-type': 'application/json',
                    'user-agent': 'yession', 'x-github-api-version': '2022-11-28' }
  if (token) headers['authorization'] = 'Bearer ' + token
  return fetch(url, { method: 'POST', headers, body: payload })
    .then(async r => ({ reachable: true, status: r.status, etag: '',
                        reset: r.headers.get('x-ratelimit-reset') || '',
                        remaining: r.headers.get('x-ratelimit-remaining') || '',
                        resource: r.headers.get('x-ratelimit-resource') || '', body: await r.text() }))
    .catch(e => ({ reachable: false, status: 0, etag: '', reset: '', remaining: '', resource: '',
                   body: String((e && e.message) || e) }))
})($0, $1, $2)""")>]
let private postJson (url: string) (token: string) (payload: string) : JS.Promise<FetchReply> = jsNative

/// A value on its way into a query string. Branch names carry `/` and, on a fork's head, `:`.
[<Emit("encodeURIComponent($0)")>]
let private urlPart (value: string) : string = jsNative

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
                return PrOpenFailed (PrRateLimited (Some (int (until.ToUnixTimeSeconds ()))))
            | Resilience.Go ->
                let bearer = Option.toObj token
                let repo = RepoRef.value draft.Repo
                let root = apiBase.TrimEnd '/'
                let succeeded (reply: FetchReply) = reply.reachable && reply.status >= 200 && reply.status < 300
                let listUrl =
                    sprintf
                        "%s/repos/%s/pulls?state=open&head=%s&base=%s"
                        root
                        repo
                        (urlPart (headRef draft))
                        (urlPart draft.Base)
                let! listing = getConditional listUrl bearer "" |> Interop.awaitPromise
                spending.Learned (allowanceIn listing)
                if not (succeeded listing) then return PrOpenFailed (failureOf listing)
                else
                    match Decode.fromString openNumbersDecoder listing.body with
                    | Error e -> return PrOpenFailed (PrUnreachable (sprintf "unrecognised pull request list: %s" e))
                    | Ok (number :: _) ->
                        match PrRef.create draft.Repo number with
                        | Ok pr -> return PrAlreadyOpen pr
                        | Error e -> return PrOpenFailed (PrUnreachable e)
                    | Ok [] ->
                        let! created =
                            postJson (sprintf "%s/repos/%s/pulls" root repo) bearer (createBody draft)
                            |> Interop.awaitPromise
                        spending.Learned (allowanceIn created)
                        // A refusal is not a failure: GitHub read the draft and said no, which
                        // is an answer somebody can act on. Every other non-2xx is the same
                        // four facts a look classifies.
                        if created.reachable && created.status = 422 then
                            match Decode.fromString refusalOf created.body with
                            | Ok (Some said) -> return PrOpenRefused said
                            | _ -> return PrOpenRefused "it would not say why"
                        elif not (succeeded created) then return PrOpenFailed (failureOf created)
                        else
                            match Decode.fromString numberDecoder created.body with
                            | Error e ->
                                return PrOpenFailed (PrUnreachable (sprintf "unrecognised pull request reply: %s" e))
                            | Ok number ->
                                match PrRef.create draft.Repo number with
                                | Ok pr -> return PrOpened pr
                                | Error e -> return PrOpenFailed (PrUnreachable e)
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

// --- the agent tools: create_pr, watch_pr, unwatch_pr -----------------------------------
//
// The three GitHub-flavoured entries `AgentTools.fs`'s registry used to declare directly,
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

/// The three tools, built from a turn's capabilities exactly the way `AgentTools.fs`'s
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
