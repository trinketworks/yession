module Yession.BindingOnlyFixture.Product

open Fable.Core
open Fable.Core.JsInterop

/// Every way a JavaScript object is built by hand, and every nullable type, declared where the
/// product can reach them. A line marked `// YES013` MUST be reported; every other line MUST NOT
/// be. `lint` reads those markers and compares them to what the analyzer said, in both directions.
///
/// The marker sits where the rule reports: on the `createObj` that starts the call, on a `==>` that
/// is not inside one, and on the `[<AllowNullLiteral>]` attribute.

// `createObj`, over pairs written either way.

let headers = createObj [ "content-type", box "text/plain" ] // YES013

let options = createObj [ "recursive" ==> true; "force" ==> true ] // YES013

let empty = createObj [] // YES013

// Reported once for the object, at the call that starts it, however many keys it names.

let spread =
    createObj // YES013
        [ "width" ==> 390
          "height" ==> 844
          "mobile" ==> true ]

// Handed on through a pipe, and nested inside a value.

let piped = [ "a", box 1 ] |> createObj // YES013

let nested =
    createObj // YES013
        [ "outer" ==> createObj [ "inner" ==> 1 ] ] // YES013

// A pair written alone is its own site: it is a key spelled by hand, whatever it is handed to.

let pair = "enabled" ==> true // YES013

// Nullable types: an interface, a class, one with its attribute on a line of its own, and one
// nested in a module.

type [<AllowNullLiteral>] Socket = // YES013
    abstract close: unit -> unit

[<AllowNullLiteral>] // YES013
type Timer() =
    member _.Stop() = ()

module Inner =
    [<AllowNullLiteral>] // YES013
    type Handle =
        abstract id: int

// What the rule has no opinion about. An interface of keys filled through `jsOptions` is the
// remedy, and it is clean.

type MkdirOptions =
    abstract recursive: bool with get, set

let mkdirOptions = jsOptions<MkdirOptions> (fun o -> o.recursive <- true)

// A type that is not nullable, with other attributes on it.

[<Sealed>]
type Plain() =
    member _.Value = 1

type Wire =
    abstract send: string -> unit

// A function that happens to be NAMED `createObj`, or `==>`, is not Fable's. The rule matches
// the function, not the spelling.

module Own =
    let createObj (pairs: (string * int) list) : Map<string, int> = Map.ofList pairs

    let (==>) (key: string) (value: int) = key, value

    let counts = createObj [ "a" ==> 1; "b" ==> 2 ]

// Plain F#.

let greeting name = sprintf "hello %s" name
