module Yession.Tests.ReleaseGate

// What may publish a release: every packaging channel that built, and nothing less.
//
// `release.yml` runs on master and nowhere else, so nothing sees a hole in its gate until a
// release has already gone out through one. One had: the `release` job named only
// `package-npm` in its `needs` and its condition, so `package:nix` was free to fail without
// stopping anything. On v12.21.2-beta.21 the aarch64 leg died — the Fable compiler segfaulted
// on the arm runner — and the release published regardless, announcing a
// `nix profile install github:trinketworks/yession` that no arm64 consumer could perform.
//
// Both halves of that gate matter and the file itself is why. A `needs` alone does not gate
// here: the condition is written `!cancelled() && …` precisely so `bench` can be depended on
// for its artifact without being able to hold delivery up. So a channel named in `needs` and
// absent from the condition is a channel that still cannot stop a release, which is the state
// this file is here to refuse.
//
// Cheap tier, and a document read rather than a run, for the same reason `DeclaredSetup` is:
// the expensive proof is a master push, which is the one place it is too late to learn this.
//
// The gate in front of master is here too: the merge queue, which decides what reaches master
// at all. Its settings live on GitHub and not in this repository, so the one question it is
// asked (does it wait longer than the jobs it waits on?) needs `LiveGitHub` to read them. The
// arithmetic of that question is the cheap tier's, below, over the jobs read here.

open Fable.Core
open Fable.Core.JsInterop
open Fable.Pyxpecto
open Yession.Host
#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

/// Anchored at the repository root, not at the runner's working directory — the same reading
/// `DeclaredSetup` does, and for the same reason: the subject is a committed document.
let private gitToplevel () : string =
    Fable.NodeExtras.SyncChildProcesses.execSync "git rev-parse --show-toplevel" Fable.NodeExtras.SyncOptions.none

let private repoRoot () : string option =
    try
        match (gitToplevel ()).Trim () with
        | "" -> None
        | root -> Some root
    with _ -> None

/// One job, as far as this file has an opinion: what it waits for, and what it demands of
/// what it waited for.
///
/// `Condition` stays an `option`, because a job with no `if` and a job with an empty one are
/// different things to say about a gate, and the whole subject here is what the gate demands.
/// `Needs` does not need the same care: a job that waits for nothing genuinely waits for an
/// empty list of jobs, and nothing is lost by writing it as one.
/// `Serialised` is the job's own `concurrency`, and an `option` for the same reason `Condition`
/// is: a job with no group and a job with one are different things to say about a release.
/// `Bound` is its `timeout-minutes`. `None` is a job GitHub stops at six hours.
/// `Name` is its `name`, which is what its check is called when it sets one.
/// `Permissions` is its own `permissions` map, empty when it sets none and takes the workflow's.
/// `Steps` are its steps, as far as the rules below read them.
/// `Environment` is the name of the environment it deploys to, as written: a name or an expression.
[<RequireQualifiedAccess>]
type private Job =
    { Needs : string list
      Condition : string option
      Serialised : Concurrency option
      Calls : string option
      Bound : Bound option
      Name : string option
      Permissions : (string * string) list
      Steps : Step list
      Environment : string option }

/// A job's `concurrency`. The shorthand form is a bare group name, which means the default
/// `cancel-in-progress: false` — so both spellings decode to the same answer and a reader here
/// never has to know which was written.
and [<RequireQualifiedAccess>] private Concurrency =
    { Group : string
      CancelInProgress : bool }

/// A job's `timeout-minutes` as written. A number is minutes. A string is an expression GitHub
/// evaluates per matrix entry (verify.yml's `verify` is one), which no reading of the file can
/// turn into minutes, so it stays the text it is rather than a number guessed at.
and [<RequireQualifiedAccess>] private Bound =
    | Minutes of float
    | Evaluated of string

/// One step: the action it runs, and its `with:` inputs as the text GitHub hands the action.
/// YAML writes `skipPush: true` as a boolean and `${{ … }}` as a string, and an action reads both
/// as a string, so both decode to one.
and [<RequireQualifiedAccess>] private Step =
    { Uses : string option
      With : (string * string) list }

/// `needs:` is one job or a list of them, and the file writes it both ways.
let private needs : Decoder<string list> =
    Decode.oneOf
        [ Decode.string |> Decode.map List.singleton
          Decode.list Decode.string ]

let private concurrency : Decoder<Concurrency> =
    Decode.oneOf
        [ Decode.string |> Decode.map (fun group -> { Concurrency.Group = group; CancelInProgress = false })
          Decode.object (fun get ->
            { Concurrency.Group = get.Required.Field "group" Decode.string
              CancelInProgress =
                get.Optional.Field "cancel-in-progress" Decode.bool |> Option.defaultValue false }) ]

