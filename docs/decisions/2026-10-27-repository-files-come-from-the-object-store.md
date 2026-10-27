# A repository's files reach the session only through git's object store

> Decided 2026-10-27 · Related: [technical-design.md §4](../technical-design.md#4-repo-local-configuration)
> and rule 20 of §5

## Context

After a clone, the session process read three kinds of file from the checkout's work tree:
`AGENTS.md`, `.agents/skills/*/SKILL.md`, and `yession.yaml`. It read them with `Fs.exists` and
`Fs.readText`, which follow symlinks, as the operator's user, unconfined.

A repository chooses what its work tree holds. A repository whose `AGENTS.md` is a committed
symlink to `~/.ssh/id_ed25519` put that key into `RepoCloned.AgentsMd`: the durable event log,
every peer's timeline, and the model's context. The same held for a skill and for
`yession.yaml`. And every work sandbox can write a checkout, so a symlink can also appear after
the clone, between any check and any read.

## Decision

- The session reads a repository's file out of git's object store, at `HEAD`, in the git
  sandbox. `ls-tree -r -z HEAD -- <paths>` judges the entry, and `cat-file blob <id>` reads it.
  The service exposes this as `ReposService.ReadCommitted`.
- Only a regular blob (mode `100644` or `100755`) is a file. A symlink (`120000`) or a submodule
  (`160000`) is no file.
- `RepoConfig.read` and `readAll` take that reader as a capability. They no longer take the
  repos directory, so no caller can hand them a path.
- `yession.yaml` is read from `HEAD`, not from the working tree. An edit that nobody has
  committed declares nothing.
- The character cap on `AGENTS.md` and the tolerance for a malformed `SKILL.md` stay.

## Alternatives rejected

**Guard the host read with `lstat` or `realpath`.** A work sandbox can write the checkout, so
it can swap a file for a link between the check and the read. The race is the defect.

**Read the working tree inside the git sandbox.** Under srt that would confine the read. Under
`host` the git sandbox is the operator, so the symlink would still reach the operator's files.
The object store holds no symlink target, whichever backend runs git.

## Consequences

- An uncommitted edit to `yession.yaml` no longer changes the declarations. A person who wants
  it applied commits it.
- Reading costs one `ls-tree` and one `cat-file` per file, in the git sandbox, instead of a host
  read. The skills cost one `ls-tree` for the directory and one `cat-file` per skill.
