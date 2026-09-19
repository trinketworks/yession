module Yession.Tests.Attach

// Attaching a terminal to somebody else's byte stream (Plan 16, part D), over a real
// WebSocket against a loopback provider.
//
// The provider below is also the REFERENCE implementation `docs/streams.md` points an
// outside implementer at: the smallest thing that conforms, running on every `check Ports`.
// That is the only reason it cannot rot — a sample nobody executes drifts from the client
// it claims to match, which is what the spec exists to stop.
//
// The provider here is written by hand — an HTTP upgrade, the RFC 6455 accept hash, and
// enough frame handling to echo — rather than built on a library, and that is the point
// rather than an inconvenience: the whole reason for choosing WebSocket over something of
// our own is that a THIRD PARTY can implement the other end. A test that talked to our own
// client through our own server abstraction would prove only that the two agreed.
//
// `Ports`, because the upgrade is the thing being tested and there is no meaningful
// in-memory stand-in for it.
//
// Reading the wire is a separate question from carrying it, and the cheap tier answers that
// one at BOTH ends. What a frame MEANS, what a control frame said, and what an ending was are
// pure functions of `app/AttachWs.fs`; what a frame IS — which opcode, masked or not, how long
// the payload is and where it begins — is a pure function below, because RFC 6455's frame
// header is arithmetic over bytes with no socket in it. They are the decisions a provider gets
// wrong, so they are the ones worth asking about on every PR rather than only where a port can
// be bound.

open Fable.Core
open Fable.NodeExtras
open Fable.Pyxpecto
open Yession.Domain
open Yession.Domain.Sandboxes
open Yession.Domain.Terminals
open Yession.Host

let private expect =
    function
    | Ok v -> v
    | Error e -> failwithf "%A" e

// --- The frames ------------------------------------------------------------------------------

/// RFC 6455 §4.2.2: the GUID a server appends to the client's `Sec-WebSocket-Key` before
/// hashing it. It is what makes an accept value unforgeable by anything that never saw the key
/// — a cache replaying a 101 cannot produce one.
[<Literal>]
let private handshakeGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"

// The three opcodes this wire uses, of RFC 6455 §5.2's sixteen. Text is CONTROL here, binary is
// the device's bytes, and close ends the stream. Named in PascalCase because they are matched
// as literals, and a lowercase name in a pattern position is one nobody can tell from a binding.

[<Literal>]
let private TextOpcode = 0x1

[<Literal>]
let private BinaryOpcode = 0x2

[<Literal>]
let private CloseOpcode = 0x8

/// What the first bytes of a frame say about the rest of it.
///
/// This is where a hand-rolled peer goes wrong: the second byte carries a flag and a length in
/// one, the length has more than one form, and the masking key sits between the header and the
/// payload it hides. So it is answered here, as arithmetic over bytes with no socket in it,
/// where the cheap tier can ask.
type private FrameHeader =
    { /// The low four bits of the first byte.
      Opcode : int
      /// Where the four-byte masking key sits, `None` when the frame carries none. RFC 6455
      /// §5.3 makes masking the CLIENT's job and forbids it of a server, so every frame
      /// arriving here has one and every frame leaving does not.
      MaskAt : int option
      /// How many bytes of payload follow.
      Length : int
      /// Where the payload begins: past the two header bytes, past the extended length when the
      /// second byte promised one, and past the masking key when there is one.
      PayloadAt : int }

[<RequireQualifiedAccess>]
module private FrameHeader =

    /// `None` while the header itself has not all arrived — fewer than two bytes, or fewer than
    /// four when the second byte said the length is extended. A socket hands over whatever the
    /// kernel had, so that is a wait for more rather than a fault.
    ///
    /// Two length forms and not three: a second byte of 126 means the two bytes after it are
    /// the length. The 64-bit form (127) needs a payload of 64KiB, and the client at the other
    /// end of THIS wire sends keystrokes and window sizes — so its byte is read as the number
    /// it literally is rather than half-implemented. An outside provider serving a client that
    /// can send one needs the third form; this peer's own client cannot.
    let read (bytes: byte []) : FrameHeader option =
        if bytes.Length < 2 then
            None
        else
            let masked = (bytes.[1] &&& 0x80uy) <> 0uy
            let stated = int (bytes.[1] &&& 0x7fuy)

            // The length, and the offset just past it. `int` before the shift: a byte shifted
            // eight places is a byte's worth of zero.
            let measured =
                if stated <> 126 then Some (stated, 2)
                elif bytes.Length < 4 then None
                else Some ((int bytes.[2] <<< 8) ||| int bytes.[3], 4)

            measured
            |> Option.map (fun (length, afterLength) ->
                { Opcode = int (bytes.[0] &&& 0x0fuy)
                  MaskAt = if masked then Some afterLength else None
                  Length = length
                  PayloadAt = if masked then afterLength + 4 else afterLength })

