module Yession.Host.OfflineAccess

// `ask_offline_access`: the agent asks the person whose turn it is to let this session use
// their own GitHub or Claude connection while they are away (2026-11-05, "A connection can be
// used while its owner is away").
//
// The agent only ASKS. It puts `OfflineAccessAsked` on the log, and the person's own client
// shows them a card. The grant is set the one way it always was: the person, signed in, posts
// the settings' own offline route, which the Manager refuses from anybody but the owner. So no
// tool, no command and no event here can change a grant. What the session does is notice the
// answer: the Manager's status stream showing that connection allowed here, or the person
// dismissing the card (`DismissOfflineAsk`). Either becomes `OfflineAccessSettled`, which owes
// the agent a turn (`AgentWake`).
//
// Everything that decides is a pure function over what the session has heard and what the log
// says, so a suite asks the same questions the session does. The effects at the bottom only
// read the log, append, and wake.

open Yession.Domain
open Yession.Domain.Access
open Yession.Domain.Sandboxes
open Yession.Domain.Away
open Yession.Domain.Tools
open Yession.Session

/// The secret a connection is stored under: the one spelling each connection's own file owns.
let secretNameOf (connection: ConnectedService) : SecretName =
    match connection with
    | ConnectedService.GitHub -> GitHubConnection.secretName
    | ConnectedService.Claude -> ClaudeConnection.secretName

/// The service a person's own connection is for, from the storage name this session chose for
/// it: `secretNameOf` read backwards. `None` for a name no connection uses.
let serviceOf (name: SecretName) : ConnectedService option =
    if name = GitHubConnection.secretName then Some ConnectedService.GitHub
    elif name = ClaudeConnection.secretName then Some ConnectedService.Claude
    else None

/// The person's own connection of this service as the statuses read it from this session
/// (`OwnConnection.ofStatuses`, the same reading the prompt's facts are made from), if they
/// have one.
let private ownConnection
    (heard: Map<ConnectionId, ConnectionStatus>)
    (person: UserId)
    (service: ConnectedService)
    : OwnConnection option =
    OwnConnection.ofStatuses serviceOf (Principal.User person) (heard |> Map.toList |> List.map snd)
    |> List.tryFind (fun own -> own.Service = service)

/// A person's own connection: the only kind that takes an offline grant.
let ownTarget (person: UserId) (connection: ConnectedService) : ConnectionId =
    { Scope = UserScope person; Name = secretNameOf connection }

/// The credential a turn for `actor` would spend on this connection: the first of that
/// connection's turn targets the session has heard of, by the rule each connection owns.
let private spentTarget
    (sessionId: SessionId)
    (heard: Map<ConnectionId, ConnectionStatus>)
    (actor: Principal)
    (connection: ConnectedService)
    : ConnectionId option =
    let targets =
        match connection with
        | ConnectedService.GitHub -> GitHubConnection.turnTargets sessionId (CredentialFor.Person actor)
        | ConnectedService.Claude -> ClaudeConnection.turnTargets sessionId (CredentialFor.Person actor)
    targets |> List.filter (fun target -> Map.containsKey target heard) |> List.tryHead

/// Whom to ask, or the answer the tool gives at once, with no card and nothing on the log.
///
/// - The turn would spend the session's own credential, or the deployment's: it is nobody's
///   own, and it works with nobody signed in already.
/// - The person has no connection of their own of this kind: there is nothing to grant.
/// - It is already allowed here.
/// - The agent already asked them, and they have not answered.
let decide
    (sessionId: SessionId)
    (heard: Map<ConnectionId, ConnectionStatus>)
    (asked: OfflineAsks.Open)
    (actor: Principal)
    (connection: ConnectedService)
    : Result<UserId, string> =
    let name = ConnectedService.name connection
    match spentTarget sessionId heard actor connection with
    | Some target when (match target.Scope with
                        | SessionScope _ | LocalScope -> true
                        | UserScope _ -> false) ->
        let whose =
            match target.Scope with
            | SessionScope _ -> "this session's own"
            | _ -> "this deployment's"
        Error (
            sprintf
                "Not asked. This session uses %s %s connection, not a person's. It already works when nobody is signed in. You do not need to ask."
                whose
                name)
    | _ ->
        match actor with
        | Principal.Peer _ ->
            Error (
                sprintf
                    "Not asked. The person whose turn this is is not signed in as a user, so they have no %s connection of their own to allow."
                    name)
        | Principal.User person ->
            match ownConnection heard person connection with
            | None ->
                Error (
                    sprintf
                        "Not asked. %s has no %s connection of their own in this session. There is nothing to allow. They can connect one in the session settings."
                        (UserId.value person)
                        name)
            | Some own when own.Offline <> OfflineChoice.Off ->
                Error (
                    sprintf
                        "Not asked. %s already allows this session to use their %s while they are away."
                        (UserId.value person)
                        name)
            | Some _ ->
                match OfflineAsks.tryFind person connection asked with
                | Some _ ->
                    Error (
                        sprintf
                            "Not asked again. You already asked %s about their %s. The card waits for their answer. You get a new turn when they answer or dismiss it."
                            (UserId.value person)
                            name)
                | None -> Ok person

/// The settlements a status frame makes due: each open ask whose connection now reads as
/// allowed from this session, with what it allows. Covers a grant from the card and one from
/// the settings alike, because both arrive the same way.
let settlements
    (heard: Map<ConnectionId, ConnectionStatus>)
    (asked: OfflineAsks.Open)
    (mint: unit -> MessageId)
    : OfflineAccessSettled list =
    OfflineAsks.asks asked
    |> List.choose (fun ask ->
        ownConnection heard ask.Person ask.Connection
        |> Option.bind (fun own -> OfflineAsks.allowanceOf own.Offline)
        |> Option.map (fun allowance ->
            { OfflineAccessSettled.MessageId = mint ()
              OfflineAccessSettled.Person = ask.Person
              OfflineAccessSettled.Connection = ask.Connection
              OfflineAccessSettled.Outcome = OfflineAccessOutcome.Allowed allowance }))

