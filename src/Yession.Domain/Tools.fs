namespace Yession.Domain.Tools

open Yession.Domain
open Yession.Domain.Terminals

// The agent's tools, as DATA (Plan 16, part A).
//
// They used to be code: twelve `sdk.tool(...)` calls written out inside one `[<Emit>]`
// template literal, a runner taking one positional callback per tool, and a literal list of
// wire names beside them that had to be kept in step by hand. Adding a tool cost an edit in
// three places and a parameter on a function that already had eighteen; adding a tool at RUN
// time — which is what a session-declared MCP server is — was not expensive, it was
// impossible.
//
// So a tool becomes a descriptor plus one dispatch function. The runner builds the SDK's
// tools in a loop, computes the auto-approve list from the same descriptors, and never
// learns what any individual tool does. Everything downstream of that — a proxied external
// server, a tool-use record that names where a call went — is then an ordinary consumer of
// the same two values.

/// One argument of a tool, in the small vocabulary the agent's tools actually use.
///
/// A field rather than raw schema text because the schema is READ as well as written: the
/// redactor walks it to decide what may be recorded, so a hand-rolled string per tool would
/// be a second place for that decision to live.
type ToolField =
    { Key : string
      /// A JSON Schema primitive: "string", "boolean", "number", "integer" — or "array",
      /// in which case `Items` says what it is an array of.
      Type : string
      /// The element type, when `Type` is "array". `None` for everything else.
      Items : string option
      /// What the model reads to decide what to put here.
      Description : string
      Required : bool
      /// Emitted as JSON Schema's own `writeOnly: true` — the field goes in and never comes
      /// back out. It is what marks an argument as secret, and it is the ONLY place that is
      /// declared: a list of field names kept beside the schema would silently stop matching
      /// the moment an argument was renamed.
      Secret : bool }

module ToolField =

    let required (key: string) (fieldType: string) (description: string) : ToolField =
        { Key = key; Type = fieldType; Items = None; Description = description; Required = true; Secret = false }

    let optional (key: string) (fieldType: string) (description: string) : ToolField =
        { Key = key; Type = fieldType; Items = None; Description = description; Required = false; Secret = false }

    /// An optional array argument, e.g. `forward: ["github"]`.
    let optionalList (key: string) (itemType: string) (description: string) : ToolField =
        { Key = key
          Type = "array"
          Items = Some itemType
          Description = description
          Required = false
          Secret = false }

    /// A required argument whose VALUE must never be recorded.
    let secret (key: string) (description: string) : ToolField =
        { Key = key; Type = "string"; Items = None; Description = description; Required = true; Secret = true }

/// A tool the agent may call, and enough about it to build the call site.
///
/// `Namespace` is what lets two providers both offer `list` without colliding, and what a
/// reader of the tool-use record sees so they can tell WHERE a call went. It is also the
/// name of the SDK MCP server the tool is mounted on, which is why the wire name a model
/// sees carries it for free.
type ToolDescriptor =
    { Namespace : string
      Name : string
      Description : string
      /// Raw JSON Schema, passed through untouched. Ours is built by `ToolSchema`; an
      /// external server's arrives already written and is never rewritten.
      InputSchema : string
      /// MCP's own `readOnlyHint`. Declared rather than inferred, so identifying the
      /// read-only tools of a THIRD-PARTY server needs no yession-specific convention.
      ReadOnly : bool
      /// A human title, when there is one worth showing beside the name.
      Title : string option
      /// Did somebody else write this schema? Redaction is schema-driven, and a schema we
      /// did not write cannot be trusted to mark its own secrets — so a foreign tool's
      /// argument VALUES are not recorded at all. The record still carries the namespace,
      /// the tool and the outcome, which is the part that answers "what did the agent just
      /// do".
      Foreign : bool }

