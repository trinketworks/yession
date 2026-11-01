module Yession.Tests.Resources

// The resources algebra, under Hedgehog.
//
// Flattening a selection is where the interesting mistakes live — a cycle, a diamond, two
// grants that cannot both hold, a dangerous grant hidden inside a friendly name — and every
// one of them is decidable without touching a filesystem. So this suite needs no capability
// and runs on both runtimes, which is the point: what is believed about the algebra is
// believed here, not inferred from a later end-to-end run.
//
// The generators CONSTRUCT the shapes they need rather than filtering for them. The vendored
// Hedgehog has no `Gen.filter`, no shrinking and no `Gen.choice`, so "draw graphs until one
// is acyclic" is not available — and it would be the wrong instinct anyway, because a
// generator that rejects most of its draws tests the rejection more than the subject.

open System
open Fable.Pyxpecto
open Hedgehog
open Yession.Domain
open Yession.Domain.Agent
open Yession.Domain.Sandboxes
open Yession.Domain.Tools
open Yession.Host
open Yession.Peer
open Yession.Session

let private expect = function Ok v -> v | Error e -> failwithf "%A" e

/// The connection a repo's sandboxes forward GitHub by, under the name the session gives it.
let private github = ConnectionName.create "github" |> expect

/// A small fixed pool, small ON PURPOSE: collisions are what produce diamonds, and a diamond
/// is what the set-semantics property is about. Eight random 8-character names would generate
/// a forest and prove nothing.
let private pool =
    [| 0 .. 7 |] |> Array.map (fun i -> ResourceName.create (sprintf "r%d" i) |> expect)

let private ghost = ResourceName.create "ghost" |> expect

/// A leaf whose target belongs to resource `i` alone.
///
/// Unique by construction, so a graph built from these never conflicts with itself — which
/// is what lets the load and set-semantics properties be about cycles and diamonds rather
/// than about collisions. Conflicts get their own cases, with the colliding pair written
/// down where the reader can see it.
/// One leaf per `ResourceLeaf` case for resource `i` — the canonical list the generator
/// draws from. The match under the list is its totality proof: warnings are errors here,
/// so a seventh case added to the union fails THIS file to compile until the list carries
/// it. Before, the case count was hand-numbered into a `Range` — a new case was
/// compiler-forced through every product site and silently absent from every property in
/// this file, which is green-by-blindness, the exact fault the analyzer fixtures exist to
/// prevent.
let private leafShapesFor (i: int) (mode: ResourceMountMode) : ResourceLeaf list =
    let shapes =
        [ Mount { From = sprintf "/from/%d" i; At = sprintf "/at/%d" i; Mode = mode }
          Socket (sprintf "/run/%d.sock" i)
          Endpoint (sprintf "h%d.example.com" i)
          Variable (sprintf "V%d" i, VariableValue.Text (sprintf "value-%d" i))
          Volume (sprintf "vol%d" i, sprintf "/vol/%d" i, (if mode = ResourceMountMode.Read then mode else ResourceMountMode.Write))
          Exec (sprintf "/bin/tool%d" i)
          Connection (sprintf "conn%d" i, ConnectionRoute.Git) ]
    for shape in shapes do
        match shape with
        | Mount _ | Socket _ | Endpoint _ | Variable _ | Volume _ | Exec _ | Connection _ -> ()
    shapes

/// Every mount mode, proved total the same way.
let private mountModes : ResourceMountMode list =
    let modes = [ ResourceMountMode.Read; ResourceMountMode.Write; ResourceMountMode.Overlay ]
    for mode in modes do
        match mode with
        | ResourceMountMode.Read | ResourceMountMode.Write | ResourceMountMode.Overlay -> ()
    modes

let private genLeafFor (i: int) : Gen<ResourceLeaf> =
    gen {
        let! mode = Gen.item mountModes
        return! Gen.item (leafShapesFor i mode)
    }

/// Every host distinction, proved total the same way — both the limits generator and
/// anything else asking "what could a host claim" draw from here.
let private distinctions : HostDistinction list =
    let all =
        [ HostDistinction.SocketsByPath
          HostDistinction.EgressByHost
          HostDistinction.OverlayMounts
          HostDistinction.NamedVolumes ]
    for distinction in all do
        match distinction with
        | HostDistinction.SocketsByPath
        | HostDistinction.EgressByHost
        | HostDistinction.OverlayMounts
        | HostDistinction.NamedVolumes -> ()
    all

/// A host that can express some arbitrary subset of the distinctions.
let private genLimits : Gen<HostLimits> =
    gen {
        let! keep = Gen.list (Range.linear (List.length distinctions) (List.length distinctions)) Gen.bool
        return HostLimits.of' (List.zip distinctions keep |> List.choose (fun (d, k) -> if k then Some d else None))
    }

/// A DAG, by construction: `r i` is either a leaf or a composition drawn ONLY from
/// { r0 … r(i-1) }, so the index IS topological rank and a back edge is not expressible.
/// `r0` is forced to a leaf because it has no predecessors. Children are drawn with
/// repetition and independently per sibling, which makes "a name reached by two paths" the
/// common case rather than a corner.
let rec private buildDag (size: int) (i: int) (acc: (ResourceName * ResourceDecl) list) =
    if i >= size then Gen.constant (List.rev acc)
    else
        gen {
            let! decl =
                if i = 0 then
                    gen {
                        let! leaf = genLeafFor 0
                        return ResourceDecl.Leaf [ leaf ]
                    }
                else
                    gen {
                        let! shape = Gen.int32 (Range.linear 0 2)
                        if shape = 0 then
                            let! leaf = genLeafFor i
                            return ResourceDecl.Leaf [ leaf ]
                        else
                            let! picks = Gen.list (Range.linear 1 3) (Gen.int32 (Range.linear 0 (i - 1)))
                            return ResourceDecl.Composition (picks |> List.map (fun p -> pool.[p]))
                    }
            return! buildDag size (i + 1) ((pool.[i], decl) :: acc)
        }

let private genDag : Gen<(ResourceName * ResourceDecl) list> =
    gen {
        let! size = Gen.int32 (Range.linear 2 8)
        return! buildDag size 0 []
    }

/// A cycle, also by construction — never by adding a back edge to a DAG and hoping. An
/// injected edge from `rj` to `ri` only closes a cycle if a path from `ri` to `rj` happens to
/// exist, so the closing edges are written explicitly: pick k distinct indices and rewrite
/// each to compose the next, with the last composing the first. k = 1 gives the self-loop
/// `a = [a]`, which is exactly the degenerate case a naive visited-set flattener passes.
let private genCyclic : Gen<(ResourceName * ResourceDecl) list * ResourceName list> =
    gen {
        let! declarations = genDag
        let size = List.length declarations
        let! k = Gen.int32 (Range.linear 1 (min 4 size))
        let! start = Gen.int32 (Range.linear 0 (size - k))
        let ring = [ start .. start + k - 1 ] |> List.map (fun i -> pool.[i])
        let rewritten =
            declarations
            |> List.map (fun (name, decl) ->
                match ring |> List.tryFindIndex (fun step -> step = name) with
                | Some position -> name, ResourceDecl.Composition [ ring.[(position + 1) % List.length ring] ]
                | None -> name, decl)
        return rewritten, ring
    }