let private input : Decoder<string> =
    Decode.oneOf
        [ Decode.string
          Decode.bool |> Decode.map (fun value -> if value then "true" else "false")
          Decode.float |> Decode.map string ]

let private step : Decoder<Step> =
    Decode.object (fun get ->
        { Step.Uses = get.Optional.Field "uses" Decode.string
          With = get.Optional.Field "with" (Decode.keyValuePairs input) |> Option.defaultValue [] })

/// A `permissions` map, scope to level. The shorthand `read-all` / `write-all` is a string this
/// does not expand, and it fails the read rather than passing for a map that grants nothing.
let private permissions : Decoder<(string * string) list> = Decode.keyValuePairs Decode.string

let private job : Decoder<Job> =
    Decode.object (fun get ->
        { Job.Needs = get.Optional.Field "needs" needs |> Option.defaultValue []
          Condition = get.Optional.Field "if" Decode.string
          Serialised = get.Optional.Field "concurrency" concurrency
          Calls = get.Optional.Field "uses" Decode.string
          Bound =
            get.Optional.Field
                "timeout-minutes"
                (Decode.oneOf
                    [ Decode.float |> Decode.map Bound.Minutes
                      Decode.string |> Decode.map Bound.Evaluated ])
          Name = get.Optional.Field "name" Decode.string
          Permissions = get.Optional.Field "permissions" permissions |> Option.defaultValue []
          Steps = get.Optional.Field "steps" (Decode.list step) |> Option.defaultValue []
          Environment =
            get.Optional.Field
                "environment"
                (Decode.oneOf [ Decode.string; Decode.field "name" Decode.string ]) })

/// A workflow's own top-level `concurrency`, which is the one that decides whether a run of it can
/// be cancelled by the next one.
let private workflowConcurrency : Decoder<Concurrency option> =
    Decode.object (fun get -> get.Optional.Field "concurrency" concurrency)

let private jobs : Decoder<(string * Job) list> =
    Decode.object (fun get ->
        match get.Optional.Field "jobs" (Decode.keyValuePairs job) with
        | Some jobs -> jobs
        | None -> [])

/// A file this cannot read is a failure carrying the reason, never an empty list: an empty
/// list is what a workflow with no jobs looks like, and the two must not read the same.
let private jobsIn (file: string) (text: string) : (string * Job) list =
    match Decode.fromString jobs (Fable.Yaml.Plain.json (Fable.Yaml.parse text)) with
    | Error reason -> failwithf "%s is not a document this file can read: %s" file reason
    | Ok jobs -> jobs

/// One workflow's jobs, by its file name. A file this cannot find answers an empty list, and
/// each reader's population case is what speaks for that.
let private jobsOf (file: string) : (string * Job) list =
    match repoRoot () |> Option.bind (fun root -> try Some (TestFiles.read (root + "/.github/workflows/" + file)) with _ -> None) with
    | Some text -> jobsIn file text
    | None -> []

let private releaseJobs () : (string * Job) list = jobsOf "release.yml"

/// release.yml's own top-level `permissions`, which every job that sets none runs under.
let private releasePermissions () : (string * string) list =
    let top = Decode.object (fun get -> get.Optional.Field "permissions" permissions |> Option.defaultValue [])
    match repoRoot () |> Option.bind (fun root -> try Some (TestFiles.read (root + "/.github/workflows/release.yml")) with _ -> None) with
    | None -> []
    | Some text ->
        match Decode.fromString top (Fable.Yaml.Plain.json (Fable.Yaml.parse text)) with
        | Error reason -> failwithf "release.yml is not a document this file can read: %s" reason
        | Ok found -> found

/// The writes each release.yml job may hold, and why. A job not named here holds none.
let private allowedWrites =
    [ "release", [ "contents"; "id-token"; "attestations" ]   // the tag, the release, its provenance
      "bench", [ "contents" ] ]                                 // `bench-publish` pushes the `bench` branch

/// Each write a job holds that `allowedWrites` does not give it, as the message that says so.
let private unexpectedWrites (jobs: (string * Job) list) : string list =
    [ for id, job in jobs do
        let allowed = allowedWrites |> List.tryFind (fun (name, _) -> name = id) |> Option.map snd |> Option.defaultValue []
        for scope, level in job.Permissions do
            if level = "write" && not (List.contains scope allowed) then
                yield sprintf "release.yml's `%s` job holds `%s: write`, which nothing it does needs" id scope ]

/// `owner/repo@<40 hex>`, with an optional path after the repo: an action pinned to a commit.
/// A tag can be moved to other code after review; a commit cannot.
let private pinned (uses: string) =
    match uses.Split '@' with
    | [| _; ref |] -> ref.Length = 40 && ref |> Seq.forall (fun c -> System.Char.IsDigit c || (c >= 'a' && c <= 'f'))
    | _ -> false

