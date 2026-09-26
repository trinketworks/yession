module Yession.Tests.ToolCalls

// A tool call, driven the way a TURN drives one, with no model in the loop.
//
// Everything between the model and the work has been testable for a while, one layer at a
// time: `Tools.fs` drives the registry over stub capabilities, `CommandGates.fs` drives the
// gate over a stub dispatch, `GitIntegration.fs` drives the repo service over local bare
// fixtures. Each of those is green while the CHAIN is broken, because the joins between them
// — the wire name, the encoded argument list, the rendered summary, and above all the wait —
// are exactly what none of them contains.
//
// This is that chain: the wire name a model would emit, through the registry the SDK adapter
// builds (`Agent.registryFor`), through the per-turn bindings a turn is given
// (`Commands.bindFor`), through the real command gate, into the real dispatch table
// (`Commands.dispatch`), and back out as the TEXT the model would read. Only the leaf — the
// service that touches git — is substituted, because what it does is somebody else's suite.
//
// What it caught on the first run is the reason it exists: an `add_repo` whose clone
// outlived a deadline meant for people answered "WAITING FOR A HUMAN TO APPROVE IT. It has
// NOT happened." — while it was happening, and while nobody had been asked anything. Every
// layer was individually right. The join was not.

open System
open Fable.Pyxpecto
open Yjs
open Yession.Domain
open Yession.Domain.Sandboxes
open Yession.Domain.Agent
open Yession.Domain.Content
open Yession.Domain.Artifacts
open Yession.Domain.Files
open Yession.Domain.Collab
open Yession.Domain.Prs
open Yession.Domain.Repos
open Yession.Host
open Yession.SessionProcess
open Yession.Tests.Support

let private expect result =
    match result with
    | Ok v -> v
    | Error e -> failwithf "invariant: %A" e

let private sessionId = SessionId.create "sess-tool-calls" |> expect
let private ada = Principal.User (UserId.create "ada" |> expect)

// --- the harness -------------------------------------------------------------------------

/// One session's tool surface, as an agent turn reaches it.
type ToolSession =
    { /// Call a tool by name with the JSON arguments a model would have written, and get
      /// back the text a model would have read. `Error` means the call never happened
      /// (no such tool, unreadable arguments) — the distinction the SDK carries as
      /// `isError`, kept here rather than flattened.
      Call : string -> string -> Async<Result<string, string>>
      /// The collaborative doc, for a test that needs to read what a peer would see.
      Doc : Y.Doc
      /// What the session recorded.
      Events : unit -> Async<SessionEvent list>
      /// Move the session's clock. The gate's deadline is measured against this, so a test
      /// crosses it without spending it, and without depending on how long anything took.
      /// Turning it fires the waiter's tick, which is how the gate notices a deadline — and
      /// an outcome nothing appended for — so it is turned once a waiter is on it.
      Advance : TimeSpan -> unit
      /// Resolve once something is waiting on the clock. A call started as a child reaches
      /// its wait a moment later than the case does its next line.
      Armed : unit -> Async<unit>
      /// A person's first repo, through the same gate a turn's `add_repo` goes through —
      /// composed the way `SessionMain` composes it, over the same gate as `Call`.
      Launch : Commands.LaunchRepo }

/// Compose a session's tool surface over the services it runs against. The composition is
/// the production one — the gate the Host builds, the dispatch table and per-turn bindings
/// `SessionMain` hands it, the registry the SDK adapter assembles — with nothing standing in
/// but the clock and whatever the caller substituted at the leaves.
let openToolSession (services: Commands.CommandServices) : ToolSession =
    let doc = Y.Doc.Create ()
    // The session's clock, turned by the test: the gate's deadline is measured on it and
    // observed on a tick that is its own, so crossing the deadline is one call.
    let clock = virtualClock (DateTimeOffset (2026, 1, 1, 0, 0, 0, TimeSpan.Zero))
    let log = InMemoryEventLog.create sessionId clock.Clock.Now

    // The Host's own change seam: one signal for an appended event and a doc update alike,
    // because a waiter does not care which happened — it re-reads and decides.
    let mutable listeners : Map<int, unit -> unit> = Map.empty
    let mutable nextListener = 0
    let subscribeToChanges (listener: unit -> unit) : unit -> unit =
        let id = nextListener
        nextListener <- nextListener + 1
        listeners <- Map.add id listener listeners
        fun () -> listeners <- Map.remove id listeners
    let notifyChanged () = listeners |> Map.iter (fun _ listener -> listener ())
    DocSync.onAnyUpdate doc notifyChanged

    let mutable minted = 0
    let mint (prefix: string) () =
        minted <- minted + 1
        sprintf "%s-%d" prefix minted

    let gate =
        CommandGates.create
            Classifier.approveAll
            (fun () -> Commands.dispatch services)
            (fun actor event ->
                async {
                    let! _ = log.Append actor event
                    notifyChanged ()
                    return ()
                })
            (fun () -> QueueId.create (mint "q" ()) |> expect)
            (fun () -> MessageId.create (mint "msg" ()) |> expect)
            clock.Clock
            subscribeToChanges

    // What the Host leaves as denials plus the one capability it owns here, then the
    // per-turn binding that turns the rest into commands — the same two steps, in the same
    // order, that a turn goes through.
    let capabilities =
        { AgentCapabilities.none with RunGated = gate.Run }
        |> Commands.bindFor services ada

    let registry = Agent.registryFor capabilities

    { Call =
        fun name args ->
            async {
                match! registry.Invoke { Namespace = AgentTools.Namespace; Name = name; Arguments = args } with
                | Ok answer -> return Ok answer.Text
                | Error reason -> return Error reason
            }
      Doc = doc
      Events =
        fun () ->
            async {
                let! page = log.Read None 1000
                return page.Events |> List.map (fun e -> e.Event)
            }
      Advance = clock.Advance
      Armed = clock.Armed
      Launch = Commands.launchRepo services gate.Run gate.Read }

