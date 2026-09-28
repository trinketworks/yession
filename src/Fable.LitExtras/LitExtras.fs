module Fable.LitExtras

// What a Lit `TemplateResult` is at runtime, for the one reader here that takes one apart
// rather than handing it to Lit: the Host's server-side renderer (`app/Ssr.fs`).
//
// `Fable.Lit` types a template as an empty interface, and a hole's value as whatever the
// caller put there — which at runtime is ANY JavaScript value: text, a number, a nested
// template, an array or a lazy sequence of more of them, a listener, one of lit's sentinels.
// A renderer has to ask which, and asking is a question about JavaScript kinds that only a
// binding can answer honestly. So it is asked HERE, once per hole, and the answer leaves as a
// closed union the renderer matches on; nothing past this file sees the value untyped.
//
// Fable-only: `dotnet build` type-checks it; Fable emits the JS that runs.

open Fable.Core
open Lit

/// One hole's value as it sits in a template, before anybody has asked what it is. Opaque: the
/// one thing to do with it is `Hole.classify`.
type Value =
    interface end

/// A template taken apart: its static markup, and the value in each hole between two parts.
/// There is always one more static part than there are holes.
[<RequireQualifiedAccess>]
type Parts =
    { Statics : string array
      Values : Value array }

/// What one hole holds, by the kind of JavaScript value it is.
[<RequireQualifiedAccess>]
type Hole =
    | Text of string
    | Number of float
    | Flag of bool
    /// A nested template: `html` or `svg`, told apart from any other object by lit's brand.
    | Template of Parts
    /// A JS array, a Fable list, a lazy `seq` — everything lit-html renders as a run of child
    /// parts. Left unclassified, so a sequence is walked once, by whoever renders it.
    | Sequence of Value seq
    /// What a string cannot carry: `null`/`undefined`, a listener, lit's `nothing` and
    /// `noChange` sentinels, any other object.
    | Inert

/// A `TemplateResult` as lit-html shapes it: the static parts, the hole values between them,
/// and the brand it puts on one (`1` for `html`, `2` for `svg`). The brand is an option
/// because on anything that is not a template it is absent — which is what makes reading it
/// the test.
type private Shape =
    abstract strings : string array
    abstract values : Value array
    abstract ``_$litType$`` : int option

let private partsOf (shape: Shape) : Parts =
    { Parts.Statics = shape.strings; Parts.Values = shape.values }

[<RequireQualifiedAccess>]
module Parts =

    /// The parts of a template. Every `TemplateResult` has this shape — it is what `html`
    /// returns — so the cast is the declaration `Fable.Lit` leaves out, not a guess.
    let ofTemplate (template: TemplateResult) : Parts = partsOf (unbox<Shape> template)

[<RequireQualifiedAccess>]
module Hole =

    /// Classify one hole's value. Every JavaScript kind is named by the F# type test that
    /// compiles to it. The order is the guard: `null` before anything a property is read off,
    /// text before the sequence arm — a string IS an `IEnumerable`, and that the probe the arm
    /// compiles to happens to answer no to one is a property of the library, not a rule. The
    /// position is the rule.
    let classify (value: Value) : Hole =
        let raw = box value
        match raw with
        | null -> Hole.Inert
        | :? string as s -> Hole.Text s
        | :? float as n -> Hole.Number n
        | :? bool as b -> Hole.Flag b
        // Reading the brand off an object that is not a template answers `None`: the cast is
        // what lets it be READ, and the read is the test.
        | _ when (unbox<Shape> raw).``_$litType$``.IsSome -> Hole.Template (partsOf (unbox<Shape> raw))
        // The NON-GENERIC `IEnumerable` deliberately: Fable refuses a test against `seq<_>` or
        // `IEnumerable<_>` outright ("Cannot type test (evals to false)", a compile error
        // rather than a silent false), while this one compiles to the library's own
        // iterability probe — which, unlike a `Symbol.iterator` macro, does not answer yes to
        // a string.
        | :? System.Collections.IEnumerable as items -> Hole.Sequence (Seq.cast<Value> items)
        | _ -> Hole.Inert
