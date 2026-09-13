namespace Yession.Domain

open System

/// Time, as an effect a component is handed rather than one it reaches for.
///
/// Two questions and nothing else: what time it is, and resuming once some of it has passed.
/// Both are ports for the reason `Resilience.Policy.Sleep` is one — a component that waits
/// on the system clock can only be tested by waiting with it, so every window it keeps (a
/// two-second detector, a three-second probe, an idle lease) is a real two, three or however
/// many seconds per case, and a case that wants the window to have PASSED has to sleep it
/// out. Handed a clock, the same component runs under one a test turns by hand: the window
/// passes when the test says so, in no time, and the assertion is about the component
/// rather than about this machine's scheduling.
///
/// `After` is the only way to wait. A component that also called `Async.Sleep` would have
/// one wait a test can drive and one it cannot, which is a component that hangs under a
/// hand-turned clock — so a composition passes ONE clock, and every wait goes through it.
type Clock =
    { Now : unit -> DateTimeOffset
      /// Resume once this much time has passed.
      After : TimeSpan -> Async<unit> }

module Clock =

    /// The system's clock — what the shipped composition passes.
    let system : Clock =
        { Now = fun () -> DateTimeOffset.UtcNow
          After = fun delay -> Async.Sleep (int delay.TotalMilliseconds) }

    /// Run `beat` every `interval` until the function returned is called.
    ///
    /// A loop over `After` rather than a timer of its own, so a beat — the idle-lease
    /// reclaim, the activity report, a provider poll — is driven by the same clock as every
    /// other wait, and a test that turns the clock past an interval sees the beat. Stopped
    /// by flag, not by cancellation: the wait in flight runs out and the loop ends at it,
    /// with nothing beaten after the stop.
    let every (clock: Clock) (interval: TimeSpan) (beat: unit -> unit) : unit -> unit =
        let mutable stopped = false
        Async.StartImmediate (
            async {
                while not stopped do
                    do! clock.After interval
                    if not stopped then beat ()
            })
        fun () -> stopped <- true
