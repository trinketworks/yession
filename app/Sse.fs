module Yession.Host.Sse

// Server-sent events, both ends, once. Five routes across the Manager push over SSE — the three
// control reverse legs (`/control/notifications`, `/control/mcp`, `/control/connections`), the
// session registry stream, and the management page's rows — and each had its own copy of the same
// six lines: headers, an opening comment, a sink, a subscription, a 15s heartbeat, teardown on
// request close. So did the consuming side, twice. This module owns the protocol; a route supplies
// only what is actually its own — how to encode a payload, and what to subscribe to.
//
// The subscribe/sink/unsubscribe shapes are the repo's push vocabulary (`Yession.Domain.Push`), so
// a hub's `Register` plugs straight in with no adapter.

open Fable.Core
open Fable.Core.JsInterop
open Fable.NodeExtras
open Yession.Domain
open Yession.Host.Interop

/// Renders a payload as one SSE event's data. May be multi-line — `frame` handles that.
type Encode<'a> = 'a -> string

// The keep-alive interval, so an idle subscription is not reaped by an HTTP idle timeout.
[<Emit("setInterval($1, $0)")>]
let private setInterval (ms: int) (callback: unit -> unit) : obj = jsNative

[<Emit("clearInterval($0)")>]
let private clearInterval (handle: obj) : unit = jsNative

/// One SSE event: every line of the payload becomes its own `data:` line, which a client rejoins
/// with newlines (the browser's `EventSource` does exactly that, and so does `subscribe` below).
/// Single-line payloads — every JSON frame on the control legs — are just the one-line case, so
/// rendered markup and wire JSON need no different treatment.
let frame (payload: string) : string =
    let lines =
        payload.Replace("\r\n", "\n").Split '\n'
        |> Array.map (fun line -> "data: " + line)
    String.concat "\n" lines + "\n\n"

/// The `data:` payload of one SSE event, or `None` for a comment-only event (`: subscribed`,
/// `: ping`) — the inverse of `frame`, and the parsing half of `subscribe`.
let dataOf (event: string) : string option =
    let data =
        event.Replace("\r\n", "\n").Split '\n'
        |> Array.filter (fun line -> line.StartsWith "data:")
        |> Array.map (fun line ->
            let value = line.Substring 5
            if value.StartsWith " " then value.Substring 1 else value)
    if Array.isEmpty data then None else Some (String.concat "\n" data)

/// Serve an SSE stream: write the headers (and an opening comment, so the client sees them at
/// once), register a sink that frames every payload onto the response, keep the connection alive,
/// and tear both the heartbeat and the subscription down when the request closes.
///
/// Returns the sink, for a route that must push a frame of its own beyond what it subscribed to —
/// `/control/connections` answers with an awaited snapshot that way.
let stream (req: IncomingMessage) (res: ServerResponse) (encode: Encode<'a>) (subscribe: Subscribe<'a>) : Sink<'a> =
    res.writeHead (
        200,
        createObj
            [ "content-type", box "text/event-stream"
              "cache-control", box "no-store"
              "connection", box "keep-alive" ])
    |> ignore
    res.write ": subscribed\n\n" |> ignore
    let sink : Sink<'a> = fun payload -> res.write (frame (encode payload)) |> ignore
    let subscription = subscribe sink
    let heartbeat = setInterval 15000 (fun () -> res.write ": ping\n\n" |> ignore)
    req.on ("close", fun _ ->
        clearInterval heartbeat
        subscription.Stop ())
    |> ignore
    sink

/// Why a connect attempt left no stream open.
[<RequireQualifiedAccess>]
type Refusal =
    /// The server ANSWERED, and this is what it said.
    | Answered of status: int
    /// Nothing answered — the host is not there, the name did not resolve, the socket dropped
    /// before a response arrived. There is no status, and inventing one (0, -1) would be a
    /// number a caller could mistake for something a server said.
    | Unanswered

