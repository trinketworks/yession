module UncheckedCastInBindingFixture.Binding

open Fable.Core
open Fable.Core.JsInterop

/// The same casts Yession.UncheckedCastFixture is reported for, in a project that is a binding:
/// the edge where a value of no declared type is given one. No line here is marked `// YES016`,
/// so none may be reported.

type Buffer =
    abstract toString: unit -> string

let text (value: obj) : string = unbox<string> value

let count (value: obj) : int = unbox value

let piped (value: obj) : float = value |> unbox

let bytesOf (body: U2<string, Buffer>) : Buffer = unbox<Buffer> body

let bang (value: obj) : string = !!value

let down (value: obj) : string = value :?> string

let inferredDown (value: obj) : Buffer = downcast value
