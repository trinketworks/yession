namespace Yession.Domain

/// A page of events read from the log. The page is the unit of deterministic, offset-based
/// reads. See docs/technical-design.md §1.
type EventPage<'event> =
    { Events     : EventEnvelope<'event> list
      LastOffset : EventOffset option
      IsEnd      : bool }

/// The result of appending an event: the monotonic offset assigned to it.
type AppendResult =
    { Offset : EventOffset }

/// How much of the append-only log one HTTP answer carries (Plan 20).
///
/// The log is read by CURSOR: a client sends the offset it has folded through and the
/// server answers at an address naming the bounds it chose — `events/{first}-{last}`.
/// Those bounds never move, so that answer is the same bytes for ever and a client can
/// keep it, tail included. The client computes none of this: it holds an offset and
/// stores what it is given under the address it was given.
///
/// This module used to map offsets to fixed chunk indices, and the index was the problem:
/// `events/3` meant *whatever chunk 3 holds now*, which grows, so the newest events could
/// never be kept by anyone.
///
/// Two nearer alternatives were rejected for one reason each. Prefix ranges (`0-99`, `0-136`,
/// `0-137` …) are equally immutable and store every event once per length at which it was ever
/// observed; disjoint ranges cost each event exactly once, because each cursor request begins
/// one past the last event folded. A `Link: rel=next` chain with a mutable `/events/head`
/// entry point duplicated what the answers already carry — their own offsets, which is what a
/// replay orders by — and what a cursor answers for free with `204`.
module EventChunk =

    /// The most events one answer carries. Server-side only — no client ever names a
    /// range, so nothing outside the session process needs to know this number.
    let size = 100

    /// How many addresses one answer to "what is ahead of me" names.
    ///
    /// The cursor is a LINKED LIST: the address of the next answer is knowable only from the
    /// last one, because the tiling begins wherever the reader did — `after/137` answers
    /// `138-237`, not `100-199` — so a client cannot run two reads at once and cannot compute
    /// one ahead. That costs a round trip per hundred events, which on a real session of
    /// 20,650 over a 30ms link was 416 requests and 15.6 seconds of a cold open spent waiting.
    ///
    /// Said in one breath instead, the same ranges fetched at once take 1.5s. Six at a time
    /// is as fast as twenty-four — HTTP/1.1's per-origin cap is already more concurrency than
    /// the saving needs — so this number is not about parallelism width. It bounds the ANSWER:
    /// at 64 a session of any length is a handful of these rather than one reply naming ten
    /// thousand addresses, and a client that wants more asks again from where it got to.
    let ahead = 64

    /// The addresses between a cursor and the head, in order, at most `ahead` of them.
    ///
    /// Arithmetic rather than a read, which it can be because an offset IS an index in both
    /// stores (`EventPaging.page` is where that is established and why). The bounds it names
    /// are the bounds the cursor would have chosen one at a time, so a client that mixes the
    /// two keeps one copy of each answer rather than two tilings of the same events.
    ///
    /// The last address may be SHORT, and is still immutable: an address names the events it
    /// names, the server refuses a range it cannot fill exactly (404 rather than a partial
    /// answer), and a reader that comes back later asks from its own cursor and is given a
    /// fresh address for what has arrived since. A partial tail is not a tile waiting to be
    /// filled in.
    let ranges (after: EventOffset option) (head: EventOffset option) : (int64 * int64) list =
        match head with
        | None -> []
        | Some head ->
            let head = EventOffset.value head
            let first =
                match after with
                | Some o -> EventOffset.value o + 1L
                | None -> 0L
            if first > head then []
            else
                [ for i in 0 .. ahead - 1 do
                    let from = first + int64 i * int64 size
                    if from <= head then
                        yield from, min (from + int64 size - 1L) head ]
