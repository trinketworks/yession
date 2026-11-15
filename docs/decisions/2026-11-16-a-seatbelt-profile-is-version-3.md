# A Seatbelt profile is version 3, and allows by name what a process needs

> Decided 2026-11-16 · Related: `SeatbeltProfile` in `src/Yession.Domain/Seatbelt.fs`,
> [2026-11-13-yession-writes-the-seatbelt-profile.md](2026-11-13-yession-writes-the-seatbelt-profile.md),
> [GAPS.md](../GAPS.md) "`seatbelt` is a second confining backend"

## Context

The profile was version 1 of Seatbelt's language: one `(deny default)`, and then allows.
On 2026-11-15 we found that a command read the environment of each process of its user,
outside its sandbox too. A block holds the loan of a person in its environment. The cause
was the default: `(deny default)` of version 1 does not refuse each operation.

We asked the kernel about each operation name of macOS 26.5.2 (`sandbox_check`, 195 names):

- Version 1 leaves 55 operations open under `(deny default)`. The operations that read a
  process are among them (`process-info*`), and so is each system call.
- Version 3 leaves 15 operations open. It refuses each system call until a rule allows it.

We first wrote a fix that kept version 1 and added two denies by name. It did not merge. It
closed the read of an environment. It did not close `csops`, which answers the code
signature of a process outside the sandbox, and it left each other open operation as it
was.

## Decision

A profile is version 3. Its head denies by default, and the fixed text of each profile
allows by name what a process needs (`SeatbeltProfile.essentials`).

- **Each allow has a measured need, but for one line.** We ran a workload with each line
  but one, and the comment of a line says what stops without it. The line for the policy
  `Sandbox` is the exception (below). The runs were a shell with the usual commands,
  `pgrep`, git, Node, the setup of a devenv shell, a .NET build of an Android app with its
  tests, the Android emulator with its tests, and the Mac cases of this repository.
- **The fixed text allows system calls as whole families** (`syscall-unix`,
  `syscall-mach`, `system-fcntl`).
- **It allows a call to a policy of the kernel by the name of the policy**, for `AMFI`,
  `Sandbox` and `Quarantine`. Each has its own line. Something stops without the line for
  `AMFI` and without the line for `Quarantine`. Nothing that we measured stops without the
  line for `Sandbox`. That line is there for the reason that `sandbox-check` stays open:
  each process makes the call when it starts, and nobody measured what a refusal changes.
- **The head denies by name what the default leaves open and no workload attempts.** That
  is eight of the 15 operations. A workload attempts each of the other seven, and
  `SeatbeltProfile.head` says what a deny of each one stops.
- **The head keeps its two denies for the arguments and the environment of a process.** The
  default of version 3 refuses those operations on the macOS that we measured. The lines
  hold on a macOS whose default is different, and against a rule of an operator that
  allows each sysctl.
- **A deny of a new name stands behind a guard.** `sandbox-exec` stops at a name that it
  does not know, and six of the eight names came after macOS 13. Public lists say what
  each release changed in the Sandbox kernel extension. The list for macOS 15.4 adds one
  of the names. For the other five, the lists that we found are of iOS, which has the same
  extension: iOS 26.0 adds three, and iOS 26.4 adds two. `(when (defined? 'name) ...)`
  leaves the deny out where macOS does not know the name, and a Mac case asks the macOS
  that runs it about each name.

## Alternatives rejected

### Version 1 with denies by name

This was the first fix. Each open operation needs a deny that somebody thought of. We
found `csops` open only when we asked the kernel about each name. With version 3 the
default does that work, and the profile says what it allows.

### A filter of system calls by number

Version 3 can allow a system call by its number, and Apple's own profiles do so for a
daemon that makes a few calls. A sandbox here runs a toolchain. Nobody measured the list of
calls that nix, .NET, a JVM or an emulator makes, and the list is different on each macOS.
A wrong list stops a build at a call that no log names clearly.

### The whole family of calls to the kernel's policies

`(allow system-mac-syscall)` with no filter lets a process call each policy of the kernel.
The workloads ask three policies. The loader asks `AMFI`, and ignores each `DYLD_` variable
when the profile refuses that. `cp` and `tar` ask `Quarantine`, and a copy has no
quarantine mark when the profile refuses that. Each process asks `Sandbox` about its own
sandbox, and no workload stopped when the profile refused that.

### A deny for each of the ten operations that no workload needs

Two of the ten are `sandbox-check` and `mach-task-special-port-set`. Each process attempts
both when it starts. With both denied, no workload failed, and the kernel refused 2639
calls in seven runs. A deny that makes a refusal changes what a program does, and
nobody measured where that shows. So the rule for a deny is that no workload attempts the
operation.

### A filter by path for the lookup of a path

Version 3 refuses the lookup of each path (`file-test-existence`) until a rule allows it.
With a rule for each granted path only, a sandbox would not learn if a file exists in a
place that nobody granted. We measured that such a rule breaks `ls /` and each path through
`/etc`, `/tmp` and `/var`: Seatbelt judges the lookup on the path as a program wrote it. So
the allow has no filter, as in version 1, and GAPS.md has the limit.

## Consequences

- **`sandbox-exec` reads a rule of an operator as version 3.** A rule with a word that
  only version 1 knows stops the start of its sandbox, and the refusal shows the word.
  [deployment.md](../deployment.md) lists the words, each with what to write in its place.
- **A sandbox has fewer operations than before.** Version 3 refuses operations that a
  sandbox had under version 1 and that no workload needed. deployment.md lists them. An
  operator can allow one with a rule.
- **We measured one macOS.** Nobody measured macOS 13, 14 or 15, and the default of version
  3 can leave a different set of operations open there. Each word that this decision added
  to a profile is in Apple's own profiles of macOS 12, or stands behind a guard.
- **A unix socket rule has no word for its kind.** Version 3 has no `unix-socket`. A filter
  by path on a network operation is a unix socket. We measured that the rule opens a socket
  at the path, no socket at another path, and no TCP listener.
- **The order of precedence has a first rule that we did not know.** A rule that names an
  operation wins over a rule that names a family with `*`, in either order. The header of
  `Seatbelt.fs` has the three rules, and deployment.md tells an operator which rules open
  an environment again.
- **[GAPS.md](../GAPS.md) says what a sandbox still learns**: the pid and the name of each
  process, the arguments with the rule for the process list, and whether a path exists.

### What would change this decision

- **A measured list of system calls** for the toolchains that run here. A filter by number
  would then be a rule like each other rule.
- **A macOS where version 3 refuses an operation that a process needs**, or leaves one
  open that reads another process. The system log shows the first. Only a question to the
  kernel about each name shows the second.
- **macOS removes `sandbox-exec`**, as in the decision that this one follows.
