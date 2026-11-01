namespace Yession.Session

open Yession.App
open Yession.Domain.Tools

/// `show_surface`: the agent puts an A2UI surface in front of everybody in the session — a
/// question with choices, a short form — and keeps it current.
///
/// The agent writes the spec's own messages, so there is one vocabulary for a surface and the
/// catalog is the spec's rather than ours. What it may not write is a component the App does not
/// draw (`SurfaceView.draws`): the call is refused naming the ones it does, because a surface
/// people see with a hole in it is worse than an agent told to use another word.
///
/// The body only reads and judges. Where the surfaces live is the caller's (`apply`), so the
/// doc stays the Host's and this is testable without one.
module SurfaceTool =

    [<Literal>]
    let Name = "show_surface"

    /// The components the App draws, by the catalog's names, as the description lists them. A
    /// test pins this against `SurfaceView.draws`, which is what the refusal goes by.
    let drawnNames =
        [ "Text"; "Row"; "Column"; "List"; "Card"; "Divider"; "Button"; "TextField"; "CheckBox"; "ChoicePicker" ]

    /// A multiple-choice question, trimmed to what one needs: the surface, its components with a
    /// `root`, and the data model the choice binds to. In the description because an example is
    /// what a model copies from, and this one is checked by a test to apply cleanly.
    let example =
        """[{"version":"v0.9.1","createSurface":{"surfaceId":"pick","catalogId":"https://a2ui.org/specification/v0_9/catalogs/basic/catalog.json"}},
 {"version":"v0.9.1","updateComponents":{"surfaceId":"pick","components":[
  {"id":"root","component":"Column","children":["q","choice","go"]},
  {"id":"q","component":"Text","text":"Which branch should I rebase onto?"},
  {"id":"choice","component":"ChoicePicker","variant":"mutuallyExclusive","options":[{"label":"main","value":"main"},{"label":"release/2.4","value":"release/2.4"}],"value":{"path":"/choice"}},
  {"id":"go","component":"Button","child":"go_label","variant":"primary","action":{"event":{"name":"submit","context":{"choice":{"path":"/choice"}}}},"checks":[{"condition":{"call":"required","args":{"value":{"path":"/choice"}}},"message":"Pick a branch"}]},
  {"id":"go_label","component":"Text","text":"Rebase"}]}},
 {"version":"v0.9.1","updateDataModel":{"surfaceId":"pick","value":{"choice":[]}}}]"""

    let description =
        sprintf
            "Show the people here a small interface and keep it current: a question with choices, or a short form. Give A2UI v0.9.1 messages, in order. createSurface makes a surface. Its catalogId is \"%s\". updateComponents adds or replaces components by id. One component must have the id \"root\". updateDataModel sets the value at a JSON pointer. Without `value` it removes that value. deleteSurface takes the surface down. Do that when its question is settled. Everyone here sees the same surface, above the composer. An input binds to the data model with {\"path\": \"/name\"}. What one person picks or types, everyone sees. When a person presses a Button whose action is an event, and its checks pass, you get a new turn: the conversation says who responded, what they chose as they saw it, and the event name and its context. This session draws only these components: %s. A message with any other component is refused. The call applies all of its messages or none of them. A refusal says which message failed and why. A multiple-choice question, with the choice required before the button is ready:\n%s"
            A2ui.Codec.BasicCatalogId
            (String.concat ", " drawnNames)
            example

    /// Why a message does not apply, said so the agent can send the right one next.
    let private describe (error: A2ui.SurfaceError) : string =
        match error with
        | A2ui.AlreadyExists id ->
            sprintf "surface '%s' already exists. Change it with updateComponents or updateDataModel, or send deleteSurface first" id
        | A2ui.NotFound id -> sprintf "there is no surface '%s'. Send createSurface for it first" id
        | A2ui.UnknownCatalog catalog -> sprintf "this session does not draw catalog '%s'. Use \"%s\"" catalog A2ui.Codec.BasicCatalogId
        | A2ui.DataRefused (id, path, reason) -> sprintf "the data model of '%s' cannot take a value at %s: %s" id path reason

    /// The messages, read and judged one by one, or the first that is refused and why.
    let read (messages: string list) : Result<A2ui.ServerToClient list, string> =
        let judge (index: int) (text: string) =
            match A2ui.Codec.fromString A2ui.Codec.serverToClientDecoder text with
            | Error e -> Error (sprintf "message %d is not an A2UI v0.9.1 message: %s" (index + 1) e)
            | Ok (A2ui.UpdateComponents (_, components) as message) ->
                match components |> List.filter (fun c -> not (SurfaceView.draws c.Kind)) with
                | [] -> Ok message
                | undrawn ->
                    Error (
                        sprintf
                            "message %d uses %s, which this session does not draw. It draws %s"
                            (index + 1)
                            (undrawn |> List.map (fun c -> sprintf "%s (%s)" (SurfaceView.kindName c.Kind) c.Id) |> String.concat ", ")
                            (String.concat ", " drawnNames))
            | Ok message -> Ok message
        (Ok [], List.indexed messages)
        ||> List.fold (fun acc (index, text) ->
            match acc with
            | Error e -> Error e
            | Ok read -> judge index text |> Result.map (fun message -> read @ [ message ]))

    /// What a person's press records, read from the surfaces as they stand: the button's event
    /// and its context, or why it sends nothing (`A2ui.Surfaces.press`).
    let answer
        (surfaces: Map<string, A2ui.Surface>)
        (messageId: Yession.Domain.MessageId)
        (by: Yession.Domain.Principal)
        (surfaceId: string)
        (componentId: string)
        (scope: string)
        : Result<Yession.Domain.Surfaces.SurfaceActionTaken, string> =
        A2ui.Surfaces.press surfaces surfaceId componentId scope
        |> Result.map (fun (name, context) ->
            { Yession.Domain.Surfaces.SurfaceActionTaken.MessageId = messageId
              Yession.Domain.Surfaces.SurfaceActionTaken.SurfaceId = surfaceId
              Yession.Domain.Surfaces.SurfaceActionTaken.Component = componentId
              Yession.Domain.Surfaces.SurfaceActionTaken.Name = name
              Yession.Domain.Surfaces.SurfaceActionTaken.Context =
                A2ui.Codec.toString A2ui.Codec.encodeJsonValue (A2ui.JObject context)
              Yession.Domain.Surfaces.SurfaceActionTaken.Responses = A2ui.Project.responses surfaces surfaceId componentId scope
              Yession.Domain.Surfaces.SurfaceActionTaken.By = by })

    /// The tool, over whatever holds the surfaces.
    let tool
        (apply: A2ui.ServerToClient list -> Result<unit, A2ui.SurfaceError>)
        : ToolDescriptor * (string -> Async<Result<ToolAnswer, string>>) =
        AgentTools.tool
            Name
            description
            (ToolArgs.objects "messages" "the A2UI v0.9.1 messages, in the order they apply, each an object with `version` and one message")
            (fun messages ->
                async {
                    let said =
                        match messages with
                        | [] -> "nothing to show. Give at least one message"
                        | messages ->
                            match read messages with
                            | Error refusal -> sprintf "REFUSED, and nothing changed: %s" refusal
                            | Ok read ->
                                match apply read with
                                | Error error -> sprintf "REFUSED, and nothing changed: %s" (describe error)
                                | Ok () -> sprintf "applied %d message%s. Everyone here sees the result" read.Length (if read.Length = 1 then "" else "s")
                    return Ok (ToolAnswer.text said)
                })