/// Whether a connect that left no stream open should be tried again, asked once per failed
/// attempt with what that attempt came back as.
///
/// The default is yes, forever, and that is right for every leg inside this product: the
/// Manager is coming back, and a stream is the only way it can reach us. It is wrong the
/// moment we talk to a server somebody else wrote — a refusal can mean "this endpoint does
/// not exist here", which is permanent, and retrying it every second is a hot loop against a
/// server behaving correctly.
///
/// Both cases are the caller's to judge, and that is why this takes a value rather than a
/// status: an `int -> bool` cannot be asked about a host that never answered, so every
/// unanswered connect used to reconnect at a fixed second FOREVER, whatever the caller had
/// decided. A caller that knows the far end is optional says so once, rather than dialling a
/// dead address for the life of the process. This module does not guess either outcome.
type Retry = Refusal -> bool

module Retry =

    /// Keep trying whatever came back — a status, or nothing at all. What the control legs and
    /// the registry stream want: the peer is ours, and its absence is always temporary, whether
    /// it is refusing us or not up yet.
    let always : Retry = fun _ -> true

/// The whole events in what has arrived so far, and the tail that is not one yet.
///
/// A stream of events is a stream of BYTES: one read can carry three events, or half of one, and
/// what separates them is a blank line. So every read hands what it has to this, dispatches what
/// came back whole, and keeps the remainder for the next read to finish. A remainder still left
/// when the connection ends is DISCARDED — half an event is not an event, and the reconnect
/// starts its buffer empty.
let events (buffered: string) : string list * string =
    let rec loop (whole: string list) (rest: string) =
        match rest.IndexOf "\n\n" with
        | -1 -> List.rev whole, rest
        | boundary -> loop (rest.Substring (0, boundary) :: whole) (rest.Substring (boundary + 2))

    loop [] buffered

/// What one connect attempt decided about the next one.
[<RequireQualifiedAccess>]
type private Attempt =
    /// Connect again after the backoff. Every ordinary end of a stream is this one: a server that
    /// closed, and any failed attempt — answered or not — the caller called temporary.
    | Reconnect
    /// Stop for good, because the caller called this refusal — a status, or a silence —
    /// permanent. The subscription is left
    /// inert rather than errored — and, like an unsubscribe, holding nothing: the connection the
    /// refusal arrived on is released rather than left to the keep-alive pool for a stream nobody
    /// will ever read.
    | Finished

/// How long a connection that ended waits before it is made again. Fixed rather than backed off,
/// because the peer these legs talk to is ours: it is restarting, not overloaded, and a growing
/// delay would only lengthen the window in which its news does not reach us.
let private retryAfterMs = 1000

