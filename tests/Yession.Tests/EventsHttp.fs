module Yession.Tests.EventsHttp

// The event log over HTTP, read by CURSOR (Plan 20): a client sends the offset it
// has folded through (`GET /events/after/{n}?token=…`) and the server redirects to the
// range it chose (`/events/{first}-{last}`), whose bytes never change because its bounds
// do not. Verified here: that a range keeps answering the same bytes after the log has
// grown past it (the tail is the case the old fixed-chunk scheme could not hold), that a
// range the log has not reached is a 404 rather than a short answer, that a current caller
// gets `204`, token gating on both halves, and a full client consuming the log over the
// HTTP fetcher instead of frames.

open System
open Fable.Core
open Fable.Pyxpecto
open Ylmish
open Yession.Domain
open Yession.App
open Yession.SessionProcess
open Yession.Host
open Yession.Tests.Support
open Yession.Peer

// The chunk GET shaped as `Client.HttpGet` is — total, with the status on a refusal. What
// the browser's port does with the same answer is in app/browser/Browser.fs; failure
// classification is covered in Resilience.fs, so here it only has to be the real thing.
let private chunkGet : Client.HttpGet =
    fun url ->
        async {
            match! TestHttp.attempt url with
            | Error reason -> return Error (Client.HttpUnreachable reason)
            | Ok reply when TestHttp.ok reply -> return Ok { Url = reply.Url; Body = reply.Body }
            | Ok reply -> return Error (Client.HttpStatus reply.Status)
        }

