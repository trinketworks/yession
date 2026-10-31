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

    /// The surfaces after each message in turn, or why the first that does not apply does not.
    let applyAll (surfaces: Map<string, Surface>) (messages: ServerToClient list) : Result<Map<string, Surface>, SurfaceError> =
        (Ok surfaces, messages) ||> List.fold (fun acc m -> acc |> Result.bind (fun s -> apply s m))
