module Yession.Tests.TelemetryE2E

// The first telemetry e2e. Cheap tier: a real Session Host with the real telemetry
// emit sink, exporting over real localhost HTTP to a stub OTLP collector (the stand-in for a
// real OpenTelemetry Collector). A human message (injected the Phase-3 way — an offline peer's
// enqueue delivered into the Host's doc) drains into a turn; the scripted agent completes with
// usage; the sink ships it; the collector records it. No WebRTC, no native addon.
//
// This is the regression guard for the silent-no-op class: any break in the wire (sink not
// threaded, exporter miswired, decode mismatch) shows up here as zero — or wrong — records at
// the collector.

open Fable.Core
open Fable.Core.JsInterop
open Fable.NodeExtras
open Fable.Pyxpecto
open Yjs
open Yession.Domain
open Yession.Domain.Agent
open Yession.Domain.Collab
open Yession.App.Collab
open Yession.App
open Yession.Host
open Yession.Tests.Support
open Yession.Peer

/// A scripted agent that completes with fixed usage — the credential-free stand-in for a real
/// turn that reports token/cache counts.
let private usageAgent (usage: AgentUsage) : RunAgent =
    fun _ _ _ _ -> async { return AgentCompleted ("scripted turn", Some usage) }

/// A finished request span as these cases read it: its name, the route and status it
/// carries, and whether it ended failed.
type private RequestSpan =
    { Name : string
      Route : string option
      Status : int
      Failed : bool }

let private requestSpans (exporter: Fable.OpenTelemetry.InMemorySpanExporter) : RequestSpan list =
    let decoder =
        Thoth.Json.Decode.map2
            (fun route status -> route, status)
            (Thoth.Json.Decode.optional "http.route" Thoth.Json.Decode.string)
            (Thoth.Json.Decode.field "http.response.status_code" Thoth.Json.Decode.int)
    [ for span in exporter.getFinishedSpans () do
        Expect.equal span.kind Fable.OpenTelemetry.spanKindServer "a request this process answered is a server span"
        match Thoth.Json.Decode.fromString decoder (Fable.OpenTelemetry.Attributes.toJson span.attributes) with
        | Error reason -> failwithf "a request span this suite cannot read: %s" reason
        | Ok (route, status) ->
            { Name = span.name
              Route = route
              Status = status
              Failed = span.status.code = Fable.OpenTelemetry.spanStatusError } ]

let private requestSpanTests =
    testList "request spans" [
        testCaseAsync "each request a session answers is one server span, named by its route's shape" <|
            async {
                let exporter = Fable.OpenTelemetry.inMemorySpanExporter ()
                let! host = Host.start (SessionId.create "e2e-request-spans" |> expect) 0
                host.ObserveRequests (Telemetry.traceRequest (Telemetry.tracerTo exporter) "")
                let at (path: string) = sprintf "http://127.0.0.1:%d/%s" host.Port path
                let token = host.MintPeerToken ()

                let! me = TestHttp.get (at "me")
                let! refused = TestHttp.get (at "events/0-99")
                let! range = TestHttp.get (at (sprintf "events/0-0?token=%s" token))
                let! unknown = TestHttp.get (at "nothing/here")
                Expect.equal (me.Status, refused.Status, range.Status, unknown.Status) (200, 401, 200, 404) "the answers are the server's own, untouched"

                // A span ends when its response closes, which the server learns after the
                // caller has its answer.
                do! waitUntil "four request spans" (fun () -> (exporter.getFinishedSpans ()).Length = 4)
                Expect.equal
                    (requestSpans exporter |> List.sortBy (fun span -> span.Name, span.Status))
                    [ { Name = "GET"; Route = None; Status = 404; Failed = false }
                      { Name = "GET /events/{first}-{last}"; Route = Some "/events/{first}-{last}"; Status = 200; Failed = false }
                      { Name = "GET /events/{first}-{last}"; Route = Some "/events/{first}-{last}"; Status = 401; Failed = false }
                      { Name = "GET /me"; Route = Some "/me"; Status = 200; Failed = false } ]
                    "one span per request, and a refusal is the caller's failure, not the span's"
                do! host.Stop ()
            }

        testCaseAsync "a request under a mount is named without it, and the server's own failure fails its span" <|
            async {
                let exporter = Fable.OpenTelemetry.inMemorySpanExporter ()
                let server =
                    createServer (fun _ res ->
                        res.writeHead (503, [ ResponseHeader.ContentType "text/plain" ])
                        res.``end`` "not now")
                server.onRequestFirst (System.Func<_, _, _> (Telemetry.traceRequest (Telemetry.tracerTo exporter) "/s/abc"))
                do! Async.FromContinuations (fun (cont, _, _) -> server.listen (0, "127.0.0.1", fun () -> cont ()) |> ignore)

                let! reply = TestHttp.get (sprintf "http://127.0.0.1:%d/s/abc/me" (serverPort server))
                Expect.equal reply.Status 503 "the handler's answer"
                do! waitUntil "the request span" (fun () -> (exporter.getFinishedSpans ()).Length = 1)
                Expect.equal
                    (requestSpans exporter)
                    [ { Name = "GET /me"; Route = Some "/me"; Status = 503; Failed = true } ]
                    "the session's id, which the mount carries, is in no name"
                do! Async.FromContinuations (fun (cont, _, _) -> server.close (fun _ -> cont ()))
            }
    ]