let private endpointTests =
    testList "HTTP endpoint" [
        testCaseAsync "a cursor redirects to a range whose bytes never change, token-gated" <|
            async {
                let! h = Host.start (SessionId.create "events-http" |> expect) 0
                let mintedToken = h.MintPeerToken ()
                let append (n: int) =
                    async {
                        for i in 1 .. n do
                            let! _ =
                                h.Log.Append
                                    ActorRef.SessionProcess
                                    (PeerJoined
                                        { PeerId = PeerId.create (sprintf "p-%d-%d" n i) |> expect
                                          DisplayName = "filler"
                                          User = None })
                            ()
                    }
                // Two answers' worth plus a five-event tail: the tail is the whole point,
                // because it is what a fixed chunk index could never give an address to.
                do! append (2 * EventChunk.size + 5)
                let at (route: SessionRoute) (token: string) =
                    sprintf "%s?token=%s" (SessionRoute.at (sprintf "http://127.0.0.1:%d" h.Port) route) token

                // The cursor itself: no events, never cached, and it says where to look.
                let! start = TestHttp.getUnredirected [] (at (EventsAfter None) mintedToken)
                Expect.equal start.Status 307 "a cursor redirects rather than answering"
                Expect.equal (TestHttp.requiredHeader "cache-control" start) "no-store" "where the events are is a thing that moves"
                Expect.stringContains (TestHttp.requiredHeader "location" start) (sprintf "events/0-%d" (EventChunk.size - 1)) "to the first range"
                Expect.stringContains (TestHttp.requiredHeader "location" start) "token=" "carrying the token, which a redirect would otherwise drop"

                // Following it lands on the events.
                let! first = TestHttp.get (at (EventsAfter None) mintedToken)
                Expect.equal first.Status 200 "the range serves"
                Expect.equal (TestHttp.requiredHeader "cache-control" first) "no-store" "the client keeps this, not the HTTP cache"
                let lines (body: string) = body.Split '\n' |> Array.filter (fun l -> l.Trim().Length > 0)
                Expect.equal (lines first.Body).Length EventChunk.size "one answer's worth"
                let decoded = Codec.fromString Codec.sessionEventEnvelope (lines first.Body).[0] |> expect
                Expect.equal (EventOffset.value decoded.Offset) 0L "starting at the beginning"

                // The tail, at an address of its own — and still five events after the log
                // has grown past it. THIS is what the chunk index could not do: `events/2`
                // meant "whatever chunk 2 holds now", so the newest events were unkeepable.
                let tailFirst = int64 (2 * EventChunk.size)
                let tailRange = Events (tailFirst, tailFirst + 4L)
                let! tail = TestHttp.get (at tailRange mintedToken)
                Expect.equal tail.Status 200 "the tail has an address"
                Expect.equal (lines tail.Body).Length 5 "and five events in it"
                do! append 10
                let! tailAgain = TestHttp.get (at tailRange mintedToken)
                Expect.equal tailAgain.Body tail.Body "the same address answers the same bytes after the log grew"

                // A range the log has not reached is a 404, never a short answer: a partial
                // body here would be kept for ever as if it were the whole range.
                let! unreached = TestHttp.get (at (Events (10_000L, 10_009L)) mintedToken)
                Expect.equal unreached.Status 404 "a range beyond the log does not exist yet"

                // Current: nothing to keep, so nothing to give an address to. The end is READ
                // rather than counted from the appends above — the session writes its own start
                // before any of them, and an arithmetic answer here would be off by exactly
                // that and fail as a cache bug.
                let! page = h.Log.Read None Int32.MaxValue
                let latest = page.Events |> List.last |> fun e -> e.Offset
                let! current = TestHttp.getUnredirected [] (at (EventsAfter (Some latest)) mintedToken)
                Expect.equal current.Status 204 "a caller at the end is told it is current"
                Expect.equal (TestHttp.requiredHeader "cache-control" current) "no-store" "and emptiness is never kept"

                let! wrongToken = TestHttp.get (at (EventsAfter None) "stolen")
                Expect.equal wrongToken.Status 401 "the cursor is gated on minted tokens"
                Expect.equal (TestHttp.requiredHeader "cache-control" wrongToken) "no-store" "rejections never cache"

                let! wrongTokenRange = TestHttp.get (at (Events (0L, 9L)) "stolen")
                Expect.equal wrongTokenRange.Status 401 "and so are the events themselves"

                let! bare = TestHttp.get (sprintf "http://127.0.0.1:%d/events" h.Port)
                Expect.equal bare.Status 401 "no cookie and no token is unauthorized"

                let! notARange = TestHttp.get (sprintf "http://127.0.0.1:%d/events/nope?token=%s" h.Port mintedToken)
                Expect.equal notARange.Status 404 "an unparseable range is not a route"
                do! h.Stop ()
            }

        Tag.needs "HTTP fetcher client" [ Tag.Ports; Tag.Native ] (fun () ->
            testCaseAsync "a client consuming over the HTTP fetcher builds the same timeline as the frame path" <|
            async {
                let! h = Host.start (SessionId.create "events-http-client" |> expect) 0
                let mintedToken = h.MintPeerToken ()
                let baseUrl = sprintf "http://127.0.0.1:%d" h.Port
                let signalUrl = SessionRoute.at baseUrl Signal
                let options =
                    { Client.ConnectOptions.defaults with
                        FetchEvents =
                            Some (Client.EventFetch.overHttp chunkGet (SessionRoute.at baseUrl) (Some mintedToken)) }
                let! a = connectClientWith options signalUrl mintedToken "ada" "Ada"

                do! compose a a.Hello.PeerId "fetched over http"
                a.Connection.SendDraft a.Hello.PeerId
                do! a.Runner.WaitFor (fun m ->
                        not m.EventConsumer.IsCatchingUp
                        && Support.saidOn m = [ "fetched over http" ])
                do! a.Channel.Close ()
                do! h.Stop ()
            })
    ]

// --- What a client keeps, and what it does with it (Plan 20, step 2) ----------------------
// The store is a port, so these need no browser: a dictionary standing in for the Cache API
// answers the same three questions, and what is under test is the client's reasoning about
// what it holds — replay it in order, notice a hole, and ask the network only for what is
// missing.

/// A `HistoryCache` over a list, ordered the way the Cache API orders `keys()`: insertion
/// first, which is fetch order, which is ascending.
let private storeOf (entries: (string * string) list) =
    let kept = ResizeArray entries
    // Type-qualified: `Read` is a field name several records in the domain share, and an
    // unqualified one would resolve to whichever was declared last. `HistoryCache` carries
    // `RequireQualifiedAccess`, so the record TYPE has to be named, not just its module.
    { Client.HistoryCache.Stored = fun () -> async.Return (kept |> Seq.map fst |> List.ofSeq)
      Client.HistoryCache.Read = fun url -> async.Return (kept |> Seq.tryPick (fun (u, body) -> if u = url then Some body else None))
      Client.HistoryCache.Write = fun url body -> async { kept.Add (url, body) } }

