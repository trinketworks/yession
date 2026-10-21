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

/// Why the kernel's OOM killer chose a process in a sandbox. The two causes need different
/// answers: one is this sandbox's own use, the other is somebody else's.
type MemoryKill =
    /// The sandbox reached its own memory limit. The command (or something beside it in the
    /// same sandbox) used too much.
    | SandboxLimit
    /// Memory ran out above the sandbox: the whole machine, or a pool that the sandbox is in.
    /// The kernel then kills the largest process, wherever it is, so the cause is often a
    /// different sandbox.
    | MachineOutOfMemory

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
    /// The kernel's OOM killer stopped the command with SIGKILL, and the counters of the
    /// sandbox's memory cgroup say so (`MemoryEvents.killed`). A shell still reports exit 137
    /// for it, so this is a `CommandFailed 137` with the cause known. The wire keeps it as
    /// exactly that plus one optional field, so a reader that does not know this case reads
    /// exit 137.
    | CommandKilledForMemory of MemoryKill

module MemoryKill =

    /// The short sentence that a block's mark and the agent's answer both say first.
    let headline = "killed: out of memory"

    /// Which of the two it was, in a few plain words.
    let cause (kill: MemoryKill) : string =
        match kill with
        | SandboxLimit -> "the sandbox reached its memory limit"
        | MachineOutOfMemory -> "the machine ran out of memory, and the kernel picked this process"

    /// How it ended, said after "was": "the shell was killed for memory (…)".
    let described (kill: MemoryKill) : string = sprintf "killed for memory (%s)" (cause kill)

    /// What the agent can do about it. The two causes ask for opposite things: use less, or
    /// try again when the other work is done.
    let advice (kill: MemoryKill) : string =
        match kill with
        | SandboxLimit -> "Use less memory (for example, fewer parallel build jobs), or ask for a higher limit."
        | MachineOutOfMemory -> "Other work on the machine used the memory, often in another sandbox. Running it again later may work."

    /// A stable token for each cause, for a hook to read.
    let token (kill: MemoryKill) : string =
        match kill with
        | SandboxLimit -> "sandbox-limit"
        | MachineOutOfMemory -> "machine"

/// The OOM counters of one cgroup v2 memory controller, as `memory.events` holds them.
///
/// Two of its lines are read, and nothing else. `oom` counts the times this cgroup hit
/// its own `memory.max` and the kernel had to act. `oom_kill` counts processes in this cgroup
/// that ANY OOM killer stopped, its own or one above it. The difference between the two is the
/// whole classification: a kill that this cgroup's own `oom` does not explain came from above.
type MemoryEvents =
    { Oom : int64
      OomKill : int64 }

module MemoryEvents =

    /// Where a container sees its own cgroup's counters (cgroup v2, a private cgroup
    /// namespace: the container's cgroup is the root of what it can see).
    let path = "/sys/fs/cgroup/memory.events"

    /// Read the file's text. `None` when either counter is missing or is not a number: a
    /// cgroup v1 host, a root cgroup (which has no such file), or garbage. A guessed zero
    /// would turn "cannot tell" into "nothing happened".
    let parse (text: string) : MemoryEvents option =
        let counters =
            text.Split '\n'
            |> Array.choose (fun line ->
                match line.Trim().Split ' ' with
                | [| name; value |] ->
                    match System.Int64.TryParse value with
                    | true, n -> Some (name, n)
                    | _ -> None
                | _ -> None)
            |> Map.ofArray
        match Map.tryFind "oom" counters, Map.tryFind "oom_kill" counters with
        | Some oom, Some oomKill -> Some { Oom = oom; OomKill = oomKill }
        | _ -> None

    /// What the counters say about a command that died from SIGKILL between two readings.
    ///
    /// `None` when no process in the sandbox was OOM-killed in the window: the SIGKILL came
    /// from somebody else (a person's `kill -9`, a timeout), and the result stays as it was.
    /// A kill with `oom` up as well is the sandbox's own limit. A kill without it came from
    /// above the sandbox. A window with both is said as the sandbox's own limit, because the
    /// sandbox did reach it.
    let killed (before: MemoryEvents) (after: MemoryEvents) : MemoryKill option =
        if after.OomKill <= before.OomKill then None
        elif after.Oom > before.Oom then Some SandboxLimit
        else Some MachineOutOfMemory

module CommandResult =

    /// The exit status that a shell (and docker, for an exec) reports for a process that
    /// SIGKILL ended: 128 + 9.
    let sigkillExit = 137
