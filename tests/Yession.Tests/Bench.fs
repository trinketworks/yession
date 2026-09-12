module Yession.Tests.Bench

// What a person waits for, measured — and written down so the next release can be compared to
// this one.
//
// `check` proves behaviour and says nothing about time. That is the right division, but it
// leaves a whole class of change invisible: #214 replaced a 150ms caret debounce with frame
// pacing, and the reconciliation y-prosemirror runs on every caret push (`view.update` is not
// gated on `docChanged`, so it walks the WHOLE document back into Yjs) went from occasional to
// once a frame. Nothing in the repository could see that, and nothing would have seen it get
// worse.
//
// Latencies swept across SIZES — a document's characters, a transcript's records, a
// conversation's items. The sweep is the point: the concern is O(document), and a number taken
// at one size cannot show a complexity regression at all. What comes out is `dist/bench.json`;
// `tasks.fsx` judges it against the recorded history and charts it.
//
// This is a measuring run, not an asserting one. The ONLY thing this suite fails on is being
// unable to take its measurement — a page that did not load, a scenario that returned nothing.
// Judging a number against a threshold is `bench-guard`'s job, deliberately: a suite that
// failed on wall clock would be the flaky test this repo warns about, on every box that is not
// the one the baseline came from.

open Fable.Pyxpecto

#if !FABLE_COMPILER

open System
open System.IO
open System.Text.Json
open Microsoft.Playwright

open Yession.Domain
open Yession.Tests.Browser

/// Its own port, away from the E2E's block: the benchmark and the gate can then run at the
/// same time on one box without one binding the other's listener out from under it.
let private BENCH_PORT = 8200

/// The sweep. Small enough to be a short message, large enough to be a long one, and an order
/// of magnitude between them so a linear cost is unmistakable in the ratio.
let private sizes = [ 200; 2_000; 20_000 ]

/// The other sweep, in transcript RECORDS — one `.cast` line, which is one pty write. A
/// different axis from `sizes` and so a list of its own: what grows here is a terminal's
/// recording, not a message, and the sizes are the ones a working session actually reaches
/// (the phone that reported this held 1,469).
let private transcriptSizes = [ 400; 1_500; 6_000 ]

/// How the transcript is cut into blocks for that sweep. It is the BLOCK COUNT that a
/// per-block read multiplies against, so a sweep of one enormous block would measure the one
/// shape the cost is linear in and miss the fault entirely — which is exactly how the reopen
/// budget in `Browser.fs`, whose seed is a single command, reads the same 2.01 renders per
/// record either side of a fix that took 6,000 records from 70.2ms a render to 2.5ms.
let private recordsPerBlock = 10

/// The third sweep, in conversation ITEMS: how long the conversation a person is scrolling
/// back through is. The session the stutter was reported from held 57.
let private conversationSizes = [ 20; 60; 200 ]

/// The open scenario: the same conversation sizes, kept as EVENTS in the client's own store,
/// `eventsPerAnswer` to each kept answer — a session somebody watched live keeps one answer
/// per poll, and a poll answers with the few events that arrived since (the session this was
/// measured on held 179 answers for 97 items). Repeated, because what comes out is counts
/// and distances a person would see, and the first open of a fresh page is the one with a
/// cold JIT in it.
let private eventsPerAnswer = 4
/// And from nothing: the cold open reads the log over the network a page at a time, and a
/// page is what the server's cursor answers with (`EventChunk.size`).
let private coldPageSize = EventChunk.size
/// A round trip: what a page costs to ask for over the home deployment's tailnet, measured
/// at twenty-five milliseconds from the phone's side. Pages arrive one per round trip because
/// the read loop asks for the next only once it has folded this one.
let private coldRoundTripMs = 25
let private openRepeats = 6
let private openWarmup = 1

/// The scroll scenario's viewport and stream. A phone's screen, because that is where a
/// fling is made with a thumb and where the stutter was seen; and records at twenty a second,
/// which is the rate a working turn's terminal output and events arrived at on that session
/// (five to twenty-two a second, in bursts). The fling is repeated until they have all landed.
let private phone = 390, 844
let private streamRecords = 100
let private streamEveryMs = 50

/// The scroll scenario runs the main thread at a QUARTER speed (`Emulation.setCPUThrottlingRate`).
/// A render that takes 5ms on the runner and 20ms on a phone lands in a frame on the phone and
/// between two on the runner, so unthrottled the frame series could not see the collision this
/// scenario is about. The slowdown is uniform and the judgement is against history on the same
/// runner, so the ratio it is judged by does not move with it.
let private scrollThrottle = 4

