# Yession

A local-first runtime where humans and AI agents work together in a shared **session**.

A session is a conversation and a workspace at the same time: a shared working room, not a
private chatbot thread. Humans take part, watch, and step in. Agents work through explicit,
scoped capabilities inside isolated environments.

For now you create a session by hand. Later, a work-intake system such as Slack or Linear might
create one. The early slices prove the fundamentals first.

## Why it's shaped this way

- **Local first.** A session runs on a local node. Clients connect to the Session over WebRTC.
- **Reactive.** Explicit, event-driven transitions change the state. No component shares
  mutable state with another.
- **Types first.** F# types hold the invariants, valid states, and allowed transitions. The
  compiler checks them where possible.
- **Ylmish is the sync boundary.** Elmish owns the model. Ylmish encodes selected state into
  Yjs and back. Yjs is not the product model.
- **Durable facts are events.** Editing state lives in Yjs. Session history lives in an
  append-only event log that the Session owns.
- **Capabilities have a scope. They are not ambient.** The application gives authority
  explicitly and composes it at its boundary.
- **Automated verification covers everything end to end.** Manual testing doesn't count.

The [technical design](https://github.com/trinketworks/yession/blob/master/docs/technical-design.md)
has the reasoning and the invariants that must survive code review.

### Components

Yession has three components. These names apply everywhere: in prose, code, and tests.

- **Manager**: the `yession-manager` bin. It launches and supervises Sessions, holds identity
  and secret custody, and serves the management UI. It outlives every Session it launches, and
  you upgrade it only when no Session runs.
- **Session**: the `yession-session` bin, one process per session. It hosts the event log, the
  Yjs document, the Elmish loop, the agent runtime, and the WebRTC protocol.
- **App**: F#/Fable in the browser. The Session serves it, and it connects back over WebRTC.

Each has its own pure core: `Yession.Manager`, `Yession.Session`, and `Yession.App` (bundled
from `app/browser`). `Yession.Host` (`app/`) is the Node shell that both bins run in.
`Yession.Domain` is the shared model. It references no package and no project, so no library
can bend the model. `DomainReferences` holds it there.

### Contracts

Each message between two components has one declaration: a codec that both ends use. It lives
in the project of the component that owns the contract:

- **App ↔ Session: the App owns it** (`Yession.App.Codecs`). The App is the client of every
  surface the Session serves, so the App says what each exchange carries. The Session
  references `Yession.App` to answer in that shape. Both ship in the same build.
- **Manager ↔ Session: the Manager owns it** (`Yession.Manager`). The Manager changes only
  when no Session runs, so it holds the contract still in both directions.
- **Shared by every surface** (ids, actors, timestamps): `Yession.Codecs`, beside the model
  and not in it.

## Getting started

[devenv](https://devenv.sh) declares the dev environment, the tasks, and the two install
channels once, in [devenv.nix](https://github.com/trinketworks/yession/blob/master/devenv.nix).
nixpkgs pins them, so one recipe builds the environment you work in and the package you
install. Never install the toolchain (Node 24, .NET SDK 10) by hand.

### Developing

**Prerequisites**

- [devenv](https://devenv.sh), and the Nix it runs on with flakes enabled. You install nothing
  else.

**Steps**

```sh
devenv shell       # enter the environment (Node, .NET on PATH)
build              # compile everything
check              # run the cheap test tier (check Browser / Ports Native / … for more)
start              # run the Session locally
```

The tasks are devenv scripts: `restore`, `build`, `start`, `dev`, `check`, `verify`,
`package`, and `clean`. Each is a thin wrapper over
[`tasks.fsx`](https://github.com/trinketworks/yession/blob/master/tasks.fsx), and
`dotnet fsi tasks.fsx <verb>` drives everything without devenv. See
[Build interface](https://github.com/trinketworks/yession/blob/master/AGENTS.md#build-interface).
[AGENTS.md](https://github.com/trinketworks/yession/blob/master/AGENTS.md) holds the
contributor rules.

### Installing

Both channels install two commands: `yession-manager` (the Manager) and `yession-session` (a
Session). `yession-manager` serves a management UI (default http://127.0.0.1:8321) to create,
launch, resume, and stop sessions. Each session runs in its own process.

#### Installing with npm

**Prerequisites**

- Node ≥24. It is the only prerequisite.

**Steps**

```sh
npm i -g <release-tarball>   # installs yession-manager + yession-session
```

npm fetches the platform-native pieces through optional dependencies: the WebRTC transport and
the agent's native Claude Code binary. To build the tarball locally, run
`devenv build outputs.npm`.

#### Installing with Nix

**Prerequisites**

- Nix, flakes enabled.

**Steps**

```sh
nix profile install github:trinketworks/yession          # add yession-manager + yession-session
nix run             github:trinketworks/yession           # run the Manager without installing
nix build           github:trinketworks/yession#yession   # just build the two wrapped bins
```

The Nix package is reproducible and self-contained. Nix builds the native WebRTC addon from
source, the agent uses nixpkgs `claude-code`, and nothing runs an npm postinstall.
[`nix/packages.nix`](https://github.com/trinketworks/yession/blob/master/nix/packages.nix)
defines the derivations.
[`flake.nix`](https://github.com/trinketworks/yession/blob/master/flake.nix) exposes them as
`packages.<system>.{default,yession,npm}`, so `nix build` and `nix profile install` are pure
and need only the nixpkgs input. devenv exposes them as `outputs.{staged,nix,npm}`.

To pin Yession in a system, add the flake as an input and put
`yession.packages.<system>.default` in NixOS `environment.systemPackages` or home-manager
`home.packages`.

### Deploying

By default, the Manager and its sessions answer on loopback and trust nobody. To reach them
from elsewhere, set who the humans are and where everything answers:

```sh
YESSION_MANAGER_URL=https://example.com          # the Manager: scheme + host, no path
YESSION_SESSION_URL=https://example.com/s/{id}   # sessions: a template over {id} / {port}

yession-manager --auth trusted-headers           # or --auth localhost on one machine
```

[`docs/deployment.md`](https://github.com/trinketworks/yession/blob/master/docs/deployment.md)
has the trust rules, the `x-yession-*` header scheme, why to prefer `{id}` over `{port}`, and
a worked Tailscale binding.

### Cloud sessions (Claude Code on the web)

Set the environment's **setup script** to `bash .claude/setup.sh`. It installs single-user Nix
with flakes and the container fixes, puts the `devenv` CLI on PATH, and warm-builds. The default Trusted network
allowlist includes `*.nixos.org` and `cache.nixos.org`. nixpkgs is a `nixos.org` channel
tarball, not a `github:` input, so it needs no extra allowed domains. For how `devenv shell`
avoids GitHub, see
[Bootstrap](https://github.com/trinketworks/yession/blob/master/AGENTS.md#bootstrap).

### Testing

Tests declare what they need in code, not by folder (`Ports`, `Docker`, `LiveAgent`,
`Browser`, or nothing for a pure suite). Where the environment does not meet those needs,
the harness skips the suite cleanly. There are two tiers:

- `check` is the cheap tier: pure, model, and protocol suites on Node, with no ports or
  credentials. PRs run it. (`check Browser`, `check Ports Native`, … add tiers.)
- `verify` is the release gate: everything above plus the port-bound suites, the live agent,
  Docker, and the real-browser E2E.

.NET is a build tool. The tests run the same JavaScript the product ships, on Node, except the
browser E2E, which drives Chromium through Playwright on the .NET CLR.
[Testing](https://github.com/trinketworks/yession/blob/master/AGENTS.md#testing) in AGENTS.md
lists the `check` tiers.
[tests/AGENTS.md](https://github.com/trinketworks/yession/blob/master/tests/AGENTS.md) holds
the capability notes and the rules for writing tests.

Central files pin dependency versions: npm packages in
[package.json](https://github.com/trinketworks/yession/blob/master/package.json), NuGet
packages (Ylmish, the Elmish↔Yjs sync boundary, among them) in
[Directory.Packages.props](https://github.com/trinketworks/yession/blob/master/Directory.Packages.props).

## Versioning

The commit history determines the version. No file stores it. Master is trunk, and every green
push publishes `1.0.0-beta.<n>`. `dotnet fsi tasks.fsx version` prints what the current commit
would publish. The policy is at the top of
[tasks.fsx](https://github.com/trinketworks/yession/blob/master/tasks.fsx). A marker in the
commit message moves the major, minor, or patch number:
[Versioning](https://github.com/trinketworks/yession/blob/master/AGENTS.md#versioning) in
AGENTS.md defines them.

`yession-manager --version` and `yession-session --version` report the build. A session also
states it on its startup readiness line, so the Manager can warn when it launched a session
from a different major version. Each process sends the version to telemetry as the
OpenTelemetry `service.version` attribute, so a collector can attribute token counts to a
build. A build with no release version reports `dev`, `test`, or `0.0.0-g<rev>` (see
[app/Version.fs](https://github.com/trinketworks/yession/blob/master/app/Version.fs)). Set
`YESSION_VERSION` to override the computation. The Nix derivations use it to learn their
version, and you can use it to rebuild a past release.
