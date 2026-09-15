module AwaitNoSeamFixture.Client

open Fable.Core

/// No seam in reach, so nothing to point at and nothing said. This is the browser client: not
/// Node, where an unhandled rejection is a dead process rather than a console warning, and
/// with no Node-only `Interop` to reach for. Give it a seam and the rule starts asking it to
/// use one.
let fetched (promise: JS.Promise<string>) =
    async { let! value = promise |> Async.AwaitPromise in return value }
