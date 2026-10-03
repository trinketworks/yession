namespace Yession.Codecs

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
                | Session -> Encode.object [ "kind", Encode.string "sessionProcess" ]
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
                | "sessionProcess" -> Decode.succeed Session
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

    let mcpServerName : Codec<McpServerName> =
        { Encode = McpServerName.value >> Encode.string
          Decode = viaSmartCtor McpServerName.create Decode.string }

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

    // --- the query surface (Plan 15) ------------------------------------------------------
    // The wire between the Session's query registry and the browser's generated
    // read surface. Both a query's DECLARATION and its VALUE cross it: the declaration
    // because the client renders a query it has never heard of, the value because that is
    // the point. Shape and value are encoded separately rather than as one fused blob, so
    // the section's markup exists before the first value arrives (`/queries` answers, the
    // stream fills in) and so an invalidation frame carries only what changed.

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

