module PageScriptFixture.Seam

open Microsoft.Playwright

/// The place this fixture's handing would live if there were one place. There is not —
/// `Elsewhere` hands a page JavaScript too — so both are reported, and that is the rule: it says
/// the handing is spread, never which file was right.

let call (page: IPage) (name: string) (args: obj array) =
    page.EvaluateAsync<string> ("([name, args]) => globalThis[name](...args)", [| box name; box args |]) // YES012

let install (context: IBrowserContext) (path: string) =
    context.AddInitScriptAsync (scriptPath = path) // YES012
