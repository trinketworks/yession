module Yession.Tests.BrowserWait

// How the browser cases look at a page without handing it a program. A predicate written as a
// JavaScript string is one nothing type-checks and nothing parses until Chromium runs it, and
// the hook names and words spliced into it are spliced as TEXT — a quote in either breaks the
// predicate, silently, into one that is never true. What is here reads the page through
// Playwright's own typed calls instead, and waits on an F# predicate over what was read.
//
// The wait is a poll, not an `Expect`: a red case has to say what the page WAS showing, and a
// poll over a typed read has the last value in hand when it gives up. That is the instrument
// `waitFor` in `Browser.fs` could never have been — its predicate answered a bare boolean, so
// its timeout could name the wait and nothing about the page.

#if !FABLE_COMPILER

open System
open System.Diagnostics
open Microsoft.Playwright

/// CSS selectors built from the `Dom` contract's names rather than spelled by hand, so a hook
/// renamed there is renamed here.
[<RequireQualifiedAccess>]
module Sel =
    let private quoted (value: string) = "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""

    /// Elements carrying the hook at all: `[data-x]`.
    let hook (name: string) : string = "[" + name + "]"

    /// Elements whose hook has exactly this value: `[data-x="v"]`.
    let hookIs (name: string) (value: string) : string = "[" + name + "=" + quoted value + "]"

/// One look at the page, typed. Each answers what `document.querySelector` would — the FIRST
/// match — so a case that read the first match before reads the same element now. None of them
/// waits for an element to appear: a read is a snapshot, and the waiting is `Wait.until`'s.
[<RequireQualifiedAccess>]
module Read =
    /// A read never waits for anything, so its own deadline is short; an element that detaches
    /// between being counted and being read throws, which `Wait.until` takes as "not yet".
    let private snapshot = LocatorGetAttributeOptions (Timeout = 1000.0f)
    let private snapshotText = LocatorTextContentOptions (Timeout = 1000.0f)

    let count (page: IPage) (selector: string) : Async<int> =
        Async.AwaitTask (page.Locator(selector).CountAsync ())

    /// The first match's attribute; None when nothing matches or the attribute is absent.
    let attr (page: IPage) (selector: string) (name: string) : Async<string option> =
        async {
            let located = page.Locator selector
            let! n = Async.AwaitTask (located.CountAsync ())
            if n = 0 then return None
            else
                let! value = Async.AwaitTask (located.First.GetAttributeAsync (name, snapshot))
                return Option.ofObj value
        }

    /// The first match's `textContent`; None when nothing matches.
    let text (page: IPage) (selector: string) : Async<string option> =
        async {
            let located = page.Locator selector
            let! n = Async.AwaitTask (located.CountAsync ())
            if n = 0 then return None
            else
                let! value = Async.AwaitTask (located.First.TextContentAsync snapshotText)
                return Option.ofObj value
        }

    /// Every match's `textContent`, in document order.
    let texts (page: IPage) (selector: string) : Async<string list> =
        async {
            let! values = Async.AwaitTask (page.Locator(selector).AllTextContentsAsync ())
            return List.ofSeq values
        }

    /// One attribute of every match, in document order; `None` where a match lacks it.
    let attrs (page: IPage) (selector: string) (name: string) : Async<string option list> =
        async {
            let! all = Async.AwaitTask (page.Locator(selector).AllAsync ())
            let found = ResizeArray<string option> ()
            for element in all do
                let! value = Async.AwaitTask (element.GetAttributeAsync (name, snapshot))
                found.Add (Option.ofObj value)
            return List.ofSeq found
        }

    /// Every match's current value, for the inputs a selector names.
    let values (page: IPage) (selector: string) : Async<string list> =
        async {
            let! inputs = Async.AwaitTask (page.Locator(selector).AllAsync ())
            let values = ResizeArray<string> ()
            for input in inputs do
                let! value = Async.AwaitTask (input.InputValueAsync (LocatorInputValueOptions (Timeout = 1000.0f)))
                values.Add value
            return List.ofSeq values
        }

    /// Whether what has focus matches the selector.
    let focused (page: IPage) (selector: string) : Async<bool> =
        async {
            let! n = Async.AwaitTask (page.Locator(selector).And(page.Locator(":focus")).CountAsync ())
            return n > 0
        }

    /// Where the page is. Read off the driver rather than the page, so it answers across a
    /// navigation as well as between them.
    let url (page: IPage) : Async<Uri> = async { return Uri page.Url }

[<RequireQualifiedAccess>]
module Wait =
    let timeoutMs = 30000.0

    /// Read until `holds` accepts what was read, or fail saying what the wait was for and what
    /// it last saw. A read that throws — an execution context torn down by a navigation
    /// mid-poll — is "not yet", and kept so the timeout can name it.
    let within (limitMs: float) (what: string) (read: unit -> Async<'a>) (holds: 'a -> bool) : Async<unit> =
        async {
            let sw = Stopwatch.StartNew ()
            let mutable held = false
            let mutable last : 'a option = None
            let mutable lastError = ""
            while not held && sw.Elapsed.TotalMilliseconds < limitMs do
                try
                    let! seen = read ()
                    last <- Some seen
                    held <- holds seen
                with e -> lastError <- e.Message
                if not held then do! Async.Sleep 100
            if not held then
                let seen =
                    match last with
                    | Some value -> sprintf "%A" value
                    | None -> "nothing (no read completed)"
                let tail = if lastError = "" then "" else sprintf "\n  last error: %s" lastError
                failwithf "waiting for %s — timed out after %gms\n  last read: %s%s" what limitMs seen tail
            // A wait that settles is otherwise silent about how long it took, and a case's time
            // is the sum of its waits: said here, a slow case's log names which wait it spent it
            // on, where its duration alone names nothing.
            elif sw.Elapsed.TotalSeconds >= 1.0 then
                printfn "browser: waited %.1fs for %s" sw.Elapsed.TotalSeconds what
        }

    let until (what: string) (read: unit -> Async<'a>) (holds: 'a -> bool) : Async<unit> =
        within timeoutMs what read holds

#endif