/// What is at the front of the bytes that have arrived so far. A socket hands over READS, not
/// frames, so one of them can carry two frames and one frame can span two of them.
[<RequireQualifiedAccess>]
type private Arrival =
    /// A whole frame, unmasked, and whatever bytes came after it.
    | Whole of opcode: int * payload: byte [] * rest: byte []
    /// Nothing whole yet: the header, or the payload it promised, is still on its way.
    | Waiting

[<RequireQualifiedAccess>]
module private Arrival =

    /// Take one frame off the front, unmasking it on the way. RFC 6455 §5.3 XORs each payload
    /// byte with the key byte at its index modulo four, and the key travels in the frame — so
    /// this is obfuscation against a confused proxy, never secrecy.
    ///
    /// The unmasked payload is a NEW array rather than a write through the one that arrived:
    /// what arrived is the accumulated buffer everything after this frame is still read out of.
    let read (bytes: byte []) : Arrival =
        match FrameHeader.read bytes with
        | Some header when bytes.Length >= header.PayloadAt + header.Length ->
            let payload =
                match header.MaskAt with
                | Some maskAt ->
                    Array.init header.Length (fun i -> bytes.[header.PayloadAt + i] ^^^ bytes.[maskAt + (i % 4)])
                | None -> Array.sub bytes header.PayloadAt header.Length

            let after = header.PayloadAt + header.Length
            Arrival.Whole (header.Opcode, payload, Array.sub bytes after (bytes.Length - after))
        | Some _
        | None -> Arrival.Waiting

/// One frame out: FIN set, unmasked — RFC 6455 §5.1 forbids a server to mask — and its length
/// in whichever of the two forms fits.
let private frame (opcode: int) (payload: byte []) : byte [] =
    let head =
        if payload.Length < 126 then
            [| 0x80uy ||| byte opcode; byte payload.Length |]
        else
            [| 0x80uy ||| byte opcode; 126uy; byte (payload.Length >>> 8); byte payload.Length |]

    Array.append head payload

// --- The socket ------------------------------------------------------------------------------

/// The socket under an accepted upgrade. From the 101 onwards nothing on it is HTTP and every
/// byte is this file's to frame.
///
/// A member per event rather than one `on` taking a name, for `Fable.NodeExtras`' reason: the
/// event's name and its handler's type are one fact, and a member per event is how the type
/// gets to say so.
[<AllowNullLiteral>]
type private UpgradedSocket =
    /// Bytes onto the wire. Node takes a `Uint8Array`, which is what Fable compiles a `byte []`
    /// to, so nothing is converted on the way out.
    abstract write : bytes: byte [] -> bool

    /// The polite end: what has already been written still goes out.
    abstract ``end`` : unit -> unit

    /// The rude one: the connection disappears with nothing said. What `/abrupt` is for.
    abstract destroy : unit -> unit

    /// Node hands each read over as a `Buffer` — a `Uint8Array` subclass, so it arrives as
    /// bytes with nothing converted here either.
    [<Emit("$0.on('data', $1)")>]
    abstract onData : handler: (byte [] -> unit) -> unit

    /// A client that disappeared mid-write raises here, and an unhandled `error` on a stream
    /// takes the process down. Every route here ends by dropping a connection, so this is the
    /// ordinary case rather than an edge of it.
    [<Emit("$0.on('error', $1)")>]
    abstract onError : handler: (obj -> unit) -> unit

[<Emit("$0.on('upgrade', $1)")>]
let private onUpgradeRaw
    (server: Interop.HttpServer)
    (handler: System.Func<Interop.IncomingMessage, UpgradedSocket, unit>)
    : unit =
    jsNative

/// `server.on('upgrade', …)`: the request that asked for it, and the socket under it. The
/// handler is an uncurried delegate so Node receives the two-argument callback it calls — the
/// way `Interop.createServer` takes its own.
let private onUpgrade (server: Interop.HttpServer) (handler: Interop.IncomingMessage -> UpgradedSocket -> unit) : unit =
    onUpgradeRaw server (System.Func<_, _, _> handler)

/// Send one frame.
let private send (socket: UpgradedSocket) (opcode: int) (payload: byte []) : unit =
    socket.write (frame opcode payload) |> ignore

/// A TEXT frame, which on this wire is CONTROL.
let private sendControl (socket: UpgradedSocket) (json: string) : unit =
    send socket TextOpcode (System.Text.Encoding.UTF8.GetBytes json)

/// A BINARY frame, which on this wire is what the device said.
let private sendOutput (socket: UpgradedSocket) (text: string) : unit =
    send socket BinaryOpcode (System.Text.Encoding.UTF8.GetBytes text)

// --- What the client said ---------------------------------------------------------------------

/// A TEXT frame's JSON as the wire admits it: `type` says which control it is, and the other two
/// are read only by the control that carries them.
[<AllowNullLiteral>]
type private ClientControl =
    abstract ``type`` : string
    abstract cols : int
    abstract rows : int

