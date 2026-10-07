module Yession.Tests.ClaudeSdk

// The Claude Agent SDK binding (`src/Fable.ClaudeAgentSdk`).
//
// A type-check proves almost nothing about a binding: every fault this layer can have — an
// argument in the wrong position, a field named for the SDK's camelCase where the wire is
// snake_case, an option that should have been absent and arrived empty, a handler the SDK
// calls with two arguments and F# curried into one — compiles clean and fails at run time,
// in a live turn, as something else. The adapter over it (`app/Agent.fs`) is F# now, so the
// product does call every member below — but it calls them where a credential is needed,
// which is the one place a fault is expensive to find.
//
// So the cheap tier drives everything here that a credential is not needed for: the two
// constructors really run (the SDK is imported, not stubbed), the options object is read back
// for which keys it has, and the union classifiers are matched against messages shaped the
// way `sdk.d.ts` says they arrive. What is left over is the query itself, which spawns the
// CLI and spends money — one `LiveAgent` case, below.

open Fable.Core
open Fable.Core.JsInterop
open Fable.Pyxpecto
open Fable.ClaudeAgentSdk
open Thoth.Json
open Yession.Host

/// Whether a JS object HAS a key, which is the question `jsOptions` exists to answer: an
/// option nobody set must be absent, not present and empty.
let private hasKey (name: string) (options: Options) : bool =
    JS.Constructors.Object.keys options |> Seq.contains name

/// The raw shape of a tool that takes no arguments.
let private noArguments : Fable.Zod.RawShape = Fable.Zod.rawShape []

/// A tool that answers the same way every time. The handler is what proves the SDK's
/// two-argument call reaches an F# function at all.
let private ping () : ToolDefinition =
    tool
        "ping"
        "says pong"
        noArguments
        (jsOptions<ToolAnnotations> (fun a -> a.readOnlyHint <- true))
        (fun _ -> async { return ToolResult.ofText false "pong" } |> Async.StartAsPromise)

// --- what the SDK builds ------------------------------------------------------------------

let private constructionTests =
    testList "declaring a tool and its server" [
        testCase "the descriptor carries the name it was declared with" <| fun () ->
            Expect.equal (ping ()).name "ping" "the name reaches the descriptor"

        testCase "the descriptor carries the description it was declared with" <| fun () ->
            Expect.equal (ping ()).description "says pong" "the description reaches the descriptor"

        testCase "the descriptor carries the read-only hint it was declared with" <| fun () ->
            Expect.isTrue (ping ()).annotations.readOnlyHint "the annotations reach the descriptor"

        testCaseAsync "the handler answers the SDK's two-argument call with its result" <|
            async {
                // A curried F# lambda would answer this call with a FUNCTION, and the SDK
                // would await something that is not a promise.
                // `extra` is the MCP request context, which nothing reads.
                let! answer = (ping ()).handler.Invoke (ToolInput.ofJson "{}", null) |> Interop.awaitPromise
                Expect.equal answer.content.[0].text "pong" "the handler ran and its text came back"
            }

        testCase "the server carries the namespace it was named with" <| fun () ->
            let server = createSdkMcpServer "yession" "1.0.0" [| ping () |]
            Expect.equal server.name "yession" "the namespace the model sees in mcp__<ns>__<tool>"
    ]

let private inputTests =
    testList "what a tool is called with" [
        testCase "the arguments read back as the JSON they arrived as" <| fun () ->
            let text = """{"cwd":"repos/octocat/hello-world","depth":3}"""
            Expect.equal (ToolInput.json (ToolInput.ofJson text)) (Some text) "key order and all"

        testCase "arguments that are not an object still read back as what they are" <| fun () ->
            // Refusing a non-object is the adapter's decision, and it can only make it over
            // the text this hands over.
            Expect.equal (ToolInput.json (ToolInput.ofJson "[1,2]")) (Some "[1,2]") "an array is an array"
    ]

