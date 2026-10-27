module Yession.Host.RepoConfig

// The bridge between a `yession.yaml` on disk and `ConfigFile` in the domain (Plan 27).
//
// Deliberately the thinnest thing that can work, because everything worth testing lives on
// the other side of it: `Yession.Domain.ConfigFile` decodes an already-parsed tree, so it
// runs in the cheap tier on both runtimes from JSON literals. This module is the part that
// cannot — it asks git for a file and calls a JS parser — and it is kept small enough that its own
// failure modes are the only thing left to get wrong.
//
// YAML is a superset of JSON, so the parse (`YamlSource`) hands the decoder a JSON tree and
// nothing about the schema knows which surface syntax it came from. Changing that syntax
// later is a change to the parse stage alone.

open Yession.Domain
open Yession.Domain.Sandboxes
open Yession.Session

/// A repo's file as it was read: what it declares, and what the analyzers had to say about
/// it. The findings never change the declarations — see `ConfigAnalysis`.
[<RequireQualifiedAccess>]
type ConfigRead =
    { File : ConfigFile
      Findings : LocatedFinding list }

/// The stages after the read, from text: parse, decode, analyze. Each is a pure function of
/// the one before, which is what this composes and nothing else.
let fromText (text: string) : Result<ConfigRead, string> =
    YamlSource.parse text
    |> Result.bind (fun parsed ->
        ConfigFile.parse parsed.Json
        |> Result.map (fun file ->
            { ConfigRead.File = file
              ConfigRead.Findings = ConfigAnalysis.run Analyzers.all parsed.Index file.Sandboxes }))

/// Read one repo's file, as its `HEAD` commits it (`files`, which reads git's object store in
/// the git sandbox). Not the working tree: whoever can push to the repo authors the file
/// (docs/technical-design.md §4), so an edit nobody has committed declares nothing, and the
/// session opens no path a repository names (§5, rule 20).
///
/// Three outcomes, and the distinction between the first two is the point: a repo with no
/// `yession.yaml` is ORDINARY and asks for nothing, while a repo whose file cannot be read is
/// a fact somebody has to see. Folding the second into the first would make a broken file
/// indistinguishable from an absent one — which is how a repo silently stops being configured
/// the day somebody mistypes a key.
let read (files: Repos.CommittedFile) (repo: RepoRef) : Async<Result<ConfigRead option, string>> =
    async {
        // Every refusal names the repo: a session holds several checkouts, and "a config is
        // broken" is not something anybody can act on.
        let saying (reason: string) =
            sprintf "%s in %s: %s" ConfigFile.FileName (RepoRef.value repo) reason
        match! files repo ConfigFile.FileName with
        | Error reason -> return Error (saying reason)
        | Ok None -> return Ok None
        | Ok (Some text) ->
            try return fromText text |> Result.map Some |> Result.mapError saying
            with e -> return Error (saying e.Message)
    }

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
      /// Every readable file's `session.shell-profile`, as written (relative to its checkout).
      /// All of them, not a winner: which one applies, if any, is `ConfiguredProfile`'s.
      ShellProfiles : (RepoRef * string) list
      Refused : (RepoRef * string) list
      Findings : (RepoRef * LocatedFinding) list }

let readAll (files: Repos.CommittedFile) (repos: RepoRef list) : Async<ReadAll> =
    async {
        let mutable results = []
        for repo in repos do
            let! result = read files repo
            results <- results @ [ repo, result ]
        let read =
            results
            |> List.choose (fun (repo, result) ->
                match result with
                | Ok (Some file) -> Some (repo, file)
                | _ -> None)
        return
            { ReadAll.Declared = ConfigFile.union (read |> List.map (fun (repo, file) -> repo, file.File))
              ReadAll.ShellProfiles =
                read |> List.choose (fun (repo, file) -> file.File.Session.ShellProfile |> Option.map (fun path -> repo, path))
              ReadAll.Refused =
                results
                |> List.choose (fun (repo, result) ->
                    match result with
                    | Error reason -> Some (repo, reason)
                    | _ -> None)
              ReadAll.Findings = read |> List.collect (fun (repo, file) -> file.Findings |> List.map (fun finding -> repo, finding)) }
    }