/// `JSON.parse`. THROWS on anything that is not JSON — ordinary here rather than exceptional,
/// because a TEXT frame is whatever the other end chose to send.
let private parseJson (text: string) : ClientControl = unbox (JS.JSON.parse text)

/// What the client's TEXT frame said, or `None` when it said nothing this peer can read: it
/// would not parse, or it parsed into something that does not say which control it is.
let private controlOf (text: string) : ClientControl option =
    try
        match parseJson text with
        | null -> None
        | parsed -> if isNull (box parsed.``type``) then None else Some parsed
    with _ ->
        None

/// What this peer does with one frame the client sent, and whether the stream goes on.
let private answer (socket: UpgradedSocket) (opcode: int) (payload: byte []) : bool =
    match opcode with
    | CloseOpcode ->
        socket.``end`` ()
        false
    | BinaryOpcode ->
        // Binary is the device, both directions: what the client typed comes back marked, so a
        // test can tell a round trip from an echo of its own making.
        sendOutput socket ("echo:" + System.Text.Encoding.UTF8.GetString payload)
        true
    | TextOpcode ->
        match controlOf (System.Text.Encoding.UTF8.GetString payload) with
        | Some control when control.``type`` = "resize" ->
            // Answered on the DATA channel: a client that sees it knows the two channels are
            // distinct rather than one stream it happens to parse twice.
            sendOutput socket (sprintf "sized %dx%d\n" control.cols control.rows)
            true
        | Some control when control.``type`` = "kill" ->
            // `kill` ends the STREAM, and says why before it does — the termination frame, then
            // the close. A provider that sent one without the other would leave a terminal
            // nobody can reattach, because nothing has ended yet.
            sendControl socket """{"type":"exited","code":7}"""
            socket.``end`` ()
            false
        | Some _
        | None ->
            // A control this peer has no meaning for, or text that is not control at all.
            // Ignored, which is what an implementation does with a frame from a later spec.
            true
    | _ -> true

/// Read frames out of the client's bytes for as long as it sends them. One frame at a time,
/// unfragmented, masked (every client frame is): enough of RFC 6455 to be a peer, and
/// deliberately not a library.
let private readFrames (socket: UpgradedSocket) : unit =
    let mutable pending : byte [] = Array.empty

    socket.onData (fun chunk ->
        pending <- Array.append pending chunk
        let mutable reading = true

        while reading do
            match Arrival.read pending with
            | Arrival.Waiting -> reading <- false
            | Arrival.Whole (opcode, payload, rest) ->
                pending <- rest
                reading <- answer socket opcode payload)

// --- The routes --------------------------------------------------------------------------------

/// The route is the PATH of the url and never the whole of it: the token an exclusive provider
/// spends on attach rides the query string (`docs/streams.md`), so a peer that matched on the
/// whole url would stop recognising its own routes the moment one was spent.
let private routeOf (url: string) : string = (url.Split '?').[0]

/// What a route says before the client has said anything.
let private greet (socket: UpgradedSocket) (route: string) : unit =
    match route with
    | "/talkative" ->
        // Two things this client has no meaning for, on the channel that is CONTROL: a control
        // type from a later version, and something that is not JSON at all — which is what
        // reaching for a framework's `send_text` to emit device output looks like from here.
        sendControl socket """{"type":"from-a-later-version"}"""
        sendControl socket "device output on the wrong channel"
    | "/later-version" ->
        // Only the first of those: a conforming provider written against a later spec.
        sendControl socket """{"type":"from-a-later-version"}"""
    | _ -> ()

/// The 101 that ends HTTP on this socket. `Sec-WebSocket-Accept` is the proof the server saw the
/// client's key: SHA-1 over the key and the GUID, base64.
let private handshake (key: string) : string =
    let hash = Node.Api.crypto.createHash "sha1"
    hash.update (key + handshakeGuid) |> ignore
    let accept = hash.digest().toString Node.Buffer.BufferEncoding.Base64

    "HTTP/1.1 101 Switching Protocols\r\n"
    + "Upgrade: websocket\r\nConnection: Upgrade\r\n"
    + "Sec-WebSocket-Accept: "
    + accept
    + "\r\n\r\n"

/// A loopback provider. `/echo` sends every data frame straight back, answers a `resize`
/// control frame by saying what size it was told, and on `kill` sends the termination frame
/// and then closes. `/abrupt` accepts the upgrade and drops the connection without saying
/// anything — which is the case an in-band termination frame exists FOR, since an abnormal
/// closure carries nothing.
/// Public rather than private to this module: `NodeExtras.fs` drives the WebSocket BINDING
/// against the same provider. A second hand-rolled RFC 6455 server in this suite would be a
/// second thing to keep in step with the spec the first one is the reference for.
type Provider =
    abstract port : int
    abstract stop : unit -> JS.Promise<unit>

