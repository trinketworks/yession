module Yession.Tests.NixSource

// What the Nix derivations are allowed to see of this repository (`src` in nix/packages.nix).
//
// This is the one contract in the build that NO CI job can check. Every route CI takes —
// `nix build .#yession`, the darwin package job, release.yml's package-nix — evaluates a FLAKE,
// and a flake's source copy has already been filtered by git: nothing ignored can reach the
// derivation, whatever the filter in packages.nix does or fails to do. Only a working tree
// reaches it unfiltered (`nix build --file nix/worktree.nix`, `devenv build outputs.…`), and
// only a machine that has entered the dev shell HAS the artefacts that leak. So the failure
// mode is exactly inverted from the usual one: green everywhere in CI, broken on the laptop.
//
// It broke that way once already. devenv's enterShell points `node_modules` at
// `${nodeModules}/node_modules` in the store, that same path is a build input of `staged`, and
// the unfiltered copy landed a live symlink to a read-only directory exactly where `staged`
// copies its own:
//
//     cp: cannot create directory './node_modules/node_modules'
//
// Alongside it, ~176MB of dotnet obj/bin, Fable's emitted .js and app/out rode in, so a local
// build was neither the build CI ran nor cacheable across a single edit.
//
// The invariant asserted here is the general one — the derivation source carries nothing git
// ignores — rather than a list of the artefacts that leaked. A new build output added to
// .gitignore next month is covered without touching this file.
//
// Needs the `Nix` capability: `check` probes for the nix CLI and drops it when absent, so this
// reports a skip rather than an error on a box without Nix.

open Fable.Core
open Fable.Core.JsInterop
open Fable.Pyxpecto
open Fable.NodeExtras

/// `spawnSync`'s own limit is 1 MiB, and the source listing this suite reads is larger than
/// that: a child killed for exceeding it would answer a truncated listing, which reads exactly
/// like a filter that dropped the rest.
let private roomForTheWholeListing = 32 * 1024 * 1024

/// What a child said, for a message about it going wrong. A stream that is `None` is not an
/// empty answer: it is a child that never ran at all, and a message printing `""` for that
/// would report the one failure this suite cannot otherwise see as silence.
let private said (stream: string option) : string =
    stream |> Option.defaultValue "(nothing — the child could not be spawned)"

/// Every entry under `root`, as paths relative to it. A symlink is reported as itself and never
/// descended into — in the failure this suite guards, `node_modules` is a symlink into the Nix
/// store, and following it would walk that entire closure instead of finding the leak.
let rec private entriesUnder (root: string) (rel: string) : string list =
    Files.entries (if rel = "" then root else root + "/" + rel)
    |> Array.toList
    |> List.collect (fun entry ->
        let path = if rel = "" then entry.name else rel + "/" + entry.name
        if entry.isDirectory () && not (entry.isSymbolicLink ()) then path :: entriesUnder root path else [ path ])

/// The build source, materialised in the store exactly as `staged` unpacks it. `--file`
/// evaluates nix/worktree.nix in place, so this is the WORKING TREE — the route CI never takes
/// and the only one where the filter can be caught failing. `--extra-experimental-features` so
/// a box that never opted into the new CLI still runs this.
let private buildSource =
    lazy
        (let result =
            spawnSync
                "nix"
                [ "--extra-experimental-features"; "nix-command"
                  "eval"; "--raw"; "--file"; "nix/worktree.nix"; "staged.src" ]
                { Input = None; MaxBuffer = Some roomForTheWholeListing }
         // `None` is a child a signal killed, which has no exit code to compare and is not a
         // success: `-1` says so where reading the absent status as `0` would have a killed
         // `nix eval` report an empty source as the answer.
         if result.status |> Option.defaultValue -1 <> 0 then
             failwithf "could not evaluate the build source: %s" (said result.stderr)
         match result.stdout with
         | Some source -> source.Trim ()
         | None -> failwith "nix eval was never run, so there is no build source to read")

let private entries = lazy (entriesUnder buildSource.Value "")

/// What the build reads out of the source. A file missing here fails the Nix build in a way
/// that names a tool rather than the filter (`MSB1003`, `ENOENT package.json`), which is a
/// long way from "the src filter dropped it".
let private consumed =
    [ "tasks.fsx"
      "package.json"
      "package-lock.json"
      "Yession.slnx"
      "Directory.Build.props"
      "Directory.Packages.props"
      ".config/dotnet-tools.json"
      "README.md" ]

/// Tracked, but not consumed: editing CI, docs, the agent notes or the devenv config must not
/// invalidate the (slow) F#/Fable build.
let private notConsumed =
    [ "nix/"; ".github/"; "docs/"; ".claude/"; ".agents/"
      "flake.nix"; "flake.lock"; "devenv.nix"; "devenv.yaml"; "AGENTS.md"; "CLAUDE.md" ]

let tests =
    testList "the Nix build source" [
        testCase "carries nothing git ignores" <| fun () ->
            let listed = entries.Value
            let result =
                spawnSync
                    "git"
                    [ "check-ignore"; "--stdin" ]
                    { Input = Some (String.concat "\n" listed); MaxBuffer = Some roomForTheWholeListing }
            let exitCode = result.status |> Option.defaultValue -1
            // check-ignore exits 1 when NOTHING matched — the passing outcome here. 0 means it
            // named at least one ignored path; anything else means git failed to answer, which
            // must not read as a pass.
            Expect.isTrue
                (exitCode = 0 || exitCode = 1)
                (sprintf "git check-ignore could not answer: %s" (said result.stderr))
            let ignored =
                match result.stdout with
                | Some listed -> listed.Trim ()
                | None -> failwith "git check-ignore was never run, so nothing was checked"
            // The message carries the offending paths: an equality assertion over the whole
            // listing reports "string was longer than expected", which names nothing.
            Expect.isTrue
                (ignored = "")
                (sprintf "the derivation source carries paths git ignores, so it is not the tree CI builds:\n%s" ignored)

        testCase "carries no node_modules — a dev shell points it at the store, read-only" <| fun () ->
            Expect.isFalse
                (entries.Value |> List.exists (fun p -> p = "node_modules" || p.StartsWith "node_modules/"))
                "`staged` copies its own node_modules to that path; anything already there makes cp descend into it"

        testCase "carries everything the build reads" <| fun () ->
            let listed = Set.ofList entries.Value
            for path in consumed do
                Expect.isTrue (Set.contains path listed) (sprintf "%s is a build input and must survive the filter" path)

        testCase "carries nothing the build does not read" <| fun () ->
            let files =
                entries.Value
                |> List.filter (fun p ->
                    notConsumed
                    |> List.exists (fun excluded ->
                        if excluded.EndsWith "/" then p.StartsWith excluded else p = excluded))
            Expect.isTrue
                (List.isEmpty files)
                (sprintf
                    "editing these must not invalidate the build, so they are filtered out of its source:\n%s"
                    (String.concat "\n" files))
    ]
