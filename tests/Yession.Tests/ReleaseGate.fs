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

open Fable.Core
open Fable.Core.JsInterop
open Fable.Pyxpecto
#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

/// Anchored at the repository root, not at the runner's working directory — the same reading
/// `DeclaredSetup` does, and for the same reason: the subject is a committed document.
let private gitToplevel () : string =
    let options = jsOptions<Node.ChildProcess.ExecOptions> (fun o -> o.encoding <- Some "utf8")
    unbox<string> (Node.Api.childProcess.execSync ("git rev-parse --show-toplevel", box options))

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
[<RequireQualifiedAccess>]
type private Job =
    { Needs : string list
      Condition : string option
      Serialised : Concurrency option }

/// A job's `concurrency`. The shorthand form is a bare group name, which means the default
/// `cancel-in-progress: false` — so both spellings decode to the same answer and a reader here
/// never has to know which was written.
and [<RequireQualifiedAccess>] private Concurrency =
    { Group : string
      CancelInProgress : bool }

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

let private job : Decoder<Job> =
    Decode.object (fun get ->
        { Job.Needs = get.Optional.Field "needs" needs |> Option.defaultValue []
          Condition = get.Optional.Field "if" Decode.string
          Serialised = get.Optional.Field "concurrency" concurrency })

let private jobs : Decoder<(string * Job) list> =
    Decode.object (fun get ->
        match get.Optional.Field "jobs" (Decode.keyValuePairs job) with
        | Some jobs -> jobs
        | None -> [])

/// A file this cannot read is a failure carrying the reason, never an empty list: an empty
/// list is what a workflow with no jobs looks like, and the two must not read the same.
let private jobsIn (text: string) : (string * Job) list =
    match Decode.fromString jobs (JS.JSON.stringify (Fable.Yaml.parse text)) with
    | Error reason -> failwithf "release.yml is not a document this file can read: %s" reason
    | Ok jobs -> jobs

let private releaseJobs () : (string * Job) list =
    match repoRoot () |> Option.bind (fun root -> try Some (TestFiles.read (root + "/.github/workflows/release.yml")) with _ -> None) with
    | Some text -> jobsIn text
    | None -> []

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
    ]
