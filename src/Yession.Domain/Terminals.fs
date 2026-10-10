namespace Yession.Domain.Terminals

open Yession.Domain
open Yession.Domain.Terminals

/// Terminals, projected from the event log (Plan 12). Like the conversation, this
/// is a pure fold over ordered events and nothing else — never the doc, never live
/// output. That is what makes the terminal list, its blocks, and their exit codes
/// identical on every replica and after every reload, and it is why there is no way for a
/// client to invent a block: the only constructor is an event the Session wrote.
///
/// The bytes a block printed are NOT here. They live in the terminal's transcript
/// (`Transcript.fs`); a block records the transcript range it produced, and a renderer
/// joins the two. The split is the whole design: facts fold, bytes stream.

// The gate a command passes on its way to running is the classifier (Classify.fs) — one
// seam, shared with every other gated act, because nothing about deciding whether an act
// may happen is terminal-shaped.

/// What a client has just learned about how far a terminal's transcript has got.
///
/// The two cases carry different UNITS, and that is the entire reason this type exists.
/// `RecordAt` is an INDEX — the seq a live record arrived at. `AvailableLength` is a COUNT —
/// how many lines the feed says the transcript holds. A client asking "is there something I
/// have not read?" therefore needs `>=` for one and `>` for the other, against the same read
/// position.
///
/// That comparison used to be made at each call site, and one of them made it with the wrong
/// operator: `readPositionOf` is the NEXT unread line, so a live record arriving at exactly
/// that seq IS unread, and `>` skipped it. For a terminal opened mid-session that path is the
/// only trigger to fetch history — the availability hint is sent once, at accept — so the
/// LAST record, the shell's output with nothing following to push the stream ahead, was shown
/// live and never fetched to the store. A reload replayed the command with no output. It only
/// ever failed on a loaded runner, because a slow local drain left the read position behind
/// and hid it.
///
/// So the units live in the type and the rule lives with the cursor. A caller says what it
/// SAW; it does not do arithmetic about somebody else's read position.
type TranscriptSignal =
    /// A live record arrived at this seq. An index into the transcript.
    | RecordAt of seq: int
    /// The feed says the transcript holds this many lines. A count.
    | AvailableLength of length: int

module TranscriptCursor =

    /// Does this signal mean there are records at or after `readPosition` that have not been
    /// read? `readPosition` is the NEXT unread line (`NextSeq = first + lines.Length`), never
    /// the last read one.
    let unread (readPosition: int) (signal: TranscriptSignal) =
        match signal with
        | RecordAt seq -> seq >= readPosition
        | AvailableLength length -> length > readPosition

    /// Does an answer that reads up to (not including) `nextSeq` take a reader at
    /// `readPosition` anywhere? The other side of `unread`: that asks whether to read, this
    /// asks whether what came back is news.
    ///
    /// An answer that does not advance holds nothing the reader lacks — the read position is
    /// the end of a CONTIGUOUS prefix, so every line below it is already held — and folding it
    /// anyway is a render of the whole page for nothing. Cheap once and ruinous in bulk: a
    /// burst of output once raised one read per live record, every one asked at the same
    /// stale position, and each answer re-rendered a page that already showed it (`seq
    /// 100000` froze a page for minutes). The read loop asks one at a time now
    /// (`ClientModel.reads`); this is what keeps a duplicate that arrives anyway — from a
    /// connection that ended with a read still out, or the `204` that says "you are
    /// current" — from costing anything.
    let advances (readPosition: int) (nextSeq: int) = nextSeq > readPosition

/// A terminal's screen size in character cells (Plan 13, stage 2b).
///
/// One size per terminal, not one per viewer. A pty has a single size and every peer is
/// looking at the same screen, so a viewer with a smaller viewport scrolls rather than
/// shrinking everyone else's — resizing the terminal down to its smallest viewer is tmux's
/// worst inheritance and the reason a shared session there is unusable on a phone.
type Size = { Cols : int; Rows : int }

module Size =

    /// 80x24: what every terminal has defaulted to since the VT100, and what the transcript
    /// header records when one opens.
    let default' : Size = { Cols = 80; Rows = 24 }

    /// Sizes a terminal can actually be. A zero or negative dimension is not a small
    /// terminal, it is a broken one — and it reaches us from a doc any peer can write.
    let isValid (size: Size) : bool = size.Cols > 0 && size.Rows > 0

    /// How a size is written into a transcript, and read back out of one — `"120x40"`, the
    /// asciicast `"r"` record's payload.
    ///
    /// The pair lives here, with the type, because the two halves run in different processes:
    /// the Session writes the record when it resizes a pty, and a browser reads it to
    /// reshape the emulator composing that terminal's screen. A `sprintf` on one side and a
    /// regex on the other is one format in two places, and the side that drifts is the side
    /// nothing round-trips.
    let format (size: Size) : string = sprintf "%dx%d" size.Cols size.Rows

    /// `None` for anything that is not two positive integers around one `x`. A transcript is
    /// replayed by clients that did not write it, and a record that cannot be read is a record
    /// to skip — never a reason to stop reading the rest.
    let parse (text: string) : Size option =
        match text.Split 'x' with
        | [| cols; rows |] ->
            match System.Int32.TryParse cols, System.Int32.TryParse rows with
            | (true, cols), (true, rows) ->
                let size = { Cols = cols; Rows = rows }
                if isValid size then Some size else None
            | _ -> None
        | _ -> None

