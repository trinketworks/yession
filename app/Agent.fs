module Yession.Host.Agent

// The real agent runner: an adapter from the `RunAgent` capability to the Claude Agent
// SDK. The turn's typed capabilities reach the model as MCP tools, and WHICH tools those
// are is no longer this file's business — `AgentTools.registry` answers that, and the
// adapter turns whatever it answers into `tool` declarations in a loop (Plan 16, part A).
// Requires ANTHROPIC_API_KEY or CLAUDE_CODE_OAUTH_TOKEN; the deterministic tests never call
// this, and the live smoke test is gated on credentials, so verification stays repeatable.
//
// It is F# over `Fable.ClaudeAgentSdk` and `Fable.Zod`. It used to be a 169-line JavaScript
// program inside one `[<Emit>]` string, and it had every fault that shape has: an emit body
// is inlined into whatever calls it, so it was invisible to a reader of this file,
// unreachable from any other, and — the sharp edge — Fable does not treat a change to one as
// a change to its callers. Nothing inside it was type-checked and nothing inside it was
// reachable from a test, which is how a thinking delta came to be told from a text one by
// which FIELD happened to hold a string. What this adapter DECIDES now lives in `Turn`
// below, over ordinary values, where the cheap tier reaches all of it.

open System
open Fable
open Fable.Core
open Fable.Core.JsInterop
open Fable.ClaudeAgentSdk
open Yession.Domain
open Yession.Domain.Sandboxes
open Yession.Domain.Agent
open Yession.Domain.Terminals
open Yession.Domain.Tools
open Yession.Domain.Chat

#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

// --- a tool descriptor, as the SDK wants one ----------------------------------------------

/// One JSON Schema node as zod says it. The slice is what `Fable.Zod` binds and no wider —
/// the leaf types a `type` can name, and `array` over another node. Anything else is `any`,
/// which is a WIDER schema and never a refusal: a tool whose arguments this cannot describe
/// must still be callable, and the model still reads the description.
///
/// A node that is not an object at all — a schema written wrongly — is `any` for the same
/// reason.
let rec private zodType () : Decoder<Zod.ZodType> =
    let node =
        Decode.object (fun get ->
            match get.Optional.Field "type" Decode.string with
            | Some "string" -> Zod.string ()
            | Some "boolean" -> Zod.boolean ()
            | Some "number"
            | Some "integer" -> Zod.number ()
            | Some "array" -> Zod.array (get.Optional.Field "items" (zodType ()) |> Option.defaultWith Zod.any)
            | Some _
            | None -> Zod.any ())
    Decode.oneOf [ node; Decode.succeed (Zod.any ()) ]

/// One property: its node, and the documentation the model is shown beside it. The
/// description is read HERE rather than inside `zodType`, so that it lands on the property
/// and not on an array's elements — which is where the JSON Schema wrote it.
let private zodProperty () : Decoder<Zod.ZodType * string option> =
    Decode.map2
        (fun node description -> node, description)
        (zodType ())
        (Decode.oneOf [ Decode.optional "description" Decode.string; Decode.succeed None ])

/// JSON Schema in, zod raw shape out — a plain object whose values are zod types, one per
/// argument, which is what the SDK's tool builder takes.
///
/// The conversion belongs here, at the one edge that needs it, rather than making the schema
/// itself SDK-shaped: every OTHER boundary a descriptor crosses (MCP's `tools/list`, an
/// external server, the audit record) speaks JSON Schema.
///
/// A schema that cannot be read at all is no arguments at all. That is what the `catch`
/// around `JSON.parse` used to say, and it is still the only answer available: refusing here
/// would take a tool away from the turn over a schema the model never sees.
let zodShape (schema: string) : obj =
    let shape =
        Decode.object (fun get ->
            let required =
                get.Optional.Field "required" (Decode.oneOf [ Decode.list Decode.string; Decode.succeed [] ])
                |> Option.defaultValue []
                |> Set.ofList
            get.Optional.Field "properties" (Decode.keyValuePairs (zodProperty ()))
            |> Option.defaultValue []
            |> List.map (fun (key, (node: Zod.ZodType, description)) ->
                let described =
                    match description with
                    | Some description -> node.describe description
                    | None -> node
                key ==> (if required.Contains key then described else described.optional ())))
    match Decode.fromString shape schema with
    | Ok properties -> createObj properties
    | Error _ -> createObj []

