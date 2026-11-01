namespace Yession.Domain.Link

open Yession.Domain

open Yession.Domain.Content
open Yession.Domain.Terminals

/// The multiplexed session transport protocol. These are pure protocol shapes shared by
/// the Session and the App; the actual WebRTC/HTTP carrier is an
/// adapter that implements a frame channel over these types. State-sync payloads are
/// opaque to the transport (owned by the Ylmish sync boundary in Step 05), hence the
/// `'State` type parameter. See docs/technical-design.md §2.3.

/// Commands are how clients ask for durable facts the CRDT cannot express. Drafting,
/// sending (enqueueing), editing, reordering, and deleting are all pure CRDT writes —
/// only the agent-turn interrupt needs a request/response.
type SessionCommand =
    /// Cancel the running agent turn (Phase 3, Step 17). Rejected if that turn is no
    /// longer running — the interrupt-vs-completion race resolves at the Process.
    | InterruptAgentTurn of AgentTurnId
    /// Open a terminal, as a SHELL, in the sandbox named. A command rather than a CRDT write
    /// because the terminal's id is minted by the Process and its existence is a durable
    /// fact — a client cannot conjure one into the doc. The new terminal arrives back as a
    /// `TerminalOpened` event, not in the response: one path for the fact, and it is the one
    /// every peer already reads.
    ///
    /// The sandbox is a `SandboxRef` and not an option, deliberately: `None` on
    /// `TerminalOpened.Sandbox` means a terminal in NO sandbox — bytes from a stream somebody
    /// else produces — and that is not something a peer may ask for, because an attached
    /// source needs a ticket from a provider and a peer command carrying a URL would be a
    /// peer choosing what this session connects to. A caller who does not care passes
    /// `SandboxRef.defaultRef`, which is a real name, and the wire reads an absent field back
    /// as that — which is exactly what every client that could not say meant.
    ///
    /// WHICH sandbox is not checked here: `SessionTerminals.Open` already ensures the one
    /// named exists and refuses an unknown name with a sentence saying what there is, for
    /// every caller alike. A second rule in the command handler would be a second answer to
    /// one question.
    | OpenTerminal of title: string * sandbox: SandboxRef
    /// Close a terminal. Rejected if it is already closed or was never opened.
    | CloseTerminal of TerminalId
    /// Interrupt the block running in a terminal: ^C to the job in its foreground, exactly
    /// what a person at its keyboard would send, and never the terminal itself — closing is
    /// `CloseTerminal`, and it ends everything the shell was carrying. Any peer may: a
    /// collaborator who can kill the whole terminal can certainly stop one command in it,
    /// and what it needs is to be on the record (`TerminalBlockInterrupted`). Rejected when
    /// nothing is running there, or there is no shell to send it through.
    | InterruptTerminal of TerminalId
    /// Take the terminal's stdin — enter live mode (Plan 13, stage 2e). Succeeds even when
    /// another peer holds it: collaborators are trusted, so this STEALS rather than queues,
    /// and the previous holder's lease ends on the record. Rejected only when the terminal
    /// is not open, or is degraded (no shell to type into).
    | TakeTerminalLease of TerminalId
    /// Hand the terminal back to block mode. Rejected when this peer is not the holder —
    /// releasing someone else's lease is a steal wearing a polite word, and a steal is
    /// `TakeTerminalLease`, which says so on the record.
    | ReleaseTerminalLease of TerminalId
    // There is no re-arm. A terminal whose marks stopped may be read by some other program, and
    // typing our rc into it would hand that program every later block's loan; the loss
    // refuses its queue instead (`TerminalIntegrationLost.Refused`). Commands are frames, not
    // log lines, so nothing persisted names the verb that was here.
    /// Ask the provider for this terminal's stream again (Plan 19, step 4), when the stream
    /// it was attached to has ended.
    ///
    /// The peer names a TERMINAL and never a url, which is what keeps `OpenTerminal`'s rule
    /// intact: a peer does not choose what this session connects to. What is replayed is the
    /// tool call that produced the stream in the first place, and only when the provider
    /// declared that asking again is safe. A new terminal arrives as `TerminalOpened`.
    | ReattachTerminal of TerminalId
    /// A person's answer to a repo's trust card: whether its code may run with their GitHub,
    /// and by which routes.
    ///
    /// Carries what the card SHOWED beside what was ticked, because the answer is about those
    /// routes and no others: a route the card did not show stays as their record has it, and one
    /// it showed and they left unticked is answered no. So a repo that asks for more after the
    /// card was drawn has asked for something nobody answered, and asks again.
    | TrustRepo of repo: RepoRef * shown: ConnectionRoute list * ticked: ConnectionRoute list
    /// Put the first repo into this session (the launch surface's act): clone it, and start
    /// on `branch` when one other than its default is named.
    ///
    /// The one human-authored repo verb. Plan 15 retired the mid-session buttons because the
    /// agent already had the verbs and asking costs a sentence; at a session's start no turn
    /// has run and which repo it is FOR is the person's to say. Same gated `add_repo`, same
    /// attribution, a second caller — admitted only while the session has no repo, so a
    /// second one is still the agent's to add. Rejected with the reason when it is not
    /// admitted; ACCEPTED means the clone has begun, and its outcome reaches everyone as
    /// `RepoCloned` or `RepoCloneFailed`, never in this response — a clone is not something to
    /// hold a peer's command pump for.
    | AddRepo of repo: RepoRef * branch: string option
    /// Press a button on a surface the agent showed (`show_surface`).
    ///
    /// The peer names the BUTTON, never what it says: the Session reads the button's event and
    /// its context out of the doc it holds, and refuses while the button's checks fail. So the
    /// answer recorded is the one everybody can see, not one a peer composed. `scope` is the
    /// template item the button was drawn under (`/items/2`), or `""` at the surface's root,
    /// which is where a relative path in its context is read.
    ///
    /// ACCEPTED means the answer is on the record (`SurfaceActionTaken`) and the agent owes a
    /// turn for it; what the agent does with it reaches everyone the usual way.
    | PressSurfaceButton of surfaceId: string * componentId: string * scope: string
    /// Take down a surface the agent showed, without answering it. Any surface, whatever
    /// buttons the agent drew. ACCEPTED means it is gone for everybody and the dismissal is on
    /// the record (`SurfaceDismissed`), which owes the agent a turn.
    | DismissSurface of surfaceId: string

