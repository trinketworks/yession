module Yession.Browser.Screens

// The live terminal screen, in the browser (Plan 14, stage 6).
//
// A terminal in live mode is running a program that moves the cursor, so what it DISPLAYS is
// a projection of what it emitted — not the stream itself. The client therefore needs an
// emulator, and it uses the SAME one the Session does (`@xterm/headless`, driven
// through `Emulator.fs`'s contract): fed the same bytes in the same order AT THE SAME SIZE,
// the two screens cannot disagree, which is the property `TerminalSnapshot` rests on and
// which a second, browser-only renderer would quietly break.
//
// The size is half of that sentence and used to be missing from it. This opened every
// emulator at 80x24 and never resized one, while the Process resized both its pty and its own
// emulator on every viewport report — so a wide pane rewrapped every line at 80 columns and a
// program drawing 40 rows spilled its frame into scrollback, under a comment promising the
// two could not disagree. The snapshot now carries the geometry it was painted at, and a
// resize record reshapes the emulator exactly as it reshaped the pty.
//
// The emulator is a live object, so it lives here rather than in the model. What reaches the
// model is its SERIALIZATION — the same form a snapshot travels in — which the view renders
// through the ANSI spans a block's output already uses.
//
//   snapshot(seq, screen)  ->  reset the emulator to that screen, remember seq
//   record(n > seq)        ->  write it, advance seq
//   after either           ->  serialize and dispatch
//
// Composability comes from the seq, exactly as it does for events: the snapshot may be NEWER
// than its seq (the screen is read after the position is taken), which is the safe direction
// — a record already drawn is drawn again, and drawing it twice is idempotent, whereas a seq
// ahead of the screen would skip a record for ever.

open Fable.Core
open Browser.Dom
open Browser.Types
open Fable.BrowserExtras
open Yession.Domain
open Yession.Domain.Terminals
open Yession.App
open Yession.Session
open Yession.Host

/// How many times this module has asked the DOCUMENT where the terminal boxes are, published
/// for the reason `Render.fs` publishes its render count: the cost being guarded is a number
/// of whole-document scans, and a test that inferred it by hooking `querySelectorAll` would
/// read a private detail of the walk below and go quietly vacuous the day the walk changes
/// shape. This is the walk saying what it did. Read in the page by name, as the render count
/// is — the scans-per-render case in `Browser.fs` is its one reader.
let private scansPublished : PageGlobal<int> = PageGlobal.named "__yessionViewportScans"

let private countScan () : unit =
    PageGlobal.set
        scansPublished
        (match PageGlobal.tryGet scansPublished with
         | Some n -> n + 1
         | None -> 1)

/// The box each terminal's output is laid into, whichever mode it is in: the live screen while
/// a program holds it, the block scrollback while commands do. ONE selector, because it is one
/// question — how wide is what this reader is looking at — and the pane shows one of the two
/// at a time. It used to name only the screen, which is why block mode never had a width:
/// there was nothing to measure until somebody took the keyboard.
///
/// Every box at once, ONE scan, rather than a scan per terminal id. The pane draws one
/// terminal, so a session with a dozen open ones asked the document about eleven boxes that
/// were not in it — and a compound attribute selector that MISSES is the most expensive
/// question there is, because nothing is indexed and the walk cannot stop early. Measured on
/// a session of 20,650 events (54k nodes, 12 open terminals): 616 of these, 5.3 seconds, a
/// third of all the CPU a cold open spent.
///
/// Keyed first-wins in document order, which is what the selector list answered before: a
/// terminal holding both boxes at once resolves to the same one it always did.
let private viewports () : Map<string, HTMLElement> =
    countScan ()
    let found = document.querySelectorAll "[data-terminal-screen], [data-terminal-scrollback][data-terminal-id]"
    (Map.empty, [ 0 .. found.length - 1 ])
    ||> List.fold (fun boxes i ->
        let element = found.[i] :?> HTMLElement
        let id =
            match element.getAttribute "data-terminal-screen" with
            | null | "" -> element.getAttribute "data-terminal-id"
            | screen -> screen
        if isNull (box id) || id = "" || Map.containsKey id boxes then boxes
        else Map.add id element boxes)

