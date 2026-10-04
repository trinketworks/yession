namespace Yession.App.Codecs

open Yession.Codecs

#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

/// What one browser had open in one session's side pane, kept across a reload (P0-4): the
/// strip's tabs in order, which of them were kept, which was on top, and whether the column
/// was open. View state, never synced — what one person has open is not what another is
/// working on — so it is written to this browser's own storage and read back by nobody else.
///
/// Tabs by `PaneTab.key` rather than by value. The key is the tab's identity everywhere else
/// (the strip's hook, the pin set), and a stretch tab cannot be rebuilt from anything but the
/// projection it was drawn from, so a key is the one form every kind of tab can be written in.
/// The read in which a tab was showing (`TabMode`) is not kept: a reload comes back to the
/// tab's text, and a recording is one press away.
///
/// Nothing here is the press in flight (`Opening`, `KillPending`): a reload abandons the hand
/// that made it, and an answer arriving after it is somebody else's terminal like any other.
///
/// Changing the shape: a stored value outlives the build that wrote it, so a later shape must
/// still read an older one rather than refuse it. Thoth's object decoder ignores a field it is
/// not asked for, which is the whole migration for a field that goes away — when pins are
/// retired, `pinned` stops being read and the values already stored keep parsing. A field that
/// is ADDED must be optional for the same reason.
[<RequireQualifiedAccess>]
type PaneMemory =
    { /// The strip, left to right — what `ClientModel.Tabs` held.
      Tabs : string list
      /// Which of those were kept — what `ClientModel.Pinned` held.
      Pinned : string list
      /// The tab the pane was about, if one had been chosen.
      Selected : string option
      /// Whether the column was on screen.
      Open : bool }

module PaneMemory =

    /// A pane nothing has happened to: no tabs, nothing kept or chosen, the column shut —
    /// what a freshly loaded client shows, and so what there is no need to write down.
    let untouched : PaneMemory =
        { PaneMemory.Tabs = []
          PaneMemory.Pinned = []
          PaneMemory.Selected = None
          PaneMemory.Open = false }

    let codec : Codec<PaneMemory> =
        { Encode =
            fun (memory: PaneMemory) ->
                Encode.object
                    [ "tabs", memory.Tabs |> List.map Encode.string |> Encode.list
                      "pinned", memory.Pinned |> List.map Encode.string |> Encode.list
                      "selected", memory.Selected |> Option.map Encode.string |> Option.defaultValue Encode.nil
                      "open", Encode.bool memory.Open ]
          Decode =
            Decode.object (fun get ->
                { PaneMemory.Tabs = get.Required.Field "tabs" (Decode.list Decode.string)
                  PaneMemory.Pinned = get.Required.Field "pinned" (Decode.list Decode.string)
                  // Absent and null are both "nothing was chosen", which is a real answer:
                  // the pane then shows its default, exactly as it would have before.
                  PaneMemory.Selected = get.Optional.Field "selected" Decode.string
                  PaneMemory.Open = get.Required.Field "open" Decode.bool }) }
