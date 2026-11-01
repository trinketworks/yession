namespace Yession.App

open Yession.Domain
open Yession.Domain.Access
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
open Yession.Domain.Files
open Yession.Domain.Prs
open Fable.BrowserExtras
open Lit

/// The client shell as Fable.Lit templates. The view is a total function of the model
/// (plus injected `ViewActions` for the few things a template cannot derive from the
/// model: the draft sends, the sidebar toggle, focus moves). In the browser Lit
/// renders it into `#app` on every model change (no manual innerHTML, no delegation, no
/// focus juggling — Lit diffs); the host renders the same templates to a string for the
/// served bootstrap (`Yession.Host.Ssr`).
///
/// Markup contract: every observable element carries a `data-*` hook. lit-html cannot
/// inject attribute *names* through a hole, so the hook names are written literally here;
/// they ARE the `Dom.Hooks` vocabulary the tests assert against, and the tests fail loudly
/// if the two drift. Classes are `Style.*` compositions (presentation only, no behaviour).

/// The side-effecting actions a template needs but cannot compute from the model. Injected
/// so the view stays free of Guid/random and of the connection; the browser supplies real
/// implementations, tests and SSR supply no-ops.
type ViewActions =
    { /// Send the local peer's draft: mint a queue id, copy the draft body fragment into the
      /// new queue entry, and enqueue. Imperative because the fragment content-copy (shared
      /// types can't be re-parented) can't live in the pure reducer.
      SendDraft : PeerId -> unit
      /// Broadcast the local selection in a collaborative INPUT — the session title, a
      /// chapter's name — as `(anchor, head)` UTF-16 indices (`None` = the caret left it),
      /// so collaborators see the cursor. The Browser resolves the field to the `Y.Text`
      /// behind it, turns the indices into relative positions over that text, and relays
      /// them; ephemeral presence.
      ///
      /// One action for every such field rather than one per field: the indices mean the
      /// same thing in each, and a second reporter would be a second thing that can forget
      /// to clear. Bodies do not come through here — an editor knows its own relative
      /// selection and reports it through `Links.ReportFocus`.
      ReportFieldSelection : FocusField -> (int * int) option -> unit
      /// Send a terminal composer slot: enqueue its command. Imperative for exactly the
      /// reason `SendDraft` is — the command text is a shared type the reducer cannot move.
      SendTerminalDraft : TerminalId -> PeerId -> unit
      /// Send keystrokes to a terminal this peer holds (Plan 14, stage 6). Imperative
      /// because it is a frame, and deliberately not acknowledged: a keystroke that needed a
      /// reply would make typing a round trip. The Session checks the lease, which
      /// is the only place it CAN be checked — a client that believes it holds one may be
      /// looking at a steal it has not seen yet.
      TypeIntoTerminal : TerminalId -> string -> unit
      /// Take files a person picked, pasted or dropped, to send into the artifacts. Imperative
      /// because a file's bytes are not model data: the browser keeps them and tells the model
      /// what it is keeping (`UploadPickedMsg`).
      Upload : Browser.Types.File list -> unit
      /// Take files a person picked, pasted or dropped in the composer, to attach to that
      /// author's draft (`AttachPickedMsg`). Imperative for the same reason as `Upload`.
      Attach : PeerId -> Browser.Types.File list -> unit
      /// Run a change to the model as a MORPH: what is named alike on the page before and
      /// after it (`view-transition-name`) moves from one place to the other rather than
      /// vanishing in one and appearing in the other. Imperative because only the browser can
      /// snapshot the page around a render; where it cannot, the change simply happens.
      Morph : (unit -> unit) -> unit }

module ViewActions =
    /// A no-op action set for rendering the view to a string (SSR + tests). The handlers
    /// are never invoked while rendering — they fire on user events in the live browser.
    let ssr : ViewActions =
        { SendDraft = ignore
          ReportFieldSelection = fun _ _ -> ()
          SendTerminalDraft = fun _ _ -> ()
          TypeIntoTerminal = fun _ _ -> ()
          Upload = ignore
          Attach = fun _ _ -> ()
          Morph = fun change -> change () }

module View =

    // --- Label helpers: map model cases to the shared `Dom.Text` tokens -----------------

    let private connectionLabel =
        function
        | SessionLink.Connecting { Failed = Some _ }
        | SessionLink.Reconnecting ({ Failed = Some _ }, _) -> Dom.Text.retrying
        | SessionLink.Connecting _ -> Dom.Text.connecting
        | SessionLink.Reconnecting _ -> Dom.Text.reconnecting
        | SessionLink.Fetching _
        | SessionLink.Stalled _
        | SessionLink.Current -> Dom.Text.connected
        | SessionLink.Refused _ -> Dom.Text.disconnected

    let private feedToken =
        function
        | SessionLink.Fetching (_, { Failed = Some _ }, _) -> Dom.Text.feedRetrying
        | SessionLink.Stalled _ -> Dom.Text.feedPaused
        | SessionLink.Fetching _
        | SessionLink.Connecting _
        | SessionLink.Reconnecting _
        | SessionLink.Current
        | SessionLink.Refused _ -> Dom.Text.feedLive

    /// The mechanism behind a notice, folded away under one word.
    ///
    /// Every surface that reports a fault has two things to say and they are not equals: what
    /// it costs the reader, and why it is happening. The second used to sit beside the first
    /// in the same faint sentence — a transport's reason, an OAuth provider's words, the
    /// browser's storage rules — and on the strip over the timeline it was CONCATENATED with
    /// the first by a `·`, so the promise that the work was safe arrived as the tail of a
    /// sentence about an attempt counter.
    ///
    /// So: the consequence stays on the surface, and this takes the rest. A real
    /// `<details>`/`<summary>`, like the timeline's tool runs and a block's facts — the
    /// browser's own disclosure, so it is keyboard-operable and announced without any of it
    /// being this view's to arrange. Nothing is hidden from a reader who cannot open it
    /// either: the words are in the document, which is what a `<details>` is FOR and what a
    /// tooltip would not have been.
    ///
    /// TOTAL over an empty list, so a surface with no mechanism to explain renders no
    /// control — a disclosure over nothing is a promise of detail that is not there.
    let private detailNote (token: string) (why: string list) : TemplateResult =
        match why |> List.filter (fun w -> w <> "") with
        | [] -> Lit.nothing
        | why ->
            let line (w: string) = html $"""<span class="{Style.detailBody}">{w}</span>"""
            html $"""
                <details class="{Style.detailNote}" data-detail="{token}">
                  <summary class="{Style.detailSummary}">{Dom.Text.details}</summary>
                  {why |> List.map line}
                </details>"""

    let private messageStatusLabel =
        function
        | Complete -> Dom.Text.complete
        | Streaming -> Dom.Text.streaming
        | ConversationItemStatus.Running -> Dom.Text.running
        | ConversationItemStatus.Failed -> Dom.Text.failed

    let private environmentLabel =
        function
        | EnvironmentNotStarted -> Dom.Text.envNotStarted
        | EnvironmentStarting -> Dom.Text.envStarting
        | EnvironmentRunning _ -> Dom.Text.envRunning
        | EnvironmentFailed _ -> Dom.Text.envFailed
        | EnvironmentDown -> Dom.Text.envStopped

    // --- Sidebar ------------------------------------------------------------------------

    /// The way back to a session this page cannot reach (Plan 11): reopening it through the
    /// Manager, which launches a session that stopped and hands the browser back.
    ///
    /// Offered while an attempt to reach the session is FAILING, and behind the status line's
    /// disclosure rather than on it. The page cannot tell a session that stopped from one it
    /// cannot reach — both answer nothing, or a gateway's error — so it does not claim the
    /// first; but reopening is the one thing a person can do about it, so it is never further
    /// than the reason it fixes. A refused page is not offered it: what it needs is a sign-in,
    /// which reopening does not give (`StatusVerb.SignInAgain`).
    ///
    /// TOTAL over the model, which is what makes the failure modes structural rather than
    /// defensive: reopening needs a failing attempt, a Manager to ask, and a session to ask
    /// for. Absent any of them there is no link, rather than a link with nowhere to go. The
    /// view never reads the DOM, so the shell omitting the meta tag is enough.
    ///
    /// A plain anchor, with NO click handler. It had one — `location.assign` of the same
    /// URL, described as an enhancement over the href — and the two fired together: every
    /// press sent `GET …/open` twice, milliseconds apart, and each GET is a launch. The
    /// Manager took both, spawned two children for one session, and the one it forgot kept
    /// its port and its OIDC registration; the login bounce then redeemed its code against
    /// whichever registration had won, and the page ended on a bare `authorization failed`.
    /// The navigation IS the mechanism, and a link performs it once.
    let private reopenAction (model: ClientModel) : TemplateResult option =
        let failing =
            match model.Link with
            | SessionLink.Connecting { Failed = Some _ }
            | SessionLink.Reconnecting ({ Failed = Some _ }, _) -> true
            | SessionLink.Connecting _
            | SessionLink.Reconnecting _
            | SessionLink.Fetching _
            | SessionLink.Stalled _
            | SessionLink.Current
            | SessionLink.Refused _ -> false
        match failing, model.Manager, model.Session with
        | true, Some origin, Some sessionId ->
            let target = ManagerRoute.at origin (ManagerRoute.OpenSession sessionId)
            Some (
                html $"""
                    <a class="{Style.statusVerbLink}" href="{target}"
                       data-session-reopen="{target}">{Dom.Text.reopenSession}</a>""")
        | _ -> None

    /// The one thing a status line offers to do, when it offers anything. A word the size of
    /// the line it rides, never a box: the line is a notice, and a filled block in it was the
    /// loudest thing on a screen that was otherwise working.
    [<RequireQualifiedAccess>]
    type private StatusVerb =
        /// Ask again, here and now: the link, or reads that gave up (`RetryNowMsg`). It acts in
        /// place, so it is a button, in ink.
        | TryAgain
        /// Sign in again, through the session's own sign-in, which comes back to this page. It
        /// goes somewhere, so it is a link, in blue.
        | SignInAgain
        /// Load the page again (`PageFault`). It goes somewhere, the page it is on, so it is a
        /// link, like `SignInAgain`.
        | Reload

    /// What the status line says, when the page's link is worth a word at all.
    type private StatusLine =
        { /// The state's token, on `data-degraded`: the same name, spelled for a machine.
          Token : string
          /// The state's name, in the caps voice, led by its dot.
          Word : string
          /// WRONG — red, its dot still — rather than a wait, whose dot beats.
          Wrong : bool
          /// What it costs, said on the surface where there is room for a sentence, for a state
          /// that waits on a person: the cost is what decides whether to act. Every other state
          /// says it behind the disclosure.
          Promise : string option
          /// Behind the disclosure: what it costs where that is not said above, why it is
          /// happening, and, where the deployment cannot keep the promise, why it cannot.
          Why : string list
          Verb : StatusVerb option }

    /// What the client's link is doing, when it is doing something worth saying — and `None`
    /// while it is healthy.
    ///
    /// Nothing is said about health. "Connected", "Up to date" and a green dot used to be on
    /// three surfaces at once (the header, the sidebar's sync row, and the sidebar again on
    /// the feed's line), which made the least actionable fact on the screen the loudest one.
    /// What is left is the link's own state token on `data-connection` — a value a test reads,
    /// not words a person does.
    ///
    /// ONE function, two mounts: the nav column carries the line where the column is on
    /// screen, and `degradedBar` carries it where the column is not — a phone, or a collapsed
    /// nav. They cannot disagree, and the visibility rule means only ever one is seen.
    ///
    /// A state keeps its name whatever is happening to it. A failing attempt to connect is
    /// still connecting, and the failure is a reason: it goes behind the disclosure with how
    /// many attempts have failed, and the line offers to try again. "Retrying" was a second
    /// name for the same wait, which a reader had to learn meant connecting.
    let private connectionReport (model: ClientModel) : StatusLine option =
        // Why the promise reads the way it does. Only an ephemeral deployment has anything to
        // explain: on a stable address the sentence IS the whole story.
        let promise =
            if model.EphemeralStorage then Dom.Text.localFallbackEphemeral else Dom.Text.localFallback
        let why =
            [ if model.EphemeralStorage then Dom.Text.ephemeralAddress ]
        let waiting (token: string) (word: string) (failure: Failure option) : StatusLine =
            { Token = token
              Word = word
              Wrong = false
              Promise = None
              Why = promise :: (failure |> Option.map (fun f -> Dom.Text.retryingWhy f.Reason f.Count) |> Option.toList) @ why
              Verb = failure |> Option.map (fun _ -> StatusVerb.TryAgain) }
        // What the line speaks for: what it is saying (`Said`), which may still be the link a
        // moment ago — a showing stands for its second — or, where nothing has begun to be
        // said, the link as it is.
        let link =
            match model.Said with
            | Some said -> said.Link
            | None -> model.Link
        // One state, so one report: a page that cannot reach its session is not reading its
        // history either, and is not said to be.
        let report =
            match link with
            // The one state that waits on a person: the session would not have this page. What
            // fixes it is signing in again, which is where the line goes.
            | SessionLink.Refused (reason, _) ->
                Some
                    { Token = Dom.Text.degradedSignedOut
                      Word = Dom.Text.signedOutStatus
                      Wrong = true
                      Promise = Some Dom.Text.signedOutPromise
                      Why = [ reason ]
                      Verb = Some StatusVerb.SignInAgain }
            | SessionLink.Connecting { Failed = Some failure } ->
                Some (waiting Dom.Text.degradedRetrying Dom.Text.connectingStatus (Some failure))
            | SessionLink.Reconnecting ({ Failed = Some failure }, _) ->
                Some (waiting Dom.Text.degradedRetrying Dom.Text.reconnectingStatus (Some failure))
            // An attempt that has run past `ClientModel.connectingQuietMs`. Every client starts
            // connecting, and one that lands inside the window says nothing, so this is never a
            // flash on a healthy load — but one that does not land is news, and a page that said
            // nothing through it looked exactly like a page that had loaded and had nothing in it.
            // A link lost and found again inside the same window is the same: a phone drops its
            // link each time it leaves the app, and has it back as it returns.
            | SessionLink.Connecting { Slow = true } ->
                Some (waiting Dom.Text.degradedConnecting Dom.Text.connectingStatus None)
            | SessionLink.Reconnecting ({ Slow = true }, _) ->
                Some (waiting Dom.Text.degradedReconnecting Dom.Text.reconnectingStatus None)
            // Reads failing while the policy tries them again: said, with nothing to press, because
            // the policy is already doing what a press would.
            | SessionLink.Fetching (_, { Failed = Some failure }, _) ->
                Some { waiting Dom.Text.feedRetrying Dom.Text.historyRetryingStatus (Some failure) with Verb = None }
            // History stopped arriving, and nothing will re-arm it until the session has news. It
            // is wrong rather than a wait, and a press asks again now.
            | SessionLink.Stalled (_, reason, _) ->
                Some
                    { Token = Dom.Text.feedPaused
                      Word = Dom.Text.historyPausedStatus
                      Wrong = true
                      Promise = None
                      Why = promise :: reason :: why
                      Verb = Some StatusVerb.TryAgain }
            // A wait inside its quiet window is not a degradation and says nothing — a strip that
            // flashed on every load would be chrome, not news.
            | SessionLink.Connecting _
            | SessionLink.Reconnecting _
            | SessionLink.Fetching _
            | SessionLink.Current -> None
        // An error nothing handled, heard while the page is on its way to its session, may be
        // what keeps it there: the way to the session can stop on one, and the page then waits
        // in `Connecting` for as long as it is open. Whether it did stop is not knowable here,
        // because an error some part of the page recovered from looks the same, so the line
        // keeps its word and says the error behind its disclosure, with a reload, which starts
        // the way again whichever it was. A page that has its session says nothing of one.
        match report, model.PageFault, link with
        | Some line, Some fault, (SessionLink.Connecting _ | SessionLink.Reconnecting _) ->
            Some
                { line with
                    Why = line.Why @ Dom.Text.pageFaultLead :: Dom.Text.pageFaultWhy fault.Name fault.Message :: List.truncate 8 fault.Stack
                    Verb = line.Verb |> Option.orElse (Some StatusVerb.Reload) }
        | _ -> report

    /// A status line's dot, on its own: it stands in the bar's gutter, and hangs to the left of
    /// the word in the nav column, so in neither does it push the word off the rail. A wait's
    /// beats; a wrong one stands still.
    let private statusLineMark (line: StatusLine) : TemplateResult =
        if line.Wrong then
            html $"""<span class="{Style.statusLineMarkWrong}" aria-hidden="true"></span>"""
        else
            Icon.spin

    let private statusLineWord (line: StatusLine) : TemplateResult =
        let voice = if line.Wrong then Style.statusErr else Style.statusWait
        html $"""<span class="{voice} {Style.statusLineWord}">{line.Word}</span>"""

    /// The line's verb, the same on both mounts. Its hit area grows to a thumb only on a phone,
    /// where the only mount that shows is the bar.
    let private statusLineVerb (dispatch: ClientMsg -> unit) (line: StatusLine) : TemplateResult =
        match line.Verb with
        | None -> Lit.nothing
        | Some StatusVerb.TryAgain ->
            html $"""
                <button type="button" class="{Style.statusVerb}" data-retry-now
                        @click={Ev(fun _ -> dispatch RetryNowMsg)}>{Dom.Text.retryNow}</button>"""
        | Some StatusVerb.SignInAgain ->
            let target = RelativeUrl.inDocument DocumentBase.shell (SessionRoute.relative SessionRoute.Login)
            html $"""<a class="{Style.statusVerbLink}" href="{target}" data-session-signin>{Dom.Text.signInAgain}</a>"""
        | Some StatusVerb.Reload ->
            let target = RelativeUrl.inDocument DocumentBase.shell (SessionRoute.relative SessionRoute.Shell)
            html $"""<a class="{Style.statusVerbLink}" href="{target}" data-page-reload>{Dom.Text.reload}</a>"""

    /// The line's disclosure: the rest of the answer somebody asks next, and — while an attempt
    /// is failing — the way back through the Manager (`reopenAction`), beside the reason it
    /// answers. `bodyClass` is the mount's: where the lines stand when it is open.
    let private statusLineDetails (token: string) (bodyClass: string) (model: ClientModel) (line: StatusLine) : TemplateResult =
        let reopen =
            match reopenAction model with
            | Some action -> html $"""<span class="{Style.statusLineReopen}">{action}</span>"""
            | None -> Lit.nothing
        match line.Why |> List.filter (fun w -> w <> "") with
        | [] -> Lit.nothing
        | why ->
            let say (w: string) = html $"""<span class="{Style.detailBody}">{w}</span>"""
            html $"""
                <details class="{Style.detailNote}" data-detail="{token}">
                  <summary class="{Style.detailSummary}">{Dom.Text.details}</summary>
                  <div class="{bodyClass}">{why |> List.map say}{reopen}</div>
                </details>"""

    /// Whether a catch-up is worth showing: PROGRESS, not health, so it is the one thing that
    /// still speaks while everything works — and only while the log is being fetched, once the
    /// fetch has lasted long enough to be something somebody is waiting on (`Effort`'s `Slow`),
    /// toward the end the session has named, since a bar needs a whole to be part of. Not
    /// linked, there is no fetch, and nothing is said.
    let private showsCatchUp (model: ClientModel) : bool =
        match model.Link with
        | SessionLink.Fetching (_, { Slow = true }, _) -> true
        | SessionLink.Fetching _
        | SessionLink.Connecting _
        | SessionLink.Reconnecting _
        | SessionLink.Stalled _
        | SessionLink.Current
        | SessionLink.Refused _ -> false

    /// The catch-up as a bar along the header's bottom rule (`Style.catchUpBar`): how much of
    /// the log this client has folded, of what it knows the log holds. A `progressbar` with
    /// its numbers on it, because a bar with no value is decoration to a screen reader.
    let private catchUpBar (model: ClientModel) : TemplateResult =
        if not (showsCatchUp model) then Lit.nothing
        else
            let consumer = model.EventConsumer
            // Offsets count from zero, so the count folded is one past the last folded.
            let folded = consumer.LastProcessedOffset |> Option.map (fun o -> EventOffset.value o + 1L) |> Option.defaultValue 0L
            let known = SessionLink.target consumer.LastProcessedOffset model.Link |> Option.map (fun o -> EventOffset.value o + 1L) |> Option.defaultValue 0L
            let percent = if known <= 0L then 0.0 else 100.0 * float (min folded known) / float known
            let width = sprintf "width: %.1f%%" percent
            html $"""<div class="{Style.catchUpBar}" role="progressbar" aria-label="{Dom.Text.catchingUp}"
                          aria-valuemin="0" aria-valuemax="{string known}" aria-valuenow="{string folded}"
                          data-catch-up-bar style="{width}"></div>"""

    /// The nav column's mount of the status line. Catching up is not said here: fetching is
    /// progress, not the link's health, and the header's bar says how far it has got on every
    /// width (`catchUpBar`), where a line here said it a second time, beside the timeline's.
    let private connectionSection (dispatch: ClientMsg -> unit) (model: ClientModel) : TemplateResult =
        let report = connectionReport model
        // ONE mount of one report, wearing one hook and one visibility rule: the browser suite
        // counts the mounts a person can see. The word on the column's rail with its dot hung
        // to the left of it, the verb at the row's end, then what it costs where that decides
        // anything, then the rest behind the disclosure.
        let body =
            match report with
            | None -> Lit.nothing
            | Some line ->
                let promise =
                    match line.Promise with
                    | Some sentence -> html $"""<span class="{Style.small}">{sentence}</span>"""
                    | None -> Lit.nothing
                html $"""
                    <div class="{Style.connectionInColumn}" data-degraded="{line.Token}">
                      <div class="{Style.statusLineRow}">
                        <span class="{Style.statusLineHung}">{statusLineMark line}</span>
                        {statusLineWord line}
                        <span class="{Style.statusLineVerbSlot}">{statusLineVerb dispatch line}</span>
                      </div>
                      {promise}
                      {statusLineDetails "connection" Style.statusLineDetailsInColumn model line}
                    </div>"""
        // The section is always in the document, and always carries the link's exact state
        // tokens, because that is the markup contract a test reads. What is conditional is
        // the words — they appear when something is wrong and at no other time — and, with
        // them, the section's own box: a section with nothing in it still costs its padding,
        // which on a healthy client is a band of empty panel under the wordmark that reads as
        // something that failed to load.
        let shape =
            if report.IsNone then Style.navLane1 else Style.cls [ Style.sideSection; Style.navLane1 ]
        html $"""
            <section class="{shape}"
                     data-feed="{feedToken model.Link}" data-connection="{connectionLabel model.Link}">
              {body}
            </section>"""

    /// Where a peer is, as the roster says it: a stable field token for the markup contract,
    /// and the words for a person. The words name things when naming them helps — the
    /// terminal they are in, whose message they are co-writing — because "somewhere" is not
    /// what anyone wanted to know.
    let private whereIs (model: ClientModel) (who: ActorRef) (field: FocusField) : string * string =
        // A terminal is NAMED when this client knows it. One that has not folded the
        // `TerminalOpened` event yet knows the peer is in some terminal and says exactly
        // that, rather than inventing a title or going quiet.
        let terminalWords () =
            ClientModel.terminalOfFocus field model
            |> Option.bind (Entity.terminalName model)
            |> Option.map Dom.Text.inTerminal
            |> Option.defaultValue Dom.Text.atSomeTerminal
        match field with
        | Title -> Dom.Text.atTitle, Dom.Text.renamingSession
        | DraftBody author when who = ActorRef.PeerRef author -> Dom.Text.atDraft, Dom.Text.writing
        | DraftBody author when author = model.Peer.PeerId -> Dom.Text.atDraft, Dom.Text.inYourDraft
        | DraftBody author -> Dom.Text.atDraft, Dom.Text.inDraftOf (ClientModel.nameOf author model)
        | QueueBody _ -> Dom.Text.atQueued, Dom.Text.editingQueued
        | TerminalDraftBody _ -> Dom.Text.atTerminal, terminalWords ()
        | TerminalQueuedBody _ -> Dom.Text.atTerminalQueued, terminalWords ()
        // A chapter is NAMED when this client has the message it opens, for the same reason a
        // terminal is — and says plainly that it does not when it does not.
        | ChapterName messageId ->
            Dom.Text.atChapter,
            ClientModel.chapterNameAt messageId model
            |> Option.map Dom.Text.namingChapter
            |> Option.defaultValue Dom.Text.atSomeChapter

    /// Who is in this session — and, when the agent is not, the ONE place the product asks for
    /// a connection. A missing member belongs in the membership list, so all three agent states
    /// wear the SAME roster row — avatar cell, name, right-aligned status — and only the words
    /// (and the prompt hanging under the row) change. Connecting flips "no agent" to what the agent is
    /// doing in place; the roster never jumps.
    /// How to read what is BELOW it. ONE renderer, used by the generated panels and by the
    /// consent prompt, because a legend that read one way where a grant is listed and
    /// another where it is agreed to would be two vocabularies wearing one name.
    ///
    /// Shut, and above what it explains. A legend is consulted before reading rather than
    /// after — it was an open list underneath, which is where a footnote goes, and eight
    /// entries at full weight under a panel nobody opened for a glossary is a wall to
    /// scroll past to reach the next answer. A real `<details>`, like the timeline's tool
    /// runs and a block's facts, so the disclosure is the browser's: keyboard-operable and
    /// announced, with no script and no state of ours to keep.
    ///
    /// The `<dl>` inside is there for the reason the query's own fields are one: these are
    /// terms and what they mean, which is exactly what the element says to a screen reader.
    /// The summary is the glossary's NAME and sits outside the list, because a `<dt>` is a
    /// term of the glossary rather than what the glossary is called.
    ///
    /// Empty renders nothing at all — a disclosure over no entries is a promise of help
    /// that is not there.
    let private legendView (entries: (string * string) list) : TemplateResult =
        match entries with
        | [] -> html $""""""
        | entries ->
            let rows =
                entries
                |> List.map (fun (shape, meaning) ->
                    html $"""
                        <div class="{Style.queryLegendEntry}">
                          <dt class="{Style.queryLegendShape}">{shape}</dt>
                          <dd class="{Style.queryLegendMeaning}">{meaning}</dd>
                        </div>""")
            html $"""
                <details class="{Style.queryLegend}" data-legend="{List.length entries}">
                  <summary class="{Style.queryLegendSummary}">how to read<span class="{Style.queryLegendMark}">{Icon.rights}</span></summary>
                  <dl class="{Style.queryLegendEntries}">{rows}</dl>
                </details>"""

    /// Where this session stands — the standings the Manager's roster shows for it, worst
    /// first (`ClientModel.standings`) — at the top of the column, where they are said at every
    /// width. The header used to say them, which on a phone spent the title's band on a word
    /// the conversation was already showing; the band now wears only the worst one's mark,
    /// under the chevron that opens this (`navReopenMark`).
    ///
    /// Each is a roster row: its attention mark in the avatars' column, its word on the names'
    /// rail, so the block and the people under it are one column of marks and one of words.
    /// The pull requests' line is pressable, because it GOES somewhere — the rows behind it
    /// are one press away in settings. Nothing to say, no block: a heading over nothing is a
    /// thing to learn to skip.
    let private standingsSection (dispatch: ClientMsg -> unit) (model: ClientModel) : TemplateResult =
        match ClientModel.standings model |> Standings.Standing.byConcern with
        | [] -> Lit.nothing
        | standings ->
            let row (standing: Standings.Standing) =
                let mark = html $"""<span class="{Style.standingMarkSeat}">{Icon.attention standing.Attention}</span>"""
                let word = html $"""<span class="{Style.cls [ Style.givesWay; Style.attentionInk standing.Attention ]}">{standing.Word}</span>"""
                if standing.Facet = PrStatus.facet then
                    // Taken to the pull-requests panel, and to its one row when the strip
                    // can name it (`ClientModel.prPanelRow`) — the same jump a chapter
                    // makes to a message, not the generic `ToggleSettingsMsg` that used to
                    // sit here and land on the settings face's own pivot instead of on
                    // what was pressed.
                    let target = ClientModel.prPanelRow model
                    html $"""
                        <button type="button" class="{Style.standingRowPress}" data-pr-strip @click={Ev(fun _ -> dispatch (MoveMsg (DomMove.RevealQueryRow (PrStatus.Columns.query, target))))}>
                          {mark}<span class="{Style.srOnly}">pull requests: </span>{word}<span class="{Style.standingRowGo}">{Icon.right}</span>
                        </button>"""
                else
                    html $"""<div class="{Style.person}" data-agent-standing>{mark}{word}</div>"""
            html $"""
                <section class="{Style.cls [ Style.sideSection; Style.navLane1 ]}" data-standings>
                  <span class="{Style.label}">status</span>
                  {standings |> List.map row}
                </section>"""

    let private peopleSection (actions: ViewActions) (dispatch: ClientMsg -> unit) (model: ClientModel) : TemplateResult =
        // The agent's row says whether a turn can RUN, which is not the same question as
        // whether a credential is stored. `agentAvailable` answers the second (any relevant
        // credential, or the host's ambient one), so a Claude sign-in that has stopped
        // working left this reading "ready" in green over a credential the next turn would
        // fail on. The status word follows the credential's health; the gate does not, so
        // the no-agent prompt keeps meaning "nothing is connected" rather than doubling up.
        //
        // What the agent is DOING is the status block's (`standingsSection`), so this row says
        // who is here and, when its credential stopped working, the verb that fixes it — the
        // same seat and voice as an absent agent's "connect".
        let agentRow =
            // `None` is the stream not having said yet, which is the one state this row has
            // nothing to say in — it was a third case of a `bool option` and is the absence
            // of the panel now.
            match model.Claude.Status |> Option.map (fun panel -> panel.AgentAvailable) with
            | Some true ->
                let verb =
                    if ClientModel.claudeNeedsSignIn model then
                        html $"""<button type="button" class="{Style.cls [ Style.btnBare; Style.rosterVerb ]}" data-agent-sign-in @click={Ev(fun _ -> dispatch ToggleSettingsMsg)}>{Dom.Text.signInAgainStatus}</button>"""
                    else Lit.nothing
                html $"""<div class="{Style.person}" data-agent-presence="live"><span class="{Style.cls [ Style.avatar; Style.agentAvatar; Style.personAvatar ]}"></span>agent{verb}</div>"""
            // One roster row, like the live one: the dimmed avatar says nobody is here, and the
            // bare verb at the right says what to do about it. It used to hang a full-width
            // bordered button under the row with a rule beside it, which took the roster's
            // second slot for good and repeated the button the settings face already wears.
            // The verb opens that face (`ToggleSettingsMsg`), where the full one lives. What a
            // message does meanwhile (recorded, unanswered — `Scheduler.create`, a `None`
            // runner at drain time) is behaviour the queue itself shows, not a sentence to
            // hang here.
            | Some false ->
                html $"""
                    <div class="{Style.person}" data-agent-presence="absent" data-no-agent><span class="{Style.cls [ Style.avatar; Style.agentAvatarAbsent; Style.personAvatar ]}"></span><span class="text-ink-faint">agent</span><button type="button" class="{Style.cls [ Style.btnBare; Style.rosterVerb ]}" data-settings-toggle="prompt" data-no-agent-connect @click={Ev(fun _ -> dispatch ToggleSettingsMsg)}>connect</button></div>"""
            | None ->
                html $"""<div class="{Style.person}" data-agent-presence="unknown"><span class="{Style.cls [ Style.avatar; Style.agentAvatarAbsent; Style.personAvatar ]}"></span><span class="text-ink-faint">agent</span></div>"""

        // Everyone else who is here, and WHERE when they are somewhere. The same roster row as
        // yours and the agent's — avatar, name, right-aligned slot — so the section is one list
        // rather than a list with an appendix, and a collaborator moving from the composer to a
        // terminal changes the words in place without moving anything. Somebody who is simply
        // here (reading, idle) has a row with no slot: no word is truer than a made-up place.
        let peerRows =
            ClientModel.roster model
            |> List.map (fun (who, name, at) ->
                let slot =
                    match at with
                    | Some (editor, field) ->
                        let token, words = whereIs model editor field
                        // The slot TRUNCATES rather than holding its width: where a peer is
                        // used to be a word or two, and a chapter's name made it a line of
                        // somebody's message — which pushed itself, and the peer's name with
                        // it, off the side of the sidebar. Capped at half the row because
                        // truncation alone spends the row on the longer of the two, and the
                        // one that has to survive is WHOSE row it is.
                        html $"""<span class="{Style.cls [ Style.label; "ml-auto min-w-0 max-w-1/2 truncate" ]}" data-peer-at="{token}">{words}</span>"""
                    | None -> html $""""""
                html $"""
                    <div class="{Style.person}" data-peer-presence="{ActorRef.token who}">
                      <span class="{Style.cls [ Style.avatar; Entity.actorMark model who; Style.personAvatar ]}"></span>
                      <span class="{Style.givesWay}">{name}</span>
                      {slot}
                    </div>""")
        html $"""
            <section class="{Style.cls [ Style.sideSection; Style.navLane1 ]}">
              <span class="{Style.label}">in this session</span>
              <div class="{Style.person}"><span class="{Style.cls [ Style.avatar; Entity.actorMark model (PeerRef model.Peer.PeerId); Style.personAvatar ]}"></span><span class="truncate" data-display-name>{model.Peer.DisplayName}</span><span class="{Style.personYou}">you</span></div>
              {peerRows}
              {agentRow}
            </section>"""

    /// Something still under way, said by a beating dot; the word is for screen readers. Live,
    /// so green: blue is never a status (docs/visual-design.md, Colour).
    let private runningDot =
        html $"""<span class="{Style.statusOk}">{Icon.spin}<span class="{Style.srOnly}">{Dom.Text.blockRunning}</span></span>"""

    /// A terminal's state, as a small mark beside its name: running, its last command failed,
    /// or closed — and nothing at all for one that is open, idle and fine, which is most of
    /// them. ONE vocabulary for the pivot, the `all` page and the sidebar's environment, so a
    /// terminal reads the same in the row of names and in each list of them: the beat is the
    /// running block's own (`runningDot`), a failure the same dot standing still in the error
    /// red, and a closed terminal the dot hollowed out. A mark beside the name, never a
    /// box round it or a word in caps after it — a row of names is what a reader scans — and
    /// each says itself to a screen reader.
    ///
    /// Running, failed and closed are three SHAPES — beating, filled, hollow — not one shape
    /// in three colours. Each word here is `srOnly`, so a sighted reader has only the mark,
    /// and colour may not be the only thing that distinguishes two of them (WCAG 2.0 SC 1.4.1).
    ///
    /// And what is NEWS to the person reading (`ClientModel.unseen`): a terminal that finished
    /// something since they last looked at it wears the settled dot with a ring round it — in
    /// ink when all of it went through, because a finished thing is a record and not live, and
    /// in the error red when something did not, because that is the newest wrong thing. Without it a build that
    /// finished in another tab looked exactly like a terminal nobody had touched. The ring
    /// goes when they look, and a failure then settles to the plain red dot it always wore —
    /// so an unseen failure is the louder of the two, never the quieter. Running and closed
    /// still win: what a terminal is doing now is the first thing to say about it.
    let private terminalMark (model: ClientModel) (view: TerminalView) : TemplateResult =
        let mark (token: string) (voice: string) (glyph: TemplateResult) (word: string) =
            html $"""<span class="{Style.pivotMark} {voice}" data-pane-mark="{token}"><span aria-hidden="true">{glyph}</span><span class="{Style.srOnly}">{word}</span></span>"""
        let news = html $"""<span class="{Style.statusDotNews}"></span>"""
        match ClientModel.terminalState view model with
        // Hollow: the dot with nothing left in it. Not a stop square, which is what a running
        // command's Stop looks like. The SHAPE is what tells this from the failed row below,
        // whose word is `srOnly` as this one's is — colour alone would leave a sighted reader
        // nothing (`Style.statusDotHollow` has the criterion).
        | TerminalState.Closed ->
            mark "closed" "text-ink-faint" (html $"""<span class="{Style.statusDotHollow}"></span>""") Dom.Text.markClosed
        | TerminalState.Running ->
            html $"""<span class="{Style.pivotMark}" data-pane-mark="running">{runningDot}</span>"""
        | TerminalState.UnseenFailed -> mark "unseen-failed" "text-err" news Dom.Text.markUnseenFailed
        | TerminalState.UnseenOk -> mark "unseen-ok" "text-ink" news Dom.Text.markUnseenOk
        // A solid dot in the error red, not the block's own cross: beside a name in a row
        // whose selected item wears a × that KILLS, a red × read as a second kill.
        | TerminalState.Failed ->
            mark "failed" "text-err" (html $"""<span class="{Style.statusDotSolid}"></span>""") Dom.Text.markFailed
        | TerminalState.Idle -> Lit.nothing

    let private environmentStatus =
        function
        | EnvironmentNotStarted -> Style.statusFaint, html $"""not started"""
        | EnvironmentStarting -> Style.statusWait, html $"""{Icon.spinLead}starting"""
        | EnvironmentRunning _ -> Style.statusOk, html $"""<span class="{Style.statusDot}"></span>running"""
        | EnvironmentFailed _ -> Style.statusErr, html $"""failed"""
        | EnvironmentDown -> Style.statusFaint, html $"""stopped"""

    /// The words the environment gave for failing to start: not drawn, but carried on the section
    /// (`Dom.Hooks.environmentReason`) for whatever waits on the page. Total over the status, so
    /// a new state is a decision here and not a silent blank.
    let private environmentReason =
        function
        | EnvironmentFailed reason -> reason
        | EnvironmentNotStarted
        | EnvironmentStarting
        | EnvironmentRunning _
        | EnvironmentDown -> ""

    /// How many terminals the environment lists before it hands the rest to the pane's `all`
    /// page. A CAP rather than a column that scrolls: on a phone this column is a drawer, and
    /// a list that grew with the session pushed `settings` off the bottom of it — the one way
    /// to settings — behind a scroll nobody knew was there. Five 44px rows and the way to
    /// the rest fit under the roster on a 390x844 screen; the `all` page is already the
    /// complete list, with every verb, so the rest is one press away rather than a second
    /// copy of it.
    let private environmentTerminalCap = 5

    /// The environment: whether it is up, and what is running in it — the session's open
    /// terminals, each the way into the pane on it.
    ///
    /// An entry is `OpenInPaneMsg` with the read the `all` page's row would choose
    /// (`ClientModel.chosenRead`), so a terminal is reached ONE way whichever list it was
    /// picked from; on a phone that message also shuts the drawer (`drawerShut`). A CLOSED
    /// terminal is not listed: this says what is running, and a recording is the `all` page's
    /// to offer. Nothing open, nothing listed — the status row is the whole section.
    let private environmentSection (dispatch: ClientMsg -> unit) (model: ClientModel) : TemplateResult =
        let status = model.Environment
        let statusClass, statusInner = environmentStatus status
        let shown = ClientModel.shownTerminal model
        let entry (view: TerminalView) =
            // `aria-current` is the selection, for a reader and for the row's fill alike.
            html $"""
                <button type="button" class="{Style.terminalEntry}"
                        data-environment-terminal="{TerminalId.value view.TerminalId}"
                        aria-current="{if shown = Some view.TerminalId then "true" else "false"}"
                        @click={Ev(fun _ -> dispatch (OpenInPaneMsg (ClientModel.chosenRead view.TerminalId model)))}>
                  <span class="{Style.givesWay}">{TerminalName.display model.Terminals view}</span>{terminalMark model view}
                </button>"""
        let terminals =
            // In the order they were opened, as the strip reads.
            match Projection.openTerminals model.Terminals with
            | [] -> Lit.nothing
            | live ->
                let shown = List.truncate environmentTerminalCap live
                let more =
                    match List.length live - List.length shown with
                    | 0 -> Lit.nothing
                    | rest ->
                        html $"""
                            <button type="button" class="{Style.terminalEntry}"
                                    data-environment-more="{rest}" aria-label="{Dom.Text.moreTerminals rest}"
                                    @click={Ev(fun _ -> dispatch OpenAllMsg)}>{Dom.Text.moreShort rest}</button>"""
                html $"""
                    <div class="flex flex-col" data-environment-terminals>
                      {shown |> List.map entry}{more}
                    </div>"""
        html $"""
            <section class="{Style.cls [ Style.sideSection; Style.navLane2 ]}" data-environment="{environmentLabel status}" data-environment-reason="{environmentReason status}">
              <div class="{Style.sideRow}"><span class="{Style.label}">environment</span><span class="{statusClass}">{statusInner}</span></div>
              {terminals}
            </section>"""

    /// What the non-session sign-in scope actually reaches here. On a deployment that
    /// attributes its users it is that person's own; on one that attributes nobody
    /// (`--auth localhost`) it is EVERYONE who can reach this Manager, and calling that
    /// "mine" would be the panel promising something the deployment cannot keep.
    /// Unknown until the panel arrives — say the cautious thing meanwhile.
    ///
    /// BOTH panels call this. The GitHub section used to write "All my sessions" outright,
    /// which under `--auth localhost` claimed as one person's a credential the whole
    /// deployment shares — the promise the line above says a panel must not make. It could
    /// only be written that way while the owner was a string nothing obliged a renderer to
    /// read; a `SharedOwner` makes the match exhaustive.
    let private sharedScopeLabel (owner: SharedOwner option) : string =
        match owner with
        | Some OwnedByUser -> "All my sessions"
        | Some OwnedByDeployment
        | None -> "All sessions"

    /// The credential, if any, that a panel's OWN scope choice already names — "mine" reads
    /// `mine`, anything else (only "session" today) reads `session`. One function because
    /// both panels read the same two rows under the same choice (`ConnectionExpectation.landed`
    /// reads them this way already, for the wait that lands on a command).
    let private chosenCredential (scope: string) (mine: CredentialRow option) (session: CredentialRow option) : CredentialRow option =
        if scope = "session" then session else mine

    /// How a connected credential reads on a panel row: green and its kind, or the fault
    /// style and the words that name the fix. ONE function, because both connection panels
    /// say the same thing about the same shape — a credential that read "sign in again" in
    /// one panel and green in the other would be two copies having drifted, not a design.
    ///
    /// The health is a STATUS here, not a second call to action. Each panel's own Connect
    /// control is already directly below this row and IS the remedy; the one new button for
    /// this lives over the timeline, where somebody who never opens settings will meet it.
    let private credentialStatus (label: string) (credential: CredentialRow) : TemplateResult =
        match credential.SignInRequired with
        | None ->
            html $"""<span class="{Style.statusOk}"><span class="{Style.statusDot}"></span>{label} ({ConnectionKind.label credential.Kind})</span>"""
        | Some _ ->
            html $"""<span class="{Style.statusErr}"><span class="{Style.statusDot}"></span>{label}: {Dom.Text.signInAgainStatus}</span>"""

    /// Why, under the row, and only when there is a why. It is the provider's own words, so
    /// it says what a person could not have guessed — "the refresh token has expired" and
    /// "github rejected this credential" send them to the same button knowing different
    /// things about how they got here.
    ///
    /// The row above already says the consequence, in the words the prompt over the timeline
    /// uses: this credential needs signing in again. So the provider's own sentence is the
    /// mechanism here, and it folds away like every other one. This is the disclosure's
    /// INSIDE: each row writes its own `<details>`, because the hook a test reads the fault
    /// off differs by provider, and an attribute's name cannot be a hole (YES020).
    let private credentialReason (reason: string) : TemplateResult =
        html $"""
            <summary class="{Style.detailSummary}">{Dom.Text.details}</summary>
            <span class="{Style.detailBody}">{reason}</span>"""

    /// The Claude connection panel (Plan 08), living in the settings drawer: status per
    /// sign-in scope, the OAuth flow (approve on claude.ai → paste the shown code), and
    /// the paste-a-token fallback.
    let private claudeSection (dispatch: ClientMsg -> unit) (claude: ClaudeViewState) : TemplateResult =
        let connectedRow (label: string) (scopeChoice: string) (credential: CredentialRow option) =
            match credential with
            | Some credential ->
                let why =
                    match credential.SignInRequired with
                    | None -> Lit.nothing
                    | Some reason ->
                        html $"""<details class="{Style.detailNote}" data-claude-signin-required="{scopeChoice}" data-detail="credential">{credentialReason reason}</details>"""
                html $"""
                    <div class="{Style.sideRow}" data-claude-connected="{scopeChoice}">{credentialStatus label credential}<button type="button" class="{Style.cls [ Style.btnIconBareDanger; "self-center" ]}" aria-label="Disconnect" data-claude-disconnect="{scopeChoice}" @click={Ev(fun _ -> dispatch (ClaudePressedMsg (ClaudePress.Disconnect scopeChoice)))}>{Icon.close}</button></div>
                    {why}"""
            | None -> html $""""""
        let controls =
            // A command of ours in flight outranks the flow: the controls that would send a
            // second one are off the screen until this one is done — done meaning the STATUS
            // shows it, not merely that the session said yes (`Pending`).
            match claude.Flow with
            | _ when Pending.inFlight claude.Pending ->
                html $"""<span class="{Style.statusWait}" data-claude-busy>{Icon.spinLead}working…</span>"""
            | ClaudeAwaitingCode (url, _) ->
                html $"""
                    <a class="{Style.btnPrimary}" href="{url}" target="_blank" rel="noreferrer" data-claude-authorize>Approve on claude.ai</a>
                    <label class="{Style.label}" for="claude-code">code from claude.ai</label>
                    <input id="claude-code" type="text" class="{Style.field}" data-claude-code placeholder="code#state"
                           autocapitalize="off" autocorrect="off" autocomplete="off" spellcheck="false"
                           .value={claude.Code}
                           @input={EvVal(fun v -> dispatch (ClaudeCodeTyped v))} />
                    <div class="{Style.settingsActions}">
                      <button type="button" class="{Style.btn}" data-claude-complete @click={Ev(fun _ -> dispatch (ClaudePressedMsg ClaudePress.Complete))}>Complete</button>
                      <button type="button" class="{Style.btn}" data-claude-cancel @click={Ev(fun _ -> dispatch (ClaudeFlowMsg ClaudeIdle))}>Cancel</button>
                    </div>"""
            | ClaudeIdle ->
                let chosen =
                    chosenCredential
                        claude.Scope
                        (claude.Status |> Option.bind (fun panel -> panel.MineCredential))
                        (claude.Status |> Option.bind (fun panel -> panel.SessionCredential))
                // Connecting the scope just chosen makes no sense while a credential is
                // already sitting under it — the remedy is the disconnect already on that
                // scope's row above, not a second control trying to do the same thing
                // again. The row above already carries the status and the X, so there is
                // nothing left for this control to say: it renders nothing at all.
                match chosen with
                | Some _ -> html $""""""
                | None ->
                    html $"""
                        <label class="{Style.label}" for="claude-scope">sign in for</label>
                        <div class="{Style.fieldSelectWrap}">
                          <select id="claude-scope" class="{Style.fieldSelect}" data-claude-scope aria-label="Sign-in scope"
                                  @change={EvVal(fun v -> dispatch (ClaudeScopeChosen v))}>
                            <option value="mine" ?selected={claude.Scope = "mine"}>{sharedScopeLabel (claude.Status |> Option.map (fun panel -> panel.Owner))}</option>
                            <option value="session" ?selected={claude.Scope = "session"}>This session only</option>
                          </select>
                          <span class="{Style.fieldSelectMark}">{Icon.caret}</span>
                        </div>
                        <button type="button" class="{Style.btn}" data-claude-connect @click={Ev(fun _ -> dispatch (ClaudePressedMsg ClaudePress.Connect))}>Connect Claude</button>
                        <details class="{Style.cls [ Style.detailNote; "pt-2" ]}" data-claude-token-form>
                          <summary class="{Style.detailSummary}" data-claude-use-token>{Dom.Text.useTokenInstead}</summary>
                          <label class="{Style.label} pt-2" for="claude-token">setup token / api key</label>
                          <input id="claude-token" type="password" class="{Style.field}" data-claude-token placeholder="sk-ant-…"
                                 .value={claude.Token}
                                 @input={EvVal(fun v -> dispatch (ClaudeTokenTyped v))} />
                          <button type="button" class="{Style.btn}" data-claude-save-token @click={Ev(fun _ -> dispatch (ClaudePressedMsg ClaudePress.SaveToken))}>Save token</button>
                        </details>"""
        let error =
            match Pending.refusal claude.Pending with
            | Some reason -> html $"""<span class="{Style.statusErr}" data-claude-error>{reason}</span>"""
            | None -> html $""""""
        html $"""
            <section class="{Style.cls [ Style.sideSection; Style.settingsLane1 ]}" data-claude-panel>
              <span class="{Style.sectionLabel}">claude</span>
              {connectedRow
                 ((sharedScopeLabel (claude.Status |> Option.map (fun panel -> panel.Owner))).ToLowerInvariant ())
                 "mine"
                 (claude.Status |> Option.bind (fun panel -> panel.MineCredential))}
              {connectedRow "this session" "session" (claude.Status |> Option.bind (fun panel -> panel.SessionCredential))}
              {error}
              {controls}
            </section>"""

    /// The GitHub connection panel (Plan 14), beside the Claude one: status per sign-in
    /// scope, the device flow (show the code → approve on github.com → the poll lands
    /// the grant), and the paste-a-token fallback.
    let private githubSection
        (actions: ViewActions)
        (dispatch: ClientMsg -> unit)
        (copied: Copy option)
        (github: GitHubViewState)
        : TemplateResult =
        let connectedRow (label: string) (scopeChoice: string) (credential: CredentialRow option) =
            match credential with
            | Some credential ->
                let why =
                    match credential.SignInRequired with
                    | None -> Lit.nothing
                    | Some reason ->
                        html $"""<details class="{Style.detailNote}" data-github-signin-required="{scopeChoice}" data-detail="credential">{credentialReason reason}</details>"""
                html $"""
                    <div class="{Style.sideRow}" data-github-connected="{scopeChoice}">{credentialStatus label credential}<button type="button" class="{Style.cls [ Style.btnIconBareDanger; "self-center" ]}" aria-label="Disconnect GitHub" data-github-disconnect="{scopeChoice}" @click={Ev(fun _ -> dispatch (GitHubPressedMsg (GitHubPress.Disconnect scopeChoice)))}>{Icon.close}</button></div>
                    {why}"""
            | None -> html $""""""
        let controls =
            // As Claude's, for its reason.
            match github.Flow with
            | _ when Pending.inFlight github.Pending ->
                html $"""<span class="{Style.statusWait}" data-github-busy>{Icon.spinLead}working…</span>"""
            | GitHubAwaitingApproval (userCode, verificationUri, _, _) ->
                // The code has to reach github.com's form, and on the phone that means the
                // clipboard: this session is one tab, the approval is another, and eight
                // characters retyped between them is where a device flow is abandoned.
                //
                // The confirmation replaces the code IN THE BOX, which is the one place the
                // reader is already looking, and it is a live region rather than a labelled
                // one: an `aria-label` becomes the accessible name, so it would be announced
                // in place of the very change it is here to report. The code is the box's
                // contents and the caps label above says what it is.
                let justCopied = copied |> Option.exists (fun copy -> copy.Box = Dom.Hooks.githubUserCode)
                html $"""
                    <span class="{Style.label}">code for github.com</span>
                    <div class="{Style.codeBoxWrap}">
                      <span class="{Style.codeBox}" data-github-user-code aria-live="polite">{if justCopied then Dom.Text.copied else userCode}</span>
                      <button type="button" class="{Style.codeBoxAction}" data-github-copy-code
                              aria-label="{if justCopied then "Device code copied" else "Copy the device code"}"
                              @click={Ev(fun _ -> dispatch (CopyMsg (Dom.Hooks.githubUserCode, userCode)))}>{if justCopied then Icon.check else Icon.copy}</button>
                    </div>
                    <div class="{Style.settingsActions}">
                      <a class="{Style.btnPrimary}" href="{verificationUri}" target="_blank" rel="noreferrer" data-github-authorize>Approve on github.com</a>
                      <button type="button" class="{Style.btn}" data-github-cancel @click={Ev(fun _ -> dispatch (GitHubFlowMsg GitHubIdle))}>Cancel</button>
                    </div>"""
            | GitHubIdle ->
                let chosen =
                    chosenCredential
                        github.Scope
                        (github.Status |> Option.bind (fun panel -> panel.MineCredential))
                        (github.Status |> Option.bind (fun panel -> panel.SessionCredential))
                // As Claude's, for its reason: the row above already carries the status
                // and the X, so there is nothing left for this control to say.
                match chosen with
                | Some _ -> html $""""""
                | None ->
                    html $"""
                        <label class="{Style.label}" for="github-scope">sign in for</label>
                        <div class="{Style.fieldSelectWrap}">
                          <select id="github-scope" class="{Style.fieldSelect}" data-github-scope aria-label="GitHub sign-in scope"
                                  @change={EvVal(fun v -> dispatch (GitHubScopeChosen v))}>
                            <option value="mine" ?selected={github.Scope = "mine"}>{sharedScopeLabel (github.Status |> Option.map (fun panel -> panel.Owner))}</option>
                            <option value="session" ?selected={github.Scope = "session"}>This session only</option>
                          </select>
                          <span class="{Style.fieldSelectMark}">{Icon.caret}</span>
                        </div>
                        <button type="button" class="{Style.btn}" data-github-connect @click={Ev(fun _ -> dispatch (GitHubPressedMsg GitHubPress.Connect))}>Connect GitHub</button>
                        <details class="{Style.cls [ Style.detailNote; "pt-2" ]}" data-github-token-form>
                          <summary class="{Style.detailSummary}" data-github-use-token>{Dom.Text.useTokenInstead}</summary>
                          <label class="{Style.label} pt-2" for="github-token">personal access token</label>
                          <input id="github-token" type="password" class="{Style.field}" data-github-token placeholder="github_pat_…"
                                 .value={github.Token}
                                 @input={EvVal(fun v -> dispatch (GitHubTokenTyped v))} />
                          <button type="button" class="{Style.btn}" data-github-save-token @click={Ev(fun _ -> dispatch (GitHubPressedMsg GitHubPress.SaveToken))}>Save token</button>
                        </details>"""
        let error =
            match Pending.refusal github.Pending with
            | Some reason -> html $"""<span class="{Style.statusErr}" data-github-error>{reason}</span>"""
            | None -> html $""""""
        html $"""
            <section class="{Style.cls [ Style.sideSection; Style.settingsLane1 ]}" data-github-panel>
              <span class="{Style.sectionLabel}">github</span>
              {connectedRow
                 ((sharedScopeLabel (github.Status |> Option.map (fun panel -> panel.Owner))).ToLowerInvariant ())
                 "mine"
                 (github.Status |> Option.bind (fun panel -> panel.MineCredential))}
              {connectedRow "this session" "session" (github.Status |> Option.bind (fun panel -> panel.SessionCredential))}
              {error}
              {controls}
            </section>"""

    /// The one place a tone becomes an ink. Exhaustive on purpose: a fifth tone fails
    /// the build HERE, where somebody has to choose a colour and prove its contrast,
    /// rather than rendering as whatever the fall-through happened to be.
    let private ink (tone: QueryTone) : string = Style.toneInk tone

    /// The generated read surface (Plan 15): ONE renderer for every query this session
    /// declares, now and later. Registering a query is what puts it on this screen —
    /// nobody writes a panel, which is the whole reason the surface is generated rather
    /// than hand-built.
    ///
    /// It is deliberately read-only. The commands that change any of this belong to the
    /// agent: a human asks, and the act lands in the timeline attributed. So there are no
    /// buttons here, no inputs, and nothing that can be in flight — which is also what
    /// makes the accessibility floor cheap to hold, because it is held once, here, for
    /// every query that will ever exist.
    let private queryValueView (shape: QueryShape) (value: QueryValue option) : TemplateResult =
        let cellAt (row: (string * QueryCell) list) (column: QueryColumn) =
            row
            |> List.tryFind (fun (key, _) -> key = column.Key)
            |> Option.map snd
            |> Option.defaultValue CellAbsent
        let face (cell: QueryCell) =
            match cell with
            | CellStatus (_, tone) -> Style.queryValueIn (ink tone)
            | _ -> Style.queryValue
        // A toned cell carries its tone as a data hook as well as an ink, because the ink
        // is the thing a test must not assert on — a class name is how the surface is
        // BUILT, and the hook is what it PROMISES.
        let toneHook (cell: QueryCell) =
            match cell with
            | CellStatus (_, tone) -> QueryTone.name tone
            | _ -> ""
        // One record's pairs, label over value (`Style.queryFields`). A `<dl>` and not a
        // table: the lane is 217px, so there are no columns to compare down, and a
        // description list is what says "these are facts about the subject above" to a
        // screen reader without pretending to a structure the eye cannot see.
        let pairs (row: (string * QueryCell) list) (columns: QueryColumn list) =
            columns
            |> List.map (fun column ->
                let cell = cellAt row column
                html $"""
                    <div class="{Style.queryField}">
                      <dt class="{Style.queryFieldLabel}">{column.Label}</dt>
                      <dd class="{face cell}" data-query-cell="{column.Key}" data-query-tone="{toneHook cell}">{QueryCell.describe cell}</dd>
                    </div>""")
        match shape, value with
        | _, None -> html $"""<span class="{Style.small}" data-query-pending>…</span>"""
        | Value, Some (ValueOf cellValue) ->
            html $"""<span class="{face cellValue}" data-query-value data-query-tone="{toneHook cellValue}">{QueryCell.describe cellValue}</span>"""
        | Fields columns, Some (FieldsOf fields) ->
            // One record, and the whole answer — so every column is a labelled pair,
            // including the first. There is no list to scan by name here.
            html $"""<dl class="{Style.queryFields}">{pairs fields columns}</dl>"""
        | Rows _, Some (RowsOf []) ->
            html $"""<span class="{Style.small}" data-query-empty>(none)</span>"""
        | Rows columns, Some (RowsOf rows) ->
            // The first column NAMES the row (`QueryShape.Rows` says so), so it is drawn
            // as the record's heading rather than as a pair — which is what makes a list
            // of records scannable at all.
            let name = List.tryHead columns
            let rest = match columns with | [] -> [] | _ :: tail -> tail
            let record index row =
                let named = name |> Option.map (cellAt row) |> Option.defaultValue CellAbsent
                let shell = if index = 0 then Style.queryRecord else Style.queryRecordAfter
                let key = name |> Option.map (fun column -> column.Key) |> Option.defaultValue ""
                // A fact the row has nothing to say about is left out rather than drawn as a
                // label over a dash: a record is the lane's whole answer about its subject,
                // and a label with no value reads as something missing instead of as
                // nothing to report. (A `Fields` answer keeps its dashes — there the
                // columns ARE the answer, and an absent one is worth seeing.)
                let present =
                    rest
                    |> List.filter (fun column ->
                        match cellAt row column with
                        | CellAbsent -> false
                        | _ -> true)
                html $"""
                    <div class="{shell}" data-query-row="{QueryCell.describe named}" tabindex="-1">
                      <span class="{Style.queryRecordName}" data-query-cell="{key}" data-query-tone="{toneHook named}">{QueryCell.describe named}</span>
                      <dl class="{Style.queryFields}">{pairs row present}</dl>
                    </div>"""
            html $"""<div class="{Style.queryRecords}">{rows |> List.mapi record}</div>"""
        // A value that does not match its declared shape never reaches here — the registry
        // refuses it Process-side — so this arm exists only to keep the match total.
        | _, Some _ -> html $"""<span class="{Style.small}" data-query-pending>…</span>"""

    let private queriesSection (queries: QueriesViewState) : TemplateResult list =
        queries.Declared
        |> List.map (fun def ->
            let name = QueryName.value def.Name
            html $"""
                <section class="{Style.cls [ Style.sideSection; Style.settingsLane1 ]}" data-query-panel="{name}" tabindex="-1">
                  <span class="{Style.label}">{def.Title}</span>
                  {legendView def.Legend}
                  {queryValueView def.Shape (Map.tryFind name queries.Values)}
                </section>""")

    /// Settings, as the sidebar column's OTHER FACE. Not a drawer over the conversation: you
    /// go there and come back, the timeline never moves under a scrim, and configuration keeps
    /// the section rhythm it already had. Open state is the model's (`Column.Face`), drawn as
    /// the root element's `settings-open` class so a re-render never fights its transition.
    ///
    /// It is laid out as the nav face's mirror: identity in the head (where the wordmark sits),
    /// the way out at the foot (where `settings ›` sits), and the column's own collapse control
    /// in the same corner on both faces — chrome that belongs to the column, not to a face, so
    /// it never disappears under you.
    /// Said only where it is true, and only once: a client that cannot keep history keeps
    /// none, and the alternative to saying so is a session that silently stops remembering.
    /// Not in the degraded strip — that reports LEGS that are down, and this is a property of
    /// how the page was served, fixed for the whole life of the document.
    let private historyStoreNote (model: ClientModel) : TemplateResult =
        match model.LocalCopy with
        | LocalCopy.Reading
        | LocalCopy.Kept _
        | LocalCopy.NoneKept -> Lit.nothing
        | LocalCopy.Unkeepable ->
            html $"""
                <section class="{Style.sideSection}" data-history-store="none">
                  <span class="{Style.label}">history</span>
                  <span class="{Style.small}">{Dom.Text.historyNotKept}</span>
                  {detailNote "history-store" [ Dom.Text.historyNotKeptWhy ]}
                </section>"""
        | LocalCopy.Unread reason ->
            html $"""
                <section class="{Style.sideSection}" data-history-store="unread">
                  <span class="{Style.label}">history</span>
                  <span class="{Style.small}">{Dom.Text.historyUnread}</span>
                  {detailNote "history-store" [ reason; Dom.Text.historyUnreadWhy ]}
                </section>"""

    /// Which build served this session, at the column's foot on BOTH faces — chrome that
    /// belongs to the column like its collapse chevron, so it does not come and go as the
    /// faces turn. Nothing at all when the shell said nothing.
    let private buildMark (model: ClientModel) : TemplateResult =
        match model.Build with
        | None -> Lit.nothing
        | Some build ->
            html $"""<span class="{Style.buildMark}" data-build="{build}">yession {build}</span>"""

    let private settingsPane (actions: ViewActions) (dispatch: ClientMsg -> unit) (model: ClientModel) : TemplateResult =
        html $"""
            <div class="{Style.settingsPane}" data-settings-panel>
              <div class="{Style.cls [ Style.settingsHead; Style.settingsLane0 ]}">
                <span class="{Style.settingsTitle}">settings</span>
                <button type="button" class="{Style.navChevronBack}" aria-label="Collapse sidebar" data-nav-toggle="hide" @click={Ev(fun _ -> dispatch ToggleNavMsg)}>{Icon.left}</button>
              </div>
              {claudeSection dispatch model.Claude}
              {githubSection actions dispatch model.Copied model.GitHub}
              {queriesSection model.Queries}
              {historyStoreNote model}
              <div class="flex-1"></div>
              <div class="{Style.cls [ Style.sideFoot; Style.settingsLane2 ]}">
                <button type="button" class="{Style.navPivot}" aria-label="Back to session" data-settings-toggle="close" @click={Ev(fun _ -> dispatch ToggleSettingsMsg)}><span class="{Style.pivotMarkBack}">{Icon.left}</span>back</button>
                {buildMark model}
              </div>
            </div>"""

    /// The contents: every chapter in the session, and the way to reach one that is not
    /// scrolling until you find it.
    ///
    /// HERE rather than in a bar of its own, and that is the mobile answer as much as the
    /// desktop one: this column is already the session's index — who is here, what the
    /// environment is — and on a phone it is already a drawer one tap from the conversation.
    /// A second surface listing chapters would be a second place to look for the same list,
    /// and a phone cannot afford either the width or the tap.
    ///
    /// Absent until there is a chapter. A heading over nothing teaches a reader to skip the
    /// place the list will appear.
    let private chaptersSection (dispatch: ClientMsg -> unit) (model: ClientModel) : TemplateResult =
        match ClientModel.chapters model with
        | [] -> Lit.nothing
        | chapters ->
            // `RevealMessage` — the same jump the reply ref makes, so a chapter reached from
            // the contents lands exactly as a reply's source does: centred, flashed, and
            // holding the cursor. A second way to arrive would be a second thing to keep
            // right.
            let entry (item: ConversationItem) =
                html $"""
                    <button type="button" class="{Style.chapterEntry}"
                            data-chapter-entry="{MessageId.value item.MessageId}"
                            @click={Ev(fun _ -> dispatch (MoveMsg (DomMove.RevealMessage item.MessageId)))}>
                      <span class="{Style.chapterEntryDot}"></span>
                      <span class="{Style.givesWay}">{ClientModel.chapterName model item}</span>
                    </button>"""
            html $"""
                <section class="{Style.cls [ Style.sideSection; Style.navLane1 ]}" data-chapters>
                  <span class="{Style.label}">chapters</span>
                  <div class="{Style.chapterEntries}">{chapters |> List.map entry}</div>
                </section>"""

    /// The workspace face of the column: identity, sync health, membership, environment, log.
    let private navPane (actions: ViewActions) (dispatch: ClientMsg -> unit) (model: ClientModel) : TemplateResult =
        html $"""
            <div class="{Style.navPane}">
              <div class="{Style.cls [ Style.sideHead; Style.navLane0 ]}">
                <span class="{Style.lockup}"><span class="{Style.lockupMark}" aria-hidden="true">{Brand.mark}</span><span class="{Style.wordmark}">yession</span></span>
                <button type="button" class="{Style.navChevronBack}" aria-label="Collapse sidebar" data-nav-toggle="hide" @click={Ev(fun _ -> dispatch ToggleNavMsg)}>{Icon.left}</button>
              </div>
              {connectionSection dispatch model}
              {standingsSection dispatch model}
              {peopleSection actions dispatch model}
              {chaptersSection dispatch model}
              {environmentSection dispatch model}
              <div class="flex-1"></div>
              <div class="{Style.cls [ Style.sideFoot; Style.navLane2 ]}">
                <button type="button" class="{Style.navPivot}" data-settings-toggle="open" @click={Ev(fun _ -> dispatch ToggleSettingsMsg)}>settings<span class="{Style.pivotMarkForward}">{Icon.right}</span></button>
                {buildMark model}
              </div>
            </div>"""

    /// The sidebar column: one region, two faces, and — on mobile — the scrim behind it.
    let private sidebar (actions: ViewActions) (dispatch: ClientMsg -> unit) (model: ClientModel) : TemplateResult =
        html $"""
            <div class="{Style.scrim}" data-nav-toggle="hide" @click={Ev(fun _ -> dispatch ToggleNavMsg)}></div>
            <aside class="{Style.sidebar}">
              {navPane actions dispatch model}
              {settingsPane actions dispatch model}
            </aside>"""

    // --- Conversation column ------------------------------------------------------------

    /// The one call to action for a credential that stopped working, over the timeline where
    /// somebody who never opens settings will meet it.
    ///
    /// It is the only NEW button this feature adds. The panel rows and the agent's roster row
    /// say the same fact as a status, because a call to action repeated is wallpaper — the
    /// rule `Style.fs`'s `noAgent*` block and the acceptance suite both already encode. Here
    /// it is a button because, unlike a degraded leg, a dead credential does not recover on
    /// its own.
    ///
    /// Shown whenever anything needs signing in, at every width — NOT only while the sidebar
    /// is off screen, which is how the header's "no agent" stand-in behaves. The two differ
    /// because their subjects do: an absent agent is a state you chose and can see in the
    /// roster, while a credential that died is news, and news that only reaches you if a
    /// column happens to be collapsed is news that does not reach you.
    ///
    /// Silent while the session leg is down, for a reason and not for tidiness: signing in
    /// runs against the session, so a prompt offering it to somebody who cannot reach the
    /// session is offering a button that cannot work. The degraded strip owns that moment,
    /// and one strip at a time is what it has always promised.
    let private signInPrompt (dispatch: ClientMsg -> unit) (model: ClientModel) : TemplateResult =
        match model.Link, ClientModel.signInRequired model with
        | SessionLink.Refused _, _
        | SessionLink.Connecting { Failed = Some _ }, _
        | SessionLink.Reconnecting ({ Failed = Some _ }, _), _
        | _, [] -> Lit.nothing
        // Whichever came first in the derivation's settled order. A second row would be the
        // same instruction twice, and the panel it opens shows every provider anyway.
        | _, (provider, reason) :: _ ->
            html $"""
                <section class="{Style.signInPrompt}" data-signin-required="{provider}">
                  <span class="{Style.signInPromptStatus}"><span class="{Style.statusDot}"></span>{provider}</span>
                  <span class="{Style.signInPromptBody}">
                    <span class="{Style.small}">{Dom.Text.signInLost provider}</span>
                    {detailNote "signin" [ reason ]}
                  </span>
                  <button type="button" class="{Style.signInPromptAction}"
                          data-signin-again data-settings-toggle="prompt"
                          @click={Ev(fun _ -> dispatch RevealSettingsMsg)}>{Dom.Text.signInAgain}</button>
                </section>"""


    /// What the session last REFUSED, in the sign-in prompt's slot and its hairline: a notice
    /// over the timeline, never a modal and never a blocker.
    ///
    /// It exists because a refused command used to be silent. Every command answers, and
    /// until now only the launch surface read the answer — so a press the session would not
    /// honour did nothing and said nothing, which is a dead control wearing a live one's
    /// clothes. The reason is the session's own sentence, written to be read.
    ///
    /// Dismissible, because a refusal is NEWS rather than a state: nothing recovers it and
    /// nothing re-raises it, so once it has been read there is nothing left for it to do. The
    /// next command that succeeds clears it too (`CommandAnsweredMsg`).
    ///
    /// Two mounts — here, and in the content pane under its strip, where every terminal verb
    /// is pressed — and ONE notice: `ClientModel.refusalMount` says which draws it, and the
    /// other draws nothing. It tells the model when the keyboard is inside it, so whatever
    /// takes it away can hand focus on instead of stranding it (`ClientModel.update`).
    let private refusalNotice (dispatch: ClientMsg -> unit) (model: ClientModel) (mount: RefusalMount) : TemplateResult =
        match model.Refused with
        | Some refusal when ClientModel.refusalMount model = Some mount ->
            let shape = Style.refusalIn mount
            html $"""
                <section class="{shape.Row}" data-command-refused role="status"
                         @focusin={Ev(fun _ -> dispatch (RefusalFocusMsg (Some mount)))}
                         @focusout={Ev(fun _ -> dispatch (RefusalFocusMsg None))}>
                  <span class="{shape.Status}"><span class="{Style.statusDot}"></span>{Dom.Text.refused}</span>
                  <span class="{shape.Body}"><span class="{Style.small}">{refusal.Reason}</span></span>
                  <button type="button" class="{shape.Dismiss}" data-command-refused-dismiss
                          aria-label="{Dom.Text.dismissRefusal}"
                          @click={Ev(fun _ -> dispatch DismissRefusalMsg)}>{Icon.close}</button>
                </section>"""
        | _ -> Lit.nothing

    /// The connection report where the nav column is NOT on screen: a phone, or a desktop
    /// with the column collapsed. Same visibility rule the header's "no agent" stand-in
    /// already uses, and for the same reason — news that reaches you only if a column happens
    /// to be open is news that does not reach you.
    ///
    /// It is the conversation column's first child, which is where it belongs on a desktop
    /// whose nav is shut: in flow, above the header, the width of the column it reports for.
    ///
    /// On a phone it leaves that flow — `position: fixed` above all three panes — because a
    /// phone shows one pane at a time, and a notice that lives inside one of them cannot be
    /// read from the other two. The panes make room for it through `Style.degradedShell`, a
    /// class this view puts on the shell whenever there is something to say, so nothing
    /// reserves a band of dead space while everything is fine.
    let private degradedBar (dispatch: ClientMsg -> unit) (model: ClientModel) : TemplateResult =
        match connectionReport model with
        | None -> Lit.nothing
        | Some line ->
            // On the conversation's own rail, as the title and every message under it are:
            // the dot in the gutter where an avatar stands, the word where a name does, and
            // the verb at the row's end — the only place a phone reader has one, because the
            // column that carries it on a desktop is hidden at that width. The disclosure's
            // lines open BELOW the bar, over the pane, rather than inside a height the panes
            // reserve.
            html $"""
                <section class="{Style.degradedBar}" data-degraded="{line.Token}">
                  <span class="{Style.degradedBarMark}">{statusLineMark line}</span>
                  <span class="{Style.degradedBarWords}">
                    {statusLineWord line}
                    {statusLineDetails "degraded" Style.degradedBarDetails model line}
                  </span>
                  <span class="{Style.degradedBarVerb}">{statusLineVerb dispatch line}</span>
                </section>"""

    /// The `(selectionStart, selectionEnd)` of the event's target input, or `None` when the
    /// target carries no caret at all: a `@focus` on a button, an event with no target, or an
    /// `<input>` whose type has no text selection (`selectionStart` answers `null` there) — each
    /// told apart from an input whose caret happens to sit at 0. Read live from the DOM; only
    /// ever invoked in the browser (SSR drops event bindings), so the `.NET` type-check sees a
    /// signature it never runs.
    let private selectionOf (e: Browser.Types.Event) : (int * int) option =
        match EventTargets.asHTMLInputElement e.target with
        | Some field when not (isNull (box field.selectionStart)) ->
            Some (int field.selectionStart, int field.selectionEnd)
        | _ -> None

    /// Enter, in a one-line field that has nothing to submit: let go of it.
    ///
    /// The title is a CRDT — every keystroke is already written, shared and reported to the
    /// Manager, so there is no save left for a key to perform. What there IS on a phone is a
    /// keyboard filling half the screen, held open by the field's own focus, with no submit
    /// button anywhere to close it: the return key does nothing, so the edit reads as one the
    /// app never took. Blurring is what a text input can honestly do with Enter — it closes
    /// the keyboard, uncovers the title that was just typed, and clears the presence caret
    /// through the field's own `@blur`.
    ///
    /// `isComposing` guards the IME exactly as the command line's Enter does (`Browser`'s
    /// `bindTerminalInput`): mid-composition, Enter accepts a candidate word, and taking the
    /// field away from someone in the middle of typing one is not what they asked for. The
    /// binding is `Fable.BrowserExtras`'s, which is where the browser client reads it from.

    let private commitOnEnter (e: Browser.Types.KeyboardEvent) : unit =
        if e.key = "Enter" && not (isComposing e) then
            // Both halves, together: the browser told not to also act on the key, and the field
            // given up — which is what the caller reads as "done". Preventing without blurring
            // would leave the keyboard open over a field that had stopped responding to Enter.
            e.preventDefault ()
            EventTargets.asHTMLElement e.currentTarget |> Option.iter (fun field -> field.blur ())

    /// The bytes a keydown sends to a pty, and the browser told not to also act on it.
    ///
    /// `Keystroke.bytesOf` decides them — what a key means to a terminal is pure, and is
    /// written down (with the reasoning) where a test can reach it without a browser. This is
    /// the half that needs the event: reading its fields, and `preventDefault` on everything
    /// that IS sent, because otherwise the browser also acts on it — Tab would leave the
    /// terminal mid-session, and Backspace used to navigate. Deciding and preventing are one
    /// verb, so no caller can send a key and leave the browser acting on it too.
    let private keystrokeOf (e: Browser.Types.KeyboardEvent) : string option =
        let sent =
            Keystroke.bytesOf
                { Key = e.key; Ctrl = e.ctrlKey; Alt = e.altKey; Shift = e.shiftKey; Meta = e.metaKey }
        if Option.isSome sent then e.preventDefault ()
        sent

    /// What the live screen's field took in that no keydown named — a phone keyboard's letters,
    /// an IME's committed word, a paste — sent, and the field emptied for the next. Read and
    /// emptied in one verb, so the `input` and the `compositionend` that can both follow one
    /// committed word cannot send it twice: whichever runs second finds nothing.
    ///
    /// Empty between insertions also keeps a phone's Backspace a KEY: with nothing in the field
    /// to delete, the keyboard reports it as `Backspace` and the chord path sends it, where a
    /// field holding text would have quietly deleted a character of it instead.
    let private sendInserted (send: string -> unit) (field: Browser.Types.HTMLTextAreaElement) : unit =
        let text = field.value
        field.value <- ""
        Keystroke.ofInsertedText text |> Option.iter send

    /// A press on the holder's live screen, said with the one fact only the document has, and
    /// only now: whether the press left text selected. Where the keyboard goes after it is the
    /// model's (`ScreenPressedMsg`).
    let private keysOnPress (dispatch: ClientMsg -> unit) (terminal: TerminalId) (_: Browser.Types.Event) : unit =
        dispatch (ScreenPressedMsg (terminal, Browser.Dom.window.getSelection().toString () <> ""))

    /// One collaborator's title caret+selection marker: a selection highlight span and a caret
    /// bar with a name label. The browser positions all three by measurement after render (from
    /// the peer's relative positions, decoded against the title `Y.Text`); colour is fixed here.
    let private remoteCursor (model: ClientModel) (who: ActorRef) (presence: RemotePresence) : TemplateResult =
        let colour = Entity.presenceColour model who
        // Container = the translucent selection highlight (positioned `lo..hi` by the browser);
        // the caret bar is offset to `head` inside it; the label rides above the caret.
        html $"""
            <span class="{Style.remoteCursor}" data-cursor-peer="{ActorRef.token who}" style="background:{Entity.presenceSelection model who}">
              <span class="{Style.remoteCursorCaret}" style="background:{colour}">
                <span class="{Style.remoteCursorLabel}" style="background:{colour}">{presence.DisplayName}</span>
              </span>
            </span>"""

    /// The way back into the content column once it is shut. Present only while it IS
    /// shut, so there are never two controls for the one column on screen at once.
    ///
    /// An edge tab that says what the column holds (P1-4): how many terminals are open. It
    /// used to say "content", in the faint small voice of a chevron, so a session's terminals
    /// were nowhere on the first screen until somebody went looking. A running command is
    /// said to the eye by the composer's tray (`composerTray`), which can take the reader to
    /// it; here only in the tab's accessible name.
    /// A mark, and on a wide screen the words beside it: a terminal on a stack (`Icon.content`),
    /// then "2 terminals". The mark with a bare number ("⧉ 2") said nothing a reader could
    /// look up, and the hover title that named it is the tooltip principle 4 refuses. A phone
    /// keeps the mark and the bare number: "‹ terminals" spent a third of its band saying what
    /// the mark says. There the words live on in the accessible name.
    let private contentReopen (dispatch: ClientMsg -> unit) (model: ClientModel) : TemplateResult =
        if model.TerminalsOpen then Lit.nothing
        else
            let live = Projection.openTerminals model.Terminals
            let running =
                live
                |> List.filter (Projection.runningBlock >> Option.isSome)
                // By the name the strip and the list give it (P1-1), so the edge tab's spoken
                // "build running" is the word a reader will find once the pane is open.
                |> List.map (TerminalName.display model.Terminals)
            let n = List.length live
            let bare = if n = 0 then Lit.nothing else html $"""<span class="{Style.terminalReopenBare}">{n}</span>"""
            let count =
                html $"""<span data-terminals-count><span class="{Style.terminalReopenWords}">{Dom.Text.terminalsCount n}</span>{bare}</span>"""
            html $"""
                <button type="button" class="{Style.terminalReopen}"
                        title="{Dom.Text.showTerminals}" aria-label="{Dom.Text.showTerminalsNamed (List.length live) running}"
                        data-content-toggle="show" data-terminals-open="{string (List.length live)}"
                        ?data-terminals-running={not (List.isEmpty running)}
                        @click={Ev(fun _ -> dispatch ToggleContentMsg)}>{Icon.content}{count}</button>"""

    let private header (actions: ViewActions) (dispatch: ClientMsg -> unit) (model: ClientModel) : TemplateResult =
        let titleStr = Ylmish.Text.toString model.Synced.Title
        let sessionIdText = model.Session |> Option.map SessionId.value |> Option.defaultValue ""
        // Only peers whose caret is in the title get a marker here; each other field renders
        // its own overlay (bodies decorate their editors).
        let cursors =
            model.Presence
            |> Map.toList
            |> List.filter (fun (_, p) -> p.Focus |> Option.exists (fun f -> f.Field = Title))
            |> List.map (fun (who, p) -> remoteCursor model who p)
        // The worst standing rides the way into the column that says them all, and its word
        // rides the control's name, so a screen reader hears what the mark shows.
        let worst = Standings.Standing.worst (ClientModel.standings model)
        let reopenLabel =
            match worst with
            | Some standing -> sprintf "Show sidebar, %s" standing.Word
            | None -> "Show sidebar"
        let reopenMark =
            match worst with
            | Some standing ->
                html $"""<span class="{Style.navReopenMark}" data-reopen-attention>{Icon.attention standing.Attention}</span>"""
            | None -> Lit.nothing
        html $"""
            <header class="{Style.header}">
              <button type="button" class="{Style.cls [ Style.navChevronForward; Style.navReopen ]}" aria-label="{reopenLabel}" data-nav-toggle="show" @click={Ev(fun _ -> dispatch ToggleNavMsg)}>{Icon.right}{reopenMark}</button>
              <div class="{Style.titleWrap}">
                <!-- `enterkeyhint="done"` because that is what the return key now does here
                     (`commitOnEnter`): it finishes with the field. A phone draws the hint on
                     the key itself, so the promise is legible before it is pressed rather
                     than only after. -->
                <input type="text" class="{Style.titleInput}" data-session-title aria-label="Session title" placeholder="session"
                       autocapitalize="off" autocorrect="off" autocomplete="off" spellcheck="false"
                       enterkeyhint="done"
                       value="{titleStr}"
                       .value={titleStr}
                       @input={EvVal(fun v -> dispatch (EditTitleMsg (Ylmish.Text.edit v model.Synced.Title)))}
                       @keydown={Ev(Events.key commitOnEnter)}
                       @keyup={Ev(fun e -> actions.ReportFieldSelection Title (selectionOf e))}
                       @click={Ev(fun e -> actions.ReportFieldSelection Title (selectionOf e))}
                       @select={Ev(fun e -> actions.ReportFieldSelection Title (selectionOf e))}
                       @focus={Ev(fun e -> actions.ReportFieldSelection Title (selectionOf e))}
                       @blur={Ev(fun _ -> actions.ReportFieldSelection Title None)}>
                {cursors}
                <span class="{Style.titleId}" data-session-id>{sessionIdText}</span>
              </div>
              <div class="{Style.headerAside}">
                {contentReopen dispatch model}
              </div>
              {catchUpBar model}
            </header>"""

    let private queue (dispatch: ClientMsg -> unit) (model: ClientModel) : TemplateResult =
        let synced = model.Synced
        match ClientModel.editingQueue model with
        | Some id ->
            // The band's OWN surface, while this entry sits in the composer instead of the
            // list (`View.drafts`): Delete and Done where the list was, because there is
            // nothing left here to reorder against an entry that is not drawn, and the
            // per-row delete this replaced is gone with it (`Style.queueEditBar`).
            //
            // The second press of a two-press delete: armed by the first (below), and taken
            // back on its own after `ClientModel.armedMs` if nobody follows through.
            let armed = model.QueueDeleteArmed = Some id
            let deleteFace = if armed then Style.queueVerbDeleteArmed else Style.queueVerbDelete
            let deleteLabel = if armed then "Confirm delete" else "Delete"
            let onDelete = if armed then DeleteQueuedMsg id else ArmQueueDeleteMsg (Some id)
            html $"""
                <section class="{Style.queueEditBar}" data-message-queue>
                  <button type="button" class="{deleteFace}" aria-label="{deleteLabel}" data-queue-delete="{QueueId.value id}" data-queue-delete-armed="{if armed then "true" else "false"}" @click={Ev(fun _ -> dispatch onDelete)}>{Icon.bin}</button>
                  <button type="button" class="{Style.queueDone}" data-queue-done @click={Ev(fun _ -> dispatch CloseQueueEditMsg)}>Done</button>
                </section>"""
        | None ->
            // Only entries the wait has cleared (`ClientModel.QueueShown`) — most sends drain
            // before it fires, so the band that used to open and shut on every ordinary send now
            // stays closed for them. A reorder press still reaches an entry fresh off this
            // filter: it reads `synced.Queue` by id, never this list's order.
            let entries = QueueOrder.sorted synced.Queue |> List.filter (fun entry -> Set.contains entry.QueueId model.QueueShown)
            let head =
                match entries with
                | [] -> Lit.nothing
                | _ ->
                    html $"""<div class="{Style.queueHead}"><span class="{Style.queueCount}">queued · {List.length entries}</span></div>"""
            let items =
                entries
                |> List.map (fun entry ->
                    let id = entry.QueueId
                    // One clamped line of it — the same read-only editor the browser mounts
                    // everywhere, so the CRDT keeps it current (`draftSummary`'s own move).
                    // Opening it moves the SAME body into the composer, editable
                    // (`View.drafts`), rather than copying its content there: `BodyKey.queued
                    // id` names the one fragment either host binds.
                    html $"""
                        <article class="{Style.queueItem}" data-queue-id="{QueueId.value id}" data-queue-author="{PeerId.value entry.Author}" data-queue-order="{string entry.Order}">
                          <button type="button" class="{Style.queueSummary}" aria-label="Edit queued message" data-queue-summary="{QueueId.value id}" data-queue-open="{QueueId.value id}" @click={Ev(fun _ -> dispatch (OpenQueueEditMsg id))}>
                            <span class="{Style.cls [ Style.avatarSm; Entity.actorMark model (PeerRef entry.Author) ]}"></span>
                            <span class="{Style.queueSummaryBody}" data-rich-body="{BodyKey.queued id}" data-rich-readonly="true"></span>
                          </button>
                          <div class="{Style.queueTools}">
                            <button type="button" class="{Style.queueVerb}" aria-label="Move up" data-queue-up="{QueueId.value id}" @click={Ev(fun _ -> match QueueOrder.moveUp synced.Queue id with Some o -> dispatch (ReorderQueuedMsg (id, o)) | None -> ())}>{Icon.up}</button>
                            <button type="button" class="{Style.queueVerb}" aria-label="Move down" data-queue-down="{QueueId.value id}" @click={Ev(fun _ -> match QueueOrder.moveDown synced.Queue id with Some o -> dispatch (ReorderQueuedMsg (id, o)) | None -> ())}>{Icon.down}</button>
                          </div>
                        </article>""")
            let band = if List.isEmpty entries then Style.queueEmpty else Style.queue
            html $"""<section class="{band}" data-message-queue>{head}{items}</section>"""

    /// The model picker, riding the composer's own row beside Send and Interrupt rather than
    /// two taps down in Settings. Choosing a model is a per-turn decision, so it belongs
    /// where a turn starts, not where the account that pays for it lives.
    ///
    /// Stands in `Style.draftLead`, not in `commitClass` beside it (`View.drafts`) — that
    /// div gates on there being a draft or a turn, which is Send's and Interrupt's own
    /// reason to come and go and never this control's. It rode `commitClass` for one
    /// revision; see `Style.draftLead` for what that cost.
    ///
    /// It always offers the provider's own default, and it always offers whatever the
    /// session has CHOSEN — even a model the catalogue no longer lists, because a control
    /// that silently displays something other than the setting behind it is worse than one
    /// showing an id nobody recognises. Everything else is the catalogue, in a person's
    /// order rather than the provider's.
    ///
    /// Nothing here knows which provider answered. The control carries ids and names a
    /// provider gave, and a second provider would change neither.
    let private modelControl (dispatch: ClientMsg -> unit) (model: ClientModel) : TemplateResult =
        let chosen = model.Synced.Model
        let status = model.Claude.Status |> Option.map (fun panel -> panel.Models)
        let offered =
            match status with
            | Some (ModelsLoaded models) -> ModelCatalogue.ordered models
            | Some ModelsUnknown
            | Some (ModelsUnavailable _)
            | None -> []
        // The chosen model, when the catalogue does not carry it: shown by its id, which is
        // the only name anything here has for it.
        let orphan =
            match chosen with
            | Some id when offered |> List.forall (fun m -> m.Id <> id) -> [ AgentModel.create id "" ]
            | _ -> []
        // What the face says: the chosen model's name, or the default's label.
        let face =
            match chosen with
            | None -> Dom.Text.modelDefaultLabel
            | Some id ->
                (orphan @ offered)
                |> List.tryFind (fun m -> m.Id = id)
                |> Option.map (fun m -> m.Name)
                |> Option.filter (System.String.IsNullOrWhiteSpace >> not)
                |> Option.defaultValue (ModelId.value id)
        let options =
            (orphan @ offered)
            |> List.map (fun offer ->
                let id = ModelId.value offer.Id
                html $"""<option value="{id}" ?selected={chosen = Some offer.Id}>{offer.Name}</option>""")
        // What the picker cannot yet offer, and why — kept, from when this sat beside the
        // Claude panel that explains it, but SR-only here: the row has no width to spend on
        // prose, and a lookup that failed is almost always "no account connected here yet",
        // which the Claude panel in Settings still says in full.
        let note =
            match status with
            | None
            | Some ModelsUnknown -> Some ("pending", "…")
            | Some (ModelsLoaded []) -> Some ("empty", "this provider offered no models")
            | Some (ModelsLoaded _) -> None
            | Some (ModelsUnavailable reason) -> Some ("unavailable", reason)
        let noteEl =
            match note with
            | None -> Lit.nothing
            | Some (kind, text) -> html $"""<span class="{Style.srOnly}" data-model-note="{kind}">{text}</span>"""
        html $"""
            <div class="{Style.modelSelectWrap}" data-model-panel>
              <span class="{Style.modelSelectFace}" aria-hidden="true">{face}</span>
              <span class="{Style.modelSelectMark}">{Icon.caret}</span>
              <select aria-label="model" class="{Style.modelSelect}"
                      data-model-select="{chosen |> Option.map ModelId.value |> Option.defaultValue Dom.Text.modelDefault}"
                      @change={EvVal(fun v -> dispatch (SetModelMsg (match ModelId.create v with Ok id -> Some id | Error _ -> None)))}>
                <option value="" ?selected={chosen.IsNone}>{Dom.Text.modelDefaultLabel}</option>
                {options}
              </select>
              {noteEl}
            </div>"""

    /// The strip on the composer's top edge (`Style.composerTray`): the way to stop the turn
    /// that is running at its leading edge, the way back to the latest message at its
    /// trailing one, and nothing at all — no strip — while neither has anything to do.
    ///
    /// Interrupt is the only control here that DOES something about a running turn rather
    /// than report it: whether one is running is said by the caret in the timeline, and to a
    /// reader who cannot see the caret by the composer's live region (`drafts`).
    ///
    /// Neither floats over the timeline. The jump did, as a square on the avatars' column, and
    /// interrupt was tried there too: anything laid over the timeline lies over words the
    /// moment a long line scrolls under it. Here they take their own height, so they cover
    /// nothing in any scroll state. Leading/trailing, not the reverse: the jump's arrow points
    /// down the column a reader's eye is already at the end of, and the destructive verb
    /// stands at the far end of the strip from Send rather than above it.
    ///
    /// The jump's hook keeps naming the conversation (`TailSurface.key`), the surface it
    /// brings back; it shows while the model says the reader is far back from the end
    /// (`ClientModel.offersLatest`).
    ///
    /// With no turn to stop, the leading edge says a command is running, if one is, and goes
    /// to its chip when pressed (`DomMove.RevealCommand`) — the way a chapter goes to its
    /// message. One spot, one thing at a time: during a turn the agent's own commands are
    /// that turn's work, and interrupt is the verb that matters.
    let private composerTray (dispatch: ClientMsg -> unit) (model: ClientModel) : TemplateResult =
        let away = ClientModel.offersLatest TailSurface.Conversation model
        // The first open terminal running a command, in the order they were opened — the
        // order the strip and the list show them in.
        let running =
            Projection.openTerminals model.Terminals
            |> List.tryPick (fun view -> Projection.runningBlock view |> Option.map (fun block -> view.TerminalId, block))
        let leading =
            match model.Agent.ActiveTurn, running with
            | None, None -> Lit.nothing
            | None, Some (terminal, block) ->
                html $"""
                    <button type="button" class="{Style.trayRunning}" aria-label="{Dom.Text.revealRunningLabel (BlockLabel.ofBlock block)}"
                            data-reveal-running="{BlockId.value block.BlockId}"
                            @click={Ev(fun _ -> dispatch (MoveMsg (DomMove.RevealCommand (terminal, block.BlockId))))}><span class="{Style.trayMark}">{Icon.spin}</span>running</button>"""
            | Some turn, _ ->
                // `Interrupting` only ever names THIS turn or none (the event fold clears it the
                // moment the turn it named ends), so a plain equality is the whole check.
                let stopping = model.Agent.Interrupting = Some turn
                let label = if stopping then Dom.Text.interruptingLabel else Dom.Text.interruptLabel
                let word = if stopping then "stopping…" else "interrupt"
                html $"""
                    <button type="button" class="{Style.trayInterrupt}" aria-label="{label}" aria-busy="{if stopping then "true" else "false"}"
                            data-interrupt-turn="{AgentTurnId.value turn}" ?disabled={stopping}
                            @click={Ev(fun _ -> dispatch (InterruptTurnMsg turn))}><span class="{Style.trayMark}">{Icon.stop}</span>{word}</button>"""
        let jump =
            if not away then Lit.nothing
            else
                let tail = TailSurface.key TailSurface.Conversation
                html $"""
                    <div class="{Style.trayJumpSlot}" data-jump-to-latest="{tail}">
                      <button type="button" class="{Style.trayJump}" aria-label="{Dom.Text.jumpToLatest}"
                              @click={Ev(fun _ -> dispatch (MoveMsg (DomMove.JumpToLatest TailSurface.Conversation)))}>latest{Icon.down}</button>
                    </div>"""
        if model.Agent.ActiveTurn.IsNone && running.IsNone && not away then Lit.nothing
        else html $"""<div class="{Style.composerTray}" data-composer-tray>{leading}{jump}</div>"""

    /// The composer: ONE draft open, everyone else's as a line you can open.
    ///
    /// A draft is shared WIP — any peer may edit any draft (the body is a CRDT; the carets are
    /// presence) and any peer may send one, so the open draft is a full composer whoever's it is.
    /// What differs by ownership is destruction: discard is the author's alone.
    let private drafts (actions: ViewActions) (dispatch: ClientMsg -> unit) (model: ClientModel) : TemplateResult =
        let editingQueue = ClientModel.editingQueue model
        if editingQueue.IsSome then
            let id = editingQueue.Value
            // The composer's OWN surface while a queued message is open for editing: the SAME
            // body the band's one-line summary named (`BodyKey.queued id`), editable here
            // instead of there — nothing moved but which host is bound to it. No Send: the
            // message is already queued, so there is nothing here to commit, and Done (the
            // band's own surface, `View.queue`) is the way out rather than a second send.
            // Collapsed drafts and the "new message" link are both about composing something
            // NEW, which this is not, so neither draws while this is open.
            html $"""
                {composerTray dispatch model}
                <section class="{Style.composer}" data-draft-editor data-queue-editor="{QueueId.value id}">
                  <span class="{Style.bandRail}"></span>
                  <span class="{Style.bandEdge}"></span>
                  <article class="{Style.draftBox}">
                    <div class="{Style.draftBody}">
                      <div class="{Style.draftInput}" data-rich-body="{BodyKey.queued id}" data-rich-readonly="false" data-queue-input="{QueueId.value id}"></div>
                    </div>
                  </article>
                </section>"""
        else

        let myPeer = model.Peer.PeerId
        let target = ClientModel.composerTarget model
        // Live carets in a draft, coloured per peer: "grace and ada are in this one".
        let editors (peerId: PeerId) =
            ClientModel.editorsOf peerId model
            |> List.map (fun (editor, name) ->
                html $"""
                    <span class="{Style.draftEditorDot}" style="background:{Entity.presenceColour model editor}"
                          title="{name}" data-draft-editor-peer="{ActorRef.token editor}"></span>""")
        // A collapsed draft: whose it is, one clamped line of it (the same read-only editor the
        // browser mounts everywhere, so the CRDT keeps it current), and who is in it. Opening it
        // collapses whatever was open — including your own composer.
        let summary (peerId: PeerId) =
            html $"""
                <button type="button" class="{Style.draftSummary}" style="border-left-color:{Entity.presenceColour model (ActorRef.PeerRef peerId)}"
                        data-draft-summary="{PeerId.value peerId}"
                        data-draft-expand="{PeerId.value peerId}" @click={Ev(fun _ -> dispatch (ExpandDraftMsg peerId))}>
                  <span class="{Style.cls [ Style.avatarSm; Entity.actorMark model (PeerRef peerId) ]}"></span>
                  <span class="{Style.draftSummaryName}">{ClientModel.nameOf peerId model}</span>
                  <span class="{Style.draftSummaryBody}" data-rich-body="{BodyKey.draft peerId}" data-rich-readonly="true"></span>
                  <span class="{Style.draftEditors}">{editors peerId}</span>
                </button>"""
        // The same fact for a reader who cannot see the caret, and the only place the sentence
        // still exists in the product.
        //
        // MOUNTED ALWAYS, empty at rest: a live region announces what changes INSIDE it, and
        // one inserted with its text already in place is announced by no screen reader
        // reliably. So the region is permanent and only its contents come and go.
        //
        // `role="status"` and nothing beside it: that role IS a polite live region, so an
        // `aria-live` next to it would be one requirement declared twice.
        let agentLive =
            let said =
                match model.Agent.ActiveTurn with
                | Some _ -> Dom.Text.agentResponding
                | None -> ""
            html $"""<span class="{Style.srOnly}" role="status" data-agent-stream>{said}</span>"""
        // The open draft: an editable rich editor bound to that body fragment (mounted
        // imperatively by the browser), Send for anyone.
        let open' =
            // Whether there is anything here to act ON. The draft slot is that fact
            // (`ClientModel.draftHasContent` — `DraftSlot` publishes one exactly while the body
            // has content), so the controls and the send path read the same truth rather than
            // two measurements that can disagree.
            let hasContent = ClientModel.draftHasContent target model
            // Send STAYS — same place in the layout, same place in focus order, so nothing
            // moves under the hand and no Tab stop appears mid-sentence — and waits at a
            // dimmed weight until there is something to send, coming to full strength with the
            // first character.
            //
            // NOT marked disabled, in either spelling. Send is always pressable; on an empty
            // draft it simply has nothing to do (already a no-op in the model), and announcing
            // "unavailable" would claim more than that — a person with an empty composer is not
            // blocked, they just have not typed yet. The weight is the signal; the control
            // stays whole. `Resilience.fs` pins the same promise from the other direction.
            //
            // Same place in FOCUS order always, and now the same place on SCREEN always: this
            // row leaves the line and sits below it (`Style.draftBox`), and it
            // stands there whether or not there is a draft. The weight is what changes.
            //
            // The row used to come and go with the draft, so the one verb the composer is for
            // was off a phone's screen until the message was already written. Two earlier
            // tries at this are why the comment is long: revealing it on `focus-within` made
            // it unreachable (focus leaves on the tap that reaches for it, and iOS gives a
            // button no focus at all), and fading it with the draft left it in the tab order
            // while invisible. Standing is the answer both of those were circling.
            let sendClass =
                if hasContent then Style.btnComposerSend else Style.btnComposerSendWaiting
            let author =
                if target = myPeer then Lit.nothing
                else html $"""<span class="{Style.draftAuthor}">{ClientModel.nameOf target model}'s message</span>"""
            // Files arrive however a person has them: chosen, pasted into the draft, or dropped
            // on it. All three are one action, as on the `all` page, and choosing is the file
            // input itself in the `<label>` that is its face, as it is there.
            let take (files: Browser.Types.File array) =
                if Array.isEmpty files then false
                else
                    actions.Attach target (List.ofArray files)
                    true
            let picked (e: Browser.Types.Event) =
                EventTargets.asHTMLInputElement e.target
                |> Option.iter (fun input ->
                    take (CarriedFiles.ofInput input) |> ignore
                    // Emptied, so the same file chosen again is a change the input reports.
                    input.value <- "")
            let pasted (e: Browser.Types.Event) = if take (CarriedFiles.ofPaste e) then e.preventDefault ()
            let dragged (e: Browser.Types.Event) = if CarriedFiles.inDrag e then e.preventDefault ()
            let dropped (e: Browser.Types.Event) = if take (CarriedFiles.ofDrop e) then e.preventDefault ()
            // Each file on the draft: its name, then what it weighs — or, while this browser is
            // sending it, how far it has got, beating in `ink-dim`; or the one red word, with
            // what can be done about it. Remove is always there: a file on a message is
            // anybody's to take off, as the message's words are anybody's to edit.
            let file (id: string, name: string, bytes: int64, upload: Upload option) =
                let token, status =
                    match upload |> Option.map (fun u -> u.State) with
                    | Some (UploadState.Sending sent) ->
                        "sending",
                        html $"""<span class="{Style.statusWait}">{Icon.spinLead}{Dom.Text.uploadSending (ContentSize.render sent) (ContentSize.render bytes)}</span>"""
                    | Some (UploadState.Failed reason) -> "failed", html $"""<span class="{Style.statusErr}">{reason}</span>"""
                    | Some (UploadState.Refused reason) -> "refused", html $"""<span class="{Style.statusErr}">{reason}</span>"""
                    | None -> "attached", html $"""<span class="{Style.draftFileSize}">{ContentSize.render bytes}</span>"""
                let retry =
                    match upload with
                    | Some { State = UploadState.Failed _ } ->
                        html $"""
                            <button type="button" class="{Style.terminalListAct}" data-draft-file-retry="{id}"
                                    aria-label="{Dom.Text.retryUpload name}"
                                    @click={Ev(fun _ -> dispatch (RetryUploadMsg id))}>{Dom.Text.retry}</button>"""
                    | _ -> Lit.nothing
                let remove =
                    match UploadId.create id with
                    | Ok upload -> RemoveAttachmentMsg upload
                    | Error _ -> DismissUploadMsg id
                html $"""
                    <span class="{Style.draftFile}" role="listitem" data-draft-file="{name}" data-upload-state="{token}">
                      <span class="{Style.entityMark}" aria-hidden="true">{Icon.fileSm}</span>
                      <span class="{Style.draftFileName}">{name}</span>
                      <span role="status">{status}</span>
                      {retry}
                      <button type="button" class="{Style.terminalListAct}" data-draft-file-remove="{id}"
                              aria-label="{Dom.Text.removeAttachment name}"
                              @click={Ev(fun _ -> dispatch remove)}>{Dom.Text.remove}</button>
                    </span>"""
            let files =
                match ClientModel.draftFiles target model with
                | [] -> Lit.nothing
                | files ->
                    html $"""<div class="{Style.draftFiles}" role="list" aria-label="{Dom.Text.draftFilesNamed}">{files |> List.map file}</div>"""
            html $"""
                <article class="{Style.draftBox}" data-draft-id="{PeerId.value target}" data-draft-author="{PeerId.value target}"
                         @paste={Ev(pasted)} @dragover={Ev(dragged)} @drop={Ev(dropped)}>
                  <div class="{Style.draftBody}">
                    {author}
                    <div class="{Style.draftInput}" data-rich-body="{BodyKey.draft target}" data-rich-readonly="false" data-draft-input="{PeerId.value target}"></div>
                    {files}
                  </div>
                  <div class="{Style.draftActionsRow}">
                    <div class="{Style.draftLead}">
                      {modelControl dispatch model}
                      <label class="{Style.draftAttach}" data-draft-attach>
                        <input type="file" class="{Style.pickerInput}" multiple data-draft-attach-input
                               aria-label="{Dom.Text.attachNamed}" @change={Ev(picked)}>{Dom.Text.attach}</label>
                    </div>
                    <div class="{Style.draftCommit}">
                      <span class="{Style.draftEditors}">{editors target}</span>
                      <button type="button" class="{sendClass}" aria-label="send" aria-keyshortcuts="Control+Enter"
                              title="{Dom.Text.composerKeys}"
                              data-send-draft="{PeerId.value target}" data-send-ready="{if hasContent then "yes" else "no"}" @click={Ev(fun _ -> actions.SendDraft target)}>{Icon.send}</button>
                    </div>
                  </div>
                </article>"""
        // "New message" only says something when you are in someone else's draft: it is the way
        // out of collaborating, and pressing it collapses theirs to a summary.
        let startMine =
            if target = myPeer then Lit.nothing
            else
                html $"""
                    <button type="button" class="{Style.draftNew}" data-draft-new
                            @click={Ev(fun _ -> dispatch StartDraftMsg)}>+ New message</button>"""
        html $"""
            {composerTray dispatch model}
            <section class="{Style.composer}" data-draft-editor>
              <span class="{Style.bandRail}"></span>
              <span class="{Style.bandEdge}"></span>
              {agentLive}
              {ClientModel.collapsedDrafts model |> List.map summary}
              {open'}
              {startMine}
            </section>"""

    // --- Terminals (Plan 13) -------------------------------------------------------------

    /// Styled terminal output. Each parsed run becomes one span carrying its SGR styling;
    /// lines are separated by real newlines inside a `pre-wrap` block, so selecting and
    /// copying output yields the text a person would expect rather than a run of divs.
    ///
    /// Unstyled text is ONE piece however many lines it spans: consecutive plain runs, and the
    /// newlines between them, are joined into a single text node, and only a styled run gets
    /// a node of its own. A template per line was a template instance, its markers and its
    /// text — some ten nodes a line, so `seq 100000` drew a million — for output whose every
    /// line looked the same. Plain output, which is most of it, now costs the page one node.
    ///
    /// With a CURSOR, the character it stands on is drawn as the caret (`Style.terminalCaret`)
    /// — a live screen's, which is the one surface here a program is typing into. A cursor past
    /// the end of its line, or below the last one, stands on blanks the serialization did not
    /// write, so they are written here: a prompt's cursor sits one past the `$ `, and a caret
    /// that snapped back onto the space would say the next key lands where it does not.
    let private ansiLines (lines: AnsiLine list) (cursor: ScreenCursor option) : TemplateResult list =
        let pieces = ResizeArray<TemplateResult> ()
        let plain = System.Text.StringBuilder ()
        let flush () =
            if plain.Length > 0 then
                pieces.Add (html $"{plain.ToString ()}")
                plain.Clear () |> ignore
        let emit (span: AnsiSpan) =
            if span.Text <> "" then
                let classes = Style.ansiClasses span.Style
                let inline' = Style.ansiInline span.Style
                if classes = "" && inline' = "" then plain.Append span.Text |> ignore
                else
                    flush ()
                    pieces.Add (html $"""<span class="{classes}" style="{inline'}">{span.Text}</span>""")
        let caret (under: string) =
            flush ()
            pieces.Add (html $"""<span class="{Style.terminalCaret}" data-terminal-caret>{under}</span>""")
        let lines =
            match cursor with
            | Some at when at.Line >= List.length lines ->
                lines @ List.replicate (at.Line + 1 - List.length lines) { Spans = [] }
            | _ -> lines
        for i, line in List.indexed lines do
            // The newline BEFORE every line but the first, so a trailing line adds no
            // trailing blank one.
            if i > 0 then plain.Append '\n' |> ignore
            match cursor with
            | Some at when at.Line = i && at.Column >= 0 ->
                let mutable column = 0
                let mutable placed = false
                for span in line.Spans do
                    if placed || column + span.Text.Length <= at.Column then emit span
                    else
                        let within = at.Column - column
                        emit { span with Text = span.Text.Substring (0, within) }
                        caret (string span.Text.[within])
                        emit { span with Text = span.Text.Substring (within + 1) }
                        placed <- true
                    column <- column + span.Text.Length
                if not placed then
                    plain.Append (System.String (' ', at.Column - column)) |> ignore
                    caret " "
            | _ -> for span in line.Spans do emit span
        flush ()
        List.ofSeq pieces

    let private ansiText (text: string) : TemplateResult list = ansiLines (Ansi.parse text) None

    /// How a block went, as its HOOKS spell it: six tokens for nine outcomes, because what a
    /// hook is asked is whether it went, not how. A line the shell refused is its own token
    /// because it did not go at all — and an outcome nobody knows borrows `ended`, which
    /// already means exactly that.
    let private terminalBlockStatusLabel =
        function
        | BlockRunning -> Dom.Text.blockRunning
        | BlockFinished (CommandSucceeded _) -> Dom.Text.blockOk
        | BlockFinished (CommandNotRun _) -> Dom.Text.blockNotRun
        | BlockFinished (CommandOutcomeUnknown _) -> Dom.Text.blockEnded
        | BlockFinished _ -> Dom.Text.blockFailed
        | BlockRejected _ -> Dom.Text.blockRejected
        | BlockEnded _ -> Dom.Text.blockEnded

    /// How a block went, in WORDS — what a screen reader hears where the mark draws a glyph
    /// and a number, and what a command chip's accessible name says in its middle.
    let private terminalBlockStatusWord (model: ClientModel) =
        function
        | BlockRunning -> Dom.Text.blockRunning
        | BlockFinished (CommandSucceeded code) -> Dom.Text.blockSucceeded code
        | BlockFinished (CommandFailed code) -> Dom.Text.blockExitFailed code
        | BlockFinished CommandTimedOut -> Dom.Text.blockTimedOut
        | BlockFinished (CommandExecutionFailed _) -> Dom.Text.failed
        | BlockFinished (CommandNotRun _) -> Dom.Text.blockDidNotRun
        | BlockFinished (CommandOutcomeUnknown _) -> Dom.Text.blockEnded
        | BlockRejected ((ActorRef.Session | ActorRef.System), _) -> Dom.Text.blockRefusedPlain
        | BlockFinished (CommandKilledForMemory _) -> Dom.Text.blockKilledForMemory
        | BlockRejected (by, _) -> Dom.Text.blockRefusedBy (Entity.actorName model by)
        | BlockEnded _ -> Dom.Text.blockEnded

    /// How a block went, drawn. ONE renderer for every surface that shows a block — its line
    /// in the pane, its tab's header, its chip in the chat — so a reader who learnt the marks
    /// on one surface has learnt them on all three: running is the pulse, `✓ 0` and `✕ 1` are
    /// how it exited, and anything that did not exit says so in a word.
    ///
    /// Success is drawn too. The pane used to leave it out, on the argument that `✓ 0` beside
    /// every command is the same fact printed whether or not it is news — and a reader who
    /// watched a command finish in the pane could not tell it had, from one still waiting on
    /// its first byte, without going to the chat to look.
    ///
    /// The glyph and the number are a picture; the words beside them are for whoever cannot
    /// see it (`terminalBlockStatusWord`). A mark that IS a word says it once.
    ///
    /// A wrong block is red WHEREVER it is drawn, and however many of them a scrollback holds.
    /// A block's line is a repeated row, and a repeated row is a surface of its own, so the one
    /// red word each surface keeps is its own (docs/visual-design.md, "Colour"). This took a
    /// `current` flag that quieted every failure but the newest to `statusWait`, counting the
    /// red words per SCREEN — which read, at 390x844, as five wrong commands with one red word
    /// between them: four failures wearing the record's ink and not looking wrong at all.
    /// Honesty is second in the Deciding order and a hidden failure trades it away.
    let private terminalBlockStatus (model: ClientModel) (status: BlockStatus) : TemplateResult =
        let token = terminalBlockStatusLabel status
        let pictured (voice: string) (mark: TemplateResult) =
            html $"""<span class="{voice}" data-block-mark="{token}"><span aria-hidden="true">{mark}</span><span class="{Style.srOnly}">{terminalBlockStatusWord model status}</span></span>"""
        match status with
        | BlockRunning -> pictured Style.statusOk Icon.spin
        | BlockFinished (CommandSucceeded code) -> pictured Style.statusDone (html $"""{Icon.checkSm} {code}""")
        | BlockFinished (CommandFailed code) -> pictured Style.statusErr (html $"""{Icon.crossSm} {code}""")
        | BlockFinished CommandTimedOut
        | BlockFinished (CommandExecutionFailed _)
        | BlockFinished (CommandNotRun _)
        | BlockFinished (CommandKilledForMemory _) ->
            html $"""<span class="{Style.statusErr}" data-block-mark="{token}">{terminalBlockStatusWord model status}</span>"""
        // A record, not a wrong thing: nothing says it failed, so it is not red. Faint like
        // any other status with nothing to report, and the block's facts carry why it ended.
        | BlockFinished (CommandOutcomeUnknown _)
        | BlockEnded _ ->
            html $"""<span class="{Style.statusFaint}" data-block-mark="{token}">{terminalBlockStatusWord model status}</span>"""
        // Named, not merely absent. "refused by nick" in line with the commands that ran
        // is the whole reason a refusal mints a block at all — so it is a NAME, resolved like
        // every other person on screen, not the id the hook carries.
        // The session's own refusals name nobody: there is no person to name, and its token is
        // internal (`Dom.Text.blockRefusedPlain`).
        | BlockRejected ((ActorRef.Session | ActorRef.System), _) ->
            html $"""<span class="{Style.statusErr}" data-block-mark="{token}">{Dom.Text.blockRefusedPlain}</span>"""
        | BlockRejected (by, _) ->
            html $"""<span class="{Style.statusErr}" data-block-mark="{token}">{Dom.Text.blockRefused} {Entity.render model by (EntityRef.Actor by)}</span>"""

    /// Whether this block is the one its terminal's loss left open (`Projection.openAtLoss`).
    let private openAtLoss (model: ClientModel) (terminal: TerminalId) (block: Block) : bool =
        Projection.tryFind terminal model.Terminals
        |> Option.exists (fun view -> Projection.openAtLoss view block)

    /// A block's hook token where it is drawn — `unknown` for the one a loss left open, which
    /// is not running so far as anyone can say.
    let private terminalBlockTokenIn (model: ClientModel) (terminal: TerminalId) (block: Block) : string =
        if openAtLoss model terminal block then Dom.Text.blockUnknown else terminalBlockStatusLabel block.Status

    /// `terminalBlockStatus`, for a block in its terminal: the one a loss left open says it
    /// may have run, in words and without the pulse, rather than spinning for as long as the
    /// loss lasts. Waiting, not wrong — the banner says what to do about it.
    let private terminalBlockStatusAt (model: ClientModel) (terminal: TerminalId) (block: Block) : TemplateResult =
        if openAtLoss model terminal block then
            html $"""<span class="{Style.statusWait}" data-block-mark="{Dom.Text.blockUnknown}">{Dom.Text.blockMayHaveRun}</span>"""
        else terminalBlockStatus model block.Status

    let private stretchEndLabel =
        function
        | LeaseReleased -> Dom.Text.stretchReleased
        | LeaseStolen _ -> Dom.Text.stretchStolen
        | LeaseHolderGone -> Dom.Text.stretchGone
        | LeaseIdle -> Dom.Text.stretchIdle

    /// How a stretch ended, said the way a reader asks it — in the `voice` of the surface it
    /// is said on: the chat's status capitals, or the pane's caption.
    let private stretchEnding (voice: string) (model: ClientModel) =
        function
        | LeaseReleased -> html $"""<span class="{voice}">handed back</span>"""
        | LeaseStolen by -> html $"""<span class="{voice}">taken over by {Entity.render model by (EntityRef.Actor by)}</span>"""
        | LeaseHolderGone -> html $"""<span class="{voice}">holder left</span>"""
        | LeaseIdle -> html $"""<span class="{voice}">went idle</span>"""

    /// One line of a task card: a command's block, or a command the turn asked for that has
    /// no block yet (`TimelineProjection.rows` groups the two alike, so a card does not lose
    /// a line, or gain one, when a command starts).
    [<RequireQualifiedAccess>]
    type private CardLine =
        | Block of TerminalId * Block
        | Call of ToolUse * CommandCall

    module private CardLine =

        /// How the line reads on the card's summary. A call with no block is still going
        /// until it says it will never start.
        let state =
            function
            | CardLine.Block (_, block) -> TaskCard.stateOf block.Status
            | CardLine.Call (_, CommandDidNotStart _) -> TaskFailed
            | CardLine.Call (_, (CommandStarting | CommandWaiting)) -> TaskRunning

    /// Which hold a queued command is under, and how that hold reads.
    ///
    /// One function because two surfaces say this one fact — the chip in the chat and the
    /// card in the terminal's own band — and a two-way verdict written twice agrees only
    /// until somebody edits one of them.
    ///
    /// A held act is a WAIT, and the pulse dot is the wait — the word only names the blocker
    /// (the same status voice every other wait in the product wears). A terminal whose shell
    /// cannot run commands holds nothing: the loss refuses its queue, so it has no status here.
    let private pendingStatus (model: ClientModel) (entry: PendingAct) : string * TemplateResult =
        if ClientModel.awaitsTerminal entry model then
            Dom.Text.queuedAwaitingTerminal,
            html $"""<span class="{Style.statusWait}">{Icon.spinLead}terminal busy</span>"""
        else
            // Queued is a WAIT, like the hold above it, so it wears its voice and its
            // dot rather than live green (docs/visual-design.md, Colour). Green said the
            // opposite of what it meant: a queued command is the one thing in the terminal
            // that is NOT running, and it was the only status on the surface wearing live's
            // colour while the command actually running wore it too.
            Dom.Text.queuedReady,
            html $"""<span class="{Style.statusWait}">{Icon.spinLead}queued</span>"""

    /// What the terminal this act is queued in is CALLED — its name, or its id while this
    /// client has not folded the terminal yet. Words, never a raw id when a name exists, for
    /// the reason every other author and subject on screen is.
    let private pendingSubject (model: ClientModel) (entry: PendingAct) : string =
        Entity.terminalName model entry.Terminal
        |> Option.defaultValue (TerminalId.value entry.Terminal)

    /// A command the agent has queued, in the chat: the SAME chip its block will leave behind
    /// once it has run, drawn where that block will land.
    ///
    /// It replaced a card that stood in a dock above the composer, editable and withdrawable
    /// in place. The card is not gone — it is in the terminal's own band, which is where the
    /// rest of that terminal's queue already was — and this is the read of it: an agent
    /// reaching for a terminal is the same kind of act as an agent reaching for a tool, and
    /// it should read like one rather than take a sixth of a phone's screen to say so. So
    /// tapping it opens the terminal it is queued in, where it can be edited, reordered or
    /// withdrawn.
    ///
    /// No author of its own, for the reason a block chip has none: WHO asked for it is the
    /// group's author line above it.
    let private pendingChip
        (actions: ViewActions)
        (dispatch: ClientMsg -> unit)
        (model: ClientModel)
        (entry: PendingAct)
        : TemplateResult =
        let statusToken, statusLine = pendingStatus model entry
        let what = pendingSubject model entry
        // The command itself lives in a `Y.Text` root, not in the model — it is editable by
        // every peer until it drains — so the text is pushed in by the browser half
        // (`Render.syncTerminalInputs`) rather than rendered here. A `<code>` and not a
        // read-only `<input>`: a form control inside a button is not legal content, and it
        // would eat the press this button exists for.
        html $"""
            <button type="button" class="{Style.chatChip}"
                    data-chat-pending="{QueueId.value entry.QueueId}"
                    data-chat-pending-status="{statusToken}"
                    data-terminal-id="{TerminalId.value entry.Terminal}"
                    @click={Ev(fun _ -> dispatch (OpenInPaneMsg (Reading entry.Terminal)))}>
              <span class="{Style.cls [ Style.chatChipPrompt; Style.terminalPrompt ]}">$</span>
              <code class="{Style.chatChipCommand}" data-terminal-text="{BodyKey.terminalQueued entry.QueueId}"></code>
              <span class="{Style.chatChipIn}">in</span>
              <span class="{Style.chatChipSubject}" data-pending-subject="terminal:{TerminalId.value entry.Terminal}" title="{what}">{what}</span>
              <span class="shrink-0">{statusLine}</span>
            </button>"""

    /// A stretch's length, in the coarsest unit that still says something. Sub-second is not
    /// a session someone had; it is a lease that bounced.
    let private durationText (span: System.TimeSpan) : string =
        let seconds = int (round span.TotalSeconds)
        if seconds >= 3600 then sprintf "%dh %dm" (seconds / 3600) ((seconds % 3600) / 60)
        elif seconds >= 60 then sprintf "%dm %ds" (seconds / 60) (seconds % 60)
        else sprintf "%ds" seconds

    /// The ask card's frame, which every card in the ask slot is drawn in: the blue lead, the
    /// track its panes ride, and the actions line under them. The caller writes the `<section>`
    /// round it, because its hook is the card's own name (`data-repo-picker`, `data-repo-trust`)
    /// and an attribute's NAME cannot be a hole.
    let private askInside (track: TemplateResult) (actions: TemplateResult) : TemplateResult =
        html $"""
            <div class="{Style.askLeadBar}" aria-hidden="true"></div>
            <div class="{Style.askBody}">
              {track}
              <div class="{Style.askActions}"><div class="{Style.askActionsLine}">{actions}</div></div>
            </div>"""

    /// A pane's head: the way out in the gutter, then who or what asks above the question. One
    /// shape for every pane of every card, so the question stands on the same line whichever
    /// card is in the slot and only the subtitle and the way out change.
    let private askHead (way: TemplateResult) (subtitle: TemplateResult) (titleId: string) (title: string) : TemplateResult =
        html $"""
            <div class="{Style.askHead}">
              <div class="{Style.askHeadLine}">
                {way}
                <div class="{Style.askHeadWords}">
                  {subtitle}
                  <h2 id="{titleId}" class="{Style.askQuestion}">{title}</h2>
                </div>
              </div>
            </div>"""

    /// A line the card says rather than one it offers, where the first row would stand
    /// (`Style.askNote`), so an answer and the absence of one arrive in one place.
    let private askNote (inner: TemplateResult) : TemplateResult =
        html $"""<div class="{Style.askNote}"><div class="{Style.askNoteLine}">{inner}</div></div>"""

    /// The chat: what was said and what was run, in the order it happened (Plan 14, stage 1).
    ///
    /// Terminal items are resolved against `Projection` at render time rather than
    /// copied into the timeline, which is what makes a running chip mutate in place as its
    /// block finishes — the timeline holds where it goes, the projection holds what it says.
    /// The launch surface: an ASK CARD, docked above the composer (`ClientModel.launchOffered`)
    /// — the session asking which repository it is for, with the ways to answer.
    ///
    /// Its anatomy is the one the agent's questions will wear later: who asks · the question
    /// · rows to hold · one button that commits · a way out. A row is a bordered rectangle —
    /// the product's one "press me" — and holding it is a state, not a send: the held row
    /// grows a branch field where its description was, and START is what sends. The card
    /// wears the blue leading edge a queued command wears, because it means the same thing
    /// there: waiting on you.
    let private askCard (actions: ViewActions) (dispatch: ClientMsg -> unit) (model: ClientModel) : TemplateResult =
        let launch = model.Launch
        let busy = Launch.busy launch
        let stage =
            match launch.Stage with
            | Choosing -> "choosing"
            | Resolving _ -> "resolving"
            | Sent _ -> "sent"
            | Cloning _ -> "cloning"
        let onFieldKey (e: Browser.Types.KeyboardEvent) =
            if e.key = "Enter" && not busy then
                e.preventDefault ()
                dispatch (LaunchMsg LaunchSubmitted)
        // Holding a row asks for its branches, and so does opening its pane: `Launch.update`
        // keeps that to once per row.
        let hold (candidate: Repos.RepoCandidate) = dispatch (LaunchMsg (LaunchSelected candidate))
        let openBranches (repo: RepoRef) = dispatch (LaunchMsg (LaunchBranchPaneOpened repo))
        // The held row's branch, at the row's trailing edge: the name it will launch on, and
        // a way into the pane that changes it. A LINK rather than the field this used to be —
        // a field inside a row is a second thing to operate in a surface whose whole grammar
        // is "tap the thing you mean", and a repository with six hundred branches was a
        // datalist nobody could reach the end of.
        // It is a SIBLING of the row's button, on its own line under the name — never a child
        // of it. A button inside a button is not content a browser parses as written and no
        // keyboard can reach the inner half of it (`Style.askRowAside` says the rest). Being a
        // sibling is also what retires the `stopPropagation` that used to live here: the press
        // no longer travels through the row that holds the repo on its way out.
        //
        // While the clone is under way there is nothing for it to act on, so it wears the bare
        // weight's waiting face and is `aria-disabled` rather than `disabled`, which is an
        // attribute this product does not have (docs/visual-design.md, State). It stays in
        // focus order and the handler refuses the press.
        let branchLink (candidate: Repos.RepoCandidate) =
            let name = RepoRef.value candidate.Repo
            html $"""
                <div class="{Style.askRowAside}">
                  <div class="{Style.askRowAsideLine}">
                    <button type="button" class="{if busy then Style.askRowBranchWaiting else Style.askRowBranch}"
                            data-repo-candidate-branch="{name}" aria-disabled="{if busy then "true" else "false"}"
                            aria-label="{Dom.Text.repoPickerBranchOf} {Launch.branchOf launch candidate}"
                            @click={Ev(fun _ -> if not busy then openBranches candidate.Repo)}>
                      <span class="{Style.askRowBranchName}">{Launch.branchOf launch candidate}</span>
                      {Icon.right}
                    </button>
                  </div>
                </div>"""
        let row (candidate: Repos.RepoCandidate) =
            let name = RepoRef.value candidate.Repo
            let heldNow = launch.Selected = Some candidate.Repo
            // A held row's second line is its branch field, so the description gives way.
            let description =
                if heldNow then []
                else
                    candidate.Description
                    |> Option.map (fun said -> html $"""<span class="{Style.askRowDescription}">{said}</span>""")
                    |> Option.toList
            // The mark's CELL is on every row and its contents only on the held one — the
            // gutter is what the names are aligned against, so a column that came and went
            // with the tick would move every name each time one was chosen.
            let mark =
                html $"""
                    <span class="{Style.askRowMark}" aria-hidden="true">{if heldNow then Icon.check else Lit.nothing}</span>"""
            html $"""
                <li class="{if heldNow then Style.askRowHeld else Style.askRow}">
                  <button type="button" class="{Style.askRowButton}" data-repo-candidate="{name}"
                          aria-pressed="{if heldNow then "true" else "false"}"
                          aria-disabled="{if busy then "true" else "false"}"
                          @click={Ev(fun _ -> if not busy then hold candidate)}>
                    <span class="{Style.askRowLine}">
                      {mark}
                      <span class="{Style.askRowName}" data-repo-candidate-name="{name}">{name}</span>
                      {description}
                    </span>
                  </button>
                  {if heldNow then branchLink candidate else Lit.nothing}
                </li>"""
        let note = askNote
        let listing =
            match launch.Listing with
            | ListingUnknown ->
                note (html $"""<span class="{Style.statusWait}" role="status">{Icon.spinLead}{Dom.Text.repoPickerLooking}</span>""")
            | ListingUnavailable (reason, true) ->
                let said = note (html $"""<span class="{Style.small}">{reason}</span>""")
                html $"""
                    {said}
                    <div class="{Style.askActions}">
                      <div class="{Style.askActionsLine}">
                        <button type="button" class="{Style.btn}" data-repo-picker-connect @click={Ev(fun _ -> dispatch RevealSettingsMsg)}>{Dom.Text.repoPickerConnect}</button>
                      </div>
                    </div>"""
            | ListingUnavailable (reason, false) ->
                note (html $"""<span class="{Style.statusErr}" role="status" data-repo-picker-failed>{reason}</span>""")
            // Nobody asked yet, so nothing failed that is theirs: one quiet line saying what
            // still works, and the provider's reason under it as the detail (`ListingWithheld`).
            | ListingWithheld reason ->
                note (html $"""
                    <p class="{Style.small}" data-repo-picker-unlisted>{Dom.Text.repoPickerUnlisted}</p>
                    <p class="{Style.small}">{reason}</p>""")
            | ListingLoaded page when List.isEmpty page.Candidates ->
                note (html $"""<span class="{Style.small}">{Dom.Text.repoPickerNothing}</span>""")
            | ListingLoaded page ->
                // The FOOT of the list: what the reader reaches, and what reaching brings.
                // Drawn only while there is a page to come, so what ends the scroll is the
                // list ending rather than a line saying it has.
                //
                // It is not a button. A press is what a reader does when the machine has
                // stopped; this is the machine carrying on, and the only reason it is in the
                // document at all is that something has to be reached. The one press here is
                // the one a FAILURE leaves, because a page that did not come will not come
                // again on its own.
                let foot =
                    match page.Next, launch.More with
                    | None, _ -> Lit.nothing
                    | Some _, MoreFailed reason ->
                        html $"""
                            <div class="{Style.askFoot}" data-repo-picker-foot="failed">
                              <div class="{Style.askFootLine}">
                                <span class="{Style.statusErr}" role="status">{reason}</span>
                                <button type="button" class="{Style.askLink}" data-repo-picker-again
                                        @click={Ev(fun _ -> dispatch (LaunchMsg LaunchMoreRetried))}>{Dom.Text.repoPickerAgain}</button>
                              </div>
                            </div>"""
                    | Some _, (MoreIdle | MoreFetching) ->
                        html $"""
                            <div class="{Style.askFoot}" data-repo-picker-foot="more">
                              <div class="{Style.askFootLine}">
                                <span class="{Style.caretWorking}" aria-hidden="true"></span>
                                <span class="{Style.label}" role="status">{Dom.Text.repoPickerMoreComing}</span>
                              </div>
                            </div>"""
                html $"""<ul class="{Style.askRows}">{page.Candidates |> List.map row}</ul>{foot}"""
        let problem =
            match launch.Problem with
            | Some reason -> note (html $"""<span class="{Style.statusErr}" role="alert" data-repo-picker-problem>{reason}</span>""")
            | None -> Lit.nothing
        // The commit button, generalised from Create's box-stable hold
        // (`Style.btnPrimarySwap`/`whenReady`/`whenBusy`, `app/ManagerUi.fs`): "Start" and
        // "starting…" share ONE grid cell, so pressing it never resizes the box, and it stays
        // the SAME element through `Sent`/`Cloning` — `aria-busy` flips rather than the button
        // being replaced by a status line, which is what a DOM swap mid-press would strand
        // focus doing. `target` is the STAGE's own, not re-derived from `Selected`, so a row
        // that drops out of the listing while the clone is under way cannot blank a target
        // already committed.
        // Nothing held is the WAITING face, not a `disabled` attribute — which does not exist
        // in this product (docs/visual-design.md, State). The button stays in the layout and in
        // focus order, its border carries the waiting, its word stays legible, and the handler
        // is what refuses a press that has nothing to start.
        let startButton (target: LaunchTarget option) (busy: bool) : TemplateResult =
            let ready = target.IsSome && not busy
            html $"""
                <button type="button" class="{if ready || busy then Style.askStart else Style.askStartWaiting}" data-repo-picker-start
                        aria-disabled="{if ready || busy then "false" else "true"}" aria-busy="{if busy then "true" else "false"}"
                        @click={Ev(fun _ -> if ready then dispatch (LaunchMsg LaunchStartPressed))}>
                  <span class="{Style.whenReady}">{Dom.Text.repoPickerStart}</span><span class="{Style.whenBusy}">{Dom.Text.repoPickerCloning}</span>
                </button>"""
        let actionsRow =
            match launch.Stage with
            | Resolving _ ->
                html $"""<span class="{Style.statusWait}" role="status">{Icon.spinLead}{Dom.Text.repoPickerLooking}</span>"""
            | Choosing -> startButton (Launch.target launch) false
            | Sent (_, target) -> startButton (Some target) true
            | Cloning target -> startButton (Some target) true
        // Both panes fill the one head (`askHead`), so the title never moves between them. The
        // gutter carries the way OUT, which is what makes the two panes one shape: × leaves the
        // card, ‹ leaves the pane.
        let head way subtitle title = askHead way subtitle "repo-picker-title" title
        let dismiss =
            html $"""
                <button type="button" class="{Style.askWay}" data-repo-picker-dismiss
                        aria-label="{Dom.Text.repoPickerDismiss}" title="{Dom.Text.repoPickerDismiss}"
                        @click={Ev(fun _ -> dispatch DismissLaunchMsg)}>{Icon.close}</button>"""
        let back =
            html $"""
                <button type="button" class="{Style.askWay}" data-repo-picker-back
                        aria-label="{Dom.Text.repoPickerBack}" title="{Dom.Text.repoPickerBack}"
                        @click={Ev(fun _ -> dispatch (LaunchMsg LaunchBranchPaneClosed))}>{Icon.left}</button>"""
        let onBranchPane =
            match launch.Pane with
            | ChoosingRepo -> false
            | ChoosingBranch _ -> true
        // The branch pane: the repo pane's shape with different words. It is DRAWN whether or
        // not it is the one on screen, because the two ride a track that slides — a pane that
        // appeared at the end of the slide would arrive on an empty stage. `inert` on the one
        // behind is what keeps a keyboard out of it, since it is still in the document.
        let branchPane =
            let subject =
                match launch.Pane with
                | ChoosingBranch repo -> Some repo
                | ChoosingRepo -> Launch.held launch |> Option.map (fun candidate -> candidate.Repo)
            match subject with
            | Some repo ->
                let name = RepoRef.value repo
                let repoCandidate = Launch.candidates launch |> List.tryFind (fun c -> c.Repo = repo)
                // The branch START WILL LAUNCH ON, which is what the tick marks. It was
                // `launch.Named` alone, so a pane opened before anybody had named a branch
                // ticked nothing at all while START stood lit and would clone the provider's
                // default — the card showing no answer to a question it had already answered.
                // State is worn, not hinted (docs/visual-design.md, principle 4), and what the
                // row says and what the command carries are now the same answer
                // (`Launch.branchOf`).
                let chosen =
                    match repoCandidate with
                    | Some candidate -> Some (Launch.branchOf launch candidate)
                    | None -> launch.Named |> Map.tryFind repo
                let pick (branch: string) =
                    dispatch (LaunchMsg (LaunchBranchNamed (repo, branch)))
                    dispatch (LaunchMsg LaunchBranchPaneClosed)
                let branchRow (branch: string) (note: TemplateResult) =
                    let heldNow = chosen = Some branch
                    html $"""
                        <li class="{if heldNow then Style.askRowHeld else Style.askRow}">
                          <button type="button" class="{Style.askRowButton}" data-repo-branch="{branch}"
                                  aria-pressed="{if heldNow then "true" else "false"}"
                                  aria-disabled="{if busy then "true" else "false"}"
                                  @click={Ev(fun _ -> if not busy then pick branch)}>
                            <span class="{Style.askRowLine}">
                              <span class="{Style.askRowMark}" aria-hidden="true">{if heldNow then Icon.check else Lit.nothing}</span>
                              <span class="{Style.askRowName}" data-repo-branch-name="{branch}">{branch}</span>
                              {note}
                            </span>
                          </button>
                        </li>"""
                let listed = Launch.branchesOn launch repo
                // What was typed, when it is not a branch the provider has. Above the list,
                // because it is the one answer a list can never carry: `switch_branch` makes
                // a branch that does not exist yet.
                let naming =
                    match Launch.namingNew launch repo with
                    | None -> Lit.nothing
                    | Some typed ->
                        branchRow typed (html $"""<span class="{Style.askRowNoteNew}">{Dom.Text.repoPickerBranchNew}</span>""")
                let rows =
                    listed
                    |> List.map (fun branch ->
                        // THIS pane's repo's default, not whichever row happens to be held in
                        // the pane behind it — the two are the same while the pane was opened
                        // from the held row and differ the moment it was not.
                        let note =
                            if Some branch = (repoCandidate |> Option.map (fun c -> c.DefaultBranch)) then
                                html $"""<span class="{Style.askRowNote}">{Dom.Text.repoPickerBranchDefault}</span>"""
                            else Lit.nothing
                        branchRow branch note)
                let branchBody =
                    match launch.Branches |> Map.tryFind repo with
                    | Some (BranchesUnavailable reason) ->
                        note (html $"""<span class="{Style.statusErr}" role="status">{reason}</span>""")
                    | Some BranchesUnknown
                    | None ->
                        note (html $"""<span class="{Style.statusWait}" role="status">{Icon.spinLead}{Dom.Text.repoPickerLooking}</span>""")
                    | Some (BranchesLoaded page) ->
                        let foot =
                            match page.Next, launch.BranchMore, launch.BranchQuery.Trim () with
                            | None, _, _ -> Lit.nothing
                            // A search narrows what has arrived rather than asking the
                            // provider (it has no branch search), so the foot stands down
                            // while one is on: paging into a filter fetches what nobody sees.
                            | Some _, _, typed when typed <> "" -> Lit.nothing
                            | Some _, MoreFailed reason, _ ->
                                html $"""
                                    <div class="{Style.askFoot}" data-repo-branch-foot="failed">
                                      <div class="{Style.askFootLine}">
                                        <span class="{Style.statusErr}" role="status">{reason}</span>
                                        <button type="button" class="{Style.askLink}" data-repo-branch-again
                                                @click={Ev(fun _ -> dispatch (LaunchMsg LaunchBranchMoreRetried))}>{Dom.Text.repoPickerAgain}</button>
                                      </div>
                                    </div>"""
                            | Some _, (MoreIdle | MoreFetching), _ ->
                                html $"""
                                    <div class="{Style.askFoot}" data-repo-branch-foot="more">
                                      <div class="{Style.askFootLine}">
                                        <span class="{Style.caretWorking}" aria-hidden="true"></span>
                                        <span class="{Style.label}" role="status">{Dom.Text.repoPickerMoreComing}</span>
                                      </div>
                                    </div>"""
                        if List.isEmpty listed && (Launch.namingNew launch repo).IsNone then
                            note (html $"""<span class="{Style.small}">{Dom.Text.repoPickerNoBranch}</span>""")
                        else html $"""<ul class="{Style.askRows}">{naming}{rows}</ul>{foot}"""
                let subtitle =
                    html $"""<span class="{Style.askSubject}" data-repo-picker-subject>{name}</span>"""
                let branchHead = head back subtitle Dom.Text.repoPickerBranchTitle
                html $"""
                    <div class="{if onBranchPane then Style.askPaneHere else Style.askPaneRight}"
                         data-repo-picker-pane="branch" ?inert={not onBranchPane}>
                      {branchHead}
                      <label class="{Style.srOnly}" for="repo-branch-search">{Dom.Text.repoPickerBranchSearchLabel}</label>
                      <div class="{Style.askField}">
                        <input id="repo-branch-search" type="search" class="{Style.askSearch}" data-repo-branch-search
                               placeholder="{Dom.Text.repoPickerBranchSearchPlaceholder}"
                               autocapitalize="off" autocorrect="off" autocomplete="off" spellcheck="false"
                               ?readonly={busy}
                               .value={launch.BranchQuery}
                               @input={EvVal(fun v -> dispatch (LaunchMsg (LaunchBranchQueryTyped v)))} />
                      </div>
                      <div class="{Style.askScroll}" data-repo-branch-body>{branchBody}</div>
                    </div>"""
            // No row held: there is no repository to have branches of, so the pane is not
            // drawn at all rather than drawn empty.
            | None -> Lit.nothing
        let repoHead =
            let subtitle = html $"""<span class="{Style.askSubtitle}">{Dom.Text.repoPickerAsker}</span>"""
            head dismiss subtitle Dom.Text.repoPickerTitle
        let repoPane =
            html $"""
                <div class="{if onBranchPane then Style.askPaneLeft else Style.askPaneHere}"
                     data-repo-picker-pane="repo" ?inert={onBranchPane}>
                  {repoHead}
                  <label class="{Style.srOnly}" for="repo-picker-search">{Dom.Text.repoPickerSearchLabel}</label>
                  <!-- `readonly` and not `disabled`: the attribute `disabled` does not exist in
                       this product (docs/visual-design.md, State). While the clone it started
                       runs there is nothing to search for, and a field that is still focusable
                       and still legible says that without leaving the document. -->
                  <div class="{Style.askField}">
                    <input id="repo-picker-search" type="search" class="{Style.askSearch}" data-repo-picker-search
                           placeholder="{Dom.Text.repoPickerSearchPlaceholder}"
                           autocapitalize="off" autocorrect="off" autocomplete="off" spellcheck="false"
                           enterkeyhint="go" ?readonly={busy}
                           .value={launch.Query}
                           @input={EvVal(fun v -> dispatch (LaunchMsg (LaunchQueryTyped v)))}
                           @keydown={Ev(Events.key onFieldKey)} />
                  </div>
                  <div class="{Style.askScroll}" data-repo-picker-body>
                    {listing}
                    {problem}
                  </div>
                </div>"""
        let track = html $"""<div class="{Style.askTrack}" data-repo-picker-track>{repoPane}{branchPane}</div>"""
        html $"""
            <section class="{Style.ask}" data-repo-picker="{stage}" aria-labelledby="repo-picker-title">
              {askInside track actionsRow}
            </section>"""

    /// A repo waiting for somebody here to trust it with their GitHub (`RepoTrustWaits`), as the
    /// SAME ask card the launch surface is (`askCard`), in the same slot: docked above the
    /// composer, the blue lead, the question in the head with the way out in its gutter, rows
    /// to hold, one button that commits. It is the launch's next question — the repo somebody
    /// just chose asking whether it may run with their GitHub — so it stands where the first
    /// one stood rather than in the roster, where a decision sat beside the people list and
    /// read as one of them.
    ///
    /// Every reader sees it, because whoever answers is whose GitHub the repo runs with; the
    /// session refuses a reader with none of their own. One card at a time, oldest first: the
    /// slot holds one question, and the next repo's stands there once this one is answered.
    ///
    /// The answer carries what the card SHOWED beside what is ticked. It answers those routes
    /// and no others, so a repo that asks for more after the card was drawn asks again. The ×
    /// is "not now": it decides nothing, and only this reader stops being asked.
    let private trustCard (dispatch: ClientMsg -> unit) (repo: RepoRef) (shown: ConnectionRoute list) (model: ClientModel) : TemplateResult =
        let name = RepoRef.value repo
        let ticked = TrustCards.ticked repo shown model.TrustCards
        let routeRow (route: ConnectionRoute) =
            let on = List.contains route ticked
            let label, detail =
                match route with
                | ConnectionRoute.Git -> Dom.Text.trustRouteGit, Dom.Text.trustRouteGitDetail
                | ConnectionRoute.Api -> Dom.Text.trustRouteApi, Dom.Text.trustRouteApiDetail
            html $"""
                <li class="{if on then Style.askRowHeld else Style.askRow}">
                  <button type="button" class="{Style.askRowButton}" role="checkbox"
                          aria-checked="{if on then "true" else "false"}"
                          data-repo-trust-route="{ConnectionRoute.name route}"
                          @click={Ev(fun _ -> dispatch (ToggleTrustRouteMsg (repo, route)))}>
                    <span class="{Style.askRowLine}">
                      <span class="{Style.askRowMark}" aria-hidden="true">{if on then Icon.check else Lit.nothing}</span>
                      <span class="{Style.askRowWords}">{label}</span>
                      <span class="{Style.askRowDescription}">{detail}</span>
                    </span>
                  </button>
                </li>"""
        let later =
            html $"""
                <button type="button" class="{Style.askWay}" data-repo-trust-later="{name}"
                        aria-label="{Dom.Text.trustLater}" title="{Dom.Text.trustLater}"
                        @click={Ev(fun _ -> dispatch (NotNowTrustMsg (repo, shown)))}>{Icon.close}</button>"""
        let subject = html $"""<span class="{Style.askSubject}">{name}</span>"""
        let why = askNote (html $"""<p class="{Style.small}">{Dom.Text.trustWhy}</p>""")
        let pane =
            html $"""
                <div class="{Style.askTrack}">
                  <div class="{Style.askPaneHere}">
                    {askHead later subject "repo-trust-title" Dom.Text.trustTitle}
                    <div class="{Style.askScroll}">
                      {why}
                      <ul class="{Style.cls [ Style.askRows; Style.askRowsAfterNote ]}">{shown |> List.map routeRow}</ul>
                    </div>
                  </div>
                </div>"""
        let answer =
            html $"""
                <button type="button" class="{Style.askStart}" data-repo-trust-answer="{name}"
                        @click={Ev(fun _ -> dispatch (TrustRepoMsg (repo, shown, ticked)))}>
                  <span class="{Style.whenReady}">{Dom.Text.trustAnswer}</span>
                </button>"""
        html $"""
            <section class="{Style.ask}" data-repo-trust="{name}" aria-labelledby="repo-trust-title">
              {askInside pane answer}
            </section>"""

    /// What stands in the ask slot above the composer, if anything: a repo waiting to be
    /// trusted first, because it is the question the launch's answer led to, then the launch.
    let private askSlot (actions: ViewActions) (dispatch: ClientMsg -> unit) (model: ClientModel) : TemplateResult =
        match TrustCards.showing (RepoTrustWaits.waiting model.TrustWaits) model.TrustCards with
        | (repo, shown) :: _ -> trustCard dispatch repo shown model
        | [] when ClientModel.launchOffered model -> askCard actions dispatch model
        | [] -> Lit.nothing

    /// Who did something, as the transcript heads what they said: their square and their name
    /// in the caps voice, the agent's in blue. One face wherever an author heads what follows —
    /// a run of messages, a command in a terminal — so one person reads as one person
    /// whichever column they are found in, you included.
    ///
    /// `square` sizes the square: the transcript's, or a terminal's, which is smaller where the
    /// line it heads is a phone's.
    let private actorHead (model: ClientModel) (square: string) (actor: ActorRef) : TemplateResult =
        let whoClass = if actor = ActorRef.Agent then Style.whoAgent else Style.who
        html $"""
            <span class="{Style.cls [ square; Entity.actorMark model actor ]}"></span>
            <span class="{whoClass}">{Entity.actorName model actor}</span>"""

    let private chat (actions: ViewActions) (dispatch: ClientMsg -> unit) (model: ClientModel) : TemplateResult =
        // Where chapters open, asked once for the whole transcript: every row and every
        // item's menu below looks itself up here rather than asking again.
        let chapterOpenings = ClientModel.chapterOpenings model
        // A file an agent pointed at in a message — linked, in angle brackets, or written bare —
        // drawn as the reference it is (`RichText`): the SAME chip the fold below it draws for
        // the act of sharing one (`Entity.render`), because a body saying "see
        // file:///artifacts/chart.png" and a note saying "shared artifact chart.png" are
        // pointing at one thing and a reader should not have to notice that twice.
        let contentChip (by: ActorRef) (ref: ContentRef) = Entity.render model by (EntityRef.Content ref)
        // What can be done to one item, behind an ellipsis at its top-right. It goes on
        // every item that HAS an id — a message and an act alike — because "divide it
        // anywhere" is the promise, and chapters chosen for the reader would be somebody
        // else's account of the session rather than the reader's own.
        //
        // A MENU rather than the bare toggle this replaced, and not only because more will
        // hang off it. The toggle wore the rail's own hairline, so the margin carried two
        // near-identical columns of dashes twenty pixels apart — one positioned by the
        // conversation, one by the rail's own arithmetic, and nothing on the screen saying
        // which was which.
        //
        // The dispatch carries the id alone: which way the verdict goes is `Chapters.toggle`'s
        // to work out from the item, so this control never holds a second copy of what an act
        // defaults to (see `Chapters`).
        //
        // `onHeadline`: the item's first line is not its headline (an act under its cause),
        // so the control and its menu are placed on the headline's row instead.
        let itemActionsAt (onHeadline: bool) (item: ConversationItem) =
            let isChapter = Set.contains item.MessageId chapterOpenings
            let opened = model.ItemMenu = Some item.MessageId
            let control = if onHeadline then Style.itemActionsOnHeadline else Style.itemActions
            let menuClass = if onHeadline then Style.itemMenuOnHeadline else Style.itemMenu
            let dress =
                if opened then Style.cls [ control; Style.itemActionsOpen ] else control
            // Rendered only while open. A menu per item kept in the document and hidden would
            // be twenty menus in the accessibility tree of a twenty-message conversation, and
            // twenty more with every message that arrives.
            let menu =
                if not opened then Lit.nothing
                else
                    html $"""
                        <button type="button" class="{Style.itemMenuBackdrop}" tabindex="-1"
                                aria-label="{Dom.Text.dismissMenu}"
                                @click={Ev(fun _ -> dispatch CloseItemMenuMsg)}></button>
                        <div class="{menuClass}" role="menu" data-item-menu="{MessageId.value item.MessageId}">
                          <button type="button" role="menuitem" class="{Style.itemMenuEntry}"
                                  data-item-chapter="{MessageId.value item.MessageId}"
                                  data-item-is-chapter="{if isChapter then "yes" else "no"}"
                                  @click={Ev(fun _ ->
                                                 dispatch (ToggleChapterMsg item.MessageId)
                                                 // Choosing strands focus exactly as Escape
                                                 // does: the entry is removed by the render
                                                 // that follows, and a keyboard that pressed
                                                 // Enter on it is left on `body`.
                                                 dispatch (MoveMsg (DomMove.FocusItemActions item.MessageId)))}>
                            {if isChapter then Dom.Text.removeChapter else Dom.Text.makeChapter}
                          </button>
                        </div>"""
            // Escape on the WRAPPER, so it fires wherever focus is inside the menu — and on
            // the control too, which is where focus is put back.
            html $"""
                <div class="contents" @keydown={Ev(Events.key (fun e ->
                                                       if e.key = "Escape" && opened then
                                                           dispatch CloseItemMenuMsg
                                                           dispatch (MoveMsg (DomMove.FocusItemActions item.MessageId))))}>
                  <button type="button" class="{dress}"
                          data-item-actions="{MessageId.value item.MessageId}"
                          aria-haspopup="menu" aria-expanded="{if opened then "true" else "false"}"
                          aria-label="{Dom.Text.itemActions}"
                          @click={Ev(fun _ -> dispatch (ToggleItemMenuMsg item.MessageId))}>{Icon.more}</button>
                  {menu}
                </div>"""
        let itemActions = itemActionsAt false
        // The particulars, under the headline and never instead of it. A second LINE rather
        // than a disclosure: what an act asked for is exactly what a person has to decide
        // about, and a decision behind a click is a decision most readers never see. One
        // element per particular, each drawn as a phrase, so a reference in one is drawn as
        // that thing is drawn everywhere. The act is what says which acts have any — see
        // `Act.particulars`.
        let actNoteParticulars (by: ActorRef) (act: Act) =
            Act.particulars act
            |> List.map (fun p -> html $"""<span class="{Style.actNoteDetail}" data-act-detail>{Entity.phrase model by p}</span>""")
        // What the agent was told, as one row of a fact table. The facts around it are a
        // screen's arrangement of the act; this is the OTHER reader's, and a person is owed
        // the ability to see it — a timeline that showed a layout the agent never saw, and
        // hid the sentence it did, would be two accounts of one act with no way to compare
        // them. The same phrase (`Act.sentence`) both readers collapse, so `data-act-said`'s
        // text IS what the prompt carried, to the character.
        //
        // PLAIN, therefore. It used to draw each reference in the quote the way the screen
        // draws one — mark, link, resolved name — over the prose spelling, which put a
        // repository glyph and a person's checker inside a quotation of a prompt that never
        // held either: the row claimed to be what the agent read and looked like what the
        // screen renders. A quote of text is text. The line around it still names the agent
        // as a reference, because that is the SCREEN saying whose sentence follows.
        let toldRow (act: Act) =
            html $"""
                <div class="{Style.actNoteToldRow}">{Entity.render model ActorRef.Agent (EntityRef.Actor ActorRef.Agent)} {Dom.Text.actSaid} <span class="{Style.actNoteTold}" data-act-said>{Phrase.said (Act.sentence act)}</span></div>"""
        // A sandbox start shows its TITLE and nothing else on the line: which sandbox, drawn
        // as a reference (under the repo that declared it, that is the bare `dev`). Everything
        // the sentence also carries — the backend, what it is for, where the checkout sits,
        // what rode in, what this host could not give exactly — is a labelled row behind ONE
        // disclosure, with the sentence the agent read as its last row. A start is the most
        // frequent act on a busy timeline and the one whose particulars change least from one
        // to the next; a screen that laid them all out made every start four lines tall.
        // Which facts show is decided from the event's typed fields, never by reading the
        // sentence back: `checkout` only when it is NOT the session's own `…/repos/<owner>/<repo>`
        // every reader can already assume (a log from before the checkouts had one path
        // says `/repos/<owner>/<repo>`, which ends the same way); `forwarding` a reference
        // per connection.
        let sandboxStartFacts (by: ActorRef) (act: Act) (s: WorkSandboxStarted) : TemplateResult list =
            let row (key: string) (fact: string) (value: TemplateResult) =
                html $"""
                    <div class="{Style.actNoteFactRow}" data-act-fact="{fact}">
                      <span class="{Style.actNoteFactKey}">{key}</span>
                      <div class="{Style.actNoteFactVal}">{value}</div>
                    </div>"""
            let describedAs =
                s.Description
                |> Option.map (fun d -> row Dom.Text.sandboxFactFor "description" (html $"""{d}"""))
                |> Option.toList
            let backend = [ row Dom.Text.sandboxFactBackend "backend" (html $"""{s.Backend}""") ]
            let conventional (path: string) =
                match SandboxRef.scope s.Sandbox with
                | RepoOwned repo -> path.TrimEnd('/').EndsWith (sprintf "/repos/%s" (RepoRef.value repo))
                | SessionOwned -> false
            let checkout =
                match s.Checkout with
                | Some path when not (conventional path) ->
                    [ row Dom.Text.sandboxFactCheckout "checkout" (html $"""<code class="{Style.actNotePath}">{path}</code>""") ]
                | _ -> []
            // Each connection drawn as a REFERENCE — the same mark and name the sidebar's
            // panel and every other sentence give it — rather than a badge of its own that
            // happened to carry the same word.
            let forwarded =
                match s.Forwarded with
                | [] -> []
                | names ->
                    let references =
                        names
                        |> List.map (fun name ->
                            html $"""<span data-act-fact="forwarded">{Entity.render model by (EntityRef.Connection name)}</span>""")
                    [ html $"""
                        <div class="{Style.actNoteFactRow}">
                          <span class="{Style.actNoteFactKey}">{Dom.Text.sandboxFactForwarding}</span>
                          <div class="{Style.actNoteFactVal}">{references}</div>
                        </div>""" ]
            let realisation =
                match s.Realisation with
                | [] -> []
                | lines ->
                    let vals =
                        lines
                        |> List.map (fun line ->
                            html $"""<span class="{Style.actNoteRealisation}" data-act-fact="realisation">{line}</span>""")
                    [ html $"""
                        <div class="{Style.actNoteFactRow}">
                          <span class="{Style.actNoteFactKey}">{Dom.Text.sandboxFactAdjusted}</span>
                          <div class="{Style.actNoteFactStack}">{vals}</div>
                        </div>""" ]
            List.concat [ describedAs; backend; checkout; forwarded; realisation; [ toldRow act ] ]
        // What a repo asked for, laid out the way a start is: the count on the line and,
        // behind ONE disclosure, every grant as its own fact — the semicolon chain the agent
        // reads was a paragraph of notation printed under the headline, which is not how a
        // person reads down a list they are deciding about. The sentence stays as the last
        // row. An empty ask has no grants to lay out and shows only that.
        let capabilityFacts (act: Act) (c: Repos.RepoCapabilitiesChanged) : TemplateResult list =
            let grants =
                match c.Granted with
                | [] -> []
                | granted ->
                    let vals =
                        granted
                        |> List.map (fun grant ->
                            html $"""<code class="{Style.actNotePath}" data-act-fact="grant">{grant}</code>""")
                    [ html $"""
                        <div class="{Style.actNoteFactRow}">
                          <span class="{Style.actNoteFactKey}">{Dom.Text.capabilityFactGrants}</span>
                          <div class="{Style.actNoteFactStack}">{vals}</div>
                        </div>""" ]
            grants @ [ toldRow act ]
        // A file change (the file verbs), laid out the way a start is: the title on the line
        // — which file, how much — and behind ONE disclosure the change itself, one row per
        // `-`/`+` line, with the sentence the agent read as its last row. The edit a reader
        // can SEE, where the same change used to be a heredoc in a terminal a reader had to
        // parse. A write carries no diff and reads as its phrase like any other act.
        let fileChangeFacts (act: Act) (diff: string) : TemplateResult list =
            let lines =
                diff.Split '\n'
                |> List.ofArray
                |> List.map (fun line ->
                    let tone =
                        if line.StartsWith "+" then Style.actNoteDiffAdd
                        elif line.StartsWith "-" then Style.actNoteDiffDel
                        else Style.actNoteDiffNote
                    html $"""<span class="{tone}">{line}</span>""")
            [ html $"""<pre class="{Style.actNoteDiff}" data-act-fact="diff">{lines}</pre>"""
              toldRow act ]
        // THE fold: the one control every disclosure on the timeline is — an arrow on the
        // dead centre of the gutter and the title's line, and something unfolding beneath,
        // grown, slid and faded at the page's one pace (`Style.Motion`) and folded back the
        // same way. Its own state per key (`OpenFolds`), so any number can be open at once; a
        // real button with its expanded state said, so a keyboard and a screen reader get
        // what a pointer gets. The container is the caller's — an act's article, a run's
        // block — and owes the gutter (`pl-[32px]`) and `relative` the arrow is placed in.
        // One pair of hooks for every fold — `data-fold` on the control and `data-fold-body`
        // on what it opens, each carrying the key, and `data-fold-open` saying which way it
        // is — because a test that walks one fold walks them all.
        //
        // The button itself carries no `@click` — `foldClickRow` below is where the toggle
        // is dispatched, once, on the row a caller wraps it into. A press ON the arrow still
        // works: a native `click` fires there and bubbles to that one listener the same as a
        // press anywhere else on the line does. A caller that forgot to wrap it would get an
        // inert arrow rather than a silently-fine one, which is the point — there is no
        // second path to the toggle to fall back to.
        let foldArrow (key: FoldKey) (mark: TemplateResult) (label: string) =
            let opened = Set.contains key model.OpenFolds
            html $"""
                <button type="button" class="{Style.fold}"
                        aria-expanded="{if opened then "true" else "false"}" aria-controls="fold-{FoldKey.value key}"
                        aria-label="{label}" data-fold="{FoldKey.value key}">
                  <span class="{if opened then Style.foldMarkOpen else Style.foldMark}">{mark}</span>
                </button>"""
        // The row the arrow rides in, made ONE control: a press anywhere on it toggles the
        // same fold the arrow alone used to — the arrow is a square a thumb can miss, the
        // title beside it is most of the line, and the design already agreed they were one
        // thing (`foldArrow`'s doc). `display:contents` (`Style.foldClick`) is why the grid
        // (`Style.foldRow`) still lands the arrow and the title on their own columns: the
        // wrapper does not lay out, it only carries the one listener.
        //
        // `wanted` is the caller's escape hatch for the one case a row can hold something
        // that must keep its OWN click: an act's title can carry a real `<a>` (an entity
        // reference), and a link inside a button is invalid — this stays a `<div>` for
        // exactly that reason. Everywhere else passes `alwaysToggle`.
        let alwaysToggle (_: Browser.Types.Event) = true
        let notOnLink (e: Browser.Types.Event) =
            match EventTargets.asElement e.target |> Option.bind (fun target -> target.closest "a") with
            | Some _ -> false
            | None -> true
        let foldClickRow (key: FoldKey) (wanted: Browser.Types.Event -> bool) (children: TemplateResult list) =
            html $"""
                <div class="{Style.foldClick}" data-fold-row="{FoldKey.value key}"
                     @click={Ev(fun (e: Browser.Types.Event) -> if wanted e then dispatch (ToggleFoldMsg key))}>
                  {children}
                </div>"""
        // What unfolds sits UNDER the title, in the content column — an act's particulars, a
        // call's input and output. `foldBodyWide` is the exception that earns itself: a run's
        // items are fold rows of their own, and spanning both columns is what puts their
        // arrows on this row's rail rather than one gutter in.
        let foldBodyIn (width: bool -> string) (key: FoldKey) (inner: string) (body: TemplateResult list) =
            let opened = Set.contains key model.OpenFolds
            html $"""
                <div id="fold-{FoldKey.value key}" class="{width opened}"
                     data-fold-body="{FoldKey.value key}" data-fold-open="{if opened then "yes" else "no"}">
                  <div class="{inner}">{if opened then body else []}</div>
                </div>"""
        let foldBody = foldBodyIn (fun opened -> if opened then Style.foldBodyOpen else Style.foldBodyShut)
        let foldBodyWide = foldBodyIn (fun opened -> if opened then Style.foldBodyWideOpen else Style.foldBodyWideShut)
        // A repo note is something someone DID, not said - one quiet line, actor-attributed,
        // no avatar and no rich body (Plan 14, repos). It rides the same timeline slot a
        // message does (both are `ConversationItem`s at an offset); `Content` is what tells
        // the two apart at render time. The headline is the act's phrase, drawn segment by
        // segment: the same words the agent reads, with each thing they name drawn as it is
        // drawn everywhere else on this screen — except where a screen has its own layout for
        // the act, and then the title is the screen's.
        // Why an act happened, when that was not its author's own idea (`CausedBy`): the repo
        // added, the session starting, a person connecting. One quiet line ABOVE the headline,
        // its mark in the gutter turning down into the act, said as a sentence with its
        // references drawn. A run of acts with one cause says it once: each act after the
        // first carries only a down mark, so the run reads as one chain
        // (`ConversationItem.causeLinks`). A cause still in the loaded conversation gets a
        // jump — the same `RevealMessage` the chapter rail and a reply's ref use — on the
        // MARK, because the sentence holds links and a link cannot sit inside a button.
        // `data-cause-ref` / `data-cause-chain` are the hooks a test finds them by.
        let causeLinks = ConversationItem.causeLinks model.Conversation.Items
        // The acts a chain continues below: each draws the rail from its chevron down to the
        // next link's mark, so the chain is one line rather than marks spaced along a gutter.
        let chainedOn =
            model.Conversation.Items
            |> List.pairwise
            |> List.choose (fun (above, item) ->
                match Map.tryFind item.MessageId causeLinks with
                | Some CauseLink.Chained -> Some above.MessageId
                | _ -> None)
            |> Set.ofList
        let causeLine (item: ConversationItem) =
            let line (mark: TemplateResult) (said: TemplateResult list) (hook: string) =
                html $"""
                    <div class="{Style.causeRow}" data-cause-ref="{hook}">
                      <span class="{Style.causeMark}">{mark}</span>
                      <span class="{Style.cls [ Style.foldContent; Style.causeSaid ]}"><span class="{Style.srOnly}">{Dom.Text.causedBy}</span><span class="{Style.replyRefQuote}">{said}</span></span>
                    </div>"""
            // The corner, drawn as the chain is: one-pixel boxes for the turn and the stem, so
            // they sit on the gutter's centre line exactly, and the same head at the foot.
            let corner =
                html $"""<span class="{Style.causeCornerTurn}"></span><span class="{Style.causeCornerStem}"></span><span class="{Style.causeChainHead}">{Icon.chained}</span>"""
            let still = html $"""<span class="{Style.causeCorner}" aria-hidden="true">{corner}</span>"""
            let drawn cause =
                match cause with
                | Cause.Item target ->
                    match model.Conversation.Items |> List.tryFind (fun i -> i.MessageId = target) with
                    | Some cause ->
                        let said =
                            match cause.Content with
                            // A boot is nobody's act: the session's own sentence, not the
                            // process's name in front of a phrase.
                            | ItemContent.Act (Act.SessionStarted _) -> [ html $"""{Dom.Text.causeBooted}""" ]
                            | ItemContent.Act (Act.SessionResumed _) -> [ html $"""{Dom.Text.causeResumed}""" ]
                            | ItemContent.Act act ->
                                Entity.phrase model cause.Author (Segment.Ref (EntityRef.Actor cause.Author) :: Segment.Text " " :: Act.phrase act)
                            | _ -> [ html $"""{ConversationItem.said cause}""" ]
                        let jump =
                            html $"""<button type="button" class="{Style.cls [ Style.causeCorner; Style.causeJump ]}" data-cause-jump aria-label="{Dom.Text.causeJumpLabel}" @click={Ev(fun _ -> dispatch (MoveMsg (DomMove.RevealMessage target)))}>{corner}</button>"""
                        // The session starting is not drawn, so there is nothing to jump to.
                        let mark =
                            match cause.Content with
                            | ItemContent.Act (Act.SessionStarted _) -> still
                            | _ -> jump
                        line mark said (MessageId.value target)
                    | None -> line still [ html $"""{Dom.Text.causeMissing}""" ] (MessageId.value target)
                | Cause.Booted -> line still [ html $"""{Dom.Text.causeBooted}""" ] "booted"
                | Cause.Connected principal ->
                    line
                        still
                        (Entity.phrase
                            model
                            item.Author
                            [ Segment.Ref (EntityRef.Actor (Principal.toActor principal)); Segment.Text (" " + Dom.Text.causeConnected) ])
                        "connected"
            match Map.tryFind item.MessageId causeLinks with
            | Some (CauseLink.Drawn cause) -> drawn cause
            | Some CauseLink.Chained ->
                html $"""
                    <div class="{Style.causeRow}" data-cause-chain>
                      <span class="{Style.causeChainMark}" aria-hidden="true"><span class="{Style.causeChainBody}"></span><span class="{Style.causeChainHead}">{Icon.chained}</span></span>
                      <span class="{Style.srOnly}">{Dom.Text.causeChained}</span>
                    </div>"""
            | Some CauseLink.Unlinked
            | None -> Lit.nothing
        let actNoteItem (act: Act) (item: ConversationItem) =
            let by = item.Author
            // A slow act coming up is marked in the LEFT gutter — the agent's diamond when it
            // is the agent's act and a circle when it is anybody else's (the diamond is the
            // agent's alone), on the margin rather than trailing the line, so the running
            // ones read as a column down the edge. A failed act still says so inline, where its reason sits: a terminal
            // state wants a word, not a dot. A settled act says nothing here — its body is the
            // whole account. `data-act-status` on the article is the stable hook a test counts
            // running work by, wherever the design puts the cue.
            let running =
                match item.Status with
                | ConversationItemStatus.Running ->
                    // Who is doing it, as the hook says it too: whose mark this is is the
                    // promise, whatever the marks look like.
                    let mark =
                        match by with
                        | ActorRef.Agent ->
                            html $"""<span class="{Style.actNoteRunningAgent}" style="{Style.thinkFrom (MessageId.value item.MessageId)}" data-act-running="agent">{Icon.cube}</span>"""
                        | PeerRef _ | UserRef _ | ActorRef.Session | ActorRef.System | ActorRef.Configured _ ->
                            html $"""<span class="{Style.actNoteRunningOther}" data-act-running="other"></span>"""
                    html $"""<span class="{Style.actNoteRunning}">{mark}<span class="{Style.srOnly}">{Dom.Text.running}</span></span>"""
                | Complete | Streaming | ConversationItemStatus.Failed -> Lit.nothing
            let failedMark =
                match item.Status with
                | ConversationItemStatus.Failed ->
                    html $"""<span class="{Style.statusErr}">{Icon.crossSm} {Dom.Text.failed}</span>"""
                | Complete | Streaming | ConversationItemStatus.Running -> Lit.nothing
            // The title, what shows beneath it, and what FOLDS beneath that. A screen lays
            // out the acts it can — a sandbox coming up or up, a file changed — with a title
            // of its own and every fact in the fold; the rest read as their phrase with their
            // particulars visible, and only the sentence the agent got in the fold.
            let title, shown, folded =
                match act with
                | Act.SandboxStarted s ->
                    Phrase.deed "started" [ Segment.Text "sandbox "; Segment.Ref (EntityRef.Sandbox s.Sandbox) ], [], sandboxStartFacts by act s
                | Act.SandboxStarting s ->
                    Phrase.deed "starting" [ Segment.Text "sandbox "; Segment.Ref (EntityRef.Sandbox s.Sandbox) ], [], [ toldRow act ]
                | Act.FileChanged { FileChanged.Diff = Some diff } -> Act.deed act, [], fileChangeFacts act diff
                | Act.RepoCapabilitiesChanged c -> Act.deed act, [], capabilityFacts act c
                // One answer is on the title ("responded main"). Several are a list in the
                // fold, as the person saw them, above what the agent was told.
                | Act.SurfaceActionTaken a ->
                    let answers =
                        match a.Responses with
                        | []
                        | [ _ ] -> []
                        | many ->
                            let rows = many |> List.map (fun r -> html $"""<li class="{Style.actNoteDetail}">{r}</li>""")
                            [ html $"""<ul class="{Style.actNoteShown}" data-surface-responses>{rows}</ul>""" ]
                    Act.deed act, [], answers @ [ toldRow act ]
                | _ -> Act.deed act, actNoteParticulars by act, [ toldRow act ]
            // The fold (`foldArrow`/`foldBody`): the particulars under the title, behind the
            // arrow on the gutter. While the act is still RUNNING the gutter holds the mark
            // instead: an act in flight is not one to unfold, and its account is about to
            // change under the reader anyway.
            let key = FoldKey.Act item.MessageId
            // A cause row above the headline moves the actions down onto the headline.
            let underCause =
                match Map.tryFind item.MessageId causeLinks with
                | Some (CauseLink.Drawn _) | Some CauseLink.Chained -> true
                | Some CauseLink.Unlinked | None -> false
            let rail =
                if Set.contains item.MessageId chainedOn then
                    html $"""<span class="{Style.causeRail}" aria-hidden="true"></span>"""
                else
                    Lit.nothing
            // Who the act was for, after the deed: "started sandbox dev for Ada". The author
            // alone would name a repo's file or the agent and stop there. Its own box, so a
            // narrow screen puts it on its own line rather than wrapping mid-clause — and
            // nothing at all when the cause already names that person.
            let whom =
                match ConversationItem.forWhom model.Conversation.Items item with
                | [] -> Lit.nothing
                | clause -> html $"""<span class="{Style.actNoteFor}" data-act-for>{Entity.phrase model by clause}</span>"""
            let titleSpan =
                html $"""<span class="{Style.cls [ Style.foldContent; Style.actNoteText; Style.foldClickable ]}">{Entity.phrase model by title}{whom} {failedMark}</span>"""
            // A press on the title toggles the same fold the arrow does — `notOnLink`
            // because the title itself can hold a real `<a>` (an entity reference, e.g. the
            // repo a `RepoAdded` names): that press means "go there", not "fold this up",
            // and a `<div>` rather than a `<button>` here is exactly what lets the two
            // coexist (a link cannot sit inside a button).
            let header =
                match item.Status with
                | ConversationItemStatus.Running -> html $"""{running}{titleSpan}"""
                | Complete | Streaming | ConversationItemStatus.Failed ->
                    foldClickRow key notOnLink [ foldArrow key Icon.right Dom.Text.details; titleSpan ]
            let fold = foldBody key Style.actNoteFoldInner folded
            html $"""
                <article class="{Style.actNote}" data-message-id="{MessageId.value item.MessageId}" tabindex="-1" data-act-note data-act-status="{messageStatusLabel item.Status}" data-message-author="{Entity.actorToken item.Author}">
                  {itemActionsAt underCause item}
                  {causeLine item}
                  {header}
                  {rail}
                  <div class="{Style.cls [ Style.foldContent; Style.actNoteShown ]}">{shown}</div>
                  {fold}
                </article>"""
        // Where a turn stopped, and why. A signpost on the chip column, after the last thing
        // the turn did, in the machine's voice: the stop mark where a chip's `$` stands, the
        // reason beside it. Not a message — nobody said this — so no rich body, no reply, no
        // actions; and not an act — nobody did it — so no fold and no attribution of its own
        // beyond the author line of the turn it ends. `data-turn-stopped` is the hook a
        // test reads a stop by, wherever the design puts the mark.
        //
        // The mark's colour is the one state it carries: the error red for a turn the process
        // could not carry on, the faint ink for one a person stopped — a hand on the stop is
        // not a fault. The hook's value says which, for a test that cannot see colour.
        let stoppedItem (stop: TurnStop) (item: ConversationItem) =
            let mark, how =
                match stop with
                | TurnStop.Failed _ -> Style.turnStopMarkFailed, Dom.Text.failed
                | TurnStop.Interrupted _ -> Style.turnStopMarkInterrupted, Dom.Text.interrupted
            html $"""
                <article class="{Style.turnStop}" data-message-id="{MessageId.value item.MessageId}" tabindex="-1" data-turn-stopped="{how}" data-message-author="{Entity.actorToken item.Author}">
                  <span class="{mark}">{Icon.stopSm}<span class="{Style.srOnly}">{Dom.Text.turnStopped}</span></span>
                  <span class="{Style.turnStopText}">{Entity.phrase model item.Author (TurnStop.phrase stop)}</span>
                </article>"""
        let messageItem (item: ConversationItem) =
            // What was said. An act never reaches here (`actNoteItem` takes those), and its
            // sentence would be the wrong thing to render as markdown if one did.
            let body = ConversationItem.said item
            let isAgent = (item.Author = ActorRef.Agent)
            // Why this turn exists, when nobody asked for it (Plan 20, stage 2). On the meta
            // line rather than in the body: it is attribution, and the body is what the agent
            // said — a sentence the agent did not say does not go in it.
            let wokeInner =
                match item.Woke with
                | None -> Lit.nothing
                | Some reason ->
                    // One word on screen whatever the reason — the meta line is three short
                    // words wide, and a turn that ran unasked says the same thing about
                    // itself however it came to. WHICH reason lives in the hook a test reads
                    // and the title a person can ask for.
                    let token, title =
                        match reason with
                        | CommandFinished -> Dom.Text.wokeCommandFinished, Dom.Text.turnWokeCommandFinished
                        | StreamEnded _ -> Dom.Text.wokeStreamEnded, Dom.Text.turnWokeStreamEnded
                        | IntegrationLost _ -> Dom.Text.wokeIntegrationLost, Dom.Text.turnWokeIntegrationLost
                        | PrChanged _ -> Dom.Text.wokePrChanged, Dom.Text.turnWokePrChanged
                        | CutOff _ -> Dom.Text.wokeCutOff, Dom.Text.turnWokeCutOff
                        | SurfaceAnswered _ -> Dom.Text.wokeSurfaceAnswered, Dom.Text.turnWokeSurfaceAnswered
                    html $"""<span class="{Style.statusFaint}" data-message-woke="{token}" title="{title}">{Dom.Text.turnWoke}</span>"""
            // A message still arriving says so with the CARET at the end of its body, and with
            // nothing else. `streaming` used to be a word on this line as well — one line
            // above that caret, and a few centimetres above a strip that said it a third
            // time. Of the three, the caret is the only one carrying something the others
            // cannot: WHERE the next word lands. So the word goes and the mark stays.
            let statusInner =
                match item.Status with
                // `Running` is an act's status, never a message's — a message arriving is
                // `Streaming`. It cannot reach this renderer (acts draw through `actNoteItem`),
                // but the match is total, so it reads with the other quiet cases.
                | Complete | Streaming | ConversationItemStatus.Running -> Lit.nothing
                | ConversationItemStatus.Failed -> html $"""<span class="{Style.statusErr}">failed</span>"""
            let bodyClass, caret =
                match item.Status with
                | Streaming ->
                    // The one visible statement that a turn is in flight, and the hook that
                    // says so is what a test counts: there must never be a second. Turning
                    // while it thinks — nothing said yet, or what it said has gone quiet
                    // (`ClientModel.agentThinking`) — and still while the words are arriving.
                    // Each turn's cube starts on a move of its own, seeded by its message.
                    // Before the first word it stands larger where the reply will begin, and
                    // the first word's caret glides in from there (`Glide`).
                    let turn = MessageId.value item.MessageId
                    let mark =
                        if not (ClientModel.agentThinking model item) then
                            html $"""<span class="{Style.agentCaret}" data-agent-writing {Glide.into turn}></span>"""
                        elif System.String.IsNullOrWhiteSpace body then
                            html $"""<span class="{Style.agentThinkingStart}" style="{Style.thinkFrom turn}" data-agent-writing {Glide.from turn}>{Icon.cube}</span>"""
                        else
                            html $"""<span class="{Style.agentThinking}" style="{Style.thinkFrom turn}" data-agent-writing>{Icon.cube}</span>"""
                    Style.messageBodyStreaming, mark
                | _ -> Style.messageBody, Lit.nothing
            let bodyClass = Style.cls [ bodyClass; Style.messageVoice isAgent ]
            // The author line is the GROUP's to say (see `group` below); a message's own meta
            // line exists only while it has news of its own — failed, woken unasked. How
            // the TURN ended is not the message's news: that is the stop signpost's
            // (`stoppedItem`), where the turn ended. Not streaming: that is the caret's, and a line that appeared to say it
            // and then vanished would move the body under the reader mid-sentence.
            let hasStatusNews =
                match item.Status with
                | Complete | Streaming | ConversationItemStatus.Running -> false
                | ConversationItemStatus.Failed -> true
            let meta =
                if item.Woke.IsSome || hasStatusNews then
                    html $"""<div class="{Style.messageMeta}">{wokeInner}{statusInner}</div>"""
                else Lit.nothing
            // A ref to what this reply answers, drawn ONLY when the projection judged it worth
            // drawing (`CausedBy = Some`, the detached case). A quiet quoted line above the
            // body — the parent's own words, truncated to one line, plain not rich, so it
            // reads as the context it is and cannot grow taller than the message it heads.
            //
            // It jumps to its source through the SAME `RevealMessage` the chapter rail uses —
            // scroll it to the middle, flash it, put the cursor on it — but only when the
            // source is on hand to jump to. A target still in the loaded conversation is a
            // button; one paged off (the quote falls back to a bare label) is inert, because a
            // control that scrolls to nothing is worse than a line that never offered to.
            let replyRef =
                match item.CausedBy with
                // A message is caused by another item or by nothing; what brings a sandbox
                // up is an act's to say (`causeLine`).
                | None
                | Some Cause.Booted
                | Some (Cause.Connected _) -> Lit.nothing
                | Some (Cause.Item target) ->
                    match model.Conversation.Items |> List.tryFind (fun i -> i.MessageId = target) with
                    | Some parent ->
                        // In the PARENT'S voice, because the typeface says who is talking
                        // before a reader reaches the author line (docs/visual-design.md,
                        // Type) — and what is quoted here is whatever they said, in a group
                        // headed by whoever is replying. With no voice composed it inherited
                        // the chrome's sans, which put a person's quoted words in the
                        // machine's face inside the agent's own run: the one thing that rule
                        // forbids.
                        let quote = Style.cls [ Style.replyRefQuote; Style.messageVoice (parent.Author = ActorRef.Agent) ]
                        html $"""
                            <button type="button" class="{Style.replyRefJump}" data-reply-ref="{MessageId.value target}" data-reply-jump
                                    aria-label="{Dom.Text.replyRefJumpLabel}"
                                    @click={Ev(fun _ -> dispatch (MoveMsg (DomMove.RevealMessage target)))}>
                              <span class="{Style.replyRefMark}">{Icon.replySm}</span>
                              <span class="{quote}">{ConversationItem.said parent}</span>
                            </button>"""
                    | None ->
                        html $"""
                            <div class="{Style.replyRef}" data-reply-ref="{MessageId.value target}" aria-label="{Dom.Text.replyRefLabel}">
                              <span class="{Style.replyRefMark}">{Icon.replySm}</span>
                              <span class="{Style.replyRefQuote}">{Dom.Text.replyRefMissing}</span>
                            </div>"""
            html $"""
                <article class="{Style.messageItem}" data-message-id="{MessageId.value item.MessageId}" tabindex="-1" data-message-author="{Entity.actorToken item.Author}" data-message-status="{messageStatusLabel item.Status}">
                  {itemActions item}
                  {meta}
                  {replyRef}
                  <div class="{bodyClass}" data-message-body>{RichText.renderTrailed (contentChip item.Author) caret body}</div>
                </article>"""
        // One line: who ran what, and how it went. No output — a tail inline would make the
        // chat noisiest exactly when it is busiest, and would put everything a command
        // printed one glance from anyone in the session rather than one tap.
        let blockOf (terminalId: TerminalId) (blockId: BlockId) =
            Projection.tryFind terminalId model.Terminals
            |> Option.bind (fun view -> view.Blocks |> List.tryFind (fun b -> b.BlockId = blockId))
        // No author of its own: WHO ran it is the group's author line above it (`group`
        // below), the same answer a message gets. Takes the block already RESOLVED, because
        // the grouping fold needs the block's authority before it can place the chip — a
        // chip whose block a page boundary withheld never becomes an entry at all.
        //
        // It reads as an act does — "ran" in the act's voice, then what was run as the thing
        // named, the way "started sandbox dev" does: the description its author gave, or for a
        // person's command, the command's meaningful part in mono. Which terminal it ran in is
        // in the accessible name and one tap away, not on the line.
        let blockChip (chip: string) (terminalId: TerminalId) (block: Block) =
            let blockId = block.BlockId
            let status = terminalBlockTokenIn model terminalId block
            let where = Entity.terminalName model terminalId |> Option.defaultValue (TerminalId.value terminalId)
            let what =
                match block.Description |> Option.bind BlockDescription.ofProse with
                | Some description -> html $"""<span class="{Style.chatChipName}">{description}</span>"""
                | None -> html $"""<code class="{Style.chatChipCommand}">{BlockLabel.ofCommand block.Command}</code>"""
            let verb = match block.Status with BlockRunning -> Dom.Text.running | _ -> Dom.Text.ran
            html $"""
                <button type="button" class="{chip}"
                        data-chat-block="{BlockId.value blockId}"
                        data-chat-block-status="{status}"
                        data-terminal-id="{TerminalId.value terminalId}"
                        aria-label="{Dom.Text.commandChip (BlockLabel.ofBlock block) (terminalBlockStatusWord model block.Status) where}"
                        @click={Ev(fun _ -> dispatch (OpenPreviewMsg (Preview.ofSubject (PreviewSubject.Block (terminalId, blockId)))))}>
                  <span class="{Style.chatChipLine}" title="{block.Command}">{Entity.verb verb} {what}</span>
                  <span class="shrink-0">{terminalBlockStatusAt model terminalId block}</span>
                </button>"""
        // A command the agent's call asked for, before its block exists — or when it never
        // will. The SAME chip the block will be, in the same place (`TimelineProjection`'s
        // `claim` puts the block there): "running", then what the call said the command is
        // for, or the command in mono. Only the verb and the mark differ, so the swap reads
        // as one chip changing state rather than one thing replaced by another.
        //
        // "running" here, on purpose: nothing at this stage has finished, whatever the
        // mark beside it says (`CommandDidNotStart` still gets this verb — it is the chip's
        // starting word, not a claim that it is under way). The block chip picks up "ran"
        // the moment its status leaves `BlockRunning`.
        //
        // Queued, it is the queue entry's chip as well: the hold it is under, and a press that
        // opens the terminal it waits in, which is where it can be edited or withdrawn. That
        // entry's own chip at the tail (`pendingChip`) is not drawn beside it.
        let commandCallChip (chip: string) (use': ToolUse) (call: CommandCall) =
            let queued =
                ClientModel.pendingActs model
                |> List.tryFind (fun entry ->
                    entry.ToolUseId = Some use'.ToolUseId && Authority.author entry.Authority = ActorRef.Agent)
            let command, description =
                match ToolUseText.command use' with
                | Some (command, description) -> Some command, description
                | None -> None, None
            let what =
                match description |> Option.bind BlockDescription.ofProse, command with
                | Some description, _ -> html $"""<span class="{Style.chatChipName}">{description}</span>"""
                | None, Some command -> html $"""<code class="{Style.chatChipCommand}">{BlockLabel.ofCommand command}</code>"""
                | None, None -> html $"""<code class="{Style.chatChipCommand}">{ToolUse.label use'}</code>"""
            let statusToken, statusLine =
                match call, queued with
                | CommandDidNotStart _, _ ->
                    Dom.Text.commandNotStarted,
                    html $"""<span class="{Style.statusErr}">{Dom.Text.commandDidNotStart}</span>"""
                | _, Some entry -> pendingStatus model entry
                | CommandStarting, None ->
                    Dom.Text.commandStarting,
                    html $"""<span class="{Style.statusWait}">{Icon.spinLead}{Dom.Text.commandStarting}</span>"""
                // Answered and queued, and the entry is not in this replica's doc: the block
                // that drained it is on its way. Said as the queue says a ready command.
                | CommandWaiting, None ->
                    Dom.Text.queuedReady,
                    html $"""<span class="{Style.statusWait}">{Icon.spinLead}queued</span>"""
            let opens =
                match queued with
                | Some entry -> Some entry.Terminal
                | None -> None
            // Why it did not start, where a pointer finds it: the chip has nothing to open,
            // and the reason is what a reader of a failure wants.
            let title =
                match call with
                | CommandDidNotStart (Some reason) -> reason
                | _ -> command |> Option.defaultValue (ToolUse.label use')
            html $"""
                <button type="button" class="{chip}"
                        data-chat-command-call="{ToolUseId.value use'.ToolUseId}"
                        data-chat-command-call-status="{statusToken}"
                        title="{title}"
                        ?disabled={Option.isNone opens}
                        @click={Ev(fun _ -> opens |> Option.iter (fun terminal -> dispatch (OpenInPaneMsg (Reading terminal))))}>
                  <span class="{Style.chatChipLine}">{Entity.verb Dom.Text.running} {what}</span>
                  <span class="shrink-0">{statusLine}</span>
                </button>"""
        let cardLine (chip: string) =
            function
            | CardLine.Block (terminalId, block) -> blockChip chip terminalId block
            | CardLine.Call (use', call) -> commandCallChip chip use' call
        let stretchItem (stretch: TerminalStretch) =
            let length = durationText (TerminalStretch.duration stretch)
            let where = Entity.terminalName model stretch.TerminalId |> Option.defaultValue stretch.Title
            html $"""
                <button type="button" class="{Style.chatChip}"
                        data-chat-stretch="{TerminalStretch.key stretch}"
                        data-chat-stretch-end="{stretchEndLabel stretch.End}"
                        data-terminal-id="{TerminalId.value stretch.TerminalId}"
                        @click={Ev(fun _ -> dispatch (OpenPreviewMsg (Preview.ofSubject (PreviewSubject.Stretch stretch))))}>
                  <span class="{Style.chatChipText}">typed in {where} for {length}</span>
                  <span class="shrink-0">{stretchEnding Style.statusFaint model stretch.End}</span>
                </button>"""
        // One call the agent made. No pane tab: unlike a block there is nothing recorded to
        // open — but there IS something to read: what the call was given and what it answered.
        // Those used to sit on the line (the arguments, truncated, beside the name) and under
        // it (the answer, behind its own "output" summary); now the line is the tool and how
        // it went, and the call is its own disclosure, one chevron, opening on input and
        // output in one shape. The minted id rides the row, because that is what a deep link
        // will address once there is somewhere for it to land.
        let toolCall (container: string) (title: TemplateResult) (use': ToolUse) =
            let status, rendered =
                match use'.Outcome with
                | None ->
                    Dom.Text.blockRunning,
                    runningDot
                | Some ToolCallOk -> Dom.Text.blockOk, html $"""<span class="{Style.statusDone}">{Icon.checkSm}</span>"""
                | Some (ToolCallFailed reason) -> Dom.Text.blockFailed, html $"""<span class="{Style.statusErr}">{reason}</span>"""
            // `None` is not "no arguments" — it is a foreign tool, whose schema we did not
            // write and therefore cannot trust to have marked its own secrets. Said so, in
            // the input's place, rather than shown as an empty input.
            let input =
                match ToolUseText.arguments use' with
                | Some recorded -> recorded
                | None -> "(arguments not recorded)"
            // The answer, when this chip is the only place to read it (a non-block call of one
            // of our own tools) — the same block the input is, under it.
            let output =
                match use'.Result with
                | Some text when text <> "" ->
                    html $"""
                        <div data-chat-tool-result="{ToolUseId.value use'.ToolUseId}">
                          <span class="{Style.chatToolIoLabel}">{Dom.Text.toolOutput}</span>
                          <pre class="{Style.chatToolIoBody}">{text}</pre>
                        </div>"""
                | _ -> Lit.nothing
            let key = FoldKey.ToolCall use'.ToolUseId
            let io =
                [ html $"""
                    <div data-chat-tool-input="{ToolUseId.value use'.ToolUseId}">
                      <span class="{Style.chatToolIoLabel}">{Dom.Text.toolInput}</span>
                      <pre class="{Style.chatToolIoBody}">{input}</pre>
                    </div>"""
                  output ]
            let arrow = foldArrow key Icon.right Dom.Text.details
            let body = foldBody key Style.chatToolIo io
            let header =
                foldClickRow key alwaysToggle
                    [ arrow
                      html $"""
                        <div class="{Style.cls [ Style.foldContent; Style.chatToolCall; Style.foldClickable ]}">
                          {title}
                          <span class="{Style.chatToolStatus}">{rendered}</span>
                        </div>""" ]
            html $"""
                <div class="{container}"
                     data-chat-tool="{ToolUseId.value use'.ToolUseId}"
                     data-chat-tool-status="{status}">
                  {header}
                  {body}
                </div>"""
        // One call on the rail: "used yession/x", its outcome, and its fold opening straight
        // onto input and output. A lone call was once wrapped in a run of one — a fold over a
        // fold, two presses to reach one thing — and `rows` no longer makes one.
        let named (use': ToolUse) = html $"""<code class="{Style.chatToolName}">{ToolUse.label use'}</code>"""
        let loneCall (use': ToolUse) =
            toolCall Style.chatToolRun (html $"""<span class="{Style.chatToolRunText}">used</span> {named use'}""") use'
        // A turn's WORK, folded to one line on the same gutter every act's arrow sits on —
        // the fold is the one control, wherever it is. The line counts what the run holds by
        // kind, each where it first appeared (`WorkRun.summary`), so a reader watching a
        // turn work sees "used 2 tools" become "used 3 tools, wrote 1 file" with nothing
        // moving. Two chevrons: the mark of several folded here. Inside, the items as they
        // happened — each call a fold of its own, each act the note it always was — on the
        // SAME rail, not a gutter further in: the two chevrons over the one already say
        // which line holds the others, and an indent on top of that was a second way of
        // saying it.
        let workRun (turn: AgentTurnId) (items: TimelineItem list) =
            let entries =
                items
                |> List.choose (fun item ->
                    match item with
                    | TimelineToolUse (_, id) ->
                        TimelineProjection.toolUse id model.Timeline
                        |> Option.map (fun use' -> toolCall Style.chatToolItem (named use') use')
                    | TimelineMessage ({ Content = ItemContent.Act act } as note) -> Some (actNoteItem act note)
                    | _ -> None)
            let key =
                match items with
                | TimelineToolUse (_, id) :: _ -> FoldKey.ToolRun id
                | _ -> FoldKey.ToolRun (ToolUseId.create (AgentTurnId.value turn) |> Result.defaultWith failwith)
            let arrow = foldArrow key Icon.rights Dom.Text.details
            let body = foldBodyWide key Style.chatToolRunInner entries
            let header =
                foldClickRow key alwaysToggle
                    [ arrow
                      html $"""<span class="{Style.cls [ Style.foldContent; Style.chatToolRunText; Style.foldClickable ]}">{WorkRun.summary items}</span>""" ]
            html $"""
                <div class="{Style.chatToolRun}" data-chat-tool-run="{AgentTurnId.value turn}">
                  {header}
                  {body}
                </div>"""
        // One agent burst: the commands one turn ran, in one row (Plan 20, stage 4). The
        // lines ARE block chips — same element, same click, same hooks — so a chip does not
        // change what it is by being grouped, and nothing here has to be kept in step with
        // the ungrouped case.
        //
        // A line is a block, or a command the turn asked for that has no block yet
        // (`commandCallChip`) — still going while it waits, failed if it never started.
        let taskCard (turn: AgentTurnId) (commands: CardLine list) =
            let lines =
                commands
                |> List.map (fun line -> line, CardLine.state line)
                |> TaskCard.ordered
            let tally = TaskCard.tally (lines |> List.map snd)
            // Each count in the glyph and colour its status already wears on a chip, and only
            // when it is non-zero: `0 ✗` prints red where nothing is wrong, which is the one
            // thing this line must never do.
            //
            // And each count says WHICH count it is, in the word the block's own status wears
            // (`terminalBlockStatus`): the mark and the colour are the picture, and a tally
            // read out as "1 3" to somebody who cannot see them says nothing at all
            // (docs/visual-design.md, Deciding: the floor is never traded).
            let count n inner = if n = 0 then Lit.nothing else inner
            let failed =
                count tally.Failed (html $"""<span class="{Style.statusErr}">{Icon.crossSm} {tally.Failed}<span class="{Style.srOnly}"> {Dom.Text.failed}</span></span>""")
            let running =
                count tally.Running (html $"""<span class="{Style.statusOk}">{Icon.spinLead}{tally.Running}<span class="{Style.srOnly}"> {Dom.Text.blockRunning}</span></span>""")
            let done' =
                count tally.Done (html $"""<span class="{Style.statusDone}">{Icon.checkSm} {tally.Done}<span class="{Style.srOnly}"> {Dom.Text.blockOk}</span></span>""")
            let counts =
                html $"""<span class="{Style.chatTaskCounts}">{failed}{running}{done'}</span>"""
            let commands =
                if tally.Commands = 1 then "1 command" else sprintf "%d commands" tally.Commands
            // The fold every other row wears, not a `<details>` of its own — see
            // `Style.chatTaskCard`. Keyed by the turn (`FoldKey.Task`), same as a tool run
            // keys by its first call: one row, one key, any number open at once.
            let key = FoldKey.Task turn
            let arrow = foldArrow key Icon.rights Dom.Text.details
            let body =
                foldBodyWide key Style.chatTaskCardInner
                    (lines |> List.map (fun (line, _) -> cardLine Style.chatChipCarded line))
            let header =
                foldClickRow key alwaysToggle
                    [ arrow
                      html $"""
                        <span class="{Style.cls [ Style.chatTaskSummary; Style.foldClickable ]}">
                          <span class="{Style.chatChipText}">ran {commands}</span>
                          {counts}
                        </span>""" ]
            html $"""
                <div class="{Style.chatTaskCard}" data-chat-task-card="{AgentTurnId.value turn}">
                  {header}
                  {body}
                </div>"""
        // Where a chapter opens: a rule across the column carrying what it is called, above
        // the item it opens at and outside whatever author group that item belongs to — a
        // divider folded into a group would be a line drawn inside somebody's turn rather
        // than across the session.
        //
        // The name is an INPUT at rest, the session title's arrangement: a name that only
        // becomes editable once pressed is a name nobody presses, and the two places this
        // product lets you write on a shared surface should not work two ways. What it diffs
        // against is what the session HOLDS (`Chapters.written`), never the guess on screen,
        // so the first keystroke on a chapter nobody has named writes a name rather than
        // editing one nobody chose.
        // A stretch with nothing running, drawn where it happened. Not an act note: nobody
        // performed it, so it wears no author and no mark, and `entryOf` hands it up with
        // `None` for the author the way a chapter rule does.
        //
        // The label is the only control, and the whole of it: a duration that does not look
        // like a button still has to BE one, so it is a real `<button>` with an accessible
        // name saying what pressing it does — the visible words are "7 hours later", which
        // announces nothing. The tooltip carries the moment even while the label shows the
        // duration, so hovering answers the question without changing anything.
        let sessionBreak (resumed: SessionResumed) (at: System.DateTimeOffset) (item: ConversationItem) =
            let showingMoment = Set.contains item.MessageId model.DatedBreaks
            let elapsed = sprintf "%s later" (Elapsed.inWords (at - resumed.LastHeardAt))
            let moment = Moment.stamp at
            let said, reading, label =
                if showingMoment then moment, Dom.Text.breakMoment, Dom.Text.sessionBreakShowElapsed
                else elapsed, Dom.Text.breakElapsed, Dom.Text.sessionBreakShowMoment
            html $"""
                <div class="{Style.sessionBreak}" data-session-break="{reading}"
                     data-message-id="{MessageId.value item.MessageId}">
                  <span class="{Style.sessionBreakLine}" aria-hidden="true"></span>
                  <button type="button" class="{Style.sessionBreakLabel}"
                          aria-label="{label}" title="{moment}"
                          @click={Ev(fun _ -> dispatch (ToggleBreakTimeMsg item.MessageId))}>{said}</button>
                  <span class="{Style.sessionBreakLine}" aria-hidden="true"></span>
                </div>"""
        let chapterRule (item: ConversationItem) =
            let held = Chapters.written CollabText.ylmish model.Synced.Chapters item
            let named = ClientModel.chapterRuleName model item
            // Only peers whose caret is in THIS chapter's name get a marker here, the way the
            // header takes the title's. A name is a field like any other, and a marker in the
            // wrong one is a collaborator apparently standing somewhere they are not.
            let cursors =
                model.Presence
                |> Map.toList
                |> List.filter (fun (_, p) -> p.Focus |> Option.exists (fun f -> f.Field = ChapterName item.MessageId))
                |> List.map (fun (who, p) -> remoteCursor model who p)
            html $"""
                <div class="{Style.chapterRule}" data-chapter-rule="{MessageId.value item.MessageId}">
                  <span class="{Style.chapterDot}" aria-hidden="true"></span>
                  <input type="text" class="{Style.chapterName}"
                         data-chapter-name="{MessageId.value item.MessageId}"
                         aria-label="{Dom.Text.chapterNameLabel}"
                         placeholder="{Dom.Text.chapterNamePlaceholder}"
                         autocapitalize="off" autocorrect="off" autocomplete="off" spellcheck="false"
                         enterkeyhint="done"
                         value="{named}"
                         .value={named}
                         @input={EvVal(fun v -> dispatch (EditChapterNameMsg (item.MessageId, Ylmish.Text.edit v held)))}
                         @keydown={Ev(Events.key commitOnEnter)}
                         @keyup={Ev(fun e -> actions.ReportFieldSelection (ChapterName item.MessageId) (selectionOf e))}
                         @click={Ev(fun e -> actions.ReportFieldSelection (ChapterName item.MessageId) (selectionOf e))}
                         @select={Ev(fun e -> actions.ReportFieldSelection (ChapterName item.MessageId) (selectionOf e))}
                         @focus={Ev(fun e -> actions.ReportFieldSelection (ChapterName item.MessageId) (selectionOf e))}
                         @blur={Ev(fun _ -> actions.ReportFieldSelection (ChapterName item.MessageId) None)}>
                  {cursors}
                </div>"""
        // What is folded is drawn only once it is worth drawing (`ClientModel.showsFolded`):
        // until then the timeline says it is reading, rather than showing the oldest end of
        // the log a page at a time.
        let showsFolded = ClientModel.showsFolded model
        let rows = if showsFolded then TimelineProjection.rows model.Conversation model.Timeline else []
        // Every row resolved to (whose act it is, its rendering) BEFORE grouping, so a row
        // whose backing state a page boundary withheld contributes no entry — never an empty
        // element, and never an author line standing over nothing. An act note carries
        // its own author, like a block chip or a tool run: consecutive acts by one actor
        // fold under a single author line rather than each repeating whose act it is.
        let entryOf row =
            match row with
                | RowItem (TimelineMessage item) ->
                    match item.Content with
                    // Before the general act rendering, because this act is not drawn as one:
                    // the session being away is a gap in the page, not a line in it.
                    | ItemContent.Act (Act.SessionResumed (resumed, at)) ->
                        Some (None, sessionBreak resumed at item)
                    // Not drawn: the top of a timeline already says the session began. It
                    // stays an item because a first boot's work names it as its cause, and
                    // that cause line is where it is said.
                    | ItemContent.Act (Act.SessionStarted _) -> None
                    | ItemContent.Act act -> Some (Some item.Author, actNoteItem act item)
                    | ItemContent.Message _ -> Some (Some item.Author, messageItem item)
                    | ItemContent.Stopped stop -> Some (Some item.Author, stoppedItem stop item)
                | RowItem (TimelineBlock (_, terminalId, blockId)) ->
                    // Both folds read the same page, so a chip without its block is a page
                    // boundary, not a bug: the next page brings it.
                    blockOf terminalId blockId
                    |> Option.map (fun block ->
                        Some (Authority.author block.Authority), blockChip Style.chatChip terminalId block)
                | RowItem (TimelineStretch stretch) -> Some (Some stretch.Holder, stretchItem stretch)
                // A lone call: `rows` leaves one call as its own row, and it draws as one —
                // as the command it asked for, when it asked for one.
                | RowItem (TimelineToolUse (_, id)) ->
                    TimelineProjection.toolUse id model.Timeline
                    |> Option.map (fun use' ->
                        match ToolUse.commandCall use' with
                        | Some call -> Some ActorRef.Agent, commandCallChip Style.chatChip use' call
                        | None -> Some ActorRef.Agent, loneCall use')
                // Nor a thought: `rows` drops the kind outright, because reasoning was never
                // said to anyone. Here for the same reason as the line above — the filter is
                // a rule the type cannot hold — and this is the line that changes on the day
                // somebody decides a screen should show it.
                | RowItem (TimelineThought _) -> None
                | RowWorkRun (turn, items) -> Some (Some ActorRef.Agent, workRun turn items)
                | RowTaskCard (turn, items) ->
                    let lines =
                        items
                        |> List.choose (function
                            | TimelineBlock (_, terminalId, blockId) ->
                                blockOf terminalId blockId |> Option.map (fun block -> CardLine.Block (terminalId, block))
                            | TimelineToolUse (_, id) ->
                                TimelineProjection.toolUse id model.Timeline
                                |> Option.bind (fun use' ->
                                    ToolUse.commandCall use' |> Option.map (fun call -> CardLine.Call (use', call)))
                            | _ -> None)
                    // A card at a page boundary can be short a line, exactly as a lone chip
                    // can be missing: the next page brings it. Below two it is no longer a
                    // burst, so it draws as the chips it is — never as a card of one.
                    match lines with
                    | [] -> None
                    | [ CardLine.Block (terminalId, block) ] ->
                        Some (Some (Authority.author block.Authority), blockChip Style.chatChip terminalId block)
                    | [ CardLine.Call (use', call) ] -> Some (Some ActorRef.Agent, commandCallChip Style.chatChip use' call)
                    | many -> Some (Some ActorRef.Agent, taskCard turn many)
        // What is about to run, at the tail — after everything that has happened, which is
        // where it happens. `rows` is a fold over EVENTS and a pending act is not one, so it
        // is not a row and cannot be: it joins the timeline as a block when it drains, and
        // this chip is replaced by that block's own in the same place. The two mounts it used
        // to have (a dock above the composer, a card in the terminal) are now the read and
        // the write of it — see `pendingChip`.
        //
        // Not a command an agent's call queued, while that call is on the timeline: the
        // call's own chip already stands for it, in the call's place (`commandCallChip`).
        let pending : (ActorRef option * TemplateResult) list =
            ClientModel.pendingActs model
            |> List.filter (fun entry ->
                match entry.ToolUseId, Authority.author entry.Authority with
                | Some id, ActorRef.Agent -> Option.isNone (TimelineProjection.toolUse id model.Timeline)
                | _ -> true)
            |> List.map (fun entry ->
                Some (Authority.author entry.Authority), pendingChip actions dispatch model entry)
        // The agent's commands a loss refused, said where a person reads the conversation —
        // which is often not the pane. The refused blocks are on the timeline already, one
        // chip each; this is the sentence about them, and the way to the terminal. The
        // terminal's name is the control, never the sentence's subject: a name is often a
        // long command line, and "dotnet fsi … is paused" read as nonsense.
        let refusedRows : (ActorRef option * TemplateResult) list =
            Projection.openTerminals model.Terminals
            |> List.choose (fun view ->
                match view.Lost with
                | None -> None
                | Some lost ->
                    let agents =
                        lost.Refused
                        |> List.filter (fun act -> Authority.author act.Authority = ActorRef.Agent)
                    if List.isEmpty agents then None
                    else
                        let id = TerminalId.value view.TerminalId
                        let where = Entity.terminalName model view.TerminalId |> Option.defaultValue id
                        Some (
                            None,
                            html $"""
                                <article class="{Style.chatPaused}" data-chat-refused="{id}" role="status">
                                  <span class="{Style.terminalPausedTitle}">{Dom.Text.chatRefused agents.Length}</span>
                                  <div class="{Style.terminalChoice}">
                                    <button type="button" class="{Style.bandAct}" data-chat-refused-open="{id}"
                                            aria-label="{Dom.Text.showTerminal}: {where}"
                                            @click={Ev(fun _ -> dispatch (OpenInPaneMsg (Reading view.TerminalId)))}>{Dom.Text.showTerminal}</button>
                                    <span class="{Style.chatChipSubject}" title="{where}">{where}</span>
                                  </div>
                                </article>"""
                        ))
        let entries : (ActorRef option * TemplateResult) list =
            (rows
             |> List.collect (fun row ->
                 let rule =
                     match row with
                     | RowItem (TimelineMessage item) when Set.contains item.MessageId chapterOpenings ->
                         [ None, chapterRule item ]
                     | _ -> []
                 rule @ Option.toList (entryOf row)))
            @ refusedRows
            @ pending
        // Consecutive acts by ONE actor fold under one author line — the avatar and name a
        // message used to repeat per turn, said once where the speaker changes. The line is
        // sticky (`Style.messageGroupHead`), so a run longer than the screen keeps saying
        // whose it is; and it is what makes a lone tool run or command wear the same
        // attribution a message does, instead of a private caps label of its own.
        let group (actor: ActorRef) (members: TemplateResult list) =
            html $"""
                <section class="{Style.messageGroup}" data-message-author="{Entity.actorToken actor}">
                  <header class="{Style.messageGroupHead}">{actorHead model Style.avatar actor}</header>
                  {members}
                </section>"""
        let items =
            let flush acc = function
                | None -> acc
                | Some (actor, members) -> group actor (List.rev members) :: acc
            let step (acc, current) (actor, rendered) =
                match actor, current with
                | Some actor, Some (actor', members) when actor = actor' ->
                    acc, Some (actor', rendered :: members)
                | Some actor, current -> flush acc current, Some (actor, [ rendered ])
                | None, current -> rendered :: flush acc current, None
            let acc, current = entries |> List.fold step ([], None)
            List.rev (flush acc current)
        // A session with nothing in it yet opens on an empty column, and an empty column says
        // nothing about where the conversation starts or that the near-black composer below it
        // is where you type. So the chat carries its OWN idle symbol — a caret standing where
        // the first message will land — exactly as the terminals pane stands an idle `$` in its
        // empty pane. A mark, not a sentence: it is the caret every text field in the world
        // wears, so it reads as "text goes here" without a word of instruction. STILL and not
        // pulsing, which is the other half of the same vocabulary — a message being written
        // pulses (`Style.caretWorking`), and nothing is being written here. It used to blink,
        // and `Style.caretIdle` says why a blink that is `steps(1)` was a mark that half of
        // every photograph of an empty session did not have in it.
        //
        // Keyed on the ROWS, not on the rendered list: the mapping above answers a bare tool
        // use and an empty run with `Lit.nothing`, so a timeline can hold rows and still draw
        // nothing — and "has rows" would then hide the caret on a screen that is blank. What
        // is DRAWN is the other half of the same test, because a pending chip is not a row:
        // a session whose first act is a queued command has nothing folded and something on
        // screen, and the caret would stand over it.
        //
        // `aria-hidden`, because it is a typographic mark rather than content: a reader that
        // cannot see it is told the timeline is empty by the timeline being empty.
        // History this client's own store does not hold (Plan 20), said ONLY while nothing is
        // coming to fill it. The feed repairs a hole by reading from the cursor the replay
        // parked at, so while this client can read, what is missing is arriving — and a line
        // that appeared on every cold open and vanished a round trip later would be the red
        // "history paused" this replaced, one voice quieter. What is left is the case nobody
        // can fix from here: a client that cannot reach its session, holding a conversation
        // that starts in the middle. The gate is the link's own (`SessionLink.cutOff`) — an
        // attempt that FAILED, or reads that gave up — so a client still on its first try says
        // nothing.
        let reasonLine (words: string) =
            html $"""
                <p class="{Style.historyGap}" data-history-gap>
                  <span class="{Style.historyGapText}">{words}</span>
                </p>"""
        let missing =
            match model.LocalCopy with
            | LocalCopy.Kept (Some _) when SessionLink.cutOff model.Link -> Some (reasonLine Dom.Text.historyMissingLocally)
            | LocalCopy.Reading
            | LocalCopy.Kept _
            | LocalCopy.NoneKept
            | LocalCopy.Unkeepable
            | LocalCopy.Unread _ -> None
        // The same case with nothing kept at all: a page that has not read the log through, has
        // no copy of it, and cannot reach the session to read it. Not the idle caret, which says
        // the session is EMPTY — nobody here knows that. A store that has not answered yet says
        // nothing either way; it may yet answer with the conversation.
        let absent =
            match model.LocalCopy, SessionLink.phase model.Link with
            | (LocalCopy.NoneKept | LocalCopy.Unkeepable | LocalCopy.Unread _), Phase.Opening when SessionLink.cutOff model.Link ->
                Some (reasonLine Dom.Text.historyNotOnDevice)
            | (LocalCopy.Reading | LocalCopy.Kept _ | LocalCopy.NoneKept | LocalCopy.Unkeepable | LocalCopy.Unread _), _ -> None
        // This client is fetching the log and has not read it through, so a message may still be
        // a round trip from landing. Shaped like the row that will take its place — an author
        // line: the beating dot where an avatar stands, the words where a name does, on the
        // conversation's rail. When the conversation arrives, the first author line lands in
        // the box this was holding, rather than the screen trading a sentence for it. It was a
        // sentence in the caps voice, its dot pushing it off the rail, on a line of its own.
        //
        // Not the agent's blue caret either (docs/visual-design.md, Colour: blue is never a
        // status) — it is `statusWait` with a beating dot, the vocabulary every wait wears,
        // and the one name catching up has wherever it is said (`Dom.Text.catchingUp`).
        // `role="status"` because it is a state, and the words are visible rather than only
        // `srOnly`: a dot alone reaches nobody who cannot see it beat.
        let reading =
            html $"""
                <section class="{Style.messageGroup}" role="status" data-history-loading>
                  <header class="{Style.messageGroupHead}">
                    <span class="{Style.readingMark}">{Icon.spin}</span>
                    <span class="{Style.statusWait}">{Dom.Text.catchingUp}</span>
                  </header>
                </section>"""
        // Fetching the history, and for long enough to say so. Not while the session is still
        // being reached: nothing is being caught up on then, and the status line is saying what
        // is happening.
        let catchingUp =
            match model.Link with
            | SessionLink.Fetching (_, { Slow = true }, _) -> true
            | SessionLink.Fetching _
            | SessionLink.Connecting _
            | SessionLink.Reconnecting _
            | SessionLink.Stalled _
            | SessionLink.Current
            | SessionLink.Refused _ -> false
        let body =
            match showsFolded, rows, items with
            // Still reading. The line saying so waits as the header's catch-up bar does,
            // until the wait has lasted long enough to be worth saying (`Effort`'s `Slow`):
            // a reload's own store answers in a few hundred milliseconds, and "catching up"
            // over a conversation already on this device, for that long, is a flash of a
            // state nobody is in. Anything of this client's own on the screen regardless — a
            // command it queued, an add the session refused — stays, under it.
            | false, _, _ when catchingUp -> reading :: items
            | false, _, _ -> items
            // Shown, and there is genuinely nothing: the caret only ever means what it has
            // always said, which is why it can stay wordless and decorative. Unless what is
            // known is that history is missing — then the timeline is truncated rather than
            // empty, and the line saying so stands where the caret would have. And a page that
            // has not read the log through knows neither: it says nothing.
            | true, [], [] ->
                match Option.orElse absent missing, SessionLink.phase model.Link with
                | Some line, _ -> [ line ]
                | None, Phase.Opening -> []
                | None, Phase.ReadThrough ->
                    // Hooked like the reading row, and for the same reason: what a mark MEANS
                    // is not readable from the mark, and these two mean opposite things.
                    [ html $"""<div class="{Style.timelineIdle}" data-timeline-empty aria-hidden="true"><span class="{Style.caretIdle}"></span></div>""" ]
            | true, _, _ -> Option.toList missing @ items
        // A press on a content chip, wherever in the timeline it sits — a message body's link or
        // a fold's note — shows the file in the pane instead of leaving the page. Delegated on
        // the conversation rather than bound per chip, because a chip is a pure template that
        // knows nothing about tabs, and there is one timeline however many chips are in it.
        //
        // What it does NOT intercept is as deliberate: a modified or middle click is the
        // reader asking the browser for the file in their own way, and a chip for something
        // the pane cannot draw is a download — the chip's mark already promised which, so
        // letting the `<a>` do its job keeps the two answers the same one.
        let contentOpen (mouse: Browser.Types.MouseEvent) =
            let plain = not (mouse.ctrlKey || mouse.metaKey || mouse.shiftKey || mouse.altKey) && mouse.button = 0.0
            let hooked =
                EventTargets.asElement mouse.target
                |> Option.bind (fun target -> target.closest ("[" + Dom.Hooks.content + "]"))
            match hooked |> Option.filter (fun _ -> plain) with
            | None -> ()
            | Some el ->
                match ContentRef.create (el.getAttribute Dom.Hooks.content) with
                | Error _ -> ()
                | Ok ref ->
                    match ContentName.kind ref with
                    | ContentKind.Download -> ()
                    | ContentKind.Image _ ->
                        mouse.preventDefault ()
                        dispatch (OpenPreviewMsg (Preview.ofSubject (PreviewSubject.Content ref)))
        // `tabindex="-1"`: never a Tab stop, but somewhere for focus to land when the jump
        // control that brought the reader here goes away under their hand (`composerTray`).
        let tail = TailSurface.key TailSurface.Conversation
        html $"""
            <div class="{Style.chatRegion}">
              <section class="{Style.timeline}" data-conversation data-tail="{tail}" tabindex="-1" @click={Ev(Events.mouse contentOpen)}>{body}</section>
            </div>"""

    /// What a block printed, as TEXT — the cheap read of the same bytes the recording holds,
    /// and the one both surfaces that show a block are made of.
    ///
    /// One renderer, because a block opened from the chat must not be a second rendering of a
    /// block, free to drift from the first. What it prints over an EMPTY one is the whole
    /// difference between a command still running, one that printed nothing, and one that
    /// never ran at all — three facts a bare blank would flatten into one.
    ///
    /// Its last `keep` lines, never all of them (`TerminalFeed.shownLines`): the page redraws
    /// a running block per record, so what it draws must not grow with what was printed. The
    /// lines left out are SAID by `above`, which is handed how many there were and how many
    /// are shown and draws what stands over the output and outside it, so a copy of the
    /// output is the output and a reader is not left taking the window for the whole.
    let private terminalBlockOutput
        (keep: int)
        (above: int -> int -> TemplateResult)
        (feed: TerminalFeed)
        (block: Block)
        : TemplateResult =
        let elided, output = BlockGroup.outputOf keep feed block
        let earlier = above elided (TerminalFeed.lineCount output)
        if output <> "" then html $"""{earlier}<div class="{Style.terminalOutput}" data-terminal-output>{ansiText output}</div>"""
        else
            match block.Status with
            | BlockRunning -> html $"""<div class="{Style.terminalOutputEmpty}" data-terminal-output>…</div>"""
            // Not "no output", which reads as a command that ran quietly. Why it did not run
            // is the block's fact, in the shell's words.
            | BlockFinished (CommandNotRun _) ->
                html $"""<div class="{Style.terminalOutputEmpty}" data-terminal-output>did not run</div>"""
            | BlockFinished _
            | BlockEnded _ -> html $"""<div class="{Style.terminalOutputEmpty}" data-terminal-output>no output</div>"""
            // "no output" would be true and useless. A refused command has no output
            // because it never ran, and the reason — when one was given — is the thing
            // the next reader actually wants.
            | BlockRejected (_, reason) ->
                let text = reason |> Option.defaultValue "did not run"
                html $"""<div class="{Style.terminalOutputEmpty}" data-terminal-output>{text}</div>"""

    /// The lines a block's output leaves to the recording, said above what is shown.
    let private outputElidedNote (elided: int) (_shown: int) : TemplateResult =
        if elided = 0 then Lit.nothing
        else
            html $"""<div class="{Style.terminalOutputElided}" data-terminal-output-elided="{string elided}">{Dom.Text.outputElided elided}</div>"""

    /// One block's output in the pane's HISTORY: its last `TerminalFeed.paneLines` lines, and
    /// above them a button that opens the block to all it prints (`TerminalFeed.shownLines`)
    /// in place — and, once open, shuts it again. Whether it is open is the model's
    /// (`FoldKey.Output` in `OpenFolds`), so a render that rebuilds the block rebuilds it the
    /// way the reader left it.
    ///
    /// ONE button in one place for both states, so the element the reader pressed is the one
    /// still there afterwards and focus is not stranded. What stays out when it is open
    /// beyond what a block ever draws is said by the same note the preview uses.
    let private terminalBlockHistoryOutput
        (dispatch: ClientMsg -> unit)
        (model: ClientModel)
        (feed: TerminalFeed)
        (terminal: TerminalId)
        (block: Block)
        : TemplateResult =
        let key = FoldKey.Output (terminal, block.BlockId)
        let opened = Set.contains key model.OpenFolds
        let keep = if opened then TerminalFeed.shownLines else TerminalFeed.paneLines
        let above (elided: int) (shown: int) =
            // Capped (`elided` counts what the cap left) or open past the cap (`shown` does).
            let total = elided + shown
            if total <= TerminalFeed.paneLines then Lit.nothing
            else
                let label =
                    if opened then Dom.Text.showFewerLines TerminalFeed.paneLines
                    elif total > TerminalFeed.shownLines then Dom.Text.showLastLines TerminalFeed.shownLines
                    else Dom.Text.showAllLines total
                let note = if opened then outputElidedNote elided shown else Lit.nothing
                html $"""
                    <button type="button" class="{Style.terminalOutputExpand}" data-terminal-output-expand="{BlockId.value block.BlockId}"
                            aria-expanded="{if opened then "true" else "false"}"
                            @click={Ev(fun _ -> dispatch (ToggleFoldMsg key))}>{label}</button>{note}"""
        terminalBlockOutput keep above feed block

    /// Where a player mounts: an empty host the browser shell attaches one to, keyed by what
    /// it plays — a terminal's tab key, or a preview's subject key (Plan 13, stage 3e; Plan 14,
    /// stage 4; `ClientModel.replayFor` reads it back).
    ///
    /// One function for every mount in the pane — a terminal's, a block's, a stretch's, a
    /// rewound terminal's — because they differ in what they play rather than in how they are
    /// mounted, and a mount that forgot its key would be a recording nothing plays while a
    /// mount that forgot its name would be a region no screen reader can announce.
    let private replayMount (label: string) (key: string) : TemplateResult =
        html $"""
            <div class="{Style.paneReadonly}" role="region" aria-label="{label}"
                 data-pane-replay="{key}"></div>"""

    /// One block: the command that ran, then everything it printed.
    ///
    /// `showAuthor` is false for a block drawn INSIDE a `terminalBlockRun` group: the group's
    /// own header already carries the one mark that answers "whose commands are these",
    /// since a group only forms where every block in it shares an author — repeating the
    /// mark on each line inside would be the same fact said once too often.
    let private terminalBlockView
        (dispatch: ClientMsg -> unit)
        (model: ClientModel)
        (feed: TerminalFeed)
        (terminal: TerminalId)
        (showAuthor: bool)
        (block: Block)
        : TemplateResult =
        let body = terminalBlockHistoryOutput dispatch model feed terminal block
        // Whose command this was — yours too, as your messages wear your name in the
        // transcript: a terminal shared with the agent and collaborators is read by whoever is
        // looking, and "the ones without a name are mine" is a rule nobody else can read.
        //
        // Named, not merely MARKED. It was a bare coloured square whose only identification
        // was a `title`: nothing on a phone, which has no hover, and nothing to a screen
        // reader either, because `title` on a non-interactive span is not an accessible name.
        // So a scrollback mixing one person's commands with the agent's and a collaborator's
        // told them apart by hue, and the name was reachable only by opening the facts below.
        // Three colours and no words is a legend a reader has to have been given.
        //
        // In the transcript's own face (`actorHead`): the square and the name in caps, the
        // agent's in blue, so a command and a message by one author read as one author's.
        //
        // On its own line OVER the command (`Style.terminalBlockByline`), with when it ran
        // and how it went at the far end: beside the command, the name pushed a long command
        // into a column a third of a phone's width.
        let author =
            let who = Authority.author block.Authority
            if not showAuthor then Lit.nothing
            else
                html $"""
                    <span class="{Style.terminalBlockAuthor}" data-terminal-block-author="{Entity.actorToken who}"
                          >{actorHead model Style.terminalAuthorSquare who}</span>"""
        // The facts the command line does not already say, as a LINE under it. They were
        // behind a `…` that read as a menu, and opening it said "ran by <you> · exit 1" — the
        // author the line leaves out because it is you, and the exit the mark beside the
        // command already draws. What is left is the one fact no mark carries: why a command
        // that did not exit did not. When the block learns when it started and how long it
        // took, this is where they go.
        //
        // Who stopped it, when somebody did: the exit code says it was interrupted, and only
        // this says by whom — the thing the author of a long build that ended early asks
        // first. A reference, for the reason the author is one.
        let stoppedBy =
            match block.StoppedBy with
            | Some who ->
                html $"""<span class="{Style.terminalBlockFact}" data-terminal-block-stopped-by="{Entity.actorToken who}"
                         >stopped by {Entity.render model who (EntityRef.Actor who)}</span>"""
            | None -> Lit.nothing
        let reason =
            match block.Status with
            | BlockFinished (CommandExecutionFailed reason)
            | BlockFinished (CommandOutcomeUnknown reason)
            | BlockEnded reason ->
                html $"""<span class="{Style.terminalBlockFact}">{reason}</span>"""
            // The shell's own words, which are the whole of why: the line never ran, so there
            // is no output under it to read them in.
            | BlockFinished (CommandNotRun said) ->
                let said = if said = "" then "The shell rejected the line and gave no reason." else said
                html $"""<span class="{Style.terminalBlockFact}" data-terminal-block-not-run>{said}</span>"""
            // Which of the two it was. The mark says "killed: out of memory"; this says
            // whether the sandbox or the machine ran out.
            | BlockFinished (CommandKilledForMemory kill) ->
                html $"""<span class="{Style.terminalBlockFact}" data-terminal-block-memory="{MemoryKill.token kill}">{MemoryKill.cause kill}</span>"""
            | BlockRunning
            | BlockFinished _
            | BlockRejected _ -> Lit.nothing
        let facts =
            match block.Status, block.StoppedBy with
            | BlockFinished (CommandExecutionFailed _), _
            | BlockFinished (CommandNotRun _), _
            | BlockFinished (CommandOutcomeUnknown _), _
            | BlockFinished (CommandKilledForMemory _), _
            | BlockEnded _, _
            | _, Some _ -> html $"""<div class="{Style.terminalBlockFacts}" data-terminal-block-facts>{reason}{stoppedBy}</div>"""
            | _ -> Lit.nothing
        // When it ran, against the page's clock — "4m ago", the moment itself under the
        // pointer. Nothing until the clock has said (`ClientModel.Now`), rather than a count
        // against a guess.
        let ago =
            match model.Now with
            | None -> Lit.nothing
            | Some now ->
                html $"""<span title="{Moment.stamp block.StartedAt}" data-terminal-block-ago>{Elapsed.ago (now - block.StartedAt)}</span>"""
        // The command a preview settled back into carries the preview's name, so the browser
        // moves the one into the other (`ViewActions.Morph`). Only while nothing is previewed:
        // two elements of one name on one page and there is no morph at all.
        let landed =
            match model.Pane with
            | Some (OnTerminal (ReadingAt (t, b))) when t = terminal && b = block.BlockId -> Style.liftedName
            | _ -> ""
        html $"""
            <article class="{Style.terminalBlock}" data-terminal-block="{BlockId.value block.BlockId}" style="{landed}"
                     data-terminal-block-status="{terminalBlockTokenIn model terminal block}">
              <div class="{Style.terminalBlockSummary}" data-terminal-block-command>
                <div class="{Style.terminalBlockByline}" data-terminal-block-byline>
                  {author}
                  <span class="{Style.terminalBlockWhen}">{ago}{terminalBlockStatusAt model terminal block}</span>
                </div>
                <span class="{Style.terminalBlockLine}">
                  <span class="{Style.terminalPrompt}">$</span>
                  <code class="{Style.terminalCommandText}">{block.Command}</code>
                </span>
              </div>
              <div class="{Style.terminalBlockBody}">
                {facts}
                {body}
              </div>
            </article>"""

    /// One fold over a run of commands one actor ran back to back in this terminal — "ran N
    /// earlier commands", with the same ✓/✗/running tally the chat's task card wears,
    /// collapsed to one line until pressed. A native `<details>`, so the fold arrives
    /// keyboard-operable and announced, rather than a second fold mechanism borrowed from the
    /// chat. Its mark is the chevron every other fold in the product turns, not an ellipsis:
    /// `…` at the end of a line reads as a menu, and what is behind this is the commands, not
    /// choices.
    ///
    /// Which runs exist is `BlockGroup.ofBlocks`'s to say; whether one is open is the
    /// model's (`OpenFolds`), bound onto the element and told back by its `toggle`, so a
    /// render that rebuilds the `<details>` rebuilds it the way the reader left it.
    ///
    /// A press on its line goes to the model FIRST rather than letting the `<details>` open
    /// itself: the render that opens it is then the one `Tail` keeps the reader's place across
    /// (from a pointer, what follows the run stays where it was and the earlier commands appear
    /// above it; from the keyboard, the line itself stays and they appear below it). Opened
    /// natively, the history jumped a frame before anything could put it back. Enter and Space
    /// on the line are presses too, and the line keeps the keyboard.
    let private terminalBlockRun
        (dispatch: ClientMsg -> unit)
        (model: ClientModel)
        (feed: TerminalFeed)
        (terminal: TerminalId)
        (leader: Block)
        (rest: Block list)
        : TemplateResult =
        let blocks = leader :: rest
        let key = BlockGroup.key terminal leader
        let opened = Set.contains key model.OpenFolds
        // The element the listener is ON, which is the `<details>`, answered as a bool at once.
        let toggled (e: Browser.Types.Event) =
            match EventTargets.asElement e.currentTarget with
            | Some details ->
                let isOpen = details.hasAttribute "open"
                if isOpen <> opened then dispatch (FoldSetMsg (key, isOpen))
            | None -> ()
        let pressed (e: Browser.Types.Event) =
            e.preventDefault ()
            dispatch (FoldSetMsg (key, not opened))
        let tally = blocks |> List.map (fun b -> TaskCard.stateOf b.Status) |> TaskCard.tally
        let count n inner = if n = 0 then Lit.nothing else inner
        // Each count says which it is, as the chat's own tally does (`taskCard`): the mark
        // and the colour are the picture, and `1 3` read out says nothing.
        let failed = count tally.Failed (html $"""<span class="{Style.statusErr}">{Icon.crossSm} {tally.Failed}<span class="{Style.srOnly}"> {Dom.Text.failed}</span></span>""")
        // No running count: a running command is never in a run (`BlockGroup.ofBlocks`).
        let done' = count tally.Done (html $"""<span class="{Style.statusDone}">{Icon.checkSm} {tally.Done}<span class="{Style.srOnly}"> {Dom.Text.blockOk}</span></span>""")
        let counts = html $"""<span class="{Style.terminalBlockRunCounts}">{failed}{done'}</span>"""
        // EARLIER commands: a run is only ever history older than what the pane draws whole
        // (`BlockGroup.ofBlocks`), and the line says so rather than reading as everything.
        let commands = if tally.Commands = 1 then "1 earlier command" else sprintf "%d earlier commands" tally.Commands
        let runAuthor = Authority.author leader.Authority
        // Whose run it is, in the face every author wears (`actorHead`), yours included.
        let author =
            html $"""
                <span class="{Style.terminalBlockAuthor}" data-terminal-block-author="{Entity.actorToken runAuthor}"
                      >{actorHead model Style.terminalAuthorSquare runAuthor}</span>"""
        html $"""
            <article class="{Style.terminalBlock}" data-terminal-block-run="{BlockId.value leader.BlockId}">
              <details class="group" ?open={opened} @toggle={Ev toggled} data-fold-open="{if opened then "yes" else "no"}">
                <summary class="{Style.terminalBlockRunSummary}" @click={Ev pressed}>
                  {author}
                  <span class="{Style.terminalCommandText}">ran {commands}</span>
                  {counts}
                  <span class="{Style.terminalBlockRunMark}">{Icon.rights}</span>
                </summary>
                <div class="{Style.terminalBlockRunBody}" data-terminal-block-run-body>
                  {blocks |> List.map (terminalBlockView dispatch model feed terminal false)}
                </div>
              </details>
            </article>"""

    /// ONE card for a queued act (Plan 15, stage 3c): what is about to run, editable,
    /// reorderable and withdrawable while it waits its turn.
    ///
    /// The WRITE of a pending act, and the only one. Its read is the chip in the chat
    /// (`pendingChip`), which is where a person finds out that something is queued at all;
    /// this is where they answer it, in the terminal it is queued in, beside the rest of that
    /// terminal's queue and the line they would type the next command on. The card used to
    /// stand in the chat column as well, which put the same three controls in two places and
    /// spent a band of the composer's dock saying what one line says.
    let private pendingCard
        (actions: ViewActions)
        (dispatch: ClientMsg -> unit)
        (model: ClientModel)
        (entry: PendingAct)
        : TemplateResult =
        let id = entry.QueueId
        let statusToken, statusLine = pendingStatus model entry
        // What this act IS, in words — the accessible name on the controls, because a screen
        // reader hearing "Delete" eleven times learns nothing about which one it is on.
        let what = pendingSubject model entry
        // The command line is characters, so it is an input any peer can fix before it runs.
        let body =
            html $"""
                <div class="{Style.terminalQueuedRow}">
                  <span class="{Style.terminalPrompt}">$</span>
                  <input type="text" class="{Style.fieldMonoBare}" aria-label="Queued command"
                         autocapitalize="off" autocorrect="off" autocomplete="off" spellcheck="false"
                         data-terminal-input="{BodyKey.terminalQueued id}">
                </div>"""
        let ordering =
            html $"""
                <button type="button" class="{Style.btnIconBare}" aria-label="Move {what} up" @click={Ev(fun _ -> match TerminalQueueOrder.moveUp model.Synced.Pending id with Some o -> dispatch (ReorderPendingMsg (id, o)) | None -> ())}>{Icon.up}</button>
                <button type="button" class="{Style.btnIconBare}" aria-label="Move {what} down" @click={Ev(fun _ -> match TerminalQueueOrder.moveDown model.Synced.Pending id with Some o -> dispatch (ReorderPendingMsg (id, o)) | None -> ())}>{Icon.down}</button>
                <button type="button" class="{Style.btnIconBareDanger}" aria-label="Delete {what}" data-terminal-queue-delete="{QueueId.value id}" @click={Ev(fun _ -> dispatch (DeletePendingMsg id))}>{Icon.close}</button>"""
        html $"""
            <article class="{Style.terminalQueuedReady}"
                     data-terminal-queued="{QueueId.value id}" data-terminal-queued-status="{statusToken}">
              {body}
              <div class="{Style.terminalQueuedRow}">
                {statusLine}
                <span class="{Style.small}">{Entity.render model (Authority.author entry.Authority) (EntityRef.Actor (Authority.author entry.Authority))}</span>
                <div class="ml-auto flex items-center gap-2">
                  {ordering}
                </div>
              </div>
            </article>"""

    /// A terminal's own pending list: every card for this terminal, in run order. No chip
    /// saying which terminal — the heading above it already does.
    let private terminalQueue (actions: ViewActions) (dispatch: ClientMsg -> unit) (model: ClientModel) (terminal: TerminalId) : TemplateResult list =
        ClientModel.terminalQueue terminal model |> List.map (pendingCard actions dispatch model)

    /// The lease bar (Plan 13, stage 2e): who is typing here, and the one control that
    /// changes it. A banner ABOVE the command lines, never in their place: the session holds
    /// the queue while somebody is live rather than refusing it
    /// (`TerminalQueueDrain.AwaitingTerminal`), so a line written now is precisely what the
    /// hand-back will run, and a watcher with nowhere to write it had lost the one thing they
    /// could still do.
    let private terminalLeaseBar (dispatch: ClientMsg -> unit) (model: ClientModel) (terminal: TerminalId) (holder: ActorRef) : TemplateResult =
        let mine = ClientModel.me model
        // The hook keeps the stable token (a test asserting WHO holds a lease should not have
        // to know what this client happens to have learned about their name); the words get
        // the name, like every other person on screen.
        let label = Entity.actorToken holder
        // Who holds it, said the way the roster says who is here: the square avatar and the
        // name. The pulsing "live" is the state; the button is what changes it; a sentence
        // ("X is using this terminal") restated all three.
        let who = if holder = mine then "you" else Entity.actorName model holder
        // And WHERE, because the bar announces itself (`aria-live`) and "live, nick" heard
        // from a pane of four terminals does not say which one nick has.
        let where = Entity.terminalName model terminal |> Option.defaultValue (TerminalId.value terminal)
        let control =
            if holder = mine then
                html $"""
                    <button type="button" class="{Style.bandActPrimary}" data-terminal-release="{TerminalId.value terminal}"
                            @click={Ev(fun _ -> dispatch (ReleaseTerminalMsg terminal))}>Hand it back</button>"""
            else
                // Any peer may take it, and no permission is asked for: collaborators are
                // trusted, so a steal needs to be VISIBLE rather than authorised — which the
                // event log is, and this button says so plainly.
                html $"""
                    <button type="button" class="{Style.bandAct}" data-terminal-take="{TerminalId.value terminal}"
                            @click={Ev(fun _ -> dispatch (TakeTerminalMsg terminal))}>Take over</button>"""
        html $"""
            <div class="{Style.terminalBandRow}" data-terminal-lease="{label}" aria-live="polite">
              <span class="{Style.paneLive}">{Icon.spinLead}live</span>
              <span class="{Style.cls [ Style.avatarSm; Entity.actorMark model holder ]}"></span>
              <span class="{Style.paneSays}">{who} in {where}</span>
              <div class="ml-auto flex items-center gap-2">{control}</div>
            </div>"""

    /// What the person a terminal's keyboard was taken from sees in place of its lease bar
    /// (`ClientModel.Stolen`): who took it, and the two answers — take it back, or put this
    /// away and leave them to it, after which the lease bar says the same thing as a state.
    ///
    /// An `alert`, because it is an interruption: the reader was typing into a screen that has
    /// just stopped taking their keys. The keyboard itself has already gone to the command
    /// line under this (`ClientModel.keyboardSwap`), where whatever they go on typing is kept
    /// as a line for the hand-back rather than lost — not onto Take back, where the space or
    /// Enter of a word typed in flight would press it and start a tug of war.
    let private terminalStolenNotice (dispatch: ClientMsg -> unit) (model: ClientModel) (terminal: TerminalId) (takenBy: ActorRef) : TemplateResult =
        let id = TerminalId.value terminal
        html $"""
            <div class="{Style.terminalBandRow}" data-terminal-stolen="{id}" role="alert">
              <span class="{Style.cls [ Style.avatarSm; Entity.actorMark model takenBy ]}"></span>
              <span class="{Style.terminalStolenSays}">{Dom.Text.tookTheKeyboard (Entity.actorName model takenBy)}</span>
              <div class="ml-auto flex items-center gap-3">
                <button type="button" class="{Style.bandActPrimary}" data-terminal-take="{id}"
                        @click={Ev(fun _ -> dispatch (TakeTerminalMsg terminal))}>{Dom.Text.takeBack}</button>
                <button type="button" class="{Style.terminalStolenDismiss}" data-terminal-stolen-dismiss="{id}"
                        aria-label="{Dom.Text.dismissStolen}"
                        @click={Ev(fun _ -> dispatch DismissStolenMsg)}>{Icon.close}</button>
              </div>
            </div>"""

    /// The live screen of a terminal in live mode (Plan 14, stage 6).
    ///
    /// A SCREEN, not a stream: the program running here moves the cursor, and what it
    /// displays is a projection of what it emitted. The platform half keeps an emulator —
    /// the same one the Session uses, so the two screens cannot disagree — and hands
    /// this its serialization; here it is rendered through the same ANSI spans a block's
    /// output uses.
    ///
    /// The holder's copy takes keystrokes. Everyone else's is the identical screen, live and
    /// read-only, which is the whole point of a shared terminal: watching is not a lesser
    /// mode, it is the ordinary one.
    let private terminalScreenView (actions: ViewActions) (dispatch: ClientMsg -> unit) (model: ClientModel) (terminal: TerminalId) (holder: ActorRef option) : TemplateResult =
        let id = TerminalId.value terminal
        let tail = TailSurface.key (TailSurface.Screen terminal)
        let body =
            match ClientModel.terminalScreen terminal model with
            | None -> html $"""<div class="{Style.terminalOutputEmpty}">…</div>"""
            | Some screen when screen.Text = "" && Option.isNone screen.Cursor ->
                html $"""<div class="{Style.terminalOutputEmpty}">…</div>"""
            | Some screen -> html $"""{ansiLines (Ansi.parse screen.Text) screen.Cursor}"""
        match holder with
        | None ->
            // Nobody is typing, and a device streams anyway. Read-only for the same reason
            // everyone else's copy is: the keyboard belongs to the lease, and there is no
            // lease to belong to yet.
            html $"""
                <div class="{Style.terminalScreen}" data-terminal-screen="{id}" data-tail="{tail}"
                     role="region" aria-live="off" aria-label="Live terminal, nobody is typing">{body}</div>"""
        | Some holder ->

        if ClientModel.typesInto terminal model then
            // The keys go into a TEXT FIELD inside the screen, on every device (P3-3). It was a
            // `tabindex="0"` div with a keydown handler, which a desktop types into and a phone
            // cannot: a platform raises its soft keyboard only for a focused text field, and a
            // phone keyboard reports few keys anyway — it inserts text. So the field takes both:
            // keydowns through `keystrokeOf` as before (and prevented, so nothing it sends lands
            // in the field), and whatever is inserted without one through `sendInserted`. It is
            // never a value — emptied as it is read — because what is typed here is a byte
            // stream, and a value would fight the program on the other end over what it is.
            //
            // One field and one way in, so nothing has to forward focus to it: the screen is not
            // a Tab stop of its own (a focusable screen handing focus to a child was a trap that
            // Shift-Tab could not leave), and a press on it focuses the field.
            //
            // Invisible but present — `display:none` takes no focus — and sticky at the screen's
            // foot, so focusing it scrolls nothing and a phone pans its keyboard to where the
            // newest line is. 16px, because iOS zooms the page onto a focused field set smaller.
            // Every assist off: autocorrect rewriting `ls` into `Is` is not typing.
            html $"""
                <div class="{Style.terminalScreen}" data-terminal-screen="{id}" data-tail="{tail}"
                     role="region" aria-live="off" aria-label="Live terminal, you are typing here"
                     @click={Ev(keysOnPress dispatch terminal)}>{body}<textarea class="{Style.terminalKeys}" data-terminal-keys="{id}"
                       aria-label="Type into the terminal" rows="1" wrap="off" inputmode="text" enterkeyhint="enter"
                       autocapitalize="off" autocomplete="off" autocorrect="off" spellcheck="false"
                       @keydown={Ev(Events.key (fun key ->
                                       // An IME's keys build a word it has not committed; the word
                                       // arrives whole at `compositionend`.
                                       if not (isComposing key) then
                                           match keystrokeOf key with
                                           | Some data -> actions.TypeIntoTerminal terminal data
                                           | None -> ()))}
                       @input={Ev(fun (e: Browser.Types.Event) ->
                                     if not (isComposingInput e) then
                                         EventTargets.asHTMLTextAreaElement e.currentTarget
                                         |> Option.iter (sendInserted (actions.TypeIntoTerminal terminal)))}
                       @compositionend={Ev(fun (e: Browser.Types.Event) ->
                                              EventTargets.asHTMLTextAreaElement e.currentTarget
                                              |> Option.iter (sendInserted (actions.TypeIntoTerminal terminal)))}
                       ></textarea></div>"""
        else
            html $"""
                <div class="{Style.terminalScreen}" data-terminal-screen="{id}" data-tail="{tail}"
                     role="region" aria-live="off" aria-label="Live terminal, {Entity.actorName model holder} is typing">{body}</div>"""

    /// The terminal composer: your command line, and everyone else's as they type them.
    let private terminalComposer (actions: ViewActions) (dispatch: ClientMsg -> unit) (model: ClientModel) (terminal: TerminalId) : TemplateResult =
        let mine = model.Peer.PeerId
        let lease =
            Projection.tryFind terminal model.Terminals |> Option.bind (fun view -> view.Lease)
        let view = Projection.tryFind terminal model.Terminals
        let editors (author: PeerId) =
            ClientModel.terminalEditorsOf terminal author model
            |> List.map (fun (editor, name) ->
                html $"""
                    <span class="{Style.draftEditorDot}" style="background:{Entity.presenceColour model editor}"
                          title="{name}" data-terminal-draft-editor="{ActorRef.token editor}"></span>""")
        // What a send does NOW, said on the control rather than left for the queue card to
        // explain afterwards. While somebody holds the keyboard the session holds the queue,
        // so the press queues and says for whom it waits. `hasCommand` is untouched: whether
        // there is anything to send is one question, what sending does is another.
        let sendName, sendWhen =
            match lease with
            | Some holder when holder = ClientModel.me model -> Dom.Text.queue, Dom.Text.runsOnHandBack None
            | Some holder -> Dom.Text.queue, Dom.Text.runsOnHandBack (Some (Entity.actorName model holder))
            | None -> Dom.Text.run, ""
        // Someone else mid-command: their live text, read-only here. Watching a collaborator
        // type a command is the same affordance as watching them type a message, which is
        // the whole reason the terminal composer is built out of the message composer's parts.
        let peerDraft (author: PeerId) =
            html $"""
                <div class="{Style.terminalPeerDraft}" style="border-left-color:{Entity.presenceColour model (ActorRef.PeerRef author)}"
                     data-terminal-draft-author="{PeerId.value author}">
                  <span class="{Style.terminalPrompt}">$</span>
                  <input type="text" class="{Style.fieldMonoBare}" readonly aria-label="{ClientModel.nameOf author model}'s command"
                         data-terminal-input="{BodyKey.terminalDraft terminal author}">
                  <span class="{Style.terminalEditors}">{editors author}</span>
                  <button type="button" class="{Style.btnSendInField}" aria-label="{sendName}" title="{sendWhen}"
                          data-terminal-send="{PeerId.value author}"
                          @click={Ev(fun _ -> actions.SendTerminalDraft terminal author)}>{Icon.send}</button>
                </div>"""
        let drafting = ClientModel.terminalDrafts terminal model
        let others = drafting |> List.filter (fun author -> author <> mine)
        // The same fact the message composer's Send reads, asked of the terminal's own slots: a
        // slot is published exactly while its body has content. So Run knows whether it has
        // anything to do without the view measuring the field, and the two cannot disagree.
        let hasCommand = drafting |> List.contains mine
        let runClass = if hasCommand then Style.btnSendInField else Style.btnSendInFieldWaiting
        // In live mode the lease bar stands ABOVE the command lines, a sibling rather than a
        // replacement, so the line you were typing in is the same element before, during and
        // after somebody's turn at the keyboard — focus and caret stay put when a lease starts
        // under them. Run says `Queue` meanwhile (`sendName`), because that is what it does.
        let leaseBar =
            match lease, model.Stolen with
            | Some holder, Some stolen when stolen.Terminal = terminal && stolen.TakenBy = holder ->
                terminalStolenNotice dispatch model terminal holder
            | Some holder, _ -> terminalLeaseBar dispatch model terminal holder
            | None, _ -> Lit.nothing
        let rewoundBar =
            html $"""
                <div class="{Style.terminalBandRow}" data-terminal-rewound="{TerminalId.value terminal}">
                  <span class="{Style.paneSays}">{Dom.Text.watchingRecording}</span>
                  <div class="ml-auto flex items-center gap-2">
                    <button type="button" class="{Style.bandActPrimary}" data-terminal-rewound-live
                            aria-label="{Dom.Text.backToLive}" title="{Dom.Text.backToLive}"
                            @click={Ev(fun _ ->
                                          // The terminal toggle's own `live` press, then focus
                                          // onto that toggle: this bar leaves the document
                                          // with the rewind, and the hand was in it.
                                          dispatch (ShowInPaneMsg (Reading terminal))
                                          dispatch (MoveMsg DomMove.FocusWatchToggle))}>{Dom.Text.live}</button>
                  </div>
                </div>"""
        let commandInput =
            html $"""
                    <div>
                      {others |> List.map peerDraft}
                      <div class="{Style.terminalCommandWrap}">
                        <!-- The placeholder is a `$`, and that is the prompt glyph rather than a
                             hint that replaced one: a placeholder sits exactly at the text
                             origin, so it marks where the command will start and the first
                             character typed takes its place. `aria-label` carries the NAME —
                             a placeholder has never been one.

                             A phone keyboard treats a text input as prose unless told
                             otherwise: it capitalises the first word, autocorrects the rest,
                             and underlines what it does not know. A command line is none of
                             those things — `Git` is not `git`, and `ls -la` is not a typo. The
                             four attributes are written out at each command surface rather
                             than composed: lit interpolates VALUES, not attribute names. -->
                        <input type="text" class="{Style.terminalCommand}" aria-label="Command"
                               placeholder="$"
                               autocapitalize="off" autocorrect="off" autocomplete="off" spellcheck="false"
                               data-terminal-input="{BodyKey.terminalDraft terminal mine}">
                        <div class="{Style.terminalCommandTrail}">
                          <span class="{Style.terminalEditors}">{editors mine}</span>
                          <button type="button" class="{runClass}" aria-label="{sendName}" title="{sendWhen}" aria-keyshortcuts="Enter"
                                  data-terminal-send="{PeerId.value mine}"
                                  @click={Ev(fun _ -> actions.SendTerminalDraft terminal mine)}>{Icon.send}</button>
                        </div>
                      </div>
                    </div>"""
        // A reader behind the live edge is watching a recording, and a line to type a command
        // into under it reads as if what they type happens in what they watch. It is
        // replaced, not disabled: a disabled field is still a field, and a person types into
        // it. What they had written is a `Y.Text` root, not this input, so it is waiting when
        // the input comes back. The queue and the lease bar stay — they are about the
        // terminal, which goes on running.
        let commandLines = if ClientModel.isRewound terminal model then rewoundBar else commandInput
        // A terminal that cannot run commands, and the loss refused what was
        // queued here. Top to bottom, what a person has to know: that, and since when, after
        // which command; the shell's own words, in the detail note; that the open command may
        // have run; what was refused; that they can still type here; and the one way on —
        // a new terminal, where the commands can run.
        let lostBanner =
            match view |> Option.bind (fun view -> view.Lost) with
            | None -> Lit.nothing
            | Some lost ->
                let id = TerminalId.value terminal
                let blocks = view |> Option.map (fun view -> view.Blocks) |> Option.defaultValue []
                let openBlock = lost.BlockId |> Option.bind (fun blockId -> blocks |> List.tryFind (fun b -> b.BlockId = blockId))
                // The command the loss happened after, by the name a block wears, cut to a head a
                // sentence can carry: a long agent command line would otherwise be the banner.
                let head (block: Block) =
                    let label = (BlockLabel.ofBlock block).TrimStart ([| '$'; ' ' |])
                    if label.Length <= 48 then label else label.Substring (0, 47) + "…"
                let since =
                    Dom.Text.terminalCannotRunSince
                        (lost.Evidence |> Option.map (fun evidence -> Moment.stamp evidence.WrittenAt))
                        (openBlock |> Option.map head)
                let unknown =
                    match openBlock with
                    | Some block when block.Status = BlockRunning ->
                        html $"""<span class="{Style.small}">{Dom.Text.terminalOpenUnknown (head block)}</span>"""
                    | _ -> Lit.nothing
                let printed = lost.Evidence |> Option.map (fun evidence -> Dom.Text.terminalShellPrinted evidence.Said)
                // The refusals are blocks of their own, on the record beside the loss: the
                // list reads them, so it says what was actually refused and by whom it was
                // queued, never what this client guesses was in the queue.
                let refusedIds = lost.Refused |> List.map (fun act -> Some act.QueueId)
                let refused =
                    blocks
                    |> List.filter (fun b ->
                        List.contains b.QueueId refusedIds
                        && (match b.Status with BlockRejected _ -> true | _ -> false))
                let refusedList =
                    match refused with
                    | [] -> Lit.nothing
                    | refused ->
                        let line (block: Block) =
                            let author = Authority.author block.Authority
                            html $"""
                                <li class="{Style.terminalHeldRow}" data-terminal-refused="{BlockId.value block.BlockId}">
                                  <code class="{Style.chatChipCommand}" title="{block.Command}">{BlockLabel.ofBlock block}</code>
                                  <span class="{Style.small}">{Entity.render model author (EntityRef.Actor author)}</span>
                                </li>"""
                        html $"""
                            <span class="{Style.small}">{Dom.Text.terminalRefusedHere refused.Length}</span>
                            <ul class="{Style.terminalHeldList}">{refused |> List.map line}</ul>"""
                let sandbox = view |> Option.bind (fun view -> view.Sandbox) |> Option.defaultValue SandboxRef.defaultRef
                html $"""
                    <div class="{Style.terminalPaused}" data-terminal-lost="{id}" role="status" aria-live="polite">
                      <span class="{Style.terminalPausedTitle}">{Dom.Text.terminalCannotRun}</span>
                      <span class="{Style.small}">{since}</span>
                      {detailNote "terminal-lost" ([ Dom.Text.terminalCannotRunWhy ] @ Option.toList printed)}
                      {unknown}
                      {refusedList}
                      <span class="{Style.small}">{Dom.Text.terminalStillTypes}</span>
                      <div class="{Style.terminalChoice}">
                        <button type="button" class="{Style.bandActPrimary}" data-terminal-lost-new="{id}"
                                @click={Ev(fun _ -> dispatch (OpenTerminalMsg ("", sandbox)))}>{Dom.Text.openNewTerminal}</button>
                      </div>
                    </div>"""
        // Stop: ^C to the command running here, the terminal left standing. On top of the
        // command line, as the chat's interrupt is on top of its composer, rather than on the
        // command's own line: a terminal runs one command at a time and it is always the
        // newest, so its own line just above already says what and by whom, and this strip
        // stays where the hand is however far the output scrolls. Only while one runs, and
        // named for it, because a screen reader hears a list of Stops otherwise
        // indistinguishable. Ctrl-C in an empty command line is the same request, which the
        // shortcut attribute says.
        let stop =
            match Projection.tryFind terminal model.Terminals |> Option.bind Projection.runningBlock with
            | None -> Lit.nothing
            | Some block ->
                html $"""
                    <div class="{Style.terminalStopStrip}">
                      <button type="button" class="{Style.terminalStop}" data-terminal-stop="{TerminalId.value terminal}"
                              aria-label="{Dom.Text.stopCommand (BlockLabel.ofBlock block)}" title="{Dom.Text.stopCommandHint}"
                              aria-keyshortcuts="Control+C"
                              @click={Ev(fun _ -> dispatch (InterruptTerminalMsg terminal))}>{Icon.stop}stop</button>
                    </div>"""
        // What is left here is what this region is FOR: what is waiting to run, and the line
        // you say the next thing on.
        html $"""
            <section class="{Style.terminalComposer}">
              <span class="{Style.bandRail}"></span>
              <span class="{Style.terminalBandEdge hasCommand}"></span>
              {stop}
              {lostBanner}
              {terminalQueue actions dispatch model terminal}
              {leaseBar}
              {commandLines}
            </section>"""

    /// A CLOSED terminal's band: what the composer's slot says once there is nothing left to
    /// type into it.
    ///
    /// Only what the read above cannot show — why the terminal closed, and whether its
    /// recording survived. The recording itself is no longer HERE: a closed terminal that ran
    /// commands has two reads of one history, and printing both at once put a player of the
    /// same two lines under every command and its result. The blocks are the read; the
    /// recording is where you go (`terminalBody`).
    /// A terminal's one control between its two reads (Plan 14, stage 7; Plan 25, stage 3):
    /// its TEXT — the live screen, or the blocks it ran — and its RECORDING.
    ///
    /// It was four controls with four names: two ways in at the top of the scrollback
    /// (`↑ replay from the start`, `↑ play the recording`) and two ways out floating over it
    /// (`Back to blocks`, `Jump to live`). Each removed another from the document, so each
    /// press had to hand focus on after itself; and each was named after the projection it
    /// mounted rather than after anything a reader wants. One control that relabels in place
    /// is the same act with neither problem — the press keeps its own focus, because the
    /// button it was pressed on is still there saying the other thing.
    ///
    /// A surface with only ONE read offers nothing: a recording that is the only read
    /// (`ReplayIsTheRead`) has no text behind it, and a terminal with nothing recorded has no
    /// recording to go to. Both are rules about the terminal, asked here rather than decided
    /// here.
    /// `None` rather than an empty render, because the answer is read by the action row, which
    /// must know whether it has anything to draw at all: a row that cannot tell "no verbs" from
    /// "a verb that rendered nothing" draws a bordered strip saying there are no controls.
    let private terminalWatchToggle
        (dispatch: ClientMsg -> unit)
        (model: ClientModel)
        (view: TerminalView)
        : TemplateResult option =
        let terminal = view.TerminalId
        let feed = ClientModel.terminalFeed terminal model
        let rewound = ClientModel.isRewound terminal model
        let playing = ClientModel.terminalPlays terminal model
        // `None` for the press means the rewind's own message: watching a LIVE terminal is
        // pinning its edge, and the pin is read off the feed there rather than by whoever
        // remembered to look it up first.
        let offer =
            if rewound then Some ("live", html $"""{Icon.rights}{Dom.Text.live}""", Some (Reading terminal))
            elif playing then
                // Back to the text, where there is text to go back to.
                if List.isEmpty view.Blocks then None else Some ("output", html $"""{Icon.rights}{Dom.Text.output}""", Some (Reading terminal))
            elif view.IsOpen then
                if feed.KnownLength > 0 then Some ("watch", html $"""{Icon.rewind}{Dom.Text.rewindAct}""", None) else None
            elif ClientModel.terminalPlayable terminal model then Some ("watch", html $"""{Icon.play}{Dom.Text.replay}""", Some (Watching terminal))
            else None
        match offer with
        | None -> None
        | Some (face, label, next) ->
            Some (
                html $"""
                <button type="button" class="{Style.paneToolbarAct}" data-terminal-watch="{face}"
                        @click={Ev(fun _ ->
                                      match next with
                                      | Some mode -> dispatch (ShowInPaneMsg mode)
                                      | None -> dispatch (RewindTerminalMsg view.TerminalId))}>{label}</button>""")

    let private terminalClosedBand (model: ClientModel) (view: TerminalView) : TemplateResult =
        let feed = ClientModel.terminalFeed view.TerminalId model
        // The per-terminal output cap (stage 3d) can eat a whole recording. Saying so is the
        // point: an empty player would be indistinguishable from a terminal that printed
        // nothing, and the whole reason the drop is recorded is that a gap in an audit trail
        // must be a stated fact. Said only of what is KNOWN: a recording the store does not
        // hold, or one it holds with every record capped away — never of one this reader has
        // simply not been answered about yet (`RecordingKnown`).
        let affords = ClientModel.affordances view model
        let gone = affords.RecordingLost || (affords.CanReplay && Map.isEmpty feed.Records && view.DroppedBytes > 0)
        // Which one closed, by the name it wore while open: a closed band reached from the
        // chat may be the only thing on screen that says which terminal this was.
        let name = TerminalName.display model.Terminals view
        // Somebody killing it is said as WHO, drawn as the reference they are everywhere
        // else — never as the sentence the close was recorded with, which says "a peer" to
        // the very person who pressed kill. Anything else (a shell that exited, a stream that
        // ended, a restart) is its reason: there was nobody, and the reason is the news.
        let closedFor =
            match view.Closed with
            | Some closed ->
                match closed.By with
                // The viewer is told "you killed term 3": the generated name that stands in
                // for them everywhere else on the screen reads, here, as somebody else.
                | Some who when who = ClientModel.me model -> html $"""{Dom.Text.youKilled} {name}"""
                | Some (UserRef _ | PeerRef _ | ActorRef.Agent as who) ->
                    html $"""{name} {Dom.Text.killedBy} {Entity.render model who (EntityRef.Actor who)}"""
                | Some (ActorRef.Session | ActorRef.System | ActorRef.Configured _)
                | None -> html $"""{name} closed: {closed.Reason}"""
            | None -> html $"""{name} closed"""
        // The gap in the audit trail, stated as a status rather than narrated: the drop is
        // recorded so it can be SAID — in the voice the `all` page says it in, because it is
        // the same fact and nothing a reader can act on (`Style.terminalGone`).
        let notKept =
            if not gone then Lit.nothing
            else
                html $"""
                    <span class="{Style.terminalGone}"
                          data-terminal-replay-gone="{TerminalId.value view.TerminalId}">{Dom.Text.recordingLost}</span>"""
        html $"""
            <section class="{Style.terminalComposer}" data-terminal-closed-band="{TerminalId.value view.TerminalId}">
              <span class="{Style.bandRail}"></span>
              <div class="{Style.terminalBandRow}">
                <span class="{Style.paneSays}">{closedFor}</span>
                {notKept}
              </div>
            </section>"""

    /// One block's read-only view, as a preview opened from its chip shows it: the command,
    /// and everything it printed, from the chunks this client already has.
    ///
    /// The very same renderer the terminal's own history uses — a block read from the chat
    /// must not be a second rendering of a block, free to drift from the first.
    let private paneBlockView (model: ClientModel) (preview: Preview) (terminalId: TerminalId) (blockId: BlockId) : TemplateResult =
        let found =
            Projection.tryFind terminalId model.Terminals
            |> Option.bind (fun view -> view.Blocks |> List.tryFind (fun b -> b.BlockId = blockId))
        match found with
        | None ->
            html $"""
                <div class="{Style.paneReadonly}" data-pane-block="{BlockId.value blockId}">
                  <div class="{Style.terminalOutputEmpty}">not on this device</div>
                </div>"""
        | Some block ->
            let playing = ClientModel.previewPlays preview model
            // What this block affords — the recording of it, and its place in the terminal's
            // own history — is the ACTION ROW's, at the bottom of the column with every other
            // kind's verbs (`paneActionsView`). It used to be a strip of this body's own, which
            // is how a reader who had learnt where "download" lives under a picture found
            // nothing in that place under a command.
            let body =
                if playing then replayMount "Command output, played" (PreviewSubject.key preview.Subject)
                else
                    let feed = ClientModel.terminalFeed terminalId model
                    html $"""
                        <div class="{Style.paneReadonly}" role="region" aria-label="Command output">
                          {terminalBlockOutput TerminalFeed.shownLines outputElidedNote feed block}
                        </div>"""
            html $"""
                <section class="{Style.paneBody}" data-pane-block="{BlockId.value blockId}">
                  <div class="{Style.paneFacts}">
                    <div class="{Style.terminalBlockCommand}">
                      <span class="{Style.terminalBlockLine}">
                        <span class="{Style.terminalPrompt}">$</span>
                        <code class="{Style.terminalCommandText}">{block.Command}</code>
                      </span>
                      <span class="ml-auto shrink-0">{terminalBlockStatusAt model terminalId block}</span>
                    </div>
                  </div>
                  {body}
                </section>"""

    /// A stretch's facts: who held the terminal, for how long, and how it ended. The
    /// recording itself mounts beneath this (Plan 14, stage 4); these are the parts that
    /// come from the event log and therefore render at any scroll depth without a transcript.
    let private paneStretchView (model: ClientModel) (preview: Preview) (stretch: TerminalStretch) : TemplateResult =
        let length = durationText (TerminalStretch.duration stretch)
        let where = Entity.terminalName model stretch.TerminalId |> Option.defaultValue stretch.Title
        let recording =
            // The count in the pane's caption voice, with tabular figures; the raw transcript
            // seqs are plumbing and stay out of the room.
            match stretch.Range with
            | Some (fromSeq, toSeq) ->
                html $"""<span class="{Style.paneSays} tabular-nums">{Dom.Text.linesRecorded (toSeq - fromSeq)}</span>"""
            // Stated, not blank: a stretch with no recorded bounds is a gap in the record,
            // and an empty player would be indistinguishable from a quiet session. Said as a
            // lost recording is said everywhere else in the pane (`Style.terminalGone`).
            | None ->
                html $"""<span class="{Style.terminalGone}">not recorded</span>"""
        // A stretch has no other read: somebody held the keyboard, and what they did is bytes
        // rather than commands. So it plays without being asked, which is what the model says
        // about it (`previewPlays`) rather than something this template decides.
        let player =
            if ClientModel.previewPlays preview model
            then replayMount "Session recording" (PreviewSubject.key preview.Subject)
            else Lit.nothing
        html $"""
            <section class="{Style.paneBody}">
              <div class="{Style.paneFacts}" data-pane-stretch="{TerminalStretch.key stretch}">
                <!-- One row: who, where, how it ended, and for how long LAST — the short parts
                     never wrap, so a narrow pane gives way in the terminal's name and nowhere
                     else. -->
                <div class="{Style.terminalQueuedRow}">
                  <span class="{Style.paneWho}">{Entity.actorName model stretch.Holder}</span>
                  <span class="{Style.cls [ Style.small; Style.givesWay ]}">typed in {where}</span>
                  <span class="ml-auto shrink-0 whitespace-nowrap">{stretchEnding Style.paneSays model stretch.End}</span>
                  <span class="{Style.small} shrink-0 whitespace-nowrap tabular-nums">{length}</span>
                </div>
                {recording}
              </div>
              {player}
            </section>"""

    /// One file from the session's content root, shown in the pane (Plan 26).
    ///
    /// The pane stopped being the terminal panel and became a content area, and this is the
    /// second kind it can hold. What it draws is decided by the NAME's media type
    /// (`ContentKind`) — the same rule the chip's mark uses, so a chip that promises a picture
    /// opens one — and a kind this build cannot draw is a DOWNLOAD rather than an error: an
    /// unsupported type is a file the reader can still have, and saying "cannot show" while
    /// withholding the bytes would be the pane's limitation reported as the file's.
    ///
    /// The address is the content route, relative like every other (`<base href>` resolves
    /// it), so the browser fetches the bytes itself with the session cookie it already has.
    /// A pinned artifact version is immutable at its address, which is what lets an `<img>`
    /// be right without this knowing that versions exist.
    /// The way to HAVE the file, wherever it is offered. `download` names it the way a person
    /// knows it, not the way it is addressed: saved from a pinned version the browser would
    /// otherwise write `0003-7f2a91` to disk.
    /// `target="_blank"` escapes an iOS PWA's standalone shell: the OS routes `download` there to
    /// QuickLook with no chrome (no Done, no back), trapping the installed app. Opening in a real
    /// Safari tab instead gives QuickLook its Done/Share button.
    let private contentDownloadLink (ref: ContentRef) : TemplateResult =
        let url = RelativeUrl.inDocument DocumentBase.shell (SessionRoute.relative (SessionRoute.Content ref))
        html $"""
            <a class="{Style.paneToolbarAct}" href="{url}" download="{ContentName.ofRef ref}" target="_blank"
               data-content-download="{ContentRef.value ref}">{Dom.Text.download}</a>"""

    let private paneContentView (ref: ContentRef) : TemplateResult =
        let url = RelativeUrl.inDocument DocumentBase.shell (SessionRoute.relative (SessionRoute.Content ref))
        let name = ContentName.ofRef ref
        // The download is NOT here: it is a verb about the thing on screen, and those are the
        // action row's, at the bottom of the column whatever kind is showing. A file that
        // carried its own copy would be the one kind whose verbs moved when you opened it.
        match ContentName.kind ref with
        | ContentKind.Image _ ->
            html $"""
                <section class="{Style.paneBody}" data-pane-content="{ContentRef.value ref}">
                  <div class="{Style.contentImageBox}">
                    <img class="{Style.contentImage}" src="{url}" alt="{name}" data-content-image="{ContentRef.value ref}">
                  </div>
                </section>"""
        | ContentKind.Download ->
            html $"""
                <section class="{Style.paneBody}" data-pane-content="{ContentRef.value ref}">
                  <div class="{Style.contentDownload}">
                    <span class="{Style.entityMark}" aria-hidden="true">{Icon.fileSm}</span>
                    <span class="{Style.small}">{name}</span>
                  </div>
                </section>"""

    /// The pane's ACTION ROW: the acts about the thing on screen, in one place at the bottom of
    /// the column whatever kind that thing is — take a terminal's keyboard, watch its
    /// recording, step from a block to its place in the history, have a file.
    ///
    /// They were in three places, one per kind: a terminal's in the head, a block's inside its
    /// own body, a file's inside its own body. Three places is three things for a reader to
    /// learn, and the cost is paid by whoever learnt one of them — having found `Download`
    /// under a picture, they look under a command and find nothing there.
    ///
    /// ABSENT rather than empty when what is showing affords nothing: a bordered strip with no
    /// controls in it is a control bar saying there are none. That is why the verbs are built
    /// as a LIST and the row asks whether it is empty, rather than each verb rendering its own
    /// nothing into a strip that is drawn regardless. `key` says what the verbs are about —
    /// a terminal's tab key, or a preview's subject key.
    let private paneActionsView (key: string) (verbs: TemplateResult list) : TemplateResult =
        if List.isEmpty verbs then Lit.nothing
        else
            html $"""
                <div class="{Style.paneActions}" data-pane-actions="{key}">{verbs}</div>"""

    /// What a terminal on screen affords, for the action row.
    let private terminalVerbs (dispatch: ClientMsg -> unit) (model: ClientModel) (terminal: TerminalId) : TemplateResult list =
        match Projection.tryFind terminal model.Terminals with
        | None -> []
        | Some view ->
            // Taking the keyboard changes what this terminal IS, not what the next command
            // says, so it is an act about the terminal. The STEAL — taking it from whoever
            // holds it — stays on the lease bar, where the name of the person you would be
            // taking it from is.
            let take =
                if not view.IsOpen || Option.isSome view.Lease then []
                else
                    [ html $"""
                        <button type="button" class="{Style.paneToolbarAct}" data-terminal-take="{TerminalId.value view.TerminalId}"
                                @click={Ev(fun _ -> dispatch (TakeTerminalMsg view.TerminalId))}>{Icon.keyboard}{Dom.Text.typeHere}</button>""" ]
            take @ Option.toList (terminalWatchToggle dispatch model view)

    /// What a preview on screen affords, for the action row: a command's recording and its
    /// place in its terminal's history, a file's download. A stretch is always its recording
    /// and plays without being asked, so it has nothing to offer and nothing to step out to.
    let private previewVerbs (preview: Preview) : TemplateResult list =
        match preview.Subject with
        // A command's preview has one control, its close, which settles it back into its
        // terminal (`View.contentPane`): it is the command lifted out of its history, and its
        // recording and its place in that history are the terminal's to give.
        | PreviewSubject.Block _ -> []
        | PreviewSubject.Stretch _ -> []
        | PreviewSubject.Content ref -> [ contentDownloadLink ref ]

    /// A terminal's kill (P0-5; P2-2): two presses in one place (`KillArmed`) — the first arms,
    /// the second kills, by `ClientModel.killPress`. ONE button whichever face it wears, so the
    /// element a keyboard pressed is the element it confirms on and focus never moves between
    /// the two. Armed, it says what it will end and what is running there. Escape, the wait
    /// (`ClientModel.armedMs`), or focus leaving it takes it back.
    ///
    /// On the list's row only: a tab's × closes the tab and ends nothing. It wears the stop
    /// mark and the word `stop`, the interrupt's face, because it is the same act on a whole
    /// terminal. Absent, not disabled, where the terminal cannot be stopped.
    let private killControl
        (dispatch: ClientMsg -> unit)
        (model: ClientModel)
        (face: string)
        (armedFace: string)
        (glyph: TemplateResult)
        (view: TerminalView)
        : TemplateResult =
        if not (ClientModel.affordances view model).CanKill then Lit.nothing
        else
            let id = TerminalId.value view.TerminalId
            let name = TerminalName.display model.Terminals view
            let armed = model.KillArmed = Some view.TerminalId
            let running = Projection.runningBlock view |> Option.map BlockLabel.ofBlock
            // Nothing running is a fact worth saying, not an absence: it is what makes a kill
            // cheap.
            let says = running |> Option.defaultValue Dom.Text.killIdle
            let klass, label, content =
                if armed then
                    armedFace,
                    Dom.Text.confirmKill name running,
                    html $"""{Dom.Text.killConfirm}<span class="{Style.killArmedRunning}">{says}</span>"""
                else face, Dom.Text.killTerminal name, glyph
            html $"""
                <button type="button" class="{klass}" data-terminal-close="{id}"
                        data-terminal-close-armed="{if armed then "true" else "false"}"
                        aria-label="{label}" title="{Dom.Text.killTerminal name}"
                        @click={Ev(fun (e: Browser.Types.Event) ->
                                      // Not the tab's press too: a × is not a way to select.
                                      e.stopPropagation ()
                                      dispatch (KillPressedMsg view.TerminalId))}
                        @keydown={Ev(Events.key (fun e ->
                                      if armed && e.key = "Escape" then
                                          // Escape here is about the arming, and only that: the
                                          // switcher or the preview around it stays.
                                          e.stopPropagation ()
                                          dispatch (ArmKillMsg None)))}
                        @focusout={Ev(fun _ -> if armed then dispatch (ArmKillMsg None))}>{content}</button>"""

    /// The `all` item's key: its pivot item's id and its panel's, as a terminal's tab key is
    /// theirs.
    let private allKey = "all"

    /// What a terminal's panel reads — its blocks, its live screen, or its recording — the
    /// one decision both the panel and the line under the pivot read, so the line can say
    /// exactly what the panel does not.
    [<RequireQualifiedAccess>]
    type private TerminalRead =
        | Blocks
        | Screen
        | Recording

    let private terminalRead (model: ClientModel) (view: TerminalView) : TerminalRead =
        if ClientModel.terminalPlays view.TerminalId model then TerminalRead.Recording
        elif (ClientModel.affordances view model).ScreenIsTheRead || Option.isSome view.Lease then TerminalRead.Screen
        else TerminalRead.Blocks

    /// The `all` page (Plan 20, stage 0; P2-2): every terminal the session has ever had, with
    /// every verb one of them affords, and every file shared into it — the pivot's first item,
    /// and the pane's body while it is selected.
    ///
    /// It has been the pane's other face behind a toggle nobody could read, and then a boxed
    /// popover hung under the head's name; the second made the pane three stacked layers.
    /// Choosing from it is choosing a tab. It is also the one place a terminal is made: New
    /// terminal on its Terminals heading, as Upload files is on its Files heading — the strip's
    /// `+` and its menu, and the empty pane's own button, were the same act in two more places.
    ///
    /// The terminals are in the order they were opened, the open ones first and the closed
    /// ones folded under one row that unfolds in place (`ClientModel.listSections`). A
    /// session's closed terminals are its record, and forty of them listed among the live
    /// ones made a list a reader had to read all of to find the one that wanted them. The
    /// rows above the fold are held while the list is up (`ListHeld`), so a row does not
    /// move under the pointer when its terminal ends.
    ///
    /// The verbs are rendered from `Affordances` and from nothing else — a row wears exactly
    /// the controls its terminal's state allows, and a control that does not apply is ABSENT
    /// rather than disabled. They are the row's, not the page's, and every row wears them at
    /// rest (`Style.terminalListVerbs`): what a control is doing is visible at rest and a
    /// thumb has no hover (docs/visual-design.md, principle 4). They were shown only on the
    /// selected row, on an armed one, and elsewhere under the pointer; the saved width cost
    /// every reader without a pointer the verbs altogether.
    /// A closed row's verbs are its recording's (`replay`) and its tab's (`put away`), where
    /// an open one's are its rewind and its kill. A row's state is the mark the pivot wears
    /// for it (`terminalMark`), whoever holds its keyboard is their own colour beside it, and
    /// the one state with no glyph — a recording the cap ate — is the only one that says a
    /// word.
    ///
    /// Lists of rows rather than a `listbox`: a row carries its verbs, and a listbox's options
    /// may hold no controls. The name is the row's own button; the arrows walk the names
    /// (`ClientModel.walkList`), Tab reaches the verbs.
    ///
    /// It is the pane's body while it is up, as any tab's panel is: in place of what was on
    /// screen, never laid over it.
    let private allPage (actions: ViewActions) (dispatch: ClientMsg -> unit) (model: ClientModel) : TemplateResult =
        let selected = ClientModel.selectedTerminal model
        // The arrow walk down the names, pressed on one: the row says which it is, and the
        // model says where that goes (`ClientModel.walkList`). Its default is refused here,
        // because that cannot wait for the model, and only for a key the walk claims, so Tab
        // still reaches the verbs.
        let walk (from: ListRow) (pressed: Browser.Types.KeyboardEvent) =
            if TabStrip.claimsRow pressed.key then
                pressed.preventDefault ()
                dispatch (WalkListMsg (pressed.key, from))
        let row (view: TerminalView) =
            let id = TerminalId.value view.TerminalId
            let name = TerminalName.display model.Terminals view
            let affords = ClientModel.affordances view model
            // Whoever is typing, in their own colour — the same dot the roster and the pivot
            // wear, so one person is one mark on every surface at once.
            let holder =
                if not view.IsOpen then Lit.nothing
                else
                    match view.Lease with
                    | Some (PeerRef peer) ->
                        html $"""<span class="{Style.syncDot}" style="background:{Entity.presenceColour model (ActorRef.PeerRef peer)}"
                                       title="{Entity.actorName model (PeerRef peer)}"></span>"""
                    | Some holder ->
                        html $"""<span class="{Style.statusOk}" title="{Entity.actorName model holder}"><span class="{Style.statusDot}"></span></span>"""
                    | None -> Lit.nothing
            // A hole in an audit trail is stated — beside the name, quietly
            // (`Style.terminalGone`): nine closed rows of it in the error red were a column of
            // alarms about something nobody can act on. On the NAME's line, never a line of its
            // own: a row with no command under it would grow by one when its terminal died,
            // and move every row beneath it.
            let gone =
                if not affords.RecordingLost then Lit.nothing
                else html $"""<span class="{Style.terminalGone}" data-terminal-list-gone="{id}">{Dom.Text.recordingLost}</span>"""
            let peers =
                ClientModel.editorsInTerminal view.TerminalId model
                |> List.map (fun (who, name) ->
                    html $"""
                        <span class="{Style.draftEditorDot}" style="background:{Entity.presenceColour model who}"
                              title="{name}" data-terminal-tab-peer="{ActorRef.token who}"></span>""")
            let reattach =
                if not affords.CanReattach then Lit.nothing
                else
                    html $"""
                        <button type="button" class="{Style.terminalListAct}" data-terminal-reattach="{id}"
                                aria-label="Attach {name} again" title="Attach {name} again"
                                @click={Ev(fun _ -> dispatch (ReattachTerminalMsg view.TerminalId))}>{Dom.Text.reattach}</button>"""
            // A closed terminal's tab, put away — the closed tab's ×, from here. Only while it
            // HAS a tab: a closed terminal nobody opened here has nothing to put away.
            let putAway =
                if view.IsOpen || not (List.contains view.TerminalId model.Tabs) then Lit.nothing
                else
                    html $"""
                        <button type="button" class="{Style.terminalListAct}" data-terminal-list-dismiss="{id}"
                                aria-label="{Dom.Text.dismissTab name}" title="{Dom.Text.dismissTab name}"
                                @click={Ev(fun _ -> dispatch (DismissTabMsg view.TerminalId))}>{Dom.Text.putAway}</button>"""
            let nameClass = if view.IsOpen then Style.terminalListName else Style.terminalListNameClosed
            let killWord = html $"""{Icon.stop}{Dom.Text.kill}"""
            // What it is doing or last did, and where, under the name: nine rows of `term N`
            // say which is which, and this says which is the one you want.
            // The row's button holds both lines, so a row is pressed anywhere on them; its
            // name is the name alone, and the line under it describes it.
            let nameId = sprintf "list-row-%s" id
            let subtitleId = sprintf "list-row-%s-says" id
            //
            // Its state's mark leads the line — the line is there on every row, so a row is
            // the same height whatever its terminal has run.
            let subtitle =
                let summary =
                    match TerminalName.summary view with
                    | "" -> Lit.nothing
                    | summary -> html $"""<span class="{Style.terminalListSummary}">{summary}</span>"""
                html $"""<span class="{Style.terminalListSubtitle}" id="{subtitleId}" title="{TerminalName.hint view}">{terminalMark model view}{summary}</span>"""
            let mode = ClientModel.chosenRead view.TerminalId model
            html $"""
                <div class="{Style.terminalListRow}" role="listitem">
                  <button type="button" class="{nameClass}" data-terminal-list-row="{id}"
                          aria-current="{if selected = Some view.TerminalId then "true" else "false"}"
                          aria-labelledby="{nameId}" aria-describedby="{subtitleId}"
                          @keydown={Ev(Events.key (walk (ListRow.Terminal view.TerminalId)))}
                          @click={Ev(fun _ -> dispatch (OpenInPaneMsg mode))}>
                    <span class="{Style.terminalListNameLine}">
                      <span class="{Style.terminalListNameText}" id="{nameId}" data-terminal-list-name>{name}</span>
                      {holder}{gone}
                      <span class="{Style.terminalTabPeers}">{peers}</span>
                    </span>
                    {subtitle}
                  </button>
                  <span class="{Style.terminalListVerbs}">{reattach}{putAway}{killControl dispatch model Style.terminalListKill Style.btnKillArmed killWord view}</span>
                </div>"""
        // A file's row, in the same shape as a terminal's. Reachability is the whole point of
        // it: a file is otherwise findable only by its chip in a message, so one shared two
        // hundred messages ago could not be opened again at all. It opens as a PREVIEW (P2-1),
        // which is what a file in this pane is.
        //
        // The mark is `ContentKind`'s, the same rule the chip and the preview use, so what a row
        // promises and what opening it draws cannot disagree. The size sits under the name
        // because it is what decides between looking at it here and taking it away.
        let artifactRow (a: ArtifactShared) =
            let content = ArtifactRef.content a.Ref
            let mark = Icon.ofContent (ContentKind.ofMediaType a.MediaType)
            let nameId = sprintf "list-file-%s" (ContentRef.value content)
            let sizeId = nameId + "-size"
            html $"""
                <div class="{Style.artifactListRow}" role="listitem">
                  <button type="button" class="{Style.terminalListName}"
                          data-artifact-list-row="{ContentRef.value content}"
                          aria-labelledby="{nameId}" aria-describedby="{sizeId}"
                          @keydown={Ev(Events.key (walk (ListRow.File content)))}
                          @click={Ev(fun _ ->
                                        dispatch (OpenPreviewMsg (Preview.ofSubject (PreviewSubject.Content content))))}>
                    <span class="{Style.terminalListNameLine}">
                      <span class="{Style.terminalListNameText}" id="{nameId}">{ArtifactRef.name a.Ref}</span>
                      <span class="{Style.pivotMark} text-ink-faint" aria-hidden="true">{mark}</span>
                    </span>
                    <span class="{Style.artifactListSize}" id="{sizeId}">{ContentSize.render a.Bytes}</span>
                  </button>
                </div>"""
        // A file on its way in, in the row a shared one has, so it becomes that row in place
        // when the store records it. What it is doing is worn under the name: a wait in
        // `ink-dim` with the bytes gone, beating; or the one red word, with what can be done
        // about it as the row's verbs, worn at rest because there is nothing else to do here.
        let uploadRow (upload: Upload) =
            let token, status, verbs =
                match upload.State with
                | UploadState.Sending sent ->
                    "sending",
                    html $"""<span class="{Style.statusWait}">{Icon.spinLead}{Dom.Text.uploadSending (ContentSize.render sent) (ContentSize.render upload.Bytes)}</span>""",
                    [ "remove" ]
                | UploadState.Failed reason ->
                    "failed", html $"""<span class="{Style.statusErr}">{reason}</span>""", [ "retry"; "remove" ]
                | UploadState.Refused reason ->
                    "refused", html $"""<span class="{Style.statusErr}">{reason}</span>""", [ "remove" ]
            let verb (which: string) =
                match which with
                | "retry" ->
                    html $"""
                        <button type="button" class="{Style.terminalListAct}" data-upload-retry="{upload.Id}"
                                aria-label="{Dom.Text.retryUpload upload.Name}"
                                @click={Ev(fun _ -> dispatch (RetryUploadMsg upload.Id))}>{Dom.Text.retry}</button>"""
                | _ ->
                    html $"""
                        <button type="button" class="{Style.terminalListAct}" data-upload-dismiss="{upload.Id}"
                                aria-label="{Dom.Text.dismissUpload upload.Name}"
                                @click={Ev(fun _ -> dispatch (DismissUploadMsg upload.Id))}>{Dom.Text.remove}</button>"""
            html $"""
                <div class="{Style.artifactListRow}" role="listitem" data-upload-row="{upload.Name}" data-upload-state="{token}">
                  <span class="{Style.terminalListWho}">
                    <span class="{Style.terminalListNameLine}">
                      <span class="{Style.uploadName}">{upload.Name}</span>
                    </span>
                    <span class="{Style.artifactListSize}" role="status">{status}</span>
                  </span>
                  <span class="{Style.terminalListVerbs}">{verbs |> List.map verb}</span>
                </div>"""
        let sections = ClientModel.listSections model
        // Files arrive however a person has them: chosen, pasted while the page has the
        // keyboard, or dropped on it. All three are one action.
        let take (files: Browser.Types.File array) =
            if Array.isEmpty files then false
            else
                actions.Upload (List.ofArray files)
                true
        let picked (e: Browser.Types.Event) =
            EventTargets.asHTMLInputElement e.target
            |> Option.iter (fun input ->
                take (CarriedFiles.ofInput input) |> ignore
                // Emptied, so the same file chosen again is a change the input reports.
                input.value <- "")
        let pasted (e: Browser.Types.Event) = if take (CarriedFiles.ofPaste e) then e.preventDefault ()
        let dragged (e: Browser.Types.Event) =
            if CarriedFiles.inDrag e then
                e.preventDefault ()
                // `dragover` fires every few milliseconds; only the change is news.
                if not model.FilesOver then dispatch (FilesOverMsg true)
        let left (e: Browser.Types.Event) = if model.FilesOver && CarriedFiles.leftOut e then dispatch (FilesOverMsg false)
        let dropped (e: Browser.Types.Event) =
            if model.FilesOver then dispatch (FilesOverMsg false)
            if take (CarriedFiles.ofDrop e) then e.preventDefault ()
        // The way to choose is the file input itself, in a `<label>` that is its face: a press
        // on the label is a press on the input, and Tab, Enter and Space reach the input as
        // they reach any control, so the browser opens its chooser with no script of ours. A
        // `hidden` input with a button beside it had to be found in the DOM and clicked.
        let uploadControl =
            html $"""
                <label class="{Style.uploadControl}" data-artifact-upload>
                  <input type="file" class="{Style.pickerInput}" multiple data-artifact-upload-input
                         aria-label="{Dom.Text.uploadNamed}" @change={Ev(picked)}>{Icon.up}{Dom.Text.upload}</label>"""
        // The closed terminals, folded under one row that says how many — and unfolded in
        // place under it, never somewhere else. No fold over nothing.
        let closed =
            match sections.ClosedFold with
            | None -> Lit.nothing
            | Some fold ->
                let count = fold.Folded
                let shown = Option.isSome fold.UnfoldedRows
                let unfolded =
                    match fold.UnfoldedRows with
                    | Some rows -> html $"""<div role="list" aria-label="{Dom.Text.closedHeading}">{rows |> List.map row}</div>"""
                    | None -> Lit.nothing
                html $"""
                    <button type="button" class="{Style.listClosedFold}" data-list-closed-fold
                            aria-expanded="{if shown then "true" else "false"}"
                            aria-label="{Dom.Text.closedTerminals count}"
                            @click={Ev(fun _ -> dispatch ToggleClosedMsg)}>
                      <span>{Dom.Text.closedHeading}</span><span class="{Style.listCount}">{string count}</span>
                      <span class="{if shown then Style.listFoldMarkOpen else Style.listFoldMark}" aria-hidden="true">{Icon.caret}</span>
                    </button>
                    {unfolded}"""
        // The files under a heading of their own, which carries the way to add one — there
        // whether or not any file is, because a session with none is where the first comes
        // from.
        let files =
            let count =
                match List.length sections.FileRows + List.length sections.UploadRows with
                | 0 -> Lit.nothing
                | n -> html $"""<span class="{Style.listCount}">{string n}</span>"""
            let drop =
                if model.FilesOver then
                    html $"""<div class="{Style.uploadDropZone}" data-artifact-drop aria-hidden="true">{Icon.up}{Dom.Text.dropToUpload}</div>"""
                else Lit.nothing
            html $"""
                <div class="{Style.listSectionHead}">
                  <div class="{Style.listSectionLabel}" data-list-section="{Dom.Text.filesHeading}">
                    <span>{Dom.Text.filesHeading}</span>
                    {count}
                  </div>
                  {uploadControl}
                </div>
                {drop}
                <div role="list" aria-label="{Dom.Text.filesHeading}">
                  {(sections.FileRows |> List.map artifactRow) @ (sections.UploadRows |> List.map uploadRow)}
                </div>"""
        let empty =
            if List.isEmpty sections.OpenRows && Option.isNone sections.ClosedFold then
                html $"""<span class="{Style.contentListEmptyWord}">{Dom.Text.nothingOpenedYet}</span>"""
            else Lit.nothing
        let opened =
            if List.isEmpty sections.OpenRows then Lit.nothing
            else html $"""<div role="list" aria-label="{Dom.Text.terminalsHeading}">{sections.OpenRows |> List.map row}</div>"""
        // Everywhere a terminal can start, and the one New terminal will use. The places are
        // the session's own and each work sandbox started here; with only the first, there is
        // nothing to choose and the link that chooses is not drawn.
        let places = ClientModel.sandboxRows model
        let place = ClientModel.startIn model
        // A repo's place carries the repo's name, since two repos may both declare `dev`.
        let placeName (sandbox: SandboxRef) =
            match SandboxRef.scope sandbox with
            | _ when sandbox = SandboxRef.defaultRef -> Dom.Text.defaultPlace
            | SessionOwned -> SandboxName.value (SandboxRef.name sandbox)
            | RepoOwned repo -> sprintf "%s:%s" (RepoRef.repo repo) (SandboxName.value (SandboxRef.name sandbox))
        let isDefault (sandbox: SandboxRef) = sandbox = SandboxRef.defaultRef
        let choosing = model.ChoosingStartIn && List.length places > 1
        // The terminals under a heading of their own, which carries the one way to make one,
        // and — when there is a choice — where it will start, the way the repo picker's row
        // carries its branch: the name as a link, into a list that changes it. Pressed again
        // while the list is unfolded under it, it folds the list away.
        let terminals =
            let count =
                match List.length sections.OpenRows with
                | 0 -> Lit.nothing
                | n -> html $"""<span class="{Style.listCount}">{string n}</span>"""
            let startIn =
                if List.length places < 2 then Lit.nothing
                else
                    html $"""
                        <button type="button" class="{Style.startInLink}" data-start-in="{SandboxRef.render place}"
                                aria-label="{Dom.Text.startInNamed (placeName place)}"
                                aria-expanded="{if choosing then "true" else "false"}"
                                @click={Ev(fun _ -> dispatch (ChoosingStartInMsg (not choosing)))}>
                          <span class="{Style.startInWords}">
                            <span class="{Style.startInWord}">{Dom.Text.startInWord}</span>
                            <span class="{if isDefault place then "" else Style.startInName}">{placeName place}</span>
                          </span>
                          <span class="{Style.startInPageMark}" aria-hidden="true">{Icon.rightSm}</span>
                          <span class="{if choosing then Style.startInFoldMarkOpen else Style.startInFoldMark}" aria-hidden="true">{Icon.caret}</span>
                        </button>"""
            html $"""
                <div class="{Style.listSectionHeadFirst}">
                  <div class="{Style.listSectionLabel}" data-list-section="{Dom.Text.terminalsHeading}">
                    <span>{Dom.Text.terminalsHeading}</span>
                    {count}
                  </div>
                  <div class="{Style.listSectionActs}">
                    <button type="button" class="{Style.terminalNew}" data-terminal-new="{SandboxRef.render place}"
                            aria-label="{Dom.Text.newTerminalIn (SandboxRef.render place)}"
                            @click={Ev(fun _ -> dispatch (OpenTerminalMsg ("", place)))}>{Icon.plus}{Dom.Text.aNewTerminal}</button>
                    {startIn}
                  </div>
                </div>"""
        // The list of places: on a phone, a page in place of the list, the repo picker's
        // branch pane in this page's rows; on a wide screen, the same rows unfolded under the
        // heading, with the rest of the list still round them. Choosing one folds it, and New
        // terminal then starts there.
        let choosingPlace () =
            let choice (sandbox: SandboxRef) =
                let chosen = sandbox = place
                // What the file said the place is for.
                let note =
                    match ClientModel.sandboxPurpose sandbox model with
                    | None -> Lit.nothing
                    | Some purpose -> html $"""<span class="{Style.startInNote}">{purpose}</span>"""
                html $"""
                    <li class="{Style.startInRow}">
                      <button type="button" class="{Style.startInChoice}" data-start-in-choice="{SandboxRef.render sandbox}"
                              aria-pressed="{if chosen then "true" else "false"}"
                              aria-label="{Dom.Text.newTerminalIn (SandboxRef.render sandbox)}"
                              @click={Ev(fun _ -> dispatch (StartInMsg sandbox))}>
                        <span class="{Style.startInMark}" aria-hidden="true">{if chosen then Icon.check else Lit.nothing}</span>
                        <span class="{Style.startInWho}">
                          <span class="{if isDefault sandbox then Style.startInChoiceNameDefault else Style.startInChoiceName}">{placeName sandbox}</span>
                          {note}
                        </span>
                      </button>
                    </li>"""
            html $"""
                <div class="{Style.startInList}" data-start-in-list>
                  <div class="{Style.startInHead}">
                    <button type="button" class="{Style.startInBack}" data-start-in-back
                            aria-label="{Dom.Text.backToList}" title="{Dom.Text.backToList}"
                            @click={Ev(fun _ -> dispatch (ChoosingStartInMsg false))}>{Icon.left}</button>
                    <span>{Dom.Text.startInHeading}</span>
                  </div>
                  <ul aria-label="{Dom.Text.startInHeading}">{places |> List.map choice}</ul>
                </div>"""
        let beside = if choosing then Style.startInBesideChoosing else Style.startInBeside
        let listed =
            html $"""
                <div class="{beside}">{terminals}</div>
                {if choosing then choosingPlace () else Lit.nothing}
                <div class="{beside}">{opened}{closed}{empty}{files}</div>"""
        html $"""
            <div class="{Style.allPanel}" role="region" tabindex="-1" aria-label="{Dom.Text.terminalsAndFiles}"
                 id="{Dom.paneListId}" data-pane-panel="{allKey}">
              <div class="{Style.allPage}" data-content-list aria-label="{Dom.Text.terminalsAndFiles}"
                   @paste={Ev(pasted)} @dragover={Ev(dragged)} @dragleave={Ev(left)} @drop={Ev(dropped)}>
                {listed}
              </div>
            </div>"""

    /// The content pane: one PIVOT across its top — `all` and every tab — the one line under
    /// it about the selected item when the body does not say it already, then whatever that
    /// item is.
    ///
    /// The tabs are `Tabs` and nothing else: terminals this client opened. Every other
    /// terminal the session has is reached through `all`. A tab's × is its terminal's kill
    /// (P2-2) — there is no way to drop a tab and leave its terminal running — and a closed
    /// one keeps its place, closed, until the reader puts it away with its × (F2).
    /// A command or a stretch the chat opens is a PREVIEW (P2-1), laid over the terminal it
    /// belongs to: one at a time, and a layer OF that terminal rather than a tab beside it (F3).
    /// The terminal's tab stays the selected item, and the preview is named in the line under
    /// the pivot, beside its own close. A file has a tab of its own, after the terminals
    /// (`FileTabs`), and its preview is that tab's panel.
    let private contentPane (actions: ViewActions) (dispatch: ClientMsg -> unit) (model: ClientModel) : TemplateResult =
        let tabs = model.Tabs |> List.choose (fun terminal -> Projection.tryFind terminal model.Terminals)
        let selected = ClientModel.selectedTerminal model
        let previewing = ClientModel.preview model
        // A command's preview is the command LIFTED out of its terminal: it wears a ground of
        // its own, and its close settles it back. A file's or a stretch's is a view over the pane.
        let lifted (preview: Preview) =
            match preview.Subject with
            | PreviewSubject.Block _ -> true
            | PreviewSubject.Stretch _ | PreviewSubject.Content _ -> false
        // With nothing open the list is the pane's body (`allPage`), so `all` is up whether or
        // not it was asked for.
        let nothingOpen = Option.isNone previewing && Option.isNone selected
        let onAll = model.Switcher || nothingOpen
        // The file on screen, if one is: its own tab is the selected item.
        let shownFile =
            previewing
            |> Option.bind (fun preview ->
                match preview.Subject with
                | PreviewSubject.Content file -> Some file
                | PreviewSubject.Block _ | PreviewSubject.Stretch _ -> None)
        // Which pivot item is selected — exactly one, or none in a pane with nothing in it:
        // `all` while it is up, else the file on screen, else the terminal the pane is about.
        // A command's or a stretch's preview is not an item of the pivot (F3): it is laid over
        // its terminal, whose tab stays selected.
        let onTerminal (terminal: TerminalId) = selected = Some terminal && Option.isNone shownFile
        // Who else has this terminal open, on its item — the answer to "am I the only one
        // looking at this", which a reader has no other way to learn. Keyed by `ViewRef`, so a
        // terminal, a preview of one of its blocks and an artifact all ask one question of one
        // value.
        //
        // `excluding` is whoever is already drawn here as an EDITOR: a peer typing in a
        // terminal is also watching it, and two marks for one person reads as two people.
        let viewerDots (excluding: ActorRef list) (terminal: TerminalId) =
            ClientModel.viewersOf (ViewingTerminal terminal) model
            |> List.filter (fun (who, _) -> not (List.contains who excluding))
            |> List.map (fun (who, name) ->
                html $"""
                    <span class="{Style.paneViewerDot}" style="border-color:{Entity.presenceColour model who}"
                          title="{name} is watching" data-pane-viewer="{ActorRef.token who}"></span>""")
        // The roving stop: the one tab Tab reaches, which is the selected one — and the first
        // when none is, so the strip is never a row Tab cannot enter.
        let nothingOn =
            not (tabs |> List.exists (fun view -> onTerminal view.TerminalId))
            && not (model.FileTabs |> List.exists (fun file -> shownFile = Some file))
        let firstTab = ClientModel.stripTabs model |> List.tryHead |> Option.map ViewRef.said
        let stopAt (key: string) (on: bool) =
            if on || (nothingOn && firstTab = Some key) then "0" else "-1"
        let terminalItem
            (activate: unit -> unit)
            (activateKey: Browser.Types.KeyboardEvent -> unit)
            (view: TerminalView)
            =
            let on = onTerminal view.TerminalId
            // Not under a preview: there the tab is the way back to its terminal, and the
            // preview's own × is right under it — two closes a thumb's height apart read as
            // one control twice.
            let showing = on && Option.isNone previewing
            let key = ClientModel.tabKey view.TerminalId
            let id = TerminalId.value view.TerminalId
            // Under a command's preview, the tab wears the preview's own lifted ground, so the
            // two read as one piece: the command, lifted out of this terminal.
            // While the list is up it goes quiet: the list button is the one lit thing then.
            let klass =
                if not on || onAll then Style.pivotItem
                elif previewing |> Option.exists lifted then Style.pivotItemLifted
                else Style.pivotItemOn
            let name = TerminalName.display model.Terminals view
            // The item says WHICH terminal; what it is running, and the sandbox it runs in, ride
            // the tooltip, because a row of names is what a person scans and a row of commands
            // or sandboxes is a row of truncations.
            let tooltip = TerminalName.hint view
            // Who is in THIS terminal, on its item — the same presence the roster reports, put
            // where you would look for it. Without it, a collaborator typing a command in a
            // terminal you are not showing is visible nowhere in this column.
            let editors = ClientModel.editorsInTerminal view.TerminalId model
            let peers =
                (editors
                 |> List.map (fun (who, name) ->
                     html $"""
                         <span class="{Style.draftEditorDot}" style="background:{Entity.presenceColour model who}"
                               title="{name}" data-terminal-tab-peer="{ActorRef.token who}"></span>"""))
                @ viewerDots (editors |> List.map fst) view.TerminalId
            // Its state (`terminalMark`), so a terminal you are not showing still says it is
            // busy, that its last command failed, or that it has closed — and that a build in
            // it finished while you were elsewhere, until you go and look.
            // No box at all for nobody: an empty box in a flex row still takes its gap.
            let peers =
                if List.isEmpty peers then Lit.nothing
                else html $"""<span class="{Style.terminalTabPeers}">{peers}</span>"""
            let mark =
                if view.IsOpen && Option.isSome (Projection.runningBlock view) then
                    html $"""<span class="{Style.pivotMark}" data-terminal-tab-running>{terminalMark model view}</span>"""
                else terminalMark model view
            // The item's ×: it closes the TAB (`DismissTabMsg`), open terminal or closed, and
            // ends nothing — the terminal keeps running, in the list, where its stop is. A ×
            // on a tab means close the tab everywhere else a person has met one; it used to
            // mean kill here, and a press that ends a build is not one to make by habit.
            // Delete on the item is the same press from the keyboard. On the selected item
            // only.
            let dismiss =
                if not showing then Lit.nothing
                else
                    let label = Dom.Text.dismissTab name
                    html $"""
                        <button type="button" class="{Style.pivotTabClose}" data-pane-tab-dismiss="{id}"
                                aria-label="{label}" title="{label}"
                                @click={Ev(fun (e: Browser.Types.Event) ->
                                              // Not the item's press too: a × is not a way to select.
                                              e.stopPropagation ()
                                              dispatch (DismissTabMsg view.TerminalId))}>{Icon.close}</button>"""
            // Two literal spellings of one item, because lit-html cannot inject an attribute
            // NAME through a hole — and the open/closed hooks must stay apart: there is
            // nothing to run in a closed terminal, only something to read.
            if view.IsOpen then
                html $"""
                    <div role="tab" class="{klass}" data-pane-tab="{key}" data-terminal-tab="{id}"
                         id="{Dom.paneTabId key}" aria-controls="{Dom.panePanelId}"
                         aria-selected="{if on then "true" else "false"}" tabindex="{stopAt key on}" title="{tooltip}"
                         @keydown={Ev(Events.key activateKey)}
                         @click={Ev(fun _ -> activate ())}><span class="{Style.pivotName}" data-terminal-tab-name>{name}</span>{mark}{peers}{dismiss}</div>"""
            else
                html $"""
                    <div role="tab" class="{klass}" data-pane-tab="{key}" data-terminal-closed-tab="{id}"
                         id="{Dom.paneTabId key}" aria-controls="{Dom.panePanelId}"
                         aria-selected="{if on then "true" else "false"}" tabindex="{stopAt key on}" title="{tooltip}"
                         @keydown={Ev(Events.key activateKey)}
                         @click={Ev(fun _ -> activate ())}><span class="{Style.pivotName}" data-terminal-tab-name>{name}</span>{mark}{peers}{dismiss}</div>"""
        // What a preview is CALLED — its name under the pivot, its close's, and so its
        // panel's. One function, so they can never disagree. With it, what its name's
        // tooltip says: a block's whole command, which its name is only the working part of.
        let previewLabel (subject: PreviewSubject) : string * string =
            let same label = label, label
            match subject with
            | PreviewSubject.Block (terminalId, blockId) ->
                Projection.tryFind terminalId model.Terminals
                |> Option.bind (fun v -> v.Blocks |> List.tryFind (fun b -> b.BlockId = blockId))
                |> Option.map (fun b -> BlockLabel.ofBlock b, b.Command)
                |> Option.defaultValue (same (BlockId.value blockId))
            | PreviewSubject.Stretch stretch ->
                let where = Entity.terminalName model stretch.TerminalId |> Option.defaultValue stretch.Title
                same (sprintf "%s typed in %s" (Entity.actorName model stretch.Holder) where)
            // The file's own name, which is what the reader asked for. Not the path:
            // `artifacts/chart.png/0003-7f2a91` truncates to the part that says least.
            | PreviewSubject.Content ref -> same (ContentName.ofRef ref)
        // Showing a terminal is showing a terminal, whichever way it is asked. Its tab under a
        // preview takes the reader to that terminal as they left it — and from `all`, to
        // whichever read of it they were in.
        let showTerminal (terminal: TerminalId) =
            let mode =
                model.Pane
                |> Option.bind PaneMode.subject
                |> Option.filter (fun mode -> TerminalMode.terminal mode = terminal)
                |> Option.defaultValue (Reading terminal)
            dispatch (ShowInPaneMsg mode)
        let tabItem (view: TerminalView) =
            let terminal = view.TerminalId
            let activate () = showTerminal terminal
            // An item is a `div role="tab"` rather than a `button`: an item that carries a
            // control of its own (its ×) cannot be a button, and what a real button gave for
            // free was Enter and Space, so the item says them itself. The arrow walk is the
            // item's too, because the item knows which tab it is without asking the document:
            // the key is claimed here, where the event is, and where focus goes is `update`'s
            // (`ClientModel.tabWalk`).
            //
            // Delete is the ×'s press from the keyboard: it puts the tab away.
            let activateKey (pressed: Browser.Types.KeyboardEvent) =
                // Only keys pressed ON the item: Enter on its × is the ×'s, not a selection.
                let onTab = System.Object.ReferenceEquals (pressed.target, pressed.currentTarget)
                if onTab && (pressed.key = "Enter" || pressed.key = " ") then
                    // Space on a focused element scrolls the page.
                    pressed.preventDefault ()
                    activate ()
                elif pressed.key = "Delete" then
                    pressed.preventDefault ()
                    dispatch (DismissTabMsg terminal)
                elif TabStrip.claims pressed.key then
                    pressed.preventDefault ()
                    dispatch (WalkTabsMsg (pressed.key, ViewingTerminal terminal))
            terminalItem activate activateKey view
        // A file's tab: its preview is this tab's panel. Pressed, it shows the file in place;
        // its ×, or Delete, puts it away and the pane goes back to the terminal under it.
        let fileItem (file: ContentRef) =
            let on = shownFile = Some file
            let key = ClientModel.fileTabKey file
            let name = ContentName.ofRef file
            let klass = if not on || onAll then Style.pivotItem else Style.pivotItemOn
            let activate () = dispatch (ShowPreviewMsg (Preview.ofSubject (PreviewSubject.Content file)))
            let activateKey (pressed: Browser.Types.KeyboardEvent) =
                let onTab = System.Object.ReferenceEquals (pressed.target, pressed.currentTarget)
                if onTab && (pressed.key = "Enter" || pressed.key = " ") then
                    pressed.preventDefault ()
                    activate ()
                elif pressed.key = "Delete" then
                    pressed.preventDefault ()
                    dispatch (DismissFileTabMsg file)
                elif TabStrip.claims pressed.key then
                    pressed.preventDefault ()
                    dispatch (WalkTabsMsg (pressed.key, ViewingFile file))
            let dismiss =
                if not on then Lit.nothing
                else
                    let label = Dom.Text.dismissTab name
                    html $"""
                        <button type="button" class="{Style.pivotTabClose}" data-pane-tab-dismiss="{key}"
                                aria-label="{label}" title="{label}"
                                @click={Ev(fun (e: Browser.Types.Event) ->
                                              e.stopPropagation ()
                                              dispatch (DismissFileTabMsg file))}>{Icon.close}</button>"""
            html $"""
                <div role="tab" class="{klass}" data-pane-tab="{key}" data-file-tab="{ContentRef.value file}"
                     id="{Dom.paneTabId key}" aria-controls="{Dom.panePanelId}"
                     aria-selected="{if on then "true" else "false"}" tabindex="{stopAt key on}" title="{name}"
                     @keydown={Ev(Events.key activateKey)}
                     @click={Ev(fun _ -> activate ())}><span class="{Style.pivotName}">{name}</span>{dismiss}</div>"""
        // The door to every terminal and file: a button at the strip's head that makes the
        // list the pane's body, in place of whatever the pane was showing. Pressed again, or
        // Escape, goes back to that.
        //
        // Up, it wears the list's ground and runs through the strip's rule into it — the
        // list's tab — and the selected tab goes quiet, so the strip has one lit thing and it
        // is the thing on screen.
        let listButton =
            html $"""
                <button type="button" class="{if onAll then Style.paneListButtonOn else Style.paneListButton}"
                        data-pane-switcher aria-controls="{Dom.paneListId}"
                        aria-expanded="{if onAll then "true" else "false"}"
                        aria-label="{Dom.Text.switchTerminal}" title="{Dom.Text.switchTerminal}"
                        @click={Ev(fun _ -> dispatch ToggleSwitcherMsg)}>{Icon.list}</button>"""
        let previewBody (preview: Preview) =
            match preview.Subject with
            | PreviewSubject.Block (terminalId, blockId) -> paneBlockView model preview terminalId blockId
            | PreviewSubject.Stretch stretch -> paneStretchView model preview stretch
            | PreviewSubject.Content ref -> paneContentView ref
        // The way back to a terminal's newest output, for a reader who scrolled up through
        // it: the chat's float, over the scroller it brings back. On screen while that reader
        // is far back from the end (`ClientModel.offersLatest`).
        let jumpToLatest (surface: TailSurface) =
            html $"""
                <div class="{Style.terminalJumpToLatestSlot}" data-jump-to-latest="{TailSurface.key surface}"
                     ?hidden={not (ClientModel.offersLatest surface model)}>
                  <button type="button" class="{Style.terminalJumpToLatest}" aria-label="{Dom.Text.jumpToLatestOutput}"
                          title="{Dom.Text.jumpToLatestOutput}"
                          @click={Ev(fun _ -> dispatch (MoveMsg (DomMove.JumpToLatest surface)))}>{Icon.down}</button>
                </div>"""
        let terminalBody (view: TerminalView) =
            let feed = ClientModel.terminalFeed view.TerminalId model
            let truncated =
                if view.DroppedBytes > 0 then
                    html $"""<div class="{Style.terminalTruncated}" data-terminal-truncated="{string view.DroppedBytes}">{view.DroppedBytes} bytes dropped</div>"""
                else Lit.nothing
            let blocks =
                // An idle prompt IS the empty state a terminal-shaped surface already has a
                // symbol for; "nothing has run here yet" was the same fact as a sentence.
                //
                // On an OPEN terminal the command line now carries that prompt itself — the `$`
                // is its placeholder — so drawing a second one above it is one idle prompt too
                // many. On a closed one there is no command line, and the symbol is the only
                // thing left to say the surface is a terminal that ran nothing.
                if not (List.isEmpty view.Blocks) then
                    BlockGroup.ofBlocks feed view.Blocks
                    |> List.map (function
                        | BlockGroup.Alone block -> terminalBlockView dispatch model feed view.TerminalId true block
                        | BlockGroup.Run (leader, rest) -> terminalBlockRun dispatch model feed view.TerminalId leader rest)
                elif view.IsOpen then []
                else [ html $"""<div class="{Style.terminalOutputEmpty}"><span class="{Style.terminalPrompt}">$</span></div>""" ]
            // A terminal's two reads, and the ONE control between them (Plan 14, stage 7;
            // Plan 25, stage 3): its text — the live screen, or the blocks it ran — and its
            // recording. On a live terminal watching means going behind the edge while it
            // keeps running, and the way back says `Live`.
            //
            // It was four controls: two ways in at the top of the scrollback and two ways out
            // floating over it, each removing another from the document, each needing focus
            // handed on after it, and each named after the projection it mounted rather than
            // after anything a reader wants. One control that relabels in place is the same
            // act with none of that — and because it never leaves, the press keeps its focus
            // and the reader keeps their place.
            let rewound = ClientModel.isRewound view.TerminalId model
            // How far behind the edge a rewound reader is, in the recording's clock, growing
            // as it moves away from them. A fact rather than a control, so it stays where a
            // reader parked behind live will see it.
            let behindLabel =
                if not (view.IsOpen && rewound) then Lit.nothing
                else
                    let behind =
                        match ClientModel.behindLive view.TerminalId model with
                        | Some seconds when seconds >= 1.0 ->
                            Dom.Text.behindLive (Some (durationText (System.TimeSpan.FromSeconds seconds)))
                        | _ -> Dom.Text.behindLive None
                    html $"""
                        <div class="{Style.terminalBehindLine}">
                          <span class="{Style.terminalBehind}" data-terminal-behind="{TerminalId.value view.TerminalId}">{behind}</span>
                        </div>"""
            // In live mode the block history gives way to the SCREEN (Plan 14, stage 6). A
            // program is running here and what it displays is not a list of commands and
            // their output — the blocks are block mode's view of a terminal, and they come
            // back the moment the lease does. The transcript keeps both either way.
            let above =
                match terminalRead model view with
                | TerminalRead.Recording ->
                    // The recording, played — behind a live edge or after a closed one, the
                    // same mount over the same cast. Which is exactly what "rewound like live
                    // TV, through the same mechanism" has to mean, and the reason a closed
                    // terminal's player is here rather than in a section of its own.
                    let label =
                        if rewound then "Terminal recording, behind live" else "Terminal recording"
                    html $"""
                        <div class="{Style.terminalReplayRegion}">
                          {replayMount label (ClientModel.tabKey view.TerminalId)}
                          {behindLabel}
                        </div>"""
                // The screen, when it is what this terminal HAS to read — somebody holds the
                // keyboard, or there are no blocks to show instead. Gated on the lease alone, a
                // device nobody had taken rendered an empty block list beside a stream
                // arriving the whole time, and the only way to see it was to claim the
                // keyboard. Watching is not typing.
                //
                // The blocks give way to the screen — and so does their BOX. It used to stay
                // behind as an empty `flex-1` region holding only the truncation notice, so a
                // live terminal spent a third of its column (measured 291px of 844 on a phone)
                // on a container with nothing in it, and the surface the keyboard actually
                // types into got the same third. The notice is a line and now renders as one.
                | TerminalRead.Screen ->
                    html $"""
                        {truncated}
                        <div class="{Style.terminalTailRegion}">
                          {terminalScreenView actions dispatch model view.TerminalId view.Lease}
                          {jumpToLatest (TailSurface.Screen view.TerminalId)}
                        </div>"""
                | TerminalRead.Blocks ->
                    html $"""
                        <div class="{Style.terminalTailRegion}">
                          <div class="{Style.terminalScrollback}" data-terminal-scrollback
                               data-terminal-id="{TerminalId.value view.TerminalId}"
                               data-tail="{TailSurface.key (TailSurface.Blocks view.TerminalId)}">
                            <div class="{Style.terminalStream}" data-tail-entries>
                              {truncated}
                              {blocks}
                            </div>
                          </div>
                          {jumpToLatest (TailSurface.Blocks view.TerminalId)}
                        </div>"""
            html $"""
                {above}
                {if not view.IsOpen then terminalClosedBand model view
                 // Behind the live edge the terminal is still live and its queue and lease
                 // still stand, but this reader is watching a recording: the composer says so
                 // where the command line would be (`terminalComposer`).
                 else terminalComposer actions dispatch model view.TerminalId}"""
        let body () =
            match previewing, selected with
            // The list, when it is up — asked for, or because nothing is open: the list is
            // where everything here is opened or started from.
            | _ when onAll -> allPage actions dispatch model
            // A preview, laid over the selected terminal: the thing itself. Not the terminal's
            // composer — the reader is reading, not typing, and its close restores it.
            //
            // In the same panel box a terminal is shown in (`tabindex="-1"`, the ring, the id
            // every item names), so a chip's focus lands the same way whichever it opened.
            | Some preview, _ ->
                let key = PreviewSubject.key preview.Subject
                // Named by the tab it is laid over and then its own name, which is how the
                // screen says it: `term 2`, then `$ seq 1 40`.
                // A file is named by its own tab.
                let labelledBy =
                    match shownFile, selected with
                    | Some file, _ -> Dom.paneTabId (ClientModel.fileTabKey file)
                    | None, Some terminal when tabs |> List.exists (fun view -> view.TerminalId = terminal) ->
                        Dom.paneTabId (ClientModel.tabKey terminal) + " " + Dom.panePreviewNameId
                    | None, (Some _ | None) -> Dom.panePreviewNameId
                html $"""
                    <div class="{if lifted preview then Style.panePanelLifted else Style.panePanel}" role="tabpanel" tabindex="-1"
                         id="{Dom.panePanelId}" aria-labelledby="{labelledBy}" style="{if lifted preview then Style.liftedName else ""}"
                         data-pane-panel="{key}" data-pane-preview="{key}">
                      {previewBody preview}
                    </div>"""
            | None, None -> Lit.nothing
            | None, Some terminal ->
                let inner =
                    match Projection.tryFind terminal model.Terminals with
                    | Some view -> terminalBody view
                    | None -> Lit.nothing
                let key = ClientModel.tabKey terminal
                // `tabindex="-1"` so the panel can take focus programmatically when a chip
                // opens it, without becoming a Tab stop of its own. A DOM swap that leaves
                // focus on the control that vanished is the failure this exists to avoid.
                //
                // And it says so when it has it (`Style.panePanel`): focus a reader cannot see
                // is focus they do not have. Named by its item, which is how a screen reader
                // says whose panel this is.
                html $"""
                    <div class="{Style.panePanel}" role="tabpanel" tabindex="-1"
                         id="{Dom.panePanelId}" aria-labelledby="{Dom.paneTabId key}"
                         data-pane-panel="{key}">
                      {inner}
                    </div>"""
        // The band's second row (`Style.paneToolbar`): what is on screen, and the acts about
        // it. It used to be two things in two places — a caption under the pivot, and an
        // action row at the column's foot, under the composer, a long way from the name of
        // what its verbs act on. The `all` page has its verbs on its rows, and no toolbar.
        //
        // Under a preview the row is the PREVIEW's (F3): its name, its acts, and its close.
        // A preview's close. A command's settles it back into its place in its terminal — the
        // tab switched first when it lives in another — and highlighted there, as a MORPH: the
        // preview and the command are named alike (`Style.liftedName`), so the browser moves
        // the one into the other rather than swapping them. Anything else just closes.
        let closePreview (preview: Preview) =
            match ClientModel.closingPreview preview model with
            | ShowInTerminalMsg _ as settle -> actions.Morph (fun () -> dispatch settle)
            | other -> dispatch other
        let toolbar =
            match previewing, selected with
            | _ when onAll -> Lit.nothing
            | Some preview, _ ->
                let key = PreviewSubject.key preview.Subject
                let label, hint = previewLabel preview.Subject
                // A file's close is its tab's ×; a second one a line below would be one control
                // twice.
                let close =
                    if Option.isSome shownFile then Lit.nothing
                    else
                        html $"""
                            <button type="button" class="{Style.paneStripAct}" data-pane-preview-close
                                    aria-label="{Dom.Text.closePreview label}" title="{Dom.Text.closePreview label}"
                                    @click={Ev(fun _ -> closePreview preview)}>{Icon.close}</button>"""
                html $"""
                    <div class="{if lifted preview then Style.paneToolbarLifted else Style.paneToolbar}">
                      <div class="{Style.panePreviewHead}" data-pane-subtitle="preview">
                        <span class="{Style.panePreviewName}" id="{Dom.panePreviewNameId}" data-pane-preview-name="{key}"
                              title="{hint}">{label}</span>
                      </div>
                      {paneActionsView key (previewVerbs preview)}
                      {close}
                    </div>"""
            | None, Some terminal ->
                // Where it runs, then what it is doing. A terminal whose BLOCKS are the read has
                // its commands right under this, each with how it went, so it says only where
                // and whether anything is running; a screen or a recording says neither, so for
                // those this names what is running, or last ran, and how it went. Not while
                // rewound: the newest command is the live terminal's, and the reader is behind it.
                let says =
                    match Projection.tryFind terminal model.Terminals with
                    | None -> Lit.nothing
                    | Some view ->
                        let place =
                            match TerminalName.place view with
                            | Some where -> html $"""<span class="shrink-0">{where}</span>"""
                            | None -> Lit.nothing
                        let doing =
                            if terminalRead model view <> TerminalRead.Blocks && not (ClientModel.isRewound terminal model) then
                                match Projection.runningBlock view |> Option.orElse (List.tryLast view.Blocks) with
                                | Some block ->
                                    html $"""<span class="contents" data-pane-subtitle="terminal"><code class="{Style.panePivotSubtitleCommand}" title="{block.Command}">{BlockLabel.ofBlock block}</code><span class="shrink-0">{terminalBlockStatusAt model terminal block}</span></span>"""
                                | None -> Lit.nothing
                            elif not view.IsOpen then html $"""<span class="shrink-0">{Dom.Text.markClosed}</span>"""
                            elif Option.isSome (Projection.runningBlock view) then
                                html $"""<span class="shrink-0 text-green">{Dom.Text.blockRunning}</span>"""
                            else html $"""<span class="shrink-0">{Dom.Text.killIdle}</span>"""
                        html $"""{place}{doing}"""
                html $"""
                    <div class="{Style.paneToolbar}">
                      <div class="{Style.paneToolbarSays}">{says}</div>
                      {paneActionsView (ClientModel.tabKey terminal) (terminalVerbs dispatch model terminal)}
                    </div>"""
            | None, None -> Lit.nothing
        // The tabs, each in its own place (F2): terminals, then files. A command's preview is
        // named under the pivot, not in it (F3). In the order `ClientModel.stripTabs` gives,
        // which is the order the arrow walk moves along (`ClientModel.tabWalk`).
        let strip =
            ClientModel.stripTabs model
            |> List.choose (function
                | ViewingTerminal terminal -> Projection.tryFind terminal model.Terminals |> Option.map tabItem
                | ViewingFile file -> Some (fileItem file))
        let pivot =
            html $"""
                <div class="{Style.panePivotRow}">
                  {listButton}
                  <div class="{Style.panePivotList}" role="tablist" aria-label="{Dom.Text.paneItems}" data-pane-pivot>
                    <div class="{Style.panePivotScroller}" data-pane-strip>
                      {strip}
                    </div>
                  </div>
                  <button type="button" class="{Style.paneStripAct}" aria-label="{Dom.Text.backToChat}"
                          title="{Dom.Text.backToChat}" data-content-toggle="hide"
                          @click={Ev(fun _ -> dispatch ToggleContentMsg)}>{Icon.right}</button>
                </div>"""
        let split = ClientModel.paneSplit model |> Option.defaultValue PaneSplit.unmeasured
        html $"""
            <!-- `inert` while shut: a shut pane is zero pixels wide on a desktop and off the
                 screen on a phone, and every control in it was still a Tab stop — a full cycle
                 stopped eleven times on things nobody could see. From the model, so the first
                 paint (`Ssr`) carries it too; the root class (`PaneShell.setOpen`) is only the
                 animation. -->
            <!-- A phone's grab edge: the chat left showing beside the pane, under a scrim, and
                 the way back when pressed. A duplicate of the pivot's `›`, so out of the tree
                 and the Tab order (`Style.paneGrabEdge`). -->
            <button type="button" class="{Style.paneGrabEdge}" tabindex="-1" aria-hidden="true"
                    aria-label="{Dom.Text.backToChat}" data-pane-grab-edge
                    @click={Ev(fun _ -> dispatch ToggleContentMsg)}></button>
            <aside class="{Style.contentPanel}" data-content-panel ?inert={not model.TerminalsOpen}>
              <!-- The split, as a real separator: `aria-valuenow` and the arrow keys are what
                   make a splitter reachable without a pointer, and the shell keeps the value
                   in step (`PaneShell.installPaneResize`). -->
              <div class="{Style.terminalResize}" data-term-resize role="separator" tabindex="0"
                   aria-orientation="vertical" aria-label="Resize the content column"
                   aria-valuemin="{PaneSplit.narrowest}" aria-valuenow="{split.Width}" aria-valuemax="{split.Widest}"></div>
              <!-- Escape anywhere in the pane steps back one item: off the list of places to
                   the list, off `all` to what was on screen before it, or a preview down, as
                   its close does. -->
              <div class="{Style.terminalPane}"
                   @keydown={Ev(Events.key (fun e ->
                                    if e.key = "Escape" then
                                        if onAll && model.ChoosingStartIn then dispatch (ChoosingStartInMsg false)
                                        elif onAll then dispatch CloseSwitcherMsg
                                        else previewing |> Option.iter closePreview))}>
                {pivot}
                <div class="{Style.paneUnderStrip}">
                  {toolbar}
                  {refusalNotice dispatch model RefusalMount.Pane}
                  {body ()}
                </div>
              </div>
            </aside>"""

    /// The client shell, rendered into `#app`.
    ///
    /// The three panes are wrapped in a `display: contents` element carrying one class:
    /// whether anything is degraded. It changes no layout of its own (the panes remain
    /// `#app`'s flex children) and exists so the panes can make room on a phone for the bar
    /// that is fixed above them — which they must do only while it is there.
    let view (actions: ViewActions) (model: ClientModel) (dispatch: ClientMsg -> unit) : TemplateResult =
        let shellState = if (connectionReport model).IsSome then Style.degradedShell else ""
        let asked = model.Asked |> Map.toList |> List.map snd
        let surfaces =
            model.Synced.Surfaces |> Map.toList |> List.map (fun (id, surface) -> SurfaceView.view dispatch asked id surface)
        html $"""
            <div class="contents {shellState}">
            {sidebar actions dispatch model}
            <div class="{Style.mainColumn}">
              {degradedBar dispatch model}
              {header actions dispatch model}
              {signInPrompt dispatch model}
              {refusalNotice dispatch model RefusalMount.Chat}
              <div class="{Style.chatColumn}">
                <div class="{Style.launchArea}">
                  {chat actions dispatch model}
                  {askSlot actions dispatch model}
                </div>
                <div class="{Style.dock}">
                  {surfaces}
                  {queue dispatch model}
                  {drafts actions dispatch model}
                </div>
              </div>
            </div>
            {contentPane actions dispatch model}
            </div>"""