let private answerTests =
    testList "what a tool answers" [
        testCase "an answer is one text block carrying its text" <| fun () ->
            let answer = ToolResult.ofText false "forty-two"
            Expect.equal answer.content.[0].text "forty-two" "the text is the block's"

        // MCP's image block is `{ type: "image", data, mimeType }`, and a binding that named
        // either field the way the API's own image block does (`source.media_type`) compiles
        // and shows the model nothing.
        testCase "a picture is a text block and then an image block, in MCP's field names" <| fun () ->
            let answer = ToolResult.ofTextAndImage "shot.png" "image/png" "QUJD"
            Expect.equal
                (answer.content |> Array.map (fun c -> c.``type``))
                [| "text"; "image" |]
                "what was looked at, then the picture"
            Expect.equal (answer.content.[1].data, answer.content.[1].mimeType) ("QUJD", "image/png") "the bytes and their type"

        testCase "a protocol failure is flagged on the answer" <| fun () ->
            // `isError` is "the call did not happen", never "the tool ran and it went
            // badly" — the argument order is the only thing between those two.
            Expect.isTrue (ToolResult.ofText true "no such tool").isError "the failure flag is set"
    ]

// --- the options one turn runs under ------------------------------------------------------

let private optionTests =
    testList "the turn's options" [
        testCase "a model nobody chose is absent, not empty" <| fun () ->
            // An empty string would be this session inventing a model id of "". Absent is
            // what leaves the pick to the SDK.
            let options = jsOptions<Options> (fun o -> o.systemPrompt <- [| "be terse" |])
            Expect.isFalse (hasKey "model" options) "no model key at all"

        testCase "a chosen model is on the options" <| fun () ->
            let options = jsOptions<Options> (fun o -> o.model <- "claude-opus-5")
            Expect.equal options.model "claude-opus-5" "the choice reaches the SDK"

        testCase "no built-in tools is an empty list, not an absent option" <| fun () ->
            // The difference between dropping every built-in from the model's context and
            // handing it all of them.
            let options = jsOptions<Options> (fun o -> o.tools <- [||])
            Expect.isTrue (hasKey "tools" options) "the option is present, and empty"

        testCase "adaptive thinking asks for the summary" <| fun () ->
            let thinking = Thinking.adaptive Summarized
            Expect.equal thinking.display "summarized" "a summary rather than a signed empty block"
    ]

// --- starting a query ---------------------------------------------------------------------

let private queryTests =
    testList "starting a query" [
        // The SDK picks its CLI binary with a synchronous `process.report.getReport()`, and a
        // report that reads the network reverse-resolves every open socket on the event loop:
        // behind a resolver that never answers a PTR, a Session froze for ten seconds
        // at the start of every turn. The spawner refuses, so nothing is run — and the SDK has
        // already chosen its binary by the time it asks for a process.
        testCase "a started query leaves this process's diagnostic reports off the network" <| fun () ->
            let refusing = Spawner (fun _ -> failwith "this case spawns nothing")
            try
                query "hello" (jsOptions<Options> (fun o -> o.spawnClaudeCodeProcess <- refusing)) |> ignore
            with _ -> ()
            Expect.isFalse
                (Fable.NodeExtras.Processes.reportsReadTheNetwork ())
                "a report taken now would not reverse-resolve this process's sockets"

        // The system prompt goes to the CLI in the SDK's `initialize` request. A strategy
        // places a cache boundary between blocks, so the blocks a turn hands over have to be
        // the blocks the CLI gets: an SDK that joined them, or wrapped them again, would move
        // the boundary and nothing would fail. Nothing is run: the spawner hands back a
        // stand-in, and what the SDK writes to its stdin is read.
        testCaseAsync "the system prompt reaches the CLI as the blocks it was given" <|
            async {
                let blocks = [| "first block"; "second block" |]
                let stdin = Node.Api.stream.PassThrough.Create<string> ()
                let relay = Fable.NodeExtras.EventRelays.createRelay ()
                let spawner =
                    Spawner (fun _ ->
                        SpawnedProcess.standingIn
                            stdin
                            (Node.Api.stream.PassThrough.Create<string> ())
                            (Node.Api.stream.PassThrough.Create<string> ())
                            (fun () -> false)
                            (fun () -> None)
                            (fun _ -> true)
                            relay)
                // What the SDK writes waits in the stream until something reads it, so the
                // reader can start before the query does.
                let! reading =
                    Async.FromContinuations (fun (resolve, _, _) ->
                        let written = System.Text.StringBuilder ()
                        let mutable answered = false
                        stdin.on (
                            "data",
                            fun (chunk: obj) ->
                                written.Append (string chunk) |> ignore
                                let complete = written.ToString().Split '\n' |> Array.rev |> Array.tail
                                match complete |> Array.tryFind (fun line -> line.Contains "\"subtype\":\"initialize\"") with
                                | Some line when not answered ->
                                    answered <- true
                                    resolve line
                                | Some _
                                | None -> ())
                        |> ignore)
                    |> Async.StartChild
                query
                    "hello"
                    (jsOptions<Options> (fun o ->
                        o.systemPrompt <- blocks
                        o.spawnClaudeCodeProcess <- spawner))
                |> ignore
                let! initialize = reading
                // Ended the way a CLI ends: its process exits. An abort would be the SDK's own
                // cancel, which writes to the process after the abort and rejects where no case
                // is listening.
                relay.exited (Some 0, None)
                let sent =
                    Decode.fromString (Decode.field "request" (Decode.field "systemPrompt" (Decode.array Decode.string))) initialize
                Expect.equal sent (Ok blocks) "the blocks, as given"
            }
    ]

