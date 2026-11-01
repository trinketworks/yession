namespace A2ui

/// One surface as a client holds it: what `createSurface` fixed, each component as the latest
/// `updateComponents` left it, and the data model.
type Surface =
    { CatalogId : string
      Theme : Theme option
      SendDataModel : bool
      Components : Map<string, Component>
      DataModel : Json }

/// Why a message does not apply to the surfaces there are.
type SurfaceError =
    | AlreadyExists of surfaceId: string
    | NotFound of surfaceId: string
    /// `createSurface` named a catalog this binding does not render.
    | UnknownCatalog of catalogId: string
    | DataRefused of surfaceId: string * path: string * reason: string

// The spec's message processing (`a2ui_protocol.md`, "Envelope message structure" and "Data
// model updates") as a pure fold. A component may name children that have not arrived and a
// binding may name data that is not there yet; the spec calls that progressive rendering, and
// it is the renderer's to show, so it is not an error here. Neither is a surface without a
// `root` yet.

[<RequireQualifiedAccess>]
module Surfaces =

    let empty : Map<string, Surface> = Map.empty

    let private within (surfaces: Map<string, Surface>) (id: string) (change: Surface -> Result<Surface, SurfaceError>) =
        match Map.tryFind id surfaces with
        | Some surface -> change surface |> Result.map (fun s -> Map.add id s surfaces)
        | None -> Error (NotFound id)

    /// The surfaces after one message, or why it does not apply.
    let apply (surfaces: Map<string, Surface>) (message: ServerToClient) : Result<Map<string, Surface>, SurfaceError> =
        match message with
        | CreateSurface (id, _, _, _) when Map.containsKey id surfaces -> Error (AlreadyExists id)
        | CreateSurface (_, catalog, _, _) when catalog <> Codec.BasicCatalogId -> Error (UnknownCatalog catalog)
        | CreateSurface (id, catalog, theme, send) ->
            Ok (
                Map.add
                    id
                    { CatalogId = catalog
                      Theme = theme
                      SendDataModel = defaultArg send false
                      Components = Map.empty
                      DataModel = JObject [] }
                    surfaces
            )
        | UpdateComponents (id, components) ->
            within surfaces id (fun s -> Ok { s with Components = (s.Components, components) ||> List.fold (fun m c -> Map.add c.Id c m) })
        | UpdateDataModel (id, path, value) ->
            let path = defaultArg path "/"
            within surfaces id (fun s ->
                match value with
                | Some v ->
                    Pointer.set path v s.DataModel
                    |> Result.map (fun model -> { s with DataModel = model })
                    |> Result.mapError (fun reason -> DataRefused (id, path, reason))
                | None -> Ok { s with DataModel = Pointer.remove path s.DataModel })
        | DeleteSurface id when Map.containsKey id surfaces -> Ok (Map.remove id surfaces)
        | DeleteSurface id -> Error (NotFound id)

    /// What pressing button `componentId` of surface `surfaceId` tells the server: the event's
    /// name and its context, read from the data model under `scope` (the template item the
    /// button was drawn under, `""` at the root). Refused when the button is not there, calls a
    /// function locally rather than telling the server, or has a check that fails — the spec
    /// does not send an event from a button whose checks fail.
    let press
        (surfaces: Map<string, Surface>)
        (surfaceId: string)
        (componentId: string)
        (scope: string)
        : Result<string * (string * Json) list, string> =
        match Map.tryFind surfaceId surfaces with
        | None -> Error (sprintf "there is no surface '%s'" surfaceId)
        | Some surface ->
            match Map.tryFind componentId surface.Components with
            | Some { Kind = ComponentKind.Button (_, _, action, checks) } ->
                let at = { Model = surface.DataModel; Base = scope }
                match action with
                | Action.LocalCall _ -> Error (sprintf "button '%s' calls a function here; it sends nothing" componentId)
                | Action.Event (name, context) ->
                    match Evaluate.failing at checks with
                    | Error reason -> Error reason
                    | Ok (_ :: _ as unmet) -> Error (String.concat " " unmet)
                    | Ok [] -> Evaluate.context at context |> Result.map (fun read -> name, read)
            | Some _ -> Error (sprintf "'%s' on surface '%s' is not a button" componentId surfaceId)
            | None -> Error (sprintf "surface '%s' has no component '%s'" surfaceId componentId)

    /// The surfaces after each message in turn, or why the first that does not apply does not.
    let applyAll (surfaces: Map<string, Surface>) (messages: ServerToClient list) : Result<Map<string, Surface>, SurfaceError> =
        (Ok surfaces, messages) ||> List.fold (fun acc m -> acc |> Result.bind (fun s -> apply s m))
