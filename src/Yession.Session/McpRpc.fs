namespace Yession.Session

open Yession.Domain
open Yession.Codecs

open Yession.Domain.Chat
open Yession.Domain.Sandboxes
open Yession.Domain.Files
open Yession.Domain.Artifacts
open Yession.Domain.Content
open Yession.Domain.Repos
open Yession.Domain.Prs
open System
open Yession.Domain.Agent
open Yession.Domain.Link
open Yession.Domain.Terminals
open Yession.Domain.Tools
open System.Globalization
#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

/// The Session's own MCP client: JSON-RPC 2.0 as MCP profiles it, the handshake, a server's
/// tool list, a call and its result, and the stream a provider may offer in a result's
/// `_meta`. Nobody else speaks this — the Manager declares servers but never calls one, and
/// the App never sees the protocol — so the Session owns it, and the servers on the other
/// end are somebody else's.
[<RequireQualifiedAccess>]
module McpRpc =

    /// A JSON value carried as TEXT: decode renders whatever is there back to a compact
    /// string; encode parses the string and embeds it as a real JSON node, so an MCP
    /// `inputSchema` object stays an object on the wire and never becomes a quoted string.
    /// A value that is not valid JSON degrades to a JSON string rather than throwing.
    let private rawJson : Codec<string> =
        { Encode =
            (fun raw ->
                match Decode.fromString Decode.value raw with
                | Ok value -> value
                | Error _ -> Encode.string raw)
          Decode = Decode.value |> Decode.map (Encode.toString 0) }

    /// MCP's own `Tool`. What the SESSION's client decodes a server's `tools/list` into
    /// (Plan 17) — it lives here rather than in the Manager's control wire because the
    /// Manager never becomes an MCP client and so never sees one.
    let tool : Codec<McpTool> =
        { Encode =
            fun (t: McpTool) ->
                Encode.object
                    ([ "name", Encode.string t.Name
                       "inputSchema", rawJson.Encode t.InputSchema ]
                     @ (t.Title |> Option.map (fun x -> [ "title", Encode.string x ]) |> Option.defaultValue [])
                     @ (t.Description
                        |> Option.map (fun x -> [ "description", Encode.string x ])
                        |> Option.defaultValue []))
          Decode =
            Decode.object (fun get ->
                { McpTool.Name = get.Required.Field "name" Decode.string
                  McpTool.Title = get.Optional.Field "title" Decode.string
                  McpTool.Description = get.Optional.Field "description" Decode.string
                  McpTool.InputSchema = get.Required.Field "inputSchema" rawJson.Decode }) }

    /// MCP's `ListToolsResult` (its `tools` array).
    let toolList : Codec<McpToolList> =
        { Encode = fun (l: McpToolList) -> Encode.object [ "tools", l.Tools |> List.map tool.Encode |> Encode.list ]
          Decode =
            Decode.object (fun get -> { McpToolList.Tools = get.Required.Field "tools" (Decode.list tool.Decode) }) }

    let request : Codec<JsonRpcRequest> =
        { Encode =
            fun (r: JsonRpcRequest) ->
                Encode.object
                    [ yield "jsonrpc", Encode.string "2.0"
                      yield "id", Encode.int r.Id
                      yield "method", Encode.string r.Method
                      match r.Params with
                      | Some p -> yield "params", rawJson.Encode p
                      | None -> () ]
          Decode =
            Decode.object (fun get ->
                { JsonRpcRequest.Id = get.Required.Field "id" Decode.int
                  JsonRpcRequest.Method = get.Required.Field "method" Decode.string
                  JsonRpcRequest.Params = get.Optional.Field "params" rawJson.Decode }) }

    /// A NOTIFICATION: a method call with no id, which is what tells the server not to
    /// answer. Encode-only, because we send them and never receive one.
    let notification (method: string) : string =
        Encode.object [ "jsonrpc", Encode.string "2.0"; "method", Encode.string method ]
        |> Encode.toString 0

    /// Success and failure are the same frame with different fields, and which arrived is
    /// the interesting part — so it decodes to a DU rather than to a record with two
    /// optionals for a caller to re-derive it from.
    let response : Codec<JsonRpcResponse> =
        { Encode =
            fun (r: JsonRpcResponse) ->
                match r with
                | JsonRpcResult (id, result) ->
                    Encode.object
                        [ "jsonrpc", Encode.string "2.0"
                          "id", Encode.int id
                          "result", rawJson.Encode result ]
                | JsonRpcFailure (id, code, message) ->
                    Encode.object
                        [ "jsonrpc", Encode.string "2.0"
                          "id", (match id with Some i -> Encode.int i | None -> Encode.nil)
                          "error", Encode.object [ "code", Encode.int code; "message", Encode.string message ] ]
          Decode =
            Decode.object (fun get ->
                match get.Optional.Field "error" (Decode.option Decode.value) |> Option.flatten with
                | Some _ ->
                    JsonRpcFailure (
                        get.Optional.Field "id" (Decode.option Decode.int) |> Option.flatten,
                        get.Optional.At [ "error"; "code" ] Decode.int |> Option.defaultValue 0,
                        get.Optional.At [ "error"; "message" ] Decode.string
                        |> Option.defaultValue "the server reported an error with no message")
                | None ->
                    JsonRpcResult (
                        get.Required.Field "id" Decode.int,
                        get.Optional.Field "result" rawJson.Decode |> Option.defaultValue "{}")) }

    /// The response to request `id`, out of the frames a reply carried, or `None` when none
    /// of them is.
    ///
    /// A POST answered with an SSE stream may put notifications (a long call's
    /// `notifications/progress`) and even requests of the server's own ahead of the response,
    /// so "the first frame" is not "the answer". A frame is the answer when it carries a
    /// `result` or an `error` and its `id` is ours. The `response` codec cannot say that on
    /// its own: it would read a server's request, which has an `id` and neither, as a result
    /// with an empty body. Anything unreadable is skipped rather than reported, since it is
    /// not ours to answer for; the caller names the request that went unanswered.
    ///
    /// One exception to "its `id` is ours": an error with a null `id`, which is how JSON-RPC
    /// answers a request it could not read far enough to identify. A POST carries exactly one
    /// request, so that error is about ours — taken only when nothing answered by id, so the
    /// server's reason reaches the caller instead of "no response".
    let replyTo (id: int) (frames: string list) : JsonRpcResponse option =
        let isResponse : Decoder<bool> =
            Decode.object (fun get ->
                get.Optional.Field "result" Decode.value |> Option.isSome
                || get.Optional.Field "error" Decode.value |> Option.isSome)
        let responses =
            frames
            |> List.choose (fun frame ->
                match Decode.fromString isResponse frame with
                | Ok true -> Decode.fromString response.Decode frame |> Result.toOption
                | _ -> None)
        let answers =
            responses
            |> List.tryFind (function
                | JsonRpcResult (answered, _) -> answered = id
                | JsonRpcFailure (answered, _, _) -> answered = Some id)
        match answers with
        | Some found -> Some found
        | None ->
            responses
            |> List.tryFind (function
                | JsonRpcFailure (None, _, _) -> true
                | JsonRpcResult _ -> false
                | JsonRpcFailure (Some _, _, _) -> false)

    /// `initialize`'s params. We declare NO client capabilities: a client that declared
    /// `sampling` would be offering the provider a way to drive the model, which is the
    /// opposite of what a proxied server is for. `roots` and `elicitation` are absent for
    /// the same reason — nothing is offered that was not asked for.
    let initializeParams (clientVersion: string) : string =
        Encode.object
            [ "protocolVersion", Encode.string McpProtocol.Version
              "capabilities", Encode.object []
              "clientInfo",
              Encode.object [ "name", Encode.string "yession"; "version", Encode.string clientVersion ] ]
        |> Encode.toString 0

    /// `tools/call`'s params. The arguments arrive as the JSON text the model produced and
    /// are embedded as an object; text that will not parse becomes an empty object rather
    /// than a quoted string, because a server reading `arguments` as a string would fail in
    /// a way that reads like OUR bug.
    let callParams (name: string) (arguments: string) : string =
        Encode.object
            [ "name", Encode.string name
              "arguments",
              (match Decode.fromString Decode.value arguments with
               | Ok value -> value
               | Error _ -> Encode.object []) ]
        |> Encode.toString 0

    /// `tools/call`'s params, as a SERVER reads them. The mirror of `mcpCallParams`, and
    /// the arguments come back as the raw JSON text a tool body decodes for itself — the
    /// same discipline `ToolCall` follows, for the same reason.
    let callRequest : Codec<string * string> =
        { Encode = fun (name, arguments) -> rawJson.Encode (callParams name arguments)
          Decode =
            Decode.object (fun get ->
                get.Required.Field "name" Decode.string,
                get.Optional.Field "arguments" rawJson.Decode |> Option.defaultValue "{}") }

    /// `initialize`'s result, reduced to what we use. `capabilities` and `instructions` are
    /// not decoded — see `McpHandshake` for why the second one is dropped on purpose.
    let handshake : Codec<McpHandshake> =
        { Encode =
            fun (h: McpHandshake) ->
                Encode.object
                    [ "protocolVersion", Encode.string h.ProtocolVersion
                      "serverInfo",
                      Encode.object
                          [ "name", Encode.option Encode.string h.ServerName
                            "version", Encode.option Encode.string h.ServerVersion ] ]
          Decode =
            Decode.object (fun get ->
                { McpHandshake.ProtocolVersion = get.Required.Field "protocolVersion" Decode.string
                  McpHandshake.ServerName = get.Optional.At [ "serverInfo"; "name" ] Decode.string
                  McpHandshake.ServerVersion = get.Optional.At [ "serverInfo"; "version" ] Decode.string }) }

    /// `tools/call`'s result. Content blocks are flattened to the text the model reads; a
    /// block of some other kind is NAMED rather than dropped, so a provider answering with
    /// an image produces "[image]" instead of a blank the model reads as success.
    let callResult : Codec<McpCallResult> =
        let block : Decoder<string> =
            Decode.field "type" Decode.string
            |> Decode.andThen (fun kind ->
                match kind with
                | "text" -> Decode.field "text" Decode.string
                | other -> Decode.succeed (sprintf "[%s]" other))
        { Encode =
            fun (r: McpCallResult) ->
                Encode.object
                    ([ "content",
                       Encode.list [ Encode.object [ "type", Encode.string "text"; "text", Encode.string r.Text ] ]
                       "isError", Encode.bool r.IsError ]
                     @ (r.Meta |> Option.map (fun m -> [ "_meta", rawJson.Encode m ]) |> Option.defaultValue []))
          Decode =
            Decode.object (fun get ->
                { McpCallResult.Text =
                    get.Optional.Field "content" (Decode.list block)
                    |> Option.defaultValue []
                    |> String.concat "\n"
                  McpCallResult.IsError =
                    get.Optional.Field "isError" Decode.bool |> Option.defaultValue false
                  McpCallResult.Meta = get.Optional.Field "_meta" rawJson.Decode }) }

    /// What the model reads of a `tools/call` result. A tool that RAN and went badly says so
    /// through `isError`, and the call stays an answer rather than a failure — so the flag
    /// has to reach the model as words, because its text alone reads exactly like a success.
    let toolText (result: McpCallResult) : string =
        if result.IsError then "The tool reported an error:\n" + result.Text else result.Text

    /// The stream a provider offered, out of a result's `_meta` (Plan 19).
    ///
    /// TOTAL, and deliberately: `_meta` is a place anyone may put anything, so a key that is
    /// missing, a value of the wrong shape, or an offer with no url all read as "no offer"
    /// rather than as a failed tool call. The only thing a provider must get right to be
    /// heard is the url.
    ///
    /// Every other field defaults to the conservative reading — `byteStream` capabilities,
    /// no label, not renewable — so the smallest conforming provider adds one string.
    /// `docs/streams.md` is what such a provider reads.
    let streamOffer (meta: string) : StreamOffer option =
        let capabilities : Decoder<SourceCapabilities> =
            Decode.object (fun get ->
                { CanInstrument = get.Optional.Field "instrument" Decode.bool |> Option.defaultValue false
                  CanResize = get.Optional.Field "resize" Decode.bool |> Option.defaultValue false
                  HasExitCode = get.Optional.Field "exitCode" Decode.bool |> Option.defaultValue false })
        let offer : Decoder<StreamOffer> =
            Decode.object (fun get ->
                { Ticket =
                    { Url = get.Required.Field "url" Decode.string
                      Capabilities =
                        get.Optional.Field "capabilities" capabilities
                        |> Option.defaultValue SourceCapabilities.byteStream
                      Label = get.Optional.Field "label" Decode.string |> Option.filter (fun s -> s.Trim () <> "") }
                  Renewable = get.Optional.Field "renewable" Decode.bool |> Option.defaultValue false })
        let container : Decoder<StreamOffer option> =
            Decode.object (fun get -> get.Optional.Field StreamOffer.metaKey offer)
        match Decode.fromString container meta with
        | Ok found -> found
        | Error _ -> None
