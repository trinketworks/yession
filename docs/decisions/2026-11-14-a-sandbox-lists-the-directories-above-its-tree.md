# A sandbox on the host lists the directories above its tree

> Decided 2026-11-14 · Related: `SeatbeltAbove` in `src/Yession.Domain/Seatbelt.fs`,
> `Launch.ManagerDataDir` in `src/Yession.Manager/SpawnWire.fs`,
> [2026-11-13-yession-writes-the-seatbelt-profile.md](2026-11-13-yession-writes-the-seatbelt-profile.md)

## Context

Some tools list the directories above the directory that they work in. The nix that devenv
runs opens each directory above a checkout. Fable reads each one to check the case of a
path. In a container those directories belong to the container. On the host's own userland
they belong to the host.

A Seatbelt profile from a policy names the tree of a sandbox and nothing above it. A
sandbox cannot list a directory that its profile does not name. So a cold `devenv shell`
failed at `/Users`, the first directory above the tree that the profile did not name.

On a default deployment the Manager keeps its data in the operator's home. The directories
above a session's tree then include that home, and the names in a home are private.

## Decision

The seatbelt backend lets a sandbox list each directory above its trees. A sandbox gets the
entry names of that one directory, and nothing that is in it (`SeatbeltPath.Node`). No file
asks for this. It is a consequence of the backend, as `safe.directory` is a consequence of
the docker backend.

The trees of a sandbox are the directory where it works, and each path that the session
gave it from the data directory down: its home, its workspace, its temporary directory. A
measurement set that list. The nix that devenv runs opened each directory above the home of
a sandbox, and the home was not above its checkout. A path that an operator granted in a
different place on the host is not a tree, and brings no listing with it.

Two kinds of directory are above a sandbox:

- **From the Manager's data directory down, a directory is Yession's own.** The sandbox
  lists it.
- **Above the data directory, a directory belongs to the host.** The sandbox lists it only
  when every account on the host can list it: its mode gives read and search to others.
  When one of them does not, the sandbox does not start. The refusal names the directory
  and tells the operator to move the data directory.

The Manager tells each session where its data directory is, in the envelope that it gives
a session at its start (`Launch.ManagerDataDir`). A session that no Manager started has
none, and a session under an older Manager has none. Such a session uses its own data
directory as the boundary.

## Why

- **A name that every account can list is not private.** The host already shows it to each
  local account. A sandbox that lists it learns nothing that the host keeps from them.
- **A home is private.** macOS gives a home the mode 750. The rule keeps a sandbox out of
  it with no list of special directories: the mode is the test.
- **A refusal, not a sandbox that starts and then fails.** Without the listing, a tool fails
  deep in a build with a message about one directory. The refusal comes at the start, in a
  sentence that the operator can act on.
- **Nobody writes it.** The directories of a session change with each session. An operator
  cannot name them in a profile, and a repository does not know them.

## Alternatives rejected

### A `walk` mode on a mount

The first design gave a mount a third mode, `walk`: the entry names of one directory. An
operator listed each directory above the data directory with that mode, and the session
added its own. It put a mode into the resource algebra that only one backend needs. It
also let an operator open the names of a home with one line, and that line looked like any
other mount.

### A Seatbelt rule from the operator for each directory

An operator can write such a rule (`seatbelt:` on a resource). It works for the
directories that do not change. It cannot name the directories of a session. A repository
would also have to select the resource by name, for a thing that each sandbox on the
backend needs.

### List each directory above the tree, private or not

This needs no boundary and no refusal. It shows a repository's code the names in the
operator's home.

### Start the sandbox without the private directory

The sandbox starts, and the tool that needs the listing fails later. The message then
comes from the tool, and it does not say what the operator can change.

## What this costs

- **A sandbox sees names in the data directory.** These are the ids of the other sessions
  and the names of the Manager's files. It reads none of them.
- **An operator moves the data directory.** While the data directory is in a home, a
  sandbox under the seatbelt backend does not start. [deployment.md](../deployment.md) says
  where to put it, and what docker under Colima then needs.
- **The test is the mode.** An access control list can make a directory private while its
  mode stays open. The rule does not read the list.
