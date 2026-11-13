# A repo names the body of its sandbox, and the operator names the backend

> Decided 2026-11-14 · Related: rule 27 of
> [technical-design.md §5](../technical-design.md#5-architectural-invariants),
> `SandboxBody` in `src/Yession.Domain/Config.fs`,
> [2026-11-13-yession-writes-the-seatbelt-profile.md](2026-11-13-yession-writes-the-seatbelt-profile.md)

## Context

A repo's work sandbox was always a container. Some repositories cannot build in one. An
Android app built with .NET needs tools that exist for x64 Linux and for macOS, and do not
exist for arm64 Linux. A container on an Apple silicon Mac is arm64 Linux, so that build
does not run in one. It does run on the Mac itself.

So a repo's sandbox needs a second kind of place to run: the userland of the host. On a Mac,
Seatbelt confines a process there. On Linux, bubblewrap does.

The question was who says "seatbelt". The first design had two answers at the same time. A
repo's file had a `seatbelt:` block, and the operator's profile had a `seatbelt:` block that
allowed it.

## Decision

A repo's file names the BODY of a sandbox, and nothing about what confines it.

- `container:` brings a userland. The repo owns an image or a build, a process and limits.
- `host:` uses the userland of the host. The repo owns only what its work runs behind
  (`entrypoint`).

A repo's sandbox names one of the two. The session refuses a sandbox that names both, and a
repo's sandbox that names none.

The operator names the BACKEND that runs a repo's `host:` sandbox, in
`YESSION_SESSION_REPO_BACKEND`. The values are `seatbelt` and `srt`. When the operator does
not set it, the session refuses a repo's `host:` sandbox. A container needs no setting.

The rule for a third body: a body is a case when the repo owns different things for it. A
different mechanism under the same body is not a case. It is a backend.

## Why

- **Policy and mechanism stay apart.** Seatbelt is mechanism. `Environment.fs` already said
  who owns it: "The backend is the operator's … the spec is partly the repo's."
- **A repo does not know this host.** `Config.fs` keeps a host path and a hostname out of the
  file for that reason. The mechanism that confines a process on this host is the same kind
  of fact. A file with a `seatbelt:` block would work on a Mac only, and the repo would have
  no reason of its own for that.
- **One statement for one fact.** "Which backend runs this sandbox" has one decider:
  `SandboxRuntime.backendFor`, with the operator's configuration. A block on each side would
  be two statements that must agree, and a rule for when they do not.
- **Rule 26 has the same shape.** A repository's container limits narrow the operator's, and
  only the operator names a cgroup. The name at the level of the mechanism is the
  operator's.

## Alternatives rejected

### A `seatbelt:` block in the repo's file and in the profile

This was the first design. The section above says why it is wrong.

### The operator alone, with no word in the repo's file

A repo's sandbox with no `container:` block would run on the host. But a sandbox on the host
has something of its own to say: what its work runs behind. That needs a block, for the
reason `command` needs `container:`. A flat `entrypoint:` beside `container.entrypoint`
would be two places for one thing.

A repo's sandbox that names no body also stays a refusal. The repo is the only author who
knows which body its commands need. A guess starts a sandbox that fails at its first
command.

### A block in the profile that allows the body

The profile has a `container:` block because the operator bounds what a repo's container
asks for. A `host:` body asks for nothing that the operator can bound, so a block would only
restate the backend setting.

## Consequences

- **A repo cannot say "macOS only".** A `host:` sandbox starts on any host whose operator
  named a backend. If that becomes necessary, add a requirement that the host satisfies or
  refuses. Do not add the name of a mechanism.
- **`entrypoint` works the same under each backend on the host.** `Sandboxes.entered` holds
  the one rule for `host`, `srt` and `seatbelt`.
- **A reader that has only a sandbox's name asks the fold for its body**
  (`SandboxRuntime.namedBackend`). Before this change the scope alone decided the backend.
- **The session judges a repo's asks on the backend of each sandbox**
  (`RepoSandboxes.capabilitiesOf`), because a container and a sandbox on the host do not
  scope the same things.
