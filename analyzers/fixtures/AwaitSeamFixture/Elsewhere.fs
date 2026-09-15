module AwaitSeamFixture.Elsewhere

open Fable.Core

/// Through the seam: what every call site is supposed to look like, and reported on by nothing.
let throughTheSeam (promise: JS.Promise<string>) =
    async { let! value = Interop.awaitPromise promise in return value }

/// Raw, piped. The ordinary shape of the mistake.
let piped (promise: JS.Promise<string>) =
    async { let! value = promise |> Async.AwaitPromise in return value }  // YES010

/// Raw, applied rather than piped: the rule reads the call, not how it was spelled.
let applied (promise: JS.Promise<string>) =
    async { let! value = Async.AwaitPromise promise in return value }  // YES010

/// Raw inside a lambda. Still written inside a binding that is not the seam, which is the
/// question — a rule that only looked at a binding's own top level would miss every await in a
/// callback, and callbacks are where the host's promises mostly live.
let inACallback (promises: JS.Promise<string> list) =
    promises
    |> List.map (fun promise ->
        async { let! value = promise |> Async.AwaitPromise in return value })  // YES010

/// Wrapped in `Async.Catch`, which is what makes this worth a rule rather than a comment: it
/// reads as handled and is not. The handler is on the side of the trampoline's gap that has
/// not run yet.
let caught (promise: JS.Promise<string>) =
    async {
        let! outcome = promise |> Async.AwaitPromise |> Async.Catch  // YES010
        return outcome
    }
