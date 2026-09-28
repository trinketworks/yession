module Fable.Yaml

// Fable bindings to the `yaml` npm package. The binding layer only, mirroring how
// `Fable.Jose` wraps jose: it declares the slice of yaml's surface this repository reads
// through and nothing more — a document parsed with the parser's own options, the
// complaints it kept, and the plain-JavaScript tree it hands over.
//
// Fable-only: `dotnet build` type-checks it; Fable emits the JS that runs on Node.

open Fable.Core

/// A parsed tree as plain JavaScript values — the objects, arrays, strings, numbers, booleans
/// and nulls YAML's core schema resolves to, which is to say JSON's. Opaque: what anything here
/// wants from it is a DECODE, and the decoders this repository keeps read JSON text on both
/// runtimes, so `Plain.json` is the way out of it.
type Plain =
    interface end

[<RequireQualifiedAccess>]
module Plain =

    /// The tree as JSON text, for a decoder to read.
    let json (value: Plain) : string = JS.JSON.stringify value

/// One thing the parser objected to, as yaml reports it (a `YAMLError`).
[<AllowNullLiteral>]
type Problem =
    abstract message : string

/// A parsed document. `parseDocument` rather than `parse` is the whole reason this exists:
/// `parse` resolves what it can and DISCARDS its warnings, so a tag the schema does not
/// define comes back as an ordinary value; a document keeps the complaints.
[<AllowNullLiteral>]
type Document =
    abstract errors : Problem array
    abstract warnings : Problem array
    /// The document as plain JavaScript values — what anything wanting JSON out of it takes.
    abstract toJS : unit -> Plain

/// How a parser is constructed. yaml reads these off a plain options object, which is what a
/// Fable record is once compiled; the names are yaml's own.
[<RequireQualifiedAccess>]
type ParseOptions =
    { /// Which tags resolve: `core` is YAML's JSON-compatible schema and nothing more.
      schema : string
      /// A repeated key is an error rather than a silent last-wins fold.
      uniqueKeys : bool
      /// A bound on alias expansion, so an anchor referring to itself cannot grow a small file
      /// into an unbounded tree.
      maxAliasCount : int }

[<Import("parseDocument", "yaml")>]
let parseDocument (text: string) (options: ParseOptions) : Document = jsNative

/// `parse`: the resolved value and nothing about what was objected to. For a question about
/// what a committed file SAYS, where a refusal is not the point.
[<Import("parse", "yaml")>]
let parse (text: string) : Plain = jsNative
