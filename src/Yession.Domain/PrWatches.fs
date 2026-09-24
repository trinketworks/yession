namespace Yession.Domain.Prs

open System
open Yession.Domain

/// The session's watched pull requests, projected from events — `ReposProjection`'s
/// sibling, and the durable half of transition detection. The poller compares fresh
/// provider snapshots against `Known`, which folds from the LOG (a watch's `Initial`
/// advanced by each recorded `PrTransitioned`), never from process memory: a restart
/// re-folds the same log and re-announces nothing, while a change that happened during
/// the downtime is still detected, because the log still says the state before it.

/// Where a pull request stands with the thing that would merge it for us.
///
/// `Stalled` is not something a provider reports — it is `Queued` followed by not queued,
/// on a pull request still open, which is what a merge queue ejecting an entry looks like
/// from outside. So it can only be known from HISTORY, which is why it lives in the
/// baseline rather than in the snapshot.
type PrQueue =
    | NotQueued
    | Queued
    | Stalled

/// What the log has recorded about one pull request: the baseline the next detection
/// compares against. Deliberately not the whole snapshot — title and head sha are display
/// facts whose movement is not news. Mergeability IS carried, because a computed conflict
/// arriving is news (`PrTransition.Conflicted`); but only its COMPUTED value, `Some`, ever
/// advances this baseline — see `PrTransitions.detect`.
type PrKnown =
    { State : PrState
      Checks : ChecksRollup
      Queue : PrQueue
      /// The last COMPUTED mergeability, or `None` if the provider has never answered one
      /// for this watch yet. Never set to `None` by a fresh look that came back `None`: a
      /// provider still recomputing does not un-know what it last computed.
      Mergeable : bool option
      Draft : bool }

/// The one word for where a pull request stands, and how loudly to say it. ONE home,
/// because the settings panel, the roster summary and the header strip must not each
/// invent their own vocabulary for the same fact — they read it from here.
module PrStatus =

    /// The last thing that happened to this pull request, in a single past-tense word.
    /// On an open one a computed conflict wins, because it is the specific blocker and it
    /// names its own fix (rebase) — a pull request ejected from the queue FOR a conflict is
    /// both stalled and conflicted, and "conflicted" is the more useful of the two to show.
    /// Otherwise queue first, because "queued" and "stalled" are the news; a merged or
    /// closed pull request has stopped caring what any queue thought. `mergeable` is the
    /// baseline's last COMPUTED value (`None` = never computed, never "clean"), so the word
    /// does not flicker off "conflicted" during the window a push leaves it recomputing.
    let word (mergeable: bool option) (queue: PrQueue) (state: PrState) : string =
        match state with
        | PrMerged -> "merged"
        | PrClosed -> "closed"
        | PrOpen ->
            match mergeable with
            | Some false -> "conflicted"
            | Some true
            | None ->
                match queue with
                | Queued -> "queued"
                | Stalled -> "stalled"
                | NotQueued -> "open"

    /// What a watch says when the session cannot currently read it — a dead credential, a
    /// pull request it cannot see, a rate-limit window. The panel's status column says
    /// WHICH; a one-line summary has room only for the fact that nobody is driving this
    /// one, and for a worse reason than a stall.
    let unreachable : string = "unreachable"

    /// Worst first. What "worst" means here is how much it wants a person: an unreachable
    /// watch is not being driven at all, a stalled pull request has nobody driving it, a
    /// conflicted one is blocked until somebody rebases — the agent can, so it ranks below a
    /// stall — an open one is waiting on somebody, a queued one is waiting on machines, and
    /// merged or closed is over.
    let order : string list = [ unreachable; "stalled"; "conflicted"; "open"; "queued"; "merged"; "closed" ]

    /// A pull request that is still owed. Merged and closed ones are history: they are why
    /// a summary of six watches can honestly be silent.
    let live (word: string) : bool = word <> "merged" && word <> "closed"

    /// Which of two status words wants a person more. Unknown words rank last rather than
    /// first: a surface should not shout about a word this module has never heard of.
    let worse (left: string) (right: string) : string =
        let rank word =
            match order |> List.tryFindIndex (fun w -> w = word) with
            | Some index -> index
            | None -> List.length order
        if rank left <= rank right then left else right

    /// One line about a set of pull requests, for a surface with room for one line and no
    /// more — the Manager's roster, and the session page's header strip.
    ///
    /// Takes a LABEL rather than a `PrRef`, because the two callers hold the identity in
    /// different shapes: the session has the watch, the browser has only what the
    /// `pull_requests` query said. One function either way, because two surfaces that
    /// computed this separately would be two surfaces that disagree in front of the same
    /// person — which is the whole reason this module exists.
    ///
    /// Only what is still OWED is counted. A session whose watches have all merged has
    /// nothing to say, and says nothing, rather than reporting a number that is really a
    /// history. Silence here means "nothing waiting", which is what makes a line that IS
    /// there worth reading.
    let summarize (standings: (string * string) list) : string =
        match standings |> List.filter (snd >> live) with
        | [] -> ""
        // One is named, because with a single pull request the label IS the answer and a
        // count of one says less than the thing it counted.
        | [ label, word ] -> sprintf "%s %s" label word
        | several ->
            let worst = several |> List.map snd |> List.reduce worse
            sprintf "%d PRs · %d %s" (List.length several) (several |> List.filter (snd >> (=) worst) |> List.length) worst

    /// How a pull request is named in a one-line summary: its number, because the surface
    /// showing it belongs to one session and a session's watches are rarely spread over
    /// enough repositories for the owner and name to be the question. Both callers go
    /// through this so the roster and the strip name it identically.
    let label (pr: PrRef) : string = sprintf "#%d" pr.Number

    /// The same, recovered from a rendered `owner/repo#12` — what a browser has, since the
    /// query hands it the rendering rather than the reference. Total: a label it cannot
    /// read is passed through rather than replaced by a guess.
    let labelOf (rendered: string) : string =
        match rendered.LastIndexOf '#' with
        | -1 -> rendered
        | at -> rendered.Substring at

    /// The `pull_requests` query's own column keys, so a reader of its rows names them once
    /// rather than each surface spelling its own string. They are the wire's keys and the
    /// browser only ever sees the query, so this is where a client learns them — the host
    /// declares the same three in `PrWatches.queryDef`, and the round-trip suite is what
    /// says the two agree.
    module Columns =
        /// The query's own name, for the same reason: a browser finds these rows by it.
        let query = "pull_requests"
        let pr = "pr"
        let state = "state"
        let status = "status"

    /// What one watch contributes to a summary, from the three facts every surface has in
    /// some shape: how it is named, the word for where it stands, and whether the session
    /// can still READ it.
    ///
    /// Unreadable outranks whatever it last said — a dead credential means nobody is
    /// driving that one, which is worse news than any state it is stuck in. The rule lives
    /// here and not at either caller, because the session holds a `PrWatchRow` and a browser
    /// holds a row of the `pull_requests` query, and two surfaces that decided this
    /// separately would disagree in front of the same person.
    ///
    /// `None` for a watch nobody has looked at yet, which is not a standing.
    let standing (label: string) (said: string option) (readable: bool) : (string * string) option =
        if not readable then Some (label, unreachable)
        else said |> Option.map (fun word -> label, word)

