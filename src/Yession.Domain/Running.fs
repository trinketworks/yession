namespace Yession.Domain

/// What running a command produces: which stream a byte came from, and how the run ended.
///
/// Kernel vocabulary rather than any one feature's fact. The gate speaks it (the
/// `Command*` events are its lifecycle) and so does a terminal block, which is why it
/// cannot live in `Events.fs` any more: a feature's facts compile ABOVE that file, and
/// `TerminalBlockCompleted` names `CommandResult`.
type OutputStream =
    | Stdout
    | Stderr

type CommandResult =
    | CommandSucceeded of exitCode: int
    | CommandFailed of exitCode: int
    | CommandTimedOut
    | CommandExecutionFailed of reason: string
    /// The shell refused the line, so NOTHING ran: a syntax error, a quote left open at the
    /// end of the input, a `!` history expansion that found no event. Carries what the shell
    /// said, which is the only account of why. Its own case rather than a failure with the
    /// code the shell's prompt hook happened to report — that code is the parser's, and a
    /// command that never started has no exit status to be read as one.
    | CommandNotRun of said: string
    /// Whether the command ran is not known, and the record says so rather than guess. A
    /// terminal whose marks went missing came back to a fresh prompt without ever saying the
    /// command started — it may have been discarded at a continuation prompt, or run with its
    /// start mark lost. Carries how that came about.
    | CommandOutcomeUnknown of reason: string