/// Where a block is in its life.
///
/// `BlockRejected` widens what a block IS, deliberately: a `BlockId` names a proposed
/// command and its outcome, not a process. A refusal shown in line with the commands that
/// did run reads as *"agent: `rm -rf /` — rejected by nick"*; without it the entry simply
/// vanishes from every screen, which is indistinguishable from a bug.
type BlockStatus =
    | BlockRunning
    | BlockFinished of CommandResult
    | BlockRejected of by: ActorRef * reason: string option
    /// It ended, and nothing on the record says HOW: its terminal closed while it was still
    /// running and no completion was ever appended for it (a log from before the Process
    /// appended one itself, or the two events landing the other way round). Carries the
    /// close's reason. Its own case rather than a `CommandResult`, because any result here
    /// would be a guess, and the guess used to be "failed" — a red word on a command whose
    /// shell may well have exited 0 under it.
    | BlockEnded of reason: string

/// One executed command and the transcript range it produced.
type Block =
    { BlockId : BlockId
      /// The queue entry this block came from, when it came through a composer (Plan 13,
      /// stage 3b). Projected rather than dropped because it is the handle the agent is given:
      /// a block does not exist until the command runs, so resuming a command that is still
      /// waiting has to be keyed on the request, and this is what joins the two afterwards.
      QueueId : QueueId option
      /// The parties behind the command (Plan 20): who wrote it, whose credential it ran on,
      /// who released it. Carried whole rather than split back into fields, because a
      /// projection that re-spells the value is another place the three can drift apart.
      Authority : Authority
      /// The command line, whole. What the block's BODY shows; its name is `BlockLabel`.
      Command : string
      /// What its author said it is for (`BlockDescription`), when they said anything —
      /// what `BlockLabel.ofBlock` calls it in place of the command.
      Description : string option
      /// Whether the agent asked for this one in the background (Plan 20, stage 2) — it did
      /// not hold a turn open, and its completion is something the agent is waiting to be
      /// told about. Projected so a surface can SAY so while it runs: work nobody is sitting
      /// in front of is the work most worth marking.
      Background : bool
      /// First transcript line of this block's output.
      FromSeq : int
      /// One past its last transcript line; `None` while it is still running.
      ToSeq : int option
      Status : BlockStatus
      /// Who interrupted it, when somebody did (`TerminalBlockInterrupted`). Beside the
      /// status rather than in it: the block still ends however its shell says, and an
      /// interrupt that came too late to matter is still somebody's act.
      StoppedBy : ActorRef option }

/// One terminal, as the UI knows it.
type TerminalView =
    { TerminalId : TerminalId
      Title : TerminalTitle
      OpenedBy : ActorRef
      /// Which of the session's WorkSandboxes this terminal runs in (Plan 15, stage 2).
      /// Fixed at open, because a terminal IS a shell process inside one sandbox — moving
      /// it would mean killing it, which is a close and an open, and those already exist.
      /// `None` for a terminal attached to a stream somebody else produces (Plan 16, part
      /// D): it runs nowhere this session owns.
      Sandbox : SandboxRef option
      /// Can this terminal's stream be asked for again (Plan 19, step 4)? What decides
      /// whether a closed device terminal offers a way back or merely its recording.
      Renewable : bool
      /// A closed terminal keeps its blocks: the audit outlives the process.
      IsOpen : bool
      /// What closed it, when something has: the reason, and the party (`TerminalClosed.By`)
      /// — so a band can say WHO killed a terminal from the fact rather than from a sentence
      /// that would have to have frozen a name into the log to say so.
      Closed : TerminalClosed option
      /// Who holds the terminal's stdin, when anyone does (Plan 13, stage 2e). `Some` IS
      /// live mode: there is no separate mode flag, because a mode nobody holds and a lease
      /// nobody holds would be two names for one fact, free to disagree.
      Lease : ActorRef option
      /// Whether the shell has stopped emitting marks (Plan 13, stage 2f). While true the
      /// Process cannot tell when a command starts or finishes here, so the queue is held and
      /// the surface says so — a stall with a name beats a stall.
      IntegrationLost : bool
      /// Blocks in the order they ran.
      Blocks : Block list
      /// Output this terminal produced that the transcript did not keep. Non-zero means
      /// the record has a stated gap.
      DroppedBytes : int }

/// What one reader KNOWS of a terminal's recording — the client-local half of what a row
/// affords (`Affordances.ofView`).
///
/// Three answers rather than a yes and a no, because "not heard of yet" and "not there" are
/// different facts with different things to say. A reader who arrives after a terminal closed
/// has heard nothing of its recording until a read answers, and when this was a `bool` that
/// silence read as the recording being gone: every closed terminal a late viewer opened said
/// "recording lost" over a `.cast` sitting intact in the store. Only the store can say a
/// recording is absent, and only by answering a read with nothing.
[<RequireQualifiedAccess>]
type RecordingKnown =
    /// Nothing has reached this reader either way: no length, no record, no answer.
    | NotYetKnown
    /// This reader holds some of it, or has been told how long it is.
    | Recorded
    /// The store answered a read from the recording's first line with nothing — and line 0
    /// is a recording's header, which every recording has.
    | NotRecorded

