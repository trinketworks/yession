namespace Yession.Session

open Yession.Domain.Repos

#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

// Reading a checkout's `.agents/skills/<directory>/SKILL.md`: the frontmatter alone, into
// `SkillSummary`. PURE and parser-free, on `ConfigFile`'s own terms -- `heads` and
// `frontmatterOf` are text handling, and `decode` reads an already-parsed JSON tree, so all
// three run in the cheap tier on both runtimes from literals. The bridge that asks git for the
// files and calls the YAML parser is the app's (`app/Repos.fs`, `skillsOf`), the same split
// `RepoConfig.fs`/`YamlSource.fs` draw for `yession.yaml`.

[<RequireQualifiedAccess>]
module SkillFile =

    let private fence = "---"

    /// The YAML between a file's first two `---` lines, or `None` when it does not open with
    /// one -- a `SKILL.md` without frontmatter is not a skill this session can menu.
    let frontmatterOf (text: string) : string option =
        let lines = text.Replace("\r\n", "\n").Split '\n' |> Array.toList
        match lines with
        | first :: rest when first.Trim () = fence ->
            rest
            |> List.tryFindIndex (fun line -> line.Trim () = fence)
            |> Option.map (fun closeAt -> rest |> List.take closeAt |> String.concat "\n")
        | _ -> None

    /// The most lines of one `SKILL.md` that the session asks git for. Frontmatter is a name
    /// and a description, so its closing `---` is in the first few lines. A file that closes
    /// it later than this is no skill of the menu.
    let maxLines = 64

    /// The most characters of one `SKILL.md` that the session keeps. The same reason as
    /// `maxLines`, for a file with few and very long lines: one file must not take the place
    /// of the others in the answer.
    let maxFileChars = 8192

    /// The most skills of one checkout that the menu holds. Each one is a line in the context
    /// of every turn, so a checkout cannot make that context as long as it likes.
    let maxSkills = 64

    /// The most characters of git's whole answer that the session reads. It holds `maxSkills`
    /// files of `maxFileChars`, each line with its path and its number in front. git prints a
    /// whole line however long it is, so the reader ends git here.
    let maxAnswerChars = 1_048_576

    /// The first lines of one skill file, and where the file is from the root of the checkout.
    type Head =
        { Path : string
          Text : string }

    /// `.agents/skills/<directory>/SKILL.md`, and no other shape. The pathspec that git is
    /// given asks for the same shape. This is the guard for a path that git printed and the
    /// pathspec did not mean, such as a name with a line end in it.
    let private isSkillPath (path: string) : bool =
        match path.Split '/' with
        | [| ".agents"; "skills"; directory; "SKILL.md" |] -> directory <> ""
        | _ -> false

    /// One line that git printed, as `(path, line number, text)`. git writes
    /// `path NUL number NUL text` with `-z -n`. This drops a line that is not of that shape.
    let private record (line: string) : (string * int * string) option =
        match line.IndexOf '\000' with
        | -1 -> None
        | pathEnd ->
            match line.IndexOf ('\000', pathEnd + 1) with
            | -1 -> None
            | numberEnd ->
                match System.Int32.TryParse (line.Substring (pathEnd + 1, numberEnd - pathEnd - 1)) with
                | true, number when number >= 1 ->
                    // A file with CRLF line ends has the CR at the end of each line that git
                    // prints.
                    let text = line.Substring (numberEnd + 1)
                    let ends = text.Length > 0 && text.[text.Length - 1] = '\r'
                    Some (line.Substring (0, pathEnd), number, (if ends then text.Substring (0, text.Length - 1) else text))
                | _ -> None

    /// The path at the start of a line that git printed, whole or cut: the text before its
    /// first NUL.
    let private pathOf (line: string) : string option =
        match line.IndexOf '\000' with
        | -1 -> None
        | pathEnd -> Some (line.Substring (0, pathEnd))

    /// The files seen so far in git's answer: their order, and for each one its lines (the
    /// last one first) with their size.
    type private Kept =
        { Order : string list
          Lines : Map<string, string list * int> }

    /// The heads of the skill files in what `git grep -z -n` printed for a checkout
    /// (`app/Repos.fs`, `skillsOf`), in the order that git printed them.
    ///
    /// This reads the first `maxAnswerChars` characters of the answer and no more. The reader
    /// stops soon after that many, at the end of a chunk, and the size of a chunk is not the
    /// same at each read. A cut at the same character each time gives the same heads each
    /// time. An answer that is longer than the cap was cut, so the file that the cut is in is
    /// not whole, and this drops that file.
    ///
    /// A last line with no line end is not read. This keeps a file from its line 1 only, up
    /// to `maxFileChars`, and it keeps the first `maxSkills` files.
    let heads (answer: string) : Head list =
        let cut = answer.Length > maxAnswerChars
        let read = if cut then answer.Substring (0, maxAnswerChars) else answer
        let lastEnd = read.LastIndexOf '\n'
        let lines = if lastEnd <= 0 then [||] else (read.Substring (0, lastEnd)).Split '\n'
        // The file of the last line that git began: the line that the cut is in, or the line
        // before it when the cut came at a line end or in a path.
        let cutFile =
            if not cut then
                None
            else
                pathOf (read.Substring (lastEnd + 1))
                |> Option.orElse (lines |> Array.tryLast |> Option.bind pathOf)
        let step (kept: Kept) (line: string) : Kept =
            match record line with
            | Some (path, number, text) when isSkillPath path ->
                match Map.tryFind path kept.Lines with
                // A file that does not start at its line 1 is not a head.
                | None when number = 1 && List.length kept.Order < maxSkills ->
                    { Order = path :: kept.Order
                      Lines = Map.add path ([ text ], text.Length + 1) kept.Lines }
                | None -> kept
                | Some (lines, size) when size + text.Length + 1 <= maxFileChars ->
                    { kept with Lines = Map.add path (text :: lines, size + text.Length + 1) kept.Lines }
                // Full. A later, shorter line must not get in after this left a line out.
                | Some (lines, _) -> { kept with Lines = Map.add path (lines, maxFileChars + 1) kept.Lines }
            | _ -> kept
        let kept = lines |> Array.fold step { Order = []; Lines = Map.empty }
        kept.Order
        |> List.rev
        |> List.filter (fun path -> Some path <> cutFile)
        |> List.map (fun path -> { Path = path; Text = kept.Lines.[path] |> fst |> List.rev |> String.concat "\n" })

    /// Whether git's exit says that the checkout holds no skill file. `git grep` exits 1
    /// when no line matched, and says nothing. A wrapper around git can also exit 1 for a
    /// failure of its own, and then it says why. So an exit of 1 with words on stderr is a
    /// failure, and the caller keeps the list that it had.
    let foundNone (code: int) (complaint: string) : bool =
        code = 1 && complaint.Trim () = ""

    /// The most characters of a skill's name, and of its description, that the menu holds.
    /// They are the limits that the Agent Skills format gives a name and a description. The
    /// menu is in the context of every turn, and a process in a sandbox can write a
    /// `SKILL.md`, so one skill cannot make that context as long as its file is.
    let maxNameChars = 64
    let maxDescriptionChars = 1024

    /// `text`, or its first `limit` characters and a mark that says the rest is not here.
    let private cut (limit: int) (text: string) : string =
        if text.Length <= limit then text else text.Substring (0, limit) + " [...]"

    /// A control character, or a character that ends a line: the C0 controls (tab, LF and CR
    /// are three of them), DEL, the C1 controls, and the separators U+2028 and U+2029.
    let private isControl (c: char) : bool =
        let code = int c
        code < 0x20 || (code >= 0x7f && code <= 0x9f) || code = 0x2028 || code = 0x2029

    /// White space, or a control character.
    let private isBlank (c: char) : bool =
        let code = int c
        isControl c
        || code = 0x20
        || code = 0xa0
        || code = 0x1680
        || (code >= 0x2000 && code <= 0x200a)
        || code = 0x202f
        || code = 0x205f
        || code = 0x3000

    /// `text` as one line: each run of white space and control characters is one space, and
    /// there is none at either end.
    ///
    /// The menu is one line for each skill, and the three fields of a line come from a file
    /// that a process in a sandbox can write. A YAML value can hold a line end. A description
    /// with one added lines to the menu, and so could show a skill of another repo that the
    /// repo does not hold.
    let private oneLine (text: string) : string =
        let line = System.Text.StringBuilder ()
        let mutable gap = false
        for c in text do
            if isBlank c then
                gap <- true
            else
                if gap && line.Length > 0 then line.Append ' ' |> ignore
                gap <- false
                line.Append c |> ignore
        line.ToString ()

    let private decoder (path: string) : Decoder<SkillSummary> =
        Decode.object (fun get ->
            { Name = get.Required.Field "name" Decode.string
              Description = get.Required.Field "description" Decode.string
              Path = path })

    /// Decode frontmatter already turned to JSON text -- what crosses from the app's YAML
    /// parse, the same shape `ConfigFile.parse` takes -- for the file at `path`.
    ///
    /// The name and the description become one line each (`oneLine`), and then each is cut to
    /// its limit. A skill with no name left is no skill. A path with a control character in
    /// it is no skill either: the menu shows the path, and git prints a path as it is.
    let decode (path: string) (json: string) : Result<SkillSummary, string> =
        if String.exists isControl path then
            Error "the path of the skill file holds a control character"
        else
            Decode.fromString (decoder path) json
            |> Result.bind (fun skill ->
                match oneLine skill.Name with
                | "" -> Error "the skill has no name"
                | name ->
                    Ok
                        { skill with
                            Name = cut maxNameChars name
                            Description = cut maxDescriptionChars (oneLine skill.Description) })

    /// The order of a list of skills: by name, then by path. One order, so that the same
    /// checkout gives the same list at each read, and the session compares a list with the
    /// last one as it stands.
    let ordered (skills: SkillSummary list) : SkillSummary list =
        skills |> List.sortBy (fun skill -> skill.Name, skill.Path)
