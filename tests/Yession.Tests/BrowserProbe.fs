module Yession.Tests.BrowserProbe

// The asking half of `Yession.App.Probes`. The measuring half is installed into every context
// the suite opens (`Probe.install`), so any page a case visits — the harness, a session's shell,
// the Manager's — can be asked; a case asks through the typed functions here and gets typed
// answers back, decoded by the same codecs the page encoded them with.

#if !FABLE_COMPILER

open System.IO
open Microsoft.Playwright
open Thoth.Json.Net
open Yession.App

[<RequireQualifiedAccess>]
module Probe =
    /// Built by the `Harness` target beside the harness it serves.
    let private bundle = Path.GetFullPath "tests/browser/out/probes.js"

    /// Give every page this context opens the probes, before any script of its own runs.
    let install (context: IBrowserContext) : Async<unit> =
        // The disposable it answers takes the script away again; it stays for the context's life.
        Async.AwaitTask (context.AddInitScriptAsync (scriptPath = bundle)) |> Async.Ignore

    /// The one piece of JavaScript the probes need from this side, and it is fixed: call the
    /// function published under a name with the JSON it was asked. Nothing is spliced into it;
    /// the name and the question cross as arguments.
    let private invoke = "([name, asked]) => globalThis[name](asked)"

    /// Ask the page one question of its contract.
    let ask (page: IPage) (probe: Probes.Probe<'Ask, 'Answer>) (asked: 'Ask) : Async<'Answer> =
        async {
            let question = Encode.toString 0 (probe.EncodeAsk asked)
            let! answer = Async.AwaitTask (page.EvaluateAsync<string> (invoke, [| probe.Name; question |]))
            return
                match Decode.fromString probe.DecodeAnswer answer with
                | Ok value -> value
                | Error e -> failwithf "%s answered something its contract cannot read: %s" probe.Name e
        }

    /// Every match of a selector, where it is drawn and whether it is painted at its centre.
    let seen (page: IPage) (selector: string) : Async<Probes.Seen list> = ask page Probes.seen selector

    /// How many matches of a selector a reader can actually see.
    let paintedCount (page: IPage) (selector: string) : Async<int> =
        async {
            let! all = seen page selector
            return all |> List.filter (fun s -> s.Painted) |> List.length
        }

    /// The first match of each selector, measured in one frame.
    let boxes (page: IPage) (selectors: string list) : Async<Probes.Box option list> =
        ask page Probes.boxes selectors

    /// One computed style property of the first match.
    let computed (page: IPage) (selector: string) (property: string) : Async<string option> =
        ask page Probes.computed (selector, property)

    /// Whether the page runs an animation frame within the given milliseconds.
    let framing (page: IPage) (withinMs: int) : Async<bool> = ask page Probes.framing withinMs

    /// Let the page run this many animation frames.
    let frames (page: IPage) (count: int) : Async<unit> = ask page Probes.frames count |> Async.Ignore

    /// Every match's own text, the remote carets' widgets left out.
    let ownTexts (page: IPage) (selector: string) : Async<string list> = ask page Probes.ownTexts selector

    /// The IndexedDB databases this page's origin holds, by name.
    let databases (page: IPage) : Async<string list> = ask page Probes.databases 0

#endif
