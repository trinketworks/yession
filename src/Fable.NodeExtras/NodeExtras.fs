namespace Fable.NodeExtras

// What a Fable program running on Node needs and `Fable.Node` does not type — plus the
// web-platform globals Node itself ships, which from this host's point of view are part of
// its platform: `TextDecoder`, `WebSocket`, WebCrypto, `ReadableStream`.
//
// Declared here rather than reached for with an `[<Emit>]` at the call site for the reason
// every binding in this repository is: an emit body is inlined into whatever calls it, so it
// is invisible to a reader of that file, unreachable from any other, and — the sharp edge —
// Fable does not treat a change to one as a change to its callers. Editing an emit leaves
// every compiled caller stale, which reads as a test failing for a reason the source does not
// contain. A binding project has none of those problems: it is a module, so it recompiles
// what depends on it.
//
// Only the slice actually used is declared, and the bar for adding is that `Fable.Node` (or a
// sibling already referenced) genuinely lacks it. What Fable.Node already covers and nothing
// here re-declares: `node:events`, `createHash`/`createHmac`/`randomBytes` from `node:crypto`,
// and `Buffer`'s `from`/`toString`/`concat`/`alloc`.
//
// `node:stream` and `node:http` are the two it covers only in part, and the part it misses is
// the one a proxy is made of: `Fable.Node`'s `ClientRequest<'T>` has no `pipe`, no `destroy`
// and no events, and its `IncomingMessage` reaches its headers as `obj`. So the streaming
// slice of both is declared at the end of this file, and nothing else of either is.
//
// Nothing in this file runs on .NET. `dotnet build` type-checks it and stops there; every
// binding below is `jsNative`, an import, or — in `base64url`'s case — a cast that is only
// meaningful once Fable has erased the type it casts to.

open Fable.Core
open Fable.Core.JsInterop
open Node.Buffer
open Node.ChildProcess

// --- Buffer encodings ---------------------------------------------------------------------

/// `base64url` as a `BufferEncoding`, which `Fable.Node` cannot express.
[<AutoOpen>]
module Encodings =

    /// RFC 4648 §5: base64 over the URL-safe alphabet, unpadded. Node has encoded and decoded
    /// it since v14; `Node.Buffer.BufferEncoding` is a CLOSED `[<StringEnum>]`
    /// (`Ascii | Utf8 | Utf16le | Usc2 | Base64 | Latin1 | Binary | Hex`), so there is no case
    /// to reach for and nothing downstream can add one.
    ///
    /// A `[<StringEnum>]` is erased by Fable to the string it stands for, so this cast emits
    /// the literal `'base64url'` — exactly what `Buffer.from` and `Buffer.toString` want.
    /// Declared ONCE so the cast is written once: an `unbox` is a place the compiler has
    /// stopped checking, and the failure mode of a mistyped one is not an error but
    /// `Buffer.from(s, undefined)` silently reading utf8.
    let base64url : BufferEncoding = unbox "base64url"

// --- Decoding bytes to text ---------------------------------------------------------------

/// The WHATWG `TextDecoder`, a Node global since v11. Absent from Fable.Node — which types
/// the other direction only (`Node.Util.TextEncoder`, and `util` is not on `Node.Api`, so it
/// does not even arrive with `open Node.Api`) — and from every browser binding this
/// repository references.
///
/// Opaque: a decoder is a value with a LIFETIME rather than a function, because what it
/// carries between calls is the tail of a multi-byte character that a chunk boundary cut in
/// half. That is the whole reason to hold one, and `decodeChunk` below is the only thing this
/// repository asks of it.
[<AllowNullLiteral>]
type TextDecoder =
    interface
    end

[<AutoOpen>]
module TextDecoding =

    /// `new TextDecoder()`: UTF-8, non-fatal — a sequence that is not valid UTF-8 decodes to
    /// U+FFFD rather than throwing. Both are the constructor's defaults, and the only
    /// configuration used here.
    [<Emit("new TextDecoder()")>]
    let createDecoder () : TextDecoder = jsNative

    /// Decode one chunk OF A STREAM: a character split across this chunk and the next is held
    /// back here and completed by the following call, rather than becoming a U+FFFD in the
    /// middle of otherwise good text. That is what `{ stream: true }` buys, and getting it
    /// wrong is invisible until somebody sends a multi-byte character at a 16KB boundary.
    [<Emit("$0.decode($1, { stream: true })")>]
    let decodeChunk (decoder: TextDecoder) (chunk: JS.Uint8Array) : string = jsNative

