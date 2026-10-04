namespace Yession.App

/// Where focus goes inside a `role="tablist"`, decided without a DOM.
///
/// ARIA's tabs pattern is half keyboard, and the half a plain row of buttons does not give
/// you is the walk: Tab reaches the strip, Enter presses what it lands on, and Left/Right
/// /Home/End are dead unless something implements them. Declaring `role="tablist"` and
/// leaving them dead is a worse lie than not declaring it at all.
///
/// The walk is arithmetic — a key, where focus is, how many tabs there are — so it is here,
/// where the cheap tier can ask it, rather than inside the handler that performs it. It used
/// to be neither: first a JavaScript program in an `[<Emit>]` string, then the same program
/// in a `.mjs` module beside it, and in both the wrap-around was the whole behaviour and
/// nothing could put a question to it. What is left at the call site is reading the event and
/// moving the focus, which only a browser can do.
///
/// Both answers are an `int option` over the same list the caller measured, and `None` means
/// this key is not part of the pattern — not that nothing happens, because the caller has
/// other keys of its own.
///
/// The strip also SCROLLS, and where it scrolls to is the same kind of question: a tab's
/// span and the window's, measured by the browser (`PaneShell`) and decided here.
module TabStrip =

    /// The index the arrow walk moves focus to, or `None` when this key is not part of it.
    ///
    /// `here` is where focus is now, and `-1` says it is not on a tab at all — which happens
    /// when the strip itself holds focus, and is why either arrow from nowhere lands on the
    /// FIRST tab rather than wrapping to the last. Left and Right wrap: a strip you cannot
    /// walk off the end of is one where the last tab is reachable only by walking the whole
    /// row.
    ///
    /// Moving FOCUS is all this is. Selection follows the Enter or Space the button already
    /// handles — ARIA's "manual activation" variant, and the right one here, because walking
    /// the strip must not mount and unmount a player under the reader on every keypress.
    let walk (key: string) (here: int) (count: int) : int option =
        if count <= 0 then None
        else
            match key with
            | "Home" -> Some 0
            | "End" -> Some (count - 1)
            | "ArrowLeft" | "ArrowRight" when here < 0 -> Some 0
            | "ArrowRight" -> Some ((here + 1) % count)
            | "ArrowLeft" -> Some ((here + count - 1) % count)
            | _ -> None

    /// The tab focus should move to before the focused one is released, or `None` when there
    /// is no such move to make — focus is not on a tab, or releasing this one empties the
    /// strip.
    ///
    /// Moving FIRST is what makes this need no timing assumption at all. Focusing afterwards
    /// is the obvious shape and it does not work: the strip has to be re-rendered first, and
    /// when that happens is the renderer's business. Measured on both attempts —
    /// synchronously, focus landed on the node about to be removed and the browser moved it
    /// to `body`; on `requestAnimationFrame`, a headless browser that paints no frames never
    /// ran the callback at all. Going first has neither problem: the neighbour exists right
    /// now, and a node that keeps focus keeps it across the patch.
    ///
    /// Which neighbour: the one that will be sitting at this index afterwards, except at the
    /// end of the row, where there is nothing after it and focus steps back instead.
    let neighbour (here: int) (count: int) : int option =
        if here < 0 || count < 2 then None else Some (min here (count - 2))

    /// The strip's scroll box as the browser measured it, in pixels: how far it is scrolled,
    /// how wide a window it shows, and how wide what it holds is.
    [<RequireQualifiedAccess>]
    type Scrollport =
        { Scrolled: float
          Shown: float
          Holds: float }

    /// How far inside an edge a revealed tab is kept. The width of the strip's fade
    /// (`Style.terminalTabScroller`, `1.5rem`): a tab "in view" under the fade is a tab half
    /// painted out, so revealing one brings it clear of it. Two spellings of one length, one
    /// in CSS and one here, because a Tailwind class has to be a literal to be generated.
    let edge = 24.0

    /// Where the strip should scroll to so that the tab spanning `start`..`stop` (in the
    /// scroller's own content coordinates) is shown clear of both edges, or `None` when it
    /// already is — so a tab in view is never a write, and a write never fights a reader's
    /// own scroll for nothing.
    ///
    /// The SMALLEST move that shows it: a tab off the right comes in at the right, one off
    /// the left at the left, which is what keeps a walk along the strip from jumping the row
    /// under the reader on every step. A tab wider than the window shows its start, where its
    /// name is.
    let reveal (port: Scrollport) (start: float) (stop: float) : float option =
        let from, upto = start - edge, stop + edge
        let target =
            if from < port.Scrolled then from
            elif upto > port.Scrolled + port.Shown then min from (upto - port.Shown)
            else port.Scrolled
        let furthest = max 0.0 (port.Holds - port.Shown)
        let target = target |> max 0.0 |> min furthest
        if abs (target - port.Scrolled) < 0.5 then None else Some target

    /// Which ends of the strip have tabs past them — what its fade says, and only while it is
    /// true, so a strip scrolled to its end does not fade out the last tab it is showing.
    [<RequireQualifiedAccess>]
    type Hidden =
        | Neither
        | Before
        | After
        | Both

    module Hidden =
        /// The `data-pane-strip-hidden` value: the styles key the fade on it.
        let token (hidden: Hidden) : string =
            match hidden with
            | Hidden.Neither -> "none"
            | Hidden.Before -> "start"
            | Hidden.After -> "end"
            | Hidden.Both -> "both"

    /// A pixel of slack at either end: a scroll offset is fractional and a content width is
    /// rounded, so "exactly at the end" is never exactly anything.
    let hidden (port: Scrollport) : Hidden =
        let before = port.Scrolled > 1.0
        let after = port.Scrolled + port.Shown < port.Holds - 1.0
        match before, after with
        | false, false -> Hidden.Neither
        | true, false -> Hidden.Before
        | false, true -> Hidden.After
        | true, true -> Hidden.Both
