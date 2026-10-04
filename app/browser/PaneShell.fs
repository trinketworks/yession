module Yession.Browser.PaneShell

// The bits of the pane that the model drives but a Lit render cannot do: moving focus, and
// the root class the column's open state is expressed as (Plan 13; Plan 14, stages 2 and 5).
//
// The chat and the pane are two columns, and tapping a chip in one puts something new in the
// other. Focus has to follow, or a keyboard user presses Enter on a chip and stays exactly
// where they were with no way of knowing anything happened. Closing that tab has the mirror
// problem: the control that was focused leaves the document.
//
// Its own module because two entry points need all of it — the app (`Browser.fs`) and the
// host-free shell harness the `Browser`-tier E2E drives. A second copy would be a second
// thing to keep correct, and the one that rotted would be the one nothing runs.
//
// Everything here waits a frame: the model changes first, Lit renders second, and `focus()`
// on an element that is not in the document yet is a no-op.
//
// This file used to be `[<Emit>]` bodies — a hundred-odd lines of JavaScript in strings, and
// most of it POLICY rather than binding: which element is stranded, how a tab key becomes a
// selector, what the splitter's bounds are. Emit bodies are inlined into whatever calls them,
// and Fable does not treat a change to one as a change to its callers, so editing this file
// silently left every compiled caller stale — a test failing for a reason its source does not
// contain, three runs of the browser tier before anybody looked at the bundle. Ordinary F# has
// none of that: it is a module, so what depends on it recompiles. What is genuinely a binding
// went to `Fable.BrowserExtras`; the rest is `Browser.Dom`, typed, and readable.

open Browser.Dom
open Browser.Types
open Browser.WebStorage
open Fable.BrowserExtras

/// A frame later, which is when the render that has to have happened, has. Every act in this
/// module is "the model already changed, now do the part the DOM owns", and every one of them
/// needs the element to be in the document before it can be touched.
let private nextFrame (act: unit -> unit) : unit =
    window.requestAnimationFrame (fun _ -> act ()) |> ignore

/// The first element matching, as something focusable. Everything here selects by `data-*`
/// attributes the view puts on real controls, so the cast is the view's contract rather than
/// an assumption: a hook on something unfocusable would be the bug, not this.
let private find (selector: string) : HTMLElement option =
    match document.querySelector selector with
    | null -> None
    | element -> Some (element :?> HTMLElement)

let private focusOn (element: HTMLElement option) : unit =
    element |> Option.iter (fun element -> element.focus ())

/// Make the browser flush layout, by reading something it can only answer by doing so. The
/// value is thrown away and the READ is the point, which is a thing to say out loud because it
/// looks exactly like a line that could be deleted. (esbuild keeps it, minified or not: a
/// property access may be a getter, so it is never assumed pure.)
let private reflow (element: HTMLElement) : unit = element.offsetWidth |> ignore


/// A CSS length as a number, with the unit dropped. `getPropertyValue` answers `"420px"` for a
/// property set in pixels and `""` for one that is not set at all, and both have to become "no
/// number I can use" rather than a parse that quietly succeeds at zero.
let private trimPx (value: string) : string = value.Trim().Replace ("px", "")

/// Whether focus has been left nowhere it can act from — on `body`, or nowhere at all — or
/// inside something that is on its way out of the document.
///
/// This is the guard that lets a focus move run from the render loop rather than from a press.
/// A press knows it is about to remove the thing under the hand; a render does not, and a
/// render that moved focus unconditionally would yank a caret out of whatever somebody was
/// typing in the moment something unrelated changed elsewhere on the page.
let private stranded (leaving: string list) : bool =
    match document.activeElement with
    | null -> true
    | active ->
        System.Object.ReferenceEquals (active, document.body)
        || leaving |> List.exists (fun selector -> (active.closest selector).IsSome)

/// Move focus into the side pane after a chip opened a tab there — onto its panel, or, in a
/// pane with nothing to show, onto its `all` item, which is the door to everything it has.
let private toPane () : unit =
    find "[data-pane-panel]"
    |> Option.orElseWith (fun () -> find "[data-pane-switcher]")
    |> focusOn

/// Onto where this peer types into a terminal. A terminal whose keyboard this peer HOLDS takes
/// keystrokes on its screen — the command line under it still queues for the hand-back, but
/// somebody who took the keyboard took it to type at the shell, so the screen comes first.
/// Otherwise THIS peer's command line, which is the one that is not `readonly` — every other
/// author's line in the same terminal is drawn beside it, read-only — and it is there whoever
/// else holds the keyboard. A closed terminal has neither, and the panel is what is left.
/// Never nothing: focus that lands nowhere lands on `body`.
let private toCommandLine (terminal: Yession.Domain.TerminalId) : unit =
    find (sprintf "[data-terminal-screen=\"%s\"][tabindex=\"0\"]" (Yession.Domain.TerminalId.value terminal))
    |> Option.orElseWith (fun () ->
        find (sprintf "[data-terminal-input^=\"%s\"]:not([readonly])" (Yession.Domain.Collab.BodyKey.terminalDraftsIn terminal)))
    |> Option.orElseWith (fun () -> find "[data-pane-panel]")
    |> focusOn

/// Onto the control that shows the pane again — present exactly while the pane is shut, so a
/// frame after the render that shut it, it is there.
let private toPaneReopen () : unit = focusOn (find "[data-content-toggle=\"show\"]")

