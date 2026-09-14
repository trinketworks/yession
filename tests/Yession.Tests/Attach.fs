module Yession.Tests.Attach

// Attaching a terminal to somebody else's byte stream (Plan 16, part D), over a real
// WebSocket against a loopback provider.
//
// The provider below is also the REFERENCE implementation `docs/streams.md` points an
// outside implementer at: the smallest thing that conforms, running on every `check Ports`.
// That is the only reason it cannot rot — a sample nobody executes drifts from the client
// it claims to match, which is what the spec exists to stop.
//
// The provider here is written by hand — an HTTP upgrade, the RFC 6455 accept hash, and
// enough frame handling to echo — rather than built on a library, and that is the point
// rather than an inconvenience: the whole reason for choosing WebSocket over something of
// our own is that a THIRD PARTY can implement the other end. A test that talked to our own
// client through our own server abstraction would prove only that the two agreed.
//
// `Ports`, because the upgrade is the thing being tested and there is no meaningful
// in-memory stand-in for it.
//
// Reading the wire is a separate question from carrying it, and the cheap tier answers that
// one: what a frame MEANS, what a control frame said, and what an ending was are pure
// functions of `app/AttachWs.fs` since the client stopped being a JavaScript program inside an
// `[<Emit>]` string. They are the decisions a provider gets wrong, so they are the ones worth
// asking about on every PR rather than only where a socket can be opened.

open Fable.Core
open Fable.NodeExtras
open Fable.Pyxpecto
open Yession.Domain
open Yession.Domain.Sandboxes
open Yession.Domain.Terminals

let private expect =
    function
    | Ok v -> v
    | Error e -> failwithf "%A" e

/// A loopback provider. `/echo` sends every data frame straight back, answers a `resize`
/// control frame by saying what size it was told, and on `kill` sends the termination frame
/// and then closes. `/abrupt` accepts the upgrade and drops the connection without saying
/// anything — which is the case an in-band termination frame exists FOR, since an abnormal
/// closure carries nothing.
/// Public rather than private to this module: `NodeExtras.fs` drives the WebSocket BINDING
/// against the same provider. A second hand-rolled RFC 6455 server in this suite would be a
/// second thing to keep in step with the spec the first one is the reference for.
type Provider =
    abstract port : int
    abstract stop : unit -> JS.Promise<unit>

[<Emit("""(async () => {
  const http = await import('node:http')
  const crypto = await import('node:crypto')
  const GUID = '258EAFA5-E914-47DA-95CA-C5AB0DC85B11'
  const frame = (opcode, payload) => {
    const body = Buffer.from(payload)
    const head = body.length < 126
      ? Buffer.from([0x80 | opcode, body.length])
      : Buffer.concat([Buffer.from([0x80 | opcode, 126]), (() => { const b = Buffer.alloc(2); b.writeUInt16BE(body.length); return b })()])
    return Buffer.concat([head, body])
  }
  const server = http.createServer((_, res) => { res.statusCode = 426; res.end() })
  server.on('upgrade', (req, socket) => {
    const key = req.headers['sec-websocket-key']
    const accept = crypto.createHash('sha1').update(key + GUID).digest('base64')
    socket.write(
      'HTTP/1.1 101 Switching Protocols\r\n' +
      'Upgrade: websocket\r\nConnection: Upgrade\r\n' +
      'Sec-WebSocket-Accept: ' + accept + '\r\n\r\n')
    // The token an exclusive provider spends on attach rides the url, so a route is the PATH
    // and never the whole of it.
    const path = req.url.split('?')[0]
    if (path === '/abrupt') { setTimeout(() => socket.destroy(), 30); return }
    // A provider that says things on the TEXT channel we have no meaning for: a control type
    // from a later version, and something that is not JSON at all — which is what reaching
    // for a framework's `send_text` to emit device output looks like from here.
    if (path === '/talkative') {
      socket.write(frame(0x1, JSON.stringify({ type: 'from-a-later-version' })))
      socket.write(frame(0x1, 'device output on the wrong channel'))
    }
    // Only the first of those: a conforming provider written against a later spec.
    if (path === '/later-version') {
      socket.write(frame(0x1, JSON.stringify({ type: 'from-a-later-version' })))
    }
    let buffer = Buffer.alloc(0)
    socket.on('data', (chunk) => {
      buffer = Buffer.concat([buffer, chunk])
      // One frame at a time, unfragmented, masked (every client frame is). Enough of RFC
      // 6455 to be a peer; deliberately not a library.
      for (;;) {
        if (buffer.length < 2) return
        const opcode = buffer[0] & 0x0f
        const masked = (buffer[1] & 0x80) !== 0
        let length = buffer[1] & 0x7f
        let offset = 2
        if (length === 126) { if (buffer.length < 4) return; length = buffer.readUInt16BE(2); offset = 4 }
        const maskKey = masked ? buffer.subarray(offset, offset + 4) : null
        if (masked) offset += 4
        if (buffer.length < offset + length) return
        const payload = Buffer.from(buffer.subarray(offset, offset + length))
        if (maskKey) for (let i = 0; i < payload.length; i++) payload[i] ^= maskKey[i % 4]
        buffer = buffer.subarray(offset + length)
        if (opcode === 0x8) { socket.end(); return }
        if (opcode === 0x1) {
          let control = null
          try { control = JSON.parse(payload.toString('utf8')) } catch (e) { control = null }
          if (control && control.type === 'resize') {
            socket.write(frame(0x2, 'sized ' + control.cols + 'x' + control.rows + '\n'))
          } else if (control && control.type === 'kill') {
            socket.write(frame(0x1, JSON.stringify({ type: 'exited', code: 7 })))
            socket.end()
            return
          }
        } else if (opcode === 0x2) {
          socket.write(frame(0x2, 'echo:' + payload.toString('utf8')))
        }
      }
    })
    socket.on('error', () => {})
  })
  await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve))
  return { port: server.address().port, stop: () => new Promise((r) => server.close(() => r())) }
})()""")>]
let startProvider () : JS.Promise<Provider> = jsNative

