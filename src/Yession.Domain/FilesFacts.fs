namespace Yession.Domain.Files

open Yession.Domain
open Yession.Domain.Sandboxes

/// How a file's text changed through the file verbs (the file verbs): one exact-string
/// edit, or the whole file written.
[<RequireQualifiedAccess>]
type FileChange =
    /// `edit_file`: `old_string` replaced `replaced` times, and what that cost in lines.
    | Edited of replaced: int * linesRemoved: int * linesAdded: int
    /// `write_file`: created or replaced whole, this long.
    | Written of lines: int

/// A file changed through `edit_file` or `write_file` — the act on the timeline.
///
/// The event is what makes the change a FACT on the record rather than a tool call's
/// arguments: a reader (a screen, a digest, a later turn) can say which file, how much, and
/// see the change itself, without parsing a heredoc out of a command line. Recorded by the
/// verb that made the change (`SessionFiles`), after the write landed, so an event here is
/// never a change that did not happen.
[<RequireQualifiedAccess>]
type FileChanged =
    { MessageId : MessageId
      /// Whose file it is: the sandbox the path is meant in.
      Sandbox : SandboxRef
      /// The path as the caller said it — in the sandbox's vocabulary, never a host path.
      Path : string
      Change : FileChange
      /// The change itself, for an edit: the lines that went out and the lines that came
      /// in, in the `-`/`+` shape every reader of a diff knows, bounded (`FileDiff.cap`). A
      /// write carries none — its diff is the whole file, which is not a thing to put on
      /// the log for every save.
      Diff : string option
      Actor : ActorRef }

/// The excerpt an edit leaves on the record.
module FileDiff =

    /// Lines kept of a diff. An edit that replaces more than this is on the record as its
    /// counts and its first lines; the file itself is where the rest is read.
    let cap = 60

    /// `oldText` as `-` lines and `newText` as `+` lines — not a minimal diff, because an
    /// exact-string edit already IS the minimal statement of what changed: the caller said
    /// exactly which text left and exactly which arrived. Computing a line diff over the
    /// two would only re-derive that, and could hide a moved line as an unchanged one.
    let ofReplacement (oldText: string) (newText: string) : string =
        let lines (prefix: string) (text: string) =
            if text = "" then []
            else text.Replace("\r\n", "\n").Split '\n' |> Array.map (fun line -> prefix + line) |> List.ofArray
        let all = lines "-" oldText @ lines "+" newText
        if List.length all <= cap then String.concat "\n" all
        else
            String.concat "\n" (List.truncate cap all) + sprintf "\n… %d more lines" (List.length all - cap)

module FileChanged =

    /// The headline a reader lands on: which file, and how much of it moved.
    let phrase (f: FileChanged) : Phrase =
        let where =
            if f.Sandbox = SandboxRef.defaultRef then "" else sprintf " in %s" (SandboxRef.render f.Sandbox)
        match f.Change with
        | FileChange.Edited (1, removed, added) -> Phrase.text (sprintf "edited %s%s (−%d +%d)" f.Path where removed added)
        | FileChange.Edited (replaced, removed, added) ->
            Phrase.text (sprintf "edited %s%s in %d places (−%d +%d)" f.Path where replaced removed added)
        | FileChange.Written lines -> Phrase.text (sprintf "wrote %s%s (%d lines)" f.Path where lines)