/// Onto the empty pane's press, or the strip's `+` when the pane is not empty after all — the
/// same act from the other place it is offered.
let private toPaneEmpty () : unit =
    find "[data-terminal-new]" |> Option.orElseWith (fun () -> find "[data-pane-new]") |> focusOn

/// Onto one terminal's row on the `all` page, or the page's pivot item when it has gone since.
let private toSwitcherRow (terminal: Yession.Domain.TerminalId) : unit =
    find (sprintf "[data-content-list] [data-terminal-list-row=\"%s\"]" (Yession.Domain.TerminalId.value terminal))
    |> Option.orElseWith (fun () -> find "[data-pane-switcher]")
    |> focusOn

/// Into the `all` page as it opens: onto the row of the terminal the pane is about (it says
/// so with `aria-current`), else the first name in it, else the page itself — an empty one
/// still holds the focus it was given — else its pivot item.
///
/// A frame late by construction, so a reader quick enough to have moved inside the page
/// already — a click on a row's verb, the arrow walk — is where they meant to be, and is left
/// there: this places focus that has nowhere to be yet, never focus somebody put down.
let private toSwitcher () : unit =
    let inside =
        match document.activeElement with
        | null -> false
        | active -> (active.closest "[data-content-list]").IsSome
    if not inside then
        find "[data-content-list] [data-terminal-list-row][aria-current=\"true\"]"
        |> Option.orElseWith (fun () -> find "[data-content-list] [data-terminal-list-row], [data-content-list] [data-artifact-list-row]")
        |> Option.orElseWith (fun () -> find "[data-pane-panel]")
        |> Option.orElseWith (fun () -> find "[data-pane-switcher]")
        |> focusOn

/// Onto the pivot's selected item, once the `all` page has gone: the terminal or preview it
/// was laid over. With nothing under it the pane is empty, and its press is what is there.
let private toPivot () : unit =
    find "[data-pane-pivot] [role=\"tab\"][aria-selected=\"true\"]"
    |> Option.orElseWith (fun () -> find "[data-terminal-new]")
    |> Option.orElseWith (fun () -> find "[data-pane-switcher]")
    |> focusOn

/// Onto one terminal's tab, or the panel when it has left the strip since.
let private toTab (terminal: Yession.Domain.TerminalId) : unit =
    find (sprintf "[data-pane-tab=\"%s\"]" (Yession.App.ClientModel.tabKey terminal))
    |> Option.orElseWith (fun () -> find "[data-pane-panel]")
    |> focusOn

/// Whether something painted over this element's centre is not the element — on a phone, the
/// pane laid over the whole chat column. Measured rather than assumed from a breakpoint,
/// because what decides it is the layout, and the layout is the one thing that knows. An
/// element scrolled out of the viewport has no centre on screen to ask about, and is NOT
/// covered: focusing it scrolls it into view, which is what a reader sent back to it wants.
let private covered (element: HTMLElement) : bool =
    let rect = element.getBoundingClientRect ()
    let x = rect.left + rect.width / 2.0
    let y = rect.top + rect.height / 2.0
    if x < 0.0 || y < 0.0 || x > window.innerWidth || y > window.innerHeight then false
    else
        match document.elementFromPoint (x, y) with
        | null -> false
        | hit -> not (element.contains hit)

/// Return focus to the chat item that opened a preview, once the preview is going — the
/// subject is the one thing the chip and the preview share (`PreviewSubject.chip`).
///
/// Where the chip cannot take it, focus goes to the pane: a chip that has scrolled out of the
/// rendered chat, a preview no chip opened (the list's file rows, the agent's `focus_tab`),
/// and a chip the pane is painted over — on a phone the pane IS the column, and a back that
/// sent focus behind it would put the cursor somewhere nobody can see. The way back into a
/// SHUT pane comes first, because hiding the pane is the other thing that sends focus here,
/// and then the panel the preview gave way to.
let private toChatItem (subject: Yession.App.PreviewSubject) : unit =
    let chip =
        let hook, value = Yession.App.PreviewSubject.chip subject
        find (sprintf "[%s=\"%s\"]" hook value)
    chip
    |> Option.filter (fun chip -> not (covered chip))
    |> Option.orElseWith (fun () -> find "[data-content-toggle=\"show\"]")
    |> Option.orElseWith (fun () -> find "[data-pane-panel]")
    |> focusOn

/// Hand focus to a terminal's watch toggle when the reader has been stranded (Plan 14,
/// stage 7; Plan 25, stage 3).
///
/// There used to be four controls here — rewind, jump-to-live, play, back-to-blocks — each
/// swapping another out of the document, so every press risked stranding focus and this had
/// to name all four to catch whichever had survived. One toggle that relabels in place needs
/// none of that: a press keeps its own focus.
///
/// What is left is the case no press causes. A rewound cast playing off its end unmounts the
/// player by itself, under whoever was reading it.
///
/// No terminal id: the toggle's VALUE is the face it will show, not which terminal it is
/// about, and the pane shows one tab at a time — so there is exactly one of these in the
/// document and naming a terminal could only ever name it wrongly.
let toWatchToggle () : unit =
    nextFrame (fun () ->
        if stranded [ "[data-pane-replay]" ] then focusOn (find "[data-terminal-watch]"))

