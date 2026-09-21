module Yession.Tests.E2E

// End-to-end verification of the real WebRTC transport: starts a Session Process and a
// client peer in this Node process, connects them over an actual libdatachannel data
// channel, and checks the token-gated handshake plus presence events.
//
// Everything is event-driven — awaits ICE gathering completion, the data-channel `open`
// event, frame receipt, and an eagerly-registered "session ended" signal. No sleeps or
// polling, so results do not depend on timing.

open System
open Fable.Pyxpecto
open Yession.Domain
open Yession.Domain.Link
open Yession.SessionProcess
open Yession.Host

let private expect =
    function
    | Ok v -> v
    | Error e -> failwith e

let private sessionId = SessionId.create "e2e-session" |> expect
let private peerId = PeerId.create "ada" |> expect
let private joined = PeerJoined { PeerId = peerId; DisplayName = "Ada"; User = None }
let private left = PeerLeft { PeerId = peerId }

let private eventsOf (host: Host.SessionHost) : Async<SessionEvent list> =
    async {
        let! page = host.Log.Read None Int32.MaxValue
        return page.Events |> List.map (fun e -> e.Event)
    }

// A single shared host for the whole E2E suite; the tests run sequentially.
let mutable private host : Host.SessionHost option = None

/// Where the host really came up. `Host.start` is given `0`, so the OS chooses and
/// `SessionHost.Port` is the bound port rather than the requested one — which is what lets
/// two runs of this suite exist at once. Read through the mutable slot because there is no
/// address until the first case has started the host.
let private signalUrl () =
    match host with
    | Some h -> sprintf "http://127.0.0.1:%d/signal" h.Port
    | None -> failwith "host not started"

let tests =
    testList "WebRTC E2E" [
        testCaseAsync "start the Session Process host" <|
            async {
                let! h = Host.start sessionId 0
                host <- Some h
            }

        testCaseAsync "a valid hello over a real data channel is accepted and appends PeerJoined" <|
            async {
                let h = host.Value
                let! channel = WebRtc.connect (signalUrl ())
                do! channel.Send (Control (PeerHello { PeerId = peerId; DisplayName = "Ada"; Token = h.MintPeerToken () }))
                let! accepted = channel.Receive ()
                match accepted with
                | Some (Control (PeerAccepted a)) ->
                    Expect.equal a.SessionId sessionId "session id"
                    Expect.equal a.AssignedDisplayName "Ada" "assigned name"
                    Expect.equal a.LatestOffset (Some EventOffset.zero) "joined offset is 0"
                | other -> failwithf "expected PeerAccepted, got %A" other
                let! events = eventsOf h
                Expect.equal events [ joined ] "only PeerJoined after handshake"

                // disconnect -> PeerLeft, observed via the session-end signal (no timing)
                let sessionEnded = h.WaitForNextSessionEnd ()
                do! channel.Close ()
                do! sessionEnded
                let! afterLeave = eventsOf h
                Expect.equal afterLeave [ joined; left ] "PeerJoined then PeerLeft after disconnect"
            }

        testCaseAsync "a bad token over a real data channel is rejected and appends nothing" <|
            async {
                let h = host.Value
                let rejectedEnded = h.WaitForNextSessionEnd ()
                let! badChannel = WebRtc.connect (signalUrl ())
                // An unminted string — never handed out by this host, so always rejected.
                do! badChannel.Send (Control (PeerHello { PeerId = peerId; DisplayName = "Mallory"; Token = "wrong-token" }))
                let! rejected = badChannel.Receive ()
                match rejected with
                | Some (Control (PeerRejected _)) -> ()
                | other -> failwithf "expected PeerRejected, got %A" other
                do! rejectedEnded
                let! events = eventsOf h
                Expect.equal events [ joined; left ] "a rejected peer must not append events"
                // Close the rejected peer's connection too. Not for the reason first written
                // here — that a global `Interop.cleanup ()` would later wait on it, which
                // `cleanup()` never did, since it closes everything itself — but because a
                // connection this case opened is this case's to close: one left open holds a
                // port and a thread for the rest of the process, and the suite that trips
                // over it is never the one that left it. `Live connections` is what says so.
                do! badChannel.Close ()
            }

        testCaseAsync "stop the Session Process host" <|
            async {
                match host with
                | Some h -> do! h.Stop ()
                | None -> ()
            }
    ]
