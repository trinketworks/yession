module Yession.Tests.CdpChromium

// `Fable.NodeExtras.Cdp` against a real Chromium, and the rule that finds one.
//
// `NodeExtras.cdpTests` hold the client to the protocol's documentation: what each command
// writes, and what each answer and event is read by, checked by a session over nothing. That
// proves the client says what the documentation says. It cannot prove that a browser agrees:
// a method a Chromium revision no longer has, a parameter it has stopped reading, an event it
// sends under another shape. Those surface in the camera (`tools/Yession.Frames`), on the day
// somebody needs a picture. So the suite below starts the browser the camera starts, through
// the same launcher (`Fable.NodeExtras.Chromium`), and asks it.
//
// One Chromium for the suite, launched by the first case that wants it, and closed when this
// process ends — by the launcher's own `exit` listener, which a run narrowed past any last case
// still reaches (Pyxpecto has no after-all, and a teardown CASE is pruned by `--only` like any
// other). Each case speaks over a CDP session of its own: Chromium keeps events, emulation and
// a screencast per session, so one case's leave nothing for the next to hear. The cases share
// the one page, and each first lands it on a document of its own (`withSession`).
//
// No expression here is a program (Type safety, in CLAUDE.md). `Runtime.evaluate` is handed a
// JSON literal, which evaluates to the value it encodes; a page that has to keep painting is a
// `data:` URL of HTML and CSS.

open Fable.Core
open Fable.Pyxpecto
open Fable.NodeExtras
open Yession.Host
open Yession.Tests.Support

#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

// --- Finding Chromium -----------------------------------------------------------------------
//
// The rule is pure over what it is handed (`ChromiumMachine`), so it is asked here about trees
// that exist only as a map: a path to the names under it. Cheap tier.

/// A machine with these variables and this tree, where a directory is a path the tree lists.
let private machine (variables: (string * string) list) (tree: (string * string list) list) : ChromiumMachine =
    let variables = Map.ofList variables
    let tree = Map.ofList tree
    { Variable = fun name -> Map.tryFind name variables
      Entries = fun dir -> Map.tryFind dir tree |> Option.defaultValue []
      IsDirectory = fun path -> tree.ContainsKey path
      Resolve = id
      Join = fun a b -> a + "/" + b }

/// A browsers directory holding one revision, its executable where x86_64-linux has it.
let private oneRevision =
    [ "/browsers", [ "chromium-1243" ]
      "/browsers/chromium-1243", [ "chrome-linux64" ]
      "/browsers/chromium-1243/chrome-linux64", [ "chrome"; "chrome_crashpad_handler" ] ]

let findingTests =
    testList "Finding Chromium (Fable.NodeExtras.ChromiumExecutable)" [

        testCase "CHROMIUM_PATH is taken as given, ahead of the browsers directory" <| fun () ->
            let found =
                ChromiumExecutable.find (
                    machine [ "CHROMIUM_PATH", "/opt/mine"; "PLAYWRIGHT_BROWSERS_PATH", "/browsers" ] oneRevision
                )
            Expect.equal found (Ok "/opt/mine") "the override"

        testCase "the executable is found by its name under a chromium revision" <| fun () ->
            let found = ChromiumExecutable.find (machine [ "PLAYWRIGHT_BROWSERS_PATH", "/browsers" ] oneRevision)
            Expect.equal found (Ok "/browsers/chromium-1243/chrome-linux64/chrome") "the revision's chrome"

        // The cut-down browser Playwright installs beside the real one. An underscore, not a
        // hyphen, is all that tells them apart.
        testCase "the headless shell is not taken for Chromium" <| fun () ->
            let shellOnly =
                [ "/browsers", [ "chromium_headless_shell-1243" ]
                  "/browsers/chromium_headless_shell-1243", [ "chrome-linux" ]
                  "/browsers/chromium_headless_shell-1243/chrome-linux", [ "chrome" ] ]
            let found = ChromiumExecutable.find (machine [ "PLAYWRIGHT_BROWSERS_PATH", "/browsers" ] shellOnly)
            Expect.isError found "no chromium-* revision, so no Chromium"

        testCase "a CHROMIUM_PATH set to nothing is not set" <| fun () ->
            let found =
                ChromiumExecutable.find (machine [ "CHROMIUM_PATH", ""; "PLAYWRIGHT_BROWSERS_PATH", "/browsers" ] oneRevision)
            Expect.equal found (Ok "/browsers/chromium-1243/chrome-linux64/chrome") "the browsers directory's"

        // The refusal is what `check Chromium` prints on a box without the browser, so it says
        // what to set.
        testCase "with neither variable set, the refusal says to set CHROMIUM_PATH" <| fun () ->
            let said =
                match ChromiumExecutable.find (machine [] oneRevision) with
                | Ok path -> sprintf "found %s" path
                | Error why -> why
            Expect.stringContains said "set CHROMIUM_PATH" "what to do"
    ]