/// One kept answer: the JSONL the server served for offsets [first, last].
let private answerOf (first: int64) (count: int) =
    let line (offset: int64) =
        Codec.toString
            Codec.sessionEventEnvelope
            { EventId = EventId.fresh ()
              SessionId = SessionId.create "kept-history" |> expect
              Offset = EventOffset.create offset |> expect
              Actor = ActorRef.SessionProcess
              Timestamp = System.DateTimeOffset.FromUnixTimeSeconds 0L
              Event =
                PeerJoined
                    { PeerId = PeerId.create (sprintf "p-%d" offset) |> expect
                      DisplayName = sprintf "peer %d" offset
                      User = None } }
    let last = first + int64 count - 1L
    sprintf "events/%d-%d" first last,
    [ first .. last ] |> List.map line |> String.concat "\n"

let private storeTests =
    testList "What a client keeps" [
        testCaseAsync "a replay folds what was kept, in order, with no network at all" <|
            async {
                let store = storeOf [ answerOf 0L 3; answerOf 3L 2 ]
                let seen = ResizeArray ()
                do! Client.EventFetch.replay store seen.Add
                let offsets =
                    seen
                    |> Seq.collect (fun msg ->
                        match msg with
                        | LocalHistoryMsg page -> page.Events |> List.map (fun e -> EventOffset.value e.Offset)
                        | _ -> [])
                    |> List.ofSeq
                Expect.equal offsets [ 0L; 1L; 2L; 3L; 4L ] "every kept event, once, in log order"
            }

        testCaseAsync "a replayed page never claims the feed is live" <|
            async {
                // The distinction the separate message exists for: these events prove this
                // client KEPT them, and nothing whatever about whether the network works. A
                // client replaying offline while reporting a healthy history feed is lying
                // about the one leg that is down.
                let store = storeOf [ answerOf 0L 2 ]
                let seen = ResizeArray ()
                do! Client.EventFetch.replay store seen.Add
                let stalled = { ClientModel.init { PeerId = PeerId.create "p" |> expect; DisplayName = "P" } with
                                  EventConsumer =
                                    { (ClientModel.init { PeerId = PeerId.create "p" |> expect; DisplayName = "P" }).EventConsumer with
                                        Feed = FeedStalled "offline" } }
                let folded = seen |> Seq.fold (fun m msg -> Support.step msg m) stalled
                Expect.equal folded.EventConsumer.Feed (FeedStalled "offline") "the feed's health is untouched by a local read"
                Expect.equal
                    (folded.Conversation.Items |> List.length)
                    0
                    "these are joins, so nothing lands in the conversation — the offsets are the point"
                Expect.equal
                    (folded.EventConsumer.LastProcessedOffset |> Option.map EventOffset.value)
                    (Some 1L)
                    "and the read position moved, which is what the resume reads"
            }

        testCaseAsync "kept answers the store hands back out of order still fold in log order" <|
            async {
                // The store's own order is not ascending and cannot be made to be: the Cache
                // API's `put` of an address already held deletes the entry and appends the new
                // one, so two tabs of one session fetching the same range moves the earliest
                // answer to the end. Walking in that order read a later answer first and
                // called every event before it missing, on a client that held them all.
                let store = storeOf [ answerOf 3L 2; answerOf 0L 3 ]
                let seen = ResizeArray ()
                do! Client.EventFetch.replay store seen.Add
                let offsets =
                    seen
                    |> Seq.collect (fun msg ->
                        match msg with
                        | LocalHistoryMsg page -> page.Events |> List.map (fun e -> EventOffset.value e.Offset)
                        | _ -> [])
                    |> List.ofSeq
                Expect.equal offsets [ 0L; 1L; 2L; 3L; 4L ] "every kept event, once, in log order"
                Expect.isFalse
                    (seen |> Seq.exists (function LocalHistoryGapMsg _ -> true | _ -> false))
                    "and nothing is missing — the events were all here, in a bag rather than a queue"
            }

        testCaseAsync "a replay is one message, however many answers were kept" <|
            async {
                // Every message is a full render. A session somebody watched live keeps one
                // answer per poll, so a replay that dispatched per answer was one render per
                // poll — 179 on the session this was measured on, in a single task, a phone
                // frozen for three seconds on an empty conversation.
                let store = storeOf [ for first in 0L .. 3L .. 297L -> answerOf first 3 ]
                let seen = ResizeArray ()
                do! Client.EventFetch.replay store seen.Add
                let pages = seen |> Seq.filter (function LocalHistoryMsg _ -> true | _ -> false) |> Seq.length
                Expect.equal pages 1 "one page for the whole contiguous run"
                let last =
                    seen |> Seq.tryPick (function LocalHistoryMsg page -> page.LastOffset | _ -> None)
                Expect.equal (last |> Option.map EventOffset.value) (Some 299L) "reaching the last kept event"
            }

        testCaseAsync "two answers that overlap put each event on the page once" <|
            async {
                // Two tabs fetching one range keep answers that meet in the middle. The page
                // carries the event where they meet once: a fold that saw it twice would count
                // a joined peer twice, and a page is folded as a whole.
                let store = storeOf [ answerOf 0L 4; answerOf 2L 4 ]
                let seen = ResizeArray ()
                do! Client.EventFetch.replay store seen.Add
                let offsets =
                    seen
                    |> Seq.collect (fun msg ->
                        match msg with
                        | LocalHistoryMsg page -> page.Events |> List.map (fun e -> EventOffset.value e.Offset)
                        | _ -> [])
                    |> List.ofSeq
                Expect.equal offsets [ 0L; 1L; 2L; 3L; 4L; 5L ] "each kept event once, in log order"
            }

        testCaseAsync "a local open says it is connecting before it folds what it kept" <|
            async {
                // The model starts "not connected", and a page wore that from its first paint
                // until the network was asked. The interval the client is about to connect in
                // starts at the first paint, so what the page wears through the replay — for
                // seconds, on a phone with a long session kept — is `Connecting`.
                let store = storeOf [ answerOf 0L 3 ]
                let seen = ResizeArray ()
                do! Client.LocalOpen.replay store Client.TranscriptCaches.none seen.Add
                let connecting = seen |> Seq.tryFindIndex (function ConnectingMsg -> true | _ -> false)
                let folded = seen |> Seq.tryFindIndex (function LocalHistoryMsg _ -> true | _ -> false)
                match connecting, folded with
                | Some c, Some f -> Expect.isTrue (c < f) "connecting is said before the first kept page lands"
                | _ -> failwith "a local open over a kept store says it is connecting and folds a page"
            }

        testCaseAsync "a hole stops the fold at its edge" <|
            async {
                // An entry evicted from the middle leaves the rest kept. Folding over the gap
                // would advance the read position past events nothing will ever offer again,
                // so the walk stops AT the hole rather than looking like the end of history.
                let store = storeOf [ answerOf 0L 2; answerOf 5L 2 ]
                let seen = ResizeArray ()
                do! Client.EventFetch.replay store seen.Add
                let folded =
                    seen
                    |> Seq.collect (fun msg ->
                        match msg with
                        | LocalHistoryMsg page -> page.Events |> List.map (fun e -> EventOffset.value e.Offset)
                        | _ -> [])
                    |> List.ofSeq
                Expect.equal folded [ 0L; 1L ] "everything up to the hole, and nothing past it"
            }

        testCaseAsync "a hole is reported as history this device lacks, never as the feed's health" <|
            async {
                // Nothing has been read from the network when a replay runs, so a hole can
                // say nothing whatever about the feed. Reported as one, it flashed a red
                // "history paused" over every cold open with a hole in it — moments before
                // the first page repaired the thing it was complaining about.
                let store = storeOf [ answerOf 0L 2; answerOf 5L 2 ]
                let seen = ResizeArray ()
                do! Client.EventFetch.replay store seen.Add
                Expect.isFalse
                    (seen |> Seq.exists (function EventFeedMsg _ -> true | _ -> false))
                    "a local read reports no feed health at all"
                let reported =
                    seen |> Seq.tryPick (function LocalHistoryGapMsg at -> Some at | _ -> None)
                match reported with
                | None -> failwith "a hole must be reported, not silently treated as the end"
                | Some at ->
                    Expect.equal (EventOffset.value at) 5L "naming where the kept history resumes"
            }

        testCaseAsync "a page off the network clears the gap it fills" <|
            async {
                // The read resumes at the cursor the replay parked at, which is exactly where
                // the fill has to start — so a page arriving from the network means the hole
                // is being filled, and what is left to arrive is ordinary catch-up.
                let store = storeOf [ answerOf 0L 2; answerOf 5L 2 ]
                let seen = ResizeArray ()
                do! Client.EventFetch.replay store seen.Add
                let model =
                    seen
                    |> Seq.fold
                        (fun m msg -> Support.step msg m)
                        (ClientModel.init { PeerId = PeerId.create "p" |> expect; DisplayName = "P" })
                Expect.equal
                    (model.EventConsumer.MissingBefore |> Option.map EventOffset.value)
                    (Some 5L)
                    "the replay left the hole on the model"
                let fill =
                    match Client.EventFetch.decodeLines (snd (answerOf 2L 3)) with
                    | Ok envelopes -> envelopes
                    | Error fault -> failwith (Client.FeedFault.describe fault)
                let filled =
                    Support.step
                        (EventsPageMsg
                            { Events = fill
                              LastOffset = fill |> List.tryLast |> Option.map (fun e -> e.Offset)
                              IsEnd = false })
                        model
                Expect.equal filled.EventConsumer.MissingBefore None "and a page off the network takes it away"
            }

        testCaseAsync "an answer is kept under the address it came back FROM, not the one asked for" <|
            async {
                // The cursor moves; the range it resolves to does not. Keying by the request
                // would key history under an address whose meaning changes, which is the bug
                // the whole scheme exists to avoid.
                let store = storeOf []
                let answering : Client.HttpGet =
                    fun _ -> async { return Ok { Url = "events/0-41"; Body = snd (answerOf 0L 2) } }
                let storing = Client.EventFetch.storing store answering
                let! _ = storing "events/after/99"
                let! kept = store.Stored ()
                Expect.equal kept [ "events/0-41" ] "the resolved range is the key"
            }
    ]