/// Samples per metric per size. The first few are discarded — a cold JIT and an unwarmed
/// layout are not what anybody experiences after the first keystroke.
let private samples = 30
let private warmup = 5

/// Percentile of a sorted-on-demand series, nearest-rank. p50 is what the guard watches (the
/// tail is where a shared runner's noise lives); p95 is recorded because the tail is what a
/// person actually notices.
let private percentile (p: float) (xs: float list) : float =
    match xs with
    | [] -> nan
    | _ ->
        let sorted = List.sort xs
        let rank = int (ceil (p * float sorted.Length)) - 1
        sorted.[max 0 (min (sorted.Length - 1) rank)]

/// A measured series, named for what a person was waiting for, in the unit it was taken in —
/// milliseconds for a wait, a count for renders and paints, pixels for how far the page moved.
[<RequireQualifiedAccess>]
type private Series = { Metric : string; Unit : string; Size : int; Values : float list }

let private seriesFrom (json: string) (size: int) (names: (string * string) list) : Series list =
    use doc = JsonDocument.Parse json
    [ for (key, metric) in names do
        let all =
            doc.RootElement.GetProperty(key).EnumerateArray ()
            |> Seq.map (fun e -> e.GetDouble ())
            |> List.ofSeq
        // Drop the warm-up, but never everything: a series that came back short is better
        // reported short — the sample-count check below is what refuses to report it at all.
        let values = if List.length all > warmup then List.skip warmup all else all
        { Series.Metric = metric; Series.Unit = "ms"; Series.Size = size; Series.Values = values } ]

/// A number that is not a series: a slope across a sweep, or a count. Name, unit, value, and
/// what it was taken over.
[<RequireQualifiedAccess>]
type private Ratio = { Name : string; Unit : string; Value : float; Over : string }

/// `dist/bench.json`, in github-action-benchmark's `customSmallerIsBetter` shape — so the
/// history is readable by that tool if this repo ever wants its chart instead of ours, and so
/// the schema was designed by somebody who had already thought about it.
let private writeReport (all: Series list) (ratios: Ratio list) =
    let dist = Path.Combine (Directory.GetCurrentDirectory (), "dist")
    Directory.CreateDirectory dist |> ignore
    use stream = File.Create (Path.Combine (dist, "bench.json"))
    use w = new Utf8JsonWriter (stream, JsonWriterOptions (Indented = true))
    w.WriteStartArray ()
    let point (name: string) (unit: string) (value: float) (extra: string) =
        w.WriteStartObject ()
        w.WriteString ("name", name)
        w.WriteString ("unit", unit)
        w.WriteNumber ("value", Math.Round (value, 3))
        w.WriteString ("extra", extra)
        w.WriteEndObject ()
    for s in all do
        for (p, label) in [ 0.5, "p50"; 0.95, "p95" ] do
            point
                (sprintf "%s.%s@%d" s.Metric label s.Size)
                s.Unit
                (percentile p s.Values)
                (sprintf "%d samples" (List.length s.Values))
    for r in ratios do
        point r.Name r.Unit r.Value r.Over
    w.WriteEndArray ()
    w.Flush ()

let private table (all: Series list) (ratios: Ratio list) =
    printfn ""
    printfn "  %-16s %8s %9s %9s %5s" "metric" "size" "p50" "p95" "unit"
    for s in all do
        printfn
            "  %-16s %8d %9.2f %9.2f %5s"
            s.Metric s.Size (percentile 0.5 s.Values) (percentile 0.95 s.Values) s.Unit
    for r in ratios do
        printfn "  %-16s %8s %9.2f%s" r.Name r.Over r.Value r.Unit
    printfn ""

