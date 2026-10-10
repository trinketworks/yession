module Fable.ClaudeAgentSdk

// Fable bindings to the `@anthropic-ai/claude-agent-sdk` npm package — Claude Code packaged
// as a library, and the thing on the far side of the `RunAgent` capability. This is the
// binding layer only, mirroring how `Fable.Dockerode` wraps dockerode: it declares the slice
// of the SDK's surface this repository drives and nothing more.
//
// The bar for adding to this file is that the product CALLS it. The SDK is enormous —
// hooks, subagents, plugins, session persistence, permission policies, thirty-odd members
// of the message union — and a binding that declares what nobody calls is a binding nobody
// notices going stale. Read `node_modules/@anthropic-ai/claude-agent-sdk/sdk.d.ts` for the
// real shape before widening it, and add the CALL in the same change.
//
// What is deliberately NOT here is anything that decides. Accumulating a turn's deltas,
// telling a success ending from a failed one, unwrapping what the SDK threw: those are the
// adapter's, and they belong in F# where a cheap test can reach them. This file's job is to
// make the surface typed enough that the adapter CAN be written in F# at all.
//
// Fable-only: `dotnet build` type-checks it; Fable emits the JS that runs on Node. The
// package is an esbuild external (`tasks.fsx`), so the static imports below resolve from
// node_modules at run time rather than being bundled.

open System
open Fable.Core
open Fable.Core.JsInterop

// --- tools --------------------------------------------------------------------------------

/// One content block of a tool's answer — MCP's `CallToolResult` content: `text`, or an
/// `image` carried as base64 `data` with its `mimeType`. Embedded resources it also admits,
/// and nothing here produces one.
type [<AllowNullLiteral>] ToolContent =
    abstract ``type`` : string with get, set
    abstract text : string with get, set
    abstract data : string with get, set
    abstract mimeType : string with get, set

/// What a tool handler resolves to (MCP's `CallToolResult`). `isError` is the PROTOCOL
/// failure flag — no such tool, unreadable arguments — not "the tool ran and it went
/// badly", which is ordinary text the model should read and act on.
type [<AllowNullLiteral>] ToolResult =
    abstract content : ToolContent array with get, set
    abstract isError : bool with get, set

module ToolResult =

    let private textBlock (text: string) : ToolContent =
        jsOptions<ToolContent> (fun b ->
            b.``type`` <- "text"
            b.text <- text)

    /// The single-text-block answer.
    let ofText (isError: bool) (text: string) : ToolResult =
        jsOptions<ToolResult> (fun r ->
            r.content <- [| textBlock text |]
            r.isError <- isError)

    /// Text and a picture after it — how a model is SHOWN an image, which no text can do.
    let ofTextAndImage (text: string) (mimeType: string) (base64: string) : ToolResult =
        let image =
            jsOptions<ToolContent> (fun b ->
                b.``type`` <- "image"
                b.data <- base64
                b.mimeType <- mimeType)
        jsOptions<ToolResult> (fun r ->
            r.content <- [| textBlock text; image |]
            r.isError <- false)

/// The arguments a call carried, as the SDK hands them to a handler: already validated
/// against the raw shape the tool was declared with. Opaque, because the only honest reading
/// of it is a DECODER's — `ToolInput.json` is the text one reads.
type ToolInput =
    interface end

/// The MCP request context the SDK passes as a handler's second argument. Nothing here reads
/// it; it is declared so the two-argument call has a type for each argument.
type [<AllowNullLiteral>] ToolExtra =
    interface end

module ToolInput =

    /// The arguments as JSON text, for a decoder to read. `None` when there is nothing
    /// JSON can say about them — which is what `JSON.stringify` answers `undefined` for — so
    /// an absent argument object arrives as an absence rather than as the text "undefined".
    let json (input: ToolInput) : string option =
        let text = JS.JSON.stringify input
        if isNullOrUndefined text then None else Some text

    /// Arguments from JSON text, the way the SDK would hand them over — for a caller that
    /// drives a handler the SDK's way without the SDK: a suite.
    let ofJson (text: string) : ToolInput = JS.JSON.parse text |> unbox

/// The hints a tool carries into the model's context. Built with `jsOptions`, so a hint
/// nobody set is ABSENT rather than false — which is what the SDK's optional fields mean
/// and what `readOnlyHint = false` would not.
type [<AllowNullLiteral>] ToolAnnotations =
    abstract readOnlyHint : bool with get, set
    abstract title : string with get, set