// --- Reading a response body as a stream ---------------------------------------------------

/// What one `read()` resolved to. `Done` and a chunk are exclusive — at the end of a stream
/// there is nothing to hand over — which is why `value` is an option rather than a
/// `Uint8Array` that happens to be `undefined`.
[<AllowNullLiteral>]
type ReadableStreamChunk =
    abstract ``done`` : bool
    abstract value : JS.Uint8Array option

/// A reader holding the stream's lock. One read at a time: asking again before the previous
/// promise settles is an error by the spec, not a queue.
[<AllowNullLiteral>]
type ReadableStreamDefaultReader =
    abstract read : unit -> JS.Promise<ReadableStreamChunk>

/// A WHATWG `ReadableStream` — what a `fetch` response's body is. Absent from Fable.Fetch,
/// Fable.Browser.Dom and Fable.Core alike.
[<AllowNullLiteral>]
type ReadableStream =
    /// Take the reader, LOCKING the stream: nothing else can read it until this reader
    /// releases, which is the point rather than a restriction — two readers of one byte
    /// stream each see half of it.
    abstract getReader : unit -> ReadableStreamDefaultReader

[<AutoOpen>]
module Streams =

    /// A response's body, unread, as the stream it is. `Fable.Fetch`'s `Response` types
    /// `text`/`json`/`arrayBuffer` — every one of which waits for the WHOLE body — and has no
    /// `body` member at all, so a response that is a stream by design (`text/event-stream`)
    /// cannot be read through it.
    ///
    /// `None` for a response with no body to read: a 204, the answer to a HEAD.
    [<Emit("$0.body")>]
    let responseBody (response: Fetch.Types.Response) : ReadableStream option = jsNative

// --- WebSocket ------------------------------------------------------------------------------

/// How a BINARY frame arrives. The WHATWG default is `Blob`, which is why a caller that wants
/// bytes says so before the first frame lands.
[<StringEnum>]
type BinaryType =
    | Blob
    | [<CompiledName("arraybuffer")>] ArrayBuffer

/// A frame that arrived. Opaque: everything it carries is reached through `payload` below,
/// because its one interesting property has two types and only JavaScript can say which.
[<AllowNullLiteral>]
type MessageEvent =
    interface
    end

/// What a frame carried. Two cases rather than one payload type because they are two CHANNELS
/// on this wire (`docs/streams.md`): text is control, binary is data, and code that read them
/// as one would make the wire mean two things.
///
/// An F# union rather than `U2<string, JS.ArrayBuffer>`, which is what this was: an erased
/// union is discriminated by a runtime type TEST, and Fable cannot test for `ArrayBuffer` —
/// it refuses the match at compile time rather than emitting one that is always false. So the
/// one question JavaScript can answer (`typeof data === 'string'`) is asked once, here, and
/// what comes out is a union the compiler can match exhaustively.
[<RequireQualifiedAccess>]
type Frame =
    | Text of string
    | Binary of JS.ArrayBuffer

