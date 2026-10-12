# Declare what a sandbox needs, rather than fork srt to allow symlink metadata

> Decided 2026-08-28 · Supersedes the fork carried between #345 and #356 · Related:
> [GAPS.md](../GAPS.md) "Sandboxes confine by default", [yession.yaml](../../yession.yaml) `env:`,
> [package.json](../../package.json) `@anthropic-ai/sandbox-runtime`

## Context

Since #330, a resource must name the path that the kernel will check. That fixed a grant written as
`/etc/ssl/cert.pem`. It does nothing for a symlink one level further in.

On a nix-darwin host, `/private/etc/nix/nix.conf` is a link to `/etc/static/nix/nix.conf`. The path
of that target starts at the denied `/etc` node. Granting `/private/etc/nix` therefore yields a
directory that the sandbox can list and a file that it cannot read (`Operation not permitted`).

This code cannot compute a policy that resolves every link under every granted tree. The honest fix
is for srt to allow the symlink nodes on the way to a granted path.

## Decision

We depend on stock `@anthropic-ai/sandbox-runtime`. We accept that a symlink INSIDE a granted
directory stays denied. We declare what a sandbox actually needed from the unreadable file instead.
For the build of this repository, that is `NIX_CONFIG` as an env leaf in `yession.yaml`.

We reverted the fork that fixed the symlink case. We did not shelve it. It worked, and it broke
something else.

## Alternatives rejected

### A fork of srt 0.0.67 that admits `file-read-metadata` on SYMLINK vnodes

The fork does work: `nix.conf` becomes readable by both spellings, and a link to a target that
nothing grants stays denied.

It also makes `/run` traversable. On a nix-darwin host, that changes what `command -v nix` resolves
to. It moves from the working Lix at `/nix/var/nix/profiles/default/bin/nix` to
`/run/current-system/sw/bin/nix`, which aborts under Seatbelt. We measured both builds:

| Build | Reading `nix.conf` | Running `nix` |
|---|---|---|
| Stock srt | denied | works (Lix) |
| Forked srt | works, both spellings | `Abort trap: 6`, exit 134 |

The fork trades a file that nobody can read for a binary that nobody can run, on the same host. That
is not an improvement. A fork of a confinement dependency is also expensive to hold, because every
upgrade has to re-argue it.

### A patch that emits both spellings of an allow

If a patch is ever worth carrying again, prefer this shape: emit BOTH spellings of an allow, the
written form beside the canonical one. It fixes the mismatch at the paths that an operator actually
granted, instead of widening metadata host-wide. We never built it.

## Consequences

The fork was also unnecessary. The sandbox wanted two experimental-features flags from `nix.conf`.
Those flags are a fact about THIS repository's build, not about anybody's machine. They belong in
`yession.yaml`, where a repo says what its sandboxes need:

```yaml
env:
  NIX_CONFIG: "experimental-features = nix-command flakes"
```

On UNPATCHED srt, with that leaf, `nix --version`, `nix store ping` and `nix eval` all work. The
limitation stands and costs nothing here.

The argument generalises. A sandbox that cannot READ a config usually does not need the file. It
needs what the file would have said. Declaring that content is narrower than granting the file. It
also works on every host, not only on the hosts whose paths happen to be spelled the same way.

### What would change this decision

- **srt allows symlink nodes on the way to a granted path**, upstream. This is the same one-line
  widening that `/usr/bin/git` → `/var/select` wants. This ADR is then obsolete, not wrong.
- **A need that cannot be re-expressed as a declaration.** Suppose some sandbox genuinely must read
  a linked file whose content nobody can restate. The trade above is then worth re-measuring.
