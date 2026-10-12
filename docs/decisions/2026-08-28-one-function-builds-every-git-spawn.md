# One function builds every git spawn, so a probe cannot gate verbs it does not run as

> Decided 2026-08-28 · Supersedes nothing · Related: [GAPS.md](../GAPS.md) "Every binary a confined
> spawn execs is NAMED". `app/Repos.fs` (`gitExec`, `hardenedEnv`, `unusableGit`).
> `tests/Yession.Tests/GitIntegration.fs` "a global git config in the operator's home never reaches
> a verb"

## Context

`YESSION_BIN_GIT` names git for a confined spawn. Unset, the verbs fall back to PATH, so an off-Nix
install does not regress. That fallback is wrong for an `npm i -g yession` on macOS. The git sandbox
therefore proves `git --version` before any verb runs one. If the proof fails, the sandbox refuses
with a sentence that names `YESSION_BIN_GIT` and the resources profile of this host. It does not
pass through the excuse of the host binary.

That probe then refused a git that worked, because it was the one git spawn built without the
hardened env. `git --version` ran with an EMPTY env, which no verb ever runs with:

1. git resolves its global config path before it does anything at all.
2. It tolerates `EACCES` there and treats every other errno as fatal.
3. Seatbelt answers `EPERM`.

On a macOS host whose operator has a `~/.config/git/config`, the probe died with `fatal: unable to
access … (Operation not permitted)`, exit 128. A home-manager install always has that file. Every
repo verb was then refused for the whole lifetime of the sandbox, in words that blamed the binary
and the read scope. Neither was at fault. The doc comment of `gitExec` in `app/Repos.fs` states this
fault.

Taking the advice of the refusal would have made it worse. Declaring `~/.config/git` as a resource
and defaulting it hands back part of the home that the read scope exists to deny. It would fix a
probe that should never have read that file.

### Why Linux could not catch it

srt denies a read on Linux by mounting emptiness over the path, so a denied config reads as ENOENT
and git shrugs. Seatbelt denies the read in place, and `EPERM` is fatal. Suppose a suite plants an
UNREADABLE file and expects git to fail. That suite is green on the platform that CI runs, whatever
env the spawn carries. The assertion passes for the wrong reason and pins nothing. That is why this
shipped.

## Decision

One function builds every git that the repo verbs spawn, the start-up probe included. That function
carries the hardened environment. A probe that runs git differently from the verbs is not a probe of
the verbs.

The regression test plants a MALFORMED config rather than an unreadable one, somewhere the sandbox
may read. A config that git PARSES is a fault that git reports as its own. That is exactly what an
unhardened spawn hands back to whoever asked, and it fails identically on both platforms. The test
pins that no git spawned here reads the global config of the operator. It does not pin the errno
that made the difference visible. The comment in `GitIntegration.fs` states the reasoning.

## Consequences

This is the colocation rule in AGENTS.md, on a seam that looked too small to have one. Two call
sites built a spawn independently, and one of them was a check on the other. The invariant "a git
spawned here carries the hardened env" held only because each caller remembered. The caller that
forgot was the one whose job was to catch mistakes.

A probe belongs to the thing it probes. If it constructs its subject differently, it tests a
different subject.

### What would change this decision

- **srt reports a denied read as ENOENT on macOS too.** The platforms would then agree, and the
  reason the fault was invisible would go. The reason for the single spawn builder would stay.
