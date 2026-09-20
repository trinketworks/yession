module Yession.Host.ManagerUi

// The management UI (Phase 4, Step 25): a deliberately server-side-rendered admin surface
// — list sessions with live status, create, open (which launches), stop, archive. Pure F# render
// functions produce full pages and FRAGMENTS from Fable.Lit templates (rendered to strings
// by our own `Ssr` wrapper — no client bundle, no Elmish, no Yjs); a tiny inline vanilla
// script swaps the fragments on stop/archive and takes live status from an SSE stream
// of rendered tables (`GET /sessions/rows`) — the server pushes, the page never polls. It
// shares the session client's `Style` (the same locally served /app.css), and — being
// online-only — is the natural home for server-side Lit SSR. This is not the collaborative
// client; it shares the Manager's 127.0.0.1 endpoint with the control RPC.

open Fable.Core.JsInterop
open Node.Api
open Node.Buffer
open Yession.Domain
open Yession.Domain.Link
open Yession.Domain.Tools
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
module private Col =
    let table = "w-full text-left border-collapse table-fixed"
    /// Wide enough for a whole Crockford id — a half id identifies nothing, so this column is
    /// sized never to truncate. MEASURED, because the number depends on the face: 26 characters
    /// of 12px Monaspace Neon is 194px, plus the cell's own 16px gutter. It was 204px when the
    /// ids were set in whatever monospace a box happened to have, and the switch to a shipped
    /// face made them 6px too wide for it. The first thing to go on a phone.
    let id = "w-[210px] max-md:hidden"
    /// The MCP table's audience column. It was the session table's status column too, until
    /// status moved under the name (`stateLine`) — where it has the name's whole width instead
    /// of a 100px cell on a phone, which was the word and nothing else.
    let reaches = "w-[256px] max-xl:w-[100px]"
    /// `2026-08-18 09:12Z` in the 12px mono face, plus the cell's gutter. MEASURED like the
    /// id beside it: 17 characters of 12px Monaspace Neon is 123px, and the caps header over
    /// it is narrower than that. Goes with the id on a phone — it is the sort KEY, and a sort
    /// you cannot see is still one you can read off the order of the rows.
    let created = "w-[144px] max-md:hidden"
    /// One `h-8` control, full-bleed in its column, plus the cell's left gutter (the MCP
    /// table's Withdraw).
    let actions = "w-[128px]"
    /// The session rail carries two BORDERLESS verbs at most: a word (`stop`, or `unarchive`,
    /// the longer of the two at 83px in the caps voice) and the 24px archive icon, 8px apart,
    /// plus the cell's left gutter. Everything the rail gave up when its verbs lost their
    /// rectangles, the name gained: on a 390px phone the name has 238px where it had 160.
    let rail = "w-[128px]"

/// The row's second line: the session's state, and beside it the one line the session says
/// about ITSELF. Plain small text, at every width — this is the answer to the question the
/// page exists for (which of six sessions wants me), and it used to yield below `xl`, which
/// is to say on the phone the page is most often opened from.
///
/// UNCOLOURED, but for a fault. `running` and `stopped` were green and faint caps; that spent
/// the palette on the commonest fact in the list, and the name above already carries it — a
/// running session's name is full ink, any other's is a step back (`nameView`). `exited (n)`
/// keeps the err tone because it is the one state that is not an operator's doing. The word
/// is always there beside the colour, so nothing rests on colour alone.
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
    match view.Record.ArchivedAt, view.Status with
    // An operator's decision, not a process state — but it belongs on this line, because
    // "archived" is the answer a reader wants here and "stopped" for a session that can no
    // longer start is true and useless.
    | Some _, _ ->
        html $"""<div class="{Style.small} truncate"><span data-status="{Dom.Manager.statusArchived}">archived</span></div>"""
    | None, ProcessManager.NotRunning ->
        html $"""<div class="{Style.small} truncate"><span data-status="{Dom.Manager.statusStopped}">stopped</span></div>"""
    | None, ProcessManager.Running _ ->
        // Rendered opaquely and NOT toned. The Manager stores a line it was told and never
        // learns what it is made of, so any colour it chose would be a colour it guessed;
        // only the session knows whether its own sentence is good news. It exists in this
        // arm alone because a summary is launch-scoped — a session that is not running has
        // no work in flight to describe.
        let summary =
            match view.Summary with
            | Some line -> html $"""<span data-session-summary> · {line}</span>"""
            | None -> html $""""""
        html
            $"""<div class="{Style.small} truncate"><span data-status="{Dom.Manager.statusRunning}">running</span>{summary}</div>"""
    | None, ProcessManager.Exited code ->
        let reason = code |> Option.map string |> Option.defaultValue "signal"
        html $"""<div class="{Style.smallErr} truncate"><span data-status="{Dom.Manager.statusExited}">exited ({reason})</span></div>"""

