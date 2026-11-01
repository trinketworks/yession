module Yession.Host.ManagerUi

// The management UI (Phase 4, Step 25): a deliberately server-side-rendered admin surface
// — list sessions with live status, create, open (which launches), stop, archive. Pure F# render
// functions produce full pages and FRAGMENTS from Fable.Lit templates (rendered to strings
// by our own `Ssr` wrapper — no session client, no Elmish, no Yjs); a small F# program of
// its own (`app/browser/ManagerPage.fs`, served from this Manager's asset set) swaps the
// fragments on stop/archive and takes live status from an SSE stream of rendered tables
// (`GET /sessions/rows`) — the server pushes, the page never polls. It
// shares the session client's `Style` (the same locally served /app.css), and — being
// online-only — is the natural home for server-side Lit SSR. This is not the collaborative
// client; it shares the Manager's 127.0.0.1 endpoint with the control RPC.

open Fable.Core.JsInterop
open Fable.NodeExtras
open Node.Api
open Node.Buffer
open Yession.Domain
open Yession.Domain.Link
open Yession.Domain.Tools
open Yession.Domain.Standings
open Yession.Manager
open Yession.Oidc
open Yession.App
open Yession.Host.Interop
open Lit

// --- Rendering (pure Lit templates, rendered to strings by Ssr) -------------------------

/// Both registries are FIXED-layout tables: the columns are declared once in the header
/// and every row obeys them, so what a row contains can never move a column — a launch
/// cannot squeeze `name` into a second line, and the two tables hang their controls on the
/// same right rail. The name column takes whatever is left.
///
/// What yields is decided by `phone:`, the shell's own layout question, and not by `max-md:`
/// — a reading column's width, which is a different question (docs/visual-design.md, Phone
/// and wide). The page's band answers `phone:`, so a column that answered `md:` beside it
/// disagreed with the head above it on a phone turned sideways.
module private Col =
    let table = "w-full text-left border-collapse table-fixed"
    /// Wide enough for a whole Crockford id — a half id identifies nothing, so this column is
    /// sized never to truncate. MEASURED, because the number depends on the face: 26 characters
    /// of 12px Monaspace Neon is 194px, plus the cell's own 16px gutter. It was 204px when the
    /// ids were set in whatever monospace a box happened to have, and the switch to a shipped
    /// face made them 6px too wide for it. The first thing to go on a phone.
    let id = "w-[210px] phone:hidden"
    /// The row's attention mark (`Style.recordMarkCell`): 32px, the mark on the page's rail
    /// and the name 32px in, at every width.
    let mark = "w-8"
    /// The session's state in words, as a column of its own on a wide screen, so every row's
    /// state starts on one rail and a scan down the column reads what the marks beside the
    /// names only rank. Wide enough for the longest state word (`failed to start`) and some
    /// of the tail after it — a running session's standings, a failed launch's reason —
    /// which clips rather than wraps. Yields on a phone, where the mark says the rank and
    /// the unfolded row says the words.
    let status = "w-[240px] phone:hidden"
    /// The MCP table's audience column.
    let reaches = "w-[256px] max-xl:w-[100px]"
    /// `2026-08-18 09:12Z` in the 12px mono face, plus the cell's gutter. MEASURED like the
    /// id beside it: 17 characters of 12px Monaspace Neon is 123px, and the caps header over
    /// it is narrower than that. Goes with the id on a phone — it is the sort KEY, and a sort
    /// you cannot see is still one you can read off the order of the rows.
    let created = "w-[144px] phone:hidden"
    /// What a yielding column's CELLS wear: the same answer about the phone as the head above
    /// them (`id`, `created`) and not the width, which a fixed-layout table takes from the head
    /// row alone. One value, so a column cannot yield in its head and stay in its body.
    let yields = "phone:hidden"
    /// The MCP table's rail, for its one verb. The session table has none: its verbs are in
    /// the row it unfolds (`rowTemplate`), so the name has the width a rail of Stop and an
    /// archive icon used to take on every row. Withdraw is bare (docs/visual-design.md,
    /// Hierarchy of controls), a word that fits in 100px with the cell's 16px gutter.
    let rail = "w-[100px]"
    /// The row's disclosure (`Style.recordChevronPress`): a thumb's 44px, LAST, so it is the
    /// table's own trailing edge at every width — the columns between it and the name all
    /// yield on a phone (`id`, `status`, `created`), but this one never does, which is the
    /// whole point of moving the press here from the mark beside the name.
    let chevron = "w-11"

/// The session's state, and beside it what the session says about ITSELF: the wide screen's
/// status column (`Col.status`). A phone reads the same words in the row's unfolded lines
/// (`rowSays`), behind the mark that ranks them.
///
/// A STATUS, in the product's one voice for one: the caps label step, in its role's colour
/// (docs/visual-design.md, Colour — "a status is text in its colour, and it leads with a
/// mark"). It was 13px lowercase with no mark, which is the voice of a sentence, and read as
/// something to press beside the bare verbs in the same row.
///
/// Running and stopped are the exception: this page's own roster already says "running" by the
/// row's own spin mark (`recordMarkCell`, at every width) and "stopped" by nothing under way at
/// all, so the word here carries no second mark of its own — plain text is unambiguous and a
/// mark beside it would only repeat what the rail already drew. Every other state here still
/// leads with one, because it is the only place that state is said at this width: `starting` is
/// a wait, so `ink-dim` with the spin mark; a launch that failed or an exit is WRONG, so it is
/// the one red word in its row, leading with a dot; `archived` one step further back again,
/// also a dot. The word is always there beside the colour, so nothing rests on colour alone
/// (WCAG 2.0 SC 1.4.1). The name above carries the same fact by weight of ink (`nameText`),
/// which is what lets a blurred screen find what is live.
///
/// The red words are counted per ROW, which is the surface a row's hover tone, its rail of
/// verbs and its one record make it. A registry of twenty exited sessions is twenty records,
/// and dimming all but the newest would be this page guessing which fault anybody came for.
///
/// Absent from the list, and deliberately: `port · pid · build`. That was plumbing — the
/// answer to "which process, running what" — and a diagnostic does not outrank the reason a
/// reader is scanning the column. The build is still on the wire the registry stream carries.
///
/// A longer line is CLIPPED rather than wrapped, because a row that changes height when a
/// session picks up a third pull request is a list you cannot keep your place in. The
/// clipping is the line's own (`truncate` on the block; `overflow` does nothing to an inline
/// span). The worst it can lose is the tail of a count; the state is first and always whole.
let private stateLine (view: ProcessManager.SessionView) : TemplateResult =
    let nothing = html $""""""
    // ONE shape for every state, so no state can arrive wearing a voice of its own: the status
    // word in its role's face, leading with its dot, then whatever this row has to add in the
    // line's own small voice. No block of its own: the cell carries the voice and the clipping
    // (`rowTemplate`).
    let dotMark (dot: string) = html $"""<span class="{dot}" aria-hidden="true"></span>"""
    let line (face: string) (mark: TemplateResult) (hook: string) (word: string) (rest: TemplateResult) =
        html $"""<span class="{face}" data-status="{hook}">{mark}{word}</span>{rest}"""
    match view.Record.ArchivedAt, view.Status with
    // An operator's decision, not a process state — but it belongs on this line, because
    // "archived" is the answer a reader wants here and "stopped" for a session that can no
    // longer start is true and useless. A step fainter than `stopped`: it is the one state
    // nothing can be done to without first being undone.
    | Some _, _ -> line Style.statusFaint (dotMark Style.statusDot) Dom.Manager.statusArchived "archived" nothing
    | None, ProcessManager.NotRunning -> line Style.statusWait nothing Dom.Manager.statusStopped "stopped" nothing
    | None, ProcessManager.Running _ ->
        // Each standing in the tone the SESSION gave it: the Manager never learns what a
        // standing is about, so a colour it chose would be one it guessed. Worst first, so
        // the clip at the line's end takes the least urgent. It exists in this arm alone
        // because standings are launch-scoped — a session that is not running has no work in
        // flight to describe.
        let standings =
            Standing.byConcern view.Standings
            |> List.map (fun s ->
                html $"""<span> · </span><span class="{Style.attentionInk s.Attention}" data-session-standing="{s.Facet}">{s.Word}</span>""")
        line Style.statusOk nothing Dom.Manager.statusRunning "running" (html $"""{standings}""")
    | None, ProcessManager.Launching ->
        line Style.statusWait Icon.spinLead Dom.Manager.statusStarting "starting" nothing
    | None, ProcessManager.LaunchFailed reason ->
        // The reason stays in the line's own voice: the red is the WORD's, and a sentence set
        // in it would be the red statement the doctrine allows one of, twice.
        line
            Style.statusErr
            (dotMark Style.statusDot)
            Dom.Manager.statusFailed
            "failed to start"
            (html $"""<span title="{reason}"> · {reason}</span>""")
    | None, ProcessManager.Exited code ->
        let reason = code |> Option.map string |> Option.defaultValue "signal"
        line Style.statusErr (dotMark Style.statusDot) Dom.Manager.statusExited (sprintf "exited (%s)" reason) nothing

/// What a row says when it unfolds, one line each, worst first: a running session's own
/// standings, or, for every other state, the state. Each is an attention, a word, and the
/// facet when it is a standing the SESSION said (`Dom.Manager.standing` marks only those).
/// The row's mark is the first line's, so the mark and the line it ranks first cannot
/// disagree.
///
/// A state is ranked by what it asks of a reader: a launch that failed or a crash is WRONG;
/// a launch under way is PENDING, a machine's work; a session at rest, stopped or archived, is REST. A running
/// session that has not yet said anything is at rest too — it has nothing in flight it has
/// told anyone about.
let private rowSays (view: ProcessManager.SessionView) : (Attention * string * string option) list =
    match view.Record.ArchivedAt, view.Status with
    | Some _, _ -> [ Rest, "archived", None ]
    | None, ProcessManager.NotRunning -> [ Rest, "stopped", None ]
    | None, ProcessManager.Running _ ->
        match Standing.byConcern view.Standings with
        | [] -> [ Rest, "running", None ]
        | standings -> standings |> List.map (fun s -> s.Attention, s.Word, Some s.Facet)
    | None, ProcessManager.Launching -> [ Pending, "starting", None ]
    | None, ProcessManager.LaunchFailed reason -> [ Wrong, sprintf "failed to start · %s" reason, None ]
    | None, ProcessManager.Exited code ->
        let reason = code |> Option.map string |> Option.defaultValue "signal"
        [ Wrong, sprintf "exited (%s)" reason, None ]

