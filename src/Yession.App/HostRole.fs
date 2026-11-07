namespace Yession.App

/// What an element the browser works on after a render IS to the pass that works on it: an
/// editor's host, a command line, a surface read from its end. The view marks each with its
/// role and its key (`HostRole.mark`), and the element registers itself under them while lit
/// keeps it in the page (`Fable.LitExtras.host`), so the pass after a render walks what is
/// registered rather than asking the whole document where its elements went
/// (`app/browser/Hosts.fs`).
///
/// A closed set, because each role is a pass that reads it: a role nobody reads is a
/// registration for nothing, and a pass reading a role no view marks reads an empty set
/// forever, which looks exactly like a page with nothing of that kind on it.
[<RequireQualifiedAccess>]
type HostRole =
    /// A rich body's host (`data-rich-body`): the editor is mounted into it.
    | RichBody
    /// A terminal command line (`data-terminal-input`): bound to its `Y.Text` root.
    | TerminalInput
    /// A read-only mount of a command line (`data-terminal-text`).
    | TerminalText

module HostRole =

    /// The role's name, as the directive carries it.
    let name (role: HostRole) : string =
        match role with
        | HostRole.RichBody -> "rich-body"
        | HostRole.TerminalInput -> "terminal-input"
        | HostRole.TerminalText -> "terminal-text"

    /// The role a name says, or `None` for a name no role has.
    let ofName (name: string) : HostRole option =
        match name with
        | "rich-body" -> Some HostRole.RichBody
        | "terminal-input" -> Some HostRole.TerminalInput
        | "terminal-text" -> Some HostRole.TerminalText
        | _ -> None

    /// In an element's tag, last: this element is in `role`'s set under `key` while it is in
    /// the page. Last, because lit commits a tag's bindings in order, and the attributes the
    /// pass reads (`readonly`, `data-rich-readonly`) are then already on the element when it
    /// registers. A string renders it as nothing (`Ssr`).
    let mark (role: HostRole) (key: string) : Lit.TemplateResult = Fable.LitExtras.host (name role) key
