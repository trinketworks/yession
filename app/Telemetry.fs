module Yession.Host.Telemetry

// The OpenTelemetry emitter, shared by both the Manager and each Session Process — every
// process is a *direct* OTel emitter (there is no Manager-side collector). One OTel *log
// record* per completed agent turn carries token/cache counts — never message content.
//
// The signal is logs, not the GenAI `gen_ai.client.token.usage` metric, and that is a deliberate
// first cut: a metric needs a reader and an aggregation pipeline at BOTH ends, while one
// self-contained record per turn is legible to any OTLP logs backend with nothing configured.
// Metrics and traces generalise onto the same env selection and the same bindings when something
// actually needs them.
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
// Unset `OTEL_LOGS_EXPORTER` defaults to `console` (stdout visibility, no network); with only
// `console`, an absent collector simply means nothing is forwarded — the "otherwise drop" case.
// The Manager passes its OTEL_* environment through to each child (Spawn merges over
// `process.env`); it overrides only the child's identity (service.name/instance.id).

open Fable.Core
open Fable.Core.JsInterop
open Yession.Domain
open Yession.Domain.Agent

/// The binding layer (module `Fable.OpenTelemetry`), qualified for clarity in app code.
module OpenTelemetry = Fable.OpenTelemetry

[<Emit("Promise.resolve()")>]
let private resolved () : JS.Promise<unit> = jsNative

/// The telemetry sink plus a graceful flush. `Emit turnId usage` records one agent-turn log
/// record (session emitters); `Log body attrs` records a general log record (the Manager's
/// lifecycle signals); `Shutdown` flushes the batch processor (call on exit so buffered
/// records ship).
type Emitter =
    { Emit : AgentTurnId -> AgentUsage -> unit
      Log : string -> (string * obj) list -> unit
      Shutdown : unit -> JS.Promise<unit> }

/// Telemetry off: every emit is a no-op, shutdown resolves immediately.
let disabled : Emitter = { Emit = (fun _ _ -> ()); Log = (fun _ _ -> ()); Shutdown = resolved }

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
let private modelAttributes (models: ModelSpend list) : (string * obj) list =
    match models with
    | [] -> []
    | [ only ] -> [ "gen_ai.response.model", box only.Model ]
    | several ->
        [ "yession.agent.turn.models", box (several |> List.map (fun m -> m.Model) |> Array.ofList)
          "yession.agent.turn.models.input_tokens",
          box (several |> List.map (fun m -> m.InputTokens) |> Array.ofList)
          "yession.agent.turn.models.output_tokens",
          box (several |> List.map (fun m -> m.OutputTokens) |> Array.ofList)
          "yession.agent.turn.models.cache_read_input_tokens",
          box (several |> List.map (fun m -> m.CacheReadTokens) |> Array.ofList)
          "yession.agent.turn.models.cache_creation_input_tokens",
          box (several |> List.map (fun m -> m.CacheCreationTokens) |> Array.ofList) ]

/// Build and emit one log record on `logger` for a completed turn. Attribute names follow
/// the OTel GenAI conventions; the `cache_*` pair is an Anthropic extension. Identifiers
/// only — no message body, prompt, or completion ever appears here.
let emitTo (logger: OpenTelemetry.Logger) (sessionId: SessionId) (turnId: AgentTurnId) (usage: AgentUsage) : unit =
    let attributes =
        [ "gen_ai.system", box "anthropic"
          "gen_ai.operation.name", box "agent_turn"
          "gen_ai.usage.input_tokens", box usage.InputTokens
          "gen_ai.usage.output_tokens", box usage.OutputTokens
          "anthropic.usage.cache_read_input_tokens", box usage.CacheReadTokens
          "anthropic.usage.cache_creation_input_tokens", box usage.CacheCreationTokens
          "yession.session.id", box (SessionId.value sessionId)
          "yession.agent.turn.id", box (AgentTurnId.value turnId) ]
        @ modelAttributes usage.Models
    logger.emit (
        createObj
            [ "severityNumber", box OpenTelemetry.severityInfo
              "body", box "agent turn usage"
              "attributes", box (createObj attributes) ])

