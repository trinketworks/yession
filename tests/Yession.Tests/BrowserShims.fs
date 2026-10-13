module Yession.Tests.BrowserShims

// The installing call for `Yession.App.Shims`, from the suite's side: a page is handed the
// statement that says which shims it is to have — a string literal of JSON the shared codec
// wrote — followed by the shims bundle, as one init script, so the list is there when the
// bundle runs and both run before anything of the page's.

#if !FABLE_COMPILER

open System.IO
open System.Text.Json
open Microsoft.Playwright
open Thoth.Json.Net
open Yession.App
open Yession.Tests.PageScript

[<RequireQualifiedAccess>]
module Shim =
    /// Built by the `Harness` target beside the harness.
    let private bundle = Path.GetFullPath "tests/browser/out/shims.js"

    /// Give this page these shims, from its next navigation on.
    let install (page: IPage) (shims: Shims.Shim list) : Async<unit> =
        async {
            let asked = Encode.toString 0 (Encode.list (List.map Shims.Shim.encode shims))
            // `JsonSerializer` spells the JSON text as a string literal, which is all this is:
            // the name and the value cross as literals it wrote, never as source.
            let published =
                sprintf "globalThis[%s] = %s;\n"
                    (JsonSerializer.Serialize (Fable.BrowserExtras.PageGlobal.name Shims.asked))
                    (JsonSerializer.Serialize asked)
            let! script = File.ReadAllTextAsync bundle |> Async.AwaitTask
            do! PageScript.fromNextLoad page (published + script)
        }

#endif
