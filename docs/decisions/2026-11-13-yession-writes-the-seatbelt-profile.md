# Yession writes the Seatbelt profile, and srt keeps the proxy

> Decided 2026-11-13 · Related: [GAPS.md](../GAPS.md) "`seatbelt` is a second confining
> backend", `src/Yession.Domain/Seatbelt.fs`,
> [2026-08-28-no-srt-fork-for-symlink-metadata.md](2026-08-28-no-srt-fork-for-symlink-metadata.md)

## Context

On macOS, confinement was srt's from end to end. Yession handed
`@anthropic-ai/sandbox-runtime` a configuration, and srt wrote the Seatbelt profile and
wrapped each command in it.

A work sandbox that builds needs three things that this configuration cannot say. Each was
measured on srt 0.0.81:

- **One node.** A .NET build stats `/tmp/`. `/tmp` is a symlink, and its own node has to be
  readable while `/private/tmp` is not. srt's allow list takes subtrees. A glob with a
  one-character set happens to compile to a pattern that matches one node, which is a spelling
  that rests on how srt translates globs.
- **A sysctl.** The Android emulator's qemu reads `hw.cachelinesize` at start and dies with
  an illegal instruction when it is refused. srt's list of sysctls is fixed.
- **The loopback alone.** `allowLocalBinding` writes `bind` and `inbound` rules for every
  interface, because Seatbelt's `localhost` does not match an IPv4-mapped address.

srt's profile also carries refusals written for another product: `.claude/commands`,
`.vscode`, `.mcp.json` and the shell rc names are denied wherever they appear.
`FilesystemConfinement.Unconfined` exists only to get a clone past them.

Two forks of srt were adopted for the first need and reverted (the ADR above, and the
narrowed fork after it).

## Decision

Yession writes the profile. `SeatbeltProfile` builds it from a `SandboxPolicy` as data: one
`(deny default)`, and then allows only. `SeatbeltSandbox` spawns each command under it with
`/usr/bin/sandbox-exec`.

srt keeps the one thing a profile cannot do. Seatbelt matches an address and a port, never
a host name, so egress is still filtered by srt's proxy. The session starts srt's manager
for each sandbox as before and uses only that proxy (`SrtSandbox.egressFor`). The profile's
one network rule is the proxy's loopback port.

The backend is `seatbelt`, and an operator chooses it for the work sandbox
(`YESSION_SESSION_WORK_BACKEND`). `srt` stays the default, and stays the confinement on Linux.

Allows only, because that is what makes a profile checkable. With no deny after the first
line, what a sandbox holds is the union of its rules. `SeatbeltProfile.allows` answers from
the rules alone, the cheap tier pins what a policy can and cannot put in a profile, and the
macOS job compares those answers with the kernel's.

## Alternatives rejected

### Configure srt harder

The node can be had through the glob spelling, the sysctl by a change upstream, and the wide
bind by accepting it. Each is a reason this backend would do less than it says, and the
first depends on a translation srt does not promise.

### A fork of srt

The ADR above has the argument, and it has not changed: a fork of a confinement dependency
is argued again at every upgrade.

### Add rules to the profile that srt generates

Seatbelt takes the last rule that matches, so allows put after srt's text do work. The
result is a profile that nobody here wrote, with denies in it. No test could say what a
sandbox holds without a model of srt's translation, and that translation changes between
releases.

### A proxy of our own as well

Not needed. srt's proxy is reached through three public calls (`initialize`, `getProxyPort`,
`getProxyAuthToken`), and it admits a client by that token under any user name.

## Consequences

- **The baseline is Yession's to keep.** `SeatbeltProfile.essentials` is the fixed part of
  what srt 0.0.81 generates, transcribed. When a macOS release or a tool needs another rule,
  the refusal is in the system log, and the rule is added there with what was measured.
- **The proxy's environment is built here** (`SeatbeltProxy.environment`): eight variables,
  where srt's wrapper sets twenty-six. A client that reads none of the eight is not sent to
  the proxy.
- **Two things write Seatbelt profiles on a Mac** until the session's own sandboxes move to
  this backend. [One mechanism per requirement](../technical-design.md#one-mechanism-per-requirement)
  says they should, and that `Unconfined` should go with them.
- **What the backend does not do yet** is in [GAPS.md](../GAPS.md).

Measured by hand before any of this was written, on macOS 26, under a profile of this shape:
a cold `devenv shell`, a .NET build of an Android app with its JDK tools, and the Android
emulator booting with adb attached.

### What would change this decision

- **srt takes a profile's rules as they are**: one node, a named sysctl, a port. The
  configuration would then say what this module says, and one writer is better than two.
- **macOS drops `sandbox-exec`.** It has been deprecated for years and srt stands on it as
  well, so both would move together.
