namespace Yession.App

open Yession.Domain.Access
open Yession.Domain
open Yession.Domain.Sandboxes
open Yession.Domain.Agent
open Yession.Domain.Link
open Yession.Domain.Terminals
open Yession.Domain.Collab
open Yession.App.Collab
open Yession.Domain.Tools
open Yession.Domain.Chat
open Yession.Domain.Content
open Yession.Domain.Artifacts
open Yession.Domain.Prs
open Yession.App.Codecs

/// The App Elmish model and update loop shell. It holds a single typed
/// snapshot of what the client knows: the local peer, connection state, synced
/// collaborative state, the conversation projection, the event-consumer read position,
/// and the agent view state. See docs/technical-design.md §2.1, §2.3.

type ConnectionState =
    /// Not connected and not trying. Carries WHY whenever the client knows — a rejected
    /// token — because a bare "disconnected" is the same dead end the event feed used to
    /// be: a true statement that helps nobody. A session that could not be reached is never
    /// this: it is `Retrying`, because it is being tried again.
    | Disconnected of reason: string option
    | Connecting
    | Connected
    | Reconnecting
    /// Not connected, and still trying: the last attempt to reach the session (`/me`, or
    /// opening the transport) failed for `reason`, and `failures` attempts have failed in a row. Distinct from `Disconnected`
    /// because what a person should take from it differs — not "this is broken" but "this is
    /// being worked on" — and from `Connecting` because it has news: why, and how long.
    | Retrying of reason: string * failures: int

type PeerState = { PeerId : PeerId; DisplayName : string }

/// The durable event feed's health — the leg that carries HISTORY, which in the browser is
/// HTTP by cursor — immutable ranges kept in the client's own store — rather than the data
/// channel. Deliberately separate from `ConnectionState`: either leg can be down while the
/// other works, and neither takes the client with it. Collaborative state is CRDT state in a
/// local doc, so a dead feed costs history, not the ability to read, write, or send
/// (docs/technical-design.md §1, local-first).
type FeedHealth =
    /// The last read succeeded — history is current.
    | FeedLive
    /// A read failed and the resilience policy is still trying. `attempt` is how many have
    /// failed so far; `reason` is the fault, for the degraded banner.
    | FeedRetrying of attempt: int * reason: string
    /// The policy gave up. History is whatever is already local, and stays that way until
    /// the next availability hint or reconnect re-arms the read — editing keeps working.
    | FeedStalled of reason: string

/// How far the client has consumed the event log versus what it knows exists.
type EventConsumerState =
    { LastProcessedOffset : EventOffset option
      LatestKnownOffset   : EventOffset option
      IsCatchingUp        : bool
      /// Whether catch-up has lasted long enough to be worth SAYING. Sending a message puts
      /// the client behind its own event for a round trip, so `IsCatchingUp` is true for a
      /// few dozen milliseconds every time anyone sends anything — and a status that flips
      /// to "catching up" and back on every send is a flicker, not information. The truth
      /// stays in `IsCatchingUp` (the read loop reads it); this is what the UI reports.
      ///
      /// Set by the timer the model declares while catch-up runs (`ClientModel.timers`),
      /// which stops when it ends, so the threshold is one number in one place
      /// (`ClientModel.catchUpQuietMs`). It can only ever be true WHILE catching up — the
      /// reducer holds that as an invariant of the state, whoever dispatches.
      CatchUpIsSlow       : bool
      /// Whether reads are getting through at all. `IsCatchingUp` says there is more to
      /// read; this says whether reading is possible — the distinction the old design had
      /// no way to express, because a failed fetch was reported as an empty final page.
      Feed                : FeedHealth
      /// Where this client's KEPT history resumes, when the boot replay could not walk to
      /// it (Plan 20): everything between `LastProcessedOffset` and this offset is not on
      /// this device. `None` is the ordinary state — nothing kept, or everything kept in
      /// one unbroken run.
      ///
      /// A fact about the STORE, never about the feed: it is settled before a single read
      /// leaves this client, so it can neither prove nor deny that history is arriving. The
      /// feed repairs it — a read resumes at the cursor, which the replay parked at exactly
      /// the offset the fill has to start from — which is why any page off the network
      /// clears it. Reported as feed health, it flashed a red "history paused" over every
      /// cold open with an out-of-order store, moments before the first page fixed it.
      MissingBefore       : EventOffset option
      /// The event read this client has out, by its number (`ClientModel.ReadsAsked`), or
      /// `None` when nothing is out. At most one at a time (`ClientModel.reads`), and only the
      /// answer that carries this number settles it — so an answer from a connection that has
      /// since ended neither clears a read the next one asked nor asks one of its own.
      Reading             : int option }

/// How far one message the agent is writing has got: the message, and how much of its body
/// has arrived. Every delta is text, so a body only ever grows while it streams, and two
/// stamps of one message are equal exactly when nothing arrived between them.
[<RequireQualifiedAccess>]
type WritingStamp = { Message : MessageId; Length : int }

type AgentViewState =
    { ActiveTurn : AgentTurnId option
      /// The stamp at which the agent's writing was last seen to have sat still for
      /// `ClientModel.writingQuietMs` (`AgentQuietMsg`). A fact about one exact body: it
      /// holds while the message's stamp is still this one and means nothing once a word
      /// lands, so nothing ever has to clear it.
      Quiet : WritingStamp option
      /// The turn `InterruptTurnMsg` was last clicked for, until the stop it asked for
      /// actually lands — `AgentTurnInterrupted`/`AgentTurnFailed`/`AgentMessageCompleted`
      /// all clear it alongside `ActiveTurn`, and so does a fresh `AgentTurnStarted` (a stale
      /// click from the turn before has nothing left to mean). Colocated with `ActiveTurn`
      /// rather than kept as a top-level flag because it is a fact about THIS turn's
      /// lifecycle and every place that ends a turn must already be the one place that
      /// clears it. Round-trip-only: the click is idempotent server-side regardless, so
      /// nothing is lost if this is ever wrong, and it exists purely so the button reads as
      /// pressed rather than broken while the stop is in flight.
      Interrupting : AgentTurnId option }

/// Where the Claude sign-in flow is (Plan 08). `ClaudeAwaitingCode` = the authorize
/// tab is open; completion may land at the Manager's callback (the panel polls status)
/// or arrive as a pasted code.
///
/// This axis is about a HUMAN being somewhere else — approving in another tab — and nothing
/// else. Whether a command of ours is in flight, and whether one failed, is the panel's
/// `Pending`: they were cases here (`ClaudeBusy`, `ClaudeError`) and the two axes kept
/// overwriting each other, which is how "the command was accepted" came to be stored as
/// "there is nothing left to wait for".
type ClaudeFlowState =
    | ClaudeIdle
    /// `scope` remembers the sign-in choice ("session" | "mine") the flow began with,
    /// so a pasted-code completion targets the same credential slot.
    | ClaudeAwaitingCode of authorizeUrl: string * scope: string

/// What the status query must SHOW for a connection command to have landed.
///
/// Data rather than a predicate: it lives in the model, so the model stays comparable and a
/// red test can print what the panel was waiting for. `Scope` is the panel's own vocabulary
/// ("mine" | "session") — the word the command was sent with — rather than a second one to
/// keep in step with it.
type ConnectionExpectation =
    { Scope : string
      /// `true` after a connect, `false` after a disconnect. Both are waits, and a
      /// disconnect that waited for "a credential is there" would never end.
      Connected : bool }

module ConnectionExpectation =

    /// Has the query shown it? Read off the two rows a panel has, because those rows ARE
    /// what the wait is about: the credential appearing (or going) under the scope the
    /// person chose. A scope neither panel offers reads as the shared one, which is the
    /// same default the command was sent under.
    let landed
        (expect: ConnectionExpectation)
        (sessionCredential: CredentialRow option)
        (mineCredential: CredentialRow option)
        : bool =
        let credential = if expect.Scope = "session" then sessionCredential else mineCredential
        credential.IsSome = expect.Connected

/// The Claude panel's client-side rules, beside the state they govern. The panel itself is
/// `Yession.Domain.Access.ClaudePanel` — one shape the session encodes and this reads.
module ClaudePanel =

    /// A panel as it should be FOLDED over the one this client already had — which, the
    /// first time, is none.
    ///
    /// A panel that says nothing about models — a lookup still in flight — must not blank a
    /// picker that has a list. Everything else is replaced: the arriving panel IS the
    /// answer, and a row it stopped naming is a credential that is gone.
    let keeping (known: ClaudePanel option) (arrived: ClaudePanel) : ClaudePanel =
        match arrived.Models, known with
        | ModelsUnknown, Some known -> { arrived with Models = known.Models }
        | _ -> arrived

    /// This panel's wait rule, beside the status it reads, so no caller composes it.
    let landed (expect: ConnectionExpectation) (panel: ClaudePanel) : bool =
        ConnectionExpectation.landed expect panel.SessionCredential panel.MineCredential

[<RequireQualifiedAccess>]
type ClaudeViewState =
    { /// The panel as the session last said it, or `None` while the read stream has not
      /// said anything yet. ONE case for "not told", rather than an option per field that
      /// every reader had to answer for separately.
      Status : ClaudePanel option
      Flow : ClaudeFlowState
      /// A command of ours on its way into `Status`, modelled rather than assumed.
      Pending : Pending<ConnectionExpectation>
      /// The panel's fields as typed: which credential a sign-in is for ("mine" |
      /// "session"), the code pasted back from claude.ai, and a pasted setup token or key.
      /// Held here rather than read off the document at the press, so the rule refusing an
      /// empty one sits with the state it reads. This client's own: none of it is synced.
      Scope : string
      Code : string
      Token : string }

/// One write the Claude panel asks the session for: the action, what it says, and what the
/// status has to show before the panel calls it done (`None` for a sign-in, which answers
/// with an authorize URL and waits on a human instead).
[<RequireQualifiedAccess>]
type ClaudeCall =
    { Action : ClaudeAction
      Request : ClaudeRequest
      Expect : ConnectionExpectation option }

/// The Claude panel's four presses.
[<RequireQualifiedAccess>]
type ClaudePress =
    | Connect
    | Complete
    | SaveToken
    | Disconnect of scope: string

/// What a Claude panel write came back with: refused with the session's reason, an
/// authorize URL to open, or accepted with nothing to show yet.
type ClaudeAnswer = Result<string option, string>

/// Where the GitHub sign-in flow is (Plan 14). Device flow: the panel shows a user
/// code, the human approves it on github.com in their own tab, and the browser polls
/// the session (which polls GitHub) until the grant lands.
///
/// Like Claude's, this axis is only about the human being elsewhere; a command of ours in
/// flight is the panel's `Pending`.
type GitHubFlowState =
    | GitHubIdle
    /// The code is on screen. `scope` remembers the sign-in choice ("session" |
    /// "mine"); `interval` is GitHub's polling pace in seconds, which `slow_down`
    /// replies may widen mid-flow.
    | GitHubAwaitingApproval of userCode: string * verificationUri: string * scope: string * interval: int

module GitHubFlow =

    /// Does a failed poll END the flow, or is the code on screen still good?
    ///
    /// The distinction is the whole difference between a sign-in that survives a lift and one
    /// that has to be started again in it. A 4xx is the SESSION saying this flow is over —
    /// the code expired, the human denied it, there is nothing pending for that scope — and
    /// nothing but starting again will do. Anything else is the session or the network having
    /// a bad moment: a 5xx, a proxy, a phone changing radios (`0`, a fetch that never
    /// answered). The device code outlives all of those, so the panel keeps waiting rather
    /// than throwing away a code the human may already have approved.
    let ended (status: int) : bool = status >= 400 && status < 500

/// The GitHub panel's client-side rule, for Claude's reason.
module GitHubPanel =

    /// This panel's wait rule, beside the status it reads, so no caller composes it.
    let landed (expect: ConnectionExpectation) (panel: GitHubPanel) : bool =
        ConnectionExpectation.landed expect panel.SessionCredential panel.MineCredential

/// Where the device flow's poll is while a code is on screen. The poll is a COMMAND — it is
/// what makes GitHub hand the grant over — and this tab is the one asking, at the interval
/// GitHub sets, until the grant lands, the human cancels, or the session says the flow ended.
type GitHubPolling =
    /// Waiting out the interval before asking. `round` keys the wait (`ClientModel.timers`),
    /// so each answer arms a new one and an answer to an old round is recognised as one.
    | PollWaiting of round: int
    /// The poll for `round` is out; its answer arms the next wait or ends the flow.
    | PollAsking of round: int
    /// The grant landed. Nothing is asked again: the Manager's frame reaches this session,
    /// the session tells every open drawer, and that status closes the flow.
    | PollGranted

/// What one poll came back with.
type GitHubPollAnswer =
    /// The session said this flow is over — the code expired, the human denied it, nothing is
    /// pending for that scope (`GitHubFlow.ended`) — with its reason. Or said something this
    /// client cannot read, which is the same ending: asking again gets the same reply.
    | PollEnded of reason: string
    /// A bad moment, not an ending: a 5xx, a proxy, a fetch that never answered. The code on
    /// screen, which the human may already have approved, is still good.
    | PollFailed
    /// Still waiting, at the interval the session says GitHub now asks for.
    | PollPending of interval: int
    | PollConnected

[<RequireQualifiedAccess>]
type GitHubViewState =
    { /// As Claude's, for its reason.
      Status : GitHubPanel option
      Flow : GitHubFlowState
      /// A command of ours on its way into `Status`, modelled rather than assumed.
      Pending : Pending<ConnectionExpectation>
      /// The panel's fields as typed, for Claude's reason: which credential a sign-in is for,
      /// and a pasted token. This client's own; none of it is synced.
      Scope : string
      Token : string
      /// The device flow's poll, while `Flow` has a code on screen.
      Polling : GitHubPolling }

/// One write the GitHub panel asks the session for, as `ClaudeCall` is for Claude's.
[<RequireQualifiedAccess>]
type GitHubCall =
    { Action : GitHubAction
      Request : GitHubRequest
      Expect : ConnectionExpectation option }

/// The GitHub panel's three presses.
[<RequireQualifiedAccess>]
type GitHubPress =
    | Connect
    | SaveToken
    | Disconnect of scope: string

/// What a GitHub panel write came back with: refused with the session's reason, a device
/// flow begun (the code to show), or accepted with nothing to show yet.
type GitHubAnswer = Result<GitHubFlowState option, string>

/// The generated read surface's state (Plan 15), folded from the `/queries` stream.
///
/// There is no `Busy` and no `Error` here, and their absence is the design rather than an
/// omission: this surface has no actions, so nothing can be in flight, and a query that
/// cannot be answered simply keeps its last known value rather than blanking. Whatever
/// went wrong went wrong for the AGENT, which is where it is actionable.
type QueriesViewState =
    { /// What this session declares. Empty until the stream's opening frame — a client
      /// renders the sections it is told about, never a list it was compiled with.
      Declared : QueryDef list
      /// The latest value per query name. Absent = not answered yet.
      Values : Map<string, QueryValue> }

/// One copy to the clipboard, as its confirmation shows it: the box it came out of, and which
/// copy this is. The count is what makes a second copy of the same box a new moment, so its
/// wait starts again rather than the first copy's deadline taking the second's confirmation
/// off the screen.
[<RequireQualifiedAccess>]
type Copy = { Box : string; Nth : int }

/// Where the refusal notice is drawn (`ClientModel.refusalMount`). Two mounts and ONE notice:
/// which of them draws it is a decision over the model, made once, so the same refusal cannot
/// be on the screen twice.
[<RequireQualifiedAccess>]
type RefusalMount =
    /// The content pane, under its strip: where every terminal verb is pressed.
    | Pane
    /// The conversation column, under its header: where everything else is pressed, and
    /// where a terminal verb's refusal goes when the pane is shut.
    | Chat

/// A terminal's keyboard taken from THIS client by somebody else (`ClientModel.Stolen`): which
/// terminal, and who has it now.
///
/// News for one person. The log says a steal to everybody alike — a lease ended, `LeaseStolen`
/// — and the lease bar renames its holder for everybody alike; but only the person it was
/// taken from was in the middle of something, and for them the bar changing a name under the
/// screen they were typing into was the whole of the announcement.
[<RequireQualifiedAccess>]
type StolenLease = { Terminal : TerminalId; TakenBy : ActorRef }

/// What the session last REFUSED (`ClientModel.Refused`): its own sentence, and the two
/// things the client needs to say it well.
///
/// The REASON rather than the command it answered: a rejection carries a sentence written to
/// be read ("there is no sandbox named 'octo/hello:dev' in this session — there is …"), and the
/// surface that shows it has nothing to add. What the command was is used, not shown — to put
/// the sentence where the press was (`FromPane`).
[<RequireQualifiedAccess>]
type Refusal =
    { Reason : string
      /// Whether what was refused was one of the content pane's own verbs, which are pressed
      /// there and so are answered there while the pane is open. `false` for an answer to a
      /// request this client has no record of sending.
      FromPane : bool
      /// Which mount had the keyboard inside it, as the notice itself last said
      /// (`RefusalFocusMsg`). Per mount rather than a flag, because the notice can change
      /// mounts under a reader (the pane shut or opened), and a flag set by the old mount's
      /// element would claim focus the new one never had.
      FocusedIn : RefusalMount option }

module Refusal =

    /// Whether `command` is pressed in the content pane — a terminal verb, every one of which
    /// lives there (the strip, its menu, the switcher, the lease bar, a lost terminal's band).
    let fromPane (command: SessionCommand) : bool =
        match command with
        | OpenTerminal _
        | CloseTerminal _
        | InterruptTerminal _
        | TakeTerminalLease _
        | ReleaseTerminalLease _
        | RearmTerminal _
        | ReattachTerminal _ -> true
        | InterruptAgentTurn _
        | ApproveRepoCapabilities _
        | AddRepo _ -> false

    /// Where `refusal` is drawn: in the pane while the pane is open and the refusal is one
    /// of its verbs' — that is where the press was — and in the conversation otherwise,
    /// including a pane verb whose pane has since been shut, because a notice behind a shut
    /// column is a notice nobody reads.
    let mount (terminalsOpen: bool) (refusal: Refusal) : RefusalMount =
        if refusal.FromPane && terminalsOpen then RefusalMount.Pane else RefusalMount.Chat
/// Which draft the composer has open. `Unchosen` is the state a fresh client is in, and the only
/// one where the DEFAULT applies (join the draft already in flight rather than start a rival) —
/// once someone picks, the pick stands, so "new message" is not undone by a peer starting to type.
type ComposerChoice =
    | Unchosen
    | Own
    | Joined of PeerId

/// Where a remote peer IS: the peer's name (for the cursor label), the `Focus` its caret is in
/// when it is in one, and what it has open in the pane. Ephemeral presence, delivered over
/// `Presence` frames — never synced through Yjs, never durable. The peer's colour is derived
/// from who it is (`Entity.presenceColour`), not carried.
///
/// Both halves are optional and an entry exists while EITHER holds, because they are genuinely
/// independent: someone reading an artifact is typing nowhere, and someone typing in the
/// composer has no pane open. `Focus` stopped being total when viewing arrived, and every site
/// that assumed a present peer had a caret is a site the compiler named.
type RemotePresence =
    { DisplayName : string
      Focus : Focus option
      Viewing : ViewRef option }

/// One terminal's live transcript as this client has it (Plan 13). Records are keyed by
/// their sequence number, which makes application idempotent by construction: the same
/// record arriving twice — once as a live frame, once inside a fetched chunk — is one map
/// entry, exactly as an event at a known offset folds once. That is the whole reason the
/// live leg and the history leg can be different transports and still agree.
type TerminalFeed =
    { Records : Map<int, TranscriptRecord>
      /// The transcript length this client believes the session has, from availability
      /// hints and from the records it has seen. Drives catch-up the way
      /// `LatestKnownOffset` drives the event feed.
      KnownLength : int
      /// How far a contiguous prefix has been read. Fetching resumes here.
      ReadThrough : int
      /// The transcript's own header, once chunk 0 has been fetched (Plan 13, stage 3e).
      /// The replay rebuilds a `.cast` from these records, and the recorded width and height
      /// are what make it come out the shape the terminal actually was.
      Header : TranscriptHeader option
      /// The store answered a read from line 0 with nothing: it holds no recording of this
      /// terminal. Kept apart from an empty feed because that is also what a reader who has
      /// not asked yet holds, and the two say opposite things (`RecordingKnown`).
      Unrecorded : bool }

/// One fetched page of a terminal's transcript (Plan 13): the records it carried with their
/// sequence numbers, and whether the transcript continues past it.
type TranscriptPage =
    { Records : (int * TranscriptRecord) list
      /// The transcript's own header, when this page carried it (line 0, so the
      /// answer from a client with nothing yet, and no other). Kept rather than
      /// discarded because the replay view (Plan 13, stage 3e) rebuilds a `.cast`
      /// from these records, and a `.cast` without its header is not one — the
      /// recorded width and height are what make a replay come out the shape the
      /// terminal actually was.
      Header : TranscriptHeader option
      /// One past the last line this page covered.
      NextSeq : int
      /// A capped answer means the server had more to give; anything shorter — a
      /// `204`, which arrives here as no lines at all — is the transcript's tail.
      IsEnd : bool }

/// A transcript read this client has out for one terminal (`ClientEffect.ReadTranscript`):
/// its number (`ClientModel.ReadsAsked`), the line it asked from, and whether a signal that
/// there is more arrived while it was out — which earns ONE more read once it lands
/// (`ClientModel.reads`).
[<RequireQualifiedAccess>]
type TranscriptRead = { Read : int; From : int; Owed : bool }

module TerminalFeed =

    let empty : TerminalFeed =
        { Records = Map.empty; KnownLength = 0; ReadThrough = 0; Header = None; Unrecorded = false }

    /// What this feed says of the recording behind it. Anything held, or any length heard,
    /// is a recording — whatever an earlier answer said, because a line cannot arrive from a
    /// recording that does not exist.
    let known (feed: TerminalFeed) : RecordingKnown =
        if feed.KnownLength > 0 || not (Map.isEmpty feed.Records) then RecordingKnown.Recorded
        elif feed.Unrecorded then RecordingKnown.NotRecorded
        else RecordingKnown.NotYetKnown

    /// Fold one record in. Out-of-order and duplicate records are both fine — the map key
    /// is the sequence number.
    let withRecord (seq: int) (record: TranscriptRecord) (feed: TerminalFeed) : TerminalFeed =
        { feed with
            Records = Map.add seq record feed.Records
            KnownLength = max feed.KnownLength (seq + 1) }

    /// The records in `[fromSeq, toSeq)`, in order — one block's output.
    ///
    /// Asked for by RANGE rather than filtered out of the whole feed, because the caller is a
    /// render and the renders are not one. `terminalBlockView` calls this once per block, so
    /// `Map.toList |> List.filter` — which materialises every record the terminal has ever
    /// held, to keep the handful in one block's range — cost the whole transcript per block,
    /// and the whole transcript times every block per render.
    ///
    /// The range walk is bounded by the range instead: finished blocks ask for their own
    /// fixed span, and their sum over a render is the transcript ONCE. In a browser, over a
    /// transcript cut into ten-record blocks, one render's worth of this: 0.4ms per render at
    /// 400 records, 4.6ms at 1,500 and 70.2ms at 6,000 the old way, against 0.5 / 0.8 / 2.5
    /// the new one. What matters is not the 28x at the far end but the SHAPE — the old cost
    /// grew 175x across that sweep and this one grows 5x — and the shape is what the
    /// `transcript.read.slope` metric in `tests/Yession.Tests/Bench.fs` exists to watch, since
    /// a number taken at one size cannot tell the two apart.
    ///
    /// Gaps are ordinary — a running block's `toSeq` runs to `KnownLength`, which availability
    /// hints move ahead of what this device has actually fetched — so a missing sequence
    /// number is skipped rather than being the end of the range.
    let slice (fromSeq: int) (toSeq: int) (feed: TerminalFeed) : TranscriptRecord list =
        [ for seq in max 0 fromSeq .. toSeq - 1 do
            match Map.tryFind seq feed.Records with
            | Some record -> record
            | None -> () ]

    /// The output text of a range: the `o`/`e` records concatenated. Input and resize
    /// records are excluded — a replay shows what was typed, a block's OUTPUT does not.
    let outputText (fromSeq: int) (toSeq: int) (feed: TerminalFeed) : string =
        slice fromSeq toSeq feed
        |> List.filter (fun r -> r.Kind = TranscriptOutput || r.Kind = TranscriptStderr)
        |> List.map (fun r -> r.Data)
        |> String.concat ""

    /// How many of its LAST lines a block shows in the page. The rest is in the recording,
    /// which plays the whole of it; what the page draws is bounded by this and by nothing a
    /// command printed.
    ///
    /// Bounded because a render draws every block, and a block is redrawn on every record
    /// that lands while it runs. Drawn whole, `seq 100000` was a hundred thousand lines
    /// re-parsed and re-diffed per record — 170 renders, a million DOM nodes at the end, and a
    /// main thread held for minutes, long enough for the link heartbeat to go unanswered and
    /// the Session to drop a peer that was alive. A bound is the only answer that holds for
    /// the next command, which will print more.
    ///
    /// Two thousand lines is more than any screen shows and well inside what a render can
    /// carry once per record; a terminal's own scrollback is often less.
    let shownLines = 2000

    /// The last `keep` lines of `text`, and how many lines came before them. A line ends at
    /// `\n` (a `\r\n` is one break, and a bare `\r` rewrites the line it is on, which is how
    /// `Ansi.parse` reads the same bytes), and a trailing partial line is a line.
    ///
    /// Cut at a line break, so the cut can never split an escape sequence — none contains one.
    /// What it CAN cut is the colour a line before the cut set and did not reset; the shown
    /// lines then start in the plain style until they set their own. That is the honest
    /// price of not parsing a hundred thousand lines to find out.
    let lastLines (keep: int) (text: string) : int * string =
        let keep = max 1 keep
        // Walk back over `keep` line breaks, not counting one that ends the text: that break
        // closes the last line rather than starting another.
        let mutable at = if text.EndsWith "\n" then text.Length - 1 else text.Length
        let mutable found = 0
        let mutable cut = -1
        while cut < 0 && at > 0 do
            let nl = text.LastIndexOf ('\n', at - 1)
            if nl < 0 then at <- 0
            else
                found <- found + 1
                if found = keep then cut <- nl + 1 else at <- nl
        if cut < 0 then 0, text
        else
            // Every line before the cut ends in a break, so the breaks are the count.
            let mutable elided = 0
            let mutable i = text.IndexOf '\n'
            while i >= 0 && i < cut do
                elided <- elided + 1
                i <- text.IndexOf ('\n', i + 1)
            elided, text.Substring cut

    /// How many of its last lines a block shows in the pane's HISTORY until a reader asks for
    /// the rest: a block is one command in a list of them, and a list one command can fill is
    /// not a history. Drawn whole, `seq 1 300` was about five thousand pixels of scroll that
    /// pushed every other command out of reach.
    ///
    /// Twenty is about half a pane at full height (`BlockGroup.unfoldedLines` is the whole
    /// of one), so a command that prints a page still shows what it ended on beside the one
    /// before it. The END, because that is where a terminal's output is read from: the last
    /// lines are what the command left on screen. The rest is one control away, in place.
    let paneLines = 20

    /// The lines of `text`, counted as `lastLines` reads them: a line ends at `\n`, and a
    /// trailing partial line is a line.
    let lineCount (text: string) : int =
        if text = "" then 0
        else (text |> Seq.filter (fun c -> c = '\n') |> Seq.length) + (if text.EndsWith "\n" then 0 else 1)

    /// What a block shows of a range's output — its last `keep` lines, never more than
    /// `shownLines` — and how many lines before them it leaves out. The preview wants all the
    /// page draws (`shownLines`); the pane's history wants `paneLines` unless asked for more.
    let shownOutput (keep: int) (fromSeq: int) (toSeq: int) (feed: TerminalFeed) : int * string =
        outputText fromSeq toSeq feed |> lastLines (min keep shownLines)

/// Something opened from the chat to READ, laid over the terminal it belongs to (P2-1): one
/// command and what it printed, one stretch of somebody holding a terminal's keyboard, or one
/// file from the session's content root.
///
/// Not a tab. These used to be tabs beside the terminals, with a pin to keep the ones worth
/// keeping, and a reader could not tell which tab was a terminal and which a glance at one
/// command: a block opened from the chat looked like the terminal it came from, and six chips
/// tapped left six tabs and no terminal. The strip is terminals now (`ClientModel.Tabs`), and
/// one of these is shown at a time, OVER the strip's selected terminal, with its own way back.
[<RequireQualifiedAccess>]
type PreviewSubject =
    /// One block, read-only: the command and what it printed. Opened by tapping its chip.
    | Block of TerminalId * BlockId
    /// One stretch of live mode. Opened from its chat item.
    | Stretch of TerminalStretch
    /// One file from the session's content root — an artifact today. Belongs to no terminal,
    /// which is why `terminal` below is not total.
    | Content of ContentRef

module PreviewSubject =

    /// Its identity, and the value its DOM hooks carry. Prefixed per kind because a block id
    /// and a content path are drawn from overlapping alphabets, and a collision would
    /// silently answer for the wrong thing. The same spelling the chat chip's own hooks are
    /// found by (`PaneShell.toChatItem`), which is what lets "back" return to the chip.
    let key =
        function
        | PreviewSubject.Block (id, blockId) -> "block:" + TerminalId.value id + ":" + BlockId.value blockId
        | PreviewSubject.Stretch stretch -> "stretch:" + TerminalStretch.key stretch
        | PreviewSubject.Content ref -> "content:" + ContentRef.value ref

    /// The chat chip that opens this, as the hook it carries and that hook's value — what
    /// "back" returns focus to (`PaneShell.toChatItem`). A file's chips are found by the file
    /// they name; several may, and each opens the same preview.
    let chip =
        function
        | PreviewSubject.Block (_, blockId) -> Dom.Hooks.chatBlock, BlockId.value blockId
        | PreviewSubject.Stretch stretch -> Dom.Hooks.chatStretch, TerminalStretch.key stretch
        | PreviewSubject.Content ref -> Dom.Hooks.content, ContentRef.value ref

    /// Which terminal this is about — the terminal it is laid over, and the feed a replay of
    /// it reads. `None` for a file, which has no feed, no header and nothing to rewind.
    let terminal =
        function
        | PreviewSubject.Block (id, _) -> Some id
        | PreviewSubject.Stretch stretch -> Some stretch.TerminalId
        | PreviewSubject.Content _ -> None

    /// What having this up says to everyone else (`ViewRef`). A block and a stretch report
    /// their terminal, because somebody asking who else is here is asking about the terminal
    /// and not about which of its commands each of them is reading.
    let view =
        function
        | PreviewSubject.Block (id, _) -> ViewingTerminal id
        | PreviewSubject.Stretch stretch -> ViewingTerminal stretch.TerminalId
        | PreviewSubject.Content ref -> ViewingFile ref

/// One preview, and which read of it is up: its text, or its recording because the reader
/// asked (`Plays`). A stretch plays without being asked and a file never does — both rules
/// about the subject, asked where they are decided (`ClientModel.previewPlays`).
[<RequireQualifiedAccess>]
type Preview =
    { Subject : PreviewSubject
      Plays : bool }

module Preview =

    /// A subject as a chip opens it: its text, where it has one.
    let ofSubject (subject: PreviewSubject) : Preview =
        { Preview.Subject = subject; Preview.Plays = false }


/// Which read of a TERMINAL the pane is showing (Plan 25, stage 2): the reader's POSITION —
/// which terminal, and where in its history — and their FIDELITY — the text of it, or the
/// recording — as ONE fact.
///
/// They were four fields that had to agree (`PaneChoice`, `PanePlaying`, `PaneRewound`,
/// `TerminalList`), and every message cleared the subset its author had in mind. Here every
/// transition states the whole next mode, so a half-cleared state is not a bug to find, it is
/// a value that cannot be written.
///
/// What is NOT here is what the reader did not choose. A closed terminal with nothing but a
/// recording plays without being asked — a rule about the terminal, folded where the terminal
/// is, and `terminalPlays` is the one place the reader's choice and that rule meet.
type TerminalMode =
    /// A terminal's text read: its scrollback, or its live screen.
    | Reading of TerminalId
    /// A terminal's recording, because the reader asked for it.
    | Watching of TerminalId
    /// A terminal's recording, entered FROM one of its blocks, and starting at that command.
    ///
    /// The block's IDENTITY rather than the transcript line it starts at: the line, and the
    /// time the player needs, are derived from the projection when the recording is assembled
    /// (`terminalReplay`), so a hint cannot go stale against blocks that arrived after it.
    | WatchingFrom of TerminalId * BlockId
    /// A LIVE terminal watched from behind its edge — the DVR — carrying the transcript length
    /// the rewind pinned. A pin exists only in this case, which is what makes "pinned to a
    /// block's recording" unwritable rather than merely unwritten.
    | WatchingBehind of TerminalId * pin: int
    /// A terminal's TEXT, positioned at one of its commands — "show in terminal" (Plan 25,
    /// stage 3). The same scrollback, scrolled to the command and marking it.
    | ReadingAt of TerminalId * BlockId

module TerminalMode =

    /// Which terminal this mode is about.
    let terminal =
        function
        | Reading terminal
        | Watching terminal
        | WatchingFrom (terminal, _)
        | WatchingBehind (terminal, _)
        | ReadingAt (terminal, _) -> terminal

    /// Whether this mode is a recording rather than a text read — the reader's half of
    /// `ClientModel.terminalPlays`.
    let watches =
        function
        | Reading _ | ReadingAt _ -> false
        | Watching _ | WatchingFrom _ | WatchingBehind _ -> true

    /// The command a terminal's text read is positioned at, if it is positioned at one — what
    /// the reveal scrolls to.
    let anchor =
        function
        | ReadingAt (terminal, blockId) -> Some (terminal, blockId)
        | Reading _ | Watching _ | WatchingFrom _ | WatchingBehind _ -> None

    /// The OTHER read of the same thing — what the one watch/read toggle dispatches.
    ///
    /// Position is navigation and fidelity is a mode, so flipping the mode never moves the
    /// reader: a watch entered at a command comes back to that command's text, and the way
    /// back out is the same control in the same slot.
    ///
    /// Total, because a terminal with only one read never renders the toggle (a closed
    /// terminal that ran nothing, `ReplayIsTheRead`). That row is unreachable and still
    /// stated, because a partial function here would be a crash waiting for the surface to
    /// change its mind.
    let toggled =
        function
        | Reading terminal -> Watching terminal
        | Watching terminal -> Reading terminal
        // The anchor survives the flip, in both directions: watching from a command comes
        // back to that command's text rather than to the top of a scrollback.
        | ReadingAt (terminal, blockId) -> WatchingFrom (terminal, blockId)
        | WatchingFrom (terminal, blockId) -> ReadingAt (terminal, blockId)
        // "Live". A pin is a fact about watching from behind an edge, so it dies with the
        // watch rather than being carried into a read that has no use for it.
        | WatchingBehind (terminal, _) -> Reading terminal

/// What a terminal finished while this person was not looking at it (`ClientModel.unseen`):
/// whether everything since went through, or something did not. One person's news, never a
/// fact about the terminal — the next person to open the session has seen none of it.
[<RequireQualifiedAccess>]
type Unseen =
    | Succeeded
    | Failed

