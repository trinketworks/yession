module EmitInBindingFixture.Binding

open Fable.Core

/// The same emits EmitInProductFixture is reported for, in a project that is a binding. No line
/// here is marked `// YES011`, so none may be reported.

[<Emit("typeof BUILD_VERSION !== 'undefined' ? BUILD_VERSION : 'dev'")>]
let current: string = jsNative

[<Emit("$0.close()")>]
let close (handle: obj) : unit = jsNative

type Socket =
    [<EmitMethod("send")>]
    abstract Send: string -> unit

    [<EmitProperty("readyState")>]
    abstract ReadyState: int

    [<EmitIndexer>]
    abstract Item: string -> string with get, set

type SocketClass =
    [<EmitConstructor>]
    abstract Create: url: string -> Socket
