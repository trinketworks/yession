# A sandbox has a door on a socket

> Decided 2026-11-22 · Related: `EntryDoor` in `app/EntryDoor.fs`, the `yession-enter` bin in
> `app/Enter.fs`, `EntryWire` and `DoorPlace` in `src/Yession.Session/EntryWire.fs`,
> `SandboxEntered`, `SandboxLeft` and `EntryProjection` in `src/Yession.Domain/`,
> `Sandbox.SpawnBytes` in `src/Yession.Domain/Sandbox.fs`, `isBusy` in `app/Host.fs`

## Context

A person works in a sandbox through a terminal of the session page. Some work needs more: an
editor on the person's own computer, a file copy, a port forward. The usual tool for that is
SSH.

Yession must not hold SSH, a tunnel or a VPN. A deployment has its own network and its own
login, and each deployment has different ones. Yession gives a mechanism, and a deployment
connects the parts.

The part that only a session can give is the last one: a command that runs in a sandbox with
its bytes connected to a process on the host. The session knows the backend of the sandbox,
its entrypoint, its directory and its environment. A deployment that ran `docker exec` did not
get those, and it had no answer for a sandbox on a different backend.

## Decision

A session has a **door**: a UNIX socket on the host. A process on the host that dials the door
runs one command in one sandbox of the session. That is one **entry**.

- **The setting.** `YESSION_SESSION_DOORS` names a directory. A session listens at
  `<directory>/<session id>.sock`, with mode 0660. With no variable, a session has no door.
- **The bin.** `yession-enter --door <socket> --as <subject> --via <label> <sandbox> --
  <command> [argument ...]` is the contract. Its input, output and error output are those of
  the command, with each byte unchanged. It ends with the exit code of the command. When it
  ends, the session ends the command.
- **The command.** The session starts the command behind the entrypoint of the sandbox, in its
  directory, with its environment, through the spawn that carries bytes (`Sandbox.SpawnBytes`).
  The session refuses a sandbox that does not run. An entry never starts a sandbox.
- **The record.** The session writes `SandboxEntered` and `SandboxLeft` to its log. The events
  are durable, as `TerminalOpened` and `TerminalClosed` are. `EntryProjection` holds the
  entries that are open.
- **In use.** An open entry keeps the session in use (`isBusy`). The Manager does not stop a
  session at its idle timeout while a person works through the door.
- **The limit.** One session has 16 open entries at most.
- **The announcement.** The `sandbox` announcement gains `door`, the path of the socket. A
  program that follows the registry stream thus knows how to enter each sandbox.

The exit codes of `yession-enter` for its own failures are 125 (it cannot reach the door, or
the door closed before the command ended) and 126 (the session refused the entry). 2 is a
command line that is wrong. 255 is a command that ended with no code of its own.

## Why

- **The Session owns the door, and not the Manager.** `Control.fs` states that environments and
  commands never cross the Manager channel. The session owns the sandbox.
- **A socket, and not a port.** The permissions of a file decide who can dial a socket. That
  is the rule of the Manager's own socket under `trusted-headers`. A loopback port has no such
  rule: each process on the host can dial it.
- **A bin is the contract, and the wire is private.** A session runs its old build until it
  stops, so the two ends of a door can be different builds. The first line of the wire carries
  a version, and each end refuses a version that it does not know. A deployment that calls the
  bin does not see the wire change.
- **The door is outside the data directory.** A sandbox can hold a grant of a part of the data
  directory, and a sandbox that can see a socket can dial it. The session refuses a directory
  in the data directory. The session also refuses a socket path that the platform cannot bind
  (104 bytes on macOS, 108 on Linux), with the length in the refusal.
- **The subject is text, and not an actor.** `--as` names who enters. The session records the
  name and does not check it. A `UserRef` is an identity that the Manager checked, and an entry
  has none. A caller on the socket can claim each name, as a caller on the Manager's socket
  can. The trust is the permissions of the socket.
- **A label, and not a protocol.** `--via` is a word that the deployment gives, for example
  `ssh`. The session shows the word and gives it no meaning. Yession thus knows that a person
  entered, and does not know SSH.
- **No shell on a pty in this decision.** `yession-enter` always takes a command, and its
  streams are pipes. A shell that the session opens on a pty must be a terminal of the session
  (`TerminalOpened`, with a recording), not a second type of shell. That form waits for a
  decision on recording (docs/GAPS.md).
- **A misconfigured door stops the session.** An operator who set the variable asked for a
  door. A session that started with no door would look the same as one that has it.

## Consequences

- An entry has what a terminal has: the environment of the sandbox, which includes each secret
  that the session resolved at its start. Access to the door of a session equals access to the
  session.
- The session does not record the bytes of an entry. The log has who entered, when, and for
  how long. docs/GAPS.md states the gap and two candidates.
- The byte spawn has no backpressure. The session holds in memory what a command did not read
  yet, and what a caller did not read yet. docs/GAPS.md states it.
- A session that starts closes each entry that its log holds open, with the reason `session
  restarted`. The process that held those entries is gone, and an entry that stayed open would
  keep the session in use for all time.
- The package has a third command. Each command of the package answers `--version`, and the
  install checks call it.
- The Manager does not change. It passes its environment to each session, and the variable is
  one more name in it.
