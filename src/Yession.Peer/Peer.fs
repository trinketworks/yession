module Yession.Peer

// The browser's client, driven headlessly.
//
// Lifted out of `tests/Yession.Tests/Support.fs`, where it grew, when a second caller appeared
// — an investigation tool that joins a real session. Writing it again there would have been two
// wirings of one client, and the day the browser's changed only one would follow; both exist to
// drive what a PERSON drives, so a second approximation is worth nothing.
//
// What stayed behind is what only a suite wants: the in-memory connectors that take a
// `SessionHost`, the SSR render, the fixtures, the waiters.

open Elmish
open Fable.Core
open Yjs
open Ylmish
open Yession.Domain
open Yession.Domain.Sandboxes
open Yession.Domain.Collab
open Yession.App.Collab
open Yession.Domain.Link
open Yession.App
open Yession.Host


/// Private, and deliberately not shared: a four-line unwrap is not wiring, and exporting it
/// would have made every suite that uses one learn that the client moved.
let private expect =
    function
    | Ok v -> v
    | Error e -> failwith e

let private user msg = Ylmish.Program.Message.User msg

/// Run an Elmish program headlessly, exposing the latest model, dispatch, and an
/// event-driven `WaitFor` that resolves the first time the model satisfies a predicate.
module Harness =

    /// How long a single `WaitFor` may wait before it is a FAILURE rather than a wait.
    ///
    /// A condition that never arrives used to hang for ever, and the run's own budget
    /// (`tasks.fsx`, 240s for the whole Node suite) was the only thing that stopped it — so
    /// one stuck predicate killed every suite after it and reported "tests timed out" with no
    /// name attached. Finding which test it was meant reading a process table. A deadline here
    /// costs nothing when things work and turns that into one named failing test.
    ///
    /// 30s is deliberately far above anything real: the slowest whole test in a healthy
    /// `check Ports Native Srt` run — a packaged manager launching real child processes over
    /// real WebRTC, with several waits inside it — is under 9s, and a single wait is
    /// milliseconds. It is a hang detector, not a performance budget.
    let waitForTimeoutMs = 30000

    type Runner<'model, 'msg> =
        { Model : unit -> 'model
          Dispatch : 'msg -> unit
          /// Resolve the first time the model satisfies the predicate, or FAIL after
          /// `waitForTimeoutMs`, or FAIL at once when the runner's `refusal` says the model
          /// has reached a state no wait will survive. Never waits for ever.
          WaitFor : ('model -> bool) -> Async<unit> }

    /// A `refusal` for a model with no state that ends a wait early: every wait runs to its
    /// predicate or its deadline.
    let neverRefuses (_model: 'model) : string option = None

    /// The runner, with the wait deadline as a parameter — so the deadline itself can be
    /// tested (a 30s one cannot be, in a cheap tier measured in milliseconds) without any
    /// suite having to reach for a different mechanism. `run` is this at the real deadline.
    ///
    /// `refusal` answers, for a model, why a wait that is not yet met will not be: the
    /// reason, or `None` while waiting is still sensible. It is asked of every model the
    /// program reaches and of the model a wait begins on, and the predicate is asked FIRST —
    /// so a wait FOR the bad state is answered, and only the waits for anything else are
    /// refused. A deadline is a hang detector and says only that nothing came; the model
    /// often knows better, and a wait that sits out thirty seconds beside a model that
    /// already holds the reason is a red that hid its own cause.
    let runWith
        (refusal: 'model -> string option)
        (timeoutMs: int)
        (program: Program<unit, 'model, 'msg, unit>)
        : Runner<'model, 'msg> =
        let mutable model = Unchecked.defaultof<'model>
        let mutable dispatch : 'msg -> unit = ignore
        // A waiter is settled exactly once, by the model, the clock or the refusal — whichever
        // comes first — so each carries how to be resumed and how to be failed.
        let mutable waiters : (('model -> bool) * (unit -> unit) * (string -> unit)) list = []
        let setState m d =
            model <- m
            dispatch <- d
            let fire, rest = waiters |> List.partition (fun (predicate, _, _) -> predicate m)
            // What is left, once the model has had its say, is a set of waits for something
            // the model has said will not come.
            let refused, keep =
                match refusal m with
                | Some reason -> rest |> List.map (fun (_, _, fail) -> fun () -> fail reason), []
                | None -> [], rest
            waiters <- keep
            // Resume on a microtask: setState runs INSIDE the Elmish dispatch loop, and
            // a continuation that dispatches synchronously from here would only enqueue
            // (ring buffer) — its Model() reads would then see stale state. Deferring
            // lets the loop drain first, so awaited WaitFor + Dispatch compose safely.
            fire |> List.iter (fun (_, resume, _) -> Fable.NodeExtras.Microtasks.queue resume)
            refused |> List.iter Fable.NodeExtras.Microtasks.queue
        Program.withSetState setState program |> Program.run
        { Model = fun () -> model
          Dispatch = fun msg -> dispatch msg
          WaitFor =
            fun predicate ->
                Async.FromContinuations (fun (cont, econt, _) ->
                    if predicate model then cont ()
                    else
                        match refusal model with
                        | Some reason -> econt (exn (sprintf "WaitFor stopped waiting: %s" reason))
                        | None ->
                            // Settled exactly once, by whichever comes first — the model, the
                            // refusal or the clock. `settled` is what makes that true: the timer
                            // cannot resume a continuation the model already resumed, and a model
                            // update cannot resume one the timer already failed.
                            let settled = ref false
                            let timer = ref 0
                            let resume () =
                                if not settled.Value then
                                    settled.Value <- true
                                    JS.clearTimeout timer.Value
                                    cont ()
                            let fail (reason: string) =
                                if not settled.Value then
                                    settled.Value <- true
                                    JS.clearTimeout timer.Value
                                    econt (exn (sprintf "WaitFor stopped waiting: %s" reason))
                            waiters <- (predicate, resume, fail) :: waiters
                            timer.Value <-
                                JS.setTimeout
                                    (fun () ->
                                        if not settled.Value then
                                            settled.Value <- true
                                            // Drop the waiter before failing: a predicate left in
                                            // the list would be re-evaluated on every later
                                            // setState, for a test that is already over.
                                            waiters <-
                                                waiters
                                                |> List.filter (fun (_, r, _) ->
                                                    not (System.Object.ReferenceEquals (r, resume)))
                                            econt (
                                                exn (
                                                    sprintf
                                                        "WaitFor timed out after %dms: the model never satisfied the predicate"
                                                        timeoutMs)))
                                    timeoutMs) }

    let run (program: Program<unit, 'model, 'msg, unit>) : Runner<'model, 'msg> =
        runWith neverRefuses waitForTimeoutMs program

    /// Wait for every named condition at once, and on the deadline say WHICH did not hold.
    ///
    /// A wait on a conjunction fails as one: its red says the predicate never held, and not
    /// which half of it — so a case waiting on a message, a reply, an environment and a
    /// block cost a full gate run per guess when it timed out (`Phase4`'s packaged
    /// composition, twice in thirty master runs, both times reading exactly that). The
    /// wait is the same one, on the same deadline; only the failure has more to say.
    ///
    /// `showing` is what else to print when it fails: the part of the model a reader needs to
    /// tell WHY a condition did not hold, which the condition's name cannot carry.
    let waitForAll
        (runner: Runner<'model, 'msg>)
        (conditions: (string * ('model -> bool)) list)
        (showing: 'model -> string)
        : Async<unit> =
        async {
            let all (model: 'model) = conditions |> List.forall (fun (_, holds) -> holds model)
            match! runner.WaitFor all |> Async.Catch with
            | Choice1Of2 () -> return ()
            | Choice2Of2 timedOut ->
                let model = runner.Model ()
                let missing = conditions |> List.filter (fun (_, holds) -> not (holds model)) |> List.map fst
                return
                    failwithf
                        "%s — still not true: %s%s"
                        timedOut.Message
                        (match missing with
                         | [] -> "nothing (every condition holds now; it came true after the deadline)"
                         | some -> String.concat "; " some)
                        (match showing model with
                         | "" -> ""
                         | shown -> " — " + shown)
        }



/// One peer, as the client identifies itself when it joins.
let peer (id: string) (name: string) : PeerState =
    { PeerId = PeerId.create id |> expect; DisplayName = name }

/// One full connected client against a host. `Registry` is the client's `BodyRegistry` (over
/// its doc), so the body seam below binds the same top-level fragment roots the app does.
type Client =
    { Runner : Harness.Runner<ClientModel, Ylmish.Program.Message<ClientMsg>>
      Connection : Client.Connection
      Registry : BodyRegistry
      /// The plain-text roots the terminal composers live in (Plan 13), alongside the rich
      /// bodies. Held on the client for the same reason `Registry` is: a test drives the
      /// composer by writing the CRDT the browser's input writes.
      Texts : TextRegistry
      Channel : FrameChannel<string>
      Doc : Y.Doc
      Hello : PeerHelloPayload
      /// Point what the program asks of the session — a command, presence, a read — at this
      /// connection: the browser's `connectionRef`. A client that reconnects over a new one
      /// says so here, or its reads go on being asked of a channel that has gone.
      Wire : Client.Connection -> unit }

/// Why a client's waits should stop: the session's environment, where every command of a work
/// sandbox starts, failed to START. Its reason is the product's own sentence (`srt sandbox
/// failed: ...`), which the model has held since the event arrived.
///
/// Without this, a suite that runs a command on a box with no sandbox waits for a block that
/// can never finish and reports a thirty-second timeout naming the wait and nothing about the
/// box. With it the first wait the client is in fails at once, quoting the reason and saying
/// what to do about it.
///
/// "Failed" ends a wait because nothing in a client's lifetime here retries: the environment is
/// started on a command's need, and a suite that expects one failure and then a recovery waits
/// FOR the failure first, which the predicate-first order of `Harness.runWith` answers.
///
/// Cases listed one by one, so a new state of the environment is a compile error here and not
/// a silent "keep waiting".
let environmentRefusal (model: ClientModel) : string option =
    match model.Environment with
    | EnvironmentFailed reason ->
        Some (
            sprintf
                "the work sandbox did not start: %s. If this box has no sandbox, the suite needs `Sandbox` in its `needs`"
                reason)
    | EnvironmentNotStarted
    | EnvironmentStarting
    | EnvironmentRunning _
    | EnvironmentDown -> None

/// Connect one full client with explicit options: WebRTC channel, its own Yjs doc, the
/// withYlmish program, and the connection driver. Resolves once the model reaches
/// `Connected`.
let connectClientWith (options: Client.ConnectOptions) (signalUrl: string) (token: string) (id: string) (name: string) : Async<Client> =
    async {
        let! channel = WebRtc.connect signalUrl
        let doc = Y.Doc.Create ()
        let local = peer id name
        let registry = BodyRegistry doc
        let texts = TextRegistry doc
        // Made after the program, as the browser makes it, so the program reads it through a
        // getter: what a message asks of the session goes to whichever connection there is.
        let wired : Client.Connection option ref = ref None
        let runner = Harness.runWith environmentRefusal Harness.waitForTimeoutMs (Client.makeProgram { Client.Ports.Connection = (fun () -> wired.Value); Client.Ports.Launch = None; Client.Ports.Panels = None; Client.Ports.Moves = ignore; Client.Ports.Clipboard = (fun _ -> async.Return false); Client.Ports.Retry = ignore; Client.Ports.Keyframe = (fun _ _ -> async.Return None); Client.Ports.Remember = ignore; Client.Ports.Uploads = None } doc (ClientModel.init local))
        // The composer's publication rule, wired exactly as the browser wires it: the client's
        // draft slot appears when its body has content and goes when the body empties.
        DraftSlot.follow doc registry local.PeerId (user >> runner.Dispatch) |> ignore
        let hello = { PeerId = local.PeerId; DisplayName = name; Token = token }
        let connection = Client.connect options doc registry texts hello (user >> runner.Dispatch) channel
        wired.Value <- Some connection
        Async.StartImmediate connection.Run
        do! runner.WaitFor (fun m -> m.Connection = Connected)
        return { Runner = runner; Connection = connection; Registry = registry; Texts = texts; Channel = channel; Doc = doc; Hello = hello; Wire = fun c -> wired.Value <- Some c }
    }

/// `connectClientWith` under the default options (frame-based event reads).
let connectClient (signalUrl: string) (token: string) (id: string) (name: string) : Async<Client> =
    connectClientWith Client.ConnectOptions.defaults signalUrl token id name



/// Author a draft body on a full Client: write the markdown into the peer's body fragment and
/// wait for the slot. No slot is dispatched — writing the body publishes it (`DraftSlot.follow`,
/// wired by the connectors above as the browser wires it), which is what typing does. The write
/// flows through the fragment CRDT and syncs like any edit. Replaces the old `editBody`/`setDraft`.
/// Co-editing another peer's slot goes through here too: their slot already exists (they typed).
/// Write a peer's draft body and nothing else — what typing into the composer does. The shared
/// half of the test suite's body seam: the rest of that seam drives bare runners, which only a
/// suite has, but writing a body is what SENDING needs and both callers send.
let writeBody (registry: BodyRegistry) (peer: PeerId) (markdown: string) : unit =
    Markdown.intoFragment markdown (registry.Fragment (BodyKey.draft peer))

let compose (client: Client) (peer: PeerId) (markdown: string) : Async<unit> =
    async {
        writeBody client.Registry peer markdown
        do! client.Runner.WaitFor (fun m -> Map.containsKey peer m.Synced.Drafts)
    }

