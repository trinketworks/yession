# Yession writes the Seatbelt profile, and srt keeps the proxy

> Decided 2026-11-13 · Related: [GAPS.md](../GAPS.md) "`seatbelt` is a second confining
> backend", `src/Yession.Domain/Seatbelt.fs`,
> [2026-08-28-no-srt-fork-for-symlink-metadata.md](2026-08-28-no-srt-fork-for-symlink-metadata.md)

## Context

On macOS, srt did all of the confinement. Yession gave `@anthropic-ai/sandbox-runtime` a
configuration. srt wrote the Seatbelt profile and wrapped each command in it.

A work sandbox that builds needs three things that this configuration cannot say. We measured
each on srt 0.0.81:

- **One node.** A .NET build stats `/tmp/`. `/tmp` is a symlink. A process must be able to
  read the node `/tmp` itself, and must not be able to read `/private/tmp`. srt's allow list
  takes subtrees. A glob with a one-character set compiles to a pattern that matches one
  node, but that spelling depends on how srt translates globs.
- **A sysctl.** The Android emulator's qemu reads `hw.cachelinesize` at start. It stops with
  an illegal instruction when the profile refuses the read. srt has a fixed list of sysctls.
- **The loopback alone.** `allowLocalBinding` writes `bind` and `inbound` rules for every
  interface, because Seatbelt's `localhost` does not match an IPv4-mapped address.

srt's profile also carries refusals that another product needs. It denies `.claude/commands`,
`.vscode`, `.mcp.json` and the shell rc names wherever they appear.
`FilesystemConfinement.Unconfined` exists only so that a clone can write those names.

This repository adopted two forks of srt for the first need, and reverted each of them. The
ADR above describes the first. A narrowed fork followed it.

## Decision

Yession writes the profile. `SeatbeltProfile` builds it from a `SandboxPolicy` as data: one
`(deny default)`, and then allows only. `SeatbeltSandbox` starts each command under it with
`/usr/bin/sandbox-exec`.

srt keeps the one thing that a profile cannot do. Seatbelt matches an address and a port. It
does not match a host name. So srt's proxy still filters egress. The session starts srt's
manager for each sandbox as before, and uses only that proxy (`SrtSandbox.egressFor`). The
profile has one network rule: the proxy's loopback port.

The backend is `seatbelt`. An operator selects it for the work sandbox
(`YESSION_SESSION_WORK_BACKEND`). `srt` stays the default. `srt` also stays the confinement
on Linux.

The profile has allows only, because that lets a test check it. With no deny after the
first line, a sandbox holds the union of its rules. `SeatbeltProfile.allows` answers from the
rules alone. The cheap tier pins what a policy can and cannot put in a profile. The macOS job
compares those answers with the kernel's answers.

## Alternatives rejected

### Configure srt more

The glob spelling gives the node. A change upstream can give the sysctl. We can accept the
wide bind. With each of these the backend does less than it says, and the first depends on a
translation that srt does not promise.

### A fork of srt

The ADR above gives the argument, and the argument still applies. Each upgrade of a forked
confinement dependency starts the argument again.

### Add rules to the profile that srt generates

Seatbelt takes the last rule that matches, so allows that follow srt's text do work. But
nobody here wrote the result, and it has denies in it. A test can then say what a sandbox
holds only with a model of srt's translation. That translation changes between releases.

### A proxy of our own as well

This is not necessary. Three public calls reach srt's proxy: `initialize`, `getProxyPort`
and `getProxyAuthToken`. The proxy admits a client that has the token, with any user name.

## Consequences

- **Yession keeps the baseline.** `SeatbeltProfile.essentials` is the fixed part of the
  profile that srt 0.0.81 generates, transcribed. When a macOS release or a tool needs
  another rule, the system log shows the refusal. Add the rule there, and say what you
  measured.
- **Yession builds the proxy's environment** (`SeatbeltProxy.environment`). It sets eight
  variables. srt's wrapper sets twenty-six. A client that reads none of the eight does not
  use the proxy.
- **Two things write Seatbelt profiles on a Mac** until the session's own sandboxes move to
  this backend. [One mechanism per requirement](../technical-design.md#one-mechanism-per-requirement)
  says that they must move, and that `Unconfined` must go with them.
- **[GAPS.md](../GAPS.md) lists what the backend does not do yet.**

We measured these by hand on macOS 26, under a profile of this shape, before we wrote the
module:

- a cold `devenv shell`
- a .NET build of an Android app, with its JDK tools
- the Android emulator, with adb attached

### What would change this decision

- **srt takes a profile's rules as they are**: one node, a named sysctl, a port. Its
  configuration would then say what this module says, and one writer is better than two.
- **macOS removes `sandbox-exec`.** Apple deprecated it years ago, and srt also depends on
  it. Both would move together.