/// The name cell. Opening a session is THE act on it, and it is carried by the name — content
/// is the interface — rather than by a second bordered rectangle in the right rail: with five
/// sessions listed, a per-row Open plus a per-row lifecycle verb is ten buttons competing with
/// the page's one real CTA (Create).
///
/// The address is the session's STABLE open route in every state it can be opened from, not
/// the port it happens to answer on. `/open` launches a stopped session and lands the browser
/// on it, which is why there is no Launch button: launching without going was a verb nobody
/// had a use for, and it left a stopped session's name as text and its way in as two acts
/// (press Launch, then find the link that appeared). A session that exited on its own is
/// opened the same way — `/open` is what relaunches it. A port, by contrast, is a launch-scoped
/// fact: bookmark it and a relaunch under idle reaping breaks the bookmark, which is the fault
/// `/open` exists to close, so this page never spells one.
///
/// An ARCHIVED session has no way in — `/open` refuses it — so its name is plain text: a link
/// whose only outcome is a refusal is worse than no link.
///
/// The name carries the STATE, by weight of ink: full ink while the session runs, a step back
/// (`bodyDim`) when it is stopped, exited or archived. That is what lets the state line below
/// it be plain text — a scan down the column finds what is live without a colour saying so —
/// and it is a token rather than an opacity so the mark and the focus ring keep theirs.
let private nameView (view: ProcessManager.SessionView) : TemplateResult =
    match view.Record.ArchivedAt, view.Status with
    | Some _, _ -> html $"""<span class="{Style.bodyDim}">{view.Record.DisplayName}</span>"""
    | None, status ->
        let openUrl = ManagerRoute.path (ManagerRoute.OpenSession view.Record.SessionId)
        let face =
            match status with
            | ProcessManager.Running _ -> Style.recordLink
            | ProcessManager.NotRunning
            | ProcessManager.Exited _ -> Style.recordLinkQuiet
        html
            $"""<a class="{face}" href="{openUrl}" target="_blank" data-open>{view.Record.DisplayName}<span class="{Style.recordLinkMark}" aria-hidden="true">↗</span></a>"""

/// The row's controls: Stop, while the session runs, and — where the session is not already
/// archived — the quiet way to retire it. There is no Launch: opening IS launching, and the name
/// carries it (`nameView`).
///
/// Both are BORDERLESS. `Style`'s rule for a verb riding a listed row is exactly this case —
/// the row already carries the structure, so the verb borrows it, faint at rest and ink (or
/// err, for the one that ends something) under the hand. Stop used to be exempted as "the
/// lifecycle verb" and wore a rectangle; that put the loudest chrome on the rarest act, and on
/// a phone it cost the name 40px. With both verbs quiet, the page has ONE bordered rectangle,
/// and it is Create.
///
/// An ARCHIVED row offers Unarchive and nothing else: no archive icon, because the word it
/// wears is already the act. Everything sits on the rail's right edge, so the icon does not
/// move when a Stop appears or disappears beside it — a control that moves when a process
/// stops is under a pointer that was aimed at its neighbour.
let private actions (view: ProcessManager.SessionView) : TemplateResult =
    let id = SessionId.value view.Record.SessionId
    let name = view.Record.DisplayName
    // Each control carries the address it posts to, spelled by the server: the script that
    // presses it reads the attribute and builds nothing.
    let posts (verb: SessionVerb) = ManagerRoute.path (ManagerRoute.Session (view.Record.SessionId, verb))
    match view.Record.ArchivedAt with
    | Some _ ->
        html $"""
            <div class="flex items-center justify-end gap-2">
              <button type="button" class="{Style.btnBare}" data-unarchive="{id}" data-post="{posts SessionVerb.Unarchive}">Unarchive</button>
            </div>"""
    | None ->
        let verb =
            match view.Status with
            | ProcessManager.Running _ ->
                html $"""<button type="button" class="{Style.btnBareDanger}" data-stop="{id}" data-post="{posts SessionVerb.Stop}">Stop</button>"""
            | ProcessManager.NotRunning
            | ProcessManager.Exited _ -> html $""""""
        html $"""
            <div class="flex items-center justify-end gap-2">
              {verb}
              <button type="button" class="{Style.btnIconBare}" data-archive="{id}" data-post="{posts SessionVerb.Archive}"
                      aria-label="Archive {name}" title="Archive {name}">{Icon.archive}</button>
            </div>"""

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
/// instead; see the rows stream.) The human name leads (content is the interface), with the
/// state under it; the minted id is plumbing, faint mono, and yields on narrow screens with
/// the created column. Actions anchor the right edge so the row reads name → state → verb.
///
/// A row is TWO lines and the same height whatever its state: 24px of name over 16px of state,
/// and the state line is rendered in every arm, so a row with nothing to add still keeps its
/// second line. The table is fixed-layout (`Col`), each text cell clips rather than wraps, and
/// the rail holds nothing taller than its 24px verbs. Rows that jump as processes start and
/// stop make a list you cannot keep your place in, and put a control under a pointer that was
/// aimed at its neighbour.
let private rowTemplate (view: ProcessManager.SessionView) : TemplateResult =
    let id = SessionId.value view.Record.SessionId
    html $"""
        <tr class="border-b border-hair hover:bg-surface transition-colors" data-session="{id}">
          <td class="py-3 pr-4 align-middle" title="{view.Record.DisplayName}">
            <div class="truncate">{nameView view}</div>
            {stateLine view}
          </td>
          <td class="py-3 pr-4 align-middle font-terminal text-code text-ink-faint truncate max-md:hidden">{id}</td>
          <td class="py-3 pr-4 align-middle font-terminal text-code text-ink-faint tabular-nums truncate max-md:hidden">{createdView view.Record.CreatedAt}</td>
          <td class="py-3 pl-4 align-middle">{actions view}</td>
        </tr>"""

