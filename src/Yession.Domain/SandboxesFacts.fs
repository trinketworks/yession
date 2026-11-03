namespace Yession.Domain.Sandboxes

open Yession.Domain

/// The path vocabulary everything OUTSIDE a sandbox speaks: a directory as a terminal in
/// that sandbox reaches it — relative to where a shell there starts when it is under
/// there, absolute when it is not.
///
/// `repos/octo/hello` is the whole path anybody in the session can act on, and it stays
/// that length however long the operator's data directory is. The absolute form is the
/// same fact wearing somebody's home directory —
/// `/Users/someone/.yession/sessions/40V9FY6MT534HDMBX6W5HS8PGR/workspace/repos/…` — which
/// every answer then carries and nobody who reads it can do anything with.
///
/// BOTH directions live here, in one module, because they are one fact seen twice: what
/// `reachedFrom` hands out, `resolvedFrom` has to take back. Converted in two places they
/// converted differently, and did — the repo verbs answered `repos/octo/hello` while the
/// shell profile stored what the sandbox's `pwd` said, so the note about where terminals
/// start wore the operator's home directory, and deleting that very checkout matched no
/// profile and cleared none.
module SandboxPath =

    /// A path names a directory whether or not it carries a trailing slash, and which it
    /// is says nothing about what it means.
    let private trimmed (path: string) = path.TrimEnd '/'

    /// An absolute path as a terminal REACHES it: relative to `root` when it is under
    /// there, unchanged when it is not — a named sandbox reaching the shared repos
    /// directory from its own workspace, anywhere else on the filesystem. A relative path
    /// that is only true somewhere else is worse than a long one.
    ///
    /// On a directory BOUNDARY, never a bare prefix: `/data/s/work` does not contain
    /// `/data/s/workspace/repos`, and answering `space/repos` for it would be a path to
    /// nowhere.
    ///
    /// The root ITSELF is `.` — the one relative path that is always true, and the only
    /// other answer would be the absolute one this exists to keep out of sight.
    let reachedFrom (root: string option) (path: string) : string =
        match root with
        | Some root when trimmed path = trimmed root -> "."
        | Some root when path.StartsWith (trimmed root + "/") -> path.Substring ((trimmed root).Length + 1)
        | _ -> path

    /// The absolute directory a spawn actually runs in: `path` resolved against the
    /// sandbox's own `root`. THE one place a relative path in this vocabulary acquires its
    /// meaning, and it acquires it from the sandbox — the only thing that knows what its
    /// root is.
    ///
    /// An absolute path is already an answer. `None` is a caller with no opinion, which is
    /// the root itself.
    /// The variable a path STARTS with, and what follows it: `~` and `~/x` are `HOME`,
    /// `$NAME/x` and `${NAME}/x` are `NAME`. A file tool takes paths "as a terminal in that
    /// sandbox would", and a terminal expands these — while the tools, handing the path to
    /// `cat` as an argument, did not, so the `$TMPDIR` that `execute_command` tells an agent to
    /// write under came back as "no such file" from `read_file` (session NR5KB8B5). Only a
    /// LEADING one, which is the shape scratch paths have; and only a name, never an
    /// expression, so resolving it is a lookup and never an evaluation.
    let leadingVariable (path: string) : (string * string) option =
        let isName (name: string) =
            name <> ""
            && (System.Char.IsLetter name.[0] || name.[0] = '_')
            && name |> Seq.forall (fun c -> System.Char.IsLetterOrDigit c || c = '_')
        if path = "~" then Some ("HOME", "")
        elif path.StartsWith "~/" then Some ("HOME", path.Substring 1)
        elif path.StartsWith "${" && path.Contains "}" then
            let close = path.IndexOf '}'
            let name = path.Substring (2, close - 2)
            if isName name then Some (name, path.Substring (close + 1)) else None
        elif path.StartsWith "$" then
            let name = path.Substring 1 |> Seq.takeWhile (fun c -> System.Char.IsLetterOrDigit c || c = '_') |> System.String.Concat
            if isName name then Some (name, path.Substring (1 + name.Length)) else None
        else None

    let resolvedFrom (root: string option) (path: string option) : string option =
        match path, root with
        | None, _ -> root
        | Some path, _ when path.StartsWith "/" -> Some path
        // `reachedFrom`'s answer for the root itself, taken back — the two arms are one
        // mapping, and a round trip that returned `/ws/.` would be that mapping losing.
        | Some ".", Some root -> Some root
        | Some path, Some root -> Some (sprintf "%s/%s" (trimmed root) path)
        // A relative path and no root: the backend's own idea of where it stands is the
        // only thing left, which is what an unconfined spawn has always used.
        | Some path, None -> Some path

