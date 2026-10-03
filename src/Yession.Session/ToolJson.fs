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

module ToolArguments =

    /// The fields a schema marks `writeOnly: true`. JSON Schema's own keyword, chosen over
    /// a custom marker because its meaning — goes in, never comes back out — is exactly the
    /// property being relied on, and because it stays intelligible to any MCP client that
    /// reads the schema.
    let secretFields (schema: string) : string list =
        let property = Decode.object (fun get -> get.Optional.Field "writeOnly" Decode.bool |> Option.defaultValue false)
        match Decode.fromString (Decode.field "properties" (Decode.keyValuePairs property)) schema with
        | Ok fields -> fields |> List.filter snd |> List.map fst
        | Error _ -> []

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
    /// rename an argument and the entry silently stops protecting the thing it named.
    let redact (schema: string) (arguments: string) : string option =
        let secrets = secretFields schema
        match Decode.fromString (Decode.keyValuePairs Decode.value) arguments with
        | Error _ -> None
        | Ok fields ->
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