/// Hand focus to the live screen when this peer has just become the one typing into it.
///
/// Taking the keyboard is the whole of what live mode IS, and until this the keyboard did not
/// follow it. Pressing `take` removes the `take` button in the render the lease arrives on, so
/// focus falls to `body` and the person then types into nothing, which is indistinguishable
/// from a terminal that does not work.
///
/// The other route in leaves focus somewhere that is no longer where the keys belong. The
/// alt-screen flip hands a block's author the terminal it just took over — `vim`, typed into
/// this terminal's command line and run from it — and the command line stays (it queues for
/// the hand-back), so the person's focus is still in it, about to type `i` into a line under
/// the editor rather than into the editor. Being in THIS terminal's command line when its
/// keyboard becomes yours is counted as stranded.
///
/// The guard is what makes this safe to run from the render loop: a lease can land on a
/// terminal while its holder is reading somewhere else entirely (the alt-screen flip follows
/// the block's AUTHOR, and the agent's blocks flip too), and yanking a caret out of the
/// message composer because a terminal three tabs away went full-screen would be worse than
/// the stranding it fixes.
///
/// `tabindex="0"` as well as the terminal's id: the screen renders in three variants and only
/// the holder's takes keystrokes.
let toTerminalScreen (terminal: Yession.Domain.TerminalId) : unit =
    nextFrame (fun () ->
        let ownLine =
            sprintf "[data-terminal-input^=\"%s\"]" (Yession.Domain.Collab.BodyKey.terminalDraftsIn terminal)
        if stranded [ ownLine ] then
            focusOn (find (sprintf "[data-terminal-screen=\"%s\"][tabindex=\"0\"]" (Yession.Domain.TerminalId.value terminal))))

/// Scroll a terminal's history to one of its commands, and say which one (Plan 25, stage 3).
///
/// The other half of "show in terminal": the model moves the reader's POSITION to that block,
/// and this is the part a rendered string cannot do. One shot, from the press — not from the
/// render — because a reveal repeated on every render would fight the reader's own scrolling
/// the moment a record arrived.
///
/// It runs a frame late for the reason everything else here does (the element has to exist),
/// and that frame is also what puts it after `Tail.restore`, which returns a freshly rendered
/// scrollback to its end. Landing after it is the whole trick: the reveal wins once — a scroll
/// UP, which `Tail` reads as the reader leaving the end — and every render afterwards puts
/// back the position the reader was left at, so the two never fight.
///
/// The mark is an animation that ends. A block scrolled to in a wall of identical mono is
/// still a block nobody can pick out; a permanent highlight would still be pointing at it
/// long after the reader had moved on.
let revealBlock (terminalId: string) (blockId: string) : unit =
    nextFrame (fun () ->
        let scrollback =
            find (sprintf "[data-terminal-scrollback][data-terminal-id=\"%s\"]" terminalId)
        let block =
            scrollback
            |> Option.bind (fun scrollback ->
                match scrollback.querySelector (sprintf "[data-terminal-block=\"%s\"]" blockId) with
                | null -> None
                | block -> Some (block :?> HTMLElement))
        block
        |> Option.iter (fun block ->
            block.scrollIntoView ()
            // Restart the animation rather than add a class that is already there: removing
            // it, forcing a reflow by READING a layout property, and adding it back is the
            // only way to replay a CSS animation on an element that has already run it. The
            // read is the load-bearing line, which is why it is not `ignore` on a call that
            // does something — nothing is computed here, the browser is being made to flush.
            // `-line` and not the flash a MESSAGE gets: that one lights a padded ground and
            // rims it, and a block has neither — it is a bare `flex flex-col`, so an edge
            // would be drawn hard against the command's own glyphs.
            block.classList.remove [| "animate-reveal-line" |]
            reflow block
            block.classList.add [| "animate-reveal-line" |]))

// --- The nav column -------------------------------------------------------------------
// Here rather than in `Browser.fs` for the reason this module exists: the host-free harness
// drives the same moves, and the nav drawer and this pane are the two sheets a phone has.

/// The first control matching, focused. Nothing to do when there is none: every selector here
/// names the control the view mounts OPPOSITE the one that just went, so a miss is a face that
/// has not arrived rather than a state to repair.
let private focusFirst (selector: string) : unit = focusOn (find selector)

/// The shell's layout bits, which live on the ROOT element — outside `#app`, which is what
/// makes them survive every re-render.
let private rootClasses () = document.documentElement.classList

/// Whether the stylesheet's own desktop breakpoint matches right now — ASKED, never decided
/// again here. `mediaMatches` carries the reason: a script comparing `innerWidth` to a number
/// of its own is a second definition of the breakpoint, and it disagrees with the first
/// whenever a scrollbar, a zoom or a rounded viewport gets between them.
let onDesktop () : bool = mediaMatches Yession.App.Style.wideMedia

/// Move focus onto the settings face's counterpart control, TWO frames on.
///
/// Two, because the face that is arriving is `visibility: hidden` until the transition it just
/// started reaches its first style flush, and `focus()` on a hidden element is a no-op — which
/// was measured: one frame left focus on `<body>`.
let private focusSettingsFace (selector: string) : unit =
    nextFrame (fun () -> nextFrame (fun () -> focusFirst selector))

