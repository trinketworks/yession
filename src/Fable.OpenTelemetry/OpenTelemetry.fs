module Fable.OpenTelemetry

// Fable bindings to the OpenTelemetry JS SDK — the *logs* signal only, the slice the
// session-process telemetry emitter uses (Plan 04). Mirrors Fable.Dockerode: a distinct
// binding layer over an npm package that declares nothing beyond what we call. A ts2fable
// pass over the packages' .d.ts is the starting point; this is the hand-trimmed surface.
//
// Fable-only: `dotnet build` type-checks it; Fable emits the JS that runs on Node. Anything
// that returns a promise surfaces as `JS.Promise<_>` for `Async.AwaitPromise` at the call
// site. Handles we only ever pass back into the SDK are opaque empty interfaces.
//
// Packages (pinned centrally in package.json): @opentelemetry/{api,api-logs,sdk-logs,
// resources,exporter-logs-otlp-http}.

open Fable.Core
open Fable.Core.JsInterop

/// One attribute value, as the OTel data model allows it: a primitive, or a homogeneous array
/// of one. Closed to the cases this repository emits — widen it here when something needs a
/// case, rather than handing the SDK an untyped value.
[<RequireQualifiedAccess>]
type AttributeValue =
    | String of string
    | Int of int
    | Bool of bool
    | Strings of string array
    | Ints of int array

/// A flat attribute set, as the SDK takes and stores one: a plain object keyed by the
/// convention's dotted names. Opaque — built from typed pairs (`Attributes.ofList`), and read
/// back only as JSON (`Attributes.toJson`) for a decoder to judge.
type Attributes = interface end

/// A finished record's body. OTel allows any value there, so what comes back is opaque until
/// a reader decodes it (`LogBody.toJson`).
type LogBody = interface end

/// An OTel severity number (`SeverityNumber` in the data model). Only the SDK's own enum
/// hands these out (`severityInfo`), so no product code writes a magic number.
[<Erase>]
type SeverityNumber = private SeverityNumber of int

/// The record `Logger.emit` takes. The emitter builds one with `jsOptions`; this repository
/// only ever emits text as the body.
[<AllowNullLiteral>]
type LogRecord =
    abstract severityNumber : SeverityNumber with get, set
    abstract body : string with get, set
    abstract attributes : Attributes with get, set

/// A logger: emits one already-built log record.
type [<AllowNullLiteral>] Logger =
    abstract emit: record: LogRecord -> unit

/// Opaque handles: we construct these and pass them back into the SDK, never inspect them.
type [<AllowNullLiteral>] Resource = interface end
type [<AllowNullLiteral>] LogRecordProcessor = interface end
type [<AllowNullLiteral>] LogRecordExporter = interface end

/// The provider: hands out loggers and flushes / shuts down its processors.
type [<AllowNullLiteral>] LoggerProvider =
    abstract getLogger: name: string -> Logger
    abstract forceFlush: unit -> JS.Promise<unit>
    abstract shutdown: unit -> JS.Promise<unit>

/// A record the SDK has FINISHED — what an exporter is handed, and a different thing from the
/// plain object the emitter passes to `emit`. The SDK wraps that object and serves the body
/// back through a getter (`body` over its own `_body`), so a reader that treats a finished
/// record as plain data — stringifying it, walking its own properties — does not find one.
/// These two members are what this repository reads back; the rest stays undeclared.
type [<AllowNullLiteral>] ReadableLogRecord =
    /// Whatever the emitter set as the body. OTel allows any value; this repository only ever
    /// emits text, and the reader is what says so (`LogBody.toJson`, then a decoder).
    abstract body: LogBody
    /// The record's attributes, flat, as the SDK stores them.
    abstract attributes: Attributes

/// In-memory exporter (tests): finished records accumulate in memory for assertions. It is
/// a `LogRecordExporter`, so it drops straight into a processor.
type [<AllowNullLiteral>] InMemoryLogRecordExporter =
    inherit LogRecordExporter
    abstract getFinishedLogRecords: unit -> ReadableLogRecord array
    abstract reset: unit -> unit

