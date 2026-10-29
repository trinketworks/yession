module Yession.Host.WebRtc

// The WebRTC carrier for the session transport: a `FrameChannel` over a libdatachannel
// data channel, plus the offer/answer signalling primitives. The connection is
// established with a non-trickle exchange — each side waits for ICE gathering to complete
// (an event), then exchanges a single complete SDP — so there are no candidate-timing
// races and nothing depends on sleeps. See docs/technical-design.md §2.3.

open Fable.Core
open Fable.Core.JsInterop
open Yession.Domain
open Yession.Codecs
open Yession.Domain.Link
open Yession.Session
open Yession.Host.Interop

#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif
open Fable.NodeDataChannel
open Yession.App.Codecs

/// The transport never inspects the state-sync payload, so its codec is just a string.
let private frameCodec : Codec<SessionFrame<string>> = Frames.session Codec.string

/// Bridge a push-based data channel into the pull-based `FrameChannel.Receive`.
/// A single consumer (the peer-session pump) is assumed.
let frameChannel (dc: DataChannel) : FrameChannel<string> =
    let queue = System.Collections.Generic.Queue<SessionFrame<string> option>()
    let mutable pending : (SessionFrame<string> option -> unit) option = None
    let mutable closed = false

    let deliver (item: SessionFrame<string> option) =
        match pending with
        | Some cont ->
            pending <- None
            cont item
        | None -> queue.Enqueue item

    dc.onMessage (fun (msg: string) ->
        match Codec.fromString frameCodec msg with
        | Ok frame -> deliver (Some frame)
        | Error e -> JS.console.error ("frame decode failed: " + e))

    dc.onClosed (fun () ->
        if not closed then
            closed <- true
            deliver None)

    { Send =
        fun frame ->
            async {
                // A peer can vanish between frames (e.g. presence broadcasts racing a
                // disconnect); sending into a closed channel must be a no-op. The `isOpen`
                // guard narrows the window but can't close it: libdatachannel runs its own
                // threads, so the channel can transition to closed between this check and the
                // native send, making `send()` throw. With the addon sharing the process C++
                // runtime (see nix/node-datachannel.nix) that throw is an ordinary catchable
                // JS error, so we swallow it here — a lost frame on a dying channel is a no-op.
                if not closed && dc.isOpen () then
                    try dc.sendMessage (Codec.toString frameCodec frame) |> ignore
                    with _ -> ()
            }
      Receive =
        fun () ->
            Async.FromContinuations(fun (cont, _, _) ->
                if queue.Count > 0 then cont (queue.Dequeue())
                elif closed then cont None
                else pending <- Some cont)
      Close = fun () -> async { dc.close () } }

/// Close a peer connection and resolve once libdatachannel reports it CLOSED — the
/// library's own signal that its threads are finished with the object. This is the
/// deterministic teardown primitive: a caller that awaits it knows the connection is gone
/// rather than going, which is what makes "nothing this run opened is still open" a
/// question with an answer (`Interop.liveConnectionNames`). The waiter registers eagerly
/// (the state callback is a single slot, and nothing else claims it on these connections —
/// gathering uses the separate onGatheringStateChange slot).
let closePeerConnection (pc: PeerConnection) : Async<unit> =
    let mutable closed = false
    let mutable waiter : (unit -> unit) option = None
    pc.onStateChange (fun state ->
        if state = "closed" && not closed then
            closed <- true
            match waiter with
            | Some w -> waiter <- None; w ()
            | None -> ())
    if pc.state () = "closed" then
        async { Interop.forgetPeerConnection pc }
    else
        async {
            pc.close ()
            do!
                Async.FromContinuations (fun (cont, _, _) ->
                    if closed then cont () else waiter <- Some cont)
            Interop.forgetPeerConnection pc
        }

/// Await the data channel `open` event. Registers the callback eagerly (at call time) so
/// the event cannot be missed between construction and awaiting.
let private onceOpen (dc: DataChannel) : Async<unit> =
    let mutable opened = false
    let mutable waiter : (unit -> unit) option = None
    dc.onOpen (fun () ->
        opened <- true
        match waiter with
        | Some w -> waiter <- None; w ()
        | None -> ())
    async {
        return!
            Async.FromContinuations(fun (cont, _, _) ->
                if opened then cont () else waiter <- Some cont)
    }

/// Resolve with the complete local SDP (JSON) once ICE gathering finishes. Registers the
/// gathering callback eagerly (at call time), so it must be created *before* the action
/// that starts negotiation (creating the data channel, or setting the remote offer).
/// Non-trickle: the gathered `localDescription()` already embeds all candidates.
let private gatherDescription (kind: SdpKind) (pc: PeerConnection) : Async<string> =
    let mutable result : string option = None
    let mutable waiter : (string -> unit) option = None
    pc.onGatheringStateChange (fun state ->
        if state = "complete" && Option.isNone result then
            let ld = pc.localDescription ()
            let json = Codec.toString Sdp.message { Kind = kind; Sdp = ld.sdp }
            result <- Some json
            match waiter with
            | Some w -> waiter <- None; w json
            | None -> ())
    async {
        return!
            Async.FromContinuations(fun (cont, _, _) ->
                match result with
                | Some json -> cont json
                | None -> waiter <- Some cont)
    }

