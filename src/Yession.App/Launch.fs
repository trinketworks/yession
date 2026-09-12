namespace Yession.App

open Yession.Domain
open Yession.Domain.Link
open Yession.Domain.Repos

/// The launch surface (the session's first screen): a person choosing which repo this
/// session is FOR, before any turn has run.
///
/// One gesture: tap a repo in the list, or paste what was copied out of the forge's address
/// bar, and the clone begins on the branch shown. There is no "start" and no "back" —
/// a row is the act — and no "start without one", because the composer beside it already is
/// that: say something, and the session has begun without a repo.
///
/// View state, local to this client and never synced — choosing is one person's act on one
/// screen, and what it produces is the `AddRepo` command, whose outcome everybody reads off
/// the log (`RepoAdded`, or `GatedCommandFailed`). The one thing folded back in from the
/// log is that failure, so the screen that asked can say why rather than sit there.
///
/// Pure: what the browser does — fetch a listing, resolve a link, send the command — is in
/// `Browser.fs`, and what it learns comes back through `LaunchMsg`.

/// What the picker has to choose from. Three honest states, for the model catalogue's
/// reason: "not looked yet" and "looked and there is nothing" are different facts, and the
/// difference is what decides whether a person waits or goes and connects an account.
type LaunchListing =
    | ListingUnknown
    | ListingLoaded of RepoCandidate list
    /// The lookup answered, and what it said was why it could not. `SignIn` is whether the
    /// answer was "connect GitHub" — a 401 either way — which is the one failure with a
    /// button rather than a retry.
    | ListingUnavailable of reason: string * signIn: bool

/// One row's branches, asked for when its branch mark is opened and not before: thirty
/// rows are thirty lookups, and nearly every launch is on the default.
type LaunchBranches =
    | BranchesUnknown
    | BranchesLoading
    | BranchesLoaded of string list
    | BranchesUnavailable of reason: string

/// What a launch is FOR: the repo, and the branch when it is not the provider's default.
/// `None` deliberately, rather than the default's name — a launch that names the default
/// is not a switch, and the command should not say it is.
[<RequireQualifiedAccess>]
type LaunchTarget =
    { Repo : RepoRef
      Branch : string option }

/// Where the act stands. `Resolving` is a pasted link being asked about (a pull request's
/// head is a fact only the provider holds); `Sent` carries the request so the session's
/// answer can be told apart from any other command's; `Cloning` is admitted — the answer
/// arrives as events. All but `Choosing` carry the target, so the row that was tapped is
/// the one that says what is happening to it.
type LaunchStage =
    | Choosing
    | Resolving of RepoLink
    | Sent of RequestId * LaunchTarget
    | Cloning of LaunchTarget

type LaunchViewState =
    { /// What was typed into the field. Empty means "my repos".
      Query : string
      Listing : LaunchListing
      /// Each row's branches, by repo, once its mark has been opened.
      Branches : Map<RepoRef, LaunchBranches>
      /// A branch picked on a row, by repo, when it is not the row's default.
      Picked : Map<RepoRef, string>
      Stage : LaunchStage
      /// The last thing that went wrong with a launch — a link that could not be resolved,
      /// a rejection at the door, or a clone that failed — shown until the next attempt.
      Problem : string option }

type LaunchMsg =
    | LaunchQueryTyped of string
    | LaunchListingArrived of LaunchListing
    /// A row's branch mark was opened: its branches are being fetched.
    | LaunchBranchesOpened of RepoRef
    /// Branches for a repo. Carries WHICH repo, so an answer for one row cannot land on
    /// another.
    | LaunchBranchesArrived of RepoRef * LaunchBranches
    | LaunchBranchPicked of RepoRef * string
    /// A pasted link is being asked about before it can be sent.
    | LaunchResolving of RepoLink
    /// The command left, under this request id, for this target.
    | LaunchSent of RequestId * LaunchTarget
    /// The session answered a command; only the one `Sent` names is this surface's.
    | LaunchAnswered of RequestId * SessionCommandResult
    /// An attempt failed — a link the provider could not resolve, or the log saying the
    /// clone did (`GatedCommandFailed` for `add_repo`). Choosing is open again.
    | LaunchFailed of reason: string

