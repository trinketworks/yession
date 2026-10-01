module Yession.Tests.Entities

open System
open Fable.Pyxpecto
open Yession.Domain

let private expect =
    function
    | Ok v -> v
    | Error e -> failwith e

let private ada = UserId.create "ada" |> expect
let private hello = RepoRef.create "octo/hello" |> expect
let private github = ConnectionName.create "github" |> expect

let tests =
    testList "Entities (what a sentence points at)" [
        // The prose reader adds nothing between segments: the fold put the spaces where it
        // wanted them, and a reader that inserted its own would disagree with the screen
        // rendering the same segments.
        testCase "a phrase is said as its segments, concatenated" <| fun () ->
            let phrase =
                [ Segment.Text "pushed to "
                  Segment.Ref (EntityRef.Repo hello)
                  Segment.Text " on behalf of "
                  Segment.Ref (EntityRef.Actor (UserRef ada)) ]
            Expect.equal (Phrase.said phrase) "pushed to github:octo/hello on behalf of user:ada" "each ref in its prose spelling, text verbatim"

        testCase "a phrase of text alone says exactly that text" <| fun () ->
            Expect.equal (Phrase.said (Phrase.text "removed repo octo/hello")) "removed repo octo/hello" "no decoration"

        testCase "an actor is said as its token" <| fun () ->
            Expect.equal (EntityRef.said (EntityRef.Actor ActorRef.Agent)) "agent" "the agent"
            Expect.equal (EntityRef.said (EntityRef.Actor (UserRef ada))) "user:ada" "a user, by subject"

        testCase "a connection is said by its name" <| fun () ->
            Expect.equal (EntityRef.said (EntityRef.Connection github)) "github" "the name, nothing else"

        // The hardening under the spelling: whatever prose says a repo is, the tool that
        // takes a repo accepts. An agent reads "github:octo/hello" and writes it into
        // `add_repo`; a refusal there would be this repository's own spelling turned
        // against it.
        testCase "the prose spelling of a repo is one add_repo accepts" <| fun () ->
            let spoken = EntityRef.said (EntityRef.Repo hello)
            Expect.equal (RepoRef.create spoken |> expect) hello "round-trips through the parser"

        testCase "a repo pasted with the github: prefix is the same repo" <| fun () ->
            Expect.equal (RepoRef.create "github:octo/hello" |> expect) hello "prefix stripped"
            Expect.equal (RepoRef.create "  github:octo/hello.git " |> expect) hello "beside the other tolerances"

        testCase "the refs of a phrase come back in order" <| fun () ->
            let phrase =
                [ Segment.Ref (EntityRef.Connection github)
                  Segment.Text " from "
                  Segment.Ref (EntityRef.Actor (UserRef ada)) ]
            Expect.equal (Phrase.refs phrase) [ EntityRef.Connection github; EntityRef.Actor (UserRef ada) ] "two refs, fold order"

        // A name is compared, so two spellings of one connection have to be one value.
        testCase "a connection name is case-folded and trimmed" <| fun () ->
            Expect.equal (ConnectionName.create " GitHub " |> expect) github "GitHub is github"

        testCase "a connection name cannot be blank" <| fun () ->
            Expect.isError (ConnectionName.create "  ") "blank refused"

        // Two spellings of one stretch, off one ladder. What matters is that they agree about
        // WHICH unit a span is in — a break reading "7 hours later" beside a sentence saying
        // "stopped for 8h" would have a reader doubting both.
        testCase "a stretch is spelled terse and in words, and the two agree on the unit" <| fun () ->
            let cases =
                [ TimeSpan.FromHours 7.0, "7h", "7 hours"
                  TimeSpan.FromHours 1.0, "1h", "1 hour"
                  TimeSpan.FromMinutes 25.0, "25m", "25 minutes"
                  TimeSpan.FromMinutes 1.0, "1m", "1 minute"
                  TimeSpan.FromDays 3.0, "3d", "3 days"
                  TimeSpan.FromDays 1.0, "1d", "1 day" ]
            for span, terse, words in cases do
                Expect.equal (Elapsed.describe span) terse (sprintf "terse, for %O" span)
                Expect.equal (Elapsed.inWords span) words (sprintf "in words, for %O" span)

        // Rounding a short stretch to nothing would have a resumed session claim it was never
        // away, which is the one thing the break exists to say.
        testCase "a stretch shorter than a minute is still a minute" <| fun () ->
            Expect.equal (Elapsed.describe (TimeSpan.FromSeconds 4.0)) "1m" "never 0m"
            Expect.equal (Elapsed.inWords (TimeSpan.FromSeconds 4.0)) "1 minute" "nor no minutes"

        // One spelling of a moment, because the screen shows a person the same string the
        // agent was given — UTC and to the minute, so neither has to translate.
        testCase "a moment is spelled to the minute in UTC" <| fun () ->
            Expect.equal
                (Moment.stamp (DateTimeOffset (2026, 9, 25, 13, 5, 30, TimeSpan.Zero)))
                "2026-09-25 13:05 UTC"
                "seconds dropped, zone named"

        // An offset is not a zone the reader has to reason about: the same instant written two
        // ways is one string.
        testCase "a moment is the same however its offset is written" <| fun () ->
            Expect.equal
                (Moment.stamp (DateTimeOffset (2026, 9, 25, 23, 5, 0, TimeSpan.FromHours 10.0)))
                (Moment.stamp (DateTimeOffset (2026, 9, 25, 13, 5, 0, TimeSpan.Zero)))
                "one instant, one spelling"
    ]
