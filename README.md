# Yession

A local-first runtime where humans and AI agents work together in a shared **session**.

A session is a conversation and a workspace at the same time. It should feel like a shared
working room, not a private chatbot thread. Humans take part, watch, and step in. Agents work
through explicit, scoped capabilities inside isolated environments.

For now you create a session by hand. Later, a session might come from a work-intake system
such as Slack or Linear. The early slices prove the fundamentals before that.

## Why it's shaped this way

A handful of constraints drive the whole design:

- **Local first.** A session runs on a local node. Clients connect to the local Session
  Process over WebRTC.
- **Reactive.** State changes through explicit transitions driven by events, with no
  shared mutable state across components.
- **Types first.** Invariants, valid states, and allowed transitions live in F# types,
  checked at compile time where possible.
- **Ylmish is the sync boundary.** Elmish owns the model. Ylmish encodes selected Elmish
  state into Yjs and back. Yjs is not the product model.
- **Durable facts are events.** Collaborative editing state lives in Yjs. Durable session
  history lives in an append-only event log that the Session owns.
- **Capabilities are scoped, not ambient.** Authority is handed out explicitly and
  composed at the application boundary.
- **Verification is automated end to end.** Manual testing doesn't count.

[docs/technical-design.md](docs/technical-design.md) has the full reasoning and the
invariants that have to survive code review.

### Components

Yession has three components. These are their names everywhere: in prose, in code, and in
tests.

- **Manager** — the `yession-manager` bin. Launches and supervises Sessions, owns identity and
  secret custody, and serves the management UI. It is long-lived. It outlives every Session it
  launches, and you upgrade it only when none is running.
- **Session** — the `yession-session` bin, one process per session. Hosts the event log, the
  Yjs document, the Elmish loop, the agent runtime, and the WebRTC protocol.
- **App** — F#/Fable in the browser. The Session serves it, and it connects back over WebRTC.

Each has a pure core of its own: `Yession.Manager`, `Yession.Session`, and `Yession.App`
(bundled from `app/browser`). `Yession.Host` (`app/`) is the Node shell that both bins run in.
`Yession.Domain` is the model that all three share. It references nothing, no package and no
project, so nothing that a library needs can bend the model. `DomainReferences` holds it there.

### Contracts

Every message that crosses between two components is declared once, as a codec that both ends
use, in the project of the component that owns it:

- **App ↔ Session: the App owns it** (`Yession.App.Codecs`). The App is the client of
  every surface the Session serves, so the App says what each exchange carries. The Session
  references `Yession.App` to answer in that shape. Both ship in the same build.
- **Manager ↔ Session: the Manager owns it** (`Yession.Manager`). The Manager moves
  slowest, because it changes only when no Session is running. The contract therefore lives on
  the side that holds it still, whichever way a message travels.
- **Shared by every surface** (identities: ids, actors, timestamps): this is cross-cutting.
  It lives in `Yession.Codecs`, beside the model and not in it.

## Getting started

