module Yession.Tests.OtlpStub

// A localhost OTLP/HTTP JSON logs receiver for tests — the stand-in for a real OpenTelemetry
// Collector now that the Manager no longer collects. It decodes the narrow OTLP logs JSON the
// emitter produces (`resourceLogs[].scopeLogs[].logRecords[]`) and records the LogRecords for
// assertions. A real Collector would receive the identical payload. Accepts a POST on any path
// (the exporter posts to `<endpoint>/v1/logs`), so callers point either an explicit url or
// `OTEL_EXPORTER_OTLP_ENDPOINT` at it.

open Fable.Core
open Fable.Core.JsInterop
open Thoth.Json
open Yession.Host

// --- Decode (the narrow OTLP/HTTP JSON logs subset the emitter emits) ---------------------

type LogValue =
    | StringValue of string
    | IntValue of int

type ReceivedLog =
    { Body : string
      Attributes : Map<string, LogValue>
      /// The attributes of the RESOURCE this record was emitted under (`service.name`,
      /// `service.version`, …). OTLP sends these once per payload, not per record, so they are
      /// folded onto each record here — but kept in their own field rather than merged into
      /// `Attributes`, so a test asserting a resource attribute cannot be satisfied by a
      /// record-level one of the same name.
      Resource : Map<string, LogValue> }

/// OTLP JSON encodes an int64 as a STRING, while the JS exporter emits a bare number for a
/// small one. Both spellings are the same integer, so the decoder names both rather than
/// asking `typeof` at the value and defaulting whatever it could not read to zero: a count
/// this stub cannot read is a payload we do not understand, and it says so by name.
let private otlpInt : Decoder<int> =
    Decode.oneOf
        [ Decode.int
          // A whole number that arrived as a float — JSON has one number type, and the
          // exporter's `1` and `1.0` are the same token to it.
          Decode.float |> Decode.map int
          Decode.string
          |> Decode.andThen (fun text ->
              match System.Int32.TryParse text with
              | true, value -> Decode.succeed value
              | _ -> Decode.fail (sprintf "an OTLP int64 is a number or a string of digits, not %s" text)) ]

/// One attribute's value. OTLP tags a value with the type it is (`stringValue`, `intValue`),
/// so the tag IS the case, and a value carrying neither is not an attribute this stub reads.
let private logValue : Decoder<LogValue> =
    Decode.oneOf
        [ Decode.field "stringValue" Decode.string |> Decode.map StringValue
          Decode.field "intValue" otlpInt |> Decode.map IntValue ]

/// The attributes of whatever carries them — a record, or the resource it was emitted under.
/// Absent is no attributes: a payload need not carry any, and that is not a malformed one.
///
/// An attribute whose value is a type this stub does not read is DROPPED rather than failing
/// the payload, and that is the one lenience here: OTLP has `boolValue`, `doubleValue`,
/// `arrayValue` and more, the emitter under test sends none of them, and a test asserting on
/// the attributes it does send should not go red because some future record carried a bool.
let private attributes : Decoder<Map<string, LogValue>> =
    Decode.optional
        "attributes"
        (Decode.list (Decode.object (fun get -> get.Required.Field "key" Decode.string, get.Optional.Field "value" logValue)))
    |> Decode.map (fun pairs ->
        pairs
        |> Option.defaultValue []
        |> List.choose (fun (key, value) -> value |> Option.map (fun v -> key, v))
        |> Map.ofList)

/// A record's body, which this stub only ever reads as text.
let private body : Decoder<string> =
    Decode.optional "body" (Decode.optional "stringValue" Decode.string)
    |> Decode.map (Option.flatten >> Option.defaultValue "")

let private logRecord (resource: Map<string, LogValue>) : Decoder<ReceivedLog> =
    Decode.map2
        (fun body attributes -> { Body = body; Attributes = attributes; Resource = resource })
        body
        attributes

/// A `resourceLogs` entry: the resource's own attributes, folded onto every record beneath it.
let private resourceLogs : Decoder<ReceivedLog list> =
    Decode.optional "resource" attributes
    |> Decode.map (Option.defaultValue Map.empty)
    |> Decode.andThen (fun resource ->
        Decode.optional "scopeLogs" (Decode.list (Decode.optional "logRecords" (Decode.list (logRecord resource))))
        |> Decode.map (fun scopes ->
            scopes
            |> Option.defaultValue []
            |> List.collect (Option.defaultValue [])))