// Consume an SSE stream: connect (with whatever request headers the caller's authentication
// wants — a control secret, a strategy's identity assertion, or none), hand each event's rejoined
// `data:` lines to the sink (whose own exceptions are its bug and stay out of this loop's
// decisions), reconnect with a fixed backoff when the connection drops, and cancel
// both the retry loop and the live fetch when the subscription ends — on unsubscribe, and equally
// on a refusal the caller called permanent. Best-effort by design: a transport error is a dropped
// connection the caller is asked about, never thrown.
let private openStream
    (url: string)
    (headers: (string * string) list)
    (onEvent: Sink<string>)
    (retry: Retry)
    : (unit -> unit) =
    let controller = Fetch.newAbortController ()

    // Written by the teardown, read by the loop, and the only thing that tells the two ends of a
    // connection apart: aborting alone would not stop a subscription, because the loop's answer to
    // a request that ended is to make another one.
    let mutable cancelled = false

    // Let go of whatever connection is live. Both ways a subscription ENDS go through this —
    // the teardown below, and the loop deciding it will never connect again — because a
    // subscription that has stopped holds nothing open either way, and the in-flight response
    // of a refusal nobody will read is as much of a held socket as a stream is.
    let release () = controller.abort ()

    let request =
        [ Fetch.Types.RequestProperties.Method Fetch.Types.HttpMethod.GET
          // `accept` goes on LAST so it wins over a caller that named one: this connection reads
          // an event stream or it reads nothing.
          Http.headers (headers @ [ "accept", "text/event-stream" ])
          Fetch.Types.RequestProperties.Signal controller.signal ]

    // A sink that throws is OUR bug and must not reach the attempt decision: a dropped socket is
    // retried, where a broken subscriber retried forever silently drops every event it was given.
    // Said once here, and the stream carries on — one bad event is not a dead connection.
    //
    // The url names the connection because it is safe to: the control legs authenticate with a
    // header (`x-yession-control`), the registry and query streams with a header or a cookie, and
    // nothing in this repository puts a credential in an SSE url. A caller that ever does has to
    // give this line another name for the connection.
    let deliver (event: string) =
        try
            onEvent event
        with error ->
            eprintfn "sse subscriber for %s threw on an event: %s" url (Http.reasonOf error)

    let drain (reader: ReadableStreamDefaultReader) =
        // One decoder for the whole connection, because a multi-byte character split across two
        // reads is completed by the decoder holding its tail; one buffer beside it, because an
        // event split across two reads is completed the same way, a few bytes further up.
        let decoder = createDecoder ()

        let rec loop (buffered: string) =
            async {
                let! chunk = reader.read () |> Interop.awaitPromise

                if chunk.``done`` then
                    return ()
                else
                    let text = chunk.value |> Option.map (decodeChunk decoder) |> Option.defaultValue ""
                    let whole, tail = events (buffered + text)
                    whole |> List.iter deliver
                    return! loop tail
            }

        loop ""

    let connect () =
        async {
            let! response = Fetch.fetchUnsafe url request |> Interop.awaitPromise

            if not response.Ok then
                // A refusal is the server ANSWERING, so what the caller judges is the status it
                // sent. The other way an attempt ends with no stream — nothing answering at all —
                // reaches `retry` in `run` below, where the transport fault arrives.
                return (if retry (Refusal.Answered response.Status) then Attempt.Reconnect else Attempt.Finished)
            else
                // A response with no body to read is a 204, or the answer to a HEAD. Nothing this
                // module asks for answers that way, and the reconnect is what covers a server that
                // did: there is no stream here NOW, so ask again in a second.
                match responseBody response with
                | Some body -> do! drain (body.getReader ())
                | None -> ()

                return Attempt.Reconnect
        }

    let rec run () =
        async {
            let! attempt =
                async {
                    try
                        return! connect ()
                    with _ ->
                        // Two unrelated things land here: a transport fault — nothing listening, a
                        // name that did not resolve, a socket that dropped mid-stream — and the
                        // abort the teardown fires. `cancelled` is what tells them apart, and only
                        // the first is a refusal: an unsubscribe is not an outcome the caller gets
                        // a vote on, and asking would hand it one. The abort `release ()` fires on
                        // a permanent refusal cannot arrive here at all — that branch has already
                        // left this workflow.
                        if cancelled then
                            return Attempt.Reconnect
                        else
                            return (if retry Refusal.Unanswered then Attempt.Reconnect else Attempt.Finished)
                }

            match attempt with
            | Attempt.Finished ->
                release ()
                return ()
            | Attempt.Reconnect ->
                if cancelled then
                    return ()
                else
                    do! Async.Sleep retryAfterMs
                    if cancelled then return () else return! run ()
        }

    // Started immediately rather than scheduled: the first connect goes out in the tick that
    // subscribed, which is what a caller that subscribes and then provokes the far end depends on.
    Async.StartImmediate (run ())

    fun () ->
        cancelled <- true
        release ()

/// Subscribe, but stop for good when `retry` says a failed attempt — a status the server sent,
/// or a connect nothing answered — is permanent. The stopped subscription is inert rather than
/// errored: a server that does not offer a stream is not a fault, it is a server whose news has
/// to arrive another way.
let subscribeWhile (url: string) (headers: (string * string) list) (retry: Retry) (onFrame: Sink<string>) : Subscription =
    Subscription.ofStop (openStream url headers (fun event -> dataOf event |> Option.iter onFrame) retry)

/// Subscribe to an SSE stream, receiving one call per event carrying data (comment-only
/// keep-alives are dropped). `headers` ride every connect and reconnect.
let subscribe (url: string) (headers: (string * string) list) (onFrame: Sink<string>) : Subscription =
    subscribeWhile url headers Retry.always onFrame
