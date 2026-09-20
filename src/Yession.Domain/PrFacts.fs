namespace Yession.Domain.Prs

open System
open Yession.Domain

/// The facts a watched pull request records. Provider-lean like `RepoRef`: "pull
/// request" is a term every forge speaks, and nothing here names an endpoint — the
/// GitHub REST knowledge that produces these values lives in the session host
/// (`app/GitHubPrs.fs`), the way `RepoRef.cloneUrl` keeps github.com out of the types
/// that carry a repo. They sit BELOW `SessionEvent` because the union names them, and
/// the projection that folds that union (`PrWatches.fs`) sits above it.

/// One pull request, named the way `add_repo` names a repo: owner/repo plus number.
type PrRef = { Repo : RepoRef; Number : int }

module PrRef =

    /// Numbers are provider-assigned and start at 1; zero or negative is a paste mistake
    /// worth refusing before it becomes a watch that can never resolve.
    let create (repo: RepoRef) (number: int) : Result<PrRef, string> =
        if number >= 1 then Ok { Repo = repo; Number = number }
        else Error (sprintf "%d is not a pull request number" number)

    /// The canonical rendering — "owner/repo#12" — used by notes, gates and queries alike.
    let render (pr: PrRef) : string = sprintf "%s#%d" (RepoRef.value pr.Repo) pr.Number

/// What OPENING a pull request needs said, before a provider has given it a number.
/// Provider-lean like `PrRef`: every forge asks for these five, and none of them is an
/// endpoint — the REST that turns one of these into a `PrRef` is the session host's
/// (`app/GitHubPrs.fs`), the way `RepoRef.cloneUrl` keeps github.com out of the types that
/// carry a repo.
type PrDraft =
    { Repo : RepoRef
      /// The branch the work is on. A fork's head is `owner:branch`, which is the provider's
      /// own spelling for it and passes through untouched.
      Head : string
      /// The branch it is FOR.
      Base : string
      Title : string
      /// The description, when there is one. A repo that squash-merges makes this the commit
      /// body, so it is not decoration — but it is optional, and `None` rather than `""`,
      /// because a pull request with no description and one whose description is nothing are
      /// the same thing at every forge and only one of them should be representable.
      Body : string option
      /// Opened as a draft: on the record, and explicitly not asking for review yet.
      Draft : bool }

module PrDraft =

    /// A branch as a caller wrote it: trimmed, and refused when it cannot be one. Blank and
    /// whitespace are the two an agent actually produces — a computed branch name that came
    /// back empty, and a title pasted into the wrong argument — and both would otherwise
    /// reach the provider as a validation failure that cost a request to discover.
    let private branch (which: string) (raw: string) : Result<string, string> =
        let trimmed = raw.Trim ()
        if trimmed = "" then Error (sprintf "a pull request needs a %s branch" which)
        elif trimmed |> Seq.exists Char.IsWhiteSpace then
            Error (sprintf "'%s' is not a branch name — a %s branch has no spaces in it" trimmed which)
        else Ok trimmed

    /// Assemble one. The refusals here are the ones that are true of a pull request rather
    /// than of GitHub: a title nobody wrote, a branch nobody named, and a head that is its
    /// own base — which the provider would refuse too, one round trip later and in its own
    /// words.
    let create
        (repo: RepoRef)
        (head: string)
        (onto: string)
        (title: string)
        (body: string option)
        (draft: bool)
        : Result<PrDraft, string> =
        let titled = title.Trim ()
        if titled = "" then Error "a pull request needs a title"
        else
            match branch "head" head, branch "base" onto with
            | Error e, _
            | _, Error e -> Error e
            | Ok head, Ok onto ->
                if head = onto then
                    Error (sprintf "the head and the base are both %s — there would be nothing to merge" head)
                else
                    Ok
                        { Repo = repo
                          Head = head
                          Base = onto
                          Title = titled
                          // Not trimmed, unlike everything above it: a description is prose,
                          // and what looks like padding in one is a fenced code block's
                          // indentation. Whitespace is all a body HAS to be, though, so a body
                          // that is only that is no body.
                          Body = body |> Option.filter (fun said -> said.Trim () <> "")
                          Draft = draft }

    /// How a draft is named where somebody has to read it before it exists — a gate's
    /// summary, a refusal. `PrRef.render` is what names it afterwards.
    let render (draft: PrDraft) : string =
        sprintf "%s %s -> %s" (RepoRef.value draft.Repo) draft.Head draft.Base

/// How a pull request's commits land on its base. Every forge offers these three; which of
/// them a repository ALLOWS is the provider's to say, and a method it does not allow comes
/// back as its refusal rather than being guessed at here.
type PrMergeMethod =
    | Squash
    | MergeCommit
    | Rebase

module PrMergeMethod =

    /// The word a caller writes. `squash` is the default everywhere one is taken, because
    /// a pull request whose description was written as the commit body it becomes
    /// (`PrDraft.Body`) is a pull request meant to squash.
    let create (raw: string) : Result<PrMergeMethod, string> =
        match raw.Trim().ToLowerInvariant () with
        | "squash" -> Ok Squash
        | "merge" -> Ok MergeCommit
        | "rebase" -> Ok Rebase
        | other -> Error (sprintf "'%s' is not a merge method — squash, merge or rebase" other)

    let render (method: PrMergeMethod) : string =
        match method with
        | Squash -> "squash"
        | MergeCommit -> "merge"
        | Rebase -> "rebase"

