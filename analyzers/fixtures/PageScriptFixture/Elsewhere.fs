module PageScriptFixture.Elsewhere

open Microsoft.Playwright

/// The second place, in the shapes a case reached for before the probes: a program to
/// evaluate, a predicate to wait on, a program run against a match, and a script for every
/// load — on a page, a locator and a context alike.

let measure (page: IPage) =
    page.EvaluateAsync<float> "() => document.body.scrollHeight" // YES012

let settle (page: IPage) =
    page.WaitForFunctionAsync "() => document.readyState === 'complete'" // YES012

let width (page: IPage) =
    page.Locator("[data-row]").EvaluateAsync<float> "row => row.clientWidth" // YES012

let rows (page: IPage) =
    page.EvalOnSelectorAllAsync<int> ("[data-row]", "rows => rows.length") // YES012

let patch (page: IPage) =
    page.AddInitScriptAsync "globalThis.patched = true" // YES012
