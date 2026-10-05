module Yession.Browser.ManagerPage

// The Manager page's program: it swaps the server's rendered fragments in on stop, archive and
// the MCP acts, takes live status from the rows stream, and keeps focus where a hand left it
// through all of them. The page is still server-rendered — every fragment arrives as markup the
// Manager wrote, and every address this program asks for is one the server spelled on the
// element that asks (`Dom.Manager.post`, `Dom.Manager.stream`). This file builds none.
//
// It was a JavaScript program in a string, inlined into the page. It is F# now for the reason
// every other program in this repository is: a string is a program nothing type-checks and
// nothing even parses. Served as an asset of the Manager's own set (`AssetFile.manager-page`),
// so it is still local — no CDN, nothing a page reaches off this machine for.

open Fable.Core
open Fable.BrowserExtras
open Browser.Types
open Browser.Dom
open Yession.App

/// `[name]` — the selector for an attribute's presence.
let private has (name: string) = "[" + name + "]"

/// The element an event was dispatched at. Every listener below is on the document, and every
/// event it listens for is dispatched at an element.
let private targetOf (event: Event) : Element = event.target :?> Element

/// The nearest element at or above the target carrying the hook, if any.
let private closestTo (event: Event) (hook: string) : Element option =
    (targetOf event).closest (has hook)

let private attribute (element: Element) (name: string) : string option =
    match element.getAttribute name with
    | null -> None
    | value -> Some value

/// The first element at or under `root` whose `name` attribute is exactly `value` — what the
/// selector `[name="value"]` finds, without building one out of a value this page did not write.
let private withAttribute (root: Element) (name: string) (value: string) : Element option =
    if attribute root name = Some value then Some root
    else
        let found = root.querySelectorAll (has name)
        seq { for i in 0 .. found.length - 1 -> found.[i] }
        |> Seq.tryFind (fun element -> attribute element name = Some value)

let private selecting (root: Element) (selector: string) : Element option =
    if root.matches selector then Some root
    else
        match root.querySelector selector with
        | null -> None
        | found -> Some found

let private focus (element: Element) = (element :?> HTMLElement).focus ()

/// Markup the server rendered, parsed where it is going: in the context of the element it
/// replaces, so a `<tr>` is parsed as a row of the table it lands in rather than dropped as a
/// stray cell outside one.
let private parseBeside (element: Element) (markup: string) : Element option =
    let range = document.createRange ()
    range.selectNode element
    match (range.createContextualFragment (markup.Trim ())).querySelector "*" with
    | null -> None
    | parsed -> Some parsed

let private replace (current: Node) (replacement: Node) =
    current.parentNode.replaceChild (replacement, current) |> ignore