/// What a terminal is, to the person reading (`ClientModel.terminalState`) — the one answer
/// its mark wears (`View.terminalMark`) and the `all` page's filters count and choose by
/// (`TerminalKind.ofState`), so the mark beside a row and the filter it answers to cannot
/// disagree.
[<RequireQualifiedAccess>]
type TerminalState =
    /// It has ended. Wins over everything: what a terminal is doing NOW is the first thing
    /// to say about it, and a closed one is doing nothing.
    | Closed
    /// A command is running in it.
    | Running
    /// It finished something since this person last looked, and something failed.
    | UnseenFailed
    /// It finished something since this person last looked, and all of it went through.
    | UnseenOk
    /// Its last command did not succeed, and they have seen that.
    | Failed
    /// Open, nothing running, nothing new, nothing wrong — most of them.
    | Idle

/// What the `all` page can be narrowed to, besides everything (`ListFilter`): a kind of
/// terminal, by what it needs of a person.
[<RequireQualifiedAccess>]
type TerminalKind =
    | Attention
    | Running
    | Idle
    | Closed

module TerminalKind =
    /// The order the filters are offered in: what needs a person first, then what is
    /// running, then what is idle, then what has ended.
    let order = [ TerminalKind.Attention; TerminalKind.Running; TerminalKind.Idle; TerminalKind.Closed ]

    /// Something to go and read — news, or a failure — is the attention kind, whether or
    /// not it has been seen: a seen failure still wears the red mark, and the filter says
    /// what the mark says. Running and closed are what the terminal is doing now, so they
    /// win over its news, exactly as on the mark.
    let ofState (state: TerminalState) : TerminalKind =
        match state with
        | TerminalState.UnseenFailed
        | TerminalState.UnseenOk
        | TerminalState.Failed -> TerminalKind.Attention
        | TerminalState.Running -> TerminalKind.Running
        | TerminalState.Idle -> TerminalKind.Idle
        | TerminalState.Closed -> TerminalKind.Closed

/// The `all` page's filter (F5): every terminal, or one kind of them — and, under a kind,
/// WHICH rows it shows, decided rather than read live.
///
/// A list somebody is pressing in holds still (`ClientModel.terminalRows`), and a filter read
/// live would break that the same way the old open-first order did: a running terminal that
/// finished would vanish from "running" under the pointer, and the next row's kill slide up
/// into its place. So the rows are decided when the filter is CHOSEN, and again when the page
/// is opened (`ClientModel.holdFilter`); a row whose terminal changes after that stays where
/// it is, wearing its new mark, until the reader chooses again or comes back. A terminal that
/// did not exist when it was decided is added, at the end, the first time it matches — and
/// once added, it is held like the rest.
///
/// View state, and not remembered across a reload (`PaneMemory`): a reload is a fresh look,
/// and the page is what answers "what is here" to it (`openOfItself`) — a filter nobody
/// remembers choosing would answer that with some of it. What it narrows by is a moment
/// besides (running, unseen), not a preference worth keeping.
[<RequireQualifiedAccess>]
type ListFilter =
    | All
    /// `shown`: the rows it shows. `decided`: every terminal it has judged — those there were
    /// when it was decided, and each later one it has since taken — so a terminal it already
    /// left out is never added by changing.
    | Only of kind: TerminalKind * shown: Set<TerminalId> * decided: Set<TerminalId>

/// The pane's one face (Plan 25, stage 2; P2-1): a terminal, or a preview over one.
///
/// The census of every terminal used to be a third face here (`OnList`), a destination the
/// pane went to and came back from. It is the SWITCHER now (P2-2), the pivot's `all` page,
/// laid over whichever face is up (`ClientModel.Switcher`) rather than replacing it — so the
/// face under it is still there to return to, exactly as it was left.
type PaneMode =
    | OnTerminal of TerminalMode
    /// Something opened from the chat, laid over the terminal it belongs to (`under`, the read
    /// of that terminal that "back" returns to — positioned where it was).
    ///
    /// This is a preview again, and the last one was removed for a fault worth naming: it sat
    /// outside `Tabs` LOOKING like every other tab, so a terminal reached from the chat had a
    /// tab with no close and nothing on screen saying why. The difference is not a slot in the
    /// model but a surface. A preview is never drawn in the strip: it has its own head, with
    /// the way back and a close, and Escape takes it down. So the invariant the old one lacked
    /// is this one: a preview is on screen exactly while `Pane` is `Previewing`, and `Tabs`
    /// never holds one — it cannot, being a list of terminals.
    | Previewing of Preview * under: TerminalMode option

module PaneMode =

    /// The terminal read this face is ABOUT — the one showing, or the one a preview is laid
    /// over. What the pane's furniture (the strip, the composer, presence) reads, because
    /// those answer "which terminal am I working with" rather than "what is on screen".
    let subject =
        function
        | OnTerminal mode -> Some mode
        | Previewing (_, under) -> under

    /// The terminal read on screen, if one is: `None` under a preview.
    let showing =
        function
        | OnTerminal mode -> Some mode
        | Previewing _ -> None

    /// The preview on screen, if one is.
    let preview =
        function
        | Previewing (preview, _) -> Some preview
        | OnTerminal _ -> None

/// What a pane tab's player should be handed (Plan 14, stage 4) — a whole recording, or a
/// range of one, plus the things the stock player already knows how to do with it.
type PaneReplay =
    { /// The `.cast` text, ready to mount — chapters included, as `"m"` events written into
      /// the recording (`TranscriptReplay.castWithMarkers`) rather than handed to the player
      /// beside it. The player compresses idle time in the EVENTS it loads and would leave a
      /// marker list on the uncompressed clock; in the file, a chapter moves with the
      /// records around it.
      Cast : string
      /// Where to start playing — how a watch entered from a command lands on that command
      /// in full context, without slicing anything. In the recording's own clock: the player
      /// maps it onto the compressed one itself.
      StartAt : float option
      /// Where the player rests until somebody presses play, in seconds on ITS clock — the
      /// one it reports positions and durations in, with the idle gaps squeezed out
      /// (`TranscriptReplay.landing` computes one). The screen, the timer and the progress bar
      /// all say so, and pressing play carries on from there. A seek rather than a `Poster` and
      /// a `StartAt` because those two are not on one clock, and a still at a position the
      /// start does not reach is a screen that goes blank the moment play is pressed.
      LandedAt : float option
      /// The time whose frame becomes the still shown before anyone presses play.
      ///
      /// Fed by replaying events while `time < poster`, so a poster asking for the frame at
      /// time T shows the one BEFORE it. Every poster here means "the screen as it stood
      /// after that record", so each is nudged past its record rather than landing on it.
      Poster : float option
      /// Set when this cast is a LIVE terminal watched from behind its edge (Plan 14,
      /// stage 7): it ends where the rewind pinned it, not where the terminal is, so
      /// playing past its end means the reader has caught up — the mount answers by
      /// jumping back to live rather than stopping on a stale frame.
      BehindLive : TerminalId option }

/// What a fold is a fold OF — the key its open state is kept under. One type for the
/// timeline's three and the pane's one, because they are one control: a mark, a title on
/// the line, and something that unfolds beneath. A key per KIND rather than one string
/// namespace, so an act and a call that happened to share an id could never open together.
[<RequireQualifiedAccess>]
type FoldKey =
    /// An act's particulars, under its note.
    | Act of MessageId
    /// A run of a turn's tool calls, under "used n tools" — keyed by its FIRST call, not
    /// the turn: a turn that spoke between two runs of calls has two rows, and one key for
    /// both would open both.
    | ToolRun of ToolUseId
    /// One call's input and output, under its line.
    | ToolCall of ToolUseId
    /// One agent burst's commands, under "ran n commands" — keyed by the turn, since a task
    /// card groups everything the turn ran into one row rather than one per call.
    | Task of AgentTurnId
    /// A run of one actor's commands in a terminal's PANE (`BlockGroup.Run`), keyed by the
    /// run's first block. The pane's fold rather than the timeline's, but kept here for the
    /// timeline's reason: it was the `<details>` element's own state, so every render that
    /// rebuilt the element — a hand-back, a preview laid over the terminal and taken down, a
    /// lone block becoming a run — shut what the reader had opened.
    | Commands of TerminalId * BlockId
    /// One block's output in the pane's history, shown past `TerminalFeed.paneLines`. Open is
    /// the reader having asked for it, and it is that block's alone.
    | Output of TerminalId * BlockId

module FoldKey =

    /// The id an element carries for it — what `aria-controls` names and a test finds.
    let value (key: FoldKey) : string =
        match key with
        | FoldKey.Act id -> "act-" + MessageId.value id
        | FoldKey.ToolRun id -> "run-" + ToolUseId.value id
        | FoldKey.ToolCall id -> "call-" + ToolUseId.value id
        | FoldKey.Task id -> "task-" + AgentTurnId.value id
        | FoldKey.Commands (terminal, leader) -> "commands-" + TerminalId.value terminal + "-" + BlockId.value leader
        | FoldKey.Output (terminal, block) -> "output-" + TerminalId.value terminal + "-" + BlockId.value block

/// How a terminal's history is drawn: each block on its own, or a run of them under one
/// "ran n earlier commands" fold — the pane's version of the chat's task card.
[<RequireQualifiedAccess>]
type BlockGroup =
    /// One block, drawn whole.
    | Alone of Block
    /// Two or more consecutive finished blocks by one actor, oldest first, folded — keyed
    /// by the first, which is the one block a run never loses as it grows.
    | Run of leader: Block * rest: Block list

module BlockGroup =

    /// The key a run's fold keeps its open state under.
    let key (terminal: TerminalId) (leader: Block) : FoldKey = FoldKey.Commands (terminal, leader.BlockId)

    /// How many lines of a terminal's history the pane draws before it folds the rest: about
    /// what the pane holds at full height, on a desktop and on a phone alike.
    ///
    /// A count of LINES rather than of commands, because what a fold is for is what does not
    /// fit — six one-line commands fit, one `seq 1 300` does not — and rather than a measured
    /// height, because a measured rule needs a browser to say what it decided and this one
    /// needs a model. What it does not see is wrapping: a phone wraps a long line a desktop
    /// does not, and is left with a little more history unfolded than its screen holds, which
    /// the pane scrolls through from its end like any other.
    let unfoldedLines = 40

    /// What a block's output is, as far as the page draws it: its last `keep` lines
    /// (`TerminalFeed.shownOutput`) over the range the block covers, and how many came before
    /// them. One read for the view and for the fold budget below, so what the budget counts is
    /// what is drawn.
    let outputOf (keep: int) (feed: TerminalFeed) (block: Block) : int * string =
        // A running block's output runs to whatever has arrived; a finished one is bounded
        // by the range its completion event recorded — which is what makes a reload show
        // exactly the same block as the live view did.
        let toSeq = block.ToSeq |> Option.defaultValue (max feed.KnownLength block.FromSeq)
        TerminalFeed.shownOutput keep block.FromSeq toSeq feed

    /// The rows an output takes: its lines, and at least one — a block with none draws a
    /// line saying so (`…` while it runs, "no output" once it is over), which is the one the
    /// view's empty cases are.
    let rowsOf (output: string) : int = max 1 (TerminalFeed.lineCount output)

    /// The lines one block takes in the pane: its command line and the gap under it, then
    /// what it shows of its output with the pane's history cap on (`TerminalFeed.paneLines`,
    /// so the budget counts what is DRAWN, not what was printed), and the control above them
    /// when some were left out.
    ///
    /// The cap, not whatever a reader has opened: expanding a block must not move the others
    /// into a fold under their hands, nor change which block keys a run.
    let linesOf (feed: TerminalFeed) (block: Block) : int =
        let elided, output = outputOf TerminalFeed.paneLines feed block
        2 + rowsOf output + (if elided > 0 then 1 else 0)

    /// Group a terminal's blocks (oldest first) for the pane, given what they printed.
    ///
    /// Only what does not fit folds. Read from the newest back, blocks are drawn whole until
    /// `unfoldedLines` have been: everything before that is EARLIER history, and only earlier
    /// history folds. The block the budget runs out INSIDE is drawn whole too — it is the one
    /// the top of a full pane cuts through, partly on screen, the way a terminal's own top
    /// line cuts a command's output — so a reader at the end has a pane's worth of history in
    /// front of them, and a `seq 1 300` three commands back fills it rather than folding away
    /// and leaving the pane empty above two short commands.
    ///
    /// Every command used to fold except the newest, so six short commands were one "ran 5
    /// commands" line over one command and a pane five-sixths empty — a person had to open the
    /// fold to see what they did a minute ago, which was all there was room for.
    ///
    /// Within earlier history, consecutive blocks from one actor fold together, never past
    /// somebody else's command, and never a group of one: a disclosure around a single
    /// command hides the only thing the row has to say behind a click.
    ///
    /// And never the NEWEST block, nor one still RUNNING, whatever it printed. A run used to
    /// take everything its actor ran, so from the second command on what had just happened
    /// went behind a shut fold with everything before it: `pwd`, then `echo second`, and
    /// neither answer was on screen; a running loop's output was hidden from everyone watching
    /// it. What just happened, and what is happening, is what a person reads a terminal FOR —
    /// the fold is for what they have already read. The newest is drawn even when it alone
    /// overflows the budget, and a running block splits a run rather than sitting inside one,
    /// so its run-mates fold either side of it.
    ///
    /// That the first block keys a run is what keeps a fold a reader opened open: a run
    /// grows at its END (a block joins as newer ones push it out of the budget), so its first
    /// block is stable for as long as the run is.
    let ofBlocks (feed: TerminalFeed) (blocks: Block list) : BlockGroup list =
        // The blocks drawn whole as the latest history: each, newest first, while the lines
        // drawn after it still leave room. Walked newest-first and stopped once the room is
        // gone, so only what the pane could show is ever measured.
        let latest =
            let rec walk (spent: int) (kept: Set<BlockId>) (newestFirst: Block list) =
                match newestFirst with
                | block :: older when spent < unfoldedLines ->
                    walk (spent + linesOf feed block) (Set.add block.BlockId kept) older
                | _ -> kept
            walk 0 Set.empty (List.rev blocks)
        let foldable (block: Block) =
            not (Set.contains block.BlockId latest)
            && (match block.Status with
                | BlockRunning -> false
                | BlockFinished _ | BlockRejected _ | BlockEnded _ -> true)
        // Each group so far as its first block and the rest newest-first, so it can never be
        // empty and its latest block is at hand.
        let step (groups: (Block * Block list) list) (next: Block) : (Block * Block list) list =
            match groups with
            | (first, restNewestFirst) :: earlier ->
                let previous = List.tryHead restNewestFirst |> Option.defaultValue first
                if foldable previous && foldable next
                   && Authority.author previous.Authority = Authority.author next.Authority then
                    (first, next :: restNewestFirst) :: earlier
                else (next, []) :: groups
            | [] -> [ next, [] ]
        List.fold step [] blocks
        |> List.rev
        |> List.map (fun (first, restNewestFirst) ->
            match restNewestFirst with
            | [] -> BlockGroup.Alone first
            | _ -> BlockGroup.Run (first, List.rev restNewestFirst))

    /// The fold a block is behind, if it is behind one — what "show in terminal" has to
    /// open before there is anything on screen to scroll to.
    let holding (terminal: TerminalId) (block: BlockId) (feed: TerminalFeed) (blocks: Block list) : FoldKey option =
        ofBlocks feed blocks
        |> List.tryPick (function
            | BlockGroup.Run (leader, rest) when leader.BlockId = block || rest |> List.exists (fun b -> b.BlockId = block) ->
                Some (key terminal leader)
            | BlockGroup.Run _ | BlockGroup.Alone _ -> None)

/// A surface read from its END: what is newest is at the bottom, and a reader who is there
/// stays there as more arrives — while one who has scrolled away is left where they are and
/// offered the way back. The rule is the browser's (`Tail`, `app/browser`), because it is a
/// fact about one scroll position; what is here is the NAME, which the surface and its "jump
/// to latest" control both carry (`Dom.Hooks.tail`) so a control can say which surface it
/// brings back, and a surface that changes what it is (blocks to a live screen) is a new one.
[<RequireQualifiedAccess>]
type TailSurface =
    /// The conversation.
    | Conversation
    /// A terminal's block history, while it runs commands as blocks.
    | Blocks of TerminalId
    /// A terminal's live screen, while a program holds it.
    | Screen of TerminalId

module TailSurface =

    /// The surface's name in the document. Two terminals' surfaces never share one, and
    /// neither do one terminal's two: a reader who had scrolled up through its blocks and then
    /// took the keyboard has not scrolled up through the screen that replaced them.
    let key (surface: TailSurface) : string =
        match surface with
        | TailSurface.Conversation -> "chat"
        | TailSurface.Blocks terminal -> "blocks:" + TerminalId.value terminal
        | TailSurface.Screen terminal -> "screen:" + TerminalId.value terminal

    /// The surface a name in the document names (`key`), or nothing for a name that is not
    /// one.
    let ofKey (key: string) : TailSurface option =
        let terminal (prefix: string) =
            if key.StartsWith prefix then TerminalId.create (key.Substring prefix.Length) |> Result.toOption
            else None
        if key = "chat" then Some TailSurface.Conversation
        else
            match terminal "blocks:", terminal "screen:" with
            | Some id, _ -> Some (TailSurface.Blocks id)
            | None, Some id -> Some (TailSurface.Screen id)
            | None, None -> None

/// Which of its two faces the sidebar column shows.
[<RequireQualifiedAccess>]
type ColumnFace =
    | Workspace
    | Settings

/// The sidebar column: whether it is on screen, and which face it shows. View state, and this
/// browser's own — a column one person collapsed is not a thing anybody else is looking at.
///
/// Two bits rather than one, because the column is two different things on each side of the
/// stylesheet's breakpoint (`Style.wideMedia`): BESIDE the chat on a desktop, where hiding it
/// is a preference and remembered, and a DRAWER over the chat on a phone, where opening it is
/// a moment and never remembered. One bit read both ways — the root's `nav-alt`, which is how
/// the stylesheet still says it (`Column.navAlt`) — made a desktop collapse turn into an open
/// drawer the moment a window narrowed past the line.
[<RequireQualifiedAccess>]
type Column =
    { /// Whether the stylesheet lays the column beside the chat, as this browser last heard.
      Wide : bool
      /// On a desktop: the reader put it away.
      Collapsed : bool
      /// On a phone: the drawer is open over the chat.
      Drawer : bool
      Face : ColumnFace }

module Column =

    let initial : Column =
        { Column.Wide = true; Column.Collapsed = false; Column.Drawer = false; Column.Face = ColumnFace.Workspace }

    /// Whether the column is on screen, on whichever side of the breakpoint this is.
    let shown (column: Column) : bool = if column.Wide then not column.Collapsed else column.Drawer

    /// The root's `nav-alt` class, which the stylesheet reads the opposite way on each side of
    /// the breakpoint: collapsed on a desktop, drawer open on a phone.
    let navAlt (column: Column) : bool = if column.Wide then column.Collapsed else column.Drawer

    let hide (column: Column) : Column =
        if column.Wide then { column with Collapsed = true } else { column with Drawer = false }

/// The split between the chat and the pane on a desktop: the pane's width, and the widest it
/// may be — which is what the CHAT can spare, because the sidebar beside them can be collapsed
/// and a bound against the window let the pane grow until the conversation was a single letter
/// wide.
///
/// The column was a fixed 420px chosen as "the width the content actually has", and measured
/// against what a terminal prints it was 20 columns short of 80. So the split moves and is
/// remembered — and until somebody moves it, it is not a constant either (`PaneSplit.resolve`).
[<RequireQualifiedAccess>]
type PaneSplit = { Width : int; Widest : int }

/// What the split is made from: how wide the window is, as the browser last measured it, and
/// the width the reader chose, if they have. The split itself is derived (`PaneSplit.resolve`),
/// so a window that changes size, or a sidebar that is put away, moves a pane nobody sized and
/// holds one somebody did — and neither is a write anybody has to remember to make.
[<RequireQualifiedAccess>]
type PaneRoom = { Window : int option; Chosen : int option }

module PaneRoom =

    /// Nothing measured and nothing chosen: a server rendering the shell, or the first paint.
    let unmeasured : PaneRoom = { PaneRoom.Window = None; PaneRoom.Chosen = None }

module PaneSplit =

    /// Neither column can be dragged away to nothing: this is the pane's floor.
    let narrowest = 320

    /// The chat's floor: the pane is never wider than what leaves the chat this much.
    let chatFloor = 420

    /// The pane's width when nobody has set one and the screen has no room to spare: the design
    /// token (`--spacing-term`), and the floor a pane nobody sized grows from.
    let natural = 420

    /// What the chat's reading column takes at its full measure: `Style.readingColumn`'s 38rem
    /// (`--spacing-measure`, 608px) and the 32px the timeline keeps either side of it
    /// (`Style.timeline`). A chat wider than this is an empty strip beside the conversation.
    let chatReadable = 608 + 2 * 32

    /// The sidebar's width on a desktop (`--spacing-side`), while it is beside the chat.
    let side = 280

    /// What the separator says before the browser has measured anything — a server rendering
    /// the shell, or the first paint: the design token's width, and a ceiling the browser's
    /// first measurement replaces.
    let unmeasured : PaneSplit = { PaneSplit.Width = natural; PaneSplit.Widest = 1080 }

    /// The split at `wanted`, within the floor and `widest`. Rounded before clamping, so a
    /// bound is a bound exactly: clamping a fraction first and rounding after could land a
    /// pixel outside one.
    let within (widest: float) (wanted: float) : PaneSplit =
        let widest = max narrowest (int (round widest))
        { PaneSplit.Width = int (round wanted) |> min widest |> max narrowest
          PaneSplit.Widest = widest }

    /// The split a window this wide lays out. The chat and the pane share what the sidebar
    /// leaves; the pane may take all of it but the chat's floor. A width the reader chose is
    /// held to that. A pane nobody sized takes what the chat's reading column does not use —
    /// the chat stays at its full measure and the remainder is the pane's, never less than its
    /// own floor — because a chat column wider than what it reads in is a blank strip beside
    /// the conversation while the terminal, usually the busiest thing on the screen, wraps.
    ///
    /// The sidebar is the model's to know rather than the browser's to measure: it animates
    /// shut, and a measurement taken while it travels is a width it was for one frame.
    let resolve (column: Column) (window: int) (chosen: int option) : PaneSplit =
        let shared = if column.Wide && not column.Collapsed then window - side else window
        let widest = float (shared - chatFloor)
        match chosen with
        | Some chosen -> within widest (float chosen)
        | None -> within widest (float (max natural (shared - chatReadable)))

/// Who is connected to this session NOW, as the log says it: how many open connections each
/// peer has, folded from the `PeerJoined` / `PeerLeft` pair the Session appends around every
/// peer link (`PeerSession.run`).
///
/// Counted rather than a set because one browser is one peer however many tabs it has open
/// (the peer id is kept per browser), so closing one tab leaves the person here. And EMPTIED at
/// every boot, because a process that died took its links with it without writing their
/// `PeerLeft` — everybody still here after a restart rejoins after the boot's own event, so a
/// join from before it describes a connection that no longer exists.
///
/// This is not `Presence`, which is where a peer's caret or pane is and holds only peers that
/// are SOMEWHERE — a person reading the timeline has no entry there and is still in the room.
/// Nor is it `Peers`, which keeps the departed so a draft's author keeps a name.
module Here =

    let empty : Map<PeerId, int> = Map.empty

    let applyEvent (here: Map<PeerId, int>) (event: SessionEvent) : Map<PeerId, int> =
        match event with
        | PeerJoined joined ->
            Map.add joined.PeerId ((Map.tryFind joined.PeerId here |> Option.defaultValue 0) + 1) here
        | PeerLeft left ->
            match Map.tryFind left.PeerId here with
            | Some n when n > 1 -> Map.add left.PeerId (n - 1) here
            | _ -> Map.remove left.PeerId here
        | SessionStarted _ | SessionResumed _ -> empty
        | _ -> here

    /// Every peer with a connection open, in id order.
    let peers (here: Map<PeerId, int>) : PeerId list = here |> Map.toList |> List.map fst

/// What this browser remembers for its next load, written where the browser keeps such things
/// (`Client.Ports.Remember`).
[<RequireQualifiedAccess>]
type Preference =
    /// The desktop column was put away, or brought back.
    | NavCollapsed of bool
    /// The pane's width on a desktop, as the reader last set it.
    | PaneWidth of int
    /// What this browser had open in one session's pane (`ClientModel.paneToRemember`).
    | Pane of SessionId * PaneMemory