type PrWatch =
    { Pr : PrRef
      /// Whose watch — see `PrWatched.watcher`.
      Watcher : Principal
      Known : PrKnown
      /// When this pull request last became what it now is: the envelope timestamp of the
      /// watch's start, advanced by each recorded transition. Read from the LOG for the
      /// baseline's reason — a poll that finds nothing new must not make a watch look
      /// fresher than it is, and only an event says something happened.
      Since : DateTimeOffset }

type PrWatchesProjection = { Watches : PrWatch list }

module PrTransitions =

    /// The baseline a watch starts from: its `Initial` snapshot, reduced to what
    /// transitions are detected on.
    ///
    /// A watch that begins on an already-ejected pull request reads `NotQueued`, not
    /// `Stalled`, and that is honest: nobody watching saw it fall out, and claiming
    /// otherwise would announce a stall that this session cannot know happened.
    let knownOf (snapshot: PrSnapshot) : PrKnown =
        { State = snapshot.State
          Checks = snapshot.Checks
          Queue = (if snapshot.Queued then Queued else NotQueued)
          Mergeable = snapshot.Mergeable
          Draft = snapshot.Draft }

    /// Advance a baseline by one announced transition — the projection's fold, and the
    /// poller's, so the two cannot disagree about what has been said.
    let advance (known: PrKnown) (transition: PrTransition) : PrKnown =
        match transition with
        | PrTransition.Merged -> { known with State = PrMerged }
        | PrTransition.Closed -> { known with State = PrClosed }
        | PrTransition.Reopened -> { known with State = PrOpen }
        | PrTransition.ChecksPassed -> { known with Checks = ChecksGreen }
        | PrTransition.ChecksFailed -> { known with Checks = ChecksRed }
        | PrTransition.Queued -> { known with Queue = Queued }
        | PrTransition.Stalled -> { known with Queue = Stalled }
        | PrTransition.Conflicted -> { known with Mergeable = Some false }
        | PrTransition.Resolved -> { known with Mergeable = Some true }
        | PrTransition.ReadyForReview -> { known with Draft = false }
        | PrTransition.Drafted -> { known with Draft = true }

    /// What a fresh snapshot means against the last recorded baseline: at most one state
    /// transition, at most one checks transition and at most one queue transition, in
    /// that order.
    ///
    /// Only ARRIVALS at green or red are checks news — a new push resetting checks to
    /// pending is the ordinary rhythm of work, not an announcement. And checks movement
    /// on a pull request that is no longer open is suppressed entirely: CI going red on
    /// a merged PR is not something the watcher can act on from here.
    let detect (known: PrKnown) (fresh: PrSnapshot) : PrTransition list =
        let state =
            match known.State, fresh.State with
            | PrOpen, PrMerged -> [ PrTransition.Merged ]
            | PrOpen, PrClosed -> [ PrTransition.Closed ]
            | PrClosed, PrOpen -> [ PrTransition.Reopened ]
            // Closed-to-merged: GitHub reports a merged PR as closed+merged, so a watch
            // whose baseline is closed learning of a merge is real (reopened-then-merged
            // between polls collapses to this) and merged is the fact that matters.
            | PrClosed, PrMerged -> [ PrTransition.Merged ]
            | _ -> []
        let stateAfter = state |> List.fold advance known
        let checks =
            match stateAfter.State with
            | PrOpen ->
                match known.Checks, fresh.Checks with
                | ChecksGreen, ChecksGreen
                | ChecksRed, ChecksRed -> []
                | _, ChecksGreen -> [ PrTransition.ChecksPassed ]
                | _, ChecksRed -> [ PrTransition.ChecksFailed ]
                | _ -> []
            | PrMerged | PrClosed -> []
        // Queue news, on the same terms as checks news and for the same reason: a merged
        // pull request left the queue by going through it, and saying "stalled" about that
        // would be reporting the success as a failure. A re-arm after a stall announces
        // `Queued` again, because it is again true that nobody is needed.
        let queue =
            match stateAfter.State with
            | PrOpen ->
                match known.Queue, fresh.Queued with
                | Queued, true -> []
                | _, true -> [ PrTransition.Queued ]
                | Queued, false -> [ PrTransition.Stalled ]
                | _, false -> []
            | PrMerged | PrClosed -> []
        // Mergeability news, and the one axis whose fresh value can be UNKNOWN. `None` is
        // the provider still computing the merge (routinely, right after a push), so it
        // holds the baseline and says nothing — only a COMPUTED value moves. A computed
        // conflict is `Conflicted`; a computed clean that follows a known conflict is
        // `Resolved`. A watch that BEGINS conflicted reads `Some false` as its baseline and
        // re-announces nothing, the honest `Stalled` rule: nobody watching saw it break.
        // Suppressed off `PrOpen` for the checks reason — a merged or closed pull request's
        // mergeability is not something the watcher acts on from here.
        let merge =
            match stateAfter.State with
            | PrOpen ->
                match known.Mergeable, fresh.Mergeable with
                | _, None -> []
                | Some false, Some false
                | Some true, Some true -> []
                | _, Some false -> [ PrTransition.Conflicted ]
                | Some false, Some true -> [ PrTransition.Resolved ]
                | _, Some true -> []
            | PrMerged | PrClosed -> []
        // Draft news, suppressed off `PrOpen` for the checks reason: a merged pull request
        // stopped being a draft by merging, which is not a second thing to say.
        let draft =
            match stateAfter.State with
            | PrOpen ->
                match known.Draft, fresh.Draft with
                | true, false -> [ PrTransition.ReadyForReview ]
                | false, true -> [ PrTransition.Drafted ]
                | _ -> []
            | PrMerged | PrClosed -> []
        state @ checks @ queue @ merge @ draft

