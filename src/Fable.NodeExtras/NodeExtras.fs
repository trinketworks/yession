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
// `node:net` it covers but for the one member a probe wants: it types a server and its
// `listen`, and then answers `address()` with `obj` — so the port the OS just chose is
// exactly what cannot be read through it. `node:tty` it does not type at all. Both slices
// are declared below, and in both cases only what a probe or a pty's far end does with one.
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

// --- The synchronous file calls a durable append is made of -------------------------------

/// What `node:fs` offers a writer that must be durable before it answers, and `Fable.Node`
/// does not type: a descriptor opened for APPEND rather than truncation, a `writeSync` that
/// takes a string, the flush that makes a write durable, and `mkdirSync` with `recursive`.
///
/// Three stores and the Manager's shared file primitives had each written the same four
/// macros, with the same comment above them saying which four members were missing — four
/// copies of one binding, each invisible to the others. They are imports rather than emits,
/// which is what the members allow once the flag and the options object are F# values.
[<AutoOpen>]
module Files =

    [<Import("openSync", "node:fs")>]
    let private openSyncWithFlag (path: string) (flag: string) : int = jsNative

    [<Import("mkdirSync", "node:fs")>]
    let private mkdirSyncWithOptions (path: string) (options: obj) : unit = jsNative

    /// A descriptor on `path` for appending, creating the file when it is not there (`'a'`).
    /// `Fable.Node`'s `openSync` takes the path alone, which is `'r'` — a reader.
    let openAppend (path: string) : int = openSyncWithFlag path "a"

    /// A descriptor on `path` for writing, truncating what is there (`'w'`). The half of an
    /// atomic write that happens out of sight, before the rename.
    let openTruncate (path: string) : int = openSyncWithFlag path "w"

    /// Write text to a descriptor. `Fable.Node` types `writeSync` over a `Buffer` only, and
    /// the answer — how many bytes went — is what a partial write is visible through.
    [<Import("writeSync", "node:fs")>]
    let writeText (fd: int) (text: string) : int = jsNative

    /// Flush a descriptor to the device. This is the call that makes a write DURABLE rather
    /// than merely issued, so it is what a store does before it answers.
    [<Import("fsyncSync", "node:fs")>]
    let fsync (fd: int) : unit = jsNative

    /// Create a directory and any missing parents; a no-op when it is already there.
    /// `Fable.Node`'s `mkdirSync` takes no options, so it cannot say `recursive`.
    let mkdirp (path: string) : unit = mkdirSyncWithOptions path (createObj [ "recursive", box true ])

    [<Import("rmSync", "node:fs")>]
    let private rmSyncWithOptions (path: string) (options: obj) : unit = jsNative

    /// Remove a path and everything under it, and say nothing about one that was not there
    /// (`recursive` + `force`). `Fable.Node` types `rmdirSync` and `unlinkSync`, neither of
    /// which is this: one refuses a non-empty directory and the other refuses a directory.
    let removeTree (path: string) : unit =
        rmSyncWithOptions path (createObj [ "recursive", box true; "force", box true ])

    /// Copy a file, overwriting the destination. `Fable.Node` does not type `copyFileSync`.
    [<Import("copyFileSync", "node:fs")>]
    let copyFile (source: string) (destination: string) : unit = jsNative

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

// --- The process environment ----------------------------------------------------------------

/// `process.env`, one variable at a time. Bindings and nothing else: what a variable MEANS when
/// it is unset or empty is a decision, and the decision belongs where the variable is read —
/// which the environment rules (`EnvReaders`, `EnvWrites`) hold to one place per variable and
/// one writing file per assembly. Those rules recognise an access by the macro on its callee,
/// so these ARE the macros, and a wrapper that hands its parameter to one of them is a reader
/// in its own right.
[<RequireQualifiedAccess>]
module ProcessEnv =

    /// The variable's value, or nothing when it is not set at all. Set to the empty string it
    /// is `Some ""`: whether that counts as set is the reader's call, not this binding's.
    [<Emit("process.env[$0]")>]
    let get (name: string) : string option = jsNative

    [<Emit("process.env[$0] = $1")>]
    let set (name: string) (value: string) : unit = jsNative

    [<Emit("delete process.env[$0]")>]
    let unset (name: string) : unit = jsNative

    /// Every variable's NAME. Names only: a report can say which are present without reading
    /// any of them, so this is not a reader of anything.
    [<Emit("Object.keys(process.env)")>]
    let names () : string array = jsNative

// --- This process ---------------------------------------------------------------------------

