module ObjSignatureInBindingFixture.Binding

/// The same signatures Yession.ObjSignatureFixture is reported for, in a project that is a
/// binding: the edge where a value of no declared type is given one. No line here is marked
/// `// YES017`, so none may be reported.

/// A binding's own name for what it hands over, which abbreviates `obj`. A product signature
/// that uses it is not reported: the name is this binding's to define (Thoth's `JsonValue` is
/// the real one).
type Json = obj

let take (value: obj) : string = string value

let give (name: string) : obj = box name

let inArray (values: obj array) : int = values.Length

let inFunction (handler: obj -> unit) : unit = handler (box 1)

type Shape =
    abstract read: unit -> obj
    abstract write: obj -> unit

type Holder(value: obj) =
    member _.Value: obj = value
