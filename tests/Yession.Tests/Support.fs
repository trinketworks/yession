module Yession.Tests.Support

// The headless client that used to live here is `Yession.Peer` now — one wiring, shared with
// the probe that investigates real sessions. What stayed is what only a suite wants: the
// connectors over an in-memory channel (they take a `SessionHost`, which no tool has), the SSR
// render, the fixtures and the waiters. `open Yession.Peer` below keeps every call site here
// unchanged.

// Shared test infrastructure: a headless Elmish runner with event-driven model waiters,
// and a full-client connector (WebRTC channel + Yjs doc + withYlmish program + drivers)
// parameterized by host, so every E2E suite composes the same way. No sleeps, no polling.

open System
open Elmish
open Fable.Core
open Fable.Pyxpecto
open Yjs
open Yession.Domain
open Yession.Domain.Sandboxes
open Yession.Domain.Link
open Yession.Domain.Collab
open Yession.App
open Yession.Host
open Yession.Peer

#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

let expect =
    function
    | Ok v -> v
    | Error e -> failwith e

let user msg = Ylmish.Program.Message.User msg

// --- The sandbox seam's deterministic test double ----------------------------------------

/// Counts sandbox lifecycle calls so tests can assert an operation happened (or was
/// prevented) at the seam.
type SandboxRecorder () =
    member val Created = 0 with get, set
    member val Disposed = 0 with get, set
    member val Spawned = 0 with get, set

/// A policy with nothing in it — for scripted sandboxes that ignore it.
let emptyPolicy : SandboxPolicy =
    { ReadPaths = []
      WritePaths = []
      AllowedDomains = None
      Sockets = []
      Binds = []
      Volumes = []
      Realisation = []
      Env = Map.empty
      WorkingDirectory = None
      Filesystem = Confined }

let preparedEmptyPolicy : unit -> Async<Result<SandboxPolicy, string>> =
    fun () -> async { return Ok emptyPolicy }

/// A clock a test turns by hand.
///
/// `After` parks the caller until `Advance` has carried the clock past its due time; timers
/// fire in due order, each with `Now` standing at its own due time, so a component that
/// re-arms itself from inside a timer sees the time it expects. Nothing fires on its own:
/// a window that has to pass is a call to `Advance`, and a case takes the time it takes to
/// do its I/O rather than the time its windows are wide.
type VirtualClock =
    { Clock : Clock
      /// Move the clock forward, firing every timer that comes due on the way.
      Advance : TimeSpan -> unit
      /// How many waits are parked — what a case asserts when it expects a component to
      /// have armed (or not armed) a timer.
      Pending : unit -> int }

let virtualClock (start: DateTimeOffset) : VirtualClock =
    let mutable now = start
    let timers = ResizeArray<DateTimeOffset * (unit -> unit)> ()
    let advance (by: TimeSpan) =
        let target = now + by
        let rec fire () =
            let due =
                timers
                |> Seq.indexed
                |> Seq.filter (fun (_, (at, _)) -> at <= target)
                |> Seq.sortBy (fun (_, (at, _)) -> at)
                |> Seq.tryHead
            match due with
            | Some (index, (at, resume)) ->
                timers.RemoveAt index
                now <- max now at
                resume ()
                fire ()
            | None -> ()
        fire ()
        now <- target
    { Clock =
        { Now = fun () -> now
          After =
            fun delay ->
                Async.FromContinuations (fun (cont, _, _) -> timers.Add ((now + delay), (fun () -> cont ()))) }
      Advance = advance
      Pending = fun () -> timers.Count }

/// A Session Process as production composes it — `Host.startFull`'s own wiring: its shell,
/// its nonce, its drain, its command path — over ONE sandbox built by `createSandbox`
/// under `policy`, standing as the session's default.
///
/// For the tests that ask "does what ships work?" rather than "does this seam hold?".
/// Nothing here re-lists a collaborator the Host already chooses, and that is the point:
/// a fixture that composes `SessionTerminals` itself has to name a nonce and a shell, and
/// the ones it names are the ones it tests. Every pty case in the suite minted a short
/// nonce; production mints a UUID; the `sh` dialect's prompt broke at exactly that width,
/// on every terminal of a deployed host, with the suite green.
let hostOver
    (createSandbox: CreateSandbox)
    (policy: SandboxPolicy)
    (name: string)
    : Async<Host.SessionHost> =
    let makeSandboxes (log: Yession.SessionProcess.EventLog<SessionEvent>) =
        Yession.SessionProcess.SessionEnvironment.create
            log
            createSandbox
            (fun () -> async { return Ok policy })
            name
            name
        |> WorkSandboxes.singleton name
    Host.startWithEnvironment None (Some makeSandboxes) None (SessionId.create name |> expect) 0

