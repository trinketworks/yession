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
    | BranchesLoaded of string list
    | BranchesUnavailable of reason: string

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
      Dismissed : bool }

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
          Branches = Map.empty
          Named = Map.empty
          Stage = Choosing
          Problem = None
          Dismissed = false }

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

    /// Whether the card is OFFERED: this client is connected, has read the log through
    /// to where the session says it ends, and the session has not BEGUN — no repo in it and
    /// nothing said. A session that has begun is the agent's to add a repo to (Plan 15).
    ///
    /// "Begun" is a repo or a message, deliberately not "anything on the timeline": a launch
    /// that failed leaves its failure on the timeline, and a person whose clone could not
    /// reach the repo needs the card still there to try another, not a note and a blank.
    ///
    /// The catch-up conditions are what keep it honest: a client that has not looked yet, or
    /// is still reading, has an empty projection too, and a launch card that flashed over
    /// every cold open of an old session would teach people it means nothing. Committed is
    /// the other half of the same rule: once the add is under way the card is not offered,
    /// so the switch to the timeline happens when the add STARTS, not when it lands.
    let offered
        (connected: bool)
        (historyRead: bool)
        (latestKnown: EventOffset option)
        (catchingUp: bool)
        (begun: bool)
        (launch: LaunchViewState)
        : bool =
        connected && historyRead && latestKnown.IsSome && not catchingUp && not begun
        && not launch.Dismissed && not (committed launch)

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
        | LaunchBranchesArrived (repo, branches) -> { launch with Branches = launch.Branches |> Map.add repo branches }
        | LaunchBranchNamed (repo, branch) -> { launch with Named = launch.Named |> Map.add repo branch }
        | LaunchResolving link -> { launch with Stage = Resolving link; Problem = None }
        | LaunchSent (request, target) -> { launch with Stage = Sent (request, target); Problem = None }
        | LaunchAnswered (request, result) ->
            match launch.Stage with
            | Sent (sent, target) when sent = request ->
                match result with
                | CommandAccepted -> { launch with Stage = Cloning target }
                | CommandRejected reason -> { launch with Stage = Choosing; Problem = Some reason }
            | _ -> launch
        | LaunchFailed reason -> { launch with Stage = Choosing; Problem = Some reason }
        | LaunchDismissed -> { launch with Dismissed = true }
