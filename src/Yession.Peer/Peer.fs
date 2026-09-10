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
open Yession.Domain.Collab
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

    [<Fable.Core.Emit("queueMicrotask($0)")>]
    let private defer (f: unit -> unit) : unit = Fable.Core.Util.jsNative

    [<Fable.Core.Emit("setTimeout($0, $1)")>]
    let private setTimer (f: unit -> unit) (ms: int) : float = Fable.Core.Util.jsNative

    [<Fable.Core.Emit("clearTimeout($0)")>]
    let private clearTimer (handle: float) : unit = Fable.Core.Util.jsNative

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
          /// `waitForTimeoutMs`. Never waits for ever.
          WaitFor : ('model -> bool) -> Async<unit> }

    /// The runner, with the wait deadline as a parameter — so the deadline itself can be
    /// tested (a 30s one cannot be, in a cheap tier measured in milliseconds) without any
    /// suite having to reach for a different mechanism. `run` is this at the real deadline.
    let runWith (timeoutMs: int) (program: Program<unit, 'model, 'msg, unit>) : Runner<'model, 'msg> =
        let mutable model = Unchecked.defaultof<'model>
        let mutable dispatch : 'msg -> unit = ignore
        let mutable waiters : (('model -> bool) * (unit -> unit)) list = []
        let setState m d =
            model <- m
            dispatch <- d
            let fire, keep = waiters |> List.partition (fun (predicate, _) -> predicate m)
            waiters <- keep
            // Resume on a microtask: setState runs INSIDE the Elmish dispatch loop, and
            // a continuation that dispatches synchronously from here would only enqueue
            // (ring buffer) — its Model() reads would then see stale state. Deferring
            // lets the loop drain first, so awaited WaitFor + Dispatch compose safely.
            fire |> List.iter (fun (_, resume) -> defer resume)
        Program.withSetState setState program |> Program.run
        { Model = fun () -> model
          Dispatch = fun msg -> dispatch msg
          WaitFor =
            fun predicate ->
                Async.FromContinuations (fun (cont, econt, _) ->
                    if predicate model then cont ()
                    else
                        // Settled exactly once, by whichever comes first — the model or the
                        // clock. `settled` is what makes that true: the timer cannot resume a
                        // continuation the model already resumed, and a model update cannot
                        // resume one the timer already failed.
                        let settled = ref false
                        let timer = ref 0.0
                        let resume () =
                            if not settled.Value then
                                settled.Value <- true
                                clearTimer timer.Value
                                cont ()
                        waiters <- (predicate, resume) :: waiters
                        timer.Value <-
                            setTimer
                                (fun () ->
                                    if not settled.Value then
                                        settled.Value <- true
                                        // Drop the waiter before failing: a predicate left in
                                        // the list would be re-evaluated on every later
                                        // setState, for a test that is already over.
                                        waiters <-
                                            waiters
                                            |> List.filter (fun (_, r) ->
                                                not (System.Object.ReferenceEquals (r, resume)))
                                        econt (
                                            exn (
                                                sprintf
                                                    "WaitFor timed out after %dms: the model never satisfied the predicate"
                                                    timeoutMs)))
                                timeoutMs) }

    let run (program: Program<unit, 'model, 'msg, unit>) : Runner<'model, 'msg> =
        runWith waitForTimeoutMs program



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
      Hello : PeerHelloPayload }

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
        let runner = Harness.run (Client.makeProgram doc (ClientModel.init local))
        // The composer's publication rule, wired exactly as the browser wires it: the client's
        // draft slot appears when its body has content and goes when the body empties.
        DraftSlot.follow doc registry local.PeerId (user >> runner.Dispatch) |> ignore
        let hello = { PeerId = local.PeerId; DisplayName = name; Token = token }
        // The model is what "how far have we consumed" means (see `ConnectOptions`).
        let options = { options with ReadPosition = Some (fun () -> (runner.Model ()).EventConsumer.LastProcessedOffset) }
        let connection = Client.connect options doc registry texts hello (user >> runner.Dispatch) channel
        Async.StartImmediate connection.Run
        do! runner.WaitFor (fun m -> m.Connection = Connected)
        return { Runner = runner; Connection = connection; Registry = registry; Texts = texts; Channel = channel; Doc = doc; Hello = hello }
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

