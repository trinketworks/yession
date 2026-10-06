module Yession.Browser.Replay

// The asciinema replay view's platform half (Plan 13, stage 3e).
//
// A closed terminal's blocks survive in the projection, but a list of commands is not the
// same artefact as the RECORDING — a replay shows the terminal as it behaved, at the speed it
// behaved, which is what someone auditing actually wants to watch.
//
// `asciinema-player` rather than the client's own renderer, and it earns itself. PR 1's
// pure-F# SGR parser (`Ansi.fs`) renders a STREAM, not a SCREEN, so a recording of anything
// that moves the cursor — `htop`, a progress bar, `vim` — would replay as garbage. That is the
// same argument the plan made for why the Session needed a real emulator rather than
// half of one, and it is why the sidecar was written as asciicast v2 in the first place: so
// the standard player replays it. The player also brings timing, seek and play/pause, which
// IS the audit-read affordance.
//
// The bindings are `Fable.AsciinemaPlayer` — the slice of the player this view uses.

open Fable.Core
open Fable.Core.JsInterop
open Fable.AsciinemaPlayer
open Fable.BrowserExtras
open Yession.App

/// A Blob URL over the `.cast` text, so the player fetches it the way it fetches any
/// recording. Built from what the client already has rather than from a new whole-file route:
/// concatenating transcript chunks reproduces the file byte for byte (see
/// `TranscriptChunk`), so the replay rides the browser's HTTP cache — which is exactly what
/// the design chose immutable chunks for.
let private blobUrl (text: string) : string =
    let cast =
        Browser.Blob.Blob.Create (
            [| box text |],
            jsOptions<Browser.Types.BlobPropertyBag> (fun o -> o.``type`` <- "text/plain")
        )
    ObjectUrls.create cast

/// Resolves once a player created NOW would measure the terminal it is going to draw.
///
/// The player measures its character cell exactly once, when it is created — a hidden
/// `.ap-term` of 80 columns, in its own stylesheet and the terminal's face — and scales every
/// recording it ever shows by that number. Nothing re-measures it. So a player created a moment
/// too early is the wrong size for its whole life, and that is how the pane's rewind came out:
/// measured under the body's proportional face with none of the sheet's borders, it scaled a
/// 58-column terminal 12% wider than its panel and pushed its own control bar underneath the
/// command row. Two things have to be true first:
///
/// - The player's stylesheet applies. The shell links it inert (`Style.deferredHeadTags`):
///   most sessions never open a recording, and a second render-blocking sheet in the head would
///   make all of them pay for the ones that do. It is turned on through the sheet's own media
///   list (`StyleSheets`) rather than the link's `media` attribute, because an attribute change
///   is applied asynchronously and a measurement in the same task does not see it — which is
///   what this used to do. The shell fetched the sheet with the page, so by the time anybody
///   opens a recording it has arrived or it never will; one that has not (it failed, or the
///   page is very new) is turned on by its attribute to apply whenever it lands, and not
///   waited for, because a link that has already failed says so to nobody and a wait on it
///   would be a replay that never mounts.
/// - The terminal's face has loaded. A web font is fetched when something first renders in it,
///   so a page that has shown nothing in it yet would have the cell measured in the fallback.
///   The family is the one the player is told to use (`--font-terminal`), read as the cascade
///   resolved it.
///
/// Idempotent, and a no-op for the sheet where no such link exists: a page may mount a replay
/// without being the shell (the editor harness links the sheet the same way the shell does).
let private measurable (hook: string) : JS.Promise<unit> =
    match Browser.Dom.document.querySelector ("link[" + hook + "]") with
    | null -> ()
    | found ->
        let link = found :?> Browser.Types.HTMLLinkElement
        match StyleSheets.ofLink link with
        | null -> link.media <- "all"
        | loaded -> loaded.media.mediaText <- "all"
    let family = Css.computedProperty Browser.Dom.document.documentElement "--font-terminal"
    Fonts.load ("15px " + family)
    |> Promise.map ignore
    // A family the shorthand cannot parse (a page with no such token) is the fallback face,
    // which is all there was to wait for.
    |> Promise.catch ignore

