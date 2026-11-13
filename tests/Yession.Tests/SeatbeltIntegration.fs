module Yession.Tests.SeatbeltIntegration

// The seatbelt backend, driven for real on a Mac.
//
// Two questions, kept apart. The first is whether the KERNEL reads a profile the way
// `SeatbeltProfile.allows` reads its rules. The cheap tier trusts that function for every
// claim it makes about a profile, and only a box with a Seatbelt can say the trust is earned.
// The second is whether the backend puts a command behind such a profile: a spawn that runs,
// and a read, a write and a connection that are refused.
//
// Under `Tag.needs [Srt]`, which is confinement under test. A Mac's cases only: on any other
// platform the suite is one pending case that says why.

open Fable.Pyxpecto
open Fable.NodeExtras
open Yession.Domain
open Yession.Domain.Sandboxes
open Yession.Host
open Yession.Tests.Support

let private expect = function Ok v -> v | Error e -> failwithf "%A" e

let private onMac () : bool = Node.Api.``process``.platform = Node.Base.Platform.Darwin

/// How this box confines, as the run's environment configures it: the parse a Session does
/// at boot. The seatbelt backend reads the same record srt does.
let private tools () =
    Sandboxes.SrtSandbox.toolsFrom (Sandboxes.ambientEnv ()) |> expect

/// A fresh directory, by the path the kernel checks. The OS temp directory on a Mac is
/// reached through the symlink `/var`, and a profile names a file's own path.
let private freshDir () : string = TestFiles.canonical (TestFiles.tempDir "yession-seatbelt-")

/// A policy whose only granted place is `workspace`, and which names no host.
let private policyIn (workspace: string) : SandboxPolicy =
    { emptyPolicy with
        ReadPaths = [ workspace ]
        WritePaths = [ workspace ]
        AllowedDomains = Some []
        Env = Sandboxes.hostBaseline (Sandboxes.ambientEnv ())
        WorkingDirectory = Some workspace }

/// The directory that stands for Yession's own in these cases: the OS temp directory, by
/// the path the kernel checks. Each case's tree is beneath it. A user's temp directory on
/// a Mac is closed to other accounts, and with it as Yession's own no case is refused for
/// that.
let private ownRoot () : string = TestFiles.canonical (Node.Api.os.tmpdir ())

let private startSandboxBehind (host: HostSpec) (policy: SandboxPolicy) : Async<Sandbox> =
    async {
        match! Sandboxes.SeatbeltSandbox.create (tools ()) host (Some (ownRoot ())) policy with
        | Error reason -> return failwithf "seatbelt sandbox failed: %s" reason
        | Ok sandbox -> return sandbox
    }

let private startSandbox (policy: SandboxPolicy) : Async<Sandbox> = startSandboxBehind HostSpec.none policy

/// An entrypoint a case can see from inside: it puts one variable in front of the work.
let private marking : HostSpec = { HostSpec.Entrypoint = Some [ "/usr/bin/env"; "BEHIND=entrypoint" ] }

/// What `sh -c script` printed when the session ran it as housekeeping, bare.
let private housekeeping (sandbox: Sandbox) (script: string) : Async<string> =
    async {
        let out = System.Text.StringBuilder ()
        let! spawned =
            sandbox.Spawn
                { Executable = "/bin/sh"
                  Arguments = [ "-c"; script ]
                  Env = Map.empty
                  WorkingDirectory = None
                  Via = Direct }
                (fun (stream, text) -> if stream = Stdout then out.Append text |> ignore)
        match spawned with
        | Error reason -> return failwithf "the sandboxed process did not run: %s" reason
        | Ok handle ->
            let! _ = handle.Exited
            return out.ToString ()
    }

/// `sh -c` inside the sandbox, answering what it printed and how it ended.
let private shell (sandbox: Sandbox) (script: string) : Async<SandboxRun * string * string> =
    runInSandbox sandbox "/bin/sh" [ "-c"; script ] Map.empty None

let private exitCode (run: SandboxRun) : int =
    match run with
    | SandboxExited code -> code
    | SandboxSignalled signal -> failwithf "the sandboxed process was ended by %s" signal
    | SandboxRunFailed reason -> failwithf "the sandboxed process did not run: %s" reason
    | SandboxKilledForMemory kill -> failwithf "the sandboxed process was %s" (MemoryKill.described kill)

/// One word quoted for `/bin/sh`.
let private quoted (value: string) : string = "'" + value.Replace ("'", "'\\''") + "'"

