module Yession.Host.RepoConfig

// The bridge between a `yession.yaml` on disk and `ConfigFile` in the domain (Plan 27).
//
// Deliberately the thinnest thing that can work, because everything worth testing lives on
// the other side of it: `Yession.Domain.ConfigFile` decodes an already-parsed tree, so it
// runs in the cheap tier on both runtimes from JSON literals. This module is the part that
// cannot — it reads a file and calls a JS parser — and it is kept small enough that its own
// failure modes are the only thing left to get wrong.
//
// YAML is a superset of JSON, so the parse (`YamlSource`) hands the decoder a JSON tree and
// nothing about the schema knows which surface syntax it came from. Changing that syntax
// later is a change to the parse stage alone.

open Yession.Domain
open Yession.Domain.Sandboxes

/// A repo's file as it was read: what it declares, and what the analyzers had to say about
/// it. The findings never change the declarations — see `ConfigAnalysis`.
[<RequireQualifiedAccess>]
type ConfigRead =
    { File : ConfigFile
      Findings : LocatedFinding list }

/// Where one repo's file lives, given the session's repos directory.
let pathIn (reposDir: string) (repo: RepoRef) : string =
    sprintf "%s/%s/%s" reposDir (RepoRef.relativePath repo) ConfigFile.FileName

/// The stages after the read, from text: parse, decode, analyze. Each is a pure function of
/// the one before, which is what this composes and nothing else.
let fromText (text: string) : Result<ConfigRead, string> =
    YamlSource.parse text
    |> Result.bind (fun parsed ->
        ConfigFile.parse parsed.Json
        |> Result.map (fun file ->
            { ConfigRead.File = file
              ConfigRead.Findings = ConfigAnalysis.run Analyzers.all parsed.Index file.Sandboxes }))

/// Read one repo's file.
///
/// Three outcomes, and the distinction between the first two is the point: a repo with no
/// `yession.yaml` is ORDINARY and asks for nothing, while a repo whose file cannot be read is
/// a fact somebody has to see. Folding the second into the first would make a broken file
/// indistinguishable from an absent one — which is how a repo silently stops being configured
/// the day somebody mistypes a key.
let read (reposDir: string) (repo: RepoRef) : Result<ConfigRead option, string> =
    let path = pathIn reposDir repo
    if not (Fs.exists path) then Ok None
    else
        // Every refusal names the repo: a session holds several checkouts, and "a config is
        // broken" is not something anybody can act on.
        let saying (reason: string) =
            sprintf "%s in %s: %s" ConfigFile.FileName (RepoRef.value repo) reason
        try fromText (Fs.readText path) |> Result.map Some |> Result.mapError saying
        with e -> Error (saying e.Message)

/// Every configured repo, read: the session-wide map of what they declare, the repos whose
/// file could not be honoured, and what the analyzers found in the files that could.
///
/// A repo whose file is unreadable contributes NOTHING and does not stop the others: a
/// session that failed to boot because one checkout had a typo would be a session held
/// hostage by any repo it happened to contain. The refusals and the findings come back beside
/// the map so the caller can record them where somebody will read them.
[<RequireQualifiedAccess>]
type ReadAll =
    { Declared : Map<SandboxRef, SandboxDecl>
      Refused : (RepoRef * string) list
      Findings : (RepoRef * LocatedFinding) list }

let readAll (reposDir: string) (repos: RepoRef list) : ReadAll =
    let results = repos |> List.map (fun repo -> repo, read reposDir repo)
    let read =
        results
        |> List.choose (fun (repo, result) ->
            match result with
            | Ok (Some file) -> Some (repo, file)
            | _ -> None)
    { ReadAll.Declared = ConfigFile.union (read |> List.map (fun (repo, file) -> repo, file.File))
      ReadAll.Refused =
        results
        |> List.choose (fun (repo, result) ->
            match result with
            | Error reason -> Some (repo, reason)
            | _ -> None)
      ReadAll.Findings = read |> List.collect (fun (repo, file) -> file.Findings |> List.map (fun finding -> repo, finding)) }