/// Console exporter: writes finished records to stdout. A `LogRecordExporter`, so it drops
/// straight into a processor — the standard "log to stdout" leg of a tee.
type [<AllowNullLiteral>] ConsoleLogRecordExporter =
    inherit LogRecordExporter

// --- Construction -------------------------------------------------------------------------
// Every constructor is an import typed by the options it takes, so an options object is
// built with `jsOptions` against a declared shape rather than as a bag of names.

[<AllowNullLiteral>]
type private LoggerProviderConfig =
    abstract resource : Resource with get, set
    abstract processors : LogRecordProcessor array with get, set

[<AllowNullLiteral>]
type private ProcessorConfig =
    abstract exporter : LogRecordExporter with get, set

/// A header-name → value dictionary, which is what the OTLP exporter reads; built from a
/// typed map in `otlpLogExporter`.
type private OtlpHeaders = interface end

[<AllowNullLiteral>]
type private OtlpExporterConfig =
    abstract url : string with get, set
    abstract headers : OtlpHeaders with get, set
    abstract timeoutMillis : float with get, set

type private LoggerProviderClass =
    [<EmitConstructor>]
    abstract Create : config: LoggerProviderConfig -> LoggerProvider

type private ProcessorClass =
    [<EmitConstructor>]
    abstract Create : config: ProcessorConfig -> LogRecordProcessor

type private OtlpExporterClass =
    [<EmitConstructor>]
    abstract Create : config: OtlpExporterConfig -> LogRecordExporter
    [<EmitConstructor>]
    abstract Create : unit -> LogRecordExporter

type private ConsoleExporterClass =
    [<EmitConstructor>]
    abstract Create : unit -> ConsoleLogRecordExporter

type private InMemoryExporterClass =
    [<EmitConstructor>]
    abstract Create : unit -> InMemoryLogRecordExporter

/// The `SeverityNumber` enum object; only the members this repository uses are declared.
type private SeverityNumbers =
    abstract INFO : SeverityNumber

[<Import("LoggerProvider", "@opentelemetry/sdk-logs")>]
let private loggerProviderClass : LoggerProviderClass = jsNative
[<Import("BatchLogRecordProcessor", "@opentelemetry/sdk-logs")>]
let private batchProcessorClass : ProcessorClass = jsNative
[<Import("SimpleLogRecordProcessor", "@opentelemetry/sdk-logs")>]
let private simpleProcessorClass : ProcessorClass = jsNative
[<Import("InMemoryLogRecordExporter", "@opentelemetry/sdk-logs")>]
let private inMemoryExporterClass : InMemoryExporterClass = jsNative
[<Import("ConsoleLogRecordExporter", "@opentelemetry/sdk-logs")>]
let private consoleExporterClass : ConsoleExporterClass = jsNative
[<Import("OTLPLogExporter", "@opentelemetry/exporter-logs-otlp-http")>]
let private otlpExporterClass : OtlpExporterClass = jsNative
[<Import("resourceFromAttributes", "@opentelemetry/resources")>]
let private resourceFromAttributes (attributes: Attributes) : Resource = jsNative
[<Import("SeverityNumber", "@opentelemetry/api-logs")>]
let private severityNumbers : SeverityNumbers = jsNative

/// A flat JS object from typed pairs — the one place a name-keyed bag is built, behind the
/// opaque types that leave this module. A later pair for the same key wins, as it would in
/// the object literal.
let private flat (pairs: (string * obj) seq) : 'T = createObj pairs |> unbox<'T>

/// JSON text of a value the SDK handed back. `JSON.stringify` answers `undefined` rather than
/// text for an absent value, and JSON's own word for absent is `null`, so that is what a
/// reader is given — a decoder then refuses it by name instead of receiving a non-string.
let private json (value: obj) : string =
    let text = JS.JSON.stringify value
    if isNull (box text) then "null" else text

