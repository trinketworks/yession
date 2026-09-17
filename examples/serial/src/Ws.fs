module SerialProvider.Ws

// The server half of the byte-stream leg (Plan 16, part D/E), hand-written.
//
// `AttachWs.fs` is the client — what a session opens against a provider. This is what a
// provider answers with, and the two are deliberately independent implementations of RFC
// 6455 rather than two halves of one library: the whole reason the data plane is a WebSocket
// is that a THIRD PARTY can be on either end, and a pair built on one library would only ever
// prove the two agreed with each other.
//
// Only what the attach protocol needs: an upgrade, unfragmented text and binary frames, the
// client's mask, close and ping. No extensions, no continuation frames, no compression — a
// device stream is short frames in both directions, and every line here is a line somebody
// has to be able to check against the RFC.
//
// Which is why the frame arithmetic is F# and the socket is the only thing bound: reading a
// length, walking a mask and refusing a frame this provider will not carry are all decisions,
// and a decision belongs where it can be read, compiled and tested one case at a time. The
// three pure functions below are that half; `serve` is the half that needs a socket to mean
// anything.

open System.Text
open SerialProvider.Interop

// --- the wire -----------------------------------------------------------------------------

/// The opcodes this provider speaks, as RFC 6455 §5.2 numbers them. Anything else on the wire
/// is read and dropped: a frame type this build has no meaning for is a peer written against
/// more of the RFC than this, and guessing at it would be worse than ignoring it.
module Opcode =

    /// A TEXT frame: the control channel, carrying JSON the client reads as protocol.
    [<Literal>]
    let Text = 0x1

    /// A BINARY frame: the device's own bytes. Keeping the two on different opcodes is what
    /// stops a device that happens to print JSON being mistaken for the provider talking.
    [<Literal>]
    let Binary = 0x2

    [<Literal>]
    let Close = 0x8

    [<Literal>]
    let Ping = 0x9

    [<Literal>]
    let Pong = 0xa

/// Text as bytes and bytes as text: UTF-8 both ways. A text frame is DEFINED to carry UTF-8,
/// and this provider's binary frames carry it too — a device's bytes are decoded to text back
/// in `Ports.fs`, at the stream that holds the tail of a character a read cut in half.
let private encode (text: string) : byte[] = Encoding.UTF8.GetBytes text
let private decode (bytes: byte[]) : string = Encoding.UTF8.GetString bytes

/// A big-endian 16-bit number, which is the first of the RFC's two length extensions.
let private uint16At (bytes: byte[]) (at: int) = (int bytes.[at] <<< 8) ||| int bytes.[at + 1]

/// A big-endian 32-bit number, read into an ordinary `int`. Only ever asked of a word
/// `addressable` has already admitted, so the top bit cannot reach the sign.
let private uint32At (bytes: byte[]) (at: int) =
    (int bytes.[at] <<< 24)
    ||| (int bytes.[at + 1] <<< 16)
    ||| (int bytes.[at + 2] <<< 8)
    ||| int bytes.[at + 3]

/// Is a 64-bit length one this provider will read at all?
///
/// The high word must be zero, and it is REFUSED rather than truncated: a four-gigabyte frame
/// is not a device, and a reader that kept the low word would go looking for the next frame at
/// an offset that is not a frame boundary — so everything after it would be read as garbage
/// rather than as the one thing that is certainly true, which is that this peer is not
/// speaking the protocol.
///
/// The low word's top bit goes the same way, for the same reason said as an index: a length
/// that cannot be addressed is one no buffer here could ever hold, and the alternative to
/// refusing it is an arithmetic answer nobody meant.
let private addressable (bytes: byte[]) = uint32At bytes 2 = 0 && int bytes.[6] &&& 0x80 = 0

