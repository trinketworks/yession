namespace Yession.App

open Yession.Domain
open Yession.Domain.Link
open Yession.Domain.Repos

/// The launch surface (the session's first screen): a person choosing which repo this
/// session is FOR, before any turn has run.
///
/// An ASK CARD, docked above the composer: the session asks which repository, offers the
/// ones the person's credential reaches (or a search, or a pasted link), and one bordered
/// button commits. Choosing is a STATE — a row held, its branch named inside it — and
/// starting is a press; nothing is sent by choosing. That split is what the card's next
/// use needs too: the agent asking a question with several answers held at once.
///
/// View state, local to this client and never synced — choosing is one person's act on one
/// screen, and what it produces is the `AddRepo` command, whose outcome everybody reads off
/// the log (`RepoAdded`, or `GatedCommandFailed`). The one thing folded back in from the
/// log is that failure, so the screen that asked can say why rather than sit there.
///
/// Pure: what the browser does — fetch a listing, resolve a link, send the command — is in
/// `Browser.fs`, and what it learns comes back through `LaunchMsg`.

/// What the card has to choose from. Three honest states, for the model catalogue's
/// reason: "not looked yet" and "looked and there is nothing" are different facts, and the
/// difference is what decides whether a person waits or goes and connects an account.
type LaunchListing =
    | ListingUnknown
    | ListingLoaded of RepoPage
    /// The lookup answered, and what it said was why it could not. `SignIn` is whether the
    /// answer was "connect GitHub" — a 401 either way — which is the one failure with a
    /// button rather than a retry.
    | ListingUnavailable of reason: string * signIn: bool

/// Where the NEXT page stands. Its own state rather than a flag on the listing, because it
/// is about an attempt and not about what is on screen: the rows already read stay read
/// while the page after them is in flight, and stay read when it fails.
type LaunchMore =
    | MoreIdle
    | MoreFetching
    /// The page did not come. Said at the foot, with a way to ask again — never by taking
    /// the rows above it away.
    | MoreFailed of reason: string

/// A held row's branches, asked for when the row is held and not before: thirty rows are
/// thirty lookups, and nearly every launch is on the default.
type LaunchBranches =
    | BranchesUnknown
    | BranchesLoaded of BranchPage
    | BranchesUnavailable of reason: string

/// Which of the card's two panes is on screen. The branch pane names the repo it is FOR,
/// because there is no branch without one — a pane that could stand over no repository would
/// be a pane with nothing to list.
type LaunchPane =
    | ChoosingRepo
    | ChoosingBranch of RepoRef

/// What a launch is FOR: the repo, and the branch when it is not the provider's default.
/// `None` deliberately, rather than the default's name — a launch that names the default
/// is not a switch, and the command should not say it is.
[<RequireQualifiedAccess>]
type LaunchTarget =
    { Repo : RepoRef
      Branch : string option }

/// Where the act stands. `Resolving` is a pasted link being asked about; `Sent` carries
/// the request so the session's answer can be told apart from any other command's;
/// `Cloning` is admitted — the answer arrives as events.
type LaunchStage =
    | Choosing
    | Resolving of RepoLink
    | Sent of RequestId * LaunchTarget
    | Cloning of LaunchTarget