// --- The browser ----------------------------------------------------------------------------

/// The suite's Chromium, once a case has asked for it.
let mutable private started : JS.Promise<Chromium.Launched> option = None

/// The suite's Chromium, launched by the first case to ask. A launch that failed fails every
/// case that asks, each with the launcher's account of why. The launcher's waits for the
/// browser are spent on this binding's clock, which is why it is marked.
[<Waits>]
let private browser () : Async<Chromium.Launched> =
    let launch () =
        Chromium.launch Promise.sleep 800 600 []
        |> Promise.map (function
            | Ok launched -> launched
            | Error why -> failwithf "the suite's Chromium did not start: %s" why)
    let launching =
        match started with
        | Some launching -> launching
        | None ->
            let launching = launch ()
            started <- Some launching
            launching
    Interop.awaitPromise launching

/// What a command came to: `None` when the browser accepted it, its refusal otherwise.
let private refusal (sent: JS.Promise<unit>) : Async<string option> =
    async {
        let! outcome = Interop.awaitPromise sent |> Async.Catch
        match outcome with
        | Choice1Of2 () -> return None
        | Choice2Of2 error -> return Some error.Message
    }

/// An event as a failure prints it: a screencast frame without its picture, which is base64
/// that would bury every other line.
let private described (event: Cdp.Event) : string =
    match event with
    | Cdp.Event.ScreencastFrame _ -> "ScreencastFrame"
    | other -> sprintf "%A" other

/// Hold for an event the case caused, and say which events DID arrive when it never does.
let private heard (cdp: Cdp.Session) (what: string) (wanted: Cdp.Event -> bool) : Async<unit> =
    async {
        let! held = settledWithin 10_000 (fun () -> cdp.Events |> Seq.exists wanted)
        let instead = cdp.Events |> Seq.map described |> String.concat "; "
        Expect.isTrue held (sprintf "%s, which never arrived. Heard instead: [%s]" what instead)
    }

/// A page whose document is `text`, which it shows as plain text.
let private plainText (text: string) : string = "data:text/plain," + text

/// Navigate, and hold until the main frame has: its `FrameNavigated` for this address.
///
/// A `data:` document gets a renderer process of its own, and between a navigation starting and
/// the new document committing, the session is attached to no page: a screencast asked for then
/// is refused ("Not attached to an active page"), as this suite's first run found. `Page.navigate`
/// answers at the start, so a case that only sent one would leave that window open under
/// whatever ran next.
let private landOn (cdp: Cdp.Session) (address: string) : Async<unit> =
    async {
        do! Cdp.Page.navigate cdp address |> Interop.awaitPromise
        do! heard cdp (sprintf "FrameNavigated (%s, the main frame)" address) ((=) (Cdp.Event.FrameNavigated (address, true)))
    }

/// How many sessions the cases have opened, which names the page each one lands on.
let mutable private sessions = 0

/// A CDP session of the case's own on the browser's page, closed however the case ends. It
/// starts on a page of its own that has finished arriving, so a navigation an earlier case left
/// in flight cannot land under this one. `Page` is enabled for that, and stays enabled.
let private withSession (body: Cdp.Session -> Async<unit>) : Async<unit> =
    async {
        let! launched = browser ()
        let! cdp = Cdp.connect launched.Page |> Interop.awaitPromise
        sessions <- sessions + 1
        try
            do! Cdp.Page.enable cdp |> Interop.awaitPromise
            do! landOn cdp (plainText (sprintf "session-%d" sessions))
            return! body cdp
        finally
            cdp.Close ()
    }

/// A page that never stops painting: a square sliding forever, in CSS. The compositor draws it
/// every frame, so a screencast of it always has a next frame to send.
let private moving =
    "data:text/html,<div style=\"width:40px;height:40px;background:red;animation:slide 1s linear infinite\"></div>"
    + "<style>@keyframes slide{to{transform:translateX(300px)}}</style>"

/// The first eight bytes of every PNG.
let private pngSignature = [| 0x89uy; 0x50uy; 0x4Euy; 0x47uy; 0x0Duy; 0x0Auy; 0x1Auy; 0x0Auy |]

