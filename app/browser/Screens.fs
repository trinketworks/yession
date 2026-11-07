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

/// The box each terminal's output is laid into, whichever mode it is in: the live screen while
/// a program holds it, the block scrollback while commands do. ONE role, because it is one
/// question — how wide is what this reader is looking at — and the pane shows one of the two
/// at a time. It used to name only the screen, which is why block mode never had a width:
/// there was nothing to measure until somebody took the keyboard.
///
/// Each box registers itself under its terminal's id as lit draws it (`HostRole.Viewport`), so
/// finding them asks the document nothing. It used to: one compound attribute selector per
/// open terminal per render, every one but the shown terminal's a MISS, which is the most
/// expensive question there is (nothing is indexed and the walk cannot stop early) — measured
/// on a session of 20,650 events (54k nodes, 12 open terminals): 616 of them, 5.3 seconds, a
/// third of all the CPU a cold open spent. Then one scan for all of them, every render. Now
/// none.
///
/// First in document order where a terminal holds both boxes at once, as the selector list
/// answered: the same one it always resolved to.
let private viewports (hosts: Hosts.Hosts) : Map<string, HTMLElement> =
    (Map.empty, hosts.In HostRole.Viewport)
    ||> List.fold (fun boxes (found, id) ->
        let element = found :?> HTMLElement
        match Map.tryFind id boxes with
        // `4` is DOCUMENT_POSITION_FOLLOWING: the held box comes first.
        | Some held when (int (held.compareDocumentPosition element) &&& 4) <> 0 -> boxes
        | Some _
        | None -> Map.add id element boxes)

/// A CSS length as pixels, or zero. `getPropertyValue` answers `"12px"` for a resolved length
/// and `""` for anything it cannot resolve, and only the first is a number to subtract.
let private pixels (element: HTMLElement) (property: string) : float =
    match System.Double.TryParse ((computedProperty element property).Trim().Replace ("px", "")) with
    | true, pixels -> pixels
    | _ -> 0.0

/// A known run of characters put in a box to be read: the CELL comes from what it takes —
/// the only way to get a font's advance width without hard-coding one — wearing the class the
/// output really renders in, so the font measured is the font used. `white-space: pre` inline
/// over that class, because it wraps, and a wrapped run measures the box instead of the text.
///
/// Placed, read and taken out in three steps rather than one, so a render can put the read in
/// its one read pass (`Render`): putting it in is a write, and a read after a write is a layout
/// the browser must do there and then. Out of flow at the box's top corner and out of sight,
/// so it moves nothing while it is in, and short — ten cells — so it overflows no box: a
/// reader's place at the end (`Tail`), read while it is in, is the place it will have once it
/// is out.
let private cellsOfProbe = 10

let private placeProbe (box: HTMLElement) : HTMLElement =
    let probe = document.createElement "span"
    probe.className <- Style.terminalOutput
    setStyleProperty probe "position" "absolute"
    setStyleProperty probe "top" "0"
    setStyleProperty probe "left" "0"
    setStyleProperty probe "visibility" "hidden"
    setStyleProperty probe "white-space" "pre"
    probe.textContent <- Array.create cellsOfProbe "M" |> String.concat ""
    box.appendChild probe |> ignore
    probe

/// The viewport size in CHARACTER CELLS, measured from the rendered page rather than assumed:
/// the pane is one width on a desktop and another on a phone, and the program on the other end
/// lays its screen out to whatever it is told. Reads only: the probe is already in the box.
///
/// Two measurements, neither of them a constant: the CELL off the probe (`placeProbe`), and
/// the BOX, `clientWidth` less its padding. That already excludes the border and any
/// scrollbar, which are exactly the pixels no character is drawn on. Read rather than
/// subtracted as a constant — the two modes pad differently, and the version of this that
/// hard-coded `- 24` was one restyle from being quietly wrong.
let private measure (box: HTMLElement) (probe: HTMLElement) : (int * int) option =
    let rect = probe.getBoundingClientRect ()
    let cell = rect.width / float cellsOfProbe
    let line = rect.height
    if not (cell > 0.0) || not (line > 0.0) then None
    else
        let width = box.clientWidth - pixels box "padding-left" - pixels box "padding-right"
        let height = box.clientHeight - pixels box "padding-top" - pixels box "padding-bottom"
        let cols = int (floor (width / cell))
        let rows = int (floor (height / line))
        if cols > 0 && rows > 0 then Some (cols, rows) else None

/// The probes a measurement put in the page, by terminal id, with the box each is in: what
/// `Read` reads and then takes out.
type Measuring = private Measuring of (string * HTMLElement * HTMLElement) list

/// One terminal's screen as this client composes it.
type private Live =
    { Emulator : Emulator
      /// The transcript position the emulator has been fed through.
      mutable Through : int }

type Screens =
    { /// The Process's screen for a terminal: the transcript position it represents, and the
      /// size it was painted at.
      Snapshot : TerminalId -> TranscriptKeyframe -> unit
      /// Fold everything the model has that this client's screens have not seen, and
      /// dispatch any that changed. Safe to call after every render: a screen that did not
      /// move dispatches nothing.
      Sync : ClientModel -> unit
      /// The first step of measuring every box the render drew: a probe into each (writes).
      Place : unit -> Measuring
      /// The second: read each box's size off its probe (reads only), and hand back the third
      /// — the probes taken out and the sizes told to the model — for after the render's
      /// other reads. Measuring is asked of the render, never done by `Sync`, so it can stand
      /// in the render's one read pass.
      Read : Measuring -> (unit -> unit)
      /// Drop a terminal's emulator. An emulator is a live object with a worker behind it;
      /// one left behind for a terminal nobody is in is a leak with no screen.
      Forget : TerminalId -> unit }

