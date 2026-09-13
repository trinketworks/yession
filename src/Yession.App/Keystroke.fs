namespace Yession.App

/// A keydown as the browser reports it, with nothing of the browser left in it: the `key` name
/// and which modifiers were down. The five fields a `KeyboardEvent` carries that the translation
/// below reads, and the reason it can be decided — and tested — without a DOM.
type KeyChord =
    { /// `KeyboardEvent.key`: one character for a printable key, a name for everything else.
      Key : string
      Ctrl : bool
      Alt : bool
      Shift : bool
      Meta : bool }

/// The bytes a keydown means to a pty (Plan 14, stage 6).
///
/// A keyboard event is not a byte stream, and the translation is the whole of what a
/// terminal front end does with keys: printable characters go as themselves, Ctrl-<key>
/// as the control code, and the keys with no character at all (arrows, Home, the
/// function block) as the escape sequences a program is waiting for. `None` means a key
/// that sends nothing — a bare modifier, or a shortcut the browser owns.
///
/// **Modifiers are part of the key, not a reason to drop it.** This used to refuse every
/// event carrying `altKey`, and to refuse `ctrlKey` with anything that was not a single
/// character — which is every way of moving by WORD rather than by character. Alt-B,
/// Alt-F and Ctrl-arrow are how a person navigates a line they have already typed, so a
/// terminal that swallows all three is one you can only walk through a character at a
/// time. Alt is `ESC` before the key, which is what `metaSendsEscape` means and what
/// readline is reading; a modified arrow is the same CSI with a parameter saying which
/// modifier (`2` shift, `3` alt, `5` ctrl — xterm's encoding, the one every shell reads).
///
/// `Meta` still sends nothing. Cmd belongs to the browser and the OS, and a terminal
/// that ate Cmd-W would be a terminal you cannot close.
///
/// One honest limit, on macOS: Option COMPOSES. `ev.key` for Option-B is `∫`, so what
/// goes is `ESC∫` unless the browser or the OS has been told to treat Option as Meta,
/// which is the setting every terminal emulator on that platform also asks for. Deriving
/// the unmodified letter from `ev.code` would be a guess about a keyboard layout — right
/// on QWERTY, wrong on Dvorak and on every non-Latin layout — so the limit is stated
/// rather than papered over.
///
/// Deciding is all this does. Acting on the decision — `preventDefault` for everything that
/// IS sent — belongs to the caller that holds the event, and is one verb with it there.
module Keystroke =

    /// The CSI an arrow (or Home/End) sends, carrying whichever modifiers were held.
    let private csi (chord: KeyChord) (final: string) : string =
        // xterm's modifier parameter: 1 + shift(1) + alt(2) + ctrl(4). 1 is "no modifier", which is
        // spelled by leaving the parameter off entirely.
        let modifier =
            1
            + (if chord.Shift then 1 else 0)
            + (if chord.Alt then 2 else 0)
            + (if chord.Ctrl then 4 else 0)
        if modifier = 1 then "\u001b[" + final else "\u001b[1;" + string modifier + final

    /// What a chord sends, or `None` for one that sends nothing.
    let bytesOf (chord: KeyChord) : string option =
        if chord.Meta then None else

        let k = chord.Key
        match k with
        | "ArrowUp" -> Some (csi chord "A")
        | "ArrowDown" -> Some (csi chord "B")
        | "ArrowRight" -> Some (csi chord "C")
        | "ArrowLeft" -> Some (csi chord "D")
        | "Home" -> Some (csi chord "H")
        | "End" -> Some (csi chord "F")
        | _ when chord.Ctrl ->
            // Ctrl-Backspace is the other delete-word, and the byte it sends is the one readline
            // binds: `\b`, not the `\x7f` an unmodified Backspace sends.
            if k = "Backspace" then Some "\b"
            elif k.Length = 1 then
                let c = int (k.ToUpperInvariant().[0])
                if c >= 64 && c <= 95 then Some (string (char (c - 64))) else None
            else None
        | _ when chord.Alt ->
            // ESC then the key: Alt-B, Alt-F, Alt-D, Alt-Backspace — a word back, a word on, kill a
            // word, rub one out. Only for keys that ARE a character; the named ones above already
            // carried their modifier in the sequence, and the rest have nothing to prefix.
            if k = "Backspace" then Some "\u001b\u007f"
            elif k.Length = 1 then Some ("\u001b" + k)
            else None
        | "Enter" -> Some "\r"
        | "Backspace" -> Some "\u007f"
        | "Tab" -> Some "\t"
        | "Escape" -> Some "\u001b"
        | "PageUp" -> Some "\u001b[5~"
        | "PageDown" -> Some "\u001b[6~"
        | "Delete" -> Some "\u001b[3~"
        | _ -> if k.Length = 1 then Some k else None
