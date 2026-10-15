namespace Fable.NodeExtras

// The Chrome DevTools Protocol, the slice `tools/Yession.Frames` speaks to a headless Chromium.
//
// A command is a JSON object whose keys the browser reads by NAME: `{"id":3,"method":"Page.navigate",
// "params":{"url":"..."}}`. Written as an object literal at the call site, a key is a string
// nothing checks, and a misspelt one is not an error but a command the browser answers with a
// protocol fault — or, worse, one it takes for something else and acts on. So every command
// this camera uses is declared ONCE, here: an interface that names the keys, filled through
// `jsOptions` so the compiler spells them, behind a function that takes the arguments the
// command means. A caller never sees the object Chromium reads, and the object never leaves
// this file.
//
// The answers are typed the same way, for the same reason. A reply is read through an
// interface that names what the command is documented to answer, and what a caller gets out is
// a plain F# value — a string, a number, a `ScriptId` it can only hand back. The one answer
// whose type no declaration can state, what an arbitrary JavaScript expression evaluated to, is
// asked for by the type the caller expects (`evaluateText`, `evaluateNumber`) and checked
// against it in the page's own terms (`typeof`), so a value of another kind arrives as
// `Unexpected` rather than as an `unbox` that asserted.
//
// The same holds for the one thing asked of the browser over plain HTTP: which page to connect
// to (`pageOn`), read off the list of targets it serves the same way an answer is read.
//
// Only what the camera uses is declared. The bar for a new command is that something calls it.
//
// Here and not in a project of its own because the socket a session speaks over is
// `WebSockets.connect`, in this project, and a protocol client has nothing else to stand on.

open System.Collections.Generic
open Fable.Core
open Fable.Core.JsInterop

