module Yession.Tests.Seatbelt

// A policy as a Seatbelt profile, under Hedgehog.
//
// A profile is one deny and then allows, so what it holds is a set of rules and every
// question about it is decidable from that set: no filesystem, no `sandbox-exec`, no Mac.
// So this suite needs no capability and runs on both runtimes. What it cannot say is that
// the kernel reads the text the way `SeatbeltProfile.allows` reads the rules. That is a
// question for a box with a kernel to ask, and it belongs to the backend that spawns
// under these profiles.
//
// The generators draw from small pools of trees that never nest. Whether a path is beneath
// a grant is then decided by which tree it was built from, and a case that is ABOUT two
// paths that share a prefix writes the two paths down where the reader can see them.

open Fable.Pyxpecto
open Hedgehog
open Yession.Domain
open Yession.Domain.Sandboxes
open Yession.Host
open Yession.Tests.Support

let private expect = function Ok v -> v | Error e -> failwithf "%A" e

let private check (name: string) (body: unit -> Property<unit>) = testCase name <| fun () -> Property.check (body ())

/// Tree `i` of the pool a policy's paths are drawn from. No tree is inside another.
let private granted (i: int) : string = sprintf "/grant/t%d" i

/// A file beneath a tree.
let private fileIn (tree: string) : string = tree + "/src/main.fs"

let private socketAt (i: int) : string = sprintf "/run/s%d/daemon.sock" i

/// Some trees of the pool, with repeats, in no order.
let private genTrees : Gen<string list> =
    Gen.list (Range.linear 0 4) (Gen.int32 (Range.linear 0 7)) |> Gen.map (List.map granted)

/// Some trees of the pool, and never tree `i`.
let private genTreesWithout (i: int) : Gen<string list> =
    Gen.list (Range.linear 0 4) (Gen.int32 (Range.linear 0 6))
    |> Gen.map (List.map (fun j -> granted (if j >= i then j + 1 else j)))

let private genSockets : Gen<string list> =
    Gen.list (Range.linear 0 2) (Gen.int32 (Range.linear 0 3)) |> Gen.map (List.map socketAt)

let private genPolicy : Gen<SandboxPolicy> =
    gen {
        let! reads = genTrees
        let! writes = genTrees
        let! sockets = genSockets
        return { emptyPolicy with ReadPaths = reads; WritePaths = writes; Sockets = sockets }
    }

/// The proxy's port in every case that has one.
let private proxy = 3128

let private genHost : Gen<SeatbeltHost> =
    gen {
        let! proxied = Gen.bool
        return
            { RuntimePaths = [ "/usr"; "/nix/store" ]
              Tmp = "/session/tmp"
              ProxyPort = (if proxied then Some proxy else None) }
    }

/// A host with nothing a case has to think about: one runtime tree, no proxy.
let private plainHost : SeatbeltHost = { RuntimePaths = [ "/usr" ]; Tmp = "/session/tmp"; ProxyPort = None }

let private profileOf (host: SeatbeltHost) (policy: SandboxPolicy) : SeatbeltProfile =
    SeatbeltProfile.forPolicy host policy |> expect

let private rulesOf (host: SeatbeltHost) (policy: SandboxPolicy) : Set<SeatbeltRule> =
    profileOf host policy |> SeatbeltProfile.rules

/// What two policies name, as one policy.
let private together (left: SandboxPolicy) (right: SandboxPolicy) : SandboxPolicy =
    { emptyPolicy with
        ReadPaths = left.ReadPaths @ right.ReadPaths
        WritePaths = left.WritePaths @ right.WritePaths
        Sockets = left.Sockets @ right.Sockets }

/// One line of a profile as what stands OUTSIDE its strings, each string left as `""`, and
/// the strings themselves read back. A reader written here, not the module's own escaping
/// run backwards, so the two can disagree.
let private read (line: string) : string * string list =
    let text (chars: char list) = System.String (chars |> List.rev |> Array.ofList)
    let rec outside (i: int) (shape: char list) (strings: string list) =
        if i >= line.Length then text shape, List.rev strings
        elif line.[i] = '"' then inside (i + 1) shape [] strings
        else outside (i + 1) (line.[i] :: shape) strings
    and inside (i: int) (shape: char list) (current: char list) (strings: string list) =
        if i >= line.Length then failwithf "a string never closes in: %s" line
        elif line.[i] = '\\' && i + 1 < line.Length then inside (i + 2) shape (line.[i + 1] :: current) strings
        elif line.[i] = '"' then outside (i + 1) ('"' :: '"' :: shape) (text current :: strings)
        else inside (i + 1) shape (line.[i] :: current) strings
    outside 0 [] []

