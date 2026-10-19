module Yession.UncheckedCastFixture.Product

open Fable.Core
open Fable.Core.JsInterop

/// Every cast that asserts a type and checks nothing, written where the product can reach it,
/// and every cast the rule leaves alone. A line marked `// YES016` MUST be reported; every other
/// line MUST NOT be. `lint` reads those markers and compares them to what the analyzer said, in
/// both directions.
///
/// The marker sits where the rule reports: on the line the cast starts on.

type Buffer =
    abstract toString: unit -> string

type Element =
    abstract tagName: string

type HtmlElement =
    inherit Element
    abstract focus: unit -> unit

// `unbox`, with its type written, inferred, and handed on through a pipe.

let text (value: obj) : string = unbox<string> value // YES016

let count (value: obj) : int = unbox value // YES016

let piped (value: obj) : float = value |> unbox // YES016

let mapped (values: obj list) : int list = values |> List.map unbox // YES016

// `unbox` of a value that has a type already: it is still an assertion about what it is.

let bytesOf (body: U2<string, Buffer>) : Buffer = unbox<Buffer> body // YES016

// Fable's `!!`, from `obj` and from a declared type alike.

let bang (value: obj) : string = !!value // YES016

let retyped (value: string) : int = !!value // YES016

// A downcast from `obj`, with `:?>` and with `downcast`. It is `unbox` spelt differently.

let down (value: obj) : string = value :?> string // YES016

let inferredDown (value: obj) : Buffer = downcast value // YES016

let spread (value: obj) =
    let element =
        value :?> Element // YES016

    element.tagName

// What the rule has no opinion about. A value built in the same expression is the one being
// asserted about, and the author is holding it.

type Double() =
    member _.toString() = "double"

let double () : Buffer = unbox (Double ())

let literal: int = unbox 3

let record: Buffer = unbox {| toString = fun () -> "record" |}

// A downcast from a declared type: the DOM's own shape, where a hierarchy is all there is.

let narrow (element: Element) : HtmlElement = element :?> HtmlElement

// A downcast from what a listener is handed, or from what an event reached. Which `Event` a
// listener gets is decided by a string, and what `target` is by whatever was clicked, so these
// are reported even though `Event` and `EventTarget` are declared types.

let pressed (event: Browser.Types.Event) : string =
    (event :?> Browser.Types.KeyboardEvent).key // YES016

let reached (event: Browser.Types.Event) : string =
    (event.target :?> Browser.Types.Element).tagName // YES016

let on (event: Browser.Types.Event) : Browser.Types.HTMLElement =
    downcast event.currentTarget // YES016

// Within the DOM's element hierarchy, the downcast is still the author's to judge.

let held (element: Browser.Types.Element) : Browser.Types.HTMLElement = element :?> Browser.Types.HTMLElement

// A type test checks, and the compiler writes downcasts of its own for one.

let describe (value: obj) : string =
    match value with
    | :? string as s -> s
    | _ -> "other"

let failure (error: exn) : string =
    match error with
    | :? System.InvalidOperationException as invalid -> invalid.Message
    | _ -> "other"

// Structural equality, and a `for` loop's enumerator, are downcasts the compiler writes.

type Point = { X: int; Y: int }

let total (points: Point list) : int =
    let mutable sum = 0

    for point in points do
        sum <- sum + point.X

    sum

// A cast that is deliberate says so on its line, and is left alone.

let justified (body: U2<string, Buffer>) : Buffer = unbox<Buffer> body // fsharpanalyzer: ignore-line YES016

// A function named `unbox`, or `!!`, declared here is not the library's.

module Own =
    let unbox (value: int) : int = value + 1

    let (!!) (value: int) : int = value * 2

    let both = unbox (!!1)

// Plain F#: `box`, and an upcast.

let boxed: obj = box 1

let widened (element: HtmlElement) : Element = element :> Element