// --- narrowing what the query yields ------------------------------------------------------

let private messageOf (tag: string) : Message = jsOptions<Message> (fun m -> m.``type`` <- tag)

/// A delta carrying the tag and whichever of its fields the case sets.
let private deltaOf (tag: string) (fill: Delta -> unit) : Delta =
    jsOptions<Delta> (fun d ->
        d.``type`` <- tag
        fill d)

let private streamEventOf (tag: string) (delta: Delta option) : StreamEvent =
    jsOptions<StreamEvent> (fun e ->
        e.``type`` <- tag
        match delta with
        | Some delta -> e.delta <- delta
        | None -> ())

let private classificationTests =
    testList "narrowing a message off the query" [
        testCase "a stream event classifies as a stream event" <| fun () ->
            match Message.classify (messageOf "stream_event") with
            | MessageCase.StreamEvent _ -> ()
            | other -> failwithf "expected a stream event, got %A" other

        testCase "a result classifies as a result" <| fun () ->
            match Message.classify (messageOf "result") with
            | MessageCase.Result _ -> ()
            | other -> failwithf "expected a result, got %A" other

        testCase "a message this repository does not read keeps its tag" <| fun () ->
            // The tag rather than a discard, so a member of the union that starts mattering
            // is readable at the call site instead of indistinguishable from one dropped.
            match Message.classify (messageOf "system") with
            | MessageCase.Other tag -> Expect.equal tag "system" "the tag survives"
            | other -> failwithf "expected an unread message, got %A" other

        testCase "the model beginning a message classifies as a message start" <| fun () ->
            match StreamEvent.classify (streamEventOf "message_start" None) with
            | StreamEventCase.MessageStart -> ()
            | other -> failwithf "expected a message start, got %A" other

        testCase "a content block delta classifies with its delta" <| fun () ->
            let delta = deltaOf "text_delta" (fun d -> d.text <- "hi")
            match StreamEvent.classify (streamEventOf "content_block_delta" (Some delta)) with
            | StreamEventCase.ContentBlockDelta d -> Expect.equal d.text "hi" "the delta comes with it"
            | other -> failwithf "expected a content block delta, got %A" other

        testCase "the end of a content block classifies as a stop" <| fun () ->
            match StreamEvent.classify (streamEventOf "content_block_stop" None) with
            | StreamEventCase.ContentBlockStop -> ()
            | other -> failwithf "expected a content block stop, got %A" other

        testCase "a text delta classifies as text" <| fun () ->
            match Delta.classify (deltaOf "text_delta" (fun d -> d.text <- "pong")) with
            | DeltaCase.Text text -> Expect.equal text "pong" "the text the model said"
            | other -> failwithf "expected text, got %A" other

        testCase "a thinking delta classifies as thinking" <| fun () ->
            // Reasoning arrives on the same stream under its own delta. Told apart by the
            // tag, never by which field happens to hold a string — that test is what let a
            // thinking delta look exactly like an event nobody cared about.
            match Delta.classify (deltaOf "thinking_delta" (fun d -> d.thinking <- "hmm")) with
            | DeltaCase.Thinking thought -> Expect.equal thought "hmm" "what was thought"
            | other -> failwithf "expected thinking, got %A" other

        testCase "a delta kind this repository does not read keeps its tag" <| fun () ->
            match Delta.classify (deltaOf "signature_delta" ignore) with
            | DeltaCase.Other tag -> Expect.equal tag "signature_delta" "the tag survives"
            | other -> failwithf "expected an unread delta, got %A" other
    ]

