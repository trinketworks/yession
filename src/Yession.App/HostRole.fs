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
    /// A surface read from its end (`data-tail`), keyed by its `TailSurface.key`.
    | Tail
    /// The box a terminal's output is laid into, live screen or block scrollback, keyed by the
    /// terminal's id: what is measured for its size.
    | Viewport
    /// A recording's player mount (`data-pane-replay`), keyed by its tab key.
    | Replay
    /// The foot of a paged listing, keyed by its hook (`Dom.Hooks.repoPickerFoot`,
    /// `Dom.Hooks.repoBranchFoot`): watched for the reader reaching it.
    | ListingFoot
    /// The pane's tab strip (`data-pane-strip`), keyed by nothing: its fade is kept per render.
    | PaneStrip

module HostRole =

    /// The role's name, as the directive carries it.
    let name (role: HostRole) : string =
        match role with
        | HostRole.RichBody -> "rich-body"
        | HostRole.TerminalInput -> "terminal-input"
        | HostRole.TerminalText -> "terminal-text"
        | HostRole.Tail -> "tail"
        | HostRole.Viewport -> "viewport"
        | HostRole.Replay -> "replay"
        | HostRole.ListingFoot -> "listing-foot"
        | HostRole.PaneStrip -> "pane-strip"

    /// The role a name says, or `None` for a name no role has.
    let ofName (name: string) : HostRole option =
        match name with
        | "rich-body" -> Some HostRole.RichBody
        | "terminal-input" -> Some HostRole.TerminalInput
        | "terminal-text" -> Some HostRole.TerminalText
        | "tail" -> Some HostRole.Tail
        | "viewport" -> Some HostRole.Viewport
        | "replay" -> Some HostRole.Replay
        | "listing-foot" -> Some HostRole.ListingFoot
        | "pane-strip" -> Some HostRole.PaneStrip
        | _ -> None

    /// In an element's tag, after its attributes: this element is in `role`'s set under `key`
    /// while it is in the page. After, because lit commits a tag's bindings in order, and the
    /// attributes a pass reads (`readonly`, `data-rich-readonly`, `data-tail`) are then already
    /// on the element when it registers. A string renders it as nothing (`Ssr`).
    let mark (role: HostRole) (key: string) : Lit.TemplateResult = Fable.LitExtras.host (name role) key
