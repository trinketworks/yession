module Yession.Tests.RepoLayout

// What this repository's layout may hold: the rules about WHERE things live that used to be
// sentences in AGENTS.md, which nothing read after the day they were written.
//
//   * The root `AGENTS.md` stays within what a session reads of one. `Repos.agentsMdOf`
//     truncates a repo's `AGENTS.md` at `Repos.agentsMdCharCap`, and this repository is one
//     that sessions are given; a file that grows past the cap loses its tail to every agent
//     working here, without a failure anywhere.
//   * The only helper script is `.claude/setup.sh`. Build, dev and repo functionality is a
//     `tasks.fsx` verb, so there is one build interface that knows what runs after what.
//   * `docs/plans` does not exist. A plan is right on the day it is written and drifts after.
//   * An example is standalone, in both directions: it lists nothing of Yession, and nothing
//     of the product lists it.
//
// The example rules are read from the projects' lockfiles, the way `DomainReferences` reads
// the domain's: a lockfile names every project a project references, transitive ones
// included, and locked mode (`Directory.Build.props`) fails any build whose lockfile no
// longer says what the project asks for. A reference cannot be added without the lockfile
// changing, so the lockfile is the whole answer and the `.fsproj` is not needed. What
// `DomainReferences` pins is a different fact (the domain references NOTHING) and this
// leaves it to that suite.
//
// The files are the ones git would commit: tracked, and untracked but not ignored. A file
// somebody has just made and not yet added is the one a rule of this kind exists to catch,
// and a run that waited for `git add` would see it a commit too late. `LockSource` and
// `DomainReferences` already spawn git, so listing the tree needs no capability either.
//
// No capability: it reads files.

open Fable.Core
open Fable.NodeExtras
open Fable.Pyxpecto
#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

/// Where the repository is, and the files in it that git would commit, as paths relative to
/// the root with `/` separators.
type private Checkout =
    { Root : string
      Files : string list }

/// `spawnSync`'s own limit is 1 MiB, and a child that exceeds it is KILLED with its output
/// truncated -- which would read exactly like files that were never there. The listing is a
/// fraction of that today; this is room for it to grow.
let private roomForTheListing = 32 * 1024 * 1024

/// A file git lists that is not one on disk -- deleted and not yet staged, a directory a
/// symlink points at (`.claude/skills`), a nested repository -- has no text to read, and is
/// nothing a rule about scripts or lockfiles is about.
let private isFile (path: string) : bool =
    try (Node.Api.fs.statSync (U2.Case1 path)).isFile () with _ -> false

/// The checkout this run is in. Anchored at the repository root rather than at the runner's
/// working directory, the way `DomainReferences` is: the subject is a committed tree, not
/// whatever the runner's directory holds.
///
/// A run that cannot list the tree has checked nothing, so it fails by name. An empty answer
/// is not a clean tree; it is a listing that broke.
let private readCheckout () : Checkout =
    let root =
        try
            (execFileSync "git" [ "rev-parse"; "--show-toplevel" ] SyncOptions.none).Trim ()
        with e ->
            failwithf "this check needs a git checkout, and proves nothing without one: %s" e.Message
    let listed =
        try
            execFileSync
                "git"
                [ "ls-files"; "-z"; "--cached"; "--others"; "--exclude-standard" ]
                { SyncOptions.none with Cwd = Some root; MaxBuffer = Some roomForTheListing }
        with e ->
            failwithf "could not list the files of %s: %s" root e.Message
    let files =
        listed.Split [| '\u0000' |]
        |> Array.filter (fun path -> path <> "" && isFile (root + "/" + path))
        |> Array.toList
        |> List.distinct
    if List.isEmpty files then
        failwithf "git listed no files in %s; a rule over an empty tree is green and proves nothing" root
    { Root = root; Files = files }

/// Listed once for the suite: every case reads the same tree.
let private checkout : Lazy<Checkout> = lazy (readCheckout ())

