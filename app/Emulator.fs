module Yession.Host.Emulator

// The Session's terminal emulator (Plan 13, stage 2b): `@xterm/headless`, which is
// the same emulator the browser renders with minus the DOM.
//
// That sameness is the point. The Process has to answer three questions about a terminal —
// what the screen looks like for a peer joining mid-session, whether a foreground program
// has taken the alternate screen, and where OSC 133 marks fall — and answering them with a
// second, home-grown interpretation of ANSI would mean two implementations that agree until
// they do not, with the disagreement showing up as a screen that reads differently on two
// people's machines. Stage 1's pure-F# SGR parser is not a competitor here: it renders a
// STREAM of block output, which is a different job from maintaining a SCREEN, and it stays
// where it is.
//
// The bindings are `Fable.Xterm` — the slice of `@xterm/headless` and its serialize addon
// this capability uses, and the two import forms that resolve on both platforms this module
// is bundled for.

open Fable.Xterm
open Yession.Domain.Terminals
open Yession.Session

/// Lines of scrollback the Process keeps per terminal. A snapshot travels in one frame, so
/// this is a frame-size decision as much as a memory one.
let private scrollback = 1000

/// Where the cursor stands, in the lines the serialization is written as (`ScreenCursor`).
///
/// The serializer writes the buffer from its first row, one line per row, except that a row
/// the emulator WRAPPED continues the line above it unbroken. So the line is the number of
/// rows that start one, up to the cursor's; the column is the cursor's own plus a width for
/// every wrapped row between it and the start of its line.
///
/// `None` on the alternate screen. Its serialization is written AFTER the normal screen's,
/// opened by a cursor-home a parse drops, so it reads as a continuation of the normal
/// screen's last line — there is no line of it a cursor could be placed on honestly.
let private cursorOf (term: Terminal) : ScreenCursor option =
    let buffer = term.buffer.active
    if buffer.``type`` = BufferType.Alternate then None
    else
        let row = buffer.baseY + buffer.cursorY
        let wrapped (y: int) =
            match buffer.getLine y with
            | null -> false
            | line -> line.isWrapped
        let line = [ 1 .. row ] |> List.filter (fun y -> not (wrapped y)) |> List.length
        let rec continued (y: int) (column: int) =
            if y > 0 && wrapped y then continued (y - 1) (column + term.cols) else column
        Some { ScreenCursor.Line = line; ScreenCursor.Column = continued row buffer.cursorX }

// --- The capability ---------------------------------------------------------------------

/// Open an emulator. `cols`/`rows` are the terminal's size register (default 80x24).
let openEmulator : OpenEmulator =
    fun cols rows ->
        // `allowProposedApi` is required for the serialize addon, which is what a snapshot is.
        // `scrollback` is bounded here rather than left at the default: the screen is a live
        // view a joining peer downloads in one frame, and the transcript — not this — is what
        // holds everything a terminal ever printed.
        let term =
            headless.Terminal.Create
                { TerminalOptions.cols = cols
                  TerminalOptions.rows = rows
                  TerminalOptions.allowProposedApi = true
                  TerminalOptions.scrollback = scrollback }
        let serializer = SerializeAddon.Create ()
        term.loadAddon serializer
        { Write = fun data -> term.write (data, ignore)
          // Empty write as a BARRIER. xterm parses writes asynchronously and in order, so a
          // zero-length write queued behind everything already fed resolves only once all of
          // it has been applied — which is the difference between serializing the screen and
          // serializing a screen that has not been drawn yet. There is no synchronous flush
          // to reach for instead.
          Serialize =
            fun () ->
                Async.FromContinuations (fun (cont, _, _) ->
                    term.write ("", fun () -> cont (serializer.serialize ())))
          Screen =
            fun () ->
                Async.FromContinuations (fun (cont, _, _) ->
                    term.write ("", fun () -> cont { LiveScreen.Text = serializer.serialize (); LiveScreen.Cursor = cursorOf term }))
          Resize = fun c r -> term.resize (c, r)
          // Reported on the TRANSITION only, which is what `onBufferChange` already is: the
          // flip policy is a decision about a change of ownership, and firing it on every
          // write would make "a TUI took the screen" a thing said continuously.
          OnAltScreen =
            fun handler -> term.buffer.onBufferChange (fun buffer -> handler (buffer.``type`` = BufferType.Alternate))
          Dispose = fun () -> term.dispose () }
