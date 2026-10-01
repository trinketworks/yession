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

// --- The config srt reads ---------------------------------------------------------------------
//
// Built with `jsOptions`, because which fields it has is a decision srt reads: an optional one
// nobody assigned is ABSENT, and absent is what srt reads as "you decide" — `bwrapPath ??
// 'bwrap'`, a ripgrep default parameter, `if (!enableWeakerNestedSandbox)`.

/// The network half. srt reads it from the config the manager was INITIALIZED with, never
/// from a spawn's own — so a manager's network policy is fixed when it starts.
type [<AllowNullLiteral>] NetworkConfig =
    abstract allowedDomains : string array with get, set
    abstract deniedDomains : string array with get, set
    /// Egress to nothing the allowlist does not name — no "ask" for an unlisted host.
    abstract strictAllowlist : bool with get, set
    abstract allowUnixSockets : string array with get, set
    /// Every unix socket, for a host that cannot scope one by path.
    abstract allowAllUnixSockets : bool with get, set
    /// Hosts whose `CONNECT`s srt's proxy hands to another proxy's UNIX socket rather than
    /// dialling. Read from the manager's config, like the allowlist.
    abstract mitmProxy : MitmProxyConfig with get, set

/// Where srt sends the `CONNECT`s for some hosts instead of dialling them.
and [<AllowNullLiteral>] MitmProxyConfig =
    abstract socketPath : string with get, set
    abstract domains : string array with get, set

/// The filesystem half: regions denied, and holes opened back in them.
type [<AllowNullLiteral>] FilesystemConfig =
    abstract denyRead : string array with get, set
    abstract allowRead : string array with get, set
    abstract allowWrite : string array with get, set
    abstract denyWrite : string array with get, set
    /// Writes to `.git/config`, which srt denies unless told otherwise.
    abstract allowGitConfig : bool with get, set
    /// No read or write rules at all, the mandatory denies included.
    abstract disabled : bool with get, set

/// The ripgrep srt scans for files to deny with.
type [<AllowNullLiteral>] RipgrepConfig =
    abstract command : string with get, set

/// srt's config object: what `initialize` is handed for the session, and what a spawn's
/// `customConfig` overrides it with.
type [<AllowNullLiteral>] RuntimeConfig =
    abstract network : NetworkConfig with get, set
    abstract filesystem : FilesystemConfig with get, set
    abstract bwrapPath : string with get, set
    abstract socatPath : string with get, set
    abstract ripgrep : RipgrepConfig with get, set
    /// The host's `/proc` kept and capabilities not dropped — what an unprivileged container,
    /// which cannot nest a user namespace, is left with.
    abstract enableWeakerNestedSandbox : bool with get, set

/// The platform's `AbortSignal`, as `wrapWithSandboxArgv` takes one to cancel a wrap in
/// flight. Opaque: nothing here cancels a wrap, so nothing here makes one — `None` is the only
/// value ever passed, and the slot is declared because the arguments after it are positional.
type AbortSignal =
    interface end

/// What `wrapWithSandboxArgv` answers with: the confined command line to spawn instead.
type [<AllowNullLiteral>] Wrapped =
    abstract argv : string array

/// The manager: a PROCESS-WIDE singleton of statics. One filtering proxy pair, one egress
/// allowlist, initialized once — which is why the Host runs one per sandbox, each in a process
/// of its own.
type [<AllowNullLiteral>] SandboxManager =
    abstract isSupportedPlatform : unit -> bool
    /// `initialize(runtimeConfig, sandboxAskCallback?, enableLogMonitor?)` — the config as
    /// the Host built it. Returns early once a manager exists.
    abstract initialize : config: RuntimeConfig -> JS.Promise<unit>
    abstract reset : unit -> JS.Promise<unit>
    /// `wrapWithSandboxArgv(command, binShell?, customConfig?, abortSignal?, cwd?)`. The
    /// per-spawn `customConfig` wins outright over the session's for what it names — the
    /// filesystem profile rides here. `cwd` `None` is "wherever this process is", which is
    /// how srt reads a missing one.
    abstract wrapWithSandboxArgv :
        command: string * binShell: string option * customConfig: RuntimeConfig * abortSignal: AbortSignal option * cwd: string option ->
            JS.Promise<Wrapped>
    /// Where srt's Linux egress bridge listens: the unix sockets the in-sandbox socat
    /// connects to. Both are absent off Linux, where Seatbelt needs no such bridge.
    abstract getLinuxHttpSocketPath : unit -> string option
    abstract getLinuxSocksSocketPath : unit -> string option

/// What the package exports, as much of it as is used.
type [<AllowNullLiteral>] Exports =
    abstract SandboxManager : SandboxManager

/// The dynamic import.
let load () : JS.Promise<Exports> = importDynamic<Exports> "@anthropic-ai/sandbox-runtime"
