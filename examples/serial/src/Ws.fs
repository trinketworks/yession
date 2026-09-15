module SerialProvider.Ws

// The server half of the byte-stream leg (Plan 16, part D/E), hand-written.
//
// `AttachWs.fs` is the client — what a session opens against a provider. This is what a
// provider answers with, and the two are deliberately independent implementations of RFC
// 6455 rather than two halves of one library: the whole reason the data plane is a WebSocket
// is that a THIRD PARTY can be on either end, and a pair built on one library would only ever
// prove the two agreed with each other.
//
// Only what the attach protocol needs: an upgrade, unfragmented text and binary frames, the
// client's mask, close and ping. No extensions, no continuation frames, no compression — a
// device stream is short frames in both directions, and every line here is a line somebody
// has to be able to check against the RFC.

open Fable.Core
open SerialProvider.Interop

[<ImportAll("node:crypto")>]
let private nodeCrypto : obj = jsNative

/// One connected peer, from the provider's side.
type WsPeer =
    { /// A DATA frame (binary): bytes from the device.
      Send : string -> unit
      /// A CONTROL frame (text): JSON the client reads as protocol, never as device output.
      /// Keeping the two on different opcodes is what stops a device that happens to print
      /// JSON from being mistaken for the provider talking.
      Control : string -> unit
      Close : unit -> unit }

/// Accept upgrades on an existing HTTP server.
///
/// `route` is given the request path and decides whether to take the connection: `None`
/// refuses the upgrade (the socket is closed with a 404 handshake), `Some` receives the peer
/// and returns the two callbacks the provider wants — what to do with an inbound data frame,
/// and what to do when the socket goes away.
[<ImportDefault("./js/ws-accept.mjs")>]
let private accept (server: HttpServer) (route: string -> obj) (crypto: obj) : unit = jsNative

/// What a route does with a connection it took.
type WsHandlers =
    { /// A binary frame: bytes the client wants written to the device.
      Data : string -> unit
      /// A text frame: control JSON (`resize`, `kill`).
      Control : string -> unit
      /// The socket went away, for any reason. Fires exactly once.
      Closed : unit -> unit }

/// Serve WebSocket upgrades on `server`, dispatching by path.
let serve (server: HttpServer) (route: string -> (WsPeer -> WsHandlers) option) : unit =
    let dispatch (path: string) : obj =
        match route path with
        | None -> null
        | Some bind ->
            box (fun (peer: WsPeer) ->
                let handlers = bind peer
                box
                    {| data = handlers.Data
                       control = handlers.Control
                       closed = handlers.Closed |})
    accept server dispatch nodeCrypto
