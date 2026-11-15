namespace Yession.Domain

open System

/// How an agent turn ended, as its last event says.
[<RequireQualifiedAccess>]
type TurnEnding =
    | Completed
    | Failed
    | Interrupted

/// A duration of one agent turn, each measured from the turn's `AgentTurnStarted`.
[<RequireQualifiedAccess>]
type TurnTiming =
    /// To the first thing the turn did that a person watching sees: a word of its reply, a
    /// thought, or a tool call. For a turn whose runner does not stream, that is the reply
    /// itself, when the turn completes. At most one for a turn, and none for a turn that
    /// failed or was interrupted before it did anything.
    | FirstOutput of TimeSpan
    /// To the event that ended it.
    | Ended of TurnEnding * TimeSpan

/// The timings of agent turns, read off the session's own events as they are appended.
///
/// The log already holds when each of these happened, on the session's clock, so the
/// timings are a fold over it and the code that runs a turn carries no stopwatch. It is
/// also the only reading that covers a turn a person interrupted: that ending is appended
/// by the Session, not by the turn.
///
/// The fold holds a turn only from its start to its end. A turn whose start was appended by
/// an earlier process is not in it, so the events that end such a turn here time nothing: a
/// turn is timed only by the process that ran all of it.
[<RequireQualifiedAccess>]
module TurnTimings =

    type private Running =
        { StartedAt : DateTimeOffset
          /// Whether its first output has been timed already.
          Spoke : bool }

    type State = private { Running : Map<AgentTurnId, Running> }

    let empty : State = { Running = Map.empty }

    let private firstOutput (turn: AgentTurnId) (at: DateTimeOffset) (state: State) : State * TurnTiming list =
        match Map.tryFind turn state.Running with
        | Some running when not running.Spoke ->
            { Running = Map.add turn { running with Spoke = true } state.Running },
            [ TurnTiming.FirstOutput (at - running.StartedAt) ]
        | _ -> state, []

    let private ended (turn: AgentTurnId) (ending: TurnEnding) (at: DateTimeOffset) (state: State) : State * TurnTiming list =
        match Map.tryFind turn state.Running with
        | Some running ->
            { Running = Map.remove turn state.Running }, [ TurnTiming.Ended (ending, at - running.StartedAt) ]
        | None -> state, []

    /// Take one appended event, written at `at`: the state after it, and what it timed.
    let step (state: State) (at: DateTimeOffset) (event: SessionEvent) : State * TurnTiming list =
        match event with
        | AgentTurnStarted started ->
            { Running = Map.add started.AgentTurnId { StartedAt = at; Spoke = false } state.Running }, []
        // `AgentMessageStarted` is not output: the Session appends the first one before the
        // model has said anything, so that the chat shows a turn under way.
        | AgentMessageDelta delta -> firstOutput delta.AgentTurnId at state
        | AgentThought thought -> firstOutput thought.AgentTurnId at state
        | ToolUseStarted call -> firstOutput call.AgentTurnId at state
        | AgentMessageCompleted completed ->
            // The reply is output too: a turn that streamed nothing first shows something now.
            let state, first = firstOutput completed.AgentTurnId at state
            let state, last = ended completed.AgentTurnId TurnEnding.Completed at state
            state, first @ last
        | AgentTurnFailed failed -> ended failed.AgentTurnId TurnEnding.Failed at state
        | AgentTurnInterrupted interrupted -> ended interrupted.AgentTurnId TurnEnding.Interrupted at state
        | _ -> state, []
