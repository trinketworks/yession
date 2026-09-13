module Yession.Tests.Keystrokes

// What a keydown means to a pty (Plan 14, stage 6).
//
// A terminal front end's whole job with a keyboard is this translation, and until it was F#
// the only thing that ran it was one `ArrowUp` in the browser tier — whose red says a wait
// never settled, never which key encoding broke. It is pure, so the cheap tier asks it
// directly: a chord in, the bytes out.
//
// What is pinned here is the ENCODING, which is not ours to choose: xterm's modifier
// parameter, the CSI sequences a shell reads, the control-character range, and the two
// different bytes Backspace sends depending on Ctrl. A terminal that sends any of them
// differently is one whose keys do the wrong thing in every program.

open Fable.Pyxpecto
open Yession.App

let private chord (key: string) : KeyChord =
    { Key = key; Ctrl = false; Alt = false; Shift = false; Meta = false }

let private ctrl (c: KeyChord) : KeyChord = { c with Ctrl = true }
let private alt (c: KeyChord) : KeyChord = { c with Alt = true }

/// The keys that send a CSI rather than a character, and the final byte each ends with.
let private cursorKeys =
    [ "ArrowUp", "A"
      "ArrowDown", "B"
      "ArrowRight", "C"
      "ArrowLeft", "D"
      "Home", "H"
      "End", "F" ]

/// xterm's modifier parameter, written out rather than recomputed from the sum the code
/// uses: `2` shift, `3` alt, `5` ctrl, and the combinations between them. A test that
/// recomputed it would agree with any formula, including a wrong one.
let private modifiers =
    [ false, false, true, 2
      false, true, false, 3
      false, true, true, 4
      true, false, false, 5
      true, false, true, 6
      true, true, false, 7
      true, true, true, 8 ]

let tests =
    testList "Keystrokes (Plan 14, stage 6)" [

        // Two branches, because "no modifier" is spelled by leaving the parameter OFF rather
        // than by sending `1`: a program reading `ESC[1A` is reading a cursor move with a
        // count, not an unmodified arrow.
        testCase "an unmodified arrow, Home or End sends its CSI with no parameter" <| fun () ->
            for key, final in cursorKeys do
                Expect.equal (Keystroke.bytesOf (chord key)) (Some ("\u001b[" + final)) key

        testCase "a modified arrow, Home or End carries xterm's modifier parameter" <| fun () ->
            for key, final in cursorKeys do
                for withCtrl, withAlt, withShift, parameter in modifiers do
                    let held = { chord key with Ctrl = withCtrl; Alt = withAlt; Shift = withShift }
                    Expect.equal
                        (Keystroke.bytesOf held)
                        (Some ("\u001b[1;" + string parameter + final))
                        (sprintf "%s with ctrl=%b alt=%b shift=%b" key withCtrl withAlt withShift)

        testCase "Ctrl and a letter sends the control character that letter names" <| fun () ->
            Expect.equal (Keystroke.bytesOf (ctrl (chord "c"))) (Some "\u0003") "Ctrl-C interrupts"
            Expect.equal (Keystroke.bytesOf (ctrl (chord "C"))) (Some "\u0003") "the shifted spelling is the same key"
            Expect.equal (Keystroke.bytesOf (ctrl (chord "a"))) (Some "\u0001") "Ctrl-A goes to the line start"
            Expect.equal (Keystroke.bytesOf (ctrl (chord "z"))) (Some "\u001a") "Ctrl-Z suspends"

        testCase "Ctrl sends a control character for every key in the 64..95 code range" <| fun () ->
            for code in 64 .. 95 do
                let key = string (char code)
                Expect.equal
                    (Keystroke.bytesOf (ctrl (chord key)))
                    (Some (string (char (code - 64))))
                    (sprintf "Ctrl-%s" key)

        testCase "Ctrl sends nothing for a character outside that range" <| fun () ->
            for key in [ "1"; "9"; "-"; "/"; "{"; "~" ] do
                Expect.equal (Keystroke.bytesOf (ctrl (chord key))) None (sprintf "Ctrl-%s" key)

        // The two deletes: readline binds them to different bytes, so a terminal that sent
        // one for both would have no delete-word at all.
        testCase "Ctrl-Backspace sends the byte readline binds to delete-word" <| fun () ->
            Expect.equal (Keystroke.bytesOf (ctrl (chord "Backspace"))) (Some "\b") "backspace, not delete"

        testCase "an unmodified Backspace sends DEL" <| fun () ->
            Expect.equal (Keystroke.bytesOf (chord "Backspace")) (Some "\u007f") "delete, not backspace"

        testCase "Alt before a character sends ESC then that character" <| fun () ->
            Expect.equal (Keystroke.bytesOf (alt (chord "b"))) (Some ("\u001bb")) "Alt-B is a word back"
            Expect.equal (Keystroke.bytesOf (alt (chord "f"))) (Some ("\u001bf")) "Alt-F is a word on"

        testCase "Alt-Backspace sends ESC then DEL" <| fun () ->
            Expect.equal (Keystroke.bytesOf (alt (chord "Backspace"))) (Some ("\u001b\u007f")) "rub out a word"

        // A named key has nothing to prefix: the ones that take a modifier carry it in the
        // sequence itself, and ESC followed by a key name is not a sequence anything reads.
        testCase "Alt with a named key that has no character sends nothing" <| fun () ->
            for key in [ "F5"; "Insert"; "Shift" ] do
                Expect.equal (Keystroke.bytesOf (alt (chord key))) None (sprintf "Alt-%s" key)

        testCase "Enter sends a carriage return" <| fun () ->
            Expect.equal (Keystroke.bytesOf (chord "Enter")) (Some "\r") "the line is submitted"

        testCase "Tab sends a tab" <| fun () ->
            Expect.equal (Keystroke.bytesOf (chord "Tab")) (Some "\t") "completion, not focus movement"

        testCase "Escape sends ESC" <| fun () ->
            Expect.equal (Keystroke.bytesOf (chord "Escape")) (Some "\u001b") "what a modal program waits for"

        testCase "PageUp sends the sequence a pager reads" <| fun () ->
            Expect.equal (Keystroke.bytesOf (chord "PageUp")) (Some ("\u001b[5~")) "page back"

        testCase "PageDown sends the sequence a pager reads" <| fun () ->
            Expect.equal (Keystroke.bytesOf (chord "PageDown")) (Some ("\u001b[6~")) "page on"

        testCase "Delete sends the forward-delete sequence" <| fun () ->
            Expect.equal (Keystroke.bytesOf (chord "Delete")) (Some ("\u001b[3~")) "the character under the cursor"

        testCase "a printable character goes as itself" <| fun () ->
            Expect.equal (Keystroke.bytesOf (chord "x")) (Some "x") "a letter"
            Expect.equal (Keystroke.bytesOf (chord " ")) (Some " ") "a space is a character too"

        // Every key that is neither a character nor named above: a bare modifier, a function
        // key, a lock. Sending its NAME would type the word into whatever is running.
        testCase "an unmapped named key sends nothing" <| fun () ->
            for key in [ "F5"; "Shift"; "Control"; "CapsLock"; "Insert" ] do
                Expect.equal (Keystroke.bytesOf (chord key)) None key

        // Cmd belongs to the browser and the OS: a terminal that ate Cmd-W is one you cannot
        // close.
        testCase "Meta sends nothing, whatever else is held" <| fun () ->
            let held = [ chord "c"; chord "ArrowUp"; chord "Enter"; ctrl (chord "c"); alt (chord "b") ]
            for c in held do
                Expect.equal (Keystroke.bytesOf { c with Meta = true }) None c.Key
    ]