let private ticket (port: int) (path: string) (capabilities: SourceCapabilities) : AttachTicket =
    { Url = sprintf "ws://127.0.0.1:%d%s" port path
      Capabilities = capabilities
      Label = Some "loopback" }

/// Poll for a condition rather than sleeping a fixed amount: what is being waited on is a
/// round trip over loopback, and a fixed sleep is either flaky or slow.
let private until (predicate: unit -> bool) : Async<bool> =
    let rec loop (remaining: int) =
        async {
            if predicate () then return true
            elif remaining <= 0 then return false
            else
                do! Async.Sleep 20
                return! loop (remaining - 20)
        }
    loop 3000

/// What `console.warn` was told, and the real one put back on `restore`.
///
/// A diagnostic is the only place this client's warning about a TEXT frame goes, so reading it
/// back is the only way to ask what it said — and what it must never contain is the ticket's
/// url, which carries a single-use attach token.
type private WarningLog =
    abstract said : string []
    abstract restore : unit -> unit

[<Emit("""(() => {
  const original = console.warn
  const said = []
  console.warn = (...parts) => { said.push(parts.join(' ')) }
  return { said, restore: () => { console.warn = original } }
})()""")>]
let private captureWarnings () : WarningLog = jsNative

let private device = { SourceCapabilities.byteStream with CanResize = true }