/// Every workflow in the repository with its jobs, by file name. Its population is the first
/// case's: the same root and the same reading, so a root this cannot find is that case's red.
let private workflowJobs () : (string * (string * Job) list) list =
    match repoRoot () with
    | None -> []
    | Some root ->
        let dir = root + "/.github/workflows"
        TestFiles.entries dir
        |> List.filter (fun name -> name.EndsWith ".yml" || name.EndsWith ".yaml")
        |> List.sort
        |> List.map (fun name -> name, jobsIn name (TestFiles.read (dir + "/" + name)))

/// The workflows release.yml CALLS, as `.github/workflows/<name>` paths, derived from the file
/// rather than listed here — one added tomorrow is one the rule already covers. A `uses:` naming
/// another repository is not ours to read and is left out.
let private calledWorkflows (jobs: (string * Job) list) =
    jobs
    |> List.choose (fun (_, job) -> job.Calls)
    |> List.filter (fun path -> path.StartsWith "./")
    |> List.map (fun path -> path.Substring 2)
    |> List.distinct

/// A workflow's top-level concurrency, read from the repository. `None` for a file this cannot
/// find or parse, which the population case below is what speaks for.
let private concurrencyOf (path: string) : Concurrency option =
    match repoRoot () |> Option.bind (fun root -> try Some (TestFiles.read (root + "/" + path)) with _ -> None) with
    | None -> None
    | Some text ->
        match Decode.fromString workflowConcurrency (Fable.Yaml.Plain.json (Fable.Yaml.parse text)) with
        | Error reason -> failwithf "%s is not a document this file can read: %s" path reason
        | Ok found -> found

/// The job that publishes, which is the subject of half the rules below. A file this cannot read
/// answers `None` here exactly as it answers an empty list above, so the population case is what
/// speaks for both and no rule has to say it twice.
let private releaseJob () : Job option =
    releaseJobs () |> List.tryFind (fun (id, _) -> id = "release") |> Option.map snd

/// The packaging channels, derived from the file rather than listed here: a channel added
/// tomorrow is one this rule already covers, which a hand-kept list would not be.
let private channels (jobs: (string * Job) list) =
    jobs |> List.map fst |> List.filter (fun id -> id.StartsWith "package")

/// Does a condition demand that this job succeeded? Read with the spacing and the quote style
/// taken out of it, because this is a question about which jobs the gate demands and not about
/// how the line is typed. A job carrying no condition demands nothing, of anything.
let private demands (channel: string) (condition: string option) =
    match condition with
    | None -> false
    | Some written ->
        written.Replace(" ", "").Replace("\"", "'").Contains (sprintf "needs.%s.result=='success'" channel)

// --- the binary cache ----------------------------------------------------------------------
// Master's packages, every work sandbox and every agent container substitute from the yession
// cachix cache. A job that pushes there runs its checkout's own tasks.fsx, devenv.nix and
// nix/*.nix, so a pushing job on a branch could publish any output under a derivation master
// later substitutes. So only code master has merged pushes: release.yml, which runs on a master
// push alone, and the workflows it calls, when it is the one calling them.

let private cachix (step: Step) =
    step.Uses |> Option.exists (fun uses -> uses.StartsWith "cachix/cachix-action@")

let private inputOf (name: string) (step: Step) =
    step.With |> List.tryFind (fun (key, _) -> key = name) |> Option.map snd

let private shown (input: string option) =
    match input with
    | Some value -> sprintf "`%s`" value
    | None -> "unset"

/// What a called workflow's cachix step writes for "push only when a master push called me".
/// `github.event_name` in a called workflow is its caller's event, and release.yml's is `push`.
let private pushOnlyOnPush = "${{ github.event_name != 'push' }}"

/// Each cachix step in `file` that can push from code master has not merged, as the message
/// that says so. pr.yaml's run on `pull_request` and `merge_group` alone, so theirs never push
/// and carry no token. A workflow that something other than release.yml can run pushes only
/// when release.yml's push event ran it.
let private branchPushes (file: string) (jobs: (string * Job) list) : string list =
    [ for id, job in jobs do
        for step in job.Steps |> List.filter cachix do
            match file with
            | "release.yml" -> ()
            | "pr.yaml" ->
                if inputOf "skipPush" step <> Some "true" || (inputOf "authToken" step).IsSome then
                    yield
                        sprintf
                            "pr.yaml's `%s` job runs cachix-action with skipPush %s and authToken %s. A pull request runs its branch's own Nix and tasks.fsx, so its cachix step carries `skipPush: true` and no `authToken`; release.yml's jobs fill the cache after the merge"
                            id
                            (shown (inputOf "skipPush" step))
                            (if (inputOf "authToken" step).IsSome then "set" else "unset")
            | _ ->
                if (inputOf "authToken" step).IsSome && inputOf "skipPush" step <> Some "true" && inputOf "skipPush" step <> Some pushOnlyOnPush then
                    yield
                        sprintf
                            "%s's `%s` job runs cachix-action with a token and skipPush %s, so a pull request or a dispatch on a branch that runs it can push. Write `skipPush: %s`, so only release.yml's master push does"
                            file
                            id
                            (shown (inputOf "skipPush" step))
                            pushOnlyOnPush ]

