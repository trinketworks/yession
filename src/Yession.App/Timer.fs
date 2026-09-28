namespace Yession.App

open System
open Elmish

/// A wait the model asks for: after `After` milliseconds, `Fire` is dispatched — unless the
/// model has stopped asking for it by then.
///
/// Declared, not armed. The model says which timers it wants as a function of its own state
/// (`ClientModel.timers`) and the program keeps exactly those running: Elmish starts a
/// subscription whose `Key` has appeared and stops one whose `Key` has gone, on every model
/// change. So every kind of wait is the same thing keyed differently. A DEBOUNCE is a timer
/// keyed by what it waits to see settle — new input, new key, and the old wait is stopped. A
/// condition held for a while is keyed by that episode; an expiry by the thing that expires.
/// Nothing ever cancels a timer by hand, which is the part hand-rolled `setTimeout` handles
/// kept getting wrong or leaving to a guard in the reducer.
///
/// The same `Key` with a different `After` is the same timer to Elmish and is not restarted:
/// a wait that should restart when its length changes carries the length in its key.
type Timer<'msg> = { Key : string list; After : int; Fire : 'msg }

module Timer =

    /// What arms a wait: call back after this many milliseconds, until disposed.
    ///
    /// The composition root's to supply, so a test hands in a clock it advances itself and
    /// runs every timer the product declares deterministically, without waiting on real time.
    type Clock = int -> (unit -> unit) -> IDisposable

    /// The platform's own clock: `setTimeout`, cleared on dispose. The same under Node and in a
    /// browser, which is what lets a headless program run the timers a page runs.
    let system : Clock =
        fun after callback ->
            let handle = Fable.Core.JS.setTimeout callback after
            { new IDisposable with
                member _.Dispose () = Fable.Core.JS.clearTimeout handle }

    /// The subscriptions that keep exactly `timers` running on `clock`.
    let subscribe (clock: Clock) (timers: Timer<'msg> list) : Sub<'msg> =
        timers |> List.map (fun timer -> timer.Key, fun dispatch -> clock timer.After (fun () -> dispatch timer.Fire))
