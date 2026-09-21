module Yession.Tests.WorkSandboxes

// The session's named WorkSandboxes (Plan 15, stage 2). What is worth pinning is the
// ENSURE contract and the credential-handling rules, because those are what the
// declarative form and the shared trust boundary respectively rest on:
//
//   * the same ask twice is the same sandbox, and records nothing — otherwise folding a
//     file into these commands at every boot accumulates instead of converging;
//   * a DIFFERENT ask is refused rather than silently recreated, because recreating kills
//     whatever is running inside;
//   * what a forwarded credential provisions reaches the sandbox's environment and NOTHING
//     else — the event carries the names and whose, and cannot carry a value — and it is
//     taken back when the sandbox stops.

open System
open Fable.Pyxpecto
open Yession.Domain
open Yession.Domain.Repos
open Yession.Domain.Sandboxes
open Yession.Domain.Agent
open Yession.Domain.Tools
open Yession.Domain.Chat
open Yession.Domain.Terminals
open Yession.Host
open Yession.SessionProcess

let private expect result =
    match result with
    | Ok v -> v
    | Error e -> failwithf "invariant: %A" e

let private sessionId = SessionId.create "sess-sandboxes" |> expect
let private ada = UserRef (UserId.create "ada" |> expect)
/// Ada as a credential is lent on: the same person, where the type asks for a principal.
let private fixedClock () = DateTimeOffset (2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
let private newLog () : EventLog<SessionEvent> = InMemoryEventLog.create sessionId fixedClock

let private sandbox (raw: string) = SandboxRef.parse raw |> expect

/// What an act note said beyond its headline. A reader rather than a match at every call
/// site: the split is the thing under test in several cases here, and a case that has to
/// destructure a union to ask its question reads as being about the union.
let private noteDetail (item: ConversationItem) : string option =
    match item.Content with
    | ItemContent.Act act ->
        match Act.particulars act with
        | [] -> None
        | particulars -> Some (particulars |> List.map Phrase.said |> String.concat "; ")
    | ItemContent.Message _
    | ItemContent.Stopped _ -> None

let private isAct (item: ConversationItem) : bool =
    match item.Content with
    | ItemContent.Act _ -> true
    | ItemContent.Message _
    | ItemContent.Stopped _ -> false

/// The ask most of these cases make: nothing in particular about the sandbox, some
/// credentials forwarded into it. The spec half has its own cases below.
let private forwarding (names: string list) : SandboxRequest =
    { SandboxRequest.defaults with Forward = ConnectionName.normalise names }

/// The one connection these cases forward, as a name.
let private github : ConnectionName = ConnectionName.create "github" |> expect

/// An ask that differs from the default in the SPEC rather than the forwarding — the half
/// only a declared sandbox has ever been able to name.
let private workingIn (dir: string) : SandboxRequest =
    { SandboxRequest.defaults with
        Spec = { EnvironmentSpec.defaults with WorkingDirectory = Some dir } }

/// A stand-in environment that records nothing but whether it is up. The registry's
/// contract is about WHICH environments exist and what they were built with, so a real
/// sandbox would only add a process to the test.
let private fakeEnvironmentHolding (realisation: string list) =
    let mutable running = false
    ({ Ensure = fun _ _ -> async { running <- true; return EnvironmentAvailable }
       Spawn = fun _ _ -> async { return Error "not under test" }
       SpawnPty = fun _ _ _ _ -> async { return Error "not under test" }
       Stop = fun () -> async { running <- false }
       CurrentRef = fun () -> if running then Some "fake" else None
       Shell = fun () -> None
       // Gated on `running` like `CurrentRef` beside it, because the real one is: what a
       // sandbox holds is a fact about a sandbox that exists. A fake that answered either way
       // would let the listing test pass while the panel spoke for a sandbox that had gone.
       Realisation = fun () -> if running then realisation else [] }
     : SessionEnvironment.SessionEnvironment)

let private fakeEnvironment () = fakeEnvironmentHolding []

/// An environment that refuses to come up — the container failed, or failed its own checks.
let private fakeEnvironmentFailing (reason: string) : SessionEnvironment.SessionEnvironment =
    { Ensure = fun _ _ -> async { return EnvironmentUnavailable reason }
      Spawn = fun _ _ -> async { return Error reason }
      SpawnPty = fun _ _ _ _ -> async { return Error reason }
      Stop = fun () -> async { return () }
      CurrentRef = fun () -> None
      Shell = fun () -> None
      Realisation = fun () -> [] }

/// A registry over fake environments, plus the record of what each was BUILT with — which
/// is where a forwarded credential would have to appear, and the only place it may.
let private registryWithSpecs (log: EventLog<SessionEvent>) (credentials: WorkSandboxes.CredentialSource list) =
    let built = ResizeArray<string * WorkSandboxes.Provision> ()
    let specs = ResizeArray<string * string option> ()
    let sandboxes =
        WorkSandboxes.create
            { Backend = fun _ -> "fake"
              Describe = fun _ -> None
              Checkout = fun _ -> None
              Credentials = credentials
              Create =
                fun name spec provision ->
                    built.Add (SandboxRef.render name, provision)
                    specs.Add (SandboxRef.render name, spec.WorkingDirectory)
                    Ok (fakeEnvironment ())
              Log = log
              Clock = fixedClock }
        |> expect
    sandboxes, built, specs

/// A registry whose sandboxes all come up holding something wider than they asked for —
/// what a host that could not scope a grant hands back.
let private registryHolding (log: EventLog<SessionEvent>) (realisation: string list) =
    WorkSandboxes.create
        { Backend = fun _ -> "fake"
          Describe = fun _ -> None
          Checkout = fun _ -> None
          Credentials = []
          Create = fun _ _ _ -> Ok (fakeEnvironmentHolding realisation)
          Log = log
          Clock = fixedClock }
    |> expect

/// The same, for the cases that only care about the environment a sandbox was built with.
let private registry (log: EventLog<SessionEvent>) (credentials: WorkSandboxes.CredentialSource list) =
    let sandboxes, built, _ = registryWithSpecs log credentials
    sandboxes, built

let private caller : ActorRef = ActorRef.Agent

/// A source that provisions the given route into any sandbox. Records what it gave and what
/// it was asked to take back, which is the pair the revoke cases compare.
let private githubSource (route: string) : WorkSandboxes.CredentialSource * ResizeArray<string> =
    let revoked = ResizeArray<string> ()
    { Name = github
      Provision =
        fun _ ->
            async {
                return
                    WorkSandboxes.CredentialForwarding.Forwarded
                        { Env = Map.ofList [ "GITHUB_ROUTE", route ]; GitConfig = []; Domains = [] }
            }
      Revoke = fun ref -> revoked.Add (SandboxRef.render ref)
      // Lends by NAME: what a block gets says whose credential it was asked for, which
      // is the whole of what the loan cases compare.
      Lend =
        fun authority _ _ _ ->
            async {
                return
                    { BlockEnv.GitConfig = None
                      BlockEnv.Vars = [ "LENT_TO", Some (CredentialFor.token (Authority.credential authority)) ] }
            }
      Retire = ignore },
    revoked

let private githubCredential (route: string) : WorkSandboxes.CredentialSource = fst (githubSource route)

let private eventsOf (log: EventLog<SessionEvent>) =
    async {
        let! page = log.Read None 1000
        return page.Events |> List.map (fun e -> e.Event)
    }

let private startedEvents (events: SessionEvent list) =
    events |> List.choose (function SessionEvent.WorkSandboxStarted s -> Some s | _ -> None)

/// The sandbox-lifecycle events in the order the log holds them, each with its MessageId — so
/// a test can assert the running act opens BEFORE the slow work and that the start or failure
/// resolves that same id.
let private lifecycleOf (events: SessionEvent list) =
    events
    |> List.choose (function
        | SessionEvent.WorkSandboxStarting s -> Some ("starting", s.MessageId)
        | SessionEvent.WorkSandboxStarted s -> Some ("started", s.MessageId)
        | SessionEvent.WorkSandboxStartFailed s -> Some ("failed", s.MessageId)
        | _ -> None)

// --- names --------------------------------------------------------------------------------

let private nameTests =
    testList "a sandbox name" [
        testCase "accepts what a container name, a directory and a label all survive" <| fun () ->
            for raw in [ "default"; "test"; "build-2"; "a_b"; "9" ] do
                Expect.isTrue (Result.isOk (SandboxName.create raw)) (sprintf "'%s' is a name" raw)
            // Leading punctuation, uppercase, spaces and separators are refused because a
            // name reaches a docker object name and a filesystem path, and escaping at
            // three call sites is three chances to forget.
            for raw in [ ""; "-test"; "_test"; "Test"; "my sandbox"; "a/b"; "a.b"; "../x" ] do
                Expect.isTrue (Result.isError (SandboxName.create raw)) (sprintf "'%s' is not a name" raw)

        testCase "the default is the one every session has always had" <| fun () ->
            Expect.equal (SandboxRef.render SandboxRef.defaultRef) "default" "named, not implicit"
    ]

let private normaliseTests =
    testList "forwarding lists" [
        // Two asks that MEAN the same thing must compare equal, or the second is refused
        // as a configuration change for a difference nobody made.
        testCase "normalise so an equivalent ask is an equal ask" <| fun () ->
            Expect.equal
                (ConnectionName.normalise [ " GitHub "; "github"; ""; "aws" ] |> List.map ConnectionName.value)
                [ "aws"; "github" ]
                "trimmed, lowercased, deduped, sorted"
            Expect.equal (ConnectionName.normalise []) [] "nothing stays nothing"
    ]

// --- the ensure contract --------------------------------------------------------------------

let private ensureTests =
    testList "ensure semantics" [

        // The whole point of the running act: it opens BEFORE the slow work — creating,
        // starting and verifying the container — not after, so the timeline shows the sandbox
        // coming up rather than dead air until it is already up. The start resolves that same
        // item, which is why the two carry ONE MessageId.
        testCaseAsync "a sandbox coming up records starting before started, under one id" <|
            async {
                let log = newLog ()
                let sandboxes, _ = registry log []
                let! _ = sandboxes.Ensure caller (sandbox "test") (forwarding [])
                let! events = eventsOf log
                match lifecycleOf events with
                | [ ("starting", opened); ("started", resolved) ] ->
                    Expect.equal opened resolved "the start resolves the very item the running act opened"
                | other -> failwithf "expected starting then started under one id, got %A" other
            }

        // A start that fails resolves the running act to a failure — never leaves it spinning
        // — and records no start.
        testCaseAsync "a sandbox that cannot come up records starting then failed, and no start" <|
            async {
                let log = newLog ()
                let sandboxes =
                    WorkSandboxes.create
                        { Backend = fun _ -> "fake"
                          Describe = fun _ -> None
                          Checkout = fun _ -> None
                          Credentials = []
                          Create = fun _ _ _ -> Ok (fakeEnvironmentFailing "the docker daemon is not reachable")
                          Log = log
                          Clock = fixedClock }
                    |> expect
                match! sandboxes.Ensure caller (sandbox "test") (forwarding []) with
                | Error reason -> Expect.equal reason "the docker daemon is not reachable" "the ask fails with why"
                | Ok _ -> failwith "a sandbox whose environment cannot come up must not report success"
                let! events = eventsOf log
                match lifecycleOf events with
                | [ ("starting", opened); ("failed", resolved) ] ->
                    Expect.equal opened resolved "the failure resolves the very item the running act opened"
                | other -> failwithf "expected starting then failed under one id, got %A" other
            }

        // What a sandbox is FOR reaches the record, so a reader choosing between two of them
        // chooses on the reason rather than the spelling.
        testCaseAsync "a sandbox that was described says so where it started" <|
            async {
                let log = newLog ()
                let sandboxes =
                    WorkSandboxes.create
                        { Backend = fun _ -> "fake"
                          Describe = fun _ -> Some "day-to-day work"
                          Checkout = fun _ -> None
                          Credentials = []
                          Create = fun _ _ _ -> Ok (fakeEnvironment ())
                          Log = log
                          Clock = fixedClock }
                    |> expect
                let! _ = sandboxes.Ensure caller (sandbox "test") (forwarding [])
                let! events = eventsOf log
                match startedEvents events with
                | [ started ] -> Expect.equal started.Description (Some "day-to-day work") "the reason travels with the start"
                | other -> failwithf "expected one start, got %d" (List.length other)
            }

        // The fault this exists for: one checkout has two addresses and only a sandbox
        // settles which. Told the host's by `add_repo`, an agent pointed a shell profile at
        // it INSIDE the container, watched it silently not take, and spent six calls working
        // out why. The start is where both halves are known at once.
        testCaseAsync "a repo's sandbox says where it sees the checkout" <|
            async {
                let log = newLog ()
                let sandboxes =
                    WorkSandboxes.create
                        { Backend = fun _ -> "fake"
                          Describe = fun _ -> None
                          Checkout = fun _ -> Some "/repos/owner/name"
                          Credentials = []
                          Create = fun _ _ _ -> Ok (fakeEnvironment ())
                          Log = log
                          Clock = fixedClock }
                    |> expect
                let! _ = sandboxes.Ensure caller (sandbox "test") (forwarding [])
                let! events = eventsOf log
                match startedEvents events with
                | [ started ] -> Expect.equal started.Checkout (Some "/repos/owner/name") "the address travels with the start"
                | other -> failwithf "expected one start, got %d" (List.length other)
            }

        // The other half, and a different layer: the address has to REACH a reader. A field on
        // an event nothing renders is a field nobody has.
        testCase "the note a start writes says where the checkout is" <| fun () ->
            let started =
                SessionEvent.WorkSandboxStarted
                    { MessageId = MessageId.create "m-1" |> expect
                      Sandbox = SandboxRef.parse "owner/name:dev" |> expect
                      Backend = "docker"
                      Description = Some "day-to-day work"
                      Checkout = Some "/repos/owner/name"
                      Forwarded = []
                      Realisation = []
                      Actor = ActorRef.Agent }
            let envelope : EventEnvelope<SessionEvent> =
                { EventId = EventId.fresh ()
                  SessionId = SessionId.create "notes" |> expect
                  Offset = EventOffset.create 0L |> expect
                  Actor = ActorRef.Agent
                  Timestamp = System.DateTimeOffset.UtcNow
                  Event = started }
            let projection, _ =
                ConversationProjection.applyEvents None [ envelope ] ConversationProjection.empty
            match projection.Items with
            | [ item ] -> Expect.stringContains (ConversationItem.said item) "/repos/owner/name" "a reader is told where the work is"
            | other -> failwithf "expected one note, got %d" (List.length other)

        // The hazard this shape exists to avoid. A description is metadata, never part of what
        // makes two asks the same sandbox — so editing prose in a repo's file must not read as
        // a configuration change and refuse every session until somebody stops the container.
        // It would, if the description rode inside `SandboxRequest`, which `Ensure` compares.
        testCaseAsync "a sandbox re-described is the same sandbox, not a changed one" <|
            async {
                let log = newLog ()
                let described = ref (Some "as first written")
                let sandboxes =
                    WorkSandboxes.create
                        { Backend = fun _ -> "fake"
                          Describe = fun _ -> described.Value
                          Checkout = fun _ -> None
                          Credentials = []
                          Create = fun _ _ _ -> Ok (fakeEnvironment ())
                          Log = log
                          Clock = fixedClock }
                    |> expect
                let! _ = sandboxes.Ensure caller (sandbox "test") (forwarding [])
                described.Value <- Some "as somebody later put it"
                match! sandboxes.Ensure caller (sandbox "test") (forwarding []) with
                | Error reason -> failwithf "re-describing is not a configuration change: %s" reason
                | Ok outcome ->
                    match outcome with
                    | WorkSandboxes.SandboxAlreadyRunning _ -> ()
                    | other -> failwithf "expected the same sandbox, got %A" other
            }

        // THE property the declarative form rests on. Ask twice, get the same sandbox, and
        // the timeline does not claim anything happened the second time.
        testCaseAsync "the same ask twice is the same sandbox, recorded once" <|
            async {
                let log = newLog ()
                let sandboxes, built = registry log []
                let! first = sandboxes.Ensure caller (sandbox "test") (forwarding [])
                let! second = sandboxes.Ensure caller (sandbox "test") (forwarding [])
                let one = expect first
                let two = expect second
                Expect.equal
                    (WorkSandboxes.SandboxOutcome.sandbox one).Ref
                    (WorkSandboxes.SandboxOutcome.sandbox two).Ref
                    "the same sandbox comes back"
                // And the answer says which ask put it there. A consequence that belongs
                // only to the start — a repo's `setup:` — reads this rather than firing
                // again on every re-ask, and the fold re-asks after every repo verb.
                match one, two with
                | WorkSandboxes.SandboxStarted _, WorkSandboxes.SandboxAlreadyRunning _ -> ()
                | _ -> failwith "the first ask started it and the second found it running"
                Expect.equal
                    (built |> Seq.filter (fun (name, _) -> name = "test") |> Seq.length)
                    1
                    "it was built once"
                let! events = eventsOf log
                Expect.equal (List.length (startedEvents events)) 1 "the second ask recorded nothing"
            }

        // Equivalent-but-differently-spelled forwarding is the SAME ask, which is what
        // normalisation is for; this pins that the registry uses it.
        testCaseAsync "an equivalent forwarding list is the same ask" <|
            async {
                let log = newLog ()
                let sandboxes, _ = registry log [ githubCredential "tok" ]
                let! _ = sandboxes.Ensure caller (sandbox "test") (forwarding [ "github" ])
                let! again = sandboxes.Ensure caller (sandbox "test") (forwarding [ " GitHub "; "github" ])
                Expect.isTrue (Result.isOk again) "it is not a configuration change"
                let! events = eventsOf log
                Expect.equal (List.length (startedEvents events)) 1 "still recorded once"
            }

        // The refusal, and why it is a refusal: a sandbox has processes in it. Converging
        // by killing somebody's build is not convergence.
        testCaseAsync "a different forwarding is refused, naming both sides" <|
            async {
                let log = newLog ()
                let sandboxes, built = registry log [ githubCredential "tok" ]
                let! _ = sandboxes.Ensure caller (sandbox "test") (forwarding [])
                match! sandboxes.Ensure caller (sandbox "test") (forwarding [ "github" ]) with
                | Ok _ -> failwith "expected a refusal"
                | Error e ->
                    Expect.isTrue (e.Contains "github") "it names what was asked for"
                    Expect.isTrue (e.Contains "nothing") "and what is running"
                    Expect.isTrue (e.Contains "stop_work_sandbox") "and how to actually change it"
                Expect.equal
                    (built |> Seq.filter (fun (name, _) -> name = "test") |> Seq.length)
                    1
                    "nothing was recreated behind the refusal"
            }

        // The forwarding was only ever the FIRST field of an ask. Once a repo can declare
        // what a sandbox is, "the same ask" has to mean the same everywhere, or a file that
        // changed its workdir would be silently answered with the old sandbox.
        testCaseAsync "a different spec is refused too, naming which part differs" <|
            async {
                let log = newLog ()
                let sandboxes, built = registry log []
                let! _ = sandboxes.Ensure caller (sandbox "test") (workingIn "/a")
                match! sandboxes.Ensure caller (sandbox "test") (workingIn "/b") with
                | Ok _ -> failwith "expected a refusal"
                | Error e ->
                    Expect.isTrue (e.Contains "/a") "it names what is running"
                    Expect.isTrue (e.Contains "/b") "and what was asked for"
                    Expect.isTrue (e.Contains "stop_work_sandbox") "and how to actually change it"
                Expect.equal
                    (built |> Seq.filter (fun (name, _) -> name = "test") |> Seq.length)
                    1
                    "nothing was recreated behind the refusal"
            }

        // The seam a declared sandbox rides: what was asked for has to reach the thing that
        // BUILDS it, or the file would be read, recorded, refused against — and ignored.
        testCaseAsync "the spec that was asked for is what the sandbox is built from" <|
            async {
                let log = newLog ()
                let sandboxes, _, specs = registryWithSpecs log []
                let! _ = sandboxes.Ensure caller (sandbox "test") (workingIn "/somewhere")
                Expect.equal
                    (specs |> Seq.filter (fun (name, _) -> name = "test") |> Seq.map snd |> List.ofSeq)
                    [ Some "/somewhere" ]
                    "the working directory reached the factory"
            }

        // Stop-then-start is the documented way to change forwarding, so it has to work.
        testCaseAsync "stopping releases the name, and the next start may differ" <|
            async {
                let log = newLog ()
                let sandboxes, _ = registry log [ githubCredential "tok" ]
                let! _ = sandboxes.Ensure caller (sandbox "test") (forwarding [])
                let! stopped = sandboxes.Stop caller (sandbox "test")
                Expect.isTrue (Result.isOk stopped) "it stops"
                let! restarted = sandboxes.Ensure caller (sandbox "test") (forwarding [ "github" ])
                Expect.equal
                    (WorkSandboxes.SandboxOutcome.sandbox (expect restarted)).Request.Forward
                    [ github ]
                    "the new configuration takes"
                let! events = eventsOf log
                Expect.equal (List.length (startedEvents events)) 2 "both starts are recorded"
            }

        // `default` is the sandbox a terminal that names nothing lands in, so it must
        // survive being stopped as a NAME even though its environment goes down.
        testCaseAsync "stopping default keeps the name reachable" <|
            async {
                let log = newLog ()
                let sandboxes, _ = registry log []
                let! _ = sandboxes.Ensure caller SandboxRef.defaultRef (forwarding [])
                let! _ = sandboxes.Stop caller SandboxRef.defaultRef
                match! (sandboxes.EnvironmentFor SandboxRef.defaultRef).Ensure None "a terminal was opened" with
                | EnvironmentAvailable -> ()
                | EnvironmentUnavailable reason -> failwithf "default should still be reachable: %s" reason
            }

        testCaseAsync "stopping a sandbox the session does not have is an error, not a no-op" <|
            async {
                let log = newLog ()
                let sandboxes, _ = registry log []
                match! sandboxes.Stop caller (sandbox "nope") with
                | Ok () -> failwith "expected an error"
                | Error e -> Expect.isTrue (e.Contains "nope") "it names what was asked for"
            }

        // A terminal has to be told no in the same shape it is told anything else, so an
        // unknown name resolves to an environment that refuses with the reason rather than
        // to a null the caller has to handle differently.
        testCaseAsync "an unknown name resolves to an environment that refuses with the reason" <|
            async {
                let log = newLog ()
                let sandboxes, _ = registry log []
                match! (sandboxes.EnvironmentFor (sandbox "ghost")).Ensure None "a terminal was opened" with
                | EnvironmentAvailable -> failwith "expected a refusal"
                | EnvironmentUnavailable reason ->
                    Expect.isTrue (reason.Contains "ghost") "it names the sandbox"
                    Expect.isTrue (reason.Contains "start_work_sandbox") "and how to get one"
            }

        // What an agent writes after reading "started sandbox octo/hello:dev" is `dev`, and
        // there is nothing else it could mean. Three sessions were told to start one instead.
        testCaseAsync "a bare name finds the one repo sandbox that carries it" <|
            async {
                let log = newLog ()
                let sandboxes, _ = registry log []
                let! _ = sandboxes.Ensure caller (sandbox "octo/hello:dev") SandboxRequest.defaults
                match! (sandboxes.EnvironmentFor (sandbox "dev")).Ensure None "a terminal was opened" with
                | EnvironmentAvailable -> ()
                | EnvironmentUnavailable reason -> failwithf "expected octo/hello:dev to answer for 'dev': %s" reason
            }

        testCaseAsync "a bare name two repos both carry is refused, naming both" <|
            async {
                let log = newLog ()
                let sandboxes, _ = registry log []
                let! _ = sandboxes.Ensure caller (sandbox "octo/hello:dev") SandboxRequest.defaults
                let! _ = sandboxes.Ensure caller (sandbox "octo/world:dev") SandboxRequest.defaults
                match! (sandboxes.EnvironmentFor (sandbox "dev")).Ensure None "a terminal was opened" with
                | EnvironmentAvailable -> failwith "expected a refusal"
                | EnvironmentUnavailable reason ->
                    Expect.isTrue (reason.Contains "'octo/hello:dev'") "names the one"
                    Expect.isTrue (reason.Contains "'octo/world:dev'") "and the other"
            }

        // The refusal says what there IS. "start_work_sandbox creates one" to somebody
        // whose sandbox is running under a name they mis-spelt sends them to create a
        // second, which the repo's file then refuses.
        testCaseAsync "an unknown name is refused naming the sandboxes that exist" <|
            async {
                let log = newLog ()
                let sandboxes, _ = registry log []
                let! _ = sandboxes.Ensure caller (sandbox "octo/hello:gate") SandboxRequest.defaults
                match! (sandboxes.EnvironmentFor (sandbox "dev")).Ensure None "a terminal was opened" with
                | EnvironmentAvailable -> failwith "expected a refusal"
                | EnvironmentUnavailable reason ->
                    Expect.isTrue (reason.Contains "'default'") "the session's own"
                    Expect.isTrue (reason.Contains "'octo/hello:gate'") "and the repo's"
                match! sandboxes.Stop caller (sandbox "dev") with
                | Ok () -> failwith "expected a refusal"
                | Error reason -> Expect.isTrue (reason.Contains "'octo/hello:gate'") "a stop says the same"
            }

        testCaseAsync "a bare name stops the one repo sandbox that carries it" <|
            async {
                let log = newLog ()
                let sandboxes, _ = registry log []
                let! _ = sandboxes.Ensure caller (sandbox "octo/hello:dev") SandboxRequest.defaults
                let! stopped = sandboxes.Stop caller (sandbox "dev")
                expect stopped
                Expect.isFalse
                    (sandboxes.Listed () |> List.exists (fun entry -> entry.Ref = sandbox "octo/hello:dev"))
                    "octo/hello:dev is gone"
            }

        // What the sandbox HOLDS is asked of the sandbox. This manager never sees a policy —
        // it hands a spec to a backend and is told an environment came up — so the one thing
        // it could have done wrong here is compute an answer of its own, and the one thing
        // that makes the log worth reading is that it did not.
        testCaseAsync "a start records where this host could not give what the sandbox asked for" <|
            async {
                let log = newLog ()
                let sandboxes = registryHolding log [ "the socket at /run/docker.sock — this host cannot scope that, so the sandbox gets any unix socket on this host" ]
                let! _ = sandboxes.Ensure caller (sandbox "test") SandboxRequest.defaults
                let! events = eventsOf log
                match startedEvents events with
                | [ started ] ->
                    Expect.equal
                        started.Realisation
                        [ "the socket at /run/docker.sock — this host cannot scope that, so the sandbox gets any unix socket on this host" ]
                        "the environment's own answer, unedited"
                | other -> failwithf "expected one start, got %A" other
            }
    ]

// --- credentials ----------------------------------------------------------------------------

let private credentialTests =
    testList "named credential forwarding" [

        // The rule the shared trust boundary rests on: what a source provisions goes into
        // the sandbox's environment and nowhere else; the EVENT carries the names — and
        // nobody's, because a route is nobody's until a block spends its own act's on it.
        testCaseAsync "the provision reaches the sandbox env, and the event carries names only" <|
            async {
                let log = newLog ()
                let sandboxes, built = registry log [ githubCredential "ghp_secret" ]
                let! started = sandboxes.Ensure caller (sandbox "test") (forwarding [ "github" ])
                Expect.equal
                    (WorkSandboxes.SandboxOutcome.sandbox (expect started)).Request.Forward
                    [ github ]
                    "it forwards what was asked"

                let _, provision = built |> Seq.find (fun (name, _) -> name = "test")
                Expect.equal (Map.tryFind "GITHUB_ROUTE" provision.Env) (Some "ghp_secret") "the provision is in the sandbox env"

                let! events = eventsOf log
                match startedEvents events with
                | [ e ] ->
                    Expect.equal e.Forwarded [ github ] "the event names the credential"
                    Expect.equal e.Actor ActorRef.Agent "and the acting party"
                | other -> failwithf "expected one start, got %A" other

                // The load-bearing negative: nothing anywhere in the log is the token.
                // The event TYPE cannot carry one, and this is what keeps it that way.
                let rendered = events |> List.map (sprintf "%A") |> String.concat "\n"
                Expect.isFalse (rendered.Contains "ghp_secret") "no rendering of the log contains the value"
            }

        // A repo's file asking at boot asks for nobody, and the agent asking on a turn
        // asks for somebody who may have nothing connected. Neither is a reason not to
        // come up: the route is the sandbox's, and whose credential goes down it is each
        // block's — a person with nothing connected is refused at their push, in words.
        testCaseAsync "a sandbox that forwards github starts with nobody's credential named" <|
            async {
                let log = newLog ()
                let sandboxes, built = registry log [ githubCredential "route" ]
                let repo = RepoRef.create "octo/hello" |> expect
                let! started = sandboxes.Ensure (ActorRef.Configured repo) (sandbox "octo/hello:dev") (forwarding [ "github" ])
                match started with
                | Error e -> failwithf "expected the start, got: %s" e
                | Ok _ -> ()
                Expect.isTrue (built |> Seq.exists (fun (name, _) -> name = "octo/hello:dev")) "it was built"
                let! events = eventsOf log
                match startedEvents events with
                | [ e ] ->
                    Expect.equal e.Forwarded [ github ] "with the route"
                    Expect.equal e.Actor (ActorRef.Configured repo) "by the file"
                | other -> failwithf "expected one start, got %A" other
            }

        // The other half of forwarding: a provision is a thing the session OPENED (a gateway
        // route), and a route that outlives its sandbox is a route somebody else can use.
        testCaseAsync "stopping a sandbox takes back what was forwarded into it" <|
            async {
                let log = newLog ()
                let source, revoked = githubSource "tok"
                let sandboxes, _ = registry log [ source ]
                let! _ = sandboxes.Ensure caller (sandbox "test") (forwarding [ "github" ])
                Expect.equal (List.ofSeq revoked) [] "nothing taken back while it runs"
                let! stopped = sandboxes.Stop caller (sandbox "test")
                expect stopped
                Expect.equal (List.ofSeq revoked) [ "test" ] "the source was told to take it back, for that sandbox"
            }

        testCaseAsync "a sandbox that could not be built keeps nothing forwarded" <|
            async {
                let log = newLog ()
                let source, revoked = githubSource "tok"
                let sandboxes =
                    WorkSandboxes.create
                        { Backend = fun _ -> "fake"
                          Describe = fun _ -> None
                          Checkout = fun _ -> None
                          Credentials = [ source ]
                          Create = fun name _ _ -> if name = SandboxRef.defaultRef then Ok (fakeEnvironment ()) else Error "no room"
                          Log = log
                          Clock = fixedClock }
                    |> expect
                match! sandboxes.Ensure caller (sandbox "test") (forwarding [ "github" ]) with
                | Ok _ -> failwith "expected the build to refuse"
                | Error e -> Expect.equal e "no room" "the build's own reason"
                Expect.equal (List.ofSeq revoked) [ "test" ] "and the provision did not outlive the attempt"
            }

        // A source may have nowhere to put a credential in THIS sandbox — a backend with no
        // route to the gateway. That is neither "you have none" nor a silent start without.
        testCaseAsync "a credential that cannot reach this sandbox refuses the start with the reason" <|
            async {
                let log = newLog ()
                let source : WorkSandboxes.CredentialSource =
                    { Name = github
                      Provision = fun _ -> async { return WorkSandboxes.CredentialForwarding.Unforwardable "no route from here" }
                      Revoke = ignore
                      Lend = fun _ _ _ _ -> async { return BlockEnv.none }
                      Retire = ignore }
                let sandboxes, built = registry log [ source ]
                match! sandboxes.Ensure caller (sandbox "test") (forwarding [ "github" ]) with
                | Ok _ -> failwith "expected a refusal"
                | Error e -> Expect.equal e "no route from here" "the source's own words"
                Expect.isFalse (built |> Seq.exists (fun (name, _) -> name = "test")) "nothing was built"
            }

        testCaseAsync "a credential this session does not know is refused, naming the ones it does" <|
            async {
                let log = newLog ()
                let sandboxes, _ = registry log [ githubCredential "tok" ]
                match! sandboxes.Ensure caller (sandbox "test") (forwarding [ "gitlab" ]) with
                | Ok _ -> failwith "expected a refusal"
                | Error e ->
                    Expect.isTrue (e.Contains "gitlab") "it names what was asked for"
                    Expect.isTrue (e.Contains "github") "and what is available"
            }
    ]

// --- the query -------------------------------------------------------------------------------

let private queryTests =
    testList "the work_sandboxes query" [

        // Read from the RUNNING sandbox, not from what the registry was told — the rule
        // the repos listing follows, for the same reason.
        testCaseAsync "reports process truth, and the forwarding by name" <|
            async {
                let log = newLog ()
                let sandboxes, _ = registry log [ githubCredential "ghp_secret" ]
                let registration = WorkSandboxes.query (fun () -> sandboxes)

                match! registration.Read () with
                | Error e -> failwithf "the query failed: %s" e
                | Ok (RowsOf [ row ]) ->
                    Expect.equal (row |> List.tryFind (fst >> (=) "name") |> Option.map snd) (Some (CellText "default")) "default is there from boot"
                    Expect.equal (row |> List.tryFind (fst >> (=) "state") |> Option.map snd) (Some (CellText "not started")) "and it has not started"
                | Ok other -> failwithf "expected one row, got %A" other

                let! _ = sandboxes.Ensure caller (sandbox "test") (forwarding [ "github" ])
                match! registration.Read () with
                | Error e -> failwithf "the query failed: %s" e
                | Ok (RowsOf rows) ->
                    Expect.equal (List.length rows) 2 "both sandboxes are listed"
                    let test = rows |> List.find (fun row -> row |> List.contains ("name", CellText "test"))
                    Expect.equal
                        (test |> List.tryFind (fst >> (=) "forwarding") |> Option.map snd)
                        (Some (CellText "github"))
                        "the forwarding is named"
                    let rendered = sprintf "%A" rows
                    Expect.isFalse (rendered.Contains "ghp_secret") "and the value is not in the answer"
                | Ok other -> failwithf "expected rows, got %A" other
            }

        // The listing is where a person finds out that a sandbox in their session was asked
        // for by a repository rather than by them — which is the whole reason to look at
        // what it runs.
        testCaseAsync "a repo's sandbox says which repo declared it, and the session's says nothing" <|
            async {
                let log = newLog ()
                let sandboxes, _ = registry log []
                let registration = WorkSandboxes.query (fun () -> sandboxes)
                let declared =
                    SandboxRef.inScope (RepoRef.create "octo/hello" |> expect) (SandboxName.create "dev" |> expect)
                let! _ = sandboxes.Ensure caller declared SandboxRequest.defaults
                match! registration.Read () with
                | Error e -> failwithf "the query failed: %s" e
                | Ok (RowsOf rows) ->
                    let cell name row = row |> List.tryFind (fst >> (=) name) |> Option.map snd
                    let repos =
                        rows |> List.find (fun row -> cell "name" row = Some (CellText "octo/hello:dev"))
                    let own = rows |> List.find (fun row -> cell "name" row = Some (CellText "default"))
                    Expect.equal (cell "declared_by" repos) (Some (CellText "octo/hello")) "the repo that asked"
                    Expect.equal (cell "declared_by" own) (Some CellAbsent) "the session's own was asked for by nobody"
                | Ok other -> failwithf "expected rows, got %A" other
            }

        // The panel's half of the degraded story. A sandbox that came up holding something
        // wider than its resources named says so where somebody looking at their session can
        // see it, without reading the log back.
        testCaseAsync "a running sandbox says in the listing where it holds more than was asked" <|
            async {
                let log = newLog ()
                let sandboxes = registryHolding log [ "the socket at /run/docker.sock — this host cannot scope that, so the sandbox gets any unix socket on this host" ]
                let registration = WorkSandboxes.query (fun () -> sandboxes)
                let! _ = sandboxes.Ensure caller (sandbox "test") SandboxRequest.defaults
                match! registration.Read () with
                | Error e -> failwithf "the query failed: %s" e
                | Ok (RowsOf rows) ->
                    let test = rows |> List.find (fun row -> row |> List.contains ("name", CellText "test"))
                    match test |> List.tryFind (fst >> (=) "degraded") |> Option.map snd with
                    | Some (CellText said) ->
                        Expect.isTrue (said.Contains "/run/docker.sock") (sprintf "the grant is named, said: %s" said)
                        Expect.isTrue (said.Contains "any unix socket") (sprintf "and what it became, said: %s" said)
                    | other -> failwithf "expected the widening, got %A" other
                | Ok other -> failwithf "expected rows, got %A" other
            }

        // And the column is EMPTY the rest of the time, which is what makes the case above
        // worth looking at. A sandbox nobody started holds nothing at all.
        testCaseAsync "a sandbox that has not started claims no widening" <|
            async {
                let log = newLog ()
                let sandboxes = registryHolding log [ "the socket at /run/docker.sock — this host cannot scope that, so the sandbox gets any unix socket on this host" ]
                let registration = WorkSandboxes.query (fun () -> sandboxes)
                match! registration.Read () with
                | Error e -> failwithf "the query failed: %s" e
                | Ok (RowsOf [ row ]) ->
                    Expect.equal
                        (row |> List.tryFind (fst >> (=) "degraded") |> Option.map snd)
                        (Some CellAbsent)
                        "nothing runs, so nothing is claimed"
                | Ok other -> failwithf "expected one row, got %A" other
            }

        testCaseAsync "the answer fits the shape the query declares" <|
            async {
                let log = newLog ()
                let sandboxes, _ = registry log []
                let registration = WorkSandboxes.query (fun () -> sandboxes)
                match! registration.Read () with
                | Error e -> failwithf "the query failed: %s" e
                | Ok value ->
                    Expect.isTrue (QueryValue.fits registration.Def.Shape value) "the registry would accept it"
            }
    ]

// --- the timeline ------------------------------------------------------------------------------

let private timelineTests =
    testList "sandbox act-lines" [

        // The line says what was forwarded, never what the value was — and nobody's name,
        // because a route is nobody's: whose credential went down it is said per push.
        testCase "a forwarding start reads as a sentence naming the credential" <| fun () ->
            let messageId = MessageId.create "msg-1" |> expect
            let envelope : EventEnvelope<SessionEvent> =
                { EventId = EventId.fresh ()
                  SessionId = sessionId
                  Offset = EventOffset.create 1L |> expect
                  Actor = ActorRef.Agent
                  Timestamp = fixedClock ()
                  Event =
                    SessionEvent.WorkSandboxStarted
                        { MessageId = messageId
                          Sandbox = sandbox "test"
                          Backend = "srt"
                          Description = None
                          Checkout = None
                          Forwarded = [ github ]
                          Realisation = []
                          Actor = ActorRef.Agent } }
            let proj, _ = ConversationProjection.applyEvents None [ envelope ] ConversationProjection.empty
            match proj.Items with
            | [ item ] ->
                Expect.equal (ConversationItem.headline item) "started sandbox test (srt)" "it reads as a sentence"
                // The sentence every reader that is not a screen gets still names the
                // credential - and nobody, because a route is nobody's: whose credential
                // went down it is said per push. The note carries the event's FACTS, for a
                // screen to arrange; the sentence is a reader's, made on the way out.
                Expect.stringContains (ConversationItem.said item) "forwarding github"
                    "what went in is on the note, and nobody's name"
                match item.Content with
                | ItemContent.Act (Act.SandboxStarted s) ->
                    Expect.equal s.Forwarded [ github ] "the note carries the sandbox's typed facts, for a screen to arrange"
                | _ -> failwith "a sandbox start is an act carrying its facts, not a message"
                Expect.equal item.Author ActorRef.Agent "attributed to whoever acted"
            | other -> failwithf "expected one note, got %A" other

        // What a start forwards is a REFERENCE to the connection, not the word that names
        // it: the prose reader spells it `github`, a screen draws the same GitHub the
        // sidebar's panel is about. The sentence case above cannot tell the two apart.
        testCase "a forwarding start points at each connection it forwards" <| fun () ->
            let aws = ConnectionName.create "aws" |> expect
            let started : WorkSandboxStarted =
                { MessageId = MessageId.create "msg-1" |> expect
                  Sandbox = sandbox "test"
                  Backend = "srt"
                  Description = None
                  Checkout = None
                  Forwarded = [ aws; github ]
                  Realisation = []
                  Actor = ActorRef.Agent }
            Expect.equal
                (WorkSandboxStarted.particulars started |> List.collect Phrase.refs)
                [ EntityRef.Connection aws; EntityRef.Connection github ]
                "one reference per connection, in the order forwarded"
            Expect.equal
                (WorkSandboxStarted.particulars started |> List.map Phrase.said)
                [ "forwarding aws, github" ]
                "and the prose reader still gets the one clause"

        // The sandbox a start names is a REFERENCE: prose spells it whole, a screen draws it
        // as that sandbox — and, under the repo that declared it, by its bare name.
        testCase "a start points at its sandbox" <| fun () ->
            let started : WorkSandboxStarted =
                { MessageId = MessageId.create "msg-1" |> expect
                  Sandbox = sandbox "test"
                  Backend = "srt"
                  Description = None
                  Checkout = None
                  Forwarded = []
                  Realisation = []
                  Actor = ActorRef.Agent }
            Expect.equal (Phrase.refs (WorkSandboxStarted.phrase started)) [ EntityRef.Sandbox (sandbox "test") ] "the sandbox, once"
            Expect.equal (Phrase.said (WorkSandboxStarted.phrase started)) "started sandbox test (srt)" "and prose says it whole"

        // The person whose credential was spent finds out HERE: the block that pushed is on
        // the timeline already, but a block says what ran, not whose key went out on it.
        testCase "a push reads as the act, naming the repository and who it was done for" <| fun () ->
            let envelope : EventEnvelope<SessionEvent> =
                { EventId = EventId.fresh ()
                  SessionId = sessionId
                  Offset = EventOffset.create 5L |> expect
                  Actor = ActorRef.Agent
                  Timestamp = fixedClock ()
                  Event =
                    SessionEvent.GitCredentialSpent
                        { MessageId = MessageId.create "msg-5" |> expect
                          Sandbox = sandbox "test"
                          Terminal = TerminalId.create "term-1" |> expect
                          Block = Some (BlockId.create "b-1" |> expect)
                          Owner = CredentialFor.Person (Principal.User (UserId.create "ada" |> expect))
                          Repo = RepoRef.create "octo/hello" |> expect
                          Actor = ActorRef.Agent } }
            let proj, _ = ConversationProjection.applyEvents None [ envelope ] ConversationProjection.empty
            match proj.Items with
            | [ item ] ->
                Expect.equal (ConversationItem.headline item) "pushed to github:octo/hello on behalf of user:ada" "where, and for whom — the act first, the credential's owner after it"
                Expect.equal item.Author ActorRef.Agent "by whoever's act the block was"
                Expect.isTrue (isAct item) "an act"
            | other -> failwithf "expected one note, got %A" other

        // The sentence's two names are REFERENCES: what the prose reader spells as
        // `github:octo/hello` and `user:ada`, a screen draws as that repository and that
        // person. A fold that pasted them in as words would say the same sentence and point
        // at nothing — which is exactly what the string case above cannot tell apart.
        testCase "a push points at its repository and the person it was done for" <| fun () ->
            let ada = UserId.create "ada" |> expect
            let spent : GitCredentialSpent =
                { MessageId = MessageId.create "msg-5" |> expect
                  Sandbox = sandbox "test"
                  Terminal = TerminalId.create "term-1" |> expect
                  Block = Some (BlockId.create "b-1" |> expect)
                  Owner = CredentialFor.Person (Principal.User ada)
                  Repo = RepoRef.create "octo/hello" |> expect
                  Actor = ActorRef.Agent }
            Expect.equal
                (Phrase.refs (GitCredentialSpent.phrase spent))
                [ EntityRef.Repo (RepoRef.create "octo/hello" |> expect); EntityRef.Actor (UserRef ada) ]
                "the repository, then the person — in the order the sentence names them"

        // The deployment is one party with one set of credentials, and not a thing a screen
        // draws: a push on its own credential names it in words and points at the repo alone.
        testCase "a push on the deployment's credential points at the repository alone" <| fun () ->
            let spent : GitCredentialSpent =
                { MessageId = MessageId.create "msg-5" |> expect
                  Sandbox = sandbox "test"
                  Terminal = TerminalId.create "term-1" |> expect
                  Block = Some (BlockId.create "b-1" |> expect)
                  Owner = CredentialFor.Deployment
                  Repo = RepoRef.create "octo/hello" |> expect
                  Actor = ActorRef.Agent }
            Expect.equal
                (Phrase.refs (GitCredentialSpent.phrase spent))
                [ EntityRef.Repo (RepoRef.create "octo/hello" |> expect) ]
                "one reference"
            Expect.equal
                (Phrase.said (GitCredentialSpent.phrase spent))
                "pushed to github:octo/hello on behalf of this deployment"
                "and the deployment in words"

        // No block says what ran when the push was typed under a lease, so the line says
        // where it was typed instead.
        testCase "a push typed under a lease says so, since no block will" <| fun () ->
            let envelope : EventEnvelope<SessionEvent> =
                { EventId = EventId.fresh ()
                  SessionId = sessionId
                  Offset = EventOffset.create 6L |> expect
                  Actor = ada
                  Timestamp = fixedClock ()
                  Event =
                    SessionEvent.GitCredentialSpent
                        { MessageId = MessageId.create "msg-6" |> expect
                          Sandbox = sandbox "test"
                          Terminal = TerminalId.create "term-1" |> expect
                          Block = None
                          Owner = CredentialFor.Person (Principal.User (UserId.create "ada" |> expect))
                          Repo = RepoRef.create "octo/hello" |> expect
                          Actor = ada } }
            let proj, _ = ConversationProjection.applyEvents None [ envelope ] ConversationProjection.empty
            match proj.Items with
            | [ item ] ->
                Expect.equal
                    (ConversationItem.headline item)
                    "pushed to github:octo/hello on behalf of user:ada, holding the terminal"
                    "whose, where, and that it was typed rather than queued"
            | other -> failwithf "expected one note, got %A" other

        testCase "a start with nothing forwarded says nothing about credentials" <| fun () ->
            let envelope : EventEnvelope<SessionEvent> =
                { EventId = EventId.fresh ()
                  SessionId = sessionId
                  Offset = EventOffset.create 2L |> expect
                  Actor = ada
                  Timestamp = fixedClock ()
                  Event =
                    SessionEvent.WorkSandboxStarted
                        { MessageId = MessageId.create "msg-2" |> expect
                          Sandbox = sandbox "test"
                          Backend = "host"
                          Description = None
                          Checkout = None
                          Forwarded = []
                          Realisation = []
                          Actor = ada } }
            let proj, _ = ConversationProjection.applyEvents None [ envelope ] ConversationProjection.empty
            match proj.Items with
            | [ item ] -> Expect.equal (ConversationItem.headline item) "started sandbox test (host)" "no forwarding clause"
            | other -> failwithf "expected one note, got %A" other

        // The warning a person reads without going looking. A sandbox that came up holding
        // something wider than its resources named says so on the line that announces it —
        // the timeline is where somebody finds out what was done on their behalf, and "the
        // confinement you were shown is not the confinement you got" is the most consequential
        // thing on it.
        testCase "a start that could not be given exactly says so on the note announcing it" <| fun () ->
            let envelope : EventEnvelope<SessionEvent> =
                { EventId = EventId.fresh ()
                  SessionId = sessionId
                  Offset = EventOffset.create 4L |> expect
                  Actor = ada
                  Timestamp = fixedClock ()
                  Event =
                    SessionEvent.WorkSandboxStarted
                        { MessageId = MessageId.create "msg-4" |> expect
                          Sandbox = sandbox "test"
                          Backend = "srt"
                          Description = None
                          Checkout = None
                          Forwarded = []
                          Realisation = [ "the socket at /run/docker.sock — this host cannot scope that, so the sandbox gets any unix socket on this host" ]
                          Actor = ada } }
            let proj, _ = ConversationProjection.applyEvents None [ envelope ] ConversationProjection.empty
            match proj.Items with
            | [ item ] ->
                // The grant a person did not get exactly is on the sentence they read, and in
                // the typed realisation the screen shows as its own line.
                let said = ConversationItem.said item
                Expect.equal (ConversationItem.headline item) "started sandbox test (srt)" "still says what started"
                Expect.isTrue (said.Contains "/run/docker.sock") (sprintf "the grant is named, said: %s" said)
                Expect.isTrue (said.Contains "any unix socket") (sprintf "and what it became, said: %s" said)
                match item.Content with
                | ItemContent.Act (Act.SandboxStarted s) ->
                    Expect.equal (List.length s.Realisation) 1 "and the note carries the realisation as a fact, not only as prose"
                | _ -> failwith "a sandbox start is an act carrying its facts"
            | other -> failwithf "expected one note, got %A" other

        // The start above and this are the two outcomes of one declaration. Until this note
        // existed only the first reached the timeline, so a file with a typo in it read
        // exactly like a file nobody had written.
        testCase "a declaration that could not start reads as a sentence too" <| fun () ->
            let hello = RepoRef.create "octo/hello" |> expect
            let envelope : EventEnvelope<SessionEvent> =
                { EventId = EventId.fresh ()
                  SessionId = sessionId
                  Offset = EventOffset.create 3L |> expect
                  Actor = ActorRef.Configured hello
                  Timestamp = fixedClock ()
                  Event =
                    SessionEvent.RepoConfigRefused
                        { RepoConfigRefused.MessageId = MessageId.create "msg-3" |> expect
                          RepoConfigRefused.Repo = hello
                          RepoConfigRefused.Sandbox = Some (sandbox "test")
                          RepoConfigRefused.Reason = "YESSION_SESSION_WORK_NET is empty"
                          RepoConfigRefused.Actor = ActorRef.Configured hello } }
            let proj, _ = ConversationProjection.applyEvents None [ envelope ] ConversationProjection.empty
            match proj.Items with
            | [ item ] ->
                Expect.equal (ConversationItem.headline item) "could not start sandbox test" "what failed"
                Expect.equal
                    (noteDetail item)
                    (Some "YESSION_SESSION_WORK_NET is empty")
                    "and why, whole, in the words it already used"
                Expect.isTrue (isAct item) "an act, like the start it is the counterpart of"
                Expect.equal item.Author (ActorRef.Configured hello) "attributed to the file that asked"
            | other -> failwithf "expected one note, got %A" other

        // A file that could not be READ has no declaration to name, and its reason already
        // says which repo and where in the file. Anything in front of it would be a second
        // copy of what it says.
        testCase "an unreadable file speaks for itself, with no sandbox to name" <| fun () ->
            let hello = RepoRef.create "octo/hello" |> expect
            let envelope : EventEnvelope<SessionEvent> =
                { EventId = EventId.fresh ()
                  SessionId = sessionId
                  Offset = EventOffset.create 4L |> expect
                  Actor = ActorRef.Configured hello
                  Timestamp = fixedClock ()
                  Event =
                    SessionEvent.RepoConfigRefused
                        { RepoConfigRefused.MessageId = MessageId.create "msg-4" |> expect
                          RepoConfigRefused.Repo = hello
                          RepoConfigRefused.Sandbox = None
                          RepoConfigRefused.Reason = "yession.yaml in octo/hello: unknown key: workdirr"
                          RepoConfigRefused.Actor = ActorRef.Configured hello } }
            let proj, _ = ConversationProjection.applyEvents None [ envelope ] ConversationProjection.empty
            match proj.Items with
            | [ item ] ->
                Expect.equal (ConversationItem.headline item) "yession.yaml in octo/hello: unknown key: workdirr" "said as it stands"
            | other -> failwithf "expected one note, got %A" other

        // The wire form, both ways. `sandbox` is the field that can be absent, and a note
        // about a file rather than a declaration is the case that exercises it.
        testCase "a refusal round-trips, with and without a sandbox to name" <| fun () ->
            let hello = RepoRef.create "octo/hello" |> expect
            for named in [ Some (sandbox "test"); None ] do
                let envelope : EventEnvelope<SessionEvent> =
                    { EventId = EventId.fresh ()
                      SessionId = sessionId
                      Offset = EventOffset.create 5L |> expect
                      Actor = ActorRef.Configured hello
                      Timestamp = fixedClock ()
                      Event =
                        SessionEvent.RepoConfigRefused
                            { RepoConfigRefused.MessageId = MessageId.create "msg-5" |> expect
                              RepoConfigRefused.Repo = hello
                              RepoConfigRefused.Sandbox = named
                              RepoConfigRefused.Reason = "the ceiling is closed"
                              RepoConfigRefused.Actor = ActorRef.Configured hello } }
                let json = Codec.toString Codec.sessionEventEnvelope envelope
                Expect.equal (Codec.fromString Codec.sessionEventEnvelope json |> expect) envelope "unchanged by the wire"
    ]

let private backendTests =
    // The scope decides the backend (`SandboxRuntime.backendFor`): the session's own
    // sandboxes keep whatever light confinement the operator configured; a repo's are
    // WORK, and work runs in a container. Pinned here because everything downstream —
    // the registry's describe, the composition's create — only ASKS.
    let repo = RepoRef.create "octo/hello" |> expect
    let container = Container { ContainerSpec.defaults with Image = Some { Name = "nixos/nix"; Tag = None } }
    testList "the backend a scope gets" [
        testCase "the session's own sandbox keeps the configured backend" (fun () ->
            Expect.equal
                (SandboxRuntime.backendFor SrtBackend SessionOwned Confinement)
                (Ok SrtBackend)
                "the default sandbox is the operator's light confinement"
            Expect.equal
                (SandboxRuntime.backendFor HostBackend SessionOwned container)
                (Ok HostBackend)
                "scope decides; the runtime is reconciled downstream")

        testCase "a repo's sandbox is a container, whatever the session runs" (fun () ->
            Expect.equal
                (SandboxRuntime.backendFor SrtBackend (RepoOwned repo) container)
                (Ok DockerBackend)
                "repo-owned work runs under docker")

        testCase "a repo's sandbox with no container is refused, naming the fix" (fun () ->
            match SandboxRuntime.backendFor SrtBackend (RepoOwned repo) Confinement with
            | Ok backend -> failwithf "started under %A instead of refusing" backend
            | Error reason ->
                Expect.isTrue (reason.Contains "octo/hello") "the refusal names the repo"
                Expect.isTrue (reason.Contains "container") "and says what to declare")

        // The total half on its own, because everything that is not a start asks it: the
        // registry's describe, the workspace, the limits a repo's ask is judged against.
        testCase "the scope's backend needs no runtime to answer" (fun () ->
            Expect.equal
                (SandboxRuntime.scopedBackend SrtBackend SessionOwned)
                SrtBackend
                "session-owned keeps the configured confinement"
            Expect.equal
                (SandboxRuntime.scopedBackend SrtBackend (RepoOwned repo))
                SandboxRuntime.repoWorkBackend
                "repo-owned is the repo work backend, whatever the session runs")

        // A repo's `workdir:` resolves against the checkout as the CONTAINER sees it —
        // the /repos bind — never the terminal's view. Resolved against the terminal's
        // view, `workdir: .` produced a container whose working directory wore the host
        // checkout path: an empty volume where nothing would look, while the checkout
        // sat under /repos (measured on a live session before this rule existed).
        testCase "a repo's declaration resolves against the container's view of its checkout" (fun () ->
            Expect.equal
                (Sandboxes.workCheckoutAt None "/Users/o/.yession/sessions/S1/workspace/repos" repo)
                "/repos/octo/hello"
                "the container view, independent of where the host keeps the clones")

        // …and against the view its own file asked for, when it asked. The same function
        // answers both, which is the point: the mount, the write path and the answer a verb
        // gives are one computation, so a declaration cannot move one of them and not the
        // others.
        testCase "a sandbox that says where it wants the checkouts is answered there" (fun () ->
            Expect.equal
                (Sandboxes.workCheckoutAt (Some "/src") "/Users/o/.yession/sessions/S1/workspace/repos" repo)
                "/src/octo/hello"
                "the declared root, with the same repo under it")
    ]

let private workspaceVolumeTests =
    // Whether a mount already provides the workspace path decides if the named workspace
    // volume is attached there. Containment counts: a repo sandbox's workdir is its
    // checkout UNDER the /repos bind, and a volume at the deeper path would shadow the
    // checkout with an empty directory.
    testList "what provides a container's workspace" [
        testCase "a mount at the path itself provides it" (fun () ->
            Expect.isTrue (ContainerMount.provides "/workspace" "/workspace") "exact"
            Expect.isTrue (ContainerMount.provides "/workspace/" "/workspace") "slashes do not matter")

        testCase "a mount above the path provides it" (fun () ->
            Expect.isTrue (ContainerMount.provides "/repos" "/repos/octo/hello") "the checkout rides the bind")

        testCase "a bare prefix is not containment" (fun () ->
            Expect.isFalse (ContainerMount.provides "/repo" "/repos/octo/hello") "directory boundary only"
            Expect.isFalse (ContainerMount.provides "/repos/octo/hello" "/repos") "and never upward")
    ]

/// What a block in a sandbox is lent: the forwarded sources' answers for the credential the
/// block's ACT runs on — which is not who started the sandbox.
let private lentTests =
    let terminal = TerminalId.create "term-a" |> expect
    let block = BlockId.create "b-1" |> expect
    let bob = Principal.Peer (PeerId.create "bob" |> expect)
    testList "what a block is lent" [
        testCaseAsync "a block in a sandbox that forwards github is lent for its act's credential, not the starter's" <|
            async {
                let log = newLog ()
                let sandboxes, _ = registry log [ githubCredential "route" ]
                let! _ = sandboxes.Ensure caller (sandbox "test") (forwarding [ "github" ])
                let! lent = sandboxes.Loans.Lend (sandbox "test") terminal (Some block) (Authority.agentFor bob)
                Expect.equal
                    lent.Vars
                    [ "LENT_TO", Some (Principal.token bob) ]
                    "the source was asked for the turn human of THIS block, though ada started the sandbox"
            }

        testCaseAsync "a sandbox that forwards nothing lends nothing" <|
            async {
                let log = newLog ()
                let sandboxes, _ = registry log [ githubCredential "route" ]
                let! _ = sandboxes.Ensure caller (sandbox "test") SandboxRequest.defaults
                let! lent = sandboxes.Loans.Lend (sandbox "test") terminal (Some block) (Authority.agentFor bob)
                Expect.equal lent BlockEnv.none "a source the sandbox does not forward is not asked"
            }

        testCaseAsync "a name the session does not have lends nothing" <|
            async {
                let log = newLog ()
                let sandboxes, _ = registry log [ githubCredential "route" ]
                let! lent = sandboxes.Loans.Lend (sandbox "nope") terminal (Some block) (Authority.agentFor bob)
                Expect.equal lent BlockEnv.none "its environment refuses the spawn; the loan has nothing to add"
            }
    ]

let tests =
    testList "WorkSandboxes" [
        nameTests
        backendTests
        workspaceVolumeTests
        normaliseTests
        ensureTests
        credentialTests
        lentTests
        queryTests
        timelineTests
    ]