/// One frame, ready for the socket: FIN set, and never masked — RFC 6455 §5.1 forbids a server
/// to mask, and a client that is sent a masked frame is entitled to hang up on it.
///
/// Three length forms because the RFC has three, and the smallest that holds the payload is
/// the one that is legal: a length written in a wider form than it needs is a protocol error
/// rather than merely wasteful. The 64-bit form's high word is always zero here, for the same
/// reason `addressable` refuses one that is not.
let frame (opcode: int) (payload: byte[]) : byte[] =
    let header =
        if payload.Length < 126 then [| byte (0x80 ||| opcode); byte payload.Length |]
        elif payload.Length < 65536 then
            [| byte (0x80 ||| opcode)
               126uy
               byte (payload.Length >>> 8)
               byte payload.Length |]
        else
            [| byte (0x80 ||| opcode)
               127uy
               0uy
               0uy
               0uy
               0uy
               byte (payload.Length >>> 24)
               byte (payload.Length >>> 16)
               byte (payload.Length >>> 8)
               byte payload.Length |]

    Array.append header payload

/// Unmask a payload: RFC 6455 §5.3 — every byte XORed with one of four key bytes, cycling.
///
/// One function for both directions, because XOR is its own inverse: masking and unmasking are
/// the same operation, which is also what lets a test mask a frame with the very function that
/// reads it back.
let unmask (key: byte[]) (payload: byte[]) : byte[] =
    payload |> Array.mapi (fun i value -> value ^^^ key.[i % 4])

/// What the bytes received so far amount to.
///
/// A socket hands over whatever the kernel had, so a chunk is neither a frame nor necessarily
/// a whole one: this takes everything received and says which of three things is true of it.
/// That is the reason it is a function over an array rather than something inside the data
/// handler — the interesting cases are a frame split across two chunks and two frames in one,
/// and neither costs a socket to write down.
[<RequireQualifiedAccess>]
type Read =
    /// Not a whole frame yet. Nothing to do but wait for more bytes.
    | Incomplete
    /// A frame, unmasked, and whatever followed it — which may be the start of the next one.
    | Frame of opcode: int * payload: byte[] * rest: byte[]
    /// A length this provider will not read. See `addressable`: the connection is dropped
    /// rather than resynchronised, because there is nowhere honest to resynchronise to.
    | Oversized

/// Read the first frame out of everything received so far.
let read (bytes: byte[]) : Read =
    // Two bytes before anything can be said at all: the opcode, and the byte that says how the
    // length is written.
    if bytes.Length < 2 then Read.Incomplete
    else

    let opcode = int bytes.[0] &&& 0x0f
    let masked = int bytes.[1] &&& 0x80 <> 0
    let short = int bytes.[1] &&& 0x7f

    if short = 126 && bytes.Length < 4 then Read.Incomplete
    elif short = 127 && bytes.Length < 10 then Read.Incomplete
    elif short = 127 && not (addressable bytes) then Read.Oversized
    else

    // 126 and 127 are escapes rather than lengths: they say the real length follows, in two
    // bytes or in eight.
    let length, header =
        if short = 126 then uint16At bytes 2, 4
        elif short = 127 then uint32At bytes 6, 10
        else short, 2

    // A client MUST mask (§5.3) and a server must not, so the four key bytes are the client's
    // alone — read where the header says they sit rather than assumed, because a peer that
    // sent none is a peer whose payload starts four bytes earlier.
    let offset = if masked then header + 4 else header

    if bytes.Length < offset + length then Read.Incomplete
    else

    let payload = Array.sub bytes offset length
    let payload = if masked then unmask (Array.sub bytes header 4) payload else payload
    Read.Frame (opcode, payload, Array.sub bytes (offset + length) (bytes.Length - offset - length))

/// The path a request line carries, which is what a route is chosen by: everything before the
/// query and the fragment. An upgrade's request-target is origin-form (`/attach/<token>`) and
/// the token rides the PATH here — but a client free to append a query must still reach the
/// same route.
let pathOf (target: string) : string =
    match target.IndexOfAny [| '?'; '#' |] with
    | -1 -> target
    | cut -> target.Substring (0, cut)

// --- the connection -----------------------------------------------------------------------

/// The constant RFC 6455 §1.3 has a server append to the client's key before hashing. Not a
/// secret and not ours: what the digest proves is that this server understood the upgrade,
/// rather than a proxy echoing bytes back at a client.
[<Literal>]
let private handshakeGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"