/// A tool descriptor, as `tool` builds one. The members are readable because they are what
/// says the declaration reached the descriptor: an argument order that slipped, or an
/// annotation that landed in the wrong slot, is otherwise invisible until a model behaves
/// oddly in a live turn.
type [<AllowNullLiteral>] ToolDefinition =
    abstract name : string
    abstract description : string
    abstract annotations : ToolAnnotations
    abstract handler : Func<ToolInput, ToolExtra, JS.Promise<ToolResult>>

/// `tool()`'s trailing options bag, of which this repository sets one member.
type private ToolExtras =
    abstract annotations : ToolAnnotations with get, set

[<Import("tool", "@anthropic-ai/claude-agent-sdk")>]
let private toolRaw
    (name: string)
    (description: string)
    (inputSchema: Fable.Zod.RawShape)
    (handler: Func<ToolInput, ToolExtra, JS.Promise<ToolResult>>)
    (extras: ToolExtras)
    : ToolDefinition = jsNative

/// Declare one tool. `inputSchema` is a zod RAW SHAPE — a plain object whose values are zod
/// types, one per argument (`Zod.rawShape []` for a tool that takes none); `Fable.Zod` is
/// what builds one.
///
/// The SDK calls the handler with two arguments, `(args, extra)`; `extra` carries the MCP
/// request context and nothing here reads it, so it is dropped on the way to `handler`. Passing
/// the handler as a `Func` is not decoration: a curried F# lambda would answer the SDK's
/// two-argument call with a FUNCTION rather than a promise.
let tool
    (name: string)
    (description: string)
    (inputSchema: Fable.Zod.RawShape)
    (annotations: ToolAnnotations)
    (handler: ToolInput -> JS.Promise<ToolResult>)
    : ToolDefinition =
    let extras = jsOptions<ToolExtras> (fun e -> e.annotations <- annotations)
    toolRaw name description inputSchema (Func<_, _, _> (fun args _extra -> handler args)) extras

/// An in-process MCP server, as `createSdkMcpServer` builds one: the value that goes into
/// `Options.mcpServers` under the name the model sees in `mcp__<name>__<tool>`. Its `type`
/// is `"sdk"` — the in-process transport, as against a stdio or HTTP server the CLI would
/// reach on its own.
type [<AllowNullLiteral>] McpServer =
    abstract ``type`` : string
    abstract name : string

type private McpServerOptions =
    abstract name : string with get, set
    abstract version : string with get, set
    abstract tools : ToolDefinition array with get, set

[<Import("createSdkMcpServer", "@anthropic-ai/claude-agent-sdk")>]
let private createSdkMcpServerRaw (options: McpServerOptions) : McpServer = jsNative

/// Build one in-process MCP server over a set of tools.
let createSdkMcpServer (name: string) (version: string) (tools: ToolDefinition array) : McpServer =
    createSdkMcpServerRaw (
        jsOptions<McpServerOptions> (fun o ->
            o.name <- name
            o.version <- version
            o.tools <- tools)
    )

/// `Options.mcpServers`: namespace -> server, as the plain JS object the SDK reads as a
/// record. Opaque, and made only by `McpServers.ofList`.
type McpServers =
    interface end

[<RequireQualifiedAccess>]
module McpServers =

    /// The servers under the namespaces given, in the order given — a JS object keeps
    /// insertion order, and it is the order the SDK meets them in.
    let ofList (servers: (string * McpServer) list) : McpServers =
        createObj [ for ns, server in servers -> ns, box server ] |> unbox

// --- the process seam ---------------------------------------------------------------------

/// A spawned CLI's environment COMPLETE, as the SDK carries one: a plain object of names to
/// values. The same object travels both ways across the seam — `Options.env` in, the spawn
/// request's `env` out — and it REPLACES the child's environment rather than merging with
/// `process.env`, so there is no such thing as a partial one.
///
/// Opaque, and made only by `Environment.ofMap`: the spawners hand it on to the child exactly
/// as it arrived, and nothing here reads it back. It is `Fable.NodeExtras`' `VerbatimEnv`
/// under the SDK's name, because that is what it IS — an environment complete, as the object
/// Node reads one from — and a spawner hands it to `spawnWithEnv` with no conversion between.
type Environment = Fable.NodeExtras.VerbatimEnv