/// What `Fable.Node`'s `process` leaves out. Everything it types — `execPath`, `platform`,
/// `argv`, `exit`, `stdin`, the listeners — is used through `Node.Api.process` directly.
[<RequireQualifiedAccess>]
module Processes =

    /// `process.kill(pid, signal)`: a signal to a process by id — or to a whole process group,
    /// which is what a NEGATIVE id names, and how a detached child's tree is taken with one.
    [<Emit("process.kill($0, $1)")>]
    let kill (pid: int) (signal: string) : unit = jsNative

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

    /// A hash of the bytes, by name (`"SHA-256"`). Keyless, so there is no key to import and
    /// nothing to keep: the only thing it can leak is what was hashed.
    abstract digest : algorithm: string * data: Buffer -> JS.Promise<JS.ArrayBuffer>

/// The WHATWG `crypto` global, which on Node is `node:crypto`'s `webcrypto`. Fable.Node types
/// neither.
[<AllowNullLiteral>]
type Crypto =
    abstract subtle : SubtleCrypto
    /// Fill the array with cryptographically strong random bytes IN PLACE, and return it.
    abstract getRandomValues : array: JS.Uint8Array -> JS.Uint8Array

[<AutoOpen>]
module WebCrypto =

    /// `crypto.randomUUID()` — a v4 UUID from the platform's CSPRNG. On `crypto` the global,
    /// which `Fable.Node`'s `node:crypto` typings predate.
    [<Emit("crypto.randomUUID()")>]
    let randomUUID () : string = jsNative

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

    /// A `Buffer` over an `ArrayBuffer`'s bytes — the shape WebCrypto answers a digest in,
    /// and the only thing that encodes bytes as base64url is a `Buffer`. A VIEW rather than a
    /// copy, which is what `Buffer.from(arrayBuffer)` means and is safe here because the
    /// digest's buffer belongs to nobody else.
    [<Emit("Buffer.from($0)")>]
    let bufferOverArrayBuffer (bytes: JS.ArrayBuffer) : Buffer = jsNative

    /// The same bytes as a `Uint8Array`, which a Node `Buffer` already IS — it is a subclass,
    /// so nothing is copied and nothing is converted. Declared once, here, for the reason
    /// `base64url` is: it is a cast, the compiler has stopped checking, and a cast written at
    /// each call site is a check nobody performs several times over.
    let bytesOf (bytes: Buffer) : JS.Uint8Array = !!bytes

// --- What was thrown ----------------------------------------------------------------------------

[<AutoOpen>]
module Thrown =

    /// An `Error` as JavaScript shapes one: the two properties a sentence is made from.
    [<AllowNullLiteral>]
    type JsError =
        abstract name : string
        abstract message : string

    /// Whether what was thrown is an `Error`. JavaScript lets a `throw` carry any value at
    /// all, and F#'s `with` binds whatever arrived without asking — so code handing a caught
    /// value on to somebody who expects an `Error` is the code that has to ask.
    [<Emit("$0 instanceof Error")>]
    let isError (thrown: obj) : bool = jsNative

    /// What was thrown, as words. Every kind of value a `throw` can carry is named here and
    /// made into text on its own terms, where `String(x)` used to be asked — and answered
    /// `[object Object]` for an object, `null` in the middle of a sentence for nothing, and
    /// `Error` for an error carrying no message. An `Error` is its message, or its name when
    /// the message is empty; text is itself; a number or a boolean is its digits or its word;
    /// nothing at all says so; anything else is its JSON, which is at least the value, or — for
    /// the values JSON has no text for, a function or a symbol — says that much.
    let describe (thrown: obj) : string =
        match thrown with
        | null -> "nothing"
        | :? string as text -> text
        | :? float as number -> string number
        | :? bool as flag -> if flag then "true" else "false"
        | error when isError error ->
            let error = unbox<JsError> error
            if System.String.IsNullOrEmpty error.message then error.name else error.message
        | value ->
            match (try JS.JSON.stringify value with _ -> null) with
            | null -> "a value with no text"
            | json -> json

    /// `new Error(message)` — the platform's own error, not F#'s `exn`, which Fable compiles
    /// to a class of its own. Both are `instanceof Error`, and only one of them is what a
    /// listener written in JavaScript will have its hands on.
    [<Emit("new Error($0)")>]
    let errorWith (message: string) : exn = jsNative

// --- Streams, and the HTTP client that speaks over them --------------------------------------