/// One connected peer, from the provider's side.
type WsPeer =
    { /// A DATA frame (binary): bytes from the device.
      Send : string -> unit
      /// A CONTROL frame (text): JSON the client reads as protocol, never as device output.
      /// Keeping the two on different opcodes is what stops a device that happens to print
      /// JSON from being mistaken for the provider talking.
      Control : string -> unit
      Close : unit -> unit }

/// What a route does with a connection it took.
type WsHandlers =
    { /// A binary frame: bytes the client wants written to the device.
      Data : string -> unit
      /// A text frame: control JSON (`resize`, `kill`).
      Control : string -> unit
      /// The socket went away, for any reason. Fires exactly once.
      Closed : unit -> unit }

/// Serve WebSocket upgrades on `server`, dispatching by path.
///
/// `route` is given the request path and decides whether to take the connection: `None`
/// refuses the upgrade (the socket is closed with a 404 handshake), `Some` receives the peer
/// and returns the two callbacks the provider wants — what to do with an inbound data frame,
/// and what to do when the socket goes away.
let serve (server: HttpServer) (route: string -> (WsPeer -> WsHandlers) option) : unit =
    onUpgrade server (fun req socket ->
        // Listened to rather than acted on — see `onSocketError`.
        onSocketError socket ignore

        match route (pathOf req.url), headerOf req "sec-websocket-key" with
        | None, _ ->
            socket.write (encode "HTTP/1.1 404 Not Found\r\nConnection: close\r\n\r\n") |> ignore
            socket.destroy ()
        // A request carrying no `Sec-WebSocket-Key` is not a WebSocket handshake: RFC 6455
        // §4.2.1 requires one, and it is the only thing the accept header is computed from.
        // Refused here rather than answered with a digest of the absence — an accept the
        // client is bound to reject a moment later, with nothing said about why.
        | Some _, None ->
            socket.write (encode "HTTP/1.1 400 Bad Request\r\nConnection: close\r\n\r\n") |> ignore
            socket.destroy ()
        | Some bind, Some key ->
            socket.write (
                encode (
                    "HTTP/1.1 101 Switching Protocols\r\n"
                    + "Upgrade: websocket\r\nConnection: Upgrade\r\n"
                    + "Sec-WebSocket-Accept: "
                    + sha1Base64 (key + handshakeGuid)
                    + "\r\n\r\n"
                )
            )
            |> ignore

            // Gone once, whichever end ended it: our own close, the client's close frame, or
            // the socket going away underneath both. A peer that has gone is not written to,
            // and the route hears about it exactly one time.
            let gone = ref false

            let send (opcode: int) (text: string) =
                if not gone.Value then
                    socket.write (frame opcode (encode text)) |> ignore

            let peer =
                { Send = send Opcode.Binary
                  Control = send Opcode.Text
                  Close =
                    fun () ->
                        if not gone.Value then
                            gone.Value <- true
                            // The close frame is a courtesy and the FIN is what ends the
                            // connection, so a socket already gone is not a fault here.
                            (try
                                socket.write (frame Opcode.Close Array.empty) |> ignore
                             with _ ->
                                 ())

                            socket.``end`` () }

            let handlers = bind peer

            let finish () =
                if not gone.Value then
                    gone.Value <- true
                    handlers.Closed ()

            // Everything received and not yet read as a frame. A frame arrives across as many
            // chunks as the network felt like, and two frames arrive in one chunk just as
            // readily.
            let pending = ref Array.empty<byte>

            onSocketData socket (fun chunk ->
                pending.Value <- Array.append pending.Value chunk
                let mutable reading = true

                while reading do
                    match read pending.Value with
                    | Read.Incomplete -> reading <- false
                    | Read.Oversized ->
                        socket.destroy ()
                        reading <- false
                    | Read.Frame (opcode, payload, rest) ->
                        pending.Value <- rest

                        if opcode = Opcode.Close then
                            finish ()
                            socket.``end`` ()
                            reading <- false
                        elif opcode = Opcode.Ping then socket.write (frame Opcode.Pong payload) |> ignore
                        elif opcode = Opcode.Text then handlers.Control (decode payload)
                        elif opcode = Opcode.Binary then handlers.Data (decode payload))

            onSocketClosed socket finish)
