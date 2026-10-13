module Yession.Host.Telemetry

// The OpenTelemetry emitter, shared by both the Manager and each Session — every
// process is a *direct* OTel emitter (there is no Manager-side collector). One OTel *log
// record* per completed agent turn carries token/cache counts — never message content.
//
// The signal is logs, not the GenAI `gen_ai.client.token.usage` metric, and that is a deliberate
// first cut: a metric needs a reader and an aggregation pipeline at BOTH ends, while one
// self-contained record per turn is legible to any OTLP logs backend with nothing configured.
// Traces sit on the same env selection and the same bindings (below); metrics would too, when
// something actually needs them.
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
// Unset `OTEL_LOGS_EXPORTER` defaults to `console` (stdout visibility, no network); with only
// `console`, an absent collector simply means nothing is forwarded — the "otherwise drop" case.
//
// Spans time the steps of a slow path (adding a repo: token, name check, sandbox, clone,
// listing), so where its time goes is measured rather than guessed.
// The Manager passes its OTEL_* environment through to each child (Spawn merges over
// `process.env`); it overrides only the child's identity (service.name/instance.id).

open Fable.Core
open Fable.Core.JsInterop
open Yession.Domain
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
            let running = OpenTelemetry.withSpan span (fun () -> Async.StartAsPromise (work, token))
            try
                try
                    let! outcome = Interop.awaitPromise running
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

/// The telemetry sink plus a graceful flush. `Emit turnId usage` records one agent-turn log
/// record (session emitters); `Log body attrs` records a general log record (the Manager's
/// lifecycle signals); `Tracer` is where spans go; `Shutdown` flushes both batch processors
/// (call on exit so buffered records ship).
type Emitter =
    { Emit : AgentTurnId -> AgentUsage -> unit
      Log : string -> (string * AttributeValue) list -> unit
      Tracer : Tracer
      Shutdown : unit -> JS.Promise<unit> }

/// Telemetry off: every emit is a no-op, shutdown resolves immediately.
let disabled : Emitter =
    { Emit = (fun _ _ -> ()); Log = (fun _ _ -> ()); Tracer = Tracer.Off; Shutdown = resolved }

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

/// Build an emitter over the given processors and identity. `emitFor` is the session this
/// emitter tags agent-turn records with (`None` for the Manager, whose `Emit` is inert).
/// Each signal with no processors is off on its own.
let private build
    (defaultServiceName: string)
    (instanceId: string option)
    (emitFor: SessionId option)
    (logProcessors: OpenTelemetry.LogRecordProcessor list)
    (spanProcessors: OpenTelemetry.SpanProcessor list)
    : Emitter =
    let resource = resourceOf defaultServiceName instanceId
    let tracer, shutdownTraces = tracerOver resource defaultServiceName spanProcessors
    match logProcessors with
    | [] -> { disabled with Tracer = tracer; Shutdown = shutdownTraces }
    | _ ->
        let provider = OpenTelemetry.loggerProviderMulti resource logProcessors
        let logger = provider.getLogger defaultServiceName
        { Emit =
            (match emitFor with
             | Some sessionId -> fun turnId usage -> try emitTo logger sessionId turnId usage with _ -> ()
             | None -> fun _ _ -> ())
          Log = fun body attrs -> try emitLogTo logger body attrs with _ -> ()
          Tracer = tracer
          Shutdown =
            fun () ->
                async {
                    do! Interop.awaitPromise (provider.shutdown ())
                    do! Interop.awaitPromise (shutdownTraces ())
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

/// The Manager's own emitter, configured from the environment. Identity `yession-manager`;
/// it emits lifecycle records via `Log` (never agent-turn usage).
let managerFromEnv () : Emitter =
    build "yession-manager" None None (logProcessorsFromEnv ()) (spanProcessorsFromEnv ())

/// A session emitter forwarding OTLP to an explicit `url` (tests / programmatic use — the
/// stand-in for a real collector). Bypasses env exporter selection. `deadline` bounds each
/// export, retries included (`OpenTelemetry.otlpLogExporter`). Logs only.
let createOtlp (sessionId: SessionId) (url: string) (deadline: System.TimeSpan) : Emitter =
    build "yession-session" (Some (SessionId.value sessionId)) (Some sessionId)
        [ OpenTelemetry.batchProcessor (OpenTelemetry.otlpLogExporter url Map.empty deadline) ]
        []

/// A tracer exporting to `exporter` over this process's resource (tests: an in-memory
/// exporter). Installs the async context manager, as every recording tracer does.
let tracerTo (exporter: OpenTelemetry.SpanExporter) : Tracer =
    fst (tracerOver (resourceOf "yession-session" None) "yession-session" [ OpenTelemetry.simpleSpanProcessor exporter ])
