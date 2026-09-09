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
      /// The credential NAMES forwarded into it — never a value, and never a token
      /// shape that could be mistaken for one. Forwarding is a fact about the sandbox
      /// that outlives the turn that asked for it, so the log has to carry it; what the
      /// credential IS belongs only in the sandbox's env.
      Forwarded : string list
      /// Whose credentials were forwarded. Distinct from `Actor` on purpose: for an
      /// agent-issued start the AGENT is the acting party while the credentials are the
      /// turn human's (Plan 08 — no borrowing, and the agent has no scope of its own).
      /// `None` when nothing was forwarded, because then nobody's were.
      CredentialOwner : ActorRef option
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