/// Put a fragment the server rendered where `element` is — unless it says nothing new.
///
/// Keyboard continuity (WCAG 2.0): replacing the focused element strands focus on `<body>`.
/// So focus lands back on the SAME thing — the swap unit is often the whole table (the rows
/// stream, a create), and the replacement's first control belongs to the first row, which is
/// another session's control under the same finger.
let private swap (element: Element option) (markup: string) =
    match element with
    | None -> ()
    | Some element ->
        match parseBeside element markup with
        | None -> ()
        | Some fresh when (fresh :?> HTMLElement).outerHTML = (element :?> HTMLElement).outerHTML -> ()
        | Some fresh ->
            let active =
                match document.activeElement with
                | null -> None
                | focused when element.contains focused -> Some focused
                | _ -> None
            let within (hook: string) = active |> Option.bind (fun a -> a.closest (has hook))
            let row = within Dom.Manager.session
            // A filter or sort control keeps a STABLE name across a swap even though its href
            // just flipped, so the hand that pressed `archived` is left on `archived` rather
            // than being dropped onto the first row of the list it just asked for.
            let filter = within Dom.Manager.filter
            // Create rides the section's header line, so a frame arriving under a hand resting
            // on it would otherwise drop that hand onto the first chip.
            let wasCreate = (within Dom.Manager.createSession).IsSome
            let wasAction = active |> Option.exists (fun a -> a.hasAttribute Dom.Manager.stop)
            // A Create that is HELD (the browser is on its way to the new session, and the rows
            // stream announces that session before the redirect lands) keeps its own element
            // through the swap: the state, the tilt it was pushed at, and the focus all live on
            // it.
            match element.querySelector (has Dom.Manager.createSession + " [aria-busy=\"true\"]") with
            | null -> ()
            | held ->
                match fresh.querySelector (has Dom.Manager.createSession + " button") with
                | null -> ()
                | stale -> replace stale held
            replace element fresh
            match active with
            | None -> ()
            | Some _ ->
                let back =
                    match filter |> Option.bind (fun f -> attribute f Dom.Manager.filter) with
                    | Some name -> withAttribute fresh Dom.Manager.filter name
                    | None -> None
                let back =
                    match back with
                    | Some _ -> back
                    | None when wasCreate -> selecting fresh (has Dom.Manager.createSession + " button")
                    | None -> None
                match back with
                | Some control -> focus control
                | None ->
                    let scope =
                        row
                        |> Option.bind (fun r -> attribute r Dom.Manager.session)
                        |> Option.bind (withAttribute fresh Dom.Manager.session)
                        |> Option.defaultValue fresh
                    // A Stop that landed has taken its own control away, and the row's first
                    // focusable is now the name — the stable open route, which is the way back in.
                    let stillThere = if wasAction then Option.ofObj (scope.querySelector (has Dom.Manager.stop)) else None
                    let first = stillThere |> Option.orElse (Option.ofObj (scope.querySelector "a[href], button, input"))
                    first |> Option.iter focus

let private sessionsSection () = Option.ofObj (document.querySelector (has Dom.Manager.sessions))

let private formEncoded = "application/x-www-form-urlencoded"

/// A form body, encoded the way a form submits one.
let private encodeForm (fields: (string * string) seq) : string =
    fields
    |> Seq.map (fun (name, value) -> System.Uri.EscapeDataString name + "=" + System.Uri.EscapeDataString value)
    |> String.concat "&"

let private post (url: string) (body: string option) : JS.Promise<Fetch.Types.Response> =
    Fetch.fetchUnsafe
        url
        [ yield Fetch.Types.RequestProperties.Method Fetch.Types.HttpMethod.POST
          match body with
          | Some body ->
              yield Fetch.requestHeaders [ Fetch.Types.HttpRequestHeaders.ContentType formEncoded ]
              yield Fetch.Types.RequestProperties.Body (U3.Case3 body)
          | None -> () ]

/// Press a control's own address, and swap whatever answers into `into`. A refusal is left
/// where it is: the page keeps showing what was true before the press.
let private pressAndSwap (control: Element) (suffix: string) (into: unit -> Element option) =
    match attribute control Dom.Manager.post with
    | None -> ()
    | Some url ->
        promise {
            let! answer = post (url + suffix) None
            if answer.Ok then
                let! markup = answer.text ()
                swap (into ()) markup
        }
        |> ignore

document.addEventListener (
    "click",
    fun event ->
        match closestTo event Dom.Manager.stop with
        | None -> ()
        | Some stop ->
            let row = stop.closest "tr"
            pressAndSwap stop "" (fun () -> row)
)

// Archiving answers with the WHOLE table, not a row: it can move a session out of the filter
// you are looking at, so the row is no longer the unit that changed. The query rides along
// because the answer has to be rendered for the list this page is showing.
document.addEventListener (
    "click",
    fun event ->
        match (targetOf event).closest (has Dom.Manager.archive + "," + has Dom.Manager.unarchive) with
        | None -> ()
        | Some control -> pressAndSwap control window.location.search sessionsSection
)

