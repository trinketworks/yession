module Yession.Host.RepoSandboxes

// The fold: every configured repo's `yession.yaml`, into the commands the session already
// has (Plan 27).
//
// There is no executor here and deliberately no second policy engine. Plan 15 made every
// mutating command ENSURE-SHAPED specifically so this could be a fold rather than a
// runner: a declaration becomes one `start_work_sandbox`, put through the same gate the
// agent's goes through, and asking twice for what is already running changes nothing and
// records nothing. So convergence is a property of the commands, not of anything written
// here — and re-folding is free, which is what lets it run at boot and after every verb
// that changes what checkouts exist.
//
// What this DOES own is the part no command can: which files there are, which of them could
// be read, and what happened to each declaration. A fold that silently did nothing for a
// repo whose file has a typo would be the exact failure the schema's strict decoding exists
// to avoid, one layer up.
//
// It never stops anything. A declaration that disappears leaves its sandbox running and
// marked as no longer declared: convergence that kills somebody's build is not convergence
// (`WorkSandboxes.fs`), and removal stays `stop_work_sandbox`.

open Yession.Domain
open Yession.Domain.Repos
open Yession.Domain.Sandboxes
open Yession.Domain.Agent
open Yession.Domain.Tools
open Yession.Domain.Chat
open Yession.Session

/// What a repo's selection comes to: the lines a person reads, and the routes its sandboxes
/// forward GitHub by. Those routes are what trusting the repo means. A repo that forwards none
/// spends nobody's GitHub, so it asks nobody: what else it holds is the operator's to grant.
type RepoCapabilities =
    { Granted : string list
      GitHubRoutes : ConnectionRoute list }

/// Whether one repo's sandboxes may start in this fold, and on whose authority.
type private TrustDecision =
    /// Start them, with this credential's act: the person whose trust runs the repo here, or —
    /// for a repo that forwards nobody's GitHub — whoever the fold runs for.
    | RunsWith of CredentialFor
    /// Hold them, and say why.
    | Holds of problem: string

/// What the last fold made of one repo. `Sandbox = None` is about the FILE itself — it
/// could not be read — and the distinction matters because those are fixed in different
/// places: one by whoever wrote the YAML, the other by whoever wrote the sandbox.
type FoldOutcome =
    { Repo : RepoRef
      Sandbox : SandboxRef option
      /// `None` is fine. There is no third state: a declaration either became a sandbox or
      /// has a reason it did not.
      Problem : string option }

type RepoSandboxes =
    { /// Re-read every checkout and ensure what it declares, on the authority of whoever
      /// asked. `None` is the fold at boot, which nobody triggered — and the one that runs
      /// again, on THEIR authority, when somebody arrives. One fold runs at a time; a
      /// second asked for while one is in flight waits for it.
      ///
      /// The fold cause is why it ran, which becomes why each sandbox it starts came up.
      Fold : FoldCause -> CredentialFor -> Async<unit>
      /// What the last fold made of each repo, in a stable order — the query's rows.
      Outcomes : unit -> FoldOutcome list
      /// Sandboxes this session is running that no file declares any more. Named rather
      /// than stopped.
      Undeclared : unit -> SandboxRef list
      /// Every declaration the last fold read, in a stable order. Answered here because this
      /// is what holds declarations; the sandbox manager holds none and asks.
      ///
      /// The whole list rather than a lookup, because the two questions a start asks are one
      /// question: what this name was declared as, and — when it was declared as nothing —
      /// which names its repo does declare, which is the only useful half of that refusal.
      ///
      /// A start that NAMES a repo's sandbox has to bring it up as the file says, and only
      /// this knows what the file said. Naming `octo/hello:dev` started it from the empty
      /// declaration the tool carries — no container — and was refused as a repo sandbox
      /// declaring none, which sends whoever reads it to a `yession.yaml` that declares one.
      ///
      /// `Declarations` rather than the bare list, because a fold that could not read the files
      /// has to be told apart from one that read them and found nothing.
      Declared : unit -> Declarations
      /// What a checkout said one of its sandboxes is FOR, from the last fold. Answered here
      /// because this is what holds declarations; the sandbox manager holds none and asks.
      Described : SandboxRef -> string option
      /// A person's answer to a repo's trust card: the routes it showed, and the ones they
      /// ticked. Written to their own record, and then the repo runs with their GitHub when
      /// nobody's trust runs it here yet and their answer covers what it asks.
      Trust : Principal -> RepoRef -> ConnectionRoute list -> ConnectionRoute list -> Async<Result<unit, string>> }

