namespace Yession.Domain.Repos

open Yession.Domain

/// One skill a repo's `.agents/skills/<name>/SKILL.md` declares — its frontmatter, and
/// nothing of its body. Enough to menu it: the agent reads the file itself
/// (`ToolName.ReadFile`) when a description matches what it is doing, so nothing here needs
/// to hold more than what a menu line says.
type SkillSummary =
    { Name : string
      Description : string }

/// The facts a repo records. They sit BELOW `SessionEvent` because the union names them,
/// and the projections that fold that union sit above it — so a feature spans the event
/// spine rather than living on one side of it. What this buys is that `Repo` and `Branch`
/// stop being field labels in the namespace every file opens.
///
/// A repo was CHOSEN for this session, and its clone has begun. Recorded before the name
/// check and the clone, so the timeline shows the act the moment it is asked for rather
/// than after the seconds a clone takes — the dead air `WorkSandboxStarting` fills for a
/// sandbox. Its item runs until a `RepoCloned` or `RepoCloneFailed` with the same
/// `MessageId` resolves it. Nothing reads a repo as present off this: the checkout does
/// not exist yet, which is why the repo's facts are on `RepoCloned`.
type [<RequireQualifiedAccess>] RepoAdded =
    { /// The timeline note's identity, minted by the Process at append time — which is
      /// what lets the conversation projection fold this without inventing ids. The
      /// clone's outcome carries the same one.
      MessageId : MessageId
      Repo : RepoRef
      /// Who brought it in — the panel's human or the agent. Carried on the payload
      /// because the projection reads events, not envelopes, and "who added this repo"
      /// is the fact the shared-trust disclosure hangs off.
      Actor : ActorRef }

/// The checkout a `RepoAdded` began is in place — the moment the repo exists here.
///
/// A log written before `RepoAdded` was recorded up front has one `repoAdded` record per
/// add, written at this moment and carrying these facts; the codec reads it as this event,
/// with no `RepoAdded` before it.
and RepoCloned =
    { /// The `RepoAdded`'s, so the item it opened is resolved in place.
      MessageId : MessageId
      Repo : RepoRef
      /// The branch the clone landed on (the remote's default).
      Branch : string
      Actor : ActorRef
      /// The checkout's own root `AGENTS.md`, read once at the moment the clone landed
      /// (the one moment its content is trustworthy -- `app/Repos.fs`, right after the
      /// staging rename). `None` when the file is absent or unreadable; an unreadable
      /// optional file is not a reason to fail an otherwise-successful clone. Carried on
      /// the fact itself, the same way `Branch` is, so a repo's notes replay from the log
      /// like everything else this system remembers -- read again only when the repo is
      /// re-added, which is this system's cache-with-explicit-invalidation, never a
      /// per-turn re-read.
      AgentsMd : string option
      /// Every skill under `.agents/skills/` this checkout declared, read at the same
      /// moment and for the same reason `AgentsMd` is: a skill's file is repo-authored, so
      /// the moment of a landed clone is the one moment it is trustworthy, and re-reading it
      /// every turn would be a re-fetch of something that only changes when the repo is
      /// re-added. A directory with no `SKILL.md`, or one whose frontmatter does not parse,
      /// contributes nothing -- a malformed skill is not a reason to fail an otherwise-
      /// successful clone, the same tolerance `AgentsMd` already has.
      Skills : SkillSummary list }

/// The clone a `RepoAdded` began did not land, and why — in the words the failure used.
/// Nothing was put in place: the clone runs in a staging area that is cleared on failure.
and RepoCloneFailed =
    { /// The `RepoAdded`'s, so the item it opened is resolved in place.
      MessageId : MessageId
      Repo : RepoRef
      Reason : string
      Actor : ActorRef }

and [<RequireQualifiedAccess>] RepoRemoved =
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
      /// The repo's file, as the party asking.
      Actor : ActorRef
      /// What made the file be read this time — the same cause the starts it precedes carry.
      CausedBy : Cause option }

