module DynamicAccessInBindingFixture.Binding

open Fable.Core
open Fable.Core.JsInterop

/// The same constructs Yession.DynamicAccessFixture is reported for, in a project that is a
/// binding: reaching a member by name is how a binding reaches the thing it binds. No line here
/// is marked `// YES015`, so none may be reported.

[<Import("statSync", "node:fs")>]
let private stat (path: string) : obj = jsNative

let size (o: obj) : int = o?size

let rename (o: obj) = o?name <- "renamed"

let byKey (o: obj) (key: string) : int = o?(key)

let close (o: obj) : unit = o?close ()

let isDirectory (path: string) : bool = (stat path)?isDirectory ()

let chained (o: obj) : bool = o?first?second

let piped (o: obj) : string = o |> fun x -> x?label

let mapped (items: obj list) : int list = items |> List.map (fun item -> item?count)
