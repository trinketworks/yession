namespace Yession.Domain.Sandboxes

open Yession.Domain

/// The facts a sandbox records — an environment identified, started or stopped, a work sandbox's life, and the shell profile set on it.
///
/// These sit BELOW `SessionEvent`, because the union names them, while the
/// projections that fold that union sit above it — so Sandboxes spans the event
/// spine rather than living on one side of it.
[<RequireQualifiedAccess>]
type EnvironmentNeedIdentified =
    { Reason : string
      AgentTurnId : AgentTurnId option }

and EnvironmentStartRequested =
    { EnvironmentId : string
      SpecSummary : string }

and EnvironmentStarted =
    { EnvironmentId : string
      ContainerRef : string }

and EnvironmentStartFailed =
    { EnvironmentId : string
      Reason : string }

and [<RequireQualifiedAccess>] EnvironmentStopRequested =
    { EnvironmentId : string }

and [<RequireQualifiedAccess>] EnvironmentStopped =
    { EnvironmentId : string }

/// What a repo's file asked to have run to make one of its sandboxes ready, and what became
/// of the asking (`setup:`).
///
/// Its own fact rather than a field on the start, because it happens after one: the sandbox is
/// up before there is anywhere to queue this. And a fact at all because nobody in the session
/// asked for it — the block appears in a terminal they will find busy, holding it until it
/// finishes, and a turn that meets that with no explanation spends calls establishing what it
/// is. Measured: three, on a session that had everything else right.
and SandboxSetupQueued =
    { MessageId : MessageId
      Sandbox : SandboxRef
      /// As written in the file. The block carries it too; this is what a reader is told
      /// without going to look for the block.
      Command : string
      /// The handle it can be picked up by — the same `check_pending` takes, which is what
      /// makes this actionable rather than merely honest. Absent only when the queueing
      /// itself failed, and then `Problem` says why: exactly one of the two is present.
      Handle : QueueId option
      Problem : string option
      Actor : ActorRef }

/// A sandbox is COMING UP: emitted the moment the work to start it begins, before the
/// container exists and the checkout is in place — the half of the story `WorkSandboxStarted`
/// used to leave untold. It carries the SAME `MessageId` the matching start (or failure)
/// will, so the timeline opens one running item here and resolves that same item when the
/// sandbox is up or could not come up, rather than a second line appearing beside it. That
/// is the same in-place lifecycle an agent message has (`AgentMessageStarted` → `Completed`),
/// and the reason an act can now be a task with a running state at all.
and WorkSandboxStarting =
    { MessageId : MessageId
      Sandbox : SandboxRef
      /// The backend it is coming up on, so the running line already says what confinement it
      /// will get rather than waiting for the start to say it.
      Backend : string
      /// What the declaration said this sandbox is FOR, when it said anything — carried for
      /// parity with the start it resolves into, so the running line and the started line read
      /// the same.
      Description : string option
      Actor : ActorRef }

/// A sandbox that began coming up (`WorkSandboxStarting`) could NOT — the container failed to
/// come up, or failed its own checks. Carries the starting item's `MessageId`, so the running
/// line it opened resolves to a failure in place rather than spinning forever. The fold that
/// re-reads a repo's declarations (`RepoSandboxes`) recognises this as the account of the
/// failure, so it does not also file a `RepoConfigRefused` saying the same thing twice.
and WorkSandboxStartFailed =
    { MessageId : MessageId
      Sandbox : SandboxRef
      /// Why it could not come up — the same sentence the start attempt returned.
      Reason : string
      Actor : ActorRef }