/// What a terminal's state affords a reader RIGHT NOW (Plan 20, stage 0) — the verbs its row
/// in the terminal list offers.
///
/// Here rather than in the view because it is a rule about a terminal's state, and a rule
/// lives with the state it governs. The same rule used to be spelled out by hand in three
/// templates — "offered only for a terminal that is actually open", "a live terminal's
/// recording is still being written", "shown ONLY when both are true" — each correct, each
/// re-derived, and each testable only by building the whole client and reading HTML back. As
/// one fold it is four booleans the cheap tier pins directly, and "a destructive control is
/// not offered over nothing" stops being a convention three templates happen to remember.
///
/// Stage 1 deleted those three with the strip's own verbs, so this is now the only place
/// that decides.
///
/// Absent verbs are ABSENT, never disabled: a control that mostly refuses teaches people not
/// to press it, and this list's controls have to work the time somebody needs them.
type Affordances =
    { /// End the process. Open terminals only — a "close" on a closed one either does
      /// nothing or reports an error, and both are worse than not being there.
      CanKill : bool
      /// Step back through what a LIVE terminal has recorded so far (Plan 14, stage 7). A
      /// DVR with nothing behind it is a control with nothing to do.
      CanRewind : bool
      /// Play a CLOSED terminal's recording.
      CanReplay : bool
      /// A CLOSED terminal whose recording the store is KNOWN not to hold — the stated gap,
      /// which the surface says rather than opening an empty player. Not merely the absence of
      /// `CanReplay`: a reader who has not heard back yet has lost nothing, and telling them
      /// otherwise is a false statement about an audit trail.
      RecordingLost : bool
      /// Ask the provider for the stream again (Plan 19, step 4). Closed, and its source
      /// said asking again is safe; a shell terminal is never renewable, because a second
      /// shell is a second terminal and opening one already exists.
      CanReattach : bool
      /// Whether the recording is the ONLY read this terminal has, so its panel opens
      /// playing rather than offering a way to.
      ///
      /// A closed terminal that ran commands has two reads of one history — the blocks, and
      /// the recording — and showing both at once made the second redundant wherever the
      /// first said everything: a command and its result, printed, with a player of the same
      /// two lines under it. So the blocks are the read and the recording is a destination.
      ///
      /// A terminal with no blocks has no such first read. A source that could not be
      /// instrumented (`SourceCapabilities.CanInstrument`) never mints one, and neither does
      /// a shell that only ever held a lease — in both the whole history is in the recording,
      /// and an empty block list is not a read, it is a `$`. Asked of the BLOCKS rather than
      /// of the source, because blocks are what the other read is made of: a source flag
      /// would answer "could this have had blocks" about a terminal that has none.
      ReplayIsTheRead : bool
      /// `ReplayIsTheRead`'s live twin: whether the SCREEN is the only read this terminal
      /// has, so its panel shows one rather than a list of commands.
      ///
      /// The screen used to be shown only while somebody held the LEASE. That is right for a
      /// shell — a held lease there means a program has taken the screen, and letting go
      /// brings the blocks back — and wrong for a device, which has no blocks to come back
      /// to. An attached serial port nobody had taken rendered an empty block list beside a
      /// stream that was arriving the whole time, and the way to see anything was to claim
      /// the keyboard.
      ///
      /// Watching is not typing. This is what a reader gets; the keyboard stays the lease's.
      ScreenIsTheRead : bool }

module Affordances =

    /// `known` is what this READER knows of the terminal's recording. The one input that is
    /// not a fact about the terminal, and it cannot be: a recording lives in the transcript
    /// store, so no fold over the event log can answer it — which is exactly why it is a
    /// named parameter rather than something this module reaches for.
    let ofView (known: RecordingKnown) (view: TerminalView) : Affordances =
        let recorded = known = RecordingKnown.Recorded
        { CanKill = view.IsOpen
          CanRewind = view.IsOpen && recorded
          CanReplay = not view.IsOpen && recorded
          RecordingLost = not view.IsOpen && known = RecordingKnown.NotRecorded
          // Not gated on `recorded`, and that is the point of asking the provider rather than
          // the store: a terminal whose recording the cap ate can still have a live device on
          // the other end, and refusing the way back because the RECORD is gone would answer
          // a question nobody asked.
          CanReattach = not view.IsOpen && view.Renewable
          ReplayIsTheRead = not view.IsOpen && recorded && List.isEmpty view.Blocks
          // The sandbox AND the blocks, and both earn their place. `None` is a stream
          // somebody else produces, which is what makes the screen the whole of it — a shell
          // that has not run its first command yet is still a shell, and its read is the
          // blocks it is about to have. And a source that declared `instrument` gets blocks
          // while still having no sandbox, so the sandbox alone would take the block read
          // away from exactly the source that has one.
          ScreenIsTheRead = view.IsOpen && Option.isNone view.Sandbox && List.isEmpty view.Blocks }

/// Every terminal this session has had, in the order they were opened.
type Projection = { Terminals : TerminalView list }

