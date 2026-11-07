module Yession.Browser.PaneReplays

// Keeping the mounted players in step with the model (Plan 13, stage 3e; Plan 14, stage 4).
//
// The view renders empty `[data-pane-replay="<tab key>"]` hosts and this attaches a player to
// each, because a `.cast` is a whole artefact rather than a value to bind and the player owns
// its own DOM once mounted. ONE mount path for all three kinds of recording — a whole
// terminal, a block's range, a stretch's — because they differ in what they play rather than
// in how they are mounted; the model turns the mount's key back into what to play
// (`ClientModel.replayFor`).
//
// Its own module because two entry points drive it: the app (`Browser.fs`) and the host-free
// shell harness the `Browser`-tier E2E runs. A second copy would be a second thing to keep
// correct, and the one that rotted would be the one nothing runs.

open Fable.BrowserExtras
open Yession.Domain
open Yession.App

let private isMounted (el: Browser.Types.Element) : bool = el.children.length > 0

/// Drive this from the render loop, after every render.
type Syncer = { Sync : ClientModel -> unit }

/// `dispatch` feeds the one message a mounted player raises on its own: a rewound cast that
/// played off its end has caught the reader up (Plan 14, stage 7), and the answer is a jump
/// back to live rather than a stale final frame.
///
/// The mounts register themselves as lit draws them (`HostRole.Replay`), and a player goes
/// with its mount as lit takes the mount away: a player whose mount is gone keeps a worker
/// alive, and there is no sweep after a render to forget it.
let create (hosts: Hosts.Hosts) (dispatch: ClientMsg -> unit) : Syncer =
    /// Mounted players by tab key, each with the mount it is in and the cast text it was
    /// mounted over — so a recording that arrived in pieces can be told apart from one that has
    /// not changed.
    let players = System.Collections.Generic.Dictionary<string, Browser.Types.Element * Replay.Mounted * string> ()

    let dispose (key: string) =
        match players.TryGetValue key with
        | true, (_, player, _) ->
            player.Dispose ()
            players.Remove key |> ignore
        | false, _ -> ()

    hosts.Watch
        HostRole.Replay
        { Came = fun _ _ -> ()
          Went =
            fun key el ->
                match players.TryGetValue key with
                | true, (mountedIn, _, _) when obj.ReferenceEquals (mountedIn, el) -> dispose key
                | _ -> () }

    let mount (model: ClientModel) (el: Browser.Types.Element) (key: string) =
        // `None` means the recording is not ready to play — chunk 0 has not arrived, or the
        // range has no end yet — or that nothing on screen answers to the key. The next
        // render runs this again.
        match ClientModel.replayFor key model with
        | None -> ()
        | Some replay ->
            let caughtUp =
                replay.BehindLive
                |> Option.map (fun terminal () -> dispatch (ReplayCaughtUpMsg terminal))
            // One player a key: one already playing it elsewhere is taken down first.
            dispose key
            players.[key] <- (el, Replay.mount el replay caughtUp, replay.Cast)

    { Sync =
        fun model ->
            for el, key in hosts.In HostRole.Replay do
                // Keyed on the mount being EMPTY rather than on a model flag: the view
                // re-renders for reasons that have nothing to do with this recording, and
                // re-mounting on every render would restart it under whoever is watching.
                if not (isMounted el) then mount model el key
            // A recording can still arrive in pieces — the chunk that carries the header, the
            // keyframe fetched beside it — and a player mounted over the earlier answer is
            // playing the wrong thing. So it is rebuilt when what it should play changes, and
            // every cast this mounts is a CLOSED range, so that happens once or twice and
            // then never again.
            //
            // Keyed on the CAST and nothing else, which bounds what this can pick up: a replay
            // whose `StartAt` moved over an unchanged recording does not remount, and the player
            // keeps the position it was built with. Nothing reaches that today — the pane mounts
            // one recording at a time, so every route that changes a start position takes the
            // mount down on the way — but a control that moved the position within a mounted tab
            // would need this loop to compare more than the text.
            for KeyValue (key, (el, _, cast)) in players |> Seq.toList do
                match ClientModel.replayFor key model with
                | Some replay when replay.Cast <> cast ->
                    dispose key
                    // Everything the mount is holding, taken out of it — so what is put
                    // back is the whole of what the player mounts over, never a second
                    // player beside the first.
                    Elements.clearChildren el
                    mount model el key
                | _ -> () }
