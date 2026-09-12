module Yession.Tests.Summaries

// Writing a few words, at the provider end of the `Summarize` seam.
//
// The domain's half — what to ask about a chapter, and what may be made of an answer — is
// pinned where it costs nothing (`Domain.fs`). What only a real request settles is the shape
// of the conversation with the provider: what is sent, how the credential presents itself,
// and which of the several ways an answer can be useless are told apart. All three are
// invisible to any in-memory stand-in, because all three ARE the HTTP.
//
// Ports, for that reason.

open Fable.Core
open Fable.Pyxpecto
open Yession.Domain.Agent
open Yession.App
open Yession.Host

/// Start a server on a free port and answer with `handler`, which sees the request.
let private serving (handler: Interop.IncomingMessage -> Interop.ServerResponse -> unit) =
    async {
        let server = Interop.createServer handler
        let! listening =
            Async.FromContinuations (fun (cont, _, _) -> server.listen (0, "127.0.0.1", fun () -> cont server) |> ignore)
        return sprintf "http://127.0.0.1:%d" (Interop.serverPort listening), server
    }

let private json (res: Interop.ServerResponse) (status: int) (body: string) =
    res.writeHead (status, JsInterop.createObj [ "content-type", box "application/json" ]) |> ignore
    res.``end`` body

[<Emit("(function (req, f) { let body = ''; req.on('data', c => body += c); req.on('end', () => f(body)) })($0, $1)")>]
let private onBody (req: Interop.IncomingMessage) (f: string -> unit) : unit = Util.jsNative

/// A provider that answers every ask with `said`, and records what it was asked.
let private answering (said: string) (seen: ResizeArray<string>) =
    fun (req: Interop.IncomingMessage) (res: Interop.ServerResponse) ->
        onBody req (fun body ->
            seen.Add body
            json res 200 (sprintf """{"content":[{"type":"text","text":"%s"}],"stop_reason":"end_turn"}""" said))

let private ask (lines: string list) : SummaryAsk =
    { Task = "Name this part of a working session."; Lines = lines; Budget = 48 }

let private expectOk (result: Result<string, string>) =
    match result with
    | Ok said -> said
    | Error e -> failwithf "expected words, got: %s" e

let portsTests =
    testList "Writing a few words" [

        testCaseAsync "the words come back as the provider wrote them" <|
            async {
                // Shaped by the caller, never here: what this end owes is the answer intact,
                // and a provider adapter that also trimmed would be a second opinion about
                // what a name may look like.
                let! url, server = serving (answering "The rollback" (ResizeArray ()))
                let! said = ClaudeConnection.summarizeAt url ("ANTHROPIC_API_KEY", "sk-ant-test") (ask [ "we reverted it" ])
                server.close ignore
                Expect.equal (expectOk said) "The rollback" "the provider's words, whole"
            }

        testCaseAsync "what to read is sent, and what it is for is sent as the instruction" <|
            async {
                // The seam's promise from the provider's side: an implementation is handed
                // the task, so the register and the length are the caller's to decide and
                // not something each provider invents.
                let seen = ResizeArray<string> ()
                let! url, server = serving (answering "A name" seen)
                let! _ =
                    ClaudeConnection.summarizeAt url ("ANTHROPIC_API_KEY", "sk-ant-test")
                        { ask [ "first thing"; "second thing" ] with Task = "Name this part." }
                server.close ignore
                let sent = String.concat "" seen
                Expect.isTrue (sent.Contains "Name this part.") "the task reached the provider"
                Expect.isTrue (sent.Contains "first thing" && sent.Contains "second thing") "and so did the lines"
            }

        testCaseAsync "an api key presents itself as one, and an oauth grant as a bearer" <|
            async {
                // The same credential rule the catalogue spends, now that two requests spend
                // it: a grant offered as an API key is refused with a 401, which reads
                // exactly like a credential that has gone stale.
                let seen = ResizeArray<string> ()
                let record =
                    fun (req: Interop.IncomingMessage) (res: Interop.ServerResponse) ->
                        match Interop.headerOf req "x-api-key", Interop.headerOf req "authorization" with
                        | Some key, _ -> seen.Add ("x-api-key:" + key)
                        | _, Some bearer -> seen.Add ("authorization:" + bearer)
                        | _ -> seen.Add "none"
                        json res 200 """{"content":[{"type":"text","text":"A name"}],"stop_reason":"end_turn"}"""
                let! url, server = serving record
                let! _ = ClaudeConnection.summarizeAt url ("ANTHROPIC_API_KEY", "sk-ant-key") (ask [ "something" ])
                let! _ = ClaudeConnection.summarizeAt url ("CLAUDE_CODE_OAUTH_TOKEN", "sk-ant-oat01-grant") (ask [ "something" ])
                server.close ignore
                Expect.isTrue (Seq.contains "x-api-key:sk-ant-key" seen) "the key goes in the key header"
                Expect.isTrue
                    (Seq.contains "authorization:Bearer sk-ant-oat01-grant" seen)
                    "and the grant goes in the bearer"
            }

        testCaseAsync "a provider that refused says so, rather than answering with nothing" <|
            async {
                // A 4xx here is a credential, a quota or a model that does not exist. The
                // caller's fallback is the same for all three, but the reason is the only
                // thing anybody has to work from when names stop appearing.
                let! url, server =
                    serving (fun _ res -> json res 401 """{"error":{"message":"invalid x-api-key"}}""")
                let! said = ClaudeConnection.summarizeAt url ("ANTHROPIC_API_KEY", "sk-ant-wrong") (ask [ "something" ])
                server.close ignore
                match said with
                | Ok words -> failwithf "expected a refusal, got %s" words
                | Error reason -> Expect.isTrue (reason.Contains "401") (sprintf "it says what happened, got %s" reason)
            }

        testCaseAsync "a model that declined is told apart from one that answered" <|
            async {
                // The one answer that arrives as a success with nothing in it. Without this
                // it would come back as empty words — which is what a model saying nothing
                // at all looks like, and neither the log nor the next reader could tell.
                let! url, server =
                    serving (fun _ res -> json res 200 """{"content":[],"stop_reason":"refusal"}""")
                let! said = ClaudeConnection.summarizeAt url ("ANTHROPIC_API_KEY", "sk-ant-test") (ask [ "something" ])
                server.close ignore
                Expect.isTrue (Result.isError said) "a decline is not an answer"
            }

        testCaseAsync "an ask with nothing to read is never sent" <|
            async {
                // A model handed no lines writes a name for a conversation it never saw, and
                // that name reads exactly like one it did.
                let seen = ResizeArray<string> ()
                let! url, server = serving (answering "Invented" seen)
                let! said = ClaudeConnection.summarizeAt url ("ANTHROPIC_API_KEY", "sk-ant-test") (ask [])
                server.close ignore
                Expect.isTrue (Result.isError said) "nothing to read, nothing to say"
                Expect.isEmpty seen "and the provider was never asked"
            }
    ]