/// A repo service that answers `add_repo` with whatever the test says, and refuses
/// everything else — the leaf substituted, and nothing above it.
let private reposAnswering (add: RepoRef -> Async<Result<RepoListing, string>>) : Repos.ReposService =
    let denied _ = async { return Error "not part of this test" }
    { AddRepo =
        fun _ repo ->
            async {
                let! added = add repo
                return added |> Result.map (fun listing -> { Repos.Recorded.Answer = listing; Repos.Recorded.RecordedAs = None })
            }
      ListRepos = fun () -> async { return Ok [] }
      SwitchBranch = fun _ _ _ _ -> async { return Error "not part of this test" }
      FetchRepo = fun _ _ -> async { return Error "not part of this test" }
      RepoStatus = denied
      RepoLog = denied
      RepoDiff = denied
      RemoveRepo = fun _ _ _ -> async { return Error "not part of this test" } }

/// A pull request service that answers `create_pr` with whatever the test says, and refuses
/// the rest — the leaf substituted, like the repo service above it.
let private prsOpening (create: PrDraft -> Async<Result<string, string>>) : PrWatches.PrService =
    { Watch = fun _ _ -> async { return Error "not part of this test" }
      Unwatch = fun _ _ -> async { return Error "not part of this test" }
      Create = fun _ draft -> create draft
      Merge = fun _ _ _ -> async { return Error "not part of this test" }
      Unmerge = fun _ _ -> async { return Error "not part of this test" }
      Ready = fun _ _ -> async { return Error "not part of this test" }
      Draft = fun _ _ -> async { return Error "not part of this test" }
      List = fun _ _ _ -> async { return Error "not part of this test" } }

let private servicesOver (service: Repos.ReposService) : Commands.CommandServices =
    { Repos = fun () -> Some service
      Sandboxes = fun () -> WorkSandboxes.unavailable
      WorkCheckout =
        fun repo _declared ->
            { InSandbox = "/repos/" + RepoRef.relativePath repo
              OnHost = "/data/repos/" + RepoRef.relativePath repo }
      DeclaredSandboxes = fun () -> []
      Terminals = fun () -> SessionTerminals.unavailable
      Files = fun () -> SessionFiles.unavailable
      Artifacts = fun () -> Artifacts.unavailable
      RunCommand = fun () -> TerminalCommands.unavailable
      Prs = fun () -> None
      Invalidate = ignore
      NoteSetup = fun _ _ _ _ -> async { return () }
      Refold = fun _ _ -> async { return () } }

/// The same, with a shell profile already set for the default sandbox — a session where
/// somebody has already said where terminals start.
let private servicesProfiledOver (cwd: string) (service: Repos.ReposService) : Commands.CommandServices =
    let profile : ShellProfileProjection =
        { Profiles = Map.ofList [ SandboxRef.defaultRef, { WorkingDirectory = Some cwd } ] }
    { servicesOver service with
        Terminals = fun () -> { SessionTerminals.unavailable with Profiles = fun () -> profile } }

/// A session whose `add_repo` succeeds at once.
let private cloningAt (branch: string) =
    openToolSession (
        servicesOver (
            reposAnswering (fun repo ->
                async { return Ok { Repo = repo; Branch = branch; Dirty = false; Path = "/repos/octo/hello" } })))

/// A session whose `add_repo` does not come back until the test says so — a clone in
/// progress, which is what every first `add_repo` is for its first several seconds.
let private slowlyCloning () : ToolSession * (unit -> unit) =
    let mutable finish : unit -> unit = ignore
    let cloning = Async.FromContinuations (fun (cont, _, _) -> finish <- fun () -> cont ())
    let session =
        openToolSession (
            servicesOver (
                reposAnswering (fun repo ->
                    async {
                        do! cloning
                        return Ok { Repo = repo; Branch = "main"; Dirty = false; Path = "/repos/octo/hello" }
                    })))
    session, fun () -> finish ()

let private addRepo (session: ToolSession) (repo: string) =
    session.Call "add_repo" (sprintf """{"repo":"%s"}""" repo)

let private answered (result: Result<string, string>) : string =
    match result with
    | Ok text -> text
    | Error reason -> failwithf "the call did not happen: %s" reason

/// A registry that reports one named sandbox, and says whether this ask is what started it
/// — the distinction a declared `setup:` turns on. `spec` is what the sandbox was asked to
/// be, so a test can give it a setup command and see what becomes of it.
let private registryReporting (outcome: WorkSandboxes.RunningSandbox -> WorkSandboxes.SandboxOutcome) (spec: EnvironmentSpec) =
    { WorkSandboxes.unavailable with
        Ensure =
            fun _ _ name _ ->
                async {
                    return
                        Ok (
                            outcome
                                { Ref = name
                                  Backend = "srt"
                                  Request = { SandboxRequest.defaults with Spec = spec }
                                  StartedBy = None
                                  StartedAt = None
                                  Environment = SessionEnvironment.unavailable })
                } }

/// A session whose one checkout declares `octo/hello:dev` — a container, a setup, a
/// credential — with the request the sandbox manager is finally asked for handed to `record`.
///
/// The declaration is what the FOLD read; the point of the tests below is that a start which
/// names this sandbox comes up as this says, and the request is the only place that shows.
let private declaringDev (record: SandboxRequest -> unit) : Commands.CommandServices =
    let dev = SandboxRef.parse "octo/hello:dev" |> expect
    let declared : SandboxDecl =
        { SandboxDecl.empty with
            Container = Some { ContainerSpec.defaults with Image = Some { Name = "ghcr.io/octo/dev"; Tag = Some "3" } }
            Setup = Some "make deps"
            Forward = [ "github" ] }
    { servicesOver (reposAnswering (fun repo -> async { return Ok { Repo = repo; Branch = "main"; Dirty = false; Path = "/repos" } })) with
        DeclaredSandboxes = fun () -> [ dev, declared ]
        Sandboxes =
            fun () ->
                { WorkSandboxes.unavailable with
                    Ensure =
                        fun _ _ name request ->
                            async {
                                record request
                                return
                                    Ok (
                                        WorkSandboxes.SandboxStarted
                                            { Ref = name
                                              Backend = "docker"
                                              Request = request
                                              StartedBy = None
                                              StartedAt = None
                                              Environment = SessionEnvironment.unavailable })
                            } } }