module Projection =

    let empty : Projection = { Terminals = [] }

    let private updateTerminal (id: TerminalId) (f: TerminalView -> TerminalView) (proj: Projection) =
        { Terminals = proj.Terminals |> List.map (fun t -> if t.TerminalId = id then f t else t) }

    let private updateBlock (id: BlockId) (f: Block -> Block) (view: TerminalView) =
        { view with Blocks = view.Blocks |> List.map (fun b -> if b.BlockId = id then f b else b) }

    /// Fold one event into the projection. Only terminal events matter; everything else
    /// passes through, so this composes with the other folds over the same page.
    let applyEvent (proj: Projection) (event: SessionEvent) : Projection =
        match event with
        | SessionEvent.TerminalOpened e ->
            // Re-opening an id that already exists is not a second terminal: ids are minted
            // by the Process, so this can only be a replayed event, and the fold must be
            // idempotent for the offset-gated page reads to stay safe.
            if proj.Terminals |> List.exists (fun t -> t.TerminalId = e.TerminalId) then proj
            else
                { Terminals =
                    proj.Terminals
                    @ [ { TerminalId = e.TerminalId
                          Title = e.Title
                          OpenedBy = e.OpenedBy
                          Sandbox = e.Sandbox
                          Renewable = e.Renewable
                          IsOpen = true
                          Closed = None
                          Lease = None
                          IntegrationLost = false
                          Blocks = []
                          DroppedBytes = 0 } ] }
        | SessionEvent.TerminalClosed e ->
            // The lease goes with the terminal. A closed terminal has no stdin to hold, and
            // a holder left standing on one would render as "nick is typing" for ever.
            //
            // So does any block still running: no process outlives its pty, so a block a
            // closed terminal still calls running is a chip spinning over nothing. The
            // Process appends the completion itself when it closes a terminal, and this is
            // the guard for the log that predates that, and for the one where the two
            // events arrive the other way round. It ends `BlockEnded`, not a result: a close
            // does not know how the command went, and a completion folded after it still
            // says. `ToSeq` stays unknown — the fold has no sequence to close the range at,
            // and a reader that slices to the end gets everything the command printed
            // before the shell went.
            proj
            |> updateTerminal e.TerminalId (fun t ->
                { t with
                    IsOpen = false
                    Closed = Some e
                    Lease = None
                    Blocks =
                        t.Blocks
                        |> List.map (fun b ->
                            match b.Status with
                            | BlockRunning -> { b with Status = BlockEnded e.Reason }
                            | _ -> b) })
        | SessionEvent.TerminalLeaseTaken e ->
            proj |> updateTerminal e.TerminalId (fun t -> { t with Lease = Some e.By })
        | SessionEvent.TerminalLeaseReleased e ->
            // Clear only if the holder is still the one this release names. A steal is two
            // events — the old lease ending and the new one starting — and this guard is what
            // makes the fold independent of which order they are appended in: a release
            // naming someone who no longer holds it is stale, and acting on it would drop the
            // lease the take beside it just granted.
            proj
            |> updateTerminal e.TerminalId (fun t ->
                if t.Lease = Some e.Was then { t with Lease = None } else t)
        | SessionEvent.TerminalBlockStarted e ->
            proj
            |> updateTerminal e.TerminalId (fun t ->
                if t.Blocks |> List.exists (fun b -> b.BlockId = e.BlockId) then t
                else
                    { t with
                        Blocks =
                            t.Blocks
                            @ [ { BlockId = e.BlockId
                                  QueueId = e.QueueId
                                  Authority = e.Authority
                                  Command = e.Command
                                  Description = e.Description
                                  Background = e.Background
                                  FromSeq = e.FromSeq
                                  ToSeq = None
                                  Status = BlockRunning
                                  StoppedBy = None } ] })
        | SessionEvent.TerminalBlockCompleted e ->
            proj
            |> updateTerminal e.TerminalId (fun t ->
                t |> updateBlock e.BlockId (fun b -> { b with ToSeq = Some e.ToSeq; Status = BlockFinished e.Result }))
        | SessionEvent.TerminalBlockInterrupted e ->
            proj |> updateTerminal e.TerminalId (updateBlock e.BlockId (fun b -> { b with StoppedBy = Some e.By }))
        | SessionEvent.TerminalCommandRejected e ->
            proj
            |> updateTerminal e.TerminalId (fun t ->
                if t.Blocks |> List.exists (fun b -> b.BlockId = e.BlockId) then t
                else
                    { t with
                        Blocks =
                            t.Blocks
                            @ [ { BlockId = e.BlockId
                                  QueueId = Some e.QueueId
                                  // A command that never ran was never anybody's wait.
                                  Background = false
                                  // Who refused it is on the status rather than smuggled
                                  // in here.
                                  Authority = e.Authority
                                  Command = e.Command
                                  Description = e.Description
                                  // An EMPTY range, not a missing one: a command that never
                                  // ran produced no output, so every reader that slices
                                  // [From, To) gets nothing without a special case.
                                  FromSeq = 0
                                  ToSeq = Some 0
                                  Status = BlockRejected (e.RejectedBy, e.Reason)
                                  StoppedBy = None } ] })
        | SessionEvent.TerminalIntegrationLost e ->
            proj |> updateTerminal e.TerminalId (fun t -> { t with IntegrationLost = true })
        | SessionEvent.TerminalIntegrationRestored e ->
            proj |> updateTerminal e.TerminalId (fun t -> { t with IntegrationLost = false })
        | SessionEvent.TerminalTranscriptTruncated e ->
            proj |> updateTerminal e.TerminalId (fun t -> { t with DroppedBytes = t.DroppedBytes + e.DroppedBytes })
        | _ -> proj

    let tryFind (id: TerminalId) (proj: Projection) : TerminalView option =
        proj.Terminals |> List.tryFind (fun t -> t.TerminalId = id)

    /// The terminals still open, in open order — what the panel lists.
    let openTerminals (proj: Projection) : TerminalView list =
        proj.Terminals |> List.filter (fun t -> t.IsOpen)

    /// The block currently running in a terminal, if any. At most one: the drain runs a
    /// terminal's queue one command at a time, which is what makes a shell's working
    /// directory and environment mean anything from one command to the next.
    let runningBlock (view: TerminalView) : Block option =
        view.Blocks |> List.tryFind (fun b -> b.Status = BlockRunning)

