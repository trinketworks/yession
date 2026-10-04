namespace Yession.Session

open Yession.Domain
open Yession.Domain.Link
open Yession.Domain.Terminals
open Yession.Domain.Collab

/// Handling of `SessionCommand` requests on the Session. Commands are how
/// clients ask for durable facts the CRDT cannot express: that is the agent-turn
/// interrupt alone — draft creation and sending are pure CRDT writes.
module SessionCommands =

    /// Handle one command from an accepted peer.
    ///
    /// Every authority is injected as a function, so this stays a pure routing decision the
    /// tests can drive without a scheduler, an environment, or a sandbox:
    ///   * `requestInterrupt` validates the turn is the one currently running (the
    ///     interrupt-vs-completion race resolves there) and performs the cancellation;
    ///   * `openTerminal`/`closeTerminal` are the terminal manager's (Plan 13).
    ///
    /// A successful `OpenTerminal` answers `CommandAccepted` and nothing more: the new
    /// terminal's id reaches every peer as a `TerminalOpened` EVENT. Returning it in the
    /// response would give the requesting peer a second, earlier, privately-delivered
    /// source of truth about a durable fact — and then two code paths to keep agreeing.
    let handle
        (requestInterrupt: PeerId -> AgentTurnId -> Result<unit, string>)
        (openTerminal: ActorRef -> Source -> TerminalTitle -> Async<Result<TerminalId, string>>)
        (closeTerminal: TerminalId -> ActorRef -> string -> Async<Result<unit, string>>)
        (takeLease: TerminalId -> ActorRef -> Async<Result<unit, string>>)
        (releaseLease: TerminalId -> ActorRef -> Async<Result<unit, string>>)
        (rearmTerminal: TerminalId -> Async<Result<unit, string>>)
        // Ask the provider for a terminal's stream again (Plan 19, step 4). A function like
        // the rest, so a session that was given no providers simply cannot: the composition
        // decides, not a flag.
        (reattachTerminal: TerminalId -> Async<Result<TerminalId, string>>)
        // Consent to a repo's capability set (Plan 27). A function like the rest, so a
        // session composed without repos simply cannot be asked.
        (approveCapabilities: Principal -> RepoRef -> string list -> Async<Result<unit, string>>)
        // The launch surface's act. Answers ADMISSION only — the clone it starts reports
        // through the log — so a function whose `Ok` means "begun", never "done".
        (addRepo: Principal -> RepoRef -> string option -> Async<Result<unit, string>>)
        // Who a peer is. A `Principal`, because every peer is one, and the two acts above
        // are a PERSON's — consent to what a repo asks for, a repo brought in on their
        // credential — which the type then says outright rather than a check inside each.
        (principalFor: PeerId -> Principal)
        (peerId: PeerId)
        (command: SessionCommand)
        : Async<SessionCommandResult> =
        async {
            match command with
            | ApproveRepoCapabilities (repo, granted) ->
                match! approveCapabilities (principalFor peerId) repo granted with
                | Ok () -> return CommandAccepted
                | Error reason -> return CommandRejected reason
            | AddRepo (repo, branch) ->
                match! addRepo (principalFor peerId) repo branch with
                | Ok () -> return CommandAccepted
                | Error reason -> return CommandRejected reason
            | InterruptAgentTurn turnId ->
                match requestInterrupt peerId turnId with
                | Ok () -> return CommandAccepted
                | Error reason -> return CommandRejected reason
            | OpenTerminal (raw, sandbox) ->
                // The title a peer typed, parsed here at the edge it arrived on. What used
                // to be an inline trim-and-default is `TerminalTitle.create`, so every other
                // way of opening a terminal gets the same rules without remembering them —
                // and an over-long one is refused with the words the composer already shows,
                // rather than silently kept as a prefix in a durable event.
                //
                // A peer who NAMED nothing gets the name the sandbox gives it — `terminal`
                // for `default`, the sandbox itself for a named one — because the only thing
                // they said was where. A title they DID type stays exactly theirs, prefix
                // and all absent: the bracket the agent's terminals wear is prose we wrote,
                // and putting it in front of somebody's own words would make us the author
                // of a title we would then have to shorten to fit.
                let said = if isNull (box raw) then "" else raw.Trim ()
                match TerminalTitle.create said with
                | Error reason -> return CommandRejected reason
                | Ok typed ->
                let title = if said = "" then TerminalTitle.inSandbox sandbox "" else typed
                // A peer's Open is always a SHELL (Plan 16, part D): an attached source needs
                // a ticket from a provider, and a peer command carrying a URL would be a peer
                // choosing what this session connects to. That rule is intact and it is the
                // whole of it.
                //
                // WHERE is theirs now. It used to be the agent's alone, on the reading that
                // naming a sandbox is a command and commands are the agent's (Plan 15) — so a
                // person who wanted a shell in `dev` asked for one and waited a turn. But the
                // set they can name is the set this session already has: the repo's own file
                // declared it and somebody here consented to it, so choosing between them
                // takes no judgement the agent could add. `AddRepo` above admitted a second,
                // human caller for the same kind of reason, and said so.
                //
                // Not checked here: `Open` ensures the sandbox named exists and refuses an
                // unknown name with a sentence saying what there is, for every caller alike.
                // A test in this handler would be a second answer to one question, and the
                // one further from the state.
                match! openTerminal (Principal.toActor (principalFor peerId)) (SandboxShell sandbox) title with
                | Ok _ -> return CommandAccepted
                | Error reason -> return CommandRejected reason
            | CloseTerminal terminalId ->
                match! closeTerminal terminalId (Principal.toActor (principalFor peerId)) "closed by a peer" with
                | Ok () -> return CommandAccepted
                | Error reason -> return CommandRejected reason
            // Taking a lease succeeds even when someone else holds it: collaborators are
            // trusted, so this is a steal rather than a request, and what it needs is to be on
            // the record rather than to be permitted. Releasing is the asymmetric one — only
            // the holder can, because releasing someone else's lease is a steal wearing a
            // polite word, and a steal has its own verb.
            | TakeTerminalLease terminalId ->
                match! takeLease terminalId (Principal.toActor (principalFor peerId)) with
                | Ok () -> return CommandAccepted
                | Error reason -> return CommandRejected reason
            | ReleaseTerminalLease terminalId ->
                match! releaseLease terminalId (Principal.toActor (principalFor peerId)) with
                | Ok () -> return CommandAccepted
                | Error reason -> return CommandRejected reason
            // Unattributed, and deliberately: re-arming repairs a terminal rather than taking
            // anything from anyone, so who pressed it decides nothing. The event it produces
            // says the terminal is marking again, which is the fact worth having.
            | RearmTerminal terminalId ->
                match! rearmTerminal terminalId with
                | Ok () -> return CommandAccepted
                | Error reason -> return CommandRejected reason
            // Unattributed for `RearmTerminal`'s reason and then some: what it produces is a
            // `TerminalOpened` carrying whoever this session opens attached streams as, and
            // the provider's own refusal — "somebody else holds it now" — is what a rejection
            // says, because nothing this session could add would be more useful.
            | ReattachTerminal terminalId ->
                match! reattachTerminal terminalId with
                | Ok _ -> return CommandAccepted
                | Error reason -> return CommandRejected reason
        }

