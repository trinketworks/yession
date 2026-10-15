module Yession.ObjSignatureFixture.Signatures

/// Every signature with `obj` in it, declared where the product can reach it, and every
/// declaration the rule leaves alone. A line marked `// YES017` MUST be reported; every other
/// line MUST NOT be. `lint` reads those markers and compares them to what the analyzer said, in
/// both directions.
///
/// The marker sits where the rule reports: on the declaration's name.

// A function's parameter and its answer, written and inferred.

let take (value: obj) : string = string value // YES017

let give (name: string) : obj = box name // YES017

let inferred (name: string) = box name // YES017

// Anywhere inside the type: an array's element, a tuple's, a function's argument, a generic's.

let inArray (values: obj array) : int = values.Length // YES017

let inTuple (pair: string * obj) : string = fst pair // YES017

let inFunction (handler: obj -> unit) : unit = handler (box 1) // YES017

let inGeneric (fields: Map<string, obj>) : int = fields.Count // YES017

let nested (answer: unit -> Result<obj list, string>) : bool = Result.isOk (answer ()) // YES017

let spread // YES017
    (first: string)
    (second: obj)
    : string =
    first + string second

// Members: an abstract slot, a method, a property, a constructor's parameter, a static member.

type Shape =
    abstract read: unit -> obj // YES017
    abstract write: obj -> unit // YES017
    abstract current: obj // YES017

type Holder(value: obj) = // YES017
    member _.Value: obj = value // YES017
    member _.Replace(next: obj) : Holder = Holder next // YES017
    static member Empty(placeholder: obj) : Holder = Holder placeholder // YES017

// In a module at any depth, private or not.

module Inner =
    let private hidden (value: obj) : string = string value // YES017

    let shown = hidden (box 1)

// An alias the product declares is what it stands for, so it does not hide one.

type Untyped = obj

let viaAlias (value: Untyped) : string = string value // YES017

// What the rule has no opinion about. A value is not a signature: a lock object, a tag.

let gate = obj ()

let private tag: obj = box "tag"

// A local is not one either.

let local () : string =
    let held: obj = box 1
    string held

// A type parameter is what a value whose type does not matter here is.

let echo (value: 'value) : 'value = value

let typed (value: string) : string = value

// An override's signature, and an interface implementation's, belong to the type declaring them.

type Counted(count: int) =
    member _.Count = count

    override _.Equals(other: obj) = obj.ReferenceEquals (other, null)

    override _.GetHashCode() = count

    interface System.IComparable with
        member _.CompareTo(other: obj) = 0

// A binding's own name for what it hands over is that name, whatever it abbreviates.

let fromBinding (value: ObjSignatureInBindingFixture.Binding.Json) : string = string value

// Record and union fields are a type's shape, not a signature.

type Bag = { Contents: obj }

type Slot =
    | Filled of obj
    | Empty
