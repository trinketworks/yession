module Yession.Tests.VerifyTiers

// The release gate runs on several runners at once, and this is what says it is still whole.
//
// `verify.yml` spreads the gate over the tiers declared in `.github/verify-tiers.json`, one job
// each, because the three halves of it share no output and ran sequentially for no reason beyond
// `verify` being one word. A tier is a list of capability NAMES, and that is the whole hazard: a
// suite declares what it needs, `Tag.needs` runs it only where those needs are met, and a suite
// whose needs no tier satisfies therefore runs NOWHERE — not as an error, not even as a skip
// anybody reads, because the tier that would have printed the skip does not exist either. The run
// is green, it is short by exactly one suite, and nothing in it says so.
//
// This repository has paid for that shape twice already, both times on the release gate: a
// `LiveAgent` tier naming a secret it does not have shipped eleven versions with the live agent
// suite skipped and green, and a daemon-less `verify` printed `383 passed, 0 ignored`. The remedy
// each time was to make the gap fail rather than pass. This is the same remedy for splitting it.
//
// Cheap tier, and a document read rather than a run, for the reason `ReleaseGate` and
// `DeclaredSetup` are: the expensive proof is a master push, which is the one place it is too late
// to learn that a tier stopped covering something.
//
// It reads the file `verify.yml` reads, not a copy of it — one declaration, two readers. What it
// reads the declarations FROM is the assembly itself (`Tag.declaredSuites`), so a suite added
// tomorrow is one this rule already covers.

open Fable.Core
open Fable.Core.JsInterop
open Fable.Pyxpecto
#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

/// Anchored at the repository root, not at the runner's working directory — the same reading
/// `ReleaseGate` does, and for the same reason: the subject is a committed document.
let private gitToplevel () : string =
    let options = jsOptions<Node.ChildProcess.ExecOptions> (fun o -> o.encoding <- Some "utf8")
    unbox<string> (Node.Api.childProcess.execSync ("git rev-parse --show-toplevel", box options))

let private repoRoot () : string option =
    try
        match (gitToplevel ()).Trim () with
        | "" -> None
        | root -> Some root
    with _ -> None

/// Which of the suite's two runtimes a tier runs (`check --runtime`). A tier that names none runs
/// both; `Neither` runs no suite at all, only the `NixBuild` build.
[<RequireQualifiedAccess>]
type private Runtime =
    | Node
    | Clr
    | Neither

/// One tier: what the job is called, the capabilities it hands `check`, and the runtime it
/// confines itself to, if any.
[<RequireQualifiedAccess>]
type private Tier =
    { Name : string
      Capabilities : string list
      Runtime : Runtime option }

/// A runtime this cannot name is a document this cannot read, not a tier that runs both: the
/// second reading would count every suite as covered by a tier that `check` then refuses to start.
let private runtime : Decoder<Runtime> =
    Decode.string
    |> Decode.andThen (function
        | "node" -> Decode.succeed Runtime.Node
        | "clr" -> Decode.succeed Runtime.Clr
        | "none" -> Decode.succeed Runtime.Neither
        | other -> Decode.fail (sprintf "`%s` is not a runtime `check` knows (node, clr, none)" other))

let private tier : Decoder<Tier> =
    Decode.object (fun get ->
        { Tier.Name = get.Required.Field "tier" Decode.string
          Capabilities =
            (get.Required.Field "capabilities" Decode.string)
                .Split ([| ' ' |], System.StringSplitOptions.RemoveEmptyEntries)
            |> List.ofArray
          Runtime = get.Optional.Field "runtime" runtime })

/// A file this cannot read is a failure carrying the reason, never an empty list: an empty list is
/// what a gate with no tiers looks like, and the two must not read the same.
let private tiersIn (text: string) : Tier list =
    match Decode.fromString (Decode.list tier) text with
    | Error reason -> failwithf ".github/verify-tiers.json is not a document this file can read: %s" reason
    | Ok tiers -> tiers

let private declaredTiers () : Tier list =
    match repoRoot () |> Option.bind (fun root -> try Some (TestFiles.read (root + "/.github/verify-tiers.json")) with _ -> None) with
    | Some text -> tiersIn text
    | None -> []

/// The suites the GATE is answerable for: everything this assembly declares whose needs are all
/// capabilities `verify` means. `Tag.allNeeds` is that set, and the two it leaves out are left out
/// on purpose — `Bench` measures rather than asserts, `Dogfood` is tens of minutes of self-hosting
/// — so a suite needing either is asked for by name and is no tier's to cover.
let private gated (declarations: (string * Tag.Need list) list) =
    declarations
    |> List.filter (fun (_, need) -> need |> List.forall (fun n -> List.contains n Tag.allNeeds))

/// Which tier runs this suite: one whose capabilities include every need it has, AND that runs
/// the runtime the suite lives on. That is `Tag.canRun` read against a tier instead of a process.
///
/// The runtime half used to need no checking, because `Browser` pins a suite to the .NET CLR and
/// is itself a capability — a tier naming it ran that runtime, and every tier ran Node. Since a
/// tier can confine itself to one (`check --runtime`), a Node suite admitted by the `browser`
/// tier's capabilities is no longer run there, and counting it would be exactly the hole this
/// file exists to refuse: a Node suite needing `Caddy` alone would read as covered and run
/// nowhere.
let private covers (tier: Tier) (need: Tag.Need list) =
    let onClr = List.contains Tag.Browser need
    let runsIt =
        match tier.Runtime with
        | None -> true
        | Some Runtime.Clr -> onClr
        | Some Runtime.Node -> not onClr
        | Some Runtime.Neither -> false
    runsIt
    && need
       |> List.forall (fun n ->
           tier.Capabilities |> List.exists (fun name -> Tag.parseNeed name = Some n))

