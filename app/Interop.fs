module Yession.Host.Interop

// Minimal Fable bindings for the Node APIs the Session Process host needs:
// `node-datachannel` (WebRTC) and `node:http` (bootstrap + signalling). Only the surface
// actually used is bound; everything is event-callback based, matching libdatachannel.

open Fable.Core
open Fable.NodeExtras
open Node.Api
open Node.Buffer
open Yession.Domain.Link
open Fable.Core.JsInterop

// --- Awaiting a promise ------------------------------------------------------
//
// Fable's async trampoline hijacks a workflow onto a `setTimeout` every 2000 steps, and
// `Async.AwaitPromise` attaches its rejection handler only once the workflow reaches the
// await. A promise that rejects inside that window has no handler when Node checks at the
// end of the turn, so Node kills the process — and a `try/with` around the await cannot
// help, because its handler is not attached yet. That is how a routine "no such
// container" 404, caught and ignored on every other run, killed the whole suite (the
// unhandled rejection of verify run 30725449198).
//
// `awaitPromise` settles the promise in JS, at creation, so both outcomes are a value the
// workflow may take as long as it likes to read. Every await in the Node host goes
// through it: one answer to the hazard rather than one per call site. (The browser keeps
// `Async.AwaitPromise` — an unhandled rejection there is a console warning, not a dead
// process, and this module is Node-only.)
//
// The settling is `Fable.Promise`'s own `Promise.either` — `.then(onOk, onErr)` — rather
// than an `[<Emit>]` of the same shape. This used to hand-roll it, which is how the fix
// read as bespoke cleverness instead of what it is: the library's ordinary way to hold a
// promise's outcome as a value. `Promise.result` is that exact function, and the only
// reason it is not what appears below is the falsy-reason case it does not name.

