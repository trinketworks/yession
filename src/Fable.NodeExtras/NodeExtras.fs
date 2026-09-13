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
// here re-declares: `node:stream` entire, `node:events`, `node:http`, `createHash`/
// `createHmac`/`randomBytes` from `node:crypto`, and `Buffer`'s `from`/`toString`/`concat`/
// `alloc`.
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