/// Services whose queued commands land in `seen` instead of a terminal.
let private servicesQueueing (seen: ResizeArray<CommandRequest>) (sandboxes: WorkSandboxes.WorkSandboxes) =
    { servicesOver (reposAnswering (fun repo -> async { return Ok { Repo = repo; Branch = "main"; Dirty = false; Path = "/repos" } })) with
        Sandboxes = fun () -> sandboxes
        RunCommand =
            fun () ->
                { TerminalCommands.unavailable with
                    Execute =
                        fun request _ ->
                            async {
                                seen.Add request
                                return
                                    Ok
                                        { Terminal = TerminalId.create "setup-term" |> expect
                                          Handle = QueueId.create "setup-queue" |> expect
                                          Block = None
                                          Status = TerminalCommandRunning
                                          Output = ""
                                          Kept = OutputEnd.Whole
                                          Elided = 0
                                          From = None }
                            } } }

let private declaring (setup: string option) : EnvironmentSpec =
    { EnvironmentSpec.defaults with Setup = setup }

let private startSandbox (session: ToolSession) =
    session.Call "start_work_sandbox" """{"name":"dev"}"""

let private tests' =
    testList "A tool call, end to end" [

        // The whole chain in one case: the arguments a model writes are decoded by the
        // registry, encoded by the per-turn binding, carried through the gate, decoded by the
        // dispatch table and handed to the service — and what comes back is what the model
        // reads. Every join in that sentence is a place a rename goes unnoticed.
        testCaseAsync "an ungated command runs inside the call, and answers with what it did" <|
            async {
                let session = cloningAt "main"
                let! answer = addRepo session "octo/hello"
                let text = answered answer
                Expect.stringContains text "added octo/hello" "the service's own words came back"
                Expect.isFalse (text.Contains "WAITING") "nobody was waiting on anything"
            }

        // A sandbox the add brings up points back to the add, so the fold after it is told
        // which item the add recorded, and for which repo.
        testCaseAsync "add_repo refolds with the item it recorded" <|
            async {
                let hello = RepoRef.create "octo/hello" |> expect
                let added = MessageId.create "m-added" |> expect
                let folds = ResizeArray<FoldCause> ()
                let repos =
                    { reposAnswering (fun _ -> async { return Error "not this one" }) with
                        AddRepo =
                            fun _ repo ->
                                async {
                                    return
                                        Ok
                                            { Repos.Recorded.Answer = { Repo = repo; Branch = "main"; Dirty = false; Path = "/repos/octo/hello" }
                                              Repos.Recorded.RecordedAs = Some added }
                                } }
                let session =
                    openToolSession { servicesOver repos with Refold = fun cause _ -> async { folds.Add cause } }
                let! _ = addRepo session "octo/hello"
                Expect.equal (List.ofSeq folds) [ FoldCause.Changed (hello, Some added) ] "the fold knows what the add recorded"
            }

        // The answer has to carry the one fact the next step needs. "added octo/hello" on
        // its own leaves the path to be guessed, and a guess is spent as a TERMINAL COMMAND
        // — `ls ~/repos`, and the next guess after that — off a turn that has a ceiling.
        testCaseAsync "the answer names the path a terminal reaches the checkout at" <|
            async {
                let session = cloningAt "main"
                let! answer = addRepo session "octo/hello"
                Expect.stringContains (answered answer) "/repos/octo/hello" "where to cd, not just what was cloned"
            }

        // Two paths for one checkout now arrive in a single answer — this verb's, and the
        // sandbox's, appended by the seam — so which view this one is has to be said. It was
        // not, for one build: an agent read the first path, pointed a dev sandbox's profile at
        // it, and spent two calls undoing that.
        testCaseAsync "the answer says which view the path it named is" <|
            async {
                let session = cloningAt "main"
                let! answer = addRepo session "octo/hello"
                Expect.stringContains (answered answer) "DEFAULT sandbox" "the path is labelled, not left to be guessed at"
            }

        // The two cases that used to sit here pinned `add_repo` telling an agent to call
        // `set_shell_profile`, and stopping once somebody had. That advice is gone: it named
        // the DEFAULT sandbox's path, so an agent that followed it into a container the repo
        // declared pointed a profile somewhere that does not exist there, and spent six calls
        // finding out. Where the work is belongs to the sandbox that mounted it — pinned now
        // by "a repo's sandbox says where it sees the checkout" and "the note a start writes
        // says where the checkout is" — and what to do about it belongs to the repo, in its
        // `description:`. Removing them here rather than loosening them: the second asserted
        // the advice was ABSENT once a profile existed, and with no advice at all it could
        // never have failed again.

        // create_pr crosses the gate as SIX encoded strings and is re-assembled on the far
        // side. Nothing but their order joins the two halves, and every one of them is a
        // string: a pair swapped in the encoding would compile, pass every layer's own suite,
        // and open a real pull request the wrong way round.
        testCaseAsync "every argument of a create_pr survives the gate in the place it was written" <|
            async {
                let mutable seen : PrDraft option = None
                let session =
                    openToolSession (
                        { servicesOver (reposAnswering (fun _ -> async { return Error "not part of this test" })) with
                            Prs =
                              fun () ->
                                Some (
                                    prsOpening (fun draft ->
                                        async {
                                            seen <- Some draft
                                            return Ok (sprintf "opened %s#7" (RepoRef.value draft.Repo))
                                        })) })
                let! answer =
                    session.Call
                        "create_pr"
                        """{"repo":"octo/hello","head":"topic","base":"master","title":"Add feature","body":"why","draft":true}"""
                Expect.equal (seen |> Option.map (fun d -> d.Head)) (Some "topic") "the head is the head"
                Expect.equal (seen |> Option.map (fun d -> d.Base)) (Some "master") "the base is the base"
                Expect.equal (seen |> Option.map (fun d -> d.Title)) (Some "Add feature") "the title is the title"
                Expect.equal (seen |> Option.map (fun d -> d.Body)) (Some (Some "why")) "the body is the body"
                Expect.equal (seen |> Option.map (fun d -> d.Draft)) (Some true) "and the flag is the flag"
                Expect.stringContains (answered answer) "opened octo/hello#7" "and the service's own words came back"
            }

        // merge_pr crosses the gate as THREE encoded strings, and the third — the method —
        // is what decides the shape of the history the merge leaves behind.
        testCaseAsync "every argument of a merge_pr survives the gate in the place it was written" <|
            async {
                let mutable seen : (PrRef * PrMergeMethod) option = None
                let session =
                    openToolSession (
                        { servicesOver (reposAnswering (fun _ -> async { return Error "not part of this test" })) with
                            Prs =
                              fun () ->
                                Some (
                                    { prsOpening (fun _ -> async { return Error "not part of this test" }) with
                                        Merge =
                                          fun _ pr method ->
                                            async {
                                                seen <- Some (pr, method)
                                                return Ok (sprintf "%s is in the merge queue" (PrRef.render pr))
                                            } }) })
                let! answer = session.Call "merge_pr" """{"repo":"octo/hello","number":12,"method":"rebase"}"""
                Expect.equal (seen |> Option.map (fun (pr, _) -> PrRef.render pr)) (Some "octo/hello#12") "the pull request"
                Expect.equal (seen |> Option.map snd) (Some Rebase) "and the method is the method"
                Expect.stringContains (answered answer) "octo/hello#12 is in the merge queue" "and the service's own words came back"
            }

        testCaseAsync "an unmerge_pr reaches the service with the pull request it named" <|
            async {
                let mutable seen : PrRef option = None
                let session =
                    openToolSession (
                        { servicesOver (reposAnswering (fun _ -> async { return Error "not part of this test" })) with
                            Prs =
                              fun () ->
                                Some (
                                    { prsOpening (fun _ -> async { return Error "not part of this test" }) with
                                        Unmerge =
                                          fun _ pr ->
                                            async {
                                                seen <- Some pr
                                                return Ok (sprintf "%s will no longer merge on its own" (PrRef.render pr))
                                            } }) })
                let! answer = session.Call "unmerge_pr" """{"repo":"octo/hello","number":12}"""
                Expect.equal (seen |> Option.map PrRef.render) (Some "octo/hello#12") "the pull request"
                Expect.stringContains (answered answer) "will no longer merge on its own" "and the service's own words came back"
            }

        testCaseAsync "a ready_pr reaches the service with the pull request it named" <|
            async {
                let mutable seen : PrRef option = None
                let session =
                    openToolSession (
                        { servicesOver (reposAnswering (fun _ -> async { return Error "not part of this test" })) with
                            Prs =
                              fun () ->
                                Some (
                                    { prsOpening (fun _ -> async { return Error "not part of this test" }) with
                                        Ready =
                                          fun _ pr ->
                                            async {
                                                seen <- Some pr
                                                return Ok (sprintf "%s is ready for review" (PrRef.render pr))
                                            } }) })
                let! answer = session.Call "ready_pr" """{"repo":"octo/hello","number":12}"""
                Expect.equal (seen |> Option.map PrRef.render) (Some "octo/hello#12") "the pull request"
                Expect.stringContains (answered answer) "is ready for review" "and the service's own words came back"
            }

        testCaseAsync "a draft_pr reaches the service with the pull request it named" <|
            async {
                let mutable seen : PrRef option = None
                let session =
                    openToolSession (
                        { servicesOver (reposAnswering (fun _ -> async { return Error "not part of this test" })) with
                            Prs =
                              fun () ->
                                Some (
                                    { prsOpening (fun _ -> async { return Error "not part of this test" }) with
                                        Draft =
                                          fun _ pr ->
                                            async {
                                                seen <- Some pr
                                                return Ok (sprintf "%s is a draft" (PrRef.render pr))
                                            } }) })
                let! answer = session.Call "draft_pr" """{"repo":"octo/hello","number":12}"""
                Expect.equal (seen |> Option.map PrRef.render) (Some "octo/hello#12") "the pull request"
                Expect.stringContains (answered answer) "is a draft" "and the service's own words came back"
            }

        // A repo name the domain refuses never reaches the gate, and the model is told what
        // to fix rather than that something failed.
        testCaseAsync "an argument the domain refuses is an answer the model can act on" <|
            async {
                let session = cloningAt "main"
                let! answer = addRepo session "not a repo"
                Expect.stringContains (answered answer) "not a repo name" "it says which argument, and why"
            }

        // THE case this file was written for, surviving the manual gate it was written
        // against. A slow command must never be reported as anybody's decision: a clone is
        // seconds of WORK, and an agent told a person is being waited on goes and asks them
        // for a decision they were never offered.
        testCaseAsync "a slow command waits for the WORK, never for a person" <|
            async {
                let session, finish = slowlyCloning ()
                let! call = Async.StartChild (addRepo session "octo/hello")
                do! session.Armed ()
                session.Advance (TimeSpan.FromSeconds 30.0)
                finish ()
                // Finished, and nothing appended for it: the tick is how the call finds out.
                do! session.Armed ()
                session.Advance (TimeSpan.FromSeconds 1.0)
                let! answer = call
                let text = answered answer
                Expect.stringContains text "added octo/hello" "the call carried the outcome back"
                Expect.isFalse (text.Contains "APPROVE") "nobody was ever asked to approve it"
            }

        // The yield is still there — a command that runs for minutes must not hold a turn
        // open — but it is bounded by the PROCESS deadline and says what it is: going, not
        // waiting on anybody.
        testCaseAsync "a command still going at the process deadline yields, saying it is running" <|
            async {
                let session, finish = slowlyCloning ()
                let! call = Async.StartChild (addRepo session "octo/hello")
                do! session.Armed ()
                session.Advance (TimeSpan.FromSeconds 600.0)
                let! answer = call
                let text = answered answer
                Expect.stringContains text "STILL RUNNING" "what is true: it is going, and it has not finished"
                Expect.stringContains text "check_pending" "with the way to pick it up"
                Expect.isFalse (text.Contains "APPROVE") "and still nobody to approve anything"
                finish ()
            }

        // A declared `setup:` is the repo MAKING its sandbox ready, and it becomes an
        // ordinary recorded block so the people in the session can watch it and read why it
        // failed. Background, because a setup worth declaring is the slow thing the first
        // command would otherwise pay for.
        testCaseAsync "a sandbox that just started runs the setup its repo declared" <|
            async {
                let seen = ResizeArray<CommandRequest> ()
                let session =
                    openToolSession (
                        servicesQueueing seen (registryReporting WorkSandboxes.SandboxStarted (declaring (Some "make deps"))))
                let! answer = startSandbox session
                Expect.stringContains (answered answer) "setup" "the answer says it is running"
                Expect.equal (seen |> Seq.map (fun r -> r.Command) |> List.ofSeq) [ "make deps" ] "the declared command, once"
                Expect.isTrue (seen |> Seq.forall (fun r -> r.Background)) "in the background, so the start does not wait it out"
            }

        // The fold re-asks at boot and after every repo verb. A setup block appearing in
        // somebody's terminal each time they touched a checkout is noise nobody asked for,
        // and it is why the registry reports which ask started the sandbox.
        testCaseAsync "a sandbox that was already running runs nothing again" <|
            async {
                let seen = ResizeArray<CommandRequest> ()
                let session =
                    openToolSession (
                        servicesQueueing seen (registryReporting WorkSandboxes.SandboxAlreadyRunning (declaring (Some "make deps"))))
                let! answer = startSandbox session
                // The call has to have SUCCEEDED for the emptiness to mean anything — an
                // arguments typo would empty `seen` just as well, and pass.
                Expect.stringContains (answered answer) "is up" "the sandbox came back"
                Expect.isEmpty seen "nothing was started, so nothing is prepared"
            }

        testCaseAsync "a sandbox that declared no setup queues nothing" <|
            async {
                let seen = ResizeArray<CommandRequest> ()
                let session =
                    openToolSession (
                        servicesQueueing seen (registryReporting WorkSandboxes.SandboxStarted (declaring None)))
                let! answer = startSandbox session
                Expect.stringContains (answered answer) "is up" "the sandbox came back"
                Expect.isEmpty seen "saying nothing is not asking to run nothing"
            }

        // A repo's sandbox is its FILE's to describe, and the tool that names one carries a
        // declaration with nothing in it. Started from that verbatim, `octo/hello:dev` came up
        // as a repo sandbox with no container — which is refused, in words that send whoever
        // reads them to a `yession.yaml` that declares one. It is how a sandbox an agent
        // stopped could not be started again.
        testCaseAsync "naming a repo's sandbox starts it as that repo's file declares it" <|
            async {
                let mutable asked : SandboxRequest option = None
                let session = openToolSession (declaringDev (fun request -> asked <- Some request))
                let! answer = session.Call "start_work_sandbox" """{"name":"octo/hello:dev"}"""
                Expect.stringContains (answered answer) "is up" "the start happened"
                let spec : EnvironmentSpec = (Option.get asked).Spec
                Expect.equal
                    (SandboxRuntime.describe spec.Runtime)
                    "ghcr.io/octo/dev:3"
                    "the container the file declared, not the `no container` the tool carries"
                Expect.equal spec.Setup (Some "make deps") "and everything else the file said"
            }

        // The one thing a file cannot know is which of THIS session's credentials to forward,
        // so the caller still contributes that — by union, because a repo that asked for a
        // credential does not stop needing it because somebody restarted the sandbox without
        // naming it.
        testCaseAsync "the credentials a start names join the ones the file asked for" <|
            async {
                let mutable asked : SandboxRequest option = None
                let session = openToolSession (declaringDev (fun request -> asked <- Some request))
                let! answer =
                    session.Call "start_work_sandbox" """{"name":"octo/hello:dev","forward":["jira"]}"""
                Expect.stringContains (answered answer) "is up" "the start happened"
                let request : SandboxRequest = Option.get asked
                Expect.equal
                    (request.Forward |> List.map ConnectionName.value)
                    [ "github"; "jira" ]
                    "the file's credential kept, the caller's added"
            }

        // The refusal a name nobody declared deserves: what this repo DOES declare. Refusing
        // it further down for having no container describes a file that declares two.
        testCaseAsync "a repo sandbox no file declares is refused naming the ones that are" <|
            async {
                let session = openToolSession (declaringDev ignore)
                let! answer = session.Call "start_work_sandbox" """{"name":"octo/hello:release"}"""
                let text = answered answer
                Expect.stringContains text "declares no sandbox named 'release'" "the name that is not there"
                Expect.stringContains text "octo/hello:dev" "and the one that is"
                Expect.isFalse (text.Contains "container") "not a complaint about a container the file declares"
            }

        // `dev` is what an agent writes after reading "started sandbox octo/hello:dev".
        // Every verb that reaches a RUNNING sandbox already reads it that way; starting was
        // the one door that did not, so a bare name whose repo sandbox had been stopped made
        // a NEW session-owned sandbox with none of the file's container — silently, under a
        // name that says it is the repo's.
        testCaseAsync "a bare name the session does not hold is the one sandbox a repo declares by it" <|
            async {
                let mutable asked : SandboxRequest option = None
                let session = openToolSession (declaringDev (fun request -> asked <- Some request))
                let! answer = session.Call "start_work_sandbox" """{"name":"dev"}"""
                Expect.stringContains (answered answer) "octo/hello:dev" "the repo's sandbox, said in full"
                let spec : EnvironmentSpec = (Option.get asked).Spec
                Expect.equal
                    (SandboxRuntime.describe spec.Runtime)
                    "ghcr.io/octo/dev:3"
                    "started as the file declares it, not as an empty session sandbox"
            }

        // Two checkouts declaring `dev` is a name with two meanings, and picking either is
        // picking for the caller. Refuse, naming both, so the next ask can say which.
        testCaseAsync "a bare name two repos declare is refused naming both" <|
            async {
                let services = declaringDev ignore
                let other = SandboxRef.parse "octo/world:dev" |> expect
                let session =
                    openToolSession
                        { services with
                            DeclaredSandboxes =
                                fun () -> services.DeclaredSandboxes () @ [ other, SandboxDecl.empty ] }
                let! answer = session.Call "start_work_sandbox" """{"name":"dev"}"""
                let text = answered answer
                Expect.stringContains text "ambiguous" "the name means two things"
                Expect.stringContains text "octo/hello:dev" "one of them"
                Expect.stringContains text "octo/world:dev" "and the other"
            }

    ]

