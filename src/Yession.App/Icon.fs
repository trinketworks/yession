namespace Yession.App

open Lit
open Yession.Domain.Content

/// The client's icons, as inline SVG.
///
/// They used to be text: `✕`, `↑`, `↓`, `‹`, `✓`, `✗`. A glyph renders only if the reader's
/// machine has a font carrying it, and the `font-ui` face this design asks for (`Monaspace Argon`
/// first) is a WINDOWS stack — everywhere else the browser falls through to whatever it has,
/// which may substitute a differently-styled glyph, an emoji, or a tofu box. The buttons stayed
/// clickable and their `aria-label`s stayed correct, but a delete button that shows a tofu box
/// is a broken button.
///
/// Drawn instead: one 16-unit grid, `currentColor` (so `hover:`/`active:` colour them like
/// text), square caps — Metro's geometry, no curves, no fills. `aria-hidden` because every icon
/// here sits inside a control that carries its own accessible name. The attention mark is the
/// exception by kind, not by taste: it is a POINT, and the point is always round
/// (docs/visual-design.md, Form) — Working alone excepted again, which is not an SVG at all but
/// the agent's own cube (`cube`, `attention`'s Working case), the one mark this module shares
/// with the transcript rather than drawing for itself.
///
/// A mark is a BOX and a WEIGHT, and the two are not independent: see `Box` and `Weight`.
module Icon =

    /// The grid every path below is drawn on, in user units. One square, so the sizes of an
    /// icon can never drift from each other.
    let private grid = 16.0

    /// A box a mark is drawn into: the Tailwind utility AND the pixels it resolves to, as one
    /// value, because what reaches the screen as a stroke is computed FROM those pixels
    /// (`Weight`) and a class string on its own cannot say how wide the mark will be.
    type private Box = { Cls: string; Px: float }

    /// 14px: a mark inside a 24px icon button, or hanging in a column's gutter.
    let private box14 = { Cls = "w-3.5 h-3.5"; Px = 14.0 }
    /// 12px: where a mark rides a caps-label line, inline with the word it belongs to.
    let private box12 = { Cls = "w-3 h-3"; Px = 12.0 }

    /// The stroke weights, in SCREEN PIXELS, and the type each one is matched to
    /// (docs/visual-design.md, Form: a stroke weight matched to the type it sits beside).
    ///
    /// Screen pixels and not grid units, because a stroke written in grid units is SCALED by
    /// the box it is drawn in. The one `1.5` this file used to carry painted 1.31px in a 14px
    /// box and 1.13px in a 12px one, so every mark riding a caps word was a sixth lighter than
    /// the chrome's marks beside it and lighter than the word's own stems — two weights from
    /// one number, and neither of them chosen. Each weight below is written as the grid stroke
    /// it has always had in the 14px box, so the chrome does not move and the 12px marks join
    /// it.
    module private Weight =
        /// The chrome's marks, matched to the caps voice (`text-label`, semibold) and to the
        /// `text-small` lines beside it. The weight every mark wears unless it rides display
        /// type, whatever box it is drawn in.
        let chrome = 1.5 * 14.0 / grid
        /// A mark that rides DISPLAY type, whose word is set extralight and would be shouted
        /// down by a UI-weight stroke: the marks of an act's cause (`chained`).
        let display = 1.25 * 14.0 / grid
        /// Not a segment weight at all: an ellipsis is drawn as zero-length subpaths, so what
        /// this sets is the SIZE OF A CAP. At the chrome's weight those caps are the smallest
        /// mark in the file and read as dirt on the screen rather than as a control.
        let dot = 2.5 * 14.0 / grid

    /// A stroked path: a weight in screen pixels, a box, and whatever the SITE adds to say how
    /// the mark sits in its line. `units` is the square the path is drawn on — `grid` for
    /// everything but the chain's head, which is measured in its own pixels (see `chained`).
    let private strokedOn (units: float) (weight: float) (box: Box) (extra: string) (d: string) =
        let painted = sprintf "%g" (weight * units / box.Px)
        let viewBox = sprintf "0 0 %g %g" units units
        html
            $"""<svg class="{box.Cls} {extra}" viewBox="{viewBox}" fill="none" stroke="currentColor" stroke-width="{painted}"
                     stroke-linecap="square" stroke-linejoin="miter" aria-hidden="true" focusable="false"><path d="{d}"></path></svg>"""

    /// A mark that stands on its own — in a control, or hanging in a column's gutter. `block`,
    /// so it never inherits a text baseline gap inside a grid-centred button.
    let private stroked (d: string) = strokedOn grid Weight.chrome box14 "block" d

    /// A mark that rides a word: the small box, nudged onto the caps baseline beside it. The
    /// nudge is measured inside the box and so is untouched by the weight the box now paints.
    let private strokedInline (nudge: string) (d: string) =
        strokedOn grid Weight.chrome box12 ("inline-block " + nudge) d

    // --- Paths (one place, so the two sizes of an icon can never drift) ---------------------

    let private closePath = "M4 4 L12 12 M12 4 L4 12"
    let private upPath = "M8 12.5 L8 4 M4.25 7.75 L8 4 L11.75 7.75"
    let private downPath = "M8 3.5 L8 12 M4.25 8.25 L8 12 L11.75 8.25"
    /// Make one more: the sheet's New terminal.
    let private plusPath = "M8 3.5 L8 12.5 M3.5 8 L12.5 8"
    /// The head of a cause's mark alone, tip at the foot of the box. The corner that opens a
    /// chain and the links that continue it both draw their line as one-pixel boxes on the
    /// gutter's centre, so no stem here can sit a sub-pixel off it.
    /// The chain's head, in the 14px of its own box (`chained`): tip at 7.5, the middle of the
    /// line's pixel column.
    let private chainedPath = "M5.75 11.8125 L7.5 13.5625 L9.25 11.8125"
    let private leftPath = "M9.75 3.5 L5.25 8 L9.75 12.5"
    let private rightPath = "M6.25 3.5 L10.75 8 L6.25 12.5"
    /// Two chevrons, one behind the other: the mark of a disclosure with SEVERAL things
    /// inside — a turn's tool calls — where one chevron marks a disclosure over one.
    let private rightsPath = "M3.75 3.5 L8.25 8 L3.75 12.5 M8.25 3.5 L12.75 8 L8.25 12.5"
    // Taller and narrower than the chrome chevrons: a mark set to the proportions of the
    // extralight lowercase word it travels with, not to a 24px button.
    let private checkPath = "M3.5 8.25 L6.5 11.25 L12.5 5.25"
    let private crossPath = closePath
    let private sendPath = "M2.5 8 L12.5 8 M8.75 4.25 L12.5 8 L8.75 11.75"
    // Transport and container marks, drawn to the same rule as everything above: straight
    // segments only, each saying its meaning in the geometry this vocabulary actually has.
    //
    /// Play, as an outline — a closed terminal is a recording, and this is the mark that
    /// says so. Never filled, because nothing in this set is.
    let private playPath = "M5.75 3.5 L12 8 L5.75 12.5 Z"
    /// Stop, in the one shape that has always meant it.
    let private stopPath = "M4 4 L12 4 L12 12 L4 12 Z"
    /// A list: three rules, the mark of the door to every terminal and file.
    let private listPath = "M2.5 4.5 L13.5 4.5 M2.5 8 L13.5 8 M2.5 11.5 L13.5 11.5"
    /// Back through what happened: two chevrons pointing behind, the `rights` mark turned round.
    let private rewindPath = "M8.25 3.5 L3.75 8 L8.25 12.5 M12.75 3.5 L8.25 8 L12.75 12.5"
    /// A keyboard: a frame, a row of keys as zero-length subpaths (the dot trick `morePath`
    /// uses), and the space bar.
    let private keyboardPath = "M1.5 4 L14.5 4 L14.5 12 L1.5 12 Z M4.5 7 L4.5 7 M7 7 L7 7 M9.5 7 L9.5 7 M12 7 L12 7 M5 9.5 L11 9.5"
    /// A terminal on a stack: a screen with a prompt and a cursor on it, and one more layer
    /// showing at its right and bottom edges, because the column it opens holds files as
    /// well as terminals. The door to that column (`View.contentReopen`), where the word
    /// "terminals" was. The layer stands 3 units off the screen, which leaves a stroke's
    /// width of ground between them in the 14px box.
    let private contentPath =
        "M1.5 1.5 L11.5 1.5 L11.5 10.5 L1.5 10.5 Z M4 4.25 L5.75 6 L4 7.75 M7 7.75 L9 7.75 "
        + "M14.5 4.5 L14.5 13.5 L4.5 13.5"
    /// A caret pointing down: this opens something beneath it (the pane head's switcher).
    let private caretPath = "M4 6.25 L8 10.25 L12 6.25"
    /// An archive box: a lid across the top, the body under it, and a notch in the middle of
    /// the lid for the hand. Deliberately NOT a downward arrow or a tray with one — this set
    /// already spends arrows on direction, and an arrow here would read as "download".
    let private archivePath =
        "M2.5 3.5 L13.5 3.5 L13.5 6.5 L2.5 6.5 Z M3.5 6.5 L3.5 12.5 L12.5 12.5 L12.5 6.5 M6.5 9 L9.5 9"

    /// An ellipsis: three dots, drawn as ZERO-LENGTH subpaths. This set has no fills, so a
    /// filled circle would be its first — and a stroked ring at this size is a doughnut, not
    /// a dot. A subpath of no length paints its cap, which under this vocabulary's square
    /// caps is exactly a square dot, at whatever weight the icon is drawn.
    /// Three dots, stacked. VERTICAL rather than the row this was: a horizontal ellipsis
    /// sitting beside the first line of a message reads as text that trailed off, and the
    /// eye tries to finish the sentence. Turned upright it is unmistakably a control.
    let private morePath = "M8 3.5 L8 3.5 M8 8 L8 8 M8 12.5 L8 12.5"
    /// Two sheets, one behind the other: the front square whole, the back one drawn as the
    /// L it shows past the front. Straight segments only, like everything here — the
    /// familiar glyph has rounded corners and this vocabulary has none.
    let private copyPath = "M6 6 L13 6 L13 13 L6 13 Z M3 10 L3 3 L10 3"
    /// A repository: a closed book seen from the spine — the cover, and the spine's own
    /// line down its left. The mark a repo wears wherever a sentence points at one
    /// (`Entity.render`), so a reader learns one shape for "this is a repository" and the
    /// host it lives on is the link's to say.
    let private repoPath = "M4 2.5 L12.5 2.5 L12.5 13.5 L4 13.5 Z M6.5 2.5 L6.5 13.5"
    /// A connection this client has no brand mark for: a key, since what a connection IS
    /// is a credential somebody signed in for. The bow a square (no curves here), the bit
    /// two teeth.
    let private keyPath = "M2.5 9.5 L6.5 5.5 L10.5 9.5 L6.5 13.5 Z M8.5 7.5 L13.5 2.5 M11.5 4.5 L13 6"
    /// A sandbox: a box, drawn as the three faces its edges show — a confined place work
    /// runs in. The mark a sandbox wears wherever a sentence points at one.
    let private sandboxPath = "M2.5 5.5 L8 2.5 L13.5 5.5 L13.5 11 L8 14 L2.5 11 Z M2.5 5.5 L8 8.5 L13.5 5.5 M8 8.5 L8 14"
    /// An MCP server: two rack units stacked, each with its status light as a zero-length
    /// subpath (the dot trick `morePath` uses). The mark a server wears wherever a sentence
    /// points at one.
    let private serverPath = "M2.5 2.5 L13.5 2.5 L13.5 7.5 L2.5 7.5 Z M2.5 8.5 L13.5 8.5 L13.5 13.5 L2.5 13.5 Z M5 5 L5 5 M5 11 L5 11"
    /// A pull request: a branch line with a commit square at each end, and a second line
    /// bending into it — a change asking to join. The mark a PR wears wherever a sentence
    /// points at one.
    let private prPath = "M4.5 3.5 L4.5 12.5 M3 2 L6 2 L6 5 L3 5 Z M3 11 L6 11 L6 14 L3 14 Z M11.5 7 L11.5 12.5 M10 11 L13 11 L13 14 L10 14 Z M8 4.5 L11.5 4.5 L11.5 7"
    /// A picture: a frame, two ridges rising to its bottom edge, and a sun as a zero-length
    /// subpath (the dot trick `morePath` uses — this set has no fills). The mark an artifact
    /// wears when the pane can SHOW it, which is the fact a reader wants off the chip: a
    /// thumbnail's worth of promise that tapping it opens something to look at.
    let private imagePath = "M2.5 3.5 L13.5 3.5 L13.5 12.5 L2.5 12.5 Z M2.5 11 L6 7.5 L9.5 11 M7.5 11 L10 8.5 L13.5 12 M11 6 L11 6"
    /// A sheet with its corner turned: an artifact this build does not draw, which is a
    /// download. Deliberately not a downward arrow — the arrow is the CONTROL's to wear, and
    /// this is a statement about what the thing is.
    let private filePath = "M4 2.5 L9.5 2.5 L12 5 L12 13.5 L4 13.5 Z M9.5 2.5 L9.5 5 L12 5"
    /// What a reply answers: a line that comes down from where the parent was said and turns
    /// back, with the head on the turn. It was the text glyph `↩`, which is a CURVE — a shape
    /// this vocabulary does not have — carried by no face this build ships, so it rendered as
    /// whatever the reader's system substituted for it (docs/visual-design.md, Form, and
    /// principle 5: a glyph where an SVG mark exists is the second way).
    let private replyPath = "M12.5 3.5 L12.5 9.5 L4 9.5 M7.5 6 L4 9.5 L7.5 13"

    // --- The vocabulary ----------------------------------------------------------------------
    // One mark per job. `box14` inside a 24px icon button or hanging in a gutter; `box12`
    // where a mark rides a caps-label line. Every one of them paints `Weight.chrome` unless
    // its own comment says which other type it is matched to.

    let close = stroked closePath
    /// Downward and upward, on ONE axis: move this item that way in an order
    /// (`Style.btnIconBare` on a queued act), or go that way to the end of a list (the
    /// timeline's jump-to-latest). An arrow with a stem is the axis; a disclosure is a
    /// chevron and wears `caret` or `right`/`rights` instead, so neither arrow ever means
    /// "this opens".
    let up = stroked upPath
    let down = stroked downPath
    let plus = stroked plusPath
    /// A lighter stroke than the chevrons': a cause's marks say how acts are linked, and
    /// recede behind the controls beside them — `Weight.display`, because the word they
    /// travel with is set extralight.
    ///
    /// Drawn in its own pixels rather than on the shared grid, because where its tip lands is
    /// measured in pixels: it hangs from a one-pixel line 7px into the same box
    /// (`Style.causeCornerTurn`), so the tip is at 7.5, the middle of that column. On the
    /// shared grid it was at 8 of 16 — 7px, the box's middle, which is the edge between two
    /// pixels, while the line paints on one of them.
    let chained = strokedOn 14.0 Weight.display box14 "block" chainedPath
    let left = stroked leftPath
    /// A disclosure over ONE thing — an act's particulars, a tool call's input and output.
    /// `rights` is the disclosure over SEVERAL, and nothing in the product opens a fold with
    /// any other mark.
    let right = stroked rightPath
    let rights = stroked rightsPath
    let send = stroked sendPath
    /// Put what this sits on onto the clipboard, and — the same control, once it has — the
    /// check that says it went. Button-sized: `checkSm` below is the inline mark that rides
    /// a status word, and a control's glyph is never the one a sentence wears.
    let copy = stroked copyPath
    let check = stroked checkPath
    /// What else can be done to the thing this sits on. `Weight.dot`, because the dots are
    /// square CAPS rather than segments and the chrome's weight makes them the smallest mark
    /// in the file, read as dirt on the screen rather than as a control.
    let more = strokedOn grid Weight.dot box14 "block" morePath

    /// Stop: the interrupt beside a running turn. It was also the terminal list's kill, and
    /// a square beside a command reads as stopping the command, not ending the terminal —
    /// the list says its verbs as words now (`Dom.Text.kill`, `reattach`).
    let stop = stroked stopPath
    /// A terminal's acts in the pane's toolbar: taking the keyboard, going back through its
    /// recording, and playing a closed one's.
    let keyboard = stroked keyboardPath
    let content = stroked contentPath
    let list = stroked listPath
    let rewind = stroked rewindPath
    let play = stroked playPath
    /// The mark of a thing that opens BENEATH its control: the switcher's door before the
    /// head's name (P2-2), a select's own caret where `appearance-none` took the native one
    /// away, and the content list's closed-terminals fold. The small box, because every one
    /// of those sits on a caps line.
    let caret = strokedOn grid Weight.chrome box12 "block shrink-0" caretPath
    /// `right` in `caret`'s small box: the disclosure to a page of its own, riding a line of
    /// small text beside `caret` on the same control (`Style.startInLink`), so the two
    /// chevrons a screen chooses between are one size.
    let rightSm = strokedOn grid Weight.chrome box12 "block shrink-0" rightPath
    /// Retire this session from the working list. A row-riding verb that needs no word: it is
    /// rendered only on a row that can be archived, and an archived row wears the word
    /// `Unarchive` instead.
    let archive = stroked archivePath

    /// Inline with a status word: smaller, and nudged onto the caps baseline. `-3px`, not
    /// `-1px`: the strokes sit inside the 12px box with ~3px of clear space under them (the
    /// check's lowest vertex lands at y≈8.4 of 12), so at -1px the paint floated a couple of
    /// pixels above the digits beside it — measured against the caps line in a tally. The
    /// nudge is a position inside the box, so the weight the box paints does not move it.
    let checkSm = strokedInline "align-[-3px]" checkPath
    let crossSm = strokedInline "align-[-3px]" crossPath
    /// The mark a row wears when what it holds is a RECORDING rather than a terminal you
    /// can type into. Sits on the caps baseline beside the row's state, like the two above.
    let playSm = strokedInline "align-[-1px]" playPath
    /// The stop, at the size a mark rides a small line: what a turn's stop signpost wears in
    /// the slot a chip's prompt glyph takes.
    let stopSm = strokedInline "align-[-2px]" stopPath
    /// What a detached reply answers (`Style.replyRefMark`), hanging in the gutter left of the
    /// column's rail. The small box, because the quote it leads is set in `text-small`.
    let replySm = strokedInline "align-[-2px]" replyPath
    /// The direction a column is sorted in, riding its caps head (`Style.sortHeader`): the
    /// same two arrows as `up`/`down` above, in the box a mark takes beside a word. One axis,
    /// one pair of marks — a sort that pointed with its own glyphs would be the second way.
    let upSm = strokedInline "align-[-2px]" upPath
    let downSm = strokedInline "align-[-2px]" downPath
    /// The repository mark at the size an entity wears inline in a sentence: the same box
    /// as a person's small avatar, so a repo and a person referenced side by side sit on one
    /// line at one height.
    let repoSm = stroked repoPath
    let keySm = stroked keyPath
    let sandboxSm = stroked sandboxPath
    let serverSm = stroked serverPath
    let prSm = stroked prPath
    let imageSm = stroked imagePath
    let fileSm = stroked filePath

    /// The mark a piece of content wears wherever it is NAMED — a chip in a message, a row in
    /// the content list, a tab in the strip. One function rather than the same two-armed match
    /// written at each surface: what the mark promises is what the pane will do with it
    /// (a picture opens, anything else downloads), and three copies of that rule are three
    /// chances for a chip to promise a view the pane refuses to give.
    let ofContent (kind: ContentKind) =
        match kind with
        | ContentKind.Image _ -> imageSm
        | ContentKind.Download -> fileSm

    /// What a cube turns: the tip and its six faces (`.agent-think` in `app/tailwind.css` says
    /// how). The caller supplies the box it turns in and the motion it plays — the transcript's
    /// own (`Style.agentThinking`, `.agentThinkingStart`) or the attention mark's own
    /// (`Style.attentionCube`) — because what the mark is on is the caller's to say and so is
    /// its hook. One cube, because the agent at work is one claim wherever it is read: moved
    /// here from `View.thinkingCube` now that Working's attention mark needs the very same one.
    let cube : TemplateResult =
        html
            $"""<span class="{Style.agentThinkingTip}"><span class="{Style.agentThinkingCube}"><i></i><i></i><i></i><i></i><i></i><i></i></span></span>"""

    /// How much something wants a person (docs/visual-design.md, Attention). A POINT, not an
    /// icon, so it is round and may be filled — Working alone excepted, which is not drawn to
    /// this rule at all. One rule ranks the five: attention is INK, and each level carries more
    /// of it than the one below, so they read in order in greyscale and under a blur. One
    /// 12-unit box, centre (6,6), and the chrome's 1.5px stroke:
    ///
    /// - Rest: a point, radius 1.75.
    /// - Absent: an empty ring, radius 4.25: a seat with nobody in it.
    /// - Pending: the same turning quarter arc that marks "still going" everywhere else
    ///   (`Icon.spin`, `Style.statusSpinCw`) — a machine's work, needs nobody, so it wears the
    ///   one mark every other wait wears rather than a shape of its own (`Style.attentionInk`).
    /// - Working: the agent's own cube (`cube` above), turning in place — the exact mark that
    ///   turns inline in the transcript while the agent thinks, now ranked among a session's
    ///   other standings too, because an agent at work is one claim however it is read. Not a
    ///   point and not `currentColor`: it keeps its own blue always (`--color-agent`), the one
    ///   exception the box and ink above do not reach.
    /// - Wanted: a disc, radius 4, with a halo that breathes out three times on arrival.
    /// - Wrong: a disc, radius 3, in a ring, radius 5.25: the most ink, the whole box.
    ///
    /// The rings that stand alone (Absent, Wanted, Wrong) start at twelve o'clock
    /// (`rotate(-90 6 6)`), so drawing one round sets off from the top; Pending's arc is the
    /// shared spin mark exactly as it is everywhere else, cap and all, so it keeps that mark's
    /// own rest position instead. Motion is `Style`'s (`statusSpinCw` for Pending), and the
    /// cube's own (`app/tailwind.css`'s `think-*` keyframes) for Working.
    /// `data-attention` spells the level for a test (`Attention.name`). `aria-hidden`: the word
    /// beside it, or the name of the control it sits in, says the state.
    let attention (level: Yession.Domain.Standings.Attention) =
        let box = Style.attentionMark level
        let name = Yession.Domain.Standings.Attention.name level
        match level with
        | Yession.Domain.Standings.Rest ->
            html
                $"""<svg class="{box}" data-attention="{name}" viewBox="0 0 12 12" aria-hidden="true" focusable="false"><circle cx="6" cy="6" r="1.75" fill="currentColor"></circle></svg>"""
        | Yession.Domain.Standings.Absent ->
            html
                $"""<svg class="{box}" data-attention="{name}" viewBox="0 0 12 12" aria-hidden="true" focusable="false"><circle cx="6" cy="6" r="4.25" fill="none" stroke="currentColor" stroke-width="1.5"></circle></svg>"""
        | Yession.Domain.Standings.Pending ->
            html
                $"""<svg class="{box}" data-attention="{name}" viewBox="0 0 12 12" aria-hidden="true" focusable="false"><circle class="{Style.statusSpinCw}" cx="6" cy="6" r="4.25" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-dasharray="6.68 20.02"></circle></svg>"""
        | Yession.Domain.Standings.Working ->
            html
                $"""<span class="{Style.attentionCube}" data-attention="{name}" aria-hidden="true">{cube}</span>"""
        | Yession.Domain.Standings.Wanted ->
            html
                $"""<svg class="{box}" data-attention="{name}" viewBox="0 0 12 12" aria-hidden="true" focusable="false"><circle class="{Style.markHalo}" cx="6" cy="6" r="4" fill="none" stroke="currentColor" stroke-width="1"></circle><g transform="rotate(-90 6 6)"><circle class="{Style.markClose}" cx="6" cy="6" r="4.25" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-dasharray="26.7"></circle></g><circle class="{Style.markFill}" cx="6" cy="6" r="4" fill="currentColor"></circle></svg>"""
        | Yession.Domain.Standings.Wrong ->
            html
                $"""<svg class="{box}" data-attention="{name}" viewBox="0 0 12 12" aria-hidden="true" focusable="false"><g transform="rotate(-90 6 6)"><circle class="{Style.markDraw}" cx="6" cy="6" r="5.25" fill="none" stroke="currentColor" stroke-width="1.5" stroke-dasharray="32.99"></circle></g><circle class="{Style.markPop}" cx="6" cy="6" r="3" fill="currentColor"></circle></svg>"""

    /// Still going: one arc, a quarter of a 12-unit ring, turning. One shape for every status
    /// that says "not settled yet" — a command live now and a wait under way alike
    /// (`Style.statusSpin`, which used to be two different beating dots, `statusDotLive` and
    /// `statusDotBeat`, then a pair of arcs passing each other), and Pending's own attention
    /// mark (`Icon.attention`) wears the very same arc, because a machine's work is the same
    /// "still going" as any other — the one exception is the ink: a status stays `ink-dim`
    /// always, so the word beside it (`Style.statusOk`'s green, `Style.statusWait`'s dim ink)
    /// is what still says whether what is going is live or a wait, while Pending's copy takes
    /// whatever ink `Style.attentionInk` gives its level. A quarter rather than a half or the
    /// whole ring, so standing still (`motion-reduce`) it still reads as a broken ring, not one
    /// Absent's own shape could be mistaken for.
    let spinPath (extra: string) =
        html
            $"""<svg class="{extra}" viewBox="0 0 12 12" aria-hidden="true" focusable="false"><circle class="{Style.statusSpinCw}" cx="6" cy="6" r="4.25" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-dasharray="6.68 20.02"></circle></svg>"""
    /// Standing alone, where no word follows on the same line.
    let spin = spinPath Style.statusSpin
    /// Leading the status word it belongs to, as `statusDot`'s own lead does for a plain dot.
    let spinLead = spinPath Style.statusSpinLead

    /// GitHub's mark, as GitHub draws it: the one FILLED shape in this file that is not a
    /// point, and the one exception to its vocabulary, because a brand's mark is not ours to redraw in strokes
    /// — a reader knows this shape, and a Metro-stroked octocat would be a different thing
    /// wearing its name. `currentColor`, so it takes the ink of the name beside it. The
    /// mark a `github` connection wears wherever a sentence points at one. `box14.Cls`, like
    /// every other mark, so the one box in this file is written in one place.
    let githubSm =
        html
            $"""<svg class="{box14.Cls} block" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true" focusable="false"><path d="M12 .297c-6.63 0-12 5.373-12 12 0 5.303 3.438 9.8 8.205 11.385.6.113.82-.258.82-.577 0-.285-.01-1.04-.015-2.04-3.338.724-4.042-1.61-4.042-1.61C4.422 18.07 3.633 17.7 3.633 17.7c-1.087-.744.084-.729.084-.729 1.205.084 1.838 1.236 1.838 1.236 1.07 1.835 2.809 1.305 3.495.998.108-.776.417-1.305.76-1.605-2.665-.3-5.466-1.332-5.466-5.93 0-1.31.465-2.38 1.235-3.22-.135-.303-.54-1.523.105-3.176 0 0 1.005-.322 3.3 1.23.96-.267 1.98-.399 3-.405 1.02.006 2.04.138 3 .405 2.28-1.552 3.285-1.23 3.285-1.23.645 1.653.24 2.873.12 3.176.765.84 1.23 1.91 1.23 3.22 0 4.61-2.805 5.625-5.475 5.92.42.36.81 1.096.81 2.22 0 1.606-.015 2.896-.015 3.286 0 .315.21.69.825.57C20.565 22.092 24 17.592 24 12.297c0-6.627-5.373-12-12-12"></path></svg>"""
