module Yession.Tests.TestFiles

// The filesystem, as a test fixture drives it.
//
// Eighteen suites needed the same half-dozen calls to stand a fixture up — a temp directory
// to work in, a tree made, a file written, one read back, the lot removed — and each had
// imported `node:fs` itself and written its own macros for them. `mkdirSync` was declared
// eleven times across `tests/`, `writeFileSync` eight, `readFileSync` seven, `mkdtempSync`
// six; no two spellings were quite the same binding, and none of them was reachable from
// the suite next door.
//
// What is below is F# over `Node.Api`'s typed `fs` and `Fable.NodeExtras`' `Files`, which is
// where the members `Fable.Node` does not type are declared. Nothing here is a macro.
//
// Its own file rather than part of `Support`, for the reason `TestHttp` is: `Secrets.fs` and
// `Assets.fs` compile BEFORE `Support.fs` and could not reach it there.

open Fable.Core
open Node.Api
open Fable.NodeExtras

/// A fresh directory under the OS temp directory, named after `prefix`. Every suite that
/// writes to disk starts here: `mkdtempSync` appends six random characters, so two runs of
/// one suite — and two suites in one process — never share a tree.
let tempDir (prefix: string) : string = fs.mkdtempSync (os.tmpdir () + "/" + prefix)

/// A fresh directory under `prefix`, which is a path rather than a name: the suites that
/// need one under `$HOME` rather than the OS temp directory do so because a Colima daemon
/// shares only `$HOME` into its VM, and a bind source outside it mounts empty.
let tempDirAt (prefix: string) : string = fs.mkdtempSync prefix

/// This account's home directory — where the fixtures above put their trees.
let homeDir () : string = os.homedir ()

/// Create a directory and any missing parents; a no-op when it is already there.
let ensureDir (path: string) : unit = Files.mkdirp path

/// Create ONE directory, failing when its parent is not there. The stricter of the two, for
/// a fixture whose point is the tree it built a moment ago.
let makeDir (path: string) : unit = fs.mkdirSync path

/// Copy a file, overwriting the destination.
let copyFile (source: string) (destination: string) : unit = Files.copyFile source destination

/// A path with every symlink on the way in resolved — the path the KERNEL checks, which is
/// not the same path a sandbox grant was written with.
let canonical (path: string) : string = fs.realpathSync (U2.Case1 path)

/// Make a file executable (0o755) — a fixture script the code under test then runs.
let makeExecutable (path: string) : unit = fs.chmodSync (U2.Case1 path, 0o755)

/// World-writable (0o777). One fixture is, on purpose: a mount-mode test has to assert
/// rw-vs-ro MOUNT semantics and nothing about the capabilities its container happens to
/// keep, so the mode is taken out of the question.
let makeWorldWritable (path: string) : unit = fs.chmodSync (U2.Case1 path, 0o777)

/// Set a path's mode. A fixture handed to a container needs one a container's user can read.
let chmod (path: string) (mode: int) : unit = fs.chmodSync (U2.Case1 path, mode)

/// Write text, creating the file or replacing what was in it.
let write (path: string) (text: string) : unit = fs.writeFileSync (path, box text)

/// Add text to the end of a file, creating it when it is not there.
let append (path: string) (text: string) : unit = fs.appendFileSync (path, box text)

/// Read a whole file as text.
let read (path: string) : string = fs.readFileSync (path, "utf8")

/// A file's bytes as base64 — the shape a binary constant in the product carries.
let readBase64 (path: string) : string = fs.readFileSync (path, "base64")

/// Is there anything at that path?
let exists (path: string) : bool = fs.existsSync (U2.Case1 path)

/// Remove a path and everything under it, saying nothing about one that was not there —
/// which is what a fixture's teardown wants, since it runs after a failure too.
let removeTree (path: string) : unit = Files.removeTree path

/// A symbolic link at `link` pointing at `target`. Two suites make one on purpose: what a
/// sandbox grant does with a path that is a link to somewhere else is a rule of its own.
let symlink (target: string) (link: string) : unit = fs.symlinkSync (U2.Case1 target, U2.Case1 link)

/// The names directly inside a directory.
let entries (path: string) : string list = List.ofSeq (fs.readdirSync (U2.Case1 path))
