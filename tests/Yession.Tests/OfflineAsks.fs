module Yession.Tests.OfflineAsks

// `ask_offline_access`: the agent asks the person whose turn it is to let this session use their
// own connection while they are away, and only that person answers, from their own client. The
// session's side (`OfflineAccess`) decides whom to ask or answers at once, settles an ask from
// the status stream or a dismissal, and the settlement wakes the agent. The client's side shows
// the card to the person asked alone, and Allow is the settings' own offline press.

open System
open Fable.Pyxpecto
open Yession.Domain
open Yession.Domain.Access
open Yession.Domain.Agent
open Yession.Domain.Away
open Yession.Domain.Chat
open Yession.Session
open Yession.Host
open Yession.App
open Yession.Tests.Support

let private sessionId = SessionId.create "sess-offline" |> expect
let private ada = UserId.create "ada" |> expect
let private ivy = UserId.create "ivy" |> expect
let private messageId = MessageId.create "m1" |> expect
let private mint () = messageId
let private now = DateTimeOffset (2026, 11, 6, 0, 0, 0, TimeSpan.Zero)

let private status (id: ConnectionId) (offline: OfflineChoice) : ConnectionStatus =
    { Id = id; Kind = OAuthConnection; Health = ConnectionUsable; UpdatedAt = now; Offline = offline }

let private heard (statuses: ConnectionStatus list) : Map<ConnectionId, ConnectionStatus> =
    statuses |> List.map (fun s -> s.Id, s) |> Map.ofList

/// Ada's own GitHub, read from this session with the grant `offline`.
let private adasGitHub (offline: OfflineChoice) =
    status (OfflineAccess.ownTarget ada ConnectedService.GitHub) offline

let private asked : OfflineAccessAsked =
    { MessageId = messageId
      Person = ada
      Connection = ConnectedService.GitHub
      Why = "to watch octo/hello#12 and merge it when the checks pass" }

let private openAsks (events: SessionEvent list) = OfflineAsks.apply OfflineAsks.empty events

let private decideFor (statuses: ConnectionStatus list) (events: SessionEvent list) =
    OfflineAccess.decide sessionId (heard statuses) (openAsks events) (Principal.User ada) ConnectedService.GitHub

let private isRefused (answer: Result<UserId, string>) =
    match answer with
    | Error _ -> true
    | Ok _ -> false

