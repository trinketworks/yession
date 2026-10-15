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
open System.Collections.Generic
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
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

    /// Whether the first match is visible — a box that is not empty, not `visibility: hidden`.
    /// The meaning Playwright's own `WaitForSelectorAsync` waits for, read once and not waited
    /// on: the waiting is `Wait.until`'s, which is what can be refused.
    let visible (page: IPage) (selector: string) : Async<bool> =
        Async.AwaitTask (page.Locator(selector).First.IsVisibleAsync ())

/// Why a wait that is not yet met will not be: the reason, or `None` while waiting is still
/// sensible. Asked after the wait's own predicate, every time that fails — so a wait FOR the bad
/// state is answered, and only a wait for anything else is refused. The Node tier's clients
/// have the same shape (`Harness.runWith`'s `refusal`), for the same reason: a deadline says
/// only that nothing came, and the page often already holds why.
type Refusal = unit -> Async<string option>

/// The refusals a case can install (`Wait.refusing`).
[<RequireQualifiedAccess>]
module Refusal =
    /// How long one page is given to say whether its environment failed. A page answers in a few
    /// milliseconds, so one that takes this long is not slow, it is stuck: a navigation its server
    /// never answers, a dialog nobody dismisses, a tab that crashed. `Locator.CountAsync` has no
    /// deadline of its own and waits for such a page for ever, and a refusal asks EVERY page the
    /// case opened, including the ones the wait is not about.
    let private askBudgetMs = 1000

    /// What one page says: the environment's words when it says it failed. A page that cannot
    /// be asked (closed, or mid-navigation) has not said.
    let private ask (page: IPage) : Async<string option> =
        async {
            try
                return!
                    Read.attr
                        page
                        (Sel.hookIs Yession.App.Dom.Hooks.environment Yession.App.Dom.Text.envFailed)
                        Yession.App.Dom.Hooks.environmentReason
            with _ -> return None
        }

    /// Why a wait in this case will not be met: the work sandbox of the session behind one of
    /// these pages did not start. The reason is the product's own sentence (`srt sandbox
    /// failed: ...`), carried on the element that says the environment failed
    /// (`Dom.Hooks.environmentReason`) — so one read of one element answers both whether and why.
    ///
    /// The pages are asked each time, because a case opens more as it goes. One that cannot be
    /// asked — closed, mid-navigation, or stuck past `askBudgetMs` — has not said, which leaves
    /// the others to. A stuck page is asked once and then left alone until it answers, so it
    /// costs a call the budget once and not on every tick, and its questions do not pile up
    /// behind it. Without that bound the refusal made every wait as patient as the stuckest page
    /// of its case: the wait's own deadline is only looked at between ticks, and a tick that
    /// never ends never reaches it.
    ///
    /// "Failed" ends a wait because nothing in a case's lifetime retries: the environment is
    /// started on a command's need, and a case that expects one failure and then a recovery
    /// waits FOR the failure first, which the predicate-first order of `Wait.withRefusal`
    /// answers. The sentence is the Node tier's (`Peer.environmentRefusal`): a suite that runs a
    /// command on a box with no sandbox is told the same thing on either runtime.
    ///
    /// The per-page budget is real time on purpose: it bounds a call into a page that may never
    /// return, and a page has no clock of its own for the case to turn.
    [<Yession.Tests.Support.Waits>]
    let sandboxDidNotStart (pages: unit -> IPage list) : Refusal =
        // The question each page was last asked. Held while it is out, so the next tick finds a
        // page that has not answered and goes on to the next one.
        let asked = Dictionary<IPage, Task<string option>> (HashIdentity.Reference)
        fun () ->
            async {
                let mutable reason : string option = None
                for page in pages () do
                    if reason.IsNone then
                        let question =
                            lock asked (fun () ->
                                match asked.TryGetValue page with
                                | true, out when not out.IsCompleted -> None
                                | _ ->
                                    let started = Async.StartAsTask (ask page)
                                    asked.[page] <- started
                                    Some started)
                        match question with
                        | None -> ()
                        | Some question ->
                            // A budget and not a wait: a page that is not stuck has answered
                            // long before it ends, and its timer is let go at once.
                            use budget = new CancellationTokenSource ()
                            let! _ = Async.AwaitTask (Task.WhenAny [| question :> Task; Task.Delay (askBudgetMs, budget.Token) |])
                            budget.Cancel ()
                            if question.IsCompleted then reason <- question.Result
                return
                    reason
                    |> Option.map (fun why ->
                        sprintf
                            "the work sandbox did not start: %s. If this box has no sandbox, the suite needs `Sandbox` in its `needs`"
                            why)
            }

