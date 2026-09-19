module Fable.SandboxRuntime

// Fable bindings to `@anthropic-ai/sandbox-runtime` (srt): bubblewrap + socat on Linux,
// Seatbelt on macOS, behind one `SandboxManager`. The binding layer only — the slice of the
// manager the Host drives, and nothing else.
//
// Loaded on demand and never statically: the package pulls a proxy stack and a TLS library,
// and a session on the host backend must not pay for either. It is ESM-only, so the load is
// a dynamic `import` rather than a `createRequire`. `load` is that import.
//
// Fable-only: `dotnet build` type-checks it; Fable emits the JS that runs on Node.

open Fable.Core
open Fable.Core.JsInterop

/// The network half of srt's config — the two fields this repository widens after the
/// manager is up. srt reads both from the config the manager was INITIALIZED with, never
/// from a spawn's own, which is why they are rewritten on the manager rather than sent
/// with the command.
type [<AllowNullLiteral>] NetworkConfig =
    abstract allowedDomains : string array
    abstract allowUnixSockets : string array

/// srt's config object, opaque beyond `network`. The Host BUILDS one from a policy — as a
/// plain object, because which fields it has is a decision srt reads (an absent one is "you
/// decide") — and reads it back only to widen the network. `updateConfig` REPLACES what it
/// is given, so a widened copy is taken with `Object.assign` over the whole object rather
/// than through a record, which would drop every field nothing here declares.
type [<AllowNullLiteral>] RuntimeConfig =
    abstract network : NetworkConfig

/// What `wrapWithSandboxArgv` answers with: the confined command line to spawn instead.
type [<AllowNullLiteral>] Wrapped =
    abstract argv : string array

/// The manager: a PROCESS-WIDE singleton of statics. One filtering proxy pair, one egress
/// allowlist, initialized once.
type [<AllowNullLiteral>] SandboxManager =
    abstract isSupportedPlatform : unit -> bool
    /// `initialize(runtimeConfig, sandboxAskCallback?, enableLogMonitor?)` — the config as
    /// the Host built it. Returns early once a manager exists.
    abstract initialize : config: obj -> JS.Promise<unit>
    abstract reset : unit -> JS.Promise<unit>
    /// `wrapWithSandboxArgv(command, binShell?, customConfig?, abortSignal?, cwd?)`. The
    /// per-spawn `customConfig` wins outright over the session's for what it names — the
    /// filesystem profile rides here. `cwd` `None` is "wherever this process is", which is
    /// how srt reads a missing one.
    abstract wrapWithSandboxArgv :
        command: string * binShell: string option * customConfig: obj * abortSignal: obj option * cwd: string option ->
            JS.Promise<Wrapped>
    /// Where srt's Linux egress bridge listens: the unix sockets the in-sandbox socat
    /// connects to. Both are absent off Linux, where Seatbelt needs no such bridge.
    abstract getLinuxHttpSocketPath : unit -> string option
    abstract getLinuxSocksSocketPath : unit -> string option
    /// The manager's own config object — absent before `initialize`.
    abstract getConfig : unit -> RuntimeConfig option
    abstract updateConfig : RuntimeConfig -> unit

/// What the package exports, as much of it as is used.
type [<AllowNullLiteral>] Exports =
    abstract SandboxManager : SandboxManager

/// The dynamic import.
let load () : JS.Promise<Exports> = importDynamic<Exports> "@anthropic-ai/sandbox-runtime"