/// What a block is CALLED on a screen: the part of its command that does the work.
///
/// An agent's command very often opens with `cd <dir> &&` and closes with
/// `2>&1 | tail -20`, so a title that is the raw command line truncates on a phone to
/// `$ cd /repos/trinketwo…` — the part that says least. The rule, applied to the first line
/// only (a heredoc's body is not its name):
///
/// - It is split at the top-level `&&`, `||` and `;` — never inside quotes, a `$(…)` or
///   a subshell's parentheses, never at a pipe.
/// - A segment that only sets up is dropped: `cd`, `pushd`, `popd`, `export`, `source`,
///   `.`, `set -…`/`set +…`, and bare `NAME=value` assignments. If every segment is set-up,
///   the last one is kept.
/// - Noise redirects are dropped (`2>&1`, and anything sent to `/dev/null`), and so are
///   trailing `| head …` and `| tail …` stages, which trim what it printed rather than do
///   anything.
/// - What is left is joined back with the operators that stood between it, so it is still
///   the command — its meaningful part, never a word it did not say.
///
/// A non-empty command never gets an empty label: if nothing is left, it is the command,
/// trimmed. The BODY that shows a command keeps all of it; this is for what NAMES one.
///
/// That rule is the fallback. A block whose author said what it is FOR
/// (`Block.Description`, which the agent writes with every command it runs) is called that
/// instead — "Run the unit tests" says more than any part of the command line can, and it
/// is the one name a person reading a phone needs. A person's own commands carry none and
/// keep the rule.
module BlockLabel =

    type private Token =
        | Word of string
        | Op of string

    /// The shell's words and the operators between them, each word as it was written
    /// (quotes and all), so a label quotes exactly what the command did.
    let private lex (line: string) : Token list =
        let tokens = ResizeArray<Token> ()
        let word = System.Text.StringBuilder ()
        let flush () =
            if word.Length > 0 then
                tokens.Add (Word (word.ToString ()))
                word.Clear () |> ignore
        let n = line.Length
        let mutable i = 0
        // Inside a quote nothing is an operator; inside parentheses (a `$(…)` or a
        // subshell) neither is — they are one word with the rest of what they belong to.
        let mutable quote : char option = None
        let mutable depth = 0
        while i < n do
            let c = line.[i]
            let next = if i + 1 < n then Some line.[i + 1] else None
            match quote with
            | Some q ->
                word.Append c |> ignore
                if c = '\\' && q <> '\'' && next.IsSome then
                    word.Append next.Value |> ignore
                    i <- i + 1
                elif c = q then quote <- None
                i <- i + 1
            | None ->
                match c, next with
                | '\\', Some escaped ->
                    word.Append(c).Append escaped |> ignore
                    i <- i + 2
                | ('\'' | '"' | '`'), _ ->
                    quote <- Some c
                    word.Append c |> ignore
                    i <- i + 1
                | '(', _ ->
                    depth <- depth + 1
                    word.Append c |> ignore
                    i <- i + 1
                | ')', _ ->
                    depth <- max 0 (depth - 1)
                    word.Append c |> ignore
                    i <- i + 1
                | _ when depth > 0 ->
                    word.Append c |> ignore
                    i <- i + 1
                | ('&', Some '&') | ('|', Some '|') | ('|', Some '&') ->
                    flush ()
                    tokens.Add (Op (string c + string next.Value))
                    i <- i + 2
                | (';' | '|'), _ ->
                    flush ()
                    tokens.Add (Op (string c))
                    i <- i + 1
                | _ when System.Char.IsWhiteSpace c ->
                    flush ()
                    i <- i + 1
                | _ ->
                    word.Append c |> ignore
                    i <- i + 1
        flush ()
        List.ofSeq tokens

    let private isPipe (op: string) = op = "|" || op = "|&"

    /// Cut a token list at the operators `at` admits: the pieces, each with the operator
    /// that came before it (`None` for the first).
    let private splitAt (at: string -> bool) (tokens: Token list) : (string option * Token list) list =
        let rec go (before: string option) (current: Token list) (acc: (string option * Token list) list) =
            function
            | [] -> List.rev ((before, List.rev current) :: acc)
            | Op op :: rest when at op -> go (Some op) [] ((before, List.rev current) :: acc) rest
            | token :: rest -> go before (token :: current) acc rest
        go None [] [] tokens

    let private words (tokens: Token list) : string list =
        tokens |> List.choose (function Word w -> Some w | Op _ -> None)

    let private isNullSink (target: string) = target = "/dev/null"

    /// A redirect that only silences or merges output — written `2>/dev/null` or
    /// `2> /dev/null` — gone, with its target when that was a word of its own.
    let rec private dropNoise (ws: string list) : string list =
        let redirect (w: string) =
            let w = w.TrimStart [| '0'; '1'; '2'; '3'; '4'; '5'; '6'; '7'; '8'; '9'; '&' |]
            w = ">" || w = ">>"
        match ws with
        | [] -> []
        | w :: rest when w = "2>&1" -> dropNoise rest
        | w :: target :: rest when redirect w && isNullSink target -> dropNoise rest
        | w :: rest when redirect (w.Replace ("/dev/null", "")) && w.EndsWith "/dev/null" -> dropNoise rest
        | w :: rest -> w :: dropNoise rest

    let private isAssignment (w: string) =
        match w.IndexOf '=' with
        | at when at > 0 ->
            let name = w.Substring (0, at)
            not (System.Char.IsDigit name.[0]) && name |> Seq.forall (fun c -> System.Char.IsLetterOrDigit c || c = '_')
        | _ -> false

    /// A segment that prepares for the work rather than doing it.
    let private isSetup (stages: string list list) =
        match stages with
        | [ ws ] ->
            match ws with
            | ("cd" | "pushd" | "popd" | "export" | "source" | ".") :: _ -> true
            | "set" :: flag :: _ when flag.StartsWith "-" || flag.StartsWith "+" -> true
            | _ :: _ when List.forall isAssignment ws -> true
            | _ -> false
        | _ -> false

    /// Its pipeline stages, cleaned: noise redirects out, then trailing output-trimming
    /// stages — but never the first, which is the command whose output they trimmed.
    let private stagesOf (tokens: Token list) : string list list =
        let stages =
            splitAt isPipe tokens
            |> List.map (snd >> words >> dropNoise)
            |> List.filter (not << List.isEmpty)
        let rec trim (reversed: string list list) =
            match reversed with
            | (("head" | "tail") :: _) :: (_ :: _ as rest) -> trim rest
            | _ -> reversed
        stages |> List.rev |> trim |> List.rev

    /// The meaningful part of a command line, by the rule above.
    let ofCommand (command: string) : string =
        let trimmed = command.Trim ()
        let line =
            trimmed.Split '\n'
            |> Array.tryHead
            |> Option.map (fun l -> l.Trim ())
            |> Option.defaultValue trimmed
        let segments =
            splitAt (fun op -> op = "&&" || op = "||" || op = ";") (lex line)
            |> List.map (fun (before, tokens) -> before, stagesOf tokens)
            |> List.filter (snd >> List.isEmpty >> not)
        let kept =
            match segments |> List.filter (snd >> isSetup >> not) with
            | [] -> segments |> List.tryLast |> Option.toList
            | work -> work
        let render (stages: string list list) =
            stages |> List.map (String.concat " ") |> String.concat " | "
        let label =
            kept
            |> List.mapi (fun at (before, stages) ->
                match at, before with
                | 0, _ | _, None -> render stages
                | _, Some ";" -> "; " + render stages
                | _, Some op -> " " + op + " " + render stages)
            |> String.concat ""
        if label = "" then trimmed else label

    /// What a block is called — the one function every surface that NAMES a block asks: its
    /// description when it has one (bounded again here, through `BlockDescription.ofProse`,
    /// because a name is shown wherever a block is and the log is read for ever), else its
    /// command's meaningful part. A command wears the prompt it was typed at, so a name that
    /// is a command reads as one; a description is prose and wears none.
    let ofBlock (block: Block) : string =
        match block.Description |> Option.bind BlockDescription.ofProse with
        | Some description -> description
        | None -> "$ " + ofCommand block.Command