// The stream is opened AT the current query and reopened when it changes; the server filters
// per connection, so live status keeps arriving under whatever is being shown. Its address is
// on the section it fills, like every other address on this page: the server spells them all
// (`ManagerRoute`), and this program spells none.
//
// A stream reopened for a NEW query owes the page its first frame: until it lands, the address
// says one filter and the rows say another. If it fails before then — offline, a Manager
// mid-restart — the page reloads at the address it already has, which the server renders
// correctly by construction. Only then: the same error on a stream that had already delivered
// is an ordinary drop, and `EventSource` reconnects on its own.
let mutable private rows : EventSource option = None

let private openRows (moved: bool) =
    rows |> Option.iter (fun stream -> stream.close ())
    match sessionsSection () |> Option.bind (fun section -> attribute section Dom.Manager.stream) with
    | None -> rows <- None
    | Some address ->
        let mutable settled = not moved
        let stream = EventSource.create (address + window.location.search)
        stream.onmessage <-
            fun frame ->
                settled <- true
                match frame.data with
                | :? string as markup when markup <> "" -> swap (sessionsSection ()) markup
                | _ -> ()
        stream.onerror <-
            fun _ ->
                if not settled then
                    settled <- true
                    window.location.reload ()
        rows <- Some stream

// A filter or sort click is a navigation this page performs itself: adopt the href the SERVER
// computed as the new location, then reopen the stream at it. The first frame is the whole
// snapshot for the new query, so one click is one swap and no page reloads — which is also why
// focus is never stranded.
//
// PUSHED, not replaced: the filter is the page's location, and the chips' whole claim to being
// links is that a bookmark restores one and the back button undoes one. A replace kept the
// first half and quietly broke the second — Back left the page — while the `popstate` handler
// below waited for an event that a chip click could never produce.
document.addEventListener (
    "click",
    fun event ->
        match closestTo event Dom.Manager.filter with
        | None -> ()
        | Some control ->
            let click = event :?> MouseEvent
            if not (click.metaKey || click.ctrlKey || click.shiftKey || click.button <> 0.0) then
                event.preventDefault ()
                // The last lit chip is a span with nowhere to go: the location stays as it is.
                match attribute control "href" with
                | Some href -> window.history.pushState (null, "", href)
                | None -> window.history.pushState (null, "")
                openRows true
)

// A row opens its session in a NEW tab (`target="_blank"`), so the Manager stays where it was —
// except where there are no tabs. In an installed app a new browsing context is a window with
// nothing painted in it, and iOS shows it white until the page lands: pressing a stopped row
// put a white screen in front of the opening one, while Create, which navigates in place, did
// not. So in an installed app a row is followed in place too, as a pushed entry: Back is the
// Manager, as it is after Create. A modified click is left alone, as on the filters above.
document.addEventListener (
    "click",
    fun event ->
        match closestTo event Dom.Manager.openLink with
        | None -> ()
        | Some link ->
            let click = event :?> MouseEvent
            if mediaMatches Dom.Manager.installedApp
               && not (click.metaKey || click.ctrlKey || click.shiftKey || click.button <> 0.0) then
                match attribute link "href" with
                | Some href ->
                    event.preventDefault ()
                    window.location.assign href
                | None -> ()
)

// Creating is deliberately NOT intercepted here (the reasoning is on the form itself) — but it
// is MARKED. Between the push and the new page there is nothing on this one to show for it, so
// the button stays down (`aria-busy`: held, filled, and saying what it is doing) until the
// browser leaves. A second push in that window is refused: two Creates are two sessions.
document.addEventListener (
    "submit",
    fun event ->
        match closestTo event Dom.Manager.createSession with
        | None -> ()
        | Some form ->
            match form.querySelector "button" with
            | null -> ()
            | button when button.getAttribute "aria-busy" = "true" -> event.preventDefault ()
            | button -> button.setAttribute ("aria-busy", "true")
)

// Back here from the session — the page restored as it was left — the hold is over: the act it
// was held for happened.
window.addEventListener (
    "pageshow",
    fun event ->
        if (event :?> PageTransitionEvent).persisted then
            let held = document.querySelectorAll "[aria-busy=\"true\"]"
            for i in 0 .. held.length - 1 do
                held.[i].removeAttribute "aria-busy"
)

