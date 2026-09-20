module Yession.Tests.Entities

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
    ]