/// The name text, styled by state but carrying no link of its own — the cell around it
/// (`rowTemplate`) is the one hit area, covering this and `stateLine` both. A session that
/// has no way in (archived) renders the same step-back ink it always has; the distinction
/// that used to live in "is this an `<a>`" now lives in whether `rowTemplate` wraps the cell
/// at all.
///
/// The name carries the STATE, by weight of ink: full ink while the session runs, a step back
/// (`bodyDim`) when it is stopped, exited or archived. That is what lets the state line below
/// it be plain text — a scan down the column finds what is live without a colour saying so —
/// and it is a token rather than an opacity so the mark and the focus ring keep theirs.
let private nameText (view: ProcessManager.SessionView) : TemplateResult =
    match view.Record.ArchivedAt, view.Status with
    | Some _, _ -> html $"""<span class="{Style.bodyDim} {Style.recordRowNameText}">{view.Record.DisplayName}</span>"""
    | None, status ->
        let face =
            match status with
            | ProcessManager.Running _ -> Style.recordLink
            | ProcessManager.NotRunning
            | ProcessManager.Launching
            | ProcessManager.LaunchFailed _
            | ProcessManager.Exited _ -> Style.recordLinkQuiet
        html $"""<span class="{face} {Style.recordRowNameText}">{view.Record.DisplayName}</span>"""

/// The row's verbs, on the line its unfolding ends with: Stop while the session runs, Start
/// where it can be started without being opened, and — where the session is not already
/// archived — Archive. Opening is still launching, by the name; Start is for a reader who
/// wants the session up and wants to stay on this page.
///
/// They live in the unfolded row and not on a rail at the row's end, so a list at rest is
/// marks and names and nothing to press by mistake, and ending a session is two deliberate
/// presses — the mark, then Stop.
///
/// All are BORDERLESS. `Style`'s rule for a verb riding a listed row is exactly this case —
/// the row already carries the structure, so the verb borrows it, faint at rest and ink (or
/// err, for the one that ends something) under the hand. Stop used to be exempted as "the
/// lifecycle verb" and wore a rectangle; that put the loudest chrome on the rarest act, and on
/// a phone it cost the name 40px. With both verbs quiet, the page's one FILL is Create and its
/// one bordered rectangle is Declare, which is the hierarchy spent in the order of scarcity it
/// asks for (docs/visual-design.md, Hierarchy of controls).
///
/// Stop and Start lead with `Icon.stop`/`Icon.play`, the same two glyphs the chat's interrupt
/// and a closed terminal's replay wear — one shape for "end the thing running" and one for
/// "run the thing again" wherever either is offered, this row included. Archive and Unarchive
/// stay bare words: neither owns a glyph anywhere else, and one would be invented just for
/// this row.
///
/// An ARCHIVED row offers Unarchive and nothing else. The lifecycle verb comes first and
/// Archive after it, so Stop and Start take turns in one place: a press on Stop that lands
/// leaves Start under the same finger, which is the act that undoes it, and never Archive.
///
/// Every one of them NAMES THE SESSION it acts on. A column of "Stop" says nothing about which
/// row you are on, which is the same floor criterion the archive icon has always answered
/// (WCAG 2.0 SC 2.4.4), and the accessible name leads with the visible word so the two agree.
let private actions (view: ProcessManager.SessionView) : TemplateResult =
    let id = SessionId.value view.Record.SessionId
    let name = view.Record.DisplayName
    // Each control carries the address it posts to, spelled by the server: the script that
    // presses it reads the attribute and builds nothing.
    let posts (verb: SessionVerb) = ManagerRoute.path (ManagerRoute.Session (view.Record.SessionId, verb))
    match view.Record.ArchivedAt with
    | Some _ ->
        html $"""<button type="button" class="{Style.btnBare}" data-unarchive="{id}" data-post="{posts SessionVerb.Unarchive}"
                         aria-label="Unarchive {name}">Unarchive</button>"""
    | None ->
        let verb =
            match view.Status with
            | ProcessManager.Running _ ->
                html $"""<button type="button" class="{Style.btnBareDanger}" data-stop="{id}" data-post="{posts SessionVerb.Stop}"
                                 aria-label="Stop {name}">{Icon.stop}Stop</button>"""
            | ProcessManager.NotRunning
            | ProcessManager.LaunchFailed _
            | ProcessManager.Exited _ ->
                html $"""<button type="button" class="{Style.btnBare}" data-start="{id}" data-post="{posts SessionVerb.Launch}"
                                 aria-label="Start {name}">{Icon.play}Start</button>"""
            // Nothing to stop until there is a child, and nothing to start while one is
            // coming up: a launch in flight ends on its own bound, either way.
            | ProcessManager.Launching -> html $""""""
        html $"""{verb}<button type="button" class="{Style.btnBare}" data-archive="{id}" data-post="{posts SessionVerb.Archive}"
                         aria-label="Archive {name}">Archive</button>"""

/// When the session was registered — the key the registry is ordered by, so it is shown.
///
/// ABSOLUTE, not relative. A render function that said "3d ago" would need a clock threaded
/// through the whole render layer, and these frames are pushed only when something CHANGES,
/// so a "2m ago" would sit on the screen going quietly wrong for hours. UTC, marked `Z` so it
/// is never ambiguous, with the full offset-bearing timestamp on the `<time>` for anything
/// that reads it properly.
let private createdView (at: System.DateTimeOffset) : TemplateResult =
    let shown = sprintf "%04d-%02d-%02d %02d:%02d" at.Year at.Month at.Day at.Hour at.Minute
    let iso = at.ToString "o"
    html $"""<time datetime="{iso}" title="{iso}">{shown}Z</time>"""

/// One session row — an action's swap unit: a stop replaces it wholesale, so the markup is
/// always a pure function of the Manager's current view. (Live status replaces the whole table
/// instead; see the rows stream.) The human name leads (content is the interface), then the
/// state; the minted id is plumbing, faint mono, and yields on narrow screens with the created
/// column. The row leads with its attention mark, so it reads mark → name → state.
///
/// At rest a row is ONE line at every width, and the same height whatever its state: the
/// mark on the page's rail (`Col.mark`), then the name; a wide screen adds the state in words
/// as a column of its own (`Col.status`), so every row's state starts on one rail. The
/// table is fixed-layout (`Col`) and each text cell clips rather than wraps. Rows that jump
/// as processes start and stop make a list you cannot keep your place in.
///
/// The chevron is the press. It lives in its own column, LAST (`Col.chevron`), so it is the
/// row's trailing edge at every width — the columns between it and the name yield on a phone,
/// this one never does. It UNFOLDS the row, under the name: one line per thing the row says
/// (`rowSays`), each mark under the row's mark and each word on the name's rail, and then
/// the verbs. The first line's mark IS the row's own attention mark, riding down to it on a
/// faint track (`Style.recordMarkRider`), so what the mark ranked first is never drawn twice —
/// that mark, beside the name, carries attention's colour alone now and answers to no press of
/// its own. The server always renders a row folded; which row is open is the page's own state
/// (`ManagerPage.fs`), carried across every frame the rows stream swaps in.
///
/// The id is stated ONCE. An unnamed session's name IS its id (`SessionRecord.isNamed`), so
/// it is already on the row as the name cell's link, and the id cell beside it is left empty
/// rather than repeating it verbatim — two columns carrying one value is a row that reads as
/// two facts. A named session shows both, because then they are two. The cell stays, empty:
/// the table is fixed-layout, and a row that dropped a cell would move every one after it.
///
/// The name's hit area is its whole line, not just the name's own glyphs — one `<a>`
/// (`Style.recordRowLink`) wraps it when there is somewhere to go, the same open route the
/// name always linked to. An archived row has no way in (`/open` refuses it, and a link whose
/// only outcome is a refusal is worse than no link), so its name stays unwrapped.
let private rowTemplate (view: ProcessManager.SessionView) : TemplateResult =
    let id = SessionId.value view.Record.SessionId
    let name = view.Record.DisplayName
    let idCell = if SessionRecord.isNamed view.Record then id else ""
    let says = rowSays view
    let attention, first, _ = List.head says
    let line = nameText view
    let nameLine =
        match view.Record.ArchivedAt with
        | Some _ -> line
        | None ->
            let openUrl = ManagerRoute.path (ManagerRoute.OpenSession view.Record.SessionId)
            html $"""<a class="{Style.recordRowLink}" href="{openUrl}" target="_blank" data-open>{line}</a>"""
    // The first line's mark is the row's own, which rides down to it (`Style.recordMarkRider`);
    // its seat in the line stays, empty, so the word keeps the name's rail.
    let saidLine (index: int) (attention: Attention, word: string, facet: string option) =
        let word =
            match facet with
            | Some facet ->
                html $"""<span class="{Style.cls [ Style.recordDetailWord; Style.attentionInk attention ]}" data-session-standing="{facet}">{word}</span>"""
            | None ->
                html $"""<span class="{Style.cls [ Style.recordDetailWord; Style.attentionInk attention ]}">{word}</span>"""
        if index = 0 then html $"""<span></span>{word}""" else html $"""{Icon.attention attention}{word}"""
    let detailId = sprintf "row-detail-%s" id
    // The chevron's name says the session and what it ranked first: the press is the one
    // thing a screen reader meets on a folded row besides the name.
    let markName = sprintf "%s: %s" name first
    html $"""
        <tr class="{Style.tableRow}" data-session="{id}">
          <td class="{Style.recordMarkCell} {Col.mark}">
            <span class="{Style.recordMarkSeatWrap}" aria-hidden="true"><span class="{Style.recordMarkSeat}"></span><span class="{Style.recordMarkTrack}"></span><span class="{Style.recordMarkRider}">{Icon.attention attention}</span></span>
          </td>
          <td class="{Style.tableCell}" title="{name}">
            {nameLine}
            <div class="{Style.recordDetail}" id="{detailId}" data-row-detail>
              <div class="{Style.recordDetailInner}">
                <div class="{Style.recordDetailLines}">
                  {says |> List.mapi saidLine}
                  <div class="{Style.recordDetailVerbs}">{actions view}</div>
                </div>
              </div>
            </div>
          </td>
          <td class="{Style.tableCell} {Style.small} truncate {Col.status}">{stateLine view}</td>
          <td class="{Style.tableCell} {Style.monoFaint} truncate {Col.yields}">{idCell}</td>
          <td class="{Style.tableCell} {Style.monoFaint} tabular-nums truncate {Col.yields}">{createdView view.Record.CreatedAt}</td>
          <td class="{Style.tableCell} {Col.chevron}">
            <button type="button" class="{Style.recordChevronPress}" data-row-mark="{id}" aria-expanded="false"
                    aria-controls="{detailId}" aria-label="{markName}"><span class="{Style.recordChevronMark}">{Icon.right}</span></button>
          </td>
        </tr>"""

