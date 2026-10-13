module Yession.Tests.Markup

// The HTML the product renders, read as a tree.
//
// The scans in "Chrome consistency" that look at ONE tag (`classesOf`) cannot see a name that
// lives in ANOTHER element: a `<label for>`, a wrapping `<label>`, an `aria-labelledby` target
// are two places in one document, and the floor is a property of the pair. So this is a tree,
// and only as much of one as that takes.
//
// It reads what the renderers emit (`Ssr`, the Manager's templates and its two standalone
// pages) and is not a validator: a stray close tag is ignored, an unclosed element ends with
// its parent, and text is NOT entity-decoded, because nothing here compares copy.

open System
open System.Text

/// One element. Attribute names are lower-cased; a bare attribute (`hidden`, `required`) has
/// the value `""`, which is also what `class=""` has — nothing here tells them apart.
type Element =
    { Tag: string
      Attributes: Map<string, string>
      Children: Node list }

and Node =
    | Text of string
    | Elem of Element

/// Elements that have no end tag, so nothing nests inside them.
let private voidTags =
    set [ "area"; "base"; "br"; "col"; "embed"; "hr"; "img"; "input"; "link"; "meta"; "source"; "track"; "wbr" ]

/// Elements whose content is text up to their own end tag, whatever it looks like.
let private rawTextTags = set [ "script"; "style"; "textarea"; "title" ]

type private Open =
    { Name: string
      Attrs: Map<string, string>
      Kids: ResizeArray<Node> }

let private isNameChar (c: char) = Char.IsLetterOrDigit c || c = '-' || c = ':' || c = '_'

/// A position in the text being read. A class rather than closures over a local, which F#
/// will not let share a mutable one.
type private Cursor (html: string) =
    let n = html.Length
    let mutable i = 0
    member _.Done = i >= n
    member _.Here = html.[i]
    member _.Next = if i + 1 < n then Some html.[i + 1] else None
    member _.Skip (count: int) = i <- min n (i + count)
    member _.At (s: string) = i + s.Length <= n && html.Substring (i, s.Length) = s
    member this.SkipSpace () =
        while i < n && Char.IsWhiteSpace html.[i] do
            i <- i + 1
    /// Past the next `terminator`, or to the end when there is none.
    member _.SkipPast (terminator: string) =
        match html.IndexOf (terminator, i, StringComparison.Ordinal) with
        | -1 -> i <- n
        | found -> i <- found + terminator.Length
    /// Everything up to the next `<`.
    member _.ReadText () =
        let next = match html.IndexOf ('<', i) with -1 -> n | found -> found
        let read = html.Substring (i, next - i)
        i <- next
        read
    member _.ReadName () =
        let from = i
        while i < n && isNameChar html.[i] do
            i <- i + 1
        html.Substring(from, i - from).ToLowerInvariant ()
    /// The body of a raw-text element: everything up to its own end tag, which is left unread.
    member _.ReadRaw (name: string) =
        let close = html.IndexOf ("</" + name, i, StringComparison.OrdinalIgnoreCase)
        let stop = if close < 0 then n else close
        let read = html.Substring (i, stop - i)
        i <- stop
        read
    /// The attributes of the tag whose name has just been read, and whether it closed itself.
    member this.ReadAttributes () : Map<string, string> * bool =
        let mutable attrs : Map<string, string> = Map.empty
        let mutable selfClosed = false
        let mutable finished = false
        while not finished do
            this.SkipSpace ()
            if i >= n then finished <- true
            elif html.[i] = '>' then
                i <- i + 1
                finished <- true
            elif this.At "/>" then
                i <- i + 2
                selfClosed <- true
                finished <- true
            elif html.[i] = '/' then i <- i + 1
            else
                let from = i
                while i < n && not (Char.IsWhiteSpace html.[i]) && html.[i] <> '=' && html.[i] <> '>' && not (this.At "/>") do
                    i <- i + 1
                let name = html.Substring(from, i - from).ToLowerInvariant ()
                this.SkipSpace ()
                let value =
                    if i < n && html.[i] = '=' then
                        i <- i + 1
                        this.SkipSpace ()
                        if i < n && (html.[i] = '"' || html.[i] = '\'') then
                            let quote = html.[i]
                            let close = html.IndexOf (quote, i + 1)
                            let stop = if close < 0 then n else close
                            let v = html.Substring (i + 1, stop - i - 1)
                            i <- min n (stop + 1)
                            v
                        else
                            let valueFrom = i
                            while i < n && not (Char.IsWhiteSpace html.[i]) && html.[i] <> '>' do
                                i <- i + 1
                            html.Substring (valueFrom, i - valueFrom)
                    else ""
                if name <> "" then attrs <- Map.add name value attrs
        attrs, selfClosed

