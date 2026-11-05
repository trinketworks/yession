module Yession.Tests.Loopback

// The loopback servers the browser suite starts, and the static server for its harness page.
//
// Two readers use this file. The browser suite (`Browser.fs`, `Bench.fs`) compiles it on the
// .NET CLR. And `tasks.fsx` `#load`s it for `frames --harness`, so the camera photographs the
// harness page as the suite serves it, and not as a copy of the server serves it. So it is
// plain F# with only `System` referenced. Fable never compiles it.

#if !FABLE_COMPILER

open System.IO
open System.Net
open System.Net.Sockets

// --- Loopback servers, on ports nobody chose ----------------------------------------------
//
// Nothing in the browser suite names a port, and no case can ask for one. Hand-picked numbers are what
// that replaces, and they had collided: the editor family took `EDITOR_PORT + n` per case with
// five offsets used twice over, and every other constant here sat inside the block those 48
// cases occupied. Two suites that bind one port cannot run at once, and the loser does not fail
// legibly — it dies at `Start` with EADDRINUSE, or waits out a readiness line that never comes
// and reports thirty seconds later as a timeout naming the wait rather than the fault. That is
// this tier's worst failure mode, and it cost four separate runs an hour of diagnosis apiece.
//
// Renumbering would only have restocked the hat the next case picks from. So the rule instead:
// whatever STARTS a server hands back the origin it came up on, and nothing else can name one.
// There is then no number for two cases to share, and adding a case requires knowing nothing
// about what any other case bound.

/// A loopback port nothing was listening on a moment ago: taken at `:0`, so the OS chooses it,
/// and released. Private to `listenOnLoopback`, which binds it at once itself: a number handed
/// to anyone else to bind later is a number anything can take in between, and it failed the
/// suite that way more than once (a Manager told `--port <n>`, a stand-in GitHub bound after
/// the session that dials it had booted). A server whose address another process must be told
/// is started FIRST, here or as a door in front of it, and its origin handed on.
let private freeLoopbackPort () : int =
    let probe = new TcpListener (IPAddress.Loopback, 0)
    probe.Start ()
    let port = (probe.LocalEndpoint :?> IPEndPoint).Port
    probe.Stop ()
    port

/// A server this file started, and the origin it answers on — `http://127.0.0.1:<port>`, with
/// no trailing slash. The two travel together because a caller holding one without the other is
/// a caller that picked a port.
type internal Serving =
    { Listener : HttpListener
      Origin : string }
    /// This server's address for an absolute path (`"/"`, `"/s/mounted/"`).
    member this.At (path: string) : string = this.Origin + path
    member this.Stop () : unit = this.Listener.Stop ()

/// Start an `HttpListener` on a loopback origin of its own.
///
/// There is no `:0` to hand it — a prefix names a port — so one is taken and released and
/// bound here, by the one party that took it, before the origin is handed to anybody. A number
/// taken in that window costs this listener another try and nobody else anything: the origin
/// does not exist outside this function until `Start` has bound it. That is what separates this
/// from the race the suite has removed elsewhere, where the number escaped to another process
/// that bound it later and had no way to choose again.
let internal listenOnLoopback () : Serving =
    let rec attempt (triesLeft: int) =
        let origin = sprintf "http://127.0.0.1:%d" (freeLoopbackPort ())
        let listener = new HttpListener ()
        listener.Prefixes.Add (origin + "/")
        try
            listener.Start ()
            { Listener = listener; Origin = origin }
        with :? HttpListenerException when triesLeft > 1 ->
            listener.Close ()
            attempt (triesLeft - 1)
    attempt 5

// --- The harness page (`tests/browser/editor-harness.html`) ---------------------------------

/// Where the harness page and its build are, relative to the repository root, where the suite
/// runs. `tasks.fsx` joins it to the root.
let internal harnessRoot = "tests/browser"

/// Answer one request a server of this suite took, with `respond`, and close it — and let no
/// failure to answer it escape.
///
/// Every such server answers on a loop it started with `Async.Start`, which has nowhere to
/// send a failure: one that escapes ends the test process, and with it every case running
/// beside the one it belonged to. Answering fails for reasons that belong to the moment, not
/// to any case: the page that asked has navigated away or closed, or the case that owned the
/// server has finished and stopped it while a reply was on its way. A release run lost its
/// whole browser tier that way, to a forwarder whose 502 met a response that was already
/// gone. So a request that cannot be answered is said, with `what` and the request, and
/// dropped; the server goes on to the next.
let internal answer (what: string) (ctx: HttpListenerContext) (respond: Async<unit>) : Async<unit> =
    let asked = sprintf "%s %s" ctx.Request.HttpMethod ctx.Request.RawUrl
    async {
        match! Async.Catch respond with
        | Choice1Of2 () -> ()
        | Choice2Of2 fault -> eprintfn "%s: %s was not answered: %s" what asked fault.Message
        try ctx.Response.Close () with _ -> ()
    }

/// A tiny read-only static file server over `HttpListener` (the harness page + its bundle).
/// Returns the server — the listener to stop, and the origin it came up on, which is the only
/// place that origin is stated; requests are served on a background loop.
let internal serveStatic (root: string) : Serving =
    let served = listenOnLoopback ()
    let listener = served.Listener
    let rec loop () =
        async {
            match! Async.Catch (listener.GetContextAsync () |> Async.AwaitTask) with
            | Choice1Of2 ctx ->
                do!
                    async {
                        let rel = ctx.Request.Url.AbsolutePath.TrimStart '/'
                        let rel = if rel = "" then "editor-harness.html" else rel
                        let path = Path.Combine (root, rel)
                        if File.Exists path then
                            let bytes = File.ReadAllBytes path
                            ctx.Response.ContentType <-
                                if path.EndsWith ".js" then "text/javascript"
                                elif path.EndsWith ".html" then "text/html"
                                // A stylesheet served as `application/octet-stream` is ignored by
                                // the browser, silently — which makes every layout measured on
                                // this page a fiction, and looks exactly like CSS that does not
                                // work.
                                elif path.EndsWith ".css" then "text/css"
                                else "application/octet-stream"
                            ctx.Response.OutputStream.Write (bytes, 0, bytes.Length)
                        else ctx.Response.StatusCode <- 404
                    }
                    |> answer "the harness page" ctx
                return! loop ()
            | Choice2Of2 _ -> ()   // listener stopped
        }
    Async.Start (loop ())
    served

#endif
