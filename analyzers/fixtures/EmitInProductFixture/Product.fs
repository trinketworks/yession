module EmitInProductFixture.Product

open Fable.Core

/// Every member of the Emit family, declared where the product can reach it. A line marked
/// `// YES011` MUST be reported; every other line MUST NOT be. `lint` reads those markers and
/// compares them to what the analyzer said, in both directions.
///
/// The marker sits on the ATTRIBUTE, which is where the rule reports: it is the declaration
/// that is in the wrong project, and the attribute is what makes it one.

// The raw macro, on a module-level value and on a function.

[<Emit("typeof BUILD_VERSION !== 'undefined' ? BUILD_VERSION : 'dev'")>] // YES011
let current: string = jsNative

[<Emit("$0.close()")>] // YES011
let close (handle: obj) : unit = jsNative

// The named variants. None has a macro to get wrong, and each is still a binding to an API
// this repository does not own.

type Socket =
    [<EmitMethod("send")>] // YES011
    abstract Send: string -> unit

    [<EmitProperty("readyState")>] // YES011
    abstract ReadyState: int

    [<EmitIndexer>] // YES011
    abstract Item: string -> string with get, set

type SocketClass =
    [<EmitConstructor>] // YES011
    abstract Create: url: string -> Socket

// Not an emit: an import names a module and an export and carries no JavaScript. It is how
// product code reaches a platform module that has no binding project of its own.

[<Import("existsSync", "node:fs")>]
let existsSync (path: string) : bool = jsNative

// Plain F# is never this rule's business, and neither is calling a binding.

let greet (socket: Socket) = socket.Send "hello"

let deadline (socket: Socket) = Yession.EmitInDomainFixture.Domain.later (float socket.ReadyState)
