namespace Yession.App.Codecs

open Yession.Codecs
open Yession.Domain

#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

/// What one browser had open in one session's side pane, kept across a reload (P0-4): the
/// strip's terminals in order, which of them the pane was about, and whether the column was
/// open. View state, never synced — what one person has open is not what another is working
/// on — so it is written to this browser's own storage and read back by nobody else.
///
/// A terminal is written as its prose spelling (`ViewRef.said`, `terminal:<id>`), the same
/// string its tab's hook carries. The read the pane was in (`TerminalMode`) is not kept, nor is
/// a preview: a reload comes back to the terminal's text, and a preview was a glance.
///
/// Nothing here is the press in flight (`Opening`, `KillPending`): a reload abandons the hand
/// that made it, and an answer arriving after it is somebody else's terminal like any other.
///
/// Changing the shape: a stored value outlives the build that wrote it, so a later shape must
/// still read an older one rather than refuse it. Thoth's object decoder ignores a field it is
/// not asked for, which is the whole migration for a field that goes away. A field that is
/// ADDED must be optional for the same reason.
///
/// The one migration so far (P2-1): the strip held four kinds of tab and a `pinned` list, and
/// now holds terminals. A stored `pinned` is simply not read. A stored tab or selection that is
/// not a terminal — `block:…`, `stretch:…`, `content:…`, every one of them a preview by today's
/// model — is dropped on the way in rather than refusing the whole memory, because a preview is
/// exactly what a reload does not bring back: the terminals beside it in the same list still do.
[<RequireQualifiedAccess>]
type PaneMemory =
    { /// The strip, left to right — what `ClientModel.Tabs` held.
      Tabs : TerminalId list
      /// The terminal the pane was about, if one had been chosen.
      Selected : TerminalId option
      /// Whether the column was on screen.
      Open : bool }

module PaneMemory =

    /// A pane nothing has happened to: no tabs, nothing chosen, the column shut — what a
    /// freshly loaded client shows, and so what there is no need to write down.
    let untouched : PaneMemory =
        { PaneMemory.Tabs = []
          PaneMemory.Selected = None
          PaneMemory.Open = false }

    let private terminalKey (terminal: TerminalId) : string = ViewRef.said (ViewingTerminal terminal)

    /// A stored key as a terminal, or `None` for one that is not a terminal's — an older
    /// build's preview (see the type's doc), never an error.
    let private terminalOf (key: string) : TerminalId option =
        match ViewRef.read key with
        | Ok (ViewingTerminal terminal) -> Some terminal
        | Ok (ViewingFile _) | Error _ -> None

    let codec : Codec<PaneMemory> =
        { Encode =
            fun (memory: PaneMemory) ->
                Encode.object
                    [ "tabs", memory.Tabs |> List.map (terminalKey >> Encode.string) |> Encode.list
                      "selected", memory.Selected |> Option.map (terminalKey >> Encode.string) |> Option.defaultValue Encode.nil
                      "open", Encode.bool memory.Open ]
          Decode =
            Decode.object (fun get ->
                { PaneMemory.Tabs = get.Required.Field "tabs" (Decode.list Decode.string) |> List.choose terminalOf
                  // Absent and null are both "nothing was chosen", which is a real answer:
                  // the pane then shows its default, exactly as it would have before.
                  PaneMemory.Selected = get.Optional.Field "selected" Decode.string |> Option.bind terminalOf
                  PaneMemory.Open = get.Required.Field "open" Decode.bool }) }