/// One tool, as the SDK declares one, over the registry's single dispatch.
///
/// Every tool — in-process today, proxied tomorrow — is answered through `registry.Invoke`,
/// which is what makes a single audit seam possible rather than one per implementation. What
/// comes back says whether the call HAPPENED: an `Error` is a protocol failure (no such tool,
/// unreadable arguments) and the SDK is told about it as `isError`, while a tool that ran and
/// went badly is `Ok` with text saying so, because that is something the model should read
/// and act on.
let private toolOf (registry: ToolRegistry) (descriptor: ToolDescriptor) : ToolDefinition =
    // A hint nobody set is ABSENT rather than false, which is what MCP's optional annotations
    // mean and what `readOnlyHint = false` would not.
    let annotations =
        jsOptions<ToolAnnotations> (fun a ->
            if descriptor.ReadOnly then a.readOnlyHint <- true
            match descriptor.Title with
            | Some title -> a.title <- title
            | None -> ())
    tool
        descriptor.Name
        descriptor.Description
        (zodShape descriptor.InputSchema)
        annotations
        (fun args ->
            async {
                let arguments = JS.JSON.stringify (if isNull args then createObj [] else args)
                let call : ToolCall =
                    { Namespace = descriptor.Namespace; Name = descriptor.Name; Arguments = arguments }
                match! registry.Invoke call with
                | Ok answer -> return ToolResult.ofText false answer.Text
                | Error reason -> return ToolResult.ofText true reason
            }
            |> Async.StartAsPromise)

/// One SDK MCP server per namespace, which is what puts the namespace in the wire name the
/// model sees (mcp__<namespace>__<tool>) without inventing a naming scheme.
///
/// Every entry here is one of OUR in-process SDK servers, built from the registry. A declared
/// external server never goes in this map, tempting as its one line is: a server the model
/// reaches directly is a second door, and its calls skip the approval gate, the tool-use
/// record and attribution — the whole of what `ToolUseLog` and `ToolStreams` wrap the merged
/// registry to guarantee. It also decides who holds a provider's claim: reached through the
/// proxy the claim belongs to the SESSION, so the terminal's write lease can arbitrate
/// between the agent and a human; reached directly it belongs to the agent's own MCP session,
/// and nobody can take the device off it.
let private serversOf (registry: ToolRegistry) : obj =
    ToolRegistry.namespaces registry
    |> List.map (fun ns ->
        let tools =
            registry.Tools
            |> List.filter (fun descriptor -> descriptor.Namespace = ns)
            |> List.map (toolOf registry)
            |> Array.ofList
        ns ==> createSdkMcpServer ns "1.0.0" tools)
    |> createObj

// --- what one turn accumulates ------------------------------------------------------------