// The sidebar/drawer state is one bit on the root element, outside `#app`, so it survives
// every re-render: default = sidebar visible on desktop, off-canvas on mobile; `nav-alt`
// = the inverse (see Style.sidebar).
//
// Collapsing is a PREFERENCE on desktop, so it is remembered; on mobile the same bit means
// "the drawer is open", which is not a preference and is never stored. The stored value is
// re-applied before first paint by the shell document's one inline script (`Ssr.page`) — here,
// only written.
//
// Bringing the column ON SCREEN, which every way in shares: `nav-alt` means the opposite thing
// on each side of the breakpoint — uncollapsed on desktop, drawer-open on a phone.
//
// And on a phone the drawer is one of TWO sheets over the chat. The content pane is the other,
// at the same layer and later in the document, so a drawer opened while the pane was up opened
// UNDERNEATH it: open, holding focus, and nowhere to be seen. Raising the drawer over the pane
// would have worked too, and left two sheets stacked over the chat with two ways back that go
// to different places; closing the pane first means there is only ever one, and the drawer's
// own way back lands on the chat — which is where the pane's goes as well. The pane is the
// model's (`HideContentMsg`), the breakpoint is the shell's, so the shell is where the two meet.
//
// One verb for every way the column arrives — the nav toggle, settings, a call to action that
// reveals settings — so the next way in cannot open the drawer and forget the pane.
let private bringColumnOn (hidePane: unit -> unit) : unit =
    let classes = rootClasses ()
    if onDesktop () then classes.remove "nav-alt"
    else
        hidePane ()
        classes.add "nav-alt"

// Focus is moved deliberately: the control that was pressed is the one about to disappear, so
// it hands focus to whichever control replaces it (the header's reopen chevron, or the nav
// head's collapse button). Skipping that strands focus on a hidden element.
let toggleNav (hidePane: unit -> unit) () : unit =
    let classes = rootClasses ()
    let desktop = onDesktop ()
    // Whether the column is SHOWN once this press lands. The bit is read against the
    // breakpoint, because `nav-alt` means the opposite thing on each side of it.
    let shown = not (desktop <> classes.contains "nav-alt")
    if shown then bringColumnOn hidePane
    elif desktop then classes.add "nav-alt"
    else classes.remove "nav-alt"
    // The nav control always returns the column to its workspace face — a column that
    // reopened on settings would be a surprise, and `settings-open` is what chooses the face.
    classes.remove "settings-open"
    if desktop then
        // Storage is denied in a private window, and a collapse that cannot be remembered is
        // still a collapse that works.
        try
            Browser.WebStorage.localStorage.setItem ("yession.nav", (if shown then "open" else "collapsed"))
        with _ ->
            ()
    nextFrame (fun () ->
        focusFirst (if shown then "button[data-nav-toggle=\"hide\"]" else "[data-nav-toggle=\"show\"]"))

// Settings is the sidebar column's other FACE (Style.settingsPane), not a drawer over the
// conversation — so opening it has to bring that column on screen (`bringColumnOn`). Focus
// follows the same rule as the nav toggle.
let toggleSettings (hidePane: unit -> unit) () : unit =
    let classes = rootClasses ()
    let opening = not (classes.contains "settings-open")
    if opening then classes.add "settings-open" else classes.remove "settings-open"
    if opening then bringColumnOn hidePane
    elif not (onDesktop ()) then
        // Closing the face on a phone closes the drawer with it. On a desktop the column
        // stays exactly where it was: what changed is which face it shows, not whether it
        // is there.
        classes.remove "nav-alt"
    focusSettingsFace (if opening then "[data-settings-toggle=\"close\"]" else "[data-settings-toggle=\"open\"]")

// The same move, in one direction only.
//
// A call to action that leads to settings must never TAKE somebody there and back: the
// prompt over the timeline is on screen whenever a credential needs signing in, including
// while the settings face is already open, and a toggle there would shut the very panel it
// is pointing at. The nav pivots stay toggles because a pivot is a two-way control and this
// is not one.
//
// Idempotent by construction rather than by the caller checking first — `settings-open` is
// SET, not flipped, so pressing it twice is pressing it once.
let revealSettings (hidePane: unit -> unit) () : unit =
    let classes = rootClasses ()
    let wasOpen = classes.contains "settings-open"
    classes.add "settings-open"
    bringColumnOn hidePane
    // Focus moves only when the face actually ARRIVED. Stealing it from whatever the reader
    // was doing, to a control that was already on screen, would be the prompt reaching into a
    // panel they are already reading.
    if not wasOpen then focusSettingsFace "[data-settings-toggle=\"close\"]"

/// On a phone the sidebar is a DRAWER over the conversation, so a jump made from inside it
/// lands behind it: the message is scrolled, flashed and focused under a sheet the reader is
/// still looking at. On a desktop the column is beside the conversation and nothing has to
/// move, which is what the query answers.
///
/// `nav-alt` is the same bit `toggleNav` writes, and on a phone it means the drawer is open.
let private closeNavDrawer () : unit =
    if not (onDesktop ()) then
        document.documentElement.classList.remove [| "nav-alt" |]

/// Scroll the conversation to one message and flash it — deliberately the same two moves
/// `revealBlock` makes: scroll it into view, then replay the reveal animation so the eye can
/// find which line moved. A jump that only scrolled would leave a reader looking at a screen
/// of text with no idea which of it they asked for.
///
/// Getting the drawer out of the way is part of taking somebody to a message, so it happens
/// HERE rather than at the one caller that can be inside one. It is a no-op from the timeline,
/// where no drawer is open, and the next surface that jumps from behind one does not have to
/// remember the rule.
let revealMessage (messageId: string) : unit =
    closeNavDrawer ()
    nextFrame (fun () ->
        find (sprintf "[data-conversation] [data-message-id=\"%s\"]" messageId)
        |> Option.iter (fun item ->
            // To the MIDDLE, unlike `revealBlock` above: this scrollport pins the author
            // line at its top, so aligning to the start puts the message under the very line
            // that says who said it. `Scrolling.scrollIntoMiddle` carries the rest of why.
            scrollIntoMiddle item
            // Removing, forcing a reflow by READING a layout property, and adding it back is
            // the only way to replay an animation an element has already run. See
            // `revealBlock`, whose comment this is the other half of.
            item.classList.remove [| "animate-reveal" |]
            reflow item
            item.classList.add [| "animate-reveal" |]
            // Take the reader's CURSOR there, not just their eye — a keyboard reader who asked
            // to be taken to a message and was left with focus on the control that scrolled
            // away has been shown the message and stranded away from it. The rail and the reply
            // ref both come through here, so both move focus by this one line. The article is
            // already centred and visible above, so `focus()` finds nothing to scroll and does
            // not fight that placement; `tabindex="-1"` is what lets it land on an article at
            // all — focusable on purpose, never a Tab stop.
            item.focus ()))