[<RequireQualifiedAccess>]
module Environment =

    let ofMap (variables: Map<string, string>) : Environment = Fable.NodeExtras.VerbatimEnv.ofMap variables

/// What the SDK asks a spawner for.
type [<AllowNullLiteral>] SpawnOptions =
    abstract command : string
    abstract args : string array

    /// Where to start the child. The SDK leaves the field out, or spells it `""`, when it has
    /// no directory to name — so `Some ""` is a request that named none, and reading it is
    /// the spawner's business (`AgentSandbox.startDirectory`), not this binding's.
    abstract cwd : string option

    abstract env : Environment

    /// The SDK's OWN forwarded abort signal, not the caller's: it fires only after stdin EOF
    /// and the SDK's grace window, so a kill hung on it never pre-empts the CLI's graceful
    /// shutdown. The SDK always sends one; `None` is a request built by somebody else — a
    /// suite driving the seam by hand — and means "nothing will abort this".
    abstract signal : Fable.NodeExtras.AbortSignal option

/// Node's `number | null` for a process's exit code, spelled as Node spells it.
///
/// Its own type because F# cannot write that down any other way. Fable spells an `int option`
/// member's `None` as `undefined`, which the SDK's `exitCode === null` reads as "exited"; and
/// it passes whatever an `int` member answers through `| 0` on the way out, so even a `null`
/// smuggled in arrives as `0` — a still-running stand-in reading as one that exited cleanly.
/// Both were tried; the cheap tier pins the result.
[<AllowNullLiteral>]
type ExitCode =
    interface end

[<RequireQualifiedAccess>]
module ExitCode =

    /// `None` as `null`, which is Node's answer for "still running" (and for a process a
    /// signal ended); a code as itself.
    [<Emit("$0 ?? null")>]
    let ofOption (code: int option) : ExitCode = jsNative

    /// What an F# reader makes of one: `None` while running. Fable's test for `None` is
    /// `== null`, which is both of JavaScript's absences, so Node's `null` reads as `None`
    /// with no conversion — the cast is to what the value already is.
    let toOption (code: ExitCode) : int option = unbox code

/// The process a spawner hands back: Node's `ChildProcess` as far as the SDK reads one.
///
/// Declared rather than opaque because F# IMPLEMENTS it — the srt spawner's stand-in answers
/// for a child that srt has not finished wrapping — and what the SDK reads off a process is
/// the only statement of what a stand-in has to answer for. Implemented through
/// `SpawnedProcess.standingIn` rather than an object expression at the call site, because what
/// it answers is read afresh on every access and a relay's listeners are handed on verbatim;
/// the host spawner hands back a real `ChildProcess` through `SpawnedProcess.ofChild`.
type [<AllowNullLiteral>] SpawnedProcess =
    /// The SDK writes a turn into `stdin` and reads the CLI's answer off `stdout`. The chunk
    /// type is `Fable.Node`'s, which is what a `ChildProcess`'s streams carry there: text is
    /// what the SDK writes, and what it reads is text once somebody has called `setEncoding`.
    abstract stdin : Node.Stream.Writable<string>
    abstract stdout : Node.Stream.Readable<string>
    abstract stderr : Node.Stream.Readable<string>

    /// Whether a kill has been ASKED for — Node's own meaning, true from the moment `kill`
    /// is called rather than from the moment anything dies.
    abstract killed : bool

    /// The code the process ended with; `null` while it is still running, and for one a
    /// signal ended — WRITTEN as Node writes it, because the SDK asks `exitCode === null` and
    /// F#'s `None` is `undefined`: a stand-in that answered `None` as-is would be a process
    /// the SDK believes has exited, and whose stdin it then refuses to write. `ExitCode` says
    /// why it is its own type, and `ExitCode.toOption` is how F# reads one.
    abstract exitCode : ExitCode

    /// Signal the process; `true` for "the signal was sent". Node's own default when the
    /// caller names none is `SIGTERM`.
    abstract kill : signal: string -> bool

    /// The SDK LISTENS — for `exit` and `error` — and a listener is its own function, handed
    /// on verbatim (`Fable.NodeExtras.RelayListener` says why adapting one would break `off`).
    /// A listener written in F# goes through `SpawnedProcess.onExit` and `onError` instead,
    /// which say what each event carries.
    abstract on : ``event``: string * listener: Fable.NodeExtras.RelayListener -> unit
    abstract once : ``event``: string * listener: Fable.NodeExtras.RelayListener -> unit
    abstract off : ``event``: string * listener: Fable.NodeExtras.RelayListener -> unit

