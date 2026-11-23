module Yession.Tests.Visits

// The visits to a sandbox (`SandboxVisit`, `VisitProjection`): the entries of one person that
// are open at the same time, as one visit. Also the words that a row of a sandbox shows for
// one, and that a visit gives the conversation no item. These cases start nothing.

open System
open Fable.Pyxpecto
open Yession.Domain
open Yession.Domain.Sandboxes
open Yession.Domain.Chat
open Yession.Tests.Support

let private sessionId : SessionId = SessionId.create "visits-tests" |> expect

let private entry (name: string) : EntryId = EntryId.create name |> expect

let private dev : SandboxRef = SandboxRef.parse "octo/hello:dev" |> expect
let private web : SandboxRef = SandboxRef.parse "octo/hello:web" |> expect

let private epoch : DateTimeOffset = DateTimeOffset (2026, 11, 22, 10, 0, 0, TimeSpan.Zero)

let private enteredAs (subject: string) (label: string) (name: string) (sandbox: SandboxRef) : SessionEvent =
    SessionEvent.SandboxEntered
        { SandboxEntered.Entry = entry name
          SandboxEntered.EnteredSandbox = sandbox
          SandboxEntered.EnteredAs = subject
          SandboxEntered.ViaLabel = label
          SandboxEntered.EnteredCommand = [ "/run/yession-ssh/serve" ] }

let private entered (name: string) (sandbox: SandboxRef) : SessionEvent = enteredAs "ada" "ssh" name sandbox

let private leftBecause (reason: string) (code: int option) (name: string) : SessionEvent =
    SessionEvent.SandboxLeft
        { SandboxLeft.LeftEntry = entry name
          SandboxLeft.ExitCode = code
          SandboxLeft.EndedBecause = reason }

let private left (name: string) : SessionEvent = leftBecause "the command ended" (Some 0) name

/// One envelope at `offset`, stamped `minutes` after the epoch.
let private at (offset: int64) (minutes: float) (event: SessionEvent) : EventEnvelope<SessionEvent> =
    { EventId = EventId.fresh ()
      SessionId = sessionId
      Offset = EventOffset.create offset |> expect
      Actor = ActorRef.Session
      Timestamp = epoch.AddMinutes minutes
      Event = event }

/// Fold envelopes. The answer is the state, and each step as a short word with the offset
/// that names its visit.
let private stepped (events: EventEnvelope<SessionEvent> list) : VisitProjection * string list =
    events
    |> List.fold
        (fun (proj: VisitProjection, said: string list) (envelope: EventEnvelope<SessionEvent>) ->
            let next, step = VisitProjection.applyEvent proj envelope.Offset envelope.Timestamp envelope.Event
            match step with
            | Some (VisitBegan visit) -> next, said @ [ sprintf "began:%d" (EventOffset.value visit.VisitOffset) ]
            | Some (VisitWent ended) -> next, said @ [ sprintf "went:%d" (EventOffset.value ended.EndedVisit.VisitOffset) ]
            | None -> next, said)
        (VisitProjection.empty, [])

