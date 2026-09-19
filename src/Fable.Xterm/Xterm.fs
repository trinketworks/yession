module Fable.Xterm

// Fable bindings to `@xterm/headless` and `@xterm/addon-serialize`: the same emulator the
// browser renders with, minus the DOM. The binding layer only — the slice of the surface the
// emulator capability uses, and nothing else. Only the members used are typed; everything
// else is opaque.
//
// Fable-only: `dotnet build` type-checks it; Fable emits the JS that runs on Node and in the
// browser alike, because this module is bundled for BOTH platforms — the Session Process
// keeps a screen with it, and the client composes a live one from it.

open Fable.Core

/// Which screen a buffer is: the normal one, or the alternate a full-screen program takes.
[<StringEnum; RequireQualifiedAccess>]
type BufferType =
    | Normal
    | Alternate

type [<AllowNullLiteral>] Buffer =
    abstract ``type`` : BufferType

type [<AllowNullLiteral>] Buffers =
    abstract active : Buffer
    /// Fires with the new active buffer whenever a program enters or leaves the alternate
    /// screen — xterm's own API for the transition, so no DECSET 1049 parsing anywhere.
    abstract onBufferChange : (Buffer -> unit) -> unit

/// Something `Terminal.loadAddon` takes. The addons are classes of their own, and the
/// terminal asks nothing of one but that it be loaded.
type [<AllowNullLiteral>] Addon =
    interface
    end

type [<AllowNullLiteral>] Terminal =
    /// Buffered and parsed asynchronously; the callback fires once THIS write has been
    /// applied, and writes are applied in order. There is no synchronous form.
    abstract write : string * (unit -> unit) -> unit
    abstract resize : int * int -> unit
    abstract dispose : unit -> unit
    abstract loadAddon : Addon -> unit
    /// The active buffer, and the transition between the two.
    abstract buffer : Buffers

/// `new Terminal(options)`'s argument — the four options this repository sets.
/// `allowProposedApi` is what the serialize addon requires.
[<RequireQualifiedAccess>]
type TerminalOptions =
    { cols : int
      rows : int
      allowProposedApi : bool
      scrollback : int }

/// The `Terminal` class as the package exports it: `new Terminal(options)`.
type [<AllowNullLiteral>] TerminalClass =
    [<Emit("new ($0)($1)")>]
    abstract Create : options: TerminalOptions -> Terminal

/// What `@xterm/headless` exports.
type [<AllowNullLiteral>] HeadlessExports =
    abstract Terminal : TerminalClass

// The two packages ship differently, and each import has to be the form that resolves under
// both platforms — Node for the Session Process, esbuild-for-the-browser for the client —
// and they are not the same form.
//
// `@xterm/headless` is a DEFAULT import: its `main` is a CommonJS bundle and it declares no
// `exports` map, so Node resolves an ESM `import` to that CJS file and offers only the
// default — a named `import { Terminal }` fails at load with "does not provide an export
// named 'Terminal'". Its `module` field names `lib/xterm.mjs` (the browser build), which is
// not shipped in the package, so a bundler falls back to the same CJS main.
[<ImportDefault("@xterm/headless")>]
let headless : HeadlessExports = jsNative

/// The serialize addon: the active screen, scrollback included, as the escape sequences
/// that redraw it.
type [<AllowNullLiteral>] SerializeAddon =
    inherit Addon
    abstract serialize : unit -> string

/// The `SerializeAddon` class as the package exports it: `new SerializeAddon()`.
type [<AllowNullLiteral>] SerializeAddonClass =
    [<Emit("new ($0)()")>]
    abstract Create : unit -> SerializeAddon

// `@xterm/addon-serialize` is a NAMED import, and the difference cost a red release job.
// It ships BOTH `main` (CJS) and `module` (`lib/addon-serialize.mjs`, which exists and
// exports `SerializeAddon` by name and nothing by default). Node resolves the CJS and its
// interop invents a default, so `ImportDefault` worked here for as long as the emulator was
// Node-only; esbuild prefers `module` for the browser, where that default does not exist —
// "No matching export ... for import default", and only when bundling the real client
// entry, which no test tier short of `stage` does. The named export is present in both
// builds, so this is the one spelling that resolves everywhere.
[<ImportMember("@xterm/addon-serialize")>]
let SerializeAddon : SerializeAddonClass = jsNative
