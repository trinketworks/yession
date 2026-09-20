namespace Yession.Domain.Repos

open Yession.Domain

/// The facts a repo records. They sit BELOW `SessionEvent` because the union names them,
/// and the projections that fold that union sit above it — so a feature spans the event
/// spine rather than living on one side of it. What this buys is that `Repo` and `Branch`
/// stop being field labels in the namespace every file opens.
type RepoAdded =
    { /// The timeline note's identity, minted by the Process at append time — which is
      /// what lets the conversation projection fold this without inventing ids.
      MessageId : MessageId
      Repo : RepoRef
      /// The branch the clone landed on (the remote's default).
      Branch : string
      /// Who brought it in — the panel's human or the agent. Carried on the payload
      /// because the projection reads events, not envelopes, and "who added this repo"
      /// is the fact the shared-trust disclosure hangs off.
      Actor : ActorRef
      /// The checkout's own root `AGENTS.md`, read once at the moment the clone landed
      /// (the one moment its content is trustworthy -- `app/Repos.fs`, right after the
      /// staging rename). `None` when the file is absent or unreadable; an unreadable
      /// optional file is not a reason to fail an otherwise-successful clone. Carried on
      /// the fact itself, the same way `Branch` is, so a repo's notes replay from the log
      /// like everything else this system remembers -- read again only when the repo is
      /// re-added, which is this system's cache-with-explicit-invalidation, never a
      /// per-turn re-read.
      AgentsMd : string option }

and RepoRemoved =
    { MessageId : MessageId
      Repo : RepoRef
      Actor : ActorRef }

and RepoBranchSwitched =
    { MessageId : MessageId
      Repo : RepoRef
      Branch : string
      /// True when the switch created the branch (`-b`), false when it checked out an
      /// existing one — one event, because it is one act at the panel and one verb.
      Created : bool
      Actor : ActorRef }

/// A repo's `yession.yaml` asked this session for something, and the session did not do it.
///
/// The `repo_config` query already answers "what became of every declaration" — but only to
/// somebody who thought to ask, and a person who has just broken their own file has no
/// reason to suspect there is a question. A start that SUCCEEDS has always announced itself
/// (`WorkSandboxStarted`); this is the missing half, so that the two outcomes of a
/// declaration are visible in the same place rather than one on the timeline and one behind
/// a query.
///
/// Recorded on CHANGE and never per fold. The fold re-runs after every repo verb, so a note
/// per outcome would rebuild exactly the accumulation `SessionEnvironment` had to stop —
/// which is why the query was chosen over notes in the first place, and why this is a delta
/// rather than a reversal of that choice.
/// What a repo's file asks this session for, in the words a person would be shown, recorded
/// the first time it says something new.
///
/// Recorded on CHANGE and never per fold — the fold re-runs after every repo verb. What
/// makes a change worth saying is that a checkout's capability set is authored by whoever can
/// push to it: a `uses:` line added in a pull request takes effect the next time anybody
/// touches a repo, and until now it did so without anybody being told.
///
/// The GRANTS are carried rather than a digest of them, and that is deliberate. A hash would
/// be smaller and would make the log unreadable — a person auditing this cannot tell what
/// `a4f2…` meant — and a short one could be collided by whoever authors the file, which is
/// exactly the party this exists to watch. Carrying the list costs a few lines and cannot be
/// forged into looking unchanged.
and [<RequireQualifiedAccess>] RepoCapabilitiesChanged =
    { MessageId : MessageId
      Repo : RepoRef
      /// Everything this repo's sandboxes would hold, flattened, deduplicated and sorted —
      /// the same rendering the operator's `resources` surface uses, so what is said here and
      /// what is read there cannot become two answers.
      Granted : string list
      /// Whether this set is one somebody has to decide about.
      ///
      /// Carried rather than re-derived by every reader, because deriving it needs the
      /// operator's profile and a client has none — and a client that guessed by looking for
      /// a word in `Granted` would be a client whose prompt depends on the wording of a
      /// sentence, which is a design and will move.
      Sensitive : bool
      /// The repo's file, as the party asking.
      Actor : ActorRef }

/// A person consented to what a repo asks for.
///
/// The APPROVER is on the record and must be an attributed human. The agent cannot consent
/// on a repo's behalf, and that is the whole point of the repo being a separate principal:
/// a checkout that could approve itself is a checkout nobody is deciding about.
and [<RequireQualifiedAccess>] RepoCapabilitiesApproved =
    { MessageId : MessageId
      Repo : RepoRef
      /// Exactly what was consented to, so a later ask that differs is a new decision rather
      /// than something the old yes silently covers.
      Granted : string list
      /// Who said yes. Never `Configured` and never the agent.
      Actor : ActorRef }

