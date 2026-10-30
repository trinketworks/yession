# A sandbox leases what it uses of a shared volume; the operator sweeps

> Decided 2026-09-30 · Supersedes nothing · Related:
> [src/Yession.Domain/OperatorProfile.fs](../../src/Yession.Domain/OperatorProfile.fs) —
> `VolumeMaintenance`, the one thing an operator declares.
> [src/Yession.Domain/Resources.fs](../../src/Yession.Domain/Resources.fs) — the `Volume` leaf,
> which this decision leaves as it was.

## Context

The shared volume is the point. Sessions on one host are semi-trusted, and what one session builds
into a warm store makes the next one fast. The volume is also unbounded. On the host that prompted
this decision, a Nix store grew ~2 GB a day with nothing to collect it.

Collecting it safely needs to know what every sandbox still uses, and **only the sandbox can say**:

- The roots of a Nix store are symlinks into the checkout of the sandbox, which no other container
  can resolve. A collector outside takes the shell of every other session for a stale root and
  removes it.
- The paths that a running process holds open are in the process table of that sandbox alone.
- Every sandbox checks out to the same path, so their roots collide on the shared volume, and at
  most one survives.

A lease written from inside, per sandbox, says what the sandbox uses in terms that any container can
read.

## Decision

An operator may declare, on a `volume` leaf, a **pin** and an **interval**:

```yaml
nix-container-store:
  volume:
    name: yession-nix
    at: /nix
    mode: write
    maintain: { pin: /nix/var/yession/pin, every: 10m }
```

A session runs `pin` inside each of its sandboxes that holds the volume. It runs it when the sandbox
starts, every `every` after that, and once more before it removes the sandbox. `pin` receives one
argument: the **lease** of that sandbox, a directory of its own on the volume. What `pin` writes
there is the operator's. The product never reads it.

Collecting is **not the product's**. The operator's own sweep runs on the operator's own schedule.
It reads the leases and keeps whichever it judges still wanted. That means every lease renewed since
it began, and those of ended sessions for as long as it chooses. It removes the rest. The product
ships an example pair for a Nix store, and it knows nothing about Nix.

## Alternatives rejected

- **The product collects, knowing Nix.** A `NixStore` resource kind: the session asks nix for roots,
  writes gcroots, and runs the collector. It works. It puts the layout of one tool into the domain.
  The next shared cache (a pnpm store, a cargo registry) would need a kind of its own.
- **Generations, collected by replacement.** A read-only base per generation, with a private
  writable layer per sandbox. Retire a generation when nothing holds it. No command runs in a
  sandbox, and nothing is collected in place. But nothing that one session builds reaches the next,
  which is the whole reason the volume is shared. We rejected it for that.
- **Only while nothing runs.** An operator-side collector that waits until no sandbox holds the
  volume. It needs nothing from the product. It loses the shell of every ended session each time,
  and on a host with a session always open it never runs.

## Consequences

The product learns one generic thing: run the command of the operator in the sandbox, on a clock. It
keeps no opinion about what the volume holds, how long a lease outlives its session, or when to
sweep.

A sweep allows for a lease that is up to `every` stale. Builds still in flight are the collector's
own to protect. For Nix, that means its temporary roots, which it checks by lock and not by process
id.

The header of [`app/VolumeLeases.fs`](../../app/VolumeLeases.fs) points here for why the product
does this at all, and does no more.
