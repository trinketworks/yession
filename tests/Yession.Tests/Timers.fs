module Yession.Tests.Timers

// The waits the client model declares (`Timer`, `ClientModel.timers`), and the first of them:
// the agent's writing going quiet. Cheap tier throughout. The rule is pure — a stamp, a
// comparison, a list of wanted timers — and where it needs the program around it, the real
// Elmish program runs on a clock this file turns by hand, so no case waits on real time.

open System
open Fable.Pyxpecto
open Yjs
open Yession.Domain
open Yession.Domain.Agent
open Yession.App
open Yession.Tests.Support
open Yession.Peer

/// A clock turned by hand: `Advance` fires every live wait that falls due, in order, each at
/// its own time — including one armed by a wait that fired earlier in the same advance.
type private ManualClock () =
    let mutable now = 0
    let armed = ResizeArray<int * (unit -> unit) * bool ref> ()
    member _.Clock : Timer.Clock =
        fun after callback ->
            let live = ref true
            armed.Add ((now + after, callback, live))
            { new IDisposable with
                member _.Dispose () = live.Value <- false }
    member _.Advance (ms: int) =
        let until = now + ms
        let rec next () =
            let due =
                armed
                |> Seq.filter (fun (at, _, live) -> live.Value && at <= until)
                |> Seq.sortBy (fun (at, _, _) -> at)
                |> Seq.tryHead
            match due with
            | Some (at, callback, live) ->
                now <- at
                live.Value <- false
                callback ()
                next ()
            | None -> now <- until
        next ()

let private sessionId = SessionId.create "timers" |> expect
let private turn = AgentTurnId.create "turn-quiet" |> expect
let private writing = MessageId.create "msg-writing" |> expect
let private asked = MessageId.create "msg-asked" |> expect

let private envelope (offset: int64) (event: SessionEvent) : EventEnvelope<SessionEvent> =
    { EventId = EventId.fresh ()
      SessionId = sessionId
      Offset = EventOffset.create offset |> expect
      Actor = ActorRef.Agent
      Timestamp = DateTimeOffset (2026, 9, 28, 0, 0, 0, TimeSpan.Zero)
      Event = event }

let private page (events: EventEnvelope<SessionEvent> list) : ClientMsg =
    EventsPageMsg
        { Events = events
          LastOffset = events |> List.tryLast |> Option.map (fun e -> e.Offset)
          IsEnd = true }

/// A turn begun and a message opened in it, nothing said yet.
let private opened : ClientMsg =
    page
        [ envelope 1L (AgentTurnStarted { AgentTurnId = turn; Cause = TurnCause.TriggeredBy asked })
          envelope 2L (AgentMessageStarted { AgentTurnId = turn; MessageId = writing; Antecedent = None }) ]

let private delta (offset: int64) (text: string) : ClientMsg =
    page [ envelope offset (AgentMessageDelta { AgentTurnId = turn; MessageId = writing; Delta = text }) ]

let private completed (offset: int64) (body: string) : ClientMsg =
    page [ envelope offset (AgentMessageCompleted { AgentTurnId = turn; MessageId = writing; Body = body }) ]

let private fold (msgs: ClientMsg list) : ClientModel =
    msgs |> List.fold (fun model msg -> Support.step msg model) (ClientModel.init (peer "ada" "Ada"))

let private thinking (model: ClientModel) : bool =
    match model.Conversation.Items |> List.tryFind (fun item -> item.MessageId = writing) with
    | Some item -> ClientModel.agentThinking model item
    | None -> failwith "the message being written is not in the conversation"

/// The real client program, with the model's timers running on a hand-turned clock.
let private program () =
    let clock = ManualClock ()
    let runner = Harness.run (Client.makeProgram Client.Ports.offline (Y.Doc.Create ()) (ClientModel.init (peer "ada" "Ada")) |> Client.withTimers clock.Clock)
    clock, (fun msg -> runner.Dispatch (user msg)), runner.Model

/// The real client program over a clipboard that answers `written` — the platform's half of a
/// copy, which only it can know.
let private copying (written: bool) =
    let ports = { Client.Ports.offline with Client.Ports.Clipboard = fun _ -> async.Return written }
    let runner = Harness.run (Client.makeProgram ports (Y.Doc.Create ()) (ClientModel.init (peer "ada" "Ada")))
    (fun msg -> runner.Dispatch (user msg)), runner.Model