/// One filter chip, wearing the COUNT of what it filters. A real `<a>`, because the filter
/// IS the page's location: it lives in the URL, a bookmark restores it, and the back button
/// undoes it. That makes it keyboard-operable and focusable with no help.
///
/// The count is what tells a reader there is anything behind an unlit chip — without it
/// `archived` was a door with no window, opened to find out — and it is the honest total for
/// the state, not the number of rows on screen. Beside it, off-screen, the state the chip is
/// in ("shown" / "hidden"): a link's name should say what following it does, and a lit chip
/// differs from an unlit one by a border step nothing but an eye can read. Not `aria-current`,
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
    let face = if shown then Style.filterChipOn else Style.filterChipOff
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
            sprintf "no sessions match — %d hidden" hidden
    let rows =
        match views with
        | [] ->
            [ html $"""
                <tr>
                  <td colspan="4" class="py-10 text-center {Style.small}">{emptyWord}</td>
                </tr>""" ]
        | views -> views |> List.map rowTemplate
    // The `created` header IS the sort control — the canonical accessible table sort, and it
    // spends no chrome anywhere else on the page. When last-activity or a summary arrives, its
    // header becomes the second one of these and nothing here has to be redesigned.
    let sortHref = sprintf "?%s" (SessionQuery.toQueryString (SessionQuery.reversed query))
    let sortMark, sortedBy =
        match query.Order with
        | NewestFirst -> "↓", "descending"
        | OldestFirst -> "↑", "ascending"
    // Create sits on the section's own header line, at the right: the list and the one act
    // that adds to it are one thing, and this is the page's one bordered rectangle, so it
    // does not need a section of its own to be found. It takes nothing but the press — the
    // id is minted server-side (a Docker-safe Crockford one) and a session is NAMED from
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
          <!-- On a phone the chips, now carrying counts, take a line of their own UNDER the
               label and Create rather than wrapping wherever the width happens to break:
               `basis-full` + `order-last` is a decision, a wrap is luck. -->
          <div class="flex items-center gap-y-3 gap-x-2.5 flex-wrap">
            <span class="{Style.label}">sessions</span>
            <div class="flex items-center gap-1.5 ml-2 max-md:ml-0 max-md:basis-full max-md:order-last" role="group" aria-label="Show sessions">
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
            <thead>
              <tr class="border-b border-hair">
                <th scope="col" class="py-2 pr-4 {Style.label}">name</th>
                <th scope="col" class="py-2 pr-4 {Style.label} {Col.id}">id</th>
                <th scope="col" class="py-2 pr-4 {Col.created}" aria-sort="{sortedBy}">
                  <a class="{Style.sortHeader}" href="{sortHref}" data-filter="sort">created <span aria-hidden="true">{sortMark}</span></a>
                </th>
                <th scope="col" class="py-2 pl-4 {Col.rail}"><span class="sr-only">actions</span></th>
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

