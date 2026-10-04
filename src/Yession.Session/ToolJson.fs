namespace Yession.Session

open Yession.Domain
open Yession.Domain.Tools

// The agent's tools as JSON: the schema a descriptor carries, what of a call's arguments
// may be recorded, and the audit wrapper that records it. The descriptors and the log are
// the domain's; reading and writing their JSON is the Session's, which serves them.

#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

module ToolSchema =

    /// The fields as a JSON Schema object, as text. Text because that is what crosses every
    /// boundary this travels — the MCP wire, an external server's `tools/list`, the SDK's
    /// tool builder — and reshaping it at each one would be three chances to lose a keyword
    /// we did not know mattered.
    let ofFields (fields: ToolField list) : string =
        let property (field: ToolField) =
            [ yield "type", Encode.string field.Type
              match field.Items with
              | Some item -> yield "items", Encode.object [ "type", Encode.string item ]
              | None -> ()
              if field.Description <> "" then yield "description", Encode.string field.Description
              if field.Secret then yield "writeOnly", Encode.bool true ]
            |> Encode.object
        let required = fields |> List.filter (fun f -> f.Required) |> List.map (fun f -> Encode.string f.Key)
        Encode.object
            [ "type", Encode.string "object"
              "properties", Encode.object (fields |> List.map (fun f -> f.Key, property f))
              "required", Encode.list required
              "additionalProperties", Encode.bool false ]
        |> Encode.toString 0

    /// No arguments at all — what a query takes, and what `list_secrets` takes.
    let none : string = ofFields []

/// A tool's arguments, declared ONCE. The schema field the model reads and the getter the
/// body decodes with are built by the same call, so a key cannot be spelled one way in one
/// and another way in the other. They used to be two lists kept in step by hand — a
/// `ToolField` per argument beside the descriptor, a decoder per tool somewhere above it —
/// and `terminal` was once missing from both at once with nothing to say so.
[<RequireQualifiedAccess>]
type ToolArgs<'a> = { Fields : ToolField list; Read : Decode.IGetters -> 'a }

