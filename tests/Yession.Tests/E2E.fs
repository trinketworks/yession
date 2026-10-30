module Yession.Tests.E2E

// End-to-end verification of the real WebRTC transport: starts a Session and a
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
open Yession.Session
open Yession.Host
open Yession.Codecs
open Yession.App.Codecs
open Yession.Tests.Support

let private expect =
    function
    | Ok v -> v
    | Error e -> failwith e

let private sessionId = SessionId.create "e2e-session" |> expect
let private peerId = PeerId.create "ada" |> expect
let private joined = PeerJoined { PeerId = peerId; DisplayName = "Ada"; User = None }
let private left = PeerLeft { PeerId = peerId }

/// What the PEERS put in the log — `appended`, because every case below compares this
/// against a list of presence events and the session's own note that it started is not one of
/// them.
let private eventsOf (host: Host.SessionHost) : Async<SessionEvent list> =
    async {
        let! page = host.Log.Read None Int32.MaxValue
        return page.Events |> List.map (fun e -> e.Event) |> appended
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

/// A client's half of a handshake, by hand: a peer connection with a data channel, and its
/// complete offer SDP. What the client does with the answer is the case's to decide, which is the
/// point — `WebRtc.connect` always applies it.
let private offering (name: string) : Async<Fable.NodeDataChannel.PeerConnection * Fable.NodeDataChannel.DataChannel * string> =
    async {
        let pc = Interop.createPeerConnection name
        let mutable gathered = false
        let mutable waiter : (unit -> unit) option = None
        pc.onGatheringStateChange (fun state ->
            if state = "complete" then
                gathered <- true
                waiter |> Option.iter (fun w -> w ()))
        let dc = pc.createDataChannel "session"
        do! Async.FromContinuations (fun (cont, _, _) -> if gathered then cont () else waiter <- Some cont)
        // The SDP itself, as `/signal` hands it on once it has read the posted message.
        return pc, dc, (pc.localDescription ()).sdp
    }

/// An answered connection with its channel open at both ends: the client's peer connection,
/// the client's end read as frames, and the session's end as `onChannel` handed it over.
let private answered (bounds: WebRtc.Answering.Bounds) (peers: WebRtc.Answering.Peers) (name: string) =
    async {
        let opened, isOpen = latch ()
        let channels = ResizeArray<FrameChannel<string>> ()
        let! client, dc, offer = offering name
        dc.onOpen (fun () -> opened ())
        match! WebRtc.Answering.answer bounds peers channels.Add offer with
        | Ok answer ->
            match Sdp.parse answer with
            | Some { Kind = SdpKind.Answer; Sdp = sdp } -> client.setRemoteDescription (sdp, SdpKind.wire SdpKind.Answer)
            | other -> failwithf "the session answered with %A" other
        | Error why -> failwithf "the session did not answer: %s" why
        do! isOpen
        do! waitUntil "the session to have the channel" (fun () -> channels.Count = 1)
        return client, WebRtc.frameChannel dc, channels.[0]
    }

let private answeringTests =
    testList "The session's answered connections" [
        // A browser that gives up on a handshake closes its end and tries again with a new
        // connection. The session's end of the old one used to be held until the session
        // stopped, one per abandoned attempt.
        testCaseAsync "an answered connection that never opens a channel is closed" <|
            async {
                let peers = WebRtc.Answering.create ()
                let bounds = { WebRtc.Answering.shipped with Open = TimeSpan.FromMilliseconds 100.0 }
                let! client, _, offer = offering "e2e-abandoning-client"
                let! answer = WebRtc.Answering.answer bounds peers (fun _ -> failwith "no channel was meant to open") offer
                Expect.isOk answer "the session answered"
                // The answer is never applied: this is the browser that gave up.
                do! waitUntil "the abandoned connection to be closed" (fun () -> WebRtc.Answering.count peers = 0)
                do! WebRtc.closePeerConnection client
            }

        testCaseAsync "an answered connection is closed when its channel ends" <|
            async {
                let peers = WebRtc.Answering.create ()
                let opened, isOpen = latch ()
                let channels = ResizeArray<FrameChannel<string>> ()
                let! client, dc, offer = offering "e2e-leaving-client"
                dc.onOpen (fun () -> opened ())
                match! WebRtc.Answering.answer WebRtc.Answering.shipped peers channels.Add offer with
                | Ok answer ->
                    match Sdp.parse answer with
                    | Some { Kind = SdpKind.Answer; Sdp = sdp } -> client.setRemoteDescription (sdp, SdpKind.wire SdpKind.Answer)
                    | other -> failwithf "the session answered with %A" other
                | Error why -> failwithf "the session did not answer: %s" why
                do! isOpen
                do! waitUntil "the session to have the channel" (fun () -> channels.Count = 1)
                // The client leaves; whoever pumps the session's end learns it as the end.
                do! WebRtc.closePeerConnection client
                let! last = channels.[0].Receive ()
                Expect.isNone last "the channel ended"
                do! waitUntil "the session's end of it to be closed" (fun () -> WebRtc.Answering.count peers = 0)
            }

        // The session rejects a peer by sending why and closing. Its channel reports closed
        // while that frame is still in SCTP's hands, and closing the connection then aborted
        // it before it left: the peer saw a dropped connection instead of the reason.
        testCaseAsync "a connection whose channel the session closed is held while what it sent last can arrive" <|
            async {
                let peers = WebRtc.Answering.create ()
                let bounds = { WebRtc.Answering.shipped with Linger = TimeSpan.FromSeconds 2.0 }
                let! client, _, session = answered bounds peers "e2e-closed-by-session"
                do! session.Send (Control (PeerRejected "invalid peer token"))
                do! session.Close ()
                match! session.Receive () with
                | None -> ()
                | Some frame -> failwithf "the session's end was still open, with %A" frame
                // A close begun at once is over in milliseconds; the linger is two seconds.
                do! quietFor 300
                Expect.equal (WebRtc.Answering.count peers) 1 "the connection is still held"
                do! WebRtc.closePeerConnection client
                do! WebRtc.Answering.closeAll peers
            }

        // A data channel refuses a message over the size the peer advertised (256 KiB from
        // libdatachannel and the browsers), and on macOS libdatachannel's refusal aborted the
        // session process: a session whose document outgrew one message died every time a
        // browser joined.
        testCaseAsync "a frame longer than one data-channel message arrives whole" <|
            async {
                let peers = WebRtc.Answering.create ()
                let! client, received, session = answered WebRtc.Answering.shipped peers "e2e-long-frame"
                let document = String.replicate 300_000 "y"
                do! session.Send (State (StateSync document))
                let! frame = received.Receive ()
                Expect.equal frame (Some (State (StateSync document))) "the whole document"
                do! WebRtc.closePeerConnection client
                do! WebRtc.Answering.closeAll peers
            }

        testCaseAsync "a connection whose channel the session closed is closed once it has lingered" <|
            async {
                let peers = WebRtc.Answering.create ()
                let bounds = { WebRtc.Answering.shipped with Linger = TimeSpan.FromMilliseconds 100.0 }
                let! client, _, session = answered bounds peers "e2e-lingered"
                do! session.Close ()
                do! waitUntil "the lingering connection to be closed" (fun () -> WebRtc.Answering.count peers = 0)
                do! WebRtc.closePeerConnection client
            }
    ]

let tests =
    testList "WebRTC E2E" [
        testCaseAsync "start the Session host" <|
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
                    // Not offset 0: the session writes its own start before it can accept a
                    // peer, so the log's end — which is what a joiner needs for catch-up — is
                    // one past it.
                    Expect.equal a.LatestOffset (EventOffset.create 1L |> Result.toOption) "the joined offset is the log's end"
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

        testCaseAsync "stop the Session host" <|
            async {
                match host with
                | Some h -> do! h.Stop ()
                | None -> ()
            }

        answeringTests
    ]