// The interactivity, without htmx: a tiny vanilla script that swaps fragments on
// stop/archive and takes live status from the rows stream. Inline (no external src) so
// the page is self-contained — local first, no CDN.
let private script =
    """
    const swap = (el, htmlText) => {
      const t = document.createElement('template'); t.innerHTML = htmlText.trim()
      const n = t.content.firstElementChild
      if (!n || !el || n.outerHTML === el.outerHTML) return
      // Keyboard continuity (WCAG 2.0): replacing the focused element strands focus on
      // <body>. Land it back on the SAME session — the swap unit is often the whole table
      // (the rows stream, a create), and the replacement's first action belongs to the
      // first row, which is another session's control under the same finger.
      const active = el.contains(document.activeElement) ? document.activeElement : null
      const row = active && active.closest('[data-session]')
      // A filter or sort control keeps a STABLE name across a swap even though its href just
      // flipped, so the hand that pressed `archived` is left on `archived` rather than being
      // dropped onto the first row of the list it just asked for.
      const filter = active && active.closest('[data-filter]')
      // Create rides the section's header line, so a frame arriving under a hand resting on
      // it would otherwise drop that hand onto the first chip.
      const wasCreate = !!active && !!active.closest('[data-create-session]')
      const wasAction = !!active && active.hasAttribute('data-stop')
      // A Create that is HELD (the browser is on its way to the new session, and the rows
      // stream announces that session before the redirect lands) keeps its own element
      // through the swap: the state, the tilt it was pushed at, and the focus all live on it.
      const held = el.querySelector('[data-create-session] [aria-busy="true"]')
      if (held) { const fresh = n.querySelector('[data-create-session] button'); if (fresh) fresh.replaceWith(held) }
      el.replaceWith(n)
      if (!active) return
      const find = (sel) => sel && (n.matches(sel) ? n : n.querySelector(sel))
      if (filter) {
        const back = find('[data-filter="' + CSS.escape(filter.getAttribute('data-filter')) + '"]')
        if (back) { back.focus(); return }
      }
      if (wasCreate) {
        const back = find('[data-create-session] button')
        if (back) { back.focus(); return }
      }
      const sel = row && '[data-session="' + CSS.escape(row.getAttribute('data-session')) + '"]'
      const scope = find(sel) || n
      // A Stop that landed has taken its own control away, and the row's first focusable is
      // now the name — the stable open route, which is the way back in.
      const f = (wasAction && scope.querySelector('[data-stop]')) || scope.querySelector('a[href], button, input')
      if (f) f.focus()
    }
    const sessionsEl = () => document.querySelector('[data-sessions]')
    document.addEventListener('click', async (e) => {
      const b = e.target.closest('[data-stop]'); if (!b) return
      const row = b.closest('tr')
      const r = await fetch(b.getAttribute('data-post'), { method: 'POST' })
      if (r.ok) swap(row, await r.text())
    })
    // Archiving answers with the WHOLE table, not a row: it can move a session out of the
    // filter you are looking at, so the row is no longer the unit that changed. The query
    // rides along because the answer has to be rendered for the list this page is showing.
    document.addEventListener('click', async (e) => {
      const b = e.target.closest('[data-archive],[data-unarchive]'); if (!b) return
      const r = await fetch(b.getAttribute('data-post') + location.search, { method: 'POST' })
      if (r.ok) swap(sessionsEl(), await r.text())
    })
    // A filter or sort click is a navigation this page performs itself: adopt the href the
    // SERVER computed as the new location, then reopen the stream at it. The first frame is
    // the whole snapshot for the new query, so one click is one swap and no page reloads —
    // which is also why focus is never stranded.
    //
    // PUSHED, not replaced: the filter is the page's location, and the chips' whole claim to
    // being links is that a bookmark restores one and the back button undoes one. A replace
    // kept the first half and quietly broke the second — Back left the page — while the
    // `popstate` handler below waited for an event that a chip click could never produce.
    document.addEventListener('click', (e) => {
      const a = e.target.closest('[data-filter]'); if (!a) return
      if (e.metaKey || e.ctrlKey || e.shiftKey || e.button !== 0) return
      e.preventDefault()
      history.pushState(null, '', a.getAttribute('href'))
      openRows(true)
    })
    // Creating is deliberately NOT intercepted here (the reasoning is on the form itself) —
    // but it is MARKED. Between the push and the new page there is nothing on this one to
    // show for it, so the button stays down (`aria-busy`: held, filled, and saying what it
    // is doing) until the browser leaves. A second push in that window is refused: two
    // Creates are two sessions.
    document.addEventListener('submit', (e) => {
      const f = e.target.closest('[data-create-session]'); if (!f) return
      const b = f.querySelector('button')
      if (b.getAttribute('aria-busy') === 'true') { e.preventDefault(); return }
      b.setAttribute('aria-busy', 'true')
    })
    // Back here from the session — the page restored as it was left — the hold is over: the
    // act it was held for happened.
    window.addEventListener('pageshow', (e) => {
      if (e.persisted) document.querySelectorAll('[aria-busy="true"]').forEach((b) => b.removeAttribute('aria-busy'))
    })
    // A button goes in where it is touched (`[data-press]`, tailwind.css). The stylesheet does
    // the pressing off `:active`; what it cannot know is WHERE, so this hands it the touch
    // point as two numbers in [-1, 1] from the button's centre. A key has no where and gets
    // the centre — straight in — rather than the corner the mouse last left behind.
    document.addEventListener('pointerdown', (e) => {
      const b = e.target.closest('[data-press]'); if (!b) return
      const r = b.getBoundingClientRect()
      b.style.setProperty('--press-x', ((e.clientX - r.left) / r.width * 2 - 1).toFixed(2))
      b.style.setProperty('--press-y', ((e.clientY - r.top) / r.height * 2 - 1).toFixed(2))
    })
    document.addEventListener('keydown', (e) => {
      const b = e.target.closest('[data-press]'); if (!b) return
      b.style.setProperty('--press-x', '0'); b.style.setProperty('--press-y', '0')
    })
    // Declaring an MCP server (Plan 17): the only place a url is written, and the only
    // management action that can be REFUSED for a reason a human needs to read — a name
    // clash. So this one reports, where stop/archive only swap.
    const mcpSwap = (htmlText) => swap(document.querySelector('[data-mcp]'), htmlText)
    document.addEventListener('submit', async (e) => {
      const f = e.target.closest('[data-declare-mcp]'); if (!f) return
      e.preventDefault()
      const r = await fetch(f.getAttribute('action'), { method: 'POST', headers: { 'content-type': 'application/x-www-form-urlencoded' }, body: new URLSearchParams(new FormData(f)) })
      const text = await r.text()
      if (r.ok) { mcpSwap(text) } else { const p = f.querySelector('[data-mcp-error]'); if (p) p.textContent = text }
    })
    document.addEventListener('click', async (e) => {
      const b = e.target.closest('[data-mcp-withdraw]'); if (!b) return
      const body = new URLSearchParams({ name: b.getAttribute('data-mcp-withdraw'), session: b.getAttribute('data-mcp-audience') || '' })
      const r = await fetch(b.getAttribute('data-post'), { method: 'POST', headers: { 'content-type': 'application/x-www-form-urlencoded' }, body })
      if (r.ok) mcpSwap(await r.text())
    })
    // The stream is opened AT the current query and reopened when it changes; the server
    // filters per connection, so live status keeps arriving under whatever is being shown.
    // Its address is on the section it fills, like every other address on this page: the
    // server spells them all (`ManagerRoute`), and this script spells none.
    //
    // A stream reopened for a NEW query owes the page its first frame: until it lands, the
    // address says one filter and the rows say another. If it fails before then — offline, a
    // Manager mid-restart — the page reloads at the address it already has, which the server
    // renders correctly by construction. Only then: the same error on a stream that had
    // already delivered is an ordinary drop, and `EventSource` reconnects on its own.
    let rows = null
    const openRows = (moved) => {
      if (rows) rows.close()
      let settled = !moved
      rows = new EventSource(sessionsEl().getAttribute('data-stream') + location.search)
      rows.onmessage = (e) => { settled = true; if (e.data) swap(sessionsEl(), e.data) }
      rows.onerror = () => { if (!settled) { settled = true; location.reload() } }
    }
    openRows(false)
    // The back button moves the filter, so the stream has to move with it.
    window.addEventListener('popstate', () => openRows(true))
    """

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
    html $"""
        <tr class="border-b border-hair hover:bg-surface transition-colors" data-mcp-server="{name}">
          <td class="py-3 pr-4 align-middle {Style.body} truncate" title="{name}">{name}</td>
          <td class="py-3 pr-4 align-middle font-terminal text-code text-ink-faint truncate max-md:hidden"
              title="{McpTransport.describe declaration.Server.Transport}">{McpTransport.describe declaration.Server.Transport}</td>
          <td class="py-3 pr-4 align-middle {Style.small} truncate" title="{audience}">{audience}</td>
          <td class="py-3 pl-4 align-middle">
            <button type="button" class="{Style.btnDanger} w-full" data-mcp-withdraw="{name}" data-mcp-audience="{audienceValue}" data-post="{ManagerRoute.path ManagerRoute.WithdrawMcpServer}">Withdraw</button>
          </td>
        </tr>"""