/// A deterministic in-memory sandbox: creations/disposals are counted, spawns are
/// delegated to an injected script. The seam analogue of the old InMemoryBackend.
let scriptedSandbox
    (recorder: SandboxRecorder)
    (script: SandboxExec -> (OutputStream * string -> unit) -> Async<SandboxRun>)
    : CreateSandbox =
    fun _policy ->
        async {
            recorder.Created <- recorder.Created + 1
            let ref = sprintf "scripted-%d" recorder.Created
            return
                Ok
                    { Ref = ref
                      Spawn =
                        fun exec onChunk ->
                            async {
                                recorder.Spawned <- recorder.Spawned + 1
                                return
                                    Ok
                                        { WriteStdin = ignore
                                          CloseStdin = ignore
                                          Kill = ignore
                                          Exited = script exec onChunk }
                            }
                      SpawnPty = None
                      Dispose = fun () -> async { recorder.Disposed <- recorder.Disposed + 1 } }
        }

/// The standard scripted spawn: one stdout chunk, exit 0.
let echoSandboxScript : SandboxExec -> (OutputStream * string -> unit) -> Async<SandboxRun> =
    fun _ onChunk ->
        async {
            onChunk (Stdout, "ok")
            return SandboxExited 0
        }

/// Run one command in a sandbox and wait for its end, accumulating stdout/stderr —
/// the execute path without an event log, for backend-level tests.
let runInSandbox
    (sandbox: Sandbox)
    (executable: string)
    (args: string list)
    (env: Map<string, string>)
    (workingDirectory: string option)
    : Async<SandboxRun * string * string> =
    async {
        let out = System.Text.StringBuilder ()
        let err = System.Text.StringBuilder ()
        let! spawned =
            sandbox.Spawn
                { Executable = executable
                  Arguments = args
                  Env = env
                  WorkingDirectory = workingDirectory }
                (fun (stream, text) ->
                    (match stream with
                     | Stdout -> out
                     | Stderr -> err)
                        .Append text
                    |> ignore)
        match spawned with
        | Error reason -> return SandboxRunFailed reason, out.ToString (), err.ToString ()
        | Ok handle ->
            let! run = handle.Exited
            return run, out.ToString (), err.ToString ()
    }

/// Render the client view to an HTML string for markup assertions — through the very
/// renderer the served bootstrap uses (`Ssr`), so tests exercise the shipped SSR path.
/// The view's `ViewActions` are no-ops (handlers fire on live browser events only).
let render (model: ClientModel) : string = Ssr.renderModel model

