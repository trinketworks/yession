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
// Shapes themselves are not a type here: a zod shape is a plain JS object keyed by property
// name, which the caller builds, and giving it a name would be inventing a layer zod does not
// have.
//
// Fable-only: `dotnet build` type-checks it; Fable emits the JS that runs on Node.

open Fable.Core

/// The outcome of validating a value: `success` says whether the schema accepted it, and
/// `data` is what it parsed to (zod coerces nothing here, but it does strip what a schema
/// does not describe). Undefined when `success` is false.
type [<AllowNullLiteral>] ParseResult =
    abstract success : bool
    abstract data : obj

/// One zod schema. Every constructor below answers one, and the refinements answer a NEW one
/// rather than mutating the receiver — `t.optional()` leaves `t` required, which is what makes
/// the conversion's `let t = ...; t = t.describe(...)` sequence mean what it reads as.
and [<AllowNullLiteral>] ZodType =
    /// Validate without throwing. Nothing in the product calls this — the SDK validates a
    /// tool's arguments against the shape it was handed — but it is the only way to OBSERVE
    /// that a schema was built as intended, so it is what the smoke tests read.
    abstract safeParse : value: obj -> ParseResult
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
