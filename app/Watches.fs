module Yession.Host.Watches

// Keeping an eye on something outside the session, with nothing in it that names WHAT. A
// pull request is one kind of watch (`PrWatches.fs`); this is the part every kind shares,
// written once so that catching up — after an idle stop, a crash, an outage — is not a
// feature any kind implements, but simply what a look already is.
//
// The idea it rests on: a watch's last known state is DURABLE (the log folds it, per kind),
// and a look compares a fresh reading of the source against it. So the first look after a
// stop compares against whatever the log last recorded, and finds everything that moved in
// between exactly as a routine look finds what moved in fifteen seconds. There is no
// catch-up path to get wrong; there is one path.
//
// What a kind supplies (`Kind`): how to look (its provider, its conditional-request cursor),
// how to tell what moved (`Detect`, pure, per kind, beside its facts), how to advance what
// is known, how soon to look again, and how to record what it found. Everything else is
// here — the cadence, the source's own "come back later", one look in flight per watch, a
// burst of pokes collapsing into one extra look, whose credential looks, and the rows a
// query reads — and a second kind of watch gets all of it by writing its five.

open System
open Yession.Domain

/// Why a look produced nothing, in the terms this engine acts on. The kind classifies its
/// own provider's failures into this, because only it can read them.
type Refusal =
    { /// What the query shows instead of a state: what is wrong, in a sentence a person
      /// can act on.
      Health : string
      /// The source said when to come back — a rate limit's reset — as an epoch second.
      /// Nothing is asked of it before then, pokes included.
      HoldUntilEpoch : int64 option
      /// The credential is dead: the one failure that is news to whoever holds it.
      CredentialRejected : bool }

/// One look's answer.
type Look<'Snapshot, 'Cursor> =
    /// A fresh reading, and the cursor to ask conditionally with next time.
    | Read of 'Snapshot * 'Cursor
    /// Nothing the look read has moved: nothing to fold, nothing to say.
    | Unmoved
    | Refused of Refusal