// --- a person's first repo -----------------------------------------------------------------
// The launch surface's one act, through the same gate. What the chain has to hold here is
// different from a turn's: there is no tool result, so what the ATTRIBUTION says and what a
// failure becomes are the only record — and the act is three gated calls in sequence, each
// waited out, which is the one thing no single tool call does.

/// A repo service that HOLDS its listing, so the launch's admission rule has something to
/// read, and records who each verb was called as.
type private HeldRepos =
    { Service : Repos.ReposService
      Calls : ResizeArray<string * ActorRef> }

let private reposHolding (initial: RepoListing list) (clone: RepoRef -> Result<RepoListing, string>) : HeldRepos =
    let mutable listings = initial
    let calls = ResizeArray<string * ActorRef> ()
    let denied _ = async { return Error "not part of this test" }
    { Calls = calls
      Service =
        { AddRepo =
            fun caller repo ->
                async {
                    calls.Add ("add_repo " + RepoRef.value repo, caller.Actor)
                    match clone repo with
                    | Error e -> return Error e
                    | Ok listing ->
                        listings <- listings @ [ listing ]
                        return Ok { Repos.Recorded.Answer = listing; Repos.Recorded.RecordedAs = None }
                }
          ListRepos = fun () -> async { return Ok listings }
          SwitchBranch =
            fun caller repo branch _ ->
                async {
                    calls.Add (sprintf "switch_branch %s -> %s" (RepoRef.value repo) branch, caller.Actor)
                    listings <- listings |> List.map (fun l -> if l.Repo = repo then { l with Branch = branch } else l)
                    return Ok { Repos.Recorded.Answer = listings |> List.find (fun l -> l.Repo = repo); Repos.Recorded.RecordedAs = None }
                }
          FetchRepo = fun _ _ -> async { return Error "not part of this test" }
          RepoStatus = denied
          RepoLog = denied
          RepoDiff = denied
          RemoveRepo = fun _ _ _ -> async { return Error "not part of this test" } } }