/// One filter chip, wearing the COUNT of what it filters. A real `<a>`, because the filter
/// IS the page's location: it lives in the URL, a bookmark restores it, and the back button
/// undoes it. That makes it keyboard-operable and focusable with no help.
///
/// The count is what tells a reader there is anything behind an unlit chip — without it
/// `archived` was a door with no window, opened to find out — and it is the honest total for
/// the state, not the number of rows on screen. Beside it, off-screen, the state the chip is
/// in ("shown" / "hidden"): a link's name should say what following it does, and a lit chip
/// differs from an unlit one by a ground step nothing but an eye can read. Not `aria-current`,
/// which names THE current item of a set and was being claimed by two chips at once.
///
/// The last lit chip is not a control. `SessionQuery.toggling` has nowhere for it to go — a
/// list is never asked to show nothing — so it renders as the lit word and count with no
/// href, rather than as a link that leads to an empty state saying to come back.
///
/// Its href is computed HERE, server-side, from `SessionQuery.toQueryString` — the page's script
/// never builds a URL, so there is exactly one encoder and a chip can never link somewhere the
/// rows beside it disagree with.
let private filterChip (query: SessionQuery) (count: int) (state: ArchiveState) : TemplateResult =
    let shown = Shown.contains state query.Show
    let word, key =
        match state with
        | Active -> "active", "show-active"
        | Archived -> "archived", "show-archived"
    let face =
        if shown then Style.filterChipOn
        elif count = 0 then Style.filterChipEmpty
        else Style.filterChipOff
    let standing = if shown then "shown" else "hidden"
    let label =
        html $"""{word}<span class="{Style.filterChipCount}">{count}</span><span class="sr-only">, {standing}</span>"""
    match SessionQuery.toggling state query with
    | Some target ->
        let href = sprintf "?%s" (SessionQuery.toQueryString target)
        html $"""<a class="{face}" href="{href}" data-filter="{key}">{label}</a>"""
    | None -> html $"""<span class="{face}" data-filter="{key}">{label}</span>"""

// The swap unit is the whole section (filters with their counts, table), so the counts, the
// empty state and the controls can never go stale against the rows they describe. Which is
// also why this takes the QUERY and does the filtering itself: a caller that filtered on its
// own could hand these chips a list they do not describe.
let private tableTemplate
    (query: SessionQuery)
    (all: ProcessManager.SessionView list)
    : TemplateResult =
    let views = SessionQuery.apply query (fun (v: ProcessManager.SessionView) -> v.Record) all
    let countOf (state: ArchiveState) =
        all |> List.filter (fun (v: ProcessManager.SessionView) -> SessionQuery.stateOf v.Record = state) |> List.length
    // Nothing to show has two quite different causes, and saying the wrong one sends somebody
    // looking for a bug. An empty REGISTRY is "no sessions yet"; a filter that hid everything
    // says so, and names what it is hiding, with the chip that reveals it directly above.
    let emptyWord =
        match all with
        | [] -> "no sessions yet"
        | _ ->
            let hidden = List.length all - List.length views
            sprintf "no sessions match (%d hidden)" hidden
    let rows =
        match views with
        | [] ->
            [ html $"""
                <tr>
                  <td colspan="6" class="{Style.tableEmpty}">{emptyWord}</td>
                </tr>""" ]
        | views -> views |> List.map rowTemplate
    // The `created` header IS the sort control — the canonical accessible table sort, and it
    // spends no chrome anywhere else on the page. When last-activity or a summary arrives, its
    // header becomes the second one of these and nothing here has to be redesigned.
    let sortHref = sprintf "?%s" (SessionQuery.toQueryString (SessionQuery.reversed query))
    // The direction is a MARK, not a text glyph: `↑`/`↓` are carried by no face this build
    // ships, so each rendered as whatever the reader's system substituted — and a glyph where
    // an `Icon` mark exists is the second way one job gets drawn (docs/visual-design.md,
    // principle 5). The marks carry their own `aria-hidden`; what states the sort is the
    // `aria-sort` on the `<th>` around this.
    let sortMark, sortedBy =
        match query.Order with
        | NewestFirst -> Icon.downSm, "descending"
        | OldestFirst -> Icon.upSm, "ascending"
    // Create sits on the section's own header line, at the right: the list and the one act
    // that adds to it are one thing, and this is the page's one FILLED rectangle — its one
    // verb — so it does not need a section of its own to be found. It takes nothing but the
    // press — the id is minted server-side (a Docker-safe Crockford one) and a session is NAMED from
    // inside itself, in the title field at the top of its own header, which reports back
    // here over the control channel. Asking for the name here as well made two naming
    // surfaces out of one fact: what was typed on this page never reached the session.
    //
    // A real form and a real POST, not intercepted by the script: the browser follows the
    // answer's redirect into the new session. Swapping a table in instead would leave this
    // page in charge of an act whose whole point is to leave it — and every other page learns
    // about the new session from the rows stream anyway.
    html $"""
        <section class="flex flex-col gap-3" data-sessions data-stream="{ManagerRoute.path ManagerRoute.SessionRows}">
          <!-- No "sessions" label: the chips name the content and the band names the page, so
               chips and Create share one line at every width (three stacked lines of caps
               before the first row was the wall of text docs/visual-design.md, Type, warns of).
               The chip group pulls back by its own padding so the lit word sits on the
               column's rail, as a selected pivot tab does. -->
          <div class="flex items-center gap-x-2.5">
            <div class="flex items-center gap-1.5 -ml-2" role="group" aria-label="Show sessions">
              {filterChip query (countOf Active) Active}
              {filterChip query (countOf Archived) Archived}
            </div>
            <form class="ml-auto" method="post" action="{ManagerRoute.path ManagerRoute.CreateSession}" data-create-session>
              <button type="submit" class="{Style.btnPrimarySwap}" data-press>
                <span class="{Style.whenReady}">Create</span><span class="{Style.whenBusy}">Creating…</span>
              </button>
            </form>
          </div>
          <table class="{Col.table}">
            <!-- On a phone the only head left standing would be `name`, over a column whose
                 content is plainly a name, so the head row yields with the columns it labels. -->
            <thead class="phone:hidden">
              <tr class="{Style.tableRule}">
                <th scope="col" class="{Col.mark}"><span class="sr-only">attention</span></th>
                <th scope="col" class="{Style.tableHead}">name</th>
                <th scope="col" class="{Style.tableHead} {Col.status}">status</th>
                <th scope="col" class="{Style.tableHead} {Col.id}">id</th>
                <th scope="col" class="{Style.tableHead} {Col.created}" aria-sort="{sortedBy}">
                  <a class="{Style.sortHeader}" href="{sortHref}" data-filter="sort">created {sortMark}</a>
                </th>
                <th scope="col" class="{Col.chevron}"><span class="sr-only">expand</span></th>
              </tr>
            </thead>
            <tbody>{rows}</tbody>
          </table>
        </section>"""

/// A rendered fragment (a single row), served as an action's answer.
let sessionRow (view: ProcessManager.SessionView) : string =
    Ssr.render (rowTemplate view)

/// A rendered fragment (the whole table), served after a create or an archive and pushed on
/// the rows stream. Takes the reader's QUERY and filters with it, so the chips it draws and
/// the rows under them are one render and cannot describe different lists.
let sessionsTable (query: SessionQuery) (views: ProcessManager.SessionView list) : string =
    Ssr.render (tableTemplate query views)

/// One declared MCP server (Plan 17). The AUDIENCE is a column rather than a separate
/// table, because host-wide and session-scoped declarations are the same kind of fact and
/// splitting them would make "which of these does session A get?" a question you answer by
/// reading two places.
let private mcpRowTemplate (views: ProcessManager.SessionView list) (declaration: McpDeclaration) : TemplateResult =
    let name = McpServerName.value declaration.Server.Name
    let audience, audienceValue =
        match declaration.Audience with
        | AnySession -> "any session", ""
        | OneSession id ->
            let label =
                views
                |> List.tryFind (fun v -> v.Record.SessionId = id)
                |> Option.map (fun v -> v.Record.DisplayName)
                |> Option.defaultValue (SessionId.value id)
            label, SessionId.value id
    // Withdraw is a verb RIDING this row, so it is bare, and reddening rather than red at rest
    // (docs/visual-design.md, Hierarchy of controls — a bordered button is a standalone act on
    // the ground). It was a bordered err rectangle stretched across a 128px column of its own:
    // the loudest chrome on the page, on every row, for the act nobody came here to do — while
    // the session rows beside it said Stop as a word.
    html $"""
        <tr class="{Style.tableRow}" data-mcp-server="{name}">
          <td class="{Style.tableCell} {Style.body} truncate" title="{name}">{name}</td>
          <td class="{Style.tableCell} {Style.monoFaint} truncate {Col.yields}"
              title="{McpTransport.describe declaration.Server.Transport}">{McpTransport.describe declaration.Server.Transport}</td>
          <td class="{Style.tableCell} {Style.small} truncate" title="{audience}">{audience}</td>
          <td class="{Style.tableCellRail}">
            <div class="{Style.tableRailVerbs}">
              <button type="button" class="{Style.btnBareDanger}" data-mcp-withdraw="{name}" data-mcp-audience="{audienceValue}"
                      data-post="{ManagerRoute.path ManagerRoute.WithdrawMcpServer}" aria-label="Withdraw {name}">Withdraw</button>
            </div>
          </td>
        </tr>"""

