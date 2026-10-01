module Yession.Host.YamlSource

// The PARSE stage of reading a YAML file this repository owns the schema of (`yession.yaml`,
// the operator's resources profile): text in, the two things the later stages read out — the
// tree as JSON for a decoder (`ConfigFile.decoder`, `OperatorProfile`), and where each key the
// file wrote sits in its text for an analyzer's findings (`ConfigAnalysis`). Pure in effect:
// no file is read here, and the parser holds no state between calls.
//
// One place, because the parser's construction is a set of decisions (below) that both files
// need identically, and two copies were two chances for one of them to drift.

open Fable.Yaml
open Yession.Domain.Sandboxes

/// What the later stages read.
type Parsed =
    { /// The tree as JSON text, for a decoder.
      Json : string
      /// Where each key and sequence item the file wrote sits.
      Index : SourceIndex }

/// Everything the parser objected to, as messages.
///
/// `parse` would not do: it RESOLVES what it can and reports the rest as warnings it then
/// discards, so a file carrying a tag the schema does not define comes back as an ordinary
/// value and decodes as though the tag had never been written. `parseDocument` keeps the
/// complaints, which is what lets an unrecognised tag be a refusal rather than a silent
/// downgrade.
let private complaints (doc: Document) : string array =
    Array.append doc.errors doc.warnings |> Array.map (fun problem -> problem.message)

/// How the parser is constructed, and every field is load-bearing.
///
/// `schema: "core"` is YAML's own JSON-compatible schema and nothing more, so the only tags
/// it resolves are the ones JSON could have expressed; anything else becomes a complaint
/// above. `uniqueKeys` turns a repeated key into an error rather than a silent last-wins fold
/// — which is what makes the domains' "declared twice" refusals reachable from a real file,
/// since JSON object semantics would have folded the duplicate before the decoder saw it.
/// `maxAliasCount` bounds alias expansion, so an anchor referring to itself cannot turn a
/// small file into an unbounded tree. A fresh line counter per parse, because it records the
/// newlines of the ONE text it was handed.
///
/// Anchors themselves are deliberately allowed: `&base` / `*base` resolve before the decoder
/// sees anything, so reuse inside a file costs the schema nothing.
let private options () : ParseOptions =
    { ParseOptions.schema = "core"
      uniqueKeys = true
      maxAliasCount = 100
      lineCounter = lineCounter () }

/// Where every key and sequence item under `node` was written, by its path. An alias's
/// target is not walked through the alias — only what the file wrote has a position.
let private indexOf (lines: LineCounter) (root: Node option) : SourceIndex =
    let at (node: Node) : SourceSpan option =
        node.range
        |> Option.bind Array.tryHead
        |> Option.map (fun offset ->
            let position = lines.linePos offset
            { SourceSpan.Line = position.line; SourceSpan.Column = position.col })
    let rec walk (path: KeyPath) (node: Node) (index: SourceIndex) : SourceIndex =
        match shape node with
        | Shape.Mapping entries ->
            entries
            |> Array.fold
                (fun index (key, keyNode, value) ->
                    let here = path @ [ KeyStep.Key key ]
                    let index = match at keyNode with Some span -> Map.add here span index | None -> index
                    match value with
                    | Some value -> walk here value index
                    | None -> index)
                index
        | Shape.Sequence items ->
            items
            |> Array.indexed
            |> Array.fold
                (fun index (position, item) ->
                    let here = path @ [ KeyStep.Index position ]
                    let index = match at item with Some span -> Map.add here span index | None -> index
                    walk here item index)
                index
        | Shape.Leaf -> index
    match root with
    | Some node -> walk [] node Map.empty
    | None -> Map.empty

/// Parse `text`, or say the first thing the parser objected to.
let parse (text: string) : Result<Parsed, string> =
    try
        let options = options ()
        let doc = parseDocument text options
        match complaints doc with
        | [||] -> Ok { Json = Plain.json (doc.toJS ()); Index = indexOf options.lineCounter doc.contents }
        | problems -> Error problems.[0]
    with e -> Error e.Message
