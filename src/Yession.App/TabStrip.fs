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