/// The whole MCP section — the swap unit, so the count and the empty state can never go
/// stale against the rows they describe. This is the ONE place a url is ever written: a
/// session does not select from these, and the agent has no command that changes them.
let private mcpTemplate (views: ProcessManager.SessionView list) (declarations: McpDeclaration list) : TemplateResult =
    // Nothing declared draws NO TABLE: three column heads, a count and a centred sentence over
    // an empty body is chrome describing nothing (principle 1 — remove the element and the
    // content is no harder to see). What is left is the sentence and the form that fills it.
    let table =
        match declarations with
        | [] -> html $"""<p class="{Style.small}">no MCP servers. A session reaches only its own tools.</p>"""
        | declarations ->
            html $"""
              <table class="{Col.table}">
                <thead>
                  <tr class="{Style.tableRule}">
                    <th scope="col" class="{Style.tableHead}">name</th>
                    <th scope="col" class="{Style.tableHead} {Col.id}">address</th>
                    <th scope="col" class="{Style.tableHead} {Col.reaches}">reaches</th>
                    <th scope="col" class="{Style.tableHeadRail} {Col.rail}"><span class="sr-only">actions</span></th>
                  </tr>
                </thead>
                <tbody>{declarations |> List.map (mcpRowTemplate views)}</tbody>
              </table>"""
    let options =
        views
        |> List.map (fun view ->
            html $"""<option value="{SessionId.value view.Record.SessionId}">{view.Record.DisplayName}</option>""")
    html $"""
        <section class="flex flex-col gap-3" data-mcp>
          <div class="flex items-baseline gap-2.5">
            <span class="{Style.label}">MCP servers</span>
            <span class="{Style.labelCount}">{List.length declarations}</span>
          </div>
          {table}
          <form class="flex flex-col gap-3 pt-3" method="post" action="{ManagerRoute.path ManagerRoute.DeclareMcpServer}" data-declare-mcp>
            <div class="flex flex-wrap items-end gap-3">
              <div class="flex flex-col gap-1.5">
                <label class="{Style.label}" for="mcp-name">name</label>
                <input id="mcp-name" name="name" placeholder="serial" autocomplete="off" required
                  class="{Style.fieldOf "w-40 max-w-full"}">
              </div>
              <div class="flex flex-col gap-1.5">
                <label class="{Style.label}" for="mcp-url">address</label>
                <input id="mcp-url" name="url" type="url" placeholder="http://127.0.0.1:7333" autocomplete="off" required
                  class="{Style.fieldOf "w-72 max-w-full"}">
              </div>
              <div class="flex flex-col gap-1.5">
                <label class="{Style.label}" for="mcp-audience">reaches</label>
                <div class="{Style.fieldSelectWrapOf "w-56 max-w-full"}">
                  <select id="mcp-audience" name="session" class="{Style.fieldSelect}">
                    <option value="">any session</option>
                    {options}
                  </select>
                  <span class="{Style.fieldSelectMark}">{Icon.caret}</span>
                </div>
              </div>
              <button type="submit" class="{Style.btn}">Declare</button>
            </div>
            <p class="{Style.small}" data-mcp-error aria-live="polite"></p>
          </form>
        </section>"""

/// The hook endpoints this deployment serves, and the secret each one accepts.
///
/// READ-ONLY, and there is no form beside it: endpoints are deployment configuration, which
/// is where the rest of this deployment's topology already lives. What this section exists
/// for is the one thing configuration cannot do — show the operator a secret the Manager
/// GENERATED, so they can paste it into the provider they are already registering.
///
/// The secret is on the page rather than behind a reveal. This page is the operator's own,
/// behind whatever the deployment's authentication strategy is, and a secret you have to
/// click to see is one you copy wrong.
///
/// Beside it, the option that produced the endpoint (`EndpointSpec.encode`). Rotating is
/// bumping a counter, and that counter appears NOWHERE else — not in the address, not in the
/// secret, not in the header — so an operator reading this page had no way to tell which
/// rotation they were on or what to write for the next one. Canonicalised rather than
/// echoed: this is a line to copy, not a transcript of what somebody typed.
let private hooksTemplate (access: PublicAccess) (endpoints: WebhookRelay.HookEndpoint list) : TemplateResult =
    let origin =
        match PublicAccess.managerUrl access with
        | Some url -> url
        // Loopback: honest about it rather than printing an address and letting the
        // operator discover that github.com cannot reach 127.0.0.1.
        | None -> "http://127.0.0.1 (not reachable from outside this machine)"
    let rows =
        endpoints
        |> List.map (fun endpoint ->
            let secrets =
                endpoint.Secrets
                |> List.mapi (fun index secret ->
                    let label = if index = 0 then "current" else "previous"
                    html $"""
                        <div class="flex items-baseline gap-2.5">
                          <span class="{Style.label}">{label}</span>
                          <code class="{Style.monoFaint} break-all">{secret}</code>
                        </div>""")
            // `tableRule` and not `tableRow`: nothing on this row can be pressed, and a
            // surface that lifts under the hand promises a press that is not there.
            html $"""
                <tr class="{Style.tableRule}" data-hook-endpoint="{endpoint.Name}">
                  <td class="{Style.tableCellTop} {Style.body}">{endpoint.Name}</td>
                  <td class="{Style.tableCellTop} {Style.monoFaint} break-all">{origin}/hooks/{endpoint.Name}</td>
                  <td class="{Style.tableCellTop} {Style.monoFaint} break-all" data-hook-declared>--webhook {WebhookRelay.EndpointSpec.encode endpoint.Declared}</td>
                  <td class="{Style.tableCellRail}">{secrets}</td>
                </tr>""")
    html $"""
        <section class="flex flex-col gap-3" data-hooks>
          <div class="flex items-baseline gap-2.5">
            <span class="{Style.label}">hook endpoints</span>
            <span class="{Style.labelCount}">{List.length endpoints}</span>
          </div>
          <table class="{Col.table}">
            <thead>
              <tr class="{Style.tableRule}">
                <th scope="col" class="{Style.tableHead}">name</th>
                <th scope="col" class="{Style.tableHead}">deliver to</th>
                <th scope="col" class="{Style.tableHead} {Col.reaches}">declared as</th>
                <th scope="col" class="{Style.tableHeadRail}">secret</th>
              </tr>
            </thead>
            <tbody>{rows}</tbody>
          </table>
        </section>"""

/// The MCP section, rendered as an action's answer.
let mcpSection (views: ProcessManager.SessionView list) (declarations: McpDeclaration list) : string =
    Ssr.render (mcpTemplate views declarations)

/// The band's CONTENTS, as one template: the lockup, at every width. Shared by the registry
/// and by the standalone pages under it, because a
/// refusal that drew its own head is how the two became two designs — one with a band and a
/// rail, the other a 32rem column with 4rem of air over it and nothing of the product on it.
let private bandLockup : TemplateResult =
    // No page word beside the lockup: the <title> says "manager", and the page shows it.
    html
        $"""<h1 class="{Style.pageBandLockup}"><span class="{Style.pageBandMark}" aria-hidden="true">{Brand.mark}</span><span class="{Style.pageBandWordmark}">yession</span></h1>"""

// The page keeps the workspace anatomy: the band every head in the product shares
// (`Style.pageBand` — `spacing-band`, and one thumb-sized row on a phone, hairline below),
// then labelled sections on one rail (`Style.pageRail`). The body shell is `Style.app` (the
// visible viewport's height, overflow-hidden), so <main> owns the scrolling — a long registry
// scrolls under a fixed viewport instead of clipping.
let private bodyTemplate
    (access: PublicAccess)
    (query: SessionQuery)
    (views: ProcessManager.SessionView list)
    (declarations: McpDeclaration list)
    (hooks: WebhookRelay.HookEndpoint list)
    : TemplateResult =
    let hooksSection =
        if List.isEmpty hooks then html $""
        else html $"""<div class="pb-10">{hooksTemplate access hooks}</div>"""
    html $"""
        <main class="flex-1 min-w-0 overflow-y-auto flex flex-col">
          <!-- A BAND, running edge to edge of the viewport; only what is in it keeps to the
               rail. Inset to the rail, it was a card whose edges stopped mid-canvas against
               the black page — 168px short of each side at 1440. It is sticky over the
               registry that scrolls under it, and opaque: the one band in the product that
               was frosted grey where every other is black.
               On a phone the band is one thumb-sized row, and the lockup steps down into it
               (`Style.pageBandMark`, `Style.pageBandWordmark`). -->
          <header class="{Style.pageBand}">
            <div class="{Style.pageRail} w-full">{bandLockup}</div>
          </header>
          <div class="{Style.pageRail} flex-1 flex flex-col">
            <div class="pt-6 pb-10">{tableTemplate query views}</div>
            <div class="pb-10">{mcpTemplate views declarations}</div>
            <!-- Only when there are any: a deployment that declared no hook endpoints has
                 nothing to say here, and an empty table would imply a thing to fill in. -->
            {hooksSection}
          </div>
          <!-- The Manager's own build, at the foot of the page in the same mark a session's
               sidebar uses for its own (`Style.buildMark`). It matters because the Manager is
               the one process that CANNOT roll forward on its own: it keeps the image it
               exec'd until something restarts it, so it is routinely the oldest Yession
               running. The content above is `flex-1`, so on a short page this sits at the
               viewport's foot, and on a long one after the last section. -->
          <footer class="{Style.pageFoot}">
            <span class="{Style.buildMark}" data-manager-build>yession {Version.current}</span>
          </footer>
        </main>"""