/// The environment that holds the cache's push token. Its deployment branches are master alone, so
/// a branch cannot read the token, even from a workflow it edited.
let private cachePush = "cache-push"

/// Each job in `file` whose cachix step carries the token while it does not deploy to
/// `cache-push`, as the message that says so. Outside that environment the token is a repository
/// secret, which a same-repository pull request can read.
let private tokenOutsideEnvironment (file: string) (jobs: (string * Job) list) : string list =
    [ for id, job in jobs do
        let holdsToken = job.Steps |> List.exists (fun step -> cachix step && (inputOf "authToken" step).IsSome)
        let inEnvironment = job.Environment |> Option.exists (fun written -> written.Contains (sprintf "'%s'" cachePush) || written = cachePush)
        if holdsToken && not inEnvironment then
            yield
                sprintf
                    "%s's `%s` job pushes to the binary cache and declares no `environment: %s`, so the token it reads is a repository secret that any same-repository pull request can read too"
                    file
                    id
                    cachePush ]

// --- the merge queue ----------------------------------------------------------------------
// The queue in front of master gives an entry's required checks a fixed time to report
// (`check_response_timeout_minutes`) and drops the entry when one has not. It drops it
// SILENTLY: the pull request goes back to open with auto-merge off, and no failure anywhere
// says why. A job its own `timeout-minutes` stops, or a suite its budget stops, says what hung.
// So a required job that can run longer than the queue waits is a job whose hang is never
// named, because the queue gives up on it first. #1411 raised pr.yaml's `test` to 60 minutes
// while the queue still waited 60, which is how this came to be asked.

/// What master's merge queue waits for, in minutes, and the ruleset that says so.
[<RequireQualifiedAccess>]
type private Queue = { Waits : float; Ruleset : int }

/// One of master's active rules, as far as its merge queue is concerned.
[<RequireQualifiedAccess>]
type private Rule =
    | MergeQueue of Queue
    | RequiredChecks of string list
    | Other

/// A rule this has an opinion on must carry what the opinion reads, and a merge queue rule
/// with no timeout is a document this cannot read, never a queue that waits forever.
let private rule : Decoder<Rule> =
    Decode.field "type" Decode.string
    |> Decode.andThen (fun kind ->
        match kind with
        | "merge_queue" ->
            Decode.object (fun get ->
                Rule.MergeQueue
                    { Queue.Waits = get.Required.At [ "parameters"; "check_response_timeout_minutes" ] Decode.float
                      Ruleset = get.Required.Field "ruleset_id" Decode.int })
        | "required_status_checks" ->
            Decode.at [ "parameters"; "required_status_checks" ] (Decode.list (Decode.field "context" Decode.string))
            |> Decode.map Rule.RequiredChecks
        | _ -> Decode.succeed Rule.Other)

/// How late a required job's own clock can start after the queue's clock could have, in
/// minutes. The queue's clock starts no earlier than the merge-group commit the queue makes, so
/// this is measured from that commit (a merge_group run's `head_commit.timestamp`) to its `test`
/// job's `started_at`: the queue dispatching the run, and a runner taking it up. Over pr.yaml's
/// 50 merge_group runs up to 2026-10-16 that was 21 seconds at the median and 508 at worst,
/// where two groups' commits were made minutes before their runs were. This is that worst
/// rounded up to the next 10 minutes. Measure it again when the queue or the runners change:
/// each run's `head_commit.timestamp` from
/// `gh api "repos/trinketworks/yession/actions/runs?event=merge_group&per_page=50"`, against its
/// `test` job's `started_at` from `gh api repos/trinketworks/yession/actions/runs/<id>/jobs`.
let private lateStartMinutes = 10.0

/// Where GitHub stops a job that declares no `timeout-minutes`.
let private sixHours = 360.0

/// The check a job reports as: its `name` when it sets one, its id when it does not.
let private reportsAs (id: string) (job: Job) : string = job.Name |> Option.defaultValue id

