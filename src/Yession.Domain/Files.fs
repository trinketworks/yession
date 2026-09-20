namespace Yession.Domain.Files

// The file verbs' pure half: what `read_file` answers with, and what `edit_file` does to
// a file's text. Both are decisions over strings — where a window falls, whether a match is
// unique — and they sit here, over ordinary values, so the cheap tier reaches every case.
// Fetching the bytes and putting them back is the session process's business
// (`SessionFiles`), because only it can reach inside a sandbox.
//
// Why these exist at all: the agent used to read with `sed -n '2020,2100p'` and edit with a
// heredoc into `$TMPDIR` followed by `head`/`tail`/`mv`. Every one of those was a shell
// line in a terminal, on the record — and nothing on the record said WHICH file was read or
// what changed in it, because a command line is text and a reader would have to parse it.
// A typed read and a typed edit put the path and the change on the record as facts.

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

/// Why an exact-string edit did not happen. Each is told to the model in its own words,
/// because each asks for a different next move.
[<RequireQualifiedAccess>]
type EditFailure =
    /// `old_string` is not in the file. Re-read before guessing again.
    | NotFound
    /// `old_string` is in the file this many times, and the caller did not say which — or
    /// that it meant all of them.
    | Ambiguous of count: int
    /// `old_string` and `new_string` are the same text.
    | NoChange
    /// An empty `old_string` matches everywhere and nowhere; creating a file is `write_file`.
    | EmptyOld

/// What an edit did: the text as it now stands, and how many places changed.
[<RequireQualifiedAccess>]
type Edited =
    { Content : string
      Replaced : int
      /// Lines the edit took out and put in, over every replacement — what the record's
      /// one-line summary says (`+3 −1`).
      LinesRemoved : int
      LinesAdded : int }

module FileEdit =

    /// Every index at which `needle` starts in `haystack`, non-overlapping, left to right.
    let private occurrences (haystack: string) (needle: string) : int list =
        let rec go (from: int) (acc: int list) =
            match haystack.IndexOf (needle, from, StringComparison.Ordinal) with
            | -1 -> List.rev acc
            | at -> go (at + needle.Length) (at :: acc)
        go 0 []

    /// The lines a piece of text spans when it sits inside a file: one more than its
    /// newlines. `""` spans none — an insertion adds nothing to count.
    let private spanned (text: string) : int =
        if text = "" then 0 else (text.Split '\n').Length

    /// Replace `oldText` with `newText` in `content`: exactly one occurrence unless
    /// `replaceAll`, in which case every one. The agent CLI's own edit tool's semantics,
    /// and deliberately so — the model's habits are tuned to them, and an edit tool that
    /// silently took the first of three matches is the sed footgun with a new name.
    let apply (content: string) (oldText: string) (newText: string) (replaceAll: bool) : Result<Edited, EditFailure> =
        if oldText = "" then Error EditFailure.EmptyOld
        elif oldText = newText then Error EditFailure.NoChange
        else
            match occurrences content oldText with
            | [] -> Error EditFailure.NotFound
            | found when List.length found = 1 || replaceAll ->
                let count = List.length found
                Ok
                    { Edited.Content = content.Replace (oldText, newText)
                      Edited.Replaced = count
                      Edited.LinesRemoved = spanned oldText * count
                      Edited.LinesAdded = spanned newText * count }
            | many -> Error (EditFailure.Ambiguous (List.length many))

    /// The failure, in the words that name the next move.
    let describe (path: string) (failure: EditFailure) : string =
        match failure with
        | EditFailure.NotFound ->
            sprintf "old_string was not found in %s — read the file again before retrying; the text may have moved or differ in whitespace" path
        | EditFailure.Ambiguous count ->
            sprintf "old_string occurs %d times in %s — include more surrounding text so it matches once, or pass replace_all: true to change every one" count path
        | EditFailure.NoChange -> "old_string and new_string are the same, so there is nothing to change"
        | EditFailure.EmptyOld -> "old_string is empty — to create a file or replace it whole, use write_file"

    /// The one line the gate and the record read for an edit, BEFORE anything runs: which
    /// file, and how many lines go out and come in. Counted from the two texts alone,
    /// because the summary is read before the file is — a classifier deciding on it cannot
    /// be shown a count that depends on the outcome.
    let summary (path: string) (oldText: string) (newText: string) (replaceAll: bool) : string =
        sprintf
            "edit_file %s −%d +%d%s"
            path
            (spanned oldText)
            (spanned newText)
            (if replaceAll then " everywhere it matches" else "")

    /// The same line for a whole-file write: which file, and how much of it.
    let writeSummary (path: string) (content: string) : string =
        sprintf "write_file %s (%d lines)" path (List.length (FileSlice.lines content))

/// What `search_files` and `find_files` hand back: the sandbox's own lines — `grep -n`'s
/// `path:line:text`, `find`'s one path per line — bounded, and saying what the bound left
/// out. The lines are the sandbox's because the two tools are the shell's own `grep` and
/// `find` behind a typed door: what changes is that the record says what was searched for,
/// not that the answer is reshaped.
module FileHits =

    /// Lines per answer. A search that matches more than this has a pattern to narrow, and
    /// the answer says so rather than spending the turn's context on the rest.
    let cap = 200

    /// The non-empty lines of `raw`, the first `cap` of them, and a closing line when there
    /// were more. `"…"` alone for nothing — the empty string a model reads as "the tool
    /// said nothing" rather than "nothing matched".
    let render (nothing: string) (raw: string) : string =
        let lines = raw.Replace("\r\n", "\n").Split '\n' |> Array.filter (fun line -> line <> "")
        if lines.Length = 0 then nothing
        elif lines.Length <= cap then String.Join ("\n", lines)
        else
            String.Join ("\n", Array.truncate cap lines)
            + sprintf "\n[%d more not shown — narrow the search]" (lines.Length - cap)
