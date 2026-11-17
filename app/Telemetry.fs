module Yession.Host.Telemetry

// The OpenTelemetry emitter, shared by both the Manager and each Session — every
// process is a *direct* OTel emitter (there is no Manager-side collector). One OTel *log
// record* per completed agent turn carries token/cache counts — never message content.
//
// The signal is logs, not the GenAI `gen_ai.client.token.usage` metric, and that is a deliberate
// first cut: a metric needs a reader and an aggregation pipeline at BOTH ends, while one
// self-contained record per turn is legible to any OTLP logs backend with nothing configured.
// Traces sit on the same env selection and the same bindings (below).
//
// Metrics are for the one thing a record per event answers badly: how long something takes,
// across every time it happened. A duration is recorded into a histogram (`Timing`, below),
// which a backend reads as a distribution without replaying records. Token counts stay log
// records: each one belongs to a turn, and a sum of them is a query, not a signal.
//
// Fire-and-forget: a dropped export must never fail or stall a turn, so `Emit`/`Log` swallow
// everything and the batch processor exports asynchronously off the turn path.
//
// Destination is chosen by how the process is started, using the STANDARD OTel env vars —
// nothing hand-rolled, nothing bespoke:
//   OTEL_LOGS_EXPORTER      console | otlp | none  (comma-separated ⇒ tee, e.g. `console,otlp`)
//   OTEL_EXPORTER_OTLP_*    the collector endpoint/headers (read by the OTLP exporter itself)
//   OTEL_SERVICE_NAME       identity override (else the per-process default below)
//   OTEL_RESOURCE_ATTRIBUTES  extra/override resource attributes (k=v,k=v)
//   OTEL_SDK_DISABLED=true  hard off
//   OTEL_TRACES_EXPORTER    console | otlp | none  (the same reading; unset is `none`)
//   OTEL_METRICS_EXPORTER   console | otlp | none  (the same reading; unset is `none`)
// Unset `OTEL_LOGS_EXPORTER` defaults to `console` (stdout visibility, no network); with only
// `console`, an absent collector simply means nothing is forwarded — the "otherwise drop" case.
//
// Spans time the steps of a slow path (adding a repo: token, name check, sandbox, clone,
// listing), so where its time goes is measured rather than guessed.
// The Manager passes its OTEL_* environment through to each child (Spawn merges over
// `process.env`); it overrides only the child's identity (service.name/instance.id).

open Fable.Core
open Fable.Core.JsInterop
open Fable.NodeExtras
open Yession.Domain
open Yession.App
open Yession.Domain.Agent

/// The binding layer (module `Fable.OpenTelemetry`), qualified for clarity in app code.
module OpenTelemetry = Fable.OpenTelemetry

/// One attribute value on a log record — the binding's closed set, named here so a caller
/// that only reports lifecycle signals does not reach past this module into the binding.
type AttributeValue = OpenTelemetry.AttributeValue

let private resolved () : JS.Promise<unit> = JS.Constructors.Promise.resolve ()

/// Where spans go: nowhere, or an SDK tracer. A value rather than a global so the code that
/// times a step is handed the tracer the way it is handed its log — and a test hands it one
/// over an in-memory exporter.
[<RequireQualifiedAccess>]
type Tracer =
    | Off
    | On of OpenTelemetry.Tracer

/// The longest status message a failed span carries. A failure here is usually git's own
/// words, already capped upstream; this keeps a span from becoming a transcript.
let private statusMessageCap = 400