/// The "jump to latest" press: the surface followed from its end (`Tail.follow`), and the
/// keyboard put where the reader now is. The press takes its own control away — a surface at
/// its end has no way back to offer — so focus has to land somewhere or it lands on `body`:
/// on the surface itself when it can hold focus (the chat, a screen whose keyboard this peer
/// has), else on the nearest box around it that can (the pane's panel, around a terminal's
/// blocks). Never a Tab stop of its own making — every one of those is `tabindex` already.
let jumpToLatest (surface: Yession.App.TailSurface) : unit =
    Tail.follow surface
    |> Option.bind (fun el ->
        match el.closest "[tabindex]" with
        | Some holder -> Some (holder :?> HTMLElement)
        | None -> None)
    |> focusOn

/// Put focus back on one item's actions control, once the menu it opened has gone.
///
/// A frame later like every act here, and for the sharper reason: the control is what the
/// menu was hanging off, and the render that removed the menu is the one that has to have
/// happened before this can find it. Nothing to fall back to if it cannot — the item has
/// scrolled out of the window this client holds, and there is no second right place for a
/// cursor that was inside a menu about it.
let private toItemActions (messageId: string) : unit =
    focusOn (find (sprintf "[data-item-actions=\"%s\"]" messageId))

/// Onto the door the menu of new things hangs from: the strip's `+`, or the empty pane's
/// button when the pane is empty — the strip offers no `+` then (P1-4), and the empty pane's
/// press is the same act. At most one of the two is on the page, so each is found by its hook.
let private toPaneNew () : unit =
    find "[data-pane-new]" |> Option.orElseWith (fun () -> find "[data-terminal-new]") |> focusOn

/// Onto the message composer's field — this peer's own, the one that is not read-only — or,
/// where no composer is on offer, the session's title: the first control of the column a
/// notice in it sat over (`DomMove.FocusComposer`).
let private toComposer () : unit =
    find "[data-draft-input][data-rich-readonly=\"false\"] [contenteditable=\"true\"]"
    |> Option.orElseWith (fun () -> find "input[data-session-title]")
    |> focusOn

/// Carry out a move the model asked for (`Yession.App.DomMove`) — the one place a move is
/// turned into the document call that makes it, for the page and the harness alike.
///
/// A move that PLACES focus waits one frame, for the render that has to have happened, and
/// then is judged and made in that same frame (`place`). A guarded move used to be judged a
/// frame before it was made: the guard ran on the first frame and asked for the move, and the
/// move waited a frame of its own. A hand put down in the frame between — the × of the tab
/// whose shell had just died, focused in the frame after the swap dropped the keyboard on
/// `body` — was then taken by a verdict about a `body` it had already left, and its Enter
/// pressed the panel instead of the ×. A verdict about where the hand is is only true in the
/// frame it is read, so it is acted on there or not at all.
let rec move (asked: Yession.App.DomMove) : unit =
    match asked with
    | Yession.App.DomMove.RevealBlock (terminalId, blockId) ->
        revealBlock (Yession.Domain.TerminalId.value terminalId) (Yession.Domain.BlockId.value blockId)
    | Yession.App.DomMove.RevealMessage messageId -> revealMessage (Yession.Domain.MessageId.value messageId)
    | Yession.App.DomMove.ScrollToLatest surface -> Tail.follow surface |> ignore
    | Yession.App.DomMove.JumpToLatest surface -> jumpToLatest surface
    | placing -> nextFrame (fun () -> place placing)

/// A placing move, made NOW — `move` has already waited the frame. Every guard here is read in
/// the frame its move is made, by the call that makes it.
and private place (asked: Yession.App.DomMove) : unit =
    match asked with
    | Yession.App.DomMove.FocusPane -> toPane ()
    | Yession.App.DomMove.FocusCommandLine terminal -> toCommandLine terminal
    | Yession.App.DomMove.FocusPaneReopen -> toPaneReopen ()
    | Yession.App.DomMove.FocusPaneEmpty -> toPaneEmpty ()
    | Yession.App.DomMove.FocusSwitcherRow terminal -> toSwitcherRow terminal
    | Yession.App.DomMove.FocusSwitcher -> toSwitcher ()
    | Yession.App.DomMove.FocusPivot -> toPivot ()
    | Yession.App.DomMove.FocusTab terminal -> toTab terminal
    // A frame on, after the render the arrival caused — which is the render that took the
    // pressed control away, so a hand still on it reads as stranded by then.
    | Yession.App.DomMove.OnArrival inner -> if stranded [ "[data-content-panel]" ] then place inner
    // The same frame on, and only for a hand that the render dropped — or that the last drop
    // left on the panel, which is a catch rather than somewhere a reader chose to be.
    | Yession.App.DomMove.IfDropped inner ->
        let onPanel =
            match document.activeElement with
            | null -> false
            | active -> active.hasAttribute Yession.App.Dom.Hooks.panePanel
        if stranded [] || onPanel then place inner
    | Yession.App.DomMove.FocusChat subject -> toChatItem subject
    | Yession.App.DomMove.FocusItemActions messageId -> toItemActions (Yession.Domain.MessageId.value messageId)
    | Yession.App.DomMove.FocusPaneNew -> toPaneNew ()
    | Yession.App.DomMove.FocusComposer -> toComposer ()
    // Not placements, and nothing guards one: made as `move` makes them.
    | Yession.App.DomMove.RevealBlock _
    | Yession.App.DomMove.RevealMessage _
    | Yession.App.DomMove.ScrollToLatest _
    | Yession.App.DomMove.JumpToLatest _ -> move asked

