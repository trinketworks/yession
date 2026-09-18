module SerialProvider.Interop

// The Node surface this provider needs, and nothing else.
//
// Every binding here is a DECLARATION of something Node already has — a type with the members
// this provider uses, an import, or a one-expression `Emit` over an API F# has no other way
// to name. Nothing here decides anything; what the bindings are used FOR is F#, in the files
// around it. There is no framework underneath either, which is the point of the example: a
// provider is an ordinary HTTP server that happens to answer MCP, and the whole of its
// runtime dependency is `node:http`, the socket an upgrade hands over, a hash function, a
// `stat` and a timer.
//
// It is deliberately a SEPARATE file from the protocol and the device, and it is one of the
// two places this provider binds anything: this is the HOST, and the top of `Ports.fs` is the
// device library. Two axes, so porting along one of them leaves the other alone — take this
// provider to Deno or Bun and this is the file you rewrite; teach it a different way to reach
// a serial port (WebSerial in a browser extension, a daemon over a socket) and that one is.

open Fable.Core

// --- promises -----------------------------------------------------------------------------

// Settled at CREATION, in the same tick the promise was made, rather than inside the
// workflow: that is the only tick in which attaching a handler is guaranteed to beat Node's
// unhandled-rejection check. Deferring it into the `async` reopens the gap this closes, and
// an unhandled rejection in Node is a dead process rather than a console warning.
[<Emit("$0.then(value => [value, null], error => [null, error ?? new Error('promise rejected')])")>]
let private settled (promise: JS.Promise<'a>) : JS.Promise<'a * exn> = jsNative

/// Await a promise; a rejection surfaces as an ordinary exception inside the workflow.
let awaitPromise (promise: JS.Promise<'a>) : Async<'a> =
    let outcome = settled promise
    async {
        let! value, error = outcome |> Async.AwaitPromise
        if isNull (box error) then return value else return raise error
    }

// --- node:http ----------------------------------------------------------------------------

type [<AllowNullLiteral>] IncomingMessage =
    abstract url : string
    abstract ``method`` : string
    abstract on : string * (obj -> unit) -> IncomingMessage

type [<AllowNullLiteral>] ServerResponse =
    abstract writeHead : int * obj -> ServerResponse
    abstract write : string -> bool
    abstract ``end`` : string -> unit

type [<AllowNullLiteral>] HttpServer =
    abstract listen : int * string * (unit -> unit) -> HttpServer
    abstract close : (obj -> unit) -> unit

[<Import("createServer", "node:http")>]
let private createServerRaw : System.Func<IncomingMessage, ServerResponse, unit> -> HttpServer = jsNative

/// Create an HTTP server. The handler is passed as an uncurried delegate so Node receives a
/// plain `(req, res) => ...` two-argument callback rather than F#'s curried chain.
let createServer (handler: IncomingMessage -> ServerResponse -> unit) : HttpServer =
    createServerRaw (System.Func<_, _, _>(handler))

/// The actual bound port, which differs from the requested one when listening on 0 — and
/// listening on 0 is what every test does, so the origin a client is handed has to be read
/// back from the socket rather than assumed from the request.
[<Emit("$0.address().port")>]
let serverPort (server: HttpServer) : int = jsNative

[<Emit("typeof $0 === 'string'")>]
let private isJsString (chunk: obj) : bool = jsNative

[<Emit("$0.toString('utf8')")>]
let private decodeUtf8 (chunk: obj) : string = jsNative

/// Decode a Node Buffer (or string) chunk to a UTF-8 string. A stream hands over whichever
/// of the two its encoding was set to, and only the Buffer has a decode to do — which is a
/// question about the chunk and an answer in F#, not a ternary inside a binding.
let bufferToString (chunk: obj) : string =
    if isJsString chunk then unbox<string> chunk else decodeUtf8 chunk

/// Read a request header. Node lowercases header names; None when absent.
[<Emit("($0.headers[$1] ?? null)")>]
let headerOf (req: IncomingMessage) (name: string) : string option = jsNative

/// Build a plain JS object from pairs — response headers, mostly.
let createObj (pairs: (string * obj) list) : obj = JsInterop.createObj pairs

// --- the upgraded socket ------------------------------------------------------------------

/// The TCP socket an upgrade hands over. From the 101 onwards nothing in `node:http` is
/// involved any more: the connection is a plain byte stream, which is why `Ws.fs` is the whole
/// of this provider's WebSocket and owes nothing to a library.
type [<AllowNullLiteral>] Socket =
    /// Bytes out. A Node `Buffer` IS a `Uint8Array`, which is what Fable compiles `byte[]` to,
    /// so a frame reaches the socket without a copy or a second encoding of its text.
    abstract write : byte[] -> bool
    /// Flush what is written, then FIN. The ordinary end of a stream.
    abstract ``end`` : unit -> unit
    /// Drop it now, unflushed. What a request this server will not serve gets.
    abstract destroy : unit -> unit

[<Emit("$0.on('upgrade', $1)")>]
let private onUpgradeRaw (server: HttpServer) (handler: System.Func<IncomingMessage, Socket, unit>) : unit =
    jsNative

/// Take WebSocket upgrades. An upgrade is an EVENT on the server rather than a request, so it
/// never reaches the handler `createServer` was given — which is why a provider serving both
/// legs on one port has to ask for this separately. Uncurried for the reason `createServer`'s
/// handler is.
let onUpgrade (server: HttpServer) (handler: IncomingMessage -> Socket -> unit) : unit =
    onUpgradeRaw server (System.Func<_, _, _>(handler))

/// Inbound bytes, as they arrive and in whatever sizes the kernel chose — a frame boundary
/// means nothing here, which is the whole reason `Ws.read` takes everything received so far.
[<Emit("$0.on('data', $1)")>]
let onSocketData (socket: Socket) (handler: byte[] -> unit) : unit = jsNative

/// The socket went away, for any reason.
[<Emit("$0.on('close', $1)")>]
let onSocketClosed (socket: Socket) (handler: unit -> unit) : unit = jsNative

/// A socket error. Listened to rather than acted on: an unhandled `'error'` on a Node stream
/// is thrown at the process, and `'close'` follows every error anyway — so the end of a
/// connection is decided in ONE place and the two cannot disagree.
[<Emit("$0.on('error', $1)")>]
let onSocketError (socket: Socket) (handler: unit -> unit) : unit = jsNative

// --- node:crypto --------------------------------------------------------------------------

/// A hash in progress, as much of Node's as the handshake uses.
type [<AllowNullLiteral>] Hash =
    abstract update : string -> Hash
    abstract digest : string -> string

[<Import("createHash", "node:crypto")>]
let private createHash (algorithm: string) : Hash = jsNative

/// SHA-1, base64. Not a security choice and not ours to make: RFC 6455 §4.2.2 names this exact
/// digest over the client's key, and what it proves is that the server understood the upgrade
/// rather than a proxy echoing bytes back. Nothing secret crosses it.
let sha1Base64 (text: string) : string = (createHash "sha1").update(text).digest "base64"

// --- node:fs ------------------------------------------------------------------------------

/// Is there a file at this path? Asked about a device node — see the watchdog in `Ports.fs`.
[<Import("existsSync", "node:fs")>]
let existsSync (path: string) : bool = jsNative

// --- timers -------------------------------------------------------------------------------

/// A repeating timer. Node answers with an object rather than the number a browser returns,
/// which is what makes `unref` reachable at all.
type [<AllowNullLiteral>] Timer =
    /// Stop this timer holding the process open. A watchdog over a device is never a reason
    /// for a provider to stay alive.
    abstract unref : unit -> Timer

[<Emit("setInterval($1, $0)")>]
let setInterval (everyMs: int) (tick: unit -> unit) : Timer = jsNative

[<Emit("clearInterval($0)")>]
let clearInterval (timer: Timer) : unit = jsNative

// --- thrown values ------------------------------------------------------------------------

/// JavaScript truthiness — the question `x || y` asks before it answers. Kept as a QUESTION so
/// the answer is written in F#, where it reads without reconstructing an operator's table.
[<Emit("!!$0")>]
let isTruthy (value: obj) : bool = jsNative

/// `String(x)`: JavaScript's own coercion to text, which is what a thrown value carrying no
/// message has always reached a person as.
[<Emit("String($0)")>]
let asText (value: obj) : string = jsNative

// --- process ------------------------------------------------------------------------------

/// A cryptographically random identifier: MCP session ids and single-use attach tokens.
[<Emit("crypto.randomUUID()")>]
let randomId () : string = jsNative

/// Read an environment variable, falling back when unset or empty.
[<Emit("process.env[$0] || $1")>]
let envOr (name: string) (fallback: string) : string = jsNative

/// The process's arguments, less the executable and script.
[<Emit("process.argv.slice(2)")>]
let args () : string array = jsNative

[<Emit("process.exit($0)")>]
let exit (code: int) : unit = jsNative
