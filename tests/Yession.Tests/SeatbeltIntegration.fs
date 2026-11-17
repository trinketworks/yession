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
let private marking : HostSpec = { HostSpec.Prepare = None; HostSpec.Entrypoint = Some [ "/usr/bin/env"; "BEHIND=entrypoint" ] }

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

/// A process that has `YESSION_MARKER=<value>` in its environment and `value` in its
/// arguments, started in `sandbox`. Answers its pid, and a way to end it. The process prints
/// its pid when it runs, so the pid is that of a process that has its environment.
let private marked (sandbox: Sandbox) (value: string) : Async<int * (unit -> unit)> =
    async {
        let printed = System.Text.StringBuilder ()
        let! spawned =
            sandbox.Spawn
                { Executable = Node.Api.``process``.execPath
                  Arguments = [ "-e"; sprintf "console.log(process.pid); setTimeout(() => {}, 600000) /* %s */" value ]
                  Env = Map.ofList [ "YESSION_MARKER", value ]
                  WorkingDirectory = None
                  Via = Direct }
                (fun (stream, text) -> if stream = Stdout then printed.Append text |> ignore)
        match spawned with
        | Error reason -> return failwithf "the marked process did not start: %s" reason
        | Ok handle ->
            let! started = settledWithin 20000 (fun () -> printed.ToString().Contains "\n")
            if not started then
                handle.Kill ()
                failwith "the marked process did not print its pid"
            return int (printed.ToString().Trim ()), handle.Kill
    }