let tests =
    testList "Claude Agent SDK binding" [
        constructionTests
        inputTests
        answerTests
        optionTests
        queryTests
        classificationTests
    ]

// -----------------------------------------------------------------------------
// Live — the one thing no stub can answer: that a real query, under options this
// binding built, iterates to a result whose usage is where this file says it is.
// Gated by `LiveAgent` alone-plus-`Ports` (it spawns the CLI); there is no second
// credential check here, which would turn a missing credential back into a skip.
// -----------------------------------------------------------------------------

/// A 16x16 PNG of one solid red, which no model could name from the tool's TEXT.
let private redSquare = "iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAIAAACQkWg2AAAAFklEQVR4nGO4KyhIEmIY1TCqYfhqAABd+f8BJ3/PcwAAAABJRU5ErkJggg=="

let liveTests =
    testList "Claude Agent SDK live" [
        // The other half of `ofTextAndImage`: that the SDK carries an MCP image block to the
        // model as a picture it SEES, rather than dropping it or flattening it to text. Only a
        // real turn can say, and the only proof is the model naming what is in the picture —
        // the tool's text says nothing about colour.
        testCaseAsync "a picture a tool answers with is one the model sees" <|
            async {
                let look =
                    tool
                        "look"
                        "shows you a picture"
                        noArguments
                        (jsOptions<ToolAnnotations> (fun a -> a.readOnlyHint <- true))
                        (fun _ -> async { return ToolResult.ofTextAndImage "here it is" "image/png" redSquare } |> Async.StartAsPromise)
                let options =
                    jsOptions<Options> (fun o ->
                        o.systemPrompt <- [| "Answer with one word and nothing else." |]
                        o.settingSources <- [||]
                        o.tools <- [||]
                        o.mcpServers <- McpServers.ofList [ "probe", createSdkMcpServer "probe" "1.0.0" [| look |] ]
                        o.allowedTools <- [| "mcp__probe__look" |])
                let running = query "Call the look tool, then reply with the colour of the picture it shows you, in one lower-case word." options
                let mutable ending : ResultMessage option = None
                let mutable finished = false
                while not finished do
                    let! step = running.next () |> Interop.awaitPromise
                    if step.``done`` then finished <- true
                    else
                        match Message.classify step.value with
                        | MessageCase.Result result -> ending <- Some result
                        | _ -> ()
                match ending with
                | Some result -> Expect.stringContains (result.result.ToLowerInvariant ()) "red" "the model named what only the picture said"
                | None -> failwith "the query never yielded a result message"
            }

        testCaseAsync "a query iterates to a result message carrying the turn's usage" <|
            async {
                let options =
                    jsOptions<Options> (fun o ->
                        o.systemPrompt <- [| "Answer with one word and nothing else." |]
                        o.settingSources <- [||]
                        o.tools <- [||]
                        o.allowedTools <- [||])
                let running = query "Reply with exactly: pong" options
                let mutable ending : ResultMessage option = None
                let mutable finished = false
                while not finished do
                    let! step = running.next () |> Interop.awaitPromise
                    if step.``done`` then
                        finished <- true
                    else
                        match Message.classify step.value with
                        | MessageCase.Result result -> ending <- Some result
                        | _ -> ()
                match ending with
                | Some result ->
                    // The field names are the API's snake_case, and nothing but a real turn
                    // can say whether this file guessed them right.
                    Expect.isTrue (result.usage.input_tokens > 0) "the turn's input tokens came back"
                | None -> failwith "the query never yielded a result message"
            }
    ]