type PrState =
    | PrOpen
    | PrMerged
    | PrClosed

module PrState =
    let describe (state: PrState) : string =
        match state with
        | PrOpen -> "open"
        | PrMerged -> "merged"
        | PrClosed -> "closed"

/// The checks rollup on the head commit. `ChecksNone` is its own case rather than a
/// pending that never resolves: a commit with zero check runs is common (no CI
/// configured, or none triggered), and "pending forever" would be a lie about it.
type ChecksRollup =
    | ChecksNone
    | ChecksPending
    | ChecksGreen
    | ChecksRed

module ChecksRollup =
    let describe (rollup: ChecksRollup) : string =
        match rollup with
        | ChecksNone -> "no checks"
        | ChecksPending -> "checks pending"
        | ChecksGreen -> "checks green"
        | ChecksRed -> "checks red"

/// What one look at the provider answered. `Mergeable` is a THREE-valued fact and its
/// third value is why it is handled with care: GitHub computes it lazily and answers
/// `None` until it has, so `None` is "not known yet", never "mergeable". Only a COMPUTED
/// value moves anything — `Some false` announces `Conflicted`, `Some true` clears it — and
/// `None` holds the baseline where it was, so the window between a push and the provider
/// recomputing raises no false alarm. `PrTransitions.detect` is where that rule lives.
type PrSnapshot =
    { State : PrState
      Title : string
      HeadSha : string
      Checks : ChecksRollup
      /// Is this pull request on its way in without anybody further being needed — auto
      /// merge armed, the merge queue holding it? Unlike `Mergeable` this one IS a fact
      /// the provider states outright rather than computes lazily, so it is announced.
      Queued : bool
      Mergeable : bool option }

module PrSnapshot =

    /// What a computed conflict adds to a one-line DESCRIPTION of a pull request — the watch
    /// report, the timeline note. ", conflicted" on an OPEN pull request the provider has
    /// computed unmergeable, nothing otherwise: a merged one's mergeability is moot, and
    /// `None` is not-yet-computed rather than clean, the same three-valued care the
    /// transition takes. Taken as state + a mergeability, not a whole snapshot, so the folded
    /// baseline (`PrKnown`) can say a conflict the same way a fresh snapshot does. Lives here,
    /// below `SessionEvent` and above the projection, because `Conversation.fs` reads it too
    /// and is compiled before `PrWatches.fs` where the status WORD lives.
    let conflictClause (state: PrState) (mergeable: bool option) : string =
        match state, mergeable with
        | PrOpen, Some false -> ", conflicted"
        | _ -> ""

/// A watched pull request's state changes — the vocabulary grows HERE, not inside
/// `SessionEvent`, which carries one `PrTransitioned` case whatever is announced.
[<RequireQualifiedAccess>]
type PrTransition =
    | Merged
    | Closed
    | Reopened
    | ChecksPassed
    | ChecksFailed
    /// Auto merge armed: from here it lands without anybody doing anything.
    | Queued
    /// Auto merge armed and no longer armed, on a pull request still open. What a merge
    /// queue does when it ejects an entry, and the reason this case exists: the ejection
    /// itself raises nothing anywhere — the state does not move, the checks do not move,
    /// the pull request simply stops being on its way in. Somebody has to re-arm it, and
    /// until this was said nobody was told.
    | Stalled
    /// The base moved under the branch and the two no longer merge — a conflict the
    /// provider has now COMPUTED (`mergeable = false`), not the `null` it answers while it
    /// is still working the merge out. Unlike a stall this is not a call for a person: the
    /// agent whose branch it is rebases, resolves and force-pushes it, the same way it
    /// answers `ChecksFailed`. The reason it earns a transition at all is that nothing else
    /// announces it — a queued pull request that develops a conflict simply stops merging,
    /// its checks last green, and until this was said the watcher armed auto-merge and
    /// waited for a landing that could never come.
    | Conflicted
    /// A computed conflict cleared: `mergeable = false` back to `true`. The other side of
    /// `Conflicted`, so a watch that announced the conflict can say when the work that
    /// answered it took — and a re-arm is worth it again.
    | Resolved

module PrTransition =
    let describe (transition: PrTransition) : string =
        match transition with
        | PrTransition.Merged -> "merged"
        | PrTransition.Closed -> "closed"
        | PrTransition.Reopened -> "reopened"
        | PrTransition.ChecksPassed -> "checks passed"
        | PrTransition.ChecksFailed -> "checks failed"
        | PrTransition.Queued -> "queued"
        | PrTransition.Stalled -> "stalled"
        | PrTransition.Conflicted -> "conflicted"
        | PrTransition.Resolved -> "conflict resolved"

// --- event payloads (the RepoFacts shape: MessageId + payload + attribution) -----------

