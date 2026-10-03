namespace Yession.App.Codecs

open Yession.Domain
open Yession.Domain.Chat
open Yession.Domain.Agent
open Yession.Domain.Link
open Yession.Domain.Terminals
open Yession.Domain.Tools
open Yession.Domain.Repos

#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

/// The frames the App and the Session exchange over the data channel: state, commands, the
/// event log, control, presence and terminals, tagged and multiplexed onto one channel. The
/// App owns them (`Sdp.fs` says why); the Session and the Node peer reference this project to
/// speak them.
[<RequireQualifiedAccess>]
module Frames =

    let private terminalFrame : Codec<TerminalFrame> =
        { Encode =
            (fun f ->
                match f with
                | TerminalRecord (id, seq, record) ->
                    Encode.object
                        [ "kind", Encode.string "record"
                          "terminalId", Codec.terminalId.Encode id
                          "seq", Encode.int seq
                          "record", Transcripts.record.Encode record ]
                | TerminalTranscriptAvailable (id, nextSeq) ->
                    Encode.object
                        [ "kind", Encode.string "available"
                          "terminalId", Codec.terminalId.Encode id
                          "nextSeq", Encode.int nextSeq ]
                // Flat rather than a nested keyframe object, so the two fields this frame
                // has always carried keep their names and their places: a client served an
                // older bundle out of its service-worker cache still reads the screen it
                // knows how to read, and simply does not learn the size.
                | TerminalSnapshot (id, keyframe) ->
                    Encode.object
                        [ "kind", Encode.string "snapshot"
                          "terminalId", Codec.terminalId.Encode id
                          "seq", Encode.int keyframe.Seq
                          "cols", Encode.int keyframe.Cols
                          "rows", Encode.int keyframe.Rows
                          "screen", Encode.string keyframe.Screen ]
                | TerminalInput (id, data) ->
                    Encode.object
                        [ "kind", Encode.string "input"
                          "terminalId", Codec.terminalId.Encode id
                          "data", Encode.string data ]
                | TerminalResize (id, cols, rows) ->
                    Encode.object
                        [ "kind", Encode.string "resize"
                          "terminalId", Codec.terminalId.Encode id
                          "cols", Encode.int cols
                          "rows", Encode.int rows ])
          Decode =
            Decode.field "kind" Decode.string
            |> Decode.andThen (function
                | "record" ->
                    Decode.map3
                        (fun id seq record -> TerminalRecord (id, seq, record))
                        (Decode.field "terminalId" Codec.terminalId.Decode)
                        (Decode.field "seq" Decode.int)
                        (Decode.field "record" Transcripts.record.Decode)
                | "available" ->
                    Decode.map2
                        (fun id nextSeq -> TerminalTranscriptAvailable (id, nextSeq))
                        (Decode.field "terminalId" Codec.terminalId.Decode)
                        (Decode.field "nextSeq" Decode.int)
                // The size is OPTIONAL, and defaults to the size every terminal opens at.
                // A frame written before it was carried is a frame from a Session Process
                // that had not resized anything — so 80x24 is not a guess there, it is what
                // that screen was painted at.
                | "snapshot" ->
                    Decode.object (fun get ->
                        TerminalSnapshot (
                            get.Required.Field "terminalId" Codec.terminalId.Decode,
                            { Seq = get.Required.Field "seq" Decode.int
                              Cols =
                                get.Optional.Field "cols" Decode.int
                                |> Option.defaultValue Size.default'.Cols
                              Rows =
                                get.Optional.Field "rows" Decode.int
                                |> Option.defaultValue Size.default'.Rows
                              Screen = get.Required.Field "screen" Decode.string }))
                | "input" ->
                    Decode.map2
                        (fun id data -> TerminalInput (id, data))
                        (Decode.field "terminalId" Codec.terminalId.Decode)
                        (Decode.field "data" Decode.string)
                | "resize" ->
                    Decode.map3
                        (fun id cols rows -> TerminalResize (id, cols, rows))
                        (Decode.field "terminalId" Codec.terminalId.Decode)
                        (Decode.field "cols" Decode.int)
                        (Decode.field "rows" Decode.int)
                | other -> Decode.fail (sprintf "Unknown terminal frame: %s" other)) }

    let private sessionCommand : Codec<SessionCommand> =
        { Encode =
            (fun c ->
                match c with
                | InterruptAgentTurn t ->
                    Encode.object [ "kind", Encode.string "interruptAgentTurn"; "agentTurnId", Codec.agentTurnId.Encode t ]
                | OpenTerminal (title, sandbox) ->
                    Encode.object
                        [ "kind", Encode.string "openTerminal"
                          "title", Encode.string title
                          "sandbox", Codec.sandboxRef.Encode sandbox ]
                | CloseTerminal id ->
                    Encode.object [ "kind", Encode.string "closeTerminal"; "terminalId", Codec.terminalId.Encode id ]
                | TakeTerminalLease id ->
                    Encode.object [ "kind", Encode.string "takeTerminalLease"; "terminalId", Codec.terminalId.Encode id ]
                | ReleaseTerminalLease id ->
                    Encode.object
                        [ "kind", Encode.string "releaseTerminalLease"; "terminalId", Codec.terminalId.Encode id ]
                | RearmTerminal id ->
                    Encode.object [ "kind", Encode.string "rearmTerminal"; "terminalId", Codec.terminalId.Encode id ]
                | ReattachTerminal id ->
                    Encode.object [ "kind", Encode.string "reattachTerminal"; "terminalId", Codec.terminalId.Encode id ]
                | ApproveRepoCapabilities (repo, granted) ->
                    Encode.object
                        [ "kind", Encode.string "approveRepoCapabilities"
                          "repo", Codec.repoRef.Encode repo
                          "granted", Encode.list (granted |> List.map Encode.string) ]
                | AddRepo (repo, branch) ->
                    Encode.object
                        [ "kind", Encode.string "addRepo"
                          "repo", Codec.repoRef.Encode repo
                          "branch", Encode.option Encode.string branch ])
          Decode =
            Decode.field "kind" Decode.string
            |> Decode.andThen (function
                | "interruptAgentTurn" -> Decode.field "agentTurnId" Codec.agentTurnId.Decode |> Decode.map InterruptAgentTurn
                | "approveRepoCapabilities" ->
                    Decode.map2
                        (fun repo granted -> ApproveRepoCapabilities (repo, granted))
                        (Decode.field "repo" Codec.repoRef.Decode)
                        (Decode.field "granted" (Decode.list Decode.string))
                | "addRepo" ->
                    Decode.map2
                        (fun repo branch -> AddRepo (repo, branch))
                        (Decode.field "repo" Codec.repoRef.Decode)
                        (Decode.optional "branch" Decode.string)
                | "openTerminal" ->
                    // An absent sandbox is `default`, which is what a client that could not
                    // name one meant: a peer's open was a shell in `default` and nothing else.
                    Decode.map2
                        (fun title sandbox -> OpenTerminal (title, sandbox |> Option.defaultValue SandboxRef.defaultRef))
                        (Decode.field "title" Decode.string)
                        (Decode.optional "sandbox" Codec.sandboxRef.Decode)
                | "closeTerminal" -> Decode.field "terminalId" Codec.terminalId.Decode |> Decode.map CloseTerminal
                | "takeTerminalLease" -> Decode.field "terminalId" Codec.terminalId.Decode |> Decode.map TakeTerminalLease
                | "releaseTerminalLease" ->
                    Decode.field "terminalId" Codec.terminalId.Decode |> Decode.map ReleaseTerminalLease
                | "rearmTerminal" -> Decode.field "terminalId" Codec.terminalId.Decode |> Decode.map RearmTerminal
                | "reattachTerminal" -> Decode.field "terminalId" Codec.terminalId.Decode |> Decode.map ReattachTerminal
                | other -> Decode.fail (sprintf "Unknown session command: %s" other)) }

    let private sessionCommandResult : Codec<SessionCommandResult> =
        { Encode =
            (fun r ->
                match r with
                | CommandAccepted -> Encode.object [ "kind", Encode.string "accepted" ]
                | CommandRejected reason ->
                    Encode.object [ "kind", Encode.string "rejected"; "reason", Encode.string reason ])
          Decode =
            Decode.field "kind" Decode.string
            |> Decode.andThen (function
                | "accepted" -> Decode.succeed CommandAccepted
                | "rejected" -> Decode.field "reason" Decode.string |> Decode.map CommandRejected
                | other -> Decode.fail (sprintf "Unknown command result: %s" other)) }

    let private commandFrame : Codec<CommandFrame> =
        { Encode =
            (fun f ->
                match f with
                | Request (rid, cmd) ->
                    Encode.object
                        [ "kind", Encode.string "request"
                          "requestId", Codec.requestId.Encode rid
                          "command", sessionCommand.Encode cmd ]
                | Response (rid, res) ->
                    Encode.object
                        [ "kind", Encode.string "response"
                          "requestId", Codec.requestId.Encode rid
                          "result", sessionCommandResult.Encode res ])
          Decode =
            Decode.field "kind" Decode.string
            |> Decode.andThen (function
                | "request" ->
                    Decode.map2
                        (fun rid cmd -> Request(rid, cmd))
                        (Decode.field "requestId" Codec.requestId.Decode)
                        (Decode.field "command" sessionCommand.Decode)
                | "response" ->
                    Decode.map2
                        (fun rid res -> Response(rid, res))
                        (Decode.field "requestId" Codec.requestId.Decode)
                        (Decode.field "result" sessionCommandResult.Decode)
                | other -> Decode.fail (sprintf "Unknown command frame: %s" other)) }

    let private eventLogFrame : Codec<EventLogFrame> =
        { Encode =
            (fun f ->
                match f with
                | EventsAvailable off ->
                    Encode.object [ "kind", Encode.string "eventsAvailable"; "latestOffset", Codec.eventOffset.Encode off ]
                | ReadEventsAfter (rid, after, limit) ->
                    Encode.object
                        [ "kind", Encode.string "readEventsAfter"
                          "requestId", Codec.requestId.Encode rid
                          "after", Encode.option Codec.eventOffset.Encode after
                          "limit", Encode.int limit ]
                | EventsPage (rid, page) ->
                    Encode.object
                        [ "kind", Encode.string "eventsPage"
                          "requestId", Codec.requestId.Encode rid
                          "page", Events.sessionEventPage.Encode page ])
          Decode =
            Decode.field "kind" Decode.string
            |> Decode.andThen (function
                | "eventsAvailable" ->
                    Decode.field "latestOffset" Codec.eventOffset.Decode |> Decode.map EventsAvailable
                | "readEventsAfter" ->
                    Decode.map3
                        (fun rid after limit -> ReadEventsAfter(rid, after, limit))
                        (Decode.field "requestId" Codec.requestId.Decode)
                        (Decode.field "after" (Decode.option Codec.eventOffset.Decode))
                        (Decode.field "limit" Decode.int)
                | "eventsPage" ->
                    Decode.map2
                        (fun rid page -> EventsPage(rid, page))
                        (Decode.field "requestId" Codec.requestId.Decode)
                        (Decode.field "page" Events.sessionEventPage.Decode)
                | other -> Decode.fail (sprintf "Unknown event-log frame: %s" other)) }

    let private peerHello : Codec<PeerHelloPayload> =
        { Encode =
            (fun (p: PeerHelloPayload) ->
                Encode.object
                    [ "peerId", Codec.peerId.Encode p.PeerId
                      "displayName", Encode.string p.DisplayName
                      "token", Encode.string p.Token ])
          Decode =
            Decode.object (fun get ->
                { PeerHelloPayload.PeerId = get.Required.Field "peerId" Codec.peerId.Decode
                  PeerHelloPayload.DisplayName = get.Required.Field "displayName" Decode.string
                  PeerHelloPayload.Token = get.Required.Field "token" Decode.string }) }

    let private peerAccepted : Codec<PeerAcceptedPayload> =
        { Encode =
            (fun (p: PeerAcceptedPayload) ->
                Encode.object
                    [ "sessionId", Codec.sessionId.Encode p.SessionId
                      "assignedDisplayName", Encode.string p.AssignedDisplayName
                      "latestOffset", Encode.option Codec.eventOffset.Encode p.LatestOffset ])
          Decode =
            Decode.object (fun get ->
                { PeerAcceptedPayload.SessionId = get.Required.Field "sessionId" Codec.sessionId.Decode
                  PeerAcceptedPayload.AssignedDisplayName = get.Required.Field "assignedDisplayName" Decode.string
                  PeerAcceptedPayload.LatestOffset = get.Required.Field "latestOffset" (Decode.option Codec.eventOffset.Decode) }) }

    let private controlFrame : Codec<ControlFrame> =
        { Encode =
            (fun f ->
                match f with
                | PeerHello p -> Encode.object [ "kind", Encode.string "peerHello"; "payload", peerHello.Encode p ]
                | PeerAccepted p -> Encode.object [ "kind", Encode.string "peerAccepted"; "payload", peerAccepted.Encode p ]
                | PeerRejected reason -> Encode.object [ "kind", Encode.string "peerRejected"; "reason", Encode.string reason ]
                | Ping -> Encode.object [ "kind", Encode.string "ping" ]
                | Pong -> Encode.object [ "kind", Encode.string "pong" ])
          Decode =
            Decode.field "kind" Decode.string
            |> Decode.andThen (function
                | "peerHello" -> Decode.field "payload" peerHello.Decode |> Decode.map PeerHello
                | "peerAccepted" -> Decode.field "payload" peerAccepted.Decode |> Decode.map PeerAccepted
                | "peerRejected" -> Decode.field "reason" Decode.string |> Decode.map PeerRejected
                | "ping" -> Decode.succeed Ping
                | "pong" -> Decode.succeed Pong
                | other -> Decode.fail (sprintf "Unknown control frame: %s" other)) }

    let private stateFrame (stateCodec: Codec<'State>) : Codec<StateFrame<'State>> =
        { Encode = fun (StateSync s) -> Encode.object [ "kind", Encode.string "stateSync"; "state", stateCodec.Encode s ]
          Decode = Decode.field "state" stateCodec.Decode |> Decode.map StateSync }

    let private focusField : Codec<FocusField> =
        { Encode =
            (fun f ->
                match f with
                | Title -> Encode.object [ "kind", Encode.string "title" ]
                | DraftBody p -> Encode.object [ "kind", Encode.string "draft"; "peerId", Codec.peerId.Encode p ]
                | QueueBody q -> Encode.object [ "kind", Encode.string "queue"; "queueId", Codec.queueId.Encode q ]
                | TerminalDraftBody (t, p) ->
                    Encode.object
                        [ "kind", Encode.string "terminalDraft"
                          "terminalId", Codec.terminalId.Encode t
                          "peerId", Codec.peerId.Encode p ]
                | TerminalQueuedBody q ->
                    Encode.object [ "kind", Encode.string "terminalQueued"; "queueId", Codec.queueId.Encode q ]
                | ChapterName m ->
                    Encode.object [ "kind", Encode.string "chapterName"; "messageId", Codec.messageId.Encode m ])
          Decode =
            Decode.field "kind" Decode.string
            |> Decode.andThen (function
                | "title" -> Decode.succeed Title
                | "draft" -> Decode.field "peerId" Codec.peerId.Decode |> Decode.map DraftBody
                | "queue" -> Decode.field "queueId" Codec.queueId.Decode |> Decode.map QueueBody
                | "terminalDraft" ->
                    Decode.map2
                        (fun t p -> TerminalDraftBody (t, p))
                        (Decode.field "terminalId" Codec.terminalId.Decode)
                        (Decode.field "peerId" Codec.peerId.Decode)
                | "terminalQueued" -> Decode.field "queueId" Codec.queueId.Decode |> Decode.map TerminalQueuedBody
                | "chapterName" -> Decode.field "messageId" Codec.messageId.Decode |> Decode.map ChapterName
                | other -> Decode.fail (sprintf "Unknown focus field: %s" other)) }

    let private cursorPos : Codec<CursorPos> =
        { Encode = fun (p: CursorPos) -> Encode.object [ "anchor", Encode.string p.Anchor; "head", Encode.string p.Head ]
          Decode =
            Decode.object (fun get ->
                { Anchor = get.Required.Field "anchor" Decode.string
                  Head = get.Required.Field "head" Decode.string }) }

    let private focus : Codec<Focus> =
        { Encode = fun (f: Focus) -> Encode.object [ "field", focusField.Encode f.Field; "pos", cursorPos.Encode f.Pos ]
          Decode =
            Decode.object (fun get ->
                { Field = get.Required.Field "field" focusField.Decode
                  Pos = get.Required.Field "pos" cursorPos.Decode }) }

    let private presencePayload : Codec<PresencePayload> =
        { Encode =
            (fun (p: PresencePayload) ->
                Encode.object
                    [ "who", Codec.actor.Encode p.Who
                      "displayName", Encode.string p.DisplayName
                      "focus", Encode.option focus.Encode p.Focus
                      "viewing", Encode.option Codec.viewRef.Encode p.Viewing ])
          Decode =
            Decode.object (fun get ->
                { PresencePayload.Who = get.Required.Field "who" Codec.actor.Decode
                  PresencePayload.DisplayName = get.Required.Field "displayName" Decode.string
                  PresencePayload.Focus = get.Required.Field "focus" (Decode.option focus.Decode)
                  // OPTIONAL where the caret is required: a browser tab left open across a
                  // deploy speaks the older frame, and a peer that cannot say what it is
                  // viewing is a peer viewing nothing — not a presence frame to throw away,
                  // which would take its caret down with it.
                  PresencePayload.Viewing = get.Optional.Field "viewing" Codec.viewRef.Decode }) }

    /// A frame codec for any `'State` codec. The transport never inspects the state
    /// payload; the state codec belongs to the sync-boundary layer (Step 05).
    let session (stateCodec: Codec<'State>) : Codec<SessionFrame<'State>> =
        let sf = stateFrame stateCodec
        { Encode =
            (fun f ->
                match f with
                | State s -> Encode.object [ "tag", Encode.string "state"; "payload", sf.Encode s ]
                | Command c -> Encode.object [ "tag", Encode.string "command"; "payload", commandFrame.Encode c ]
                | EventLog e -> Encode.object [ "tag", Encode.string "eventLog"; "payload", eventLogFrame.Encode e ]
                | Control c -> Encode.object [ "tag", Encode.string "control"; "payload", controlFrame.Encode c ]
                | Presence p -> Encode.object [ "tag", Encode.string "presence"; "payload", presencePayload.Encode p ]
                | Terminal t -> Encode.object [ "tag", Encode.string "terminal"; "payload", terminalFrame.Encode t ])
          Decode =
            Decode.field "tag" Decode.string
            |> Decode.andThen (function
                | "state" -> Decode.field "payload" sf.Decode |> Decode.map State
                | "command" -> Decode.field "payload" commandFrame.Decode |> Decode.map Command
                | "eventLog" -> Decode.field "payload" eventLogFrame.Decode |> Decode.map EventLog
                | "control" -> Decode.field "payload" controlFrame.Decode |> Decode.map Control
                | "presence" -> Decode.field "payload" presencePayload.Decode |> Decode.map Presence
                | "terminal" -> Decode.field "payload" terminalFrame.Decode |> Decode.map Terminal
                | other -> Decode.fail (sprintf "Unknown session frame: %s" other)) }