/// What a kind of watch supplies. See the header for why it is exactly this.
type Kind<'Key, 'Snapshot, 'Known, 'Cursor, 'Change> =
    { /// One look: the credential resolved for the watcher, the key, the cursor from the
      /// last look, and the last reading it was taken beside.
      Look : string option -> 'Key -> 'Cursor -> 'Snapshot option -> int64 -> Async<Look<'Snapshot, 'Cursor>>
      /// What moved between what is known and a fresh reading. Pure, per kind.
      Detect : 'Known -> 'Snapshot -> 'Change list
      /// What is known after one recorded change.
      Advance : 'Known -> 'Change -> 'Known
      /// How many seconds until this watch is next due, from what a look found — `None`
      /// for a look that failed.
      DueIn : 'Snapshot option -> int64
      /// The cursor a watch starts with, before any look.
      NoCursor : 'Cursor
      /// Make what moved durable: whose watch, which key, the reading, and the changes. The
      /// engine calls this and only then advances what it knows, so what it knows never
      /// runs ahead of what the log says.
      Record : Principal -> 'Key -> 'Snapshot -> 'Change list -> Async<unit> }

/// One watch as the durable projection has it.
type Watch<'Key, 'Known> =
    { Key : 'Key
      Watcher : Principal
      Known : 'Known
      /// When it last became what it is: the envelope of the watch's start or its last
      /// recorded change.
      Since : DateTimeOffset }

/// One watch as a query reads it: what the log knows and what this process has seen.
type Row<'Key, 'Snapshot, 'Known> =
    { Key : 'Key
      Watcher : Principal
      /// `None` until this process has looked — which is not a state, and nothing should
      /// guess one from `Known` in its place: that is the log's last word, and the world may
      /// have moved on while nothing was running.
      Snapshot : 'Snapshot option
      Known : 'Known
      Since : DateTimeOffset
      /// Has a poke ever reached this watch?
      Pushed : bool
      /// `None` while the last look worked; what is wrong otherwise.
      Health : string option }

type private Entry<'Key, 'Snapshot, 'Known, 'Cursor> =
    { Key : 'Key
      Watcher : Principal
      mutable Known : 'Known
      mutable Since : DateTimeOffset
      mutable Snapshot : 'Snapshot option
      mutable Cursor : 'Cursor
      mutable Health : string option
      /// The source said to come back later; the epoch second it named.
      mutable HoldUntilEpoch : int64 option
      /// Our own cadence: the epoch second this watch is next due. Zero until its first
      /// look, which is what makes a watch this process has not yet looked at due at once.
      mutable DueAtEpoch : int64
      /// One look in flight per watch: two overlapping looks could each detect the same
      /// change and record it twice.
      mutable InFlight : bool
      /// A poke that arrived mid-look, served once by that look when it finishes.
      mutable PokeAgain : bool
      mutable Pushed : bool }

/// A kind's watches, looked after.
///
/// Qualified because `PrWatches.PrWatchers` carries the same four labels: a bare
/// construction would build whichever of the two was declared last, silently.
[<RequireQualifiedAccess>]
type Watchers<'Key, 'Snapshot, 'Known> =
    { /// Reconcile against the projection — at boot, and after every watch or unwatch. An
      /// entry that is still there keeps its cursor and its last reading, so reconciling
      /// costs nothing.
      Apply : Watch<'Key, 'Known> list -> unit
      /// Look at every watch that is due. `true` when anything a query shows moved.
      Poll : unit -> Async<bool>
      /// Look NOW at every watch the predicate picks, whatever its cadence says — never
      /// inside a hold the source named.
      Poke : ('Key -> bool) -> Async<bool>
      Rows : unit -> Row<'Key, 'Snapshot, 'Known> list }

let create
    (now: unit -> DateTimeOffset)
    (resolveToken: CredentialFor -> Async<string option>)
    (onUnauthorized: CredentialFor -> Async<unit>)
    (kind: Kind<'Key, 'Snapshot, 'Known, 'Cursor, 'Change>)
    : Watchers<'Key, 'Snapshot, 'Known> =

    let mutable entries : Entry<'Key, 'Snapshot, 'Known, 'Cursor> list = []

    let apply (watches: Watch<'Key, 'Known> list) : unit =
        entries <-
            watches
            |> List.map (fun watch ->
                match entries |> List.tryFind (fun e -> e.Key = watch.Key && e.Watcher = watch.Watcher) with
                // Kept, cursor and all — the projection's baseline still wins, because a
                // recorded change advanced both and they cannot disagree.
                | Some existing ->
                    existing.Known <- watch.Known
                    existing.Since <- watch.Since
                    existing
                | None ->
                    { Key = watch.Key
                      Watcher = watch.Watcher
                      Known = watch.Known
                      Since = watch.Since
                      Snapshot = None
                      Cursor = kind.NoCursor
                      Health = None
                      HoldUntilEpoch = None
                      DueAtEpoch = 0L
                      InFlight = false
                      PokeAgain = false
                      Pushed = false })

    let lookOnce (force: bool) (entry: Entry<'Key, 'Snapshot, 'Known, 'Cursor>) : Async<bool> =
        async {
            let nowEpoch = (now ()).ToUnixTimeSeconds ()
            let held = entry.HoldUntilEpoch |> Option.exists (fun until -> until > nowEpoch)
            // A poke overrides OUR cadence and never the source's hold — asking inside a
            // window the source already named would spend a request to be refused.
            if held || (not force && entry.DueAtEpoch > nowEpoch) then return false
            else
                entry.HoldUntilEpoch <- None
                let! token = resolveToken (CredentialFor.Person entry.Watcher)
                let! outcome = kind.Look token entry.Key entry.Cursor entry.Snapshot nowEpoch
                // Whatever it found, this watch has had its turn: the next one is scheduled
                // from what it now knows.
                let schedule (reading: 'Snapshot option) = entry.DueAtEpoch <- nowEpoch + kind.DueIn reading
                match outcome with
                | Unmoved ->
                    schedule entry.Snapshot
                    return false
                | Read (snapshot, cursor) ->
                    let changes = kind.Detect entry.Known snapshot
                    if not (List.isEmpty changes) then
                        do! kind.Record entry.Watcher entry.Key snapshot changes
                        entry.Known <- changes |> List.fold kind.Advance entry.Known
                    let moved = entry.Snapshot <> Some snapshot || entry.Health <> None
                    entry.Snapshot <- Some snapshot
                    entry.Cursor <- cursor
                    entry.Health <- None
                    schedule (Some snapshot)
                    return moved
                | Refused refusal ->
                    if refusal.CredentialRejected then do! onUnauthorized (CredentialFor.Person entry.Watcher)
                    entry.HoldUntilEpoch <- refusal.HoldUntilEpoch
                    let moved = entry.Health <> Some refusal.Health
                    entry.Health <- Some refusal.Health
                    schedule None
                    return moved
        }

    let rec look (force: bool) (entry: Entry<'Key, 'Snapshot, 'Known, 'Cursor>) : Async<bool> =
        async {
            if force then entry.Pushed <- true
            if entry.InFlight then
                entry.PokeAgain <- entry.PokeAgain || force
                return false
            else
                entry.InFlight <- true
                let! moved = lookOnce force entry
                entry.InFlight <- false
                if entry.PokeAgain then
                    entry.PokeAgain <- false
                    let! again = look true entry
                    return moved || again
                else
                    return moved
        }

    let across (force: bool) (picked: Entry<'Key, 'Snapshot, 'Known, 'Cursor> list) =
        async {
            let mutable moved = false
            // A snapshot of the list, so a watch added mid-pass is picked up by the next one
            // rather than mutating what this one is walking.
            for entry in picked do
                let! entryMoved = look force entry
                moved <- moved || entryMoved
            return moved
        }

    { Apply = apply
      Poll = fun () -> across false entries
      Poke = fun pick -> across true (entries |> List.filter (fun e -> pick e.Key))
      Rows =
        fun () ->
            entries
            |> List.map (fun e ->
                { Key = e.Key
                  Watcher = e.Watcher
                  Snapshot = e.Snapshot
                  Known = e.Known
                  Since = e.Since
                  Pushed = e.Pushed
                  Health = e.Health }) }