type ClientModel =
    { Peer          : PeerState
      Connection    : ConnectionState
      /// The serving session's id: seeded from the shell (so it is known before — and
      /// without — any connection) and re-learned from `PeerAccepted`. Shown as the
      /// header's secondary identifier beside the editable title, and it names the session
      /// the reconnect offer asks the Manager for, which is a moment at which no
      /// `PeerAccepted` has happened by definition.
      Session       : SessionId option
      /// The Manager's public origin as the SHELL was told it (Plan 11): where to ask for
      /// this session back once it has stopped. `None` when the shell carried none — a
      /// Manager-less session — and then there is nothing to offer.
      ///
      /// Static for the life of the page. Never a message, never folded: it is a fact
      /// about the deployment that served this document, not part of the session's state.
      Manager       : string option
      /// Whether this deployment's sessions change address between launches (Plan 12).
      /// When true the browser's storage does not survive a restart, because it is
      /// partitioned by origin and the origin carries the port — so the client's
      /// local-first promise has to be qualified wherever it is made.
      ///
      /// Static for the life of the page, like `Manager`: a fact about the deployment that
      /// served this document, never a message and never folded.
      EphemeralStorage : bool
      /// Which build of Yession served this document (`Yession.Host.Version.current`), as the
      /// shell was told it: the serving session renders it into the first paint, and the
      /// browser reads it back off the shell. `None` when no shell said — a test harness, a
      /// peer with no server behind it — and then the footer says nothing rather than guess.
      ///
      /// The SERVER's build, never the client's: the browser bundle is one of that server's
      /// assets, so they are the same build, and the server is the one that can say which.
      /// Static for the life of the page, like `Manager`.
      Build : string option
      /// Whether this client can keep the history it is given (Plan 20). The store is the
      /// Cache API, which needs a secure context — loopback and every `https://` mount are
      /// one, a session reached over plain HTTP at a LAN address is not.
      ///
      /// Defaults to TRUE, and the browser says otherwise: the server renders this shell too,
      /// and a default of false would have every server-rendered page announce a missing store
      /// before the client that knows has had a chance to look.
      CanKeepHistory : bool
      /// Whether this client has finished reading the history it already had (Plan 20).
      ///
      /// It exists because an empty timeline had two opposite meanings wearing one mark: the
      /// idle caret says *nothing was ever said here*, and it was also what a client showed
      /// while it had not yet looked. After the local store landed, the second is the common
      /// case on a cold open — so the caret was telling most people the opposite of the truth.
      ///
      /// One flag rather than a `Pending | Restoring | Restored`: the view asks one question,
      /// "has this client looked yet", and a state nothing distinguishes is a state nobody
      /// can act on. Starts FALSE, including on the server-rendered shell, because at first
      /// paint no client has looked.
      HistoryRead : bool
      Synced        : SyncedSessionState<Ylmish.Text>
      Conversation  : ConversationProjection
      /// The terminal half of the chat (Plan 14, stage 1): block chips and lease-stretch
      /// items, with the offset each is anchored at. Merged with `Conversation` at render
      /// time by `TimelineProjection.items` — a view-level fold, so the projection that
      /// builds the agent's context is untouched.
      Timeline      : TimelineProjection
      EventConsumer : EventConsumerState
      Agent         : AgentViewState
      /// Live carets+selections, keyed by WHO. Cleared when somebody moves their caret out of
      /// every collaborative field, or disconnects (their `Focus` becomes `None`).
      ///
      /// Keyed by actor rather than peer because the Session edits here too (Plan 25),
      /// and it never joined as a peer. `Peers` below stays peer-keyed: it is a fold of who
      /// has connected, which is a different question with a different answer.
      Presence      : Map<ActorRef, RemotePresence>
      /// Where THIS peer's own caret is, as its editors last reported it (`CaretMovedMsg`):
      /// the half of its presence the model cannot derive. The other half, what it has open,
      /// is `viewing`. Kept here rather than beside the connection so one rule can say when
      /// either half is told to everyone else (`presenceToSend`), and so a caret reported
      /// before there is a channel is still known when one is accepted.
      Caret         : Focus option
      /// Every peer this session has seen, with the display name it joined under — folded from
      /// the durable log (`PeerJoined`/`PeerLeft`), so it survives a reload and names a draft's
      /// author even while that author is away. Presence is who is here NOW; this is who is who.
      Peers         : Map<PeerId, string>
      /// Who has a connection open right now, folded from the same log (`Here`). The roster
      /// lists everyone in it, whether or not they are editing anything.
      Here          : Map<PeerId, int>
      /// Peer↔user attribution, the client's own copy of `Yession.Domain.Attribution`,
      /// folded from the same `PeerJoined` events the Session folds. It exists so
      /// chat can resolve a `UserRef` author to a real name — and a peer to its durable
      /// user — through the exact rule that decided the author was a `UserRef` in the
      /// first place, rather than a client-side guess that could disagree with it.
      Attribution   : Attribution.State
      /// Which draft this client has OPEN in the composer, of the at-most-one that can be. App
      /// state, never synced: two people in one session may each have a different draft open.
      Composer      : ComposerChoice
      /// The session environment's UI status, folded from lifecycle events (Step 12).
      Environment   : EnvironmentStatus
      /// Terminals, folded from terminal events (Plan 13) — the panel's structure.
      Terminals     : Projection
      /// Each terminal's transcript as this client has it. Separate from the projection
      /// because it arrives on a different leg: facts fold from the event log, bytes
      /// stream from the transcript.
      TerminalFeeds : Map<TerminalId, TerminalFeed>
      /// The transcript reads this client has out, one per terminal at most (`reads`).
      /// Beside the feeds rather than in them: a feed is what the transcript SAYS, and this is
      /// what the connection is doing about it, which a new connection starts afresh.
      TranscriptReads : Map<TerminalId, TranscriptRead>
      /// How many reads — of the event log or of a transcript — this client has ever asked
      /// for, so each read is asked under a number no other has carried and its answer can be
      /// told from one that belongs to a read since forgotten. Never reset: across a reconnect
      /// is exactly where the two would otherwise meet.
      ReadsAsked : int
      /// Keyframes this client has fetched, keyed by terminal and the transcript line each
      /// paints (Plan 14, stage 3). One per range this client has opened, not one per block
      /// the session ever ran: they are fetched on demand, and a range is only opened by
      /// somebody choosing to read it.
      TerminalKeyframes : Map<TerminalId * int, TranscriptKeyframe>
      /// Keyframes this client has ASKED for, answered or not. What stops a burst of
      /// identical requests while the first is still out, and a keyframe that did not answer
      /// being asked again on every message after: the range still rebases and plays without
      /// one, as the naive slice, so asking again would be a spin with nothing to gain.
      KeyframesAsked : Set<TerminalId * int>
      /// The live SCREEN of each terminal, as this client has composed it (Plan 14, stage
      /// 6): the serialized output of an emulator fed the Process's snapshot and every
      /// record since.
      ///
      /// A screen, not a stream — a terminal in live mode is running a program that moves
      /// the cursor, and what it DISPLAYS is a projection of what it emitted. The transcript
      /// stays the record; this is the view.
      TerminalScreens : Map<TerminalId, LiveScreen>
      /// How big this client's own view of each terminal is, in CHARACTER CELLS: the box the
      /// output is laid into, measured from the rendered page rather than assumed.
      ///
      /// What it is FOR is the command about to be queued (`PendingAct.Size`). A block that
      /// ran at eighty columns is eighty-column text in the transcript for ever, so the width
      /// a command runs at is worth as much as the command — and until this, block mode had
      /// no width at all: every terminal was 80x24 for its whole life.
      ///
      /// LOCAL, never synced. A shared register of everyone's viewport has to answer "whose
      /// wins", and that is the question the size riding the ACT exists to avoid. What leaves
      /// this client is a claim about one command, or a resize on a lease this peer holds —
      /// neither of them a fact about the room.
      ///
      /// A terminal the pane is not showing keeps its last measurement rather than losing it.
      /// There is nothing on screen to measure, and no measurement is not the same as a
      /// measurement of nothing: the width this reader last had is the truer answer, and the
      /// only one they could have meant.
      TerminalViewports : Map<TerminalId, Size>
      /// The terminals in this client's strip, in the order they opened (Plan 20, stage 1;
      /// P2-1).
      ///
      /// The strip used to be a census — every terminal the session ever had, for ever,
      /// because it was the only door to a recording. The switcher is that door now, so the
      /// strip can be what a person is actually working with: the terminals THIS client
      /// opened — pressed for, chose from the switcher, reached through a chip — or was
      /// handed by `open_tab`/`focus_tab`. Not every live terminal: the agent's own are
      /// reached through the switcher, because nothing a session DOES puts a tab in somebody's
      /// strip.
      ///
      /// Terminals and nothing else, by type. A block, a stretch or a file opened from the
      /// chat is a PREVIEW over the selected terminal (`PaneMode.Previewing`), never a tab,
      /// and there are no pins: a terminal here is always kept, and a preview never needs to
      /// be.
      ///
      /// A LIST rather than a set, held in the order the terminals OPENED (`withTab`), so a
      /// tab has one place in the strip for its whole life. LOCAL to this client, never
      /// synced: what one person has open is not what another is working on.
      ///
      /// What takes one out is the READER: a terminal that closes keeps its tab, in its
      /// place, wearing its closed mark, until they put it away (`DismissTabMsg`). Or
      /// `close_tab`, for one not on screen. Nothing else — not choosing another tab, not a
      /// preview over it, not a reload (`PaneMemory`) — so a tab never blinks out or comes
      /// back somewhere else (F2). There is no way to drop a tab and leave its terminal
      /// running (P2-2): a running tab's × is the kill, so the strip never says a terminal
      /// is gone while it is still running somewhere.
      Tabs          : TerminalId list
      /// How many terminals this client has ASKED for and not yet been shown.
      ///
      /// Pressing "new terminal" is a REQUEST, and the only record of it. `OpenTerminal`
      /// answers nothing — the new terminal reaches every peer as an event like any other
      /// (`Client.OpenTerminal`) — so without this the client cannot tell the terminal it
      /// asked for from one that merely belongs to it, and it must be able to: under a
      /// verified login the log records which USER opened a terminal and cannot say which
      /// of their connections did, so "any terminal that is mine" would let the phone in
      /// somebody's pocket take the pane out of the tab they are working in.
      ///
      /// A COUNT rather than a flag, because two presses are owed two terminals and a flag
      /// would land the second press on the first terminal. Spent on arrival: a press buys
      /// exactly the terminal that answers it, and the next one to arrive finds nothing
      /// owed. Spent too by the session REFUSING it (`CommandAnsweredMsg`), which is the
      /// other answer a press can get, and the one after which no terminal is coming.
      Opening       : int
      /// The terminal this client last asked to END, until the close arrives.
      ///
      /// `Opening`'s shape, for `Opening`'s reason: the kill is pressed on a tab's × or a
      /// row of the switcher, the close comes back as an event like every other peer's, and that event removes the
      /// control that was pressed — so the only thing that can say where focus goes next is
      /// the fold that sees the close arrive, and it can say so only if it knows the close is
      /// the answer to a press made HERE. A terminal somebody else ends must not move this
      /// reader's cursor. One slot rather than a count: there is one kill control under a
      /// hand at a time, and the latest press is the one focus follows.
      ///
      /// Spent too by the session REFUSING the kill (`CommandAnsweredMsg`), `Opening`'s other
      /// answer for the same reason: after a refusal no close is coming for this press, and
      /// left owed, whatever closed that terminal next — its shell exiting, somebody else's
      /// kill — would be taken for this press's answer and move the reader's focus.
      KillPending   : TerminalId option
      /// Which terminal's kill is ARMED — pressed once, waiting on a second press in the same
      /// place to confirm it (`ArmKillMsg`). `QueueDeleteArmed`'s shape, for its reason and a
      /// stronger one: a kill ends what runs in the terminal for everybody in the session, and
      /// it used to happen on one press of a glyph that a double-click could land twice.
      ///
      /// ONE slot: arming another row's kill disarms this one, so at most one terminal is ever
      /// a press from gone. View state, local and transient — an unconfirmed press is one
      /// person's moment, not a fact the room agrees on. Taken back on its own after `armedMs`
      /// (`ClientModel.timers`), by Escape, by focus leaving the control, and by the terminal
      /// closing under it.
      KillArmed     : TerminalId option
      /// What the pane is SHOWING: which terminal and which read of it, or a preview over one
      /// (Plan 25, stage 2; P2-1). `None` = nothing chosen yet, resolved to a
      /// default by `selectedTerminal`.
      ///
      /// One field rather than the four this replaces, because the four had to agree and
      /// nothing made them: see `PaneMode`. The terminal it names is always one in `Tabs` —
      /// showing a terminal opens it — so this says which of them is on top and never what the
      /// strip holds.
      Pane          : PaneMode option
      /// Whether the terminals panel is open. View state, never synced: two people in one
      /// session may reasonably want different columns on screen.
      TerminalsOpen : bool
      /// What this browser remembered of the pane for this session, HELD until the log has
      /// been read through (P0-4) — applied to each terminal as the log names it, and
      /// cleared once the whole log has been read (`recall`).
      ///
      /// Held rather than applied at boot, because a tab is only worth restoring onto a
      /// terminal the session still has, and only the log can say which those are: the
      /// events fold drops every unkept terminal tab the projection lacks, on every page, so a
      /// strip seeded before its `TerminalOpened` arrived would empty itself on the first
      /// page. Only the open bit is applied at boot (`remembered`), because it needs nothing
      /// checked and is the one part a person would see jump.
      ///
      /// While this is held, nothing writes the memory back: what the model holds then is
      /// the part of the strip whose terminals have arrived so far, and writing it would
      /// forget the rest of what is waiting to be restored.
      PaneMemory    : PaneMemory option
      /// Whether there WAS a memory for this session when the page loaded — so "restored shut"
      /// can be told from "never remembered", which `TerminalsOpen = false` alone cannot say.
      /// Set at boot, never changed.
      PaneRemembered : bool
      /// Whether this client has, once, read the log through to where the session said it
      /// ended — after which what a page brings is NEWS, and before which it is history being
      /// replayed. Latched: a later catch-up (a reconnect, or the round trip after any send)
      /// does not turn news back into history. What reads it is a move that answers a change
      /// under the reader (`keyboardSwap`): a terminal's face changing in a replayed log is
      /// not something that happened under anybody's hand.
      HeardThrough : bool
      /// Whether the pane may still open ITSELF, once, when the log has been read through
      /// (P1-4, `openOfItself`): a desktop's first look at a session with a terminal running
      /// should show it, rather than a header with nothing in it about the build.
      ///
      /// A boot fact first — `false` here and for the server, which has no screen, and the
      /// browser's own answer (`Browser.fs`) of whether its screen is wide enough for the pane
      /// to sit BESIDE the chat. On a phone the pane is the whole screen, and a pane that
      /// opened itself there would hide the conversation somebody came to read. Then spent:
      /// by the decision, or by anything at all moving the column first, because a person who
      /// has already opened or shut it has answered the question this was going to.
      PaneOpensItself : bool
      /// How far through each terminal's commands this person has LOOKED: what had finished
      /// in it the last time it was on their screen (`ClientModel.looking`). A terminal that
      /// has finished more since is news to them (`unseen`), and says so on its tab and its
      /// row — so a build that finished in another tab is not left looking exactly like a
      /// terminal nobody touched.
      ///
      /// Kept by the fold after every message (`notice`), never by a message that remembers
      /// to: a terminal can come on screen by a press, a reload, the pane opening itself, a
      /// preview closing over it — and a rule each of those had to remember is a rule one of
      /// them forgets. Per browser, like the rest of the pane, and kept across a reload with
      /// it (`PaneMemory.Seen`): a build that finished while you were away is the one most
      /// worth saying so about.
      Seen : Map<TerminalId, CommandTally>
      /// The sidebar column (`Column`). Seeded at boot by the browser with its breakpoint and
      /// its remembered collapse, as the served shell's one inline script already applied it
      /// before first paint — so the first render here agrees with what is on screen.
      Column : Column
      /// What the split between the chat and the pane is made from (`PaneRoom`); the split
      /// itself is `ClientModel.paneSplit`, and there is none until the browser has measured
      /// the window, which a server rendering the shell never does.
      PaneRoom : PaneRoom
      /// The surfaces read from their end whose reader has scrolled away from it — what puts
      /// each one's "jump to latest" on screen. A surface not here is being followed, which is
      /// where every reader arrives. WHETHER a reader moved is the document's to see (`Tail`,
      /// in the browser); what that means for what is drawn is this.
      Away : Set<TailSurface>
      /// Which timeline item has its actions menu open, if any. View state for the same
      /// reason the column above is: a menu one person opened is not a thing anybody else
      /// is looking at.
      ///
      /// ONE field rather than a set, and that is the invariant: a second menu cannot be
      /// open, because opening one is writing this. Two open menus would be two popovers
      /// over one column with one Escape between them.
      ItemMenu      : MessageId option
      /// Whether the strip's `+` has its menu open (Plan 20, stage 1).
      ///
      /// A `bool` rather than an option of something, because there is one of these on the
      /// screen: it hangs off one control, where `ItemMenu` hangs off whichever timeline item
      /// was asked. Model state rather than the DOM's own, for `ItemMenu`'s reason — a menu
      /// rendered only while open cannot be asked whether it is open.
      PaneMenu      : bool
      /// Whether the pane is on its "all" page: every terminal this session has had, live and
      /// closed, and every file shared into it.
      ///
      /// The SWITCHER (P2-2), now a page rather than a popover. It hung under the head's name
      /// over whatever the pane showed, which put a boxed list, a strip of tabs and a head
      /// that named one of them in three stacked layers — and "which of these do I press to
      /// get somewhere" had three answers. It is the pivot's last item now, `all`, and the
      /// pane's body while it is up. It is still OVER the pane's face rather than a face of its
      /// own (`PaneMode`): the terminal or preview the reader was on stays selected under it,
      /// so leaving it — Escape, or the item it was opened from — returns to exactly that, and
      /// choosing from it is choosing a tab.
      Switcher      : bool
      /// What the `all` page is narrowed to, and which rows that decided on (`ListFilter`).
      ListFilter    : ListFilter
      /// The last thing the session REFUSED, in its own words.
      ///
      /// A command answers `CommandAccepted` or `CommandRejected`, and until now only the
      /// launch surface read that answer — every other refusal was dropped where it arrived.
      /// So pressing a button the session would not honour did nothing, said nothing, and
      /// left no trace: a dead control, which is the one thing a control must never be. It
      /// cost an afternoon to find that terminals could not open on one machine, because the
      /// refusal that said why was discarded by the client that asked for it.
      ///
      /// The REASON rather than the command it answered (`Refusal`). Drawn once, where the
      /// press was (`refusalMount`); never for the launch's own add, whose card says its own
      /// refusal under the row it was sent from.
      Refused       : Refusal option
      /// The last time somebody took a terminal's keyboard from this client, while it still
      /// stands: set by the steal arriving as NEWS (`HeardThrough` — a steal replayed on a
      /// reload happened to nobody who is here now), and gone the moment it is no longer
      /// true — this client takes it back, the taker hands it on or back, the terminal ends —
      /// or once it has been read and put away (`DismissStolenMsg`). One slot: a second steal
      /// is the newer news. Drawn in place of that terminal's lease bar, for this client only.
      Stolen        : StolenLease option
      /// The commands this client has sent and not yet heard back about, by request id, with
      /// what each asked for. Written where a command is SENT (`CommandSentMsg`, from the one
      /// verb that sends one, `Client.Connection.Ask`) and spent by its answer — which comes
      /// back as a bare id, so this is the only thing that can say what a refusal refused.
      Asked         : Map<RequestId, SessionCommand>
      /// Which folds are UNFOLDED — an act's particulars, a turn's tool calls, one call's
      /// input and output, a run of commands in the pane. Not remembered across a reload:
      /// that is a fresh read, and every fold starts shut. View state like the menu above — what one person opened to read
      /// is nobody else's — but a set rather than one slot: two folds open at once are two
      /// things being read, not two popovers fighting over an Escape. Empty is every line
      /// folded to its title, which is how a timeline is read.
      OpenFolds     : Set<FoldKey>
      /// Which session breaks are showing the moment they happened instead of how long they
      /// were. View state for `OpenFolds`' reason — which reading of a time one person wants
      /// is nobody else's business — and a set for the same reason too: two breaks on screen
      /// are two questions, and answering one must not re-answer the other.
      ///
      /// NOT a `FoldKey`. That type is documented as one control — an arrow, a title, and
      /// something that unfolds beneath — and nothing unfolds here; the label changes what it
      /// says. Borrowing the key would make a break the fourth kind of fold and the doc
      /// comment on `FoldKey` a lie.
      DatedBreaks   : Set<MessageId>
      /// Which queued entry's delete is ARMED — one press from gone, waiting on a second
      /// press to confirm it (`ArmQueueDeleteMsg`). View state, local and transient for the
      /// same reason `Copied` below is: an unconfirmed press is one person's moment, not a
      /// fact the room needs to agree on.
      ///
      /// ONE slot rather than a set, same rule as `ItemMenu`: arming a second entry disarms
      /// whatever was armed before it, so at most one delete in the queue is ever a press
      /// away from happening. Taken back on its own after `armedMs`
      /// (`ClientModel.timers`) — an arm nobody confirms must not stay armed forever, the way
      /// a menu left open or a copy's confirmation left showing would be wrong too.
      QueueDeleteArmed : QueueId option
      /// What this client has just put on the clipboard, named by the hook of the box it
      /// came out of (`Dom.Hooks.githubUserCode` and whatever joins it). View state, local
      /// and transient for the same reason the menu above is: copying is one person's act
      /// on one machine, and nobody else is looking at their clipboard.
      ///
      /// ONE slot, so the confirmation cannot be showing on two boxes at once — and `None`
      /// again a moment later, taken back by the wait the model declares for it
      /// (`ClientModel.timers`), because what it says is "just now".
      Copied        : Copy option
      /// The Claude connection panel's state (Plan 08), driven by the /claude routes.
      Claude        : ClaudeViewState
      /// The GitHub connection panel's state (Plan 14), driven by the /github routes.
      GitHub        : GitHubViewState
      /// The generated read surface (Plan 15), driven by the /queries stream.
      Queries       : QueriesViewState
      /// Repos whose sensitive capability set is waiting on somebody here (Plan 27).
      ///
      /// Folded from the same events the Process gates on, so what a person is asked and
      /// what a sandbox is waiting for are two readings of one log rather than two answers.
      Approvals     : RepoApprovals.Pending
      /// The launch surface's state — choosing the session's first repo. View state, local
      /// to this client: choosing is one person's act on one screen, and what it produces
      /// reaches everyone through the log.
      Launch        : LaunchViewState
      /// The session's repos, folded from the same events the timeline's repo notes come
      /// from: what the launch surface asks to know whether the session has one.
      Repos         : Repos.ReposProjection }

/// A move only the document can make: focus, and scrolling something into view. The model says
/// what is on screen; where the cursor is and how far the reader has scrolled are the
/// document's, so these leave the reducer as effects (`ClientEffect.Move`) and are carried
/// out after the render that put their target on screen.
[<RequireQualifiedAccess>]
type DomMove =
    /// Into the side pane, after something opened there. A chip that opened a pane and left
    /// focus behind it is the failure the WCAG floor names.
    | FocusPane
    /// Back to the chat item a preview was opened from, once the preview is going — the
    /// subject is the one thing the chip and the preview share (`PreviewSubject.key`).
    | FocusChat of PreviewSubject
    /// Back to one item's actions control, after the menu it opened has gone. Without it,
    /// dismissing a menu strands focus on `body`.
    | FocusItemActions of MessageId
    /// Back to the door the menu of new things hangs from, after the menu has gone —
    /// `FocusItemActions`' reason, for the other menu this shell has. The strip's `+`, or the
    /// empty pane's own button while the pane is empty and the strip offers no `+` (P1-4).
    | FocusPaneNew
    /// Onto this terminal's command line — this peer's own, the one that takes keystrokes.
    /// Where a terminal tab is FOR, so it is where showing one lands. A terminal with no
    /// command line to offer (somebody holds its keyboard, or it has closed) lands on what it
    /// does offer instead, and never on nothing.
    | FocusCommandLine of TerminalId
    /// Onto the control that shows the pane again, once the pane has gone. Hiding the pane
    /// takes every control in it out of reach at once, so focus has to go somewhere outside
    /// it, and the way back in is the one place that is always about the pane.
    | FocusPaneReopen
    /// Onto the empty pane's one press — what a pane with nothing in it offers.
    | FocusPaneEmpty
    /// Onto this terminal's row in the switcher.
    | FocusSwitcherRow of TerminalId
    /// Into the switcher, once it has opened: onto the row of the terminal the pane is about,
    /// else its first row — the place a reader who opened it to change terminals starts from.
    | FocusSwitcher
    /// Onto the pivot's selected item, once the switcher has gone — the terminal or preview
    /// it was laid over, which is what the pane shows again — and onto the empty pane's press
    /// when there was nothing under it. The page that had focus left the document; the item
    /// the reader is back on is where they are.
    | FocusPivot
    /// Onto this terminal's tab in the strip — where a kill pressed on the strip lands.
    | FocusTab of TerminalId
    /// The same move, made only while focus is in the pane or nowhere at all.
    ///
    /// For a move that follows an EVENT rather than a press: a terminal a press asked for, a
    /// kill a press asked for, each arriving a round trip later. A press knows it is about to
    /// take the control under the hand away; an arrival does not know where the hand has gone
    /// since, and a caret yanked out of the message composer because a terminal landed would
    /// be worse than the stranding this exists to fix.
    | OnArrival of DomMove
    /// The same move, made only while focus has been DROPPED: on `body`, because the control
    /// that held it has just left the document — or on the pane's panel, which is where the
    /// last such drop was caught when there was nothing better to land on.
    ///
    /// Narrower than `OnArrival`, which also moves a hand that is anywhere in the pane. For a
    /// swap nobody here pressed for — the terminal on screen changing what it offers a
    /// keyboard (`ClientModel.keyboardSwap`) — a reader in the strip or the switcher when a
    /// shell dies is still somewhere, and is left there.
    | IfDropped of DomMove
    /// Onto a terminal's live screen, when this peer has just become the one typing into it —
    /// and only from a hand that is nowhere, or in that terminal's own command line, where
    /// the keys no longer belong (`PaneShell.toTerminalScreen`).
    | FocusTerminalScreen of TerminalId
    /// Onto the pane's watch toggle, when the replay the hand was in has left the document.
    | FocusWatchToggle
    /// Scroll a terminal's history to one of its commands and mark it.
    | RevealBlock of TerminalId * BlockId
    /// Scroll the conversation to one message and mark it.
    | RevealMessage of MessageId
    /// Scroll the pane's strip the least distance that shows its selected tab whole
    /// (`ClientModel.pivotReveal`).
    | RevealPivot
    /// Scroll a surface to its own tail and follow it from there — what a sent message
    /// does to the conversation. Focus stays where it was: the sender is still typing.
    | ScrollToLatest of TailSurface
    /// The same, from the surface's own "jump to latest" control — which is not on screen at
    /// the tail, so the press takes it away and focus goes to the surface it brought back.
    | JumpToLatest of TailSurface
    /// Onto the message composer's field — or the session's title, where there is no
    /// composer to offer. Where a notice in the conversation column hands the keyboard when
    /// it goes from under it.
    | FocusComposer
    /// Onto whichever control now stands where the nav toggle that was pressed went: the
    /// column's own collapse when it is `shown`, the header's reopen when it is not.
    | FocusNavToggle of shown: bool
    /// Onto the settings face's counterpart control: its way back when `opened`, the way in
    /// when not.
    | FocusSettingsToggle of opened: bool

/// Messages that drive the client model. Connection-lifecycle messages are produced by
/// the connection driver (Connection.fs); the suffix avoids clashing with the
/// `ConnectionState` cases and the transport frame DU cases. Draft and queue messages
/// mutate only the synced collaborative state; the Ylmish binding turns those model
/// changes into CRDT deltas — sending needs no command round-trip (Phase 3).
type ClientMsg =
    | ConnectingMsg
    | ConnectedMsg of PeerAcceptedPayload
    | RejectedMsg of reason: string
    /// The session could not be reached — to ask who this is (`/me`), or to open the
    /// transport — and it will be tried again: `reason` is why this attempt failed and
    /// `failures` how many have failed in a row. Distinct from `RejectedMsg` (the session
    /// refusing a peer it did hear from) because the remedy differs: wait, versus re-auth.
    | RetryingMsg of reason: string * failures: int
    | EventsAvailableMsg of latestOffset: EventOffset
    /// A read-only event page from the Session (Step 07): the conversation is
    /// built by folding pages through the shared projection; offsets track progress.
    ///
    /// A page that answers no read this client has out — one the connection cannot match to
    /// a request. Folded, and decides nothing: what to read next is `EventsReadMsg`'s.
    | EventsPageMsg of EventPage<SessionEvent>
    /// The answer to event read `read` (`ClientEffect.ReadEvents`): a page, folded exactly as
    /// `EventsPageMsg` is, or the reason the read failed — which is settled, because the feed's
    /// resilience policy has already spent its retries by the time a failure gets here. What
    /// is read next is decided from it (`ClientModel.reads`).
    | EventsReadMsg of read: int * Result<EventPage<SessionEvent>, string>
    /// A page this client had already been given and kept (Plan 20): replayed out of its own
    /// store at boot, before any network read and without a session.
    ///
    /// Folded through exactly the same projection as `EventsPageMsg` — the events are the
    /// same events — and differing in one thing, which is the reason it is a separate case:
    /// it says NOTHING about the feed. A page off the network proves the feed works; a page
    /// off the local store proves only that this client kept it, and an offline client
    /// reporting a live history feed would be lying about the one leg that is down.
    | LocalHistoryMsg of EventPage<SessionEvent>
    /// The boot replay could not walk all the way through what this client kept (Plan 20):
    /// the carried offset is where the kept history resumes, and everything between the read
    /// cursor and it is not on this device.
    ///
    /// Its own message rather than a feed fault, because it is not one: no read has been
    /// attempted when it is dispatched, and the next one repairs it. See
    /// `EventConsumerState.MissingBefore`.
    | LocalHistoryGapMsg of EventOffset
    /// The client has finished reading what it already had (Plan 20) — whether that was a
    /// full conversation, or nothing at all because it keeps nothing. Either way it has now
    /// LOOKED, which is what the timeline needs to know before it can claim a session is
    /// empty.
    | HistoryReadMsg
    /// The event feed's health changed: a read failed and is being retried (reported by the
    /// resilience policy composed with the transport), or it failed for good (reported by
    /// the read loop, which is the one place that knows a read is over). A successful page
    /// needs no message — `EventsPageMsg` itself proves the feed is live.
    | EventFeedMsg of FeedHealth
    /// Catch-up has been running long enough to be worth saying (or has stopped being).
    /// The client arms a timer when catch-up begins; this is what the timer reports, and
    /// it is the ONLY thing that lights the "catching up" status — see
    /// `EventConsumerState.CatchUpIsSlow` for why the truth alone is too noisy to show.
    | CatchUpSlowMsg of bool
    /// The agent's writing sat still at this stamp for `ClientModel.writingQuietMs` — fired
    /// by the timer `ClientModel.timers` declares for it, never by anything else.
    | AgentQuietMsg of WritingStamp
    | DisconnectedMsg
    /// Edit the session title (collaborative text, merges like a draft body). A pure CRDT
    /// write; the Session reports the settled title to the Manager for the list.
    | EditTitleMsg of Ylmish.Text
    /// A remote peer's cursor moved (or cleared) in the title — ephemeral presence folded
    /// into `Presence`, never into the synced state.
    | RemotePresenceMsg of PresencePayload
    /// This peer's caret+selection moved, or left every collaborative field (`None`). Paced by
    /// whoever reports it — once a frame at most in the browser (`Render.focusReporter`) — so a
    /// burst of keystrokes is one message, not one per key.
    | CaretMovedMsg of Focus option
    /// Ensure the draft slot keyed by `PeerId` exists (author only), carrying the queue key it
    /// will become when anyone sends it. The body is a rich-text `Y.XmlFragment` anchored by the
    /// codec once the slot exists, so the editor has a synced fragment to bind — the client
    /// ensures its own slot so its composer can mount. Editing is the editor writing that
    /// fragment directly (it syncs through the doc); no body message.
    | EnsureDraftMsg of PeerId * QueueId
    /// Send = enqueue (Phase 3): the draft in this slot moves into the shared message queue at
    /// the tail under the key the slot has carried since it was published, and the slot clears.
    /// ANY co-editor may send; the same key from every sender is what makes concurrent sends one
    /// entry. The body fragment's content is copied draft->queue imperatively at send (shared
    /// types can't be re-parented).
    | SendDraftMsg of PeerId
    /// Discard the draft in the slot keyed by `PeerId` without sending it. The author's call:
    /// a co-editor collapses a draft, it does not destroy one.
    | DiscardDraftMsg of PeerId
    /// Open this peer's draft in the composer, collapsing whatever was open. Local view state.
    | ExpandDraftMsg of PeerId
    /// Open the local peer's own composer (the "new message" path), collapsing anyone else's.
    | StartDraftMsg
    /// Reorder a queued message: one fractional-index register write.
    | ReorderQueuedMsg of QueueId * order: float
    /// Delete a queued message. Until consumed, deletion wins: a deleted entry never
    /// becomes an event.
    ///
    /// Dispatched by the view only on a SECOND press, once the entry is already
    /// `QueueDeleteArmed` — the first press sends `ArmQueueDeleteMsg` instead. This message
    /// itself still deletes unconditionally, the way it always has: the two-press rule is a
    /// property of the click handler, not of the delete, so nothing here changes for a test
    /// (or a race) that dispatches it directly.
    | DeleteQueuedMsg of QueueId
    /// Arm (`Some`) or take back the arming (`None`) of a queued entry's delete — the
    /// confirm-before-destroy a mis-tap next to reorder needs, and did not have. `Some`
    /// replaces whatever was armed before it, the one-slot rule `ItemMenu` already uses;
    /// `None` is sent back by the entry's own wait (`armedMs`) when nobody
    /// confirms it, the same shape as `CopiedMsg`'s expiry.
    | ArmQueueDeleteMsg of QueueId option
    /// A fresh /claude status probe result (Plan 08).
    | ClaudeStatusMsg of ClaudePanel
    /// The Claude sign-in flow moved (the authorize tab opened, or the person cancelled).
    | ClaudeFlowMsg of ClaudeFlowState
    /// A Claude connection command moved (sent, accepted and now awaiting the status that
    /// will show it, or refused).
    | ClaudePendingMsg of Pending<ConnectionExpectation>
    /// The Claude panel's fields, as typed (`ClaudeViewState`).
    | ClaudeScopeChosen of string
    | ClaudeCodeTyped of string
    | ClaudeTokenTyped of string
    /// A press on the panel. Refused here when a field it needs is empty; otherwise it asks
    /// the session (`ClientEffect.Claude`) and waits for the answer.
    | ClaudePressedMsg of ClaudePress
    /// What a write came back with, for the call that asked, at the moment it arrived —
    /// which is when an accepted command's wait for the status starts (`Pending.Awaiting`).
    | ClaudeAnsweredMsg of ClaudeCall * ClaudeAnswer * at: int64
    /// A fresh /github status probe result (Plan 14).
    | GitHubStatusMsg of GitHubPanel
    /// The GitHub sign-in flow moved (the code came up, or the person cancelled).
    | GitHubFlowMsg of GitHubFlowState
    /// A GitHub connection command moved, exactly as Claude's does.
    | GitHubPendingMsg of Pending<ConnectionExpectation>
    /// The GitHub panel's fields, as typed (`GitHubViewState`).
    | GitHubScopeChosen of string
    | GitHubTokenTyped of string
    /// A press on the panel, as Claude's.
    | GitHubPressedMsg of GitHubPress
    /// What a write came back with, as Claude's.
    | GitHubAnsweredMsg of GitHubCall * GitHubAnswer * at: int64
    /// The interval before poll `round` has passed (`ClientModel.timers`).
    | GitHubPollDueMsg of round: int
    /// What poll `round` came back with.
    | GitHubPolledMsg of round: int * GitHubPollAnswer
    /// The clock, for every panel waiting on a query at once: one tick, because a deadline
    /// is about elapsed time and not about which panel is watching it. Fired by the deadline
    /// the model declares for each wait (`ClientModel.timers`), carrying the moment it fell
    /// due.
    | PendingWaitedMsg of now: int64
    /// The launch surface moved (typed, listed, chose, sent, answered, failed).
    | LaunchMsg of LaunchMsg
    /// The session's opening question put away: no repository, for now. A synced register
    /// like `SetModelMsg`'s, not launch view state, because it is the session's answer and
    /// not one client's (`SyncedSessionState.LaunchDismissed`).
    | DismissLaunchMsg
    /// This client sent the session a command, under this request id. Dispatched by the one
    /// verb that sends commands (`Client.Connection.Ask`), BEFORE the command leaves, so no
    /// answer can arrive ahead of the record of what it answers.
    | CommandSentMsg of RequestId * SessionCommand
    /// The session answered a command this client sent. The launch's add is the launch
    /// card's to answer; any other refusal is the notice (`ClientModel.Refused`).
    | CommandAnsweredMsg of RequestId * SessionCommandResult
    /// Pick the model this session's turns run on — `None` hands the choice back to the
    /// provider. One register, written like a gate: the reducer sets it and the Ylmish
    /// binding carries it to every peer.
    | SetModelMsg of ModelId option
    /// Open a chapter at this message, or close the one there — one message, because there is
    /// one act. Which way it goes is `Chapters.toggle`'s to decide, from the item and the
    /// verdicts already recorded: a message carrying the desired state would be a message
    /// whose sender had to know what the chapter policy defaults to.
    | ToggleChapterMsg of MessageId
    /// Call the chapter at this message something else — the whole name, as the field now
    /// reads, carried as `Ylmish.Text` so the EDIT crosses rather than the result. A message
    /// holding a plain string would be a message that clobbers whatever a peer typed in the
    /// same second, which is the one thing collaborative text exists to prevent.
    | EditChapterNameMsg of MessageId * Ylmish.Text
    /// One frame off the multiplexed query stream (Plan 15) — the declarations, or one
    /// query's current value. ONE message for the whole read surface, however many
    /// queries there are: a message per query would be a message per FUTURE query too.
    | QueryFrameMsg of QueryFrame
    // --- Terminals (Plan 13) ---------------------------------------------------------
    /// Transcript records for this terminal, in arrival order — live off the data channel,
    /// one per pty read, or a page's worth folded through here by `TerminalPageMsg`. Keyed
    /// by seq, so folding them is idempotent against the same records arriving in a page
    /// below.
    ///
    /// The fold is an insert and nothing else, and has to stay that cheap: a burst is many
    /// reads a second — `seq 100000` sent some 170 in under three seconds — each one its own
    /// message, ahead of the link's heartbeat in the same queue. What keeps a burst from
    /// costing a RENDER per record is the render itself, which draws at most once a frame
    /// (`Render.setState`, in the browser).
    | TerminalRecordsMsg of TerminalId * records: (int * TranscriptRecord) list
    /// A PAGE of a terminal's transcript — fetched over HTTP, or replayed from what this
    /// device kept: its records, the header when the page carried line 0, and how far the
    /// contiguous prefix now reaches. ONE message for the whole page, deliberately: this used
    /// to be one record message per record plus the two after, and back when every message
    /// was a render, a reopen replayed a session's kept 2,138 lines as 2,176 full re-renders
    /// of the page — 9.9s of the main thread on a laptop, minutes on a phone, during which no
    /// tap landed and the `/me` probe that would have said "session stopped" never ran. The
    /// render now draws at most once a frame (`Render.setState`), but a message per line is
    /// still a pass of the whole update loop per line. The fold is exactly the three it
    /// replaced, in the order they were dispatched; what changes is how often anything is
    /// asked to happen.
    | TerminalPageMsg of
        TerminalId * records: (int * TranscriptRecord) list * header: TranscriptHeader option * readThrough: int
    /// A terminal's transcript is this long. A hint that triggers a read, never data.
    | TerminalAvailableMsg of TerminalId * length: int
    /// The answer to transcript read `read` (`ClientEffect.ReadTranscript`): the page, or
    /// `None` for a read that failed. Folded as `TerminalPageMsg` only when it takes the read
    /// position somewhere; what is read next is decided from it (`ClientModel.reads`).
    | TranscriptReadMsg of TerminalId * read: int * TranscriptPage option
    /// A contiguous prefix of a terminal's transcript has been read through this seq.
    | TerminalReadThroughMsg of TerminalId * seq: int
    /// The transcript's header, from the chunk that carried line 0 (Plan 13, stage 3e).
    | TerminalHeaderMsg of TerminalId * TranscriptHeader
    /// A keyframe arrived for a terminal (Plan 14, stage 3): the screen a ranged replay
    /// starts from. Fetched when a tab needs one, never streamed — a keyframe is read by
    /// somebody opening a recording, not by everybody watching one grow.
    | TerminalKeyframeMsg of TerminalId * TranscriptKeyframe
    /// The live screen, recomposed (Plan 14, stage 6). Dispatched by the platform half,
    /// which owns the emulator: a screen is a projection an emulator maintains, and the
    /// reducer is pure.
    | TerminalScreenMsg of TerminalId * screen: LiveScreen
    /// This client's own view of a terminal was measured, and it had moved (Plan 13, stage
    /// 2b). Dispatched by the platform half, which is the only half that can measure a box —
    /// and it measures on the edges a render loop cannot see, a splitter dragged or a window
    /// turned, because those change the box without changing anything the model holds.
    | TerminalViewportMsg of TerminalId * Size
    /// Show a terminal in the pane, in a stated read of it (Plan 25, stage 2).
    ///
    /// ONE message for every way to a terminal — a tab in the strip, a row in the list, the
    /// watch toggle, catching back up to live — because each of them is the same act: name
    /// the mode the pane is in next. The six messages this replaces each cleared a different
    /// subset of four fields, which is what let a chip open a tab the list was still covering
    /// and let the list's rewind cancel itself.
    ///
    /// Showing a terminal OPENS it (Plan 20, stage 1): it is in the strip afterwards. Showing
    /// one also takes down any preview over the strip, because the reader has moved to a
    /// terminal and a preview is about the one they were on.
    | ShowInPaneMsg of TerminalMode
    /// Show this terminal and take the reader there: a queued command's chip, a row of the
    /// list. One message for both, so nothing can open a pane and leave focus behind it.
    | OpenInPaneMsg of TerminalMode
    /// Lay this preview over the pane's terminal, in place (P2-1) — the preview's own watch
    /// toggle, which keeps its focus because it never leaves the document.
    | ShowPreviewMsg of Preview
    /// Lay this preview over the pane's terminal and take the reader there: what a chip in
    /// the chat does. ONE preview at a time, so a second chip REPLACES the first rather than
    /// adding to anything — six chips read leave one preview and the strip as it was.
    | OpenPreviewMsg of Preview
    /// Take the preview down, back to the read of the terminal it was laid over — positioned
    /// where it was — with focus back on the chip that opened it. Its close and Escape are
    /// both this one act.
    | ClosePreviewMsg
    /// Show a terminal's history at one of its commands, scrolled to it, with focus in the
    /// pane — a block preview's "show in terminal". Takes the preview down on the way, since
    /// the reader has asked for the terminal instead.
    | ShowInTerminalMsg of TerminalId * BlockId
    /// A move only the document can make, asked for by a control that changes nothing in the
    /// model (`DomMove`).
    | MoveMsg of DomMove
    /// Put `text` on the clipboard and, if the platform lets it, say so on the box whose hook is
    /// `box` (`CopiedMsg`). How long it says so is the model's (`ClientModel.timers`).
    | CopyMsg of box: string * text: string
    /// Try the session again NOW, rather than when the supervised loop next would. A trigger,
    /// never a second schedule (Plan 20): it shortens the wait the lifecycle is already in, and
    /// earns its place on the one client the loop deliberately will not carry — a peer whose
    /// token was refused, which no amount of waiting fixes.
    | RetryNowMsg
    /// Ask the session for a terminal with this title, and remember that this client asked
    /// (`Opening`).
    ///
    /// One message for both halves, so no third `+ new` can be added that opens a terminal
    /// without recording that somebody here asked for it: asking and remembering that you
    /// asked are one act, and a caller that could do the first without the second is the dead
    /// button this exists to end. The terminal comes back as an event that says which USER
    /// opened it and cannot say which of their tabs did, so the count is the only thing that
    /// can tell the terminal THIS press asked for from one that merely belongs to the same
    /// person.
    | OpenTerminalMsg of title: string * sandbox: SandboxRef
    /// Rewind a LIVE terminal (Plan 14, stage 7): watch what it has recorded so far, from a
    /// transcript length pinned NOW while the terminal keeps running.
    ///
    /// Its own message rather than a `ShowInPaneMsg (WatchingBehind …)` a caller composes,
    /// because the pin is read off the feed at the moment of the rewind — a caller that had
    /// to look it up first could look it up wrong, or forget, and the rule belongs with the
    /// state it governs.
    | RewindTerminalMsg of TerminalId
    /// A rewound terminal's replay played off its end and caught up with the live edge: the
    /// pane goes back to reading the terminal, and the player the reader was in leaves the
    /// document under them.
    | ReplayCaughtUpMsg of TerminalId
    /// Open or close the content column.
    | ToggleContentMsg
    /// The sidebar's own chevron (and the drawer's scrim): bring the column on screen or put
    /// it away, always on its workspace face.
    | ToggleNavMsg
    /// Turn the column to its settings face, or back — bringing it on screen to do so.
    | ToggleSettingsMsg
    /// Take the reader TO settings, never back: `ToggleSettingsMsg`'s one-way sibling, for a
    /// call to action that is on screen while settings may already be open, and must not shut
    /// the panel it points at.
    | RevealSettingsMsg
    /// The stylesheet's breakpoint was crossed: the column is now beside the chat, or not.
    | ViewportMsg of wide: bool
    /// The reader set the split: the width wanted, held to what the chat can spare.
    | PaneSplitMsg of wanted: float
    /// The reader moved the split by `by` pixels from where it is — the separator's arrow
    /// keys. Nothing to step from until the window has been measured.
    | PaneNudgedMsg of by: float
    /// The browser measured the window: at boot, and whenever it changes size.
    | PaneWindowMsg of width: float
    /// The width this browser kept from an earlier visit, put back at boot. The reader's own
    /// choice, so it holds like one — but not a new one, so it is not written back.
    | PaneKeptMsg of width: float
    /// The reader of a surface read from its end left that end, or came back to it.
    | ReaderMovedMsg of TailSurface * following: bool
    /// Close the content column, and never open it: the shell's half of "on a phone, two
    /// sheets never cover the chat at once". The nav drawer and the pane are both overlays
    /// there, and opening the drawer over an open pane opened it UNDER the pane (both z-40,
    /// the pane later in the document) — open, and nowhere to be seen. Only the shell knows it
    /// is on a phone, so it sends this before it brings the drawer on screen. Not
    /// `ToggleContentMsg`, which would OPEN a shut pane, and which moves focus into or out of
    /// it: here the drawer arriving is what says where focus goes, and it says so itself.
    | HideContentMsg
    /// Open this item's actions menu, or shut it if it is the one already open. A toggle
    /// rather than an open, because the control that sends it is the same control either
    /// way — pressing the ellipsis a second time has to put the menu away.
    | ToggleItemMenuMsg of MessageId
    /// Unfold what this line holds, or fold it if it is open. One toggle for the one
    /// control, as with the menu — and one message for every fold on the timeline, because
    /// they are one control drawn in three places.
    | ToggleFoldMsg of FoldKey
    /// A fold the DOCUMENT opened or shut: the pane's runs are native `<details>`, whose
    /// `toggle` reports the state it now has. A set rather than a toggle because the event
    /// also fires for an open the model asked for itself, and a toggle would undo it.
    | FoldSetMsg of FoldKey * opened: bool
    /// Show this break's moment instead of its duration, or go back to the duration if it is
    /// already showing one. A toggle rather than a one-way reveal, for the reason the menu and
    /// the folds are: the control that sends it is the same control either way, and a label a
    /// press cannot put back is a label people stop pressing.
    | ToggleBreakTimeMsg of MessageId
    /// Shut whatever menu is open. Everything that dismisses one sends this: Escape, a
    /// press outside it, and choosing something from it.
    | CloseItemMenuMsg
    /// Put away the notice saying what the session last refused. A refusal is news, not a
    /// state: once it has been read there is nothing left for it to do.
    | DismissRefusalMsg
    /// Put away the notice saying somebody took a terminal's keyboard from this client. The
    /// lease bar it stood in place of says the rest: who has it, and how to take it.
    | DismissStolenMsg
    /// The keyboard went into the refusal notice drawn at this mount (`Some`), or left it
    /// (`None`). Told by the notice itself, so that whatever takes it away — its dismiss, or
    /// an acceptance — can hand focus on rather than strand it on `body`.
    | RefusalFocusMsg of RefusalMount option
    /// Open or shut the strip's menu of things to open (Plan 20, stage 1). Opening is a
    /// toggle rather than a pair, so the control that opened it is the control that shuts it
    /// and focus never has to go looking for a replacement.
    | TogglePaneMenuMsg
    | ClosePaneMenuMsg
    /// Go to the pivot's `all` page, or back from it (P2-2). A toggle because its shortcut is
    /// one key for both; the pivot item itself only ever opens it, as a selected tab pressed
    /// again does nothing. Opening it brings the pane with it — reaching for a terminal you
    /// cannot see is exactly the case where the pane is shut.
    | ToggleSwitcherMsg
    /// Go to the `all` page and take the reader there — SET, not flipped, so it lands there
    /// whatever was up: the sidebar's way to the terminals it does not list, pressed from a
    /// column that cannot see whether the page is already showing.
    | OpenAllMsg
    /// Leave the `all` page for the item it was laid over: Escape. Choosing from it leaves it
    /// too, as part of the choice (`ShowInPaneMsg`), and lands focus where the choice put the
    /// reader.
    | CloseSwitcherMsg
    /// Narrow the `all` page to one kind of terminal, or (`None`) widen it to every one —
    /// which decides, now, the rows it shows (`ListFilter`).
    | FilterListMsg of TerminalKind option
    /// Something was copied to the clipboard (`Some` the hook of the box it came from), or
    /// the moment for saying so has passed (`None`).
    ///
    /// The clipboard write itself is the browser's — a permission the page may be refused —
    /// so this is dispatched only where the write SUCCEEDED. A confirmation the reducer
    /// could set on its own would be a claim about a clipboard nothing here has read.
    | CopiedMsg of string option
    /// Ensure the composer slot for (terminal, author) exists, carrying the queue key it
    /// becomes when sent. The author's own call, exactly as for a message draft.
    | EnsureTerminalDraftMsg of TerminalId * PeerId * QueueId
    /// Send = enqueue: the slot's command moves into the terminal's queue at the tail
    /// under the key the slot has carried since publication, and the slot clears.
    | SendTerminalDraftMsg of TerminalId * PeerId
    /// Drop a composer slot without sending it.
    | DiscardTerminalDraftMsg of TerminalId * PeerId
    /// Delete a queued command. Until consumed, deletion wins.
    | DeletePendingMsg of QueueId
    /// Reorder a queued command within its terminal: one fractional-index register write.
    | ReorderPendingMsg of QueueId * order: float
    /// Take a terminal's stdin — enter live mode, stealing the lease if another peer holds it
    /// (Plan 13, stage 2e). There is one control because there is one act, and any peer may
    /// perform it. Asked of the session (`ClientEffect.TakeTerminal`); the lease arrives as a
    /// `TerminalLeaseTaken` event, so the model changes only when every peer's does.
    | TakeTerminalMsg of TerminalId
    /// Hand a terminal this peer holds back to block mode. Refused by the session unless this
    /// peer is the holder.
    | ReleaseTerminalMsg of TerminalId
    /// Type the shell instrumentation in again after a terminal stopped marking (Plan 13,
    /// stage 2f). Any peer may — it repairs rather than takes.
    | RearmTerminalMsg of TerminalId
    /// Ask the provider for a closed terminal's stream again (Plan 19, step 4).
    | ReattachTerminalMsg of TerminalId
    /// End a terminal — the one verb that stops what runs in it, and what a tab's × is (P2-2).
    ///
    /// Dispatched by a kill control only on a SECOND press, once the terminal is `KillArmed` —
    /// the first press sends `ArmKillMsg` instead (`ClientModel.killPress`). Like `DeleteQueuedMsg`, the two-press rule is a
    /// property of the control, not of this message, which still ends the terminal whoever
    /// sends it.
    | CloseTerminalMsg of TerminalId
    /// Stop the block running in a terminal — ^C to it, the terminal left standing. What a
    /// running block's Stop sends, and what Ctrl-C in an EMPTY command line sends: there the
    /// key has no other meaning, and with text in the line it stays the platform's copy. A
    /// request of the session only when the terminal HAS a running block, which is decided
    /// here, from the model, rather than at either control (`update`).
    | InterruptTerminalMsg of TerminalId
    /// A terminal's kill was pressed — the strip's ×, Delete on its tab, a row's kill in the
    /// switcher. What the press MEANS, arming or ending, is `ClientModel.killPress` asked of
    /// the model as it is when the press lands, not of the one the control was last drawn
    /// from: the page draws at most once a frame, so a second press inside the frame that
    /// armed it would otherwise be judged by a picture that had not caught up.
    | KillPressedMsg of TerminalId
    /// Arm (`Some`) or take back the arming (`None`) of a terminal's kill. `Some` replaces
    /// whatever was armed before it; `None` is sent by the wait (`armedMs`), by Escape on the
    /// armed control, and by focus leaving it.
    | ArmKillMsg of TerminalId option
    /// Put a CLOSED terminal's tab away: the × a closed item wears in place of its kill, and
    /// Delete on any closed item.
    ///
    /// A closed tab stays in the strip until this (`ClientModel.Tabs`), so the thing a reader
    /// was working with does not vanish as it finishes. This is the one way to say "done with
    /// this", and only for a terminal that has closed: the refusal is HERE rather than in the
    /// control, so no route can drop the tab of a terminal still running somewhere (P2-2).
    /// The pane moves on to the tab beside it, or to nothing. Put away, it stays away until
    /// the reader opens that terminal again.
    | DismissTabMsg of TerminalId
    /// Ask the session to cancel the running agent turn (Step 17). The outcome arrives as
    /// events: `AgentTurnInterrupted` on success, or nothing if the turn already finished.
    | InterruptTurnMsg of AgentTurnId
    /// Consent to what a repo asks for (Plan 27). Carries the set that is on screen, so the
    /// session can refuse if the file moved between the screen and the button.
    | ApproveRepoCapabilitiesMsg of RepoRef * granted: string list

