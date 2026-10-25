# Yession

A local-first runtime where humans and AI agents work together in a shared **session**: a
conversation and a workspace at the same time. Humans take part, watch and step in. Agents work
through scoped capabilities inside isolated environments.

The [technical design](https://github.com/trinketworks/yession/blob/master/docs/technical-design.md)
says why Yession has its shape: the principles, the components and the invariants.

## Using Yession

Yession installs two commands: `yession-manager` (the Manager) and `yession-session` (one
Session). The Manager serves a management UI, by default at http://127.0.0.1:8321, where you
create, launch, resume and stop sessions. Each session runs in its own process.

### Install with npm

Node 24 or later is the only prerequisite.

```sh
npm i -g <release-tarball>
yession-manager
```

### Install with Nix

Nix with flakes enabled is the only prerequisite.

```sh
nix profile install github:trinketworks/yession   # install both commands
nix run github:trinketworks/yession               # run the Manager without installing
```

To pin Yession in a system, add the flake as an input and put
`yession.packages.<system>.default` in `environment.systemPackages` (NixOS) or `home.packages`
(home-manager).

### Deploy

By default, the Manager and its sessions answer on loopback and trust nobody. To reach them
from elsewhere, say where each one answers and who the humans are:

```sh
YESSION_MANAGER_URL=https://example.com          # the Manager: scheme + host, no path
YESSION_SESSION_URL=https://example.com/s/{id}   # sessions: a template over {id} / {port}

yession-manager --auth trusted-headers \
  --socket /var/lib/yession/manager.sock         # only the proxy opens it; or --auth localhost on one machine
```

[docs/deployment.md](https://github.com/trinketworks/yession/blob/master/docs/deployment.md)
has the trust rules, the header scheme and a worked Tailscale binding.

`yession-manager --version` and `yession-session --version` print the build. Every green
push to master publishes `1.0.0-beta.<n>`.

## Contributing

[devenv](https://devenv.sh), on Nix with flakes enabled, is the only prerequisite. Do not
install Node or .NET yourself.

```sh
devenv shell   # enter the environment
check          # run the cheap test tier
start          # build, then run the Manager
```

On Claude Code on the web, set the environment's setup script to `bash .claude/setup.sh`.

Read these before you change something:

- [AGENTS.md](https://github.com/trinketworks/yession/blob/master/AGENTS.md): how a change
  gets to master, the build tasks, versioning and testing. It is for people and agents.
- [technical-design.md](https://github.com/trinketworks/yession/blob/master/docs/technical-design.md):
  the principles and invariants that a change must keep.
- [tests/AGENTS.md](https://github.com/trinketworks/yession/blob/master/tests/AGENTS.md): the
  test capabilities and the rules for writing tests.