let tests =
    testList "The CDP client against a real Chromium (Fable.NodeExtras)" [

        // One case per command, the commands `cdpTests` checks the spelling of, sent to the
        // browser that has to read them. "Accepted" is answered without an error: a method it
        // does not have, and parameters it will not take, are both refusals.
        yield!
            [ for (label, send, _, _) in NodeExtras.wireCases ->
                  testCaseAsync (sprintf "Chromium accepts %s" label) <| withSession (fun cdp ->
                      async {
                          let! refused = refusal (send cdp)
                          Expect.isNone refused "the browser's answer to the command"
                      }) ]

        // Removing needs the identifier adding answered, so it is not in the list above.
        testCaseAsync "Chromium accepts Page.removeScriptToEvaluateOnNewDocument" <| withSession (fun cdp ->
            async {
                let! script = Cdp.Page.addScriptToEvaluateOnNewDocument cdp "" |> Interop.awaitPromise
                let! refused = refusal (Cdp.Page.removeScriptToEvaluateOnNewDocument cdp script)
                Expect.isNone refused "the browser's answer to the command"
            })

        // `Runtime.evaluate` is accepted when an answer comes back at all; that it comes back
        // as the kind asked for is the reading `cdpTests` cannot make without a real answer.
        testCaseAsync "a string Chromium evaluates comes back as the text" <| withSession (fun cdp ->
            async {
                let! answer = Cdp.Runtime.evaluateText cdp (Encode.toString 0 (Encode.string "evaluated")) |> Interop.awaitPromise
                Expect.equal answer (Cdp.Evaluated.Returned "evaluated") "the literal's value"
            })

        testCaseAsync "a number Chromium evaluates comes back as the number" <| withSession (fun cdp ->
            async {
                let! answer = Cdp.Runtime.evaluateNumber cdp (Encode.toString 0 (Encode.float 1234.5)) |> Interop.awaitPromise
                Expect.equal answer (Cdp.Evaluated.Returned 1234.5) "the literal's value"
            })

        testCaseAsync "a screenshot Chromium takes decodes to PNG bytes" <| withSession (fun cdp ->
            async {
                let! picture = Cdp.Page.captureScreenshot cdp |> Interop.awaitPromise
                let bytes = Node.Api.buffer.Buffer.from (picture, Node.Buffer.BufferEncoding.Base64)
                let head = [| for i in 0 .. min 8 bytes.length - 1 -> byte bytes.[i] |]
                Expect.equal head pngSignature "the PNG signature"
            })

        // Every session already lands this way (`withSession`), so a regression here reds every
        // case of the suite; this is the one whose name says which reading broke.
        testCaseAsync "navigating the page is heard as the main frame navigating" <| withSession (fun cdp ->
            landOn cdp (plainText "frame-navigated"))

        testCaseAsync "a navigation to an invalid URL is refused with Chromium's reason" <| withSession (fun cdp ->
            async {
                let! refused = refusal (Cdp.Page.navigate cdp "not a url")
                Expect.stringContains (Option.defaultValue "accepted" refused) "Cannot navigate to invalid URL" "Chromium's words"
            })

        // A `data:` document is fetched like any other as far as the Network domain says, so the
        // two events need no server. The third reading, a redirect's status, does: a `data:` URL
        // never redirects, and a server is `Ports`. `cdpTests` keeps that one.
        testCaseAsync "a navigation's document request is heard" <| withSession (fun cdp ->
            async {
                let address = plainText "request-sent"
                do! Cdp.Network.enable cdp |> Interop.awaitPromise
                do! landOn cdp address
                do! heard cdp (sprintf "RequestSent (Document, %s)" address) ((=) (Cdp.Event.RequestSent (Some "Document", address, None)))
            })

        testCaseAsync "a navigation's document response is heard with its status" <| withSession (fun cdp ->
            async {
                let address = plainText "response-received"
                do! Cdp.Network.enable cdp |> Interop.awaitPromise
                do! landOn cdp address
                do! heard cdp (sprintf "ResponseReceived (Document, %s, 200)" address) ((=) (Cdp.Event.ResponseReceived (Some "Document", address, 200)))
            })

        // Chromium sends a screencast at most three frames ahead of the acknowledgements it has
        // had (measured: three, from a page that never stops painting, with none acknowledged),
        // so a session that did not acknowledge would hear three and then nothing. Ten is past
        // that by enough that only acknowledged frames reach it.
        testCaseAsync "screencast frames keep coming once acknowledged" <| withSession (fun cdp ->
            async {
                let frames () = cdp.Events |> Seq.filter (function Cdp.Event.ScreencastFrame _ -> true | _ -> false) |> Seq.length
                do! landOn cdp moving
                do! Cdp.Page.startScreencast cdp 1 400 300 |> Interop.awaitPromise
                let! kept = settledWithin 10_000 (fun () -> frames () >= 10)
                do! Cdp.Page.stopScreencast cdp |> Interop.awaitPromise
                Expect.isTrue kept (sprintf "ten frames from a page that never stops painting; %d arrived" (frames ()))
            })
    ]