/// The whole MCP section — the swap unit, so the count and the empty state can never go
/// stale against the rows they describe. This is the ONE place a url is ever written: a
/// session does not select from these, and the agent has no command that changes them.
let private mcpTemplate (views: ProcessManager.SessionView list) (declarations: McpDeclaration list) : TemplateResult =
    let rows =
        match declarations with
        | [] ->
            [ html $"""
                <tr>
                  <td colspan="4" class="py-10 text-center {Style.small}">no MCP servers — a session reaches only its own tools</td>
                </tr>""" ]
        | declarations -> declarations |> List.map (mcpRowTemplate views)
    let options =
        views
        |> List.map (fun view ->
            html $"""<option value="{SessionId.value view.Record.SessionId}">{view.Record.DisplayName}</option>""")
    html $"""
        <section class="flex flex-col gap-3" data-mcp>
          <div class="flex items-baseline gap-2.5">
            <span class="{Style.label}">MCP servers</span>
            <span class="font-semibold text-[11px] leading-4 tracking-[0.18em] text-ink-faint tabular-nums">{List.length declarations}</span>
          </div>
          <table class="{Col.table}">
            <thead>
              <tr class="border-b border-hair">
                <th scope="col" class="py-2 pr-4 {Style.label}">name</th>
                <th scope="col" class="py-2 pr-4 {Style.label} {Col.id}">address</th>
                <th scope="col" class="py-2 pr-4 {Style.label} {Col.reaches}">reaches</th>
                <th scope="col" class="py-2 pl-4 {Col.actions}"><span class="sr-only">actions</span></th>
              </tr>
            </thead>
            <tbody>{rows}</tbody>
          </table>
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
                  <span class="{Style.fieldSelectMark}">{Icon.down}</span>
                </div>
              </div>
              <button type="submit" class="{Style.btnPrimary}">Declare</button>
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
                          <code class="font-terminal text-code text-ink-faint break-all">{secret}</code>
                        </div>""")
            html $"""
                <tr class="border-b border-hair" data-hook-endpoint="{endpoint.Name}">
                  <td class="py-3 pr-4 align-top {Style.body}">{endpoint.Name}</td>
                  <td class="py-3 pr-4 align-top font-terminal text-code text-ink-faint break-all">{origin}/hooks/{endpoint.Name}</td>
                  <td class="py-3 pr-4 align-top font-terminal text-code text-ink-faint break-all" data-hook-declared>--webhook {WebhookRelay.EndpointSpec.encode endpoint.Declared}</td>
                  <td class="py-3 pl-4 align-top">{secrets}</td>
                </tr>""")
    html $"""
        <section class="flex flex-col gap-3" data-hooks>
          <div class="flex items-baseline gap-2.5">
            <span class="{Style.label}">hook endpoints</span>
            <span class="font-semibold text-[11px] leading-4 tracking-[0.18em] text-ink-faint tabular-nums">{List.length endpoints}</span>
          </div>
          <table class="{Col.table}">
            <thead>
              <tr class="border-b border-hair">
                <th scope="col" class="py-2 pr-4 {Style.label}">name</th>
                <th scope="col" class="py-2 pr-4 {Style.label}">deliver to</th>
                <th scope="col" class="py-2 pr-4 {Style.label} {Col.reaches}">declared as</th>
                <th scope="col" class="py-2 pl-4 {Style.label}">secret</th>
              </tr>
            </thead>
            <tbody>{rows}</tbody>
          </table>
        </section>"""