type LaunchViewState =
    { /// What was typed into the field. Empty means "my repos".
      Query : string
      Listing : LaunchListing
      /// The row held, if one is. One for now; the shape of a set, so holding several
      /// later is a list rather than a redesign.
      Selected : RepoRef option
      /// Where the next page stands.
      More : LaunchMore
      /// Which pane is on screen, and the one the branch pane is for.
      Pane : LaunchPane
      /// What was typed on the BRANCH pane. It both narrows what is listed and stands as a
      /// name of its own: `switch_branch` creates a branch the provider has not got, so what
      /// is typed here is always offerable even when it matches nothing.
      BranchQuery : string
      /// Where the branch listing's next page stands. One, not one per repo, because one
      /// branch pane is open at a time.
      BranchMore : LaunchMore
      /// Each held row's branches, by repo.
      Branches : Map<RepoRef, LaunchBranches>
      /// The branch named on a row, by repo, when it is not the row's default. Named, not
      /// picked: a field with the provider's branches to choose from, that also takes a
      /// name the provider has not got yet — `switch_branch` creates one.
      Named : Map<RepoRef, string>
      Stage : LaunchStage
      /// The last thing that went wrong with a launch — a link that could not be resolved,
      /// a rejection at the door, or a clone that failed — shown until the next attempt.
      Problem : string option
      /// The card was dismissed: it steps aside for this client. The ordinary empty
      /// timeline and the composer are what is left, which is how a session that is not
      /// about a repository begins.
      Dismissed : bool
      /// Decided ONCE and held: the client was connected, caught up, and the session was
      /// unstarted. `offered` reads this instead of asking the live connection on every
      /// render (`Launch.anchor` sets it, `Launch.eligible` is the live question it asks) --
      /// a reconnect catching up on what arrived while it was gone must not take the card
      /// away and bring it back. What still retires it live is `begun`, `Dismissed`, or a
      /// launch under way.
      Anchored : bool }

type LaunchMsg =
    | LaunchQueryTyped of string
    | LaunchListingArrived of LaunchListing
    /// A row was tapped: held if it was not, let go if it was.
    | LaunchSelected of RepoCandidate
    /// A pasted link resolved to a row: put at the head of the list if it is not in it,
    /// and held, with the branch the link named.
    | LaunchLinked of RepoCandidate * branch: string option
    /// The foot of the list came into view and a page was asked for.
    | LaunchMoreStarted
    /// The next page landed: its rows go after the ones already read, and its own `Next`
    /// replaces the cursor that fetched it.
    | LaunchMoreArrived of RepoPage
    | LaunchMoreFailed of reason: string
    /// Branches for a repo. Carries WHICH repo, so an answer for one row cannot land on
    /// another.
    | LaunchBranchesArrived of RepoRef * LaunchBranches
    | LaunchBranchNamed of RepoRef * string
    /// The card moved to the branch pane, and back. The repo is carried because the pane is
    /// ABOUT one — going there from a row is the only way in.
    | LaunchBranchPaneOpened of RepoRef
    | LaunchBranchPaneClosed
    | LaunchBranchQueryTyped of string
    | LaunchBranchMoreStarted
    | LaunchBranchMoreArrived of RepoRef * BranchPage
    | LaunchBranchMoreFailed of reason: string
    /// A pasted link is being asked about before it can be held.
    | LaunchResolving of RepoLink
    /// The command left, under this request id, for this target.
    | LaunchSent of RequestId * LaunchTarget
    /// The session answered a command; only the one `Sent` names is this surface's.
    | LaunchAnswered of RequestId * SessionCommandResult
    /// An attempt failed — a link the provider could not resolve, or the log saying the
    /// clone did (`GatedCommandFailed` for `add_repo`). Choosing is open again.
    | LaunchFailed of reason: string
    | LaunchDismissed