/// Run `work` as one span: it starts now, ends when `work` settles, and is the parent of every
/// span started inside it. `failed` reads the outcome — `Some reason` marks the span failed —
/// and a throw marks it failed with the exception's message before it propagates.
///
/// Telemetry must never change an outcome: the span is ended whatever `work` does, and with
/// the tracer off `work` runs exactly as written.
let spanWith
    (failed: 'a -> string option)
    (tracer: Tracer)
    (name: string)
    (attributes: (string * AttributeValue) list)
    (work: Async<'a>)
    : Async<'a> =
    match tracer with
    | Tracer.Off -> work
    | Tracer.On sdk ->
        async {
            let span = OpenTelemetry.startSpan sdk name (OpenTelemetry.Attributes.ofList attributes)
            let fail (reason: string) =
                OpenTelemetry.failSpan
                    span
                    (if reason.Length > statusMessageCap then reason.Substring (0, statusMessageCap) else reason)
            let! token = Async.CancellationToken
            // Started INSIDE the span's context, so every await in `work` carries it.
            //
            // Settled in the step that starts it, and not at the `let!` below. Work that
            // throws at once rejects its promise in this step. `Interop.awaitPromise`
            // attaches the handler at its call, and the trampoline can move this workflow
            // to a timer before it reaches the `let!`. Node ends a process for a rejection
            // that has no handler at the end of a turn.
            let settled =
                OpenTelemetry.withSpan span (fun () -> Async.StartAsPromise (work, token))
                |> Interop.awaitPromise
            try
                try
                    let! outcome = settled
                    failed outcome |> Option.iter fail
                    return outcome
                with e ->
                    fail e.Message
                    return raise e
            finally
                span.``end`` ()
        }

/// `spanWith` for work whose outcome needs no reading.
let span (tracer: Tracer) (name: string) (attributes: (string * AttributeValue) list) (work: Async<'a>) : Async<'a> =
    spanWith (fun _ -> None) tracer name attributes work

/// `spanWith` for work that answers a `Result`: an `Error` marks the span failed with it.
let spanResult
    (tracer: Tracer)
    (name: string)
    (attributes: (string * AttributeValue) list)
    (work: Async<Result<'a, string>>)
    : Async<Result<'a, string>> =
    spanWith (function Error reason -> Some reason | Ok _ -> None) tracer name attributes work

/// Time one request a session's HTTP server answers, as one server span: it starts now and
/// ends when the response is over, however that came about. Named by the route's shape
/// (`SessionRoute.pattern`) and never by its path, so the names are as few as the routes;
/// the mount is left out for the same reason, because it carries the session's id. A request
/// for nothing the session serves is named by its method alone.
///
/// Attach it with the server's `onRequestFirst`. It answers nothing and reads no body, so
/// the server's own handler sees the request exactly as it would without it.
let traceRequest (tracer: Tracer) (mount: string) (req: IncomingMessage) (res: ServerResponse) : unit =
    match tracer with
    | Tracer.Off -> ()
    | Tracer.On sdk ->
        let route =
            SessionRoute.parseUnder mount req.``method`` (Interop.pathnameOf req.url)
            |> Option.map (fun route -> "/" + SessionRoute.pattern route)
        let span =
            OpenTelemetry.startServerSpan
                sdk
                (match route with
                 | Some route -> req.``method`` + " " + route
                 | None -> req.``method``)
                (OpenTelemetry.Attributes.ofList [
                    "http.request.method", AttributeValue.String req.``method``
                    match route with
                    | Some route -> "http.route", AttributeValue.String route
                    | None -> () ])
        res.onceClosed (fun () ->
            let status = res.statusCode
            span.setAttributes (
                OpenTelemetry.Attributes.ofList [ "http.response.status_code", AttributeValue.Int status ])
            // The convention's rule for a server: only its own failures fail the span. A 4xx
            // is the caller's, and a 401 here is how a browser learns to sign in.
            if status >= 500 then OpenTelemetry.failSpan span (sprintf "HTTP %d" status)
            span.``end`` ())

/// A duration this repository records, as a histogram in seconds. Closed, so the set of
/// metric names and their attribute values is this type: a case carries only what has a few
/// possible values. A session's id is never one of them, because every value of an attribute
/// is a time series at the backend, and the resource already says which process recorded it.
/// The two parts of a session process's boot, which do not overlap and together are the
/// whole of it.
[<RequireQualifiedAccess>]
type BootPhase =
    /// From the system starting the process to the session's own code running: Node coming
    /// up and every module of the session being read and evaluated.
    | Load
    /// From there to the readiness line: the stores opened, the log read, the Host
    /// listening, the Manager told.
    | Start

[<RequireQualifiedAccess>]
type Timing =
    /// One session launch, as the Manager waits for it: from spawning the process to its
    /// readiness line (`ready`), or to the launch failing (a timeout, an early exit, a build
    /// the Manager refuses).
    | SessionLaunch of ready: bool
    /// One part of a session's boot, as the session itself measures it. Recorded only by a
    /// boot that reached its readiness line.
    | SessionBoot of BootPhase
    /// From an agent turn starting to its first output, as the session reads it off its own
    /// log (`TurnTimings`).
    | TurnFirstOutput
    /// From an agent turn starting to the event that ended it, read the same way.
    | Turn of TurnEnding
    /// From a work sandbox beginning to come up to its being up, or failing to, read off
    /// the log the same way (`SandboxTimings`). `backend` is one of the few a session can
    /// host, never a sandbox's name.
    | SandboxStart of backend: string * started: bool
    /// From the navigation that opened a session's page to one moment of its opening, as
    /// the page timed it and told the session (`OpenReport`).
    | ClientOpen of OpenMark

module Timing =
    /// The metric's name. Ours, under `yession.`: the conventions name no such duration.
    let name (timing: Timing) : string =
        match timing with
        | Timing.SessionLaunch _ -> "yession.session.launch.duration"
        | Timing.SessionBoot _ -> "yession.session.boot.duration"
        | Timing.TurnFirstOutput -> "yession.agent.turn.first_output.duration"
        | Timing.Turn _ -> "yession.agent.turn.duration"
        | Timing.SandboxStart _ -> "yession.sandbox.start.duration"
        | Timing.ClientOpen _ -> "yession.client.open.duration"

    let description (timing: Timing) : string =
        match timing with
        | Timing.SessionLaunch _ -> "From spawning a session process to its readiness line, or to the launch failing."
        | Timing.SessionBoot _ -> "One part of a session process's boot: loading its code, then starting the session."
        | Timing.TurnFirstOutput ->
            "From an agent turn starting to the first thing it does that a person sees: a word, a thought or a tool call."
        | Timing.Turn _ -> "From an agent turn starting to the event that ends it."
        | Timing.SandboxStart _ -> "From a work sandbox beginning to come up to its being up, or failing to."
        | Timing.ClientOpen _ ->
            "From the navigation that opened a session's page to one moment of its opening, as the page timed it."

    let attributes (timing: Timing) : (string * AttributeValue) list =
        match timing with
        | Timing.SessionLaunch ready ->
            [ "yession.session.launch.outcome", AttributeValue.String (if ready then "ready" else "failed") ]
        | Timing.SessionBoot phase ->
            [ "yession.session.boot.phase",
              AttributeValue.String (
                  match phase with
                  | BootPhase.Load -> "load"
                  | BootPhase.Start -> "start") ]
        | Timing.TurnFirstOutput -> []
        | Timing.Turn ending ->
            [ "yession.agent.turn.outcome",
              AttributeValue.String (
                  match ending with
                  | TurnEnding.Completed -> "completed"
                  | TurnEnding.Failed -> "failed"
                  | TurnEnding.Interrupted -> "interrupted") ]

        | Timing.SandboxStart (backend, started) ->
            [ "yession.sandbox.backend", AttributeValue.String backend
              "yession.sandbox.start.outcome", AttributeValue.String (if started then "started" else "failed") ]
        | Timing.ClientOpen mark -> [ "yession.client.open.mark", AttributeValue.String (OpenMark.name mark) ]

    /// A turn's timing as the metric it is recorded under, and how long it was.
    let ofTurn (timing: TurnTiming) : Timing * System.TimeSpan =
        match timing with
        | TurnTiming.FirstOutput took -> Timing.TurnFirstOutput, took
        | TurnTiming.Ended (ending, took) -> Timing.Turn ending, took

    /// The bucket edges, in seconds. A launch is under a second on a warm machine and is
    /// given up on after fifteen (`LaunchTimeoutMs`); a boot and a turn's first output live
    /// in the same range. A whole turn does not: one that calls tools runs for minutes, and
    /// one that works alone for an hour.
    let boundaries (timing: Timing) : float list =
        match timing with
        | Timing.Turn _ ->
            [ 1.0; 2.0; 5.0; 10.0; 20.0; 30.0; 60.0; 120.0; 300.0; 600.0; 1200.0; 1800.0; 3600.0; 7200.0 ]
        // Under a second for a confinement on the host, and minutes for a container that
        // pulls an image or assembles a toolchain first.
        | Timing.SandboxStart _ ->
            [ 0.1; 0.25; 0.5; 1.0; 2.0; 5.0; 10.0; 20.0; 30.0; 60.0; 120.0; 300.0; 600.0; 1200.0 ]
        // A page that kept its session shows it in a few tens of milliseconds, and one
        // that signs in and reads a long log takes seconds.
        | Timing.ClientOpen _ ->
            [ 0.05; 0.1; 0.25; 0.5; 0.75; 1.0; 1.5; 2.0; 3.0; 5.0; 7.5; 10.0; 15.0; 30.0; 60.0 ]
        | Timing.SessionLaunch _
        | Timing.SessionBoot _
        | Timing.TurnFirstOutput ->
            [ 0.1; 0.25; 0.5; 0.75; 1.0; 1.5; 2.0; 3.0; 5.0; 7.5; 10.0; 15.0; 30.0; 60.0 ]

/// Every duration a session reads off its own log, as one fold: the turns' and the work
/// sandboxes'. One, so that the listener a session puts on its log is one line, and a
/// duration added here is recorded without that line changing.
[<RequireQualifiedAccess>]
module SessionTimings =

    type State = private { Turns : TurnTimings.State; Sandboxes : SandboxTimings.State }

    let empty : State = { Turns = TurnTimings.empty; Sandboxes = SandboxTimings.empty }

    /// Take one appended event, written at `at`: the state after it, and each duration it
    /// completed, as the metric it is recorded under.
    let step (state: State) (at: System.DateTimeOffset) (event: SessionEvent) : State * (Timing * System.TimeSpan) list =
        let turns, turnTimings = TurnTimings.step state.Turns at event
        let sandboxes, sandboxTimings = SandboxTimings.step state.Sandboxes at event
        { Turns = turns; Sandboxes = sandboxes },
        (turnTimings |> List.map Timing.ofTurn)
        @ (sandboxTimings |> List.map (fun start -> Timing.SandboxStart (start.Backend, start.Started), start.Took))

/// The telemetry sink plus a graceful flush. `Emit turnId usage` records one agent-turn log
/// record (session emitters); `Log body attrs` records a general log record (the Manager's
/// lifecycle signals); `Time timing took` records one duration; `Tracer` is where spans go;
/// `Shutdown` flushes every signal (call on exit so what is buffered ships).
type Emitter =
    { Emit : AgentTurnId -> AgentUsage -> unit
      Log : string -> (string * AttributeValue) list -> unit
      Time : Timing -> System.TimeSpan -> unit
      Tracer : Tracer
      Shutdown : unit -> JS.Promise<unit> }

/// Record a page's opening as the page reported it, received at `receivedAt`: each moment
/// as a duration from the navigation, and the whole as one trace, a span from the navigation
/// to the last moment and under it a span for each stretch between two moments, named by the
/// moment it ends at.
///
/// The page sent only how long ago each thing was by its own count, so the navigation is
/// placed at `receivedAt` less the report's age: no clock of the page's is believed, and
/// the time the report spent on the wire is the error.
let recordOpening (emitter: Emitter) (receivedAt: System.DateTimeOffset) (report: OpenReport) : unit =
    for mark, at in report.Marks do
        emitter.Time (Timing.ClientOpen mark) (System.TimeSpan.FromMilliseconds at)
    match emitter.Tracer with
    | Tracer.Off -> ()
    | Tracer.On sdk ->
        let navigation = float (receivedAt.ToUnixTimeMilliseconds ()) - report.SentAt
        let inOrder = report.Marks |> List.sortBy snd
        let last = inOrder |> List.map snd |> List.fold max 0.0
        let none = OpenTelemetry.Attributes.ofList []
        let whole = OpenTelemetry.startSpanAt sdk "yession.client.open" none navigation
        OpenTelemetry.withSpan whole (fun () ->
            inOrder
            |> List.fold
                (fun (from: float) (mark, at) ->
                    let stretch = OpenTelemetry.startSpanAt sdk (OpenMark.name mark) none (navigation + from)
                    stretch.endAt (navigation + at)
                    at)
                0.0
            |> ignore)
        whole.endAt (navigation + last)

/// The route a page reports its opening to (`SessionRoute.OpenReported`), for the session's extra
/// routes. A page that is signed in may say it, once it has something to say; with no
/// sign-in configured, as on a session nobody fronts, any page may. The answer says only
/// that it was heard: 204, or why not.
let openReports
    (now: unit -> System.DateTimeOffset)
    (emitter: Emitter)
    (auth: SessionAuth.Auth option)
    (mount: string)
    : IncomingMessage -> ServerResponse -> bool =
    fun req res ->
        let answer (status: int) (body: string) =
            res.writeHead (status, [ ResponseHeader.ContentType "text/plain"; ResponseHeader.CacheControl "no-store" ])
            res.``end`` body
        match SessionRoute.parseUnder mount req.``method`` (Interop.pathnameOf req.url) with
        | Some OpenReported ->
            let allowed =
                match auth with
                | Some auth -> auth.IsAuthenticated req
                | None -> true
            if not allowed then answer 401 "unauthorized"
            else
                // Read to its end whatever its size, and kept only while it could still be
                // a report: a body past the limit is refused without being held.
                let body = System.Text.StringBuilder ()
                let mutable tooLarge = false
                Readables.text req (fun chunk ->
                    if body.Length + chunk.Length > OpenReport.largestBytes then tooLarge <- true
                    elif not tooLarge then body.Append chunk |> ignore)
                req.onEnd (fun () ->
                    if tooLarge then answer 413 "too large"
                    else
                        match OpenReport.ofJson (body.ToString ()) with
                        | Error _ -> answer 400 "not a report of an opening"
                        | Ok report ->
                            recordOpening emitter (now ()) report
                            res.writeHead (204, [ ResponseHeader.CacheControl "no-store" ])
                            res.``end`` "")
            true
        | _ -> false

/// Telemetry off: every emit is a no-op, shutdown resolves immediately.
let disabled : Emitter =
    { Emit = (fun _ _ -> ()); Log = (fun _ _ -> ()); Time = (fun _ _ -> ()); Tracer = Tracer.Off; Shutdown = resolved }

/// Which models the turn ran, as attributes.
///
/// `gen_ai.response.model` names THE model that produced the response, so a turn that ran two
/// — a fallback did what a fallback does — has no such single answer, and naming either of
/// them would be this process inventing one. So the convention's attribute is emitted only
/// where it means what it says, and everything else is reported as the breakdown the provider
/// actually gave: four parallel arrays under our own prefix, aligned with `...models` by
/// index, so a collector can read what each model spent without a key per model id.
///
/// A turn that reported no model at all gets neither. That is the honest reading of a
/// provider that said nothing, and it is what the previous shape could not express — it kept
/// whichever model an EARLIER ending had named, so a turn's last word about itself could be
/// overruled by its first.
let private modelAttributes (models: ModelSpend list) : (string * OpenTelemetry.AttributeValue) list =
    let counts (spent: ModelSpend -> int) =
        OpenTelemetry.AttributeValue.Ints (models |> List.map spent |> Array.ofList)
    match models with
    | [] -> []
    | [ only ] -> [ "gen_ai.response.model", OpenTelemetry.AttributeValue.String only.Model ]
    | several ->
        [ "yession.agent.turn.models",
          OpenTelemetry.AttributeValue.Strings (several |> List.map (fun m -> m.Model) |> Array.ofList)
          "yession.agent.turn.models.input_tokens", counts (fun m -> m.InputTokens)
          "yession.agent.turn.models.output_tokens", counts (fun m -> m.OutputTokens)
          "yession.agent.turn.models.cache_read_input_tokens", counts (fun m -> m.CacheReadTokens)
          "yession.agent.turn.models.cache_creation_input_tokens", counts (fun m -> m.CacheCreationTokens) ]

/// Build and emit one log record on `logger` for a completed turn. Attribute names follow
/// the OTel GenAI conventions; the `cache_*` pair is an Anthropic extension. Identifiers
/// only — no message body, prompt, or completion ever appears here.
/// Emit one general log record (no session/turn context) — used by the Manager for its own
/// lifecycle signals (startup, session launch/exit).
let emitLogTo
    (logger: OpenTelemetry.Logger)
    (body: string)
    (attributes: (string * OpenTelemetry.AttributeValue) list)
    : unit =
    logger.emit (
        jsOptions<OpenTelemetry.LogRecord> (fun r ->
            r.severityNumber <- OpenTelemetry.severityInfo
            r.body <- body
            r.attributes <- OpenTelemetry.Attributes.ofList attributes))

/// Build and emit one log record on `logger` for a completed turn. Attribute names follow
/// the OTel GenAI conventions; the `cache_*` pair is an Anthropic extension. Identifiers
/// only — no message body, prompt, or completion ever appears here.
let emitTo (logger: OpenTelemetry.Logger) (sessionId: SessionId) (turnId: AgentTurnId) (usage: AgentUsage) : unit =
    emitLogTo
        logger
        "agent turn usage"
        ([ "gen_ai.system", OpenTelemetry.AttributeValue.String "anthropic"
           "gen_ai.operation.name", OpenTelemetry.AttributeValue.String "agent_turn"
           "gen_ai.usage.input_tokens", OpenTelemetry.AttributeValue.Int usage.InputTokens
           "gen_ai.usage.output_tokens", OpenTelemetry.AttributeValue.Int usage.OutputTokens
           "anthropic.usage.cache_read_input_tokens", OpenTelemetry.AttributeValue.Int usage.CacheReadTokens
           "anthropic.usage.cache_creation_input_tokens",
           OpenTelemetry.AttributeValue.Int usage.CacheCreationTokens
           "yession.session.id", OpenTelemetry.AttributeValue.String (SessionId.value sessionId)
           "yession.agent.turn.id", OpenTelemetry.AttributeValue.String (AgentTurnId.value turnId) ]
         @ modelAttributes usage.Models)

/// How long a process waits for its telemetry to ship before it ends anyway. Shorter than
/// the ten seconds a container runtime gives a stop before it kills, and long enough for one
/// export to a collector that answers.
let shutdownDeadline = System.TimeSpan.FromSeconds 5.0

/// The emitter's shutdown, for a process that is ending: over when everything buffered has
/// shipped, when the shutdown fails, or when `deadline` has passed, whichever is first.
///
/// It never throws and never outlasts the deadline, because the caller's next act is to end
/// the process, and telemetry must not decide whether or when that happens. A collector
/// that does not answer costs the deadline and what was buffered, nothing more.
let shutdownWithin (clock: Clock) (deadline: System.TimeSpan) (emitter: Emitter) : Async<unit> =
    Async.FromContinuations (fun (resolve, _, _) ->
        let mutable settled = false
        let settle () =
            if not settled then
                settled <- true
                resolve ()
        Async.StartImmediate (
            async {
                try
                    do! Interop.awaitPromise (emitter.Shutdown ())
                with _ -> ()
                settle ()
            })
        Async.StartImmediate (
            async {
                do! clock.After deadline
                settle ()
            }))

/// From now on, a signal that stops this process ships its telemetry first.
///
/// On SIGTERM or SIGINT the emitter is shut down, within `shutdownDeadline`, and the process
/// then sends itself the same signal with nothing listening for it. So it ends the way the
/// signal would have ended it, and whoever sent the signal reads a process that the signal
/// stopped. A second signal during the wait ends the process at once (`onceSignalled`).
///
/// For a process whose only work at a stop is this. One that has more to do at a stop owns
/// its own ending and calls `shutdownWithin` from it.
let endOnSignal (clock: Clock) (emitter: Emitter) : unit =
    for signal in [ "SIGTERM"; "SIGINT" ] do
        Fable.NodeExtras.Processes.onceSignalled signal (fun () ->
            Async.StartImmediate (
                async {
                    do! shutdownWithin clock shutdownDeadline emitter
                    Fable.NodeExtras.Processes.signalSelf signal
                }))

// --- Resource identity (code default, overridable by the standard env vars) --------------

/// The operator's own `OTEL_RESOURCE_ATTRIBUTES`, as written. Read HERE rather than wherever
/// it happens to be wanted: this process overlays its own resource with it (below), and the
/// Manager prepends it to the identity it gives a child (`ProcessManager`), so a second reader
/// would be a second default for one value.
let inheritedResourceAttributes () : string = Interop.envOr "OTEL_RESOURCE_ATTRIBUTES" ""

/// Parse `OTEL_RESOURCE_ATTRIBUTES` (`k1=v1,k2=v2`) into pairs; malformed entries are skipped.
let private envResourceAttributes () : (string * string) list =
    match inheritedResourceAttributes () with
    | "" -> []
    | s ->
        s.Split ','
        |> Array.choose (fun kv ->
            match kv.Split ([| '=' |], 2) with
            | [| k; v |] when k.Trim().Length > 0 -> Some (k.Trim (), v.Trim ())
            | _ -> None)
        |> List.ofArray

/// The resource for this process: the per-process defaults (`service.name`, namespace, and an
/// optional `service.instance.id`) overlaid by `OTEL_RESOURCE_ATTRIBUTES` then `OTEL_SERVICE_NAME`
/// (env wins — the Manager sets these on a child to adapt its identity).
let private resourceOf (defaultServiceName: string) (instanceId: string option) : OpenTelemetry.Resource =
    let baseAttrs =
        [ "service.name", defaultServiceName
          // The BUILD these records came from — without it two releases' counts are
          // indistinguishable at the collector. It belongs here, in the code default, and NOT in
          // the OTEL_RESOURCE_ATTRIBUTES the Manager injects into a child (ProcessManager): env
          // wins below, so injecting it would make every session report the MANAGER's version and
          // hide exactly the skew this is here to expose. Each process reports its own.
          "service.version", Version.current
          "service.namespace", "yession" ]
        @ (match instanceId with Some id -> [ "service.instance.id", id ] | None -> [])
    // env attributes override the defaults; OTEL_SERVICE_NAME wins for service.name.
    let merged =
        (baseAttrs @ envResourceAttributes ())
        |> List.fold (fun acc (k, v) -> Map.add k v acc) Map.empty
    let merged =
        match Interop.envOr "OTEL_SERVICE_NAME" "" with
        | "" -> merged
        | name -> Map.add "service.name" name merged
    OpenTelemetry.resource (
        OpenTelemetry.Attributes.ofList [ for KeyValue (k, v) in merged -> k, OpenTelemetry.AttributeValue.String v ])

// --- Exporter selection from OTEL_LOGS_EXPORTER ------------------------------------------

/// The exporter names one `OTEL_*_EXPORTER` variable selects — `console`, `otlp`, both — read
/// the same way for every signal. `OTEL_SDK_DISABLED=true` selects none, whatever is named.
/// Unknown names are ignored, not fatal.
let private exportersNamed (variable: string) (fallback: string) : string list =
    if (Interop.envOr "OTEL_SDK_DISABLED" "").Trim().ToLowerInvariant() = "true" then []
    else
        (Interop.envOr variable fallback).Split ','
        |> Array.map (fun s -> s.Trim().ToLowerInvariant ())
        |> Array.filter (fun s -> s = "console" || s = "otlp")
        |> Array.distinct
        |> List.ofArray

/// The log processors `OTEL_LOGS_EXPORTER` selects: `console` → stdout, `otlp` → the
/// collector (endpoint from `OTEL_EXPORTER_OTLP_*`).
let private logProcessorsFromEnv () : OpenTelemetry.LogRecordProcessor list =
    exportersNamed "OTEL_LOGS_EXPORTER" "console"
    |> List.map (function
        | "console" -> OpenTelemetry.simpleProcessor (OpenTelemetry.consoleLogExporter ())
        | _ -> OpenTelemetry.batchProcessor (OpenTelemetry.otlpLogExporterFromEnv ()))

/// The span processors `OTEL_TRACES_EXPORTER` selects, read like the logs one.
///
/// Unset is `none`, unlike logs: a span is several lines of console dump where a log record
/// is one, so stdout-by-default would turn every session's output into a span listing.
/// Traces are for the operator who asked for them.
let private spanProcessorsFromEnv () : OpenTelemetry.SpanProcessor list =
    exportersNamed "OTEL_TRACES_EXPORTER" "none"
    |> List.map (function
        | "console" -> OpenTelemetry.simpleSpanProcessor (OpenTelemetry.consoleSpanExporter ())
        | _ -> OpenTelemetry.batchSpanProcessor (OpenTelemetry.otlpSpanExporterFromEnv ()))

/// The metric readers `OTEL_METRICS_EXPORTER` selects, read like the logs one. Unset is
/// `none`, for the traces' reason: the console exporter prints every instrument on every
/// interval.
let private metricReadersFromEnv () : OpenTelemetry.MetricReader list =
    exportersNamed "OTEL_METRICS_EXPORTER" "none"
    |> List.map (function
        | "console" -> OpenTelemetry.periodicReader (OpenTelemetry.consoleMetricExporter ())
        | _ -> OpenTelemetry.periodicReader (OpenTelemetry.otlpMetricExporterFromEnv ()))

/// What records a duration over the given readers, with its flush and its shutdown. No
/// readers ⇒ nothing is recorded and nothing is built.
///
/// One histogram per metric name, made the first time that name is recorded. Fire-and-forget
/// like the rest: a measurement that cannot be recorded is dropped.
let private timerOver
    (resource: OpenTelemetry.Resource)
    (name: string)
    (readers: OpenTelemetry.MetricReader list)
    : (Timing -> System.TimeSpan -> unit) * (unit -> JS.Promise<unit>) * (unit -> JS.Promise<unit>) =
    match readers with
    | [] -> (fun _ _ -> ()), resolved, resolved
    | _ ->
        let provider = OpenTelemetry.meterProvider resource readers
        let meter = provider.getMeter name
        let histograms = System.Collections.Generic.Dictionary<string, OpenTelemetry.Histogram> ()
        let histogramFor (timing: Timing) : OpenTelemetry.Histogram =
            let metric = Timing.name timing
            match histograms.TryGetValue metric with
            | true, histogram -> histogram
            | _ ->
                let histogram =
                    OpenTelemetry.durationHistogram meter metric (Timing.description timing) (Timing.boundaries timing)
                histograms.[metric] <- histogram
                histogram
        let time (timing: Timing) (took: System.TimeSpan) : unit =
            try
                (histogramFor timing).record (took.TotalSeconds, OpenTelemetry.Attributes.ofList (Timing.attributes timing))
            with _ -> ()
        time, (fun () -> provider.forceFlush ()), (fun () -> provider.shutdown ())

/// A tracer over the given span processors. None ⇒ `Tracer.Off`, and nothing is installed:
/// the async context manager goes in only for a process that records spans.
let private tracerOver
    (resource: OpenTelemetry.Resource)
    (name: string)
    (processors: OpenTelemetry.SpanProcessor list)
    : Tracer * (unit -> JS.Promise<unit>) =
    match processors with
    | [] -> Tracer.Off, resolved
    | _ ->
        OpenTelemetry.enableAsyncContext ()
        let provider = OpenTelemetry.tracerProvider resource processors
        Tracer.On (provider.getTracer name), fun () -> provider.shutdown ()

/// Build an emitter over the given processors, readers and identity. `emitFor` is the session
/// this emitter tags agent-turn records with (`None` for the Manager, whose `Emit` is inert).
/// Each signal with nothing to export to is off on its own.
let private build
    (defaultServiceName: string)
    (instanceId: string option)
    (emitFor: SessionId option)
    (logProcessors: OpenTelemetry.LogRecordProcessor list)
    (spanProcessors: OpenTelemetry.SpanProcessor list)
    (metricReaders: OpenTelemetry.MetricReader list)
    : Emitter =
    let resource = resourceOf defaultServiceName instanceId
    let tracer, shutdownTraces = tracerOver resource defaultServiceName spanProcessors
    let time, _, shutdownMetrics = timerOver resource defaultServiceName metricReaders
    let emit, log, shutdownLogs =
        match logProcessors with
        | [] -> disabled.Emit, disabled.Log, resolved
        | _ ->
            let provider = OpenTelemetry.loggerProviderMulti resource logProcessors
            let logger = provider.getLogger defaultServiceName
            (match emitFor with
             | Some sessionId -> fun turnId usage -> try emitTo logger sessionId turnId usage with _ -> ()
             | None -> fun _ _ -> ()),
            (fun body attrs -> try emitLogTo logger body attrs with _ -> ()),
            (fun () -> provider.shutdown ())
    { Emit = emit
      Log = log
      Time = time
      Tracer = tracer
      Shutdown =
        fun () ->
            async {
                do! Interop.awaitPromise (shutdownLogs ())
                do! Interop.awaitPromise (shutdownTraces ())
                do! Interop.awaitPromise (shutdownMetrics ())
            }
            |> Async.StartAsPromise }

// --- Constructors ------------------------------------------------------------------------

/// The Session emitter, configured from the environment (the Manager passes OTEL_*
/// through and sets this child's identity). Tags agent-turn records with `sessionId`.
let fromEnv (sessionId: SessionId) : Emitter =
    build
        "yession-session"
        (Some (SessionId.value sessionId))
        (Some sessionId)
        (logProcessorsFromEnv ())
        (spanProcessorsFromEnv ())
        (metricReadersFromEnv ())

/// The Manager's own emitter, configured from the environment. Identity `yession-manager`;
/// it emits lifecycle records via `Log` (never agent-turn usage).
let managerFromEnv () : Emitter =
    build "yession-manager" None None (logProcessorsFromEnv ()) (spanProcessorsFromEnv ()) (metricReadersFromEnv ())

/// A session emitter forwarding OTLP to an explicit `url` (tests / programmatic use — the
/// stand-in for a real collector). Bypasses env exporter selection. `deadline` bounds each
/// export, retries included (`OpenTelemetry.otlpLogExporter`). Logs only.
let createOtlp (sessionId: SessionId) (url: string) (deadline: System.TimeSpan) : Emitter =
    build "yession-session" (Some (SessionId.value sessionId)) (Some sessionId)
        [ OpenTelemetry.batchProcessor (OpenTelemetry.otlpLogExporter url Map.empty deadline) ]
        []
        []

/// A tracer exporting to `exporter` over this process's resource (tests: an in-memory
/// exporter). Installs the async context manager, as every recording tracer does.
let tracerTo (exporter: OpenTelemetry.SpanExporter) : Tracer =
    fst (tracerOver (resourceOf "yession-session" None) "yession-session" [ OpenTelemetry.simpleSpanProcessor exporter ])

/// What records a duration into `exporter`, and the flush that hands it what was recorded
/// (tests: an in-memory exporter, which would otherwise wait out the reader's interval).
let timerTo (exporter: OpenTelemetry.MetricExporter) : (Timing -> System.TimeSpan -> unit) * (unit -> JS.Promise<unit>) =
    let time, flush, _ =
        timerOver (resourceOf "yession-manager" None) "yession-manager" [ OpenTelemetry.periodicReader exporter ]
    time, flush