let private linesOf (profile: SeatbeltProfile) : string list =
    (SeatbeltProfile.render profile).Split '\n' |> List.ofArray

/// A path made of what a profile's reader could take for the end of a string or of a rule.
/// It opens with `/p` and closes with `x`, so it starts at the root and ends in no slash.
let private genAwkwardPath : Gen<string> =
    Gen.list (Range.linear 1 12) (Gen.item [ '"'; '\\'; '('; ')'; ' '; ';'; '#'; '/'; 'a' ])
    |> Gen.map (fun chars -> "/p" + System.String (Array.ofList chars) + "x")

let tests =
    testList "the Seatbelt profile" [

        // --- what a profile holds -------------------------------------------------------

        testCase "a policy that names nothing holds what its host gives every sandbox, and nothing more" <| fun () ->
            let tree (path: string) = SeatbeltPath.Tree path
            Expect.equal
                (rulesOf plainHost emptyPolicy)
                (Set.union
                    SeatbeltProfile.baseline
                    (Set.ofList
                        [ SeatbeltRule.Read (tree "/usr")
                          SeatbeltRule.Read (tree "/session/tmp")
                          SeatbeltRule.Write (tree "/session/tmp") ]))
                "the baseline, the runtime to read, the temporary directory to read and write"

        check "two policies together hold what each holds, and nothing more" <| fun () ->
            property {
                let! host = genHost
                let! left = genPolicy
                let! right = genPolicy
                Expect.equal
                    (rulesOf host (together left right))
                    (Set.union (rulesOf host left) (rulesOf host right))
                    "a profile is the union of its rules"
            }

        check "the order a policy names its paths in does not change the profile" <| fun () ->
            property {
                let! host = genHost
                let! policy = genPolicy
                let reversed =
                    { policy with
                        ReadPaths = List.rev policy.ReadPaths
                        WritePaths = List.rev policy.WritePaths
                        Sockets = List.rev policy.Sockets }
                Expect.equal
                    (SeatbeltProfile.render (profileOf host reversed))
                    (SeatbeltProfile.render (profileOf host policy))
                    "one set of grants is one text"
            }

        check "every rule beyond what the host gives every sandbox is something the policy names" <| fun () ->
            property {
                let! host = genHost
                let! policy = genPolicy
                // Total over the rules, so a new kind of rule does not compile until
                // somebody has said here what in a policy can put it in a profile.
                let named (rule: SeatbeltRule) =
                    match rule with
                    | SeatbeltRule.Read (SeatbeltPath.Tree path) ->
                        List.contains path policy.ReadPaths || List.contains path policy.WritePaths
                    | SeatbeltRule.Write (SeatbeltPath.Tree path) -> List.contains path policy.WritePaths
                    | SeatbeltRule.Socket path -> List.contains path policy.Sockets
                    | SeatbeltRule.UnixSockets -> not (List.isEmpty policy.Sockets)
                    | SeatbeltRule.Read (SeatbeltPath.Node _)
                    | SeatbeltRule.Write (SeatbeltPath.Node _)
                    | SeatbeltRule.DirectoryMetadata
                    | SeatbeltRule.LoopbackPort _ -> false
                let unnamed =
                    Set.difference (rulesOf host policy) (rulesOf host emptyPolicy) |> Set.filter (named >> not)
                Expect.equal unnamed Set.empty "a rule nobody named is a grant nobody was shown"
            }

        // --- what every profile holds ---------------------------------------------------

        testCase "the baseline opens no tree but the descriptors a process already holds" <| fun () ->
            let trees =
                SeatbeltProfile.baseline
                |> Set.toList
                |> List.choose (function
                    | SeatbeltRule.Read (SeatbeltPath.Tree path)
                    | SeatbeltRule.Write (SeatbeltPath.Tree path) -> Some path
                    | _ -> None)
                |> List.distinct
            Expect.equal trees [ "/dev/fd" ] "every other rule of the baseline is one node"

        testCase "the baseline writes nothing outside /dev" <| fun () ->
            let elsewhere =
                SeatbeltProfile.baseline
                |> Set.toList
                |> List.choose (function
                    | SeatbeltRule.Write (SeatbeltPath.Node path)
                    | SeatbeltRule.Write (SeatbeltPath.Tree path) when not (path.StartsWith "/dev/") -> Some path
                    | _ -> None)
            Expect.equal elsewhere [] "a sandbox writes only devices before its policy names a path"

        testCase "the essentials name no file to read or to write" <| fun () ->
            Expect.isFalse
                (SeatbeltProfile.essentials.Contains "file-read" || SeatbeltProfile.essentials.Contains "file-write")
                "a file a sandbox reads or writes is a rule, where `allows` can see it"

        // --- what a profile allows ------------------------------------------------------

        check "a file beneath a path granted to read is read" <| fun () ->
            property {
                let! host = genHost
                let! policy = genPolicy
                let! i = Gen.int32 (Range.linear 0 7)
                let profile = profileOf host { policy with ReadPaths = granted i :: policy.ReadPaths }
                Expect.isTrue (SeatbeltProfile.allows profile (SeatbeltAsk.Read (fileIn (granted i)))) "the grant holds"
            }

        check "a file beneath no grant is not read" <| fun () ->
            property {
                let! host = genHost
                let! policy = genPolicy
                let! i = Gen.int32 (Range.linear 0 7)
                Expect.isFalse
                    (SeatbeltProfile.allows (profileOf host policy) (SeatbeltAsk.Read (sprintf "/elsewhere/%d/id_ed25519" i)))
                    "nothing a profile holds reaches a tree nobody named"
            }

        check "a file beneath a path granted to read alone is not written" <| fun () ->
            property {
                let! host = genHost
                let! i = Gen.int32 (Range.linear 0 7)
                let! reads = genTrees
                let! writes = genTreesWithout i
                let profile = profileOf host { emptyPolicy with ReadPaths = granted i :: reads; WritePaths = writes }
                Expect.isFalse
                    (SeatbeltProfile.allows profile (SeatbeltAsk.Write (fileIn (granted i))))
                    "reading a path is not writing it"
            }

        check "a file beneath a path granted to write is read too" <| fun () ->
            property {
                let! host = genHost
                let! i = Gen.int32 (Range.linear 0 7)
                let! reads = genTreesWithout i
                let! writes = genTrees
                let profile = profileOf host { emptyPolicy with ReadPaths = reads; WritePaths = granted i :: writes }
                Expect.isTrue
                    (SeatbeltProfile.allows profile (SeatbeltAsk.Read (fileIn (granted i))))
                    "a workspace that can be written and not read is no workspace"
            }

        testCase "a path that only begins like a grant is not beneath it" <| fun () ->
            let profile = profileOf plainHost { emptyPolicy with ReadPaths = [ "/data/work" ] }
            Expect.isFalse
                (SeatbeltProfile.allows profile (SeatbeltAsk.Read "/data/work-2/notes"))
                "a tree ends at a slash"

        testCase "a node is read, and nothing beneath it is" <| fun () ->
            let profile = profileOf plainHost emptyPolicy
            Expect.equal
                (SeatbeltProfile.allows profile (SeatbeltAsk.Read "/"), SeatbeltProfile.allows profile (SeatbeltAsk.Read "/Users"))
                (true, false)
                "the entry names of /, and nothing those names lead to"

        check "a granted socket is reached" <| fun () ->
            property {
                let! host = genHost
                let! i = Gen.int32 (Range.linear 0 3)
                let profile = profileOf host { emptyPolicy with Sockets = [ socketAt i ] }
                Expect.isTrue (SeatbeltProfile.allows profile (SeatbeltAsk.Socket (socketAt i))) "the grant holds"
            }

        check "a socket nobody granted is not reached" <| fun () ->
            property {
                let! host = genHost
                let! i = Gen.int32 (Range.linear 0 3)
                let profile = profileOf host { emptyPolicy with Sockets = [ socketAt i ] }
                Expect.isFalse
                    (SeatbeltProfile.allows profile (SeatbeltAsk.Socket (socketAt (i + 1))))
                    "a socket is granted by its path, and one grant is one path"
            }

        check "a profile dials its proxy's port" <| fun () ->
            property {
                let! policy = genPolicy
                let profile = profileOf { plainHost with ProxyPort = Some proxy } policy
                Expect.isTrue (SeatbeltProfile.allows profile (SeatbeltAsk.Dial proxy)) "the one route out is open"
            }

        check "a profile dials no port but its proxy's" <| fun () ->
            property {
                let! policy = genPolicy
                let! other = Gen.int32 (Range.linear 1 (proxy - 1))
                let profile = profileOf { plainHost with ProxyPort = Some proxy } policy
                Expect.isFalse (SeatbeltProfile.allows profile (SeatbeltAsk.Dial other)) "the proxy is the ONE route out"
            }

        check "a sandbox given no proxy dials nothing" <| fun () ->
            property {
                let! policy = genPolicy
                let! port = Gen.int32 (Range.linear 1 65535)
                Expect.isFalse
                    (SeatbeltProfile.allows (profileOf plainHost policy) (SeatbeltAsk.Dial port))
                    "no policy can name a port"
            }

        testCase "an unconfined policy reads everywhere" <| fun () ->
            let profile = profileOf plainHost { emptyPolicy with Filesystem = Unconfined }
            Expect.isTrue
                (SeatbeltProfile.allows profile (SeatbeltAsk.Read "/elsewhere/notes"))
                "the one policy that asks for no confinement of files gets none"

        testCase "an unconfined policy writes everywhere" <| fun () ->
            let profile = profileOf plainHost { emptyPolicy with Filesystem = Unconfined }
            Expect.isTrue
                (SeatbeltProfile.allows profile (SeatbeltAsk.Write "/elsewhere/notes"))
                "the one policy that asks for no confinement of files gets none"

        // --- the text -------------------------------------------------------------------

        check "a profile denies once, at its head" <| fun () ->
            property {
                let! host = genHost
                let! policy = genPolicy
                let denies =
                    linesOf (profileOf host policy)
                    |> List.indexed
                    |> List.filter (fun (_, line) -> line.Contains "(deny")
                    |> List.map fst
                Expect.equal denies [ 1 ] "the second line, and no line after it"
            }

        check "a path is read back from the profile as the path it was" <| fun () ->
            property {
                let! path = genAwkwardPath
                let strings =
                    linesOf (profileOf plainHost { emptyPolicy with ReadPaths = [ path ] })
                    |> List.collect (read >> snd)
                Expect.isTrue (List.contains path strings) (sprintf "no string of the profile reads back as %s" path)
            }

        check "no path changes the shape of the line it is written in" <| fun () ->
            property {
                let! path = genAwkwardPath
                let shapes (named: string) =
                    linesOf (profileOf plainHost { emptyPolicy with ReadPaths = [ named ]; Sockets = [ named ] })
                    |> List.map (read >> fst)
                    |> List.sort
                Expect.equal (shapes path) (shapes "/p/plain") "whatever a path holds stays inside its quotes"
            }

        testCase "a path with a control character is refused, and the refusal shows the character" <| fun () ->
            match SeatbeltProfile.forPolicy plainHost { emptyPolicy with ReadPaths = [ "/data/a\nb" ] } with
            | Ok _ -> failwith "a path with a newline in it was written into a profile"
            | Error refusal -> Expect.isTrue (refusal.Contains "/data/a\\x0ab") (sprintf "said: %s" refusal)

        testCase "a path that does not start at the root is refused" <| fun () ->
            match SeatbeltProfile.forPolicy plainHost { emptyPolicy with WritePaths = [ "work/out" ] } with
            | Ok _ -> failwith "a relative path was written into a profile"
            | Error refusal -> Expect.isTrue (refusal.Contains "'work/out'") (sprintf "said: %s" refusal)

        testCase "a proxy port that is not a port is refused" <| fun () ->
            match SeatbeltProfile.forPolicy { plainHost with ProxyPort = Some 70000 } emptyPolicy with
            | Ok _ -> failwith "port 70000 was written into a profile"
            | Error refusal -> Expect.isTrue (refusal.Contains "70000") (sprintf "said: %s" refusal)

        testCase "a trailing slash names the same tree" <| fun () ->
            Expect.equal
                (rulesOf plainHost { emptyPolicy with ReadPaths = [ "/data/work/" ] })
                (rulesOf plainHost { emptyPolicy with ReadPaths = [ "/data/work" ] })
                "Seatbelt compares paths, and /data/work/ is not a path it would ever be asked about"
    ]

