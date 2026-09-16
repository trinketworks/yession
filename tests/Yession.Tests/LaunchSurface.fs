module Yession.Tests.LaunchSurface

// The launch surface: a person choosing the session's first repo, standing where the
// timeline's first line will.
//
// What is worth pinning is what has to hold whatever the screen looks like:
//
//   * it is OFFERED only to a client that is connected, has read the log to where the
//     session says it ends, and found nothing on it — a cold open of an old session, or a
//     client still reading, must never flash it;
//   * a repo, or anything said, takes it away: once the session has begun, adding a repo
//     is the agent's (Plan 15), and this surface does not come back — while a launch that
//     FAILED leaves it standing, over its own note, for the next try;
//   * what it shows to choose from is named as the provider names it, so what is held is
//     what `add_repo` clones — and holding a row SENDS NOTHING: the one button does, on the
//     row's default unless another branch was named on it;
//   * a link copied from the forge becomes a held row; anything else typed is a search,
//     because a name half typed still parses as a name;
//   * a way out is on the card: dismissed, it steps aside for this client;
//   * the session's answer to THIS command is the one it acts on — another command's
//     rejection is not its problem — and a failed clone reaches the screen that asked;
//   * with no credential, the way out is on the surface (connect GitHub), not a blank list.
//
// Pure, through the model a browser folds and the renderer it serves: the cheap tier.

open System
open Fable.Pyxpecto
open Yession.Domain
open Yession.Domain.Link
open Yession.Domain.Repos
open Yession.Domain.Chat
open Yession.App
open Yession.Tests.Support

let private ada = PeerId.create "ada" |> expect
let private sessionId = SessionId.create "launch-session" |> expect
let private hello = RepoRef.create "octo/hello" |> expect
let private offset (n: int64) = EventOffset.create n |> expect

let private at (n: int64) (event: SessionEvent) : EventEnvelope<SessionEvent> =
    { EventId = EventId.fresh ()
      SessionId = sessionId
      Offset = offset n
      Actor = ActorRef.SessionProcess
      Timestamp = DateTimeOffset (2026, 9, 12, 0, 0, 0, TimeSpan.Zero)
      Event = event }

let private candidate (name: string) : RepoCandidate =
    { RepoCandidate.Repo = RepoRef.create name |> expect
      Description = Some "a thing"
      DefaultBranch = "main"
      Private = false
      PushedAt = None }

/// A client that has connected to a session whose log ends at `latest`, read its own store,
/// and folded `events` — the path a browser takes, in the order it takes it.
/// A branch page, on the listing page's terms.
let private branches (names: string list) (next: string option) : BranchPage =
    { BranchPage.Names = names; BranchPage.Next = next }

/// A listing page: the rows, and whether the provider says there is more. The cursor is
/// opaque to everything on this side, so any string is as good as the real one.
let private page (names: string list) (next: string option) : RepoPage =
    { RepoPage.Candidates = names |> List.map candidate; RepoPage.Next = next }

let private clientAt (latest: int64) (events: EventEnvelope<SessionEvent> list) : ClientModel =
    ClientModel.init { PeerId = ada; DisplayName = "swift-heron" }
    |> ClientModel.update (ConnectedMsg { SessionId = sessionId; AssignedDisplayName = "swift-heron"; LatestOffset = Some (offset latest) })
    |> ClientModel.update HistoryReadMsg
    |> ClientModel.update
        (EventsPageMsg { Events = events; LastOffset = events |> List.tryLast |> Option.map (fun e -> e.Offset); IsEnd = true })

/// A fresh session: created, one peer in — nothing on the timeline.
let private fresh = [ at 0L (SessionCreated { SessionCreated.SessionId = sessionId }); at 1L (PeerJoined { PeerId = ada; DisplayName = "swift-heron"; User = None }) ]

let private launch (msg: LaunchMsg) (model: ClientModel) = ClientModel.update (LaunchMsg msg) model

