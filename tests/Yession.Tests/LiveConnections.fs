module Yession.Tests.LiveConnections

// Every peer connection this run opened, closed by whoever opened it.
//
// A native connection that outlives the case that made it holds a port, a thread and a
// callback slot for the rest of the process, and the case that trips over it later is never
// the case that leaked it. That is how this suite has been read wrong three times: a
// ten-second timeout inside an unrelated teardown, diagnosed as "something was still open"
// with no way to say what.
//
// So the question is asked directly, once, after every Node suite has run: what is still
// open, and who opened it. `Interop` knows, because it is the one place a connection is
// created and the one place a completed close is recorded.
//
// Last in the list deliberately — the answer is only meaningful once nothing else will run.

open Fable.Pyxpecto
open Yession.Host

let tests =
    testList "Live connections" [
        testCase "every peer connection this run opened was closed" <| fun () ->
            Expect.equal
                (Interop.liveConnectionNames ())
                []
                "these peer connections were never closed by the case that opened them"
    ]