let private text (checkout: Checkout) (path: string) : string = TestFiles.read (checkout.Root + "/" + path)

let private under (prefix: string) (files: string list) : string list =
    files |> List.filter (fun path -> path.StartsWith prefix)

// --- helper scripts ----------------------------------------------------------------------

/// The one helper script this repository keeps. It exists because it must run where `dotnet`
/// cannot: before Nix and devenv do, in a fresh Claude Code container. Nothing else has that
/// excuse.
let private allowedScripts = [ ".claude/setup.sh" ]

/// A script: named for a shell, or starting with a shebang. The shebang is what catches the
/// extensionless ones. `examples/` is not looked at: an example is built the way somebody
/// OUTSIDE this repository would build it, and `examples/nix-store/pin` is a script because
/// that is what its subject is.
let private helperScripts (checkout: Checkout) : string list =
    checkout.Files
    |> List.filter (fun path -> not (path.StartsWith "examples/"))
    |> List.filter (fun path -> path.EndsWith ".sh" || (text checkout path).StartsWith "#!")

// --- lockfiles ---------------------------------------------------------------------------

/// Everything a lockfile names across every framework it restores for, as (name, kind) --
/// kind being `Direct`, `Transitive` or `Project`. Only the NAMES and the kind: what version a
/// dependency resolves to is NuGet's business, and these rules are about whether it is there.
let private lockedEntries : Decoder<(string * string) list> =
    Decode.field
        "dependencies"
        (Decode.keyValuePairs (Decode.keyValuePairs (Decode.field "type" Decode.string))
         |> Decode.map (List.collect snd))

/// A lockfile this cannot read fails by name. An empty list is what a lockfile that obeys the
/// rule looks like, and the two must not read the same.
let private entriesOf (checkout: Checkout) (lockfile: string) : (string * string) list =
    match Decode.fromString lockedEntries (text checkout lockfile) with
    | Ok entries -> entries
    | Error reason -> failwithf "%s is not a lockfile this can read: %s" lockfile reason

/// The lockfiles under the given directories. Refuses to find none: a rule over no lockfile is
/// green and proves nothing, and that is what a moved directory looks like.
let private lockfilesUnder (checkout: Checkout) (directories: string list) : string list =
    let found =
        checkout.Files
        |> List.filter (fun path ->
            path.EndsWith "/packages.lock.json"
            && directories |> List.exists (fun directory -> path.StartsWith directory))
    if List.isEmpty found then
        failwithf "found no packages.lock.json under %s; the rule has nothing to read" (String.concat ", " directories)
    found

/// A lockfile spells a project by its name in lower case (`yession.domain`), and a package as
/// its author did.
let private isYession (name: string) : bool = name.ToLowerInvariant().StartsWith "yession."

/// The projects the examples define, named as a lockfile would name them if something
/// referenced one: the project file's name, lower-cased.
let private exampleProjects (checkout: Checkout) : string list =
    let found =
        checkout.Files
        |> under "examples/"
        |> List.filter (fun path -> path.EndsWith ".fsproj" || path.EndsWith ".csproj")
        |> List.map (fun path ->
            let file = path.Substring ((path.LastIndexOf "/") + 1)
            (file.Substring (0, file.LastIndexOf ".")).ToLowerInvariant ())
    if List.isEmpty found then
        failwith "found no project file under examples/; the rule has nothing to tell a reference to one from"
    found