/// Each required check the queue can drop before the job behind it says why, as the message
/// that says what to raise. Empty when the queue outlasts every one.
///
/// A required check that no job in `jobs` reports is one of them, never a pass. How long it can
/// run is unknown here, and a loop over nothing is how a rule like this one goes quiet.
let private droppedUnexplained (queue: Queue) (lateStart: float) (jobs: (string * Job) list) (required: string list) : string list =
    [ for check in required do
        match jobs |> List.tryFind (fun (id, job) -> reportsAs id job = check) with
        | None ->
            yield
                sprintf
                    "the merge queue waits for a required check called `%s`, and no pr.yaml job reports one, so how long it can run is unknown here. Name the pr.yaml job it is, or teach this case the workflow that runs it"
                    check
        | Some (id, job) ->
            let runs =
                match job.Bound with
                | Some (Bound.Minutes minutes) -> Ok (minutes, "its `timeout-minutes`")
                | None -> Ok (sixHours, "GitHub's own limit, as it declares no `timeout-minutes`")
                | Some (Bound.Evaluated expression) -> Error expression
            match runs with
            | Error expression ->
                yield
                    sprintf
                        "pr.yaml's `%s` job, a required check, bounds itself with `%s`, which is no number of minutes to hold the merge queue's %g against. Write its `timeout-minutes` as a number"
                        id
                        expression
                        queue.Waits
            | Ok (minutes, said) when queue.Waits <= minutes + lateStart ->
                yield
                    sprintf
                        "the merge queue drops an entry whose `%s` check has not reported within %g minutes, while pr.yaml's `%s` job may run %g minutes (%s) and start up to %g minutes after the queue formed its group. A hung `%s` therefore leaves the queue silently, with auto-merge off and no failure saying why, before its own timeout can name what hung. Raise the queue's limit above %g minutes: the repository's Settings, Rules, Rulesets, the ruleset holding the merge queue (https://github.com/trinketworks/yession/settings/rules/%d), Merge queue, \"Status check timeout\". It moves whenever the job's timeout does"
                        check
                        queue.Waits
                        id
                        minutes
                        said
                        lateStart
                        id
                        (minutes + lateStart)
                        queue.Ruleset
            | Ok _ -> () ]

/// The rules GitHub enforces on master, merged from every ruleset that targets it. Read access
/// is all this endpoint asks, which is all a run's own token has.
let private rulesUrl = "https://api.github.com/repos/trinketworks/yession/rules/branches/master"

/// Master's active rules, through `Http` like the product's own GitHub reads, so a sandbox's
/// proxy swaps in its real credential. Bounded, so a GitHub that never answers fails this case
/// by name rather than spending the whole suite's budget.
let private masterRules () : Async<Rule list> =
    async {
        // The token the run was given: `check` refused `LiveGitHub` without one GitHub accepts.
        let token = Interop.envOr "GITHUB_TOKEN" ""
        match! Http.text rulesUrl [ Http.headers (GitHubRepos.sentHeaders token); Http.deadline 10_000.0 ] with
        | Http.Unreachable reason -> return failwithf "GitHub did not answer %s: %s" rulesUrl reason
        | Http.Answered (response, body) when response.Status <> 200 ->
            return failwithf "GitHub answered %s with %d, so this run cannot read master's rules: %s" rulesUrl response.Status body
        | Http.Answered (_, body) ->
            match Decode.fromString (Decode.list rule) body with
            | Error reason -> return failwithf "master's rules are not a document this can read: %s" reason
            | Ok rules -> return rules
    }

let private queuesIn (rules: Rule list) : Queue list =
    rules |> List.choose (function Rule.MergeQueue queue -> Some queue | Rule.RequiredChecks _ | Rule.Other -> None)

let private requiredIn (rules: Rule list) : string list =
    rules |> List.collect (function Rule.RequiredChecks contexts -> contexts | Rule.MergeQueue _ | Rule.Other -> [])

