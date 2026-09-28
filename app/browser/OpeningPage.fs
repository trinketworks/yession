module Yession.Browser.OpeningPage

// The opening screen's program: ask the Manager whether the session's address answers yet, and
// once it does, hand the browser over — not before the intro has landed, and not forever. The
// screen is the Manager's (`ManagerUi.openingPage`); the two addresses this asks for are on it,
// spelled by the server (`Dom.Manager.openingReady`, `Dom.Manager.openingTarget`).
//
// It was a JavaScript program in a string, with the addresses spliced into it as JSON literals.
// It is F# now, and the addresses are attributes the page carries, so nothing is spliced into a
// program at all.

open Fable.Core
open Fable.BrowserExtras
open Browser.Types
open Browser.Dom
open Yession.App

/// The intro is 2.4s and a beat after it lands is the least anyone is shown; under reduced
/// motion there is no intro, and nothing to wait for.
let private dwellMs =
    if mediaMatches "(prefers-reduced-motion: reduce)" then 0.0 else 2800.0

/// How often the readiness address is asked, and how many times before the screen gives up and
/// says why: 40 at 500ms is the 20 seconds its words promise.
let private pollMs = 500
let private attemptsAllowed = 40

let private shownAt = Browser.Performance.performance.now ()

let private attributeOn (selector: string) (name: string) : string option =
    match document.querySelector selector with
    | null -> None
    | element -> Option.ofObj (element.getAttribute name)

/// Did the Manager say the address answers? `ok`, not "the request settled". A front door that
/// has not mapped this session yet answers — with a 404, or a gateway error — and a fetch that
/// only caught THROWN requests reads that as the session answering, redirects into it, and
/// leaves whoever pressed Create looking at the front door's 404. The readiness route reports
/// the difference. A request that throws is the Manager itself unreachable: the same wait,
/// bounded the same way.
let private answered (ready: string) : JS.Promise<bool> =
    Fetch.fetchUnsafe ready [ Fetch.Types.RequestProperties.Cache Fetch.Types.RequestCache.Nostore ]
    |> Promise.map (fun answer -> answer.Ok)
    |> Promise.catch (fun _ -> false)

let rec private poll (status: HTMLElement) (ready: string) (target: string) (attempts: int) =
    promise {
        let! isReady = answered ready
        if isReady then
            match status.querySelector ("[" + Dom.Manager.openingWord + "]") with
            | null -> ()
            | word -> word.textContent <- "ready"
            let waited = Browser.Performance.performance.now () - shownAt
            JS.setTimeout (fun () -> window.location.replace target) (int (max 0.0 (dwellMs - waited))) |> ignore
        elif attempts >= attemptsAllowed then
            status.className <- Style.statusErr + " " + Style.startStatus
            status.textContent <-
                "The session started, but its address is not answering after 20 seconds. "
                + "If this deployment maps session ports through a proxy, that mapping has not appeared."
        else
            JS.setTimeout (fun () -> poll status ready target (attempts + 1) |> ignore) pollMs |> ignore
    }

match
    Option.ofObj (document.getElementById "status"),
    attributeOn ("[" + Dom.Manager.openingReady + "]") Dom.Manager.openingReady,
    attributeOn ("[" + Dom.Manager.openingTarget + "]") "href"
with
| Some status, Some ready, Some target -> poll status ready target 1 |> ignore
// Not the opening screen: there is nothing to wait for.
| _ -> ()