let tests =
    testList "Verify tiers" [

        // The population, asserted first and in both directions: every rule below is green over a
        // file this cannot find and over an assembly whose declarations it cannot see, and a gate
        // that obeys reads identically to one nobody managed to read. `DeclaredSetup` learned that
        // on its own first run, and `Kept` is the other half of the same lesson.
        testCase "the gate still declares tiers, and this assembly still declares suites to spread over them" <| fun () ->
            Expect.isNonEmpty (declaredTiers ()) ".github/verify-tiers.json declares at least one tier"
            // A floor rather than an exact count: suites are added weekly and the number is not the
            // subject. What it refuses is a registry that has stopped being written to at all —
            // which would make every rule below vacuously true.
            let declarations = Tag.declaredSuites ()
            Expect.isTrue
                (List.length declarations > 100)
                (sprintf
                    "Tag.declaredSuites saw only %d declarations, which is too few to be this suite: has `Tag.needs` stopped recording, or is this reading the registry before Main.fs built the tree?"
                    (List.length declarations))

        // A tier names capabilities, and a name is either one this repository has or a typo that
        // silently narrows the tier. `check` would refuse an unknown name at the runner, ten
        // minutes into a master push; `Tag.parseNeed` refuses it here.
        testCase "every capability a tier names is one that exists" <| fun () ->
            for tier in declaredTiers () do
                for name in tier.Capabilities do
                    Expect.isTrue
                        (Option.isSome (Tag.parseNeed name))
                        (sprintf "tier `%s` names `%s`, which is not a capability (see Tag.Need)" tier.Name name)

        // The gate is what it means: a tier asking for `Dogfood` would spend an hour of a master
        // push re-running this suite inside a container, and one asking for `Bench` would put a
        // timing measurement in the path of delivery. Both are opt-in by name, and `Tag.allNeeds`
        // is where that is written down.
        testCase "no tier asks for a capability the gate deliberately leaves out" <| fun () ->
            for tier in declaredTiers () do
                for name in tier.Capabilities do
                    match Tag.parseNeed name with
                    | Some need ->
                        Expect.isTrue
                            (List.contains need Tag.allNeeds)
                            (sprintf
                                "tier `%s` asks for `%s`, which `verify` leaves out on purpose — it is asked for by name, never by the gate"
                                tier.Name
                                name)
                    | None -> ()   // the rule above is what says this

        // THE promise, and the reason this file exists: the tiers together still run every suite
        // the gate is answerable for. A capability dropped from a tier, or a suite declaring a
        // combination no tier satisfies, fails here — on a pull request, in the cheap tier, naming
        // the suite that would otherwise have gone quiet.
        testCase "every suite the gate must run is run by one of its tiers" <| fun () ->
            match declaredTiers () with
            | [] -> ()   // the population case above is what says this
            | tiers ->
                for label, need in gated (Tag.declaredSuites ()) do
                    Expect.isTrue
                        (tiers |> List.exists (fun tier -> covers tier need))
                        (sprintf
                            "no tier covers `%s`, which needs %A — so the release gate would not run it anywhere, and would say nothing about not having. Add the capability to a tier in .github/verify-tiers.json (the tiers are: %s)."
                            label
                            need
                            (tiers
                             |> List.map (fun t -> sprintf "%s = %s" t.Name (String.concat " " t.Capabilities))
                             |> String.concat "; "))

        // The rule above reads suites, and a capability can be the gate's without being any suite's:
        // `NixBuild` is the build of the installable itself, which `check` runs rather than a case
        // declaring it. Dropping it from every tier would leave each suite covered and the release
        // gate never once building what it ships. So the tiers together must also NAME everything
        // `verify` means.
        testCase "every capability the gate means is asked for by one of its tiers" <| fun () ->
            match declaredTiers () with
            | [] -> ()   // the population case above is what says this
            | tiers ->
                let named = tiers |> List.collect (fun tier -> tier.Capabilities |> List.choose Tag.parseNeed)
                for need in Tag.allNeeds do
                    Expect.isTrue
                        (List.contains need named)
                        (sprintf
                            "no tier asks for `%A`, which `verify` means — so the release gate never exercises it. Add it to a tier in .github/verify-tiers.json"
                            need)

        // What `tasks.fsx bench` relies on to run the .NET CLR alone (`--runtime clr`): there is
        // nothing to measure on Node, because measuring a render needs a real browser. That is a
        // fact about how the timing suites are declared, so it is pinned where they are declared
        // rather than assumed where the saving is taken.
        testCase "a measuring suite needs a browser to measure in" <| fun () ->
            for label, need in Tag.declaredSuites () do
                if List.contains Tag.Bench need then
                    Expect.isTrue
                        (List.contains Tag.Browser need)
                        (sprintf
                            "`%s` needs Bench without Browser, so it would run on Node — which `bench` does not run at all (`--runtime clr`), because every Bench suite until now needed a browser"
                            label)
    ]