module ToolDescriptor =

    let create (ns: string) (name: string) (description: string) (schema: string) : ToolDescriptor =
        { Namespace = ns
          Name = name
          Description = description
          InputSchema = schema
          ReadOnly = false
          Title = None
          Foreign = false }

    /// A tool somebody else declared — an external MCP server's, arriving already written.
    let foreign (ns: string) (name: string) (description: string) (schema: string) : ToolDescriptor =
        { create ns name description schema with Foreign = true }

    /// The name the model calls and the audit records: the SDK's `mcp__<server>__<tool>`,
    /// where the server IS the namespace. Existing tools keep the names they already had,
    /// which is what makes part A a refactor rather than a wire change.
    let wireName (descriptor: ToolDescriptor) : string =
        sprintf "mcp__%s__%s" descriptor.Namespace descriptor.Name

/// One invocation: which tool, and its arguments as the JSON object the model produced.
///
/// Arguments stay JSON text rather than being decoded into a per-tool record, because the
/// two things that must happen to every call — recording it, and redacting what may not be
/// recorded — are schema-driven and generic. A tool body decodes its own arguments; nothing
/// on the path to it has to know their shape.
[<RequireQualifiedAccess>]
type ToolCall =
    { Namespace : string
      Name : string
      Arguments : string }

/// What a tool answered: the text the model reads, and — when the call became one — the
/// block it produced.
///
/// The block is here rather than dug out of the text because the audit record needs it and
/// nothing else can supply it: `execute_command` is the only party that knows a call turned
/// into something the chat already draws, and a record that could not say so would draw a
/// second chip beside the block's own.
type ToolAnswer =
    { Text : string
      Block : BlockId option
      /// A byte stream the provider offered along with its answer (Plan 19), already
      /// admitted by whoever decoded it. `None` for every in-process tool: the session's own
      /// verbs run in the session, and a stream is by definition somebody else's.
      Stream : StreamOffer option }

module ToolAnswer =

    let text (value: string) : ToolAnswer = { Text = value; Block = None; Stream = None }

/// Invoke a tool. ONE function, so an in-process tool and a proxied one are
/// indistinguishable to the caller — which is what makes a single audit seam possible
/// rather than one per implementation.
///
/// `Error` is reserved for the call not happening at all (no such tool, unreadable
/// arguments). A tool that ran and failed answers `Ok` with text saying so: the model is
/// meant to read that and choose differently, not to see a protocol error.
type InvokeTool = ToolCall -> Async<Result<ToolAnswer, string>>

/// What a turn may call, and how. The whole of the agent's authority surface, as a value.
type ToolRegistry =
    { Tools : ToolDescriptor list
      Invoke : InvokeTool }

module ToolRegistry =

    let empty : ToolRegistry =
        { Tools = []
          Invoke = fun call -> async { return Error (sprintf "no tool '%s/%s'" call.Namespace call.Name) } }

    /// Every tool's wire name — the SDK's auto-approve list, computed from the descriptors
    /// instead of maintained beside them. The list and the tools cannot disagree because
    /// there is only one of them.
    let allowedTools (registry: ToolRegistry) : string list =
        registry.Tools |> List.map ToolDescriptor.wireName

    /// The namespaces in play, in first-seen order — one SDK MCP server each.
    let namespaces (registry: ToolRegistry) : string list =
        registry.Tools |> List.map (fun t -> t.Namespace) |> List.distinct

    let private has (call: ToolCall) (tools: ToolDescriptor list) =
        tools |> List.exists (fun t -> t.Namespace = call.Namespace && t.Name = call.Name)

    /// The descriptor a call names, if the registry has it. The audit seam's one lookup:
    /// the schema is what decides what may be recorded.
    let tryFind (call: ToolCall) (registry: ToolRegistry) : ToolDescriptor option =
        registry.Tools |> List.tryFind (fun t -> t.Namespace = call.Namespace && t.Name = call.Name)

    /// A registry over one namespace: descriptors, and a dispatch that only ever sees calls
    /// belonging to it.
    let ofNamespace (ns: string) (tools: ToolDescriptor list) (invoke: InvokeTool) : ToolRegistry =
        let tools = tools |> List.map (fun t -> { t with Namespace = ns })
        { Tools = tools
          Invoke =
            fun call ->
                if has call tools then invoke call
                else async { return Error (sprintf "no tool '%s/%s'" call.Namespace call.Name) } }

    /// Combine two registries, REFUSING a collision rather than resolving one.
    ///
    /// Silently letting one shadow the other would mean the model's tool list and what
    /// actually runs could differ, and the difference would only show as the wrong thing
    /// happening. Namespaces exist so this is rare; when it is not, the session says which
    /// name is doubled and declines to start.
    let merge (first: ToolRegistry) (second: ToolRegistry) : Result<ToolRegistry, string> =
        let collisions =
            second.Tools
            |> List.filter (fun t -> first.Tools |> List.exists (fun e -> e.Namespace = t.Namespace && e.Name = t.Name))
            |> List.map ToolDescriptor.wireName
        match collisions with
        | [] ->
            Ok
                { Tools = first.Tools @ second.Tools
                  Invoke =
                    fun call ->
                        if has call first.Tools then first.Invoke call
                        elif has call second.Tools then second.Invoke call
                        else async { return Error (sprintf "no tool '%s/%s'" call.Namespace call.Name) } }
        | doubled ->
            Error (sprintf "two tools claim %s" (doubled |> String.concat ", "))

    let mergeAll (registries: ToolRegistry list) : Result<ToolRegistry, string> =
        registries
        |> List.fold
            (fun acc registry ->
                match acc with
                | Error e -> Error e
                | Ok combined -> merge combined registry)
            (Ok empty)

