namespace Yession.App.Codecs

open Yession.Domain
open Yession.Codecs

open Yession.Domain.Chat
open Yession.Domain.Sandboxes
open Yession.Domain.Files
open Yession.Domain.Artifacts
open Yession.Domain.Content
open Yession.Domain.Repos
open Yession.Domain.Prs
open System
open Yession.Domain.Agent
open Yession.Domain.Link
open Yession.Domain.Terminals
open Yession.Domain.Tools
open System.Globalization
#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

/// The read surface the App draws from the Session: the query stream and its declarations,
/// the model catalogue, and the connection panels. The App renders a query it has never heard
/// of from what crosses here, so the App owns the shape.
[<RequireQualifiedAccess>]
module Reads =

    let queryName : Codec<QueryName> =
        { Encode = QueryName.value >> Encode.string
          Decode = Codec.viaSmartCtor QueryName.create Decode.string }

    let private queryColumn : Codec<QueryColumn> =
        { Encode =
            fun (c: QueryColumn) -> Encode.object [ "key", Encode.string c.Key; "label", Encode.string c.Label ]
          Decode =
            Decode.object (fun get ->
                { Key = get.Required.Field "key" Decode.string
                  Label = get.Required.Field "label" Decode.string }) }

    let private queryShape : Codec<QueryShape> =
        { Encode =
            (fun shape ->
                match shape with
                | Value -> Encode.object [ "kind", Encode.string "value" ]
                | Fields columns ->
                    Encode.object [ "kind", Encode.string "fields"; "columns", Encode.list (columns |> List.map queryColumn.Encode) ]
                | Rows columns ->
                    Encode.object [ "kind", Encode.string "rows"; "columns", Encode.list (columns |> List.map queryColumn.Encode) ])
          Decode =
            Decode.field "kind" Decode.string
            |> Decode.andThen (function
                | "value" -> Decode.succeed Value
                | "fields" -> Decode.field "columns" (Decode.list queryColumn.Decode) |> Decode.map Fields
                | "rows" -> Decode.field "columns" (Decode.list queryColumn.Decode) |> Decode.map Rows
                | other -> Decode.fail (sprintf "Unknown query shape: %s" other)) }

    /// A tone as one word. Spelled out rather than numbered so the stream stays readable
    /// to anything consuming it without this codec, which is the same reason a cell rides
    /// as its native JSON type below.
    let private queryTone : Codec<QueryTone> =
        { Encode = QueryTone.name >> Encode.string
          Decode =
            Decode.string
            |> Decode.andThen (fun raw ->
                match QueryTone.parse raw with
                | Some tone -> Decode.succeed tone
                | None -> Decode.fail (sprintf "Unknown query tone: %s" raw)) }

    /// A cell rides as its NATIVE JSON type — a string, a bool, or null — rather than as a
    /// tagged object, which keeps the payload readable to anything that consumes the stream
    /// without this codec.
    ///
    /// A toned cell is the one that cannot: it carries two facts, so it takes an object.
    /// It is tried LAST, after the three native forms, because `oneOf` takes the first
    /// decoder that succeeds and a bare string must stay a `CellText` — the object form is
    /// the only shape none of the others can claim.
    let private queryCell : Codec<QueryCell> =
        { Encode =
            (fun cell ->
                match cell with
                | CellText text -> Encode.string text
                | CellFlag flag -> Encode.bool flag
                | CellStatus (text, tone) ->
                    Encode.object [ "text", Encode.string text; "tone", queryTone.Encode tone ]
                | CellAbsent -> Encode.nil)
          Decode =
            Decode.oneOf
                [ Decode.string |> Decode.map CellText
                  Decode.bool |> Decode.map CellFlag
                  Decode.nil CellAbsent
                  Decode.map2
                      (fun text tone -> CellStatus (text, tone))
                      (Decode.field "text" Decode.string)
                      (Decode.field "tone" queryTone.Decode) ] }

    let private queryRow : Codec<(string * QueryCell) list> =
        { Encode = fun row -> Encode.object (row |> List.map (fun (key, cell) -> key, queryCell.Encode cell))
          Decode = Decode.keyValuePairs queryCell.Decode }

    let queryValue : Codec<QueryValue> =
        { Encode =
            (fun value ->
                match value with
                | ValueOf cell -> Encode.object [ "kind", Encode.string "value"; "value", queryCell.Encode cell ]
                | FieldsOf fields -> Encode.object [ "kind", Encode.string "fields"; "fields", queryRow.Encode fields ]
                | RowsOf rows ->
                    Encode.object [ "kind", Encode.string "rows"; "rows", Encode.list (rows |> List.map queryRow.Encode) ])
          Decode =
            Decode.field "kind" Decode.string
            |> Decode.andThen (function
                | "value" -> Decode.field "value" queryCell.Decode |> Decode.map ValueOf
                | "fields" -> Decode.field "fields" queryRow.Decode |> Decode.map FieldsOf
                | "rows" -> Decode.field "rows" (Decode.list queryRow.Decode) |> Decode.map RowsOf
                | other -> Decode.fail (sprintf "Unknown query value: %s" other)) }

    let queryDef : Codec<QueryDef> =
        { Encode =
            (fun (def: QueryDef) ->
                Encode.object
                    [ "name", queryName.Encode def.Name
                      "title", Encode.string def.Title
                      "description", Encode.string def.Description
                      "shape", queryShape.Encode def.Shape
                      // A pair per entry rather than an object, because the order is the
                      // reading order and an object's keys are not ordered on the wire.
                      "legend",
                      Encode.list (
                          def.Legend
                          |> List.map (fun (shape, meaning) ->
                              Encode.list [ Encode.string shape; Encode.string meaning ])) ])
          Decode =
            Decode.object (fun get ->
                { Name = get.Required.Field "name" queryName.Decode
                  Title = get.Required.Field "title" Decode.string
                  Description = get.Required.Field "description" Decode.string
                  Shape = get.Required.Field "shape" queryShape.Decode
                  // Optional: a page still open from before queries could carry one reads
                  // the declarations again on its next connection, and until then a
                  // missing legend is a query without one rather than a broken frame.
                  Legend =
                    get.Optional.Field "legend" (Decode.list (Decode.list Decode.string))
                    |> Option.defaultValue []
                    |> List.choose (function [ shape; meaning ] -> Some (shape, meaning) | _ -> None) }) }

    /// One frame of the multiplexed query stream. Tagged like the App's `Frames.session` for
    /// the same reason: one connection carries every query there will ever be, and a client
    /// folds each frame by name into a map.
    let queryFrame : Codec<QueryFrame> =
        { Encode =
            (fun frame ->
                match frame with
                | QueriesDeclared defs ->
                    Encode.object [ "tag", Encode.string "queries"; "queries", Encode.list (defs |> List.map queryDef.Encode) ]
                | QueryValued (name, value) ->
                    Encode.object
                        [ "tag", Encode.string "value"
                          "name", queryName.Encode name
                          "value", queryValue.Encode value ])
          Decode =
            Decode.field "tag" Decode.string
            |> Decode.andThen (function
                | "queries" -> Decode.field "queries" (Decode.list queryDef.Decode) |> Decode.map QueriesDeclared
                | "value" ->
                    Decode.map2
                        (fun name value -> QueryValued (name, value))
                        (Decode.field "name" queryName.Decode)
                        (Decode.field "value" queryValue.Decode)
                | other -> Decode.fail (sprintf "Unknown query frame: %s" other)) }

    let modelId : Codec<ModelId> =
        { Encode = ModelId.value >> Encode.string
          Decode = Codec.viaSmartCtor ModelId.create Decode.string }

    let agentModel : Codec<AgentModel> =
        { Encode =
            (fun (model: AgentModel) ->
                Encode.object
                    [ "id", modelId.Encode model.Id
                      "name", Encode.string model.Name ])
          Decode =
            // `map2`, not the getter API, and the difference is not style. Thoth's
            // `Decode.object` does NOT short-circuit: a required field that is missing
            // stashes the error, hands the builder `Unchecked.defaultof<_>` — null — and
            // runs it anyway, reporting only afterwards. A builder that merely STORES that
            // value is fine, which is why every record literal here is; this one called
            // `AgentModel.create`, which reads the id back out (`ModelId.value`, for the
            // name's fallback), so a model entry with no `id` dereferenced null and THREW
            // where a decode should have refused.
            //
            // `map2` applies the constructor only once both parts have decoded, which is
            // the guarantee the getter cannot make. A decoder that calls a function on a
            // required field's value wants a combinator, not a getter.
            Decode.map2
                AgentModel.create
                (Decode.field "id" modelId.Decode)
                (Decode.optional "name" Decode.string |> Decode.map Option.toObj) }

    /// The catalogue as the session serves it to a picker. An OBJECT around the list rather
    /// than a bare array, so the reply has somewhere to grow — a provider's default, a
    /// deprecation note — without every reader needing a new shape on the same day.
    let modelCatalogue : Codec<AgentModel list> =
        { Encode = (fun models -> Encode.object [ "models", Encode.list (models |> List.map agentModel.Encode) ])
          Decode = Decode.field "models" (Decode.list agentModel.Decode) }

    /// One connection as a panel row reads it. The kind crosses as the word a person is
    /// shown, which is `ConnectionKind`'s to say — here and on the screen alike.
    let private credentialRow : Codec<Access.CredentialRow> =
        { Encode =
            fun (row: Access.CredentialRow) ->
                Encode.object
                    [ "kind", Encode.string (Access.ConnectionKind.label row.Kind)
                      "signInRequired", Encode.option Encode.string row.SignInRequired ]
          Decode =
            Decode.object (fun get ->
                // REQUIRED, and the distinction is the rule's: a row with no `kind` at all is
                // a malformed row and fails, while a kind this build does not KNOW is a word
                // `ofLabel` has an answer for. Defaulting the missing one to `""` made the
                // two the same thing and called it static.
                { Access.CredentialRow.Kind =
                    get.Required.Field "kind" Decode.string |> Access.ConnectionKind.ofLabel
                  Access.CredentialRow.SignInRequired = get.Optional.Field "signInRequired" Decode.string }) }

    /// Who the shared scope belongs to, as the wire spells it. The words are the ones this
    /// surface has always used; what changed is that a reader must now answer for both.
    let private sharedOwner : Codec<Access.SharedOwner> =
        { Encode =
            (fun owner ->
                Encode.string (
                    match owner with
                    | Access.OwnedByUser -> "user"
                    | Access.OwnedByDeployment -> "local"))
          Decode =
            Decode.string
            |> Decode.andThen (function
                | "user" -> Decode.succeed Access.OwnedByUser
                | "local" -> Decode.succeed Access.OwnedByDeployment
                // Not defaulted to either: one of them promises a credential is private
                // and the other that it is shared, and a build that cannot tell which must
                // not pick. The panel is refused; the drawer keeps what it had.
                | other -> Decode.fail (sprintf "unknown shared-scope owner '%s'" other)) }

    /// The Claude panel as the session serves it.
    ///
    /// `models` is decoded as a value and run through `modelCatalogue` SEPARATELY, rather
    /// than inline where its failure would fail the whole reply: a catalogue this build
    /// cannot read is a reason to show in the picker, and it must not also take the
    /// connection rows down with it. `modelsUnavailable` is why there is none — the same
    /// null-or-reason shape as `signInRequired`, and never both.
    let claudePanel : Codec<Access.ClaudePanel> =
        { Encode =
            fun (panel: Access.ClaudePanel) ->
                Encode.object
                    [ "session", Encode.option credentialRow.Encode panel.SessionCredential
                      "mine", Encode.option credentialRow.Encode panel.MineCredential
                      "owner", sharedOwner.Encode panel.Owner
                      "agent", Encode.bool panel.AgentAvailable
                      "models",
                      (match panel.Models with
                       | Access.ModelsLoaded models -> modelCatalogue.Encode models
                       | Access.ModelsUnknown
                       | Access.ModelsUnavailable _ -> Encode.nil)
                      "modelsUnavailable",
                      (match panel.Models with
                       | Access.ModelsUnavailable reason -> Encode.string reason
                       | Access.ModelsUnknown
                       | Access.ModelsLoaded _ -> Encode.nil) ]
          Decode =
            Decode.object (fun get ->
                { Access.ClaudePanel.SessionCredential = get.Optional.Field "session" credentialRow.Decode
                  Access.ClaudePanel.MineCredential = get.Optional.Field "mine" credentialRow.Decode
                  // Required, both: a panel says these or it is not a panel. "Not told yet"
                  // is the absence of a panel, which a client spells by holding one as an
                  // option — never by a panel that arrived saying nothing.
                  Access.ClaudePanel.Owner = get.Required.Field "owner" sharedOwner.Decode
                  Access.ClaudePanel.AgentAvailable = get.Required.Field "agent" Decode.bool
                  Access.ClaudePanel.Models =
                    match get.Optional.Field "models" Decode.value with
                    | Some raw ->
                        match Decode.fromValue "$.models" modelCatalogue.Decode raw with
                        | Ok models -> Access.ModelsLoaded models
                        | Error reason -> Access.ModelsUnavailable reason
                    | None ->
                        match get.Optional.Field "modelsUnavailable" Decode.string with
                        | Some reason -> Access.ModelsUnavailable reason
                        // Neither: a session process answering the rows alone.
                        | None -> Access.ModelsUnknown }) }

    /// The GitHub panel as the session serves it: the same two rows, and nothing this
    /// provider has no answer for.
    let githubPanel : Codec<Access.GitHubPanel> =
        { Encode =
            fun (panel: Access.GitHubPanel) ->
                Encode.object
                    [ "session", Encode.option credentialRow.Encode panel.SessionCredential
                      "mine", Encode.option credentialRow.Encode panel.MineCredential
                      "owner", sharedOwner.Encode panel.Owner ]
          Decode =
            Decode.object (fun get ->
                { Access.GitHubPanel.SessionCredential = get.Optional.Field "session" credentialRow.Decode
                  Access.GitHubPanel.MineCredential = get.Optional.Field "mine" credentialRow.Decode
                  Access.GitHubPanel.Owner = get.Required.Field "owner" sharedOwner.Decode }) }

    /// One frame of the session's read stream. Tagged, for `queryFrame`'s reason: one
    /// connection carries every read model, and a client folds each frame by what it is.
    let readFrame : Codec<Tools.ReadFrame> =
        { Encode =
            (fun frame ->
                match frame with
                | Tools.Queried inner ->
                    Encode.object [ "kind", Encode.string "query"; "frame", queryFrame.Encode inner ]
                | Tools.Panels (claude, github) ->
                    Encode.object
                        [ "kind", Encode.string "panels"
                          "claude", claudePanel.Encode claude
                          "github", githubPanel.Encode github ])
          Decode =
            Decode.field "kind" Decode.string
            |> Decode.andThen (function
                | "query" -> Decode.field "frame" queryFrame.Decode |> Decode.map Tools.Queried
                | "panels" ->
                    Decode.map2
                        (fun claude github -> Tools.Panels (claude, github))
                        (Decode.field "claude" claudePanel.Decode)
                        (Decode.field "github" githubPanel.Decode)
                | other -> Decode.fail (sprintf "unknown read frame '%s'" other)) }