/// The session's side of a handshake: answering an offer, and every peer connection an answer
/// made, held until it is closed.
///
/// Both halves are bounded, because the browser's are. A browser gives a whole handshake
/// ten seconds (`channelOpenTimeoutMs`) and then tries again with a new peer connection. The
/// answer used to wait for this side's ICE gathering to say `complete`, with no limit: on a
/// machine where gathering is slow to finish, every attempt timed out before its answer was
/// sent, and the next attempt met the same wait. Each abandoned attempt also left its peer
/// connection open here for the life of the session, with a port and a thread behind it.
module Answering =

    type Bounds =
        { /// How long the answer waits for gathering to finish before it goes with the
          /// candidates it has.
          Gathering : System.TimeSpan
          /// How long an answered connection has to open its data channel before it counts
          /// as abandoned and is closed.
          Open : System.TimeSpan
          /// How long a connection is held after the SESSION closed its channel, so that
          /// what the session sent just before the close still reaches the far end.
          Linger : System.TimeSpan }

    /// Gathering: this side asks no STUN or TURN server, so its candidates are host
    /// candidates, local work that arrives in a burst of milliseconds. Two seconds is a
    /// margin of orders of magnitude over that, and is the part of a slow `complete` that
    /// a browser's ten-second handshake can afford. A candidate that misses it does not
    /// cost the connection: the browser's offer carries ITS candidates, and an address this
    /// answer left out is learned from the check that arrives from it.
    ///
    /// Open: twice the browser's whole handshake. A browser that has not opened by then
    /// has given up, closed its end, and is trying again with a new connection.
    ///
    /// Linger: a frame sent and then closed behind is still in SCTP's hands when the
    /// channel reports closed, and closing the connection then can abort it before it is on
    /// the wire. On a loaded box, five hundred such closes lost the last frame seventeen
    /// times when the connection closed at once, and none when it was held: a rejected peer
    /// saw a dropped connection instead of the reason. Five seconds leaves SCTP room to send
    /// a lost packet again more than once. A far end that reads the close closes its own end
    /// long before then.
    let shipped =
        { Gathering = System.TimeSpan.FromSeconds 2.0
          Open = System.TimeSpan.FromSeconds 20.0
          Linger = System.TimeSpan.FromSeconds 5.0 }

    /// The answer, bounded: what gathering resolves with when it finishes inside `limit`,
    /// and otherwise what `soFar` has gathered when the limit passes or gathering fails.
    /// `Error` only when there is
    /// nothing to send at all. `sleep` is a parameter so a test drives this rule on its own
    /// clock.
    let within
        (sleep: System.TimeSpan -> Async<unit>)
        (limit: System.TimeSpan)
        (gathered: Async<string>)
        (soFar: unit -> string option)
        : Async<Result<string, string>> =
        async {
            let complete () = async { let! answer = gathered in return Ok answer }
            match! Resilience.Policy.deadline sleep limit () (fun _ -> Some ()) complete () with
            | Ok answer -> return Ok answer
            | Error () ->
                match soFar () with
                | Some answer -> return Ok answer
                | None -> return Error "the session had no answer to give when gathering ran out of time"
        }

    /// The answer as it stands, candidates gathered so far included: libdatachannel adds
    /// each local candidate to the local description as it is gathered.
    let private described (pc: PeerConnection) : string option =
        let local = pc.localDescription ()
        if isNull local || System.String.IsNullOrEmpty local.sdp then None
        else Some (Codec.toString Sdp.message { Kind = SdpKind.Answer; Sdp = local.sdp })

    /// Run `work` once, however many ask. Every ask awaits that one run. A peer connection is
    /// released from three places (its channel ending, its open deadline, the session
    /// stopping), and `closePeerConnection` claims the connection's one state-change slot, so
    /// a second close would take the first one's waiter and leave it waiting for ever.
    let private once (work: Async<unit>) : Async<unit> =
        let mutable started = false
        let mutable finished = false
        let waiters = ResizeArray<unit -> unit> ()
        async {
            if not started then
                started <- true
                Async.StartImmediate (
                    async {
                        try do! work
                        finally
                            finished <- true
                            for waiter in List.ofSeq waiters do waiter ()
                            waiters.Clear ()
                    })
            if not finished then
                do! Async.FromContinuations (fun (cont, _, _) -> if finished then cont () else waiters.Add cont)
        }

    /// Every peer connection the session has answered and not yet closed.
    type Peers = private { Live : ResizeArray<PeerConnection * Async<unit>> }

    let create () : Peers = { Live = ResizeArray () }

    /// How many are held: open, or still closing. Read by the tests, which hold the session
    /// to closing what it answered.
    let count (peers: Peers) : int = peers.Live.Count

    /// Close one, and stop holding it. The same close however often it is asked for.
    let private release (peers: Peers) (pc: PeerConnection) : Async<unit> =
        match peers.Live |> Seq.tryFind (fun (p, _) -> obj.ReferenceEquals (p, pc)) with
        | Some (_, close) -> close
        | None -> async.Return ()

    /// Answer `offerSdp` on a new peer connection. The data channel it opens goes to
    /// `onChannel`. The connection is closed when the far end ends that channel, `bounds.Linger`
    /// after the session closes it, when no channel has opened within `bounds.Open`, or when
    /// the answer cannot be given. `Error` says why there is no answer.
    let answer
        (bounds: Bounds)
        (peers: Peers)
        (onChannel: FrameChannel<string> -> unit)
        (offerSdp: string)
        : Async<Result<string, string>> =
        let pc = createPeerConnection "yession-process"
        let mutable openDeadline : int option = None
        let mutable lingering : int option = None
        let close =
            once (
                async {
                    openDeadline |> Option.iter JS.clearTimeout
                    lingering |> Option.iter JS.clearTimeout
                    do! closePeerConnection pc
                    // Held until the close has finished, not merely begun, so `closeAll`
                    // still awaits a close that something else started.
                    match peers.Live |> Seq.tryFindIndex (fun (p, _) -> obj.ReferenceEquals (p, pc)) with
                    | Some i -> peers.Live.RemoveAt i
                    | None -> ()
                })
        peers.Live.Add ((pc, close))
        pc.onDataChannel (fun dc ->
            openDeadline |> Option.iter JS.clearTimeout
            openDeadline <- None
            let channel = frameChannel dc
            onChannel
                { channel with
                    Receive =
                        fun () ->
                            async {
                                let! frame = channel.Receive ()
                                // The channel ended: the connection under it has nothing left
                                // to carry. Closed in the background, so whoever is pumping
                                // learns of the end without waiting on the native close. Unless
                                // the session ended it, and the connection is lingering.
                                if Option.isNone frame && Option.isNone lingering then Async.StartImmediate close
                                return frame
                            }
                    Close =
                        fun () ->
                            async {
                                // What the session sent last may still be on its way
                                // (`Bounds.Linger`). Set before the close, which is what
                                // ends the channel under `Receive`.
                                if Option.isNone lingering then
                                    lingering <- Some (JS.setTimeout (fun () -> Async.StartImmediate close) (int bounds.Linger.TotalMilliseconds))
                                do! channel.Close ()
                            } })
        openDeadline <-
            Some (JS.setTimeout (fun () -> Async.StartImmediate close) (int bounds.Open.TotalMilliseconds))
        async {
            let gathered = gatherDescription SdpKind.Answer pc
            pc.setRemoteDescription (offerSdp, SdpKind.wire SdpKind.Offer)
            match! within Resilience.Policy.sleep bounds.Gathering gathered (fun () -> described pc) with
            | Ok answer -> return Ok answer
            | Error reason ->
                do! release peers pc
                return Error reason
        }

    /// Close every peer connection still held, and resolve once libdatachannel has reported
    /// each one closed.
    let closeAll (peers: Peers) : Async<unit> =
        async {
            for (_, close) in List.ofSeq peers.Live do
                do! close
        }