[devenv](https://devenv.sh) declares the dev environment, the tasks, and the two install
channels once, in [devenv.nix](devenv.nix). nixpkgs pins them, so the environment you work in
and the package you install are built the same way. The toolchain (Node 24, .NET SDK 10) comes
from that declaration. Never install it by hand.

### Developing

**Prerequisites**

- [devenv](https://devenv.sh), and the Nix it runs on with flakes enabled. devenv provides the
  pinned Node 24 and .NET SDK 10, so you install nothing else.

**Steps**

```sh
devenv shell       # enter the environment (Node, .NET on PATH)
build              # compile everything
check              # run the cheap test tier (check Browser / Ports Native / … for more)
start              # run the Session locally
```

The tasks are devenv scripts: `restore`, `build`, `start`, `dev`, `check`, `verify`,
`package`, and `clean`. `check` runs the tests and takes capabilities as arguments, for
example `check Browser`. Each task is a thin wrapper over [`tasks.fsx`](tasks.fsx), the
complete, standalone build interface. The devenv scripts, the GitHub Actions workflows, and
the Nix outputs all call it. Without devenv and CI, `dotnet fsi tasks.fsx <verb>` still
drives everything.

[AGENTS.md](AGENTS.md) holds the contributor rules.

### Installing

Yession ships two ways, side by side. Either way installs two commands: `yession-manager`
(the Manager) and `yession-session` (a Session). `yession-manager` serves a management UI
(default http://127.0.0.1:8321). Use it to create, launch, resume, and stop sessions. Each
session runs in its own process.

#### Installing with npm

**Prerequisites**

- Node ≥24. It is the only prerequisite.

**Steps**

```sh
npm i -g <release-tarball>   # installs yession-manager + yession-session
```

On install, npm fetches the platform-native pieces through optional dependencies: the WebRTC
transport and the agent's native Claude Code binary. That one command is all it takes. To
build the tarball locally, run `devenv build outputs.npm`.

#### Installing with Nix

**Prerequisites**

- Nix, flakes enabled.

**Steps**

```sh
nix profile install github:trinketworks/yession          # add yession-manager + yession-session
nix run             github:trinketworks/yession           # run the Manager without installing
nix build           github:trinketworks/yession#yession   # just build the two wrapped bins
```

The Nix package is reproducible and self-contained. The native WebRTC addon is built from
source, the agent points at nixpkgs `claude-code`, and nothing runs an npm postinstall.
[`nix/packages.nix`](nix/packages.nix) defines the derivations. [`flake.nix`](flake.nix)
exposes them as `packages.<system>.{default,yession,npm}` without devenv, so `nix build` and
`nix profile install` are pure and need only the nixpkgs input. devenv exposes the same
derivations as `outputs.{staged,nix,npm}`.

To pin Yession in a system, add the flake as an input. Then put
`yession.packages.<system>.default` in a NixOS `environment.systemPackages` list or a
home-manager `home.packages` list.

### Deploying

By default, the Manager and its sessions answer on loopback and trust nobody. To reach them
from anywhere else, settle two things: who the humans are, and where everything answers.

```sh
YESSION_MANAGER_URL=https://example.com          # the Manager: scheme + host, no path
YESSION_SESSION_URL=https://example.com/s/{id}   # sessions: a template over {id} / {port}

yession-manager --auth trusted-headers           # or --auth localhost on one machine
```

[`docs/deployment.md`](docs/deployment.md) describes the interfaces in full: the trust rules,
the canonical `x-yession-*` header scheme, and the session template. It explains why `{id}`
keeps a session's browser storage across restarts and `{port}` cannot. A worked Tailscale
binding follows.

### Cloud sessions (Claude Code on the web)

Set the environment's **setup script** to `bash .claude/setup.sh`. The script installs
single-user Nix with flakes enabled and the container-specific fixes. It puts the `devenv` CLI
on PATH and warm-builds. The default Trusted network allowlist includes `*.nixos.org` and
`cache.nixos.org`. nixpkgs is a `nixos.org` channel tarball, not a `github:` input, so it
resolves and substitutes with no extra allowed domains.

The sandbox blocks the `github:cachix/devenv` fetch. [`.claude/setup.sh`](.claude/setup.sh)
therefore repoints the `devenv` input at devenv's own source, **substituted from
`cache.nixos.org`**. `devenv shell` then needs no GitHub access. On a laptop or in CI the
script does nothing, and devenv uses the normal `github:` input. The Bootstrap section of
[AGENTS.md](AGENTS.md) describes the mechanism.

### Testing

Tests declare what they need in code, not by folder: `Ports`, `Docker`, `LiveAgent`,
`Browser`, or nothing for a pure suite. The harness runs a suite only where the environment
meets those needs, and skips it cleanly otherwise. There are two tiers:

- `check` is the cheap tier: pure, model, and protocol suites on Node. It is fast and needs no
  ports or credentials. PRs run it. (`check Browser`, `check Ports Native`, … add tiers.)
- `verify` is the release gate: everything above plus the port-bound suites, the live
  agent, Docker, and the real-browser E2E.

Almost everything runs on Node. .NET is a build tool, and the tests exercise the same
JavaScript that the product ships. The one exception is the browser E2E. It runs on the .NET
CLR so that it can drive Chromium through Playwright against a live Session.

The Testing section of [AGENTS.md](AGENTS.md) lists every capability and the rules for writing
tests.

Dependency versions are pinned centrally: npm packages in [package.json](package.json), and
NuGet packages (including [Ylmish](Directory.Packages.props), the Elmish↔Yjs sync boundary)
in [Directory.Packages.props](Directory.Packages.props).

## Versioning

The commit history determines Yession's version. The version is never stored in a file. Master
is trunk, and every green push publishes `1.0.0-beta.<n>`. `dotnet fsi tasks.fsx version`
prints what the current commit would publish. The policy is at the top of
[tasks.fsx](tasks.fsx).

A marker in the commit message moves the major, minor, or patch number. The Versioning section
of [AGENTS.md](AGENTS.md) defines the markers, where they count, and when to use each.

Both binaries report their build: run `yession-manager --version` or
`yession-session --version`. A session also states its build on the readiness line that it
prints at startup, so the Manager can warn when it has just launched a session from a
different major version. Each process carries the version into telemetry as the OpenTelemetry
`service.version` resource attribute. A collector can then attribute token counts to the build
that produced them.

A build that cannot know a release version says so and does not invent one. It reports `dev`
for an unbundled dev run, `test` for the test tiers, and `0.0.0-g<rev>` for a Nix build, whose
source has no `.git`. Set `YESSION_VERSION` to override the computation. The Nix derivations
use it to learn what they are, and you can use it to rebuild a past release.
