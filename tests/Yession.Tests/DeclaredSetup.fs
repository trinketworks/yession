module Yession.Tests.DeclaredSetup

// What THIS repository's own `yession.yaml` may declare as the way into its devshell — a
// sandbox's `setup:`, or its container's `entrypoint` (where the line lives now).
//
// One rule, and it exists because the two halves of it drifted: `nix develop` in this
// container resolves devenv's root from the PROCESS's working directory, which a pure
// evaluation does not have — so the pure form fails, every time, with "devenv was not able to
// determine the current directory". `DevContainer.fs` has run the `--impure` form for as long
// as it has run anything; `yession.yaml` and the prose in AGENTS.md said the pure one. Nothing
// disagreed out loud, because `setup:` is queued in the BACKGROUND and a block nobody waited
// for is a block nobody reads.
//
// What it cost, measured in a real session: an agent asked to check out this repository and
// run its tests followed the documented form, watched it fail, and spent nine tool calls
// working out why — one of them grepping the workflows for the flag this file now requires.
//
// Cheap tier deliberately. The Dogfood suite proves the command WORKS, and it needs Docker, a
// warm nix store and about eleven minutes; this proves the repository still ASKS for the form
// that works, which is a question about a committed file and wants none of that. The
// expensive test is the reason to believe the rule; this is what keeps the file obeying it.

open Fable.Core
open Fable.Core.JsInterop
open Fable.Pyxpecto

let private childProcess : obj = importAll "node:child_process"
let private nodeFs : obj = importAll "node:fs"

/// Imported, never `require`d. This module compiles to an ES module, where `require` is not
/// defined — and the throw does not read as "this check cannot run", it reads as every case in
/// the file erroring for a reason that looks like the file's own subject. `LockSource` carries
/// the same warning for the same reason; this rule earned it on its first run.
let private yaml : obj = importAll "yaml"

/// Anchored at the repository root rather than at the runner's working directory, which is
/// not this repository's business and has moved before.
[<Emit("(() => { try { return $0.execSync('git rev-parse --show-toplevel', { encoding: 'utf8', stdio: ['ignore','pipe','ignore'] }).trim() } catch { return '' } })()")>]
let private repoRoot (cp: obj) : string = jsNative

[<Emit("(() => { try { return $0.readFileSync($1, 'utf8') } catch { return '' } })()")>]
let private readText (fs: obj) (path: string) : string = jsNative

/// Every command line the file declares a sandbox runs — its `setup:`, and its container's
/// `entrypoint` (a string, or a list joined back into one) — as (where, command). Read
/// straight out of the YAML the way `LockSource` reads straight out of the lock's JSON: this
/// is a question about what a committed file SAYS, and routing it through the domain's
/// decoder would put a second thing between the assertion and the text it is about.
[<Emit("(function (y, text) { const parsed = y.parse(text) ?? {}; const declared = parsed.sandboxes ?? {}; const out = []; for (const k of Object.keys(declared)) { const s = declared[k] ?? {}; if (s.setup) out.push([k + ' setup', String(s.setup)]); const e = s.container && s.container.entrypoint; if (e) out.push([k + ' entrypoint', Array.isArray(e) ? e.join(' ') : String(e)]) } return out })($0, $1)")>]
let private commandsWith (y: obj) (text: string) : (string * string) array = jsNative

let private declaredCommands () : (string * string) list =
    match repoRoot childProcess with
    | "" -> []
    | root -> readText nodeFs (root + "/yession.yaml") |> commandsWith yaml |> List.ofArray

let tests =
    testList "Declared setup" [

        // The population, asserted first: a rule over an empty list is green and proves
        // nothing, and this file is exactly as strong as its ability to still find the
        // declarations. A `yession.yaml` that moves, stops parsing, or renames the key reads
        // identically to one that obeys — unless this says otherwise. It read that way on this
        // rule's first run, which is the reason the case exists rather than a precaution.
        testCase "this repository declares a way into its devshell to have an opinion about" <| fun () ->
            Expect.isTrue
                (declaredCommands () |> List.exists (fun (_, command) -> command.Contains "nix develop"))
                "yession.yaml declares at least one `nix develop` — as a sandbox's entrypoint, or a setup"

        testCase "a declared command that enters the devshell enters it impurely" <| fun () ->
            for where, command in declaredCommands () do
                if command.Contains "nix develop" then
                    Expect.isTrue
                        (command.Contains "--impure")
                        (sprintf
                            "the %s runs `nix develop` purely, which cannot resolve devenv's root and fails every time: %s"
                            where
                            command)
    ]
