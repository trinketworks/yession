namespace Yession.Manager

open Yession.Domain.Hooks

#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

// The Manager's reading of a hook delivery. The filter a session declares is domain data
// (`DeliveryFilter`, a conjunction of equalities over `FieldPath`s); reading the request it
// is matched against is the relay's job, and the relay is the Manager's, so the one place a
// delivery's JSON is parsed lives here and the domain never sees a JSON value.

/// A delivery as a path addresses it: ONE document, whose halves are the request's headers
/// and its body.
///
/// The two halves have different shapes — headers are names to strings, a body is whatever
/// JSON arrived — and the type is what says so, rather than a pair of bags passed side by
/// side and kept in step by whoever remembers. The first segment of a path is what picks a
/// half, so "a delivery is one document" is a rule this type holds rather than a convention
/// its callers observe.
///
/// Private, so a delivery cannot exist without having been read: a body that is not a JSON
/// object addresses nothing, and `create` is where that is settled once, not at every
/// lookup.
type Delivery =
    private
        { /// Names lowercased, because HTTP does not promise a case.
          Headers : Map<string, string>
          /// Known to be a JSON object — nothing else reaches this field.
          Body : JsonValue }

module Delivery =

    /// A body is addressable only if it is an object: an array, a bare value and invalid
    /// JSON all address nothing, and there is no partial answer to give about one.
    let private anObject : Decoder<JsonValue> = Decode.keys |> Decode.andThen (fun _ -> Decode.value)

    /// What a segment resolves to when it lands on a value: a string answers itself, a
    /// number or a boolean answers its own rendering, and anything else — a container, a
    /// JSON `null` — answers nothing. A constraint is an equality between strings, so a
    /// value that has no string is a value no constraint can name.
    let private aValue : Decoder<string option> =
        Decode.oneOf
            [ Decode.string |> Decode.map Some
              Decode.bool |> Decode.map (fun value -> Some (if value then "true" else "false"))
              Decode.float |> Decode.map (fun value -> Some (string value))
              Decode.succeed None ]

    /// Walk the remaining segments of a path through a JSON value.
    ///
    /// Keys are compared case-insensitively against the already-lowercased segment, so a
    /// provider's camelCase body resolves without a session having to know its casing; the
    /// cost is that two keys in one object differing only by case are not distinguishable,
    /// and no provider this serves has a pair like that.
    let rec private walk (segments: string list) : Decoder<string option> =
        match segments with
        | [] -> aValue
        | segment :: rest ->
            Decode.oneOf
                [ Decode.keys
                  |> Decode.andThen (fun keys ->
                      match keys |> List.tryFind (fun key -> key.ToLowerInvariant () = segment) with
                      | None -> Decode.succeed None
                      | Some key -> Decode.field key (walk rest))
                  // Not an object, so the path runs off the end of the document.
                  Decode.succeed None ]

    /// Read a delivery. `None` when the body is not a JSON object, which is the one thing
    /// that makes a delivery unreadable rather than merely unmatched — a relay answers that
    /// with a 400 and never with a lookup that quietly finds nothing.
    let create (headers: (string * string) list) (body: string) : Delivery option =
        match Decode.fromString anObject body with
        | Error _ -> None
        | Ok parsed ->
            Some
                { Headers = headers |> List.map (fun (name, value) -> name.ToLowerInvariant (), value) |> Map.ofList
                  Body = parsed }

    /// Resolve one path against a delivery.
    ///
    /// The first segment names which half — `headers` or `body` — and anything else
    /// addresses nothing, which is what makes "a delivery is one document" a rule rather
    /// than a convention.
    ///
    /// A path that lands on a container, or on nothing, answers `None` — which fails its
    /// constraint. Matching by accident is the direction that would hurt.
    let resolve (delivery: Delivery) (path: FieldPath) : string option =
        match FieldPath.segments path with
        // A header value is a string, so a name is the whole of the path: `headers` alone
        // lands on the half itself, and anything past a name lands inside a string.
        | [ "headers"; name ] -> delivery.Headers |> Map.tryFind name
        | "body" :: rest ->
            match Decode.fromValue "$" (walk rest) delivery.Body with
            | Ok found -> found
            | Error _ -> None
        | _ -> None
