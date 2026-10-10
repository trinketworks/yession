namespace Yession.App

open Yession.Domain
open Yession.Domain.Terminals

/// The client's visual language, authored entirely in F# by composing Tailwind's own
/// utility classes into typed, named values. Tailwind supplies the utilities; F# supplies
/// the composition; the TOKENS — palette, type ramp, caps tracking, structural spacing,
/// fonts, motion — are `app/tokens.css`, and nothing here carries a raw hex or a structural
/// pixel count that has a token.
///
/// The DOCTRINE — what the design is, the rules, and the order they are settled in — is
/// `docs/visual-design.md`, and it is written once, there. This file composes it: each value
/// below says which rule it is spending and why, and a value this file wants that the
/// doctrine does not admit is a question for the doctrine, not a local exception.
///
/// The design is Metro / Zune (pre-Windows 8) worn by a Slack/Cursor workspace anatomy.
/// Zune's own panorama — horizontal surfaces you pan between — was considered for the whole
/// shell and rejected: it is a media-browsing metaphor, and the job here is watching one
/// conversation, editing a queue, and intervening fast. The session is one room, and
/// navigation through it is vertical time, not horizontal space. Zune survives in the type,
/// the colour and the motion, and the pivot idiom survives at two scales — the sidebar's two
/// destinations (`navPivot`), which are a place you go, not a surface you pan; and the content
/// pane's row of what it holds (`panePivotRow`), which IS a set of surfaces side by side, one
/// at a time, which is what a pivot was for.
///
/// What is this file's own, rather than the doctrine's:
///
///   Strokes — every border in the product is composed from the `Stroke` vocabulary
///   below (width, tone, and what interaction does to it) into a handful of phrases —
///   `field`/`fieldSelect`/`fieldBare`, `rowBase`/`rowLift`, `focusRing` — and those
///   phrases are what surfaces wear. No bare `border-*` utility is written outside
///   `Stroke`, and none at all in the views. The two remaining literals are variant-
///   PREFIXED (`md:[.nav-alt_&]:border-r-0`), undoing a column's divider while it is
///   shut: the variant is part of the class name, so there is no token to compose.
module Style =

    /// Join utility groups into a class attribute value.
    let cls (groups: string list) : string = String.concat " " groups

    // --- Strokes: the border vocabulary every bordered thing is built from ---------------
    //
    // A stroke is three independent choices — a WIDTH, a TONE at rest, and what happens
    // under interaction — and naming the three separately is what stops a settings field,
    // a queued row and a terminal composer from each inventing their own. Nothing below
    // this section writes a `border-*` utility directly; it composes these.
    //
    // Two widths, and only two:
    //   `ring` — 1px on all four sides. What you TYPE IN or PRESS (fields, buttons).
    //   `lead` — 2px on the leading edge. What is LISTED where two states share one surface
    //            (the terminal's queued commands, a collaborator's collapsed draft) and the
    //            edge says what the row IS. The message queue is NOT one of these any more:
    //            its rows are bands in the composer's dock (see `queueItem`).
    //   `underline` — the single exception, and it is a shape not a third width: a heading
    //            that edits in place (the session title), where a rectangle drawn round
    //            28px type reads as a box rather than a field.
    //
    // Two rules keep the states honest, and they are opposites on purpose:
    //   * A RING answers to INTERACTION. It brightens on hover and goes blue while
    //     focused — every field, every button, no exceptions. Blue is interactive, and
    //     focus is the most interactive a thing gets.
    //   * A LEAD answers to the MODEL and never to the pointer: green = live and still
    //     editable, blue = waiting on you, err = wrong, a peer's colour = whose it is.
    //     Interaction lifts the SURFACE instead (`rowLift`), so a row's tone can never be
    //     misread as "the mouse happens to be here".
    module Stroke =

        // Widths.
        let ring = "border"
        let lead = "border-l-2"
        let underline = "border-0 border-b"

        // Tones at rest. `hair` is the quiet separator, `rim` a step brighter for a
        // control at rest; the rest carry the meanings they carry everywhere else.
        let hair = "border-hair"
        let rim = "border-edge"
        let faint = "border-ink-faint"
        let blue = "border-blue"
        let green = "border-green"
        /// Present but unpainted — a tab that is not the selected one still occupies its
        /// border box, so selecting it moves no text.
        let clear = "border-transparent"

        // What a RING does under interaction. `focus:` fires on a control that takes focus
        // ITSELF — which is every ring in the product, because a host whose real input is a
        // descendant (a mounted ProseMirror) draws no ring at all: it wears `fieldBare` and
        // lets its container carry the signal.
        let hoverRim = "hover:border-edge"
        let hoverInk = "hover:border-ink"
        let hoverErr = "hover:border-err"
        let focus = "focus:border-blue"
        /// The title's affordance: dotted until you are in it, then solid blue.
        let dotted = "border-dotted focus:border-solid"

        /// The region divider: a hairline on ONE side, separating two surfaces rather than
        /// enclosing a control. Named per side because which side it is on is structural,
        /// and it answers to nothing — not hover, not focus, not the model.
        let dividerTop = "border-t border-hair"
        let dividerBottom = "border-b border-hair"
        let dividerLeft = "border-l border-hair"
        let dividerRight = "border-r border-hair"

    /// The keyboard focus ring, worn by every control that can take focus. One value, so a
    /// control cannot ship with half a ring (`outline-2` with no `outline`, which is how the
    /// terminal tabs used to draw nothing at all).
    ///
    /// NEVER compose it with `outline-none`, which is not the opposite of a ring but a
    /// SETTING: Tailwind v4 emits `.outline-none{--tw-outline-style:none}` and every outline
    /// utility resolves its style through that variable, so a control wearing both draws no
    /// ring at all — the class is present, the CSS is served, and the keyboard gets nothing
    /// (measured live: `outlineStyle: "none"` on a focused control carrying the full ring).
    /// A control that must suppress the UA's own box wears `fieldBare`, whose signal is its
    /// container's; a control that draws its own wears this and nothing else.
    let focusRing =
        "focus-visible:outline focus-visible:outline-2 focus-visible:outline-blue focus-visible:outline-offset-2"

    /// The same ring drawn just INSIDE the control's edge — for a control that lives in a box
    /// that clips, where a ring outside its edge is cut off wherever the box ends. A scroll
    /// container clips both axes whichever one it scrolls, so the pane's tabs, which sit in
    /// one flush to its top and bottom, showed a focus ring as its right edge alone: a
    /// vertical bar that read as a separator. Same width, same colour; only the side of the
    /// edge it is drawn on moves.
    let focusRingInset =
        "focus-visible:outline focus-visible:outline-2 focus-visible:outline-blue focus-visible:-outline-offset-2"

    /// The same ring held further off — for type with no box of its own, where a ring on the
    /// glyphs' own edge reads as an underline.
    let private focusRingFar =
        "focus-visible:outline focus-visible:outline-2 focus-visible:outline-blue focus-visible:outline-offset-4"

    // --- The acrylic: a surface that floats over another ----------------------------------
    // Worn only where there is something under it to blur — a band over a scrolling list, a
    // pane over the workspace, a card over the composer. On its own over the black ground it
    // is indistinguishable from the panel, which is the point: nothing depends on the effect,
    // and a reader who declines transparency gets the panel, opaque, and the same edge.
    let acrylic =
        "bg-acrylic backdrop-blur-acrylic backdrop-saturate-150 border-t border-acrylic-edge "
        + "reduced-transparency:bg-panel reduced-transparency:backdrop-blur-none reduced-transparency:backdrop-saturate-100"

    // --- Motion (one vocabulary, so every surface that moves moves the same way) ---------
    // Zune's signature: things arrive by sliding a little and fading in, fast, eased out,
    // and leave the way they came. The settings drawer's lanes, the ask card's panes and an
    // act's unfolding particulars all compose these rather than each spelling a duration.
    // `motion-reduce:transition-none` rides every one, so a reader who asked for no motion
    // gets the end state at once.
    module Motion =

        /// The pace: 200ms out-eased. What a lane, a fold and a turning arrow all take.
        let pace = "duration-200 ease-out motion-reduce:transition-none"
        /// A whole pane crossing the width of its track takes a beat longer.
        let paceLong = "duration-300 ease-out motion-reduce:transition-none"
        /// Slide-and-fade, for content that ARRIVES: it starts a little off and clear, and
        /// settles into place opaque. The opposite state is what it leaves through.
        let slideFade = cls [ "transition-[translate,opacity]"; pace ]
        /// Content UNFOLDING beneath a line: it grows from nothing (the grid trick — a row of
        /// `0fr` to `1fr` is the one height animation CSS will run without a measured pixel
        /// value), slides down a touch, and fades in; folding runs it back. `visibility` is
        /// in the list so the folded content leaves the tab order and the accessibility tree
        /// — and is transitioned, so it stays visible for the whole fold on the way out.
        let unfold = cls [ "grid transition-[grid-template-rows,translate,opacity,visibility]"; pace ]
        let folded = "grid-rows-[0fr] -translate-y-1 opacity-0 invisible"
        let unfolded = "grid-rows-[1fr] translate-y-0 opacity-100 visible"
        /// What sits inside an unfolding grid: the one row, clipped while it is short.
        let unfoldInner = "min-h-0 overflow-hidden"
        /// A mark that TURNS to say which way a fold is: a chevron pointing on at rest, down
        /// when what it fronts is unfolded.
        let turn = cls [ "transition-[rotate]"; pace ]
        let turned = "rotate-90"

    // --- Typography (the ramp lives as `--text-*` tokens in app/tailwind.css) -----------
    // Each `text-<step>` utility sets size AND line-height together, so a size can never
    // drift off its 4px line box; `leading-*` composes over a step where a context needs
    // a different box (roster rows and fields sit 13/20, a draft summary clamps 13/32).

    /// The wordmark: `yession`, at 400 rather than the 200 it was drawn at, because it no
    /// longer stands alone — beside the mark (`lockup`) the extralight read thin, and the
    /// full stop that was its one colour is the mark's job now (assets/logo/lockup.svg).
    let wordmark = "font-normal text-wordmark tracking-[-0.02em] text-ink"
    /// The mark and the wordmark on one line, the lockup's own proportions scaled to the
    /// band: the mark 56px to the word's 32 (the lockup file draws 64 to 32, which the 88px
    /// band has no room for), its foot 15px below the word's line box so the x-height band
    /// sits where the lockup puts it — on the mark's optical middle, a little above its own.
    let lockup = "flex items-end gap-2"
    let lockupMark = "block w-14 h-14 -mb-[15px] shrink-0 [&>svg]:block [&>svg]:w-full [&>svg]:h-full"
    let heading = "font-extralight text-heading tracking-[-0.01em] lowercase text-ink truncate"
    let body = "font-light text-body text-ink"
    /// The body voice one step back: worn by a record that is not live — a stopped, exited or
    /// archived session's name in the Manager's list — so a scan of the column finds what is
    /// running by weight of ink alone. A TOKEN rather than opacity: opacity would dim the
    /// mark and the focus ring riding the same element, and a token is what the contrast
    /// suite can pin.
    let bodyDim = "font-light text-body text-ink-dim"
    let small = "font-light text-small text-ink-faint"
    /// `small` in the err tone: a status line whose word is a fault. The one status on the
    /// Manager's list that keeps a colour — running and stopped are plain text, and what tells
    /// them apart is the name above them.
    let smallErr = "font-light text-small text-err"
    /// The caps voice — one size, one tracking, semibold — worn by every label, status,
    /// button, and author line. Colour composes at the use site.
    let private caps = "font-semibold text-label tracking-caps uppercase"

    /// The same voice one step up, for a verb that has ROOM: 13px rather than 11. Worn by
    /// the composer's Send and by the interrupt above it — the two that stand on a band of
    /// their own with nothing competing for the width. At the label size a word button there
    /// read as a caption of the glyph it replaced rather than as the thing you press.
    let private capsLg = "font-semibold text-small tracking-caps uppercase"

    /// The voice a body is written in: Source Serif 4 for a person, and the system's own Noto
    /// Sans for the agent, which is part of the system (`--font-human`/`--font-agent`). A serif
    /// on the sans's own skeleton, so a conversation reads as one voice in two registers rather
    /// than two typefaces arguing — the difference is one of CLASS rather than of letterform
    /// detail, which is what makes it survive 15px, and of class ONLY, which is what keeps the
    /// registers close (see the face story in `app/tailwind.css`). Attribution the eye reads before the words — the author line and the avatar say the same thing, but
    /// they say it once at the top, and a long turn scrolls past them.
    ///
    /// A face, not a colour: the caps author line already spends the palette on this
    /// distinction (`who` vs `whoAgent`), and message bodies are held at full-strength `ink`
    /// on purpose — what was said is the content, and dimming half of it to mark who said it
    /// would trade legibility for a fact the line above already carries.
    ///
    /// Worn by everything a person is CURRENTLY writing too — the open draft, a collapsed
    /// peer's draft, a queued message, every field they type into — so text does not change
    /// typeface at the moment it is sent. A command line is the exception and stays
    /// `font-terminal` (`fieldMono`): what is being written there is machine input.
    ///
    /// The BODY WEIGHT is part of the voice, because the two faces do not weigh the same at
    /// the same number: Source Serif's light is drawn thinner than Noto Sans's, so a shared
    /// `font-light` read the agent slightly heavier than the person. The human body face is
    /// a 350 instanced for exactly this (see `app/tailwind.css`), and the utility lives here
    /// so no use site can compose its own weight and reopen the gap.
    let messageVoice (isAgent: bool) =
        if isAgent then "font-agent font-light" else "font-human font-[350]"
    let label = caps + " text-ink-faint"
    let mono = "font-terminal text-code text-ink"
    let monoOut = "font-terminal text-code-sm text-ink-faint whitespace-pre-wrap"

    // --- Statuses: text only — never filled, never boxed --------------------------------

    let statusOk = caps + " text-green"
    /// A wait: reconnecting, starting, looking, busy. `ink-dim`, never blue — blue is never a
    /// status (docs/visual-design.md, Colour). A LIVE thing is `statusOk` with `statusDotLive`.
    let statusWait = caps + " text-ink-dim"
    let statusErr = caps + " text-err"
    let statusFaint = caps + " text-ink-faint"
    /// The small leading dot a live status may carry (`bg-current` follows the text colour).
    let statusDot = "inline-block w-1.5 h-1.5 rounded-full bg-current mr-1.5 align-[1px]"
    /// A wait that is still under way beats its dot (opacity); a LIVE thing wears `statusDotLive`.
    let statusDotPulse = statusDot + " animate-pulse2 motion-reduce:animate-none"
    /// The same dot standing still: a state that has SETTLED (a terminal's last command
    /// failed), where the pulse is one still under way.
    let statusDotSolid = "inline-block w-1.5 h-1.5 rounded-full bg-current"
    /// The dot with nothing in it: a state that has ENDED (a terminal that closed).
    let statusDotHollow = "inline-block w-1.5 h-1.5 rounded-full border border-current"
    /// A live dot: a command running. It beats in COLOUR, live's green to live's dim and back
    /// (`beat-live`), never in opacity, so it holds the 3:1 a mark owes its surface at every
    /// frame. Still and green under reduced motion.
    let statusDotLive = "inline-block w-1.5 h-1.5 rounded-full bg-green animate-beat-live motion-reduce:animate-none"
    /// The live dot leading a word or a count on its line, as `statusDot` leads a status.
    let statusDotLiveLead = statusDotLive + " mr-1.5 align-[1px]"
    /// The settled dot with a ring round it: something has happened that the person reading
    /// has not seen yet (`ClientModel.unseen`). An outline rather than a ring utility, so the
    /// gap between the two is the surface it sits on, whichever that is.
    let statusDotNews = statusDotSolid + " outline outline-1 outline-current outline-offset-[1.5px]"

    /// A standalone dot given its colour explicitly (`bg-green` etc. composed at the use
    /// site) for a row whose text is a DIFFERENT colour — `bg-current` would fight the
    /// composed colour utility, and which `bg-*` wins is stylesheet order, not authoring
    /// order.
    let syncDot = "inline-block w-1.5 h-1.5 rounded-full shrink-0"
    let syncDotPulse = syncDot + " animate-pulse2 motion-reduce:animate-none"
    /// The sidebar's one-line sync summary: dot and status words on one baseline.
    let syncRow = "flex items-center gap-2"

    // --- Buttons: bordered Metro rectangles — hover brightens, press fills --------------
    //
    // WHEN A BUTTON WEARS ITS BORDER, and when it wears none — the rule, because "some have
    // borders" is how two conventions breed a third:
    //
    //   * BORDERED — a STANDALONE act on the ground: it begins or ends something of its own
    //     (connect, interrupt, sign in, open the terminal list). A Metro button IS its
    //     rectangle; the rectangle is the claim to be a thing you press.
    //   * BORDERLESS — a verb RIDING the thing it acts on: send/discard at a field's trailing
    //     edge, reorder/delete on a listed row. The row or field already carries the
    //     structure, so the verb borrows it — quiet at rest, ink (or err) under the hand —
    //     and a second rectangle inside the first would be chrome describing chrome.

    /// Sized by construction, not padding arithmetic: the box is `h-control` with the line
    /// flex-centred in it — the old `py-[7px]` was that same height, hand-derived and
    /// easy to break. A FIELD is built the same way from the same token (`fieldFace`), which
    /// is what makes a button and an input in one row actually line up.
    ///
    /// Layout is split from the rest of the face (`btnFlex`/`btnGrid`) because one button
    /// — Create — needs a different one: its two words (`whenReady`/`whenBusy`)
    /// share a box whose size cannot depend on which of them is showing.
    let private btnFace =
        cls [ "group/btn bg-transparent cursor-pointer font-ui"; caps
              "h-control px-3.5 transition-colors"
              Stroke.ring; focusRing ]

    /// The ordinary layout: one line of content, centred. What every button used before a
    /// second layout existed to need distinguishing from.
    let private btnFlex = "inline-flex items-center justify-center"

    /// A single grid cell, for a button whose two words (`whenReady`/`whenBusy`) both sit in
    /// it at once — `col-start-1 row-start-1` on each keeps them stacked rather than
    /// auto-flowed into two rows. Both stay laid out (`invisible`, never `display:none`), so
    /// the cell — and the button around it — sizes to the WIDER of the two and
    /// never changes size when the one showing changes. `hidden`/`inline` (display) would
    /// drop the other out of the box being measured, which is exactly the resize this exists
    /// to prevent.
    let private btnGrid = "inline-grid grid-cols-1 place-items-center"

    /// The three faces, as (rest tone, hover, press) — the only thing that varies
    /// between them, so a fourth would be three tokens rather than another hand-written
    /// string.
    ///
    /// The press face is `pressed:` (app/tailwind.css) rather than `active:` — the
    /// finger's press AND the hold after it (`aria-busy`), for a button whose act takes the
    /// browser somewhere else and has nothing to show on this page until it arrives. Filled
    /// while down, and down until it lands.
    let private btnPrimaryFace = cls [ Stroke.blue; "text-blue hover:text-blue-up-1 pressed:bg-blue pressed:text-bg" ]

    let btn =
        cls [ btnFace; btnFlex; Stroke.rim; "text-ink-dim"; Stroke.hoverInk; "hover:text-ink pressed:bg-ink pressed:text-bg" ]

    let btnPrimary =
        cls [ btnFace; btnFlex; btnPrimaryFace ]

    /// `btnPrimary`, laid out on `btnGrid` — the one a held word
    /// (`whenReady`/`whenBusy`) sits on top of, so pressing it never changes its own size.
    /// Not Create's alone: `askStart` (the repo picker's commit button, below) builds on it
    /// too, which is the point — the box-stable hold is a property of the LAYOUT, not of
    /// which button first needed it.
    let btnPrimarySwap =
        cls [ btnFace; btnGrid; btnPrimaryFace ]

    let btnDanger =
        cls [ btnFace; btnFlex; Stroke.rim; "text-ink-dim"; Stroke.hoverErr; "hover:text-err pressed:bg-err pressed:text-bg" ]

    /// The two words a held button can be saying — the verb, and the verb under way
    /// — as siblings inside it, both always laid out (`btnGrid`, on `btnPrimarySwap`)
    /// so the box they share never resizes between them; `invisible` (not `hidden`) is what
    /// keeps the one not showing in that box rather than out of it. Copy stays in the markup;
    /// only `aria-busy` on the button flips which is visible — set by a script for a plain
    /// `<form>` post (Create, `app/ManagerUi.fs`), or straight from view state for a button
    /// whose stage IS the model (Start, `View.fs`'s repo picker). The button wears the
    /// named group for it (`btnFace`) — named, so a button sitting inside some other
    /// group answers to its own state and never to that one's.
    let whenReady = "col-start-1 row-start-1 group-aria-busy/btn:invisible"
    let whenBusy = "col-start-1 row-start-1 invisible group-aria-busy/btn:visible"

    /// The name of a LISTED record, when the name itself opens it. The row's primary act
    /// is carried by its content rather than by another rectangle in the right rail —
    /// which is how a table of five sessions ends up wearing ten bordered buttons, all
    /// shouting the same loudness as the page's one real CTA. Ink at rest, so a list of
    /// names reads as a list of names; blue and underlined under the pointer, because
    /// blue is what interactive means here. Underline (not colour) at rest would say the
    /// same thing, but on every row at once — the same noise in a quieter register.
    let recordLink =
        cls [ body; "no-underline hover:text-blue hover:underline decoration-1 underline-offset-4"; focusRing ]

    /// The same link on a record that is not live (`bodyDim`): still the way in — opening a
    /// stopped session is what starts it — but a step back from the ones already running.
    let recordLinkQuiet =
        cls [ bodyDim; "no-underline hover:text-blue hover:underline decoration-1 underline-offset-4"; focusRing ]

    /// The mark that says a record link LEAVES (the session serves its own origin). Faint
    /// and small: it is a signpost, not a second status.
    let recordLinkMark = "text-code text-ink-faint ml-1 align-[1px]"

    /// The hit area for a listed record's whole cell — name line and state line both, not
    /// just the name's own glyphs. `block` so the link's box is the cell's full width rather
    /// than the text it wraps; colour and hover stay on the name text itself (`recordLink` /
    /// `recordLinkQuiet`, worn by a span inside), so hovering the state line below does not
    /// blue it — only the name was ever styled to look like a link, this just widens what you
    /// can press to get it.
    let recordRowLink = cls [ "block no-underline"; focusRing ]

    /// A filter over a listed registry — which archive states the management page is showing.
    /// Real links, because the filter IS the page's location, so these are chips only in how
    /// they look; everything about how they behave comes free from `<a>`.
    ///
    /// The two faces differ by TONE and border, in the same vocabulary the buttons above use:
    /// a selected chip sits on the working rim in full ink, an unselected one recedes to the
    /// hairline in the faint step and comes up to ink under the pointer. No fill on either —
    /// a filled chip would read as the page's CTA, and the CTA here is Create.
    let private filterChipBase =
        cls [ caps; "h-6 px-2 inline-flex items-center no-underline cursor-pointer transition-colors"
              Stroke.ring; focusRing ]

    let filterChipOn = cls [ filterChipBase; Stroke.rim; "text-ink" ]
    /// The count a chip wears, in the chip's own voice and colour — the word is the filter,
    /// the number is what it holds, and they light and dim together.
    let filterChipCount = "ml-1.5 tabular-nums"
    let filterChipOff = cls [ filterChipBase; Stroke.hair; "text-ink-faint"; Stroke.hoverInk; "hover:text-ink" ]

    /// A sortable column header. Wears the header's own caps-faint voice so a sortable column
    /// does not shout over the ones that are not, and comes up to ink under the pointer — the
    /// only thing that says it is a control, besides its focus ring and the direction mark it
    /// carries. The `<th>` around it holds `aria-sort`, which is what actually states the sort.
    let sortHeader =
        cls [ label; "no-underline cursor-pointer hover:text-ink transition-colors"; focusRing ]

    /// A button with nothing to do YET — composed over one of the faces above, never used
    /// alone. It stays in the layout and in focus order (its work is coming, and a control
    /// that appears mid-sentence moves everything under the reader's hand); the BORDER carries
    /// the waiting, dropping to the quiet rim so the rectangle reads as an outline not yet
    /// filled. Which is this design's own vocabulary: a Metro button IS its border, hover
    /// brightens it, press fills it.
    ///
    /// Deliberately NOT an opacity dim: fading `text-blue` on the composer's surface takes it
    /// from 6.5:1 to about 2.5:1, and an 11px caps label at 2.5:1 is below the AA floor this
    /// product holds. The word stays exactly as legible as it was; only the frame changes.
    let btnWaiting = "!border-edge"

    /// Square icon buttons — self-contained, NOT composed over `btn`: Tailwind emits `p-0`
    /// BEFORE `px-*`/`py-*` in the stylesheet, so "btn + p-0" kept the text button's padding
    /// and crushed the glyph into a corner of a lopsided box (measured live: 30×24, ×
    /// touching the bottom-right edge).
    let private btnIconBase =
        cls [ "bg-transparent cursor-pointer p-0 grid place-items-center transition-colors"; Stroke.ring; focusRing ]

    let private btnIconNeutralFace =
        " " + cls [ Stroke.rim; "text-ink-dim"; Stroke.hoverInk; "hover:text-ink active:bg-ink active:text-bg" ]

    let private btnIconDangerFace =
        " " + cls [ Stroke.rim; "text-ink-dim"; Stroke.hoverErr; "hover:text-err active:bg-err active:text-bg" ]

    /// 24px square (composed up to 32 where it stands in a bar): the STANDALONE icon acts —
    /// currently the terminal pane's list toggle. Row-riding verbs are `btnIconBare` below.
    let btnIcon = btnIconBase + " w-6 h-6" + btnIconNeutralFace
    /// 32px square, destructive: the composer's discard — the same height as the Send
    /// button it sits beside, so the pair shares top and bottom edges.
    let btnIconDangerLg = btnIconBase + " w-8 h-8" + btnIconDangerFace

    /// The borderless icon verbs (see the rule at the head of this section): 24px square,
    /// grid-centred, riding a listed row they act on — the queue's reorder and delete, a
    /// terminal row's verbs, a roster row's disconnect. Faint at rest because the ROW is the
    /// subject; ink (or err, when what they do destroys) under the hand; the focus ring is
    /// the one piece of chrome they keep, because the keyboard has no hover.
    let private btnIconBareBase =
        cls [ "w-6 h-6 shrink-0 bg-transparent border-0 cursor-pointer p-0 grid place-items-center"
              "transition-colors"; focusRing ]
    let btnIconBare = cls [ btnIconBareBase; "text-ink-faint hover:text-ink" ]
    let btnIconBareDanger = cls [ btnIconBareBase; "text-ink-faint hover:text-err" ]
    /// `btnIconBareDanger`, ARMED: a second press away from actually deleting (the queue's
    /// delete — see `View.queue`), which this says by wearing the err fill AT REST rather
    /// than only under the hand. A touch has no hover to hold the hint up, so the state a
    /// phone needs to see has to be worn rather than revealed — the same reasoning
    /// `btnInterrupt` uses to stay unworn, read the other way: that button is never armed
    /// twice, this one specifically IS, between the two presses the confirm needs.
    /// Un-arms itself back to `btnIconBareDanger` on its own (`armedMs`), so a
    /// press nobody confirms cannot leave a row looking primed for ever.
    let btnIconBareDangerArmed = cls [ btnIconBareBase; "bg-err text-bg" ]
    /// A terminal's kill, ARMED (`View.killControl`): a second press away from ending it, which
    /// it says by being in the error red AT REST — a touch has no hover to hold the warning up —
    /// and by saying what it will end: `kill`, then what is running there. A sentence in the
    /// pane's light lowercase rather than a red slab of capitals: the red alone is the alarm,
    /// and it is the one red word on screen because it is the one press nobody can take back.
    /// The command after it is what tells it from the unarmed word reddening under the
    /// pointer, in the weight a step up from it. The row verb's height, so the row does not move;
    /// wider, growing leftwards from its right edge, so the second press lands where the
    /// first did.
    let btnKillArmed =
        cls [ "h-6 shrink-0 inline-flex items-center gap-1.5 px-1 bg-transparent border-0 cursor-pointer"
              "font-ui font-normal text-body lowercase whitespace-nowrap text-err transition-colors"; focusRing ]
    /// What the armed kill says is running, inside it: the command as typed — case kept — cut
    /// short rather than allowed to push the row's name out of its column.
    let killArmedRunning =
        "font-terminal text-code normal-case tracking-normal truncate max-w-[7rem]"
    /// The same verb where it is the only control a row offers a thumb — the Manager's
    /// archive, which on a phone is the one per-row target there is. 24px is WCAG 2.5.8's
    /// minimum exactly and far under a comfortable touch target, so the HIT area is 44px
    /// while the glyph, the box and the row stay as they were: an empty `::before` reaches
    /// 10px past the box on every side, and a press on a pseudo-element is a press on its
    /// element. The box itself stays 24px on purpose — it is what the focus ring is drawn
    /// round, and a ring round the 44px area crowded the verb beside it and the screen's
    /// edge. Not the default, because the other bare icons ride in pairs (the queue's up
    /// and down) whose areas would overlap; a site wearing this keeps 10px clear of its
    /// neighbour so the area it grows never lands on one.
    let btnIconBareTouch =
        cls [ btnIconBareBase; "relative before:absolute before:-inset-2.5 before:content-['']"
              "text-ink-faint hover:text-ink" ]

    /// The borderless verb as a WORD rather than an icon, for a row whose verb has no glyph
    /// that says it (stop, unarchive). Same rule, same box height, same rest and hover tones
    /// as the icon form; the caps voice because that is what every button here speaks.
    let private btnBareBase =
        cls [ "h-6 px-1 shrink-0 inline-flex items-center bg-transparent border-0 cursor-pointer font-ui"; caps
              "transition-colors"; focusRing ]
    let btnBare = cls [ btnBareBase; "text-ink-faint hover:text-ink" ]
    let btnBareDanger = cls [ btnBareBase; "text-ink-faint hover:text-err" ]
    /// A bare verb at the right of a roster row (`person`): pushed to the row's trailing edge,
    /// `-mr-1` so its WORD, not its padding, lines up with the status words other rows end in,
    /// and a thumb's height on a phone, where a 24px word is not a target. It keeps the row's
    /// baseline, so its word stands on the same line as the name beside it however tall its
    /// hit area grows.
    let rosterVerb = "ml-auto -mr-1 phone:min-h-11"

    /// 32px square and BORDERLESS: the verb parked INSIDE a field, over the text.
    ///
    /// A Metro button IS its rectangle, so dropping the rectangle is not a quieter button, it
    /// is a different kind of control — and it earns that by living inside the field it acts
    /// on rather than beside it. The terminal's command line is what still needs one: its Run
    /// is absolutely placed at the line's trailing edge (`terminalCommandTrail`), where a word
    /// would sit on top of the command being typed. The name goes on `aria-label`, which is
    /// where an icon-only control keeps it.
    ///
    /// The message composer's Send used to be this control too, and is not any more
    /// (`btnComposerSend`, below). They parted over a fact about the two surfaces rather than
    /// a preference: the composer's verbs have a row to themselves, and a row with room in it
    /// should say the word.
    ///
    /// 44px square on a phone, where the field it rides is a thumb's height too.
    let private btnInField =
        cls [ "w-8 h-8 phone:w-11 phone:h-11 shrink-0 grid place-items-center bg-transparent border-0 cursor-pointer p-0"
              "transition-colors"; focusRing ]
    let btnSendInField = cls [ btnInField; "text-blue hover:text-blue-up-1" ]
    /// Waiting for something to run. The same control in the same place, at the weight of a
    /// thing with nothing to do — never `disabled`, in either spelling: an empty command line
    /// is not a blocked one.
    let btnSendInFieldWaiting = cls [ btnInField; "text-ink-faint hover:text-ink" ]

    /// Send's own box — SOLID, not the word every other composer verb wears. The product's
    /// own rule (above) is bordered for a standalone act, borderless for a verb riding the
    /// thing it acts on, and Send is the second kind by that rule — it wore the borderless
    /// word for three revisions on exactly that reasoning (`SEND →` once, then the word
    /// alone). Direct design direction overrode it: a thumb on a phone finds a filled
    /// rectangle before it finds a word's own baseline, and now that the model picker rides
    /// this same row (`Style.modelSelect`) Send is the one control in it worth standing apart
    /// from, not blending into. No `rounded-*` — every rectangle this product draws is
    /// sharp-cornered (`btn`/`btnPrimary`/every bordered verb above), and a lone rounded
    /// button would read as a different product's chrome leaking in, not as "more primary."
    ///
    /// 40px tall, which is the composer's resting line exactly (`draftInput`: a 24px line in
    /// `py-2`) — so it bottom-aligns onto that line with no correction, same as the word it
    /// replaces, and `draftCommit` spends no `pb` to centre it.
    let private btnComposerBox =
        cls [ "h-10 px-3 shrink-0 inline-flex items-center justify-center cursor-pointer font-ui"
              capsLg; "transition-colors"; Stroke.ring; focusRing ]
    /// Send's own hit area, grown past its 40px box the way `btnIconBareTouch` grows a bare
    /// icon's: an empty `::before` reaching past the glyph on three sides, a press on the
    /// pseudo-element landing on the element. Only three sides, not four — Send is the
    /// TRAILING control in this row, and `draftEditors` sits to its left across one `gap-1`;
    /// growing left would widen onto that presence display rather than onto nothing.
    let private btnComposerSendTouch =
        "relative before:absolute before:content-[''] before:-top-1.5 before:-bottom-1.5 before:-right-1.5"
    let btnComposerSend =
        cls [ btnComposerBox; btnComposerSendTouch; Stroke.blue; "bg-blue text-bg hover:bg-blue-up-1 hover:border-blue-up-1" ]
    /// Waiting for something to send. The same box in the same place, unfilled — a hairline
    /// rectangle at the weight of a thing with nothing to do, never `disabled` in either
    /// spelling: an empty composer is not a blocked one.
    let btnComposerSendWaiting =
        cls [ btnComposerBox; btnComposerSendTouch; Stroke.hair; "text-ink-faint hover:text-ink hover:border-edge" ]
    /// Chrome, not an action: the small sidebar collapse/reveal chevrons. They lean the way
    /// they travel on hover and lead further on press — the only motion chrome earns, and the
    /// reason the two directions are separate values rather than one class plus a guess.
    ///
    /// On a phone the glyph stays small and the BOX does not: 44px each way, the target this
    /// product holds a thumb to (UI baseline), with the glyph centred in it. The `-m-1.5`
    /// still pays the padding back to the flow, so a site that needs the rest paid back too
    /// says so itself (`navReopen`).
    let private navChevronBase =
        "bg-transparent border-0 cursor-pointer text-ink-faint hover:text-ink text-small p-1.5 -m-1.5 "
        + "flex items-center gap-1 transition-[translate,color] duration-150 ease-out "
        + "phone:min-w-11 phone:min-h-11 phone:justify-center "
        + "motion-reduce:transition-none " + focusRing

    let navChevronBack = navChevronBase + " hover:-translate-x-0.5 active:-translate-x-1"
    let navChevronForward = navChevronBase + " hover:translate-x-0.5 active:translate-x-1"

    /// Worn by every control you TYPE INTO, and nowhere else. iOS zooms the page in when it
    /// focuses a text control whose type is under 16px and never zooms back out — tapping the
    /// terminal's 12px command line left the whole column magnified and shifted, which is not
    /// something a person can undo without pinching the page back themselves.
    ///
    /// The size is the fix. `maximum-scale=1` in the viewport tag stops the same zoom, and
    /// stops a person zooming the page AT ALL — a WCAG 1.4.4 failure, and the UI baseline
    /// (AGENTS.md) is a floor rather than a preference. So the phone gets 16px in the places
    /// where a keyboard is coming, and the ramp is the ramp everywhere else.
    ///
    /// `--text-touch` carries no line-height pair, so this sets the SIZE alone and each field
    /// keeps the line box its own step gave it.
    ///
    /// Not private any more: `fieldType` folds it in below, and the mono/message fields that
    /// still hand-spell their own font class (no shared size function to fold it into) keep
    /// composing it directly, the way they always did.
    let touchType = "phone:text-touch"

    // --- Fields: ONE face, worn by every input in the product ----------------------------
    // A field is the surface tone inside a hairline ring that brightens on hover and goes
    // blue while focused. The settings inputs, the terminal command line and the queued
    // command all wear it; what varies is the TYPE it holds and the WIDTH its row gives it,
    // never the chrome. `fieldFace` therefore sets no width and no font — those belong to
    // the caller, and baking them in is how the three drifted apart in the first place.

    /// Built exactly as `btnBase` is, and from the same `h-control`: the box sets the height
    /// and the content is centred in it. It used to set `py-2` and let the line box decide,
    /// which made an input 42px, a select 40px and a button 32px — three heights in one row.
    /// `inline-flex` is what centres the content of the one field that is not an input: the
    /// device code is a `span`, and a span does not centre itself in a fixed height.
    let fieldFace =
        cls [ "bg-surface outline-none appearance-none h-control px-3 transition-colors"
              "inline-flex items-center"
              Stroke.ring; Stroke.hair; Stroke.hoverRim; Stroke.focus ]

    /// What a field holds, as opposed to what it looks like: the body scale, never the title's.
    ///
    /// The UI voice, not the human one — the distinction is SPEECH, not keystrokes. A composer,
    /// a queued message and a peer's draft wear `messageVoice` because what is typed there
    /// becomes a message and keeps the face it was written in. A session's display name, an API
    /// token, a mode select are chrome a person operates: nothing is being said, so there is no
    /// attribution to make, and a serif form control on a sans page reads as a mistake rather
    /// than a signal.
    /// `touchType` is IN here rather than beside it at every call site: nine of these used to
    /// each hand-append `touchType` themselves, and a tenth (`fieldSelect`) forgot to — the
    /// field rendered fine everywhere except a thumb on iOS, and nothing failed loudly enough
    /// to notice. Folding it into the one function every settings-style field already calls
    /// for its size means there is no longer a second ingredient to remember: any field built
    /// on `fieldType` gets the phone-safe size for free, forgetting is no longer a way to lose it.
    let private fieldType =
        cls [ "font-ui font-light text-small leading-5 text-ink placeholder:text-ink-faint"; touchType ]

    /// A settings field (input/select), filling the column it sits in.
    let field = cls [ fieldFace; fieldType; "w-full" ]

    /// The same field where the ROW gives it a width rather than the column — the Manager's
    /// forms lay three of them out side by side. Public because the alternative is what was
    /// here before: the Manager spelling the whole face inline, which drifted to a 42px input
    /// beside a 32px button.
    let fieldOf (width: string) = cls [ fieldFace; fieldType; width ]

    /// A select. Everything a field is, plus room for the mark below it — `appearance-none`
    /// (see `fieldFace`) takes the platform's caret away, and a menu with no caret is a text
    /// box that will not take text.
    let fieldSelect = cls [ fieldFace; fieldType; "w-full pr-9" ]

    /// Its mark, drawn beside it. Absolutely positioned in the wrapper the select sits in and
    /// deaf to the pointer, so the whole rectangle still opens the menu.
    ///
    /// The WRAPPER carries the width — `fieldSelect` always fills it — so a row that sizes its
    /// own controls sizes the wrapper (`fieldSelectWrapOf`) and the mark still lands on the
    /// box's right edge.
    let fieldSelectWrap = "relative w-full"
    let fieldSelectWrapOf (width: string) = cls [ "relative"; width ]
    let fieldSelectMark = "pointer-events-none absolute right-3 top-1/2 -translate-y-1/2 text-ink-faint"

    /// The model picker's own width, now that it rides the composer's row (`View.modelControl`)
    /// instead of a settings column that gave it the row's full width. `shrink-0` so Send and
    /// Interrupt, packed in beside it, never squeeze it below the model names it has to show.
    ///
    /// `w-28` (112px) was too narrow for the control's OWN default label, "Provider's
    /// default" — a native `<select>` does not ellipsize an overflowing option, it hard-clips
    /// the text at the box edge, so the row read "Provider's de" with the caret crammed
    /// against it. `w-40` (160px) is sized to that longest label, which is also longer than
    /// any catalogue model name (`ModelCatalogue`) seen in testing — fixing the label fixes
    /// every shorter option too.
    let modelControlWidth = "w-40 shrink-0"

    /// The model picker's own FACE, now that it stands on the composer's row rather than in a
    /// settings column: bare — deliberately NOT the same register as Send beside it any more.
    /// Send wears a solid box now (`btnComposerSend`), the one filled rectangle this row
    /// draws, precisely so the picker does not have to: a bare control next to a filled one
    /// reads, correctly, as "this is the one that matters." Riding `fieldSelect` here made
    /// the picker a filled rectangle fighting Send for that same attention — the bordered/
    /// filled/ringed box every settings `<select>` wears (`fieldFace`) — which is the mistake
    /// this face still avoids, just against a different trailing neighbour now.
    /// `appearance-none` still drops the platform caret (the mark beside it draws the one
    /// that's left); nothing else of the field face survives.
    let modelSelect =
        cls [ "h-10 pl-0 pr-5 bg-transparent border-0 appearance-none cursor-pointer w-full"
              "font-ui font-light text-ink-dim hover:text-ink transition-colors"; touchType; focusRing ]

    /// Its wrapper: `modelControlWidth` for the width Send and Interrupt need it to hold to,
    /// `relative` for the mark `modelSelectMark` sits against — the same split `fieldSelectWrapOf`
    /// makes, kept separate because this wrapper carries none of that function's field chrome.
    let modelSelectWrap = cls [ "relative"; modelControlWidth ]

    /// The caret `appearance-none` took away, drawn at the picker's own faint weight —
    /// `fieldSelectMark` is `text-ink-faint` too, so the two marks read as one family even
    /// though the box behind them no longer matches.
    let modelSelectMark = "pointer-events-none absolute right-0 top-1/2 -translate-y-1/2 text-ink-faint"

    /// A field with a VERB at its trailing edge, built exactly as the terminal composer's is
    /// (`terminalCommandWrap` / `terminalCommandTrail`): the wrapper carries the width, the
    /// field reserves the room, and the control sits INSIDE the box it acts on rather than
    /// beside it. The select's caret above is the other half of the pattern and deliberately
    /// not this — a mark takes no pointer events, a button is the thing you press.
    let fieldActionWrap = "relative w-full"
    let fieldWithAction = cls [ fieldFace; fieldType; "w-full pr-10" ]
    let fieldAction = cls [ btnInField; "absolute right-1 top-1/2 -translate-y-1/2 text-ink-faint hover:text-ink" ]

    /// The chrome-LESS field: an input whose CONTAINER already carries the stroke (the
    /// composer's box, a listed row's leading edge), so the control itself must draw
    /// nothing. `[&_.ProseMirror]:outline-none` is not tidying: a mounted editor puts its
    /// `contenteditable` INSIDE this host, so an `outline-none` on the host never reaches
    /// it and the browser draws its default focus box — a white rectangle, in a design
    /// with no rectangles. The focus signal is the container's (a gradient edge, a lift).
    let private fieldBare =
        "bg-transparent border-0 outline-none resize-none [&_.ProseMirror]:outline-none"

    /// The mono field, chrome-less — a command line inside a row that already has a leading
    /// edge (a queued command, a collaborator's slot). Double chrome, a bordered field inside
    /// an edged card, is what made a queued COMMAND look like a different kind of thing from
    /// a queued MESSAGE, which is the one thing they are not.
    let fieldMonoBare =
        cls [ "flex-1 min-w-0 phone:min-h-11"; fieldBare; "font-terminal text-code text-ink placeholder:text-ink-faint"; touchType ]

    // --- Listed rows: the leading edge says what the row IS ------------------------------
    // Every row in a list — a queued message, a queued command, a peer's collapsed draft —
    // is this: a surface with a 2px leading edge whose TONE is the row's state, lifting a
    // tone while the pointer or the caret is in it. The tone is composed at the use site
    // (`Stroke.green`, `Stroke.blue`) or set inline when it is a peer's own colour.

    let private rowBase = cls [ "flex bg-surface transition-colors"; Stroke.lead ]
    /// A row you can act on: the surface lifts, the edge does not move.
    let private rowLift = "hover:bg-surface-2 focus-within:bg-surface-2"

    // --- Pivots: the sidebar's two destinations, set as type ----------------------------
    // Zune navigated by WORDS — big, quiet, lowercase, with a thin chevron pointing the way the
    // surface was about to move — and Courier's chrome earned its place by being set rather than
    // drawn. `settings ›` and `‹ back` are ONE control, mirrored: same size, same foot of the
    // same column, so pressing it leaves the word replaced and the mark flipped, in place. (The
    // head is identity — the wordmark, then the settings title — and never navigation; the two
    // fought for the 280px band when they shared it, and two chevrons a thumb apart read as a
    // pair of arrows rather than a way in and a way out.)
    //
    // Everything that marks them as interactive is a RESPONSE: the word brightens to ink, the
    // mark turns blue and steps the way it points, and a press sends it further. Nothing at rest
    // but type.

    /// One step below the settings title (28/32) and two below the wordmark (32/36), on the
    /// same 4px rhythm: a destination, never a heading.
    let private pivotBase =
        "group bg-transparent border-0 cursor-pointer flex items-center gap-2 "
        + "font-extralight text-pivot tracking-[-0.01em] lowercase "
        + "text-ink-faint hover:text-ink focus-visible:text-ink transition-colors duration-150 ease-out "
        + "phone:min-h-11 motion-reduce:transition-none " + focusRingFar

    let navPivot = pivotBase

    /// A column's foot: the pivot, and opposite it on the same baseline, the build. One row
    /// so the build costs the column no height — it lives in the slack the pivot already
    /// leaves to its right.
    let sideFoot = "flex items-baseline justify-between gap-3 min-w-0"

    /// Which build this is, as a footer says it: the faintest step of the mono face, set
    /// rather than drawn, and never a control. Shared by the session's sidebar foot and the
    /// Manager's page footer, so "which Yession is this" reads the same on both surfaces.
    /// `select-all` because the one thing anybody does with it is paste it into a report.
    let buildMark = "font-terminal text-code-sm text-ink-faint tabular-nums truncate select-all"

    let private pivotMarkBase =
        "block transition-[translate,color] duration-150 ease-out motion-reduce:transition-none "
        + "group-hover:text-blue group-focus-visible:text-blue"

    /// Into settings — the column turns and the mark leads right.
    let pivotMarkForward =
        pivotMarkBase + " group-hover:translate-x-1 group-focus-visible:translate-x-1 group-active:translate-x-2"

    /// Back to the session — the same step, mirrored.
    let pivotMarkBack =
        pivotMarkBase + " group-hover:-translate-x-1 group-focus-visible:-translate-x-1 group-active:-translate-x-2"

    // --- Tiny square display pics (never round) -----------------------------------------
    // Two-tone checkers stand in for people until real avatars exist; the variant is the
    // person's seat, the order they joined the session in, so identity is stable and two
    // people share one only when there are more people than checkers. The agent is not a
    // checker: it is a blue diamond (`agentAvatar`), and the palette keeps it that way.

    let avatar = "w-5 h-5 shrink-0"
    let avatarSm = "w-3.5 h-3.5 shrink-0"

    // --- A thing a sentence points at (`Entity.render`) ---------------------------------
    // Mark and name, inline in the sentence's own line: an entity is part of what is being
    // said, not a chip beside it. The name one step brighter than the words around it, so
    // WHO and WHAT is where the eye lands first — the step `actNoteWho` took for the
    // author before acts folded under a shared author line.
    //
    // INLINE, not inline-flex. A flex box lends the line its first item's baseline, and the
    // mark has none — an empty square, an svg — so the box's bottom edge stood in for it and
    // the name rode a descender above the words either side (seen on a phone: every `dev`
    // floating over its `started sandbox`). As plain inline content the name IS text on the
    // line's baseline, and the mark is nudged down onto it the way `Icon.checkSm` and its
    // kin sit beside a status word. `whitespace-nowrap` keeps mark and name on one line.
    let entity = "inline whitespace-nowrap"
    let entityName = "text-ink"
    /// A reference that is somewhere to go — a repository, a pull request, on their host — is
    /// a real link and LOOKS like the links in the transcript's prose: blue, underlined, a
    /// step brighter under the pointer (`proseLink`, the one hyperlink face on this page).
    /// The mark inherits the blue, since it is part of the same link.
    let entityLink = cls [ entity; "text-blue underline decoration-1 underline-offset-2 hover:text-blue-up-1"; focusRing ]
    /// The name inside a link inherits the link's ink rather than wearing `text-ink`.
    let entityLinkName = ""
    /// The mark's seat on the line: an inline box the small avatar's size, its bottom two
    /// pixels below the baseline so a filled square sits on the descender line like a letter
    /// with one, and a stroked glyph — whose lowest vertex is ~2px above its box's bottom —
    /// lands on the baseline itself. A gap to the name, since inline content has no `gap`.
    /// `inline-grid`, not `inline-block`: the agent's mark draws its inner square as a grid
    /// child (`agentAvatar`), and a seat that overrode the display left it an empty box.
    let entitySeat = "inline-grid place-items-center align-[-2px] mr-1"
    /// A person's mark on that seat: the checker, sized as the small avatar is everywhere.
    let entityAvatar = cls [ avatarSm; entitySeat ]
    /// A non-person reference's mark on that seat: an icon in a step fainter ink than the
    /// name beside it, so the name is what the eye reads and the mark says what kind.
    let entityMark = cls [ entitySeat; "text-ink-dim" ]

    /// The people's colours, one per seat: the theme's `--color-person-n`, said as the text
    /// colour the `checker` utility paints with. Written out, not composed, because Tailwind
    /// generates only the classes that appear literally in the source.
    ///
    /// BLUE IS THE AGENT. The mark is a blue cube held by green panels — the agent and the
    /// people (`assets/logo`) — and the agent's avatar is a blue diamond, so no person is
    /// drawn in blue or in anything that reads as it: no colour between cyan and violet (HSL
    /// hue 180°–270°). A checker once carried the mark's own `#1ba1e2` and sat directly above
    /// the agent's diamond in the same colour. Never red either, which is `--color-err`.
    ///
    /// A person's colour is their seat modulo the length, so the length is how many people a
    /// room holds before two share one. It was their id's hash, which made the length a
    /// collision RATE instead — even at eleven colours a three-person room shared one about a
    /// quarter of the time. The colours themselves are in `app/tailwind.css`, and `Phase4`'s
    /// "People's marks" suite holds them to the rule, the contrast and each other.
    let private personColours =
        [| "text-person-1"; "text-person-2"; "text-person-3"; "text-person-4"; "text-person-5"; "text-person-6" |]

    /// How many people a room seats before colours repeat.
    let seats = personColours.Length

    /// A person's colour, as a CSS colour for an inline style: the caret and its flag, a
    /// presence dot, a draft's edge. Read from the seat, as the checker is, so a person's mark
    /// and their cursor cannot disagree.
    let humanColour (seat: int) : string = sprintf "var(--color-person-%d)" (seat % seats + 1)

    /// A person's checker, from their seat (`Attribution.seatOf`).
    let humanAvatar (seat: int) : string = "checker " + personColours.[seat % seats]

    /// The agent's mark: a dark square holding a small solid blue DIAMOND — the product's
    /// mark seen from above, its first frame, in the same blue the mark is drawn in. The
    /// square turned 45° is the whole of it: solid, one colour, no rim. The humans keep their
    /// checkers; a diamond is the agent and only the agent.
    let agentAvatar =
        "bg-agent-ground grid place-items-center after:content-[''] after:w-2 after:h-2 after:bg-blue after:rotate-45"

    let agentAvatarSm =
        "bg-agent-ground grid place-items-center after:content-[''] after:w-1.5 after:h-1.5 after:bg-blue after:rotate-45"

    /// A thing's mark — the session, the system, a repo's file: the agent's shape with the
    /// colour taken out. A dark square holding a small grey SQUARE, upright where the agent's
    /// is turned, so the two read as kin and never as each other, and grey because a colour
    /// on this page is somebody: blue the agent, the checker tones the people.
    let thingAvatar =
        "bg-surface-2 grid place-items-center after:content-[''] after:w-2 after:h-2 after:bg-ink-faint"

    // --- The starting screen: what a browser looks at while a session launches -----------
    // Black, one object, one line of type. The mark's intro plays once at 224px, the
    // wordmark rises under it as the last frame lands, and the status line under that says
    // the one thing the operator wants to know. Nothing else: no band, no table.
    let startScreen = "min-h-dvh flex flex-col items-center justify-center px-4 pb-16"
    let private startMarkBox = "w-56 h-56 [&>svg]:block [&>svg]:w-full [&>svg]:h-full"
    /// The intro, breathing once it has landed. Hidden for a reader who declined motion —
    /// SMIL cannot read the preference, so the page decides for it.
    let startMarkIntro = startMarkBox + " motion-reduce:hidden animate-breathe motion-reduce:animate-none"
    /// The still mark, shown only where the intro is not.
    let startMarkStill = startMarkBox + " hidden motion-reduce:block"
    let startWord = wordmark + " mt-2 animate-rise motion-reduce:animate-none"
    let startStatus = "mt-7 flex items-center gap-2"
    /// The session's minted id beside the status word, in the face an identifier wears; the
    /// caps voice of the word around it is undone here rather than composed.
    let startStatusId = "font-terminal text-code text-ink-dim normal-case tracking-normal"
    let startLinks = "mt-10 " + body

    // --- Workspace regions ---------------------------------------------------------------
    // Two bits live on the root <html> element, outside `#app`, so a re-render never fights the
    // column's transition: `nav-alt` and `settings-open`, both drawn from the model's `Column`
    // (`PaneShell.setColumn`). Default = sidebar visible on desktop, off-canvas on mobile;
    // `nav-alt` = the inverse. Expressed with arbitrary variants so it stays plain Tailwind.

    /// The stylesheet's `wide` variant (app/tailwind.css), as the media query a SCRIPT asks.
    /// The shell reads `nav-alt` the opposite way on each side of this line, so the script
    /// that writes it and the stylesheet that draws it must draw the line in one place — this
    /// string is that place for the script, and the cheap tier fails if it ever stops being
    /// the stylesheet's own query. `phone` is its complement, so nothing asks for that one.
    let wideMedia = "(width >= 48rem) and (height > 500px)"

    // --- The degradation bar's one number ------------------------------------------------
    // On a phone the bar is FIXED above all three panes, so the panes leave room for it, so
    // its height and that room are the same number in three class strings. They cannot be
    // composed from a shared token: Tailwind generates only classes that appear LITERALLY in
    // the source, so `"phone:h-" + n` produces a class the stylesheet never contains — which
    // fails silently, the bar falling back to its content height and the reservation to
    // whatever the last edit left. So the number is written out three times, here, together,
    // and `Phase4`'s theme suite fails if the three ever stop agreeing.

    /// The bar's own height on a phone.
    let degradedBarHeight = "phone:h-12"
    /// The room a pane anchored to the top edge leaves for it (the two off-canvas overlays).
    let degradedBarRoom = "phone:[.is-degraded_&]:top-12"
    /// The same room, paid in padding, by the column that is in normal flow.
    let degradedBarRoomPad = "phone:[.is-degraded_&]:pt-12"

    /// Reduced motion for a side column, whose transitions are all behind a breakpoint: the
    /// width shutting on desktop (`wide:`), the drawer sliding on a phone (`phone:`). Tailwind
    /// orders the stylesheet by variant, so a bare `motion-reduce:transition-none` is EMITTED
    /// ABOVE both and loses to each; the cancellation has to carry the same breakpoint as the
    /// transition it cancels (as `draftCommitBase` does, for the same reason).
    let private reduceColumnMotion = "wide:motion-reduce:transition-none phone:motion-reduce:transition-none"

    /// The 280px column. It holds TWO faces — the workspace nav and settings (`navPane` /
    /// `settingsPane`) — because settings is a place you go, not a thing that covers what you
    /// were reading. Collapsing on desktop animates the column's width shut; on mobile the
    /// column is an off-canvas drawer that slides over the conversation.
    let sidebar =
        "relative w-side shrink-0 bg-panel h-full overflow-hidden z-40 " + Stroke.dividerRight + " "
        + "wide:transition-[width] wide:duration-200 wide:ease-out "
        + "wide:[.nav-alt_&]:w-0 wide:[.nav-alt_&]:border-r-0 "
        + "phone:fixed phone:inset-y-0 phone:left-0 phone:w-[min(var(--spacing-side),84vw)] "
        + degradedBarRoom + " "
        + "phone:transition-transform phone:duration-200 phone:ease-out phone:-translate-x-[101%] "
        + "phone:[.nav-alt_&]:translate-x-0 " + reduceColumnMotion

    /// One face of the column: the two are stacked in place and held at the column's full
    /// width, so nothing reflows while the column animates shut.
    ///
    /// `visibility` is in the transition list on purpose — it is what keeps the hidden face out
    /// of the tab order and the accessibility tree, and transitioning it holds `visible` for the
    /// whole fade OUT (a discrete step at the end) while flipping instantly on the way IN.
    /// `opacity-0` alone would leave focusable controls behind an invisible panel.
    let private paneBase =
        "absolute inset-y-0 left-0 w-side phone:w-[min(var(--spacing-side),84vw)] flex flex-col px-6 pb-5 "
        + "overflow-y-auto transition-[opacity,visibility] " + Motion.pace

    let navPane = paneBase + " [.settings-open_&]:opacity-0 [.settings-open_&]:invisible"

    /// Acrylic, because it is the one pane that floats over another: while it fades in, the
    /// workspace face is still under it, and the blur is what says so.
    let settingsPane =
        paneBase + " opacity-0 invisible [.settings-open_&]:opacity-100 [.settings-open_&]:visible " + acrylic

    // Zune's signature motion, recast: the two faces do not merely cross-fade — the arriving
    // face's rows slide in from the side a beat apart, and the leaving face's go in one piece,
    // the way it came from. Directional, fast, and never delayed on the way out.
    //
    // The delays are LITERAL class names, one value per lane, because Tailwind scans this source
    // for class names: a `sprintf "delay-[%dms]"` would compose a class that is never generated
    // (the same trap the avatar checkers hit — see `@source inline` in app/tailwind.css).
    // `translate`, not `transform`: Tailwind v4's `translate-x-*` utilities set the CSS
    // `translate` property, so a transition list naming `transform` animates nothing and the
    // rows would jump into place. (Measured on the live page — computed `transform` stayed
    // `none` through the whole toggle.)
    let private laneBase = Motion.slideFade

    let private navLaneOut = " [.settings-open_&]:-translate-x-6 [.settings-open_&]:opacity-0 [.settings-open_&]:delay-0"
    /// The nav's rows, in arrival order (they return staggered and leave together).
    let navLane0 = laneBase + navLaneOut
    let navLane1 = laneBase + " delay-[60ms]" + navLaneOut
    let navLane2 = laneBase + " delay-[120ms]" + navLaneOut

    let private settingsLaneIn = " translate-x-6 opacity-0 [.settings-open_&]:translate-x-0 [.settings-open_&]:opacity-100"
    /// The settings rows, in arrival order (they arrive staggered and leave together).
    let settingsLane0 = laneBase + settingsLaneIn
    let settingsLane1 = laneBase + settingsLaneIn + " [.settings-open_&]:delay-[60ms]"
    let settingsLane2 = laneBase + settingsLaneIn + " [.settings-open_&]:delay-[120ms]"

    /// Mobile-only backdrop behind the open drawer; clicking it closes (data-nav-toggle).
    let scrim = "hidden phone:[.nav-alt_&]:block fixed inset-0 z-30 bg-black/60"

    /// The shared header band (`--spacing-band`): baselines align across the sidebar/main
    /// hairline because all three heads compose the same token.
    let sideHead = "h-band shrink-0 flex items-end justify-between pb-5"
    let sideSection = "flex flex-col gap-2 py-4 " + Stroke.dividerTop
    let sideSectionFirst = "flex flex-col gap-2 pb-4"
    let sideRow = "flex items-baseline justify-between gap-2"
    /// A roster row aligns on the TEXT BASELINE, so the 11px caps ("you", a status) sit on
    /// the 13px name's baseline instead of floating box-centred beside it.
    let person = "flex items-baseline gap-2.5 font-light text-small leading-5 text-ink-dim"
    /// The avatar opts back out: a box has no baseline (it would park its bottom edge on
    /// the line), so it centres in the row the way it always did.
    let personAvatar = "self-center"
    let commandCard = "flex flex-col gap-1 px-3 py-2 bg-surface"

    /// The contents' entries, as one stack. Their own container rather than the section's, so
    /// the gap the section puts under its heading (`sideSection`'s 8px) is not also put
    /// between the entries: on a phone each entry is already a full 44px target
    /// (`chapterEntry`), and a gap on top of that spread two chapters 52px apart.
    let chapterEntries = "flex flex-col gap-2 phone:gap-0"

    /// One chapter in the contents: the roster row's shape, worn by a button.
    ///
    /// The same row as a person's, because the column holds one kind of list and a second
    /// shape here would read as a second kind of thing. What it adds is what a control has to
    /// have: a name that brightens under the pointer, and the ring a keyboard sees. The
    /// padding is spent OUTWARD, so the text sits on the column's rail with everything else
    /// and the hover fill grows around it.
    let chapterEntry =
        cls [ person; "w-full text-left px-2 -mx-2 py-0.5 phone:py-3 hover:text-ink hover:bg-surface-2"
              "transition-colors cursor-pointer"; focusRing ]

    /// Its mark, the same dot the rule in the timeline wears, so one chapter looks like one
    /// thing in both places.
    let chapterEntryDot = "w-1.5 h-1.5 rounded-full bg-ink-faint shrink-0 self-center"

    /// One terminal under the environment: the contents' entry, because the column holds one
    /// kind of list. Its name truncates and its state mark (`View.terminalMark`) follows it,
    /// as on the pivot; the way to the ones it does not list wears it too.
    let terminalEntry = chapterEntry

    /// The generated read surface (Plan 15). A query answers with rows, fields, or one
    /// value, and these are the renderings — defined ONCE here because they are what every
    /// future query gets to look like, including the ones nobody has written.
    ///
    /// **A row is a RECORD, not a table row.** The lane a query lands in is 280px wide at
    /// every breakpoint — the settings face of a fixed column on desktop, that same column
    /// as a drawer on a phone — and no table of more than two columns fits it. It was a
    /// table, scrolling inside itself, and what that bought was a surface whose every
    /// meaningful column sat past a horizontal scroll: four watched pull requests read as
    /// four identifiers and a clipped title, with not one of the toned words a tone exists
    /// to colour on the screen at rest. A record wraps where a table scrolls, so the answer
    /// is READ rather than found.
    ///
    /// Which is also why `Fields` and `Rows` are drawn by one of these and not two: a
    /// fields answer is one record, a rows answer is a list of them, and a second rendering
    /// of the same thing is a second thing to keep in step.
    let queryRecords = "flex flex-col gap-3"
    /// One record: its name, then its fields.
    let queryRecord = "flex flex-col gap-1"
    /// Every record after the first, separated by the hairline the settings sections
    /// already use — so a list of records keeps the pane's rhythm rather than inventing one.
    let queryRecordAfter = queryRecord + " pt-3 " + Stroke.dividerTop
    /// The record's name — its first column's value, at full `ink` and with no label of its
    /// own, because a list of records is scanned by name exactly as a table was scanned by
    /// its first column, and "pull request: octo/hello#1" says the word twice.
    let queryRecordName = "font-light text-small leading-5 text-ink break-words"
    /// The pairs, each one over two lines: the label, then its value on the lane.
    ///
    /// It was a two-track grid, a label column beside a value column, capped so that a long
    /// label could not take the lane. Measured, that cap WAS the lane: the pane is 217px of
    /// content, the resources query's longest label spent the whole 7rem allowance, and the value
    /// got 93px — a label track wider than the value it was starving, and
    /// `!sock:/nix/var/nix/daemon-socket/socket` read four lines deep at ten characters a
    /// line. Every narrower cap moves the same problem: 217px cannot hold a caps label
    /// column beside a value, and since the notation a value is one unbreakable token with
    /// no space in it to wrap at politely.
    ///
    /// So the label goes above, which is what the legend under these panels already does
    /// and for the same reason. What it gives up is real — values no longer line up down a
    /// rail — and it was buying that alignment at 93px, where nothing was readable to line
    /// up. A pair costs one line more; a value costs three fewer.
    let queryFields = "flex flex-col gap-y-1"
    /// One pair, kept together. A `<div>` inside a `<dl>` is the grouping element doing its
    /// job: the pairing is in the markup a screen reader walks, not only in the spacing.
    let queryField = "flex flex-col"
    /// Field labels carry `ink-dim`, not `ink-faint`: they are 11px caps, which is below
    /// the 24px/19px-bold threshold where 3:1 would do, so they need the 4.5:1 ratio
    /// against `surface` that `ink-dim` gives (CLAUDE.md, UI baseline).
    ///
    /// `overflow-wrap:anywhere` — not the `break-words` a value wears — is what makes the
    /// cap above unconditional: it is the form that lowers min-content, and `fit-content`
    /// still floors at min-content, so one unbreakable label word would otherwise walk
    /// straight back through the cap and starve the value again.
    let queryFieldLabel = caps + " text-ink-dim [overflow-wrap:anywhere]"

    /// The four inks a `QueryTone` asks for, named by what they mean so a query never
    /// spells a Tailwind class. Text only — never filled, never boxed, the rule the
    /// Statuses block above sets — so a tone emphasises the word rather than wrapping it,
    /// and a reader who cannot see the colour still reads the value. All four already
    /// clear 4.5:1 on every surface; the theme-contrast suite is what says so.
    let toneOk = "text-green"
    let toneBusy = "text-ink-dim"
    let toneBad = "text-err"
    let toneMuted = "text-ink-faint"

    /// A value, with no colour of its own. A base that carried one could not be recoloured
    /// by appending: two Tailwind text utilities on one element resolve by stylesheet order
    /// rather than by the order they are written here, so the colour is always the last
    /// thing added and never the second.
    let private queryValueShape = "font-light text-small leading-5 break-words"

    let queryValue = queryValueShape + " text-ink-dim"
    /// The same value, in the ink its tone asked for.
    let queryValueIn (tone: string) = queryValueShape + " " + tone

    /// How to read values written in a vocabulary rather than in words: the shapes, and
    /// what each one means.
    ///
    /// A real `<details>`, like every other disclosure in the product, and ABOVE what it
    /// explains. It was an open list underneath, which is where a footnote goes — but a
    /// legend is not a footnote: you consult it before reading, and eight entries at full
    /// weight under a panel nobody opened for a glossary is a wall to scroll past. Closed
    /// it costs one dim line, and a reader who already knows the notation never pays more.
    ///
    /// STACKED, where a query's own fields are a two-track grid: a shape is up to 25
    /// characters of punctuation with no break opportunity in it, so a label track would
    /// either take the lane or wrap the shape one character per line. The pairs read down
    /// instead, which is also how a legend is read — you arrive knowing the token and look
    /// for it.
    let queryLegend = "group flex flex-col gap-1"
    /// The name of the glossary, and the control that opens it. The product's own
    /// disclosure voice (`detailSummary`, the timeline's tool runs, a block's facts) rather
    /// than the caps a label wears: it now sits directly under the panel's title, and two
    /// caps lines in a row read as two headings — the second of which is a control. A real
    /// `<summary>`, so the disclosure is the browser's: keyboard-operable and announced
    /// without a line of script, with `list-none` dropping the platform triangle as every
    /// other summary in the product does.
    let queryLegendSummary =
        cls [ small; "flex items-center gap-1.5 cursor-pointer list-none pb-1"
              "hover:text-ink-dim transition-colors duration-150 ease-out"
              focusRing ]
    /// Its mark. A control in this product is quiet type with a thin chevron pointing the
    /// way the surface is about to move — `settings ›` sets the rule — and without one this
    /// summary was a quiet word in a stack of label-over-value pairs, which is exactly what
    /// a VALUE looks like: `resources` over `how to read` read as the panel's answer. The
    /// mark turns to point down when the glossary is open, so the line says which way it
    /// goes rather than only that it goes.
    let queryLegendMark =
        "text-ink-faint transition-transform duration-150 ease-out motion-reduce:transition-none "
        + "group-open:rotate-90"
    /// The entries themselves, under the summary. The `<dl>` holds only terms and their
    /// meanings — the block's own name is the summary above it, because a heading inside
    /// the list would be a term of the glossary rather than what the glossary is called.
    ///
    /// The gap is BETWEEN entries and not inside one: evenly spaced, a meaning sat as close
    /// to the next shape as to its own, and eight pairs read as sixteen lines.
    let queryLegendEntries = "flex flex-col gap-2 phone:gap-0"
    /// One pair, kept together. A `<div>` inside a `<dl>` is exactly what the grouping
    /// element is for, so the pairing is in the markup a screen reader walks and not only
    /// in the spacing a sighted reader sees.
    let queryLegendEntry = "flex flex-col"
    /// The shape. At full `ink` because it is the thing being looked up, and
    /// `overflow-wrap:anywhere` because `path:PATH[>AT]:ro|rw|ovl` is one unbreakable token
    /// as far as the polite rule is concerned.
    let queryLegendShape = "font-light text-small leading-5 text-ink [overflow-wrap:anywhere]"
    /// What it means. `ink-dim` rather than `ink-faint`: this is prose somebody reads to
    /// understand what they are consenting to, so it holds the 4.5:1 floor.
    let queryLegendMeaning = "font-light text-small leading-5 text-ink-dim break-words"

    /// On a phone this column sits under the fixed degradation bar, so it pays for it in
    /// padding — but only while the bar is there (`degradedShell`).
    let mainColumn = "flex-1 flex flex-col min-w-0 h-full " + degradedBarRoomPad

    /// The phone's band is a ROW, not a compressed copy of the desktop's stack: the two
    /// chevrons a phone always has in this band — the sidebar's and the terminals' — and the
    /// title between them, on one line, because that is what they are. Stacked, the 28/32
    /// heading over its id spent 80px of an 844px screen (10%) restating the tab you are on;
    /// the title steps down a size (`titleInput`) and the id keeps hanging out of flow under
    /// it (`titleId`), which seats the whole band in 56px.
    ///
    /// `pb-[11px]` leaves the row 44px over the band's 1px rule, which is what every control
    /// in it is on a phone: the chevrons, the edge tab and the title's own box are thumb-sized
    /// and fill the row exactly, and the id hangs into the 11px under it.
    ///
    /// On a wide screen the stack stays: the header and the sidebar wordmark share one bottom edge
    /// (`h-band`, `items-end`), and that shared baseline is the whole reason the band exists.
    let header =
        "relative h-band shrink-0 flex items-end gap-4 px-8 pb-5 "
        + "phone:h-14 phone:items-center phone:gap-2 phone:px-4 phone:pb-[11px] "
        + Stroke.dividerBottom

    /// A slow catch-up's progress, drawn ON the header's bottom rule: the rule is the one
    /// line every screen has under the title, and a bar growing along it says "loading, this
    /// far" without taking a pixel of room from the band — which on a phone is 56px and
    /// geometry-pinned. Two pixels tall over the one-pixel rule, so it reads as a bar and not
    /// as the rule changing colour. Its width moves once per paced render (`Render`, every
    /// half second while a client stays behind), and the transition carries it between.
    let catchUpBar =
        "absolute left-0 -bottom-px h-0.5 bg-blue pointer-events-none "
        + "transition-[width] duration-500 ease-linear motion-reduce:transition-none"

    /// The header's right-hand group: sync status, and — only while the sidebar is off screen —
    /// the agent's absence. `pb-[1px]` is optical, not rhythm: it drops the 11px caps line's
    /// baseline onto the wordmark/title baseline (pb-1 left it 3px high, measured live). On a
    /// phone there is no wordmark to meet: the group centres on the title's line instead.
    let headerAside = "ml-auto shrink-0 flex items-end gap-5 pb-[1px] phone:items-center phone:gap-3 phone:pb-0"
    let headerStatus = "shrink-0"

    /// What this session's pull requests amount to, in the header band: the same line the
    /// Manager's roster shows for this session, on the page a person is actually looking at.
    ///
    /// A real button, because it GOES somewhere — the panel with the rows behind the line is
    /// one press away in settings, and a line a reader cannot follow is a line that raises a
    /// question it will not answer. Bordered like nothing else in the band on purpose: it
    /// wears the caps voice of its neighbours and takes its colour from the tone of the worst
    /// thing it is reporting, so the band gains a word rather than a control.
    ///
    /// Desktop only. The phone band is 56px and geometry-pinned (`Browser.fs` measures that
    /// the bar never covers the header under it), so a third thing in it is a redesign of the
    /// band rather than an addition to it — and a phone has the tab title, which is the
    /// signal that reaches somebody who is not looking at all.
    let private prStripBase =
        "bg-transparent border-0 cursor-pointer " + caps + " transition-colors " + focusRing + " phone:hidden"

    let prStripIn (tone: string) = prStripBase + " " + tone

    /// The agent's absence, FOLLOWING the surface that normally says it: shown only when the
    /// sidebar column (which holds the real call to action) is collapsed or off-canvas — which
    /// on a phone is most of the time. Never both at once, so it is a relocation, not a repeat.
    /// Same visibility rule as `navReopen`, for the same reason.
    let headerNoAgent =
        "bg-transparent border-0 cursor-pointer " + caps + " text-ink-dim hover:text-ink transition-colors "
        + focusRing + " "
        + "hidden wide:[.nav-alt_&]:block phone:block phone:[.nav-alt_&]:hidden phone:py-3.5"

    /// The nav column's own mount of the connection report. Hidden on a phone, where the bar
    /// above every pane carries it — the two mounts are complementary by construction, so
    /// exactly one is ever on screen and the report is never read twice.
    ///
    /// It covers the STATUS only, never the reconnect card: the card is an action, and a
    /// phone that could see what was wrong but not the button that fixes it would be the
    /// worse half of the trade.
    let connectionInColumn = "phone:hidden flex flex-col gap-2"

    /// The class the shell wears while anything is degraded, so the panes can make room for
    /// the bar fixed above them. A marker, never a look: the two rules that read it are
    /// `sidebar`, `mainColumn` and the terminals column below, and only on a phone.
    let degradedShell = "is-degraded"
    /// The connection report where the nav column cannot be seen. On a desktop with the
    /// column open it is not rendered at all — the column says it, and saying it twice on one
    /// screen is what this whole surface was rebuilt to stop.
    ///
    /// A hairline notice, never a modal and never a blocker: the client under it stays fully
    /// usable, which is the promise the words in it make.
    ///
    /// One row that never wraps. `signInPrompt` below takes a second row because it holds a sentence;
    /// this holds three short things — a status, a disclosure, and the way back — and its
    /// height is a number the panes reserve, so wrapping is the one thing it must not do.
    let degradedBar =
        cls [ "flex flex-nowrap items-center gap-3 px-8 py-2 bg-surface"
              Stroke.dividerBottom
              // Where the column IS on screen, the column says it. Where it is not — a
              // collapsed nav, or any phone — this does. Same rule as `headerNoAgent`.
              "hidden wide:[.nav-alt_&]:flex phone:flex"
              // A phone shows one pane at a time and the other two are overlays anchored to
              // the top edge, so this leaves the conversation column's flow and sits over all
              // three. `z-50` clears the overlays (`z-40`) and the scrim between them.
              "phone:fixed phone:inset-x-0 phone:top-0 phone:z-50 phone:px-4"
              // One row, always, tall enough for the control it carries whether or not this
              // state has one — the panes reserve this number, so a height that tracked the
              // contents would leave a gap under the bar in some states and overlap the
              // header in others (it did: a wrapped 73px bar against a 36px reservation).
              // It still GROWS when somebody opens the disclosure, and that growth overlays
              // the pane rather than moving it — a notice you opened is one you are reading.
              degradedBarHeight ]

    /// The status word, and the only thing in the row that may be squeezed: an action is
    /// useless truncated and the disclosure is one word, so the give has to come from here.
    let degradedBarStatus = "min-w-0 truncate"
    /// The way back, at the row's end. `ml-auto` rather than a spacer, so the states with no
    /// action to offer close the gap instead of leaving a hole where one would be.
    let degradedBarAction = "ml-auto shrink-0"

    /// The sign-in prompt, in the same slot and the same hairline as the degradation strip:
    /// a notice over the timeline, never a modal and never a blocker. It carries a real
    /// button because unlike a degraded leg — which recovers on its own — a credential that
    /// stopped working recovers only when a person does something.
    ///
    /// A grid rather than a wrapping row, so where each piece lands is decided here and not
    /// by how long the provider's reason happens to be. A wrapping row put the button on a
    /// line of its own under a closed reason, and beside an open one in whatever column was
    /// left over — the one control that matters moved every time somebody read the reason.
    ///
    /// On a phone the status and the button share the top row and the sentence takes the
    /// full width under them; from `md` all three sit on one row. Either way the button is
    /// pinned to the top-right corner, and an opened reason grows DOWN from the sentence.
    /// `items-baseline` aligns on each cell's FIRST line, so that growth moves nothing else.
    let signInPrompt =
        cls [ "shrink-0 grid grid-cols-[minmax(0,1fr)_auto] md:grid-cols-[auto_minmax(0,1fr)_auto] items-baseline"
              "gap-x-3 gap-y-1 px-8 py-2 bg-surface max-md:px-4"
              Stroke.dividerBottom ]

    let signInPromptStatus = cls [ statusErr; "row-start-1 col-start-1" ]

    /// The sentence and its reason, as one cell. The disclosure is inline so its summary
    /// follows the sentence on the same line, and its body — block-level — breaks under both
    /// at the cell's full width rather than in a column beside the summary.
    let signInPromptBody =
        cls [ "min-w-0 col-span-2 md:col-span-1 md:row-start-1 md:col-start-2"
              "[&>details]:inline [&>details]:ml-2 [&_summary]:inline" ]

    let signInPromptAction = cls [ btnPrimary; "row-start-1 col-start-2 md:col-start-3 justify-self-end" ]

    /// A refusal's way out, in the corner the sign-in prompt's button holds: an icon rather
    /// than a primary button, because dismissing news is not the action the notice is FOR.
    let refusalDismiss = cls [ btnIcon; "row-start-1 col-start-2 md:col-start-3 justify-self-end" ]

    /// The refusal notice's four cells, per mount (`RefusalMount`). In the conversation it is
    /// the sign-in prompt's row exactly. In the content pane it keeps that vocabulary at the
    /// pane's own gutter, and stays in the narrow shape — status and dismiss over the
    /// sentence — at every viewport: the `md:` steps above are the WINDOW's width, and the
    /// pane is a column a few hundred pixels wide inside a wide window.
    [<RequireQualifiedAccess>]
    type RefusalShape = { Row : string; Status : string; Body : string; Dismiss : string }

    let refusalIn (mount: RefusalMount) : RefusalShape =
        match mount with
        | RefusalMount.Chat ->
            { Row = signInPrompt; Status = signInPromptStatus; Body = signInPromptBody; Dismiss = refusalDismiss }
        | RefusalMount.Pane ->
            { Row =
                cls [ "shrink-0 grid grid-cols-[minmax(0,1fr)_auto] items-baseline gap-x-3 gap-y-1 px-3 py-2 bg-surface"
                      Stroke.dividerBottom ]
              Status = cls [ statusErr; "row-start-1 col-start-1" ]
              Body = "min-w-0 col-span-2"
              Dismiss = cls [ btnIcon; "row-start-1 col-start-2 justify-self-end" ] }

    /// The mechanism behind a notice, folded away (the degradation strip, the sign-in
    /// prompt, the reconnect card, a credential's fault, the history-store note, a terminal
    /// that stopped marking). What every one of those surfaces has to say FIRST is what it
    /// costs the reader; the provider's own words, the transport's reason and the browser's
    /// storage rules are what they go looking for afterwards, and a notice that leads with
    /// them buries the sentence somebody actually needed.
    ///
    /// A real `<details>`/`<summary>`, like every other disclosure in the product: the
    /// browser's own, so it arrives keyboard-operable and correctly announced.
    let detailNote = "min-w-0"
    let detailSummary =
        cls [ small; "cursor-pointer list-none"
              "hover:text-ink-dim transition-colors duration-150 ease-out"
              focusRing ]
    /// The words inside, on the summary's own column so they read as its continuation.
    let detailBody = small + " block pt-1"

    // --- Editable session title ------------------------------------------------------------

    /// The title block: the editable heading over its dim secondary id. `relative` anchors
    /// the absolutely-positioned remote-cursor overlays; `ml-8` keeps it on the content column.
    /// On a phone the chevron is in the row and pays part of that indent: it occupies 12px
    /// of flow (`navReopen`) plus the row's 8px gap, so 12px more is what puts the heading
    /// back on the 32px rail every message below it sits on — measured live at 390, where the
    /// title's text and the timeline's caret both start at x=48.
    /// `phone:flex-1` is what makes the phone's band a row: the title takes the space the
    /// two chevrons leave rather than sizing to an input's default 20 characters.
    let titleWrap = "relative flex flex-col min-w-0 ml-8 phone:ml-3 phone:flex-1"

    /// The title itself: the heading, worn by a text input — and worn as a HEADING at rest.
    /// It used to carry a dotted underline whose job was to say "this edits"; a rule under 28px
    /// type is a rule, and it said it whether or not anyone was going to type. So the field
    /// says it the way the composer band does instead: the surface lifts a tone under the
    /// pointer and while focused, and the heading becomes a box you are typing in exactly when
    /// you are typing in it. The 8px padding is spent OUTWARD (`-mx-2`), so the glyphs do not
    /// move when the fill appears — the box grows around the text already on screen. Sideways
    /// only: a phone's band seats the id 2px under the title's line box, and vertical padding
    /// put the fill and its ring through it.
    ///
    /// `focusRing` is what a keyboard sees. The fill alone is a real focus signal for a
    /// pointer, but it is a tone step on a dark ground; the ring is the product's own
    /// vocabulary and the UI baseline's floor (AGENTS.md), so the two ride together.
    ///
    /// `md:top-[2px]` is optical: a 28/32 line box holds its baseline 2px higher over the
    /// shared bottom edge than the wordmark's 32/36 does, so the input steps down to put both
    /// on one line (measured live). A phone has no cross-column baseline to meet and no room
    /// for 28px in a 56px row, so it takes the pivot step (19/24) and no nudge.
    ///
    /// And on a phone the field is a thumb's height, which a 24px line in a 56px band does
    /// not leave room for — unless the box takes in the id under it. So it does: 44px tall,
    /// its last 20 of them padding (`pb-5`) that the id hangs in (`titleId`), and the same 20
    /// paid back to the flow (`-mb-5`), so the row lays out the 24px line it always did. The
    /// text sits in the top 24, where it was; the fill and the ring go round the title and its
    /// id together, though a press on the id's own text is the id's, so it can be selected. `Render.placeInputCursor`'s marker
    /// subtracts the padding, so a collaborator's caret stays the height of the line.
    let titleInput =
        cls [ "w-full min-w-0 bg-transparent border-0 px-2 -mx-2 py-0 phone:h-11 phone:pb-5 phone:-mb-5"
              "hover:bg-surface-2 focus:bg-surface-2 transition-colors"; focusRing
              "font-extralight text-heading phone:text-pivot tracking-[-0.01em] lowercase text-ink"
              "placeholder:text-ink-faint truncate relative wide:top-[2px]" ]

    /// The session id, shown small and dim under the title as a stable secondary identifier.
    /// It hangs OUT OF FLOW below the title, into the band's bottom padding: in flow it added
    /// 18px under the title inside the bottom-aligned stack and lifted the title's baseline
    /// that far off the wordmark's (measured 41.5 vs 61 at 1440) — and on a phone it is what
    /// made the band a stack rather than the row it now is.
    /// `mt-1` rather than the old `mt-0.5`: the title now draws a focus ring 2px outside its
    /// own box, and 2px of clearance is what keeps that ring off this line on a desktop. On a
    /// phone the title's box reaches down behind this line (`titleInput`), but the id still
    /// takes the presses that land ON it: it is a thing people copy, and a press that fell
    /// through to the field could only ever edit the title, never select the id.
    let titleId = "font-terminal text-code-sm text-ink-faint truncate mt-1 absolute top-full left-0 right-0 select-text"

    /// A collaborator's selection highlight in the title: an absolutely-positioned span the
    /// browser places and sizes against the input's own box by measurement (the translucent
    /// background is set inline). The `h-8` is the desktop line box, and it is only what the
    /// span wears until that measurement lands — the title is 28/32 at one width and 19/24 at
    /// the other, so the height a marker keeps is the one the browser read off the field.
    /// Ignores pointer events so it never blocks typing; a collapsed selection has zero width.
    let remoteCursor = "absolute top-0 h-8 pointer-events-none rounded-sm"
    /// The caret bar inside a remote selection, offset to the peer's `head` by the browser.
    /// Full height of the marker, so it answers to that one measurement rather than a second.
    let remoteCursorCaret = "absolute top-0 w-0.5 h-full -ml-px"
    /// The peer-name pill floating just above a remote caret.
    let remoteCursorLabel =
        "absolute -top-3 left-0 whitespace-nowrap font-semibold text-[9px] leading-3 "
        + "tracking-[0.08em] uppercase px-1 text-bg"

    /// The reopen chevron. On a wide screen it is floated in the gutter left of the title so that
    /// collapsing the sidebar never shifts the heading off the content column. On a phone it
    /// is IN the row — the band is a line of chrome with the title in it, and a control
    /// hovering over that line would be the one thing on it that is not. It is a 44px target
    /// (`navChevronBase`) reaching to the screen's edge and occupying 12px of flow (`-mx-4`
    /// against the band's 16px padding), which with the row's 8px gap pays 20 of the 32px
    /// indent the heading gives up there (`titleWrap`). Hidden while the sidebar is visible.
    let navReopen =
        "absolute left-2 bottom-4.5 w-6 h-6 place-items-center hidden wide:[.nav-alt_&]:grid "
        + "phone:static phone:grid phone:[.nav-alt_&]:hidden phone:-mx-4"

    // --- Timeline --------------------------------------------------------------------------

    /// The reading column. `break-words` is not typography, it is containment: a scroller on
    /// the vertical axis is a scroller on BOTH (`overflow-x` computes to `auto` beside an
    /// `auto` `overflow-y`), so anything that hangs out of this column takes the whole
    /// conversation sideways with it — under a header that stays put, which is what makes it
    /// read as a broken page rather than as a wide line. A phone column is ~326px and an agent
    /// says things like `/home/user/.yession/sessions/AAZFRYD.../repos`, so a token wider than
    /// the column is the ordinary case, not the pathological one. `overflow-wrap` inherits, so
    /// the rule is stated once for everything the timeline will ever hold.
    /// The leading gap is IN FLOW, not this box's padding, and that is load
    /// bearing rather than a spelling preference. A sticky child's `top-0` pins it to this
    /// scroller's padding box — so a top padding here is a strip the pinned author line can
    /// never reach, and scrolled text rides up through it above the name. Carried in flow
    /// instead, the gap scrolls away with everything else and the line pins flush.
    /// `min-h-0` is what the frame around this used to carry. The frame existed to give the
    /// rail an absolute box that did not scroll; with the rail gone it was a wrapper around
    /// one child, and a flex child that scrolls has to be allowed to shrink or it grows past
    /// the column instead.
    /// `overflow-x-hidden` is the guard for what `break-words` cannot reach — a flex item
    /// that refuses to shrink, an element with a width of its own. The next of those clips
    /// at the column's edge, which is a defect a reader can see and name; sliding the whole
    /// conversation under the header is one they cannot. It hides nothing from the test that
    /// pins this: `scrollWidth` still counts what is clipped.
    ///
    /// BOTTOM-ALIGNED while the conversation is shorter than the column: the newest thing said
    /// sits by the composer it was typed into, and the empty space goes above the first
    /// message rather than between the last one and the composer (three short messages on a
    /// 1440x900 screen used to end 598px above it). What takes the space is a `::before` that GROWS —
    /// a flex item, so the column's own gap follows it and IS the leading gap, and zero high
    /// once the conversation fills the column, so a long one scrolls from its start exactly
    /// as before. Not `justify-end`: overflow pushed past the START edge of a scroller is
    /// unreachable in some engines, so a long conversation would lose its top. The first
    /// child's own margin goes to zero so nothing stacks on top of that gap (a chapter rule
    /// opening the conversation brings a margin of its own); on a phone the gap is 20 and the
    /// leading gap 16, hence the `-mb-1`.
    ///
    /// The ring is drawn INSIDE: focus lands on the column itself when the "jump to latest"
    /// that brought a keyboard reader here goes, and a ring outside a box that fills its
    /// region would be clipped to nothing.
    let timeline =
        "flex-1 min-h-0 overflow-y-auto overflow-x-hidden px-8 pb-6 flex flex-col gap-6 "
        + "before:grow [&>*:first-child]:mt-0 "
        + "max-md:px-4 max-md:pb-4 max-md:gap-5 max-md:before:-mb-1 break-words "
        + focusRingInset

    /// The box `timeline` floats its "jump to latest" over — mirrors `terminalReplayRegion`'s
    /// reason for existing: `timeline` IS the scroller, so an `absolute` child of it would
    /// scroll away with everything else, and something has to sit around it to be the
    /// positioned ancestor the float resolves against instead.
    let chatRegion = "relative flex-1 min-h-0 flex flex-col"

    /// The float's own slot: positioning, the scrim, and the show/hide `Render.fs` toggles (a
    /// scroll-distance fact, not model state — see there for why), kept off the button itself
    /// so toggling it can never fight the button's own layout for which `display` wins.
    ///
    /// The scrim fades to `bg` and NOT to `surface`: the timeline draws no fill of its own, so
    /// what a line of prose actually sits on down there is the canvas `mainColumn` inherits.
    /// Fading to the wrong one would leave a visible band edge where the two greys meet.
    /// Tonal, so the module header still holds — the ONE gradient spent in this product is the
    /// composer's blue→green edge, and darkening toward the ground the text is already on is
    /// not a second one.
    ///
    /// `pointer-events-none`, with the button taking them back: the slot is a full-width strip
    /// lying over live text, and a scrim that swallowed a tap or a selection would take more
    /// from the reader than the contrast it buys.
    ///
    /// `px-8` is the timeline's own padding, so the rail inside it has the reading column's
    /// geometry exactly — see `chatJumpToLatestRail`. None on a phone, where the grounds bleed
    /// to the screen's edges and the column they draw is the whole width.
    let chatJumpToLatestSlot =
        "absolute inset-x-0 bottom-0 z-10 pointer-events-none px-8 max-md:px-0 "
        + "pt-12 pb-4 bg-linear-to-t from-bg/90 to-transparent"

    /// How wide anything in the timeline is allowed to get.
    ///
    /// 38rem is 608px, which at the body's 15px Source Serif is about 68 characters — inside
    /// the 60-75 a line of prose is comfortable at. It was 46rem/736px, near 96, and a line
    /// that long is one the eye loses its place returning from.
    ///
    /// A token rather than the five copies of `max-w-[46rem]` this replaced. The measure is
    /// ONE decision — a chip, a tool run and a message all sit in the same column, and a
    /// number written five times is a number that gets changed four times.
    ///
    /// Uncapped on a phone, where the screen is already narrower than any measure worth
    /// setting and the grounds inside it run edge to edge (`itemGround`).
    let readingColumn = "max-w-[38rem]"

    /// The reading column again, drawn in the slot so the button can be placed against the
    /// column's edge rather than against the window's.
    ///
    /// It used to be centred, as a control a thumb reaches with either hand — and a centred
    /// opaque square over a column of prose covers whatever line is under it. Measured on a
    /// phone it hid 22px of a paragraph. So it stands where no text is: beside the column on
    /// a desktop, and on a phone, where there is no beside, in the strip every item keeps
    /// free for its actions handle (`itemGround`'s `pr-8`), which no message's words enter.
    let chatJumpToLatestRail = cls [ readingColumn; "max-md:max-w-none relative h-8" ]

    /// The button on that rail: the chat's twin of `terminalJumpToLatest`'s solid ground —
    /// opaque so it reads over whatever is scrolling under it, no rounded corners because
    /// this product draws none (see `itemGround`).
    ///
    /// 32px square, which is the width of that strip: `left-full` puts it just past the
    /// column on a desktop (in the gutter, or in the timeline's own 32px padding when the
    /// column fills the pane), and `right-0` on a phone puts it exactly over the strip, its
    /// centre on the same line as every handle above it.
    let chatJumpToLatest =
        cls [ "pointer-events-auto absolute bottom-0 left-full max-md:left-auto max-md:right-0"
              "w-8 h-8 flex items-center justify-center bg-surface"
              "text-ink-dim hover:text-ink cursor-pointer transition-colors"
              Stroke.ring; Stroke.rim; focusRing ]

    /// One actor's consecutive run — their messages, the commands they ran, the tools they
    /// called — under ONE author line. Attribution is said where it CHANGES, which is how a
    /// conversation reads; a name repeated over every consecutive turn is wallpaper, and
    /// wallpaper is what stops a reader noticing when the speaker actually turns over.
    ///
    /// `gap-3` between the members is what the message GROUNDS grow into — `itemGround` pulls
    /// half of it back at each end — so two of them meet rather than leaving a dead stripe
    /// between two hoverable surfaces.
    let messageGroup = cls [ "flex flex-col gap-3"; readingColumn; "max-md:max-w-none" ]

    /// The author line: avatar and name, STICKY — while a long run scrolls, who is speaking
    /// stays readable at the top of the column.
    ///
    /// `pb-2 -mb-1` gives the stuck line's ground room past the label, so scrolled text does
    /// not touch the name: padding paints it, the negative margin keeps it out of flow.
    ///
    /// There was a `pt-6 -mt-6` above it meant to do the same upward — and it did neither
    /// thing it claimed. Padding pushes content DOWN inside a box; a negative top margin pulls
    /// the FOLLOWING siblings up rather than lifting this box. Measured, the pair put the name
    /// 48px below the column top instead of 24 and laid the first message 16px UP INTO this
    /// opaque `bg-bg z-10` band — so every conversation opened with its first line sliced in
    /// half under the speaker's name, and the ground it was buying extended nowhere above the
    /// box at all.
    ///
    /// What it was reaching for is real, and `timeline` now holds it: the column's leading gap
    /// is carried in flow rather than as the scroller's padding, so `top-0` pins this
    /// line flush to the scrollport's own top edge and there is no strip left above the name
    /// for scrolled text to show through. The rule that fixes it lives with the box that
    /// creates it, which is why it is stated there and not compensated for here.
    /// It was `pb-1`, and the extra step is the boxes' doing: the ground under the label now
    /// has to clear the first item's own GROUND rather than just its first line, and that
    /// starts 8px above the words in it. At `pb-1` a hovered first message came within four
    /// pixels of the name.
    let messageGroupHead =
        "sticky top-0 z-10 bg-bg flex items-center gap-3 pb-2 -mb-1"

    /// THE GROUND one item stands on — the rectangle that lights under the pointer, and that
    /// a jump from the rail flashes (`animate-reveal`). Shared by a message and an act note,
    /// because "mark any of it" is the promise and a surface that only some of the timeline
    /// had would say the opposite.
    ///
    /// Sharp, filled, unbordered: this product has no rounded rectangle in it, and a border
    /// here would be the first thing that is neither a field nor a button to carry one. The
    /// lit state's rim is the exception and it lives in the keyframes, because it exists for
    /// a second and a half — see `@keyframes reveal` in `app/tailwind.css`. That flash is the
    /// GROUND's; a terminal block, which has no ground, wears `reveal-line` instead.
    ///
    /// `-my-1.5 py-2` rather than plain padding: 8px inside each end, six of which is taken
    /// back out of the 12px that already sat between two items. The ink keeps its rhythm and
    /// the two grounds MEET, so a pointer travelling down the column is never over nothing.
    ///
    /// No left padding, because the 32px avatar gutter every body already carries
    /// (`messageBody`, `actNote`) is the box's left padding — the box starts at the column's
    /// edge and not one word moves. `pr-8` is the actions control's berth: without it the
    /// last words of a wrapping line run underneath the ellipsis.
    let itemGround =
        cls [ "relative group/item -my-1.5 py-2 pr-8"
              // FULL BLEED on a phone: the box escapes the scroller's own padding and puts
              // exactly as much back, so the ground reaches both edges of the screen and the
              // text stays on the line it was on. A 390px screen has no margin to spend on
              // making a surface look like a card.
              "max-md:-mx-4"
              "hover:bg-surface transition-colors duration-150 ease-out" ]

    // --- The fold: the one disclosure the timeline has -------------------------------

    /// The fold: an arrow in the gutter, a title beside it, and something unfolding beneath
    /// — an act's particulars, a turn's work, one call's input and output. ONE control, so
    /// the timeline's disclosures are one thing a reader learns once (`View.foldArrow`).
    ///
    /// A ROW owns the geometry, and that is the whole of why this aligns. The arrow used to
    /// place itself — `absolute left-0 top-2 h-5` — against a first line it ASSUMED was
    /// twenty pixels tall and eight pixels down, so every row that wanted one had to be
    /// given `relative`, a gutter, `py-2`, and a title overridden to `leading-5`: four
    /// compensations, one per row kind, and a mono line (`text-code-sm` is 11/16, not 13/20)
    /// still sat two pixels off because its own step brought its own line-height. Here the
    /// row is a grid — gutter, then content — and it states the line box ONCE (`leading-5`).
    /// The arrow is `1lh` of that box and centred in it, the title inherits it, and a new
    /// kind of row aligns by being a row.
    /// The gutter is ONE width everywhere (`2rem`), including on a phone: a row that bleeds
    /// to the screen's edges (`itemGround`) puts the bleed back as padding, exactly as a
    /// message does (`max-md:pl-4`), rather than widening its gutter by it — which put a
    /// bled row's arrow sixteen pixels left of a nested row's on a phone and nowhere else.
    let foldRow = cls [ "grid grid-cols-[2rem_1fr] leading-5" ]
    /// Everything that is not the arrow sits in the content column, and says so: a row is a
    /// grid, and a child that did not name its column would be placed in the gutter under
    /// the arrow. `min-w-0` so a long line truncates inside the column rather than widening
    /// it.
    let foldContent = "col-start-2 min-w-0"
    /// The wrapper a fold's header wears so a press anywhere on the LINE — not only the
    /// arrow's own square — toggles it (`View.foldClickRow`). `display:contents` is the
    /// whole trick: the wrapper lays out as if it were not there, so the grid still sees
    /// the arrow and the title as its two direct children and `foldRow`'s columns hold —
    /// what the wrapper adds is a single delegated `click`, one listener over both instead
    /// of one on the arrow alone that a title press could never reach.
    let foldClick = "contents"
    /// The cue that the wrapper above is real: without it the row LOOKS like plain text
    /// wearing an arrow, and a pointer never learns the text is part of the same control.
    /// Only on the pieces the wrapper actually holds — never on `foldBody`'s own classes,
    /// which sit outside it and open nothing further by being pressed.
    let foldClickable = "cursor-pointer"
    /// The arrow's cell: one line of the row's own box, whatever the title is set in, and
    /// the AVATAR's width at the gutter's start (`w-5`), so every mark the margin carries
    /// stands on the column the speakers' avatars do — centred in the whole 2rem gutter it
    /// sat six pixels right of them, a second rail beside the one the eye already had. Named
    /// into the gutter, so a row drawn above the title (an act's cause) cannot push it over.
    let fold =
        cls [ "col-start-1 justify-self-start w-5 h-[1lh] flex items-center justify-center cursor-pointer bg-transparent border-0 p-0"
              "text-ink-faint hover:text-ink transition-colors"; focusRing ]
    let foldMark = cls [ "block"; Motion.turn ]
    let foldMarkOpen = cls [ foldMark; Motion.turned ]
    /// What unfolds UNDER the title, in the content column — an act's particulars, a call's
    /// input and output: an aside to the line, indented with it.
    let foldBodyOpen = cls [ "col-start-2"; Motion.unfold; Motion.unfolded ]
    let foldBodyShut = cls [ "col-start-2"; Motion.unfold; Motion.folded ]
    /// What unfolds BESIDE it, across both columns — a run's items, which are rows of their
    /// own and put their arrows on this row's rail rather than one gutter in.
    let foldBodyWideOpen = cls [ "col-span-2"; Motion.unfold; Motion.unfolded ]
    let foldBodyWideShut = cls [ "col-span-2"; Motion.unfold; Motion.folded ]

    /// One item inside a group: its (rare) meta line over its body.
    let messageItem = cls [ itemGround; "max-md:pl-4"; "flex flex-col gap-1" ]
    /// The meta line carries only what is NEWS — streaming, failed, woke unasked. The author
    /// is the group's to say, so a settled message has no meta line at all.
    let messageMeta = "flex items-baseline gap-2.5 pl-8"
    let who = caps + " text-ink-dim"
    let whoAgent = caps + " text-blue"
    /// `pl-8` is the group head's own geometry — 20px avatar + 12px gutter — so bodies, chips
    /// (`chatChip`, already at 32px) and tool runs all share one reading edge under the name.
    // No weight here: these compose with `messageVoice`, which owns the body weight.
    let messageBody = "pl-8 text-body text-ink"
    let messageBodyStreaming = "pl-8 text-body text-ink-dim"

    /// A detached reply's quote of what it answers — one quiet line above the body, on the
    /// same `pl-8` reading edge, dimmed so it reads as context rather than as the message.
    /// The mark is a corner glyph; the quote truncates in one line so a long parent cannot
    /// push the reply itself down the screen.
    let replyRef = "pl-8 flex items-baseline gap-1.5 text-small text-ink-faint"
    let replyRefMark = "shrink-0 text-ink-faint"
    /// `pr-px` beside `truncate`: italic leans its last glyph's ink past its own advance
    /// width, and a box whose `overflow: hidden` edge sits flush on that advance width
    /// slices the overhang off with no ellipsis to show for it — text measured as fitting
    /// exactly, rendered one stroke short ("The session resumed" losing the tail of its
    /// "d"). The truncate/ellipsis path is unaffected: it still fires on real overflow:
    /// this is for the near-exact-fit case where the browser never thought it had any.
    let replyRefQuote = "truncate min-w-0 italic pr-px"
    /// An act's cause, ABOVE its headline: a row of the act's own grid (`contents`), its mark
    /// in the gutter over the fold arrow and its sentence in the content column. The same
    /// quiet voice as a reply's ref, but the sentence holds references (some of them links),
    /// so the jump is the MARK alone — a link cannot sit inside a button.
    let causeRow = "contents"
    /// The marks linking acts wear `edge`, the controls' rim: a line, not text, and OPAQUE —
    /// a chain's pieces meet across two rows, and a translucent line doubles where they touch.
    let causeMark = "col-start-1 justify-self-start w-5 flex justify-center text-edge"
    /// The corner that opens a chain: a line and a gap tall, so the cause's sentence sits clear
    /// of the headline under it and the head stops as far above the chevron as a chain's does.
    let causeCorner = "relative block w-3.5 h-[calc(1lh+0.75rem)]"
    /// Its turn: from the sentence's middle, in to the centre line.
    ///
    /// The centre line is the pixel column 7px into this box (and into the head's box,
    /// which is the same box): a WHOLE number of pixels from the head's left edge, so the
    /// line and the head round to the pixel grid together. A browser snaps a box and an
    /// SVG alike by rounding each one's own edges, and the line used to sit 6.5px in — half
    /// a pixel off the head's grid — so the two rounded apart and the head painted half a
    /// pixel to one side of its stem. The head's tip is drawn at 7.5px (`Icon.chained`),
    /// the middle of that column.
    let causeCornerTurn = "absolute top-2.5 left-[7px] right-0 h-px bg-current"
    /// Its stem: down the centre line from the turn to the head.
    let causeCornerStem = "absolute top-2.5 bottom-1.5 left-[7px] w-px bg-current"
    let causeSaid = "flex items-baseline text-small leading-[inherit] text-ink-faint"
    /// The link of a chain, above the act's chevron: shorter than a line, and reaching up
    /// through the act's top padding to meet the rail the act above draws (`causeRail`). Its
    /// line is a box, as the rail is, with only the head drawn over its foot. The bottom
    /// padding holds the head off the chevron by the gap the corner mark keeps (~12px).
    let causeChainMark = "col-start-1 justify-self-start w-5 -mt-2 h-5.5 pb-1.5 relative flex justify-center text-edge"
    /// The body sits one pixel right of centre (`ml-px`: a 2px margin box, centred), 7px into
    /// the head's box — the corner's centre line (`causeCornerTurn`), on the same grid.
    let causeChainBody = "w-px h-full ml-px bg-current"
    let causeChainHead = "absolute bottom-1.5 left-1/2 -translate-x-1/2"
    /// The line from an act's chevron down to the next link of its chain: the avatar column's
    /// centre (10px — the same line `causeMark`'s and `causeChainMark`'s `w-5` boxes centre on),
    /// through the rows under the headline and on through the gap between the two acts.
    /// Starts the same ~12px under the chevron that the head stops above the next one.
    /// On the same centre line as every head it runs to (`causeCornerTurn`), a whole pixel
    /// column from the gutter's edge so the two round to the pixel grid together.
    let causeRail = "col-start-1 row-span-2 justify-self-start w-px ml-[10px] mt-1.5 -mb-2.5 bg-edge"
    let causeJump =
        cls [ "bg-transparent border-0 p-0 cursor-pointer hover:text-ink focus-visible:text-ink"; focusRing ]
    /// The same quiet line as `replyRef`, but a real control — it jumps to the message it
    /// quotes. Borderless and transparent (it rides above the body, not a box of its own),
    /// brightening under the pointer and wearing the shared focus ring so a keyboard reaches
    /// it. `text-left` because a button centres its text by default and this is a quote.
    let replyRefJump =
        cls [ "pl-8 flex items-baseline gap-1.5 text-small text-ink-faint w-full text-left"
              "bg-transparent border-0 p-0 cursor-pointer hover:text-ink"; focusRing ]

    /// The mark itself, with nothing said about what it MEANS: a 7px bar sitting on the text's
    /// baseline. Two marks wear it, and the ANIMATION is the whole difference between them —
    /// which is the vocabulary this app already had and had stopped spending.
    let private caretBar = "inline-block w-[7px] h-[15px] bg-blue align-[-2px] ml-0.5"

    /// BLINKING: "text goes here". An invitation, and the terminal cursor everybody already
    /// knows. Worn where nothing is happening and something could be.
    let caret = caretBar + " animate-blink motion-reduce:animate-none"

    /// PULSING: "something is happening". ONE name for it, because the client reading what it
    /// kept and the agent writing into a message are the same statement made by the same mark.
    ///
    /// A streaming message wore the BLINKING one, which was survivable while a pulsing dot and
    /// an activity strip carried the claim beside it. Alone it is not: `blink` is `steps(1)`,
    /// fully transparent for half of every second, so a turn that had been accepted and had
    /// said nothing yet showed an empty row and — half the time — nothing whatsoever. `pulse2`
    /// bottoms out at 0.25, so the mark is never gone. Photographed on a phone, twice, before
    /// anybody noticed the picture was of a screen saying nothing.
    let caretWorking = caretBar + " animate-pulse2 motion-reduce:animate-none"

    /// The agent's caret: its own mark, the solid blue diamond its avatar carries (the cube
    /// seen from above that the intro opens on), standing where its next word lands. The
    /// bar above is a person's shape — a turn that is yours to type into — and a message the
    /// agent is writing is not that.
    ///
    /// Placed by the font, not by eye. The box is `1ex` square, so the diamond's height is
    /// the face's x-height, and an empty inline-block rests its bottom edge on the baseline:
    /// the lower point stands on the line the letters stand on and the upper one at the top
    /// of an `x`, in whatever face and size the body is set in. It is a clip of that box
    /// rather than a square turned 45°, because a turn is painted and not laid out — the
    /// turned corners land wherever the square's centre puts them, which is how a mark ends
    /// up a pixel off the baseline it was aligned to.
    ///
    /// Then overshot, as type is: a point antialiases to a thinner tip than a flat meets, so
    /// a diamond ending exactly on the baseline and at the x-height reads short of both —
    /// it did, side by side with `xoxvx` at 1x and 3x. Every point is carried 0.03em past
    /// its line (the box is 0.06em larger and lowered by half of that), which is about
    /// twice what Noto's `o` overshoots by and the usual ratio for a point against a curve.
    let private agentMark =
        "inline-block w-[calc(1ex_+_0.06em)] h-[calc(1ex_+_0.06em)] align-[-0.03em] bg-blue "
        + "[clip-path:polygon(50%_0,100%_50%,50%_100%,0_50%)]"

    /// Writing: held still, a small space after the last word. The words arriving are the
    /// movement; a pulse on top of them said nothing they were not already saying.
    let agentCaret = agentMark + " ml-[0.35em]"

    /// Thinking — a turn accepted and nothing said yet, or what it said gone quiet: the cube
    /// the diamond is the top of, turned in the hand (`.agent-think` in `app/tailwind.css`,
    /// which says how). Its box is the still caret's and every move ends on the still caret's
    /// picture, so thinking and writing differ only while it moves. No margin: alone in its
    /// body it stands on the content column, where the first word will. Without motion it is
    /// dimmed instead, so thinking and writing still differ.
    let agentThinking = "agent-think"
    /// Before the first word: the same cube, larger, standing where the reply will begin — the
    /// turn's opening, said a size up, until the first word's caret takes it to the end of
    /// that word (`Glide`). Drawn larger, not laid out larger (`.agent-think-start`), so the
    /// line it stands on is the line the first word will set and nothing moves when it does.
    let agentThinkingStart = agentThinking + " agent-think-start"
    /// What it turns: the tip and the cube inside it, drawn by `View.thinkingCube`.
    let agentThinkingTip = "agent-think-tip"
    let agentThinkingCube = "agent-think-cube"

    /// How many slots the thinking series has and how long each lasts — `think-tip` in
    /// `app/tailwind.css` is ten moves of two seconds. Kept beside `thinkFrom`, the one place
    /// that steps through them.
    let private thinkSlots, thinkSlotSeconds = 10, 2

    /// Where in the series a thinking mark starts, as the inline style that says so: a slot
    /// picked by its seed, so each turn opens on a move of its own rather than all of them on
    /// the same nod, and one turn keeps its start however often it is drawn. Always a slot's
    /// start, which is the rest picture, so a mark never appears mid-move.
    let thinkFrom (seed: string) : string =
        let hash = seed |> Seq.fold (fun acc c -> (acc * 31 + int c) &&& 0x7fffffff) 7
        sprintf "--think-from:-%ds" (hash % thinkSlots * thinkSlotSeconds)

    /// The empty timeline's own mark: the blinking caret, standing where the first message will
    /// land. Dimmed on top of that, because it is an invitation rather than an event — a
    /// full-strength caret in an empty room reads as something already happening.
    ///
    /// Aligned to the message grid's content column (20px avatar + 12px gutter) so the first
    /// real message appears exactly where the caret was standing, rather than stepping sideways
    /// as it replaces it.
    let timelineIdle = "pl-8 max-md:pl-8"

    // --- The ask card ---------------------------------------------------------------------
    // A band docked above the composer, in the queue's dock: somebody is asking, and here are
    // the ways to answer. It wears the blue LEAD a queued command wears, because it means the
    // same thing — waiting on you — and lifts a tone off the timeline behind it.
    //
    // It reads on the conversation's own line and in the conversation's own voice. The
    // question is set in the app's NAVIGATION voice — the one `session`, `settings` and the
    // wordmark wear — because it is the one thing on this surface a person has to read, and
    // at the body size it was the fourth-loudest thing here, under a caps line that said
    // "session asks" and a question that said the same thing again in more words.
    //
    // Out in the margin, alone, is the tick. That column is where the transcript puts a face
    // and where this puts a mark; everything else — rules, grounds, type — begins at the
    // reading edge, which is what makes the edge visible at all.
    //
    // Nothing here is a box. Rows were bordered rectangles under a bordered search field with
    // a bordered branch field inside the held one: four rectangle vocabularies in eight
    // centimetres, of which this product has exactly one and spends it on the START button.
    // What tells rows apart is a rule. What says one is held is its tick and its GROUND, edge
    // to edge — the queue's rows are bands for the same reason, and a highlight that stopped
    // at a measure would read as a box drawn round the name rather than as the row itself.

    /// The chat and the launch card share ONE column, and the card takes its own room in it:
    /// the conversation's scrollport ends where the card begins, so everything said can be
    /// scrolled into view above it. It used to be an OVERLAY (`absolute bottom-0` over the
    /// chat's lower edge), to keep the chat from reflowing when the card arrived and grew —
    /// and on a session whose first acts were commands, the newest of them sat under the card
    /// for as long as it stood: hidden, unscrollable, and still reached by Tab. A reflow the
    /// pinned conversation rides (`Render.restoreSurfaceScroll` keeps a reader at the end at
    /// the end) is a price; content nobody can reach is a fault.
    let launchArea = "flex-1 min-h-0 flex flex-col"

    /// Docked at the foot of `launchArea`, under the conversation and above the composer, so
    /// START and the composer stay where the hand expects them. A FIXED share of the column
    /// rather than of the screen, because what it is sized against is the conversation's room:
    /// a size in `vh` ignored the header and the composer, and on a phone a full listing left
    /// the chat a strip. Everything inside down to the list is a shrinkable flex column
    /// (`askBody`, `askTrack`, the pane), so the size lands on the LIST, which scrolls — never
    /// on the question or the button. Opaque (`bg-surface`) for the band it draws across the
    /// column.
    ///
    /// FIXED and not a cap (`max-h`): the card claims this room the instant it is offered, so
    /// repositories arriving fill a list that already has its height rather than growing it.
    /// Capped, the card stood only as tall as what was in it — a looking line, then a page of
    /// repositories — and so it LEAPT upward off its pinned foot the moment they loaded, taking
    /// the conversation above it up with it. The cost is a card taller than a short list needs,
    /// with room to spare under it; the list scrolls into that room as it grows, and nothing
    /// moves when it does.
    ///
    /// It RISES in rather than appearing full-grown (`animate-ask-rise`): the card is not in the
    /// first paint — it anchors at connect (`Launch.anchor`) — so without this it popped into the
    /// column in one frame and snapped the conversation up by its height. Growing its height from
    /// nothing raises its top off the composer and the chat gives way over the same beat, so the
    /// arrival reads as motion rather than a jump. `overflow-hidden` so the list inside is clipped
    /// to the growing height instead of spilling over the chat while it rises; the off-stage pane
    /// was already clipped by `askTrack`, and nothing else here reaches the card's edge.
    let ask =
        cls [ "relative shrink-0 h-[60%] flex flex-col pt-6 pb-6 bg-surface overflow-hidden"
              "animate-ask-rise motion-reduce:animate-none"; Stroke.dividerTop ]

    /// The blue lead, DRAWN rather than bordered — and it has to be, because a `border-l-2`
    /// sits inside the band's padding box and would push every line in the card two pixels
    /// off the reading edge the card exists to meet. Same device and the same reason as
    /// `bandRail`. It spans the band rather than the scrollport, which is why the card's
    /// scroll is `askBody`'s: on the band it would carry the lead up out of sight with it.
    /// `z-10` because the rows beneath it run edge to edge: a ground that reached the card's
    /// left edge painted over the lead, so the one mark that says this card is waiting on you
    /// was broken into dashes by whichever rows happened to be lit.
    let askLeadBar = "absolute inset-y-0 left-0 w-0.5 bg-blue z-10"

    /// The reading inset, spent by each BLOCK rather than once by the band. 64px is the
    /// timeline's `px-8` plus the avatar gutter `messageBody` carries, and 48 is that same sum
    /// on a phone — so a line here starts where the header title and every message body start.
    ///
    /// Why not simply pad the band: a row's ground has to run edge to edge, and a band that
    /// spent the padding would stop it short of both edges. Carried as padding INSIDE each
    /// full-width block, the ground reaches the screen and the name still lands on the line.
    let private askInset = "pl-16 pr-8 max-md:pl-12 max-md:pr-4"

    /// The measure, INSIDE a block that runs edge to edge. A row's ground and its rule are the
    /// band's full width, because a highlight that stopped at a measure reads as a box drawn
    /// round the name rather than as the row; what is ON them reads at the timeline's own
    /// measure, because a name and the description of it a thousand pixels apart are not a row,
    /// and a question set across a desktop column is a line the eye loses its place returning
    /// from. Both halves of that are what `readingColumn` already means.
    let private askMeasure = cls [ "w-full min-w-0"; readingColumn ]

    /// The card's own scroll, rather than the band's — see `askLeadBar`. No `gap`: the spacing
    /// down this column is a RAMP, not a rhythm (the question stands alone at the top, the
    /// ways to answer stand apart from it, and the button apart from all of it), so each block
    /// states its own.
    let askBody = "flex-1 min-h-0 flex flex-col overflow-x-hidden"

    /// Two panes, one box. The card asks one thing at a time and the second thing is to the
    /// RIGHT of the first, because that is where a thing you went INTO is — the Zune move,
    /// and the reason the way back is a chevron pointing the way the surface will go.
    ///
    /// The pane on screen is IN FLOW and fills the card (`askPaneHere`'s `flex-1`); the other
    /// sits absolute in the same box (`inset-0`), pushed a full width aside — there to slide in
    /// at the height it will land at, and contributing no height while it is not. The card's own
    /// height is fixed (`ask`), so a short pane no longer shrinks it — the list owns the spare
    /// room and scrolls in it.
    ///
    /// The track GROWS to fill the card and shrinks within it, and so does the pane in it, down
    /// to the list: the card's fixed height (`ask`) has to land on the LIST, which fills the
    /// room and scrolls. `flex-1` is the growing half — without it the track is only as tall as
    /// what is in it and the fixed card has a void under START, which floats up mid-card instead
    /// of sitting at the foot. `min-h-0` is the shrinking half — a track that could not shrink
    /// (`shrink-0`, which it once was, under a pane capped by its own `max-h`) would be cut by
    /// its `overflow-hidden` instead, the rows below the cut no longer existing to be scrolled
    /// to and the foot that pages never reached. The clip is for the pane that is off to the
    /// side, and only that.
    let askTrack = "relative flex-1 min-h-0 flex flex-col overflow-hidden"
    /// A pane is a COLUMN with two parts: what it asks, and what there is to answer with.
    /// Only the list scrolls - the question stays legible while a long one is read. START is
    /// not a third part of the pane: it is the one thing both panes mean the same way, so it
    /// sits below the TRACK rather than inside whichever pane is showing. A button that rode
    /// the pane would slide off with the one you just left and a second copy would slide in
    /// with the one you land on - two buttons where there is one, the same seam `Launch.anchor`
    /// closed for the card itself.
    ///
    /// No cap of its own: the card's is the one, and a second in different units would be a
    /// second answer to how tall the card may be.
    let private askPaneBase =
        cls [ "min-w-0 min-h-0 flex flex-col transition-transform"; Motion.paceLong ]
    /// The list, and the only thing in a pane that scrolls. `min-h-0` is what lets it: a flex
    /// child's floor is its content, so without it the column grows past the card's cap and
    /// the rows are cut rather than scrolled.
    let askScroll = "flex-1 min-h-0 overflow-y-auto"
    /// On screen. `min-w-0` so a long repo name in the subtitle truncates rather than widening
    /// the pane.
    /// `flex-1` so the in-flow pane fills the track, which fills the fixed card — the way the
    /// off-stage panes fill it through `inset-0`. The list inside (`askScroll`) then has the
    /// card's room to scroll in whether it holds a looking line or a page of repositories, so
    /// neither moves START off the foot.
    let askPaneHere = askPaneBase + " translate-x-0 flex-1"
    /// Off stage, over the track's own box (`inset-0`), so a pane slides in at the height it
    /// will have when it lands, its list already scrolling inside it.
    let askPaneLeft = askPaneBase + " absolute inset-0 -translate-x-full"
    let askPaneRight = askPaneBase + " absolute inset-0 translate-x-full"

    /// The card's own edge margin — what a block spends when what is in it is not a line of
    /// reading. `askInset` is the rail, and the rail is for WORDS; a block that has no word to
    /// land on it has nothing to buy with those 64 pixels and only ends up shoved off centre.
    let private askEdge = "px-8 max-md:px-4"

    /// The question and the way out, on one line — there is no author line above it any more.
    /// A caps `session asks` over `Which repository is this session for?` spent two lines
    /// saying one thing, and what the card is asking is legible from the question alone.
    /// The head's own inset is the BAND's, not the reading one: its way out sits in the
    /// gutter, which is the column the ticks are in, so the head starts where they do and the
    /// words after it land on the reading edge by the gutter's own width.
    let askHead = cls [ askEdge; "pb-1" ]
    let askHeadLine = cls [ askMeasure; "flex items-start gap-3" ]
    /// The subtitle over the title. Both panes fill this slot — `session asks` over `which
    /// repository?`, the repo's name over `which branch?` — so the title sits at the same
    /// height on both and only the line above it changes.
    let askHeadWords = "flex flex-col min-w-0"
    let askSubtitle = "font-light text-small leading-4 text-ink-faint truncate"
    /// The branch pane's subtitle: the repo it is a pane OF, in the terminal face because it
    /// is an identifier, on the same line box so the title under it does not move.
    let askSubject = cls [ askSubtitle; "font-terminal" ]

    /// The way out, in the gutter. The mark column is the NAVIGATION column here: × leaves the
    /// card, ‹ leaves the pane, and a tick marks a row — one slot, one meaning, which is what
    /// lets the two panes share a shape.
    ///
    /// ONE face for both, and grey: blue is what this card spends on the thing you are being
    /// asked to do, and leaving is not it. A blue back chevron read as the answer.
    ///
    /// On a phone the BOX is 44px, the target this product holds a thumb to (UI baseline), and
    /// the negative margins pay the growth back to the head, so the glyph and the words beside
    /// it stand where they do on a desktop. It was a 20px box, the one way out of a card that
    /// stood over half the screen.
    let askWay =
        cls [ "w-5 h-5 shrink-0 mt-1 grid place-items-center bg-transparent border-0 cursor-pointer transition-colors"
              "phone:w-11 phone:h-11 phone:-mx-3 phone:-mt-2 phone:-mb-3"
              "text-ink-faint hover:text-ink"; focusRing ]

    /// The navigation voice: the wordmark's family, lowercased, at the heading step. Not
    /// `heading`, which truncates — a question is allowed to take the second line it needs.
    let askQuestion = "font-extralight text-heading tracking-[-0.01em] lowercase text-ink min-w-0"

    /// The way in that is not the list — a search, or a link pasted. A step quieter than the
    /// question and flush with the table under it, whose first rule is this field's own
    /// underline: in a box it read as a second question with a line drawn under it, and its
    /// text began inside the box rather than on the edge everything else starts on. The focus
    /// signal is the field face's — the rule goes blue.
    let askSearch =
        cls [ askInset; "w-full h-12 mt-6 bg-transparent outline-none appearance-none"; fieldType
              Stroke.underline; Stroke.hair; Stroke.hoverRim; Stroke.focus ]

    /// A line the card says rather than one it offers — looking, cloning, a refusal, a clone
    /// that failed. Where the first row would have been, so an answer and the absence of one
    /// stand in the same place.
    let askNote = cls [ askInset; "pt-4" ]
    let askNoteLine = askMeasure

    /// A press that is not the commit: more of the list, a way back, a way out. BLUE, because
    /// it is a link and the things beside it are labels — in the label's own faint ink the two
    /// read as one grey line of which only half could be pressed.
    let askLink =
        cls [ caps; "text-blue hover:text-blue-up-1 transition-colors"
              "inline-flex items-center gap-1 bg-transparent border-0 cursor-pointer"; focusRing ]

    let askRows = "flex flex-col divide-y divide-hair"
    let askRow = cls [ "flex flex-col transition-colors"; rowLift ]
    let askRowHeld = "flex flex-col transition-colors bg-surface-2"
    /// The button is the whole row, so the whole row is the hit target; the LINE inside it is
    /// where the measure applies.
    let askRowButton =
        cls [ askInset; "w-full text-left flex h-12 bg-transparent border-0 cursor-pointer min-w-0"
              "disabled:cursor-default"; focusRing ]
    let askRowLine = cls [ askMeasure; "flex items-center gap-3" ]
    /// The repo's name at the READING size, in the terminal face. It is the content of this
    /// surface, not chrome on it, and at the mono ramp's 12px — the step for output and ids —
    /// four of them read as a log rather than as the four things you are choosing between.
    let askRowName = "font-terminal text-body text-ink truncate min-w-0"
    /// The tick, out in the MARGIN where the column's marks go: `-ml-8` against the button's
    /// own `gap-3` is the 20px avatar and the 12px gutter `messageGroupHead` spends, so the
    /// mark lands on the avatar's column and the name on the reading edge. Drawn on every row
    /// and filled only on the held one — a cell that appeared with the tick would move every
    /// name out from under the pointer that chose it.
    let askRowMark = "-ml-8 w-5 shrink-0 self-center text-blue"
    let askRowDescription = cls [ small; "truncate min-w-0 ml-auto max-w-[45%]" ]
    /// The branch a held row will launch on, at its trailing edge, and the way into the pane
    /// that changes it. Blue because it is a link; the chevron because it goes somewhere, and
    /// that somewhere is to the right.
    let askRowBranch =
        cls [ caps; "ml-auto shrink-0 inline-flex items-center gap-1.5 bg-transparent border-0 cursor-pointer"
              "text-blue hover:text-blue-up-1 transition-colors"; focusRing ]
    let askRowBranchName = "font-terminal text-small normal-case tracking-normal"
    /// What a branch row says about itself beside its name — the provider's default, or that
    /// this one does not exist yet.
    let askRowNote = cls [ label; "ml-auto shrink-0" ]
    let askRowNoteNew = cls [ caps; "ml-auto shrink-0 text-blue" ]

    /// The foot of the list: where the next page is reached rather than pressed for. A row's
    /// height, because that is what it stands in for — the rows still to come.
    let askFoot = cls [ askInset; "h-12 flex items-center" ]
    let askFootLine = cls [ askMeasure; "flex items-center gap-3" ]
    /// On the card's edge margin rather than the reading rail. START is a control, not a line —
    /// it has a rim, and a rim held 48px off one edge and 16 off the other is not a margin, it
    /// is a slab pushed sideways. Full width on a phone, that asymmetry is the whole button.
    let askActions = cls [ askEdge; "shrink-0 flex flex-wrap items-center gap-4 pt-6" ]
    /// Full width on a phone, where a thumb is the pointer and the band is the screen; on a
    /// desktop it takes the room its word needs.
    let askStartWidth = "w-full md:w-auto"
    /// The commit button, which is disabled until something is held — and looks it: the
    /// rim recedes to a hairline and the type to faint, so the press state is one a held row
    /// visibly buys. On `btnPrimarySwap` rather than `btnPrimary`: pressing it holds through
    /// `Sent`/`Cloning` (`Launch.LaunchStage`), and "Start"/"starting…" share its box the same
    /// way Create's two words do, so admission does not resize the one control the card asks
    /// a thumb to find twice.
    let askStart = cls [ btnPrimarySwap; askStartWidth; "h-12 disabled:border-hair disabled:text-ink-faint disabled:cursor-default" ]
    let caretIdle = caret + " opacity-50"


    /// Present to a screen reader, absent to everyone else. For a state whose whole expression
    /// is a moving mark: the pulse says "reading" to people who can see it, and this says the
    /// same thing to the people who cannot.
    let srOnly = "sr-only"

    /// Terminal work in the chat (Plan 14, stage 1): one line, subtler than a message, and a
    /// real `<button>` — so it is keyboard-operable and focus-ringed by construction rather
    /// than by a handler bolted onto a div.
    ///
    /// It sits on the message grid's CONTENT column (`20px` avatar + `12px` gutter) so a run
    /// of chips between two messages lines up with the prose rather than drifting under the
    /// avatars. The palette stays `text-ink-dim`/`text-ink-faint` — chips are the busiest
    /// thing the chat will carry, and they must read as texture next to what people said.
    let private chatChipEnding (rightEdge: string) =
        cls [ "w-full bg-transparent cursor-pointer text-left"
              readingColumn
              "flex items-baseline gap-2 pl-[32px] py-0.5 phone:py-2.5"
              rightEdge
              "text-ink-dim hover:text-ink transition-colors duration-150 ease-out"
              focusRing ]
    /// The same right edge a tool run's line ends on (`itemGround`: `pr-8` less the phone's
    /// 16px bleed), so a chip's status and a call's stand in one column.
    let chatChip = chatChipEnding "pr-8 max-md:pr-4"
    /// A chip inside a task card, whose ground has already drawn that right edge: padding it
    /// again would stop the chip's status 16px (phone) or 32px short of every other row's.
    let chatChipCarded = chatChipEnding ""
    /// Who ran it — the same caps voice a message's author line wears, one step fainter.
    let chatChipWho = caps + " text-ink-faint shrink-0"
    /// Which terminal a queued command waits in. The same voice as `chatChipWho`, but it
    /// TRUNCATES, and is held to half the row: an agent's terminal is titled `[sandbox]
    /// reason…`, sixty characters of caps, and a name that will not shrink is wider than a
    /// phone's column on its own — it took the whole conversation sideways with it. Capped
    /// rather than merely shrinkable because the command beside it has a zero basis, so an
    /// uncapped name would squeeze it to nothing before giving up a character of its own.
    let chatChipSubject = caps + " text-ink-faint truncate min-w-0 max-w-[50%]"
    /// The command itself: mono, truncated to one line. A chip that wrapped to three would
    /// stop being a chip.
    let chatChipCommand = "font-terminal text-code-sm text-ink-dim truncate min-w-0 flex-1"
    /// A stretch item's sentence — prose, not mono: nothing was typed that we recorded.
    let chatChipText = "font-light text-small text-ink-dim truncate min-w-0 flex-1"

    /// Where a turn stopped, and why: a SIGNPOST, not a message. It stands on the chip
    /// column — the same rail the `$` chips and the tool runs stand on — with the mark in
    /// the slot the prompt glyph takes, because it is one more line in the record of what
    /// the turn did, and the last. Drawn as prose it was read as the agent's closing
    /// sentence; the mark is what says "this is the machine's account".
    let turnStop = cls [ "w-full"; readingColumn; "flex items-baseline gap-2 pl-[32px] py-0.5" ]
    /// The mark: a stop. Colour is what carries the state in this set — the error colour a
    /// failed chip's status wears for a turn the process could not carry on, the faint ink
    /// for one a person stopped — and the screen-reader word beside it carries "stopped"
    /// for anything that cannot see colour.
    let turnStopMarkFailed = "shrink-0 text-err"
    let turnStopMarkInterrupted = "shrink-0 text-ink-faint"
    /// The reason, in the quiet small voice — and WRAPPING, unlike a chip's one line: a
    /// reason is the one thing here a reader must not lose the end of.
    let turnStopText = "font-light text-small text-ink-dim min-w-0"

    /// A turn's tool calls (Plan 16): the same ground, column and gutter an act note has
    /// (`actNote`), because it IS the same thing on the timeline — one line with a fold on
    /// the gutter — and the two used to sit on different rails, the run a gutter further in
    /// than the act above it. `itemGround` gives it the act's rhythm and the phone's full
    /// bleed; the gutter is where `fold` puts the arrow.
    let chatToolRun = cls [ itemGround; readingColumn; foldRow; "max-md:pl-4" ]
    /// "used n tools" — the chip voice, on its line; also the "used" before a lone call's
    /// name. On its OWN step's line box, not the row's: Noto Sans's tall ascender on the
    /// row's 20px box sat its x-height 2px below the arrow's centre, while the task card's
    /// "ran n commands" (`chatChipText`, the same step) sat on it.
    let chatToolRunText = "font-light text-small text-ink-dim truncate min-w-0"
    /// The calls, unfolding beneath the run's line. The body spans both columns
    /// (`foldBodyWide*`), so each call lays its own gutter and its arrow lands on the run's
    /// rail — no stepping back out of anything.
    let chatToolRunInner = cls [ Motion.unfoldInner; "flex flex-col pt-1" ]

    /// One call inside an unfolded run: a fold row of its own, on the SAME rail as the run
    /// — it is a row, so its arrow lands where every other arrow does. Its input and output
    /// are a tap away rather than crammed onto the line: the line is the tool and how it
    /// went, and everything else is under it.
    let chatToolItem = cls [ foldRow; "py-0.5" ]
    /// The call's line: tool, then outcome. Everything on it sits on the BASELINE, so a prose
    /// "used", a mono name and the outcome — three steps with three line-heights — read as
    /// one line rather than each centring in its own box. The line is ONE of the row's
    /// (`h-[1lh]`), which is what keeps it a line: a baseline-aligned ICON hangs below the
    /// text's baseline and would otherwise grow the box, taking the arrow measured against
    /// this line off it.
    let chatToolCall = "flex items-baseline gap-2 h-[1lh] text-ink-dim"
    /// The outcome at the end of the call's line, on the baseline with the words — where the
    /// task card's counts stand (`chatTaskCounts`, the same shape); a centred box put this
    /// one a pixel below them. A FLEX box, so the mark brings its own line and not this
    /// one's inherited strut, which would sink the whole line's baseline.
    let chatToolStatus = "flex items-baseline shrink-0"
    /// `namespace/name` — mono, because it is an identifier and reads as one.
    /// `namespace/name` — mono, because it is an identifier and reads as one, and on the
    /// ROW's line box (`leading-[inherit]`) rather than its own step's: the row states the
    /// line once and everything on it agrees, which is what puts the arrow on the line
    /// without anybody subtracting pixels.
    let chatToolName = "font-terminal text-code-sm leading-[inherit] text-ink-dim truncate min-w-0 flex-1"

    /// What the call was given and what it answered, unfolding under its line: two labelled
    /// blocks in the same shape, because they are the same kind of thing — text that crossed
    /// the tool boundary, one way and then the other.
    let chatToolIo = cls [ Motion.unfoldInner; "flex flex-col gap-1.5 pt-1 pb-1.5" ]
    /// "input" / "output" — the same faint label the rest of these chips use.
    let chatToolIoLabel = label
    /// The text itself: mono, wrapped, dim, and bounded — a preview that scrolls rather than
    /// a pane that grows. The `resultCap` upstream keeps an answer small; this keeps a small
    /// text from still being a wall.
    let chatToolIoBody =
        cls [ monoOut; "mt-0.5 max-h-64 overflow-auto bg-surface-2 p-2 rounded" ]

    /// One agent burst (Plan 20, stage 4). The same fold every other row on the timeline
    /// wears (`View.foldArrow`/`foldBody`), not a `<details>` of its own: a native
    /// disclosure keeps its open state on the DOM node rather than the model, so nothing
    /// that drives a re-render — `Tail`'s "jump to latest" among them — has anything to read it
    /// from. Same ground and rail as a turn's tool run (`chatToolRun`), because it IS the
    /// same thing: a line that reads as one until somebody wants the several it groups.
    let chatTaskCard = cls [ itemGround; readingColumn; foldRow; "max-md:pl-4" ]
    /// The summary line, in the fold's content column. No cursor or focus styling of its
    /// own — the arrow beside it is the control now, same as `chatToolRunText`.
    let chatTaskSummary = cls [ foldContent; "flex items-baseline gap-2 text-ink-dim" ]
    /// The counts, at the end of the summary line. Baseline-aligned with the sentence beside
    /// them so the glyphs sit on the text's line rather than floating above it.
    let chatTaskCounts = "flex items-baseline gap-2 shrink-0"
    /// The lines, unfolding beneath — spanning both columns like a run's items
    /// (`chatToolRunInner`), because each line is a row of its own with its own gutter.
    let chatTaskCardInner = cls [ Motion.unfoldInner; "flex flex-col pt-1" ]

    // --- Chapters: where the session divides ------------------------------------------

    /// The rule a chapter draws across the transcript.
    ///
    /// This replaced a RAIL: hairlines in the timeline's left padding, each placed by
    /// measuring the laid-out page on every scroll frame and eased once its message passed
    /// the top of the screen. A mark that has to be positioned against moving text is a mark
    /// that is wrong for part of every scroll, and none of the arithmetic could carry a word.
    /// A rule is in the flow, so there is nothing left to place and the name is simply there.
    ///
    /// A top BORDER rather than a pair of hairlines around the name, because what a chapter
    /// divides is the COLUMN: the line runs the measure and the name hangs under it, which is
    /// how a document says "new section" and how an eye skimming finds one.
    ///
    /// The space above is larger than the space below — the timeline's own `gap-6`, plus this
    /// `mt-6`, against `-mb-1` — because the rule belongs to what FOLLOWS it. Spaced evenly, a
    /// divider reads as sitting between two messages without saying which one it opens.
    ///
    /// `readingColumn`, the same measure a message group wears, because what it divides is
    /// that column: a rule running the whole scroller while the words stop at 38rem reads as
    /// a line drawn on the page rather than a break in the conversation.
    /// `relative` because a collaborator's caret in the name is placed against this box: the
    /// marker is positioned from the input's own offsets, and those are its offset parent's.
    ///
    /// A fold's grid (`foldRow`): the 2rem gutter, then the content column. A flex row with a
    /// gap put the dot at the column's left edge and the name wherever the dot's width and
    /// the gap happened to land it — eight pixels short of the edge every body and fold
    /// title starts on, so a chapter's words were the one line in the column that did not
    /// line up with the words under it.
    let chapterRule =
        cls [ "relative grid grid-cols-[2rem_1fr] items-center border-t border-hair pt-3 mt-6 -mb-1 max-md:mt-4"
              readingColumn; "max-md:max-w-none" ]

    /// The mark on it, in the gutter where a fold's chevron sits and centred the way the
    /// chevron is (`fold`: on the avatar column, 10px in), so the marks the margin carries
    /// stand on one rail. `ml-[7px]` puts a 6px dot's centre there.
    /// Decorative — the name beside it is what says which chapter this is.
    let chapterDot = "col-start-1 justify-self-start ml-[7px] w-1.5 h-1.5 rounded-full bg-ink-faint"

    /// A stretch in which nothing was running, drawn as a break in the page rather than as
    /// something somebody said — because nobody did. Where a chapter hangs its name UNDER a
    /// full-measure line (the line belongs to what follows), a break puts its words IN the
    /// line: what it divides is not two sections but two times, and the reader needs to see
    /// the seam, not a heading.
    ///
    /// `readingColumn`, the same measure a chapter rule and a message group wear, so the break
    /// stops where the words stop; a line running the whole scroller reads as drawn on the page
    /// rather than as a gap in the conversation. Symmetric margins, unlike `chapterRule`'s: a
    /// break belongs to neither side, which is the one thing it is saying.
    let sessionBreak =
        cls [ "flex items-center gap-3 my-6 max-md:my-4"; readingColumn; "max-md:max-w-none" ]

    /// The line either side. `bg-edge` and a one-pixel box, which is exactly what a causal
    /// link's rail is (`causeRail`) — the marks that join things in this timeline are all one
    /// weight, and a break is another of them. Opaque for the same reason: a translucent
    /// hairline doubles wherever two of them meet.
    let sessionBreakLine = "flex-1 h-px bg-edge"

    /// Its words, and a real control: pressing them swaps how long ago for when. Borderless and
    /// transparent — it rides in the line rather than sitting in a box of its own — brightening
    /// under the pointer, and carrying the shared focus ring so a keyboard reaches it.
    /// `tabular-nums` so the label does not jitter its neighbours as the numbers change width.
    ///
    /// The chapter name's voice, not `statusFaint`'s caps: this is a sibling of `chapterRule`,
    /// the other thing that divides this column, and the two should read alike. Caps is for a
    /// status word — seen on the page it made a quiet break shout, and spread the moment
    /// ("2026-09-26 22:26 UTC") across 148px of letter-spacing to say one date.
    let sessionBreakLabel =
        cls [ "font-ui font-light text-small text-ink-faint shrink-0"
              "bg-transparent border-0 px-1.5 py-0 cursor-pointer tabular-nums"
              "hover:text-ink focus-visible:text-ink transition-colors"; focusRing ]

    /// The name, worn by a text input for the reason the session title is: it is editable
    /// text, and a control that only becomes editable once you have pressed it is a control
    /// nobody presses. The same arrangement as `titleInput` — transparent at rest, the
    /// surface lifting under the pointer and while focused, so the line becomes a box you are
    /// typing in exactly when you are typing in it.
    ///
    /// `truncate` rather than wrap: a rule is one line, and a name long enough to wrap has
    /// stopped being a name. `touchType` because a keyboard is coming, and a phone zooms into
    /// anything under 16px it focuses and never zooms back out.
    ///
    /// The placeholder of a chapter nobody has named is invisible at rest — the divider is a
    /// clean line until somebody reaches for it — and `ink-faint` under the pointer or the
    /// keyboard's focus, which is when an invitation to name it is wanted. Ink and not
    /// opacity, so the contrast floor holds on the lifted surface it appears over.
    let chapterName =
        // `-ml-1.5` takes the padding back out on the left, so the WORDS start on the content
        // column's edge and only the lifted surface reaches into the gutter.
        cls [ "col-start-2 -ml-1.5 min-w-0 bg-transparent border-0 px-1.5 py-0.5"
              "hover:bg-surface-2 focus:bg-surface-2 transition-colors"; focusRing
              "font-ui font-light text-small text-ink-dim focus:text-ink truncate"
              "placeholder:text-transparent hover:placeholder:text-ink-faint focus:placeholder:text-ink-faint"
              touchType ]

    /// What a person can DO to one item, behind an ellipsis at its top-right.
    ///
    /// TOP rather than bottom, which is the one place on a message that does not move: an
    /// agent's message streams, so a control anchored to its foot slides down the screen for
    /// as long as the answer is arriving — away from the pointer reaching for it.
    ///
    /// FAINT at rest, and on every device: this is also where one message ends and the next
    /// begins. A run of sends from one person sits under one author line with nothing between
    /// the bodies but the gap, so "ok" after a paragraph read as that paragraph's last line.
    /// A rule or a wider gap would say so too, and would cost every run of short messages its
    /// density — people send a lot of them. The handle was already there, one per item and
    /// level with its first line; it only had to stop being invisible to mark where each one
    /// starts. It was `opacity-0` until the pointer or the keyboard reached the item, which
    /// left that boundary unmarked for anyone just reading.
    ///
    /// Quiet enough that a column of them does not become the loudest thing in the
    /// conversation, and full strength under the pointer, on focus, and while open — the
    /// item is `group/item`, so hovering anywhere on the message lifts it, not hovering the
    /// 24px it occupies. `opacity`, never `hidden`, so it never leaves the tab order.
    ///
    /// One value for a device that hovers and one that cannot, where there used to be a
    /// `[@media(hover:none)]` case beside an invisible rest: on a phone this is the ONLY way
    /// to the menu. A hold on the message used to open it as well, and that gesture is gone:
    /// every platform binds a long press on text to selecting that text, so the two were one
    /// finger meaning two things, and the reader lost the half only the platform can give.
    /// This being permanently visible is what made the gesture affordable to drop.
    /// `top-2 right-1` and not the corner it used to sit in: `top-2` is the ground's own top
    /// padding, so a 24px control there is centred on the 24px first line — the dots read as
    /// belonging to that line rather than floating above it.
    let itemActions =
        cls [ "absolute right-1 top-2 w-6 h-6 flex items-center justify-center"
              "bg-transparent cursor-pointer text-ink-faint hover:text-ink"
              "opacity-60 group-hover/item:opacity-100 focus-visible:opacity-100"
              "transition-opacity duration-150 ease-out"
              focusRing ]
    /// While its menu is open the control is at full strength: a menu hanging off something
    /// faint reads as a menu hanging off nothing.
    let itemActionsOpen = "opacity-100"

    /// The menu itself, hung under the control. `panel` rather than the page's ground,
    /// because it is a surface OVER the conversation and has to be told from it.
    let itemMenu =
        cls [ "absolute right-0 top-7 z-30 min-w-[12rem] py-1"
              // Chrome, not content: a menu is a list of things to press, and dragging across
              // one should never start selecting its labels.
              "select-none"
              "bg-panel"
              Stroke.ring
              Stroke.hair ]
    /// One entry. Full width so the whole row is the target, left-aligned so the entries read
    /// as a list rather than as a row of buttons.
    let itemMenuEntry =
        cls [ "w-full text-left px-3 py-1.5 phone:py-3 bg-transparent cursor-pointer"
              "text-small leading-5 text-ink-dim hover:text-ink hover:bg-surface"
              "transition-colors duration-150 ease-out"
              focusRing ]
    /// The rest of a name an @ is completing, greyed in after the caret. `ink-faint`, the
    /// placeholder's ink: words that are offered, not written, and the dimmest the floor
    /// admits on the composer.
    let addressHint = "text-ink-faint cursor-pointer"
    /// What a press ANYWHERE else lands on. A real button rather than a document listener:
    /// the listener would have to be added, removed and reasoned about against a view that
    /// re-renders, while this exists exactly as long as the menu does. Transparent and over
    /// everything below the menu.
    /// `select-none` because it is the size of the screen. A backdrop is chrome and holds no
    /// words, so nothing is lost by making it unselectable — and what is gained is that a
    /// viewport-sized element can never take a selection that was in flight when it mounted.
    ///
    /// It is a `button`, so it wears the ring every button does (the chrome-consistency scan):
    /// `tabindex="-1"` keeps it out of the Tab order and a pointer press does not match
    /// `:focus-visible`, so the ring is a promise this control never has to keep — and the one
    /// it would, if a keyboard ever reached it.
    let itemMenuBackdrop = cls [ "fixed inset-0 z-20 bg-transparent cursor-default select-none"; focusRing ]

    /// The strip's menu of things to open (Plan 20, stage 1). `itemMenu`'s own chrome, because
    /// it is the same KIND of surface — a short set of acts over the page — and two menus in
    /// one product differing in their ring and their ground are two menus a reader has to
    /// learn separately. Hung under its control and wider than that one, because its entries
    /// carry a second line.
    /// Capped, and that is the load-bearing part. `min-w` alone let the box grow to its
    /// widest entry, and an entry carries what a repo's file says its sandbox is FOR —
    /// whole sentences, 170 characters in this repository's own `yession.yaml`. Measured on
    /// a 375px phone: a 962px menu, hung from `right-0`, so it reached 599px off the left of
    /// the screen with its text cut off mid-word. `truncate` on the note could not save it,
    /// because a flex column with no width to fit into has nothing to truncate against.
    ///
    /// 18rem rather than the pane, so the cap holds wherever the menu is: the pane's splitter
    /// stops at 320px, which this clears with its gutters. The viewport term is the floor
    /// under that, for a screen narrower than any phone sold.
    let paneNewMenu =
        cls [ "absolute right-0 top-full mt-1 z-30 py-1 select-none bg-panel"
              "min-w-[14rem]"
              Stroke.ring
              Stroke.hair ]
    /// The same menu hung from the empty pane's button (P1-4), which is centred in the
    /// column rather than at its right edge: so it hangs centred under it, and reads its
    /// entries from the left whatever the empty state's own alignment.
    let paneNewMenuUnder =
        cls [ "absolute left-1/2 -translate-x-1/2 top-full mt-1 z-30 py-1 select-none bg-panel text-left"
              "min-w-[14rem]"
              Stroke.ring
              Stroke.hair ]

    /// An entry carrying a note under its name. The menu's own entry sets the box, the
    /// padding and the states; this only makes it two lines.
    /// `min-w-0`, or the column refuses to shrink below its content and the cap above is a
    /// number the box ignores.
    let menuEntryStacked = cls [ itemMenuEntry; "flex flex-col gap-0.5" ]
    /// Its first line — ink, not the dim a one-line entry wears, because a name no brighter
    /// than its own footnote is not a name.
    ///
    /// NOT truncated, though it is a label and labels usually are: a `SandboxName` is capped
    /// at 40 characters, so the widest a name can make this menu is bounded and small, and
    /// the menu sizing to its longest name is a better answer than a name with its end cut
    /// off. Truncating would need the column to shrink, and a `min-w-0` kept for a case that
    /// cannot arrive is CSS nobody can test.
    let menuEntryName = "text-body text-ink"
    /// Which repo declared this place, and what its file said the place is for.
    ///
    /// WRAPPED and clamped rather than truncated: a description is prose somebody wrote, and
    /// an ellipsis after the first four words of a sentence says less than nothing. Two lines
    /// is what distinguishes one sandbox from another without the menu becoming a document.
    let menuEntryNote = "line-clamp-2 font-light text-small text-ink-faint"

    /// A repo note in the timeline (Plan 14): one quiet act-line, indented past the
    /// avatar gutter so the reading edge lines up with message bodies.
    let actNote = cls [ itemGround; readingColumn; foldRow; "max-md:pl-4" ]
    /// An act in flight is marked in the LEFT gutter rather than trailing the line, so the
    /// running ones read as a column down the edge. Out of the text flow and unclickable;
    /// the reader's cue is the mark, the screen-reader's is the `sr-only` word it wraps.
    ///
    /// The gutter is set as a line of the title's own text — its size, its inherited leading
    /// — so its first line box is the title's first line box, and the mark on it stands on
    /// the title's baseline by the same rule it stands on a message's (`agentMark`), and is
    /// centred across the gutter as text is. It was a flex box centring a dot on the line's
    /// height, which is the middle of the leading and not anywhere the letters are.
    ///
    /// On the avatar column (`w-5`) the fold arrow stands on (`fold`). `pl-px` centres the
    /// mark on the cause's line rather than that column's middle: the line runs in the pixel
    /// column right of that middle (`causeCornerTurn`), half a pixel over.
    let actNoteRunning = cls [ "col-start-1 justify-self-start w-5 pl-px text-small leading-[inherit] text-center pointer-events-none" ]
    /// The mark for the agent's own act: its diamond, turning — the same mark that turns at
    /// the end of a message it has not started writing, because an agent working on a tool
    /// and an agent thinking are the same statement, and where the mark stands says which.
    /// One mark on the screen at a time: a message's caret goes when the message closes,
    /// before a tool runs.
    let actNoteRunningAgent = agentThinking

    /// The mark for an act the agent is not doing — the session bringing a sandbox up at
    /// boot, a repository's file configuring one: a circle, because the diamond is the
    /// agent's and a diamond on the session's work says the agent did it. In the chrome's
    /// dim ink rather than blue, which is the agent's colour as the diamond is its shape.
    ///
    /// Placed by the same rule as the diamond (`agentMark`): an `1ex` box on the baseline,
    /// so it spans the title's lowercase, overshot as a round letter is. Its diameter is
    /// rounded to an odd number of pixels (`mark-round` in `app/tailwind.css`, which says
    /// why) so it paints centred on the one-pixel line of the cause it hangs under, rather
    /// than half a pixel to its side. Its motion is a beat — one dip in scale and a rest —
    /// on the tempo the agent's cube keeps, so the two read as the same kind of statement.
    let actNoteRunningOther =
        "inline-block mark-round rounded-full bg-ink-dim "
        + "animate-beat motion-reduce:animate-none motion-reduce:opacity-60"
    /// Sentence case, deliberately. This wore the caps LABEL voice, and a label voice is for
    /// two or three words: `STARTED SANDBOX WORK (DOCKER), FORWARDING ANTHROPIC_API_KEY FROM
    /// ADA` is a line nobody reads, because uppercase flattens the word shapes a reader scans
    /// by and the tracking stretches one clause across the whole column. A label that has
    /// grown into a sentence is a sentence, and the timeline is prose.
    let actNoteText = "text-small leading-[inherit] text-ink-dim"
    /// Who an act was for (" for Ada"): inline on a wide screen, its own line on a narrow
    /// one, where the whole headline would otherwise wrap mid-clause.
    let actNoteFor = "max-md:block"
    /// The particulars under the headline: the same size, one step fainter, so the pair reads
    /// as one act rather than as two lines about it.
    let actNoteDetail = "text-small leading-[inherit] text-ink-faint"
    /// The particulars that show WITHOUT unfolding, stacked under the title in the content
    /// column. Empty for most acts, and an empty box costs nothing.
    let actNoteShown = "flex flex-col gap-0.5"

    /// A sandbox start's particulars, laid out as labelled fact rows rather than one
    /// sentence (`View.sandboxStartFacts`). The container stacks each fact on its own row at
    /// the same faint voice `actNoteDetail` uses, so the group still reads as one act under
    /// the headline - what changed is that a screen arranges the parts, and labels them,
    /// rather than chaining them into prose.
    /// One fact: its label and its value on a line, wrapping onto the next when the value is
    /// a run of badges too wide for the column. `items-baseline` so a one-word label sits on
    /// the value's first line rather than centred against a stack.
    let actNoteFactRow = "flex flex-wrap items-baseline gap-x-2 gap-y-0.5"
    /// The label side of a fact - one quiet word, a step fainter than its value, so a reader
    /// scans the labels and reads across only the fact they came for. `shrink-0` so the label
    /// keeps its word while the value takes the wrap.
    let actNoteFactKey = cls [ "text-small leading-5 text-ink-faint shrink-0" ]
    /// The value side of a fact - the same faint voice the one-line detail used, free to wrap
    /// and to shrink inside the row rather than push it wide.
    let actNoteFactVal = "text-small leading-5 text-ink-dim min-w-0 break-words"
    /// Several value lines (a realisation is a list) stacked under one label.
    let actNoteFactStack = "flex flex-col gap-0.5 min-w-0"
    /// The description rides behind a native <details>, so it opens on tap with no script.
    let actNoteDisclosure = "min-w-0"
    /// Its summary is the label `for`, tappable. The platform's own disclosure triangle is
    /// left on as the affordance - a mark every reader already knows means "there is more
    /// here" - so no glyph of ours has to stand in for it.
    let actNoteDisclosureKey =
        cls [ actNoteFactKey; "cursor-pointer select-none hover:text-ink-dim" ]
    /// A path inside a fact line - the checkout, when it is worth showing. Mono, because it
    /// is an identifier and reads as one, and dim enough to sit inside the faint line around it.
    let actNotePath = cls [ mono; "text-code-sm text-ink-dim" ]
    /// What an act unfolds: the rows it lays out and — last — what the agent was told, in
    /// the detail voice, so a reference in it is drawn as it is drawn above. The fold
    /// itself, and where its arrow sits, is `fold` above: an act wears the timeline's one
    /// disclosure rather than a disclosure of its own.
    let actNoteFoldInner = cls [ Motion.unfoldInner; "flex flex-col gap-1 pt-1" ]
    /// The sentence the agent was told, QUOTED: typographic marks either side, drawn by the
    /// stylesheet rather than written into the row, so what the element SAYS stays exactly
    /// what the prompt carried (`data-act-said`'s text is `ConversationItem.said`, to the
    /// character, and a test reads it so). The marks are a step fainter and a touch larger
    /// than the words they hold, the way a pull quote's are — they frame, they do not speak.
    let actNoteTold =
        cls [ "before:content-['“'] after:content-['”']"
              "before:text-ink-faint after:text-ink-faint before:text-[1.15em] after:text-[1.15em]"
              "before:mr-px after:ml-px" ]
    /// The row that holds it reads as ONE line of prose — `agent told “…”` — wrapping where
    /// the words do, rather than a key column with the quote parked beneath it.
    let actNoteToldRow = cls [ actNoteFactVal; "text-ink-faint" ]
    /// A line this host could not honour exactly. One step brighter than the other
    /// particulars (`ink-dim`, not `ink-faint`), so the one fact that means "you did not get
    /// quite what you asked for" is the one the eye catches - without the line having to grow
    /// louder than the act it belongs to, which a red or a fill would.
    let actNoteRealisation = "text-small leading-5 text-ink-dim"
    /// An edit's diff behind its disclosure (`View.fileChangeFacts`): mono, each `-`/`+`
    /// line its own row and free to wrap as one, the two signs in the colours every diff
    /// reader already knows — and no louder than that, since it sits under a faint act line.
    let actNoteDiff = cls [ mono; "text-code-sm leading-5 whitespace-pre-wrap break-words flex flex-col mt-0.5 min-w-0" ]
    let actNoteDiffAdd = "text-green"
    let actNoteDiffDel = "text-err"
    /// The closing line when the excerpt was cut ("… 12 more lines").
    let actNoteDiffNote = "text-ink-faint"

    /// History this device does not hold, standing at the top of the timeline where it would
    /// have been. An act note's voice and column, because it is the same kind of line — a
    /// thing that happened to this conversation rather than a thing anyone said.
    let historyGap = actNote
    let historyGapText = actNoteText

    /// Read-only rendered Markdown in the timeline (the mirror of the composer's live
    /// formatting). Preflight strips heading/list defaults, so each rendered element carries
    /// its own utilities — the same "F# composes utilities, no hand CSS" rule as everything
    /// else. Blocks share a tight vertical rhythm; only the first/last drop their outer margin.
    let proseP = "[&:not(:first-child)]:mt-2"
    let proseH1 = "text-pivot leading-7 font-normal text-ink [&:not(:first-child)]:mt-3 mb-1"
    // 17px is deliberately off the ramp: the one intermediate a four-level heading scale
    // needs between pivot (19) and body (15).
    let proseH2 = "text-[17px] leading-6 font-normal text-ink [&:not(:first-child)]:mt-3 mb-1"
    let proseH3 = "text-body font-semibold text-ink [&:not(:first-child)]:mt-2 mb-1"
    let proseH4 = "text-small leading-5 font-semibold uppercase tracking-[0.08em] text-ink-dim [&:not(:first-child)]:mt-2 mb-1"
    let proseUl = "list-disc pl-5 [&:not(:first-child)]:mt-2 marker:text-ink-faint"
    let proseOl = "list-decimal pl-5 [&:not(:first-child)]:mt-2 marker:text-ink-faint"
    let proseLi = "[&:not(:first-child)]:mt-1"
    let proseStrong = "font-semibold text-ink"
    // Inline code keeps the paragraph's line box (no leading of its own), so only the size
    // is set — 13px, the small step, but written bare to leave line-height inherited.
    let proseCode = "font-terminal text-[13px] bg-surface-2 text-ink px-1 py-0.5"
    /// A fenced block WRAPS, as the terminal's own output does — it does not scroll inside
    /// itself. The `overflow-x-auto` it used to carry was the second answer to a question the
    /// column already answers: with the timeline's `break-words` reaching in here, no line can
    /// exceed the block, so the scroll container never had anything to scroll and only hid
    /// which rule was doing the work.
    let prosePre = "font-terminal text-code leading-5 bg-surface-2 text-ink p-3 [&:not(:first-child)]:mt-2 whitespace-pre-wrap"
    let proseQuote = Stroke.lead + " " + Stroke.hair + " pl-3 text-ink-dim [&:not(:first-child)]:mt-2"
    /// The one hyperlink face: prose links and the references that lead somewhere
    /// (`entityLink`) compose the same words, so a link is a link wherever it stands.
    let proseLink = "text-blue underline decoration-1 underline-offset-2 hover:text-blue-up-1"
    let proseHr = "border-0 " + Stroke.dividerTop + " my-3"
    /// A GFM table (`RichText`). The wrapper carries the horizontal scroll — WCAG 1.4.10's own
    /// exemption from reflow is two-dimensional content such as a table, so a wide one scrolls
    /// rather than shrinking its cells past reading — and the `[&:not(:first-child)]:mt-2`
    /// rhythm lives here rather than on `proseTable`, since it is the wrapper that sits among
    /// the other blocks.
    let proseTableWrap = "overflow-x-auto [&:not(:first-child)]:mt-2"
    let proseTable = "w-full border-collapse text-left align-top"
    /// One border rule for header and body cells alike: `border-collapse` merges a header
    /// cell's bottom edge with the hairline under the row below it, so the header never carries
    /// a second, heavier rule of its own. `first:pl-0 last:pr-0` drop the horizontal padding at
    /// the row's own edges — `px-2` is for the gutter BETWEEN columns, and the outermost columns
    /// have no column on that side, only the wrapper's edge, so prose there should sit flush
    /// with the prose above and below it rather than indented in from it.
    let proseTableCell = "px-2 py-1 border-b first:pl-0 last:pr-0 " + Stroke.hair
    let proseTableHeaderCell = cls [ "font-semibold text-ink"; proseTableCell ]
    let proseTableAlignLeft = "text-left"
    let proseTableAlignCenter = "text-center"
    let proseTableAlignRight = "text-right"

    // --- Interrupt: one verb, in the composer's own row -----------------------------------
    // The agent's activity strip used to live here: a 48px band carrying a pulse, the words
    // "agent is responding", the turn's number and a bordered Interrupt. It said what the
    // streaming message's own meta line said one line above it, and what that message's caret
    // said in the same breath — one fact, three animated marks, a twelfth of a phone's screen
    // spent on the third of them. The control it carried was the only part that was its own.
    //
    // That control then spent a revision as a band of ITS OWN above the composer — one verb,
    // nothing beside it, which was the right call next to a strip but still a second place to
    // look beside the row Send already stands on. It lives in that row now (`View.drafts`),
    // beside Send rather than above it: a turn running was never a reason to take Send away
    // (queuing the next message is the one thing there is to do while the agent writes), so
    // the two verbs sharing one row is the row admitting what was already true.

    /// Ink at rest, err under the hand — the face every destructive verb here wears, and worn
    /// for the same reason rather than out of symmetry. Err AT rest is this product's tone for
    /// *something is wrong*, and nothing is: a turn running is the normal case, and a red word
    /// standing beside Send every time the agent speaks would say otherwise within a day.
    ///
    /// A step brighter than the faint verbs that ride a listed row (`btnBare`), though, and
    /// that difference is the same rule read the other way: those are faint because the ROW is
    /// the subject and they are a thing you can do to it. Here the verb IS a subject, same as
    /// Send beside it. `gap-1.5` pairs the stop glyph with the word (`Icon.stop`, the same mark
    /// the timeline's stopped-turn item already wears at `stopSm` size, now costing nothing to
    /// repeat here since the vocabulary existed before this button used it). `disabled:*` is
    /// new too: the turn between a click and the stop actually landing
    /// (`AgentViewState.Interrupting`), where without it the button would sit unchanged —
    /// inert in every way a person can check except that the request, in fact, went.
    /// `pointer-events-none` rather than relying on `hover:` losing a specificity fight, so the
    /// err hover-face this button is built around never has a chance to paint while there is
    /// nothing left to hover for.
    let btnInterrupt =
        cls [ "h-8 px-2 shrink-0 inline-flex items-center gap-1.5 bg-transparent border-0 cursor-pointer font-ui"
              capsLg; "text-ink-dim hover:text-err transition-colors"
              "disabled:text-ink-faint disabled:cursor-default disabled:pointer-events-none"; focusRing ]

    // --- Queue: editable until drained; the head's green count says so ------------------------
    // The queue is the composer's dock, not a list floating over the ground: its rows are
    // BANDS, running edge to edge exactly as the composer band below them does, told apart
    // from the ground by their tone. They used to be lead-edged cards inside a padded strip —
    // a third rectangle vocabulary two centimetres from the band that already had the answer.

    let queue = "shrink-0 flex flex-col gap-0.5 pt-4"

    /// The same band holding nothing. Padding is what a band spends on the rows inside it, and
    /// with no rows the `pt-4` was 16px of ground between whatever sits above it and the
    /// composer — a gap that said something was there. Nothing is there, so it takes no room:
    /// the composer's rail sits directly under whatever is above it.
    let queueEmpty = "shrink-0"
    /// On the composer's own gutter (`draftInput` spends px-4), because the head belongs to
    /// the dock it heads, not to the timeline above it.
    let queueHead = "flex items-baseline gap-3 pb-2 px-4"
    /// Green, AT REST: "still editable" is a fact about the whole queue, said once at its
    /// head — the rows beneath no longer repeat it per entry.
    let queueCount = caps + " text-green"

    /// A full-bleed band on the composer's gutter. The surface tone is what marks it (the
    /// timeline's ground is `bg`, the dock's bands are `surface`), and interaction lifts the
    /// surface exactly as every other actionable row here does.
    let queueItem =
        cls [ "group flex items-center gap-3 h-10 px-4 bg-surface transition-colors"; rowLift ]

    /// `focus-within`, not `focus`: the caret lands in the editor MOUNTED INSIDE this host,
    /// so `focus:` on the host never fires and the brightening never happened.
    let queueInput =
        cls [ "flex-1 min-w-0 self-center h-5"; fieldBare
              messageVoice false
              "text-small leading-5 text-ink-dim focus-within:text-ink"; touchType ]

    let queueTools =
        "flex gap-1 opacity-0 group-hover:opacity-100 group-focus-within:opacity-100 max-md:opacity-100 transition-opacity"

    /// Extra clearance before Delete, on top of the `gap-1` the row already spends between
    /// Move-up and Move-down. Reorder and delete used to sit in one evenly-spaced row of
    /// three — a destructive control 4px from a frequent one, with nothing between them but
    /// the icon each wears. This does not grow the HIT area (that trick, `btnIconBareTouch`,
    /// is for a row whose icon has no neighbour to overlap; delete has one on each side of
    /// it in spirit even though reorder is the only real neighbour) — it grows the DISTANCE
    /// a thumb has to travel wrong to reach it instead.
    let queueDeleteGap = "ml-2"

    // --- Composer: the one gradient in the product lives on its focus edge --------------------
    // A COMPOSER IS A BAND, not a box on a ground. It used to be a bordered box inside a padded
    // section inside a bordered column — a box in a box in a box, of which only the innermost
    // one could be typed in, and each nested rectangle bought nothing but another edge to
    // notice. The band runs the full width of its column and is told apart from what is above
    // it the way any two surfaces here are: a tone, and a rule.

    /// The band a message is written in. Lifts a tone while anything inside it has focus, so
    /// "this is where I am typing" is legible at a glance and not only from the 2px rule.
    ///
    /// The tone, the rule and the transition are the one thing the message composer and the
    /// terminal's command band (`terminalComposer`, below) share, and share by referring to
    /// this rather than by typing the string twice, so neither can quietly drift from the
    /// other the way `bandRail`'s gradient is not allowed to either. The bottom clearance is
    /// NOT in here any more: `composer` and `terminalComposer` now want different amounts of
    /// it, so each states its own, below.
    let private composerBand =
        "group relative shrink-0 flex flex-col bg-surface focus-within:bg-surface-2 transition-colors"

    /// The band is the last thing on screen on a phone, and used to run flush to the bottom
    /// edge, under the thumb about to press it. That called for a `max-md`-only clearance,
    /// but an iPad in portrait (and plenty of other tablets) sits ABOVE the `md` breakpoint
    /// while still being a thumb, not a cursor: the gated version left exactly the touch
    /// screens it was for without the room it was for. So the clearance is unconditional —
    /// a few pixels desktop never asked for, spent everywhere else on every screen a thumb
    /// actually reaches this band on.
    ///
    /// ONE clearance, plain thumb room — the same `pb-4` the terminal's command band spends
    /// for the same reason. It briefly had a second, wider one for when the verbs' row was
    /// showing; the row carries its own `pt-1` and its own height, so the band was paying
    /// twice for one gap, and the wider number only ever arrived while a state elsewhere in
    /// the file happened to agree with this one.
    let composer = composerBand + " pb-4"

    /// The band's top rule, in two parts — because it is doing two jobs and one element could
    /// only ever do one of them.
    ///
    /// The gradient used to be scaled to NOTHING until focus, so an untouched session was a
    /// black column containing a black box: `#111` barely separates from the `#000` canvas, and
    /// the edge — the one thing marking "this is where you type" — appeared only once you had
    /// already found it and clicked. Leaving it up at rest instead would spend the focus
    /// gesture. So: a dim white hairline that is always there (the marker), and the gradient
    /// growing over it on focus (the gesture).
    ///
    /// It is the same pair that used to run down the composer's LEFT edge, turned ninety
    /// degrees to become the band's separator from the column above. Same 2px, same
    /// blue→green — Zune's orange→pink signature, recast — still spent exactly once in the
    /// product. Worn by the message composer and the terminal's command band alike, because
    /// they are the same object on two surfaces.
    let bandRail = "absolute inset-x-0 top-0 h-0.5 bg-ink/15"
    let bandEdge =
        "absolute inset-x-0 top-0 h-0.5 bg-grad scale-x-0 origin-left transition-transform "
        + "duration-300 ease-out group-focus-within:scale-x-100 motion-reduce:transition-none"

    /// A ROW, since the actions moved into it: the box was 56px of which 40 was a strip under
    /// the text holding a 93px bordered rectangle that said Send and then drew an arrow saying
    /// it again. The verb belongs at the trailing edge of the line you just wrote, which is
    /// where it now is — the same shape the terminal's command line takes, so a person moving
    /// between the two columns meets one object twice instead of two conventions.
    ///
    /// Carries no fill and no edge of its own any more: the band it sits in is the surface.
    ///
    /// `max-md:flex-col`, added since: on a phone the row becomes a column, Send and
    /// Discard (`draftCommit`, below) drop under the text instead of squeezing it, and
    /// the text gets the width back. Above `md` the row shape stands as written above.
    let draftBox = "relative flex items-end max-md:flex-col max-md:items-stretch"

    /// Chrome-less by construction (`fieldBare`): the band carries the tone and the rule, and
    /// the focus signal is the gradient growing across it.
    ///
    /// ONE LINE at rest, growing upward on focus. A composer that is 96px of empty box for
    /// most of a session is spending the conversation's height on the promise of a message
    /// rather than on the messages; it takes the room when it is being used and gives it back
    /// when it is not. `max-height` rather than `height` so the growth is animatable and so a
    /// long draft still scrolls inside rather than pushing the timeline off the top.
    ///
    /// A LINE AND A HALF, once there is more than a line. The rest height was exactly the one
    /// line (40px), so a longer draft stopped dead at the padding — a hard horizontal cut
    /// through the second line, which reads as a rendering fault rather than as text
    /// continuing. 52px shows most of the next line and the mask fades it out, so the
    /// collapsed composer says *there is more here* in the only language a collapsed thing
    /// has. It costs nothing when there is not: height is content-driven and `max-height`
    /// only caps it, so a one-line draft still stands at 40.
    ///
    /// The mask's stops are absolute FROM THE TOP (2.5rem → 3.25rem) rather than a percentage
    /// or an offset from the bottom, and that is the whole reason it is safe. A ramp measured
    /// from the bottom edge is a ramp whose position moves with the box: at 40px it would run
    /// up through the first line and fade the descenders of a draft that fits perfectly well.
    /// Anchored at the top it begins exactly where line one's box ends, so line one is never
    /// touched and the ramp is simply off the end of a box that has not grown.
    let draftInput =
        cls [ "block w-full max-h-[3.25rem] overflow-hidden transition-[max-height] duration-200 ease-out"
              "[mask-image:linear-gradient(#000_2.5rem,transparent_3.25rem)]"
              "group-focus-within:max-h-64 group-focus-within:overflow-y-auto"
              // Gone on focus, not merely pushed down: the ramp would otherwise sit over the
              // line being typed the moment a draft scrolls inside.
              "group-focus-within:[mask-image:none] motion-reduce:transition-none"
              fieldBare
              messageVoice false
              // No `placeholder:` variant: this field is a mounted editor, not an `<input>`,
              // so it has no placeholder attribute for that variant to have ever matched. The
              // prompt is drawn on the editor itself — `[data-rich-body]` in `tailwind.css`.
              "text-body text-ink px-4 py-2"; touchType ]

    /// The writing side of the box: whose message it is, then the message. A column only
    /// because someone else's draft says so above the words; your own — the case that is
    /// almost always on screen — is the input and nothing else.
    let draftBody = "flex-1 min-w-0 flex flex-col"

    /// Send sits at the TRAILING edge — where the eye ends the line it just wrote, and where
    /// every send button a person has ever used lives — with whoever is typing beside it.
    ///
    /// The keyboard hint that used to sit on this row is gone from the layout. It was true and
    /// it taught something, but it spent a permanent strip saying what one press teaches; it
    /// survives as `aria-keyshortcuts` on the control it describes, which is where a screen
    /// reader looks for it and where it cannot go stale.
    /// `pr-1` beside the buttons' own `px-3` is the composer's 16px gutter, read from the
    /// other edge: the word ends where `draftInput`'s `px-4` begins, so the line of text and
    /// the verb that sends it are on one rail rather than four pixels apart.
    ///
    /// No `pb`, derived, not eyeballed: the rest-state input is a 24px line inside `py-2`
    /// (40px), so the line's centre sits 20px from the box top; a 40px control bottom-aligned
    /// on the same 40px box centres at 20px too. It used to spend `pb-1` because the controls
    /// were 32px squares and needed 4px under them to reach that centre — the correction went
    /// out with the glyphs (`btnComposerBox`, above, is the line's own height).
    ///
    /// On a phone this row leaves the line entirely, same as before, but no longer owns a
    /// full line of its own: it shares one with the model picker (`draftActionsRow`, below)
    /// rather than stacking under it as a second line — `max-md:w-full max-md:justify-end`
    /// used to push its buttons to the far edge of a line THIS row had entirely to itself;
    /// now the picker is the other half of that line, so sizing to its own content and
    /// letting the row's `justify-between` do the push is what keeps it at the trailing
    /// edge without claiming width the picker needs. It sits BELOW the text (`draftBox`'s
    /// `max-md:flex-col` puts it there in document order). A row that was always there
    /// spent a band of every phone screen on a control a thumb reaches once per message, so
    /// it comes and goes — and WHAT it comes and goes with is the whole of this bug's story.
    ///
    /// It used to be `group-focus-within`, and that could not work. `focus-within` is false
    /// the instant focus leaves the composer, and pressing a button is how focus leaves: on
    /// iOS Safari a `<button>` takes no focus from a tap at all, so the editor's blur lands
    /// FIRST, the row goes `pointer-events-none` under the finger, and the press arrives at
    /// whatever was behind it — the timeline. Measured: with a draft typed and the editor
    /// blurred, `elementFromPoint` at Send's own centre answered the `<article>` behind it.
    /// Send did nothing, the composer collapsed, and that was the whole of what a person saw.
    ///
    /// So the row follows the DRAFT, not the focus: it stands exactly while there is
    /// something for it to do (`draftCommitReady`), which is the rule Send's two faces are
    /// already computed from. Nothing about a press can retract it, because a press cannot
    /// empty the draft before the press lands. An empty composer still gives the room back,
    /// which is what the coming-and-going was for; it simply no longer offers a control with
    /// nothing to act on.
    ///
    /// Interrupt rides this same row now (`View.drafts`), and "something to act on" grew a
    /// second reason to be true: a turn running, which is what Interrupt acts on, not the
    /// draft. `View.drafts` ORs the two in rather than this file computing it, because the
    /// fact "is there a turn running" lives in `model.Agent`, not here.
    ///
    /// GONE means `max-h-0` beside the fade, not the fade alone. `opacity-0` hides a row and
    /// keeps every pixel of its height, so the band under an empty composer carried a 44px
    /// row of invisible buttons plus the clearance meant to sit below them — two thirds of a
    /// collapsed composer, and a gap no markup test can tell from an empty one.
    let private draftCommitBase =
        cls [ "shrink-0 flex items-center gap-1 pr-1"
              "max-md:overflow-hidden"
              "max-md:transition-[max-height,opacity] max-md:duration-150"
              // `max-md:` on the reduced-motion variant too, and not for symmetry: Tailwind
              // orders the stylesheet by variant, so a bare `motion-reduce:transition-none`
              // is EMITTED ABOVE the `max-md:` transition it is meant to cancel and loses to
              // it at exactly the widths that have one.
              "max-md:motion-reduce:transition-none" ]

    /// Nothing to act on. The row takes no height and no presses — `pointer-events-none`
    /// beside the fade, because a control nobody can see is not one a thumb should find.
    let draftCommit =
        cls [ draftCommitBase; "max-md:max-h-0 max-md:opacity-0 max-md:pointer-events-none" ]

    /// There is a draft. `pt-1` arrives with the row rather than sitting under it: `max-h-0`
    /// is a border-box cap and cannot clamp below its own padding, so a `pt-1` left on in the
    /// face above is 4px of band the row still owns while claiming to be gone.
    let draftCommitReady = cls [ draftCommitBase; "max-md:pt-1 max-md:max-h-12" ]

    /// The model picker's own row-mate, standing beside `draftCommit`/`draftCommitReady`
    /// rather than inside either: choosing a model has nothing to do with whether there is
    /// a draft to send or a turn to stop, so it carries none of that pair's `max-md:` gating
    /// — no `max-h-0`, no `opacity-0`, no transition. It was inside `draftCommitBase` for one
    /// revision (riding `View.drafts`' `commitClass`) and that was the bug, twice over: on a
    /// phone with an empty draft the picker was invisible along with Send, so nobody could
    /// choose a model before writing a word; and because the row it rode DOES transition
    /// (`max-md:transition-[max-height,opacity]`), a tap landing while that transition was
    /// still settling opened the platform's OWN popup anchored to a rect that kept moving
    /// under it — a native menu a phone drew in the wrong place for a reason no amount of
    /// styling the menu itself could reach, because the bug was never the popup's.
    /// `shrink-0` matches `draftCommitBase`'s own, so the two sit side by side on desktop
    /// without either claiming space the other needs.
    ///
    /// `md:order-first`: on desktop, `draftBox`'s row (`draftBody`, this, `commitClass`, in
    /// that document order) otherwise lands the picker pressed up against Send at the row's
    /// trailing edge — the two controls that most need telling apart end up the two closest
    /// together. Reordering it to the LEAD edge (the original proposal's own layout: model
    /// beside the future `+`, Send alone at the trailing edge) puts a sentence's worth of
    /// text between them instead. `md:` only: on a phone `draftBox` is already a column
    /// (`max-md:flex-col`), where `order` would lift the picker above the text entirely
    /// rather than sideways past it — a bigger move than asked for, and the one place this
    /// row already read correctly (the picker stands flush to ITS row's own leading edge,
    /// `draftCommitBase`'s `max-md:justify-end` pushing Send to the opposite one below it).
    /// `pl-4` matches `draftInput`'s own left gutter, on EVERY width, not just `md:` — on a
    /// phone `draftBox`'s `max-md:items-stretch` stretches this row to the full width same as
    /// `draftInput` above it, and nothing else gives it a left inset: unlike `draftCommitBase`,
    /// whose gutter comes free from its buttons' own `px-3`, this row is the model control
    /// alone, so the row itself has to carry it. Without it the picker sits flush against the
    /// viewport edge, outside the gutter every other line in the composer keeps.
    let draftLead = "shrink-0 flex items-center pl-4 md:order-first"

    /// The picker (`draftLead`) and Send's row (`draftCommit`/`draftCommitReady`) ride
    /// together as ONE line on a phone, not two: `draftBox`'s `max-md:flex-col` makes every
    /// direct child its own row, and the two used to be siblings there, so "Provider's
    /// default" sat on a line by itself with SEND stacked on a second line under it —
    /// readable, but not the "dock at the bottom" the picker and Send were both asked for.
    /// Wrapping them in one element makes them ONE child of that column, hence one row.
    ///
    /// `md:contents`: above `md`, this wrapper must get entirely out of the way rather than
    /// become a fourth flex item — `draftBox` there is a plain row of `draftBody`, `draftLead`
    /// (reordered to the front by its own `md:order-first`) and the commit row as three
    /// direct siblings, and `draftLead`'s reordering only makes sense among THOSE three. A
    /// wrapper that stayed a real box at `md:` would turn that into `draftBody` beside a
    /// two-item box, and reordering inside the box could no longer put the picker ahead of
    /// `draftBody` — exactly what `draftLead`'s own `md:order-first` is for. `display:
    /// contents` removes the wrapper's own box without touching its children: they rejoin
    /// `draftBox`'s flex context directly, so desktop is unchanged by this wrapper existing.
    ///
    /// `max-md:justify-between` is what used to be `draftCommitBase`'s own `justify-end` on
    /// a line it had to itself: with the picker as the row's other half now, pushing Send to
    /// the trailing edge is this shared row's job, not the commit box's.
    let draftActionsRow = "max-md:flex max-md:items-center max-md:w-full max-md:justify-between md:contents"

    let draftAuthor = "pl-4 pt-2 " + caps + " text-ink-faint truncate"

    // A draft nobody has open here: one line of it, so the composer reads as "what is being
    // written" rather than a stack of boxes. Clicking it opens it (and closes whatever was).
    //
    // Its leading edge is the AUTHOR'S colour (set inline, from `Entity.presenceColour`) — the same
    // move the terminal's peer-draft row makes, and the reason is the same: the row's whole
    // subject is whose words these are, so the edge should say it rather than repeat a
    // generic hover tint.
    let draftSummary =
        cls [ "group w-full items-center gap-3 h-8 pl-4 pr-2 text-left cursor-pointer"
              rowBase; rowLift; focusRing ]

    let draftSummaryName = "shrink-0 " + caps + " text-ink-faint"

    /// The clamped body: the same read-only editor as anywhere else, held to one line. `truncate`
    /// on the host would fight ProseMirror's block children, so the clamp is on its descendants.
    let draftSummaryBody =
        "flex-1 min-w-0 " + messageVoice false + " text-small leading-8 text-ink-dim "
        + "overflow-hidden whitespace-nowrap [&_*]:inline [&_*]:truncate [&_*]:m-0"

    /// Who is in this draft right now: one dot per live caret, coloured by peer (`Entity.presenceColour`).
    let draftEditors = "shrink-0 flex items-center gap-1 pr-1"
    let draftEditorDot = "inline-block w-1.5 h-1.5 rounded-full"

    /// Who has this OPEN right now: one ring per peer, coloured the same way a caret is
    /// (`Entity.presenceColour`) but hollow, because watching and typing are not the same claim. A
    /// filled dot says somebody's cursor is in here; a ring says somebody is looking.
    let paneViewerDot = "inline-block w-1.5 h-1.5 rounded-full border bg-transparent"

    /// Starts your own draft, collapsing whoever's is open — the escape hatch from joining.
    let draftNew =
        "self-end bg-transparent border-0 cursor-pointer px-4 py-2 " + caps
        + " text-ink-faint hover:text-blue transition-colors " + focusRing

    // --- Settings ------------------------------------------------------------------------------
    // Settings is the column's other face, not a drawer over the conversation: you go there and
    // come back, and the thing you were reading never moves. Its open state is one bit on the
    // root <html> element (`settings-open`, drawn from the model's `Column.Face`).

    /// The settings face's header band: the same 88px rhythm as the nav and the main header, so
    /// the three baselines still align when the column changes face.
    let settingsHead = "h-band shrink-0 flex items-end justify-between pb-5"
    let settingsTitle = "font-extralight text-heading tracking-[-0.01em] lowercase text-ink"

    // --- The agent's absence -------------------------------------------------------------------
    // Said ONCE, in the section that lists who is in this session, because that is where a
    // missing member is missing. It used to be said three times over (a sidebar row, a strip
    // above the composer, and the settings copy); repetition made it wallpaper, not a prompt.
    //
    // The absent state is the SAME roster row as the live one — same avatar cell, same
    // right-aligned slot — so connecting an agent flips the bare `connect` verb to "ready" in
    // place; nothing moves and no box appears or collapses. (It was a boxed card, then a row
    // with a full-width button hanging under it; both took the roster's second slot for good.)
    // The block below is the PROMPT shape the other two asks still wear — a session that has
    // ended, and a checkout waiting on approval — where there is a sentence to put under the
    // row and a decision to make.

    let noAgentBlock = "flex flex-col gap-2 phone:gap-0"
    /// The prompt reuses the roster's own grid — a 20px avatar column and the text column,
    /// with the roster's 10px gutter — so the edge centres under the avatar and the text
    /// lands on the text column BY CONSTRUCTION, not by pixel arithmetic.
    let noAgentPrompt = "grid grid-cols-[20px_1fr] gap-x-2.5"
    /// The edge itself: the product's 2px edge width, in the agent's blue, centred in the
    /// avatar column and spanning the prompt's height.
    let noAgentEdge = "w-0.5 justify-self-center bg-blue"
    /// The prompt's text column: the explainer over its one action.
    let noAgentBody = "flex flex-col gap-2 phone:gap-0"
    /// Full-width within the column so it reads as the section's one action, and a thumb's
    /// height on a phone, where the column is a drawer held in one hand.
    let noAgentAction = "w-full phone:h-11"

    // --- Terminals (Plan 13) -------------------------------------------------------------------
    // The conversation column's mirror on the right: a strip of open terminals, the blocks
    // that have run in the selected one, and beneath them the composer — the message
    // composer's sibling, because queueing a command and queueing a message are the same act.
    //
    // It is a COLUMN, not an overlay. The conversation never moves when terminals open, for
    // the same reason settings is the sidebar's other face rather than a drawer over the
    // timeline: reading something and then having it slide out from under you is the thing
    // this shell does not do.

    /// The pane. Mirrors `sidebar`'s geometry (a fixed column on desktop that animates shut,
    /// an off-canvas column on mobile) reflected to the right edge.
    ///
    /// On a phone it takes the WHOLE width (Plan 14, stage 5). The columns collapse to one
    /// and opening a tab switches that column to the pane, keeping the tab strip — so the
    /// chat is a back-swipe away rather than an overlay to dismiss, and desktop and phone
    /// stay the same mental model. The old 92vw left a sliver of chat showing beside it,
    /// which reads as a dialog: something you get rid of rather than somewhere you are.
    /// On desktop the width is a CUSTOM PROPERTY the shell root carries, not the token — the
    /// token is only the first paint's. 420px was picked as "the width the content actually
    /// has" and is 20 columns short of the 80 a terminal prints; rather than guess a better
    /// number for everybody, the split is laid out by the model — a pane nobody sized takes the
    /// room the chat's reading column leaves (`PaneSplit.resolve`) — and is draggable and
    /// remembered (`PaneShell.installPaneResize`). The transition is suppressed while dragging,
    /// or the column chases the pointer a frame late.
    let contentPanel =
        "relative w-term wide:w-[var(--term-w,var(--spacing-term))] shrink-0 bg-panel h-full overflow-hidden z-40 flex flex-col "
        + Stroke.dividerLeft + " "
        + "wide:transition-[width] wide:duration-200 wide:ease-out wide:[.term-resizing_&]:transition-none "
        + "wide:[.term-closed_&]:w-0 wide:[.term-closed_&]:border-l-0 "
        + "phone:fixed phone:inset-y-0 phone:right-0 phone:w-full phone:border-l-0 "
        + degradedBarRoom + " "
        + "phone:transition-transform phone:duration-200 phone:ease-out "
        + "phone:[.term-closed_&]:translate-x-[101%] " + reduceColumnMotion

    /// Held at the column's full width so nothing reflows while the column animates shut.
    let terminalPane = "absolute inset-0 wide:w-[var(--term-w,var(--spacing-term))] w-term phone:w-full flex flex-col"

    /// The split between the chat and this column, made draggable — a real `separator`, so it
    /// answers to the arrow keys as well as the pointer. Desktop only: on a phone the pane IS
    /// the column and there is nothing to divide.
    ///
    /// Inside the panel rather than straddling the divider, because the panel clips its
    /// overflow — which also rules out an outline for focus, so focus is the same blue the
    /// hover shows, at full strength.
    let terminalResize =
        cls [ "phone:hidden absolute left-0 inset-y-0 w-1.5 z-50 cursor-col-resize"
              "bg-transparent hover:bg-blue/50 focus-visible:bg-blue focus-visible:outline-none"
              "transition-colors motion-reduce:transition-none" ]

    /// The pane's left edge on a phone: the way back, drawn where a sheet that slid in from
    /// the right is held. The `›` in the head is the way back for everyone; this is the same
    /// press for a thumb already at the edge, and the mark is what says the sheet came from
    /// somewhere — the affordance a swipe would have, without a gesture to discover.
    ///
    /// Out of the accessibility tree and the Tab order (`aria-hidden`, `tabindex=-1`): it is a
    /// duplicate, and a reader of the tree should meet the way back once. Which is also why it
    /// may be 6px wide — WCAG 2.5.8 excepts a target whose act an equivalent control on the
    /// same screen offers at full size, and the head's is 44. Its mark is wider than that and
    /// hangs over the pane's gutter, where no words sit; being the button's own child it is
    /// pressable all the way across.
    ///
    /// The splitter's place on a desktop (`terminalResize`); the two never share a screen.
    let paneGrabEdge =
        cls [ "wide:hidden absolute left-0 inset-y-0 w-1.5 z-50 p-0 border-0 bg-transparent cursor-pointer"
              "flex items-center"; focusRing ]
    /// Its mark: the head's own `›` (`Icon.rightSm`) in the faint ink, midway down the edge.
    /// The same glyph as the control it duplicates, so the edge reads as the way back to the
    /// chat rather than as a bare rule with no meaning (which is what a short bar was).
    ///
    /// Hung in the gutter and no further: a phone's rail is 12px from the edge, and the 14px
    /// box the head's glyph is drawn in ran 2px over the first letters of whatever row sat at
    /// its height ("›exit"). The mark's box is 12px, so it ends where the rail begins.
    let paneGrabMark = "block text-ink-faint"

    /// The column's head: a PIVOT, the one row this pane is navigated by (Zune's own idiom,
    /// which the rest of the shell already speaks: big light words, no boxes).
    ///
    /// It was three layers — a 40px bar naming the selected terminal and opening a boxed
    /// popover of every terminal, under it a strip of uppercase tabs with a `+` and a `+N`,
    /// then the content — and the name of where you were sat ABOVE the row you moved between
    /// places with, so the hierarchy read upside down and there were two ways to make a
    /// terminal. Now every name is in one row, in the type the shell uses for places
    /// (`navPivot`'s face, a size up), the selected one in full ink and the rest in the
    /// faintest ink the floor admits; `all` leads it, then the tabs, then — on a desktop — the
    /// one `+`, then the way back to the chat. Nothing in it is boxed.
    ///
    /// On a phone every control in it is a thumb's 44 (`phone:min-h-11` on each).
    let panePivotRow = "shrink-0 flex items-center gap-1 pl-1.5 pr-3 pt-2 phone:pt-1 phone:pr-1"
    /// The tablist: `all`, then the scroller of tabs. `all` stays put at the row's head however
    /// many tabs follow it — a door to every terminal that scrolled away with them would be no
    /// door, and one that moved along every time a tab opened would be a door to hunt for.
    /// `min-w-0` so the scroller is what gives way when the names outgrow the row.
    let panePivotList = "flex-1 min-w-0 flex items-center gap-1"
    /// The tabs' own scroll box, and nothing else that moves.
    ///
    /// When names run past an end, that end fades (`TabStrip.hidden`, written by the browser
    /// as `data-pane-strip-hidden`): a row cut off at a name reads as a row that ends there,
    /// and a desktop's overlay scrollbar shows only while something is scrolling. The fade is
    /// 1.5rem, which `TabStrip.edge` keeps a revealed tab clear of. No scrollbar: a pivot is
    /// panned, and a bar under 22px type would be the one line in the head. Nothing a tab
    /// paints may leave this box, which is why tabs wear `focusRingInset`.
    ///
    /// On a phone the tabs sit a step closer (`gap-1`; each item's own padding still parts
    /// the names by 16px): the row there is 390px for `all`, the tabs and the way back, and
    /// at `gap-2` it showed under two of them.
    let panePivotScroller =
        cls [ "min-w-0 flex items-center gap-2 phone:gap-1 overflow-x-auto overflow-y-hidden [scrollbar-width:none] [&::-webkit-scrollbar]:hidden"
              "data-[pane-strip-hidden=end]:[mask-image:linear-gradient(to_right,#000_calc(100%_-_1.5rem),transparent)]"
              "data-[pane-strip-hidden=start]:[mask-image:linear-gradient(to_left,#000_calc(100%_-_1.5rem),transparent)]"
              "data-[pane-strip-hidden=both]:[mask-image:linear-gradient(to_right,transparent,#000_1.5rem,#000_calc(100%_-_1.5rem),transparent)]" ]
    /// One pivot item: a name, as it was written — never cased or tracked into a label — in
    /// light 22px type. A flex ROW rather than a truncating box, because it carries a mark and,
    /// selected, the terminal's kill; the bound and the ellipsis are the name's.
    ///
    /// Told apart by INK alone, which is how Zune did it: the selected item in full ink, the
    /// rest in `ink-faint`, the dimmest ink that still clears 4.5:1 on every surface
    /// (app/tailwind.css) — so an item not chosen is quieter and still readable, never a
    /// ghost. Selecting moves no text: there is no rule to grow and no weight to change.
    let private pivotItemBase =
        cls [ "shrink-0 bg-transparent border-0 cursor-pointer px-1.5 py-1 phone:min-h-11 phone:min-w-11 inline-flex items-center justify-center gap-1.5"
              "font-ui font-light text-[22px] leading-7 whitespace-nowrap transition-colors duration-150 ease-out"
              "motion-reduce:transition-none"; focusRingInset ]
    let pivotItem = cls [ pivotItemBase; "text-ink-faint hover:text-ink" ]
    let pivotItemOn = cls [ pivotItemBase; "text-ink" ]
    /// A pivot item's name: as much of it as fits, and an ellipsis for the rest — capped so
    /// that at the narrowest pane the splitter allows, the selected item and its × still fit
    /// beside `all` whole. The `all` page writes the name out in full.
    let pivotName = "max-w-32 truncate"
    /// A mark beside a name (`View.terminalMark`): the running pulse, a failed exit, a closed
    /// terminal. Sized to the small type, sat on the name's line.
    let pivotMark = "inline-flex items-center self-center"
    /// A tab's × (P2-2): the terminal's kill, worn by the selected tab (and by one whose kill
    /// a Delete armed). The row verb's own face, sat on the name's line so it costs the row no
    /// height. On a phone its box is a thumb's 44 with the glyph centred, pulled in by as much
    /// again (`-my-2.5`), so the item it rides is still the 44 every item is.
    let terminalTabKill = cls [ btnIconBareDanger; "self-center phone:min-w-11 phone:min-h-11 phone:-my-2.5" ]
    /// The same, armed: the kill's armed face, sat the same way, so arming it grows the item
    /// sideways and never the row downwards.
    let terminalTabKillArmed = cls [ btnKillArmed; "self-center phone:min-h-11 phone:-my-2.5" ]
    /// The × a TERMINAL's tab wears — its kill, or a closed one's dismiss: `terminalTabKill`,
    /// pulled in sideways on a phone as it already is up and down (`-mx-2`). The glyph is a
    /// third of its 44px box, and the rest, in flow, made the selected tab 140px wide — half
    /// the strip. The box is still the thumb's 44 and still on top of what it overlaps, which
    /// is its own tab's padding and the gap after it, never a neighbour.
    let pivotTabKill = cls [ terminalTabKill; "phone:-mx-2" ]
    /// The pivot's one way to make something: a `+` in the pivot's own type, unboxed, faint at
    /// rest like every item not chosen. The cell is the positioning context its menu hangs
    /// from, outside the scroller, because a menu hung inside an `overflow-x-auto` box is
    /// clipped to that box.
    ///
    /// A DESKTOP control. On a phone the row is 390px for `all`, the tabs and the way back,
    /// and a 44px `+` in it was a tab the strip could not show; there the `all` page carries
    /// the same press (`allNewCell`), and exactly one of the two is ever on screen.
    let terminalTabNewCell = "relative shrink-0 flex items-center phone:hidden"
    let terminalTabNew =
        cls [ "shrink-0 w-8 h-8 phone:w-11 phone:h-11 grid place-items-center bg-transparent border-0 cursor-pointer p-0"
              "font-ui font-extralight text-[26px] leading-none text-ink-faint hover:text-ink transition-colors"; focusRing ]
    /// The empty pane's button's cell: the positioning context its menu hangs from.
    let terminalEmptyNewCell = "relative"
    /// A tab's presence marks: one dot per peer whose caret is in THAT terminal, so a
    /// collaborator typing a command in a terminal you are not looking at is visible from
    /// the pivot rather than only from inside it.
    let terminalTabPeers = "inline-flex items-center gap-0.5 self-center"

    /// The one line under the pivot, about the selected item — present only when the body
    /// does not already say it (`View.paneSubtitle`). Small and faint: it is a caption to the
    /// name over it, aligned with it, never a second heading.
    let panePivotSubtitle =
        "shrink-0 flex items-center gap-2 min-w-0 px-3 pb-2 font-ui text-small text-ink-faint"
    /// A command inside it, in the terminal's own face, cut short rather than wrapped.
    let panePivotSubtitleCommand = "min-w-0 truncate font-terminal text-code-sm text-ink-dim"
    /// A preview's line under the pivot (F3) — `‹ term 2 / $ seq 1 40 ×`: the way back to the
    /// terminal it is laid over, its name, and its close. The preview is a layer OF that
    /// terminal, whose tab stays selected in the strip; as an item of its own in the strip it
    /// was a sibling that looked like one more tab, and on a phone it cost the strip one of
    /// the three tabs it has room for. The same caption's place and size as the subtitle it
    /// stands in for, with the name in full ink because it is what is on screen.
    let panePreviewHead = "shrink-0 flex items-center gap-1.5 min-w-0 px-3 pb-2 font-ui text-small"
    /// Its way back: the terminal's name after a `‹`, an act in `ink-dim` brightening under
    /// the hand, a thumb's 44 tall on a phone. Capped, so a long terminal name leaves the
    /// preview's own name the room.
    let panePreviewBack =
        cls [ "shrink-0 max-w-[40%] inline-flex items-center gap-1 bg-transparent border-0 cursor-pointer p-0 phone:min-h-11"
              "font-ui text-small text-ink-dim hover:text-ink transition-colors"; focusRing ]
    /// What parts the way back from the name: a mark, not a word.
    let panePreviewSep = "shrink-0 text-ink-faint select-none"
    /// The preview's name, cut short rather than pushing its close off the line.
    let panePreviewName = "min-w-0 truncate text-ink"
    /// Its close: a bare ×, neutral rather than red — closing a preview ends nothing — and a
    /// thumb's 44 on a phone.
    let panePreviewClose = cls [ btnIconBare; "phone:min-w-11 phone:min-h-11" ]

    /// The pane's verbs, as WORDS in the shell's own type — `settings ›`'s voice rather than a
    /// button's. Lowercase and light, no rectangle: the pivot over them is names in light type
    /// with nothing boxed, and a row of bordered, tracked capitals under it was the one part of
    /// the column still speaking the older design. What says a word is pressable is the
    /// RESPONSE — it brightens under the hand — and the ring the keyboard gets, held off the
    /// glyphs (`focusRingFar`) because type with no box of its own wears a ring on its edge as
    /// an underline.
    ///
    /// The rest tone is `ink-dim`, a step up from the pivot's unchosen names: those are places,
    /// these are acts, and an act should not read as the quietest thing on screen. Both inks
    /// and the blue clear 4.5:1 on every surface (app/tailwind.css, Phase4). A thumb's 44 is the
    /// ROW's to give where the row holds the act directly (`paneActions`, `terminalEmpty`); an
    /// act inside a band line sits in a group of its own there, so it says so itself.
    let private paneActBase =
        cls [ "shrink-0 inline-flex items-center gap-1.5 bg-transparent border-0 cursor-pointer p-0 no-underline"
              "font-ui font-light lowercase whitespace-nowrap"
              "transition-colors duration-150 ease-out motion-reduce:transition-none"; focusRingFar ]
    /// An act at the foot of the column (`paneActions`), in the pivot's size.
    let paneAct = cls [ paneActBase; "text-pivot leading-6 text-ink-dim hover:text-ink" ]
    /// The one act a surface is FOR — the empty pane's way to make a terminal: blue, which is
    /// what interactive means here, and nothing louder than that.
    let paneActPrimary = cls [ paneActBase; "text-pivot leading-6 text-blue hover:text-blue-up-1" ]
    /// An act inside a line of the command band (the lease bar, "not marking"): the same word
    /// at the size of the line it sits in, so it reads as part of the sentence it ends.
    let bandAct = cls [ paneActBase; "text-body text-ink-dim hover:text-ink phone:min-h-11" ]
    /// The band act the line is asking for: handing the keyboard back, re-arming marking.
    let bandActPrimary = cls [ paneActBase; "text-body text-blue hover:text-blue-up-1 phone:min-h-11" ]

    /// A state said in words, in the pane: lowercase as written, small and light — a caption,
    /// never a label. What it says is in `ink-dim`; nothing here is in the error red unless a
    /// person has to do something about it (`smallErr`), because a red word repeated down a
    /// column is an alarm nobody can answer.
    let paneSays = "min-w-0 font-ui font-light text-small text-ink-dim"
    /// Who a fact is about, at the head of its line: the caption's size in full ink.
    let paneWho = "shrink-0 font-ui font-normal text-small text-ink"
    /// Somebody holds the keyboard: the live beat and the word, in live's green.
    let paneLive = "shrink-0 inline-flex items-center font-ui font-light text-small text-green"

    /// The pane's body — whatever the selected tab shows. It takes the column's remaining
    /// height so the thing inside it scrolls rather than the column.
    let paneBody = "flex-1 min-h-0 flex flex-col"
    /// The tab panel itself: the body's box, and a ring when it holds focus. A chip, a row of
    /// the list and "show in terminal" all put focus HERE, and without a ring of its own the
    /// panel wore the browser's — a 1px near-black outline on a near-black column, which is
    /// focus nobody can see. Drawn INSIDE the edge, because the column clips at its own
    /// border and a ring held outside it would be cut away on every side but none.
    let panePanel =
        paneBody
        + " focus-visible:outline focus-visible:outline-2 focus-visible:outline-blue focus-visible:-outline-offset-2"
    /// A read-only region: a player's own mount, or text shown rather than typed into. The
    /// same scrolling box the block history uses, so a block read from the chat looks exactly
    /// like the block read in its terminal — without the bottom anchoring, because a player
    /// is one child and belongs at the top of its region.
    ///
    /// One name for one box. It had two, and the second was reached for by whichever surface
    /// its author happened to be reading.
    let paneReadonly = "flex-1 min-h-0 overflow-y-auto flex flex-col gap-3 px-3 py-3"
    /// What a replay player is mounted into, inside its read-only region (`Replay.mount`): the
    /// region's whole height, because the player scales its terminal to fit the box it is given
    /// in BOTH directions and a box with no height of its own gives it nothing to fit. Never
    /// shorter than the player's control bar plus a few lines, so a region squeezed to a sliver
    /// scrolls rather than handing the player a height it would scale to below zero.
    ///
    /// The player itself spans the region's width whatever its terminal does, with the terminal
    /// centred in it. Left to itself the player is exactly as wide as the terminal it scaled,
    /// and a tall, narrow recording fitted to a short panel came out narrower than its own
    /// control bar: the bar's last button ran past the player's edge and was clipped away. The
    /// room either side of the terminal is the player's own ground, so it reads as letterboxing.
    ///
    /// A chapter's label — the command that began there, over its mark on the bar — is at most
    /// two lines of a narrow measure, cut short with an ellipsis. The player draws it on one
    /// line however long the command, centred on its mark, so a `for` loop's label ran off
    /// both sides of a phone and off the right of a desktop pane, clipped to a middle that
    /// named nothing. Narrow enough that a mark anywhere on a phone's bar keeps its label on
    /// the screen; two lines so the command is still recognisable there. The wrap is `!`
    /// because the player's sheet sets `nowrap` outside any cascade layer, which beats every
    /// utility in one whatever its specificity.
    let replayStage =
        "h-full min-h-24 [&_.ap-player]:min-w-full [&_.ap-term]:mx-auto "
        + "[&_.ap-tooltip]:w-max [&_.ap-tooltip]:max-w-48 [&_.ap-tooltip]:whitespace-normal! "
        + "[&_.ap-tooltip]:wrap-anywhere [&_.ap-tooltip]:line-clamp-2"
    /// A stretch's facts, above whatever renders its recording.
    let paneFacts = "shrink-0 flex flex-col gap-1 px-3 py-3 " + Stroke.dividerBottom
    /// A read-only tab's verbs, under whatever it is showing: the way to the recording, and
    /// the way back. A row rather than a column, because they are alternatives to each other
    /// rather than a list of facts.
    ///
    /// On a phone every verb in it is a thumb's height (`[&>*]`), whatever face it wears — a
    /// button, or a download link.
    ///
    /// Words (`paneAct`) with air between them, and no rule over them: the band above ends on
    /// its own ground, and a hairline between it and a row of light type was a box's edge with
    /// the rest of the box taken away.
    let paneActions = "shrink-0 flex flex-wrap items-center gap-x-7 gap-y-1 px-3 pt-2.5 pb-3 phone:py-1 phone:[&>*]:min-h-11"

    // --- Content: a file the pane shows rather than a terminal ---------------------------

    /// The box a picture sits in. Centred and scrollable, on the surface's own dim rather than
    /// on white: an image with transparency or a pale edge needs a backdrop that says where the
    /// picture stops, and a checkerboard would be a second thing to look at.
    let contentImageBox =
        "flex-1 min-h-0 overflow-auto flex items-center justify-center p-3 bg-surface-2"
    /// The picture itself: fitted to the box, never upscaled past its own pixels — a 16px icon
    /// stretched across a desktop pane is a blur presented as a fact.
    let contentImage = "max-w-full max-h-full object-contain"
    /// What a file the pane cannot draw gets instead: its name, its size, and the way to have
    /// it. Centred like the empty pane, because it IS an empty pane — of this kind.
    let contentDownload = "flex-1 flex flex-col items-center justify-center gap-3 px-6 text-center"

    // --- The `all` page: the switcher (Plan 20, stage 0; P2-2) ------------------------------

    /// The `all` page: the pane's body while the pivot's first item is selected. It has been
    /// the pane's other face and then a bordered popover hung under the head, with dots, a
    /// divider under every row and a foot that made terminals; it is a page now, the way a
    /// Zune list is a page — names in large light type down the left, no rule between them,
    /// the air doing the separating.
    let allPage = "flex-1 min-h-0 overflow-y-auto overscroll-contain flex flex-col py-2 select-none"

    /// One row: its name (and the marks beside it) over what it last ran, its verbs at the
    /// right. The names line up down the page because every mark sits AFTER its name — a
    /// leading column of marks of four different widths is what made the old list's left
    /// edge ragged.
    let terminalListRow = "group/row flex items-center gap-3 px-3 py-2 phone:py-1"

    /// The row's own control: its name, which opens it — the pivot's type, so the page reads
    /// as the pivot's names written out in full. Ink at rest so the page reads as a list of
    /// names, blue under the pointer because that is what interactive means here.
    let terminalListName =
        cls [ "min-w-0 truncate bg-transparent border-0 cursor-pointer text-left p-0 phone:min-h-11"
              "font-ui font-light text-[22px] leading-7 text-ink hover:text-blue transition-colors"; focusRing ]

    /// A closed row's name: dimmer, which says which half of the list it is in by its tone
    /// rather than by repeating the word — its mark says the rest.
    let terminalListNameClosed =
        cls [ "min-w-0 truncate bg-transparent border-0 cursor-pointer text-left p-0 phone:min-h-11"
              "font-ui font-light text-[22px] leading-7 text-ink-dim hover:text-blue transition-colors"; focusRing ]

    /// A row's name line: the name, then its marks, on one line that gives way in the name.
    let terminalListNameLine = "min-w-0 flex items-center gap-2"

    /// A closed terminal's "recording lost": said once where it is about the one terminal on
    /// screen (its closed band) and once per row on the `all` page — so in the quietest voice
    /// that is still read, never squeezed, lowercase as written. It was the error red in
    /// tracked capitals, and nine closed rows made a column of alarms about something nobody
    /// can do anything about; the hollow mark beside the name already says the terminal is
    /// closed, and the missing rewind says there is nothing to play.
    let terminalGone = "shrink-0 whitespace-nowrap font-ui font-light text-small text-ink-faint"

    /// What a terminal row's terminal is doing or last ran, under its name: the command, in
    /// the terminal's own face, small and faint — it tells nine `term N` rows apart, and it is
    /// not the thing the row opens. An artifact's size sits in the same slot.
    let terminalListSubtitle = "block min-w-0 truncate font-terminal text-code-sm text-ink-faint"

    /// A row's verb, as a WORD: `rewind`, `kill`, `replay`, `put away`. They were glyphs, and
    /// the kill's was a square — which is what stop looks like, beside a command that now has
    /// a real Stop. A word cannot be read as anything else, and a verb that rides a listed row
    /// is bare, in the caps voice every bare verb speaks in, `ink-dim` at rest
    /// (docs/visual-design.md, "Hierarchy of controls") — ink under the hand, or the error red
    /// for the one that ends something.
    let private terminalListActBase =
        cls [ "h-6 px-1 shrink-0 inline-flex items-center justify-center bg-transparent border-0 cursor-pointer"
              caps; "whitespace-nowrap transition-colors"; focusRing ]
    let terminalListAct = cls [ terminalListActBase; "text-ink-dim hover:text-ink" ]
    let terminalListKill = cls [ terminalListActBase; "text-ink-dim hover:text-err" ]

    /// The row's verbs, at its right edge, WORN: on the row the pane is about, and on one whose
    /// kill is armed — a confirm that faded out would be a confirm nobody could see. Which rows
    /// wear them is the view's to say (`View.allPage`); the rest wear `terminalListVerbsAtRest`.
    ///
    /// On a phone each is at least a 44px box with its word centred (`[&>*]`): the 24px line
    /// is WCAG 2.5.8's floor for the MARK, never for the target, and a kill 4px from a rewind
    /// is a kill pressed by a thumb aiming at the rewind.
    let terminalListVerbs =
        "ml-auto flex items-center gap-4 shrink-0 phone:gap-2 phone:[&>*]:min-w-11 phone:[&>*]:min-h-11"

    /// Every other row's verbs: there, but not shown, until they are wanted. Under the pointer
    /// or the keyboard anywhere in the row they come up (`group/row`, and `focus-within` so Tab
    /// reaches them visibly) — `opacity`, never `hidden`, so they never leave the tab order.
    ///
    /// A thumb has no hover, so on a device that cannot hover they are not merely see-through
    /// but out of the way (`sr-only` until focus is inside them): a see-through kill at a
    /// row's edge is a kill a thumb can press without seeing, and the width they kept was the
    /// width the command under the name was cut short by. There, the row is how to reach them:
    /// it opens its terminal, whose own surface carries every one of these, and the row it was
    /// opened from wears them when the reader comes back.
    let terminalListVerbsAtRest =
        cls [ terminalListVerbs
              "opacity-0 group-hover/row:opacity-100 group-focus-within/row:opacity-100"
              "[@media(hover:none)]:[&:not(:focus-within)]:sr-only"
              "transition-opacity duration-150 ease-out motion-reduce:transition-none" ]

    /// The page's filters (F5), at its head over the rows: `all` and each kind of terminal
    /// there is, with how many. ONE line, never wrapped: a filter arriving or leaving as
    /// terminals change must not grow the row and move every row under it, so words that
    /// outgrow it pan inside it rather than wrap or push the page sideways. The first word
    /// on the rail, as every name under it is.
    let allFilters =
        "shrink-0 flex items-center gap-x-4 px-3 pb-2 phone:gap-x-3 overflow-x-auto [scrollbar-width:none] [&::-webkit-scrollbar]:hidden"

    /// One filter: the pivot's idiom at the section label's size — a word, lowercase and
    /// light, told apart from the others by INK alone: the one the page is narrowed to in
    /// full ink, the rest in `ink-faint` and brightening under the hand. A setting, so bare
    /// (docs/visual-design.md, "Hierarchy of controls"); a lens over the list rather than a
    /// verb on it, so in the page's lowercase rather than the row verbs' caps. Read from
    /// `aria-pressed`, so what is drawn and what is announced are one attribute. A thumb's 44
    /// on a phone, with the word kept on the rail.
    let allFilter =
        cls [ "shrink-0 inline-flex items-center gap-1 bg-transparent border-0 cursor-pointer p-0 phone:min-h-11 phone:min-w-11"
              "font-ui font-light text-body lowercase whitespace-nowrap text-ink-faint hover:text-ink aria-pressed:text-ink"
              "transition-colors duration-150 ease-out motion-reduce:transition-none"; focusRingFar ]

    /// A filter's count: figures that do not shift the words beside them as they change.
    let allFilterCount = "tabular-nums"

    /// The page's word when there is nothing on it.
    let contentListEmptyWord = "px-3 py-3 font-ui font-light text-body text-ink-faint select-none"

    /// The empty pane: the terminal's own symbol, display-sized, over the one press that
    /// fills it — a thumb's height on a phone, like every press in this column.
    let terminalEmpty = "flex-1 flex flex-col items-center justify-center gap-3 px-6 text-center phone:[&_button]:min-h-11"

    /// The `all` page's way to make a terminal, at its head — on a phone only, the other half
    /// of `terminalTabNewCell`: the `+` is a desktop control, and the two are one press, so
    /// each is hidden wherever the other shows. The empty pane's own act in look (blue, the
    /// pivot's size), not a row: a row that MAKES a thing drawn like one that SELECTS one is
    /// what the old switcher's foot was removed for. The cell is what its menu hangs from,
    /// left-aligned under it as the page is.
    let allNewCell = "wide:hidden relative self-start px-3 pb-2"
    let allNew = cls [ paneActPrimary; "min-h-11" ]
    let allNewMenu =
        cls [ "absolute left-3 top-full z-30 py-1 select-none bg-panel text-left"
              "min-w-[14rem]"
              Stroke.ring
              Stroke.hair ]

    /// What a section of the page is called: the pivot's lowercase, small, faint. The rows
    /// are the content, and a heading that competed with them would make a page of three
    /// terminals read as two lists. Present only when there is more than one kind to tell
    /// apart (`listSections`).
    let listSectionLabel = "px-3 pt-4 pb-1 font-ui font-light text-small lowercase text-ink-faint select-none"

    /// An artifact's row: the same row as a terminal's, so the two sections read as one list
    /// of things rather than two designs.
    let artifactListRow = terminalListRow

    /// The size under an artifact's name — the one fact that decides whether to open it here
    /// or take it away, so it is on the row rather than behind it.
    let artifactListSize = "block font-ui font-light text-small text-ink-faint tabular-nums"

    /// The block history's scroll box, and the stream inside it.
    ///
    /// A terminal grows DOWNWARD from the top and the viewport rides the tail. Those are two
    /// different statements and only the second one is about the bottom: with a long history
    /// the newest line does sit at the bottom edge, because the scroller is kept there
    /// (`Tail`), not because the content is.
    ///
    /// `mt-auto` on the stream said the first statement as if it were the second, and with a
    /// short history it showed: two lines pinned to the floor under 500px of void, measured on
    /// a phone. No terminal has ever looked like that. `relative` so the way back to the live
    /// edge can float over this box rather than take a band from it.
    ///
    /// The vertical air is the STREAM's, not the box's: a command line held at the top while
    /// its output scrolls (`terminalBlockSummary`) is held at the box's content edge, and a
    /// padded box held it 12px down with the output it was hiding showing above it.
    let terminalScrollback = "relative flex-1 min-h-0 overflow-y-auto flex flex-col px-3"
    let terminalStream = "flex flex-col gap-2 py-3"
    /// The commands inside a run's fold: the stream's rhythm, without its edges.
    let terminalBlockRunBody = "flex flex-col gap-2 phone:gap-0"

    /// One block: the command that ran, then everything it printed.
    ///
    /// It is not a card. A card per command turned a scrollback into a stack of boxes —
    /// borders, fills and 12px of padding repeated around every two lines of mono, which is
    /// the one thing a terminal does not look like. What separates two blocks is what
    /// separates them in a terminal: a green prompt glyph, the command in ink, its output
    /// dim beneath, and a line of air.
    let terminalBlock = "flex flex-col"
    /// The command line as it was run, and how it went. It WRAPS: the command (`terminalBlockLine`)
    /// asks for its whole length, so when it and its status do not fit one line the status goes
    /// UNDER it, at the end of its own line, and the command gets the full width. Side by side,
    /// a long status ("refused by <name>") took half a phone's column and the command broke
    /// mid-word in the other half.
    let terminalBlockCommand = "flex flex-wrap items-baseline gap-x-2 gap-y-0.5"
    /// The prompt and the command, held together as one item of `terminalBlockCommand` so a wrap
    /// takes the status down a line rather than the command away from its `$`. `grow` so the
    /// status that does fit sits at the line's end; `min-w-0` so it can give way to the row.
    let terminalBlockLine = "flex min-w-0 grow items-baseline gap-2"
    /// A block's command line in the scrollback, which STAYS while its output scrolls: a
    /// 300-line output used to carry its own command off the top, and a reader in the middle
    /// of it was looking at a screen of numbers with nothing to say what printed them.
    /// Sticky within its block, so the next block's command takes over as it arrives rather
    /// than stacking beneath; painted with the pane's own ground, so the lines going under it
    /// go under it rather than through it.
    let terminalBlockSummary = cls [ terminalBlockCommand; "sticky top-0 z-10 bg-panel" ]
    /// A run's fold ("ran N commands") — a real `<summary>`, so the disclosure is the
    /// browser's and arrives keyboard-operable and correctly announced. Not sticky: the
    /// commands inside it are, and two rows held at one edge is one row hidden.
    let terminalBlockRunSummary =
        cls [ "flex items-baseline gap-2 cursor-pointer list-none"
              "hover:text-ink transition-colors duration-150 ease-out"; focusRing ]
    /// Its mark: the chevron every fold turns (`queryLegendMark`), pointing on at rest and
    /// down when open. It was an ellipsis, which at the end of a line reads as a menu.
    let terminalBlockRunMark =
        "ml-auto shrink-0 text-ink-faint select-none group-hover:text-ink "
        + "transition-[color,rotate] duration-150 ease-out motion-reduce:transition-none group-open:rotate-90"
    /// The facts under a command, on the output's column so they read as an aside to the
    /// command rather than as more output.
    let terminalBlockFacts = "flex flex-wrap items-baseline gap-x-4 gap-y-0.5 pl-4 py-1"
    /// One fact: a sentence as the session wrote it (why a command did not exit), so small
    /// and light and in its own case — tracked capitals made an aside shout over the output.
    let terminalBlockFact = "font-ui font-light text-small text-ink-faint"
    /// The ✓/✗ tally beside "ran N commands" on a `terminalBlockRun` — the same
    /// shape the chat's task card counts wear (`chatTaskCounts`), kept as its own name
    /// because this one sits on the pane's own fold rather than the chat's.
    let terminalBlockRunCounts = "flex items-baseline gap-2 shrink-0"

    /// Stop, on a running command's line: the bare danger verb with its glyph, which is what
    /// the chat's interrupt says too. 44px on a phone, the target this product holds a thumb
    /// to; the line is a reading line on a desktop and the 24px box stays there.
    let terminalBlockStop =
        cls [ btnBareDanger; "shrink-0 gap-1 phone:min-h-11 phone:min-w-11 phone:justify-center" ]

    /// Who ran a command, on the command's own line — a reference, so it carries the mark AND
    /// the name. `shrink-0` with a bounded name, because the command beside it is the longer
    /// thing and the one that should give way; `min-w-0` lets the name itself give way before
    /// the row does, on a phone where neither fits.
    let terminalBlockAuthor = "shrink-0 min-w-0 max-w-[40%] inline-flex items-baseline"

    let terminalPrompt = "shrink-0 font-terminal text-code text-green select-none"
    /// A command breaks where a shell would let you: at its spaces. `anywhere` is the last resort
    /// for a single token wider than the column (a long path), which must wrap rather than push
    /// the page sideways, and it is also what lets a flex item holding one shrink below that
    /// token's width. It was `break-all`, which broke every command mid-word the moment it
    /// reached the edge.
    let terminalCommandText = "min-w-0 font-terminal text-code text-ink [overflow-wrap:anywhere]"
    /// Output: preformatted, wrapping, and horizontally scrollable for the lines that will
    /// not wrap — the column must never make the PAGE scroll sideways. No padding of its
    /// own: it sits directly under its command, on the scrollback's own gutter, the way a
    /// terminal prints.
    let terminalOutput = "overflow-x-auto font-terminal text-code-sm leading-4 whitespace-pre-wrap break-words text-ink-dim"
    let terminalOutputEmpty = small
    /// The lines a block leaves to its recording (`TerminalFeed.shownLines`), said above the
    /// ones it shows — quiet, because nothing is missing: the recording holds every one.
    let terminalOutputElided = small
    /// Open a block in the pane's history to all its output, or shut it: the bare verb in
    /// words, on the output's own gutter and 44px on a phone, like Stop beside it.
    let terminalOutputExpand = cls [ btnBare; "-ml-1 phone:min-h-11" ]
    /// The truncation notice: a stated gap in the record. Stated, because a missing audit
    /// trail is not a neutral fact — but in the pane's caption voice, in `ink-dim` rather
    /// than the error red: nobody can do anything about bytes the cap already dropped, and
    /// the red is kept for what a person has to act on.
    let terminalTruncated = "shrink-0 px-3 py-2 font-ui font-light text-small text-ink-dim"

    /// The live screen (Plan 14, stage 6). Monospaced, preformatted, and scrollable in both
    /// axes — a terminal's lines are as wide as the program made them, and wrapping them
    /// would redraw a screen the program laid out. The focus ring matters more here than
    /// anywhere: this is the one surface whose whole purpose is having the keyboard.
    ///
    /// So it is the one place the ring paints on ANY focus, not only a keyboard's. Every way
    /// into it is programmatic — the lease landing (`Screens.Sync`), Take over, a jump back to
    /// the tail — and a script's `focus()` is not `:focus-visible`, so the ring the rest of the
    /// product wears painted nothing here at the one moment it was needed: a reader who had
    /// just taken the keyboard could not see where it had gone. Inset, because the screen
    /// fills a box that ends where it does. Only the holder's copy can take focus at all, and
    /// it takes it in the field inside it (`terminalKeys`), so the ring is the screen's while
    /// focus is WITHIN it: the field is invisible, and the screen is what has the keyboard.
    let terminalScreen =
        cls [ "group/screen flex-1 min-h-0 overflow-auto px-3 py-2 font-terminal text-code-sm leading-4"
              "whitespace-pre text-ink bg-bg"
              "focus-within:outline focus-within:outline-2 focus-within:outline-blue focus-within:-outline-offset-2" ]

    /// The field the holder's live screen types through (P3-3): a real text field, because a
    /// phone raises its keyboard for nothing else, and invisible, because the screen is what
    /// shows what was typed. Not `display:none` or zero-sized — neither takes focus on every
    /// platform — but one transparent pixel that gives its height back. Sticky at the screen's
    /// foot, so focusing it never scrolls the screen off its tail and a phone pans the page to
    /// where the newest line is. 16px, under which iOS zooms the page onto a focused field.
    let terminalKeys =
        "sticky bottom-0 left-0 block w-px h-px -mt-px p-0 border-0 opacity-0 bg-transparent resize-none overflow-hidden text-base caret-transparent"

    /// Where the screen's cursor stands (`ScreenCursor`): the cell outlined, the way a terminal
    /// draws the cursor of a window that does not have the keyboard — and filled once the
    /// screen it is on does, which only the holder's can. Ink on ground both ways round, so
    /// the character under it reads at the same contrast it had beside it.
    let terminalCaret =
        "outline outline-1 -outline-offset-1 outline-ink-dim group-focus-within/screen:bg-ink group-focus-within/screen:text-bg group-focus-within/screen:outline-0"

    /// The DVR, which is TWO acts that were wearing one button in one band.
    ///
    /// Going back and coming back are opposites with opposite lifetimes, and once separated
    /// each has an obvious home — neither of them a band of its own.
    ///
    /// Going back is a DESTINATION, and you reach a destination by scrolling to it. So the way
    /// in sits at the top of the scrollback, in the content, scrolling with it, exactly where
    /// the history you have runs out: earlier is up. It costs no permanent chrome at all, and
    /// it is there only when something is actually recorded.
    let terminalReplayFrom =
        cls [ "self-start flex items-center gap-2 bg-transparent border-0 cursor-pointer px-0 py-1"
              "font-terminal text-code-sm text-ink-faint hover:text-ink transition-colors"; focusRing ]

    /// Coming back is TRANSIENT — it exists only while you are behind the live edge. How far
    /// behind is said on a line of its own UNDER the player, starting on the pane's rail like
    /// every other line of text in it.
    ///
    /// Under it rather than over it. It floated in that corner, and the corner of a player is
    /// its control bar: the fact sat on top of the bar's last buttons, so the words were
    /// unreadable and the buttons unpressable. In the flow, the player fits itself to what is
    /// left (`Replay.mount`) and the two never meet.
    ///
    /// A status line, not a box: what stands here is a fact (how far behind live), and a
    /// bordered box around a fact reads as a control nobody can press.
    let terminalBehindLine = "shrink-0 flex items-center gap-3 px-3 pb-2"
    /// What it says: how far behind, as a caption (`paneSays`'s voice).
    let terminalBehind = "font-ui font-light text-small text-ink-dim"
    /// The rewound read: the player's region, and under it how far behind live it is.
    let terminalReplayRegion = "flex-1 min-h-0 flex flex-col"

    /// The region a terminal's text — its blocks, or its live screen — scrolls in, and its
    /// "jump to latest" floats over: `chatRegion`'s reason, since the scroller cannot be the
    /// positioned ancestor of something that must not scroll away with it.
    let terminalTailRegion = "relative flex-1 min-h-0 flex flex-col"

    /// That float's slot: the corner every chat client puts the way back in, and hidden until
    /// `Tail` says the reader has left the end.
    let terminalJumpToLatestSlot = "absolute right-3 bottom-3 z-10"

    /// The button in it: `chatJumpToLatest`'s square, without the rail that places that one
    /// beside a reading column — a terminal has no column to stand beside.
    let terminalJumpToLatest =
        cls [ "w-8 h-8 flex items-center justify-center bg-surface"
              "text-ink-dim hover:text-ink cursor-pointer transition-colors"
              Stroke.ring; Stroke.rim; focusRing ]

    /// The command band beneath the blocks: the command line, whatever is queued against this
    /// terminal, and nothing else. The approval control that used to head it is a property of
    /// the terminal and now says so from the bar.
    ///
    /// The message composer's band exactly — same tone, same top rule, same gradient on focus
    /// — because typing a command here and typing a message there are the same act, and the
    /// pane was built out of the composer's parts for that reason. It carries no gutter of its
    /// own: what is in it runs edge to edge, and the rows that are not the command line bring
    /// their own padding (`terminalBandRow`).
    ///
    /// `composerBand`, above, kept as one token rather than a matching string, so the tone
    /// and the rule can never quietly drift from the message composer's. The bottom
    /// clearance is its own, though (`phone:pb-4`, plain thumb room): Run stays on the
    /// command line rather than dropping below it the way Send does, so this band never
    /// needs its room — which is also the clearance the message composer falls back to when
    /// its own row is not showing (`composer`, above).
    let terminalComposer = composerBand + " phone:pb-4"

    /// A row in the band that is not the command line — the lease bar, the "not marking"
    /// notice. They used to inherit the section's padding; the band has none.
    let terminalBandRow = "flex items-center gap-2 px-3 py-2 phone:[&>button]:min-h-11"

    /// The notice to the person a terminal's keyboard was taken from (`ClientModel.Stolen`),
    /// in the lease bar's place: one sentence and the two answers to it. Said in full ink and
    /// not in the error red — nothing is wrong, and taking it back is a choice, not a repair —
    /// but louder than the lease bar's caption it stands in for, because for this one reader
    /// it is news rather than a state.
    let terminalStolenSays = "min-w-0 font-ui font-light text-small text-ink"
    /// Its ×: the bare icon verb, a thumb's square on a phone.
    let terminalStolenDismiss = btnIconBare + " phone:w-11 phone:h-11"

    /// The command line: the row IS the field.
    ///
    /// It used to share its row with a `$` glyph and a Run button, and its column with two
    /// more rows above — leaving the one thing you type into the smallest thing in the pane
    /// (measured 273px of a 390px phone), narrow enough that a real command scrolled inside it
    /// while being typed.
    ///
    /// The `$` is now the PLACEHOLDER. That is not a trick to save an element: it puts the
    /// glyph exactly at the text origin, so it marks where the command will start and is
    /// replaced by the first character rather than sitting beside a box the text then begins
    /// to the right of. The old placeholder said "a command to run here", which a `$` says
    /// shorter; the field keeps its `aria-label`, because a placeholder is not a name.
    let terminalCommandWrap = "relative w-full"
    /// `py-3` is not a guess: 12 + 16 + 12 is exactly the 40px the message composer's band
    /// stands at (`py-2` around a 24px line), and the two sit side by side on a desktop where
    /// four pixels of disagreement between them reads as one of the columns being wrong.
    /// A phone has no column beside it to agree with, and a thumb to hold: 44px there, with
    /// room at the trailing edge for the 44px Run (`btnInField`).
    let terminalCommand =
        cls [ "w-full"; fieldBare
              "font-terminal text-code text-ink px-3 py-3 pr-10 phone:py-3.5 phone:pr-13 placeholder:text-green"; touchType ]
    /// What sits at the field's trailing edge, inside its border: whoever else has a caret in
    /// this slot, and the verb.
    let terminalCommandTrail = "absolute right-1 inset-y-0 flex items-center gap-1"

    // Run itself is `btnSendInField` / `btnSendInFieldWaiting`, which the message composer's
    // Send used to be as well. The two parted over the geometry rather than the meaning:
    // queueing a command and sending a message are still the same act, but this verb is
    // parked OVER the command line (`terminalCommandTrail`, absolutely placed inside the
    // field), where a word would sit on top of what is being typed, while the composer's
    // verbs have a row to themselves and can afford one (`btnComposerSend`). Which of the two
    // faces it wears comes from the MODEL (a published slot, the same fact the send path acts
    // on), never from a second measurement of the field.
    /// A queued command awaiting its turn — a listed row, so its leading edge carries its
    /// state.
    let private terminalQueued = cls [ "flex-col gap-1 px-3 py-2"; rowBase ]
    let terminalQueuedReady = cls [ terminalQueued; Stroke.green ]
    let terminalQueuedRow = "flex items-center gap-2 phone:[&_button]:min-w-11 phone:[&_button]:min-h-11"
    /// Someone else's composer slot in this terminal: shown, not editable-by-mistake — it is
    /// the same live text, so it is the terminal's version of watching a draft being written.
    /// Its leading edge is the author's own colour, set inline.
    let terminalPeerDraft = cls [ "items-center gap-2 px-3 py-2"; rowBase ]
    /// Who is in a slot right now, by live caret — one dot per peer, coloured by peer.
    let terminalEditors = "shrink-0 flex items-center gap-1"

    /// The empty state, when no terminal is open.

    /// Reopens the column once it is shut — the mirror of the sidebar's reopen chevron,
    /// leaning the way the column travels. Rendered from the model rather than hidden by a
    /// variant: whether the control exists is a fact about the model, and a button that is
    /// merely invisible is still in the tab order.
    let terminalReopen = navChevronBack + " shrink-0"
    /// Its mark, AFTER the count ("2 terminals ●"), because the count is what the tab is
    /// named by and the mark only qualifies it. Idle: a dot in the control's own faint ink,
    /// saying terminals are there and nothing is happening in them. Running: the live
    /// dot itself (`statusDotLive`), the same mark a running block wears everywhere else.
    let terminalReopenIdle = "inline-block w-1.5 h-1.5 rounded-full bg-current"
    let terminalReopenRunning = statusDotLive

    // --- ANSI styling ---------------------------------------------------------------------------
    // Turning a parsed `AnsiStyle` into what a span wears. Split in two on purpose:
    //
    //   * the SIXTEEN named colours are theme tokens, emitted as literal utility class names.
    //     Literal because Tailwind scans this source for the classes it must generate — a
    //     `sprintf "text-term-%s"` composes a class that is never built, and the span would
    //     come out unstyled. (The same trap the avatar checkers hit; see `@source inline`.)
    //   * the other 240 (the 6x6x6 cube, the grey ramp) and any 24-bit colour are ARITHMETIC,
    //     resolved by `Ansi.rgbOf` and emitted inline. There is no token to name them with,
    //     and 240 generated utilities to cover a case a build log might use once is not a
    //     trade worth making.

    let private ansiFgClass (colour: AnsiColour) : string =
        match colour with
        | IndexedColour 0 -> "text-term-black"
        | IndexedColour 1 -> "text-term-red"
        | IndexedColour 2 -> "text-term-green"
        | IndexedColour 3 -> "text-term-yellow"
        | IndexedColour 4 -> "text-term-blue"
        | IndexedColour 5 -> "text-term-magenta"
        | IndexedColour 6 -> "text-term-cyan"
        | IndexedColour 7 -> "text-term-white"
        | IndexedColour 8 -> "text-term-black-bright"
        | IndexedColour 9 -> "text-term-red-bright"
        | IndexedColour 10 -> "text-term-green-bright"
        | IndexedColour 11 -> "text-term-yellow-bright"
        | IndexedColour 12 -> "text-term-blue-bright"
        | IndexedColour 13 -> "text-term-magenta-bright"
        | IndexedColour 14 -> "text-term-cyan-bright"
        | IndexedColour 15 -> "text-term-white-bright"
        | _ -> ""

    let private ansiBgClass (colour: AnsiColour) : string =
        match colour with
        | IndexedColour 0 -> "bg-term-black"
        | IndexedColour 1 -> "bg-term-red"
        | IndexedColour 2 -> "bg-term-green"
        | IndexedColour 3 -> "bg-term-yellow"
        | IndexedColour 4 -> "bg-term-blue"
        | IndexedColour 5 -> "bg-term-magenta"
        | IndexedColour 6 -> "bg-term-cyan"
        | IndexedColour 7 -> "bg-term-white"
        | IndexedColour 8 -> "bg-term-black-bright"
        | IndexedColour 9 -> "bg-term-red-bright"
        | IndexedColour 10 -> "bg-term-green-bright"
        | IndexedColour 11 -> "bg-term-yellow-bright"
        | IndexedColour 12 -> "bg-term-blue-bright"
        | IndexedColour 13 -> "bg-term-magenta-bright"
        | IndexedColour 14 -> "bg-term-cyan-bright"
        | IndexedColour 15 -> "bg-term-white-bright"
        | _ -> ""

    /// A span's colours, resolved with `Inverse` already applied — the flag is a render-time
    /// swap, which is exactly why the parser leaves it as a flag rather than pre-swapping
    /// colours it does not know the defaults for. A background fill also forces the
    /// foreground to the page ground, so inverted text stays readable when only one side of
    /// the pair was ever set.
    let private ansiColours (style: AnsiStyle) : AnsiColour * AnsiColour =
        if style.Inverse then
            (match style.Background with DefaultColour -> IndexedColour 0 | c -> c),
            (match style.Foreground with DefaultColour -> IndexedColour 7 | c -> c)
        else style.Foreground, style.Background

    /// The utility classes for a styled run.
    let ansiClasses (style: AnsiStyle) : string =
        let foreground, background = ansiColours style
        [ if style.Bold then "font-semibold"
          // Dim is opacity, not a colour: it has to compose with whatever colour is set,
          // and it must not drop text below the contrast floor — 75% of a colour that
          // clears 4.5:1 on this ground still clears 3:1, and dim text is decoration.
          if style.Dim then "opacity-75"
          if style.Italic then "italic"
          if style.Underline then "underline"
          ansiFgClass foreground
          if background <> DefaultColour then "px-0.5"
          ansiBgClass background ]
        |> List.filter (fun c -> c <> "")
        |> String.concat " "

    /// The inline `style` for a run whose colour is arithmetic rather than named. Empty for
    /// every named colour, which is the common case.
    let ansiInline (style: AnsiStyle) : string =
        let foreground, background = ansiColours style
        let rgb (label: string) (colour: AnsiColour) =
            match AnsiColour.rgbOf colour with
            | Some (r, g, b) -> sprintf "%s:rgb(%d %d %d);" label r g b
            | None -> ""
        rgb "color" foreground + rgb "background-color" background

    // --- Document shell ------------------------------------------------------------------------

    /// Classes for the `#app` wrapper (served once in `View.page`; the browser only ever
    /// swaps its innerHTML, so these persist untouched across re-renders).
    ///
    /// `h-dvh`, never `h-screen`. `100vh` is the LARGE viewport — the height the page WOULD
    /// have with the browser's toolbars hidden — so on a phone showing its toolbars the shell
    /// is ~140px taller than anything that can be seen, and the whole document pans inside the
    /// visible area. Every symptom of that is a chrome one: the header slides up under the
    /// address bar and loses the top of the title, the composer sits behind the bottom bar,
    /// and the timeline is cut by an edge that is off screen (photographed on iOS Safari;
    /// desktop never shows it, because a desktop's viewport does not move).
    ///
    /// `100dvh` is what is visible NOW, so the shell fills exactly that and nothing pans —
    /// which is what the layout below already assumes, every region being sized off this one
    /// height. On a viewport that never changes (every desktop, and the test harnesses) the
    /// two units are the same number.
    let app = "flex h-dvh overflow-hidden bg-bg text-ink font-ui antialiased"

    /// The same ground and face as `app`, on a page that is a DOCUMENT rather than a shell: the
    /// Manager's standalone pages (`/open` while a session launches, a refusal with a way
    /// back). No `h-dvh`/`overflow-hidden` — a few paragraphs scroll like paragraphs — but the
    /// colours are `app`'s exactly, because the one thing these pages must not do is flash a
    /// different ground between two surfaces that share one.
    let standalone = "bg-bg text-ink font-ui antialiased"

    /// Tailwind, built locally into a stylesheet and served by both the Session and
    /// the Manager UI — never a CDN (local first). The utilities and the theme tokens come
    /// from the CLI build over `app/tailwind.css`, whose `@source` rules scan the F# sources
    /// for the composed class names.
    ///
    /// Takes the URL rather than building it: the stylesheet is addressed by a digest of its
    /// own bytes, which only the serving process (having read them) can know.
    ///
    /// The colour SCHEME is not said here. It rides in the sheet, beside the ground it
    /// belongs with (`tailwind.css`); a `<meta name="color-scheme">` stood here for two
    /// releases on the belief that being parsed before any fetch let it colour what a phone
    /// shows between two documents, and photographs on WebKit say it does not. What is on
    /// the glass there is the standalone app's window: `WebApp.managerManifest`.
    let headTags (styleSheetUrl: string) =
        sprintf "<link rel=\"stylesheet\" href=\"%s\">" styleSheetUrl

    /// A stylesheet the page may never need: linked, so its address is the server's to state
    /// and the browser may fetch it whenever it likes, but `media="not all"` so it matches
    /// nothing and cannot hold up first paint. Whoever needs it turns it on by flipping
    /// `media` — `Replay.mount` does, for the replay player's sheet.
    ///
    /// Only worth doing for a sheet that is BOTH sizeable and used by a minority of sessions.
    /// The app's own stylesheet is neither, and a page that deferred it would paint unstyled.
    let deferredHeadTags (styleSheetUrl: string) (hook: string) =
        sprintf "<link rel=\"stylesheet\" href=\"%s\" media=\"not all\" %s>" styleSheetUrl hook