[<RequireQualifiedAccess>]
module Wait =
    let timeoutMs = 30000.0

    /// The refusal of the case that is running, installed by `refusing`. A wait here is
    /// `Wait.until what read holds` with no page in hand — 160-odd call sites, each closing over
    /// its own — so the case's world has to reach it some other way. Cases run one at a time
    /// (Pyxpecto does not run them in parallel), and a second installation while one is in force
    /// is refused, so a runner that starts to overlap them says so, rather than one case's
    /// waits being ended by another case's page.
    let mutable private inForce : Refusal option = None
    let private gate = obj ()

    /// Run `body` with `refusal` consulted by every `within` and `until` in it, and not after.
    let refusing (refusal: Refusal) (body: Async<'a>) : Async<'a> =
        async {
            lock gate (fun () ->
                match inForce with
                | Some _ ->
                    failwith
                        "a wait refusal is already in force: two cases are running at once, and a wait \
                         here would be ended by whichever case's page failed first. Give `Wait.within` \
                         the page it waits on, in place of `refusing`."
                | None -> inForce <- Some refusal)
            try
                return! body
            finally
                lock gate (fun () -> inForce <- None)
        }

    /// Read until `holds` accepts what was read, or fail saying what the wait was for and what
    /// it last saw — or stop at once when `refusal` says the page has reached a state no wait
    /// will survive. A read that throws — an execution context torn down by a navigation
    /// mid-poll — is "not yet", and kept so the timeout can name it; so is a refusal that throws.
    ///
    /// The deadline holds whatever the reads do. Each look (the read, and the refusal if it is
    /// not met) runs on a task of its own, raced against the deadline, because a call into a
    /// page can have no end of its own — a navigation the server never answers, a dialog nobody
    /// dismisses, a tab that crashed — and a loop that only looks at the clock between looks
    /// never reaches it. The wait then fails by its deadline and says a read was still out. A
    /// look that is abandoned is left to finish or be torn down with the case's contexts.
    ///
    /// The poll's pause and the deadline are real time on purpose: this is the wait every
    /// browser case goes through, and Chromium runs on the real clock.
    [<Yession.Tests.Support.Waits>]
    let withRefusal
        (refusal: Refusal)
        (limitMs: float)
        (what: string)
        (read: unit -> Async<'a>)
        (holds: 'a -> bool)
        : Async<unit> =
        async {
            let sw = Stopwatch.StartNew ()
            use deadline = new CancellationTokenSource ()
            let expired = Task.Delay (TimeSpan.FromMilliseconds limitMs, deadline.Token)
            let mutable held = false
            let mutable refused : string option = None
            let mutable stuck = false
            let mutable last : 'a option = None
            let mutable lastError = ""
            while not held && refused.IsNone && not stuck && sw.Elapsed.TotalMilliseconds < limitMs do
                let look =
                    Async.StartAsTask (
                        async {
                            let mutable seen : 'a option = None
                            let mutable met = false
                            let mutable error = ""
                            try
                                let! value = read ()
                                seen <- Some value
                                met <- holds value
                            with e -> error <- e.Message
                            let mutable why : string option = None
                            if not met then
                                // After the predicate, never before: a wait for the failed state
                                // is answered by it.
                                try
                                    let! reason = refusal ()
                                    why <- reason
                                with e -> error <- e.Message
                            return seen, met, why, error
                        })
                let! first = Async.AwaitTask (Task.WhenAny [| look :> Task; expired |])
                if obj.ReferenceEquals (first, look) then
                    let seen, met, why, error = look.Result
                    if seen.IsSome then last <- seen
                    if error <> "" then lastError <- error
                    held <- met
                    refused <- why
                    if not held && refused.IsNone then do! Async.Sleep 100
                else stuck <- true
            deadline.Cancel ()
            if not held then
                let seen =
                    match last with
                    | Some value -> sprintf "%A" value
                    | None -> "nothing (no read completed)"
                let tail = if lastError = "" then "" else sprintf "\n  last error: %s" lastError
                match refused with
                | Some reason ->
                    failwithf "waiting for %s — stopped waiting: %s\n  last read: %s%s" what reason seen tail
                | None when stuck ->
                    failwithf
                        "waiting for %s — timed out after %gms, with a call into a page still unanswered (a navigation its server never answers, a dialog nobody dismissed, a tab that crashed)\n  last read: %s%s"
                        what
                        limitMs
                        seen
                        tail
                | None ->
                    failwithf "waiting for %s — timed out after %gms\n  last read: %s%s" what limitMs seen tail
            // A wait that settles is otherwise silent about how long it took, and a case's time
            // is the sum of its waits: said here, a slow case's log names which wait it spent it
            // on, where its duration alone names nothing.
            elif sw.Elapsed.TotalSeconds >= 1.0 then
                printfn "browser: waited %.1fs for %s" sw.Elapsed.TotalSeconds what
        }

    /// `withRefusal` under the refusal of the case that is running — none, outside one.
    let within (limitMs: float) (what: string) (read: unit -> Async<'a>) (holds: 'a -> bool) : Async<unit> =
        async {
            // Asked when the wait RUNS, not when it is written down: a wait built before the case
            // began and awaited inside it is the case's.
            let refusal = lock gate (fun () -> inForce) |> Option.defaultValue (fun () -> async.Return None)
            return! withRefusal refusal limitMs what read holds
        }

    let until (what: string) (read: unit -> Async<'a>) (holds: 'a -> bool) : Async<unit> =
        within timeoutMs what read holds

#endif
