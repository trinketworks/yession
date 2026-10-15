module Yession.Tests.PageScript

// The one place the suite hands a page JavaScript (the `PageScripts` analyzer, YES012, holds it
// to one). Everything a case asks a page goes through F# the page publishes — the probes
// (`Yession.App.Probes`), the harness's keys (`Yession.App.HarnessPage`), the shims
// (`Yession.App.Shims`) — and what is left on this side is the few fixed lines that reach
// them: call what is published under a name, read what is held under one, and install a
// bundle. Each takes its names and arguments AS arguments and splices nothing into a program.

#if !FABLE_COMPILER

open Microsoft.Playwright

[<RequireQualifiedAccess>]
module PageScript =
    let private invoking = "([name, args]) => globalThis[name](...args)"
    let private holding = "name => globalThis[name] ?? null"

    /// Call the function the page publishes under `name`, answering what it answered — a
    /// promise awaited for what it settles to. The arguments cross as Playwright serializes
    /// them, which is why they are `obj`: `EvaluateAsync` takes its argument as `object`, and
    /// what types a call is the key it is made through (`BrowserHarness`), not this line.
    let call<'T> (page: IPage) (name: string) (args: obj array) : Async<'T> = // fsharpanalyzer: ignore-line YES017
        Async.AwaitTask (page.EvaluateAsync<'T> (invoking, [| box name; box args |]))

    /// Call it for what it does, not for an answer. Its arguments are `obj` for `call`'s reason.
    let run (page: IPage) (name: string) (args: obj array) : Async<unit> = // fsharpanalyzer: ignore-line YES017
        page.EvaluateAsync (invoking, [| box name; box args |]) |> Async.AwaitTask |> Async.Ignore

    /// What the page holds under `name`, or null.
    let held<'T> (page: IPage) (name: string) : Async<'T> =
        Async.AwaitTask (page.EvaluateAsync<'T> (holding, name))

    /// A built bundle, run in every page the context opens before any script of the page's own.
    /// The disposable Playwright answers takes it away again; it stays for the context's life.
    let everyPage (context: IBrowserContext) (bundle: string) : Async<unit> =
        Async.AwaitTask (context.AddInitScriptAsync (scriptPath = bundle)) |> Async.Ignore

    /// A script run in this page from its next navigation on, before any script of its own.
    let fromNextLoad (page: IPage) (script: string) : Async<unit> =
        Async.AwaitTask (page.AddInitScriptAsync script) |> Async.Ignore

#endif