type SessionCommandResult =
    | CommandAccepted
    | CommandRejected of reason: string

type CommandFrame =
    | Request of RequestId * SessionCommand
    | Response of RequestId * SessionCommandResult

type EventLogFrame =
    | EventsAvailable of latestOffset: EventOffset
    | ReadEventsAfter of RequestId * after: EventOffset option * limit: int
    | EventsPage of RequestId * EventPage<SessionEvent>

type PeerHelloPayload =
    { PeerId : PeerId
      DisplayName : string
      Token : string }

type PeerAcceptedPayload =
    { SessionId : SessionId
      AssignedDisplayName : string
      LatestOffset : EventOffset option }

type ControlFrame =
    | PeerHello of PeerHelloPayload
    | PeerAccepted of PeerAcceptedPayload
    | PeerRejected of reason: string
    | Ping
    | Pong

/// The state-sync frame. Its payload is opaque to the transport; encoding belongs to the
/// sync-boundary layer (Step 05).
type StateFrame<'State> = StateSync of 'State

/// Terminal traffic (Plan 13), multiplexed onto the session's existing data channel
/// rather than given a leg of its own.
///
/// The alternative considered and rejected was SSE-out/POST-in per terminal. Three
/// reasons it loses here: `technical-design.md` §5 makes WebRTC the session transport and HTTP
/// bootstrap-only; N terminals across M tabs runs into the browser's six-connections-
/// per-origin cap, because a locally served session has no TLS and therefore no HTTP/2
/// multiplexing; and every byte would take a server round trip on a channel that is
/// already direct, ordered and reliable.
///
/// HISTORY still rides HTTP, and that is not a contradiction — it is the same split the
/// event log already makes. A transcript is read by CURSOR and answered at an address
/// naming the range it chose (`TranscriptChunk`), so those bytes never change and the
/// client keeps the ranges it has folded in its own Cache API store — the responses
/// themselves are `no-store`, because the address a cursor is asked at is not the address
/// the answer is kept under. A reload replays terminals out of that store; only the live
/// tail needs the channel.
type TerminalFrame =
    /// One transcript record as it is written, with the line index it was written at.
    /// The seq is what makes the live leg and the HTTP leg interchangeable: a client
    /// keeps a high-water mark and ignores anything at or below it, so a record arriving
    /// twice — once live, once in a fetched chunk — is folded once.
    | TerminalRecord of TerminalId * seq: int * TranscriptRecord
    /// How long a terminal's transcript is now, so a client can tell whether it is
    /// behind. The exact counterpart of `EventsAvailable`: a hint that triggers a read,
    /// never data.
    | TerminalTranscriptAvailable of TerminalId * nextSeq: int
    /// The terminal's screen as the Session has it, with the transcript position
    /// it represents (Plan 13, stage 2b) — what a peer joining mid-session renders instead
    /// of replaying every byte the terminal ever printed.
    ///
    /// The seq is what makes it composable with the live feed, exactly as an event offset
    /// is: render this, then fold records ABOVE that seq. The snapshot may be NEWER than
    /// its seq (the screen is read after the position is taken), which is the safe
    /// direction — a client re-folds a record already drawn, and drawing it twice is
    /// idempotent, whereas a seq ahead of the screen would skip a record for ever.
    ///
    /// A `TranscriptKeyframe` rather than a seq and a screen, because that IS the pair plus
    /// the one thing it was missing: the SIZE the screen was drawn at. A serialized screen is
    /// a paint at a geometry, and a client that repaints it at a different one rewraps every
    /// line in it — which the ranged replay has always known (`Serialization.range` takes the
    /// keyframe's `Cols`/`Rows` over the header's) and this frame did not, so a browser
    /// composing a live screen sized it 80x24 and left it there for the terminal's whole life.
    /// One type, produced by one serializer, carrying what a screen cannot be read without.
    | TerminalSnapshot of TerminalId * TranscriptKeyframe
    /// Keystrokes from the lease holder, relayed straight to the pty (Plan 13, stage 2e).
    ///
    /// Lease-checked at the Session, which is the only place it CAN be checked: a
    /// client that believes it holds the lease may be wrong (a steal it has not seen yet),
    /// and the pty is the Process's. A frame from a non-holder is dropped, not answered —
    /// there is no response frame here by design, because a keystroke that needed an
    /// acknowledgement would make typing a round trip.
    ///
    /// Never recorded as a transcript `"i"` record.
    ///
    /// The obvious narrower rule — record keystrokes except while the pty's ECHO bit is off —
    /// cannot be implemented on this seam. `termios` lives kernel-side on the pty slave,
    /// neither `node-pty` nor `docker exec` surfaces it, and it is not inferable from the byte
    /// stream a headless emulator sees. So the choice is all keystrokes or none, and none is
    /// right: live mode makes typing a password ordinary (`ssh`, `sudo`, a REPL token prompt),
    /// and a durable, replayable, HTTP-fetchable keystroke log is a class of exposure the
    /// session never had. Nothing is lost from the audit, because a shell echoes what is typed
    /// at it — what recording these would add is exactly what the terminal deliberately did not
    /// display. Attribution survives in the lease events, which bracket transcript ranges.
    | TerminalInput of TerminalId * data: string
    /// The lease holder's viewport size, relayed to the pty (Plan 13, stage 2e). Live-mode
    /// only: their foreground program is the one that has to agree with the pty, so in block
    /// mode the size comes from the synced register instead and this frame is dropped.
    | TerminalResize of TerminalId * cols: int * rows: int