// A button goes in where it is touched (`[data-press]`, tailwind.css). The stylesheet does the
// pressing off `:active`; what it cannot know is WHERE, so this hands it the touch point as two
// numbers in [-1, 1] from the button's centre. A key has no where and gets the centre — straight
// in — rather than the corner the mouse last left behind.
let private pressAt (button: Element) (x: string) (y: string) =
    let button = button :?> HTMLElement
    setStyleProperty button "--press-x" x
    setStyleProperty button "--press-y" y

document.addEventListener (
    "pointerdown",
    fun event ->
        match closestTo event Dom.press with
        | None -> ()
        | Some button ->
            let pointer = event :?> MouseEvent
            let box = button.getBoundingClientRect ()
            let along (at: float) (start: float) (size: float) = sprintf "%.2f" ((at - start) / size * 2.0 - 1.0)
            pressAt button (along pointer.clientX box.left box.width) (along pointer.clientY box.top box.height)
)

document.addEventListener (
    "keydown",
    fun event ->
        match closestTo event Dom.press with
        | None -> ()
        | Some button -> pressAt button "0" "0"
)

// Declaring an MCP server (Plan 17): the only place a url is written, and the only management
// action that can be REFUSED for a reason a human needs to read — a name clash. So this one
// reports, where stop/archive only swap.
let private mcpSection () = Option.ofObj (document.querySelector (has Dom.Manager.mcp))

/// What a form holds, as it would submit it: every enabled named field, in document order. The
/// binding's `FormData` cannot be made FROM a form, so this reads the fields the declare form
/// has — text inputs and a select — each typed by the tag it was found under.
let private formFields (form: Element) : (string * string) list =
    let named = form.querySelectorAll "input[name], select[name], textarea[name]"
    [ for i in 0 .. named.length - 1 do
          let field = named.[i]
          match field.tagName with
          | "SELECT" ->
              let select = field :?> HTMLSelectElement
              if not select.disabled then yield select.name, select.value
          | "TEXTAREA" ->
              let area = field :?> HTMLTextAreaElement
              if not area.disabled then yield area.name, area.value
          | _ ->
              let input = field :?> HTMLInputElement
              let submitted =
                  match input.``type`` with
                  | "submit" | "button" | "reset" | "image" | "file" -> false
                  | "checkbox" | "radio" -> input.``checked``
                  | _ -> true
              if submitted && not input.disabled then yield input.name, input.value ]

document.addEventListener (
    "submit",
    fun event ->
        match closestTo event Dom.Manager.declareMcp with
        | None -> ()
        | Some form ->
            event.preventDefault ()
            let fields = formFields form
            promise {
                let! answer = post (form.getAttribute "action") (Some (encodeForm fields))
                let! text = answer.text ()
                if answer.Ok then swap (mcpSection ()) text
                else
                    match form.querySelector (has Dom.Manager.mcpError) with
                    | null -> ()
                    | report -> report.textContent <- text
            }
            |> ignore
)

document.addEventListener (
    "click",
    fun event ->
        match closestTo event Dom.Manager.mcpWithdraw with
        | None -> ()
        | Some control ->
            // The row carries all three, always: the server's name, its audience — EMPTY for
            // the host-wide one, which is an answer rather than a gap — and the address.
            match
                attribute control Dom.Manager.mcpWithdraw,
                attribute control Dom.Manager.mcpAudience,
                attribute control Dom.Manager.post
            with
            | Some name, Some audience, Some url ->
                promise {
                    let! answer = post url (Some (encodeForm [ "name", name; "session", audience ]))
                    if answer.Ok then
                        let! markup = answer.text ()
                        swap (mcpSection ()) markup
                }
                |> ignore
            | _ -> ()
)

openRows false

// The back button moves the filter, so the stream has to move with it.
window.addEventListener ("popstate", fun _ -> openRows true)