/// Services over a held repo service whose profile writes land in the same call record.
let private servicesRecording (held: HeldRepos) : Commands.CommandServices =
    { servicesOver held.Service with
        Terminals =
            fun () ->
                { SessionTerminals.unavailable with
                    SetProfile =
                        fun authority sandbox cwd ->
                            async {
                                held.Calls.Add (
                                    sprintf "set_shell_profile %s %s" (SandboxRef.render sandbox) (defaultArg cwd "(cleared)"),
                                    Authority.author authority)
                                return Ok "set"
                            } } }

let private hello = RepoRef.create "octo/hello" |> expect

/// The person, as an act is attributed to them.
let private adaActs = ada
/// Ada as the log records her.
let private adaActor = Principal.toActor ada

let private cloned (branch: string) (repo: RepoRef) : Result<RepoListing, string> =
    Ok { Repo = repo; Branch = branch; Dirty = false; Path = "repos/octo/hello" }

/// Admit, then run the work in line — what the Host does in the background.
let private launched (session: ToolSession) (repo: RepoRef) (branch: string option) =
    async {
        match! session.Launch adaActs repo branch with
        | Error reason -> return Error (sprintf "not admitted: %s" reason)
        | Ok work ->
            match! work with
            | Ok () -> return Ok ()
            | Error (failure: Commands.LaunchFailure) -> return Error (sprintf "%s: %s" failure.Summary failure.Reason)
    }