let private payload : Decoder<ReceivedLog list> =
    Decode.optional "resourceLogs" (Decode.list resourceLogs)
    |> Decode.map (Option.defaultValue [] >> List.concat)

/// Decode an OTLP/HTTP JSON logs payload into the flat list of records it carries, each
/// carrying the attributes of the resource it was emitted under.
///
/// A body this cannot read is no records, NOT a throw: this runs inside a request handler
/// nobody is awaiting, and the stub's contract is that a malformed POST is answered rather
/// than crashing the suite. The reason goes to the console instead of being swallowed
/// silently, because "the emitter sent something we do not understand" and "the emitter sent
/// nothing" are different failures and a test that sees zero records cannot tell them apart.
let decode (json: string) : ReceivedLog list =
    match Decode.fromString payload json with
    | Ok records -> records
    | Error reason ->
        JS.console.debug ("OtlpStub: a POST body this stub cannot read: " + reason)
        []

// --- Accessors ---------------------------------------------------------------------------

let stringAttr (key: string) (r: ReceivedLog) : string option =
    match Map.tryFind key r.Attributes with Some (StringValue s) -> Some s | _ -> None

/// A string attribute of the record's RESOURCE — the emitting process's identity.
let resourceAttr (key: string) (r: ReceivedLog) : string option =
    match Map.tryFind key r.Resource with Some (StringValue s) -> Some s | _ -> None

let intAttr (key: string) (r: ReceivedLog) : int option =
    match Map.tryFind key r.Attributes with Some (IntValue i) -> Some i | _ -> None

/// The agent-turn usage a record carries, when it is one (the yession ids are present).
type TurnUsage =
    { SessionId : string
      TurnId : string
      InputTokens : int
      OutputTokens : int
      CacheReadTokens : int
      CacheCreationTokens : int
      Model : string option }

let turnUsage (r: ReceivedLog) : TurnUsage option =
    match stringAttr "yession.session.id" r, stringAttr "yession.agent.turn.id" r with
    | Some sessionId, Some turnId ->
        Some
            { SessionId = sessionId
              TurnId = turnId
              InputTokens = intAttr "gen_ai.usage.input_tokens" r |> Option.defaultValue 0
              OutputTokens = intAttr "gen_ai.usage.output_tokens" r |> Option.defaultValue 0
              CacheReadTokens = intAttr "anthropic.usage.cache_read_input_tokens" r |> Option.defaultValue 0
              CacheCreationTokens = intAttr "anthropic.usage.cache_creation_input_tokens" r |> Option.defaultValue 0
              Model = stringAttr "gen_ai.response.model" r }
    | _ -> None

// --- The server --------------------------------------------------------------------------

/// A running stub collector: its base URL, the decoded records so far, and a close.
type Stub =
    { Url : string
      Received : unit -> ReceivedLog list
      Close : unit -> unit }

/// Start a stub OTLP collector on localhost, calling `onRecord` for each decoded record (so a
/// caller can await arrival without polling). Any POST body is decoded and its records appended.
let startWith (onRecord: ReceivedLog -> unit) : Async<Stub> =
    async {
        let received = ResizeArray<ReceivedLog> ()
        let server =
            Interop.createServer (fun req res ->
                match req.``method`` with
                | "POST" ->
                    Interop.readBody req (fun body ->
                        for r in decode body do
                            received.Add r
                            onRecord r
                        res.writeHead (200, createObj [ "content-type", box "application/json" ]) |> ignore
                        res.``end`` "{}")
                | _ ->
                    res.writeHead (405, createObj [ "content-type", box "text/plain" ]) |> ignore
                    res.``end`` "method not allowed")
        let! url =
            Async.FromContinuations (fun (cont, _, _) ->
                server.listen (0, "127.0.0.1", fun () ->
                    cont (sprintf "http://127.0.0.1:%d" (Interop.serverPort server)))
                |> ignore)
        return
            { Url = url
              Received = fun () -> List.ofSeq received
              Close = fun () -> server.close ignore }
    }

/// Start a stub OTLP collector on localhost (no arrival signal).
let start () : Async<Stub> = startWith ignore