/// Drive the OIDC authorization flow over plain HTTP, the way a browser would: a cookie
/// jar plus MANUAL redirect following. Manual matters twice — an auto-following fetch
/// drops intermediate `Set-Cookie` headers, and the hops cross ports (session → manager
/// → session), which must share this jar, not the runtime's.
module OidcHttp =

    type Jar = { mutable Cookies : Map<string, string> }

    let newJar () : Jar = { Cookies = Map.empty }

    /// The jar as a `cookie` request header. Public because a caller that has been through
    /// the sign-in bounce and then wants a plain request — an SSE stream, say — needs the
    /// same string this module sends, not its own spelling of it.
    let cookieHeader (jar: Jar) : string =
        jar.Cookies |> Map.toList |> List.map (fun (k, v) -> sprintf "%s=%s" k v) |> String.concat "; "

    type private ManualReply =
        abstract status : int
        abstract location : string
        abstract setCookies : string []
        abstract cacheControl : string
        abstract body : string

    /// Resolve a (possibly relative) URL against a base, exactly as a browser resolves a
    /// `Location` header against the request URI.
    [<Fable.Core.Emit("new URL($0, $1).href")>]
    let private resolveUrl (location: string) (baseUrl: string) : string = Fable.Core.Util.jsNative

    [<Fable.Core.Emit("""fetch($0, { redirect: 'manual', headers: { ...Object.fromEntries($2), cookie: $1 } }).then(async r => ({
      status: r.status,
      location: r.headers.get('location') || '',
      setCookies: r.headers.getSetCookie(),
      cacheControl: r.headers.get('cache-control') || '',
      body: await r.text() }))""")>]
    let private fetchManualWith (url: string) (cookie: string) (headers: (string * string) []) : Fable.Core.JS.Promise<ManualReply> = Fable.Core.Util.jsNative

    let private fetchManual (url: string) (cookie: string) : Fable.Core.JS.Promise<ManualReply> =
        fetchManualWith url cookie [||]

    let private store (jar: Jar) (setCookies: string []) =
        for header in setCookies do
            match header.Split ';' |> Array.tryHead with
            | Some pair ->
                match pair.IndexOf '=' with
                | i when i > 0 -> jar.Cookies <- Map.add (pair.Substring (0, i)) (pair.Substring (i + 1)) jar.Cookies
                | _ -> ()
            | None -> ()

    /// GET with the jar and extra request headers (what an authenticating proxy would
    /// assert on every hop — Plan 07), storing any cookies; no redirect following.
    let getWithJarAs (headers: (string * string) list) (jar: Jar) (url: string) : Async<{| Status: int; Location: string; CacheControl: string; Body: string |}> =
        async {
            let! reply = fetchManualWith url (cookieHeader jar) (Array.ofList headers) |> Async.AwaitPromise
            store jar reply.setCookies
            return {| Status = reply.status; Location = reply.location; CacheControl = reply.cacheControl; Body = reply.body |}
        }

    /// GET with the jar, storing any cookies; no redirect following.
    let getWithJar (jar: Jar) (url: string) : Async<{| Status: int; Location: string; CacheControl: string; Body: string |}> =
        getWithJarAs [] jar url

    /// Follow a redirect chain (capped) with the jar and extra headers on every hop,
    /// returning the final non-3xx reply.
    let followWithJarAs (headers: (string * string) list) (jar: Jar) (startUrl: string) : Async<{| Status: int; Location: string; CacheControl: string; Body: string |}> =
        // Resolve a `Location` against the request URI the way RFC 3986 (and every
        // browser) does. It used to graft the location onto `scheme://host:port`, which
        // only handles a location that is already absolute or root-anchored — so the
        // callback's `./`, and every redirect a session mounted under a path emits,
        // resolved to nonsense.
        let rec go (url: string) (hops: int) =
            async {
                let! reply = getWithJarAs headers jar url
                if reply.Status >= 300 && reply.Status < 400 && hops < 10 then
                    return! go (resolveUrl reply.Location url) (hops + 1)
                else
                    return reply
            }
        go startUrl 0

    /// Follow a redirect chain (capped) with the jar, returning the final non-3xx reply.
    let followWithJar (jar: Jar) (startUrl: string) : Async<{| Status: int; Location: string; CacheControl: string; Body: string |}> =
        followWithJarAs [] jar startUrl

    /// Log in to a session the way the browser client does — start at `loginPath`, ride
    /// the hops to the manager and back with `headers` on every hop (what an
    /// authenticating proxy would assert, Plan 07) — and return the jar plus a minted
    /// peer token from `/me`. Fails loudly on any non-success step.
    let openSessionVia (headers: (string * string) list) (loginPath: string) (sessionBaseUrl: string) : Async<{| Jar: Jar; PeerToken: string |}> =
        async {
            let jar = newJar ()
            let! landed = followWithJarAs headers jar (sessionBaseUrl + loginPath)
            if landed.Status <> 200 then
                failwithf "OIDC login chain ended with %d: %s" landed.Status landed.Body
            let! me = getWithJar jar (sessionBaseUrl + "/me")
            if me.Status <> 200 then failwithf "/me after login answered %d: %s" me.Status me.Body
            let token =
                match Decode.fromString (Decode.field "peerToken" Decode.string) me.Body with
                | Ok token -> token
                | Error e -> failwithf "malformed /me body: %s" e
            return {| Jar = jar; PeerToken = token |}
        }

    /// `openSessionVia` with no extra headers from `/login` — the plain localhost bounce.
    let openSession (sessionBaseUrl: string) : Async<{| Jar: Jar; PeerToken: string |}> =
        openSessionVia [] "/login" sessionBaseUrl

// --- The process env, taken and given back ------------------------------------------------
//
// A test that plants a variable and DELETES it on the way out has not restored the
// environment, it has cleared it — and the suite is one process, so everything compiled
// after it runs without. `Phase2`'s credential-leak regression did exactly that with
// `ANTHROPIC_API_KEY`: every `LiveAgent` suite after it got a session with no credential,
// which `SessionMain` answers by starting NO AGENT — so a turn simply never got a reply
// and nothing said why. Take-then-restore is one verb because the half that gives it back
// is the half a caller forgets.