/// What a terminal is CALLED on a screen, and the line that says what it is doing.
///
/// Every terminal a person opens without naming one is titled `terminal`
/// (`TerminalTitle.fallback`), so a strip of eight was eight identical words, the list was
/// that many identical rows, every kill button shared one accessible name, and a chat chip
/// could not say where its command ran. The title is what the Session RECORDS and stays so;
/// this is a projection over it, which is why it takes the whole `Projection` — an ordinal
/// is a fact about the others, not about the one.
///
/// A client derivation: the agent's tools still say a terminal's title or id, because the
/// Session does not count. Teaching it the same ordinal is a separate change.
module TerminalName =

    /// Who an untitled terminal is numbered among, and what its name starts with — or `None`
    /// when somebody gave it a title, which is then its name as written.
    ///
    /// Untitled is the two titles a terminal gets for saying nothing (`TerminalTitle.inSandbox`
    /// with no name): the fallback word, and a named sandbox's own name. A typed title that
    /// merely equals one of them said nothing either, so it is numbered too. A terminal in
    /// `default` — and one attached to a stream, which runs in no sandbox — is a `term`; one
    /// in a named sandbox wears the sandbox, spelled as its title would have been.
    let private numberedAs (view: TerminalView) : string option =
        let untitled =
            view.Title = TerminalTitle.fallback
            || (match view.Sandbox with
                | Some sandbox -> view.Title = TerminalTitle.inSandbox sandbox ""
                | None -> false)
        if not untitled then None
        else
            match view.Sandbox with
            | Some sandbox when sandbox <> SandboxRef.defaultRef -> Some (SandboxRef.render sandbox)
            | Some _
            | None -> Some "term"

    /// The name: the title when somebody gave one, else the sandbox and an ordinal — `term 1`,
    /// `dev 2`. Only the name — a title recorded with its sandbox in front of it reads
    /// without (`TerminalTitle.named`), because where it runs is `place`'s to say, beside it.
    ///
    /// The ordinal is the terminal's place among the untitled terminals numbered the same way,
    /// in the order they OPENED, closed ones included — so a name never changes once given:
    /// closing `term 1` does not make `term 2` the first, and the next one is `term 3`. Only
    /// the untitled are counted, so the first unnamed terminal beside a `build` is `term 1`
    /// rather than leaving a reader looking for the one before it.
    let display (proj: Projection) (view: TerminalView) : string =
        match numberedAs view with
        | None -> TerminalTitle.named view.Sandbox view.Title
        | Some prefix ->
            let before =
                proj.Terminals
                |> List.takeWhile (fun t -> t.TerminalId <> view.TerminalId)
                |> List.filter (fun t -> numberedAs t = Some prefix)
                |> List.length
            sprintf "%s %d" prefix (before + 1)

    /// Where a terminal runs, when that tells a person something: a named sandbox, rendered.
    /// `None` for `default`, which every session has, and for a stream, which runs nowhere.
    ///
    /// Secondary text, beside the name and never in it: the name is what a strip of tabs
    /// truncates, and a sandbox in front of it is what survives the cut.
    let place (view: TerminalView) : string option =
        match view.Sandbox with
        | Some sandbox when sandbox <> SandboxRef.defaultRef -> Some (SandboxRef.render sandbox)
        | Some _
        | None -> None

    /// What the terminal is doing, or last did: the block running, else the last one it
    /// ran, else nothing — a terminal that has run nothing has nothing to add to its name.
    /// The block rather than a string, because a surface wants both halves of it: its name
    /// (`BlockLabel.ofBlock`) to show, and its whole command for whoever asks for more.
    let latest (view: TerminalView) : Block option =
        Projection.runningBlock view |> Option.orElse (List.tryLast view.Blocks)

    /// What it is doing, then where it runs — whichever of the two there is, and nothing
    /// when neither — with the block spelled however the surface needs it.
    let private alongside (spell: Block -> string) (view: TerminalView) : string =
        [ latest view |> Option.map spell |> Option.defaultValue ""
          place view |> Option.map (sprintf "in %s") |> Option.defaultValue "" ]
        |> List.filter (fun part -> part <> "")
        |> String.concat " · "

    /// The line a terminal's list row carries under its name: the block by its NAME
    /// (`BlockLabel.ofBlock`), because that row is scanned and a whole command is a truncation.
    let summary (view: TerminalView) : string = alongside BlockLabel.ofBlock view

    /// The same line in full, for the hover that a person asks for more from: the whole command.
    let hint (view: TerminalView) : string = alongside (fun block -> block.Command) view

