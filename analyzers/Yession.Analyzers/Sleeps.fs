module Yession.Analyzers.Sleeps

open FSharp.Compiler.Symbols
open FSharp.Analyzers.SDK
open Yession.Analyzers.Expressions

/// A test waits FOR something, never for a while.
///
/// A real-time sleep in a case is a guess at how long something takes. Too short and the case
/// is flaky; long enough to be safe and every run pays for it; and either way its red says only
/// that time passed, never what failed to happen in it. A flaky case is worse than none,
/// because it trains everyone to read red as noise. The suite had a sentence asking for no
/// sleeps and sixty-odd of them anyway, most of them hand-rolled polls — `do! Async.Sleep 20`
/// in a loop until something held — each with a budget and a failure message of its own.
///
/// What a case does instead is wait for the thing it means, and each kind of wait has one
/// helper: a condition (`Support.waitUntil` and its relatives, which end the moment it holds),
/// an absence (`Support.quietFor`, the one wait whose window IS the claim), a turn of the event
/// loop (`Support.nextTurn`), and a window the code under test arms (its own clock, turned by
/// hand with `Support.virtualClock`). Those helpers still call the real clock, and so do the few
/// fixtures that race an event against a deadline, because waiting is their job. They say so
/// with `[<Waits>]` on the binding, or on a module or type around it, and that is the whole
/// exemption: in plain sight where the waiting is spent. A list of exempt names kept here would
/// be a second place to look, and the one nobody reads.
///
/// A sleep is a call whose symbol IS one — `Async.Sleep`, `Thread.Sleep`, `Task.Delay`,
/// Fable.Promise's `Promise.sleep`, and Fable.Core's `JS.setTimeout` and `JS.setInterval` —
/// resolved through the typed tree. A shell's `sleep 600` in a string is not one, nor is a
/// binding that happens to be called `sleep`, nor a product function that sleeps inside: that
/// is the product's clock, and a case that cannot turn it has found a clock to inject.
///
/// The population is derived rather than named, as YES010's is: a project is judged when it
/// can NAME a `WaitsAttribute`. The suite declares one, so the suite is judged. The product
/// declares none — its waits go through `Clock`, which a test turns — and is not. The remedy
/// has to exist before the rule can demand it, and a project that declares the marker has
/// said it has one.

[<Literal>]
let Code = "YES014"

/// The exemption's name. Matched by name, as YES010 matches its seam, so a fixture declares
/// its own and is judged exactly as the suite is.
[<Literal>]
let private Marker = "WaitsAttribute"

/// Each sleep's home as FCS names it, and its name there. `Async` compiles to `FSharpAsync`,
/// which is what its members' declaring entity answers to.
let private sleeps =
    set
        [ "Microsoft.FSharp.Control.FSharpAsync", "Sleep"
          "System.Threading.Thread", "Sleep"
          "System.Threading.Tasks.Task", "Delay"
          "Promise", "sleep"
          "Fable.Core.JS", "setTimeout"
          "Fable.Core.JS", "setInterval" ]

/// A callee that cannot say where it lives is not one of these.
let private isSleep (mfv: FSharpMemberOrFunctionOrValue) =
    match (try mfv.DeclaringEntity |> Option.bind (fun e -> e.TryFullName) with _ -> None) with
    | Some home -> sleeps.Contains (home, mfv.LogicalName)
    | None -> false

let private marks (attributes: seq<FSharpAttribute>) =
    attributes |> Seq.exists (fun a -> try a.AttributeType.LogicalName = Marker with _ -> false)

/// Marked itself, or nested in a module or type that is.
let rec private markedEntity (entity: FSharpEntity) =
    (try marks entity.Attributes with _ -> false)
    || (match (try entity.DeclaringEntity with _ -> None) with
        | Some parent -> markedEntity parent
        | None -> false)

/// Whether a binding waits on purpose. A module-level `do` has no owner to mark, so it never
/// does.
let private waitsOnPurpose (owner: FSharpMemberOrFunctionOrValue option) =
    match owner with
    | None -> false
    | Some mfv ->
        (try marks mfv.Attributes with _ -> false)
        || (match (try mfv.DeclaringEntity with _ -> None) with
            | Some entity -> markedEntity entity
            | None -> false)

/// Whether this project can name the marker, which is the rule's whole population.
let private judged (ctx: CliContext) =
    Population.of' ctx
    |> List.exists (fun declaration -> try declaration.Entity.LogicalName = Marker with _ -> false)

let private message =
    "this waits in real time outside a helper whose job is to wait. A sleep in a test is a guess "
    + "at how long something takes: too short and the case is flaky, long enough to be safe and "
    + "every run pays for it, and its red says only that time passed. Wait for what the sleep "
    + "stands in for. UNTIL SOMETHING HOLDS (a sleep in a loop, or a sleep and then a look): "
    + "`Support.waitUntil` or `waitUntilWithin`, or `settledWithin` / `settledWithinAsync` when "
    + "the case has more to say than the timeout. They end the moment it holds. THAT SOMETHING "
    + "DID NOT HAPPEN (no further frame, no second run): `Support.quietFor`, the one wait whose "
    + "window is the claim. A TURN OF THE EVENT LOOP, so what is already queued runs first: "
    + "`Support.nextTurn`. A WINDOW THE CODE UNDER TEST ARMS (a debounce, a deadline, an idle "
    + "timeout): give it a clock and turn that (`Support.virtualClock`). Code that takes no clock "
    + "needs one before its window can be tested. A DEADLINE ON A WAIT OF ITS OWN: put the wait "
    + "in a helper and mark the helper `[<Waits>]`, as the helpers above are."

[<CliAnalyzer("Sleeps", "A test waits for something, never for a while", "")>]
let sleepsAnalyzer: Analyzer<CliContext> =
    fun ctx ->
        async {
            if not (Population.reportsHere ctx) || not (judged ctx) then
                return []
            else
                let offenders =
                    [ for binding in Expressions.of' ctx do
                          if not (waitsOnPurpose binding.Owner) then
                              for call in binding.Calls do
                                  if isSleep call.Callee then
                                      yield call.Where ]
                    |> List.distinct

                return
                    [ for where in offenders ->
                        { Type = "Sleeps"
                          Message = message
                          Code = Code
                          Severity = Severity.Error
                          Range = where
                          Fixes = [] } ]
        }
