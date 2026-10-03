module Yession.Host.SandboxHost

// The srt host entry: ONE sandbox's srt manager, in a process of its own. A Session
// starts one per srt sandbox (`Sandboxes.SrtSandbox.create`, and the agent's `wrapperFor`)
// and speaks `SrtSandbox.HostWire` to it over stdin and stdout.
//
// A process rather than a module because srt's manager is a process-wide singleton: one
// filtering proxy pair, one allowlist, one set of intercepted hosts. While one manager served
// a whole session, every sandbox in it could reach what any of them was allowed, and a host
// one sandbox forwarded to the credential proxy was intercepted for all of them. One process
// per sandbox is one manager per sandbox, and the network policy a sandbox was given is the
// one it runs under.
//
// Not a bin: nobody runs this but a session, and it takes no options. It is found beside the
// module that starts it, which a bundle flattens into the session's own file.

open Yession.Host

Sandboxes.SrtSandbox.Host.serve ()