// What one page costs does not grow with the log behind it.
//
// A reader catching up asks for every page in turn, so `Read` is called once per page — and
// both stores used to answer by walking the WHOLE log from the cursor and then discarding
// all but the first `limit`. That is quadratic in the length of the log, per reader, per
// cold open: a real 20,650-event session cost 208 reads averaging ten thousand copied
// envelopes apiece, about two million of them, and roughly 1.9s of a cold open on a laptop.
// `EventPaging.page` takes the slice instead, which it can because an envelope's offset is
// its index.
//
// Pinned as a RATIO, for the reason the render budget is a count rather than a duration: an
// absolute millisecond figure on a shared runner is the flaky test this repository warns
// about, while "a page off a long log costs about what it costs off a short one" is the same
// claim on every box. The bound is deliberately loose — a hundred times the log may cost ten
// times the page and still pass — because what it exists to catch is the walk, which makes
// it cost a hundred times.
// What is ahead of a cursor, as addresses — the plural of the cursor, and the thing that
// lets a catching-up client stop paying a round trip per hundred events.
//
// The arithmetic is pinned apart from the HTTP, because they fail for different reasons: the
// bounds are a rule about where ranges fall, and the endpoint is about authorization, framing
// and the empty case.
let private aheadTests =
    testList "What is ahead" [
        testCase "the addresses are the ones the cursor would have chosen, one at a time" <| fun () ->
            // The two surfaces must tile identically or a client that used both would keep
            // two copies of the same events under two sets of addresses. A cursor at 137
            // answers 138-237 — the tiling starts where the READER is, not on a boundary —
            // and so does this.
            let offset (n: int64) = EventOffset.create n |> expect
            Expect.equal
                (EventChunk.ranges (Some (offset 137L)) (Some (offset 500L)) |> List.head)
                (138L, 237L)
                "the first address begins one past the cursor, as the cursor's own does"
            Expect.equal
                (EventChunk.ranges None (Some (offset 500L)) |> List.head)
                (0L, int64 EventChunk.size - 1L)
                "and from the beginning, at the beginning"

        testCase "the last address stops at the head, and is short when it has to be" <| fun () ->
            let offset (n: int64) = EventOffset.create n |> expect
            let ranges = EventChunk.ranges None (Some (offset 142L))
            Expect.equal ranges [ (0L, 99L); (100L, 142L) ] "two addresses, the second as far as the log goes"
            // Not a tile waiting to be filled: the address names what it names, and the
            // events it names will never be different ones. What arrives later is somebody
            // else's address, handed out from their own cursor.
            let grown = EventChunk.ranges (Some (offset 142L)) (Some (offset 150L))
            Expect.equal grown [ (143L, 150L) ] "and what came after is addressed from the cursor that missed it"

        testCase "a caller at the head is told nothing is ahead" <| fun () ->
            let offset (n: int64) = EventOffset.create n |> expect
            Expect.equal (EventChunk.ranges (Some (offset 99L)) (Some (offset 99L))) [] "current means no addresses"
            Expect.equal (EventChunk.ranges None None) [] "and an empty log has none to give"

        testCase "one answer names no more than it promised" <| fun () ->
            let offset (n: int64) = EventOffset.create n |> expect
            // A log of any length is a handful of these rather than one reply naming ten
            // thousand addresses; a client that wants more asks again from where it got to.
            let far = int64 EventChunk.size * int64 EventChunk.ahead * 4L
            let ranges = EventChunk.ranges None (Some (offset far))
            Expect.equal (List.length ranges) EventChunk.ahead "capped at one answer's worth"
            let resumed = EventChunk.ranges (Some (offset (snd (List.last ranges)))) (Some (offset far))
            Expect.equal (fst (List.head resumed)) (snd (List.last ranges) + 1L) "and the next answer continues from it"

        testCaseAsync "the plan hands out addresses that serve, and says nothing when there are none" <|
            async {
                let! h = Host.start (SessionId.create "events-ahead" |> expect) 0
                let mintedToken = h.MintPeerToken ()
                let at (route: SessionRoute) (token: string) =
                    sprintf "%s?token=%s" (SessionRoute.at (sprintf "http://127.0.0.1:%d" h.Port) route) token

                // A caller who is already current: nothing is ahead, and `204` says so for
                // the reason the cursor answers `204` — an empty answer is a resource
                // somebody would keep, and "nothing yet" is exactly what stops being true.
                // Asked from the HEAD rather than of an empty log: a Host has recorded its
                // own start by the time it is up, so an empty log is not a state this
                // surface can be asked about from out here.
                let! atHead = h.Log.Head ()
                let! current = TestHttp.getUnredirected [] (at (EventsAhead atHead) mintedToken)
                Expect.equal current.Status 204 "a caller with nothing to catch up on is told so"
                let before = atHead |> Option.map EventOffset.value |> Option.defaultValue -1L

                for i in 1 .. 2 * EventChunk.size + 5 do
                    let! _ =
                        h.Log.Append
                            ActorRef.SessionProcess
                            (PeerJoined
                                { PeerId = PeerId.create (sprintf "ahead-%d" i) |> expect
                                  DisplayName = "filler"
                                  User = None })
                    ()

                let! refused = TestHttp.getUnredirected [] (at (EventsAhead None) "not-a-token")
                Expect.equal refused.Status 401 "and it is gated like every other read here"

                // From where that caller stood, so what is ahead is exactly what this case
                // appended and the count is its own arithmetic rather than the Host's.
                let from = EventOffset.create before |> Result.toOption
                let! plan = TestHttp.get (at (EventsAhead from) mintedToken)
                Expect.equal plan.Status 200 "the plan serves"
                Expect.equal (TestHttp.requiredHeader "cache-control" plan) "no-store" "which addresses are ahead of you moves"
                let addresses = plan.Body.Split '\n' |> Array.filter (fun l -> l.Trim().Length > 0)
                Expect.equal addresses.Length 3 "three answers' worth: two full and the tail"

                // The addresses are FETCHABLE as given — that is the whole promise. A client
                // is handed these and asks for them; it does not build one, so a missing
                // mount or a dropped token here is a client that cannot read its own history.
                let origin = sprintf "http://127.0.0.1:%d" h.Port
                let! first = TestHttp.get (origin + addresses.[0])
                Expect.equal first.Status 200 "the first address answers"
                let lines (body: string) = body.Split '\n' |> Array.filter (fun l -> l.Trim().Length > 0)
                Expect.equal (lines first.Body).Length EventChunk.size "with one answer's worth of events"
                let! last = TestHttp.get (origin + addresses.[addresses.Length - 1])
                Expect.equal last.Status 200 "and so does the short one at the end"
                Expect.equal (lines last.Body).Length 5 "carrying the tail"
                Expect.stringContains addresses.[0] "token=" "each carries the token a redirect would otherwise drop"
            }
    ]

