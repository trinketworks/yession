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

/// One node of the document's tree, opaque: what a reader wants of one is its `shape` and
/// where it starts, both below.
[<AllowNullLiteral>]
type Node =
    /// `[start, valueEnd, nodeEnd]` as offsets into the text; absent on a node the parser
    /// made rather than read.
    abstract range : int array option

/// A line and column, 1-based, as `LineCounter` reports one.
[<AllowNullLiteral>]
type LinePos =
    abstract line : int
    abstract col : int

/// What turns an offset into a line: handed to the parser, which records every newline it
/// passes, and asked afterwards.
[<AllowNullLiteral>]
type LineCounter =
    abstract linePos : offset: int -> LinePos

[<AllowNullLiteral>]
type LineCounterClass =
    [<EmitConstructor>]
    abstract Create : unit -> LineCounter

[<Import("LineCounter", "yaml")>]
let private lineCounterClass : LineCounterClass = jsNative

let lineCounter () : LineCounter = lineCounterClass.Create ()

/// A parsed document. `parseDocument` rather than `parse` is the whole reason this exists:
/// `parse` resolves what it can and DISCARDS its warnings, so a tag the schema does not
/// define comes back as an ordinary value; a document keeps the complaints.
[<AllowNullLiteral>]
type Document =
    abstract errors : Problem array
    abstract warnings : Problem array
    /// The document as plain JavaScript values — what anything wanting JSON out of it takes.
    abstract toJS : unit -> Plain
    /// The root node, absent for an empty document.
    abstract contents : Node option

/// What a node is, as far as a reader walking the tree needs: a mapping's entries (key text,
/// the key's node, the value's node when there is one), a sequence's items, or anything else.
[<RequireQualifiedAccess>]
type Shape =
    | Mapping of (string * Node * Node option) array
    | Sequence of Node array
    | Leaf

[<AllowNullLiteral>]
type private Pair =
    abstract key : Node
    abstract value : Node option

[<AllowNullLiteral>]
type private Collection<'item> =
    abstract items : 'item array

[<Import("isMap", "yaml")>]
let private isMap (node: Node) : bool = jsNative

[<Import("isSeq", "yaml")>]
let private isSeq (node: Node) : bool = jsNative

[<Import("isScalar", "yaml")>]
let private isScalar (node: Node) : bool = jsNative

/// A node the predicate beside the call just classified, as that class. Private and only ever
/// behind its predicate, which is what makes the assertion true rather than hoped.
[<Emit("$0")>]
let private classified<'shape> (node: Node) : 'shape = jsNative

/// A scalar key as the text a mapping key reads as.
[<Emit("String($0.value)")>]
let private scalarText (node: Node) : string = jsNative

let shape (node: Node) : Shape =
    if isMap node then
        (classified<Collection<Pair>> node).items
        |> Array.choose (fun pair ->
            if not (isNull pair.key) && isScalar pair.key then Some (scalarText pair.key, pair.key, pair.value)
            else None)
        |> Shape.Mapping
    elif isSeq node then Shape.Sequence (classified<Collection<Node>> node).items
    else Shape.Leaf

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
      maxAliasCount : int
      /// Where the parser records newlines, so a node's offset can be put back as a line.
      lineCounter : LineCounter }

[<Import("parseDocument", "yaml")>]
let parseDocument (text: string) (options: ParseOptions) : Document = jsNative

/// `parse`: the resolved value and nothing about what was objected to. For a question about
/// what a committed file SAYS, where a refusal is not the point.
[<Import("parse", "yaml")>]
let parse (text: string) : Plain = jsNative