/// A path a file wrote, resolved under a checkout and CLAMPED there. The decoder already
/// refuses a path that climbs out — that is the upstream fix, made where a person can
/// correct their file — and this is the downstream guard: a path reaching here some other
/// way still cannot name a directory above the checkout, because there is no arithmetic
/// here that could produce one.
module CheckoutPath =

    let under (checkout: string) (dir: string) : string =
        let resolved =
            dir.Split ([| '/'; '\\' |])
            |> Array.fold
                (fun acc segment ->
                    match segment with
                    | "" | "." -> acc
                    | ".." -> (match acc with [] -> [] | _ :: rest -> rest)
                    | segment -> segment :: acc)
                []
            |> List.rev
        match resolved with
        | [] -> checkout.TrimEnd '/'
        | segments -> checkout.TrimEnd '/' + "/" + String.concat "/" segments

/// A directory said from its sandbox's ROOT — where a spawn that names no directory runs —
/// and so the same string for as long as the sandbox exists. The shell profile is stored
/// in this vocabulary, and anything compared with a profile has to be in it too.
///
/// A TYPE, not a string, because the session speaks a second vocabulary that looks the
/// same: what `SandboxPath.reachedFrom` answers against where terminals start NOW, which is
/// what a listing tells a terminal and a file tool. That one moves whenever the profile
/// does. Compared with a profile it made a fold that never settled: with the profile at
/// the workspace, the listing said `repos/octo/hello` and the fold moved the profile
/// there; from there the listing said `.`, and the fold moved it back (session 3V9P,
/// twelve times in two days). Removing that checkout cleared nothing for the same reason.
/// Two strings, two meanings, and nothing to say which was which; with one of them a
/// type, mixing them does not compile.
[<Struct>]
type RootPath = private RootPath of string