/// `styleSheetUrl` and `programUrl` are passed in rather than read from the module below: F#
/// scopes top-down, and every asset's address is derived from bytes read further down the file.
///
/// The page's interactivity — fragment swaps on stop/archive and the MCP acts, the rows stream,
/// focus kept through both — is a program of its own (`app/browser/ManagerPage.fs`), served
/// from this Manager's asset set like the stylesheet: local, and never a program in a string.
let page
    (styleSheetUrl: string)
    (programUrl: string)
    (access: PublicAccess)
    (query: SessionQuery)
    (views: ProcessManager.SessionView list)
    (declarations: McpDeclaration list)
    (hooks: WebhookRelay.HookEndpoint list)
    : string =
    String.concat "" [
        "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">"
        "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">"
        "<title>Yession Manager</title>"
        Style.headTags styleSheetUrl
        WebApp.managerHeadTags (ManagerRoute.path ManagerRoute.Manifest) (ManagerRoute.path ManagerRoute.Icon) (ManagerRoute.path ManagerRoute.Favicon)
        sprintf "</head><body class=\"%s\">" Style.app
        Ssr.render (bodyTemplate access query views declarations hooks)
        sprintf "<script type=\"module\" src=\"%s\"></script>" (Ssr.escapeAttr programUrl)
        "</body></html>"
    ]

// --- Routing ----------------------------------------------------------------------------

/// One field of a form-encoded body: `None` when the form did not name it, `Some ""` when it
/// named it and left it blank. Those are different submissions — a select whose first option
/// is "any session" posts `session=` — and the decoders below are where each gets its meaning.
let private formField (body: string) (name: string) : string option =
    (Node.Api.URLSearchParams.Create body).get name

/// Which sessions a server's form says it reaches. The field is REQUIRED: the page always
/// posts it, empty meaning every session, so a body without one is not that choice made —
/// it is a caller who did not say, and reading it as `AnySession` would declare a server for
/// (or withdraw the one that covers) every session on the strength of a typo.
let private audienceOfForm (body: string) : Result<McpAudience, string> =
    match formField body "session" with
    | None -> Error "the form did not say which sessions the server reaches (an empty session field means every session)"
    | Some "" -> Ok AnySession
    | Some id -> SessionId.create id |> Result.map OneSession

let private serverNameOfForm (body: string) : Result<McpServerName, string> =
    match formField body "name" with
    | None -> Error "the form did not name the MCP server"
    | Some name -> McpServerName.create name

/// The form that declares an MCP server: a name, an address, who it reaches, and optionally a
/// sentence for the humans. Pure, so what a body means is answered without a Manager.
let declarationOfForm (body: string) : Result<McpDeclaration, string> =
    match serverNameOfForm body, audienceOfForm body with
    | Error e, _
    | _, Error e -> Error e
    | Ok name, Ok audience ->
        match formField body "url" with
        | None -> Error "the form did not give the MCP server an address"
        | Some "" -> Error "an MCP server needs an address"
        | Some url ->
            Ok
                { Server =
                    { Name = name
                      Transport = McpHttp url
                      // No description is a legitimate declaration, so absent and blank agree.
                      Description =
                        (match formField body "description" with
                         | None
                         | Some "" -> None
                         | Some description -> Some description) }
                  Audience = audience }

/// The form that withdraws one declaration: its name and the audience it was declared for,
/// because the same name may be declared for one session and for every session.
let withdrawalOfForm (body: string) : Result<McpServerName * McpAudience, string> =
    match serverNameOfForm body, audienceOfForm body with
    | Error e, _
    | _, Error e -> Error e
    | Ok name, Ok audience -> Ok (name, audience)

/// The same static asset service the Session runs, over this process's OWN set — read
/// and addressed once at boot rather than per request, so every render of this page (it is
/// rendered per request) names the same bytes. Where the set lives is `Assets`' own business,
/// which is also why this page can link a stylesheet whose faces this file has never heard of.
let private assets = Assets.configured ()

let private cssUrl = ManagerRoute.path (ManagerRoute.asset assets.Build AssetFile.``app``)

/// The Manager page's program, from the same set and addressed the same way as the stylesheet.
/// (The opening screen's is not served: see `openingProgram`.)
let private managerProgramUrl = ManagerRoute.path (ManagerRoute.asset assets.Build AssetFile.``manager-page``)

let private respondWith (res: ServerResponse) (status: int) (contentType: string) (cacheControl: string) (body: string) =
    res.writeHead (status, [ ResponseHeader.ContentType contentType; ResponseHeader.CacheControl cacheControl ])
    res.``end`` body

/// Every management response but one is a live view of mutable process state, so `no-store` is
/// the default here. The stylesheet is the exception — see its route.
let private respond (res: ServerResponse) (status: int) (contentType: string) (body: string) =
    respondWith res status contentType "no-store" body

let private html (res: ServerResponse) (body: string) = respond res 200 "text/html; charset=utf-8" body

/// A whole DOCUMENT, as against a fragment the page's script swaps in: the Manager page, the
/// opening screen, a problem page. Each is sent with a nonce of its own and the policy that
/// names it (`Ssr.ContentPolicy`), and rendered with that nonce, so an inline script on it
/// runs only because this response put it there.
let private document (res: ServerResponse) (status: int) (render: string -> string) =
    let nonce = Ssr.ContentPolicy.nonce ()
    res.writeHead (
        status,
        [ ResponseHeader.ContentType "text/html; charset=utf-8"
          ResponseHeader.CacheControl "no-store"
          Ssr.ContentPolicy.header Ssr.ContentPolicy.Document.ManagerDocument nonce ])
    res.``end`` (render nonce)

/// POST-redirect-GET. `303` rather than `302` so the browser is required to follow it with a
/// GET: what the caller lands on is a resource, not a resubmission of the form waiting to be
/// re-fired by a reload.
let private seeOther (res: ServerResponse) (location: string) =
    res.writeHead (303, [ ResponseHeader.Location location; ResponseHeader.CacheControl "no-store" ])
    res.``end`` ""

/// How long the readiness check waits for the session's address to answer.
///
/// Shorter than the opening screen's own bound on one poll (`openingProgram`), so the screen
/// hears this route's `503` and never has to abort it. Without a bound, an address that
/// accepted the connection and then said nothing held the screen's poll for ever, and the
/// screen's give-up never fired.
let private readinessDeadlineMs = 3000.0

/// GET a URL and report the status its answer carried; `0` when nothing answered at all,
/// inside `readinessDeadlineMs` or never. Redirects are followed, because a session that
/// bounces its shell through sign-in has still answered. Nothing reads the body, so nothing
/// reads it.
let private statusOf (url: string) : Async<int> =
    async {
        let! attempt =
            Http.attempt
                (fun _ -> Promise.lift ())
                url
                [ Fetch.Types.RequestProperties.Redirect Fetch.Types.RedirectMode.Follow
                  Fetch.Types.RequestProperties.Cache Fetch.Types.RequestCache.Nostore
                  Http.deadline readinessDeadlineMs ]

        match attempt with
        | Http.Answered (response, ()) -> return response.Status
        | Http.Unreachable _ -> return 0
    }

/// Is something answering FOR this address yet?
///
/// A front door that has not mapped this session yet does not stay silent — it answers, which
/// is the whole trap: `404` (nothing routed here) or a gateway status (a route to nothing).
/// `0` is nothing answering at all. Everything else, the `401` an authenticating proxy gives
/// included, is somebody answering for this address, which is the question being asked.
let private answeredFor (status: int) : bool =
    status <> 0 && status <> 404 && not (status >= 502 && status <= 504)

/// One of the Manager's own standalone pages: the two below are the only surfaces a browser
/// reaches that are not the management page, and they wear the same sheet it does.
///
/// The SAME sheet, linked, not a private inline one. These pages sit between the Manager and
/// a session — `/open` is the page a browser is looking at while a session launches — and
/// both neighbours paint the stylesheet's black ground on `<html>`. An inline sheet here said
/// nothing about the ground, so the browser painted its default, and every launch was a
/// white page between two black ones. Linking the app's stylesheet is what makes that
/// impossible to reintroduce: whatever the ground becomes, these pages have it, because it
/// is declared once, on the document, in the one file every surface links.
///
/// Linked from the ROOT, like every address the Manager emits (`ManagerRoute.path`). These
/// pages live under `/sessions/{id}/`, and a relative link copied from the management page
/// at `/` resolved to `/sessions/{id}/assets/…`, a 404 — the page was white with a
/// stylesheet link in it. `ManagerRoute` has no relative form at all now, which is what
/// makes that a compile error rather than a review catch.
///
/// HTML, and that is the point rather than a detail. Every other answer this file gives is
/// read by the page's script, so `text/plain` is right for them — but these are NAVIGATIONS
/// by construction: a browser is the only thing that ever lands on `/open`. A browser that is
/// handed `text/plain` does not necessarily show it; a phone downloads it, so an operator who
/// pressed Create is left holding a file called `document.txt` and no idea what went wrong.
///
/// And the Manager page's OTHER head tags — its icon, and the tint it asks of the browser's
/// own bars — because this page sits between two that carry them: a phone's toolbar that is
/// black on the Manager and black in the session went light for the seconds in between.
/// And the page's own ANATOMY, which it used not to have: the band every Manager page wears,
/// the rail its text starts on, and the build at its foot. It was a 32rem column with 4rem of
/// air above it — no band, no rail, no mark, and nothing saying which Manager had answered —
/// so the one surface a reader meets when something has gone wrong was the one surface that
/// did not look like the product (docs/visual-design.md, Space: every head shares the band).
let private standalonePage (title: string) (body: string) : string =
    sprintf
        """<!doctype html>
<html lang="en"><head><meta charset="utf-8"><title>%s</title>
<meta name="viewport" content="width=device-width, initial-scale=1">
%s%s
</head><body class="%s">
<header class="%s"><div class="%s w-full">%s</div></header>
<main class="%s">
<h1 class="%s">%s</h1>
%s
</main>
<footer class="%s"><span class="%s" %s>yession %s</span></footer>
</body></html>"""
        (Ssr.escapeText title)
        (Style.headTags cssUrl)
        (WebApp.managerHeadTags (ManagerRoute.path ManagerRoute.Manifest) (ManagerRoute.path ManagerRoute.Icon) (ManagerRoute.path ManagerRoute.Favicon))
        Style.standalone
        Style.pageBand
        Style.pageRail
        (Ssr.render bandLockup)
        Style.pageProse
        Style.heading
        (Ssr.escapeText title)
        body
        Style.pageFoot
        Style.buildMark
        Dom.Manager.managerBuild
        Version.current