/// Client side: connect to a Session by posting an offer to its signalling URL,
/// applying the returned answer, and resolving once the data channel is open. Auto-
/// negotiation generates the offer automatically when the data channel is created.
let connect (signalUrl: string) : Async<FrameChannel<string>> =
    async {
        let pc = createPeerConnection "yession-client"
        let offerReady = gatherDescription SdpKind.Offer pc
        let dc = pc.createDataChannel "session"
        let opened = onceOpen dc
        let! offer = offerReady
        let! answerText = postText signalUrl offer |> Interop.awaitPromise
        let answer =
            match Sdp.parse answerText with
            | Some { Kind = SdpKind.Answer; Sdp = sdp } -> sdp
            | Some { Kind = SdpKind.Offer } -> failwith "the signalling url answered the offer with an offer"
            | None -> failwith "the signalling url answered the offer with no session description"
        pc.setRemoteDescription (answer, SdpKind.wire SdpKind.Answer)
        do! opened
        // The client owns this side's PeerConnection: closing the channel also closes
        // the connection and WAITS for libdatachannel to report it closed, so a caller
        // that has awaited `Close` has left nothing of this connection behind
        // (deterministic — no sleeps).
        // Supervised like every other end of this transport: a Node peer whose link dies
        // silently must learn it the same way a browser one does.
        let channel = Link.supervise Link.LinkPolicy.shipped (frameChannel dc)
        let close () =
            async {
                do! channel.Close ()
                do! closePeerConnection pc
            }
        return { channel with Close = close }
    }