/// Each terminal's viewport is MEASURED here, and only measured: the size is dispatched as a
/// `TerminalViewportMsg`, and whether it goes on to the Session as a pty resize is the model's
/// decision (`ClientModel.ptyResizes`), which knows who holds the lease and what was measured
/// last. It used to be decided here, beside the measurement, where only a browser could test
/// it — and a rule only the browser tier reaches is a rule the cheap tier never re-checks.
///
/// The measuring lives here, with the screen, rather than in the app beside the connection.
/// The element to measure is the one this already tracks, and the moment to measure is a
/// render this already runs after. In the app it was also unreachable — the browser tier
/// drives the harness, which has no copy of it — which is how the one thing it did wrong went
/// unnoticed: it ran only from `setState`, so a splitter drag or a window resize, which change
/// the box and dispatch nothing, never measured anything at all.
let create (hosts: Hosts.Hosts) (dispatch: ClientMsg -> unit) (current: unit -> ClientModel option) : Screens =
    let live = System.Collections.Generic.Dictionary<string, Live> ()

    let forget (key: string) =
        match live.TryGetValue key with
        | true, existing ->
            existing.Emulator.Dispose ()
            live.Remove key |> ignore
        | _ -> ()

    /// Serialize and dispatch, if the screen moved. `Serialize` waits on the emulator's own
    /// write barrier, so this reads a screen that has been drawn rather than one that is
    /// still being parsed.
    ///
    /// Moved means different from what the LATEST model holds (`TerminalScreens`), asked
    /// once the screen is read rather than when it was asked for: an unchanged screen told
    /// again is a message, an update and a render for nothing, into a render loop that would
    /// then ask for it again. The cursor is part of it: a caret moving along a line the
    /// program did not redraw is still the screen moving.
    let publish (id: TerminalId) (entry: Live) =
        Async.StartImmediate (
            async {
                let! screen = entry.Emulator.Screen ()
                let held = current () |> Option.bind (fun model -> Map.tryFind id model.TerminalScreens)
                if held <> Some screen then dispatch (TerminalScreenMsg (id, screen))
            })

    /// Measure what this reader is looking at, and tell the model — for every terminal, held or
    /// not: a width is a fact about this client's own window, and the command about to be
    /// queued claims it (`PendingAct.Size`). That claim is made in BLOCK mode, where nobody
    /// holds anything — so measuring only the holder, as this used to, meant block mode never
    /// had a width to claim. What reaches the pty, and only for the holder, the model decides.
    ///
    /// Told only when it differs from what the model already holds (`TerminalViewports`).
    /// Not the pty's de-duplication — the model asks for a resize only when the size changed —
    /// but the render loop's: every message costs an update and a render, this runs after every
    /// render, and the observer below fires on every frame of a drag. Asked of the LATEST
    /// model (`current`), not the one a sync was handed: a model that came during the frame
    /// and has not been drawn yet may already hold the size, and telling it again is a message
    /// for nothing. `measure` refuses a zero box as the model does, so what is told is kept.
    let place () : Measuring =
        match current () with
        | None -> Measuring []
        | Some model ->
            let boxes = viewports hosts
            Projection.openTerminals model.Terminals
            |> List.choose (fun terminal ->
                let key = TerminalId.value terminal.TerminalId
                Map.tryFind key boxes |> Option.map (fun box -> key, box, placeProbe box))
            |> Measuring

    let read (Measuring probes) : unit -> unit =
        let sizes = probes |> List.map (fun (key, box, probe) -> key, measure box probe)
        fun () ->
            for _, box, probe in probes do
                box.removeChild probe |> ignore
            match current () with
            | None -> ()
            | Some model ->
                for terminal in Projection.openTerminals model.Terminals do
                    match sizes |> List.tryPick (fun (key, size) -> if key = TerminalId.value terminal.TerminalId then size else None) with
                    | None -> ()
                    | Some (cols, rows) ->
                        let size = { Cols = cols; Rows = rows }
                        if Map.tryFind terminal.TerminalId model.TerminalViewports <> Some size then
                            dispatch (TerminalViewportMsg (terminal.TerminalId, size))

    /// One observer, watching every terminal's box from the moment lit draws it to the moment
    /// lit takes it away. A box can change without the model changing — the splitter is
    /// dragged, the window is resized, the phone is turned — and those are exactly the cases a
    /// render-loop measurement cannot see.
    ///
    /// Comparing against the model is what keeps this cheap: an observer fires on every frame
    /// of a drag, and only the frames that cross a whole character cell say anything. The pane
    /// draws one terminal at a time, so there is one box, or two for the frame its mode
    /// changes in.
    let observer =
        if ResizeObserver.isSupported () then
            Some (ResizeObserver.create (fun () -> (read (place ())) ()))
        else None
    hosts.Watch
        HostRole.Viewport
        { Came = fun _ el -> observer |> Option.iter (fun observer -> observer.observe el)
          Went = fun _ el -> observer |> Option.iter (fun observer -> observer.unobserve el) }

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
            let entry = { Emulator = emulator; Through = keyframe.Seq }
            live.[key] <- entry
            publish id entry
      Sync =
        fun model ->
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
                live.Keys
                |> Seq.filter (fun k -> not (Set.contains k open'))
                |> Seq.toList do
                forget stale
      Place = place
      Read = read
      Forget = fun id -> forget (TerminalId.value id) }