/// A session with nothing to fold: no repos service, or a composition without one. Total,
/// so a caller never branches on whether the fold exists.
let none : RepoSandboxes =
    { Fold = fun _ _ -> async { return () }
      Outcomes = fun () -> []
      Undeclared = fun () -> []
      Declared = fun () -> Read Map.empty
      Described = fun _ -> None
      Trust = fun _ _ _ _ -> async { return Error "this session has no repos to trust" } }

/// What the log already says about one declaration: the reason it was last refused, when
/// that refusal is still the last word about it.
///
/// A start ENDS a refusal rather than merely following it, so a failure after one is news
/// again — the same rule `SessionEnvironment` applies to its own start attempts, and for the
/// same reason: what is stale is a refusal that is still true, not every refusal ever made.
///
/// Read from the log rather than from `outcomes`, which is the field beside it. `outcomes`
/// is what the LAST fold made of things and is empty in a process that has just started, so
/// a delta against it would re-announce every standing refusal on every restart. The log is
/// what the session has actually been told.
let private lastRefusal (events: SessionEvent list) (repo: RepoRef) (sandbox: SandboxRef option) : string option =
    let rendered = sandbox |> Option.map SandboxRef.render
    events
    |> List.choose (fun event ->
        match event with
        | SessionEvent.RepoConfigRefused r when
            RepoRef.value r.Repo = RepoRef.value repo
            && (r.Sandbox |> Option.map SandboxRef.render) = rendered -> Some (Some r.Reason)
        // A start that ran and failed already accounts for itself — it opened a running act
        // and resolved it to a failure a person reads on the timeline. Counting it here means
        // the fold sees this reason as already told and does not ALSO file a
        // `RepoConfigRefused` saying the same thing, which is the two-accounts-of-one-fault
        // this dedup exists to prevent.
        | SessionEvent.WorkSandboxStartFailed f when Some (SandboxRef.render f.Sandbox) = rendered ->
            Some (Some f.Reason)
        | SessionEvent.WorkSandboxStarted started when Some (SandboxRef.render started.Sandbox) = rendered ->
            Some None
        | _ -> None)
    |> List.tryLast
    |> Option.flatten

/// Whether the log already carries this exact note about this repo's file — the same words,
/// at the same place. A note moves when its file does, and a moved one is news.
let private alreadyNoted (events: SessionEvent list) (repo: RepoRef) (where: string) (warning: string) : bool =
    events
    |> List.exists (fun event ->
        match event with
        | SessionEvent.RepoConfigWarned w ->
            RepoRef.value w.Repo = RepoRef.value repo && w.Where = where && w.Warning = warning
        | _ -> false)

/// What the log already says this repo asks for.
///
/// The LOG rather than a field, for the reason every delta in this file uses it: a field is
/// empty in a process that has just started, so a delta against one would re-announce every
/// repo's capabilities on every restart — which is the opposite of the point, since a
/// re-announcement nobody caused trains people to stop reading them.
let private lastCapabilities (events: SessionEvent list) (repo: RepoRef) : string list option =
    events
    |> List.choose (fun event ->
        match event with
        | SessionEvent.RepoCapabilitiesChanged c when RepoRef.value c.Repo = RepoRef.value repo ->
            Some c.Granted
        | _ -> None)
    |> List.tryLast

/// Who this repo runs with here: the person in its last `RepoTrusted` since it last came into the
/// session. A repo removed and added again is one nobody here has trusted yet.
let private trusterOf (events: SessionEvent list) (repo: RepoRef) : Principal option =
    events
    |> List.fold
        (fun truster event ->
            match event with
            | SessionEvent.RepoAdded a when RepoRef.value a.Repo = RepoRef.value repo -> None
            | SessionEvent.RepoRemoved r when RepoRef.value r.Repo = RepoRef.value repo -> None
            | SessionEvent.RepoTrusted t when RepoRef.value t.Repo = RepoRef.value repo -> Some t.By
            | _ -> truster)
        None

/// The routes of the card this repo has standing — what it last asked trust for — or `None`
/// when nothing is asked: somebody answered it, or it never asked. The same fold as the
/// client's (`RepoTrustWaits`), so a card on a screen and a held sandbox cannot disagree.
let private standingAsk (events: SessionEvent list) (repo: RepoRef) : ConnectionRoute list option =
    RepoTrustWaits.pending events
    |> List.tryFind (fun (waiting, _) -> RepoRef.value waiting = RepoRef.value repo)
    |> Option.map snd

