module Yession.Tests.SandboxTimings

// How long a work sandbox took to come up, folded from the events the session appends.
// Like `TurnTimings`, a pure function of events and the times they were written at.

open System
open Fable.Pyxpecto
open Yession.Domain
open Yession.Domain.Sandboxes

let private expect =
    function
    | Ok v -> v
    | Error e -> failwith e

let private start = DateTimeOffset (2026, 11, 15, 9, 0, 0, TimeSpan.Zero)
let private seconds (n: float) = TimeSpan.FromSeconds n
let private message (id: string) = MessageId.create id |> expect

let private starting (id: string) (backend: string) =
    WorkSandboxStarting
        { MessageId = message id
          Sandbox = SandboxRef.defaultRef
          Backend = backend
          Description = None
          Actor = ActorRef.System
          OnBehalfOf = None
          CausedBy = None }

let private started (id: string) =
    WorkSandboxStarted
        { MessageId = message id
          Sandbox = SandboxRef.defaultRef
          // What the START says is not what is read: the backend is taken from the event
          // that began the work, so a timing has one even when the start fails.
          Backend = "another"
          Description = None
          Checkout = None
          Forwarded = []
          Realisation = []
          Actor = ActorRef.System
          OnBehalfOf = None
          CausedBy = None }

let private startFailed (id: string) =
    WorkSandboxStartFailed
        { MessageId = message id
          Sandbox = SandboxRef.defaultRef
          Reason = "the image could not be pulled"
          Actor = ActorRef.System
          OnBehalfOf = None
          CausedBy = None }

let private timed (events: (float * SessionEvent) list) : SandboxStartTiming list =
    events
    |> List.fold
        (fun (state, soFar) (after, event) ->
            let next, timings = SandboxTimings.step state (start + seconds after) event
            next, soFar @ timings)
        (SandboxTimings.empty, [])
    |> snd

let tests =
    testList "Sandbox start timings" [
        testCase "a sandbox that comes up is timed from the moment its work began" <| fun () ->
            Expect.equal
                (timed [ 0.0, starting "a" "docker"; 42.0, started "a" ])
                [ { Backend = "docker"; Started = true; Took = seconds 42.0 } ]
                "on the backend its start named"

        testCase "a sandbox that could not come up is timed too, as a failure" <| fun () ->
            Expect.equal
                (timed [ 0.0, starting "a" "srt"; 3.0, startFailed "a" ])
                [ { Backend = "srt"; Started = false; Took = seconds 3.0 } ]
                "how long somebody waited to be told no"

        testCase "two sandboxes coming up at once are each timed from their own beginning" <| fun () ->
            Expect.equal
                (timed [ 0.0, starting "a" "docker"; 1.0, starting "b" "srt"; 2.0, started "b"; 60.0, started "a" ])
                [ { Backend = "srt"; Started = true; Took = seconds 1.0 }
                  { Backend = "docker"; Started = true; Took = seconds 60.0 } ]
                "matched by the message the two events share"

        // A start recorded with nothing before it: nothing here saw when its work began,
        // and a guess would be a duration of zero.
        testCase "a start with no beginning seen times nothing" <| fun () ->
            Expect.equal (timed [ 5.0, started "a"; 6.0, startFailed "b" ]) [] "only a start seen from its beginning is timed"

        testCase "one beginning is resolved once" <| fun () ->
            Expect.equal
                (timed [ 0.0, starting "a" "srt"; 1.0, started "a"; 2.0, started "a" ])
                [ { Backend = "srt"; Started = true; Took = seconds 1.0 } ]
                "the second resolution has no beginning left"
    ]