/// Node 22+ ships the WHATWG `WebSocket` as a global; the repo pins 24. No package this
/// repository references types it — not Fable.Browser.Dom, not transitively — so the slice
/// `app/AttachWs.fs` speaks is declared here: construction, the two sends, close, and the
/// four events.
[<AllowNullLiteral>]
type WebSocket =
    /// Set BEFORE the first frame can arrive, i.e. immediately after construction.
    abstract binaryType : BinaryType with get, set

    /// Send a TEXT frame. On this wire that is CONTROL.
    [<Emit("$0.send($1)")>]
    abstract sendText : text: string -> unit

    /// Send a BINARY frame. On this wire that is data.
    [<Emit("$0.send($1)")>]
    abstract sendBinary : bytes: Buffer -> unit

    /// Start the closing handshake. Frames already sent still arrive; frames sent after do
    /// not.
    abstract close : unit -> unit

    // `addEventListener` rather than the `on*` properties, and one member per event rather
    // than one taking the event's name: the name and the handler's type are the same fact,
    // and a member per event is how the type gets to say so.
    [<Emit("$0.addEventListener('open', $1)")>]
    abstract onOpen : handler: (unit -> unit) -> unit

    [<Emit("$0.addEventListener('message', $1)")>]
    abstract onMessage : handler: (MessageEvent -> unit) -> unit

    /// An `error` is ALWAYS followed by a `close`, so a caller that decides both the failure
    /// to open and the end of the stream in one place decides it in `onClose`.
    [<Emit("$0.addEventListener('error', $1)")>]
    abstract onError : handler: (unit -> unit) -> unit

    [<Emit("$0.addEventListener('close', $1)")>]
    abstract onClose : handler: (unit -> unit) -> unit

[<AutoOpen>]
module WebSockets =

    /// `new WebSocket(url)`. THROWS synchronously on a URL it cannot parse or whose scheme is
    /// not `ws:`/`wss:` — a connection that fails to OPEN does not throw, it reports through
    /// `onClose`.
    [<Emit("new WebSocket($0)")>]
    let connect (url: string) : WebSocket = jsNative

    // The three halves of one question, private because two of them are only true on one side
    // of it: read `bytes` off a text frame and the type says `ArrayBuffer` over a string.
    // Nothing outside can, because `payload` is the only way in.
    [<Emit("typeof $0.data === 'string'")>]
    let private isText (event: MessageEvent) : bool = jsNative

    [<Emit("$0.data")>]
    let private frameText (event: MessageEvent) : string = jsNative

    [<Emit("$0.data")>]
    let private frameBytes (event: MessageEvent) : JS.ArrayBuffer = jsNative

    /// What this frame carried. Binary arrives as an `ArrayBuffer` only where `binaryType` was
    /// set to `ArrayBuffer` before it landed; the WHATWG default hands over a `Blob` instead.
    let payload (event: MessageEvent) : Frame =
        if isText event then Frame.Text (frameText event) else Frame.Binary (frameBytes event)

// --- Child processes ------------------------------------------------------------------------

/// What the child's three standard streams are wired to. Node's uniform shorthand; the
/// per-stream form (`['pipe', 'inherit', 'ignore']`) is not declared because nothing here
/// wants the streams to differ.
[<StringEnum>]
type Stdio =
    /// A pipe each, read through `stdout`/`stderr` and written through `stdin`. Node's own
    /// default, stated rather than assumed.
    | Pipe
    /// The parent's own handles — the child writes where this process writes.
    | Inherit
    /// `/dev/null` in both directions.
    | Ignore

/// The options `child_process.spawn` takes, which `Fable.Node` types as `obj` — so `cwd`,
/// `env`, `stdio` and `detached` get no checking at all, and a misspelled one is silently a
/// property Node never reads.
type SpawnOptions =
    { /// Where the child starts. `None` inherits this process's directory.
      Cwd : string option
      /// The child's environment, COMPLETE. Node replaces rather than merges: what is not in
      /// here is not in the child, `PATH` included.
      Env : Map<string, string>
      /// See `Stdio`.
      Stdio : Stdio
      /// Make the child its own process-group leader, so a signal to `-pid` takes the whole
      /// tree down rather than only the process spawned. It also outlives this process unless
      /// something kills it.
      Detached : bool }

