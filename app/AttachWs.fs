module Yession.Host.AttachWs

// Attaching a terminal to a byte stream somebody else is producing (Plan 16, part D).
//
// This file is the client half of a contract somebody outside this repository implements,
// and `docs/streams.md` is that contract written down — MUST/SHOULD/MAY, and which two
// rules are load-bearing. Change what happens here and change it there; a spec that drifts
// from its only implementation is worse than none, which is how `{"type":"failed"}` came to
// be sent, parsed, and specified nowhere.
//
// WebSocket, for three reasons and not because it is fashionable: the client costs nothing
// (Node 22+ ships a global `WebSocket`, and the repo pins 24), a provider is already running
// an HTTP server for its MCP endpoint so the upgrade rides the same port, and
// terminal-over-WebSocket is the shape every comparable system uses — ttyd, gotty, xterm.js
// attach, `kubectl exec`. That last one matters most: the point of this seam is that a third
// party can implement the other end.
//
// It breaks the repo's SSE-only streak, and that is fine. This is session ↔ local provider,
// like the control RPC — not the session transport `design.md` pins.
//
// The wire:
//   * BINARY frames are data, in both directions. No framing of ours.
//   * TEXT frames are control: {"type":"resize","cols":N,"rows":M}, {"type":"kill"}.
//   * Termination is an explicit {"type":"exited","code":N} and THEN a close. Not because a
//     close reason is too small — 123 bytes is ample — but because an abnormal closure
//     (1006) carries nothing at all, and that path exists whatever we do. The two cases then
//     land cleanly on the domain type that already draws the distinction: `SandboxExited`
//     for a source that ended, `SandboxRunFailed` for one that merely stopped.
//
// The socket is `Fable.NodeExtras`' binding; everything this file does with it is F#. It was
// a 65-line program inside one `[<Emit>]` string, which is the shape the binding effort
// exists to remove: a two-promise state machine, four listeners and the whole reading of the
// wire, none of it reachable from another file, none of it testable below a real socket, and
// none of it recompiling its callers when it changed.

open Fable.Core
open Fable.NodeExtras
open Node.Api
open Node.Buffer
open Yession.Domain
open Yession.Domain.Sandboxes
open Yession.Domain.Terminals

// --- Reading the control channel ------------------------------------------------------------

/// A TEXT frame's JSON, as the wire admits it rather than as this client hopes: `type` says
/// which control it is, and the other two are read only by the control that carries one.
///
/// `obj` for both of those, because JSON admits anything there and what JavaScript did with a
/// surprise is part of the behaviour rather than beside it — a `reason` that arrived as a
/// number has always reached a person as the text of that number, and a `code` has always been
/// read through ToInt32.
[<AllowNullLiteral>]
type private ControlFrame =
    abstract ``type`` : string
    abstract code : obj
    abstract reason : obj

/// `JSON.parse`. THROWS on anything that is not JSON — ordinary here rather than exceptional,
/// because a TEXT frame is whatever a provider chose to send.
[<Emit("JSON.parse($0)")>]
let private parseJson (text: string) : ControlFrame = jsNative

/// `String(x)` — JavaScript's own coercion to text, which is what a `reason` that arrived as
/// something other than a string has always been put in front of a person as.
[<Emit("String($0)")>]
let private asText (value: obj) : string = jsNative

/// JavaScript truthiness — the question `reason || "…"` asked. Kept as a question so the
/// ANSWER is in F#, where it can be read without reconstructing an operator's table.
[<Emit("!!$0")>]
let private isTruthy (value: obj) : bool = jsNative

/// `x | 0` — ToInt32, the coercion an `exited` frame's code has always been read through. An
/// absent or non-numeric code therefore reads as 0 rather than as a refusal, which is a fault
/// of this client's rather than of the wire's: a provider that sends a bare `{"type":"exited"}`
/// is reported as having exited SUCCESSFULLY.
[<Emit("$0 | 0")>]
let private toInt32 (value: obj) : int = jsNative