/// The collaborative field a peer's cursor is in — the extension point for presence "in many
/// places": add a case plus its report/render sites. `DraftBody`/`QueueBody` name the same body
/// roots as `BodyKey`.
type FocusField =
    | Title
    | DraftBody of PeerId
    | QueueBody of QueueId
    /// A terminal composer's command line (Plan 13) — presence works there for the same
    /// reason it works in a message draft: co-editing a command you cannot see someone
    /// else's caret in is co-editing blind.
    | TerminalDraftBody of TerminalId * PeerId
    /// A queued terminal command's line, still editable while it waits for approval.
    | TerminalQueuedBody of QueueId
    /// A chapter's name, on the rule that opens it. The name is collaborative text like the
    /// session title, so it carries carets for the same reason: a name two people are writing
    /// at once is one nobody can see the other in.
    | ChapterName of MessageId

/// A peer's caret+selection within its focused field, as base64-encoded Yjs *relative positions*
/// over that field's shared type (the title `Y.Text` / a body `Y.XmlFragment`). Relative positions
/// survive concurrent edits; a collapsed selection (`Anchor = Head`) is a bare caret.
type CursorPos = { Anchor : string; Head : string }

type Focus = { Field : FocusField; Pos : CursorPos }

/// Ephemeral presence: where a peer's caret+selection is. Relayed peer-to-peer by the Session
/// (never durable, never in Yjs, never an event) so a collaborator's cursor is visible
/// while they edit. `Focus = None` when the peer's caret is nowhere collaborative, or the peer
/// has left (which clears it everywhere).
///
/// Deliberately NOT Yjs awareness. Awareness would carry presence on a second, binary relay
/// keyed by Yjs `clientID`, which means a `clientID`↔`PeerId` mapping to maintain and a sync
/// path outside the typed frames — Ylmish is the sync boundary, and presence is not doc state.
/// The typed frame costs a little more render code and buys one identity, one relay, and a
/// payload the cheap tier can round-trip.
type PresencePayload =
    { /// Who is present. An `ActorRef` rather than a `PeerId` because the Session is
      /// one of the parties editing now (Plan 25): it writes a name into the same `Y.Text` a
      /// person types into, and a collaborator you cannot see the caret of is the thing this
      /// frame exists to prevent.
      ///
      /// The alternative was a reserved peer id for the process, and it is the aliasing this
      /// repository has paid for before: a `PeerId` that is not a peer reads as one at every
      /// site, and nothing in the type says otherwise. Widening the key costs one refactor
      /// the compiler enumerates; the alias costs a wrong answer somewhere nobody is looking.
      Who : ActorRef
      DisplayName : string
      Focus : Focus option
      /// What this peer has open in the pane, or `None` when the pane is closed or covered.
      /// Carried on the SAME frame as the caret because they are one fact — where a peer is —
      /// and two frames would need two clear-on-leave rules that could disagree. Ephemeral
      /// exactly as the caret is: the peer going clears both.
      Viewing : ViewRef option }