/// What the emulator's alt-screen state proposes doing about the lease (Plan 13, stage 2e).
///
/// "The flip is detected, not configured": a TUI taking the screen is the universal signal
/// that a program, not a prompt, owns the terminal, and the person whose command started it
/// is the person who now needs to type into it. This is the whole policy, deliberately one
/// pure function over the emulator's state so that shipping it, tuning it, or turning it off
/// is a one-line change rather than an excavation.
type Flip =
    /// Give the lease to this actor — a block became a TUI and its author needs the keyboard.
    | FlipToLive of ActorRef
    /// The TUI exited and nobody claimed the terminal by hand: back to block mode.
    | FlipToBlock
    | FlipNothing

module Flip =

    /// `altScreen` is the emulator's current buffer; `holder` the lease as it stands;
    /// `autoHeld` whether that holder got it from a previous `FlipToLive` rather than by
    /// asking; `runningAuthor` the author of the block running now, if one is.
    ///
    /// Three rules, and the second two are what "detection PROPOSES the mode" means:
    ///
    ///   * A held lease is never overridden by detection. A peer who took the terminal owns
    ///     it until they release it or someone steals it — a program exiting is not either.
    ///   * Detection only ever RELEASES what detection took. Otherwise leaving `vim` would
    ///     yank the keyboard from a peer who had taken the terminal explicitly and happened
    ///     to run an editor in it.
    ///   * The flip follows the AUTHOR, and the agent is one (Plan 20, stage 6). This rule
    ///     used to read "an agent-authored block does not flip — live mode is human-only",
    ///     which was true of a session where the agent had no hands: live mode was a browser
    ///     surface, so handing it a terminal would have handed it to nobody. Plan 19 gave it
    ///     `write_terminal`/`read_terminal`, and what the exception left behind was a wedge —
    ///     an agent command that takes the screen waits for a keystroke nobody is allowed to
    ///     send, so its block never finishes and the queue behind it never moves. Nothing
    ///     flips to `Session`, `System` or a repo's file, and that is not policy
    ///     either: nothing in the session can type as any of them.
    let propose
        (altScreen: bool)
        (holder: ActorRef option)
        (autoHeld: bool)
        (runningAuthor: ActorRef option)
        : Flip =
        match altScreen, holder with
        | true, None ->
            match runningAuthor with
            | Some (PeerRef _ as author) | Some (UserRef _ as author) | Some (Agent as author) -> FlipToLive author
            | Some Session | Some System | Some (Configured _) | None -> FlipNothing
        | true, Some _ -> FlipNothing
        | false, Some _ when autoHeld -> FlipToBlock
        | false, _ -> FlipNothing