/// What a stream's `error` event carries. Node's own streams emit an `Error`, but only by
/// convention — `emit('error', …)` can carry anything, `undefined` included — so this types
/// the one property worth reading and `StreamError.describe` is what reads it safely.
[<AllowNullLiteral>]
type StreamError =
    abstract message : string

[<RequireQualifiedAccess>]
module StreamError =

    /// What to put in a sentence somebody reads: the error's message, or — when there is
    /// none, because what arrived was not an `Error` or carried an empty message — what
    /// `Thrown.describe` makes of the value itself. Declared here, beside the type, rather
    /// than written out at each stream that can fail: it is the same defensive dance every
    /// time, and one spelled out per call site is one that is subtly different per call site.
    let describe (error: StreamError) : string =
        if isNull (box error) || System.String.IsNullOrEmpty error.message then Thrown.describe (box error) else error.message

/// A byte stream something can be written INTO — an outgoing request, a server response, a
/// child's stdin. Only what a proxy needs of one: somewhere for `pipe` to end up, a way to
/// tear it down, and the failure it reports.
[<AllowNullLiteral>]
type Writable =
    /// Tear the stream down NOW, without finishing what is in flight — the socket goes with
    /// it. What a proxy does to the half it can no longer answer for.
    abstract destroy : unit -> unit

    /// Write BYTES, which is the only way a body that is not text can go out: `write` on a
    /// string ENCODES it — utf8 unless told otherwise — so a byte above 0x7F leaves as two,
    /// and a packfile relayed that way is a packfile git cannot read.
    ///
    /// The `false` it answers means the stream's buffer is full: what is written after it is
    /// held in memory rather than on the wire until `drain`, which is what a writer that
    /// minds backpressure waits for.
    [<Emit("$0.write($1)")>]
    abstract writeBytes : bytes: Buffer -> bool

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

    /// Every chunk, as it arrives — which starts the stream FLOWING, exactly as `pipe` and
    /// `resume` do, and hands the bytes to F# instead of to another stream or to nothing.
    /// What to attach when the bytes have to be READ on the way past.
    ///
    /// A chunk is a `Buffer`, whatever `Fable.Node` says about a child process's streams —
    /// unless `setEncoding` was called, after which it is text and `Readables.text` below is
    /// the way to say so.
    [<Emit("$0.on('data', $1)")>]
    abstract onData : handler: (Buffer -> unit) -> unit

    /// Have the STREAM decode its bytes to text, with the tail of a character a chunk
    /// boundary cut in half carried into the next chunk — which per-chunk `toString('utf8')`
    /// cannot do, and is why a `TextDecoder` is held rather than called. Node returns the
    /// stream, for chaining; nothing here chains.
    abstract setEncoding : encoding: BufferEncoding -> Readable

    /// The stream ended: every byte it had has been handed on. Mutually exclusive with
    /// `onError`, which is why a caller that settles on either settles once.
    [<Emit("$0.on('end', $1)")>]
    abstract onEnd : handler: (unit -> unit) -> unit

    [<Emit("$0.on('error', $1)")>]
    abstract onError : handler: (StreamError -> unit) -> unit

[<RequireQualifiedAccess>]
module Readables =

    /// The data event AFTER `setEncoding`, when a chunk is a string. Private, because the type
    /// is true only on that side of the call: attached to a stream nobody told to decode, it
    /// hands a `Buffer` to a handler that was promised text.
    [<Emit("$0.on('data', $1)")>]
    let private onText (stream: Readable) (handler: string -> unit) : unit = jsNative

    /// Every chunk as TEXT. One verb rather than `setEncoding` and a data event apart, for
    /// the reason the WebSocket bindings above keep `payload` as the only way in: a handler
    /// typed `string` is a promise the encoding keeps, and a caller who could attach one
    /// without the other is the caller who gets bytes where the type said text.
    ///
    /// Sixteen readers in this repository used to ask `typeof chunk === 'string'` and call
    /// `toString('utf8')` on the other branch — a decision per chunk, made in JavaScript, and
    /// wrong at every chunk boundary that split a multi-byte character.
    let text (stream: Readable) (handler: string -> unit) : unit =
        stream.setEncoding BufferEncoding.Utf8 |> ignore
        onText stream handler

/// The connection a message arrived on: the one thing read off it is who is at the other end.
[<AllowNullLiteral>]
type Socket =
    /// The peer's address, and nothing once the socket has been torn down.
    abstract remoteAddress : string option

