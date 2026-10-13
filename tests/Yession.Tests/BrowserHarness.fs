module Yession.Tests.BrowserHarness

// The calling half of `Yession.App.HarnessPage`: the browser cases and bench scenarios drive the
// harness page through the same typed keys it publishes under, so a call with the wrong
// arguments, or a read as the wrong type, is a compile error here rather than an `undefined`
// inside a string Chromium was handed.

#if !FABLE_COMPILER

open System.Text.Json
open Microsoft.Playwright
open Microsoft.FSharp.Reflection
open Fable.Core
open Fable.BrowserExtras
open Yession.Tests.PageScript

[<RequireQualifiedAccess>]
module Harness =
    /// An argument as the page should see it. Playwright serializes an F# option as the object
    /// it is in .NET; the page's F# reads `None` as null and `Some x` as `x`, so it crosses as
    /// those.
    let private arg (value: obj) : obj =
        match value with
        | null -> null
        | v when v.GetType().IsGenericType && v.GetType().GetGenericTypeDefinition () = typedefof<option<_>> ->
            let _, fields = FSharpValue.GetUnionFields (v, v.GetType ())
            if fields.Length = 0 then null else fields.[0]
        | v -> v

    let private call (page: IPage) (name: string) (args: obj list) : Async<unit> =
        PageScript.run page name (args |> List.map arg |> Array.ofList)

    let private answer<'T> (page: IPage) (name: string) (args: obj list) : Async<'T> =
        PageScript.call<'T> page name (args |> List.map arg |> Array.ofList)

    // --- Calls whose answer is nothing ----------------------------------------------------

    let run (page: IPage) (key: PageGlobal<unit -> unit>) : Async<unit> = call page (PageGlobal.name key) []

    let run1 (page: IPage) (key: PageGlobal<'A -> unit>) (a: 'A) : Async<unit> =
        call page (PageGlobal.name key) [ box a ]

    let run3 (page: IPage) (key: PageGlobal<System.Action<'A, 'B, 'C>>) (a: 'A) (b: 'B) (c: 'C) : Async<unit> =
        call page (PageGlobal.name key) [ box a; box b; box c ]

    let run4 (page: IPage) (key: PageGlobal<System.Action<'A, 'B, 'C, 'D>>) (a: 'A) (b: 'B) (c: 'C) (d: 'D) : Async<unit> =
        call page (PageGlobal.name key) [ box a; box b; box c; box d ]

    let run5
        (page: IPage)
        (key: PageGlobal<System.Action<'A, 'B, 'C, 'D, 'E>>)
        (a: 'A) (b: 'B) (c: 'C) (d: 'D) (e: 'E)
        : Async<unit> =
        call page (PageGlobal.name key) [ box a; box b; box c; box d; box e ]

    /// A call whose promise of nothing is awaited.
    let settle (page: IPage) (key: PageGlobal<unit -> JS.Promise<unit>>) : Async<unit> =
        call page (PageGlobal.name key) []

    // --- Calls that answer ---------------------------------------------------------------

    let ask (page: IPage) (key: PageGlobal<unit -> 'T>) : Async<'T> = answer<'T> page (PageGlobal.name key) []

    let ask3 (page: IPage) (key: PageGlobal<System.Func<'A, 'B, 'C, 'T>>) (a: 'A) (b: 'B) (c: 'C) : Async<'T> =
        answer<'T> page (PageGlobal.name key) [ box a; box b; box c ]

    /// A call whose answer is a promise, awaited for what it settles to.
    let await1 (page: IPage) (key: PageGlobal<'A -> JS.Promise<'T>>) (a: 'A) : Async<'T> =
        answer<'T> page (PageGlobal.name key) [ box a ]

    let await2 (page: IPage) (key: PageGlobal<System.Func<'A, 'B, JS.Promise<'T>>>) (a: 'A) (b: 'B) : Async<'T> =
        answer<'T> page (PageGlobal.name key) [ box a; box b ]

    let await3
        (page: IPage)
        (key: PageGlobal<System.Func<'A, 'B, 'C, JS.Promise<'T>>>)
        (a: 'A) (b: 'B) (c: 'C)
        : Async<'T> =
        answer<'T> page (PageGlobal.name key) [ box a; box b; box c ]

    // --- Values --------------------------------------------------------------------------

    /// What the page holds under this key; `None` where nothing has been published yet.
    let read (page: IPage) (key: PageGlobal<'T>) : Async<'T option> =
        async {
            let! held = PageScript.held<JsonElement> page (PageGlobal.name key)
            return
                match held.ValueKind with
                | JsonValueKind.Null
                | JsonValueKind.Undefined -> None
                | _ -> Some (held.Deserialize<'T> ())
        }

    /// What the page holds under a key it publishes from the start — a counter, say — where
    /// nothing there is the harness having failed to load rather than a value.
    let held (page: IPage) (key: PageGlobal<'T>) : Async<'T> =
        async {
            match! read page key with
            | Some value -> return value
            | None -> return failwithf "the harness published nothing under %s" (PageGlobal.name key)
        }

#endif
