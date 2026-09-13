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

/// One content block of a tool's answer. Text is the only kind this repository's tools
/// return — MCP's `CallToolResult` admits images and embedded resources, and nothing here
/// produces one.
type [<AllowNullLiteral>] ToolContent =
    abstract ``type`` : string
    abstract text : string

/// What a tool handler resolves to (MCP's `CallToolResult`). `isError` is the PROTOCOL
/// failure flag — no such tool, unreadable arguments — not "the tool ran and it went
/// badly", which is ordinary text the model should read and act on.
type [<AllowNullLiteral>] ToolResult =
    abstract content : ToolContent array
    abstract isError : bool

module ToolResult =

    /// The single-text-block answer, the only shape this repository's tools produce.
    let ofText (isError: bool) (text: string) : ToolResult =
        let block = createObj [ "type" ==> "text"; "text" ==> text ]
        createObj [ "content" ==> [| block |]; "isError" ==> isError ] |> unbox

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
    abstract handler : Func<obj, obj, JS.Promise<ToolResult>>

[<Import("tool", "@anthropic-ai/claude-agent-sdk")>]
let private toolRaw
    (name: string)
    (description: string)
    (inputSchema: obj)
    (handler: Func<obj, obj, JS.Promise<ToolResult>>)
    (extras: obj)
    : ToolDefinition = jsNative

/// Declare one tool. `inputSchema` is a zod RAW SHAPE — a plain object whose values are zod
/// types, one per argument (`{}` for a tool that takes none); `Fable.Zod` is what builds one
/// from the JSON Schema every other boundary a descriptor crosses speaks.
///
/// The SDK calls the handler with two arguments, `(args, extra)`; `extra` carries the MCP
/// request context and nothing here reads it, so it is dropped rather than declared. Passing
/// the handler as a `Func` is not decoration: a curried F# lambda would answer the SDK's
/// two-argument call with a FUNCTION rather than a promise.
let tool
    (name: string)
    (description: string)
    (inputSchema: obj)
    (annotations: ToolAnnotations)
    (handler: obj -> JS.Promise<ToolResult>)
    : ToolDefinition =
    toolRaw name description inputSchema (Func<_, _, _> (fun args _extra -> handler args)) (createObj [ "annotations" ==> annotations ])

/// An in-process MCP server, as `createSdkMcpServer` builds one: the value that goes into
/// `Options.mcpServers` under the name the model sees in `mcp__<name>__<tool>`. Its `type`
/// is `"sdk"` — the in-process transport, as against a stdio or HTTP server the CLI would
/// reach on its own.
type [<AllowNullLiteral>] McpServer =
    abstract ``type`` : string
    abstract name : string

[<Import("createSdkMcpServer", "@anthropic-ai/claude-agent-sdk")>]
let private createSdkMcpServerRaw (options: obj) : McpServer = jsNative

/// Build one in-process MCP server over a set of tools.
let createSdkMcpServer (name: string) (version: string) (tools: ToolDefinition array) : McpServer =
    createSdkMcpServerRaw (createObj [ "name" ==> name; "version" ==> version; "tools" ==> tools ])

// --- the process seam ---------------------------------------------------------------------

/// What the SDK asks a spawner for. `signal` is the SDK's OWN forwarded abort signal, not
/// the caller's: it fires only after stdin EOF and the SDK's grace window, so a kill hung on
/// it never pre-empts the CLI's graceful shutdown.
type [<AllowNullLiteral>] SpawnOptions =
    abstract command : string
    abstract args : string array
    abstract cwd : string
    abstract env : obj
    abstract signal : obj

/// The process a spawner hands back. Opaque, because nothing in this repository READS one —
/// the two spawners build it (or hand back a Node `ChildProcess`) and the SDK consumes it.
/// Declaring its members would be declaring an interface no F# here implements, with two
/// `on`/`once`/`off` overloads whose listener arity differs by event.
type [<AllowNullLiteral>] SpawnedProcess =
    interface end

// --- the turn's options -------------------------------------------------------------------

/// How much of the model's reasoning comes back. The provider offers exactly these two, and
/// unasked it answers with a signed empty block — proof that something was thought, and
/// nothing about what.
type ThinkingDisplay =
    | Summarized
    | Omitted

type [<AllowNullLiteral>] Thinking =
    abstract ``type`` : string
    abstract display : string