let private offeredTests =
    testList "when it is offered" [

        testCase "a connected client that has read an empty log to its end is offered it" <| fun () ->
            Expect.isTrue (ClientModel.launchOffered (clientAt 1L fresh)) "offered"
            Expect.stringContains (render (clientAt 1L fresh)) "data-repo-picker=\"choosing\"" "and it is rendered"

        testCase "a client that has not connected is not, whatever it has read" <| fun () ->
            let unconnected =
                ClientModel.init { PeerId = ada; DisplayName = "swift-heron" }
                |> ClientModel.update HistoryReadMsg
            Expect.isFalse (ClientModel.launchOffered unconnected) "the command it produces needs a session to send it to"

        testCase "a client still reading is not: the log may hold the repo it is about to see" <| fun () ->
            // Connected to a session whose log runs past what this client has folded.
            let behind = clientAt 40L fresh
            Expect.isTrue behind.EventConsumer.IsCatchingUp "still catching up"
            Expect.isFalse (ClientModel.launchOffered behind) "not offered over an unread log"
            Expect.isFalse ((render behind).Contains "data-repo-picker") "and not drawn"

        testCase "a repo, or anything said, takes it away" <| fun () ->
            let begun =
                clientAt 2L (fresh @ [ at 2L (RepoAdded { MessageId = MessageId.create "r1" |> expect; Repo = hello; Branch = "main"; Actor = PeerRef ada }) ])
            Expect.isFalse (ClientModel.launchOffered begun) "a repo note is the session having begun"
            let spoken =
                clientAt 2L (fresh @ [ at 2L (MessageSent { MessageId = MessageId.create "m1" |> expect; QueueId = None; Author = Principal.Peer ada; Body = "hi" }) ])
            Expect.isFalse (ClientModel.launchOffered spoken) "so is a message"

        testCase "dismissed, it steps aside for this client" <| fun () ->
            let dismissed = clientAt 1L fresh |> launch LaunchDismissed
            Expect.isFalse (ClientModel.launchOffered dismissed) "dismissed"
            Expect.isFalse ((render dismissed).Contains "data-repo-picker") "and gone from the screen"
    ]

