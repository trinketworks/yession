module Yession.Tests.Visits

// The visits to a sandbox (`SandboxVisit`, `VisitProjection`): the entries of one person that
// are open at the same time, as one visit. Also the two timeline items of a visit and the
// words that a page shows for them. These cases start nothing.

open System
open Fable.Pyxpecto
open Yession.Domain
open Yession.Domain.Sandboxes
open Yession.Domain.Chat
open Yession.Host
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

let private left (name: string) : SessionEvent = leftBecause SandboxVisit.commandEnded (Some 0) name

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

/// The timeline items of the visits in a log, as the words that a page shows.
let private lines (events: EventEnvelope<SessionEvent> list) : string list =
    let timeline, _ = TimelineProjection.applyEvents None events TimelineProjection.empty
    TimelineProjection.items ConversationProjection.empty timeline
    |> List.choose (fun (item: TimelineItem) ->
        match item with
        | TimelineVisitStarted visit -> Some (SandboxVisit.entered visit)
        | TimelineVisitEnded ended -> Some (SandboxVisit.left ended)
        | _ -> None)

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

        testCase "the words of a visit" <| fun () ->
            Expect.equal
                (lines [ at 1L 0.0 (entered "e1" dev); at 2L 12.0 (left "e1") ])
                [ "ada entered octo/hello:dev through ssh"; "ada left octo/hello:dev after 12 minutes" ]
                "who, where, how, and for how long"

        testCase "the time of a visit is from its first entry to the end of its last" <| fun () ->
            Expect.equal
                (lines
                    [ at 1L 0.0 (entered "e1" dev)
                      at 2L 10.0 (entered "e2" dev)
                      at 3L 20.0 (left "e1")
                      at 4L 90.0 (left "e2") ])
                [ "ada entered octo/hello:dev through ssh"; "ada left octo/hello:dev after 1 hour" ]
                "one line for each end of the visit"

        testCase "an end that is not an ordinary one says why" <| fun () ->
            Expect.equal
                (lines
                    [ at 1L 0.0 (entered "e1" dev)
                      at 2L 3.0 (leftBecause SandboxLeft.sessionRestarted None "e1") ])
                [ "ada entered octo/hello:dev through ssh"
                  "ada left octo/hello:dev after 3 minutes: session restarted" ]
                "the reason follows the line"

        testCase "a caller that closes the door is an ordinary end" <| fun () ->
            Expect.equal
                (lines [ at 1L 0.0 (entered "e1" dev); at 2L 2.0 (leftBecause SandboxVisit.callerClosed None "e1") ])
                [ "ada entered octo/hello:dev through ssh"; "ada left octo/hello:dev after 2 minutes" ]
                "a person who closes an SSH connection left, and the line says no more"

        testCase "the ordinary reasons are the words that the door writes" <| fun () ->
            // The door writes the reason (`EntryDoor.ending`) and the page reads it
            // (`SandboxVisit.endedAsUsual`). This case holds the two spellings together.
            let _, ended, _, _ = EntryDoor.ending false (SandboxExited 0)
            Expect.equal ended SandboxVisit.commandEnded "a command that ended by itself"
            let _, closed, _, _ = EntryDoor.ending true (SandboxExited 0)
            Expect.equal closed SandboxVisit.callerClosed "a caller that closed the door"

        testCase "a row of a sandbox says who is in it" <| fun () ->
            let proj, _ = stepped [ at 1L 0.0 (entered "e1" dev) ]
            Expect.equal
                (VisitProjection.openIn dev proj |> List.map SandboxVisit.here)
                [ "ada is here through ssh" ]
                "the sandbox is on the row, so the words do not repeat it"

        testCase "the two items of a visit stand where the visit started and ended" <| fun () ->
            let timeline, _ =
                TimelineProjection.applyEvents
                    None
                    [ at 4L 0.0 (entered "e1" dev); at 5L 0.5 (entered "e2" dev); at 7L 1.0 (left "e1"); at 9L 2.0 (left "e2") ]
                    TimelineProjection.empty
            let items = TimelineProjection.items ConversationProjection.empty timeline
            Expect.equal
                (items |> List.map (fun item -> TimelineItem.key item, EventOffset.value (TimelineItem.offset item)))
                [ "visit:4", 4L; "visit-end:9", 9L ]
                "one item at the first entry and one at the end of the last"
            Expect.isEmpty timeline.Visits.OpenVisits "and the visit is not open"
    ]
