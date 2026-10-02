namespace Yession.Domain

open Yession.Domain.Content

/// A thing the pane can SHOW: a file under the session's content root, or a terminal.
///
/// One type over both kinds because the pane is one pane: it shows a terminal or it shows a
/// file, never both, so the peers viewing a tab are found by one comparison rather than by a
/// per-kind register that each new kind of content would have to be added to. A repo file
/// arrives as a `ContentRef` and needs nothing here.
///
/// It began as presence — what a peer has in front of them, which is the answer to "who else
/// is looking at this" — and lived with the transport frames that carry presence. It is more
/// than that: it is the vocabulary of the pane, and a tab the log records somebody opening
/// carries one. The log compiles long before the transport does, which is the mechanical half
/// of why it moved; the other half is that `Transport.fs` was never where the question "what
/// can the pane show" belonged.
type ViewRef =
    /// A file under the session's content root — an artifact version today.
    | ViewingFile of ContentRef
    /// A terminal, whatever face of it is up: its screen, one block, one stretch. The
    /// distinction is what the viewer is reading, not what they have open, and a dot beside
    /// a terminal that says "and they are three blocks down it" is noise.
    | ViewingTerminal of TerminalId

module ViewRef =

    /// How a terminal is spelled when it has to be said rather than held. Prefixed because a
    /// terminal id and a content path are both made of ordinary characters, so without it
    /// `artifacts` and an id are the same string to a reader — and this reader is a tool
    /// argument an agent writes by hand.
    let terminalPrefix = "terminal:"

    /// The ONE prose spelling of a thing the pane can show, for every reader that is not a
    /// screen — a tool's argument, a tool's answer, a log line, a test.
    ///
    /// A file is its `file:///` URL, which is what `share_artifact` already answers with and
    /// what a message body already links to (`EntityRef.said` spells it the same way), so an
    /// agent quoting an address it has seen is understood without being told a second form.
    let said (view: ViewRef) : string =
        match view with
        | ViewingFile ref -> ContentRef.url ref
        | ViewingTerminal id -> terminalPrefix + TerminalId.value id

    /// `said` read back. Refuses in the writer's words, naming both spellings, because what
    /// reads this is an agent that has to get it right on the next try.
    let read (raw: string) : Result<ViewRef, string> =
        let trimmed = raw |> Option.ofObj |> Option.map (fun r -> r.Trim ()) |> Option.defaultValue ""
        if trimmed = "" then Error "name something to show: a file as file:///artifacts/<name>, or a terminal as terminal:<id>"
        elif trimmed.StartsWith terminalPrefix then
            TerminalId.create (trimmed.Substring terminalPrefix.Length) |> Result.map ViewingTerminal
        else
            // Everything else is a content path, and `ContentRef.create` is what decides —
            // it already takes the three spellings a reader meets (`file:///artifacts/x`,
            // `/artifacts/x`, `artifacts/x`) and refuses the rest in its own words.
            ContentRef.create trimmed |> Result.map ViewingFile

/// A thing put in front of the people here — the act `open_tab` and `focus_tab` record.
///
/// ONE fact for both, because they differ in exactly one thing and it is a property of the
/// act rather than a different act: whether it takes the screen. A second case would be two
/// facts free to disagree about whether the thing is now open, when the answer is always yes.
///
/// It is the whole of what the agent can do to a strip, and deliberately so: nothing here
/// says which tab is kept, or in what order anyone's tabs sit. Those are the person's, held
/// in their own browser, and an act that could reach them would be an act that could undo a
/// decision somebody made about their own screen.
[<RequireQualifiedAccess>]
type TabOpened =
    { /// What to show.
      Ref : ViewRef
      /// Take the screen with it, or wait to be chosen? `false` puts it in reach and leaves
      /// every reader where they are, which is what an agent that merely has something ready
      /// should do; `true` is somebody having asked to be shown it.
      Focus : bool }

/// A thing taken back off the strip — `close_tab`.
///
/// What a reader has KEPT is untouched, and that rule lives in the reader rather than here:
/// a pin is local to one browser, so this act cannot know about one, and a client folding
/// this leaves a tab its person kept exactly where it is. Said once, in the fold, rather than
/// as a refusal this side could only guess at.
[<RequireQualifiedAccess>]
type TabClosed =
    { Ref : ViewRef }