/// The pane's open state, as a class on the shell root — the same mechanism the sidebar uses,
/// so a Lit re-render never fights the CSS transition. A `set` rather than a toggle, because
/// the model holds the bit and this only reflects it: the app opens this column itself
/// whenever a chip or a tab is chosen. The served shell (`Ssr.page`) writes the same class
/// from the same field before the first paint, so the first call here changes nothing —
/// which is the point: a column that painted open and was then shut is a jump.
let setOpen (isOpen: bool) : unit =
    if isOpen then document.documentElement.classList.remove [| Yession.App.Dom.termClosedClass |]
    else document.documentElement.classList.add [| Yession.App.Dom.termClosedClass |]

/// The pane's tab strip scrolls sideways, and a scroll box keeps nothing in view by itself: the
/// fourth terminal at the pane's default width opened past the right-hand edge, selected and
/// invisible, and the arrow walk moved focus onto tabs nobody could see. Where to scroll is
/// `TabStrip`'s arithmetic; measuring and scrolling is this.
module private Strip =

    let private selector = "[" + Yession.App.Dom.Hooks.paneStrip + "]"

    let private port (scroller: HTMLElement) : Yession.App.TabStrip.Scrollport =
        { Yession.App.TabStrip.Scrollport.Scrolled = scroller.scrollLeft
          Yession.App.TabStrip.Scrollport.Shown = scroller.clientWidth
          Yession.App.TabStrip.Scrollport.Holds = scroller.scrollWidth }

    /// The strip a node is in, if it is in one.
    let around (node: Element) : HTMLElement option =
        node.closest selector |> Option.map (fun strip -> strip :?> HTMLElement)

    /// Write which ends have tabs past them, for the fade to key on — and only on a change,
    /// because this runs on every scroll event and an attribute write is a style recalc.
    let mark (scroller: HTMLElement) : unit =
        let token = Yession.App.TabStrip.Hidden.token (Yession.App.TabStrip.hidden (port scroller))
        if scroller.getAttribute Yession.App.Dom.Hooks.paneStripHidden <> token then
            scroller.setAttribute (Yession.App.Dom.Hooks.paneStripHidden, token)

    /// Scroll the strip the least distance that shows this tab clear of both edges, and not at
    /// all when it already is. The tab's span is taken in the scroller's own content
    /// coordinates: where it is on screen, less where the scroll box is, plus how far it has
    /// scrolled.
    let reveal (scroller: HTMLElement) (tab: HTMLElement) : unit =
        let box = scroller.getBoundingClientRect ()
        let span = tab.getBoundingClientRect ()
        let start = span.left - box.left - scroller.clientLeft + scroller.scrollLeft
        Yession.App.TabStrip.reveal (port scroller) start (start + span.width)
        |> Option.iter (fun left -> scroller.scrollLeft <- left)
        mark scroller

    /// The least scroll that shows this tab's START clear of the edges — where its name
    /// begins, and where a press on it landed — and none when it already is.
    let revealStart (scroller: HTMLElement) (tab: HTMLElement) : unit =
        let box = scroller.getBoundingClientRect ()
        let span = tab.getBoundingClientRect ()
        let start = span.left - box.left - scroller.clientLeft + scroller.scrollLeft
        Yession.App.TabStrip.reveal (port scroller) start (start + min span.width 1.0)
        |> Option.iter (fun left -> scroller.scrollLeft <- left)
        mark scroller

    /// The selected tab's key (and width) at the last render that revealed it. A reveal on EVERY render
    /// would take the strip back from a reader scrolling it to look at the other tabs the
    /// moment anything at all arrived; on a CHANGE of selection it is the reader's own act (or
    /// a collaborator's `TabOpened`) being answered.
    let mutable private revealed = ""

    let sync () : unit =
        match find selector with
        | None -> revealed <- ""
        | Some scroller ->
            // A strip with no width is not laid out, and measures zero for everything — and
            // remembering its selection as revealed would skip the reveal once it is.
            if scroller.clientWidth > 0.0 then
                match scroller.querySelector "[role=\"tab\"][aria-selected=\"true\"]" with
                | null -> revealed <- ""
                | selected ->
                    let selected = selected :?> HTMLElement
                    // Its WIDTH too: arming its × widens the selected tab (P2-2). But a tab
                    // that only GREW is kept by its start, where it was: the arming is a
                    // press, and the confirm is only a confirm if the spot that was pressed
                    // is still the control — a strip that scrolled the grown tab whole into
                    // view slid the armed kill out from under the second press. So a new
                    // selection is revealed whole, and a wider one only if its start left.
                    let width = sprintf "%.0f" (selected.getBoundingClientRect ()).width
                    let key = selected.id + " " + width
                    if key <> revealed then
                        let sameTab = revealed.StartsWith (selected.id + " ")
                        revealed <- key
                        if sameTab then revealStart scroller selected else reveal scroller selected
                mark scroller