module Launch =

    let empty : LaunchViewState =
        { Query = ""
          Listing = ListingUnknown
          Selected = None
          More = MoreIdle
          Pane = ChoosingRepo
          BranchQuery = ""
          BranchMore = MoreIdle
          Branches = Map.empty
          Named = Map.empty
          Stage = Choosing
          Problem = None
          Dismissed = false
          Anchored = false }

    /// Whether the add has been SENT and the card is now waiting on events, not on the
    /// person. `Resolving` is still the person's step — a pasted link being turned into a
    /// row to hold — so it is NOT committed; `Sent`/`Cloning` are, and once committed the
    /// card steps aside for the timeline, where the add shows and the outcome lands, exactly
    /// as an agent's `add_repo` does. A failure returns the stage to `Choosing`
    /// (`LaunchFailed`/a rejection), which brings the card back to try another — which is
    /// why this is read here rather than folded into `begun`: a clone that could not reach
    /// its repo needs the card, not a blank.
    let committed (launch: LaunchViewState) : bool =
        match launch.Stage with
        | Choosing | Resolving _ -> false
        | Sent _ | Cloning _ -> true

    /// The live question `anchor` asks, once, on the way to deciding: this client is
    /// connected, has read the log through to where the session says it ends, and the
    /// session has not BEGUN - no repo in it and nothing said. A session that has begun is
    /// the agent's to add a repo to (Plan 15).
    ///
    /// "Begun" is a repo or a message, deliberately not "anything on the timeline": a launch
    /// that failed leaves its failure on the timeline, and a person whose clone could not
    /// reach the repo needs the card still there to try another, not a note and a blank.
    ///
    /// The catch-up conditions are what keep it honest: a client that has not looked yet, or
    /// is still reading, has an empty projection too, and a launch card that flashed over
    /// every cold open of an old session would teach people it means nothing.
    ///
    /// NOT what a render reads (`offered` is): see `anchor`, below, for why a live signal
    /// answers this question exactly once and is then retired.
    let eligible
        (connected: bool)
        (historyRead: bool)
        (latestKnown: EventOffset option)
        (catchingUp: bool)
        (begun: bool)
        : bool =
        connected && historyRead && latestKnown.IsSome && not catchingUp && not begun

    /// The card's start, ANCHORED: once `eligible` has been true, once, it is decided - a
    /// session that begins offered stays offered through whatever its OWN connection does
    /// next. Without this, a phone backgrounding the tab drops the socket; reconnecting
    /// spends a moment `catchingUp` on whatever arrived while it was gone; and a card
    /// computed fresh from those live signals on every render winks out and back for no
    /// reason a person watching it could name. `begun` alone still closes it, live, in
    /// `offered` below - this only ever LATCHES true, never false, and never at all once the
    /// session has already begun (a cold open of an old session must not anchor a card
    /// nobody will see offered).
    let anchor
        (connected: bool)
        (historyRead: bool)
        (latestKnown: EventOffset option)
        (catchingUp: bool)
        (begun: bool)
        (launch: LaunchViewState)
        : LaunchViewState =
        if launch.Anchored || begun then launch
        elif eligible connected historyRead latestKnown catchingUp begun then
            { launch with Anchored = true }
        else launch

    /// Whether the card is OFFERED. Reads the ANCHOR (`Launch.anchor`), not the live
    /// connection - that is the point of anchoring it - alongside what is still read live:
    /// `begun`, because a message or a repo landing while the card stands is what retires it
    /// for real; `Dismissed`, this client's own way out; and `committed`, once under way.
    let offered (begun: bool) (launch: LaunchViewState) : bool =
        launch.Anchored && not begun && not launch.Dismissed && not (committed launch)

    /// Whether the card is busy with an attempt: rows are not for holding while one is
    /// under way, because two clones of two repos is not what anyone meant.
    let busy (launch: LaunchViewState) : bool =
        match launch.Stage with
        | Choosing -> false
        | Resolving _ | Sent _ | Cloning _ -> true

    let candidates (launch: LaunchViewState) : RepoCandidate list =
        match launch.Listing with
        | ListingLoaded page -> page.Candidates
        | ListingUnknown | ListingUnavailable _ -> []

    /// The cursor the listing on screen would ask with next, whatever else is happening to
    /// it — so a row put at the head by a pasted link does not throw away the rest of the
    /// list's paging on its way in.
    let private nextOf (launch: LaunchViewState) : string option =
        match launch.Listing with
        | ListingLoaded page -> page.Next
        | ListingUnknown | ListingUnavailable _ -> None

    /// The cursor the foot would ask with, if it should ask at all: a page to come, nothing
    /// already in flight, and no attempt under way. ONE rule, here, rather than a condition
    /// spelled out at whatever is watching the foot — the thing that watches fires many
    /// times for one scroll, and a guard it owned would be a guard the reducer could not see.
    let wanting (launch: LaunchViewState) : string option =
        match launch.Listing, launch.More with
        | ListingLoaded page, MoreIdle when not (busy launch) -> page.Next
        | _ -> None

    /// The row held, as a candidate — if the list still has it.
    let held (launch: LaunchViewState) : RepoCandidate option =
        launch.Selected |> Option.bind (fun repo -> candidates launch |> List.tryFind (fun c -> c.Repo = repo))

    /// The branches on screen for the pane's repo, narrowed by what was typed. Narrowing is
    /// done HERE, over the pages already read, because the provider has no branch search to
    /// ask — `/branches` takes a page and nothing else. So what is typed filters what has
    /// arrived, and `namingNew` is what covers the rest: a name nobody has scrolled to yet
    /// and a name that does not exist are the same offer, and `switch_branch` makes both work.
    let branchesOn (launch: LaunchViewState) (repo: RepoRef) : string list =
        let typed = launch.BranchQuery.Trim ()
        let all =
            match launch.Branches |> Map.tryFind repo with
            | Some (BranchesLoaded page) -> page.Names
            | Some (BranchesUnavailable _) | Some BranchesUnknown | None -> []
        if typed = "" then all
        else all |> List.filter (fun name -> name.Contains typed)

    /// What was typed, when it is a branch to NAME rather than one already listed. Offered
    /// above the listing, because a name the provider does not have is the one thing a list
    /// can never contain.
    let namingNew (launch: LaunchViewState) (repo: RepoRef) : string option =
        match launch.BranchQuery.Trim () with
        | "" -> None
        | typed when branchesOn launch repo |> List.exists (fun name -> name = typed) -> None
        | typed -> Some typed

    /// The cursor the BRANCH pane's foot would ask with — `wanting`'s rule, for the other
    /// listing. Nothing while a search is narrowing, because what is on screen is then a
    /// filter over what has arrived rather than the head of the listing, and paging into a
    /// filter fetches pages nobody sees.
    let wantingBranches (launch: LaunchViewState) : (RepoRef * string) option =
        match launch.Pane, launch.BranchMore with
        | ChoosingBranch repo, MoreIdle when not (busy launch) && launch.BranchQuery.Trim () = "" ->
            match launch.Branches |> Map.tryFind repo with
            | Some (BranchesLoaded page) -> page.Next |> Option.map (fun next -> repo, next)
            | Some (BranchesUnavailable _) | Some BranchesUnknown | None -> None
        | _ -> None

    /// The branch a row launches on: the one named on it, or the provider's default.
    let branchOf (launch: LaunchViewState) (candidate: RepoCandidate) : string =
        match launch.Named |> Map.tryFind candidate.Repo with
        | Some named when named.Trim () <> "" -> named.Trim ()
        | _ -> candidate.DefaultBranch

    /// What starting asks for: the held repo, and its branch only when it is not the
    /// default (see `LaunchTarget`). Nothing, when nothing is held.
    let target (launch: LaunchViewState) : LaunchTarget option =
        held launch
        |> Option.map (fun candidate ->
            let branch = branchOf launch candidate
            { LaunchTarget.Repo = candidate.Repo
              LaunchTarget.Branch = if branch = candidate.DefaultBranch then None else Some branch })

    /// What pressing Enter on the field means. A link copied from the forge — a hosted URL,
    /// a clone URL — names a repo, and is resolved to a row and held. Anything else, a bare
    /// `owner/name` included, is a search: a name half typed still parses as a name, and a
    /// search shows what it matched where a lookup would fail against it.
    let linkOf (query: string) : RepoLink option =
        let text = query.Trim ()
        let hosted = text.Contains "github.com/" || text.StartsWith "git@github.com:"
        if hosted then RepoLink.parse text else None

    let update (msg: LaunchMsg) (launch: LaunchViewState) : LaunchViewState =
        match msg with
        | LaunchQueryTyped text -> { launch with Query = text }
        // A listing ARRIVING is the start of a new list, so whatever the foot was doing for
        // the old one is over: a page in flight for a search two keystrokes ago must not
        // append itself to what is on screen now.
        | LaunchListingArrived listing -> { launch with Listing = listing; More = MoreIdle }
        | LaunchSelected candidate ->
            if launch.Selected = Some candidate.Repo then { launch with Selected = None }
            else { launch with Selected = Some candidate.Repo; Problem = None }
        | LaunchLinked (candidate, branch) ->
            let listed = candidates launch
            let listing =
                if listed |> List.exists (fun c -> c.Repo = candidate.Repo) then listed
                else candidate :: listed
            { launch with
                Listing = ListingLoaded { RepoPage.Candidates = listing; RepoPage.Next = nextOf launch }
                Selected = Some candidate.Repo
                Named =
                    match branch with
                    | Some branch -> launch.Named |> Map.add candidate.Repo branch
                    | None -> launch.Named |> Map.remove candidate.Repo
                Stage = Choosing
                Problem = None }
        | LaunchMoreStarted -> { launch with More = MoreFetching }
        | LaunchMoreArrived page ->
            match launch.Listing with
            // Only onto the list the page was asked for. A page that lands after the list
            // under it was replaced belongs to a question nobody is asking any more.
            | ListingLoaded seen when launch.More = MoreFetching ->
                let known = seen.Candidates |> List.map (fun c -> c.Repo) |> Set.ofList
                let added = page.Candidates |> List.filter (fun c -> not (known.Contains c.Repo))
                { launch with
                    Listing = ListingLoaded { RepoPage.Candidates = seen.Candidates @ added; RepoPage.Next = page.Next }
                    More = MoreIdle }
            | _ -> launch
        | LaunchMoreFailed reason -> { launch with More = MoreFailed reason }
        | LaunchBranchesArrived (repo, branches) ->
            { launch with Branches = launch.Branches |> Map.add repo branches; BranchMore = MoreIdle }
        // The pane's own query does not survive it. A branch typed on one repo's pane is not
        // a filter over another's, and a pane reopened is a question asked again.
        | LaunchBranchPaneOpened repo -> { launch with Pane = ChoosingBranch repo; BranchQuery = ""; BranchMore = MoreIdle }
        | LaunchBranchPaneClosed -> { launch with Pane = ChoosingRepo; BranchQuery = "" }
        | LaunchBranchQueryTyped text -> { launch with BranchQuery = text }
        | LaunchBranchMoreStarted -> { launch with BranchMore = MoreFetching }
        | LaunchBranchMoreArrived (repo, page) ->
            match launch.Branches |> Map.tryFind repo with
            // Only onto the listing it is a page of, and only while it was asked for — the
            // repo listing's rule, for the same reason.
            | Some (BranchesLoaded seen) when launch.BranchMore = MoreFetching ->
                let known = Set.ofList seen.Names
                let added = page.Names |> List.filter (fun name -> not (known.Contains name))
                { launch with
                    Branches =
                        launch.Branches
                        |> Map.add repo (BranchesLoaded { BranchPage.Names = seen.Names @ added; BranchPage.Next = page.Next })
                    BranchMore = MoreIdle }
            | _ -> launch
        | LaunchBranchMoreFailed reason -> { launch with BranchMore = MoreFailed reason }
        | LaunchBranchNamed (repo, branch) -> { launch with Named = launch.Named |> Map.add repo branch }
        | LaunchResolving link -> { launch with Stage = Resolving link; Problem = None }
        | LaunchSent (request, target) -> { launch with Stage = Sent (request, target); Problem = None; Pane = ChoosingRepo }
        | LaunchAnswered (request, result) ->
            match launch.Stage with
            | Sent (sent, target) when sent = request ->
                match result with
                | CommandAccepted -> { launch with Stage = Cloning target }
                | CommandRejected reason -> { launch with Stage = Choosing; Problem = Some reason }
            | _ -> launch
        | LaunchFailed reason -> { launch with Stage = Choosing; Problem = Some reason }
        | LaunchDismissed -> { launch with Dismissed = true }