/// The MCP section, rendered as an action's answer.
let mcpSection (views: ProcessManager.SessionView list) (declarations: McpDeclaration list) : string =
    Ssr.render (mcpTemplate views declarations)

// The page keeps the workspace anatomy: the shared 88px header band (wordmark on the
// common baseline, hairline below), then labelled sections on one left rail. The body
// shell is `Style.app` (the visible viewport's height, overflow-hidden), so <main> owns the
// scrolling — a long registry scrolls under a fixed viewport instead of clipping.
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
        <main class="flex-1 min-w-0 overflow-y-auto">
          <!-- The registry's measure, not a reading column's. `name` is the only elastic
               column, and it gets whatever the fixed ones leave: at the 896px this used to
               be, five columns (name, id, created, status, and a rail carrying two controls)
               left it 38px and every session on the page rendered as a single letter and an
               ellipsis. 1152px leaves it 318px — wider than the 254px it had when there were
               four — and gives the MCP table's address column room it was already short of. -->
          <div class="max-w-6xl w-full mx-auto flex flex-col px-8 max-md:px-4">
            <header class="h-[88px] shrink-0 flex items-end pb-5 border-b border-hair">
              <h1 class="{Style.wordmark}">yession<span class="text-green">.</span> <span class="{Style.label}">manager</span></h1>
              <!-- The Manager's own build, in the same faint mono step the rows use for
                   theirs. It belongs beside them because it is the same question asked of a
                   different process, and because the Manager is the one that CANNOT roll
                   forward on its own: it keeps the image it exec'd until something restarts
                   it, so it is routinely the oldest thing on the page.
                   It yields at the SAME width its rows do, though the header has room to
                   spare: the answer here is a comparison, and a page showing one process's
                   build while hiding every other's reads as the Manager's version banner —
                   which is the thing that was already there to be misread. -->
              <span class="font-terminal text-code-sm text-ink-faint tabular-nums ml-3 pb-0.5 max-xl:hidden" data-manager-build>{Version.current}</span>
            </header>
            <div class="pt-6 pb-10">{tableTemplate query views}</div>
            <div class="pb-10">{mcpTemplate views declarations}</div>
            <!-- Only when there are any: a deployment that declared no hook endpoints has
                 nothing to say here, and an empty table would imply a thing to fill in. -->
            {hooksSection}
          </div>
        </main>"""

/// `styleSheetUrl` is passed in rather than read from the module below: F# scopes top-down, and
/// the stylesheet's address is derived from bytes read further down the file.
let page
    (styleSheetUrl: string)
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
        WebApp.managerHeadTags (ManagerRoute.path ManagerRoute.Icon)
        sprintf "</head><body class=\"%s\">" Style.app
        Ssr.render (bodyTemplate access query views declarations hooks)
        sprintf "<script>%s</script>" script
        "</body></html>"
    ]

// --- Routing ----------------------------------------------------------------------------

/// One field of a form-encoded body. Absent reads as empty, which is what every reader below
/// tests for — a form that named the field and left it blank and a form that did not name it
/// are the same submission.
let private formField (body: string) (name: string) : string =
    match (Node.Api.URLSearchParams.Create body).get name with
    | Some value -> value
    | None -> ""

/// The same static asset service the Session Process runs, over this process's OWN set — read
/// and addressed once at boot rather than per request, so every render of this page (it is
/// rendered per request) names the same bytes. Where the set lives is `Assets`' own business,
/// which is also why this page can link a stylesheet whose faces this file has never heard of.
let private assets = Assets.configured ()

let private cssUrl = ManagerRoute.path (ManagerRoute.asset assets.Build AssetFile.``app``)

/// The icon's constant is base64 (it lives in source); the wire wants the PNG. Same decode
/// the session server does, for the same reason — `res.end` takes what Node's `end` takes.
let private decodeBase64 (encoded: string) : string =
    unbox (buffer.Buffer.from (encoded, BufferEncoding.Base64))

let private respondWith (res: ServerResponse) (status: int) (contentType: string) (cacheControl: string) (body: string) =
    res.writeHead (status, createObj [ "content-type", box contentType; "cache-control", box cacheControl ]) |> ignore
    res.``end`` body

/// Every management response but one is a live view of mutable process state, so `no-store` is
/// the default here. The stylesheet is the exception — see its route.
let private respond (res: ServerResponse) (status: int) (contentType: string) (body: string) =
    respondWith res status contentType "no-store" body

let private html (res: ServerResponse) (body: string) = respond res 200 "text/html; charset=utf-8" body

/// POST-redirect-GET. `303` rather than `302` so the browser is required to follow it with a
/// GET: what the caller lands on is a resource, not a resubmission of the form waiting to be
/// re-fired by a reload.
let private seeOther (res: ServerResponse) (location: string) =
    res.writeHead (303, createObj [ "location", box location; "cache-control", box "no-store" ]) |> ignore
    res.``end`` ""

/// A string as a JS literal, for the one inline script below — so a URL containing a quote
/// is data rather than syntax.
let private jsonLiteral (s: string) : string = Fable.Core.JS.JSON.stringify s

/// GET a URL and report the status its answer carried; `0` when nothing answered at all.
/// Redirects are followed, because a session that bounces its shell through sign-in has
/// still answered. Nothing reads the body, so nothing reads it.
let private statusOf (url: string) : Async<int> =
    async {
        let! attempt =
            Http.attempt
                (fun _ -> Promise.lift ())
                url
                [ Fetch.Types.RequestProperties.Redirect Fetch.Types.RedirectMode.Follow
                  Fetch.Types.RequestProperties.Cache Fetch.Types.RequestCache.Nostore ]

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
let private standalonePage (title: string) (body: string) : string =
    sprintf
        """<!doctype html>