[<RequireQualifiedAccess>]
module Attributes =
    let private raw (value: AttributeValue) : obj =
        match value with
        | AttributeValue.String s -> box s
        | AttributeValue.Int n -> box n
        | AttributeValue.Bool b -> box b
        | AttributeValue.Strings xs -> box xs
        | AttributeValue.Ints xs -> box xs

    /// An attribute set from typed pairs.
    let ofList (pairs: (string * AttributeValue) list) : Attributes =
        flat [ for key, value in pairs -> key, raw value ]

    /// The set as JSON text, for a reader to decode.
    let toJson (attributes: Attributes) : string = json attributes

[<RequireQualifiedAccess>]
module LogBody =
    /// The body as JSON text, for a reader to decode.
    let toJson (body: LogBody) : string = json body

/// The OTel logs severity number for INFO (9 in the data model), read from the SDK enum so
/// it tracks the package rather than a magic literal.
let severityInfo : SeverityNumber = severityNumbers.INFO

/// A Resource from a flat attribute set, e.g. `service.name` = `yession-session`.
let resource (attributes: Attributes) : Resource = resourceFromAttributes attributes

/// A LoggerProvider fanning one emit out to several processors — the SDK-native tee
/// (each exporter gets a copy). E.g. console + OTLP: "log to stdout AND forward."
let loggerProviderMulti (resource: Resource) (processors: LogRecordProcessor list) : LoggerProvider =
    loggerProviderClass.Create (
        jsOptions<LoggerProviderConfig> (fun c ->
            c.resource <- resource
            c.processors <- Array.ofList processors))

/// A LoggerProvider wired to one processor over the given resource (SDK 2.x config form).
let loggerProvider (resource: Resource) (processor: LogRecordProcessor) : LoggerProvider =
    loggerProviderMulti resource [ processor ]

// Both processors take an options object `{ exporter; ... }` (SDK 2.x) — NOT the bare
// exporter. Passing the exporter directly silently no-ops: the export throws on
// `undefined.export` and the processor swallows it via the global error handler.

/// Batch processor: exports asynchronously off the caller's path (production).
let batchProcessor (exporter: LogRecordExporter) : LogRecordProcessor =
    batchProcessorClass.Create (jsOptions<ProcessorConfig> (fun c -> c.exporter <- exporter))

/// Simple processor: exports per record (tests).
let simpleProcessor (exporter: LogRecordExporter) : LogRecordProcessor =
    simpleProcessorClass.Create (jsOptions<ProcessorConfig> (fun c -> c.exporter <- exporter))

/// OTLP/HTTP logs exporter (JSON) posting to `url`, with the given headers (name → value).
///
/// `deadline` bounds one export, retries included: the SDK retries a refused connect with a
/// backoff starting at a second, and stops once the next attempt would land past it.
let otlpLogExporter (url: string) (headers: Map<string, string>) (deadline: System.TimeSpan) : LogRecordExporter =
    otlpExporterClass.Create (
        jsOptions<OtlpExporterConfig> (fun c ->
            c.url <- url
            c.headers <- flat [ for KeyValue (name, value) in headers -> name, box value ]
            c.timeoutMillis <- deadline.TotalMilliseconds))

/// OTLP/HTTP logs exporter (JSON) self-configured from the environment — no explicit url:
/// the SDK reads `OTEL_EXPORTER_OTLP_LOGS_ENDPOINT`/`OTEL_EXPORTER_OTLP_ENDPOINT` (+ `_HEADERS`).
/// This is the standard "point me at the collector via env" form.
let otlpLogExporterFromEnv () : LogRecordExporter = otlpExporterClass.Create ()

/// Console exporter (stdout) — the standard "log to stdout" leg of a tee.
let consoleLogExporter () : LogRecordExporter = consoleExporterClass.Create ()

/// In-memory exporter for tests.
let inMemoryExporter () : InMemoryLogRecordExporter = inMemoryExporterClass.Create ()
