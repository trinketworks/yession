module Yession.Host.SandboxSupervisor

// The supervisor entry: the first process INSIDE one Seatbelt sandbox. A session starts one
// for each seatbelt sandbox, under `sandbox-exec` and the profile of that sandbox, and speaks
// `Supervisor.Wire` to it over stdin and stdout. `Supervisor.fs` says what it does and why.
//
// Not a bin: nobody runs this but a session, and it takes no options. The session finds it
// beside the module that starts it, which a bundle flattens into the session's own file.
//
// This file names the signal that ends the processes of a sandbox, and no other file does.
// A supervisor ends such a process only with the two proofs of `Supervisor.Confinement`,
// and this entry cannot give it one.

open Yession.Host

Supervisor.run "SIGKILL"