module RootPath =

    let private trimmed (path: string) = path.TrimEnd '/'

    /// The root itself.
    let root : RootPath = RootPath "."

    /// An absolute directory as the sandbox whose root is `root` reaches it.
    let reached (root: string option) (absolute: string) : RootPath =
        RootPath (SandboxPath.reachedFrom root absolute)

    /// A path the log already holds in this vocabulary: a stored profile, read back.
    let stored (path: string) : RootPath = RootPath (trimmed path |> fun p -> if p = "" then "." else p)

    let value (RootPath path) : string = path

    let isRoot (RootPath path) : bool = path = "."

    /// `dir`, written relative to the checkout at `checkout`, clamped inside it
    /// (`CheckoutPath.under`).
    let under (RootPath checkout) (dir: string) : RootPath =
        match CheckoutPath.under checkout dir with
        | under when checkout = "." && under.StartsWith "./" -> RootPath (under.Substring 2)
        | under -> RootPath under

    /// Is `path` the directory `tree`, or somewhere inside it? On a directory BOUNDARY,
    /// never a bare prefix: `repos/hello-world` starts with `repos/hello` and is a
    /// different checkout. The root contains everything.
    let isInside (RootPath tree) (RootPath path) : bool =
        let tree = trimmed tree
        let path = trimmed path
        tree = "." || path = tree || path.StartsWith (tree + "/")

    /// Where a terminal starts, as the end of a sentence: "new terminals in dev start …".
    /// The root is said in words, because `.` at the end of a sentence reads as nothing.
    let describe (path: RootPath) : string =
        if isRoot path then "where the sandbox puts them" else sprintf "in %s" (value path)

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
      Actor : ActorRef
      /// Carried for the same parity: who the start is for, as `WorkSandboxStarted` says it.
      OnBehalfOf : Principal option
      /// What made it come up, when that was not its author's own idea: the repo added, the
      /// session starting, a person joining. `None` on a start somebody asked for directly,
      /// whose cause is the ask itself.
      CausedBy : Cause option }

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
      Actor : ActorRef
      /// Who the start was for, as `WorkSandboxStarted` says it.
      OnBehalfOf : Principal option
      /// What made it try, as `WorkSandboxStarting` says it.
      CausedBy : Cause option }

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
      /// The checkout has one absolute address in every sandbox (`Sandboxes.checkoutAt`),
      /// so this is not a second view of it. It is on the START because it is the start
      /// that says this sandbox has the checkout at all, and where its work begins. Logs
      /// written before the address was unified carry a container's own `/repos/…` here,
      /// true of the sandbox that started then.
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
      Actor : ActorRef
      /// Whose authority the start ran on, when that is not the actor's own: the person
      /// behind the agent, or behind a repo's file. `None` on a person's own start and on
      /// the boot fold, which nobody asked for. Attribution only — a start lends no
      /// credential, and each block spends its own act's.
      OnBehalfOf : Principal option
      /// What made it come up, as `WorkSandboxStarting` says it.
      CausedBy : Cause option }

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
      WorkingDirectory : RootPath option
      Actor : ActorRef
      /// The person behind the agent that set it, or behind the fold that applied a repo's
      /// `session.shell-profile`. `None` when a person set it themselves.
      OnBehalfOf : Principal option
      /// What made it change, as `WorkSandboxStarting` says it: the repo added whose file
      /// declared it. `None` on a set somebody asked for directly.
      CausedBy : Cause option }

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
        Phrase.deed "started" [ Segment.Text "sandbox "; Segment.Ref (EntityRef.Sandbox s.Sandbox); Segment.Text (sprintf " (%s)" s.Backend) ]

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
        Phrase.deed "starting" [ Segment.Text "sandbox "; Segment.Ref (EntityRef.Sandbox s.Sandbox); Segment.Text (sprintf " (%s)" s.Backend) ]

    let particulars (s: WorkSandboxStarting) : Phrase list = s.Description |> Option.map Phrase.text |> Option.toList

module WorkSandboxStartFailed =

    let phrase (s: WorkSandboxStartFailed) : Phrase =
        [ Segment.Text "sandbox "; Segment.Ref (EntityRef.Sandbox s.Sandbox); Segment.Text " "; Segment.Verb "could not start" ]

    let particulars (s: WorkSandboxStartFailed) : Phrase list = [ Phrase.text s.Reason ]

module WorkSandboxStopped =

    let phrase (s: WorkSandboxStopped) : Phrase =
        Phrase.deed "stopped" [ Segment.Text "sandbox "; Segment.Ref (EntityRef.Sandbox s.Sandbox) ]

module ShellProfileSet =

    /// Verb + noun, like every other headline: what happened, not where it takes effect —
    /// that is the particular, because "set shell profile for X" is already one clause and
    /// where terminals start is the second fact a reader asks about separately.
    let phrase (p: ShellProfileSet) : Phrase =
        Phrase.deed "set" [ Segment.Text "shell profile for "; Segment.Ref (EntityRef.Sandbox p.Sandbox) ]

    let particulars (p: ShellProfileSet) : Phrase list =
        match p.WorkingDirectory with
        | Some cwd -> [ Phrase.text ("terminals start " + RootPath.describe cwd) ]
        | None -> [ Phrase.text "terminals start where the sandbox puts them" ]

module SandboxSetupQueued =

    let phrase (q: SandboxSetupQueued) : Phrase =
        let what =
            match q.Problem with
            | Some _ -> " could not start its setup"
            | None -> sprintf " is running its setup: %s" q.Command
        [ Segment.Ref (EntityRef.Sandbox q.Sandbox); Segment.Text what ]

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
