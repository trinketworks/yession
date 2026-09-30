# Keeping a shared Nix store bounded

A `pin` and a `sweep` for the warm store an operator shares between sandboxes
([deployment.md](../../docs/deployment.md#keeping-the-warm-store-bounded)): the volume
mounted at `/nix` that every sandbox builds into, so the next session starts warm, and that
nothing else ever collects.

| | |
|---|---|
| [pin](pin) | Run by Yession **inside** each sandbox holding the volume, with that sandbox's lease directory. Writes every store path the sandbox holds — its roots, resolved through its own checkout, and the paths its processes have open — to `LEASE/paths`. |
| [sweep](sweep) | Run by **you**, in a container of its own. Waits one pin interval so every live lease is current, roots whatever the leases name, drops leases older than KEEP, and collects the rest. |

## Why the pin runs inside

A Nix store's indirect roots are symlinks into whoever made them: a sandbox's `result` link
lives in that sandbox's checkout, and `gcroots/auto` on the shared volume only points at it.
From any other container the target does not exist, and nix deletes a root whose target it
cannot find. Every sandbox also checks its repository out at the same path, so their roots
collide on the volume and at most one of them is there at all. And the paths a running
process holds open are only visible in that sandbox's own process table.

So only the sandbox can say what it uses. The lease is where it says it, in terms any
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

On whatever schedule suits the host — a weekly launchd agent, a cron entry:

```sh
docker run --rm -v yession-nix:/nix --tmpfs /nix/var/nix/gcroots/auto \
  nixos/nix /nix/var/yession/sweep 600 604800
```

`600` is the pin interval in seconds (the profile's `every`), and the sweep waits that long
first so every live sandbox has pinned since it began. `604800` is how long an ENDED
sandbox's lease still counts — a week, here — which is what keeps a closed session's shell
warm for the next one on the same repository. Longer keeps more warm and more on disk.

The `--tmpfs` over `gcroots/auto` is not optional, and the sweep refuses to run without it:
unmasked, nix would take every sandbox's indirect roots for stale ones and delete them, which
is exactly the loss the leases exist to prevent.

What it does not protect: a path created and rooted in a live sandbox in the moments
between that sandbox's last pin and the collection. `every` bounds that window. A build still
running is Nix's own to protect through its temporary roots.

`dotnet fsi tasks.fsx example nix-store` only checks both scripts parse. What they do is
proved by the suite: `check Docker --only "a sweep keeps"` runs two real sandboxes whose
roots collide, pins both, sweeps, and asserts both survived and the garbage did not.
