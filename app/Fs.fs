module Yession.Host.Fs

// Shared synchronous file primitives for the Manager's durable stores (extracted from
// ManagerStore for Plan 06 so the secrets store gets the identical guarantee): writes
// are atomic — temp file + fsync + rename — so a crash mid-write leaves the previous
// content intact, never a half-written file.

open Fable.Core
open Node.Api
open Fable.NodeExtras

/// `fs.constants.X_OK` — the mode `accessSync` is asked about below. `Fable.Node` types
/// `accessSync` and not the constants it takes, and this is the only one this file wants.
[<Import("constants", "node:fs")>]
let private constants : {| X_OK : int |} = jsNative

let exists (path: string) : bool = fs.existsSync (U2.Case1 path)

/// Can THIS process execute that path? Not the same question as `exists` — a path can be
/// there and unrunnable — and it is the question a named tool has to answer before its
/// absence is blamed for anything.
let executable (path: string) : bool =
    try
        fs.accessSync (U2.Case1 path, constants.X_OK)
        true
    with _ -> false

/// Create a directory (and any missing parents); a no-op when it already exists.
let ensureDir (path: string) : unit = Files.mkdirp path

let readText (path: string) : string = fs.readFileSync (path, "utf8")

/// Move a path. Within one filesystem this is atomic, which is what lets a thing be built
/// out of sight and then APPEAR whole — the guarantee `writeTextAtomic` below leans on for
/// a file, and the repo manager's clone leans on for a directory.
let rename (from: string) (dest: string) : unit = fs.renameSync (from, dest)

let private directoryOf (path: string) : string =
    let idx = path.LastIndexOf '/'
    if idx > 0 then path.Substring (0, idx) else ""

/// Write + fsync the file before returning, so the rename in `writeTextAtomic` can never
/// expose un-flushed content. The descriptor is closed on the way out however the write
/// ended — a failed write that kept its descriptor would leak one per attempt for the life
/// of a Manager that keeps trying.
///
/// Not `finally`, which would let a close failure REPLACE the write failure that is already
/// unwinding: the write error is the one that says what went wrong, and a descriptor being
/// awkward to shut on the way out of a full disk is not news. So the close is attempted and
/// its own error dropped, and the original is re-raised untouched.
let private writeSynced (path: string) (text: string) : unit =
    let fd = Files.openTruncate path
    try
        Files.writeText fd text |> ignore
        Files.fsync fd
        fs.closeSync fd
    with _ ->
        (try fs.closeSync fd with _ -> ())
        reraise ()

/// Write atomically; durable before returning.
let writeTextAtomic (path: string) (text: string) : unit =
    let directory = directoryOf path
    if directory <> "" then Files.mkdirp directory
    let temp = path + ".tmp"
    writeSynced temp text
    rename temp path

/// A path as an ABSOLUTE one, resolved against the process's working directory.
///
/// The rule it exists for: a path that is stored, handed to another process, or used as
/// BOTH a working directory and an argument must not depend on where anybody happens to
/// stand. `git -C <p>` run with the cwd already set to `p`'s parent resolves `p` twice —
/// so a relative repos directory made every verb say `cannot change to ...: No such file
/// or directory` about a checkout that was sitting right there.
let absolute (target: string) : string = path.resolve target

/// The path the KERNEL will check, with every symlink on the way in resolved.
///
/// Two paths that name one file are not interchangeable to a sandbox: srt canonicalises an
/// allow-list entry, and the OS then denies reading the symlink NODES an access traverses —
/// macOS's escape hatch is `file-read-metadata` on DIRECTORIES, and `/etc`, `/tmp` and
/// `/run` are all symlinks there. So a grant written one way is a denial used the other, and
/// the difference is invisible until something fails far downstream.
///
/// `None` when the path does not resolve — a directory a tool has yet to create is ordinary,
/// and refusing it here would be a different rule wearing this one's name.
let canonical (path: string) : string option =
    try Some (fs.realpathSync (U2.Case1 path)) with _ -> None