let private toolTests =
    testList "The tool" [
        testCase "a person with their own connection, not allowed and not asked, is asked" <| fun () ->
            Expect.equal (decideFor [ adasGitHub OfflineChoice.Off ] []) (Ok ada) "Ada is asked"

        testCase "a person with no connection of their own is not asked" <| fun () ->
            Expect.isTrue (isRefused (decideFor [] [])) "nothing to allow"

        testCase "a connection already allowed in this session is not asked for" <| fun () ->
            Expect.isTrue (isRefused (decideFor [ adasGitHub OfflineChoice.ThisSession ] [])) "already allowed"

        testCase "a connection allowed in all their sessions is not asked for" <| fun () ->
            Expect.isTrue (isRefused (decideFor [ adasGitHub OfflineChoice.AllMySessions ] [])) "already allowed"

        testCase "an ask still open is not asked again" <| fun () ->
            Expect.isTrue
                (isRefused (decideFor [ adasGitHub OfflineChoice.Off ] [ SessionEvent.OfflineAccessAsked asked ]))
                "one ask at a time"

        testCase "an ask that was dismissed can be asked again" <| fun () ->
            let dismissed : OfflineAccessSettled =
                { MessageId = messageId; Person = ada; Connection = ConnectedService.GitHub; Outcome = OfflineAccessOutcome.Dismissed }
            Expect.equal
                (decideFor
                    [ adasGitHub OfflineChoice.Off ]
                    [ SessionEvent.OfflineAccessAsked asked; SessionEvent.OfflineAccessSettled dismissed ])
                (Ok ada)
                "a new ask"

        testCase "a turn that would spend the session's own credential asks nobody" <| fun () ->
            let sessions = status { Scope = SessionScope sessionId; Name = GitHubConnection.secretName } OfflineChoice.Off
            Expect.isTrue (isRefused (decideFor [ sessions; adasGitHub OfflineChoice.Off ] [])) "it works without them"

        testCase "a peer nobody attributed is not asked" <| fun () ->
            let peer = Principal.Peer (PeerId.create "p1" |> expect)
            Expect.isTrue
                (isRefused (OfflineAccess.decide sessionId (heard [ adasGitHub OfflineChoice.Off ]) OfflineAsks.empty peer ConnectedService.GitHub))
                "a peer owns no connection"

        testCaseAsync "an ask writes the event, as the agent's, for the person whose turn it is" <| async {
            let log = InMemoryEventLog.create sessionId (fun () -> now)
            let _, body =
                OfflineAccess.tool sessionId (fun () -> heard [ adasGitHub OfflineChoice.Off ]) (fun () -> Some log) mint (Principal.User ada)
            let! _ = body """{"connection":"github","why":"to watch octo/hello#12 and merge it when the checks pass"}"""
            let! page = log.Read None 100
            Expect.equal
                (page.Events |> List.map (fun e -> e.Actor, e.Event))
                [ ActorRef.Agent, SessionEvent.OfflineAccessAsked asked ]
                "one ask, by the agent, naming Ada"
        }

        testCaseAsync "a refused ask writes nothing" <| async {
            let log = InMemoryEventLog.create sessionId (fun () -> now)
            let _, body = OfflineAccess.tool sessionId (fun () -> Map.empty) (fun () -> Some log) mint (Principal.User ada)
            let! _ = body """{"connection":"github","why":"to keep watching"}"""
            let! page = log.Read None 100
            Expect.isEmpty page.Events "no card, no event"
        }

        testCase "the turn an answer wakes names the tool it answers" <| fun () ->
            Expect.stringContains (WakeReason.offlineAccessSettled ConnectedService.GitHub) OfflineAccess.Name "the wake names the tool"
    ]

let private settleTests =
    testList "Settling" [
        testCase "an open ask settles when the person's connection reads as allowed here" <| fun () ->
            Expect.equal
                (OfflineAccess.settlements
                    (heard [ adasGitHub OfflineChoice.AllMySessions ])
                    (openAsks [ SessionEvent.OfflineAccessAsked asked ])
                    mint)
                [ { MessageId = messageId
                    Person = ada
                    Connection = ConnectedService.GitHub
                    Outcome = OfflineAccessOutcome.Allowed OfflineAllowance.AllMySessions } ]
                "allowed in all their sessions"

        testCase "an open ask whose connection is still off does not settle" <| fun () ->
            Expect.isEmpty
                (OfflineAccess.settlements (heard [ adasGitHub OfflineChoice.Off ]) (openAsks [ SessionEvent.OfflineAccessAsked asked ]) mint)
                "still waiting"

        testCase "the person asked can dismiss the ask" <| fun () ->
            Expect.equal
                (OfflineAccess.dismissal (openAsks [ SessionEvent.OfflineAccessAsked asked ]) messageId (Principal.User ada) ConnectedService.GitHub)
                (Ok { MessageId = messageId; Person = ada; Connection = ConnectedService.GitHub; Outcome = OfflineAccessOutcome.Dismissed })
                "dismissed by Ada"

        testCase "somebody else cannot dismiss another person's ask" <| fun () ->
            Expect.isError
                (OfflineAccess.dismissal (openAsks [ SessionEvent.OfflineAccessAsked asked ]) messageId (Principal.User ivy) ConnectedService.GitHub)
                "Ivy has no ask to dismiss"

        testCaseAsync "a dismissal is recorded under the person who dismissed it" <| async {
            let log = InMemoryEventLog.create sessionId (fun () -> now)
            let! _ = log.Append ActorRef.Agent (SessionEvent.OfflineAccessAsked asked)
            let! _ = OfflineAccess.dismiss log mint (Principal.User ada) ConnectedService.GitHub
            let! page = log.Read None 100
            Expect.equal
                (page.Events |> List.last |> fun e -> e.Actor)
                (UserRef ada)
                "Ada's act"
        }

        testCase "a settlement owes the agent a turn, as the person asked" <| fun () ->
            let settled : OfflineAccessSettled =
                { MessageId = messageId; Person = ada; Connection = ConnectedService.GitHub; Outcome = OfflineAccessOutcome.Dismissed }
            Expect.equal
                (AgentWake.pendingReason [ SessionEvent.OfflineAccessAsked asked; SessionEvent.OfflineAccessSettled settled ])
                (Some (WakeReason.OfflineAccessSettled ConnectedService.GitHub, Principal.User ada))
                "owed to Ada"

        testCase "an ask alone owes the agent nothing" <| fun () ->
            Expect.equal (AgentWake.pendingReason [ SessionEvent.OfflineAccessAsked asked ]) None "nobody answered yet"

        testCase "the ask reads on the timeline as the person asked and the connection" <| fun () ->
            Expect.equal
                (Act.phrase (Act.OfflineAccessAsked asked) |> Phrase.refs)
                [ EntityRef.Actor (UserRef ada) ]
                "names Ada"
    ]

