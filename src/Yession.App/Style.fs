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
/// The anatomy is a Slack/Cursor workspace: a sidebar, a conversation, a pane. The session is
/// one room, and navigation through it is vertical time, not horizontal space. The pivot idiom
/// survives in one place, the content pane's row of what it holds (`panePivotRow`), which IS a
/// set of surfaces side by side, one at a time, which is what a pivot was for.
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
    //            28px type reads as a box rather than a field. It has exactly one wearer
    //            (`titleWrap`, round `titleInput`), which is what keeps it a shape; a second would make it a
    //            variant, and principle 5 does not admit one.
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
        /// A ring that paints nothing. It holds the border BOX so a bare control and a
        /// bordered one are the same size, and choosing between them moves no text. This was
        /// deleted as dead vocabulary while this file had no wearer for it (#1372, finding
        /// S1-34); the filled and bare faces below are its wearers, so it is live again. A
        /// bare `border-transparent` at those sites would be the bare utility this module
        /// exists to stop.
        let clear = "border-transparent"
        /// The one SHAPE in this vocabulary rather than a width: the bottom edge alone, for the
        /// session title (`titleWrap`, round `titleInput`) — a heading that edits in place, where a rectangle drawn
        /// round 28px type reads as a box rather than a field. `border-0` first, so the shape is
        /// stated and not inherited from whatever the UA gives an `<input>`.
        ///
        /// ONE wearer, and that is the whole of what keeps it a shape and not a third width
        /// (principle 5). `askSearch` wore it once and wears `field` now, which is why the
        /// comment there says a second wearer would make this a variant.
        let underline = "border-0 border-b"

        // Tones at rest. `hair` is the quiet separator, `rim` a step brighter for a
        // control at rest; the rest carry the meanings they carry everywhere else.
        let hair = "border-hair"
        let rim = "border-edge"
        let faint = "border-ink-faint"
        let blue = "border-blue"
        let green = "border-green"
        /// The tone that follows the TEXT colour, for a stroke whose voice composes at the use
        /// site rather than here — the hollow mark (`statusDotHollow`), drawn in whatever ink
        /// the status around it is speaking in.
        let current = "border-current"

        // What a RING does under interaction. `focus:` fires on a control that takes focus
        // ITSELF — which is every ring in the product, because a host whose real input is a
        // descendant (a mounted ProseMirror) draws no ring at all: it wears `fieldBare` and
        // lets its container carry the signal.
        let hoverRim = "hover:border-edge"
        let hoverInk = "hover:border-ink"
        let hoverErr = "hover:border-err"
        let focus = "focus:border-blue"

        /// The acrylic's rim: a hairline on the ONE side a floating surface meets what it
        /// covers, in the tint's own edge tone (`--color-acrylic-edge`) rather than a grey,
        /// because what is under it shows through and a neutral would read as a painted edge.
        /// The only stroke whose tone is off the neutral ramp, and the reason `acrylic` below
        /// composes a name from here rather than writing its own `border-*`.
        let acrylicTop = "border-t border-acrylic-edge"
        /// The same rim on the BOTTOM, for a band pinned to the top of a scroller: what it
        /// covers scrolls up under its lower edge. Worn only while pinned (`stuck:`), on the
        /// band's ground (`before:`), because that is the only time anything is under it.
        let acrylicBottomPinned = "stuck:before:border-b stuck:before:border-acrylic-edge"

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

    /// The same ring worn by a `<label>` for the file input inside it (`pickerInput`). The
    /// input keeps the keyboard, because Tab reaches a real file input and Enter or Space on
    /// it opens the browser's chooser with no script at all; but the input has no box anyone
    /// sees, so the label it sits in draws the ring for it while it has focus.
    let focusRingHeld =
        "has-focus-visible:outline has-focus-visible:outline-2 has-focus-visible:outline-blue has-focus-visible:outline-offset-2"

    /// `focusRingHeld`, drawn just inside the label's edge, as `focusRingInset` is.
    let focusRingHeldInset =
        "has-focus-visible:outline has-focus-visible:outline-2 has-focus-visible:outline-blue has-focus-visible:-outline-offset-2"

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
        cls [ "bg-acrylic backdrop-blur-acrylic backdrop-saturate-150"
              Stroke.acrylicTop
              "reduced-transparency:bg-panel reduced-transparency:backdrop-blur-none"
              "reduced-transparency:backdrop-saturate-100" ]

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
        /// The quick pace: 150ms. What a change of TONE takes — a hover brightening its ink,
        /// a mark turning in place — where nothing moves far enough for the pace to be read
        /// as travel. Named because the file spelled it by hand at a dozen sites, each of
        /// which could have drifted on its own (principle 5: one way per job).
        let quick = "duration-150 ease-out motion-reduce:transition-none"

        /// The lane stagger: what a column of lanes arriving one after another is spaced by,
        /// so a drawer's rows land in reading order rather than all in one frame.
        ///
        /// Literal class names, not a computed `sprintf "delay-[%dms]"`: Tailwind's scanner
        /// reads this SOURCE, so a composed delay is a class name no stylesheet contains.
        /// That is also why the same two delays worn under a VARIANT (`[.settings-open_&]:`)
        /// cannot compose these — the variant is part of the class name, so there is nothing
        /// to compose, exactly as for the two border literals this file's header names.
        let lane1 = "delay-[60ms]"
        let lane2 = "delay-[120ms]"
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

    // --- Typography (the ramp lives as `--text-*` tokens in app/tokens.css) -------------
    // Each `text-<step>` utility sets size AND line-height together, so a size can never
    // drift off its 4px line box. A `leading-*` over a step is for a context that genuinely
    // wants a different box — a draft summary clamped to one 32px line — and never for one
    // the ramp already has: 13/20 is the `text-note` STEP, so `text-small leading-5` is a
    // step restated as an override, which is a size off the ramp by another route
    // (docs/visual-design.md, Type). The sites still spelling it that way are outside this
    // section; the step they want is `text-note`.
    //
    // Tracking is the same rule for the same reason: `tracking-display` and
    // `tracking-wordmark` are steps, so no face hand-writes an em value.

    /// The wordmark: `yession`, at 400 rather than the 200 it was drawn at, because it no
    /// longer stands alone — beside the mark (`lockup`) the extralight read thin, and the
    /// full stop that was its one colour is the mark's job now (assets/logo/lockup.svg).
    let wordmark = "font-normal text-wordmark tracking-wordmark text-ink"
    /// The mark and the wordmark on one line, the lockup's own proportions scaled to the
    /// band: the mark 56px to the word's 32 (the lockup file draws 64 to 32, which the
    /// band has no room for), its foot 15px below the word's line box so the x-height band
    /// sits where the lockup puts it — on the mark's optical middle, a little above its own.
    let lockup = "flex items-end gap-2"
    let lockupMark = "block w-14 h-14 -mb-[15px] shrink-0 [&>svg]:block [&>svg]:w-full [&>svg]:h-full"
    let heading = "font-extralight text-heading tracking-display lowercase text-ink truncate"
    let body = "font-light text-body text-ink"
    /// The body voice one step back: worn by a record that is not live — a stopped, exited or
    /// archived session's name in the Manager's list — so a scan of the column finds what is
    /// running by weight of ink alone. A TOKEN rather than opacity: opacity would dim the
    /// mark and the focus ring riding the same element, and a token is what the contrast
    /// suite can pin.
    let bodyDim = "font-light text-body text-ink-dim"
    let small = "font-light text-small text-ink-faint"
    /// `small` in the err tone: a CAPTION whose subject is a fault — the exception `paneSays`
    /// names, for a line a person has to do something about. It is not a status: a status is
    /// the caps voice with a dot (`statusErr`), which is what the Manager's registry wears now
    /// that its states speak in that voice rather than in a sentence's.
    let smallErr = "font-light text-small text-err"
    /// The caps voice — one size, one tracking, semibold — worn by every label, status,
    /// button, and author line. Colour composes at the use site.
    let private caps = "font-semibold text-label tracking-caps uppercase"

    /// The same voice one step up, for a verb that has ROOM: 13px rather than 11. Worn by
    /// the composer's Send, which stands on a band of its own with nothing competing for the
    /// width. At the label size a word button there read as a caption of the glyph it
    /// replaced rather than as the thing you press.
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
    /// A FIELD's name, and the smallest thing the caps voice says: 11px in the faintest ink
    /// that still means something. It is the floor of the label hierarchy, so a group's name
    /// above it has somewhere to stand (`sectionLabel`).
    let label = caps + " text-ink-faint"

    /// A GROUP's name — a section inside a drawer, with field labels under it. One step up the
    /// ramp from `label` and one step brighter in ink, because a heading and the fields it
    /// heads were one face set 24px apart: nothing but position said which was which, and
    /// position is the weakest of the three (principle 2 — within a group, size, weight and
    /// position say what matters, in that order). Still the caps voice: a group's name is a
    /// short label like any other, so what changes is the STEP, not the voice.
    let sectionLabel = capsLg + " text-ink-dim"

    let mono = "font-terminal text-code text-ink"
    let monoOut = "font-terminal text-code-sm text-ink-faint whitespace-pre-wrap"
    /// The mono face as PLUMBING: a minted id, an address, a timestamp — a machine's string
    /// beside a human's name, so it recedes to the faintest ink that still means something.
    /// Written once because the Manager's three tables spelled it out at six sites, each free
    /// to drift a step (principle 5: one way per job).
    let monoFaint = "font-terminal text-code text-ink-faint"

    /// The body voice in the err tone: the one wrong thing on a surface said as a SENTENCE
    /// rather than as a status — a refusal page's reason, the opening screen's giving up.
    /// `statusErr` is the caps voice and is for a WORD; a sentence set in capitals is the
    /// wall of text the caps rule above warns about.
    let bodyErr = "font-light text-body text-err"

    /// A count beside a label — how many MCP servers, how many hook endpoints: the label's
    /// own voice and ink, with the figures on one width so a changing number does not move
    /// the words. The two sites that needed it had each hand-written the caps voice, one of
    /// them at a tracking the ramp does not have.
    let labelCount = label + " tabular-nums"

    /// A NAME in a row that has to GIVE WAY: it ellipsises, and `min-w-0` is what lets it. A
    /// flex item's floor is the intrinsic width of its content, so without that the row widens
    /// instead and pushes whatever follows the name — a state mark, a count, a label — off the
    /// end of it. The two utilities are one move and nothing wants one without the other, so
    /// they are one name rather than the pair that was written out at five sites (principle 5:
    /// search for the raw utility, and fold it back into the vocabulary that owns it).
    ///
    /// The give comes from ONE side. What rides beside such a name holds its width
    /// (`shrink-0`), which is the other half of the same arrangement.
    let givesWay = "min-w-0 truncate"

    // --- Statuses: text only — never filled, never boxed --------------------------------

    /// LIVE: a running command, a connected peer, a session that runs (docs/visual-design.md,
    /// Colour). Green is the one voice that says a thing is doing something NOW, which is why
    /// a WAIT may never wear it — `queued`, `starting` and `looking` are `statusWait`, and
    /// green on a queued command said the opposite of what it meant.
    let statusOk = caps + " text-green"
    /// A record of something that finished well — `✓ 0`, a tally of done. `ink`, not green:
    /// green is LIVE (a running command, a connected peer), and a command that exited is no
    /// longer doing anything (docs/visual-design.md, Colour). Running keeps `statusOk`.
    let statusDone = caps + " text-ink"
    /// A wait: reconnecting, catching up, queued, looking, no agent yet. `ink-dim`, never blue
    /// — blue is never a status (docs/visual-design.md, Colour). A LIVE thing is `statusOk`;
    /// both it and a wait still under way lead with the same turning mark, `Icon.spin`.
    ///
    /// A WRONG thing never wears this. It is the one tone a failure may not be quieted into:
    /// this value once carried "a fault that is no longer the newest" as a second meaning, and
    /// what wore it was four failed commands out of five in one scrollback, each reading as a
    /// wait (see `View.terminalBlockStatus`).
    let statusWait = caps + " text-ink-dim"
    /// WRONG. There is at most one red word on a SURFACE, and a repeated row — a block's line
    /// in a scrollback, a repeated item in the conversation — is a surface of its own, so a
    /// failed row keeps its own red word however many failed rows are on the screen
    /// (docs/visual-design.md, Colour). Nothing dims a failure into a record: what makes a
    /// wrong thing quiet is a surface holding two of them, which is the surface's bug.
    let statusErr = caps + " text-err"
    let statusFaint = caps + " text-ink-faint"

    /// The one disc every SETTLED status's mark is drawn as: 6px, round, inline. The point is
    /// the second of the two primitives and it is always round (docs/visual-design.md, Form);
    /// written once, here, because it had been spelled out at nine sites, each free to arrive
    /// a pixel off. What a settled status's mark MEANS is the colour composed onto it below —
    /// never its size, and never its shape. The one other point that ranks by shape is the
    /// attention mark (`attentionMark`, `Icon.attention`), which answers a different question:
    /// not what state a thing is in, but how much it wants a person. A status still UNDER WAY
    /// is the one further exception, and by kind rather than by taste, same as the attention
    /// mark: it turns rather than beats (`Icon.spin`), and needs more than a disc's room to
    /// show that, so it is not drawn from this shape at all.
    let private disc = "inline-block w-1.5 h-1.5 rounded-full"

    /// How a mark that LEADS a word sits: in the gutter to its left, on the text's baseline,
    /// so the mark never pushes the words in (docs/visual-design.md, Space).
    let private discLead = "mr-1.5 align-[1px]"

    /// The small leading dot a status may carry (`bg-current` follows the text colour).
    let statusDot = cls [ disc; "bg-current"; discLead ]
    /// The same dot standing still: a state that has SETTLED, such as a terminal whose last
    /// command failed, where motion would claim it is still happening.
    let statusDotSolid = cls [ disc; "bg-current" ]

    /// The dot with nothing in it: a state that has ENDED (a terminal that closed). Its ring
    /// composes `Stroke` rather than writing a bare `border-*`, which is the rule every other
    /// border in this file answers to — that was the whole defect, and deleting the shape was
    /// never the remedy for it.
    ///
    /// Because the SHAPE here is holding a floor criterion up. A closed terminal and a failed
    /// one sit in the same row of names, their words are `srOnly`, and their voices differ
    /// only in ink — so with one shape between them, colour would be the only visual means of
    /// telling them apart, which WCAG 2.0 SC 1.4.1 (Use of Color, Level A) forbids. The floor
    /// is first in the doctrine's Deciding order and is never traded; vocabulary is fourth and
    /// cannot buy it.
    ///
    /// That leaves a real question open, and open on purpose: this ring also means "somebody
    /// is watching" on a person (`paneViewerDot`, which still draws its own bare border), and
    /// one shape may not mean two things. Watching is the rarer meaning and the one with
    /// somewhere else to go, so it is the one to move — not this.
    let statusDotHollow = cls [ disc; Stroke.ring; Stroke.current ]

    /// Still going: a command running, or a wait under way (`Icon.spin`). Always `ink-dim`,
    /// never the status word's own colour (`statusOk`'s green, `statusWait`'s dim ink): the
    /// word beside the mark is what still says which, the same way it always has
    /// (docs/visual-design.md, Colour). Two 12-unit arcs, half the ring each, turning opposite
    /// ways — passing each other rather than chasing, so at least one gap always shows. The
    /// box is `overflow-visible` for nothing to clip the stroke's round caps at the ring's
    /// four compass points.
    let statusSpin = "inline-block w-3 h-3 align-[-2px] text-ink-dim overflow-visible"
    /// The spin mark leading a word or a count on its line, as `statusDot` leads a settled one.
    let statusSpinLead = cls [ statusSpin; "mr-1" ]
    /// One arc's turn, clockwise from twelve o'clock.
    let statusSpinCw =
        "origin-center [transform-box:fill-box] animate-status-spin-cw motion-reduce:animate-none"
    /// The other arc's turn, the same rate the other way, starting from six o'clock — so at
    /// rest the two arcs sit nose to tail and trace the full ring between them.
    let statusSpinCcw =
        "origin-center [transform-box:fill-box] animate-status-spin-ccw motion-reduce:animate-none"
    /// The settled dot with a halo round it: something has happened that the person reading
    /// has not seen yet (`ClientModel.unseen`). An `outline` rather than a border, so the gap
    /// between the two is the surface it sits on, whichever that is — and so this stays a
    /// FILLED point with something around it, which is what keeps it clear of the hollow one.
    let statusDotNews =
        cls [ statusDotSolid; "outline outline-1 outline-current outline-offset-[1.5px]" ]

    /// A standalone dot given its colour explicitly (`bg-green` etc. composed at the use
    /// site) for a row whose text is a DIFFERENT colour — `bg-current` would fight the
    /// composed colour utility, and which `bg-*` wins is stylesheet order, not authoring
    /// order.
    let syncDot = cls [ disc; "shrink-0" ]
    /// The sidebar's one-line sync summary: dot and status words on one baseline.
    let syncRow = "flex items-center gap-2"

    // --- Buttons: four weights, in this order of scarcity -------------------------------
    //
    // The doctrine's hierarchy of controls (docs/visual-design.md), and the whole of it,
    // because "some have borders" is how two conventions breed a third:
    //
    //   * FILLED — a solid `blue` rectangle with its text in `bg`. *The* verb of a surface,
    //     and at most one of them per SURFACE — the count is per surface and not per screen,
    //     because a screen holds several and each has its own one verb (docs/visual-design.md,
    //     principle 3). While a flow is under way, the step it waits on wears the fill and
    //     every other verb on that surface drops a weight.
    //   * BORDERED — ONE face, and only one: `ring` in the `edge` tone with `ink` text, and
    //     interaction brightens the ring. A STANDALONE act on the ground — it begins or ends
    //     something of its own (connect, interrupt, sign in, open the terminal list). A Metro
    //     button IS its rectangle; the rectangle is the claim to be a thing you press. There
    //     is no blue-ringed button: blue text is a link's, the fill's and Send's.
    //   * BARE — a word or an icon, no rectangle: a verb RIDING the thing it acts on
    //     (send/discard at a field's trailing edge, reorder/delete on a listed row). The row
    //     or the field already carries the structure, so the verb borrows it, and a second
    //     rectangle inside the first would be chrome describing chrome. ONE voice: the caps
    //     label step, `ink-dim` at rest, because the thing it rides is the subject.
    //   * TEXT — a status, not pressable at all. Those are the `status*` values above.
    //
    // Three STATES cut across all four, and none of them is `disabled`, which does not
    // exist in this product (docs/visual-design.md, State):
    //
    //   * WAITING — nothing to act on YET. It stays in the layout and in focus order, its
    //     border drops to the quiet hairline, and its word stays legible. `btnWaiting` and
    //     `btnWaitingSwap` are the bordered box's; `bareInkWaiting` is a bare verb's, one
    //     role back from the `ink-dim` a bare verb rests in, so a verb you can press and one
    //     you cannot are two pictures and not one.
    //   * HELD — the act has been pressed and is still landing. That is the `pressed:`
    //     variant every face below carries: `:active` OR `aria-busy` in ONE variant
    //     (app/tailwind.css), so a face cannot fill for the finger and forget the hold. A
    //     site adopts it by setting `aria-busy` from the model, and `whenReady`/`whenBusy`
    //     swap the word inside it.
    //   * ARMED — a destructive act one press from done. ONE way, whatever weight it arms
    //     from: the `err` FILL at rest (`btnIconBareDangerArmed`, `btnKillArmed`). A touch
    //     has no hover, so the state a phone must see is worn rather than revealed. The fill
    //     is not the surface's verb, so principle 3 does not count it, and it is a fill and
    //     not a red WORD, so the one-red-word budget is untouched either.
    //
    // Nothing in this section writes two utilities on ONE property. Which of them wins is
    // stylesheet order rather than authoring order, so a base that sets a background, a
    // cursor or a border tone takes the choice away from the face composed over it — and
    // `!important` is what that mistake looks like once somebody has worked around it. The
    // background, the cursor and the border tone therefore live on each FACE, and the shared
    // `btnFace` holds only what no face disagrees about.

    /// What a BARE control IS: the user agent's own button box, taken away. The bare weight
    /// is a word or an icon with no rectangle, so the ABSENCE of the rectangle is one value
    /// written once rather than `border-0` spelled out at every bare control in the file —
    /// it was spelled out at thirty-odd. Public, because a bare verb rides a surface in
    /// nearly every section below.
    ///
    /// Padding is deliberately NOT in it: `p-*` and `px-*` are one property to the cascade,
    /// so a base that zeroed the padding and a site that set its own would race. Nor is the
    /// cursor, which the waiting faces have to be able to answer differently.
    ///
    /// Nor, and this one was a live bug, the BACKGROUND. It used to carry `bg-transparent`,
    /// and Tailwind emits `.bg-transparent` AFTER `.bg-err` — so `btnIconBareDangerArmed`,
    /// which is this base plus `bg-err`, painted no fill at all: the armed queue delete, the
    /// one control in the product whose whole job is to LOOK armed before the second press,
    /// looked exactly like the unarmed one. A bare control does not need the utility anyway:
    /// Tailwind's own preflight already gives every `button` a transparent background, and an
    /// `<a>` or a `<span>` has one by default.
    ///
    /// Not a `Stroke` tone either: this is the user agent's own border being cancelled,
    /// which is a fact about the control rather than an edge this product draws.
    let bareBox = "border-0"

    /// The ONE voice a bare verb speaks in, and the three tones it has. The caps label step
    /// comes from the box it is composed into; this is the ink:
    ///
    ///   * `bareInk` — `ink-dim` at rest, because the THING it rides is the subject; full
    ///     ink under the hand; and full ink while the press lands. That last one is not
    ///     decoration — a touch screen has no hover, so `pressed:` is the only feedback a
    ///     thumb gets from a verb with no rectangle to fill.
    ///   * `bareInkDanger` — the same, reddening for a verb that destroys.
    ///   * `bareInkWaiting` — nothing to act on: one role further back in ink, still
    ///     legible, still in focus order, and with no hover promise, because a control that
    ///     brightens under the hand promises something it cannot do.
    ///
    /// `ink-dim` at rest and `ink-faint` waiting are one step apart on purpose, and the step
    /// is the whole of what the waiting face says (docs/visual-design.md, State). Both tones
    /// rested at `ink-faint` until #1373, so waiting and ready were one picture and
    /// principle 4's screenshot could not tell a verb you can press from one you cannot. Do
    /// not bring the rest tone back down to meet the waiting one.
    ///
    /// They are ALTERNATIVES, never layers: each one names the same two properties, so a
    /// site picks exactly one of the three.
    let bareInk = "cursor-pointer text-ink-dim hover:text-ink pressed:text-ink"
    let bareInkDanger = "cursor-pointer text-ink-dim hover:text-err pressed:text-err"
    let bareInkWaiting = "cursor-default text-ink-faint"

    /// Sized by construction, not padding arithmetic: the box is `h-control` with the line
    /// flex-centred in it — the old `py-[7px]` was that same height, hand-derived and
    /// easy to break. A FIELD is built the same way from the same token (`fieldFace`), which
    /// is what makes a button and an input in one row actually line up.
    ///
    /// On a phone it grows to `spacing-touch`, which is what every control does there
    /// (docs/visual-design.md, Space). `phone:` and not a width: a phone turned sideways is
    /// still a phone, and its controls are still under a thumb.
    ///
    /// `whitespace-nowrap`, because the height is FIXED. A label let wrap inside a one-line
    /// box puts its second line outside the rectangle, which is how APPROVE ON GITHUB.COM
    /// came to be two lines in a 32px button.
    ///
    /// Layout is split from the rest of the face (`btnFlex`/`btnGrid`) because one button
    /// — Create — needs a different one: its two words (`whenReady`/`whenBusy`)
    /// share a box whose size cannot depend on which of them is showing.
    let private btnFace =
        cls [ "group/btn font-ui"; caps
              "h-control phone:h-touch px-3.5 whitespace-nowrap transition-colors"
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

    /// THE bordered face, and the only one: `ring` in the `edge` tone with `ink` text, and
    /// interaction brightens the ring (docs/visual-design.md, Hierarchy of controls).
    ///
    /// There used to be two — this one in `ink-dim`, and a blue-ringed one beside it —
    /// standing in for a primary and a secondary weight that the hierarchy does not have.
    /// What it has is a FILL for the surface's one verb and a border for every standalone
    /// act. So the second bordered face became the fill (`btnPrimary`, below) and this one
    /// came up to `ink`: a bordered button is not a quieter button, it is a different KIND
    /// of control, and dimming its word was the half-weight the extra face was invented for.
    ///
    /// The press face is `pressed:` rather than `active:` — the finger's press AND the hold
    /// after it (`aria-busy`), for a button whose act takes the browser somewhere else and
    /// has nothing to show on this page until it arrives. Filled while down, and down until
    /// it lands. `prefers-reduced-motion` keeps exactly this fill and drops the tilt
    /// (app/tailwind.css), which is what makes the fill the press state and the tilt its
    /// motion rather than the other way round.
    let btn =
        cls [ btnFace; btnFlex; "bg-transparent cursor-pointer"; Stroke.rim; "text-ink"
              Stroke.hoverInk; "pressed:bg-ink pressed:text-bg" ]

    /// The same face with `err` where the interaction goes: a standalone act that DESTROYS
    /// something. Not a second bordered face — at rest it is the one above to the pixel,
    /// because `err` at rest is this product's tone for *something is wrong* rather than for
    /// *this could go wrong*, and what a control does is not visible until you reach for it.
    /// An ARMED control is the exception, and it wears the err FILL at rest instead
    /// (`btnIconBareDangerArmed`).
    let btnDanger =
        cls [ btnFace; btnFlex; "bg-transparent cursor-pointer"; Stroke.rim; "text-ink"
              Stroke.hoverErr; "hover:text-err pressed:bg-err pressed:text-bg" ]

    /// The FILL: a solid `blue` rectangle with its text in `bg` — *the* verb of this
    /// surface, and at most one on that surface (docs/visual-design.md, Hierarchy of controls).
    /// `btnPrimary` is the doctrine's own word for it: each surface has one PRIMARY act, and
    /// only that act wears the fill.
    ///
    /// Blue, and the only blue rectangle there is. It used to be a blue RING with blue text,
    /// which is the one bordered face above wearing the interactive colour to mean "this one
    /// matters more" — a weight said twice, in a hue the doctrine spends on links, on this
    /// fill and on the composer's Send. Filling it says the same thing once.
    ///
    /// `Stroke.clear` keeps the border BOX while painting nothing on it, so a filled button
    /// and a bordered one standing in one row are the same size and sit on the same
    /// baseline. Interaction brightens the fill the way it brightens a ring, and `pressed:`
    /// holds that brightening past the finger while the act lands.
    let private btnPrimaryFace =
        cls [ Stroke.clear; "bg-blue text-bg cursor-pointer"
              "hover:bg-blue-up-1 pressed:bg-blue-up-1" ]

    let btnPrimary =
        cls [ btnFace; btnFlex; btnPrimaryFace ]

    /// `btnPrimary`, laid out on `btnGrid` — the one a held word
    /// (`whenReady`/`whenBusy`) sits on top of, so pressing it never changes its own size.
    /// Not Create's alone: `askStart` (the repo picker's commit button, below) builds on it
    /// too, which is the point — the box-stable hold is a property of the LAYOUT, not of
    /// which button first needed it.
    let btnPrimarySwap =
        cls [ btnFace; btnGrid; btnPrimaryFace ]

    /// The WAITING face: a control with NOTHING TO ACT ON yet (docs/visual-design.md,
    /// State). It stays in the layout and in focus order — its work is coming, and a control
    /// that appears mid-sentence moves everything under the reader's hand — and it is never
    /// `disabled`, in either spelling, because that attribute does not exist here. The
    /// BORDER carries the waiting, dropping from the working rim to the quiet hairline so
    /// the rectangle reads as an outline not yet filled, which is this design's own
    /// vocabulary: a Metro button IS its border, hover brightens it, press fills it. The
    /// word steps back one role of ink and stays legible.
    ///
    /// Deliberately NOT an opacity dim: fading an 11px caps label takes it under the AA
    /// floor this product holds, and opacity would dim the focus ring riding the same
    /// element with it.
    ///
    /// A whole FACE rather than the override it used to be. It was `"!border-edge"`, and the
    /// `!important` was not emphasis — it was the only way one `border-*` utility could beat
    /// another in the same class list. A face names each property once, so there is nothing
    /// to beat. It was also worn NOWHERE, so the override was never once tested.
    let btnWaiting =
        cls [ btnFace; btnFlex; "bg-transparent cursor-default"; Stroke.hair; "text-ink-dim" ]

    /// The waiting face on the box-stable layout, for a control whose word swaps when its
    /// act lands (`whenReady`/`whenBusy`): one size waiting, ready and held, so admission
    /// never resizes the one control a surface asks a thumb to find twice.
    let btnWaitingSwap =
        cls [ btnFace; btnGrid; "bg-transparent cursor-default"; Stroke.hair; "text-ink-dim" ]

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
    ///
    /// The SAME look while the tap that opened it is still landing — `group-aria-busy/open:`,
    /// reading the hold the outer link carries (`recordRowLink`, `ManagerPage.fs`'s click
    /// handler), because a touch has no hover and this is the only pointer this control can
    /// give one. Not a second look: held wears exactly the hover one, same as Create's own
    /// press wears its own hover-equivalent fill.
    let recordLink =
        cls [ body
              "no-underline hover:text-blue hover:underline decoration-1 underline-offset-4"
              "group-aria-busy/open:text-blue group-aria-busy/open:underline"
              focusRing ]

    /// The same link on a record that is not live (`bodyDim`): still the way in — opening a
    /// stopped session is what starts it — but a step back from the ones already running.
    let recordLinkQuiet =
        cls [ bodyDim
              "no-underline hover:text-blue hover:underline decoration-1 underline-offset-4"
              "group-aria-busy/open:text-blue group-aria-busy/open:underline"
              focusRing ]

    /// The mark that says a record link LEAVES (the session serves its own origin). Fainter
    /// than the faintest ink (`opacity-70` over `ink-faint`, safe because this is decorative
    /// — `aria-hidden`, and never the only way a reader learns the link leaves): it is a
    /// signpost, not a second status. `inline-flex` so the idle nudge below has a box a
    /// transform actually applies to (a bare inline element ignores `transform`). The nudge
    /// itself — a 1px lift toward the corner the mark points at, and a rest, slow and mostly
    /// still — is `animate-leave-nudge` (app/tokens.css; the keyframe and why it moves this
    /// little are in app/tailwind.css). `Icon.leavesSm` is the mark; the gap and the ink are
    /// all this carries otherwise.
    let recordLinkMark =
        cls [ "inline-flex ml-1 text-ink-faint opacity-70"; "animate-leave-nudge motion-reduce:animate-none" ]

    /// The hit area for a listed record's whole cell — the state word beside the name
    /// included, not just the name's own glyphs. `block` so the link's box is the cell's full
    /// width rather than the text it wraps; colour and hover stay on the name text itself
    /// (`recordLink` / `recordLinkQuiet`, worn by a span inside), so hovering the state word
    /// does not blue it — only the name was ever styled to look like a link, this just widens what you
    /// can press to get it.
    ///
    /// `group/open`, named for the hold a tap puts on it (`ManagerPage.fs`'s click handler
    /// sets `aria-busy` on exactly this element) rather than `data-press`'s tilt: that tilt is
    /// sized for an 80px-wide button (app/tailwind.css), and this hit area is a whole row
    /// wide, so the same geometry here would read as the row keeling over rather than a
    /// button going in. The name span inside reads the hold instead (`recordLink`'s
    /// `group-aria-busy/open:`), and `aria-busy:cursor-progress` says the same thing the
    /// pointer does on every other held control.
    let recordRowLink = cls [ "group/open block no-underline aria-busy:cursor-progress"; focusRing ]

    /// A listed record's name and its leaves mark (`recordLinkMark`), as two flex children
    /// rather than one truncating block: on a narrow row (a phone) something has to give, and
    /// it must be the NAME, never the mark that says the link leaves — that is exactly the
    /// fact a reader on a thumb's-width row most needs, and a single truncating block let the
    /// browser clip it away first since it sat last. `min-w-0` so this box can itself shrink
    /// inside whatever fixed-width cell holds it.
    let recordRowName = "flex items-baseline min-w-0"

    /// The name's own box inside `recordRowName`: `flex-1` takes what the mark beside it
    /// leaves, `min-w-0` lets that shrink below its content's width, and `truncate` is what
    /// clips there — a row that changes height when a session is renamed is a list you
    /// cannot keep your place in, so this still clips rather than wraps.
    let recordRowNameText = "flex-1 min-w-0 truncate"

    /// The roster's mark column (`ManagerUi.Col.mark`): the row's attention mark, at every
    /// width, as the session page wears its worst standing under the chevron. The column is
    /// 32px and the mark sits at its left edge, which is the page's rail — so the chips, the
    /// section heads and every row's mark start on one line, and every name starts 32px in,
    /// where the session page's title starts beside its chevron.
    ///
    /// The cell has no gutter of its own (the column's width IS the gap), and 2px above and
    /// below a 44px target makes the row's 48px: the name cell's 12px + 24px line + 12px. Top,
    /// not baseline, so an unfolded row keeps its mark on the name's line.
    let recordMarkCell = "py-0.5 align-top"

    /// The mark's press: a thumb's 44px, grown LEFT into the page's gutter rather than right
    /// into the name, so the mark stays on the rail and the name's own target is untouched.
    /// It unfolds the row (`aria-expanded`). No ground of its own: a ground would hang 12px
    /// into the gutter, off the rail every other edge on the page keeps, and the unfolded
    /// lines under it already say the row is open.
    let recordMarkPress = cls [ "relative -ml-3 w-11 h-11 pl-3 flex items-center bg-transparent border-0 cursor-pointer"; focusRing ]

    /// The row's mark, which RIDES when the row unfolds: down a track to the first unfolded
    /// line, which is the one it ranks first, so that line says its word with no mark of its
    /// own rather than repeating the one above it (`--spacing-mark-slide`). A wrapper, so the
    /// mark's own motion (`markOrbit` and its siblings) runs on undisturbed while it travels.
    let recordMarkRider =
        cls [ "relative flex transition-[translate]"; Motion.pace
              "[[data-expanded]_&]:translate-y-(--spacing-mark-slide)" ]

    /// What the mark leaves behind as it rides: a line from its seat to where it stops, drawn
    /// down with it (scaled from the top, at the same pace). The weight and ink of a causal
    /// link's rail (`causeRail`): one pixel of `edge`, because it joins two things the way
    /// those do, and a track that read as heavy as the mark competed with it. Centred on the
    /// mark's 12px box: the box sits 12px into the press, so its centre is 18px in — the
    /// pixel straddles 18, which a 2x screen draws as two whole device pixels — and the
    /// press's own middle down.
    let recordMarkTrack =
        cls [ "absolute left-[17.5px] top-1/2 w-px h-(--spacing-mark-slide) bg-edge origin-top scale-y-0"
              "transition-[scale]"; Motion.pace
              "[[data-expanded]_&]:scale-y-100" ]

    /// The track's start: a 3px point on the mark's seat, centred on the same 18px, so the
    /// line begins round rather than squared without the point outweighing the line. Its own
    /// element, not the line's `::before`, so the line's scale does not squash it; it fades in
    /// where the mark leaves rather than travelling.
    let recordMarkSeat =
        cls [ "absolute left-[16.5px] top-1/2 -mt-[1.5px] size-[3px] rounded-full bg-edge opacity-0"
              "transition-[opacity]"; Motion.pace
              "[[data-expanded]_&]:opacity-100" ]

    /// What a row unfolds to, under its name: each thing it says, then its verbs. Folded at
    /// rest — `visibility` takes the verbs out of the tab order with it — and unfolded by
    /// the ROW's `data-expanded`, which the page's program sets on the one row that is open
    /// and carries across every frame the rows stream swaps in.
    let recordDetail =
        cls [ Motion.unfold; Motion.folded
              "[[data-expanded]_&]:grid-rows-[1fr] [[data-expanded]_&]:translate-y-0"
              "[[data-expanded]_&]:opacity-100 [[data-expanded]_&]:visible" ]

    /// The unfolding row, pulled back over the mark column so its lines are the column's
    /// lines: each mark under the row's mark, each word on the name's rail.
    let recordDetailInner = cls [ Motion.unfoldInner; "-ml-8" ]
    let recordDetailLines = "grid grid-cols-[2rem_minmax(0,1fr)] items-center gap-y-2 pt-2"
    /// A line's word: the sidebar status block's voice (`person`), in its attention's ink.
    let recordDetailWord = "font-light text-small leading-5 truncate"
    /// The verbs, on the name's rail under what the row said: Stop, Start, Archive. Bare
    /// words, the hierarchy's quietest act, in a row a thumb can tell apart. Pulled back by a
    /// verb's own 4px padding, so the first verb's WORD is on the name's rail, and each grown
    /// to a thumb's height on a phone.
    let recordDetailVerbs = "col-start-2 -ml-1 flex items-center gap-5 h-11 phone:[&>button]:h-touch"

    /// A filter over a listed registry — which archive states the management page is showing.
    /// Real links, because the filter IS the page's location, so these are chips only in how
    /// they look; everything about how they behave comes free from `<a>`.
    ///
    /// Told apart by GROUND, as the pane's pivot tabs are (`pivotItemOn`): the lit chip wears
    /// the `panel` step, the rest are bare words. Not by a ring. A bordered rectangle on this
    /// page means a standalone act on the ground — Declare — and the lit chip is a state,
    /// not an act; blurred, a ringed chip beside the filled Create read as a second button.
    /// The ground is chrome showing structure (principle 2 — blur the screen until no word
    /// reads, and the selection is still visible), and it is the ONE way this product says
    /// "the selected one of these". Choosing a filter moves no text: no face has a border.
    ///
    /// `h-control`, and a thumb's height on a phone, like every other control: a row of
    /// chips beside the page's Create button is a row of things of one height.
    ///
    /// No fill on any of them — the filled weight on this page belongs to Create.
    let private filterChipBase =
        cls [ caps; "h-control phone:h-touch px-2 inline-flex items-center no-underline border-0"
              "whitespace-nowrap transition-colors"; focusRing ]

    let filterChipOn = cls [ filterChipBase; "bg-panel text-ink cursor-pointer" ]
    /// The count a chip wears, in the chip's own voice and colour — the word is the filter,
    /// the number is what it holds, and they light and dim together.
    let filterChipCount = "ml-1.5 tabular-nums"
    let filterChipOff = cls [ filterChipBase; bareInk ]
    /// An unlit chip with nothing behind it (its count is 0) wears the waiting face
    /// (docs/visual-design.md, State): still pressable, since showing an empty state is a
    /// legitimate ask, but a step quieter than a chip that would show something.
    let filterChipEmpty = cls [ filterChipBase; "cursor-pointer text-ink-faint hover:text-ink" ]

    /// A sortable column header. Wears the header's own caps-faint voice so a sortable column
    /// does not shout over the ones that are not, and comes up to ink under the pointer — the
    /// only thing that says it is a control, besides its focus ring and the direction mark it
    /// carries. The `<th>` around it holds `aria-sort`, which is what actually states the sort.
    let sortHeader =
        cls [ label; "no-underline cursor-pointer hover:text-ink transition-colors"; focusRing ]

    /// Square icon buttons — the BORDERED weight with a glyph in it instead of a word, for a
    /// standalone icon act. Self-contained, NOT composed over `btn`: Tailwind emits `p-0`
    /// BEFORE `px-*`/`py-*` in the stylesheet, so "btn + p-0" kept the text button's padding
    /// and crushed the glyph into a corner of a lopsided box (measured live: 30×24, ×
    /// touching the bottom-right edge).
    ///
    /// `spacing-control` square, and a thumb's target on a phone — the same height as every
    /// other control, because a bordered rectangle is a control whatever is drawn inside it.
    /// It used to be 24px "composed up to 32 where it stands in a bar", which is the second
    /// control height that sentence admits to.
    let private btnIconBase =
        cls [ "w-control h-control phone:w-touch phone:h-touch bg-transparent cursor-pointer p-0"
              "grid place-items-center transition-colors"; Stroke.ring; focusRing ]

    /// The standalone icon acts. Row-riding verbs are `btnIconBare` below, and the press is
    /// `pressed:` rather than `active:` for the reason `btn` gives: a glyph button whose act
    /// is a round trip has to be able to stay down until it lands.
    /// There is no danger reading of this face any more: `btnIconDangerLg` was a 32px err
    /// version of it and nothing wore it, so it went (docs/visual-design.md, Deciding —
    /// taste prefers removal). A destructive verb on a row is bare (`btnIconBareDanger`),
    /// and a destructive standalone act says its word (`btnDanger`).
    let btnIcon =
        cls [ btnIconBase; Stroke.rim; "text-ink"; Stroke.hoverInk
              "pressed:bg-ink pressed:text-bg" ]

    /// The borderless icon verbs (the BARE weight at the head of this section): 24px square,
    /// grid-centred, riding a listed row they act on — the queue's reorder and delete, a
    /// terminal row's verbs, a roster row's disconnect. The focus ring is the one piece of
    /// chrome they keep, because the keyboard has no hover.
    ///
    /// 24px and not `spacing-control`: a bare verb has no rectangle, so there is no control
    /// box for the control height to govern — the height it occupies is the ROW's, and a
    /// 32px box inside a 24px row would push the row open to make room for a verb that is
    /// supposed to be riding it. What a phone needs instead is a bigger HIT area, which
    /// `hitTouch` below grows by padding the box out and handing the height straight back —
    /// so the glyph does not move and the row does not open.
    let private btnIconBareBase =
        cls [ "w-6 h-6 shrink-0 grid place-items-center transition-colors"; bareBox; "p-0"
              focusRing ]
    let btnIconBare = cls [ btnIconBareBase; bareInk ]
    let btnIconBareDanger = cls [ btnIconBareBase; bareInkDanger ]
    /// `btnIconBareDanger`, ARMED: a second press away from actually deleting (the queue's
    /// delete — see `View.queue`), which this says by wearing the err fill AT REST rather
    /// than only under the hand. A touch has no hover to hold the hint up, so the state a
    /// phone needs to see has to be worn rather than revealed — the same reasoning
    /// `trayInterrupt` uses to stay unworn, read the other way: that button is never armed
    /// twice, this one specifically IS, between the two presses the confirm needs.
    /// Un-arms itself back to `btnIconBareDanger` on its own (`armedMs`), so a
    /// press nobody confirms cannot leave a row looking primed for ever.
    let btnIconBareDangerArmed = cls [ btnIconBareBase; "cursor-pointer bg-err text-bg" ]
    /// A terminal's kill, ARMED (`View.killControl`): the same state as the one above, so the
    /// same way of drawing it — the `err` FILL at rest (docs/visual-design.md, State). A touch
    /// has no hover to hold the warning up, so what a phone has to see is worn.
    ///
    /// It was a red WORD on a transparent box, which made two armed faces for one state: a
    /// fill on the queue's delete, a reddened word here. Principle 5 allows one way per job,
    /// and the doctrine names which — so the word's box fills. The reason to avoid the fill is
    /// also gone: #1376 settled that an armed `err` fill is a different mark from the
    /// surface's blue verb, which principle 3 counts alone; and a fill is not a red word, so
    /// the one-red-word budget it used to spend is handed back to the surface.
    ///
    /// What it still says is what it will end: `kill`, then what is running there, in the
    /// pane's light lowercase rather than a slab of capitals, because the fill is the alarm
    /// and the sentence inside it is only the fact. The command after it is what tells it
    /// from the unarmed word reddening under the pointer. The row verb's height, so the row
    /// does not move; wider, growing leftwards from its right edge, so the second press lands
    /// where the first did. `px-2` and not the unarmed verb's `px-1`: a fill has an edge, and
    /// a word 4px from a painted edge reads as a word that overflowed it.
    let btnKillArmed =
        cls [ "h-6 shrink-0 inline-flex items-center gap-1.5 px-2 cursor-pointer"; bareBox
              "font-ui font-normal text-body lowercase whitespace-nowrap"
              "bg-err text-bg transition-colors"; focusRing ]
    /// What the armed kill says is running, inside it: the command as typed — case kept — cut
    /// short rather than allowed to push the row's name out of its column.
    let killArmedRunning =
        "font-terminal text-code normal-case tracking-normal truncate max-w-[7rem]"
    /// A bare verb's thumb target, on a phone and only there: the box pads out to
    /// `spacing-touch`, so the BORDER box is 44px — which is what a finger lands on and what
    /// the focus ring is drawn round — while the glyph, centred in the content box the padding
    /// leaves, does not move. The doctrine asks for precisely this: a control with an edge
    /// grows on a phone, a bare verb grows only its hit area (docs/visual-design.md, Space).
    ///
    /// It grows LEFT and DOWN, never right, and that asymmetry is the whole of what is
    /// delicate here. Every verb this is worn by sits at the trailing edge of something — a
    /// message's handle in the timeline's right gutter, the queue's delete at its row's, the
    /// Manager's archive at its rail's — so a box that grew rightward reached past the gutter
    /// it was parked in and gave the conversation column a sideways scroll at 390, which two
    /// browser cases pin and which is how that was caught. Twenty pixels of `pl` against
    /// nothing on the right keeps the right edge exactly where it was.
    ///
    /// Sideways the box really grows, and vertically it only pretends to: `-my` hands the
    /// vertical padding straight back, so the row it rides keeps the height it had, which is
    /// what the doctrine's "a bare verb takes the height of its row" is about. Across the row
    /// there is nothing to protect — a verb 44px wide simply sits beside the next one, and
    /// targets that tile are the point.
    ///
    /// ONE way to grow a hit area (principle 5). There was a second, worn by the Manager's
    /// archive under this same name: an empty `::before` reaching 10px past a box left at 24.
    /// This box form is what the two folded into, for two reasons the overlay cannot answer —
    /// the overlay's box is not the target, so the focus ring drew a 24px square around a 44px
    /// press; and an overlay reaches onto whatever is beside it, so a row of three could not
    /// tile, while boxes in flow simply sit next to each other. What the fold costs is FLOW: a
    /// rail measured against growth that took no room pays the 20px for real
    /// (`ManagerUi.Col.rail`).
    ///
    /// Private, because the thing a site wears is a FACE and not a hit area. Every wearer below
    /// composes it into one, so no view can grow a target without also saying which verb it is.
    let private hitTouch = "phone:w-touch phone:h-touch phone:pl-5 phone:py-2.5 phone:-my-2.5"

    /// A bare icon verb with that target — the queue's reorder, the Manager's archive, every
    /// icon that is the only thing a row offers a thumb.
    let btnIconBareTouch = cls [ btnIconBare; hitTouch ]

    /// The borderless verb as a WORD rather than an icon, for a row whose verb has no glyph
    /// that says it (stop, unarchive). Same rule, same box height, same ink as the icon form;
    /// the caps voice, because that is the one voice a bare verb speaks in.
    let private btnBareBase =
        cls [ "h-6 px-1 shrink-0 inline-flex items-center font-ui"; caps; bareBox
              "transition-colors"; focusRing ]
    let btnBare = cls [ btnBareBase; bareInk ]
    let btnBareDanger = cls [ btnBareBase; bareInkDanger ]
    /// The bare word with nothing to act on yet: its waiting face (docs/visual-design.md,
    /// State), as `btnSendInFieldWaiting` is for the icon in a field.
    let btnBareWaiting = cls [ btnBareBase; bareInkWaiting ]
    /// A bare verb at the right of a roster row (`person`): pushed to the row's trailing edge,
    /// `-mr-1` so its WORD, not its padding, lines up with the status words other rows end in,
    /// and a thumb's height on a phone, where a 24px word is not a target. It keeps the row's
    /// baseline, so its word stands on the same line as the name beside it however tall its
    /// hit area grows.
    let rosterVerb = "ml-auto -mr-1 phone:min-h-touch"

    /// `spacing-control` square and BORDERLESS: the verb parked INSIDE a field, over the text.
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
    /// A thumb's target on a phone, where the field it rides is one too.
    let private btnInField =
        cls [ "w-control h-control phone:w-touch phone:h-touch shrink-0 grid place-items-center"
              bareBox; "p-0 transition-colors"; focusRing ]
    /// The terminal command line's Run. Blue, as the composer's Send is: this is the same
    /// verb on the terminal's own composer, and blue is what the doctrine spends on the one
    /// bare verb that IS the surface's act (docs/visual-design.md, Hierarchy of controls).
    let btnSendInField = cls [ btnInField; "cursor-pointer text-blue hover:text-blue-up-1" ]
    /// Waiting for something to run: the same control in the same place, at the weight of a
    /// thing with nothing to do — never `disabled`, in either spelling, because an empty
    /// command line is not a blocked one.
    let btnSendInFieldWaiting = cls [ btnInField; bareInkWaiting ]

    /// Send is a WORD: the caps voice one step up (`capsLg`), in blue, with no rectangle
    /// around it. It wore a solid blue box for a while — a thumb finds a filled rectangle
    /// before a baseline — and on a phone that box ran to within 4px of the glass, because
    /// the row's gutter (`draftCommitBase`'s `pr-1` beside this `px-3`) is measured to the
    /// WORD. Direct design direction took the box back off: the word in the interactive
    /// colour says "press" on its own, and the word is what sits on the 16px gutter.
    ///
    /// Bare, as the doctrine has a verb that rides the thing it acts on — the draft — and
    /// blue, as the one bare verb that is the surface's own act, which the doctrine blesses
    /// by name (docs/visual-design.md, Hierarchy of controls). No `rounded-*`, no border, no
    /// fill.
    ///
    /// The step UP the caps ramp is the composer's alone, and deliberately: the tray's verbs
    /// sit directly above it on `caps`, and at Send's size they stood level with the verb the
    /// composer is for (`trayVerb` says the same thing from its end). One voice, two steps.
    ///
    /// 40px tall, which is the composer's resting line box below its top room (`draftInput`:
    /// a 24px line, `pb-2`) — so it bottom-aligns onto that line with no correction. The
    /// thumb's target comes from the hit area below rather than from the box, which is why
    /// this is one of the two heights in this file that does not compose `spacing-control`.
    let private btnComposerBox =
        // `pr` is `px-3` less one caps tracking: letter-spacing is laid after EVERY glyph,
        // the last included, so a tracked word's ink ends 0.16em before its box does and the
        // D stopped 2px short of the gutter the draft's text keeps from the same edge.
        cls [ "h-10 pl-3 pr-[calc(0.75rem-var(--tracking-caps))] shrink-0 inline-flex items-center justify-center font-ui"
              // ONE breakpoint's worth of answers and no second one: this box carried a
              // `max-md:transition-none` (no colour tween where there is no hover, so the
              // waiting → ready change would not run beside the row's own fade) and a
              // `max-md:self-baseline` (be the item the row takes its baseline from). The
              // first outlived its reason — `draftCommit` has no fade left to collide with —
              // and the second is now said by that row for every item at once
              // (`draftCommit`'s `phone:items-baseline`). A `max-md:` is a reading column's
              // WIDTH and nothing else (docs/visual-design.md, Phone and wide), so neither
              // belongs here once it has stopped doing anything.
              bareBox; capsLg; "transition-colors"; focusRing ]
    /// Send's own hit area, grown past its 40px box: an empty `::before` reaching past the
    /// word on three sides, a press on the pseudo-element landing on the element. An OVERLAY
    /// and not `hitTouch`'s padded box, because this control is a WORD — a 44px square would
    /// crush the one it says, and the growth it wants is a few pixels past a box whose width
    /// the word sets. Only three sides, not four — Send is the
    /// TRAILING control in this row, and `draftEditors` sits to its left across one `gap-1`;
    /// growing left would widen onto that presence display rather than onto nothing.
    let private btnComposerSendTouch =
        "relative before:absolute before:content-[''] before:-top-1.5 before:-bottom-1.5 before:-right-1.5"
    let btnComposerSend =
        cls [ btnComposerBox; btnComposerSendTouch
              "cursor-pointer text-blue hover:text-blue-up-1" ]
    /// Waiting for something to send: the same word in the same place at the weight of a
    /// thing with nothing to do, never `disabled` in either spelling, because an empty
    /// composer is not a blocked one.
    let btnComposerSendWaiting =
        cls [ btnComposerBox; btnComposerSendTouch; bareInkWaiting ]
    /// Chrome, not an action: the small sidebar collapse/reveal chevrons, and the terminal
    /// column's own door, which composes these (`terminalReopen`) so that the two ways to
    /// the other column are one control. They lean the way they travel on hover and lead
    /// further while pressed — the only motion chrome earns, and the reason the two
    /// directions are separate values rather than one class plus a guess. `pressed:` and not
    /// `active:`, so the lean is one variant with every other press in this file.
    ///
    /// On a phone the glyph stays small and the BOX does not: it grows to `spacing-touch`
    /// each way, with the glyph centred in it. The `-m-1.5` still pays the padding back to
    /// the flow, so a site that needs the rest paid back too says so itself (`navReopen`).
    let private navChevronBase =
        bareBox + " cursor-pointer text-ink-faint hover:text-ink text-small p-1.5 -m-1.5 "
        + "flex items-center gap-1 transition-[translate,color] duration-150 ease-out "
        + "phone:min-w-touch phone:min-h-touch phone:justify-center "
        + "motion-reduce:transition-none " + focusRing

    let navChevronBack = navChevronBase + " hover:-translate-x-0.5 pressed:-translate-x-1"
    let navChevronForward = navChevronBase + " hover:translate-x-0.5 pressed:translate-x-1"

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

    /// Built exactly as `btnFace` is, and from the same `h-control`: the box sets the height
    /// and the content is centred in it. It used to set `py-2` and let the line box decide,
    /// which made an input 42px, a select 40px and a button 32px — three heights in one row.
    /// `inline-flex` centres whatever the box holds against a height the box sets rather than
    /// the line box — which an `<input>` does for itself and a `<select>` does not.
    ///
    /// On a phone it grows to `spacing-touch`, for the reason `btnFace` does and from the same
    /// token: a field has an EDGE, and on a phone a control with an edge grows to the touch
    /// target (docs/visual-design.md, Space). Without it every input, select and token box in
    /// the settings drawer stood 32px under a thumb while the buttons beside them stood 44 —
    /// two heights in one column, and the shorter one below the floor the Deciding order never
    /// trades. `phone:` and not a width, because a phone turned sideways is still a phone.
    let fieldFace =
        cls [ "bg-surface outline-none appearance-none h-control phone:h-touch px-3 transition-colors"
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
    /// `Icon.caret`, never `Icon.down`: the arrow is the timeline's jump-to-latest.
    let fieldSelectMark = "pointer-events-none absolute right-3 top-1/2 -translate-y-1/2 text-ink-faint"

    /// The model picker's FACE, on the composer's row: a setting, so bare, and a bare control
    /// speaks the caps label voice (docs/visual-design.md, Hierarchy of controls) — the face
    /// Send's row-mate should wear beside Send rather than a 16px light line of grey that read
    /// as a placeholder.
    ///
    /// The face is a SPAN (`modelSelectFace`), and the real `<select>` lies over it with its
    /// text transparent (`modelSelect`). Two reasons it is not simply a `<select>` set in
    /// caps: iOS zooms the page into any focused field under 16px and leaves it zoomed, so
    /// the select must stay at `touchType` whatever the face is set in; and a native select
    /// does not ellipsize, it hard-clips — which is why this used to be a fixed `w-40` sized to
    /// its longest label. The face is ordinary text, so the wrapper is as wide as what it
    /// says and the select, filling the wrapper, is exactly as wide as the face. Transparent
    /// text rather than `opacity-0`, so the focus ring the select wears still paints.
    ///
    /// The wrapper's HEIGHT is the select's hit area, because the select fills it (`inset-0`),
    /// and that is the one thing about this control a thumb has to reach: the face is text and
    /// the box paints nothing. 40px is the composer row's own line; on a phone it grows to
    /// `spacing-touch`, which is what every control does there (docs/visual-design.md, Space)
    /// and all the growth costs is 4px of a row that already seats a 44px Send.
    let modelSelectWrap = "relative shrink-0 h-10 phone:h-touch inline-flex items-center gap-1.5 min-w-0"
    let modelSelectFace = cls [ caps; "text-ink-dim truncate pointer-events-none" ]
    let modelSelect =
        cls [ "absolute inset-0 w-full h-full p-0 bg-transparent border-0 appearance-none cursor-pointer"
              "text-transparent"; touchType; focusRing ]

    /// The caret `appearance-none` took away: `Icon.caret`, the chevron a disclosure points
    /// with. It was `Icon.down`, the arrow the timeline's jump-to-latest wears on the same
    /// screen, so one glyph meant "go to the newest" and "open this list" a few pixels apart.
    let modelSelectMark = "pointer-events-none text-ink-faint"

    /// A CODE to read and copy — the GitHub device flow's eight characters — with the verb
    /// that copies it at its trailing edge, built as the terminal composer's trail is
    /// (`terminalCommandWrap` / `terminalCommandTrail`): the wrapper carries the width, the
    /// box reserves the room, and the control sits INSIDE the box it acts on rather than
    /// beside it. The select's caret above is the other half of the pattern and deliberately
    /// not this — a mark takes no pointer events, a button is the thing you press.
    ///
    /// It is NOT a field, and it used to be drawn as one — `fieldFace` plus `fieldType`, so a
    /// ring on all four sides in the UI sans. Both halves of that were wrong. `ring` is for
    /// what you type in or press (docs/visual-design.md, Stroke) and nobody types in here: the
    /// code is minted by github.com and the one thing anybody does with it is take it away.
    /// And an identifier wears MONO everywhere else in the product — the session id under the
    /// title, the id on the starting screen, the build in the column's foot — so a device code
    /// in the chrome's own face was the one identifier set in a voice that is not for
    /// identifiers. `Stroke.clear` holds the border box while painting nothing on it, so the
    /// code stands exactly as tall and as wide as the fields above it and the row costs the
    /// same; `select-all` because taking it away is the whole point, and `buildMark` already
    /// says that is how a copyable identifier behaves.
    let codeBoxWrap = "relative w-full"
    let codeBox =
        cls [ "bg-surface h-control phone:h-touch px-3 w-full pr-10 phone:pr-13"
              "inline-flex items-center"; Stroke.clear
              "font-terminal text-code text-ink select-all truncate" ]
    let codeBoxAction = cls [ btnInField; "absolute right-1 top-1/2 -translate-y-1/2 text-ink-faint hover:text-ink" ]

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

    // --- The sidebar's way to settings and back ----------------------------------------
    // `settings ›` and `‹ back` are ONE control, mirrored: same row, same foot of the same
    // column, so pressing it leaves the word replaced and the mark flipped, in place. (The
    // head is identity — the wordmark, then the settings title — and never navigation; two
    // chevrons a thumb apart read as a pair of arrows rather than a way in and a way out.)
    //
    // It is a ROW, the column's own entry shape (`chapterEntry`), because it is one more place
    // the column takes you; the mark turns blue and steps the way it points.
    let navPivot =
        "group bg-transparent border-0 cursor-pointer flex items-center gap-2 "
        + "font-light text-small leading-5 text-ink-dim px-3 -mx-3 py-1.5 phone:min-h-11 "
        + "hover:text-ink hover:bg-surface focus-visible:text-ink transition-colors duration-150 ease-out "
        + "motion-reduce:transition-none " + focusRing

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
    /// A deed's verb (`Entity.verb`): light and dim, so the thing it was done to — an entity,
    /// a command's description — is where the eye lands. One class for every verb on the
    /// timeline, so "ran", "started" and "shared" cannot drift apart.
    let deedVerb = "font-light text-small text-ink-dim"
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

    /// The agent's mark: a dark square holding a small solid DIAMOND — the product's mark
    /// seen from above, its first frame — in `agent`, the shade under the interactive blue,
    /// so the agent and a control that can be pressed are two blues and not one. The square
    /// turned 45° is the whole of it: solid, one colour, no rim. The humans keep their
    /// checkers; a diamond is the agent and only the agent.
    let agentAvatar =
        "bg-agent-ground grid place-items-center after:content-[''] after:w-2 after:h-2 after:bg-agent after:rotate-45"

    let agentAvatarSm =
        "bg-agent-ground grid place-items-center after:content-[''] after:w-1.5 after:h-1.5 after:bg-agent after:rotate-45"

    /// A thing's mark — the session, the system, a repo's file: the agent's shape with the
    /// colour taken out. A dark square holding a small grey SQUARE, upright where the agent's
    /// is turned, so the two read as kin and never as each other, and grey because a colour
    /// on this page is somebody: blue the agent, the checker tones the people.
    let thingAvatar =
        "bg-surface-2 grid place-items-center after:content-[''] after:w-2 after:h-2 after:bg-ink-faint"

    /// The agent's mark with NOTHING connected: `agentAvatar`'s square and `agentAvatar`'s
    /// turned diamond, with the colour taken out of the diamond — the same sentence
    /// `thingAvatar` speaks one line up, and the shape still says agent while the grey says
    /// nobody is there.
    ///
    /// It was the live mark under `opacity-40`, and dimming is a ROLE, never opacity
    /// (docs/visual-design.md, Colour): an opacity floor is a contrast floor, and 40% took the
    /// diamond to about 2:1 against the panel it sits on. `ink-faint` is the faintest ink that
    /// still means something — 5.7:1 on the agent's own ground — so the state is worn rather
    /// than faded.
    ///
    /// The geometry is spelt out again rather than composed onto `agentAvatar`, because a
    /// second `after:bg-*` in the same class attribute is settled by the order Tailwind emits
    /// the two colours in, not by the order written here: that would be a mark whose colour
    /// depends on where a token sits in `app/tokens.css`. The two faces of this one mark want
    /// a shared base, and that is an edit to `agentAvatar` rather than an addition beside it.
    let agentAvatarAbsent =
        "bg-agent-ground grid place-items-center after:content-[''] after:w-2 after:h-2 after:bg-ink-faint after:rotate-45"

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
    /// What the screen says when it gives up: a SENTENCE — why it stopped waiting and what to
    /// look at — so the body voice in the err tone and not the caps status voice of the word it
    /// replaces. The caps voice is for a short label, and a stack of it is a wall of text
    /// (docs/visual-design.md, Type); this line is two sentences long when a proxy has not
    /// mapped the session. Centred on the screen's one column, and held to the measure, because
    /// the thing it replaces was one word wide.
    let startFailed = cls [ bodyErr; "mt-7 max-w-measure text-center" ]

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
    /// The hairline under it is the band's OWN, and the only rule in the column: groups below
    /// it are told apart by the space between them and by their heads, not by a line each.
    /// That is what keeps it at ONE height — it used to be the first group's top border, so
    /// the rule sat a pixel below the header's and moved down the column whenever a connection
    /// notice took the first slot (docs/visual-design.md, Space: the hairline under the band
    /// runs across every column at one height).
    ///
    /// BREAKS the second half of that rule, and says so here rather than pretending: on a
    /// phone the band is `spacing-band-phone` and this head is still `spacing-band`, so a
    /// drawer open over the conversation shows two hairlines 28px apart. It cannot take the
    /// phone band as it stands — the head holds the lockup, whose mark is 56px with its foot
    /// 15px below the wordmark's line box, and no bottom padding seats 71px of object inside
    /// 60px of band. The fix is a phone step on `lockup`/`lockupMark`/`wordmark` (Typography),
    /// after which this is one `phone:h-band-phone` and a smaller `pb`.
    let sideHead = "h-band shrink-0 flex items-end justify-between pb-band-foot " + Stroke.dividerBottom
    /// A group in the column: its head, then its rows. There is one of these and not two —
    /// a `sideSectionFirst` stood beside it, byte for byte the same value, left over from when
    /// the first group carried the rule the head now owns. A second name for one way of
    /// drawing something is a second way waiting to happen (principle 5).
    let sideSection = "flex flex-col gap-2 pt-6"
    let sideRow = "flex items-baseline justify-between gap-2"
    /// A roster row aligns on the TEXT BASELINE, so the 11px caps ("you", a status) sit on
    /// the 13px name's baseline instead of floating box-centred beside it.
    let person = "flex items-baseline gap-2.5 font-light text-small leading-5 text-ink-dim"
    /// Beside your own name, in the row's voice rather than caps: the roster's right edge is
    /// for what a person is DOING (a place, a verb), and being you is not news.
    let personYou = "text-ink-faint shrink-0"
    /// The avatar opts back out: a box has no baseline (it would park its bottom edge on
    /// the line), so it centres in the row the way it always did.
    let personAvatar = "self-center"

    /// A standing's mark in the column's status block (`standingsSection`): centred in the
    /// AVATAR's 20px column, so the marks stack on the axis the roster's avatars do and every
    /// word in the block starts on the rail the names below it start on.
    ///
    /// Vertically, placed by the font rather than by eye (the Space rule `agentMark` already
    /// answers to): the attention mark stays a fixed 12px, because its shape needs the room
    /// (`attentionMark`'s comment), but the small circle inside that box has to read as
    /// CENTRED on the word beside it, which means the x-height band, not the row's own
    /// line-box. `self-center` centred it on the latter — the row's full `leading-5`, which a
    /// font typically carries more of above the x-height than below it — and so the mark read
    /// high of the word every time, a pixel further off wherever a surface set the row's
    /// type at another size. Left on `items-baseline` (the row's default: no `self-*` here),
    /// the mark's own box starts flush on the baseline like a word does, and is carried up by
    /// half of what is left once the x-height is subtracted from the box's fixed height — so
    /// the shift is a formula in the two things that decide it (the box and the font), never a
    /// constant, and reads true at a size this file has not tried yet.
    let standingMarkSeat =
        "w-5 shrink-0 grid place-items-center translate-y-[calc((0.75rem_-_1ex)/2)]"
    /// A standing that GOES somewhere (the pull requests' line opens their rows in settings):
    /// the roster row's own shape, pressable. A thumb's height on a phone, centred there
    /// because a baseline row in a 44px box parks its words at the top of it.
    let standingRowPress =
        cls [ person; "w-full bg-transparent border-0 p-0 text-left cursor-pointer"
              "hover:text-ink transition-colors duration-150 ease-out motion-reduce:transition-none"
              "phone:min-h-touch phone:items-center"
              focusRing ]
    /// Where that row says it goes: the settings pivot's chevron, at the row's right edge.
    let standingRowGo = "ml-auto self-center text-ink-faint"
    let commandCard = "flex flex-col gap-1 px-3 py-2 bg-surface"

    /// The contents' entries, as one stack. Their own container rather than the section's, so
    /// the gap the section puts under its heading (`sideSection`'s 8px) is not also put
    /// between the entries: on a phone each entry is already a full 44px target
    /// (`chapterEntry`), and a gap on top of that spread two chapters 52px apart.
    let chapterEntries = "flex flex-col"

    /// One chapter in the contents: the roster row's shape, worn by a button.
    ///
    /// The same row as a person's, because the column holds one kind of list and a second
    /// shape here would read as a second kind of thing. What it adds is what a control has to
    /// have: a row's fill under the pointer, the ring a keyboard sees, and — on the one entry
    /// that is `aria-current` — the step-up fill and full ink that say "this is the one on
    /// screen". The padding is spent OUTWARD, so the text sits on the column's rail with
    /// everything else and the fill grows around it — on both sides, which is why the width
    /// is the column's plus both margins rather than `w-full`, a box shifted left by one.
    let chapterEntry =
        cls [ person; "w-[calc(100%+1.5rem)] text-left px-3 -mx-3 py-1.5 phone:py-3 hover:text-ink hover:bg-surface"
              "aria-[current=true]:bg-surface-2 aria-[current=true]:text-ink"
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
    /// against `surface` that `ink-dim` gives (WCAG 2.0 AA, pinned by Phase4's "Theme contrast").
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

    /// The ink a tone asks for, once, for every surface that is handed one.
    let toneInk (tone: Yession.Domain.Tools.QueryTone) : string =
        match tone with
        | Yession.Domain.Tools.ToneOk -> toneOk
        | Yession.Domain.Tools.ToneBusy -> toneBusy
        | Yession.Domain.Tools.ToneBad -> toneBad
        | Yession.Domain.Tools.ToneMuted -> toneMuted

    /// The ink a standing's attention asks for. A person's turn is green, because green is
    /// the people; the agent at work is the agent's own blue, a tone under the one a control
    /// is pressed in; a machine's work and an absent agent are a wait, dim; wrong is red;
    /// rest recedes.
    let attentionInk (attention: Yession.Domain.Standings.Attention) : string =
        match attention with
        | Yession.Domain.Standings.Wanted -> toneOk
        | Yession.Domain.Standings.Working -> "text-agent"
        | Yession.Domain.Standings.Pending
        | Yession.Domain.Standings.Absent -> toneBusy
        | Yession.Domain.Standings.Wrong -> toneBad
        | Yession.Domain.Standings.Rest -> toneMuted

    /// The attention mark's box and ink (`Icon.attention`): 12px, the chrome's 1.5px stroke,
    /// because its SHAPE carries the rank and a 6px disc has no room for a ring or a core.
    /// `overflow-visible` because Wanted's halo breathes out past the box, over nothing.
    let attentionMark (attention: Yession.Domain.Standings.Attention) : string =
        cls [ "block shrink-0 w-3 h-3 overflow-visible"; attentionInk attention ]

    // The mark's motion (docs/visual-design.md, Attention), one part each, every part turning
    // about its own centre (`fill-box`), which for every circle here is the box's (6,6). Motion
    // goes only where a state is live or asks, and `motion-reduce` keeps the still face.

    /// Pending's arc going round its track: the one mark that never stops, because the work
    /// under it has not. Under reduced motion the arc holds still at three o'clock.
    let markOrbit = "origin-center [transform-box:fill-box] animate-mark-orbit motion-reduce:animate-none"
    /// Wanted's halo: breathes out three times once the mark has arrived, then is gone, so a
    /// session left waiting an hour does not nag. Reduced motion draws no halo at all.
    let markHalo = "origin-center [transform-box:fill-box] animate-mark-halo motion-reduce:hidden"
    /// Wanted arriving: the ring the arc was closes round, then the disc fills it. Once, on
    /// arrival; reduced motion draws neither the ring nor the fill, only the disc.
    let markClose = "animate-mark-close motion-reduce:hidden"
    let markFill = "origin-center [transform-box:fill-box] animate-mark-fill motion-reduce:animate-none"
    /// Wrong arriving: its ring draws round from twelve o'clock and its core springs in.
    /// Once, then it holds; a mark that kept moving would read as a spinner, not a fault.
    let markDraw = "animate-mark-draw motion-reduce:animate-none"
    let markPop = "origin-center [transform-box:fill-box] animate-mark-pop motion-reduce:animate-none"

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
    /// padding — but only while the bar is there (`degradedShell`), and at the pace the bar
    /// unfolds at, so the conversation moves down with it rather than in one frame.
    ///
    /// FULL WIDTH, always — the header's band and its hairline (`header`, below), the
    /// degradation bar and the sign-in/refusal notices all read as a row that spans this
    /// column edge to edge, the sidebar's edge to the pane's. Capping this column (an earlier
    /// version of this fix did) crops every one of those bands along with the words, which is
    /// a layout change wearing a reading-comfort one's name. Only `chatColumn`, below —
    /// the timeline and the composer, the two things actually read and typed into — asks for
    /// a measure. The row above it does not.
    ///
    /// `--column-gutter` is that measure's margin, said ONCE, here, for the three things that
    /// read it: `chatColumn` stands in from it, `timeline` reaches back out across it (so its
    /// scrollbar is at this column's edge, not floating beside the words) and `header` pads its
    /// title by it (so the title starts where the words do). Zero except on a wide screen with
    /// the pane SHUT — why then, and why a `calc` off `100vw` rather than `mx-auto`, is
    /// `chatColumn`'s doc.
    let mainColumn =
        "flex-1 flex flex-col min-w-0 h-full " + degradedBarRoomPad
        + " phone:transition-[padding-top] phone:duration-200 phone:ease-out motion-reduce:transition-none"
        + " [--column-gutter:0px]"
        + " wide:[.term-closed_&]:[--column-gutter:max(0px,calc((100vw-var(--spacing-side)-(var(--spacing-measure)+var(--spacing-rail)*4))/2))]"

    /// The part of `mainColumn` that is actually read or typed into — the timeline and the
    /// composer, wrapped together so one margin holds both to the same edges. The header, the
    /// degradation bar and the two notices stand outside this and stay at `mainColumn`'s full
    /// width (see its own doc): a bar's hairline is layout, meant to span the row it sits in,
    /// and cropping it along with the prose was the bug this wrapper exists to undo.
    ///
    /// On a wide screen with the pane SHUT, nothing else claims the row, so `flex-1` alone
    /// would hand this column the whole width the sidebar leaves — a timeline and a composer
    /// band both stretched to whatever the monitor happens to be. Capped and centered instead,
    /// near the measure the open pane already holds it to (`PaneSplit.chatReadable`, Model.fs:
    /// the reading column's 38rem plus the 32px rail on each side) with one more rail's worth
    /// of room on each side again, so the timeline still has somewhere to show a row's ground
    /// stopping short of the column's own edge — flush to the measure, every row's highlight
    /// would reach exactly as far as the text, which reads as a box drawn round the words
    /// rather than as the row (the invariant `Browser.fs`'s "a message's ground stops at the
    /// reading column, not the window" holds desktop to). `[.term-closed_&]` only: the open
    /// pane already derives this column's width from the same number, through room a reader
    /// can drag past it on purpose (`PaneSplit.resolve`), and a static cap here would fight
    /// that drag.
    ///
    /// MARGIN, not `max-w` + `mx-auto`: the pane's own width animates shut over 200ms
    /// (`contentPanel`'s `wide:transition-[width]`), and an auto margin centers within
    /// whatever space is free AT THAT INSTANT — so as the pane closed, this column visibly
    /// RECENTERED under the words every frame, sliding the conversation sideways while it
    /// read, which is the one thing this shell promises never to do (see `contentPanel`'s own
    /// doc). The margin here (`--column-gutter`, set on `mainColumn`) is a `calc` off `100vw`
    /// instead: fixed the moment `.term-closed`
    /// lands, same on every frame of the pane's close, so the column's left edge — where every
    /// word starts — never moves. Only its right edge does, keeping pace with the pane as it
    /// closes, which is the pane sliding, not the column. `max(0px, …)` is the floor for a
    /// `wide` screen too narrow to hold the cap at all: the margin collapses to nothing and
    /// `flex-1` alone takes what the sidebar leaves, same as an uncapped column always did.
    let chatColumn = "flex-1 min-h-0 flex flex-col min-w-0 mx-(--column-gutter)"

    /// The phone's band is a ROW, not a compressed copy of the desktop's stack: the two
    /// doors a phone always has in this band — the sidebar's chevron and the terminals' mark — and the
    /// title between them, on one line, because that is what they are. Stacked, the 28/32
    /// heading over its id spent 80px of an 844px screen (10%) restating the tab you are on;
    /// the title steps down a size (`titleInput`) and the id keeps hanging out of flow under
    /// it (`titleId`), which seats the whole band in 60px.
    ///
    /// `pb-[15px]` leaves the row 44px over the band's 1px rule, which is what every control
    /// in it is on a phone: the chevrons, the edge tab and the title's own box are thumb-sized
    /// and fill the row exactly, and the id hangs into the 15px under it. It was 11px in a
    /// 56px band, which left the id's ink under 6px off the hairline.
    ///
    /// On a wide screen the stack stays: the header and the sidebar wordmark share one bottom edge
    /// (`h-band`, `items-end`), and that shared baseline is the whole reason the band exists.
    ///
    /// Both heights are the band's TOKEN — `h-band` wide, `h-band-phone` on a phone
    /// (docs/visual-design.md, Space: the band is `spacing-band`, and on a phone it is
    /// `spacing-band-phone`). The phone one was the literal `h-14`, which is the same 56px
    /// said in a second place, and a second place is where two heads come to disagree: the
    /// column's own head is still the full `spacing-band` under a thumb, so the two hairlines a
    /// drawer shows at once sit 32px apart (see `sideHead`).
    ///
    /// The left padding is the timeline's own plus `--column-gutter` (`mainColumn`), so the
    /// title stands in exactly as far as the conversation does: with the pane shut and the
    /// conversation centered, the title starts where the words start rather than at the
    /// band's edge (the title's own `ml-8` is the rail every body is inset by). Only the left:
    /// the band's right-hand group is the way to the pane, and it stays at the edge the pane
    /// opens from.
    let header =
        "relative h-band shrink-0 flex items-end gap-4 pl-[calc(var(--column-gutter)+2rem)] pr-8 pb-band-foot "
        + "phone:h-band-phone phone:items-center phone:gap-2 phone:px-4 phone:pb-[15px] "
        + Stroke.dividerBottom

    /// A slow catch-up's progress, drawn ON the header's bottom rule: the rule is the one
    /// line every screen has under the title, and a bar growing along it says "loading, this
    /// far" without taking a pixel of room from the band — which on a phone is 60px and
    /// geometry-pinned. Two pixels tall over the one-pixel rule, so it reads as a bar and not
    /// as the rule changing colour. Its width moves once per paced render (`Render`, every
    /// half second while a client stays behind), and the transition carries it between.
    let catchUpBar =
        "absolute left-0 -bottom-px h-0.5 bg-blue pointer-events-none "
        + "transition-[width] duration-500 ease-linear motion-reduce:transition-none"

    /// The header's right-hand group: the way back to the terminals. The session's standings
    /// are the sidebar's (`standingsSection`), and the band only wears the worst of them,
    /// under the chevron that opens it (`navReopenMark`). On a wide screen the door carries
    /// words ("2 terminals", `View.contentReopen`), and `pb-[1px]` stands them on the title's
    /// baseline (measured live at 1440: both at y=63; `pb-2` left the words 7px high). The
    /// mark beside them then rises onto the title's x-height on its own (`terminalReopen`).
    /// On a phone the group centres on the title's line.
    let headerAside = "ml-auto shrink-0 flex items-end gap-5 pb-[1px] phone:items-center phone:gap-3 phone:pb-0"
    let headerStatus = "shrink-0"

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
        cls [ "relative grid grid-cols-[var(--spacing-rail)_minmax(0,1fr)_auto] items-center px-8 py-2 bg-surface"
              Stroke.dividerBottom
              // Where the column IS on screen, the column says it. Where it is not — a
              // collapsed nav, or any phone — this does. Same rule as `navReopen`.
              "hidden wide:[.nav-alt_&]:grid phone:grid"
              // Over the header that follows it, so the disclosure's lines, which open below
              // the bar, are not painted under it.
              "wide:z-floating"
              // A phone shows one pane at a time and the other two are overlays anchored to
              // the top edge, so this leaves the conversation column's flow and sits over all
              // three. `z-50` clears the overlays (`z-40`) and the scrim between them.
              "phone:fixed phone:inset-x-0 phone:top-0 phone:z-50 phone:px-4"
              // Unfolding into place as the chat makes room for it (`mainColumn`).
              "phone:motion-safe:animate-bar-unfold"
              // One row, always, tall enough for the control it carries whether or not this
              // state has one — the panes reserve this number, so a height that tracked the
              // contents would leave a gap under the bar in some states and overlap the
              // header in others (it did: a wrapped 73px bar against a 36px reservation).
              // The disclosure does not grow it: its lines open BELOW the bar, over the pane
              // (`degradedBarDetails`) — a notice you opened is one you are reading.
              degradedBarHeight ]

    /// The bar's dot, in the gutter's own column: centred on the 20px an avatar takes there, so
    /// it stands over the conversation's avatars and the word beside it on their rail — the
    /// title's rail, and every message's under it (docs/visual-design.md, Space).
    let degradedBarMark = "flex w-5 justify-center"
    /// The word and its disclosure, on the rail. The only thing in the row that may be
    /// squeezed: a verb is useless truncated, and the disclosure is one word.
    let degradedBarWords = "min-w-0 flex items-baseline gap-3"
    /// The verb, at the row's end.
    let degradedBarVerb = "flex pl-4"
    /// Where the disclosure's lines stand once it is open: below the bar and over the pane,
    /// the bar's width, on the same rail as the word. INSIDE the bar's height they spilled
    /// over the header under it, because that height is a number the panes reserve.
    let degradedBarDetails =
        cls [ "absolute inset-x-0 top-full grid grid-cols-[var(--spacing-rail)_minmax(0,1fr)] px-8 pb-3 bg-surface"
              "phone:px-4 [&>*]:col-start-2"
              Stroke.dividerBottom ]

    // --- The status line: what the link is doing, on either mount ---------------------------

    let statusLineMarkWrong = cls [ syncDot; "bg-err" ]
    /// The word: one line, and the part of the row that gives way first.
    let statusLineWord = "min-w-0 truncate whitespace-nowrap"
    /// The nav column's row: the word on the column's rail and the verb at the row's end.
    /// `relative`, so the dot can hang to the word's left (`statusLineHung`).
    let statusLineRow = "relative flex items-baseline gap-3"
    /// The dot hung in the column's gutter: its 6px centred on the caps line's 16px, 8px clear
    /// of the word.
    let statusLineHung = "absolute -left-3.5 top-[5px] flex"
    /// The verb's place in the column's row.
    let statusLineVerbSlot = "ml-auto flex"
    /// Where the disclosure's lines stand in the column: under the summary, in its flow.
    let statusLineDetailsInColumn = "flex flex-col"
    /// The way back through the Manager, under the reason it answers (`View.reopenAction`).
    let statusLineReopen = "flex pt-2"

    /// A status line's verb: a bare word in the caps voice, the size of the line it rides. A
    /// notice is not a surface's verb, and the filled block it used to be — 44px in a 48px
    /// bar, the full width of the nav column on a desktop — was the loudest thing on a screen
    /// that was otherwise working. On a phone its box grows to a thumb and gives the growth
    /// back in margin, so the bar's height is still the number the panes reserve
    /// (`degradedBarHeight`).
    let private statusVerbBase =
        cls [ caps; bareBox; "inline-flex items-center shrink-0 whitespace-nowrap no-underline transition-colors"
              "phone:h-touch phone:-my-2.5"
              focusRing ]
    /// A verb that acts here and now (`Try again`): ink, because it is the one thing on the
    /// line to press, and dims under the hand.
    let statusVerb = cls [ statusVerbBase; "cursor-pointer text-ink hover:text-ink-dim pressed:text-ink-dim" ]
    /// A verb that goes somewhere (`Sign in again`, `Reopen session`): a link, so it is blue.
    let statusVerbLink = cls [ statusVerbBase; "text-blue hover:text-blue-up-1" ]

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
    ///
    /// It is a BARE verb — a word, no rectangle — so on a phone it grows its HIT AREA to the
    /// touch target and not its box (docs/visual-design.md, Space). Which matters most here of
    /// anywhere: this one word is the only way to read why a connection dropped, and at
    /// 13/16 it was a 16px-tall target on the surface a phone reader meets first. The area is
    /// an empty `::before` reaching 14px above and below the line and 4px past each end — 44px
    /// tall and wider than the word — and a press on a pseudo-element is a press on its
    /// element. An OVERLAY and not `hitTouch`'s padded box, and this is the one site that has
    /// a reason: the notice's height is a number the panes reserve
    /// (`degradedBarHeight`), so a box that really grew would move every pane that
    /// subtracts it. The box, the word
    /// and the focus ring drawn round it all stay exactly where they were, so the notice's
    /// height is still the number the panes reserve (`degradedBarHeight`).
    let detailNote = "min-w-0"
    let detailSummary =
        cls [ small; "cursor-pointer list-none"
              "phone:relative phone:before:absolute phone:before:content-['']"
              "phone:before:-inset-y-3.5 phone:before:-inset-x-1"
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
    ///
    /// The wrap, not the field, wears the title's rule (`titleInput` says why, and why not on a
    /// phone). The field pays its 8px padding back with `-mx-2`, so the wrap's box is the
    /// text's own extent: the rule starts at the first glyph and ends at the last. The field's
    /// box still reaches 8px past it, so `wide:mr-2` keeps the band's gap between that box and
    /// the terminals' door (measured live with a title the band cannot hold).
    let titleWrap =
        cls [ "relative wide:top-[2px] flex flex-col min-w-0 ml-8 wide:mr-2 phone:ml-3 phone:flex-1"
              Stroke.underline; Stroke.rim; "phone:border-b-0" ]

    /// The title itself: the heading, worn by a text input — and it says SO AT REST, with the
    /// one shape the `Stroke` vocabulary keeps for it (`Stroke.underline` in `Stroke.rim`).
    ///
    /// It carried that rule once, lost it to an argument that a rule "said it whether or not
    /// anyone was going to type", and was left saying it only under a pointer
    /// (`hover:bg-surface-2`). That argument is the one principle 4 refuses: what a thing is —
    /// here, that it edits — is visible at rest, because a thumb has no hover and a reader has
    /// no tooltip. A heading nobody hovers is a heading nobody learns is a field. So the rule
    /// is back, and `Stroke.underline` has its wearer again. The wrap round this field wears
    /// it (`titleWrap`), so the rule is the text's width and not the padded box's, and the
    /// field itself is bare (`bareBox`).
    ///
    /// What does NOT come back is the DASH. It was `border-dotted border-ink-faint` over this
    /// same `border-0 border-b`, and the dash had a name in the vocabulary (`Stroke.dotted`,
    /// "dotted until you are in it, then solid blue") that #1372 deleted along with the
    /// solid-on-focus half of it. A stroke here has a width, a tone and an answer to
    /// interaction, and never a pattern — so what is left of that rule is the shape, which is
    /// the one the `Stroke` header keeps for this control, in the tone that header gives a
    /// control at rest (`rim`, a step brighter than the quiet separator).
    ///
    /// `phone:border-b-0`, and this is a measurement rather than a preference. On a wide screen
    /// the box is the title's line alone, so the rule lands at y=68 with the id starting at 71:
    /// a heading with a rule under it, which is what it is. On a phone the box reaches down over
    /// the id (below), so the rule lands at 53-54 — flush under the id's own text box, which
    /// ends at 54, and one pixel of black above the band's hairline at 55-56. That draws a rule
    /// under the thing that does NOT edit, and puts a second hairline beside the one the
    /// doctrine runs across every column at one height (Space). Measured live at 390x844.
    ///
    /// So the phone keeps the fill and the ring and gains nothing at rest, which is the finding
    /// (S1-7) still open for a thumb. It does not close here because the band has no room: the
    /// rule belongs under the title's 24px line, and the 44px the floor asks of the field is
    /// what puts its bottom edge 20px further down. Moving it would mean moving the id out of
    /// the field's box, and a 60px band has nowhere to put it.
    ///
    /// ONE tone, and no hover or focus tone of its own, which is the opposite of what a `ring`
    /// does — because here the rule answers a different question from the fill. The rule says
    /// *this edits*; the fill and the ring say *you are editing it*. Giving the rule a second
    /// voice would be two mechanisms for the one requirement, which is what the `Stroke`
    /// header's interaction rule is for on a control whose only mark IS its border.
    ///
    /// The 8px padding is spent OUTWARD (`-mx-2`), so the glyphs do not
    /// move when the fill appears — the box grows around the text already on screen. Sideways
    /// only: a phone's band seats the id 2px under the title's line box, and vertical padding
    /// put the fill and its ring through it.
    ///
    /// As wide as its text, not as wide as an input's default 20 characters: the rule under
    /// "session" ran 300px, twice the word, and began 8px before it in the padding below.
    /// `field-sizing:content` sizes the box to the value, or to the placeholder while there
    /// is none, so an empty title still wears a rule as long as "session". It sizes the height
    /// too, and a placeholder's line is taller than the value's (38px against 32, measured
    /// live), so `wide:h-8` holds the box to the heading's line. The wrap is that
    /// wide, and the field stretches across it, so a long title the band cannot hold shrinks
    /// the wrap (`min-w-0`) and truncates as before. On a phone the wrap takes the row
    /// (`titleWrap`) and `phone:w-full` keeps the field the box it was, the thumb's target
    /// across it, clear of the terminals' door.
    ///
    /// `focusRing` is what a keyboard sees. The fill alone is a real focus signal for a
    /// pointer, but it is a tone step on a dark ground; the ring is the product's own
    /// vocabulary and the UI baseline's floor (AGENTS.md), so the two ride together.
    ///
    /// The wrap's `wide:top-[2px]` (`titleWrap`) is optical, and it moves this field and its
    /// rule together: a 28/32 line box holds its baseline 2px higher over the shared bottom
    /// edge than the wordmark's 32/36 does, so the title steps down to put both on one line
    /// (measured live). A phone has no cross-column baseline to meet and no room
    /// for 28px in a 56px row, so it takes the pivot step (19/24) and no nudge.
    ///
    /// And on a phone the field is a thumb's height, which a 24px line in a 60px band does
    /// not leave room for — unless the box takes in the id under it. So it does: 44px tall,
    /// its last 20 of them padding (`pb-5`) that the id hangs in (`titleId`), and the same 20
    /// paid back to the flow (`-mb-5`), so the row lays out the 24px line it always did. The
    /// text sits in the top 24, where it was; the fill and the ring go round the title and its
    /// id together, though a press on the id's own text is the id's, so it can be selected.
    /// `Render.placeInputCursor`'s marker subtracts the padding and the border off the live box,
    /// so a collaborator's caret stays the height of the line.
    let titleInput =
        cls [ bareBox; "[field-sizing:content] wide:h-8 phone:w-full min-w-0 bg-transparent px-2 -mx-2 py-0 phone:h-11 phone:pb-5 phone:-mb-5"
              "hover:bg-surface-2 focus:bg-surface-2 transition-colors"; focusRing
              "font-extralight text-heading phone:text-pivot tracking-[-0.01em] lowercase text-ink"
              "placeholder:text-ink-faint truncate relative" ]

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
    /// Its `mt-1` and its line are `--spacing-id-drop`, which the chevron's mark lands on
    /// (`navReopenMark`): change one and change the other.
    /// On a wide screen the wrap carries the title's 2px optical drop and its 1px rule
    /// (`titleWrap`), which together put the padding box's bottom 1px lower than the line the
    /// id hung from when the field wore both. `wide:top-[calc(100%-1px)]` pays it back.
    let titleId = "font-terminal text-code-sm text-ink-faint truncate mt-1 absolute top-full wide:top-[calc(100%-1px)] left-0 right-0 select-text"

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
    /// (`navChevronBase`) reaching nearly to the screen's edge and occupying 12px of flow (`-ml-3`
    /// and `-mr-5` against the band's 16px padding), which with the row's 8px gap pays 20 of the 32px
    /// indent the heading gives up there (`titleWrap`). Hidden while the sidebar is visible.
    /// On a wide screen it stands in by `--column-gutter` as the title does (`header`), so
    /// with the conversation centred it stays beside the title rather than at the window's
    /// edge, which is where #1521 left it (measured at 1280: chevron at x=15, title at 306).
    /// And it stands on the title's LINE, as it does on a phone: the title's middle is 35px
    /// over the band's floor (`pb-band-foot`, the field's 1px rule, half its 32px line, less
    /// the field's 2px `top` nudge), and the foot and 10px, with the 6px `-m-1.5` takes back,
    /// puts the glyph's middle on it (measured live at 1280 and 1600: glyph and title both at
    /// y=86). It is placed from the foot's token so the two cannot part when the band grows.
    /// It sat on `bottom-4.5` once, 12px under the title's middle.
    ///
    /// Across, it stands on the AVATAR's column: the glyph and the mark under it centred on the
    /// middle of the `w-5` every group head's avatar fills (`messageGroupHead`), so the band's
    /// marks and the conversation's share one vertical line. A wide screen names that middle
    /// from the tokens — the rail and half the avatar — less half the 14px glyph, which starts
    /// at `left` because the box's `-m-1.5` and `p-1.5` cancel (measured live at 1024 and
    /// 1440: glyph, mark and avatar all centred on one x). It sat at `gutter + 0.5rem`, 27px
    /// left of the avatars. A phone's avatar middle is the 16px
    /// padding and half the avatar, 26px from the edge: the 44px target starts 4px in
    /// (`-ml-3` against the band's 16px) and pays the other side back (`-mr-5`) so the title
    /// does not move. `-mx-4` centred it at 22px, 4px left of every avatar under it.
    ///
    /// Down, the glyph points at the middle stroke of the title's lowercase `e`, not at the
    /// middle of its line box: a lowercase title's ink is its x-height, and the line's middle
    /// is above it. Noto Sans ExtraLight holds that stroke 0.28em over the baseline, which
    /// lands 3.2px under the line's middle at 28/32 and 1.7px under it at 19/24 (measured
    /// live at 390, 1024 and 1440 by scanning a drawn `e`). Only the GLYPH drops
    /// (`*:first`, the button's first child; not `[&>svg]`, because a `>` in a class string
    /// ends the tag for the tests that read classes off the render, and not `[&_svg]`,
    /// because the mark is an svg too), so the box, and the mark placed from it (`navReopenMark`), stay on the
    /// id's line. The terminals' mark across the band drops the same way (`terminalReopen`).
    let navReopen =
        "absolute left-[calc(var(--column-gutter)+var(--spacing-rail)+0.625rem-7px)] bottom-[calc(var(--spacing-band-foot)+10px)] w-6 h-6 place-items-center hidden wide:[.nav-alt_&]:grid "
        + "*:first:translate-y-[3px] phone:*:first:translate-y-[1.5px] "
        + "phone:relative phone:inset-auto phone:grid phone:[.nav-alt_&]:hidden phone:-ml-3 phone:-mr-5"

    /// The session's worst standing, under the reopen chevron: the column it opens is where
    /// the standings are said, so the way in carries the one that matters most. Centred on
    /// the chevron's axis, and centred on the session id's LINE: the chevron sits on the
    /// title's line, so the pair reads as one column beside the two lines, the glyph against
    /// the title and the mark against the id. It adds a mark and not a pixel to the band.
    ///
    /// It is placed from the chevron's box, and the id from the title's, so the drop between
    /// them is named once (`--spacing-id-drop`, `titleId`'s `mt-1` plus half its 16px line)
    /// and the rest is where each screen puts the two boxes:
    ///
    /// - A phone centres both on one line, so the title line's middle is the 44px target's
    ///   middle, and the id's is 12px (half the 24px `text-pivot` line) and the drop under it.
    ///   The glyph is centred in the target, so the target's middle is the glyph's.
    /// - A wide screen bottoms the title on the band's `pb-band-foot` and lifts the box with
    ///   its glyph onto the title's middle (`navReopen`), which leaves the id's middle, the
    ///   drop under the title's foot, the drop and 4px under the box's. The 14px glyph
    ///   overflows the 24px box's 12px of content to the right, so its middle is 13px in (the
    ///   `p-1.5` and half the glyph), not the box's 12.
    ///
    /// `-translate-y-1/2` makes it the mark's centre that lands, so the mark's own size never
    /// moves it. Measured live: at 390 the mark hung 8px over the id's line when it was placed
    /// from the target's foot, and at 1280 10px under it when the margin was not counted.
    let navReopenMark =
        "absolute left-[13px] -translate-x-1/2 -translate-y-1/2 pointer-events-none "
        + "top-[calc(100%+4px+var(--spacing-id-drop))] "
        + "phone:left-1/2 phone:top-[calc(50%+12px+var(--spacing-id-drop))]"

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
    ///
    /// NOT DRAWN while the column has less room than one of its items (`too-short`,
    /// app/tailwind.css). A column that scrolls and cannot be touched promises what it cannot
    /// give, and the honest answer is absence (docs/visual-design.md, Space). At 844x390 the
    /// ask card's floor left this column 24px with 608px of extent under it, and every one of
    /// its fourteen items, scrolled to the middle, landed under the author line the column
    /// pins — which was the whole strip. `visibility: hidden` and not a cover, because a drawn
    /// column is one Tab reaches: the fault the card stopped being an overlay to fix. An
    /// invisible one is out of the focus order, out of hit-testing and out of the
    /// accessibility tree, as a `display: none` one is. What takes the room carries the way
    /// back, and here that is the card's own way out (`askWay`): the conversation is one
    /// press away for as long as it is not drawn.
    ///
    /// Not `display: none`, which it was: a scroller taken out of layout LOSES ITS SCROLL, and
    /// comes back at its top. On a phone with the keyboard up, a composer five lines deep left
    /// this column about 90px; the sixth line took it under 72, and a reader two hundred
    /// messages down came back at message one. Worse when the reader was far back: the tray
    /// offering the way to the latest took 44px of the column, the column went, `Tail`
    /// measured a scroller with no box as at its end, the model stopped offering the way, the
    /// tray left, the column came back at its top, was measured far back, and the tray
    /// returned — a render a frame, each laying the whole conversation out again, until the
    /// typing stopped. `visibility` keeps the box and the scroll position in it, so there is
    /// nothing to lose and nothing to measure wrongly (`Tail.tell` refuses a boxless surface
    /// too, for the next thing that takes the box away).
    ///
    /// ALWAYS SCROLLS, by one pixel at least, so it bounces like a native iOS scroll view
    /// even when there is nothing more to see: iOS rubber-bands only a box that overflows,
    /// and hands the gesture of one that does not to the document, which `html`'s
    /// `overscroll-behavior: none` (app/tailwind.css) holds still. The pixel is the `::after`
    /// — absolutely placed just under the box (`relative` makes the box its containing
    /// block), so it adds scroll without entering the flex column the `::before` fills, and
    /// sits inside a long conversation's content where it adds nothing. It takes no presses:
    /// inside that content it lies across whichever item happens to be one box-height down,
    /// and a press there landed on the scroller instead (at 1440x900, the middle of the sixth
    /// item under the launch card). `overscroll-y-contain`
    /// keeps the bounce in here rather than chaining it to the page. The pixel means "at the
    /// end" cannot be `scrollTop + clientHeight = scrollHeight` with no slack: see
    /// `Tail.atEnd` (app/browser/Tail.fs), whose 4px slack is what absorbs it.
    ///
    /// THE SCROLLBAR IS AT THE COLUMN'S EDGE, never beside the words. With the pane shut the
    /// conversation stands in by `--column-gutter` (`mainColumn`), and a scroller that stood in
    /// with it drew its bar in mid-screen, a grey stripe down the right of the reading column
    /// with half the page beyond it. So the scroller reaches back out across the gutter (the
    /// negative margin) and puts the same distance back as padding: the words stay exactly
    /// where they were, the bar sits at the edge of `mainColumn` the way a document's does,
    /// and a wheel anywhere over the gutter scrolls the conversation too. Thin, on a clear
    /// track, in `edge`: there to find when you look for it, and nothing to read when you
    /// do not, which is what the platforms' own overlay scrollbars do.
    let timeline =
        "relative flex-1 min-h-0 overflow-y-auto overflow-x-hidden overscroll-y-contain "
        + "mx-[calc(var(--column-gutter)*-1)] px-[calc(var(--column-gutter)+2rem)] pb-6 flex flex-col gap-6 "
        + "[scrollbar-width:thin] [scrollbar-color:var(--color-edge)_transparent] "
        + "after:absolute after:inset-x-0 after:top-full after:h-px after:pointer-events-none "
        + "before:grow [&>*:first-child]:mt-0 too-short:invisible "
        + "max-md:px-4 max-md:pb-4 max-md:gap-5 max-md:before:-mb-1 break-words "
        + focusRingInset

    /// The box around `timeline`, the scroller. Nothing floats over it any more: the way back
    /// to the latest message stands in the composer's tray (`composerTray`), below the words
    /// rather than on them.
    ///
    /// It is the SIZE CONTAINER the room rule reads (`room-for-items`), because this box is
    /// what the column was actually given — the screen less the header, the composer and the
    /// ask card standing beside it — and that is a height no media query can name. A container
    /// cannot query itself, which is why the rule lands on the scroller inside rather than
    /// here. Containment costs nothing: this box never sized itself from what is in it, it is
    /// `flex-1` of a column whose height is imposed from above.
    let chatRegion = "relative flex-1 min-h-0 flex flex-col room-for-items"

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
    /// is carried in flow rather than as the scroller's padding, so `top-0` pins this
    /// line flush to the scrollport's own top edge and there is no strip left above the name
    /// for scrolled text to show through. The rule that fixes it lives with the box that
    /// creates it, which is why it is stated there and not compensated for here.
    /// It was `pb-1`, and the extra step is the boxes' doing: the ground under the label now
    /// has to clear the first item's own GROUND rather than just its first line, and that
    /// starts 8px above the words in it. At `pb-1` a hovered first message came within four
    /// pixels of the name.
    /// The phone's FULL BLEED, exactly as an item carries it (`itemGround`): the band escapes
    /// the scroller's padding and puts the same back, so the opaque strip reaches both edges of
    /// the screen and the name still stands on the line. Without it the band stopped at the
    /// column's padding while every item under it ran 16px past, so a scrolled item's own
    /// trailing control showed in that strip, beside the author line pinned over it.
    ///
    /// PINNED, it is a BAND, not a label left floating. It was the name on 28px of black flush
    /// against the header's rule, with the ground ending 8px under the name: at 1440x900 the
    /// name sat 10px under the rule and the next line was sliced across its glyphs right
    /// beneath it, so the pinned line read as a stray label over a cut. Now the ground holds
    /// the name in the middle of the band — `pt-2` over it to match the `pb-2` under it, and
    /// `-mt-2` so in flow the name stands where it did and the pad spends the gap above — and,
    /// while it is stuck, that ground is the `acrylic` with its rim on the lower edge
    /// (`Stroke.acrylicBottomPinned`): what scrolls under it is blurred out of reading rather
    /// than cut, and the band's end is an edge the reader can see. At rest nothing is under
    /// it, so it is the column's own black and every author line in the conversation stays
    /// as it was. A browser without the `stuck` query keeps the black band.
    ///
    /// The ground is the band's `::before`, not its own background, because a scroll-state
    /// container's query styles what is IN it and never the container itself. The blur and
    /// tint are `acrylic`'s, spelled out under the variant, because Tailwind writes only the
    /// classes a `.fs` file spells as a literal.
    let messageGroupHead =
        cls [ "sticky top-0 z-10 flex items-center gap-3 pt-2 -mt-2 pb-2 -mb-1 max-md:-mx-4 max-md:px-4"
              "[container-type:scroll-state] before:absolute before:inset-0 before:-z-10 before:bg-bg"
              "stuck:before:bg-acrylic stuck:before:backdrop-blur-acrylic stuck:before:backdrop-saturate-150"
              "reduced-transparency:stuck:before:bg-panel reduced-transparency:stuck:before:backdrop-blur-none"
              "reduced-transparency:stuck:before:backdrop-saturate-100"
              Stroke.acrylicBottomPinned ]

    /// Catching up, drawn as the author line that will take its place (`View.chat`): its dot
    /// centred in the box an avatar fills, so the words stand where a name will.
    let readingMark = cls [ avatar; "grid place-items-center" ]

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
    /// And it is the RAIL's width, composed from the token that names it rather than spelled
    /// as a length: the column has one rail, every line of text in it starts on that rail, and
    /// a mark that leads a line hangs in the gutter to its left (docs/visual-design.md, Space).
    /// This grid IS that gutter, so the number it is wide is the token's or it is a second
    /// answer to where the rail is.
    let foldRow = cls [ "grid grid-cols-[var(--spacing-rail)_1fr] leading-5" ]
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
    ///
    /// `items-center` centres the chevron on `1lh` — the row's own `leading-5`, 20px — but
    /// the title beside it sets its OWN line box (`text-small`, 16px) and sits at the TOP of
    /// that taller cell, not its middle, so the two centres are not the same point. Measured
    /// against the real title (the compiled title's crossbar on "used") rather than guessed:
    /// the chevron's point sat 0.7px below where the title's own ink sits, at every size this
    /// mark is drawn at (`box14`, the one size `fold` ever uses) — a constant nudge, not a
    /// font-metric formula, because the gap is between a GLYPH'S drawn crossbar and the box
    /// it rides in, which CSS has no unit for (the pattern `Icon.checkSm` and kin already use
    /// for the same reason).
    let fold =
        cls [ "col-start-1 justify-self-start w-5 h-[1lh] flex items-center justify-center -translate-y-[0.7px] cursor-pointer bg-transparent border-0 p-0"
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
    let messageMeta = "flex items-baseline gap-2.5 pl-rail"
    let who = caps + " text-ink-dim"
    let whoAgent = caps + " text-blue"
    /// `pl-rail` is THE rail of this column — `spacing-rail`, the one every line of text in it
    /// starts on, with the group head's avatar and every leading mark hanging in the gutter to
    /// its left (docs/visual-design.md, Space). It was `pl-8`, which is the same length written
    /// as a number: the same number the fold's gutter and the chips each spelled their own way,
    /// so the column had one rail by coincidence and three places to change it.
    // No weight here: these compose with `messageVoice`, which owns the body weight.
    let messageBody = "pl-rail text-body text-ink"
    let messageBodyStreaming = "pl-rail text-body text-ink-dim"

    /// A detached reply's quote of what it answers — one quiet line above the body, dimmed so
    /// it reads as context rather than as the message. The quote truncates in one line so a
    /// long parent cannot push the reply itself down the screen.
    ///
    /// A ROW with a gutter (`foldRow`), not a padded flex line: the corner mark LEADS this
    /// line, so it hangs in the gutter left of the rail and the words start on the rail with
    /// every other line in the column. Padded and flexed, the mark stood ON the rail and shoved
    /// the quote in by its own width plus a gap — a fourth left edge in a column that has one.
    let replyRef = cls [ foldRow; "items-baseline text-small text-ink-faint" ]
    /// The mark, in the gutter: the avatar's own width at the gutter's start (`w-5`), as every
    /// other mark this margin carries stands there (`fold`, `causeMark`).
    let replyRefMark = "col-start-1 justify-self-start w-5 flex justify-center text-ink-faint"
    /// `pr-px` beside `truncate`: italic leans its last glyph's ink past its own advance
    /// width, and a box whose `overflow: hidden` edge sits flush on that advance width
    /// slices the overhang off with no ellipsis to show for it — text measured as fitting
    /// exactly, rendered one stroke short ("The session resumed" losing the tail of its
    /// "d"). The truncate/ellipsis path is unaffected: it still fires on real overflow:
    /// this is for the near-exact-fit case where the browser never thought it had any.
    ///
    /// No FACE here, on purpose. The typeface says who is talking before a reader reaches the
    /// author line (docs/visual-design.md, Type), and what this holds is whatever the parent
    /// said — so the use site composes `messageVoice` for the parent's own author. Set here it
    /// inherited the chrome's sans, which put a person's quoted words in the machine's face,
    /// the one thing that rule forbids. The act's cause reuses this value with no voice
    /// composed over it, which is right: that sentence IS the machine's account.
    let replyRefQuote = cls [ foldContent; "truncate italic pr-px" ]
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
        cls [ replyRef; "w-full text-left"
              "bg-transparent border-0 p-0 cursor-pointer hover:text-ink"; focusRing ]

    /// The mark's BOX, with nothing said about what it means: a 7px bar sitting on the text's
    /// baseline, painted blue. Two marks wear it — the empty column's still one (`caretIdle`)
    /// and the agent's pulsing one (`caretWorking`) — and the MOTION is the whole difference
    /// between them, which is the vocabulary this app already had and had stopped spending.
    /// The paint is named once here rather than appended per mark: a second `bg-*` beside this
    /// one would be two utilities on one property, resolved by the cascade and not by this file.
    let private caretBar = "inline-block w-[7px] h-[15px] bg-blue align-[-2px] ml-0.5"

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
        "inline-block w-[calc(1ex_+_0.06em)] h-[calc(1ex_+_0.06em)] align-[-0.03em] bg-agent "
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
    /// What it turns: the tip and the cube inside it, drawn by `Icon.cube`.
    let agentThinkingTip = "agent-think-tip"
    let agentThinkingCube = "agent-think-cube"

    /// Working's attention mark (`Icon.attention`, `Icon.cube`): the very cube above, turning in
    /// the attention mark's own 12px box (`.agent-think-mark` in app/tailwind.css) instead of
    /// the line of text a thought stands on — the agent at work is one claim whether it is read
    /// inline in the transcript or ranked beside a session's other standings, so it wears the
    /// one mark rather than a shape of its own (the running stroke round a diamond this mark
    /// used to draw, `markRun`, is retired with it). `block`, not inline: there is no baseline
    /// to sit on here.
    let attentionCube = agentThinking + " agent-think-mark block shrink-0"

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

    /// The empty timeline's own mark, standing where the first message will land. On the
    /// column's rail (`spacing-rail`), so the first real message appears exactly where the mark
    /// was standing rather than stepping sideways as it replaces it. It said `pl-8 max-md:pl-8`
    /// — the same length twice, once guarded by a breakpoint that could only ever restate it.
    let timelineIdle = "pl-rail"

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
    ///
    /// The share is a FLOOR, not a fraction. It was `h-[60%]`, and a share is only a share
    /// while the column has room to share: at 844x390 — a phone on its side, which this
    /// stylesheet says is still a phone — 60% of the conversation's room is about 167px, and
    /// the question and the START row alone want more than that. So the field, the list and
    /// CONNECT GITHUB were clipped to nothing by the `overflow-hidden` above, and a person on a
    /// sideways phone with no GitHub connected could not reach the one control that would let
    /// them answer the card at all. `max()` takes whichever is larger, `spacing-ask` names the
    /// least room the card can be answered in — its question, its field, one row and its verb
    /// — and `max-h-full` is what keeps the floor from ever exceeding the column: on a screen
    /// too short for both, the card takes the column and the conversation is one × away, which
    /// is the trade the floor is worth (docs/visual-design.md, Deciding: the floor is never
    /// traded). The paddings come in a step on a phone for the same reason — every 8px there
    /// is 8px the list does not get.
    let ask =
        cls [ "relative shrink-0 h-[max(60%,var(--spacing-ask))] max-h-full flex flex-col"
              "pt-6 pb-6 phone:pt-4 phone:pb-4 bg-surface overflow-hidden"
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
    /// `leading-4` states the head's FIRST line box once, so the mark that leads the line can
    /// be one of it (`askWay`'s `h-[1lh]`) rather than a hand-guessed height nudged down by a
    /// margin. The words set their own steps under it (`askSubtitle` is this same 16px,
    /// `askQuestion` brings the heading step's), so nothing in the words moves.
    let askHeadLine = cls [ askMeasure; "flex items-start gap-3 leading-4" ]
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
    ///
    /// Its box is ONE LINE of the head (`h-[1lh]`, on the line `askHeadLine` states), which is
    /// how every other mark in this file lands on the line it leads (`fold`, `causeMark`). It
    /// was `h-5 mt-1`: a 20px box pushed 4px down over a 16px line sitting above a 32px one, so
    /// the glyph spanned 4 to 24 and was centred on neither of the head's two lines — a mark on
    /// no rail at all (docs/visual-design.md, Space: rows align on the text baseline).
    let askWay =
        cls [ "w-5 h-[1lh] shrink-0 grid place-items-center bg-transparent border-0 cursor-pointer transition-colors"
              "phone:w-11 phone:h-11 phone:-mx-3 phone:-my-3.5"
              "text-ink-faint hover:text-ink"; focusRing ]

    /// The navigation voice: the wordmark's family, lowercased, at the heading step. Not
    /// `heading`, which truncates — a question is allowed to take the second line it needs.
    let askQuestion = "font-extralight text-heading tracking-[-0.01em] lowercase text-ink min-w-0"

    /// The way in that is not the list — a search, or a link pasted. Its BLOCK, on the card's
    /// edge margin beside START, because what sits in it is a control with a rim and a rim held
    /// one rail off one edge and the edge margin off the other is a slab pushed sideways rather
    /// than a margin — the reason START sits here too (`askActions`).
    let askField = cls [ askEdge; "mt-6 phone:mt-4" ]

    /// And the field itself: THE field face, the one every input in the product wears — the
    /// surface tone in a hairline ring that brightens under the hand and goes blue on focus.
    ///
    /// It used to be `Stroke.underline` with `outline-none` and no ring, which broke two rules
    /// at once. `underline` is the one shape exception in the `Stroke` vocabulary and it
    /// belongs to the session title, a heading that edits in place — a second wearer makes it a
    /// variant, and a second way to draw a field is a bug rather than a variant (principle 5).
    /// Worse, its focus was then a one-pixel colour change on a rule, with `outline-none`
    /// cancelling any ring that might have been declared beside it: a field that opts out of
    /// the product's one focus face is not keyboard-operable in the way the floor means, and
    /// the cheap tier's "every input draws the one field face, or draws nothing" passed it only
    /// because `border-0` made it read as bare.
    ///
    /// `phone:h-touch` is the growth every control takes under a thumb (docs/visual-design.md,
    /// Space); the measure is the card's, because a search box run across a desktop column is
    /// a line the eye loses its place returning from.
    ///
    /// `pl-rail` is what puts the TEXT in it on the card's rail while the BOX stands on the
    /// card's edge: the rail is one `spacing-rail` in from that edge, so the gutter inside the
    /// field is the same gutter every other line in the card has, and a reader sees one left
    /// edge for the question, the placeholder and the names. It is the face's `px-3` written
    /// longhand on one side rather than a second answer to it — Tailwind emits `pl-*` after
    /// `px-*` by construction, and the trailing inset stays the face's.
    let askSearch = cls [ field; readingColumn; "phone:h-touch pl-rail" ]

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
    ///
    /// `aria-disabled:` and not `disabled:`, because the attribute `disabled` does not exist in
    /// this product (docs/visual-design.md, State) — a row with nothing to act on while the
    /// clone it started runs stays in the layout and in focus order, and its press is refused
    /// by the handler rather than by the user agent taking the control out of the document's
    /// reach.
    let askRowButton =
        cls [ askInset; "w-full text-left flex h-12 bg-transparent border-0 cursor-pointer min-w-0"
              "aria-disabled:cursor-default"; focusRing ]
    let askRowLine = cls [ askMeasure; "flex items-center gap-3" ]
    /// The repo's name at the READING size, in the terminal face. It is the content of this
    /// surface, not chrome on it, and at the mono ramp's 12px — the step for output and ids —
    /// four of them read as a log rather than as the four things you are choosing between.
    let askRowName = "font-terminal text-body text-ink truncate min-w-0"
    /// The tick, out in the MARGIN where the column's marks go: `-ml-rail` against the button's
    /// own `gap-3` hangs it in the gutter the names are measured from, so the mark lands on the
    /// avatar's column and the name on the reading edge — one rail, named by the token that
    /// says where it is. Drawn on every row and filled only on the held one — a cell that
    /// appeared with the tick would move every name out from under the pointer that chose it.
    let askRowMark = "-ml-rail w-5 shrink-0 self-center text-blue"
    /// What the provider said about the repository, when there is room for it. There is not, on
    /// a phone: held to 45% of a 326px column it took 147px off a name that is the only thing
    /// on the row anybody is choosing between, so the name was cut while its description kept
    /// every character of its share. The name is the content; a phone shows the content.
    let askRowDescription = cls [ small; "truncate min-w-0 ml-auto max-w-[45%] phone:hidden" ]
    /// The branch a held row will launch on, and the way into the pane that changes it. Blue
    /// because it is a link; the chevron because it goes somewhere, and that somewhere is to
    /// the right.
    ///
    /// Its own LINE under the row's name, and a SIBLING of the row's button rather than a child
    /// of it. It was inside it: a button within a button, which is not content a browser will
    /// parse as written and which no keyboard can reach the inner half of — the floor, and
    /// never traded (docs/visual-design.md, Deciding). Two controls side by side on the row's
    /// one line would each have had to give up the other's width; stacked, the row keeps one
    /// full-width hit target for "this is the one I mean" and the branch rides under it, on the
    /// same rail the name stands on.
    let askRowAside = cls [ askInset; "pb-2" ]
    let askRowAsideLine = cls [ askMeasure; "flex items-center" ]
    /// A bare verb has no edge, so it takes the height of its line and grows only its HIT AREA
    /// on a phone (docs/visual-design.md, Space): `py-3.5` on a 16px caps line is the 44px
    /// target, bought in padding rather than in a box.
    let private askRowBranchBox =
        "shrink-0 inline-flex items-center gap-1.5 bg-transparent border-0 phone:py-3.5"
    let askRowBranch =
        cls [ caps; askRowBranchBox; "cursor-pointer"
              "text-blue hover:text-blue-up-1 transition-colors"; focusRing ]
    /// The same link with nothing to act on — the clone it is a detail of is already under way.
    /// `bareInkWaiting` is the bare weight's waiting face (docs/visual-design.md, State): one
    /// role further back in ink, still legible, still in focus order, and with no hover promise.
    /// An ALTERNATIVE to the face above and never a layer over it, so neither names a colour
    /// the other also names.
    let askRowBranchWaiting = cls [ caps; askRowBranchBox; bareInkWaiting; focusRing ]
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
    let askActions = cls [ askEdge; "shrink-0 pt-6 phone:pt-4" ]
    /// At the card's own measure, like everything else on it. Without it the row was the band,
    /// and the button that fills it on a phone (`askStartWidth`) filled 780px of an 844-wide
    /// sideways phone — a slab, and the loudest thing on a card whose question it dwarfed.
    let askActionsLine = cls [ askMeasure; "flex flex-wrap items-center gap-4" ]
    /// Full width on a phone, where a thumb is the pointer and the band is the screen; on a
    /// desktop it takes the room its word needs. `phone:`, not `md:`: the card is the shell's
    /// layout, and the shell's layout has ONE breakpoint, the question *is this a phone?*
    /// (docs/visual-design.md, Phone and wide). `w-full md:w-auto` was the exact bug that pair
    /// exists to stop — on a phone turned sideways both matched, so the widest thing on a card
    /// too short to answer was the button that could not yet be pressed.
    let askStartWidth = "phone:w-full"
    /// The commit button: the card's one verb, and the only filled rectangle on this surface.
    /// On `btnPrimarySwap` rather than `btnPrimary`: pressing it holds through `Sent`/`Cloning`
    /// (`Launch.LaunchStage`), and "Start"/"starting…" share its box the same way Create's two
    /// words do, so admission does not resize the one control the card asks a thumb to find
    /// twice. No height of its own: `btnFace` is `h-control`, growing to `spacing-touch` under
    /// a thumb, and the `h-12` this carried was a third height in a product that has two.
    let askStart = cls [ btnPrimarySwap; askStartWidth ]
    /// The same button with NOTHING HELD yet. `btnWaitingSwap` is the waiting face on the same
    /// box-stable layout, so the one control a thumb has to find twice is one size waiting,
    /// ready and held (docs/visual-design.md, State).
    ///
    /// A face, not an override. It was `disabled:bg-transparent disabled:border-hair
    /// disabled:text-ink-faint` layered over the fill — three `disabled:` utilities each
    /// fighting one the fill had already named, to stand in for a face that exists; and
    /// `disabled` is an attribute this product does not have, which is why the override had to
    /// exist at all. The border carries the waiting and the word stays legible, which is what
    /// the face says and what `disabled:text-ink-faint` over a blue fill (1.05:1, had either
    /// utility lost) did not.
    let askStartWaiting = cls [ btnWaitingSwap; askStartWidth ]
    /// The empty timeline's mark: the caret's box, STILL, and at full strength. It wore the
    /// blinking caret with `opacity-50` over it, and that is nothing twice. `blink` is
    /// `steps(1)` — gone for half of every second — and the opacity took what was left of the
    /// other half under the floor, so a photograph of an empty session at any of the three
    /// anchors caught a column saying nothing at all. Dimming is a role, never opacity
    /// (docs/visual-design.md, Colour), and a blink has nothing to say where nothing is
    /// happening and nobody has typed: the bar's JOB here is to stand where the first word
    /// will land. Blue, because that place is interactive, and the one mark that moves on this
    /// screen stays the agent's (`caretWorking`).
    let caretIdle = caretBar


    /// Present to a screen reader, absent to everyone else. For a state whose whole expression
    /// is a moving mark: the pulse says "reading" to people who can see it, and this says the
    /// same thing to the people who cannot.
    let srOnly = "sr-only"

    /// A file input inside the `<label>` that is its face (`focusRingHeld`): out of sight, but
    /// still in the focus order and the accessibility tree, which `hidden` takes it out of.
    /// It used to be `hidden`, with a button beside it reaching into the DOM to click it. The
    /// bare face (`border-0`) is the one every field without a face of its own declares, and
    /// it takes a phone's field size (`touchType`) as any focusable field does, though nobody
    /// sees its type: a phone zooms in on a focused field by its size, not by whether it shows.
    let pickerInput = cls [ "sr-only border-0"; touchType ]

    /// Terminal work in the chat (Plan 14, stage 1): one line, subtler than a message, and a
    /// real `<button>` — so it is keyboard-operable and focus-ringed by construction rather
    /// than by a handler bolted onto a div.
    ///
    /// It starts on the column's RAIL (`pl-rail`), so a run of chips between two messages lines
    /// up with the prose rather than drifting under the avatars. It said `pl-[32px]`, which is
    /// the same length as the rail written as a number — the column had one rail by coincidence
    /// and three places to change it. The palette stays `text-ink-dim`/`text-ink-faint` — chips
    /// are the busiest thing the chat will carry, and they must read as texture next to what
    /// people said.
    ///
    /// Disabled is a command the agent asked for whose terminal is not known yet: the chip
    /// stands where its block will be, with nothing yet to open, so it neither points nor
    /// brightens under the pointer.
    let private chatChipEnding (rightEdge: string) =
        cls [ "w-full bg-transparent cursor-pointer disabled:cursor-default text-left"
              readingColumn
              "flex items-baseline gap-2 pl-rail py-0.5 phone:py-2.5"
              rightEdge
              "text-ink-dim enabled:hover:text-ink transition-colors duration-150 ease-out"
              focusRing ]
    /// The same right edge a tool run's line ends on (`itemGround`: `pr-8` less the phone's
    /// 16px bleed), so a chip's status and a call's stand in one column.
    let chatChip = chatChipEnding "pr-8 max-md:pr-4"
    /// A chip inside a task card, whose ground has already drawn that right edge: padding it
    /// again would stop the chip's status 16px (phone) or 32px short of every other row's.
    let chatChipCarded = chatChipEnding ""
    /// Who ran it — the same caps voice a message's author line wears, one step fainter.
    let chatChipWho = caps + " text-ink-faint shrink-0"
    /// The `$` a queued command leads its line with, HUNG in the gutter left of the rail: a
    /// mark that leads a line never pushes the words in (docs/visual-design.md, Space). It sat
    /// on the rail, so a queued command began one glyph and one gap right of every other line
    /// in the column. The voice is the prompt's own (`terminalPrompt`, composed at the use
    /// site); this is only where it stands.
    ///
    /// The box is the WHOLE gutter and `-mr-2` pays back the row's own `gap-2`, so what follows
    /// the mark begins on the rail rather than a gap past it — the one thing a hung mark must
    /// not do is move the words it leads. `pr-3` then stands the glyph on the avatars' 20px
    /// column, where every other mark this margin carries stands (`fold`, `replyRefMark`).
    let chatChipPrompt = "-ml-rail -mr-2 w-rail pr-3 flex justify-center"
    /// Which terminal a queued command waits in, by its name: MONO, because a terminal an agent
    /// opens is named for what it runs (`dotnet fsi tasks.fsx build 2>&1 | tee build.log`), and
    /// mono is the terminal's face. It wore the caps voice, which is for short labels: a
    /// command in tracked capitals is the wall of text the doctrine warns of, and it read as
    /// chrome rather than as the name it is (docs/visual-design.md, Type).
    ///
    /// It takes the ROOM THE COMMAND LEAVES (`flex-1` on a zero basis), down to a floor that
    /// still shows a name's first word. It was held to half the row instead, which cut a
    /// 1440px chip's name at `… | t…` with half the row standing empty beside a short
    /// command: the cut fell on exactly the part that tells two such terminals apart. And it
    /// TRUNCATES, because a name that will not shrink is wider than a phone's column on its
    /// own and takes the whole conversation sideways with it.
    let chatChipSubject = "font-terminal text-code-sm text-ink-faint truncate flex-1 min-w-24"
    /// The `in` that joins a queued command to the terminal it waits in, in the act's prose
    /// voice: two mono runs side by side read as one command line.
    let chatChipIn = "font-ui font-light text-small text-ink-faint shrink-0"
    /// The command itself: mono, truncated to one line. A chip that wrapped to three would
    /// stop being a chip.
    ///
    /// On its CONTENT'S basis and never growing: the command is the content of the line, so it
    /// keeps every character it has room for, and the terminal's name beside it gives way
    /// first (`chatChipSubject`). It was `flex-1`, a zero basis, which has nothing to give up:
    /// the command was cut from its first character while the name kept all of its own.
    let chatChipCommand = "font-terminal text-code-sm text-ink-dim truncate min-w-0 flex-initial"
    /// A ran command's line: "ran" and what ran, truncated together as one sentence.
    let chatChipLine = "truncate min-w-0 flex-1"
    /// What ran, when its author described it: prose a step brighter than its verb
    /// (`deedVerb`), as an act's named thing (`entityName`) is.
    let chatChipName = cls [ "text-small"; entityName ]
    /// A stretch item's sentence — prose, not mono: nothing was typed that we recorded.
    let chatChipText = "font-light text-small text-ink-dim truncate min-w-0 flex-1"

    /// Where a turn stopped, and why: a SIGNPOST, not a message. It is one more line in the
    /// record of what the turn did, and the last. Drawn as prose it was read as the agent's
    /// closing sentence; the mark is what says "this is the machine's account".
    ///
    /// A ROW with a gutter (`foldRow`), because the stop mark LEADS this line: it hangs in the
    /// gutter and the reason starts on the column's rail, with every other line. It used to pad
    /// to the rail and then flex, so the mark stood on the rail and pushed the reason off it.
    let turnStop = cls [ "w-full"; readingColumn; foldRow; "items-baseline py-0.5" ]
    /// The mark: a stop, in the gutter, on the column the avatars and the fold arrows stand on.
    /// Colour is what carries the state in this set — the error colour a failed chip's status
    /// wears for a turn the process could not carry on, the faint ink for one a person stopped
    /// — and the screen-reader word beside it carries "stopped" for anything that cannot see
    /// colour.
    let private turnStopMark = "col-start-1 justify-self-start w-5 flex justify-center"
    let turnStopMarkFailed = turnStopMark + " text-err"
    let turnStopMarkInterrupted = turnStopMark + " text-ink-faint"
    /// The reason, in the quiet small voice — and WRAPPING, unlike a chip's one line: a
    /// reason is the one thing here a reader must not lose the end of.
    let turnStopText = cls [ foldContent; "font-light text-small text-ink-dim" ]

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
    /// nobody presses. Interaction is said as `titleInput` says it — the surface lifting under
    /// the pointer and while focused, so the line becomes a box you are typing in exactly when
    /// you are typing in it. What it says AT REST is its own, and the last paragraph says why.
    ///
    /// `truncate` rather than wrap: a rule is one line, and a name long enough to wrap has
    /// stopped being a name. `touchType` because a keyboard is coming, and a phone zooms into
    /// anything under 16px it focuses and never zooms back out.
    ///
    /// The placeholder of a chapter nobody has named is drawn AT REST, in `ink-faint`. It used
    /// to be `text-transparent` until the pointer or the keyboard arrived, which kept the
    /// divider a clean line — and kept the field a secret from everyone who never hovered it.
    /// A thumb has no hover (principle 4), so an invitation nobody can see is not an
    /// invitation; the Deciding order puts honesty above less, and this is that trade made the
    /// way it says. Ink and not opacity, so the contrast floor holds on the lifted surface it
    /// appears over.
    ///
    /// NOT `Stroke.underline`, which is what the session title wears for the same requirement
    /// (`titleInput`). Two reasons, and either is enough: that shape keeps one wearer or it
    /// stops being a shape (principle 5), and this control sits under the chapter's own
    /// hairline (`chapterRule`'s `border-t`), so a rule of its own would be a second hairline
    /// a few pixels below the first. The word is the affordance here.
    let chapterName =
        // `-ml-1.5` takes the padding back out on the left, so the WORDS start on the content
        // column's edge and only the lifted surface reaches into the gutter.
        cls [ "col-start-2 -ml-1.5 min-w-0 bg-transparent border-0 px-1.5 py-0.5"
              "hover:bg-surface-2 focus:bg-surface-2 transition-colors"; focusRing
              "font-ui font-light text-small text-ink-dim focus:text-ink truncate"
              "placeholder:text-ink-faint"
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
    ///
    /// Rest is `ink-faint`, a role, and nothing on top of it. It used to be that AND
    /// `opacity-60`, lifted by hover — and a phone never hovers, so on the one device where
    /// this is the only way to the menu it sat at a dimming the doctrine does not allow
    /// (dimming is a role, never opacity).
    ///
    /// On a phone its HIT AREA is `spacing-touch`, and only its hit area: a bare verb takes
    /// the height of what it rides and grows the target instead (docs/visual-design.md,
    /// Space). `hitTouch` is that growth — padding out to 44px, paid straight back as margin,
    /// so the glyph stays on the first line it is centred on, and nothing in the flow moves.
    /// 24px is WCAG 2.5.8's minimum exactly, and on a phone this is the only way to a
    /// message's actions, so the minimum is not where it should rest.
    let private itemActionsBase =
        cls [ "absolute w-6 h-6 flex items-center justify-center"; hitTouch
              "bg-transparent cursor-pointer text-ink-faint hover:text-ink transition-colors duration-150 ease-out"
              focusRing ]
    let itemActions = cls [ itemActionsBase; "right-1 top-2" ]
    /// The same control on an act whose headline is NOT its first line, because its cause
    /// sits above it (`causeRow`). `top-2` would put the dots on the cause — which reads as
    /// the menu of "Resumed session" — or, under a chain's link, beside a row holding nothing
    /// but an arrow. So it is placed in the headline's grid ROW instead: an absolutely
    /// positioned grid child with a row named resolves against that row's area, so the dots
    /// stand on the headline whatever height the cause above it draws. `-right-7` is
    /// `right-1` read from the content column's edge rather than the padding's (`itemGround`'s
    /// `pr-8`, less 4px).
    let itemActionsOnHeadline = cls [ itemActionsBase; "row-start-2 col-start-2 top-0 -right-7" ]
    /// While its menu is open the control is at full strength: a menu hanging off something
    /// faint reads as a menu hanging off nothing.
    let itemActionsOpen = "text-ink"

    /// The menu itself, hung under the control. `panel` rather than the page's ground,
    /// because it is a surface OVER the conversation and has to be told from it.
    let private itemMenuBase =
        cls [ "absolute z-30 min-w-[12rem] py-1"
              // Chrome, not content: a menu is a list of things to press, and dragging across
              // one should never start selecting its labels.
              "select-none"
              "bg-panel"
              Stroke.ring
              Stroke.hair ]
    let itemMenu = cls [ itemMenuBase; "right-0 top-7" ]
    /// Hung under `itemActionsOnHeadline`, from the same row: `top-5` is `top-7` less the
    /// control's own `top-2`, and `-right-8` the padding edge read from the content column's.
    let itemMenuOnHeadline = cls [ itemMenuBase; "row-start-2 col-start-2 top-5 -right-8" ]
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
    /// Where the beat is refused, the mark steps back a ROLE rather than fading: dimming is
    /// `ink-dim` → `ink-faint`, never an opacity, which breaks the contrast floor and is what
    /// this used to spend (`motion-reduce:opacity-60`) — docs/visual-design.md, Colour.
    let actNoteRunningOther =
        "inline-block mark-round rounded-full bg-ink-dim "
        + "animate-beat motion-reduce:animate-none motion-reduce:bg-ink-faint"
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

    /// History this device does not hold, standing where it would have been. An act note's
    /// voice and column, because it is the same kind of line — a thing that happened to this
    /// conversation rather than a thing anyone said. Its words name their column: a row is a
    /// grid, and words that did not were set in the gutter, a word to a line.
    let historyGap = actNote
    let historyGapText = cls [ foldContent; actNoteText ]

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
    /// A fenced block. Outlined, not filled: the composer goes `bg-surface-2` while focused,
    /// the top step of the ramp, so a filled block had no lighter step to stand on there and
    /// vanished. The timeline wears the same outline so the two draw one code block.
    let private proseFence =
        Stroke.ring + " " + Stroke.hair + " font-terminal text-code leading-5 text-ink p-3 [&:not(:first-child)]:mt-2"
    /// The block being written WRAPS: it is in the composer's editor, which needs
    /// `white-space: pre-wrap` (`[data-rich-body]` in app/tailwind.css), and a line being typed
    /// stays in view.
    let prosePre = proseFence + " whitespace-pre-wrap"
    /// The block being read keeps its lines and SCROLLS sideways, as a table does
    /// (`proseTableWrap`). A fence's line break is its meaning: a shell line wrapped at the
    /// reading column's 38rem — the measure prose is held to, about 68 characters of the body
    /// face (`readingColumn`) — read as four commands where there was one. So the column stays
    /// at the measure, which is the reading column's to set, and the one thing in it whose
    /// shape cannot reflow goes the way the doctrine already has for that: it holds more than
    /// fits and says so with a control, the scrollbar (docs/visual-design.md, Colour: a
    /// surface that holds more than fits says so with a control or a rule). The bar is
    /// `timeline`'s: thin, `edge`, on a clear track.
    let proseFenceRead =
        proseFence + " whitespace-pre overflow-x-auto [scrollbar-width:thin] [scrollbar-color:var(--color-edge)_transparent]"
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

    // --- The composer's tray: interrupt and the way back to the latest message --------------
    // The agent's activity strip used to live here: a 48px band carrying a pulse, the words
    // "agent is responding", the turn's number and a bordered Interrupt. It said what the
    // streaming message's caret already said, and the control it carried was the only part
    // that was its own. That control then rode Send's row; the way back to the latest message
    // floated over the timeline, a square on the avatars' column.
    //
    // Both now stand in one strip on the composer's top edge, shown only while either has
    // something to do. Not floating: anything laid over the timeline lies over words the
    // moment a long line scrolls under it, at whatever spot it is put, and room reserved
    // under the newest line does nothing for a reader scrolled back. A strip that takes its
    // own height covers nothing in any scroll state.

    /// The strip. A step DOWN from the composer (`panel` under `surface`), above the composer's
    /// rule rather than under it: the rule is where writing starts, so it stays the composer's
    /// top edge, and these are controls for the timeline, not part of the draft. Edge to edge
    /// like the composer and the queue's bands: inset, it would read as an object floating
    /// over the conversation again. No rule of its own — the step from `bg` is its top edge.
    ///
    /// The leading edge is the timeline's (`px-8`, `px-4` on a phone), so interrupt's word
    /// stands on the reading rail every message body starts on and its glyph hangs on the
    /// avatars' column (`trayMark`) — the line a reader draws down the conversation runs on
    /// through it. The trailing edge is the composer's gutter (`px-4`), so the jump's arrow
    /// ends on Send's. 44px on a phone, a thumb's target; one control high elsewhere.
    ///
    /// It ARRIVES rather than appears: drawn first (`starting:`) a touch low and clear, it
    /// rises onto the composer's rule and fades in, on the pace every arrival here takes
    /// (`Motion.pace`) — out from behind the composer, which is later in the document and
    /// positioned, so the few pixels it starts below the rule are under the band, not over it.
    ///
    /// Its HEIGHT is not animated, and that is the point of doing it this way. The strip is
    /// laid out at its full height in one frame, so the conversation gives up its room once;
    /// growing it from nothing instead moved the conversation's box every frame for 200ms —
    /// the newest line re-pinned twelve times over, which the cold-open bench measured as six
    /// times the paints of a strip that simply arrived. Opacity and translation move nothing
    /// else on the page.
    ///
    /// Its HEIGHT asks the shell's own question (`phone:`), and spends the touch token rather
    /// than a number: a thumb's row is a thumb's row however the phone is held, and `max-md:`
    /// said no at 844x390, where the strip's two controls were 32px under a finger.
    /// Its GUTTER is `max-md:`, and stays: it is not a question of its own but an echo of
    /// `timeline`'s, so the word stands on the rail the messages above it start on. That
    /// column's inset IS a width question, which is the one thing `max-md:` is for
    /// (docs/visual-design.md, Phone and wide) — and it is not one inset but a family, every
    /// full-bleed `-mx-4` in the conversation measured against it. The two answers have to
    /// agree, so this one follows the column rather than asking the shell's question beside
    /// it. On a wide screen the strip stands in the dock (`dock`), whose edge is already the
    /// ground's edge a message stands on, so the gutter there is no padding at all: the mark
    /// takes the avatars' column at the edge and the word lands on the rail after it.
    let composerTray =
        cls [ "shrink-0 flex items-center h-8 phone:h-touch pl-8 pr-4 max-md:pl-4 wide:pl-0 wide:pr-rail bg-panel"
              "transition-[translate,opacity] starting:translate-y-1 starting:opacity-0"; Motion.pace ]

    /// The label voice (`caps`), not Send's step up: these are controls for the timeline
    /// above, and at Send's size they stood level with the verb the composer is for.
    let private trayVerb =
        cls [ "h-full min-w-11 shrink-0 inline-flex items-center p-0 bg-transparent border-0 cursor-pointer font-ui"
              caps; "text-ink-dim"
              // A control arriving in a strip already open fades in on the same pace
              // (`starting:`, the style it is first drawn from), so the second of the two
              // turning up does not pop where the first arrived.
              "transition-[color,opacity] starting:opacity-0"; Motion.pace; focusRing ]

    /// Ink at rest, err under the hand — the face every destructive verb here wears. Err AT
    /// rest is this product's tone for *something is wrong*, and a turn running is the normal
    /// case. `disabled:*` is the turn between a press and the stop landing
    /// (`AgentViewState.Interrupting`); `pointer-events-none` so the err face never paints
    /// while there is nothing left to press.
    ///
    /// No gap of its own: the glyph's cell (`trayMark`) plus `gap-3` is the 20px avatar and
    /// the 12px gutter `messageGroupHead` spends, so the word lands on the reading rail.
    let trayInterrupt =
        cls [ trayVerb; "gap-3 hover:text-err"
              "disabled:text-ink-faint disabled:cursor-default disabled:pointer-events-none" ]

    /// While no turn runs and a command does, the leading edge says so: `running` behind
    /// the spin mark (`Icon.spin`) every running command wears, pressed to go to that
    /// command's chip. The mark sits in interrupt's own cell (`trayMark`), so the two states
    /// hand off at the same width and it lands dead centre on the column the transcript's
    /// avatars and icons stand on, rather than crowding the word off that rail. `gap-3` is
    /// `trayInterrupt`'s own gutter, so the word lands on the same reading rail whichever
    /// state is showing. Ink at rest, ink under the hand — it moves the reader, it stops
    /// nothing.
    let trayRunning = cls [ trayVerb; "gap-3 hover:text-ink" ]

    /// The way back to the latest message, at the strip's trailing edge whatever stands at
    /// its leading one. Its word ends in the glyph, so no caps tracking trails past the ink.
    let trayJumpSlot = "ml-auto h-full flex"
    let trayJump = cls [ trayVerb; "gap-1.5 justify-end hover:text-ink" ]

    /// The leading edge's glyph cell, interrupt's and running's alike: a mark hanging in the
    /// gutter left of the rail, the avatar's width, centred, so it stands on the column the
    /// speakers' avatars and the fold arrows do whichever glyph it holds.
    let trayMark = "w-5 shrink-0 inline-flex justify-center"

    // --- Queue: editable until drained; the head's green count says so ------------------------
    // The queue is the composer's dock, not a list floating over the ground: its rows are
    // BANDS, running edge to edge exactly as the composer band below them does, told apart
    // from the ground by their tone. They used to be lead-edged cards inside a padded strip —
    // a third rectangle vocabulary two centimetres from the band that already had the answer.

    /// The dock itself: the queue, the tray and the composer, held as one block so one rule
    /// sets all three edges.
    ///
    /// On a phone it runs edge to edge, as every ground in the conversation above it does.
    /// On a wide screen it stands where a message's ground does: one rail in from the column
    /// on each side (`timeline`'s `2rem`) and no wider than the measure (`readingColumn`), so
    /// the composer is the transcript column's width on the transcript's rail, not a band
    /// wider than what it writes into. And it stands off the screen's bottom edge by the
    /// timeline's own foot (`pb-6`), so it reads as the column's last object, not as chrome
    /// fixed to the window.
    let dock = "shrink-0 flex flex-col wide:mx-rail wide:mb-6 wide:max-w-measure"

    let queue = "shrink-0 flex flex-col gap-0.5 pt-4"

    /// The same band holding nothing. Padding is what a band spends on the rows inside it, and
    /// with no rows the `pt-4` was 16px of ground between whatever sits above it and the
    /// composer — a gap that said something was there. Nothing is there, so it takes no room:
    /// the composer's rail sits directly under whatever is above it.
    let queueEmpty = "shrink-0"
    /// On the composer's own gutter (`draftInput` spends px-4), because the head belongs to
    /// the dock it heads, not to the timeline above it.
    let queueHead = "flex items-baseline gap-3 pb-2 px-4 wide:px-rail"
    /// Green, AT REST: "still editable" is a fact about the whole queue, said once at its
    /// head — the rows beneath no longer repeat it per entry.
    let queueCount = caps + " text-green"

    /// A full-bleed band on the composer's gutter. The surface tone is what marks it (the
    /// timeline's ground is `bg`, the dock's bands are `surface`), and interaction lifts the
    /// surface exactly as every other actionable row here does.
    ///
    /// A FLOOR height, not a fixed one. It was `h-10`, and a queued message is a message:
    /// at 390 the second one in the fixture ("check the phone layout too — the pane was
    /// clipped last time") wrapped, and its second line was painted under the next band,
    /// unreadable. A queue you cannot read is a queue you cannot edit, which is the one
    /// thing it is for. `py-2` holds a one-line row at exactly the 40px it was — the verbs'
    /// 24px box plus 16 — and every further line simply makes the band taller. On a phone
    /// most rows DO take a second line, and that is the clipping being undone rather than a
    /// new cost: the queued message is set at `touchType`'s 16px there, so it was already
    /// two lines tall inside a host held to one.
    let queueItem =
        cls [ "group flex items-center gap-3 min-h-10 py-2 px-4 wide:px-rail bg-surface transition-colors"; rowLift ]

    /// `focus-within`, not `focus`: the caret lands in the editor MOUNTED INSIDE this host,
    /// so `focus:` on the host never fires and the brightening never happened.
    ///
    /// `min-h-5`, never `h-5`: the fixed line was the other half of the clipping above — a
    /// 20px host cannot report a two-line draft's height to the band around it, so the band
    /// had nothing to grow by and the words left the box.
    let queueInput =
        cls [ "flex-1 min-w-0 self-center min-h-5"; fieldBare
              messageVoice false
              "text-small leading-5 text-ink-dim focus-within:text-ink"; touchType ]

    /// The row's verbs, AT REST. They used to be `opacity-0` until the pointer or the caret
    /// reached the row, with a `max-md:` case putting them back for a touch screen — and that
    /// case asked the wrong question: `max-md` is a width, so a phone turned sideways (844
    /// wide) answered no, and the only way to reorder or drop a queued message was not drawn
    /// at all on a device with no hover to reveal it. Worn, not hinted (docs/visual-design.md,
    /// principle 4), and the doctrine already names the rest a row's verb keeps: bare, in
    /// `ink-dim`, which is what `btnIconBare` rests at.
    let queueTools = "flex gap-1"

    /// Move-up and move-down. A verb riding a listed row, so bare, and a thumb's target on a
    /// phone without the band moving to make room for it.
    let queueVerb = btnIconBareTouch

    /// Extra clearance before Delete, on top of the gap the row already spends between
    /// Move-up and Move-down. Reorder and delete used to sit in one evenly-spaced row of
    /// three — a destructive control 4px from a frequent one, with nothing between them but
    /// the icon each wears. This is not the hit area (that is `hitTouch`, which every verb
    /// here now wears) — it grows the DISTANCE a thumb has to travel wrong to reach the one
    /// that destroys.
    let private queueDeleteGap = "ml-2"
    /// Delete, and delete ARMED — a second press from dropping the message, which it says by
    /// wearing the err fill at rest, because a touch has no hover to hold a warning up.
    let queueVerbDelete = cls [ btnIconBareDanger; hitTouch; queueDeleteGap ]
    let queueVerbDeleteArmed = cls [ btnIconBareDangerArmed; hitTouch; queueDeleteGap ]

    // --- Agent surfaces: what the agent draws for people to answer (A2UI) ----------------------
    // The agent says WHAT is on a surface, in the basic catalog's words; this product says how it
    // looks, from the vocabulary above. So nothing here takes a colour, a size or a font from the
    // surface: a Primary button is this product's fill, a heading is a step on this ramp.

    /// One surface: a band in the dock, on the composer's gutter, as a queued message is —
    /// because it is a thing waiting on a person, in the place a person answers from.
    ///
    /// Capped at half the visible height, and it scrolls inside the cap. The agent decides how
    /// long a surface is, so without a cap a long question on a phone on its side (844x390) took
    /// the whole column and pushed the conversation and the composer off screen, with its own
    /// buttons cut off at the bottom. The dock is only as tall as its content, so a percentage
    /// would resolve against nothing. The cap is a share of the shell's own height
    /// (`--visual-h`, see `shell`), which already accounts for the iOS keyboard.
    let agentSurface =
        cls [ "shrink-0 flex flex-col gap-3 py-4 px-4 wide:px-rail bg-surface"
              "max-h-[calc(var(--visual-h,100dvh)/2)] overflow-y-auto overscroll-contain" ]
    let surfaceColumn = "flex flex-col gap-3 min-w-0"
    let surfaceRow = "flex flex-wrap items-center gap-3 min-w-0"
    /// A card inside a surface: one step up the greys, which is what nesting is said with.
    let surfaceCard = "flex flex-col gap-3 p-4 bg-surface-2 min-w-0"
    let surfaceDivider = cls [ Stroke.dividerTop; "w-full" ]
    let surfaceDividerVertical = cls [ Stroke.dividerLeft; "self-stretch" ]

    /// A container's `justify` along its own axis, and its `align` across it, as the spec names
    /// them. Literal class names, because Tailwind generates only what a file writes out.
    let surfaceJustify (justify: A2ui.Justify) : string =
        match justify with
        | A2ui.Justify.Start -> "justify-start"
        | A2ui.Justify.Center -> "justify-center"
        | A2ui.Justify.End -> "justify-end"
        | A2ui.Justify.SpaceBetween -> "justify-between"
        | A2ui.Justify.SpaceAround -> "justify-around"
        | A2ui.Justify.SpaceEvenly -> "justify-evenly"
        | A2ui.Justify.Stretch -> "justify-stretch"

    let surfaceAlign (align: A2ui.Align) : string =
        match align with
        | A2ui.Align.Start -> "items-start"
        | A2ui.Align.Center -> "items-center"
        | A2ui.Align.End -> "items-end"
        | A2ui.Align.Stretch -> "items-stretch"

    /// The agent's words on a surface, in the agent's voice (sans): a heading at the heading
    /// step, a subheading at the body step in weight, prose at the body step, a caption small.
    let surfaceHeading = "font-ui font-extralight text-heading text-ink min-w-0"
    let surfaceSubheading = "font-ui font-semibold text-body text-ink min-w-0"
    let surfaceText = cls [ "font-ui"; body; "min-w-0" ]
    let surfaceCaption = cls [ small; "min-w-0" ]

    /// Why a control is not ready, or why an input is not valid: a REASON, so body text in the
    /// body voice, no dot and no caps (docs/visual-design.md, Colour).
    let surfaceReason = cls [ small; "min-w-0" ]

    /// A field's own label above it, in the caps voice labels speak in.
    let surfaceFieldLabel = cls [ label; "flex flex-col gap-1.5 min-w-0" ]
    let surfaceField = field
    /// The long-text field: the same face, given the height several lines need rather than
    /// the one-line box's.
    let surfaceFieldLong =
        cls [ "bg-surface outline-none appearance-none min-h-20 px-3 py-2 transition-colors w-full resize-y"
              Stroke.ring; Stroke.hair; Stroke.hoverRim; Stroke.focus; fieldType ]

    /// A choice: a whole-row press, as the launch card's repositories are, with the tick in a
    /// cell drawn on every row and filled on the chosen ones, so choosing moves no word.
    let surfaceChoices = "flex flex-col divide-y divide-hair min-w-0"
    let private surfaceChoiceBase =
        cls [ "w-full text-left flex items-center gap-3 min-h-10 phone:min-h-touch py-2 border-0 cursor-pointer min-w-0 transition-colors"
              focusRing ]
    let surfaceChoice = cls [ surfaceChoiceBase; "bg-transparent"; rowLift ]
    let surfaceChoiceChosen = cls [ surfaceChoiceBase; "bg-surface-2" ]
    let surfaceChoiceMark = "w-5 shrink-0 self-center text-blue"
    let surfaceChoiceLabel = cls [ "font-ui"; body; "min-w-0" ]

    /// A component this renderer does not draw yet, said rather than silently dropped.
    let surfaceUnsupported = surfaceCaption

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

    /// The terminal command band's own edge, spent on a different gesture than the message
    /// composer's: focus alone lands a caret in the field with nothing written, which a phone
    /// does on every tap into it, so a focus-within trigger lit the gradient on a glance into
    /// an empty line. The terminal has a plainer fact to read instead — whether there is a
    /// command in the line, the same one Run and the lease bar already read (`hasCommand` at
    /// the call site) — so the edge answers to actually typing, not merely landing a caret.
    /// One rule still, not two: `active` is read from the model that already decides Run's
    /// own weight, rather than this element measuring the field a second time.
    let terminalBandEdge (active: bool) : string =
        cls [ "absolute inset-x-0 top-0 h-0.5 bg-grad origin-left transition-transform duration-300 ease-out"
              "motion-reduce:transition-none"
              if active then "scale-x-100" else "scale-x-0" ]

    /// A COLUMN, on every screen: the line you write, then the verbs under it
    /// (`draftActionsRow`). It was a row on a wide screen — the picker, the line and Send
    /// abreast — and a column only on a phone. One arrangement now, the phone's, because the
    /// two said the same thing two ways (docs/visual-design.md, principle 5), and the row left
    /// the line whatever width the picker and the attach did not take: the words started a
    /// third of the way across the box, off the rail every message above them starts on.
    ///
    /// Carries no fill and no edge of its own: the band it sits in is the surface.
    let draftBox = "relative flex flex-col items-stretch"

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
    /// On a phone the line has `pt-3`, the box is 44px at rest, and the cap and the ramp move
    /// down 4px with it (2.75rem → 3.5rem).
    ///
    /// The mask's stops are absolute FROM THE TOP (2.5rem → 3.25rem) rather than a percentage
    /// or an offset from the bottom, and that is the whole reason it is safe. A ramp measured
    /// from the bottom edge is a ramp whose position moves with the box: at 40px it would run
    /// up through the first line and fade the descenders of a draft that fits perfectly well.
    /// Anchored at the top it begins exactly where line one's box ends, so line one is never
    /// touched and the ramp is simply off the end of a box that has not grown.
    let draftInput =
        cls [ "block w-full max-h-[3.25rem] phone:max-h-[3.5rem] overflow-hidden transition-[max-height] duration-200 ease-out"
              "[mask-image:linear-gradient(#000_2.5rem,transparent_3.25rem)]"
              "phone:[mask-image:linear-gradient(#000_2.75rem,transparent_3.5rem)]"
              "group-focus-within:max-h-64 group-focus-within:overflow-y-auto"
              // Gone on focus, not merely pushed down: the ramp would otherwise sit over the
              // line being typed the moment a draft scrolls inside.
              "group-focus-within:[mask-image:none] motion-reduce:transition-none"
              // The line unfolds in when the editor mounts, rather than the band growing a
              // line in one frame (`line-unfold` in `tailwind.css`).
              "motion-safe:[&>.ProseMirror]:animate-line-unfold"
              fieldBare
              messageVoice false
              // No `placeholder:` variant: this field is a mounted editor, not an `<input>`,
              // so it has no placeholder attribute for that variant to have ever matched. The
              // prompt is drawn on the editor itself — `[data-rich-body]` in `tailwind.css`.
              // `phone:pt-3` over `pb-2`: on a phone the line sat 8px under the band's top rule
              // and 16px in from its side, cramped against the one edge that carries the focus
              // gradient, and 44px is the thumb's height. The extra 4px is spent above, so the
              // line still ends where Send's box does. A desktop keeps `py-2`, the 40px the
              // terminal's command band beside it agrees with (`terminalCommand`).
              "text-body text-ink px-4 wide:px-rail py-2 phone:pt-3"; touchType ]

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
    /// No `pb`, derived, not eyeballed: the rest-state input is a 24px line over `pb-2`, so
    /// the line's centre sits 20px above the box bottom; a 40px control bottom-aligned on the
    /// same box centres 20px above it too (the 4px of `pt-3` beyond that is all above). It used to spend `pb-1` because the controls
    /// were 32px squares and needed 4px under them to reach that centre — the correction went
    /// out with the glyphs (`btnComposerBox`, above, is the line's own height).
    ///
    /// This row leaves the line entirely, but does not own a full line of its own: it shares one with the model picker (`draftActionsRow`, below) rather than
    /// stacking under it as a second line — `phone:w-full phone:justify-end` used to push its
    /// buttons to the far edge of a line THIS row had entirely to itself; now the picker is
    /// the other half of that line, so sizing to its own content and letting the row's
    /// `justify-between` do the push is what keeps it at the trailing edge without claiming
    /// width the picker needs. It sits BELOW the text (`draftBox`'s column puts it there in
    /// document order).
    ///
    /// And it STANDS. It used to come and go with the draft — clear and deaf to presses while
    /// there was nothing to send — so on a phone at rest the composer offered a model, an
    /// attach, and no verb at all: the one control the surface is FOR was off the screen
    /// until you had already written the message. That is not a control waiting, it is a
    /// control leaving, and a state has to be visible at rest (docs/visual-design.md,
    /// principle 4). Send waits where it stands instead, at the weight of a thing with
    /// nothing to do (`btnComposerSendWaiting`), which is the face the doctrine names for
    /// exactly this and which Send already wore on every screen wide enough to keep it.
    /// Nothing is gated here any more, so there is one value rather than a pair.
    ///
    /// The fade it waited behind took the row out of the picture and left it in the TAB
    /// order — `opacity-0` hides nothing from a keyboard — so a keyboard reached a control
    /// nobody could see. The doctrine's waiting face has no such gap: it stays in the layout
    /// and in focus order because it is still there to be pressed.
    ///
    /// Before the fade it was `group-focus-within`, and that could not work either.
    /// `focus-within` is false the instant focus leaves the composer, and pressing a button is
    /// how focus leaves: on iOS Safari a `<button>` takes no focus from a tap at all, so the
    /// editor's blur lands FIRST, the row goes `pointer-events-none` under the finger, and the
    /// press arrives at whatever was behind it — the timeline. Measured: with a draft typed
    /// and the editor blurred, `elementFromPoint` at Send's own centre answered the
    /// `<article>` behind it. Send did nothing, the composer collapsed, and that was the whole
    /// of what a person saw.
    ///
    /// Interrupt rode this row for a while, which gave it a second reason to stand (a turn
    /// running). It stands in the composer's tray now (`composerTray`), so the draft is
    /// Send's own business and the row has no reason to move at all.
    ///
    /// So this is a box and nothing else: no gate, no fade, no height to animate, and no
    /// second value for the state it used to have. Whether there is anything to send is said
    /// by the WORD inside it, which is the only place it was ever legible — the row carried
    /// `max-md:opacity-0 max-md:pointer-events-none` (and before that `max-h-0`) and three
    /// `max-md:` transition rules to settle how that fade moved the line it shares. All of
    /// it was arranging the disappearance of a control the doctrine says should stay.
    ///
    /// `phone:items-baseline`: on a phone this box is one half of a row aligned on its text
    /// baseline (`draftActionsRow`), and a flex container lends its parent the baseline of
    /// its first baseline-aligned item. With nothing aligned on one, that was the presence
    /// span beside Send — usually empty, and an empty box's synthesized baseline is its
    /// bottom edge, so Send's word sat five pixels under the picker's. Aligning this box's
    /// own items puts Send's word in the group, which is the baseline the row then reads.
    let draftCommit = "shrink-0 flex items-baseline gap-1 pr-1 wide:pr-5"

    /// The model picker's own row-mate, standing beside `draftCommit` rather than inside it:
    /// choosing a model has nothing to do with whether there is a draft to send or a turn to
    /// stop. It was inside the commit row for one
    /// revision (riding a gated `commitClass` in `View.drafts`) and that was the bug, twice
    /// over: on a phone with an empty draft the picker was invisible along with Send, so
    /// nobody could choose a model before writing a word; and because the row it rode
    /// transitioned, a tap landing while that transition was still settling opened the
    /// platform's OWN popup anchored to a rect that kept moving under it — a native menu a
    /// phone drew in the wrong place for a reason no amount of styling the menu itself could
    /// reach, because the bug was never the popup's. Both halves are moot now that nothing
    /// about this row comes or goes, and the separation is kept because the two controls are
    /// still two different claims on the line.
    /// `shrink-0` matches `draftCommit`'s own, so the two sit side by side without either
    /// claiming space the other needs.
    ///
    /// `pl-4` matches `draftInput`'s own left gutter: `draftBox`'s `items-stretch` stretches this row to the full width same
    /// as `draftInput` above it, and nothing else gives it a left inset: unlike `draftCommit`,
    /// whose gutter comes free from its buttons' own `px-3`, this row is the model control
    /// alone, so the row itself has to carry it. Without it the picker sits flush against the
    /// viewport edge, outside the gutter every other line in the composer keeps. A wide screen
    /// puts every line in the composer on the column's rail (`wide:pl-rail`), as `draftInput`.
    let draftLead = "shrink-0 flex items-center gap-4 pl-4 wide:pl-rail"
    /// The composer's way to attach a file, beside the model picker on `draftLead`: the
    /// picker's own voice (`modelSelectFace`) and height (`modelSelectWrap`), so the row reads
    /// as one set of controls rather than a label beside a word of the message. It is the
    /// `<label>` of the file input it holds, so it wears that input's ring (`focusRingHeld`).
    let draftAttach =
        cls [ "shrink-0 h-10 inline-flex items-center bg-transparent border-0 cursor-pointer p-0"
              caps; "text-ink-dim hover:text-ink transition-colors"; focusRingHeld ]

    /// The files on the open draft, under its words: one line of them that wraps, in the
    /// input's own gutter.
    let draftFiles = "flex flex-wrap gap-x-4 gap-y-1 px-4 wide:px-rail pb-2"
    /// One file: its name, then what it weighs or where it has got to, then its verbs.
    let draftFile = "min-w-0 inline-flex items-center gap-2"
    let draftFileName = "min-w-0 max-w-[16rem] truncate font-ui font-light text-small text-ink"
    let draftFileSize = "shrink-0 font-ui font-light text-small text-ink-faint tabular-nums"

    /// The picker (`draftLead`) and Send's row (`draftCommit`) ride together as ONE line under
    /// the text, on every screen: the picker at the leading edge, Send pushed to the trailing
    /// one (`justify-between`), a sentence's width between the two controls that most need
    /// telling apart. One wrapper, so `draftBox`'s column takes the pair as one row rather
    /// than stacking "Provider's default" over SEND.
    ///
    /// `items-baseline`: the picker speaks the 11px caps label and Send the 13px one, so
    /// centring the two boxes put their words on two lines, a pixel and a half apart. The
    /// doctrine aligns a row on its text baseline (Space), and so does this one.
    let draftActionsRow = "flex items-baseline w-full justify-between"

    let draftAuthor = "pl-4 wide:pl-rail pt-2 " + caps + " text-ink-faint truncate"

    // A draft nobody has open here: one line of it, so the composer reads as "what is being
    // written" rather than a stack of boxes. Clicking it opens it (and closes whatever was).
    //
    // Its leading edge is the AUTHOR'S colour (set inline, from `Entity.presenceColour`) — the same
    // move the terminal's peer-draft row makes, and the reason is the same: the row's whole
    // subject is whose words these are, so the edge should say it rather than repeat a
    // generic hover tint.
    let draftSummary =
        cls [ "group w-full items-center gap-3 h-8 pl-4 wide:pl-rail pr-2 text-left cursor-pointer"
              rowBase; rowLift; focusRing ]

    let draftSummaryName = "shrink-0 " + caps + " text-ink-faint"

    /// The clamped body: the same read-only editor as anywhere else, held to one line, and
    /// ending in an ELLIPSIS rather than at whatever letter the edge fell on. The truncation
    /// used to be asked of the descendants (`[&_*]:truncate`) to keep it off a host whose
    /// children are ProseMirror's blocks — but `text-overflow` is a property of the box that
    /// overflows, and those children are made inline on the line above, so no box was ever
    /// asked to draw the mark. A word cut mid-letter reads as a rendering fault; a word cut
    /// with a mark reads as "there is more of this", which is what a summary is saying.
    /// `[&_*]:inline` is what makes the host's own content inline, so the clamp belongs here.
    /// The descendants still need `whitespace-nowrap` of their own, and that is the half the
    /// old `[&_*]:truncate` was really doing: ProseMirror sets `white-space: pre-wrap` on its
    /// own root, so without it the words wrap and spill out of the row rather than running
    /// off its end.
    let draftSummaryBody =
        "flex-1 min-w-0 " + messageVoice false + " text-small leading-8 text-ink-dim "
        + "truncate [&_*]:inline [&_*]:whitespace-nowrap [&_*]:m-0"

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

    /// The settings face's header band: the same band rhythm as the nav and the main header, so
    /// the three baselines still align when the column changes face — the nav's head, hairline
    /// and all, so the band's rule does not come and go as the faces turn.
    let settingsHead = sideHead
    let settingsTitle = "font-extralight text-heading tracking-[-0.01em] lowercase text-ink"

    /// Two acts of a flow side by side in a lane — approve and cancel, complete and cancel.
    ///
    /// It WRAPS, and that is the whole of this value. A button's label cannot wrap inside its
    /// box (`btnFace` is one fixed height, so it is `whitespace-nowrap`), which leaves the ROW
    /// as the only thing that can give: at 390 the sidebar is a 280px drawer, and APPROVE ON
    /// GITHUB.COM in the caps voice is wider than what a Cancel beside it leaves — so Cancel
    /// was clipped to "CAN" at the column's edge. Wrapped, the second verb takes the next
    /// line, which costs 40px on one screen of one flow and costs nothing anywhere else.
    /// The two rows that wear it spelled `flex gap-2` inline, which is also why only one of
    /// them was ever looked at.
    let settingsActions = "flex flex-wrap gap-2"

    // --- The agent's absence -------------------------------------------------------------------
    // Said ONCE, in the section that lists who is in this session, because that is where a
    // missing member is missing. It used to be said three times over (a sidebar row, a strip
    // above the composer, and the settings copy); repetition made it wallpaper, not a prompt.
    //
    // The absent state is the SAME roster row as the live one — same avatar cell, same
    // right-aligned slot — so connecting an agent flips the bare `connect` verb to its standing in
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
    let noAgentEdge = "w-0.5 justify-self-center bg-agent"
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
        + "phone:fixed phone:inset-y-0 phone:right-0 phone:left-5 phone:w-auto "
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
    /// overflow — which also rules out an outline for focus, so the focus signal is the fill.
    ///
    /// AT REST it paints nothing, and that is not a hidden control: what the pointer aims at
    /// is the column's own divider (`contentPanel`'s `Stroke.dividerLeft`), drawn under this
    /// strip at the same place and always there. The strip is that hairline's hit area, so
    /// principle 4's screenshot shows a separator where a separator is.
    ///
    /// ONE interaction tone, `blue`, for the hand and for the keyboard alike. It was
    /// `blue/50` under the hand and `blue` focused — a half-strength hue is dimming by
    /// opacity, which the doctrine forbids (Colour) and which no role here replaces: there is
    /// no half-blue in `app/tokens.css` and a separator does not need two interaction tones
    /// to tell a reader it can be dragged.
    let terminalResize =
        cls [ "phone:hidden absolute left-0 inset-y-0 w-1.5 z-50 cursor-col-resize"
              "bg-transparent hover:bg-blue focus-visible:bg-blue focus-visible:outline-none"
              "transition-colors motion-reduce:transition-none" ]

    /// The pane's left edge on a phone: a strip of the chat left showing, under a scrim, and
    /// pressing it is the way back. The sheet slid in from the right and stops short of the
    /// screen's edge, so what it covers is still there beside it — the same dimmed ground the
    /// nav drawer leaves behind it (`scrim`), and the same press dismisses both. No mark: a
    /// glyph in 6px of edge was a `›` hanging in the middle of the screen that said nothing a
    /// reader could act on, and the chat behind the scrim says where the press goes.
    ///
    /// Outside the panel, because the panel clips its overflow and the strip is not the pane's.
    /// Fixed, so it lies over the chat column at the screen's edge, as wide as the gap the
    /// panel leaves (`contentPanel`'s `phone:left-5`), and it fades with the panel's slide.
    ///
    /// Out of the accessibility tree and the Tab order (`aria-hidden`, `tabindex=-1`): it is a
    /// duplicate of the head's `›`, and a reader of the tree should meet the way back once.
    /// WCAG 2.5.8 excepts a target whose act an equivalent control on the same screen offers
    /// at full size, and the head's is 44.
    ///
    /// 20px: wide enough to find with a thumb and to read as the chat rather than a rule, and
    /// no wider, because every pixel of it comes out of the tab row (`panePivotRow`).
    ///
    /// The splitter's place on a desktop (`terminalResize`); the two never share a screen.
    let paneGrabEdge =
        cls [ "hidden phone:block fixed left-0 inset-y-0 w-5 z-40 p-0 border-0 bg-black/60 cursor-pointer"
              "transition-opacity duration-200 ease-out motion-reduce:transition-none"
              "phone:[.term-closed_&]:opacity-0 phone:[.term-closed_&]:pointer-events-none"; focusRing ]

    /// The column's head claims the band itself (`spacing-band`, `spacing-band-phone` on a
    /// phone) rather than owing it to the ARITHMETIC of two `spacing-touch` rows. That used to
    /// be 44+44=88, which matched the chat header's band only while a toolbar row
    /// (`paneToolbar`) was there to carry the second 44 — an empty pane has no toolbar
    /// (`paneUnderStrip`'s `None, None`), so the strip alone sat at 44 against the header's 88,
    /// and on a PHONE the sum was 88 against the header's 56 in every state. Both are the
    /// mismatch `docs/visual-design.md`'s "a head with two rows is taller... its hairline lines
    /// up with nothing" names and accepts for a two-row head in general; this one head instead
    /// pins its FIRST row to the token, so it needs no second row to land on the band.
    ///
    /// `items-stretch`, not the chat header's own `items-end` + `pb-5` (`phone:items-center` +
    /// `phone:pb-[15px]`) copied at first, because that alignment is the header's TEXT settling
    /// short of the hairline with room a title doesn't need to fill. A tab is not text: a
    /// `h-touch` chip centred or end-anchored in the now-taller row left its own fill — the
    /// thing that is supposed to say which tab the page belongs to (`pivotItemOn`'s `bg-panel`)
    /// — stopping short of the row's rule, a visible band of the strip's bare `bg` between the
    /// chip and the hairline it should run into. `items-stretch`, and no explicit height on
    /// `pivotItemBase` (`min-w-touch` still bounds its width, never its height), lets every
    /// entry fill the row edge to edge instead, so the selected tab's own ground runs all the
    /// way to the hairline with nothing of the strip's showing between them. The controls
    /// outside the scroller (`paneListButtonBase`, `paneStripAct`) keep their own `h-touch`, so
    /// a bare `items-stretch` would snap them to the row's top instead of leaving them centred
    /// as before — they carry `self-center` for that.
    ///
    /// `paneToolbar`, where there is one, is now the column's own second row underneath —
    /// `spacing-touch` tall and no longer asked to land on the band. Its hairline is its own,
    /// the ordinary case the "two rows" clause describes.
    ///
    /// Its OWN hairline (`Stroke.dividerBottom`) is the one visual-design.md already promises
    /// ("the hairline under it runs across every column at one height") and the one the chat
    /// header wears for the same band — this row had the height but drew no line, so the
    /// header's rule stopped dead at the pane's divider instead of running on into it.
    let panePivotRow =
        cls [ "shrink-0 h-band flex items-stretch bg-bg phone:h-band-phone"; Stroke.dividerBottom ]
    /// The tablist: `all`, then the scroller of tabs. `all` stays put at the row's head however
    /// many tabs follow it. `min-w-0` so the scroller is what gives way when the names outgrow
    /// the row.
    let panePivotList = "flex-1 min-w-0 flex items-stretch"
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
    /// `divide-x divide-hair` between the tabs themselves: the same hairline the row's own
    /// bottom rule draws (`panePivotRow`), run upright, between every pair — so a reader who
    /// cannot tell the selected tab's one-step fill from the strip's ground at a glance still
    /// sees where one tab ends and the next begins. `divide-x` skips the first child, so there
    /// is no stray edge against `all` on one side or the strip's fade mask on the other.
    let panePivotScroller =
        cls [ "min-w-0 flex items-stretch divide-x divide-hair overflow-x-auto overflow-y-hidden [scrollbar-width:none] [&::-webkit-scrollbar]:hidden"
              "data-[pane-strip-hidden=end]:[mask-image:linear-gradient(to_right,#000_calc(100%_-_1.5rem),transparent)]"
              "data-[pane-strip-hidden=start]:[mask-image:linear-gradient(to_left,#000_calc(100%_-_1.5rem),transparent)]"
              "data-[pane-strip-hidden=both]:[mask-image:linear-gradient(to_right,transparent,#000_1.5rem,#000_calc(100%_-_1.5rem),transparent)]" ]
    /// One tab: a name, as it was written, at the body's size. A flex ROW rather than a
    /// truncating box, because it carries a mark and, selected, its ×; the bound and the
    /// ellipsis are the name's.
    ///
    /// Told apart by GROUND: the selected tab wears the pane's (`panel`), the rest the strip's,
    /// and lift a step (`surface`) under the hand. Ink follows, a step quieter on the tabs not
    /// chosen; both inks clear 4.5:1 on every ground here (Phase4). Selecting moves no text.
    ///
    /// `text-small`, not the body size: a name here is truncated to 28ch already
    /// (`pivotName`), so the body size bought the row height back for words it never shows in
    /// full. The caption size reads as chrome rather than prose, which is what a tab's own
    /// name is.
    /// No `border-0`: a `div role="tab"` has no native border to reset, and `border-0` here
    /// cancelled the scroller's `divide-x` — a bare utility beats `:where()`'s zero specificity
    /// however it is ordered (measured live: `border-0` present, `border-right-width` 0 on
    /// every tab; removed, 1px, matching `divide-hair`).
    let private pivotItemBase =
        cls [ "shrink-0 cursor-pointer min-w-touch px-3 phone:px-2 inline-flex items-center justify-center gap-1.5"
              "font-ui font-normal text-small whitespace-nowrap transition-colors duration-150 ease-out"
              "motion-reduce:transition-none"; focusRingInset ]
    let pivotItem = cls [ pivotItemBase; "bg-transparent text-ink-dim hover:text-ink hover:bg-surface" ]
    let pivotItemOn = cls [ pivotItemBase; "bg-panel text-ink" ]
    /// The selected tab under a command's preview: the preview's lifted ground, so the tab and
    /// the command lifted out of its terminal read as one piece (`paneToolbarLifted`).
    let pivotItemLifted = cls [ pivotItemBase; "bg-surface-2 text-ink" ]
    /// A pivot item's name: as much of it as fits, and an ellipsis for the rest — capped so
    /// that at the narrowest pane the splitter allows, the selected item and its × still fit
    /// beside `all` whole. The `all` page writes the name out in full.
    let pivotName = "max-w-28 truncate"
    /// A mark beside a name (`View.terminalMark`): the running pulse, a failed exit, a closed
    /// terminal. Sized to the small type, sat on the name's line.
    let pivotMark = "inline-flex items-center self-center"
    /// A tab's ×: it closes the tab and ends nothing, so it is neutral under the hand rather
    /// than the error red. Sat on the name's line so it costs the row no height. On a phone its
    /// box is the touch target with the glyph centred, pulled in by as much again up and down
    /// (`-my-2.5`) and sideways (`-mx-2`), so the item it rides is still the one height every
    /// item in the strip is and the selected tab is not half the strip.
    let pivotTabClose = cls [ btnIconBare; "self-center phone:min-w-touch phone:min-h-touch phone:-my-2.5 phone:-mx-2" ]
    /// The pivot's one way to make something: a `+` in the pivot's own type, unboxed, faint at
    /// rest like every item not chosen. The cell is the positioning context its menu hangs
    /// from, outside the scroller, because a menu hung inside an `overflow-x-auto` box is
    /// clipped to that box. The same press on a phone as on a desktop: the list has no door
    /// of its own to make a terminal.
    /// A square act at the strip's end — `+`, and the way back to the chat: the strip's
    /// height square, its glyph in the tabs' resting ink.
    let paneStripAct =
        cls [ "shrink-0 self-center w-touch h-touch grid place-items-center bg-transparent border-0 cursor-pointer p-0"
              "text-ink-dim hover:text-ink hover:bg-surface transition-colors motion-reduce:transition-none"; focusRingInset ]
    /// The list button: the list's glyph and how many terminals it holds — on a phone only
    /// one tab fits, and the count is the one clue that there are others. As tall as the
    /// strip, at least as wide, its figures in the tabs' type so they read as one line.
    let private paneListButtonBase =
        cls [ "shrink-0 self-center h-touch min-w-touch px-3 phone:px-2 inline-flex items-center justify-center gap-1.5 border-0 cursor-pointer"
              "font-ui font-normal text-small tabular-nums transition-colors motion-reduce:transition-none"; focusRingInset ]
    let paneListButton = cls [ paneListButtonBase; "bg-transparent text-ink-dim hover:text-ink hover:bg-surface" ]
    /// The same with its sheet down: the sheet's own ground (`allSheet`), flush with it, so
    /// the button reads as the sheet's tab and is the one lit thing in the strip.
    let paneListButtonOn = cls [ paneListButtonBase; "bg-surface text-ink" ]

    /// Everything under the strip — the toolbar, the body, and the list's sheet when it is down,
    /// which is laid over the first two from the strip's edge.
    let paneUnderStrip = "relative flex-1 min-h-0 flex flex-col"
    /// A tab's presence marks: one dot per peer whose caret is in THAT terminal, so a
    /// collaborator typing a command in a terminal you are not looking at is visible from
    /// the pivot rather than only from inside it.
    let terminalTabPeers = "inline-flex items-center gap-0.5 self-center"

    /// The band's second row: what is on screen, said small on the left, and the acts about it
    /// on the right — the acts used to stand at the column's foot, under the composer, a long
    /// way from the name of what they act on. Its hairline is the band's.
    let paneToolbar =
        cls [ "shrink-0 h-touch flex items-center gap-4 min-w-0 pl-3 pr-1.5 font-ui"; Stroke.dividerBottom ]
    /// The toolbar under a command's preview: the command LIFTED out of its terminal, said by
    /// a ground two steps up and held for as long as it is lifted — which is how this product
    /// says nesting and selection (docs/visual-design.md, principle 2).
    ///
    /// No edge beside it. It carried a 2px leading edge in ink as an inset shadow, which is a
    /// painted edge saying depth — the one thing "Honest materials" names twice, and belt-and-
    /// braces besides, since the surface step already says it. The highlight the command lands
    /// with when it settles BACK (`animate-reveal-line`) keeps its rim: there the rim has to
    /// animate, and a border would reflow the row mid-animation.
    let paneToolbarLifted = cls [ paneToolbar; "bg-surface-2" ]
    /// The name a lifted command and the place it settles back into share, so the browser
    /// morphs one into the other (`ViewActions.Morph`, app/tailwind.css). Inline style: a
    /// property with a value no utility composes.
    let liftedName = "view-transition-name: pane-lifted"
    /// The toolbar's words about the thing on screen: where it runs, what it is doing.
    let paneToolbarSays = "min-w-0 flex-1 flex items-center gap-2 truncate text-small font-light text-ink-faint"
    /// A command inside them, in the terminal's own face, cut short rather than wrapped.
    let panePivotSubtitleCommand = "min-w-0 truncate font-terminal text-code-sm text-ink-dim"
    /// A preview's line under the pivot (F3) — `$ seq 1 40 ×`: its name and its close. The
    /// preview is a layer OF the terminal it is laid over, whose tab stays selected in the
    /// strip; as an item of its own in the strip it was a sibling that looked like one more
    /// tab, and on a phone it cost the strip one of the three tabs it has room for. The
    /// subtitle's place, at the body's size rather than the caption's: this is the one line
    /// that names what is on screen, and the action row's verbs under it are never louder
    /// than the name of what they act on.
    let panePreviewHead = "min-w-0 flex-1 flex items-center gap-1.5 font-ui text-body"
    /// The preview's name, cut short rather than pushing its close off the line.
    let panePreviewName = "min-w-0 truncate text-ink"
    /// Its close: a bare ×, neutral rather than red — closing a preview ends nothing — and a
    /// thumb's 44 on a phone.
    let panePreviewClose = cls [ btnIconBare; "phone:min-w-touch phone:min-h-touch" ]

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
    /// ROW's to give where the row holds the act directly (`paneActions`); an
    /// act inside a band line sits in a group of its own there, so it says so itself.
    let private paneActBase =
        cls [ "shrink-0 inline-flex items-center gap-1.5 bg-transparent border-0 cursor-pointer p-0 no-underline"
              "font-ui font-light lowercase whitespace-nowrap"
              "transition-colors duration-150 ease-out motion-reduce:transition-none"; focusRingFar ]
    /// There are TWO faces below and no more, one per kind of line a bare verb ends: the
    /// toolbar's caps and a band line's body. Others stood here with nothing wearing them —
    /// "show in <terminal>"'s, gone when the row became the way in (#1308), and the empty
    /// pane's door, gone when the sheet's New terminal became the one way to make one. A face
    /// with no wearer is a face the next author reaches for by accident, so it is deleted
    /// rather than kept for symmetry.
    ///
    /// An act in the toolbar: its mark and its word in the caps voice, the face the agent's
    /// interrupt wears — a verb about the whole thing on screen, not a word in a sentence.
    let private paneToolbarFace =
        cls [ "h-touch px-1.5 shrink-0 inline-flex items-center gap-1.5 bg-transparent border-0 cursor-pointer no-underline"
              caps; "whitespace-nowrap text-ink-dim hover:text-ink transition-colors motion-reduce:transition-none" ]
    let paneToolbarAct = cls [ paneToolbarFace; focusRingInset ]
    /// An act inside a line of the command band (the lease bar, the cannot-run notice): the same word
    /// at the size of the line it sits in, so it reads as part of the sentence it ends.
    let bandAct = cls [ paneActBase; "text-body text-ink-dim hover:text-ink phone:min-h-touch" ]
    /// The band act the line is asking for: handing the keyboard back, opening a new terminal.
    let bandActPrimary = cls [ paneActBase; "text-body text-blue hover:text-blue-up-1 phone:min-h-touch" ]

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
    /// border and a ring held outside it would be cut away on every side but none — which is
    /// `focusRingInset`, composed rather than spelled out: the ring had been written here as
    /// its four utilities, a second way to draw the one ring the product has (principle 5).
    let panePanel = cls [ paneBody; focusRingInset ]
    /// The panel under a command's preview, on its lifted ground and with no edge of its own,
    /// for the reason the toolbar over it has none (`paneToolbarLifted`).
    let panePanelLifted = cls [ panePanel; "bg-surface-2" ]
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
    ///
    /// The last three lines are the vendored sheet being held to this product's finish, and
    /// every one of them is `!` for that same reason — an unlayered declaration beats a
    /// layered one however specific, and an important layered one beats it back.
    ///
    ///   * SHARP corners. The sheet rounds the player by 4px, its tooltip by 4 and the played
    ///     part of its scrub bar by 3. A rounded rectangle is a hybrid of the two primitives
    ///     and does not exist here (docs/visual-design.md, Form). Its marker dots stay round:
    ///     a point is round, and those are points.
    ///   * NO shadow. The play mark carried a `drop-shadow`, which is the one thing "Honest
    ///     materials" forbids outright — nothing but the brand mark may look lit. The mark
    ///     itself needs nothing done to it: the glyph is a filled polygon with a triangle
    ///     masked out of it, which renders as the outline triangle the icon rule asks for.
    ///   * The player's GROUND and ink are this product's, by name. They were `#000000` and
    ///     `#ffffff` written into the sheet as its own `--term-color-*` pair — the same two
    ///     colours `bg` and `ink` hold today, and tokens that cannot follow them tomorrow.
    ///
    /// What is NOT reachable from here, and so is not fixed: the sheet maps all sixteen of its
    /// ANSI slots to the foreground, so a recording replays monochrome where the live screen
    /// renders `--color-term-*`. That is sixteen more properties and a picture of a replay to
    /// check them against, which is its own change.
    let replayStage =
        "h-full min-h-24 [&_.ap-player]:min-w-full [&_.ap-term]:mx-auto "
        + "[&_.ap-tooltip]:w-max [&_.ap-tooltip]:max-w-48 [&_.ap-tooltip]:whitespace-normal! "
        + "[&_.ap-tooltip]:wrap-anywhere [&_.ap-tooltip]:line-clamp-2 "
        + "[&_.ap-player]:rounded-none! [&_.ap-tooltip]:rounded-none! [&_.ap-gutter-full]:rounded-none! "
        + "[&_.ap-play-button_svg]:filter-none! "
        + "[&_.ap-player]:[--term-color-background:var(--color-bg)]! "
        + "[&_.ap-player]:[--term-color-foreground:var(--color-ink)]!"
    /// A stretch's facts, above whatever renders its recording.
    let paneFacts = "shrink-0 flex flex-col gap-1 px-3 py-3 " + Stroke.dividerBottom

    /// The acts in the toolbar, at its right end, after the words about the thing.
    let paneActions = "shrink-0 flex items-center gap-1"

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
    let allPage = "flex flex-col pb-3 select-none"

    /// The list's sheet: dropped from the strip's edge over the pane, which goes on showing the
    /// terminal under it. A step up the ground (`surface`), because it is a layer over the
    /// pane, and opaque, because rows read over a terminal's text are rows nobody can read; its
    /// rim is at the foot, where it ends over the terminal. As tall as it needs and no taller
    /// than the pane, scrolling inside itself.
    ///
    /// Its focus ring is `focusRingInset` — inset because the sheet scrolls and clips, and
    /// composed because there is one ring in this product: it had been written here as its
    /// four utilities, which is a second way to draw it (principle 5).
    ///
    /// No drop shadow. It carried one, and a surface lit from above is the whole of what
    /// "Honest materials" forbids (docs/visual-design.md, Form: surfaces are flat, depth is
    /// said by blur over something real). What the shadow was for is already said by three
    /// things that are real — a step up the ground, a rim where the sheet ends, and the pane
    /// dimmed under it (`allSheetScrim`).
    let allSheet =
        cls [ "absolute inset-x-0 top-0 max-h-full z-30 overflow-y-auto overscroll-contain"
              "bg-surface border-b border-edge"; focusRingInset ]

    /// What the sheet is laid over, dimmed: the pane goes on showing under it, a step back.
    /// Under the sheet in the stack and over everything else the pane has.
    let allSheetScrim = cls [ "absolute inset-0 z-20 p-0 border-0 cursor-default bg-black/45"; focusRingInset ]

    /// One row: its name (and the marks beside it) over what it last ran, its verbs at the
    /// right. The names line up down the page because every mark sits AFTER its name — a
    /// leading column of marks of four different widths is what made the old list's left
    /// edge ragged.
    ///
    /// The row's button holds both its lines (`terminalListName`), so a row needs be no
    /// taller than they are to be a thumb's: 60px.
    let terminalListRow =
        cls [ "group/row flex items-stretch gap-3 pr-1 min-h-15"
              "hover:bg-surface-2 has-[[aria-current=true]]:bg-surface-2"; Stroke.dividerBottom ]

    /// The row's own control: its name, which opens it — the pivot's type, so the page reads
    /// as the pivot's names written out in full. Ink at rest so the page reads as a list of
    /// names, blue under the pointer because that is what interactive means here.
    ///
    /// It is both of the row's lines, the name over what it says, so the row is pressed
    /// anywhere but its verbs.
    let private terminalListNameBase =
        cls [ "min-w-0 flex-1 flex flex-col justify-center gap-0.5 pl-3 pr-0 py-2 bg-transparent border-0 cursor-pointer text-left"
              "font-ui font-normal text-body transition-colors"; focusRingInset ]
    let terminalListName = cls [ terminalListNameBase; "text-ink" ]
    /// The name itself, as much of it as fits.
    let terminalListNameText = "min-w-0 truncate"
    /// A row's two lines where they are not a control: a file on its way in.
    let terminalListWho = "min-w-0 flex-1 flex flex-col justify-center gap-0.5 pl-3 py-2"

    /// A closed row's name: dimmer, which says which half of the list it is in by its tone
    /// rather than by repeating the word — its mark says the rest.
    let terminalListNameClosed = cls [ terminalListNameBase; "text-ink-dim" ]

    /// A row's name line: the name, then its marks, on one line that gives way in the name.
    let terminalListNameLine = "min-w-0 flex items-center gap-2"

    /// A closed terminal's "recording lost": said once where it is about the one terminal on
    /// screen (its closed band) and once per row on the `all` page — so in the quietest voice
    /// that is still read, never squeezed, lowercase as written. It was the error red in
    /// tracked capitals, and nine closed rows made a column of alarms about something nobody
    /// can do anything about; the hollow mark beside the name already says the terminal is
    /// closed, and the missing rewind says there is nothing to play.
    let terminalGone = "shrink-0 whitespace-nowrap font-ui font-light text-small text-ink-faint"

    /// What a terminal row's terminal is doing or last ran, under its name, led by its
    /// state's mark: small, and `ink-dim` — it tells nine `term N` rows apart, and it is not
    /// the thing the row opens. In the UI's face: what it says is mostly a sentence an agent
    /// wrote, and a sentence in the terminal's face read as output.
    let terminalListSubtitle =
        "min-w-0 flex items-center gap-1.5 font-ui font-light text-small text-ink-dim [&_[data-pane-mark]]:shrink-0"
    let terminalListSummary = "min-w-0 truncate"

    /// A row's verb, as a WORD: `rewind`, `kill`, `replay`, `put away`. They were glyphs, and
    /// the kill's was a square — which is what stop looks like, beside a command that now has
    /// a real Stop. A word cannot be read as anything else, and a verb that rides a listed row
    /// is bare, in the caps voice every bare verb speaks in, `ink-dim` at rest
    /// (docs/visual-design.md, "Hierarchy of controls") — ink under the hand, or the error red
    /// for the one that ends something.
    let private terminalListActBase =
        cls [ "h-6 px-1 shrink-0 inline-flex items-center justify-center gap-1.5 bg-transparent border-0 cursor-pointer"
              caps; "whitespace-nowrap transition-colors"; focusRing ]
    let terminalListAct = cls [ terminalListActBase; "text-ink-dim hover:text-ink" ]
    let terminalListKill = cls [ terminalListActBase; "text-ink-dim hover:text-err" ]

    /// The row's verbs, at its right edge, WORN — on EVERY row, at rest.
    ///
    /// They used to come up under the pointer and be `sr-only` where nothing can hover, with
    /// a second name (`terminalListVerbsAtRest`) carrying the fade. #1308 made them worn and
    /// left the second name an alias of this one: two names for one value, which is a second
    /// way to draw one thing (principle 5), and two comments still arguing for a reveal the
    /// code no longer did. Both are gone, and the rule they were breaking is why they do not
    /// come back: state is worn, not hinted, and a thumb has no hover
    /// (docs/visual-design.md, principle 4) — a kill reachable only by a pointer is a kill
    /// half the readers of this page cannot see at all.
    ///
    /// On a phone each is at least a 44px box with its word centred (`[&>*]`): the 24px line
    /// is WCAG 2.5.8's floor for the MARK, never for the target, and a kill 4px from a rewind
    /// is a kill pressed by a thumb aiming at the rewind.
    let terminalListVerbs =
        "ml-auto self-center flex items-center gap-4 shrink-0 phone:gap-2 phone:[&>*]:min-w-11 phone:[&>*]:min-h-11"

    /// The one row the closed terminals fold under: the section voice, its count, and the
    /// fold's mark at the far edge, turned while it is open. The row is the press — a
    /// thumb's 44 across the sheet.
    let listClosedFold =
        cls [ "w-full h-11 flex items-center gap-2.5 pl-3 pr-4 bg-transparent border-0 cursor-pointer text-left"
              caps; "text-ink-dim hover:text-ink hover:bg-surface-2 transition-colors motion-reduce:transition-none"
              Stroke.dividerBottom; focusRingInset ]
    let listFoldMark = "ml-auto inline-flex transition-transform motion-reduce:transition-none"
    let listFoldMarkOpen = cls [ listFoldMark; "rotate-180" ]
    /// How many a section holds, beside its name: a step quieter, figures that hold still.
    let listCount = "text-ink-faint tabular-nums"

    /// The page's word when there is nothing on it.
    let contentListEmptyWord = "px-3 py-3 font-ui font-light text-body text-ink-faint select-none"

    /// The list as the pane's body, when nothing is open to lay it over: the sheet's rows on the
    /// pane's own ground, filling the pane and scrolling inside it — a page, not a layer, so no
    /// step up and no rim.
    let allDocked = cls [ "flex-1 min-h-0 overflow-y-auto overscroll-contain"; focusRingInset ]


    /// A section of the sheet's head: what it is called, and its acts. One line on a wide
    /// screen, the acts at its right end and one hairline under both. Two on a phone, the acts
    /// a full-width row of their own under the name, because a thumb lands on a row and not on
    /// the corner of one.
    /// The sheet's first head sits at the sheet's top edge; the next is a clear gap below the
    /// list before it, so the two sections read as two.
    let listSectionHeadFirst = "flex phone:flex-col wide:items-center wide:border-b wide:border-hair"
    let listSectionHead = cls [ "mt-8"; listSectionHeadFirst ]
    /// What a section is called: the pivot's light lowercase at its step, in `ink`, with its
    /// count beside it. It was the caps voice in `ink-dim`, which is the voice of the closed
    /// fold and of every verb on the sheet — a heading that read as one more thing to press
    /// (docs/visual-design.md, Type: caps are for labels and buttons).
    let listSectionLabel =
        cls [ "h-11 flex items-center gap-2.5 pl-3 pr-1 select-none wide:flex-1"
              "font-ui font-extralight text-pivot tracking-[-0.01em] lowercase text-ink"
              "phone:border-b phone:border-hair" ]
    /// A section's act, a blue link like the repo picker's branch (`askRowBranch`): no box,
    /// because the row it stands on is the box.
    let private listSectionActBase =
        cls [ caps; "h-11 shrink-0 inline-flex items-center gap-1.5 px-3 bg-transparent border-0 cursor-pointer"
              "text-blue hover:text-blue-up-1 transition-colors motion-reduce:transition-none" ]
    let private listSectionActFace = cls [ listSectionActBase; "phone:w-full phone:border-b phone:border-hair" ]
    /// The terminals' two acts on one line: on a phone, New terminal takes the row and where
    /// it starts sits at the row's right end, as the branch sits on the repo picker's row.
    let listSectionActs = "flex items-center phone:border-b phone:border-hair"
    let terminalNew = cls [ listSectionActBase; "phone:flex-1"; focusRingInset ]
    /// Where New terminal starts one, and the way to choose: the repo picker's branch link —
    /// the place's name in the terminal face, then the chevron, because it goes somewhere.
    /// Shown only while there is more than one place.
    let startInLink = cls [ listSectionActBase; focusRingInset ]
    let startInWord = "text-ink-dim"
    let startInName = "font-terminal text-small normal-case tracking-normal"

    /// The list of places, in the sheet in place of its terminals and files: its head (the
    /// way back, and what the list is for), then one row per place, the chosen one ticked.
    let startInHead =
        cls [ "h-11 flex items-center gap-1 pr-3 select-none text-ink-dim border-b border-hair"; caps ]
    let startInBack =
        cls [ "h-11 w-11 shrink-0 inline-flex items-center justify-center bg-transparent border-0 cursor-pointer"
              "text-blue hover:text-blue-up-1 transition-colors motion-reduce:transition-none"; focusRingInset ]
    let startInRow = cls [ "flex hover:bg-surface-2"; Stroke.dividerBottom ]
    let startInChoice =
        cls [ "min-w-0 flex-1 flex items-center gap-2 pl-3 pr-3 py-2 min-h-11 bg-transparent border-0 cursor-pointer text-left"
              focusRingInset ]
    /// The tick's cell is on every row and filled only on the chosen one, so the names do
    /// not move when the choice does.
    let startInMark = "w-5 shrink-0 self-center text-blue"
    let startInWho = "min-w-0 flex-1 flex flex-col gap-0.5"
    /// The place's name, in the terminal face, as the repo picker writes a branch.
    let startInChoiceName = "font-terminal text-body text-ink truncate min-w-0"
    /// Which repo declared the place, and what its file said it is for — prose somebody
    /// wrote, so wrapped and clamped rather than cut after its first words.
    let startInNote = "line-clamp-2 font-ui font-light text-small text-ink-faint"
    /// "default", beside the session's own place.
    let startInDefault = cls [ label; "ml-auto shrink-0" ]

    /// An artifact's row: the same row as a terminal's, so the two sections read as one list
    /// of things rather than two designs.
    let artifactListRow = terminalListRow

    /// The size under an artifact's name — the one fact that decides whether to open it here
    /// or take it away, so it is on the row rather than behind it.
    let artifactListSize = "block font-ui font-light text-small text-ink-faint tabular-nums"

    /// A file on its way in: the name a shared one's row will carry, in its type, but `ink-dim`
    /// and not a control — there is nothing to open until the store has it.
    let uploadName = "min-w-0 truncate font-ui font-light text-body text-ink-dim"

    /// The files' act: the `<label>` of a file input, so its ring is the input's
    /// (`focusRingHeldInset`). The input is `sr-only`, so the label draws the ring while it has focus.
    let uploadControl = cls [ listSectionActFace; focusRingHeldInset ]

    /// Where a file being dragged over the sheet will land: under the files' head, dashed,
    /// because it is a place and not a control. Drawn only while a drag carrying files is over
    /// the sheet (`ClientModel.FilesOver`). Dropping anywhere on the sheet uploads, so this
    /// shows that the drop is taken. It is not a target of its own.
    let uploadDropZone =
        cls [ caps; "mx-3 my-3 h-20 flex items-center justify-center gap-1.5 pointer-events-none"
              "border border-dashed border-blue bg-surface-2 text-blue" ]

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
    /// A query container (`@container`), so a block lays out by the PANE's width — set by its
    /// splitter — rather than the screen's (`terminalBlockSummary`).
    ///
    /// Read like a document: the space BETWEEN commands (20px) is several times the space
    /// inside one — the byline over its command (`terminalBlockByline`, 4px), the command over
    /// its output — so a command's lines group with each other and not with the next one's.
    /// At one gap throughout, an output's last line sat as close to the next author as to its
    /// own command.
    let terminalStream = "@container flex flex-col gap-5 py-3"
    /// The commands inside a run's fold: the stream's rhythm, without its edges.
    let terminalBlockRunBody = "flex flex-col gap-5"

    /// One block: the command that ran, then everything it printed.
    ///
    /// It is not a card. A card per command turned a scrollback into a stack of boxes —
    /// borders, fills and 12px of padding repeated around every two lines of mono, which is
    /// the one thing a terminal does not look like. What separates two blocks is what
    /// separates them in a terminal: a green prompt glyph, the command in ink, its output
    /// dim beneath, and a line of air.
    let terminalBlock = "flex flex-col -mx-3 px-3"
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
    ///
    /// A strip a step up the ground (`surface`), edge to edge of the column: the chrome says
    /// where one command's group begins before a word of it is read (docs/visual-design.md,
    /// principle 2).
    ///
    /// Two lines: who ran it, when, and how it went (`terminalBlockByline`), over the command,
    /// which then has the column's whole width. In a pane 40rem wide or more the byline is a
    /// gutter beside the command instead, the author over the time — the width a short
    /// command leaves empty at its end is put to use, and the commands stand in one column.
    let terminalBlockSummary =
        cls [ "sticky top-0 z-10 bg-surface -mx-3 px-3 py-1.5 flex flex-col gap-1"
              "@min-[40rem]:flex-row @min-[40rem]:items-baseline @min-[40rem]:gap-3" ]
    /// Who ran it at the start, when and how it went at the end: one line, or the gutter.
    let terminalBlockByline =
        cls [ "flex items-center gap-2 min-w-0 min-h-5"
              "@min-[40rem]:w-30 @min-[40rem]:shrink-0 @min-[40rem]:flex-col @min-[40rem]:items-start @min-[40rem]:gap-0 @min-[40rem]:min-h-0" ]
    /// When it ran and its state, at the byline's far end; under the author in the gutter.
    let terminalBlockWhen =
        "ml-auto shrink-0 inline-flex items-baseline gap-2 font-ui font-light text-small text-ink-faint tabular-nums @min-[40rem]:ml-0"
    /// What a block says under its command, held to the command's column when the byline is
    /// a gutter beside it.
    let terminalBlockBody = "flex flex-col min-w-0 @min-[40rem]:pl-33"
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

    /// The strip on top of a terminal's command line while a command runs there: its one
    /// verb at the trailing edge, as tall as the chat's tray is: a thumb's on a phone.
    let terminalStopStrip = "flex items-center justify-end h-10 phone:h-11 pr-1.5"
    /// Stop: the bare danger verb with its glyph, which is what the chat's interrupt says too.
    let terminalStop = cls [ btnBareDanger; "h-full min-w-11 px-1.5 gap-1.5 justify-center" ]

    /// Who ran a command, on its byline or its run's line, in the transcript's face
    /// (`View.actorHead`): the square and the name. Bounded, so the time and state beside it
    /// keep their room; the name is what gives way, on a phone where neither fits.
    let terminalBlockAuthor = "shrink-0 min-w-0 max-w-[60%] @min-[40rem]:max-w-full inline-flex items-center gap-1.5 @min-[40rem]:gap-2 [&>:last-child]:truncate"
    /// Its square: the transcript's 20px in the gutter a wide pane gives it, 14px on the
    /// narrow line over the command, where a 20px square stood taller than the caps beside it
    /// and than the command it heads.
    let terminalAuthorSquare = "w-3.5 h-3.5 shrink-0 @min-[40rem]:w-5 @min-[40rem]:h-5"

    /// The `$` that leads a command wherever a command is RECORDED — a block in the
    /// scrollback, a chip in the chat, an empty output. It is a mark that leads a line
    /// (docs/visual-design.md, Space), so it hangs quieter than the command it heads and says
    /// nothing about state.
    ///
    /// `ink-faint`, not green. Every one of these was green, the agent's and the finished
    /// ones alike, and green means two things this glyph is neither of: LIVE, which a command
    /// that exited is not, and the PEOPLE, which an agent's command is not (Colour). Nor may
    /// it be an attribution — people are told apart by their square, never by a colour, and
    /// the byline above carries that square already.
    ///
    /// The command line's own placeholder glyph (`terminalCommand`) wears this same weight
    /// now too, not a louder one: one tone for the prompt wherever it stands, record or live
    /// edge alike.
    let terminalPrompt = "shrink-0 font-terminal text-code text-ink-faint select-none"
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
    let terminalOutputExpand =
        cls [ "h-6 shrink-0 self-start inline-flex items-center bg-transparent border-0 cursor-pointer p-0 phone:min-h-11"
              "font-ui font-light text-small lowercase text-ink-dim hover:text-ink transition-colors"; focusRing ]
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

    /// The button in it: an opaque square, so it reads over whatever is scrolling under it.
    let terminalJumpToLatest =
        cls [ "w-8 h-8 phone:w-11 phone:h-11 flex items-center justify-center bg-surface"
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
    /// and the rule can never quietly drift from the message composer's.
    ///
    /// The bottom clearance is NOT just `composer`'s own `pb-4`, though: on a phone the
    /// message composer always carries a second row under its line (the model picker and
    /// attach, `draftActionsRow`) even when nothing in it needs pressing, and this band has
    /// nothing of its own to put there — Run stays on the command line rather than dropping
    /// below it the way Send does. Matching only the PADDING (both at `pb-4`) left the two
    /// bars 45px apart in total height, measured, which is exactly that row's own height: a
    /// terminal beside a chat column, both idle, read as two different-sized objects. The
    /// padding spends what the row would have, on a phone only, so the two bars land at one
    /// height — `h-11` command line (44) + `pb-[3.8125rem]` (61 = the row's 45 + the shared
    /// `pb-4`'s 16) = 105, `composer`'s own total there. A wide screen spends nothing extra:
    /// there the message composer stands off the bottom edge in its dock (`dock`), so the two
    /// bands share no bottom edge to agree on.
    let terminalComposer = composerBand + " pb-4 phone:pb-[3.8125rem]"

    /// A row in the band that is not the command line — the lease bar, the cannot-run
    /// notice. They used to inherit the section's padding; the band has none.
    let terminalBandRow = "flex items-center gap-2 px-3 py-2 phone:[&>button]:min-h-11"

    /// A terminal that cannot run commands: not one line but a short read, top to bottom —
    /// what happened, when, what was refused, and the way on. Stacked rather than squeezed
    /// onto the band's one row, because a phone has no room for it beside a control.
    let terminalPaused = "flex flex-col gap-1.5 px-3 py-2 min-w-0"
    /// Its first sentence: the caption's size in full ink — it is news, not a state at rest.
    let terminalPausedTitle = "min-w-0 font-ui font-normal text-small text-ink"
    /// What was refused: an indented list, no rule beside it — borders are for fields and
    /// buttons.
    let terminalHeldList = "m-0 pl-3 list-none flex flex-col gap-1 min-w-0"
    /// One refused command: what it was, and who queued it.
    let terminalHeldRow = "flex items-center gap-2 flex-wrap min-w-0"
    /// An act and its label, on one line that wraps on a phone.
    let terminalChoice = "flex items-baseline gap-2 flex-wrap min-w-0 phone:[&>button]:min-h-11"
    /// The chat's row for the agent's refused commands: the same read, in the column.
    let chatPaused = "flex flex-col gap-1.5 py-2 min-w-0"

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
    /// room at the trailing edge for the 44px Run (`btnInField`) — and the message composer
    /// stands at 44 there too (`draftInput`'s `phone:pt-3`).
    ///
    /// Its placeholder glyph wears ink-faint now, the same weight the message composer's own
    /// placeholder wears, rather than the GREEN it used to be: every recorded command's prompt
    /// already wore ink-faint (terminalPrompt above), and this one agrees with it.
    ///
    /// h-10 phone:h-11: an explicit height, not left to padding and the line box alone. A
    /// native input does not size its content box to line-height the way a block element
    /// does — measured, the padding-and-line-height arithmetic below lands this field 3px
    /// taller than the message composer's band on a phone (47px against 44) even though both
    /// compute the same numbers on paper. The height is stated outright so the two never
    /// drift apart, on either breakpoint, however a browser rounds a form control's box.
    let terminalCommand =
        cls [ "w-full h-10 phone:h-11"; fieldBare
              "font-terminal text-code text-ink px-3 py-3 pr-10 phone:py-3.5 phone:pr-13 placeholder:text-ink-faint"; touchType ]
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
    ///
    /// Its glyph sits on the title's x-height, so the two marks either side of the title
    /// point at one stroke (`navReopen`). On a phone it drops by the chevron's own amount. On
    /// a wide screen the button stands its words on the title's baseline (`headerAside`), which
    /// leaves the glyph's middle below the stroke, so it rises instead.
    let terminalReopen =
        navChevronBack + " shrink-0 *:first:-translate-y-[3px] phone:*:first:translate-y-[1.5px]"
    /// Its face says what it opens on a wide screen ("2 terminals", `View.contentReopen`), and
    /// only the count on a phone, whose band has no room for the words. Exactly one of the two
    /// is drawn at any size, by the one pair of variants that split the screens.
    let terminalReopenWords = "phone:hidden"
    let terminalReopenBare = "wide:hidden"

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

    /// A span's colours, resolved with `Inverse` and `Dim` already applied — both are
    /// render-time swaps, which is exactly why the parser leaves them as flags rather than
    /// pre-swapping colours it does not know the defaults for.
    ///
    /// INVERSE (SGR 7) swaps the pair, and fills in whichever half the program never set from
    /// this palette's own ends: a default background becomes the foreground as index 0, a
    /// default foreground becomes the fill as index 7. So a run that set only one side still
    /// inverts to a pair a reader can read. A fill on its own moves no foreground — this said
    /// for a while that a background forces the foreground to the page ground, which neither
    /// half of was true: nothing here reads the page ground, and index 0 in this palette is
    /// `ink-faint` rather than `bg` (app/tokens.css).
    ///
    /// DIM (SGR 2) is resolved HERE, as a colour, because dimming is a role and never opacity
    /// (docs/visual-design.md, Colour): opacity breaks the contrast floor, and the floor is
    /// first in the Deciding order. `opacity-75` stood here on the argument that dim text is
    /// decoration — it is not, it is output, and 75% of a colour pinned at 4.5:1 is a colour
    /// pinned at nothing.
    ///
    /// The role a faint run reads in is index 0, which this palette defines as `ink-faint`
    /// (app/tokens.css) — the quietest ink that still keeps AA on every surface, which Phase4
    /// pins. So faint is one colour rather than a shade of each of the 256: there is no step
    /// below a colour already held at the floor that is still at the floor, and a run cannot
    /// wear the red role and the faint role at once. A program asking for faint is asking for
    /// the quietest thing on this ground, and that is what it gets.
    ///
    /// Faint has nowhere to go against a FILL, where contrast is the pair's and the program
    /// set both halves of it, so a filled run keeps the foreground it was given.
    let private ansiColours (style: AnsiStyle) : AnsiColour * AnsiColour =
        let foreground, background =
            if style.Inverse then
                (match style.Background with DefaultColour -> IndexedColour 0 | c -> c),
                (match style.Foreground with DefaultColour -> IndexedColour 7 | c -> c)
            else style.Foreground, style.Background
        if style.Dim && background = DefaultColour then IndexedColour 0, background
        else foreground, background

    /// The utility classes for a styled run. Intensity is not here: bold is the weight the
    /// mono face ships (600), and faint is a colour `ansiColours` has already resolved.
    let ansiClasses (style: AnsiStyle) : string =
        let foreground, background = ansiColours style
        [ if style.Bold then "font-semibold"
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
    ///
    /// iOS does not shorten `100dvh` for its keyboard, though. It pans the full-height page up
    /// to the composer, and the top of the timeline goes above the screen, out of reach of any
    /// scroll. So the session's page measures the visual viewport (`PaneShell.followVisualViewport`)
    /// and the shell is FIXED to it: `--visual-h` tall, `--visual-top` down the layout viewport
    /// (`--shell-position` says fixed). Unset, as before the script runs and on every page that
    /// does not run it, they are relative, `100dvh` and 0: the same box as before, in flow, so a
    /// page that mounts several (the fixtures harness) stacks them rather than piling them up.
    let app =
        "[position:var(--shell-position,relative)] inset-x-0 top-[var(--visual-top,0px)] flex h-[var(--visual-h,100dvh)] overflow-hidden bg-bg text-ink font-ui antialiased"

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

    // --- The Manager's pages: the band, the rail and the registry tables ------------------
    //
    // The management surface is a DOCUMENT rather than a shell — a band, then sections of
    // tables on one rail — and it is the only surface in the product that lists records in a
    // table. It used to spell all of that in raw utilities at forty-odd sites, which is how it
    // came to be a second design beside the session's: a band of its own height that stayed
    // 88px on a phone, rows with their own rule and hover tone, heads in no voice at all, and
    // a layout switched on a reading column's breakpoint while the band was switched on
    // nothing. These are the values it wears instead, and nothing here is a second way to draw
    // something the sections above already draw — a table row is not a `rowBase` row (that is
    // a surface with a leading edge), and the band is this file's only other wearer of
    // `spacing-band`.

    /// The band every Manager page shares: `spacing-band` tall, `spacing-band-phone` on a
    /// phone, its contents standing on the band's bottom edge so the lockup sits where the
    /// shell's wordmark does (docs/visual-design.md, Space). The hairline under it runs the
    /// width of the viewport; only what is IN it keeps to the rail.
    ///
    /// OPAQUE, and sticky over a registry that scrolls under it. It wore the `acrylic` for a
    /// while, which made the one band in the product that is frosted grey where every other
    /// is black — and, because `acrylic`'s rim is a border COLOUR for all four sides, it also
    /// put two `border-*` colours on one element and left which one painted the hairline to
    /// stylesheet order. The blur is for a surface that floats over another; this is the page's
    /// own head.
    ///
    /// `phone:`, not a width: a phone turned sideways is still a phone, and this is the shell's
    /// layout question rather than a reading column's (docs/visual-design.md, Phone and wide).
    let pageBand =
        cls [ "h-band phone:h-band-phone shrink-0 sticky top-0 z-ground bg-bg"
              "flex items-end pb-band-foot phone:items-center phone:pb-0"
              Stroke.dividerBottom ]

    /// The lockup in that band. On a phone the band is one thumb-sized row, which cannot hold
    /// 32px type beside a 56px mark — so the mark steps down to 32px and the wordmark to the
    /// pivot step (19px to the mark's 32 is the lockup's own ratio, 32 to 56, near enough), and
    /// the two centre on the row rather than standing on its edge. The wordmark used to go on
    /// a phone, as the session's own phone band drops it (`header`); but this band has no title
    /// beside it to say whose product this is, and a mark alone said nothing a reader could
    /// read.
    let pageBandLockup = lockup + " phone:items-center"
    let pageBandMark = lockupMark + " phone:w-8 phone:h-8 phone:mb-0"
    let pageBandWordmark = wordmark + " phone:text-pivot"

    /// The page's content rail: the registry's measure, centred, with `spacing-rail` between
    /// its text and the page's edge and the shell's own narrower gutter on a phone. Worn by
    /// the band's CONTENTS and by the sections under it, as one value, so the lockup and the
    /// table's left edge cannot drift apart.
    ///
    /// The measure is the registry's, not a reading column's: `name` is the only elastic
    /// column and it gets whatever the fixed ones leave, so at the 896px this used to be every
    /// session rendered as a letter and an ellipsis.
    let pageRail = "max-w-6xl w-full mx-auto px-rail phone:px-4"

    /// The foot of a Manager page: the build, on the same rail, under whatever the page holds
    /// (`buildMark`).
    let pageFoot = cls [ pageRail; "shrink-0 pb-6" ]

    /// A standalone page's one column — a refusal, a 404: the reading measure on the page's
    /// rail, with the band above it and the build below, so a page that says no is the same
    /// product as the registry.
    let pageProse =
        cls [ "max-w-measure w-full mx-auto px-rail phone:px-4 py-10 flex flex-col gap-3" ]

    /// The way off a standalone page, and the way back from the opening screen: the one
    /// hyperlink face (`proseLink`) with the product's focus ring on it — a link with none is
    /// a control the keyboard cannot see, and the floor is never traded — and a thumb's target
    /// on a phone, where the words alone are 24px of glass.
    let pageWayBack = cls [ proseLink; focusRing; "inline-flex items-center phone:min-h-touch" ]

    /// The hairline that tells one listed record from the next, and the only rule these tables
    /// draw: under the heads, and under every row (principle 2 — chrome shows structure).
    let tableRule = Stroke.dividerBottom

    /// A row you can act on: the same hairline, and the surface lifts a tone while the pointer
    /// is in it — the lift says "this row", the edge says nothing, which is the rule every
    /// other listed thing in this file follows (`rowLift`). A row that is a RECORD and nothing
    /// more wears `tableRule` alone: a surface that brightens under the hand promises a press
    /// that is not there.
    let tableRow = cls [ tableRule; "hover:bg-surface transition-colors"; Motion.quick ]

    /// A column's head: the label voice, which is the one voice this product has for a head.
    /// Both of these wear it, including the one that is also the sort control — `sortHeader`
    /// composes the same voice, and the two sat 2px apart for as long as only one of them did.
    let tableHead = cls [ label; "py-2 pr-4 align-baseline" ]
    /// The head over the rail of verbs, whose padding is on the other side.
    let tableHeadRail = cls [ label; "py-2 pl-4 align-baseline" ]

    /// A cell. Its first line of text stands on the row's baseline — never box-centred, which
    /// is what left a one-line cell aligned to neither line of the two-line one beside it
    /// (docs/visual-design.md, Space: rows align on the text baseline).
    let tableCell = "py-3 pr-4 align-baseline"
    /// A cell whose content is several lines of its own (a hook endpoint's secrets): there is
    /// no single line for it to stand on, so it starts at the top of the row.
    let tableCellTop = "py-3 pr-4 align-top"
    /// The last cell, on the table's right rail, holding the row's verbs: aligned to the top,
    /// because the verbs ride the row's FIRST line (`tableRailVerbs`) rather than the middle
    /// of however many lines the row has.
    let tableCellRail = "py-3 pl-4 align-top"
    /// The verbs inside a rail cell: at the row's trailing edge, on its first line — 24px, the
    /// name's own line box, so a verb arriving or leaving moves nothing. 10px apart rather than
    /// 8: the gap used to be what an icon's hit area overhung its box by, and it is now plain
    /// clearance — `btnIconBareTouch` grows its BOX on a phone (`hitTouch`), so the targets sit
    /// side by side in flow instead of reaching onto the word beside them.
    let tableRailVerbs = "flex items-center justify-end gap-2.5 h-6"

    /// What a table says over nothing, where the table stays because its heads carry a control
    /// (the registry's sort): one line in the small voice with room around it. The only cell in
    /// these tables that is not on a row's baseline, because there is no row.
    let tableEmpty = cls [ small; "py-10 text-center" ]