/// A party started watching a pull request.
///
/// Private, with `PrWatched.create` the only way to build one, because the record holds a
/// derived fact beside the fact it is derived from: WHOSE watch this is comes from the
/// authority it was started on, and a watch on nobody's credential — the deployment's own,
/// which a repo file's boot fold acts on — is not a watch, because there would be nobody to
/// keep looking as and nobody to wake. Two public fields let a caller write them apart; one
/// constructor asks the question once, and the readers below get the answer it gave.
///
/// The watcher used to be whoever appended the event. For a watch the agent started that
/// was the agent, so the polls ran on nobody's credential and the wake the merge caused
/// dispatched a turn as the agent, which failed saying "sign in". A `Principal` cannot be
/// the agent, and a constructor cannot be skipped — which between them is what closes it.
type PrWatched =
    private
        { /// The timeline note's identity, minted by the Process at append time.
          PwMessageId : MessageId
          PwPr : PrRef
          /// The state at the moment the watch began — the durable BASELINE transition
          /// detection compares against. Folded from the log (`PrWatches.fs`), this is what
          /// makes a restart re-announce nothing and a merge that happened while the process
          /// was down still get announced: the log says what was last known, not memory.
          PwInitial : PrSnapshot
          /// Who asked, and on whose authority: the note's attribution is the author (the
          /// agent, when it was the agent's `watch_pr`); the credential is what the watcher
          /// below was read off.
          PwAuthority : Authority
          /// Whose watch it is: the credential every later poll resolves on behalf of, and the
          /// principal any wake this watch causes runs as. The turn human's when the agent
          /// asked — the same split `RepoCaller` makes, and for the same reason: the agent
          /// acts, and has no credential of its own.
          PwWatcher : Principal }

module PrWatched =

    /// Whose watch an authority would start, or why it cannot start one. The rule, stated
    /// once: `create` applies it, and the verb that has to refuse BEFORE it has a snapshot
    /// to create with asks it directly.
    let watcherOf (pr: PrRef) (authority: Authority) : Result<Principal, string> =
        match Authority.credential authority with
        | CredentialFor.Person watcher -> Ok watcher
        | CredentialFor.Deployment ->
            Error (
                sprintf
                    "%s cannot be watched on the deployment's own credential — a watch keeps looking as somebody, and wakes them"
                    (PrRef.render pr))

    let create (messageId: MessageId) (authority: Authority) (pr: PrRef) (initial: PrSnapshot) : Result<PrWatched, string> =
        watcherOf pr authority
        |> Result.map (fun watcher ->
            { PwMessageId = messageId
              PwPr = pr
              PwInitial = initial
              PwAuthority = authority
              PwWatcher = watcher })

    let messageId (p: PrWatched) : MessageId = p.PwMessageId
    let pr (p: PrWatched) : PrRef = p.PwPr
    let initial (p: PrWatched) : PrSnapshot = p.PwInitial
    let authority (p: PrWatched) : Authority = p.PwAuthority
    /// The note's attribution: who asked.
    let actor (p: PrWatched) : ActorRef = Authority.author p.PwAuthority
    let watcher (p: PrWatched) : Principal = p.PwWatcher

    /// What the watch SAYS on the timeline (see RepoFacts.fs for why prose lives beside
    /// the event).
    let phrase (p: PrWatched) : Phrase = Phrase.text (sprintf "PR %s watched" (PrRef.render p.PwPr))

    /// Where the waiting began, and what it began from: the state at the moment the watch
    /// started, said whole so a reader knows what the first transition will be from.
    let particulars (p: PrWatched) : Phrase list =
        [ Phrase.text (
              sprintf
                  "%s, %s%s"
                  (PrState.describe p.PwInitial.State)
                  (ChecksRollup.describe p.PwInitial.Checks)
                  (PrSnapshot.conflictClause p.PwInitial.State p.PwInitial.Mergeable)) ]

type PrUnwatched =
    { MessageId : MessageId
      Pr : PrRef
      Actor : ActorRef }

/// The session OBSERVED a watched pull request change. Appended by the Process under
/// `ActorRef.System` — nobody in the session did it — while the payload names whose
/// watch noticed, because the projection reads events, not envelopes, and "whose news
/// is this" is the fact attribution and credentials both hang off.
type PrTransitioned =
    { MessageId : MessageId
      Pr : PrRef
      Transition : PrTransition
      /// The state after the change, carried so a note can say it without a query —
      /// and so the fold can advance its baseline from the event alone.
      State : PrState
      Checks : ChecksRollup
      /// `PrWatched.watcher`, carried forward: whose credential noticed, and who the turn
      /// this wakes runs as.
      Watcher : Principal }

// --- What each pull-request act SAYS (see RepoFacts.fs for why prose lives beside the event) ---

module PrUnwatched =

    let phrase (p: PrUnwatched) : Phrase = Phrase.text (sprintf "PR %s unwatched" (PrRef.render p.Pr))

module PrTransitioned =

    let phrase (p: PrTransitioned) : Phrase =
        Phrase.text (sprintf "PR %s %s" (PrRef.render p.Pr) (PrTransition.describe p.Transition))
