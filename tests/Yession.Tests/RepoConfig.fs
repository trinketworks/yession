module Yession.Tests.RepoConfig

// The `yession.yaml` bridge (Plan 27): a real file on disk, through a real YAML parser, into
// the domain's decoder.
//
// The DECODER's own behaviour is pinned in `Domain.fs` from JSON literals, on both runtimes.
// What is left for this suite is only what a file can do that a JSON literal cannot — YAML
// syntax the parser has to be configured correctly to refuse or to allow, and the difference
// between a repo with no file and a repo with a broken one.

open Fable.Core
open Fable.Pyxpecto
open Yession.Domain
open Yession.Codecs
open Yession.Domain.Agent
open Yession.Domain.Repos
open Yession.Domain.Sandboxes
open Yession.Domain.Tools
open Yession.Domain.Chat
open Yession.Host
open Yession.App.Codecs
open Yession.Session

let private expect = function Ok v -> v | Error e -> failwithf "%A" e

let private repo (raw: string) = RepoRef.create raw |> expect

/// The item a boot recorded, which a boot's fold names as its cause.
let private boot = MessageId.create "boot" |> expect

/// Where a case writes a checkout's `yession.yaml`, under its repos directory.
let private pathIn (reposDir: string) (r: RepoRef) : string =
    sprintf "%s/%s/%s" reposDir (RepoRef.relativePath r) ConfigFile.FileName

/// The files a case wrote under `reposDir`, standing in for what each checkout's `HEAD`
/// commits. The session reads git's object store (`Repos.ReposService.ReadCommitted`, whose
/// own cases are in `GitIntegration`); what THIS suite pins is the parse and the fold over
/// whatever that read answers.
let private committedIn (reposDir: string) : Repos.CommittedFile =
    fun r path ->
        async {
            let file = sprintf "%s/%s/%s" reposDir (RepoRef.relativePath r) path
            return Ok (if TestFiles.exists file then Some (TestFiles.read file) else None)
        }

