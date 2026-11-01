# Yession — Design & System Fundamentals

This document states *why* the Yession runtime has its shape. For the product framing, see
the [root README](../README.md).

---

## 1. Core principles

These are architectural constraints, not preferences. They are for everybody who changes this
repository, a person or an agent. Read them before a change that adds a component, a
dependency, an option, a test or a rule. A reviewer cites one by name.

When a change has to break a principle, change this section first, in the same PR, and say
why. A principle that the code no longer follows misleads the next reader.

**How to build anything here:**

- **[Fast feedback](#fast-feedback)**: the cheapest signal that can catch a fault catches it,
  without a manual step.
- **[Colocation](#colocation)**: a rule lives with the state that it governs.
- **[One mechanism per requirement](#one-mechanism-per-requirement)**: keep only the
  mechanism that is verified to work, and remove its spare.
- **[Policy/mechanism separation](#policymechanism-separation)**: a mechanism gives an
  ability, and a policy decides its use.
- **[Acyclic dependencies](#acyclic-dependencies)**: a component refers only to the
  components before it.
- **[Composition at the top](#composition-at-the-top)**: one root composes the concrete
  infrastructure, and decides nothing.
- **[Types first](#types-first)**: the type system specifies correctness.
- **[High-signal automated verification](#high-signal-automated-verification)**: a test pins
  one invariant, and no manual run takes the place of one.
- **[Fix upstream, guard downstream](#fix-upstream-guard-downstream)**: a bug fix stops the
  cause, and a guard stops the next fault at the place where the damage shows.

**What the runtime is:**

- **[Local first](#local-first)**: a session runs on a local node, and its clients reach it
  over WebRTC.
- **[Reactive](#reactive)**: events drive explicit state transitions, and each view is a
  projection.
- **[Ylmish is the sync boundary](#ylmish-is-the-sync-boundary)**: Elmish owns the model, and
  Ylmish encodes it into Yjs (§5 names one exception).
- **[Durable facts are events](#durable-facts-are-events)**: an append-only log holds what
  happened, and only the Session writes it.

[§5](#5-architectural-invariants) lists the invariants that follow from these principles.

### Fast feedback

A change gets to a signal without a manual step. The cheapest signal that can catch a fault is
the one that catches it. For example, `dev` runs the Manager under `fable watch`,
`check --only "<text>"` runs only the cases whose name contains the text, and
`frames --boot --still` takes a picture of a surface.

- Put a rule where the cheap tier reaches it. A rule that only the composition root or the
  browser tier can see is not checked between runs of that tier ([Colocation](#colocation)).
- Tag a suite with the minimum capabilities that it needs, so that it runs in the cheapest
  tier that can run it ([tests/AGENTS.md](../tests/AGENTS.md#writing-tests)).
- Give time and entropy to the code as ports, for example `Resilience.Policy` and
  `LinkPolicy`. A test then drives a backoff or a timeout in zero real time. A test waits for
  a condition, never for a duration (YES014).

### Colocation

What belongs to one piece of state is in one place: its type, the rules that guard it, the
operations on it, and the tests that pin them. An invariant that holds only because a caller
remembered to ask first is not an invariant. The next caller has not read it.

The tell is a member exported for no local reason. `SessionTerminals` once exported an
`Instrumented` predicate that only `Host.fs` used, to subtract a line window and cap the
answer. No cheap test saw that arithmetic, and a bug lived in it.

Ask three questions. Any `no` means the rule is in the wrong place:

- **Can a caller break it by not calling something first?** Then those calls are one verb.
- **Would testing it mean building the composition root?** Then move it down to where the
  state is. A rule the cheap tier cannot reach is a rule nobody re-checks.
- **Does a sibling operation on the same state live somewhere else?** Then one of them has
  moved.

A seam belongs where it is used. Pass state down. Do not reach up for it.

The same applies to reasons. The reason for a piece of code is in its doc comment, and this
document points to it. Write no plan document. Put what is worth keeping next to the code
that it governs, where a test can contradict it. For example, the header of `src/Yession.Domain/Config.fs` holds the
design of `yession.yaml` and the rule for each future key.

### One mechanism per requirement

Two mechanisms can meet the same requirement, for example two config locations, or a fallback
beside a primary. Keep only the one that is verified to work here. Remove the other. A spare
hides which path is live, rots unverified, and turns the next failure into an archaeology dig.

### Policy/mechanism separation

A mechanism gives an ability. A policy decides when and how to use it. Keep them apart. Then a
policy is a value that a cheap test reads without the mechanism, and a new policy costs a
declaration and not a change to the mechanism.

- `Policy.guard` in `Yession.Domain/Resilience.fs` is the mechanism of a retry. A `Schedule`
  and a `Verdict` are its policy: values that compose, print and test without an operation.
- A Session has the mechanism to spawn and confine sandboxes (`CreateSandbox`,
  `SandboxBackend`). The operator's declarations are the policy: what a session may reach,
  which connections and which MCP servers ([§3](#3-authority-model)). A
  `yession.yaml` narrows that ceiling and never makes it wider
  ([§4](#4-repo-local-configuration)).

Do not add an option to a mechanism for a choice that its caller can make. Let the caller give
the mechanism a value.

### Acyclic dependencies

A component refers only to the components before it. F# enforces the order. In a project, a
file sees only the files above it in the `.fsproj`. Between projects, a `ProjectReference`
refers back. The order is:

```text
Fable.*            binding projects: APIs that we do not own
Yession.Domain     types, rules and pure transitions. It refers to nothing.
Yession.Codecs     the wire formats of the domain
Yession.App        the client: the Elmish model, Ylmish, the views
Yession.Session    one session. It serves the App.
Yession.Manager    the Manager's pure state and wire layer
Yession.Oidc       the Manager's OIDC provider
app/               Yession.Host, the composition root
Yession.Peer       the browser's client, driven headlessly for tests and probes
```

When a component needs something from a component after it, the earlier component declares a
function-shaped port, and the composition root gives it the implementation. Pass state down. Do
not reach up for it ([Colocation](#colocation)).

### Composition at the top

Capabilities are small and function-shaped. The application boundary composes concrete
infrastructure. Concrete infrastructure does not spread through domain code.

The composition root composes and decides nothing. A bound, a fallback or a refusal that the
root computes is a decision that no cheap test reaches ([Colocation](#colocation)).

### Types first

The type system specifies correctness. F# types encode core invariants, valid states, and
allowed transitions. Where possible, the check happens at compile time. Yjs is not the
product model. JSON blobs, Y maps, and stringly-typed payloads remain boundary formats.
They must not leak into application logic.

**Narrow the type, and let the compiler find the sites.** Change a field's type, a
parameter's type or a union's cases, and nothing else. Every place that no longer fits is a
compile error. Fix each site with the value it already had, never with a default. A site with
nothing of the right type to give is the finding: give it a case, a constructor or a refusal,
not `Option.defaultValue`. Keep no bridging alias, second constructor or `option` "for now".
A test that pinned the old shape is not a regression guard when the shape was the bug.

`MessageSent.Author` stayed `ActorRef` because it had sixty-five sites, and the turn's
principal rode beside the message, consistent by discipline and not by type. Narrowing it took
one edit and forty minutes of compiler errors. The sites were never the cost. The aliasing was.

**Do not open the type system again.** Each construct below reopens the hole that Fable
closed. Do not add one, and remove the ones in code that you change. Where an analyzer reports
one, the doc comment of its module in `analyzers/Yession.Analyzers/` says why.

- A JavaScript program in a string: an `[<Emit>]` outside `src/Fable.*` (YES011), an inline
  `<script>` body, a CDP `Runtime.evaluate` source, or a Playwright `EvaluateAsync` program
  (YES012 holds the suite's few fixed ones to `PageScript`). In the browser suite, write a
  probe in `Yession.App.Probes`.
- Dynamic access with `?` and `?<-` (YES015). Bind the module. Key the map with a type.
- `unbox`, `!!` or `:?>` on a value that you did not just construct (YES016). Decode it, or
  type what produced it. Narrow an event or its target with `Events` and `EventTargets`
  (`Fable.BrowserExtras`).
- `obj` in the signature of a function or a member (YES017).
- `createObj`, `==>` or `[<AllowNullLiteral>]` outside a binding project (YES013).
- A decode that cannot refuse: `Option.defaultValue ""` over a field read (YES009). Answer
  `None`, or say in the code why the default is the right answer.

The escape is one of three: a **binding project** (an API that we do not own), a **codec** (a
wire), or a **typed options interface** (a bag). Where none applies, keep the cast on the line
that needs it, mark that line for its rule ([analyzers/AGENTS.md](../analyzers/AGENTS.md),
"A justified exception"), and give the result a type at once. Never carry `obj` onward.

### High-signal automated verification

Each implementation phase must include automated end-to-end tests. Manual testing is not
a substitute for verification. There is no "one-shot" manual validation.

A test pins one invariant and goes red only when that invariant breaks. The rules for
writing tests are in [tests/AGENTS.md](../tests/AGENTS.md#writing-tests).

### Fix upstream, guard downstream

Fix the root cause upstream, and harden against the symptom downstream. Do both. The upstream
fix stops this fault. The downstream guard stops the next, different fault from becoming a
plausible wrong answer at the same place. This is one mechanism at each of two points, not two
mechanisms for one requirement ([One mechanism per requirement](#one-mechanism-per-requirement)).

For example, `echo "version=$(...)"` discarded the exit status of a failed
`devenv shell -- version`. The step went green with an empty version, and the release job
tagged the commit `v`. That tag matched `--match 'v*'` and restarted the 6.13.1 line at 1.0.0.

- Upstream: capture under `pipefail`, and refuse an empty result.
- Downstream: `--match 'v[0-9]*'`, and refuse a nearest tag of the wrong shape.

The tell that you did half: the fault reads as a chain (X failed, so Y saw nothing, so Z
published garbage), and your change touches one link. Ask what the other links do when X is
different.

### Local first

A session runs on a local node. The first implementation assumes one node. The design defers
central discovery, placement, and remote directories.

Clients connect to the local Session over WebRTC. HTTP may serve static app bootstrap and
signalling, but it is **not** the main session API.

### Reactive

Events drive the system as explicit state transitions. Components share no mutable state.
Transitions should be deterministic and composable. UI, collaboration, agent progress, and
event consumption are projections of state. Explicit boundaries isolate side effects.

### Ylmish is the sync boundary

Elmish owns the model and update loop. Ylmish encodes selected Elmish state into Yjs and
decodes synced Yjs state back into Elmish state.

```text
Elmish Model / Msg / update
  -> Ylmish codec
  -> Yjs document
  -> WebRTC sync
  -> Ylmish codec
  -> Elmish Model
```

### Durable facts are events

Collaborative editing state belongs in Yjs. Durable session history belongs in a
Session-owned event log.

```text
Yjs        = collaborative editable state
Event log  = durable facts
Client offset = read progress through the event log
```

The Session is the only event writer.

---

## 2. System overview

### 2.1 Runtime components

Yession has three components, and these names apply everywhere: in prose, code and tests. The
Manager is the `yession-manager` bin. It outlives every Session that it launches, and you
upgrade it only when no Session runs. A Session is the `yession-session` bin, one process for
each session. The App is F#/Fable in the browser. The Session serves it, and it connects back
over WebRTC.

Each has its own pure core: `Yession.Manager`, `Yession.Session` and `Yession.App`.
`Yession.Host` (`app/`) is the Node shell that both bins run in.

```text
Session
  - owns one session
  - hosts the web app bootstrap
  - owns the append-only event log
  - owns/hosts the Yjs document
  - runs the Elmish model/update loop
  - runs the agent runtime
  - exposes a multiplexed WebRTC protocol to clients
  - later receives delegated capabilities from the Manager

App
  - runs the Elmish model/update loop
  - connects over WebRTC
  - edits collaborative state through Ylmish/Yjs
  - consumes read-only event pages by offset
  - renders projections from events and synced state

Manager
  - launches Sessions
  - owns launch, identity, and secret custody
  - declares what a session may reach, and holds no environment authority itself
```

### 2.2 State split

```text
Elmish model        Product state and transitions.
Ylmish/Yjs          Encoding and synchronization of selected collaborative state.
Event log           Append-only durable session facts owned by the Session.
Client event offset Client-side read position through the event log.
```

### 2.3 Transport

The session transport is WebRTC. The connection multiplexes:

```text
collaboration sync frames (opaque payload)
event-log page requests/responses
session commands
control/presence frames
agent progress notifications (hints only)
```

The design allows HTTP only for the web app, initial local bootstrap, and temporary
signalling. **HTTP is not the session API.**

One read-only exception: HTTP serves the durable HISTORY by CURSOR, the event log and each
terminal's transcript on identical terms. A client sends the position it folded through
(`GET /events/after/{n}`, `GET /terminals/{t}/after/{n}`, or the bare path from the
beginning). The server redirects to the range it chose (`GET /events/{first}-{last}`,
`GET /terminals/{t}/{first}-{last}`), or answers `204` when that client is already current.
The bounds of a range do not move, so its bytes never change, the growing tail included. That
makes an answer worth keeping. `app/Signalling.fs` states the cursor and range contract.

The client keeps the answers in the Cache API (a store it can enumerate and ask to persist):
one store for the session's events and one for each terminal. Every response on this surface
is `no-store`, because the copy that matters is the client's. A second copy in the HTTP cache
would be a spare nobody reads. The client computes no address. It stores each answer under the
address it received, and takes where an answer starts from what it ASKED: the answer to
`after n` starts at `n + 1`. `TranscriptCache` in `src/Yession.App/Client.fs` says why
transcripts depend on that server contract, which has a test of its own.

The history feed is therefore a second leg that can fail independently, and the design treats
it as one:

- A read is `EventOffset option -> Async<Result<EventPage, FeedFault>>`. A dead feed is
  distinguishable from an empty one. An empty page never stands in for a failure.
- The feed's health is model state, part of the page's link (`SessionLink`). A read that fails
  is a fact about the fetch, and reads that gave up are `Stalled`. The UI shows it on the status
  line. A stalled feed disables nothing: collaborative state is CRDT state in the local doc, so
  reading, writing, and sending continue. A lost history feed costs history and nothing else.

The session leg can also fail independently, and silently: a WebRTC data channel can keep
`readyState` `open` while sends go into nothing. `Link.supervise` (`Yession.Domain/Link.fs`)
wraps every channel that carries a session before anything else holds it. It probes a quiet
link once a second and closes it after three quiet ticks, so death has one expression, a
close, which both pumps already handle. Supervision is symmetric, which is also how a
silently-dead peer stops holding a terminal lease. `LinkPolicy` takes its clock as a port, as
`Resilience.Policy` does. The header of `Link.fs` and the
[ADR](decisions/2026-08-20-session-link-liveness.md) hold the reasoning.

### 2.4 Contracts

Each message between two components has one declaration: a codec that both ends use. It lives
in the project of the component that owns the contract.

- **App ↔ Session: the App owns it** (`Yession.App.Codecs`). The App is the client of every
  surface the Session serves, so the App says what each exchange carries. The Session
  references `Yession.App` to answer in that shape. Both ship in the same build.
- **Manager ↔ Session: the Manager owns it** (`Yession.Manager`). The Manager changes only
  when no Session runs, so it holds the contract still in both directions. A breaking change
  to it is a major version bump.
- **Shared by every surface** (ids, actors, timestamps): `Yession.Codecs`, beside the model
  and not in it.

---

## 3. Authority model

The Manager owns launch, identity, and secret custody, but not environment authority. A
Session spawns its own sandboxes through the `CreateSandbox` seam and confines them, the
agent's own CLI included, with the `SandboxBackend` it boots with (`Yession.Domain/Sandbox.fs`).
A Session cannot mint secrets. They cross from the Manager only at sandbox spawn, over the
authenticated control channel.

```text
Manager               owns launch, identity, and secret custody.
Session               owns orchestration. Spawns and confines its own sandboxes.
Session Environment   a sandbox associated with exactly one SessionId. Started lazily.
```

Environments start **lazily**, only when the agent determines work requires one. A
one-shot conversational answer must not start a sandbox.

### Threat model (Phases 1–2)

```text
Protect against LAN snooping in the client/session transport.
Manager may read plaintext.
Process may read plaintext.
Command-to-container encryption is designed for but not implemented yet.
```

The Manager authorizes user access to a session as an OIDC provider (authorization code +
PKCE). Each Session registers as a client with its per-launch control secret. The `--auth`
argument selects one of three authentication strategies at Manager start: `none` (the
default), `localhost` or `trusted-headers`. [deployment.md](deployment.md#authorizing) says
what each one does. `localhost` matches this threat model. Under `trusted-headers`, the
operator's authenticating proxy must be the only non-local path in, and it must strip the
`x-yession-*` headers from client requests. Further schemes are strategy swaps, not
redesigns. Under `trusted-headers` the Manager authorizes every attributed user to every
session: identity is per human, but a session has no owner
([GAPS.md](GAPS.md#security--trust)).

Secrets are Manager-owned authority:

- A key store the Manager never reads as a file holds the key that encrypts secrets at
  rest: the OS credential manager, or a helper the operator names. Without one, nothing
  persists.
- A pure default-deny policy authorizes access. It reads the composite identity that the
  Manager verified itself: the calling session plus the users bound to its launch at
  ID-token issuance.
- The store keeps two families apart by type: generic secrets (`SecretId`) and connection
  credentials (`ConnectionId`). Each family's actions take only its own identity, so no
  generic action (set, delete, list, inject) can name a connection credential
  ([ADR](decisions/2026-10-26-secrets-and-connections-are-two-families.md)).
- Sessions hold pre-scoped write, list, and delete capabilities on generic secrets. They have
  no agent-facing read.
- Values leave the store through exactly two resolve-shaped routes. The caller's readable
  scopes gate each route.
  - `/control/secrets/resolve` feeds `SecretRef` env injection at the session's own sandbox
    spawn. Sandboxes are session-owned, so the injection point is there and not in the
    Manager. A repository's file writes the reference, so it reads the session's own scope
    and nothing else: not a bound user's scope, not `LocalScope`, and not the Manager's
    process environment.
  - `/control/connections/resolve` releases a connection credential for one agent turn.
- Refresh tokens never leave the Manager. No agent tool wraps either route.

That refresh runs under a resilience policy (`grantPolicy` and `Broker.resilient` in
`app/Broker.fs`, composed in `ProcessManager`), whose comments say why. A provider that calls
the grant dead still stops the refresh at once. That answer differs from a failed one.

MCP servers are Manager-owned authority in the same way. A declaration in `ManagerState`
names a url and who reaches it, and every session it names connects itself over the reverse
control leg. The Manager never becomes an MCP client. Neither an agent nor a peer can name a
url: what a session talks to is the operator's decision.
A session admits a stream that a tool result offers only on the host the operator already
declared ([streams.md](streams.md#admission)). The tool descriptions that come back are the
one untrusted text the model must read. See [GAPS.md](GAPS.md).

---

## 4. Repo-local configuration

A checkout may carry a `yession.yaml` at its root. A session folds every such file it holds
into the commands it already has (Plan 27). The name has no leading dot, matching the other
files a repo means people to read.

```yaml
version: 2
sandboxes:
  app:
    container:
      image: node:24
      entrypoint: nix develop --command   # compose's word: what every piece of WORK runs behind
      command: ./serve                    # compose's word for the container's own process
      mem_limit: 4g                       # optional: its memory ceiling, else 3/4 of the daemon's
    dialect: bash                         # optional: the shell a terminal opens, else detected
    workdir: ./packages/web
    env:
      DATABASE_URL: { secret: db-url }   # a name, never a value: the session's own secret (set_secret)
      PATH: ${env.PATH}:/opt/tools       # composed over what lies beneath; `$${` is a literal `${`
    uses: [ npm-cache ]
    wants: [ github ]                     # the operator's `{ connection: { github: [git, api] } }`, where offered
```

The `container:` block is a compose service fragment: compose's keys, with compose's
meanings, so a block copied from a compose file means the same thing here. `docker compose
config` can lint it once the file it is pasted into declares `workspace` under a top-level
`volumes:`, because compose refuses a volume source that no top-level entry declares. The
decoder refuses, by name, each key that it does not take, and says what this file offers
instead.

### Connections and references

A connection is a resource like any other. It lets `git push` and `gh` in a sandbox act as
whoever each command runs for. The operator offers it (`github: { connection: { github: [git,
api] } }`), and a sandbox selects it under `uses` or `wants`. The sandbox forwards what that
selection reaches (`ForwardedConnections`, `WorkSandboxes.provisionSelection`). The operator
names the ROUTES that forward it, each its own grant (`conn:github/git`): `git` is the git
gateway and `api` is the credential proxy (`ConnectionRoute`). The session refuses a sandbox
that needs a route the provider does not offer.

`${env.NAME}` in a value is what NAME would be in this sandbox without that line (under
docker, the image's own `ENV`). The backend resolves it (`Sandboxes.environment`).
`EnvTemplate.fs` holds the grammar.

`${proxy.https}`, `${proxy.ca-file}` and `${proxy.ca-dir}` ask the session's credential proxy
for its URL (this sandbox's own, which admits it), a whole trust bundle, and a directory that
holds its authority alone, each as the sandbox sees it (`CredentialProxy.provide`). The proxy
provides values only for a sandbox that names one. They compose like any value:

```yaml
    env:
      HTTPS_PROXY: ${proxy.https}
      SSL_CERT_DIR: ${env.SSL_CERT_DIR}:${proxy.ca-dir}
```

An srt sandbox (the session's `default`) takes a connection by `git` alone and never gets
its `api` route. `CredentialProxy.forwardApi` says why. So under srt:

- A `uses:` of an `api` route refuses the start.
- A `wants:` goes without that route.
- A `${<connection>.token}` or `${proxy.…}` named there refuses, as it does wherever nothing
  forwards that connection by `api`.

`${github.token}` (any connection's `.token`) is a variable's whole value and nothing else: a
stand-in that the credential proxy swaps for the credential of whoever each command runs for,
lent per command (`EnvironmentVariableRef.Lent`). The sandbox must forward that connection by
`api`, or it refuses to start. Whether the rotation is worth it is open in
[GAPS.md](GAPS.md#security--trust).

`${session.version}` is the build of Yession that runs this session, as `--version` and the
UI's footer show it. The session answers it itself (`WorkSandboxes.answerSession`), so it
needs nothing selected and works under every backend. Nothing sets it unasked. An operator
who wants the agent to know it threads it through a resource:

```yaml
yession-version:
  env:
    YESSION_SESSION_VERSION: ${session.version}
```

An operator may bind the same references in a resource's `env:`, for every sandbox that
selects it (`VariableValue`, `SelectionGrant`):

```yaml
resources:
  github:
    connection: { github: [ git, api ] }
    env:
      HTTPS_PROXY: ${proxy.https}
      GH_TOKEN: ${github.token}
```

A repo's line for the same variable wins. The session supplies them as it does a repo's, and
refuses, where it reads the file, a resource that lends a token it does not itself forward by
`api`.

### Entrypoint and shell

The session reads a container's `entrypoint` as compose does: a list of words, or one string
split as a shell would split it, with nothing expanded. The container's own `command` and
every piece of **work** (a terminal's shell, a block, `setup:`) run behind it. The session's
housekeeping (start-up checks, `git` by path, the probe behind `set_shell_profile`) runs bare,
as `docker exec` does. `ProcessEntry` in `src/Yession.Domain/Sandbox.fs` says why.

The backend finds the shell that a terminal opens. At start it runs one `command -v` behind
the entrypoint for the dialects a terminal can instrument, most capable first (`bash`, `zsh`,
`sh`), and a terminal opens the first one found. This also warms a devshell, once, off the
first terminal. `dialect:` names a dialect instead. A container without that dialect refuses
to start, and names what the backend looked for.

### Container limits

All containers on a daemon (on a Mac, one Colima VM) share its memory, so one session's build
can make the global OOM killer take another session's work. Every container therefore gets
four limits. The doc comment of each type in `src/Yession.Domain/Environment.fs` gives its
reasoning.

The operator profile's `container:` block (`ContainerCeiling`) is a ceiling on the repo's
limits, in the same spelling. A repo may name less, never more. A value above the ceiling
refuses the sandbox, with a sentence that names both values and the file that owns each. It
is never lowered in silence. A repo that names nothing gets the operator's value
(`ContainerSpec.forDaemon`).

| key | who writes it | when nothing is declared | what it does |
|---|---|---|---|
| `mem_limit:` (`ContainerMemory`) | repo, under the operator's ceiling | the operator's ceiling, else three quarters of what the daemon reports | The cgroup's `memory.max`, no swap beyond it, in compose's grammar (`4g`). The kernel kills inside the container that overran. A value above the daemon's total refuses the sandbox. An image build runs under the same ceiling, with no swap (`ContainerLimits.buildMemory`). |
| `oom_score_adj:` (`OomScoreAdj`) | repo | 500 | From 0 to 1000. Work (a terminal's shell, a block, `setup:`) sets it on itself before it starts. The container, its init, its main process and the session's housekeeping execs stay at 0. The daemon's processes are below zero, so the global killer takes a sandbox process first. Inside the container, an overrun's kills take work, never the processes that keep the container up. |
| `pids_limit:` (`PidsLimit`) | repo, under the operator's ceiling | the operator's ceiling, else 4096 | A fork bomb uses up its own container and not the host. |
| `cgroup_parent:` (`CgroupParent`) | operator only | the daemon's own parent | Puts the container under a pool that the host's operator limits below the VM's total, so an OOM stays inside it. A repo that writes it is refused. |

Containers that each stay under their ceiling can still add up to more than the VM has. The
OOM score and the parent cgroup cover that case.

Write `cgroup_parent:` as a bare name (`yession`), so that daemons of both cgroup drivers can
read one profile. The backend spells it for the driver (`CgroupParent.forDriver`). Under
systemd the backend refuses a path. `CgroupParent` lists the spellings.

Yession does not make the parent. Docker (cgroupfs) or systemd makes a missing one with no
limit, and an OOM can again reach the daemon. [GAPS.md](GAPS.md#agent) ("A `cgroup_parent` is
the operator's to name, and the host's to make") says how this repo's host makes it.

### Scope of the file

The file has three top-level keys, and one is a version. **The file is almost all
sandboxes.** A sandbox name is scoped to its repo, so the union of the files is disjoint: no
precedence rule, nothing shadows anything. The session refuses a clash inside one file, where
its author wrote it.

The one session-wide key is `session.shell-profile`: where the `default` sandbox's terminals
start, relative to the checkout. It applies only while exactly one repo declares it, and no
file moves a profile that a person or the agent set (`ConfiguredProfile` in
`src/Yession.Domain/ShellProfile.fs`). Anything else session-wide stays the operator's (see
[GAPS.md](GAPS.md)). The header of `src/Yession.Domain/Config.fs` holds the design and the
rule for every future key.

Three properties make the file safe to read from code a session just cloned:

- **It is a fold, not an executor.** A declaration is one ensure-shaped `start_work_sandbox`
  through the same gate as the agent's. Asking twice changes nothing and records nothing, so
  the fold can re-run at boot and after every verb that changes a checkout.
- **The operator's environment is a ceiling, not a default.** Narrowing takes effect at once.
  The session refuses an ask that exceeds the ceiling and names what exceeded it. A file that
  says nothing gets the operator's list whole.
- **Whoever can push to the repo authors the file**, and that person is neither the operator
  nor anybody in the session. So the file's acts are `ActorRef.Configured`, and it owns no
  credential. The schema refuses what a repo may not name: the whole `YESSION_` prefix in
  `env:`, a host path as a volume source, a `workdir` outside the checkout, and a
  `cgroup_parent`. So the session
  reads the file from the checkout's `HEAD`, out of git's object store, and never from the
  working tree. An edit in the working tree declares nothing until somebody commits it. A
  `yession.yaml` that `HEAD` commits as a symlink is no file (rule 20).

This repository's own `yession.yaml` is the schema's acceptance test: what yession needs to
maintain yession measures whether the file can say anything real.

---

## 5. Architectural invariants

These must be visible in code review. They matter more than polish in the first phases. Each
follows from a principle in [§1](#1-core-principles). A reviewer cites a rule by its number.

Update this list before a change that breaks a rule, in the same PR, and say why.

1. Elmish is the product model.
2. Ylmish is the encoding/sync boundary.
3. Yjs is not the domain model.
4. WebRTC is the session transport.
5. HTTP is bootstrap/signalling only. [§2.3](#23-transport) names its one read-only exception,
   the history by cursor.
6. The Session is the only event writer.
7. Clients consume events read-only by offset.
8. Drafts are collaborative state.
9. Sent messages are durable events.
10. Conversation is a projection.
11. Manager owns authority.
12. Session owns orchestration.
13. Environment starts lazily.
14. Capabilities are scoped, not ambient.
15. Verification is automated end-to-end, not manual.
16. A project refers only to the projects before it in the order of
    [Acyclic dependencies](#acyclic-dependencies). `Yession.Domain` refers to no other project
    and no package but FSharp.Core (`DomainReferences` in `tests/` pins it).
17. The composition root composes and decides nothing.
18. Nothing on a TCP port believes its peer. An identity arrives through a door only its
    asserter can open, a unix socket, and a listener whose strategy reads headers refuses a
    port (`Listener.admits`). A container may reach the host loopback (a VM daemon gives it
    no other way to the host), so everything there authenticates by capability, cookie or
    token.
19. A `SecretRef` resolves only against what a principal gave the session on purpose: the
    session's own generic secrets. It never reads another principal's scope, the Manager's
    process environment, or a connection credential. The last is a type, not a check: the
    resolver holds only the store's generic partition (`SecretStore.SecretResolution`).
20. A repository's file reaches the session only through git's object store, read in the git
    sandbox (`ReposService.ReadCommitted`). The session process opens no path that a
    repository names. A work sandbox can write the checkout, so a path in it can be a symlink
    to anything the session's user can read, and a check before the open races the write.
21. No git process holds a GitHub credential. git reaches github.com only through the git
    gateway (`GitGateway`), which adds the credential of a loan on the way. A work sandbox's
    git and the session's own clone and fetch take the same route.
22. A string the agent supplied reaches a program only as an operand, never as an option. It
    stands after `--` or as an option's own argument, and a path operand is a `PathOperand`,
    which starts with `/` or `.`. A read-only tool never meets the gate, so this is what keeps
    `find_files` from deleting with `-delete` (`ScriptArg`).
23. A string from outside the process reaches HTML only through `Ssr`'s escapers, and the
    hole's context in the template chooses the escaper (`Ssr`'s scanner). A display name, an
    MCP server's name and a refusal's words are all from outside. The Manager page parses the
    fragments it is sent inert, so the escaping is the one door.
24. A Manager route that changes state accepts only a request whose `Sec-Fetch-Site` or
    `Origin` names the Manager's own origin (`ManagerUi.requestOrigin`). A request with
    neither is no browser's, and passes on its identity alone. A `GET` changes no state, so
    `/sessions/{id}/open` answers a stopped session with a screen that asks for the launch.
25. A grant is decided by the person it belongs to. A repo's sandbox is lent a person's GitHub
    only by the routes their own trust in that repo grants, read from their record for each
    block, and only from their own connection (`RepoTrust.granted`, `GrantedRoutes`). A repo
    runs here with the GitHub of a person whose own act offered their trust (`RepoTrusted`).
    Joining a session never does. A volume or a socket reaches other sessions and the host, so
    the operator decides it in the profile, and a volume states its mode there. The session's
    user is not asked about it.
26. A repository's container limits narrow the operator's, and only the operator names a
    cgroup (`ContainerSpec.forDaemon`, `ContainerCeiling`).

Rule 2 has one named exception. Message bodies and terminal command lines are top-level Yjs
roots, keyed by `BodyKey`, that the app co-manages outside the encoded tree, and the structural
reader skips them. The header of
`src/Yession.App/Collab/Bodies.fs` says why. Everything else crosses through the codec.

---

## 6. Cross-cutting type vocabulary

`Yession.Domain/Identity.fs` declares the identity and envelope types that nearly every step
shares. The actor glossary:

- **User** (`UserId`) — a durable, Manager-verified human identity: the OIDC `sub` that the
  Manager itself issued. It exists only when a real authentication strategy attributed one.
- **Peer** (`PeerId`) — one client connection (a browser profile, stable via
  localStorage). It is connection identity, not human identity. The peer mints it, and
  nothing verifies it.
- **Actor** (`ActorRef`) — the attribution union on events:
  `UserRef | PeerRef | Agent | Session | System`.
- **Unattributed access** — the localhost strategy's grant. The request enters under the
  shared `local` subject, but no attributable user stands behind it, so events fall back to
  `PeerRef` attribution. The `Attributed`/`Unattributed` split in `AuthenticationOutcome`
  distinguishes a "real user". Never compare subject strings.
