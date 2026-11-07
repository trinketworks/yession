namespace Yession.App

open Yession.Domain.Link

/// Where carets are TO THE CHARACTER, kept outside the Elmish model.
///
/// The model holds what the view reads, and the view reads a caret's FIELD — whose name a
/// title wears, which composer someone is in, who is writing where — and never its position.
/// The position is read by three things, none of them the view: the rich editors' decoration
/// push, the measurement that places a caret over a plain input (both in `Render`), and the
/// presence frame that tells everyone else (`Client.connect`). A caret moves on every
/// keystroke, and a message is a pass through `update` and a render of the whole page, so a
/// position held in the model cost a page render per key for an answer only those three read.
///
/// So the position lives here, and the model hears only of a move that changes a field
/// (`CaretMovedMsg`). ONE slot for this peer's caret, whoever reports to it — the rich editor's
/// plugin, the title input, the chapter names and the terminal command lines — so whether a
/// report is news is asked of what this peer last said from anywhere: a command line
/// re-reporting `None` after the editor had claimed the caret is a clear somebody needs.
///
/// Not Yjs awareness, for the reason `PresencePayload` gives.
type Carets () =
    let mutable own : Focus option = None

    /// This peer's caret and selection, as its editors last reported them.
    member _.Own : Focus option = own

    /// This peer's caret was reported here. Kept, then told to whichever of two needs it: the
    /// model, through `fieldMoved`, when the caret is in a different field than it was (or in
    /// none); the presence sender, through `positionMoved`, when only where it stands in the
    /// same field changed. Neither when it is where it was — most reports are not news (a keyup
    /// for a key that typed, a click where the caret already was, an editor going by in a
    /// scroll).
    member _.Report (focus: Focus option) (fieldMoved: FocusField option -> unit) (positionMoved: unit -> unit) : unit =
        if focus <> own then
            let before = own |> Option.map (fun f -> f.Field)
            let after = focus |> Option.map (fun f -> f.Field)
            own <- focus
            if after <> before then fieldMoved after else positionMoved ()