/// A server holding the one route a page reports its opening to, with `allowed` standing in
/// for the sign-in; what it recorded, as (mark, seconds); and how to stop it.
let private reportServer (allowed: bool option) =
    async {
        let recorded = ResizeArray<string * float> ()
        let emitter =
            { Telemetry.disabled with
                Time =
                    fun timing took ->
                        match timing with
                        | Telemetry.Timing.ClientOpen mark -> recorded.Add (OpenMark.name mark, took.TotalSeconds)
                        | _ -> () }
        let auth =
            allowed
            |> Option.map (fun allowed ->
                { (SessionAuth.create (SessionId.create "e2e-open-report" |> expect) "" Yession.Domain.Access.CookieScheme.AnyScheme Yession.Session.SignInStore.none) with
                    IsAuthenticated = fun _ -> allowed })
        let route = Telemetry.openReports (fun () -> (System.DateTimeOffset.FromUnixTimeMilliseconds 0L)) emitter auth ""
        let server =
            createServer (fun req res ->
                if not (route req res) then
                    res.writeHead (404, [ ResponseHeader.ContentType "text/plain" ])
                    res.``end`` "not found")
        do! Async.FromContinuations (fun (cont, _, _) -> server.listen (0, "127.0.0.1", fun () -> cont ()) |> ignore)
        let stop () = Async.FromContinuations (fun (cont, _, _) -> server.close (fun _ -> cont ()))
        return sprintf "http://127.0.0.1:%d/opened" (serverPort server), recorded, stop
    }

let private aReport = """{"marks":[{"mark":"booted","at":250},{"mark":"shown","at":1500}],"sentAt":1600}"""

let private openReportTests =
    testList "a page's report of its opening" [
        testCaseAsync "a report is heard, and each moment recorded as its time from the navigation" <|
            async {
                let! url, recorded, stop = reportServer None
                let! reply = TestHttp.post [] "text/plain" aReport url
                do! stop ()
                Expect.equal
                    (reply.Status, List.ofSeq recorded)
                    (204, [ "booted", 0.25; "shown", 1.5 ])
                    "in seconds, by the moment's name"
            }

        testCaseAsync "a page that is not signed in is refused, and nothing is recorded" <|
            async {
                let! url, recorded, stop = reportServer (Some false)
                let! reply = TestHttp.post [] "text/plain" aReport url
                do! stop ()
                Expect.equal (reply.Status, recorded.Count) (401, 0) "the report is a signed-in page's to make"
            }

        testCaseAsync "a signed-in page's report is heard" <|
            async {
                let! url, recorded, stop = reportServer (Some true)
                let! reply = TestHttp.post [] "text/plain" aReport url
                do! stop ()
                Expect.equal (reply.Status, recorded.Count) (204, 2) "both moments"
            }

        testCaseAsync "a body that is no report is refused, and nothing is recorded" <|
            async {
                let! url, recorded, stop = reportServer None
                let! reply = TestHttp.post [] "text/plain" """{"marks":[{"mark":"teleported","at":1}],"sentAt":2}""" url
                do! stop ()
                Expect.equal (reply.Status, recorded.Count) (400, 0) "said in the answer"
            }

        testCaseAsync "a body larger than any report is refused, and nothing is recorded" <|
            async {
                let! url, recorded, stop = reportServer None
                let! reply = TestHttp.post [] "text/plain" (String.replicate (OpenReport.largestBytes + 1) "x") url
                do! stop ()
                Expect.equal (reply.Status, recorded.Count) (413, 0) "without being read as one"
            }
    ]

