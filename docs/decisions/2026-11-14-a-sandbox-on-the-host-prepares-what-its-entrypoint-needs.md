# A sandbox on the host prepares what its entrypoint needs

> Decided 2026-11-14 · Related: `HostSpec` in `src/Yession.Domain/Environment.fs`,
> `SessionEnvironment.runPreparation` in `src/Yession.Session/Environment.fs`,
> [2026-11-14-a-repo-names-the-body-the-operator-names-the-backend.md](2026-11-14-a-repo-names-the-body-the-operator-names-the-backend.md)

## Context

A `host:` sandbox runs a repository's work on the userland of the host. The repository's
tools are not there. A container gets its tools from its image. A sandbox on the host had
no step that installs them.

The first repository with a `host:` sandbox is a devenv project. Its `setup.sh` installs
devenv, and its work runs behind `devenv shell --`. `setup:` could not run that script.
`setup:` is work, so it runs behind the entrypoint, and the entrypoint did not exist before
the script ran.

The repository then made a script of its own the entrypoint. That script installed devenv
when it found none, and then started the shell. It was a second script beside `setup.sh`,
and it held knowledge about yession that belongs in `yession.yaml`.

## Decision

A `host:` body has a second key, `prepare:`. Its value is one command, as an argv.

- The session runs the command each time it starts the sandbox. It runs after the start-up
  checks, and before the session records the start. Nothing else can use the sandbox before
  the command ends.
- The command runs bare. The entrypoint is not in front of it.
- The command runs in the sandbox, where the sandbox puts a process. It holds what the
  sandbox holds, and nothing more.
- The command is not a block. The session lends it no credential.
- A command that fails refuses the start. The refusal quotes the end of what the command
  printed.

`setup:` does not change. It is a block that runs behind the entrypoint, after the start.

## Why

- **A body states what makes its userland.** A `container:` body has `image:` and `build:`
  for that. `prepare:` is the same statement for a `host:` body. So the key is in the body,
  and a container does not have it.
- **The operator's bound does not move.** The command is one more process under the policy
  of the sandbox. It reaches a path, a host or a socket only when a resource that the
  sandbox selected names it. The operator offers what makes a repository's script work, and
  the repository selects it.
- **A repository keeps one script for each kind of machine.** `setup.sh` prepares a machine
  that has only Nix. `yession.yaml` says that a session runs it, and what the work runs
  behind. No file in the repository has to know how a session starts a process.
- **The checks run first.** A sandbox that does not hold what its policy grants fails a
  check, and the check names the grant. A script that ran first would fail later, with an
  error about something else.

## Alternatives rejected

### `setup:` before the entrypoint

One key instead of two. But the docker backend runs the entrypoint when a container starts,
to find the shell for a terminal, and `setup:` runs after the start. The rule could not
hold for a container. The key would then have one order for each body.

### No entrypoint

A `host:` body with `setup: ./setup.sh` and no entrypoint works without a change. Each
command then needs `devenv shell --` in front of it, typed by whoever runs it. A terminal
under seatbelt runs each block as a separate process, so nothing can enter the shell one
time for all of them.

### The operator offers the tool

An `exec:` resource can put devenv on `PATH`. The operator then owns the version of a tool
that the repository's lock file pins. On the first host, the operator's package set had
devenv 2.0.6 and the repository's lock file asked for 2.4.

### A script in the repository as the entrypoint

This was the first form. The section above says what was wrong with it.

## Consequences

- **A start waits for the command, without a limit.** A container's start waits in the same
  way for its image and for the first run of its entrypoint.
- **The command runs at each start.** A repository writes a command that is safe to run
  again.
- **Nothing records the output of the command.** A refusal quotes the end of it.
  [GAPS.md](../GAPS.md) has this.
- **`ProcessEntry.Direct` has a second user.** The session's housekeeping runs bare,
  because it needs no toolchain. A `prepare:` command runs bare too, because the entrypoint
  can be a tool that it installs.
