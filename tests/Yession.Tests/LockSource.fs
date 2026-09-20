module Yession.Tests.LockSource

// What the COMMITTED devenv.lock is allowed to say.
//
// One rule: it carries the `nixpkgs` node and nothing else. `devenv.yaml` names nixpkgs by a
// CHANNEL, which moves, so the lock is what makes two jobs in one pull request build against
// the same thing; devenv's own input is deliberately absent, because in a github-restricted
// sandbox `devenv.local.yaml` replaces it with a /nix/store path from THAT container, which is
// not a fact about this repository and is not valid anywhere else. `.gitignore` states both
// halves at length.
//
// It was already guarded, by a clean filter `.claude/setup.sh` installs into `.git/info`, and
// the guard was believed total — "the store path cannot reach the index even by `git commit
// -a`". That is true of a clone where the filter is INSTALLED. It says nothing about a clone
// where it is not yet, and the filter arrives only when setup runs, which is at session start.
// A container whose session began before the filter existed spent two commits in that window
// and put `/nix/store/lxdvfnj1yvx2jg9wn9680792dvhwbx0v-source` on master, silently, through a
// guard that had just been hardened against the OTHER way it fails (a filter that runs and
// errors — #254). Absent is a third state, and it is fail-open.
//
// So the check moved to where no clone's local configuration can be missing it. A filter keeps
// a working tree clean, which is worth having and is why it stays; this refuses the content.
// `YES007`, the rule that keeps the environment written from one place, says the same thing
// about a different guard: it fires on the pull request that writes it.
//
// No capability: it reads a file.

open Fable.Core
open Fable.Core.JsInterop
open Fable.Pyxpecto
open Fable.NodeExtras
open Thoth.Json

/// The two things this rule asks the lock: which nodes it carries, and which inputs its root
/// resolves through. Only the NAMES — what a node pins is devenv's business, and the rule is
/// about which nodes exist at all.
type private Lock =
    { Nodes : string list
      RootInputs : string list }

/// A map's keys, whatever its values are. `Decode.value` is what says "and this decoder has
/// no opinion about them": a node's contents vary by input type, and reading them would make
/// this rule fail on a lock it has nothing to say about.
let private names : Decoder<string list> =
    Decode.keyValuePairs Decode.value |> Decode.map (List.map fst)

/// An absent key is no names rather than a failure: a lock with no `nodes` at all, or a root
/// with no `inputs`, is a lock this rule has something to SAY about (both cases below assert
/// exactly what should be there), not one it cannot read.
let private lock : Decoder<Lock> =
    Decode.map2
        (fun nodes rootInputs -> { Nodes = nodes; RootInputs = rootInputs })
        (Decode.optional "nodes" names |> Decode.map (Option.defaultValue []))
        (Decode.optional "nodes" (Decode.optional "root" (Decode.optional "inputs" names))
         |> Decode.map (Option.flatten >> Option.flatten >> Option.defaultValue []))

/// The lock, read. A file this cannot decode fails by name — where `JSON.parse` inside a macro
/// threw a `SyntaxError` from somewhere in the middle of the assertion, saying nothing about
/// which file was unreadable or why.
let private read (json: string) : Lock =
    match Decode.fromString lock json with
    | Ok lock -> lock
    | Error reason -> failwithf "HEAD:devenv.lock is not a lock this check can read: %s" reason

/// The lock as GIT has it, never the working copy. The working copy carries the devenv node
/// on any machine that has run devenv — that is the normal state and not what this is about.
/// Read through `git show` so this asks the question that matters: what would a laptop or a CI
/// runner get when it checks this out.
///
/// git's own stderr is silenced rather than inherited: in a checkout that cannot answer, the
/// throw below is the answer, and a "fatal: not a git repository" printed into the run would
/// read as a failure of the suite rather than the state it is testing for.
let private gitShow () : string =
    execFileSync
        "git"
        [ "show"; "HEAD:devenv.lock" ]
        { SyncOptions.none with
            Streams = Some { Stdin = Stdio.Ignore; Stdout = Stdio.Pipe; Stderr = Stdio.Ignore } }

/// Nothing, rather than empty text, for a checkout that cannot answer — every case below
/// fails on it out loud, because a run that could not read the tracked lock has checked
/// nothing, and a guard that says otherwise is a decoration.
///
/// This module is compiled to an ES module, where `require` is not defined — a `require` in
/// an emit body throws, and the `catch` around it turns that into "the lock is missing"
/// rather than "this check cannot run". It read as the former for one whole run.
let private committedLock () : string option =
    try
        match gitShow () with
        | "" -> None
        | json -> Some json
    with _ -> None

let tests =
    testList "the committed devenv.lock" [

        testCase "carries nixpkgs, and nothing that belongs to one machine" <| fun () ->
            match committedLock () with
            | None ->
                // Not a pass. A run that cannot read the tracked lock has not checked
                // anything, and saying so is the difference between a guard and a decoration.
                failwith "could not read HEAD:devenv.lock — this check needs a git checkout, and proves nothing without one"
            | Some json ->
                Expect.equal
                    ((read json).Nodes |> List.sort)
                    [ "nixpkgs"; "root" ]
                    "the lock pins the channel this repo names, and records nobody's container"

        // Belt and braces would be two mechanisms for one requirement; this is the other half
        // of the same one. The node is how the path gets in TODAY, and the thing that must
        // never be true is the PATH — so that is what the second case says, and it would still
        // fire if devenv started writing it somewhere else in the file.
        testCase "names no /nix/store path at all" <| fun () ->
            match committedLock () with
            | None -> failwith "could not read HEAD:devenv.lock"
            | Some json ->
                Expect.isFalse
                    (json.Contains "/nix/store/")
                    "a store path is valid on exactly one machine, and this file is read by every checkout"

        testCase "the root still takes its inputs from the lock" <| fun () ->
            match committedLock () with
            | None -> failwith "could not read HEAD:devenv.lock"
            | Some json ->
                Expect.equal
                    ((read json).RootInputs |> List.sort)
                    [ "nixpkgs" ]
                    "stripping the node without its root input would leave a lock that does not resolve"
    ]
