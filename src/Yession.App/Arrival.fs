namespace Yession.App

open Browser.Types
open Fable.BrowserExtras

/// The conversation arriving: the first time a page load shows it, the rows a reader can see
/// rise into place one lane after another, top to bottom, instead of the whole screen
/// appearing in one frame after a moment of nothing. What moves, and at what pace, is the
/// stylesheet's (`[data-arrive]` in `app/tailwind.css`); this decides only WHICH rows, and in
/// which lane.
///
/// Presentation, not the model's: where a row is laid out is a fact about the page, and only
/// the page can say which rows the reader can see. WHEN is the model's
/// (`ClientModel.showsConversation`), and the page plays it once (`Render`).
module Arrival =

    /// How many lanes a screen fills in. A row past the last lane shares it, so a screen has
    /// settled one pace after the last lane starts whatever is on it.
    let lanes = 4

    /// The lane each row rises in, given the view's extent and each row's, as `(top, bottom)`
    /// on one axis. The rows in view take a lane each, in order; a row out of view takes none,
    /// because nobody would see it move, and on a long conversation that is almost every row.
    /// A row with no height is not in view.
    let laneOf (viewTop: float, viewBottom: float) (rows: (float * float) list) : int option list =
        rows
        |> List.mapFold
            (fun next (top, bottom) ->
                if bottom > top && bottom > viewTop && top < viewBottom then Some (min next (lanes - 1)), next + 1
                else None, next)
            0
        |> fst

    /// Rise the rows of the conversation that are in view, each in its lane (`laneOf`), by the
    /// hook the stylesheet keys the motion off (`Dom.Hooks.arrive`). The hook comes off again
    /// when the row's own rise ends — not a rise under it, which bubbles the same event — so a
    /// row the renderer later moves does not rise a second time. Not at all for a reader who
    /// has asked for less motion, who sees the rows simply there.
    let play (conversation: Element) : unit =
        if not (mediaMatches "(prefers-reduced-motion: reduce)") then
            let view = conversation.getBoundingClientRect ()
            let children = conversation.children
            let rows = [ for i in 0 .. children.length - 1 -> children.[i] ]
            let extents =
                rows
                |> List.map (fun row ->
                    let box = row.getBoundingClientRect ()
                    box.top, box.bottom)
            List.zip rows (laneOf (view.top, view.bottom) extents)
            |> List.iter (fun (row, lane) ->
                match lane with
                | Some lane ->
                    row.addEventListener (
                        "animationend",
                        fun event ->
                            match EventTargets.asElement event.target with
                            | Some target when obj.ReferenceEquals (target, row) -> row.removeAttribute Dom.Hooks.arrive
                            | Some _ | None -> ()
                    )
                    row.setAttribute (Dom.Hooks.arrive, string lane)
                | None -> ())