/// Who may reach INTO an instrumented terminal — one whose commands are blocks — while a
/// block runs: type raw bytes into it, or read its screen mid-run.
///
/// The rule the admission asks, kept apart from the write and the read that apply it.
/// Blocks exist so that what runs is classified and on the record, and raw bytes into the
/// shell would be the door around that — so nobody types into a shell terminal at large;
/// and a block's output comes back as the block's answer, so nobody reads a shell terminal
/// mid-run at large either. Two parties are admitted, and both are reaching into something
/// already on the record rather than running something new:
///
///   * the LEASE HOLDER — detection handed them the terminal over a block that took the
///     alternate screen (`Flip`), their keystrokes are that block's, and the screen is the
///     only answer that block has;
///   * the AUTHOR OF THE RUNNING BLOCK — the block is theirs, classified and recorded, and a
///     command that prompts is waiting on exactly them. Without this an agent's block that
///     asked for stdin (`BlockStdinPolicy`) had stdin and no hand to feed it with, and a
///     block of its that turned out to be stuck could be ended only by closing the whole
///     terminal, `cd` and all. And the same agent, waiting on its own ten-minute `lint`,
///     was refused a `read_terminal` of it — told to run it with `execute_command`, which it
///     had — so it could not wait for a line of the output it was allowed to type at.
///
/// One rule for both verbs, because they are one question: is this block yours to be in the
/// middle of. A reader takes nothing and blocks nobody, which is why the read used to be
/// gated on detection alone; but whose block it is still decides whether the screen is
/// theirs to read mid-run, or another author's answer to wait for.
///
/// Neither admits either while NOTHING runs: between blocks the shell is at its prompt,
/// bytes typed there would be a command that skipped the queue, and there is nothing to read
/// that a block's answer did not already carry.
module BlockAccess =

    let admits (holder: ActorRef option) (runningAuthor: ActorRef option) (by: ActorRef) : bool =
        holder = Some by || runningAuthor = Some by

/// One block an agent turn is told the outcome of (Plan 13, stage 3a).
///
/// Terminal events fold into `Projection` and deliberately NOT into the
/// conversation — a command someone ran is not something someone said. That is right for
/// the chat log and wrong for the agent, whose context is built from the conversation, so
/// without this it cannot see the result of anything it queued, on that turn or any later
/// one. Which is the substantive reason it reaches for a private execution path instead.
type BlockDigest =
    { TerminalId : TerminalId
      /// The terminal's title, so the agent can name the place rather than an opaque id.
      Title : TerminalTitle
      /// Which sandbox the terminal runs in — `None` for a stream. Carried beside the title
      /// rather than read out of it: a title is only a name (`TerminalTitle.inSandbox`), and
      /// the agent has to know WHERE a command ran to know what it ran against.
      Sandbox : SandboxRef option
      BlockId : BlockId
      /// Who wrote the command — the agent's own, or someone else's it should know ran.
      Author : ActorRef
      Command : string
      Status : BlockStatus
      /// The tail of what the block printed, capped. All of it stays in the transcript;
      /// this is the part that fits in a context window.
      OutputTail : string
      /// Characters of output the tail leaves out. Stated rather than silently elided: a
      /// model that cannot tell a short output from a truncated one will confidently
      /// describe the wrong thing.
      Elided : int }

/// What an agent turn is told about the terminals since it last ran (Plan 13, stage 3a).
module Digest =

    /// Characters of output tail kept per block. The transcript keeps the rest, and a
    /// block's full range travels with it, so nothing here is the only copy.
    let tailCap = 2000

    /// The blocks whose start or completion fell after the PREVIOUS turn began.
    ///
    /// No stored cursor is needed, and that is a property of when the page is read rather
    /// than a trick: an agent turn's context is built from a page read BEFORE that turn
    /// appends its own `AgentTurnStarted`, so resetting on every `AgentTurnStarted` in the
    /// page leaves exactly what moved since the previous one.
    ///
    /// Completion counts as movement, not just the start. A block that began before the
    /// last turn and finished during it is precisely the case the agent is waiting on —
    /// reporting only newly-started blocks would drop every outcome it actually asked for.
    let window (events: SessionEvent list) : Set<string> =
        events
        |> List.fold
            (fun acc event ->
                match event with
                | SessionEvent.AgentTurnStarted _ -> Set.empty
                | SessionEvent.TerminalBlockStarted b -> Set.add (BlockId.value b.BlockId) acc
                | SessionEvent.TerminalBlockCompleted b -> Set.add (BlockId.value b.BlockId) acc
                | _ -> acc)
            Set.empty

    /// Assemble the digest: every in-window block, in the order it ran, with a bounded
    /// tail of what it printed. `readOutput` is handed the block's transcript range —
    /// `None` for a running block, which has no end yet and reads to whatever the terminal
    /// has so far.
    let build
        (readOutput: TerminalId -> int -> int option -> string)
        (window: Set<string>)
        (proj: Projection)
        : BlockDigest list =
        [ for terminal in proj.Terminals do
            for block in terminal.Blocks do
                if Set.contains (BlockId.value block.BlockId) window then
                    let output = readOutput terminal.TerminalId block.FromSeq block.ToSeq
                    let elided = max 0 (output.Length - tailCap)
                    { TerminalId = terminal.TerminalId
                      Title = terminal.Title
                      Sandbox = terminal.Sandbox
                      BlockId = block.BlockId
                      Author = Authority.author block.Authority
                      Command = block.Command
                      Status = block.Status
                      OutputTail = (if elided > 0 then output.Substring elided else output)
                      Elided = elided } ]
