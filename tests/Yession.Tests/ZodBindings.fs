module Yession.Tests.ZodBindings

// `Fable.Zod`, exercised. Cheap tier: zod is pure JavaScript and resolves from node_modules,
// so these need no capability at all — no ports, no processes, no native addon.
//
// What they are for: a binding project that nothing calls yet is only TYPE-checked, and a
// type-check cannot see the two ways a binding actually breaks — an import that names
// something the package does not export (`jsNative` compiles whatever you claim), and a
// method whose real behaviour is not the one the signature suggests. So every constructor and
// every refinement this file declares is built here and asked to accept and to refuse, which
// is the only observation a schema offers.

open Fable
open Fable.Core
open Fable.Pyxpecto

/// A genuinely absent value, which is what `optional` is about. `null` is NOT it: zod refuses
/// a null against an optional schema, so testing absence with one would prove the opposite of
/// what it looks like.
[<Emit("undefined")>]
let private undefined : obj = jsNative

let tests =
    testList "Fable.Zod" [
        testCase "a string schema accepts a string, unchanged" <| fun () ->
            let r = (Zod.string ()).safeParse (box "hello")
            Expect.isTrue r.success "a string is what a string schema is for"
            Expect.equal (unbox<string> r.data) "hello" "and it parses to itself"

        testCase "a string schema refuses a value that is not a string" <| fun () ->
            let r = (Zod.string ()).safeParse (box 7)
            Expect.isFalse r.success "a number is not a string"

        testCase "a number schema accepts a number" <| fun () ->
            let r = (Zod.number ()).safeParse (box 7)
            Expect.isTrue r.success "7 is a number"

        testCase "a number schema refuses a value that is not a number" <| fun () ->
            let r = (Zod.number ()).safeParse (box "7")
            Expect.isFalse r.success "the digits of a number are not one"

        testCase "a boolean schema accepts a boolean" <| fun () ->
            let r = (Zod.boolean ()).safeParse (box true)
            Expect.isTrue r.success "true is a boolean"

        testCase "a boolean schema refuses a value that is not a boolean" <| fun () ->
            let r = (Zod.boolean ()).safeParse (box "true")
            Expect.isFalse r.success "the word is not the value"

        testCase "an array schema accepts a list of its element type" <| fun () ->
            let r = (Zod.array (Zod.string ())).safeParse (box [| "a"; "b" |])
            Expect.isTrue r.success "every element is a string"

        testCase "an array schema refuses a list carrying a wrong element" <| fun () ->
            let r = (Zod.array (Zod.string ())).safeParse (box [| box "a"; box 2 |])
            Expect.isFalse r.success "one element off is the whole array off"

        testCase "an any schema accepts what a typed schema refuses" <| fun () ->
            // What an unrecognised JSON Schema `type` becomes, so it has to admit the values
            // a typed schema was refusing above — otherwise the fallback is a second refusal.
            Expect.isTrue ((Zod.any ()).safeParse (box 7)).success "a number"
            Expect.isTrue ((Zod.any ()).safeParse (box null)).success "a null"
            Expect.isTrue ((Zod.any ()).safeParse undefined).success "nothing at all"

        testCase "a schema refuses absence until it is made optional" <| fun () ->
            let r = (Zod.string ()).safeParse undefined
            Expect.isFalse r.success "a property outside `required` must not validate by default"

        testCase "an optional schema accepts absence" <| fun () ->
            let r = ((Zod.string ()).optional ()).safeParse undefined
            Expect.isTrue r.success "which is what a property outside `required` needs"

        testCase "an optional schema still refuses a wrong-typed value" <| fun () ->
            // `optional` widens a schema by exactly one value. A binding that answered
            // `z.any()` here would pass the case above and lose every type the model is told.
            let r = ((Zod.string ()).optional ()).safeParse (box 7)
            Expect.isFalse r.success "optional is not untyped"

        testCase "describing a schema leaves what it accepts unchanged" <| fun () ->
            let described = (Zod.string ()).describe "the name of the thing"
            Expect.isTrue (described.safeParse (box "hello")).success "still accepts a string"
            Expect.isFalse (described.safeParse (box 7)).success "still refuses a number"

        testCase "the refinements compose, in the order the conversion applies them" <| fun () ->
            // describe-then-optional, which is the sequence a described property outside
            // `required` goes through — each refinement answers a new schema, so a binding
            // that dropped one would be invisible in the two cases above.
            let refined = ((Zod.string ()).describe "the name of the thing").optional ()
            Expect.isTrue (refined.safeParse undefined).success "absent is allowed"
            Expect.isFalse (refined.safeParse (box 7)).success "and the type survived both"
    ]