/// What a repo's selection comes to on THIS host: the lines the timeline says it asks for, and
/// the routes its sandboxes forward GitHub by.
///
/// A function here rather than a lambda in the composition root, because it settles two
/// things a test has to be able to reach. First, that the lines are the host's realisation
/// and not the selection's own words — the operator named a socket, the repo picked it, and
/// on a host that cannot scope one the sandbox gets every socket, so that is what is said.
/// Second, that the routes come from the closure, under the connection's own name: they are
/// what a person is asked to trust the repo with, and what a loan is then built from.
///
/// A selection of nothing resolves to nothing without asking the resolver. That is not an
/// optimisation — a repo that selects no resource asks nobody anything, and running it
/// through a host that declares no profile would turn silence into a refusal.
let capabilitiesOn
    (limits: HostLimits)
    (github: ConnectionName)
    (resolve: ResourceName list -> ResourceName list -> Result<ResourceClosure, string>)
    ((uses, wants): ResourceName list * ResourceName list)
    : Result<RepoCapabilities, string> =
    match uses, wants with
    | [], [] -> Ok { Granted = []; GitHubRoutes = [] }
    | uses, wants ->
        // Both postures reach the same resolution, so a connection is asked about exactly as
        // much wanted as used: what is shown is what will be GRANTED, and a want this host
        // does not offer is neither granted nor asked about.
        resolve uses wants
        |> Result.map (fun closure ->
            { Granted = RealisedClosure.describeOn limits closure
              // Read off the closure, never off the lines above: the lines are words for a
              // person, and the routes are what a loan is built from.
              GitHubRoutes =
                ResourceClosure.leaves closure
                |> Set.toList
                |> List.choose (fun leaf ->
                    match leaf with
                    | ResourceLeaf.Connection (name, route) when name = ConnectionName.value github -> Some route
                    | _ -> None)
                |> List.distinct
                |> List.sort })