/// The turn, as a fold: what the SDK's partial-message stream adds up to, and what each
/// message gives the session to forward on the way.
///
/// A fold over values rather than a loop over mutable state, because this is the whole of
/// what the adapter DECIDES — which delta is a thought, when a thought is whole, which body a
/// turn ends with, what it spent — and none of it needs a model, a credential or a process to
/// check. `Fable.ClaudeAgentSdk` stops deliberately short of it: the binding narrows the
/// union, and everything here reads the narrowing.
module Turn =

    /// What has arrived so far.
    type State =
        { /// The ending's own words, when it carried any. Not the answer on its own: an
          /// ending that carried none — and a stream that ends with no ending at all — falls
          /// back to `Streamed`, and `outcome` below is where that happens.
          Body : string
          /// The LAST message's text deltas — the fallback body for an ending that carries
          /// none. Reset at every `message_start`, because a turn is several messages once
          /// the model calls a tool, and the whole turn's text is not what the last message
          /// said.
          Streamed : string
          /// One thought's deltas, held until its block ends, so that one thought is one
          /// `Thinking` chunk. Forwarded per delta, a thought arrived as an event per token —
          /// twenty-eight events for seven thoughts, split at "I" / "'ll clone the
          /// repository" — and every reader had to put them back together by adjacency, which
          /// is a rule nothing states and nothing checks. Text gets away with per-delta
          /// because a message is bracketed by started/completed and the completion carries
          /// the whole body; a thought is bracketed by nothing.
          Thinking : string
          /// The non-success ending the SDK YIELDED, if it yielded one.
          Failed : string option
          /// Plan 04, Step 28: the `result` message's usage block, kept instead of discarded.
          Usage : AgentUsage }

    let empty : State =
        { Body = ""
          Streamed = ""
          Thinking = ""
          Failed = None
          Usage =
            { InputTokens = 0
              OutputTokens = 0
              CacheReadTokens = 0
              CacheCreationTokens = 0
              Model = None } }

    /// One count off the usage block. A result carrying no usage block at all is zero, and
    /// that is the only absence there is to answer for: the SDK types every count inside one
    /// non-null, and reading a missing one as `int` answers 0 anyway.
    let private counted (read: Usage -> int) (usage: Usage) : int =
        if isNull usage then 0 else read usage

    /// Which model actually answered. `modelUsage` is keyed by model id, and it is the only
    /// place a turn says which one ran; an empty key is no answer.
    let private modelOf (result: ResultMessage) : string option =
        if isNull result.modelUsage then None
        else
            JS.Constructors.Object.keys result.modelUsage
            |> Seq.tryHead
            |> Option.filter (fun id -> id <> "")

    /// The spend, read off an ending. An ending that says nothing about the model leaves the
    /// one already read standing rather than clearing it.
    let private usageFrom (previous: AgentUsage) (result: ResultMessage) : AgentUsage =
        { InputTokens = counted (fun usage -> usage.input_tokens) result.usage
          OutputTokens = counted (fun usage -> usage.output_tokens) result.usage
          CacheReadTokens = counted (fun usage -> usage.cache_read_input_tokens) result.usage
          CacheCreationTokens = counted (fun usage -> usage.cache_creation_input_tokens) result.usage
          Model = modelOf result |> Option.orElse previous.Model }

    /// The pending thought, forwarded and cleared.
    ///
    /// Three things end a block and all three come here: the provider's own
    /// `content_block_stop`, the next `message_start`, and the end of the stream. So a block
    /// the provider never closes is still reported rather than lost — an unterminated thought
    /// is worth reading and this is the only copy of it.
    let flush (state: State) : State * AgentResponseChunk list =
        if state.Thinking = "" then state, []
        else { state with Thinking = "" }, [ AgentResponseChunk.Thinking state.Thinking ]

    /// Fold one message off the query into the turn, and say what the session forwards for it.
    let step (state: State) (message: Message) : State * AgentResponseChunk list =
        match Message.classify message with
        | MessageCase.StreamEvent partial ->
            match StreamEvent.classify partial.``event`` with
            // One `message_start` per API round: the model beginning its next message, which
            // after a tool call is the next thing it has to say. The pending thought is
            // flushed, a boundary is forwarded so the turn can be split where the model split
            // it, and `Streamed` starts over.
            | StreamEventCase.MessageStart ->
                let state, thought = flush state
                { state with Streamed = "" }, thought @ [ AgentResponseChunk.MessageBoundary ]
            | StreamEventCase.ContentBlockDelta delta ->
                match Delta.classify delta with
                | DeltaCase.Text text ->
                    { state with Streamed = state.Streamed + text }, [ AgentResponseChunk.Text text ]
                // Reasoning arrives on the same stream under its own delta, told apart by the
                // delta's TAG and never by which field happens to hold a string — that test
                // is what let a thinking delta look exactly like an event nobody cared about.
                // It is NOT added to `Streamed`: that is the fallback body for what the model
                // SAID.
                | DeltaCase.Thinking thought -> { state with Thinking = state.Thinking + thought }, []
                | DeltaCase.Other _ -> state, []
            | StreamEventCase.ContentBlockStop -> flush state
            // Everything else, `content_block_start` among it. A block's opening is read by
            // nobody here on purpose: the API sends it with its text or its thinking EMPTY and
            // puts the content in the deltas, and the block is already bracketed by the stop
            // above — so a second flush point would be a spare mechanism for a requirement one
            // already meets.
            | StreamEventCase.Other _ -> state, []
        | MessageCase.Result result ->
            let state = { state with Usage = usageFrom state.Usage result }
            if result.subtype = "success" then
                // The ending's own text, and nothing in its place when it has none: the
                // fallback to what was streamed lives in `outcome`, because an ending is not
                // the only way a turn ends and both ways fall back to the same text.
                let said = result.result
                { state with Body = (if String.IsNullOrEmpty said then "" else said) }, []
            else { state with Failed = Some ("agent run ended: " + result.subtype) }, []
        | MessageCase.Other _ -> state, []

    /// What the turn answers with: the reason it stopped, or what the model said.
    ///
    /// What it said is the ending's own words when it carried any, and the last message's
    /// deltas when it did not — INCLUDING when there was no `result` message at all. A
    /// stream can simply run out, and answering that with `Body` alone reported a success
    /// over an empty body while throwing away everything the model had streamed. The deltas
    /// are the only copy left in either case, so one fallback answers both.
    let outcome (state: State) : Result<string, string> =
        match state.Failed with
        | Some reason -> Error reason
        | None -> Ok (if state.Body = "" then state.Streamed else state.Body)

