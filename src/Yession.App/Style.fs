namespace Yession.App

open Yession.Domain
open Yession.Domain.Terminals

/// The client's visual language, authored entirely in F# by composing Tailwind's own
/// utility classes into typed, named values. Tailwind supplies the utilities; F# supplies
/// the composition; the TOKENS — palette, type ramp, caps tracking, structural spacing,
/// fonts, keyframes — live in the `@theme` block of `app/tailwind.css`, and nothing here
/// carries a raw hex or a structural pixel count that has a token.
///
/// The design is Metro / Zune (pre-Windows 8) worn by a Slack/Cursor workspace anatomy.
/// Zune's own panorama — horizontal surfaces you pan between — was considered for the whole
/// shell and rejected: it is a media-browsing metaphor, and the job here is watching one
/// conversation, editing a queue, and intervening fast. The session is one room, and
/// navigation through it is vertical time, not horizontal space. Zune survives in the type,
/// the colour and the motion, and the pivot idiom survives at exactly one scale — the
/// sidebar's two destinations (`navPivot`), which are a place you go, not a surface you pan.
///
/// The rules that keep it coherent:
///
///   Type grid — everything sits on a 4px baseline rhythm, as paired size/line tokens:
///     label 11/16 · small 13/16 · body 15/24 · pivot 19/24 · heading 28/32 ·
///     wordmark 32/36 (px), plus the mono pair code 12/16 · code-sm 11/16.
///   The sidebar wordmark and the main header share one band (`h-band`, items-end,
///   common bottom padding) so their baselines align across the hairline.
///
///   Affordance — statuses are TEXT (colored caps, at most a small dot; never filled,
///   never boxed). Buttons are bordered Metro rectangles (transparent; hover brightens
///   the border; press fills solid). Nothing else carries a border.
///
///   Strokes — every border in the product is composed from the `Stroke` vocabulary
///   below (width, tone, and what interaction does to it) into a handful of phrases —
///   `field`/`fieldSelect`/`fieldBare`, `rowBase`/`rowLift`, `focusRing` — and those
///   phrases are what surfaces wear. No bare `border-*` utility is written outside
///   `Stroke`, and none at all in the views. The two remaining literals are variant-
///   PREFIXED (`md:[.nav-alt_&]:border-r-0`), undoing a column's divider while it is
///   shut: the variant is part of the class name, so there is no token to compose.
///
///   Colour — technocool: blue is interactive and the agent's voice; green is live/ok
///   and the human pulse. People are identified by tiny square display pics, not name
///   colours. The blue→green gradient appears exactly ONCE: the composer's focus edge.
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

    /// The same ring held further off — for type with no box of its own, where a ring on the
    /// glyphs' own edge reads as an underline.
    let private focusRingFar =
        "focus-visible:outline focus-visible:outline-2 focus-visible:outline-blue focus-visible:outline-offset-4"

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

    let wordmark = "font-extralight text-wordmark tracking-[-0.02em] text-ink"
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
    /// the composer's Send and Clear and by the interrupt above them — the three that stand
    /// on a band of their own with nothing competing for the width. At the label size a word
    /// button there read as a caption of the glyph it replaced rather than as the thing you
    /// press.
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
    let statusRun = caps + " text-blue"
    let statusErr = caps + " text-err"
    let statusFaint = caps + " text-ink-faint"
    /// The small leading dot a live status may carry (`bg-current` follows the text colour).
    let statusDot = "inline-block w-1.5 h-1.5 rounded-full bg-current mr-1.5 align-[1px]"
    let statusDotPulse = statusDot + " animate-pulse2 motion-reduce:animate-none"

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
    let private btnPrimaryFace = cls [ Stroke.blue; "text-blue hover:text-blue-bright pressed:bg-blue pressed:text-bg" ]

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
    /// Deliberately NOT an opacity dim: fading `text-blue` on the composer's `#111` takes it
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

    /// The borderless verb as a WORD rather than an icon, for a row whose verb has no glyph
    /// that says it (stop, unarchive). Same rule, same box height, same rest and hover tones
    /// as the icon form; the caps voice because that is what every button here speaks.
    let private btnBareBase =
        cls [ "h-6 px-1 shrink-0 inline-flex items-center bg-transparent border-0 cursor-pointer font-ui"; caps
              "transition-colors"; focusRing ]
    let btnBare = cls [ btnBareBase; "text-ink-faint hover:text-ink" ]
    let btnBareDanger = cls [ btnBareBase; "text-ink-faint hover:text-err" ]

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
    let private btnInField =
        cls [ "w-8 h-8 shrink-0 grid place-items-center bg-transparent border-0 cursor-pointer p-0"
              "transition-colors"; focusRing ]
    let btnSendInField = cls [ btnInField; "text-blue hover:text-blue-bright" ]
    /// Waiting for something to run. The same control in the same place, at the weight of a
    /// thing with nothing to do — never `disabled`, in either spelling: an empty command line
    /// is not a blocked one.
    let btnSendInFieldWaiting = cls [ btnInField; "text-ink-faint hover:text-ink" ]

    /// The composer's verbs, as WORDS — a Metro button with its border taken off.
    ///
    /// `SEND →` was once the word and an arrow saying one thing twice, in a 93px bordered box
    /// on a strip of its own; the arrow alone replaced it and said it once, correctly, while
    /// it rode the end of the line you had just written. It stopped riding that line when the
    /// verbs dropped onto a row of their own on a phone — and a row holding two glyphs and
    /// the rest of the screen is room the word was only ever given up for. So the word is
    /// back, at 13px caps, without the rectangle: the band IS the surface here, and a border
    /// round a control standing on it is the box this design spent three revisions removing.
    ///
    /// 40px tall, which is the composer's resting line exactly (`draftInput`: a 24px line in
    /// `py-2`) — so the pair bottom-aligns onto it with no correction, and `draftCommit`
    /// spends no `pb` to centre them.
    let private btnComposerWord =
        cls [ "h-10 px-3 shrink-0 inline-flex items-center bg-transparent border-0 cursor-pointer font-ui"
              capsLg; "transition-colors"; focusRing ]
    let btnComposerSend = cls [ btnComposerWord; "text-blue hover:text-blue-bright" ]
    /// Waiting for something to send. The same control in the same place, at the weight of a
    /// thing with nothing to do — never `disabled`, in either spelling: an empty composer is
    /// not a blocked one.
    let btnComposerSendWaiting = cls [ btnComposerWord; "text-ink-faint hover:text-ink" ]
    /// What the `✕` became. The glyph was a verdict on the draft — *discard* — drawn in the
    /// one mark a tab strip uses for *gone*; the word says what the press does to the line in
    /// front of you, which is the same act described from where the person is standing.
    let btnComposerClear = cls [ btnComposerWord; "text-ink-faint hover:text-err" ]
    /// Chrome, not an action: the small sidebar collapse/reveal chevrons. They lean the way
    /// they travel on hover and lead further on press — the only motion chrome earns, and the
    /// reason the two directions are separate values rather than one class plus a guess.
    let private navChevronBase =
        "bg-transparent border-0 cursor-pointer text-ink-faint hover:text-ink text-small p-1.5 -m-1.5 "
        + "flex items-center gap-1 transition-[translate,color] duration-150 ease-out "
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
    let touchType = "max-md:text-touch"

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
        cls [ "flex-1 min-w-0"; fieldBare; "font-terminal text-code text-ink placeholder:text-ink-faint"; touchType ]

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
        + "motion-reduce:transition-none " + focusRingFar

    let navPivot = pivotBase

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
    // Two-tone checkers in the blue/green family stand in until real avatars exist; the
    // variant is picked by hashing the peer id so identity is stable without name colours.
    // The checker hexes are deliberately NOT theme tokens: they are artwork constants, and
    // each class must appear as the same literal in the `@source inline` mirror in
    // app/tailwind.css — a var() inside would decouple nothing and complicate the mirror.

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
    let entityLink = cls [ entity; "text-blue underline decoration-1 underline-offset-2 hover:text-blue-bright"; focusRing ]
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

    let private checker (a: string) (b: string) =
        sprintf "bg-[conic-gradient(from_0deg,%s_25%%,%s_0_50%%,%s_0_75%%,%s_0)]" a b a b

    let private humanCheckers =
        [| checker "#1ba1e2" "#0b5d85"
           checker "#a8dd00" "#55700a"
           checker "#17c3b2" "#0a5c54"
           checker "#4ab8f0" "#1a6a96"
           checker "#7fb800" "#3d5a05" |]

    /// A stable checker for a human peer id.
    let humanAvatar (id: string) : string =
        let hash = id |> Seq.fold (fun acc c -> acc * 31 + int c |> abs) 7
        humanCheckers.[hash % humanCheckers.Length]

    /// The agent's mark: a dark square holding a small solid blue square.
    let agentAvatar =
        "bg-agent-ground grid place-items-center after:content-[''] after:w-2 after:h-2 after:bg-blue"

    let agentAvatarSm =
        "bg-agent-ground grid place-items-center after:content-[''] after:w-1.5 after:h-1.5 after:bg-blue"

    // --- Workspace regions ---------------------------------------------------------------
    // Two presentation bits live on the root <html> element, outside `#app`, so they survive
    // every re-render and stay out of the model: `nav-alt` (toggled by [data-nav-toggle]) and
    // `settings-open` (by [data-settings-toggle]). Default = sidebar visible on desktop,
    // off-canvas on mobile; `nav-alt` = the inverse. Expressed with arbitrary variants so it
    // stays plain Tailwind.

    // --- The degradation bar's one number ------------------------------------------------
    // On a phone the bar is FIXED above all three panes, so the panes leave room for it, so
    // its height and that room are the same number in three class strings. They cannot be
    // composed from a shared token: Tailwind generates only classes that appear LITERALLY in
    // the source, so `"max-md:h-" + n` produces a class the stylesheet never contains — which
    // fails silently, the bar falling back to its content height and the reservation to
    // whatever the last edit left. So the number is written out three times, here, together,
    // and `Phase4`'s theme suite fails if the three ever stop agreeing.

    /// The bar's own height on a phone.
    let degradedBarHeight = "max-md:h-12"
    /// The room a pane anchored to the top edge leaves for it (the two off-canvas overlays).
    let degradedBarRoom = "max-md:[.is-degraded_&]:top-12"
    /// The same room, paid in padding, by the column that is in normal flow.
    let degradedBarRoomPad = "max-md:[.is-degraded_&]:pt-12"

    /// The 280px column. It holds TWO faces — the workspace nav and settings (`navPane` /
    /// `settingsPane`) — because settings is a place you go, not a thing that covers what you
    /// were reading. Collapsing on desktop animates the column's width shut; on mobile the
    /// column is an off-canvas drawer that slides over the conversation.
    let sidebar =
        "relative w-side shrink-0 bg-panel h-full overflow-hidden z-40 " + Stroke.dividerRight + " "
        + "md:transition-[width] md:duration-200 md:ease-out "
        + "md:[.nav-alt_&]:w-0 md:[.nav-alt_&]:border-r-0 "
        + "max-md:fixed max-md:inset-y-0 max-md:left-0 max-md:w-[min(var(--spacing-side),84vw)] "
        + degradedBarRoom + " "
        + "max-md:transition-transform max-md:duration-200 max-md:ease-out max-md:-translate-x-[101%] "
        + "max-md:[.nav-alt_&]:translate-x-0 motion-reduce:transition-none"

    /// One face of the column: the two are stacked in place and held at the column's full
    /// width, so nothing reflows while the column animates shut.
    ///
    /// `visibility` is in the transition list on purpose — it is what keeps the hidden face out
    /// of the tab order and the accessibility tree, and transitioning it holds `visible` for the
    /// whole fade OUT (a discrete step at the end) while flipping instantly on the way IN.
    /// `opacity-0` alone would leave focusable controls behind an invisible panel.
    let private paneBase =
        "absolute inset-y-0 left-0 w-side max-md:w-[min(var(--spacing-side),84vw)] flex flex-col px-6 pb-5 "
        + "overflow-y-auto transition-[opacity,visibility] " + Motion.pace

    let navPane = paneBase + " [.settings-open_&]:opacity-0 [.settings-open_&]:invisible"

    let settingsPane =
        paneBase + " opacity-0 invisible [.settings-open_&]:opacity-100 [.settings-open_&]:visible"

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
    let scrim = "hidden max-md:[.nav-alt_&]:block fixed inset-0 z-30 bg-black/60"

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

    /// One chapter in the contents: the roster row's shape, worn by a button.
    ///
    /// The same row as a person's, because the column holds one kind of list and a second
    /// shape here would read as a second kind of thing. What it adds is what a control has to
    /// have: a name that brightens under the pointer, and the ring a keyboard sees. The
    /// padding is spent OUTWARD, so the text sits on the column's rail with everything else
    /// and the hover fill grows around it.
    let chapterEntry =
        cls [ person; "w-full text-left px-2 -mx-2 py-0.5 hover:text-ink hover:bg-surface-2"
              "transition-colors cursor-pointer"; focusRing ]

    /// Its mark, the same dot the rule in the timeline wears, so one chapter looks like one
    /// thing in both places.
    let chapterEntryDot = "w-1.5 h-1.5 rounded-full bg-ink-faint shrink-0 self-center"

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
    let toneBusy = "text-blue"
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
    let queryLegendEntries = "flex flex-col gap-2"
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
    /// Above `md` the stack stays: the header and the sidebar wordmark share one bottom edge
    /// (`h-band`, `items-end`), and that shared baseline is the whole reason the band exists.
    let header =
        "relative h-band shrink-0 flex items-end gap-4 px-8 pb-5 "
        + "max-md:h-14 max-md:items-center max-md:gap-2 max-md:px-4 max-md:pb-4 "
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
    let headerAside = "ml-auto shrink-0 flex items-end gap-5 pb-[1px] max-md:items-center max-md:gap-3 max-md:pb-0"
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
        "bg-transparent border-0 cursor-pointer " + caps + " transition-colors " + focusRing + " max-md:hidden"

    let prStripIn (tone: string) = prStripBase + " " + tone

    /// The agent's absence, FOLLOWING the surface that normally says it: shown only when the
    /// sidebar column (which holds the real call to action) is collapsed or off-canvas — which
    /// on a phone is most of the time. Never both at once, so it is a relocation, not a repeat.
    /// Same visibility rule as `navReopen`, for the same reason.
    let headerNoAgent =
        "bg-transparent border-0 cursor-pointer " + caps + " text-blue hover:text-blue-bright transition-colors "
        + focusRing + " "
        + "hidden md:[.nav-alt_&]:block max-md:block max-md:[.nav-alt_&]:hidden"

    /// The nav column's own mount of the connection report. Hidden on a phone, where the bar
    /// above every pane carries it — the two mounts are complementary by construction, so
    /// exactly one is ever on screen and the report is never read twice.
    ///
    /// It covers the STATUS only, never the reconnect card: the card is an action, and a
    /// phone that could see what was wrong but not the button that fixes it would be the
    /// worse half of the trade.
    let connectionInColumn = "max-md:hidden flex flex-col gap-2"

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
    /// One row that never wraps. `signInPrompt` below wraps because it may hold a sentence;
    /// this holds three short things — a status, a disclosure, and the way back — and its
    /// height is a number the panes reserve, so wrapping is the one thing it must not do.
    let degradedBar =
        cls [ "flex flex-nowrap items-center gap-3 px-8 py-2 bg-surface"
              Stroke.dividerBottom
              // Where the column IS on screen, the column says it. Where it is not — a
              // collapsed nav, or any phone — this does. Same rule as `headerNoAgent`.
              "hidden md:[.nav-alt_&]:flex max-md:flex"
              // A phone shows one pane at a time and the other two are overlays anchored to
              // the top edge, so this leaves the conversation column's flow and sits over all
              // three. `z-50` clears the overlays (`z-40`) and the scrim between them.
              "max-md:fixed max-md:inset-x-0 max-md:top-0 max-md:z-50 max-md:px-4"
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
    /// `flex-wrap` and `mr-auto` rather than a fixed row: the provider's own reason can be a
    /// sentence, and on a phone it has to be able to take the line above the button instead
    /// of squeezing it off the edge.
    let signInPrompt =
        "shrink-0 flex flex-wrap items-baseline gap-x-3 gap-y-2 px-8 py-2 bg-surface max-md:px-4 "
        + Stroke.dividerBottom

    /// The reason, taking the room between the status word and the button.
    let signInPromptReason = "mr-auto"

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
    /// (a 24px target with `-m-1.5` around it) plus the row's 8px gap, so 12px more is what
    /// puts the heading back on the 32px rail every message below it sits on — measured live
    /// at 390, where the title's text and the timeline's caret both start at x=48.
    /// `max-md:flex-1` is what makes the phone's band a row: the title takes the space the
    /// two chevrons leave rather than sizing to an input's default 20 characters.
    let titleWrap = "relative flex flex-col min-w-0 ml-8 max-md:ml-3 max-md:flex-1"

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
    let titleInput =
        cls [ "w-full min-w-0 bg-transparent border-0 px-2 -mx-2 py-0"
              "hover:bg-surface-2 focus:bg-surface-2 transition-colors"; focusRing
              "font-extralight text-heading max-md:text-pivot tracking-[-0.01em] lowercase text-ink"
              "placeholder:text-ink-faint truncate relative md:top-[2px]" ]

    /// The session id, shown small and dim under the title as a stable secondary identifier.
    /// It hangs OUT OF FLOW below the title, into the band's bottom padding: in flow it added
    /// 18px under the title inside the bottom-aligned stack and lifted the title's baseline
    /// that far off the wordmark's (measured 41.5 vs 61 at 1440) — and on a phone it is what
    /// made the band a stack rather than the row it now is.
    /// `mt-1` rather than the old `mt-0.5`: the title now draws a focus ring 2px outside its
    /// own box, and 2px of clearance is what keeps that ring off this line on a phone.
    let titleId =
        "font-terminal text-code-sm text-ink-faint truncate mt-1 absolute top-full left-0 right-0"

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

    /// The reopen chevron. Above `md` it is floated in the gutter left of the title so that
    /// collapsing the sidebar never shifts the heading off the content column. On a phone it
    /// is IN the row — the band is a line of chrome with the title in it, and a control
    /// hovering over that line would be the one thing on it that is not. It is a 24px target
    /// occupying 12px of flow (`-m-1.5`), which with the row's 8px gap pays 20 of the 32px
    /// indent the heading gives up there (`titleWrap`). Hidden while the sidebar is visible.
    let navReopen =
        "absolute left-2 bottom-4.5 w-6 h-6 place-items-center hidden md:[.nav-alt_&]:grid "
        + "max-md:static max-md:grid max-md:[.nav-alt_&]:hidden"

    // --- Timeline --------------------------------------------------------------------------

    /// The reading column. `break-words` is not typography, it is containment: a scroller on
    /// the vertical axis is a scroller on BOTH (`overflow-x` computes to `auto` beside an
    /// `auto` `overflow-y`), so anything that hangs out of this column takes the whole
    /// conversation sideways with it — under a header that stays put, which is what makes it
    /// read as a broken page rather than as a wide line. A phone column is ~326px and an agent
    /// says things like `/home/user/.yession/sessions/AAZFRYD.../repos`, so a token wider than
    /// the column is the ordinary case, not the pathological one. `overflow-wrap` inherits, so
    /// the rule is stated once for everything the timeline will ever hold.
    /// The leading gap is the first child's MARGIN, not this box's padding, and that is load
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
    let timeline =
        "flex-1 min-h-0 overflow-y-auto overflow-x-hidden px-8 pb-6 flex flex-col gap-6 [&>*:first-child]:mt-6 "
        + "max-md:px-4 max-md:pb-4 max-md:gap-5 max-md:[&>*:first-child]:mt-4 break-words"

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
    /// is the first child's margin rather than the scroller's padding, so `top-0` pins this
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
    let replyRefQuote = "truncate min-w-0 italic"
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

    let ask =
        cls [ "relative shrink-0 pt-6 pb-6 bg-surface"; Stroke.dividerTop ]

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
    let askBody = "flex flex-col overflow-x-hidden"

    /// Two panes, one box. The card asks one thing at a time and the second thing is to the
    /// RIGHT of the first, because that is where a thing you went INTO is — the Zune move,
    /// and the reason the way back is a chevron pointing the way the surface will go.
    ///
    /// The pane on screen is IN FLOW, so the card is exactly as tall as what is showing; the
    /// other sits absolute in the same box, pushed a full width aside — there to slide in,
    /// and contributing no height while it is not. Without that the card stands at the height
    /// of its TALLER pane always, with a grey void under whichever is shorter.
    /// `shrink-0` is load bearing: this is a flex child of a `max-h` scroller, so without it
    /// the track is COMPRESSED to the card's height and its `overflow-hidden` clips the rest
    /// of the list away — the rows below the fold stop existing rather than being scrolled
    /// to, and the foot that pages is never reachable. The clip is for the pane that is off
    /// to the side, and only that.
    let askTrack = "relative shrink-0 overflow-hidden"
    /// A pane is a COLUMN with two parts: what it asks, and what there is to answer with.
    /// Only the list scrolls - the question stays legible while a long one is read. START is
    /// not a third part of the pane: it is the one thing both panes mean the same way, so it
    /// sits below the TRACK rather than inside whichever pane is showing. A button that rode
    /// the pane would slide off with the one you just left and a second copy would slide in
    /// with the one you land on - two buttons where there is one, the same seam `Launch.anchor`
    /// closed for the card itself.
    let private askPaneBase =
        cls [ "min-w-0 flex flex-col max-h-[60vh] transition-transform"; Motion.paceLong ]
    /// The list, and the only thing in a pane that scrolls. `min-h-0` is what lets it: a flex
    /// child's floor is its content, so without it the column grows past its own `max-h` and
    /// the pane scrolls instead of the list inside it.
    let askScroll = "flex-1 min-h-0 overflow-y-auto"
    /// On screen. `min-w-0` so a long repo name in the subtitle truncates rather than widening
    /// the pane.
    let askPaneHere = askPaneBase + " translate-x-0"
    let askPaneLeft = askPaneBase + " absolute inset-x-0 top-0 -translate-x-full"
    let askPaneRight = askPaneBase + " absolute inset-x-0 top-0 translate-x-full"

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
    let askWay =
        cls [ "w-5 h-5 shrink-0 mt-1 grid place-items-center bg-transparent border-0 cursor-pointer transition-colors"
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
        cls [ caps; "text-blue hover:text-blue-bright transition-colors"
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
              "text-blue hover:text-blue-bright transition-colors"; focusRing ]
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
    let chatChip =
        cls [ "w-full bg-transparent cursor-pointer text-left"
              readingColumn
              "flex items-baseline gap-2 pl-[32px] py-0.5"
              "text-ink-dim hover:text-ink transition-colors duration-150 ease-out"
              focusRing ]
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
    /// The mark: a stop, in the error colour a failed chip's status wears. Colour is what
    /// carries the state in this set; the screen-reader word beside it carries it for
    /// anything that cannot see colour.
    let turnStopMark = "shrink-0 text-err"
    /// The reason, in the quiet small voice — and WRAPPING, unlike a chip's one line: a
    /// reason is the one thing here a reader must not lose the end of.
    let turnStopText = "font-light text-small text-ink-dim min-w-0"

    /// A turn's tool calls (Plan 16): a `<details>` on the same content column as the chips,
    /// so a chatty turn reads as one quiet line until somebody wants the detail.
    let chatToolRun = cls [ "w-full pl-[32px] py-0.5"; readingColumn ]
    /// Its summary. A real `<summary>` rather than a button, so the disclosure is the
    /// browser's and arrives keyboard-operable and correctly announced.
    let chatToolSummary =
        cls [ "flex items-baseline gap-2 cursor-pointer list-none"
              "text-ink-dim hover:text-ink transition-colors duration-150 ease-out"
              focusRing ]
    /// One call inside an expanded run: the tool it called, then how it went.
    let chatToolCall = "flex items-baseline gap-2 py-0.5"
    /// `namespace/name` — mono, because it is an identifier and reads as one.
    let chatToolName = "font-terminal text-code-sm text-ink-dim truncate min-w-0"
    /// The arguments as recorded. Dim and truncated: this is evidence, not content.
    let chatToolArgs = "font-terminal text-code-sm text-ink-faint truncate min-w-0 flex-1"

    /// A tool call and, under it, its disclosable answer. A block wrapper so the answer sits
    /// BELOW the line rather than in it — the line stays one row whether or not there is a
    /// result to open.
    let chatToolItem = "w-full"
    /// The answer disclosure — collapsed by default, aligned under the name. A result a
    /// reader opens, never a thing that fills the chat on its own.
    let chatToolResult = "pl-2 mt-0.5"
    /// "output ›" — the same faint label the rest of these chips use, and a pointer cursor so
    /// it reads as openable.
    let chatToolResultSummary =
        cls [ label; "inline-flex items-center gap-1 cursor-pointer select-none hover:text-ink transition-colors"; focusRing ]
    /// The answer itself: mono, wrapped, dim, and bounded — a preview that scrolls rather than
    /// a pane that grows. The `resultCap` upstream keeps the text small; this keeps a small
    /// text from still being a wall.
    let chatToolResultBody =
        cls [ monoOut; "mt-1 max-h-64 overflow-auto bg-surface-2 p-2 rounded" ]

    /// One agent burst (Plan 20, stage 4). A `<details>` on the same content column the chips
    /// and tool runs sit on, for the same reason: a turn that ran twelve commands reads as
    /// one line until somebody wants the twelve.
    let chatTaskCard = cls [ "w-full pl-[32px] py-0.5"; readingColumn ]
    /// Its summary. A real `<summary>`, so the disclosure is the browser's and arrives
    /// keyboard-operable and correctly announced.
    let chatTaskSummary =
        cls [ "flex items-baseline gap-2 cursor-pointer list-none"
              "text-ink-dim hover:text-ink transition-colors duration-150 ease-out"
              focusRing ]
    /// The counts, at the end of the summary line. Baseline-aligned with the sentence beside
    /// them so the glyphs sit on the text's line rather than floating above it.
    let chatTaskCounts = "flex items-baseline gap-2 shrink-0"

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
    let chapterRule =
        cls [ "relative flex items-center gap-2.5 border-t border-hair pt-3 mt-6 -mb-1 max-md:mt-4"
              readingColumn; "max-md:max-w-none" ]

    /// The mark on it: a dot at the reading edge, so a chapter has an anchor the eye finds
    /// on the way past. Decorative — the name beside it is what says which chapter this is.
    let chapterDot = "w-1.5 h-1.5 rounded-full bg-ink-faint shrink-0"

    /// The name, worn by a text input for the reason the session title is: it is editable
    /// text, and a control that only becomes editable once you have pressed it is a control
    /// nobody presses. The same arrangement as `titleInput` — transparent at rest, the
    /// surface lifting under the pointer and while focused, so the line becomes a box you are
    /// typing in exactly when you are typing in it.
    ///
    /// `truncate` rather than wrap: a rule is one line, and a name long enough to wrap has
    /// stopped being a name. `touchType` because a keyboard is coming, and a phone zooms into
    /// anything under 16px it focuses and never zooms back out.
    let chapterName =
        cls [ "flex-1 min-w-0 bg-transparent border-0 px-1.5 py-0.5"
              "hover:bg-surface-2 focus:bg-surface-2 transition-colors"; focusRing
              "font-ui font-light text-small text-ink-dim focus:text-ink truncate"
              touchType ]

    /// What a person can DO to one item, behind an ellipsis at its top-right.
    ///
    /// TOP rather than bottom, which is the one place on a message that does not move: an
    /// agent's message streams, so a control anchored to its foot slides down the screen for
    /// as long as the answer is arriving — away from the pointer reaching for it.
    ///
    /// Present but INVISIBLE until the pointer or the keyboard reaches the item: a control on
    /// every line of a conversation would be the loudest thing in it, and one that vanished
    /// from the tab order would be a control keyboard readers do not have. `opacity`, never
    /// `hidden`, is what keeps both true at once — and the item is `group/item`, so hovering
    /// anywhere on the message is what reveals it, not hovering the 20px it occupies.
    ///
    /// A device with no pointer never hovers, so on one it has to be on the screen or it does
    /// not exist. Half strength there — enough to find, not enough to become the loudest
    /// thing in a conversation.
    ///
    /// `[@media(hover:none)]` is now the WHOLE story on a phone, not a fallback beside one. A
    /// hold on the message used to open this menu as well, and that gesture is gone: every
    /// platform binds a long press on text to selecting that text, so the two were one finger
    /// meaning two things, and the reader lost the half only the platform can give. This
    /// being permanently visible is what made the gesture affordable to drop.
    /// `top-2 right-1` and not the corner it used to sit in: `top-2` is the ground's own top
    /// padding, so a 24px control there is centred on the 24px first line — the dots read as
    /// belonging to that line rather than floating above it.
    let itemActions =
        cls [ "absolute right-1 top-2 w-6 h-6 flex items-center justify-center"
              "bg-transparent cursor-pointer text-ink-faint hover:text-ink"
              "opacity-0 group-hover/item:opacity-100 focus-visible:opacity-100"
              "[@media(hover:none)]:opacity-60"
              "transition-opacity duration-150 ease-out"
              focusRing ]
    /// While its menu is open the control stays put: a menu hanging off something invisible
    /// reads as a menu hanging off nothing.
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
        cls [ "w-full text-left px-3 py-1.5 bg-transparent cursor-pointer"
              "text-small leading-5 text-ink-dim hover:text-ink hover:bg-surface"
              "transition-colors duration-150 ease-out"
              focusRing ]
    /// What a press ANYWHERE else lands on. A real button rather than a document listener:
    /// the listener would have to be added, removed and reasoned about against a view that
    /// re-renders, while this exists exactly as long as the menu does. Transparent and over
    /// everything below the menu.
    /// `select-none` because it is the size of the screen. A backdrop is chrome and holds no
    /// words, so nothing is lost by making it unselectable — and what is gained is that a
    /// viewport-sized element can never take a selection that was in flight when it mounted.
    let itemMenuBackdrop = "fixed inset-0 z-20 bg-transparent cursor-default select-none"

    /// A repo note in the timeline (Plan 14): one quiet act-line, indented past the
    /// avatar gutter so the reading edge lines up with message bodies.
    let actNote =
        cls [ itemGround; readingColumn; "pl-[32px] max-md:pl-12"; "flex flex-col gap-0.5" ]
    /// The pulse for an act in flight, sat in the LEFT gutter rather than trailing the line.
    /// The box spans exactly the margin the text clears (`pl-[32px]`, `pl-12` on a phone) and
    /// the first line's own height, so `justify-center`/`items-center` put the dot on the dead
    /// centre of both — the gutter across, the headline down — however wide the platform's
    /// gutter is. `top-2` matches `itemGround`'s `py-2`, so it sits on the first line even when
    /// a detail wraps below. Out of the text flow and unclickable; the reader's cue is the dot,
    /// the screen-reader's is the `sr-only` word it wraps.
    let actNoteRunning =
        cls [ "absolute left-0 top-2 h-5 w-8 max-md:w-12"
              "flex items-center justify-center text-blue pointer-events-none" ]
    /// The dot itself: the same size and pulse as elsewhere, but no inline margin or baseline
    /// nudge — those are for a dot that rides text, and this one is centred by its box.
    let actNoteRunningDot =
        "inline-block w-1.5 h-1.5 rounded-full bg-current animate-pulse2 motion-reduce:animate-none"
    /// Sentence case, deliberately. This wore the caps LABEL voice, and a label voice is for
    /// two or three words: `STARTED SANDBOX WORK (DOCKER), FORWARDING ANTHROPIC_API_KEY FROM
    /// ADA` is a line nobody reads, because uppercase flattens the word shapes a reader scans
    /// by and the tracking stretches one clause across the whole column. A label that has
    /// grown into a sentence is a sentence, and the timeline is prose.
    let actNoteText = "text-small leading-5 text-ink-dim"
    /// The particulars under the headline: the same size, one step fainter, so the pair reads
    /// as one act rather than as two lines about it.
    let actNoteDetail = "text-small leading-5 text-ink-faint"

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
    /// The fold an act's particulars sit behind, and the arrow that opens it.
    ///
    /// The arrow lives in the LEFT gutter, on the dead centre of the margin the text clears
    /// and of the headline's own line — the box `actNoteRunning` uses for its dot, so the two
    /// cues an act can wear sit on one spot. A real button, so it is a Tab stop with a ring
    /// and a name; faint at rest, because a column of acts should read as its titles, and
    /// brighter under the pointer or the keyboard, since it is the only way in. The chevron
    /// points ON at rest and turns DOWN when the particulars are open, at the page's one pace.
    let actNoteFold =
        cls [ "absolute left-0 top-2 h-5 w-8 max-md:w-12"
              "flex items-center justify-center cursor-pointer bg-transparent border-0 p-0"
              "text-ink-faint hover:text-ink transition-colors"; focusRing ]
    let actNoteFoldMark = cls [ "block"; Motion.turn ]
    let actNoteFoldMarkOpen = cls [ actNoteFoldMark; Motion.turned ]
    /// The particulars, unfolding beneath the title: grown, slid and faded in as one, at the
    /// page's pace, and folded back the same way. Inside, the rows the act lays out and —
    /// last — what the agent was told, in the detail voice, so a reference in it is drawn as
    /// it is drawn above.
    let actNoteFoldBody = Motion.unfold
    let actNoteFoldBodyOpen = cls [ Motion.unfold; Motion.unfolded ]
    let actNoteFoldBodyShut = cls [ Motion.unfold; Motion.folded ]
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
    let proseLink = "text-blue underline decoration-1 underline-offset-2 hover:text-blue-bright"
    let proseHr = "border-0 " + Stroke.dividerTop + " my-3"

    // --- Interrupt: one verb, docked over the composer ---------------------------------------
    // The agent's activity strip used to live here: a 48px band carrying a pulse, the words
    // "agent is responding", the turn's number and a bordered Interrupt. It said what the
    // streaming message's own meta line said one line above it, and what that message's caret
    // said in the same breath — one fact, three animated marks, a twelfth of a phone's screen
    // spent on the third of them. The control it carried was the only part that was its own.

    /// Where that control ended up: a band of its own again, holding the verb and NOTHING
    /// ELSE. That is the whole difference from the strip, and it is worth stating because the
    /// two look alike from a distance — what says a turn is running is still the caret in the
    /// timeline where the words are landing, and still the composer's live region for a
    /// reader the caret cannot reach. A band that holds one verb is a verb you can reach; a
    /// band that holds a bulletin is the strip coming back.
    ///
    /// It spent one revision at the LEADING edge of the composer's own line, mirroring Send.
    /// That put a destructive verb exactly where the cursor starts, and moved the line
    /// sideways every time a turn began. Above the line it interrupts nothing: the composer
    /// keeps its full width whether or not the agent is writing.
    ///
    /// `px-2` around the button's own `px-2` is the composer's `px-4` gutter, so the word
    /// starts on the same reading edge as the text under it.
    let interruptBand = "shrink-0 flex items-center px-2 pb-1"

    /// Ink at rest, err under the hand — the face every destructive verb here wears, and worn
    /// for the same reason rather than out of symmetry. Err AT rest is this product's tone for
    /// *something is wrong*, and nothing is: a turn running is the normal case, and a red word
    /// standing over it every time the agent speaks would say otherwise within a day. What
    /// makes this one findable is not its colour but that it is the only thing in its band.
    ///
    /// A step brighter than the faint verbs that ride a listed row (`btnBare`), though, and
    /// that difference is the same rule read the other way: those are faint because the ROW is
    /// the subject and they are a thing you can do to it. Here the verb IS the subject.
    let btnInterrupt =
        cls [ "h-8 px-2 shrink-0 inline-flex items-center bg-transparent border-0 cursor-pointer font-ui"
              capsLg; "text-ink-dim hover:text-err transition-colors"; focusRing ]

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

    /// On the phones this is for, the band is the last thing on screen and used to run flush
    /// to the bottom edge, under the thumb about to press it; the `max-md` clearance gives it
    /// room without touching desktop, where the band never meets an edge at all.
    ///
    /// ONE clearance, plain thumb room — the same `pb-4` the terminal's command band spends
    /// for the same reason. It briefly had a second, wider one for when the verbs' row was
    /// showing; the row carries its own `pt-1` and its own height, so the band was paying
    /// twice for one gap, and the wider number only ever arrived while a state elsewhere in
    /// the file happened to agree with this one.
    let composer = composerBand + " max-md:pb-4"

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
    /// every send button a person has ever used lives — with discard as its quiet neighbour
    /// and whoever is typing beside them.
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
    /// out with the glyphs (`btnComposerWord`, above, is the line's own height).
    ///
    /// On a phone this row leaves the line entirely: it wants the full width
    /// (`max-md:w-full max-md:justify-end`, its buttons pushed to the trailing edge the
    /// way they sit on desktop) and it sits BELOW the text (`draftBox`'s `max-md:flex-col`
    /// puts it there in document order). A row that was always there spent a band of every
    /// phone screen on two controls a thumb reaches once per message, so it comes and goes
    /// — and WHAT it comes and goes with is the whole of this bug's story.
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
    /// something for it to do (`draftCommitReady`), which is the rule Clear already followed
    /// on its own and the rule Send's two faces are already computed from. Nothing about a
    /// press can retract it, because a press cannot empty the draft before the press lands.
    /// An empty composer still gives the room back, which is what the coming-and-going was
    /// for; it simply no longer offers two controls with nothing to act on.
    ///
    /// GONE means `max-h-0` beside the fade, not the fade alone. `opacity-0` hides a row and
    /// keeps every pixel of its height, so the band under an empty composer carried a 44px
    /// row of invisible buttons plus the clearance meant to sit below them — two thirds of a
    /// collapsed composer, and a gap no markup test can tell from an empty one.
    let private draftCommitBase =
        cls [ "shrink-0 flex items-center gap-1 pr-1"
              "max-md:w-full max-md:justify-end max-md:overflow-hidden"
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
    let draftAuthor = "pl-4 pt-2 " + caps + " text-ink-faint truncate"

    // A draft nobody has open here: one line of it, so the composer reads as "what is being
    // written" rather than a stack of boxes. Clicking it opens it (and closes whatever was).
    //
    // Its leading edge is the AUTHOR'S colour (set inline, from `EditorColour`) — the same
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

    /// Who is in this draft right now: one dot per live caret, coloured by peer (`EditorColour`).
    let draftEditors = "shrink-0 flex items-center gap-1 pr-1"
    let draftEditorDot = "inline-block w-1.5 h-1.5 rounded-full"

    /// Starts your own draft, collapsing whoever's is open — the escape hatch from joining.
    let draftNew =
        "self-end bg-transparent border-0 cursor-pointer px-4 py-2 " + caps
        + " text-ink-faint hover:text-blue transition-colors " + focusRing

    // --- Settings ------------------------------------------------------------------------------
    // Settings is the column's other face, not a drawer over the conversation: you go there and
    // come back, and the thing you were reading never moves. Its open state is one bit on the
    // root <html> element (`settings-open`, toggled by [data-settings-toggle]).

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
    // right-aligned status slot — so connecting an agent flips "no agent" to "ready" in
    // place; nothing moves and no box appears or collapses. (It used to be a boxed card,
    // which broke the roster's geometry and made the connect moment a layout jump.) The
    // prompt hangs beneath the row, on the roster's text column.

    let noAgentBlock = "flex flex-col gap-2"
    /// The prompt reuses the roster's own grid — a 20px avatar column and the text column,
    /// with the roster's 10px gutter — so the edge centres under the avatar and the text
    /// lands on the text column BY CONSTRUCTION, not by pixel arithmetic.
    let noAgentPrompt = "grid grid-cols-[20px_1fr] gap-x-2.5"
    /// The edge itself: the product's 2px edge width, in the agent's blue, centred in the
    /// avatar column and spanning the prompt's height.
    let noAgentEdge = "w-0.5 justify-self-center bg-blue"
    /// The prompt's text column: the explainer over its one action.
    let noAgentBody = "flex flex-col gap-2"
    /// Full-width within the column so it reads as the section's one action.
    let noAgentAction = "w-full"

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
    /// token is its default. 420px was picked as "the width the content actually has" and is
    /// 20 columns short of the 80 a terminal prints; rather than guess a better number for
    /// everybody, the split is draggable and remembered (`PaneShell.installPaneResize`). The
    /// transition is suppressed while dragging, or the column chases the pointer a frame late.
    let terminalPanel =
        "relative w-term md:w-[var(--term-w,var(--spacing-term))] shrink-0 bg-panel h-full overflow-hidden z-40 flex flex-col "
        + Stroke.dividerLeft + " "
        + "md:transition-[width] md:duration-200 md:ease-out md:[.term-resizing_&]:transition-none "
        + "md:[.term-closed_&]:w-0 md:[.term-closed_&]:border-l-0 "
        + "max-md:fixed max-md:inset-y-0 max-md:right-0 max-md:w-full max-md:border-l-0 "
        + degradedBarRoom + " "
        + "max-md:transition-transform max-md:duration-200 max-md:ease-out "
        + "max-md:[.term-closed_&]:translate-x-[101%] motion-reduce:transition-none"

    /// Held at the column's full width so nothing reflows while the column animates shut.
    let terminalPane = "absolute inset-0 md:w-[var(--term-w,var(--spacing-term))] w-term max-md:w-full flex flex-col"

    /// The split between the chat and this column, made draggable — a real `separator`, so it
    /// answers to the arrow keys as well as the pointer. Desktop only: on a phone the pane IS
    /// the column and there is nothing to divide.
    ///
    /// Inside the panel rather than straddling the divider, because the panel clips its
    /// overflow — which also rules out an outline for focus, so focus is the same blue the
    /// hover shows, at full strength.
    let terminalResize =
        cls [ "max-md:hidden absolute left-0 inset-y-0 w-1.5 z-50 cursor-col-resize"
              "bg-transparent hover:bg-blue/50 focus-visible:bg-blue focus-visible:outline-none"
              "transition-colors motion-reduce:transition-none" ]

    /// The column's head: a PROPERTIES BAR, not a title.
    ///
    /// It used to be the 88px band the sidebar and the main header wear, carrying the word
    /// "terminals" — the largest text on a phone screen, telling a reader looking at terminals
    /// that these are terminals, for 10% of the height. What a reader cannot otherwise know is
    /// which terminal this is and whether the agent has to ask before it runs, and neither had
    /// anywhere to live: the second was a labelled form control directly above the command
    /// line, in the highest-attention position on the surface, for a setting changed twice a
    /// session.
    ///
    /// So: 40px, at BOTH breakpoints, holding what this terminal IS plus the acts that are
    /// about the terminal rather than about the command you are typing. The band token is not
    /// missed — a band is for a heading, and this is a readout.
    let terminalHead = "h-10 shrink-0 flex items-center gap-2 px-3 " + Stroke.dividerBottom
    /// Which terminal this is. The one thing in the bar that is neither a fact you can change
    /// nor an act — so it is the only thing in ink.
    let terminalHeadName = "flex-1 min-w-0 truncate font-ui text-small text-ink"

    /// A property of the terminal, stated as a fact and changed by touching the fact.
    ///
    /// An act that is about THIS TERMINAL rather than about the command you are writing:
    /// closing it, stepping back through its recording. In the bar's voice — quiet text — not
    /// as another bordered rectangle in a strip already full of them.
    let private terminalBarActBase =
        cls [ caps; "bg-transparent cursor-pointer shrink-0 px-1.5 py-1 transition-colors"
              Stroke.clear; focusRing ]
    let terminalBarAct = cls [ terminalBarActBase; "text-ink-faint hover:text-ink" ]
    /// The acts, grouped and set apart from the facts beside them. Same tone, same voice —
    /// what separates them is the gap, because a bar of eight evenly spaced words reads as one
    /// run-on and a reader has to parse it to find the verb.
    let terminalBarActs = "flex items-center gap-1 ml-3"
    /// Closing a terminal kills what is running in it. It reddens under the hand — the same
    /// promise the danger button makes, kept without the rectangle.
    let terminalBarDanger = cls [ terminalBarActBase; "text-ink-faint hover:text-err" ]

    /// The open-terminal strip: one chip per terminal, scrolling horizontally when there are
    /// more than fit rather than wrapping into a second band that shifts the whole column.
    ///
    /// The tabs run down to the strip's own divider so their marks land on it rather than
    /// drawing a second line above it (see `tabBase`). `pb-px` is the one pixel their pulled-up
    /// borders occupy: this strip scrolls, and anything painted outside a scroll container's
    /// padding box is something to scroll TO — a 1px overhang made the row scrollable on the
    /// vertical axis (`overflow-x` forces `overflow-y` to `auto`) and Chromium drew a scrollbar
    /// down the side of the tabs for it.
    let terminalTabs = "shrink-0 flex items-stretch gap-1 px-3 pt-2 pb-px overflow-x-auto " + Stroke.dividerBottom
    /// The tabs THEMSELVES, and nothing else. `role="tablist"` is a promise about what its
    /// children are, and the strip also holds two things that are not tabs — "+ new" and
    /// "close" — which a reader was told were tabs (four of them, in a list of two) and which
    /// the strip's own arrow-key walk had to step over. The row keeps its look; the promise
    /// now covers only what keeps it.
    ///
    /// A real box rather than `display: contents`: a role on a contents box is dropped from
    /// the accessibility tree by some browsers, which would trade one wrong tablist for no
    /// tablist at all. `shrink-0` keeps the strip the thing that scrolls when the tabs
    /// outgrow it, exactly as when they were its direct children.
    let terminalTabList = "flex items-stretch gap-1 shrink-0"
    /// A tab is marked by an UNDERLINE, not by a box.
    ///
    /// The selected one used to be a full blue rectangle — which in this design is the button
    /// vocabulary, and specifically the primary one: `btnPrimary` is a blue-bordered rectangle
    /// wearing blue text. So the strip's answer to "which terminal am I looking at" shouted at
    /// exactly the weight of the page's one real call to action, next to the thing you actually
    /// press. Nothing in a tab strip is a CTA; a tab is a statement about where you are.
    ///
    /// The mark is `Stroke.underline`, the same hairline the session title wears — an
    /// established, chrome-light affordance in this vocabulary rather than a new width. Every
    /// tab carries the border box (`Stroke.clear` when it is not the one), so selecting moves
    /// no text, and the selection is said twice quietly instead of once loudly: ink rather than
    /// faint, over a blue rule.
    ///
    /// And it is the strip's OWN rule, not a second one. `-mb-px` pulls each tab's bottom
    /// border down onto the divider the strip already draws, so there is one line under the
    /// row and the selected tab paints its segment of it: an unselected tab's border is
    /// transparent and the hairline shows straight through. Drawn at the tab's own height
    /// instead, it was a second rule 23px above the first — two horizontal lines saying one
    /// thing, which is the ornament this pass exists to remove.
    let private tabBase =
        cls [ caps; "bg-transparent cursor-pointer px-2.5 pt-1.5 pb-2 -mb-px max-w-40 truncate transition-colors"
              Stroke.underline; focusRing ]
    let terminalTab = cls [ tabBase; Stroke.clear; "text-ink-faint hover:text-ink" ]
    let terminalTabActive = cls [ tabBase; Stroke.blue; "text-ink" ]
    /// Adds a terminal. The one action in the strip that is not a selection — so it wears the
    /// strip's own quiet face and says `+`, rather than being the bordered rectangle that
    /// out-shouted every tab beside it. What it does is named for anyone not reading pixels.
    ///
    /// The strip's other non-tab, `close`, is gone from here entirely: it kills a running
    /// terminal, and dressing that as a sibling of "switch to this one" put the most
    /// destructive control in the pane one pixel from the most routine. It is an act about
    /// the terminal, and it lives with the terminal's other properties, in the bar.
    let terminalTabNew = cls [ tabBase; Stroke.clear; "text-ink-faint hover:text-ink" ]
    /// A tab's presence marks: one dot per peer whose caret is in THAT terminal, so a
    /// collaborator typing a command in a terminal you are not looking at is visible from
    /// the strip rather than only from inside it.
    let terminalTabPeers = "inline-flex items-center gap-0.5 ml-1.5 align-[1px]"

    /// The pin is a MARK now, not a control (Plan 20, stage 1 revised).
    ///
    /// It used to be a second button beside every keepable tab, wearing one of two faces so
    /// that a glance could tell pinned from not. But a strip of tabs each trailing its own
    /// button is a strip of two controls per terminal, and on a touch screen the quiet one
    /// was a 24px target beside a 30px one. The gesture replaced it: activating the tab you
    /// are ALREADY on is the toggle, which every pointer, finger and keyboard already has.
    ///
    /// So this says one thing — that the tab is kept — and only when it is true. Blue,
    /// exactly as the selected tab's rule is blue, because both mean "this is mine and it
    /// stays". Nothing here destroys anything, so nothing here wears the danger tone.
    let paneTabPinMark = "ml-1.5 text-blue"

    /// The pane's body — whatever the selected tab shows. It takes the column's remaining
    /// height so the thing inside it scrolls rather than the column.
    let paneBody = "flex-1 min-h-0 flex flex-col"
    /// A read-only region: a player's own mount, or text shown rather than typed into. The
    /// same scrolling box the block history uses, so a block read from the chat looks exactly
    /// like the block read in its terminal — without the bottom anchoring, because a player
    /// is one child and belongs at the top of its region.
    ///
    /// One name for one box. It had two, and the second was reached for by whichever surface
    /// its author happened to be reading.
    let paneReadonly = "flex-1 min-h-0 overflow-y-auto flex flex-col gap-3 px-3 py-3"
    /// A stretch tab's facts, above whatever renders its recording.
    let paneFacts = "shrink-0 flex flex-col gap-1 px-3 py-3 " + Stroke.dividerBottom
    /// A read-only tab's verbs, under whatever it is showing: the way to the recording, and
    /// the way back. A row rather than a column, because they are alternatives to each other
    /// rather than a list of facts.
    let paneActions = "shrink-0 flex items-center gap-2 px-3 py-3 " + Stroke.dividerTop

    // --- The terminal list (Plan 20, stage 0) --------------------------------------------

    /// The list's scroll box. It takes the pane's whole body, because the list IS the body
    /// while it is showing — not a drawer over a terminal, which would leave two surfaces
    /// arguing about which one the reader is in.
    let terminalListBody = "flex-1 min-h-0 overflow-y-auto flex flex-col"

    /// One row: state, name, verbs. A grid rather than a flex row so the names line up down
    /// the list whatever their state marks are — a ragged left edge is what makes a list of
    /// twenty read as twenty unrelated things.
    let terminalListRow =
        "grid grid-cols-[auto_1fr_auto] items-center gap-2 px-3 py-2 " + Stroke.dividerBottom

    /// The row's own control: its name, which opens it. Ink at rest so the list reads as a
    /// list of names, blue under the pointer because that is what interactive means here —
    /// the same reasoning `recordLink` carries, at the list's size.
    let terminalListName =
        cls [ "bg-transparent cursor-pointer text-left w-full truncate p-0 font-ui text-body text-ink"
              "no-underline hover:text-blue transition-colors"; focusRing ]

    /// A closed row's name. The recording is still worth opening, and the row says which
    /// half of the list it is in by its tone rather than by repeating the word "closed" —
    /// the play mark beside it is what it IS.
    let terminalListNameClosed =
        cls [ "bg-transparent cursor-pointer text-left w-full truncate p-0 font-ui text-body text-ink-dim"
              "no-underline hover:text-blue transition-colors"; focusRing ]

    /// The row's verbs, kept on one baseline at its right edge.
    let terminalListVerbs = "flex items-center gap-1 shrink-0"

    /// The list's own empty state: the same idle prompt the empty pane wears, because a
    /// session with no terminals is one fact however you arrive at it.
    let terminalListEmpty = "flex-1 flex flex-col items-center justify-center gap-3 px-6 text-center"

    /// The block history's scroll box, and the stream inside it.
    ///
    /// A terminal grows DOWNWARD from the top and the viewport rides the tail. Those are two
    /// different statements and only the second one is about the bottom: with a long history
    /// the newest line does sit at the bottom edge, because the scroller is pinned there
    /// (`keepSurfacesPinned`), not because the content is.
    ///
    /// `mt-auto` on the stream said the first statement as if it were the second, and with a
    /// short history it showed: two lines pinned to the floor under 500px of void, measured on
    /// a phone. No terminal has ever looked like that. `relative` so the way back to the live
    /// edge can float over this box rather than take a band from it.
    let terminalScrollback = "relative flex-1 min-h-0 overflow-y-auto flex flex-col px-3 py-3"
    let terminalStream = "flex flex-col gap-2"

    /// One block: the command that ran, then everything it printed.
    ///
    /// It is not a card. A card per command turned a scrollback into a stack of boxes —
    /// borders, fills and 12px of padding repeated around every two lines of mono, which is
    /// the one thing a terminal does not look like. What separates two blocks is what
    /// separates them in a terminal: a green prompt glyph, the command in ink, its output
    /// dim beneath, and a line of air.
    let terminalBlock = "flex flex-col"
    /// The command line as it was run — and the block's disclosure: pressing it opens the
    /// facts (who ran it, who let it, how it ended) that used to be printed beside every
    /// command whether anyone wanted them or not. A real `<summary>`, so the disclosure is
    /// the browser's and arrives keyboard-operable and correctly announced.
    let terminalBlockCommand = "flex items-baseline gap-2"
    let terminalBlockSummary =
        cls [ "group"; terminalBlockCommand; "cursor-pointer list-none"
              "hover:text-ink transition-colors duration-150 ease-out"; focusRing ]
    /// The mark at the end of the line: an ellipsis, because what it hides is the rest of
    /// the sentence. `group-open:` turns it while the facts are showing.
    let terminalBlockMark =
        "ml-auto shrink-0 font-terminal text-code-sm text-ink-faint select-none "
        + "group-hover:text-ink transition-colors duration-150 ease-out motion-reduce:transition-none"
    /// The facts themselves, on the output's column so they read as an aside to the command
    /// rather than as more output.
    let terminalBlockFacts = "flex flex-wrap items-baseline gap-x-4 gap-y-0.5 pl-4 py-1"
    let terminalBlockFact = caps + " text-ink-faint"

    let terminalPrompt = "shrink-0 font-terminal text-code text-green select-none"
    let terminalCommandText = "font-terminal text-code text-ink break-all"
    /// Output: preformatted, wrapping, and horizontally scrollable for the lines that will
    /// not wrap — the column must never make the PAGE scroll sideways. No padding of its
    /// own: it sits directly under its command, on the scrollback's own gutter, the way a
    /// terminal prints.
    let terminalOutput = "overflow-x-auto font-terminal text-code-sm leading-4 whitespace-pre-wrap break-words text-ink-dim"
    let terminalOutputEmpty = small
    /// The truncation notice: a stated gap in the record, in the error voice because a
    /// missing audit trail is not a neutral fact.
    let terminalTruncated = caps + " shrink-0 px-3 py-2 text-err"

    /// The live screen (Plan 14, stage 6). Monospaced, preformatted, and scrollable in both
    /// axes — a terminal's lines are as wide as the program made them, and wrapping them
    /// would redraw a screen the program laid out. The focus ring matters more here than
    /// anywhere: this is the one surface whose whole purpose is having the keyboard.
    let terminalScreen =
        cls [ "flex-1 min-h-0 overflow-auto px-3 py-2 font-terminal text-code-sm leading-4"
              "whitespace-pre text-ink bg-bg"; focusRing ]

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

    /// Coming back is TRANSIENT — it exists only while you are behind the live edge — and it
    /// is about where you are in the scroll, so it floats over the scroller. The same slot
    /// every chat client puts "jump to latest" in, for the same reason.
    let terminalLiveFloat =
        cls [ "absolute right-3 bottom-3 z-10 flex items-center gap-3 px-3 py-2 bg-surface"
              Stroke.ring; Stroke.rim ]
    /// The rewound read is a player, not a scroller, so it has no scroll box of its own to
    /// float over — this is the positioned region the way back hangs in.
    let terminalReplayRegion = "relative flex-1 min-h-0 flex flex-col"

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
    /// clearance is its own, though (`max-md:pb-4`, plain thumb room): Run stays on the
    /// command line rather than dropping below it the way Send and Clear do, so this band
    /// never needs their room — which is also the clearance the message composer falls back
    /// to when its own row is not showing (`composer`, above).
    let terminalComposer = composerBand + " max-md:pb-4"

    /// A row in the band that is not the command line — the lease bar, the "not marking"
    /// notice. They used to inherit the section's padding; the band has none.
    let terminalBandRow = "flex items-center gap-2 px-3 py-2"

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
    let terminalCommand =
        cls [ "w-full"; fieldBare
              "font-terminal text-code text-ink px-3 py-3 pr-10 placeholder:text-green"; touchType ]
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
    let terminalQueuedRow = "flex items-center gap-2"
    /// Someone else's composer slot in this terminal: shown, not editable-by-mistake — it is
    /// the same live text, so it is the terminal's version of watching a draft being written.
    /// Its leading edge is the author's own colour, set inline.
    let terminalPeerDraft = cls [ "items-center gap-2 px-3 py-2"; rowBase ]
    /// Who is in a slot right now, by live caret — one dot per peer, coloured by peer.
    let terminalEditors = "shrink-0 flex items-center gap-1"

    /// The empty state, when no terminal is open.
    let terminalEmpty = "flex-1 flex flex-col items-center justify-center gap-3 px-6 text-center"

    /// Reopens the column once it is shut — the mirror of the sidebar's reopen chevron,
    /// leaning the way the column travels. Rendered from the model rather than hidden by a
    /// variant: whether the control exists is a fact about the model, and a button that is
    /// merely invisible is still in the tab order.
    let terminalReopen = navChevronBack + " shrink-0"

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

    /// Tailwind, built locally into a stylesheet and served by both the Session Process and
    /// the Manager UI — never a CDN (local first). The utilities and the theme tokens come
    /// from the CLI build over `app/tailwind.css`, whose `@source` rules scan the F# sources
    /// for the composed class names.
    ///
    /// Takes the URL rather than building it: the stylesheet is addressed by a digest of its
    /// own bytes, which only the serving process (having read them) can know.
    ///
    /// The colour scheme is said HERE, in a meta tag AHEAD of the link, and not only in the
    /// sheet (`tailwind.css` paints the ground on `html`). The sheet governs the document once
    /// it has arrived; the interval before that is the browser's, painted in its canvas
    /// colour — and WebKit commits a navigation by tearing the old page down and showing that
    /// canvas until the new document's first paint, which a render-blocking stylesheet holds
    /// back for as long as it takes to fetch. A canvas the sheet is going to make black is
    /// white until the sheet is there to say so. The meta tag is parsed with the head, before
    /// any fetch, so it is the one statement that reaches the canvas in time: with it, every
    /// document is dark from commit. Photographed on iOS Safari as a white screen between
    /// pressing Create and the session's own address — the `/open` page, a new origin for
    /// the shell, each with its sheet still on the wire.
    ///
    /// One place, because there is one interval and it happens on every document: the
    /// Manager page used to carry this tag itself, and it was taken out as a duplicate of the
    /// sheet's `color-scheme` (#127) — which it is not, for exactly the milliseconds that
    /// matter. Beside the link it precedes, it cannot be dropped from one page and kept on
    /// another.
    let headTags (styleSheetUrl: string) =
        sprintf "<meta name=\"color-scheme\" content=\"dark\"><link rel=\"stylesheet\" href=\"%s\">" styleSheetUrl

    /// A stylesheet the page may never need: linked, so its address is the server's to state
    /// and the browser may fetch it whenever it likes, but `media="not all"` so it matches
    /// nothing and cannot hold up first paint. Whoever needs it turns it on by flipping
    /// `media` — `Replay.mount` does, for the replay player's sheet.
    ///
    /// Only worth doing for a sheet that is BOTH sizeable and used by a minority of sessions.
    /// The app's own stylesheet is neither, and a page that deferred it would paint unstyled.
    let deferredHeadTags (styleSheetUrl: string) (hook: string) =
        sprintf "<link rel=\"stylesheet\" href=\"%s\" media=\"not all\" %s>" styleSheetUrl hook
