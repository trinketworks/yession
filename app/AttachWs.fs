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
let private parseJson (text: string) : ControlFrame = unbox (JS.JSON.parse text)

/// `String(x)` — JavaScript's own coercion to text, which is what a `reason` that arrived as
/// something other than a string has always been put in front of a person as.
[<Emit("String($0)")>]
let private asText (value: obj) : string = jsNative

/// JavaScript truthiness — the question `reason || "…"` asked. Kept as a question so the
/// ANSWER is in F#, where it can be read without reconstructing an operator's table.
[<Emit("!!$0")>]
let private isTruthy (value: obj) : bool = jsNative

/// `x | 0` — ToInt32, the coercion an `exited` frame's code is read through once there is
/// something to read. Asked only of a value `isNumber` has already admitted, because ToInt32
/// answers 0 for everything it cannot make sense of, and 0 is the one answer a person acts on
/// differently.
[<Emit("$0 | 0")>]
let private toInt32 (value: obj) : int = jsNative

/// Is this a number at all — finite, and a number rather than the text of one? `Number.isFinite`
/// and not the global `isFinite`, which coerces first and so answers true for `"7"`, `[]` and
/// `null` alike. It is the question `toInt32` cannot ask for itself: a bare `{"type":"exited"}`
/// and `{"type":"exited","code":0}` are the same value to it, and only one of them is a clean
/// exit.
[<Emit("Number.isFinite($0)")>]
let private isNumber (value: obj) : bool = jsNative

/// The code an `exited` frame that named none is reported with: `SandboxRun`'s own answer for
/// a process whose exit code nobody could read. NOT 0, which says the source ended well.
[<Literal>]
let private noExitCode = -1

/// What a `failed` frame that named no reason a person could read says instead. A `failed`
/// frame is a failure whatever its reason renders as, so there is always something to say.
[<Literal>]
let private unstatedFailure = "the source failed"

/// What a TEXT frame said.
///
/// Four cases and not three, because the two this client cannot act on are two different facts
/// about the provider and only one of them is anybody's fault. Both are ignored; they differ
/// in what `connect` SAYS about them.
[<RequireQualifiedAccess>]
type Control =
    /// `{"type":"exited","code":N}` — the stream ended, and this is what the source exited
    /// with. Whether a person is told the number is `SourceCapabilities.HasExitCode`'s to say.
    | Exited of code: int
    /// `{"type":"failed","reason":"…"}` — it ended, and not because the device finished. The
    /// reason reaches the person verbatim, so a provider that named none still says something.
    | Failed of reason: string
    /// A control frame whose `type` this build has no meaning for. `docs/streams.md` MAY 9
    /// says new control types will appear and an implementation must ignore the ones it does
    /// not know — so this is a provider written against a later spec doing exactly the right
    /// thing, and it is ignored SILENTLY.
    | Unknown
    /// A TEXT frame that is not a control frame at all: it would not parse, or it parsed into
    /// something carrying no `type`. That is what reaching for a framework's `send_text` to
    /// emit device output looks like from here, and it is the one `connect` warns about.
    | NotControl

/// Read a TEXT frame.
let control (text: string) : Control =
    let frame =
        try
            parseJson text
        with _ ->
            null

    // JSON admits `null`, a number and a bare string, none of which carry a `type` — and text
    // that would not parse at all arrived here as `null` too. A control frame is one that says
    // which control it is.
    if isNull frame || not (isTruthy (box frame.``type``)) then Control.NotControl
    elif frame.``type`` = "exited" then
        Control.Exited (if isNumber frame.code then toInt32 frame.code else noExitCode)
    elif frame.``type`` = "failed" then
        // The frame's TYPE is what says it failed; the reason is only the words. `String([])`
        // is "", so a reason can coerce to nothing at all and there is still a failure to
        // report.
        let reason = if isTruthy frame.reason then asText frame.reason else ""
        Control.Failed (if reason = "" then unstatedFailure else reason)
    else Control.Unknown

/// What a frame MEANS on this wire — a different question from what it CARRIED, which is
/// `Frame` and the one runtime test only JavaScript can make. Text is control, binary is the
/// device; the two channels are the whole wire, and code that read them as one would make it
/// mean two things.
[<RequireQualifiedAccess>]
type Heard =
    /// A BINARY frame: bytes from the device, as text.
    | Output of string
    /// A TEXT frame: what the provider said ABOUT the stream, never what came out of it.
    | Said of Control

