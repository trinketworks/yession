module Yession.Tests.ByteSpawn

// The byte spawn of the sandbox seam (`Sandbox.SpawnBytes`), on the host backend.
//
// The text spawn decodes each output as UTF-8 and encodes each input as UTF-8. A protocol
// that is not text does not survive that. These cases send bytes that a text stream changes,
// and expect them back unchanged. The srt, Seatbelt and Docker suites run the same case
// (`Support.bytesGoThroughUnchanged`) on their own backends.

open Fable.Pyxpecto
open Yession.Domain
open Yession.Domain.Sandboxes
open Yession.Host
open Yession.Tests.Support

let private policy : SandboxPolicy =
    { ReadPaths = []
      WritePaths = []
      AllowedDomains = None
      Sockets = []
      Binds = []
      Volumes = []
      SeatbeltRules = []
      Devices = []
      LinuxCapabilities = []
      Realisation = []
      Env = Sandboxes.hostBaseline (Sandboxes.ambientEnv ())
      WorkingDirectory = None
      Filesystem = Confined
      Derived = Map.empty }

let private startSandbox () : Async<Sandbox> =
    async {
        match! Sandboxes.HostSandbox.create HostSpec.none policy with
        | Error reason -> return failwithf "host sandbox failed: %s" reason
        | Ok sandbox -> return sandbox
    }

let tests =
    testList "The byte spawn on the host" [

        testCaseAsync "each byte comes back as it went in" (async {
            let! sandbox = startSandbox ()
            do! bytesGoThroughUnchanged sandbox
            do! sandbox.Dispose ()
        })

        testCaseAsync "the end of the input reaches the process" (async {
            let! sandbox = startSandbox ()
            // `cat` ends only when its input ends.
            let! run, out, _ = runBytesInSandbox sandbox "/bin/cat" [] [ bytesThatTextChanges ]
            Expect.equal (run, out) (SandboxExited 0, List.ofArray bytesThatTextChanges) "the whole input, and then the end"
            do! sandbox.Dispose ()
        })

        testCaseAsync "the error output carries bytes too, under its own name" (async {
            let! sandbox = startSandbox ()
            let! run, out, err = runBytesInSandbox sandbox "/bin/sh" [ "-c"; "cat >&2" ] [ bytesThatTextChanges ]
            Expect.equal (run, out, err) (SandboxExited 0, [], List.ofArray bytesThatTextChanges) "nothing on the output, and each byte on the error output"
            do! sandbox.Dispose ()
        })

        testCaseAsync "a character that two writes cut comes back as its bytes" (async {
            let! sandbox = startSandbox ()
            // The three bytes of one character, in two writes with a wait between them.
            let! run, out, _ =
                runBytesInSandbox sandbox "/bin/sh" [ "-c"; "printf '\\342\\202'; sleep 0.2; printf '\\254'" ] []
            Expect.equal (run, out) (SandboxExited 0, [ 0xE2uy; 0x82uy; 0xACuy ]) "the bytes, with nothing put in their place"
            do! sandbox.Dispose ()
        })

        testCaseAsync "the process gives back the code that it ended with" (async {
            let! sandbox = startSandbox ()
            let! run, _, _ = runBytesInSandbox sandbox "/bin/sh" [ "-c"; "exit 7" ] []
            Expect.equal run (SandboxExited 7) "the code that the process chose"
            do! sandbox.Dispose ()
        })

        testCaseAsync "a kill ends a process that reads no input" (async {
            let! sandbox = startSandbox ()
            let! spawned =
                sandbox.SpawnBytes
                    { Executable = "/bin/sleep"; Arguments = [ "30" ]; Env = Map.empty; WorkingDirectory = None; Via = Entrypoint }
                    ignore
            match spawned with
            | Error reason -> failwithf "the process did not start: %s" reason
            | Ok handle ->
                handle.Kill ()
                let! run = handle.Exited
                // -1 is the code of a process that a signal ended.
                Expect.equal run (SandboxExited -1) "the process ended, and not after 30 seconds"
            do! sandbox.Dispose ()
        })
    ]