<html lang="en"><head><meta charset="utf-8"><title>%s</title>
<meta name="viewport" content="width=device-width, initial-scale=1">
%s%s
</head><body class="%s">
<main class="max-w-[32rem] mx-auto my-16 px-4 flex flex-col gap-3">
<h1 class="%s">%s</h1>
%s
</main>
</body></html>"""
        (Ssr.escapeText title)
        (Style.headTags cssUrl)
        (WebApp.managerHeadTags (ManagerRoute.path ManagerRoute.Icon))
        Style.standalone
        Style.heading
        (Ssr.escapeText title)
        body

/// What `/open` says when it cannot hand the browser over: the refusal, and the way back to
/// the page that can do something about it.
let private problemPage (title: string) (detail: string) : string =
    standalonePage
        title
        (sprintf """<p class="%s">%s</p>
<p><a class="%s" href="%s">Back to the session manager</a></p>""" Style.body (Ssr.escapeText detail) Style.proseLink (ManagerRoute.path ManagerRoute.Home))

/// A refusal a BROWSER is holding: the status it deserves, and a page that says so.
let private problem (res: ServerResponse) (status: int) (title: string) (detail: string) =
    respond res status "text/html; charset=utf-8" (problemPage title detail)

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
/// It hands the browser to the session's SIGN-IN entry (`/login`), not its shell. Every
/// session this page can name was launched by this Manager, so every one of them gates its
/// data behind the Manager's bounce — and a browser that landed on the shell first painted
/// it, ran the client, asked `/me`, was told 401, and went round the bounce to land on the
/// shell a second time: two full loads of the same page, with a repaint between them, before
/// a person saw anything they could use. Entering through `/login` runs the bounce first and
/// paints once; a browser already holding the session's cookie is sent straight on by that
/// route (`Signalling.fs`), so the return visit pays one redirect, never a second sign-in.
let private openingPage (target: string) (readyUrl: string) : string =
    standalonePage
        "Opening session…"
        (sprintf
            """<p id="status" class="%s">Waiting for it to answer.</p>
<p><a id="target" class="%s" href="%s">Open it directly</a></p>
<script>
  const target = %s
  const ready = %s
  let attempts = 0
  async function poll () {
    attempts++
    try {
      // `ok`, not "the request settled". A front door that has not mapped this session yet
      // answers — with a 404, or a gateway error — and a fetch that only caught THROWN
      // requests reads that as the session answering, redirects into it, and leaves whoever
      // pressed Create looking at the front door's 404. This route reports the difference.
      const answer = await fetch(ready, { cache: 'no-store' })
      if (answer.ok) { location.replace(target); return }
    } catch (e) { /* the Manager itself is unreachable: the same wait, bounded the same way */ }
    if (attempts >= 40) {
      document.getElementById('status').textContent =
        'The session started, but its address is not answering after 20 seconds. ' +
        'If this deployment maps session ports through a proxy, that mapping has not appeared.'
      return
    }
    setTimeout(poll, 500)
  }
  poll()