and RepoConfigRefused =
    { MessageId : MessageId
      Repo : RepoRef
      /// Which declaration. `None` is the FILE itself — it could not be read at all, so
      /// there is no sandbox to name, and the fix is in the YAML rather than in what it
      /// asked for.
      Sandbox : SandboxRef option
      /// Said whole, in the words the refusal already used. A note that summarised would be
      /// a second copy of a sentence the query is also showing, free to disagree with it.
      Reason : string
      /// The repo's file, as the party that asked (`ActorRef.Configured`) — the same
      /// attribution its successful starts carry.
      Actor : ActorRef }

// --- What each repo act SAYS ----------------------------------------------------------------
// The sentence an event writes into the timeline lives beside the event, for the reason
// `WorkSandboxStarted.phrase` does: what an event's leaves MEAN is knowledge that belongs
// with the event, not assembled by whatever folds it. `Act.phrase` (Acts.fs) dispatches here;
// the fold composes nothing. A `Phrase` rather than a string, so a reader that is a screen
// can draw what the sentence points at: the repository is a REFERENCE in each, which prose
// spells `github:owner/repo` (`EntityRef.said`) and a screen draws with its mark, as a link.

module RepoAdded =

    let phrase (r: RepoAdded) : Phrase = [ Segment.Text "added repo "; Segment.Ref (EntityRef.Repo r.Repo) ]

    let particulars (r: RepoAdded) : Phrase list = [ Phrase.text (sprintf "on branch %s" r.Branch) ]

module RepoRemoved =

    let phrase (r: RepoRemoved) : Phrase = [ Segment.Text "removed repo "; Segment.Ref (EntityRef.Repo r.Repo) ]

module RepoBranchSwitched =

    let phrase (r: RepoBranchSwitched) : Phrase =
        if r.Created then
            [ Segment.Text (sprintf "created branch %s in " r.Branch); Segment.Ref (EntityRef.Repo r.Repo) ]
        else
            [ Segment.Text "switched "; Segment.Ref (EntityRef.Repo r.Repo); Segment.Text (sprintf " to branch %s" r.Branch) ]

module RepoCapabilitiesChanged =

    /// What a repo asks for, when it changed. A person reading the timeline sees the whole
    /// set rather than the diff: a diff answers "what moved", and the question somebody
    /// actually has to answer is "is THIS the access I am content for this checkout to
    /// have" — which needs the whole of it.
    let phrase (c: RepoCapabilitiesChanged) : Phrase =
        match c.Granted with
        | [] -> Phrase.text "asks for nothing"
        | [ one ] -> Phrase.text (sprintf "asks for %s" one)
        | granted -> Phrase.text (sprintf "asks for %d capabilities" (List.length granted))

    /// The whole set, never a count on its own: the particulars are rendered beside the
    /// headline rather than behind a disclosure, so what a person has to decide about is
    /// still on the screen. One clause, because one is already the headline.
    let particulars (c: RepoCapabilitiesChanged) : Phrase list =
        match c.Granted with
        | []
        | [ _ ] -> []
        | granted -> [ Phrase.text (String.concat "; " granted) ]

module RepoCapabilitiesApproved =

    let phrase (a: RepoCapabilitiesApproved) : Phrase =
        [ Segment.Text "approved what "; Segment.Ref (EntityRef.Repo a.Repo); Segment.Text " asks for" ]

module RepoConfigRefused =

    /// Said in the refusal's own words rather than summarised: the `repo_config` query is
    /// showing that same sentence, and two renderings of one refusal are two things free to
    /// disagree. When it is the FILE itself, the reason already names the repo and the path
    /// inside the file, so anything in front of it would be a second copy of what it says —
    /// which is also why it is the headline there and not a particular under one.
    let phrase (r: RepoConfigRefused) : Phrase =
        match r.Sandbox with
        | Some sandbox -> [ Segment.Text "could not start sandbox "; Segment.Ref (EntityRef.Sandbox sandbox) ]
        | None -> Phrase.text r.Reason

    let particulars (r: RepoConfigRefused) : Phrase list =
        match r.Sandbox with
        | Some _ -> [ Phrase.text r.Reason ]
        | None -> []