/// One thumb, down near the top of the conversation and dragged fast to its foot, then
/// lifted: a touch sequence through the browser's own input pipeline, timestamped as a
/// thumb moves, so the gesture recogniser scrolls on every move and flings on the lift.
/// Sent as touch EVENTS rather than through `Input.synthesizeScrollGesture`, whose touch
/// synthesis carried the conversation nowhere on the Linux runner while carrying it fine on
/// a Mac — and a gesture that may or may not have happened is not a fling.
let private thumbFling (cdp: ICDPSession) (x: float) (fromY: float) (toY: float) : Async<unit> =
    async {
        let touch (kind: string) (y: float) (at: float) =
            async {
                let point = Collections.Generic.Dictionary<string, obj> ()
                point.["x"] <- box x
                point.["y"] <- box y
                let args = Collections.Generic.Dictionary<string, obj> ()
                args.["type"] <- box kind
                args.["touchPoints"] <- box (if kind = "touchEnd" then [||] else [| point |])
                args.["timestamp"] <- box at
                let! reply = await (cdp.SendAsync ("Input.dispatchTouchEvent", args))
                ignore reply
            }
        let steps = 12
        let stepMs = 8.0
        let t0 = float (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds ()) / 1000.0
        do! touch "touchStart" fromY t0
        for i in 1 .. steps do
            let y = fromY + (toY - fromY) * float i / float steps
            do! touch "touchMove" y (t0 + float i * stepMs / 1000.0)
        do! touch "touchEnd" toY (t0 + float (steps + 1) * stepMs / 1000.0)
    }