/// Start it on a loopback port the OS picks. The promise settles once the server is listening,
/// so a caller holding one has somewhere to dial.
let startProvider () : JS.Promise<Provider> =
    Async.FromContinuations (fun (listening, _, _) ->
        // Anything that is not an upgrade is told so. 426 is the status for "this endpoint is
        // WebSocket", and it is what a plain GET at a provider should read.
        let server =
            Interop.createServer (fun _ res ->
                res.writeHead (426, JsInterop.createObj []) |> ignore
                res.``end`` "")

        onUpgrade server (fun req socket ->
            // Before anything can be written: a client that drops mid-write raises here, and an
            // `error` nobody is listening for ends the process rather than the connection.
            socket.onError ignore

            match Interop.headerOf req "sec-websocket-key" with
            | None ->
                // Not a WebSocket handshake at all. There is no accept value to compute and
                // nothing to say on a socket that is already past HTTP, so it goes.
                socket.destroy ()
            | Some key ->
                socket.write (System.Text.Encoding.UTF8.GetBytes (handshake key)) |> ignore

                match routeOf req.url with
                | "/abrupt" ->
                    // Upgraded, then dropped with nothing said. Nothing is read either: there is
                    // no stream left to read by the time anybody could have written to it.
                    JS.setTimeout (fun () -> socket.destroy ()) 30 |> ignore
                | route ->
                    greet socket route
                    readFrames socket)

        server.listen (
            0,
            "127.0.0.1",
            fun () ->
                listening
                    { new Provider with
                        member _.port = Interop.serverPort server

                        member _.stop () =
                            // `close` waits for the connections that are still open, so a test
                            // that walks away from one hangs here rather than failing.
                            Async.FromContinuations (fun (closed, _, _) -> server.close (fun _ -> closed ()))
                            |> Async.StartAsPromise })
        |> ignore)
    |> Async.StartAsPromise

let private ticket (port: int) (path: string) (capabilities: SourceCapabilities) : AttachTicket =
    { Url = sprintf "ws://127.0.0.1:%d%s" port path
      Capabilities = capabilities
      Label = Some "loopback" }

/// Poll for a condition rather than sleeping a fixed amount: what is being waited on is a
/// round trip over loopback, and a fixed sleep is either flaky or slow.
let private until (predicate: unit -> bool) : Async<bool> =
    let rec loop (remaining: int) =
        async {
            if predicate () then return true
            elif remaining <= 0 then return false
            else
                do! Async.Sleep 20
                return! loop (remaining - 20)
        }
    loop 3000

/// The platform's `console.warn` as it stands. `console` is one mutable object for the whole
/// process, so hearing what was warned means putting something else in `warn`'s place — and
/// reading the real one first is the only way to put it back.
[<Emit("console.warn")>]
let private consoleWarn () : obj = jsNative

[<Emit("console.warn = $0")>]
let private setConsoleWarn (warn: obj) : unit = jsNative

/// A function called the way `console` calls one: with however many arguments the caller
/// passed, where an F# function takes exactly one. The parts arrive as an array, so what to
/// make of them is F#'s decision rather than this line's.
[<Emit("(...parts) => $0(parts)")>]
let private variadic (handler: obj [] -> unit) : obj = jsNative

