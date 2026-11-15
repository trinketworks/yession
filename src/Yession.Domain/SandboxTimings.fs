namespace Yession.Domain

open System

/// How long one work sandbox took to come up, or to fail to.
type SandboxStartTiming =
    { /// The backend it was coming up on, as its `WorkSandboxStarting` said.
      Backend : string
      /// Whether it came up (`WorkSandboxStarted`) or could not (`WorkSandboxStartFailed`).
      Started : bool
      Took : TimeSpan }

/// The timings of work sandbox starts, read off the session's own events as they are
/// appended, the way `TurnTimings` reads a turn's.
///
/// A start is the stretch from a `WorkSandboxStarting` to the event that resolves it, and
/// the two share a `MessageId`. A sandbox recorded as started with no `Starting` before it
/// in this process is not timed: nothing here saw when its work began.
[<RequireQualifiedAccess>]
module SandboxTimings =

    type private Starting = { BeganAt : DateTimeOffset; Backend : string }

    type State = private { Starting : Map<MessageId, Starting> }

    let empty : State = { Starting = Map.empty }

    let private resolved (start: MessageId) (started: bool) (at: DateTimeOffset) (state: State) : State * SandboxStartTiming list =
        match Map.tryFind start state.Starting with
        | Some starting ->
            { Starting = Map.remove start state.Starting },
            [ { Backend = starting.Backend; Started = started; Took = at - starting.BeganAt } ]
        | None -> state, []

    /// Take one appended event, written at `at`: the state after it, and what it timed.
    let step (state: State) (at: DateTimeOffset) (event: SessionEvent) : State * SandboxStartTiming list =
        match event with
        | WorkSandboxStarting starting ->
            { Starting = Map.add starting.MessageId { BeganAt = at; Backend = starting.Backend } state.Starting }, []
        | WorkSandboxStarted started -> resolved started.MessageId true at state
        | WorkSandboxStartFailed failed -> resolved failed.MessageId false at state
        | _ -> state, []