[<AutoOpen>]
module ChildProcesses =

    /// Whether `kill` has been CALLED on this child — not whether the child is dead. The
    /// signal may be pending, and one the child handles may never end it at all.
    [<Emit("$0.killed")>]
    let killed (child: ChildProcess) : bool = jsNative

    /// The code the child exited with. `None` while it is still running, and also for a child
    /// a SIGNAL ended — that case reports through `signalCode` and never here, which is why
    /// the two are declared together.
    [<Emit("$0.exitCode")>]
    let exitCode (child: ChildProcess) : int option = jsNative

    /// The signal that ended the child, `None` when one did not.
    [<Emit("$0.signalCode")>]
    let signalCode (child: ChildProcess) : string option = jsNative

    /// Fable.Node's `spawn`, with its `obj` options typed.
    ///
    /// The conversion to the shape Node reads lives here rather than at the call sites for
    /// the reason the whole project exists: it is the only place both the F# record and the
    /// JavaScript object are in view at once.
    ///
    /// Note what the returned `ChildProcess` says about its streams and is wrong about:
    /// Fable.Node pins `stdout`/`stderr`/`stdin` to a chunk type of `string`, which is a lie
    /// unless `setEncoding` was called — real stdio yields `Buffer`, and a caller that wants
    /// text converts it.
    let spawn (command: string) (arguments: string list) (options: SpawnOptions) : ChildProcess =
        let env =
            options.Env |> Map.toList |> List.map (fun (name, value) -> name ==> value) |> createObj

        let js =
            !!{| cwd = options.Cwd
                 env = env
                 stdio = options.Stdio
                 detached = options.Detached |}

        Node.Api.childProcess.spawn (command, ResizeArray arguments, js)

// --- Crypto ---------------------------------------------------------------------------------

/// An imported key. Opaque on purpose: a key imported as non-extractable has no serialization
/// any code path can reach, and a type with members would suggest otherwise.
[<AllowNullLiteral>]
type CryptoKey =
    interface
    end

/// How key material is presented to `importKey`. Only raw bytes here; `pkcs8`, `spki` and
/// `jwk` are the other three the platform takes.
[<StringEnum>]
type KeyFormat =
    | Raw

/// What a key may be used FOR — enforced by the platform, so a key imported to decrypt cannot
/// be talked into signing.
[<StringEnum>]
type KeyUsage =
    | Encrypt
    | Decrypt

/// The algorithm a key is imported UNDER (`{ name: 'AES-GCM' }` and friends).
[<AllowNullLiteral>]
type KeyAlgorithm =
    interface
    end

/// The per-message parameters AES-GCM's `encrypt`/`decrypt` take.
[<AllowNullLiteral>]
type AesGcmParams =
    interface
    end

/// The subset of `crypto.subtle` this repository uses, narrowed to AES-GCM: the algorithm
/// type each operation takes is what says so, and widening it is how a second algorithm would
/// arrive.
[<AllowNullLiteral>]
type SubtleCrypto =
    /// `extractable = false` is the discipline the secrets store and the OIDC signing key both
    /// keep: after import, no code path can serialize the key — `exportKey` rejects.
    abstract importKey :
        format: KeyFormat *
        keyData: Buffer *
        algorithm: KeyAlgorithm *
        extractable: bool *
        keyUsages: KeyUsage array ->
            JS.Promise<CryptoKey>

    abstract encrypt : algorithm: AesGcmParams * key: CryptoKey * data: Buffer -> JS.Promise<JS.ArrayBuffer>

    /// Rejects when authentication fails — a tampered ciphertext, one transplanted onto
    /// another entry's AAD, or the wrong key. That rejection is the guarantee, not an error
    /// case to route around.
    abstract decrypt : algorithm: AesGcmParams * key: CryptoKey * data: Buffer -> JS.Promise<JS.ArrayBuffer>

/// The WHATWG `crypto` global, which on Node is `node:crypto`'s `webcrypto`. Fable.Node types
/// neither.
[<AllowNullLiteral>]
type Crypto =
    abstract subtle : SubtleCrypto
    /// Fill the array with cryptographically strong random bytes IN PLACE, and return it.
    abstract getRandomValues : array: JS.Uint8Array -> JS.Uint8Array