/// Read a frame, through the CONNECTION's decoder.
///
/// The decoder is threaded rather than made here because UTF-8 does not respect frame
/// boundaries: a device that emits a multi-byte character across two binary frames — which a
/// real one does, at whatever buffer size it has — had each half decoded on its own and each
/// half became U+FFFD. The decoder is the thing that holds the tail of a split character
/// between calls, so it belongs to the connection and one per frame is no decoder at all.
let heard (decoder: TextDecoder) (frame: Frame) : Heard =
    match frame with
    | Frame.Binary bytes -> Heard.Output (decodeChunk decoder (JS.Constructors.Uint8Array.Create bytes))
    | Frame.Text text -> Heard.Said (control text)

/// How the stream ENDED, given what the connection heard before the socket closed.
///
/// Decided in one place, at the close, because `docs/streams.md` MUST 3 puts it there: the
/// close is what ends the terminal and the frames only say why. A named failure wins over an
/// exit code, because a provider that sends both is saying the exit is not the story (MAY 8).
/// A `failed` frame is a failure whatever its reason renders as: what failed is the frame's
/// TYPE, and the reason is only the words. An empty one reaching here means a frame decoder let
/// one through rather than that nothing failed — `control` already fills it, and this says the
/// same thing where the outcome is decided, because the two go red at different times.
let ending (failure: string option) (exitCode: int option) : SandboxRun =
    match failure, exitCode with
    | Some "", _ -> SandboxRunFailed unstatedFailure
    | Some reason, _ -> SandboxRunFailed reason
    | None, Some code -> SandboxExited code
    | None, None -> SandboxRunFailed "the stream closed without saying why"

/// How a connection is NAMED in anything a person or a log reads.
///
/// Never by its url, which is a CREDENTIAL: `docs/streams.md` tells a provider whose resource
/// is exclusive to mint a single-use token and spend it on attach, so the url is the one thing
/// about a ticket that must not be reproduced anywhere it outlives the connection. What is left
/// is what whoever reads the line already knows — the label the provider put on the stream, and
/// the authority it is served from. Path and query go because that is where a token rides;
/// userinfo goes because it is a credential of its own, and the admission rule refuses one
/// anyway.
let describing (ticket: AttachTicket) : string =
    let authority =
        match ticket.Url.IndexOf "://" with
        | -1 -> None
        | scheme ->
            let rest = ticket.Url.Substring (scheme + 3)
            let authority =
                match rest.IndexOfAny [| '/'; '?'; '#' |] with
                | -1 -> rest
                | cut -> rest.Substring (0, cut)
            // The port stays: it is how two providers on one box are told apart, and it is no
            // more secret than the host.
            let withoutUserInfo =
                match authority.LastIndexOf '@' with
                | -1 -> authority
                | at -> authority.Substring (at + 1)

            if withoutUserInfo = "" then None else Some withoutUserInfo

    match ticket.Label, authority with
    | Some label, Some host -> sprintf "%s (%s)" label host
    | Some label, None -> label
    | None, Some host -> host
    // A url with no authority to read is one nothing could have connected to, and there is
    // still a sentence to write about it that is not the url.
    | None, None -> "a stream whose url names no host"

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

let private toJson (value: obj) : string = JS.JSON.stringify value

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
    (ticket: AttachTicket)
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
                Ok (WebSockets.connect ticket.Url)
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

        // One decoder for the whole connection: it is what holds the tail of a character a
        // frame boundary cut in half until the frame carrying the rest of it arrives.
        let decoder = createDecoder ()

        socket.onMessage (fun event ->
            match heard decoder (payload event) with
            | Heard.Output text -> onData text
            // Text is CONTROL. A provider that reaches for its framework's `send_text` to emit
            // device output gets a terminal showing nothing, and every layer below here is
            // working correctly, so nothing else can say why. Said once per connection: the
            // fault repeats per frame and the diagnosis does not. Named by `describing` and
            // never by the url, which carries the attach token.
            | Heard.Said Control.NotControl ->
                if not warnedText then
                    warnedText <- true

                    JS.console.warn (
                        "attach "
                        + describing ticket
                        + ": ignoring a TEXT frame — text frames are control, device output goes in BINARY frames (docs/streams.md)"
                    )
            // A control frame of a type this build does not know is a provider written against
            // a later spec, doing what `docs/streams.md` MAY 9 asks of THIS end: ignored, and
            // ignored without comment, because there is nothing for anybody to fix.
            | Heard.Said Control.Unknown -> ()
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
                resolve (Error ("could not attach to " + describing ticket))

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
            let! opened = connect ticket onData |> Interop.awaitPromise

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