/// A message that ARRIVED over HTTP — its headers, and its body as the stream it is. Both
/// halves of an exchange are one of these on the receiving side, which is why the shape is
/// shared rather than written twice.
[<AllowNullLiteral>]
type HttpMessage =
    inherit Readable

    /// The socket it arrived on — nothing once the connection is gone, which a message read
    /// after its peer disconnected can be.
    abstract socket : Socket option

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
/// The half of a spawned child that is only sayable once `Readable` exists: its streams as
/// the streams this project reads, and the two events that end it. `ChildProcesses` above
/// declares the spawn.
[<AutoOpen>]
module ChildProcessStreams =

    /// The child's output as the stream `Readables.text` can tell to decode. Fable.Node types
    /// the same stream as a `Readable<string>`, which is a chunk type it has only if
    /// somebody called `setEncoding` — `Readables.text` is that somebody.
    [<Emit("$0.stdout")>]
    let stdout (child: ChildProcess) : Readable = jsNative

    [<Emit("$0.stderr")>]
    let stderr (child: ChildProcess) : Readable = jsNative

    /// A spawn that failed before exec, or a child that could not be signalled: the
    /// platform's own `Error`, for `StreamError.describe` to read.
    [<Emit("$0.on('error', $1)")>]
    let onError (child: ChildProcess) (handler: StreamError -> unit) : unit = jsNative

    /// The child ended and its stdio closed. `None` is a child a signal took, which Node
    /// reports as a `null` code; what to say about that is the caller's.
    [<Emit("$0.on('close', $1)")>]
    let onClose (child: ChildProcess) (handler: int option -> unit) : unit = jsNative

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

// --- A port nothing else is on -----------------------------------------------------------------

/// A `node:net` server as a PROBE uses one: bound to port 0, asked what the OS chose, and
/// handed straight back. What a caller wants out of it is that number — a port to give a
/// child that refuses to pick its own.
///
/// `Fable.Node` types a `net.Server` and its `listen`, and then answers `address()` with
/// `obj`, so the one fact this exists to read is the one fact it cannot say. The other two
/// steps are declared beside `boundPort` rather than reached for through `Fable.Node` because
/// the three are one act: `address()` answers `null` before `listen` has called back, and
/// `null` again after `close`, so a port read outside that window is not a port.

/// Where a listening server is bound.
[<AllowNullLiteral>]
type BoundAddress =
    abstract port : int

/// Anything that listens: a `net.Server`, an `http.Server`. `address()` answers `null` before
/// `listen` has called back and again after `close`, which is what the option says.
[<AllowNullLiteral>]
type Listening =
    abstract address : unit -> BoundAddress option

[<AllowNullLiteral>]
type NetServer =
    inherit Listening

    /// Bind, and call back once the OS has chosen. Port 0 is the whole point — asked for a
    /// particular port, a probe would be racing whoever else wanted that one.
    abstract listen : port: int * host: string * onListening: (unit -> unit) -> unit

    /// Stop listening, and call back once the socket is released — before which the port is
    /// still this process's, and a child told to bind it would be refused. Node hands this
    /// callback an error when the server was not open, which is not a case a probe can be in:
    /// it closes one server, once, having just watched it listen.
    abstract close : onClosed: (unit -> unit) -> unit