let portsTests =
    testList "Foreign terminal attach (Plan 16)" [

        testCaseAsync "bytes go both ways, and a control frame is control rather than data" <|
            async {
                let! provider = startProvider () |> Async.AwaitPromise
                let received = System.Text.StringBuilder ()
                let! attached =
                    Yession.Host.AttachWs.attach (ticket provider.port "/echo" device) 80 24 (fun text ->
                        received.Append text |> ignore)
                let handle = attached |> expect
                // The opening size is sent as a TEXT frame; the provider answers on the data
                // channel, so seeing it proves the two channels are distinct rather than one
                // stream we happen to parse twice.
                let! sized = until (fun () -> received.ToString().Contains "sized 80x24")
                Expect.isTrue sized "the resize control frame arrived as control"
                handle.Write "hello"
                let! echoed = until (fun () -> received.ToString().Contains "echo:hello")
                Expect.isTrue echoed "a binary frame went out and came back"
                handle.Resize 132 43
                let! resized = until (fun () -> received.ToString().Contains "sized 132x43")
                Expect.isTrue resized "and a later resize is control too"
                handle.Kill ()
                let! ending = handle.Exited
                Expect.equal ending (SandboxExited 7) "the in-band exited frame carries the code"
                do! provider.stop () |> Async.AwaitPromise
            }

        // Control types will be added, and a client that treated an unknown one as fatal
        // would break every provider written against the newer spec. Ignoring it is the rule
        // `docs/streams.md` states, and this is the rule rather than the wording.
        testCaseAsync "a text frame we have no meaning for does not end the stream" <|
            async {
                let! provider = startProvider () |> Async.AwaitPromise
                let received = System.Text.StringBuilder ()
                let! attached =
                    Yession.Host.AttachWs.attach (ticket provider.port "/talkative" device) 80 24 (fun text ->
                        received.Append text |> ignore)
                let handle = attached |> expect
                handle.Write "still here"
                let! echoed = until (fun () -> received.ToString().Contains "echo:still here")
                Expect.isTrue echoed "the stream carries data after text it could not read"
                // Close the socket before the server: `server.close` waits for its
                // connections, so a test that walks away from an open one hangs the whole
                // suite rather than failing.
                handle.Kill ()
                let! _ = handle.Exited
                do! provider.stop () |> Async.AwaitPromise
            }

        // Text is CONTROL, and a provider that sends device output on it gets a terminal
        // showing nothing while every layer below is working correctly. So the bytes must
        // NOT arrive as output — the fault is the provider's, and a client that quietly
        // accepted them would make the wire mean two things.
        testCaseAsync "device output sent as text is not mistaken for output" <|
            async {
                let! provider = startProvider () |> Async.AwaitPromise
                let received = System.Text.StringBuilder ()
                let! attached =
                    Yession.Host.AttachWs.attach (ticket provider.port "/talkative" device) 80 24 (fun text ->
                        received.Append text |> ignore)
                let handle = attached |> expect
                handle.Write "marker"
                let! echoed = until (fun () -> received.ToString().Contains "echo:marker")
                Expect.isTrue echoed "the round trip completed, so anything text-borne had arrived by now"
                Expect.isFalse
                    (received.ToString().Contains "device output on the wrong channel")
                    "a text frame is never data"
                handle.Kill ()
                let! _ = handle.Exited
                do! provider.stop () |> Async.AwaitPromise
            }

        // The reason termination is a frame and not a close code: an abnormal closure (1006)
        // carries nothing at all, and that path exists whatever the provider intends.
        testCaseAsync "a stream that just stops is a failure, not an exit" <|
            async {
                let! provider = startProvider () |> Async.AwaitPromise
                let! attached =
                    Yession.Host.AttachWs.attach (ticket provider.port "/abrupt" device) 80 24 ignore
                match attached with
                | Error _ ->
                    // Racing the drop: the connection may never have opened, which is the
                    // same fact reported one step earlier.
                    do! provider.stop () |> Async.AwaitPromise
                | Ok handle ->
                    let! ending = handle.Exited
                    match ending with
                    | SandboxRunFailed reason -> Expect.isFalse (reason = "") "it says why, however little it knows"
                    | SandboxExited code -> failwithf "a dropped connection is not an exit (got %d)" code
                    do! provider.stop () |> Async.AwaitPromise
            }

        // The url IS the credential: `docs/streams.md` tells an exclusive provider to mint a
        // single-use token and spend it on attach. A diagnostic that reproduces the url puts
        // that token in a log, where whatever reads logs can spend it — so the warning names
        // the connection by what an operator already knows instead.
        testCaseAsync "the text-frame warning does not put the attach token in the log" <|
            async {
                let! provider = startProvider () |> Async.AwaitPromise
                let warnings = captureWarnings ()

                try
                    let! attached =
                        Yession.Host.AttachWs.attach
                            (ticket provider.port "/talkative?token=s3cr3t-single-use" device)
                            80
                            24
                            ignore
                    let handle = attached |> expect
                    let! warned = until (fun () -> warnings.said.Length > 0)
                    Expect.isTrue warned "the provider's text frame drew the diagnostic"
                    Expect.isFalse
                        (warnings.said |> Array.exists (fun said -> said.Contains "s3cr3t-single-use"))
                        "the attach token is not recoverable from the log"
                    handle.Kill ()
                    let! _ = handle.Exited
                    do! provider.stop () |> Async.AwaitPromise
                finally
                    warnings.restore ()
            }

        // MAY 9 from the client's end: a text frame that PARSES as a control frame IS one, of a
        // version this build does not know, and is ignored silently. Warning about it tells a
        // conforming provider its control frame should have been device output — a diagnosis
        // that sends them looking at the one thing they got right.
        testCaseAsync "a control type from a later version is ignored silently" <|
            async {
                let! provider = startProvider () |> Async.AwaitPromise
                let received = System.Text.StringBuilder ()
                let warnings = captureWarnings ()

                try
                    let! attached =
                        Yession.Host.AttachWs.attach (ticket provider.port "/later-version" device) 80 24 (fun text ->
                            received.Append text |> ignore)
                    let handle = attached |> expect
                    handle.Write "marker"
                    let! echoed = until (fun () -> received.ToString().Contains "echo:marker")
                    Expect.isTrue echoed "the round trip completed, so the text frame had arrived by now"
                    Expect.equal warnings.said.Length 0 "a control frame of a type we do not know says nothing"
                    handle.Kill ()
                    let! _ = handle.Exited
                    do! provider.stop () |> Async.AwaitPromise
                finally
                    warnings.restore ()
            }

        testCaseAsync "a provider that is not there is an error, not a hang" <|
            async {
                // Port 1 on loopback: reserved, never listening, and refused immediately.
                let! attached =
                    Yession.Host.AttachWs.attach
                        { Url = "ws://127.0.0.1:1/echo"; Capabilities = device; Label = Some "nothing" }
                        80 24 ignore
                Expect.isError attached "a refused connection is reported, not awaited"
            }
    ]