/// The value of an async that never waits, which `committedIn`'s reads do not: started
/// immediately, it has settled by the time this returns.
let private settled (work: Async<'a>) : 'a =
    let mutable answer = None
    Async.StartImmediate (async {
        let! value = work
        answer <- Some value
    })
    match answer with
    | Some value -> value
    | None -> failwith "a read that was meant to settle at once is still waiting"

/// `RepoConfig.read` over the files a case wrote.
let private readIn (reposDir: string) (r: RepoRef) = settled (RepoConfig.read (committedIn reposDir) r)

/// A repos directory holding one checkout, with `text` as its `yession.yaml` when given.
let private checkout (r: RepoRef) (text: string option) : string =
    let reposDir = TestFiles.tempDir "yession-config-"
    TestFiles.ensureDir (sprintf "%s/%s" reposDir (RepoRef.relativePath r))
    text |> Option.iter (TestFiles.write (pathIn reposDir r))
    reposDir

// YAML fixtures live at module level: a triple-quoted string whose content starts at column 0
// is offside inside the list expression below.

let private devWithNet = """
version: 2
sandboxes:
  dev:
    container:
      image: node:24
    uses:
      - npm
"""

let private duplicateName = """
version: 2
sandboxes:
  dev:
    container: { image: node:24 }
  dev:
    container: { image: node:20 }
"""

let private anchored = """
version: 2
sandboxes:
  dev: &base
    uses: [ npm ]
  gate: *base
"""

let tests =
    testList "yession.yaml on disk (Plan 27)" [

        // --- the operator's profile, as a FILE ----------------------------------------------

        // Measured before it was written. A `ca` resource declared as `/etc/ssl/cert.pem` on
        // this macOS host produced a sandbox that reads `/private/etc/ssl/cert.pem` and is
        // refused `/etc/ssl/cert.pem` — the path the operator wrote, and the path the approval
        // prompt showed a person before they consented to it. srt canonicalises the allow-list
        // entry; the OS then denies the symlink node the access has to traverse.
        //
        // A grant that reads as held and behaves as denied says nothing at any point, so this
        // is refused where an operator is still looking at their own file.
        testCase "a resource reached through a symlink is refused, naming the path to write" <| fun () ->
            let dir = TestFiles.tempDir "yession-config-" |> Fs.canonical |> Option.get
            TestFiles.ensureDir (dir + "/real")
            TestFiles.write (dir + "/real/thing") "x"
            TestFiles.symlink (dir + "/real") (dir + "/link")
            let file = dir + "/resources.yaml"
            TestFiles.write file (
                sprintf "version: 1\nresources:\n  thing:\n    mount:\n      - { from: %s/link/thing, mode: read }\n" dir)
            match OperatorResources.read file with
            | Ok _ -> failwith "expected a refusal"
            | Error e ->
                Expect.isTrue (e.Contains (dir + "/link/thing"))
                    (sprintf "the refusal quotes what was written, said: %s" e)
                Expect.isTrue (e.Contains (dir + "/real/thing"))
                    (sprintf "and the form to write instead, said: %s" e)

        // The other half, and the reason the rule is not just "refuse anything unusual": the
        // canonical form of the SAME file loads. Without this, a rule that refused everything
        // would be green above and useless.
        testCase "the canonical form of that same path loads" <| fun () ->
            let dir = TestFiles.tempDir "yession-config-" |> Fs.canonical |> Option.get
            TestFiles.ensureDir (dir + "/real")
            TestFiles.write (dir + "/real/thing") "x"
            TestFiles.symlink (dir + "/real") (dir + "/link")
            let file = dir + "/resources.yaml"
            TestFiles.write file (
                sprintf "version: 1\nresources:\n  thing:\n    mount:\n      - { from: %s/real/thing, mode: read }\n" dir)
            match OperatorResources.read file with
            | Ok (Some profile) ->
                Expect.equal
                    (Sandboxes.ResourceProfile.declared profile.Profile.Resources
                     |> Set.toList
                     |> List.map ResourceName.value)
                    [ "thing" ]
                    "it declares what it said it declares"
            | other -> failwithf "expected a profile, got %A" other

        // One form, whoever writes it: an operator's sandbox reads as a repo's does, and is
        // analyzed by the same analyzers.
        testCase "an operator declares sandboxes in the form a repo does, and is read the same way" <| fun () ->
            let dir = TestFiles.tempDir "yession-config-" |> Fs.canonical |> Option.get
            let file = dir + "/resources.yaml"
            TestFiles.write file "version: 1\nresources: {}\nsandboxes:\n  default:\n    uses: [ github ]\n    wants: [ a, a ]\n"
            match OperatorResources.read file with
            | Ok (Some read) ->
                Expect.equal
                    (read.Profile.Sandboxes
                     |> Map.toList
                     |> List.map (fun (name, decl) -> SandboxName.value name, decl.Uses |> List.map ResourceName.value))
                    [ "default", [ "github" ] ]
                    "the declaration, as a repo's would decode"
            | other -> failwithf "expected a profile, got %A" other

        testCase "an operator's sandbox is refused as a repo's would be" <| fun () ->
            let dir = TestFiles.tempDir "yession-config-" |> Fs.canonical |> Option.get
            let file = dir + "/resources.yaml"
            TestFiles.write file "version: 1\nresources: {}\nsandboxes:\n  default:\n    workdirr: .\n"
            Expect.isError (OperatorResources.read file) "an unknown key, refused by the one decoder"

        testCase "an operator's declarations are noted where they were written" <| fun () ->
            let dir = TestFiles.tempDir "yession-config-" |> Fs.canonical |> Option.get
            let file = dir + "/resources.yaml"
            TestFiles.write file "version: 1\nresources:\n  a: { env: { A: '1' } }\nsandboxes:\n  default:\n    uses: [ a ]\n    wants: [ a ]\n"
            match OperatorResources.read file with
            | Ok (Some read) ->
                Expect.equal
                    (read.Findings |> List.map Yession.Domain.Sandboxes.LocatedFinding.where)
                    [ "sandboxes.default.wants[0], line 7" ]
                    "the want the need already covers, on its line"
            | other -> failwithf "expected a profile, got %A" other

        // A path nothing has created yet is left alone. A cache directory a tool makes on
        // first use is ordinary, and refusing it here would be an existence check wearing the
        // symlink rule's name — an operator would read "reached through a symlink" about a
        // path that is not.
        testCase "a path that does not exist yet is not refused" <| fun () ->
            let dir = TestFiles.tempDir "yession-config-" |> Fs.canonical |> Option.get
            let file = dir + "/resources.yaml"
            TestFiles.write file (
                sprintf "version: 1\nresources:\n  cache:\n    mount:\n      - { from: %s/not-yet, mode: write }\n" dir)
            match OperatorResources.read file with
            | Ok (Some _) -> ()
            | other -> failwithf "expected a profile, got %A" other

        testCase "a real file decodes to what the repo asked for" <| fun () ->
            // The end-to-end claim: YAML on disk reaches the domain intact.
            let r = repo "octo/hello"
            let dir = checkout r (Some devWithNet)
            let file = (readIn dir r |> expect |> Option.get).File
            let dev = file.Sandboxes |> Map.find (SandboxName.create "dev" |> expect)
            Expect.equal (dev.Container |> Option.get).Image (Some { Name = "node"; Tag = Some "24" })
                "the image survived the round trip"
            Expect.equal (dev.Uses |> List.map ResourceName.value) [ "npm" ] "so did the resources it selects"

        // Yession's own `yession.yaml`, decoded by the real thing. It is the acceptance test
        // for the schema: if this repo cannot say what a session working on it needs, the
        // schema is wrong — and a plausible-looking sample in a doc would never find out.
        testCase "the file this repo carries is one this build can read" <| fun () ->
            let r = repo "trinketworks/yession"
            let dir = TestFiles.tempDir "yession-config-"
            TestFiles.ensureDir (sprintf "%s/%s" dir (RepoRef.relativePath r))
            TestFiles.write (pathIn dir r) (TestFiles.read "yession.yaml")
            let file = (readIn dir r |> expect |> Option.get).File
            let dev = file.Sandboxes |> Map.find (SandboxName.create "dev" |> expect)
            let gate = file.Sandboxes |> Map.find (SandboxName.create "gate" |> expect)
            // A repo's work sandbox is a container; a declaration without one is refused
            // at start. What the container needs is exactly what a person needs locally:
            // nix, and the checkout — the devshell does the rest.
            for name, decl in [ "dev", dev; "gate", gate ] do
                match decl.Container with
                | None -> failwithf "%s declares no container, and a repo work sandbox is one" name
                | Some container ->
                    Expect.equal
                        (container.Image |> Option.map (fun i -> i.Name))
                        (Some "nixos/nix")
                        (sprintf "%s runs the nix base image" name)
                // The whole nix configuration the devshell sees, features and substituters
                // both — in a container there is no /etc/nix/nix.conf story to inherit.
                Expect.isTrue
                    (decl.EnvironmentVariables |> Map.containsKey "NIX_CONFIG")
                    (sprintf "%s carries the nix settings its build needs" name)
            for name, decl in [ "dev", dev; "gate", gate ] do
                // WANTS, not uses: the same file has to work on hosts that offer no warm
                // store and no GitHub connection, and a `uses:` there would refuse the
                // sandbox outright. Where the operator offers `github`, `git push` works.
                Expect.equal
                    (decl.Wants |> List.map ResourceName.value)
                    [ "github"; "nix-container-store" ]
                    (sprintf "%s wishes for the GitHub connection and the warm store where an operator offers them" name)
            // `gh` finds its repo from the checkout's remotes, and behind the git gateway's
            // `insteadOf` it finds none it recognises — so `dev`, where gh runs, names it.
            Expect.equal
                (dev.EnvironmentVariables |> Map.tryFind "GH_REPO")
                (Some (Yession.Domain.Sandboxes.PlainValue "trinketworks/yession"))
                "dev points gh at this repo, which its rewritten remote no longer says"

        // The release's Docker tier creates this repo's dev container on CI's daemon, which
        // uses the systemd cgroup driver; the operator's Colima VM uses cgroupfs. A
        // declaration that only one driver takes passed `test` once, and failed every
        // release after it. This asks both drivers, with no docker, through the same pure
        // check the backend runs before it pulls (`ContainerSpec.forDaemon`).
        testCase "the containers this repo declares are ones both cgroup drivers take" <| fun () ->
            let r = repo "trinketworks/yession"
            let dir = TestFiles.tempDir "yession-config-"
            TestFiles.ensureDir (sprintf "%s/%s" dir (RepoRef.relativePath r))
            TestFiles.write (pathIn dir r) (TestFiles.read "yession.yaml")
            let file = (readIn dir r |> expect |> Option.get).File
            // The operator's VM, the smallest daemon this repo is run on.
            let vm = 8L * 1024L * 1024L * 1024L
            let containers =
                file.Sandboxes
                |> Map.toList
                |> List.choose (fun (name, decl) -> decl.Container |> Option.map (fun c -> SandboxName.value name, c))
            Expect.isNonEmpty containers "this repo declares containers"
            for name, container in containers do
                for driver in [ "cgroupfs"; "systemd" ] do
                    match Yession.Domain.Sandboxes.ContainerSpec.forDaemon container Yession.Domain.Sandboxes.ContainerCeiling.none vm driver with
                    | Ok _ -> ()
                    | Error reason -> failwithf "%s: a %s daemon refuses it: %s" name driver reason

        testCase "a repo with no file asks for nothing, and that is not an error" <| fun () ->
            // The ordinary case. Most repos will never carry one.
            let r = repo "octo/plain"
            Expect.equal (readIn (checkout r None) r) (Ok None) "absent is absent, not broken"

        testCase "a repo with a broken file is refused, not treated as absent" <| fun () ->
            // Folding this into the case above is how a repo silently stops being configured
            // the day somebody mistypes a key.
            let r = repo "octo/broken"
            Expect.isError (readIn (checkout r (Some "version: 2\nsandboxes:\n  dev:\n    workdirr: ./app\n")) r)
                "an unknown key fails the file rather than yielding an empty one"

        testCase "the refusal names the repo it came from" <| fun () ->
            // A session can hold several checkouts; "a config is broken" is not actionable.
            let r = repo "octo/broken"
            match readIn (checkout r (Some "version: 99\n")) r with
            | Ok _ -> failwith "expected a refusal"
            | Error e -> Expect.isTrue (e.Contains "octo/broken") "it says whose file"

        testCase "a repeated sandbox name is refused rather than silently folded" <| fun () ->
            // This is what `uniqueKeys` buys, and it cannot be tested from a JSON literal:
            // JSON object semantics fold a duplicate to last-wins BEFORE any decoder sees it,
            // so without the option the second `dev` would quietly win.
            let r = repo "octo/hello"
            let dir = checkout r (Some duplicateName)
            Expect.isError (readIn dir r) "one name declared twice fails the file"

        testCase "a YAML anchor is resolved before the decoder sees anything" <| fun () ->
            // Reuse inside a file costs the schema nothing — which is what lets a repo declare
            // a second sandbox on the same configuration without repeating it.
            let r = repo "octo/hello"
            let dir = checkout r (Some anchored)
            let file = (readIn dir r |> expect |> Option.get).File
            let gate = file.Sandboxes |> Map.find (SandboxName.create "gate" |> expect)
            Expect.equal (gate.Uses |> List.map ResourceName.value) [ "npm" ] "the alias carried the anchor's value"

        testCase "a custom tag cannot ask the parser for something the schema never agreed to" <| fun () ->
            // `schema: 'core'` is the whole mitigation, and a test that never exercises a tag
            // would not notice it being dropped.
            let r = repo "octo/hello"
            Expect.isError
                (readIn (checkout r (Some "version: 2\nsandboxes: !!python/object:os.system {}\n")) r)
                "an unknown tag is refused"

        testCase "one repo's broken file does not cost the others their configuration" <| fun () ->
            // A session held hostage by whichever checkout happens to have a typo would be
            // worse than no file support at all.
            let good, bad = repo "octo/good", repo "octo/bad"
            let dir = TestFiles.tempDir "yession-config-"
            for r in [ good; bad ] do TestFiles.ensureDir (sprintf "%s/%s" dir (RepoRef.relativePath r))
            TestFiles.write (pathIn dir good) "version: 2\nsandboxes:\n  dev: {}\n"
            TestFiles.write (pathIn dir bad) "version: 2\nsandboxes:\n  dev:\n    nope: 1\n"
            let read = settled (RepoConfig.readAll (committedIn dir) [ good; bad ])
            let declared, refused = read.Declared, read.Refused
            Expect.equal (Map.count declared) 1 "the good repo's sandbox survived"
            Expect.isTrue
                (declared |> Map.containsKey (SandboxRef.inScope good (SandboxName.create "dev" |> expect)))
                "and it is the good repo's, scoped to it"
            Expect.equal (List.length refused) 1 "the broken one is reported rather than dropped"
            Expect.equal (fst refused.[0]) bad "named, so somebody can fix it"

        // The analyzer answers in key paths; the parse is what knows lines. A note an author
        // cannot find in their own file is a note they cannot act on.
        testCase "a resource both needed and wanted is noted where the want was written" <| fun () ->
            let text = "version: 2\nsandboxes:\n  dev:\n    uses: [ nix ]\n    wants:\n      - cache\n      - nix\n"
            match RepoConfig.fromText text with
            | Error e -> failwithf "the file should decode: %s" e
            | Ok read ->
                Expect.equal
                    (read.Findings |> List.map Yession.Domain.Sandboxes.LocatedFinding.where)
                    [ "sandboxes.dev.wants[1], line 7" ]
                    "the second want, on the line it is on"

        // A note is said, never acted on: the file is honoured as written.
        testCase "a noted file still declares everything it wrote" <| fun () ->
            let text = "version: 2\nsandboxes:\n  dev:\n    uses: [ nix ]\n    wants: [ nix ]\n"
            match RepoConfig.fromText text with
            | Error e -> failwithf "the file should decode: %s" e
            | Ok read ->
                let decl = read.File.Sandboxes |> Map.find (SandboxName.create "dev" |> expect)
                Expect.equal (decl.Uses, decl.Wants) ([ ResourceName.create "nix" |> expect ], [ ResourceName.create "nix" |> expect ]) "both lines, as written"

        // What a composed value MEANS: the value beneath, then what the file added.
        testCase "a variable composed over itself extends what lies beneath it" <| fun () ->
            let text = "version: 2\nsandboxes:\n  dev:\n    env:\n      SSL_CERT_DIR: ${env.SSL_CERT_DIR}:/run/ca\n"
            match RepoConfig.fromText text with
            | Error e -> failwithf "the file should decode: %s" e
            | Ok read ->
                let decl = read.File.Sandboxes |> Map.find (SandboxName.create "dev" |> expect)
                match decl.EnvironmentVariables |> Map.tryFind "SSL_CERT_DIR" with
                | Some (Yession.Domain.Sandboxes.Derived template) ->
                    Expect.equal
                        (Yession.Domain.Sandboxes.EnvTemplate.resolve (fun name -> if name = "SSL_CERT_DIR" then Some "/etc/ssl/certs" else None) template)
                        "/etc/ssl/certs:/run/ca"
                        "the image's directory, then the file's"
                | other -> failwithf "expected a composed value, got %A" other

        testCase "a reference to nothing beneath composes over empty" <| fun () ->
            match Yession.Domain.Sandboxes.EnvTemplate.parse "${env.UNSET}:/x" with
            | Ok template -> Expect.equal (Yession.Domain.Sandboxes.EnvTemplate.resolve (fun _ -> None) template) ":/x" "empty where there is nothing"
            | Error e -> failwithf "should parse: %s" e

        // A file that writes `${` meaning the characters has a way to say so.
        testCase "a doubled dollar is a literal reference opener, and stays plain text" <| fun () ->
            let text = "version: 2\nsandboxes:\n  dev:\n    env:\n      PROMPT: cost $${dollars}\n"
            match RepoConfig.fromText text with
            | Error e -> failwithf "the file should decode: %s" e
            | Ok read ->
                let decl = read.File.Sandboxes |> Map.find (SandboxName.create "dev" |> expect)
                Expect.equal (decl.EnvironmentVariables |> Map.tryFind "PROMPT") (Some (Yession.Domain.Sandboxes.PlainValue "cost ${dollars}")) "the characters, as meant"

        // A reference a later build would understand is refused here, not kept as text: a
        // file that meant a reference and got the literal would run on something unsaid.
        testCase "a reference this build does not know refuses the file, naming it" <| fun () ->
            let text = "version: 2\nsandboxes:\n  dev:\n    env:\n      TOKEN: ${vault.secret}\n"
            match RepoConfig.fromText text with
            | Ok _ -> failwith "should refuse"
            | Error e -> Expect.stringContains e "vault.secret" "names what it did not know"

        // What a sandbox asks the credential proxy for is a reference like `${env.NAME}`, so a
        // file composes it with what it already has: the image's trust store AND the proxy's.
        testCase "a proxy reference composes with what lies beneath" <| fun () ->
            let text = "version: 2\nsandboxes:\n  dev:\n    env:\n      SSL_CERT_DIR: ${env.SSL_CERT_DIR}:${proxy.ca-dir}\n"
            match RepoConfig.fromText text with
            | Error e -> failwithf "the file should decode: %s" e
            | Ok read ->
                let decl = read.File.Sandboxes |> Map.find (SandboxName.create "dev" |> expect)
                Expect.equal
                    (decl.EnvironmentVariables |> Map.tryFind "SSL_CERT_DIR")
                    (Some (
                        Yession.Domain.Sandboxes.Derived
                            [ Yession.Domain.Sandboxes.TemplatePart.Beneath "SSL_CERT_DIR"
                              Yession.Domain.Sandboxes.TemplatePart.Literal ":"
                              Yession.Domain.Sandboxes.TemplatePart.Proxy Yession.Domain.Sandboxes.ProxyValue.CaDir ]
                    ))
                    "the image's, then the proxy's"
                Expect.equal (Yession.Session.ConfigFile.parseSandbox (Yession.Session.ConfigFile.encodeSandbox decl)) (Ok decl) "and it crosses the gate as written"

        // A token is lent to each command rather than held by the sandbox, so it is a
        // variable's whole value — and reads back across the gate as it was written.
        testCase "a connection's token is lent into the variable that names it" <| fun () ->
            let text = "version: 2\nsandboxes:\n  dev:\n    env:\n      GH_TOKEN: ${github.token}\n"
            match RepoConfig.fromText text with
            | Error e -> failwithf "the file should decode: %s" e
            | Ok read ->
                let decl = read.File.Sandboxes |> Map.find (SandboxName.create "dev" |> expect)
                Expect.equal
                    (decl.EnvironmentVariables |> Map.tryFind "GH_TOKEN")
                    (Some (Yession.Domain.Sandboxes.Lent (ConnectionName.create "github" |> expect)))
                    "lent, not held"
                Expect.equal (Yession.Session.ConfigFile.parseSandbox (Yession.Session.ConfigFile.encodeSandbox decl)) (Ok decl) "and it crosses the gate as written"

        // Composed into a larger value it would be a value the sandbox holds while the stand-in
        // inside it rotates — so it is refused, pointing at the open question behind the rule.
        testCase "a token composed into a larger value refuses the file, pointing at why" <| fun () ->
            let text = "version: 2\nsandboxes:\n  dev:\n    env:\n      AUTH: Bearer ${github.token}\n"
            match RepoConfig.fromText text with
            | Ok _ -> failwith "should refuse"
            | Error e ->
                Expect.stringContains e "whole value" "says what it may be"
                Expect.stringContains e "docs/GAPS.md" "and where the rule is argued"

        testCase "a proxy field this build does not know refuses the file, naming what it knows" <| fun () ->
            let text = "version: 2\nsandboxes:\n  dev:\n    env:\n      ALL_PROXY: ${proxy.socks}\n"
            match RepoConfig.fromText text with
            | Ok _ -> failwith "should refuse"
            | Error e ->
                Expect.stringContains e "proxy.socks" "names what it did not know"
                Expect.stringContains e "${proxy.https}" "and what it does"

        // A sandbox is built from values: what the proxy provided is written in, and a
        // reference it did not provide is refused rather than left for a backend.
        testCase "a provided proxy value is written into the template, and an unprovided one refused" <| fun () ->
            let template = Yession.Domain.Sandboxes.EnvTemplate.parse "${env.SSL_CERT_DIR}:${proxy.ca-dir}" |> expect
            let provided = Map.ofList [ Yession.Domain.Sandboxes.ProxyValue.CaDir, "/run/yession/proxy/authority" ]
            let written = Yession.Domain.Sandboxes.EnvTemplate.provide provided template |> expect
            Expect.equal
                (Yession.Domain.Sandboxes.EnvTemplate.resolve (fun _ -> Some "/etc/ssl/certs") written)
                "/etc/ssl/certs:/run/yession/proxy/authority"
                "the image's, then what the proxy provided"
            match Yession.Domain.Sandboxes.EnvTemplate.provide Map.empty template with
            | Ok _ -> failwith "an unprovided reference should refuse"
            | Error e -> Expect.stringContains e "${proxy.ca-dir}" "naming it"

        // The session says which build it is, so a sandbox (and the agent in it) can know what
        // it is talking to — and the reference crosses the gate as the file wrote it.
        testCase "a session reference reads as one and crosses the gate as written" <| fun () ->
            // Not `YESSION_…`: that prefix is the operator's and Yession's, never a repo's.
            let text = "version: 2\nsandboxes:\n  dev:\n    env:\n      BUILT_UNDER: ${session.version}\n"
            match RepoConfig.fromText text with
            | Error e -> failwithf "the file should decode: %s" e
            | Ok read ->
                let decl = read.File.Sandboxes |> Map.find (SandboxName.create "dev" |> expect)
                Expect.equal
                    (decl.EnvironmentVariables |> Map.tryFind "BUILT_UNDER")
                    (Some (Yession.Domain.Sandboxes.Derived [ Yession.Domain.Sandboxes.TemplatePart.Session Yession.Domain.Sandboxes.SessionValue.Version ]))
                    "a reference, not text"
                Expect.equal (Yession.Session.ConfigFile.parseSandbox (Yession.Session.ConfigFile.encodeSandbox decl)) (Ok decl) "and it crosses the gate as written"

        testCase "a session reference is answered with what the session says, composed in place" <| fun () ->
            let template = Yession.Domain.Sandboxes.EnvTemplate.parse "yession/${session.version}" |> expect
            let answered = Yession.Domain.Sandboxes.EnvTemplate.answer (fun _ -> "1.2.3-beta.4") template
            Expect.equal (Yession.Domain.Sandboxes.EnvTemplate.resolve (fun _ -> None) answered) "yession/1.2.3-beta.4" "the build, where the file put it"

        testCase "a session field this build does not know refuses the file, naming what it knows" <| fun () ->
            let text = "version: 2\nsandboxes:\n  dev:\n    env:\n      WHO: ${session.owner}\n"
            match RepoConfig.fromText text with
            | Ok _ -> failwith "should refuse"
            | Error e ->
                Expect.stringContains e "session.owner" "names what it did not know"
                Expect.stringContains e "${session.version}" "and what it does"

        // What crosses the command gate reads back as the file said it.
        testCase "a composed value crosses the command gate as it was written" <| fun () ->
            let text = "version: 2\nsandboxes:\n  dev:\n    env:\n      PATH: ${env.PATH}:/opt/$${odd}\n"
            match RepoConfig.fromText text with
            | Error e -> failwithf "the file should decode: %s" e
            | Ok read ->
                let decl = read.File.Sandboxes |> Map.find (SandboxName.create "dev" |> expect)
                Expect.equal (Yession.Session.ConfigFile.parseSandbox (Yession.Session.ConfigFile.encodeSandbox decl)) (Ok decl) "the same declaration after the round trip"

        // A container is told what its declaration composed, and nothing copied from beneath.
        testCase "a sandbox's environment resolves each template over what lies beneath it" <| fun () ->
            let policy =
                { Support.emptyPolicy with
                    Env = Map.ofList [ "A", "1" ]
                    Derived =
                        Map.ofList
                            [ "PATH", [ Yession.Domain.Sandboxes.TemplatePart.Beneath "PATH"; Yession.Domain.Sandboxes.TemplatePart.Literal ":/x" ] ] }
            Expect.equal
                (Sandboxes.environment (Map.ofList [ "PATH", "/img"; "HOME", "/root" ]) policy)
                (Map.ofList [ "A", "1"; "PATH", "/img:/x" ])
                "the image's PATH extended; its HOME is the image's to set, not copied"

        // What the credential proxy's references are for, read against each other: a note for
        // each way a declaration can ask for the proxy and still not get through it.
        testCase "a proxy URL with nothing trusting the proxy is noted where it was written" <| fun () ->
            let text = "version: 2\nsandboxes:\n  dev:\n    env:\n      HTTPS_PROXY: ${proxy.https}\n"
            match RepoConfig.fromText text with
            | Error e -> failwithf "the file should decode: %s" e
            | Ok read ->
                Expect.equal
                    (read.Findings |> List.map Yession.Domain.Sandboxes.LocatedFinding.where)
                    [ "sandboxes.dev.env.HTTPS_PROXY, line 5" ]
                    "the variable that sends HTTPS there"

        testCase "a token lent with no proxy URL beside it is noted" <| fun () ->
            let text = "version: 2\nsandboxes:\n  dev:\n    env:\n      GH_TOKEN: ${github.token}\n"
            match RepoConfig.fromText text with
            | Error e -> failwithf "the file should decode: %s" e
            | Ok read ->
                Expect.equal
                    (read.Findings |> List.map Yession.Domain.Sandboxes.LocatedFinding.where)
                    [ "sandboxes.dev.env.GH_TOKEN, line 5" ]
                    "the variable lent it"

        testCase "the proxy's authority alone as a whole trust store is noted" <| fun () ->
            let text = "version: 2\nsandboxes:\n  dev:\n    env:\n      SSL_CERT_DIR: ${proxy.ca-dir}\n"
            match RepoConfig.fromText text with
            | Error e -> failwithf "the file should decode: %s" e
            | Ok read ->
                Expect.equal
                    (read.Findings |> List.map Yession.Domain.Sandboxes.LocatedFinding.where)
                    [ "sandboxes.dev.env.SSL_CERT_DIR, line 5" ]
                    "the variable it replaces"

        // The declaration each of those notes asks for, which none of them has anything to say
        // about.
        testCase "a sandbox that asks for the proxy the whole way is not noted" <| fun () ->
            let text =
                "version: 2\nsandboxes:\n  dev:\n    env:\n      HTTPS_PROXY: ${proxy.https}\n      SSL_CERT_DIR: ${env.SSL_CERT_DIR}:${proxy.ca-dir}\n      GH_TOKEN: ${github.token}\n"
            match RepoConfig.fromText text with
            | Error e -> failwithf "the file should decode: %s" e
            | Ok read -> Expect.isEmpty read.Findings "nothing noted"

        testCase "a file with nothing to say about it has no notes" <| fun () ->
            let text = "version: 2\nsandboxes:\n  dev:\n    uses: [ nix ]\n    wants: [ cache ]\n"
            match RepoConfig.fromText text with
            | Error e -> failwithf "the file should decode: %s" e
            | Ok read -> Expect.isEmpty read.Findings "nothing noted"
    ]

// --- The fold ----------------------------------------------------------------------------

/// A repos service that answers the listing with `reads` and refuses everything else.
let private reposReading (reposDir: string) (reads: unit -> RepoRead list) : Repos.ReposService =
    let denied _ = async { return Error "not part of this test" }
    { AddRepo = fun _ _ -> denied ()
      ListRepos = fun () -> async { return Ok (reads ()) }
      SwitchBranch = fun _ _ _ _ -> denied ()
      FetchRepo = fun _ _ -> denied ()
      RepoStatus = fun _ -> denied ()
      RepoLog = fun _ -> denied ()
      RepoDiff = fun _ -> denied ()
      ReadCommitted = committedIn reposDir
      RemoveRepo = fun _ _ _ -> denied () }

let private listedAt (reposDir: string) (r: RepoRef) : RepoRead =
    RepoRead.Listed
        { Repo = r
          Branch = "main"
          Dirty = false
          Path = sprintf "%s/%s" reposDir (RepoRef.relativePath r) }

/// A checkout git could not read, as the listing reports one.
let private unreadableAt (reposDir: string) (r: RepoRef) : RepoRead =
    RepoRead.Unreadable
        { Repo = r
          Path = sprintf "%s/%s" reposDir (RepoRef.relativePath r)
          Reason = "fatal: not a git repository: /repos/elsewhere" }

/// A repos service that knows about `listed` and nothing else. Only `ListRepos` is the
/// fold's business; every other verb is somebody else's suite.
let private reposOver (reposDir: string) (listed: RepoRef list) : Repos.ReposService =
    reposReading reposDir (fun () -> listed |> List.map (listedAt reposDir))

/// The same service with a listing that fails while `failing` says so — the whole listing, as
/// one that could not be made at all does. (A checkout git cannot read is not this: it is a
/// row of its own, `RepoRead.Unreadable`.)
let private listingFailingWhile (failing: unit -> bool) (service: Repos.ReposService) : Repos.ReposService =
    { service with
        ListRepos =
            fun () ->
                async {
                    if failing () then return Error "git -C failed (exit 128)"
                    else return! service.ListRepos ()
                } }

/// A gate that approves everything and records what it was asked to do, so a test can see
/// which declarations reached it and in what shape.
let private recordingGate (seen: ResizeArray<GatedCall>) : RunGatedCommand =
    fun call ->
        async {
            seen.Add call
            return Ok { Handle = None; Tool = call.Tool; Summary = call.Summary; Status = CommandRan "ok" }
        }

/// A gate that refuses everything, with a reason a row has to carry.
let private refusingGate (reason: string) : RunGatedCommand =
    fun call ->
        async {
            return
                Ok
                    { Handle = None
                      Tool = call.Tool
                      Summary = call.Summary
                      Status = CommandRefusedBy (ActorRef.System, Some reason) }
        }

let private cell (value: 'a) = fun () -> value

/// A session whose host declares no resources: every selection comes to nothing. The
/// capability notes have their own cases, where a real vocabulary is worth the setup.
let private noCapabilities : ResourceName list * ResourceName list -> Result<RepoSandboxes.RepoCapabilities, string> =
    fun _ -> Ok { RepoSandboxes.Granted = []; RepoSandboxes.GitHubRoutes = [] }

/// A vocabulary that grants exactly these lines and forwards nobody's GitHub, so the fold asks
/// nobody to trust anything in cases about something else.
let private granting (lines: string list) : ResourceName list * ResourceName list -> Result<RepoSandboxes.RepoCapabilities, string> =
    fun _ -> Ok { RepoSandboxes.Granted = lines; RepoSandboxes.GitHubRoutes = [] }

/// A vocabulary that forwards GitHub by these routes: what a repo asks people to trust it with.
let private forwardingGitHub (routes: ConnectionRoute list) : ResourceName list * ResourceName list -> Result<RepoSandboxes.RepoCapabilities, string> =
    fun _ ->
        Ok
            { RepoSandboxes.Granted = routes |> List.map (fun route -> sprintf "conn:github/%s" (ConnectionRoute.name route))
              RepoSandboxes.GitHubRoutes = routes }

/// The fold over nobody's trust: every case here that is not about trust.
let private createFold repos sandboxes gate log capabilities =
    RepoSandboxes.create repos (RepoTrusts.none "nobody keeps trust in these cases") sandboxes gate log capabilities

let private ada = Principal.User (UserId.create "ada" |> expect)
let private bob = Principal.User (UserId.create "bob" |> expect)
/// Both routes, in the order a set lists them.
let private both = [ ConnectionRoute.Git; ConnectionRoute.Api ]

/// What each person's record says, in memory, and whose cannot be read: somebody not signed in
/// to this launch. An answer writes the record, as the Manager would.
let private trustOf (answers: (Principal * Set<ConnectionRoute>) list) (unreadable: Principal list) : RepoTrusts =
    let key (owner: Yession.Domain.Access.CredentialOwner) = Yession.Domain.Access.CredentialOwner.describe owner
    let kept =
        ref (
            answers
            |> List.map (fun (person, routes) ->
                key (RepoTrust.ownerOf person),
                { RepoTrust.Repo = "octo/hello"; RepoTrust.Granted = routes; RepoTrust.Seen = routes })
            |> Map.ofList
        )
    let away = unreadable |> List.map (RepoTrust.ownerOf >> key) |> Set.ofList
    let read owner =
        async {
            if Set.contains (key owner) away then return Error "user is not signed in to this session"
            else return Ok (Map.tryFind (key owner) kept.Value)
        }
    { RepoTrusts.Read = fun owner _ -> read owner
      RepoTrusts.Answer =
        fun owner repo shown ticked ->
            async {
                let next = RepoTrust.answer repo shown ticked (Map.tryFind (key owner) kept.Value)
                kept.Value <- Map.add (key owner) next kept.Value
                return Ok next
            }
      RepoTrusts.Granted =
        fun owner _ forwarded ->
            async {
                match! read owner with
                | Ok trust -> return RepoTrust.granted owner forwarded trust
                | Error _ -> return RepoTrust.granted owner forwarded None
            } }

/// A repo whose one sandbox selects a resource, which `trustingFold`'s vocabulary says forwards
/// GitHub by both routes.
let private selecting () =
    let r = repo "octo/hello"
    r, checkout r (Some "version: 2\nsandboxes:\n  dev:\n    uses: [ github ]\n")

/// The fold over these people's trust, for a repo whose sandboxes forward GitHub by both routes.
let private trustingFold (trusts: RepoTrusts) (r: RepoRef) (dir: string) (gate: RunGatedCommand) (log: EventLog<SessionEvent>) =
    RepoSandboxes.create (cell (Some (reposOver dir [ r ]))) trusts (cell WorkSandboxes.unavailable) gate log (forwardingGitHub both)

/// How many cards the log has asked.
let private trustAsks (log: EventLog<SessionEvent>) =
    async {
        let! page = log.Read None System.Int32.MaxValue
        return
            page.Events
            |> List.filter (fun e -> match e.Event with SessionEvent.RepoTrustAsked _ -> true | _ -> false)
            |> List.length
    }

let private trustAsked (r: RepoRef) (routes: ConnectionRoute list) =
    SessionEvent.RepoTrustAsked
        { RepoTrustAsked.MessageId = MessageId.create "m1" |> expect
          RepoTrustAsked.Repo = r
          RepoTrustAsked.Routes = routes
          RepoTrustAsked.Actor = ActorRef.Configured r
          RepoTrustAsked.CausedBy = None }

let private trusted (r: RepoRef) (person: Principal) =
    SessionEvent.RepoTrusted
        { RepoTrusted.MessageId = MessageId.create "m2" |> expect
          RepoTrusted.Repo = r
          RepoTrusted.By = person
          RepoTrusted.Actor = Principal.toActor person }

/// The log already says this repo runs here with this person's trust.
let private trustedHere (log: EventLog<SessionEvent>) (r: RepoRef) (person: Principal) =
    async {
        let! _ = log.Append (Principal.toActor person) (trusted r person)
        return ()
    }

/// A host offering a store (`nix`) and a connection (`github`), each selection put through
/// the rule the session applies (`RepoSandboxes.capabilitiesOn`) — for the cases about
/// WHETHER a fold waits for somebody, which a flag the case chose could not answer.
let private offering : ResourceName list * ResourceName list -> Result<RepoSandboxes.RepoCapabilities, string> =
    fun selection ->
        let profile =
            OperatorProfile.parse
                """{ "version": 1,
                     "resources": { "nix": { "mount": { "from": "/nix" } },
                                    "github": { "connection": { "github": [ "git" ] } } } }"""
            |> expect
        RepoSandboxes.capabilitiesOn
            HostLimits.unlimited
            (ConnectionName.create "github" |> expect)
            (fun uses wants -> ResourceProfile.resolve profile.Resources (ResourceProfile.selected profile.Resources uses wants))
            selection

/// A log for the fold to read its own history from and append its notes to. Fresh per case:
/// what a fold says is a delta against what the session was already told, so a shared log
/// would make one case's notes another's silence.
let private foldLog () =
    Yession.Session.InMemoryEventLog.create
        (SessionId.create "fold" |> expect)
        (fun () -> System.DateTimeOffset.UtcNow)

let foldTests =
    testList "the yession.yaml fold (Plan 27)" [

        // The fold's whole job: a declaration becomes one of the commands the session
        // already has, through the same gate the agent's goes through.
        testCaseAsync "every declaration reaches the gate as a start_work_sandbox" <|
            async {
                let one, two = repo "octo/one", repo "octo/two"
                let dir = TestFiles.tempDir "yession-config-"
                for r in [ one; two ] do TestFiles.ensureDir (sprintf "%s/%s" dir (RepoRef.relativePath r))
                TestFiles.write (pathIn dir one) "version: 2\nsandboxes:\n  dev: {}\n"
                TestFiles.write (pathIn dir two) "version: 2\nsandboxes:\n  dev: {}\n  gate: {}\n"
                let seen = ResizeArray<GatedCall> ()
                let folded =
                    createFold
                        (cell (Some (reposOver dir [ one; two ])))
                        (cell WorkSandboxes.unavailable)
                        (recordingGate seen)
                        (foldLog ())
                        noCapabilities
                do! folded.Fold (FoldCause.Booted boot) CredentialFor.Deployment
                Expect.equal (seen |> Seq.map (fun c -> c.Tool) |> Set.ofSeq) (Set.ofList [ "start_work_sandbox" ]) "one verb, no other"
                Expect.equal (seen.Count) 3 "every declaration in every file, and nothing else"
            }

        // A repo's sandboxes come up AT ONCE, not one waiting on the one before it — the
        // `dev` and `gate` of a checkout no longer queue behind each other. Pinned by a gate
        // whose calls each block until TWO have arrived: a parallel fold starts both, so both
        // arrive and proceed; a sequential fold starts the second only once the first has
        // finished, so it parks at one and never reaches two. Reverting the fold to
        // `Async.Sequential` turns this red.
        testCaseAsync "a repo's sandboxes come up in parallel, not one waiting on the next" <|
            async {
                let r = repo "octo/hello"
                let dir = checkout r (Some "version: 2\nsandboxes:\n  dev: {}\n  gate: {}\n")
                let mutable arrived = 0
                let waiters = ResizeArray<unit -> unit> ()
                let barrier : RunGatedCommand =
                    fun call ->
                        async {
                            do!
                                Async.FromContinuations (fun (cont, _, _) ->
                                    arrived <- arrived + 1
                                    if arrived >= 2 then
                                        cont ()
                                        for w in waiters do w ()
                                        waiters.Clear ()
                                    else
                                        waiters.Add cont)
                            return Ok { Handle = None; Tool = call.Tool; Summary = call.Summary; Status = CommandRan "ok" }
                        }
                let folded =
                    createFold (cell (Some (reposOver dir [ r ]))) (cell WorkSandboxes.unavailable) barrier (foldLog ()) noCapabilities
                // Started as a child: a sequential fold would deadlock here (the first call
                // waits on a second the fold has not started), so we do NOT await it to a
                // finish — we check that both declarations reached the gate.
                let! _ = Async.StartChild (folded.Fold (FoldCause.Booted boot) CredentialFor.Deployment)
                let! _ = Support.settledWithin 5000 (fun () -> arrived >= 2)
                Expect.equal arrived 2 "both sandboxes reached the gate before either finished; a sequential fold parks at one"
            }

        // Attribution is the point of `ActorRef.Configured`: freshly-cloned code is less
        // trusted than the agent, so the timeline has to say which file asked.
        testCaseAsync "each ask is authored by the file that made it" <|
            async {
                let r = repo "octo/hello"
                let dir = checkout r (Some "version: 2\nsandboxes:\n  dev: {}\n")
                let seen = ResizeArray<GatedCall> ()
                let folded =
                    createFold (cell (Some (reposOver dir [ r ]))) (cell WorkSandboxes.unavailable) (recordingGate seen) (foldLog ()) noCapabilities
                do! folded.Fold (FoldCause.Booted boot) CredentialFor.Deployment
                Expect.equal (Authority.author seen.[0].Authority) (ActorRef.Configured r) "the repo's own file"
                Expect.equal (Authority.onBehalfOf seen.[0].Authority) None "and a boot fold borrows nobody's authority"
            }

        testCaseAsync "a triggered fold runs on the authority of whoever triggered it" <|
            async {
                let r = repo "octo/hello"
                let dir = checkout r (Some "version: 2\nsandboxes:\n  dev:\n    uses: [ github ]\n")
                let seen = ResizeArray<GatedCall> ()
                let folded =
                    createFold (cell (Some (reposOver dir [ r ]))) (cell WorkSandboxes.unavailable) (recordingGate seen) (foldLog ()) noCapabilities
                let ada = Principal.User (UserId.create "ada" |> expect)
                do! folded.Fold (FoldCause.Connected (CredentialFor.Person ada)) (CredentialFor.Person ada)
                Expect.equal (Authority.credential seen.[0].Authority) (CredentialFor.Person ada) "whose authority the started sandbox is on"
            }

        // `start_work_sandbox` decides "already running?" before it starts anything, and a
        // start can be a container to pull — so two folds in flight at once would both find
        // nothing running and both start it. A person arrives seconds after the boot fold
        // began, which is exactly that overlap; their fold has to wait its turn.
        testCaseAsync "a fold asked for while one is in flight waits for it" <|
            async {
                let r = repo "octo/hello"
                let dir = checkout r (Some "version: 2\nsandboxes:\n  dev: {}\n")
                let seen = ResizeArray<Principal option> ()
                let mutable release : (unit -> unit) option = None
                // A gate whose FIRST call holds until released, so the first fold is
                // caught mid-flight with a second one asked for.
                let holding : RunGatedCommand =
                    fun call ->
                        async {
                            seen.Add (Authority.onBehalfOf call.Authority)
                            if seen.Count = 1 then
                                do! Async.FromContinuations (fun (cont, _, _) -> release <- Some cont)
                            return Ok { Handle = None; Tool = call.Tool; Summary = call.Summary; Status = CommandRan "ok" }
                        }
                let folded =
                    createFold (cell (Some (reposOver dir [ r ]))) (cell WorkSandboxes.unavailable) holding (foldLog ()) noCapabilities
                let ada = Principal.User (UserId.create "ada" |> expect)
                let! first = Async.StartChild (folded.Fold (FoldCause.Booted boot) CredentialFor.Deployment)
                do! Support.waitUntil "the first fold to be held at the gate" (fun () -> release.IsSome)
                let! second = Async.StartChild (folded.Fold (FoldCause.Connected (CredentialFor.Person ada)) (CredentialFor.Person ada))
                // What is asserted is that the second has NOT arrived, which only a window can show.
                do! Support.quietFor 20
                Expect.equal (List.ofSeq seen) [ None ] "the first is at the gate; the second has not reached it"
                release.Value ()
                do! first
                do! second
                Expect.equal (List.ofSeq seen) [ None; Some ada ] "and then it does, after"
            }

        // The row is the fold's only surface for a declaration that did not become a
        // sandbox, so a refusal that produced no row would be a silent one.
        testCaseAsync "a refused declaration is a row saying which, and why" <|
            async {
                let r = repo "octo/hello"
                let dir = checkout r (Some "version: 2\nsandboxes:\n  dev: {}\n")
                let folded =
                    createFold
                        (cell (Some (reposOver dir [ r ])))
                        (cell WorkSandboxes.unavailable)
                        (refusingGate "registry.npmjs.org is not in this session's egress")
                        (foldLog ())
                        noCapabilities
                do! folded.Fold (FoldCause.Booted boot) CredentialFor.Deployment
                match folded.Outcomes () with
                | [ outcome ] ->
                    Expect.equal outcome.Repo r "the repo whose file asked"
                    Expect.equal (outcome.Sandbox |> Option.map SandboxRef.render) (Some "octo/hello:dev") "the sandbox it asked for"
                    Expect.equal outcome.Problem (Some "registry.npmjs.org is not in this session's egress") "and the reason it did not get it"
                | other -> failwithf "expected one row, got %A" other
            }

        testCaseAsync "a declaration that became a sandbox is a row with no problem" <|
            async {
                let r = repo "octo/hello"
                let dir = checkout r (Some "version: 2\nsandboxes:\n  dev: {}\n")
                let folded =
                    createFold
                        (cell (Some (reposOver dir [ r ])))
                        (cell WorkSandboxes.unavailable)
                        (recordingGate (ResizeArray<GatedCall> ()))
                        (foldLog ())
                        noCapabilities
                do! folded.Fold (FoldCause.Booted boot) CredentialFor.Deployment
                Expect.equal (folded.Outcomes () |> List.map (fun o -> o.Problem)) [ None ] "nothing to report is nothing to report"
            }

        // A file nobody can read is fixed by whoever wrote the YAML; a sandbox that would
        // not start is fixed by whoever wrote the sandbox. Different people, so the row
        // distinguishes them.
        testCaseAsync "an unreadable file is a row about the file, not about a sandbox" <|
            async {
                let r = repo "octo/hello"
                let dir = checkout r (Some "version: 2\nsandboxes:\n  dev:\n    nope: 1\n")
                let folded =
                    createFold
                        (cell (Some (reposOver dir [ r ])))
                        (cell WorkSandboxes.unavailable)
                        (recordingGate (ResizeArray<GatedCall> ()))
                        (foldLog ())
                        noCapabilities
                do! folded.Fold (FoldCause.Booted boot) CredentialFor.Deployment
                match folded.Outcomes () with
                | [ outcome ] ->
                    Expect.equal outcome.Sandbox None "there is no sandbox to name — the file did not parse"
                    Expect.isTrue (outcome.Problem |> Option.exists (fun p -> p.Contains "nope")) "the key that broke it"
                | other -> failwithf "expected one row, got %A" other
            }

        // The incident (session TNQDWN3T): a stray checkout git could not read stopped the
        // whole listing, so the fold read no file and a healthy repo's sandboxes were
        // refused as undeclared.
        testCaseAsync "a checkout git cannot read does not stop its neighbour's declarations" <|
            async {
                let r, stray = repo "octo/hello", repo "octo/hello-phone"
                let dir = checkout r (Some "version: 2\nsandboxes:\n  dev: {}\n")
                let folded =
                    createFold
                        (cell (Some (reposReading dir (fun () -> [ listedAt dir r; unreadableAt dir stray ]))))
                        (cell WorkSandboxes.unavailable)
                        (recordingGate (ResizeArray<GatedCall> ()))
                        (foldLog ())
                        noCapabilities
                do! folded.Fold (FoldCause.Booted boot) CredentialFor.Deployment
                Expect.equal
                    (folded.Declared () |> Declarations.known |> Map.toList |> List.map (fst >> SandboxRef.render))
                    [ "octo/hello:dev" ]
                    "the healthy repo's declaration is read"
            }

        testCaseAsync "a checkout git cannot read leaves the fold read, not unreadable" <|
            async {
                let r, stray = repo "octo/hello", repo "octo/hello-phone"
                let dir = checkout r (Some "version: 2\nsandboxes:\n  dev: {}\n")
                let folded =
                    createFold
                        (cell (Some (reposReading dir (fun () -> [ listedAt dir r; unreadableAt dir stray ]))))
                        (cell WorkSandboxes.unavailable)
                        (recordingGate (ResizeArray<GatedCall> ()))
                        (foldLog ())
                        noCapabilities
                do! folded.Fold (FoldCause.Booted boot) CredentialFor.Deployment
                Expect.isNone (Declarations.whyNotCurrent (folded.Declared ())) "the files that could be read were"
            }

        testCaseAsync "a checkout git cannot read is a row naming it" <|
            async {
                let stray = repo "octo/hello-phone"
                let dir = TestFiles.tempDir "yession-config-"
                let folded =
                    createFold
                        (cell (Some (reposReading dir (fun () -> [ unreadableAt dir stray ]))))
                        (cell WorkSandboxes.unavailable)
                        (recordingGate (ResizeArray<GatedCall> ()))
                        (foldLog ())
                        noCapabilities
                do! folded.Fold (FoldCause.Booted boot) CredentialFor.Deployment
                match folded.Outcomes () with
                | [ outcome ] ->
                    Expect.equal
                        (outcome.Repo, outcome.Problem |> Option.exists (fun p -> p.Contains "/repos/elsewhere"))
                        (stray, true)
                        "the checkout, with git's reason"
                | other -> failwithf "expected one row, got %A" other
            }

        // A checkout that blinked says nothing about its file, so what it declared before
        // stands rather than reading as "declares nothing".
        testCaseAsync "a checkout that becomes unreadable keeps what it declared" <|
            async {
                let r = repo "octo/hello"
                let dir = checkout r (Some "version: 2\nsandboxes:\n  dev: {}\n")
                let readable = ref true
                let folded =
                    createFold
                        (cell (Some (reposReading dir (fun () -> [ (if readable.Value then listedAt dir r else unreadableAt dir r) ]))))
                        (cell WorkSandboxes.unavailable)
                        (recordingGate (ResizeArray<GatedCall> ()))
                        (foldLog ())
                        noCapabilities
                do! folded.Fold (FoldCause.Booted boot) CredentialFor.Deployment
                readable.Value <- false
                do! folded.Fold (FoldCause.Booted boot) CredentialFor.Deployment
                Expect.equal
                    (folded.Declared () |> Declarations.known |> Map.toList |> List.map (fst >> SandboxRef.render))
                    [ "octo/hello:dev" ]
                    "the earlier declaration stands"
            }

        // Re-folding is what makes the trigger cheap enough to run after every repo verb,
        // and the property that buys it is `ensure`'s, not this module's.
        testCaseAsync "a repo with no file contributes nothing and refuses nothing" <|
            async {
                let r = repo "octo/hello"
                let dir = checkout r None
                let seen = ResizeArray<GatedCall> ()
                let folded =
                    createFold (cell (Some (reposOver dir [ r ]))) (cell WorkSandboxes.unavailable) (recordingGate seen) (foldLog ()) noCapabilities
                do! folded.Fold (FoldCause.Booted boot) CredentialFor.Deployment
                Expect.equal seen.Count 0 "nothing was asked for"
                Expect.equal (folded.Outcomes ()) [] "and nothing is wrong"
            }

        // A listing that failed used to leave the declarations empty, which every reader took
        // to mean "no file declares anything": `repo_config` said `(none)` and a start said the
        // repo "declares no sandboxes", over a file that declared two.
        testCaseAsync "a fold whose listing failed is not read as declaring nothing" <|
            async {
                let r = repo "octo/hello"
                let dir = checkout r (Some "version: 2\nsandboxes:\n  dev: {}\n")
                let folded =
                    createFold
                        (cell (Some (listingFailingWhile (fun () -> true) (reposOver dir [ r ]))))
                        (cell WorkSandboxes.unavailable)
                        (recordingGate (ResizeArray<GatedCall> ()))
                        (foldLog ())
                        noCapabilities
                do! folded.Fold (FoldCause.Booted boot) CredentialFor.Deployment
                Expect.equal (folded.Declared ()) (Unreadable ("git -C failed (exit 128)", None)) "unreadable, with the reason"
            }

        testCaseAsync "a fold that reads after a failed one is the current answer again" <|
            async {
                let r = repo "octo/hello"
                let dir = checkout r (Some "version: 2\nsandboxes:\n  dev: {}\n")
                let mutable failing = true
                let folded =
                    createFold
                        (cell (Some (listingFailingWhile (fun () -> failing) (reposOver dir [ r ]))))
                        (cell WorkSandboxes.unavailable)
                        (recordingGate (ResizeArray<GatedCall> ()))
                        (foldLog ())
                        noCapabilities
                do! folded.Fold (FoldCause.Booted boot) CredentialFor.Deployment
                failing <- false
                do! folded.Fold (FoldCause.Booted boot) CredentialFor.Deployment
                Expect.equal
                    (match folded.Declared () with
                     | Read declared -> declared |> Map.toList |> List.map (fst >> SandboxRef.render)
                     | other -> failwithf "expected a read, got %A" other)
                    [ "octo/hello:dev" ]
                    "what the file declares"
            }

        // The files did not go anywhere when a re-read failed, so what the last read found
        // still stands. It is marked, because it may no longer be what the files say.
        testCaseAsync "a failed re-read keeps the last read, marked as not current" <|
            async {
                let r = repo "octo/hello"
                let dir = checkout r (Some "version: 2\nsandboxes:\n  dev: {}\n")
                let mutable failing = false
                let folded =
                    createFold
                        (cell (Some (listingFailingWhile (fun () -> failing) (reposOver dir [ r ]))))
                        (cell WorkSandboxes.unavailable)
                        (recordingGate (ResizeArray<GatedCall> ()))
                        (foldLog ())
                        noCapabilities
                do! folded.Fold (FoldCause.Booted boot) CredentialFor.Deployment
                failing <- true
                do! folded.Fold (FoldCause.Booted boot) CredentialFor.Deployment
                Expect.equal
                    (match folded.Declared () with
                     | Unreadable (_, Some declared) -> declared |> Map.toList |> List.map (fst >> SandboxRef.render)
                     | other -> failwithf "expected the last read kept beside the failure, got %A" other)
                    [ "octo/hello:dev" ]
                    "the declarations the last good read found"
            }

        testCaseAsync "repo_config names a listing that failed" <|
            async {
                let r = repo "octo/hello"
                let dir = checkout r (Some "version: 2\nsandboxes:\n  dev: {}\n")
                let folded =
                    createFold
                        (cell (Some (listingFailingWhile (fun () -> true) (reposOver dir [ r ]))))
                        (cell WorkSandboxes.unavailable)
                        (recordingGate (ResizeArray<GatedCall> ()))
                        (foldLog ())
                        noCapabilities
                do! folded.Fold (FoldCause.Booted boot) CredentialFor.Deployment
                let! answer = (RepoSandboxes.query (fun () -> folded)).Read ()
                let problems =
                    match answer with
                    | Ok (RowsOf rows) ->
                        rows
                        |> List.choose (fun row ->
                            match List.tryFind (fst >> (=) "problem") row with
                            | Some (_, CellText text) -> Some text
                            | _ -> None)
                    | other -> failwithf "expected rows, got %A" other
                Expect.isTrue
                    (problems |> List.exists (fun text -> text.Contains "git -C failed (exit 128)"))
                    "a row carrying why the files could not be read"
            }

        // The query answers "what became of every declaration" to whoever asks. This is the
        // other half: a refusal SAYS so, once, where a person is already looking — because a
        // person who has just broken their own file has no reason to suspect a question.
        testCaseAsync "a refused declaration says so on the timeline, attributed to the file" <|
            async {
                let r = repo "octo/hello"
                let dir = checkout r (Some "version: 2\nsandboxes:\n  dev: {}\n")
                let log = foldLog ()
                let folded =
                    createFold
                        (cell (Some (reposOver dir [ r ])))
                        (cell WorkSandboxes.unavailable)
                        (refusingGate "registry.npmjs.org is not in this session's egress")
                        log
                        noCapabilities
                do! folded.Fold (FoldCause.Booted boot) CredentialFor.Deployment
                let! page = log.Read None System.Int32.MaxValue
                match page.Events |> List.choose (fun e -> match e.Event with SessionEvent.RepoConfigRefused n -> Some n | _ -> None) with
                | [ note ] ->
                    Expect.equal note.Repo r "the repo whose file asked"
                    Expect.equal (note.Sandbox |> Option.map SandboxRef.render) (Some "octo/hello:dev") "the declaration that was refused"
                    Expect.equal note.Reason "registry.npmjs.org is not in this session's egress" "said whole, in the refusal's own words"
                    Expect.equal note.Actor (ActorRef.Configured r) "the file is the party that asked, so it is the party that was refused"
                | other -> failwithf "expected one note, got %A" other
            }

        // Why the file was read this time is on the note, as on the starts it did not produce.
        testCaseAsync "a refused declaration records what caused the fold" <|
            async {
                let r = repo "octo/hello"
                let dir = checkout r (Some "version: 2\nsandboxes:\n  dev: {}\n")
                let log = foldLog ()
                let folded =
                    createFold
                        (cell (Some (reposOver dir [ r ])))
                        (cell WorkSandboxes.unavailable)
                        (refusingGate "registry.npmjs.org is not in this session's egress")
                        log
                        noCapabilities
                do! folded.Fold (FoldCause.Booted boot) CredentialFor.Deployment
                let! page = log.Read None System.Int32.MaxValue
                match page.Events |> List.choose (fun e -> match e.Event with SessionEvent.RepoConfigRefused n -> Some n | _ -> None) with
                | [ note ] -> Expect.equal note.CausedBy (Some (Cause.Item boot)) "the boot that read the file"
                | other -> failwithf "expected one note, got %A" other
            }

        // The fold runs after every repo verb. A note per outcome would rebuild exactly the
        // accumulation the query was chosen to avoid — on the surface it was avoided for.
        testCaseAsync "the same refusal folded twice is said once" <|
            async {
                let r = repo "octo/hello"
                let dir = checkout r (Some "version: 2\nsandboxes:\n  dev: {}\n")
                let log = foldLog ()
                let folded =
                    createFold
                        (cell (Some (reposOver dir [ r ])))
                        (cell WorkSandboxes.unavailable)
                        (refusingGate "the ceiling is closed")
                        log
                        noCapabilities
                do! folded.Fold (FoldCause.Booted boot) CredentialFor.Deployment
                do! folded.Fold (FoldCause.Booted boot) CredentialFor.Deployment
                do! folded.Fold (FoldCause.Booted boot) CredentialFor.Deployment
                let! page = log.Read None System.Int32.MaxValue
                let notes =
                    page.Events |> List.choose (fun e -> match e.Event with SessionEvent.RepoConfigRefused n -> Some n.Reason | _ -> None)
                Expect.equal notes [ "the ceiling is closed" ] "three folds, one thing to say"
            }

        // A note is news once. The fold runs after every repo verb, and a note repeated at
        // each is a note people learn to stop reading.
        testCaseAsync "a note about a file is said once, however often the fold runs" <|
            async {
                let r = repo "octo/hello"
                let dir = checkout r (Some "version: 2\nsandboxes:\n  dev:\n    uses: [ nix ]\n    wants: [ nix ]\n")
                let log = foldLog ()
                let folded =
                    createFold
                        (cell (Some (reposOver dir [ r ])))
                        (cell WorkSandboxes.unavailable)
                        (recordingGate (ResizeArray ()))
                        log
                        noCapabilities
                do! folded.Fold (FoldCause.Booted boot) CredentialFor.Deployment
                do! folded.Fold (FoldCause.Booted boot) CredentialFor.Deployment
                let! page = log.Read None System.Int32.MaxValue
                let notes =
                    page.Events |> List.choose (fun e -> match e.Event with SessionEvent.RepoConfigWarned n -> Some n.Where | _ -> None)
                Expect.equal notes [ "sandboxes.dev.wants[0], line 5" ] "two folds, one note"
            }

        // The suppression is on the REASON, so a refusal that moved is news. Without this
        // the note would be a cache of the first thing that ever went wrong.
        testCaseAsync "a refusal that changed is said again" <|
            async {
                let r = repo "octo/hello"
                let dir = checkout r (Some "version: 2\nsandboxes:\n  dev: {}\n")
                let log = foldLog ()
                let mutable why = "the ceiling is closed"
                let moving : RunGatedCommand =
                    fun call ->
                        async {
                            return Ok { Handle = None; Tool = call.Tool; Summary = call.Summary
                                        Status = CommandRefusedBy (ActorRef.System, Some why) }
                        }
                let folded =
                    createFold (cell (Some (reposOver dir [ r ]))) (cell WorkSandboxes.unavailable) moving log noCapabilities
                do! folded.Fold (FoldCause.Booted boot) CredentialFor.Deployment
                why <- "no credential to forward"
                do! folded.Fold (FoldCause.Booted boot) CredentialFor.Deployment
                let! page = log.Read None System.Int32.MaxValue
                let notes =
                    page.Events |> List.choose (fun e -> match e.Event with SessionEvent.RepoConfigRefused n -> Some n.Reason | _ -> None)
                Expect.equal notes [ "the ceiling is closed"; "no credential to forward" ] "a different reason is a different thing to say"
            }

        // A declaration that worked has always announced itself, as a WorkSandboxStarted.
        // This must not add a second voice for the same outcome.
        testCaseAsync "a declaration that became a sandbox says nothing here" <|
            async {
                let r = repo "octo/hello"
                let dir = checkout r (Some "version: 2\nsandboxes:\n  dev: {}\n")
                let log = foldLog ()
                let folded =
                    createFold
                        (cell (Some (reposOver dir [ r ])))
                        (cell WorkSandboxes.unavailable)
                        (recordingGate (ResizeArray<GatedCall> ()))
                        log
                        noCapabilities
                do! folded.Fold (FoldCause.Booted boot) CredentialFor.Deployment
                let! page = log.Read None System.Int32.MaxValue
                Expect.equal
                    (page.Events |> List.filter (fun e -> match e.Event with SessionEvent.RepoConfigRefused _ -> true | _ -> false))
                    []
                    "nothing went wrong, so nothing is said"
            }

        // A capability set is authored by whoever can push to the checkout, so a `uses:` line
        // added in a pull request takes effect the next time anybody touches a repo. Until
        // this, it did so with nobody told.
        testCaseAsync "what a repo asks for is said on the timeline" <|
            async {
                let r = repo "octo/hello"
                let dir = checkout r (Some "version: 2\nsandboxes:\n  dev:\n    uses: [ nix ]\n")
                let log = foldLog ()
                let folded =
                    createFold
                        (cell (Some (reposOver dir [ r ])))
                        (cell WorkSandboxes.unavailable)
                        (recordingGate (ResizeArray<GatedCall> ()))
                        log
                        (granting [ "path:/nix:ro"; "net:cache.nixos.org" ])
                do! folded.Fold (FoldCause.Booted boot) CredentialFor.Deployment
                let! page = log.Read None System.Int32.MaxValue
                match page.Events |> List.choose (fun e -> match e.Event with SessionEvent.RepoCapabilitiesChanged c -> Some c | _ -> None) with
                | [ note ] ->
                    Expect.equal note.Repo r "the repo whose file asked"
                    Expect.equal note.Granted [ "path:/nix:ro"; "net:cache.nixos.org" ] "the whole set, in the words a person is shown"
                    Expect.equal note.Actor (ActorRef.Configured r) "attributed to the file, like everything else it asks for"
                | other -> failwithf "expected one note, got %A" other
            }

        // Why the file was read this time is on the note, as on the starts it precedes.
        testCaseAsync "what a repo asks for records what caused the fold" <|
            async {
                let r = repo "octo/hello"
                let dir = checkout r (Some "version: 2\nsandboxes:\n  dev:\n    uses: [ nix ]\n")
                let log = foldLog ()
                let folded =
                    createFold
                        (cell (Some (reposOver dir [ r ])))
                        (cell WorkSandboxes.unavailable)
                        (recordingGate (ResizeArray<GatedCall> ()))
                        log
                        (granting [ "path:/nix:ro" ])
                do! folded.Fold (FoldCause.Booted boot) CredentialFor.Deployment
                let! page = log.Read None System.Int32.MaxValue
                match page.Events |> List.choose (fun e -> match e.Event with SessionEvent.RepoCapabilitiesChanged c -> Some c | _ -> None) with
                | [ note ] -> Expect.equal note.CausedBy (Some (Cause.Item boot)) "the boot that read the file"
                | other -> failwithf "expected one note, got %A" other
            }

        // The cause crosses the wire with the set it explains.
        testCase "what a repo asks for keeps its cause on the wire" <| fun () ->
            let r = repo "octo/hello"
            let envelope : EventEnvelope<SessionEvent> =
                { EventId = EventId.fresh ()
                  SessionId = SessionId.create "caps-session" |> expect
                  Offset = EventOffset.create 1L |> expect
                  Actor = ActorRef.Configured r
                  Timestamp = System.DateTimeOffset (2026, 8, 8, 10, 0, 0, System.TimeSpan.Zero)
                  Event =
                    SessionEvent.RepoCapabilitiesChanged
                        { RepoCapabilitiesChanged.MessageId = MessageId.create "m1" |> expect
                          RepoCapabilitiesChanged.Repo = r
                          RepoCapabilitiesChanged.Granted = [ "path:/nix:ro" ]
                          RepoCapabilitiesChanged.Actor = ActorRef.Configured r
                          RepoCapabilitiesChanged.CausedBy = Some (Cause.Item (MessageId.create "added" |> expect)) } }
            let json = Codec.toString Events.sessionEventEnvelope envelope
            Expect.equal (Codec.fromString Events.sessionEventEnvelope json |> expect) envelope "unchanged by the wire"

        // The fold runs after every repo verb. A note per fold would be the accumulation the
        // whole delta rule exists to avoid.
        testCaseAsync "an unchanged capability set is said once, however often the fold runs" <|
            async {
                let r = repo "octo/hello"
                let dir = checkout r (Some "version: 2\nsandboxes:\n  dev:\n    uses: [ nix ]\n")
                let log = foldLog ()
                let folded =
                    createFold
                        (cell (Some (reposOver dir [ r ])))
                        (cell WorkSandboxes.unavailable)
                        (recordingGate (ResizeArray<GatedCall> ()))
                        log
                        (granting [ "path:/nix:ro" ])
                do! folded.Fold (FoldCause.Booted boot) CredentialFor.Deployment
                do! folded.Fold (FoldCause.Booted boot) CredentialFor.Deployment
                do! folded.Fold (FoldCause.Booted boot) CredentialFor.Deployment
                let! page = log.Read None System.Int32.MaxValue
                let notes =
                    page.Events |> List.choose (fun e -> match e.Event with SessionEvent.RepoCapabilitiesChanged c -> Some c.Granted | _ -> None)
                Expect.equal notes [ [ "path:/nix:ro" ] ] "three folds, one thing to say"
            }

        // The case the whole note exists for: a checkout widening what it holds.
        testCaseAsync "a repo that widens what it asks for says so again" <|
            async {
                let r = repo "octo/hello"
                let dir = checkout r (Some "version: 2\nsandboxes:\n  dev:\n    uses: [ nix ]\n")
                let log = foldLog ()
                let mutable granted = [ "path:/nix:ro" ]
                let folded =
                    createFold
                        (cell (Some (reposOver dir [ r ])))
                        (cell WorkSandboxes.unavailable)
                        (recordingGate (ResizeArray<GatedCall> ()))
                        log
                        (granting granted)
                do! folded.Fold (FoldCause.Booted boot) CredentialFor.Deployment
                granted <- [ "path:/nix:ro"; "net:anywhere" ]
                do! folded.Fold (FoldCause.Booted boot) CredentialFor.Deployment
                let! page = log.Read None System.Int32.MaxValue
                let notes =
                    page.Events |> List.choose (fun e -> match e.Event with SessionEvent.RepoCapabilitiesChanged c -> Some c.Granted | _ -> None)
                Expect.equal (List.length notes) 2 "the widening is news"
                Expect.equal
                    (notes |> List.last)
                    [ "path:/nix:ro"; "net:anywhere" ]
                    "and the second note carries the whole set, so a person reads what it is now rather than what moved"
            }

        // A repo whose sandboxes forward nobody's GitHub spends nothing of anybody's, so it asks
        // nobody: what else it holds is the operator's to grant, in the profile.
        testCaseAsync "a repo that forwards no GitHub starts without asking anybody" <|
            async {
                let r, dir = selecting ()
                let seen = ResizeArray<GatedCall> ()
                let folded =
                    createFold
                        (cell (Some (reposOver dir [ r ]))) (cell WorkSandboxes.unavailable)
                        (recordingGate seen) (foldLog ()) offering
                do! folded.Fold (FoldCause.Booted boot) CredentialFor.Deployment
                Expect.equal seen.Count 1 "the declaration reached the gate"
            }

        testCaseAsync "a repo that forwards GitHub starts nothing until somebody here trusts it" <|
            async {
                let r, dir = selecting ()
                let seen = ResizeArray<GatedCall> ()
                let folded = trustingFold (trustOf [] []) r dir (recordingGate seen) (foldLog ())
                do! folded.Fold (FoldCause.Booted boot) CredentialFor.Deployment
                Expect.equal seen.Count 0 "nothing was started"
            }

        testCaseAsync "a repo waiting for trust says so in its row" <|
            async {
                let r, dir = selecting ()
                let folded = trustingFold (trustOf [] []) r dir (recordingGate (ResizeArray ())) (foldLog ())
                do! folded.Fold (FoldCause.Booted boot) CredentialFor.Deployment
                match folded.Outcomes () |> List.choose (fun outcome -> outcome.Problem) with
                | [ problem ] -> Expect.stringContains problem "trust" "the row says what it waits for"
                | other -> failwithf "expected one problem, got %A" other
            }

        // The card is a fact of the log, so it is asked once and stands: a fold that runs again
        // for any other reason says nothing new.
        testCaseAsync "a repo waiting for trust asks once, however often the fold runs" <|
            async {
                let r, dir = selecting ()
                let log = foldLog ()
                let folded = trustingFold (trustOf [] []) r dir (recordingGate (ResizeArray ())) log
                do! folded.Fold (FoldCause.Booted boot) CredentialFor.Deployment
                do! folded.Fold (FoldCause.Changed (r, None)) CredentialFor.Deployment
                let! asks = trustAsks log
                Expect.equal asks 1 "one card"
            }

        // Trusting is not a note taken for later. The person pressed Trust because they wanted
        // the repo to run, and nothing else here is going to ask on their behalf, so the fold runs
        // on the answer. Deliberately NO `Fold` call after `Trust` here: with one, this case is
        // green whether or not trusting does anything at all.
        testCaseAsync "trusting it starts it, without waiting for another repo verb" <|
            async {
                let r, dir = selecting ()
                let seen = ResizeArray<GatedCall> ()
                let folded = trustingFold (trustOf [] []) r dir (recordingGate seen) (foldLog ())
                do! folded.Fold (FoldCause.Booted boot) CredentialFor.Deployment
                let! trusted = folded.Trust ada r both both
                Expect.equal (trusted, seen.Count) (Ok (), 1) "the answer alone starts it"
            }

        testCaseAsync "a repo somebody trusts here starts on their authority" <|
            async {
                let r, dir = selecting ()
                let seen = ResizeArray<GatedCall> ()
                let folded = trustingFold (trustOf [] []) r dir (recordingGate seen) (foldLog ())
                let! _ = folded.Trust ada r both both
                Expect.equal
                    (seen |> Seq.map (fun call -> Authority.credential call.Authority) |> List.ofSeq)
                    [ CredentialFor.Person ada ]
                    "its setup is lent the GitHub of the person who trusts it"
            }

        // An answer is about the routes the card showed. A route it did not show is unanswered,
        // and a repo that asks for one still waits.
        testCaseAsync "an answer that did not see a route the repo asks for leaves it waiting" <|
            async {
                let r, dir = selecting ()
                let seen = ResizeArray<GatedCall> ()
                let folded = trustingFold (trustOf [] []) r dir (recordingGate seen) (foldLog ())
                let! _ = folded.Trust ada r [ ConnectionRoute.Git ] [ ConnectionRoute.Git ]
                Expect.equal seen.Count 0 "api is still unanswered"
            }

        // The one act besides answering the card that offers somebody's trust: adding the repo.
        // A person who trusts it already is not asked again, in this session or any other.
        testCaseAsync "adding a repo its person already trusts starts it without asking" <|
            async {
                let r, dir = selecting ()
                let seen = ResizeArray<GatedCall> ()
                let log = foldLog ()
                let folded = trustingFold (trustOf [ ada, Set.ofList both ] []) r dir (recordingGate seen) log
                do! folded.Fold (FoldCause.Offered (r, None, ada)) (CredentialFor.Person ada)
                let! asks = trustAsks log
                Expect.equal (seen.Count, asks) (1, 0) "started, and nobody asked"
            }

        // Trusting a repo is not lending your GitHub to every session it is open in. A person
        // who joins reads who the repo runs with; they never become it.
        testCaseAsync "somebody joining never starts a repo that waits for trust" <|
            async {
                let r, dir = selecting ()
                let seen = ResizeArray<GatedCall> ()
                let folded = trustingFold (trustOf [ bob, Set.ofList both ] []) r dir (recordingGate seen) (foldLog ())
                do! folded.Fold (FoldCause.Booted boot) CredentialFor.Deployment
                do! folded.Fold (FoldCause.Connected (CredentialFor.Person bob)) (CredentialFor.Person bob)
                Expect.equal seen.Count 0 "bob's trust is his, not this session's"
            }

        testCaseAsync "switching a branch does not make the switcher the repo's truster" <|
            async {
                let r, dir = selecting ()
                let seen = ResizeArray<GatedCall> ()
                let folded = trustingFold (trustOf [ bob, Set.ofList both ] []) r dir (recordingGate seen) (foldLog ())
                do! folded.Fold (FoldCause.Changed (r, Some (MessageId.create "switched" |> expect))) (CredentialFor.Person bob)
                Expect.equal seen.Count 0 "a switch offers nothing"
            }

        // After a restart the session cannot read the record of somebody not signed in to it
        // yet. The repo waits for them, and without a card: a card would hand it to whoever
        // came first.
        testCaseAsync "after a restart the repo waits for its truster without a card" <|
            async {
                let r, dir = selecting ()
                let seen = ResizeArray<GatedCall> ()
                let log = foldLog ()
                do! trustedHere log r ada
                let folded = trustingFold (trustOf [ ada, Set.ofList both ] [ ada ]) r dir (recordingGate seen) log
                do! folded.Fold (FoldCause.Booted boot) CredentialFor.Deployment
                let! asks = trustAsks log
                Expect.equal (seen.Count, asks) (0, 0) "held, and nobody else asked"
            }

        testCaseAsync "its truster coming back resumes the repo" <|
            async {
                let r, dir = selecting ()
                let seen = ResizeArray<GatedCall> ()
                let log = foldLog ()
                do! trustedHere log r ada
                let folded = trustingFold (trustOf [ ada, Set.ofList both ] []) r dir (recordingGate seen) log
                do! folded.Fold (FoldCause.Connected (CredentialFor.Person ada)) (CredentialFor.Person ada)
                Expect.equal
                    (seen |> Seq.map (fun call -> Authority.credential call.Authority) |> List.ofSeq)
                    [ CredentialFor.Person ada ]
                    "started again, on her authority"
            }

        testCaseAsync "a later answer does not take a repo from the person it runs with" <|
            async {
                let r, dir = selecting ()
                let seen = ResizeArray<GatedCall> ()
                let log = foldLog ()
                do! trustedHere log r ada
                let folded =
                    trustingFold (trustOf [ ada, Set.ofList both; bob, Set.ofList both ] []) r dir (recordingGate seen) log
                let! _ = folded.Trust bob r both both
                Expect.equal
                    (seen |> Seq.map (fun call -> Authority.credential call.Authority) |> List.ofSeq)
                    [ CredentialFor.Person ada ]
                    "it still runs with ada's"
            }

        testCaseAsync "a repo removed and added again is trusted by nobody here" <|
            async {
                let r, dir = selecting ()
                let seen = ResizeArray<GatedCall> ()
                let log = foldLog ()
                do! trustedHere log r ada
                let! _ =
                    log.Append
                        (Principal.toActor ada)
                        (SessionEvent.RepoRemoved
                            { RepoRemoved.MessageId = MessageId.create "removed" |> expect
                              RepoRemoved.Repo = r
                              RepoRemoved.Actor = Principal.toActor ada })
                let folded = trustingFold (trustOf [ ada, Set.ofList both ] []) r dir (recordingGate seen) log
                do! folded.Fold (FoldCause.Booted boot) CredentialFor.Deployment
                Expect.equal seen.Count 0 "her trust here went with the repo"
            }

        // A peer who did not sign in is still a PERSON, and on a deployment that trusts whoever
        // reaches it they are the only person there. What the record must not do is call them
        // a signed-in user: `Principal` keeps the two apart, and the event says which one it is.
        testCaseAsync "a peer who did not sign in may trust a repo, and is recorded as a peer" <|
            async {
                let r, dir = selecting ()
                let log = foldLog ()
                let folded = trustingFold (trustOf [] []) r dir (recordingGate (ResizeArray ())) log
                let anonymous = Principal.Peer (PeerId.create "peer-1" |> expect)
                let! _ = folded.Trust anonymous r both both
                let! page = log.Read None System.Int32.MaxValue
                match page.Events |> List.choose (fun e -> match e.Event with SessionEvent.RepoTrusted t -> Some t.By | _ -> None) with
                | [ by ] -> Expect.equal by anonymous "recorded as the peer they are, not promoted to a user"
                | other -> failwithf "expected one trust, got %A" other
            }

        // Both sides fold the same log: the Process to know what is held, a client to know which
        // card to show. A card somebody sees and a sandbox that is waiting have to be two
        // readings of one fact rather than two answers free to disagree.
        testCase "a trust ask is pending until somebody trusts the repo" <| fun () ->
            let r = repo "octo/hello"
            Expect.equal (RepoTrustWaits.pending [ trustAsked r both ]) [ r, both ] "somebody has to answer"

        testCase "trusting a repo settles its ask" <| fun () ->
            let r = repo "octo/hello"
            Expect.equal (RepoTrustWaits.pending [ trustAsked r both; trusted r ada ]) [] "settled"

        testCase "a repo that leaves the session stops waiting" <| fun () ->
            let r = repo "octo/hello"
            let removed =
                SessionEvent.RepoRemoved
                    { RepoRemoved.MessageId = MessageId.create "m3" |> expect
                      RepoRemoved.Repo = r
                      RepoRemoved.Actor = Principal.toActor ada }
            Expect.equal (RepoTrustWaits.pending [ trustAsked r both; removed ]) [] "nothing to answer"

        // Where the session's terminals start is the one thing a file asks of the session,
        // and it is asked the way a sandbox is: through the gate, as the file, on the
        // authority of whoever triggered the fold, pointing back at the add that caused it.
        testCaseAsync "a repo's session.shell-profile reaches the gate as that repo, caused by its add" <|
            async {
                let r = repo "octo/hello"
                let dir = checkout r (Some "version: 2\nsession:\n  shell-profile: src\n")
                let seen = ResizeArray<GatedCall> ()
                let folded =
                    createFold (cell (Some (reposOver dir [ r ]))) (cell WorkSandboxes.unavailable) (recordingGate seen) (foldLog ()) noCapabilities
                let ada = Principal.User (UserId.create "ada" |> expect)
                let added = MessageId.create "added" |> expect
                do! folded.Fold (FoldCause.Changed (r, Some added)) (CredentialFor.Person ada)
                match List.ofSeq seen with
                | [ call ] ->
                    Expect.equal call.Tool "set_shell_profile" "the verb a person would use"
                    Expect.equal call.Summary (sprintf "set_shell_profile default -> %s/octo/hello/src" dir) "the default sandbox, at the checkout's src"
                    Expect.equal (Authority.author call.Authority) (ActorRef.Configured r) "authored by the file"
                    Expect.equal (Authority.credential call.Authority) (CredentialFor.Person ada) "on the authority of whoever added it"
                    Expect.stringContains call.Args "added" "and caused by the add"
                | other -> failwithf "expected one call, got %A" (other |> List.map (fun c -> c.Summary))
            }

        testCaseAsync "two repos that both declare a shell profile get neither, and are each told why" <|
            async {
                let one, two = repo "octo/one", repo "octo/two"
                let dir = TestFiles.tempDir "yession-config-"
                for r in [ one; two ] do
                    TestFiles.ensureDir (sprintf "%s/%s" dir (RepoRef.relativePath r))
                    TestFiles.write (pathIn dir r) "version: 2\nsession:\n  shell-profile: .\n"
                let seen = ResizeArray<GatedCall> ()
                let log = foldLog ()
                let folded =
                    createFold (cell (Some (reposOver dir [ one; two ]))) (cell WorkSandboxes.unavailable) (recordingGate seen) log noCapabilities
                do! folded.Fold (FoldCause.Booted boot) CredentialFor.Deployment
                do! folded.Fold (FoldCause.Booted boot) CredentialFor.Deployment
                Expect.isEmpty (List.ofSeq seen) "no profile is set: neither file wins"
                let! page = log.Read None System.Int32.MaxValue
                let warned =
                    page.Events
                    |> List.choose (fun e ->
                        match e.Event with
                        | SessionEvent.RepoConfigWarned w when w.Where = "session.shell-profile" -> Some (RepoRef.value w.Repo)
                        | _ -> None)
                Expect.equal (List.sort warned) [ "octo/one"; "octo/two" ] "each is told, once, however often it folds"
            }

        testCaseAsync "a session with no repos service folds nothing rather than failing" <|
            async {
                let folded =
                    createFold (cell None) (cell WorkSandboxes.unavailable) (recordingGate (ResizeArray<GatedCall> ())) (foldLog ()) noCapabilities
                do! folded.Fold (FoldCause.Booted boot) CredentialFor.Deployment
                Expect.equal (folded.Outcomes ()) [] "no repos is not a fault"
            }

    ]