/// Every message exchanged over the session transport is one of these multiplexed frames.
type SessionFrame<'State> =
    | State of StateFrame<'State>
    | Command of CommandFrame
    | EventLog of EventLogFrame
    | Control of ControlFrame
    /// Ephemeral cursor presence; relayed to other peers, never logged or persisted.
    | Presence of PresencePayload
    /// Live terminal traffic (Plan 13). Durable by the time it is sent — the Session
    /// appends to the transcript before it broadcasts — so a dropped frame costs
    /// latency, never the record.
    | Terminal of TerminalFrame

/// An abstract, bidirectional channel of session frames shared by both peers (Session
/// and App). The real carrier is a WebRTC data channel (with HTTP used
/// only for static bootstrap and temporary signalling); this abstraction lets the
/// handshake and frame-pump logic be written and tested without the Node/WebRTC IO. The
/// WebRTC adapter is a thin shell that implements this interface.
///
/// `Receive` returns `None` once the channel is closed by the remote end.
type FrameChannel<'State> =
    { Send : SessionFrame<'State> -> Async<unit>
      Receive : unit -> Async<SessionFrame<'State> option>
      Close : unit -> Async<unit> }

/// A frame too big for one data-channel message, cut into pieces and put back together.
///
/// A peer refuses a message over the size the other side advertised, and libdatachannel
/// refuses by throwing from native code: on macOS nothing catches that throw, so it aborts
/// the session process. A session whose document grew past 256 KiB died this way every
/// time a browser joined and it sent the document whole. So the carriers never hand the
/// channel a message that long. A frame that fits goes as it always did; a longer one goes
/// as pieces, each marked as more to come or as the last, and the channel is ordered and
/// reliable (the default `createDataChannel` asks for), so the receiver joins them in the
/// order they arrive.
module FramePieces =

    /// The longest piece, in UTF-16 code units. A code unit is at most three UTF-8 bytes,
    /// so a piece and its mark stay under 64 KiB, the size a peer may assume when the other
    /// side advertises none (RFC 8841 §6). Chrome, Safari and libdatachannel advertise
    /// 256 KiB; the floor is what every one of them accepts.
    let length = 16384

    // An encoded frame is JSON, so it never begins with either mark.
    let private more = "+"
    let private last = "="

    /// The messages that carry `text`: `text` itself when it fits, otherwise its pieces.
    /// A cut never falls inside a surrogate pair, which UTF-8 could not encode in halves.
    let split (text: string) : string list =
        if text.Length <= length then
            [ text ]
        else
            let rec cut (from: int) (pieces: string list) =
                let remaining = text.Length - from
                if remaining <= length then
                    List.rev ((last + text.Substring from) :: pieces)
                else
                    let size = if System.Char.IsHighSurrogate text.[from + length - 1] then length - 1 else length
                    cut (from + size) ((more + text.Substring (from, size)) :: pieces)
            cut 0 []

    /// The receiving end: hand it each message as it arrives, and it answers a whole frame
    /// once one is complete (a message that fit answers at once) and `None` while pieces
    /// are still coming.
    let assembler () : string -> string option =
        let held = System.Text.StringBuilder ()
        fun (message: string) ->
            if message.StartsWith more then
                held.Append (message.Substring 1) |> ignore
                None
            elif message.StartsWith last then
                let whole = held.Append(message.Substring 1).ToString ()
                held.Clear () |> ignore
                Some whole
            else
                Some message
