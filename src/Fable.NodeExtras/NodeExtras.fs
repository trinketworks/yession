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
// sibling already referenced) genuinely lacks it. `Errors.errno` is the one member here that
// is not a binding at all: `Fable.Node` types `ErrnoException` and stops, and what two callers
// were each writing out beside it — a throw that handed over something that is not an object,
// an errno spelled `''` — is a decision, so it is taken once. What Fable.Node already covers and nothing
// here re-declares: `node:events`, `randomBytes` from `node:crypto`, and `Buffer`'s
// `from`/`toString`/`concat`/`alloc`. `createHash`/`createHmac` it covers but for the one
// thing asked of them: a digest in a named encoding comes back typed `obj`, so the text a
// caller wanted is exactly what cannot be read through it — `Digests` below types that.
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

// --- The errno on a thrown error ----------------------------------------------------------

/// What a caller can ask about an error Node threw.
[<RequireQualifiedAccess>]
module Errors =

    /// The errno this error carries (`ENOENT`, `EACCES`, `EPERM`), or nothing for a failure
    /// that is not one of Node's.
    ///
    /// `Fable.Node` types the shape — `ErrnoException` — and stops there, which leaves the
    /// two things a reader has to get right written out at each call site instead: that a
    /// `throw` can hand over something that is not an object at all, and that an errno spelled
    /// `''` names no more of a fault than a missing one does. Both were being said with
    /// `($0.code || undefined)`, a macro that folds them together by accident rather than by
    /// decision, and folds `0` and `false` in with them for good measure.
    ///
    /// An option rather than `""`, because the two absences — a throw with no error at all,
    /// and an error naming no errno — are both "this fault has no errno", and a reader that
    /// has to tell an errno from a hole must be given one it cannot mistake.
    let errno (error: exn) : string option =
        if isNullOrUndefined (box error) then
            None
        else
            match (unbox<Node.Base.ErrnoException> (box error)).code with
            | None | Some "" -> None
            | code -> code

// --- The synchronous file calls a durable append is made of -------------------------------

/// What `node:fs` offers a writer that must be durable before it answers, and `Fable.Node`
/// does not type: a descriptor opened for APPEND rather than truncation, a `writeSync` that
/// takes a string, the flush that makes a write durable, and `mkdirSync` with `recursive`.
///
/// Three stores and the Manager's shared file primitives had each written the same four
/// macros, with the same comment above them saying which four members were missing — four
/// copies of one binding, each invisible to the others. They are imports rather than emits,
/// which is what the members allow once the flag and the options object are F# values.
/// One entry of a directory read `withFileTypes`: its name, and what KIND of thing it is,
/// answered without a second call. That distinction is the point for a SYMLINK, whose `stat`
/// answers about the thing it points at — so a walk that must not follow one has to be told
/// by the read itself.
[<AllowNullLiteral>]
type DirectoryEntry =
    abstract name : string
    abstract isDirectory : unit -> bool
    abstract isSymbolicLink : unit -> bool

/// The options `readdirSync` is handed here: the one that makes it answer entries rather than
/// names. Built with `jsOptions`, so the name Node reads is spelled by the compiler.
[<AllowNullLiteral>]
type internal ReaddirOptions =
    abstract withFileTypes : bool with get, set

/// The options `mkdirSync` is handed here.
[<AllowNullLiteral>]
type internal MkdirOptions =
    abstract recursive : bool with get, set

/// The options `rmSync` is handed here.
[<AllowNullLiteral>]
type internal RmOptions =
    abstract recursive : bool with get, set
    abstract force : bool with get, set

[<AutoOpen>]
module Files =

    [<Import("openSync", "node:fs")>]
    let private openSyncWithFlag (path: string) (flag: string) : int = jsNative

    [<Import("mkdirSync", "node:fs")>]
    let private mkdirSyncWithOptions (path: string) (options: MkdirOptions) : unit = jsNative

    [<Import("readdirSync", "node:fs")>]
    let private readdirSyncWithOptions (path: string) (options: ReaddirOptions) : DirectoryEntry array = jsNative

    /// What `dir` holds, each entry saying what it is. `Fable.Node`'s `readdirSync` answers
    /// names alone, so a caller that needs the kinds either asks the filesystem again per
    /// name — which for a symlink answers about its target — or reads them here.
    let entries (dir: string) : DirectoryEntry array =
        readdirSyncWithOptions dir (jsOptions<ReaddirOptions> (fun o -> o.withFileTypes <- true))

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
    let mkdirp (path: string) : unit =
        mkdirSyncWithOptions path (jsOptions<MkdirOptions> (fun o -> o.recursive <- true))

    [<Import("rmSync", "node:fs")>]
    let private rmSyncWithOptions (path: string) (options: RmOptions) : unit = jsNative

    /// Remove a path and everything under it, and say nothing about one that was not there
    /// (`recursive` + `force`). `Fable.Node` types `rmdirSync` and `unlinkSync`, neither of
    /// which is this: one refuses a non-empty directory and the other refuses a directory.
    let removeTree (path: string) : unit =
        rmSyncWithOptions
            path
            (jsOptions<RmOptions> (fun o ->
                o.recursive <- true
                o.force <- true))

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

    /// Every header pair an answer carried, as `Headers` iterates them — names lowercased,
    /// and a header sent twice joined the way HTTP joins one.
    ///
    /// `Fable.Fetch`'s `Headers` types `get`, `has` and `getAll`, each of which takes the name
    /// in hand: they answer what a reader already knows to ask about. A reader that wants to
    /// know WHICH headers arrived — a test asserting on the whole answer, a relay copying it —
    /// has no member to call, because the iterator the object actually has is not declared.
    [<Emit("[...$0]")>]
    let headerPairs (headers: Fetch.Types.Headers) : (string * string)[] = jsNative

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

// --- What the bundler substituted -----------------------------------------------------------

/// Globals that exist only because `tasks.fsx stage` asked esbuild to define them when it
/// bundled the bins. Not Node's, strictly — but a bundle of this host is the only place they
/// are ever read, and a program that reaches for one is reaching for its platform.
[<RequireQualifiedAccess>]
module Bundle =

    /// `YESSION_BUILD_VERSION`, which esbuild replaces with a string literal (`--define`), or
    /// nothing when this code was not bundled at all. `typeof` guards the bare identifier, so an
    /// unbundled run cannot ReferenceError — esbuild substitutes inside the `typeof` too, which
    /// is harmless. What an absent version is CALLED is the reader's decision, not this
    /// binding's (`Yession.Host.Version`).
    [<Emit("typeof YESSION_BUILD_VERSION !== 'undefined' ? YESSION_BUILD_VERSION : undefined")>]
    let buildVersion : string option = jsNative

// --- This process ---------------------------------------------------------------------------