and WorkSandboxStarted =
    { MessageId : MessageId
      /// Which sandbox, scope included. The wire form is `SandboxRef.render`, and it is
      /// backward compatible BY CONSTRUCTION rather than by a migration: a session-owned ref
      /// renders to the bare name every log already holds, and `parse` reads a bare name back
      /// as session-owned. A log written before repos could declare sandboxes genuinely had
      /// only session-owned ones, so that is not a guess.
      Sandbox : SandboxRef
      /// Which backend it came up on, so the record says what confinement it actually
      /// got rather than what the operator configured at some point.
      Backend : string
      /// What the declaration said this sandbox is FOR, when it said anything. On the START
      /// rather than looked up when a note is drawn, because a log is read long after the
      /// file that described it has changed, and a timeline that re-reads today's prose onto
      /// last week's event is a timeline that quietly rewrites itself.
      Description : string option
      /// Where the repo's checkout is AS THIS SANDBOX SEES IT, for a sandbox its repo
      /// declared. `None` for a session's own, which has no repo to hold one.
      ///
      /// One checkout has two addresses (`CheckoutViews`) and which one is right depends on
      /// where you are standing: the host's path under srt, `/repos/…` inside a container.
      /// Only a sandbox settles that, which is why the answer is on the START and not on
      /// `add_repo` — measured, an agent told the host path by `add_repo` pointed a shell
      /// profile at it inside the container, watched it silently not take, and spent six
      /// calls working out why.
      Checkout : string option
      /// The connections forwarded into it, by NAME — never a value, and never a token
      /// shape that could be mistaken for one. Forwarding is a fact about the sandbox
      /// that outlives the turn that asked for it, so the log has to carry it; what the
      /// credential IS belongs only in the sandbox's env. WHOSE is not a fact about the
      /// sandbox at all: a forward is a route, and each block's request spends the
      /// credential of the act that made it (`GitCredentialSpent`).
      Forwarded : ConnectionName list
      /// Where this host could not give exactly what the sandbox's resources named, one line
      /// each. Empty is the ordinary case and says nothing.
      ///
      /// Recorded on the START and not left to the `work_sandboxes` panel, because the panel
      /// answers what is running NOW and this is a fact about a sandbox somebody's work then
      /// ran inside. A sandbox stopped an hour ago still widened what it held, and the log is
      /// where that is still true.
      ///
      /// Rendered lines rather than the leaves they came from, like `Forwarded` beside it:
      /// what a leaf MEANS is the operator's vocabulary at the time, and a log that outlived
      /// that vocabulary would be re-reading old grants through a profile that has moved.
      Realisation : string list
      Actor : ActorRef }

and WorkSandboxStopped =
    { MessageId : MessageId
      Sandbox : SandboxRef
      Actor : ActorRef }

and ShellProfileSet =
    { MessageId : MessageId
      /// Which sandbox's shells this is about. A path is only a path inside the filesystem
      /// that has it, so the profile is per sandbox rather than per session — and a repo's
      /// sandbox is a sandbox, so this addresses one the same way everything else does.
      Sandbox : SandboxRef
      /// Where a shell opened in that sandbox starts. `None` is the CLEAR — back to the
      /// sandbox's own default, which is what every terminal did before this plan.
      WorkingDirectory : string option
      Actor : ActorRef }

