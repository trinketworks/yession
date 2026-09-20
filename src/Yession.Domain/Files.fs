namespace Yession.Domain.Files

// The file verbs' pure half: what `read_file` answers with. A decision over strings —
// where a window falls, what a page says about the whole — and it sits here, over ordinary
// values, so the cheap tier reaches every case of it. Fetching the bytes is the session
// process's business (`SessionFiles`), because only it can reach inside a sandbox.
//
// Why this exists at all: the agent used to read with `sed -n '2020,2100p'` in a terminal.
// That was on the record — and nothing on the record said WHICH file was read, because a
// command line is text and a reader would have to parse it. A typed read puts the path on
// the record as a fact, and answers the model the way its own read tool does.

open System

/// A window of a file's lines, as `read_file` answers — numbered, bounded, and saying what
/// it is a window OF so a model told the text cannot mistake a page for the whole.
[<RequireQualifiedAccess>]
type FileSlice =
    { /// The first line shown, 1-based.
      From : int
      /// The last line shown, 1-based, inclusive. `From - 1` when the window is empty.
      Through : int
      /// How many lines the file has.
      Total : int
      /// The lines, each prefixed with its number. Empty for an empty window.
      Text : string }

module FileSlice =

    /// Lines per answer when the caller names no limit. The same figure the agent CLI's own
    /// read tool uses, which is what the model's habits are tuned to.
    let defaultLimit = 2000

    /// Characters kept of one line. A minified bundle is one line of a megabyte, and a
    /// window that carried it whole would spend the turn's context on a line nobody reads.
    let maxLineChars = 2000

    /// The lines of `content`. A trailing newline ends the last line rather than starting
    /// an empty one, so a file of three lines answers three whether or not it ends in `\n`.
    let lines (content: string) : string list =
        if content = "" then []
        else
            let split = content.Replace("\r\n", "\n").Split '\n' |> List.ofArray
            match List.rev split with
            | "" :: rest -> List.rev rest
            | _ -> split

    /// The window `offset`/`limit` name over `content`. `offset` is the first line to show,
    /// 1-based, and an absent one is the top; `limit` is how many, and an absent one is
    /// `defaultLimit`. Both are clamped rather than refused — an `offset` past the end is
    /// an empty window that still says how long the file is, which is the answer a caller
    /// who guessed wrong needs.
    let ofContent (content: string) (offset: int option) (limit: int option) : FileSlice =
        let all = lines content
        let total = List.length all
        let from = max 1 (defaultArg offset 1)
        let count = max 0 (defaultArg limit defaultLimit)
        let shown =
            all
            |> List.skip (min total (from - 1))
            |> List.truncate count
        let numbered =
            shown
            |> List.mapi (fun i line ->
                let cut =
                    if line.Length > maxLineChars then line.Substring (0, maxLineChars) + "…"
                    else line
                sprintf "%d\t%s" (from + i) cut)
        { FileSlice.From = from
          FileSlice.Through = from + List.length shown - 1
          FileSlice.Total = total
          FileSlice.Text = String.Join ("\n", numbered) }

    /// The bounds, stated on EVERY answer, then the lines. A model told the text alone
    /// cannot tell "this is everything" from "this is the first 2000 lines", and it reads it
    /// as the first — which is how an agent concludes a file ends where the page did.
    let render (path: string) (slice: FileSlice) : string =
        let where =
            if slice.Total = 0 then sprintf "%s is empty" path
            elif slice.Through < slice.From then
                sprintf "%s has %d lines; nothing at line %d" path slice.Total slice.From
            elif slice.Through >= slice.Total then
                sprintf "%s lines %d-%d of %d" path slice.From slice.Through slice.Total
            else
                sprintf
                    "%s lines %d-%d of %d — read on with offset: %d"
                    path
                    slice.From
                    slice.Through
                    slice.Total
                    (slice.Through + 1)
        if slice.Text = "" then where else where + "\n" + slice.Text