// The feed that asks what is ahead and fetches it together.
//
// What it must keep is in three parts, and they break for different reasons: the ASKING (it
// does not wait for one answer before asking for the next — the whole point), the ORDER (log
// order, one page at a time, so nothing downstream sees a different shape than the cursor
// gave it), and the REFUSALS (a reader who moved, a bad answer, a caller who is current).
let private feedTests =
    let answers = System.Collections.Generic.Dictionary<string, string> ()
    let planFor (addresses: string list) = String.concat "\n" addresses

    /// A `HttpGet` that records the order calls were STARTED in and only settles the ones a
    /// test releases. Started-but-unsettled is what proves concurrency: a serial feed cannot
    /// have two in flight, so if it does, it asked before it was answered.
    let gateable () =
        let started = ResizeArray<string> ()
        let gates = System.Collections.Generic.Dictionary<string, unit -> unit> ()
        let get : Client.HttpGet =
            fun url ->
                started.Add url
                async {
                    // A plan settles at once; a range waits to be released.
                    if url.Contains "ahead" then
                        return Ok { Url = url; Body = answers.[url] }
                    else
                        let! () =
                            Async.FromContinuations (fun (ok, _, _) ->
                                gates.[url] <- fun () -> ok ())
                        return Ok { Url = url; Body = answers.[url] }
                }
        get, started, gates

    testList "Fetching what is ahead" [
        testCaseAsync "it asks for every address before it waits for any of them" <|
            async {
                answers.Clear ()
                let ranges = [ for first in 0L .. 3L .. 9L -> answerOf first 3 ]
                for (address, body) in ranges do answers.["/" + address] <- body
                answers.["/events/ahead"] <- planFor [ for (a, _) in ranges -> "/" + a ]
                let get, started, gates = gateable ()
                let feed = Client.EventFetch.aheadOf Client.HistoryCache.none get (fun r -> RelativeUrl.under "" (SessionRoute.relative r)) None
                // Started, not awaited — `StartChild` rather than a promise, which the
                // await-seam rule rightly refuses here.
                let! page = Async.StartChild (feed None)
                // Nothing has been released, so nothing has answered — and yet every
                // address has been asked for. A feed that waited would have asked once.
                do! Async.Sleep 20
                let asked = started |> Seq.filter (fun u -> not (u.Contains "ahead")) |> List.ofSeq
                Expect.equal (List.length asked) (List.length ranges) "every address is in flight at once"
                for (_, release) in List.ofSeq (Seq.map (fun (KeyValue (k, v)) -> k, v) gates) do release ()
                let! first = page
                match first with
                | Ok p -> Expect.equal (List.length p.Events) 3 "and the first answer comes back as its own page"
                | Error e -> failwithf "the feed faulted: %A" e
            }

        testCaseAsync "the pages come back in log order, one at a time" <|
            async {
                answers.Clear ()
                let ranges = [ for first in 0L .. 3L .. 9L -> answerOf first 3 ]
                for (address, body) in ranges do answers.["/" + address] <- body
                answers.["/events/ahead"] <- planFor [ for (a, _) in ranges -> "/" + a ]
                let get : Client.HttpGet = fun url -> async { return Ok { Url = url; Body = answers.[url] } }
                let feed = Client.EventFetch.aheadOf Client.HistoryCache.none get (fun r -> RelativeUrl.under "" (SessionRoute.relative r)) None
                // Driven the way the read loop drives it: ask, fold, ask again from the last
                // offset it gave back. Each answer is ONE range — the renderer sees the
                // shape the cursor always gave it, not a plan's worth in one task.
                let mutable after = None
                let mutable seen = []
                let mutable go = true
                while go do
                    match! feed after with
                    | Ok page when List.isEmpty page.Events -> go <- false
                    | Ok page ->
                        Expect.equal (List.length page.Events) 3 "one range per page, never a plan's worth at once"
                        seen <- seen @ (page.Events |> List.map (fun e -> EventOffset.value e.Offset))
                        after <- page.LastOffset
                        if page.IsEnd then go <- false
                    | Error e -> failwithf "the feed faulted: %A" e
                Expect.equal seen [ 0L .. 11L ] "every event once, in log order"
            }

        testCaseAsync "a reader who moved is answered from where they are, not from the queue" <|
            async {
                answers.Clear ()
                let ranges = [ for first in 0L .. 3L .. 9L -> answerOf first 3 ]
                for (address, body) in ranges do answers.["/" + address] <- body
                answers.["/events/ahead"] <- planFor [ for (a, _) in ranges -> "/" + a ]
                // A second plan, for a cursor that is not where the queue was filled for.
                let later = answerOf 50L 2
                answers.["/" + fst later] <- snd later
                answers.["/events/ahead/49"] <- planFor [ "/" + fst later ]
                let get : Client.HttpGet = fun url -> async { return Ok { Url = url; Body = answers.[url] } }
                let feed = Client.EventFetch.aheadOf Client.HistoryCache.none get (fun r -> RelativeUrl.under "" (SessionRoute.relative r)) None
                let! _ = feed None
                // The queue now holds 3.. onward. Asking from somewhere else entirely — a
                // gap repaired, a position restored — must not be served somebody else's
                // place in the log.
                let! moved = feed (EventOffset.create 49L |> Result.toOption)
                match moved with
                | Ok page ->
                    Expect.equal
                        (page.Events |> List.map (fun e -> EventOffset.value e.Offset))
                        [ 50L; 51L ]
                        "it asked again from the cursor it was given"
                | Error e -> failwithf "the feed faulted: %A" e
            }

        testCaseAsync "a caller with nothing ahead is told so, and told it is the end" <|
            async {
                answers.Clear ()
                answers.["/events/ahead"] <- ""
                let get : Client.HttpGet = fun url -> async { return Ok { Url = url; Body = answers.[url] } }
                let feed = Client.EventFetch.aheadOf Client.HistoryCache.none get (fun r -> RelativeUrl.under "" (SessionRoute.relative r)) None
                match! feed None with
                | Ok page ->
                    Expect.isTrue (List.isEmpty page.Events) "no events"
                    Expect.isTrue page.IsEnd "and the loop is told to stop asking"
                | Error e -> failwithf "the feed faulted: %A" e
            }

        testCaseAsync "one bad answer fails the whole refill rather than leaving a hole" <|
            async {
                answers.Clear ()
                let ranges = [ for first in 0L .. 3L .. 9L -> answerOf first 3 ]
                for (address, body) in ranges do answers.["/" + address] <- body
                answers.["/events/ahead"] <- planFor [ for (a, _) in ranges -> "/" + a ]
                let bad = "/" + fst ranges.[2]
                let get : Client.HttpGet =
                    fun url ->
                        async {
                            if url = bad then return Error (Client.HttpStatus 503)
                            else return Ok { Url = url; Body = answers.[url] }
                        }
                let feed = Client.EventFetch.aheadOf Client.HistoryCache.none get (fun r -> RelativeUrl.under "" (SessionRoute.relative r)) None
                // Not a short queue with a gap in the middle: a page missing from the middle
                // is one the fold would walk straight past, and whether to try again belongs
                // to the policy around this feed.
                match! feed None with
                | Error (Client.FeedRefused status) -> Expect.equal status 503 "the refusal is carried, with its status"
                | Error other -> failwithf "the wrong fault: %A" other
                | Ok page -> failwithf "answered with %d events over a refused address" (List.length page.Events)
            }
    ]