[<AutoOpen>]
module WebCrypto =

    /// The `crypto` global. A function rather than a value so that reaching for it emits
    /// nothing until somebody does.
    [<Emit("crypto")>]
    let webcrypto () : Crypto = jsNative

    /// AES-GCM, as the two dictionaries the platform asks for. Plain JavaScript objects
    /// rather than emits: an anonymous record is one already.
    [<RequireQualifiedAccess>]
    module AesGcm =

        /// What a raw AES key is imported under.
        let algorithm : KeyAlgorithm = !!{| name = "AES-GCM" |}

        /// One message's parameters: an IV that must never repeat under one key (96 bits is
        /// the size GCM is specified for), and the additional authenticated data the
        /// ciphertext is bound to — authenticated, not encrypted, and required identically to
        /// decrypt.
        let parameters (iv: JS.Uint8Array) (additionalData: Buffer) : AesGcmParams =
            !!{| name = "AES-GCM"
                 iv = iv
                 additionalData = additionalData |}

    /// Constant-time comparison, from `node:crypto` — the one place a secret may be compared,
    /// because `=` returns as soon as two bytes differ and that timing is the secret's prefix.
    ///
    /// THROWS when the two differ in length rather than answering `false`, so a caller
    /// comparing things that may differ in length checks that first (and leaks only the
    /// length, which it already leaked by sending it).
    [<Import("timingSafeEqual", "node:crypto")>]
    let timingSafeEqual (left: Buffer) (right: Buffer) : bool = jsNative

// --- Buffers over bytes -----------------------------------------------------------------------

/// The two directions between a `Buffer` and the `Uint8Array` the web-platform half of this
/// file speaks. They are here rather than at a call site because they are the seam between the
/// two byte types Node has, and a program that has to convert once has to convert everywhere.
[<AutoOpen>]
module Bytes =

    /// A `Buffer` over a typed array's bytes — the direction WebCrypto forces: `getRandomValues`
    /// fills a `Uint8Array`, and the only thing that encodes bytes as base64url is a `Buffer`.
    ///
    /// `Fable.Node` types both its `Buffer.from` overloads over `obj`, differing only in an
    /// optional second argument, so a lone typed array is an AMBIGUITY there (FS0041) rather
    /// than a conversion — and `obj` would not have checked it either way.
    ///
    /// `Buffer.from(typedArray)` COPIES the bytes — unlike `Buffer.from(arrayBuffer)`, which
    /// is a view over the same memory — so writing through the array afterwards does not
    /// change what this returned.
    [<Emit("Buffer.from($0)")>]
    let bufferOf (bytes: JS.Uint8Array) : Buffer = jsNative

    /// The same bytes as a `Uint8Array`, which a Node `Buffer` already IS — it is a subclass,
    /// so nothing is copied and nothing is converted. Declared once, here, for the reason
    /// `base64url` is: it is a cast, the compiler has stopped checking, and a cast written at
    /// each call site is a check nobody performs several times over.
    let bytesOf (bytes: Buffer) : JS.Uint8Array = !!bytes

// --- Streams, and the HTTP client that speaks over them --------------------------------------

/// What a stream's `error` event carries. Node's own streams emit an `Error`, but only by
/// convention — `emit('error', …)` can carry anything, `undefined` included — so this types
/// the one property worth reading and `StreamError.describe` is what reads it safely.
[<AllowNullLiteral>]
type StreamError =
    abstract message : string

[<RequireQualifiedAccess>]
module StreamError =

    /// JavaScript's own `String` conversion, for the cases `message` cannot answer.
    [<Emit("String($0)")>]
    let private stringify (error: StreamError) : string = jsNative

    /// What to put in a sentence somebody reads: the error's message, or — when there is
    /// none, because what arrived was not an `Error` or carried an empty message — whatever
    /// JavaScript makes of the value itself. Declared here, beside the type, rather than
    /// written out at each stream that can fail: it is the same defensive dance every time,
    /// and one spelled out per call site is one that is subtly different per call site.
    let describe (error: StreamError) : string =
        if isNull (box error) || System.String.IsNullOrEmpty error.message then stringify error else error.message

/// A byte stream something can be written INTO — an outgoing request, a server response, a
/// child's stdin. Only what a proxy needs of one: somewhere for `pipe` to end up, a way to
/// tear it down, and the failure it reports.
[<AllowNullLiteral>]
type Writable =
    /// Tear the stream down NOW, without finishing what is in flight — the socket goes with
    /// it. What a proxy does to the half it can no longer answer for.
    abstract destroy : unit -> unit

    /// An `error` here is terminal: a stream that has errored never emits `finish`. Declared
    /// as its own member rather than a `on(name, handler)` taking a string for the reason the
    /// WebSocket bindings above are: the event's name and its handler's type are one fact,
    /// and a member per event is how the type gets to say so.
    [<Emit("$0.on('error', $1)")>]
    abstract onError : handler: (StreamError -> unit) -> unit

