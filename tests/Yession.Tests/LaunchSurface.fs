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
            let listed = clientAt 1L fresh |> launch (LaunchListingArrived (ListingLoaded [ candidate "trinketworks/yession"; candidate "octo/hello" ]))
            let html = render listed
            Expect.stringContains html "data-repo-candidate=\"trinketworks/yession\"" "the first, by canonical name"
            Expect.stringContains html "data-repo-candidate=\"octo/hello\"" "and the second"

        testCase "with no credential, the way out is on the surface" <| fun () ->
            let signIn = clientAt 1L fresh |> launch (LaunchListingArrived (ListingUnavailable ("connect GitHub to list your repositories", true)))
            Expect.stringContains (render signIn) "data-repo-picker-connect" "a button to the settings face"
            let other = clientAt 1L fresh |> launch (LaunchListingArrived (ListingUnavailable ("github could not be reached", false)))
            Expect.isFalse ((render other).Contains "data-repo-picker-connect") "which a fault that is not a sign-in does not offer"

        testCase "holding a row sends nothing; the button does, on the default unless another branch is named" <| fun () ->
            let listed = clientAt 1L fresh |> launch (LaunchListingArrived (ListingLoaded [ candidate "octo/hello" ]))
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
                |> launch (LaunchListingArrived (ListingLoaded [ candidate "octo/hello"; candidate "octo/other" ]))
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
                |> launch (LaunchListingArrived (ListingLoaded [ candidate "octo/hello"; candidate "octo/other" ]))
                |> launch (LaunchBranchesArrived (hello, BranchesLoaded [ "main"; "stale" ]))
            Expect.equal (model.Launch.Branches |> Map.tryFind other) None "other's are still unknown"

        testCase "a few rows are shown, and the held one is among them wherever it is" <| fun () ->
            let many = [ 1 .. 8 ] |> List.map (fun n -> candidate (sprintf "octo/repo-%d" n))
            let listed = clientAt 1L fresh |> launch (LaunchListingArrived (ListingLoaded many))
            let html = render listed
            Expect.stringContains html "data-repo-candidate=\"octo/repo-4\"" "the first few"
            Expect.isFalse (html.Contains "data-repo-candidate=\"octo/repo-5\"") "not the rest"
            Expect.stringContains html "data-repo-picker-more" "which are behind more"
            let heldLate = listed |> launch (LaunchSelected (candidate "octo/repo-7"))
            Expect.stringContains (render heldLate) "data-repo-candidate=\"octo/repo-7\"" "a row held from a search stays shown after the list shrinks"
            let expanded = listed |> launch LaunchExpanded
            Expect.stringContains (render expanded) "data-repo-candidate=\"octo/repo-8\"" "and more shows them all"

        testCase "a link copied from the forge is resolved; anything else typed is a search" <| fun () ->
            Expect.equal (Launch.linkOf "https://github.com/octo/hello/tree/fix") (Some (RepoLink.Branch (hello, "fix"))) "a branch page"
            Expect.equal (Launch.linkOf "git@github.com:octo/hello.git") (Some (RepoLink.Repo hello)) "a clone url"
            Expect.equal (Launch.linkOf "octo/hello") None "a bare name is a search: half of one still parses as one"
            Expect.equal (Launch.linkOf "hello") None "so is a word"

        testCase "a resolved link is a held row, at the head of the list if it was not in it" <| fun () ->
            let resolving = clientAt 1L fresh |> launch (LaunchListingArrived (ListingLoaded [ candidate "octo/other" ])) |> launch (LaunchResolving (RepoLink.PullRequest (hello, 42)))
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
            |> launch (LaunchListingArrived (ListingLoaded [ candidate "octo/hello"; candidate "octo/other" ]))
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

        testCase "admitted, the surface waits on the clone, on the row that was tapped" <| fun () ->
            let admitted = ClientModel.update (CommandAnsweredMsg (request, CommandAccepted)) waiting
            Expect.equal admitted.Launch.Stage (Cloning target) "cloning"
            Expect.stringContains (render admitted) "data-repo-picker=\"cloning\"" "and says so"
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
