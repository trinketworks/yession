namespace Yession.Session

open Yession.Domain.Repos

#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

// Reading a repo's `.agents/skills/<name>/SKILL.md`: the frontmatter alone, into
// `SkillSummary`. PURE and parser-free, on `ConfigFile`'s own terms -- `frontmatterOf` is
// text handling, and `decode` reads an already-parsed JSON tree, so both run in the cheap
// tier on both runtimes from literals. The bridge that reads a file and calls the YAML
// parser is the app's (`app/Repos.fs`, alongside `agentsMdOf`), the same split
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

    let private decoder : Decoder<SkillSummary> =
        Decode.object (fun get ->
            { Name = get.Required.Field "name" Decode.string
              Description = get.Required.Field "description" Decode.string })

    /// Decode frontmatter already turned to JSON text -- what crosses from the app's YAML
    /// parse, the same shape `ConfigFile.parse` takes.
    let decode (json: string) : Result<SkillSummary, string> =
        Decode.fromString decoder json
