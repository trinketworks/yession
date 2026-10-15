module SleepFixture.Sleeps

open System
open System.Threading
open System.Threading.Tasks
open Fable.Core

// The forms YES014 is about. A line ending `// YES014` must be reported; every other line
// here must not be. One file is enough: the rule is per call site, so one site can both break
// it and prove it still sees a break.

/// The exemption, declared here as the suite declares its own. Declaring it is also what puts
/// this project in the rule's population.
[<AttributeUsage(AttributeTargets.Method ||| AttributeTargets.Property ||| AttributeTargets.Class)>]
type WaitsAttribute () =
    inherit Attribute ()

// Every sleep the rule knows, each on a line of its own.
let asyncSleep = async { do! Async.Sleep 20 } // YES014
let threadSleep () = Thread.Sleep 20 // YES014
let taskDelay () = Task.Delay 20 // YES014
let promiseSleep () = Promise.sleep 20 // YES014
let timer () = JS.setTimeout ignore 20 // YES014
let interval () = JS.setInterval ignore 20 // YES014

// A hand-rolled poll: the shape most of the suite's sleeps had.
let rec poll (held: unit -> bool) =
    async {
        if not (held ()) then
            do! Async.Sleep 20 // YES014
            return! poll held
    }

// Inside a case's body, whose owner is the list of cases: nothing marks it, so it waits for
// nothing on purpose.
let cases = [ async { do! Async.Sleep 20 } ] // YES014

// A helper whose job is to wait says so, and is left alone.
[<Waits>]
let quietFor (ms: int) = Async.Sleep ms

// So is everything in a module marked as a whole, nested modules included.
[<Waits>]
module Deadlines =
    let raced (ms: int) = Task.Delay ms

    module Nested =
        let pause (ms: int) = Thread.Sleep ms

// A member marks itself; its sibling is not covered by it.
type Waiter () =
    [<Waits>]
    member _.Pause (ms: int) = Async.Sleep ms

    member _.Guess () = Async.Sleep 20 // YES014

// Calling a helper is not a sleep: it is the whole point of one.
let quiet = quietFor 20

// What only LOOKS like a sleep is none: a shell's `sleep` in a string, a binding named after one.
let shell = "sleep 600"
let sleep (ms: int) = ms
let notASleep = sleep 20