[<RequireQualifiedAccess>]
module Cdp =

    // --- What a caller is handed ------------------------------------------------------------

    /// The identifier `Page.addScriptToEvaluateOnNewDocument` answered with. Opaque: the
    /// browser minted it, and the only thing it is good for is handing back to
    /// `Page.removeScriptToEvaluateOnNewDocument`.
    type ScriptId =
        interface
        end

    /// What the browser said on its own, without being asked. Only the events the camera reads
    /// are decoded; every other is dropped on arrival, because nothing here has a use for it.
    [<RequireQualifiedAccess>]
    type Event =
        /// `Page.screencastFrame`: one painted frame, as base64 PNG, and when it was painted in
        /// seconds since the epoch. The timestamp is optional in the protocol, so it is here:
        /// a frame that says nothing of when it was painted cannot be put on the page's clock,
        /// and what to do about that is the caller's decision.
        | ScreencastFrame of pngBase64: string * epochSeconds: float option
        /// `Network.requestWillBeSent`: the kind of resource when the browser said, the
        /// address, and the status of the redirect that led here when this request is the
        /// follow-up to one.
        | RequestSent of resourceType: string option * url: string * redirectedWith: int option
        /// `Network.responseReceived`.
        | ResponseReceived of resourceType: string option * url: string * status: int
        /// `Page.frameNavigated`; `isMainFrame` is a frame with no parent.
        | FrameNavigated of url: string * isMainFrame: bool

    /// What an expression evaluated to, by the type the caller asked for.
    [<RequireQualifiedAccess>]
    type Evaluated<'T> =
        /// The value, of the kind asked for.
        | Returned of 'T
        /// It evaluated, to something of another kind. The JSON of what it was.
        | Unexpected of json: string
        /// It threw. The browser's account of where and what, as JSON.
        | Threw of details: string

    // --- What the wire carries --------------------------------------------------------------

    /// What the browser's `error` member says when it refuses a command.
    type internal Fault =
        abstract message: string

    /// A frame from the browser, parsed and not yet read. It is the answer to a command when it
    /// names an `id`, and an event when it names a `method`.
    type internal Incoming =
        abstract id: int option
        abstract ``method``: string option
        abstract error: Fault option

    [<Emit("JSON.parse($0)")>]
    let internal parse (text: string) : Incoming = jsNative

    /// The `params` of an event, as the interface the caller names. The name is the event's
    /// documented one, and each use sits beside the case that decodes it.
    [<Emit("$0.params")>]
    let internal paramsOf<'P> (incoming: Incoming) : 'P = jsNative

    /// The `result` of an answer, on the same terms as `paramsOf`.
    [<Emit("$0.result")>]
    let internal resultOf<'R> (incoming: Incoming) : 'R = jsNative

    /// What goes out.
    type internal Envelope<'Params> =
        abstract id: int with get, set
        abstract ``method``: string with get, set
        abstract ``params``: 'Params with get, set

    // --- The events' parameters ---------------------------------------------------------------

    type internal ScreencastMetadata =
        abstract timestamp: float option

    type internal ScreencastFrameParams =
        abstract data: string
        abstract metadata: ScreencastMetadata
        abstract sessionId: int

    type internal RequestInfo =
        abstract url: string

    type internal RedirectInfo =
        abstract status: int

    type internal RequestSentParams =
        abstract ``type``: string option
        abstract request: RequestInfo
        abstract redirectResponse: RedirectInfo option

    type internal ResponseInfo =
        abstract url: string
        abstract status: int

    type internal ResponseReceivedParams =
        abstract ``type``: string option
        abstract response: ResponseInfo

    type internal FrameInfo =
        abstract parentId: string option
        abstract url: string

    type internal FrameNavigatedParams =
        abstract frame: FrameInfo

    type internal ScreencastAckParams =
        abstract sessionId: int with get, set

    // --- A session ---------------------------------------------------------------------------

    /// One conversation with a browser, over whatever carries its text frames. `connect` is the
    /// real carrier; the constructor takes the carrier as two functions so that the protocol —
    /// what goes out for each command, what an answer and an event decode to — can be asked
    /// about without a browser, by feeding `Receive` the frames one would have sent.
    ///
    /// A command is answered by the frame carrying its `id`, whatever order the answers come
    /// in. A frame naming a `method` is an event, and is kept in `Events` if it is one of the
    /// four decoded.
    type Session(transmit: string -> unit, close: unit -> unit) =
        let pending = Dictionary<int, Incoming -> unit> ()
        let events = ResizeArray<Event> ()
        let mutable nextId = 1

        /// Every event heard so far, in the order it arrived.
        member _.Events: ResizeArray<Event> = events

        /// End the conversation. Commands still awaiting an answer are not answered.
        member _.Close() : unit = close ()

        /// Send a command and wait for its answer. A refusal rejects, with the command's name and
        /// the browser's reason: a protocol fault is a fault in the caller, and the alternative —
        /// the empty answer a swallowed one reads as — surfaces somewhere else.
        member internal _.Call<'Params>(methodName: string, parameters: 'Params) : JS.Promise<Incoming> =
            Promise.create (fun resolve reject ->
                let id = nextId
                nextId <- nextId + 1

                pending.[id] <-
                    (fun reply ->
                        match reply.error with
                        | Some fault -> reject (exn $"CDP %s{methodName} was refused: %s{fault.message}")
                        | None -> resolve reply)

                let envelope =
                    jsOptions<Envelope<'Params>> (fun o ->
                        o.id <- id
                        o.``method`` <- methodName
                        o.``params`` <- parameters)

                transmit (JS.JSON.stringify envelope))

        /// A screencast frame is acknowledged as it arrives, or the browser stops sending them.
        /// An acknowledgement the browser refuses (a frame that was in flight when the
        /// screencast stopped) is not a fault of anyone's, so its refusal is not heard.
        member private this.Acknowledge(sessionId: int) : unit =
            let parameters = jsOptions<ScreencastAckParams> (fun o -> o.sessionId <- sessionId)

            this.Call("Page.screencastFrameAck", parameters)
            |> Promise.map ignore
            |> Promise.catch ignore
            |> ignore

        member private this.Hear(name: string, incoming: Incoming) : unit =
            match name with
            | "Page.screencastFrame" ->
                let frame = paramsOf<ScreencastFrameParams> incoming
                this.Acknowledge frame.sessionId
                events.Add (Event.ScreencastFrame (frame.data, frame.metadata.timestamp))
            | "Network.requestWillBeSent" ->
                let sent = paramsOf<RequestSentParams> incoming

                events.Add (
                    Event.RequestSent (
                        sent.``type``,
                        sent.request.url,
                        sent.redirectResponse |> Option.map (fun redirect -> redirect.status)
                    )
                )
            | "Network.responseReceived" ->
                let received = paramsOf<ResponseReceivedParams> incoming
                events.Add (Event.ResponseReceived (received.``type``, received.response.url, received.response.status))
            | "Page.frameNavigated" ->
                let navigated = paramsOf<FrameNavigatedParams> incoming
                events.Add (Event.FrameNavigated (navigated.frame.url, navigated.frame.parentId.IsNone))
            | _ -> ()

        /// Take one text frame from the browser.
        member this.Receive(text: string) : unit =
            let incoming = parse text

            match incoming.id with
            | Some id when pending.ContainsKey id ->
                let settle = pending.[id]
                pending.Remove id |> ignore
                settle incoming
            | _ ->
                match incoming.``method`` with
                | Some name -> this.Hear (name, incoming)
                | None -> ()

    /// Open a session on the address a browser gave for one of its pages
    /// (`webSocketDebuggerUrl`). The promise settles once the socket is open.
    ///
    /// CDP is a TEXT protocol, and the frame type says so rather than a cast: a binary frame on
    /// this wire is not a message that failed to parse, it is a wire that is not CDP.
    let connect (url: string) : JS.Promise<Session> =
        Promise.create (fun resolve _ ->
            let socket = WebSockets.connect url
            let session = Session ((fun text -> socket.sendText text), (fun () -> socket.close ()))

            socket.onMessage (fun frame ->
                match WebSockets.payload frame with
                | Frame.Binary _ -> ()
                | Frame.Text text -> session.Receive text)

            socket.onOpen (fun () -> resolve session))

    // --- Finding a page to connect to -------------------------------------------------------

    /// One entry of the list a browser serves at `/json/list`: a target it offers a debugger.
    /// The address is absent from a target that cannot take one.
    type internal Target =
        abstract ``type``: string
        abstract webSocketDebuggerUrl: string option

    [<Emit("JSON.parse($0)")>]
    let internal parseTargets (text: string) : Target array = jsNative

    /// The address `connect` wants for a page of the browser debugging on `port` (the first
    /// line of the `DevToolsActivePort` file it writes into its profile), read off the targets
    /// it lists; nothing while it lists no page. A browser not listening yet rejects, as any
    /// refused fetch does, so a caller polling a fresh one retries both.
    let pageOn (port: string) : JS.Promise<string option> =
        Fetch.fetchUnsafe $"http://127.0.0.1:%s{port}/json/list" []
        |> Promise.bind (fun listed -> listed.text ())
        |> Promise.map (fun text ->
            parseTargets text
            |> Array.tryFind (fun target -> target.``type`` = "page")
            |> Option.bind (fun page -> page.webSocketDebuggerUrl))

    // --- The commands -----------------------------------------------------------------------

    /// Commands that take nothing: `{}`, which is what the browser reads an absent `params` as.
    type internal NoParams =
        interface
        end

    let internal noParams () : NoParams = jsOptions<NoParams> ignore

    [<RequireQualifiedAccess>]
    module Page =

        type internal NavigateParams =
            abstract url: string with get, set

        type internal CaptureScreenshotParams =
            abstract format: string with get, set

        type internal AddScriptParams =
            abstract source: string with get, set

        type internal AddScriptResult =
            abstract identifier: ScriptId

        type internal RemoveScriptParams =
            abstract identifier: ScriptId with get, set

        type internal StartScreencastParams =
            abstract format: string with get, set
            abstract everyNthFrame: int with get, set
            abstract maxWidth: int with get, set
            abstract maxHeight: int with get, set

        type internal CapturedScreenshot =
            abstract data: string

        /// `Page.enable`.
        let enable (cdp: Session) : JS.Promise<unit> =
            cdp.Call ("Page.enable", noParams ()) |> Promise.map ignore

        /// `Page.navigate`. Answers once the browser has STARTED the navigation, not once the
        /// page has loaded: that is a thing to poll for, with `Runtime.evaluate`.
        let navigate (cdp: Session) (url: string) : JS.Promise<unit> =
            cdp.Call ("Page.navigate", jsOptions<NavigateParams> (fun o -> o.url <- url))
            |> Promise.map ignore

        /// `Page.captureScreenshot` as PNG, the one format asked for. Answers the picture as
        /// base64.
        let captureScreenshot (cdp: Session) : JS.Promise<string> =
            cdp.Call ("Page.captureScreenshot", jsOptions<CaptureScreenshotParams> (fun o -> o.format <- "png"))
            |> Promise.map (fun reply -> (resultOf<CapturedScreenshot> reply).data)

        /// `Page.addScriptToEvaluateOnNewDocument`: `source` runs in every document the page
        /// loads from now on, before any script of the document does.
        let addScriptToEvaluateOnNewDocument (cdp: Session) (source: string) : JS.Promise<ScriptId> =
            cdp.Call ("Page.addScriptToEvaluateOnNewDocument", jsOptions<AddScriptParams> (fun o -> o.source <- source))
            |> Promise.map (fun reply -> (resultOf<AddScriptResult> reply).identifier)

        /// `Page.removeScriptToEvaluateOnNewDocument`: what the identifier it was given names
        /// stops running in documents loaded from now on.
        let removeScriptToEvaluateOnNewDocument (cdp: Session) (script: ScriptId) : JS.Promise<unit> =
            cdp.Call (
                "Page.removeScriptToEvaluateOnNewDocument",
                jsOptions<RemoveScriptParams> (fun o -> o.identifier <- script)
            )
            |> Promise.map ignore

        /// `Page.startScreencast` as PNG, the one format asked for. The browser then sends a
        /// `Event.ScreencastFrame` per painted frame (every `everyNthFrame`th), each scaled to
        /// fit `maxWidth` by `maxHeight`; this session acknowledges them as they arrive.
        let startScreencast (cdp: Session) (everyNthFrame: int) (maxWidth: int) (maxHeight: int) : JS.Promise<unit> =
            cdp.Call (
                "Page.startScreencast",
                jsOptions<StartScreencastParams> (fun o ->
                    o.format <- "png"
                    o.everyNthFrame <- everyNthFrame
                    o.maxWidth <- maxWidth
                    o.maxHeight <- maxHeight)
            )
            |> Promise.map ignore

        /// `Page.stopScreencast`.
        let stopScreencast (cdp: Session) : JS.Promise<unit> =
            cdp.Call ("Page.stopScreencast", noParams ()) |> Promise.map ignore

    [<RequireQualifiedAccess>]
    module Network =

        /// `Network.enable`: the browser then sends `Event.RequestSent` and
        /// `Event.ResponseReceived`.
        let enable (cdp: Session) : JS.Promise<unit> =
            cdp.Call ("Network.enable", noParams ()) |> Promise.map ignore

    [<RequireQualifiedAccess>]
    module Runtime =

        type internal EvaluateParams =
            abstract expression: string with get, set
            abstract awaitPromise: bool with get, set
            abstract returnByValue: bool with get, set

        /// The browser's account of an exception, kept only to be said back as JSON.
        type internal ExceptionDetails =
            interface
            end

        /// What an expression evaluated to. `value` is read only through the three readers
        /// below, each of which tests the kind first.
        type internal RemoteObject =
            interface
            end

        type internal EvaluateResult =
            abstract result: RemoteObject
            abstract exceptionDetails: ExceptionDetails option

        [<Emit("JSON.stringify($0)")>]
        let private jsonOfDetails (details: ExceptionDetails) : string = jsNative

        // Each of these is a function over its one argument rather than a bare member access, so
        // the argument is evaluated once and the page's own `typeof` decides whether it is a
        // string, a number or neither.
        [<Emit("(function (remote) { return typeof remote.value === 'string' ? remote.value : undefined })($0)")>]
        let private textOf (remote: RemoteObject) : string option = jsNative

        [<Emit("(function (remote) { return typeof remote.value === 'number' ? remote.value : undefined })($0)")>]
        let private numberOf (remote: RemoteObject) : float option = jsNative

        /// What `value` is, as JSON; the word `undefined` when it is not there at all, which
        /// is how CDP answers an expression that evaluated to nothing.
        [<Emit("(function (remote) { return String(JSON.stringify(remote.value)) })($0)")>]
        let private jsonOf (remote: RemoteObject) : string = jsNative

        /// `Runtime.enable`.
        let enable (cdp: Session) : JS.Promise<unit> =
            cdp.Call ("Runtime.enable", noParams ()) |> Promise.map ignore

        /// `Runtime.evaluate`, awaited and by value, read as `kind` says.
        let private evaluate
            (cdp: Session)
            (expression: string)
            (kind: RemoteObject -> 'T option)
            : JS.Promise<Evaluated<'T>> =
            cdp.Call (
                "Runtime.evaluate",
                jsOptions<EvaluateParams> (fun o ->
                    o.expression <- expression
                    o.awaitPromise <- true
                    o.returnByValue <- true)
            )
            |> Promise.map (fun reply ->
                let answer = resultOf<EvaluateResult> reply

                match answer.exceptionDetails with
                | Some details -> Evaluated.Threw (jsonOfDetails details)
                | None ->
                    match kind answer.result with
                    | Some value -> Evaluated.Returned value
                    | None -> Evaluated.Unexpected (jsonOf answer.result))

        /// Evaluate `expression`, expecting a string.
        let evaluateText (cdp: Session) (expression: string) : JS.Promise<Evaluated<string>> =
            evaluate cdp expression textOf

        /// Evaluate `expression`, expecting a number.
        let evaluateNumber (cdp: Session) (expression: string) : JS.Promise<Evaluated<float>> =
            evaluate cdp expression numberOf

        /// Evaluate `expression` and say what it was, as JSON, whatever it was. Never
        /// `Unexpected`: the kind is the question being asked.
        let evaluateJson (cdp: Session) (expression: string) : JS.Promise<Evaluated<string>> =
            evaluate cdp expression (fun remote -> Some (jsonOf remote))

    [<RequireQualifiedAccess>]
    module Emulation =

        type internal DeviceMetricsParams =
            abstract width: int with get, set
            abstract height: int with get, set
            abstract deviceScaleFactor: float with get, set
            abstract mobile: bool with get, set

        type internal FocusEmulationParams =
            abstract enabled: bool with get, set

        type internal TouchEmulationParams =
            abstract enabled: bool with get, set
            abstract maxTouchPoints: int with get, set

        /// `Emulation.setDeviceMetricsOverride`: a TRUE device viewport. Headless Chromium
        /// clamps its window to about 500px wide, so a `--window-size` phone is a 500px layout
        /// cropped to 390 — overflow hides and wrapping never happens.
        let setDeviceMetricsOverride
            (cdp: Session)
            (width: int)
            (height: int)
            (deviceScaleFactor: float)
            (mobile: bool)
            : JS.Promise<unit> =
            cdp.Call (
                "Emulation.setDeviceMetricsOverride",
                jsOptions<DeviceMetricsParams> (fun o ->
                    o.width <- width
                    o.height <- height
                    o.deviceScaleFactor <- deviceScaleFactor
                    o.mobile <- mobile)
            )
            |> Promise.map ignore

        /// `Emulation.clearDeviceMetricsOverride`.
        let clearDeviceMetricsOverride (cdp: Session) : JS.Promise<unit> =
            cdp.Call ("Emulation.clearDeviceMetricsOverride", noParams ()) |> Promise.map ignore

        /// `Emulation.setFocusEmulationEnabled`: a headless page is never the focused window, so
        /// `:focus` matches nothing; this makes the page believe it is frontmost.
        let setFocusEmulationEnabled (cdp: Session) (enabled: bool) : JS.Promise<unit> =
            cdp.Call (
                "Emulation.setFocusEmulationEnabled",
                jsOptions<FocusEmulationParams> (fun o -> o.enabled <- enabled)
            )
            |> Promise.map ignore

        /// `Emulation.setTouchEmulationEnabled`: no hover, a coarse pointer, and up to
        /// `maxTouchPoints` fingers.
        let setTouchEmulationEnabled (cdp: Session) (enabled: bool) (maxTouchPoints: int) : JS.Promise<unit> =
            cdp.Call (
                "Emulation.setTouchEmulationEnabled",
                jsOptions<TouchEmulationParams> (fun o ->
                    o.enabled <- enabled
                    o.maxTouchPoints <- maxTouchPoints)
            )
            |> Promise.map ignore
