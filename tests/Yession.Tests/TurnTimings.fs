module Yession.Tests.TurnTimings

// How long a turn took, folded from the events the session appends. A pure function of
// events and the times they were written at, so every case here is on a clock the case
// writes down: no turn runs and nothing waits.

open System
open Fable.Pyxpecto
open Yession.Domain
open Yession.Domain.Agent

let private expect =
    function
    | Ok v -> v
    | Error e -> failwith e

let private start = DateTimeOffset (2026, 11, 15, 9, 0, 0, TimeSpan.Zero)
let private seconds (n: float) = TimeSpan.FromSeconds n
let private turn (id: string) = AgentTurnId.create id |> expect
let private message (id: string) = MessageId.create id |> expect

let private started (id: string) =
    AgentTurnStarted { AgentTurnId = turn id; Cause = TurnCause.TriggeredBy (message "asked") }
let private opened (id: string) =
    AgentMessageStarted { AgentTurnId = turn id; MessageId = message "reply"; Antecedent = None }
let private word (id: string) =
    AgentMessageDelta { AgentTurnId = turn id; MessageId = message "reply"; Delta = "hello" }
let private completed (id: string) =
    AgentMessageCompleted { AgentTurnId = turn id; MessageId = message "reply"; Body = "hello" }
let private failed (id: string) =
    AgentTurnFailed { AgentTurnId = turn id; Reason = "no credential"; ProcessEnded = None }

/// Every timing a run of events gives, each event written `after` seconds past the start.
let private timed (events: (float * SessionEvent) list) : TurnTiming list =
    events
    |> List.fold
        (fun (state, soFar) (after, event) ->
            let next, timings = TurnTimings.step state (start + seconds after) event
            next, soFar @ timings)
        (TurnTimings.empty, [])
    |> snd

let tests =
    testList "Turn timings" [
        testCase "a turn that speaks and completes gives its first output and its length" <| fun () ->
            Expect.equal
                (timed [ 0.0, started "t"; 0.1, opened "t"; 2.0, word "t"; 3.0, word "t"; 9.0, completed "t" ])
                [ TurnTiming.FirstOutput (seconds 2.0); TurnTiming.Ended (TurnEnding.Completed, seconds 9.0) ]
                "the first word, once, and the end"

        // The Session opens the first message before the model has said anything, so that
        // the chat shows a turn under way. Timing that would report every turn's first
        // output as instant.
        testCase "the message the Session opens for a turn is not the turn's first output" <| fun () ->
            Expect.equal (timed [ 0.0, started "t"; 0.1, opened "t" ]) [] "nothing is timed until the model does something"

        // A runner that does not stream hands the whole reply over at the end. A person
        // watching sees nothing until then, so that is when the turn's first output is.
        testCase "a turn that completes having streamed nothing has its first output at its end" <| fun () ->
            Expect.equal
                (timed [ 0.0, started "t"; 0.1, opened "t"; 6.0, completed "t" ])
                [ TurnTiming.FirstOutput (seconds 6.0); TurnTiming.Ended (TurnEnding.Completed, seconds 6.0) ]
                "the reply is the first thing it showed"

        testCase "a turn that fails before it says anything has a length and no first output" <| fun () ->
            Expect.equal
                (timed [ 0.0, started "t"; 0.1, opened "t"; 4.0, failed "t" ])
                [ TurnTiming.Ended (TurnEnding.Failed, seconds 4.0) ]
                "a failure is an ending, and nothing was said before it"

        testCase "an interrupt ends the turn, and what the runner appends after it times nothing" <| fun () ->
            Expect.equal
                (timed
                    [ 0.0, started "t"
                      1.0, word "t"
                      5.0, AgentTurnInterrupted { AgentTurnId = turn "t"; RequestedBy = PeerId.create "ada" |> expect }
                      6.0, word "t"
                      7.0, completed "t" ])
                [ TurnTiming.FirstOutput (seconds 1.0); TurnTiming.Ended (TurnEnding.Interrupted, seconds 5.0) ]
                "one ending for one turn"

        // A session that crashed mid-turn comes back with that turn's start in its log and
        // not in this fold. The event that then ends it must not be read as a turn that
        // took no time.
        testCase "an ending for a turn this process did not start times nothing" <| fun () ->
            Expect.equal (timed [ 0.0, word "earlier"; 1.0, failed "earlier" ]) [] "only a turn seen from its start is timed"

        testCase "two turns under way at once are each timed from their own start" <| fun () ->
            Expect.equal
                (timed [ 0.0, started "a"; 10.0, started "b"; 12.0, word "b"; 13.0, word "a"; 20.0, completed "b"; 30.0, completed "a" ])
                [ TurnTiming.FirstOutput (seconds 2.0)
                  TurnTiming.FirstOutput (seconds 13.0)
                  TurnTiming.Ended (TurnEnding.Completed, seconds 10.0)
                  TurnTiming.Ended (TurnEnding.Completed, seconds 30.0) ]
                "b from its start at ten, a from its start at nought"
    ]
