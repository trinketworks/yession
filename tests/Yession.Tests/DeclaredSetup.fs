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

/// Imported, never `require`d. This module compiles to an ES module, where `require` is not
/// defined — and the throw does not read as "this check cannot run", it reads as every case in
/// the file erroring for a reason that looks like the file's own subject. `LockSource` carries
/// the same warning for the same reason; this rule earned it on its first run.
let private yaml : obj = importAll "yaml"

/// Anchored at the repository root rather than at the runner's working directory, which is
/// not this repository's business and has moved before.
[<Emit("$0.execSync('git rev-parse --show-toplevel', { encoding: 'utf8', stdio: ['ignore','pipe','ignore'] })")>]
let private gitToplevel (cp: obj) : string = jsNative

/// `yaml.parse`, and the handful of questions this file asks of what it returns. Each one is a
/// JavaScript expression naming a platform API; the walk over the document is below, in F#.
[<Emit("$0.parse($1)")>]
let private parseYaml (y: obj) (text: string) : obj = jsNative

/// A property, or `undefined` — off an absent holder too, so a file with no `sandboxes:` and a
/// sandbox with no `container:` are the same nothing rather than a throw.
[<Emit("$0?.[$1]")>]
let private prop (o: obj) (key: string) : obj = jsNative

/// The keys of an object that may not be there — an absent block is no keys, not a throw.
let private keysOf (o: obj) : string array =
    if isNull o then [||] else JS.Constructors.Object.keys o |> Array.ofSeq

/// JS truthiness, kept because that is what the document is read with: a `setup:` written
/// empty declares no command, and always did.
[<Emit("!!$0")>]
let private isDeclared (o: obj) : bool = jsNative

let private isArray (o: obj) : bool = JS.Constructors.Array.isArray o

[<Emit("$0.join(' ')")>]
let private joinedWithSpaces (o: obj) : string = jsNative

[<Emit("String($0)")>]
let private asText (o: obj) : string = jsNative

/// Nothing for a working directory git will not answer about — this file's subject is a
/// committed document, and a box that cannot find one has not read it rather than read an
/// empty one.
let private repoRoot () : string option =
    try
        match (gitToplevel childProcess).Trim () with
        | "" -> None
        | root -> Some root
    with _ -> None

let private readText (path: string) : string option =
    try Some (TestFiles.read path) with _ -> None

/// Every command line the file declares a sandbox runs — its `setup:`, and its container's
/// `entrypoint` (a string, or a list joined back into one) — as (where, command). Read
/// straight out of the YAML the way `LockSource` reads straight out of the lock's JSON: this
/// is a question about what a committed file SAYS, and routing it through the domain's
/// decoder would put a second thing between the assertion and the text it is about.
let private commandsIn (text: string) : (string * string) list =
    let declared = prop (parseYaml yaml text) "sandboxes"
    [ for name in keysOf declared do
        let sandbox = prop declared name

        let setup = prop sandbox "setup"
        if isDeclared setup then
            // `String`, never the join: a `setup:` that is a list is not a command line this
            // file has an opinion about, and rendering it as one would invent the opinion.
            yield name + " setup", asText setup

        let entrypoint = prop (prop sandbox "container") "entrypoint"
        if isDeclared entrypoint then
            yield name + " entrypoint", (if isArray entrypoint then joinedWithSpaces entrypoint else asText entrypoint) ]

let private declaredCommands () : (string * string) list =
    match repoRoot () |> Option.bind (fun root -> readText (root + "/yession.yaml")) with
    | Some text -> commandsIn text
    | None -> []

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
