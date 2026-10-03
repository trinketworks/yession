namespace Yession.Domain

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

/// Decoders for what a wire ADMITS where Thoth's own are more forgiving than a contract
/// here is. Beside the codecs because they guard the same boundary: the place a value stops
/// being JSON and starts being a type.
[<RequireQualifiedAccess>]
module Strict =

    /// An int as a JSON NUMBER, refusing the text of one. Thoth's `int` accepts `"1234"`, and
    /// the two macros this replaced — `typeof $0?.port === 'number'` on a spawn's readiness
    /// line, `Number.isFinite($0)` on a stream's exit code — did not: a peer that states a
    /// number as text is not speaking the contract, and reading it anyway would hide that
    /// from the one process in a position to notice. A fraction is refused by Thoth's `int`
    /// itself, where `x | 0` used to truncate one to a code no process ever returned.
    let int : Decoder<int> =
        Decode.value
        |> Decode.andThen (fun raw ->
            match Decode.fromValue "$" Decode.string raw with
            | Ok _ -> Decode.fail "a number, not the text of one"
            | Error _ -> Decode.int)

/// A paired encoder/decoder for a single domain type. Serialization is an explicit
/// boundary concern: codecs are written by hand so private constructors are honoured and
/// the wire format never leaks into application logic. See docs/design.md §1 (Types
/// first) and §6.
type Codec<'a> =
    { Encode : 'a -> JsonValue
      Decode : Decoder<'a> }

module Codec =

    /// Lift a smart constructor into a decoder, failing the decode on rejected input.
    let viaSmartCtor (create: 'raw -> Result<'a, string>) (raw: Decoder<'raw>) : Decoder<'a> =
        raw
        |> Decode.andThen (fun value ->
            match create value with
            | Ok v -> Decode.succeed v
            | Error e -> Decode.fail e)

    let sessionId : Codec<SessionId> =
        { Encode = SessionId.value >> Encode.string
          Decode = viaSmartCtor SessionId.create Decode.string }

    let peerId : Codec<PeerId> =
        { Encode = PeerId.value >> Encode.string
          Decode = viaSmartCtor PeerId.create Decode.string }

    let queueId : Codec<QueueId> =
        { Encode = QueueId.value >> Encode.string
          Decode = viaSmartCtor QueueId.create Decode.string }

    let sandboxName : Codec<SandboxName> =
        { Encode = SandboxName.value >> Encode.string
          Decode = viaSmartCtor SandboxName.create Decode.string }

    /// A sandbox as the log spells it. `render`/`parse` are each other's inverse and a
    /// session-owned ref renders to the bare name, so this reads every log ever written
    /// without a compatibility branch.
    let sandboxRef : Codec<SandboxRef> =
        { Encode = fun (r: SandboxRef) -> Encode.string (SandboxRef.render r)
          Decode =
            Decode.string
            |> Decode.andThen (fun raw ->
                match SandboxRef.parse raw with
                | Ok r -> Decode.succeed r
                | Error e -> Decode.fail e) }

    let messageId : Codec<MessageId> =
        { Encode = MessageId.value >> Encode.string
          Decode = viaSmartCtor MessageId.create Decode.string }

    let agentTurnId : Codec<AgentTurnId> =
        { Encode = AgentTurnId.value >> Encode.string
          Decode = viaSmartCtor AgentTurnId.create Decode.string }

    let eventId : Codec<EventId> =
        { Encode = EventId.value >> Encode.guid
          Decode = viaSmartCtor EventId.create Decode.guid }

    let requestId : Codec<RequestId> =
        { Encode = RequestId.value >> Encode.guid
          Decode = viaSmartCtor RequestId.create Decode.guid }

    let eventOffset : Codec<EventOffset> =
        { Encode = EventOffset.value >> Encode.int64
          Decode = viaSmartCtor EventOffset.create Decode.int64 }

    /// Timestamps are encoded as round-trippable ISO-8601 strings (offset preserved).
    let timestamp : Codec<DateTimeOffset> =
        { Encode = fun t -> Encode.string (t.ToString("o", CultureInfo.InvariantCulture))
          Decode =
            Decode.string
            |> Decode.andThen (fun s ->
                match DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) with
                | true, v -> Decode.succeed v
                | false, _ -> Decode.fail (sprintf "Invalid ISO-8601 timestamp: %s" s)) }

    let userId : Codec<UserId> =
        { Encode = UserId.value >> Encode.string
          Decode = viaSmartCtor UserId.create Decode.string }

    let repoRef : Codec<RepoRef> =
        { Encode = RepoRef.value >> Encode.string
          Decode = viaSmartCtor RepoRef.create Decode.string }

    let actor : Codec<ActorRef> =
        { Encode =
            (fun a ->
                match a with
                | UserRef u -> Encode.object [ "kind", Encode.string "user"; "sub", userId.Encode u ]
                | PeerRef p -> Encode.object [ "kind", Encode.string "peer"; "peerId", peerId.Encode p ]
                | Agent -> Encode.object [ "kind", Encode.string "agent" ]
                | SessionProcess -> Encode.object [ "kind", Encode.string "sessionProcess" ]
                | System -> Encode.object [ "kind", Encode.string "system" ]
                | Configured repo ->
                    Encode.object [ "kind", Encode.string "configured"; "repo", repoRef.Encode repo ])
          Decode =
            Decode.field "kind" Decode.string
            |> Decode.andThen (fun kind ->
                match kind with
                | "user" -> Decode.field "sub" userId.Decode |> Decode.map UserRef
                | "peer" -> Decode.field "peerId" peerId.Decode |> Decode.map PeerRef
                | "agent" -> Decode.succeed Agent
                | "sessionProcess" -> Decode.succeed SessionProcess
                | "system" -> Decode.succeed System
                | "configured" -> Decode.field "repo" repoRef.Decode |> Decode.map Configured
                | other -> Decode.fail (sprintf "Unknown actor kind: %s" other)) }

    /// A principal on the wire is the actor it is — the same tagged object, so a field that
    /// narrowed from `ActorRef` to `Principal` reads every event already written by a
    /// person. What it refuses is the other kinds: a stored `agent` where a principal is
    /// required is a fact this version cannot represent, and a decode that answered
    /// something else for it would be the fault the narrowing closed, coming back in.
    let principal : Codec<Principal> =
        { Encode = Principal.toActor >> actor.Encode
          Decode =
            actor.Decode
            |> Decode.andThen (fun a ->
                match Principal.ofActor a with
                | Some p -> Decode.succeed p
                | None -> Decode.fail (sprintf "Not a principal: %s" (ActorRef.token a))) }

    /// Why something happened, as a tagged object. A kind this version does not know fails,
    /// so a reader is never shown a cause it made up.
    let cause : Codec<Cause> =
        { Encode =
            fun (c: Cause) ->
                match c with
                | Cause.Item id -> Encode.object [ "kind", Encode.string "item"; "messageId", messageId.Encode id ]
                | Cause.Booted -> Encode.object [ "kind", Encode.string "booted" ]
                | Cause.Connected p -> Encode.object [ "kind", Encode.string "connected"; "principal", principal.Encode p ]
          Decode =
            Decode.field "kind" Decode.string
            |> Decode.andThen (fun kind ->
                match kind with
                | "item" -> Decode.field "messageId" messageId.Decode |> Decode.map Cause.Item
                | "booted" -> Decode.succeed Cause.Booted
                | "connected" -> Decode.field "principal" principal.Decode |> Decode.map Cause.Connected
                | other -> Decode.fail (sprintf "Unknown cause kind: %s" other)) }

    /// Whose credential: a person is the principal's tagged object, the deployment its own
    /// kind. Not an actor kind — the deployment is not a party that acts in the log, it is
    /// whose credentials an act ran on when nobody's were named — so the actor decoder is
    /// asked second, for the shape it knows.
    let credentialFor : Codec<CredentialFor> =
        { Encode =
            fun credential ->
                match credential with
                | CredentialFor.Person p -> principal.Encode p
                | CredentialFor.Deployment -> Encode.object [ "kind", Encode.string "deployment" ]
          Decode =
            Decode.field "kind" Decode.string
            |> Decode.andThen (fun kind ->
                match kind with
                | "deployment" -> Decode.succeed CredentialFor.Deployment
                | _ -> principal.Decode |> Decode.map CredentialFor.Person) }

    let terminalId : Codec<TerminalId> =
        { Encode = TerminalId.value >> Encode.string
          Decode = viaSmartCtor TerminalId.create Decode.string }

    let terminalTitle : Codec<TerminalTitle> =
        { Encode = TerminalTitle.value >> Encode.string
          Decode = viaSmartCtor TerminalTitle.create Decode.string }

    /// The three parties behind an act, on the wire (Plan 20). Not a nested object: these
    /// keys sit at the payload's top level and always have, and an event log is read back for
    /// the life of its session — so what changed is where the value lives in F#, and nothing
    /// about what is written. One pair of helpers rather than a spelling per event, which is
    /// how the three came to disagree in the first place.
    let authorityFields (authority: Authority) =
        [ "author", actor.Encode (Authority.author authority)
          "onBehalfOf", Encode.option principal.Encode (Authority.onBehalfOf authority) ]

    /// Recovered, never authored — `recover`'s reason. `onBehalfOf` is optional on the way in
    /// because a person's act has none, and events written before Plan 20 have no such key at
    /// all; what `recover` then refuses — an agent act with nobody named — fails the event,
    /// which is a page that will not open. Deliberate: that act was never one this version
    /// can run, and a decoder that stood something in for the missing owner would put back the
    /// degraded state the sum took out. Keys this stopped asking for (`approvedBy`, Plan 23)
    /// are simply ignored where old events still carry them.
    let authorityOf : Decoder<Authority> =
        Decode.object (fun get ->
            get.Required.Field "author" actor.Decode,
            get.Optional.Field "onBehalfOf" (Decode.option principal.Decode) |> Option.flatten)
        |> Decode.andThen (fun (author, onBehalfOf) ->
            match Authority.recover author onBehalfOf with
            | Ok authority -> Decode.succeed authority
            | Error reason -> Decode.fail reason)

    let prRef : Codec<PrRef> =
        { Encode =
            fun (pr: PrRef) ->
                Encode.object [ "repo", repoRef.Encode pr.Repo; "number", Encode.int pr.Number ]
          Decode =
            Decode.object (fun get ->
                (get.Required.Field "repo" repoRef.Decode, get.Required.Field "number" Decode.int))
            |> Decode.andThen (fun (repo, number) ->
                match PrRef.create repo number with
                | Ok pr -> Decode.succeed pr
                | Error e -> Decode.fail e) }

    let commandId : Codec<CommandId> =
        { Encode = CommandId.value >> Encode.string
          Decode = viaSmartCtor CommandId.create Decode.string }

    let blockId : Codec<BlockId> =
        { Encode = BlockId.value >> Encode.string
          Decode = viaSmartCtor BlockId.create Decode.string }

    let toolUseId : Codec<ToolUseId> =
        { Encode = ToolUseId.value >> Encode.string
          Decode = viaSmartCtor ToolUseId.create Decode.string }

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
    let mcpTool : Codec<McpTool> =
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
    let mcpToolList : Codec<McpToolList> =
        { Encode = fun (l: McpToolList) -> Encode.object [ "tools", l.Tools |> List.map mcpTool.Encode |> Encode.list ]
          Decode =
            Decode.object (fun get -> { McpToolList.Tools = get.Required.Field "tools" (Decode.list mcpTool.Decode) }) }

    // Declaring an MCP server (Plan 17). One set of codecs for two consumers — the
    // Manager's state file and the `/control/mcp` frame — because they carry the same
    // value, and two codecs for one type is two chances to disagree about it.

    let mcpServerName : Codec<McpServerName> =
        { Encode = McpServerName.value >> Encode.string
          Decode = viaSmartCtor McpServerName.create Decode.string }

    /// Tagged even with one case: a second transport changes who owns the PROCESS, and a
    /// bare url on the wire would have to be re-tagged to admit one.
    let mcpTransport : Codec<McpTransport> =
        { Encode =
            fun (transport: McpTransport) ->
                match transport with
                | McpHttp url -> Encode.object [ "type", Encode.string "http"; "url", Encode.string url ]
          Decode =
            Decode.field "type" Decode.string
            |> Decode.andThen (fun t ->
                match t with
                | "http" -> Decode.field "url" Decode.string |> Decode.map McpHttp
                | other -> Decode.fail (sprintf "Unknown MCP transport: %s" other)) }

    let mcpServerRef : Codec<McpServerRef> =
        { Encode =
            fun (server: McpServerRef) ->
                Encode.object
                    [ "name", mcpServerName.Encode server.Name
                      "transport", mcpTransport.Encode server.Transport
                      "description", Encode.option Encode.string server.Description ]
          Decode =
            Decode.object (fun get ->
                { McpServerRef.Name = get.Required.Field "name" mcpServerName.Decode
                  McpServerRef.Transport = get.Required.Field "transport" mcpTransport.Decode
                  McpServerRef.Description = get.Optional.Field "description" Decode.string }) }

    let mcpAudience : Codec<McpAudience> =
        { Encode =
            fun (audience: McpAudience) ->
                match audience with
                | AnySession -> Encode.object [ "type", Encode.string "any" ]
                | OneSession id ->
                    Encode.object [ "type", Encode.string "session"; "sessionId", sessionId.Encode id ]
          Decode =
            Decode.field "type" Decode.string
            |> Decode.andThen (fun t ->
                match t with
                | "any" -> Decode.succeed AnySession
                | "session" -> Decode.field "sessionId" sessionId.Decode |> Decode.map OneSession
                | other -> Decode.fail (sprintf "Unknown MCP audience: %s" other)) }

    let mcpDeclaration : Codec<McpDeclaration> =
        { Encode =
            fun (declaration: McpDeclaration) ->
                Encode.object
                    [ "server", mcpServerRef.Encode declaration.Server
                      "audience", mcpAudience.Encode declaration.Audience ]
          Decode =
            Decode.object (fun get ->
                { McpDeclaration.Server = get.Required.Field "server" mcpServerRef.Decode
                  McpDeclaration.Audience = get.Required.Field "audience" mcpAudience.Decode }) }

    // Talking to a server (Plan 17, step 3). JSON-RPC 2.0 as MCP profiles it, and the two
    // results we read. Codecs rather than string surgery in the Host, so the protocol is
    // testable with no socket — which is the same split `Sse.fs` and the control wire use.

    let jsonRpcRequest : Codec<JsonRpcRequest> =
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
    let jsonRpcNotification (method: string) : string =
        Encode.object [ "jsonrpc", Encode.string "2.0"; "method", Encode.string method ]
        |> Encode.toString 0

    /// Success and failure are the same frame with different fields, and which arrived is
    /// the interesting part — so it decodes to a DU rather than to a record with two
    /// optionals for a caller to re-derive it from.
    let jsonRpcResponse : Codec<JsonRpcResponse> =
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

    /// `initialize`'s params. We declare NO client capabilities: a client that declared
    /// `sampling` would be offering the provider a way to drive the model, which is the
    /// opposite of what a proxied server is for. `roots` and `elicitation` are absent for
    /// the same reason — nothing is offered that was not asked for.
    let mcpInitializeParams (clientVersion: string) : string =
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
    let mcpCallParams (name: string) (arguments: string) : string =
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
    let mcpCallRequest : Codec<string * string> =
        { Encode = fun (name, arguments) -> rawJson.Encode (mcpCallParams name arguments)
          Decode =
            Decode.object (fun get ->
                get.Required.Field "name" Decode.string,
                get.Optional.Field "arguments" rawJson.Decode |> Option.defaultValue "{}") }

    /// `initialize`'s result, reduced to what we use. `capabilities` and `instructions` are
    /// not decoded — see `McpHandshake` for why the second one is dropped on purpose.
    let mcpHandshake : Codec<McpHandshake> =
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
    let mcpCallResult : Codec<McpCallResult> =
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

    /// One `/control/mcp` frame: the whole resolved set for THIS session, every time. The
    /// AUDIENCE is deliberately absent — resolution already happened, and a session that
    /// could read who else reaches a server would be reading the Manager's configuration.
    let mcpServerSet : Codec<McpServerSet> =
        { Encode =
            fun (set: McpServerSet) ->
                Encode.object [ "servers", set.Servers |> List.map mcpServerRef.Encode |> Encode.list ]
          Decode =
            Decode.object (fun get ->
                { McpServerSet.Servers = get.Required.Field "servers" (Decode.list mcpServerRef.Decode) }) }

    /// The transcript bound a lease event carries (Plan 14, stage 1).
    ///
    /// OPTIONAL on the way in, alone among the terminal payloads' value fields, because a log
    /// written before this existed is a log a running session still has to replay — and the
    /// store fails loudly on anything it cannot decode. Absent reads as 0, so a stretch from
    /// before the range was recorded has `ToSeq <= FromSeq`: an EMPTY range, which every reader
    /// already treats as "nothing to replay" (it is what a rejected block carries). A default
    /// that guessed a real range instead would replay the wrong bytes and look right.
    let leaseSeq (get: Decode.IGetters) (field: string) : int =
        get.Optional.Field field Decode.int |> Option.defaultValue 0

    /// An artifact version on the wire as its PATH (`artifacts/chart.png/0003-7f2a91`), not as
    /// three fields. The path is the canonical form everywhere else — the route serves it, a
    /// message links to it, a chip carries it — and `ArtifactRef.ofContent` is its inverse, so
    /// one spelling is on the log and a name, a number and a stamp cannot arrive disagreeing.
    let artifactRef : Codec<ArtifactRef> =
        { Encode = fun (r: ArtifactRef) -> Encode.string (ContentRef.value (ArtifactRef.content r))
          Decode =
            Decode.string
            |> Decode.andThen (fun raw ->
                match ContentRef.create raw |> Result.bind ArtifactRef.ofContent with
                | Ok r -> Decode.succeed r
                | Error e -> Decode.fail e) }

    /// What the pane can show, on the wire. Up here rather than beside the presence frame it
    /// was written for, because the events that record a tab being opened carry one too and
    /// the event codec is declared first — one spelling, so a tab read off the log and a peer
    /// read off a presence frame can never be two different things.
    let viewRef : Codec<ViewRef> =
        { Encode =
            (fun v ->
                match v with
                | ViewingFile ref -> Encode.object [ "kind", Encode.string "file"; "path", Encode.string (ContentRef.value ref) ]
                | ViewingTerminal t -> Encode.object [ "kind", Encode.string "terminal"; "terminalId", terminalId.Encode t ])
          Decode =
            Decode.field "kind" Decode.string
            |> Decode.andThen (function
                | "file" ->
                    // The path is re-CHECKED on the way in rather than trusted: this arrives
                    // from a peer's presence frame or off the log, and a `ContentRef` is what
                    // the pane turns into a URL.
                    Decode.field "path" Decode.string
                    |> Decode.andThen (fun path ->
                        match ContentRef.create path with
                        | Ok ref -> Decode.succeed (ViewingFile ref)
                        | Error reason -> Decode.fail reason)
                | "terminal" -> Decode.field "terminalId" terminalId.Decode |> Decode.map ViewingTerminal
                | other -> Decode.fail (sprintf "Unknown view ref: %s" other)) }

    /// A plain string codec, handy as the `'State` codec when exercising frames whose
    /// state payload is opaque to the transport.
    let string : Codec<string> =
        { Encode = Encode.string; Decode = Decode.string }

    /// One asciicast line. Deliberately NOT this file's usual tagged-object shape: the
    /// format is asciinema's, and matching it exactly is the point — a transcript is only
    /// worth calling an audit artifact if something other than Yession can read it. So the
    /// header is a bare object with `version: 2` and a record is a bare three-element
    /// array, `[time, code, data]`.
    let transcriptLine : Codec<TranscriptLine> =
        let encodeHeader (h: TranscriptHeader) =
            Encode.object
                [ "version", Encode.int 2
                  "width", Encode.int h.Width
                  "height", Encode.int h.Height
                  "timestamp", Encode.int64 h.Timestamp ]
        let encodeRecord (r: TranscriptRecord) =
            [ Encode.float r.At; Encode.string (TranscriptKind.code r.Kind); Encode.string r.Data ]
            |> Encode.list
        let decodeHeader : Decoder<TranscriptLine> =
            Decode.object (fun get ->
                { Width = get.Required.Field "width" Decode.int
                  Height = get.Required.Field "height" Decode.int
                  Timestamp = get.Required.Field "timestamp" Decode.int64 })
            |> Decode.map TranscriptHeaderLine
        let decodeRecord : Decoder<TranscriptLine> =
            Decode.map3
                (fun at code data -> at, code, data)
                (Decode.index 0 Decode.float)
                (Decode.index 1 Decode.string)
                (Decode.index 2 Decode.string)
            |> Decode.andThen (fun (at, code, data) ->
                match TranscriptKind.parse code with
                | Some kind -> Decode.succeed (TranscriptRecordLine { At = at; Kind = kind; Data = data })
                | None -> Decode.fail (sprintf "Unknown transcript record kind: %s" code))
        { Encode =
            (fun line ->
                match line with
                | TranscriptHeaderLine h -> encodeHeader h
                | TranscriptRecordLine r -> encodeRecord r)
          // The header is tried first because it is the one shape with a discriminator of
          // its own; a record is anything array-shaped.
          Decode = Decode.oneOf [ decodeHeader; decodeRecord ] }

    let transcriptRecord : Codec<TranscriptRecord> =
        { Encode = fun r -> transcriptLine.Encode (TranscriptRecordLine r)
          Decode =
            transcriptLine.Decode
            |> Decode.andThen (function
                | TranscriptRecordLine r -> Decode.succeed r
                | TranscriptHeaderLine _ -> Decode.fail "expected a transcript record, found the header") }

    /// One keyframe line in a terminal's `.keys.jsonl` sidecar (Plan 14, stage 3). This one
    /// IS this file's usual object shape rather than asciinema's, and deliberately so: it is
    /// ours, it is not in the `.cast`, and nothing outside Yession has to read it.
    let transcriptKeyframe : Codec<TranscriptKeyframe> =
        { Encode =
            fun (k: TranscriptKeyframe) ->
                Encode.object
                    [ "seq", Encode.int k.Seq
                      "cols", Encode.int k.Cols
                      "rows", Encode.int k.Rows
                      "screen", Encode.string k.Screen ]
          Decode =
            Decode.object (fun get ->
                { Seq = get.Required.Field "seq" Decode.int
                  Cols = get.Required.Field "cols" Decode.int
                  Rows = get.Required.Field "rows" Decode.int
                  Screen = get.Required.Field "screen" Decode.string }) }

    // --- the query surface (Plan 15) ------------------------------------------------------
    // The wire between the Session Process's query registry and the browser's generated
    // read surface. Both a query's DECLARATION and its VALUE cross it: the declaration
    // because the client renders a query it has never heard of, the value because that is
    // the point. Shape and value are encoded separately rather than as one fused blob, so
    // the section's markup exists before the first value arrives (`/queries` answers, the
    // stream fills in) and so an invalidation frame carries only what changed.

    let queryName : Codec<QueryName> =
        { Encode = QueryName.value >> Encode.string
          Decode = viaSmartCtor QueryName.create Decode.string }

    let private queryColumn : Codec<QueryColumn> =
        { Encode =
            fun (c: QueryColumn) -> Encode.object [ "key", Encode.string c.Key; "label", Encode.string c.Label ]
          Decode =
            Decode.object (fun get ->
                { Key = get.Required.Field "key" Decode.string
                  Label = get.Required.Field "label" Decode.string }) }

    let private queryShape : Codec<QueryShape> =
        { Encode =
            (fun shape ->
                match shape with
                | Value -> Encode.object [ "kind", Encode.string "value" ]
                | Fields columns ->
                    Encode.object [ "kind", Encode.string "fields"; "columns", Encode.list (columns |> List.map queryColumn.Encode) ]
                | Rows columns ->
                    Encode.object [ "kind", Encode.string "rows"; "columns", Encode.list (columns |> List.map queryColumn.Encode) ])
          Decode =
            Decode.field "kind" Decode.string
            |> Decode.andThen (function
                | "value" -> Decode.succeed Value
                | "fields" -> Decode.field "columns" (Decode.list queryColumn.Decode) |> Decode.map Fields
                | "rows" -> Decode.field "columns" (Decode.list queryColumn.Decode) |> Decode.map Rows
                | other -> Decode.fail (sprintf "Unknown query shape: %s" other)) }

    /// A tone as one word. Spelled out rather than numbered so the stream stays readable
    /// to anything consuming it without this codec, which is the same reason a cell rides
    /// as its native JSON type below.
    let private queryTone : Codec<QueryTone> =
        { Encode = QueryTone.name >> Encode.string
          Decode =
            Decode.string
            |> Decode.andThen (fun raw ->
                match QueryTone.parse raw with
                | Some tone -> Decode.succeed tone
                | None -> Decode.fail (sprintf "Unknown query tone: %s" raw)) }

    /// A cell rides as its NATIVE JSON type — a string, a bool, or null — rather than as a
    /// tagged object, which keeps the payload readable to anything that consumes the stream
    /// without this codec.
    ///
    /// A toned cell is the one that cannot: it carries two facts, so it takes an object.
    /// It is tried LAST, after the three native forms, because `oneOf` takes the first
    /// decoder that succeeds and a bare string must stay a `CellText` — the object form is
    /// the only shape none of the others can claim.
    let private queryCell : Codec<QueryCell> =
        { Encode =
            (fun cell ->
                match cell with
                | CellText text -> Encode.string text
                | CellFlag flag -> Encode.bool flag
                | CellStatus (text, tone) ->
                    Encode.object [ "text", Encode.string text; "tone", queryTone.Encode tone ]
                | CellAbsent -> Encode.nil)
          Decode =
            Decode.oneOf
                [ Decode.string |> Decode.map CellText
                  Decode.bool |> Decode.map CellFlag
                  Decode.nil CellAbsent
                  Decode.map2
                      (fun text tone -> CellStatus (text, tone))
                      (Decode.field "text" Decode.string)
                      (Decode.field "tone" queryTone.Decode) ] }

    let private queryRow : Codec<(string * QueryCell) list> =
        { Encode = fun row -> Encode.object (row |> List.map (fun (key, cell) -> key, queryCell.Encode cell))
          Decode = Decode.keyValuePairs queryCell.Decode }

    let queryValue : Codec<QueryValue> =
        { Encode =
            (fun value ->
                match value with
                | ValueOf cell -> Encode.object [ "kind", Encode.string "value"; "value", queryCell.Encode cell ]
                | FieldsOf fields -> Encode.object [ "kind", Encode.string "fields"; "fields", queryRow.Encode fields ]
                | RowsOf rows ->
                    Encode.object [ "kind", Encode.string "rows"; "rows", Encode.list (rows |> List.map queryRow.Encode) ])
          Decode =
            Decode.field "kind" Decode.string
            |> Decode.andThen (function
                | "value" -> Decode.field "value" queryCell.Decode |> Decode.map ValueOf
                | "fields" -> Decode.field "fields" queryRow.Decode |> Decode.map FieldsOf
                | "rows" -> Decode.field "rows" (Decode.list queryRow.Decode) |> Decode.map RowsOf
                | other -> Decode.fail (sprintf "Unknown query value: %s" other)) }

    let queryDef : Codec<QueryDef> =
        { Encode =
            (fun (def: QueryDef) ->
                Encode.object
                    [ "name", queryName.Encode def.Name
                      "title", Encode.string def.Title
                      "description", Encode.string def.Description
                      "shape", queryShape.Encode def.Shape
                      // A pair per entry rather than an object, because the order is the
                      // reading order and an object's keys are not ordered on the wire.
                      "legend",
                      Encode.list (
                          def.Legend
                          |> List.map (fun (shape, meaning) ->
                              Encode.list [ Encode.string shape; Encode.string meaning ])) ])
          Decode =
            Decode.object (fun get ->
                { Name = get.Required.Field "name" queryName.Decode
                  Title = get.Required.Field "title" Decode.string
                  Description = get.Required.Field "description" Decode.string
                  Shape = get.Required.Field "shape" queryShape.Decode
                  // Optional: a page still open from before queries could carry one reads
                  // the declarations again on its next connection, and until then a
                  // missing legend is a query without one rather than a broken frame.
                  Legend =
                    get.Optional.Field "legend" (Decode.list (Decode.list Decode.string))
                    |> Option.defaultValue []
                    |> List.choose (function [ shape; meaning ] -> Some (shape, meaning) | _ -> None) }) }

    /// One frame of the multiplexed query stream. Tagged like the App's `Frames.session` for
    /// the same reason: one connection carries every query there will ever be, and a client
    /// folds each frame by name into a map.
    let queryFrame : Codec<QueryFrame> =
        { Encode =
            (fun frame ->
                match frame with
                | QueriesDeclared defs ->
                    Encode.object [ "tag", Encode.string "queries"; "queries", Encode.list (defs |> List.map queryDef.Encode) ]
                | QueryValued (name, value) ->
                    Encode.object
                        [ "tag", Encode.string "value"
                          "name", queryName.Encode name
                          "value", queryValue.Encode value ])
          Decode =
            Decode.field "tag" Decode.string
            |> Decode.andThen (function
                | "queries" -> Decode.field "queries" (Decode.list queryDef.Decode) |> Decode.map QueriesDeclared
                | "value" ->
                    Decode.map2
                        (fun name value -> QueryValued (name, value))
                        (Decode.field "name" queryName.Decode)
                        (Decode.field "value" queryValue.Decode)
                | other -> Decode.fail (sprintf "Unknown query frame: %s" other)) }

    let modelId : Codec<ModelId> =
        { Encode = ModelId.value >> Encode.string
          Decode = viaSmartCtor ModelId.create Decode.string }

    let agentModel : Codec<AgentModel> =
        { Encode =
            (fun (model: AgentModel) ->
                Encode.object
                    [ "id", modelId.Encode model.Id
                      "name", Encode.string model.Name ])
          Decode =
            // `map2`, not the getter API, and the difference is not style. Thoth's
            // `Decode.object` does NOT short-circuit: a required field that is missing
            // stashes the error, hands the builder `Unchecked.defaultof<_>` — null — and
            // runs it anyway, reporting only afterwards. A builder that merely STORES that
            // value is fine, which is why every record literal here is; this one called
            // `AgentModel.create`, which reads the id back out (`ModelId.value`, for the
            // name's fallback), so a model entry with no `id` dereferenced null and THREW
            // where a decode should have refused.
            //
            // `map2` applies the constructor only once both parts have decoded, which is
            // the guarantee the getter cannot make. A decoder that calls a function on a
            // required field's value wants a combinator, not a getter.
            Decode.map2
                AgentModel.create
                (Decode.field "id" modelId.Decode)
                (Decode.optional "name" Decode.string |> Decode.map Option.toObj) }

    /// The catalogue as the session serves it to a picker. An OBJECT around the list rather
    /// than a bare array, so the reply has somewhere to grow — a provider's default, a
    /// deprecation note — without every reader needing a new shape on the same day.
    let modelCatalogue : Codec<AgentModel list> =
        { Encode = (fun models -> Encode.object [ "models", Encode.list (models |> List.map agentModel.Encode) ])
          Decode = Decode.field "models" (Decode.list agentModel.Decode) }

    /// One connection as a panel row reads it. The kind crosses as the word a person is
    /// shown, which is `ConnectionKind`'s to say — here and on the screen alike.
    let private credentialRow : Codec<Access.CredentialRow> =
        { Encode =
            fun (row: Access.CredentialRow) ->
                Encode.object
                    [ "kind", Encode.string (Access.ConnectionKind.label row.Kind)
                      "signInRequired", Encode.option Encode.string row.SignInRequired ]
          Decode =
            Decode.object (fun get ->
                // REQUIRED, and the distinction is the rule's: a row with no `kind` at all is
                // a malformed row and fails, while a kind this build does not KNOW is a word
                // `ofLabel` has an answer for. Defaulting the missing one to `""` made the
                // two the same thing and called it static.
                { Access.CredentialRow.Kind =
                    get.Required.Field "kind" Decode.string |> Access.ConnectionKind.ofLabel
                  Access.CredentialRow.SignInRequired = get.Optional.Field "signInRequired" Decode.string }) }

    /// Who the shared scope belongs to, as the wire spells it. The words are the ones this
    /// surface has always used; what changed is that a reader must now answer for both.
    let private sharedOwner : Codec<Access.SharedOwner> =
        { Encode =
            (fun owner ->
                Encode.string (
                    match owner with
                    | Access.OwnedByUser -> "user"
                    | Access.OwnedByDeployment -> "local"))
          Decode =
            Decode.string
            |> Decode.andThen (function
                | "user" -> Decode.succeed Access.OwnedByUser
                | "local" -> Decode.succeed Access.OwnedByDeployment
                // Not defaulted to either: one of them promises a credential is private
                // and the other that it is shared, and a build that cannot tell which must
                // not pick. The panel is refused; the drawer keeps what it had.
                | other -> Decode.fail (sprintf "unknown shared-scope owner '%s'" other)) }

    /// The Claude panel as the session serves it.
    ///
    /// `models` is decoded as a value and run through `modelCatalogue` SEPARATELY, rather
    /// than inline where its failure would fail the whole reply: a catalogue this build
    /// cannot read is a reason to show in the picker, and it must not also take the
    /// connection rows down with it. `modelsUnavailable` is why there is none — the same
    /// null-or-reason shape as `signInRequired`, and never both.
    let claudePanel : Codec<Access.ClaudePanel> =
        { Encode =
            fun (panel: Access.ClaudePanel) ->
                Encode.object
                    [ "session", Encode.option credentialRow.Encode panel.SessionCredential
                      "mine", Encode.option credentialRow.Encode panel.MineCredential
                      "owner", sharedOwner.Encode panel.Owner
                      "agent", Encode.bool panel.AgentAvailable
                      "models",
                      (match panel.Models with
                       | Access.ModelsLoaded models -> modelCatalogue.Encode models
                       | Access.ModelsUnknown
                       | Access.ModelsUnavailable _ -> Encode.nil)
                      "modelsUnavailable",
                      (match panel.Models with
                       | Access.ModelsUnavailable reason -> Encode.string reason
                       | Access.ModelsUnknown
                       | Access.ModelsLoaded _ -> Encode.nil) ]
          Decode =
            Decode.object (fun get ->
                { Access.ClaudePanel.SessionCredential = get.Optional.Field "session" credentialRow.Decode
                  Access.ClaudePanel.MineCredential = get.Optional.Field "mine" credentialRow.Decode
                  // Required, both: a panel says these or it is not a panel. "Not told yet"
                  // is the absence of a panel, which a client spells by holding one as an
                  // option — never by a panel that arrived saying nothing.
                  Access.ClaudePanel.Owner = get.Required.Field "owner" sharedOwner.Decode
                  Access.ClaudePanel.AgentAvailable = get.Required.Field "agent" Decode.bool
                  Access.ClaudePanel.Models =
                    match get.Optional.Field "models" Decode.value with
                    | Some raw ->
                        match Decode.fromValue "$.models" modelCatalogue.Decode raw with
                        | Ok models -> Access.ModelsLoaded models
                        | Error reason -> Access.ModelsUnavailable reason
                    | None ->
                        match get.Optional.Field "modelsUnavailable" Decode.string with
                        | Some reason -> Access.ModelsUnavailable reason
                        // Neither: a session process answering the rows alone.
                        | None -> Access.ModelsUnknown }) }

    /// The GitHub panel as the session serves it: the same two rows, and nothing this
    /// provider has no answer for.
    let githubPanel : Codec<Access.GitHubPanel> =
        { Encode =
            fun (panel: Access.GitHubPanel) ->
                Encode.object
                    [ "session", Encode.option credentialRow.Encode panel.SessionCredential
                      "mine", Encode.option credentialRow.Encode panel.MineCredential
                      "owner", sharedOwner.Encode panel.Owner ]
          Decode =
            Decode.object (fun get ->
                { Access.GitHubPanel.SessionCredential = get.Optional.Field "session" credentialRow.Decode
                  Access.GitHubPanel.MineCredential = get.Optional.Field "mine" credentialRow.Decode
                  Access.GitHubPanel.Owner = get.Required.Field "owner" sharedOwner.Decode }) }

    /// One frame of the session's read stream. Tagged, for `queryFrame`'s reason: one
    /// connection carries every read model, and a client folds each frame by what it is.
    let readFrame : Codec<Tools.ReadFrame> =
        { Encode =
            (fun frame ->
                match frame with
                | Tools.Queried inner ->
                    Encode.object [ "kind", Encode.string "query"; "frame", queryFrame.Encode inner ]
                | Tools.Panels (claude, github) ->
                    Encode.object
                        [ "kind", Encode.string "panels"
                          "claude", claudePanel.Encode claude
                          "github", githubPanel.Encode github ])
          Decode =
            Decode.field "kind" Decode.string
            |> Decode.andThen (function
                | "query" -> Decode.field "frame" queryFrame.Decode |> Decode.map Tools.Queried
                | "panels" ->
                    Decode.map2
                        (fun claude github -> Tools.Panels (claude, github))
                        (Decode.field "claude" claudePanel.Decode)
                        (Decode.field "github" githubPanel.Decode)
                | other -> Decode.fail (sprintf "unknown read frame '%s'" other)) }

    let private repoCandidate : Codec<Repos.RepoCandidate> =
        { Encode =
            fun (candidate: Repos.RepoCandidate) ->
                Encode.object
                    [ "repo", repoRef.Encode candidate.Repo
                      "description", Encode.option Encode.string candidate.Description
                      "defaultBranch", Encode.string candidate.DefaultBranch
                      "private", Encode.bool candidate.Private
                      "pushedAt", Encode.option Encode.string candidate.PushedAt ]
          Decode =
            Decode.object (fun get ->
                { Repos.RepoCandidate.Repo = get.Required.Field "repo" repoRef.Decode
                  Repos.RepoCandidate.Description = get.Optional.Field "description" Decode.string
                  Repos.RepoCandidate.DefaultBranch = get.Required.Field "defaultBranch" Decode.string
                  Repos.RepoCandidate.Private = get.Optional.Field "private" Decode.bool |> Option.defaultValue false
                  Repos.RepoCandidate.PushedAt = get.Optional.Field "pushedAt" Decode.string }) }

    /// What a person chooses a repo FROM, as the session serves it to the picker — the
    /// `modelCatalogue` shape, for its reason: an object around the list, with room to grow.
    ///
    /// `next` is absent at the end of the listing rather than null-and-present: the picker
    /// asks whether there is one, and an optional field answers that in the codec rather
    /// than in a reader downstream.
    let repoPage : Codec<Repos.RepoPage> =
        { Encode =
            fun (page: Repos.RepoPage) ->
                Encode.object
                    [ yield "repos", Encode.list (page.Candidates |> List.map repoCandidate.Encode)
                      match page.Next with
                      | Some next -> yield "next", Encode.string next
                      | None -> () ]
          Decode =
            Decode.object (fun get ->
                { Repos.RepoPage.Candidates = get.Required.Field "repos" (Decode.list repoCandidate.Decode)
                  Repos.RepoPage.Next = get.Optional.Field "next" Decode.string }) }

    /// One page of a repo's branches, by name — `repoPage`'s shape, since it is the same
    /// question asked of a different listing.
    let branchPage : Codec<Repos.BranchPage> =
        { Encode =
            fun (page: Repos.BranchPage) ->
                Encode.object
                    [ yield "branches", Encode.list (page.Names |> List.map Encode.string)
                      match page.Next with
                      | Some next -> yield "next", Encode.string next
                      | None -> () ]
          Decode =
            Decode.object (fun get ->
                { Repos.BranchPage.Names = get.Required.Field "branches" (Decode.list Decode.string)
                  Repos.BranchPage.Next = get.Optional.Field "next" Decode.string }) }

    /// Where a pull request comes from: the repository holding its head, and the branch.
    let pullHead : Codec<Repos.PullHead> =
        { Encode =
            fun (head: Repos.PullHead) ->
                Encode.object [ "repo", repoRef.Encode head.Repo; "branch", Encode.string head.Branch ]
          Decode =
            Decode.object (fun get ->
                { Repos.PullHead.Repo = get.Required.Field "repo" repoRef.Decode
                  Repos.PullHead.Branch = get.Required.Field "branch" Decode.string }) }

    /// Serialize a value to a compact JSON string.
    let toString (codec: Codec<'a>) (value: 'a) : string =
        codec.Encode value |> Encode.toString 0

    /// Deserialize a value from a JSON string.
    ///
    /// A decoder that RAISES is answered here as an `Error`, never let through. That is not
    /// a second mechanism for the same requirement as `agentModel`'s `map2` above: that one
    /// stops THIS decoder throwing, while this stops the NEXT one taking a reader down with
    /// it, and they go red at different times. Every caller of this handles `Error`; none of
    /// them survives an exception — the read stream's own promise is that a malformed frame
    /// is dropped rather than thrown, and a frame that arrives mid-stream is exactly where
    /// a decoder nobody has exercised on bad input gets its first hostile value.
    let fromString (codec: Codec<'a>) (json: string) : Result<'a, string> =
        try
            Decode.fromString codec.Decode json
        with e ->
            Error (sprintf "the decoder raised rather than refused: %s" e.Message)

    /// A gated command's arguments, on the wire (Plan 15, stage 3b).
    ///
    /// A list of strings, deliberately — every gated command's arguments are names, branches
    /// and flags, and a positional list is the smallest thing that survives the doc and comes
    /// back to a process that did not write it. A per-command schema would be the
    /// JSON-Schema-subset renderer this plan already deferred, arriving through the back
    /// door; when that lands, the card can read these and this becomes its encoding.
    ///
    /// Never a credential. `PendingAct.OnBehalfOf` names WHOSE, and the value is resolved at
    /// execution — the pending list replicates to every peer, and a shape that could hold a
    /// token eventually does.
    let gatedArgs : Codec<string list> =
        { Encode = fun values -> Encode.list (values |> List.map Encode.string)
          Decode = Decode.list Decode.string }

/// Rebuilding a `.cast` file from what a client has fetched (Plan 13, stage 3e).
///
/// The audit read. A closed terminal's blocks survive in the projection, but a list of
/// commands is not the same artefact as the RECORDING — a replay shows the terminal as it
/// behaved, at the speed it behaved, which is what someone auditing actually wants to watch.
///
/// It is cheap because the transcript already IS asciicast v2 on disk and the chunk route
/// already serves it: concatenating chunk 0, 1, 2 … reproduces the file byte for byte, so any
/// prefix of chunks is a valid `.cast`. This reassembles that from the DECODED records a
/// client holds, which is the same thing through a round trip the codec already pins — and it
/// means the replay rides the browser's HTTP cache rather than a second whole-file route.
module TranscriptReplay =

    /// The `.cast` text for a header, the records under it, and CHAPTER MARKERS spliced in
    /// as asciicast's own `"m"` events (Plan 25, stage 1).
    ///
    /// Markers belong in the file rather than in the player's `markers` option, and the
    /// difference is not cosmetic. The player idle-compresses EVENTS as it loads a recording
    /// (`idleTimeLimit`), then multiplexes an option-supplied marker list in afterwards, on
    /// the clock the file was written in — so option markers land in dead air the
    /// compression just removed. Everything downstream of that reads wrong at once: the
    /// duration shown is the uncompressed one, playback trudges through gaps to reach a
    /// marker, a `startAt` computed against the compressed clock lands short of the chapter
    /// it names, and the last marker is dropped by the chapter list's `time < duration`
    /// filter because it has become the final event. A marker written into the cast rides
    /// the same compression as every record around it and none of that happens.
    ///
    /// A marker sorts BEFORE a record at the same time — the order the player's own
    /// multiplex picks — so a chapter names the command whose first byte follows it.
    ///
    /// `"m"` is not a `TranscriptKind`, and it should not become one: no transcript on disk
    /// contains a marker. Chapters are a fact about the BLOCKS a terminal ran, folded from
    /// the event log at the moment a recording is assembled for a reader.
    ///
    /// Two residuals the player keeps, and neither is a reason to go back to the option. A
    /// marker at exactly the cast's LAST event time is still dropped from the chapter list —
    /// the UI filters on `time < duration`, strictly, and a marker that is the final event has
    /// become the duration. That is a block that printed nothing, and the `"m"` event is in the
    /// recording either way. And the control bar shows the raw poster time until the reader
    /// first interacts, so a poster nudged past the final record reads a hair long before play.
    let castWithMarkers
        (header: TranscriptHeader)
        (records: (int * TranscriptRecord) list)
        (markers: (float * string) list)
        : string =
        let markerLine (at: float) (label: string) =
            [ Encode.float at; Encode.string "m"; Encode.string label ]
            |> Encode.list
            |> Encode.toString 0
        let recordLine (record: TranscriptRecord) =
            Codec.toString Codec.transcriptLine (TranscriptRecordLine record)
        let rec merge (records: (int * TranscriptRecord) list) (markers: (float * string) list) =
            match records, markers with
            | [], [] -> []
            | [], (at, label) :: restMarkers -> markerLine at label :: merge [] restMarkers
            | (_, record) :: restRecords, [] -> recordLine record :: merge restRecords []
            | (_, record) :: restRecords, (at, label) :: restMarkers ->
                if at <= record.At then markerLine at label :: merge records restMarkers
                else recordLine record :: merge restRecords markers
        let lines =
            (Codec.toString Codec.transcriptLine (TranscriptHeaderLine header))
            :: merge (records |> List.sortBy fst) (markers |> List.sortBy fst)
        String.concat "\n" lines + "\n"

    /// The `.cast` text for a header and the records under it, in sequence order.
    ///
    /// Gaps are simply absent rather than filled: a record the client never fetched, or one
    /// retention deleted, is a line that is not there. asciicast has no notion of a hole, and
    /// inventing a placeholder would put something in the recording that the terminal never
    /// printed. Whether the recording is COMPLETE is a separate question, answered by the
    /// terminal's `DroppedBytes` and said in the surface rather than smuggled into the file.
    ///
    /// A recording with no chapters: the same text `castWithMarkers` writes when nothing
    /// marks it, so the two can never disagree about the shape of a cast.
    let cast (header: TranscriptHeader) (records: (int * TranscriptRecord) list) : string =
        castWithMarkers header records []

    /// The `.cast` text for the half-open transcript range `[fromSeq, toSeq)` — one block's
    /// output, or one stretch of live mode — as a standalone recording the stock player
    /// renders unmodified.
    ///
    /// Three things happen here, and the first two are the ones a naive slice gets wrong:
    ///
    ///   * **The keyframe is painted first**, as a synthesized output record at `t = 0`, so
    ///     the range starts from the screen it actually started from rather than from a
    ///     blank VT. Its size overrides the header's, because the header records the size
    ///     the terminal OPENED at and a resize before the range changed it.
    ///   * **Times are rebased** to the range's first record, because asciicast times are
    ///     relative to the start of the file — without this a block forty minutes in makes
    ///     the player idle for forty minutes before its first frame.
    ///   * Records outside the range are dropped, and INPUT records are kept: what someone
    ///     typed is part of a stretch of live mode, and a pty echoes it anyway.
    ///
    /// With no keyframe (a recording written before they existed) the range still rebases
    /// and still plays; it is the naive slice, approximately right for command output and
    /// wrong wherever the screen carried state in. That is the honest degradation — the
    /// alternative is refusing to play a recording we do have.
    let range
        (header: TranscriptHeader)
        (keyframe: TranscriptKeyframe option)
        (fromSeq: int)
        (toSeq: int)
        (records: (int * TranscriptRecord) list)
        : string =
        let inRange =
            records
            |> List.filter (fun (seq, _) -> seq >= fromSeq && seq < toSeq)
            |> List.sortBy fst
        let origin = inRange |> List.tryHead |> Option.map (fun (_, r) -> r.At) |> Option.defaultValue 0.0
        let header =
            match keyframe with
            | Some k -> { header with Width = k.Cols; Height = k.Rows }
            | None -> header
        let painted =
            match keyframe with
            | Some k when k.Screen <> "" -> [ 0, { At = 0.0; Kind = TranscriptOutput; Data = k.Screen } ]
            | _ -> []
        let rebased =
            inRange |> List.map (fun (seq, record) -> seq + 1, { record with At = record.At - origin })
        cast header (painted @ rebased)