/// The block that splits a system prompt in two: what comes before it is the same for every
/// session and may be cached across them, what comes after it is this session's own. Placed
/// as a block of its own in `Options.systemPrompt`.
[<Import("SYSTEM_PROMPT_DYNAMIC_BOUNDARY", "@anthropic-ai/claude-agent-sdk")>]
let dynamicBoundary : string = jsNative

/// What `spawnClaudeCodeProcess` is: one request in, one process out, synchronously.
type Spawner = Func<SpawnOptions, SpawnedProcess>

module SpawnedProcess =

    /// A real Node child, as the process the SDK was promised. The SDK's own declaration of
    /// `SpawnedProcess` is the slice of `ChildProcess` it reads, so a child IS one; F# has no
    /// structural typing to say so, and this is the one place that asserts it.
    let ofChild (child: Node.ChildProcess.ChildProcess) : SpawnedProcess = unbox child

    /// A process F# answers for, over streams it owns and a relay its events come from.
    /// `killed` and `exitCode` are asked afresh on every read, as the SDK reads them — a
    /// value captured here would be the answer at the moment of spawning, forever.
    let standingIn
        (stdin: Node.Stream.Writable<string>)
        (stdout: Node.Stream.Readable<string>)
        (stderr: Node.Stream.Readable<string>)
        (killed: unit -> bool)
        (exitCode: unit -> int option)
        (kill: string -> bool)
        (events: Fable.NodeExtras.EventRelay)
        : SpawnedProcess =
        { new SpawnedProcess with
            member _.stdin = stdin
            member _.stdout = stdout
            member _.stderr = stderr
            member _.killed = killed ()
            member _.exitCode = ExitCode.ofOption (exitCode ())
            member _.kill signal = kill signal
            member _.on (``event``, listener) = events.on (``event``, listener)
            member _.once (``event``, listener) = events.once (``event``, listener)
            member _.off (``event``, listener) = events.off (``event``, listener) }

    [<Emit("$0.on('exit', $1)")>]
    let private listenExit (proc: SpawnedProcess) (listener: Func<int option, string option, unit>) : unit =
        jsNative

    [<Emit("$0.on('error', $1)")>]
    let private listenError (proc: SpawnedProcess) (listener: Func<Fable.NodeExtras.StreamError, unit>) : unit =
        jsNative

    /// Listen for the process ending: the code it ended with, or the signal that ended it.
    /// A `Func` because the event hands over two arguments at once, and a curried F#
    /// function would answer the first with a function rather than take both.
    let onExit (proc: SpawnedProcess) (handler: int option -> string option -> unit) : unit =
        listenExit proc (Func<int option, string option, unit> handler)

    /// Listen for the process failing — a spawn that never happened, or a stand-in whose
    /// wrap refused.
    let onError (proc: SpawnedProcess) (handler: Fable.NodeExtras.StreamError -> unit) : unit =
        listenError proc (Func<Fable.NodeExtras.StreamError, unit> handler)

// --- the turn's options -------------------------------------------------------------------

/// How much of the model's reasoning comes back. The provider offers exactly these two, and
/// unasked it answers with a signed empty block — proof that something was thought, and
/// nothing about what.
type ThinkingDisplay =
    | Summarized
    | Omitted

type [<AllowNullLiteral>] Thinking =
    abstract ``type`` : string with get, set
    abstract display : string with get, set

module Thinking =

    /// Claude decides when and how much to think. The only mode this repository asks for;
    /// the SDK's fixed-budget and disabled modes are not declared because nothing sets one.
    let adaptive (display: ThinkingDisplay) : Thinking =
        let display =
            match display with
            | Summarized -> "summarized"
            | Omitted -> "omitted"
        jsOptions<Thinking> (fun t ->
            t.``type`` <- "adaptive"
            t.display <- display)