module Thinking =

    /// Claude decides when and how much to think. The only mode this repository asks for;
    /// the SDK's fixed-budget and disabled modes are not declared because nothing sets one.
    let adaptive (display: ThinkingDisplay) : Thinking =
        let display =
            match display with
            | Summarized -> "summarized"
            | Omitted -> "omitted"
        createObj [ "type" ==> "adaptive"; "display" ==> display ] |> unbox

/// The options one query runs under. Build with `jsOptions<Options>`, which starts from an
/// empty object and writes only what is assigned — so an option nobody set is ABSENT, and
/// absent is what leaves the choice to the SDK. That distinction is load-bearing in both
/// directions here: an unset `model` is the SDK's own pick, while `tools = [||]` is "no
/// built-in tools at all" and omitting it is every built-in.
type [<AllowNullLiteral>] Options =
    /// The whole system prompt, replacing Claude Code's preset.
    abstract systemPrompt : string with get, set
    /// The model id. Unset = the SDK's own choice; there is no id meaning "no choice".
    abstract model : string with get, set
    /// Which on-disk settings layers to read. `[||]` reads none.
    abstract settingSources : string array with get, set
    abstract thinking : Thinking with get, set
    /// Ask for `stream_event` messages — without it the deltas a turn is streamed from
    /// never arrive.
    abstract includePartialMessages : bool with get, set
    /// Namespace -> `McpServer`, as a plain JS object (`createObj`). Not an F# map: the SDK
    /// reads it as a record.
    abstract mcpServers : obj with get, set
    /// The BASE set of built-in tools. `[||]` drops every one of them from the model's
    /// context; MCP servers ride a separate channel and survive it.
    abstract tools : string array with get, set
    /// The auto-approve list — NOT a restriction. On its own it leaves the read-only
    /// built-ins reachable.
    abstract allowedTools : string array with get, set
    /// A JS `AbortController`; aborting it cancels the live query.
    abstract abortController : obj with get, set
    /// A system Claude Code install, instead of the SDK's own vendored executable.
    abstract pathToClaudeCodeExecutable : string with get, set
    /// The spawned CLI's environment, as a plain JS object (`createObj`). It REPLACES the
    /// subprocess environment rather than merging with `process.env`.
    abstract env : obj with get, set
    abstract spawnClaudeCodeProcess : Func<SpawnOptions, SpawnedProcess> with get, set

// --- what a query yields ------------------------------------------------------------------

/// One delta on the partial-message stream. `text` and `thinking` are each present only on
/// the delta kind that carries it — read `Delta.classify` rather than either field, which is
/// also the difference between seeing a thinking delta and silently dropping it.
type [<AllowNullLiteral>] Delta =
    abstract ``type`` : string
    abstract text : string
    abstract thinking : string

/// One raw event inside a `stream_event` message.
type [<AllowNullLiteral>] StreamEvent =
    abstract ``type`` : string
    abstract delta : Delta

/// The SDK's message union, as it arrives off the async iteration: discriminated by `type`,
/// and on a `result` by `subtype` as well. Narrow it with `Message.classify`.
type [<AllowNullLiteral>] Message =
    abstract ``type`` : string

type [<AllowNullLiteral>] StreamEventMessage =
    inherit Message
    abstract ``event`` : StreamEvent

/// The turn's spend. The SDK types these non-null (`NonNullableUsage`), and the names are
/// the API's own snake_case rather than the SDK's camelCase elsewhere.
type [<AllowNullLiteral>] Usage =
    abstract input_tokens : int
    abstract output_tokens : int
    abstract cache_read_input_tokens : int
    abstract cache_creation_input_tokens : int

/// The one message that ends a turn. `result` carries the final text on `subtype =
/// "success"` and is absent on every other subtype, so read `subtype` first. `modelUsage` is
/// keyed by model id — which is the only place a turn says which model actually answered.
type [<AllowNullLiteral>] ResultMessage =
    inherit Message
    abstract subtype : string
    abstract result : string
    abstract usage : Usage
    abstract modelUsage : obj

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

[<Import("query", "@anthropic-ai/claude-agent-sdk")>]
let private queryRaw (parameters: obj) : Query = jsNative

/// Start a turn. Nothing runs until the first `next` — the CLI is spawned lazily.
///
/// A non-success ending does NOT always arrive as a `result` message: the SDK reports one by
/// THROWING, so the promise `next` returns rejects. That is the adapter's to catch and say
/// something about, and it is the single sharpest thing to know about this surface.
let query (prompt: string) (options: Options) : Query =
    queryRaw (createObj [ "prompt" ==> prompt; "options" ==> options ])
