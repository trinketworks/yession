module Yession.Tests.DomainReferences

// What the domain may depend on: nothing.
//
// `Yession.Domain` is the model — identities, facts, events and the rules over them — and
// every service builds on it. A package in it is a package in all of them, and a reason for
// the model to bend toward a library: it used to carry Yjs, Ylmish and ProseMirror for the
// collaborative document, and Thoth for whichever codecs had not yet found the project that
// owns them. Each of those moved to its owner (the App's document, the Session's tool JSON,
// `Yession.Codecs` for identities), and what is left references nothing but FSharp.Core.
//
// Read from the project's lockfile rather than its `.fsproj`, because the lockfile is the
// whole answer: it lists every package the project restores, transitive ones included, and
// every project it references, and locked mode (`Directory.Build.props`) fails any build
// whose lockfile no longer says what the project asks for. A reference cannot be added
// without this file changing.
//
// No capability: it reads a file.

open Fable.Pyxpecto
#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

/// Anchored at the repository root, the way `ReleaseGate` reads its workflow: the subject is
/// a committed file, not whatever the runner's working directory holds.
let private repoRoot () : string option =
    try
        match (Fable.NodeExtras.SyncChildProcesses.execSync "git rev-parse --show-toplevel" Fable.NodeExtras.SyncOptions.none).Trim () with
        | "" -> None
        | root -> Some root
    with _ -> None

/// Every framework the lockfile restores for, and the names it restores there. Only the
/// NAMES: what version a dependency resolves to is NuGet's business, and the rule is about
/// whether there is one at all.
let private dependencies : Decoder<(string * string list) list> =
    Decode.field
        "dependencies"
        (Decode.keyValuePairs (Decode.keyValuePairs Decode.value |> Decode.map (List.map fst)))

let tests =
    testList "the domain's references" [

        testCase "the domain restores no package and references no project" <| fun () ->
            let lockfile =
                repoRoot ()
                |> Option.bind (fun root ->
                    try Some (TestFiles.read (root + "/src/Yession.Domain/packages.lock.json")) with _ -> None)
            match lockfile with
            | None ->
                // Not a pass: a run that could not read the lockfile has checked nothing.
                failwith "could not read src/Yession.Domain/packages.lock.json"
            | Some json ->
                match Decode.fromString dependencies json with
                | Error reason -> failwithf "src/Yession.Domain/packages.lock.json is not a lockfile this can read: %s" reason
                | Ok frameworks ->
                    Expect.isNonEmpty frameworks "the lockfile names the framework it restores for"
                    Expect.equal
                        (frameworks |> List.collect snd)
                        []
                        "the domain is the model every service builds on; a dependency here is one in all of them"
    ]
