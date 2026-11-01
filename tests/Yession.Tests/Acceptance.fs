module Yession.Tests.Acceptance

// Step 09 — the Phase 1 acceptance gate's own checks. The seven required E2E scenarios
// and the model/protocol invariants live in their step suites (Sync/Agent/E2E/Client/
// Domain/Session — every E2E-N is named in its test title); this file pins the
// remaining acceptance items: the UI checklist, rendered from one representative model,
// and the random peer display name.
//
// The `(E2E-N)` suffixes are the phase acceptance gates' scenario numbers, and they are
// numbered PER PHASE — Phase 1's E2E-1 is the two-client draft, Phase 2's is the
// conversational one-shot. The gate documents that held both lists are gone; the titles are
// the index now, so a scenario's number means nothing without the phase beside it.

open System
open Fable.Pyxpecto
open Yession.Domain
open Yession.Domain.Access
open Yession.Domain.Sandboxes
open Yession.Domain.Agent
open Yession.Domain.Link
open Yession.Domain.Terminals
open Yession.Domain.Collab
open Yession.Domain.Repos
open Yession.Domain.Prs
open Yession.Domain.Chat
open Yession.Domain.Tools
open Yession.Domain.Chat
open Yession.Domain.Files
open Yession.Domain.Content
open Yession.Domain.Artifacts
open Yession.App
open Yession.Tests.Support

let private queueId = QueueId.create "queue-ui" |> expect
/// The key the fixture's draft will become when someone sends it — distinct from the entry
/// already queued, because a draft's key is one the queue does not hold yet.
let private draftQueueId = QueueId.create "queue-ui-draft" |> expect
let private ada = PeerId.create "ada" |> expect
let private bob = PeerId.create "bob" |> expect
let private carol = UserId.create "carol@example.com" |> expect

/// Two acts for the cases that need one and do not care which: one clause, and one with a
/// particular under it.
let private repoRemoved (repo: string) : Act =
    Act.RepoRemoved
        { MessageId = MessageId.create "act-removed" |> expect
          Repo = RepoRef.create repo |> expect
          Actor = PeerRef ada }

let private repoAdded (repo: string) (branch: string) : Act =
    Act.RepoCloned
        { MessageId = MessageId.create "act-added" |> expect
          Repo = RepoRef.create repo |> expect
          Branch = branch
          Actor = PeerRef ada
          AgentsMd = None
          Skills = [] }
let private sessionId = SessionId.create "demo-session" |> expect
let private terminalId = TerminalId.create "term-ui" |> expect
let private blockId = BlockId.create "block-ui" |> expect
let private terminalDraftQueueId = QueueId.create "queue-ui-term-draft" |> expect
let private terminalQueueId = QueueId.create "queue-ui-term" |> expect
/// The model this representative session has picked, and the catalogue it picked from.
/// Ids and labels a provider would give, so the picker renders the case that matters —
/// something is chosen, and the list it came from is on screen with it.
let private pickedModel = ModelId.create "example-model-large" |> expect
let private offeredModels =
    [ AgentModel.create pickedModel "Example Large"
      AgentModel.create (ModelId.create "example-model-small" |> expect) "Example Small" ]

/// A model exercising every UI element at once: connected, mid catch-up, one draft
/// (sendable), one queued message (editable/reorderable/deletable), a completed human
/// message, a streaming agent response, and a running agent turn.
let private representativeModel : ClientModel =
    let turnId = AgentTurnId.create "turn-ui" |> expect
    { Peer = { PeerId = ada; DisplayName = "swift-heron" }
      // Read through once, so the timeline it renders is the session's — and fetching news
      // again, for long enough to be worth saying, so the catch-up status and its offsets are
      // on screen for the checklist. A brief one is deliberately silent.
      Link = SessionLink.Fetching (EventOffset.create 7L |> expect, { Effort.fresh with Slow = true }, Phase.ReadThrough)
      Waited = true
      Said = None
      Showings = 0
      Session = Some sessionId
      TrustWaits = RepoTrustWaits.empty
      TrustCards = TrustCards.empty
      Launch = Launch.empty
      Repos = ReposProjection.empty
      // Connected, so the reconnect offer (Plan 11) is not showing — but the origin is
      // present, which is the interesting case: the offer must be gated on the CONNECTION,
      // not merely on whether a Manager is known.
      Manager = Some "http://127.0.0.1:8321"
      // Path-mounted unless a case says otherwise: the address survives a restart.
      EphemeralStorage = false
      // Served by a build that says which it is, so the sidebar's foot carries it.
      Build = Some "1.2.3-beta.4"
      // A representative client is one that kept what it was given; the case that cannot
      // keep anything is the exception, and says so where it matters.
      LocalCopy = LocalCopy.Kept None
      PageFault = None
      Synced =
        // Draft/queue bodies are rich-text `Y.XmlFragment`s mounted by the browser editor,
        // not fields on the model — so the SSR fixture carries only the slot's identity; the
        // checklist below asserts the mount *hosts* render (`data-rich-body`/`data-*-input`),
        // and the body-content rendering is a browser concern covered by the editor E2E.
        { Drafts = Map.ofList [ ada, { Author = ada; QueueId = draftQueueId } ]
          Queue = Map.ofList [ queueId, { QueueId = queueId; Author = ada; Order = 1.0 } ]
          Title = Ylmish.Text.ofString "planning the launch"
          SharedBrief = None
          // A terminal composer slot, and one queued command the AGENT wrote.
          TerminalDrafts =
            Map.ofList [ (terminalId, ada), { Terminal = terminalId; Author = ada; QueueId = terminalDraftQueueId } ]
          Pending =
            Map.ofList
                [ terminalQueueId,
                  { QueueId = terminalQueueId
                    Terminal = terminalId
                    // What the product actually writes for an agent command: the agent acts,
                    // on the turn human's authority. There is no other agent-shaped way to
                    // build one.
                    Authority = Authority.agentFor (Principal.Peer ada)
                    Order = 1.0
                    Description = None
                    Background = false
                    Stdin = false
                    // An agent command, so no viewport and no claim about width.
                    Size = None
                    ToolUseId = None } ]
          Model = Some pickedModel
          Chapters = Map.empty
          LaunchDismissed = false
          Attachments = Map.empty
          Surfaces = Map.empty }
      Conversation =
        ConversationProjection.ofItems
            [ { MessageId = MessageId.create "msg-1" |> expect
                Author = PeerRef ada
                Content = ItemContent.Message ("ship it")
                Status = Complete
                Offset = EventOffset.create 1L |> expect
                Woke = None; CausedBy = None }
              { MessageId = MessageId.create "msg-agent" |> expect
                Author = ActorRef.Agent
                Content = ItemContent.Message ("Sounds go")
                Status = Streaming
                Offset = EventOffset.create 4L |> expect
                Woke = None; CausedBy = None } ]
      // The terminal half of the chat (Plan 14): the fixture's one block, anchored between
      // the two messages — so the checklist renders a chip in the middle of the conversation
      // rather than only at the end, which is the ordering the merge exists for.
      Timeline =
        { TimelineProjection.empty with
            TerminalItems = [ TimelineBlock (EventOffset.create 2L |> expect, terminalId, blockId) ] }
      EventConsumer =
        { LastProcessedOffset = Some (EventOffset.create 5L |> expect)
          Reading = None }
      Agent = { ActiveTurn = Some turnId; Quiet = None; Interrupting = None }
      Presence =
        Map.ofList
            [ PeerRef bob,
              { DisplayName = "brave-owl"
                Focus = Some { Field = Title; Pos = { Anchor = "AQI="; Head = "AwQ=" } }
                Viewing = None } ]
      Caret = None
      // The roster names a draft's author even when they are not here: a label, never a peer id.
      Peers = Map.ofList [ ada, "swift-heron"; bob, "brave-owl" ]
      Here = Here.empty
      Attribution = Attribution.empty
      Composer = Unchosen
      Environment = EnvironmentNotStarted
      Terminals =
        { Terminals =
            [ { TerminalId = terminalId
                Title = (TerminalTitle.fromProse "build")
                OpenedBy = PeerRef ada
                Sandbox = Some SandboxRef.defaultRef
                Renewable = false
                IsOpen = true
                Closed = None
                Lease = None
                Lost = None
                Blocks =
                  [ { BlockId = blockId
                      QueueId = None
                      Authority = Authority.ofAuthor (Principal.Peer ada)
                      Command = "ls -la"
                      Description = None
                      Background = false
                      FromSeq = 0
                      ToSeq = Some 2
                      Status = BlockFinished (CommandSucceeded 0)
                      StoppedBy = None
                      StartedAt = System.DateTimeOffset (2026, 6, 14, 0, 0, 0, System.TimeSpan.Zero) } ]
                DroppedBytes = 0 } ] }
      // The transcript this client has: one coloured line, so the SSR render exercises the
      // ANSI path rather than only the plain one.
      TerminalFeeds =
        Map.ofList
            [ terminalId,
              { Records =
                  Map.ofList
                      [ 0, { At = 0.0; Kind = TranscriptInput; Data = "ls -la\n" }
                        1, { At = 0.1; Kind = TranscriptOutput; Data = "\u001b[32mtotal 0\u001b[0m\n" } ]
                KnownLength = 2
                ReadThrough = 2
                Header = Some { Width = 80; Height = 24; Timestamp = 0L }
                Unrecorded = false } ]
      TranscriptReads = Map.empty
      ReadsAsked = 0
      TerminalKeyframes = Map.empty
      KeyframesAsked = Set.empty
      TerminalScreens = Map.empty
      TerminalViewports = Map.empty
      // The terminal this client opened, as the events fold leaves it: the strip is `Tabs`
      // and nothing else, so a terminal with no tab here would be one this client cannot see.
      Tabs = [ terminalId ]
      FileTabs = []
      // Nothing pressed for and still owed: this client is looking, not mid-request.
      Opening = 0
      KillPending = None
      Pane = None
      TerminalsOpen = true
      PaneMemory = None
      PaneRemembered = false
      PaneOpensItself = false
      Seen = Map.empty
      Column = Column.initial
      PaneRoom = PaneRoom.unmeasured
      Readers = Map.empty
      ItemMenu = None
      StartIn = SandboxRef.defaultRef
      ChoosingStartIn = false
      Switcher = false
      ListHeld = Set.empty
      ClosedShown = false
      FilesOver = false
      Now = None
      Refused = None
      Stolen = None
      Asked = Map.empty
      OpenFolds = Set.empty
      DatedBreaks = Set.empty
      QueueDeleteArmed = None
      // Past the wait (`ClientModel.QueueShown`) already: this fixture is the checklist's
      // camera on the mount itself, not on the flash the wait exists to hide.
      QueueShown = Set.ofList [ queueId ]
      Copied = None
      Uploads = []
      // The pane shows a TAB by default; the list is what the cases below turn on.
      Claude =
        { Status =
            Some
                { SessionCredential = None
                  MineCredential = None
                  Owner = OwnedByUser
                  AgentAvailable = false
                  Models = ModelsLoaded offeredModels }
          Flow = ClaudeIdle
          Pending = Pending.Ready
          Scope = "mine"
          Code = ""
          Token = "" }
      GitHub =
        { Status = Some { SessionCredential = None; MineCredential = None; Owner = OwnedByUser }
          Flow = GitHubIdle
          Pending = Pending.Ready
          Scope = "mine"
          Token = ""
          Polling = PollWaiting 0 }
      // The generated read surface (Plan 15), with all three shapes declared at once, so
      // the acceptance render exercises the ONE renderer every future query goes through
      // rather than the one shape today's queries happen to use.
      Queries =
        { Declared =
            [ { Name = QueryName.create "repos" |> expect
                Title = "repos"
                Description = "the session's checkouts"
                Shape =
                  Rows
                      [ QueryColumn.create "repo" "repo"
                        QueryColumn.create "branch" "branch"
                        QueryColumn.create "checks" "checks"
                        QueryColumn.create "dirty" "uncommitted changes" ]
                Legend = [] }
              { Name = QueryName.create "work_environment" |> expect
                Title = "work environment"
                Description = "where commands run"
                Shape = Fields [ QueryColumn.create "backend" "backend"; QueryColumn.create "state" "state" ]
                // A query whose values are written in a vocabulary, so the rendered surface
                // this suite walks includes a legend — the acceptance render is where the
                // ONE renderer for every future query is exercised.
                Legend = [ "sock:PATH", "a unix socket" ] }
              // Declared but not yet answered: the surface must render a section for a
              // query whose first value has not arrived, or a slow query is an empty gap
              // rather than a thing that is loading.
              { Name = QueryName.create "leases" |> expect
                Title = "leases"
                Description = "devices leased to this session"
                Shape = Value
                Legend = [] } ]
          Values =
            Map.ofList
                [ "repos",
                  RowsOf
                      [ [ "repo", CellText "octo/hello"
                          "branch", CellText "main"
                          // A cell that carries a verdict about itself, so the render
                          // exercises the toned path as well as the plain one.
                          "checks", CellStatus ("checks red", ToneBad)
                          "dirty", CellFlag true ] ]
                  "work_environment",
                  FieldsOf [ "backend", CellText "srt"; "state", CellStatus ("running", ToneBusy) ] ] } }

/// The representative model with its Claude panel adjusted.
///
/// A panel is an OPTION now — the read stream may not have said anything yet — and every
/// case below is about one that HAS arrived, so they say so through here rather than each
/// spelling the `Option.map` again.
let private withClaudePanel (change: ClaudePanel -> ClaudePanel) (model: ClientModel) : ClientModel =
    { model with Claude = { model.Claude with Status = model.Claude.Status |> Option.map change } }

/// The same for the GitHub panel.
let private withGitHubPanel (change: GitHubPanel -> GitHubPanel) (model: ClientModel) : ClientModel =
    { model with GitHub = { model.GitHub with Status = model.GitHub.Status |> Option.map change } }

/// A session with watched pull requests, as the `pull_requests` query reports them — the
/// only shape a browser has of them, and so the only shape the status block can read.
let private withPullRequests (rows: (string * QueryCell) list list) : ClientModel =
    { representativeModel with
        Queries =
            { representativeModel.Queries with
                Values = representativeModel.Queries.Values |> Map.add PrStatus.Columns.query (RowsOf rows) } }

let private prRow (number: int) (state: string) (status: QueryCell) : (string * QueryCell) list =
    [ PrStatus.Columns.pr, CellText (sprintf "octo/hello#%d" number)
      PrStatus.Columns.state, CellStatus (state, ToneMuted)
      PrStatus.Columns.status, status ]

/// A session divided into chapters at some of its timeline. The verdicts are what a rule
/// reads, so this is the only shape it needs.
let private withChapters (ids: string list) : ClientModel =
    { representativeModel with
        Synced =
            { representativeModel.Synced with
                Chapters =
                    ids
                    |> List.map (fun id ->
                        MessageId.create id |> expect, { Opens = true; Name = Ylmish.Text.empty })
                    |> Map.ofList } }

/// The representative terminal, with its one fixture block replaced by whatever a case
/// wants to assert the pane's grouping fold against (`terminalBlockRun`) — and a transcript
/// in which each of them printed more than the pane draws whole, so every block but the
/// newest is earlier history and only who ran what decides the grouping.
let private runModelWith (blocks: Block list) : ClientModel =
    let overflowing =
        { Records =
            Map.ofList [ 0, { At = 0.0; Kind = TranscriptOutput; Data = String.replicate (BlockGroup.unfoldedLines + 1) "line\n" } ]
          KnownLength = 1
          ReadThrough = 1
          Header = None
          Unrecorded = false }
    { representativeModel with
        TerminalFeeds = Map.add terminalId overflowing representativeModel.TerminalFeeds
        Terminals =
            { representativeModel.Terminals with
                Terminals =
                    representativeModel.Terminals.Terminals
                    |> List.map (fun t ->
                        if t.TerminalId = terminalId then { t with Blocks = blocks } else t) } }

/// One block for a grouping case: `n` only tells its command and its id apart from its
/// run-mates, never its order — `FromSeq`/`ToSeq` point every block at the one long record
/// `runModelWith` gives the terminal.
let private runBlock (authority: Authority) (n: int) (status: BlockStatus) : Block =
    { BlockId = BlockId.create (sprintf "block-run-%d" n) |> expect
      QueueId = None
      Authority = authority
      Command = sprintf "step %d" n
      Description = None
      Background = false
      FromSeq = 0
      ToSeq = Some 1
      Status = status
      StoppedBy = None
      StartedAt = System.DateTimeOffset (2026, 6, 14, 0, 0, 0, System.TimeSpan.Zero) }

/// The representative session holding these terminals instead of its one, each a copy of the
/// fixture's terminal under another id, its last command `last` (`None`: never ran one), and
/// open or not — the shapes the sidebar's environment and the pane's marks are read from.
let private withTerminals (terminals: (string * BlockStatus option * bool) list) : ClientModel =
    let template = List.head representativeModel.Terminals.Terminals
    let view (id: string, last: BlockStatus option, isOpen: bool) =
        let terminal = TerminalId.create id |> expect
        { template with
            TerminalId = terminal
            Title = TerminalTitle.fromProse id
            IsOpen = isOpen
            Closed = (if isOpen then None else Some { TerminalId = terminal; Reason = "closed by a peer"; By = Some (PeerRef bob) })
            Blocks = last |> Option.map (runBlock (Authority.ofAuthor (Principal.Peer ada)) 1) |> Option.toList }
    let views = List.map view terminals
    { representativeModel with
        Terminals = { representativeModel.Terminals with Terminals = views }
        // Each one looked at since it last finished something, so its mark is its state and
        // not news (`ClientModel.unseen`): the cases here are about what a terminal is doing.
        Seen = views |> List.map (fun v -> v.TerminalId, Codecs.CommandTally.ofView v) |> Map.ofList }

/// The markup of one terminal's entry under the sidebar's environment, if it is listed: from
/// its hook to the end of its button, so what a case reads off it is that entry's and not the
/// same terminal's tab or row elsewhere on the page.
let private environmentEntry (id: string) (html: string) : string option =
    match html.IndexOf (Dom.attr Dom.Hooks.environmentTerminal id) with
    | -1 -> None
    | at -> Some (html.Substring (at, html.IndexOf ("</button>", at) - at))

/// The opening tag of the element that wears an attribute, if one does: so what a case reads off
/// it is that element's and not the same words elsewhere on the page.
let private openingTagWith (attribute: string) (html: string) : string option =
    match html.IndexOf attribute with
    | -1 -> None
    | at ->
        let opens = html.LastIndexOf ('<', at)
        Some (html.Substring (opens, html.IndexOf ('>', at) - opens + 1))

/// How many times a hook appears in a rendered page. Counting MOUNTS rather than words: what
/// these cases promise is one control per item and one stroke per mark, and a substring of
/// somebody's prose is not either.
let private occurrences (needle: string) (haystack: string) : int =
    (haystack.Split ([| needle |], System.StringSplitOptions.None)).Length - 1

/// A turn that has started and not yet said a word. The moment a phone spends the longest on,
/// and the one the old activity strip was worst at: an author line, the word `streaming`, an
/// empty body under it, and a 48px band below saying the same thing a third time.
let private silentTurnModel : ClientModel =
    { representativeModel with
        Conversation =
            { representativeModel.Conversation with
                Recent =
                    representativeModel.Conversation.Recent
                    |> List.map (fun item -> if item.Status = Streaming then { item with Content = ItemContent.Message "" } else item) } }

/// Nothing running: the last turn finished and nobody has asked for another.
let private restingModel : ClientModel =
    { representativeModel with
        Agent = { ActiveTurn = None; Quiet = None; Interrupting = None }
        Conversation =
            { representativeModel.Conversation with
                Recent = representativeModel.Conversation.Recent |> List.map (fun item -> { item with Status = Complete })
                ActiveAgentMessages = Map.empty } }

/// The composer when a PEER is the one writing: their draft is what you are in, yours (if any)
/// is a summary you can open, and "new message" is the way out of collaborating.
let private joinedComposerModel : ClientModel =
    { representativeModel with
        Synced =
            { representativeModel.Synced with
                Drafts =
                    Map.ofList
                        [ ada, { Author = ada; QueueId = draftQueueId }
                          bob, { Author = bob; QueueId = QueueId.create "queue-ui-bob" |> expect } ] }
        // Bob is writing, and his caret is in his own draft — the activity the summary shows.
        Presence =
            Map.ofList
                [ PeerRef bob,
                  { DisplayName = "brave-owl"
                    Focus = Some { Field = DraftBody bob; Pos = { Anchor = "AQI="; Head = "AQI=" } }
                    Viewing = None } ]
        Composer = Joined bob }

/// The same session with bob typing in the terminal (Plan 13, stage 2e) — live mode as every
/// OTHER peer sees it, which is the case the lease bar exists for.
let private leasedTerminalModel : ClientModel =
    { representativeModel with
        Terminals =
            { Terminals =
                representativeModel.Terminals.Terminals
                |> List.map (fun t -> { t with Lease = Some (PeerRef bob) }) }
        // The screen this client composed from the Process's snapshot and the records since
        // (Plan 14, stage 6). Coloured, so the render exercises the ANSI path.
        TerminalScreens = Map.ofList [ terminalId, { LiveScreen.Text = "\u001b[32mvim ~/notes\u001b[0m"; LiveScreen.Cursor = None } ] }

/// The same terminal, held by THIS peer: the one copy of the screen that takes keystrokes.
let private heldTerminalModel : ClientModel =
    { leasedTerminalModel with
        Terminals =
            { Terminals =
                leasedTerminalModel.Terminals.Terminals
                |> List.map (fun t -> { t with Lease = Some (PeerRef ada) }) } }

/// The accessible name of THIS peer's send control in a rendered page — what its own command
/// line's button says pressing it will do. Empty when there is no such control, so a case
/// asserting a name also fails when the line is gone.
let private sendName (html: string) : string =
    let tag =
        System.Text.RegularExpressions.Regex.Match(
            html, sprintf "<button[^>]*%s[^>]*>" (Dom.attr Dom.Hooks.terminalSend (PeerId.value ada))).Value
    System.Text.RegularExpressions.Regex.Match(tag, "aria-label=\"([^\"]*)\"").Groups.[1].Value

/// The same session with the terminal's shell no longer marking (Plan 13, stage 2f). The
/// queued command is a PEER's.
let private lostIntegrationModel : ClientModel =
    { representativeModel with
        Synced =
            { representativeModel.Synced with
                Pending =
                    representativeModel.Synced.Pending
                    |> Map.map (fun _ entry -> { entry with Authority = Authority.ofAuthor (Principal.Peer ada) }) }
        Terminals =
            { Terminals =
                representativeModel.Terminals.Terminals
                |> List.map (fun t ->
                    { t with Lost = Some { TerminalId = t.TerminalId; BlockId = None; Evidence = None; Refused = [] } }) } }

/// The same session after the terminal has closed (Plan 13, stage 3e) — the audit read. The
/// choice is explicit because that is the real flow: the panel LANDS on a live terminal, and
/// a closed one is somewhere you go on purpose.
let private closedTerminalModel : ClientModel =
    { representativeModel with
        Terminals =
            { Terminals =
                representativeModel.Terminals.Terminals
                |> List.map (fun t -> { t with IsOpen = false; Closed = Some { TerminalId = t.TerminalId; Reason = "closed by a peer"; By = Some (PeerRef bob) } }) }
        Pane = Some (OnTerminal (Reading terminalId)) }

/// A closed terminal whose bytes came from a provider that said its stream can be asked for
/// again (Plan 19, step 4) — the one case where a closed terminal has a way back.
let private renewableTerminalModel : ClientModel =
    { closedTerminalModel with
        Terminals =
            { Terminals =
                closedTerminalModel.Terminals.Terminals
                |> List.map (fun t -> { t with Sandbox = None; Renewable = true }) } }

/// …and after the per-terminal output cap ate its recording (stage 3d): the blocks survive
/// in the projection, the transcript does not, and the byte count is the only trace of what
/// it held.
///
/// The store has SAID so — its answer to a read from line 0 was nothing. This fixture used
/// to be an empty feed, which is also what a reader holds who has not been answered yet, and
/// a surface that called that "lost" told every late viewer their recordings were gone.
let private forgottenTerminalModel : ClientModel =
    { closedTerminalModel with
        Terminals =
            { Terminals =
                closedTerminalModel.Terminals.Terminals |> List.map (fun t -> { t with DroppedBytes = 4096 }) }
        TerminalFeeds = Map.ofList [ terminalId, { TerminalFeed.empty with Unrecorded = true } ] }

/// The buttons in a rendered page that a screen reader would announce as nothing but
/// "button": no text between the tags once markup is stripped, and no `aria-label` /
/// `aria-labelledby` on the tag. Returns their open tags, so a failure names the offender.
///
/// Hand-scanned rather than matched with a `Regex`, because this file runs on BOTH runtimes
/// and string indexing is the one thing that behaves identically on each.
let private namelessButtons (html: string) : string list =
    let visibleText (inner: string) =
        // What is left after every tag and comment: an inline SVG contributes nothing, which
        // is exactly the case this test exists to catch. Angle brackets nest (a self-closing
        // `<path/>` opens and closes; so does a `<!--lit-part-->` marker), so depth-count
        // rather than assume one level.
        let mutable depth = 0
        let kept = System.Text.StringBuilder ()
        for ch in inner do
            if ch = '<' then depth <- depth + 1
            elif ch = '>' then depth <- max 0 (depth - 1)
            elif depth = 0 then kept.Append ch |> ignore
        kept.ToString().Trim ()

    let rec scan (from: int) (found: string list) =
        let start = html.IndexOf ("<button", from)
        if start < 0 then List.rev found
        else
            let openEnd = html.IndexOf (">", start)
            let closeAt = html.IndexOf ("</button>", start)
            if openEnd < 0 || closeAt < 0 || closeAt < openEnd then List.rev found
            else
                let tag = html.Substring (start, openEnd - start)
                let named =
                    tag.Contains "aria-label=" || tag.Contains "aria-labelledby="
                    || visibleText (html.Substring (openEnd + 1, closeAt - openEnd - 1)) <> ""
                scan (closeAt + 9) (if named then found else tag :: found)
    scan 0 []

/// A timeline holding one act still running, done by `author`.
let private renderRunningBy (author: ActorRef) : string =
    let running : ConversationItem =
        { MessageId = MessageId.create "msg-running-by" |> expect
          Author = author
          Content =
            ItemContent.Act (
                Act.SandboxStarting
                    { MessageId = MessageId.create "msg-running-by" |> expect
                      Sandbox = SandboxRef.defaultRef
                      Backend = "srt"
                      Description = None
                      Actor = author; OnBehalfOf = None; CausedBy = None })
          Status = ConversationItemStatus.Running
          Offset = EventOffset.create 1L |> expect
          Woke = None; CausedBy = None }
    Support.render { representativeModel with Conversation = { representativeModel.Conversation with Recent = List.rev ([ running ]) } }

/// What the theme paints the fixtures' green (SGR 32) as: the classes a themed run wears, named
/// through `Style` so that no case here knows their Tailwind string. A run the theme gave NO class
/// would be painted by the escape, or not at all, and every `Contains` of an empty string is true,
/// so that is refused here rather than passed.
let private themedGreen () : string =
    match Style.ansiClasses { AnsiStyle.plain with Foreground = IndexedColour 2 } with
    | "" -> failwith "Style.ansiClasses gives SGR 32 no class: a green run would wear no theme token"
    | classes -> classes

/// The representative session with nothing in it — no conversation, no terminal items, nothing
/// queued (a command waiting to run is something ON the screen) — standing at `sync`.
/// Opening, fetching towards the end the session named, and — `slow` — waited on long enough
/// to say so.
let private fetchingOpening (slow: bool) : SessionLink =
    SessionLink.Fetching (EventOffset.create 7L |> expect, { Effort.fresh with Slow = slow }, Phase.Opening)

let private emptyAt (link: SessionLink) : ClientModel =
    { representativeModel with
        Conversation = ConversationProjection.empty
        Timeline = TimelineProjection.empty
        Synced = { representativeModel.Synced with Pending = Map.empty }
        // Nothing on screen because nothing was kept: the store answered with nothing.
        LocalCopy = LocalCopy.NoneKept
        Link = link }