/// What a message asks of the world outside the model, as a value `ClientModel.update` returns
/// beside the next model.
///
/// Data rather than an Elmish `Cmd`, because a `Cmd` is a function: a test can see that one
/// was returned and never what it would do. A value says which request a message makes, in
/// the cheap tier, next to the fold that decided it; carrying it out is the composition
/// root's, which is handed the ports to do it with (`Client.makeProgram`).
///
/// Qualified access because the Domain's commands share these names — `CloseTerminal` is the
/// request as it reaches the session, and this is this client asking for it.
[<RequireQualifiedAccess>]
type ClientEffect =
    | TakeTerminal of TerminalId
    | ReleaseTerminal of TerminalId
    | RearmTerminal of TerminalId
    | ReattachTerminal of TerminalId
    | CloseTerminal of TerminalId
    | InterruptTerminal of TerminalId
    | OpenTerminal of title: string * sandbox: SandboxRef
    | InterruptTurn of AgentTurnId
    | ApproveRepoCapabilities of RepoRef * granted: string list
    | Launch of LaunchEffect
    | Claude of ClaudeCall
    | GitHub of GitHubCall
    /// Ask poll `round` of the device flow begun for this scope.
    | GitHubPoll of round: int * scope: string
    | Move of DomMove
    /// The screen a ranged replay starts from (`ClientModel.missingKeyframe`), answered as a
    /// `TerminalKeyframeMsg`.
    | FetchKeyframe of TerminalId * seq: int
    /// Tell the Session the size of a pty this peer holds the lease on, so the program drawing
    /// on it lays its screen out to the box the holder is looking at (`ClientModel.ptyResizes`).
    | ResizeTerminal of TerminalId * Size
    /// Tell everyone else where this peer is: its caret and what it has open, both halves on
    /// one frame (`ClientModel.presenceToSend`).
    | SendPresence of focus: Focus option * viewing: ViewRef option
    /// Read the event log after this offset, under read number `read` — one fetch or one
    /// frame, answered as `EventsReadMsg` carrying the same number (`ClientModel.reads`).
    | ReadEvents of read: int * after: EventOffset option
    /// Read a terminal's transcript from this line, under read number `read`, answered as
    /// `TranscriptReadMsg` carrying the same number (`ClientModel.reads`).
    | ReadTranscript of TerminalId * read: int * fromSeq: int
    | Remember of Preference
    | Copy of box: string * text: string
    | RetryNow

/// What each of the Claude panel's presses asks the session for, or why it asks nothing.
/// One function for both halves of a press — the state it moves to and the effect it
/// sends — so they cannot come to disagree about whether there was a call at all.
module ClaudePress =

    let call (press: ClaudePress) (claude: ClaudeViewState) : Result<ClaudeCall, string> =
        match press with
        | ClaudePress.Connect ->
            Ok
                { Action = ClaudeAction.Begin
                  Request = ClaudeRequest.scoped claude.Scope
                  Expect = None }
        | ClaudePress.Complete ->
            // The scope selector is unmounted while awaiting; the flow carries it.
            let scope =
                match claude.Flow with
                | ClaudeAwaitingCode (_, scope) -> scope
                | ClaudeIdle -> "mine"
            match claude.Code with
            | "" -> Error "paste the code first"
            | code ->
                Ok
                    { Action = ClaudeAction.Complete
                      Request = { Scope = scope; Code = Some code; Token = None }
                      Expect = Some { Scope = scope; Connected = true } }
        | ClaudePress.SaveToken ->
            match claude.Token with
            | "" -> Error "paste a token first"
            | token ->
                Ok
                    { Action = ClaudeAction.Token
                      Request = { Scope = claude.Scope; Code = None; Token = Some token }
                      Expect = Some { Scope = claude.Scope; Connected = true } }
        | ClaudePress.Disconnect scope ->
            Ok
                { Action = ClaudeAction.Disconnect
                  Request = ClaudeRequest.scoped scope
                  Expect = Some { Scope = scope; Connected = false } }

/// What each of the GitHub panel's presses asks the session for, or why it asks nothing.
module GitHubPress =

    let call (press: GitHubPress) (github: GitHubViewState) : Result<GitHubCall, string> =
        match press with
        // Nothing for the status to show yet: the grant lands when the human approves the
        // code this puts on screen.
        | GitHubPress.Connect ->
            Ok { Action = GitHubAction.Begin; Request = GitHubRequest.scoped github.Scope; Expect = None }
        | GitHubPress.SaveToken ->
            match github.Token with
            | "" -> Error "paste a token first"
            | token ->
                Ok
                    { Action = GitHubAction.Token
                      Request = { Scope = github.Scope; Token = Some token }
                      Expect = Some { Scope = github.Scope; Connected = true } }
        | GitHubPress.Disconnect scope ->
            Ok
                { Action = GitHubAction.Disconnect
                  Request = GitHubRequest.scoped scope
                  Expect = Some { Scope = scope; Connected = false } }

/// The device flow's poll, beside the state it reads.
module GitHubPoll =

    /// The scope to poll for, when poll `round` is the one this panel is waiting to ask — and
    /// nothing for a round already asked, a flow cancelled or granted, or no flow at all.
    let due (round: int) (github: GitHubViewState) : string option =
        match github.Flow, github.Polling with
        | GitHubAwaitingApproval (_, _, scope, _), PollWaiting waiting when waiting = round -> Some scope
        | _ -> None

    /// The wait before the next poll, while a code is on screen and nothing is out. Keyed by
    /// the code and the round, so each answer arms a fresh wait and a new flow never inherits
    /// an old one's.
    let timer (github: GitHubViewState) : Timer<ClientMsg> list =
        match github.Flow, github.Polling with
        | GitHubAwaitingApproval (userCode, _, _, interval), PollWaiting round ->
            [ { Key = [ "github-poll"; userCode; string round ]
                After = max 1 interval * 1000
                Fire = GitHubPollDueMsg round } ]
        | _ -> []

