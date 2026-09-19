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
#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

/// Anchored at the repository root rather than at the runner's working directory, which is
/// not this repository's business and has moved before. git's own stderr is left where it
/// goes: on a box with no repository it says so once, and `repoRoot` says nothing below.
///
/// `Fable.Node` types the answer as a string or a `Buffer`, because which one depends on the
/// `encoding` option; with `utf8` named it is the string, and the one `unbox` says so here
/// rather than in a match Fable cannot compile (a `Buffer` is an interface, so a type test
/// on it evaluates to false).
let private gitToplevel () : string =
    let options = jsOptions<Node.ChildProcess.ExecOptions> (fun o -> o.encoding <- Some "utf8")
    unbox<string> (Node.Api.childProcess.execSync ("git rev-parse --show-toplevel", box options))

/// Nothing for a working directory git will not answer about — this file's subject is a
/// committed document, and a box that cannot find one has not read it rather than read an
/// empty one.
let private repoRoot () : string option =
    try
        match (gitToplevel ()).Trim () with
        | "" -> None
        | root -> Some root
    with _ -> None

let private readText (path: string) : string option =
    try Some (TestFiles.read path) with _ -> None

/// What one sandbox declares as a way in: its `setup:`, and its container's `entrypoint`.
[<RequireQualifiedAccess>]
type private Declared =
    { Setup : string option
      Entrypoint : string option }

/// An argv as the file may write one — a list of words, or the one line — read back as the
/// line this file has an opinion about.
let private argv : Decoder<string> =
    Decode.oneOf
        [ Decode.string
          Decode.list Decode.string |> Decode.map (String.concat " ") ]

/// A `setup:` is one command line. Written empty it declares no command, and always did:
/// the document was once read with JavaScript truthiness, and this is what that meant. A
/// `setup:` that is anything else — a list, a number — is not a command line this file has
/// an opinion about, and rendering it as one would invent the opinion, so it is a refusal
/// to read the file rather than a command nobody wrote.
let private setup : Decoder<string option> =
    Decode.string
    |> Decode.map (function
        | "" -> None
        | line -> Some line)

/// A sandbox's declarations. Absent `container:` is no entrypoint rather than a throw, and
/// so is an absent `setup:` — the decoder's `Optional` is the same nothing for both.
let private declared : Decoder<Declared> =
    Decode.object (fun get ->
        { Declared.Setup = get.Optional.Field "setup" setup |> Option.flatten
          Entrypoint = get.Optional.At [ "container"; "entrypoint" ] argv })

/// The file's sandboxes by name. A file with no `sandboxes:` declares none.
let private sandboxes : Decoder<(string * Declared) list> =
    Decode.object (fun get ->
        match get.Optional.Field "sandboxes" (Decode.keyValuePairs declared) with
        | Some declared -> declared
        | None -> [])

/// Every command line the file declares a sandbox runs — its `setup:`, and its container's
/// `entrypoint` — as (where, command). Read straight out of the YAML the way `LockSource`
/// reads straight out of the lock's JSON: this is a question about what a committed file
/// SAYS, and routing it through the domain's decoder would put a second thing between the
/// assertion and the text it is about. The resolved value goes through JSON on the way to
/// the decoder, as `RepoConfig` sends it, which is what lets one reader serve both runtimes.
///
/// A file this cannot read is a failure with the reason in it, not an empty list: an empty
/// list is what a file that obeys the rule looks like, and the two must not read the same.
let private commandsIn (text: string) : (string * string) list =
    match Decode.fromString sandboxes (JS.JSON.stringify (Fable.Yaml.parse text)) with
    | Error reason -> failwithf "yession.yaml is not a document this file can read: %s" reason
    | Ok declared ->
        [ for name, sandbox in declared do
              match sandbox.Setup with
              | Some line -> yield name + " setup", line
              | None -> ()

              match sandbox.Entrypoint with
              | Some line -> yield name + " entrypoint", line
              | None -> () ]

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