[<AutoOpen>]
module NetServers =

    /// A server with no connection handler, because nothing ever connects to a probe: what it
    /// is for is holding a port long enough to be told which one it got.
    [<Import("createServer", "node:net")>]
    let createNetServer () : NetServer = jsNative

    /// The port a LISTENING server was given. Asked of one that is not listening, this is a
    /// fault at the caller — a port read outside the listen/close window is not a port — and
    /// says so rather than answering a number.
    let boundPort (server: #Listening) : int =
        match server.address () with
        | Some bound -> bound.port
        | None -> failwith "the server is not listening, so it has no port"

// --- A terminal, by a descriptor something else opened -----------------------------------------

[<AutoOpen>]
module Ttys =

    /// `node:tty`'s `ReadStream` class, imported as the value it is so that the construction
    /// below is Node's own `new`.
    [<Import("ReadStream", "node:tty")>]
    let private readStreamClass : obj = jsNative

    [<Emit("new ($0)($1)")>]
    let private construct (cls: obj) (fd: int) : Readable = jsNative

    /// Read a tty through a descriptor already open on it — a pty's far end, a terminal this
    /// process was handed. `Fable.Node` types no `node:tty` at all, and the stream it does
    /// type is the wrong one here in a way that costs a day to find.
    ///
    /// `fs.createReadStream` reads through the libuv THREADPOOL, and a blocking read on a pty
    /// is not cancellable: `destroy()` returns while the read is still parked in a worker
    /// thread, and closing the descriptor then frees the NUMBER for reuse. The next `spawn`
    /// gets it back as a child's stderr pipe, and the stale read swallows what that child
    /// says — which presents as a child that announced nothing in time, from a child that is
    /// running perfectly.
    ///
    /// A tty handle is epoll-driven on the event loop, with nothing in flight to outlive it.
    /// It does not own the descriptor either, so closing that stays the caller's to do — and
    /// is safe to do once this has been destroyed.
    let openTty (fd: int) : Readable = construct readStreamClass fd

// --- Aborting ---------------------------------------------------------------------------------

/// The WHATWG `AbortSignal` — a Node global since v15, and what a cancellable Node API takes.
/// `Fable.Node` types none of it, and the `Fable.Browser.*` family is not on a Node program's
/// path.
///
/// The LISTENING end only, because that is the end this repository holds: the signals it sees
/// arrive from somebody else's API (the agent SDK hands one to the spawner it is given), and
/// what fires one is an `AbortController` nothing here constructs.
[<AllowNullLiteral>]
type AbortSignal =

    /// Whether the signal has ALREADY fired. A listener registered after that is never
    /// called, so this is asked BESIDE registering one rather than instead of it.
    abstract aborted : bool

    /// Run `handler` when the signal fires. `{ once: true }` is not tidiness: `abort` fires
    /// at most once by the spec, so a listener that removes itself costs nothing and is what
    /// keeps a long-lived signal from retaining every handler ever hung on it.
    [<Emit("$0.addEventListener('abort', $1, { once: true })")>]
    abstract onAbort : handler: (unit -> unit) -> unit

// --- Relaying somebody else's listeners --------------------------------------------------------

/// A Node `EventEmitter` used as a RELAY: what one thing said, re-emitted to listeners
/// somebody ELSE wrote — registered through here and never called from here.
///
/// `Fable.Node`'s `EventEmitter` cannot say that. It types a listener as an F# function of a
/// definite arity, which is right for a listener written here and wrong for one passed
/// through: Fable adapts a function whose arity it can see, and an adapted listener is a
/// DIFFERENT function object — so `off` would no longer match what `on` registered, and a
/// two-argument listener hung on `exit` would be handed one. Nor is there an arity to see,
/// since it varies by event.
///
/// So a listener is `obj` here: it arrives as a JavaScript function and is handed on as that
/// same function, which is the only thing a relay may do with one.
[<AllowNullLiteral>]
type EventRelay =

    [<Emit("$0.on($1, $2)")>]
    abstract on : ``event``: string * listener: obj -> unit

    [<Emit("$0.once($1, $2)")>]
    abstract once : ``event``: string * listener: obj -> unit

    [<Emit("$0.off($1, $2)")>]
    abstract off : ``event``: string * listener: obj -> unit

    /// Emit one event, with the arguments its listeners take. THROWS when the event is
    /// `error` and nothing is listening — Node's own rule, and the reason a relay is a real
    /// `EventEmitter` rather than a list of functions kept here.
    [<Emit("$0.emit($1, ...$2)")>]
    abstract emit : ``event``: string * arguments: obj array -> unit

[<AutoOpen>]
module EventRelays =

    /// `new EventEmitter()`, with its listeners left opaque.
    let createRelay () : EventRelay = !!Node.Api.events.EventEmitter.Create ()

// --- Child processes: an environment this process did not build ----------------------------------

[<AutoOpen>]
module ChildProcessSeams =

    /// `spawn`, where the environment arrives as the JavaScript object it already IS and the
    /// child is handed that object.
    ///
    /// `SpawnOptions.Env` cannot express this. A `Map<string, string>` round trip RE-ENCODES
    /// an environment, and a re-encoding is not what a seam like the agent SDK's
    /// `spawnClaudeCodeProcess` promises: it hands a spawner `options.env` and the contract is
    /// that the child sees exactly that object. What a round trip would quietly drop — a value
    /// that is not a string, a key this process's own reader does not admit — is precisely
    /// what "verbatim" is there to protect.
    ///
    /// Everything else is `SpawnOptions`' story, spelled the same way here — including what it
    /// says about the streams of the `ChildProcess` that comes back.
    let spawnWithEnv
        (command: string)
        (arguments: string list)
        (env: obj)
        (cwd: string option)
        (stdio: Stdio)
        (detached: bool)
        : ChildProcess =
        let js =
            !!{| cwd = cwd
                 env = env
                 stdio = stdio
                 detached = detached |}

        Node.Api.childProcess.spawn (command, ResizeArray arguments, js)