/// A byte stream bytes can be read OUT of — an incoming request, an upstream response.
[<AllowNullLiteral>]
type Readable =
    /// `source.pipe(destination)`: Node moves the bytes, applying backpressure, and nothing
    /// in this process ever holds them. That is the whole reason a proxy pipes rather than
    /// reads — bytes that are never decoded cannot be decoded WRONG.
    abstract pipe : destination: Writable -> unit

    /// Start the stream flowing with nothing attached to read it, so the bytes are
    /// DISCARDED and the socket is freed. What to do with a response whose body is not
    /// wanted: leaving it paused instead leaks the connection.
    abstract resume : unit -> unit

    abstract destroy : unit -> unit

    /// The stream ended: every byte it had has been handed on. Mutually exclusive with
    /// `onError`, which is why a caller that settles on either settles once.
    [<Emit("$0.on('end', $1)")>]
    abstract onEnd : handler: (unit -> unit) -> unit

    [<Emit("$0.on('error', $1)")>]
    abstract onError : handler: (StreamError -> unit) -> unit

/// A message that ARRIVED over HTTP — its headers, and its body as the stream it is. Both
/// halves of an exchange are one of these on the receiving side, which is why the shape is
/// shared rather than written twice.
[<AllowNullLiteral>]
type HttpMessage =
    inherit Readable

    /// Every header that arrived, as `name, value` pairs — Node LOWERCASES the names on the
    /// way in, so a caller comparing them compares lowercase.
    ///
    /// A value is `obj` because it is a string OR an array of them (a header that repeated),
    /// and a proxy that narrowed it to `string` would silently drop the second `set-cookie`.
    /// Pairs rather than the object itself so that deciding WHICH headers to carry is F# a
    /// test can run, instead of an `Object.entries` loop inside an emit.
    [<Emit("Object.entries($0.headers)")>]
    abstract headerEntries : unit -> (string * obj)[]

/// The upstream's answer to a request this process made.
[<AllowNullLiteral>]
type HttpResponse =
    inherit HttpMessage

    /// The status line's code. Always present on a response that was RECEIVED — Node types
    /// it optional only because the same type is a server's view of a request.
    abstract statusCode : int

/// A request this process is MAKING: write the body into it, and its answer arrives at the
/// callback `httpRequest` took.
[<AllowNullLiteral>]
type HttpRequest =
    inherit Writable

[<AutoOpen>]
module HttpClient =

    // Node splits its client across two modules by scheme, and the two take the same
    // arguments — so the pair is imported once here and `httpRequest` below is the only
    // place that has to know there are two.
    [<Import("request", "node:http")>]
    let private overHttp (url: string) (options: obj) (onResponse: HttpResponse -> unit) : HttpRequest = jsNative

    [<Import("request", "node:https")>]
    let private overHttps (url: string) (options: obj) (onResponse: HttpResponse -> unit) : HttpRequest = jsNative

    /// Open an HTTP request to `url` and call back with the response's head as soon as it
    /// lands — before the body, which is what makes a streaming proxy possible at all.
    ///
    /// `Fable.Node` types this as `ClientRequest<'T>` over a `RequestOptions` whose `method`
    /// is a closed enum and whose `headers` is `obj`, and the request it returns has no
    /// `pipe`, no `destroy` and no events — so the one thing a proxy does with it is exactly
    /// what cannot be said through it.
    ///
    /// The scheme decides the module, here rather than at the call site: `https:` is the one
    /// URL Node's `node:http` cannot open, and a caller that forgot would get a connection
    /// that speaks plaintext at a TLS port and reports it as a parse error.
    let httpRequest
        (url: string)
        (``method``: string)
        (headers: (string * obj)[])
        (onResponse: HttpResponse -> unit)
        : HttpRequest =
        let options = createObj [ "method" ==> ``method``; "headers" ==> createObj headers ]
        if url.StartsWith "https:" then overHttps url options onResponse else overHttp url options onResponse