let private uiChecklistTests =
    testList "UI checklist" [
        // Pinned ONCE, over the whole shell, rather than remembered at each control: the
        // verbs at the edge of a field are glyphs now (run, send, discard, the strip's `+`),
        // which is the right shape for a verb you meet mid-sentence and the wrong shape for
        // anyone who cannot see it. A label that is a picture is not a label, and the failure
        // is silent — the control still works, still takes focus, and still announces itself
        // as "button", saying nothing about which one.
        testCase "no control is announced as nothing but \"button\"" <| fun () ->
            let offenders =
                [ representativeModel; joinedComposerModel; leasedTerminalModel; closedTerminalModel ]
                |> List.collect (Support.render >> namelessButtons)
            Expect.equal
                offenders
                []
                (sprintf "every button needs a name, from its text or aria-label — these have neither: %s"
                    (String.concat " | " offenders))

        // The promise, not the design: one card per repo somebody here has to trust, and none
        // for a repo somebody already trusts. What it SAYS is a design and will move; that a
        // waiting repo is offered exactly once is what a person is owed.
        testCase "a repo waiting for trust is offered exactly once, and one that is trusted is not offered" <| fun () ->
            let waiting = RepoRef.create "octo/hello" |> expect
            let settled = RepoRef.create "octo/quiet" |> expect
            let asked repo =
                SessionEvent.RepoTrustAsked
                    { RepoTrustAsked.MessageId = MessageId.create (RepoRef.value repo) |> expect
                      RepoTrustAsked.Repo = repo
                      RepoTrustAsked.Routes = [ ConnectionRoute.Git; ConnectionRoute.Api ]
                      RepoTrustAsked.Actor = ActorRef.Configured repo
                      RepoTrustAsked.CausedBy = None }
            let ada = Principal.User (UserId.create "ada" |> expect)
            let trusted =
                SessionEvent.RepoTrusted
                    { RepoTrusted.MessageId = MessageId.create "trusted" |> expect
                      RepoTrusted.Repo = settled
                      RepoTrusted.By = ada
                      RepoTrusted.Actor = Principal.toActor ada }
            let model =
                { representativeModel with
                    TrustWaits = RepoTrustWaits.apply RepoTrustWaits.empty [ asked waiting; asked settled; trusted ] }
            let html = Support.render model
            // Counted rather than matched, so the assertion is about how MANY there are —
            // which is the promise — and not about any of the words around them.
            let occurrences (needle: string) =
                let rec count (from: int) (found: int) =
                    match html.IndexOf (needle, from) with
                    | -1 -> found
                    | at -> count (at + needle.Length) (found + 1)
                count 0 0
            Expect.equal
                (occurrences (Dom.attr Dom.Hooks.trustCard "octo/hello"),
                 occurrences (Dom.attr Dom.Hooks.trustCard "octo/quiet"),
                 occurrences (Dom.attr Dom.Hooks.trustAnswer "octo/hello"))
                (1, 0, 1)
                "one card for the repo that waits, with one way to trust it, and none for the one somebody trusts"

        // Putting a card off decides nothing and is this reader's alone: the card goes, and it
        // comes back when the repo asks for something it did not ask before.
        testCase "a card put off is not offered again until the repo asks for something new" <| fun () ->
            let repo = RepoRef.create "octo/hello" |> expect
            let asked routes =
                SessionEvent.RepoTrustAsked
                    { RepoTrustAsked.MessageId = MessageId.create "ask" |> expect
                      RepoTrustAsked.Repo = repo
                      RepoTrustAsked.Routes = routes
                      RepoTrustAsked.Actor = ActorRef.Configured repo
                      RepoTrustAsked.CausedBy = None }
            let putOff = TrustCards.putOff repo [ ConnectionRoute.Git ] TrustCards.empty
            let shownWhen routes =
                TrustCards.showing (RepoTrustWaits.pending [ asked routes ]) putOff |> List.length
            Expect.equal
                (shownWhen [ ConnectionRoute.Git ], shownWhen [ ConnectionRoute.Git; ConnectionRoute.Api ])
                (0, 1)
                "gone while it asks the same, back when it asks for more"

        // A glossary is consulted, not read: it is offered above what it explains and shut
        // until somebody wants it, so a panel of answers is not a wall of vocabulary. Every mount
        // is checked at once because it is ONE promise about every legend there is — and a
        // `<details>` is what keeps it the browser's disclosure rather than ours. (A trust card
        // asks in words rather than in the grant vocabulary, so it has no legend to offer.)
        testCase "every legend is offered without taking the surface" <| fun () ->
            let model = representativeModel
            let html = Support.render model
            // The tag each legend hook sits in: back to its `<`, forward to its `>`.
            let rec tags (from: int) (found: string list) =
                match html.IndexOf (Dom.Hooks.legend, from) with
                | -1 -> List.rev found
                | at ->
                    let opened = html.LastIndexOf ('<', at)
                    let closed = html.IndexOf ('>', at)
                    tags (closed + 1) (html.Substring (opened, closed - opened) :: found)
            let legends = tags 0 []
            Expect.isNonEmpty legends "the panel's, so the promise is not vacuous"
            for tag in legends do
                Expect.isTrue (tag.StartsWith "<details") (sprintf "a legend is a disclosure, this one is %s" tag)
                Expect.isFalse (tag.Contains " open") (sprintf "and it is shut, this one is %s" tag)

        testCase "every required Phase 1 UI element renders from the model" <| fun () ->
            let html = Support.render representativeModel
            let required =
                [ "session connection status", Dom.Hooks.connection
                  "connection state value", Dom.attr Dom.Hooks.connection Dom.Text.connected
                  "editable session title", Dom.Hooks.sessionTitle
                  "title body", "planning the launch"
                  "session id secondary identifier", Dom.hookText Dom.Hooks.sessionId "demo-session"
                  "remote collaborator cursor", Dom.attr Dom.Hooks.cursorPeer (ActorRef.token (PeerRef bob))
                  "remote cursor peer label", "brave-owl"
                  "peer display name", Dom.hookText Dom.Hooks.displayName "swift-heron"
                  "collaborative draft editor", Dom.Hooks.draftEditor
                  "draft editor mount host", Dom.attr Dom.Hooks.draftInput "ada"
                  "send button", Dom.attr Dom.Hooks.sendDraft "ada"
                  "conversation timeline", Dom.Hooks.conversation
                  // The sent body is rendered as formatted rich text (a paragraph here), not
                  // raw markdown text sitting directly under the hook.
                  "sent message in timeline", ">ship it</p>"
                  "agent streaming response", Dom.attr Dom.Hooks.messageStatus Dom.Text.streaming
                  "agent stream indicator", Dom.Hooks.agentStream
                  "a way to stop the turn that is running", Dom.attr Dom.Hooks.interruptTurn "turn-ui"
                  // A catch-up worth waiting on, with how far it has got of how much: offsets
                  // count from zero, so five folded of seven is six of eight.
                  "catch-up progress", Dom.Hooks.catchUpBar
                  "how far the catch-up has got", "aria-valuenow=\"6\""
                  "of how much", "aria-valuemax=\"8\""
                  "environment status (Phase 2)", Dom.Hooks.environment
                  "message queue (Phase 3)", Dom.Hooks.messageQueue
                  "queued message editor", Dom.attr Dom.Hooks.queueInput "queue-ui"
                  "queue reorder up", Dom.attr Dom.Hooks.queueUp "queue-ui"
                  "queue reorder down", Dom.attr Dom.Hooks.queueDown "queue-ui"
                  "queue delete", Dom.attr Dom.Hooks.queueDelete "queue-ui"
                  // The content pane (Plan 13): the column, the terminal it is showing, the
                  // block that ran with its exit status, and the composer that queues the next
                  // command.
                  "content panel", Dom.Hooks.contentPanel
                  "terminal tab", Dom.attr Dom.Hooks.terminalTab "term-ui"
                  "the list's door", Dom.Hooks.paneSwitcher
                  "terminal block", Dom.attr Dom.Hooks.terminalBlock "block-ui"
                  "terminal block status", Dom.attr Dom.Hooks.terminalBlockStatus Dom.Text.blockOk
                  "terminal block command", "ls -la"
                  "terminal output", Dom.Hooks.terminalOutput
                  // The output is ANSI-coloured, and the colour is a THEME token — a raw
                  // ANSI colour would not clear the contrast floor on this ground.
                  "terminal output colour is a theme token", themedGreen ()
                  "terminal output text", "total 0"
                  "queued terminal command", Dom.attr Dom.Hooks.terminalQueued "queue-ui-term"
                  "terminal composer input", Dom.attr Dom.Hooks.terminalInput "term-draft:term-ui:ada"
                  // Terminal work in the CHAT (Plan 14): the block that ran has a chip where
                  // it ran, carrying who ran it and how it went — and no output, which is the
                  // whole reason it is a chip.
                  "block chip in the chat", Dom.attr Dom.Hooks.chatBlock "block-ui"
                  "chip carries the block's status", Dom.attr Dom.Hooks.chatBlockStatus Dom.Text.blockOk
                  // Settings + agent presence (Plan 08 pass): the model has no agent, so
                  // the sidebar row says absent and carries the verb that connects one,
                  // and the drawer holds the Claude panel.
                  "settings drawer toggle", Dom.Hooks.settingsToggle
                  "settings drawer panel", Dom.Hooks.settingsPanel
                  "claude panel in settings", Dom.Hooks.claudePanel
                  // The model picker, showing the session's choice: the CONTROL's own
                  // attribute, so what a person reads and what the register holds are one
                  // thing rather than two that could disagree.
                  "model picker", Dom.attr Dom.Hooks.modelSelect (ModelId.value pickedModel)
                  "agent presence row (absent)", Dom.attr Dom.Hooks.agentPresence "absent"
                  "no-agent marker on the agent's row", Dom.Hooks.noAgent
                  "no-agent connect verb", Dom.Hooks.noAgentConnect ]
            for label, marker in required do
                Expect.isTrue (html.Contains marker) (sprintf "%s (`%s`) must render" label marker)

        // --- a credential that stopped working ------------------------------------------
        // Three surfaces, one fact. Each is asserted where it renders rather than against
        // the whole page, because a whole-page render contains every surface at once and a
        // bare `Contains` would be satisfied by whichever one happened to be right.

        /// The representative client, with one connection needing a sign-in.
        let private' (provider: string) (reason: string) =
            let needing = Some { Kind = StaticConnection; SignInRequired = Some reason }
            match provider with
            | "claude" ->
                representativeModel
                |> withClaudePanel (fun panel -> { panel with MineCredential = needing; AgentAvailable = true })
            | _ -> representativeModel |> withGitHubPanel (fun panel -> { panel with MineCredential = needing })

        // The one derivation every surface reads, so they cannot disagree about whether
        // anything is wrong. Ordered, not a map's iteration: what the prompt names first
        // must not change between renders of an unchanged model.
        testCase "what needs signing in is derived once, in a settled order" <| fun () ->
            let needing reason = Some { Kind = StaticConnection; SignInRequired = Some reason }
            let both =
                representativeModel
                |> withClaudePanel (fun panel -> { panel with MineCredential = needing "claude said no" })
                |> withGitHubPanel (fun panel -> { panel with MineCredential = needing "github said no" })
            Expect.equal
                (ClientModel.signInRequired both)
                [ "claude", "claude said no"; "github", "github said no" ]
                "both, and always in this order"
            Expect.isEmpty
                (ClientModel.signInRequired representativeModel)
                "and nothing at all when every connection is fine"

        testCase "a panel row for a credential that stopped working says so, and says why" <| fun () ->
            let html = Support.render (private' "github" "github rejected this credential")
            Expect.isTrue
                (html.Contains (Dom.attr Dom.Hooks.githubSignInRequired "mine"))
                "the row carries the fault on the scope it is held under"
            Expect.isTrue
                (html.Contains "github rejected this credential")
                "with the provider's own words, which a person could not have guessed"

        testCase "a healthy credential's row says nothing about signing in" <| fun () ->
            let healthy =
                representativeModel
                |> withGitHubPanel (fun panel ->
                    { panel with MineCredential = Some { Kind = OAuthConnection; SignInRequired = None } })
            let html = Support.render healthy
            Expect.isTrue (html.Contains "data-github-connected=\"mine\"") "it is still shown as connected"
            Expect.isFalse (html.Contains Dom.Hooks.githubSignInRequired) "and nothing asks for a sign-in"
            Expect.isFalse (html.Contains Dom.Hooks.signInRequired) "so no prompt over the timeline either"

        // Connecting the scope the selector already names makes no sense while a credential
        // is sitting there: the only remedy for THAT scope is the disconnect already on its
        // row, and that row already carries the status and the X, so the control under the
        // selector says nothing at all rather than repeating it in prose.
        testCase "the connect controls hide for a scope that already has a credential" <| fun () ->
            let connected =
                representativeModel
                |> withGitHubPanel (fun panel ->
                    { panel with MineCredential = Some { Kind = OAuthConnection; SignInRequired = None } })
            let html = Support.render connected
            Expect.isFalse (html.Contains "data-github-scope") "the selector itself is gone (mine, the default, already has a credential)"
            Expect.isFalse (html.Contains "data-github-connect>") "and offers no second way to connect it"
            Expect.isFalse (html.Contains "data-github-token ") "nor its token fallback"

        // The other scope is untouched, so switching to it must still offer to connect —
        // this is about the scope the selector names, not about the panel as a whole.
        testCase "a scope the selector does not name still offers to connect" <| fun () ->
            let connectedMine =
                representativeModel
                |> withGitHubPanel (fun panel ->
                    { panel with MineCredential = Some { Kind = OAuthConnection; SignInRequired = None } })
            let choosingSession =
                { connectedMine with GitHub = { connectedMine.GitHub with Scope = "session" } }
            let html = Support.render choosingSession
            Expect.stringContains html "data-github-connect>" "session has no credential yet, so it can be connected"
            Expect.isFalse (html.Contains "data-github-already-connected") "and nothing says otherwise"

        // Signing in is the way in; the pasted token is the fallback for where that flow
        // cannot reach. It sits behind its own disclosure rather than beside the button that
        // starts the flow, so the panel's default shape offers one way in, not two.
        testCase "the pasted-token fallback sits behind its own disclosure, below the connect button" <| fun () ->
            let html = Support.render representativeModel
            Expect.stringContains html "data-github-token-form" "the token fallback is a disclosure of its own"
            Expect.stringContains html Dom.Text.useTokenInstead "named for what it opens"
            let connectAt = html.IndexOf "data-github-connect>"
            let formAt = html.IndexOf "data-github-token-form"
            Expect.isTrue (connectAt >= 0 && formAt > connectAt) "the connect button leads, the fallback follows"

        // --- the device code, and getting it to github.com --------------------------------
        // The code is useless where it is rendered: it has to reach a form in another tab,
        // and on a phone that is a paste or it is eight characters carried in somebody's
        // head. So the promise is that the code can be TAKEN, and that taking it is answered
        // — never which glyph the control wears, or how long the answer stays up.

        /// The client mid device flow, with a code on screen waiting to be approved.
        let awaitingApproval =
            { representativeModel with
                GitHub =
                    { representativeModel.GitHub with
                        Flow = GitHubAwaitingApproval ("049A-EBB0", "https://github.com/login/device", "mine", 5) } }

        /// What the element carrying `hook` says — its own text, so an assertion about the
        /// code's box cannot be satisfied by the same word rendered anywhere else on the page.
        let textOf (hook: string) (html: string) : string =
            let at = html.IndexOf hook
            Expect.isTrue (at >= 0) (sprintf "`%s` is in the document" hook)
            let opened = html.IndexOf ('>', at)
            html.Substring (opened + 1, html.IndexOf ('<', opened) - opened - 1)

        testCase "the device code is offered with a way to take it" <| fun () ->
            let html = Support.render awaitingApproval
            Expect.equal (textOf Dom.Hooks.githubUserCode html) "049A-EBB0" "the code is on screen, readable"
            let at = html.IndexOf Dom.Hooks.githubCopyCode
            Expect.isTrue (at >= 0) "and a control offers to copy it"
            // A real button, with a name: an icon-only control that is a div with a handler
            // is operable by nobody using a keyboard, and announced to nobody at all.
            let start = html.LastIndexOf ("<", at)
            let tag = html.Substring (start, html.IndexOf ('>', at) - start)
            Expect.isTrue (tag.StartsWith "<button") (sprintf "the control is a button: %s" tag)
            Expect.isTrue (tag.Contains "aria-label=") (sprintf "and it has an accessible name: %s" tag)

        testCase "a copy is answered in the box the value came from" <| fun () ->
            let html = Support.render { awaitingApproval with Copied = Some { Copy.Box = Dom.Hooks.githubUserCode; Copy.Nth = 1 } }
            Expect.equal
                (textOf Dom.Hooks.githubUserCode html)
                Dom.Text.copied
                "the confirmation lands where the reader is already looking"

        testCase "a copy of something else says nothing about the device code" <| fun () ->
            // ONE slot holds what was copied, so the mark is keyed by the box it belongs to.
            // Without that key every copyable thing on the page would confirm at once, and
            // three of them would be lying.
            let html = Support.render { awaitingApproval with Copied = Some { Copy.Box = "data-some-other-box"; Copy.Nth = 1 } }
            Expect.equal (textOf Dom.Hooks.githubUserCode html) "049A-EBB0" "its box still holds the code"
            Expect.isFalse (html.Contains (">" + Dom.Text.copied + "<")) "and nothing here claims to have been copied"

        // The roster used to read "ready" in green over a Claude credential the next turn
        // would fail on, because the agent gate asks whether a credential is STORED and not
        // whether it still works.
        testCase "the agent's row follows the credential's health, not merely its presence" <| fun () ->
            let html = Support.render (private' "claude" "the refresh token has expired")
            let row = html.Substring (html.IndexOf (Dom.attr Dom.Hooks.agentPresence "live"))
            let row = row.Substring (0, row.IndexOf "</div>")
            Expect.isTrue (row.Contains Dom.Text.signInAgainStatus) "the agent's own row says what is needed"
            Expect.isFalse (row.Contains ">ready<") "and no longer claims to be ready"

        testCase "a credential that stopped working is offered a way to fix it, over the timeline" <| fun () ->
            let html = Support.render (private' "github" "github rejected this credential")
            Expect.isTrue
                (html.Contains (Dom.attr Dom.Hooks.signInRequired "github"))
                "the prompt names which connection needs it"
            Expect.isTrue (html.Contains Dom.Hooks.signInAgain) "and carries the button that leads to the fix"
            Expect.isTrue (html.Contains Dom.Text.signInAgain) "in words"
            // Over the timeline, not under it: this is a notice, and a notice below what it
            // is about is one somebody scrolls past.
            Expect.isTrue
                (html.IndexOf Dom.Hooks.signInRequired < html.IndexOf Dom.Hooks.conversation)
                "it sits above the conversation"

        // The rule `Style.fs`'s noAgent block and the case below it already encode: a call to
        // action repeated is wallpaper. The panel row and the roster row say the same fact as
        // STATUSES, and exactly one button in the document offers the remedy.
        testCase "the way to fix it is offered exactly once" <| fun () ->
            let html = Support.render (private' "claude" "the refresh token has expired")
            let occurrences (needle: string) = (html.Split needle |> Array.length) - 1
            Expect.equal (occurrences Dom.Hooks.signInAgain) 1 "one button, not one per surface that mentions it"

        // Two dead credentials are still one instruction, and the panel it opens shows both.
        // A panel offers a SHARED scope, and on a deployment that attributes nobody that
        // scope is everyone who can reach the Manager. Both panels have to say which, and
        // the GitHub one used to say "all my sessions" outright — a promise about privacy
        // that `--auth localhost` cannot keep. It was only writable that way while the
        // owner was a string a renderer was under no obligation to read.
        testCase "a shared scope owned by the deployment is not called mine, on either panel" <| fun () ->
            let shared =
                representativeModel
                |> withClaudePanel (fun panel -> { panel with Owner = OwnedByDeployment })
                |> withGitHubPanel (fun panel -> { panel with Owner = OwnedByDeployment })
                |> Support.render
            Expect.isFalse
                (shared.Contains "all my sessions" || shared.Contains "All my sessions")
                "nothing claims as one person's what the whole deployment shares"
            Expect.isTrue (shared.Contains "All sessions") "it is offered as what it is"

        testCase "a shared scope owned by one person is called theirs, on either panel" <| fun () ->
            let connected = Some { Kind = StaticConnection; SignInRequired = None }
            let mine =
                representativeModel
                |> withClaudePanel (fun panel -> { panel with Owner = OwnedByUser; MineCredential = connected })
                |> withGitHubPanel (fun panel -> { panel with Owner = OwnedByUser; MineCredential = connected })
                |> Support.render
            // Twice: the Claude section's connected row and the GitHub one, which read the
            // same answer the same way now. The count is the point — one of them used to
            // assert it instead of asking.
            Expect.equal
                ((mine.Split "all my sessions" |> Array.length) - 1)
                2
                "both panels label the connected row from the owner they were told"

        testCase "two credentials needing a sign-in are still one prompt" <| fun () ->
            let needing reason = Some { Kind = StaticConnection; SignInRequired = Some reason }
            let both =
                representativeModel
                |> withClaudePanel (fun panel -> { panel with MineCredential = needing "claude said no" })
                |> withGitHubPanel (fun panel -> { panel with MineCredential = needing "github said no" })
            let html = Support.render both
            let occurrences (needle: string) = (html.Split needle |> Array.length) - 1
            Expect.equal (occurrences Dom.Hooks.signInRequired) 1 "one prompt"
            Expect.equal (occurrences Dom.Hooks.signInAgain) 1 "one button"

        // Signing in runs against the session. Offering it to somebody who cannot reach the
        // session is offering a button that cannot work — and the degraded strip already owns
        // that moment, which is the one-strip-at-a-time promise it has always made.
        testCase "nothing is offered while the session cannot be reached" <| fun () ->
            let offline =
                { private' "github" "github rejected this credential" with
                    Link = SessionLink.Refused ("transport closed", Phase.ReadThrough) }
            let html = Support.render offline
            Expect.isFalse (html.Contains Dom.Hooks.signInRequired) "no sign-in prompt while offline"
            Expect.isTrue
                (html.Contains (Dom.attr Dom.Hooks.degraded Dom.Text.degradedSignedOut))
                "the degraded strip is what speaks for that moment"
            // The panel row still says it. A status is true whether or not it can be acted on
            // right now; only the OFFER is withheld.
            Expect.isTrue
                (html.Contains (Dom.attr Dom.Hooks.githubSignInRequired "mine"))
                "and the panel still reports the credential honestly"

        // Two opposite facts used to wear the same mark. The idle caret says "nothing was ever
        // said here"; it was also what a client showed BEFORE it had read anything, or while
        // it was still behind the session's own log — the ordinary cold open, and the
        // catch-up that can still follow it (a message never kept on this device, a round
        // trip from landing). So the caret was telling most people the opposite of the truth,
        // and neither state could be told from the other on screen.
        testCase "an empty timeline being fetched, for long enough to say, says it is reading" <| fun () ->
            let looking = Support.render (emptyAt (fetchingOpening true))
            Expect.isTrue
                (looking.Contains "data-history-loading")
                "a client still short of where the session says its log ends says it is reading, rather than claiming emptiness"

        testCase "the reading state says it in words" <| fun () ->
            Expect.isTrue
                ((Support.render (emptyAt (fetchingOpening true))).Contains Dom.Text.catchingUp)
                "the pulse alone reaches nobody who cannot see it"

        // Nothing is being caught up on while the session is still being reached, and the
        // status line is the one saying what is happening. It used to say "catching up" here,
        // from the server's page on, before anything had been asked for.
        testCase "a client still reaching its session does not say it is catching up" <| fun () ->
            Expect.isFalse
                ((Support.render (emptyAt (SessionLink.Connecting { Effort.fresh with Slow = true }))).Contains "data-history-loading")
                "reaching is not reading"

        // A reload's own store answers in a few hundred milliseconds; "catching up" for that
        // long, over a conversation already on this device, was a flash of a state nobody
        // was in.
        testCase "a brief wait says nothing yet" <| fun () ->
            Expect.isFalse
                ((Support.render (emptyAt (fetchingOpening false))).Contains "data-history-loading")
                "no reading line before the wait is worth saying"

        testCase "a brief wait does not claim the session is empty either" <| fun () ->
            Expect.isFalse
                ((Support.render (emptyAt (fetchingOpening false))).Contains "data-timeline-empty")
                "nothing is said at all"

        // Cut off with nothing of the session on this device: what is known is that its history
        // is not here, which the idle caret — "nothing was ever said" — got backwards.
        testCase "a page with nothing kept that cannot reach its session says its history is not here" <| fun () ->
            let cut = emptyAt (SessionLink.Connecting { Effort.fresh with Failed = Some { Reason = "HTTP 502"; Count = 3 } })
            Expect.isTrue ((Support.render cut).Contains Dom.Text.historyNotOnDevice) "the history is somewhere else"

        testCase "a page with nothing kept that cannot reach its session does not claim it is empty" <| fun () ->
            let cut = emptyAt (SessionLink.Connecting { Effort.fresh with Failed = Some { Reason = "HTTP 502"; Count = 3 } })
            Expect.isFalse ((Support.render cut).Contains "data-timeline-empty") "nobody here knows it is empty"

        // Its store may yet answer with the conversation, so it says nothing either way.
        testCase "a page whose store has not answered says nothing of its history, cut off or not" <| fun () ->
            let cut =
                { emptyAt (SessionLink.Connecting { Effort.fresh with Failed = Some { Reason = "HTTP 502"; Count = 3 } }) with
                    LocalCopy = LocalCopy.Reading }
            let html = Support.render cut
            Expect.isFalse (html.Contains Dom.Text.historyNotOnDevice || html.Contains "data-timeline-empty") "neither here nor empty"

        testCase "an empty timeline read through says it is empty" <| fun () ->
            Expect.isTrue
                ((Support.render (emptyAt SessionLink.Current)).Contains "data-timeline-empty")
                "a client that has read through and found nothing says so"

        testCase "an empty timeline read through is not still reading" <| fun () ->
            Expect.isFalse
                ((Support.render (emptyAt SessionLink.Current)).Contains "data-history-loading")
                "a client that has read through and found nothing is not still reading"

        testCase "a timeline read through with messages in it shows no reading state" <| fun () ->
            Expect.isFalse
                ((Support.render representativeModel).Contains "data-history-loading")
                "a timeline with messages in it is not an empty one"

        // The camera's cold open: the first pages off the network are the OLDEST end of the
        // log, and drawn pinned to its foot they were a screen of somebody else's history,
        // swapped for the real end of it a moment later.
        testCase "history off the network before read through is not drawn" <| fun () ->
            let opening =
                Support.render
                    { representativeModel with
                        LocalCopy = LocalCopy.NoneKept
                        Link = fetchingOpening true }
            Expect.isTrue (opening.Contains "data-history-loading") "it says it is reading instead"

        testCase "history this client kept is drawn before read through" <| fun () ->
            let kept =
                Support.render
                    { representativeModel with
                        LocalCopy = LocalCopy.Kept None
                        Link = fetchingOpening false }
            Expect.isFalse (kept.Contains "data-history-loading") "the reader's own last picture is shown at once"

        testCase "a session whose only act is queued shows it and not the empty caret" <| fun () ->
            // Nothing is FOLDED in this session — its first command is still queued — so a
            // caret keyed on the fold would stand over the one thing on the screen and say the
            // session was empty.
            let queuedOnly = Support.render { emptyAt SessionLink.Current with Synced = representativeModel.Synced }
            Expect.isTrue (queuedOnly.Contains Dom.Hooks.chatPending) "a session whose first act is queued shows it"
            Expect.isFalse (queuedOnly.Contains "data-timeline-empty") "and does not also say it is empty"

        testCase "the way back to the latest message is rendered, named, and the chat's" <| fun () ->
            // Whether it is OFFERED is the reader's scroll, which `Tail` in the browser reports
            // and the model holds (`Readers`); given that, what markup owns is the rest, and it is the half that
            // breaks silently: a control whose word is not its whole name, and one naming the
            // wrong surface would bring back the wrong column.
            let html = Support.render { representativeModel with Readers = Map.ofList [ TailSurface.Conversation, Behind.Far ] }
            Expect.isTrue
                (html.Contains Dom.Text.jumpToLatest)
                "carrying a name, since the word on it says less than the whole act"
            let chats = sprintf "%s=\"%s\"" Dom.Hooks.jumpToLatest (TailSurface.key TailSurface.Conversation)
            Expect.isTrue (html.Contains chats) "naming the conversation as the surface it brings back"

        testCase "the way back to the latest message is not offered to a reader already there" <| fun () ->
            // Availability from the other side: a reader at the end has nowhere to jump to.
            let html = Support.render { representativeModel with Readers = Map.empty }
            Expect.isFalse (html.Contains Dom.Text.jumpToLatest) "no jump while the reader is at the end"

        testCase "a client that cannot keep history says so; one that can says nothing" <| fun () ->
            // The availability invariant, not the wording: a client whose context denies it a
            // store keeps no history, and the alternative to saying so is a session that
            // quietly stops remembering with nothing on screen to explain it. The note is
            // ABSENT for every ordinary client, which is the half that keeps it meaningful.
            let ordinary = Support.render representativeModel
            Expect.isFalse
                (ordinary.Contains "data-history-store")
                "a client that keeps history has nothing to explain"
            let denied = Support.render { representativeModel with LocalCopy = LocalCopy.Unkeepable }
            Expect.isTrue
                (denied.Contains "data-history-store")
                "a client that cannot keep history says which capability is missing"
            Expect.isTrue
                (denied.Contains "HTTPS")
                "and names the remedy, which is the operator's and is one flag"

        testCase "a client that could not read its own store says so, with the reason" <| fun () ->
            // Availability: a page running without what this device kept says so, rather
            // than looking like a session with no history.
            let unread = Support.render { representativeModel with LocalCopy = LocalCopy.Unread "the saved history did not answer in 5 seconds" }
            Expect.isTrue (unread.Contains "data-history-store") "the page says it runs without its copy"
            Expect.isTrue (unread.Contains "did not answer in 5 seconds") "and why"

        testCase "live mode: the lease bar names the holder and offers the steal" <| fun () ->
            let html = Support.render leasedTerminalModel
            let required =
                [ "the lease bar names who holds it", Dom.attr Dom.Hooks.terminalLease (PeerId.value bob)
                  // Any peer may take it, so the control is offered rather than gated —
                  // collaborators are trusted, and the event log is what makes a steal safe.
                  "the steal control", Dom.attr Dom.Hooks.terminalTake (TerminalId.value terminalId)
                  // The queue survives live mode: an entry queued now runs the moment the
                  // terminal comes back, and it says which of the two holds it is under.
                  "the queue is still there", Dom.attr Dom.Hooks.terminalQueued "queue-ui-term" ]
            for label, marker in required do
                Expect.isTrue (html.Contains marker) (sprintf "%s (`%s`) must render" label marker)

        // This case used to assert the opposite: that the command line GAVE WAY to the bar,
        // because a box marked "Run" that could not run was the misleading half of live mode.
        // The fix for that was the label, not the box. The session holds the queue while a
        // peer is live rather than refusing it, so the watcher's line is the one thing they
        // can still use — and without it they had nowhere to say the next command.
        testCase "live mode: a watcher keeps their command line under the lease bar" <| fun () ->
            let html = Support.render leasedTerminalModel
            Expect.isTrue
                (html.Contains (Dom.attr Dom.Hooks.terminalInput (BodyKey.terminalDraft terminalId ada)))
                "the composer's own command line is still there"

        testCase "live mode: Run says Queue, for the watcher and for the holder" <| fun () ->
            // What a send does while somebody holds the keyboard is wait for the hand-back,
            // and the control says so in its accessible name rather than leaving the queue
            // card to explain afterwards.
            Expect.equal
                (sendName (Support.render leasedTerminalModel), sendName (Support.render heldTerminalModel))
                (Dom.Text.queue, Dom.Text.queue)
                "both sides of a lease queue"

        testCase "a terminal nobody holds runs what is sent" <| fun () ->
            // The other half, so the case above cannot pass on a control that always says Queue.
            Expect.equal (sendName (Support.render representativeModel)) Dom.Text.run "a free terminal runs"

        testCase "behind live, nothing offers to run a command — the bar offers live instead" <| fun () ->
            // This case used to say the opposite: that rewinding is only where the reader is
            // looking, so the line stays and its Run still runs. A line under a recording
            // reads as typing into the recording, so it is replaced (`terminalRewound`); the
            // terminal still runs, and the way back to it is the bar's press.
            let rewound = Support.step (RewindTerminalMsg terminalId) representativeModel
            Expect.isTrue (ClientModel.isRewound terminalId rewound) "the fixture is behind live"
            let html = Support.render rewound
            Expect.isFalse (html.Contains (Dom.attr Dom.Hooks.terminalSend (PeerId.value ada))) "no Run to press"
            Expect.isTrue (html.Contains Dom.Hooks.terminalRewoundLive) "and the way back is offered"

        testCase "a queued command in a leased terminal says it waits for the TERMINAL" <| fun () ->
            // A queue that said only *pending* would leave the hold looking like a stall;
            // this one resolves when a person finishes a task, and says so.
            let model =
                { leasedTerminalModel with
                    Synced =
                        { leasedTerminalModel.Synced with
                            Pending =
                                leasedTerminalModel.Synced.Pending
                                |> Map.map (fun _ entry -> { entry with Authority = Authority.ofAuthor (Principal.Peer ada) }) } }
            let html = Support.render model
            Expect.isTrue
                (html.Contains (Dom.attr Dom.Hooks.terminalQueuedStatus Dom.Text.queuedAwaitingTerminal))
                "the hold names the terminal"

        testCase "a terminal whose shell was replaced says so, and offers a new terminal" <| fun () ->
            // Named, not shown as a stall: the terminal runs no commands, and a surface that
            // only showed "pending" would be indistinguishable from a bug.
            let html = Support.render lostIntegrationModel
            for label, marker in
                [ "the state is named", Dom.attr Dom.Hooks.terminalLost (TerminalId.value terminalId)
                  "with the way on", Dom.attr Dom.Hooks.terminalLostNew (TerminalId.value terminalId) ] do
                Expect.isTrue (html.Contains marker) (sprintf "%s (`%s`) must render" label marker)

        testCase "no surface offers to re-arm a terminal" <| fun () ->
            // Re-arming typed our rc into whatever read the pty — over ssh, the remote host —
            // and handed it every later block's loan. The control is gone, and so is its word.
            let html = (Support.render lostIntegrationModel).ToLowerInvariant ()
            Expect.isFalse (html.Contains "re-arm") "no re-arm"
            Expect.isFalse (html.Contains "marking") "and no word for the marks a person never sees"

        testCase "a closed terminal is reachable, and shows what it ran rather than a composer" <| fun () ->
            let html = Support.render closedTerminalModel
            for label, marker in
                [ "a closed terminal has a tab of its own",
                  Dom.attr Dom.Hooks.terminalClosedTab (TerminalId.value terminalId)
                  // What it RAN is the read; how it behaved is a recording one press away.
                  // Both at once put a player of the same two lines under every command and
                  // its result.
                  "the commands it ran are listed", Dom.attr Dom.Hooks.terminalBlock "block-ui"
                  "and the way to its recording is offered",
                  Dom.attr Dom.Hooks.terminalWatch "watch" ] do
                Expect.isTrue (html.Contains marker) (sprintf "%s (`%s`) must render" label marker)
            // Nothing can be run in a closed terminal, so nothing offers to: a command line
            // that queues into a terminal with no shell behind it is the misleading half.
            Expect.isFalse
                (html.Contains (Dom.attr Dom.Hooks.terminalInput (BodyKey.terminalDraft terminalId ada)))
                "no command line"
            // The kill and the attach-again used to be asserted here, against the STRIP.
            // They live on the terminal's row in the list now (Plan 20, stage 1), and the
            // list's own cases pin both halves of each. Re-asserting their absence from a
            // strip that offers no verbs at all would be a test that cannot fail.

        testCase "terminal work sits in the chat WHERE it happened, not at the end" <| fun () ->
            // Plan 14, stage 1. The fixture's block is anchored at offset 2, between the two
            // messages at 1 and 4 — so the merge has to put it there. Appending it after
            // everything said would be the thing the offset exists to prevent.
            let html = Support.render representativeModel
            let indexOf (needle: string) = html.IndexOf needle
            let said = indexOf (Dom.attr Dom.Hooks.messageId "msg-1")
            let ran = indexOf (Dom.attr Dom.Hooks.chatBlock "block-ui")
            let answered = indexOf (Dom.attr Dom.Hooks.messageId "msg-agent")
            Expect.isTrue (said >= 0 && ran >= 0 && answered >= 0) "all three render"
            Expect.isTrue (said < ran && ran < answered) "said, ran, answered — in log order"

        testCase "a chip is one line: who ran what, and how it went — never the output" <| fun () ->
            // Output inline would make the chat noisiest exactly when it is busiest, and
            // would put everything a command printed one glance from everyone in the session
            // rather than one tap. The panel is where output lives.
            let html = Support.render representativeModel
            let chat =
                let start = html.IndexOf Dom.Hooks.conversation
                html.Substring (start, html.IndexOf ("</section>", start) - start)
            Expect.isTrue (chat.Contains (Dom.attr Dom.Hooks.chatBlock "block-ui")) "the chip is there"
            Expect.isTrue (chat.Contains "ls -la") "with the command it ran"
            Expect.isFalse (chat.Contains Dom.Hooks.terminalOutput) "and nothing it printed"
            // The panel is where output lives, and it still does.
            Expect.isTrue (html.Contains Dom.Hooks.terminalOutput) "the terminal panel is unchanged"

        // Nick, 2026-10-03: "aggregate like we do with tool use" — fold the pane's own
        // replay the way the chat already folds a turn's tool calls into `chatTaskCard`,
        // by the same rule: only CONSECUTIVE blocks from one actor group, never past a
        // command from somebody else, and never a group of one. (Helpers `runModelWith`/
        // `runBlock` are defined above, beside `representativeModel`.)
        testCase "consecutive commands from one actor fold under 'ran n commands', with a tally" <| fun () ->
            let agent = Authority.agentFor (Principal.Peer ada)
            let html =
                Support.render
                    (runModelWith
                        [ runBlock agent 1 (BlockFinished (CommandSucceeded 0))
                          runBlock agent 2 (BlockFinished (CommandFailed 1))
                          runBlock agent 3 (BlockFinished (CommandSucceeded 0))
                          // The newest two stay out of the fold (`BlockGroup.ofBlocks`): each
                          // is a share of the pane, and two of them fill it.
                          runBlock agent 4 (BlockFinished (CommandSucceeded 0))
                          runBlock agent 5 (BlockFinished (CommandSucceeded 0)) ])
            Expect.isTrue
                (html.Contains (Dom.attr Dom.Hooks.terminalBlockRun "block-run-1"))
                "the fold is keyed by the FIRST block in it"
            Expect.isTrue (html.Contains "ran 3 earlier commands") "the header counts every command inside"
            for n in 1 .. 3 do
                Expect.isTrue
                    (html.Contains (Dom.attr Dom.Hooks.terminalBlock (sprintf "block-run-%d" n)))
                    (sprintf "command %d is still its own block, nested inside the fold" n)

        testCase "one command from one actor is never wrapped in a fold of one" <| fun () ->
            // The chat's task card holds the same line — a disclosure around a single chip
            // hides the only thing the row has to say behind a click.
            let html = Support.render representativeModel
            Expect.isFalse
                (html.Contains Dom.Hooks.terminalBlockRun)
                "a lone command stays a lone block"

        testCase "a command from somebody else splits a run, even between two of the agent's own" <| fun () ->
            let agent = Authority.agentFor (Principal.Peer ada)
            let human = Authority.ofAuthor (Principal.Peer ada)
            let html =
                Support.render
                    (runModelWith
                        [ runBlock agent 1 (BlockFinished (CommandSucceeded 0))
                          runBlock human 2 (BlockFinished (CommandSucceeded 0))
                          runBlock agent 3 (BlockFinished (CommandSucceeded 0)) ])
            Expect.isFalse
                (html.Contains Dom.Hooks.terminalBlockRun)
                "three commands, but no two of them consecutive and same-author — nothing folds"

        testCase "a concluded lease stretch is its own item, and says how it ended" <| fun () ->
            let stretchModel =
                { representativeModel with
                    Timeline =
                        { representativeModel.Timeline with
                            TerminalItems =
                                representativeModel.Timeline.TerminalItems
                                @ [ TimelineStretch
                                        { Offset = EventOffset.create 3L |> expect
                                          TerminalId = terminalId
                                          Title = "build"
                                          Holder = PeerRef bob
                                          End = LeaseStolen (PeerRef ada)
                                          Range = Some (2, 40)
                                          StartedAt = DateTimeOffset (2026, 8, 8, 0, 0, 0, TimeSpan.Zero)
                                          EndedAt = DateTimeOffset (2026, 8, 8, 0, 2, 0, TimeSpan.Zero) } ] } }
            let html = Support.render stretchModel
            let required =
                [ "the stretch item", Dom.attr Dom.Hooks.chatStretch "term-ui@3"
                  // Four endings, four answers: "did they finish, get taken over, drop out,
                  // or wander off?" is not one question.
                  "how it ended", Dom.attr Dom.Hooks.chatStretchEnd Dom.Text.stretchStolen
                  "who held it", "bob"
                  "where", "build"
                  "for how long", "2m 0s" ]
            for label, marker in required do
                Expect.isTrue (html.Contains marker) (sprintf "%s (`%s`) must render" label marker)

        testCase "live mode shows the SCREEN, and every peer sees the same one" <| fun () ->
            // Plan 14, stage 6. A program is running here, and what it displays is a
            // projection of what it emitted — the block history is block mode's view of a
            // terminal, and it comes back the moment the lease does.
            let watching = Support.render leasedTerminalModel
            Expect.isTrue
                (watching.Contains (Dom.attr Dom.Hooks.terminalScreen (TerminalId.value terminalId)))
                "the screen renders for a peer who is only watching"
            Expect.isTrue (watching.Contains "vim ~/notes") "with what the program drew"
            // ANSI through the same theme tokens a block's output uses — a raw ANSI colour
            // would not clear the contrast floor on this ground.
            Expect.isTrue (watching.Contains (themedGreen ())) "coloured by the theme, not by the escape"
            Expect.isFalse
                (watching.Contains (Dom.attr Dom.Hooks.terminalBlock (BlockId.value blockId)))
                "and the block history gives way to it"
            // Watching is not a lesser mode; it is the ordinary one. What the holder gets in
            // addition is the keyboard.
            let keys = Dom.attr Dom.Hooks.terminalKeys (TerminalId.value terminalId)
            Expect.isFalse (watching.Contains keys) "a watcher's screen takes no keystrokes"
            let held = Support.render heldTerminalModel
            // In a text field, because a phone raises its keyboard for nothing else (P3-3) —
            // and a field is a Tab stop, so a keyboard can reach it too.
            let at = held.IndexOf keys
            Expect.isTrue (at >= 0) "the holder's does"
            let tag = (held.Substring (held.LastIndexOf ('<', at) + 1)).Split(' ').[0]
            Expect.equal tag "textarea" "in a text field"

        testCase "a recording the cap ate is a STATED gap, not an empty player" <| fun () ->
            // The one place stage 3d's behaviour reaches the surface. An empty player is
            // indistinguishable from a terminal that printed nothing, and the whole reason
            // the drop is recorded is that a hole in an audit trail must be a stated fact.
            let html = Support.render forgottenTerminalModel
            Expect.isTrue
                (html.Contains (Dom.attr Dom.Hooks.terminalReplayGone (TerminalId.value terminalId)))
                "the gap is named"
            Expect.isFalse
                (html.Contains (Dom.attr Dom.Hooks.paneReplay (ClientModel.tabKey terminalId)))
                "and no player is mounted over nothing"

        testCase "a closed terminal's band says nothing of a recording not yet heard back about" <| fun () ->
            // The same output dropped, but the store has not answered this reader yet: a gap
            // nobody has been told about is not one to state.
            let html = Support.render { forgottenTerminalModel with TerminalFeeds = Map.empty }
            Expect.isFalse
                (html.Contains (Dom.attr Dom.Hooks.terminalReplayGone (TerminalId.value terminalId)))
                "nothing is known to be missing"

        testCase "the random peer display name is human-readable" <| fun () ->
            let rng = Random 1234
            for _ in 1 .. 20 do
                let name = PeerName.random rng
                let parts = name.Split '-'
                Expect.equal parts.Length 2 "adjective-animal shape"
                Expect.isTrue (parts.[0].Length > 0 && parts.[1].Length > 0) "both halves non-empty"

        testCase "joining a peer's draft: theirs is the composer, yours is a summary, and either can be sent" <| fun () ->
            let html = Support.render joinedComposerModel
            let required =
                [ "the joined draft is the composer", Dom.attr Dom.Hooks.draftInput "bob"
                  "its author is named, not numbered", "brave-owl"
                  "anyone may send the draft they are in", Dom.attr Dom.Hooks.sendDraft "bob"
                  "your own draft collapses to a summary", Dom.attr Dom.Hooks.draftSummary "ada"
                  "a summary opens on click", Dom.attr Dom.Hooks.expandDraft "ada"
                  "the summary carries the body, clamped", Dom.attr "data-rich-body" (BodyKey.draft ada)
                  "who is editing it right now", Dom.attr Dom.Hooks.draftEditor' (ActorRef.token (PeerRef bob))
                  "the way out of collaborating", Dom.Hooks.newDraft ]
            for label, needle in required do
                Expect.isTrue (html.Contains needle) (sprintf "%s (%s)" label needle)
            // And there is exactly ONE editable body host: the composer, not the summaries.
            Expect.equal
                (html.Split "data-rich-readonly=\"false\"" |> Array.length |> (fun n -> n - 1))
                2
                "one editable draft body, plus the queued message's own editor"

        // The agent's absence is asked for exactly once, and by a verb on the agent's own row
        // rather than a box under it. It used to be a sidebar row, a strip over the composer
        // AND the settings copy; then a full-width button hanging under the row, repeating
        // the one settings already has. A real <button>, so Tab and Enter reach it.
        testCase "the agent's absence is asked for exactly once, by a button on the agent's own row" <| fun () ->
            let html = Support.render representativeModel // no agent in this model
            let occurrences (needle: string) = (html.Split needle |> Array.length) - 1
            Expect.equal (occurrences Dom.Hooks.noAgentConnect) 1 "one connect verb, not several"
            // It sits in the membership section, which the shell renders before the timeline.
            Expect.isTrue
                (html.IndexOf Dom.Hooks.noAgentConnect < html.IndexOf Dom.Hooks.conversation)
                "the verb is in the sidebar's membership section, not over the composer"
            // A button, opened after the agent's row opens: the verb rides that row.
            let row = html.IndexOf (Dom.attr Dom.Hooks.agentPresence "absent")
            let verb = html.IndexOf Dom.Hooks.noAgentConnect
            Expect.isTrue (html.Substring(0, verb).LastIndexOf "<button" > row) "the verb is a button opened inside the agent's row"
            // A session WITH an agent asks for nothing at all.
            let connected = representativeModel |> withClaudePanel (fun panel -> { panel with AgentAvailable = true })
            let connectedHtml = Support.render connected
            Expect.isFalse (connectedHtml.Contains Dom.Hooks.noAgent) "nothing asks for a connection once there is one"

        // The timeline renders a sent body's Markdown as formatted rich text — the read-only
        // mirror of the composer — through the very SSR path the browser also runs. This pins
        // it in the cheap tier; the real-browser two-peer flow (Browser.fs) covers it live.
        testCase "a sent markdown body renders as formatted rich text in the timeline" <| fun () ->
            let richItem : ConversationItem =
                { MessageId = MessageId.create "msg-rich" |> expect
                  Author = PeerRef ada
                  Content = ItemContent.Message ("# Heading one\n\nText with **bold** and `code`.\n\n- item one\n- item two")
                  Status = Complete
                  Offset = EventOffset.create 1L |> expect
                  Woke = None; CausedBy = None }
            let model =
                { representativeModel with
                    Conversation = { representativeModel.Conversation with Recent = List.rev ([ richItem ]) } }
            let html = Support.render model
            let timeline =
                let start = html.IndexOf Dom.Hooks.conversation
                html.Substring (start, html.IndexOf ("</section>", start) - start)
            // Block/inline structure comes through as semantic elements…
            for label, marker in
                [ "heading text", ">Heading one</h1>"
                  "bold mark", ">bold</strong>"
                  "inline code", ">code</code>"
                  "bullet list", "<ul"
                  "list item", "<li"
                  "list item text", ">item one</p>" ] do
                Expect.isTrue (timeline.Contains marker) (sprintf "%s (`%s`) must render formatted" label marker)
            // …and the Markdown syntax itself is transformed away, never left as literal source.
            Expect.isFalse (timeline.Contains "# Heading one") "the heading '#' is not literal text"
            Expect.isFalse (timeline.Contains "**bold**") "the bold '**' is not literal text"

        // A repo note (Plan 14) is an ACT in the timeline, not a message: it renders as
        // the quiet attributed line, never as an avatar'd message article.
        testCase "a repo note renders as an attributed act-line, not a message" <| fun () ->
            let note : ConversationItem =
                { MessageId = MessageId.create "msg-repo-note" |> expect
                  Author = PeerRef ada
                  Content = ItemContent.Act (repoRemoved "octo/hello")
                  Status = Complete
                  Offset = EventOffset.create 1L |> expect
                  Woke = None; CausedBy = None }
            let model =
                { representativeModel with
                    Conversation = { representativeModel.Conversation with Recent = List.rev ([ note ]) } }
            let html = Support.render model
            Expect.isTrue (html.Contains "data-act-note") "the note hook renders"
            Expect.isTrue ((Support.readable html).Contains "removed repo ") "the act reads as its sentence"
            Expect.isTrue
                (html.Contains (Dom.attr "data-entity" (EntityRef.said (EntityRef.Repo (RepoRef.create "octo/hello" |> expect)))))
                "naming its repository as a reference"
            let noteStart = html.IndexOf "data-act-note"
            let article = html.Substring (html.LastIndexOf ("<article", noteStart), 300)
            Expect.isFalse (article.Contains "data-message-body") "no message body — it is not something someone said"

        // An act's particulars are on the note, under its headline — not appended to the
        // headline, and not behind a disclosure. Both of those were how this read before:
        // one line carrying a sandbox, a backend, a forwarded credential and a paragraph of
        // realisation, set in the caps label voice. What is pinned is the SPLIT — that the
        // two halves are separate elements — because that is the promise a redesign of the
        // line must keep, whatever it ends up looking like.
        testCase "an act with particulars says them under its headline, not along it" <| fun () ->
            let note : ConversationItem =
                { MessageId = MessageId.create "msg-sandbox-note" |> expect
                  Author = PeerRef ada
                  Content = ItemContent.Act (repoAdded "octo/hello" "main")
                  Status = Complete
                  Offset = EventOffset.create 1L |> expect
                  Woke = None; CausedBy = None }
            let model =
                { representativeModel with
                    Conversation = { representativeModel.Conversation with Recent = List.rev ([ note ]) } }
            let html = Support.render model
            let noteStart = html.IndexOf "data-act-note"
            // To the article's own END, not a fixed number of characters: an article grows
            // every time the item gains a control, and a window sized by hand turns that into
            // a failure of this case rather than of whatever it is watching.
            let openedAt = html.LastIndexOf ("<article", noteStart)
            let article = html.Substring (openedAt, html.IndexOf ("</article>", openedAt) - openedAt)
            Expect.isTrue ((Support.readable article).Contains "added repo ") "the headline is what the act was"
            Expect.isTrue (article.Contains "data-act-detail") "and the particulars are their own element"
            let detail = article.Substring (article.IndexOf "data-act-detail")
            Expect.isTrue (detail.Contains "on branch main") "carrying what the headline left out"

        // A file change is the act the file verbs exist to put on the timeline. What is pinned
        // is the promise, not the look: the change itself is THERE, under the headline, as
        // its own element a reader can open — where the same change used to be a heredoc in a
        // terminal. A write carries no diff, so it makes no such promise and draws no element.
        testCase "an edit's diff is on its note, as its own disclosure" <| fun () ->
            let edited : ConversationItem =
                { MessageId = MessageId.create "msg-edit" |> expect
                  Author = ActorRef.Agent
                  Content =
                    ItemContent.Act (
                        Act.FileChanged
                            { FileChanged.MessageId = MessageId.create "msg-edit" |> expect
                              FileChanged.Sandbox = SandboxRef.defaultRef
                              FileChanged.Path = "src/A.fs"
                              FileChanged.Change = FileChange.Edited (1, 1, 1)
                              FileChanged.Diff = Some "-let x = 1\n+let x = 2"
                              FileChanged.Actor = ActorRef.Agent }
                    )
                  Status = Complete
                  Offset = EventOffset.create 1L |> expect
                  Woke = None; CausedBy = None }
            let model =
                { representativeModel with
                    Conversation = { representativeModel.Conversation with Recent = List.rev ([ edited ]) } }
            let html = Support.render model
            Expect.isTrue ((Support.readable html).Contains "edited src/A.fs") "the headline names the file"
            // Behind the disclosure, which is where a diff belongs: the line says what
            // happened, the fold says what changed.
            let diff = Support.behindFold (FoldKey.Act (MessageId.create "msg-edit" |> expect)) model
            Expect.isTrue (diff.Contains "data-act-fact=\"diff\"") "and the change is its own element"
            Expect.isTrue (diff.Contains "+let x = 2") "carrying the line that came in"
            Expect.isTrue (diff.Contains "-let x = 1") "and the one that went out"

        testCase "a write's note carries no diff element" <| fun () ->
            let written : ConversationItem =
                { MessageId = MessageId.create "msg-write" |> expect
                  Author = ActorRef.Agent
                  Content =
                    ItemContent.Act (
                        Act.FileChanged
                            { FileChanged.MessageId = MessageId.create "msg-write" |> expect
                              FileChanged.Sandbox = SandboxRef.defaultRef
                              FileChanged.Path = "src/B.fs"
                              FileChanged.Change = FileChange.Written 3
                              FileChanged.Diff = None
                              FileChanged.Actor = ActorRef.Agent }
                    )
                  Status = Complete
                  Offset = EventOffset.create 1L |> expect
                  Woke = None; CausedBy = None }
            let model =
                { representativeModel with
                    Conversation = { representativeModel.Conversation with Recent = List.rev ([ written ]) } }
            let html = Support.render model
            Expect.isTrue ((Support.readable html).Contains "wrote src/B.fs") "the headline says it was written"
            Expect.isFalse (html.Contains "data-act-fact=\"diff\"") "nothing pretends to be a diff"

        // A repository a sentence points at is somewhere a person can go, and the reference
        // is how they get there: a real link, reachable by keyboard like every action here.
        // Which glyph marks it and how the link dresses are the design; that it LEADS to the
        // repository is the promise.
        testCase "a push's repository is a reference that leads to the repository" <| fun () ->
            let hello = RepoRef.create "octo/hello" |> expect
            let push : ConversationItem =
                { MessageId = MessageId.create "msg-push" |> expect
                  Author = ActorRef.Agent
                  Content =
                    ItemContent.Act (
                        Act.CredentialSpent
                            { MessageId = MessageId.create "msg-push" |> expect
                              Sandbox = SandboxRef.defaultRef
                              Terminal = TerminalId.create "term-1" |> expect
                              Block = None
                              Owner = CredentialFor.Person (Principal.Peer ada)
                              Repo = hello
                              Actor = ActorRef.Agent })
                  Status = Complete
                  Offset = EventOffset.create 1L |> expect
                  Woke = None; CausedBy = None }
            let model =
                { representativeModel with
                    Conversation = { representativeModel.Conversation with Recent = List.rev ([ push ]) } }
            let html = Support.render model
            let start = html.IndexOf (Dom.attr "data-entity" (EntityRef.said (EntityRef.Repo hello)))
            Expect.isTrue (start >= 0) "the repository is its own element on the push"
            let opened = html.LastIndexOf ("<a ", start)
            let element = html.Substring (opened, html.IndexOf (">", start) - opened)
            Expect.isTrue (element.Contains "href=\"https://github.com/octo/hello\"") "and it leads to the repository on its host"

        // The human can always see what the agent was told. The facts above are the screen's
        // arrangement of an act; the sentence behind the disclosure is the other reader's, and
        // the two are the same phrase — so what a person finds there is what the prompt
        // carried, to the character. Pinned as equality with `ConversationItem.said`, which is
        // exactly the string the agent's context is built from (`Host.fs`).
        testCase "what an act says behind its disclosure is what the agent was told" <| fun () ->
            let note : ConversationItem =
                { MessageId = MessageId.create "msg-said-note" |> expect
                  Author = PeerRef ada
                  Content = ItemContent.Act (repoAdded "octo/hello" "main")
                  Status = Complete
                  Offset = EventOffset.create 1L |> expect
                  Woke = None; CausedBy = None }
            let model =
                { representativeModel with
                    Conversation = { representativeModel.Conversation with Recent = List.rev ([ note ]) } }
            let behind = Support.behindFold (FoldKey.Act (MessageId.create "msg-said-note" |> expect)) model
            Expect.isTrue (behind.Contains "data-act-said") "every act offers what the agent was told"
            let element = behind.Substring (behind.IndexOf "data-act-said")
            let text = Support.readable (element.Substring (element.IndexOf ">" + 1))
            Expect.equal text (ConversationItem.said note) "the disclosure reads exactly as the prompt did"
            Expect.isTrue (text.Contains "on branch main") "particulars included, not the headline alone"
            // And it is TEXT. The quote used to draw each reference in it the way the screen
            // draws one — a repository glyph, a person's checker, a link — inside a quotation
            // of a prompt that held none of them: it claimed to be what the agent read and
            // looked like what the screen renders. The words were always right; what they
            // wore was not.
            let quote = element.Substring (element.IndexOf ">" + 1)
            Expect.isFalse (quote.Contains "data-entity") "a quote of what the agent read carries no drawn references"
            Expect.isFalse (quote.Contains "<a ") "and nothing in it leads anywhere the agent could not go"

        // The fold is a CONTROL, and says which way it is: a real button carrying its
        // expanded state, so a keyboard reaches it and a screen reader hears it — and the
        // particulars it fronts say the same thing, so a test (or a stylesheet) reading
        // either cannot be told two stories. Folded by default: a timeline is read as its
        // titles. Which glyph the control wears, and how the fold moves, are the design.
        testCase "an act's particulars unfold from a control that says whether it is open" <| fun () ->
            let note : ConversationItem =
                { MessageId = MessageId.create "msg-fold-note" |> expect
                  Author = PeerRef ada
                  Content = ItemContent.Act (repoAdded "octo/hello" "main")
                  Status = Complete
                  Offset = EventOffset.create 1L |> expect
                  Woke = None; CausedBy = None }
            let folded =
                { representativeModel with
                    Conversation = { representativeModel.Conversation with Recent = List.rev ([ note ]) } }
            let control (html: string) =
                let at = html.IndexOf (Dom.attr "data-fold" "act-msg-fold-note")
                Expect.isTrue (at >= 0) "the act offers a fold"
                let opened = html.LastIndexOf ("<button", at)
                html.Substring (opened, html.IndexOf (">", at) - opened)
            let shut = Support.render folded
            Expect.isTrue ((control shut).Contains "aria-expanded=\"false\"") "folded by default, and the control says so"
            Expect.isTrue (shut.Contains (Dom.attr "data-fold-open" "no")) "and the particulars agree"
            let open' = Support.render (Support.step (ToggleFoldMsg (FoldKey.Act note.MessageId)) folded)
            Expect.isTrue ((control open').Contains "aria-expanded=\"true\"") "one press unfolds it, and the control says so"
            Expect.isTrue (open'.Contains (Dom.attr "data-fold-open" "yes")) "and the particulars agree"

        // A shut disclosure builds NOTHING behind it, and that is a promise about the whole
        // page rather than a detail of one act.
        //
        // Every act, every tool call and every run of them is a fold, and a session's
        // timeline holds thousands: measured on a real one of 20,650 events, 2,125 folds, all
        // shut, NOT ONE ever opened — and 41,269 of the document's 54,016 elements, 76% of
        // it, were inside them. All built, styled, laid out and diffed by Lit on every
        // render, then clipped away by `grid-rows-[0fr]`. Dropping them took the open's
        // main-thread work from 4.9s to 1.6s on a phone-speed CPU, and the document from
        // 54,016 elements to 13,645.
        //
        // Nothing a reader could reach is lost: a folded body is `invisible`, so find-in-page
        // and a screen reader already skipped it. What is lost is the cost.
        //
        // Asked of the CONTENT rather than of a tag count. The first draft of this counted
        // tags per act against a budget, which needed a number nobody could derive — the
        // first guess was wrong by a factor of three — and which a restyle of the visible
        // half would move. What is behind a fold either reached the document or it did not,
        // and that question has no magic number in it.
        testCase "a fold nobody has opened builds nothing behind it" <| fun () ->
            let note : ConversationItem =
                { MessageId = MessageId.create "msg-shut" |> expect
                  Author = PeerRef ada
                  Content = ItemContent.Act (repoAdded "octo/hello" "main")
                  Status = Complete
                  Offset = EventOffset.create 1L |> expect
                  Woke = None; CausedBy = None }
            let model =
                { representativeModel with
                    Conversation = { representativeModel.Conversation with Recent = List.rev ([ note ]) } }
            let shut = Support.render model
            // The wrapper stays, always: `aria-controls` names it, the control's state rides
            // it, and a fold that vanished when shut would be a control pointing at nothing.
            Expect.isTrue (shut.Contains (Dom.attr "data-fold-body" "act-msg-shut")) "the disclosure is there"
            Expect.isTrue (shut.Contains (Dom.attr "data-fold-open" "no")) "and it says it is shut"
            // What is BEHIND it is not.
            Expect.isFalse
                (shut.Contains "data-act-said")
                "a fold nobody opened has not built what is behind it — see `foldBodyIn` in \
                 `src/Yession.App/View.fs`; a timeline is thousands of these and three \
                 quarters of the document was inside them"
            // And the other way, so this cannot pass by drawing nothing at all.
            let opened = Support.behindFold (FoldKey.Act note.MessageId) model
            Expect.isTrue (opened.Contains "data-act-said") "and opening it says what the agent was told"

        // An act still in flight is not one to unfold — its account is about to change under
        // the reader, and the gutter is where its pulse sits — so it offers no fold until it
        // settles. The pulse is pinned elsewhere; here, that the two cues never share a spot.
        testCase "an act in flight offers no fold" <| fun () ->
            let running : ConversationItem =
                { MessageId = MessageId.create "msg-running" |> expect
                  Author = ActorRef.Agent
                  Content =
                    ItemContent.Act (
                        Act.SandboxStarting
                            { MessageId = MessageId.create "msg-running" |> expect
                              Sandbox = SandboxRef.defaultRef
                              Backend = "srt"
                              Description = None
                              Actor = ActorRef.Agent; OnBehalfOf = None; CausedBy = None })
                  Status = ConversationItemStatus.Running
                  Offset = EventOffset.create 1L |> expect
                  Woke = None; CausedBy = None }
            let html =
                Support.render
                    { representativeModel with
                        Conversation = { representativeModel.Conversation with Recent = List.rev ([ running ]) } }
            Expect.isFalse (html.Contains (Dom.attr "data-fold" "act-msg-running")) "no fold while it runs"
            Expect.isTrue (html.Contains "data-act-status=\"running\"") "the pulse has the gutter"

        // The diamond is the agent's mark, so whose work an act in flight is shows in whose
        // mark its gutter holds: the session bringing a sandbox up at boot is not the agent
        // doing it, and a diamond there would say it was.
        testCase "an act the session is doing is not marked as the agent's" <| fun () ->
            Expect.isTrue
                ((renderRunningBy ActorRef.Session).Contains (Dom.attr "data-act-running" "other"))
                "the session's running act wears a mark that is not the agent's"

        testCase "the agent's own act in flight is marked as the agent's" <| fun () ->
            Expect.isTrue
                ((renderRunningBy ActorRef.Agent).Contains (Dom.attr "data-act-running" "agent"))
                "the agent's running act wears the agent's mark"

        // A connection a sandbox forwards is drawn as the connection — the same reference
        // every other sentence gives it — not as a badge that happens to carry the word.
        // That it is a reference to the connection is the promise; the mark is the design.
        testCase "a sandbox start's forwarded connection is drawn as the connection" <| fun () ->
            let github = ConnectionName.create "github" |> expect
            let start : ConversationItem =
                { MessageId = MessageId.create "msg-start" |> expect
                  Author = ActorRef.Agent
                  Content =
                    ItemContent.Act (
                        Act.SandboxStarted
                            { MessageId = MessageId.create "msg-start" |> expect
                              Sandbox = SandboxRef.defaultRef
                              Backend = "srt"
                              Description = None
                              Checkout = None
                              Forwarded = [ github ]
                              Realisation = []
                              Actor = ActorRef.Agent; OnBehalfOf = None; CausedBy = None })
                  Status = Complete
                  Offset = EventOffset.create 1L |> expect
                  Woke = None; CausedBy = None }
            let model =
                { representativeModel with
                    Conversation = { representativeModel.Conversation with Recent = List.rev ([ start ]) } }
            let behind = Support.behindFold (FoldKey.Act (MessageId.create "msg-start" |> expect)) model
            let fact = behind.IndexOf (Dom.attr "data-act-fact" "forwarded")
            Expect.isTrue (fact >= 0) "the forwarding is on the start"
            let row = behind.Substring fact
            Expect.isTrue
                (row.Contains (Dom.attr "data-entity" (EntityRef.said (EntityRef.Connection github))))
                "and what it forwards is the connection, as a reference"

        /// A repo's capability ask, as its file made it.
        let capabilityAsk (granted: string list) : ConversationItem =
            let hello = RepoRef.create "octo/hello" |> expect
            { MessageId = MessageId.create "msg-asked" |> expect
              Author = ActorRef.Configured hello
              Content =
                ItemContent.Act (
                    Act.RepoCapabilitiesChanged
                        { RepoCapabilitiesChanged.MessageId = MessageId.create "msg-asked" |> expect
                          RepoCapabilitiesChanged.Repo = hello
                          RepoCapabilitiesChanged.Granted = granted
                          RepoCapabilitiesChanged.Actor = ActorRef.Configured hello
                          RepoCapabilitiesChanged.CausedBy = None })
              Status = Complete
              Offset = EventOffset.create 1L |> expect
              Woke = None; CausedBy = None }

        let renderedAsk (granted: string list) =
            { representativeModel with
                Conversation = { representativeModel.Conversation with Recent = [ capabilityAsk granted ] } }

        // What a repo asks for is a list a person reads DOWN, deciding about each line. The
        // promise is that each grant is its own element in the fold — not that it is drawn
        // any particular way.
        testCase "a capability ask lays out each grant as its own fact" <| fun () ->
            let granted = [ "conn:github/api"; "vol:yession-nix>/nix" ]
            let behind = Support.behindFold (FoldKey.Act (MessageId.create "msg-asked" |> expect)) (renderedAsk granted)
            let facts = behind.Split (Dom.attr "data-act-fact" "grant") |> Array.length |> fun n -> n - 1
            Expect.equal facts (List.length granted) "one fact per grant"

        // The chain the agent reads is not also printed under the headline: the line says
        // how many, and the fold says which.
        testCase "a capability ask does not print its grants under the headline" <| fun () ->
            let html = Support.render (renderedAsk [ "conn:github/api"; "vol:yession-nix>/nix" ])
            Expect.isFalse (html.Contains "data-act-detail") "no second line under it"

        /// A repo-declared sandbox coming up, started by `by`.
        let sandboxStartBy (by: ActorRef) : ConversationItem =
            let hello = RepoRef.create "octo/hello" |> expect
            let dev = SandboxRef.inScope hello (SandboxName.create "dev" |> expect)
            { MessageId = MessageId.create "msg-dev" |> expect
              Author = by
              Content =
                ItemContent.Act (
                    Act.SandboxStarted
                        { MessageId = MessageId.create "msg-dev" |> expect
                          Sandbox = dev
                          Backend = "docker"
                          Description = Some "the dev loop"
                          Checkout = None
                          Forwarded = []
                          Realisation = []
                          Actor = by; OnBehalfOf = None; CausedBy = None })
              Status = Complete
              Offset = EventOffset.create 1L |> expect
              Woke = None; CausedBy = None }

        // A repo's file is the author of its sandbox's start, but somebody asked for the
        // fold that started it — and a note that names only the file answers "who did this"
        // with a file. The person it was for is named on the note, as a reference.
        testCase "a repo's sandbox start names the person it was for" <| fun () ->
            let ada = Principal.Peer (PeerId.create "ada" |> expect)
            let item = sandboxStartBy (ActorRef.Configured (RepoRef.create "octo/hello" |> expect))
            let forAda =
                match item.Content with
                | ItemContent.Act (Act.SandboxStarted s) ->
                    { item with Content = ItemContent.Act (Act.SandboxStarted { s with OnBehalfOf = Some ada }) }
                | _ -> failwith "the fixture is a sandbox start"
            let html =
                Support.render
                    { representativeModel with
                        Conversation = { representativeModel.Conversation with Recent = List.rev ([ forAda ]) } }
            let noteStart = html.IndexOf "data-act-note"
            let note = html.Substring (noteStart, html.IndexOf ("</article>", noteStart) - noteStart)
            Expect.isTrue
                (note.Contains (Dom.attr "data-entity" (EntityRef.said (EntityRef.Actor (Principal.toActor ada)))))
                "the note names Ada as a reference"

        /// The sandbox reference's NAME as rendered — the text inside the entity element.
        let sandboxNameOn (html: string) : string =
            let start = html.IndexOf (Dom.attr "data-entity-kind" "sandbox")
            Expect.isTrue (start >= 0) "the sandbox is a reference on the start"
            let element = html.Substring (start, html.IndexOf ("</span></span>", start) - start)
            let nameAt = element.LastIndexOf ">"
            element.Substring (nameAt + 1)

        // Under the repo that declared it, a sandbox is `dev`: the author line over the act
        // already says `octo/hello`, and a title that said it again said the repo twice on
        // every start. The whole spelling is one hover away, and prose keeps it.
        testCase "a repo's sandbox under the repo's own attribution wears its bare name" <| fun () ->
            let by = ActorRef.Configured (RepoRef.create "octo/hello" |> expect)
            let html =
                Support.render
                    { representativeModel with
                        Conversation = { representativeModel.Conversation with Recent = List.rev ([ sandboxStartBy by ]) } }
            Expect.equal (sandboxNameOn html) "dev" "the scope is the author's, so the name drops it"

        // Under any other author the scope stays: the agent starting `octo/hello:dev` beside
        // `octo/other:dev` would otherwise read as two starts of one sandbox.
        testCase "a repo's sandbox under another author keeps its scope" <| fun () ->
            let html =
                Support.render
                    { representativeModel with
                        Conversation = { representativeModel.Conversation with Recent = List.rev ([ sandboxStartBy ActorRef.Agent ]) } }
            Expect.equal (sandboxNameOn html) "octo/hello:dev" "nothing over it says which repo, so the name does"

        // The one disclosure on a start still carries what the agent read, to the character
        // — the same promise the generic act keeps, kept by the layout that folds its facts.
        testCase "a sandbox start's disclosure still says what the agent was told" <| fun () ->
            let by = ActorRef.Configured (RepoRef.create "octo/hello" |> expect)
            let item = sandboxStartBy by
            let behind =
                Support.behindFold
                    (FoldKey.Act (MessageId.create "msg-dev" |> expect))
                    { representativeModel with
                        Conversation = { representativeModel.Conversation with Recent = List.rev ([ item ]) } }
            Expect.isTrue (behind.Contains "data-act-said") "the sentence is offered"
            let element = behind.Substring (behind.IndexOf "data-act-said")
            let text = Support.readable (element.Substring (element.IndexOf ">" + 1))
            Expect.equal text (ConversationItem.said item) "whole, scope and backend included"

        // A pull request a sentence points at leads to the pull request, like a repository
        // leads to its page: a real link, reachable by keyboard.
        testCase "a PR note's pull request is a reference that leads to it" <| fun () ->
            let pr = PrRef.create (RepoRef.create "octo/hello" |> expect) 12 |> expect
            let note : ConversationItem =
                { MessageId = MessageId.create "msg-pr" |> expect
                  Author = PeerRef ada
                  Content =
                    ItemContent.Act (Act.PrUnwatched { MessageId = MessageId.create "msg-pr" |> expect; Pr = pr; Actor = PeerRef ada })
                  Status = Complete
                  Offset = EventOffset.create 1L |> expect
                  Woke = None; CausedBy = None }
            let html =
                Support.render
                    { representativeModel with
                        Conversation = { representativeModel.Conversation with Recent = List.rev ([ note ]) } }
            let start = html.IndexOf (Dom.attr "data-entity" (EntityRef.said (EntityRef.Pr pr)))
            Expect.isTrue (start >= 0) "the pull request is its own element on the note"
            let opened = html.LastIndexOf ("<a ", start)
            let element = html.Substring (opened, html.IndexOf (">", start) - opened)
            Expect.isTrue (element.Contains "href=\"https://github.com/octo/hello/pull/12\"") "and it leads to the pull request"

        // The other half, and the reason the detail is an option rather than an empty
        // string: an act that is already one clause must not grow a blank second line under
        // it, which reads as something withheld.
        testCase "an act that is one clause has nothing under it" <| fun () ->
            let note : ConversationItem =
                { MessageId = MessageId.create "msg-plain-note" |> expect
                  Author = PeerRef ada
                  Content = ItemContent.Act (repoRemoved "octo/hello")
                  Status = Complete
                  Offset = EventOffset.create 1L |> expect
                  Woke = None; CausedBy = None }
            let model =
                { representativeModel with
                    Conversation = { representativeModel.Conversation with Recent = List.rev ([ note ]) } }
            let html = Support.render model
            Expect.isTrue ((Support.readable html).Contains "removed repo ") "the act reads as its sentence"
            Expect.isFalse (html.Contains "data-act-detail") "and no second line under it"

        // A turn nobody asked for (Plan 20, stage 2). The agent may now speak with nobody
        // having spoken to it, and on a shared surface that reads as the agent deciding
        // things on its own unless the item itself says otherwise. What is pinned is that
        // the mark is ON the woken item and on nothing else — not what it looks like.
        testCase "what a woken turn said is attributed as a turn nobody asked for" <| fun () ->
            let asked = MessageId.create "msg-asked" |> expect
            let woken = MessageId.create "msg-woken" |> expect
            let agentItem (messageId: MessageId) (woke: WakeReason option) : ConversationItem =
                { MessageId = messageId
                  Author = ActorRef.Agent
                  Content = ItemContent.Message ("the build finished")
                  Status = Complete
                  Offset = EventOffset.create 1L |> expect
                  Woke = woke; CausedBy = None }
            let model =
                { representativeModel with
                    Conversation =
                        { representativeModel.Conversation with
                            Recent = List.rev [ agentItem asked None; agentItem woken (Some CommandFinished) ] } }
            let html = Support.render model
            // Scoped to each article, because a whole-page render contains both and a bare
            // `Contains` would pass with the mark on the wrong one.
            let article (messageId: MessageId) =
                let start = html.IndexOf (sprintf "data-message-id=\"%s\"" (MessageId.value messageId))
                Expect.isTrue (start > 0) "the message renders"
                html.Substring (start, html.IndexOf ("</article>", start) - start)
            Expect.isTrue
                ((article woken).Contains Dom.Text.wokeCommandFinished)
                "the woken turn's message says why it exists"
            Expect.isFalse
                ((article asked).Contains "data-message-woke")
                "and a turn somebody asked for says nothing — there is nothing to explain"

        // The generated read surface (Plan 15). One renderer draws every query, so this
        // pins the RENDERER — a section per declared query, each shape drawn the way its
        // shape says — rather than any particular query's panel. A query added later gets
        // this behaviour without a line of view code, which is the property worth holding.
        testCase "the read surface renders a section per declared query, in every shape" <| fun () ->
            let html = Support.render representativeModel
            Expect.isTrue (html.Contains "data-query-panel=\"repos\"") "the rows query renders its section"
            Expect.isTrue (html.Contains "data-query-panel=\"work_environment\"") "the fields query renders its section"
            Expect.isTrue (html.Contains "data-query-panel=\"leases\"") "a query with no value yet still renders its section"
            Expect.isTrue (html.Contains "octo/hello") "a row's cells render"
            // The flag is rendered as a WORD, not a raw `true`: a human reads the answer
            // to "uncommitted changes", and `true` is the wire's word for it, not theirs.
            Expect.isTrue (html.Contains ">yes<") "a flag cell renders as a word"
            Expect.isFalse (html.Contains ">true<") "the wire's boolean does not reach the page"
            Expect.isTrue (html.Contains "data-query-pending") "an unanswered query says so rather than rendering nothing"
            // A query whose values are written in a vocabulary carries its legend into the
            // panel; one whose values are words carries none, so the surface does not fill
            // with headings explaining `octo/hello`.
            Expect.isTrue (html.Contains (Dom.attr Dom.Hooks.legend "1")) "the declared legend renders"
            Expect.isFalse (html.Contains (Dom.attr Dom.Hooks.legend "0")) "and a query without one renders no legend"

        // A tone is how loudly a value is said, never what it says. What is pinned is the
        // HOOK and the word — not the ink, which is how the surface is built and what a
        // redesign is allowed to change.
        // The pull requests' line in the sidebar's status block (the roster summary's twin, on
        // the page a person is looking at). What it promises is that a session which owes
        // something SAYS SO, and that pressing it reaches the rows behind the line.
        testCase "a session that owes something says so in its status block, and the line leads somewhere" <| fun () ->
            let html = Support.render (withPullRequests [ prRow 377 "queued" (CellStatus ("ok", ToneMuted)) ])
            let strip =
                let start = html.IndexOf "data-pr-strip"
                Expect.isTrue (start > 0) "the line renders"
                html.Substring (start, html.IndexOf ("</button>", start) - start)
            Expect.isTrue (strip.Contains "#377 queued") "wearing the line the query's rows amount to"
            Expect.isTrue (strip.Contains "pull requests: ") "and a screen reader hears what the line is about"

        // Silence is the feature, here as on the roster: a strip that was always there would
        // be a thing to stop seeing, and the point is that its presence means something.
        testCase "a session with nothing owed has no pull requests' line at all" <| fun () ->
            let quiet = Support.render (withPullRequests [ prRow 1 "merged" (CellStatus ("ok", ToneMuted)) ])
            Expect.isFalse (quiet.Contains "data-pr-strip") "watches that have all merged owe nothing"
            let none = Support.render representativeModel
            Expect.isFalse (none.Contains "data-pr-strip") "and a session watching nothing says nothing"

        // The attention mark ranks by SHAPE (docs/visual-design.md, Attention): each face is
        // told apart without its colour. Working alone wears the agent's own cube — not a
        // point at all, the very mark that turns inline in the transcript — and Pending wears
        // the same quarter-arc spin mark every other "still going" does (`Icon.spin`). What is
        // pinned is what each face draws, so two levels collapsing into one shape goes red
        // here, and so does the agent's cube losing its own turn.
        testCase "each attention level draws its own face, and only work under way keeps moving" <| fun () ->
            let levels =
                [ Standings.Rest; Standings.Absent; Standings.Pending; Standings.Working; Standings.Wanted; Standings.Wrong ]
            let drawn level = Support.renderTemplate (Icon.attention level)
            // The face, with the box and ink stripped: what is left is the shape alone. Working
            // draws a `<span>`, not an `<svg>`, so the split point is `aria-hidden`, which both
            // kinds of mark carry, rather than `viewBox`, which only one kind does.
            let shape (html: string) = html.Substring (html.IndexOf "aria-hidden")
            let faces = levels |> List.map (drawn >> shape)
            Expect.equal (List.length (List.distinct faces)) 6 "six levels, six shapes"
            Expect.notEqual
                (Yession.App.Style.attentionInk Standings.Working)
                (Yession.App.Style.attentionInk Standings.Pending)
                "whoever's work it is, the ink still says so too"
            for level in levels do
                let html = drawn level
                Expect.stringContains
                    html
                    (sprintf "data-attention=\"%s\"" (Standings.Attention.name level))
                    "each spells its level for a test"
                Expect.equal
                    (html.Contains Yession.App.Style.statusSpinCw)
                    (level = Standings.Pending)
                    (sprintf "%s spins only if it is a machine's wait" (Standings.Attention.name level))
                Expect.equal
                    (html.Contains Yession.App.Style.agentThinkingCube)
                    (level = Standings.Working)
                    (sprintf "%s turns the agent's own cube only if it is the agent's own" (Standings.Attention.name level))
            // The ones that ask arrive once; the ones that do not, do not.
            let arrives level =
                let html = drawn level
                html.Contains Yession.App.Style.markFill || html.Contains Yession.App.Style.markPop
            Expect.equal
                (levels |> List.filter arrives)
                [ Standings.Wanted; Standings.Wrong ]
                "only Wanted and Wrong arrive with a motion of their own"

        // The agent's standing rides beside the pull requests' line, under the rule the
        // Manager is sent (`AgentStatus`), read off what this page already holds.
        testCase "the status block says what the agent is doing" <| fun () ->
            let said (model: ClientModel) =
                let html = Support.render model
                let start = html.IndexOf "data-agent-standing"
                Expect.isTrue (start > 0) "the agent's standing renders"
                html.Substring (start, html.IndexOf ("</div>", start) - start)
            let noTurn = { representativeModel with Agent = { representativeModel.Agent with ActiveTurn = None } }
            let connected = noTurn |> withClaudePanel (fun panel -> { panel with AgentAvailable = true })
            Expect.stringContains (said connected) "waiting" "idle is the ball with the people"
            Expect.stringContains (said { connected with Agent = representativeModel.Agent }) "working" "a turn is work"
            Expect.stringContains (said noTurn) "no agent" "and a session nothing can answer says so"

        // The band is the title's room at every width. It says no standing in words; it
        // wears the worst one's mark under the chevron that opens the block, and says its
        // word in the chevron's name.
        testCase "the header wears only the worst standing, as a mark on the way to the rest" <| fun () ->
            let noTurn =
                { (withPullRequests [ prRow 377 "conflicted" (CellStatus ("ok", ToneMuted)) ]) with
                    Agent = { representativeModel.Agent with ActiveTurn = None } }
            let connected = noTurn |> withClaudePanel (fun panel -> { panel with AgentAvailable = true })
            let html = Support.render connected
            let band =
                // The session's band, found by the chevron it holds: a message group has a
                // `<header>` of its own.
                let start = html.LastIndexOf ("<header", html.IndexOf "data-nav-toggle=\"show\"")
                html.Substring (start, html.IndexOf ("</header>", start) - start)
            Expect.isFalse (band.Contains "data-pr-strip") "the pull requests' line is not in the band"
            Expect.isFalse (band.Contains "data-agent-standing") "nor the agent's"
            let mark =
                let start = band.IndexOf "data-reopen-attention"
                Expect.isTrue (start > 0) "the chevron carries a mark"
                band.Substring (start, band.IndexOf ("</svg>", start) - start)
            Expect.stringContains
                mark
                (sprintf "data-attention=\"%s\"" (Standings.Attention.name Standings.Wrong))
                "the conflicted pull request's, which outranks a waiting agent"
            Expect.stringContains band "aria-label=\"Show sidebar, #377" "and the chevron says it"
            let blank = Support.render { representativeModel with Claude = { representativeModel.Claude with Status = None } }
            Expect.isFalse (blank.Contains "data-reopen-attention") "a session with nothing to say wears no mark"

        // The block says what the agent is doing; the agent's row says who is here. Said once.
        testCase "the status block sits above the people, and the agent's row does not repeat it" <| fun () ->
            let noTurn = { representativeModel with Agent = { representativeModel.Agent with ActiveTurn = None } }
            let html = Support.render (noTurn |> withClaudePanel (fun panel -> { panel with AgentAvailable = true }))
            let block, people = html.IndexOf "data-standings", html.IndexOf ">in this session<"
            Expect.isTrue (block > 0 && people > block) "the block comes first"
            let row =
                let start = html.IndexOf (Dom.attr Dom.Hooks.agentPresence "live")
                html.Substring (start, html.IndexOf ("</div>", start) - start)
            Expect.isFalse (row.Contains "waiting") "the agent's row leaves the word to the block"

        // The one rule the strip applies that the rows do not state outright, and the reason
        // it reads the status cell's TONE rather than its sentence: the words in a health
        // line belong to a provider and change with it.
        testCase "a watch the session cannot read is said to be unreachable, over what it last said" <| fun () ->
            let html =
                Support.render (
                    withPullRequests
                        [ prRow 377 "queued" (CellStatus ("github rejected this credential", ToneBad)) ])
            Expect.isTrue (html.Contains "#377 unreachable") "the strip says nobody is driving it"
            Expect.isFalse (html.Contains "#377 queued") "and not the state it is stuck in"

        // --- Chapters in the timeline -------------------------------------------------

        // Where a chapter opens, a rule stands — one, on the item it opens at, and none
        // anywhere else. This replaced a rail whose hairlines were placed by measuring the
        // laid-out page; what a rule promises instead is only that it is in the flow, and
        // this is the half of that a rendered string can see.
        testCase "a rule stands where a chapter opens, and nowhere else" <| fun () ->
            let bare = Support.render representativeModel
            Expect.isFalse (bare.Contains Dom.Hooks.chapterRule) "no chapters, no rules"
            let divided = Support.render (withChapters [ "msg-1"; "msg-agent" ])
            Expect.equal (occurrences (Dom.attr Dom.Hooks.chapterRule "msg-1") divided) 1 "one rule for one chapter"
            Expect.equal (occurrences (Dom.attr Dom.Hooks.chapterRule "msg-agent") divided) 1 "and one for the other"

        // A rule is a divider across the session, so it cannot be a member of an author's
        // group: folded into one it would be a line drawn inside somebody's turn, under their
        // name. The group head is what a run of messages by one actor opens with, so a rule
        // that broke out of the run leaves the message it opens at starting a new one.
        testCase "a chapter breaks the run of messages it opens in" <| fun () ->
            // A run to break, and nothing else on the timeline: consecutive messages by one
            // person fold under one author line, and the representative session has no two of
            // them in a row — a block sits between its only pair.
            let saidBy id at =
                { MessageId = MessageId.create id |> expect
                  Author = PeerRef ada
                  Content = ItemContent.Message ("ship it")
                  Status = Complete
                  Offset = EventOffset.create at |> expect
                  Woke = None; CausedBy = None }
            let first = saidBy "msg-a" 1L
            let next = saidBy "msg-b" 2L
            let joined =
                { representativeModel with
                    Conversation = { ConversationProjection.empty with Recent = List.rev ([ first; next ]) }
                    Timeline = TimelineProjection.empty }
            let divided =
                { joined with
                    Synced =
                        { joined.Synced with
                            Chapters = Map.ofList [ next.MessageId, { Opens = true; Name = Ylmish.Text.empty } ] } }
            let runs (model: ClientModel) =
                occurrences (Dom.attr Dom.Hooks.messageAuthor (PeerId.value ada)) (Support.render model)
            Expect.equal (runs divided) (runs joined + 1) "the run is split, rather than the rule joining it"

        // The contents: the list is what a phone gets instead of a gutter, and what a desktop
        // gets instead of scrolling to find a chapter. Its order is the CONVERSATION's, not
        // the order chapters were made in — a reader walking the list is walking the session.
        testCase "the contents list every chapter, in the order the session holds them" <| fun () ->
            let html = Support.render (withChapters [ "msg-agent"; "msg-1" ])
            let entry id = html.IndexOf (Dom.attr Dom.Hooks.chapterEntry id)
            Expect.isTrue (entry "msg-1" >= 0 && entry "msg-agent" >= 0) "both chapters are listed"
            Expect.isTrue (entry "msg-1" < entry "msg-agent") "oldest first, whatever order they were made in"

        // A heading over nothing teaches a reader to skip the place the list will appear.
        testCase "a session nobody has divided has no contents" <| fun () ->
            Expect.isFalse
                ((Support.render representativeModel).Contains Dom.Hooks.chapters)
                "no chapters, no contents"

        // The environment says what is running in it. Every OPEN terminal is an entry — the
        // pane is one press from the index, rather than only from the "n terminals" over the
        // conversation.
        testCase "the environment lists every open terminal" <| fun () ->
            let html =
                Support.render
                    (withTerminals
                        [ "term-idle", Some (BlockFinished (CommandSucceeded 0)), true
                          "term-run", Some BlockRunning, true ])
            Expect.isSome (environmentEntry "term-idle" html) "an idle terminal is listed"
            Expect.isSome (environmentEntry "term-run" html) "and a busy one"

        // ONE vocabulary for a terminal's state (`View.terminalMark`): the entry wears the mark
        // the pivot does, so a failure reads the same in the index as in the pane.
        testCase "an environment entry wears its terminal's state mark" <| fun () ->
            let html =
                Support.render
                    (withTerminals
                        [ "term-idle", Some (BlockFinished (CommandSucceeded 0)), true
                          "term-run", Some BlockRunning, true
                          "term-fail", Some (BlockFinished (CommandFailed 2)), true ])
            let mark id =
                environmentEntry id html
                |> Option.map (fun entry ->
                    match entry.IndexOf (Dom.Hooks.paneMark + "=\"") with
                    | -1 -> "none"
                    | at ->
                        let from = at + Dom.Hooks.paneMark.Length + 2
                        entry.Substring (from, entry.IndexOf ("\"", from) - from))
            Expect.equal
                ([ "term-idle"; "term-run"; "term-fail" ] |> List.map mark)
                [ Some "none"; Some "running"; Some "failed" ]
                "idle says nothing, running pulses, a failed last command says so"

        // What is RUNNING: a closed terminal is a recording, and the `all` page offers those.
        testCase "a closed terminal is not listed under the environment" <| fun () ->
            let html =
                Support.render
                    (withTerminals
                        [ "term-live", None, true
                          "term-gone", Some (BlockFinished (CommandSucceeded 0)), false ])
            Expect.isNone (environmentEntry "term-gone" html) "closed, so not running in it"

        // A heading over nothing teaches a reader to skip the place the list will appear.
        testCase "an environment with no open terminal lists none" <| fun () ->
            let html = Support.render (withTerminals [ "term-gone", None, false ])
            Expect.isFalse (html.Contains Dom.Hooks.environmentTerminals) "no open terminals, no list"

        // Not drawn: a reader that is not a person. A browser case waiting for a command to run,
        // on a box whose work sandbox did not start, reads the product's own reason off the
        // element that says the environment failed (`Refusal.sandboxDidNotStart`), in one look
        // and not two — so there is no moment at which the page states a failure and not yet why.
        testCase "a failed environment carries the reason it gave on the element that says it failed" <| fun () ->
            let reason = "srt sandbox failed: no user namespace"
            let html = Support.render { withTerminals [] with Environment = EnvironmentFailed reason }
            Expect.equal
                (openingTagWith (Dom.attr Dom.Hooks.environment Dom.Text.envFailed) html
                 |> Option.map (fun tag -> tag.Contains (Dom.attr Dom.Hooks.environmentReason reason)))
                (Some true)
                "the element that says it failed, wearing the reason"

        // The reason is a value in an attribute, so a quote in it must not end the attribute.
        testCase "a reason with a quote in it is carried whole" <| fun () ->
            let html =
                Support.render { withTerminals [] with Environment = EnvironmentFailed "bwrap: \"/x\" & more" }
            Expect.equal
                (openingTagWith (Dom.attr Dom.Hooks.environment Dom.Text.envFailed) html
                 |> Option.map (fun tag ->
                     tag.Contains (Dom.attr Dom.Hooks.environmentReason "bwrap: &quot;/x&quot; &amp; more")))
                (Some true)
                "escaped as every attribute the view renders is"

        // An environment started again is not still blamed for the last start.
        testCase "an environment that has not failed says no reason" <| fun () ->
            let model = withTerminals []
            let blamed =
                [ EnvironmentNotStarted, Dom.Text.envNotStarted
                  EnvironmentStarting, Dom.Text.envStarting
                  EnvironmentRunning "srt", Dom.Text.envRunning
                  EnvironmentDown, Dom.Text.envStopped ]
                |> List.filter (fun (status, label) ->
                    let html = Support.render { model with Environment = status }
                    match openingTagWith (Dom.attr Dom.Hooks.environment label) html with
                    | Some tag -> not (tag.Contains (Dom.attr Dom.Hooks.environmentReason ""))
                    | None -> true)
                |> List.map snd
            Expect.isEmpty blamed "every other state wears an empty reason"

        // A phone's drawer has to keep `settings` on screen, so the list stops at a cap and the
        // rest are one press away on the `all` page — counted, so the door says what is behind it.
        testCase "past its cap the environment says how many more terminals there are" <| fun () ->
            let html =
                Support.render
                    (withTerminals [ for n in 1..8 -> sprintf "term-%d" n, None, true ])
            Expect.equal
                (occurrences (Dom.Hooks.environmentTerminal + "=") html, occurrences (Dom.attr Dom.Hooks.environmentMore "3") html)
                (5, 1)
                "five listed, and a way to the other three"

        // What a rule says is the SESSION's answer (`Chapters.name`), not this surface's: two
        // surfaces computing a name apiece are two surfaces that can call one chapter two
        // things. The cut, the guess and the stripped markdown are the domain's, and are
        // pinned there.
        testCase "a rule wears the name the session holds for its chapter" <| fun () ->
            let messageId = MessageId.create "msg-1" |> expect
            let model = withChapters [ "msg-1" ]
            let named =
                { model with
                    Synced =
                        { model.Synced with
                            Chapters =
                                model.Synced.Chapters
                                |> Map.add messageId { Opens = true; Name = Ylmish.Text.ofString "Where it was settled" } } }
            let item = named.Conversation.Items |> List.find (fun i -> i.MessageId = messageId)
            Expect.equal (ClientModel.chapterName named item) "Where it was settled" "theirs, not a second guess"
            Expect.isTrue ((Support.render named).Contains "Where it was settled") "and it is on the page"

        // The divider stands directly above the message it opens, so a name that is only that
        // message's own words (`Chapters.defaultName`) says one sentence twice, a line apart.
        // The rule of a chapter somebody named keeps its name; one still wearing the guess is a
        // plain divider, and the contents — where nothing sits beside an entry — keep the guess.
        let ruleOf (messageId: string) (html: string) =
            let start = html.IndexOf (Dom.attr Dom.Hooks.chapterRule messageId)
            Expect.isTrue (start >= 0) "the rule renders"
            html.Substring (start, html.IndexOf ("</div>", start) - start)

        testCase "a rule over a chapter nobody named does not repeat its opening message" <| fun () ->
            Expect.isFalse
                ((ruleOf "msg-1" (Support.render (withChapters [ "msg-1" ]))).Contains "ship it")
                "the guess is the message under the rule, so the rule leaves it unsaid"

        // `Chapters.toggle` writes the guess into the session when a chapter is made, so a
        // chapter somebody opened by hand holds written words from its first moment. Words
        // that are still exactly the guess are not a name.
        testCase "a rule over a chapter holding only the guess does not repeat it either" <| fun () ->
            let model = withChapters [ "msg-1" ]
            let seeded =
                { model with
                    Synced =
                        { model.Synced with
                            Chapters =
                                model.Synced.Chapters
                                |> Map.add
                                    (MessageId.create "msg-1" |> expect)
                                    { Opens = true; Name = Ylmish.Text.ofString "ship it" } } }
            Expect.isFalse
                ((ruleOf "msg-1" (Support.render seeded)).Contains "ship it")
                "the seeded guess is still the guess"

        testCase "a rule over a chapter somebody named wears that name" <| fun () ->
            let model = withChapters [ "msg-1" ]
            let named =
                { model with
                    Synced =
                        { model.Synced with
                            Chapters =
                                model.Synced.Chapters
                                |> Map.add
                                    (MessageId.create "msg-1" |> expect)
                                    { Opens = true; Name = Ylmish.Text.ofString "Where it was settled" } } }
            Expect.isTrue
                ((ruleOf "msg-1" (Support.render named)).Contains "Where it was settled")
                "a name that says more than the message does belongs on the rule"

        // A pull request's own milestone opens a chapter BY NATURE (`AutoChapters`), which
        // never gets an entry in the doc and so wears its guess for good
        // (`AutoChapters.fs`, "written to be kept"). That guess ("Merged PR #…") is never
        // the sentence the merge card itself prints, so the rule's usual dedup — leave a
        // guess unsaid because the message repeats it — does not apply here, and hiding it
        // anyway would be the chapter's only name, gone.
        testCase "a rule over a chapter a pull request opened by itself wears its guess" <| fun () ->
            let pr = PrRef.create (RepoRef.create "octo/hello" |> expect) 12 |> expect
            let merged : ConversationItem =
                { MessageId = MessageId.create "msg-1" |> expect
                  Author = PeerRef ada
                  Content =
                    ItemContent.Act (
                        Act.PrTransitioned
                            { MessageId = MessageId.create "msg-1" |> expect
                              Pr = pr
                              Transition = PrTransition.Merged
                              State = PrMerged
                              Checks = ChecksGreen
                              Watcher = Principal.Peer ada
                              OccurredAt = None
                              Title = Some "Add feature" })
                  Status = Complete
                  Offset = EventOffset.create 1L |> expect
                  Woke = None; CausedBy = None }
            let model =
                { representativeModel with
                    Conversation = { representativeModel.Conversation with Recent = List.rev [ merged ] } }
            Expect.isTrue
                ((ruleOf "msg-1" (Support.render model)).Contains "Merged PR #12 Add feature")
                "nobody will ever name this chapter, so its only name had better be on the rule"

        testCase "the contents still name a chapter nobody named by its guess" <| fun () ->
            let html = Support.render (withChapters [ "msg-1" ])
            let start = html.IndexOf (Dom.attr Dom.Hooks.chapterEntry "msg-1")
            Expect.isTrue (start >= 0) "the entry renders"
            Expect.isTrue
                (html.Substring(start, html.IndexOf ("</button>", start) - start).Contains "ship it")
                "an entry with no message beside it needs the guess to be told apart"

        // A chapter can sit on a message that has not said anything yet — a turn that started
        // and wrote nothing. A control named by an empty string is announced as "button".
        testCase "a chapter on a message with nothing in it still names its control" <| fun () ->
            let silent =
                { MessageId = MessageId.create "msg-silent" |> expect
                  Author = PeerRef ada
                  Content = ItemContent.Message ("")
                  Status = Complete
                  Offset = EventOffset.create 9L |> expect
                  Woke = None; CausedBy = None }
            Expect.equal
                (ClientModel.chapterName representativeModel silent)
                Dom.Text.unnamedChapter
                "a name, rather than nothing to announce"

        // "Divide it anywhere" is the promise, so the control is on every item that has an id —
        // a message and an act alike. Chapters chosen for the reader would be somebody else's
        // account of the session rather than the reader's own.
        testCase "every item in the timeline offers its actions, said and done alike" <| fun () ->
            // Both kinds explicitly: the representative fixture is all messages, so a case
            // counting only it would pass with the control missing from every act note in the
            // product — which is exactly half of what a timeline holds.
            let item id content : ConversationItem =
                { MessageId = MessageId.create id |> expect
                  Author = PeerRef ada
                  Content = content
                  Status = Complete
                  Offset = EventOffset.create 1L |> expect
                  Woke = None; CausedBy = None }
            let model =
                { representativeModel with
                    Conversation =
                        { representativeModel.Conversation with
                            Recent =
                                List.rev
                                    [ item "said" (ItemContent.Message "something happened")
                                      item "done" (ItemContent.Act (repoRemoved "octo/hello")) ] } }
            let html = Support.render model
            Expect.equal (occurrences "data-message-id=" html) 2 "a message and an act"
            Expect.equal (occurrences "data-item-actions=" html) 2 "one control per item, none left out"

        // A menu entry is READ before it is chosen, so it can say which way it goes — which a
        // toggle wearing `aria-pressed` could not, since a name that flips as well as a state
        // announces itself twice and in two voices.
        testCase "the chapter entry names which way it will go" <| fun () ->
            let opened (marks: string list) : string =
                Support.render { (withChapters marks) with ItemMenu = Some (MessageId.create "msg-1" |> expect) }
            Expect.isTrue ((opened [ "msg-1" ]).Contains Dom.Text.removeChapter) "an item that opens one offers to close it"
            Expect.isTrue ((opened []).Contains Dom.Text.makeChapter) "one that does not offers to open it"

        // The menu exists only while it is open. Kept in the document and hidden, a twenty
        // message conversation would ship twenty menus to the accessibility tree, and twenty
        // more with every message that arrives.
        testCase "an item's menu is in the document only while it is open" <| fun () ->
            Expect.isFalse
                ((Support.render representativeModel).Contains Dom.Hooks.itemMenu)
                "nothing open, no menu anywhere"
            let html =
                Support.render
                    { representativeModel with ItemMenu = Some (MessageId.create "msg-1" |> expect) }
            Expect.equal (occurrences (Dom.Hooks.itemMenu + "=") html) 1 "and exactly one when one is"

        // The tab title is the only surface that reaches somebody who is not looking at this
        // session at all, so what it says has to survive being read out of the corner of an
        // eye. These pin the base string first: a prefix test whose red could also mean "the
        // title broke" is a prefix test nobody can read.
        testCase "a tab is named for its session, and says nothing more when nothing is owed" <| fun () ->
            Expect.equal
                (ClientModel.tabTitle representativeModel)
                "planning the launch · yession"
                "a quiet session wears its title and no mark"
            Expect.equal
                (ClientModel.tabTitle (withPullRequests [ prRow 1 "queued" (CellStatus ("ok", ToneMuted)) ]))
                "planning the launch · yession"
                "and a pull request merely on its way in is not an interruption"

        // Checks going red is deliberately NOT a signal: the agent is already fixing that,
        // and a mark that fires while somebody is handling it is a mark people learn to
        // ignore before it ever means something.
        testCase "a tab warns when a pull request has stopped moving and nobody will notice" <| fun () ->
            Expect.equal
                (ClientModel.tabTitle (withPullRequests [ prRow 377 "stalled" (CellStatus ("ok", ToneMuted)) ]))
                "⚠ planning the launch · yession"
                "a stall raises no event of its own, so the tab is where it surfaces"
            Expect.equal
                (ClientModel.tabTitle (
                    withPullRequests [ prRow 377 "queued" (CellStatus ("github rejected this credential", ToneBad)) ]))
                "⚠ planning the launch · yession"
                "and a watch nobody can read has stopped moving too"

        // The one agent state nobody but a person can end. Working and waiting are the agent
        // getting on with it, for the red suite's reason. A turn in flight does not clear it:
        // the page reads one agent standing, and a credential that needs signing in again
        // fails the NEXT turn whatever this one is doing.
        testCase "a tab warns when the agent cannot run until somebody signs in again" <| fun () ->
            let needing = Some { Kind = StaticConnection; SignInRequired = Some "token expired" }
            let stuck =
                { representativeModel with Agent = { representativeModel.Agent with ActiveTurn = None } }
                |> withClaudePanel (fun panel -> { panel with MineCredential = needing; AgentAvailable = true })
            Expect.equal (ClientModel.tabTitle stuck) "⚠ planning the launch · yession" "a sign-in only a person can give"
            Expect.equal
                (ClientModel.tabTitle { stuck with Agent = representativeModel.Agent })
                "⚠ planning the launch · yession"
                "and a running turn does not hide it"

        testCase "a tab ticks when something merged, until the watch goes away" <| fun () ->
            Expect.equal
                (ClientModel.tabTitle (withPullRequests [ prRow 1 "merged" (CellStatus ("ok", ToneMuted)) ]))
                "✓ planning the launch · yession"
                "the one thing worth telling somebody who stopped watching: stop waiting"
            Expect.equal
                (ClientModel.tabTitle representativeModel)
                "planning the launch · yession"
                "and unwatching it is what clears the tick"

        // A tab says one thing. The one that needs a person is the one that has stopped.
        testCase "a warning outranks a tick when both are true at once" <| fun () ->
            Expect.equal
                (ClientModel.tabTitle (
                    withPullRequests
                        [ prRow 1 "merged" (CellStatus ("ok", ToneMuted))
                          prRow 2 "stalled" (CellStatus ("ok", ToneMuted)) ]))
                "⚠ planning the launch · yession"
                "finished work waits; stopped work does not"

        testCase "a cell that carries a verdict says so, and still says the word" <| fun () ->
            let html = Support.render representativeModel
            Expect.isTrue (html.Contains "data-query-tone=\"bad\"") "a row's toned cell reports its tone"
            Expect.isTrue (html.Contains "data-query-tone=\"busy\"") "and so does a field's"
            // Colour is never the only signal: a reader who cannot see it reads the value.
            Expect.isTrue (html.Contains "checks red") "the word is still on the page"
            Expect.isTrue (html.Contains ">running<") "and so is the field's"
            // The hook means something only if an ordinary cell does not claim one.
            Expect.isTrue (html.Contains "data-query-tone=\"\"") "an ordinary cell claims no tone, so the hook means something"

        // The lane a query lands in is 280px wide at every breakpoint, so a row is drawn as
        // a RECORD — named by its first column, with the rest as labelled facts — rather
        // than as a table row that only a horizontal scroll could reach the end of. What is
        // pinned is that the record still carries the WHOLE row: an identity column dropped
        // from the pairs must reappear as the name, and no other column may go missing.
        testCase "a row is one record, named by its first column, carrying every other one" <| fun () ->
            let html = Support.render representativeModel
            Expect.isTrue (html.Contains "data-query-row=\"octo/hello\"") "the record is named by its first column"
            let record =
                let start = html.IndexOf "data-query-row=\"octo/hello\""
                Expect.isTrue (start > 0) "the record renders"
                html.Substring (start, html.IndexOf ("</dl>", start) - start)
            for label, value in [ "branch", "main"; "checks", "checks red"; "uncommitted changes", "yes" ] do
                Expect.isTrue (record.Contains (sprintf ">%s<" label)) (sprintf "'%s' is labelled" label)
                Expect.isTrue (record.Contains (sprintf ">%s<" value)) (sprintf "'%s' is on the page" value)
            // The name is not said twice: a record headed "octo/hello" with a "repo:
            // octo/hello" pair under it spends two lines of a narrow lane saying one thing.
            Expect.isFalse (record.Contains ">repo<") "the naming column is not also a labelled pair"

        // A record is the lane's whole answer about its subject, so a label with a dash under
        // it reads as something missing rather than as nothing to report. The sandbox that
        // nobody declared was the case: "declared by" over "—".
        let recordWithAbsent () =
            let model =
                { representativeModel with
                    Queries =
                        { representativeModel.Queries with
                            Values =
                                representativeModel.Queries.Values
                                |> Map.add
                                    "repos"
                                    (RowsOf
                                        [ [ "repo", CellText "octo/hello"
                                            "branch", CellText "main"
                                            "checks", CellStatus ("checks red", ToneBad)
                                            "dirty", CellAbsent ] ]) } }
            let html = Support.render model
            let start = html.IndexOf "data-query-row=\"octo/hello\""
            Expect.isTrue (start > 0) "the record renders"
            html.Substring (start, html.IndexOf ("</dl>", start) - start)

        testCase "a record leaves out the fact it has nothing to say about" <| fun () ->
            Expect.isFalse
                ((recordWithAbsent ()).Contains ">uncommitted changes<")
                "no label is drawn over a dash"

        testCase "a record keeps the facts it does have when one is left out" <| fun () ->
            let record = recordWithAbsent ()
            Expect.isTrue (record.Contains ">branch<") "the branch is still labelled"
            Expect.isTrue (record.Contains ">checks<") "and so are the checks"

        // The one pending act, at both mount points (Plan 15, stage 3c). What is worth
        // pinning is that the CHAT carries the terminal's pending commands too: the whole
        // claim of this stage is that reading what the agent is about to run is the same act
        // as reading what it is about to say, and a card only a panel shows would quietly
        // make it a different one again.
        //
        // In the CONVERSATION rather than in a dock of its own above the composer, which is
        // the half that changed: a command about to run is the same kind of act as one that
        // has run, so it reads as one — the chip its block will leave behind, in the place
        // that block will land.
        testCase "what is about to run reads as a chip in the conversation" <| fun () ->
            let html = Support.render representativeModel
            // The representative model's one pending act is the AGENT's terminal command
            // under the default mode — the case the gate exists for.
            let chip = html.IndexOf (Dom.attr Dom.Hooks.chatPending (QueueId.value terminalQueueId))
            let timeline = html.IndexOf Dom.Hooks.conversation
            let dock = html.IndexOf Dom.Hooks.messageQueue
            Expect.isTrue (chip > 0) "the chat carries a chip for what is queued"
            Expect.isTrue (timeline >= 0 && dock > timeline) "the timeline and the composer's dock both render"
            Expect.isTrue (chip > timeline && chip < dock) "the chip is in the timeline, not in a dock above the composer"
            Expect.isTrue (html.Contains "data-pending-subject") "and says which terminal it is queued in"
            // No author line of its own: it wears the group's, exactly as a block chip does.
            // A chip that named its own author would be the one act in the conversation whose
            // attribution came from somewhere else.
            let before = html.Substring (timeline, chip - timeline)
            let named = before.LastIndexOf Dom.Hooks.messageAuthor
            Expect.isTrue (named > 0) "something above it says whose turn this is"
            Expect.isTrue
                ((before.Substring named).StartsWith (Dom.attr Dom.Hooks.messageAuthor Dom.Text.agent))
                "and it is the agent's, because the agent is what queued this"

        // The structure an answer owes a screen reader (src/Yession.App/AGENTS.md, UI baseline).
        // Held in the renderer, so it is held for every query — the reason the surface is generated.
        //
        // A description list, since a row became a record: at 280px there are no columns to
        // compare down, so a `<table>` would claim a structure the eye cannot see, and it is
        // `<dt>`/`<dd>` that says which label a value answers to. (This case used to assert
        // `<table>` and `scope="col"`, which was the same promise in the shape the renderer
        // happened to have.)
        testCase "a query's values are structurally bound to the labels they answer to" <| fun () ->
            let html = Support.render representativeModel
            Expect.isTrue (html.Contains "<dl") "an answer's pairs are a description list"
            Expect.isTrue (html.Contains "<dt") "each value has a term naming it"
            Expect.isTrue (html.Contains "uncommitted changes") "the column's human label is the term, not its wire key"

        // What Plan 15 RETIRED: the panel's write actions. A human asks the agent, so
        // there is one authorization path and one place the act is recorded.
        testCase "the read surface offers no way to mutate anything" <| fun () ->
            let html = Support.render representativeModel
            Expect.isFalse (html.Contains "data-repo-add-input") "the add input is gone"
            Expect.isFalse (html.Contains "data-repo-remove") "the remove control is gone"
            Expect.isFalse (html.Contains "data-repo-switch") "the branch switch is gone"
    ]

// The terminal list (Plan 20, stage 0). What is pinned here is AVAILABILITY — which verbs a
// row offers over which state — and nothing about how a row looks: the marks, the tones and
// the order of the controls are the design, and a test that quoted them would go red on the
// next improvement while saying the design was wrong.
let private terminalListTests =
    testList "The terminal list" [
        // Unfolded: which verbs a row offers is the question here, and a closed row's are
        // under the fold (`ClosedShown`).
        let listed (model: ClientModel) = Support.render { model with Switcher = true; ClosedShown = true }
        let id = TerminalId.value terminalId

        // Every case below is the same shape deliberately: the verb where it works, and its
        // absence where it does not. Only asserting the presence would leave a row that
        // offered everything to everyone looking correct.

        testCase "a row offers the kill while its terminal is running, and never once it has stopped" <| fun () ->
            Expect.isTrue
                ((listed representativeModel).Contains (Dom.attr Dom.Hooks.terminalClose id))
                "a running terminal can be killed from its row"
            Expect.isFalse
                ((listed closedTerminalModel).Contains (Dom.attr Dom.Hooks.terminalClose id))
                "a closed one has nothing left to kill"

        // (A row used to offer its terminal's rewind, and a closed one its replay. Both are the
        // terminal's own now, on its toolbar; the row opens the terminal.)

        testCase "a row offers the way back only where a provider said there is one" <| fun () ->
            Expect.isTrue
                ((listed renewableTerminalModel).Contains (Dom.attr Dom.Hooks.terminalReattach id))
                "a closed stream whose provider allows asking again"
            Expect.isFalse
                ((listed closedTerminalModel).Contains (Dom.attr Dom.Hooks.terminalReattach id))
                "and never for a shell terminal, which has no provider to ask"

        testCase "a recording the cap ate is stated on its row rather than left to look empty" <| fun () ->
            // The gap is the one state with no mark of its own, because the absence of a
            // recording has no glyph — so it is the one that says a word.
            Expect.isTrue
                ((listed forgottenTerminalModel).Contains (Dom.attr Dom.Hooks.terminalListGone id))
                "the hole in the audit trail is said"
            Expect.isFalse
                ((listed closedTerminalModel).Contains (Dom.attr Dom.Hooks.terminalListGone id))
                "and a recording that survived says nothing of the sort"

        // Unknown is not lost. A reader who arrived after the terminal closed holds nothing of
        // its recording until a read answers, and the row used to call that a hole.
        testCase "a closed terminal's row says nothing of a recording not yet heard back about" <| fun () ->
            let unheard = { forgottenTerminalModel with TerminalFeeds = Map.empty }
            Expect.isFalse
                ((listed unheard).Contains (Dom.attr Dom.Hooks.terminalListGone id))
                "nothing is known to be missing"

        // "the strip and the list are never on screen together" was here: the list REPLACED
        // the pane then, and a tablist left standing over it promised a panel that was not in
        // the document. The switcher (P2-2) is a popover over the pane, panel and all, so the
        // strip under it is telling the truth.

        testCase "every terminal the session has had is reachable from the list, open or not" <| fun () ->
            // The reason the strip can stop being a census (Plan 20, stage 1): the row IS the
            // way to a closed terminal's recording, so nothing is lost by dropping it from
            // the strip.
            Expect.isTrue
                ((listed closedTerminalModel).Contains (Dom.attr Dom.Hooks.terminalListRow id))
                "a closed terminal has a row that opens it"

        // Folded, a closed terminal's row is one press away, and the press is there: a fold
        // over nothing to open would be a session's record with no door.
        testCase "a closed terminal the list folds away is under a fold that says it is shut" <| fun () ->
            let html = Support.render { closedTerminalModel with Switcher = true; Pane = None }
            let fold = System.Text.RegularExpressions.Regex.Match(html, "<button[^>]*data-list-closed-fold[^>]*>").Value
            Expect.stringContains fold "aria-expanded=\"false\"" "a fold, shut, over the closed terminal"

        // A stop is one press, so its accessible name has to say what that press ends: a
        // screen reader hears what a sighted reader sees on the row.
        testCase "a row's stop names the terminal it will end" <| fun () ->
            let title =
                Projection.tryFind terminalId representativeModel.Terminals
                |> Option.map (fun view -> TerminalTitle.value view.Title)
                |> Option.defaultWith (fun () -> failwith "the fixture has its terminal")
            let stop =
                System.Text.RegularExpressions.Regex.Match(
                    listed representativeModel,
                    sprintf "<button[^>]*%s=\"%s\"[^>]*>" Dom.Hooks.terminalClose (TerminalId.value terminalId)).Value
            let label = System.Text.RegularExpressions.Regex.Match(stop, "aria-label=\"([^\"]*)\"").Groups.[1].Value
            Expect.stringContains label title "the stop says which terminal it ends"

        // A shell that has ended has no way back, so its row says it stopped where the stop
        // was, for as long as the list holds it above the fold. Under the fold the heading
        // says it, and a device stream's `attach` already does.
        testCase "a stopped shell held above the fold says stopped where its stop was" <| fun () ->
            let held = { closedTerminalModel with Switcher = true; ListHeld = Set.singleton terminalId }
            let html = Support.render held
            Expect.stringContains html (Dom.attr Dom.Hooks.terminalStopped (TerminalId.value terminalId)) "stopped, on the row"
            Expect.isFalse (html.Contains (Dom.attr Dom.Hooks.terminalClose (TerminalId.value terminalId))) "and no stop beside it"
            let folded = Support.render { held with ListHeld = Set.empty; ClosedShown = true; Pane = None }
            Expect.isFalse (folded.Contains Dom.Hooks.terminalStopped) "under the fold, the heading says it"
    ]

// Artifacts in the same panel. What is pinned is REACHABILITY: an artifact is otherwise
// findable only by its chip in a message, so one shared long enough ago to have scrolled out
// of the conversation would be addressable and unreachable at once.
let private contentListTests =
    testList "The list panel's artifacts" [
        let listed (model: ClientModel) = Support.render { model with Switcher = true }
        let stamp = ArtifactStamp.ofActor ActorRef.Agent
        let shareOf (name: string) : ConversationItem =
            { MessageId = MessageId.create ("msg-" + name) |> expect
              Author = ActorRef.Agent
              Content =
                ItemContent.Act (
                    Act.ArtifactShared
                        { ArtifactShared.MessageId = MessageId.create ("msg-" + name) |> expect
                          ArtifactShared.Ref = ArtifactRef.first name stamp |> expect
                          ArtifactShared.MediaType = Some "image/png"
                          ArtifactShared.Bytes = 2_048L
                          ArtifactShared.Digest = ContentDigest.create (String.replicate 64 "a") |> expect
                          ArtifactShared.Actor = ActorRef.Agent })
              Status = Complete
              Offset = EventOffset.create 1L |> expect
              Woke = None; CausedBy = None }
        let withShare (name: string) =
            { representativeModel with
                Conversation = { representativeModel.Conversation with Recent = List.rev ([ shareOf name ]) } }

        testCase "an artifact the session holds has a row that opens it in the pane" <| fun () ->
            let html = listed (withShare "chart.png")
            Expect.isTrue
                (html.Contains (Dom.attr Dom.Hooks.artifactListRow "artifacts/chart.png/0000-e7f1a6"))
                "the row names the version it opens, so the panel and the pane address one thing"
            Expect.isTrue (html.Contains "chart.png") "and reads as the artifact's name, not its path"
            Expect.isFalse
                ((listed representativeModel).Contains Dom.Hooks.artifactListRow)
                "a session that has shared nothing grows no rows"

        // "the sections are named only when there are two kinds to tell apart" was here. Each
        // heading now carries the way to add one of its kind, so both are there with nothing
        // under them.

        // A census of nothing says there is nothing, and its heading still offers the one way
        // to make a terminal.
        testCase "an all page with nothing on it says so, and still offers New terminal" <| fun () ->
            let bare =
                { representativeModel with
                    Terminals = { representativeModel.Terminals with Terminals = [] }
                    Conversation = ConversationProjection.empty }
            let html = listed bare
            Expect.isTrue (html.Contains Dom.Text.nothingOpenedYet) "it says there is nothing"
            Expect.isTrue (html.Contains (Dom.attr Dom.Hooks.terminalNew "default")) "and offers New terminal"
    ]

/// The sheet's New terminal, on its Terminals heading, and the list of places it starts one in
/// — the repo picker's branch link and branch pane, in the sheet's rows.
let private paneNewTests =
    let withSandbox (name: string) =
        let started : ConversationItem =
            { MessageId = MessageId.create ("msg-" + name) |> expect
              Author = ActorRef.Agent
              Content =
                ItemContent.Act (
                    Act.SandboxStarted
                        { MessageId = MessageId.create ("msg-" + name) |> expect
                          Sandbox = SandboxRef.parse name |> expect
                          Backend = "docker"
                          Description = Some "day-to-day work"
                          Checkout = None
                          Forwarded = []
                          Realisation = []
                          Actor = ActorRef.Agent
                          OnBehalfOf = None
                          CausedBy = None })
              Status = Complete
              Offset = EventOffset.create 1L |> expect
              Woke = None; CausedBy = None }
        { representativeModel with
            // `Recent` is the write order (newest first) and `Items` is the read view over
            // it, so a fixture sets the field. One item, so the order says nothing here.
            Conversation = { representativeModel.Conversation with Recent = [ started ] } }

    let sheet (model: ClientModel) = model |> Support.step OpenAllMsg

    testList "The sheet's New terminal" [
        testCase "the sheet is the one way to make a terminal" <| fun () ->
            // The strip's `+`, its menu and the empty pane's button were the same act in three
            // places. The sheet's Terminals heading carries it now, and nothing else does.
            let shut = Support.render (withSandbox "octo/hello:dev")
            Expect.isFalse (shut.Contains Dom.Hooks.terminalNew) "nothing makes a terminal with the sheet put away"
            let html = Support.render (sheet (withSandbox "octo/hello:dev"))
            Expect.isTrue (html.Contains (Dom.attr Dom.Hooks.terminalNew "default")) "New terminal is on the sheet"

        testCase "with one place to put a terminal there is nothing to choose" <| fun () ->
            // A choice of one is a tap for nothing, and that is the shape of every session
            // with no repo.
            let html = Support.render (sheet representativeModel)
            Expect.isTrue (html.Contains (Dom.attr Dom.Hooks.terminalNew "default")) "New terminal starts in default"
            Expect.isFalse (html.Contains Dom.Hooks.startIn) "and no link offers a choice"

        testCase "with a choice the sheet says where New terminal starts one" <| fun () ->
            let html = Support.render (sheet (withSandbox "octo/hello:dev"))
            Expect.isTrue (html.Contains (Dom.attr Dom.Hooks.startIn "default")) "where it starts, as a link"
            Expect.isTrue (html.Contains (Dom.Text.startInNamed Dom.Text.defaultPlace)) "named for the place and the act"

        testCase "the list of places names every place, scope and all" <| fun () ->
            let html =
                sheet (withSandbox "octo/hello:dev")
                |> Support.step (ChoosingStartInMsg true)
                |> Support.render
            Expect.isTrue (html.Contains (Dom.attr Dom.Hooks.startInChoice "default")) "this session's own"
            Expect.isTrue (html.Contains (Dom.attr Dom.Hooks.startInChoice "octo/hello:dev")) "and the repo's"
            Expect.isTrue (html.Contains ">hello:dev<") "its name carries the repo's, because two may declare one name"
            // Named for what choosing it leads to, scope included. The visible label leaves the
            // owner out, so without this two owners' `hello:dev` would be called the same.
            Expect.isTrue
                (html.Contains (Dom.Text.newTerminalIn "octo/hello:dev"))
                "and every row says the whole act"

        testCase "a place chosen is where New terminal starts the next one" <| fun () ->
            let dev = SandboxRef.parse "octo/hello:dev" |> expect
            let model =
                sheet (withSandbox "octo/hello:dev")
                |> Support.step (ChoosingStartInMsg true)
                |> Support.step (StartInMsg dev)
            Expect.isFalse model.ChoosingStartIn "choosing goes back to the list"
            Expect.equal (ClientModel.startIn model) dev "and the place is kept"
            let html = Support.render model
            Expect.isTrue (html.Contains (Dom.attr Dom.Hooks.terminalNew "octo/hello:dev")) "New terminal starts there"

        testCase "the sheet comes down on its list, not on the places it was left choosing" <| fun () ->
            let model =
                sheet (withSandbox "octo/hello:dev")
                |> Support.step (ChoosingStartInMsg true)
                |> Support.step CloseSwitcherMsg
                |> Support.step OpenAllMsg
            Expect.isFalse model.ChoosingStartIn "back on the list"

        testCase "a place the session no longer offers falls back to the session's own" <| fun () ->
            let gone = SandboxRef.parse "octo/gone:dev" |> expect
            let model = sheet (withSandbox "octo/hello:dev") |> Support.step (StartInMsg gone)
            Expect.equal (ClientModel.startIn model) SandboxRef.defaultRef "default"
    ]

// The pane's action row: the acts about the thing on screen, in ONE place whatever kind it is.
// What is pinned here is the place — a reader who learns where `Download` lives under a picture
// must find a terminal's verbs in the same place, which is the whole reason the row exists.
let private paneActionsTests =
    testList "The pane's toolbar acts" [
        // The row and nothing outside it: its children are buttons and links, so the first
        // `</div>` after the hook is its end.
        let rowFor (key: string) (html: string) : string option =
            match html.IndexOf (Dom.attr Dom.Hooks.paneActions key) with
            | -1 -> None
            | at -> Some (html.Substring (at, html.IndexOf ("</div>", at) + "</div>".Length - at))
        let picture = Content.ContentRef.create "artifacts/chart.png/0000-e7f1a6" |> expect

        testCase "a file's way to have it is in the row, not inside the picture" <| fun () ->
            let html =
                representativeModel
                |> Support.step (OpenPreviewMsg (Preview.ofSubject (PreviewSubject.Content picture)))
                |> Support.render
            let row = rowFor "content:artifacts/chart.png/0000-e7f1a6" html
            Expect.isTrue
                (row |> Option.exists (fun r -> r.Contains Dom.Hooks.contentDownload))
                "the download is in the action row"
            // The picture is DRAWN, which is the whole reason a version is worth opening — and
            // asserting only the order let this pass while the body was a download link and the
            // image was nowhere (an absent hook indexes -1, which is less than anything).
            Expect.isTrue (html.Contains Dom.Hooks.contentImage) "the picture itself is on screen"

        // "A content tab in the strip says which kind it is" was here. A file is not a tab
        // (P2-1): it opens as a preview over the terminal, whose head says it is a file, and
        // the strip holds terminals only — so there is no kind left for a tab to mark.

        testCase "a terminal nobody holds offers its keyboard there; a held one does not" <| fun () ->
            let free = Support.render representativeModel
            Expect.isTrue
                (rowFor ("terminal:" + TerminalId.value terminalId) free
                 |> Option.exists (fun r -> r.Contains Dom.Hooks.terminalTake))
                "the keyboard of a free terminal is taken from the row"
            // A HELD terminal's take is the steal, and it stays on the lease bar where the name
            // of the person it would be taken from is. Two takes in two places, saying different
            // things, is the thing this must not become.
            let held = Support.render leasedTerminalModel
            Expect.isFalse
                (rowFor ("terminal:" + TerminalId.value terminalId) held
                 |> Option.exists (fun r -> r.Contains Dom.Hooks.terminalTake))
                "a held one offers nothing of the kind in the row"
            Expect.isTrue
                (held.Contains (Dom.attr Dom.Hooks.terminalLease (PeerId.value bob)))
                "the steal is the lease bar's, and it is still there"

        testCase "a preview that affords nothing draws no row at all" <| fun () ->
            // A bordered strip with no controls in it is a control bar saying there are none.
            // A stretch is always its recording and plays without being asked: there is no other
            // read of it to offer and nothing to step out to.
            let stretch =
                { Offset = EventOffset.create 3L |> expect
                  TerminalId = terminalId
                  Title = "build"
                  Holder = PeerRef bob
                  End = LeaseStolen (PeerRef ada)
                  Range = Some (2, 40)
                  StartedAt = DateTimeOffset (2026, 8, 8, 0, 0, 0, TimeSpan.Zero)
                  EndedAt = DateTimeOffset (2026, 8, 8, 0, 2, 0, TimeSpan.Zero) }
            let html =
                representativeModel
                |> Support.step (OpenPreviewMsg (Preview.ofSubject (PreviewSubject.Stretch stretch)))
                |> Support.render
            Expect.isFalse (html.Contains Dom.Hooks.paneActions) "no row"

        // On a phone the pane's left edge is a second way back to the chat, drawn for a thumb
        // already at the edge. The head's `›` is the named control, so the edge is a duplicate:
        // out of the accessibility tree and the Tab order, or a screen reader meets "back to
        // the chat" twice and a keyboard stops on it twice.
        testCase "the pane's edge handle is a duplicate the accessibility tree does not meet" <| fun () ->
            let html = Support.render representativeModel
            let hook = html.IndexOf "data-pane-grab-edge"
            Expect.isTrue (hook > 0) "the handle renders"
            let tag = html.Substring (html.LastIndexOf ("<button", hook), hook - html.LastIndexOf ("<button", hook))
            Expect.stringContains tag "aria-hidden=\"true\"" "hidden from the tree"
            Expect.stringContains tag "tabindex=\"-1\"" "and out of the Tab order"
    ]

// The offer to bring a stopped session back (Plan 11). It replaces the connection status
// word, so the thing to pin is WHEN it appears — a button with nowhere to go, or one shown
// over a session that is merely reconnecting, are both worse than the plain status.
let private reconnectOfferTests =
    testList "The way back to a session" [
        /// A page whose attempts to reach its session keep failing — which is what a session
        /// that stopped looks like from here, since the page cannot tell the two apart.
        let failing (manager: string option) (session: SessionId option) =
            { representativeModel with
                Link =
                    SessionLink.Reconnecting (
                        { Effort.fresh with Failed = Some { Reason = "the session did not answer"; Count = 3 } },
                        Phase.ReadThrough)
                Manager = manager
                Session = session }

        /// The nav column's connection section alone. The report has two mounts — the column,
        /// and the bar for where the column cannot be seen — so a whole-page `Contains` is
        /// answered by whichever happened to be right.
        let navConnection (html: string) : string =
            let at = html.IndexOf Dom.Hooks.feed
            Expect.isTrue (at >= 0) "the connection section renders at all"
            let start = html.LastIndexOf ("<section", at)
            html.Substring (start, html.IndexOf ("</section>", at) - start)

        testCase "a failing attempt with a manager and a session offers a way back" <| fun () ->
            let html = Support.render (failing (Some "http://127.0.0.1:8321") (Some sessionId))
            Expect.isTrue (html.Contains Dom.Text.reopenSession) "the way back renders"
            Expect.isTrue
                (html.Contains "http://127.0.0.1:8321/sessions/demo-session/open")
                "pointing at the manager's open route for THIS session"

        // Beside the reason it answers, because the page cannot say that the session stopped:
        // it says it is still reaching it, and why that is failing.
        testCase "the way back stands behind the disclosure, with the reason" <| fun () ->
            let column = navConnection (Support.render (failing (Some "http://127.0.0.1:8321") (Some sessionId)))
            let details = column.Substring (column.IndexOf "<details")
            Expect.isTrue (details.Contains Dom.Hooks.sessionReopen) "inside the disclosure"
            Expect.isTrue (details.Contains "the session did not answer") "beside the reason"

        // The report has two mounts and only one is ever visible, so the way back has to be on
        // BOTH or it is missing from whichever is showing. It went missing from the bar's:
        // once the column's mount became `max-md:hidden` — the rule that stops the report
        // being read twice — a phone could be told its session had stopped and offered
        // nothing whatever to do about it.
        testCase "the way back is offered at both mounts, since only one is ever seen" <| fun () ->
            let html = Support.render (failing (Some "http://127.0.0.1:8321") (Some sessionId))
            Expect.equal
                ((html.Split Dom.Hooks.sessionReopen |> Array.length) - 1)
                2
                "the nav column's, and the bar's for where the column cannot be seen"
            Expect.isTrue ((navConnection html).Contains Dom.Hooks.sessionReopen) "one of them is the column's"

        // The ways the offer must decline to render, each of which would otherwise be a link
        // that cannot work.
        testCase "no manager origin means no way back, just the status" <| fun () ->
            let column = navConnection (Support.render (failing None (Some sessionId)))
            Expect.isFalse (column.Contains Dom.Hooks.sessionReopen) "nothing to ask, so nothing offered"
            Expect.isTrue (column.Contains Dom.Text.reconnectingStatus) "the ordinary status still renders"

        testCase "no session id means no way back" <| fun () ->
            let column = navConnection (Support.render (failing (Some "http://127.0.0.1:8321") None))
            Expect.isFalse (column.Contains Dom.Hooks.sessionReopen) "nothing to ask FOR"

        testCase "a session that is merely reconnecting is not offered a reopen" <| fun () ->
            let html =
                Support.render
                    { representativeModel with
                        Link = SessionLink.Reconnecting ({ Effort.fresh with Slow = true }, Phase.ReadThrough)
                        Manager = Some "http://127.0.0.1:8321" }
            Expect.isFalse (html.Contains Dom.Hooks.sessionReopen) "nothing has failed yet"

        // A refused page needs its sign-in back, which reopening does not give it: the one way
        // in it is offered is the session's own sign-in.
        testCase "a page the session refused is offered its sign-in, not a reopen" <| fun () ->
            let html =
                Support.render
                    { representativeModel with
                        Link = SessionLink.Refused ("invalid peer token", Phase.ReadThrough)
                        Manager = Some "http://127.0.0.1:8321" }
            Expect.isTrue (html.Contains Dom.Hooks.sessionSignIn) "signing in again is offered"
            Expect.isFalse (html.Contains Dom.Hooks.sessionReopen) "and reopening is not"

        // Plan 13: the line promises what the deployment can actually deliver. Nothing
        // asserted this sentence before, which is how it came to claim work was safe on a
        // deployment that strands it.
        testCase "a stable-address deployment promises the work comes back" <| fun () ->
            let html = Support.render (failing (Some "http://127.0.0.1:8321") (Some sessionId))
            Expect.isTrue (html.Contains Dom.Text.localFallback) "the promise is kept where it can be"
            Expect.isFalse (html.Contains Dom.Text.localFallbackEphemeral) "and no warning where none is due"

        testCase "an ephemeral-address deployment warns instead of promising" <| fun () ->
            let html =
                Support.render
                    { failing (Some "http://127.0.0.1:8321") (Some sessionId) with EphemeralStorage = true }
            Expect.isTrue (html.Contains Dom.Text.localFallbackEphemeral) "it says what the wait costs"
            Expect.isFalse (html.Contains Dom.Text.localFallback) "and never both"

        testCase "the column says it once" <| fun () ->
            let column = navConnection (Support.render (failing (Some "http://127.0.0.1:8321") (Some sessionId)))
            Expect.equal
                ((column.Split Dom.Text.localFallback |> Array.length) - 1)
                1
                "the promise is stated once"

        testCase "a connected session shows no way back" <| fun () ->
            Expect.isFalse
                ((Support.render representativeModel).Contains Dom.Hooks.sessionReopen)
                "connected, with a manager known — still nothing to offer"
    ]

/// The two templates the renderer's sequence arm is asked about, written where the `open Lit`
/// they need cannot reach the rest of this file. One hole each, and the second one is the
/// point: `string seq` is one static type over three different runtime shapes — a JS array,
/// a Fable list, a lazy sequence — and only the renderer can tell them apart.
module private Holes =

    open Lit

    let list (parts: string list) = html $"""<p>{parts}</p>"""

    let sequence (parts: string seq) = html $"""<p>{parts}</p>"""

    // One hole in each context the renderer tells apart (`Ssr`'s scanner).
    let headOfValue (value: string) = html $"""<p title="{value}">t</p>"""

    let afterTextInValue (value: string) = html $"""<p title="Stop {value}">t</p>"""

    let singleQuotedValue (value: string) = html $"""<p title='Stop {value}'>t</p>"""

    let unquotedValue (value: string) = html $"""<p title={value}>t</p>"""

    let betweenTags (value: string) = html $"""<p>{value}</p>"""

    let afterClosedValue (value: string) = html $"""<p title="a">{value}</p>"""

    let afterQuoteInText (value: string) = html $"""<p>say "{value}</p>"""

    let valueAfterComment (value: string) = html $"""<!-- the column's rail --><p title="{value}">t</p>"""

    let textAfterComment (value: string) = html $"""<!-- the column's rail --><p>{value}</p>"""

// The renderer's sequence arm (`Ssr.renderValue`), which every surface reaches through and no
// view test can tell apart from the markup around it. A view builds its sequences with
// `List.map`, so the list shape is the only one the suite was exercising by accident — while
// the template-hole rule admits any `seq`, and a lazy one reaching a hole renders through the
// same arm. What is pinned here is that all three shapes render as their parts: this arm used
// to be a `Symbol.iterator` macro, and what replaced it is a type test against the NON-generic
// `System.Collections.IEnumerable` — the only one Fable compiles (a test against `seq<_>` or
// `IEnumerable<_>` is a compile error, not a silent false).
//
// The ordering guard above it — text before the sequence arm — is deliberately NOT pinned
// here: a string rendered as its characters would concatenate back to the same markup, because
// escaping is per-character, so no assertion on the output can tell the two apart. It is
// guarded by the arm's position and said in the comment there.
let private templateHoleTests =
    testList "Template holes" [
        testCase "a list in a hole renders as its parts" <| fun () ->
            Expect.equal
                (Support.renderTemplate (Holes.list [ "alpha"; "beta" ]))
                "<p>alphabeta</p>"
                "a Fable list is a sequence of child parts, not one object"

        testCase "an array in a hole renders as its parts" <| fun () ->
            Expect.equal
                (Support.renderTemplate (Holes.sequence [| "alpha"; "beta" |]))
                "<p>alphabeta</p>"
                "a JS array is the shape lit-html itself renders as child parts"

        testCase "a lazy sequence in a hole renders as its parts" <| fun () ->
            Expect.equal
                (Support.renderTemplate (Holes.sequence (seq { yield "alpha"; yield "beta" })))
                "<p>alphabeta</p>"
                "a sequence that has not been forced is still a sequence"

        // The parts go through the renderer, not a `String.concat` around it — so whatever a
        // part holds is escaped exactly as a hole of its own would be.
        testCase "a part of a sequence is escaped like any other text" <| fun () ->
            Expect.equal
                (Support.renderTemplate (Holes.sequence (seq { yield "<b>" })))
                "<p>&lt;b&gt;</p>"
                "an element is rendered, never interpolated raw"
    ]

// What a hole is escaped for is decided by where it sits (`Ssr`'s scanner). `'` is the one
// character the two escapers part on, so each case below says which escaper ran; `"` is the
// character that closed an attribute when the scanner was a test of a part's last characters.
let private hostile = "a\"'<b>&"

let private asValue = "a&quot;&#39;&lt;b&gt;&amp;"

let private asText = "a&quot;'&lt;b&gt;&amp;"

let private holeContextTests =
    testList "Hole contexts" [
        testCase "a hole at the head of a quoted value is escaped as a value" <| fun () ->
            Expect.equal
                (Support.renderTemplate (Holes.headOfValue hostile))
                (sprintf "<p title=\"%s\">t</p>" asValue)
                "the value's own quote cannot close it"

        testCase "a hole after text in a quoted value is escaped as a value" <| fun () ->
            Expect.equal
                (Support.renderTemplate (Holes.afterTextInValue hostile))
                (sprintf "<p title=\"Stop %s\">t</p>" asValue)
                "a hole in the middle of a value is still in the value"

        testCase "a hole in a single-quoted value is escaped as a value" <| fun () ->
            Expect.equal
                (Support.renderTemplate (Holes.singleQuotedValue hostile))
                (sprintf "<p title='Stop %s'>t</p>" asValue)
                "the other quote cannot close it either"

        testCase "a hole in an unquoted value is quoted by the renderer" <| fun () ->
            Expect.equal
                (Support.renderTemplate (Holes.unquotedValue hostile))
                (sprintf "<p title=\"%s\">t</p>" asValue)
                "a space cannot end a value the renderer quoted"

        testCase "a hole between tags is escaped as text" <| fun () ->
            Expect.equal
                (Support.renderTemplate (Holes.betweenTags hostile))
                (sprintf "<p>%s</p>" asText)
                "text"

        testCase "a hole after a closed value is escaped as text" <| fun () ->
            Expect.equal
                (Support.renderTemplate (Holes.afterClosedValue hostile))
                (sprintf "<p title=\"a\">%s</p>" asText)
                "the value's closing quote and the tag's end are both seen"

        testCase "a quote in text before a hole leaves the hole in text" <| fun () ->
            Expect.equal
                (Support.renderTemplate (Holes.afterQuoteInText hostile))
                (sprintf "<p>say \"%s</p>" asText)
                "a quote between tags opens nothing"

        testCase "an apostrophe in a comment leaves a later value hole a value" <| fun () ->
            Expect.equal
                (Support.renderTemplate (Holes.valueAfterComment hostile))
                (sprintf "<!-- the column's rail --><p title=\"%s\">t</p>" asValue)
                "a comment's prose opens no value"

        testCase "an apostrophe in a comment leaves a later text hole text" <| fun () ->
            Expect.equal
                (Support.renderTemplate (Holes.textAfterComment hostile))
                (sprintf "<!-- the column's rail --><p>%s</p>" asText)
                "a comment's prose opens no value"
    ]

// The bootstrap shell itself (`Ssr.page`), which had no Node-tier coverage. What matters
// here is the manager meta tag's ABSENCE rule: the client's offer is gated on the value
// being present, so a shell that emitted an empty one would turn a structural guarantee
// into a string check nobody wrote.
let private shellTests =
    testList "Bootstrap shell" [
        // The build the shell merely carries into its asset URLs; this suite is about the
        // manager meta tag, so any address does.
        let assets = AssetBuild "testbuild001"

        let pageWith (managerOrigin: string option) (ephemeralStorage: bool) =
            Yession.Host.Ssr.page "test-nonce" sessionId "" managerOrigin ephemeralStorage assets representativeModel

        let page (managerOrigin: string option) = pageWith managerOrigin false

        testCase "a manager origin is emitted as its meta tag" <| fun () ->
            Expect.isTrue
                ((page (Some "http://127.0.0.1:8321")).Contains
                    """<meta name="yession-manager" content="http://127.0.0.1:8321">""")
                "the origin rides the shell"

        testCase "no manager origin emits no tag at all — not an empty one" <| fun () ->
            let html = page None
            Expect.isFalse (html.Contains Dom.managerMetaName) "the tag is absent, so the client reads None"

        testCase "the session id is always there, so a client knows what it is before connecting" <| fun () ->
            Expect.isTrue
                ((page None).Contains (sprintf """<meta name="%s" content="demo-session">""" Dom.sessionMetaName))
                "session identity does not depend on having a manager"

        // The origin is operator-configured rather than user input, but it lands in an
        // attribute, and one escaper for every attribute is the rule.
        testCase "an origin containing a quote is escaped, not injected" <| fun () ->
            let html = page (Some "http://x\"onload=alert(1)")
            Expect.isFalse (html.Contains "\"onload=alert(1)") "the quote must not close the attribute"
            Expect.isTrue (html.Contains "&quot;onload=alert(1)") "it is escaped in place"

        // Plan 13. The tag says the one thing worth saying, and only when it is true, so a
        // path-mounted shell carries nothing about storage at all.
        testCase "an ephemeral-storage deployment marks its shell" <| fun () ->
            Expect.isTrue
                ((pageWith None true).Contains (sprintf """<meta name="%s" content="1">""" Dom.ephemeralStorageMetaName))
                "a deployment whose sessions move must say so"

        testCase "a stable-address deployment says nothing about storage" <| fun () ->
            Expect.isFalse
                ((pageWith None false).Contains Dom.ephemeralStorageMetaName)
                "absence is the good case, so the client reads false"

        // The browser bundle has no version of its own; it reads the server's off the shell.
        // The meta and the footer come from one model field, so they cannot disagree.
        testCase "the shell says which build served it, in its meta and at the column's foot" <| fun () ->
            let html = page None
            Expect.isTrue
                (html.Contains (sprintf """<meta name="%s" content="1.2.3-beta.4">""" Dom.buildMetaName))
                "the client reads the build back off the shell"
            Expect.isTrue
                (html.Contains (sprintf """%s="1.2.3-beta.4">yession 1.2.3-beta.4<""" Dom.Hooks.build))
                "and the first paint shows it"

        testCase "a model with no build says nothing about one" <| fun () ->
            let html = Yession.Host.Ssr.page "test-nonce" sessionId "" None false assets { representativeModel with Build = None }
            Expect.isFalse (html.Contains Dom.buildMetaName) "no tag, not an empty one"
            Expect.isFalse (html.Contains Dom.Hooks.build) "and no footer guessing at one"

        // The terminals column's open state lives on `<html>`, outside the mount the client
        // re-renders, so the client cannot paint it — only the shell can, and if it does not,
        // the first render shuts a column the first paint showed open. What is pinned is
        // agreement: the shell root says what the model says, in both directions.
        let shellWith (terminalsOpen: bool) =
            Yession.Host.Ssr.page "test-nonce" sessionId "" None false assets { representativeModel with TerminalsOpen = terminalsOpen }

        let rootOf (html: string) =
            let start = html.IndexOf "<html"
            html.Substring (start, html.IndexOf ('>', start) - start + 1)

        testCase "a shell whose column is shut says so on its root, before the client runs" <| fun () ->
            Expect.isTrue
                ((rootOf (shellWith false)).Contains Dom.termClosedClass)
                "the first paint carries the class the first render would otherwise have to add"

        testCase "a shell whose column is open carries no such class" <| fun () ->
            Expect.isFalse
                ((rootOf (shellWith true)).Contains Dom.termClosedClass)
                "the class is the model's bit, not a default"
    ]

// Where everyone IS. Presence already drove per-field overlays, but each of those is only
// visible from inside the surface it is about — so the thing pinned here is that a peer is
// findable from OUTSIDE it: the roster names what they are doing, and a terminal's tab shows
// who is in it whether or not that terminal is the one on screen.
let private presenceTests =
    testList "Peer presence" [
        let withBobIn (field: FocusField) =
            { representativeModel with
                Presence =
                    Map.ofList
                        [ PeerRef bob,
                          { DisplayName = "brave-owl"
                            Focus = Some { Field = field; Pos = { Anchor = "AQI="; Head = "AQI=" } }
                            Viewing = None } ] }

        testCase "a peer is in the roster with where they are" <| fun () ->
            let html = Support.render (withBobIn Title)
            Expect.isTrue (html.Contains (Dom.attr Dom.Hooks.peerPresence (ActorRef.token (PeerRef bob)))) "the peer has a roster row"
            Expect.isTrue
                (html.Contains (Dom.hookText (Dom.attr Dom.Hooks.peerAt Dom.Text.atTitle) Dom.Text.renamingSession))
                "and it says they are renaming the session"

        testCase "a peer writing their own message reads differently from one in yours" <| fun () ->
            let own = Support.render (withBobIn (DraftBody bob))
            Expect.isTrue (own.Contains (Dom.hookText (Dom.attr Dom.Hooks.peerAt Dom.Text.atDraft) Dom.Text.writing)) "their own draft is 'writing'"
            let mine = Support.render (withBobIn (DraftBody ada))
            Expect.isTrue
                (mine.Contains (Dom.hookText (Dom.attr Dom.Hooks.peerAt Dom.Text.atDraft) Dom.Text.inYourDraft))
                "being in the LOCAL peer's draft is said as yours, not as a name"

        testCase "a peer in a terminal is named by the terminal they are in" <| fun () ->
            let html = Support.render (withBobIn (TerminalDraftBody (terminalId, bob)))
            Expect.isTrue
                (html.Contains (Dom.hookText (Dom.attr Dom.Hooks.peerAt Dom.Text.atTerminal) (Dom.Text.inTerminal "build")))
                "the roster names the terminal, not just 'a terminal'"
            Expect.isTrue
                (html.Contains (Dom.attr Dom.Hooks.terminalTabPeer (ActorRef.token (PeerRef bob))))
                "and the terminal's own tab carries their mark"

        // A queued command names only its entry; the entry names the terminal. That join is
        // the one thing that could quietly return "somewhere" instead of a name.
        testCase "a peer editing a queued command is still placed in its terminal" <| fun () ->
            let html = Support.render (withBobIn (TerminalQueuedBody terminalQueueId))
            Expect.isTrue
                (html.Contains (Dom.hookText (Dom.attr Dom.Hooks.peerAt Dom.Text.atTerminalQueued) (Dom.Text.inTerminal "build")))
                "the queued command's terminal is resolved through the entry"
            Expect.isTrue (html.Contains (Dom.attr Dom.Hooks.terminalTabPeer (ActorRef.token (PeerRef bob)))) "and shows on that terminal's tab"

        // A chapter's name is collaborative text like the session title, so the roster places
        // somebody in it the same way — and says WHICH chapter, because "a chapter" in a
        // session divided into eight is not what anyone wanted to know.
        testCase "a peer naming a chapter is placed at the chapter they are naming" <| fun () ->
            let messageId = MessageId.create "msg-1" |> expect
            let model = withChapters [ "msg-1" ]
            let html =
                Support.render
                    { model with
                        Synced =
                            { model.Synced with
                                Chapters =
                                    model.Synced.Chapters
                                    |> Map.add messageId { Opens = true; Name = Ylmish.Text.ofString "The rollback" } }
                        Presence =
                            Map.ofList
                                [ PeerRef bob,
                                  { DisplayName = "brave-owl"
                                    Focus = Some { Field = ChapterName messageId; Pos = { Anchor = "AQI="; Head = "AQI=" } }
                                    Viewing = None } ] }
            Expect.isTrue
                (html.Contains
                    (Dom.hookText (Dom.attr Dom.Hooks.peerAt Dom.Text.atChapter) (Dom.Text.namingChapter "The rollback")))
                "the roster names the chapter, not just 'a chapter'"

        // A caret is placed against the box its field lives in, so the marker has to be inside
        // that box — and inside the RIGHT one, since a position taken in one chapter's name
        // would land at a real-looking offset in another's. The rule the marker sits in is the
        // only thing that says which name it is a caret in.
        testCase "a caret in a chapter's name is drawn on that chapter's rule" <| fun () ->
            let here = MessageId.create "msg-1" |> expect
            let elsewhere = MessageId.create "msg-agent" |> expect
            let model = withChapters [ "msg-1"; "msg-agent" ]
            let html =
                Support.render
                    { model with
                        Presence =
                            Map.ofList
                                [ PeerRef bob,
                                  { DisplayName = "brave-owl"
                                    Focus = Some { Field = ChapterName here; Pos = { Anchor = "AQI="; Head = "AQI=" } }
                                    Viewing = None } ] }
            let ruleOf (messageId: MessageId) =
                let opens = html.IndexOf (Dom.attr Dom.Hooks.chapterRule (MessageId.value messageId))
                html.Substring (opens, html.IndexOf ("</div>", opens) - opens)
            Expect.isTrue
                ((ruleOf here).Contains (Dom.attr Dom.Hooks.cursorPeer (ActorRef.token (PeerRef bob))))
                "their caret is on the rule of the chapter they are naming"
            Expect.isFalse
                ((ruleOf elsewhere).Contains (Dom.attr Dom.Hooks.cursorPeer (ActorRef.token (PeerRef bob))))
                "and on no other chapter's"

        // The title and a chapter's name are both collaborative inputs, and a marker that
        // appeared in every one of them at once would put one person in several places.
        testCase "a caret in the title is drawn on no chapter's rule" <| fun () ->
            let html = Support.render { (withChapters [ "msg-1" ]) with Presence = (withBobIn Title).Presence }
            let opens = html.IndexOf (Dom.attr Dom.Hooks.chapterRule "msg-1")
            let rule = html.Substring (opens, html.IndexOf ("</div>", opens) - opens)
            Expect.isFalse (rule.Contains (Dom.attr Dom.Hooks.cursorPeer (ActorRef.token (PeerRef bob)))) "the rule carries no caret"
            Expect.isTrue (html.Contains (Dom.attr Dom.Hooks.cursorPeer (ActorRef.token (PeerRef bob)))) "the title still does"

        // Presence is relayed live; the conversation is caught up over the event feed. A caret
        // can therefore arrive in a chapter this client has never seen, and the roster has to
        // say something true about it rather than invent a name or go quiet.
        testCase "a peer naming a chapter this client has not seen is still placed" <| fun () ->
            let unseen = MessageId.create "msg-not-here" |> expect
            let html = Support.render (withBobIn (ChapterName unseen))
            Expect.isTrue
                (html.Contains (Dom.hookText (Dom.attr Dom.Hooks.peerAt Dom.Text.atChapter) Dom.Text.atSomeChapter))
                "they are at a chapter, unnamed"

        // `Peers` deliberately keeps the departed so a draft's author still has a name —
        // reporting them as present would make the roster a guest book. Who is here is who has
        // a connection open, and this model has nobody's.
        testCase "a peer the log remembers but who is not connected has no roster row" <| fun () ->
            let html = Support.render { representativeModel with Presence = Map.empty }
            Expect.isFalse (html.Contains Dom.Hooks.peerPresence) "nobody else is claimed to be here"
            Expect.isTrue (html.Contains "swift-heron") "the local peer's own row is untouched"

        // Who is in the room is folded from the log's joins and leaves, the path a browser
        // takes; nobody here has a caret anywhere, so presence cannot be what puts them there.
        let after (events: SessionEvent list) =
            let envelopes =
                events
                |> List.mapi (fun i event ->
                    { EventId = EventId.fresh ()
                      SessionId = sessionId
                      Offset = EventOffset.create (6L + int64 i) |> expect
                      Actor = ActorRef.Session
                      Timestamp = DateTimeOffset (2026, 10, 6, 0, 0, 0, TimeSpan.Zero)
                      Event = event })
            { representativeModel with Presence = Map.empty }
            |> Support.step
                (EventsPageMsg
                    { Events = envelopes
                      LastOffset = envelopes |> List.tryLast |> Option.map (fun e -> e.Offset)
                      IsEnd = true })
        let joins (peer: PeerId) (name: string) = PeerJoined { PeerId = peer; DisplayName = name; User = None }
        let leaves (peer: PeerId) = PeerLeft { PeerId = peer }
        let rowOf (who: ActorRef) (html: string) =
            match html.IndexOf (Dom.attr Dom.Hooks.peerPresence (ActorRef.token who)) with
            | -1 -> None
            | opens -> Some (html.Substring (opens, html.IndexOf ("</div>", opens) - opens))

        testCase "a connected peer who is editing nothing has a roster row" <| fun () ->
            let html = Support.render (after [ joins bob "brave-owl" ])
            Expect.isSome (rowOf (PeerRef bob) html) "somebody reading the timeline is still in the room"

        // Where an idle person is, is nowhere the roster can name — and a word in the slot
        // would claim somewhere.
        testCase "a connected peer who is editing nothing is placed nowhere" <| fun () ->
            Expect.isTrue
                (rowOf (PeerRef bob) (Support.render (after [ joins bob "brave-owl" ]))
                 |> Option.exists (fun row -> not (row.Contains Dom.Hooks.peerAt)))
                "they have a row, and it says nothing about where they are"

        testCase "a peer who disconnects leaves the roster" <| fun () ->
            let html = Support.render (after [ joins bob "brave-owl"; leaves bob ])
            Expect.isNone (rowOf (PeerRef bob) html) "a departed peer is not in the room"

        // One browser is one peer however many tabs it has open, so a tab closing is one of
        // that peer's connections ending, not the person leaving.
        testCase "a peer with two tabs open is still here when one closes" <| fun () ->
            let html = Support.render (after [ joins bob "brave-owl"; joins bob "brave-owl"; leaves bob ])
            Expect.isSome (rowOf (PeerRef bob) html) "the other tab is still connected"

        testCase "a peer with two tabs open has one roster row" <| fun () ->
            let html = Support.render (after [ joins bob "brave-owl"; joins bob "brave-owl" ])
            let token = Dom.attr Dom.Hooks.peerPresence (ActorRef.token (PeerRef bob))
            Expect.equal (html.Split token).Length 2 "one person, one row"

        // A process that dies writes no `PeerLeft` for the links it took with it; everybody
        // still here rejoins after the next boot's own event.
        testCase "a session's boot forgets the connections its last process held" <| fun () ->
            let html =
                Support.render
                    (after
                        [ joins bob "brave-owl"
                          SessionResumed { MessageId = MessageId.create "msg-boot" |> expect; LastHeardAt = DateTimeOffset (2026, 10, 6, 0, 0, 0, TimeSpan.Zero) } ])
            Expect.isNone (rowOf (PeerRef bob) html) "nobody is here until they rejoin"

        // Two devices of one verified user are two peers and one person: one row, under the
        // name everybody sees them by.
        testCase "one person on two devices has one roster row" <| fun () ->
            let dora = PeerId.create "dora" |> expect
            let model =
                { after [ joins bob "lucid-tern"; joins dora "warm-tern" ] with
                    Attribution = { Attribution.empty with PeerUsers = Map.ofList [ bob, carol; dora, carol ]; UserPeers = Map.ofList [ carol, dora ] } }
            let html = Support.render model
            Expect.isSome (rowOf (UserRef carol) html) "the person has a row"
            Expect.isNone (rowOf (PeerRef bob) html) "and neither of their devices has another"

        // Where they ARE still shows when they are somewhere: being connected adds the row,
        // and a caret still fills its slot.
        testCase "a connected peer with a caret is placed on the roster where it is" <| fun () ->
            let model = { after [ joins bob "brave-owl" ] with Presence = (withBobIn Title).Presence }
            Expect.isTrue
                (rowOf (PeerRef bob) (Support.render model)
                 |> Option.exists (fun row ->
                     row.Contains (Dom.hookText (Dom.attr Dom.Hooks.peerAt Dom.Text.atTitle) Dom.Text.renamingSession)))
                "the row says they are renaming the session"

        testCase "the local peer never appears as their own collaborator" <| fun () ->
            let html = Support.render (withBobIn Title)
            Expect.isFalse (html.Contains (Dom.attr Dom.Hooks.peerPresence (ActorRef.token (PeerRef ada)))) "you are 'you', not a peer row"
    ]

// Sync status, said ONCE. Both halves of this used to be wrong at the same time: "up to date"
// appeared in the header AND the sidebar, and the catch-up that replaces it flickered on
// every send (a client is behind its own event for one round trip).
let private syncStatusTests =
    testList "Sync status" [
        let settled = { representativeModel with Link = SessionLink.Current }

        // It used to be said once, having been said three times before that. Now it is said
        // nowhere: "everything is fine" is the least actionable thing a screen can carry, and
        // a client that is working says so by working. What survives is the state TOKEN, on
        // an attribute, which is what a test should have been reading all along.
        testCase "a healthy client says nothing about its connection" <| fun () ->
            let html = (Support.render settled).ToLowerInvariant ()
            for word in [ "up to date"; ">connected<"; ">connecting<"; "not connected" ] do
                Expect.isFalse (html.Contains word) (sprintf "nothing on the screen reads %s" word)
            Expect.isTrue
                ((Support.render settled).Contains (Dom.attr Dom.Hooks.connection Dom.Text.connected))
                "but the state is still on the attribute a test reads"

        testCase "a brief catch-up says nothing at all" <| fun () ->
            let html =
                Support.render
                    { representativeModel with
                        Link = SessionLink.Fetching (EventOffset.create 7L |> expect, Effort.fresh, Phase.ReadThrough) }
            Expect.isFalse (html.Contains Dom.Hooks.catchUpBar) "the header's bar stays put"
            Expect.isFalse (html.Contains "Catching up") "and nothing anywhere else says it either"

        // Catching up is progress, not the link's health, so it never takes the status line.
        // The sidebar said it a second time, beside the header's bar, on a desktop.
        testCase "a catch-up is not said in the status line" <| fun () ->
            let html = Support.render representativeModel
            Expect.isFalse (html.Contains Dom.Hooks.degraded) "no status line for a fetch"

        // The header's bottom rule is on every screen, so a catch-up is drawn along it — as a
        // progress bar with its value, not a line changing colour, because a bar with no value
        // is decoration to a screen reader.
        testCase "a catch-up worth waiting on is drawn along the header's edge, with its value" <| fun () ->
            let html = Support.render representativeModel
            Expect.isTrue (html.Contains Dom.Hooks.catchUpBar) "the header carries the bar"
            Expect.isTrue (html.Contains "role=\"progressbar\"") "which is a progress bar"
            let folded = representativeModel.EventConsumer.LastProcessedOffset |> Option.map (fun o -> EventOffset.value o + 1L) |> Option.defaultValue 0L
            Expect.isTrue (html.Contains (sprintf "aria-valuenow=\"%d\"" folded)) "carrying how far it has got"

        // A showing stands for its second, whatever the link has done meanwhile (`Said`).
        testCase "the line says what it was saying while its second stands" <| fun () ->
            let reconnecting = SessionLink.Reconnecting ({ Effort.fresh with Slow = true }, Phase.ReadThrough)
            let back =
                { settled with Said = Some { Link = reconnecting; Showing = 1; Dwelt = false } }
            Expect.isTrue
                ((Support.render back).Contains (Dom.attr Dom.Hooks.degraded Dom.Text.degradedReconnecting))
                "still reconnecting, for the rest of its second"

        // Slow describes a wait that is RUNNING (`Effort` lives inside the cases that wait), so
        // a timer that fires just as the page lands has nothing to land on.
        testCase "'slow' cannot be claimed once there is nothing left to catch up on" <| fun () ->
            let model = Support.step EffortSlowMsg settled
            Expect.equal model.Link SessionLink.Current "a late timer is refused, not stored"
            Expect.isFalse ((Support.render model).Contains Dom.Hooks.catchUpBar) "and nothing is shown"
    ]

// --- every document a bin can serve --------------------------------------------------------
//
// "Chrome consistency" holds the floor over `pages`, a list somebody keeps, and a document added
// tomorrow is in it only if they remember. So the product's source is read for every document it
// can write. This product writes a document ONE way, whichever function does it: the text opens
// with `<!doctype html>`. The session's shell and the Manager's page concatenate one, and the
// standalone pages, the opening screen and the provider's callback page format one. That literal
// is what is looked for, and the top-level function it stands in names the document. A PRIVATE
// function is not one a test can call, so it is reached through the public functions that call
// it, and those are the surfaces: the refusal page is what `standalonePage` writes.

/// A document the product can write, by the public function a test calls to render it.
type private DocumentSurface =
    { /// `Module.function`, as a test spells the call.
      Surface : string
      /// Where its `<!doctype html>` is written, as `file:line`.
      WrittenAt : string }

/// A line that is code. A line that is only a comment calls nothing and writes nothing.
let private isCode (line: string) : bool =
    let trimmed = line.TrimStart ()
    trimmed <> "" && not (trimmed.StartsWith "//") && not (trimmed.StartsWith "(*")

/// A top-level function: a `let` at column 0, and whether it is private.
let private topLevelLet =
    System.Text.RegularExpressions.Regex @"^let\s+(?:(?:rec|inline)\s+)*(?:(private)\s+)?(?:(?:rec|inline)\s+)*([A-Za-z_][\w']*)"

/// The top-level function line `at` stands in: the nearest `let` at column 0 at or above it,
/// with whether it is private.
let private enclosingFunction (lines: string array) (at: int) : (string * bool) option =
    seq { at .. -1 .. 0 }
    |> Seq.tryPick (fun k ->
        let found = topLevelLet.Match lines.[k]
        if found.Success then Some (found.Groups.[2].Value, found.Groups.[1].Value = "private") else None)

/// The public functions of `lines` that reach `name`, and `name` itself when it is public.
let rec private surfacesOf (lines: string array) (visited: Set<string>) (name: string, isPrivate: bool) : string list =
    if not isPrivate then [ name ]
    else
        let called = System.Text.RegularExpressions.Regex (sprintf @"\b%s\b" (System.Text.RegularExpressions.Regex.Escape name))
        [ for k in 0 .. lines.Length - 1 do
              if isCode lines.[k] && called.IsMatch lines.[k] then
                  match enclosingFunction lines k with
                  | Some (caller, callerIsPrivate) when not (visited.Contains caller) ->
                      yield! surfacesOf lines (visited.Add caller) (caller, callerIsPrivate)
                  | _ -> () ]
        |> List.distinct

/// The product's F# sources as git would commit them: tracked, and untracked but not ignored,
/// because a page written a minute ago and not yet added is the one this exists to catch.
let private productSources () : string list =
    let listed =
        Fable.NodeExtras.SyncChildProcesses.execFileSync
            "git"
            [ "ls-files"; "-z"; "--cached"; "--others"; "--exclude-standard"; "--"; "app"; "src" ]
            { Fable.NodeExtras.SyncOptions.none with MaxBuffer = Some (32 * 1024 * 1024) }
    listed.Split [| '\u0000' |] |> Array.filter (fun path -> path.EndsWith ".fs") |> Array.distinct |> List.ofArray

/// Every document the product's source can write, by the public function that renders it.
let private documentSurfaces () : DocumentSurface list =
    [ for file in productSources () do
          let lines = (TestFiles.read file).Split '\n'
          let inModule =
              lines
              |> Array.tryPick (fun line ->
                  let declared = System.Text.RegularExpressions.Regex.Match (line, @"^module\s+([\w.]+)\s*$")
                  if declared.Success then Some (declared.Groups.[1].Value.Split '.' |> Array.last) else None)
          for at in 0 .. lines.Length - 1 do
              if isCode lines.[at] && lines.[at].ToLowerInvariant().Contains "<!doctype" then
                  let writtenAt = sprintf "%s:%d" file (at + 1)
                  match inModule, enclosingFunction lines at with
                  | Some inModule, Some (name, isPrivate) ->
                      match surfacesOf lines (Set.singleton name) (name, isPrivate) with
                      | [] -> failwithf "%s writes a document in `%s`, which nothing calls: delete it, or call it" writtenAt name
                      | surfaces ->
                          for surface in surfaces do
                              yield { Surface = inModule + "." + surface; WrittenAt = writtenAt }
                  | _ ->
                      failwithf
                          "%s writes a document outside a top-level function of a module, and this scan names a document by the function that writes it"
                          writtenAt ]
    |> List.distinct

// The chrome's shared vocabulary (`Style.Stroke` and the phrases over it), asserted where it
// is observable: the rendered markup. These are invariants about EVERY control of a kind, so
// they catch the next surface that invents its own field or forgets a focus state — which is
// exactly how the inputs drifted apart in the first place.
let private chromeTests =
    testList "Chrome consistency" [
        /// The `class="…"` of every tag whose name is in `names`.
        let classesOf (names: string list) (html: string) : string list =
            let rec collect (from: int) (acc: string list) =
                let starts =
                    names
                    |> List.choose (fun name ->
                        match html.IndexOf ("<" + name, from) with
                        | -1 -> None
                        | i -> Some i)
                match starts with
                | [] -> List.rev acc
                | starts ->
                    let start = List.min starts
                    let tagEnd = html.IndexOf ('>', start)
                    let tag = html.Substring (start, tagEnd - start)
                    let classes =
                        match tag.IndexOf "class=\"" with
                        | -1 -> ""
                        | i ->
                            let from = i + 7
                            tag.Substring (from, tag.IndexOf ('"', from) - from)
                    collect (tagEnd + 1) (classes :: acc)
            collect 0 []

        let shell = Support.render representativeModel
        let settingsShell = Support.render { representativeModel with Claude = { representativeModel.Claude with Flow = ClaudeAwaitingCode ("https://claude.ai/auth", "mine") } }
        // Mid device flow: the one surface carrying a field with a verb inside it, so the
        // copy control is invisible to the scan unless this render is in it — and a surface
        // the floor's scan cannot see is a surface the floor does not cover.
        let deviceShell =
            Support.render
                { representativeModel with
                    GitHub =
                        { representativeModel.GitHub with
                            Flow = GitHubAwaitingApproval ("049A-EBB0", "https://github.com/login/device", "mine", 5) } }
        // The switcher (Plan 20, stage 0; P2-2) renders only while open, so no other render
        // contains its controls. Scanned HERE rather than pinned again beside the list's own
        // tests: the accessibility floor is asserted once and centrally, and a surface that
        // is invisible to the scan is a surface the floor does not cover.
        let listShell = Support.render { representativeModel with Switcher = true }
        // Every notice at once — a dead feed, a credential the provider rejected, a session
        // that stopped, and a deployment that can keep none of it. Scanned here for the same
        // reason the terminal list is: a surface the floor's scan cannot see is a surface the
        // floor does not cover, and the disclosures these notices fold their detail into are
        // controls like any other.
        //
        // TWO renders, because the session leg SUBSUMES the history leg — a Process nobody can
        // reach cannot serve its feed either, and the report says one problem — so a stalled
        // feed is only ever reported over a session that is otherwise fine.
        let stoppedShell =
            { representativeModel with
                Link = SessionLink.Refused ("the session did not answer", Phase.ReadThrough)
                Manager = Some "http://127.0.0.1:8321"
                LocalCopy = LocalCopy.Unkeepable
                EphemeralStorage = true }
            |> withGitHubPanel (fun panel ->
                { panel with
                    MineCredential =
                        Some { Kind = StaticConnection; SignInRequired = Some "github rejected this credential" } })
            |> Support.render
        let stalledShell =
            Support.render
                { representativeModel with
                    Link = SessionLink.Stalled (EventOffset.create 7L |> expect, "ECONNREFUSED", Phase.ReadThrough) }
        let noticeShell = stoppedShell + stalledShell

        // The surfaces whose controls none of the renders above reach, each shown in the state
        // that draws them: a markdown table in a message (the one `th` the shell draws), the
        // terminal this peer holds (a textarea), a chapter's rule (its name is a field), a
        // collaborator's half-typed command (a read-only field), and the launch card with a
        // repository held (two search fields).
        let tableShell =
            { representativeModel with
                Conversation =
                    ConversationProjection.ofItems
                        [ { MessageId = MessageId.create "msg-table" |> expect
                            Author = ActorRef.Agent
                            Content = ItemContent.Message "| Name | Qty |\n| --- | --- |\n| Widget | 3 |"
                            Status = Complete
                            Offset = EventOffset.create 1L |> expect
                            Woke = None; CausedBy = None } ] }
            |> Support.render
        let heldShell = Support.render heldTerminalModel
        let chapterShell = Support.render (withChapters [ "msg-1" ])
        let collaboratorShell =
            { representativeModel with
                Synced =
                    { representativeModel.Synced with
                        TerminalDrafts =
                            representativeModel.Synced.TerminalDrafts
                            |> Map.add
                                (terminalId, bob)
                                { Terminal = terminalId; Author = bob; QueueId = QueueId.create "queue-ui-term-draft-bob" |> expect } } }
            |> Support.render
        let launchShell =
            let hello = RepoRef.create "octo/hello" |> expect
            let candidate : RepoCandidate =
                { Repo = hello; Description = Some "a thing"; DefaultBranch = "main"; Private = false; PushedAt = None }
            let at (n: int64) (event: SessionEvent) : EventEnvelope<SessionEvent> =
                { EventId = EventId.fresh ()
                  SessionId = sessionId
                  Offset = EventOffset.create n |> expect
                  Actor = ActorRef.Session
                  Timestamp = DateTimeOffset (2026, 9, 12, 0, 0, 0, TimeSpan.Zero)
                  Event = event }
            // A fresh session, read to its end, with nothing said: the launch card stands where
            // the timeline's first line will.
            let events =
                [ at 0L (SessionStarted { MessageId = MessageId.create "msg-started" |> expect })
                  at 1L (PeerJoined { PeerId = ada; DisplayName = "swift-heron"; User = None }) ]
            ClientModel.init { PeerId = ada; DisplayName = "swift-heron" }
            |> Support.step (ConnectedMsg { SessionId = sessionId; AssignedDisplayName = "swift-heron"; LatestOffset = Some (EventOffset.create 1L |> expect) })
            |> Support.step (EventsPageMsg { Events = events; LastOffset = events |> List.tryLast |> Option.map (fun e -> e.Offset); IsEnd = true })
            |> Support.step (LaunchMsg (LaunchListingArrived ("", ListingLoaded { Candidates = [ candidate ]; Next = None })))
            |> Support.step (LaunchMsg (LaunchSelected candidate))
            |> Support.step (LaunchMsg (LaunchBranchesArrived (hello, BranchesLoaded { Names = [ "main" ]; Next = None })))
            |> Support.render

        // Every render above, named: what the structure cases at the foot of this list read
        // one by one, so a failure can say which surface it found the fault on. The cases
        // that scan for a class read the same renders end to end.
        let renders =
            [ "the shell", shell
              "the settings column, Claude waiting for its code", settingsShell
              "the settings column, GitHub waiting for approval", deviceShell
              "the terminal list", listShell
              "the notices, a session that stopped", stoppedShell
              "the notices, a feed that stalled", stalledShell
              "a message holding a table", tableShell
              "the terminal this peer holds", heldShell
              "a chapter's rule", chapterShell
              "a collaborator's half-typed command", collaboratorShell
              "the launch card, a repository held", launchShell ]
        let everyRender = renders |> List.map snd |> String.concat ""

        // Every input either wears the ONE field face (a ring that goes blue on focus) or
        // wears nothing at all, because the row around it carries the stroke. What is ruled
        // out is the third thing: a control that invents its own border, or one that draws a
        // box with no focus state.
        testCase "every input draws the one field face, or draws nothing" <| fun () ->
            for classes in classesOf [ "input"; "select"; "textarea" ] everyRender do
                let isField = classes.Contains "focus:border-blue"
                let isBare = classes.Contains "border-0"
                Expect.isTrue (isField || isBare) (sprintf "an input is neither the field face nor bare: %s" classes)

        // Every pressable thing has a visible keyboard focus state (src/Yession.App/AGENTS.md,
        // UI baseline). The failure this pins is silent by nature: a control with `outline-2` and no
        // `outline` draws nothing, and you only find out with a keyboard.
        testCase "every button and link declares a visible focus ring" <| fun () ->
            for classes in classesOf [ "button"; "a "; "summary" ] everyRender do
                Expect.isTrue
                    (classes.Contains "focus-visible:outline focus-visible:outline-2 focus-visible:outline-blue")
                    (sprintf "a control has no visible focus ring: %s" classes)

        // The other way to ship no focus ring, and the quieter one: declare it and then turn
        // outlines off. Tailwind v4's `outline-none` is not the absence of an outline but a
        // SETTING — `--tw-outline-style: none` — and every outline utility resolves its style
        // through that variable, so a control wearing both has the classes, is served the CSS,
        // and draws nothing (measured live on the terminal's approval readout: `outlineStyle`
        // reported `none` on a focused control carrying the whole ring).
        //
        // The token is matched exactly, not by substring: `[&_.ProseMirror]:outline-none`
        // reaches a DESCENDANT (a mounted editor's contenteditable, whose focus signal is its
        // container's) and is the one legitimate way to write those characters.
        testCase "a declared focus ring is never switched off by outline-none" <| fun () ->
            let tags = [ "button"; "a "; "input"; "select"; "textarea" ]
            for classes in classesOf tags everyRender do
                if classes.Contains "focus-visible:outline" then
                    Expect.isFalse
                        (classes.Split ' ' |> Array.contains "outline-none")
                        (sprintf "a control declares a focus ring and then disables outlines: %s" classes)

        // Hover is how a desktop reader learns an icon: a control that shows only a glyph
        // carries its name twice, once for a screen reader (`aria-label`) and once for a
        // pointer (`title`). Enumerated by HOOK, because a string render cannot tell an
        // icon-only control from a worded one — and each hook must be FOUND, or a control
        // that stopped rendering here would pass this by never being looked at.
        testCase "every icon-only control in the pane carries a name and a title" <| fun () ->
            let shut = Support.render { representativeModel with TerminalsOpen = false }
            let reattachable = Support.render { renewableTerminalModel with Switcher = true }
            // A closed terminal the reader is still on wears the × that puts its tab away.
            let closed = Support.render closedTerminalModel
            let previewing =
                Support.render
                    (Support.step (ShowPreviewMsg (Preview.ofSubject (PreviewSubject.Block (terminalId, blockId)))) representativeModel)
            let html = shell + listShell + shut + reattachable + closed + previewing
            // The opening tag of every element carrying `hook`, valued or bare — and only that
            // attribute, not one it is a prefix of.
            let tagsWith (hook: string) =
                let rec from (i: int) (acc: string list) =
                    match html.IndexOf (hook, i) with
                    | -1 -> List.rev acc
                    | at ->
                        let next = html.[at + hook.Length]
                        if next = '=' || next = '>' || System.Char.IsWhiteSpace next then
                            let opens = html.LastIndexOf ('<', at)
                            from (at + 1) (html.Substring (opens, html.IndexOf ('>', at) - opens) :: acc)
                        else from (at + 1) acc
                from 0 []
            for hook in
                [ Dom.Hooks.contentToggle
                  Dom.Hooks.paneTabDismiss
                  Dom.Hooks.panePreviewClose
                  Dom.Hooks.terminalReattach
                  Dom.Hooks.terminalClose ] do
                let tags = tagsWith hook
                Expect.isNonEmpty tags (sprintf "%s is rendered somewhere this case looks" hook)
                for tag in tags do
                    Expect.isTrue (tag.Contains "aria-label=\"") (sprintf "%s has no accessible name: %s" hook tag)
                    Expect.isTrue (tag.Contains " title=\"") (sprintf "%s has no title to hover: %s" hook tag)

        // Deliberately no "focus is blue, everywhere" here. That focus is BLUE rather than
        // green is house style, not a floor — a design that moved it would fail such a test
        // while breaking nothing, which is the shape this suite does not keep (tests/AGENTS.md,
        // "Writing tests"). That focus is VISIBLE is the invariant, and the cases above are
        // what hold it.

        // The degradation bar is fixed above all three panes on a phone, so its height and the
        // room the panes leave for it are one number in three class strings — and they cannot
        // be composed from a shared token, because Tailwind emits only classes that appear
        // literally in the source. That makes drift between them silent in the worst way: the
        // stylesheet simply lacks the class, the bar falls back to its content height, and the
        // result is a bar overlapping the header or a band of dead space under it. Arithmetic,
        // not design — the number may be any number, so long as it is the same one.
        testCase "the bar's height and the room the panes leave for it are one number" <| fun () ->
            let sizeOf (what: string) (token: string) =
                let n = token.Substring (token.LastIndexOf '-' + 1)
                Expect.isTrue (n |> Seq.forall System.Char.IsDigit && n <> "") (sprintf "%s ends in a size: %s" what token)
                n
            let height = sizeOf "the bar's height" Style.degradedBarHeight
            Expect.equal (sizeOf "the overlays' inset" Style.degradedBarRoom) height "an overlay leaves exactly the bar"
            Expect.equal (sizeOf "the column's padding" Style.degradedBarRoomPad) height "and so does the column"

        // The shell's breakpoint has two readers: the stylesheet, which lays a phone out as one
        // column and sheets, and the script that writes `nav-alt` — which means "collapsed" on
        // one side of the line and "drawer open" on the other. A script asking a different
        // question from the sheet's flips that meaning on the screens between the two answers:
        // the drawer it thinks it opened is a desktop column it collapsed. So the script's
        // query is the stylesheet's `wide` variant, verbatim.
        testCase "the script asks the stylesheet's own breakpoint" <| fun () ->
            let css = TestFiles.read "app/tailwind.css"
            let opens = css.IndexOf "@custom-variant wide"
            Expect.isTrue (opens >= 0) "the stylesheet declares a `wide` variant"
            let block = css.Substring (opens, css.IndexOf ("@slot", opens) - opens)
            Expect.stringContains block (sprintf "@media %s {" Style.wideMedia) "Style.wideMedia is that variant's query"

        // --- what a notice says first -----------------------------------------------------

        /// The rendered page with every `<details …data-detail>` cut out of it: what a notice
        /// says WITHOUT anyone opening anything.
        let rec onTheSurface (html: string) : string =
            match html.IndexOf Dom.Hooks.detail with
            | -1 -> html
            | at ->
                let start = html.LastIndexOf ("<details", at)
                let stop = html.IndexOf ("</details>", at)
                if start < 0 || stop < 0 then html
                else onTheSurface (html.Remove (start, stop + "</details>".Length - start))

        // A disclosure is only a disclosure if the browser is the one making it. Written as a
        // real `<details>`/`<summary>`, it arrives keyboard-operable and announced; written as
        // a div with a click handler it arrives as neither, and looks identical.
        testCase "every folded mechanism is a real details/summary" <| fun () ->
            let rec check (from: int) (n: int) =
                match noticeShell.IndexOf (Dom.Hooks.detail, from) with
                | -1 -> n
                | at ->
                    let start = noticeShell.LastIndexOf ("<", at)
                    let stop = noticeShell.IndexOf ("</details>", at)
                    Expect.equal (noticeShell.Substring (start, "<details".Length)) "<details" "the element is a details"
                    Expect.isTrue
                        (stop > at && noticeShell.Substring(at, stop - at).Contains "<summary")
                        "and it opens by a summary, not by a handler on something else"
                    check (stop + 1) (n + 1)
            Expect.isTrue (check 0 0 > 0) "the notices this shell renders do fold something away"

        // The half of the split that can regress silently. Leading with the consequence is
        // visible the moment anybody looks at the screen; losing the mechanism is not — a
        // fault nobody can read is a fault nobody can report, and the words that go missing
        // are the provider's and the transport's own.
        testCase "a notice keeps its mechanism, and keeps it off the surface" <| fun () ->
            let surface = onTheSurface noticeShell
            for what, mechanism in
                [ "the transport's reason for stopping", "the session did not answer"
                  "the feed's fault", "ECONNREFUSED"
                  "the provider's own words", "github rejected this credential"
                  "why nothing can be kept here", Dom.Text.historyNotKeptWhy ] do
                Expect.isTrue (noticeShell.Contains mechanism) (sprintf "%s is still in the document" what)
                Expect.isFalse (surface.Contains mechanism) (sprintf "%s is not also on the surface" what)

        // --- structure -----------------------------------------------------------------------
        // WCAG 2.0 AA's structural floor: a control has a name, a header cell says what it
        // heads, a page says what language it is in and what it is called. Asserted of every
        // document below and never again beside a surface (tests/AGENTS.md, "UI tests pin an
        // invariant"): a surface added to this list is held to all of it.

        /// The Manager's page, over a table of sessions (one stopped, one archived), a declared
        /// MCP server and a hook endpoint: every section it can draw, so every table and form.
        let managerPage =
            let record (id: string) (archived: bool) : Yession.Manager.SessionRecord =
                { SessionId = SessionId.create id |> expect
                  DisplayName = id
                  CreatedAt = DateTimeOffset (2026, 7, 15, 12, 0, 0, TimeSpan.Zero)
                  DataDir = "sessions/" + id
                  ArchivedAt = if archived then Some (DateTimeOffset (2026, 8, 1, 9, 30, 0, TimeSpan.Zero)) else None }
            let view (r: Yession.Manager.SessionRecord) : Yession.Host.ProcessManager.SessionView =
                { Record = r; Status = Yession.Host.ProcessManager.NotRunning; Standings = [] }
            Yession.Host.ManagerUi.page
                "app.css"
                "manager-page.js"
                PublicAccess.Loopback
                { Yession.Manager.SessionQuery.defaults with Show = Yession.Manager.Shown.Both }
                [ view (record "alpha" false); view (record "beta" true) ]
                [ { Server =
                      { Name = McpServerName.create "serial" |> expect
                        Transport = McpHttp "http://127.0.0.1:7333"
                        Description = Some "USB serial ports" }
                    Audience = AnySession } ]
                (Yession.Host.WebhookRelay.endpointsFor
                    "chrome-kek"
                    [ { Name = "github"; Rotation = 0; Signature = Yession.Host.WebhookRelay.SignatureSpec.webSub } ])

        /// Every document a bin serves, whole. One entry per place the product writes an
        /// `<html>`: the session's shell, the Manager's page, the two pages `/open` can answer
        /// with, and the page a provider's redirect lands on.
        let pages =
            [ "the session's shell",
              Yession.Host.Ssr.page "test-nonce" sessionId "" None false (AssetBuild "chrome-floor") representativeModel
              "the Manager's page", managerPage
              "the page /open answers a refusal with",
              Yession.Host.ManagerUi.problemPage "Cannot open this session" "It is archived."
              "the screen /open shows while a session launches",
              Yession.Host.ManagerUi.openingPage "test-nonce" sessionId "/sessions/demo-session/open" "/sessions/demo-session/launch" "/sessions/demo-session/ready" (Some "https://example.com/s/demo-session/")
              "the page a provider's redirect lands on",
              snd (Yession.Host.ProcessManager.connectionsCallbackResponse "Connected" "Close this tab and return to your session.") ]

        // What a structure case reads: the renders, and the pages (which hold the Manager's
        // forms and tables, and the shell again with its document around it).
        let documents = renders @ pages

        /// A document as a tree, with every element in it and the elements around each.
        let treeOf (html: string) = Markup.elements (Markup.parse html)

        let isControl (e: Markup.Element) = e.Tag = "input" || e.Tag = "select" || e.Tag = "textarea"

        /// The controls of a document that a person can meet. An `<input type="hidden">` is not
        /// one, and neither is anything under `hidden`, which the browser takes out of the page
        /// and out of the accessibility tree with it.
        let controlsOf (all: (Markup.Element * Markup.Element list) list) =
            all
            |> List.filter (fun (e, ancestors) ->
                let inert = e.Attributes.ContainsKey "hidden" || ancestors |> List.exists (fun a -> a.Attributes.ContainsKey "hidden")
                let hiddenInput = e.Tag = "input" && (Markup.said "type" e |> Option.map (fun t -> t.ToLowerInvariant ())) = Some "hidden"
                isControl e && not inert && not hiddenInput)

        /// What a screen reader calls a control, by the four ways this product may say it: an
        /// `aria-labelledby` that points at words, an `aria-label`, a `<label for>` and a
        /// wrapping `<label>`. A `placeholder` and a `title` are not on the list, because
        /// neither is an accessible name that survives the field being filled in.
        let nameOf (all: (Markup.Element * Markup.Element list) list) (control: Markup.Element) (ancestors: Markup.Element list) : string option =
            let words (e: Markup.Element) =
                match Markup.text isControl e with
                | "" -> None
                | spoken -> Some spoken
            let labelledBy =
                Markup.said "aria-labelledby" control
                |> Option.bind (fun ids ->
                    let named =
                        ids.Split ([| ' ' |], StringSplitOptions.RemoveEmptyEntries)
                        |> Array.choose (fun id ->
                            all |> List.tryPick (fun (e, _) -> if Markup.attribute "id" e = Some id then words e else None))
                    if Array.isEmpty named then None else Some (String.concat " " named))
            let labelFor =
                Markup.said "id" control
                |> Option.bind (fun id ->
                    all |> List.tryPick (fun (e, _) -> if e.Tag = "label" && Markup.attribute "for" e = Some id then words e else None))
            let wrapping = ancestors |> List.tryPick (fun a -> if a.Tag = "label" then words a else None)
            [ labelledBy; Markup.said "aria-label" control; labelFor; wrapping ] |> List.tryPick id

        /// Refuses with EVERY offender at once, one per line and each once however many
        /// documents repeat it, so a change that breaks the floor in three places is fixed in
        /// one run. An offender is the document it was found on and what was found there.
        let refuse (what: string) (offenders: (string * string) list) =
            let distinct = offenders |> List.distinctBy snd
            Expect.isTrue
                (List.isEmpty distinct)
                (distinct
                 |> List.map (fun (page, fault) -> sprintf "  - on %s: %s" page fault)
                 |> String.concat "\n"
                 |> sprintf "%s:\n%s" what)

        testCase "every form control has an accessible name" <| fun () ->
            let found =
                [ for page, html in documents do
                    let all = treeOf html
                    for control, ancestors in controlsOf all -> page, control, ancestors, nameOf all control ancestors ]
            refuse
                "a form control has no accessible name. Give it a <label for=\"its id\"> (class `Style.srOnly` when the design has no room for the words), wrap it in a <label>, or give it an aria-label. A placeholder is not a name"
                [ for page, control, ancestors, name in found do
                    if name.IsNone then page, Markup.describe (List.tryHead ancestors) control ]
            // Every kind is in a document this case reads, or it looked at nothing of that kind.
            Expect.equal
                (found |> List.map (fun (_, control, _, _) -> control.Tag) |> Set.ofList)
                (set [ "input"; "select"; "textarea" ])
                "this case reads an input, a select and a textarea"

        testCase "every table header cell says whether it heads a column or a row" <| fun () ->
            let cells =
                [ for page, html in documents do
                    for th, ancestors in treeOf html do
                        if th.Tag = "th" then page, th, ancestors ]
            refuse
                "a table header cell has no scope. Add scope=\"col\" when it heads a column, or scope=\"row\" when it heads a row"
                [ for page, th, ancestors in cells do
                    if not (Markup.said "scope" th |> Option.exists (fun scope -> List.contains scope [ "col"; "row"; "colgroup"; "rowgroup" ])) then
                        page, Markup.describe (List.tryHead ancestors) th ]
            Expect.isNonEmpty cells "this case reads a table"

        testCase "every page a bin serves declares its language" <| fun () ->
            refuse
                "a page does not declare its language. Write <html lang=\"en\"> on its root"
                [ for page, html in pages do
                    match treeOf html |> List.tryFind (fun (e, _) -> e.Tag = "html") with
                    | None -> page, "it has no <html> element at all"
                    | Some (root, _) -> if (Markup.said "lang" root).IsNone then page, "its <html> has no lang" ]

        testCase "every page a bin serves has a title" <| fun () ->
            refuse
                "a page has no title. Put a <title> that names the page in its <head>"
                [ for page, html in pages do
                    let titles = treeOf html |> List.filter (fun (e, ancestors) -> e.Tag = "title" && ancestors |> List.exists (fun a -> a.Tag = "head"))
                    match titles with
                    | [] -> page, "it has no <title> in its <head>"
                    | (title, _) :: _ -> if Markup.text (fun _ -> false) title = "" then page, "its <title> is empty" ]

        // `pages` is a list kept by hand, so a document added tomorrow is held to none of the
        // floor above until somebody remembers it. So the product's source is read for every
        // document it can write (`documentSurfaces`), and each must be one this suite builds:
        // one called before `documents`, which is where `renders` and `pages` are made.
        testCase "every document a bin can serve is one this suite renders" <| fun () ->
            let lines = (TestFiles.read "tests/Yession.Tests/Acceptance.fs").Split '\n'
            let opens = lines |> Array.findIndex (fun line -> line.TrimStart().StartsWith "testList \"Chrome consistency\"")
            let built =
                match lines |> Array.skip opens |> Array.tryFindIndex (fun line -> line.TrimStart().StartsWith "let documents =") with
                | Some length -> lines |> Array.skip opens |> Array.take length |> Array.filter isCode |> String.concat "\n"
                | None -> failwith "Chrome consistency has no `let documents =`, and what this case reads as rendered is what the suite builds before it"
            refuse
                "a document a bin can serve is not one this suite renders, so the floor is not held on it. Add a render of it to `pages` (a state of the session's shell goes in `renders` instead)"
                [ for document in documentSurfaces () do
                    let named = System.Text.RegularExpressions.Regex (sprintf @"\b%s\b" (System.Text.RegularExpressions.Regex.Escape document.Surface))
                    if not (named.IsMatch built) then
                        document.WrittenAt, sprintf "`%s` serves the document written here, and nothing in this suite renders it" document.Surface ]

        // A scan that found nothing would pass the case above, so it is asked for the one
        // document that cannot go away.
        testCase "the scan for documents finds the session's shell" <| fun () ->
            Expect.isTrue
                (documentSurfaces () |> List.exists (fun document -> document.Surface = "Ssr.page"))
                "the session's shell, among the documents the scan found"
    ]

// What the session page must keep saying, whatever it comes to look like: one person wears
// one name, an author is never a raw id, and a control is offered only when it does something.
// Deliberately NOT here — the caret marking an empty timeline, the composer's rest-state rail,
// which weight a waiting button wears — because those are the design, and the design changing
// is not a regression (tests/AGENTS.md, "Writing tests").
let private semanticsTests =
    testList "What the screen says about people and their choices" [

        /// What one message ELEMENT says, from its author hook to its body — so an assertion
        /// about the chat's attribution cannot be satisfied by the same word appearing in the
        /// roster, which is exactly how the first version of this test passed while the chat
        /// was still printing a peer id.
        let messageMetaOf (peer: PeerId) (html: string) : string =
            let start = html.IndexOf (Dom.attr Dom.Hooks.messageAuthor (PeerId.value peer))
            Expect.isTrue (start >= 0) "the message renders at all"
            let stop = html.IndexOf (Dom.Hooks.messageBody, start)
            html.Substring (start, stop - start)

        /// What a reader SEES of a fragment: its text with every tag, and so every attribute,
        /// taken out. An assertion about what is on screen that reads the markup is answered
        /// by a `title` or an `aria-label` just as readily as by a word somebody can see.
        let visibleTextOf (html: string) : string =
            let out = System.Text.StringBuilder ()
            let mutable inside = false
            for c in html do
                if c = '<' then inside <- true
                elif c = '>' then inside <- false
                elif not inside then out.Append c |> ignore
            out.ToString().Trim ()

        /// One terminal BLOCK's command line, from its hook to the output beneath it — so an
        /// assertion that the line says who ran a command cannot be satisfied by the name
        /// appearing in anything printed under it.
        let blockLineOf (block: string) (html: string) : string =
            // From the `<` that OPENS the element, not from its hook attribute, which is in the
            // middle of a tag — a fragment starting there reads as text to anything stripping
            // tags, and the first attributes of the element come out as words.
            let at = html.IndexOf (Dom.attr Dom.Hooks.terminalBlock block)
            Expect.isTrue (at >= 0) "the block renders at all"
            let start = html.LastIndexOf ('<', at)
            let stop = html.IndexOf (Dom.Hooks.terminalOutput, start)
            Expect.isTrue (stop > start) "the block has its output beneath it"
            html.Substring (start, stop - start)

        testCase "a command somebody else ran says WHO on its own line, not only in its facts" <| fun () ->
            // It was a bare coloured square with a `title`: nothing on a phone, which has no
            // hover, and nothing to a screen reader, because `title` on a non-interactive span
            // is not an accessible name. A scrollback mixing a person's commands with the
            // agent's told them apart by hue alone.
            let agentRan =
                { representativeModel with
                    Terminals =
                        { representativeModel.Terminals with
                            Terminals =
                                representativeModel.Terminals.Terminals
                                |> List.map (fun t ->
                                    { t with
                                        Blocks =
                                            t.Blocks
                                            |> List.map (fun b ->
                                                { b with Authority = Authority.agentFor (Principal.Peer ada) }) }) } }
            let line = blockLineOf "block-ui" (Support.render agentRan)
            Expect.isTrue (line.Contains Dom.Hooks.terminalBlockAuthor) "the line attributes the command"
            // Read off the TEXT, with every tag's innards removed. The first version of this
            // asserted on the markup and passed over the bare square it was written to
            // forbid — because that square carried the name in a `title`, which is an
            // attribute, which is exactly the hiding place in question.
            Expect.isTrue
                (visibleTextOf line |> fun text -> text.Contains "agent")
                (sprintf "the name is on the line as words, not inside an attribute: %s" (visibleTextOf line))

        // "your own commands are not attributed in your own terminal" was here. A terminal is
        // shared, and a scrollback where your commands alone wore no name read, to everyone
        // else looking at it, as commands by nobody. Your commands wear your name as your
        // messages do in the transcript.
        testCase "your own command wears your own name, as your messages do" <| fun () ->
            let line = blockLineOf "block-ui" (Support.render representativeModel)
            Expect.isTrue
                ((visibleTextOf line).Contains representativeModel.Peer.DisplayName)
                (sprintf "your name, as words on the line: %s" (visibleTextOf line))

        /// The same lookup, keyed by the author hook's raw TOKEN rather than a `PeerId` —
        /// what a `UserRef` author's message carries (`UserId.value`, per `authorLabel`).
        let messageMetaOfLabel (label: string) (html: string) : string =
            let start = html.IndexOf (Dom.attr Dom.Hooks.messageAuthor label)
            Expect.isTrue (start >= 0) "the message renders at all"
            let stop = html.IndexOf (Dom.Hooks.messageBody, start)
            html.Substring (start, stop - start)

        /// The fixture with one message from a COLLABORATOR — `bob`, deliberately not the
        /// local peer, so what the chat prints about him can only have come from the roster
        /// lookup under test rather than from the client's own identity.
        let withMessageFromBob (peers: Map<PeerId, string>) =
            { representativeModel with
                Peers = peers
                Presence = Map.empty
                Conversation =
                    ConversationProjection.ofItems
                        [ { MessageId = MessageId.create "msg-bob" |> expect
                            Author = PeerRef bob
                            Content = ItemContent.Message ("on it")
                            Status = Complete
                            Offset = EventOffset.create 1L |> expect
                            Woke = None; CausedBy = None } ]
                Timeline = TimelineProjection.empty }

        // A peer id is a token, not a person. The roster, the draft summaries and the lease
        // bar all resolved one to a name; the chat did not, so the same human appeared as
        // `brave-owl` in the sidebar and `bob` on their own message.
        testCase "the chat attributes a message to the name, not the peer id" <| fun () ->
            let html = Support.render (withMessageFromBob (Map.ofList [ bob, "quiet-otter" ]))
            let meta = messageMetaOf bob html
            Expect.isTrue (meta.Contains ">quiet-otter<") "the author is the name the roster knows"
            Expect.isFalse (meta.Contains ">bob<") "and never the raw id as a person's name"
            Expect.isTrue
                (html.Contains (Dom.attr Dom.Hooks.messageAuthor (PeerId.value bob)))
                "while the hook still carries the stable id, so tests and delegation do not move"

        // The fallback is the reason this resolves through the roster rather than asserting a
        // name exists: a peer who left before this client ever saw them has no name to show,
        // and a blank author would be worse than an ugly one.
        testCase "an unknown peer still gets attributed, by id" <| fun () ->
            let html = Support.render (withMessageFromBob Map.empty)
            Expect.isTrue
                ((messageMetaOf bob html).Contains ">bob<")
                "no name known, so the id stands in rather than nothing"

        // A `UserRef` author is an attributed peer's durable identity — the same one the
        // Session's `actorFor` (`Yession.Domain.Attribution`) stamped `MessageSent`
        // with. Resolving it means finding the peer this client saw join AS that user and
        // reading the roster's name for THAT peer, exactly as `nameOf` already does for a
        // `PeerRef` — which is the unification this whole thread was about: chat and the
        // roster fold the same log through the same rule and can no longer disagree.
        testCase "a message from an attributed user wears the peer's real name, not their subject" <| fun () ->
            let html =
                Support.render
                    { representativeModel with
                        Peers = Map.ofList [ bob, "quiet-otter" ]
                        Attribution = { Attribution.empty with PeerUsers = Map.ofList [ bob, carol ]; UserPeers = Map.ofList [ carol, bob ] }
                        Presence = Map.empty
                        Conversation =
                            ConversationProjection.ofItems
                                [ { MessageId = MessageId.create "msg-carol" |> expect
                                    Author = UserRef carol
                                    Content = ItemContent.Message ("on it")
                                    Status = Complete
                                    Offset = EventOffset.create 1L |> expect
                                    Woke = None; CausedBy = None } ]
                        Timeline = TimelineProjection.empty }
            let meta = messageMetaOfLabel (UserId.value carol) html
            Expect.isTrue (meta.Contains ">quiet-otter<") "the author is the name the attributed peer's join carried"
            Expect.isFalse (meta.Contains ">carol@example.com<") "and never the raw subject as a person's name"

        // A user this client has never seen a `PeerJoined` for (their only connection was
        // to a peer that joined before this client's replay began, say) has nothing to
        // resolve through — the raw subject stands in, exactly as an id does for an unknown
        // `PeerRef`.
        testCase "an attributed user this client has never seen a join for falls back to their subject" <| fun () ->
            let html =
                Support.render
                    { representativeModel with
                        Peers = Map.empty
                        Attribution = Attribution.empty
                        Presence = Map.empty
                        Conversation =
                            ConversationProjection.ofItems
                                [ { MessageId = MessageId.create "msg-carol" |> expect
                                    Author = UserRef carol
                                    Content = ItemContent.Message ("on it")
                                    Status = Complete
                                    Offset = EventOffset.create 1L |> expect
                                    Woke = None; CausedBy = None } ]
                        Timeline = TimelineProjection.empty }
            Expect.isTrue
                ((messageMetaOfLabel (UserId.value carol) html).Contains ">carol@example.com<")
                "no peer join known for this user, so the subject stands in rather than nothing"

        // The exact defect reported live: an attributed user's EARLIER join (before a real
        // name reached it, or simply an earlier session) stays in `PeerUsers` right alongside
        // their current one — reverse-scanning that map for "a peer attributed to this user"
        // can land on either, which is how chat showed a stale random name while the sidebar
        // (self, which never goes through this lookup) showed the current one on the same
        // screen. `UserPeers` is what makes "current" well-defined: whichever `PeerJoined`
        // came LAST wins, deterministically, not whichever `PeerId` sorts first.
        testCase "an attributed user's message wears their CURRENT join's name, not a stale one" <| fun () ->
            let dora = PeerId.create "dora" |> expect
            let html =
                Support.render
                    { representativeModel with
                        Peers = Map.ofList [ bob, "lucid-tern"; dora, "warm-tern" ]
                        Attribution = { Attribution.empty with PeerUsers = Map.ofList [ bob, carol; dora, carol ]; UserPeers = Map.ofList [ carol, dora ] }
                        Presence = Map.empty
                        Conversation =
                            ConversationProjection.ofItems
                                [ { MessageId = MessageId.create "msg-carol" |> expect
                                    Author = UserRef carol
                                    Content = ItemContent.Message ("on it")
                                    Status = Complete
                                    Offset = EventOffset.create 1L |> expect
                                    Woke = None; CausedBy = None } ]
                        Timeline = TimelineProjection.empty }
            let meta = messageMetaOfLabel (UserId.value carol) html
            Expect.isTrue (meta.Contains ">warm-tern<") "the current join's name, not the stale one"
            Expect.isFalse (meta.Contains ">lucid-tern<") "an earlier join under the same user is not who they are now"

        // The roster is folded from the durable `PeerJoined` log; your own display name comes
        // from THIS connection. When a peer rejoined under a new name the two disagreed, and a
        // chat reading the roster for its own messages would contradict the sidebar's "you"
        // row — two names for one person, which is the defect this whole thread is about.
        testCase "your own messages wear the name the sidebar calls you" <| fun () ->
            let html =
                Support.render
                    { representativeModel with
                        Peer = { PeerId = ada; DisplayName = "warm-tern" }
                        Peers = Map.ofList [ ada, "a-stale-name-from-the-log" ] }
            Expect.isTrue ((messageMetaOf ada html).Contains ">warm-tern<") "the chat says what you are called now"
            Expect.isTrue (html.Contains (Dom.hookText Dom.Hooks.displayName "warm-tern")) "and so does the roster"
            Expect.isFalse (html.Contains "a-stale-name-from-the-log") "the log's older name is nobody's current name"

        // One person, one mark. A `UserRef` and a `PeerRef` are two ways an event can point
        // at one human — the Session records the user when attribution knows one and
        // the peer when it does not — and a mark seeded by whichever was recorded gave that
        // human two checkers on one screen: one over their messages, another on the blocks
        // they ran. What is pinned is the AGREEMENT, never which checker it is.
        testCase "a person wears one mark whichever reference names them" <| fun () ->
            let model =
                { representativeModel with
                    Attribution = { Attribution.empty with PeerUsers = Map.ofList [ bob, carol ]; UserPeers = Map.ofList [ carol, bob ] } }
            Expect.equal
                (Entity.actorMark model (PeerRef bob))
                (Entity.actorMark model (UserRef carol))
                "the peer bob joined as, and the user bob is, are one person and one mark"

        // The same person, the same colour, where they are TYPING as well as where they are
        // named: the caret over a shared field is read from the checker's own seat, so it
        // agrees with the mark beside their name and across both references.
        testCase "a person's caret is drawn in their mark's colour whichever reference names them" <| fun () ->
            let model =
                { representativeModel with
                    Attribution = { Attribution.empty with PeerUsers = Map.ofList [ bob, carol ]; UserPeers = Map.ofList [ carol, bob ] } }
            let caret = Entity.presenceColour model (PeerRef bob)
            let seat = [ 0 .. Style.seats - 1 ] |> List.find (fun seat -> Style.humanColour seat = caret)
            Expect.equal (Entity.actorMark model (UserRef carol)) (Style.humanAvatar seat)
                "the caret bob types with is the colour of the mark carol wears"

        // The other half of the same rule: the mark a surface draws is the one `Entity`
        // answers, on every surface. The roster seeded its own by a different string from
        // the chat's (`ActorRef.token` against `UserId.value`), so the two disagreed for
        // every person on the page even before attribution entered into it.
        testCase "the roster and the chat draw one person with one mark" <| fun () ->
            let model =
                { representativeModel with
                    Peers = Map.ofList [ bob, "quiet-otter" ]
                    Presence =
                        Map.ofList
                            [ PeerRef bob,
                              { DisplayName = "quiet-otter"
                                Focus = Some { Field = Title; Pos = { Anchor = "AQI="; Head = "AwQ=" } }
                                Viewing = None } ]
                    Attribution = { Attribution.empty with PeerUsers = Map.ofList [ bob, carol ]; UserPeers = Map.ofList [ carol, bob ] }
                    Conversation =
                        ConversationProjection.ofItems
                            [ { MessageId = MessageId.create "msg-carol" |> expect
                                Author = UserRef carol
                                Content = ItemContent.Message ("on it")
                                Status = Complete
                                Offset = EventOffset.create 1L |> expect
                                Woke = None; CausedBy = None } ]
                    Timeline = TimelineProjection.empty }
            let html = Support.render model
            let mark = Entity.actorMark model (UserRef carol)
            // The roster's row is the PERSON's — one a person, however many peers they are on
            // — so it is carol's row that bob's caret puts on it.
            let rosterRow =
                let start = html.IndexOf (Dom.attr Dom.Hooks.peerPresence (ActorRef.token (UserRef carol)))
                Expect.isTrue (start >= 0) "carol is on the roster"
                html.Substring (start, html.IndexOf ("</div>", start) - start)
            Expect.isTrue (rosterRow.Contains mark) "the roster row wears the person's mark"
            Expect.isTrue ((messageMetaOfLabel (UserId.value carol) html).Contains mark) "and so does the chat's author line"

        // A sentence that points at a person shows the person — the name the roster knows,
        // drawn as they are drawn everywhere else — and never their subject. The prose reader
        // gets `user:carol@example.com`; a screen has a roster and uses it. What is pinned is
        // that the reference element carries the resolved name and not the raw one; how a
        // reference is drawn is the design.
        testCase "a phrase's reference to a person is drawn as the person, not their subject" <| fun () ->
            let model =
                { representativeModel with
                    Peers = Map.ofList [ bob, "quiet-otter" ]
                    Attribution = { Attribution.empty with PeerUsers = Map.ofList [ bob, carol ]; UserPeers = Map.ofList [ carol, bob ] } }
            let phrase = [ Segment.Text "pushed on behalf of "; Segment.Ref (EntityRef.Actor (UserRef carol)) ]
            let html = Entity.phrase model ActorRef.Agent phrase |> List.map Support.renderTemplate |> String.concat ""
            let start = html.IndexOf (Dom.attr "data-entity" (EntityRef.said (EntityRef.Actor (UserRef carol))))
            Expect.isTrue (start >= 0) "the reference is its own element, found by the prose spelling"
            let element = html.Substring start
            Expect.isTrue (element.Contains ">quiet-otter<") "and it shows the person's name"
            Expect.isFalse (element.Contains ">carol@example.com<") "never the subject as a name"
            Expect.isTrue (html.StartsWith "pushed on behalf of ") "the words around it are words"

        // Send is never withdrawn for want of content: an empty draft is not a blocked one,
        // so the control that sends it stays whole whether there is anything to send or not.
        // `Resilience.fs` pins the same thing against a dead feed.
        testCase "send is offered whether or not there is a draft to send" <| fun () ->
            let empty = Support.render { representativeModel with Synced = { representativeModel.Synced with Drafts = Map.empty } }
            Expect.isTrue
                (empty.Contains (Dom.attr Dom.Hooks.sendDraft (PeerId.value ada)))
                "send keeps its place either way — it is never taken away"
    ]

// A turn in flight is ONE fact, and this is the suite that keeps it being said once.
//
// It used to be said three times at once: the streaming message's meta line wore a pulsing dot
// and the word `streaming`, its body wore the caret, and a 48px band under the timeline wore a
// second pulse, the sentence "agent is responding", the turn's number and a bordered Interrupt.
// Three animated marks for one fact, on a phone screen that had room for about six.
//
// What is pinned here is the COUNT, never the shape: a redesign may move the mark, restyle it,
// or put the stop control somewhere else entirely, and these stay green. Two of anything is
// what goes red.
let private agentTurnTests =
    testList "What a turn in flight says about itself" [
        testCase "a turn in flight is marked exactly once" <| fun () ->
            Expect.equal
                (occurrences Dom.Hooks.agentWriting (Support.render representativeModel))
                1
                "one mark, in the timeline, where the words are landing"

        testCase "a turn in flight offers exactly one way to stop it" <| fun () ->
            Expect.equal
                (occurrences Dom.Hooks.interruptTurn (Support.render representativeModel))
                1
                "one control, on the band where a person answers the turn"

        // The sentence survives for the people the caret cannot reach, and for nobody else. A
        // second copy of it is the strip coming back.
        testCase "the sentence a screen reader is told exists in one place" <| fun () ->
            let html = Support.render representativeModel
            Expect.equal (occurrences Dom.Text.agentResponding html) 1 "said once"
            Expect.isTrue
                (html.Contains (Dom.hookText Dom.Hooks.agentStream Dom.Text.agentResponding))
                "and it is the live region that says it"

        // The screenshot that started this: a turn accepted, nothing said yet. The mark stands
        // where the first word will land — which is the whole of what it is for.
        testCase "a turn that has said nothing yet still marks where the words will land" <| fun () ->
            Expect.equal
                (occurrences Dom.Hooks.agentWriting (Support.render silentTurnModel))
                1
                "an empty body is still a message arriving"

        testCase "nothing running: nothing marked" <| fun () ->
            Expect.equal
                (occurrences Dom.Hooks.agentWriting (Support.render restingModel))
                0
                "no turn, no mark"

        testCase "nothing running: nothing to stop" <| fun () ->
            Expect.equal
                (occurrences Dom.Hooks.interruptTurn (Support.render restingModel))
                0
                "a stop control over nothing is a control that cannot work"

        // A live region announces what CHANGES inside it, so it has to be there BEFORE the
        // sentence is: mounted at rest, empty, waiting. A region that appeared with its text
        // already in it is a region several screen readers never read out.
        testCase "the live region is mounted before there is anything to say" <| fun () ->
            let html = Support.render restingModel
            Expect.equal (occurrences Dom.Hooks.agentStream html) 1 "mounted"
            Expect.isTrue (html.Contains (Dom.hookText Dom.Hooks.agentStream "")) "and saying nothing"
    ]

let tests =
    testList "Acceptance" [
        uiChecklistTests
        agentTurnTests
        terminalListTests
        contentListTests
        paneNewTests
        paneActionsTests
        presenceTests
        syncStatusTests
        chromeTests
        semanticsTests
        reconnectOfferTests
        templateHoleTests
        holeContextTests
        shellTests
    ]