let private launchTests =
    testList "A person's first repo" [

        testCaseAsync "a session that already has a repo is not launched into, and names the one it has" <|
            async {
                let held = reposHolding [ { Repo = hello; Branch = "main"; Dirty = false; Path = "repos/octo/hello" } ] (cloned "main")
                let session = openToolSession (servicesRecording held)
                let! admitted = session.Launch adaActs (RepoRef.create "octo/other" |> expect) None
                match admitted with
                | Ok _ -> failwith "admitted a second repo"
                | Error reason -> Expect.stringContains reason "octo/hello" "the refusal says which repo is already here"
                Expect.isEmpty held.Calls "and nothing was proposed to the gate"
            }

        testCaseAsync "the clone is attributed to the person, not the agent" <|
            async {
                let held = reposHolding [] (cloned "main")
                let session = openToolSession (servicesRecording held)
                let! outcome = launched session hello None
                expect outcome
                let addCall = held.Calls |> Seq.find (fun (call, _) -> call = "add_repo octo/hello")
                Expect.equal (snd addCall) adaActor "the repo service was called as ada"
            }

        testCaseAsync "a choice of the clone's own branch is not a switch; another is" <|
            async {
                let held = reposHolding [] (cloned "main")
                let session = openToolSession (servicesRecording held)
                let! sameBranch = launched session hello (Some "main")
                expect sameBranch
                Expect.isFalse
                    (held.Calls |> Seq.exists (fun (call, _) -> call.StartsWith "switch_branch"))
                    "the default branch was chosen, so no switch was made"

                let other = reposHolding [] (cloned "main")
                let another = openToolSession (servicesRecording other)
                let! switched = launched another hello (Some "feature/x")
                expect switched
                Expect.isTrue
                    (other.Calls |> Seq.exists (fun (call, who) -> call = "switch_branch octo/hello -> feature/x" && who = adaActor))
                    "a branch other than the clone's is switched to, as the person"
            }

        testCaseAsync "terminals start in the checkout afterwards" <|
            async {
                let held = reposHolding [] (cloned "main")
                let session = openToolSession (servicesRecording held)
                let! outcome = launched session hello None
                expect outcome
                Expect.equal
                    (held.Calls |> Seq.last)
                    ("set_shell_profile default repos/octo/hello", adaActor)
                    "the default sandbox's profile points at the path the clone answered with"
            }

        // A turn's `add_repo` YIELDS at the process deadline and the model picks it up; a
        // launch has nobody to pick it up, and the next call depends on the clone being
        // there — so the yield is resumed, however many deadlines the clone outlives.
        testCaseAsync "a clone that outlives the gate's deadline is waited out, not abandoned" <|
            async {
                let mutable finish : unit -> unit = ignore
                let cloning = Async.FromContinuations (fun (cont, _, _) -> finish <- fun () -> cont ())
                let held : HeldRepos =
                    let slow = reposHolding [] (cloned "main")
                    { Calls = slow.Calls
                      Service =
                        { slow.Service with
                            AddRepo =
                                fun caller repo ->
                                    async {
                                        do! cloning
                                        return! slow.Service.AddRepo caller repo
                                    } } }
                let session = openToolSession (servicesRecording held)
                let! work =
                    async {
                        match! session.Launch adaActs hello None with
                        | Error reason -> return failwithf "not admitted: %s" reason
                        | Ok work -> return work
                    }
                let! running = Async.StartChild work
                do! session.Armed ()
                session.Advance (TimeSpan.FromSeconds 600.0)
                do! session.Armed ()
                session.Advance (TimeSpan.FromSeconds 600.0)
                Expect.isFalse
                    (held.Calls |> Seq.exists (fun (call, _) -> call.StartsWith "set_shell_profile"))
                    "two deadlines on, the clone is still the thing being waited for"
                finish ()
                do! session.Armed ()
                session.Advance (TimeSpan.FromSeconds 1.0)
                let! outcome = running
                expect outcome
                Expect.isTrue
                    (held.Calls |> Seq.exists (fun (call, _) -> call.StartsWith "set_shell_profile"))
                    "and once it lands, the rest follows"
            }

        testCaseAsync "a clone that fails is a failure that names the call and the reason" <|
            async {
                let held = reposHolding [] (fun _ -> Error "github says not found")
                let session = openToolSession (servicesRecording held)
                match! session.Launch adaActs hello None with
                | Error reason -> failwithf "not admitted: %s" reason
                | Ok work ->
                    match! work with
                    | Ok () -> failwith "a failed clone reported success"
                    | Error failure ->
                        Expect.equal failure.Tool "add_repo" "which call"
                        Expect.equal failure.Summary "add_repo octo/hello" "as the record will show it"
                        Expect.equal failure.Reason "github says not found" "and why"
                Expect.isFalse
                    (held.Calls |> Seq.exists (fun (call, _) -> call.StartsWith "set_shell_profile"))
                    "nothing after the failed step ran"
            }
    ]

