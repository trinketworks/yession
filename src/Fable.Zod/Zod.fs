[<RequireQualifiedAccess>]
module Fable.Zod

// Fable bindings to the `zod` npm package. This is the binding layer only, mirroring how
// `Fable.Jose` wraps jose: it declares the slice of zod's surface this repo uses and nothing
// more.
//
// The one caller is the agent adapter's JSON-Schema-to-zod conversion. The Claude Agent SDK's
// `tool()` builder wants a zod shape; every other boundary a tool descriptor crosses (MCP's
// `tools/list`, an external server, the audit record) speaks JSON Schema — so the conversion
// exists at that one edge, and what it constructs is exactly this file: the five leaf types a
// JSON Schema `type` can name here, `array`, and the two refinements a property carries
// (`describe` for its documentation, `optional` for its absence from `required`).
//
// Deliberately absent, because the conversion does not construct them: `object`, `enum`,
// `union`, `record`, `tuple`, `literal`, `nullable`, `default`, `refine`, `transform`, and
// every string/number constraint (`min`, `max`, `email`, `int`, ...). A JSON Schema that asks
// for one of those is answered with `any` today, and widening that is a change to the
// CONVERSION — which is where the new binding would then be needed, and where the case for it
// can be read. A binding nobody calls is one nobody notices going stale.
//
// A raw shape IS a type here, and an opaque one: zod's own `ZodRawShape`, the plain JS object
// keyed by property name that the SDK's `tool()` takes. The conversion builds its properties
// as an F# list, where a test can read them, and `rawShape` is the one place that list
// becomes the object — so nothing outside this file ever holds the object as `obj`.
//
// Fable-only: `dotnet build` type-checks it; Fable emits the JS that runs on Node.

open Fable.Core
open Fable.Core.JsInterop

/// The outcome of validating a value: `success` says whether the schema accepted it, and
/// `data` is what it parsed to. Typed as what went IN because none of the schemas this file
/// builds changes a value it accepts — zod coerces nothing here, and the stripping it does
/// is `object`'s, which is not bound. Undefined when `success` is false, so read `success`
/// first.
type [<AllowNullLiteral>] ParseResult<'T> =
    abstract success : bool
    abstract data : 'T

/// One zod schema. Every constructor below answers one, and the refinements answer a NEW one
/// rather than mutating the receiver — `t.optional()` leaves `t` required, which is what makes
/// the conversion's `let t = ...; t = t.describe(...)` sequence mean what it reads as.
and [<AllowNullLiteral>] ZodType =
    /// Validate without throwing. Nothing in the product calls this — the SDK validates a
    /// tool's arguments against the shape it was handed — but it is the only way to OBSERVE
    /// that a schema was built as intended, so it is what the smoke tests read. Generic in
    /// what is offered, because offering the wrong thing is the point of half the calls: an
    /// erased union says "a string or a number" and `None` says "absent" (Fable's `None` is
    /// `undefined`, which is what an omitted property is).
    abstract safeParse<'T> : value: 'T -> ParseResult<'T>
    /// Attach the property's documentation; the SDK forwards it to the model.
    abstract describe : description: string -> ZodType
    /// Accept absence as well as a value — a property outside the schema's `required`.
    abstract optional : unit -> ZodType

/// The `z` namespace object, as `import { z } from 'zod'` yields it.
type private Z =
    abstract any : unit -> ZodType
    abstract string : unit -> ZodType
    abstract number : unit -> ZodType
    abstract boolean : unit -> ZodType
    abstract array : element: ZodType -> ZodType

[<Import("z", "zod")>]
let private z : Z = jsNative

/// Accepts anything. What an unrecognised (or absent) JSON Schema `type` becomes.
let any () : ZodType = z.any ()

let string () : ZodType = z.string ()

/// JSON Schema's `number` AND `integer`: zod's `number` is the JS one, and integrality is a
/// constraint this conversion does not carry.
let number () : ZodType = z.number ()

let boolean () : ZodType = z.boolean ()

let array (element: ZodType) : ZodType = z.array element

/// zod's `ZodRawShape`: a plain object whose values are zod types, one per property. Opaque —
/// made only by `rawShape`, and handed on to whatever takes one (the SDK's `tool()`).
type RawShape =
    interface end

/// The raw shape over these properties, in the order given (a JS object keeps insertion
/// order, and the SDK lists a tool's arguments in it). `[]` is the shape of a tool that takes
/// no arguments at all.
let rawShape (properties: (string * ZodType) list) : RawShape =
    createObj [ for name, schema in properties -> name, box schema ] |> unbox