let private genDangling : Gen<(ResourceName * ResourceDecl) list> =
    gen {
        let! declarations = genDag
        let! victim = Gen.int32 (Range.linear 0 (List.length declarations - 1))
        return
            declarations
            |> List.mapi (fun i (name, decl) ->
                if i = victim then name, ResourceDecl.Composition [ ghost ] else name, decl)
    }

let private genSelection (declarations: (ResourceName * ResourceDecl) list) : Gen<ResourceName list> =
    Gen.list (Range.linear 0 4) (Gen.int32 (Range.linear 0 (List.length declarations - 1)))
    |> Gen.map (List.map (fun i -> fst declarations.[i]))

/// The oracle: a second, dumber implementation that never calls the module under test.
/// Naive expansion into a LIST with duplicates left in — the duplicates are the proof that a
/// diamond was there for dedup to remove.
let rec private expand (declarations: (ResourceName * ResourceDecl) list) (name: ResourceName) =
    match declarations |> List.tryFind (fun (declared, _) -> declared = name) with
    | None -> []
    | Some (_, ResourceDecl.Leaf leaves) -> leaves
    | Some (_, ResourceDecl.Composition children) -> children |> List.collect (expand declarations)

/// How many of these leaves ask a person: the connections, counted by matching rather than
/// through the rule under test, so the two can disagree.
let private connections (leaves: ResourceLeaf seq) : int =
    leaves
    |> Seq.filter (function
        | Connection _ -> true
        | _ -> false)
    |> Seq.length

let private check (name: string) (body: unit -> Property<unit>) = testCase name <| fun () -> Property.check (body ())

/// One resource `r`, declared as `leaf` (a JSON object), resolved.
let private resolved (leaf: string) : ResourceClosure =
    let profile =
        OperatorProfile.parse (sprintf """{ "version": 1, "resources": { "r": %s } }""" leaf) |> expect
    ResourceProfile.resolve profile.Resources [ ResourceName.create "r" |> expect ] |> expect

/// Two resources that each load and cannot hold together: one target, two modes.
let private alternatives =
    let at = { From = "/nix"; At = "/nix"; Mode = ResourceMountMode.Read }
    [ ResourceName.create "nix-ro" |> expect, ResourceDecl.Leaf [ Mount at ]
      ResourceName.create "nix-rw" |> expect, ResourceDecl.Leaf [ Mount { at with Mode = ResourceMountMode.Write } ] ]

