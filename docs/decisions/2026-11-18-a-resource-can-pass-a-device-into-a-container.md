# A resource can pass a device into a container

> Decided 2026-11-18 · Related: `ResourceLeaf.Device` in `src/Yession.Domain/Resources.fs`,
> `HostDistinction.Devices`, `Sandboxes.limitsFor` and `DockerSandbox.create` in
> `app/Sandboxes.fs`

## Context

A repository's tests can boot a VM. KOS Workspaces runs its workloads in VMs with smolvm, and
its `vm` tests need `/dev/kvm`. A docker sandbox drops every capability except three and
gets the devices that docker gives each container. `/dev/kvm` is not one of them. Nothing that
an operator or a repository could write gave a container a device of the host.

## Decision

A resource can name devices of the host, under `device:`. Each is a path under `/dev`. The
container gets each one at the same path, through docker's `--device` with the cgroup
permissions `rw`. The container gets no capability for a device: root opens the node with the
`DAC_OVERRIDE` that it already has.

A device is a ninth primitive (`ResourceLeaf.Device`), and the operator's alone, as a volume
is. A repository's file cannot name a host path, so it selects the resource by name. The
session's user is not asked, because the harm of a device lands on the host and not on them
(invariant 25).

Only the docker backend passes a device through (`HostDistinction.Devices`). Every other
backend withholds the grant with a reason, as it withholds a named volume. The host backend
allows everything, and still has no device to give that the host does not already show.

## Why

- **A primitive and not a mount.** A bind mount of `/dev/kvm` gives the node, but the
  container's device cgroup still refuses to open it. `--device` adds the cgroup rule.
- **The same path on both sides.** A tool looks for a device where the kernel puts it. An
  `at:` would add a way to put a device where nothing looks.
- **`rw` and not `rwm`.** The container opens the node that docker made, and needs to make
  none.

## Consequences

- An operator who grants `/dev/kvm` lets every sandbox that holds it run code in the host's
  kernel through KVM's interface. That is the operator's decision, written in the profile.
- A host that is itself a VM has `/dev/kvm` only where its hypervisor offers nested
  virtualization. On EC2 that is some Intel instance types, and no Graviton type.