let create
    (repos: unit -> Repos.ReposService option)
    // Everybody's trust, read from their own records: what decides whether a repo that forwards
    // GitHub may start, and with whose.
    (trusts: RepoTrusts)
    (sandboxes: unit -> WorkSandboxes.WorkSandboxes)
    (run: RunGatedCommand)
    (log: EventLog<SessionEvent>)
    // What a selection of resource names comes to. The fold needs it because a capability
    // set is a fact about a REPO, not about one of its sandboxes, and only the fold sees all
    // of a repo's declarations at once.
    //
    // Whether it asks is REPORTED rather than inferred from the words: reading it back out of
    // a rendered description would make the prompt depend on the wording of a sentence, and
    // the wording is a design that will move.
    (capabilitiesOf: ResourceName list * ResourceName list -> Result<RepoCapabilities, string>)
    : RepoSandboxes =

    let mintMessageId () : MessageId =
        match MessageId.create (string (System.Guid.NewGuid ())) with
        | Ok id -> id
        | Error e -> failwithf "message id invariant violated: %s" e

    let append (actor: ActorRef) (event: SessionEvent) : Async<unit> =
        async {
            let! _ = log.Append actor event
            return ()
        }

    let mutable outcomes : FoldOutcome list = []
    // What the last fold read, by rendered ref: the DECLARATIONS themselves, which is the one
    // fact every answer below is a slice of.
    //
    // It is a fact about the FILES as they stood when they were folded, so it is kept rather
    // than re-read: reading again later would answer about a file that has since changed. It
    // was three maps — the refs, the descriptions, and the `repos:` paths — each a projection
    // of this one, and each able to disagree with the others about which fold it came from.
    //
    // `NotYetRead` until the first fold finishes, so nothing reads the empty map as an answer.
    let mutable declarations : Declarations = NotYetRead

    /// Whether a repo whose sandboxes forward GitHub may start in this fold, and with whose.
    ///
    /// It runs with the GitHub of the person in its last `RepoTrusted` while their record still
    /// answers everything it asks. Otherwise it waits, with a card for everyone here, until an
    /// act that OFFERS somebody's trust finds their record answering it: adding the repo, or
    /// answering the card (`FoldCause.Offered`). Nothing else makes anybody its truster. A
    /// person who joins, a boot and a branch switch only read who it already is.
    ///
    /// The person it runs with may not be readable — not signed in to this launch yet after a
    /// restart, or the Manager refused. Then it waits for them without a card: a card would
    /// hand the repo to whoever arrived first, and its owner coming back resumes it.
    let decideTrust (told: SessionEvent list) (cause: FoldCause) (repo: RepoRef) (asked: Set<ConnectionRoute>) : Async<TrustDecision> =
        async {
            let answering (person: Principal) =
                async {
                    match! trusts.Read (RepoTrust.ownerOf person) repo with
                    | Ok trust -> return Ok (RepoTrust.answers asked trust)
                    | Error reason -> return Error reason
                }
            let trusted (person: Principal) =
                async {
                    let actor = Principal.toActor person
                    do!
                        append
                            actor
                            (SessionEvent.RepoTrusted
                                { RepoTrusted.MessageId = mintMessageId ()
                                  RepoTrusted.Repo = repo
                                  RepoTrusted.By = person
                                  RepoTrusted.Actor = actor })
                    return RunsWith (CredentialFor.Person person)
                }
            let! current =
                async {
                    match trusterOf told repo with
                    | None -> return None
                    | Some truster ->
                        match! answering truster with
                        | Ok true -> return Some (Ok truster)
                        | Ok false -> return None
                        | Error reason -> return Some (Error reason)
                }
            match current with
            // Settling a card that stood, so it leaves every screen once somebody runs it.
            | Some (Ok truster) when standingAsk told repo |> Option.isSome -> return! trusted truster
            | Some (Ok truster) -> return RunsWith (CredentialFor.Person truster)
            | _ ->
                let! offered =
                    async {
                        match cause with
                        | FoldCause.Offered (offeredRepo, _, person) when RepoRef.value offeredRepo = RepoRef.value repo ->
                            match! answering person with
                            | Ok true -> return Some person
                            | _ -> return None
                        | _ -> return None
                    }
                match offered, current with
                | Some person, _ -> return! trusted person
                | None, Some (Error reason) ->
                    return Holds (sprintf "waiting for the person whose trust runs %s here: %s" (RepoRef.value repo) reason)
                | None, _ ->
                    let routes = Set.toList asked
                    if standingAsk told repo <> Some routes then
                        let actor = ActorRef.Configured repo
                        do!
                            append
                                actor
                                (SessionEvent.RepoTrustAsked
                                    { RepoTrustAsked.MessageId = mintMessageId ()
                                      RepoTrustAsked.Repo = repo
                                      RepoTrustAsked.Routes = routes
                                      RepoTrustAsked.Actor = actor
                                      RepoTrustAsked.CausedBy = FoldCause.causeFor repo cause })
                    return Holds (sprintf "waiting for somebody here to trust %s with their GitHub" (RepoRef.value repo))
        }

    let foldOnce (cause: FoldCause) (onBehalfOf: CredentialFor) : Async<unit> =
        async {
            match repos () with
            | None ->
                outcomes <- []
                declarations <- Read Map.empty
            | Some service ->
                match! service.ListRepos () with
                // A listing that failed says nothing about any repo in particular, so there
                // is no row to write and the previous declarations stand. What changes is
                // that they are no longer CURRENT, and the state says so with the reason:
                // kept silently, a failure at boot left nothing at all, which every reader
                // took to mean that no file declares anything.
                | Error reason -> declarations <- Declarations.after declarations (Error reason)
                | Ok reads ->
                    let unreadableCheckouts = RepoRead.unreadable reads
                    let! read = RepoConfig.readAll service.ReadCommitted (RepoRead.listed reads |> List.map (fun listing -> listing.Repo))
                    // One checkout git cannot read says nothing about its neighbours, so they
                    // are read as they are and the fold stays `Read`. Nor does it say anything
                    // about its OWN file, so what that repo declared last time stands, beside
                    // a row giving the reason: "this repo declares nothing" because one
                    // checkout blinked would be a worse lie than saying nothing (`RepoRead`).
                    let declared =
                        let kept =
                            Declarations.known declarations
                            |> Map.filter (fun ref _ ->
                                match SandboxRef.scope ref with
                                | SessionOwned -> false
                                | RepoOwned repo -> unreadableCheckouts |> List.exists (fun u -> u.Repo = repo))
                        Map.fold (fun acc ref decl -> Map.add ref decl acc) read.Declared kept
                    let unreadable =
                        read.Refused
                        @ (unreadableCheckouts
                           |> List.map (fun u -> u.Repo, sprintf "git cannot read the checkout at %s: %s" u.Path u.Reason))
                    // Kept BEFORE anything is started, so a sandbox that comes up in this fold
                    // can be described as it comes up rather than on the next one — and so a
                    // start in this fold can be answered about, which is what a declaration is
                    // read for at all.
                    declarations <- Declarations.after declarations (Ok declared)
                    let fileProblems =
                        unreadable
                        |> List.map (fun (repo, reason) ->
                            { Repo = repo; Sandbox = None; Problem = Some reason })
                    // What each repo asks for, said when it changes — before anything is
                    // started, so the timeline reads in the order things happened rather
                    // than announcing a set after the sandboxes it describes came up.
                    let! page = log.Read None System.Int32.MaxValue
                    let told = page.Events |> List.map (fun e -> e.Event)
                    let byRepo =
                        declared
                        |> Map.toList
                        |> List.choose (fun (ref, decl) ->
                            match SandboxRef.scope ref with
                            | SessionOwned -> None
                            | RepoOwned repo -> Some (repo, decl))
                        |> List.groupBy (fun (repo, _) -> RepoRef.value repo)
                    for _, entries in byRepo do
                        let repo = entries |> List.head |> fst
                        let asked = entries |> List.map snd |> SandboxDecl.selectionOf
                        match capabilitiesOf asked with
                        // A selection that does not resolve is already a refusal further
                        // down, said per declaration and with the reason. Saying it twice
                        // here, in different words, would be two accounts of one fault.
                        | Error _ -> ()
                        | Ok capabilities ->
                            let granted = capabilities.Granted
                            if lastCapabilities told repo <> Some granted then
                                let actor = ActorRef.Configured repo
                                do!
                                    append
                                        actor
                                        (SessionEvent.RepoCapabilitiesChanged
                                            { RepoCapabilitiesChanged.MessageId = mintMessageId ()
                                              RepoCapabilitiesChanged.Repo = repo
                                              RepoCapabilitiesChanged.Granted = granted
                                              RepoCapabilitiesChanged.Actor = actor
                                              RepoCapabilitiesChanged.CausedBy = FoldCause.causeFor repo cause })
                    // Where the session's own terminals start, as the files say it
                    // (`session.shell-profile`). Before the starts, because it waits on
                    // nothing they bring up: the default sandbox is the session's own. Which
                    // file applies, if any, is `ConfiguredProfile`'s to decide; this only
                    // resolves each path to where the default sandbox reaches it and carries
                    // the decision out through the same gate as everything else here.
                    let profileKey = "session.shell-profile"
                    let declaredProfiles =
                        read.ShellProfiles
                        |> List.choose (fun (repo, path) ->
                            RepoRead.listed reads
                            |> List.tryFind (fun listing -> RepoRef.value listing.Repo = RepoRef.value repo)
                            |> Option.map (fun listing -> repo, CheckoutPath.under listing.Path path))
                    let lastProfile =
                        told
                        |> List.choose (fun event ->
                            match event with
                            | SessionEvent.ShellProfileSet p when
                                SandboxRef.render p.Sandbox = SandboxRef.render SandboxRef.defaultRef -> Some p
                            | _ -> None)
                        |> List.tryLast
                    let warn (repo: RepoRef) (warning: string) =
                        async {
                            if not (alreadyNoted told repo profileKey warning) then
                                let actor = ActorRef.Configured repo
                                do!
                                    append
                                        actor
                                        (SessionEvent.RepoConfigWarned
                                            { RepoConfigWarned.MessageId = mintMessageId ()
                                              RepoConfigWarned.Repo = repo
                                              RepoConfigWarned.Sandbox = None
                                              RepoConfigWarned.Where = profileKey
                                              RepoConfigWarned.Warning = warning
                                              RepoConfigWarned.Actor = actor
                                              RepoConfigWarned.CausedBy = FoldCause.causeFor repo cause })
                        }
                    for repo, warning in ConfiguredProfile.conflicts declaredProfiles do
                        do! warn repo warning
                    let change =
                        match ConfiguredProfile.decide declaredProfiles lastProfile with
                        | ConfiguredProfile.Leave -> None
                        | ConfiguredProfile.Apply (repo, path) -> Some (repo, Some path)
                        | ConfiguredProfile.Withdraw repo -> Some (repo, None)
                    match change with
                    | None -> ()
                    | Some (repo, cwd) ->
                        let call =
                            Commands.setShellProfileCall
                                (Authority.configuredBy repo onBehalfOf)
                                (FoldCause.causeFor repo cause)
                                SandboxRef.defaultRef
                                cwd
                        // A profile that could not be set is the file's to hear about, said
                        // once, like an analyzer's note: the session carries on where it was.
                        let failed =
                            async {
                                match! run call with
                                | Error reason -> return Some reason
                                | Ok outcome ->
                                    match outcome.Status with
                                    | CommandRefusedBy (_, reason) -> return Some (defaultArg reason "refused")
                                    | CommandRan text when text.StartsWith "failed: " -> return Some (text.Substring 8)
                                    | CommandRan _
                                    | CommandRunning -> return None
                            }
                        match! failed with
                        | Some reason -> do! warn repo (sprintf "the shell profile could not be set: %s" reason)
                        | None -> ()
                    // Who each repo's sandboxes run with. One that forwards nobody's GitHub
                    // runs on the authority of whoever folded, as every start here always has:
                    // it spends nothing of theirs. One that forwards GitHub runs with the GitHub
                    // of somebody who trusts it here, and waits while nobody's trust covers it
                    // (`decideTrust`). In turn rather than at once, because each may write the
                    // log, and the next reads it.
                    let! decided =
                        async {
                            let decided = ref Map.empty
                            for _, entries in byRepo do
                                let repo = entries |> List.head |> fst
                                let asked =
                                    match capabilitiesOf (entries |> List.map snd |> SandboxDecl.selectionOf) with
                                    | Ok capabilities -> Set.ofList capabilities.GitHubRoutes
                                    // Refused per declaration below, with the reason; nothing
                                    // here starts it, so there is nothing to trust.
                                    | Error _ -> Set.empty
                                if Set.isEmpty asked then
                                    decided.Value <- Map.add (RepoRef.value repo) (RunsWith onBehalfOf) decided.Value
                                else
                                    let! decision = decideTrust told cause repo asked
                                    decided.Value <- Map.add (RepoRef.value repo) decision decided.Value
                            return decided.Value
                        }
                    let! rows =
                        declared
                        |> Map.toList
                        |> List.map (fun (ref, decl) ->
                            async {
                                match SandboxRef.scope ref with
                                // Every key in the map came from `scoped`, which only ever
                                // writes a repo scope. A session-owned one here would mean
                                // the union had been fed something no file produced.
                                | SessionOwned -> return None
                                | RepoOwned repo ->
                                  match Map.tryFind (RepoRef.value repo) decided with
                                  // Nothing starts until somebody here trusts it. The row says
                                  // so, so the answer to "why is my sandbox not running" is in
                                  // the same place as what to do about it.
                                  | Some (Holds problem) -> return Some { Repo = repo; Sandbox = Some ref; Problem = Some problem }
                                  | decision ->
                                    let credential =
                                        match decision with
                                        | Some (RunsWith credential) -> credential
                                        | _ -> onBehalfOf
                                    let call =
                                        Commands.startWorkSandboxCall
                                            (Authority.configuredBy repo credential)
                                            (FoldCause.causeFor repo cause)
                                            ref
                                            decl
                                    match! run call with
                                    | Error reason ->
                                        return Some { Repo = repo; Sandbox = Some ref; Problem = Some reason }
                                    | Ok outcome ->
                                        // The gate answers with what happened, and a
                                        // command that ran and failed is not a command that
                                        // was refused — both are reasons this declaration
                                        // is not a sandbox, and the row says which.
                                        let problem =
                                            match outcome.Status with
                                            | CommandRefusedBy (_, reason) ->
                                                Some (defaultArg reason "refused")
                                            | CommandRan text when text.StartsWith "failed: " ->
                                                Some (text.Substring 8)
                                            | CommandRan _
                                            | CommandRunning -> None
                                        return Some { Repo = repo; Sandbox = Some ref; Problem = problem }
                            })
                        // In PARALLEL, so a repo's sandboxes come up at once rather than one
                        // waiting for the one before it to finish — the `dev` and `gate` of a
                        // checkout no longer queue behind each other. Safe because each
                        // declaration is a DISTINCT sandbox ref: `WorkSandboxes.ensure`'s
                        // find-then-start cannot collide across two names, this host is
                        // single-threaded (Fable/JS — asyncs interleave only at I/O awaits, so
                        // the shared registry list is never torn), and `EventStore.append` is
                        // synchronous, so concurrent starts cannot interleave a log line. The
                        // one-fold-at-a-time guard is untouched: this parallelism is WITHIN a
                        // fold, over refs a single fold owns. `Async.Parallel` preserves input
                        // order in its result, so `outcomes` reads the same; only the starts'
                        // own timeline events interleave by completion, which is the coming-up
                        // happening at once made visible.
                        |> Async.Parallel
                    outcomes <- fileProblems @ (rows |> Array.toList |> List.choose id)
                    // Say the refusals that are NEW. A start already announces itself, so
                    // without this the two outcomes of a declaration were split — one on the
                    // timeline, one behind a query nobody opens until they suspect a problem,
                    // which is the state a broken file leaves you least able to suspect.
                    //
                    // On change, never per fold: this runs after every repo verb, so a note
                    // per outcome would be the accumulation the query was chosen to avoid in
                    // the first place, rebuilt on the surface it was avoided for.
                    let! page = log.Read None System.Int32.MaxValue
                    let told = page.Events |> List.map (fun e -> e.Event)
                    for outcome in outcomes do
                        match outcome.Problem with
                        | None -> ()
                        | Some reason when lastRefusal told outcome.Repo outcome.Sandbox = Some reason -> ()
                        | Some reason ->
                            // Attributed to the file, like the starts it did not produce.
                            // `onBehalfOf` is whoever triggered the fold, and it is not
                            // borrowed here: nothing is being done on their authority — this
                            // is the file reporting that nothing was.
                            let actor = ActorRef.Configured outcome.Repo
                            do!
                                append
                                    actor
                                    (SessionEvent.RepoConfigRefused
                                        { RepoConfigRefused.MessageId = mintMessageId ()
                                          RepoConfigRefused.Repo = outcome.Repo
                                          RepoConfigRefused.Sandbox = outcome.Sandbox
                                          RepoConfigRefused.Reason = reason
                                          RepoConfigRefused.Actor = actor
                                          RepoConfigRefused.CausedBy = FoldCause.causeFor outcome.Repo cause })
                    // What the analyzers had to say, each said ONCE: a note already in the log
                    // is not news on the next fold, and re-announcing it every time anybody
                    // touched a repo would train people to stop reading them.
                    for repo, located in read.Findings do
                        let sandbox = located.Finding.Sandbox |> Option.map (SandboxRef.inScope repo)
                        let where = LocatedFinding.where located
                        if not (alreadyNoted told repo where located.Finding.Message) then
                            let actor = ActorRef.Configured repo
                            do!
                                append
                                    actor
                                    (SessionEvent.RepoConfigWarned
                                        { RepoConfigWarned.MessageId = mintMessageId ()
                                          RepoConfigWarned.Repo = repo
                                          RepoConfigWarned.Sandbox = sandbox
                                          RepoConfigWarned.Where = where
                                          RepoConfigWarned.Warning = located.Finding.Message
                                          RepoConfigWarned.Actor = actor
                                          RepoConfigWarned.CausedBy = FoldCause.causeFor repo cause })
        }

    // One fold at a time. Folding is idempotent by construction, but only ONE AT A TIME:
    // `start_work_sandbox` decides "is this already running" before it starts anything, and
    // a start can be a container to pull, so two folds in flight at once would both find
    // nothing running and both start it. That was latent while a fold could only overlap
    // an approval; it became routine once a person's arrival folds, because the person
    // arrives seconds after the boot fold began. A fold that finds one running waits its
    // turn, and reads a registry the earlier one has already filled.
    let mutable folding = false
    let waiting = System.Collections.Generic.Queue<unit -> unit> ()

    let fold (cause: FoldCause) (onBehalfOf: CredentialFor) : Async<unit> =
        async {
            if folding then
                do! Async.FromContinuations (fun (cont, _, _) -> waiting.Enqueue cont)
            folding <- true
            try
                do! foldOnce cause onBehalfOf
            finally
                // Hand straight to the next in line, so `folding` never reads false with a
                // fold about to start; it is the last one out that clears it.
                if waiting.Count > 0 then waiting.Dequeue () ()
                else folding <- false
        }

    /// A person's answer to a repo's trust card.
    ///
    /// Written to their own record first, so their answer holds in every session they open the
    /// repo in, whatever happens here. Then the fold runs on their authority with their trust
    /// OFFERED: a repo that nobody's trust runs here yet starts with their GitHub when their
    /// answer covers what it asks. A repo that already runs with somebody's keeps them.
    ///
    /// A PERSON may answer, and `Principal` is exactly that: the agent, a process and a repo's
    /// own file have no GitHub to trust anything with, and there is no call to write that asks
    /// them. A checkout that could vouch for itself is one nobody is deciding about.
    let trust (person: Principal) (repo: RepoRef) (shown: ConnectionRoute list) (ticked: ConnectionRoute list) : Async<Result<unit, string>> =
        async {
            match! trusts.Answer (RepoTrust.ownerOf person) repo (Set.ofList shown) (Set.ofList ticked) with
            | Error reason -> return Error reason
            | Ok _ ->
                do! fold (FoldCause.Offered (repo, None, person)) (CredentialFor.Person person)
                return Ok ()
        }

    let undeclared () =
        // Only declarations that are the whole truth can say a sandbox is no longer declared:
        // against a file the session could not read, every running sandbox would read as
        // abandoned.
        match declarations with
        | NotYetRead
        | Unreadable _ -> []
        | Read known ->
            (sandboxes ()).Listed ()
            |> List.map (fun entry -> entry.Ref)
            // Only a REPO's sandbox can stop being declared. The session's own were never
            // declared by a file, so they are not undeclared by one either.
            |> List.filter (fun ref ->
                match SandboxRef.scope ref with
                | SessionOwned -> false
                | RepoOwned _ -> not (Map.containsKey ref known))

    let declaredAs (ref: SandboxRef) = Map.tryFind ref (Declarations.known declarations)

    { Fold = fold
      Outcomes = fun () -> outcomes
      Undeclared = undeclared
      Declared = fun () -> declarations
      Described = fun ref -> declaredAs ref |> Option.bind (fun decl -> decl.Description)
      Trust = trust }