let private codecTests =
    let roundTrips (event: SessionEvent) =
        let codec = Yession.App.Codecs.Events.sessionEvent
        Expect.equal (Yession.Codecs.Codec.fromString codec (Yession.Codecs.Codec.toString codec event)) (Ok event) "round-trip"
    let settled outcome : OfflineAccessSettled =
        { MessageId = messageId; Person = ada; Connection = ConnectedService.Claude; Outcome = outcome }
    testList "Codecs" [
        testCase "an ask round-trips" <| fun () -> roundTrips (SessionEvent.OfflineAccessAsked asked)
        testCase "an ask allowed in this session round-trips" <| fun () ->
            roundTrips (SessionEvent.OfflineAccessSettled (settled (OfflineAccessOutcome.Allowed OfflineAllowance.ThisSession)))
        testCase "an ask allowed in all sessions round-trips" <| fun () ->
            roundTrips (SessionEvent.OfflineAccessSettled (settled (OfflineAccessOutcome.Allowed OfflineAllowance.AllMySessions)))
        testCase "a dismissed ask round-trips" <| fun () ->
            roundTrips (SessionEvent.OfflineAccessSettled (settled OfflineAccessOutcome.Dismissed))
        testCase "a turn woken by a settlement round-trips" <| fun () ->
            roundTrips (
                SessionEvent.AgentTurnStarted
                    { AgentTurnId = AgentTurnId.create "t1" |> expect
                      Cause = TurnCause.Woke (WakeReason.OfflineAccessSettled ConnectedService.Claude) })
        testCase "a dismissal round-trips as a command" <| fun () ->
            let codec = Yession.App.Codecs.Frames.session Yession.Codecs.Codec.string
            let frame =
                Yession.Domain.Link.Command (
                    Yession.Domain.Link.Request (RequestId.fresh (), Yession.Domain.Link.DismissOfflineAsk ConnectedService.GitHub))
            Expect.equal (Yession.Codecs.Codec.fromString codec (Yession.Codecs.Codec.toString codec frame)) (Ok frame) "round-trip"
    ]

// --- the card ----------------------------------------------------------------------------

let private adaPeer : PeerState = { PeerId = PeerId.create "ada-peer" |> expect; DisplayName = "Ada" }