// The backend that spawns under these profiles, as far as it can be asked without a Mac:
// its name, what it tells the algebra about itself, how it reaches this host, and what a
// spawn under it is made of. Whether a command it starts is in fact confined is a question
// for a box with a Seatbelt (`SeatbeltIntegration`).

/// Every backend, proved total the way the resources suite proves its lists: a backend
/// added to the union fails this file to compile until the list carries it.
let private backends : SandboxBackend list =
    let all = [ HostBackend; SrtBackend; SeatbeltBackend; DockerBackend ]
    for backend in all do
        match backend with
        | HostBackend | SrtBackend | SeatbeltBackend | DockerBackend -> ()
    all

let private mac = Node.Base.Platform.Darwin

let private proxyAt (port: int) (token: string) : SeatbeltProxy =
    { SeatbeltProxy.Port = port; SeatbeltProxy.Token = token }

let private execOf (executable: string) (arguments: string list) (env: Map<string, string>) : SandboxExec =
    { Executable = executable
      Arguments = arguments
      Env = env
      WorkingDirectory = None
      Via = Entrypoint }

let backendTests =
    testList "the Seatbelt backend" [

        testCase "every backend is named by a word that parses back to it" <| fun () ->
            Expect.equal
                (backends |> List.map (SandboxBackend.describe >> SandboxBackend.parse))
                (backends |> List.map Ok)
                "an operator writes the name a refusal showed them"

        testCase "the agent sandbox refuses seatbelt" <| fun () ->
            Expect.isError (SandboxBackend.parseAgent "seatbelt") "the agent CLI has never run under this profile"

        testCase "the git sandbox refuses seatbelt" <| fun () ->
            Expect.isError (SandboxBackend.parseGit (Some "seatbelt")) "the git verbs have never run under this profile"

        testCase "a seatbelt sandbox holds a socket and a host exactly as they were granted" <| fun () ->
            let asked = Set.ofList [ Socket "/run/daemon.sock"; Endpoint "cache.example.com" ]
            Expect.equal
                (RealisedClosure.of' (Sandboxes.limitsFor SeatbeltBackend mac) asked |> RealisedClosure.differences)
                []
                "a profile names a socket's path, and the proxy names a host"

        testCase "a seatbelt sandbox dials this host by the name srt on a Mac dials" <| fun () ->
            Expect.equal
                (Sandboxes.hostAddressFrom "box.local" mac SeatbeltBackend)
                (Sandboxes.hostAddressFrom "box.local" mac SrtBackend)
                "one proxy, so one way to this host"

        testCase "a seatbelt sandbox is allowed the route to this host that srt on a Mac is" <| fun () ->
            let door : Sandboxes.HostDoor = { Host = "box.local"; Port = 4100 }
            Expect.equal
                (Sandboxes.hostRouteFrom [ "100.64.0.7" ] mac SeatbeltBackend door)
                (Sandboxes.hostRouteFrom [ "100.64.0.7" ] mac SrtBackend door)
                "the proxy re-judges a name by what it resolves to, whoever wrote the profile"

        testCase "the seatbelt backend refuses a host that is not a Mac" <| fun () ->
            Expect.isError
                (Sandboxes.SeatbeltSandbox.hostedOn Node.Base.Platform.Linux)
                "no other platform has a Seatbelt to confine with"

        testCase "the seatbelt backend is hosted on a Mac" <| fun () ->
            Expect.equal (Sandboxes.SeatbeltSandbox.hostedOn mac) (Ok ()) "Seatbelt ships with the OS"

        testCase "a client is sent to the proxy by address, with its token" <| fun () ->
            Expect.equal
                (SeatbeltProxy.environment (proxyAt 3128 "abc123") |> Map.tryFind "HTTPS_PROXY")
                (Some "http://yession:abc123@127.0.0.1:3128")
                "an address needs no resolver, and a confined process has none"

        testCase "a token is written so that nothing in it changes the proxy's address" <| fun () ->
            Expect.equal
                (SeatbeltProxy.environment (proxyAt 3128 "a@b:c/d") |> Map.tryFind "HTTPS_PROXY")
                (Some "http://yession:a%40b%3Ac%2Fd@127.0.0.1:3128")
                "the token stays the password, whatever it holds"

        testCase "nothing a command sets points a client past the proxy" <| fun () ->
            let exec = execOf "curl" [ "https://example.com" ] (Map.ofList [ "HTTPS_PROXY", "http://elsewhere:1" ])
            Expect.equal
                (Sandboxes.SeatbeltSandbox.environmentFor (proxyAt 3128 "abc123") emptyPolicy exec |> Map.tryFind "HTTPS_PROXY")
                (Some "http://yession:abc123@127.0.0.1:3128")
                "the proxy's variables are laid over the command's own"

        testCase "a spawn is the command as it was given, behind sandbox-exec and the profile" <| fun () ->
            Expect.equal
                (Sandboxes.SeatbeltSandbox.argvFor "(version 1)" HostSpec.none (execOf "git" [ "log"; "--format=%s two words" ] Map.empty))
                ("/usr/bin/sandbox-exec", [ "-p"; "(version 1)"; "git"; "log"; "--format=%s two words" ])
                "no shell stands between an argv and its program"

        testCase "work under seatbelt stands behind its sandbox's entrypoint, inside the profile" <| fun () ->
            let host = { HostSpec.Entrypoint = Some [ "devenv"; "shell"; "--" ] }
            Expect.equal
                (Sandboxes.SeatbeltSandbox.argvFor "(version 1)" host (execOf "dotnet" [ "build" ] Map.empty))
                ("/usr/bin/sandbox-exec", [ "-p"; "(version 1)"; "devenv"; "shell"; "--"; "dotnet"; "build" ])
                "the entrypoint is confined as the work it starts is"

        // One rule for the three backends that run on the host's own userland.
        testCase "work on the host stands behind the entrypoint its sandbox declares" <| fun () ->
            let host = { HostSpec.Entrypoint = Some [ "nix"; "develop"; "--command" ] }
            Expect.equal
                (Sandboxes.entered host (execOf "make" [ "test" ] Map.empty))
                ("nix", [ "develop"; "--command"; "make"; "test" ])
                "the first word is what is started, and the command follows the rest"

        testCase "housekeeping on the host runs bare, whatever entrypoint its sandbox declares" <| fun () ->
            let host = { HostSpec.Entrypoint = Some [ "nix"; "develop"; "--command" ] }
            Expect.equal
                (Sandboxes.entered host { execOf "git" [ "status" ] Map.empty with Via = Direct })
                ("git", [ "status" ])
                "a start-up check needs no toolchain and does not pay for one"

        testCase "work on a host sandbox that declares no entrypoint runs as it stands" <| fun () ->
            Expect.equal
                (Sandboxes.entered HostSpec.none (execOf "make" [ "test" ] Map.empty))
                ("make", [ "test" ])
                "nothing is put in front of it"

        testCase "a host that is ready says where its proxy listens" <| fun () ->
            let sent = Sandboxes.SrtSandbox.HostWire.Ready (Some (proxyAt 52950 "abc123"))
            Expect.equal
                (Sandboxes.SrtSandbox.HostWire.parseReply (Sandboxes.SrtSandbox.HostWire.reply sent))
                (Some sent)
                "the port and the token cross the wire as they were"

        testCase "a host whose manager started no proxy says so" <| fun () ->
            let sent = Sandboxes.SrtSandbox.HostWire.Ready None
            Expect.equal
                (Sandboxes.SrtSandbox.HostWire.parseReply (Sandboxes.SrtSandbox.HostWire.reply sent))
                (Some sent)
                "no proxy is an answer, and not a line the session cannot read"
    ]

