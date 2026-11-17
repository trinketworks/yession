module Yession.Tests.OpenTimings

// When each moment of a page's opening was reached, read off the model the page holds and
// the clock reading the page hands in. Pure, so every case writes its own models and its
// own times: no page opens and nothing waits.

open Fable.Pyxpecto
open Yession.Domain
open Yession.App
open Yession.Tests.Support

let private sessionId = SessionId.create "open-timings" |> expect
let private ada = PeerId.create "ada" |> expect

let private fresh = ClientModel.init { PeerId = ada; DisplayName = "swift-heron" }

let private taken (latest: int64 option) =
    ConnectedMsg
        { SessionId = sessionId
          AssignedDisplayName = "swift-heron"
          LatestOffset = latest |> Option.map (fun n -> EventOffset.create n |> expect) }

let private nothingKept : ClientMsg =
    LocalHistoryMsg ({ Events = []; LastOffset = None; IsEnd = true }, None)

let tests =
    testList "Open timings" [
        testCase "a page that has just loaded has reached nothing the model can say" <| fun () ->
            Expect.equal (OpenTimings.ofModel fresh) [] "its store has not answered and its session has not either"

        testCase "the store's answer is a moment, whatever it answered" <| fun () ->
            Expect.equal
                (fresh |> Support.step nothingKept |> OpenTimings.ofModel)
                [ OpenMark.StoreAnswered ]
                "a store that kept nothing has still answered"

        testCase "a session with more log than the page has folded has taken it, and is not read through" <| fun () ->
            Expect.equal
                (fresh |> Support.step (taken (Some 9L)) |> OpenTimings.ofModel)
                [ OpenMark.Accepted ]
                "the log is still to read"

        testCase "a session whose log is empty is read through the moment it takes the page" <| fun () ->
            Expect.equal
                (fresh |> Support.step (taken None) |> OpenTimings.ofModel)
                [ OpenMark.Accepted; OpenMark.ReadThrough ]
                "there is nothing to read"

        testCase "a moment keeps the time it was first reached at" <| fun () ->
            let first, _ = OpenTimings.step OpenTimings.empty 120.0 [ OpenMark.Booted ]
            let second, _ = OpenTimings.step first 480.0 [ OpenMark.Booted; OpenMark.Accepted ]
            Expect.equal
                (OpenTimings.reached second)
                [ OpenMark.Booted, 120.0; OpenMark.Accepted, 480.0 ]
                "a mark that stands reached again is not reached again"

        testCase "only the moments reached for the first time are news" <| fun () ->
            let first, _ = OpenTimings.step OpenTimings.empty 120.0 [ OpenMark.Booted ]
            let _, news = OpenTimings.step first 480.0 [ OpenMark.Booted; OpenMark.Accepted ]
            Expect.equal news [ OpenMark.Accepted, 480.0 ] "what the page writes on its timeline, once"

        testCase "an opening is over once the timeline is shown and the log is read through" <| fun () ->
            let shown, _ = OpenTimings.step OpenTimings.empty 300.0 [ OpenMark.Shown ]
            let both, _ = OpenTimings.step shown 900.0 [ OpenMark.ReadThrough ]
            Expect.equal
                (OpenTimings.complete OpenTimings.empty, OpenTimings.complete shown, OpenTimings.complete both)
                (false, false, true)
                "a kept copy on the screen is not the whole of it"

        testCase "a page that has reached nothing has nothing to report" <| fun () ->
            Expect.equal (OpenTimings.report OpenTimings.empty 50.0) None "no moment, no report"

        testCase "a report reads back as it was written" <| fun () ->
            let report =
                { Marks = [ OpenMark.Responded, 41.5; OpenMark.Booted, 320.0; OpenMark.Shown, 900.25 ]
                  SentAt = 901.0 }
            Expect.equal (OpenReport.toJson report |> OpenReport.ofJson) (Ok report) "every moment, at its time"

        testCase "a moment this build does not know is refused" <| fun () ->
            Expect.isError
                (OpenReport.ofJson """{"marks":[{"mark":"teleported","at":1}],"sentAt":2}""")
                "the set of names is closed"

        testCase "a moment reported twice is refused" <| fun () ->
            Expect.isError
                (OpenReport.ofJson """{"marks":[{"mark":"booted","at":1},{"mark":"booted","at":2}],"sentAt":3}""")
                "a moment is reached once"

        testCase "a moment later than the report itself is refused" <| fun () ->
            Expect.isError
                (OpenReport.ofJson """{"marks":[{"mark":"booted","at":9}],"sentAt":3}""")
                "nothing is reached after it is told of"

        testCase "a moment before the navigation is refused" <| fun () ->
            Expect.isError
                (OpenReport.ofJson """{"marks":[{"mark":"booted","at":-1}],"sentAt":3}""")
                "the navigation is where the count starts"

        testCase "a report sent hours into an opening is refused" <| fun () ->
            Expect.isError
                (OpenReport.ofJson (sprintf """{"marks":[{"mark":"booted","at":1}],"sentAt":%f}""" (OpenReport.longestMs + 1.0)))
                "that is a fault, not a duration"

        testCase "a report of no moment is refused" <| fun () ->
            Expect.isError (OpenReport.ofJson """{"marks":[],"sentAt":3}""") "there is nothing in it to record"

        testCase "every moment has a name of its own" <| fun () ->
            let names = OpenMark.all |> List.map OpenMark.name
            Expect.equal (List.distinct names) names "two moments under one name would be one mark on the timeline"
    ]
