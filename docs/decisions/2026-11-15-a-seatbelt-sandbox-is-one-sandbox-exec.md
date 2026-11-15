# A seatbelt sandbox is one `sandbox-exec`, with a supervisor inside

> Decided 2026-11-15 · Related: `app/Supervisor.fs`, `SeatbeltSandbox` in `app/Sandboxes.fs`,
> [2026-11-13-yession-writes-the-seatbelt-profile.md](2026-11-13-yession-writes-the-seatbelt-profile.md)

## Context

The seatbelt backend started each command as its own `/usr/bin/sandbox-exec`, as the leader
of a process group. When a sandbox stopped, the backend ended the groups that still ran. Two
things did not work:

- A process that made a session of its own was in no group that the backend knew. An
  Android emulator ran on after its sandbox stopped, and kept its memory. An adb server did
  the same.
- A command signalled only what it started. `kill` in one block did not reach a server that
  an earlier block started.

Both have one cause. To Seatbelt, each `sandbox-exec` is a sandbox of its own, and a profile
lets a process signal only its own sandbox (`(allow signal (target same-sandbox))`).

We measured on macOS 26.5, under the profile of a real sandbox:

- A second `sandbox-exec` with the same profile text gets EPERM for a process of the first.
- The kernel shows no environment for a program that Apple signed. So a variable that marks
  the sandbox does not find each process.
- Inside one `sandbox-exec`, a process signals a child that made a session of its own, and
  a child that process 1 adopted. A process in a second `sandbox-exec` and a process outside
  each sandbox get nothing.

## Decision

One `sandbox-exec` holds a sandbox. The first process in it is a supervisor
(`app/SandboxSupervisor.fs`), which is a second entry of this package beside the srt host.
The session writes one JSON object on a line to it, and reads one back (`Supervisor.Wire`).
The supervisor starts each command, with the environment of that command, and sends back
its output and its end.

The end of the supervisor's input is the stop. The session closes the input when the
sandbox stops, and the kernel closes it when the session dies. The supervisor then sends
SIGKILL to each pid number from 2 to 99999, its own excepted, and the kernel refuses the
signal for each process outside the sandbox. Two passes in a row that find nothing end the
stop.

Under no profile, the same pass ends each process of the user. So the pass takes two proofs
as values, and only a check makes each one (`Supervisor.Confinement`):

- At start, the kernel refuses a signal from the supervisor to the session. The session
  says which process it is and which user it runs as, so the refusal has no other cause.
- The kernel refuses a second profile for a child of the supervisor. A process that no
  profile confines enters a second profile. The supervisor asks at start, and again before
  each pass.

A supervisor is ready only with both proofs. Without them it starts no command, and the
sandbox does not start. So a rule of an operator that lets a signal out of the sandbox
refuses the start, in words.

The question for the second proof starts a process. A sandbox that fills the limit of
processes for the user leaves none for the question. Then the proof from the start stands
and the pass runs: that sandbox most needs its stop, and a process under a profile of ours
does not get a different profile. Measured: the kernel refuses a second profile under each
profile that denies something. A second profile that applied means no pass. So does a
question that ran and gave a different answer, and a question that did not end in two
seconds.

A fault of the supervisor that nothing caught is a stop too, with the same proofs. A
supervisor that left with its processes alive would leave nothing that can end them. A
start that Node refuses at once is not such a fault: it is one command that did not start.

The ending signal is a port. The entry gives SIGKILL. A rehearsal of a new build gives a
signal that a process ignores, with a witness outside the sandbox that records each signal.

## Alternatives

### A signal to each process at once (`kill(-1, …)`)

Measured: the kernel also sends such a signal to its sender, and the call succeeds while a
process of the user exists, in the sandbox or outside it. A supervisor cannot use it twice,
and its answer does not say which processes still run.

### End the groups from outside, and find the other processes

The session is outside each sandbox, so the kernel lets it signal each process of its user.
It does not know which of them belong to a sandbox. The two measurements in the context
are the ways that we tried.

### A supervisor that is not a Node program

A shell cannot carry the wire, and a compiled program needs a second toolchain in the
build. The srt host is already a Node entry that the session finds beside its own file,
and the supervisor uses that pattern. Its bundle holds Node built-ins only.

## Consequences

- **The processes of one sandbox signal each other.** `kill` in one block reaches a server
  that an earlier block started.
- **A block reads the environment of each other block of its sandbox.** A profile lets a
  process read the arguments and the environment of the processes of its own sandbox, and
  one `sandbox-exec` now holds each command. A block holds the loan of a person in its
  environment, so a second block of the same sandbox reads a live loan. While each command
  was its own `sandbox-exec`, a profile that refuses that read between two sandboxes kept
  the blocks apart. This decision gives that up on a Mac. It is the boundary that
  `app/GitGateway.fs` states for a sandbox, and a container has it too.
  [GAPS.md](../GAPS.md) names it, and a case pins it.
- **A command can end the supervisor.** `kill -9 -1` in a block ends each process of the
  sandbox, and the supervisor is one of them. The system can end it too, when memory is
  short. Such a supervisor makes no stop: the processes of the sandbox can still run, and
  the session says so. The sandbox then answers each command with that reason, until
  somebody stops it and starts it again. The supervisor gives its process the title
  `yession-supervisor`. Measured in a sandbox with the process list: `pkill node` and
  `pkill -f node` did not match it.
- **A cancel ends a process group only while its first process runs.** A cancel ends the
  process group of a command. The group is the command's own only until the first process
  of the command ends: after that, the system can give the number to a different process.
  So a cancel then sends nothing. The command ends when the processes that it left close
  its output, or when the sandbox stops.
- **A stop takes a fraction of a second, and ten seconds at most.** One pass over the pid
  numbers takes about 60 ms, the second proof takes about 30 ms, and a stop makes three
  passes when each process ends at once. A stop starts no question and no pass after ten
  seconds. The session waits for the supervisor as long as the supervisor says that a stop
  can take (`Supervisor.longestStopMs`), so it does not end a supervisor in the middle of
  its stop.
- **A stop ends a sandbox that is full of processes.** Measured with the limit of
  processes full: a stop ended 25 processes that filled the limit, and ten processes that
  forked with no pause, each time in less than a second.
- **A zombie answers a signal.** The kernel answers a signal for a zombie with success,
  whoever the zombie belonged to. So a pass counts a pid only when a live process has it
  (`os.getPriority`).
- **A sandbox reads the files of the supervisor.** They are the tree that holds its entry,
  and the `package.json` of its package (`SeatbeltSandbox.supervisorReads`). In a checkout
  of this repository, that tree is the compiled output.
- **A profile that Seatbelt cannot read stops the start of a sandbox.** It once stopped
  each command. The refusal still has the words of `sandbox-exec`.
- **srt on a Mac and the `host` backend end only process groups**, as before.
  [GAPS.md](../GAPS.md) says so.