let tests =
    testList "Release gate" [

        // The population, asserted first: every rule below is green over a file this cannot
        // find or has stopped understanding, and a workflow that obeys reads identically to
        // one nobody managed to read. `DeclaredSetup` learned that on its own first run.
        testCase "the release workflow still declares a release to gate and channels to gate it on" <| fun () ->
            let jobs = releaseJobs ()
            Expect.isTrue
                (jobs |> List.exists (fun (id, _) -> id = "release"))
                "release.yml declares a `release` job"
            Expect.isNonEmpty
                (channels jobs)
                "release.yml declares at least one packaging channel (a `package*` job)"

        // The promise: a release says every channel it ships was built. A channel that failed
        // has to be able to stop it — which here means named in `needs` AND demanded by the
        // condition, because the condition is deliberately `!cancelled()` and a `needs` on its
        // own decides nothing.
        testCase "a packaging channel that failed stops the release" <| fun () ->
            let jobs = releaseJobs ()
            match jobs |> List.tryFind (fun (id, _) -> id = "release") with
            | None -> ()   // the population case above is what says this
            | Some (_, release) ->
                for channel in channels jobs do
                    Expect.isTrue
                        (release.Needs |> List.contains channel)
                        (sprintf
                            "the release job does not wait for %s, so that channel cannot stop a release: needs %A"
                            channel
                            release.Needs)
                    Expect.isTrue
                        (demands channel release.Condition)
                        (sprintf
                            "the release job's condition does not require %s to have succeeded, and `!cancelled()` means waiting for it decides nothing: %s"
                            channel
                            (match release.Condition with
                             | Some written -> written
                             | None -> "it carries no condition at all"))

        // A version is claimed by exactly one commit, and creating the tag is the one step of a
        // release that two runs can race. The workflow used to serialise ENTIRELY — half an hour of
        // gate and packaging per push — on a rationale that turned out to describe a different bug
        // (`target_commitish`, #138, landing three pull requests after the lock). Taking that off
        // is only safe while the thing it was reaching for stays covered, so these two are what
        // keep it covered.
        //
        // Read off the JOB rather than the workflow deliberately. A group at the top would satisfy
        // a rule that only asked "is anything serialised?" while costing what the old one cost, and
        // the whole finding was that those are different things.
        testCase "the job that publishes a release is serialised" <| fun () ->
            match releaseJob () with
            | None -> ()   // the population case above is what says this
            | Some release ->
                Expect.isSome
                    release.Serialised
                    "the release job declares no `concurrency` group, so two overlapping runs can reach the tag cut at once. A version is claimed by one commit, and the steps that refuse an empty or an already-taken version are the second line of that, not the first."

        // Its own case, because its red means something entirely different from the one above: not
        // "two runs can tag at once" but "a run that has already built and packaged a commit can be
        // killed on the doorstep", which leaves that commit released by nothing at all. Cancelling
        // is the one thing the original lock was right to refuse.
        testCase "a release in flight is never cancelled by the next one" <| fun () ->
            match releaseJob () |> Option.bind (fun release -> release.Serialised) with
            | None -> ()   // the case above is what says this
            | Some serialised ->
                Expect.isFalse
                    serialised.CancelInProgress
                    (sprintf
                        "the release job's concurrency group `%s` cancels in progress, so a run that has already built and packaged a commit can be killed before it publishes it"
                        serialised.Group)

        // The one that cost a release. Both workflows this one calls grouped per `github.ref`,
        // which is `refs/heads/master` for EVERY push, with `cancel-in-progress: true` — so once
        // the workflow-level lock came off and two runs could overlap, the newer one cancelled the
        // older one's gate where it stood. Run 35603572519 had two of three tiers green and `nix`
        // still running when the next push arrived; `nix` died 51 seconds later, every job
        // downstream skipped, and 711cfa4 was never released.
        //
        // A group per COMMIT is what makes a called workflow safe to overlap, and naming the sha is
        // how a group says it is one. The rule asks that rather than asking for a literal spelling,
        // so `github.sha`, `github.event.pull_request.head.sha` and a run id all satisfy it — what
        // it refuses is a group that two different commits can land in while it cancels.
        testCase "a workflow this one calls cannot be cancelled by the next push" <| fun () ->
            let jobs = releaseJobs ()
            for path in calledWorkflows jobs do
                match concurrencyOf path with
                | None -> ()   // no group at all: nothing can cancel it
                | Some serialised ->
                    let perCommit =
                        [ "github.sha"; "head.sha"; "github.run_id" ]
                        |> List.exists serialised.Group.Contains
                    Expect.isTrue
                        (perCommit || not serialised.CancelInProgress)
                        (sprintf
                            "%s groups as `%s` and cancels in progress, so two pushes to one ref land in one group and the newer kills the older's run — which skips this release entirely. Put the commit in the group, or stop cancelling."
                            path
                            serialised.Group)

        // The population for the rule above, asserted separately so its silence cannot pass for a
        // verdict: a `uses:` spelling this cannot read, or a file it cannot find, would make that
        // loop run over nothing and report a clean gate.
        testCase "the release workflow still calls workflows this can read" <| fun () ->
            let called = calledWorkflows (releaseJobs ())
            Expect.isNonEmpty called "release.yml calls at least one workflow in this repository"
            for path in called do
                Expect.isTrue
                    (repoRoot () |> Option.map (fun root -> TestFiles.exists (root + "/" + path)) |> Option.defaultValue false)
                    (sprintf "release.yml names %s, which is not a file in this repository" path)

        // A job with no `timeout-minutes` runs until GitHub stops it, six hours in. The suites,
        // `lint`, the Nix build and the npm install carry their own budgets (`bounded` in tasks.fsx
        // kills an overrunning one and names it), but nothing else in a job does, so a hang while
        // installing Nix or devenv, in the build or in an upload held a runner until GitHub gave
        // up. Every workflow, not only this one: the bound is per job,
        // and a pull request's hang holds a runner as surely as a release's does.
        //
        // A job that CALLS a workflow is left out. GitHub refuses the key there, and the jobs of
        // the workflow it calls are read here from their own file. Which number is right is not
        // this case's to say; verify.yml's `verify` job says how each is measured. This asks only
        // that there is one.
        testCase "every workflow job that runs on a runner declares its own timeout" <| fun () ->
            let unbounded =
                [ for file, jobs in workflowJobs () do
                    for id, job in jobs do
                        if job.Calls.IsNone && job.Bound.IsNone then yield sprintf "%s:%s" file id ]
            Expect.isEmpty
                unbounded
                (sprintf
                    "these jobs declare no `timeout-minutes`: %s. A hang outside their steps' own budgets (the suites', `lint`'s, the Nix build's, the npm install's) holds such a runner for GitHub's six hours. Measure the job's slowest recent runs, then add the key above that plus the most budget one of those steps left unspent in them and a cold dev shell. The comment on verify.yml's `verify` job says why the bound sits above the steps' budgets"
                    (String.concat ", " unbounded))

        // The population for the case after it: a cachix step this cannot see, under a key it no
        // longer reads, would leave that loop running over nothing and calling the cache safe.
        testCase "pull requests still read the binary cache through steps this can read" <| fun () ->
            Expect.isTrue
                (jobsOf "pr.yaml" |> List.exists (fun (_, job) -> job.Steps |> List.exists cachix))
                "pr.yaml runs no cachix-action step this can find"

        testCase "only code master has merged pushes to the binary cache" <| fun () ->
            let pushes = [ for file, jobs in workflowJobs () do yield! branchPushes file jobs ]
            Expect.isEmpty pushes (String.concat ".\n" pushes)

        testCase "a job that pushes to the binary cache takes the token from the master-only environment" <| fun () ->
            let outside = [ for file, jobs in workflowJobs () do yield! tokenOutsideEnvironment file jobs ]
            Expect.isEmpty outside (String.concat ".\n" outside)
            let red =
                jobsIn
                    "a release.yml"
                    "jobs:\n  package:\n    steps:\n      - uses: cachix/cachix-action@0\n        with:\n          authToken: ${{ secrets.CACHIX_AUTH_TOKEN }}\n"
            Expect.isNonEmpty (tokenOutsideEnvironment "a release.yml" red) "a pushing job with no environment reads a repository secret"

        // The rule's red, over a pr.yaml written here: the step this file had until master
        // alone could push.
        testCase "a pull request's cachix step with a token is a branch that can push" <| fun () ->
            let jobs =
                jobsIn
                    "a pr.yaml"
                    "jobs:\n  test:\n    steps:\n      - uses: cachix/cachix-action@0\n        with:\n          name: yession\n          authToken: ${{ secrets.CACHIX_AUTH_TOKEN }}\n"
            Expect.isNonEmpty (branchPushes "pr.yaml" jobs) "a pull request's cachix step that holds the token can push"

        // A write is held only where it is spent. The package jobs build from the commit and run
        // what its dev shell fetched, so a write there is one any of that could use, and the
        // release's assets and tag are the thing it would forge.
        testCase "release.yml reads by default, and a job writes only what it publishes" <| fun () ->
            Expect.equal
                (releasePermissions () |> List.tryFind (fun (scope, _) -> scope = "contents") |> Option.map snd)
                (Some "read")
                "release.yml's top-level `permissions` gives every job `contents: read`; a job that writes says so on itself"
            Expect.isFalse
                (releasePermissions () |> List.exists (fun (_, level) -> level = "write"))
                "release.yml's top-level `permissions` grants no write; the job that needs one declares it"
            let writes = unexpectedWrites (releaseJobs ())
            Expect.isEmpty writes (String.concat ".\n" writes)
            Expect.isTrue
                (releaseJob () |> Option.exists (fun release -> List.contains ("contents", "write") release.Permissions))
                "the release job declares `contents: write`, which tagging and publishing need"

        // The rule's red: a package job that kept the write the whole workflow used to hold.
        testCase "a package job holding contents: write is a write nothing spends" <| fun () ->
            let jobs = jobsIn "a release.yml" "jobs:\n  package-npm:\n    permissions:\n      contents: write\n"
            Expect.isNonEmpty (unexpectedWrites jobs) "a package job's `contents: write` is reported"

        // What an operator checks a download against (docs/deployment.md, "Checking a release").
        testCase "the release attests its assets" <| fun () ->
            match releaseJob () with
            | None -> ()   // the population case above is what says this
            | Some release ->
                Expect.isTrue
                    (release.Steps
                     |> List.exists (fun step -> step.Uses |> Option.exists (fun uses -> uses.StartsWith "actions/attest-build-provenance@")))
                    "the release job runs actions/attest-build-provenance, so a published tarball carries signed provenance"

        // Every action, in every workflow: a tag is a name its owner can move to code nobody here
        // reviewed, and a release job runs with the write to forge what it publishes.
        testCase "every action a workflow runs is pinned to a commit" <| fun () ->
            let loose =
                [ for file, jobs in workflowJobs () do
                    for id, job in jobs do
                        for step in job.Steps do
                            match step.Uses with
                            | Some uses when not (uses.StartsWith "./") && not (pinned uses) -> yield sprintf "%s:%s runs %s" file id uses
                            | _ -> () ]
            Expect.isEmpty
                loose
                (sprintf "these actions are not pinned to a commit: %s. Write `owner/repo@<full sha> # <tag>`" (String.concat ", " loose))

        testCase "an action pinned to a tag is not pinned" <| fun () ->
            Expect.isFalse (pinned "actions/attest-build-provenance@v4") "a tag is a name its owner can move"
            Expect.isTrue (pinned "actions/attest-build-provenance@4d101475d8b20a2381f78447822ac1eab6504dd8") "a full commit sha is pinned"

        // The merge queue's arithmetic, over a pr.yaml written here. `queueTests` below holds the
        // real pr.yaml against the real queue; the numbers that started this are the red example.
        testCase "a merge queue that waits no longer than a required job runs drops it before it says why" <| fun () ->
            let jobs = jobsIn "a pr.yaml" "jobs:\n  test:\n    runs-on: ubuntu-latest\n    timeout-minutes: 60\n"
            Expect.isNonEmpty
                (droppedUnexplained { Queue.Waits = 60.0; Ruleset = 1 } 10.0 jobs [ "test" ])
                "a queue waiting 60 minutes in front of a 60-minute job that can start 10 minutes late gives up first"

        testCase "a merge queue that outlasts a required job and its late start waits for it to report" <| fun () ->
            let jobs = jobsIn "a pr.yaml" "jobs:\n  test:\n    runs-on: ubuntu-latest\n    timeout-minutes: 60\n"
            Expect.isEmpty
                (droppedUnexplained { Queue.Waits = 90.0; Ruleset = 1 } 10.0 jobs [ "test" ])
                "a queue waiting 90 minutes outlasts a 60-minute job that starts 10 minutes late"

        // Its own case, because the loop above it is otherwise free to run over nothing: a
        // required check renamed on one side and not the other compares no job at all.
        testCase "a required check no pr.yaml job reports is a reason, never a pass" <| fun () ->
            let jobs = jobsIn "a pr.yaml" "jobs:\n  test:\n    runs-on: ubuntu-latest\n    timeout-minutes: 60\n"
            Expect.isNonEmpty
                (droppedUnexplained { Queue.Waits = 90.0; Ruleset = 1 } 10.0 jobs [ "build" ])
                "a queue waiting on `build`, which no job reports, cannot be shown to outlast it"
    ]

/// The question above, asked of the queue GitHub runs in front of master and of the pr.yaml in
/// this checkout. `LiveGitHub`, because the queue's settings live on GitHub and nowhere here.
let queueTests =
    testList "The merge queue, for real" [

        // The population, first, for the reason the release gate's is: the case after it is
        // green over a queue this cannot find, and that reads the same as one that obeys.
        testCaseAsync "master merges through a queue that waits on a required check" <|
            async {
                let! rules = masterRules ()
                Expect.isNonEmpty
                    (queuesIn rules)
                    "master's active rules carry no `merge_queue` rule, so the case after this compares nothing. If the queue was turned off on purpose, delete both cases. Otherwise GitHub no longer answers the way this reads it"
                Expect.isNonEmpty
                    (requiredIn rules)
                    "master's active rules require no status check, so its merge queue waits on nothing and the case after this compares nothing"
            }

        testCaseAsync "the merge queue waits longer than any job it requires can run" <|
            async {
                let! rules = masterRules ()
                let dropped =
                    queuesIn rules
                    |> List.collect (fun queue -> droppedUnexplained queue lateStartMinutes (jobsOf "pr.yaml") (requiredIn rules))
                Expect.isEmpty dropped (String.concat ".\n" dropped)
            }
    ]