/// The options one query runs under. Build with `jsOptions<Options>`, which starts from an
/// empty object and writes only what is assigned — so an option nobody set is ABSENT, and
/// absent is what leaves the choice to the SDK. That distinction is load-bearing in both
/// directions here: an unset `model` is the SDK's own pick, while `tools = [||]` is "no
/// built-in tools at all" and omitting it is every built-in.
type [<AllowNullLiteral>] Options =
    /// The whole system prompt, replacing Claude Code's preset, as blocks. Blocks are what
    /// the SDK sends the CLI either way (a bare string goes as a one-block list), so declaring
    /// the list is declaring what arrives: the blocks a turn planned, as it planned them.
    abstract systemPrompt : string array with get, set
    /// The model id. Unset = the SDK's own choice; there is no id meaning "no choice".
    abstract model : string with get, set
    /// Which on-disk settings layers to read. `[||]` reads none.
    abstract settingSources : string array with get, set
    abstract thinking : Thinking with get, set
    /// Ask for `stream_event` messages — without it the deltas a turn is streamed from
    /// never arrive.
    abstract includePartialMessages : bool with get, set
    /// Namespace -> `McpServer`, as the SDK reads it: a record, not an F# map.
    abstract mcpServers : McpServers with get, set
    /// The BASE set of built-in tools. `[||]` drops every one of them from the model's
    /// context; MCP servers ride a separate channel and survive it.
    abstract tools : string array with get, set
    /// The auto-approve list — NOT a restriction. On its own it leaves the read-only
    /// built-ins reachable.
    abstract allowedTools : string array with get, set
    /// Aborting it cancels the live query.
    abstract abortController : Fable.NodeExtras.AbortController with get, set
    /// A system Claude Code install, instead of the SDK's own vendored executable.
    abstract pathToClaudeCodeExecutable : string with get, set
    /// The spawned CLI's environment. It REPLACES the subprocess environment rather than
    /// merging with `process.env`, and it is what a spawner is handed back as the request's
    /// `env`.
    abstract env : Environment with get, set
    abstract spawnClaudeCodeProcess : Spawner with get, set

// --- what a query yields ------------------------------------------------------------------
//
// Settable, though nothing in the product writes one: the SDK builds these, and a suite
// builds them the same way with `jsOptions` — a typed object shaped as `sdk.d.ts` says one
// arrives, rather than an untyped bag asserted into the interface.

/// One delta on the partial-message stream. `text` and `thinking` are each present only on
/// the delta kind that carries it — read `Delta.classify` rather than either field, which is
/// also the difference between seeing a thinking delta and silently dropping it.
type [<AllowNullLiteral>] Delta =
    abstract ``type`` : string with get, set
    abstract text : string with get, set
    abstract thinking : string with get, set

/// One raw event inside a `stream_event` message.
type [<AllowNullLiteral>] StreamEvent =
    abstract ``type`` : string with get, set
    abstract delta : Delta with get, set

/// The SDK's message union, as it arrives off the async iteration: discriminated by `type`,
/// and on a `result` by `subtype` as well. Narrow it with `Message.classify`.
type [<AllowNullLiteral>] Message =
    abstract ``type`` : string with get, set

type [<AllowNullLiteral>] StreamEventMessage =
    inherit Message
    abstract ``event`` : StreamEvent with get, set

/// The turn's spend. The SDK types these non-null (`NonNullableUsage`), and the names are
/// the API's own snake_case rather than the SDK's camelCase elsewhere.
type [<AllowNullLiteral>] Usage =
    abstract input_tokens : int with get, set
    abstract output_tokens : int with get, set
    abstract cache_read_input_tokens : int with get, set
    abstract cache_creation_input_tokens : int with get, set

/// What ONE model spent, as an entry of `modelUsage` holds it. Camel-cased, unlike `Usage`
/// above: the per-model breakdown is the SDK's own record rather than the API's usage block,
/// and the two wear their own spellings.
type [<AllowNullLiteral>] ModelUsage =
    abstract inputTokens : int with get, set
    abstract outputTokens : int with get, set
    abstract cacheReadInputTokens : int with get, set
    abstract cacheCreationInputTokens : int with get, set

/// The per-model breakdown: a MAP keyed by model id, not a model and a total. It is keyed
/// that way because a turn is usually one model and a fallback makes it two, so the provider
/// will not promise one — read every key (`Object.keys`), never the first.
type [<AllowNullLiteral>] ModelUsageMap =
    [<EmitIndexer>]
    abstract Item : string -> ModelUsage with get, set