/// After every render: the selected tab in view if the selection changed, and the strip's
/// fade on whichever ends have tabs past them.
let syncStrip () : unit = Strip.sync ()

/// The listeners that keep the strip honest between renders — bound once per page, delegated
/// from the document so they survive Lit replacing the strip.
///
/// FOCUS is revealed as it lands, from whichever path moved it (the arrow walk, Home/End, the
/// neighbour a Delete hands focus to, Tab). Only KEYBOARD focus, which is what `:focus-visible`
/// says: a pointer pressing a half-hidden tab focuses it on the way down, and a strip that
/// scrolled under the pointer then would release it over a different tab — a click whose two
/// halves land on different elements goes to neither. A pointer's selection is revealed after
/// the render it causes, by `syncStrip`.
let installStrip () : unit =
    document.addEventListener (
        "focusin",
        fun event ->
            match EventTargets.asHTMLElement event.target with
            | Some focused when focused.matches ":focus-visible" ->
                match focused.closest "[role=\"tab\"]", Strip.around focused with
                | Some tab, Some strip -> Strip.reveal strip (tab :?> HTMLElement)
                | _ -> ()
            | _ -> ())
    // Scroll does not bubble, so captured; and the strip is Lit's to replace, so delegated.
    document.addEventListener (
        "scroll",
        (fun event ->
            match EventTargets.asHTMLElement event.target with
            | Some scrolled when scrolled.hasAttribute Yession.App.Dom.Hooks.paneStrip -> Strip.mark scrolled
            | _ -> ()),
        true)
    // A narrower window is a narrower strip, and what fits changes with it.
    window.addEventListener ("resize", fun _ -> Strip.sync ())

/// What this browser had open in one session's pane, kept across a reload (P0-4): the strip,
/// the pins, the tab on top and whether the column was open. Per session, because a strip is
/// a set of things in ONE session; per browser, like the split below, because it is one
/// person's view and never anybody else's.
///
/// The model decides what is remembered (`ClientModel.paneMemory`) and when it is put back
/// (`ClientModel.remembered`, then the fold's `recall`); this only reads and writes it.
module Memory =

    let private key (session: Yession.Domain.SessionId) : string =
        "yession:pane:" + Yession.Domain.SessionId.value session

    /// What storage is known to hold, so a render that changed nothing about the pane writes
    /// nothing. Seeded by `read` with what it found — or with an untouched pane when it found
    /// nothing — so a session merely visited leaves no key behind until something in its pane
    /// is actually moved.
    let mutable private stored : (Yession.Domain.SessionId * Yession.App.Codecs.PaneMemory) option = None

    /// The pane this browser remembered for this session.
    ///
    /// `None` for nothing kept, for storage that will not answer (a private window), and for
    /// a value that will not decode. The last is a forgetting rather than a refusal on
    /// purpose: what is lost is one person's tab arrangement, the page that cannot read it
    /// starts exactly as a session never seen, and the next change overwrites it.
    let read (session: Yession.Domain.SessionId) : Yession.App.Codecs.PaneMemory option =
        let found =
            try
                match localStorage.getItem (key session) with
                | null | "" -> None
                | text ->
                    Yession.Codecs.Codec.fromString Yession.App.Codecs.PaneMemory.codec text
                    |> Result.toOption
            with _ -> None
        stored <- Some (session, found |> Option.defaultValue Yession.App.Codecs.PaneMemory.untouched)
        found

    /// Write the pane down, if it changed. Not while a memory is still HELD by the model
    /// (`ClientModel.PaneMemory`): the strip on screen then holds only the terminals the log
    /// has named so far, and writing it would forget the rest of the strip waiting to be put
    /// back.
    let keep (model: Yession.App.ClientModel) : unit =
        match model.Session, model.PaneMemory with
        | Some session, None ->
            let memory = Yession.App.ClientModel.paneMemory model
            if stored <> Some (session, memory) then
                stored <- Some (session, memory)
                // Denied in a private window, and a pane that cannot be remembered is still a
                // pane that works.
                try
                    localStorage.setItem (key session, Yession.Codecs.Codec.toString Yession.App.Codecs.PaneMemory.codec memory)
                with _ ->
                    ()
        | _ -> ()