module ClientModel =

    /// Is `latest` strictly ahead of `processed` (i.e. there is more to consume)?
    let private isBehind (processed: EventOffset option) (latest: EventOffset option) : bool =
        match latest with
        | None -> false
        | Some latest ->
            match processed with
            | None -> true
            | Some processed -> EventOffset.value latest > EventOffset.value processed

    /// The model for a freshly loaded client: connecting, nothing consumed, idle. The
    /// optimistic start is deliberate and is what `connectionReport` already assumes ("Connecting
    /// is the state every client starts in"): a client that has just loaded IS about to connect,
    /// so the first paint says `Connecting` — which the report keeps silent — rather than wearing
    /// the "not connected" strip for the ~75ms until the transport is asked and then reflowing the
    /// panes under it up by the strip's height. A genuine failure replaces this with
    /// `Disconnected reason` and earns the strip.
    let init (peer: PeerState) : ClientModel =
        { Peer = peer
          Connection = Connecting
          Session = None
          Manager = None
          EphemeralStorage = false
          Build = None
          CanKeepHistory = true
          HistoryRead = false
          Synced = (SyncedSessionState.empty CollabText.ylmish)
          Conversation = ConversationProjection.empty
          Approvals = RepoApprovals.empty
          Launch = Launch.empty
          Repos = Repos.ReposProjection.empty
          Timeline = TimelineProjection.empty
          EventConsumer =
            { LastProcessedOffset = None
              LatestKnownOffset = None
              IsCatchingUp = false
              CatchUpIsSlow = false
              // Nothing has failed yet; the first read decides.
              Feed = FeedLive
              // Nothing has been looked at yet; the replay decides.
              MissingBefore = None
              Reading = None }
          Agent = { ActiveTurn = None; Quiet = None; Interrupting = None }
          Presence = Map.empty
          Caret = None
          Peers = Map.empty
          Here = Here.empty
          Attribution = Attribution.empty
          Composer = Unchosen
          Environment = EnvironmentNotStarted
          Terminals = Projection.empty
          TerminalFeeds = Map.empty
          TranscriptReads = Map.empty
          ReadsAsked = 0
          TerminalKeyframes = Map.empty
          KeyframesAsked = Set.empty
          TerminalScreens = Map.empty
          TerminalViewports = Map.empty
          Tabs = []
          Opening = 0
          KillPending = None
          KillArmed = None
          Pane = None
          TerminalsOpen = false
          PaneMemory = None
          PaneRemembered = false
          HeardThrough = false
          PaneOpensItself = false
          Seen = Map.empty
          Column = Column.initial
          PaneRoom = PaneRoom.unmeasured
          Away = Set.empty
          ItemMenu = None
          PaneMenu = false
          Switcher = false
          ListFilter = ListFilter.All
          Refused = None
          Stolen = None
          Asked = Map.empty
          OpenFolds = Set.empty
          DatedBreaks = Set.empty
          QueueDeleteArmed = None
          Copied = None
          Claude =
            { Status = None
              Flow = ClaudeIdle
              Pending = Pending.Ready
              Scope = "mine"
              Code = ""
              Token = "" }
          GitHub =
            { Status = None
              Flow = GitHubIdle
              Pending = Pending.Ready
              Scope = "mine"
              Token = ""
              Polling = PollWaiting 0 }
          Queries = { Declared = []; Values = Map.empty } }

    /// Advance the latest-known offset and recompute the catch-up indicator. "Slow" is a
    /// property of a catch-up that is STILL RUNNING, so it dies with the catch-up it
    /// described — the timer that set it never has to be raced.
    let private withLatestKnown (latest: EventOffset option) (consumer: EventConsumerState) : EventConsumerState =
        let catchingUp = isBehind consumer.LastProcessedOffset latest
        { consumer with
            LatestKnownOffset = latest
            IsCatchingUp = catchingUp
            CatchUpIsSlow = catchingUp && consumer.CatchUpIsSlow }

    let private withSynced (synced: SyncedSessionState<Ylmish.Text>) (model: ClientModel) : ClientModel =
        { model with Synced = synced }

    /// A repo, or anything said - the fact that retires the launch surface for good; see
    /// `Launch.offered`'s own doc for why these two and nothing wider.
    let private launchBegun (model: ClientModel) : bool =
        not (List.isEmpty model.Repos.Repos)
        || model.Conversation.Items
           |> List.exists (fun item ->
               match item.Content with
               | ItemContent.Message _ -> true
               // A stop is a turn's, and a turn follows a message: never the first thing.
               | ItemContent.Act _
               | ItemContent.Stopped _ -> false)

    /// Whether the launch surface stands at the head of the timeline. Reads the anchor
    /// `reconcileLaunch` keeps decided, not the live connection - `Launch.offered`'s doc,
    /// and `Launch.anchor`'s beside it, say why a live read is the wrong read for this.
    let launchOffered (model: ClientModel) : bool =
        Launch.offered (launchBegun model) model.Synced.LaunchDismissed model.Launch

    /// Keeps `model.Launch.Anchored` decided. Run after every message (`update`, below,
    /// pipes its whole result through this), so whichever one first carries the client past
    /// eligible - connected, historyRead, its own read of the log, caught up, still
    /// unstarted - is the one that anchors it, for the rest of this client's life on this
    /// session. Idempotent: a model already anchored, or not yet eligible, comes back with
    /// the same `Launch` it was given, so paying this after every message costs a few
    /// comparisons on the ones that were never going to move it.
    let private reconcileLaunch (model: ClientModel) : ClientModel =
        { model with
            Launch =
                Launch.anchor
                    (model.Connection = Connected)
                    model.HistoryRead
                    model.EventConsumer.LatestKnownOffset
                    model.EventConsumer.IsCatchingUp
                    (launchBegun model)
                    model.Synced.LaunchDismissed
                    model.Launch }

    /// Whose draft the composer is showing — the resolved answer to `ComposerChoice`, and the
    /// only place the "join what is already being written" default lives.
    ///
    /// A choice is honoured while that draft still exists (a sent or discarded one falls back).
    /// Unchosen prefers your own draft if you have one, then the drafts already in flight in a
    /// stable order, and finally your own empty composer — so a session where someone is midway
    /// through a message opens on THEIR words, not on a second blank box beside them.
    let composerTarget (model: ClientModel) : PeerId =
        let mine = model.Peer.PeerId
        let others =
            model.Synced.Drafts
            |> Map.toList
            |> List.map fst
            |> List.filter (fun peer -> peer <> mine)
        match model.Composer with
        | Own -> mine
        | Joined peer when Map.containsKey peer model.Synced.Drafts -> peer
        | Joined _
        | Unchosen ->
            if Map.containsKey mine model.Synced.Drafts then mine
            else
                match others with
                | first :: _ -> first
                | [] -> mine

    /// The drafts NOT in the composer, in stable order: the collapsed summaries.
    let collapsedDrafts (model: ClientModel) : PeerId list =
        let target = composerTarget model
        model.Synced.Drafts |> Map.toList |> List.map fst |> List.filter (fun peer -> peer <> target)

    /// Whether a draft carries anything a send would take.
    ///
    /// The SLOT is the answer, not a second measurement: `DraftSlot` publishes a peer's slot
    /// exactly while their body has content and retracts it the moment it empties, so this is
    /// the same fact the send path already acts on. Serializing the body again here would cost
    /// a Markdown pass per render and, worse, give the composer a way to disagree with the rule
    /// about whether there is anything to send.
    let draftHasContent (peer: PeerId) (model: ClientModel) : bool =
        Map.containsKey peer model.Synced.Drafts

    /// Who is editing this draft right now, by their live caret (never the local peer — you are
    /// not your own collaborator). Names come from presence, which is where a live caret's name
    /// already travels.
    let editorsOf (peer: PeerId) (model: ClientModel) : (ActorRef * string) list =
        model.Presence
        |> Map.toList
        |> List.filter (fun (_, presence) -> presence.Focus |> Option.exists (fun f -> f.Field = DraftBody peer))
        |> List.map (fun (editor, presence) -> editor, presence.DisplayName)

    /// The strip with this terminal in it, in the slot the order terminals OPENED in gives
    /// it (`order`, the projection's) — ahead of the first tab that opened after it — if it
    /// is not already there.
    ///
    /// Never moved, and never moving another: a strip that re-orders under a reader is the
    /// thing the order was a list for in the first place. And by opening order rather than at
    /// the end, so a terminal put away and opened again comes back where it stood rather than
    /// after everything opened since (F2). ONE adder, because every way a terminal gets into
    /// the strip has to agree about what "already there" means and where "there" is.
    let withTab (order: TerminalId list) (terminal: TerminalId) (tabs: TerminalId list) : TerminalId list =
        if List.contains terminal tabs then tabs
        else
            let rank (id: TerminalId) = List.tryFindIndex (fun t -> t = id) order
            match rank terminal with
            | None -> tabs @ [ terminal ]
            | Some mine ->
                match tabs |> List.tryFindIndex (fun tab -> rank tab |> Option.exists (fun theirs -> theirs > mine)) with
                | Some before -> List.insertAt before terminal tabs
                | None -> tabs @ [ terminal ]

    /// A terminal's tab, as its DOM hooks carry it — the terminal's prose spelling
    /// (`ViewRef.said`), which is also how a remembered strip writes it down (`PaneMemory`).
    let tabKey (terminal: TerminalId) : string = ViewRef.said (ViewingTerminal terminal)

    /// Which terminal the pane is about: the stored choice while it is still in the strip,
    /// else the first OPEN terminal in the strip. Resolved rather than stored, for the same
    /// reason `composerTarget` is: a choice that outlives what it pointed at is a blank pane
    /// nobody asked for. The default lands somewhere you can type.
    ///
    /// Only ever a member of `Tabs`, and that is the rule the strip stands on: the strip IS
    /// `Tabs`, so whatever terminal the pane is about has a tab there. It used to resolve a
    /// third way — the session's first open terminal, whoever's it was — and the strip drew
    /// that default as a tab it was not. A client with nothing open sees the empty pane and its
    /// New terminal, and reaches everybody else's terminals through the list.
    ///
    /// The choice is the read SHOWING, or the one a preview is laid over (`PaneMode.subject`):
    /// the strip, the head and presence answer "which terminal am I working with", which a
    /// preview does not change. A choice naming a CLOSED terminal survives while its tab does —
    /// which is until the reader puts it away — and it is how the switcher opens a recording.
    let selectedTerminal (model: ClientModel) : TerminalId option =
        let exists (terminal: TerminalId) = Projection.tryFind terminal model.Terminals |> Option.isSome
        match model.Pane |> Option.bind PaneMode.subject |> Option.map TerminalMode.terminal with
        | Some chosen when exists chosen && List.contains chosen model.Tabs -> Some chosen
        | _ ->
            // Open, because this default exists to land somewhere a person can TYPE. A
            // recording is a fine thing to read and a poor place to arrive with nothing chosen.
            model.Tabs
            |> List.tryFind (fun terminal ->
                Projection.tryFind terminal model.Terminals |> Option.exists (fun view -> view.IsOpen))

    /// What choosing `terminal` from a list shows: the read the pane is already in when it is
    /// already about that terminal — a rewind, a scroll position — as pressing its tab keeps
    /// them, and its text otherwise. ONE answer for every list a terminal is chosen from (the
    /// `all` page, the sidebar's environment), so two lists cannot open one terminal two ways.
    let chosenRead (terminal: TerminalId) (model: ClientModel) : TerminalMode =
        model.Pane
        |> Option.bind PaneMode.subject
        |> Option.filter (fun mode -> TerminalMode.terminal mode = terminal)
        |> Option.defaultValue (Reading terminal)

    /// The preview laid over the pane, if one is on screen (P2-1).
    let preview (model: ClientModel) : Preview option =
        model.Pane |> Option.bind PaneMode.preview

    /// What a preview is laid OVER, given the terminal read the pane is about now: the
    /// terminal the subject belongs to — that same read, positioned where it was, when it is
    /// the one already up — and for a file, which belongs to no terminal, whatever is up.
    let private underFor (subject: PreviewSubject) (current: TerminalMode option) : TerminalMode option =
        match PreviewSubject.terminal subject with
        | Some terminal ->
            match current with
            | Some mode when TerminalMode.terminal mode = terminal -> Some mode
            | Some _ | None -> Some (Reading terminal)
        | None -> current

    /// The strip held to what it is (P2-1): terminals the session has.
    ///
    /// Run after every message, like `recall`, because the rule is about the STATE and not
    /// about which message changed it. A terminal CLOSING is not a reason to leave: it used
    /// to be — a closed tab went at once unless it was selected, and when the reader chose
    /// another if it was — and on a phone that read as tabs blinking in and out, and one
    /// coming back after everything else when a chip laid a preview over it (F2). A closed
    /// tab stays where it stood until the reader puts it away (`DismissTabMsg`).
    let private settle (model: ClientModel) : ClientModel =
        let keeps (terminal: TerminalId) = Projection.tryFind terminal model.Terminals |> Option.isSome
        if List.forall keeps model.Tabs then model
        else { model with Tabs = model.Tabs |> List.filter keeps }

    /// The order terminals OPENED in, which is the strip's order (`withTab`).
    let private openOrder (terminals: Projection) : TerminalId list =
        terminals.Terminals |> List.map (fun view -> view.TerminalId)

    /// The split between the chat and the pane this browser lays out (`PaneSplit.resolve`);
    /// `None` until the window has been measured.
    let paneSplit (model: ClientModel) : PaneSplit option =
        model.PaneRoom.Window
        |> Option.map (fun window -> PaneSplit.resolve model.Column window model.PaneRoom.Chosen)

    /// What this browser should remember of the pane — what a reload must give back (P0-4).
    /// The CHOICE rather than `selectedTerminal`'s resolution of it: with nothing chosen the
    /// default is worked out again on the way back, from the same strip. A preview is not
    /// remembered: it was a glance, and a reload comes back to the terminal it was over.
    let paneMemory (model: ClientModel) : PaneMemory =
        { PaneMemory.Tabs = model.Tabs
          PaneMemory.Selected = model.Pane |> Option.bind PaneMode.subject |> Option.map TerminalMode.terminal
          PaneMemory.Open = model.TerminalsOpen
          PaneMemory.Seen = model.Seen }

    /// A freshly loaded client, given what this browser remembered of the pane for this
    /// session. The column's open bit is applied now, because it needs nothing checked and is
    /// the part a person sees move; the strip comes back as the log names its terminals
    /// (`PaneMemory`'s doc, and `recall` below).
    ///
    /// `None` — a session this browser has never seen, or storage that would not answer — is
    /// the client exactly as `init` made it.
    let remembered (memory: PaneMemory option) (model: ClientModel) : ClientModel =
        match memory with
        | None -> model
        | Some memory ->
            { model with
                TerminalsOpen = memory.Open
                PaneMemory = Some memory
                PaneRemembered = true
                // Applied now rather than held, because it needs nothing checked: a terminal
                // the session no longer has is simply never asked about.
                Seen = memory.Seen }

    /// Whether this client has read the log through to where the session says it ends — the
    /// moment a remembered strip can be checked against what the session has. Connected,
    /// because only the session knows where its log ends; the local store read, because it
    /// is read first and holds most of it; and caught up.
    let private readThrough (model: ClientModel) : bool =
        model.Connection = Connected && model.HistoryRead && not model.EventConsumer.IsCatchingUp

    /// Latch `HeardThrough` on whichever message first carries the client past `readThrough`.
    /// Run last after every message (`fold`), so that it is the fold's own verdict and no
    /// message has to remember to set it.
    let private heard (model: ClientModel) : ClientModel =
        if model.HeardThrough || not (readThrough model) then model else { model with HeardThrough = true }

    /// The terminal this person is LOOKING at: the pane's terminal, while the pane is on screen
    /// and nothing is laid over it — not the `all` page, not a preview. What the pane is ABOUT
    /// (`selectedTerminal`) stays the same under both, but its output is not on screen, so a
    /// command finishing there is as unseen as one in another tab.
    ///
    /// Whether the browser tab itself is in front is not known here: a person on another
    /// window entirely is still counted as looking at what their pane shows.
    let looking (model: ClientModel) : TerminalId option =
        if not model.TerminalsOpen || model.Switcher || Option.isSome (preview model) then None
        else selectedTerminal model

    /// Keep `Seen` (its doc). Nothing while the log is still being REPLAYED: what arrives
    /// then is history, and a terminal part-way through its own would be frozen half-seen.
    /// From the message that carries the client past the read-through line, the terminal being
    /// looked at has been seen up to now; a terminal this browser has no record of has been
    /// seen up to wherever the history went — it is not news, and a first visit marking every
    /// terminal that ever ran a command would be marking all of them — unless it is met AFTER
    /// that line, when it is news itself and has been seen not at all. A remembered record
    /// (`remembered`) is left as it was, so what finished while this person was away is news.
    ///
    /// Run after every message, before `heard`, so the message that crosses the line can be
    /// told from those after it.
    let private notice (model: ClientModel) : ClientModel =
        if not model.HeardThrough && not (readThrough model) then model
        else
            let looked = looking model
            let seen =
                model.Terminals.Terminals
                |> List.fold
                    (fun (seen: Map<TerminalId, CommandTally>) view ->
                        let terminal = view.TerminalId
                        if looked = Some terminal then Map.add terminal (CommandTally.ofView view) seen
                        elif Map.containsKey terminal seen then seen
                        elif model.HeardThrough then Map.add terminal CommandTally.zero seen
                        else Map.add terminal (CommandTally.ofView view) seen)
                    model.Seen
            if seen = model.Seen then model else { model with Seen = seen }

    /// What a terminal has finished since this person last looked at it, if anything — its
    /// tab's and its row's "unseen" mark. Something that FAILED in that time wins over
    /// everything that went through, because it is the one a person has to go and read.
    ///
    /// Never while the log is still being replayed (`HeardThrough`): before then a remembered
    /// terminal is part-way through its own history, and would read as news it is not.
    let unseen (terminal: TerminalId) (model: ClientModel) : Unseen option =
        match Projection.tryFind terminal model.Terminals with
        | Some view when model.HeardThrough ->
            let now = CommandTally.ofView view
            let seen = Map.tryFind terminal model.Seen |> Option.defaultValue CommandTally.zero
            if now.Finished <= seen.Finished then None
            elif now.Failed > seen.Failed then Some Unseen.Failed
            else Some Unseen.Succeeded
        | Some _ | None -> None

    /// What a terminal is to this reader, in the order the answers win: closed, then running
    /// — what it is doing now — then what it finished that they have not seen, then whether
    /// its last command failed. Its mark and the `all` page's filters both read this.
    let terminalState (view: TerminalView) (model: ClientModel) : TerminalState =
        if not view.IsOpen then TerminalState.Closed
        elif Option.isSome (Projection.runningBlock view) then TerminalState.Running
        else
            match unseen view.TerminalId model with
            | Some Unseen.Failed -> TerminalState.UnseenFailed
            | Some Unseen.Succeeded -> TerminalState.UnseenOk
            | None ->
                match view.Blocks |> List.tryLast |> Option.map (fun block -> block.Status) with
                | Some (BlockFinished (CommandFailed _ | CommandTimedOut | CommandExecutionFailed _))
                | Some (BlockRejected _) -> TerminalState.Failed
                | Some (BlockFinished (CommandSucceeded _))
                | Some (BlockEnded _)
                | Some BlockRunning
                | None -> TerminalState.Idle

    /// Put back what this browser remembered of the pane (P0-4), terminal by terminal as the
    /// log names them, and let go of the memory once the log has been read through. Run after
    /// every message, like `reconcileLaunch`, so whichever message brings a remembered
    /// terminal in is the one that restores its tab, and whichever carries the client past the
    /// line — the last page of catch-up, or the connection itself when the local store already
    /// held everything — is the one that settles it. Idempotent: with no memory held it returns
    /// the model it was given.
    ///
    /// AS the log arrives, not once it has: catch-up is a fraction of a second on a quiet
    /// session and tens of seconds on a long one under load, and a pane that waited for the end
    /// of it showed the strip's FIRST terminal on top all that while — the default for a pane
    /// with nothing chosen — so a reload looked like it had forgotten the terminal you were on,
    /// and a person who started typing typed into the wrong one. The terminal you were on is on
    /// top from the page that names it.
    ///
    /// The remembered strip REPLACES the one history rebuilt. Replaying the log reopens every
    /// terminal this person ever opened, including the ones they had since closed, and the
    /// strip they had is the answer to which of those they wanted. A terminal the session does
    /// not have (yet) has no tab; the memory is held until the log has been read through, so
    /// one named on a later page is not lost, and one the session no longer has is gone for
    /// good once it has. A closed one comes back closed, where it stood: it was never put
    /// away, so a reload does not put it away either (F2).
    ///
    /// What this person has done SINCE loading wins over what they had: a terminal chosen
    /// before the log had arrived stays chosen, and stays in the strip. The choice this
    /// restores is one of those too, so a later page never moves it.
    let private recall (model: ClientModel) : ClientModel =
        match model.PaneMemory with
        | Some memory ->
            let restored =
                memory.Tabs
                |> List.filter (fun terminal -> Projection.tryFind terminal model.Terminals |> Option.isSome)
                |> List.distinct
            let tabs =
                match model.Pane |> Option.bind PaneMode.subject |> Option.map TerminalMode.terminal with
                | Some chosen -> withTab (openOrder model.Terminals) chosen restored
                | None -> restored
            let pane =
                match model.Pane with
                | Some _ -> model.Pane
                | None ->
                    memory.Selected
                    |> Option.filter (fun terminal -> List.contains terminal tabs)
                    |> Option.map (Reading >> OnTerminal)
            { model with
                Tabs = tabs
                Pane = pane
                PaneMemory = if readThrough model then None else model.PaneMemory }
        | None -> model

    /// What the pane LANDS on when it comes on screen with nothing chosen: the reader's own tab
    /// where the strip offers one (`selectedTerminal`), and otherwise, over a session with
    /// terminals open, the switcher (P2-2). The strip holds only what this reader opened, so a
    /// fresh browser on a session whose terminals are the agent's or a colleague's has an empty
    /// strip, and an empty pane saying "New terminal" over a running build is the one thing the
    /// pane must not land on. The switcher is what answers "what is here".
    ///
    /// ONE rule for every way the pane comes up onto nothing. It was the desktop's alone
    /// (`openOfItself`), so a phone — whose pane never opens itself, and comes up by its edge
    /// tab — landed every fresh viewer on exactly that empty pane, over six running terminals.
    /// Nothing changes once something is up: a preview, the switcher, or a terminal chosen.
    let private landed (model: ClientModel) : ClientModel =
        if model.Switcher
           || Option.isSome (selectedTerminal model)
           || Option.isSome (preview model)
           || List.isEmpty (Projection.openTerminals model.Terminals) then
            model
        else { model with Switcher = true }

    /// The pane's other landing: the log arriving under a pane ALREADY on screen. Before the
    /// read-through line "this session has no terminal" and "this client has not heard about
    /// it yet" look identical, so a pane brought up early — the edge tab pressed before the log
    /// arrived, or the pane put back open by a reload's memory (`remembered`) over a strip that
    /// had nothing in it — lands on the empty pane honestly, and is landed again by the message
    /// that carries the client past that line (`heard`, which runs after this and latches it).
    let private landsWhenHeard (model: ClientModel) : ClientModel =
        if model.HeardThrough || not model.TerminalsOpen || not (readThrough model) then model
        else landed model

    /// Where the keyboard goes when `landsWhenHeard` put the switcher up under a pane that was
    /// already on screen: whatever the hand was on in the empty pane went with it, so onto the
    /// switcher — and only if it was dropped (`IfDropped`), because an arrival does not know
    /// where the hand is. Not for a pane that came up on the same message (`openOfItself`):
    /// focus resting on `body` then is a page that has just loaded, not a control that went.
    let heardLanding (before: ClientModel) (after: ClientModel) : DomMove option =
        let crossed = not before.HeardThrough && after.HeardThrough
        if before.TerminalsOpen && crossed && not before.Switcher && after.Switcher then
            Some (DomMove.IfDropped DomMove.FocusSwitcher)
        else None

    /// The pane opening itself on a desktop's first look at a session (P1-4), once the log has
    /// been read through — the same line `recall` lets go of its memory at, and for the same
    /// reason: before it, "this session has no terminal" and "this client has not heard about
    /// it yet" look identical.
    ///
    /// Only when nothing else has answered for the column: no memory of it (`PaneRemembered`,
    /// whose open bit is that person's own answer), a screen it can sit beside the chat on, and
    /// nothing having moved it since the page loaded (`before` against `model`: a column opened
    /// from a chip or shut by its chevron before the log arrived is somebody's decision, and
    /// this one would only be overriding it). Spent either way, so it happens at most once:
    /// a person who shuts the pane afterwards is not argued with by the next page of events.
    ///
    /// Opens, and never shuts: a session with nothing open leaves the column as the page found
    /// it. What it opens ONTO is what the pane always lands on (`landed`).
    let private openOfItself (before: ClientModel) (model: ClientModel) : ClientModel =
        if not model.PaneOpensItself then model
        elif model.TerminalsOpen <> before.TerminalsOpen then { model with PaneOpensItself = false }
        elif not (readThrough model) then model
        elif model.PaneRemembered || List.isEmpty (Projection.openTerminals model.Terminals) then
            { model with PaneOpensItself = false }
        else
            landed { model with PaneOpensItself = false; TerminalsOpen = true }

    /// Where focus lands when the pane comes on screen showing what this model shows.
    ///
    /// ONE answer for every way the pane is shown — the reopen control, a chip, a row of the
    /// switcher, a terminal arriving that somebody here pressed for — because each of them is
    /// the same promise: the keyboard follows the reader into the column. A terminal is a thing
    /// you type into, so it lands on its command line; a preview on its panel; nothing on the
    /// press that makes something.
    let paneLanding (model: ClientModel) : DomMove =
        match preview model, selectedTerminal model with
        // The `all` page is the body while it is up, whatever is under it.
        | _ when model.Switcher -> DomMove.FocusSwitcher
        | Some _, _ -> DomMove.FocusPane
        | None, Some terminal -> DomMove.FocusCommandLine terminal
        | None, None -> DomMove.FocusPaneEmpty

    /// Where the refusal notice is drawn, if there is one (`Refusal.mount`). The decision
    /// for BOTH mounts, made once, so the view asks it rather than deciding twice.
    let refusalMount (model: ClientModel) : RefusalMount option =
        model.Refused |> Option.map (Refusal.mount model.TerminalsOpen)

    /// The command the pane's text read is positioned at (Plan 25, stage 3) — what the
    /// browser scrolls into view once the render that put it on screen has happened. Not
    /// under a preview, which covers the history the reveal would scroll.
    let paneAnchor (model: ClientModel) : (TerminalId * BlockId) option =
        model.Pane |> Option.bind PaneMode.showing |> Option.bind TerminalMode.anchor

    /// What this peer has open, as presence names it — what the browser reports to everyone
    /// else. A DERIVATION rather than a field, so what collaborators are told and what is on
    /// this screen cannot drift: there is one answer and the render and the report read it.
    ///
    /// A preview reports what it is OF (`PreviewSubject.view`): a command or a stretch is its
    /// terminal, which is the terminal it is laid over, and a file is the file. The `all` page
    /// changes nothing here: what it is laid over is still the pane's selection, and is what
    /// the reader goes back to.
    let viewing (model: ClientModel) : ViewRef option =
        match model.Pane with
        | None -> None
        | Some (Previewing (preview, _)) -> Some (PreviewSubject.view preview.Subject)
        | Some (OnTerminal _) -> selectedTerminal model |> Option.map ViewingTerminal

    /// Who else has this open right now, by their live presence — never the local peer, who is
    /// not their own audience. Ordered by name, so a row of faces does not reshuffle when a
    /// map's internal order changes.
    ///
    /// Takes a `ViewRef` rather than a tab, so the pane's header, a tab in the strip and a row
    /// in the list all ask the same question of the same value — and a terminal gets this the
    /// day it lands rather than as a second mechanism for the same fact.
    let viewersOf (what: ViewRef) (model: ClientModel) : (ActorRef * string) list =
        model.Presence
        |> Map.toList
        |> List.filter (fun (who, presence) ->
            who <> ActorRef.PeerRef model.Peer.PeerId && presence.Viewing = Some what)
        |> List.map (fun (who, presence) -> who, presence.DisplayName)
        |> List.sortBy (fun (who, name) -> name, ActorRef.token who)

    /// The transcript length this client's rewind pinned, while the terminal is still LIVE
    /// (Plan 14, stage 7). Resolved rather than read raw, for the same reason
    /// `selectedTerminal` is: a pin that outlives its live edge — the terminal closed while somebody sat behind
    /// it — is not a rewind any more, it is simply the recording, and the closed-terminal
    /// replay already shows that in full.
    let rewoundTo (terminal: TerminalId) (model: ClientModel) : int option =
        model.Pane
        |> Option.bind PaneMode.subject
        |> Option.bind (function WatchingBehind (id, pin) -> Some (id, pin) | _ -> None)
        |> Option.filter (fun (id, _) -> id = terminal)
        |> Option.filter (fun _ ->
            Projection.tryFind terminal model.Terminals
            |> Option.map (fun view -> view.IsOpen)
            |> Option.defaultValue false)
        |> Option.map snd

    /// The `.cast` for one range of a terminal's recording — a block's output, or a stretch
    /// of live mode — from the records and the keyframe this client has (Plan 14, stage 3).
    ///
    /// `None` when the header has not arrived: the header is transcript line 0, so a client
    /// that has not read the first chunk cannot say how big the screen is, and a recording
    /// under a guessed geometry rewraps every line in it.
    ///
    /// A MISSING keyframe is not `None`. The range still rebases and still plays; it is then
    /// the naive slice, approximately right for command output and wrong wherever the screen
    /// carried state in. Refusing to play a recording we do have would be the worse answer,
    /// and the surface says which one it is showing.
    let private rangedCastFrom
        (header: TranscriptHeader)
        (feed: TerminalFeed)
        (model: ClientModel)
        (terminal: TerminalId)
        (fromSeq: int)
        (toSeq: int)
        : string =
        TranscriptReplay.range
            header
            (Map.tryFind (terminal, fromSeq) model.TerminalKeyframes)
            fromSeq
            toSeq
            (feed.Records |> Map.toList)

    let rangedCast (terminal: TerminalId) (fromSeq: int) (toSeq: int) (model: ClientModel) : string option =
        let feed = model.TerminalFeeds |> Map.tryFind terminal |> Option.defaultValue TerminalFeed.empty
        feed.Header |> Option.map (fun header -> rangedCastFrom header feed model terminal fromSeq toSeq)

    /// The transcript range a block's recording covers, when it has one.
    ///
    /// A block's range is only a RANGE once it has an end. While it runs, its recording grows
    /// on every record, and a player rebuilt on each one would thrash through a streaming
    /// build; a refused command never ran at all. Both are cases the surface reports rather
    /// than plays.
    ///
    /// One rule, two callers: what a player is handed (`previewReplay`) and whether a player is
    /// OFFERED (`previewPlayable`) are the same question asked at two moments, and a surface that
    /// offered a control the builder then refused would be a button that does nothing.
    let private blockRange (terminal: TerminalId) (blockId: BlockId) (model: ClientModel) : (int * int) option =
        Projection.tryFind terminal model.Terminals
        |> Option.bind (fun view -> view.Blocks |> List.tryFind (fun b -> b.BlockId = blockId))
        |> Option.filter (fun block -> match block.Status with BlockRejected _ -> false | _ -> true)
        |> Option.bind (fun block -> block.ToSeq |> Option.map (fun toSeq -> block.FromSeq, toSeq))

    /// How far past a record's own time a poster asking for THAT record's screen must sit.
    ///
    /// The player paints a poster by replaying events while `time < poster`, so a poster at
    /// a record's exact time stops just short of it and shows the screen as it stood BEFORE
    /// that record — one frame early, in the still whose whole job is to be the last thing
    /// that happened. Smaller than any interval a recording can distinguish, so it can never
    /// reach into the next record.
    let private posterNudge = TranscriptReplay.nudge

    /// How many seconds of playing a rewind leaves between where it lands and the pin, at
    /// least — on the player's clock, so dead air a replay skips does not count towards it.
    let private rewindSpan = 5.0

    /// What a preview's player should be handed (Plan 14, stage 4) — a block's range of its
    /// terminal's recording, or a stretch's. `None` for a file, which is drawn, not played.
    ///
    /// Assembled here rather than in the browser entry because every part of it is a
    /// function of the model, and a value the cheap tier can assert on is worth more than
    /// one only a real player can.
    let previewReplay (subject: PreviewSubject) (model: ClientModel) : PaneReplay option =
        let feed =
            PreviewSubject.terminal subject
            |> Option.bind (fun terminal -> model.TerminalFeeds |> Map.tryFind terminal)
            |> Option.defaultValue TerminalFeed.empty
        /// The recording's own clock at a transcript line — what a marker, a poster and a
        /// start position are all measured in. `None` for a line this client has not read.
        let timeOf (seq: int) = feed.Records |> Map.tryFind seq |> Option.map (fun r -> r.At)
        match feed.Header with
        // The header is transcript line 0; without it the geometry is a guess, and a
        // recording replayed under the wrong one rewraps every line in it.
        | None -> None
        | Some header ->
            match subject with
            // A file is not a recording: there is nothing to play, and the empty feed above
            // means this arm is only ever reached by way of exhaustiveness.
            | PreviewSubject.Content _ -> None
            | PreviewSubject.Block (terminal, blockId) ->
                blockRange terminal blockId model
                |> Option.map (fun (fromSeq, toSeq) ->
                    { Cast = rangedCastFrom header feed model terminal fromSeq toSeq
                      StartAt = None
                      LandedAt = None
                      // The first frame, so a block has a face before anyone presses play: with no
                      // poster the player stays blank until it is asked to play. The range is
                      // rebased to its own zero and starts from its keyframe, so this is the
                      // screen the command began on.
                      Poster = Some posterNudge
                      BehindLive = None })
            | PreviewSubject.Stretch stretch ->
                match stretch.Range with
                // Nothing to replay, and the surface says so rather than mounting a player
                // over an empty recording — which is indistinguishable from a quiet session.
                | None -> None
                | Some (fromSeq, toSeq) ->
                    let origin = timeOf fromSeq |> Option.defaultValue 0.0
                    Some
                        { Cast = rangedCastFrom header feed model stretch.TerminalId fromSeq toSeq
                          StartAt = None
                          LandedAt = None
                          // A still of the FINAL screen, so the item has a face before anyone
                          // presses play. It costs nothing extra: the player builds it by
                          // replaying internally to that time.
                          Poster = timeOf (toSeq - 1) |> Option.map (fun at -> at - origin + posterNudge)
                          BehindLive = None }

    /// What a terminal's player should be handed: the whole recording, chaptered by its
    /// commands — or, rewound, what it had recorded at the pin.
    let terminalReplay (terminal: TerminalId) (model: ClientModel) : PaneReplay option =
        let feed = model.TerminalFeeds |> Map.tryFind terminal |> Option.defaultValue TerminalFeed.empty
        let timeOf (seq: int) = feed.Records |> Map.tryFind seq |> Option.map (fun r -> r.At)
        match feed.Header with
        | None -> None
        | Some header ->
            // A REWOUND live terminal plays what it has recorded so far, up to the
            // length pinned when the rewind began. Everything else about it is the
            // whole-terminal recording, which is the point: rewinding live TV and
            // replaying a finished session are the same mechanism with a moving end.
            let pin = rewoundTo terminal model
            let markers =
                Projection.tryFind terminal model.Terminals
                |> Option.map (fun view ->
                    view.Blocks
                    // A rewind's cast ends at the pin, and a command started after it is not in
                    // it: its chapter would be the cast's last event, past every record, and the
                    // player would stretch the recording out to reach it — dead air at the end
                    // of a rewind, and a mark naming a command the replay never shows.
                    |> List.filter (fun block -> pin |> Option.forall (fun length -> block.FromSeq < length))
                    |> List.choose (fun block ->
                        // A block whose first line this client has not read has no time
                        // to mark, and a marker at a guessed one would point at the
                        // wrong command.
                        timeOf block.FromSeq |> Option.map (fun at -> at, BlockLabel.ofBlock block)))
                |> Option.defaultValue []
            let records =
                match pin with
                | Some length -> feed.Records |> Map.toList |> List.filter (fun (seq, _) -> seq < length)
                | None -> feed.Records |> Map.toList
            // A rewind goes back a little, and plays forward from there until it catches the
            // reader up with the pin. The pin is the END of what this cast holds, so landing on
            // it leaves nothing to play: the player answers a press of play at the end of a
            // recording by starting it over from zero, which put the reader back at the top of
            // an hour-old terminal with nothing to say where they were. Landing at the end was
            // the first design — the still being the live screen they just left, and the scrub
            // bar the way back from there — but a control named for watching a terminal from a
            // moment ago has to be one that plays, and that needs a moment ago.
            //
            // How far back: to the start of the latest command that leaves a few seconds of
            // playing to the pin, so the reader begins on a command rather than mid-screen and
            // the replay is long enough to be one. A terminal with no such command — one that
            // has run none, or whose shell does not mark them — goes back by the same few
            // seconds to the latest record that leaves them, and one too short for either lands
            // on its first record, which is its start.
            //
            // The player is sent there with a seek (`LandedAt`), which puts the screen, the timer
            // and the progress bar at the one position, so pressing play carries on from the
            // frame that was showing rather than clearing it.
            let times = (records |> List.map (fun (_, r) -> r.At)) @ (markers |> List.map fst)
            let landed =
                pin
                |> Option.bind (fun length ->
                    let clock = TranscriptReplay.playerClock times
                    let recorded = records |> List.map (fun (_, r) -> r.At)
                    let roomToPlay at =
                        match List.tryLast recorded with
                        | Some edge -> clock edge - clock at >= rewindSpan
                        | None -> false
                    let startsOfCommands =
                        Projection.tryFind terminal model.Terminals
                        |> Option.map (fun view ->
                            view.Blocks
                            |> List.filter (fun block -> block.FromSeq < length)
                            |> List.sortBy (fun block -> block.FromSeq)
                            |> List.choose (fun block -> timeOf block.FromSeq))
                        |> Option.defaultValue []
                    [ startsOfCommands; recorded ]
                    |> List.tryPick (fun candidates -> candidates |> List.filter roomToPlay |> List.tryLast)
                    |> Option.orElse (List.tryHead recorded))
                |> Option.map (TranscriptReplay.landing times)
            Some
                { Cast = TranscriptReplay.castWithMarkers header records markers
                  LandedAt = landed
                  StartAt =
                    match landed with
                    | Some _ -> None
                    | None ->
                        // A watch entered from one of this terminal's blocks starts at
                        // that command. The block's line is looked up HERE rather than
                        // carried in the mode, so a hint cannot disagree with the blocks
                        // the projection actually has.
                        model.Pane
                        |> Option.bind PaneMode.subject
                        |> Option.bind (function
                            | WatchingFrom (id, blockId) when id = terminal -> Some blockId
                            | _ -> None)
                        |> Option.bind (fun blockId ->
                            Projection.tryFind terminal model.Terminals
                            |> Option.bind (fun view -> view.Blocks |> List.tryFind (fun b -> b.BlockId = blockId)))
                        |> Option.bind (fun block -> timeOf block.FromSeq)
                  Poster = None
                  BehindLive = pin |> Option.map (fun _ -> terminal) }

    /// The player a mount in the pane is keyed to — the hook a mount carries, read back
    /// (`PaneReplays`): the preview on screen by its subject's key, or a terminal in the strip
    /// by its tab's. Nothing else can have a mount, so nothing else is looked for.
    let replayFor (key: string) (model: ClientModel) : PaneReplay option =
        match preview model with
        | Some preview when PreviewSubject.key preview.Subject = key -> previewReplay preview.Subject model
        | Some _ | None ->
            model.Tabs
            |> List.tryFind (fun terminal -> tabKey terminal = key)
            |> Option.bind (fun terminal -> terminalReplay terminal model)

    /// The keyframe the preview on screen needs and this client does not have (Plan 14,
    /// stage 4). A terminal's whole recording starts at the start, and its header is its
    /// keyframe; only a range — a block's, a stretch's — starts somewhere a screen has to be
    /// fetched for.
    ///
    /// Fetched on demand rather than streamed: a keyframe is read by somebody opening a
    /// recording, not by everybody watching one grow, and there is one per block in a
    /// session that may have run thousands.
    let missingKeyframe (model: ClientModel) : (TerminalId * int) option =
        let wanted =
            match preview model |> Option.map (fun preview -> preview.Subject) with
            | Some (PreviewSubject.Block (terminal, blockId)) ->
                Projection.tryFind terminal model.Terminals
                |> Option.bind (fun view -> view.Blocks |> List.tryFind (fun b -> b.BlockId = blockId))
                // A refused command has an empty range and never ran, so there is no screen
                // it started from and nothing to fetch.
                |> Option.filter (fun block -> block.Status <> BlockRunning && (match block.Status with BlockRejected _ -> false | _ -> true))
                |> Option.map (fun block -> terminal, block.FromSeq)
            | Some (PreviewSubject.Stretch stretch) -> stretch.Range |> Option.map (fun (fromSeq, _) -> stretch.TerminalId, fromSeq)
            // A file is not a replay: it has no screen to start from.
            | Some (PreviewSubject.Content _) | None -> None
        wanted |> Option.filter (fun key -> not (Map.containsKey key model.TerminalKeyframes))

    /// Whether this client is watching a terminal behind its live edge (Plan 14, stage 7).
    let isRewound (terminal: TerminalId) (model: ClientModel) : bool =
        rewoundTo terminal model |> Option.isSome

    /// How much recording has accrued past this client's pin, in the recording's own clock
    /// (seconds). `None` when not rewound; `Some 0.0` while nothing new has arrived. What
    /// lets the surface say HOW FAR behind the reader is, which a bare "behind live" cannot.
    let behindLive (terminal: TerminalId) (model: ClientModel) : float option =
        rewoundTo terminal model
        |> Option.map (fun pin ->
            let feed = model.TerminalFeeds |> Map.tryFind terminal |> Option.defaultValue TerminalFeed.empty
            let latestBefore limit =
                feed.Records |> Map.fold (fun acc seq r -> if seq < limit then max acc r.At else acc) 0.0
            max 0.0 (latestBefore System.Int32.MaxValue - latestBefore pin))

    /// The live screen of a terminal, when this client has composed one.
    let terminalScreen (terminal: TerminalId) (model: ClientModel) : LiveScreen option =
        model.TerminalScreens |> Map.tryFind terminal

    /// A terminal's feed, empty when nothing has arrived for it yet.
    let terminalFeed (terminal: TerminalId) (model: ClientModel) : TerminalFeed =
        model.TerminalFeeds |> Map.tryFind terminal |> Option.defaultValue TerminalFeed.empty

    /// What this client knows of a terminal's recording (Plan 20, stage 0) — the one
    /// client-local input `Affordances.ofView` takes.
    ///
    /// Either signal counts as a recording, because they are the same fact reaching this
    /// client two ways: a LIVE terminal's length arrives as a catch-up hint before any chunk
    /// is fetched, and a CLOSED one's records arrive as chunks with no live hint behind them.
    /// Asking only one would offer the rewind on a terminal whose records had not been
    /// fetched, or refuse the replay on a recording sitting in the feed.
    let recordingOf (terminal: TerminalId) (model: ClientModel) : RecordingKnown =
        TerminalFeed.known (terminalFeed terminal model)

    /// What a terminal's row offers this reader.
    let affordances (view: TerminalView) (model: ClientModel) : Affordances =
        Affordances.ofView (recordingOf view.TerminalId model) view

    /// Whether a terminal has a recording to play at all — what decides whether a surface
    /// OFFERS one. Cheap on purpose: a map lookup, no cast built, so a view can ask it on
    /// every render without assembling a recording nobody watches.
    ///
    /// The header is the whole question for a terminal: it is transcript line 0, the whole
    /// recording starts there, and without it the geometry is a guess and a recording
    /// replayed under the wrong one rewraps every line in it.
    let terminalPlayable (terminal: TerminalId) (model: ClientModel) : bool =
        (terminalFeed terminal model).Header |> Option.isSome

    /// Whether a preview's subject has a recording to play at all — `terminalPlayable`'s
    /// question for a range of one.
    let previewPlayable (subject: PreviewSubject) (model: ClientModel) : bool =
        match subject with
        | PreviewSubject.Block (terminal, blockId) ->
            terminalPlayable terminal model && blockRange terminal blockId model |> Option.isSome
        // A stretch with no recorded bounds is a gap in the record, which the surface
        // states rather than playing an empty player over.
        | PreviewSubject.Stretch stretch -> terminalPlayable stretch.TerminalId model && Option.isSome stretch.Range
        // A file is not a recording: the surface offers the file, and nothing to press
        // play on.
        | PreviewSubject.Content _ -> false

    /// Whether the pane shows this terminal as its RECORDING rather than as its text.
    ///
    /// The two reads of one history: what a terminal PRINTED, which the client can render as
    /// text from the same transcript bytes, and how it BEHAVED, which only a player can show.
    /// Showing both at once made the second redundant wherever the first said everything — a
    /// command and its result, with a player of the same two lines beneath — so text is the
    /// read and the recording is a destination you go to.
    ///
    /// Except where there is no text read to go back to, and then the recording is not a
    /// destination, it is the surface. That rule is a fact about the terminal, so it lives
    /// with the terminal (`ReplayIsTheRead`) and this only asks it.
    ///
    /// Every terminal player in the pane is here, the DVR included: rewinding a live terminal
    /// is this same swap with a moving end (`RewindTerminalMsg` asks for the recording like
    /// any other way in), and a second condition beside this one is how two surfaces that
    /// mount the same player start disagreeing about when.
    ///
    /// Not gated on `terminalPlayable`: the mode is what the READER asked for, and a recording
    /// whose header has not arrived yet is a mount that fills in on the render after it does.
    /// The two questions are separate on purpose — the one decides what to OFFER, this decides
    /// what is SHOWN, and a control is only ever offered where the first says yes.
    let terminalPlays (terminal: TerminalId) (model: ClientModel) : bool =
        let chosen =
            model.Pane
            |> Option.bind PaneMode.subject
            |> Option.exists (fun mode -> TerminalMode.watches mode && TerminalMode.terminal mode = terminal)
        chosen
        || (Projection.tryFind terminal model.Terminals
            |> Option.exists (fun view -> (affordances view model).ReplayIsTheRead))

    /// Whether a preview shows its RECORDING rather than its text — `terminalPlays`' question
    /// for a preview.
    let previewPlays (preview: Preview) (model: ClientModel) : bool =
        match preview.Subject with
        // A stretch IS a stretch of recording: somebody held the keyboard, and what they did
        // is bytes rather than commands. There are no blocks to read instead, so it plays
        // wherever there is anything to play — and where there is not, the surface says so in
        // words rather than mounting a player over a gap.
        | PreviewSubject.Stretch _ -> previewPlayable preview.Subject model
        // A block's output is the cheaper read of the same bytes, so a block plays only
        // where its reader said so.
        | PreviewSubject.Block _ -> preview.Plays
        // Nothing to play: a file is drawn, not replayed.
        | PreviewSubject.Content _ -> false

    /// The switcher's terminals, in the order it lists them (Plan 20, stage 0; P2-2): every
    /// terminal in the order it was OPENED, closed ones where they stood — those its filter
    /// shows (`ListFilter`), which is every one until the reader narrows it.
    ///
    /// One order, and it never changes under a row. It used to be two — the open terminals
    /// first, then the closed ones newest first — and that made a kill a reorder: the killed
    /// row dropped to the bottom and the next live terminal's kill slid up under the pointer
    /// that had just pressed one, so a double-click ended two terminals. A list somebody is
    /// pressing in holds still; whether a row is open is said by its mark and its tone, not
    /// by where it went. The open terminals still read in the strip's order, because both are
    /// open order. A filter keeps the promise by deciding its rows rather than reading them
    /// live (`holdFilter`), so a row does not leave it either.
    ///
    /// Ordered by OPEN order rather than by last activity, which the projection cannot
    /// answer: a `TerminalView` carries no clock, and inventing one from block ranges would
    /// make the list's order a function of how much a terminal printed.
    let terminalRows (model: ClientModel) : TerminalView list =
        match model.ListFilter with
        | ListFilter.All -> model.Terminals.Terminals
        | ListFilter.Only (_, shown, _) -> model.Terminals.Terminals |> List.filter (fun view -> Set.contains view.TerminalId shown)

    /// Which filter a terminal answers to: its state (`terminalState`), so the filter a row is
    /// listed under and the mark it wears say one thing.
    let terminalKind (view: TerminalView) (model: ClientModel) : TerminalKind =
        TerminalKind.ofState (terminalState view model)

    /// `kind` decided over the terminals as they stand: the ones that are it now, and every
    /// one judged. Nothing being it is no filter at all — a page narrowed to nothing would be
    /// an empty page with a pressed button over it, saying nothing about what is there.
    let private decideFilter (kind: TerminalKind) (model: ClientModel) : ListFilter =
        let views = model.Terminals.Terminals
        let shown =
            views
            |> List.filter (fun view -> terminalKind view model = kind)
            |> List.map (fun view -> view.TerminalId)
        if List.isEmpty shown then ListFilter.All
        else ListFilter.Only (kind, Set.ofList shown, views |> List.map (fun view -> view.TerminalId) |> Set.ofList)

    /// The filter as the reader chose it: decided now.
    let chooseFilter (kind: TerminalKind option) (model: ClientModel) : ListFilter =
        match kind with
        | None -> ListFilter.All
        | Some kind -> decideFilter kind model

    /// Keep the filter's rows still, after every message (`ListFilter`'s rule): decided again
    /// when the page comes up — `before` did not have it up, `model` does — and otherwise
    /// only ever added to, by a terminal it had not judged that now matches. Nothing a
    /// terminal does takes its row away.
    let holdFilter (before: ClientModel) (model: ClientModel) : ClientModel =
        match model.ListFilter with
        | ListFilter.All -> model
        | ListFilter.Only (kind, _, _) when model.Switcher && not before.Switcher ->
            { model with ListFilter = decideFilter kind model }
        | ListFilter.Only (kind, shown, decided) ->
            let joining =
                model.Terminals.Terminals
                |> List.filter (fun view -> not (Set.contains view.TerminalId decided) && terminalKind view model = kind)
                |> List.map (fun view -> view.TerminalId)
            if List.isEmpty joining then model
            else
                { model with
                    ListFilter = ListFilter.Only (kind, Set.union shown (Set.ofList joining), Set.union decided (Set.ofList joining)) }

    /// The filters the `all` page offers, each with how many terminals are that kind NOW:
    /// every one (`None`) and each kind there is a terminal of — plus the chosen one whatever
    /// its count, because the button that is pressed cannot be the one that goes. Offered
    /// whenever there is a terminal at all, even of one kind, where they narrow nothing: a row
    /// of filters that arrived when a second kind did — a terminal dying, say — would push
    /// every row under it down, which is the move this page exists not to make.
    let listFilters (model: ClientModel) : (TerminalKind option * int) list =
        let views = model.Terminals.Terminals
        let chosen =
            match model.ListFilter with
            | ListFilter.All -> None
            | ListFilter.Only (kind, _, _) -> Some kind
        let counted =
            TerminalKind.order
            |> List.map (fun kind -> kind, views |> List.filter (fun view -> terminalKind view model = kind) |> List.length)
        let offered = counted |> List.filter (fun (kind, count) -> count > 0 || chosen = Some kind)
        if List.isEmpty views then []
        else (None, List.length views) :: (offered |> List.map (fun (kind, count) -> Some kind, count))

    /// The other item in `items` that takes `gone`'s place once it has gone — the next one,
    /// or at the end the one before: `TabStrip.neighbour`'s rule over what is left.
    let private successor (items: TerminalId list) (gone: TerminalId) : TerminalId option =
        let others = items |> List.filter (fun id -> id <> gone)
        items
        |> List.tryFindIndex (fun id -> id = gone)
        |> Option.bind (fun here -> TabStrip.neighbour here (List.length items))
        |> Option.bind (fun index -> List.tryItem index others)

    /// The tab that takes a dismissed one's place: `successor`'s rule, over the strip.
    let dismissLanding (model: ClientModel) (gone: TerminalId) : TerminalId option =
        successor model.Tabs gone

    /// Where focus lands after a kill this client asked for, given the pane as it stood when
    /// the kill was pressed (`model` is the fold's BEFORE).
    ///
    /// Pressed in the SWITCHER (it was open): the next row, asked of the order the reader was
    /// looking at — the switcher holds still through a close (`terminalRows`), so that is also
    /// the order they are looking at after it. With no other row, the killed terminal's own: a
    /// closed terminal keeps its row, as its recording, and that row is where the hand is.
    ///
    /// Pressed on the STRIP: a tab stays, closed, until the reader puts it away
    /// (`ClientModel.Tabs`), so a kill lands on the killed terminal's own tab — the hand is
    /// already there, and that tab's × is now the one that puts it away.
    let killLanding (model: ClientModel) (killed: TerminalId) : DomMove =
        if model.Switcher then
            let rows = terminalRows model |> List.map (fun view -> view.TerminalId)
            successor rows killed |> Option.defaultValue killed |> DomMove.FocusSwitcherRow
        elif List.contains killed model.Tabs then DomMove.FocusTab killed
        else DomMove.FocusPane

    /// Whether a key press is the switcher's shortcut (P2-2): Ctrl+` (⌘` on a Mac — either
    /// modifier, because a page cannot know which keyboard it is under). By the key's
    /// POSITION (`code`), not the character it types, which differs by layout. Spelled for the
    /// reader in `Dom.Text.switchTerminal`.
    let opensSwitcher (code: string) (ctrl: bool) (meta: bool) : bool =
        (ctrl || meta) && code = "Backquote"

    /// What one press on a terminal's kill asks for, wherever the control is (P2-2) — the
    /// strip's × and Delete on a tab, a row's kill in the switcher. `None` where there is
    /// nothing to kill.
    ///
    /// The two-press rule (`KillArmed`) is HERE rather than in each control, so the strip and
    /// the switcher cannot disagree about it: the first press arms, a press on the armed one
    /// kills. A press on a terminal other than the armed one arms THAT one, which disarms the
    /// first — one slot, so at most one terminal is ever a press from gone.
    let killPress (terminal: TerminalId) (model: ClientModel) : ClientMsg option =
        match Projection.tryFind terminal model.Terminals with
        | Some view when (affordances view model).CanKill ->
            if model.KillArmed = Some terminal then Some (CloseTerminalMsg terminal)
            else Some (ArmKillMsg (Some terminal))
        | Some _ | None -> None

    /// Every artifact the session holds, latest version first shared first — what the switcher
    /// offers beneath the terminals, and the only way to reach one whose chip has scrolled
    /// out of the conversation.
    ///
    /// Read straight off the conversation projection (`ConversationProjection.artifacts`), so
    /// the panel and the timeline cannot disagree about what exists: both are the same fold.
    let artifactRows (model: ClientModel) : ArtifactShared list =
        ConversationProjection.artifacts model.Conversation

    /// Everywhere this session can open a terminal, in the order the chooser offers them:
    /// `default` first, because every session has it, then the ones a repo declared and this
    /// session started.
    ///
    /// `SandboxRef` and nothing else, because the only thing a row needs to ACT is where —
    /// what each one is FOR is on the start the view reads beside this. `default` is prepended
    /// rather than folded for the reason `startedSandboxes` leaves it out: nothing started it,
    /// so no event says so, and it is a fact about the session existing.
    let sandboxRows (model: ClientModel) : SandboxRef list =
        SandboxRef.defaultRef
        :: (ConversationProjection.startedSandboxes model.Conversation |> List.map (fun s -> s.Sandbox))

    /// What a repo's file said one of those sandboxes is FOR, when it said anything. From the
    /// start that brought it up, which is the only place it is recorded.
    let sandboxPurpose (sandbox: SandboxRef) (model: ClientModel) : string option =
        ConversationProjection.startedSandboxes model.Conversation
        |> List.tryFind (fun s -> s.Sandbox = sandbox)
        |> Option.bind (fun s -> s.Description)

    /// A terminal's queued commands in run order.
    let terminalQueue (terminal: TerminalId) (model: ClientModel) : PendingAct list =
        TerminalQueueOrder.sortedFor terminal model.Synced.Pending

    /// Every act waiting on a verdict, in a stable total order (Plan 15, stage 3c): by
    /// subject, then by the subject's own order, then by id. What the CHAT shows, at the tail
    /// of the conversation, and it deliberately includes the TERMINAL ones — reading a command
    /// the agent is about to run is the same act as reading what it is about to say, so it
    /// belongs where the reading happens rather than only inside a panel you may not have open.
    let pendingActs (model: ClientModel) : PendingAct list =
        model.Synced.Pending
        |> Map.toList
        |> List.map snd
        |> List.sortBy (fun act -> TerminalId.value act.Terminal, act.Order, QueueId.value act.QueueId)

    /// The composer slots published in a terminal, in stable order — every peer mid-command
    /// there, the local peer included.
    let terminalDrafts (terminal: TerminalId) (model: ClientModel) : PeerId list =
        model.Synced.TerminalDrafts
        |> Map.toList
        |> List.filter (fun ((t, _), _) -> t = terminal)
        |> List.map (fun ((_, author), _) -> author)
        |> List.sortBy PeerId.value

    /// Whether a queued command is held because a peer is typing in its terminal (Plan 13,
    /// stage 2e). Named because it resolves when a person finishes a task, and a queue that
    /// said only *pending* would leave that looking like a stall.
    let awaitsTerminal (entry: PendingAct) (model: ClientModel) : bool =
        Projection.tryFind entry.Terminal model.Terminals
        |> Option.bind (fun view -> view.Lease)
        |> Option.isSome

    /// Whether a queued command is held because its terminal's shell stopped emitting marks
    /// (Plan 13, stage 2f) rather than because a peer is typing there. Apart again for the
    /// same reason: they resolve differently — one when a person finishes, this one when
    /// somebody repairs the terminal.
    let awaitsIntegration (entry: PendingAct) (model: ClientModel) : bool =
        Projection.tryFind entry.Terminal model.Terminals
        |> Option.map (fun view -> view.IntegrationLost)
        |> Option.defaultValue false

    /// Who is editing a terminal composer right now, by their live caret.
    let terminalEditorsOf (terminal: TerminalId) (author: PeerId) (model: ClientModel) : (ActorRef * string) list =
        model.Presence
        |> Map.toList
        |> List.filter (fun (_, presence) ->
            presence.Focus |> Option.exists (fun f -> f.Field = TerminalDraftBody (terminal, author)))
        |> List.map (fun (editor, presence) -> editor, presence.DisplayName)

    // --- Where everyone is ------------------------------------------------------------------
    // Presence already drove the per-field overlays (a caret in a body, a dot on a draft), but
    // each of those is only visible from INSIDE the surface it is about — so a collaborator
    // typing a command in a terminal you are not looking at, or renaming the session while you
    // read the timeline, was invisible. These answer "where is everyone" from the model, and
    // the roster and the terminal strip render it.
    //
    // WHERE someone is comes from presence, and holds exactly while their caret is in a
    // collaborative field: presence clears when the caret leaves (`Focus = None`) and when the
    // peer goes. WHETHER someone is here is a different question with a different source —
    // `Here`, the log's open connections — because a person reading the timeline has no caret
    // anywhere and is still in the room. The roster (`roster`, below `userName`) asks both.
    // Neither is `Peers`, which deliberately keeps the departed so a draft's author still has
    // a name, and would report people who left days ago as being in the room.

    /// Every connected credential that needs a person to sign in again, as
    /// `(provider, reason)` — Claude's before GitHub's, and each panel's shared scope before
    /// its session-only one, so the order on screen never depends on a map's iteration.
    ///
    /// A DERIVATION rather than a field, so the surfaces that report this — the panel rows,
    /// the roster, the prompt over the timeline — cannot disagree about whether anything is
    /// wrong. The view stays a total function of the model, and the cheap tier can ask this
    /// question without rendering anything.
    let signInRequired (model: ClientModel) : (string * string) list =
        let needing (provider: string) (credential: CredentialRow option) =
            match credential with
            | Some view -> view.SignInRequired |> Option.map (fun reason -> provider, reason)
            | None -> None
        // A panel nobody has been told about owes nothing: a prompt to sign in again is a
        // statement about a credential, and there is no credential to speak of yet.
        let rows (panel: 'panel option) (mine: 'panel -> CredentialRow option) (session: 'panel -> CredentialRow option) =
            match panel with
            | Some panel -> [ mine panel; session panel ]
            | None -> []
        [ yield! rows model.Claude.Status (fun p -> p.MineCredential) (fun p -> p.SessionCredential) |> List.map (needing "claude")
          yield! rows model.GitHub.Status (fun p -> p.MineCredential) (fun p -> p.SessionCredential) |> List.map (needing "github") ]
        |> List.choose id

    /// Everyone whose caret is somewhere right now, except you. Actors rather than peers,
    /// because the Session is one of them.
    let presentEditors (model: ClientModel) : (ActorRef * string * FocusField) list =
        model.Presence
        |> Map.toList
        |> List.filter (fun (who, _) -> who <> ActorRef.PeerRef model.Peer.PeerId)
        // A peer present only because a pane is open is not EDITING anything, so it is absent
        // from this list rather than carried with a made-up field. Where it is showing is
        // `viewersOf`, which the pane renders.
        |> List.choose (fun (who, presence) ->
            presence.Focus |> Option.map (fun focus -> who, presence.DisplayName, focus.Field))
        |> List.sortBy (fun (who, name, _) -> name, ActorRef.token who)

    /// The terminal a focus is in, when it is in one. A composer slot names its terminal
    /// directly; a queued command names only its entry, and the entry names the terminal —
    /// so this is the one place that join lives.
    let terminalOfFocus (field: FocusField) (model: ClientModel) : TerminalId option =
        match field with
        | TerminalDraftBody (terminal, _) -> Some terminal
        | TerminalQueuedBody queueId ->
            model.Synced.Pending |> Map.tryFind queueId |> Option.map (fun act -> act.Terminal)
        | Title | DraftBody _ | QueueBody _ | ChapterName _ -> None

    /// Who is in a given terminal right now — whether writing a new command or editing a
    /// queued one, because from the strip they are the same fact: someone is in there.
    let editorsInTerminal (terminal: TerminalId) (model: ClientModel) : (ActorRef * string) list =
        presentEditors model
        |> List.filter (fun (_, _, field) -> terminalOfFocus field model = Some terminal)
        |> List.map (fun (who, name, _) -> who, name)

    /// Which actor THIS client is, by the same rule the Session used to stamp what
    /// this client asked for: `Attribution.actorFor` — the durable user when this peer's join
    /// was attributed, the peer connection itself when it was not.
    ///
    /// Asking the rule rather than building `ActorRef.PeerRef model.Peer.PeerId` and comparing.
    /// The two agree only under `--auth localhost`, which verifies nobody; under a
    /// Manager-verified deployment every command this connection sends is written `UserRef`,
    /// so a client that assumed `PeerRef` matched NOTHING it had done. It cost a strip that
    /// never held a terminal you opened (`+ new` opened one and showed you nothing, so people
    /// pressed it again — 24 empty terminals in one session), a lease bar that named its holder
    /// "somebody else" to the holder, a live screen that refused the holder's keystrokes, and a
    /// pty that never heard the holder's viewport size. One wrong answer, four surfaces.
    ///
    /// This is for a DURABLE actor — one the log carries. Presence is not: an awareness frame
    /// is keyed by the peer that sent it and never attributed, so the handful of comparisons
    /// against `model.Presence` are right to build a `PeerRef`, and are deliberately left alone.
    let me (model: ClientModel) : ActorRef =
        Attribution.actorFor model.Attribution.PeerUsers model.Peer.PeerId

    /// Whether this peer holds the lease on an open terminal: the one question both the
    /// keyboard's landing and the pty's size ask of a lease.
    let private holds (model: ClientModel) (terminal: TerminalId) : bool =
        Projection.tryFind terminal model.Terminals
        |> Option.exists (fun view -> view.IsOpen && view.Lease = Some (me model))

    /// Where the keyboard goes when the terminal on screen changes what it offers one — `None`
    /// when it does not. `before` and `after` are a message's fold, either side of it.
    ///
    /// The terminal offers this peer its command line, its live screen while this peer holds
    /// the lease, or neither — somebody else holds it, or the shell has gone. A change between
    /// those swaps the control under the hand for another: Hand it back is removed by the
    /// release it asked for, a lease taken by somebody else replaces the command line with
    /// their bar, a shell that dies replaces both with its closed band. Focus on the control
    /// that went falls to `body`, and the floor this shell holds itself to is that a swap of
    /// the focused element refocuses its replacement. So it lands where the pane lands now
    /// (`paneLanding`: the command line, which falls back to the panel — or, where a dead
    /// terminal took the default with it, the terminal the pane has moved to), and only if it
    /// was dropped (`IfDropped`): an arrival does not know where the hand is.
    ///
    /// Only for NEWS (`HeardThrough`): a log being replayed on load changes a terminal's face
    /// under nobody's hand, and focus resting on `body` then is a page that has just loaded,
    /// not a control that went.
    ///
    /// Not the edge INTO this peer's own live screen, which is `leaseLanding`'s. Not under a
    /// preview or the switcher, which cover the terminal; and not for a kill pressed here,
    /// whose answer `killLanding` places.
    let keyboardSwap (before: ClientModel) (after: ClientModel) : DomMove option =
        let offers (model: ClientModel) (terminal: TerminalId) =
            Projection.tryFind terminal model.Terminals |> Option.map (fun view -> view.IsOpen, view.Lease)
        let covered (model: ClientModel) = Option.isSome (preview model) || model.Switcher
        match selectedTerminal before with
        | Some shown when before.HeardThrough && not (covered before) && not (covered after) ->
            let live = selectedTerminal after = Some shown && offers after shown = Some (true, Some (me after))
            let killed = before.KillPending = Some shown && after.KillPending = None
            if offers before shown <> offers after shown && not live && not killed then
                Some (DomMove.IfDropped (paneLanding after))
            else None
        | Some _ | None -> None

    /// Whether the strip owes its selected tab a reveal after a message — a scroll box keeps
    /// nothing in view by itself, and the newest tab, the one just selected, opens past the
    /// right-hand edge.
    ///
    /// Only on a CHANGE of what it would show: a reveal on every message would take the strip
    /// back from a reader scrolling it to look at the other tabs the moment anything at all
    /// arrived, while a change of selection is the reader's own act (or a collaborator's
    /// `TabOpened`) being answered. The selected tab is what the pivot marks: the terminal the
    /// pane is about, and none while `all` is up, which is not in the strip. A tab that only
    /// GREW — its × armed (P2-2), a collaborator's mark beside its name — is not a change: the
    /// confirm is only a confirm if the spot that was pressed is still the control, and a
    /// strip that scrolled the grown tab whole into view slid it out from under the second
    /// press.
    ///
    /// And on the two moments a selection that has not changed is first drawn where it can be
    /// measured: the column opening (a shut strip has no width, so a selection made behind it
    /// could not be revealed), and the log read through (`HeardThrough`), before which renders
    /// are held while a reload replays it, so a remembered selection may not have been drawn by
    /// the frame its own reveal ran in.
    let pivotReveal (before: ClientModel) (after: ClientModel) : DomMove option =
        let shown (model: ClientModel) = if model.Switcher then None else selectedTerminal model
        let opened = after.TerminalsOpen && not before.TerminalsOpen
        let caughtUp = after.HeardThrough && not before.HeardThrough
        match shown after with
        | Some _ when shown before <> shown after || opened || caughtUp -> Some DomMove.RevealPivot
        | Some _ | None -> None

    /// Where the keyboard goes when this peer has just become the one typing into the terminal
    /// on screen — onto its live screen. Taking the keyboard is the whole of what live mode IS,
    /// and both ways into it leave focus where the keys no longer belong: pressing `take`
    /// removes the `take` button in the render the lease arrives on, and the alt-screen flip
    /// hands a block's author the terminal while their caret is still in its command line.
    ///
    /// One edge for both routes, because the flip has no press to hang it on: it is the
    /// Session saying the mode changed, which reaches this client as a fold like any other.
    /// Only the terminal the pane is SHOWING — a lease landing on one the reader is not looking
    /// at has no screen in the document to focus — and only an edge: a lease already held is
    /// not news, and moving to a terminal held all along is the reader's own move.
    let leaseLanding (before: ClientModel) (after: ClientModel) : DomMove option =
        match selectedTerminal after with
        | Some shown when holds after shown && not (holds before shown) ->
            Some (DomMove.FocusTerminalScreen shown)
        | Some _ | None -> None

    /// The ptys this peer has to tell the Session the size of, after a message — each terminal
    /// it holds the lease on whose viewport that message moved, or whose lease that message
    /// brought while its viewport was already known.
    ///
    /// Only the holder: the pty has one size while a program is drawing on it, and every peer
    /// is watching the same screen, so a viewer with a narrower pane scrolls rather than
    /// reshaping everyone else's terminal. (Every peer still MEASURES — the width a queued
    /// command claims is a block-mode fact, `PendingAct.Size` — and that measurement is what
    /// this reads.)
    ///
    /// Only a CHANGE, read off `TerminalViewports` either side of the message: a resize is a
    /// signal to the program on the other end, and repeating one makes a full-screen program
    /// redraw for no reason. A measurement the reducer refused leaves the map as it was, so it
    /// asks for nothing.
    ///
    /// And on the lease ARRIVING, because the pty it arrives on is whatever size the last
    /// block or the last holder left it at, and a holder whose box has not moved since would
    /// otherwise type into a screen laid out for somebody else's until they next dragged a
    /// splitter. Every terminal held, not only the one on screen — a measurement kept for a
    /// terminal the pane has moved off is still the truest width this reader has for it.
    ///
    /// A lease replayed from history asks too; that is safe rather than right by luck, because
    /// the Session resizes a pty only for the peer holding its lease NOW (`SessionTerminals`'
    /// `Resize`), and refuses the rest.
    let ptyResizes (before: ClientModel) (after: ClientModel) : (TerminalId * Size) list =
        after.TerminalViewports
        |> Map.toList
        |> List.filter (fun (terminal, size) ->
            holds after terminal
            && (not (holds before terminal) || Map.tryFind terminal before.TerminalViewports <> Some size))

    /// What this peer tells everyone else about where it is, read off the model either side of
    /// a message: its caret (`Caret`) and what it has open (`viewing`), or `None` when there is
    /// nothing to say.
    ///
    /// Presence is ONE frame with two halves, learned in different places at different moments
    /// — a caret from an editor event, a view from whatever moved the pane — and both always
    /// leave together, so restating either never erases the other: a peer that stops typing
    /// while still reading an artifact must not vanish from the pane it has open, and a peer
    /// that closes the pane must not lose its caret.
    ///
    /// Only a CHANGE: the model moves on every keystroke, and a pane that has not moved tells a
    /// collaborator nothing. Presence is relayed last-write-wins with no keepalive, so a repeat
    /// buys nothing.
    ///
    /// Nothing while the connection is anything but `Connected`, because the first frame on a
    /// channel has to be the hello (`Connection.run`): the Session reads an unannounced peer's
    /// frame as a peer it does not know. What changed meanwhile is not dropped — it is in the
    /// model — and is RESTATED on acceptance, as is whatever was told to a channel that has
    /// since gone (a reconnect passes through `Reconnecting`): a peer that opened a pane while
    /// the channel was still coming up is viewing it just as much as one who opened it after,
    /// and presence has no keepalive, so a frame nobody sent is a peer nobody sees. An
    /// acceptance with nothing to restate sends nothing: a peer that left was cleared
    /// everywhere when it went.
    let presenceToSend (before: ClientModel) (after: ClientModel) : (Focus option * ViewRef option) option =
        let caret, view = after.Caret, viewing after
        match before.Connection, after.Connection with
        | Connected, Connected when before.Caret <> caret || viewing before <> view -> Some (caret, view)
        | Connected, _ -> None
        | _, Connected when caret.IsSome || view.IsSome -> Some (caret, view)
        | _ -> None

    /// What this browser writes down of the pane after a message (P0-4), or `None` when the
    /// pane it would write is the one already kept. Read off the model either side of the
    /// message rather than by the messages that move it: the four fields `paneMemory` reads
    /// have a dozen writers between them, and a write per writer is a writer somebody adds
    /// without one.
    ///
    /// Only a CHANGE from what storage already holds, which the model knows without asking:
    /// the memory it was HELD (`remembered`) up to the message that lets go of it, the pane as
    /// it stood before the message once it has, and an untouched pane when this session's
    /// arrival found nothing kept — so a session merely visited leaves no key behind until
    /// something in its pane is actually moved.
    ///
    /// Never while a memory is still held: the strip then holds only the terminals the log has
    /// named so far, and writing it would forget the rest of what is waiting to be put back.
    let paneToRemember (before: ClientModel) (after: ClientModel) : (SessionId * PaneMemory) option =
        match after.Session, after.PaneMemory with
        | Some session, None ->
            let kept =
                match before.PaneMemory with
                | Some held -> held
                | None when before.Session = after.Session -> paneMemory before
                | None -> PaneMemory.untouched
            let memory = paneMemory after
            if memory <> kept then Some (session, memory) else None
        | _ -> None

    /// Whether a durable actor is this client. The question every ownership rule here asks —
    /// is this terminal mine, is this lease mine — with `me` as its one answer.
    let isMine (actor: ActorRef) (model: ClientModel) : bool =
        actor = me model

    /// A peer's display name: your own connection's, else the roster's, else the peer's own
    /// live presence, else the raw id (an id is a last resort, not a label — `PEER-129755065`
    /// is not a person).
    ///
    /// YOUR OWN name comes first and from your own connection, because those two can disagree
    /// and only one of them is what the rest of the screen is showing. The roster is folded
    /// from the durable `PeerJoined` log, while `Peer.DisplayName` is what THIS connection was
    /// assigned — so a peer that rejoins under a new name has an old one still in the log, and
    /// a client reading the roster for itself would put a name in the chat that the sidebar's
    /// "you" row contradicts. Which is the exact defect resolving names was meant to end.
    let nameOf (peer: PeerId) (model: ClientModel) : string =
        if peer = model.Peer.PeerId && model.Peer.DisplayName <> "" then model.Peer.DisplayName
        else
            match Map.tryFind peer model.Peers with
            | Some name -> name
            | None ->
                match Map.tryFind (ActorRef.PeerRef peer) model.Presence with
                | Some presence when presence.DisplayName <> "" -> presence.DisplayName
                | _ -> PeerId.value peer

    /// Everybody here a message can address by name: each peer by the name it is seen under,
    /// those connected now as well as those the log remembers, and never this client itself —
    /// nobody addresses themselves. The agent is the picker's to add (`Addressed.offer`).
    let addressable (model: ClientModel) : string list =
        let here =
            model.Presence
            |> Map.toList
            |> List.choose (fun (who, presence) ->
                match who with
                | ActorRef.PeerRef _ when presence.DisplayName <> "" -> Some presence.DisplayName
                | _ -> None)
        (Map.toList model.Peers |> List.filter (fun (peer, _) -> peer <> model.Peer.PeerId) |> List.map snd) @ here
        |> List.filter (fun name -> name <> model.Peer.DisplayName)
        |> List.distinct

    /// A `UserRef` author's real name, resolved through the SAME rule the Session
    /// used to decide the author was a `UserRef` in the first place
    /// (`Yession.Domain.Attribution`) rather than a client-side guess that could disagree
    /// with it: ask `Attribution.UserPeers` for the peer THIS user most recently joined
    /// as, and ask `nameOf` for that peer's name. A user attributed across several joins
    /// (a reconnect, or simply having been in this session before under an older name)
    /// has several entries in `Attribution.PeerUsers`, and reverse-scanning that map for
    /// a match would pick whichever one happened to sort first — stale or current, no way
    /// to tell them apart. `Attribution.UserPeers` is folded in event order and
    /// overwritten on each join, so it always names the CURRENT one. Falls back to the
    /// raw subject only when this client has never seen the user's peer join at all — an
    /// id is a last resort here exactly as it is in `nameOf`.
    let userName (user: UserId) (model: ClientModel) : string =
        model.Attribution.UserPeers
        |> Map.tryFind user
        |> Option.map (fun peer -> nameOf peer model)
        |> Option.defaultValue (UserId.value user)

    /// Everybody in the room but you — one row a PERSON — and where each is, when they are
    /// somewhere. What the sidebar's roster renders, on the desk and in the phone's drawer.
    ///
    /// Who is here is everyone with a connection open (`Here`) and everyone whose presence
    /// says they are somewhere: the second is live and the log is caught up over the feed, so a
    /// peer can be placed a moment before their join is folded, and should not be missing for
    /// that moment. A person is who `Attribution` says a peer is — so two tabs (one peer) and
    /// two devices of one verified user (two peers, one user) are one row, and a device of
    /// YOURS is you, not a guest. Where they are is the first caret any of their peers has,
    /// which is `None` for somebody simply here — the roster says nothing about where, rather
    /// than inventing somewhere.
    ///
    /// The `ActorRef` beside the field is the PEER whose caret it is: a draft is written by a
    /// peer, so "their own message" is a question about that peer, not about the person.
    ///
    /// Actors that are not peers (the Session, naming things) are here only while they are
    /// editing — they never join, so presence is the only thing that says they are around.
    /// Ordered by name so the roster does not reshuffle when a map's internal order changes.
    let roster (model: ClientModel) : (ActorRef * string * (ActorRef * FocusField) option) list =
        let mine = me model
        let personOf (peer: PeerId) = Attribution.actorFor model.Attribution.PeerUsers peer
        let caretOf (who: ActorRef) =
            Map.tryFind who model.Presence
            |> Option.bind (fun presence -> presence.Focus)
            |> Option.map (fun focus -> who, focus.Field)
        let presentPeers =
            model.Presence
            |> Map.toList
            |> List.choose (fun (who, _) ->
                match who with
                | ActorRef.PeerRef peer -> Some peer
                | _ -> None)
        let people =
            Here.peers model.Here @ presentPeers
            |> List.distinct
            |> List.filter (fun peer -> peer <> model.Peer.PeerId)
            |> List.groupBy personOf
            |> List.filter (fun (person, _) -> person <> mine)
            |> List.map (fun (person, peers) ->
                let name =
                    match person with
                    | ActorRef.UserRef user -> userName user model
                    // A group is never empty: `groupBy` makes one only for a peer it was given.
                    | _ -> nameOf (List.head peers) model
                let at = peers |> List.sort |> List.tryPick (fun peer -> caretOf (ActorRef.PeerRef peer))
                person, name, at)
        let others =
            model.Presence
            |> Map.toList
            |> List.choose (fun (who, presence) ->
                match who with
                | ActorRef.PeerRef _ -> None
                | _ -> presence.Focus |> Option.map (fun focus -> who, presence.DisplayName, Some (who, focus.Field)))
        people @ others |> List.sortBy (fun (who, name, _) -> name, ActorRef.token who)

    /// What the chapter at this item is called, on a surface.
    ///
    /// The name itself is the session's (`Chapters.name`: what somebody wrote, or the
    /// heuristic until they do). What is added here is the floor under it — a chapter can sit
    /// on a message that has said nothing yet, and a control named by an empty string is a
    /// control a screen reader announces as "button".
    let chapterName (model: ClientModel) (item: ConversationItem) : string =
        match Chapters.name CollabText.ylmish AutoChapters.policy model.Synced.Chapters item with
        | "" -> Dom.Text.unnamedChapter
        | said -> said

    /// What a chapter's DIVIDER in the timeline says: the name somebody chose, and nothing
    /// for a chapter still wearing the guess.
    ///
    /// The guess is the opening message's own words (`Chapters.defaultName`), and the divider
    /// stands directly above that message — so printing it there says the same sentence twice,
    /// one line apart. A name only earns the divider once it says something the message under
    /// it does not: a person's, or the model's. The list of chapters keeps `chapterName`,
    /// guess and all, because there a name is how one entry is told from the next and no
    /// message is beside it.
    let chapterRuleName (model: ClientModel) (item: ConversationItem) : string =
        if Chapters.unwritten CollabText.ylmish AutoChapters.policy model.Synced.Chapters item then ""
        else chapterName model item

    /// What the chapter at this message is called, for a surface that has an id and not the
    /// item — presence, which reports a `MessageId` because that is what identifies a chapter
    /// on the wire.
    ///
    /// `None` when this client has not folded that message yet. A peer's caret can arrive
    /// before the message it is in: presence is relayed live and the conversation is caught
    /// up over the event feed, so the two legs are not in step. Saying "a chapter" then is
    /// honest; inventing a name for one nobody here has seen is not.
    let chapterNameAt (messageId: MessageId) (model: ClientModel) : string option =
        model.Conversation.Items
        |> List.tryFind (fun item -> item.MessageId = messageId)
        |> Option.map (fun item -> chapterName model item)

    /// Every chapter in the session, oldest first — the conversation's own order, which is
    /// the order the contents lists them in and the order a reader walking the session would
    /// meet them.
    ///
    /// No position here, and that is the whole lesson of the rail this replaced: where a
    /// chapter IS on a screen is a measurement of a laid-out page, and the list is the same
    /// on a phone and a desk.
    let chapters (model: ClientModel) : ConversationItem list =
        Chapters.over AutoChapters.policy model.Synced.Chapters model.Conversation.Items

    /// Every message a chapter opens at, for a surface that draws the transcript and asks of
    /// each row in turn. Asked once per render rather than once per row, because the policy
    /// reads what came before an item (`Chapters.openings`).
    let chapterOpenings (model: ClientModel) : Set<MessageId> =
        Chapters.openings AutoChapters.policy model.Synced.Chapters model.Conversation.Items

    /// What this session's pull-request watches currently stand at, read off the
    /// `pull_requests` query — the only shape a browser has them in, since the query stream
    /// is what delivers them.
    ///
    /// Here rather than at either surface because there are now two: the header strip and
    /// the tab title. Two readers of one set of rows that parsed them separately would be
    /// two readers that can disagree about the same session in the same window — which is
    /// the fault `PrStatus` exists to prevent, one layer up from where it prevents it.
    ///
    /// Every standing, live or not. The callers want different halves — a summary counts
    /// only what is still owed, the tab title's tick is specifically about one that is NOT
    /// owed any more — and `PrStatus.live` is how each says which.
    let prStandings (model: ClientModel) : (string * string) list =
        let cell (row: (string * QueryCell) list) (key: string) =
            row |> List.tryFind (fun (k, _) -> k = key) |> Option.map snd
        let text row key =
            match cell row key with
            | Some (CellStatus (said, _)) -> Some said
            | Some (CellText said) -> Some said
            | _ -> None
        // The TONE and never the sentence: a health line's words are the provider's and
        // change with it, while the tone is this repository's own verdict vocabulary.
        let readable row =
            match cell row PrStatus.Columns.status with
            | Some (CellStatus (_, ToneBad)) -> false
            | _ -> true
        match model.Queries.Values |> Map.tryFind PrStatus.Columns.query with
        | Some (RowsOf rows) ->
            rows
            |> List.choose (fun row ->
                match text row PrStatus.Columns.pr with
                | Some named ->
                    PrStatus.standing (PrStatus.labelOf named) (text row PrStatus.Columns.state) (readable row)
                | None -> None)
        | _ -> []

    /// What a pull-request watch puts in front of the tab name, for somebody who is not
    /// looking at this tab at all — the only surface that reaches them there.
    ///
    /// Two signals and deliberately not three. A red suite is the AGENT's to fix and it is
    /// already fixing it, so interrupting a person with one trains them to ignore the mark
    /// by the time it means something. What reaches them is the two facts nobody else is
    /// acting on:
    ///
    /// - `⚠` — a live watch stalled, or one nobody can read any more. Both mean the same
    ///   thing to the person waiting: this pull request has stopped being on its way in and
    ///   no machine is going to notice.
    /// - `✓` — something merged and the watch is still there. Stop waiting; the watch going
    ///   away is what clears it.
    ///
    /// The warning wins, because a tab can only say one thing and the one that needs a
    /// person is the one that has stopped moving.
    let private tabSignal (model: ClientModel) : string =
        let standings = prStandings model
        // No `live` filter on these two: a watch that stalled or cannot be read is by
        // construction still owed, so asking would be asking a question with one answer.
        let stopped =
            standings |> List.exists (fun (_, word) -> word = "stalled" || word = PrStatus.unreachable)
        if stopped then "⚠ "
        elif standings |> List.exists (fun (_, word) -> word = "merged") then "✓ "
        else ""

    /// What the browser tab is called: the session's own title, falling back to its id, and
    /// always saying which product it belongs to. Every session shell served the constant
    /// "Yession", so a person with three of them open had three identical tabs and no way to
    /// tell which was which without visiting each.
    ///
    /// A pure projection rather than something the composition root assembles, because every
    /// part of it is a decision — what wins, what a blank title falls back to, how the two
    /// are joined — and a decision inside `setState` is one no cheap test can reach. The
    /// browser only applies the answer.
    ///
    /// The id fallback is the honest one here, unlike `nameOf` where an id is a last resort:
    /// this is the tab for a session, and its id is what the header shows beside the title
    /// until somebody names it.
    let tabTitle (model: ClientModel) : string =
        let named = (Ylmish.Text.toString model.Synced.Title).Trim ()
        let subject =
            if named <> "" then Some named
            else model.Session |> Option.map SessionId.value
        let signal = tabSignal model
        match subject with
        | Some subject -> sprintf "%s%s — yession" signal subject
        | None -> signal + "yession"

    /// How long the agent's writing has to sit still before it reads as thinking again. Long
    /// enough to ride over the gap between two deltas of one sentence, short enough that a
    /// pause to reason — or to reach for a tool — shows as one within a breath.
    let writingQuietMs = 700

    /// How long catch-up must run before it is worth SAYING (`EventConsumerState.CatchUpIsSlow`).
    /// Long enough that a send — which puts this client one event behind itself for a round
    /// trip — never lights it; short enough that a real wait is reported rather than sat
    /// through in silence.
    let catchUpQuietMs = 500

    /// How long a copy says so for. Long enough to be read as an answer to the press, short
    /// enough that the code it stands in front of comes back before anybody needs it again.
    let copiedShownMs = 1500

    /// How long an armed destructive press — a queue delete, a terminal's kill — stays armed
    /// waiting for the confirming one. Shared, because the two are one gesture and a reader
    /// who learns its rhythm on one should not find a different clock on the other. Long enough
    /// that the second press is the same gesture as the first — a deliberate double-tap,
    /// not a race against a clock — short enough that a row left alone settles back to
    /// "editable", not "one press from gone", by the time anyone returns to it.
    let armedMs = 2000

    /// A message's stamp, while it is one the agent is still writing and has said something in.
    /// Nothing is a stamp before the first word: an empty body already reads as thinking, and
    /// has no quiet to wait for.
    let writingStamp (item: ConversationItem) : WritingStamp option =
        match item.Status with
        | Streaming when not (System.String.IsNullOrWhiteSpace (ConversationItem.said item)) ->
            Some { WritingStamp.Message = item.MessageId; WritingStamp.Length = (ConversationItem.said item).Length }
        | Streaming | Complete | ConversationItemStatus.Running | ConversationItemStatus.Failed -> None

    /// Whether a message the agent is writing reads as THINKING rather than writing: nothing
    /// said yet, or what has been said has sat still since the quiet timer last fired at
    /// exactly this stamp. A word arriving moves the stamp, so it reads as writing again with
    /// nothing dispatched, and a timer armed before that word fires a stamp that no longer
    /// matches, so it cannot make the new words read as a pause.
    let agentThinking (model: ClientModel) (item: ConversationItem) : bool =
        match writingStamp item with
        | None -> true
        | Some stamp -> model.Agent.Quiet = Some stamp

    /// Every wait this model wants running (`Timer`). The program keeps exactly these alive,
    /// keyed, so a wait is started by appearing here and stopped by leaving.
    ///
    /// Catch-up is the normal state for a moment after anything happens — a send puts this
    /// client behind its own event until the page comes back — so it is reported only once
    /// it has lasted `catchUpQuietMs`. Without the wait the header flickered "up to date" →
    /// "catching up" → "up to date" on every message sent. One key for the whole episode:
    /// the pages that arrive while it runs leave the wait alone rather than pushing it out,
    /// which would mean a long catch-up was never reported; the episode ending takes it away.
    ///
    /// A copy's confirmation is an expiry, keyed by the copy itself: a copy of another box, or
    /// the same box again, is a new key and a fresh wait, so no earlier deadline can take a
    /// later confirmation off the screen.
    ///
    /// A panel's wait on the query is a deadline, keyed by the wait itself (the panel, and
    /// when its command was accepted): a wait that ends — the query showed it, or a new
    /// command replaced it — takes its deadline with it. What it fires is the moment the
    /// deadline falls due, `since` plus `Pending.deadlineMillis`, which is exactly when a
    /// timer started with the wait and running that long fires.
    ///
    /// The agent's quiet is a debounce: one timer per message it is writing, keyed by the
    /// stamp, so each delta replaces the wait with a fresh one and only a body that stops
    /// growing for `writingQuietMs` ever fires. None once it has fired for this stamp — the
    /// fact is recorded, and a wait for it again would be asking a settled question.
    let timers (model: ClientModel) : Timer<ClientMsg> list =
        let catchUp =
            if model.EventConsumer.IsCatchingUp && not model.EventConsumer.CatchUpIsSlow then
                [ { Key = [ "catch-up-slow" ]; After = catchUpQuietMs; Fire = CatchUpSlowMsg true } ]
            else []
        let quiet =
            model.Conversation.Items
            |> List.choose writingStamp
            |> List.filter (fun stamp -> model.Agent.Quiet <> Some stamp)
            |> List.map (fun stamp ->
                { Key = [ "agent-quiet"; MessageId.value stamp.Message; string stamp.Length ]
                  After = writingQuietMs
                  Fire = AgentQuietMsg stamp })
        let copied =
            match model.Copied with
            | Some copy ->
                [ { Key = [ "copied"; copy.Box; string copy.Nth ]; After = copiedShownMs; Fire = CopiedMsg None } ]
            | None -> []
        let queueDeleteArmed =
            match model.QueueDeleteArmed with
            | Some queueId ->
                [ { Key = [ "queue-delete-armed"; QueueId.value queueId ]
                    After = armedMs
                    Fire = ArmQueueDeleteMsg None } ]
            | None -> []
        let killArmed =
            match model.KillArmed with
            | Some terminal ->
                [ { Key = [ "kill-armed"; TerminalId.value terminal ]
                    After = armedMs
                    Fire = ArmKillMsg None } ]
            | None -> []
        let pending =
            [ "claude", model.Claude.Pending; "github", model.GitHub.Pending ]
            |> List.choose (fun (panel, pending) ->
                match pending with
                | Pending.Awaiting (_, since) ->
                    Some
                        { Key = [ "pending"; panel; string since ]
                          After = int Pending.deadlineMillis
                          Fire = PendingWaitedMsg (since + Pending.deadlineMillis) }
                | Pending.Ready | Pending.Sending | Pending.Refused _ -> None)
        catchUp @ copied @ queueDeleteArmed @ killArmed @ pending @ quiet @ GitHubPoll.timer model.GitHub

    /// Fold a message into the model — the state half of `update`, its only caller.
    /// Piped through `reconcileLaunch` (see its doc) so the launch surface anchors here,
    /// after every message, rather than being read live from whatever the connection
    /// happens to be doing at render time — and through `recall`, for the same reason, so a
    /// remembered pane comes back on whichever message brings its terminals in; and through
    /// `settle`, after it, so the strip holds only what it may whichever message moved it;
    /// and through `openOfItself`, which waits for the line `recall` settles at and needs the
    /// model from before the message to tell whether something else moved the column first;
    /// and through `landsWhenHeard` after it, so a pane already up when that line is crossed
    /// lands where a pane brought up then would;
    /// and through `notice`, so what this person has looked at is counted from wherever the
    /// message left the pane; and through `holdFilter` after it, last before `heard`, so the
    /// `all` page's filter judges a terminal by what it is once that has been counted.
    /// The pane put away — and any popover hanging in it, which does not outlive it.
    let private paneHidden (model: ClientModel) : ClientModel =
        { model with TerminalsOpen = false; Switcher = false; PaneMenu = false }

    /// Bringing the sidebar column on screen, which every way in shares — the nav toggle,
    /// settings, a call to action that reveals settings — so the next way in cannot open the
    /// drawer and forget the pane.
    ///
    /// On a phone the drawer is one of TWO sheets over the chat, and the pane is the other, at
    /// the same layer and later in the document: a drawer opened while the pane was up opened
    /// UNDERNEATH it, open and holding focus and nowhere to be seen. Closing the pane first
    /// means there is only ever one sheet, and the drawer's way back lands on the chat — which
    /// is where the pane's goes as well.
    let private columnOn (model: ClientModel) : ClientModel =
        if model.Column.Wide then { model with Column = { model.Column with Collapsed = false } }
        else paneHidden { model with Column = { model.Column with Drawer = true } }

    /// The other half of `columnOn`'s rule: taking the reader INTO the pane from inside the
    /// phone's drawer — the sidebar's terminals — shuts the drawer, so there is still only one
    /// sheet and it is the one they asked for, rather than a pane opened behind the drawer they
    /// pressed it in. Nothing on a desktop, where the column is not a sheet.
    let private drawerShut (model: ClientModel) : ClientModel =
        if model.Column.Wide then model else { model with Column = { model.Column with Drawer = false } }

    /// The reader chose a width. What is kept is what the screen then shows — `wanted` held to
    /// what the chat can spare — so a drag past the end is a choice of the end. Before the
    /// window is measured there is nothing to hold it to, and it is kept as asked.
    let private choosing (wanted: float) (model: ClientModel) : ClientModel =
        let width =
            match model.PaneRoom.Window with
            | Some window -> (PaneSplit.resolve model.Column window (Some (int (round wanted)))).Width
            | None -> int (round wanted)
        { model with PaneRoom = { model.PaneRoom with Chosen = Some width } }

    let rec private fold (msg: ClientMsg) (model: ClientModel) : ClientModel =
        heard (holdFilter model (notice (reconcileLaunch (landsWhenHeard (openOfItself model (settle (recall (
        match msg with
        | ConnectingMsg ->
            { model with Connection = Connecting }
        | ConnectedMsg accepted ->
            { model with
                Connection = Connected
                Session = Some accepted.SessionId
                Peer = { model.Peer with DisplayName = accepted.AssignedDisplayName }
                EventConsumer = withLatestKnown accepted.LatestOffset model.EventConsumer }
        | RejectedMsg reason ->
            { model with Connection = Disconnected (Some reason) }
        | RetryingMsg (reason, failures) ->
            { model with Connection = Retrying (reason, failures) }
        | EventsAvailableMsg latest ->
            { model with EventConsumer = withLatestKnown (Some latest) model.EventConsumer }
        // An answer is folded whichever read it answers — the fold is offset-gated, so a late
        // one costs nothing — and what it settles is `reads`' to decide, below.
        | EventsReadMsg (_, Ok page) -> fold (EventsPageMsg page) model
        | EventsReadMsg (_, Error reason) -> fold (EventFeedMsg (FeedStalled reason)) model
        // One fold, reached by two messages. The events are the same events and the
        // projection is the same projection; what differs is what arriving PROVED, and that
        // is `Feed`, decided below rather than in here.
        | EventsPageMsg page | LocalHistoryMsg page ->
            // The offset-gated projection fold makes overlapping/duplicate pages
            // idempotent: events at or below the processed offset are skipped.
            let conversation, highWater =
                ConversationProjection.applyEvents
                    model.EventConsumer.LastProcessedOffset
                    page.Events
                    model.Conversation
            let freshEvents =
                let appliedThrough = model.EventConsumer.LastProcessedOffset |> Option.map EventOffset.value
                page.Events
                |> List.filter (fun e ->
                    match appliedThrough with
                    | Some n -> EventOffset.value e.Offset > n
                    | None -> true)
            let agent =
                freshEvents
                |> List.fold
                    (fun (agent: AgentViewState) e ->
                        match e.Event with
                        // A fresh turn clears a stale `Interrupting`, too: that flag could
                        // only have named the turn just gone, and a click against the one
                        // before it has nothing left to mean.
                        | AgentTurnStarted a -> { agent with ActiveTurn = Some a.AgentTurnId; Interrupting = None }
                        | AgentMessageCompleted _ | AgentTurnFailed _ | AgentTurnInterrupted _ ->
                            { agent with ActiveTurn = None; Interrupting = None }
                        | _ -> agent)
                    model.Agent
            let environment =
                freshEvents
                |> List.fold (fun status e -> EnvironmentStatus.applyEvent status e.Event) model.Environment
            // The one fact the launch surface reads off the log: its clone failed. Only while
            // it is waiting on one — a failure from before this client arrived is history,
            // and history is the timeline's to show.
            let launch =
                freshEvents
                |> List.fold
                    (fun (launch: LaunchViewState) e ->
                        match e.Event, launch.Stage with
                        | SessionEvent.GatedCommandFailed failed, (Sent _ | Cloning _) when failed.Tool = "add_repo" ->
                            Launch.update (LaunchFailed failed.Reason) launch |> fst
                        | _ -> launch)
                    model.Launch
            let terminals =
                freshEvents
                |> List.fold (fun proj e -> Projection.applyEvent proj e.Event) model.Terminals
            // The roster keeps departed peers: a draft's author may have left while their words
            // are still in the composer, and "who wrote this" must still have an answer.
            let peers =
                freshEvents
                |> List.fold
                    (fun roster e ->
                        match e.Event with
                        | PeerJoined joined when joined.DisplayName.Trim () <> "" ->
                            Map.add joined.PeerId joined.DisplayName roster
                        | _ -> roster)
                    model.Peers
            // Same fold, same events, but the DECISION `Yession.Domain.Attribution` makes —
            // shared with the Session, which stamps `MessageSent.Author` with it — so
            // a `UserRef` author resolves to a name through the identical rule that decided
            // it was a `UserRef` in the first place, not a client-side guess that could
            // disagree with it. One state, one incremental step per event — `applyEvent`
            // already updates both directions (`PeerUsers` and `UserPeers`) from the one
            // `PeerJoined` match arm, so there is nothing left to fold twice or merge.
            let attribution =
                freshEvents
                |> List.map (fun e -> e.Event)
                |> List.fold Attribution.applyEvent model.Attribution
            let here = freshEvents |> List.fold (fun here e -> Here.applyEvent here e.Event) model.Here
            // A steal FROM this client, which only this client is told about (`Stolen`). Read
            // off the release the steal wrote rather than off the lease changing hands: a
            // hand-back and somebody else's take in one page change the holder just the same,
            // and nobody took anything from anybody. "This client" is `ClientModel.me`'s rule,
            // asked of the attribution this page folds — as `opened` below asks it, and for
            // the same reason.
            //
            // Only NEWS (`HeardThrough`): the log keeps every steal for ever, and a reload that
            // announced last week's would be telling nobody anything. Then held only while it
            // is still true — the taker still has the keyboard of a terminal still open — so a
            // notice can never outlive the steal it reports, whichever event ended it.
            let stolen =
                let mine = Attribution.actorFor attribution.PeerUsers model.Peer.PeerId
                let heard =
                    if not model.HeardThrough then model.Stolen
                    else
                        freshEvents
                        |> List.fold
                            (fun stolen e ->
                                match e.Event with
                                | SessionEvent.TerminalLeaseReleased released when released.Was = mine ->
                                    match released.Reason with
                                    | LeaseStolen by when by <> mine ->
                                        Some { StolenLease.Terminal = released.TerminalId; StolenLease.TakenBy = by }
                                    | LeaseStolen _ | LeaseReleased | LeaseHolderGone | LeaseIdle -> stolen
                                | _ -> stolen)
                            model.Stolen
                heard
                |> Option.filter (fun stolen ->
                    Projection.tryFind stolen.Terminal terminals
                    |> Option.exists (fun view -> view.IsOpen && view.Lease = Some stolen.TakenBy))
            // The terminal half of the chat, gated on the same offset as the conversation —
            // one page, two folds, merged only at render.
            let timeline, _ =
                TimelineProjection.applyEvents
                    model.EventConsumer.LastProcessedOffset
                    page.Events
                    model.Timeline
            // The tabs follow the terminals, which is what a tab is FOR: a terminal I asked
            // for opens one. One that ends takes its tab away again once nobody is looking at
            // it, which is `settle`'s rule and not this page's: it holds whichever message
            // moved the reader off it.
            //
            // "I asked for it" is `ClientModel.me`'s rule — `Attribution.actorFor`, the same
            // one the Session stamped the open with — asked of the attribution this
            // page has just been folded into rather than of `model.Attribution`, because the
            // `PeerJoined` that says who I am can arrive in the SAME page as the terminal I
            // opened. Reading the older copy here would leave the session's first page
            // tabless and nothing else, which is the kind of gap that is found once.
            let opened =
                let mine = Attribution.actorFor attribution.PeerUsers model.Peer.PeerId
                freshEvents
                |> List.choose (fun e ->
                    match e.Event with
                    | SessionEvent.TerminalOpened t when t.OpenedBy = mine -> Some t.TerminalId
                    // A terminal somebody put in front of the people here (`open_tab`). Not
                    // only the ones I asked for: that rule is about terminals a session starts
                    // on its own business, and this is an act whose entire point is that it is
                    // for whoever is reading. A FILE opened that way is not a tab — the strip
                    // holds terminals — and is shown only when it was asked to be (below).
                    | SessionEvent.TabOpened t ->
                        match t.Ref with
                        | ViewingTerminal terminal -> Some terminal
                        | ViewingFile _ -> None
                    | _ -> None)
            // Taken back (`close_tab`), in the same pass and after the opening, so a thing
            // opened and closed in one page ends closed rather than depending on which list
            // was built first.
            let closed =
                freshEvents
                |> List.choose (fun e ->
                    match e.Event with
                    | SessionEvent.TabClosed t -> Some t.Ref
                    | _ -> None)
                |> Set.ofList
            // Nor is the terminal the reader is LOOKING AT taken from under them: what is on
            // screen stays until they move off it. That held once by accident — the pane kept
            // its choice and the strip drew it back as a tab it no longer was — and is said
            // here now that the strip draws only `Tabs`.
            let showing = model.Pane |> Option.bind PaneMode.subject |> Option.map TerminalMode.terminal
            // Each in the slot its opening gives it (`withTab`), which for a terminal that has
            // just arrived is the end.
            //
            // A page of HISTORY (`HeardThrough`) is not a terminal closing under anybody: it
            // closed while this client was not here, and a strip rebuilt from the log with no
            // memory to say otherwise (`recall`) would otherwise be every terminal this person
            // ever opened. So a replay keeps only what still runs, or what is on screen — and a
            // closed tab this person had not put away comes back from the memory, which `recall`
            // applies after this. News of a close leaves the tab where it is (`settle`).
            let tabs =
                opened
                |> List.fold (fun tabs terminal -> withTab (openOrder terminals) terminal tabs) model.Tabs
                |> List.filter (fun terminal ->
                    showing = Some terminal
                    || (not (Set.contains (ViewingTerminal terminal) closed)
                        && (model.HeardThrough
                            || Projection.tryFind terminal terminals |> Option.exists (fun view -> view.IsOpen))))
            // Being SHOWN the terminal you pressed for, which is the whole of what the press
            // promised. A tab in the strip is not that: `selectedTerminal` keeps the stored
            // choice while what it names still exists, and the terminal you were on still
            // exists — so the press added a word to the strip and moved nothing, which on a
            // phone, where the strip scrolls, is a control that does nothing at all.
            //
            // Only against a press (`Opening`), and spent by it. The agent opening a terminal
            // beside your work never takes the pane, and neither does your own other tab —
            // which the log cannot tell from this one, and which is why the request rather
            // than the ownership is what this reads.
            let pane, opening =
                match model.Opening, List.tryLast opened with
                | 0, _ | _, None -> model.Pane, model.Opening
                | asked, Some arrived ->
                    Some (OnTerminal (Reading arrived)), max 0 (asked - List.length opened)
            // The other thing that may move the pane: somebody having been ASKED to show this
            // (`focus_tab`). The last one in the page wins, for the same reason the press
            // above takes the last terminal to arrive — two of them in one page is one answer
            // arriving late, not two answers. A terminal is shown; a file is laid over
            // whatever terminal is up, as a preview, exactly as its chip would lay it.
            //
            // Never on this client's FIRST page. A focus is a thing somebody asked for at a
            // moment, and the log keeps it for ever: replayed on a reload it would land a
            // reader on whatever the agent was showing an hour ago, ahead of whatever they
            // opened since. Arriving history is not a command.
            //
            // Read off `LastProcessedOffset` rather than `IsCatchingUp`, which looks like the
            // right question and is not: catch-up is true for a few dozen milliseconds every
            // time ANYBODY sends anything (its own declaration says so), so a focus landing in
            // that window would be dropped for a reason having nothing to do with it. Having
            // folded nothing yet is the one fact that distinguishes a page of history from a
            // page of news.
            //
            // A file opened WITHOUT focus opens nothing. It has no tab to land in, and a
            // preview nobody asked to see is the screen taken by somebody else's act; the list
            // is where it is in reach.
            let pane =
                if model.EventConsumer.LastProcessedOffset.IsNone then pane
                else
                    freshEvents
                    |> List.rev
                    |> List.tryPick (fun e ->
                        match e.Event with
                        | SessionEvent.TabOpened t when t.Focus ->
                            match t.Ref with
                            | ViewingTerminal terminal -> Some (Some (OnTerminal (Reading terminal)))
                            | ViewingFile ref ->
                                let subject = PreviewSubject.Content ref
                                let under = underFor subject (pane |> Option.bind PaneMode.subject)
                                Some (Some (Previewing (Preview.ofSubject subject, under)))
                        | _ -> None)
                    |> Option.defaultValue pane
            // A file taken back (`close_tab`) takes down its preview, if that is what is up —
            // back to the terminal it was laid over, as the reader's own back would.
            let pane =
                match pane with
                | Some (Previewing (preview, under)) when Set.contains (PreviewSubject.view preview.Subject) closed ->
                    match preview.Subject with
                    | PreviewSubject.Content _ -> under |> Option.map OnTerminal
                    | PreviewSubject.Block _ | PreviewSubject.Stretch _ -> pane
                | other -> other
            let latestKnown = EventOffset.maxOption model.EventConsumer.LatestKnownOffset highWater
            { model with
                Conversation = conversation
                Launch = launch
                Repos = freshEvents |> List.fold (fun proj e -> Repos.ReposProjection.applyEvent proj e.Event) model.Repos
                Approvals = RepoApprovals.apply model.Approvals (freshEvents |> List.map (fun e -> e.Event))
                Timeline = timeline
                Agent = agent
                Environment = environment
                Terminals = terminals
                Stolen = stolen
                Tabs = tabs
                Pane = pane
                Opening = opening
                // Spent by the close that answers it, in the page that carries it.
                KillPending =
                    model.KillPending
                    |> Option.filter (fun killed ->
                        not (
                            freshEvents
                            |> List.exists (fun e ->
                                match e.Event with
                                | SessionEvent.TerminalClosed closed -> closed.TerminalId = killed
                                | _ -> false)))
                // An armed kill means nothing over a terminal that has already gone — somebody
                // else ended it, or it exited — and must not sit there waiting to fire.
                KillArmed =
                    model.KillArmed
                    |> Option.filter (fun armed ->
                        Projection.tryFind armed terminals |> Option.exists (fun view -> view.IsOpen))
                Peers = peers
                Here = here
                Attribution = attribution
                EventConsumer =
                    { LastProcessedOffset = highWater
                      LatestKnownOffset = latestKnown
                      IsCatchingUp = isBehind highWater latestKnown
                      // A catch-up that has finished was never slow, whatever the timer
                      // was about to say.
                      CatchUpIsSlow =
                        isBehind highWater latestKnown && model.EventConsumer.CatchUpIsSlow
                      // A page off the NETWORK is proof the feed works, so recovery from a
                      // stall needs no separate signal. A page off the local store proves
                      // only that this client kept it — the feed is whatever it already
                      // was, which offline is exactly the truth the strip is showing.
                      Feed =
                        match msg with
                        | EventsPageMsg _ -> FeedLive
                        | _ -> model.EventConsumer.Feed
                      // A page off the NETWORK resumes at the cursor and runs unbroken from
                      // it, so whatever the store was missing before it is being filled —
                      // what is left to arrive is ordinary catch-up, which `IsCatchingUp`
                      // already says. A page off the local store is what found the hole in
                      // the first place and cannot have repaired it.
                      MissingBefore =
                        match msg with
                        | EventsPageMsg _ -> None
                        | _ -> model.EventConsumer.MissingBefore
                      // What the read loop has out is `reads`' to settle, not a page's.
                      Reading = model.EventConsumer.Reading } }
        | LocalHistoryGapMsg resumesAt ->
            { model with EventConsumer = { model.EventConsumer with MissingBefore = Some resumesAt } }
        | HistoryReadMsg -> { model with HistoryRead = true }
        | EventFeedMsg health ->
            { model with EventConsumer = { model.EventConsumer with Feed = health } }
        | CatchUpSlowMsg slow ->
            // Gated on still being behind: slow is a property of a catch-up that is running,
            // and the state holds that whoever dispatches.
            { model with
                EventConsumer =
                    { model.EventConsumer with
                        CatchUpIsSlow = slow && model.EventConsumer.IsCatchingUp } }
        | AgentQuietMsg stamp -> { model with Agent = { model.Agent with Quiet = Some stamp } }
        | DisconnectedMsg ->
            // What was asked over the channel that just died will never be answered over it:
            // a response belongs to the connection that carried its request.
            { model with Connection = Reconnecting; Asked = Map.empty }
        | EditTitleMsg title ->
            model |> withSynced { model.Synced with Title = title }
        | RemotePresenceMsg payload ->
            // Never render your own remote caret; a cleared focus removes the entry.
            if payload.Who = ActorRef.PeerRef model.Peer.PeerId then model
            else
                // The entry goes when the peer is nowhere AT ALL — no caret and no pane. A
                // cleared caret used to mean "forget them", and with viewing on the same frame
                // that would drop a reader the moment they stopped typing, which is precisely
                // the peer this feature exists to show.
                let presence =
                    match payload.Focus, payload.Viewing with
                    | None, None -> Map.remove payload.Who model.Presence
                    | focus, viewing ->
                        Map.add
                            payload.Who
                            { DisplayName = payload.DisplayName; Focus = focus; Viewing = viewing }
                            model.Presence
                { model with Presence = presence }
        | CaretMovedMsg focus -> { model with Caret = focus }
        | EnsureDraftMsg (peerId, queueId) ->
            // Materialise the slot keyed by `peerId` (author only) if absent, so the codec
            // anchors its body fragment and the editor can bind. Idempotent — and the queue key
            // of an existing slot is never re-minted, because every co-editor's send depends on
            // it staying the one the draft was published with.
            if Map.containsKey peerId model.Synced.Drafts then model
            else
                model
                |> withSynced
                    { model.Synced with
                        Drafts = Map.add peerId { Author = peerId; QueueId = queueId } model.Synced.Drafts }
        | SendDraftMsg peerId ->
            // Draft -> queue entry, atomically in one model update (one CRDT transaction):
            // the slot is deleted and its queue key created. The entry is attributed to the
            // slot's AUTHOR, not to whoever pressed send — the sender committed it, the author
            // wrote it — and it lands at the queue tail. Two peers sending this draft
            // concurrently write the same key, so the replicas merge to one entry instead of
            // queueing the message twice. The body fragment's content is carried over
            // imperatively (`Client.connect`'s SendDraft), not in the model.
            match Map.tryFind peerId model.Synced.Drafts with
            | Some draft when not (Map.containsKey draft.QueueId model.Synced.Queue) ->
                let entry =
                    { QueueId = draft.QueueId
                      Author = draft.Author
                      Order = QueueOrder.next model.Synced.Queue }
                model
                |> withSynced
                    { model.Synced with
                        Drafts = Map.remove peerId model.Synced.Drafts
                        Queue = Map.add draft.QueueId entry model.Synced.Queue }
            | _ -> model
        | DiscardDraftMsg peerId ->
            model |> withSynced { model.Synced with Drafts = Map.remove peerId model.Synced.Drafts }
        | ExpandDraftMsg peerId ->
            // One draft is open at a time, so opening one IS collapsing the other.
            { model with Composer = if peerId = model.Peer.PeerId then Own else Joined peerId }
        | StartDraftMsg ->
            { model with Composer = Own }
        | ReorderQueuedMsg (queueId, order) ->
            match Map.tryFind queueId model.Synced.Queue with
            | Some entry ->
                model
                |> withSynced
                    { model.Synced with Queue = Map.add queueId { entry with Order = order } model.Synced.Queue }
            | None -> model
        | DeleteQueuedMsg queueId ->
            // The armed slot dies with the entry it was guarding either way — a confirmed
            // press consumes it, and a direct dispatch (a test, a race) finds the entry gone
            // and nothing left for the armed id to mean.
            let model = if model.QueueDeleteArmed = Some queueId then { model with QueueDeleteArmed = None } else model
            model |> withSynced { model.Synced with Queue = Map.remove queueId model.Synced.Queue }
        | ArmQueueDeleteMsg next ->
            { model with QueueDeleteArmed = next }
        | ClaudeStatusMsg status ->
            // A connected credential ends the wait for the human in the other tab (the
            // callback completed there); otherwise the flow is untouched by a mere probe.
            let connected = status.SessionCredential.IsSome || status.MineCredential.IsSome
            let flow =
                match model.Claude.Flow, connected with
                | ClaudeAwaitingCode _, true -> ClaudeIdle
                | flow, _ -> flow
            // And this is the probe our own command is waiting on: it ends that wait when it
            // shows what the command asked for, and only then. A probe that has not caught
            // up leaves the wait standing, which is the difference between eventual
            // consistency and a coin flip.
            // `keeping` rather than the status bare: a reply that says nothing about models
            // must not blank a picker that has a list, and that rule lives with the status
            // so no caller can forget it (it used to be a `match` at the one dispatch site).
            let status = ClaudePanel.keeping model.Claude.Status status
            { model with
                Claude =
                  { model.Claude with
                      Status = Some status
                      Flow = flow
                      Pending = model.Claude.Pending |> Pending.observed ClaudePanel.landed status } }
        | ClaudeFlowMsg flow ->
            { model with Claude = { model.Claude with Flow = flow } }
        | ClaudePendingMsg pending ->
            { model with Claude = { model.Claude with Pending = pending } }
        | ClaudeScopeChosen scope -> { model with Claude = { model.Claude with Scope = scope } }
        | ClaudeCodeTyped code -> { model with Claude = { model.Claude with Code = code } }
        | ClaudeTokenTyped token -> { model with Claude = { model.Claude with Token = token } }
        | ClaudePressedMsg press ->
            match ClaudePress.call press model.Claude with
            // What was typed goes with the call: a code or a token sent is not one to send
            // again, and a field still holding it after the panel came back would be.
            | Ok _ -> { model with Claude = { model.Claude with Pending = Pending.Sending; Code = ""; Token = "" } }
            | Error reason -> { model with Claude = { model.Claude with Pending = Pending.Refused reason } }
        | ClaudeAnsweredMsg (call, answer, at) ->
            match answer with
            | Error reason -> { model with Claude = { model.Claude with Pending = Pending.Refused reason } }
            // Nothing for the panel to show yet: the credential arrives when the human
            // finishes in the tab this opens, and the stream says so.
            | Ok (Some authorizeUrl) ->
                { model with
                    Claude =
                        { model.Claude with
                            Pending = Pending.Ready
                            Flow = ClaudeAwaitingCode (authorizeUrl, call.Request.Scope) } }
            | Ok None ->
                let pending =
                    match call.Expect with
                    | Some expect -> Pending.Awaiting (expect, at)
                    | None -> Pending.Ready
                { model with Claude = { model.Claude with Pending = pending } }
        | GitHubStatusMsg status ->
            // The same two rules, for the same two reasons (see Claude's above).
            let connected = status.SessionCredential.IsSome || status.MineCredential.IsSome
            let flow =
                match model.GitHub.Flow, connected with
                | GitHubAwaitingApproval _, true -> GitHubIdle
                | flow, _ -> flow
            { model with
                GitHub =
                  { model.GitHub with
                      Status = Some status
                      Flow = flow
                      Pending = model.GitHub.Pending |> Pending.observed GitHubPanel.landed status } }
        | GitHubFlowMsg flow ->
            { model with GitHub = { model.GitHub with Flow = flow } }
        | GitHubPendingMsg pending ->
            { model with GitHub = { model.GitHub with Pending = pending } }
        | GitHubScopeChosen scope -> { model with GitHub = { model.GitHub with Scope = scope } }
        | GitHubTokenTyped token -> { model with GitHub = { model.GitHub with Token = token } }
        | GitHubPressedMsg press ->
            match GitHubPress.call press model.GitHub with
            | Ok _ -> { model with GitHub = { model.GitHub with Pending = Pending.Sending; Token = "" } }
            | Error reason -> { model with GitHub = { model.GitHub with Pending = Pending.Refused reason } }
        | GitHubAnsweredMsg (call, answer, at) ->
            match answer with
            | Error reason -> { model with GitHub = { model.GitHub with Pending = Pending.Refused reason } }
            | Ok (Some flow) ->
                { model with GitHub = { model.GitHub with Pending = Pending.Ready; Flow = flow; Polling = PollWaiting 0 } }
            | Ok None ->
                let pending =
                    match call.Expect with
                    | Some expect -> Pending.Awaiting (expect, at)
                    | None -> Pending.Ready
                { model with GitHub = { model.GitHub with Pending = pending } }
        | GitHubPollDueMsg round ->
            match GitHubPoll.due round model.GitHub with
            | Some _ -> { model with GitHub = { model.GitHub with Polling = PollAsking round } }
            | None -> model
        | GitHubPolledMsg (round, answer) ->
            match model.GitHub.Flow, model.GitHub.Polling with
            | GitHubAwaitingApproval (userCode, verificationUri, scope, interval), PollAsking asked when asked = round ->
                let next = PollWaiting (round + 1)
                match answer with
                // The flow is over as well as refused, and both have to be said: the code on
                // screen is dead, so it goes with the reason it died.
                | PollEnded reason ->
                    { model with GitHub = { model.GitHub with Flow = GitHubIdle; Pending = Pending.Refused reason; Polling = next } }
                | PollFailed -> { model with GitHub = { model.GitHub with Polling = next } }
                // GitHub's `slow_down` only ever widens the interval.
                | PollPending revised when revised > interval ->
                    { model with
                        GitHub =
                            { model.GitHub with
                                Flow = GitHubAwaitingApproval (userCode, verificationUri, scope, revised)
                                Polling = next } }
                | PollPending _ -> { model with GitHub = { model.GitHub with Polling = next } }
                | PollConnected -> { model with GitHub = { model.GitHub with Polling = PollGranted } }
            // An answer to a flow that was cancelled, or to a round already answered.
            | _ -> model
        | PendingWaitedMsg now ->
            { model with
                Claude = { model.Claude with Pending = Pending.waited now model.Claude.Pending }
                GitHub = { model.GitHub with Pending = Pending.waited now model.GitHub.Pending } }
        | QueryFrameMsg (QueriesDeclared defs) ->
            // The declarations REPLACE rather than merge: a reconnect re-declares, and a
            // query the session has dropped must leave the surface with it.
            { model with Queries = { model.Queries with Declared = defs } }
        | QueryFrameMsg (QueryValued (name, value)) ->
            { model with
                Queries =
                    { model.Queries with Values = Map.add (QueryName.value name) value model.Queries.Values } }
        | TerminalRecordsMsg (terminal, records) ->
            let feed =
                records
                |> List.fold (fun feed (seq, record) -> TerminalFeed.withRecord seq record feed) (terminalFeed terminal model)
            { model with TerminalFeeds = Map.add terminal feed model.TerminalFeeds }
        | TerminalPageMsg (terminal, records, header, readThrough) ->
            // Through the three folds it stands for rather than a fourth spelling of them, so
            // a page and the same lines arriving live cannot come to differ.
            let folded = fold (TerminalRecordsMsg (terminal, records)) model
            let withHeader =
                match header with
                | Some h -> fold (TerminalHeaderMsg (terminal, h)) folded
                | None -> folded
            fold (TerminalReadThroughMsg (terminal, readThrough)) withHeader
        | TerminalAvailableMsg (terminal, length) ->
            let feed = terminalFeed terminal model
            { model with
                TerminalFeeds =
                    Map.add terminal { feed with KnownLength = max feed.KnownLength length } model.TerminalFeeds }
        // Only news is folded: an answer that takes the reader nowhere is lines it holds
        // already, and a fold is a pass of the whole update loop over the page for nothing.
        | TranscriptReadMsg (terminal, _, Some page)
            when TranscriptCursor.advances (terminalFeed terminal model).ReadThrough page.NextSeq ->
            fold (TerminalPageMsg (terminal, page.Records, page.Header, page.NextSeq)) model
        // An answer from line 0 that ends where it began: the store has no line 0, and a
        // recording's line 0 is its header. That is the one way this client learns a recording
        // is not there — as opposed to not read yet, which is every feed's starting state.
        | TranscriptReadMsg (terminal, _, Some page) when page.NextSeq = 0 ->
            let feed = terminalFeed terminal model
            { model with TerminalFeeds = Map.add terminal { feed with Unrecorded = true } model.TerminalFeeds }
        | TranscriptReadMsg _ -> model
        | TerminalHeaderMsg (terminal, header) ->
            let feed = terminalFeed terminal model
            { model with TerminalFeeds = Map.add terminal { feed with Header = Some header } model.TerminalFeeds }
        | TerminalReadThroughMsg (terminal, seq) ->
            let feed = terminalFeed terminal model
            { model with
                TerminalFeeds =
                    Map.add
                        terminal
                        { feed with ReadThrough = max feed.ReadThrough seq; KnownLength = max feed.KnownLength seq }
                        model.TerminalFeeds }
        | TerminalKeyframeMsg (terminal, keyframe) ->
            { model with TerminalKeyframes = Map.add (terminal, keyframe.Seq) keyframe model.TerminalKeyframes }
        | TerminalScreenMsg (terminal, screen) ->
            { model with TerminalScreens = Map.add terminal screen model.TerminalScreens }
        | TerminalViewportMsg (terminal, size) ->
            // A box that is not in the document yet, or has just left it, measures as zero
            // cells — and a zero-column terminal is not a narrow one, it is a broken one. The
            // refusal is here, with the state, so that no route to it can put one in front of
            // a command: the reducer is the only way in, and it says no.
            if Size.isValid size then
                { model with TerminalViewports = Map.add terminal size model.TerminalViewports }
            else model
        // Asking shuts the menu that asked. The entry pressed is about to leave the
        // document, and a menu left standing over a terminal that is on its way is a surface
        // the reader has to dismiss before they can see what they asked for.
        | OpenTerminalMsg _ -> { model with Opening = model.Opening + 1; PaneMenu = false; Switcher = false }
        | ShowInPaneMsg mode ->
            // The WHOLE next face, stated by every way in. Nothing here clears a subset and
            // hopes the rest was already right: the switcher cannot outlive the choice made in
            // it, a preview cannot outlive the reader moving to a terminal, and a pin or a
            // start hint cannot outlive the mode that carried it.
            //
            // And showing a terminal OPENS it: the strip is the list of terminals the pane
            // can be about, so a terminal shown with no tab would be a pane showing something
            // the strip has no name for.
            { model with
                Tabs = withTab (openOrder model.Terminals) (TerminalMode.terminal mode) model.Tabs
                Pane = Some (OnTerminal mode)
                Switcher = false
                TerminalsOpen = true }
        | OpenInPaneMsg mode -> drawerShut (fold (ShowInPaneMsg mode) model)
        | ReplayCaughtUpMsg terminal -> fold (ShowInPaneMsg (Reading terminal)) model
        | ShowPreviewMsg preview
        | OpenPreviewMsg preview ->
            // Laid over the terminal the subject belongs to, which is shown in the strip as
            // the selected tab — so the strip never empties under a preview, and "back" has
            // somewhere to go. That terminal is opened if it was not: tapping a command's chip
            // is asking about that terminal, the same as choosing it from the switcher.
            //
            // ONE preview: this replaces whatever preview was up, and what it was laid over
            // carries across, so six chips tapped are one preview over the terminal the
            // reader was on, and back returns to that terminal where they left it.
            //
            // Except a CLOSED terminal with no tab: the reader put it away (`DismissTabMsg`),
            // or never had it, and a chip is a glance at one of its commands rather than
            // asking for the recording back — which it used to do, at the END of the strip
            // (F2). That preview is laid over whatever is up, as a file's is, and the strip
            // is left exactly as it was. The list is where a recording is opened again.
            let current = model.Pane |> Option.bind PaneMode.subject
            let putAway =
                PreviewSubject.terminal preview.Subject
                |> Option.exists (fun terminal ->
                    not (List.contains terminal model.Tabs)
                    && Projection.tryFind terminal model.Terminals |> Option.exists (fun view -> not view.IsOpen))
            let under = if putAway then current else underFor preview.Subject current
            { model with
                Tabs =
                    match under with
                    | Some mode -> withTab (openOrder model.Terminals) (TerminalMode.terminal mode) model.Tabs
                    | None -> model.Tabs
                Pane = Some (Previewing (preview, under))
                Switcher = false
                TerminalsOpen = true }
        | ClosePreviewMsg ->
            match model.Pane with
            | Some (Previewing (_, under)) -> { model with Pane = under |> Option.map OnTerminal }
            | Some (OnTerminal _) | None -> model
        | ShowInTerminalMsg (terminal, block) ->
            // The command has to be ON the page before anything can scroll to it or mark it:
            // a block inside a shut run is not, so the run opens on the way.
            let behind =
                model.Terminals.Terminals
                |> List.tryFind (fun view -> view.TerminalId = terminal)
                |> Option.bind (fun view ->
                    BlockGroup.holding terminal block (terminalFeed terminal model) view.Blocks)
            let unfolded =
                match behind with
                | Some key -> { model with OpenFolds = Set.add key model.OpenFolds }
                | None -> model
            fold (ShowInPaneMsg (ReadingAt (terminal, block))) unfolded
        // Taking somebody to a message from inside the phone's drawer lands it BEHIND the
        // drawer: scrolled, flashed and focused under a sheet they are still looking at. So
        // the drawer goes, here, for every jump to a message — a no-op from the timeline,
        // where none is open, and nothing for the next surface that jumps to remember.
        | MoveMsg (DomMove.RevealMessage _) when not model.Column.Wide ->
            { model with Column = { model.Column with Drawer = false } }
        | MoveMsg _
        | CopyMsg _
        | RetryNowMsg -> model
        | RewindTerminalMsg terminal ->
            // The length is pinned NOW rather than followed. A recording that grew under a
            // reader would move the scrub bar out from under them, which is the one thing
            // rewinding exists to avoid.
            let length = (model.TerminalFeeds |> Map.tryFind terminal |> Option.defaultValue TerminalFeed.empty).KnownLength
            // Rewinding IS asking to watch, said the same way as every other way in, with the
            // pin as the extra fact rather than a second kind of watching. What that buys: a
            // terminal that CLOSES under a rewound reader keeps playing rather than dropping
            // them back into its blocks, because the pin was the only part of their state
            // that died with the live edge (`rewoundTo` resolves it against `IsOpen`).
            //
            // What a pin gives up is following the tail while behind it: the recording under the
            // reader is fixed until they catch up. The alternative was a custom player source
            // driving history, tail and seek itself — the stock player's file source is static
            // and its live sources do not seek backwards — and it was not needed, because the
            // client already holds every record and mounting the ordinary whole-terminal cast
            // over `[0, pin)` replays through the same player a finished terminal uses. If
            // following-while-behind is ever wanted, that custom source is where it goes; it is
            // not something this reducer can grow.
            //
            // And watching opens the tab, as every way to a terminal does.
            { model with
                Tabs = withTab (openOrder model.Terminals) terminal model.Tabs
                Pane = Some (OnTerminal (WatchingBehind (terminal, length)))
                Switcher = false
                TerminalsOpen = true }
        | ToggleContentMsg ->
            // A popover does not outlive the pane it hangs in. Brought up, the pane lands where
            // every way of bringing it up lands (`landed`).
            if model.TerminalsOpen then paneHidden model
            else landed { model with TerminalsOpen = true; Switcher = false; PaneMenu = false }
        | HideContentMsg -> paneHidden model
        | ToggleNavMsg ->
            // The nav control always returns the column to its workspace face: a column that
            // came back on settings would be a surprise.
            let model = { model with Column = { model.Column with Face = ColumnFace.Workspace } }
            if Column.shown model.Column then { model with Column = Column.hide model.Column }
            else columnOn model
        | ToggleSettingsMsg ->
            let opening = model.Column.Face <> ColumnFace.Settings
            let model =
                { model with Column = { model.Column with Face = (if opening then ColumnFace.Settings else ColumnFace.Workspace) } }
            if opening then columnOn model
            // Closing the face on a phone closes the drawer with it. On a desktop the column
            // stays where it was: what changed is which face it shows, not whether it is there.
            elif not model.Column.Wide then { model with Column = { model.Column with Drawer = false } }
            else model
        // SET, not flipped, so pressing it twice is pressing it once.
        | RevealSettingsMsg -> columnOn { model with Column = { model.Column with Face = ColumnFace.Settings } }
        | ViewportMsg wide -> { model with Column = { model.Column with Wide = wide } }
        | PaneSplitMsg wanted -> choosing wanted model
        | ReaderMovedMsg (surface, following) ->
            { model with Away = (if following then Set.remove surface model.Away else Set.add surface model.Away) }
        | PaneNudgedMsg by ->
            match paneSplit model with
            | Some split -> choosing (float split.Width + by) model
            | None -> model
        | PaneWindowMsg width -> { model with PaneRoom = { model.PaneRoom with Window = Some (int (round width)) } }
        | PaneKeptMsg width -> { model with PaneRoom = { model.PaneRoom with Chosen = Some (int (round width)) } }
        | ToggleItemMenuMsg messageId ->
            // Opening one is writing the field, so opening a second shuts the first without
            // anybody arranging it. That is the whole reason this is one slot and not a set.
            let next = if model.ItemMenu = Some messageId then None else Some messageId
            { model with ItemMenu = next }
        | CloseItemMenuMsg -> { model with ItemMenu = None }
        // The menu hangs from the pivot's `+`, which the `all` page keeps on screen: it opens
        // over whatever the pane shows, that page included, and leaves it where it was.
        | TogglePaneMenuMsg -> { model with PaneMenu = not model.PaneMenu }
        | ClosePaneMenuMsg -> { model with PaneMenu = false }
        | ToggleFoldMsg key ->
            let next =
                if Set.contains key model.OpenFolds then Set.remove key model.OpenFolds
                else Set.add key model.OpenFolds
            { model with OpenFolds = next }
        | FoldSetMsg (key, opened) ->
            { model with OpenFolds = (if opened then Set.add key model.OpenFolds else Set.remove key model.OpenFolds) }
        | ToggleBreakTimeMsg messageId ->
            let next =
                if Set.contains messageId model.DatedBreaks then Set.remove messageId model.DatedBreaks
                else Set.add messageId model.DatedBreaks
            { model with DatedBreaks = next }
        | CopiedMsg (Some box) ->
            let nth =
                match model.Copied with
                | Some copy -> copy.Nth + 1
                | None -> 1
            { model with Copied = Some { Copy.Box = box; Copy.Nth = nth } }
        | CopiedMsg None -> { model with Copied = None }
        | ToggleSwitcherMsg ->
            // Opening brings the pane: reaching for a terminal you cannot see is exactly the
            // case where the pane is shut. Leaving it leaves the pane as it is. Going to it
            // shuts the menu, as moving to any other pivot item would: a popover does not
            // outlive the page it hung over.
            if model.Switcher then { model with Switcher = false }
            else fold OpenAllMsg model
        | OpenAllMsg -> drawerShut { model with Switcher = true; PaneMenu = false; TerminalsOpen = true }
        | CloseSwitcherMsg -> { model with Switcher = false }
        | FilterListMsg kind -> { model with ListFilter = chooseFilter kind model }
        | EnsureTerminalDraftMsg (terminal, author, queueId) ->
            // Typing changes nothing about the strip: a terminal being typed in is on screen
            // already, which is the whole of what it needs.
            //
            // Idempotent, and the queue key of an existing slot is never re-minted: every
            // co-editor's send depends on it staying the one the slot was published with.
            if Map.containsKey (terminal, author) model.Synced.TerminalDrafts then model
            else
                model
                |> withSynced
                    { model.Synced with
                        TerminalDrafts =
                            Map.add
                                (terminal, author)
                                { Terminal = terminal; Author = author; QueueId = queueId }
                                model.Synced.TerminalDrafts }
        | SendTerminalDraftMsg (terminal, author) ->
            // Slot -> queue entry in one model update (one CRDT transaction), attributed to
            // the slot's AUTHOR rather than to whoever pressed send. Two peers sending the
            // same slot write the same key, so the replicas merge to one entry instead of
            // running the command twice. The command TEXT is carried over imperatively in
            // the same transaction (`Client.connect`'s SendTerminalDraft) — shared types
            // cannot be re-parented.
            match Map.tryFind (terminal, author) model.Synced.TerminalDrafts with
            | Some draft when not (Map.containsKey draft.QueueId model.Synced.Pending) ->
                let entry =
                    { QueueId = draft.QueueId
                      Terminal = terminal
                      Order = TerminalQueueOrder.nextFor terminal model.Synced.Pending
                      // The author is the PEER who wrote it. Attribution to a verified user
                      // happens at the durable append, where the Session knows the
                      // binding — the doc only ever knows connections.
                      //
                      // `ofAuthor`, so it runs as its own author: a terminal command is a
                      // shell line in a sandbox, not a call against somebody's credential —
                      // and a person's act cannot accidentally carry one.
                      Authority = Authority.ofAuthor (Principal.Peer author)
                      // A person's composer never waits on a command, so there is nothing
                      // for a background flag to spare them (Plan 20, stage 2).
                      Background = false
                      // Nor asks for stdin: a person's block reads the terminal regardless
                      // (`BlockStdinPolicy`), so the ask is the agent's alone to make.
                      Stdin = false
                      // The width of the box this author is looking at, so the output is laid
                      // out for the screen it will be read on. Absent when nothing has been
                      // measured — a terminals column that has never been opened — which is a
                      // claim of nothing rather than a guess at eighty.
                      Size = Map.tryFind terminal model.TerminalViewports }
                model
                |> withSynced
                    { model.Synced with
                        TerminalDrafts = Map.remove (terminal, author) model.Synced.TerminalDrafts
                        Pending = Map.add draft.QueueId entry model.Synced.Pending }
            | _ -> model
        | DiscardTerminalDraftMsg (terminal, author) ->
            model
            |> withSynced
                { model.Synced with TerminalDrafts = Map.remove (terminal, author) model.Synced.TerminalDrafts }
        | DeletePendingMsg queueId ->
            model |> withSynced { model.Synced with Pending = Map.remove queueId model.Synced.Pending }
        | ReorderPendingMsg (queueId, order) ->
            match Map.tryFind queueId model.Synced.Pending with
            | Some entry ->
                model
                |> withSynced
                    { model.Synced with
                        Pending = Map.add queueId { entry with Order = order } model.Synced.Pending }
            | None -> model
        | LaunchMsg msg -> { model with Launch = Launch.update msg model.Launch |> fst }
        | CommandSentMsg (request, command) -> { model with Asked = Map.add request command model.Asked }
        | CommandAnsweredMsg (request, result) ->
            // The launch surface still reads every answer, because it tracks the request it
            // sent and has its own place to show the outcome. Every OTHER refusal is kept,
            // whoever asked: a command the session would not honour used to arrive here and
            // go no further.
            let asked = Map.tryFind request model.Asked
            let launchOwns = Launch.awaits request model.Launch
            let model =
                { model with
                    Launch = Launch.update (LaunchAnswered (request, result)) model.Launch |> fst
                    Asked = Map.remove request model.Asked }
            match result with
            // The launch card says its own refusal, under the row it was sent from. The
            // notice saying it again over the same card was one refusal on the screen twice.
            | CommandRejected _ when launchOwns -> model
            | CommandRejected reason ->
                // A refused New terminal is still an ANSWER to the press that asked for it,
                // and spends it. Left owed, the next terminal this person opened anywhere —
                // from another tab, or one of their own the log cannot tell from this one —
                // would be taken for the one this press asked for and pull the pane over to
                // it, long after the press had been told no.
                let opening =
                    match asked with
                    | Some (OpenTerminal _) -> max 0 (model.Opening - 1)
                    | _ -> model.Opening
                // And a refused kill spends the kill it asked for, by the same rule: no close
                // is coming for it now. Only the press still owed — a refusal of an earlier
                // kill must not cancel a later one.
                let killPending =
                    match asked, model.KillPending with
                    | Some (CloseTerminal refused), Some pending when refused = pending -> None
                    | _ -> model.KillPending
                let refusal =
                    { Refusal.Reason = reason
                      Refusal.FromPane = asked |> Option.exists Refusal.fromPane
                      Refusal.FocusedIn = None }
                // A refusal replacing another in the same mount is drawn into the same
                // element, so a keyboard that was in the old one is in the new one.
                let focusedIn =
                    model.Refused
                    |> Option.bind (fun was -> was.FocusedIn)
                    |> Option.filter (fun was -> was = Refusal.mount model.TerminalsOpen refusal)
                { model with
                    Opening = opening
                    KillPending = killPending
                    Refused = Some { refusal with FocusedIn = focusedIn } }
            // An acceptance clears whatever the last refusal was. The reader has just been
            // told something worked, and a notice about something that did not, left standing
            // beside it, is a screen arguing with itself.
            | CommandAccepted -> { model with Refused = None }
        | DismissRefusalMsg -> { model with Refused = None }
        | DismissStolenMsg -> { model with Stolen = None }
        | RefusalFocusMsg mount ->
            { model with Refused = model.Refused |> Option.map (fun refusal -> { refusal with FocusedIn = mount }) }
        | SetModelMsg choice -> model |> withSynced { model.Synced with Model = choice }
        | DismissLaunchMsg -> model |> withSynced { model.Synced with LaunchDismissed = true }
        // An id this client's window does not hold is a page boundary, not a bug — and
        // there is nothing to toggle, because what the verdict would default to is on the item.
        | ToggleChapterMsg messageId ->
            // The menu shuts either way. It is the surface a chapter is opened from, and one
            // left standing over an act it has already performed is a menu asking to be
            // pressed again — including when the item was not found, where leaving it open
            // would be a menu offering something that cannot happen.
            let model = { model with ItemMenu = None }
            match model.Conversation.Items |> List.tryFind (fun item -> item.MessageId = messageId) with
            | Some item ->
                model
                |> withSynced
                    { model.Synced with Chapters = Chapters.toggle CollabText.ylmish AutoChapters.policy model.Conversation.Items item model.Synced.Chapters }
            | None -> model
        // The item again, and for the reason the toggle needs it: a chapter nobody has touched
        // has no entry, so the rename has to record the verdict the item already carried.
        | EditChapterNameMsg (messageId, said) ->
            match model.Conversation.Items |> List.tryFind (fun item -> item.MessageId = messageId) with
            | Some item ->
                model
                |> withSynced
                    { model.Synced with Chapters = Chapters.rename AutoChapters.policy model.Conversation.Items item said model.Synced.Chapters }
            | None -> model
        // Requests of the session and nothing else: what they change arrives as events, which
        // every peer folds alike, so a local guess here would be a state only this peer had.
        // Local-only: the request itself is the session's to grant, and its outcome arrives
        // as events like every other fact here — but the click sets a flag nothing else would,
        // so the button reads as pressed rather than silent for however long the round trip
        // takes. The event fold above is what clears it; this never does.
        | InterruptTurnMsg turn ->
            { model with Agent = { model.Agent with Interrupting = Some turn } }
        | TakeTerminalMsg _
        | ReleaseTerminalMsg _
        | RearmTerminalMsg _
        | ReattachTerminalMsg _
        | InterruptTerminalMsg _
        | ApproveRepoCapabilitiesMsg _ -> model
        // A request of the session like those above, and also a press whose control the
        // answer will take away: remembered, so the close that answers it can say where the
        // hand goes next (`KillPending`).
        // The armed slot is spent by the press that confirms it.
        | CloseTerminalMsg terminal -> { model with KillPending = Some terminal; KillArmed = None }
        // Never reaches a fold: `update` turns it into the press it is first.
        | KillPressedMsg _ -> model
        | ArmKillMsg next -> { model with KillArmed = next }
        | DismissTabMsg terminal ->
            match Projection.tryFind terminal model.Terminals with
            | Some view when view.IsOpen -> model
            | Some _
            | None ->
                let next = dismissLanding model terminal
                { model with
                    Tabs = model.Tabs |> List.filter (fun tab -> tab <> terminal)
                    Pane =
                        if selectedTerminal model = Some terminal then next |> Option.map (Reading >> OnTerminal)
                        else model.Pane }
        ))))))))

    /// The read loop's decisions: what a message, once folded, asks to be read next — of the
    /// event log and of each terminal's transcript — and what it settles of the reads out.
    /// The connection only carries the reads out (`ClientEffect.ReadEvents`,
    /// `ClientEffect.ReadTranscript`) and hands each answer back under the number it was
    /// asked with.
    ///
    /// Event consumption is read-only and offset-driven: `EventsAvailable` hints (and the
    /// accepted handshake's latest offset) only trigger reads; the returned pages are the
    /// source of truth. One read is out at a time; a non-final page immediately asks for the
    /// next, and a final one asks again only while the model is still behind. A read that
    /// FAILS (only possible over an HTTP feed, which has already exhausted its resilience
    /// policy) parks the loop and reports `FeedStalled` — it never masquerades as an empty
    /// page.
    ///
    /// Where each read starts is the model's read position, taken as the read is asked — the
    /// event log's `LastProcessedOffset`, a terminal's `ReadThrough` — and nothing else: the
    /// loop keeps no cursor of its own, because two drift. A private cursor advances when a
    /// page ARRIVES; the model's advances when the page is FOLDED. Anything that discards a
    /// fold — a decode failure keeping the current model, a reconnect onto a restored replica
    /// — moves them apart, and a loop reading its own cursor then believes it is up to date
    /// while the model is missing events nothing will ever offer again. Asking the model
    /// makes that unrepresentable: a model that lost a fold is visibly behind, so the next
    /// hint re-reads it, and the fold is offset-gated, so a re-read costs a round trip and
    /// changes nothing else. The same holds one feed over: a client that replayed a terminal
    /// out of its own store resumes where that got to, not at line 0. And a reconnect needs
    /// no offset handed to it — the model already says where it got to.
    ///
    /// A connection is accepted (`ConnectedMsg`) or lost (`DisconnectedMsg`) with whatever
    /// it had out forgotten: what the old channel was asked it will never answer, and what the
    /// old HTTP reads answer late is folded — the fold cannot be hurt by it — but carries a
    /// number nothing is waiting on, so it neither settles a read the new connection asked nor
    /// asks one of its own. A new connection starts with nothing out, as it always has.
    let private reads (msg: ClientMsg) (model: ClientModel) : ClientModel * ClientEffect list =
        // Each read under a number no read before it carried (`ReadsAsked`).
        let numbered (model: ClientModel) = model.ReadsAsked + 1, { model with ReadsAsked = model.ReadsAsked + 1 }
        let askEvents (model: ClientModel) =
            let read, model = numbered model
            { model with EventConsumer = { model.EventConsumer with Reading = Some read } },
            [ ClientEffect.ReadEvents (read, model.EventConsumer.LastProcessedOffset) ]
        // Behind, and nothing out: the one state in which a hint is worth a read.
        let askEventsIfBehind (model: ClientModel) =
            if model.EventConsumer.Reading.IsNone
               && isBehind model.EventConsumer.LastProcessedOffset model.EventConsumer.LatestKnownOffset then
                askEvents model
            else model, []
        let askTranscript (terminal: TerminalId) (fromSeq: int) (owed: bool) (model: ClientModel) =
            let read, model = numbered model
            { model with
                TranscriptReads =
                    Map.add terminal { TranscriptRead.Read = read; TranscriptRead.From = fromSeq; TranscriptRead.Owed = owed } model.TranscriptReads },
            [ ClientEffect.ReadTranscript (terminal, read, fromSeq) ]
        // Where this terminal's transcript has been read to — the model's `ReadThrough`. Only
        // a contiguous HTTP read moves it. A live record at a higher seq is still folded into
        // the model — it is keyed by seq, so it lands wherever it belongs — but it does not
        // prove the records BEFORE it have arrived, and treating it as if it did is how a
        // client ends up with a hole it will never fetch.
        let readPositionOf (terminal: TerminalId) = (terminalFeed terminal model).ReadThrough
        // ONE read per terminal at a time, for the reason the event log has one: every signal
        // that there is more — each live record past the read position, each availability
        // hint — used to start a read of its own, and the read position does not move until an
        // answer lands. So a burst asked the same question once per record, all at once: `seq
        // 100000` is ~170 records in under three seconds, and it raised ~170 concurrent reads
        // of `after/3`, each answered with a larger page, each page a fold and a render. That
        // queue of renders held the page's main thread long enough for the link heartbeat to
        // go unanswered (`Link`), so the Session dropped a peer that was alive — and the
        // reconnect raised the storm again.
        //
        // A signal that arrives during a read is not dropped: it earns ONE more read, from
        // wherever the first got to, once it lands. However long the burst, that is at most
        // two reads per terminal — the one out, and the one owed.
        let signalled (terminal: TerminalId) =
            match Map.tryFind terminal model.TranscriptReads with
            | Some out -> { model with TranscriptReads = Map.add terminal { out with Owed = true } model.TranscriptReads }, []
            | None -> askTranscript terminal (readPositionOf terminal) false model
        let forgotten (model: ClientModel) =
            { model with
                EventConsumer = { model.EventConsumer with Reading = None }
                TranscriptReads = Map.empty }
        // A CLOSED terminal signals nothing: no live record, and no length at a join — those
        // are the open terminals' (`SessionTerminals.Lengths`). So a reader who was not here
        // while it ran had nothing to read on, held nothing of its recording, and was told
        // the recording was lost. Each closed terminal this client has heard nothing of is
        // asked about once, from line 0, and the answer settles it either way: lines are the
        // recording, and nothing at all is the store saying it has none
        // (`TerminalFeed.Unrecorded`).
        //
        // Asked when a connection is accepted, and when an event page may have brought a
        // terminal this client did not know — the only two ways a closed terminal nobody has
        // read reaches the model — and only while connected, because a read asked with no
        // connection to carry it is a read the model believes is out until the next one. Never
        // on a transcript answer: a read that failed leaves the terminal unheard, and asking
        // again from its own answer is a spin.
        let unheard ((model: ClientModel), (effects: ClientEffect list)) =
            model.Terminals.Terminals
            |> List.filter (fun view ->
                model.Connection = Connected
                && not view.IsOpen
                && recordingOf view.TerminalId model = RecordingKnown.NotYetKnown
                && not (Map.containsKey view.TerminalId model.TranscriptReads))
            |> List.fold
                (fun (model, effects) view ->
                    let model, asked = askTranscript view.TerminalId 0 false model
                    model, effects @ asked)
                (model, effects)
        match msg with
        | ConnectedMsg _ -> askEventsIfBehind (forgotten model) |> unheard
        | DisconnectedMsg -> forgotten model, []
        | EventsAvailableMsg _ -> askEventsIfBehind model
        | EventsReadMsg (read, answer) when model.EventConsumer.Reading = Some read ->
            let settled = { model with EventConsumer = { model.EventConsumer with Reading = None } }
            match answer with
            // A non-final page means more events already exist beyond this one.
            | Ok page when not page.IsEnd -> askEvents settled |> unheard
            | Ok _ -> askEventsIfBehind settled |> unheard
            // The feed's policy has already spent its retries by the time this is reached, so
            // do NOT re-request here: park, and re-arm on the next availability hint or
            // reconnect. The read position is untouched, so the re-arm resumes exactly where
            // consumption stopped.
            //
            // This is the seam that used to fail silently. A failed fetch became an empty
            // FINAL page, which advanced nothing; "behind" therefore stayed true and the loop
            // re-requested immediately — an unbounded spin, one request per round trip, with
            // no log line and nothing in the model. Drafts, title, and presence kept syncing
            // over the data channel the whole time, so the only symptom was a timeline that
            // never filled.
            | Error _ -> settled, []
        // Folded whichever read they answer, so a terminal they bring is the model's either way.
        | EventsReadMsg (_, Ok _)
        | EventsPageMsg _ -> unheard (model, [])
        // The terminal-feed counterpart of `EventsAvailable`: a hint that there is more,
        // answered by a read rather than by trusting the hint's contents.
        | TerminalAvailableMsg (terminal, length)
            when TranscriptCursor.unread (readPositionOf terminal) (AvailableLength length) ->
            signalled terminal
        // A live record at or beyond the read position means history exists that this client
        // has not fetched — the records between where it read to and where the live stream now
        // is. Ask for them.
        //
        // Which comparison that is belongs to `TranscriptCursor`, not here: the index vs count
        // distinction that decides it is carried by `TranscriptSignal`, and getting it wrong at
        // this call site is the bug that shipped a terminal whose output never reached the
        // store. See `Yession.Domain.Terminals`.
        | TerminalRecordsMsg (terminal, records)
            when records |> List.exists (fun (seq, _) -> TranscriptCursor.unread (readPositionOf terminal) (RecordAt seq)) ->
            signalled terminal
        | TranscriptReadMsg (terminal, read, answer) ->
            match Map.tryFind terminal model.TranscriptReads with
            | Some out when out.Read = read ->
                match answer with
                // `NextSeq > From` guards the one way this could spin: a chunk that yields
                // nothing new would otherwise be re-read for ever at the same offset.
                | Some page when not page.IsEnd && page.NextSeq > out.From ->
                    askTranscript terminal page.NextSeq out.Owed model
                // Settled — at the tail, or failed. A transcript read that fails is not a
                // session that failed: the live leg keeps delivering, and the next
                // availability hint re-arms this. Parking beats spinning. What was owed is
                // asked now, from wherever this read got to.
                | _ when out.Owed -> askTranscript terminal (readPositionOf terminal) false model
                | _ -> { model with TranscriptReads = Map.remove terminal model.TranscriptReads }, []
            | _ -> model, []
        | _ -> model, []

    /// A message's consequences: the next model, and what it asks of the world outside it.
    let private apply (msg: ClientMsg) (model: ClientModel) : ClientModel * ClientEffect list =
        let next, reading = reads msg (fold msg model)
        // The launch surface's listing is asked for by whichever message first offers it:
        // anchoring happens once in a client's life on a session, so this asks once, and
        // the surface has no mount of its own to ask from.
        let offering =
            if not model.Launch.Anchored && next.Launch.Anchored then
                [ ClientEffect.Launch (LaunchEffect.Search next.Launch.Query) ]
            else []
        let effects =
            match msg with
            | TakeTerminalMsg terminal -> [ ClientEffect.TakeTerminal terminal ]
            // The notice goes from under the press that put it away, and the lease bar takes
            // its place; the keyboard goes to the command line under both, which is where the
            // pane lands whenever somebody else holds the keyboard.
            | DismissStolenMsg ->
                model.Stolen
                |> Option.map (fun stolen -> ClientEffect.Move (DomMove.FocusCommandLine stolen.Terminal))
                |> Option.toList
            | ReleaseTerminalMsg terminal -> [ ClientEffect.ReleaseTerminal terminal ]
            | RearmTerminalMsg terminal -> [ ClientEffect.RearmTerminal terminal ]
            | ReattachTerminalMsg terminal -> [ ClientEffect.ReattachTerminal terminal ]
            | CloseTerminalMsg terminal -> [ ClientEffect.CloseTerminal terminal ]
            // Asked only of a terminal with a block running: Ctrl-C in an empty command line
            // is pressed in idle terminals far more often than in busy ones, and a request the
            // session can only refuse is a refusal notice for a key that meant nothing.
            //
            // And the keyboard goes to the command line, because the Stop it may have been
            // pressed on goes when the command ends — a round trip later, with focus on it,
            // which strands focus on `body`. The command line is where the next thing is typed
            // after stopping one; from Ctrl-C there, the move is to where focus already is.
            | InterruptTerminalMsg terminal ->
                match Projection.tryFind terminal model.Terminals |> Option.bind Projection.runningBlock with
                | Some _ ->
                    [ ClientEffect.InterruptTerminal terminal
                      ClientEffect.Move (DomMove.FocusCommandLine terminal) ]
                | None -> []
            | OpenTerminalMsg (title, sandbox) -> [ ClientEffect.OpenTerminal (title, sandbox) ]
            | InterruptTurnMsg turn -> [ ClientEffect.InterruptTurn turn ]
            | ApproveRepoCapabilitiesMsg (repo, granted) -> [ ClientEffect.ApproveRepoCapabilities (repo, granted) ]
            | LaunchMsg launchMsg -> Launch.update launchMsg model.Launch |> snd |> List.map ClientEffect.Launch
            | ClaudePressedMsg press -> ClaudePress.call press model.Claude |> Result.toList |> List.map ClientEffect.Claude
            | GitHubPressedMsg press -> GitHubPress.call press model.GitHub |> Result.toList |> List.map ClientEffect.GitHub
            // Into the pane, and onto what the pane is FOR when it is a terminal: the reader
            // was moved, so their keyboard is too (`paneLanding`).
            | OpenInPaneMsg _ -> [ ClientEffect.Move (paneLanding next) ]
            // Showing the pane is the same promise as a chip opening something in it. Hiding it
            // sends focus back where the reader came from — the chip that opened the preview
            // that was up — or, with none, to the way back in: every control in a pane that
            // has gone is out of reach, so focus cannot stay where it was.
            | ToggleContentMsg ->
                if next.TerminalsOpen then [ ClientEffect.Move (paneLanding next) ]
                else
                    match preview model with
                    | Some preview -> [ ClientEffect.Move (DomMove.FocusChat preview.Subject) ]
                    | None -> [ ClientEffect.Move DomMove.FocusPaneReopen ]
            | HideContentMsg -> []
            // The control pressed is the one about to disappear, so focus goes to whichever
            // replaces it. Only a desktop's collapse is remembered: on a phone the same press
            // opens a drawer, which is a moment, not a preference.
            | ToggleNavMsg ->
                [ yield ClientEffect.Move (DomMove.FocusNavToggle (Column.shown next.Column))
                  if next.Column.Wide then yield ClientEffect.Remember (Preference.NavCollapsed next.Column.Collapsed) ]
            // A width the reader moved is kept; the one the browser put back at boot, from what it
            // had kept, is not written back, and nor is a window that changed size.
            | PaneSplitMsg _
            | PaneNudgedMsg _ ->
                match next.PaneRoom.Chosen with
                | Some width when model.PaneRoom.Chosen <> Some width -> [ ClientEffect.Remember (Preference.PaneWidth width) ]
                | _ -> []
            | ToggleSettingsMsg -> [ ClientEffect.Move (DomMove.FocusSettingsToggle (next.Column.Face = ColumnFace.Settings)) ]
            // Only when the face actually ARRIVED: stealing focus to a control already on screen
            // would be the prompt reaching into a panel the reader is already in.
            | RevealSettingsMsg when model.Column.Face <> ColumnFace.Settings ->
                [ ClientEffect.Move (DomMove.FocusSettingsToggle true) ]
            // A chip opening a preview is the same promise: the reader was moved, so their
            // keyboard is too.
            | OpenPreviewMsg _ -> [ ClientEffect.Move (paneLanding next) ]
            // The preview's close and Escape both hand focus back to the chip that
            // opened it: the preview is leaving the document with focus inside it, and the
            // chip is where the reader came from (`PaneShell.toChatItem` falls back to the pane
            // when the chip is covered, as it is on a phone).
            | ClosePreviewMsg ->
                match preview model with
                | Some preview -> [ ClientEffect.Move (DomMove.FocusChat preview.Subject) ]
                | None -> []
            // A terminal shown from under a preview — its tab pressed — takes the preview out
            // of the document, and with it whatever in the preview had the keyboard. So the
            // keyboard lands where the pane now does, if it was dropped; pressed on the tab
            // itself, it stays on the tab.
            | ShowInPaneMsg _ when Option.isSome (preview model) && not model.Switcher ->
                [ ClientEffect.Move (DomMove.IfDropped (paneLanding next)) ]
            | ShowInTerminalMsg (terminal, block) ->
                [ ClientEffect.Move (DomMove.RevealBlock (terminal, block)); ClientEffect.Move DomMove.FocusPane ]
            // Into the list as the page opens, and back onto the item it was laid over as it
            // goes: either way the control under the hand is about to leave the document, and
            // the keyboard has to go with the reader rather than be left on `body`.
            | ToggleSwitcherMsg ->
                if next.Switcher then [ ClientEffect.Move DomMove.FocusSwitcher ]
                else [ ClientEffect.Move DomMove.FocusPivot ]
            | OpenAllMsg -> [ ClientEffect.Move DomMove.FocusSwitcher ]
            | CloseSwitcherMsg when model.Switcher -> [ ClientEffect.Move DomMove.FocusPivot ]
            // The × that was pressed leaves with its tab: onto the tab that took its place, or
            // the pane's empty press when there is none.
            // Except put away from the `all` page: the row stays, as the closed terminal's row,
            // and only the press that put its tab away has left — so the hand stays on the row.
            | DismissTabMsg terminal when model.Tabs <> next.Tabs && model.Switcher ->
                [ ClientEffect.Move (DomMove.FocusSwitcherRow terminal) ]
            | DismissTabMsg terminal when model.Tabs <> next.Tabs ->
                match dismissLanding model terminal with
                | Some tab -> [ ClientEffect.Move (DomMove.FocusTab tab) ]
                | None -> [ ClientEffect.Move DomMove.FocusPivot ]
            | MoveMsg move -> [ ClientEffect.Move move ]
            // The player the hand was in is gone; the toggle that replaces it is where it lands.
            | ReplayCaughtUpMsg _ -> [ ClientEffect.Move DomMove.FocusWatchToggle ]
            // A deliberate send settles the view on what was just sent, whether or not the
            // sender had scrolled away while composing — the tail rule (`Tail`, in the
            // browser) only keeps a reader who was ALREADY following, which is a different
            // question. The jump-to-latest control's own scroll, without its focus move.
            | SendDraftMsg _ -> [ ClientEffect.Move (DomMove.ScrollToLatest TailSurface.Conversation) ]
            | CopyMsg (box, text) -> [ ClientEffect.Copy (box, text) ]
            | RetryNowMsg -> [ ClientEffect.RetryNow ]
            | GitHubPollDueMsg round ->
                GitHubPoll.due round model.GitHub |> Option.map (fun scope -> ClientEffect.GitHubPoll (round, scope)) |> Option.toList
            | _ -> []
        // The answers to presses made here, arriving a round trip later — read off what the
        // fold SPENT rather than off which message carried it, so whatever folds an arrival
        // answers for it. Both are `OnArrival`: the hand may have gone elsewhere since.
        //
        // A terminal pressed for lands where the pane now shows it — and so does one the
        // session REFUSED, which spends the press too: the pane has not moved, so that is
        // back where the press was made rather than on `body`. A kill lands where
        // `killLanding` says, measured against the pane the press was made on — once it has
        // CLOSED. A refused kill spends the press too, but nothing left the document: the
        // terminal is still open, its kill is still under the hand, and a landing measured for
        // a terminal that went would take the reader to its neighbour.
        let answered =
            [ if next.Opening < model.Opening then
                  ClientEffect.Move (DomMove.OnArrival (paneLanding next))
              match model.KillPending, next.KillPending with
              | Some killed, None when not (Projection.tryFind killed next.Terminals |> Option.exists (fun view -> view.IsOpen)) ->
                  ClientEffect.Move (DomMove.OnArrival (killLanding model killed))
              | _ -> () ]
        // The refusal notice going while the keyboard is in it — its dismiss pressed, or an
        // acceptance clearing it — takes the focused element out of the document, so focus
        // goes on to the surface the notice sat over: what the pane is showing, or the
        // message composer. Only when it WAS in there: an acceptance can arrive under
        // somebody typing elsewhere, and must not pull them out of it.
        let unnoticed =
            match model.Refused, refusalMount model, next.Refused with
            | Some was, Some mount, None when was.FocusedIn = Some mount ->
                match mount with
                | RefusalMount.Pane -> [ ClientEffect.Move (paneLanding next) ]
                | RefusalMount.Chat -> [ ClientEffect.Move DomMove.FocusComposer ]
            | _ -> []
        let swapped = keyboardSwap model next |> Option.map ClientEffect.Move |> Option.toList
        let landing = heardLanding model next |> Option.map ClientEffect.Move |> Option.toList
        let leased = leaseLanding model next |> Option.map ClientEffect.Move |> Option.toList
        let revealed = pivotReveal model next |> Option.map ClientEffect.Move |> Option.toList
        let resized = ptyResizes model next |> List.map ClientEffect.ResizeTerminal
        let present = presenceToSend model next |> Option.map ClientEffect.SendPresence |> Option.toList
        let kept = paneToRemember model next |> Option.map (Preference.Pane >> ClientEffect.Remember) |> Option.toList
        // Asked once per keyframe, whichever message first left the preview needing one.
        let next, fetching =
            match missingKeyframe next with
            | Some key when not (Set.contains key next.KeyframesAsked) ->
                { next with KeyframesAsked = Set.add key next.KeyframesAsked }, [ ClientEffect.FetchKeyframe key ]
            | Some _ | None -> next, []
        next, effects @ answered @ unnoticed @ swapped @ landing @ leased @ revealed @ resized @ present @ kept @ fetching @ offering @ reading

    /// A message and the effects it asks for. A kill's press is resolved here, against the
    /// model as it stands, into the press it is (`killPress`) — then applied like any other.
    let update (msg: ClientMsg) (model: ClientModel) : ClientModel * ClientEffect list =
        match msg with
        | KillPressedMsg terminal ->
            match killPress terminal model with
            | Some press -> apply press model
            | None -> model, []
        | _ -> apply msg model