/// What `Fable.Node`'s `process` leaves out. Everything it types — `execPath`, `platform`,
/// `argv`, `stdin`, the listeners — is used through `Node.Api.process` directly.
[<RequireQualifiedAccess>]
module Processes =

    /// `process.kill(pid, signal)`: a signal to a process by id — or to a whole process group,
    /// which is what a NEGATIVE id names, and how a detached child's tree is taken with one.
    [<Emit("process.kill($0, $1)")>]
    let kill (pid: int) (signal: string) : unit = jsNative

    /// `process.exit(code)`, typed as the call it IS: one that does not come back. `Fable.Node`
    /// types it `unit`, which is true of an exit written as a statement and useless to the
    /// caller that has to answer with a value it will never produce — a command line refused
    /// halfway down a boot, which is every `abort` in this repository. Answering `'a` is what
    /// keeps those from being a `failwith` that Fable's async turns into an unhandled
    /// rejection reading `[object Object]`.
    [<Emit("process.exit($0)")>]
    let exitWith (code: int) : 'a = jsNative

    /// The code this process WILL exit with when it ends — `process.exitCode`, which is not
    /// an exit: it says how the ending should come out, and the ending still has to arrive.
    /// That is the difference that makes it the only thing an `exit` listener can change,
    /// because by then the exit is already happening.
    ///
    /// Unset reads as 0, which is what Node does with it: a process that sets nothing and
    /// runs out of work exits 0. So this answers the same 0 rather than an `option` whose
    /// `None` would mean the identical thing.
    [<Emit("process.exitCode ?? 0")>]
    let exitCode () : int = jsNative

    /// Say how this process should end, without ending it.
    [<Emit("process.exitCode = $0")>]
    let setExitCode (code: int) : unit = jsNative

    /// The two endings a Node process has, as something to be told about.
    ///
    /// `"beforeExit"` is the loop running out of work — nothing left to do, and nobody said
    /// to stop. `"exit"` is the ending itself, however it was reached, including an explicit
    /// `process.exit`, which skips `"beforeExit"` entirely. A listener on either runs
    /// synchronously: scheduling work from one is scheduling work for a process that is
    /// already leaving.
    [<Emit("process.on($0, $1)")>]
    let onEnding (event: string) (listener: unit -> unit) : unit = jsNative

// --- Child processes ------------------------------------------------------------------------

/// What ONE of a child's standard streams is wired to. `spawnWithEnv` at the end of this file
/// takes a single one for all three, as Node's own shorthand does; `StreamWiring` below is the
/// form for a caller whose three streams differ.
[<StringEnum>]
type Stdio =
    /// A pipe, read through `stdout`/`stderr` or written through `stdin`. Node's own default,
    /// stated rather than assumed.
    | Pipe
    /// The parent's own handle — the child writes where this process writes.
    | Inherit
    /// `/dev/null` in both directions.
    | Ignore

/// Where each of a child's three streams goes, named one at a time. For the caller that does
/// not mean the same thing about all three — one that wants the child's answer and silence
/// from its complaints, or one that parses what it pipes and passes the rest through.
type StreamWiring =
    { Stdin : Stdio
      Stdout : Stdio
      Stderr : Stdio }

module StreamWiring =

    /// Node reads the three POSITIONALLY, in this order. Written once, beside the record,
    /// rather than at each option record that carries one: the order is a fact about Node, and
    /// a second copy of it is a second chance to put stderr where stdout goes.
    let internal toJs (streams: StreamWiring) : Stdio array =
        [| streams.Stdin; streams.Stdout; streams.Stderr |]

/// What environment a child is given — in both of the meanings a caller can hold, each named,
/// because Node has only one of them and it is not the one usually meant.
///
/// Node REPLACES: a child handed an `env` sees that object and nothing else, `PATH` included.
/// So a caller that meant to say two things about a child, and wrote the two things, gets a
/// child that cannot find its own executable — and the failure names neither the variable nor
/// the option. The fault is invisible at the call site, because both meanings are spelled the
/// same way there: a map of the names the caller cares about.
///
/// Hence no default. A caller says which meaning it holds, and the merge — the half that is
/// easy to get wrong, and that was written out by hand at more than one call site — happens
/// once, below.
[<RequireQualifiedAccess>]
type ChildEnv =
    /// This process's environment, with these names written over it. What a caller that wants
    /// to tell a child two things means, and what it would otherwise have to spell out an
    /// entire environment to say.
    | Adding of Map<string, string>
    /// These names and NOTHING else — Node's own meaning for `env`. What a caller building a
    /// child's environment from scratch wants: a fixture that pins every variable its child
    /// may see, or a child launched by absolute path that needs none.
    | Replacing of Map<string, string>

/// A child's environment COMPLETE, as the JavaScript object Node reads one from: names to
/// values, and nothing else in the child's view. Opaque on purpose — what it holds was decided
/// by whoever built it, and the only thing done with one here is hand it to a child VERBATIM.
///
/// Where `ChildEnv` is what a caller MEANS, this is what Node is handed: `ChildEnv` resolves to
/// one, and a seam that receives an environment already built (the agent SDK's spawn request)
/// carries one through untouched rather than re-encoding it (see `spawnWithEnv`).
type VerbatimEnv =
    interface end

[<RequireQualifiedAccess>]
module VerbatimEnv =

    /// These names and nothing else. The one cast is here: the object is built name by name,
    /// so the names are dynamic and no declared shape can spell them.
    let ofMap (names: Map<string, string>) : VerbatimEnv =
        unbox (createObj [ for name, value in Map.toList names -> name ==> value ])

    /// This process's environment with `names` written over it, as a FRESH object: a child's
    /// environment is a copy, and this process's is not something a spawn may edit on the way
    /// past.
    let internal overProcess (names: Map<string, string>) : VerbatimEnv =
        unbox (JS.Constructors.Object.assign (createObj [], Node.Api.``process``.env, ofMap names))

module ChildEnv =

    /// The `env` to hand Node, or NOTHING — which is how "unchanged" is said, since Node's own
    /// default for an absent `env` is this process's environment entire. `Adding` nothing is
    /// exactly that, so it is said that way rather than by copying an environment to no end.
    let internal toJs (env: ChildEnv) : VerbatimEnv option =
        match env with
        | ChildEnv.Replacing names -> Some (VerbatimEnv.ofMap names)
        | ChildEnv.Adding names when Map.isEmpty names -> None
        | ChildEnv.Adding names -> Some (VerbatimEnv.overProcess names)

/// The object `child_process.spawn` reads its options from. `Fable.Node` types it as `obj`, so
/// it is declared here and filled through `jsOptions`: a field nobody set is ABSENT, which is
/// how Node is told to use its own default.
[<AllowNullLiteral>]
type internal NodeSpawnOptions =
    abstract cwd : string with get, set
    abstract env : VerbatimEnv with get, set
    /// Positional: stdin, stdout, stderr.
    abstract stdio : Stdio array with get, set
    abstract detached : bool with get, set

module internal NodeSpawnOptions =

    [<Import("spawn", "node:child_process")>]
    let private spawnWith (command: string) (arguments: string array) (options: NodeSpawnOptions) : ChildProcess =
        jsNative

    /// Spawn with the options built from these four answers. The one place both the F# values
    /// and the object Node reads are in view.
    let spawn
        (command: string)
        (arguments: string list)
        (cwd: string option)
        (env: VerbatimEnv option)
        (stdio: Stdio array)
        (detached: bool)
        : ChildProcess =
        let options =
            jsOptions<NodeSpawnOptions> (fun o ->
                cwd |> Option.iter (fun cwd -> o.cwd <- cwd)
                env |> Option.iter (fun env -> o.env <- env)
                o.stdio <- stdio
                o.detached <- detached)

        spawnWith command (Array.ofList arguments) options