/// The pane's width on desktop, as a custom property on the shell root — the same mechanism
/// the open state uses, and for the same reasons: it is presentation, a Lit re-render must not
/// fight it, and the model has no business holding a number of pixels.
///
/// The column was a fixed 420px chosen as "the width the content actually has", and measured
/// against what a terminal actually prints it is 20 columns short of 80. Rather than guess a
/// better constant for every screen, the split moves and is remembered.
module private Split =

    /// Where a reader's chosen width survives a reload. Per browser profile, like the peer id:
    /// it is a preference about this screen, not a fact about the session.
    let key = "yession:term-width"

    /// Neither column can be dragged away to nothing. The pane's floor is its own; the chat's
    /// is what bounds the pane's ceiling.
    let minPane = 320.0
    let minChat = 420.0

    let private root = document.documentElement

    let private handles () : HTMLElement list =
        let found = document.querySelectorAll "[data-term-resize]"
        [ for i in 0 .. found.length - 1 -> found.[i] :?> HTMLElement ]

    /// The ceiling is what the CHAT can spare, not what the window is: the sidebar takes 280px
    /// of the window and can be collapsed, so a bound measured against `innerWidth` let the pane
    /// grow to 932px on a 1440 screen and left the conversation 228px — its title truncated to a
    /// single letter and its commands gone. Ask the two columns how wide they actually are.
    let widest () : float =
        match find "[data-content-panel]", find "[data-conversation]" with
        | Some pane, Some chat ->
            let spare =
                pane.getBoundingClientRect().width + chat.getBoundingClientRect().width - minChat
            max minPane spare
        | _ -> max minPane (window.innerWidth - minChat)

    /// Set the split, clamped, and tell everything that reports it. The separator's
    /// `aria-valuenow` is a value assistive technology reads out, so it is written here rather
    /// than left at whatever literal the template shipped.
    let apply (width: float) : unit =
        // Rounded before clamping, so a bound is a bound exactly: clamping a fraction first
        // and rounding after could land a pixel outside one.
        let next = width |> round |> min (widest ()) |> max minPane
        setStyleProperty root "--term-w" (sprintf "%dpx" (int next))
        for handle in handles () do
            handle.setAttribute ("aria-valuenow", string (int next))
            handle.setAttribute ("aria-valuemin", string (int minPane))
            handle.setAttribute ("aria-valuemax", string (int (widest ())))
        // A wider or narrower pane is a wider or narrower strip, and what fits changes with it.
        Strip.sync ()
        // Storage is denied in a private window, and a split that cannot be remembered is
        // still a split that works.
        try localStorage.setItem (key, string (int next)) with _ -> ()

    /// Where the split is now, asked of the property rather than of the column — because the
    /// column ANIMATES, and a measurement taken mid-transition is not a width anybody chose.
    let current () : float =
        match System.Double.TryParse (styleProperty root "--term-w" |> trimPx) with
        | true, said when said > 0.0 -> said
        | _ -> find "[data-content-panel]" |> Option.map (fun pane -> pane.getBoundingClientRect().width) |> Option.defaultValue minPane

    /// The width to start at: what was remembered, else the design token, else the floor.
    ///
    /// Seeded at install ALWAYS — not only when a width was remembered. Unseeded, `current`
    /// had to fall back to measuring the column, and asked while the column is opening it
    /// answers 1px (a shut pane is its own left border) or whatever the easing has reached, so
    /// the arrow keys then step from a number that was never the split.
    let seed () : float =
        let remembered =
            try
                match System.Double.TryParse (localStorage.getItem key) with
                | true, width when width > 0.0 -> Some width
                | _ -> None
            with _ -> None
        let token =
            match
                System.Double.TryParse
                    (computedProperty root "--spacing-term" |> trimPx)
                with
            | true, width when width > 0.0 -> Some width
            | _ -> None
        remembered |> Option.orElse token |> Option.defaultValue minPane

/// Install the splitter: the pane's width becomes something the reader sets, with a pointer or
/// with the keyboard, and keeps.
///
/// Installed once, delegated from the document so it survives every re-render of the handle.
/// The handle is a `separator` with a value, so the arrow keys have to move it — a splitter
/// that only answers a drag is a control a keyboard cannot reach at all.
let installPaneResize () : unit =
    // Which handle an event happened on, asked as "does a handle contain this": a press on
    // the grip's own children is a press on the handle. A target that is not a node at all is
    // under no handle.
    let handleUnder (target: EventTarget) : HTMLElement option =
        match EventTargets.asNode target with
        | None -> None
        | Some node ->
            let found = document.querySelectorAll "[data-term-resize]"
            [ for i in 0 .. found.length - 1 -> found.[i] :?> HTMLElement ]
            |> List.tryFind (fun handle -> handle.contains node)

    Split.apply (Split.seed ())
    window.addEventListener ("resize", fun _ -> Split.apply (Split.current ()))

    document.addEventListener (
        "pointerdown",
        fun event ->
            match handleUnder event.target with
            | None -> ()
            | Some handle ->
                let event = event :?> PointerEvent
                event.preventDefault ()
                handle.focus ()
                handle.setPointerCapture event.pointerId
                document.documentElement.classList.add "term-resizing"
                // The pane's edge is on the LEFT of a right-hand column, so its width is the
                // distance from the pointer to the right of the window.
                let move = fun (moving: Event) ->
                    Split.apply (window.innerWidth - (moving :?> PointerEvent).clientX)
                let rec finish =
                    fun (_: Event) ->
                        document.documentElement.classList.remove "term-resizing"
                        handle.removeEventListener ("pointermove", move)
                        handle.removeEventListener ("pointerup", finish)
                        handle.removeEventListener ("pointercancel", finish)
                handle.addEventListener ("pointermove", move)
                handle.addEventListener ("pointerup", finish)
                handle.addEventListener ("pointercancel", finish))

    document.addEventListener (
        "keydown",
        fun event ->
            match handleUnder event.target with
            | None -> ()
            | Some _ ->
                let event = event :?> KeyboardEvent
                // Left GROWS this column, because the column is on the right and its edge is
                // what moves.
                let step = if event.shiftKey then 64.0 else 16.0
                let moved =
                    match event.key with
                    | "ArrowLeft" -> Some (Split.current () + step)
                    | "ArrowRight" -> Some (Split.current () - step)
                    | "Home" -> Some (Split.widest ())
                    | "End" -> Some Split.minPane
                    | _ -> None
                match moved with
                | None -> ()
                | Some width ->
                    Split.apply width
                    event.preventDefault ())
