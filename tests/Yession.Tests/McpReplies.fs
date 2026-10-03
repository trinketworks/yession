module Yession.Tests.McpReplies

// What a session makes of a server's reply to a `tools/call`.
//
// Two things, both about not mistaking one thing for another. A POST answered with an SSE
// stream may carry notifications and server requests ahead of the response, so the response
// is the frame that answers THIS request rather than the first one. And a result flagged
// `isError` is still an answer, which means its flag has to reach the model as words.
//
// Pure — no socket. The POSTing is the client's; what it reads out of what came back is not.

open Fable.Pyxpecto
open Yession.Host
open Yession.Domain
open Yession.Domain.Tools
open Yession.Session

let private progress =
    """{"jsonrpc":"2.0","method":"notifications/progress","params":{"progressToken":1,"progress":1}}"""

let private resultFor (id: int) = sprintf """{"jsonrpc":"2.0","id":%d,"result":{"ok":true}}""" id

let private stream (frames: string list) =
    frames |> List.map (fun frame -> "event: message\ndata: " + frame + "\n\n") |> String.concat ""

let private flagged (isError: bool) : McpCallResult =
    { McpCallResult.Text = "no such device"; McpCallResult.IsError = isError; McpCallResult.Meta = None }

let tests =
    testList "MCP replies" [
        test "a progress notification ahead of the response does not stand in for it" {
            match McpClient.replyTo 7 "tools/call" (stream [ progress; resultFor 7 ]) with
            | Ok (JsonRpcResult (id, _)) -> Expect.equal id 7 "the frame that answers this request"
            | other -> failwithf "expected the response behind the notification, got %A" other
        }

        test "a server's own request carrying our id does not stand in for the response" {
            let ask = """{"jsonrpc":"2.0","id":7,"method":"sampling/createMessage","params":{}}"""
            match McpClient.replyTo 7 "tools/call" (stream [ ask; resultFor 7 ]) with
            | Ok (JsonRpcResult (_, result)) -> Expect.equal result """{"ok":true}""" "the result, not an empty one"
            | other -> failwithf "expected the response behind the request, got %A" other
        }

        test "a stream with no response for this id is an error naming the method" {
            match McpClient.replyTo 7 "tools/call" (stream [ progress; resultFor 6 ]) with
            | Error e -> Expect.stringContains e "tools/call" "the request that went unanswered"
            | Ok found -> failwithf "read a frame as the answer that was not: %A" found
        }

        test "a plain JSON body is read as the response" {
            match McpClient.replyTo 3 "tools/list" (resultFor 3) with
            | Ok (JsonRpcResult (id, _)) -> Expect.equal id 3 "the one frame there is"
            | other -> failwithf "expected the body to be the response, got %A" other
        }

        test "an error frame for this id is the response, not an absence of one" {
            let failed = """{"jsonrpc":"2.0","id":4,"error":{"code":-32602,"message":"bad params"}}"""
            match McpClient.replyTo 4 "tools/call" (stream [ progress; failed ]) with
            | Ok (JsonRpcFailure (Some 4, code, _)) -> Expect.equal code -32602 "what the server said"
            | other -> failwithf "expected the failure frame, got %A" other
        }

        test "an error the server could not attach to an id is the response when nothing else is" {
            let unreadable = """{"jsonrpc":"2.0","id":null,"error":{"code":-32700,"message":"parse error"}}"""
            match McpClient.replyTo 4 "tools/call" unreadable with
            | Ok (JsonRpcFailure (None, code, _)) -> Expect.equal code -32700 "the server's own reason"
            | other -> failwithf "expected the id-less failure, got %A" other
        }

        test "an answer the tool flagged as an error tells the model so" {
            let text = McpRpc.toolText (flagged true)
            Expect.stringContains text "reported an error" "the failure is stated"
            Expect.stringContains text "no such device" "and so is what the tool said"
        }

        test "an answer not flagged reads as the tool's text unchanged" {
            Expect.equal (McpRpc.toolText (flagged false)) "no such device" "nothing added to a success"
        }
    ]
