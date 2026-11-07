namespace Yession.App

open Yession.Domain
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
/// Everyone else's carets are the same story from the other end: each presence frame lands
/// here (`Receive`), the model hears of it only when where that actor IS changed
/// (`RemotePresenceMsg`), and a move within a field reaches the page through `Changed`, which
/// the render's caret placement listens to, with no render.
///
/// Not Yjs awareness, for the reason `PresencePayload` gives.
type Carets () =
    let mutable own : Focus option = None
    let mutable remote : Map<ActorRef, PresencePayload> = Map.empty
    let changed = Event<unit> ()
    /// What the model holds of a frame: everything but where in its field the caret stands.
    let fieldLevel (payload: PresencePayload) : RemotePresence =
        { DisplayName = payload.DisplayName
          Field = payload.Focus |> Option.map (fun focus -> focus.Field)
          Viewing = payload.Viewing }

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

    /// Where a remote actor's caret and selection stand, as its last presence frame said, or
    /// `None` when it is in no field (or has not said).
    member _.Remote (who: ActorRef) : Focus option =
        remote |> Map.tryFind who |> Option.bind (fun payload -> payload.Focus)

    /// Fires after every presence frame `Receive` keeps, whatever it changed: what draws a
    /// caret where it stands listens here, so a caret that moved within its field is drawn
    /// with no message and no render.
    member _.Changed : IEvent<unit> = changed.Publish

    /// A presence frame arrived — the ONE way one is taken in, by the connection and by the
    /// harness alike. Kept, then told to the model (`RemotePresenceMsg`) only when it changes
    /// what the model holds of that actor: its caret's field, its name or what it has open.
    /// A frame saying nowhere at all (`Focus` and `Viewing` both `None`) forgets the actor
    /// here, and is told, because the model forgets it too. Then `Changed`, so whatever
    /// draws carets draws this one where it now stands.
    member _.Receive (dispatch: ClientMsg -> unit) (payload: PresencePayload) : unit =
        let before = Map.tryFind payload.Who remote |> Option.map fieldLevel
        remote <-
            match payload.Focus, payload.Viewing with
            | None, None -> Map.remove payload.Who remote
            | _ -> Map.add payload.Who payload remote
        let now = fieldLevel payload
        if before <> Some now then dispatch (RemotePresenceMsg (payload.Who, now))
        changed.Trigger ()