[<RequireQualifiedAccess>]
module ToolArgs =

    let map (f: 'a -> 'b) (input: ToolArgs<'a>) : ToolArgs<'b> =
        { ToolArgs.Fields = input.Fields; ToolArgs.Read = input.Read >> f }

    let zip (first: ToolArgs<'a>) (second: ToolArgs<'b>) : ToolArgs<'a * 'b> =
        { ToolArgs.Fields = first.Fields @ second.Fields
          ToolArgs.Read = fun get -> first.Read get, second.Read get }

    /// No arguments at all — what a query takes, and what `list_secrets` takes.
    let none : ToolArgs<unit> = { ToolArgs.Fields = []; ToolArgs.Read = ignore }

    /// A required string.
    let text (key: string) (description: string) : ToolArgs<string> =
        { ToolArgs.Fields = [ ToolField.required key "string" description ]
          ToolArgs.Read = fun get -> get.Required.Field key Decode.string }

    /// A required string whose value is never recorded: `writeOnly`, which is what
    /// `ToolArguments` redacts by.
    let secret (key: string) (description: string) : ToolArgs<string> =
        { ToolArgs.Fields = [ ToolField.secret key description ]
          ToolArgs.Read = fun get -> get.Required.Field key Decode.string }

    /// An optional string. An empty one is absent: a model that computed a value and got
    /// nothing has not asked for "", and a tool handed "" would refuse it later and less
    /// clearly.
    let textOption (key: string) (description: string) : ToolArgs<string option> =
        { ToolArgs.Fields = [ ToolField.optional key "string" description ]
          ToolArgs.Read = fun get -> get.Optional.Field key Decode.string |> Option.filter (fun s -> s <> "") }

    /// An optional integer.
    let integerOption (key: string) (description: string) : ToolArgs<int option> =
        { ToolArgs.Fields = [ ToolField.optional key "integer" description ]
          ToolArgs.Read = fun get -> get.Optional.Field key Decode.int }

    /// An optional number, and what an absent one means.
    let number (key: string) (description: string) (otherwise: float) : ToolArgs<float> =
        { ToolArgs.Fields = [ ToolField.optional key "number" description ]
          ToolArgs.Read = fun get -> get.Optional.Field key Decode.float |> Option.defaultValue otherwise }

    /// An optional boolean, false when absent: every flag a tool takes is an opt-in.
    let flag (key: string) (description: string) : ToolArgs<bool> =
        { ToolArgs.Fields = [ ToolField.optional key "boolean" description ]
          ToolArgs.Read = fun get -> get.Optional.Field key Decode.bool |> Option.defaultValue false }

    /// An optional list of strings, empty when absent.
    let textList (key: string) (description: string) : ToolArgs<string list> =
        { ToolArgs.Fields = [ ToolField.optionalList key "string" description ]
          ToolArgs.Read = fun get -> get.Optional.Field key (Decode.list Decode.string) |> Option.defaultValue [] }

    /// The JSON Schema a descriptor carries.
    let schema (input: ToolArgs<'a>) : string = ToolSchema.ofFields input.Fields

    /// One call's arguments. Blank is the empty object: a client calling a tool that has no
    /// required arguments may send nothing at all.
    let read (input: ToolArgs<'a>) (json: string) : Result<'a, string> =
        let json = if System.String.IsNullOrWhiteSpace json then "{}" else json
        Decode.fromString (Decode.object input.Read) json
        |> Result.mapError (sprintf "could not read the arguments: %s")

/// `toolArgs { let! a = … and! b = … return … }`. Applicative only — there is no `Bind` —
/// so no argument can depend on another's value, which is what lets every one of them be
/// in the schema before any call has been read.
type ToolArgsBuilder () =
    member _.BindReturn (input: ToolArgs<'a>, f: 'a -> 'b) : ToolArgs<'b> = ToolArgs.map f input
    member _.MergeSources (first: ToolArgs<'a>, second: ToolArgs<'b>) : ToolArgs<'a * 'b> = ToolArgs.zip first second

[<AutoOpen>]
module ToolArgsSyntax =
    let toolArgs = ToolArgsBuilder ()

module ToolArguments =

    /// The fields a schema marks `writeOnly: true`. JSON Schema's own keyword, chosen over
    /// a custom marker because its meaning — goes in, never comes back out — is exactly the
    /// property being relied on, and because it stays intelligible to any MCP client that
    /// reads the schema.
    ///
    /// A schema this cannot read is an `Error`, never an empty list: "no field is secret" is
    /// the one answer that lets every value through, so it is not available as a fallback.
    let secretFields (schema: string) : Result<string list, string> =
        let property = Decode.object (fun get -> get.Optional.Field "writeOnly" Decode.bool |> Option.defaultValue false)
        Decode.fromString (Decode.field "properties" (Decode.keyValuePairs property)) schema
        |> Result.map (List.filter snd >> List.map fst)

    /// What may be recorded of one call's arguments.
    ///
    /// This is a REBUILD, not a mask: the recorded object is constructed field by field and
    /// a secret field's value is simply never copied into it. "Masked on write" would still
    /// route the plaintext through the logging path, where one bug prints it; here there is
    /// no write path left to get wrong.
    ///
    /// The trade-off, stated rather than hidden: an allowlist would fail SAFE (an unmarked
    /// field is never recorded) and a marker fails OPEN (an unmarked field is recorded).
    /// The marker is chosen anyway because an allowlist is a parallel list of field names —
    /// rename an argument and the entry silently stops protecting the thing it named. What
    /// the marker may NOT also do is fail open on a schema it cannot read: that records
    /// nothing, as a foreign tool's call does, rather than everything.
    let redact (schema: string) (arguments: string) : string option =
        match secretFields schema, Decode.fromString (Decode.keyValuePairs Decode.value) arguments with
        | Error _, _
        | _, Error _ -> None
        | Ok secrets, Ok fields ->
            fields
            |> List.map (fun (key, value) ->
                if List.contains key secrets then key, Encode.nil else key, value)
            |> Encode.object
            |> Encode.toString 0
            |> Some

module ToolUseLog =


    /// How many notes one answer carries before it stops listing them. A call that set off a
    /// dozen things has told the agent what it needs by the fourth; the rest are in the
    /// conversation, which is where a turn reads the whole of anything.
    [<Literal>]
    let private NotesShown = 4

    /// The notes, as the sentence appended to an answer — or nothing at all, which is the
    /// ordinary case: most calls set nothing else off.
    let internal describeNotes (notes: string list) : string =
        match notes with
        | [] -> ""
        | notes ->
            let shown = notes |> List.truncate NotesShown
            let more =
                match List.length notes - List.length shown with
                | 0 -> ""
                | n -> sprintf "; and %d more, in the session" n
            sprintf "\n[while this ran: %s%s]" (String.concat "; " shown) more

    /// Wrap a registry so every call through it is recorded. Applied ONCE, to the merged
    /// registry, which is what makes "the same way" true rather than aspirational: a
    /// provider added later cannot arrive with its own logging, or without any.
    /// How much of a non-block answer a chip keeps. Small on purpose: this is a preview a
    /// reader expands, not the record the agent reads — the agent already has the whole
    /// answer in its own transcript. A tool whose answer is genuinely large (`repo_diff`)
    /// caps itself and points at a terminal for the rest; this is the backstop under that.
    let private resultCap = 4000

    let private cappedResult (text: string) : string =
        if text.Length <= resultCap then text
        else text.Substring (0, resultCap) + sprintf "\n… (%d more characters — read the full answer in a terminal)" (text.Length - resultCap)

    let wrap (log: ToolUseLog) (registry: ToolRegistry) : ToolRegistry =
        { registry with
            Invoke =
              fun call ->
                async {
                    // Whether this is one of OUR tools decides two things the same way: an
                    // argument value may only be recorded from a schema we wrote, and — below
                    // — a result may only be disclosed from a body we wrote. A foreign call is
                    // trusted for neither.
                    let ours =
                        match ToolRegistry.tryFind call registry with
                        | Some descriptor -> not descriptor.Foreign
                        | None -> false
                    let recorded =
                        if ours then
                            match ToolRegistry.tryFind call registry with
                            | Some descriptor -> ToolArguments.redact descriptor.InputSchema call.Arguments
                            | None -> None
                        else None
                    let! handle =
                        log.Started { Namespace = call.Namespace; Name = call.Name; Arguments = recorded }
                    // A tool body that throws is still a call that happened, and an audit
                    // that loses exactly the calls that went worst is worse than none.
                    let! answer =
                        async {
                            try
                                return! registry.Invoke call
                            with e ->
                                return Error e.Message
                        }
                    match handle with
                    | None -> return answer
                    | Some id ->
                        let ending =
                            match answer with
                            | Ok answer ->
                                // Disclosed only when this chip is the only place to read the
                                // answer: a call that became a block has its output on the
                                // block's chip already, and a foreign call's result is no more
                                // ours to broadcast than its arguments were to record.
                                let result =
                                    match answer.Block with
                                    | Some _ -> None
                                    | None when not ours -> None
                                    | None -> Some (cappedResult answer.Text)
                                { Outcome = ToolCallOk; Block = answer.Block; Result = result }
                            | Error reason -> { Outcome = ToolCallFailed reason; Block = None; Result = None }
                        do! log.Finished id ending
                        // Only onto an answer that HAPPENED. A refusal is about the call, and
                        // appending the session's unrelated news to it would make a reader
                        // hunt for the connection there is not one of.
                        match answer with
                        | Error _ -> return answer
                        | Ok answered ->
                            let! notes = log.Noted id
                            match describeNotes notes with
                            | "" -> return answer
                            | said -> return Ok { answered with Text = answered.Text + said }
                } }