/// Services whose file changes land in `seen` instead of a sandbox, answering as told.
let private servicesEditing (seen: ResizeArray<FileEditRequest>) (edit: FileEditRequest -> Result<Edited, string>) =
    { servicesOver (reposAnswering (fun _ -> async { return Error "not part of this test" })) with
        Files =
            fun () ->
                { SessionFiles.unavailable with
                    Edit =
                        fun _ request ->
                            async {
                                seen.Add request
                                return edit request
                            } } }

let private fileTests =
    testList "The file commands, end to end" [

        // The whole chain for an edit: five arguments, two of them arbitrary text, encoded by
        // the binding, carried through the gate, decoded by the dispatch table. Every join is a
        // place a reordering or an escape goes unnoticed — so the texts here carry newlines,
        // quotes and a backslash on purpose.
        testCaseAsync "every argument of an edit_file survives the gate in the place it was written" <|
            async {
                let seen = ResizeArray ()
                let session =
                    openToolSession (
                        servicesEditing seen (fun _ ->
                            Ok
                                { Edited.Content = ""
                                  Edited.Replaced = 1
                                  Edited.LinesRemoved = 2
                                  Edited.LinesAdded = 1 }))
                let! answer =
                    session.Call
                        "edit_file"
                        """{"path":"src/A.fs","old_string":"let x = \"a\"\n  |> f","new_string":"let x = 'b' \\ c","replace_all":true,"sandbox":"dev"}"""
                let request = Seq.exactlyOne seen
                Expect.equal request.Path "src/A.fs" "the path"
                Expect.equal request.OldText "let x = \"a\"\n  |> f" "the text to find, newline and quotes intact"
                Expect.equal request.NewText "let x = 'b' \\ c" "the text to put, backslash intact"
                Expect.isTrue request.ReplaceAll "the flag"
                Expect.equal (SandboxRef.render request.Sandbox) "dev" "the sandbox"
                Expect.stringContains (answered answer) "edited src/A.fs: −2 +1 lines" "and what was done came back"
            }

        // A refusal from the file side — not found, ambiguous — is an ANSWER the model reads,
        // in the words `FileEdit.describe` chose, not a protocol error it would route around.
        testCaseAsync "an edit the file refuses is told to the model in the file's words" <|
            async {
                let seen = ResizeArray ()
                let session = openToolSession (servicesEditing seen (fun r -> Error (FileEdit.describe r.Path EditFailure.NotFound)))
                let! answer = session.Call "edit_file" """{"path":"f","old_string":"zzz","new_string":"y"}"""
                let text = answered answer
                Expect.stringContains text "old_string was not found in f" "which file, and why"
            }

        testCaseAsync "an edit without a sandbox is about the default one" <|
            async {
                let seen = ResizeArray ()
                let session =
                    openToolSession (
                        servicesEditing seen (fun _ ->
                            Ok { Edited.Content = ""; Edited.Replaced = 1; Edited.LinesRemoved = 1; Edited.LinesAdded = 1 }))
                let! _ = session.Call "edit_file" """{"path":"f","old_string":"a","new_string":"b"}"""
                Expect.equal (Seq.exactlyOne seen).Sandbox SandboxRef.defaultRef "where every terminal starts"
            }

        testCaseAsync "a write_file carries the whole content through the gate" <|
            async {
                let mutable written : (SandboxRef * string * string) option = None
                let services =
                    { servicesOver (reposAnswering (fun _ -> async { return Error "not part of this test" })) with
                        Files =
                            fun () ->
                                { SessionFiles.unavailable with
                                    Write =
                                        fun _ sandbox path content ->
                                            async {
                                                written <- Some (sandbox, path, content)
                                                return Ok ()
                                            } } }
                let session = openToolSession services
                let! answer = session.Call "write_file" """{"path":"new/B.fs","content":"one\ntwo\n"}"""
                match written with
                | Some (sandbox, path, content) ->
                    Expect.equal sandbox SandboxRef.defaultRef "the default sandbox"
                    Expect.equal path "new/B.fs" "the path"
                    Expect.equal content "one\ntwo\n" "the content, whole"
                | None -> failwith "nothing was written"
                Expect.stringContains (answered answer) "wrote new/B.fs (2 lines)" "and what was done came back"
            }
    ]