/// A client signed in as `user`, holding the log so far.
let private clientOf (user: UserId) (events: SessionEvent list) : ClientModel =
    let init = ClientModel.init adaPeer
    { init with
        Attribution = { init.Attribution with PeerUsers = Map.ofList [ adaPeer.PeerId, user ] }
        OfflineAsks = OfflineAsks.apply OfflineAsks.empty events }

/// The GitHub panel reading Ada's own connection with the grant `offline`.
let private withGitHubGrant (offline: OfflineChoice) (model: ClientModel) : ClientModel =
    let panel : GitHubPanel =
        { SessionCredential = None
          MineCredential = Some { Kind = OAuthConnection; SignInRequired = None; Offline = offline }
          Owner = OwnedByUser }
    { model with GitHub = { model.GitHub with Status = Some panel } }

let private cardsIn (html: string) =
    let needle = Dom.attr Dom.Hooks.offlineAsk "github"
    let rec count (from: int) (found: int) =
        match html.IndexOf (needle, from) with
        | -1 -> found
        | at -> count (at + needle.Length) (found + 1)
    count 0 0

let private cardTests =
    testList "The card" [
        testCase "the person asked has the card" <| fun () ->
            Expect.equal (cardsIn (render (clientOf ada [ SessionEvent.OfflineAccessAsked asked ]))) 1 "one card for Ada"

        testCase "somebody else in the session has no card" <| fun () ->
            Expect.equal (cardsIn (render (clientOf ivy [ SessionEvent.OfflineAccessAsked asked ]))) 0 "Ivy reads the timeline"

        testCase "the card goes when the connection reads as allowed here" <| fun () ->
            let model = clientOf ada [ SessionEvent.OfflineAccessAsked asked ] |> withGitHubGrant OfflineChoice.ThisSession
            Expect.isEmpty (ClientModel.offlineAsks model) "allowed, from the card or the settings"

        testCase "the card goes when the ask is settled" <| fun () ->
            let dismissed : OfflineAccessSettled =
                { MessageId = messageId; Person = ada; Connection = ConnectedService.GitHub; Outcome = OfflineAccessOutcome.Dismissed }
            Expect.isEmpty
                (ClientModel.offlineAsks (clientOf ada [ SessionEvent.OfflineAccessAsked asked; SessionEvent.OfflineAccessSettled dismissed ]))
                "not shown again"

        testCase "Allow sends the settings' offline route with this session held by default" <| fun () ->
            let model = clientOf ada [ SessionEvent.OfflineAccessAsked asked ]
            Expect.equal
                (ClientModel.update (ClientModel.allowOffline ConnectedService.GitHub model) model |> snd)
                [ ClientEffect.GitHub
                      { Action = GitHubAction.Offline
                        Request = { GitHubRequest.scoped "mine" with Offline = Some OfflineChoice.ThisSession }
                        Expect = None } ]
                "the owner's own grant, this session"

        testCase "Allow sends the row the person holds" <| fun () ->
            let model =
                clientOf ada [ SessionEvent.OfflineAccessAsked asked ]
                |> step (HoldOfflineMsg (ConnectedService.GitHub, OfflineAllowance.AllMySessions))
            Expect.equal
                (ClientModel.update (ClientModel.allowOffline ConnectedService.GitHub model) model |> snd)
                [ ClientEffect.GitHub
                      { Action = GitHubAction.Offline
                        Request = { GitHubRequest.scoped "mine" with Offline = Some OfflineChoice.AllMySessions }
                        Expect = None } ]
                "all my sessions"

        testCase "dismissing asks the session" <| fun () ->
            Expect.equal
                (ClientModel.update (DismissOfflineAskMsg ConnectedService.GitHub) (clientOf ada [ SessionEvent.OfflineAccessAsked asked ]) |> snd)
                [ ClientEffect.DismissOfflineAsk ConnectedService.GitHub ]
                "one request, naming the connection"
    ]

let tests =
    testList "Offline asks" [
        toolTests
        settleTests
        codecTests
        cardTests
    ]