let tests =
    testList "The visits to a sandbox" [
        testCase "one entry is one visit, which starts and ends with it" <| fun () ->
            let proj, said = stepped [ at 1L 0.0 (entered "e1" dev); at 2L 5.0 (left "e1") ]
            Expect.equal said [ "began:1"; "went:1" ] "the entry starts the visit and ends it"
            Expect.isEmpty proj.OpenVisits "no visit is open at the end"

        testCase "entries of one person that are open at the same time are one visit" <| fun () ->
            // An editor on a remote computer opens some connections. The page says "entered"
            // one time.
            let proj, said =
                stepped
                    [ at 1L 0.0 (entered "e1" dev)
                      at 2L 0.1 (entered "e2" dev)
                      at 3L 0.2 (entered "e3" dev)
                      at 4L 1.0 (left "e1")
                      at 5L 2.0 (left "e3") ]
            Expect.equal said [ "began:1" ] "one start, and no end while one entry is open"
            Expect.equal
                (proj.OpenVisits |> List.map (fun visit -> visit.VisitEntries))
                [ [ entry "e2" ] ]
                "the visit holds the entry that is open"

        testCase "a visit ends when its last entry ends, in each order" <| fun () ->
            let _, said =
                stepped
                    [ at 1L 0.0 (entered "e1" dev)
                      at 2L 0.1 (entered "e2" dev)
                      at 3L 1.0 (left "e2")
                      at 4L 2.0 (left "e1") ]
            Expect.equal said [ "began:1"; "went:1" ] "the end of the last entry ends the visit"

        testCase "an entry after a visit ended starts a new visit" <| fun () ->
            let _, said =
                stepped
                    [ at 1L 0.0 (entered "e1" dev)
                      at 2L 1.0 (left "e1")
                      at 3L 2.0 (entered "e2" dev)
                      at 4L 3.0 (left "e2") ]
            Expect.equal said [ "began:1"; "went:1"; "began:3"; "went:3" ] "two visits that do not touch"

        testCase "a different person, sandbox or label is a different visit" <| fun () ->
            let proj, said =
                stepped
                    [ at 1L 0.0 (entered "e1" dev)
                      at 2L 0.1 (enteredAs "bob" "ssh" "e2" dev)
                      at 3L 0.2 (entered "e3" web)
                      at 4L 0.3 (enteredAs "ada" "console" "e4" dev) ]
            Expect.equal said [ "began:1"; "began:2"; "began:3"; "began:4" ] "four visits"
            Expect.equal
                (VisitProjection.openIn dev proj |> List.map (fun visit -> visit.Visitor, visit.VisitorCame))
                [ "ada", "ssh"; "bob", "ssh"; "ada", "console" ]
                "who is in one sandbox, oldest first"
            Expect.equal
                (VisitProjection.openIn web proj |> List.map (fun visit -> visit.Visitor))
                [ "ada" ]
                "and in the other one"

        testCase "a replay of a page changes nothing" <| fun () ->
            let proj, said =
                stepped
                    [ at 1L 0.0 (entered "e1" dev)
                      at 1L 0.0 (entered "e1" dev)
                      at 2L 1.0 (left "e1")
                      at 2L 1.0 (left "e1")
                      at 3L 2.0 (left "never-started") ]
            Expect.equal said [ "began:1"; "went:1" ] "an entry that starts two times is one, and an end with no start is nothing"
            Expect.isEmpty proj.OpenVisits "nothing is open"

        testCase "a row of a sandbox says who is in it" <| fun () ->
            let proj, _ = stepped [ at 1L 0.0 (entered "e1" dev) ]
            Expect.equal
                (VisitProjection.openIn dev proj |> List.map SandboxVisit.here)
                [ "ada is here through ssh" ]
                "the sandbox is on the row, so the words do not repeat it"

        testCase "a visit gives the conversation no item, and the sandbox knows who is in it" <| fun () ->
            let folded (events: EventEnvelope<SessionEvent> list) : TimelineProjection =
                TimelineProjection.applyEvents None events TimelineProjection.empty |> fst
            let during = folded [ at 4L 0.0 (entered "e1" dev) ]
            Expect.isEmpty (TimelineProjection.items ConversationProjection.empty during) "an entry is not a line"
            Expect.equal
                (VisitProjection.openIn dev during.Visits |> List.map (fun visit -> visit.Visitor))
                [ "ada" ]
                "the visit is open on the sandbox"
            let after = folded [ at 4L 0.0 (entered "e1" dev); at 9L 2.0 (left "e1") ]
            Expect.isEmpty (TimelineProjection.items ConversationProjection.empty after) "nor is the end of one"
            Expect.isEmpty after.Visits.OpenVisits "and the visit is not open"
    ]