// --- Reading the wire -------------------------------------------------------------------
//
// No socket, so no `Ports`: these are the client's own decisions about what arrived, and the
// cheapest tier that can host them is the one every PR runs.

module Ws = Yession.Host.AttachWs

/// UTF-8 bytes as a BINARY frame carries them: a standalone `ArrayBuffer`, which is what
/// `binaryType <- ArrayBuffer` buys and what the socket hands over.
[<Emit("new TextEncoder().encode($0).buffer")>]
let private utf8Bytes (text: string) : JS.ArrayBuffer = jsNative

/// One raw byte as a BINARY frame carries it. Two of these are how a multi-byte character
/// arrives when a chunk boundary cuts it in half, which is what real device output does.
[<Emit("new Uint8Array([$0]).buffer")>]
let private oneByte (value: int) : JS.ArrayBuffer = jsNative

let tests =
    testList "Foreign terminal attach, reading the wire (Plan 16)" [

        testCase "a binary frame is what the device said" <| fun () ->
            Expect.equal
                (Ws.heard (createDecoder ()) (Frame.Binary (utf8Bytes "hello")))
                (Ws.Heard.Output "hello")
                "binary frames are the bytes"

        // The other channel, and the distinction the whole wire is built on: a provider that
        // reaches for its framework's `send_text` to emit device output gets a terminal
        // showing nothing, and a client that quietly accepted it would make text mean two
        // things.
        testCase "a text frame is control, never device output" <| fun () ->
            match Ws.heard (createDecoder ()) (Frame.Text "device output on the wrong channel") with
            | Ws.Heard.Said _ -> ()
            | Ws.Heard.Output text -> failwithf "a text frame is never data (got %s)" text

        testCase "an exited frame carries the code the provider sent" <| fun () ->
            Expect.equal
                (Ws.control """{"type":"exited","code":7}""")
                (Ws.Control.Exited 7)
                "the in-band termination frame is where an exit code comes from"

        testCase "a failed frame carries the provider's own words" <| fun () ->
            Expect.equal
                (Ws.control """{"type":"failed","reason":"the port went away"}""")
                (Ws.Control.Failed "the port went away")
                "the reason reaches the person verbatim"

        testCase "a failed frame that named no reason still says something" <| fun () ->
            Expect.equal
                (Ws.control """{"type":"failed"}""")
                (Ws.Control.Failed "the source failed")
                "a failure with nothing to read is still a failure"

        // `docs/streams.md` MAY 9, from this end: control types will be added, and a client
        // that treated an unknown one as fatal would break on every provider written against
        // the newer spec.
        testCase "a control type from a later version is ignored rather than fatal" <| fun () ->
            Expect.equal
                (Ws.control """{"type":"from-a-later-version"}""")
                Ws.Control.Unknown
                "an unknown control type is nothing, not an error"

        // The other half of that: what a provider sent is a control frame when it SAYS which
        // control it is. Text that does not is not a frame from a later spec, it is device
        // output on the wrong channel — and only that is worth a word to anybody.
        testCase "a text frame that is not JSON at all is not a control frame" <| fun () ->
            Expect.equal
                (Ws.control "device output on the wrong channel")
                Ws.Control.NotControl
                "text that will not parse is nothing, not an error"

        testCase "JSON carrying no type at all is not a control frame" <| fun () ->
            Expect.equal
                (Ws.control """{"cols":80,"rows":24}""")
                Ws.Control.NotControl
                "a control frame is one that says which control it is"

        // MAY 8: a provider that sends both is saying the exit is not the story.
        testCase "a named failure outranks an exit code" <| fun () ->
            Expect.equal
                (Ws.ending (Some "the port went away") (Some 0))
                (SandboxRunFailed "the port went away")
                "the failure is what the person needs to read"

        testCase "an exit code is the ending when nothing failed" <| fun () ->
            Expect.equal (Ws.ending None (Some 7)) (SandboxExited 7) "the code the exited frame carried"

        // The reason termination is a frame and not a close code: an abnormal closure (1006)
        // carries nothing at all, and that path exists whatever the provider intends.
        testCase "a close that said nothing is a failure rather than an exit" <| fun () ->
            Expect.equal
                (Ws.ending None None)
                (SandboxRunFailed "the stream closed without saying why")
                "an ending nobody explained is not an exit 0"

        // The url IS a credential — `docs/streams.md` has an exclusive provider mint a
        // single-use token and spend it on attach — so nothing a person or a log reads may
        // reproduce it.
        testCase "a connection is named without its attach token" <| fun () ->
            let named =
                Ws.describing
                    { Url = "ws://127.0.0.1:7334/attach/8f2c-single-use"
                      Capabilities = device
                      Label = Some "serial console" }

            Expect.isFalse (named.Contains "8f2c-single-use") "the token is not recoverable from the name"

        testCase "a connection is named without credentials from its url" <| fun () ->
            let named =
                Ws.describing
                    { Url = "ws://spender:s3cret@127.0.0.1:7334/attach/8f2c"
                      Capabilities = device
                      Label = Some "serial console" }

            Expect.isFalse (named.Contains "s3cret") "userinfo is a credential of its own"

        // Naming it is the point: a diagnostic a reader cannot act on is no better than none,
        // and the authority is what an operator already knows about their own provider.
        testCase "a connection is named by the authority it is served from" <| fun () ->
            let named =
                Ws.describing
                    { Url = "ws://127.0.0.1:7334/attach/8f2c-single-use"
                      Capabilities = device
                      Label = Some "serial console" }

            Expect.isTrue (named.Contains "127.0.0.1:7334") "which provider, and which port of it"

        // An absent code is not a zero. `x | 0` answered 0 for one, so a provider that said
        // only "it ended" was reported as having ended WELL — the difference a person acts on.
        testCase "an exited frame that named no code is not an exit 0" <| fun () ->
            Expect.equal
                (Ws.control """{"type":"exited"}""")
                (Ws.Control.Exited(-1))
                "no code reported is the domain's -1, never a success"

        testCase "an exited frame whose code is not a number is not an exit 0" <| fun () ->
            Expect.equal
                (Ws.control """{"type":"exited","code":"seven"}""")
                (Ws.Control.Exited(-1))
                "a code this client cannot read is not a success either"

        // `String([])` is "", so a provider can send a `failed` frame whose reason coerces to
        // nothing at all — and a failure that says nothing is unreadable.
        testCase "a failed frame whose reason coerces to empty text still says something" <| fun () ->
            Expect.equal
                (Ws.control """{"type":"failed","reason":[]}""")
                (Ws.Control.Failed "the source failed")
                "a failure with no readable reason still reaches a person as words"

        // The frame's TYPE decides whether it failed; the reason is only the words. An empty
        // one reaching here means the frame decoder let one through, not that nothing failed.
        testCase "a failure whose reason came out empty is still a failure" <| fun () ->
            Expect.equal
                (Ws.ending (Some "") (Some 7))
                (SandboxRunFailed "the source failed")
                "a failed frame is a failure whatever its reason renders as"

        // One decoder for the connection, because UTF-8 does not respect frame boundaries: a
        // character decoded per frame becomes two U+FFFDs, which is what real device output at
        // a buffer boundary looks like.
        testCase "a character split across two binary frames arrives whole" <| fun () ->
            let decoder = createDecoder ()

            let decoded frame =
                match Ws.heard decoder frame with
                | Ws.Heard.Output text -> text
                | Ws.Heard.Said _ -> failwith "a binary frame is device output"
            // The two bytes of "\u00e9".
            let text = decoded (Frame.Binary (oneByte 0xC3)) + decoded (Frame.Binary (oneByte 0xA9))
            Expect.equal text "\u00e9" "the tail of a split character is held until the next frame"
    ]