let tests =
    testList "Telemetry E2E (real Host, stub collector, no WebRTC)" [
        requestSpanTests
        openReportTests
        testCaseAsync "a completed turn's usage reaches the collector over OTLP" <|
            async {
                // A stub OTLP collector on localhost — the stand-in for a real OTel Collector.
                let! stub = OtlpStub.start ()

                let sessionId = SessionId.create "e2e-telemetry" |> expect
                let usage : AgentUsage =
                    { InputTokens = 123; OutputTokens = 45; CacheReadTokens = 6; CacheCreationTokens = 7
                      Models =
                        [ { Model = "claude-opus-4-8"
                            InputTokens = 123
                            OutputTokens = 45
                            CacheReadTokens = 6
                            CacheCreationTokens = 7 } ] }
                let emitter = Telemetry.createOtlp sessionId stub.Url (System.TimeSpan.FromSeconds 10.0)

                // A turn-complete signal taken off the emit sink itself: it fires exactly when
                // a completed turn reports usage, so no polling and no sleeps.
                let mutable fired = false
                let mutable waiter : (unit -> unit) option = None
                let sink turnId u =
                    emitter.Emit turnId u
                    fired <- true
                    match waiter with
                    | Some resume -> waiter <- None; resume ()
                    | None -> ()

                let! host = Host.startFull Clock.system (fun () -> Some (usageAgent usage)) (fun _ -> None) (fun _ -> []) (fun _ -> async { return Error "this host has no repo service" }) None None None None None None None None sink None McpClient.McpConnections.none None sessionId None "" None false 0

                // Inject a human message the Phase-3 way: an offline peer builds the enqueue on
                // its own doc, then we deliver that state into the Host's doc, which drains it
                // into one turn.
                let peerDoc = Y.Doc.Create ()
                peerDoc.clientID <- 7.0
                let peerRegistry = BodyRegistry peerDoc
                let peerRunner = Harness.run (Client.makeProgram Client.Ports.offline peerDoc (ClientModel.init (peer "ada" "Ada")))
                let peerId = (peerRunner.Model ()).Peer.PeerId
                Body.authorAs (QueueId.create "q-1" |> expect) peerRegistry peerRunner peerId "trigger a turn"
                Body.send peerRegistry peerRunner peerId |> ignore
                Y.applyUpdate (host.Doc, Y.encodeStateAsUpdate peerDoc)

                // Wait for the turn's usage to be emitted, then flush the batch exporter so the
                // record has certainly reached the receiver.
                do! Async.FromContinuations (fun (cont, _, _) -> if fired then cont () else waiter <- Some cont)
                do! emitter.Shutdown () |> Interop.awaitPromise

                match stub.Received () |> List.choose OtlpStub.turnUsage with
                | [ u ] ->
                    Expect.equal u.SessionId "e2e-telemetry" "the record is tagged with the session id"
                    Expect.equal
                        (u.InputTokens, u.OutputTokens, u.CacheReadTokens, u.CacheCreationTokens)
                        (123, 45, 6, 7)
                        "the turn's counts crossed the wire intact"
                    Expect.equal u.Model (Some "claude-opus-4-8") "the model crossed the wire"
                    Expect.isFalse (System.String.IsNullOrEmpty u.TurnId) "a turn id is present"
                | other -> failwithf "expected exactly one usage record at the collector, got %d" (List.length other)

                do! host.Stop ()
                stub.Close ()
            }
    ]
