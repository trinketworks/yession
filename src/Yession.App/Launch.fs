namespace Yession.App

open Yession.Domain
open Yession.Domain.Link
open Yession.Domain.Repos

/// The launch surface (the session's first screen): a person choosing which repo this
/// session is FOR, before any turn has run.
///
/// View state, local to this client and never synced — choosing is one person's act on one
/// screen, and what it produces is the `AddRepo` command, whose outcome everybody reads off
/// the log (`RepoAdded`, or `GatedCommandFailed`). The one thing folded back in from the
/// log is that failure, so the screen that asked can say why rather than sit there.
///
/// Pure: what the browser does — fetch a listing, send the command — is in `Browser.fs`,
/// and what it learns comes back through `LaunchMsg`.

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

type LaunchBranches =
    | BranchesUnknown
    | BranchesLoaded of string list
    | BranchesUnavailable of reason: string

/// The repo somebody has picked, and which of its branches — the provider's default until
/// they say otherwise.
type LaunchChoice =
    { Repo : RepoCandidate
      Branches : LaunchBranches
      Branch : string }

/// Where the act stands. `Sent` carries the request so the session's answer can be told
/// apart from any other command's; `Cloning` is admitted — the answer arrives as events.
type LaunchStage =
    | Choosing
    | Sent of RequestId
    | Cloning

type LaunchViewState =
    { /// What was typed into the search. Empty means "my repos".
      Query : string
      Listing : LaunchListing
      Choice : LaunchChoice option
      Stage : LaunchStage
      /// The last thing that went wrong with a launch — a rejection at the door, or a clone
      /// that failed — shown until the next attempt. Cleared by choosing again.
      Problem : string option
      /// "Start without a repo" was pressed: the surface steps aside for this client, and
      /// the ordinary empty timeline is what is left.
      Dismissed : bool }

type LaunchMsg =
    | LaunchQueryTyped of string
    | LaunchListingArrived of LaunchListing
    | LaunchChosen of RepoCandidate
    | LaunchUnchosen
    /// Branches for a repo. Carries WHICH repo, so an answer for a choice since abandoned
    /// cannot land on the one after it.
    | LaunchBranchesArrived of RepoRef * LaunchBranches
    | LaunchBranchPicked of string
    /// The command left, under this request id.
    | LaunchSent of RequestId
    /// The session answered a command; only the one `Sent` names is this surface's.
    | LaunchAnswered of RequestId * SessionCommandResult
    /// The log said the clone failed (`GatedCommandFailed` for `add_repo`).
    | LaunchFailed of reason: string
    | LaunchDismissed

module Launch =

    let empty : LaunchViewState =
        { Query = ""
          Listing = ListingUnknown
          Choice = None
          Stage = Choosing
          Problem = None
          Dismissed = false }

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
        (launch: LaunchViewState)
        : bool =
        connected
        && historyRead
        && latestKnown.IsSome
        && not catchingUp
        && not begun
        && not launch.Dismissed

    let update (msg: LaunchMsg) (launch: LaunchViewState) : LaunchViewState =
        match msg with
        | LaunchQueryTyped text -> { launch with Query = text }
        | LaunchListingArrived listing -> { launch with Listing = listing }
        | LaunchChosen candidate ->
            { launch with
                Choice = Some { Repo = candidate; Branches = BranchesUnknown; Branch = candidate.DefaultBranch }
                Problem = None }
        | LaunchUnchosen -> { launch with Choice = None }
        | LaunchBranchesArrived (repo, branches) ->
            match launch.Choice with
            | Some choice when choice.Repo.Repo = repo -> { launch with Choice = Some { choice with Branches = branches } }
            | _ -> launch
        | LaunchBranchPicked branch ->
            match launch.Choice with
            | Some choice -> { launch with Choice = Some { choice with Branch = branch } }
            | None -> launch
        | LaunchSent request -> { launch with Stage = Sent request; Problem = None }
        | LaunchAnswered (request, result) ->
            match launch.Stage with
            | Sent sent when sent = request ->
                match result with
                | CommandAccepted -> { launch with Stage = Cloning }
                | CommandRejected reason -> { launch with Stage = Choosing; Problem = Some reason }
            | _ -> launch
        | LaunchFailed reason -> { launch with Stage = Choosing; Problem = Some reason }
        | LaunchDismissed -> { launch with Dismissed = true }

    /// The branch to ask for, or none when the provider's default was left alone: a choice
    /// that names the default is not a switch, and the command should not say it is.
    let branchToAsk (choice: LaunchChoice) : string option =
        if choice.Branch = choice.Repo.DefaultBranch then None else Some choice.Branch