/// The one message that ends a turn. `result` carries the final text on `subtype =
/// "success"` and is absent on every other subtype, so read `subtype` first. `modelUsage` is
/// keyed by model id — which is the only place a turn says which models actually ran.
type [<AllowNullLiteral>] ResultMessage =
    inherit Message
    abstract subtype : string with get, set
    abstract result : string with get, set
    abstract usage : Usage with get, set
    abstract modelUsage : ModelUsageMap with get, set

/// What one delta is. `Other` keeps the tag rather than discarding it, so a delta kind that
/// appears later is readable at the call site instead of being indistinguishable from one
/// that was dropped.
[<RequireQualifiedAccess>]
type DeltaCase =
    | Text of string
    | Thinking of string
    | Other of tag: string

module Delta =

    let classify (delta: Delta) : DeltaCase =
        match delta.``type`` with
        | "text_delta" -> DeltaCase.Text delta.text
        | "thinking_delta" -> DeltaCase.Thinking delta.thinking
        | other -> DeltaCase.Other other

/// What one stream event is. A content block's START is not a case because nothing reads
/// one: a block is bracketed by the message it begins in and the stop below it.
[<RequireQualifiedAccess>]
type StreamEventCase =
    /// The model beginning its next message — one per API round, which after a tool call is
    /// the next thing it has to say.
    | MessageStart
    | ContentBlockDelta of Delta
    /// The provider's own end of a block.
    | ContentBlockStop
    | Other of tag: string

module StreamEvent =

    let classify (event: StreamEvent) : StreamEventCase =
        match event.``type`` with
        | "message_start" -> StreamEventCase.MessageStart
        | "content_block_delta" -> StreamEventCase.ContentBlockDelta event.delta
        | "content_block_stop" -> StreamEventCase.ContentBlockStop
        | other -> StreamEventCase.Other other

/// What one message off the query is. The SDK's union has thirty-odd members; these are the
/// two this repository reads, and `Other` carries the tag of everything else.
[<RequireQualifiedAccess>]
type MessageCase =
    | StreamEvent of StreamEventMessage
    | Result of ResultMessage
    | Other of tag: string

module Message =

    let classify (message: Message) : MessageCase =
        match message.``type`` with
        | "stream_event" -> MessageCase.StreamEvent !!message
        | "result" -> MessageCase.Result !!message
        | other -> MessageCase.Other other

/// One step of the iteration: `done` first, then `value`.
type [<AllowNullLiteral>] MessageStep =
    abstract ``done`` : bool
    abstract value : Message

/// A running query. It is a JS async generator, and `next` is exactly what `for await`
/// desugars to — which is what makes the turn loop writable as an ordinary F# loop over an
/// awaited promise, since F# has no `for await`.
type [<AllowNullLiteral>] Query =
    abstract next : unit -> JS.Promise<MessageStep>

/// What `query()` takes: the prompt, as a single message, and the options it runs under.
type private QueryParameters =
    abstract prompt : string with get, set
    abstract options : Options with get, set

[<Import("query", "@anthropic-ai/claude-agent-sdk")>]
let private queryRaw (parameters: QueryParameters) : Query = jsNative

/// Start a turn. The CLI is spawned by this call, synchronously, through
/// `spawnClaudeCodeProcess` — a spawner that throws makes `query` throw — and nothing is read
/// from it until the first `next`.
///
/// A non-success ending does NOT always arrive as a `result` message: the SDK reports one by
/// THROWING, so the promise `next` returns rejects. That is the adapter's to catch and say
/// something about, and it is the single sharpest thing to know about this surface.
///
/// The second sharpest: a query with no `pathToClaudeCodeExecutable` picks its vendored CLI
/// binary by asking whether this is a musl system, and it asks with
/// `process.report.getReport()` — synchronous, on the caller's event loop, and by default a
/// reverse lookup of every open socket's endpoints. In a process holding sockets (every
/// Session: its HTTP server, its peers) behind a resolver that does not answer PTR
/// queries, that froze the whole session for the resolver's timeout at the start of EVERY
/// turn — ten seconds on the release gate's runner, during which the message that started
/// the turn could not even reach the person who sent it. The SDK reads one header field of
/// that report; the network is never what it wanted, so it is kept out here, where the
/// call that needs it is, rather than by every caller remembering to first.
let query (prompt: string) (options: Options) : Query =
    Fable.NodeExtras.Processes.excludeNetworkFromReports ()
    queryRaw (
        jsOptions<QueryParameters> (fun p ->
            p.prompt <- prompt
            p.options <- options)
    )