/// Run `body` with a recorder in `console.warn`'s place, and put the real one back however the
/// body ends. `said` answers with what has been warned so far, one entry per call, spelled the
/// way `console` would have printed it — arguments joined by spaces.
///
/// The body runs INSIDE rather than there being a take and a matching give-back, for
/// `Support.withEnv`'s reason: the process has one `console`, so a capture that is not given
/// back swallows every later suite's warnings, and the half a caller forgets is the give-back.
///
/// A diagnostic is the only place this client's warning about a TEXT frame goes, so reading it
/// back is the only way to ask what it said — and what it must never contain is the ticket's
/// url, which carries a single-use attach token.
let private withCapturedWarnings (body: (unit -> string list) -> Async<'a>) : Async<'a> =
    async {
        let said = ResizeArray<string> ()
        let original = consoleWarn ()
        setConsoleWarn (variadic (fun parts -> said.Add (parts |> Array.map Thrown.describe |> String.concat " ")))

        try
            return! body (fun () -> List.ofSeq said)
        finally
            setConsoleWarn original
    }

let private device = { SourceCapabilities.byteStream with CanResize = true }

let portsTests =
    testList "Foreign terminal attach (Plan 16)" [

        testCaseAsync "bytes go both ways, and a control frame is control rather than data" <|
            async {
                let! provider = startProvider () |> Interop.awaitPromise
                let received = System.Text.StringBuilder ()
                let! attached =
                    Yession.Host.AttachWs.attach (ticket provider.port "/echo" device) 80 24 (fun text ->
                        received.Append text |> ignore)
                let handle = attached |> expect
                // The opening size is sent as a TEXT frame; the provider answers on the data
                // channel, so seeing it proves the two channels are distinct rather than one
                // stream we happen to parse twice.
                let! sized = until (fun () -> received.ToString().Contains "sized 80x24")
                Expect.isTrue sized "the resize control frame arrived as control"
                handle.Write "hello"
                let! echoed = until (fun () -> received.ToString().Contains "echo:hello")
                Expect.isTrue echoed "a binary frame went out and came back"
                handle.Resize 132 43
                let! resized = until (fun () -> received.ToString().Contains "sized 132x43")
                Expect.isTrue resized "and a later resize is control too"
                handle.Kill ()
                let! ending = handle.Exited
                Expect.equal ending (SandboxExited 7) "the in-band exited frame carries the code"
                do! provider.stop () |> Interop.awaitPromise
            }

        // Control types will be added, and a client that treated an unknown one as fatal
        // would break every provider written against the newer spec. Ignoring it is the rule
        // `docs/streams.md` states, and this is the rule rather than the wording.
        testCaseAsync "a text frame we have no meaning for does not end the stream" <|
            async {
                let! provider = startProvider () |> Interop.awaitPromise
                let received = System.Text.StringBuilder ()
                let! attached =
                    Yession.Host.AttachWs.attach (ticket provider.port "/talkative" device) 80 24 (fun text ->
                        received.Append text |> ignore)
                let handle = attached |> expect
                handle.Write "still here"
                let! echoed = until (fun () -> received.ToString().Contains "echo:still here")
                Expect.isTrue echoed "the stream carries data after text it could not read"
                // Close the socket before the server: `server.close` waits for its
                // connections, so a test that walks away from an open one hangs the whole
                // suite rather than failing.
                handle.Kill ()
                let! _ = handle.Exited
                do! provider.stop () |> Interop.awaitPromise
            }

        // Text is CONTROL, and a provider that sends device output on it gets a terminal
        // showing nothing while every layer below is working correctly. So the bytes must
        // NOT arrive as output — the fault is the provider's, and a client that quietly
        // accepted them would make the wire mean two things.
        testCaseAsync "device output sent as text is not mistaken for output" <|
            async {
                let! provider = startProvider () |> Interop.awaitPromise
                let received = System.Text.StringBuilder ()
                let! attached =
                    Yession.Host.AttachWs.attach (ticket provider.port "/talkative" device) 80 24 (fun text ->
                        received.Append text |> ignore)
                let handle = attached |> expect
                handle.Write "marker"
                let! echoed = until (fun () -> received.ToString().Contains "echo:marker")
                Expect.isTrue echoed "the round trip completed, so anything text-borne had arrived by now"
                Expect.isFalse
                    (received.ToString().Contains "device output on the wrong channel")
                    "a text frame is never data"
                handle.Kill ()
                let! _ = handle.Exited
                do! provider.stop () |> Interop.awaitPromise
            }

        // The reason termination is a frame and not a close code: an abnormal closure (1006)
        // carries nothing at all, and that path exists whatever the provider intends.
        testCaseAsync "a stream that just stops is a failure, not an exit" <|
            async {
                let! provider = startProvider () |> Interop.awaitPromise
                let! attached =
                    Yession.Host.AttachWs.attach (ticket provider.port "/abrupt" device) 80 24 ignore
                match attached with
                | Error _ ->
                    // Racing the drop: the connection may never have opened, which is the
                    // same fact reported one step earlier.
                    do! provider.stop () |> Interop.awaitPromise
                | Ok handle ->
                    let! ending = handle.Exited
                    match ending with
                    | SandboxRunFailed reason -> Expect.isFalse (reason = "") "it says why, however little it knows"
                    | SandboxExited code -> failwithf "a dropped connection is not an exit (got %d)" code
                    do! provider.stop () |> Interop.awaitPromise
            }

        // The url IS the credential: `docs/streams.md` tells an exclusive provider to mint a
        // single-use token and spend it on attach. A diagnostic that reproduces the url puts
        // that token in a log, where whatever reads logs can spend it — so the warning names
        // the connection by what an operator already knows instead.
        testCaseAsync "the text-frame warning does not put the attach token in the log" <|
            async {
                let! provider = startProvider () |> Interop.awaitPromise

                do!
                    withCapturedWarnings (fun said ->
                        async {
                            let! attached =
                                Yession.Host.AttachWs.attach
                                    (ticket provider.port "/talkative?token=s3cr3t-single-use" device)
                                    80
                                    24
                                    ignore
                            let handle = attached |> expect
                            let! warned = until (fun () -> not (List.isEmpty (said ())))
                            Expect.isTrue warned "the provider's text frame drew the diagnostic"
                            Expect.isFalse
                                (said () |> List.exists (fun line -> line.Contains "s3cr3t-single-use"))
                                "the attach token is not recoverable from the log"
                            handle.Kill ()
                            let! _ = handle.Exited
                            do! provider.stop () |> Interop.awaitPromise
                        })
            }

        // MAY 9 from the client's end: a text frame that PARSES as a control frame IS one, of a
        // version this build does not know, and is ignored silently. Warning about it tells a
        // conforming provider its control frame should have been device output — a diagnosis
        // that sends them looking at the one thing they got right.
        testCaseAsync "a control type from a later version is ignored silently" <|
            async {
                let! provider = startProvider () |> Interop.awaitPromise
                let received = System.Text.StringBuilder ()

                do!
                    withCapturedWarnings (fun said ->
                        async {
                            let! attached =
                                Yession.Host.AttachWs.attach (ticket provider.port "/later-version" device) 80 24 (fun text ->
                                    received.Append text |> ignore)
                            let handle = attached |> expect
                            handle.Write "marker"
                            let! echoed = until (fun () -> received.ToString().Contains "echo:marker")
                            Expect.isTrue echoed "the round trip completed, so the text frame had arrived by now"
                            Expect.equal (List.length (said ())) 0 "a control frame of a type we do not know says nothing"
                            handle.Kill ()
                            let! _ = handle.Exited
                            do! provider.stop () |> Interop.awaitPromise
                        })
            }

        testCaseAsync "a provider that is not there is an error, not a hang" <|
            async {
                // Port 1 on loopback: reserved, never listening, and refused immediately.
                let! attached =
                    Yession.Host.AttachWs.attach
                        { Url = "ws://127.0.0.1:1/echo"; Capabilities = device; Label = Some "nothing" }
                        80 24 ignore
                Expect.isError attached "a refused connection is reported, not awaited"
            }
    ]

// --- Reading the wire -------------------------------------------------------------------
//
// No socket, so no `Ports`: these are the decisions each end makes about what arrived — the
// client's about what a frame MEANT, the provider's about what a frame IS — and the cheapest
// tier that can host them is the one every PR runs.

module Ws = Yession.Host.AttachWs

/// UTF-8 bytes as a BINARY frame carries them: a standalone `ArrayBuffer`, which is what
/// `binaryType <- ArrayBuffer` buys and what the socket hands over.
[<Emit("new TextEncoder().encode($0).buffer")>]
let private utf8Bytes (text: string) : JS.ArrayBuffer = jsNative

/// One raw byte as a BINARY frame carries it. Two of these are how a multi-byte character
/// arrives when a chunk boundary cuts it in half, which is what real device output does.
[<Emit("new Uint8Array([$0]).buffer")>]
let private oneByte (value: int) : JS.ArrayBuffer = jsNative

/// One frame the way a CLIENT sends one, for the provider to read: FIN and opcode 2, the mask
/// bit and a length of five, a four-byte masking key, and "hello" XOR'd under it. Written out
/// byte by byte rather than built by a helper, because a helper that framed it would be the
/// thing under test spelled a second way.
let private helloFrame =
    [| 0x82uy; 0x85uy; 0x37uy; 0xfauy; 0x21uy; 0x3duy; 0x5fuy; 0x9fuy; 0x4duy; 0x51uy; 0x58uy |]

let tests =
    testList "Foreign terminal attach, reading the wire (Plan 16)" [

        // What a frame IS, from the provider's end: the arithmetic every hand-rolled peer has to
        // get right, and the reason `docs/streams.md` can point an outside implementer at one.
        testList "the provider's frame header" [

            testCase "a short frame's length is the low seven bits of its second byte" <| fun () ->
                Expect.equal
                    (FrameHeader.read helloFrame)
                    (Some { Opcode = 0x2; MaskAt = Some 2; Length = 5; PayloadAt = 6 })
                    "two header bytes, a four-byte key, then the five bytes it promised"

            testCase "a second byte of 126 means the length is the two bytes after it" <| fun () ->
                Expect.equal
                    (FrameHeader.read [| 0x82uy; 0xfeuy; 0x01uy; 0x2cuy; 0x00uy; 0x00uy; 0x00uy; 0x00uy |])
                    (Some { Opcode = 0x2; MaskAt = Some 4; Length = 300; PayloadAt = 8 })
                    "the extended form moves the key and the payload two bytes along"

            testCase "an unmasked frame's payload begins two bytes in" <| fun () ->
                Expect.equal
                    (FrameHeader.read [| 0x82uy; 0x03uy; 0x61uy; 0x62uy; 0x63uy |])
                    (Some { Opcode = 0x2; MaskAt = None; Length = 3; PayloadAt = 2 })
                    "there is no masking key to step over — §5.1 forbids a server one"

            testCase "one byte is not yet a header" <| fun () ->
                Expect.equal (FrameHeader.read [| 0x82uy |]) None "the flags and the length are two bytes, not one"

            testCase "an extended length whose bytes have not arrived is not yet a header" <| fun () ->
                Expect.equal
                    (FrameHeader.read [| 0x82uy; 0xfeuy; 0x01uy |])
                    None
                    "the second byte promised two more and only one of them is here"
        ]

        testList "the provider's frames, as they arrive" [

            testCase "a masked payload is XOR'd with the key byte at its index, wrapping at four" <| fun () ->
                match Arrival.read helloFrame with
                | Arrival.Whole (_, payload, _) ->
                    Expect.equal
                        (System.Text.Encoding.UTF8.GetString payload)
                        "hello"
                        "the fifth byte wraps back to the first key byte"
                | Arrival.Waiting -> failwith "the whole frame is there"

            testCase "which channel a frame came in on is its opcode" <| fun () ->
                match Arrival.read helloFrame with
                | Arrival.Whole (opcode, _, _) -> Expect.equal opcode BinaryOpcode "binary is the device's bytes"
                | Arrival.Waiting -> failwith "the whole frame is there"

            // A socket hands over reads, not frames. Both halves of that: a frame can be short of
            // its payload, and a read can carry more than one frame.
            testCase "a frame whose payload has not all arrived is not yet a frame" <| fun () ->
                Expect.equal
                    (Arrival.read (Array.sub helloFrame 0 9))
                    Arrival.Waiting
                    "a header promising five bytes over three of them is a wait, not a fault"

            testCase "the bytes after a frame are what the next read starts from" <| fun () ->
                match Arrival.read (Array.append helloFrame [| 0x88uy; 0x80uy |]) with
                | Arrival.Whole (_, _, rest) ->
                    Expect.equal (List.ofArray rest) [ 0x88uy; 0x80uy ] "the second frame is not consumed with the first"
                | Arrival.Waiting -> failwith "the first frame is whole"
        ]

        testList "the provider's frames, on the way out" [

            testCase "a short payload states its length in the second byte" <| fun () ->
                Expect.equal
                    (List.ofArray (Array.sub (frame BinaryOpcode (Array.create 5 0x41uy)) 0 2))
                    [ 0x82uy; 0x05uy ]
                    "FIN, opcode 2, and a length of five"

            testCase "a payload of 126 bytes or more states its length in the two bytes after" <| fun () ->
                Expect.equal
                    (List.ofArray (Array.sub (frame BinaryOpcode (Array.create 300 0x41uy)) 0 4))
                    [ 0x82uy; 126uy; 0x01uy; 0x2cuy ]
                    "126 is the marker, and the length is what follows it"

            testCase "a frame this peer sends is never masked" <| fun () ->
                Expect.equal
                    ((frame TextOpcode (Array.create 300 0x41uy)).[1] &&& 0x80uy)
                    0uy
                    "§5.1: a server that masks is a protocol error, and the extended form is no exception"
        ]

        testCase "a binary frame is what the device said" <| fun () ->
            Expect.equal
                (Ws.heard (createDecoder ()) (Frame.Binary (utf8Bytes "hello")))
                (Ws.Heard.Output "hello")
                "binary frames are the bytes"

        // The other channel, and the distinction the whole wire is built on: a provider that
        // reaches for its framework's `send_text` to emit device output gets a terminal
        // showing nothing, and a client that quietly accepted it would make text mean two
        // things.
        testCase "a text frame is control, never device output" <| fun () ->
            match Ws.heard (createDecoder ()) (Frame.Text "device output on the wrong channel") with
            | Ws.Heard.Said _ -> ()
            | Ws.Heard.Output text -> failwithf "a text frame is never data (got %s)" text

        testCase "an exited frame carries the code the provider sent" <| fun () ->
            Expect.equal
                (Ws.control """{"type":"exited","code":7}""")
                (Ws.Control.Exited 7)
                "the in-band termination frame is where an exit code comes from"

        testCase "a failed frame carries the provider's own words" <| fun () ->
            Expect.equal
                (Ws.control """{"type":"failed","reason":"the port went away"}""")
                (Ws.Control.Failed "the port went away")
                "the reason reaches the person verbatim"

        testCase "a failed frame that named no reason still says something" <| fun () ->
            Expect.equal
                (Ws.control """{"type":"failed"}""")
                (Ws.Control.Failed "the source failed")
                "a failure with nothing to read is still a failure"

        // `docs/streams.md` MAY 9, from this end: control types will be added, and a client
        // that treated an unknown one as fatal would break on every provider written against
        // the newer spec.
        testCase "a control type from a later version is ignored rather than fatal" <| fun () ->
            Expect.equal
                (Ws.control """{"type":"from-a-later-version"}""")
                Ws.Control.Unknown
                "an unknown control type is nothing, not an error"

        // The other half of that: what a provider sent is a control frame when it SAYS which
        // control it is. Text that does not is not a frame from a later spec, it is device
        // output on the wrong channel — and only that is worth a word to anybody.
        testCase "a text frame that is not JSON at all is not a control frame" <| fun () ->
            Expect.equal
                (Ws.control "device output on the wrong channel")
                Ws.Control.NotControl
                "text that will not parse is nothing, not an error"

        testCase "JSON carrying no type at all is not a control frame" <| fun () ->
            Expect.equal
                (Ws.control """{"cols":80,"rows":24}""")
                Ws.Control.NotControl
                "a control frame is one that says which control it is"

        // MAY 8: a provider that sends both is saying the exit is not the story.
        testCase "a named failure outranks an exit code" <| fun () ->
            Expect.equal
                (Ws.ending (Some "the port went away") (Some 0))
                (SandboxRunFailed "the port went away")
                "the failure is what the person needs to read"

        testCase "an exit code is the ending when nothing failed" <| fun () ->
            Expect.equal (Ws.ending None (Some 7)) (SandboxExited 7) "the code the exited frame carried"

        // The reason termination is a frame and not a close code: an abnormal closure (1006)
        // carries nothing at all, and that path exists whatever the provider intends.
        testCase "a close that said nothing is a failure rather than an exit" <| fun () ->
            Expect.equal
                (Ws.ending None None)
                (SandboxRunFailed "the stream closed without saying why")
                "an ending nobody explained is not an exit 0"

        // The url IS a credential — `docs/streams.md` has an exclusive provider mint a
        // single-use token and spend it on attach — so nothing a person or a log reads may
        // reproduce it.
        testCase "a connection is named without its attach token" <| fun () ->
            let named =
                Ws.describing
                    { Url = "ws://127.0.0.1:7334/attach/8f2c-single-use"
                      Capabilities = device
                      Label = Some "serial console" }

            Expect.isFalse (named.Contains "8f2c-single-use") "the token is not recoverable from the name"

        testCase "a connection is named without credentials from its url" <| fun () ->
            let named =
                Ws.describing
                    { Url = "ws://spender:s3cret@127.0.0.1:7334/attach/8f2c"
                      Capabilities = device
                      Label = Some "serial console" }

            Expect.isFalse (named.Contains "s3cret") "userinfo is a credential of its own"

        // Naming it is the point: a diagnostic a reader cannot act on is no better than none,
        // and the authority is what an operator already knows about their own provider.
        testCase "a connection is named by the authority it is served from" <| fun () ->
            let named =
                Ws.describing
                    { Url = "ws://127.0.0.1:7334/attach/8f2c-single-use"
                      Capabilities = device
                      Label = Some "serial console" }

            Expect.isTrue (named.Contains "127.0.0.1:7334") "which provider, and which port of it"

        // An absent code is not a zero. `x | 0` answered 0 for one, so a provider that said
        // only "it ended" was reported as having ended WELL — the difference a person acts on.
        testCase "an exited frame that named no code is not an exit 0" <| fun () ->
            Expect.equal
                (Ws.control """{"type":"exited"}""")
                (Ws.Control.Exited(-1))
                "no code reported is the domain's -1, never a success"

        testCase "an exited frame whose code is not a number is not an exit 0" <| fun () ->
            Expect.equal
                (Ws.control """{"type":"exited","code":"seven"}""")
                (Ws.Control.Exited(-1))
                "a code this client cannot read is not a success either"

        // `String([])` is "", so a provider can send a `failed` frame whose reason coerces to
        // nothing at all — and a failure that says nothing is unreadable.
        testCase "a failed frame whose reason coerces to empty text still says something" <| fun () ->
            Expect.equal
                (Ws.control """{"type":"failed","reason":[]}""")
                (Ws.Control.Failed "the source failed")
                "a failure with no readable reason still reaches a person as words"

        // The frame's TYPE decides whether it failed; the reason is only the words. An empty
        // one reaching here means the frame decoder let one through, not that nothing failed.
        testCase "a failure whose reason came out empty is still a failure" <| fun () ->
            Expect.equal
                (Ws.ending (Some "") (Some 7))
                (SandboxRunFailed "the source failed")
                "a failed frame is a failure whatever its reason renders as"

        // One decoder for the connection, because UTF-8 does not respect frame boundaries: a
        // character decoded per frame becomes two U+FFFDs, which is what real device output at
        // a buffer boundary looks like.
        testCase "a character split across two binary frames arrives whole" <| fun () ->
            let decoder = createDecoder ()

            let decoded frame =
                match Ws.heard decoder frame with
                | Ws.Heard.Output text -> text
                | Ws.Heard.Said _ -> failwith "a binary frame is device output"
            // The two bytes of "\u00e9".
            let text = decoded (Frame.Binary (oneByte 0xC3)) + decoded (Frame.Binary (oneByte 0xA9))
            Expect.equal text "\u00e9" "the tail of a split character is held until the next frame"
    ]