/// What a TEXT frame said.
///
/// Three cases and not two: a frame this client cannot act on is the same outcome whether it
/// was JSON from a later version of the protocol or not JSON at all (`docs/streams.md` MAY 9 —
/// an unrecognised control type is ignored, never fatal), and the warning in `connect` is about
/// both.
[<RequireQualifiedAccess>]
type Control =
    /// `{"type":"exited","code":N}` — the stream ended, and this is what the source exited
    /// with. Whether a person is told the number is `SourceCapabilities.HasExitCode`'s to say.
    | Exited of code: int
    /// `{"type":"failed","reason":"…"}` — it ended, and not because the device finished. The
    /// reason reaches the person verbatim, so a provider that named none still says something.
    | Failed of reason: string
    /// Anything else, which is ignored.
    | Unrecognised

/// Read a TEXT frame.
let control (text: string) : Control =
    let frame =
        try
            parseJson text
        with _ ->
            null

    if isNull frame then Control.Unrecognised
    elif frame.``type`` = "exited" then Control.Exited (toInt32 frame.code)
    elif frame.``type`` = "failed" then
        Control.Failed (if isTruthy frame.reason then asText frame.reason else "the source failed")
    else Control.Unrecognised

/// What a frame MEANS on this wire — a different question from what it CARRIED, which is
/// `Frame` and the one runtime test only JavaScript can make. Text is control, binary is the
/// device; the two channels are the whole wire, and code that read them as one would make it
/// mean two things.
[<RequireQualifiedAccess>]
type Heard =
    /// A BINARY frame: bytes from the device, decoded as UTF-8.
    | Output of string
    /// A TEXT frame: what the provider said ABOUT the stream, never what came out of it.
    | Said of Control

/// Read a frame.
let heard (frame: Frame) : Heard =
    match frame with
    | Frame.Binary bytes -> Heard.Output (buffer.Buffer.from(bytes).toString BufferEncoding.Utf8)
    | Frame.Text text -> Heard.Said (control text)

/// How the stream ENDED, given what the connection heard before the socket closed.
///
/// Decided in one place, at the close, because `docs/streams.md` MUST 3 puts it there: the
/// close is what ends the terminal and the frames only say why. A named failure wins over an
/// exit code, because a provider that sends both is saying the exit is not the story (MAY 8).
let ending (failure: string option) (exitCode: int option) : SandboxRun =
    match failure, exitCode with
    // PRESERVED, not endorsed: a failure whose reason came out EMPTY is reported as an exit
    // with -1 rather than as a failure. The JavaScript this replaced carried an ending as a
    // `{code, reason}` pair and read an empty reason as "nothing failed", and the case is
    // reachable — `String([])` is "". A fix belongs in a change that is about that.
    | Some "", _ -> SandboxExited -1
    | Some reason, _ -> SandboxRunFailed reason
    | None, Some code -> SandboxExited code
    | None, None -> SandboxRunFailed "the stream closed without saying why"

// --- The connection -------------------------------------------------------------------------

/// How long a provider has to answer `kill` with its own termination frame before the socket is
/// closed from this end.
[<Literal>]
let private closeDeadlineMs = 2000

/// Bytes to the device: a BINARY frame.
///
/// A send that throws is swallowed because a socket that has gone away is the ordinary end of a
/// stream rather than a fault — the same courtesy `docs/streams.md` SHOULD 5 asks of a
/// provider, owed in this direction too.
let private writeBytes (socket: WebSocket) (text: string) =
    try
        socket.sendBinary (buffer.Buffer.from (text, BufferEncoding.Utf8))
    with _ ->
        ()

/// A word on the control channel: a TEXT frame. Swallowed for the same reason.
let private sendControl (socket: WebSocket) (json: string) =
    try
        socket.sendText json
    with _ ->
        ()

[<Emit("JSON.stringify($0)")>]
let private toJson (value: obj) : string = jsNative

/// Close on a deadline — never in this tick.
///
/// `kill` ASKS the provider to end the stream; ending it is the provider's to do, and it ends
/// it by sending the termination frame and then closing. Closing from here in the same tick
/// would race that frame and lose the exit code — which is how this was first written, and what
/// the loopback test caught. The deadline is not a second mechanism doing the same job: the
/// frame is the request, this is what happens when a provider does not answer it.
let private closeOnDeadline (socket: WebSocket) =
    async {
        do! Async.Sleep closeDeadlineMs

        try
            socket.close ()
        with _ ->
            ()
    }
    |> Async.StartImmediate

