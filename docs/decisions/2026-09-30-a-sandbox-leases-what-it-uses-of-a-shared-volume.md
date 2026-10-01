# A sandbox leases what it uses of a shared volume; the operator sweeps

> Decided 2026-09-30 · Supersedes nothing · Related:
> [src/Yession.Domain/OperatorProfile.fs](../../src/Yession.Domain/OperatorProfile.fs) —
> `VolumeMaintenance`, the one thing an operator declares,
> [src/Yession.Domain/Resources.fs](../../src/Yession.Domain/Resources.fs) — the `Volume`
> leaf, which this leaves as it was

## Decision

An operator may declare, on a `volume` leaf, a **pin** and an **interval**:

```yaml
nix-container-store:
  volume:
    name: yession-nix
    at: /nix
    maintain: { pin: /nix/var/yession/pin, every: 10m }
```

A session runs `pin` inside each of its sandboxes that holds the volume — when the sandbox
starts, every `every` after that, and once more before it is removed — with one argument:
that sandbox's **lease**, a directory of its own on the volume. What `pin` writes there is
the operator's; the product never reads it.

Collecting is **not the product's**. The operator's own sweep, on the operator's own
schedule, reads the leases, keeps whichever it judges still wanted — every lease renewed
since it began, and those of ended sessions for as long as it chooses — and removes the
rest. The product ships an example pair for a Nix store; it knows nothing about Nix.

## Why

The shared volume is the point: sessions on one host are semi-trusted, and what one builds
into a warm store makes the next one fast. It is also unbounded — on the host that prompted
this, a Nix store grew ~2 GB a day with nothing ever collecting it.

Collecting it safely needs to know what every sandbox still uses, and **only the sandbox
can say**. A Nix store's roots are symlinks into the sandbox's own checkout, which no other
container can resolve, so a collector outside takes every other session's shell for a stale
root and deletes it; and the paths a running process holds open are in that sandbox's
process table alone. Every sandbox also checks out to the same path, so their roots collide
on the shared volume and at most one survives. A lease written from inside, per sandbox,
says what the sandbox uses in terms any container can read.

## What was weighed

**The product collects, knowing Nix.** A `NixStore` resource kind: the session asks nix
for roots, writes gcroots, runs the collector. Works, and puts one tool's layout into the
domain; the next shared cache (a pnpm store, a cargo registry) would need a kind of its own.

**Generations, collected by replacement.** A read-only base per generation with a private
writable layer per sandbox; retire a generation when nothing holds it. No command runs in
a sandbox and nothing is collected in place — and nothing one session builds reaches the
next, which is the whole reason the volume is shared. Rejected for that.

**Only while nothing runs.** An operator-side collector that waits until no sandbox holds
the volume. Needs nothing from the product, loses every ended session's shell each time,
and on a host with a session always open, never runs.

**Leases, operator sweeps (this).** The product learns one generic thing — run the
operator's command in the sandbox, on a clock — and keeps no opinion about what the volume
holds, how long a lease outlives its session, or when to sweep. A sweep allows for a
lease being up to `every` stale, and builds still in flight are the collector's own to
protect — for Nix, its temporary roots, which it checks by lock rather than by process id.
