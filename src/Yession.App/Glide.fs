namespace Yession.App

open System.Collections.Generic
open Browser.Types
open Lit
open Fable.BrowserExtras

/// The agent's mark carried from where a turn began to where its first words end.
///
/// Before the first word the mark stands larger, alone in the body, where the reply will begin
/// (`Style.agentThinkingStart`); with the first word it becomes the caret after that word.
/// Those are two elements in two places — one in an empty body, one in the last line of the
/// rendered text — so nothing in a render can move one into the other. What can: remember
/// where the first stood each time it is drawn, and when the second is first drawn, play it in
/// FROM there (a FLIP, on the compositor). Presentation, not the model's: where a box stood is
/// a fact about a layout, and a re-render must not fight it.
///
/// Measured against the message body rather than the viewport, so a timeline that scrolled
/// between the two renders — it does, as the first words lengthen it — glides from where the
/// mark stood in the body and not from wherever that spot is on the screen now.
module Glide =

    /// Where each turn's opening mark last stood in its body: its centre and its painted height.
    let private stood = Dictionary<string, float * float * float> ()

    let private within (element: HTMLElement) : (float * float * float) option =
        match element.closest "[data-message-body]" with
        | None -> None
        | Some body ->
            let r = element.getBoundingClientRect ()
            let b = body.getBoundingClientRect ()
            if r.height > 0. then Some (r.left + r.width / 2. - b.left, r.top + r.height / 2. - b.top, r.height)
            else None

    /// On the opening mark: remember where it stands, each time it is drawn. Measured once the
    /// render has put it on the page: Lit hands a ref its element while the template is still
    /// a fragment, where it has no body to stand in and no box.
    let from (turn: string) : TemplateResult =
        Lit.refCallback (fun (element: HTMLElement option) ->
            match element with
            | Some element ->
                Elements.afterInsertion (fun () ->
                    match within element with
                    | Some at -> stood.[turn] <- at
                    | None -> ())
            | None -> ())

    /// On the caret the first words put down: start it where the opening mark stood and let it
    /// come to rest where it is laid out. Once per turn — the mark is put down once — and not
    /// at all for a reader who has asked for less motion, who sees it simply arrive.
    let into (turn: string) : TemplateResult =
        Lit.refCallback (fun (element: HTMLElement option) ->
            match element, stood.TryGetValue turn with
            | Some element, (true, (x, y, height)) ->
                stood.Remove turn |> ignore
                // Placed, as the opening mark was measured, once it is on the page — and before
                // anything is painted, so no frame shows it already at rest.
                Elements.afterInsertion (fun () ->
                    match within element with
                    | Some (x', y', height') when not (mediaMatches "(prefers-reduced-motion: reduce)") ->
                        let from = sprintf "translate(%.2fpx, %.2fpx) scale(%.4f)" (x - x') (y - y') (height / height')
                        Elements.playFrom element from 520. "cubic-bezier(0.2, 0.8, 0.2, 1)"
                    | Some _ | None -> ())
            | Some _, (false, _) | None, _ -> ())
