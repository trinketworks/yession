module Fable.FetchExtras

// The fetch surface `Fable.Fetch` (https://github.com/fable-compiler/fable-fetch) does not
// bind.
//
// That package binds `fetch` and the request and response types around it, and aborting is a
// separate specification that fetch merely CONSUMES: `RequestProperties.Signal` takes an
// `AbortSignal` the package types but gives no way to make. So a request that must finish
// inside a bound has to reach past the binding for the signal itself, and until this project
// existed both consumers reached past it separately — the host in `app/Http.fs` and the
// browser client in `app/browser/Browser.fs`, the same macro written twice, in two files
// neither of which is a place a reader looks for the shape of a platform API.
//
// One project serves both because `AbortSignal.timeout` is a GLOBAL on both runtimes: Node has
// had it since 17.3 — well under the 24 this repository builds against — and every browser the
// shell runs in has it too. That is the fact that makes a shared home possible where the
// earlier comments said it was not: nothing here assumes a `window` or a `process`, so neither
// consumer has to reference the other, and neither has to carry a runtime the other lacks.
//
// Fable-only: `dotnet build` type-checks it; Fable emits the JS that runs.

open Fable.Core
open Fable.Core.JsInterop

/// A signal that aborts `afterMs` milliseconds from now: `AbortSignal.timeout(afterMs)`.
///
/// The platform keeps the timer, so a caller has nothing to cancel when its request settles
/// first — which is the reason to prefer this over an `AbortController` and a `setTimeout`
/// that outlives the request it was armed for.
///
/// Typed as `Fable.Fetch`'s own `AbortSignal` so it goes straight into
/// `Fetch.Types.RequestProperties.Signal`. When it fires, the fetch rejects with an
/// `AbortError` — the same channel a dropped socket arrives on, which is what lets a caller
/// treat a deadline and an unreachable far end as the one outcome: no reply.
[<Emit("AbortSignal.timeout($0)")>]
let timeoutSignal (afterMs: float) : Fetch.Types.AbortSignal = jsNative