/// Await a promise; a rejection surfaces as an ordinary exception inside the workflow.
let awaitPromise (promise: JS.Promise<'a>) : Async<'a> =
    // Settle HERE, not inside the workflow below: `Promise.either` attaches in the same tick
    // that created the promise, which is the only tick in which attaching a handler is
    // guaranteed to beat Node's check. Deferring it into the `async` would reproduce the very
    // gap this exists to close.
    //
    // The failure side is `Promise.result`'s `Error` with one thing added: a rejection whose
    // reason is falsy — `Promise.reject()`, or `reject(null)` — still has to arrive as
    // something raisable, so it is NAMED here rather than reaching `raise` as null.
    let outcome =
        promise
        |> Promise.either Ok (fun error -> Error (if isNull (box error) then exn "promise rejected" else error))

    async {
        // `outcome` never rejects, so this await is safe whenever the workflow reaches it.
        let! settled = outcome |> Async.AwaitPromise
        match settled with
        | Ok value -> return value
        | Error error -> return raise error
    }

// --- node-datachannel --------------------------------------------------------

type [<AllowNullLiteral>] LocalDescription =
    abstract ``type`` : string
    abstract sdp : string

type [<AllowNullLiteral>] DataChannel =
    abstract sendMessage : string -> bool
    abstract close : unit -> unit
    abstract isOpen : unit -> bool
    abstract getLabel : unit -> string
    abstract onOpen : (unit -> unit) -> unit
    abstract onClosed : (unit -> unit) -> unit
    abstract onError : (string -> unit) -> unit
    abstract onMessage : (string -> unit) -> unit

type [<AllowNullLiteral>] PeerConnection =
    abstract close : unit -> unit
    abstract setLocalDescription : unit -> unit
    abstract setRemoteDescription : string * string -> unit
    abstract localDescription : unit -> LocalDescription
    abstract createDataChannel : string -> DataChannel
    abstract state : unit -> string
    abstract gatheringState : unit -> string
    abstract onLocalDescription : (string -> string -> unit) -> unit
    abstract onStateChange : (string -> unit) -> unit
    abstract onGatheringStateChange : (string -> unit) -> unit
    abstract onDataChannel : (DataChannel -> unit) -> unit

// `node-datachannel` is a native addon. A static top-level `import` loads its `.node`
// binary at module-eval — which would force the CHEAP test tier (pure/model/protocol tests
// that never open a WebRTC connection) to build and ship that binary just to LOAD the test
// bundle. Resolve it lazily via `createRequire` instead: the addon loads only on the first
// real connection (the verify tier and production), so the cheap tier runs without it. This
// mirrors the dynamic-`import()` pattern already used for the agent SDK and Docker backend.
// `createRequire`, not a bare `require`: Fable emits ESM and the bundle runs as ESM, where
// `require` is simply not defined — a bare one throws ReferenceError, which a lookup's `try`
// would swallow into "the thing is absent" on a box that has it. That is exactly what happened
// once: a standalone node-pty probe passed (`node -e` runs as CJS) while every pty test
// reported no pty support.
//
// ONE argument, answering the `NodeRequire` itself — never `string -> (string -> obj)`: Fable
// sees a curried arrow and wraps the import in `uncurry2`, which rewrites a use into
// `createRequire(url, name)`, one call where two were meant.
[<Import("createRequire", "node:module")>]
let private createRequire (url: string) : Node.Base.NodeRequire = jsNative

/// This module's own address. A macro by necessity, and the one thing a binding project could
/// not declare for its callers: `import.meta.url` names the module it is written in, so a copy
/// in `Fable.NodeExtras` would answer for NodeExtras. Everything below is relative to the
/// bundle this module is part of — one file, once esbuild has flattened it.
[<Emit("import.meta.url")>]
let private moduleUrl : string = jsNative

/// A CommonJS `require` rooted here. Made once: `createRequire` is not free, and the answer
/// cannot change within a process.
let private required = lazy (createRequire moduleUrl)

/// `require(id)`, from this bundle's location — for the native addons that are CJS-only and
/// loaded lazily, so their absence is an answer at the lookup rather than a failure of the
/// whole module's load. THROWS the way `require` does when there is nothing to load.
let require (id: string) : obj = required.Force().Invoke id

/// Where `require(id)` would load from, without loading it. THROWS when it would not resolve.
let resolveModule (id: string) : string = required.Force().resolve id

/// A url resolved against this bundle's own — `./assets` beside the running file.
let urlBesideModule (relative: string) : Node.Url.URL = Node.Api.URL.Create (relative, moduleUrl)

let mutable private nodeDataChannel : obj = null
/// The lazily-required `node-datachannel` module (cached after first use).
let private ndc () : obj =
    if isNull nodeDataChannel then
        nodeDataChannel <- require "node-datachannel"
    nodeDataChannel

[<Emit("new ($0.PeerConnection)($1, $2)")>]
let private newPeerConnection (module': obj) (name: string) (config: obj) : PeerConnection = jsNative

[<Emit("$0.cleanup()")>]
let private ndcCleanup (module': obj) : unit = jsNative

/// libdatachannel's global teardown; lazy like the constructor (loads the addon on demand).
let cleanup () : unit = ndcCleanup (ndc ())

/// Create a peer connection. Empty `iceServers` means no STUN and no TURN: gathering stops at
/// host candidates. Those are gathered on EVERY interface, not just loopback — so a session on
/// an overlay network puts a routable address (a tailnet `100.x`, say) in its non-trickle SDP
/// and a remote browser connects to it directly. That is the whole of remote data-channel
/// access, and why it needs a network whose addresses route directly; narrowing this to
/// loopback would silently take remote sessions with it.
let createPeerConnection (name: string) : PeerConnection =
    let config = createObj [ "iceServers" ==> ([||]: obj[]) ]
    newPeerConnection (ndc ()) name config

// --- node:http ---------------------------------------------------------------

/// A request as it ARRIVED at this process's server. `HttpMessage` is where its headers and
/// its body-as-a-stream come from, stated as inheritance rather than re-declared here,
/// because a Node server's request and a Node client's response are the same received thing
/// — and the gateway pipes one straight into the other.
type [<AllowNullLiteral>] IncomingMessage =
    inherit HttpMessage
    abstract url : string
    abstract ``method`` : string
    abstract on : string * (obj -> unit) -> IncomingMessage

/// The response this process's server is writing. `Writable` for the same reason: it is
/// where an upstream body is piped, and what gets destroyed when that body cannot finish.
type [<AllowNullLiteral>] ServerResponse =
    inherit Writable
    abstract writeHead : int * obj -> ServerResponse
    abstract write : string -> bool
    abstract ``end`` : string -> unit
    /// Whether a head has gone out — what decides if an error can still be said on this
    /// response or has to close it.
    abstract headersSent : bool

type [<AllowNullLiteral>] HttpServer =
    abstract listen : int * string * (unit -> unit) -> HttpServer
    abstract close : (obj -> unit) -> unit

/// The actual bound port (differs from the requested one when listening on 0).
[<Emit("$0.address().port")>]
let serverPort (server: HttpServer) : int = jsNative

[<Import("createServer", "node:http")>]
let private createServerRaw : System.Func<IncomingMessage, ServerResponse, unit> -> HttpServer = jsNative

/// Create an HTTP server. The handler is passed as an uncurried delegate so Node receives
/// a plain `(req, res) => ...` two-argument callback.
let createServer (handler: IncomingMessage -> ServerResponse -> unit) : HttpServer =
    createServerRaw (System.Func<_, _, _>(handler))

/// The whole body of a request, as text, then `cont`. Every route that takes a body is
/// small and decodes it entire, so there is nothing here to stream.
///
/// The stream decodes — `Readables.text` sets the encoding and hands over strings — rather
/// than each chunk being asked whether it is one. Thirteen readers used to write this loop
/// out with a `typeof chunk === 'string'` in it and a `toString('utf8')` on the other branch,
/// which is a decision per chunk made in JavaScript and wrong whenever a chunk boundary fell
/// inside a multi-byte character.
let readBody (req: IncomingMessage) (cont: string -> unit) : unit =
    let acc = System.Text.StringBuilder ()
    Readables.text req (fun chunk -> acc.Append chunk |> ignore)
    req.onEnd (fun () -> cont (acc.ToString ()))

/// Read a request header (Node lowercases header names); None when absent.
[<Emit("($0.headers[$1] ?? null)")>]
let headerOf (req: IncomingMessage) (name: string) : string option = jsNative

[<ImportAll("node:os")>]
let private nodeOs : obj = jsNative

[<Emit("$0.hostname()")>]
let private hostnameOf (os: obj) : string = jsNative

/// This box's own name — what a confined sandbox's git names to reach a listener here
/// through srt's proxy on macOS (`Sandboxes.hostAddressFrom`).
let hostname () : string = hostnameOf nodeOs

/// A cryptographically random identifier (per-launch control secrets).
[<Emit("crypto.randomUUID()")>]
let randomSecret () : string = jsNative

/// Uniform `[0, 1)` — what a jittered retry schedule spreads its delays with. Not
/// cryptographic and not meant to be: the only thing it decides is which millisecond inside
/// a backoff window a retry lands on.
let random () : float = JS.Math.random ()

/// A repeating timer, for the beats a long-lived process keeps (the MCP poll, the activity
/// report). Returns the handle `JS.clearInterval` wants.
let setInterval (ms: int) (callback: unit -> unit) : int = JS.setInterval callback ms

[<ImportAll("node:crypto")>]
let private nodeCrypto : obj = jsNative

/// SHA-256 of the UTF-8 input, base64url-encoded — the PKCE S256 operation the provider
/// applies to a `code_verifier` (RFC 7636 §4.2).
[<Emit("$0.createHash('sha256').update($1, 'utf8').digest('base64url')")>]
let private sha256B64u (cryptoModule: obj) (input: string) : string = jsNative

let sha256Base64Url (input: string) : string = sha256B64u nodeCrypto input

/// HMAC-SHA256 of the UTF-8 input under a secret, digested in `encoding` (`hex`,
/// `base64`, `base64url`). Beside the hash above because it is the same kind of thing and
/// the same imported module; the hook relay verifies signed deliveries with it, over the
/// bytes exactly as they arrived.
[<Emit("$0.createHmac('sha256', $1).update($2, 'utf8').digest($3)")>]
let private hmacSha256In (cryptoModule: obj) (secret: string) (input: string) (encoding: string) : string = jsNative

let hmacSha256 (secret: string) (input: string) (encoding: string) : string =
    hmacSha256In nodeCrypto secret input encoding

/// A short content address: enough of the SHA-256 that a different build is a different
/// string, which is what lets bytes be served under an immutable cache policy — and short
/// enough to read in a network panel. 72 bits; a collision needs two builds whose hashes agree
/// there, which no real edit produces.
///
/// `None` has nothing to address, and renders as the empty string.
///
/// Used for the shell's `ETag` — "are these the bytes you already have?", asked of a document.
/// The static files are addressed by `Assets`, which asks the same question of a whole
/// directory at once so that a stylesheet and the faces it names can never answer differently.
let contentDigest (content: string option) : string =
    match content with
    | Some text -> (sha256Base64Url text).Substring (0, 12)
    | None -> ""

/// Constant-time string equality (client secrets); length mismatch short-circuits,
/// which leaks only the length.
///
/// `timingSafeEqual` THROWS on operands of different lengths, which is why the guard is not
/// an optimisation and cannot be dropped. The comparison is `Fable.NodeExtras`'s binding of
/// `node:crypto`'s, which is where the one place a secret may be compared lives.
let timingSafeEqualStr (a: string) (b: string) : bool =
    let left = buffer.Buffer.from (a, BufferEncoding.Utf8)
    let right = buffer.Buffer.from (b, BufferEncoding.Utf8)
    left.length = right.length && timingSafeEqual left right

/// The TCP peer address of a request (`socket.remoteAddress`); None once disconnected.
[<Emit("($0.socket?.remoteAddress ?? null)")>]
let remoteAddressOf (req: IncomingMessage) : string option = jsNative

/// A query parameter of a request URL; None when absent.
[<Emit("new URL($0, 'http://local').searchParams.get($1)")>]
let queryParamOf (url: string) (name: string) : string option = jsNative

/// POST a JSON body and resolve with the response text.
///
/// `Fable.Fetch` rather than a macro, and `fetchUnsafe` rather than `fetch`, because the
/// plain binding throws on a non-2xx status — which these two do not, and never did.
/// `Http.fs` is where this host's requests otherwise go; it is compiled AFTER this file
/// (it awaits through `Interop.awaitPromise`), so these two name the binding themselves.
let postText (url: string) (body: string) : JS.Promise<string> =
    Fetch.fetchUnsafe
        url
        [ Fetch.Types.RequestProperties.Method Fetch.Types.HttpMethod.POST
          Fetch.requestHeaders [ Fetch.Types.HttpRequestHeaders.ContentType "application/json" ]
          Fetch.Types.RequestProperties.Body (U3.Case3 body) ]
    |> Promise.bind (fun response -> response.text ())

/// GET a URL and resolve with the response text.
let getText (url: string) : JS.Promise<string> =
    Fetch.fetchUnsafe url [] |> Promise.bind (fun response -> response.text ())

/// Read an environment variable, falling back to `fallback` when unset or empty.
[<Emit("process.env[$0] || $1")>]
let envOr (name: string) (fallback: string) : string = jsNative

[<Emit("process.env[$0] = $1")>]
let setEnv (name: string) (value: string) : unit = jsNative

/// Every variable name in this process's environment, sorted. NAMES only, and that is the
/// point: a report can say which are present without reading any of them, so this is not a
/// second reader of anything — `envOr` remains the one way a value is read.
[<Emit("Object.keys(process.env).sort()")>]
let envNames () : string array = jsNative

/// How this deployment is reached from outside: the two operator
/// variables, parsed into the one value that decides both the Manager's public origin
/// and where sessions live. Error = a combination that cannot be deployed; every caller
/// fails its boot loudly rather than starting a half-reachable process.
///
/// Read once per process at boot. The Manager parses it to render open links and to be
/// its own OIDC issuer; a session parses the same variables, inherited by plain env, to
/// build its OAuth redirect URI and to know the path it is mounted under.
let publicAccess () : Result<Yession.Domain.Link.PublicAccess, string> =
    Yession.Domain.Link.PublicAccess.create (envOr "YESSION_MANAGER_URL" "") (envOr "YESSION_SESSION_URL" "")

/// Terminate the Node process with an exit code.
let exit (code: int) : unit = ``process``.exit code

// Command-line reading lives in `Cli`, over Node's own `parseArgs`. There used to be a
// `versionFlag` and an `argValue` here that scanned `process.argv` by hand; they could not
// tell an unknown option from an absent one, so a typo ran the bin with the option missing.
