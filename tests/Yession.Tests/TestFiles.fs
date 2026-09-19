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

/// Create a directory and any missing parents; a no-op when it is already there.
let ensureDir (path: string) : unit = Files.mkdirp path

/// Write text, creating the file or replacing what was in it.
let write (path: string) (text: string) : unit = fs.writeFileSync (path, box text)

/// Add text to the end of a file, creating it when it is not there.
let append (path: string) (text: string) : unit = fs.appendFileSync (path, box text)

/// Read a whole file as text.
let read (path: string) : string = fs.readFileSync (path, "utf8")

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