// --- one turn, run ------------------------------------------------------------------------

/// Everything one query runs under, assembled from what the session decided.
let private optionsFor
    (systemPrompt: string)
    (model: string option)
    (registry: ToolRegistry)
    (controller: Fetch.Types.AbortController)
    (claudePath: string)
    (agentEnv: obj)
    (claudeSpawner: obj)
    : Options =
    jsOptions<Options> (fun o ->
        o.systemPrompt <- systemPrompt
        // The session's model choice, and ONLY when it has made one: an absent option is
        // what leaves the pick to the SDK, and passing an empty string instead would be
        // this session inventing a model id of "".
        match model with
        | Some chosen -> o.model <- chosen
        | None -> ()
        // No `maxTurns`: unset is the SDK's no-cap default, the same setting interactive
        // Claude Code runs under. A turn ends when the model is done or somebody
        // interrupts it, never at a step count this file picked.
        o.settingSources <- [||]
        // Ask for the reasoning, summarised — the only two choices the provider offers are a
        // summary and nothing, and unasked it answers with a signed empty block: proof that
        // something was thought, and nothing about what.
        o.thinking <- Thinking.adaptive Summarized
        o.includePartialMessages <- true
        o.mcpServers <- serversOf registry
        // The turn's ONLY tools are the registry's. `tools = [||]` drops every built-in
        // (Bash/Read/Glob/Grep/WebFetch/Agent/Skill) from the model's context; MCP servers
        // ride a separate channel, so the registry's tools survive it. `allowedTools` is
        // NOT a restriction — it is the auto-approve list, and on its own it left the
        // read-only built-ins reachable (a session could list the host filesystem). It
        // stays so our tools run without a permission round-trip, and it is COMPUTED from
        // the same descriptors the servers were built from, so the two cannot drift.
        o.tools <- [||]
        o.allowedTools <- ToolRegistry.allowedTools registry |> Array.ofList
        o.abortController <- box controller
        if claudePath <> "" then o.pathToClaudeCodeExecutable <- claudePath
        o.env <- agentEnv
        o.spawnClaudeCodeProcess <- !!claudeSpawner)

/// Drive one query to its end: forward what the turn says as it says it, and answer with the
/// body or the reason, and what it spent either way.
let private runQuery
    (systemPrompt: string)
    (prompt: string)
    (model: string option)
    (registry: ToolRegistry)
    (agentEnv: obj)
    (claudePath: string)
    (claudeSpawner: obj)
    (registerAbort: (unit -> unit) -> unit)
    (forward: AgentResponseChunk -> unit)
    : Async<Result<string, string> * AgentUsage> =
    async {
        // Held OUTSIDE the try because a turn does not always end by returning: the SDK
        // reports a non-success ending by THROWING, and what the turn spent before that has
        // to survive the throw. See the `with` below.
        let mutable state = Turn.empty
        try
            let controller = Fetch.newAbortController ()
            registerAbort (fun () -> controller.abort ())
            let options = optionsFor systemPrompt model registry controller claudePath agentEnv claudeSpawner
            let running = query prompt options
            let mutable finished = false
            while not finished do
                let! step = running.next () |> Interop.awaitPromise
                if step.``done`` then finished <- true
                else
                    let next, chunks = Turn.step state step.value
                    state <- next
                    chunks |> List.iter forward
            let ended, chunks = Turn.flush state
            state <- ended
            chunks |> List.iter forward
            return Turn.outcome state, state.Usage
        with error ->
            // Where a real ending arrives. The `Result` branch of `Turn.step` reads the ending
            // the SDK YIELDS; the endings that happen — a step ceiling, a refused credential —
            // are thrown instead, so they land here, and answering with zeros threw away the
            // usage of the longest turns in the session. The streamed text is already durable
            // (every chunk was forwarded as a delta while it arrived), so what is recovered
            // here is the spend and the reason; `sdkFailureReason` below unwraps the latter.
            //
            // A thought still PENDING when the throw arrives is not forwarded — the flush sits
            // inside the try, above. That is how this has always behaved, and this change is
            // deliberately not where it gets decided.
            return Error (Http.reasonOf error), state.Usage
    }