/// What the audit seam is told when a call starts. The turn and the handle are NOT here:
/// the log is bound to a turn, and minting the handle is the Session's job — the
/// same rule `MessageId` and `BlockId` already follow.
[<RequireQualifiedAccess>]
type ToolUseBegin =
    { Namespace : string
      Name : string
      /// Already redacted, or `None` when nothing of the arguments may be recorded.
      Arguments : string option }

/// …and when it ends.
type ToolUseEnd =
    { Outcome : ToolOutcome
      Block : BlockId option
      /// The answer text a chip may disclose, already capped — set only for a call that
      /// succeeded, drew its own chip (no `Block`), and is one of OUR tools. See
      /// `ToolUseFinished.Result` for why those are the conditions.
      Result : string option }

/// The audit seam: ONE service, injected into every path a tool call can take, so the
/// session's own tools and a proxied external server are recorded the same way. Different
/// implementations may answer a call; only one thing records it.
///
/// Two moments rather than one, mirroring `TerminalBlockStarted`/`TerminalBlockCompleted`:
/// the start is what anchors the call in the chat, so a four-minute call is visible while
/// it is the only thing happening, and the finish moves what it says without moving where
/// it sits.
type ToolUseLog =
    { /// Record that a call started, and answer with the handle it will be addressed by.
      /// `None` means nothing is being recorded, and `Finished` is then never called.
      Started : ToolUseBegin -> Async<ToolUseId option>
      Finished : ToolUseId -> ToolUseEnd -> Async<unit>
      /// What the SESSION recorded while this call was running, said the way the timeline
      /// says it — the act notes appended by somebody OTHER than the agent between the call
      /// starting and finishing.
      ///
      /// This is how a turn learns a consequence of its own call that no verb could name.
      /// `add_repo` brings up whatever sandboxes the checkout declares, and it must not say
      /// so: a verb that named them would be a repo verb carrying sandbox vocabulary, and the
      /// next subsystem to acquire a consequence would want a line in it too. The consequence
      /// announces itself instead, here, once, for every verb there will ever be.
      ///
      /// Between turns the same notes reach the agent as conversation. That is not enough on
      /// its own and the gap is measured: a turn's context is built ONCE, and in a real
      /// session the pack was assembled 7.7 seconds before the sandbox it needed came up, so
      /// twenty-eight calls ran without it. A tool answer is the only thing that reaches a
      /// turn already running.
      Noted : ToolUseId -> Async<string list> }

module ToolUseLog =

    /// Records nothing — for turns nobody is watching (tests, one-shots).
    let none : ToolUseLog =
        { Started = fun _ -> async { return None }
          Finished = fun _ _ -> async { return () }
          Noted = fun _ -> async { return [] } }
