module Yession.Tests.TabStrips

// Where focus goes when somebody walks the pane's tablist.
//
// ARIA's tabs pattern promises Left/Right/Home/End over a `role="tablist"`, and until this was
// F# the only thing that ran it was a browser: the arithmetic lived in a JavaScript program —
// first in an `[<Emit>]` string, then in a `.mjs` module — where nothing could put a question
// to it. It is an index and a count, so the cheap tier asks it directly.
//
// What is pinned here is the PATTERN, which is not ours to choose: the wrap at both ends, the
// jumps to either end, and the two states that are easy to get wrong — focus not on a tab at
// all, and a strip with nothing in it.

open Fable.Pyxpecto
open Yession.App

/// Four tabs, which is the smallest strip where "next", "wrap" and "neither end" are three
/// different answers.
let private four = 4

let tests =
    testList "TabStrips" [

        testCase "Right moves on, and wraps off the end" <| fun () ->
            Expect.equal (TabStrip.walk "ArrowRight" 0 four) (Some 1) "from the first"
            Expect.equal (TabStrip.walk "ArrowRight" 2 four) (Some 3) "from the middle"
            Expect.equal (TabStrip.walk "ArrowRight" 3 four) (Some 0) "off the end"

        testCase "Left moves back, and wraps off the start" <| fun () ->
            Expect.equal (TabStrip.walk "ArrowLeft" 3 four) (Some 2) "from the last"
            Expect.equal (TabStrip.walk "ArrowLeft" 1 four) (Some 0) "from the middle"
            Expect.equal (TabStrip.walk "ArrowLeft" 0 four) (Some 3) "off the start"

        testCase "Home and End jump to the ends whatever is focused" <| fun () ->
            for here in -1 .. four - 1 do
                Expect.equal (TabStrip.walk "Home" here four) (Some 0) "Home"
                Expect.equal (TabStrip.walk "End" here four) (Some (four - 1)) "End"

        // The tablist itself can hold focus, and from there "back one" has no meaning. Landing
        // on the first tab is what both arrows do; wrapping to the LAST would put a reader at
        // the far end of a strip they had only just reached.
        testCase "either arrow from off the tabs lands on the first" <| fun () ->
            Expect.equal (TabStrip.walk "ArrowRight" -1 four) (Some 0) "Right"
            Expect.equal (TabStrip.walk "ArrowLeft" -1 four) (Some 0) "Left"

        // `None` is not "stay put" — it is "this key is not mine", which is what lets the same
        // handler carry the Delete/Backspace unpin without the walk swallowing it.
        testCase "a key outside the pattern is not the walk's" <| fun () ->
            for key in [ "Delete"; "Backspace"; "Enter"; " "; "a"; "ArrowUp"; "ArrowDown" ] do
                Expect.equal (TabStrip.walk key 1 four) None key

        testCase "an empty strip has nowhere to walk to" <| fun () ->
            for key in [ "ArrowLeft"; "ArrowRight"; "Home"; "End" ] do
                Expect.equal (TabStrip.walk key -1 0) None key

        // The neighbour is taken BEFORE the tab is released, so it is an index into the strip
        // as it stands now — and what will be sitting there afterwards is the tab currently
        // one along, except at the end of the row.
        testCase "the neighbour of a released tab is the one that takes its place" <| fun () ->
            Expect.equal (TabStrip.neighbour 0 four) (Some 0) "the first"
            Expect.equal (TabStrip.neighbour 1 four) (Some 1) "the middle"

        testCase "the last tab hands focus back down the row" <| fun () ->
            Expect.equal (TabStrip.neighbour 3 four) (Some 2) "the last"

        testCase "there is no neighbour when focus is off the tabs" <| fun () ->
            Expect.equal (TabStrip.neighbour -1 four) None "off the tabs"

        // Releasing the only tab empties the strip, so there is nothing to hand focus to and
        // the caller must not be told to focus index -1.
        testCase "there is no neighbour when releasing the only tab" <| fun () ->
            Expect.equal (TabStrip.neighbour 0 1) None "the only tab"
    ]