/// The tree of a rendered fragment or document.
let parse (html: string) : Node list =
    let cursor = Cursor html
    let stack = ResizeArray<Open> [ { Name = ""; Attrs = Map.empty; Kids = ResizeArray () } ]
    let top () = stack.[stack.Count - 1]
    let add (node: Node) = (top ()).Kids.Add node
    let addText (text: string) = if text <> "" then add (Text text)
    let pop () =
        let finished = top ()
        stack.RemoveAt (stack.Count - 1)
        add (Elem { Tag = finished.Name; Attributes = finished.Attrs; Children = List.ofSeq finished.Kids })
    while not cursor.Done do
        if cursor.Here <> '<' then addText (cursor.ReadText ())
        elif cursor.At "<!--" then cursor.SkipPast "-->"
        elif cursor.At "<!" || cursor.At "<?" then cursor.SkipPast ">"
        elif cursor.Next = Some '/' then
            cursor.Skip 2
            let name = cursor.ReadName ()
            cursor.SkipPast ">"
            // A close tag ends the nearest open element of its name, and whatever was left
            // open inside it; one with nothing to close is ignored.
            let mutable found = -1
            for depth in 1 .. stack.Count - 1 do
                if stack.[depth].Name = name then found <- depth
            if found > 0 then
                while stack.Count > found do
                    pop ()
        elif cursor.Next |> Option.exists Char.IsLetter then
            cursor.Skip 1
            let name = cursor.ReadName ()
            let attrs, selfClosed = cursor.ReadAttributes ()
            if voidTags.Contains name || selfClosed then
                add (Elem { Tag = name; Attributes = attrs; Children = [] })
            elif rawTextTags.Contains name then
                let body = cursor.ReadRaw name
                cursor.SkipPast ">"
                add (Elem { Tag = name; Attributes = attrs; Children = (if body = "" then [] else [ Text body ]) })
            else stack.Add { Name = name; Attrs = attrs; Kids = ResizeArray () }
        else
            addText "<"
            cursor.Skip 1
    while stack.Count > 1 do
        pop ()
    List.ofSeq stack.[0].Kids

/// Every element under `nodes`, in document order, each with the elements around it (nearest
/// first).
let elements (nodes: Node list) : (Element * Element list) list =
    let found = ResizeArray<Element * Element list> ()
    let rec walk (ancestors: Element list) (node: Node) =
        match node with
        | Text _ -> ()
        | Elem element ->
            found.Add (element, ancestors)
            for child in element.Children do
                walk (element :: ancestors) child
    for node in nodes do
        walk [] node
    List.ofSeq found

let attribute (name: string) (element: Element) : string option = Map.tryFind name element.Attributes

/// The value of an attribute, trimmed, and `None` when it is absent or blank — the question
/// the floor asks of `lang`, `scope`, `aria-label` is whether it SAYS anything.
let said (name: string) (element: Element) : string option =
    attribute name element
    |> Option.map (fun value -> value.Trim ())
    |> Option.filter (fun value -> value <> "")

/// What a reader is given by an element's content: its text, single-spaced, without what is
/// hidden from them (`hidden`, `aria-hidden="true"`), what is never text (`script`, `style`)
/// and whatever `exclude` rules out.
let text (exclude: Element -> bool) (element: Element) : string =
    let out = StringBuilder ()
    let rec into (node: Node) =
        match node with
        | Text t -> out.Append(t).Append ' ' |> ignore
        | Elem e ->
            let hidden = Map.containsKey "hidden" e.Attributes || attribute "aria-hidden" e = Some "true"
            if not hidden && e.Tag <> "script" && e.Tag <> "style" && not (exclude e) then
                for child in e.Children do
                    into child
    for child in element.Children do
        into child
    out.ToString().Split ([| ' '; '\n'; '\r'; '\t' |], StringSplitOptions.RemoveEmptyEntries)
    |> String.concat " "

/// An element as the failure that names it should print it: the tag, the attributes that say
/// WHICH one it is (id, type, name, the `data-*` hooks, a stretch of the class), and the text
/// of the element around it. Not the whole opening tag, which for this product is a paragraph
/// of utility classes.
let describe (parent: Element option) (element: Element) : string =
    let shown =
        [ for key in [ "id"; "type"; "name" ] do
              match attribute key element with
              | Some value -> yield sprintf "%s=\"%s\"" key value
              | None -> ()
          for KeyValue (key, value) in element.Attributes do
              if key.StartsWith "data-" then yield (if value = "" then key else sprintf "%s=\"%s\"" key value)
          match said "class" element with
          | Some classes -> yield sprintf "class=\"%s\"" (if classes.Length > 48 then classes.Substring (0, 48) + "…" else classes)
          | None -> () ]
    let around =
        match parent with
        | Some p ->
            let words = text (fun _ -> false) p
            sprintf " in <%s> \"%s\"" p.Tag (if words.Length > 72 then words.Substring (0, 72) + "…" else words)
        | None -> ""
    sprintf "<%s%s>%s" element.Tag (shown |> List.map (fun s -> " " + s) |> String.concat "") around
