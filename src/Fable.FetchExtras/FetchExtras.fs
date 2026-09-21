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
// `Headers.getSetCookie` is the same story told about a member rather than a specification:
// the package predates it, `Headers.get` joins repeated headers into one string, and a cookie
// whose `Expires` carries a comma makes that join unsplittable. So a caller that wants the
// `set-cookie`s apart has to reach past the binding for the one member that keeps them apart.
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

/// Every `set-cookie` this response's headers carried, kept apart — or nothing at all on a
/// runtime that has no `getSetCookie`.
///
/// `Headers.get` joins repeated headers with `, `, and a cookie's own `Expires` attribute
/// carries a comma, so a joined `set-cookie` cannot be split back into the cookies that made
/// it. `getSetCookie` is the only member that keeps them apart and `Fable.Fetch` — a binding
/// older than it — does not have it. Node has had it since 19.7 and every browser since 2023,
/// which is the same both-runtimes fact that puts anything in this file.
///
/// An option, not an empty array, because "this runtime cannot tell them apart" and "this
/// response set no cookies" are different answers and only a caller can say what the first one
/// means for it. The optional call is what reads that difference: `getSetCookie?.()` is
/// `undefined` where the method is absent, which is what `string array option` reads as `None`
/// — and it names `$0` once, so the headers are evaluated once however the caller spelled them.
[<Emit("$0.getSetCookie?.()")>]
let setCookies (headers: Fetch.Types.Headers) : string array option = jsNative