/// A person consented to what a repo asks for. RETIRED: consent is now a person's trust in
/// the repo (`RepoTrusted`). Nothing appends this. The case stays because a log written before
/// that change still holds it, and a log that would not load is not a tidier union.
and [<RequireQualifiedAccess>] RepoCapabilitiesApproved =
    { MessageId : MessageId
      Repo : RepoRef
      /// Exactly what was consented to, so a later ask that differs is a new decision rather
      /// than something the old yes silently covers.
      Granted : string list
      /// Who said yes. Never `Configured` and never the agent.
      Actor : ActorRef }

/// A repo's sandboxes wait for somebody here to trust it with their GitHub.
///
/// Written by the fold when the repo's sandboxes forward GitHub and nobody here can run them
/// with theirs: nobody trusts it yet, or the one who does is not here, or it now asks for a
/// route their record has not answered. A card asks everyone here; the sandboxes start when
/// one of them trusts it (`RepoTrusted`).
and [<RequireQualifiedAccess>] RepoTrustAsked =
    { MessageId : MessageId
      Repo : RepoRef
      /// The routes its sandboxes forward GitHub by, which is what trusting it means.
      Routes : ConnectionRoute list
      /// The repo's file, as the party asking.
      Actor : ActorRef
      CausedBy : Cause option }

/// A repo's sandboxes run with this person's GitHub here.
///
/// Their trust in the repo is their own record, kept across sessions (`RepoTrust`). This is the
/// other fact: that THIS session runs the repo on it. Only an act of theirs writes it: adding
/// the repo when their record already trusts it, or answering its card. Joining a session never
/// does, because trusting a repo is not lending your GitHub to every session it is in.
and [<RequireQualifiedAccess>] RepoTrusted =
    { MessageId : MessageId
      Repo : RepoRef
      /// Whose GitHub. A person: the agent, a process and a repo's file cannot trust anything.
      By : Principal
      /// The person, as the party acting.
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
      Actor : ActorRef
      /// What made the file be read this time, as on its starts.
      CausedBy : Cause option }

/// Something an analyzer had to tell a repo's file author (`ConfigAnalysis`). The file was
/// honoured as written; this is only said.
and RepoConfigWarned =
    { MessageId : MessageId
      Repo : RepoRef
      /// Which declaration it is about, when it is about one.
      Sandbox : SandboxRef option
      /// Where in the file, as its author would find it: `sandboxes.dev.wants[1], line 12`.
      Where : string
      /// Said whole, as the analyzer said it.
      Warning : string
      /// The repo's file, as on its refusals.
      Actor : ActorRef
      CausedBy : Cause option }

// --- What each repo act SAYS ----------------------------------------------------------------
// The sentence an event writes into the timeline lives beside the event, for the reason
// `WorkSandboxStarted.phrase` does: what an event's leaves MEAN is knowledge that belongs
// with the event, not assembled by whatever folds it. `Act.phrase` (Acts.fs) dispatches here;
// the fold composes nothing. A `Phrase` rather than a string, so a reader that is a screen
// can draw what the sentence points at: the repository is a REFERENCE in each, which prose
// spells `github:owner/repo` (`EntityRef.said`) and a screen draws with its mark, as a link.

module RepoAdded =

    let phrase (r: RepoAdded) : Phrase = Phrase.deed "added" [ Segment.Text "repo "; Segment.Ref (EntityRef.Repo r.Repo) ]

    /// Said to a reader that is not a screen, which sees no running mark: the add is not a
    /// checkout yet.
    let particulars (_: RepoAdded) : Phrase list = [ Phrase.text "cloning" ]

/// The same headline as the add it resolves, because to a reader it is the same act, finished.
module RepoCloned =

    let phrase (r: RepoCloned) : Phrase = Phrase.deed "added" [ Segment.Text "repo "; Segment.Ref (EntityRef.Repo r.Repo) ]

    let particulars (r: RepoCloned) : Phrase list = [ Phrase.text (sprintf "on branch %s" r.Branch) ]

module RepoCloneFailed =

    let phrase (r: RepoCloneFailed) : Phrase = Phrase.deed "could not clone" [ Segment.Text "repo "; Segment.Ref (EntityRef.Repo r.Repo) ]

    let particulars (r: RepoCloneFailed) : Phrase list = [ Phrase.text r.Reason ]

module RepoRemoved =

    let phrase (r: RepoRemoved) : Phrase = Phrase.deed "removed" [ Segment.Text "repo "; Segment.Ref (EntityRef.Repo r.Repo) ]