/// What the SDK threw, said as the reason a turn stopped.
///
/// A non-success ending is not the `result` message the runner reads for one — the SDK
/// throws, wrapping the CLI's own sentence in one of its own: `Claude Code returned an
/// error result: Reached maximum number of turns (32)`. Every ending on record in a real
/// session arrived that way, which is how the runner's own wording for a step ceiling
/// never once reached a screen, and how a person reading a stopped turn was told what
/// layer had spoken rather than what had happened.
///
/// Anything not wearing the wrapper is already the reason and passes through.
let sdkFailureReason (raw: string) : string =
    let prefix = "Claude Code returned an error result:"
    let said = raw.Trim ()
    if said.StartsWith prefix then said.Substring(prefix.Length).Trim () else said

/// Some sandboxes disallow the SDK's own vendored executable; `YESSION_BIN_CLAUDE`
/// points the SDK at a system Claude Code install instead. Empty = SDK default.
let private claudePath () = Interop.envOr "YESSION_BIN_CLAUDE" ""

/// One prompt per turn: the completed conversation as a transcript plus the message to
/// answer. Built from the projection only — draft/Yjs state never appears here.
let private promptOf (context: AgentContextPack) : string =
    let label (author: ActorRef) =
        match author with
        | UserRef u -> UserId.value u
        | PeerRef p -> PeerId.value p
        | ActorRef.Agent -> "agent"
        | ActorRef.SessionProcess -> "session-process"
        | ActorRef.System -> "system"
        | ActorRef.Configured repo -> RepoRef.value repo
    let transcript =
        context.Conversation
        |> List.filter (fun item -> item.Status = Complete)
        // `said`, never `Body`: an act note's body is its headline, and a transcript built
        // from headlines would tell the agent a sandbox started without telling it whose
        // credential went in — the particulars are exactly what a next turn has to act on.
        |> List.map (fun item -> sprintf "%s: %s" (label item.Author) (ConversationItem.said item))
        |> String.concat "\n"
    // The terminal digest is rendered as its own section, never folded into the
    // conversation: the model must be able to tell what someone SAID from what a machine
    // PRINTED, and a block attributed like a chat line invites it to reply to the output.
    let terminals =
        match context.Terminals with
        | [] -> ""
        | blocks ->
            let render (block: BlockDigest) =
                let outcome =
                    match block.Status with
                    | BlockRunning -> "still running"
                    | BlockFinished (CommandSucceeded code) -> sprintf "exit %d" code
                    | BlockFinished (CommandFailed code) -> sprintf "exit %d" code
                    | BlockFinished (CommandExecutionFailed reason) -> sprintf "could not run: %s" reason
                    | BlockFinished CommandTimedOut -> "timed out"
                    // The agent is told it was refused, and by whom. This is the feedback
                    // the review gate owes whoever it refused: without it a rejected
                    // command is indistinguishable from one that vanished, and the model
                    // reasonably tries again.
                    | BlockRejected (by, Some why) -> sprintf "refused by %s: %s" (label by) why
                    | BlockRejected (by, None) -> sprintf "refused by %s" (label by)
                let elided =
                    if block.Elided > 0 then
                        sprintf "[%d earlier characters omitted — the whole output is in the transcript]\n" block.Elided
                    else ""
                sprintf
                    "[%s] %s ran: %s (%s)\n%s%s"
                    (TerminalTitle.value block.Title)
                    (label block.Author)
                    block.Command
                    outcome
                    elided
                    block.OutputTail
            sprintf
                "\n\nTerminal activity since your last turn (you did not see this before now):\n%s"
                (blocks |> List.map render |> String.concat "\n\n")
    match context.CurrentMessage with
    | Some message ->
        sprintf
            "Conversation so far:\n%s%s\n\nReply to the latest message from %s:\n%s"
            transcript
            terminals
            (label message.Author)
            (ConversationItem.said message)
    // A turn nobody asked for (Plan 20, stage 2): work this agent started finished while it
    // was not running. There is no message to reply to, and inventing one — "the system says
    // your build finished" — would put words in somebody's mouth on a shared transcript. It
    // is told what it is, and the terminal activity above is what it acts on.
    | None ->
        sprintf
            "Conversation so far:\n%s%s\n\nNobody has said anything new. You are running because work you started in the background finished — the terminal activity above is that work. Carry on with it, and say what it means for what you were doing."
            transcript
            terminals