// Runtime-aware, because this file is compiled for BOTH runtimes: a bare `jsNative` here is a
// trap that springs the first time a browser-tier suite reaches for the environment.

[<Fable.Core.Emit("process.env[$0] = $1")>]
let private jsSetEnv (name: string) (value: string) : unit = Fable.Core.Util.jsNative

[<Fable.Core.Emit("delete process.env[$0]")>]
let private jsUnsetEnv (name: string) : unit = Fable.Core.Util.jsNative

[<Fable.Core.Emit("(process.env[$0] ?? null)")>]
let private jsGetEnv (name: string) : string option = Fable.Core.Util.jsNative

let private setEnvRaw (name: string) (value: string) : unit =
    if Compiler.isDotnet then System.Environment.SetEnvironmentVariable (name, value) else jsSetEnv name value

let private unsetEnvRaw (name: string) : unit =
    if Compiler.isDotnet then System.Environment.SetEnvironmentVariable (name, null) else jsUnsetEnv name

let private getEnvRaw (name: string) : string option =
    if Compiler.isDotnet then
        match System.Environment.GetEnvironmentVariable name with
        | null -> None
        | v -> Some v
    else jsGetEnv name

/// Run `body` with these environment variables replaced (`None` removes one), then put back
/// exactly what was there — including absence — whether the body returns or throws.
let withEnv (bindings: (string * string option) list) (body: unit -> Async<'a>) : Async<'a> =
    async {
        let saved = bindings |> List.map (fun (name, _) -> name, getEnvRaw name)
        let apply (name, value) =
            match value with
            | Some v -> setEnvRaw name v
            | None -> unsetEnvRaw name
        bindings |> List.iter apply
        try
            return! body ()
        finally
            saved |> List.iter apply
    }

// --- Waiting on a signal that is not a model change ---------------------------------------
//
// For the few signals a model waiter cannot see (a file appearing, an SSE frame arriving, a hub
// publishing) — a model waiter is `Runner.WaitFor` and needs no polling.

let private pollMs = 50

/// The whole Node run's budget, as `check` computed it from the capabilities this run declared
/// (`tasks.fsx` `nodeBudgetMs`). Read per call rather than at module init so a test can drive it.
/// `None` when the bundle was started by hand, which is not an error — nothing is being spent.
let private runBudgetMs () : int option =
    getEnvRaw "YESSION_TEST_BUDGET_MS"
    |> Option.bind (fun raw ->
        match System.Int32.TryParse raw with
        | true, ms when ms > 0 -> Some ms
        | _ -> None)

/// Poll until the condition holds or `timeoutMs` elapses, ANSWERING which — for the few cases
/// that have something to say about not settling (the live clone case prints what the session
/// actually did before it fails). `waitUntilWithin` is this plus the failure, and is what
/// almost every wait wants.
///
/// A case's deadline is spent out of the whole run's budget, so one that exceeds it is refused
/// before it waits at all, naming both numbers. A 180s deadline under a 240s run budget once
/// took the runner down four minutes later, killing every other suite and naming none of them —
/// discovering that at the deadline rather than at the wait cost two red release runs.
let settledWithin (timeoutMs: int) (condition: unit -> bool) : Async<bool> =
    let rec go (remaining: int) =
        async {
            if condition () then return true
            elif remaining <= 0 then return false
            else
                do! Async.Sleep pollMs
                return! go (remaining - 1)
        }
    async {
        match runBudgetMs () with
        | Some budget when timeoutMs >= budget ->
            return
                failwithf
                    "a case asked to wait up to %dms, but this whole Node run's budget is %dms — reaching that deadline would kill every other suite with it. Narrow the run (`check --only \"<part of a test name>\"`), or give the tier a bigger allowance in tasks.fsx."
                    timeoutMs
                    budget
        | _ -> return! go (max 1 (timeoutMs / pollMs))
    }

/// Poll until the condition holds, failing loudly with `label` if it never does.
let waitUntilWithin (timeoutMs: int) (label: string) (condition: unit -> bool) : Async<unit> =
    async {
        let! held = settledWithin timeoutMs condition
        if not held then failwithf "timed out waiting for %s" label
    }

/// The everyday wait: 5s, a hang detector for a signal that normally arrives in milliseconds.
let waitUntil (label: string) (condition: unit -> bool) : Async<unit> = waitUntilWithin 5_000 label condition

/// Connect one full client to a host over an IN-MEMORY channel pair — the same drivers as
/// the WebRTC path (`Client.makeProgram` + `Client.connect` on one end, the Host's real per-peer
/// pump on the other via `host.Connect`), but with no WebRTC, HTTP, or native addon, so it
/// runs in the cheap tier. The peer token is minted from the host — what `/me` would serve
/// an authorized browser. Resolves once the model reaches `Connected`.
///
/// The options are built FROM the client's dispatch, mirroring the browser's `dispatchRef`:
/// a transport composed against dispatch (the event feed's resilience policy reports its
/// interim health that way) is therefore wired before the first frame moves, with no window
/// in which reports are dropped.
let connectInMemoryClientVia
    (makeOptions: (ClientMsg -> unit) -> Client.ConnectOptions)
    (host: Host.SessionHost)
    (id: string)
    (name: string)
    : Async<Client> =
    async {
        let carrier, serverEnd = Yession.SessionProcess.InMemoryChannel.createPair<string> ()
        // The Host drives the server end exactly as it would a WebRTC connection.
        host.Connect serverEnd
        // ...and this end is supervised exactly as the browser supervises its own. Not
        // decoration: the Host now holds every peer to a heartbeat, so a client that did not
        // answer one would be evicted three seconds into any test that sat still.
        let clientEnd = Link.supervise Link.LinkPolicy.shipped carrier
        let doc = Y.Doc.Create ()
        let local = peer id name
        let registry = BodyRegistry doc
        let texts = TextRegistry doc
        let runner = Harness.run (Client.makeProgram doc (ClientModel.init local))
        // As the browser wires it (see `connectClientWith`).
        DraftSlot.follow doc registry local.PeerId (user >> runner.Dispatch) |> ignore
        let hello = { PeerId = local.PeerId; DisplayName = name; Token = host.MintPeerToken () }
        let dispatch = user >> runner.Dispatch
        let options =
            { makeOptions dispatch with
                ReadPosition = Some (fun () -> (runner.Model ()).EventConsumer.LastProcessedOffset) }
        let connection = Client.connect options doc registry texts hello dispatch clientEnd
        Async.StartImmediate connection.Run
        do! runner.WaitFor (fun m -> m.Connection = Connected)
        return { Runner = runner; Connection = connection; Registry = registry; Texts = texts; Channel = clientEnd; Doc = doc; Hello = hello }
    }

/// `connectInMemoryClientVia` with options that do not depend on dispatch.
let connectInMemoryClientWith (options: Client.ConnectOptions) : Host.SessionHost -> string -> string -> Async<Client> =
    connectInMemoryClientVia (fun _ -> options)

/// `connectInMemoryClientWith` under the default options (events over frames).
let connectInMemoryClient : Host.SessionHost -> string -> string -> Async<Client> =
    connectInMemoryClientWith Client.ConnectOptions.defaults

/// Reconnect an existing client on a fresh channel, resuming event consumption from its
/// model's processed offset (E2E-4's catch-up path). Small pages force multi-page reads.
let reconnectClient (signalUrl: string) (client: Client) : Async<Client> =
    async {
        let! channel = WebRtc.connect signalUrl
        let options =
            { Client.ConnectOptions.defaults with
                ResumeAfter = (client.Runner.Model ()).EventConsumer.LastProcessedOffset
                PageSize = 2
                ReadPosition = Some (fun () -> (client.Runner.Model ()).EventConsumer.LastProcessedOffset) }
        let connection = Client.connect options client.Doc client.Registry client.Texts client.Hello (user >> client.Runner.Dispatch) channel
        Async.StartImmediate connection.Run
        do! client.Runner.WaitFor (fun m -> m.Connection = Connected)
        return { client with Connection = connection; Channel = channel }
    }

/// The body-agnostic seam. These helpers are the ONLY test code that touches a body
/// fragment; every suite drives drafts/queues through them, so no test outside the seam
/// knows the body is a `Y.XmlFragment`. Bodies are markdown strings at this boundary.
///
/// Body fragments are top-level doc roots (`BodyKey`), created idempotently by
/// `BodyRegistry.Fragment` (`doc.getXmlFragment`), so they are always available — no waiting to
/// anchor. Reading a peer's fragment before its content has synced yields the empty string
/// until the owner's update arrives (an empty root merges with the incoming one by name).
module Body =

    type Runner = Harness.Runner<ClientModel, Ylmish.Program.Message<ClientMsg>>

    /// Author a peer's draft body on a bare runner under an EXPLICIT queue key: publish the slot
    /// carrying that key, then write the markdown into its top-level body fragment. A bare runner
    /// has no `DraftSlot.follow` on its doc (that is client composition, `connectClientWith`), so
    /// the slot is dispatched here — the same slot the rule would publish, with the key named so a
    /// test can assert the queue entry it becomes.
    let authorAs (queueId: QueueId) (registry: BodyRegistry) (runner: Runner) (peer: PeerId) (markdown: string) : unit =
        runner.Dispatch (user (EnsureDraftMsg (peer, queueId)))
        Markdown.intoFragment markdown (registry.Fragment (BodyKey.draft peer))

    /// `authorAs` under a minted key — for tests that never name the queue entry.
    let author (registry: BodyRegistry) (runner: Runner) (peer: PeerId) (markdown: string) : unit =
        authorAs (QueueId.create (string (System.Guid.NewGuid ())) |> expect) registry runner peer markdown

    /// The queue key a peer's published draft carries, as any co-editor's send would read it.
    let queueKeyOf (runner: Runner) (peer: PeerId) : QueueId option =
        (runner.Model ()).Synced.Drafts |> Map.tryFind peer |> Option.map (fun draft -> draft.QueueId)

    /// Write a peer's draft body and NOTHING else — what typing into the composer does. The slot
    /// is whatever the publication rule makes of the content (`DraftSlot.follow`), so this is how
    /// a test drives that rule; `author` is the bare-runner shortcut that dispatches the slot too.
    /// The empty string empties the composer.
    /// The shared one (`Yession.Peer.writeBody`), named here so the seam still reads as one
    /// thing and no suite has to know which half of it moved.
    let write (registry: BodyRegistry) (peer: PeerId) (markdown: string) : unit =
        writeBody registry peer markdown

    /// Read a peer's draft body as markdown (the empty string before any content exists).
    let draft (registry: BodyRegistry) (peer: PeerId) : string option =
        Some (Markdown.ofFragment (registry.Fragment (BodyKey.draft peer)))

    /// The bare-runner analogue of `Connection.SendDraft`: capture the draft body, seed the queue
    /// fragment under the key the SLOT carries (the draft->queue content copy that shared Y types
    /// cannot do by re-parenting), then dispatch the enqueue. Returns the key it went in under, so
    /// a caller can assert the entry. A no-op returning `None` when nothing is published.
    let send (registry: BodyRegistry) (runner: Runner) (peer: PeerId) : QueueId option =
        match queueKeyOf runner peer with
        | None -> None
        | Some queueId ->
            // Seed the queue body BEFORE the entry (mirrors `Connection.SendDraft`): over an
            // ordered transport the body update reaches a draining Session Process before the
            // entry, so the drain never snapshots an entry whose body has not yet landed. Only
            // when there IS body text — an absent or empty draft fragment has nothing to copy.
            match draft registry peer with
            | Some md when md <> "" -> Markdown.intoFragment md (registry.Fragment (BodyKey.queued queueId))
            | _ -> ()
            runner.Dispatch (user (SendDraftMsg peer))
            // The composer empties after send (the body root is durable, not removed with the slot).
            Markdown.intoFragment "" (registry.Fragment (BodyKey.draft peer))
            Some queueId

    /// One queue entry's markdown, read straight from the doc (exactly the drain's read).
    let queued (doc: Y.Doc) (queueId: QueueId) : string =
        SyncedStateSync.queuedBodyMarkdown doc queueId

/// Empty a peer's composer on a full Client (select-all-delete, or the ✕), and wait for the
/// slot to go: publication follows the body, so an empty composer has no draft slot.
let clearComposer (client: Client) (peer: PeerId) : Async<unit> =
    async {
        Body.write client.Registry peer ""
        do! client.Runner.WaitFor (fun m -> not (Map.containsKey peer m.Synced.Drafts))
    }

/// Read a peer's draft body as markdown (the empty string until content has synced). Replaces
/// the old `bodyOf`.
let draftBody (client: Client) (peer: PeerId) : string option =
    Body.draft client.Registry peer

/// Read one queued entry's body as markdown, straight from the doc (the same read the
/// drain uses). Replaces the old `queueBodyOf`.
let queueBody (client: Client) (queueId: QueueId) : string =
    Body.queued client.Doc queueId

/// Every queued entry as `(queueId, markdown)`, in consumption order. Replaces `queueView`.
let queueBodies (client: Client) : (string * string) list =
    (client.Runner.Model ()).Synced.Queue
    |> QueueOrder.sorted
    |> List.map (fun entry -> QueueId.value entry.QueueId, queueBody client entry.QueueId)
