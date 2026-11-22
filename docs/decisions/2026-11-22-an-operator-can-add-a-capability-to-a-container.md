# An operator can add a capability to a container

> Decided 2026-11-22 · Related: `ResourceLeaf.Capability` and `LinuxCapability` in
> `src/Yession.Domain/Resources.fs`, `HostDistinction.KernelCapabilities`,
> `Sandboxes.limitsFor` and `DockerSandbox.create` in `app/Sandboxes.fs`,
> [a resource can pass a device into a container](2026-11-18-a-resource-can-pass-a-device-into-a-container.md)

## Context

A docker sandbox drops every capability and adds three: `CHOWN`, `FOWNER` and
`DAC_OVERRIDE`. A build needs those three, and most work needs no other.

Some programs need one more. An `sshd` that runs as root is the case that we measured, with
OpenSSH 10.3 in the `nixos/nix` image. Before a login, `sshd` moves to an empty directory
with `chroot` and changes to a user with no rights. With the three usual capabilities it
stops at `chroot("/var/empty"): Operation not permitted`. With `SETGID` and `SYS_CHROOT`
added it stops at `setresuid 74: Operation not permitted`. It needs `SETUID`, `SETGID` and
`SYS_CHROOT`.

Nothing that an operator or a repository could write gave a container a capability. The
other answer was to give the three names to each container, which widens each sandbox for
the need of a small number.

## Decision

A resource can name capabilities of the Linux kernel, under `capability:`. Each is a name as
docker writes it, without the prefix `CAP_`. The docker backend adds each granted name to
`CapAdd`, after the three that it always adds. `CapDrop ALL` and `no-new-privileges` stay.

A capability is a tenth primitive (`ResourceLeaf.Capability`), and the operator's alone, as a
device is. A file of a repository cannot name one, so a repository selects the resource by
name. The session's user is not asked, because the harm of a capability lands on the host
and not on them (invariant 25).

Only the docker backend adds a capability (`HostDistinction.KernelCapabilities`). Each other
backend withholds the grant with a reason, as it withholds a device.

`LinuxCapability.parse` checks the shape of a name: upper case letters, digits and `_`, no
prefix `CAP_`, and not `ALL`. Yession keeps no list of names. The daemon refuses a name that
its kernel does not have, and the start of the sandbox fails with the words of the daemon.

## Why

- **The operator's, and by name.** The resource algebra already says who gives what: the
  operator offers, and a repository selects. A capability that only a sandbox with the
  resource holds keeps each other sandbox as narrow as before.
- **A primitive in the language of one backend.** A Seatbelt rule and a device are the same
  type of grant. A different backend has no equal for a capability, so one name for all
  backends would promise what they cannot give.
- **No list of names.** The kernel adds capabilities, and a list in Yession would be old
  after the next one. The daemon is the authority, and its refusal reaches the person.
- **Yession refuses `ALL`.** A grant names what it gives. `ALL` also gives each capability that a
  later kernel adds.

## Consequences

- Root in a container that holds `SETUID` and `SETGID` can become each other user. In a
  container without a user namespace, root with `DAC_OVERRIDE` could already read and write
  the files of each user in its mounts.
- Some capabilities are a way out of the container, for example `SYS_ADMIN` and
  `SYS_MODULE`. Yession does not refuse them. The operator who writes one in the profile
  decides it.
- A resource with a capability must not be under `always:` on a host that also runs
  sandboxes on a different backend. Those sandboxes withhold it, and one that `uses:` it
  does not start.