/// The queue drain's pure decision core (Phase 3, Step 16). The Session is the
/// queue's single consumer; `plan` computes, from a snapshot of its replica plus the
/// log-derived set of already-consumed entries, exactly what one drain does: which
/// entries become `MessageSent` events (in which order) and which doc keys to remove.
module QueueDrain =

    type DrainPlan =
        { /// Entries to consume, in `(Order, QueueId)` order — each becomes one
          /// `MessageSent` with the body snapshotted from this plan.
          Batch : QueuedMessage list
          /// Every snapshot key leaves the doc: the batch, plus entries already named
          /// by a `MessageSent` (a crash between append and removal left them behind —
          /// repaired here rather than consumed twice).
          Removals : QueueId list }

    let plan (consumed: Set<string>) (queue: Map<QueueId, QueuedMessage>) : DrainPlan =
        let snapshot = QueueOrder.sorted queue
        { Batch = snapshot |> List.filter (fun m -> not (Set.contains (QueueId.value m.QueueId) consumed))
          Removals = snapshot |> List.map (fun m -> m.QueueId) }

    /// The consumed-set contribution of one event: drains dedup against every
    /// `MessageSent` that names a queue entry.
    let consumedOf (event: SessionEvent) : string option =
        match event with
        | MessageSent m -> m.QueueId |> Option.map QueueId.value
        | _ -> None