/// The program with a message open and nothing said in it yet.
let private running () =
    let clock, send, model = program ()
    send opened
    clock, send, fun () -> thinking (model ())

let private available (offset: int64) : ClientMsg = EventsAvailableMsg (EventOffset.create offset |> expect)

let tests =
    testList "Timers" [
        testCase "before its first word, the agent reads as thinking" <| fun () ->
            Expect.isTrue (thinking (fold [ opened ])) "an empty message is a turn that has said nothing yet"

        testCase "while its words are arriving, the agent reads as writing" <| fun () ->
            Expect.isFalse (thinking (fold [ opened; delta 3L "Looking" ])) "a message with words just in is being written"

        testCase "writing that sits still for the quiet interval reads as thinking" <| fun () ->
            let clock, send, isThinking = running ()
            send (delta 3L "Looking")
            clock.Advance ClientModel.writingQuietMs
            Expect.isTrue (isThinking ()) "words that stopped arriving are a pause, not a hang"

        testCase "words arriving closer together than the quiet interval never read as thinking" <| fun () ->
            let clock, send, isThinking = running ()
            let gap = ClientModel.writingQuietMs - 100
            send (delta 3L "Looking")
            clock.Advance gap
            send (delta 4L " at")
            clock.Advance gap
            send (delta 5L " it")
            clock.Advance gap
            Expect.isFalse (isThinking ()) "each word restarts the wait, so steady writing is never a pause"

        // The other half of a debounce: each word restarts the wait rather than the first word
        // starting the only one, so a pause after a run of words is still found.
        testCase "the quiet interval is measured from the last word" <| fun () ->
            let clock, send, isThinking = running ()
            let gap = ClientModel.writingQuietMs - 100
            send (delta 3L "Looking")
            clock.Advance gap
            send (delta 4L " at it")
            clock.Advance ClientModel.writingQuietMs
            Expect.isTrue (isThinking ()) "a pause after the second word is a pause"

        testCase "a word after a pause reads as writing again" <| fun () ->
            let clock, send, isThinking = running ()
            send (delta 3L "Looking")
            clock.Advance ClientModel.writingQuietMs
            send (delta 4L " at it")
            Expect.isFalse (isThinking ()) "the pause ends the moment the agent says something"

        // The wait fired for words that are no longer the latest: without the stamp, a pause
        // measured before a word landed would be announced after it.
        testCase "a quiet measured before the latest words cannot make them read as a pause" <| fun () ->
            let before = fold [ opened; delta 3L "Looking" ]
            let stale = ClientModel.timers before |> List.map (fun timer -> timer.Fire)
            let after = List.fold (fun model msg -> Support.step msg model) (Support.step (delta 4L " at it") before) stale
            Expect.isFalse (thinking after) "the quiet belonged to the shorter body"

        testCase "writing already found quiet asks for no second wait" <| fun () ->
            let quiet =
                let model = fold [ opened; delta 3L "Looking" ]
                ClientModel.timers model |> List.fold (fun model timer -> Support.step timer.Fire model) model
            Expect.isEmpty (ClientModel.timers quiet) "a settled question is not asked again"

        testCase "catch-up that runs past its quiet interval is reported as slow" <| fun () ->
            let clock, send, model = program ()
            send (available 2L)
            clock.Advance ClientModel.catchUpQuietMs
            Expect.isTrue (model ()).EventConsumer.CatchUpIsSlow "a wait that lasted is a wait worth saying"

        testCase "catch-up that ends inside its quiet interval is never reported" <| fun () ->
            let clock, send, model = program ()
            send (available 2L)
            clock.Advance (ClientModel.catchUpQuietMs / 2)
            send opened
            clock.Advance ClientModel.catchUpQuietMs
            Expect.isFalse (model ()).EventConsumer.CatchUpIsSlow "a send's round trip is not news"

        // One wait for the whole episode: a page that lands while more is still to come is
        // progress, and restarting the wait on each would keep a long catch-up from ever
        // being reported.
        testCase "pages landing during catch-up do not push its report out" <| fun () ->
            let clock, send, model = program ()
            send (available 4L)
            clock.Advance (ClientModel.catchUpQuietMs / 2)
            send opened
            clock.Advance (ClientModel.catchUpQuietMs / 2)
            Expect.isTrue (model ()).EventConsumer.CatchUpIsSlow "still behind at the interval, however many pages came"

        testCase "a copy's confirmation is taken back after its moment" <| fun () ->
            let clock, send, model = program ()
            send (CopiedMsg (Some "data-box"))
            clock.Advance ClientModel.copiedShownMs
            Expect.isNone (model ()).Copied "\"just now\" stops being true"

        // Each copy is a moment of its own: without that, the first copy's deadline would take
        // the second copy's confirmation off the screen part-way through it.
        testCase "copying the same box again restarts its moment" <| fun () ->
            let clock, send, model = program ()
            let most = ClientModel.copiedShownMs * 2 / 3
            send (CopiedMsg (Some "data-box"))
            clock.Advance most
            send (CopiedMsg (Some "data-box"))
            clock.Advance most
            Expect.isSome (model ()).Copied "the second copy is shown for as long as the first was"

        testCase "a queue delete armed by one press is taken back if nobody confirms it" <| fun () ->
            let clock, send, model = program ()
            let queueId = QueueId.create "q-armed" |> expect
            send (ArmQueueDeleteMsg (Some queueId))
            clock.Advance ClientModel.queueDeleteArmedMs
            Expect.isNone (model ()).QueueDeleteArmed "an unconfirmed press does not stay armed forever"

        // Same one-slot rule `ItemMenu` uses: a second entry armed is the first disarmed, so
        // at most one delete in the queue is ever one press from happening.
        testCase "arming a different entry replaces whatever was armed before it" <| fun () ->
            let _, send, model = program ()
            let first = QueueId.create "q-first" |> expect
            let second = QueueId.create "q-second" |> expect
            send (ArmQueueDeleteMsg (Some first))
            send (ArmQueueDeleteMsg (Some second))
            Expect.equal (model ()).QueueDeleteArmed (Some second) "one slot, like the item menu"

        testCase "a confirmed delete clears the armed slot along with the entry" <| fun () ->
            let _, send, model = program ()
            let queueId = QueueId.create "q-confirmed" |> expect
            send (ArmQueueDeleteMsg (Some queueId))
            send (DeleteQueuedMsg queueId)
            Expect.isNone (model ()).QueueDeleteArmed "nothing left for the armed id to mean"

        testCase "a copy the clipboard took is confirmed on the box it came from" <| fun () ->
            let send, model = copying true
            send (CopyMsg ("data-box", "ABCD-1234"))
            Expect.equal ((model ()).Copied |> Option.map (fun copy -> copy.Box)) (Some "data-box") "said where the reader is looking"

        testCase "a copy the clipboard refused is never confirmed" <| fun () ->
            // A "copied" over an empty clipboard would send the reader to the other tab with
            // nothing to paste; the box goes on showing the value instead.
            let send, model = copying false
            send (CopyMsg ("data-box", "ABCD-1234"))
            Expect.isNone (model ()).Copied "nothing to confirm"

        testCase "a panel's wait on the query is refused once its deadline passes" <| fun () ->
            let clock, send, model = program ()
            send (ClaudePendingMsg (Pending.Awaiting ({ Scope = "mine"; Connected = true }, 1_000L)))
            clock.Advance (int Pending.deadlineMillis)
            Expect.equal (model ()).Claude.Pending (Pending.Refused Pending.unseen) "a status that never came is said, not waited on for ever"

        testCase "a wait the query answers in time is never refused" <| fun () ->
            let clock, send, model = program ()
            send (ClaudePendingMsg (Pending.Awaiting ({ Scope = "mine"; Connected = true }, 1_000L)))
            clock.Advance (int Pending.deadlineMillis / 2)
            send (ClaudePendingMsg Pending.Ready)
            clock.Advance (int Pending.deadlineMillis)
            Expect.equal (model ()).Claude.Pending Pending.Ready "an answered wait keeps its answer"

        testCase "a finished message asks for no wait" <| fun () ->
            Expect.isEmpty
                (ClientModel.timers (fold [ opened; delta 3L "Looking"; completed 4L "Looking" ]))
                "nothing is being written, so there is no quiet to wait for"
    ]
