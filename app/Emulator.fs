module Yession.Host.Emulator

// The Session Process's terminal emulator (Plan 13, stage 2b): `@xterm/headless`, which is
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
open Yession.SessionProcess

/// Lines of scrollback the Process keeps per terminal. A snapshot travels in one frame, so
/// this is a frame-size decision as much as a memory one.
let private scrollback = 1000

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
          Resize = fun c r -> term.resize (c, r)
          // Reported on the TRANSITION only, which is what `onBufferChange` already is: the
          // flip policy is a decision about a change of ownership, and firing it on every
          // write would make "a TUI took the screen" a thing said continuously.
          OnAltScreen =
            fun handler -> term.buffer.onBufferChange (fun buffer -> handler (buffer.``type`` = BufferType.Alternate))
          Dispose = fun () -> term.dispose () }