/// What `/open` says when it cannot hand the browser over: the refusal, and the way back to
/// the page that can do something about it.
///
/// The REASON wears the err tone, because this page's whole subject is a thing that went
/// wrong, and a page that said so in the same ink as the registry's prose wore nothing wrong
/// at all (docs/visual-design.md, Honesty). One red statement, not two: the heading stays
/// `ink`, because the reason is the newest and more particular of the two.
///
/// The way back is a real control: the one hyperlink face with the product's focus ring on it
/// and a thumb's target on a phone (`pageWayBack`). It had neither, and it is the only way off
/// this page.
let problemPage (title: string) (detail: string) : string =
    standalonePage
        title
        (sprintf """<p class="%s">%s</p>
<p><a class="%s" href="%s">Back to the session manager</a></p>""" Style.bodyErr (Ssr.escapeText detail) Style.pageWayBack (ManagerRoute.path ManagerRoute.Home))

/// A refusal a BROWSER is holding: the status it deserves, and a page that says so.
let private problem (res: ServerResponse) (status: int) (title: string) (detail: string) =
    document res status (fun _ -> problemPage title detail)

/// The `/sessions/{id}/open` landing page (Plan 11).
///
/// Not a bare 302. A session that had to be launched is reachable at its own address only
/// once the operator's proxy has a mapping for it, and a reconciler driven by
/// `/sessions/stream` gets there in a few hundred milliseconds — quick, but a race against
/// a redirect the browser follows immediately. So the page waits until the address really
/// answers and only then goes.
///
/// It asks the MANAGER, at `/sessions/{id}/ready`, rather than probing the target itself —
/// see that route for why the browser is the one place this question cannot be answered.
///
/// Bounded, and it says why it gave up. An `/open` that spins forever is indistinguishable
/// from one that is about to work, which is the failure mode this whole feature is supposed
/// to remove rather than add.
///
/// Sent BEFORE the launch it covers has started: a page that waited for the spawn left the
/// browser with no document to paint for as long as a boot takes. The open route changes
/// nothing (a GET that an `<img>` on any website can cause must not start a session), so the
/// screen's program asks for the launch, `POST /sessions/{id}/launch`, under the Manager's
/// request-origin rule (`requestOrigin`), and polls readiness once it is answered. A refused
/// launch is shown where a refused readiness is.
///
/// It hands the browser to the session's SIGN-IN entry (`/login`), not its shell. Every
/// session this page can name was launched by this Manager, so every one of them gates its
/// data behind the Manager's bounce — and a browser that landed on the shell first painted
/// it, ran the client, asked `/me`, was told 401, and went round the bounce to land on the
/// shell a second time: two full loads of the same page, with a repaint between them, before
/// a person saw anything they could use. Entering through `/login` runs the bounce first and
/// paints once; a browser already holding the session's cookie is sent straight on by that
/// route (`Signalling.fs`), so the return visit pays one redirect, never a second sign-in.
/// A standalone SCREEN rather than a page: the same document as `standalonePage` (same
/// stylesheet, same head, for the same reasons) with no heading, because what is on it is
/// the heading.
let private screenPage (title: string) (body: string) : string =
    sprintf
        """<!doctype html>
<html lang="en"><head><meta charset="utf-8"><title>%s</title>
<meta name="viewport" content="width=device-width, initial-scale=1">
%s
%s
</head><body class="%s">
%s
</body></html>"""
        (Ssr.escapeText title)
        (Style.headTags cssUrl)
        (WebApp.managerHeadTags (ManagerRoute.path ManagerRoute.Manifest) (ManagerRoute.path ManagerRoute.Icon) (ManagerRoute.path ManagerRoute.Favicon))
        Style.standalone
        body

/// The opening screen's program, inline at the end of its `<body>` and NOT a served module.
///
/// The dwell below is timed from `performance.now()` when this script runs. An inline script
/// at the end of `<body>` runs during parse, as the intro starts painting; a module script is
/// deferred, running only after the document has parsed AND its file has been fetched — so
/// served, the dwell started late and the screen held past the intro it exists to wait for.
/// That is why this one page keeps a program in a string, against the rule that says not to.
///
/// It is a constant: every value it needs is an attribute on the screen (`openingPage`, below),
/// named here by the `Dom.Manager` hooks' values — `openingReady`, `openingShell`,
/// `openingWord`, `openingFailed` — or the readiness route's answer, which is where the
/// session's address arrives: the page is sent before the launch has a port. Written out rather than spliced so
/// the program is literal text; rename one of the first two without it and the screen never
/// hands over, which the opening page's browser cases see.
let private openingProgram =
    """
  // The intro is 2.4s and a beat after it lands is the least anyone is shown; under reduced
  // motion there is no intro, and nothing to wait for.
  const DWELL = matchMedia('(prefers-reduced-motion: reduce)').matches ? 0 : 2800
  const shownAt = performance.now()
  const screen = document.querySelector('[data-opening-ready]')
  const ready = screen.getAttribute('data-opening-ready')
  const launch = screen.getAttribute('data-opening-launch')
  const shell = screen.getAttribute('data-opening-shell')
  const status = document.getElementById('status')
  const giveUp = (why) => {
    status.className = status.getAttribute('data-opening-failed')
    status.textContent = why
  }
  // Does this browser already keep the session's shell? Only a shell this browser has opened
  // before is kept: its service worker is registered for that address, and the worker's store
  // holds the page. Such a shell paints the session's history from this browser's own store
  // at once, says it is connecting, and signs in by itself once the session answers. So it
  // needs neither this screen's wait nor the sign-in entry, and a phone that waited here saw
  // a blank or an opening page for as long as the session took to come up. Anything that
  // cannot answer (no worker support, an address on another origin) is "no": the screen does
  // what it did before.
  async function kept () {
    if (!shell || !navigator.serviceWorker || !self.caches) return false
    try {
      const at = new URL(shell, location.href).href
      const registration = await navigator.serviceWorker.getRegistration(at)
      if (!registration || !registration.active || !at.startsWith(registration.scope)) return false
      return Boolean(await caches.match(at))
    } catch (e) { return false }
  }
  // The bound is TIME since the launch finished, not a count of polls. A count advances only
  // when a poll settles, and a poll that never settled held the screen for ever.
  let since = null
  async function poll () {
    try {
      // `ok`, not "the request settled". A front door that has not mapped this session yet
      // answers — with a 404, or a gateway error — and a fetch that only caught THROWN
      // requests reads that as the session answering, redirects into it, and leaves whoever
      // pressed Create looking at the front door's 404. The readiness route reports the
      // difference, and says where to go: the address only exists once the launch does.
      // Bounded too, past the Manager's own bound on its check, so a Manager that does not
      // answer is the same wait as a session that does not.
      const answer = await fetch(ready, { cache: 'no-store', signal: AbortSignal.timeout(5000) })
      // Still launching: the Manager bounds that itself and says so if it fails, so it is
      // not counted against the wait for the session's address below.
      if (answer.status === 202) {
        setTimeout(poll, 500)
        return
      }
      if (answer.ok) {
        const target = await answer.text()
        status.querySelector('[data-word]').textContent = 'ready'
        setTimeout(() => location.replace(target), Math.max(0, DWELL - (performance.now() - shownAt)))
        return
      }
      // 503 is "not yet", and so is a gateway between here and the Manager. Anything else
      // is the Manager saying this launch will not come up, and why — shown now, rather
      // than waited out to a bound that would then report the wrong reason.
      if (answer.status < 502 || answer.status > 504) {
        giveUp('The session could not start: ' + await answer.text())
        return
      }
    } catch (e) { /* the Manager itself is unreachable: the same wait, bounded the same way */ }
    since = since ?? performance.now()
    if (performance.now() - since >= 20000) {
      giveUp(
        'The session started, but its address is not answering after 20 seconds. ' +
        'If this deployment maps session ports through a proxy, that mapping has not appeared.')
      return
    }
    setTimeout(poll, 500)
  }
  // The launch is this screen's to ask for: the open route that sent it changes nothing,
  // because a GET any page can cause must not start a session. A refusal (an archived
  // session, a launch that failed) goes where a readiness refusal goes. A kept shell does
  // not wait for the answer: `keepalive` delivers the request as the browser leaves.
  async function launched () {
    try {
      const answer = await fetch(launch, { method: 'POST', cache: 'no-store' })
      if (answer.ok) return true
      giveUp('The session could not start: ' + await answer.text())
    } catch (e) {
      giveUp('The session could not start: the session manager did not answer.')
    }
    return false
  }
  kept().then(async (yes) => {
    if (yes) {
      fetch(launch, { method: 'POST', cache: 'no-store', keepalive: true }).catch(() => {})
      location.replace(shell)
    } else if (await launched()) poll()
  })
"""