/// Open a socket, and answer with what the connection became: the live socket and the promise
/// of how its stream ends, or why it never opened.
///
/// A PROMISE for the ending rather than a callback, because a promise settles once and
/// REMEMBERS: the stream ends whether or not anybody is waiting, and a caller that asks
/// afterwards still gets the answer. Both settle-once guards are load-bearing — a `close`
/// follows an `error`, and it follows a successful open too.
let private connect
    (url: string)
    (onData: string -> unit)
    : JS.Promise<Result<WebSocket * JS.Promise<SandboxRun>, string>> =
    JS.Constructors.Promise.Create (fun resolve _ ->
        let mutable finish : SandboxRun -> unit = ignore

        // The executor runs during construction, so `finish` is settled into before anything
        // can reach `endWith`.
        let exited : JS.Promise<SandboxRun> =
            JS.Constructors.Promise.Create (fun settle _ -> finish <- settle)

        let mutable ended = false

        let endWith (outcome: SandboxRun) =
            if not ended then
                ended <- true
                finish outcome

        // `new WebSocket` THROWS on a url it cannot parse, where a url it merely cannot reach
        // reports through `close` below. Two shapes of one answer, and this is the shape that
        // never reaches an event.
        let opening =
            try
                Ok (WebSockets.connect url)
            with error ->
                Error (if isTruthy error.Message then error.Message else asText error)

        match opening with
        | Error reason -> resolve (Error reason)
        | Ok socket ->

        // Before the first frame can land: the WHATWG default hands binary over as a `Blob`.
        socket.binaryType <- ArrayBuffer

        let mutable settled = false
        let mutable exitCode : int option = None
        let mutable failure : string option = None
        let mutable warnedText = false

        socket.onMessage (fun event ->
            match heard (payload event) with
            | Heard.Output text -> onData text
            // Text is CONTROL. A provider that reaches for its framework's `send_text` to emit
            // device output gets a terminal showing nothing, and every layer below here is
            // working correctly, so nothing else can say why. Said once per connection: the
            // fault repeats per frame and the diagnosis does not.
            | Heard.Said Control.Unrecognised ->
                if not warnedText then
                    warnedText <- true

                    JS.console.warn (
                        "attach "
                        + url
                        + ": ignoring a TEXT frame — text frames are control, device output goes in BINARY frames (docs/streams.md)"
                    )
            | Heard.Said (Control.Exited code) -> exitCode <- Some code
            | Heard.Said (Control.Failed reason) -> failure <- Some reason)

        socket.onOpen (fun () ->
            if not settled then
                settled <- true
                resolve (Ok (socket, exited)))

        // Swallowed: 'error' is always followed by 'close', and that is where both the failure
        // to open and the end of the stream are decided — one place, so the two cannot
        // disagree.
        socket.onError ignore

        socket.onClose (fun () ->
            if not settled then
                settled <- true
                resolve (Error ("could not attach to " + url))

            endWith (ending failure exitCode)))

/// The session's `AttachTerminal`: a ticket in, a handle shaped exactly like a pty's out.
///
/// `PtyHandle` and not a parallel type, because everything downstream — the lease, the input
/// route, the transcript — already speaks it. Where the source genuinely lacks something its
/// `SourceCapabilities` says so and the manager never calls the member; the member itself is
/// still there, and sending a resize to something that ignores it is nothing rather than an
/// error.
let attach : AttachTerminal =
    fun ticket cols rows onData ->
        async {
            let! opened = connect ticket.Url onData |> Interop.awaitPromise

            match opened with
            | Error reason -> return Error reason
            | Ok (socket, exited) ->
                // The opening size, sent once, for a source that has one. A source that does
                // not declare `CanResize` is never told a size at all — inventing 80x24 for a
                // serial line would be a fact nobody could check.
                if ticket.Capabilities.CanResize then
                    sendControl socket (toJson {| ``type`` = "resize"; cols = cols; rows = rows |})

                return
                    Ok
                        { Write = writeBytes socket
                          Resize =
                            fun c r -> sendControl socket (toJson {| ``type`` = "resize"; cols = c; rows = r |})
                          Kill =
                            fun () ->
                                sendControl socket (toJson {| ``type`` = "kill" |})
                                closeOnDeadline socket
                          // ^ asks, then holds the provider to a deadline. See the wire note
                          //   above: termination is the provider's frame, not our close.
                          Exited = exited |> Interop.awaitPromise }
        }