module PrWatchesProjection =

    let empty : PrWatchesProjection = { Watches = [] }

    /// Fold one event. Re-watching an existing pull request replaces its entry (the
    /// newest baseline wins — the `add_repo` rule); a transition advances that watch's
    /// baseline by exactly what was announced.
    let applyEvent (proj: PrWatchesProjection) (envelope: EventEnvelope<SessionEvent>) : PrWatchesProjection =
        match envelope.Event with
        | PrWatched p ->
            let pr = PrWatched.pr p
            let entry =
                { Pr = pr
                  Watcher = PrWatched.watcher p
                  Known = PrTransitions.knownOf (PrWatched.initial p)
                  Since = envelope.Timestamp }
            if proj.Watches |> List.exists (fun w -> w.Pr = pr) then
                { Watches = proj.Watches |> List.map (fun w -> if w.Pr = pr then entry else w) }
            else
                { Watches = proj.Watches @ [ entry ] }
        | PrUnwatched p ->
            { Watches = proj.Watches |> List.filter (fun w -> w.Pr <> p.Pr) }
        | PrTransitioned p ->
            { Watches =
                proj.Watches
                |> List.map (fun w ->
                    if w.Pr = p.Pr then
                        { w with
                            Known = PrTransitions.advance w.Known p.Transition
                            Since = envelope.Timestamp }
                    else w) }
        | _ -> proj

    let tryFind (pr: PrRef) (proj: PrWatchesProjection) : PrWatch option =
        proj.Watches |> List.tryFind (fun w -> w.Pr = pr)
