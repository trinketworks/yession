module BindingOnlyInBindingFixture.Binding

open Fable.Core
open Fable.Core.JsInterop

/// The same constructs Yession.BindingOnlyFixture is reported for, in a project that is a binding.
/// No line here is marked `// YES013`, so none may be reported.

let headers = createObj [ "content-type", box "text/plain" ]

let options = createObj [ "recursive" ==> true; "force" ==> true ]

let empty = createObj []

let spread =
    createObj
        [ "width" ==> 390
          "height" ==> 844
          "mobile" ==> true ]

let piped = [ "a", box 1 ] |> createObj

let pair = "enabled" ==> true

type [<AllowNullLiteral>] Socket =
    abstract close: unit -> unit

[<AllowNullLiteral>]
type Timer() =
    member _.Stop() = ()

module Inner =
    [<AllowNullLiteral>]
    type Handle =
        abstract id: int