/// A CSS length as pixels, or zero. `getPropertyValue` answers `"12px"` for a resolved length
/// and `""` for anything it cannot resolve, and only the first is a number to subtract.
let private pixels (element: HTMLElement) (property: string) : float =
    match System.Double.TryParse ((computedProperty element property).Trim().Replace ("px", "")) with
    | true, pixels -> pixels
    | _ -> 0.0

/// The viewport size in CHARACTER CELLS, measured from the rendered page rather than assumed:
/// the pane is one width on a desktop and another on a phone, and the program on the other end
/// lays its screen out to whatever it is told.
///
/// Two measurements, neither of them a constant. The CELL comes from putting a known run of
/// characters in the box and reading what it takes — the only way to get a font's advance
/// width without hard-coding one — wearing the class the output really renders in, so the font
/// measured is the font used. `white-space: pre` inline over that class, because it wraps, and
/// a wrapped run measures the box instead of the text.
///
/// The BOX is `clientWidth` less its padding: that already excludes the border and any
/// scrollbar, which are exactly the pixels no character is drawn on. Read rather than
/// subtracted as a constant — the two modes pad differently, and the version of this that
/// hard-coded `- 24` was one restyle from being quietly wrong.
///
/// Takes the box rather than a terminal id: finding one is `viewports` above, and it finds
/// them all together.
let private measure (box: HTMLElement) : (int * int) option =
    let probe = document.createElement "span"
    probe.className <- Style.terminalOutput
    // Out of flow and out of sight, so measuring a box never moves it. `pre` over the
    // class, which wraps: a wrapped run measures the box instead of the text.
    setStyleProperty probe "position" "absolute"
    setStyleProperty probe "visibility" "hidden"
    setStyleProperty probe "white-space" "pre"
    probe.textContent <- Array.create 80 "M" |> String.concat ""
    box.appendChild probe |> ignore
    let rect = probe.getBoundingClientRect ()
    let cell = rect.width / 80.0
    let line = rect.height
    box.removeChild probe |> ignore
    if not (cell > 0.0) || not (line > 0.0) then None
    else
        let width = box.clientWidth - pixels box "padding-left" - pixels box "padding-right"
        let height = box.clientHeight - pixels box "padding-top" - pixels box "padding-bottom"
        let cols = int (floor (width / cell))
        let rows = int (floor (height / line))
        if cols > 0 && rows > 0 then Some (cols, rows) else None

/// One terminal's screen as this client composes it.
type private Live =
    { Emulator : Emulator
      /// The transcript position the emulator has been fed through.
      mutable Through : int
      /// The last serialization dispatched, so an unchanged screen is not re-dispatched into
      /// a render loop that would then ask for it again.
      mutable Rendered : string }

type Screens =
    { /// The Process's screen for a terminal: the transcript position it represents, and the
      /// size it was painted at.
      Snapshot : TerminalId -> TranscriptKeyframe -> unit
      /// Fold everything the model has that this client's screens have not seen, and
      /// dispatch any that changed. Safe to call after every render: a screen that did not
      /// move dispatches nothing.
      Sync : ClientModel -> unit
      /// Drop a terminal's emulator. An emulator is a live object with a worker behind it;
      /// one left behind for a terminal nobody is in is a leak with no screen.
      Forget : TerminalId -> unit }

