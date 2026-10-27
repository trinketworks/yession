# The git sandbox has a backend of its own, and it is srt

> Decided 2026-10-27 · Related: [deployment.md](../deployment.md),
> [2026-10-27-repository-files-come-from-the-object-store.md](2026-10-27-repository-files-come-from-the-object-store.md),
> rule 21 of [technical-design.md §5](../technical-design.md#5-architectural-invariants)

## Context

A session runs three sandbox families. The work sandbox runs the agent's terminals, blocks and
`setup:`, and defaults to `srt`. The agent sandbox runs the `claude` CLI, and defaults to `host`.
The git sandbox runs the session's own git verbs: clone, fetch, switch, status, log and diff.
It took the agent backend. So in every deployment that kept the defaults, the git verbs ran as
the operator, unconfined.

The agent backend is `host` for a reason that is the CLI's: the Bun `claude` binary ignores
`HTTP_PROXY`, so under srt its API egress fails. That reason does not hold for git. The `Srt`
suite shows that git and node honour srt's proxy.

The git verbs work on checkouts that every work sandbox can write, `.git/config` included. One
exec vector stays open whatever the env says: `filter.<driver>.smudge` runs on `switch_branch`.
Under `host` it runs as the operator.

## Decision

- `YESSION_SESSION_GIT_BACKEND` chooses the git sandbox's backend: `host` or `srt`. Unset is
  `srt`. Anything else stops the boot (`SandboxBackend.parseGit`).
- It is parsed in `app/SessionMain.fs` beside the other two, and the srt tools check covers it.
- The agent backend keeps its default, `host`.
- Under srt the git sandbox reaches only the git gateway's route, and writes only the repos
  directory.

## Alternatives rejected

**Change the agent backend's default to srt.** The CLI does not work there yet, and that is a
separate change.

**Keep one variable for both.** The two defaults have different reasons. One variable makes
the weaker default win for both.

## Consequences

- A smudge filter that a work sandbox plants runs with the git sandbox's reach, which the
  writing sandbox already had.
- A host without srt's tools must set `YESSION_SESSION_GIT_BACKEND=host` too, as it must for the
  work sandbox. That is the operator's explicit choice, and docs/deployment.md says what it costs.