module Launch =

    let empty : LaunchViewState =
        { Query = ""
          Listing = ListingUnknown
          Branches = Map.empty
          Picked = Map.empty
          Stage = Choosing
          Problem = None }

    /// Whether the surface is OFFERED: this client is connected, has read the log through
    /// to where the session says it ends, and the session has not BEGUN — no repo in it and
    /// nothing said. A session that has begun is the agent's to add a repo to (Plan 15).
    ///
    /// "Begun" is a repo or a message, deliberately not "anything on the timeline": a launch
    /// that failed leaves its failure on the timeline, and a person whose clone could not
    /// reach the repo needs the picker still there to try another, not a note and a blank.
    ///
    /// The catch-up conditions are what keep it honest: a client that has not looked yet, or
    /// is still reading, has an empty projection too, and a launch screen that flashed over
    /// every cold open of an old session would teach people it means nothing.
    let offered
        (connected: bool)
        (historyRead: bool)
        (latestKnown: EventOffset option)
        (catchingUp: bool)
        (begun: bool)
        : bool =
        connected && historyRead && latestKnown.IsSome && not catchingUp && not begun

    /// Whether the surface is busy with an attempt: rows are not for tapping while one is
    /// under way, because two clones of two repos is not what anyone meant.
    let busy (launch: LaunchViewState) : bool =
        match launch.Stage with
        | Choosing -> false
        | Resolving _ | Sent _ | Cloning _ -> true

    /// The branch a row launches on: the one picked on it, or the provider's default.
    let branchOf (launch: LaunchViewState) (candidate: RepoCandidate) : string =
        launch.Picked |> Map.tryFind candidate.Repo |> Option.defaultValue candidate.DefaultBranch

    /// What tapping a row asks for: its repo, and its branch only when it is not the
    /// default (see `LaunchTarget`).
    let targetOf (launch: LaunchViewState) (candidate: RepoCandidate) : LaunchTarget =
        let branch = branchOf launch candidate
        { LaunchTarget.Repo = candidate.Repo
          LaunchTarget.Branch = if branch = candidate.DefaultBranch then None else Some branch }

    /// What a link asks for, when it can be sent without asking the provider: a repo, or a
    /// branch of one. A pull request is the one that cannot — its head is the provider's to
    /// say — and answers `None` here.
    let targetOfLink (link: RepoLink) : LaunchTarget option =
        match link with
        | RepoLink.Repo repo -> Some { LaunchTarget.Repo = repo; LaunchTarget.Branch = None }
        | RepoLink.Branch (repo, branch) -> Some { LaunchTarget.Repo = repo; LaunchTarget.Branch = Some branch }
        | RepoLink.PullRequest _ -> None

    /// What pressing Enter on the field means. A link copied from the forge — a hosted URL,
    /// a clone URL — is a launch: what was copied is the thing wanted, and a second tap to
    /// confirm it is a tax on the gesture. Anything else, a bare `owner/name` included, is a
    /// search: a name half typed still parses as a name, and a search shows what it matched
    /// where a launch would fail against it.
    let linkOf (query: string) : RepoLink option =
        let text = query.Trim ()
        let hosted =
            text.Contains "github.com/" || text.StartsWith "git@github.com:"
        if hosted then RepoLink.parse text else None

    /// The target under way, when there is one: what the row that was tapped, or the field
    /// that was pasted into, is waiting on.
    let underway (launch: LaunchViewState) : LaunchTarget option =
        match launch.Stage with
        | Sent (_, target)
        | Cloning target -> Some target
        | Choosing
        | Resolving _ -> None

    let update (msg: LaunchMsg) (launch: LaunchViewState) : LaunchViewState =
        match msg with
        | LaunchQueryTyped text -> { launch with Query = text }
        | LaunchListingArrived listing -> { launch with Listing = listing }
        | LaunchBranchesOpened repo -> { launch with Branches = launch.Branches |> Map.add repo BranchesLoading }
        | LaunchBranchesArrived (repo, branches) -> { launch with Branches = launch.Branches |> Map.add repo branches }
        | LaunchBranchPicked (repo, branch) -> { launch with Picked = launch.Picked |> Map.add repo branch }
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