/// `body` with the pid of a marked process in `sandbox` (`marked`). The process ends when
/// `body` ends, and when `body` throws: a marked process lives for ten minutes, and a case
/// that fails after it started one must not leave it.
let private withMarked (sandbox: Sandbox) (value: string) (body: int -> Async<'T>) : Async<'T> =
    async {
        let! pid, stop = marked sandbox value
        try
            return! body pid
        finally
            stop ()
    }

/// Perl that asks the kernel for the arguments and the environment of one process, and
/// answers the value of `YESSION_MARKER` there. The call is the sysctl `kern.procargs2`,
/// which is the call that `ps -E` makes. A refusal answers `refused` and the error number.
///
/// Perl, because each Mac has it and it can make the call. `ps` is setuid, and a sandbox
/// does not run a setuid program.
let private markerOf : string =
    """sub marker { my $mib = pack('i3', 1, 49, $_[0]); my $size = pack('Q', 1 << 20); my $buf = "\0" x (1 << 20);
  return 'refused ' . ($! + 0) if syscall(202, $mib, 3, $buf, $size, 0, 0) != 0;
  return substr($buf, 0, unpack('Q', $size)) =~ /YESSION_MARKER=([\w-]+)/ ? "marker $1" : 'no marker'; }
"""

/// What a command in `sandbox` reads of the environment of process `pid`, and what the
/// command said on stderr.
let private readsOf (sandbox: Sandbox) (pid: int) : Async<string * string> =
    async {
        let! _, out, err =
            runInSandbox sandbox "/usr/bin/perl" [ "-e"; markerOf + "print marker($ARGV[0]);"; string pid ] Map.empty None
        return out.Trim (), err
    }

/// The backend that confines nothing, for a process that a case needs outside each sandbox.
let private unconfined () : Async<Sandbox> =
    async {
        let! host = Sandboxes.HostSandbox.create HostSpec.none emptyPolicy
        return host |> expect
    }

/// `body` with the pid of a marked process outside each sandbox (`withMarked`).
let private withOutside (value: string) (body: int -> Async<'T>) : Async<'T> =
    async {
        let! host = unconfined ()
        let! answer = withMarked host value body
        do! host.Dispose ()
        return answer
    }

/// What a command in a new sandbox under `policy` reads of the environment of process `pid`,
/// and what the command said on stderr.
let private readsUnder (policy: SandboxPolicy) (pid: int) : Async<string * string> =
    async {
        let! sandbox = startSandbox policy
        let! answer = readsOf sandbox pid
        do! sandbox.Dispose ()
        return answer
    }

/// The name that one line of the head guards, `(when (defined? 'name) ...)`. A line with no
/// guard has none.
let private guarded (line: string) : string option =
    let opening = "(when (defined? '"
    if line.StartsWith opening then
        let rest = line.Substring opening.Length
        match rest.IndexOf ')' with
        | -1 -> None
        | closing -> Some (rest.Substring (0, closing))
    else
        None

/// The version of this macOS as two numbers: (26, 5) for 26.5.2. Nothing when `sw_vers` does
/// not say one.
let private macOSVersion () : (int * int) option =
    let number (text: string) : int option =
        match System.Int32.TryParse text with
        | true, value -> Some value
        | _ -> None
    match (SyncChildProcesses.spawnSync "/usr/bin/sw_vers" [ "-productVersion" ] SyncOptions.none).stdout with
    | Some said ->
        match said.Trim().Split '.' |> List.ofArray |> List.map number with
        | Some major :: Some minor :: _ -> Some (major, minor)
        | _ -> None
    | None -> None

/// Whether this macOS is 26.5 or later. We measured 26.5.2, and it knows each name that the
/// head of a profile guards.
let private atLeastTheMeasuredMacOS () : bool =
    match macOSVersion () with
    | Some version -> version >= (26, 5)
    | None -> false

/// Whether the loader prints its lines for this Node outside each sandbox, when
/// `DYLD_PRINT_LIBRARIES` asks. The loader ignores that variable for a program whose
/// signature asks for the hardened runtime, under each profile and under none. For such a
/// Node, no case can judge what a profile does with the variable.
let private loaderAnswersOutside () : bool =
    let ran =
        SyncChildProcesses.spawnSync
            Node.Api.``process``.execPath
            [ "-e"; "0" ]
            { SyncOptions.none with Env = ChildEnv.Adding (Map.ofList [ "DYLD_PRINT_LIBRARIES", "1" ]) }
    ran.stderr |> Option.exists (fun said -> said.Contains "dyld[")

/// What a confined command reads of the environment of the process that started its
/// sandbox, and what the command said on stderr.
///
/// The launcher is a Node process with `YESSION_MARKER` in its environment. It starts
/// `sandbox-exec` itself, under the profile of a policy, as the backend does. The confined
/// command is in the process group of the launcher, or in a session of its own
/// (`ownSession`), which is where the backend puts a command.
let private readsItsLauncher (ownSession: bool) : Async<string * string> =
    async {
        let workspace = freshDir ()
        let profile =
            SeatbeltProfile.forPolicy
                { RuntimePaths = (tools ()).Runtime; Tmp = workspace; ProxyPort = None; Above = [] }
                (policyIn workspace)
            |> expect
        let launcher =
            """const [profile, program, session] = process.argv.slice(1);
const child = require('child_process').spawn(
  '/usr/bin/sandbox-exec', ['-p', profile, '/usr/bin/perl', '-e', program, String(process.pid)],
  { stdio: 'inherit', detached: session === 'own' });
child.on('exit', () => process.exit(0));"""
        let! host = unconfined ()
        let! _, out, err =
            runInSandbox
                host
                Node.Api.``process``.execPath
                [ "-e"
                  launcher
                  SeatbeltProfile.render profile
                  markerOf + "print marker($ARGV[0]);"
                  (if ownSession then "own" else "shared") ]
                (Map.ofList [ "YESSION_MARKER", "launcher" ])
                None
        do! host.Dispose ()
        return out.Trim (), err
    }

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

        // A Mac has two ways to ask for a home. A program built on CoreFoundation does not
        // read `HOME`: it takes the home from the account, and it reads `CFFIXED_USER_HOME`
        // before that. The policy of a sandbox names its home both ways
        // (`Sandboxes.policyFor`), and this asks the kernel's side of it: that Foundation,
        // behind this profile, gives the home that the second name says. `osascript` is the
        // program that each Mac has and that can ask Foundation.
        testCaseAsync "a program that asks CoreFoundation for its home gets the home that its sandbox names" (async {
            let home = freshDir ()
            let policy = policyIn home
            let named = policy.Env |> Map.add "HOME" home |> Map.add "CFFIXED_USER_HOME" home
            let! sandbox = startSandbox { policy with Env = named }
            let ask = """ObjC.import("Foundation"); $.NSHomeDirectory().js"""
            let! _, out, err = shell sandbox (sprintf "/usr/bin/osascript -l JavaScript -e %s" (quoted ask))
            do! sandbox.Dispose ()
            // Foundation leaves `/private` off a path below `/tmp` or `/var`. So this compares
            // the directory that it gave, and not how it spelled the path.
            let answered = out.Trim ()
            let gave = if TestFiles.exists answered then TestFiles.canonical answered else answered
            Expect.equal gave home (sprintf "Foundation gave the sandbox's home, and stderr said: %s" err)
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

        // The command that a host body declares to make itself ready (`prepare:`). Here it
        // writes a tool into the workspace, and the entrypoint of the sandbox is that tool.
        testCaseAsync "work runs behind an entrypoint that the `prepare` command of its sandbox made" (async {
            let host =
                { HostSpec.Prepare =
                    Some
                        [ "/bin/sh"
                          "-c"
                          "printf '#!/bin/sh\\nexec /usr/bin/env BEHIND=prepared \"$@\"\\n' > enter && chmod +x enter" ]
                  HostSpec.Entrypoint = Some [ "./enter" ] }
            let! sandbox = startSandboxBehind host (policyIn (freshDir ()))
            let! _ = Yession.Session.SessionEnvironment.runPreparation (SandboxRuntime.preparation (OnHost host)) sandbox
            let! _, out, _ = shell sandbox "echo \"$BEHIND\""
            do! sandbox.Dispose ()
            Expect.equal (out.Trim ()) "prepared" "the command ran bare, when the entrypoint that it wrote was not there yet"
        })

        testCaseAsync "a `prepare` command holds what its sandbox holds, and nothing more" (async {
            let outside = freshDir ()
            let host =
                { HostSpec.Prepare = Some [ "/bin/sh"; "-c"; sprintf "echo taken > %s" (quoted (outside + "/taken")) ]
                  HostSpec.Entrypoint = None }
            let! sandbox = startSandboxBehind host (policyIn (freshDir ()))
            let! _ = Yession.Session.SessionEnvironment.runPreparation (SandboxRuntime.preparation (OnHost host)) sandbox
            do! sandbox.Dispose ()
            Expect.isFalse (TestFiles.exists (outside + "/taken")) "the profile of the sandbox refused a write outside its workspace"
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
        // Seatbelt does not know stops `sandbox-exec`, which says which word. One `sandbox-exec`
        // holds the sandbox, so the sandbox does not start, and its refusal has those words.
        testCaseAsync "a rule that Seatbelt cannot read stops the start of the sandbox, and sandbox-exec names the word" (async {
            let rule = SeatbeltAllow.parse "(allow no-such-operation)" |> expect
            let! started =
                Sandboxes.SeatbeltSandbox.create (tools ()) HostSpec.none (Some (ownRoot ())) { policyIn (freshDir ()) with SeatbeltRules = [ rule ] }
            let! err =
                async {
                    match started with
                    | Ok sandbox ->
                        do! sandbox.Dispose ()
                        return "the sandbox started"
                    | Error refusal -> return refusal
                }
            Expect.isTrue (err.Contains "no-such-operation") (sprintf "said: %s" err)
        })

        // A profile is version 3, and `sandbox-exec` reads a rule of an operator as version 3
        // too. `unix-socket` is a word of a filter that only version 1 knows.
        testCaseAsync "a rule with a word that only version 1 knows stops the start of the sandbox, and sandbox-exec names the word" (async {
            let rule = SeatbeltAllow.parse "(allow network-outbound (remote unix-socket))" |> expect
            let! started =
                Sandboxes.SeatbeltSandbox.create (tools ()) HostSpec.none (Some (ownRoot ())) { policyIn (freshDir ()) with SeatbeltRules = [ rule ] }
            let! err =
                async {
                    match started with
                    | Ok sandbox ->
                        do! sandbox.Dispose ()
                        return "the sandbox started"
                    | Error refusal -> return refusal
                }
            Expect.isTrue (err.Contains "unix-socket") (sprintf "said: %s" err)
        })

        // Each deny of the head after the first two stands behind a guard, so that a macOS
        // that does not know a name leaves that deny out. A name that no macOS knows, for
        // example a name with a wrong letter, is then left out on each macOS, and no sandbox
        // says so. This case takes each guarded name from the head itself. It asks the
        // `sandbox-exec` of this Mac about each one: a profile with a plain deny of a name
        // that it does not know does not compile.
        //
        // We measured macOS 26.5.2, which knows each name. An older macOS does not know each
        // one, and that is what the guards are for. A failure there is not a fault, so the
        // case stands as pending there and says why. On a later macOS the case fails when a
        // name is gone, and then a deny of the head is not in force.
        (if atLeastTheMeasuredMacOS () then
            testCaseAsync "this macOS knows each operation that the head denies behind a guard" (async {
                let names = SeatbeltProfile.head |> List.choose guarded
                if List.isEmpty names then failwith "no line of the head has a guard that this case can read"
                let! host = unconfined ()
                let unknown = ResizeArray<string> ()
                for name in names do
                    let! run, _, err =
                        runInSandbox
                            host
                            "/usr/bin/sandbox-exec"
                            [ "-p"; sprintf "(version 3)(allow default)(deny %s)" name; "/usr/bin/true" ]
                            Map.empty
                            None
                    if exitCode run <> 0 then unknown.Add (sprintf "%s: %s" name (err.Trim ()))
                do! host.Dispose ()
                Expect.equal (List.ofSeq unknown) [] "each name compiles in a plain deny, so each guard holds and each deny is in force"
            })
         else
            ptestCase
                "this macOS knows each operation that the head denies behind a guard (macOS 26.5 or later only: an older macOS knows fewer names)"
                (fun () -> ()))

        // --- what version 3 refuses until a profile allows it -------------------------------
        //
        // `(deny default)` of version 3 refuses each system call, and each operation that the
        // fixed text of a profile does not allow (`SeatbeltProfile.essentials`). Each case
        // above that runs a command needs most of those lines: no program starts without
        // them. These cases are for the lines that a plain command does not need, and for one
        // operation that the head leaves open.

        // The loader asks the policy `AMFI` of the kernel when a program starts. When the
        // profile refuses that call, the loader ignores each `DYLD_` variable. Node is the
        // program here, because the system removes those variables for a program of Apple.
        //
        // The case is about the profile, so it runs only where the same command prints the
        // lines of the loader outside a sandbox (`loaderAnswersOutside`). A Node whose
        // signature asks for the hardened runtime prints none in each place, and the case
        // then stands as pending and says why.
        (if loaderAnswersOutside () then
            testCaseAsync "the loader takes its variables in a sandbox, as it does outside one" (async {
                let! sandbox = startSandbox (policyIn (freshDir ()))
                let! _, _, err =
                    runInSandbox sandbox Node.Api.``process``.execPath [ "-e"; "0" ] (Map.ofList [ "DYLD_PRINT_LIBRARIES", "1" ]) None
                do! sandbox.Dispose ()
                Expect.isTrue (err.Contains "dyld[") "the loader printed each library, as the variable asks"
            })
         else
            ptestCase
                "the loader takes its variables in a sandbox, as it does outside one (not this Node: its loader ignores them outside a sandbox too)"
                (fun () -> ()))

        testCaseAsync "a command makes a hard link in its workspace" (async {
            let workspace = freshDir ()
            TestFiles.write (workspace + "/first") "linked"
            let! sandbox = startSandbox (policyIn workspace)
            let! _, out, err = shell sandbox "ln first second && cat second"
            do! sandbox.Dispose ()
            Expect.equal (out.Trim ()) "linked" (sprintf "the second name reads as the file, and stderr said: %s" err)
        })

        // `/bin/cp`, because `-c` is an option of the `cp` of macOS, and a `cp` of a different
        // maker can be first on PATH.
        testCaseAsync "a command makes a copy that shares the blocks of its file" (async {
            let workspace = freshDir ()
            TestFiles.write (workspace + "/first") "cloned"
            let! sandbox = startSandbox (policyIn workspace)
            let! _, out, err = shell sandbox "/bin/cp -c first second && cat second"
            do! sandbox.Dispose ()
            Expect.equal (out.Trim ()) "cloned" (sprintf "the copy reads as the file, and stderr said: %s" err)
        })

        // The `cp` of macOS asks the policy `Quarantine` of the kernel when it copies a file.
        // When the profile refuses that call, the copy of a file that has the quarantine mark
        // has no mark, and nothing says so. A `cp` that clones the file keeps the mark with
        // no such call, so this case names `/bin/cp`, which copies the data.
        testCaseAsync "a copy keeps the quarantine mark of its file" (async {
            let workspace = freshDir ()
            TestFiles.write (workspace + "/first") "marked"
            let! sandbox = startSandbox (policyIn workspace)
            let! _, out, err =
                shell
                    sandbox
                    "/usr/bin/xattr -w com.apple.quarantine '0083;65000000;Safari;' first && /bin/cp first second && /usr/bin/xattr -p com.apple.quarantine second"
            do! sandbox.Dispose ()
            Expect.isTrue (out.Contains ";") (sprintf "the copy has a mark, and stdout said: %s, and stderr said: %s" out err)
        })

        // `file-lock` is one of the operations that the default of version 3 leaves open,
        // and the head does not deny it: devenv locks its files.
        testCaseAsync "a command locks a file of its workspace" (async {
            let! sandbox = startSandbox (policyIn (freshDir ()))
            let program = """use Fcntl qw(:flock); open(my $file, '>', 'lock') or die "no file: $!"; print flock($file, LOCK_EX | LOCK_NB) ? 'locked' : 'refused ' . ($! + 0);"""
            let! _, out, err = runInSandbox sandbox "/usr/bin/perl" [ "-e"; program ] Map.empty None
            do! sandbox.Dispose ()
            Expect.equal (out.Trim ()) "locked" (sprintf "the kernel gave the lock, and stderr said: %s" err)
        })

        // --- the processes that a sandbox reads ------------------------------------------------
        //
        // `(deny default)` of version 1 does not refuse the operations that read the arguments
        // and the environment of a process. A profile is version 3, whose default refuses them
        // on the macOS that we measured, and its head denies them by name too. The essentials
        // allow them for the processes of the same sandbox (`SeatbeltProfile.head`). A command
        // holds a loan in its environment, so these cases ask the kernel who reads an
        // environment.

        testCaseAsync "a command reads the environment of a process that it started" (async {
            let! sandbox = startSandbox (policyIn (freshDir ()))
            // One program starts the marked process and reads it, so the two are in one
            // sandbox. The child prints a line when it runs, and the read follows that line.
            let program =
                markerOf
                + """$ENV{YESSION_MARKER} = 'inside';
my $pid = open(my $child, '-|', $ARGV[0], '-e', 'console.log(1); setTimeout(() => {}, 600000)') or die "no child: $!";
my $line = <$child>;
print marker($pid);
kill('TERM', $pid);"""
            let! _, out, err =
                runInSandbox sandbox "/usr/bin/perl" [ "-e"; program; Node.Api.``process``.execPath ] Map.empty None
            do! sandbox.Dispose ()
            Expect.equal (out.Trim ()) "marker inside" (sprintf "the read of its own child, and stderr said: %s" err)
        })

        testCaseAsync "a command does not read the environment of a process outside its sandbox" (async {
            let! read, err = withOutside "outside" (readsUnder (policyIn (freshDir ())))
            Expect.equal read "refused 1" (sprintf "the kernel refused the read with EPERM, and stderr said: %s" err)
        })

        testCaseAsync "a command does not read the environment of a process of another sandbox" (async {
            let! other = startSandbox (policyIn (freshDir ()))
            let! read, err = withMarked other "other" (readsUnder (policyIn (freshDir ())))
            do! other.Dispose ()
            Expect.equal read "refused 1" (sprintf "the kernel refused the read with EPERM, and stderr said: %s" err)
        })

        // The call is `csops`, which answers the status of the code signature of a process.
        // The default of version 1 leaves that call open for a process outside the sandbox,
        // and a deny of `process-info*` by name does not close it there. Version 3 refuses it.
        testCaseAsync "a command does not read the code signature of a process outside its sandbox" (async {
            let! out, err =
                withOutside "outside" (fun pid ->
                    async {
                        let! sandbox = startSandbox (policyIn (freshDir ()))
                        // `+ 0`, because Perl gives `syscall` a pointer for an argument that is a string.
                        let program = "my $status = pack('L', 0); print syscall(169, $ARGV[0] + 0, 0, $status, 4) == 0 ? 'answered' : 'refused ' . ($! + 0);"
                        let! _, out, err = runInSandbox sandbox "/usr/bin/perl" [ "-e"; program; string pid ] Map.empty None
                        do! sandbox.Dispose ()
                        return out, err
                    })
            Expect.equal (out.Trim ()) "refused 1" (sprintf "the kernel refused the call with EPERM, and stderr said: %s" err)
        })

        // The process that starts a sandbox is the session, and it is outside the sandbox. A
        // report says that a deny with the filter `(target others)` leaves a process in the
        // process group of the sandbox readable, so the head's deny has no filter. These two
        // cases put the launcher in the process group of the confined command, and then in a
        // different session.
        testCaseAsync "a command does not read the environment of the process that started its sandbox" (async {
            let! read, err = readsItsLauncher false
            Expect.equal read "refused 1" (sprintf "the kernel refused the read with EPERM, and stderr said: %s" err)
        })

        testCaseAsync "a command in a session of its own does not read the environment of the process that started its sandbox" (async {
            let! read, err = readsItsLauncher true
            Expect.equal read "refused 1" (sprintf "the kernel refused the read with EPERM, and stderr said: %s" err)
        })

        // The kernel gives the arguments of a process when the check of `process-info` allows
        // it, or the check of `sysctl-read` does. An operator can write a rule that allows
        // each sysctl. The head's deny of `kern.procargs` has a filter, so it holds against
        // that rule.
        testCaseAsync "a rule that allows each sysctl does not open the environment of a process outside the sandbox" (async {
            let rule = SeatbeltAllow.parse "(allow sysctl-read)" |> expect
            let! read, err = withOutside "outside" (readsUnder { policyIn (freshDir ()) with SeatbeltRules = [ rule ] })
            Expect.equal read "refused 1" (sprintf "the kernel refused the read with EPERM, and stderr said: %s" err)
        })

        // Two rules of an operator that DO open the environment again, pinned as what the
        // kernel does. Between two rules that both have a filter, or that both have none, the
        // later rule wins, and a rule of an operator comes after the head. docs/deployment.md
        // tells an operator what these two rules give a sandbox.
        testCaseAsync "a rule that allows process information with no filter opens the environment of a process outside the sandbox" (async {
            let rule = SeatbeltAllow.parse "(allow process-info*)" |> expect
            let! read, err = withOutside "outside" (readsUnder { policyIn (freshDir ()) with SeatbeltRules = [ rule ] })
            Expect.equal read "marker outside" (sprintf "the later rule with no filter won, and stderr said: %s" err)
        })

        testCaseAsync "a rule that allows the sysctls of a prefix that covers kern.procargs opens the environment of a process outside the sandbox" (async {
            let rule = SeatbeltAllow.parse """(allow sysctl-read (sysctl-name-prefix "kern.proc"))""" |> expect
            let! read, err = withOutside "outside" (readsUnder { policyIn (freshDir ()) with SeatbeltRules = [ rule ] })
            Expect.equal read "marker outside" (sprintf "the later rule with a filter won, and stderr said: %s" err)
        })

        // An operator can offer the process list of the host as a resource: a rule for
        // `sysmond`, which is the service that `pgrep` asks. The service answers for each
        // process of the host, and the denies at the head do not change that.
        testCaseAsync "a rule for the process list still finds a process outside the sandbox" (async {
            let name = sprintf "listed-%d" (int Node.Api.``process``.pid)
            let rule = SeatbeltAllow.parse """(allow mach-lookup (global-name "com.apple.sysmond"))""" |> expect
            let! found, pid, err =
                withOutside name (fun pid ->
                    async {
                        let! sandbox = startSandbox { policyIn (freshDir ()) with SeatbeltRules = [ rule ] }
                        let! _, out, err = runInSandbox sandbox "/usr/bin/pgrep" [ "-f"; name ] Map.empty None
                        do! sandbox.Dispose ()
                        return out.Trim (), pid, err
                    })
            Expect.equal found (string pid) (sprintf "the pid that the service gave, and stderr said: %s" err)
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

// --- the supervisor of a sandbox ----------------------------------------------------------------
//
// One `sandbox-exec` holds a sandbox, and the supervisor inside it ends each process of the
// sandbox at the stop (`app/Supervisor.fs`). These cases ask the kernel, with real processes
// and the real ending signal, which processes that is: each process that a command started,
// and no process of a different sandbox or of the host.

/// A long-lived process that a case starts outside each sandbox, as a child of this process.
/// Answers its pid.
let private outsideProcess () : int =
    let child =
        ChildProcesses.spawn
            Node.Api.``process``.execPath
            [ "-e"; "setTimeout(() => {}, 600000)" ]
            { Cwd = None
              Env = ChildEnv.Replacing Map.empty
              Streams = { Stdin = Stdio.Ignore; Stdout = Stdio.Ignore; Stderr = Stdio.Ignore }
              Detached = false }
    // Node reports a spawn that failed as an `error` event, and it throws when nobody hears
    // that event.
    ChildProcessStreams.onError child ignore
    match ChildProcesses.pid child with
    | Some number -> number
    | None -> failwith "the process outside each sandbox did not start"

/// End a process that a case started, by its pid.
let private endByPid (pid: int) : unit = Processes.trySignal pid "SIGKILL" |> ignore

/// A command in `sandbox` that runs until something ends it, with `environment` beside what
/// its sandbox gives it. It prints its pid when it runs. Answers that pid, and the handle
/// of the command.
let private runningCommandWith (environment: Map<string, string>) (sandbox: Sandbox) : Async<int * SandboxProcessHandle> =
    async {
        let printed = System.Text.StringBuilder ()
        let! spawned =
            sandbox.Spawn
                { Executable = Node.Api.``process``.execPath
                  Arguments = [ "-e"; "console.log(process.pid); setTimeout(() => {}, 600000)" ]
                  Env = environment
                  WorkingDirectory = None
                  Via = Direct }
                (fun (stream, text) -> if stream = Stdout then printed.Append text |> ignore)
        match spawned with
        | Error reason -> return failwithf "the command did not start: %s" reason
        | Ok handle ->
            let! started = settledWithin 20000 (fun () -> printed.ToString().Contains "\n")
            if not started then failwith "the command did not print its pid"
            return int (printed.ToString().Trim ()), handle
    }

/// A command in `sandbox` that runs until something ends it. Answers its pid, and the handle
/// of the command.
let private runningCommand (sandbox: Sandbox) : Async<int * SandboxProcessHandle> = runningCommandWith Map.empty sandbox

/// Perl that asks the kernel for the environment of one process, and prints the value of
/// `LOAN` there. The call is the sysctl `kern.procargs2`, which is the call that `ps -E`
/// makes. Perl, because each Mac has it and it can make the call.
let private loanOf : string =
    """my $mib = pack('i3', 1, 49, $ARGV[0]); my $size = pack('Q', 1 << 20); my $buf = "\0" x (1 << 20);
if (syscall(202, $mib, 3, $buf, $size, 0, 0) != 0) { print 'refused ', $! + 0; exit 0; }
print substr($buf, 0, unpack('Q', $size)) =~ /LOAN=([\w-]+)/ ? "loan $1" : 'no loan';"""

/// What `text` became as the input of a command in `sandbox` that counts the bytes of its
/// input.
let private countedAsInput (sandbox: Sandbox) (text: string) : Async<string> =
    async {
        let out = System.Text.StringBuilder ()
        let! spawned =
            sandbox.Spawn
                { Executable = "/usr/bin/wc"; Arguments = [ "-c" ]; Env = Map.empty; WorkingDirectory = None; Via = Direct }
                (fun (stream, chunk) -> if stream = Stdout then out.Append chunk |> ignore)
        match spawned with
        | Error reason -> return failwithf "the command did not start: %s" reason
        | Ok handle ->
            handle.WriteStdin text
            handle.CloseStdin ()
            let! _ = handle.Exited
            return out.ToString().Trim ()
    }

/// A process in `sandbox` that left the process group of the command that started it: it is
/// the leader of a session of its own, and its command ended, so process 1 holds it.
/// Answers its pid.
let private leftItsGroup (sandbox: Sandbox) : Async<int> =
    async {
        let program =
            "const child = require('child_process').spawn(process.execPath, ['-e', 'setTimeout(() => {}, 600000)'], "
            + "{ detached: true, stdio: 'ignore' }); child.unref(); console.log(child.pid);"
        let! run, out, err = runInSandbox sandbox Node.Api.``process``.execPath [ "-e"; program ] Map.empty None
        if exitCode run <> 0 then failwithf "the command that starts the process failed, and said: %s" err
        return int (out.Trim ())
    }

/// Whether the process with this pid goes, in the time that a case gives it.
let private goes (pid: int) : Async<bool> = settledWithin 20000 (fun () -> not (Processes.isAlive pid))

let private supervisionCases () =
    [
        testCaseAsync "a stop ends a process that left the group of its command" (async {
            let! sandbox = startSandbox (policyIn (freshDir ()))
            let! pid = leftItsGroup sandbox
            do! sandbox.Dispose ()
            let! gone = goes pid
            Expect.isTrue gone "the process was in a session of its own, and the sandbox was still its sandbox"
        })

        testCaseAsync "a stop ends a command that still runs" (async {
            let! sandbox = startSandbox (policyIn (freshDir ()))
            let! pid, _ = runningCommand sandbox
            do! sandbox.Dispose ()
            let! gone = goes pid
            Expect.isTrue gone "the command would have run for ten minutes"
        })

        testCaseAsync "a command that still runs when its sandbox stops ends as one that a signal took" (async {
            let! sandbox = startSandbox (policyIn (freshDir ()))
            let! _, handle = runningCommand sandbox
            do! sandbox.Dispose ()
            let! run = handle.Exited
            Expect.equal run (SandboxExited -1) "the answer that the session got when each command was its own child"
        })

        testCaseAsync "a stop leaves a process of another sandbox running" (async {
            let! other = startSandbox (policyIn (freshDir ()))
            let! pid = leftItsGroup other
            let! sandbox = startSandbox (policyIn (freshDir ()))
            do! sandbox.Dispose ()
            let alive = Processes.isAlive pid
            do! other.Dispose ()
            Expect.isTrue alive "the kernel refused the signal for a process of a different sandbox"
        })

        testCaseAsync "a stop leaves a process outside each sandbox running" (async {
            let pid = outsideProcess ()
            let! sandbox = startSandbox (policyIn (freshDir ()))
            do! sandbox.Dispose ()
            let alive = Processes.isAlive pid
            endByPid pid
            Expect.isTrue alive "the kernel refused the signal for a process of the host"
        })

        // An operator can write a rule that lets a signal out of a sandbox. Under such a
        // profile a stop would end processes of the host, so the supervisor refuses at its
        // start, and the sandbox does not start.
        testCaseAsync "a sandbox whose profile lets a signal out does not start" (async {
            let rule = SeatbeltAllow.parse "(allow signal)" |> expect
            let! started =
                Sandboxes.SeatbeltSandbox.create (tools ()) HostSpec.none (Some (ownRoot ())) { policyIn (freshDir ()) with SeatbeltRules = [ rule ] }
            match started with
            | Ok sandbox ->
                do! sandbox.Dispose ()
                failwith "the sandbox started, and its stop would signal processes of the host"
            | Error refusal -> Expect.isTrue (refusal.Contains "lets a signal out of the sandbox") (sprintf "said: %s" refusal)
        })

        testCaseAsync "a command signals a process that an earlier command started" (async {
            let! sandbox = startSandbox (policyIn (freshDir ()))
            let! pid, _ = runningCommand sandbox
            let! run, _, err = shell sandbox (sprintf "kill -TERM %d" pid)
            do! sandbox.Dispose ()
            Expect.equal (exitCode run) 0 (sprintf "the two commands are in one sandbox, and stderr said: %s" err)
        })

        // A KNOWN LIMIT, pinned so that nobody takes it for held. One `sandbox-exec` holds each
        // command of a sandbox, and a profile lets a process read the arguments and the
        // environment of the processes of its own sandbox. A block holds the loan of a person
        // in its environment, so a second block of the same sandbox reads that loan. This is
        // the boundary that the header of `app/GitGateway.fs` states for a sandbox, and
        // docs/GAPS.md says that this backend has it.
        testCaseAsync "a command reads the environment of an earlier command of its sandbox, which is a known limit" (async {
            let! sandbox = startSandbox (policyIn (freshDir ()))
            let! pid, _ = runningCommandWith (Map.ofList [ "LOAN", "of-the-first-block" ]) sandbox
            let! _, out, err = runInSandbox sandbox "/usr/bin/perl" [ "-e"; loanOf; string pid ] Map.empty None
            do! sandbox.Dispose ()
            Expect.equal (out.Trim ()) "loan of-the-first-block" (sprintf "one sandbox-exec holds the two commands, and stderr said: %s" err)
        })

        // A command can end the supervisor: the two are in one sandbox. Such a supervisor
        // makes no stop, so the sandbox must say that its processes can still run. The command
        // signals the process that started it, and only a process with a pid above 1.
        testCaseAsync "a sandbox whose supervisor a command ended says that its processes can still run" (async {
            let! sandbox = startSandbox (policyIn (freshDir ()))
            let! run, _, _ = shell sandbox "[ \"$PPID\" -gt 1 ] && kill -KILL \"$PPID\"; sleep 5"
            do! sandbox.Dispose ()
            let said = match run with SandboxRunFailed reason -> reason | other -> sprintf "%A" other
            Expect.stringContains said "Processes of the sandbox can still run" "the sandbox answers the command that was in flight with the reason"
        })

        testCaseAsync "a sandbox still runs a command after one that Node could not start" (async {
            let! sandbox = startSandbox (policyIn (freshDir ()))
            let! _ = runInSandbox sandbox "/bin/echo" [ "a\u0000b" ] Map.empty None
            let! _, out, _ = runInSandbox sandbox "/bin/echo" [ "still here" ] Map.empty None
            do! sandbox.Dispose ()
            Expect.equal (out.Trim ()) "still here" "a NUL in an argument is the end of one command, and not of the supervisor"
        })

        testCaseAsync "an input of five megabytes reaches a command whole" (async {
            let! sandbox = startSandbox (policyIn (freshDir ()))
            let! counted = countedAsInput sandbox (String.replicate (5 * 1024 * 1024) "x")
            do! sandbox.Dispose ()
            Expect.equal counted "5242880" "the session sent it as bounded requests, and the command read each byte"
        })

        testCaseAsync "the end of one command leaves a second command running" (async {
            let! sandbox = startSandbox (policyIn (freshDir ()))
            let! _, first = runningCommand sandbox
            let! second, _ = runningCommand sandbox
            first.Kill ()
            let! _ = first.Exited
            let alive = Processes.isAlive second
            do! sandbox.Dispose ()
            Expect.isTrue alive "a cancel ends the process group of its own command, and no other"
        })
    ]

let tests =
    Tag.needs "Seatbelt integration" [ Tag.Srt ] (fun () ->
        testList "Seatbelt integration" (
            if onMac () then macCases () @ supervisionCases ()
            else [ ptestCase "the seatbelt backend confines (macOS only: no other platform has a Seatbelt)" (fun () -> ()) ]
        ))