/// One mounted replay, and how to take it down.
type Mounted =
    { Dispose : unit -> unit }

/// Mount a replay into `element`, with whatever the model computed for this tab.
///
/// `element` must give the player a HEIGHT as well as a width — the pane's read-only region
/// does (`Style.paneReadonly`). `fit: "both"` scales the recorded geometry (the header's width
/// and height are what make a replay come out the shape the terminal actually was) to
/// whichever of the two runs out first. It was `"width"`, which is right only while the panel
/// is taller than the terminal is wide: a tall terminal in a short panel came out with its last
/// lines and its control bar below the panel's edge, under the command row, where nothing
/// could see or press them.
///
/// `controls: true` keeps the control bar on show. The player's default shows it for two
/// seconds after a mouse moves over it, which on a phone is never and on a desktop is a play
/// button nobody knows is there — and the bar is the audit-read affordance itself: timing,
/// seek, play and pause. Its height is reserved under the terminal either way, so showing it
/// covers nothing.
///
/// The player is created into a stage of its own, put in `element` at once and filled once the
/// player can measure (`measurable`). At once, because a mount is recognised by having
/// something in it (`PaneReplays`), and an element left empty while the font loads would be
/// mounted again by the next render.
///
/// `idleTimeLimit` compresses the long gaps a terminal spends waiting for a person — an audit
/// read of a session someone left open for an hour should not be an hour long.
///
/// `startAt` and `poster` are the player's own options (Plan 14, stage 4), which is why a
/// watch entered from a chip needs no second recording: a whole-terminal cast with a start
/// position expresses everything a slice can. Both are given in the RECORDING's clock and the
/// player maps them onto the compressed one itself; `poster: "npt:<t>"` costs nothing extra,
/// because the player builds the still by replaying to that point internally.
///
/// There is deliberately no `markers` option (Plan 25, stage 1). Chapters are written into the
/// cast as `"m"` events, so they ride the same idle compression as the records around them —
/// and supplying the option would STRIP the events in the file, which is how one mechanism
/// here stays one mechanism.
///
/// `caughtUp` is the DVR's half (Plan 14, stage 7): a rewound live terminal's cast ends at
/// the pin, so playing off its end means the reader has caught up — the handler jumps back
/// to live rather than leaving them on a stale final frame. `None` for every recording
/// whose end really is the end.
let mount (element: Browser.Types.Element) (replay: PaneReplay) (caughtUp: (unit -> unit) option) : Mounted =
    let url = blobUrl replay.Cast
    let stage = Browser.Dom.document.createElement "div"
    stage.className <- Style.replayStage
    element.appendChild stage |> ignore
    let player : Player option ref = ref None
    let disposed = ref false
    measurable Dom.playerStylesheetHook
    |> Promise.iter (fun () ->
        // Taken down while the face was loading: nothing to create, and nobody to show it to.
        if not disposed.Value then
            let created =
                Fable.AsciinemaPlayer.create
                    url
                    stage
                    { Options.fit = "both"
                      Options.controls = true
                      Options.idleTimeLimit = 2
                      // The same face a live terminal wears. A literal here meant a recording
                      // replayed in a different typeface than the terminal beside it — invisible
                      // on a box where both fell back to the platform mono, plain on any box
                      // where they did not.
                      Options.terminalFontFamily = "var(--font-terminal)"
                      Options.startAt = replay.StartAt
                      Options.poster = replay.Poster |> Option.map (sprintf "npt:%f") }
            caughtUp |> Option.iter (fun handler -> created.addEventListener ("ended", handler))
            player.Value <- Some created)
    { Dispose =
        fun () ->
            disposed.Value <- true
            player.Value |> Option.iter (fun created -> created.dispose ())
            // The Blob outlives the player unless it is revoked, and a panel someone clicks
            // through leaks one per terminal otherwise.
            ObjectUrls.revoke url }
