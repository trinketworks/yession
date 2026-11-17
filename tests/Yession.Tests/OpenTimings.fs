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

        testCase "every moment has a name of its own" <| fun () ->
            let names = OpenMark.all |> List.map OpenMark.name
            Expect.equal (List.distinct names) names "two moments under one name would be one mark on the timeline"
    ]
