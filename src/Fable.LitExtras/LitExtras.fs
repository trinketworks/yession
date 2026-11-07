module Fable.LitExtras

// What a Lit `TemplateResult` is at runtime, for the one reader here that takes one apart
// rather than handing it to Lit: the Host's server-side renderer (`app/Ssr.fs`). And the one
// directive this repository writes (`memo`), which lives here because it is lit's API typed.
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
    ///
    /// Also what a directive that only CHOOSES what to render is, to a string: lit's `repeat`
    /// (each item's template, in order), `guard` and `memo` (the one template they would
    /// draw). What makes them worth using in the browser (keys, and skipping an unchanged
    /// subtree) is about a DOM that already exists, and a first paint has none, so a string
    /// is what they render, the same as the plain list or template they wrap.
    | Sequence of Value seq
    /// What a string cannot carry: `null`/`undefined`, a listener, lit's `nothing` and
    /// `noChange` sentinels, any other directive, any other object.
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

/// A directive's class, as lit keeps it on a directive's result. Opaque: the one thing to do
/// with it is compare it with another by reference.
type DirectiveClass =
    interface end

/// What calling a directive function returns (lit's `DirectiveResult`): the directive's class
/// under lit's brand, and the arguments it was called with. The class is an option because on
/// anything that is not a directive's result it is absent, which is what makes reading it the
/// test, as `_$litType$` is for a template.
type private Directed =
    abstract ``_$litDirective$`` : DirectiveClass option
    abstract values : Value array

/// The class of the directive behind `directive`, read off one result of calling it. lit
/// exports `repeat` and `guard` but not their classes, and a result of each carries its class:
/// that is the class every other result of the same function carries, so comparing by
/// reference recognises exactly that directive, whatever its arguments look like.
let private classOf (result: TemplateResult) : DirectiveClass option =
    (unbox<Directed> result).``_$litDirective$``

/// Equality of two directive classes: the same constructor, by reference.
let private sameClass (a: DirectiveClass) (b: DirectiveClass option) : bool =
    match b with
    | Some b -> obj.ReferenceEquals (a, b)
    | None -> false

/// The props and the template of one `memo` site: what the directive compares, and what it
/// calls when they differ.
///
/// Lit's own skipping (`guard`) compares its dependencies by reference, and a model that
/// rebuilds a record on every update has none that hold still. This compares by F#
/// structural equality, so props are DATA: a record, a union, a list, compared field by
/// field. A function in the props never equals another one, and a template result is a fresh
/// object each render, so either would make every comparison unequal.
///
/// What a skipped subtree keeps is everything it drew, its listeners included: a handler in
/// it closes over what it closed over the last time the props differed. So a template under
/// `memo` reads only its props and what is the same on every render (stable `dispatch` and
/// action functions), never a wider value it could see from where it was written.
type MemoDirective() =
    inherit Lit.Types.Directive()
    let mutable drawn = false
    let mutable last : obj = null
    /// Lit calls this for a first draw outside `update`, and a string renderer through it.
    member _.render (_props: obj, template: System.Func<TemplateResult>) : TemplateResult = template.Invoke ()
    /// Lit calls this on every render of the part: equal props say `noChange`, and lit leaves
    /// the subtree as it is, walking none of it.
    member this.update (_part: Lit.Types.Part, args: obj array) : TemplateResult =
        let props = args.[0]
        if drawn && LanguagePrimitives.GenericEquality last props then LitBindings.noChange
        else
            drawn <- true
            last <- props
            this.render (props, unbox<System.Func<TemplateResult>> args.[1])

/// The directive function `lit/directive.js` makes of the class: lit's spread call, two
/// arguments. A JS function, so a `Func` invokes it with them.
let private memoDirective : System.Func<obj, System.Func<TemplateResult>, TemplateResult> =
    unbox (LitBindings.directive (JsInterop.jsConstructor<MemoDirective>))

/// Draw `template props`, and on a later render of the same place skip the subtree entirely
/// (lit's `noChange`) while `props` is structurally equal to what it drew last.
/// `MemoDirective` says what props may hold and what the template may read.
let memo<'Props when 'Props : equality> (props: 'Props) (template: 'Props -> TemplateResult) : TemplateResult =
    memoDirective.Invoke (box props, System.Func<TemplateResult> (fun () -> template props))

let private repeatClass : DirectiveClass option =
    classOf (LitBindings.repeat<int> ([||], (fun _ -> ""), (fun _ _ -> LitBindings.nothing)))

let private guardClass : DirectiveClass option =
    classOf (LitBindings.guard ([||], fun () -> LitBindings.nothing))

let private memoClass : DirectiveClass option =
    classOf (memo 0 (fun _ -> LitBindings.nothing))

/// What one of the directives a string renders through would render, or `None` for anything
/// else. `repeat`'s arguments are `items, keyFn, template` or, without keys,
/// `items, template`; its template takes the item and its index. `guard`'s and `memo`'s last
/// argument is the function that draws.
let private renderedThrough (raw: obj) : Value seq option =
    match (unbox<Directed> raw).``_$litDirective$`` with
    | None -> None
    | Some directive ->
        let values = (unbox<Directed> raw).values
        if sameClass directive repeatClass then
            let template =
                if values.Length > 2 && not (isNull (box values.[2])) then values.[2] else values.[1]
            let draw = unbox<System.Func<Value, int, Value>> template
            let items = unbox<System.Collections.IEnumerable> values.[0] |> Seq.cast<Value>
            Some (items |> Seq.mapi (fun i item -> draw.Invoke (item, i)))
        elif sameClass directive guardClass || sameClass directive memoClass then
            Some (Seq.singleton ((unbox<System.Func<Value>> values.[1]).Invoke ()))
        else None

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
        // A directive's result is an object with lit's other brand; the class under it says
        // which directive. Before the sequence arm, so nothing decides by a value's shape.
        | _ when (unbox<Directed> raw).``_$litDirective$``.IsSome ->
            match renderedThrough raw with
            | Some drawn -> Hole.Sequence drawn
            | None -> Hole.Inert
        // The NON-GENERIC `IEnumerable` deliberately: Fable refuses a test against `seq<_>` or
        // `IEnumerable<_>` outright ("Cannot type test (evals to false)", a compile error
        // rather than a silent false), while this one compiles to the library's own
        // iterability probe — which, unlike a `Symbol.iterator` macro, does not answer yes to
        // a string.
        | :? System.Collections.IEnumerable as items -> Hole.Sequence (Seq.cast<Value> items)
        | _ -> Hole.Inert
