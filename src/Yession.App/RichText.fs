namespace Yession.App

open Fable.Core
open Lit
open Fable.ProseMirror.ProseMirror
open Yession.Domain.Content

/// Read-only rendering of a Markdown body to formatted rich text for the conversation
/// timeline — the mirror of the composer's live formatting, so the timeline shows exactly
/// what the editor produced. It reuses the *same* `prosemirror-markdown` parser the editor's
/// paste path and the drain serializer use (`ProseMirror.mdParser`), then walks the parsed
/// document into Lit templates. That document is pure data (no DOM), so the walk runs
/// identically under Node (SSR) and in the browser, and every text value flows through a
/// Lit text/attribute hole — so it is escaped, never injected.
module RichText =

    /// A link into the session's own content root, if that is what an href is: a `file:///`
    /// URL naming a path something here SERVES. Today that is `artifacts/…`; `repos/…` parses
    /// the same way and joins the day a handler answers for it, which is the whole reason the
    /// reference is a content path rather than an artifact one.
    ///
    /// A `file:` URL that is neither — a link to somebody's disk — is not a place this page can
    /// send a reader, so it is refused here rather than handed to the browser.
    let private contentLink (href: string) : ContentRef option =
        if not (href.StartsWith ContentRef.urlPrefix) then None
        else
            match ContentRef.create href with
            | Ok ref when ContentRef.root ref = ArtifactRef.root -> Some ref
            | Ok _
            | Error _ -> None

    // Marks wrap a text run from the innermost outward. prosemirror-markdown's default schema
    // carries em / strong / code / link; an unknown mark passes its content through unwrapped.
    // `chip` is how a reference into this session is drawn — the timeline's entity chip, passed
    // in rather than built here, so a file an agent links to and a file a fold names look the
    // same without this module knowing what a chip is.
    let private wrapMark (chip: ContentRef -> TemplateResult) (mark: obj) (inner: TemplateResult) : TemplateResult =
        match markTypeName mark with
        | "strong" -> html $"""<strong class="{Style.proseStrong}">{inner}</strong>"""
        | "em" -> html $"""<em>{inner}</em>"""
        | "code" -> html $"""<code class="{Style.proseCode}">{inner}</code>"""
        | "link" ->
            match markHref mark |> Option.map (fun href -> href, contentLink href) with
            // A reference to something here draws as the chip, and its own name is what the
            // chip says: the link text an agent wrote around it ("this chart") is the sentence's
            // words, not the file's, and the chip has to be recognisable as the same thing the
            // fold below it names.
            | Some (_, Some ref) -> chip ref
            | Some (href, None) when href.StartsWith ContentRef.urlPrefix -> inner
            | Some (href, None) ->
                html $"""<a class="{Style.proseLink}" href="{href}" target="_blank" rel="noopener noreferrer">{inner}</a>"""
            // A link naming no target is not one a person can follow — its words still read,
            // where `href=""` used to point them at the page they were already on.
            | None -> inner
        | "s" | "strike" | "strikethrough" | "del" -> html $"""<s>{inner}</s>"""
        | _ -> inner

    /// One inline node — a text run under its marks, or a hard break.
    let private inlineNode (chip: ContentRef -> TemplateResult) (node: Node) : TemplateResult =
        if nodeIsText node then
            Array.foldBack (wrapMark chip) (nodeMarks node) (html $"{nodeText node}")
        elif nodeTypeName node = "hard_break" then html $"<br>"
        else html $"{nodeTextContent node}"

    let private children (node: Node) : Node list =
        [ for i in 0 .. nodeChildCount node - 1 -> nodeChild node i ]

    let private inlineContent (chip: ContentRef -> TemplateResult) (node: Node) : TemplateResult list =
        children node |> List.map (inlineNode chip)

    /// A cell's alignment as the utility that draws it — `""` for a plain column, so the class
    /// list gains nothing and the cell falls back to `proseTable`'s own `text-left`.
    let private cellAlignClass (node: Node) : string =
        match (nodeAttrs node).align with
        | Some "right" -> Style.proseTableAlignRight
        | Some "center" -> Style.proseTableAlignCenter
        | Some "left" -> Style.proseTableAlignLeft
        | _ -> ""

    /// One block node. Recurses for nested structure (lists, list items, blockquotes, tables).
    let rec private block (chip: ContentRef -> TemplateResult) (node: Node) : TemplateResult =
        let inlineContent node = inlineContent chip node
        let blocks node = blocks chip node
        match nodeTypeName node with
        | "paragraph" -> html $"""<p class="{Style.proseP}">{inlineContent node}</p>"""
        | "heading" ->
            match headingLevel node with
            | 1 -> html $"""<h1 class="{Style.proseH1}">{inlineContent node}</h1>"""
            | 2 -> html $"""<h2 class="{Style.proseH2}">{inlineContent node}</h2>"""
            | 3 -> html $"""<h3 class="{Style.proseH3}">{inlineContent node}</h3>"""
            | _ -> html $"""<h4 class="{Style.proseH4}">{inlineContent node}</h4>"""
        | "blockquote" -> html $"""<blockquote class="{Style.proseQuote}">{blocks node}</blockquote>"""
        | "code_block" -> html $"""<pre class="{Style.prosePre}"><code>{nodeTextContent node}</code></pre>"""
        | "bullet_list" -> html $"""<ul class="{Style.proseUl}">{blocks node}</ul>"""
        | "ordered_list" -> html $"""<ol class="{Style.proseOl}" start="{listStart node}">{blocks node}</ol>"""
        | "list_item" -> html $"""<li class="{Style.proseLi}">{blocks node}</li>"""
        | "horizontal_rule" -> html $"""<hr class="{Style.proseHr}">"""
        // A wrapper carries the horizontal scroll (WCAG 1.4.10 exempts two-dimensional
        // content like a table from reflow) so the `<table>` itself stays a table, not a
        // block — a real reader still gets header/data cell semantics, only wrapped by a div
        // that never appears in the accessibility tree.
        | "table" -> html $"""<div class="{Style.proseTableWrap}"><table class="{Style.proseTable}">{blocks node}</table></div>"""
        | "table_row" -> html $"""<tr>{blocks node}</tr>"""
        // `scope="col"` because a GFM table's only header row names its COLUMNS — there is no
        // row-header syntax to read a different scope off.
        | "table_header" ->
            html $"""<th scope="col" class="{Style.cls [ Style.proseTableHeaderCell; cellAlignClass node ]}">{inlineContent node}</th>"""
        | "table_cell" ->
            html $"""<td class="{Style.cls [ Style.proseTableCell; cellAlignClass node ]}">{inlineContent node}</td>"""
        // Any node the default schema adds later still shows its text rather than vanishing.
        | _ -> html $"""<p class="{Style.proseP}">{inlineContent node}</p>"""

    and private blocks (chip: ContentRef -> TemplateResult) (node: Node) : TemplateResult list =
        children node |> List.map (block chip)

    /// Render a Markdown body as read-only formatted rich text. A parse failure (or an absent
    /// body) degrades to the raw text so a message can never silently vanish from the timeline.
    ///
    /// `chip` draws a reference into this session's content root — the one thing in a body that
    /// is not text or a link out. The caller supplies it because the caller is the surface that
    /// knows what a reference looks like there and what opening one does.
    ///
    /// Parses with `tableMdParser`, not the composer's own `mdParser`: the timeline only reads
    /// Markdown, so it is the one surface that can carry GFM tables without teaching the
    /// composer how to edit one (`ProseMirror.tableMdParser`'s own comment has the reasoning).
    let render (chip: ContentRef -> TemplateResult) (markdown: string) : TemplateResult =
        let md = if isNull (box markdown) then "" else markdown
        let doc = tableMdParser.parse md
        if isNull (box doc) then html $"{md}" else html $"{blocks chip doc}"