/// Flinging back: a thumb through the conversation, again and again until the stream of
/// records has been sent — jumping to the end and flinging again whenever the top is reached,
/// so a short conversation is measured over as many records as a long one. Real input rather
/// than a `scrollTop` write, because a write is one scroll event and a thumb is hundreds —
/// and the listeners the app hangs on scroll are part of what a frame pays for.
///
/// Returns how far the conversation was carried, in pixels. Nothing means the frames
/// recorded were not a fling's, whatever else they were.
let private flingWhileStreaming (page: IPage) (cdp: ICDPSession) (records: int) : Async<float> =
    async {
        let scrollTop = "() => document.querySelector('#shell [data-conversation]').scrollTop"
        // Where the thumb goes, found again before every gesture: a fling that reaches the
        // conversation's top chains to the page, which scrolls the shell out from under a
        // box measured once.
        let box' =
            """() => {
              const el = document.querySelector('#shell [data-conversation]')
              el.scrollIntoView()
              const r = el.getBoundingClientRect()
              return [r.x + r.width / 2, r.top + r.height * 0.2, r.top + r.height * 0.9]
            }"""
        let mutable carried = 0.0
        let mutable sent = 0
        let mutable gestures = 0
        // Bounded, so a stream that stopped early cannot fling forever: at most one gesture
        // per record is far more than any conversation needs.
        while sent < records && gestures < records do
            let! b = await (page.EvaluateAsync<float[]> box')
            let! before = await (page.EvaluateAsync<float> scrollTop)
            do! thumbFling cdp b.[0] b.[1] b.[2]
            // Let the fling run out before measuring what it carried: a thumb lifted is a
            // scroll still going.
            do! awaitU (page.WaitForFunctionAsync """() => new Promise(done => {
                  const el = document.querySelector('#shell [data-conversation]')
                  let last = el.scrollTop, still = 0
                  const tick = () => { if (el.scrollTop === last) still++; else { still = 0; last = el.scrollTop }
                    if (still >= 3) done(true); else requestAnimationFrame(tick) }
                  requestAnimationFrame(tick) })""")
            gestures <- gestures + 1
            let! after = await (page.EvaluateAsync<float> scrollTop)
            carried <- carried + max 0.0 (before - after)
            if after <= 0.0 then
                do! awaitU (page.EvaluateAsync "() => { const el = document.querySelector('#shell [data-conversation]'); el.scrollTop = el.scrollHeight }")
            let! n = await (page.EvaluateAsync<int> "() => window.__benchScrollSent()")
            sent <- n
        return carried
    }

let tests =
    testList "Client performance" [
        testCaseAsync "the editor, the collaboration path, the transcript read, the scroll and the open, swept by size" <|
            async {
                let server = serveStatic harnessRoot BENCH_PORT
                let! pw = await (Playwright.CreateAsync ())
                let! br =
                    await (pw.Chromium.LaunchAsync (
                        BrowserTypeLaunchOptions (ExecutablePath = chromiumPath ())))
                let! page = await (br.NewPageAsync ())
                let evidence = watching page
                page.SetDefaultTimeout 30000.0f

                let body =
                    reporting "Client performance" page evidence <| async {
                        let! _ = await (page.GotoAsync (sprintf "http://127.0.0.1:%d/" BENCH_PORT))
                        let! _ = await (page.WaitForSelectorAsync "#peer-b .ProseMirror")

                        let collected = ResizeArray<Series> ()
                        // What the typing burst at each size actually did (keydowns seen, frames
                        // fired, where focus sat) — printed every run and quoted when a `type` or
                        // `receive` series comes up short, so the failure names a cause.
                        let diagBySize = System.Collections.Generic.Dictionary<int, string> ()
                        for size in sizes do
                            // Seed through the real relay, and check it actually landed: a
                            // sweep whose sizes all ended up the same is a sweep measuring one
                            // size three times, and it would look like a beautifully flat line.
                            let! seeded = await (page.EvaluateAsync<int> (sprintf "() => window.__benchSeed(%d)" size))
                            if seeded < size / 2 then
                                failwithf
                                    "seeding asked for ~%d characters and the co-editor's doc holds %d — the sweep is not sweeping"
                                    size seeded
                            printfn "  seeded %d chars -> co-editor doc holds %d" size seeded

                            // Typing is driven from OUT here, with real key events, so what is
                            // timed includes the browser's own dispatch. One drive yields both
                            // halves of a keystroke: the author's echo and the co-editor's screen.
                            do! awaitU (page.EvaluateAsync "() => window.__benchReset()")
                            do! awaitU (page.ClickAsync "#peer-b .ProseMirror")
                            do! awaitU (page.Keyboard.PressAsync "ControlOrMeta+End")
                            do! awaitU (page.Keyboard.TypeAsync (String.replicate samples "x"))
                            // Let this burst's sample frames land before reading. Every `type`
                            // and `receive` sample is recorded in a `requestAnimationFrame`, and
                            // reading the instant `TypeAsync` returns catches them mid-flight —
                            // on a busy frame that truncated the series to a handful or to none.
                            do! awaitU (page.EvaluateAsync "() => window.__benchSettle()")
                            let! typing = await (page.EvaluateAsync<string> "() => window.__benchTyping()")
                            let! diag = await (page.EvaluateAsync<string> "() => window.__benchDiag()")
                            diagBySize.[size] <- diag
                            printfn "  typed %d keystrokes at size %d — %s" samples size diag
                            collected.AddRange (seriesFrom typing size [ "type", "type"; "receive", "receive" ])

                            // The caret push, timed twice: the work it sets off, and the wait
                            // until it is on screen.
                            let! carets = await (page.EvaluateAsync<string> (sprintf "() => window.__benchCarets(%d)" samples))
                            collected.AddRange (seriesFrom carets size [ "push", "caret.push"; "paint", "caret.paint" ])

                        // The transcript read, on its own axis. No seeding through the relay and
                        // nothing rendered: the feed is built in the page and the blocks are read
                        // off it, which is the part of a render that grows with the recording.
                        for records in transcriptSizes do
                            let! read =
                                await (page.EvaluateAsync<string> (
                                    sprintf
                                        "() => window.__benchTranscript(%d, %d, %d)"
                                        records recordsPerBlock samples))
                            collected.AddRange (
                                seriesFrom read records [ "transcript.read", "transcript.read" ])
                            printfn "  read %d records in %d-record blocks" records recordsPerBlock

                        // The scroll, on a phone's screen with the main thread slowed to a
                        // phone's pace. Its own context, because the viewport is part of the
                        // scenario: what a render draws at 390px is not what it draws at the
                        // browser's own window, and the fling is a thumb's.
                        //
                        // `ViewportSize` and `HasTouch`, never `IsMobile`: that additionally
                        // asks Chromium to fit the layout to a device window, which lands at
                        // 648px rather than 390 (`Browser.fs` tells the same story). Touch is
                        // what makes the fling a touch gesture rather than a wheel.
                        let! phoneContext =
                            await (br.NewContextAsync (
                                BrowserNewContextOptions (
                                    ViewportSize = ViewportSize (Width = fst phone, Height = snd phone),
                                    HasTouch = true)))
                        let! phonePage = await (phoneContext.NewPageAsync ())
                        let! _ = await (phonePage.GotoAsync (sprintf "http://127.0.0.1:%d/" BENCH_PORT))
                        let! _ = await (phonePage.WaitForSelectorAsync "#shell [data-conversation]")

                        // The open, first and unthrottled: what it records is mostly counts
                        // and distances, which a slower main thread does not change, and the
                        // one wait in it (the freeze) is judged against history on this runner
                        // like every other wait here. Twice: from what the client kept, and
                        // from nothing — the cold open, everything over the network a page at
                        // a time, which is where the words under the eye move.
                        let opens =
                            [ "open", (fun items -> sprintf "__benchOpen(%d, %d)" items eventsPerAnswer)
                              "cold", (fun items -> sprintf "__benchOpenCold(%d, %d, %d)" items coldPageSize coldRoundTripMs) ]
                        for (prefix, call) in opens do
                            for items in conversationSizes do
                                let opens = ResizeArray<Collections.Generic.Dictionary<string, float>> ()
                                for _ in 1 .. openRepeats do
                                    let! report =
                                        await (phonePage.EvaluateAsync<string> (
                                            "() => window." + call items))
                                    use doc = JsonDocument.Parse report
                                    let number (name: string) = doc.RootElement.GetProperty(name).GetDouble ()
                                    let connection = doc.RootElement.GetProperty("connection").GetString ()
                                    // Anti-vacuity: an open that folded nothing, drew nothing, or
                                    // never connected would report a page that opened beautifully.
                                    if int (number "items") < items then
                                        failwithf "%s of %d items put %d on the page — the fold did not fold" prefix items (int (number "items"))
                                    if number "renders" < 1.0 || number "paints" < 1.0 then
                                        failwithf "%s of %d items rendered %.0f times and painted %.0f — nothing was measured" prefix items (number "renders") (number "paints")
                                    if connection <> "Connected" then
                                        failwithf "%s of %d items settled %s rather than Connected" prefix items connection
                                    let point = Collections.Generic.Dictionary<string, float> ()
                                    for key in [ "renders"; "paints"; "jumps"; "jump"; "blocked"; "time" ] do
                                        point.[key] <- number key
                                    opens.Add point
                                let last = opens.[opens.Count - 1]
                                printfn
                                    "  %s of %d items: %.0f renders, %.0f paints, %.0f jumps moving %.0fpx, frozen %.0fms, settled in %.0fms"
                                    prefix items last.["renders"] last.["paints"] last.["jumps"] last.["jump"] last.["blocked"] last.["time"]
                                let series (key: string) (metric: string) (unit: string) =
                                    { Series.Metric = prefix + "." + metric; Series.Unit = unit; Series.Size = items
                                      Series.Values = opens |> Seq.skip openWarmup |> Seq.map (fun p -> p.[key]) |> List.ofSeq }
                                collected.AddRange
                                    [ series "renders" "renders" "renders"
                                      series "paints" "paints" "paints"
                                      series "jump" "jump" "px"
                                      series "blocked" "blocked" "ms"
                                      series "time" "time" "ms" ]

                        let! cdp = await (phonePage.Context.NewCDPSessionAsync phonePage)
                        let throttle = Collections.Generic.Dictionary<string, obj> ()
                        throttle.["rate"] <- box scrollThrottle
                        let! _ = await (cdp.SendAsync ("Emulation.setCPUThrottlingRate", throttle))
                        let rendersPerRecord = ResizeArray<float> ()
                        for items in conversationSizes do
                            do!
                                awaitU (phonePage.EvaluateAsync (
                                    sprintf "() => window.__benchScrollBegin(%d, %d, %d, false)" items streamRecords streamEveryMs))
                            let! carried = flingWhileStreaming phonePage cdp streamRecords
                            let! report = await (phonePage.EvaluateAsync<string> "() => window.__benchScrollEnd()")
                            use doc = JsonDocument.Parse report
                            let renders = doc.RootElement.GetProperty("renders").GetInt32 ()
                            let records = doc.RootElement.GetProperty("records").GetInt32 ()
                            let startedAt = doc.RootElement.GetProperty("scrolledFrom").GetDouble ()
                            // Anti-vacuity, the three ways this scenario measures nothing and
                            // reports a smooth scroll: a conversation that never scrolled (it
                            // fit the screen, so the frames were a page standing still), a fling
                            // that carried it nowhere (the gestures went somewhere else), and a
                            // stream that never arrived (the frames had nothing to collide with).
                            // The distance is a few screens' worth: a thumb's fling is one, and
                            // the stream outlasts several.
                            if startedAt <= 0.0 then
                                failwithf "the conversation of %d items starts at scrollTop %.0f — it does not scroll, so nothing here was flung" items startedAt
                            if carried < 3.0 * float (snd phone) then
                                failwithf "flinging through %d items carried the conversation %.0fpx — less than three screens, so these frames are not a fling's" items carried
                            if records < streamRecords || renders < 1 then
                                failwithf
                                    "the stream over %d items sent %d of %d records and the page rendered %d times — too few to have collided with a fling"
                                    items records streamRecords renders
                            printfn
                                "  flung %d items %.0fpx while %d records landed in %d renders"
                                items carried records renders
                            rendersPerRecord.Add (float renders / float records)
                            collected.AddRange (seriesFrom report items [ "frame", "scroll.frame"; "render", "scroll.render" ])
                        do! awaitU (phoneContext.CloseAsync ())

                        let all = List.ofSeq collected
                        let medianOf metric size =
                            all
                            |> List.tryFind (fun s -> s.Metric = metric && s.Size = size)
                            |> Option.map (fun s -> percentile 0.5 s.Values)
                            |> Option.defaultValue nan
                        let pushAt = medianOf "caret.push"
                        let slope = pushAt (List.max sizes) / pushAt (List.min sizes)
                        let readAt = medianOf "transcript.read"
                        let transcriptSlope =
                            readAt (List.max transcriptSizes) / readAt (List.min transcriptSizes)
                        let renderAt = medianOf "scroll.render"
                        let scrollSlope =
                            renderAt (List.max conversationSizes) / renderAt (List.min conversationSizes)
                        let ratios =
                            [ // The one number that barely moves with the hardware it was taken
                              // on, and the only one that answers the question this suite was
                              // built for: is the caret push's cost growing with the document?
                              // Linear puts it near 100; a fix upstream would collapse it toward 1.
                              { Ratio.Name = "caret.push.slope"; Ratio.Unit = "x"; Ratio.Value = slope
                                Ratio.Over = sprintf "%d/%d chars" (List.max sizes) (List.min sizes) }
                              // The same question on the other axis: is a render's transcript
                              // reading growing with the TRANSCRIPT, or with the transcript
                              // times the blocks on it? Proportional puts this near the size
                              // ratio (15); the per-block scan it replaced put it near its square.
                              { Ratio.Name = "transcript.read.slope"; Ratio.Unit = "x"; Ratio.Value = transcriptSlope
                                Ratio.Over = sprintf "%d/%d records" (List.max transcriptSizes) (List.min transcriptSizes) }
                              // And on the third: does a render mid-scroll grow with the
                              // conversation? It does today — the view draws every item — and
                              // this is the number that says by how much.
                              { Ratio.Name = "scroll.render.slope"; Ratio.Unit = "x"; Ratio.Value = scrollSlope
                                Ratio.Over = sprintf "%d/%d items" (List.max conversationSizes) (List.min conversationSizes) }
                              // Renders per record that arrived while the conversation was
                              // being flung. A COUNT, so it is the same on every box: one today,
                              // because each record is dispatched and each dispatch renders.
                              // Holding renders while a surface scrolls is the change that
                              // would move it, and this is where that change would show.
                              { Ratio.Name = "scroll.renders"; Ratio.Unit = "x"
                                Ratio.Value = (Seq.sum rendersPerRecord) / float rendersPerRecord.Count
                                Ratio.Over = sprintf "%d records at %dms" streamRecords streamEveryMs } ]

                        // Anti-vacuity. Every one of these has a silent failure that produces a
                        // perfectly plausible report: a scenario that never ran leaves an empty
                        // series, and an empty series percentiles to NaN, which JSON-serialises
                        // and charts as if it were a measurement.
                        for s in all do
                            if List.length s.Values < 5 then
                                let diag =
                                    match diagBySize.TryGetValue s.Size with
                                    | true, d -> sprintf " — typing diagnostics for size %d: %s" s.Size d
                                    | _ -> ""
                                failwithf
                                    "%s@%d collected %d samples — too few to take a percentile from, so this report would be fiction%s"
                                    s.Metric s.Size (List.length s.Values) diag
                        for r in ratios do
                            if Double.IsNaN r.Value || Double.IsInfinity r.Value then
                                failwithf "%s is not a number — one end of its sweep produced nothing" r.Name

                        table all ratios
                        writeReport all ratios
                    }

                // Teardown unconditionally, then re-raise — the shape every browser case here
                // uses, and for the same reason: a listener left bound outlives the run.
                let! outcome = Async.Catch body
                do! awaitU (br.CloseAsync ())
                pw.Dispose ()
                server.Stop ()
                match outcome with
                | Choice1Of2 () -> ()
                | Choice2Of2 e -> raise e
            }
    ]

#else

let tests : Fable.Pyxpecto.Model.TestCase = testList "Client performance" []

#endif