/// `report` is told the holder's viewport size whenever it changes — the app relays it to the
/// Session, which resizes the pty.
///
/// It lives here, with the screen, rather than in the app beside the connection. The size of a
/// screen is a fact about that screen: the element to measure is the one this already tracks,
/// the moment to measure is a render this already runs after, and the de-duplication is about
/// what this already knows. In the app it was also unreachable — the browser tier drives the
/// harness, which has no copy of it — which is how the one thing it does wrong went unnoticed:
/// it ran only from `setState`, so a splitter drag or a window resize, which change the box
/// and dispatch nothing, never reached the pty at all.
let create (dispatch: ClientMsg -> unit) (report: TerminalId -> int -> int -> unit) : Screens =
    let live = System.Collections.Generic.Dictionary<string, Live> ()

    /// Whether this client held each terminal's lease at the last sync — the other half of the
    /// edge below. Kept apart from `live` on purpose: the lease can land on a terminal whose
    /// snapshot has not arrived, and an edge that only fired for terminals with an emulator
    /// would miss exactly the terminal somebody just opened.
    let held = System.Collections.Generic.Dictionary<string, bool> ()

    /// The size each terminal's viewport was last measured at, so a render that moved nothing
    /// says nothing — a resize is a signal to the program on the other end, and repeating it
    /// makes a full-screen program redraw for no reason.
    let measuredSize = System.Collections.Generic.Dictionary<string, int * int> ()

    let forget (key: string) =
        held.Remove key |> ignore
        measuredSize.Remove key |> ignore
        match live.TryGetValue key with
        | true, existing ->
            existing.Emulator.Dispose ()
            live.Remove key |> ignore
        | _ -> ()

    /// Serialize and dispatch, if the screen moved. `Serialize` waits on the emulator's own
    /// write barrier, so this reads a screen that has been drawn rather than one that is
    /// still being parsed.
    let publish (id: TerminalId) (entry: Live) =
        Async.StartImmediate (
            async {
                let! screen = entry.Emulator.Serialize ()
                if screen <> entry.Rendered then
                    entry.Rendered <- screen
                    dispatch (TerminalScreenMsg (id, screen))
            })

    /// The model as of the last sync, so the observer below has something to measure against.
    /// A box changing is not a model change — that is the whole point of watching it — so the
    /// callback cannot be handed one.
    let mutable latest : ClientModel option = None

    /// Measure what this reader is looking at, and say so — twice, to two different places,
    /// because the two answers are about different things.
    ///
    /// Into the MODEL, always: a width is a fact about this client's own window, and the
    /// command about to be queued claims it (`PendingAct.Size`). That claim is made in BLOCK
    /// mode, where nobody holds anything — so measuring only the holder, as this used to,
    /// meant block mode never had a width to claim.
    ///
    /// Over the WIRE, only for the holder: the pty has one size while a program is drawing on
    /// it, and every peer is watching the same screen, so a viewer with a narrower pane
    /// scrolls rather than reshaping everyone else's terminal.
    let measureSizes (boxes: Map<string, HTMLElement>) (model: ClientModel) =
        let mine = ClientModel.me model
        for terminal in Projection.openTerminals model.Terminals do
            let key = TerminalId.value terminal.TerminalId
            match Map.tryFind key boxes |> Option.bind measure with
            | None -> ()
            | Some (cols, rows) ->
                let last = match measuredSize.TryGetValue key with | true, v -> Some v | _ -> None
                if last <> Some (cols, rows) then
                    measuredSize.[key] <- (cols, rows)
                    dispatch (TerminalViewportMsg (terminal.TerminalId, { Cols = cols; Rows = rows }))
                    if terminal.Lease = Some mine then report terminal.TerminalId cols rows

    /// One observer, re-pointed at whichever terminal the pane is showing. A box can change
    /// without the model changing — the splitter is dragged, the window is resized, the phone
    /// is turned — and those are exactly the cases a render-loop measurement cannot see.
    ///
    /// `measuredSize` is what keeps this cheap: an observer fires on every frame of a drag, and
    /// only the frames that cross a whole character cell say anything.
    let observer =
        if ResizeObserver.isSupported () then
            Some (ResizeObserver.create (fun () -> latest |> Option.iter (measureSizes (viewports ()))))
        else None
    let mutable observed : HTMLElement option = None

    /// The pane shows one terminal at a time, so there is one box worth watching. Named from
    /// the MODEL rather than found by a bare selector: two variants of a screen and a
    /// scrollback all match, and the one that matters is the one whose terminal is selected.
    let watchViewport (boxes: Map<string, HTMLElement>) (model: ClientModel) =
        let element =
            ClientModel.selectedTerminal model
            |> Option.bind (fun terminal -> Map.tryFind (TerminalId.value terminal) boxes)
        let unchanged =
            match element, observed with
            | Some element, Some observed -> System.Object.ReferenceEquals (element, observed)
            | None, None -> true
            | _ -> false
        if not unchanged then
            observer
            |> Option.iter (fun observer ->
                observer.disconnect ()
                element |> Option.iter (fun element -> observer.observe (element :> Element)))
            observed <- element

    { Snapshot =
        fun id keyframe ->
            let key = TerminalId.value id
            // A snapshot is the whole screen, so the emulator starts again from it rather
            // than having it appended: re-seeding an emulator that already holds a screen
            // would draw the old one under the new.
            forget key
            // Opened at the size the screen was PAINTED at, never at the default: a paint is
            // a grid, and repainting it into a narrower one wraps every line that was long
            // enough to matter.
            let emulator = Emulator.openEmulator keyframe.Cols keyframe.Rows
            emulator.Write keyframe.Screen
            let entry = { Emulator = emulator; Through = keyframe.Seq; Rendered = "" }
            live.[key] <- entry
            publish id entry
      Sync =
        fun model ->
            // The keyboard follows the lease. Both ways into live mode — pressing `take`, and
            // the alt-screen flip handing a block's author the terminal it just took over —
            // remove the focused element in the render they arrive on, so without this the
            // person who now owns the keyboard is typing into `body`.
            //
            // Here rather than on the `take` press because the flip has no press to hang it
            // on: it is the Session saying the mode changed, which reaches this client
            // as a model change like any other. One edge, both routes.
            let mine = ClientModel.me model
            let showing = ClientModel.selectedTerminal model
            for terminal in Projection.openTerminals model.Terminals do
                let key = TerminalId.value terminal.TerminalId
                let isMine = terminal.Lease = Some mine
                let was = match held.TryGetValue key with | true, v -> v | _ -> false
                held.[key] <- isMine
                // Only the terminal the pane is SHOWING: a lease landing on one the reader is
                // not looking at has no screen in the document to focus, and the selector
                // would otherwise find whichever live screen happened to be on it instead.
                if isMine && not was && showing = Some terminal.TerminalId then
                    PaneShell.toTerminalScreen ()
            for terminal in Projection.openTerminals model.Terminals do
                let key = TerminalId.value terminal.TerminalId
                match live.TryGetValue key with
                // No snapshot yet: nothing to fold onto. The Process sends one when a peer
                // joins a terminal, and this runs again when it lands.
                | false, _ -> ()
                | true, entry ->
                    let feed = ClientModel.terminalFeed terminal.TerminalId model
                    // Every record but nothing else, in ONE ordered pass — the same fold the
                    // Process runs, which is what lets a snapshot from there seed the screen
                    // here. Resizes are folded alongside the output because what a program
                    // drew before a resize was drawn at the old geometry and what it drew
                    // after at the new, so applying them out of order — or not at all —
                    // reflows the wrong half of the screen. Input records are folded because
                    // they are the one rendering of a block's command: the Process does not
                    // record the shell's echo of the line it was handed (`Marks.lineFor`).
                    let fresh =
                        feed.Records
                        |> Map.toList
                        |> List.filter (fun (seq, _) -> seq >= entry.Through)
                    if not (List.isEmpty fresh) then
                        for _, record in fresh do
                            match record.Kind with
                            | TranscriptResize ->
                                // A record nobody here wrote, so a payload this cannot read is
                                // one to skip rather than to fail on.
                                match Size.parse record.Data with
                                | Some size -> entry.Emulator.Resize size.Cols size.Rows
                                | None -> ()
                            | _ -> entry.Emulator.Write record.Data
                        entry.Through <- (fresh |> List.map fst |> List.max) + 1
                        publish terminal.TerminalId entry
            // A terminal that closed keeps neither a screen nor an emulator.
            let open' =
                Projection.openTerminals model.Terminals
                |> List.map (fun t -> TerminalId.value t.TerminalId)
                |> Set.ofList
            for stale in
                Seq.append live.Keys held.Keys
                |> Seq.filter (fun k -> not (Set.contains k open'))
                |> Seq.distinct
                |> Seq.toList do
                forget stale
            // Last, because both read the document this render has just produced: which box
            // this reader is looking at, and how big it is.
            latest <- Some model
            // One look at the document for both, because both ask it the same question —
            // where are the terminal boxes — and asking twice is asking twice.
            let boxes = viewports ()
            watchViewport boxes model
            measureSizes boxes model
      Forget = fun id -> forget (TerminalId.value id) }