/// What a fixture's listener does with a connection: say one thing and let it go. Its
/// clients here are probes that hang up as soon as they know they connected, so it listens
/// for the error such a client leaves behind before it answers.
let private answering (connection: NetConnection) : unit =
    connection.onError ignore
    connection.close "ok"

/// A listener on the loopback that answers, on a port the OS chose, and a thunk that closes
/// it. Something has to answer: a refused connect and a denied one are both failures, and
/// only a connect that succeeds says a rule reached the kernel.
let private listenOnLoopback () : Async<int * (unit -> unit)> =
    Async.FromContinuations (fun (cont, _, _) ->
        let server = createNetServerAnswering answering
        server.listen (0, "127.0.0.1", fun () -> cont (boundPort server, fun () -> try server.close () with _ -> ())))

/// A unix socket with something listening on it, and a thunk that closes it.
let private listenOn (path: string) : unit -> unit =
    let server = createNetServerAnswering answering
    server.listen path
    fun () -> try server.close () with _ -> ()

/// Whether `probe`, a line of shell, succeeds under `profile`. Run by `sandbox-exec` itself,
/// from the unconfining backend, and not through the backend under test: the question is
/// about the profile's text and the kernel, and nothing between them.
let private kernelAllows (host: Sandbox) (profile: SeatbeltProfile) (probe: string) : Async<bool> =
    async {
        let! run, _, _ =
            runInSandbox
                host
                "/usr/bin/sandbox-exec"
                [ "-p"; SeatbeltProfile.render profile; "/bin/sh"; "-c"; probe + " >/dev/null 2>&1" ]
                Map.empty
                None
        return exitCode run = 0
    }

/// One thing a process might try: what it is called when the two answers differ, the asks
/// the model has to allow for it to succeed, and the shell that tries it.
let private trying (what: string) (questions: SeatbeltAsk list) (probe: string) : string * SeatbeltAsk list * string =
    what, questions, probe