/// What a person's dismissal records, or why there is nothing for them to dismiss. Only the
/// person asked can: the ask is found by who sent the command.
let dismissal
    (asked: OfflineAsks.Open)
    (messageId: MessageId)
    (by: Principal)
    (connection: ConnectedService)
    : Result<OfflineAccessSettled, string> =
    let none = sprintf "there is no open ask to use your %s while you are away" (ConnectedService.name connection)
    match by with
    | Principal.Peer _ -> Error none
    | Principal.User person ->
        match OfflineAsks.tryFind person connection asked with
        | None -> Error none
        | Some _ ->
            Ok
                { OfflineAccessSettled.MessageId = messageId
                  OfflineAccessSettled.Person = person
                  OfflineAccessSettled.Connection = connection
                  OfflineAccessSettled.Outcome = OfflineAccessOutcome.Dismissed }

[<Literal>]
let Name = AgentTools.ToolName.AskOfflineAccess

/// Simplified Technical English, like every tool description here.
let description =
    "Ask the person whose turn this is to let this session use their own connection while they are away. Give the connection, \"github\" or \"claude\", and why you need it. Use it before work that must continue after the person leaves, for example a pull request to watch or a long task. The person sees your reason on a card, and only they can answer it. Everyone sees the ask in the timeline. You cannot grant access yourself. You get a new turn when the person allows it or dismisses the card. Do not wait for the answer in this turn. The answer comes back at once, and nobody is asked, when: the person has no connection of their own of that kind; it is already allowed in this session; you already asked and they did not answer yet; or this session uses a connection that is not a person's, which works without them."

/// The tool, over what the session has heard and the log. `turnActor` is whose turn this is,
/// bound by the dispatcher: the agent cannot name somebody else to ask.
let tool
    (sessionId: SessionId)
    (heard: unit -> Map<ConnectionId, ConnectionStatus>)
    (log: unit -> EventLog<SessionEvent> option)
    (mint: unit -> MessageId)
    (turnActor: Principal)
    : ToolDescriptor * (string -> Async<Result<ToolAnswer, string>>) =
    AgentTools.tool
        Name
        description
        (toolArgs {
            let! connection = ToolArgs.text "connection" "\"github\" or \"claude\""
            and! why =
                ToolArgs.text
                    "why"
                    "why you need it, in one or two sentences the person reads on the card, e.g. \"to watch octo/hello#12 and merge it when the checks pass\""
            return
                match ConnectedService.ofWord connection, why.Trim () with
                | None, _ -> Error (sprintf "connection must be \"github\" or \"claude\", not \"%s\"" connection)
                | Some _, "" -> Error "give a reason in why. The person reads it on the card"
                | Some connection, why -> Ok (connection, why)
         })
        (AgentTools.whenValid (fun (connection, why) ->
            async {
                let said =
                    async {
                        match log () with
                        | None -> return "Not asked. This session cannot record an ask yet."
                        | Some log ->
                            let! page = log.Read None System.Int32.MaxValue
                            let asked = OfflineAsks.apply OfflineAsks.empty (page.Events |> List.map (fun e -> e.Event))
                            match decide sessionId (heard ()) asked turnActor connection with
                            | Error answer -> return answer
                            | Ok person ->
                                let! _ =
                                    log.Append
                                        ActorRef.Agent
                                        (SessionEvent.OfflineAccessAsked
                                            { OfflineAccessAsked.MessageId = mint ()
                                              OfflineAccessAsked.Person = person
                                              OfflineAccessAsked.Connection = connection
                                              OfflineAccessAsked.Why = why })
                                return
                                    sprintf
                                        "Asked %s. They see a card that asks to let this session use their %s while they are away. You get a new turn when they answer or dismiss it."
                                        (UserId.value person)
                                        (ConnectedService.name connection)
                    }
                let! said = said
                return Ok (ToolAnswer.text said)
            }))

/// Read the log's open asks.
let private openIn (log: EventLog<SessionEvent>) : Async<OfflineAsks.Open> =
    async {
        let! page = log.Read None System.Int32.MaxValue
        return OfflineAsks.apply OfflineAsks.empty (page.Events |> List.map (fun e -> e.Event))
    }

/// Settle every open ask the statuses now allow, and say whether any was: the caller wakes
/// the agent when one was.
let settle (log: EventLog<SessionEvent>) (heard: Map<ConnectionId, ConnectionStatus>) (mint: unit -> MessageId) : Async<bool> =
    async {
        let! asked = openIn log
        let due = settlements heard asked mint
        for settled in due do
            let! _ = log.Append (UserRef settled.Person) (SessionEvent.OfflineAccessSettled settled)
            ()
        return not (List.isEmpty due)
    }

/// A person dismissed the ask: record it under them. The caller wakes the agent.
let dismiss
    (log: EventLog<SessionEvent>)
    (mint: unit -> MessageId)
    (by: Principal)
    (connection: ConnectedService)
    : Async<Result<unit, string>> =
    async {
        let! asked = openIn log
        match dismissal asked (mint ()) by connection with
        | Error reason -> return Error reason
        | Ok settled ->
            let! _ = log.Append (Principal.toActor by) (SessionEvent.OfflineAccessSettled settled)
            return Ok ()
    }