/// The options `child_process.spawn` takes, which `Fable.Node` types as `obj` — so `cwd`,
/// `env`, `stdio` and `detached` get no checking at all, and a misspelled one is silently a
/// property Node never reads.
type SpawnOptions =
    { /// Where the child starts. `None` inherits this process's directory.
      Cwd : string option
      /// The child's environment, in whichever of `ChildEnv`'s two meanings the caller holds.
      Env : ChildEnv
      /// Where each of the child's three streams goes. Named one at a time rather than as
      /// Node's uniform shorthand, because a caller whose streams differ — parse one, pass
      /// another straight through — would otherwise have no way to say so.
      Streams : StreamWiring
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
        NodeSpawnOptions.spawn
            command
            arguments
            options.Cwd
            (ChildEnv.toJs options.Env)
            (StreamWiring.toJs options.Streams)
            options.Detached

// --- Synchronous children -------------------------------------------------------------------

/// What a synchronous child answered, whole. `spawnSync` runs the child to completion and
/// hands back everything it said at once, which is what a fixture wants: no streams to drain
/// and no exit to await. `Fable.Node` types the result as `obj`.
[<AllowNullLiteral>]
type SyncResult =

    /// The code the child exited with, and NOTHING for one a SIGNAL killed — Node reports
    /// `null` there, and reading that as `0` would have a killed child report success.
    abstract status : int option

    /// What the child wrote to stdout, decoded. `None` for a child that could not be spawned
    /// at all, which is a different answer from the empty string a child that ran and said
    /// nothing writes.
    abstract stdout : string option

    /// What the child wrote to stderr, on the same terms.
    abstract stderr : string option

/// The options a synchronous child is given here. `encoding` is not among them because this
/// binding always asks for text: `stdout` and `stderr` above are typed as strings, and a run
/// that did not name an encoding would hand back Buffers under those names.
type SyncOptions =
    { /// Text handed to the child on stdin, which is closed after. `None` gives it none.
      Input : string option
      /// How much output to keep, in bytes. Node's own default is 1 MiB and a child that
      /// exceeds it is KILLED with its output truncated — so a caller that expects a large
      /// answer says how large, rather than discovering the limit as a mysterious kill.
      MaxBuffer : int option
      /// Where the child starts. `None` inherits this process's directory.
      Cwd : string option
      /// The child's environment, in whichever of `ChildEnv`'s two meanings the caller holds.
      /// A fixture usually holds `Adding`: one pinning `GIT_CONFIG_GLOBAL` wants git to go on
      /// finding a `PATH`, and spelling a whole environment out to add two names is how one
      /// gets dropped.
      Env : ChildEnv
      /// Where the child's streams go. `None` leaves Node's own default, which pipes them.
      Streams : StreamWiring option }

/// Nothing named: a child run as it comes, inheriting this process's directory and
/// environment, given no stdin and answering within Node's own limit. What a caller wants a
/// FIELD of is then the one it writes down, and the rest read as "unchanged" rather than as
/// four decisions it had to make.
module SyncOptions =
    let none : SyncOptions =
        { Input = None; MaxBuffer = None; Cwd = None; Env = ChildEnv.Adding Map.empty; Streams = None }

/// The object the synchronous `child_process` calls read their options from, as this binding
/// fills it. `encoding` is always `utf8` — see `SyncOptions`.
[<AllowNullLiteral>]
type internal NodeSyncOptions =
    abstract encoding : BufferEncoding with get, set
    abstract input : string with get, set
    abstract maxBuffer : int with get, set
    abstract cwd : string with get, set
    abstract env : VerbatimEnv with get, set
    abstract stdio : Stdio array with get, set

[<AutoOpen>]
module SyncChildProcesses =

    [<Import("spawnSync", "node:child_process")>]
    let private spawnSyncWith (command: string) (arguments: string array) (options: NodeSyncOptions) : SyncResult =
        jsNative

    [<Import("execFileSync", "node:child_process")>]
    let private execFileSyncWith (command: string) (arguments: string array) (options: NodeSyncOptions) : string =
        jsNative

    [<Import("execSync", "node:child_process")>]
    let private execSyncWith (line: string) (options: NodeSyncOptions) : string = jsNative

    /// The shape Node reads, built once. What the environment MEANS is `ChildEnv`'s to say and
    /// the caller's to choose, so all that is left here is naming the fields Node reads them
    /// under — which is the one thing `Fable.Node`'s `obj` cannot check. A field left `None`
    /// is left ABSENT, which is how Node is told to use its own default.
    let private toJs (options: SyncOptions) : NodeSyncOptions =
        jsOptions<NodeSyncOptions> (fun o ->
            o.encoding <- BufferEncoding.Utf8
            options.Input |> Option.iter (fun input -> o.input <- input)
            options.MaxBuffer |> Option.iter (fun bytes -> o.maxBuffer <- bytes)
            options.Cwd |> Option.iter (fun cwd -> o.cwd <- cwd)
            ChildEnv.toJs options.Env |> Option.iter (fun env -> o.env <- env)
            options.Streams |> Option.iter (fun streams -> o.stdio <- StreamWiring.toJs streams))

    /// Run `command` to completion and answer everything it said. A child that FAILED is not
    /// an exception here: `status` carries what it exited with, and a caller reads it.
    let spawnSync (command: string) (arguments: string list) (options: SyncOptions) : SyncResult =
        spawnSyncWith command (Array.ofList arguments) (toJs options)

    /// Run `command` with `arguments` and answer what it wrote, THROWING if it failed. The
    /// opposite of `spawnSync` on exactly that point, and the shape a fixture's setup wants:
    /// a step that did not happen must not read as one that did.
    ///
    /// The arguments go as a list, never a line — so nothing in them is a shell's business,
    /// whatever characters a path or a message happens to contain.
    let execFileSync (command: string) (arguments: string list) (options: SyncOptions) : string =
        execFileSyncWith command (Array.ofList arguments) (toJs options)

    /// Run a SHELL line and answer what it wrote, throwing if it failed. For a caller whose
    /// command is a line — a pipeline, a redirect, something read from configuration — where
    /// `execFileSync` above is for one that knows its arguments.
    let execSync (line: string) (options: SyncOptions) : string =
        execSyncWith line (toJs options)

// --- Asynchronous children, answered whole ---------------------------------------------------

/// The options an `execFile` child is given here. Text back, always, for `SyncOptions`'
/// reason: `ExecResult` says it holds strings.
type ExecOptions =
    { /// Where the child starts. `None` inherits this process's directory.
      Cwd : string option
      /// The child's environment, in whichever of `ChildEnv`'s two meanings the caller holds.
      Env : ChildEnv
      /// How much output to keep, in bytes — past Node's own 1 MiB default a child is KILLED
      /// with its output truncated, so a caller expecting a large answer says how large.
      MaxBuffer : int option
      /// How long, in milliseconds, before the child is sent `KillSignal`. `None` is no limit.
      Timeout : int option
      /// What a timeout sends. `None` is Node's own default, `SIGTERM`.
      KillSignal : string option }

/// What an `execFile` child answered, whole. Qualified, because the three names are the
/// obvious ones for any record of a run, and a caller's own would otherwise capture this one's
/// constructions or be captured by them.
[<RequireQualifiedAccess>]
type ExecResult =
    { /// The code the child exited with — `0` for one that succeeded — and NOTHING for one
      /// that never started (`ENOENT`) or that a signal ended, a timeout's included: neither
      /// chose a status, and reading theirs as a number would be inventing one.
      Status : int option
      /// What the child wrote to stdout, decoded. `None` where Node handed over nothing.
      Stdout : string option
      /// What the child wrote to stderr, on the same terms.
      Stderr : string option }

/// The object `execFile` reads its options from, as this binding fills it.
[<AllowNullLiteral>]
type internal NodeExecOptions =
    abstract encoding : BufferEncoding with get, set
    abstract cwd : string with get, set
    abstract env : VerbatimEnv with get, set
    abstract maxBuffer : int with get, set
    abstract timeout : int with get, set
    abstract killSignal : string with get, set

/// What `execFile` hands its callback for a child that did not exit 0. `code` is the exit
/// STATUS where the child ran and chose it, an errno string (`ENOENT`) where it could never be
/// started, and absent where a signal ended it.
[<AllowNullLiteral>]
type internal ExecFailure =
    abstract code : U2<int, string> option

[<AutoOpen>]
module AsyncChildProcesses =

    [<Import("execFile", "node:child_process")>]
    let private execFileWith
        (command: string)
        (arguments: string array)
        (options: NodeExecOptions)
        (completed: System.Action<ExecFailure, string, string>)
        : ChildProcess =
        jsNative

    let private toJs (options: ExecOptions) : NodeExecOptions =
        jsOptions<NodeExecOptions> (fun o ->
            o.encoding <- BufferEncoding.Utf8
            options.Cwd |> Option.iter (fun cwd -> o.cwd <- cwd)
            ChildEnv.toJs options.Env |> Option.iter (fun env -> o.env <- env)
            options.MaxBuffer |> Option.iter (fun bytes -> o.maxBuffer <- bytes)
            options.Timeout |> Option.iter (fun ms -> o.timeout <- ms)
            options.KillSignal |> Option.iter (fun signal -> o.killSignal <- signal))

    let private statusOf (failure: ExecFailure) : int option =
        if isNull failure then Some 0
        else
            match failure.code with
            | Some (U2.Case1 status) -> Some status
            | _ -> None

    /// Run `command` with `arguments` to completion WITHOUT holding the event loop, and answer
    /// everything it said. The asynchronous twin of `spawnSync`, for a child whose answer
    /// depends on something this same process is serving — a synchronous run would hold the
    /// loop while the child waited for an answer that could then never come. A child that
    /// FAILED is not an exception: `Status` says how it ended.
    let execFile (command: string) (arguments: string list) (options: ExecOptions) : Async<ExecResult> =
        Async.FromContinuations (fun (cont, _, _) ->
            execFileWith
                command
                (Array.ofList arguments)
                (toJs options)
                (System.Action<ExecFailure, string, string> (fun failure out err ->
                    cont
                        { ExecResult.Status = statusOf failure
                          ExecResult.Stdout = Option.ofObj out
                          ExecResult.Stderr = Option.ofObj err }))
            |> ignore)

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

// --- node:crypto: hashes and HMACs, digested as text ------------------------------------------

/// How a digest is written out as text — Node's own name for the set. A `[<StringEnum>]`, so
/// each case is erased to the literal Node takes (`hex`, `base64`, `base64url`); `binary` is
/// the fourth, and nothing here wants it.
///
/// Qualified access because `Node.Buffer.BufferEncoding` has a `Hex` and a `Base64` of its own,
/// and a file opening both would otherwise have its bare cases re-pointed at this one.
[<StringEnum>]
[<RequireQualifiedAccess>]
type BinaryToTextEncoding =
    | Hex
    | Base64
    | Base64url

/// A hash or an HMAC being fed. `update` answers the same object, so a chain reads as one
/// expression; `digest` ends it, and Node throws on a second.
///
/// `Fable.Node` types the same object, and answers a digest in a named encoding as `obj` —
/// which is `string` whenever an encoding is named, and a `Buffer` only when none is. Typing
/// that here is what keeps a cast out of every caller.
[<AllowNullLiteral>]
type Digester =
    abstract update : data: string * inputEncoding: BufferEncoding -> Digester
    abstract update : data: Buffer -> Digester
    abstract digest : encoding: BinaryToTextEncoding -> string

[<RequireQualifiedAccess>]
module Digests =

    /// `createHash(algorithm)` — `"sha256"`, say.
    [<Import("createHash", "node:crypto")>]
    let hash (algorithm: string) : Digester = jsNative

    /// `createHmac(algorithm, key)`, keyed by the key's UTF-8 bytes.
    [<Import("createHmac", "node:crypto")>]
    let hmac (algorithm: string) (key: string) : Digester = jsNative

// --- The module a call is written in ----------------------------------------------------------

/// `import.meta`, which no binding types because it is syntax rather than a value.
[<RequireQualifiedAccess>]
module ImportMeta =

    /// `import.meta.url`: the address of the module this CALL is compiled into — not this
    /// file's. An emit is pasted into its caller, which for every other binding here is the
    /// hazard the header describes and for this one is the whole point: the answer is about the
    /// caller, so it has to be spelled where the caller is. Once esbuild has flattened a bin into
    /// one file, that is the bundle's own address.
    [<Emit("import.meta.url")>]
    let url () : string = jsNative

/// `node:url`'s conversions between a `file:` URL and a path.
[<RequireQualifiedAccess>]
module FileUrls =

    /// `fileURLToPath(url)` — the platform path a `file:` URL names. THROWS on any other scheme.
    [<Import("fileURLToPath", "node:url")>]
    let toPath (url: Node.Url.URL) : string = jsNative

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
    /// the message is empty; an F# exception is its message too; text is itself; a number or a
    /// boolean is its digits or its word; nothing at all says so; anything else is its JSON,
    /// which is at least the value, or — for the values JSON has no text for, a function or a
    /// symbol — says that much.
    ///
    /// The F# case is its own because Fable's `Exception` is deliberately NOT an `Error` (it
    /// skips the stack capture), so `isError` answers no for `raise (exn "…")` and it used to
    /// fall through to the JSON — `{"message":"…"}` where a sentence wanted the message.
    let describe (thrown: obj) : string =
        match thrown with
        | null -> "nothing"
        | :? string as text -> text
        | :? float as number -> string number
        | :? bool as flag -> if flag then "true" else "false"
        | error when isError error ->
            let error = unbox<JsError> error
            if System.String.IsNullOrEmpty error.message then error.name else error.message
        | :? exn as error when not (System.String.IsNullOrEmpty error.Message) -> error.Message
        | value ->
            match (try JS.JSON.stringify value with _ -> null) with
            | null -> "a value with no text"
            | json -> json

    /// `new Error(message)` — the platform's own error, not F#'s `exn`, which Fable compiles
    /// to a class of its own that is NOT `instanceof Error`. Only this one is what a listener
    /// written in JavaScript expects to have its hands on.
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

    [<Emit("new Error($0)")>]
    let private withMessage (message: string) : StreamError = jsNative

    /// Whatever was thrown, as the `Error` a listener registered on `error` is written
    /// against. JavaScript admits a `throw` of any value at all, and an `error` event
    /// carrying a string is how a handler reading `.message` gets `undefined` instead of a
    /// reason. An `Error` goes on as itself — the cast is to the one property it was just
    /// asked to have; anything else becomes one that describes it.
    let ofThrown (thrown: exn) : StreamError =
        if Thrown.isError (box thrown) then unbox thrown else withMessage (Thrown.describe (box thrown))

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

    /// Write TEXT, encoded as UTF-8 — what a line protocol over a pipe writes, where the
    /// bytes are the encoding of the text by definition. Answers what `writeBytes` answers.
    [<Emit("$0.write($1)")>]
    abstract writeText : text: string -> bool

    /// `end()`: no more is coming. The reader sees the stream END — for a child's stdin, the
    /// same end of input it sees when the writer dies, which is what makes it the polite
    /// half of one protocol rather than a second one.
    [<Emit("$0.end()")>]
    abstract finish : unit -> unit

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

[<AutoOpen>]
module FileStreams =

    /// A file as a stream to pipe somewhere — a response, usually. `Fable.Node` types
    /// `createReadStream` as its own stream class, which is not the `Readable` everything that
    /// moves bytes in this repository speaks, and the conversion would be an `unbox` at every
    /// call site saying nothing about why it is safe.
    ///
    /// Streamed and not read whole because the files this serves are allowed to be 100 MB: a
    /// `readFileSync` per request would hold the whole of one in this process's heap for as
    /// long as the slowest viewer's connection lasts, and several at once is the session out
    /// of memory. Piped, Node moves the bytes with backpressure and nothing here holds them.
    ///
    /// A file that cannot be opened fails on the stream's `error` event, not here — so a
    /// caller pipes only after it has written a head it is willing to stand behind, or
    /// destroys the response when it cannot.
    [<Import("createReadStream", "node:fs")>]
    let openFileStream (path: string) : Readable = jsNative

/// The connection a message arrived on: the one thing read off it is who is at the other end.
[<AllowNullLiteral>]
type Socket =
    /// The peer's address, and nothing once the socket has been torn down.
    abstract remoteAddress : string option

    /// The connection is gone, whichever end let go of it. What a server watches to learn that
    /// the far side has dropped a request it was holding open — the honest observation of that,
    /// where anything on the client's side would only say what the client thinks.
    [<Emit("$0.on('close', $1)")>]
    abstract onClose : handler: (unit -> unit) -> unit

/// One header's value, as HTTP carries it: said once, or said again. A proxy that narrowed
/// it to `string` would silently drop the second `set-cookie`, and one that kept it `obj`
/// would hand every reader a `:?` to write — and the reader that wrote `unbox<string>`
/// instead got an array that claimed to be text.
[<RequireQualifiedAccess>]
type HeaderValue =
    | Single of string
    /// A header that repeated: every value, in the order it arrived.
    | Repeated of string[]

[<RequireQualifiedAccess>]
module HeaderValue =

    /// Node's own shape for one: a string, or an array of them. What a request's options and
    /// `writeHead` take, which is why `OutgoingHeaders.ofPairs` is the only caller.
    let internal wire (value: HeaderValue) : obj =
        match value with
        | HeaderValue.Single text -> box text
        | HeaderValue.Repeated values -> box values

/// The headers object Node takes on a message this process SENDS: `writeHead`'s, and a
/// request's options. Opaque, and made only by `OutgoingHeaders.ofPairs`: from typed headers,
/// or from an upstream's exactly as they arrived.
type OutgoingHeaders =
    interface end

module internal OutgoingHeaders =

    /// The object, name by name. The names are data — a proxy relays ones it has no case for —
    /// so no declared shape can spell them, and the one cast is here.
    let ofPairs (pairs: (string * HeaderValue) seq) : OutgoingHeaders =
        unbox (createObj [ for name, value in pairs -> name, HeaderValue.wire value ])

/// A message that ARRIVED over HTTP — its headers, and its body as the stream it is. Both
/// halves of an exchange are one of these on the receiving side, which is why the shape is
/// shared rather than written twice.
[<AllowNullLiteral>]
type HttpMessage =
    inherit Readable

    /// The socket it arrived on — nothing once the connection is gone, which a message read
    /// after its peer disconnected can be.
    abstract socket : Socket option

[<AutoOpen>]
module HttpMessageHeaders =

    [<Emit("Object.entries($0.headers)")>]
    let private entries (message: HttpMessage) : (string * obj)[] = jsNative

    /// Node's typings admit three shapes for a value — a string, an array of them, and
    /// `undefined` — and only the first two are a header that arrived. The third answers
    /// `None`, and the pair is dropped: a header with no value is not one to carry.
    let private decode (value: obj) : HeaderValue option =
        match value with
        | :? string as text -> Some (HeaderValue.Single text)
        | :? (string[]) as values -> Some (HeaderValue.Repeated values)
        | _ -> None

    type HttpMessage with

        /// Every header that arrived, as `name, value` pairs — Node LOWERCASES the names on
        /// the way in, so a caller comparing them compares lowercase.
        ///
        /// Pairs rather than the object itself so that deciding WHICH headers to carry is F#
        /// a test can run, instead of an `Object.entries` loop inside an emit; and each value
        /// decoded HERE, once, so a caller pattern-matches a `HeaderValue` instead of asking
        /// JavaScript what it was handed.
        member this.headerEntries () : (string * HeaderValue)[] =
            entries this
            |> Array.choose (fun (name, value) -> decode value |> Option.map (fun decoded -> name, decoded))

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

    /// The child's input as the stream bytes are piped INTO. `Fable.Node` types it as a
    /// `Writable<string>`, which is not the `Writable` a `pipe` here ends at.
    [<Emit("$0.stdin")>]
    let stdin (child: ChildProcess) : Writable = jsNative

    /// A spawn that failed before exec, or a child that could not be signalled: the
    /// platform's own `Error`, for `StreamError.describe` to read.
    [<Emit("$0.on('error', $1)")>]
    let onError (child: ChildProcess) (handler: StreamError -> unit) : unit = jsNative

    /// The child ended and its stdio closed. `None` is a child a signal took, which Node
    /// reports as a `null` code; what to say about that is the caller's.
    [<Emit("$0.on('close', $1)")>]
    let onClose (child: ChildProcess) (handler: int option -> unit) : unit = jsNative

    /// The child ENDED — which is earlier than `onClose` above, and the difference matters to
    /// whoever is still reading: exit fires when the process is gone, close once the pipes it
    /// was writing into have drained as well. A caller waiting to hear that no more output is
    /// coming wants `onClose`; one waiting to hear that the PROCESS is over — a launch that
    /// must fail the moment its child dies, rather than once a stream it may never have
    /// written to finishes — wants this. `None` is a child a signal took, on the same terms
    /// as `onClose`.
    [<Emit("$0.on('exit', $1)")>]
    let onExit (child: ChildProcess) (handler: int option -> unit) : unit = jsNative

/// This process's own standard input, as the stream this project reads — `Fable.Node` types
/// it as its own socket class, which `Readables.text` cannot be handed.
[<RequireQualifiedAccess>]
module ProcessStreams =

    [<Emit("process.stdin")>]
    let stdin () : Readable = jsNative

/// callback `httpRequest` took.
[<AllowNullLiteral>]
type HttpRequest =
    inherit Writable

/// What `http(s).request` is handed here beside the URL: the method, as the caller spelled it,
/// and the headers.
[<AllowNullLiteral>]
type internal ClientRequestOptions =
    abstract ``method`` : string with get, set
    abstract headers : OutgoingHeaders with get, set

[<AutoOpen>]
module HttpClient =

    // Node splits its client across two modules by scheme, and the two take the same
    // arguments — so the pair is imported once here and `httpRequest` below is the only
    // place that has to know there are two.
    [<Import("request", "node:http")>]
    let private overHttp (url: string) (options: ClientRequestOptions) (onResponse: HttpResponse -> unit) : HttpRequest =
        jsNative

    [<Import("request", "node:https")>]
    let private overHttps (url: string) (options: ClientRequestOptions) (onResponse: HttpResponse -> unit) : HttpRequest =
        jsNative

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
        (headers: (string * HeaderValue)[])
        (onResponse: HttpResponse -> unit)
        : HttpRequest =
        let options =
            jsOptions<ClientRequestOptions> (fun o ->
                o.``method`` <- ``method``
                o.headers <- OutgoingHeaders.ofPairs headers)

        if url.StartsWith "https:" then overHttps url options onResponse else overHttp url options onResponse

// --- node:http, answering ---------------------------------------------------------------------

/// One header on a response this host writes. Closed, so each name is spelled once — here —
/// rather than as a string at every call site, where `"cache-contol"` is a header nobody reads
/// and nothing says so.
[<RequireQualifiedAccess>]
type ResponseHeader =
    | CacheControl of string
    | Connection of string
    | ContentDisposition of string
    | ContentLength of int64
    | ContentSecurityPolicy of string
    | ContentType of string
    /// `x-content-type-options`.
    | ContentTypeOptions of string
    | ETag of string
    | Location of string
    | SetCookie of string

[<RequireQualifiedAccess>]
module ResponseHeader =

    /// The name on the wire. Lowercase, which is what Node answers a request's with too.
    let name (header: ResponseHeader) : string =
        match header with
        | ResponseHeader.CacheControl _ -> "cache-control"
        | ResponseHeader.Connection _ -> "connection"
        | ResponseHeader.ContentDisposition _ -> "content-disposition"
        | ResponseHeader.ContentLength _ -> "content-length"
        | ResponseHeader.ContentSecurityPolicy _ -> "content-security-policy"
        | ResponseHeader.ContentType _ -> "content-type"
        | ResponseHeader.ContentTypeOptions _ -> "x-content-type-options"
        | ResponseHeader.ETag _ -> "etag"
        | ResponseHeader.Location _ -> "location"
        | ResponseHeader.SetCookie _ -> "set-cookie"

    let value (header: ResponseHeader) : string =
        match header with
        | ResponseHeader.CacheControl v
        | ResponseHeader.Connection v
        | ResponseHeader.ContentDisposition v
        | ResponseHeader.ContentSecurityPolicy v
        | ResponseHeader.ContentType v
        | ResponseHeader.ContentTypeOptions v
        | ResponseHeader.ETag v
        | ResponseHeader.Location v
        | ResponseHeader.SetCookie v -> v
        | ResponseHeader.ContentLength bytes -> string bytes

/// The response this process's server is writing. `Writable` because it is where an upstream
/// body is piped, and what gets destroyed when that body cannot finish.
///
/// `Fable.Node` types `writeHead`'s headers as `obj` and `end` as text or a Buffer; the head is
/// declared here so the headers are `ResponseHeader`s (see `writeHead` below), and both `end`s
/// so a body that is bytes goes out as bytes instead of being cast to a string on the way.
[<AllowNullLiteral>]
type ServerResponse =
    inherit Writable

    [<Emit("$0.writeHead($1, $2)")>]
    abstract writeHeadWith : statusCode: int * headers: OutgoingHeaders -> unit

    abstract write : text: string -> bool

    abstract ``end`` : text: string -> unit

    abstract ``end`` : bytes: Buffer -> unit

    /// Whether a head has gone out — what decides if an error can still be said on this
    /// response or has to close it.
    abstract headersSent : bool

[<AutoOpen>]
module ResponseHeads =

    type ServerResponse with

        /// Send the status line and these headers.
        member this.writeHead (statusCode: int, headers: ResponseHeader list) : unit =
            this.writeHeadWith (
                statusCode,
                OutgoingHeaders.ofPairs
                    [ for header in headers -> ResponseHeader.name header, HeaderValue.Single (ResponseHeader.value header) ]
            )

        /// Send the status line with an upstream's headers as they came (`headerEntries`,
        /// filtered): a proxy relays names it has no case for, and a value that repeated —
        /// a second `set-cookie` — goes out repeated.
        member this.relayHead (statusCode: int, headers: (string * HeaderValue)[]) : unit =
            this.writeHeadWith (statusCode, OutgoingHeaders.ofPairs headers)

        /// Send the status line with headers this process speaks but `ResponseHeader` has no
        /// case for — which is only ever a stand-in for somebody else's server (a test playing
        /// github.com's rate limit, or an MCP provider's session id). Product responses are
        /// ours to name, and go through `writeHead`.
        member this.writeNamedHead (statusCode: int, headers: (string * string) list) : unit =
            this.writeHeadWith (statusCode, OutgoingHeaders.ofPairs [ for name, value in headers -> name, HeaderValue.Single value ])

// --- The environment's outbound proxy ----------------------------------------------------------

[<AutoOpen>]
module Proxies =

    /// `http.setGlobalProxyFromEnv()` (Node 24.14): from here on, `fetch` and
    /// `http(s).request` go through the proxy `process.env` names (`HTTPS_PROXY`,
    /// `HTTP_PROXY`, past whatever `NO_PROXY` exempts), read at the moment of the call.
    /// Process-wide, because Node's global dispatcher is. Answers the function that puts the
    /// previous dispatcher back.
    [<Import("setGlobalProxyFromEnv", "node:http")>]
    let setGlobalProxyFromEnv () : (unit -> unit) = jsNative

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

/// A connection somebody made to a server listening here — the serving side of one, which is
/// all this repository holds. What a probe's listener does with a connection is say one thing
/// and let it go, so that is the whole of what is declared.
[<AllowNullLiteral>]
type NetConnection =

    /// Say this, then close. `end` rather than `write`: a probe's answer is complete when it
    /// is written, and leaving the connection open would have the far side waiting for more.
    [<Emit("$0.end($1)")>]
    abstract close : said: string -> unit

[<AllowNullLiteral>]
type NetServer =
    inherit Listening

    /// Bind, and call back once the OS has chosen. Port 0 is the whole point — asked for a
    /// particular port, a probe would be racing whoever else wanted that one.
    abstract listen : port: int * host: string * onListening: (unit -> unit) -> unit

    /// Bind a UNIX SOCKET at `path`. No callback, and no port to be told: the path is the
    /// address, chosen by the caller rather than by the OS, so there is nothing to learn
    /// once it is bound.
    abstract listen : path: string -> unit

    /// Stop listening, and call back once the socket is released — before which the port is
    /// still this process's, and a child told to bind it would be refused. Node hands this
    /// callback an error when the server was not open, which is not a case a probe can be in:
    /// it closes one server, once, having just watched it listen.
    abstract close : onClosed: (unit -> unit) -> unit

    /// Stop listening, without waiting to be told it has stopped. For a caller tearing a
    /// fixture down after its verdict is already in: Node THROWS here for a server that never
    /// bound or has already closed, so a caller that cannot know which state it is in catches
    /// rather than letting a fixture's fault replace the answer the case reached.
    abstract close : unit -> unit

[<AutoOpen>]
module NetServers =

    /// A server with no connection handler, because nothing ever connects to a probe: what it
    /// is for is holding a port long enough to be told which one it got.
    [<Import("createServer", "node:net")>]
    let createNetServer () : NetServer = jsNative

    /// A server that ANSWERS, for the probe whose question is whether a connection can be made
    /// at all: a refused connect and a denied connect are both failures, and only a successful
    /// one says a grant reached the kernel — which needs something on the other end to succeed
    /// against.
    [<Import("createServer", "node:net")>]
    let createNetServerAnswering (onConnection: NetConnection -> unit) : NetServer = jsNative

    /// The port a LISTENING server was given. Asked of one that is not listening, this is a
    /// fault at the caller — a port read outside the listen/close window is not a port — and
    /// says so rather than answering a number.
    let boundPort (server: #Listening) : int =
        match server.address () with
        | Some bound -> bound.port
        | None -> failwith "the server is not listening, so it has no port"

// --- A proxy's sockets: CONNECT, and TLS terminated here -------------------------------------

/// A connection as bytes both ways: the client's socket after a `CONNECT`, a socket this
/// process dialled, or the TLS stream laid over either. Declared as its own shape rather than
/// as `Readable` and `Writable` together, which would give it two `destroy`s and two
/// `onError`s and a call to either an ambiguity.
[<AllowNullLiteral>]
type Duplex =
    inherit Readable

    /// The same socket, as somewhere a `pipe` can end.
    [<Emit("$0")>]
    abstract sink : Writable

    /// Write text, encoded as UTF-8 — what a status line is.
    [<Emit("$0.write($1)")>]
    abstract writeText : text: string -> unit

    /// Put bytes already read back at the front of the stream, for whatever reads it next.
    /// What a `CONNECT`'s `head` is for: bytes the client sent after the request line, which
    /// belong to the tunnel and not to the proxy.
    abstract unshift : chunk: Buffer -> unit

    /// The first chunk and no other — for a reader that hands the stream on after it, as a
    /// client does once a proxy has answered its `CONNECT`.
    [<Emit("$0.once('data', $1)")>]
    abstract onceData : handler: (Buffer -> unit) -> unit

    /// Finish writing and close, once what was written has gone.
    [<Emit("$0.end()")>]
    abstract finish : unit -> unit

    /// A connection this process DIALLED is open, and what is written now reaches the peer.
    [<Emit("$0.once('connect', $1)")>]
    abstract onceConnect : handler: (unit -> unit) -> unit

/// A `CONNECT` as a server sees one: its target, in authority form (`host:port`), and the
/// headers it came with — a proxy that admits only some clients reads their
/// `proxy-authorization` there.
[<AllowNullLiteral>]
type ConnectRequest =
    inherit HttpMessage
    abstract url : string

/// What a `node:http` server offers a proxy beyond requests: the `CONNECT`s it would otherwise
/// refuse, and a connection handed to it from elsewhere — a TLS stream this process
/// terminated — to parse HTTP out of as though it had accepted it itself.
[<AllowNullLiteral>]
type Connectable =
    /// A `CONNECT` arrived. `head` is whatever the client sent past the request line, which
    /// is usually nothing: a client waits to be told the tunnel is open before it speaks.
    [<Emit("$0.on('connect', $1)")>]
    abstract onConnect : handler: System.Func<ConnectRequest, Duplex, Buffer, unit> -> unit

    /// Serve HTTP on a connection this server did not accept.
    [<Emit("$0.emit('connection', $1)")>]
    abstract serve : connection: Duplex -> unit

[<AutoOpen>]
module NetClients =

    /// Dial a UNIX socket. Writing may start at once: what is written before the connection
    /// opens is held until it does.
    [<Import("connect", "node:net")>]
    let connectPath (path: string) : Duplex = jsNative

    /// Dial a TCP port on a host, by name or address. Open once `onceConnect` fires; a host
    /// that cannot be reached is an `error` on the stream instead.
    [<Import("connect", "node:net")>]
    let connectTcp (port: int, host: string) : Duplex = jsNative

/// Where `node:tls` keeps the certificates it trusts: the Mozilla set Node was built with,
/// or what the operating system trusts — which is where a site's own root lives.
[<StringEnum; RequireQualifiedAccess>]
type CaStore =
    | Default
    | System

/// A key and the certificate that goes with it, ready to answer a handshake.
[<AllowNullLiteral>]
type SecureContext = interface end

[<AllowNullLiteral>]
type SecureContextOptions =
    /// PEM.
    abstract cert : string with get, set
    /// PEM.
    abstract key : string with get, set

[<AllowNullLiteral>]
type TlsServerSocketOptions =
    abstract isServer : bool with get, set
    abstract secureContext : SecureContext with get, set
    abstract ALPNProtocols : string array with get, set

[<AllowNullLiteral>]
type TlsClientOptions =
    /// The connection to speak TLS over, already open.
    abstract socket : Duplex with get, set
    /// The name the server's certificate has to carry.
    abstract servername : string with get, set
    /// PEMs to trust INSTEAD of Node's own set.
    abstract ca : string array with get, set

[<AllowNullLiteral>]
type TlsSocketClass =
    [<EmitConstructor>]
    abstract Create : socket: Duplex * options: TlsServerSocketOptions -> Duplex

[<RequireQualifiedAccess>]
module Tls =

    [<Import("createSecureContext", "node:tls")>]
    let secureContext (options: SecureContextOptions) : SecureContext = jsNative

    [<Import("TLSSocket", "node:tls")>]
    let private tlsSocket : TlsSocketClass = jsNative

    /// Answer TLS on `socket` as the server, with `context`'s certificate, and hand back the
    /// plaintext stream the handshake opens. HTTP/1.1 is the one protocol offered, because
    /// the plaintext is handed to a `node:http` server, which speaks nothing else.
    let terminate (socket: Duplex) (context: SecureContext) : Duplex =
        tlsSocket.Create (
            socket,
            jsOptions<TlsServerSocketOptions> (fun o ->
                o.isServer <- true
                o.secureContext <- context
                o.ALPNProtocols <- [| "http/1.1" |])
        )

    /// Speak TLS as the client over an open connection.
    [<Import("connect", "node:tls")>]
    let connect (options: TlsClientOptions) : Duplex = jsNative

    /// Every certificate one store trusts, as PEM.
    [<Import("getCACertificates", "node:tls")>]
    let caCertificates (store: CaStore) : string array = jsNative

// --- node:http, serving ------------------------------------------------------------------------
//
// The other half of "node:http, answering" above: the server a response is written from, and
// the request it answers. Declared here rather than beside `ServerResponse` because a server is
// `Listening`, which is only sayable once the section above has said it.

/// A request as it ARRIVED at this process's server. `HttpMessage` is where its headers and its
/// body-as-a-stream come from, stated as inheritance rather than re-declared here, because a
/// Node server's request and a Node client's response are the same received thing — and the
/// gateway pipes one straight into the other.
type IncomingMessage =
    inherit HttpMessage
    abstract url : string
    abstract ``method`` : string

    /// The request is over — answered, or its peer went away first. What a response that stays
    /// open (a server-sent event stream) waits for to let go of whatever it was feeding it from.
    /// A member rather than `on(name, handler)` for the reason `Readable`'s events are: the
    /// event's name and its handler's type are one fact.
    [<Emit("$0.on('close', $1)")>]
    abstract onClose : handler: (unit -> unit) -> unit

/// A `node:http` server, as this repository runs one: bound, asked its port, and closed.
type HttpServer =
    inherit Listening
    inherit Connectable

    /// Bind, and call back once the OS has chosen — port 0 asks it to choose. Node answers the
    /// server itself, for chaining.
    abstract listen : port: int * host: string * onListening: (unit -> unit) -> HttpServer

    /// Bind a UNIX socket at `path`, and call back once it is bound.
    abstract listen : path: string * onListening: (unit -> unit) -> HttpServer

    /// A bind that failed — the address taken, the directory gone — raises here, once, and an
    /// unhandled `error` on a server takes the PROCESS down; so a caller that binds listens
    /// for it first and fails with a sentence instead.
    [<Emit("$0.once('error', $1)")>]
    abstract onceError : handler: (StreamError -> unit) -> unit

    /// Stop accepting connections, and call back once every open one has ended. Node hands the
    /// callback an `Error` when the server was not listening and nothing when it closed, which
    /// is what the option says.
    abstract close : onClosed: (exn option -> unit) -> unit

[<AutoOpen>]
module HttpServers =

    [<Import("createServer", "node:http")>]
    let private createServerRaw (handler: System.Func<IncomingMessage, ServerResponse, unit>) : HttpServer = jsNative

    /// Create an HTTP server. The handler is passed as an uncurried delegate so Node receives a
    /// plain `(req, res) => ...` two-argument callback.
    let createServer (handler: IncomingMessage -> ServerResponse -> unit) : HttpServer =
        createServerRaw (System.Func<_, _, _> handler)

    /// The actual bound port (differs from the requested one when listening on 0).
    let serverPort (server: HttpServer) : int = boundPort server

// --- node:http, upgrading ----------------------------------------------------------------------
//
// What a server is handed when a request asks to stop being HTTP: the `upgrade` event, and the
// raw socket under it. Its own section rather than more of "serving" above, because a server
// that never upgrades never sees any of it, and the socket it hands over speaks no HTTP at all.

/// The connection under an accepted upgrade. From the 101 onwards nothing on it is HTTP, and
/// every byte is the caller's to frame.
///
/// Bytes go both ways as `byte []` rather than `Buffer`: Node's `write` takes a `Uint8Array`
/// and its `data` event hands over a `Buffer`, which is a `Uint8Array` subclass — and a
/// `Uint8Array` is what Fable compiles a `byte []` to. So a framer written in F# reads and
/// writes arrays, and nothing is converted in either direction.
[<AllowNullLiteral>]
type UpgradedSocket =
    inherit Socket

    /// Bytes onto the wire.
    abstract write : bytes: byte [] -> bool

    /// The polite end: what has already been written still goes out.
    abstract ``end`` : unit -> unit

    /// The rude one: the connection disappears with nothing said.
    abstract destroy : unit -> unit

    /// Every read, as it arrives.
    [<Emit("$0.on('data', $1)")>]
    abstract onData : handler: (byte [] -> unit) -> unit

    /// A peer that disappeared mid-write raises here, and an unhandled `error` on a socket
    /// takes the PROCESS down rather than the connection — so a caller that drops connections
    /// as a matter of course listens before it writes anything.
    [<Emit("$0.on('error', $1)")>]
    abstract onError : handler: (StreamError -> unit) -> unit

[<AutoOpen>]
module HttpUpgrades =

    [<Emit("$0.on('upgrade', $1)")>]
    let private onUpgradeRaw
        (server: HttpServer)
        (handler: System.Func<IncomingMessage, UpgradedSocket, unit>)
        : unit =
        jsNative

    /// `server.on('upgrade', …)`: the request that asked for it, and the socket under it. Node
    /// also hands over whatever bytes arrived past the head, which a peer never has from a
    /// client that speaks only after the 101 — as every WebSocket client does. The handler is
    /// an uncurried delegate so Node receives the callback it calls, the way `createServer`
    /// takes its own.
    let onUpgrade (server: HttpServer) (handler: IncomingMessage -> UpgradedSocket -> unit) : unit =
        onUpgradeRaw server (System.Func<_, _, _> handler)

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
/// The listening end is what the PRODUCT holds: the signals it sees arrive from somebody
/// else's API, where the agent SDK hands one to the spawner it is given. The firing end is
/// `AbortController` below — declared because the suites that prove a listening binding
/// listens need something to make it fire, and two of them had each written the same three
/// macros to get one.
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

/// The WHATWG `AbortController` — the firing end of the signal above, and a Node global since
/// v15. One controller owns one signal for its whole life: `signal` answers the same object
/// every time, which is what lets a caller hang a listener on it and abort through the
/// controller afterwards.
[<AllowNullLiteral>]
type AbortController =

    /// The signal this controller fires. The SAME signal on every read, not a fresh one.
    abstract signal : AbortSignal

    /// Fire it. At most once by the spec: a second `abort` on a controller that has already
    /// fired changes nothing and runs no listener.
    abstract abort : unit -> unit

[<AutoOpen>]
module Aborting =

    /// A fresh controller, its signal unfired.
    [<Emit("new AbortController()")>]
    let abortController () : AbortController = jsNative

// --- Relaying somebody else's listeners --------------------------------------------------------

/// A listener somebody ELSE wrote, held only to be handed on.
///
/// `Fable.Node`'s `EventEmitter` types a listener as an F# function of a definite arity, which
/// is right for a listener written here and wrong for one passed through: Fable adapts a
/// function whose arity it can see, and an adapted listener is a DIFFERENT function object —
/// so `off` would no longer match what `on` registered, and a two-argument listener hung on
/// `exit` would be handed one. Nor is there an arity to see, since it varies by event.
///
/// So a listener is opaque: it arrives as a JavaScript function and is handed on as that same
/// function, which is the only thing a relay may do with one. One written in F# is made by
/// `RelayListener.onExit` or `onError`, which say what each event carries.
type RelayListener =
    interface end

[<RequireQualifiedAccess>]
module RelayListener =

    /// A listener for `exit`: the code a process ended with, or the signal that ended it.
    /// A `Func` because the event hands both over at once, and a curried F# function would
    /// answer the first with a function rather than take both. The cast is to what it was
    /// just built as: a JavaScript function.
    let onExit (handler: int option -> string option -> unit) : RelayListener =
        unbox (System.Func<int option, string option, unit> handler)

    /// A listener for `error`.
    let onError (handler: StreamError -> unit) : RelayListener =
        unbox (System.Func<StreamError, unit> handler)

/// A Node `EventEmitter` used as a RELAY for a PROCESS's two ending events: what one thing
/// said, re-emitted to listeners somebody ELSE wrote — registered through here and never
/// called from here.
///
/// The listening half takes any event name, because the name arrives from whoever listens and
/// a relay has no business refusing one. The emitting half is one member per event, because
/// the event's name and what it carries are one fact, and a member per event is how the type
/// gets to say so.
[<AllowNullLiteral>]
type EventRelay =

    [<Emit("$0.on($1, $2)")>]
    abstract on : ``event``: string * listener: RelayListener -> unit

    [<Emit("$0.once($1, $2)")>]
    abstract once : ``event``: string * listener: RelayListener -> unit

    [<Emit("$0.off($1, $2)")>]
    abstract off : ``event``: string * listener: RelayListener -> unit

    /// The process ended: the code it ended with, or the signal that ended it — each exactly
    /// as it was handed over, since a listener written in JavaScript compares the code
    /// against `null`.
    [<Emit("$0.emit('exit', $1, $2)")>]
    abstract exited : code: int option * signal: string option -> unit

    /// The process failed. THROWS when nothing is listening — Node's own rule for `error`,
    /// and the reason a relay is a real `EventEmitter` rather than a list of functions kept
    /// here.
    [<Emit("$0.emit('error', $1)")>]
    abstract failed : error: StreamError -> unit

[<AutoOpen>]
module EventRelays =

    /// `new EventEmitter()`, seen as a relay: `Fable.Node`'s own view of it cannot hold a
    /// listener verbatim (see `RelayListener`), and this is the one place that says the two
    /// are the same object.
    let createRelay () : EventRelay = unbox (Node.Api.events.EventEmitter.Create ())

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
    /// Everything else is `SpawnOptions`' story — except the streams, which are one `Stdio`
    /// for all three here rather than a `StreamWiring`, because no caller of this seam wants
    /// its three to differ.
    let spawnWithEnv
        (command: string)
        (arguments: string list)
        (env: VerbatimEnv)
        (cwd: string option)
        (stdio: Stdio)
        (detached: bool)
        : ChildProcess =
        // Node reads one `Stdio` as shorthand for all three; spelled out, it is the same child.
        NodeSpawnOptions.spawn command arguments cwd (Some env) [| stdio; stdio; stdio |] detached

// --- The console, overheard ------------------------------------------------------------------

/// Hearing what was warned, for a caller whose only way to ask what some code said is to be the
/// thing it said it to. `console` is ONE mutable object for the whole process, so hearing means
/// putting something else in `warn`'s place — and reading the real one first is the only way to
/// put it back.
[<RequireQualifiedAccess>]
module ConsoleWarnings =

    /// A `console.warn` — the platform's, or the stand-in below — held only to be put back.
    type private Warner = interface end

    [<Emit("console.warn")>]
    let private current () : Warner = jsNative

    [<Emit("console.warn = $0")>]
    let private install (warner: Warner) : unit = jsNative

    /// A function called the way `console` calls one: with however many arguments the caller
    /// passed, where an F# function takes exactly one. The parts arrive as an array, so what
    /// to make of them is F#'s decision rather than this line's.
    [<Emit("(function (hear) { return (...parts) => hear(parts); })($0)")>]
    let private variadic (hear: obj [] -> unit) : Warner = jsNative

    /// Run `body` with `heard` in `console.warn`'s place, and put the real one back however the
    /// body ends. Each warning arrives as one line, spelled the way `console` would have printed
    /// it — its arguments described and joined by spaces.
    ///
    /// The body runs INSIDE rather than there being a take and a matching give-back, for
    /// `Support.withEnv`'s reason: a capture that is not given back swallows every later
    /// caller's warnings, and the half a caller forgets is the give-back.
    let overhearing (heard: string -> unit) (body: Async<'a>) : Async<'a> =
        async {
            let original = current ()
            install (variadic (fun parts -> heard (parts |> Array.map Thrown.describe |> String.concat " ")))

            try
                return! body
            finally
                install original
        }