let tests =
    testList "Repo layout" [

        testCase "the root AGENTS.md is no longer than a session reads of one" <| fun () ->
            let length = (text checkout.Value "AGENTS.md").Length
            let cap = Yession.Host.Repos.agentsMdCharCap
            Expect.isTrue
                (length <= cap)
                (sprintf
                    "AGENTS.md is %d characters and a session reads %d of a repo's AGENTS.md (Repos.agentsMdCharCap), then drops the rest. This repository is one sessions are given, so the agents working in it stop seeing whatever follows the cap, and nothing says they did. Shorten the file: put what is worth keeping next to the code it governs, where a test can contradict it, and let AGENTS.md point there. The cap is a product setting and not this file's to raise"
                    length
                    cap)

        testCase "the only helper script outside examples/ is .claude/setup.sh" <| fun () ->
            let extra = helperScripts checkout.Value |> List.filter (fun path -> not (List.contains path allowedScripts))
            Expect.isEmpty
                extra
                (sprintf
                    "%s: a script beside the build. New build, dev or repo functionality is a verb in tasks.fsx (`dotnet fsi tasks.fsx <verb>`): the devenv scripts, the workflows and the Nix outputs wrap it, and `make` builds each Target once, after what it stands on. A script of its own is a second build interface that nothing orders. Add a Target and a verb; to run it from devenv, wrap the verb. .claude/setup.sh is the one exception, because it runs before dotnet, Nix and devenv exist. Scripts under examples/ are exempt: an example is built the way somebody outside this repository would build it"
                    (String.concat ", " extra))

        // The population, asserted beside the rule: a scan that stopped seeing scripts finds no
        // extra one either, and reads exactly like a tree that obeys.
        testCase "the scan for helper scripts still finds the one it allows" <| fun () ->
            let found = helperScripts checkout.Value
            let missing = allowedScripts |> List.filter (fun path -> not (List.contains path found))
            Expect.isEmpty
                missing
                (sprintf
                    "%s is allowed as a helper script and the scan did not find it. If it moved or was renamed, change `allowedScripts` in RepoLayout.fs; if it is still there, the scan (a .sh name, or a #! on the first line) stopped seeing scripts, and `the only helper script` above proves nothing"
                    (String.concat ", " missing))

        testCase "docs/plans holds no file" <| fun () ->
            let plans = checkout.Value.Files |> under "docs/plans/"
            Expect.isEmpty
                plans
                (sprintf
                    "%s: docs/plans/ was removed and stays removed. A plan is right on the day it is written and unfalsifiable after, and it drifts silently while it reads as current. Say the shape of the work to the person asking, and put what is worth keeping next to the code it governs, where a test can contradict it"
                    (String.concat ", " plans))

        testCase "no example's lockfile lists a Yession project or package" <| fun () ->
            let checkout = checkout.Value
            let reached =
                [ for lockfile in lockfilesUnder checkout [ "examples/" ] do
                      for name, _ in entriesOf checkout lockfile do
                          if isYession name then
                              yield sprintf "%s lists %s" lockfile name ]
            Expect.isEmpty
                reached
                (sprintf
                    "%s. An example is built the way somebody OUTSIDE this repository would build it, so it references nothing of Yession: no Domain, no Host, no product package. Copy what it needs and own the copy; the duplication is the point, because a path through product internals is one no reader can reproduce. Then remove the reference from the example's project and run `dotnet fsi tasks.fsx lock`"
                    (String.concat "; " reached))

        testCase "no product project's lockfile lists an example's project" <| fun () ->
            let checkout = checkout.Value
            let examples = exampleProjects checkout
            let reached =
                [ for lockfile in lockfilesUnder checkout [ "src/"; "app/" ] do
                      for name, kind in entriesOf checkout lockfile do
                          if kind = "Project" && List.contains name examples then
                              yield sprintf "%s lists %s" lockfile name ]
            Expect.isEmpty
                reached
                (sprintf
                    "%s. Dependencies go one way: the suite (tests/Yession.Tests) references an example and tests it, and nothing in src/ or app/ may reference one. The product must build, ship and read without the examples, which are not in the npm package or the Nix installable. If the product needs what an example has, that is the product's code: move it out of the example, or copy it. Then remove the reference from the project and run `dotnet fsi tasks.fsx lock`"
                    (String.concat "; " reached))
    ]