</script>"""
            Style.body
            Style.proseLink
            (Ssr.escapeAttr target)
            (jsonLiteral target)
            (jsonLiteral readyUrl))

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
    // durable state the caller can resolve, which matters most for `/open`, the URL a session
    // client's reconnect card points at. This picks the STATUS CODE and nothing else: the
    // refusal itself is `ManagerState.launchable`'s, and its words are what gets sent, so the
    // rule and the code it is reported under cannot drift apart.
    let refusalStatus (sessionId: SessionId) =
        match pm.TryFind sessionId with
        | Some view when view.Record.ArchivedAt.IsSome -> 409
        | _ -> 500
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
            html res (page cssUrl pm.Public query (pm.Sessions ()) (pm.McpServers ()) pm.HookEndpoints)
        | ManagerRoute.Asset (build, file) ->
            // Everything static this build ships, served by path and by nothing else — the
            // same service the Session Process runs, over this process's own set. The Manager
            // page links the stylesheet, and the stylesheet names its own faces; neither this
            // route nor this file knows what those are, which is the point: a build that adds
            // an asset adds a file, not a case.
            Assets.serve assets build file res
        | ManagerRoute.Icon ->
            // The same mark the session shells wear, from the same constant, at the address
            // the page emits for it.
            res.writeHead (
                200,
                createObj [ "content-type", box "image/png"; "cache-control", box CachePolicy.shell ])
            |> ignore
            res.``end`` (decodeBase64 WebApp.iconPngBase64)
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
                let id =
                    match formField body "id" with
                    | "" -> SessionId.value (SessionId.mint ())
                    | provided -> provided
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
                let audience =
                    match formField body "session" with
                    | "" -> Ok AnySession
                    | id -> SessionId.create id |> Result.map OneSession
                let declared =
                    match McpServerName.create (formField body "name"), audience with
                    | Error e, _ -> Error e
                    | _, Error e -> Error e
                    | Ok name, Ok audience ->
                        match formField body "url" with
                        | "" -> Error "an MCP server needs an address"
                        | url ->
                            Ok
                                { Server =
                                    { Name = name
                                      Transport = McpHttp url
                                      Description =
                                        (match formField body "description" with
                                         | "" -> None
                                         | description -> Some description) }
                                  Audience = audience }
                match declared |> Result.bind pm.DeclareMcpServer with
                | Ok () -> html res (mcpSection (pm.Sessions ()) (pm.McpServers ()))
                | Error e -> respond res 400 "text/plain" e)
        | ManagerRoute.WithdrawMcpServer ->
            readBody req (fun body ->
                let audience =
                    match formField body "session" with
                    | "" -> Ok AnySession
                    | id -> SessionId.create id |> Result.map OneSession
                match McpServerName.create (formField body "name"), audience with
                | Error e, _
                | _, Error e -> respond res 400 "text/plain" e
                | Ok name, Ok audience ->
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
        // No control on the page posts here any more — opening a session launches it
        // (`OpenSession`, below) — but the verb is still the Manager's HTTP API for starting
        // one WITHOUT a browser to hand over, which is what the composition suite drives.
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
        // reconnect offer points at. Launch it if it is stopped, then hand the browser
        // to wherever this deployment says the session lives.
        | ManagerRoute.OpenSession sessionId ->
            Async.StartImmediate (
                async {
                    match pm.TryFind sessionId with
                    | None ->
                        problem res 404 "No such session" (sprintf "This Manager has no session %s." (SessionId.value sessionId))
                    | Some view ->
                        // Already running is the common case once a client has
                        // reconnected on its own; asking for the port it already
                        // has is not a relaunch.
                        let! port =
                            match view.Status with
                            | ProcessManager.Running (port, _, _) -> async { return Ok port }
                            | ProcessManager.NotRunning
                            | ProcessManager.Exited _ -> pm.Launch sessionId
                        match port with
                        | Error reason -> problem res (refusalStatus sessionId) "Cannot open this session" reason
                        | Ok port ->
                            let address = PublicAccess.sessionAddress sessionId port pm.Public
                            html
                                res
                                (openingPage
                                    (RelativeUrl.under address.Url (SessionRoute.relative Login))
                                    (ManagerRoute.path (ManagerRoute.SessionReady sessionId)))
                })
        // Does this deployment's front door reach the session yet? The question the
        // opening page above is really asking, answered HERE because here is the only
        // place it CAN be answered: a browser can read the status of a same-origin
        // address and learns nothing at all about a cross-origin one (an opaque `no-cors`
        // answer carries status `0` whether the session served the page or the proxy
        // served a 404), while this process has no origin to be blinded by. The page that
        // guessed instead redirected whoever pressed Create straight into the front
        // door's 404, a few hundred milliseconds before the mapping appeared.
        //
        // Read by a script, so `text/plain` — unlike `OpenSession` beside it, nothing
        // navigates here.
        | ManagerRoute.SessionReady sessionId ->
            match pm.TryFind sessionId with
            | None -> respond res 404 "text/plain" (sprintf "unknown session %s" (SessionId.value sessionId))
            | Some view ->
                match view.Status with
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
                            if answeredFor status then respond res 200 "text/plain" "ready"
                            else
                                respond
                                    res
                                    503
                                    "text/plain"
                                    (sprintf "%s answered %d" address.Url status)
                        })
    // Behind the identity gate either way: a malformed id is answered by the Manager, but
    // only to someone the Manager would answer at all.
    let gated (answer: unit -> unit) =
        Async.StartImmediate (
            async {
                match! identify req with
                | Denied reason -> respond res 401 "text/plain" reason
                | Attributed _ | Unattributed _ -> answer ()
            })
        true
    match ManagerRoute.parse req.``method`` path with
    | Error ManagerMiss.Unclaimed -> false
    | Error (ManagerMiss.MalformedSessionId (raw, reason)) ->
        gated (fun () -> respond res 400 "text/plain" (sprintf "%s is not a session id: %s" raw reason))
    | Ok route -> gated (fun () -> handle route)