/// Every tool ONE turn can reach, assembled once: the session's own registry, plus a
/// namespace per MCP server it was given (Plan 17), wrapped in the audit seam (Plan 16,
/// part C) over the merged whole — applying it per server would let a provider added later
/// arrive with its own logging, or with none.
///
/// It lives here rather than inside the runner below because "what can this turn call, and
/// what happens when it does" is a question worth answering without a model in the loop: a
/// harness that drives a tool call drives THIS, so the thing it exercises is the thing a
/// turn exercises rather than a second assembly that resembles it.
///
/// A merge refusal can only be a BUG — resolution already made the names unique — so it is
/// reported and the turn proceeds on the session's own tools rather than failing. A turn
/// that cannot reach a printer is a smaller problem than a turn that will not run.
let registryFor (capabilities: AgentCapabilities) : ToolRegistry =
    let own = AgentTools.registry capabilities
    let merged =
        match ToolRegistry.mergeAll (own :: capabilities.Tools.Foreign) with
        | Ok registry -> registry
        | Error reason ->
            eprintfn "mcp: two registries claim one namespace (%s); using the session's own tools" reason
            own
    merged |> ToolUseLog.wrap capabilities.Tools.Record

/// The Claude Agent SDK–backed `RunAgent`, over this session's data directory (the CLI's
/// scratch HOME hangs off it), the backend that confines the CLI — decided once at
/// session boot and passed in, never re-read here — and the turn's credential:
/// `None` = the ambient credential variables pass through (the documented last resort
/// — how CI's LiveAgent tier feeds the agent); `Some (envVar, value)` = the spawned
/// CLI runs with exactly that credential, both ambient credential variables displaced.
/// Either way the CLI's environment is the AgentSandbox policy env — allowlisted
/// baseline + per-session scratch HOME — never the raw process env, and the CLI
/// process itself comes up through the `spawnClaudeCodeProcess` seam. Streams text
/// deltas as chunks; the typed capabilities surface as MCP tools; failures are values,
/// never exceptions. The abort signal maps onto the SDK's AbortController, so an
/// interrupt cancels the live query promptly (the returned failure is then discarded
/// by the orchestrator); the spawner's own kill fires only on the SDK's forwarded
/// signal, after the graceful stdin-EOF window.
let runWith (dataDir: string) (backend: SandboxBackend) (credential: (string * string) option) : RunAgent =
    fun context capabilities signal onChunk ->
        async {
            let cli = Sandboxes.AgentSandbox.prepare backend dataDir credential
            // What this turn can call, assembled where every driver of a tool call assembles
            // it — the registry, then the audit, in that order and only once.
            let registry = registryFor capabilities
            let! outcome, usage =
                runQuery
                    context.SystemPrompt
                    (promptOf context)
                    // No choice is `None`, all the way down to the SDK option that is then
                    // not passed. The turn carries the choice rather than the runner holding
                    // one, so a person changing it changes the next turn and nothing else.
                    (context.Model |> Option.map ModelId.value)
                    registry
                    (cli.Env |> Map.toList |> List.map (fun (name, value) -> name ==> value) |> createObj)
                    (claudePath ())
                    cli.Spawner
                    signal.OnAbort
                    onChunk
            return
                match outcome with
                | Ok body -> AgentCompleted (body, Some usage)
                | Error reason -> AgentFailed (sdkFailureReason reason, Some usage)
        }

/// The ambient-credential runner over a given data directory (existing call sites and the
/// env fallback). The data dir is where the CLI's scratch HOME goes, so a caller that has no
/// launch of its own passes `Launch.unlaunched.DataDir` and says so by doing it.
let run (dataDir: string) (backend: SandboxBackend) : RunAgent = runWith dataDir backend None