/// What the screen looks like: the mark's intro at 224px (`Brand.intro`, one SVG, SMIL),
/// the wordmark rising under it as the last frame lands, and under that the status line —
/// `starting` with the session's id, then `ready`. The way out is on the screen from the
/// start, because a screen that waits must never trap anyone: the session's own address, and
/// the manager. A reader who declined motion gets the still mark (`Brand.mark`) and the same
/// words; the switch is CSS, since SMIL cannot read the preference.
///
/// The poll keeps its shape and its bound (`openingProgram`, below). What is added is a DWELL:
/// the page does not go before the intro has finished plus a beat (2.4s + 0.4s), however fast
/// the session comes up, because a start that flashes past is not a start anyone saw — and it
/// does not hold a slow one, which keeps polling, and breathing, until the bound says why it
/// gave up.
///
/// Everything the program needs is an attribute on the screen: the launch route and the
/// readiness route on `<main>` (`Dom.Manager.openingLaunch`, `openingReady`), the status word
/// it changes (`openingWord`), and the classes
/// the status line wears when the screen gives up (`openingFailed`). Nothing is spliced into
/// the program, so there is nothing to escape but attributes. The session's address is not on
/// the screen at all: this page is sent before the launch it covers has a port, so the way out
/// it offers is the open route itself (which redirects once the session runs), and the address
/// the program goes to is the readiness route's answer.
///
/// `shell` is the session's own shell address where the deployment knows it before the launch
/// (`PublicAccess.stableSessionAddress`). A browser that keeps a copy of that shell is sent
/// there at once and skips the wait (`openingProgram`). Where it is `None` the screen has no
/// such attribute, and every browser waits.
///
/// `nonce` is the response's (`Ssr.ContentPolicy`), and the program's `<script>` carries it.
let openingPage (nonce: string) (sessionId: SessionId) (target: string) (launchUrl: string) (readyUrl: string) (shell: string option) : string =
    let shellAttr =
        match shell with
        | Some url -> sprintf " %s=\"%s\"" Dom.Manager.openingShell (Ssr.escapeAttr url)
        | None -> ""
    screenPage
        "Opening session…"
        (sprintf
            """<main class="%s" %s="%s" %s="%s"%s>
<div class="%s" data-mark-intro aria-hidden="true">%s</div>
<div class="%s" data-mark-static aria-hidden="true">%s</div>
<p class="%s">yession</p>
<p id="status" role="status" class="%s %s" %s="%s">%s<span %s>starting</span><span class="%s">%s</span></p>
<p class="%s"><a id="target" class="%s" href="%s">Open it directly</a> · <a class="%s" href="%s">Back to the session manager</a></p>
</main>
<script nonce="%s">%s</script>"""
            Style.startScreen
            Dom.Manager.openingLaunch
            (Ssr.escapeAttr launchUrl)
            Dom.Manager.openingReady
            (Ssr.escapeAttr readyUrl)
            shellAttr
            Style.startMarkIntro
            (Ssr.render Brand.intro)
            Style.startMarkStill
            (Ssr.render Brand.mark)
            Style.startWord
            Style.statusWait
            Style.startStatus
            Dom.Manager.openingFailed
            (Ssr.escapeAttr Style.startFailed)
            (Ssr.render Icon.spinLead)
            Dom.Manager.openingWord
            Style.startStatusId
            (Ssr.escapeText (SessionId.value sessionId))
            Style.startLinks
            Style.pageWayBack
            (Ssr.escapeAttr target)
            Style.pageWayBack
            (ManagerRoute.path ManagerRoute.Home)
            (Ssr.escapeAttr nonce)
            openingProgram)

/// Did a request that changes state come from the Manager's own pages?
///
/// The identity header says WHO a request is for, and nothing about who CAUSED it: a tailnet
/// member's browser carries that identity on a form another website submits, and on an image
/// it loads. So every route but a read asks where the request came from. ONE rule, here, for
/// every such route; a form token beside it would be a second mechanism for one requirement.
///
/// - `GET` and `HEAD` change nothing, and pass.
/// - `Sec-Fetch-Site` present: `same-origin` (this Manager's own page) and `none` (the person
///   typed it or followed a bookmark) are accepted. Every other value is refused.
/// - `Sec-Fetch-Site` absent and `Origin` present: accepted when the host and port in `Origin`
///   are the request's own `Host`. Refused otherwise, and `Origin: null` is a refusal.
/// - Both absent: accepted. A browser sends at least one of the two on every POST, so a
///   request with neither is no browser's, and the identity header already names its sender.
let requestOrigin (method: string) (fetchSite: string option) (origin: string option) (host: string option) : Result<unit, string> =
    if method = "GET" || method = "HEAD" then Ok ()
    else
        match fetchSite, origin with
        | Some ("same-origin" | "none"), _ -> Ok ()
        | Some site, _ -> Error (sprintf "a request from another site (Sec-Fetch-Site: %s) cannot change this Manager" site)
        | None, Some origin ->
            let own =
                host
                |> Option.exists (fun host ->
                    try
                        let asked = Node.Api.URL.Create origin
                        asked.host = (Node.Api.URL.Create (asked.protocol + "//" + host)).host
                    with _ -> false)
            if own then Ok () else Error (sprintf "a request from %s cannot change this Manager" origin)
        | None, None -> Ok ()