let private choosingTests =
    testList "choosing" [

        testCase "the candidates are offered under the provider's names" <| fun () ->
            let listed = clientAt 1L fresh |> launch (LaunchListingArrived (ListingLoaded (page [ "trinketworks/yession"; "octo/hello" ] None)))
            let html = render listed
            Expect.stringContains html "data-repo-candidate=\"trinketworks/yession\"" "the first, by canonical name"
            Expect.stringContains html "data-repo-candidate=\"octo/hello\"" "and the second"

        testCase "with no credential, the way out is on the surface" <| fun () ->
            let signIn = clientAt 1L fresh |> launch (LaunchListingArrived (ListingUnavailable ("connect GitHub to list your repositories", true)))
            Expect.stringContains (render signIn) "data-repo-picker-connect" "a button to the settings face"
            let other = clientAt 1L fresh |> launch (LaunchListingArrived (ListingUnavailable ("github could not be reached", false)))
            Expect.isFalse ((render other).Contains "data-repo-picker-connect") "which a fault that is not a sign-in does not offer"

        testCase "holding a row sends nothing; the button does, on the default unless another branch is named" <| fun () ->
            let listed = clientAt 1L fresh |> launch (LaunchListingArrived (ListingLoaded (page [ "octo/hello" ] None)))
            Expect.equal (Launch.target listed.Launch) None "nothing held, nothing to start"
            let heldRow = listed |> launch (LaunchSelected (candidate "octo/hello"))
            Expect.equal heldRow.Launch.Stage Choosing "still choosing: holding is a state"
            Expect.equal (Launch.target heldRow.Launch) (Some { LaunchTarget.Repo = hello; LaunchTarget.Branch = None }) "the default is not a switch, and the command does not name it"
            let named = heldRow |> launch (LaunchBranchNamed (hello, "feature/x"))
            Expect.equal (Launch.target named.Launch) (Some { LaunchTarget.Repo = hello; LaunchTarget.Branch = Some "feature/x" }) "another is"
            let letGo = named |> launch (LaunchSelected (candidate "octo/hello"))
            Expect.equal (Launch.target letGo.Launch) None "tapped again, let go"

        testCase "the held row carries the branch field; no other row does" <| fun () ->
            let heldRow =
                clientAt 1L fresh
                |> launch (LaunchListingArrived (ListingLoaded (page [ "octo/hello"; "octo/other" ] None)))
                |> launch (LaunchSelected (candidate "octo/hello"))
            let html = render heldRow
            let rowOf (name: string) =
                let at = html.IndexOf (sprintf "data-repo-candidate=\"%s\"" name)
                html.Substring (at, min 200 (html.Length - at))
            Expect.stringContains (rowOf "octo/hello") "aria-pressed=\"true\"" "the row says it is held"
            Expect.stringContains (rowOf "octo/other") "aria-pressed=\"false\"" "the other does not"
            Expect.stringContains html "data-repo-candidate-branch=\"octo/hello\"" "and carries the branch field"
            Expect.isFalse (html.Contains "data-repo-candidate-branch=\"octo/other\"") "not the other's"

        testCase "branches that arrive for one row do not land on another" <| fun () ->
            let other = RepoRef.create "octo/other" |> expect
            let model =
                clientAt 1L fresh
                |> launch (LaunchListingArrived (ListingLoaded (page [ "octo/hello"; "octo/other" ] None)))
                |> launch (LaunchBranchesArrived (hello, BranchesLoaded (branches [ "main"; "stale" ] None)))
            Expect.equal (model.Launch.Branches |> Map.tryFind other) None "other's are still unknown"

        // What replaced a case that pinned the opposite: the list used to be truncated to
        // four rows behind a `more` button, and the held one spliced back in wherever it had
        // been. Every row the client holds is now drawn, and what is not held is not on the
        // client yet.
        testCase "every row the listing holds is drawn" <| fun () ->
            let many = [ 1 .. 8 ] |> List.map (sprintf "octo/repo-%d")
            let html = render (clientAt 1L fresh |> launch (LaunchListingArrived (ListingLoaded (page many None))))
            for name in many do
                Expect.stringContains html (sprintf "data-repo-candidate=\"%s\"" name) "drawn"

        testCase "the foot stands exactly while the provider says there is more" <| fun () ->
            let ended = clientAt 1L fresh |> launch (LaunchListingArrived (ListingLoaded (page [ "octo/hello" ] None)))
            Expect.isFalse ((render ended).Contains "data-repo-picker-foot") "a listing that ended has nothing to reach"
            let more = clientAt 1L fresh |> launch (LaunchListingArrived (ListingLoaded (page [ "octo/hello" ] (Some "cursor-2"))))
            Expect.stringContains (render more) "data-repo-picker-foot" "one that has not, does"

        testCase "the cursor is asked for once, not once per look" <| fun () ->
            let more = clientAt 1L fresh |> launch (LaunchListingArrived (ListingLoaded (page [ "octo/hello" ] (Some "cursor-2"))))
            Expect.equal (Launch.wanting more.Launch) (Some "cursor-2") "the foot asks with what the page carried"
            let asking = more |> launch LaunchMoreStarted
            Expect.equal (Launch.wanting asking.Launch) None "and not again while that page is in flight"

        testCase "a page lands after the rows already read, and brings its own cursor" <| fun () ->
            let arrived =
                clientAt 1L fresh
                |> launch (LaunchListingArrived (ListingLoaded (page [ "octo/one"; "octo/two" ] (Some "cursor-2"))))
                |> launch LaunchMoreStarted
                |> launch (LaunchMoreArrived (page [ "octo/two"; "octo/three" ] (Some "cursor-3")))
            Expect.equal
                (Launch.candidates arrived.Launch |> List.map (fun c -> RepoRef.value c.Repo))
                [ "octo/one"; "octo/two"; "octo/three" ]
                "appended, and a row the provider repeated across a page boundary is not listed twice"
            Expect.equal (Launch.wanting arrived.Launch) (Some "cursor-3") "the new page's cursor is what the foot asks with next"

        testCase "a page that lands on a list it is not a page of is dropped" <| fun () ->
            let searched =
                clientAt 1L fresh
                |> launch (LaunchListingArrived (ListingLoaded (page [ "octo/one" ] (Some "cursor-2"))))
                |> launch LaunchMoreStarted
                // The reader typed, and the search answered, while the page was in flight.
                |> launch (LaunchListingArrived (ListingLoaded (page [ "found/by-name" ] None)))
                |> launch (LaunchMoreArrived (page [ "octo/two" ] None))
            Expect.equal
                (Launch.candidates searched.Launch |> List.map (fun c -> RepoRef.value c.Repo))
                [ "found/by-name" ]
                "what is on screen is what was asked for last"

        testCase "a page that did not come says so and leaves the rows alone" <| fun () ->
            let failed =
                clientAt 1L fresh
                |> launch (LaunchListingArrived (ListingLoaded (page [ "octo/one" ] (Some "cursor-2"))))
                |> launch LaunchMoreStarted
                |> launch (LaunchMoreFailed "github is rate limiting this credential")
            Expect.equal (Launch.candidates failed.Launch |> List.map (fun c -> RepoRef.value c.Repo)) [ "octo/one" ] "the rows read stay read"
            Expect.stringContains (render failed) "data-repo-picker-again" "with a way to ask again"
            Expect.equal (Launch.wanting failed.Launch) None "which the foot will not do on its own"

        testCase "the branch pane is a pane OF a repo, and what it picks is what the command names" <| fun () ->
            let held =
                clientAt 1L fresh
                |> launch (LaunchListingArrived (ListingLoaded (page [ "octo/hello" ] None)))
                |> launch (LaunchSelected (candidate "octo/hello"))
                |> launch (LaunchBranchesArrived (hello, BranchesLoaded (branches [ "main"; "fix/thing" ] None)))
            Expect.equal held.Launch.Pane ChoosingRepo "the card opens on the repositories"
            let onBranches = held |> launch (LaunchBranchPaneOpened hello)
            Expect.equal onBranches.Launch.Pane (ChoosingBranch hello) "and goes to the branches OF the row held"
            Expect.stringContains (render onBranches) "data-repo-branch=\"fix/thing\"" "which are the ones it has"
            let picked = onBranches |> launch (LaunchBranchNamed (hello, "fix/thing")) |> launch LaunchBranchPaneClosed
            Expect.equal picked.Launch.Pane ChoosingRepo "picking comes back"
            Expect.equal
                (Launch.target picked.Launch)
                (Some { LaunchTarget.Repo = hello; LaunchTarget.Branch = Some "fix/thing" })
                "with the branch on the command"

        testCase "a name the provider does not have is offered, because switch_branch makes one" <| fun () ->
            let typing =
                clientAt 1L fresh
                |> launch (LaunchListingArrived (ListingLoaded (page [ "octo/hello" ] None)))
                |> launch (LaunchSelected (candidate "octo/hello"))
                |> launch (LaunchBranchesArrived (hello, BranchesLoaded (branches [ "main"; "fix/thing" ] None)))
                |> launch (LaunchBranchPaneOpened hello)
                |> launch (LaunchBranchQueryTyped "fix/")
            Expect.equal (Launch.branchesOn typing.Launch hello) [ "fix/thing" ] "what is typed narrows what is listed"
            Expect.equal (Launch.namingNew typing.Launch hello) (Some "fix/") "and stands as a name of its own"
            let exact = typing |> launch (LaunchBranchQueryTyped "main")
            Expect.equal (Launch.namingNew exact.Launch hello) None "a name the provider HAS is not a new one"

        testCase "the branch pane does not page into a search, because the provider has no branch search" <| fun () ->
            let opened =
                clientAt 1L fresh
                |> launch (LaunchListingArrived (ListingLoaded (page [ "octo/hello" ] None)))
                |> launch (LaunchSelected (candidate "octo/hello"))
                |> launch (LaunchBranchesArrived (hello, BranchesLoaded (branches [ "main" ] (Some "branch-2"))))
                |> launch (LaunchBranchPaneOpened hello)
            Expect.equal (Launch.wantingBranches opened.Launch) (Some (hello, "branch-2")) "the foot asks with what the page carried"
            let narrowing = opened |> launch (LaunchBranchQueryTyped "fi")
            Expect.equal (Launch.wantingBranches narrowing.Launch) None "and stands down while what is listed is a filter over what arrived"

        testCase "a branch pane reopened is a question asked again" <| fun () ->
            let reopened =
                clientAt 1L fresh
                |> launch (LaunchListingArrived (ListingLoaded (page [ "octo/hello" ] None)))
                |> launch (LaunchSelected (candidate "octo/hello"))
                |> launch (LaunchBranchPaneOpened hello)
                |> launch (LaunchBranchQueryTyped "fix/")
                |> launch LaunchBranchPaneClosed
                |> launch (LaunchBranchPaneOpened hello)
            Expect.equal reopened.Launch.BranchQuery "" "what was typed on it does not survive it"

        testCase "a link copied from the forge is resolved; anything else typed is a search" <| fun () ->
            Expect.equal (Launch.linkOf "https://github.com/octo/hello/tree/fix") (Some (RepoLink.Branch (hello, "fix"))) "a branch page"
            Expect.equal (Launch.linkOf "git@github.com:octo/hello.git") (Some (RepoLink.Repo hello)) "a clone url"
            Expect.equal (Launch.linkOf "octo/hello") None "a bare name is a search: half of one still parses as one"
            Expect.equal (Launch.linkOf "hello") None "so is a word"

        testCase "a resolved link is a held row, at the head of the list if it was not in it" <| fun () ->
            let resolving = clientAt 1L fresh |> launch (LaunchListingArrived (ListingLoaded (page [ "octo/other" ] None))) |> launch (LaunchResolving (RepoLink.PullRequest (hello, 42)))
            Expect.stringContains (render resolving) "data-repo-picker=\"resolving\"" "the card says it is asking"
            let linked = resolving |> launch (LaunchLinked (candidate "octo/hello", Some "fix/thing"))
            Expect.equal linked.Launch.Stage Choosing "choosing again, with the row held"
            Expect.equal (Launch.target linked.Launch) (Some { LaunchTarget.Repo = hello; LaunchTarget.Branch = Some "fix/thing" }) "on the branch the link named"
            Expect.equal (Launch.candidates linked.Launch |> List.map (fun c -> c.Repo)) [ hello; RepoRef.create "octo/other" |> expect ] "at the head"
            let failed = resolving |> launch (LaunchFailed "github does not show that repository to this credential")
            Expect.equal failed.Launch.Stage Choosing "a link that could not be resolved opens choosing again"
            Expect.stringContains (render failed) "data-repo-picker-problem" "with the reason on the card"
    ]

