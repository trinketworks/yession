namespace Yession.Domain.Hooks

#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

/// What a session asks the Manager to send it, when something out on the internet posts to
/// one of the Manager's hook endpoints.
///
/// The Manager relays hooks it cannot read. It verifies that a delivery is signed, matches
/// it against the filters sessions have declared, and forwards it — and at no point does it
/// learn which service sent it, what the event means, or what any field is called. The
/// knowledge stays in the session that declared the filter, which is the only place that
/// has the credential to act on it anyway.
///
/// That is what makes this a FILTER rather than a predicate: a session ships the Manager
/// data, never code. Code would have to be interpreted, the Manager would have to implement
/// every construct the code could contain, and a session upgraded to emit a new one would
/// stop working against an older Manager — making the Manager a version ceiling on the
/// sessions it supervises, which is the opposite of why it is kept ignorant.
///
/// So the language is made small enough to have no versions at all: a conjunction of
/// equalities. Every operator that could be added — a disjunction, a negation, a pattern —
/// is one more thing two builds can disagree about, and equality is the one that cannot be
/// read two ways. There is nothing here to extend, and that is the feature.

/// A path into a delivery, as dotted segments.
///
/// A delivery is addressed as ONE document — `headers.x-github-event` and
/// `body.repository.full_name` are the same kind of path — so the language needs no second
/// form for "look in the headers". Header names are matched lowercased, because HTTP does
/// not promise a case and nothing downstream should have to care.
///
/// Private, so a path cannot exist without having been parsed. A segment containing a dot
/// is not addressable; no provider this serves has one, and inventing an escape now would
/// be a syntax to disagree about later.
type FieldPath = private FieldPath of string list

/// What a session will accept from an endpoint: every one of these must hold.
///
/// An empty `Where` matches every delivery to that endpoint, which is the honest reading of
/// "no constraints" and not a special case.
type DeliveryFilter = { Where : (FieldPath * string) list }

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

module FieldPath =

    let segments (FieldPath path) = path

    /// The path as it travels and as it reads: `body.repository.full_name`.
    let render (FieldPath path) = String.concat "." path

    let create (raw: string) : Result<FieldPath, string> =
        let trimmed = raw.Trim ()
        if trimmed = "" then Error "a field path is empty"
        elif trimmed |> Seq.exists System.Char.IsWhiteSpace then
            Error (sprintf "field path %s contains whitespace" trimmed)
        else
            let parts = trimmed.Split '.' |> List.ofArray
            if parts |> List.exists (fun segment -> segment = "") then
                Error (sprintf "field path %s has an empty segment" trimmed)
            else Ok (FieldPath (parts |> List.map (fun s -> s.ToLowerInvariant ())))

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

module DeliveryFilter =

    /// Accepts everything on the endpoint it is declared against.
    let everything : DeliveryFilter = { Where = [] }

    /// Does this delivery satisfy every constraint?
    ///
    /// `lookup` is a FUNCTION rather than a parsed document, which is what keeps the rule
    /// itself a fold over a list and leaves it nothing to say about how a document is read.
    /// `Delivery.resolve` is the reader a relay hands it; a path that is absent, or that
    /// names a container rather than a value, answers `None` and fails its constraint —
    /// never matching by accident is the direction that matters here.
    let matches (filter: DeliveryFilter) (lookup: FieldPath -> string option) : bool =
        filter.Where |> List.forall (fun (path, expected) -> lookup path = Some expected)
