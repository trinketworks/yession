module Fable.NodePty

// Fable bindings to the `node-pty` npm package: a pseudo-terminal, which is what a sandbox
// opens when someone runs `vim`. The binding layer only: the slice of its surface the
// sandboxes use.
//
// No static `import` of the package, deliberately — the same reasoning as
// `Fable.NodeDataChannel`. The addon is native and built from source into the Nix
// `nodeModules` derivation; off Nix it may not be there at all, and a backend that cannot
// open a pty reports so rather than failing to load. So what `require('node-pty')` answers
// with is declared here as `Exports`, `load` is the `require` that answers it, and the Host
// calls `load` lazily, through a try.
//
// Fable-only: `dotnet build` type-checks it; Fable emits the JS that runs on Node.

open Fable.Core

/// What `onExit` hands over. node-pty reports a code AND a signal; a process a signal ended
/// has no meaningful code, and what to say about that is the caller's.
type [<AllowNullLiteral>] Exit =
    abstract exitCode : int
    /// The signal that ended the process, when one did. `None` — and node-pty's own `0` —
    /// when none did.
    abstract signal : int option

type [<AllowNullLiteral>] Pty =
    abstract pid : int
    /// Output, as text. One stream, not two: a tty has a single device and stdout/stderr
    /// are indistinguishable on it by construction.
    abstract onData : (string -> unit) -> unit
    abstract onExit : (Exit -> unit) -> unit
    abstract resize : columns: int * rows: int -> unit
    abstract write : data: string -> unit
    /// `kill(signal?)`: SIGHUP when none is named, as a terminal closing sends.
    abstract kill : ?signal: string -> unit

/// A child's environment COMPLETE, as node-pty reads it: a plain object of names to values.
/// Opaque, and made only by `Environment.ofMap` — node-pty replaces rather than merges, so
/// there is no such thing as a partial one.
type Environment =
    interface end

[<RequireQualifiedAccess>]
module Environment =

    /// `Object.fromEntries` over the pairs, which Fable compiles to two-element arrays — the
    /// shape it takes.
    [<Emit("Object.fromEntries($0)")>]
    let private ofEntries (entries: (string * string) array) : Environment = jsNative

    let ofMap (variables: Map<string, string>) : Environment = ofEntries (Map.toArray variables)

/// `spawn`'s options — the five this repository sets. `cwd` `None` starts the process where
/// this one is.
[<RequireQualifiedAccess>]
type ForkOptions =
    { name : string
      cols : int
      rows : int
      cwd : string option
      env : Environment }

/// Both ends of a pty `Native.open` made, as descriptors, and the far end's device name.
///
/// Both are NON-blocking, as node-pty leaves them: what writes the master has to expect
/// `EAGAIN`, and the far end has to be made blocking again before a program reads it as its
/// stdin — Node's `spawn` does, for the three standard streams. Both are close-on-exec only
/// as this repository builds node-pty (nix/node-pty-cloexec.patch); npm's leaves them
/// inheritable, which for the master means every later child holds this terminal's keyboard.
type [<AllowNullLiteral>] Opened =
    abstract master : int
    abstract slave : int
    abstract pty : string

/// The addon underneath `spawn`, which node-pty exports as `native`: a pty with NO process on
/// it yet. `spawn` opens one and makes it the controlling terminal of the session it forks,
/// which is the one thing a caller that wants a process FURTHER DOWN to take it cannot have —
/// a terminal is the controlling terminal of at most one session.
type [<AllowNullLiteral>] Native =
    /// `openpty(3)` at this size.
    abstract ``open`` : columns: int * rows: int -> Opened
    /// `TIOCSWINSZ` on a master, which is what sends the far end's foreground job `SIGWINCH`.
    abstract resize : master: int * columns: int * rows: int -> unit

/// What `require('node-pty')` answers with.
type [<AllowNullLiteral>] Exports =
    abstract spawn : file: string * args: string array * options: ForkOptions -> Pty
    /// `null` on Windows, which has no `openpty`.
    abstract native : Native

/// `createRequire(from)` — a CommonJS `require` resolving from the module at `from`. Typed as
/// answering THIS package's exports, because `load` is the only thing that asks it anything.
///
/// A `Func` rather than an F# arrow: Fable uncurries an imported function that answers an
/// arrow, which would rewrite `createRequire(from)(id)` into `createRequire(from, id)` — one
/// call where two were meant.
[<Import("createRequire", "node:module")>]
let private createRequire (from: string) : System.Func<string, Exports> = jsNative

/// `require('node-pty')`, resolved from the module at `from` (a `file:` URL) — the caller's
/// own, which is where its `node_modules` are. This is where the addon's typing is asserted,
/// and the only place. THROWS the way `require` does when there is nothing to load.
let load (from: string) : Exports = (createRequire from).Invoke "node-pty"