/// The prose a start writes into the timeline - the headline a screen lands on, and the
/// particulars beneath it. It lives HERE, beside the event, for the same reason
/// `PrTransition.describe` sits beside `PrTransitioned`: what this event's leaves MEAN is
/// knowledge that belongs with the event, not assembled by whatever folds it. `Act.phrase`
/// and `Act.particulars` (Acts.fs) dispatch here; the fold composes nothing.
///
/// A screen wants the two halves apart - a gist to land on, particulars beneath - so they
/// are two functions rather than one. Every other reader (the agent's prompt above all)
/// joins them, which `ConversationItem.said` does with an em-dash and semicolons; neither
/// reader parses the other's prose, because both build from these same fields.
[<RequireQualifiedAccess>]
module WorkSandboxStarted =

    /// The one thing worth deciding from at a glance: which sandbox, on what backend.
    let phrase (s: WorkSandboxStarted) : Phrase =
        Phrase.text (sprintf "started sandbox %s (%s)" (SandboxRef.render s.Sandbox) s.Backend)

    /// Everything the headline holds back, each fact its own phrase: what the sandbox is
    /// FOR, where its checkout sits, whose credential rode in, and where this host could
    /// not give exactly what was asked. Empty when the start is already one clause -
    /// nothing declared, nothing forwarded, nothing rescoped - because a seam printed over
    /// a single clause stands for content that is not there.
    let particulars (s: WorkSandboxStarted) : Phrase list =
        // Each connection a REFERENCE: the prose reader spells it by name, a screen draws it
        // as that connection is drawn everywhere else — the same GitHub the sidebar's panel
        // is about, not a bare word that happens to match.
        let forwarded =
            match s.Forwarded with
            | [] -> None
            | names ->
                Some (
                    Segment.Text "forwarding "
                    :: (names
                        |> List.mapi (fun i name ->
                            let reference = Segment.Ref (EntityRef.Connection name)
                            if i = 0 then [ reference ] else [ Segment.Text ", "; reference ])
                        |> List.concat))
        // Where this host could not give what the sandbox's resources named. On the start
        // NOTE rather than a note of its own, because it is a property of THIS sandbox coming
        // up - a separate item would be a second thing to correlate, and the correlation is
        // the whole content of it.
        let realisation =
            match s.Realisation with
            | [] -> None
            | lines ->
                Some (
                    sprintf
                        "where this host could not give exactly what was asked: %s"
                        (String.concat "; " lines))
        let checkout = s.Checkout |> Option.map (sprintf "the checkout is at %s in here")
        // What it is for, where its checkout sits, whose credential rode in, and what this
        // host could not give exactly are separate facts, not clauses chained onto the
        // headline - each is its own phrase, so each stays its own fact.
        List.choose id
            [ s.Description |> Option.map Phrase.text
              checkout |> Option.map Phrase.text
              forwarded
              realisation |> Option.map Phrase.text ]

module WorkSandboxStarting =

    /// Short headline, like the start it resolves into: which sandbox, on what backend.
    /// What it is for rides the particulars, not the headline.
    let phrase (s: WorkSandboxStarting) : Phrase =
        Phrase.text (sprintf "starting sandbox %s (%s)" (SandboxRef.render s.Sandbox) s.Backend)

    let particulars (s: WorkSandboxStarting) : Phrase list = s.Description |> Option.map Phrase.text |> Option.toList

module WorkSandboxStartFailed =

    let phrase (s: WorkSandboxStartFailed) : Phrase =
        Phrase.text (sprintf "sandbox %s could not start" (SandboxRef.render s.Sandbox))

    let particulars (s: WorkSandboxStartFailed) : Phrase list = [ Phrase.text s.Reason ]

module WorkSandboxStopped =

    let phrase (s: WorkSandboxStopped) : Phrase =
        Phrase.text (sprintf "stopped sandbox %s" (SandboxRef.render s.Sandbox))

module ShellProfileSet =

    let phrase (p: ShellProfileSet) : Phrase =
        match p.WorkingDirectory with
        | Some cwd -> Phrase.text (sprintf "new terminals in %s start in %s" (SandboxRef.render p.Sandbox) cwd)
        | None ->
            Phrase.text (sprintf "new terminals in %s start where the sandbox puts them" (SandboxRef.render p.Sandbox))

module SandboxSetupQueued =

    let phrase (q: SandboxSetupQueued) : Phrase =
        match q.Problem with
        | Some _ -> Phrase.text (sprintf "%s could not start its setup" (SandboxRef.render q.Sandbox))
        | None -> Phrase.text (sprintf "%s is running its setup: %s" (SandboxRef.render q.Sandbox) q.Command)

    /// The handle, which is what makes this actionable rather than merely honest — the
    /// agent is told both, and a screen shows the headline with the mechanics beside it.
    let particulars (q: SandboxSetupQueued) : Phrase list =
        match q.Handle, q.Problem with
        | Some handle, _ ->
            [ Phrase.text (
                  sprintf
                      "it holds that terminal until it finishes; check_pending with handle '%s' for the outcome"
                      (QueueId.value handle)) ]
        | None, Some problem -> [ Phrase.text problem ]
        | None, None -> []
