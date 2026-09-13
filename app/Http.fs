module Yession.Host.Http

// One request, made the same way every time this host talks to something over HTTP.
//
// `Fable.Fetch` (https://github.com/fable-compiler/fable-fetch) is the binding — a typed
// one, not a hand-rolled `Emit` string, which is what `Directory.Packages.props` already
// says of it. What used to live in those strings was the same small program written seven
// times: assemble some headers, await a fetch, read the body, and turn a rejection into a
// record saying the request never arrived. None of it was type-checked, none of it was
// reachable from a test, and each copy had drifted a little from its neighbours.
//
// What is left here is the part that is genuinely shared: the await, the catch, and the
// two shapes a request can settle into. What each CALLER does with an answer — which
// status means what, which headers it sends, what it calls a failure — stays with the
// caller, because that is the part a test should be able to ask about on its own.
//
// The await goes through `Interop.awaitPromise` rather than `Async.AwaitPromise`, and that
// is load-bearing rather than a house style: a `fetch` that rejects before the workflow
// reaches its await has no handler attached when Node checks at the end of the turn, and
// Node kills the process. `Interop.awaitPromise` settles the promise in the tick that
// created it. The JS this replaced was safe for the same reason from the other side — its
// `try/catch` was inside the promise — so moving the catch into F# without moving the
// await with it would have introduced exactly that fault.

open System
open Fable.Core

/// Why a request never produced a response.
///
/// The message where the rejection has one — a `TypeError` from the socket, an
/// `AbortError` from a deadline — and the value's own string form where it does not, which
/// is what `String((err && err.message) || err)` said in every copy of this.
let reasonOf (error: exn) : string =
    let message = error.Message
    if String.IsNullOrEmpty message then string error else message

/// How one request settled.
///
/// `Answered` is an HTTP reply WHATEVER its status: a 404 or a 500 is the server answering,
/// and every caller here distinguishes that from silence. `Unreachable` is a request that
/// produced no reply at all — nothing listening, DNS, a dropped socket, a deadline.
type Attempt<'body> =
    | Answered of Fetch.Types.Response * 'body
    | Unreachable of string

/// Request headers as the plain pairs they are on the wire.
///
/// Every header set in this host is assembled as a `(name, value) list` and handed here,
/// which is the whole reason "is this header sent, and when" is a question the cheap tier
/// can ask: the assembly is an ordinary F# function over ordinary values, and only this one
/// line knows how `Fable.Fetch` wants them.
let headers (pairs: (string * string) list) : Fetch.Types.RequestProperties =
    pairs
    |> List.map (fun (name, value) -> Fetch.Types.HttpRequestHeaders.Custom (name, box value))
    |> Fetch.requestHeaders

/// A request that must finish inside a bound, whatever the far end is doing.
///
/// `AbortSignal.timeout(...)` is the one piece `Fable.Fetch` does not bind — it is not part
/// of the fetch surface itself — so this stays a one-expression Emit, typed against the
/// package's own `AbortSignal` so it slots straight into `RequestProperties.Signal`. The
/// browser client carries the same one line (`app/browser/Browser.fs`); the two cannot be
/// one binding, because that project references neither this one nor anything this one can
/// see, and a shared home for it would be a new project holding a single line.
[<Emit("AbortSignal.timeout($0)")>]
let private abortAfter (afterMs: float) : Fetch.Types.AbortSignal = jsNative

let deadline (afterMs: float) : Fetch.Types.RequestProperties =
    Fetch.Types.RequestProperties.Signal (abortAfter afterMs)

/// A response header, or `""` when the reply did not carry one — the shape every caller
/// here wants, because each of them puts the value straight into a record whose empty
/// string already means "not said".
let headerOf (name: string) (response: Fetch.Types.Response) : string =
    match response.Headers.get name with
    | null -> ""
    | value -> value

/// A value on its way into a query string.
[<Emit("encodeURIComponent($0)")>]
let urlPart (value: string) : string = jsNative

/// One request, with its body read by `read`.
///
/// `fetchUnsafe`, not `fetch`: the plain binding throws on a non-2xx status, which would
/// fold "the server refused" back into the same channel as "the server is not there" —
/// and telling those apart is what every caller of this does first.
let attempt
    (read: Fetch.Types.Response -> JS.Promise<'body>)
    (url: string)
    (init: Fetch.Types.RequestProperties list)
    : Async<Attempt<'body>> =
    async {
        try
            let! response = Fetch.fetchUnsafe url init |> Interop.awaitPromise
            let! body = read response |> Interop.awaitPromise
            return Answered (response, body)
        with error ->
            return Unreachable (reasonOf error)
    }

/// The common case: the body as text, whatever the status said.
let text (url: string) (init: Fetch.Types.RequestProperties list) : Async<Attempt<string>> =
    attempt (fun response -> response.text ()) url init