/// Emit one general log record (no session/turn context) — used by the Manager for its own
/// lifecycle signals (startup, session launch/exit).
let emitLogTo (logger: OpenTelemetry.Logger) (body: string) (attributes: (string * obj) list) : unit =
    logger.emit (
        createObj
            [ "severityNumber", box OpenTelemetry.severityInfo
              "body", box body
              "attributes", box (createObj attributes) ])

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
    OpenTelemetry.resource (createObj [ for KeyValue (k, v) in merged -> k, box v ])

// --- Exporter selection from OTEL_LOGS_EXPORTER ------------------------------------------

/// The processors selected by the environment: `console` → stdout, `otlp` → the collector
/// (endpoint from `OTEL_EXPORTER_OTLP_*`). Empty ⇒ telemetry disabled (`none`/`OTEL_SDK_DISABLED`).
let private processorsFromEnv () : OpenTelemetry.LogRecordProcessor list =
    if (Interop.envOr "OTEL_SDK_DISABLED" "").Trim().ToLowerInvariant() = "true" then []
    else
        (Interop.envOr "OTEL_LOGS_EXPORTER" "console").Split ','
        |> Array.map (fun s -> s.Trim().ToLowerInvariant ())
        |> Array.filter (fun s -> s <> "" && s <> "none")
        |> Array.distinct
        |> Array.choose (function
            | "console" -> Some (OpenTelemetry.simpleProcessor (OpenTelemetry.consoleLogExporter ()))
            | "otlp" -> Some (OpenTelemetry.batchProcessor (OpenTelemetry.otlpLogExporterFromEnv ()))
            | _ -> None)  // unknown exporter names are ignored, not fatal
        |> List.ofArray

/// Build an emitter over the given processors and identity. `emitFor` is the session this
/// emitter tags agent-turn records with (`None` for the Manager, whose `Emit` is inert).
/// Empty processors ⇒ `disabled`.
let private build
    (defaultServiceName: string)
    (instanceId: string option)
    (emitFor: SessionId option)
    (processors: OpenTelemetry.LogRecordProcessor list)
    : Emitter =
    match processors with
    | [] -> disabled
    | _ ->
        let provider = OpenTelemetry.loggerProviderMulti (resourceOf defaultServiceName instanceId) processors
        let logger = provider.getLogger defaultServiceName
        { Emit =
            (match emitFor with
             | Some sessionId -> fun turnId usage -> try emitTo logger sessionId turnId usage with _ -> ()
             | None -> fun _ _ -> ())
          Log = fun body attrs -> try emitLogTo logger body attrs with _ -> ()
          Shutdown = fun () -> provider.shutdown () }

// --- Constructors ------------------------------------------------------------------------

/// The Session Process emitter, configured from the environment (the Manager passes OTEL_*
/// through and sets this child's identity). Tags agent-turn records with `sessionId`.
let fromEnv (sessionId: SessionId) : Emitter =
    build "yession-session" (Some (SessionId.value sessionId)) (Some sessionId) (processorsFromEnv ())

/// The Manager's own emitter, configured from the environment. Identity `yession-manager`;
/// it emits lifecycle records via `Log` (never agent-turn usage).
let managerFromEnv () : Emitter =
    build "yession-manager" None None (processorsFromEnv ())

/// A session emitter forwarding OTLP to an explicit `url` (tests / programmatic use — the
/// stand-in for a real collector). Bypasses env exporter selection.
let createOtlp (sessionId: SessionId) (url: string) : Emitter =
    build "yession-session" (Some (SessionId.value sessionId)) (Some sessionId)
        [ OpenTelemetry.batchProcessor (OpenTelemetry.otlpLogExporter url (createObj [])) ]