let tests =
    testList "the resources algebra" [

        // --- loading a vocabulary -------------------------------------------------------

        check "a vocabulary built acyclically loads" <| fun () ->
            property {
                let! declarations = genDag
                match ResourceProfile.load declarations with
                | Ok _ -> ()
                | Error e -> failwithf "expected a profile, got: %s" e
            }

        // The red here also means "flattening did not terminate": a traversal that diverges
        // never returns to be asserted on.
        check "a vocabulary whose resources contain themselves is refused, naming one on the cycle" <| fun () ->
            property {
                let! declarations, ring = genCyclic
                match ResourceProfile.load declarations with
                | Ok _ -> failwith "expected a refusal"
                | Error e ->
                    let named = ring |> List.exists (fun name -> e.Contains (ResourceName.value name))
                    Expect.isTrue named (sprintf "the refusal names a resource on the cycle, said: %s" e)
            }

        check "a composition naming a resource nobody declares is refused, naming it" <| fun () ->
            property {
                let! declarations = genDangling
                match ResourceProfile.load declarations with
                | Ok _ -> failwith "expected a refusal"
                | Error e -> Expect.isTrue (e.Contains "ghost") (sprintf "the refusal names the missing resource, said: %s" e)
            }

        check "a name declared twice is refused rather than silently losing one declaration" <| fun () ->
            property {
                let! declarations = genDag
                let (name, decl) = List.head declarations
                match ResourceProfile.load (declarations @ [ name, decl ]) with
                | Ok _ -> failwith "expected a refusal"
                | Error e ->
                    Expect.isTrue
                        (e.Contains (ResourceName.value name))
                        (sprintf "the refusal names the duplicated resource, said: %s" e)
            }

        // --- flattening ------------------------------------------------------------------

        check "the closure is the naive expansion of the selection, deduplicated" <| fun () ->
            property {
                let! declarations = genDag
                let! selection = genSelection declarations
                let profile = ResourceProfile.load declarations |> expect
                let closure = ResourceProfile.resolve profile selection |> expect
                let oracle = selection |> List.collect (expand declarations) |> Set.ofList
                Expect.equal (ResourceClosure.leaves closure) oracle "every leaf the naive walk reaches, once each"
            }

        check "a resource selected twice resolves to exactly the closure it does once" <| fun () ->
            property {
                let! declarations = genDag
                let! selection = genSelection declarations
                let profile = ResourceProfile.load declarations |> expect
                Expect.equal
                    (ResourceProfile.resolve profile (selection @ selection) |> expect |> ResourceClosure.leaves)
                    (ResourceProfile.resolve profile selection |> expect |> ResourceClosure.leaves)
                    "asking twice is asking once"
            }

        check "the order of a selection does not change what it grants" <| fun () ->
            property {
                let! declarations = genDag
                let! selection = genSelection declarations
                let profile = ResourceProfile.load declarations |> expect
                Expect.equal
                    (ResourceProfile.resolve profile (List.rev selection) |> expect |> ResourceClosure.leaves)
                    (ResourceProfile.resolve profile selection |> expect |> ResourceClosure.leaves)
                    "a set, not a sequence"
            }

        // --- attenuation -----------------------------------------------------------------

        check "the closure of any selection is inside the whole vocabulary" <| fun () ->
            property {
                let! declarations = genDag
                let! selection = genSelection declarations
                let profile = ResourceProfile.load declarations |> expect
                let closure = ResourceProfile.resolve profile selection |> expect
                Expect.isTrue
                    (Set.isSubset (ResourceClosure.leaves closure) (ResourceProfile.ceiling profile))
                    "nothing a selection reaches is outside what the operator declared"
            }

        check "asking for a resource the operator did not declare is refused, listing what is" <| fun () ->
            property {
                let! declarations = genDag
                let! selection = genSelection declarations
                let profile = ResourceProfile.load declarations |> expect
                match ResourceProfile.resolve profile (selection @ [ ghost ]) with
                | Ok _ -> failwith "expected a refusal"
                | Error e ->
                    Expect.isTrue (e.Contains "ghost") (sprintf "the refusal names what was asked for, said: %s" e)
                    Expect.isTrue
                        (e.Contains (ResourceName.value (fst (List.head declarations))))
                        (sprintf "and lists what there is instead, said: %s" e)
            }

        // --- monotonicity ----------------------------------------------------------------

        check "adding a resource to a selection never removes a leaf" <| fun () ->
            property {
                let! declarations = genDag
                let! selection = genSelection declarations
                let! extra = Gen.int32 (Range.linear 0 (List.length declarations - 1))
                let profile = ResourceProfile.load declarations |> expect
                let before = ResourceProfile.resolve profile selection |> expect
                let after = ResourceProfile.resolve profile (selection @ [ fst declarations.[extra] ]) |> expect
                Expect.isTrue
                    (Set.isSubset (ResourceClosure.leaves before) (ResourceClosure.leaves after))
                    "a longer ask grants at least as much"
            }

        // Without this, the case above could be vacuous — it only asserts anything when both
        // resolves succeed, and a `resolve` that refused everything would satisfy it.
        check "adding a resource to a refused selection never rescues it" <| fun () ->
            property {
                let! declarations = genDag
                let! selection = genSelection declarations
                let! extra = Gen.int32 (Range.linear 0 (List.length declarations - 1))
                let profile = ResourceProfile.load declarations |> expect
                match ResourceProfile.resolve profile (ghost :: selection) with
                | Ok _ -> failwith "a selection naming an undeclared resource must not resolve"
                | Error _ ->
                    match ResourceProfile.resolve profile (ghost :: selection @ [ fst declarations.[extra] ]) with
                    | Ok _ -> failwith "adding a declared resource must not rescue an undeclared one"
                    | Error _ -> ()
            }

        // --- conflict ---------------------------------------------------------------------

        check "no closure ever holds two grants on one target" <| fun () ->
            property {
                let! declarations = genDag
                let! selection = genSelection declarations
                let profile = ResourceProfile.load declarations |> expect
                let closure = ResourceProfile.resolve profile selection |> expect
                Expect.equal
                    (ResourceLeaf.conflicts (ResourceClosure.leaves closure))
                    []
                    "a resolved closure is materialisable"
            }

        // The operator contradicting themselves, refused where they are standing.
        check "a resource whose own closure mounts one target two ways is refused at load" <| fun () ->
            property {
                let! declarations = genDag
                let both = ResourceName.create "both" |> expect
                let clashing = alternatives @ [ both, ResourceDecl.Composition (alternatives |> List.map fst) ]
                match ResourceProfile.load (declarations @ clashing) with
                | Ok _ -> failwith "expected a refusal"
                | Error e ->
                    Expect.isTrue (e.Contains "both") (sprintf "the refusal names the resource, said: %s" e)
                    Expect.isTrue (e.Contains "/nix") (sprintf "and the target they collide on, said: %s" e)
            }

        check "a resource whose own closure sets one variable two ways is refused at load" <| fun () ->
            property {
                let! declarations = genDag
                let a = ResourceName.create "cc-clang" |> expect
                let b = ResourceName.create "cc-gcc" |> expect
                let both = ResourceName.create "cc-both" |> expect
                let clashing =
                    [ a, ResourceDecl.Leaf [ Variable ("CC", VariableValue.Text "clang") ]
                      b, ResourceDecl.Leaf [ Variable ("CC", VariableValue.Text "gcc") ]
                      both, ResourceDecl.Composition [ a; b ] ]
                match ResourceProfile.load (declarations @ clashing) with
                | Ok _ -> failwith "expected a refusal"
                | Error e -> Expect.isTrue (e.Contains "CC") (sprintf "the refusal names the variable, said: %s" e)
            }

        // The REPO's mistake, and it cannot exist until a selection does — which is why the
        // operator's vocabulary is allowed to hold both.
        check "two resources that cannot hold together load, and are refused at selection" <| fun () ->
            property {
                let! declarations = genDag
                let profile = ResourceProfile.load (declarations @ alternatives) |> expect
                match ResourceProfile.resolve profile (alternatives |> List.map fst) with
                | Ok _ -> failwith "expected a refusal"
                | Error e -> Expect.isTrue (e.Contains "/nix") (sprintf "the refusal names the target, said: %s" e)
            }

        // --- who is asked ------------------------------------------------------------------
        //
        // A connection is spent as the person a sandbox's commands run for, so a selection
        // holding one waits for a person to approve it. Everything else a profile grants is
        // the operator's, and the session's user is not asked about it (invariant 25).

        // The oracle is the naive walk again, so a rule that missed a connection somewhere
        // down a composition, or started asking about another kind, reads as a disagreement.
        check "a selection asks its user exactly when it holds a connection" <| fun () ->
            property {
                let! declarations = genDag
                let! selection = genSelection declarations
                let profile = ResourceProfile.load declarations |> expect
                let closure = ResourceProfile.resolve profile selection |> expect
                Expect.equal
                    (ResourceClosure.asksItsUser closure)
                    (connections (selection |> List.collect (expand declarations)) > 0)
                    "the naive walk and the closure agree"
            }

        // Whether a selection asks is read off its leaves' kinds alone, and a closure holds
        // nothing beside its leaves, so no composition, second route or declaration can raise
        // or lower it. The cases that pinned a sensitivity mark through each of those went
        // with the mark. A profile that still writes `sensitive:` is refused (below).

        // --- the syntax an operator writes ---------------------------------------------------
        //
        // From JSON literals, like `Config.fs`'s own cases: what a YAML front end does with a
        // document is its business, and pinning the SCHEMA does not need one.

        testCase "an object is a leaf and an array is a composition" <| fun () ->
            let profile =
                OperatorProfile.parse """
                    { "version": 1,
                      "resources": { "store": { "mount": { "from": "/nix" } },
                                     "daemon": { "socket": "/nix/var/nix/daemon-socket" },
                                     "nix": [ "store", "daemon" ] } }"""
                |> expect
            let nix = ResourceName.create "nix" |> expect
            let closure = ResourceProfile.resolve profile.Resources [ nix ] |> expect
            Expect.equal
                (ResourceClosure.leaves closure)
                (Set.ofList
                    [ Mount { From = "/nix"; At = "/nix"; Mode = ResourceMountMode.Read }
                      Socket "/nix/var/nix/daemon-socket" ])
                "the composition flattens to both leaves, and neither is a name any more"

        // A cache is a mount and an endpoint and the variable pointing a tool at it. Three
        // names an operator then composes would be three names for one idea, none of which
        // means anything alone.
        testCase "one leaf may declare several primitives" <| fun () ->
            let profile =
                OperatorProfile.parse """
                    { "version": 1,
                      "resources": { "npm": { "mount": { "from": "/h/.npm", "mode": "overlay" },
                                              "endpoint": "registry.npmjs.org",
                                              "env": { "npm_config_cache": "/h/.npm" } } } }"""
                |> expect
            let closure = ResourceProfile.resolve profile.Resources [ ResourceName.create "npm" |> expect ] |> expect
            Expect.equal (Set.count (ResourceClosure.leaves closure)) 3 "a mount, an endpoint and a variable"

        testCase "at defaults to from, so a path that means the same on both sides is written once" <| fun () ->
            let profile =
                OperatorProfile.parse """{ "version": 1, "resources": { "s": { "mount": { "from": "/nix" } } } }"""
                |> expect
            match ResourceProfile.resolve profile.Resources [ ResourceName.create "s" |> expect ] |> expect |> ResourceClosure.leaves |> Set.toList with
            | [ Mount mount ] -> Expect.equal mount.At "/nix" "the target is the source unless it is named"
            | other -> failwithf "expected one mount, got %A" other

        // A named volume is host-global and persistent — the sharing is the point — so it
        // is a thing only an operator's file may put on the table, and both halves are
        // required: a volume with no target is a thing with nowhere to be.
        testCase "a volume resource decodes with its name and target" <| fun () ->
            let profile =
                OperatorProfile.parse
                    """{ "version": 1, "resources": { "warm-store": { "volume": { "name": "yession-nix", "at": "/nix", "mode": "write" } } } }"""
                |> expect
            match ResourceProfile.resolve profile.Resources [ ResourceName.create "warm-store" |> expect ] |> expect |> ResourceClosure.leaves |> Set.toList with
            | [ Volume (name, at, _) ] ->
                Expect.equal name "yession-nix" "docker's name for it"
                Expect.equal at "/nix" "and where the operator says it belongs"
            | other -> failwithf "expected one volume, got %A" other

        // Maintenance is a fact about the volume, not the grant: what a sandbox is told it
        // holds does not change, and the pin is looked up by the volume's name.
        testCase "a volume's maintain decodes onto the profile by volume name, leaving the leaf as it was" <| fun () ->
            let profile =
                OperatorProfile.parse
                    """{ "version": 1, "resources": { "warm-store": { "volume": { "name": "yession-nix", "at": "/nix", "mode": "write", "maintain": { "pin": "/nix/var/yession/pin", "every": "10m" } } } } }"""
                |> expect
            Expect.equal
                (Map.tryFind "yession-nix" profile.Maintenance)
                (Some { Pin = "/nix/var/yession/pin"; Every = TimeSpan.FromMinutes 10.0 })
                "the pin and its interval, under docker's name for the volume"
            match ResourceProfile.resolve profile.Resources [ ResourceName.create "warm-store" |> expect ] |> expect |> ResourceClosure.leaves |> Set.toList with
            | [ Volume (name, at, _) ] -> Expect.equal (name, at) ("yession-nix", "/nix") "the grant is the plain volume"
            | other -> failwithf "expected one volume, got %A" other

        testCase "a volume with no maintain is not maintained" <| fun () ->
            let profile =
                OperatorProfile.parse """{ "version": 1, "resources": { "v": { "volume": { "name": "x", "at": "/x", "mode": "write" } } } }"""
                |> expect
            Expect.isEmpty profile.Maintenance "nothing is run in a sandbox the operator did not ask for"

        // Whether a repository may write what every other session builds from is the
        // operator's decision, so the profile makes them write it down.
        testCase "a volume with no mode is refused, naming the modes a volume has" <| fun () ->
            match OperatorProfile.parse """{ "version": 1, "resources": { "v": { "volume": { "name": "x", "at": "/x" } } } }""" with
            | Ok _ -> failwith "a volume with no mode decoded"
            | Error e ->
                Expect.isTrue
                    (e.Contains "mode: read" && e.Contains "mode: write")
                    (sprintf "the refusal names read and write, said: %s" e)

        testCase "a volume with mode: read decodes read" <| fun () ->
            match resolved """{ "volume": { "name": "x", "at": "/x", "mode": "read" } }""" |> ResourceClosure.leaves |> Set.toList with
            | [ Volume (_, _, mode) ] -> Expect.equal mode ResourceMountMode.Read "the operator's word"
            | other -> failwithf "expected one volume, got %A" other

        testCase "an overlay volume is refused, naming the modes a volume has" <| fun () ->
            match
                OperatorProfile.parse
                    """{ "version": 1, "resources": { "v": { "volume": { "name": "x", "at": "/x", "mode": "overlay" } } } }"""
            with
            | Ok _ -> failwith "an overlay volume decoded"
            | Error e ->
                Expect.isTrue
                    (e.Contains "read" && e.Contains "write")
                    (sprintf "the refusal names read and write, said: %s" e)

        // A lease is written into the volume, so a pin in a sandbox that cannot write it
        // fails every time it runs.
        testCase "a read volume cannot be maintained" <| fun () ->
            Expect.isError
                (OperatorProfile.parse
                    """{ "version": 1, "resources": { "v": { "volume": { "name": "x", "at": "/x", "mode": "read", "maintain": { "pin": "/p", "every": "10m" } } } } }""")
                "its lease has nowhere to be written"

        testCase "maintain refuses what it cannot run: a relative pin, a unitless or zero interval, an unknown key" <| fun () ->
            let withMaintain (m: string) =
                OperatorProfile.parse (sprintf """{ "version": 1, "resources": { "v": { "volume": { "name": "x", "at": "/x", "mode": "write", "maintain": %s } } } }""" m)
            Expect.isError (withMaintain """{ "pin": "pin.sh", "every": "10m" }""") "relative to whatever the container's working directory is"
            Expect.isError (withMaintain """{ "pin": "/p", "every": "10" }""") "ten what: a unit is always written"
            Expect.isError (withMaintain """{ "pin": "/p", "every": "0m" }""") "a pin run continuously"
            Expect.isError (withMaintain """{ "pin": "/p", "every": "1d" }""") "s, m or h"
            Expect.isError (withMaintain """{ "pin": "/p" }""") "an interval is required"
            Expect.isError (withMaintain """{ "pin": "/p", "every": "1h", "keep": "7d" }""") "retention is the sweep's, not the product's"
            Expect.isOk (withMaintain """{ "pin": "/p", "every": "90s" }""") "seconds are a unit"

        testCase "one volume maintained two ways is refused; the same way twice is one maintenance" <| fun () ->
            let two (a: string) (b: string) =
                OperatorProfile.parse (
                    sprintf
                        """{ "version": 1, "resources": { "a": { "volume": { "name": "x", "at": "/x", "mode": "write", "maintain": %s } }, "b": { "volume": { "name": "x", "at": "/x", "mode": "write", "maintain": %s } } } }"""
                        a b)
            Expect.isError
                (two """{ "pin": "/p", "every": "10m" }""" """{ "pin": "/q", "every": "10m" }""")
                "which pin runs would depend on which resource a repo selected"
            Expect.equal
                (two """{ "pin": "/p", "every": "10m" }""" """{ "pin": "/p", "every": "10m" }""" |> Result.map (fun p -> Map.count p.Maintenance))
                (Ok 1)
                "agreeing declarations are one"

        testCase "a volume with no target is refused" <| fun () ->
            Expect.isError
                (OperatorProfile.parse """{ "version": 1, "resources": { "v": { "volume": { "name": "x", "mode": "write" } } } }""")
                "the operator is the one author who knows where it belongs"

        testCase "a resource that grants nothing is refused, not decoded as an empty one" <| fun () ->
            // A name that reads as configuration and is none — the same failure an unknown
            // key would be, arriving by another route.
            Expect.isError
                (OperatorProfile.parse """{ "version": 1, "resources": { "hollow": { } } }""")
                "an empty leaf is not a resource"

        testCase "an unknown key inside a resource is refused, not skipped" <| fun () ->
            // The resource grants something REAL as well, so the only thing that can refuse
            // this is the unknown key. The first draft named only the typo, which left the
            // "grants nothing" refusal catching it and this case green with the key check
            // deleted — a test passing for a reason that is not its name.
            Expect.isError
                (OperatorProfile.parse """
                    { "version": 1, "resources": { "s": { "mount": { "from": "/nix" }, "mounts": "/x" } } }""")
                "a misspelled key fails the file even when the rest of the resource is fine"

        // `sensitive` once marked a resource to be asked about, and a kind decides that now. A
        // file still carrying it says something about who is asked, so its operator is told
        // by name what became of that, whatever the mark said, rather than handed the
        // unknown-key sentence a typo gets.
        testCase "a resource that still marks itself sensitive is refused by name" <| fun () ->
            for mark in [ "true"; "false" ] do
                match
                    OperatorProfile.parse (
                        sprintf
                            """{ "version": 1, "resources": { "s": { "mount": { "from": "/nix" }, "sensitive": %s } } }"""
                            mark)
                with
                | Ok _ -> failwithf "sensitive: %s was accepted" mark
                | Error e ->
                    Expect.stringContains e "'sensitive'" (sprintf "the refusal names the key, said: %s" e)
                    Expect.isFalse (e.Contains "unknown") (sprintf "and is not the typo's sentence, said: %s" e)

        testCase "a mount mode this build does not know is refused, naming the three there are" <| fun () ->
            match OperatorProfile.parse """{ "version": 1, "resources": { "s": { "mount": { "from": "/n", "mode": "rw" } } } }""" with
            | Ok _ -> failwith "expected a refusal"
            | Error e -> Expect.isTrue (e.Contains "overlay") (sprintf "the refusal lists the modes, said: %s" e)

        // The one block in this file that is not about resources: what the operator says about
        // the agent. Its words are read as written apart from the newline a YAML block scalar
        // leaves on the end, and absent — not empty — when the block is not there, so the
        // prompt that appends them can tell "wrote nothing" from "wrote an empty string".
        testCase "an operator's guidance is read as written, and absent when unwritten" <| fun () ->
            let profile =
                OperatorProfile.parse """
                    { "version": 1, "resources": {},
                      "agent": { "guidance": "Never push to main on this host.\n" } }"""
                |> expect
            Expect.equal profile.Agent.Guidance (Some "Never push to main on this host.") "the words, without the scalar's closing newline"
            let silent = OperatorProfile.parse """{ "version": 1, "resources": {} }""" |> expect
            Expect.equal silent.Agent.Guidance None "no block is no words"

        // The same failure a hollow resource is: a block that reads as configuration and is
        // none. Both spellings of nothing — no key, and a key with only whitespace behind it.
        testCase "an agent block that says nothing is refused, not read as no guidance" <| fun () ->
            for body in [ """{ }"""; """{ "guidance": "   \n" }""" ] do
                match OperatorProfile.parse (sprintf """{ "version": 1, "resources": {}, "agent": %s }""" body) with
                | Ok _ -> failwithf "%s was accepted, and it says nothing" body
                | Error e -> Expect.isTrue (e.Contains "guidance") (sprintf "the refusal names the key, said: %s" e)

        testCase "an unknown key inside the agent block is refused, not skipped" <| fun () ->
            Expect.isError
                (OperatorProfile.parse """
                    { "version": 1, "resources": {}, "agent": { "guidance": "x", "promt": "static" } }""")
                "a misspelled key fails the file even when the guidance beside it is fine"

        // The name an operator writes is the name a boot reports (`PromptStrategy.name`), and
        // every strategy the build has can be chosen by it, with or without guidance beside it.
        testCase "every prompt strategy is chosen by the name it is reported under" <| fun () ->
            let chosen (strategy: PromptStrategy) =
                OperatorProfile.parse (
                    sprintf """{ "version": 1, "resources": {}, "agent": { "prompt": "%s" } }""" (PromptStrategy.name strategy))
                |> expect
                |> fun profile -> profile.Agent.Prompt
            Expect.equal (PromptStrategy.all |> List.map chosen) PromptStrategy.all "each name reads back as its strategy"

        // A host that names no strategy runs the one every host ran before there was a choice.
        testCase "a profile that names no prompt strategy gets the static one" <| fun () ->
            for agent in [ ""; """, "agent": { "guidance": "x" }""" ] do
                let profile = OperatorProfile.parse (sprintf """{ "version": 1, "resources": {}%s }""" agent) |> expect
                Expect.equal profile.Agent.Prompt PromptStrategy.Static (sprintf "unnamed in %s" (if agent = "" then "a profile with no agent block" else agent))

        // A typo must not run the agent on a prompt nobody chose, and the refusal says what
        // there is to choose from.
        testCase "a prompt strategy this build does not have is refused, naming the ones it has" <| fun () ->
            match OperatorProfile.parse """{ "version": 1, "resources": {}, "agent": { "prompt": "claude-code" } }""" with
            | Ok _ -> failwith "expected a refusal"
            | Error e ->
                for strategy in PromptStrategy.all do
                    Expect.stringContains e $"{PromptStrategy.name strategy}" "the refusal lists every strategy"

        // The policy the file's two halves express: `resources` is the menu, `always` is what
        // is served whether or not anybody ordered it. A name in one and not the other is the
        // state this model exists to make expressible — available, and not granted.
        testCase "what a host always grants is one selection from its own menu" <| fun () ->
            let profile =
                OperatorProfile.parse """
                    { "version": 1,
                      "resources": { "store": { "mount": { "from": "/nix" } },
                                     "docker": { "socket": "/run/docker.sock" } },
                      "always": [ "store" ] }"""
                |> expect
            Expect.equal
                (profile.Always |> List.map ResourceName.value)
                [ "store" ]
                "the one that is handed out, and not the one that is merely offered"

        // Refused while the operator is looking at the file, rather than at whichever
        // session first fails to start with it.
        testCase "a host cannot always grant something it does not declare" <| fun () ->
            match OperatorProfile.parse """{ "version": 1, "resources": {}, "always": [ "ghost" ] }""" with
            | Ok _ -> failwith "expected a refusal"
            | Error e -> Expect.isTrue (e.Contains "ghost") (sprintf "the refusal names it, said: %s" e)

        // `always` was `default`, and an operator with the old spelling must be told which
        // word replaced it rather than that a key is unknown — the courtesy a variable moved
        // onto the command line already gets.
        testCase "the spelling this key had before is refused by name" <| fun () ->
            match OperatorProfile.parse """{ "version": 1, "resources": {}, "default": [] }""" with
            | Ok _ -> failwith "expected a refusal"
            | Error e ->
                Expect.isTrue (e.Contains "always") (sprintf "the refusal names the word to write, said: %s" e)
                // Not "unknown key: default (known: version, resources, always)", which names
                // the new word only by listing every word there is, and leaves the operator
                // to work out that one replaced the other.
                Expect.isFalse (e.Contains "unknown") (sprintf "and says it was replaced, not that it is a typo, said: %s" e)

        testCase "a version this build does not speak is refused" <| fun () ->
            Expect.isError (OperatorProfile.parse """{ "version": 2, "resources": {} }""") "not read as a lossy version 1"

        // The algebra's refusals arrive through `load` rather than being re-implemented here.
        // A decoder with its own copy of them is a second mechanism free to disagree with the
        // first — so this asserts they REACH the operator, not that they exist twice.
        testCase "a cycle in a profile is refused by the algebra, through the decoder" <| fun () ->
            match OperatorProfile.parse """{ "version": 1, "resources": { "a": [ "b" ], "b": [ "a" ] } }""" with
            | Ok _ -> failwith "expected a refusal"
            | Error e -> Expect.isTrue (e.Contains "->") (sprintf "and the refusal carries the path, said: %s" e)

        testCase "a composition naming an undeclared resource is refused, through the decoder" <| fun () ->
            Expect.isError
                (OperatorProfile.parse """{ "version": 1, "resources": { "a": [ "nope" ] } }""")
                "the algebra's dangling-name refusal reaches the operator"

        // The refusal that REPLACED the ceiling. There is no list-versus-list comparison any
        // more: a repo cannot exceed what an operator offered because it can only name what
        // an operator offered, and a name it was not offered does not resolve. That is a
        // stronger arrangement than comparing two lists and hoping the comparison is right.
        testCase "a repo selecting something this host does not declare is refused, and told what there is" <| fun () ->
            let profile =
                OperatorProfile.parse """
                    { "version": 1, "resources": { "nix": { "mount": { "from": "/nix" } } } }"""
                |> expect
            match ResourceProfile.resolve profile.Resources [ ResourceName.create "npm" |> expect ] with
            | Ok _ -> failwith "expected a refusal"
            | Error e ->
                Expect.isTrue (e.Contains "npm") (sprintf "the refusal names what was asked for, said: %s" e)
                Expect.isTrue (e.Contains "nix") (sprintf "and lists what this host has, said: %s" e)

        // --- what the operator is shown ------------------------------------------------------

        // The row an operator reads is the CLOSURE, not the line they wrote. A composite whose
        // whole content is one other name has to say what that name comes to, or the surface
        // answers a question nobody asked.
        testCase "a resource's row says what selecting it finally grants" <| fun () ->
            let profile =
                OperatorProfile.parse """
                    { "version": 1,
                      "resources": { "store": { "mount": { "from": "/nix" } },
                                     "daemon": { "socket": "/nix/var/nix/daemon-socket" },
                                     "nix": [ "store", "daemon" ] } }"""
                |> expect
            let cellOf name key =
                OperatorResources.rows profile
                |> List.find (fun row -> row |> List.contains ("resource", CellText name))
                |> List.pick (fun (column, cell) -> if column = key then Some cell else None)
            match cellOf "nix" "grants" with
            | CellText grants ->
                Expect.isTrue (grants.Contains "path:/nix:ro") (sprintf "the mount it reaches, said: %s" grants)
                Expect.isTrue (grants.Contains "daemon-socket") (sprintf "and the socket, said: %s" grants)
            | other -> failwithf "expected text, got %A" other

        // Declared and not granted is a real state, and the row is the only place an operator
        // can see which of their names they are handing out unasked.
        testCase "a resource this host always grants says so, and one it merely offers does not" <| fun () ->
            let profile =
                OperatorProfile.parse """
                    { "version": 1,
                      "resources": { "store": { "mount": { "from": "/nix" } },
                                     "docker": { "socket": "/run/docker.sock" } },
                      "always": [ "store" ] }"""
                |> expect
            let always name =
                OperatorResources.rows profile
                |> List.find (fun row -> row |> List.contains ("resource", CellText name))
                |> List.pick (fun (column, cell) -> if column = "always" then Some cell else None)
            Expect.equal (always "store") (CellText "yes") "handed out without asking"
            Expect.equal (always "docker") CellAbsent "on the menu, and not on the table"

        // A row has no sensitive column any more: no resource is marked, and a grant that asks
        // is marked on its own line in `grants`, which the describing property below pins.

        // --- what a person is shown --------------------------------------------------------

        // The notation itself, pinned as a whole. Its red means "the notation changed", which
        // is a thing to decide rather than a thing to fix — every surface that shows a grant
        // shows these words, and a person who has learned to read one has learned the others.
        //
        // A file and a directory are ONE kind on purpose: telling them apart needs a stat,
        // the module has no IO, and the kernel does not make the distinction either.
        testCase "a grant is written as its kind, then what it names" <| fun () ->
            Expect.equal
                ([ Mount { From = "/nix"; At = "/nix"; Mode = ResourceMountMode.Read }
                   Mount { From = "/h/.npm"; At = "/root/.npm"; Mode = ResourceMountMode.Overlay }
                   Mount { From = "/w"; At = "/w"; Mode = ResourceMountMode.Write }
                   Socket "/run/docker.sock"
                   Endpoint "registry.npmjs.org"
                   Volume ("yession-nix", "/nix", ResourceMountMode.Write)
                   Variable ("CI", VariableValue.Text "1")
                   Exec "/usr/bin/git" ]
                 |> List.map ResourceLeaf.describe)
                [ "path:/nix:ro"
                  "path:/h/.npm>/root/.npm:ovl"
                  "path:/w:rw"
                  "sock:/run/docker.sock"
                  "net:registry.npmjs.org"
                  "vol:yession-nix>/nix:rw"
                  "env:CI=1"
                  "exec:/usr/bin/git" ]
                "the notation"

        // A variable's value is the one part of a grant that is arbitrary text, so it is the
        // one part that can carry whatever a reader splits a list of grants on. Quoted only
        // where it has to be: an `env:` line that wore quotes every time would teach people
        // to read past them, which is how the one that means something gets missed.
        testCase "a value that could be mistaken for the end of a grant is quoted" <| fun () ->
            Expect.equal
                ([ Variable ("A", VariableValue.Text "1")
                   Variable ("B", VariableValue.Text "a b")
                   Variable ("C", VariableValue.Text "x;y")
                   Variable ("D", VariableValue.Text "say \"hi\"") ]
                 |> List.map ResourceLeaf.describe)
                [ "env:A=1"; "env:B=\"a b\""; "env:C=\"x;y\""; "env:D=\"say \\\"hi\\\"\"" ]
                "quoted exactly where it is ambiguous"

        // The legend is what makes a short token readable, so a kind the notation writes and
        // the legend does not describe is a token nobody can decode. The compiler covers the
        // other direction — an entry is a match on a leaf, so a seventh primitive does not
        // build until it has one — and this covers the half the compiler cannot see: that the
        // shape written in the legend is the shape the renderer actually emits.
        testCase "the legend names every kind the notation writes" <| fun () ->
            for leaf in
                [ Mount { From = "/nix"; At = "/nix"; Mode = ResourceMountMode.Read }
                  Socket "/run/docker.sock"
                  Endpoint "registry.npmjs.org"
                  Volume ("yession-nix", "/nix", ResourceMountMode.Write)
                  Variable ("CI", VariableValue.Text "1")
                  Exec "/usr/bin/git" ] do
                let written = ResourceLeaf.describe leaf
                let kind = written.Substring (0, written.IndexOf ':' + 1)
                Expect.isTrue
                    (GrantNotation.legend |> List.exists (fun (shape, _) -> shape.StartsWith kind))
                    (sprintf "%s is written but the legend does not say what %s means" written kind)

        // A legend beside a decision is read only while it is short enough to read, so the
        // one shown over a consent prompt is the entries THOSE lines use. Its red means the
        // filter has stopped tracking what the renderer writes — a token nobody can decode,
        // or seven entries about grants nobody was offered.
        testCase "a legend for particular grants holds what they use, and nothing else" <| fun () ->
            let shapes =
                GrantNotation.legendFor [ "!conn:github/git"; "sock:/run/docker.sock ~> sock:*"; "path:/nix:ro" ]
                |> List.map fst
            Expect.equal
                shapes
                [ "path:PATH[>AT]:ro|rw|ovl"; "sock:PATH"; "conn:NAME/ROUTE"; "!GRANT"; "GRANT ~> OTHER" ]
                "the three kinds and both marks, in reading order"

        // The empty case is the one that would go unnoticed: a surface with nothing to
        // explain must render no legend at all, rather than a heading over the vocabulary.
        testCase "grants that need no explaining ask for no legend" <| fun () ->
            Expect.equal (GrantNotation.legendFor []) [] "nothing shown, nothing to read"

        // A leaf that materialises and was never shown is the fault this module exists to
        // prevent. Counts leaves and marks; never the wording, which is a design and moves.
        check "every leaf in a closure is described, and every connection is marked" <| fun () ->
            property {
                let! declarations = genDag
                let! selection = genSelection declarations
                let profile = ResourceProfile.load declarations |> expect
                let closure = ResourceProfile.resolve profile selection |> expect
                let lines = ResourceClosure.describe closure
                Expect.equal
                    (List.length lines)
                    (Set.count (ResourceClosure.leaves closure))
                    "one line per leaf, and no leaf without one"
                Expect.equal
                    (lines |> List.filter (fun line -> line.StartsWith "!") |> List.length)
                    (connections (ResourceClosure.leaves closure))
                    "and every connection carries the mark, and nothing else does"
            }

        // --- the third author: what this host can actually express -------------------------

        // The rule that makes this an algebra rather than a table of platform quirks: a leaf
        // is realised exactly when the host can make the distinction its KIND requires. A
        // host that can make all of them changes nothing at all — which is what says the
        // machinery costs nothing where it is not needed.
        check "a host that can express everything grants exactly what was asked" <| fun () ->
            property {
                let! declarations = genDag
                let! selection = genSelection declarations
                let profile = ResourceProfile.load declarations |> expect
                let closure = ResourceProfile.resolve profile selection |> expect
                let realised = RealisedClosure.of' HostLimits.unlimited (ResourceClosure.leaves closure)
                Expect.equal (RealisedClosure.differences realised) [] "nothing differs"
                Expect.equal (RealisedClosure.held realised) (ResourceClosure.leaves closure)
                    "and everything asked for is held"
            }

        // The leaves that need no distinction are the ones every backend expresses the same
        // way, and they must never appear as a difference however poor the host. Without this
        // the rule could quietly grow into "anything might degrade", which is a table again.
        check "a mount, a variable and an executable are the same grant on every host" <| fun () ->
            property {
                let! declarations = genDag
                let! selection = genSelection declarations
                let! limits = genLimits
                let profile = ResourceProfile.load declarations |> expect
                let closure = ResourceProfile.resolve profile selection |> expect
                let realised = RealisedClosure.of' limits (ResourceClosure.leaves closure)
                let differing = RealisedClosure.differences realised |> List.map fst
                for leaf in differing do
                    match leaf with
                    | Variable _
                    | Exec _
                    | Connection _ -> failwithf "%A cannot depend on the host" leaf
                    | Mount mount ->
                        Expect.equal mount.Mode ResourceMountMode.Overlay
                            "only an overlay mount can, and only because no backend has a union mount"
                    | Socket _
                    | Endpoint _
                    | Volume _ -> ()
            }

        // Every difference is one of the two directions, and which one is not a detail: a
        // coarsening is still HELD — wider than asked — and a withholding is not held at all.
        // A materialiser that dropped a coarsened leaf would give a sandbox less than the
        // person approved rather than more, which is the opposite mistake and just as wrong.
        check "a coarsened grant is still held, a withheld one is not" <| fun () ->
            property {
                let! declarations = genDag
                let! selection = genSelection declarations
                let! limits = genLimits
                let profile = ResourceProfile.load declarations |> expect
                let closure = ResourceProfile.resolve profile selection |> expect
                let realised = RealisedClosure.of' limits (ResourceClosure.leaves closure)
                let held = RealisedClosure.held realised
                for leaf, outcome in RealisedClosure.differences realised do
                    match outcome with
                    | LeafRealisation.Coarsened _ ->
                        Expect.isTrue (Set.contains leaf held) (sprintf "%A is wider, not absent" leaf)
                    | LeafRealisation.Withheld _ ->
                        Expect.isFalse (Set.contains leaf held) (sprintf "%A is not granted" leaf)
                    | LeafRealisation.AsAsked -> failwith "an unchanged leaf is not a difference"
            }

        // Held is always a subset of asked. The coarsening is a widening of what ONE leaf
        // means, never a new leaf — so nothing a person was not shown can appear here, which
        // is the invariant the whole module exists for, still true after a third author.
        check "a host never adds a leaf nobody asked for" <| fun () ->
            property {
                let! declarations = genDag
                let! selection = genSelection declarations
                let! limits = genLimits
                let profile = ResourceProfile.load declarations |> expect
                let closure = ResourceProfile.resolve profile selection |> expect
                let realised = RealisedClosure.of' limits (ResourceClosure.leaves closure)
                Expect.isTrue
                    (Set.isSubset (RealisedClosure.held realised) (ResourceClosure.leaves closure))
                    "held is within asked"
            }

        // Realising is a projection: putting an answer through the same host again changes
        // nothing. Without this a caller could not safely re-derive the actual grant, which is
        // what a page re-render and a sandbox restart both do.
        check "realising the same selection twice says the same thing" <| fun () ->
            property {
                let! declarations = genDag
                let! selection = genSelection declarations
                let! limits = genLimits
                let profile = ResourceProfile.load declarations |> expect
                let closure = ResourceProfile.resolve profile selection |> expect
                let once = RealisedClosure.of' limits (ResourceClosure.leaves closure)
                let twice = RealisedClosure.of' limits (ResourceClosure.leaves (ResourceProfile.resolve profile selection |> expect))
                Expect.equal (RealisedClosure.differences twice) (RealisedClosure.differences once) "same differences"
                Expect.equal (RealisedClosure.held twice) (RealisedClosure.held once) "same held"
            }

        // Every difference is SAID, because a difference nobody is told about is the fault
        // this whole layer exists to remove — the Linux socket grant that resolved, prompted,
        // started, and then did not work.
        check "every difference gets a line a person can read" <| fun () ->
            property {
                let! declarations = genDag
                let! selection = genSelection declarations
                let! limits = genLimits
                let profile = ResourceProfile.load declarations |> expect
                let closure = ResourceProfile.resolve profile selection |> expect
                let realised = RealisedClosure.of' limits (ResourceClosure.leaves closure)
                Expect.equal
                    (List.length (RealisedClosure.describeDifferences realised))
                    (List.length (RealisedClosure.differences realised))
                    "one line each"
                for line in RealisedClosure.describeDifferences realised do
                    Expect.isFalse (line = "") "and none of them empty"
            }

        // --- what a person CONSENTS to -----------------------------------------------------

        // The refinement, stated as an equality: where the host is not a narrowing at all,
        // consent reads exactly what the selection named. Its red says the two renderings
        // have grown apart — which is how a prompt starts spelling a grant one way and the
        // operator's own surface another.
        check "on a host that can express everything, consent reads the selection's own words" <| fun () ->
            property {
                let! declarations = genDag
                let! selection = genSelection declarations
                let profile = ResourceProfile.load declarations |> expect
                let closure = ResourceProfile.resolve profile selection |> expect
                Expect.equal
                    (RealisedClosure.describeOn HostLimits.unlimited closure)
                    (ResourceClosure.describe closure)
                    "the same lines"
            }

        // What a person consents to is read from these lines, so the mark that says a person
        // has to decide is on them whatever this host made of the rest.
        check "however this host realises a selection, every leaf is shown and every connection marked" <| fun () ->
            property {
                let! declarations = genDag
                let! selection = genSelection declarations
                let! limits = genLimits
                let profile = ResourceProfile.load declarations |> expect
                let closure = ResourceProfile.resolve profile selection |> expect
                let lines = RealisedClosure.describeOn limits closure
                Expect.equal
                    (List.length lines)
                    (Set.count (ResourceClosure.leaves closure))
                    "one line per leaf, including the ones this host withheld"
                Expect.equal
                    (lines |> List.filter (fun line -> line.StartsWith "!") |> List.length)
                    (connections (ResourceClosure.leaves closure))
                    "and the host changed no leaf's mark"
            }

        // The whole point of the surface: a person cannot say yes to a scope this host does
        // not have. The line still NAMES the grant — that is what makes it findable — and
        // then says what the sandbox gets instead, so the two can never be the same string.
        check "a grant this host could not express is never shown as if it had been" <| fun () ->
            property {
                let! declarations = genDag
                let! selection = genSelection declarations
                let! limits = genLimits
                let profile = ResourceProfile.load declarations |> expect
                let closure = ResourceProfile.resolve profile selection |> expect
                let realised = RealisedClosure.of' limits (ResourceClosure.leaves closure)
                let lines = RealisedClosure.describeOn limits closure
                for leaf, _ in RealisedClosure.differences realised do
                    let named = ResourceClosure.mark leaf
                    Expect.isFalse
                        (lines |> List.contains named)
                        (sprintf "%s is not what this host gives" named)
                    Expect.isTrue
                        (lines |> List.exists (fun line -> line.StartsWith named && line <> named))
                        (sprintf "%s is named, and then said to come out differently" named)
            }

        // --- the surface a person presses a button on --------------------------------------
        //
        // `capabilitiesOn` is the seam the composition root fills, and these are here rather
        // than beside the fold because what they pin is the algebra reaching a person: the
        // same selection, put through two hosts, is two different things to say yes to.

        // The fault this increment exists for. On a host that cannot scope a unix socket, a
        // repo that selects one gets EVERY socket — and until now the prompt said the path.
        testCase "a repo is shown what its host will grant, not what the resource named" <| fun () ->
            let profile =
                OperatorProfile.parse """
                    { "version": 1,
                      "resources": { "docker": { "socket": "/run/docker.sock" } } }"""
                |> expect
            let selection = [ ResourceName.create "docker" |> expect ]
            let named =
                ResourceProfile.resolve profile.Resources selection |> expect |> ResourceClosure.describe
            let asked =
                RepoSandboxes.capabilitiesOn
                    (Sandboxes.limitsFor SrtBackend Node.Base.Platform.Linux)
                    github
                    (fun uses wants -> ResourceProfile.resolve profile.Resources (uses @ wants))
                    (selection, [])
                |> expect
            Expect.notEqual asked.Granted named "the words the operator wrote are not the offer"
            match asked.Granted, named with
            | [ line ], [ grant ] ->
                Expect.isTrue (line.StartsWith grant) (sprintf "the grant is still named, said: %s" line)
                Expect.isTrue (line.Contains "~> sock:*") (sprintf "and said to come out wider, said: %s" line)
            | granted, _ -> failwithf "expected one line, got %A" granted

        // The other half, and the reason the first is not vacuous: where the host narrows
        // nothing, nothing is added to what a person reads. A prompt that editorialised on
        // every grant would be a prompt whose extra sentence stops being read.
        testCase "a grant this host can express reads exactly as the selection named it" <| fun () ->
            let profile =
                OperatorProfile.parse """
                    { "version": 1,
                      "resources": { "docker": { "socket": "/run/docker.sock" } } }"""
                |> expect
            let selection = [ ResourceName.create "docker" |> expect ]
            let asked =
                RepoSandboxes.capabilitiesOn
                    (Sandboxes.limitsFor SrtBackend Node.Base.Platform.Darwin)
                    github
                    (fun uses wants -> ResourceProfile.resolve profile.Resources (uses @ wants))
                    (selection, [])
                |> expect
            Expect.equal
                asked.Granted
                (ResourceProfile.resolve profile.Resources selection |> expect |> ResourceClosure.describe)
                "the selection's own words"

        // A repo that selects nothing has nothing to consent to, whatever the host is — and
        // is never put through a resolver, which on a host declaring no profile at all would
        // turn that silence into a refusal.
        testCase "a repo that selects nothing asks for nothing, without resolving anything" <| fun () ->
            let asked =
                RepoSandboxes.capabilitiesOn
                    (Sandboxes.limitsFor SrtBackend Node.Base.Platform.Linux)
                    github
                    (fun _ _ -> failwith "nothing should have been resolved")
                    ([], [])
                |> expect
            Expect.equal (asked.Granted, asked.GitHubRoutes) ([], []) "nothing to show, and nobody to ask"

        // The timeline says what a repo's sandboxes hold, so a volume's line says
        // whether the sandbox may write the store every other session builds from.
        testCase "the line for a volume shows its mode" <| fun () ->
            let asked =
                RepoSandboxes.capabilitiesOn
                    (Sandboxes.limitsFor DockerBackend Node.Base.Platform.Linux)
                    github
                    (fun _ _ -> Ok (resolved """{ "volume": { "name": "warm", "at": "/nix", "mode": "read" } }"""))
                    ([], [ ResourceName.create "r" |> expect ])
                |> expect
            Expect.equal asked.Granted [ "vol:warm>/nix:ro" ] "the mode, on the line a person reads"

        // What a repo asks people to trust it with is the routes its sandboxes forward GitHub
        // by (`RepoSandboxes.create` holds them until somebody does): a repository's
        // `wants: [ github ]` once lent a credential with nobody asked.
        testCase "a selection forwarding GitHub asks to be trusted with its routes" <| fun () ->
            let asked =
                RepoSandboxes.capabilitiesOn
                    (Sandboxes.limitsFor DockerBackend Node.Base.Platform.Linux)
                    github
                    (fun _ _ -> Ok (resolved """{ "connection": { "github": [ "git" ] } }"""))
                    ([], [ ResourceName.create "r" |> expect ])
                |> expect
            Expect.equal asked.GitHubRoutes [ ConnectionRoute.Git ] "trusting it means git"

        // The other half: a shared volume is the operator's decision, made in the profile,
        // and the session user is not the one to ask about it.
        testCase "a selection holding a volume asks nobody to trust anything" <| fun () ->
            let asked =
                RepoSandboxes.capabilitiesOn
                    (Sandboxes.limitsFor DockerBackend Node.Base.Platform.Linux)
                    github
                    (fun _ _ -> Ok (resolved """{ "volume": { "name": "warm", "at": "/nix", "mode": "write" } }"""))
                    ([], [ ResourceName.create "r" |> expect ])
                |> expect
            Expect.equal asked.GitHubRoutes [] "the operator already decided"

    ]
