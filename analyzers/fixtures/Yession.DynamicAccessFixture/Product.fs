module Yession.DynamicAccessFixture.Product

open Fable.Core
open Fable.Core.JsInterop

/// Every way a member is reached by name, written where the product can reach it. A line marked
/// `// YES015` MUST be reported; every other line MUST NOT be. `lint` reads those markers and
/// compares them to what the analyzer said, in both directions.
///
/// The marker sits where the rule reports: on the line the `?` or `?<-` starts on.

[<Import("statSync", "node:fs")>]
let private stat (path: string) : obj = jsNative

// A member read, a member set, a member named by a value, and a method called through one.

let size (o: obj) : int = o?size // YES015

let rename (o: obj) = o?name <- "renamed" // YES015

let byKey (o: obj) (key: string) : int = o?(key) // YES015

let close (o: obj) : unit = o?close () // YES015

// A member of what a call answered, and a member of a member: one line each, however many `?`
// it holds.

let isDirectory (path: string) : bool = (stat path)?isDirectory () // YES015

let chained (o: obj) : bool = o?first?second // YES015

// Handed on through a pipe, inside a lambda, and on a line of its own in a longer expression.

let piped (o: obj) : string = o |> fun x -> x?label // YES015

let mapped (items: obj list) : int list = items |> List.map (fun item -> item?count) // YES015

let spread (o: obj) =
    let width: int =
        o?width // YES015

    width * 2

// What the rule has no opinion about. An optional parameter is a `?` in a signature, not the
// operator.

type Widget() =
    member _.Resize(?width: int) = defaultArg width 0

let resized = Widget().Resize(width = 3)

// A `?` inside a string is text.

let question = "is it? o?size"

// An operator of the same spelling, declared here, is not Fable's. The rule matches the
// function, not the spelling.

module Own =
    let (?) (counts: Map<string, int>) (key: string) : int = counts.[key]

    let counts = Map.ofList [ "a", 1 ]

    let a = counts?a

// A member reached through a declared type is the remedy, and it is clean.

type Stats =
    abstract isDirectory: unit -> bool

let typed (s: Stats) : bool = s.isDirectory ()