/// Handle a management-UI request against the Manager. Returns false for paths that
/// are not the UI's (the composing server falls through — e.g. to the control routes).
/// Every UI route is gated by `identify` — the Manager's authentication strategy:
/// a denial is a 401 on every route; both attributed and unattributed
/// outcomes are let through (under trust-localhost every loopback request is
/// unattributed, which is exactly today's behaviour).
let tryHandle
    (pm: ProcessManager.ProcessManager)
    (identify: IncomingMessage -> Async<AuthenticationOutcome>)
    (req: IncomingMessage)
    (res: ServerResponse)
    : bool =
    let path = pathnameOf req.url
    // What this reader asked to see. Parsed off the request rather than held anywhere, so the
    // page, the stream and every action that answers with a table agree by construction —
    // and a bookmark renders correctly on first paint with no client state machine.
    let query = SessionQuery.ofQueryString req.url
    let tableNow () = sessionsTable query (pm.Sessions ())
    let rowOf (sessionId: SessionId) =
        match pm.TryFind sessionId with
        | Some view -> sessionRow view
        | None -> ""
    // A refusal over an ARCHIVED session is not a server fault — it is a conflict with
    // durable state the caller can resolve, which matters most for the launch the opening
    // screen sends from `/open`, the URL a session client's reconnect card points at. This picks the STATUS CODE and nothing else: the
    // refusal itself is `ManagerState.launchable`'s, and its words are what gets sent, so the
    // rule and the code it is reported under cannot drift apart.
    let refusalStatus (sessionId: SessionId) =
        match pm.TryFind sessionId with
        | Some view when view.Record.ArchivedAt.IsSome -> 409
        | _ -> 500
    // Where a browser enters a running session: its sign-in route, so the Manager's bounce
    // runs before the one paint (see `openingPage`).
    let signIn (sessionId: SessionId) (port: int) =
        let address = PublicAccess.sessionAddress sessionId port pm.Public
        RelativeUrl.under address.Url (SessionRoute.relative Login)
    // An action's outcome is not discarded: a launch that fails leaves the session stopped,
    // and answering with an ordinary row said nothing about why. The row still comes back on
    // success (it is the swap unit); a failure answers with its reason.
    let sessionAction (sessionId: SessionId) (action: SessionId -> Async<Result<unit, string>>) =
        Async.StartImmediate (
            async {
                match! action sessionId with
                | Ok () -> html res (rowOf sessionId)
                | Error reason -> respond res (refusalStatus sessionId) "text/plain" reason
            })
    // Route first (pure — did the UI claim this path?), authenticate second: the gate
    // runs once, ahead of every claimed route, and unclaimed paths fall through to the
    // composing server untouched. ONE match over the contract, so a route added to
    // `ManagerRoute` fails the build here until it is answered.
    let handle (route: ManagerRoute) : unit =
        match route with
        | ManagerRoute.Home ->
            document res 200 (fun _ -> page cssUrl managerProgramUrl pm.Public query (pm.Sessions ()) (pm.McpServers ()) pm.HookEndpoints)
        | ManagerRoute.Asset (build, file) ->
            // Everything static this build ships, served by path and by nothing else — the
            // same service the Session runs, over this process's own set. The Manager
            // page links the stylesheet, and the stylesheet names its own faces; neither this
            // route nor this file knows what those are, which is the point: a build that adds
            // an asset adds a file, not a case.
            Assets.serve assets build file res
        | ManagerRoute.Manifest ->
            // What an installed Manager is: the app's name, its mark, and — the reason this
            // route exists — the ground its WINDOW is painted in on a phone
            // (`WebApp.managerManifest`). Cached like the shell, since it changes only when
            // the build does.
            res.writeHead (
                200,
                [ ResponseHeader.ContentType "application/manifest+json"
                  ResponseHeader.CacheControl CachePolicy.shell ])
            res.``end`` (WebApp.managerManifest (ManagerRoute.path ManagerRoute.Icon))
        | ManagerRoute.Icon ->
            // The same mark the session shells wear, from the same constant, at the address
            // the page emits for it.
            res.writeHead (200, [ ResponseHeader.ContentType "image/png"; ResponseHeader.CacheControl CachePolicy.shell ])
            res.``end`` (buffer.Buffer.from (Brand.iconPngBase64, BufferEncoding.Base64))
        | ManagerRoute.Favicon ->
            res.writeHead (
                200,
                [ ResponseHeader.ContentType "image/svg+xml; charset=utf-8"
                  ResponseHeader.CacheControl CachePolicy.shell ])
            res.``end`` Brand.faviconSvg
        // Creating a session is asking to WORK in one. It used to answer with a refreshed
        // table, which left the primary path at three acts — create, find the row, Launch —
        // and then a fourth to open what you had just made. So the answer says where the
        // session now is, and `OpenSession` (below) does what it has always done: launch it
        // if it is stopped and land the browser on its address.
        //
        // One answer, for every caller. A route that returned a fragment to some callers and
        // a redirect to others would be two contracts wearing one address, and the one nobody
        // was looking at is the one that would rot — which is precisely how the vanishing-row
        // bug lived: two renderings of the session list, only one of them exercised.
        | ManagerRoute.CreateSession ->
            readBody req (fun body ->
                // The human UI omits the id, so mint a Docker-safe Crockford one; a caller that
                // supplies an explicit id (automation, tests) keeps it.
                // Absent and blank agree here, unlike the MCP forms: both mean "mint one".
                let id =
                    match formField body "id" with
                    | None
                    | Some "" -> SessionId.value (SessionId.mint ())
                    | Some provided -> provided
                // No name: a session is named from inside itself and reports it back
                // (`setDisplayName`), so `DisplayName` starts as the minted id and the
                // list shows that until somebody names it.
                match pm.CreateSession id "" with
                | Ok record -> seeOther res (ManagerRoute.path (ManagerRoute.OpenSession record.SessionId))
                | Error e -> respond res 400 "text/plain" e)
        // Declaring an MCP server (Plan 17). The ONE act that names a url, and the only
        // one that is not read-only. There is deliberately no per-session enable beside it:
        // an operator who declares a server declares it in order for it to be used.
        | ManagerRoute.DeclareMcpServer ->
            readBody req (fun body ->
                match declarationOfForm body |> Result.bind pm.DeclareMcpServer with
                | Ok () -> html res (mcpSection (pm.Sessions ()) (pm.McpServers ()))
                | Error e -> respond res 400 "text/plain" e)
        | ManagerRoute.WithdrawMcpServer ->
            readBody req (fun body ->
                match withdrawalOfForm body with
                | Error e -> respond res 400 "text/plain" e
                | Ok (name, audience) ->
                    pm.WithdrawMcpServer name audience
                    html res (mcpSection (pm.Sessions ()) (pm.McpServers ())))
        // Both streams are the same subscription projected differently — one publish per
        // launch, exit, and rename; snapshots, never deltas, so a reconnect is the whole
        // recovery protocol and a consumer that connects, reads one frame, and disconnects
        // has done a poll.
        | ManagerRoute.SessionRegistry ->
            // The session registry: the Running set as wire frames. An
            // operator's serving binding holds this open to reconcile its proxy.
            Sse.stream req res
                (ProcessManager.registryFrameOf >> ControlWire.toString ControlWire.sessionRegistryFrame)
                pm.SubscribeSessions
            |> ignore
        | ManagerRoute.SessionRows ->
            // The management page's live status, pushed rather than polled: the WHOLE table,
            // rendered by the same `tableTemplate` the page and the action swaps use, so the
            // browser keeps no reconciliation logic. Stopped and exited rows are in the
            // published views, which is why the page renders them and the registry does not.
            Sse.stream req res (sessionsTable query) pm.SubscribeSessions |> ignore
        // The one way a session is started: a row's Start posts here, and so does the opening
        // screen (`openingProgram`) that `OpenSession` answers for a stopped session. A POST,
        // so the request-origin rule (`gated`) stands in front of every launch.
        | ManagerRoute.Session (sessionId, SessionVerb.Launch) ->
            sessionAction sessionId (fun sessionId ->
                async {
                    let! outcome = pm.Launch sessionId
                    return outcome |> Result.map ignore
                })
        | ManagerRoute.Session (sessionId, SessionVerb.Stop) -> sessionAction sessionId pm.Stop
        // Archiving answers with the WHOLE table rather than a row: it can move a session
        // out of the filter the caller is looking at, so the row is no longer the unit
        // that changed. (The verbs also publish, which is what reaches every OTHER open
        // page and the registry stream — the same split `launch` already makes.)
        | ManagerRoute.Session (sessionId, SessionVerb.Archive) ->
            Async.StartImmediate (
                async {
                    match! pm.Archive sessionId with
                    | Ok () -> html res (tableNow ())
                    | Error reason -> respond res 500 "text/plain" reason
                })
        | ManagerRoute.Session (sessionId, SessionVerb.Unarchive) ->
            match pm.Unarchive sessionId with
            | Ok () -> html res (tableNow ())
            | Error reason -> respond res 500 "text/plain" reason
        // The stable way back into a session (Plan 11). A session's own address changes
        // whenever it is relaunched, and under idle reaping that is routine rather than
        // rare — so THIS is the URL to bookmark and the one the session client's
        // reconnect offer points at. It changes NOTHING: a GET is what an `<img>` on any
        // website can cause, so a stopped session is answered with the opening screen, and
        // the screen's program asks for the launch by POST (`openingPage`).
        //
        // The opening screen is only for the launch. It exists to cover two things
        // arriving — the session process coming up and the front door's mapping to it
        // appearing — and a session that is already running has both, so a screen in
        // front of it would only be a delay (an intro, a dwell, a poll that answers
        // at once). A running session is answered with a redirect straight to the
        // address the screen would have sent the browser to.
        | ManagerRoute.OpenSession sessionId ->
            match pm.TryFind sessionId with
            | None ->
                problem res 404 "No such session" (sprintf "This Manager has no session %s." (SessionId.value sessionId))
            | Some view ->
                match view.Status with
                // Already running is the common case once a client has
                // reconnected on its own; asking for the port it already
                // has is not a relaunch, and there is nothing to wait for.
                | ProcessManager.Running (port, _, _) -> seeOther res (signIn sessionId port)
                // Answered BEFORE any launch: awaiting one first held the browser on a
                // navigation with no document for as long as a boot takes — which Chromium
                // hides by keeping the last page painted, and an installed app on iOS shows
                // as a white screen between two black ones. The screen asks for the launch,
                // and a refusal (an archived session) is the launch route's to give.
                | ProcessManager.NotRunning
                | ProcessManager.Launching
                | ProcessManager.LaunchFailed _
                | ProcessManager.Exited _ ->
                    let shell =
                        PublicAccess.stableSessionAddress sessionId pm.Public
                        |> Option.map (fun address -> address.Url + "/")
                    document res 200 (fun nonce ->
                        openingPage
                            nonce
                            sessionId
                            (ManagerRoute.path (ManagerRoute.OpenSession sessionId))
                            (ManagerRoute.path (ManagerRoute.Session (sessionId, SessionVerb.Launch)))
                            (ManagerRoute.path (ManagerRoute.SessionReady sessionId))
                            shell)
        // Does this deployment's front door reach the session yet? The question the
        // opening page above is really asking, answered HERE because here is the only
        // place it CAN be answered: a browser can read the status of a same-origin
        // address and learns nothing at all about a cross-origin one (an opaque `no-cors`
        // answer carries status `0` whether the session served the page or the proxy
        // served a 404), while this process has no origin to be blinded by. The page that
        // guessed instead redirected whoever pressed Create straight into the front
        // door's 404, a few hundred milliseconds before the mapping appeared.
        //
        // Four answers, and the page treats each differently: `200` with the address to go
        // to (only known once the launch has a port, which is after the page was sent),
        // `202` for "still launching", `503` for "running, and its address not answering
        // yet", and anything else for "this will not come up, and here is why", which the
        // page shows instead of waiting out its bound.
        //
        // Read by a script, so `text/plain` — unlike `OpenSession` beside it, nothing
        // navigates here.
        | ManagerRoute.SessionReady sessionId ->
            match pm.TryFind sessionId with
            | None -> respond res 404 "text/plain" (sprintf "unknown session %s" (SessionId.value sessionId))
            | Some view ->
                match view.Status with
                // Accepted, not unavailable: the launch is bounded by the Manager's own
                // timeout and ends in `Running` or `LaunchFailed`, so the page must not spend
                // its wait for the ADDRESS on it.
                | ProcessManager.Launching -> respond res 202 "text/plain" "the session is starting"
                | ProcessManager.LaunchFailed reason -> respond res 500 "text/plain" reason
                // Not running is not ready, and it is not an error either: `OpenSession`
                // launches, and a session can exit under a reader who is watching.
                | ProcessManager.NotRunning
                | ProcessManager.Exited _ ->
                    respond res 503 "text/plain" "the session is not running"
                | ProcessManager.Running (port, _, _) ->
                    let address = PublicAccess.sessionAddress sessionId port pm.Public
                    Async.StartImmediate (
                        async {
                            let! status = statusOf (sprintf "%s/" address.Url)
                            if answeredFor status then respond res 200 "text/plain" (signIn sessionId port)
                            else
                                respond
                                    res
                                    503
                                    "text/plain"
                                    (sprintf "%s answered %d" address.Url status)
                        })
    // Behind the identity gate either way: a malformed id is answered by the Manager, but
    // only to someone the Manager would answer at all. And behind the request-origin rule
    // (`requestOrigin`), so a route that changes state answers only the Manager's own pages.
    let gated (answer: unit -> unit) =
        Async.StartImmediate (
            async {
                match! identify req with
                | Denied reason -> respond res 401 "text/plain" reason
                | Attributed _ | Unattributed _ ->
                    let header = Interop.headerOf req
                    match requestOrigin req.``method`` (header "sec-fetch-site") (header "origin") (header "host") with
                    | Error reason -> respond res 403 "text/plain" reason
                    | Ok () -> answer ()
            })
        true
    // A refusal a BROWSER is holding gets a page; everything else gets the line its reader
    // reads. The page's own script reads every other answer this file gives, so `text/plain`
    // is right for those — but a browser that is handed one does not necessarily show it, and
    // a phone downloads it. An unrouted path used to fall through to the composing server's
    // bare `text/plain` 404, which is a document with no viewport meta, no rail and no way
    // back: a phone rendered it at 980px, zoomed out, and left whoever followed a stale
    // bookmark with nothing to press (docs/visual-design.md, Deciding — the floor).
    //
    // The question is answered by what the request ASKS FOR rather than by what its path looks
    // like: a GET that accepts HTML is a navigation, and nothing else is. The composing
    // server's 404 stays as it is, which is the downstream half — this claims the navigations,
    // and that one still answers everything no handler claimed (AGENTS.md "Fixing bugs").
    let navigating =
        req.``method`` = "GET"
        && (Interop.headerOf req "accept"
            |> Option.map (fun accept -> accept.Contains "text/html")
            |> Option.defaultValue false)
    match ManagerRoute.parse req.``method`` path with
    | Error ManagerMiss.Unclaimed when not navigating -> false
    | Error ManagerMiss.Unclaimed ->
        gated (fun () ->
            problem res 404 "No such page" (sprintf "This Manager has nothing at %s." path))
    | Error (ManagerMiss.MalformedSessionId (raw, reason)) when navigating ->
        gated (fun () -> problem res 400 "No such session" (sprintf "%s is not a session id: %s" raw reason))
    | Error (ManagerMiss.MalformedSessionId (raw, reason)) ->
        gated (fun () -> respond res 400 "text/plain" (sprintf "%s is not a session id: %s" raw reason))
    | Ok route -> gated (fun () -> handle route)
