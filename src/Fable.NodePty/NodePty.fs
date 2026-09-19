module Fable.NodePty

// Fable bindings to the `node-pty` npm package: a pseudo-terminal, which is what a sandbox
// opens when someone runs `vim`. The binding layer only: the slice of its surface the
// sandboxes use.
//
// SHAPES ONLY, and no `[<Import>]` anywhere, deliberately — the same reasoning as
// `Fable.NodeDataChannel`. The addon is native and built from source into the Nix
// `nodeModules` derivation; off Nix it may not be there at all, and a backend that cannot
// open a pty reports so rather than failing to load. So the Host `require`s it lazily,
// through a try, and views what it gets through `Exports`.
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

/// `spawn`'s options — the five this repository sets. `cwd` `None` starts the process where
/// this one is; `env` is the child's environment COMPLETE, as a plain object of names to
/// values (`createObj` makes one), because node-pty replaces rather than merges.
[<RequireQualifiedAccess>]
type ForkOptions =
    { name : string
      cols : int
      rows : int
      cwd : string option
      env : obj }

/// What `require('node-pty')` answers with.
type [<AllowNullLiteral>] Exports =
    abstract spawn : file: string * args: string array * options: ForkOptions -> Pty
