namespace A2ui

// The data model's JSON Pointers (RFC 6901), read and written as the spec's conformance suite
// (`conformance/core/data_model.yaml`) pins them:
//
//   * empty segments collapse, so `//a///b/` is `/a/b`, and `/` and `` are the root;
//   * a read that finds nothing is `None`, never an error, whatever the path;
//   * a write makes the containers it is missing, a list where the next segment is an index and
//     an object where it is not, and writes through a `null` as through nothing;
//   * a write through a string, number or boolean, or into a list by a segment that is not an
//     index, is refused. An index has no leading zero (RFC 6901 §4).
//
// `Json` has no `undefined`, so the slot a list removal leaves, and the slots a write past the
// end pads, hold `null`.

[<RequireQualifiedAccess>]
module Pointer =

    /// The highest list index a write may make. A write is the agent's or a user's, and a write
    /// at a far index pads the list out to it, so the bound is on memory, not on taste. The
    /// suite pins the number.
    [<Literal>]
    let MaxIndex = 10000

    type private Segment =
        | Key of string
        | Index of int
        /// An index past what an `int` holds: a list never has it, and a write refuses it. An
        /// object can still have it as a key.
        | FarIndex of string

    let private segment (raw: string) : Segment =
        let key = raw.Replace("~1", "/").Replace ("~0", "~")
        let digits = key <> "" && key |> Seq.forall (fun c -> c >= '0' && c <= '9')
        if not digits || (key.Length > 1 && key.[0] = '0') then Key key
        elif key.Length > 9 then FarIndex key
        else Index (int key)

    let private name (s: Segment) =
        match s with
        | Key k | FarIndex k -> k
        | Index i -> string i

    let private segments (pointer: string) : Segment list =
        pointer.Split '/' |> Array.filter ((<>) "") |> Array.map segment |> List.ofArray

    /// `path` as read from under `scope`: an absolute path as it is, a relative one (no leading
    /// `/`, the spec's template paths) appended to `scope`.
    let resolve (scope: string) (path: string) : string =
        if path.StartsWith "/" then path else scope.TrimEnd '/' + "/" + path

    let rec private getIn (path: Segment list) (json: Json) : Json option =
        match path, json with
        | [], _ -> Some json
        | (Key k | FarIndex k) :: rest, JObject fields -> fields |> List.tryFind (fun (key, _) -> key = k) |> Option.bind (snd >> getIn rest)
        | Index i :: rest, JArray items -> List.tryItem i items |> Option.bind (getIn rest)
        | _ -> None

    /// The value at `pointer`, if there is one.
    let get (pointer: string) (model: Json) : Json option = getIn (segments pointer) model

    let rec private setIn (path: Segment list) (value: Json) (node: Json option) : Result<Json, string> =
        match path with
        | [] -> Ok value
        | s :: rest ->
            let intoObject (fields: (string * Json) list) (key: string) =
                let existing = fields |> List.tryFind (fun (k, _) -> k = key) |> Option.map snd
                setIn rest value existing
                |> Result.map (fun v ->
                    match existing with
                    | Some _ -> JObject (fields |> List.map (fun (k, old) -> if k = key then k, v else k, old))
                    | None -> JObject (fields @ [ key, v ]))
            let intoArray (items: Json list) =
                match s with
                | Index i when i <= MaxIndex ->
                    setIn rest value (List.tryItem i items)
                    |> Result.map (fun v ->
                        if i < List.length items then JArray (items |> List.mapi (fun j old -> if j = i then v else old))
                        else JArray (items @ List.replicate (i - List.length items) JNull @ [ v ]))
                | Index _ | FarIndex _ -> Error (sprintf "a list index is %d at most" MaxIndex)
                | Key k -> Error (sprintf "a list takes an index, not the non-numeric segment '%s'" k)
            match node, s with
            | Some (JObject fields), _ -> intoObject fields (name s)
            | Some (JArray items), _ -> intoArray items
            | (None | Some JNull), Key k -> intoObject [] k
            | (None | Some JNull), _ -> intoArray []
            | Some _, _ -> Error (sprintf "Cannot set path through '%s': the value before it is not an object or a list" (name s))

    /// The model with `value` at `pointer`, making the containers on the way.
    let set (pointer: string) (value: Json) (model: Json) : Result<Json, string> =
        setIn (segments pointer) value (Some model)

    let rec private removeIn (path: Segment list) (json: Json) : Json =
        match path, json with
        | [ s ], JObject fields -> JObject (fields |> List.filter (fun (key, _) -> key <> name s))
        | [ Index i ], JArray items -> JArray (items |> List.mapi (fun j old -> if j = i then JNull else old))
        | s :: rest, JObject fields ->
            let k = name s
            JObject (fields |> List.map (fun (key, v) -> if key = k then key, removeIn rest v else key, v))
        | Index i :: rest, JArray items -> JArray (items |> List.mapi (fun j v -> if j = i then removeIn rest v else v))
        | _ -> json

    /// The model without the value at `pointer`. Removing what is not there changes nothing;
    /// removing the root leaves an empty object.
    let remove (pointer: string) (model: Json) : Json =
        match segments pointer with
        | [] -> JObject []
        | path -> removeIn path model
