module Yession.Tests.TasksCapabilities

// The capabilities `check` knows, and the capabilities the suite declares, are one set.
//
// A capability is written twice. `Need` in `Tags.fs` is what a suite asks for; the `capabilities`
// table in `tasks.fsx` is what `check` probes, what `verify` asks for and what `help` prints.
// They compile apart, one in the Fable suite and one in a script, so no type joins them, and each
// can say a thing the other has never heard of. Added to `Need` alone, a capability has suites and
// no probe: `check` cannot refuse a box that lacks it, and `help` never mentions it. Added to the
// table alone, it is offered by `help` and nothing runs under it.
//
// Cheap tier, and a document read rather than a run, for the reason `VerifyTiers` is: nothing
// else reads `tasks.fsx` before a master push does, and a script cannot be loaded into a Fable
// suite. What it reads the table FROM is the file `check` itself runs, and what it reads `Need`
// from is the type, by reflection, so neither side is a copy.

open Fable.Pyxpecto

/// One row of `capabilities` in `tasks.fsx`: its name, and whether `verify` asks for it.
type private Row = { Name: string; Gated: bool }

/// Rows are found by their SHAPE (`{ Name = "X"`, then `Needs =`, then `Gated =`) and not by where
/// they sit, so a blank line or a comment between two of them changes nothing. The verbs beside
/// them in the same file are `{ Name = …; Summary = … }`, which this does not match.
let private rowPattern = "\\{ Name = \"(\\w+)\"\\s+Needs = \".*\"\\s+Gated = (true|false)"

/// A file this cannot read rows from is a failure carrying the reason, never an empty list: an
/// empty list is what a table nobody parsed looks like, and it would make every rule below true.
let private rowsIn (source: string) : Row list =
    let rows =
        System.Text.RegularExpressions.Regex.Matches (source, rowPattern)
        |> Seq.map (fun found -> { Name = found.Groups.[1].Value; Gated = found.Groups.[2].Value = "true" })
        |> List.ofSeq
    if List.isEmpty rows then
        failwith
            "tasks.fsx has no capability rows this can read. They are written { Name = \"X\" Needs = \"…\" Gated = true … } in the `capabilities` table; if that table changed shape, change rowPattern in tests/Yession.Tests/TasksCapabilities.fs with it."
    rows

let private declared () : Row list = rowsIn (TestFiles.read "tasks.fsx")

/// Every case of `Need`, enumerated by the type rather than listed, so a case added to it is a
/// case this sees. `Tag.allNeeds` is hand-listed and leaves two out on purpose.
let private needs () : string list =
    FSharp.Reflection.FSharpType.GetUnionCases(typeof<Tag.Need>)
    |> Array.map (fun case -> case.Name)
    |> List.ofArray

let private names (rows: Row list) : string list = rows |> List.map (fun row -> row.Name)

let tests =
    testList "Tasks capabilities" [

        // The refusal half: a name tasks.fsx offers that no suite can ask for is a capability
        // `check` would probe on behalf of nothing, and `help` would list as real.
        testCase "every capability tasks.fsx lists is a case of Need" <| fun () ->
            let known = needs ()
            let unknown = names (declared ()) |> List.filter (fun name -> not (List.contains name known))
            Expect.isEmpty
                unknown
                (sprintf
                    "tasks.fsx lists %s, which `Need` in tests/Yession.Tests/Tags.fs has no case for. Add the case (and its line in `parseNeed`), or remove the row from `capabilities` in tasks.fsx"
                    (String.concat ", " unknown))

        // The silence half, and the one that bites: a `Need` case with no row has suites, and
        // nothing that checks the box can host them or tells a person it exists.
        testCase "every case of Need has a row in tasks.fsx" <| fun () ->
            let listed = names (declared ())
            let unlisted = needs () |> List.filter (fun name -> not (List.contains name listed))
            Expect.isEmpty
                unlisted
                (sprintf
                    "`Need` has %s, which the `capabilities` table in tasks.fsx has no row for. Add one: its name, what it needs and how it is probed (the line `help` prints), whether verify asks for it, and the probe `check` refuses with"
                    (String.concat ", " unlisted))

        // `Gated` and `Tag.allNeeds` both say which capabilities the release gate means. Two
        // answers to one question do not agree by habit: `VerifyTiers` holds the tiers to
        // `allNeeds`, so a capability `verify` asks for and `allNeeds` lacks runs in no tier.
        testCase "verify asks for exactly the capabilities Tag.allNeeds names" <| fun () ->
            let asked = declared () |> List.filter (fun row -> row.Gated) |> names |> List.sort
            let gate = Tag.allNeeds |> List.map (sprintf "%A") |> List.sort
            Expect.equal
                asked
                gate
                "the `Gated = true` rows of `capabilities` in tasks.fsx (actual) and `Tag.allNeeds` in tests/Yession.Tests/Tags.fs (expected) must be the same set. A capability the release gate means is in both; one left out on purpose (Bench, Dogfood) is in neither"

        // `Srt` is a sandbox too: the strict one. A run that has it hosts the suites that only
        // need somewhere to RUN a command, so a tier naming `Srt` need not name `Sandbox`.
        testCase "a run that has Srt also has Sandbox" <| fun () ->
            Expect.isTrue (List.contains Tag.Sandbox (Tag.withImplied [ Tag.Srt ])) "Srt grants Sandbox"

        // The direction that must never hold. `Sandbox` is satisfied by the weak profile, and a
        // weak sandbox is no evidence that the boundary holds: if it granted `Srt`, an escape
        // probe would run (and pass) under a profile production never uses.
        testCase "a run that has only Sandbox does not have Srt" <| fun () ->
            Expect.isFalse (List.contains Tag.Srt (Tag.withImplied [ Tag.Sandbox ])) "Sandbox does not grant Srt"
    ]
