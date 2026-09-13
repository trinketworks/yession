module Yession.Tests.ClaudeSdk

// The Claude Agent SDK binding (`src/Fable.ClaudeAgentSdk`).
//
// A binding project that nothing calls yet is only TYPE-CHECKED, and a type-check proves
// almost nothing about a binding: every fault this layer can have — an argument in the wrong
// position, a field named for the SDK's camelCase where the wire is snake_case, an option
// that should have been absent and arrived empty, a handler the SDK calls with two arguments
// and F# curried into one — compiles clean and fails at run time, in a live turn, as
// something else.
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

/// Whether a JS object HAS a key, which is the question `jsOptions` exists to answer: an
/// option nobody set must be absent, not present and empty.
let private hasKey (name: string) (value: obj) : bool =
    JS.Constructors.Object.keys value |> Seq.contains name

let private noArguments : obj = createObj []

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
                let! answer = (ping ()).handler.Invoke (noArguments, noArguments) |> Async.AwaitPromise
                Expect.equal answer.content.[0].text "pong" "the handler ran and its text came back"
            }

        testCase "the server carries the namespace it was named with" <| fun () ->
            let server = createSdkMcpServer "yession" "1.0.0" [| ping () |]
            Expect.equal server.name "yession" "the namespace the model sees in mcp__<ns>__<tool>"
    ]

let private answerTests =
    testList "what a tool answers" [
        testCase "an answer is one text block carrying its text" <| fun () ->
            let answer = ToolResult.ofText false "forty-two"
            Expect.equal answer.content.[0].text "forty-two" "the text is the block's"

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
            let options = jsOptions<Options> (fun o -> o.systemPrompt <- "be terse")
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

// --- narrowing what the query yields ------------------------------------------------------

let private messageOf (tag: string) : Message =
    createObj [ "type" ==> tag ] |> unbox

let private deltaOf (delta: obj) : Delta = unbox delta

let private streamEventOf (tag: string) (delta: obj) : StreamEvent =
    createObj [ "type" ==> tag; "delta" ==> delta ] |> unbox

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
            match StreamEvent.classify (streamEventOf "message_start" null) with
            | StreamEventCase.MessageStart -> ()
            | other -> failwithf "expected a message start, got %A" other

        testCase "a content block delta classifies with its delta" <| fun () ->
            let delta = createObj [ "type" ==> "text_delta"; "text" ==> "hi" ]
            match StreamEvent.classify (streamEventOf "content_block_delta" delta) with
            | StreamEventCase.ContentBlockDelta d -> Expect.equal d.text "hi" "the delta comes with it"
            | other -> failwithf "expected a content block delta, got %A" other

        testCase "the end of a content block classifies as a stop" <| fun () ->
            match StreamEvent.classify (streamEventOf "content_block_stop" null) with
            | StreamEventCase.ContentBlockStop -> ()
            | other -> failwithf "expected a content block stop, got %A" other

        testCase "a text delta classifies as text" <| fun () ->
            match Delta.classify (deltaOf (createObj [ "type" ==> "text_delta"; "text" ==> "pong" ])) with
            | DeltaCase.Text text -> Expect.equal text "pong" "the text the model said"
            | other -> failwithf "expected text, got %A" other

        testCase "a thinking delta classifies as thinking" <| fun () ->
            // Reasoning arrives on the same stream under its own delta. Told apart by the
            // tag, never by which field happens to hold a string — that test is what let a
            // thinking delta look exactly like an event nobody cared about.
            match Delta.classify (deltaOf (createObj [ "type" ==> "thinking_delta"; "thinking" ==> "hmm" ])) with
            | DeltaCase.Thinking thought -> Expect.equal thought "hmm" "what was thought"
            | other -> failwithf "expected thinking, got %A" other

        testCase "a delta kind this repository does not read keeps its tag" <| fun () ->
            match Delta.classify (deltaOf (createObj [ "type" ==> "signature_delta" ])) with
            | DeltaCase.Other tag -> Expect.equal tag "signature_delta" "the tag survives"
            | other -> failwithf "expected an unread delta, got %A" other
    ]

let tests =
    testList "Claude Agent SDK binding" [
        constructionTests
        answerTests
        optionTests
        classificationTests
    ]

// -----------------------------------------------------------------------------
// Live — the one thing no stub can answer: that a real query, under options this
// binding built, iterates to a result whose usage is where this file says it is.
// Gated by `LiveAgent` alone-plus-`Ports` (it spawns the CLI); there is no second
// credential check here, which would turn a missing credential back into a skip.
// -----------------------------------------------------------------------------

let liveTests =
    testList "Claude Agent SDK live" [
        testCaseAsync "a query iterates to a result message carrying the turn's usage" <|
            async {
                let options =
                    jsOptions<Options> (fun o ->
                        o.systemPrompt <- "Answer with one word and nothing else."
                        o.settingSources <- [||]
                        o.tools <- [||]
                        o.allowedTools <- [||])
                let running = query "Reply with exactly: pong" options
                let mutable ending : ResultMessage option = None
                let mutable finished = false
                while not finished do
                    let! step = running.next () |> Async.AwaitPromise
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