let private pagingTests =
    testList "Paging" [
        testCaseAsync "one page costs about the same whatever the log behind it holds" <|
            async {
                let logOf (n: int) =
                    let log =
                        InMemoryEventLog.create
                            (SessionId.create "paging" |> expect)
                            (fun () -> DateTimeOffset (2026, 1, 1, 0, 0, 0, TimeSpan.Zero))
                    async {
                        for i in 1 .. n do
                            let! _ =
                                log.Append
                                    ActorRef.System
                                    (SessionEvent.PeerJoined
                                        { PeerId = PeerId.create (sprintf "p-%d" i) |> expect
                                          DisplayName = "x"
                                          User = None })
                            ()
                        return log
                    }
                let short' = 200
                let long' = 20_000
                let! shortLog = logOf short'
                let! longLog = logOf long'
                // The FIRST page of each, which is where the walk and the slice differ most:
                // one copies the whole log to hand back a hundred, the other copies a hundred.
                let timeReads (log: Yession.SessionProcess.EventLog<SessionEvent>) =
                    async {
                        // Warm first, so neither side pays a one-off the other does not.
                        let! _ = log.Read None 100
                        let started = DateTimeOffset.UtcNow
                        for _ in 1 .. 50 do
                            let! page = log.Read None 100
                            Expect.equal (List.length page.Events) 100 "a full page each time"
                        return (DateTimeOffset.UtcNow - started).TotalMilliseconds
                    }
                let! shortMs = timeReads shortLog
                let! longMs = timeReads longLog
                // A floor on the denominator: 50 reads of a 200-event log can land on 0ms,
                // and a ratio over zero is not a measurement.
                let ratio = longMs / (max shortMs 1.0)
                printfn
                    "  50 pages off %d events: %.0fms; off %d events: %.0fms — %.1fx"
                    short' shortMs long' longMs ratio
                Expect.isTrue
                    (ratio < 10.0)
                    (sprintf
                        "a page off a log %dx longer cost %.1fx as much (%.0fms against %.0fms) — \
                         reading a page is walking the log again; see `EventPaging.page` in \
                         `src/Yession.SessionProcess/EventLog.fs`"
                        (long' / short') ratio longMs shortMs)
            }
    ]

let tests =
    testList "EventsHttp" [
        endpointTests
        storeTests
        aheadTests
        feedTests
        pagingTests
    ]