let private answerTests =
    testList "the session's answer" [

        let request = RequestId.fresh ()
        let target = { LaunchTarget.Repo = hello; LaunchTarget.Branch = None }
        let waiting =
            clientAt 1L fresh
            |> launch (LaunchListingArrived (ListingLoaded (page [ "octo/hello"; "octo/other" ] None)))
            |> launch (LaunchSelected (candidate "octo/hello"))
            |> launch (LaunchSent (request, target))

        testCase "a rejection at the door is shown, and choosing is open again" <| fun () ->
            let rejected = ClientModel.update (CommandAnsweredMsg (request, CommandRejected "this session already has octo/other")) waiting
            Expect.equal rejected.Launch.Stage Choosing "back to choosing"
            Expect.equal rejected.Launch.Problem (Some "this session already has octo/other") "with the reason"
            Expect.stringContains (render rejected) "data-repo-picker-problem" "on the surface"

        testCase "another command's answer is not this surface's" <| fun () ->
            let other = ClientModel.update (CommandAnsweredMsg (RequestId.fresh (), CommandRejected "no")) waiting
            Expect.equal other.Launch.Stage (Sent (request, target)) "still waiting on its own"
            Expect.equal other.Launch.Problem None "and nothing to say"

        testCase "once the add is sent, the card steps aside for the timeline" <| fun () ->
            // The switch happens when the add STARTS, not when it lands: a sent add is
            // committed, so the card is no longer offered and the timeline — where the
            // add shows and its outcome lands, as an agent's add_repo does — takes over.
            Expect.isTrue (Launch.committed waiting.Launch) "sent is committed"
            Expect.isFalse (ClientModel.launchOffered waiting) "so the card is not offered"
            Expect.isFalse ((render waiting).Contains "data-repo-picker") "and nothing of it is drawn"

        testCase "admitted, the card stays aside while the clone runs, on the row that was tapped" <| fun () ->
            let admitted = ClientModel.update (CommandAnsweredMsg (request, CommandAccepted)) waiting
            Expect.equal admitted.Launch.Stage (Cloning target) "cloning"
            Expect.isTrue (Launch.committed admitted.Launch) "still committed"
            Expect.isFalse (ClientModel.launchOffered admitted) "so the card stays aside — the timeline is showing the add"
            Expect.isTrue (Launch.busy admitted.Launch) "and no row is for holding meanwhile"

        testCase "a clone that failed reaches the screen that asked, while it is waiting" <| fun () ->
            let admitted = ClientModel.update (CommandAnsweredMsg (request, CommandAccepted)) waiting
            let failed =
                ClientModel.update
                    (EventsPageMsg
                        { Events =
                            [ at 2L
                                  (GatedCommandFailed
                                      { MessageId = MessageId.create "f1" |> expect
                                        Tool = "add_repo"
                                        Summary = "add_repo octo/hello"
                                        Author = PeerRef ada
                                        Reason = "github says not found" }) ]
                          LastOffset = Some (offset 2L)
                          IsEnd = true })
                    admitted
            Expect.equal failed.Launch.Problem (Some "github says not found") "the reason, off the log"
            Expect.equal failed.Launch.Stage Choosing "and choosing is open again"
            // The failure is also the timeline's first line, and the surface stays above it:
            // a session with no repo and nothing said has not begun, and the next attempt is
            // a click rather than a conversation.
            Expect.isTrue (ClientModel.launchOffered failed) "still offered over its own failure"
            let html = render failed
            Expect.stringContains html "data-repo-picker=\"choosing\"" "drawn"
            Expect.stringContains html "failed add_repo octo/hello" "beside the timeline's own account of it"
    ]

let tests = testList "Launch surface" [ offeredTests; choosingTests; answerTests ]