let private macCases () =
    [
        // --- the kernel against the model ---------------------------------------------------

        testCaseAsync "the kernel answers every ask as SeatbeltProfile.allows answers it" (async {
            let root = freshDir ()
            let at (name: string) = root + "/" + name
            let awkward = "we\"ird\\dir (x)"
            for directory in [ "ro"; "rw"; "un"; "tmp"; "work"; "work-2"; awkward; "s"; "up" ] do
                TestFiles.makeDir (at directory)
            for directory in [ "ro"; "rw"; "un"; "work"; "work-2"; awkward; "up" ] do
                TestFiles.write (at directory + "/f") "x"
            let granted, ungranted = at "s/g.sock", at "s/o.sock"
            let closeGranted, closeUngranted = listenOn granted, listenOn ungranted
            let! proxyPort, closeProxy = listenOnLoopback ()
            let! otherPort, closeOther = listenOnLoopback ()
            // As `Sandboxes.policyFor` fills it: a granted socket is read and written too.
            let policy =
                { emptyPolicy with
                    ReadPaths = [ at "ro"; at "work"; at awkward; granted ]
                    WritePaths = [ at "rw"; granted ]
                    Sockets = [ granted ] }
            let profile =
                SeatbeltProfile.forPolicy
                    { RuntimePaths = (tools ()).Runtime; Tmp = at "tmp"; ProxyPort = Some proxyPort; Above = [ at "up" ] }
                    policy
                |> expect
            let asks =
                [ trying "read a file under a read grant" [ SeatbeltAsk.Read (at "ro/f") ] ("cat " + quoted (at "ro/f"))
                  trying "list a read grant" [ SeatbeltAsk.Read (at "ro") ] ("ls " + quoted (at "ro"))
                  trying "read a file under no grant" [ SeatbeltAsk.Read (at "un/f") ] ("cat " + quoted (at "un/f"))
                  trying "list a directory under no grant" [ SeatbeltAsk.Read (at "un") ] ("ls " + quoted (at "un"))
                  trying "read a file under a write grant" [ SeatbeltAsk.Read (at "rw/f") ] ("cat " + quoted (at "rw/f"))
                  trying
                      "read beside a grant that shares its prefix"
                      [ SeatbeltAsk.Read (at "work-2/f") ]
                      ("cat " + quoted (at "work-2/f"))
                  trying
                      "read under a path with a quote and a backslash"
                      [ SeatbeltAsk.Read (at awkward + "/f") ]
                      ("cat " + quoted (at awkward + "/f"))
                  trying "list the directory above the grants" [ SeatbeltAsk.Read root ] ("ls " + quoted root)
                  trying "list a directory above the sandbox" [ SeatbeltAsk.Read (at "up") ] ("ls " + quoted (at "up"))
                  trying
                      "read a file in a directory above the sandbox"
                      [ SeatbeltAsk.Read (at "up/f") ]
                      ("cat " + quoted (at "up/f"))
                  trying "list /" [ SeatbeltAsk.Read "/" ] "ls /"
                  trying "list /Users" [ SeatbeltAsk.Read "/Users" ] "ls /Users"
                  trying "read the link /tmp" [ SeatbeltAsk.Read "/tmp" ] "readlink /tmp"
                  trying "list /private/tmp" [ SeatbeltAsk.Read "/private/tmp" ] "ls /private/tmp"
                  trying "write under a write grant" [ SeatbeltAsk.Write (at "rw/new") ] ("touch " + quoted (at "rw/new"))
                  trying "write under a read grant" [ SeatbeltAsk.Write (at "ro/new") ] ("touch " + quoted (at "ro/new"))
                  trying "write under no grant" [ SeatbeltAsk.Write (at "un/new") ] ("touch " + quoted (at "un/new"))
                  trying "write the temporary directory" [ SeatbeltAsk.Write (at "tmp/new") ] ("touch " + quoted (at "tmp/new"))
                  trying "write /dev/null" [ SeatbeltAsk.Write "/dev/null" ] ": > /dev/null"
                  trying "read /dev/urandom" [ SeatbeltAsk.Read "/dev/urandom" ] "head -c 1 /dev/urandom"
                  trying
                      "write /dev/stdout by its path"
                      [ SeatbeltAsk.Write "/dev/stdout"; SeatbeltAsk.Write "/dev/fd/1" ]
                      "echo x > /dev/stdout"
                  trying "read a descriptor by its path" [ SeatbeltAsk.Read "/dev/fd/3" ] "exec 3< /dev/null; cat /dev/fd/3"
                  trying
                      "connect the granted socket"
                      [ SeatbeltAsk.Socket granted ]
                      ("/usr/bin/nc -U " + quoted granted + " < /dev/null")
                  trying
                      "connect a socket nobody granted"
                      [ SeatbeltAsk.Socket ungranted ]
                      ("/usr/bin/nc -U " + quoted ungranted + " < /dev/null")
                  trying "dial the proxy's port" [ SeatbeltAsk.Dial proxyPort ] (sprintf "/usr/bin/nc -z 127.0.0.1 %d" proxyPort)
                  trying
                      "dial another port on the loopback"
                      [ SeatbeltAsk.Dial otherPort ]
                      (sprintf "/usr/bin/nc -z 127.0.0.1 %d" otherPort) ]
            let! host =
                Sandboxes.HostSandbox.create HostSpec.none
                    { emptyPolicy with Env = Map.ofList [ "PATH", "/usr/bin:/bin"; "TMPDIR", at "tmp" ] }
            let host = host |> expect
            let disagreements = ResizeArray<string> ()
            for what, questions, probe in asks do
                let model = questions |> List.forall (SeatbeltProfile.allows profile)
                let! kernel = kernelAllows host profile probe
                if kernel <> model then
                    disagreements.Add (sprintf "%s: the model says %b, the kernel says %b" what model kernel)
            do! host.Dispose ()
            closeGranted ()
            closeUngranted ()
            closeProxy ()
            closeOther ()
            Expect.equal (List.ofSeq disagreements) [] "what `allows` says of a profile is what a confined process finds"
        })

        // --- the backend ---------------------------------------------------------------------

        testCaseAsync "a command runs behind the profile, and what it prints comes back" (async {
            let! sandbox = startSandbox (policyIn (freshDir ()))
            let! _, out, _ = shell sandbox "echo confined"
            do! sandbox.Dispose ()
            Expect.equal (out.Trim ()) "confined" "its stdout reached the caller"
        })

        testCaseAsync "a command writes the workspace it was given" (async {
            let workspace = freshDir ()
            let! sandbox = startSandbox (policyIn workspace)
            let! _ = shell sandbox "echo kept > marker"
            do! sandbox.Dispose ()
            Expect.isTrue (TestFiles.exists (workspace + "/marker")) "the write landed on the host"
        })

        testCaseAsync "a write outside the policy's paths is refused" (async {
            let outside = freshDir ()
            let! sandbox = startSandbox (policyIn (freshDir ()))
            let! _ = shell sandbox (sprintf "echo escaped > %s" (quoted (outside + "/escaped")))
            do! sandbox.Dispose ()
            Expect.isFalse (TestFiles.exists (outside + "/escaped")) "nothing was written where nothing was granted"
        })

        testCaseAsync "a read outside the policy's paths is refused" (async {
            let outside = freshDir ()
            TestFiles.write (outside + "/secret") "not-yours"
            let! sandbox = startSandbox (policyIn (freshDir ()))
            let! _, out, _ = shell sandbox (sprintf "cat %s" (quoted (outside + "/secret")))
            do! sandbox.Dispose ()
            Expect.isFalse (out.Contains "not-yours") "what nobody granted never reaches stdout"
        })

        testCaseAsync "the host runtime stays readable, or no command could run at all" (async {
            let! sandbox = startSandbox (policyIn (freshDir ()))
            // `--version` runs no program, so the interpreter is the only thing it reads.
            let! _, out, _ = runInSandbox sandbox Node.Api.``process``.execPath [ "--version" ] Map.empty None
            do! sandbox.Dispose ()
            Expect.isTrue (out.StartsWith "v") "the interpreter this very process runs on answers from inside"
        })

        testCaseAsync "work runs behind the entrypoint that its sandbox declares" (async {
            let! sandbox = startSandboxBehind marking (policyIn (freshDir ()))
            let! _, out, _ = shell sandbox "echo \"$BEHIND\""
            do! sandbox.Dispose ()
            Expect.equal (out.Trim ()) "entrypoint" "the entrypoint started the work"
        })

        testCaseAsync "housekeeping runs bare, whatever entrypoint its sandbox declares" (async {
            let! sandbox = startSandboxBehind marking (policyIn (freshDir ()))
            let! out = housekeeping sandbox "echo \"bare:$BEHIND\""
            do! sandbox.Dispose ()
            Expect.equal (out.Trim ()) "bare:" "nothing stood in front of it"
        })

        // The OS temp directory on a Mac is reached through the symlink /var, and a profile
        // names a file's own path. A policy written with the spelling a person would use has
        // to hold all the same.
        testCaseAsync "a path that is granted through a symlink is held by the path it leads to" (async {
            let spelled = TestFiles.tempDir "yession-seatbelt-"
            let! sandbox = startSandbox (policyIn spelled)
            let! _ = shell sandbox "echo kept > marker"
            do! sandbox.Dispose ()
            Expect.isTrue (TestFiles.exists (spelled + "/marker")) "the write landed, by either spelling"
        })

        testCaseAsync "a connection that goes round the proxy is refused" (async {
            let! port, close = listenOnLoopback ()
            let! sandbox = startSandbox (policyIn (freshDir ()))
            let! run, _, _ = shell sandbox (sprintf "/usr/bin/nc -z 127.0.0.1 %d" port)
            do! sandbox.Dispose ()
            close ()
            Expect.notEqual (exitCode run) 0 "a listener on this host is not reached by dialling it"
        })

        // The directories above a sandbox's tree. The tree here is three levels beneath a
        // fresh directory, and the fresh directory stands for the Manager's data directory.
        testCaseAsync "a sandbox lists each directory above its tree, and reads nothing in one" (async {
            let data = freshDir ()
            let workspace = data + "/sessions/s1/workspace"
            TestFiles.ensureDir workspace
            TestFiles.ensureDir (data + "/sessions/s2")
            TestFiles.write (data + "/secrets.json") "not-yours"
            let! sandbox = startSandbox (policyIn workspace)
            let! _, names, _ = shell sandbox (sprintf "ls %s" (quoted (data + "/sessions")))
            let! _, secret, _ = shell sandbox (sprintf "cat %s" (quoted (data + "/secrets.json")))
            let! other, _, _ = shell sandbox (sprintf "ls %s" (quoted (data + "/sessions/s2")))
            do! sandbox.Dispose ()
            Expect.isTrue (names.Contains "s1" && names.Contains "s2") (sprintf "the names in a directory above the tree, said: %s" names)
            Expect.isFalse (secret.Contains "not-yours") "a file beside the tree stays unread"
            Expect.notEqual (exitCode other) 0 "and so does the directory of another session"
        })

        // What nix does with the home of a sandbox: it opens each directory above it. The
        // home here is beside the workspace, so its parents are not the workspace's.
        testCaseAsync "a sandbox lists the directories above a path that its policy names beside its workspace" (async {
            let data = freshDir ()
            let workspace = data + "/sessions/s1/workspace"
            let home = data + "/sessions/s1/sandboxes/mac/home"
            TestFiles.ensureDir workspace
            TestFiles.ensureDir home
            TestFiles.ensureDir (data + "/sessions/s1/sandboxes/other")
            let policy = policyIn workspace
            let! sandbox = startSandbox { policy with WritePaths = home :: policy.WritePaths }
            let! _, names, _ = shell sandbox (sprintf "ls %s" (quoted (data + "/sessions/s1/sandboxes")))
            let! other, _, _ = shell sandbox (sprintf "ls %s" (quoted (data + "/sessions/s1/sandboxes/other")))
            do! sandbox.Dispose ()
            Expect.isTrue (names.Contains "mac" && names.Contains "other") (sprintf "the names above the home, said: %s" names)
            Expect.notEqual (exitCode other) 0 "and nothing in the directory of another sandbox"
        })

        // The fresh directory is in the user's temp directory, which other accounts cannot
        // list. With only the workspace as Yession's own, that directory is the host's.
        testCaseAsync "a sandbox does not start beneath a directory that some accounts cannot list" (async {
            let workspace = freshDir () + "/sessions/s1/workspace"
            TestFiles.ensureDir workspace
            let! refused = Sandboxes.SeatbeltSandbox.create (tools ()) HostSpec.none (Some workspace) (policyIn workspace)
            match refused with
            | Ok sandbox ->
                do! sandbox.Dispose ()
                failwith "the sandbox started, and would list a closed directory"
            | Error refusal -> Expect.isTrue (refusal.Contains "--data-dir") (sprintf "said: %s" refusal)
        })

        // The two cases above, each with one rule that an operator wrote. The kernel
        // reads the rule. `SeatbeltProfile.allows` does not, and the cheap tier says so.
        testCaseAsync "a rule that an operator wrote opens what it names, and the kernel agrees" (async {
            let outside = freshDir ()
            TestFiles.write (outside + "/shared") "now-yours"
            let rule = SeatbeltAllow.parse (sprintf "(allow file-read* (subpath \"%s\"))" outside) |> expect
            let! sandbox = startSandbox { policyIn (freshDir ()) with SeatbeltRules = [ rule ] }
            let! _, out, _ = shell sandbox (sprintf "cat %s" (quoted (outside + "/shared")))
            do! sandbox.Dispose ()
            Expect.isTrue (out.Contains "now-yours") "the rule is in the profile that confined the command"
        })

        testCaseAsync "a rule for the loopback lets a command dial a listener on this host" (async {
            let! port, close = listenOnLoopback ()
            let rule = SeatbeltAllow.parse """(allow network-outbound (remote ip "localhost:*"))""" |> expect
            let! sandbox = startSandbox { policyIn (freshDir ()) with SeatbeltRules = [ rule ] }
            let! run, _, _ = shell sandbox (sprintf "/usr/bin/nc -z 127.0.0.1 %d" port)
            do! sandbox.Dispose ()
            close ()
            Expect.equal (exitCode run) 0 "what the profile refused is what this rule allows"
        })

        // `SeatbeltAllow.parse` checks the shape of a rule and not its words. A word that
        // Seatbelt does not know stops every command, and `sandbox-exec` says which word.
        // The start-up check of a sandbox runs a command, so it refuses such a sandbox.
        testCaseAsync "a rule that Seatbelt cannot read stops the command, and sandbox-exec names the word" (async {
            let rule = SeatbeltAllow.parse "(allow no-such-operation)" |> expect
            let! sandbox = startSandbox { policyIn (freshDir ()) with SeatbeltRules = [ rule ] }
            let! run, out, err = shell sandbox "echo ran"
            do! sandbox.Dispose ()
            Expect.notEqual (exitCode run) 0 "no command runs under a profile that does not compile"
            Expect.isFalse (out.Contains "ran") "and this one did not"
            Expect.isTrue (err.Contains "no-such-operation") (sprintf "said: %s" err)
        })

        testCaseAsync "a client is handed the proxy, and the proxy refuses a host nobody named" (async {
            let! sandbox = startSandbox (policyIn (freshDir ()))
            // curl reads the proxy from the environment the backend gave it. The proxy answers
            // a host that is not on its allowlist itself, so nothing here reaches the network.
            let! _, out, _ = shell sandbox "/usr/bin/curl -s -o /dev/null -w '%{http_connect}' --max-time 20 https://example.com"
            do! sandbox.Dispose ()
            Expect.equal (out.Trim ()) "403" "the proxy answered the CONNECT, and its answer was no"
        })
    ]

let tests =
    Tag.needs "Seatbelt integration" [ Tag.Srt ] (fun () ->
        testList "Seatbelt integration" (
            if onMac () then macCases ()
            else [ ptestCase "the seatbelt backend confines (macOS only: no other platform has a Seatbelt)" (fun () -> ()) ]
        ))