/// The artifact verb, driven the way a turn drives it: the tool, the gate, the dispatch, and
/// the store at the far end. What is pinned here is the JOIN — that the path and the name
/// survive the encoding, that the sandbox defaults to the one everybody works in, and that the
/// address the store minted is what comes back, since it is the one thing the caller could not
/// have worked out for itself.
let private artifactTests =
    let stamp =
        match ArtifactStamp.create "7f2a1c" with
        | Ok s -> s
        | Error e -> failwithf "stamp: %s" e

    let digest =
        match ContentDigest.create (String.replicate 64 "a") with
        | Ok d -> d
        | Error e -> failwithf "digest: %s" e

    /// A store that records what it was asked and answers as if the bytes had landed.
    let servicesSharing (seen: ResizeArray<SandboxRef * string * string option>) (result: string -> Result<ArtifactRef, string>) =
        { servicesOver (reposAnswering (fun _ -> async { return Error "not part of this test" })) with
            Artifacts =
                fun () ->
                    { Artifacts.unavailable with
                        Share =
                            fun actor sandbox path name ->
                                async {
                                    seen.Add (sandbox, path, name)
                                    let asked = name |> Option.defaultValue (Artifacts.nameOfPath path)
                                    match result asked with
                                    | Error e -> return Error e
                                    | Ok ref ->
                                        return
                                            Ok
                                                { ArtifactShared.MessageId = MessageId.create "m1" |> Result.toOption |> Option.get
                                                  ArtifactShared.Ref = ref
                                                  ArtifactShared.MediaType = ArtifactRef.mediaType ref
                                                  ArtifactShared.Bytes = 2048L
                                                  ArtifactShared.Digest = digest
                                                  ArtifactShared.Actor = actor }
                                } } }

    let firstVersion (name: string) = ArtifactRef.first name stamp

    let sandboxNamed name =
        match SandboxName.create name with
        | Ok n -> SandboxRef.create SessionOwned n
        | Error e -> failwithf "sandbox name: %s" e

    /// The refusal, which is the thing under test — a weighing that answered with a size when it
    /// was asked about a path that is not there would be the fault, not a bad message.
    let expectError (result: Result<int64, string>) =
        match result with
        | Error said -> said
        | Ok bytes -> failwithf "expected a refusal, got %d bytes" bytes

    testList
        "artifacts"
        [ testCaseAsync "a share_artifact answers with the address the store minted" <|
            async {
                let seen = ResizeArray ()
                let session = openToolSession (servicesSharing seen firstVersion)
                let! answer = session.Call "share_artifact" """{"path":"out/chart.png"}"""
                let sandbox, path, name = Seq.exactlyOne seen
                Expect.equal sandbox SandboxRef.defaultRef "the sandbox everybody works in, when none was said"
                Expect.equal path "out/chart.png" "the file, as the sandbox takes it"
                Expect.equal name None "no name asked for — the store takes the file's own"
                let text = answered answer
                Expect.stringContains text "file:///artifacts/chart.png/0000-7f2a1c" "the address it got"
                // 2048 bytes in the decimal units ContentSize.render speaks, so the size beside an
                // address reads in the same units as the cap a refusal quotes.
                Expect.stringContains text "2.05 kB" "and how big it is"
            }

          testCaseAsync "a name and a sandbox survive the gate's encoding" <|
            async {
                let seen = ResizeArray ()
                let session = openToolSession (servicesSharing seen firstVersion)
                let! _ = session.Call "share_artifact" """{"path":"/tmp/x.png","name":"coverage.png","sandbox":"dev"}"""
                let sandbox, path, name = Seq.exactlyOne seen
                Expect.equal (SandboxRef.render sandbox) "dev" "the sandbox asked for"
                Expect.equal path "/tmp/x.png" "the file"
                Expect.equal name (Some "coverage.png") "what to call it here"
            }

          // The cap is the store's to enforce and its wording is what the agent acts on, so the
          // refusal comes back whole rather than as "the command failed".
          testCaseAsync "a refusal comes back in the store's own words" <|
            async {
                let seen = ResizeArray ()
                let session =
                    openToolSession (servicesSharing seen (fun _ -> Error "big.iso is 4.1 GB, and an artifact may be at most 100 MB"))
                let! answer = session.Call "share_artifact" """{"path":"big.iso"}"""
                Expect.stringContains (answered answer) "at most 100 MB" "the cap, as the store said it"
            }

          // What the sandbox's shell says about a path is written for whoever wrote the shell
          // (`sh: 1: cannot open /x: No such file`). The agent reads this and decides what to do
          // next, so every answer the question can have is one this side named.
          testList
              "weighing the file the caller named"
              [ test "a byte count is the size" {
                    Expect.equal
                        (Artifacts.weighed SandboxRef.defaultRef "out/chart.png" (0, "2048\n", ""))
                        (Ok 2048L)
                        "what wc counted"
                }

                test "a path that is not there says so, and where it looked" {
                    let said = expectError (Artifacts.weighed (sandboxNamed "dev") "out/chart.png" (3, "", ""))
                    Expect.stringContains said "out/chart.png" "the path the caller named"
                    Expect.stringContains said "'dev'" "the sandbox it was looked for in — the file may be in another"
                    Expect.isFalse (said.Contains "sh:") "not the shell's own complaint"
                }

                test "a directory says what an artifact is instead" {
                    let said = expectError (Artifacts.weighed SandboxRef.defaultRef "out" (4, "", ""))
                    Expect.stringContains said "one file" "an artifact is a file, and the agent has to pick one"
                }

                test "a file it cannot read is not a file that is missing" {
                    let said = expectError (Artifacts.weighed SandboxRef.defaultRef "/root/key" (5, "", ""))
                    Expect.stringContains said "not readable" "which is a different thing to do about"
                }

                // An exit code nothing here anticipated is the one case where the shell knows more
                // than this side does, so its words are kept — under a sentence that says what was
                // being attempted.
                test "an answer nobody anticipated keeps what the shell said" {
                    let said = expectError (Artifacts.weighed SandboxRef.defaultRef "out/chart.png" (126, "", "Permission denied\n"))
                    Expect.stringContains said "Permission denied" "the shell's words"
                    Expect.stringContains said "how big out/chart.png is" "and what was being asked"
                } ]
        ]

let tests = testList "Tool calls" [ tests'; fileTests; artifactTests; launchTests ]
