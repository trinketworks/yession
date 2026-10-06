namespace Yession.App.Codecs

open Yession.Codecs
open Yession.Domain
open Yession.Domain.Terminals

#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

/// How many of one terminal's commands have finished, and how many of those did not succeed —
/// what "this person has looked at it" is measured in (`ClientModel.Seen`). Counts rather than
/// the last block's id, because a command in the background can finish after one that started
/// later: the last block in the list is not always the last one to END, and a count of what
/// has ended goes up whichever of them it was.
///
/// Failed is what the terminal's failed mark means: an exit that was not zero, a timeout, a
/// command that never got to exit, or one that was refused.
[<RequireQualifiedAccess>]
type CommandTally =
    { Finished : int
      Failed : int }

module CommandTally =

    /// Nothing finished: a terminal nobody has seen anything of.
    let zero : CommandTally = { CommandTally.Finished = 0; CommandTally.Failed = 0 }

    /// What has finished in this terminal so far.
    let ofView (view: TerminalView) : CommandTally =
        let failed =
            function
            | BlockFinished (CommandFailed _ | CommandTimedOut | CommandExecutionFailed _)
            | BlockRejected _ -> true
            | BlockFinished (CommandSucceeded _)
            | BlockRunning -> false
        { CommandTally.Finished = view.Blocks |> List.filter (fun b -> b.Status <> BlockRunning) |> List.length
          CommandTally.Failed = view.Blocks |> List.filter (fun b -> failed b.Status) |> List.length }

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
///
/// `seen` was added later (an optional field, as above): how far through each terminal's
/// commands this person had looked, so a build that finished while they were away still says
/// so after a reload. A memory written before it reads as nothing seen in particular, which
/// counts everything the log replays as already looked at — what a reload did before.
[<RequireQualifiedAccess>]
type PaneMemory =
    { /// The strip, left to right — what `ClientModel.Tabs` held.
      Tabs : TerminalId list
      /// The terminal the pane was about, if one had been chosen.
      Selected : TerminalId option
      /// Whether the column was on screen.
      Open : bool
      /// How far through each terminal's commands this person had looked (`ClientModel.Seen`).
      Seen : Map<TerminalId, CommandTally> }

module PaneMemory =

    /// A pane nothing has happened to: no tabs, nothing chosen, the column shut — what a
    /// freshly loaded client shows, and so what there is no need to write down.
    let untouched : PaneMemory =
        { PaneMemory.Tabs = []
          PaneMemory.Selected = None
          PaneMemory.Open = false
          PaneMemory.Seen = Map.empty }

    let private terminalKey (terminal: TerminalId) : string = ViewRef.said (ViewingTerminal terminal)

    /// A stored key as a terminal, or `None` for one that is not a terminal's — an older
    /// build's preview (see the type's doc), never an error.
    let private terminalOf (key: string) : TerminalId option =
        match ViewRef.read key with
        | Ok (ViewingTerminal terminal) -> Some terminal
        | Ok (ViewingFile _) | Error _ -> None

    let private tally : Decoder<CommandTally> =
        Decode.object (fun get ->
            { CommandTally.Finished = get.Required.Field "finished" Decode.int
              CommandTally.Failed = get.Required.Field "failed" Decode.int })

    let codec : Codec<PaneMemory> =
        { Encode =
            fun (memory: PaneMemory) ->
                Encode.object
                    [ "tabs", memory.Tabs |> List.map (terminalKey >> Encode.string) |> Encode.list
                      "selected", memory.Selected |> Option.map (terminalKey >> Encode.string) |> Option.defaultValue Encode.nil
                      "open", Encode.bool memory.Open
                      "seen",
                      memory.Seen
                      |> Map.toList
                      |> List.map (fun (terminal, tally) ->
                          terminalKey terminal,
                          Encode.object
                              [ "finished", Encode.int tally.Finished
                                "failed", Encode.int tally.Failed ])
                      |> Encode.object ]
          Decode =
            Decode.object (fun get ->
                { PaneMemory.Tabs = get.Required.Field "tabs" (Decode.list Decode.string) |> List.choose terminalOf
                  // Absent and null are both "nothing was chosen", which is a real answer:
                  // the pane then shows its default, exactly as it would have before.
                  PaneMemory.Selected = get.Optional.Field "selected" Decode.string |> Option.bind terminalOf
                  PaneMemory.Open = get.Required.Field "open" Decode.bool
                  // Absent in a memory written before it was kept: nothing in particular seen.
                  // A key that is not a terminal's is dropped, as a tab's is.
                  PaneMemory.Seen =
                      get.Optional.Field "seen" (Decode.keyValuePairs tally)
                      |> Option.defaultValue []
                      |> List.choose (fun (key, tally) -> terminalOf key |> Option.map (fun terminal -> terminal, tally))
                      |> Map.ofList }) }