// --- the `repo_config` query ------------------------------------------------------------

let queryName : QueryName = QueryName.create "repo_config" |> Result.defaultWith failwith

let private queryDef : QueryDef =
    { Name = queryName
      Title = "repo config"
      Description =
        "What each checkout's yession.yaml asked this session for, and the result. A row with \
         no problem is a declaration that became a sandbox. A row that names a problem is a \
         declaration that did not become a sandbox, and the row says why. A sandbox listed as \
         no longer declared is still running. Only stop_work_sandbox removes it. A fold never does."
      Shape =
        Rows
            [ QueryColumn.create "repo" "repo"
              QueryColumn.create "sandbox" "sandbox"
              QueryColumn.create "state" "state"
              QueryColumn.create "problem" "problem" ]
      Legend = [] }

/// Register the fold's answer as a query.
///
/// A QUERY and not a stream of notes, and the difference is the accumulation. A fold runs
/// at boot and after every repo verb, so a note per outcome would write the same sentence
/// on every trigger — which is the thing Plan 17's declaration-delta rule exists to stop.
/// The starts that DID happen already have act-lines of their own, once each, because
/// `WorkSandboxes.ensure` records nothing on a repeat. What is left is live state, and live
/// state belongs in a query where a re-fold costs nothing and a restart shows the truth.
let query (current: unit -> RepoSandboxes) : Queries.QueryRegistration =
    { Def = queryDef
      Read =
        fun () ->
            async {
                let folded = current ()
                // First, when there is one: why the rows below are not the whole truth. A
                // fold that could not list the checkouts used to leave this query answering
                // `(none)`, the same answer as a session whose files declare nothing.
                let unread =
                    match folded.Declared () with
                    | Read _ -> []
                    | unread ->
                        [ [ "repo", CellAbsent
                            "sandbox", CellAbsent
                            "state",
                            CellText (
                                match unread with
                                | NotYetRead -> "not read yet"
                                | Unreadable (_, Some _) -> "stale"
                                | Unreadable (_, None)
                                | Read _ -> "unreadable")
                            "problem",
                            (match Declarations.whyNotCurrent unread with
                             | Some why -> CellText why
                             | None -> CellAbsent) ] ]
                let declarations =
                    folded.Outcomes ()
                    |> List.map (fun outcome ->
                        [ "repo", CellText (RepoRef.value outcome.Repo)
                          "sandbox",
                          (match outcome.Sandbox with
                           | Some sandbox -> CellText (SandboxRef.render sandbox)
                           | None -> CellAbsent)
                          "state",
                          CellText (
                              match outcome.Sandbox, outcome.Problem with
                              | None, _ -> "unreadable"
                              | Some _, None -> "declared"
                              | Some _, Some _ -> "not started")
                          "problem",
                          (match outcome.Problem with
                           | Some problem -> CellText problem
                           | None -> CellAbsent) ])
                let orphans =
                    folded.Undeclared ()
                    |> List.map (fun sandbox ->
                        [ "repo",
                          (match SandboxRef.scope sandbox with
                           | RepoOwned repo -> CellText (RepoRef.value repo)
                           | SessionOwned -> CellAbsent)
                          "sandbox", CellText (SandboxRef.render sandbox)
                          "state", CellText "no longer declared"
                          "problem", CellAbsent ])
                return Ok (RowsOf (unread @ declarations @ orphans))
            } }
