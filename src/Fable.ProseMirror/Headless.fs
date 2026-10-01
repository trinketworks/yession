namespace Fable.ProseMirror

open Fable.Core
open Yjs

/// The DOM-free half of the ProseMirror surface: the `prosemirror-markdown` schema, its
/// default parser and serializer, and the `y-prosemirror` conversions between a Yjs
/// `XmlFragment` and a ProseMirror document. Nothing here needs a page, so the Session Process
/// drain can serialize a body with exactly the vocabulary the browser editor writes it in.
///
/// Its own file, apart from `ProseMirror`, because a module compiles to one JavaScript file and
/// whoever names one of its values loads all of it. `ProseMirror` constructs things at load —
/// the composer's parser is the default one with `file:///` links admitted, patched in place,
/// and the timeline's table parser is built beside it — so reaching it from the Session Process
/// would change what that process parses. This module only imports, and every value in it is
/// the library's own, untouched.
module Headless =

    /// A ProseMirror document node, passed around whole; `ProseMirror` types the members the
    /// renderer reads off one.
    type Node = obj
    type Schema = obj

    type [<AllowNullLiteral>] MarkdownParser =
        abstract parse : string -> Node

    type [<AllowNullLiteral>] MarkdownSerializer =
        abstract serialize : Node -> string

    /// The prosemirror-markdown schema — the single source of the node/mark vocabulary the
    /// editor and the serializer share.
    [<Import("schema", "prosemirror-markdown")>]
    let schema : Schema = jsNative

    [<Import("defaultMarkdownParser", "prosemirror-markdown")>]
    let defaultMarkdownParser : MarkdownParser = jsNative

    [<Import("defaultMarkdownSerializer", "prosemirror-markdown")>]
    let defaultMarkdownSerializer : MarkdownSerializer = jsNative

    /// The document a fragment holds, as a ProseMirror node under `schema`.
    [<Import("yXmlFragmentToProseMirrorRootNode", "y-prosemirror")>]
    let fragmentToRootNode (fragment: Y.XmlFragment) (schema: Schema) : Node = jsNative

    /// Write a ProseMirror document into a fragment.
    [<Import("prosemirrorToYXmlFragment", "y-prosemirror")>]
    let rootNodeToFragment (node: Node) (fragment: Y.XmlFragment) : unit = jsNative
