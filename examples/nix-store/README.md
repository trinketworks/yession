# Keeping a shared Nix store bounded

This example has a `pin` and a `sweep` for the warm store that an operator shares between
sandboxes ([deployment.md](../../docs/deployment.md#keeping-the-warm-store-bounded)). The warm
store is the volume mounted at `/nix`. Every sandbox builds into it, so the next session starts
warm. Nothing else ever collects it.

| | |
|---|---|
| [pin](pin) | Run by Yession **inside** each sandbox that holds the volume, with that sandbox's lease directory. Writes every store path that the sandbox holds to `LEASE/paths`: its roots, resolved through its own checkout, and the paths that its processes hold open. |
| [sweep](sweep) | Run by **you**, in a container of its own. Waits one pin interval so every live lease is current, roots whatever the leases name, drops leases older than KEEP, and collects the rest. |

## Why the pin runs inside

A Nix store's indirect roots are symlinks into whoever made them. A sandbox's `result` link
lives in that sandbox's checkout, and `gcroots/auto` on the shared volume only points at it.
From any other container the target does not exist, and nix deletes a root whose target it
cannot find. Every sandbox also uses the same path for its repository checkout. Their roots
therefore collide on the volume, and at most one of them is there at all. The paths that a
running process holds open are visible only in that sandbox's own process table.

Only the sandbox can say what it uses. The lease is where it says so, in terms that any
container can read: a list of store paths.

## Installing

Put both scripts on the volume, where every sandbox can run `pin`:

```sh
docker run --rm -v yession-nix:/nix -v "$PWD/examples/nix-store:/ex:ro" nixos/nix \
  sh -c 'mkdir -p /nix/var/yession && cp /ex/pin /ex/sweep /nix/var/yession/'
```

Then name `pin` in the resources profile, on the volume:

```yaml
nix-container-store:
  volume:
    name: yession-nix
    at: /nix
    maintain: { pin: /nix/var/yession/pin, every: 10m }
```

## Sweeping

Run the sweep on any schedule that suits the host, for example a weekly launchd agent or a
cron entry:

```sh
docker run --rm -v yession-nix:/nix --tmpfs /nix/var/nix/gcroots/auto \
  nixos/nix /nix/var/yession/sweep 600 604800
```

`600` is the pin interval in seconds (the profile's `every`). The sweep waits that long first,
so every live sandbox has pinned since the sweep began. `604800` is how long the lease of an
ENDED sandbox still counts. Here it is one week. This keeps a closed session's shell warm for
the next session on the same repository. A longer time keeps more warm and uses more disk.

The `--tmpfs` over `gcroots/auto` is not optional, and the sweep refuses to run without it.
Without it, nix takes every sandbox's indirect roots for stale ones and deletes them. This is
the loss that the leases exist to prevent.

The sweep does not protect a path that a live sandbox creates and roots in the moments between
that sandbox's last pin and the collection. `every` bounds that window. Nix protects a build
that is still running through its own temporary roots.

`dotnet fsi tasks.fsx example nix-store` only checks that both scripts parse. The suite proves
what they do: `check Docker --only "a sweep keeps"` runs two real sandboxes whose roots
collide, pins both, sweeps, and asserts that both survived and the garbage did not.
