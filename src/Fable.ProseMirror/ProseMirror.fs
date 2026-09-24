namespace Fable.ProseMirror

open Fable.Core
open Fable.Core.JsInterop
open Fable.BrowserExtras
open Yjs

/// Fable bindings for the focused ProseMirror + y-prosemirror surface the rich-text editor
/// uses. Pure `[<Import>]`/`[<Emit>]` interop — the platform boundary, not authored JS (repo
/// invariant, master #7). Hand-written for the small used surface rather than full ts2fable
/// generation (the `Fable.Yjs` precedent, scaled down). Opaque PM values we only pass around
/// are `obj`; only the members actually called are typed.
///
/// Its own project, beside `Fable.Yjs` and `Fable.BrowserExtras` rather than inside the app
/// that uses it, because that is what it IS: bindings to somebody else's library. It lived in
/// `Yession.App` only because that is where the editor was written, and a reader of that
/// project met forty-four macros before reaching a line of this repository's own code.
///
/// TipTap was rejected here: its value is JS-side ergonomics that F# does not collect, it drags
/// a heavier dependency tree, and its own mount lifecycle would be a second thing fighting Lit
/// for the editor's DOM. Raw ProseMirror is the smaller surface and the one `y-prosemirror` is
/// written against.
module ProseMirror =

    type Node = obj
    type Plugin = obj
    type Schema = obj
    type NodeType = obj
    type MarkType = obj
    type Command = obj
    type InputRule = obj

    /// A ProseMirror transaction. Members are chainable (each returns the mutated `this`).
    type [<AllowNullLiteral>] Transaction =
        abstract delete : int * int -> Transaction
        abstract addMark : int * int * obj -> Transaction
        abstract removeStoredMark : obj -> Transaction
        abstract replaceSelectionWith : Node * bool -> Transaction

    type [<AllowNullLiteral>] EditorState =
        abstract tr : Transaction

    type [<AllowNullLiteral>] EditorView =
        abstract state : EditorState
        abstract dispatch : Transaction -> unit
        abstract destroy : unit -> unit

    // --- prosemirror-markdown: the schema + parser/serializer (markdown round-trip) --------

    type [<AllowNullLiteral>] MarkdownParser =
        abstract parse : string -> Node

    [<Import("schema", "prosemirror-markdown")>]
    let schema : Schema = jsNative

    [<Import("defaultMarkdownParser", "prosemirror-markdown")>]
    let private defaultMdParser : MarkdownParser = jsNative

    /// markdown-it refuses a link by SCHEME before anything sees it: `javascript:`,
    /// `vbscript:`, `data:` and `file:` never become links at all — they stay literal text.
    /// Three of those are refused because a page that followed one would run somebody else's
    /// script. `file:` is refused because on the open web it names the reader's own disk.
    ///
    /// Here it does not. `file:///` is how this product spells a reference to something the
    /// SESSION holds, and a renderer decides what one means: a path it recognises draws as a
    /// reference, and a path it does not is refused there and shown as words (`RichText`). So
    /// the scheme is admitted to the parse and nothing else is — the other three stay refused,
    /// and no `file:` URL becomes an href to a disk on the way through.
    ///
    /// Applied to the parser rather than at a call site because every parse wants the same
    /// answer: the composer that accepts a pasted body and the timeline that renders one have
    /// to agree, or a link survives being typed and vanishes being read.
    [<Emit("(function (p) { const inner = p.tokenizer.validateLink.bind(p.tokenizer); p.tokenizer.validateLink = url => inner(url) || /^file:\\/\\/\\//i.test(url.trim()); return p })($0)")>]
    let private admittingContentLinks (parser: MarkdownParser) : MarkdownParser = jsNative

    let mdParser : MarkdownParser = admittingContentLinks defaultMdParser

    /// `schema.nodes[name]` / `schema.marks[name]`: the type under that name, and nothing when
    /// the schema declares none — which the option says, where a JS truthiness test used to be
    /// asked of the result afterwards.
    [<Emit("$0.nodes[$1]")>]
    let nodeType (s: Schema) (name: string) : NodeType option = jsNative
    [<Emit("$0.marks[$1]")>]
    let markType (s: Schema) (name: string) : MarkType option = jsNative
    [<Emit("$0.create()")>]
    let markCreate (m: MarkType) : obj = jsNative
    /// A fresh JS RegExp from a pattern string (input-rule triggers).
    [<Emit("new RegExp($0)")>]
    let regex (pattern: string) : obj = jsNative

    // --- Structural read of a parsed document (read-only markdown rendering) ----------------
    // A `Node` parsed by `mdParser` is pure data — no DOM — so this walk runs identically on
    // Node (SSR) and in the browser. Only the members the timeline renderer reads are typed.

    /// The node's type name (`paragraph`, `heading`, `bullet_list`, `text`, …).
    [<Emit("$0.type.name")>]
    let nodeTypeName (node: Node) : string = jsNative
    [<Emit("$0.childCount")>]
    let nodeChildCount (node: Node) : int = jsNative
    [<Emit("$0.child($1)")>]
    let nodeChild (node: Node) (index: int) : Node = jsNative
    [<Emit("$0.isText")>]
    let nodeIsText (node: Node) : bool = jsNative
    /// A text node's literal text (empty for non-text nodes).
    [<Emit("$0.text || ''")>]
    let nodeText (node: Node) : string = jsNative
    /// The concatenated text of a node's descendants (code blocks, fallbacks).
    [<Emit("$0.textContent")>]
    let nodeTextContent (node: Node) : string = jsNative
    /// A text node's marks (`em`/`strong`/`code`/`link`), innermost-last.
    [<Emit("$0.marks")>]
    let nodeMarks (node: Node) : obj[] = jsNative
    /// The attributes the markdown schema puts on a node — a heading's `level`, an ordered
    /// list's `order`, a table cell's `align` — each an option because a node of another type
    /// carries neither.
    [<AllowNullLiteral>]
    type NodeAttrs =
        abstract level : int option
        abstract order : int option
        abstract align : string option

    /// A mark's attributes: a link's `href`, and nothing on any other mark.
    [<AllowNullLiteral>]
    type MarkAttrs =
        abstract href : string option

    /// A node's attributes as they stand, undefaulted. The two readers below answer what a
    /// RENDERER wants — a level, a start number, each with the schema's fallback already
    /// folded in — and that is the wrong question for a predicate, which has to be able to
    /// tell an absent attribute from a present one that happens to equal the default.
    [<Emit("$0.attrs")>]
    let nodeAttrs (node: Node) : NodeAttrs = jsNative
    [<Emit("$0.attrs")>]
    let private markAttrs (mark: obj) : MarkAttrs = jsNative

    /// A heading's level (1–6), defaulting to 1. This used to be `level || 1`, and the one
    /// case that operator folds in is kept on purpose: a heading whose level is 0 is not a
    /// heading at level 0, and never was.
    let headingLevel (node: Node) : int =
        match (nodeAttrs node).level with
        | Some level when level > 0 -> level
        | _ -> 1
    /// An ordered list's first number, defaulting to 1 — `order || 1`, spelled out the same way.
    let listStart (node: Node) : int =
        match (nodeAttrs node).order with
        | Some order when order > 0 -> order
        | _ -> 1

    /// The WRITE half of those two readers: the attribute object handed back for a node about
    /// to be created, which is what an input rule's `getAttrs` returns. It sits beside them
    /// because it is the same story — a heading wears a level and an ordered list a start
    /// number — and a constructor that drifted from its reader would be caught by neither.
    ///
    /// Each carries only the attribute its own node type declares. ProseMirror ignores a key
    /// the type never asked for, so a shared `{ level, order }` would build and would say, of
    /// every heading, that it is also a list starting somewhere.
    [<RequireQualifiedAccess>]
    module NodeAttrs =

        let heading (level: int) : NodeAttrs = unbox (createObj [ "level" ==> level ])
        let orderedList (order: int) : NodeAttrs = unbox (createObj [ "order" ==> order ])
        let tableCell (align: string option) : NodeAttrs =
            unbox (createObj [ "align" ==> (align |> Option.map box |> Option.defaultValue null) ])

    [<Emit("$0.type.name")>]
    let markTypeName (mark: obj) : string = jsNative
    /// A link mark's target, and nothing for a mark that is not a link.
    let markHref (mark: obj) : string option = (markAttrs mark).href

    // --- GFM tables (read-only): the timeline's OWN schema + parser, never the editor's -----
    // `mdParser`/`schema` above are what the composer edits with, and their commonmark
    // tokenizer excludes GFM's `table` rule on purpose: editing a table needs
    // `prosemirror-tables`' cell-selection machinery, which nothing here has. The timeline only
    // READS Markdown (`RichText.render`), so it gets its own schema — the same nodes/marks,
    // with `table`/`table_row`/`table_header`/`table_cell` appended — and its own tokenizer
    // (commonmark plus `table` re-enabled), never sharing either with the composer: a body
    // pasted or typed there still cannot hold a table, because nothing there knows how to edit
    // one, while a table an agent writes still renders in the timeline that only reads it.

    [<Import("Schema", "prosemirror-model")>]
    let private schemaClass : obj = jsNative

    /// `base.spec.nodes.append({...})`: prosemirror-model's own way to extend a schema's node
    /// set without re-declaring the nodes it already has.
    [<Emit("(function (Cls, b) { return new Cls({ nodes: b.spec.nodes.append({ \
        table: { content: 'table_row+', group: 'block', isolating: true }, \
        table_row: { content: '(table_header | table_cell)+' }, \
        table_header: { content: 'inline*', attrs: { align: { default: null } } }, \
        table_cell: { content: 'inline*', attrs: { align: { default: null } } } \
    }), marks: b.spec.marks }) })($0, $1)")>]
    let private appendTableNodes (cls: obj) (baseSchema: Schema) : Schema = jsNative

    let private tableSchema : Schema = appendTableNodes schemaClass schema

    /// `markdown-it`'s `exports` map points both Node and esbuild at the same `index.mjs`,
    /// which has a real `export default` — unlike `@xterm/headless` (`Fable.Xterm`), where the
    /// two platforms resolve different files and `ImportDefault` only works because one of them
    /// is a CJS interop default. No such caveat here.
    [<ImportDefault("markdown-it")>]
    let private markdownItClass : obj = jsNative

    /// The same `file:///` admission `admittingContentLinks` patches onto `mdParser`'s
    /// tokenizer above, applied here to a tokenizer directly — a fresh instance has none of it,
    /// and without it a `file:///artifacts/…` reference inside a table cell would fail
    /// validation and read as plain text instead of the chip `RichText` draws it as elsewhere.
    [<Emit("(function (t) { const inner = t.validateLink.bind(t); t.validateLink = url => inner(url) || /^file:\\/\\/\\//i.test(url.trim()); return t })($0)")>]
    let private admittingContentLinksOnTokenizer (tokenizer: obj) : obj = jsNative

    /// A FRESH tokenizer, never `mdParser`'s own: enabling `table` on the shared one would
    /// hand the composer's paste path a `table_open` token its schema has no node for.
    [<Emit("new $0('commonmark', { html: false }).enable('table')")>]
    let private newTableTokenizerRaw (cls: obj) : obj = jsNative

    let private newTableTokenizer (cls: obj) : obj = admittingContentLinksOnTokenizer (newTableTokenizerRaw cls)

    [<Emit("$0.tokens")>]
    let private parserTokens (p: MarkdownParser) : obj = jsNative

    [<Emit("Object.assign({}, $0, $1)")>]
    let private mergedTokens (baseTokens: obj) (extra: obj) : obj = jsNative

    /// The raw `style` markdown-it's table rule puts on an aligned column's `th`/`td` tokens
    /// (`text-align:left|center|right`), or `""` for a plain `---` column.
    [<Emit("$0.attrGet('style') || ''")>]
    let private cellStyleAttr (tok: obj) : string = jsNative

    /// A cell's alignment, read off that style — decided here in F# rather than in the token
    /// config below, which stays wiring: assembling somebody else's library, not reading one.
    let private cellAlign (tok: obj) : string option =
        let style = cellStyleAttr tok
        if style.Contains "right" then Some "right"
        elif style.Contains "center" then Some "center"
        elif style.Contains "left" then Some "left"
        else None

    let private cellAttrs : System.Func<obj, obj[], int, NodeAttrs> =
        System.Func<obj, obj[], int, NodeAttrs>(fun tok _ _ -> NodeAttrs.tableCell (cellAlign tok))

    [<Import("MarkdownParser", "prosemirror-markdown")>]
    let private markdownParserClass : obj = jsNative

    [<Emit("new $0($1, $2, $3)")>]
    let private newMarkdownParser (cls: obj) (schema: Schema) (tokenizer: obj) (tokens: obj) : MarkdownParser = jsNative

    /// `thead`/`tbody` are pure grouping GFM adds around the header/body rows — the schema
    /// above has no node for either, so both tokens are ignored and their rows land straight
    /// in the table's own content, matching `table: "table_row+"`.
    let private tableTokens : obj =
        mergedTokens (parserTokens mdParser) (createObj [
            "table" ==> createObj [ "block" ==> "table" ]
            "thead" ==> createObj [ "ignore" ==> true ]
            "tbody" ==> createObj [ "ignore" ==> true ]
            "tr" ==> createObj [ "block" ==> "table_row" ]
            "th" ==> createObj [ "block" ==> "table_header"; "getAttrs" ==> cellAttrs ]
            "td" ==> createObj [ "block" ==> "table_cell"; "getAttrs" ==> cellAttrs ]
        ])

    /// The timeline's parser: the same Markdown dialect `mdParser` reads, extended with GFM
    /// tables.
    let tableMdParser : MarkdownParser = newMarkdownParser markdownParserClass tableSchema (newTableTokenizer markdownItClass) tableTokens

    // --- prosemirror-state / -view ---------------------------------------------------------

    [<Import("EditorState", "prosemirror-state")>]
    let private editorStateClass : obj = jsNative
    [<Emit("$0.create($1)")>]
    let private stateCreate (cls: obj) (config: obj) : EditorState = jsNative
    let createState (config: obj) : EditorState = stateCreate editorStateClass config

    [<Import("EditorView", "prosemirror-view")>]
    let private editorViewClass : obj = jsNative
    [<Emit("new ($0)($1, $2)")>]
    let private viewNew (cls: obj) (host: obj) (props: obj) : EditorView = jsNative
    let createView (host: obj) (props: obj) : EditorView = viewNew editorViewClass host props

    // --- prosemirror-keymap / -commands ----------------------------------------------------

    [<Import("keymap", "prosemirror-keymap")>]
    let keymap (bindings: obj) : Plugin = jsNative
    [<Import("baseKeymap", "prosemirror-commands")>]
    let baseKeymap : obj = jsNative
    [<Import("toggleMark", "prosemirror-commands")>]
    let toggleMark (mark: MarkType) : Command = jsNative

    /// What plain Enter does in a bare ProseMirror — split the block, make a paragraph,
    /// lift an empty one, break a line inside code. Read off `baseKeymap` rather than
    /// reassembled from its four parts, so rebinding Enter can hand the ORIGINAL behaviour
    /// to another key without a second definition of it drifting from ProseMirror's.
    [<Emit("$0.Enter")>]
    let baseEnter (bindings: obj) : Command = jsNative

    /// `chainCommands(a, b)`: try `a`, fall through to `b` when it declines. Variadic in JS,
    /// so it is called explicitly rather than imported as a curried F# function.
    [<Import("chainCommands", "prosemirror-commands")>]
    let private chainCommandsFn : obj = jsNative
    [<Emit("$0($1, $2)")>]
    let private callChain (fn: obj) (a: Command) (b: Command) : Command = jsNative
    let chain (a: Command) (b: Command) : Command = callChain chainCommandsFn a b

    /// Steps out of a code block rather than typing into it — chained ahead of a command the
    /// schema would refuse inside one (a hard break, which `code_block`'s `text*` content
    /// cannot hold).
    [<Import("exitCode", "prosemirror-commands")>]
    let exitCode : Command = jsNative

    /// A command that always handles the key by running an effect — how a keystroke reaches
    /// the app (Ctrl+Enter sends). Returning `true` is what stops ProseMirror inserting anything.
    /// A `System.Func` because ProseMirror calls it with three arguments, not curried.
    let effectCommand (run: unit -> unit) : Command =
        box (System.Func<EditorState, obj, obj, bool>(fun _ _ _ -> run (); true))

    /// `nodeType.create()` — a leaf node to insert (the hard break).
    [<Emit("$0.create()")>]
    let nodeCreate (n: NodeType) : Node = jsNative

    [<Emit("$0.scrollIntoView()")>]
    let trScrollIntoView (tr: Transaction) : Transaction = jsNative

    /// A command that EDITS the document, written the way ProseMirror expects: `dispatch` is
    /// absent when the editor is only asking whether the command applies, and a command that
    /// edited anyway would change the document on a mere probe. The option is that absence,
    /// where a truthiness test used to be asked of an `obj`.
    let editCommand (edit: EditorState -> Transaction) : Command =
        box (System.Func<EditorState, (Transaction -> unit) option, obj, bool>(fun state dispatch _ ->
            dispatch |> Option.iter (fun dispatch -> dispatch (edit state))
            true))

    // --- prosemirror-inputrules ------------------------------------------------------------

    [<Import("inputRules", "prosemirror-inputrules")>]
    let inputRules (config: obj) : Plugin = jsNative
    [<Import("wrappingInputRule", "prosemirror-inputrules")>]
    let wrappingInputRule (regexp: obj) (nodeType: NodeType) : InputRule = jsNative
    /// What ProseMirror hands an input rule's callbacks is the REGEX MATCH that fired it, and
    /// `getAttrs` answers with the attributes for the node about to be created — or `null` to
    /// take the node type's own defaults. `System.Func` rather than an F# function because
    /// ProseMirror calls them with plain positional arguments, never curried.
    ///
    /// Both were `obj` while the callbacks were JavaScript in a string, and `obj` is what let
    /// them be: nothing said what arrived, so nothing could be written in a language that
    /// type-checks it.
    [<Import("wrappingInputRule", "prosemirror-inputrules")>]
    let wrappingInputRuleAttrs
        (regexp: obj)
        (nodeType: NodeType)
        (getAttrs: System.Func<string[], NodeAttrs>)
        (joinPredicate: System.Func<string[], Node, bool>)
        : InputRule = jsNative
    [<Import("textblockTypeInputRule", "prosemirror-inputrules")>]
    let textblockTypeInputRule (regexp: obj) (nodeType: NodeType) : InputRule = jsNative
    [<Import("textblockTypeInputRule", "prosemirror-inputrules")>]
    let textblockTypeInputRuleAttrs
        (regexp: obj)
        (nodeType: NodeType)
        (getAttrs: System.Func<string[], NodeAttrs>)
        : InputRule = jsNative
    [<Import("smartQuotes", "prosemirror-inputrules")>]
    let smartQuotes : InputRule[] = jsNative
    [<Import("emDash", "prosemirror-inputrules")>]
    let emDash : InputRule = jsNative
    [<Import("ellipsis", "prosemirror-inputrules")>]
    let ellipsis : InputRule = jsNative
    [<Import("InputRule", "prosemirror-inputrules")>]
    let private inputRuleClass : obj = jsNative
    /// `new InputRule(regexp, handler)` — the handler is a JS multi-arg callback, so it is a
    /// `System.Func` (Fable emits a native n-ary function, never a curried F# closure).
    [<Emit("new ($0)($1, $2)")>]
    let private inputRuleNew (cls: obj) (regexp: obj) (handler: System.Func<EditorState, string[], int, int, Transaction>) : InputRule = jsNative
    let makeInputRule (regexp: obj) (handler: System.Func<EditorState, string[], int, int, Transaction>) : InputRule =
        inputRuleNew inputRuleClass regexp handler

    // --- prosemirror-schema-list -----------------------------------------------------------

    [<Import("splitListItem", "prosemirror-schema-list")>]
    let splitListItem (itemType: NodeType) : Command = jsNative
    [<Import("liftListItem", "prosemirror-schema-list")>]
    let liftListItem (itemType: NodeType) : Command = jsNative
    [<Import("sinkListItem", "prosemirror-schema-list")>]
    let sinkListItem (itemType: NodeType) : Command = jsNative

    // --- y-prosemirror ---------------------------------------------------------------------

    [<Import("ySyncPlugin", "y-prosemirror")>]
    let ySyncPlugin (fragment: Y.XmlFragment) : Plugin = jsNative
    [<Import("yUndoPlugin", "y-prosemirror")>]
    let yUndoPlugin () : Plugin = jsNative
    [<Import("undo", "y-prosemirror")>]
    let yUndo : Command = jsNative
    [<Import("redo", "y-prosemirror")>]
    let yRedo : Command = jsNative

    // --- Presence: selection, plugin-with-state, decorations, relative positions ------------
    // Everything below powers the collaborative cursor overlays. Positions travel the wire as
    // Yjs RELATIVE positions (base64), which survive concurrent edits; a body's positions ride
    // the y-prosemirror ySync mapping, the title's ride the raw `Y.Text`.

    // State/transaction/view members not on the minimal interfaces above.
    [<Emit("$0.selection")>]
    let selection (state: EditorState) : obj = jsNative
    [<Emit("$0.doc")>]
    let stateDoc (state: EditorState) : obj = jsNative
    [<Emit("$0.content.size")>]
    let docContentSize (doc: obj) : int = jsNative
    /// The first child of a doc, read only once the count says there is one to read.
    [<Emit("$0.firstChild")>]
    let private docFirstChild (doc: obj) : obj = jsNative
    /// Whether a node is a textblock — a block node holding inline content.
    [<Emit("$0.isTextblock")>]
    let private nodeIsTextblock (node: obj) : bool = jsNative
    /// A doc nobody has typed in: ProseMirror's empty document is ONE empty textblock, not an
    /// absence, so "is there anything here" is this question and not `size = 0`.
    ///
    /// The count is asked first and `firstChild` only after it, because a doc with no children
    /// has none to read — the short circuit is the guard, not an optimisation.
    let docIsEmpty (doc: obj) : bool =
        if nodeChildCount doc <> 1 then false
        else
            let first = docFirstChild doc
            nodeIsTextblock first && docContentSize first = 0
    [<Emit("$0.hasFocus()")>]
    let viewHasFocus (view: EditorView) : bool = jsNative
    [<Emit("$0.anchor")>]
    let selAnchor (sel: obj) : int = jsNative
    [<Emit("$0.head")>]
    let selHead (sel: obj) : int = jsNative
    [<Emit("$0.setMeta($1, $2)")>]
    let trSetMeta (tr: Transaction) (key: obj) (value: obj) : Transaction = jsNative
    [<Emit("$0.getMeta($1)")>]
    let trGetMeta (tr: Transaction) (key: obj) : obj = jsNative
    [<Emit("$0.docChanged")>]
    let trDocChanged (tr: Transaction) : bool = jsNative
    [<Emit("$0.selectionSet")>]
    let trSelectionSet (tr: Transaction) : bool = jsNative
    [<Emit("$0.mapping")>]
    let trMapping (tr: Transaction) : obj = jsNative
    [<Emit("$0.doc")>]
    let trDoc (tr: Transaction) : obj = jsNative
    [<Emit("$0")>]
    let asTransaction (tr: obj) : Transaction = jsNative

    // prosemirror-state: PluginKey + a Plugin carrying state + props.
    [<Import("PluginKey", "prosemirror-state")>]
    let private pluginKeyClass : obj = jsNative
    [<Emit("new ($0)($1)")>]
    let private pluginKeyNew (cls: obj) (name: string) : obj = jsNative
    let pluginKey (name: string) : obj = pluginKeyNew pluginKeyClass name
    [<Emit("$0.getState($1)")>]
    let pluginKeyGetState (key: obj) (state: EditorState) : obj = jsNative
    [<Import("Plugin", "prosemirror-state")>]
    let private pluginClass : obj = jsNative
    [<Emit("new ($0)($1)")>]
    let private pluginNew (cls: obj) (spec: obj) : Plugin = jsNative
    let makePlugin (spec: obj) : Plugin = pluginNew pluginClass spec

    // prosemirror-view: Decoration widgets/inlines + a DecorationSet.
    [<Import("Decoration", "prosemirror-view")>]
    let private decorationClass : obj = jsNative
    [<Emit("$0.widget($1, $2, $3)")>]
    let private decorationWidget (cls: obj) (pos: int) (dom: obj) (spec: obj) : obj = jsNative
    let decoWidget (pos: int) (dom: obj) (spec: obj) : obj = decorationWidget decorationClass pos dom spec
    [<Emit("$0.inline($1, $2, $3)")>]
    let private decorationInline (cls: obj) (from: int) (to': int) (attrs: obj) : obj = jsNative
    let decoInline (from: int) (to': int) (attrs: obj) : obj = decorationInline decorationClass from to' attrs
    [<Emit("$0.node($1, $2, $3)")>]
    let private decorationNode (cls: obj) (from: int) (to': int) (attrs: obj) : obj = jsNative
    /// Attributes on the NODE spanning `from..to'` — as opposed to `decoInline`'s span inside
    /// one. What puts a marker on a whole empty paragraph without putting anything in it.
    let decoNode (from: int) (to': int) (attrs: obj) : obj = decorationNode decorationClass from to' attrs
    [<Import("DecorationSet", "prosemirror-view")>]
    let private decorationSetClass : obj = jsNative
    [<Emit("$0.create($1, $2)")>]
    let private decorationSetCreate (cls: obj) (doc: obj) (decos: obj[]) : obj = jsNative
    let decoSetCreate (doc: obj) (decos: obj[]) : obj = decorationSetCreate decorationSetClass doc decos
    [<Emit("$0.empty")>]
    let private decorationSetEmpty (cls: obj) : obj = jsNative
    let decoSetEmpty : obj = decorationSetEmpty decorationSetClass
    [<Emit("$0.map($1, $2)")>]
    let decoSetMap (set: obj) (mapping: obj) (doc: obj) : obj = jsNative

    /// The two inline colours the caret carries, written through `Fable.BrowserExtras`'s
    /// CSSOM slice — the same one the shell writes its layout number with. `setProperty` is
    /// the CSSOM's general accessor, so a standard property goes through it as readily as a
    /// custom one.
    let private setBorderColour (element: Browser.Types.HTMLElement) (colour: string) : unit =
        setStyleProperty element "border-color" colour

    let private setBackground (element: Browser.Types.HTMLElement) (colour: string) : unit =
        setStyleProperty element "background" colour

    /// The DOM for one caret + name label (a widget decoration). Built through the typed DOM
    /// binding rather than a JavaScript program in a string: an emit binds a platform API, and
    /// assembling elements is logic, which belongs where the compiler reads it.
    let caretDom (color: string) (name: string) : obj =
        let caret = Browser.Dom.document.createElement "span"
        caret.className <- "pm-caret"
        setBorderColour caret color
        let label = Browser.Dom.document.createElement "span"
        label.className <- "pm-caret-label"
        label.textContent <- name
        setBackground label color
        caret.appendChild label |> ignore
        box caret

    // --- Yjs relative positions (survive concurrent edits) + lib0 base64 for the wire --------

    [<Import("encodeRelativePosition", "yjs")>]
    let private encodeRelPos (rp: obj) : JS.Uint8Array = jsNative
    [<Import("decodeRelativePosition", "yjs")>]
    let private decodeRelPos (bytes: JS.Uint8Array) : obj = jsNative
    [<Import("createRelativePositionFromTypeIndex", "yjs")>]
    let relPosFromTypeIndex (typ: obj) (index: int) : obj = jsNative
    [<Import("createAbsolutePositionFromRelativePosition", "yjs")>]
    let private createAbsPos (rp: obj) (doc: Y.Doc) : obj = jsNative
    let private toBase64 = Lib0.Buffer.toBase64
    let private fromBase64 = Lib0.Buffer.fromBase64

    /// A relative position -> its base64 wire form.
    let encodeRel (relPos: obj) : string = toBase64 (encodeRelPos relPos)
    /// Base64 wire form -> a relative position.
    let decodeRel (encoded: string) : obj = decodeRelPos (fromBase64 encoded)
    /// The absolute index of a base64 relative position in a `Y.Text`/`Y.XmlFragment` on `doc`,
    /// or `None` if it no longer resolves (its anchor content was deleted).
    let absIndexInDoc (doc: Y.Doc) (encoded: string) : int option =
        match createAbsPos (decodeRel encoded) doc with
        | null -> None
        | abs -> Some (abs?index |> unbox<int>)

    // --- y-prosemirror position bridging (ProseMirror positions <-> Yjs relative positions) --

    [<Import("ySyncPluginKey", "y-prosemirror")>]
    let ySyncPluginKey : obj = jsNative
    [<Import("getRelativeSelection", "y-prosemirror")>]
    let private getRelativeSelection (binding: obj) (state: EditorState) : obj = jsNative
    [<Import("relativePositionToAbsolutePosition", "y-prosemirror")>]
    let private relToAbs (doc: Y.Doc) (typ: obj) (relPos: obj) (mapping: obj) : obj = jsNative

    /// The ySync binding for a state (holds the ProseMirror<->Yjs `mapping`, the `type`, `doc`).
    let syncBinding (state: EditorState) : obj = (pluginKeyGetState ySyncPluginKey state)?binding

    /// The editor's current selection as base64 relative anchor/head over its body fragment.
    let relSelectionOf (state: EditorState) : (string * string) option =
        match syncBinding state with
        | null -> None
        | binding ->
            let rs = getRelativeSelection binding state
            Some (encodeRel rs?anchor, encodeRel rs?head)

    /// Map a base64 relative position back to an absolute ProseMirror position in this editor,
    /// or `None` if it no longer resolves.
    let absPosInBody (state: EditorState) (encoded: string) : int option =
        match syncBinding state with
        | null -> None
        | binding ->
            match relToAbs binding?doc binding?``type`` (decodeRel encoded) binding?mapping with
            | null -> None
            | pos -> Some (unbox<int> pos)
