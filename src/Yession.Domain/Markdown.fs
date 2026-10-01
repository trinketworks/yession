namespace Yession.Domain.Collab

open Yession.Domain

open Fable.ProseMirror.Headless
open Yjs

/// Headless ProseMirror-markdown serialization for rich-text bodies. A body is a
/// `Y.XmlFragment` holding a ProseMirror document; this converts between that fragment and
/// Markdown with no DOM, so it is shared by the browser editor AND the Session Process drain
/// (which snapshots a queue body to Markdown for the durable `MessageSent`). The bindings are
/// `Fable.ProseMirror.Headless`, the same npm packages the editor uses — no authored JS.
module Markdown =

    /// Serialize a fragment's ProseMirror doc to Markdown (durable body / agent input).
    let ofFragment (fragment: Y.XmlFragment) : string =
        defaultMarkdownSerializer.serialize (fragmentToRootNode fragment schema)

    /// Parse Markdown into an (empty) fragment — seeds a queue body on send.
    let intoFragment (markdown: string) (fragment: Y.XmlFragment) : unit =
        rootNodeToFragment (defaultMarkdownParser.parse (if isNull (box markdown) then "" else markdown)) fragment

    /// Content-copy one fragment's document into another (draft -> queue on send). Shared
    /// types cannot be re-parented, so copy via the Markdown round-trip.
    let copy (src: Y.XmlFragment) (dst: Y.XmlFragment) : unit =
        intoFragment (ofFragment src) dst
