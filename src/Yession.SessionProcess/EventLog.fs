namespace Yession.SessionProcess

open System
open Yession.Domain

/// Function-shaped event-log capabilities. The Session Process is the only caller of
/// these. See docs/design.md §1 ("Durable facts are events", "Composition at the top").
///
/// `ReadEvents` returns a single page of at most `limit` events after the given offset.
/// Callers page by re-reading from `page.LastOffset` until `page.IsEnd`. This single-page
/// shape (rather than a stream) keeps the capability portable to the Fable/Node runtime
/// and mirrors the `ReadEventsAfter(after, limit)` / `EventsPage` transport frames.
type AppendEvent<'event> = ActorRef -> 'event -> Async<AppendResult>
type ReadEvents<'event> = EventOffset option -> int -> Async<EventPage<'event>>

/// The append-only event log, with its storage implementation hidden. Callers depend on
/// these functions, never on the representation.
type EventLog<'event> =
    { Append : AppendEvent<'event>
      Read   : ReadEvents<'event> }

/// One page of a log kept in a list where an envelope's OFFSET IS ITS INDEX.
///
/// Both stores guarantee that the same way and say so where they do it: `append` assigns
/// `events.Count` as the offset, and nothing is ever removed, so the nth envelope is the one
/// at offset n. That is what makes a page a SLICE — taken by position, costing what the page
/// holds — and it lives here rather than in either store because it is one rule and there are
/// two of them (`InMemoryEventLog` below, `Yession.Host.EventStore` over a JSONL file), which
/// replayed it in two copies.
///
/// Both copies walked the WHOLE log and then threw away all but the first `limit`:
/// `Seq.filter (offset > after) |> Seq.toArray |> Array.truncate limit`. Correct, and
/// quadratic in exactly the case the surface exists for — a client catching up asks for every
/// page in turn, so a 20,000-event log cost 208 reads averaging 10,000 copied envelopes
/// apiece, about two million, per reader, per cold open. On the laptop that is ~1.9s of a
/// cold open spent walking a list the reader is already most of the way down.
///
/// A page off the end is empty rather than an error: a cursor at the head is a caller who is
/// current, which is the ordinary steady state and not a mistake.
module EventPaging =

    let page (events: ResizeArray<EventEnvelope<'event>>) (after: EventOffset option) (limit: int) : EventPage<'event> =
        let from =
            match after with
            | Some offset -> int (EventOffset.value offset) + 1
            | None -> 0
        let from = max 0 from
        let count = max 0 (min limit (events.Count - from))
        let pageEvents = List.init count (fun i -> events.[from + i])
        { Events = pageEvents
          LastOffset = if count = 0 then None else Some events.[from + count - 1].Offset
          // The page is the tail when it holds everything still available after `after`.
          IsEnd = from + count >= events.Count }

/// An in-memory event log. Phase 1 storage; the `EventLog` interface it returns does not
/// expose the in-memory representation, so storage is replaceable without changing callers.
///
/// Works on both .NET (where appends may race across threads, guarded by a lock) and the
/// single-threaded Fable/Node runtime (where the lock is unnecessary and elided).
module InMemoryEventLog =

    [<Literal>]
    let DefaultPageSize = 100

    /// Run `f` under the gate. On .NET this is a real lock so concurrent appends stay
    /// monotonic; under Fable (single-threaded JS) no lock is needed.
    let inline private withLock (gate: obj) (f: unit -> 'a) : 'a =
#if FABLE_COMPILER
        ignore gate
        f ()
#else
        lock gate f
#endif

    /// Create an append-only, in-memory event log for a single session.
    ///
    /// - `clock` stamps each appended envelope (injected so time stays at the boundary).
    let create
        (sessionId: SessionId)
        (clock: unit -> DateTimeOffset)
        : EventLog<'event> =

        let gate = obj ()
        let events = ResizeArray<EventEnvelope<'event>>()

        let append (actor: ActorRef) (event: 'event) : Async<AppendResult> =
            async {
                return
                    withLock gate (fun () ->
                        // Append-only: the next offset is the current count. Monotonicity
                        // follows directly because events are never removed.
                        let offset =
                            match EventOffset.create (int64 events.Count) with
                            | Ok o -> o
                            | Error e -> failwithf "event log offset invariant violated: %s" e

                        let envelope =
                            { EventId = EventId.fresh ()
                              SessionId = sessionId
                              Offset = offset
                              Actor = actor
                              Timestamp = clock ()
                              Event = event }

                        events.Add envelope
                        { Offset = offset })
            }

        let read (after: EventOffset option) (limit: int) : Async<EventPage<'event>> =
            async {
                // Under the gate so the page is deterministic against the log state observed
                // at read time — and taken by position, which is `EventPaging.page`'s whole
                // subject: the offset assigned two functions up IS the index.
                return withLock gate (fun () -> EventPaging.page events after limit)
            }

        { Append = append
          Read = read }
