namespace Yession.Manager

open Yession.Domain
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

/// The MCP servers an operator declares on the Manager, and the set of them the Manager hands
/// each Session over the control channel. The Manager owns both: it keeps the declarations in
/// its state file and serves the set at `/control/mcp`, and a Session only ever reads it.
/// One set of codecs for both, because they carry the same value, and two codecs for one type
/// is two chances to disagree about it.
[<RequireQualifiedAccess>]
module McpWire =

    /// Tagged even with one case: a second transport changes who owns the PROCESS, and a
    /// bare url on the wire would have to be re-tagged to admit one.
    let transport : Codec<McpTransport> =
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

    let serverRef : Codec<McpServerRef> =
        { Encode =
            fun (server: McpServerRef) ->
                Encode.object
                    [ "name", Codec.mcpServerName.Encode server.Name
                      "transport", transport.Encode server.Transport
                      "description", Encode.option Encode.string server.Description ]
          Decode =
            Decode.object (fun get ->
                { McpServerRef.Name = get.Required.Field "name" Codec.mcpServerName.Decode
                  McpServerRef.Transport = get.Required.Field "transport" transport.Decode
                  McpServerRef.Description = get.Optional.Field "description" Decode.string }) }

    let audience : Codec<McpAudience> =
        { Encode =
            fun (audience: McpAudience) ->
                match audience with
                | AnySession -> Encode.object [ "type", Encode.string "any" ]
                | OneSession id ->
                    Encode.object [ "type", Encode.string "session"; "sessionId", Codec.sessionId.Encode id ]
          Decode =
            Decode.field "type" Decode.string
            |> Decode.andThen (fun t ->
                match t with
                | "any" -> Decode.succeed AnySession
                | "session" -> Decode.field "sessionId" Codec.sessionId.Decode |> Decode.map OneSession
                | other -> Decode.fail (sprintf "Unknown MCP audience: %s" other)) }

    let declaration : Codec<McpDeclaration> =
        { Encode =
            fun (declaration: McpDeclaration) ->
                Encode.object
                    [ "server", serverRef.Encode declaration.Server
                      "audience", audience.Encode declaration.Audience ]
          Decode =
            Decode.object (fun get ->
                { McpDeclaration.Server = get.Required.Field "server" serverRef.Decode
                  McpDeclaration.Audience = get.Required.Field "audience" audience.Decode }) }

    /// One `/control/mcp` frame: the whole resolved set for THIS session, every time. The
    /// AUDIENCE is deliberately absent — resolution already happened, and a session that
    /// could read who else reaches a server would be reading the Manager's configuration.
    let serverSet : Codec<McpServerSet> =
        { Encode =
            fun (set: McpServerSet) ->
                Encode.object [ "servers", set.Servers |> List.map serverRef.Encode |> Encode.list ]
          Decode =
            Decode.object (fun get ->
                { McpServerSet.Servers = get.Required.Field "servers" (Decode.list serverRef.Decode) }) }