module RepoBranchSwitched =

    let phrase (r: RepoBranchSwitched) : Phrase =
        if r.Created then
            Phrase.deed "created" [ Segment.Text (sprintf "branch %s in " r.Branch); Segment.Ref (EntityRef.Repo r.Repo) ]
        else
            Phrase.deed "switched" [ Segment.Ref (EntityRef.Repo r.Repo); Segment.Text (sprintf " to branch %s" r.Branch) ]

module RepoCapabilitiesChanged =

    /// What a repo asks for, when it changed. A person reading the timeline sees the whole
    /// set rather than the diff: a diff answers "what moved", and the question somebody
    /// actually has to answer is "is THIS the access I am content for this checkout to
    /// have" — which needs the whole of it.
    let phrase (c: RepoCapabilitiesChanged) : Phrase =
        match c.Granted with
        | [] -> Phrase.deed "asked for" (Phrase.text "nothing")
        | [ one ] -> Phrase.deed "asked for" (Phrase.text one)
        | granted -> Phrase.deed "asked for" (Phrase.text (sprintf "%d capabilities" (List.length granted)))

    /// The whole set, never a count on its own: the sentence the agent reads carries every
    /// grant. A screen lays the same set out as one fact per grant (`View`), rather than
    /// printing this semicolon chain under the headline. None for one, because one is
    /// already the headline.
    let particulars (c: RepoCapabilitiesChanged) : Phrase list =
        match c.Granted with
        | []
        | [ _ ] -> []
        | granted -> [ Phrase.text (String.concat "; " granted) ]

module RepoCapabilitiesApproved =

    let phrase (a: RepoCapabilitiesApproved) : Phrase =
        Phrase.deed "approved" [ Segment.Text "what "; Segment.Ref (EntityRef.Repo a.Repo); Segment.Text " asks for" ]

module RepoTrustAsked =

    /// What a route lets a repo's code do with somebody's GitHub, in a person's words.
    let private routeWords (route: ConnectionRoute) : string =
        match route with
        | ConnectionRoute.Git -> "push and fetch"
        | ConnectionRoute.Api -> "the API"

    let phrase (a: RepoTrustAsked) : Phrase =
        Phrase.deed
            "is waiting"
            (Phrase.text (
                sprintf
                    "for somebody here to trust it with their GitHub (%s)"
                    (a.Routes |> List.map routeWords |> String.concat ", ")
            ))

module RepoTrusted =

    let phrase (t: RepoTrusted) : Phrase =
        Phrase.deed "trusts" [ Segment.Ref (EntityRef.Repo t.Repo); Segment.Text ": its sandboxes run with their GitHub here" ]

module RepoConfigRefused =

    /// Said in the refusal's own words rather than summarised: the `repo_config` query is
    /// showing that same sentence, and two renderings of one refusal are two things free to
    /// disagree. When it is the FILE itself, the reason already names the repo and the path
    /// inside the file, so anything in front of it would be a second copy of what it says —
    /// which is also why it is the headline there and not a particular under one.
    let phrase (r: RepoConfigRefused) : Phrase =
        match r.Sandbox with
        | Some sandbox -> Phrase.deed "could not start" [ Segment.Text "sandbox "; Segment.Ref (EntityRef.Sandbox sandbox) ]
        | None -> Phrase.text r.Reason

    let particulars (r: RepoConfigRefused) : Phrase list =
        match r.Sandbox with
        | Some _ -> [ Phrase.text r.Reason ]
        | None -> []

module RepoConfigWarned =

    /// The declaration it is about as the headline, the analyzer's own sentence under it —
    /// the same split a refusal about one declaration makes.
    let phrase (w: RepoConfigWarned) : Phrase =
        match w.Sandbox with
        | Some sandbox -> [ Segment.Text "a note about sandbox "; Segment.Ref (EntityRef.Sandbox sandbox) ]
        | None -> [ Segment.Text "a note about "; Segment.Ref (EntityRef.Repo w.Repo); Segment.Text "'s yession.yaml" ]

    let particulars (w: RepoConfigWarned) : Phrase list =
        [ Phrase.text (sprintf "%s: %s" w.Where w.Warning) ]
