# Yession — Design & System Fundamentals

This document states the durable principles and system design of Yession. It is the
canonical reference for *why* the runtime has its shape.

For the product framing, see the [root README](../README.md).

---

## 1. Core principles

These are architectural constraints, not preferences.

### Local first

A session runs on a local node. The first implementation assumes one node. Central
discovery, placement, and remote directories are deferred.

Clients connect to the local Session over WebRTC. HTTP may be used for static
app bootstrap and signalling, but **not** as the main session API.

### Reactive

Events drive the system as explicit state transitions. Components share no mutable state.
Transitions should be deterministic and composable. UI, collaboration, agent progress, and
event consumption are projections of state. Explicit boundaries isolate side effects.

### Types first

The type system specifies correctness. F# types encode core invariants, valid states, and
allowed transitions. Where possible, the check happens at compile time. Yjs is not the
product model. JSON blobs, Y maps, and stringly-typed payloads remain boundary formats.
They must not leak into application logic.

### Ylmish as the sync boundary

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

### Composition at the top

Capabilities are small and function-shaped. The application boundary composes concrete
infrastructure. Concrete infrastructure does not spread through domain code.

### High-signal automated verification

Each implementation phase must include automated end-to-end tests. Manual testing is not
a substitute for verification. There is no "one-shot" manual validation.

A test pins one invariant and goes red only when that invariant breaks. The rules for
writing tests are in [AGENTS.md](../AGENTS.md#writing-tests).

---

## 2. System overview

### 2.1 Runtime components

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

Manager (Phase 2)
  - launches Sessions
  - owns launch, identity, and secret custody
  - declares what a session may reach; holds no environment authority itself
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

The design allows HTTP only for serving the web app, initial local bootstrap, and temporary
signalling. **HTTP is not the session API.**

One read path is the exception the rule permits. It is read-only. HTTP also serves the
durable HISTORY by CURSOR: the event log and each terminal's transcript, on identical terms.
A client sends the position it has folded through (`GET /events/after/{n}`,
`GET /terminals/{t}/after/{n}`, or the bare path from the beginning). The server redirects to
the range it chose (`GET /events/{first}-{last}`, `GET /terminals/{t}/{first}-{last}`). It
answers `204` when that client is already current. The bounds of a range do not move, so its
bytes stay the same for ever. This holds for the growing tail too, which a fixed chunk index
could never manage. That stability makes an answer worth keeping.

The client keeps the answers in a store that it can enumerate and ask to persist. That store
is the Cache API, with one store named for the session's events and one per terminal. Every
response on this surface is `no-store`, because the copy that matters is the client's. A
second copy in the HTTP cache would be a spare nobody reads. `app/Signalling.fs` states the
cursor and range contract.

The client computes no address. It holds a position and stores each answer under the address
it received. The one number it computes is where an answer starts. That number comes from
what the client ASKED, not from where the answer lives: the answer to `after n` starts at
`n + 1`. Events carry their own offsets. Transcript lines cannot, because the file is an
asciicast and a private index field would stop it being one (`TranscriptCache` in
`src/Yession.App/Client.fs`). So the start of the cursor is a server contract with a test of
its own.

The durable history feed is therefore a second leg that can fail independently. The design
treats it as one:

- A read is `EventOffset option -> Async<Result<EventPage, FeedFault>>`. A dead feed is
  distinguishable from an empty one. An empty page never stands in for a failure.
- Transient-fault handling is a `Resilience.Policy` (`Yession.Domain/Resilience.fs`): retry,
  backoff, and jitter as values, with the clock injected. The application boundary
  (`app/browser/Browser.fs`) composes it *with the transport*, per "composition at the top".
  `App.connect` receives a feed that has already settled, so no application code holds a
  notion of retrying. `Resilience.fs` also holds `Verdict`, `Policy.deadline`, `Breaker`,
  `Quota`, and `Resilience.Http.verdict`. Its comments say what each one decides and how to
  compose it.
- The feed's health (`FeedHealth`) is model state. The UI renders it as a status and a
  banner. A stalled feed disables nothing. Collaborative state is CRDT state in the local
  doc, so reading, writing, and sending continue. Per "local first", a lost history feed
  costs history and nothing else.

The session leg can also fail independently, so the design supervises it on the same terms.
A WebRTC data channel can die without saying so, for example on a backgrounded phone or a
WiFi-to-cellular switch. `readyState` stays `open`, sends go into nothing, and no `close`
event fires. So `Link.supervise` (`Yession.Domain/Link.fs`) wraps every channel that carries
a session before anything else holds it. Any inbound frame is proof of life. A quiet link is
probed once a second, and three quiet ticks make it dead. Supervision is symmetric: the
Session holds every peer to the heartbeat it answers. This is also how a silently-dead peer
stops holding a terminal lease.

Death has exactly one expression: the channel CLOSES. Nothing above the wrapper learns about
liveness through a second channel, so no second state needs to stay consistent with the
first. Both pumps already handle a close. The client reconnects and re-pushes its full doc
state. The Session runs the cleanup it already ran. `LinkPolicy` takes its clock as a port,
as `Resilience.Policy` does, so a cheap-tier test asserts the whole quiet-tick sequence in
zero real time. The [ADR](decisions/2026-08-20-session-link-liveness.md) holds the reasoning.

---

## 3. Authority model (Phase 2)

The Manager owns launch, identity, and secret custody. It does not own environment
authority. A Session spawns its own sandboxes through the `CreateSandbox` seam. It confines
them with the `SandboxBackend` it boots with (`Yession.Domain/Sandbox.fs`). The sandbox
seam confines the agent's own CLI as well as the work it runs. A Session cannot mint
secrets. They cross from the Manager only at sandbox spawn, over the authenticated control
channel.

```text
Manager               owns launch, identity, and secret custody.
Session               owns orchestration; spawns and confines its own sandboxes.
Session Environment   a sandbox associated with exactly one SessionId; started lazily.
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
PKCE). Each Session registers as a client with its per-launch control secret. Three
authentication strategies ship. The `--auth` argument selects one at Manager start:

- `none` (the default): nothing authenticates until the operator chooses a trust rule.
- `localhost`: any loopback request is the single unattributed `local` subject, which
  matches this threat model.
- `trusted-headers`: an operator-run authenticating proxy (for example Tailscale plus a
  header-rewriting proxy) asserts the user in canonical `x-yession-*` headers. The proxy
  must be the only non-local path in. The proxy must strip those headers from client
  requests.

Further schemes are strategy swaps, not redesigns.

Secrets are Manager-owned authority:

- The OS credential manager holds the key that encrypts secrets at rest. Without a
  credential manager, nothing persists.
- A pure default-deny policy authorizes access. It reads the composite identity that the
  Manager verified itself: the calling session plus the users bound to its launch at
  ID-token issuance.
- Sessions hold pre-scoped write, list, and delete capabilities. They have no agent-facing
  read.
- Values leave the store through exactly two resolve-shaped routes. The caller's readable
  scopes gate each route.
  - `/control/secrets/resolve` feeds `SecretRef` env injection at the session's own sandbox
    spawn. Sandboxes are session-owned, so the injection point is there and not in the
    Manager.
  - `/control/connections/resolve` releases a connection credential for one agent turn.
- Refresh tokens never leave the Manager. No agent tool wraps either route.

That refresh is one of the resilience policies (`Broker.resilient` in `app/Broker.fs`,
composed in `ProcessManager`). It runs where nobody is looking, inside a turn or a git verb.
Without a policy, a provider's bad minute reads as a broken credential. A provider that
calls the grant dead still stops the refresh at once. That answer differs from a failed one.

MCP servers are Manager-owned authority in the same way. A declaration names a url and who
reaches it. The declaration lives in `ManagerState`, and every session it names connects
itself over the reverse control leg. The Manager never becomes an MCP client. Neither an
agent nor a peer can name a url, because what a session talks to is the operator's decision.
A session admits a stream that a tool result offers only on the host the operator already
declared. The tool descriptions that come back are the one untrusted text the model must
read. See [GAPS.md](GAPS.md).

---

## 4. Repo-local configuration

A checkout may carry a `yession.yaml` at its root. A session folds every such file it holds
into the commands it already has (Plan 27). The name is undotted, matching the other files a
repo means people to read.

```yaml
version: 2
sandboxes:
  app:
    container:
      image: node:24
      entrypoint: nix develop --command   # compose's word: what every piece of WORK runs behind
      command: ./serve                    # compose's word for the container's own process (`cmd` still reads)
    dialect: bash                         # optional: the shell a terminal opens, else detected
    workdir: ./packages/web
    env:
      DATABASE_URL: { secret: db-url }   # a name, never a value
      PATH: ${env.PATH}:/opt/tools       # composed over what lies beneath; `$${` is a literal `${`
    uses: [ npm-cache ]
    wants: [ github ]                     # the operator's `{ connection: { github: [git, api] } }`, where offered
```

A connection is a resource like any other. It lets `git push` and `gh` in a sandbox act as
whoever each command runs for. The operator offers it (`github: { connection: { github: [git,
api] } }`). A sandbox selects it under `uses` or `wants`. What the sandbox forwards is what
that selection reaches (`ForwardedConnections`, `WorkSandboxes.provisionSelection`). The
operator names the ROUTES that forward it (`ConnectionRoute`). Each route is its own grant
(`conn:github/git`). `git` is the git gateway and `api` is the credential proxy. A sandbox
that holds `git` alone is lent nothing its API client could spend. The session refuses a route
that the provider does not offer, where a sandbox needs it.

`${env.NAME}` in a value is what NAME would be in this sandbox without that line. Under docker
it is the image's own `ENV`, which is known only after the image is pulled or built. So the
backend resolves it (`Sandboxes.environment`). `EnvTemplate.fs` holds the grammar.

`${proxy.https}`, `${proxy.ca-file}` and `${proxy.ca-dir}` ask the session's credential proxy
for three things: its URL (this sandbox's own, which admits it), a whole trust bundle, and a
directory that holds its authority alone. Each value is as the sandbox sees it, so a container
is told mounted paths (`CredentialProxy.provide`). The proxy provides values only for a
sandbox that names one. These references compose like any value:

```yaml
    env:
      HTTPS_PROXY: ${proxy.https}
      SSL_CERT_DIR: ${env.SSL_CERT_DIR}:${proxy.ca-dir}
```

An srt sandbox is never given a connection's `api` route. `CredentialProxy.forwardApi` says
why. An srt sandbox is the session's `default`, a minimal place to check a repo out and commit,
and it takes a connection by `git` alone. The API (`gh`, Actions, releases) belongs to a
container. So under srt:

- A `uses:` of an `api` route refuses the start.
- A `wants:` goes without that route.
- A `${<connection>.token}` or `${proxy.…}` named there refuses, as it does wherever nothing
  forwards that connection by `api`.

`${github.token}` (any connection's `.token`) is a variable's whole value and nothing else.
It is a stand-in. The credential proxy swaps it for the credential of whoever each command runs
for. The session lends the stand-in per command and returns it with the command
(`EnvironmentVariableRef.Lent`). The sandbox must forward that connection by `api`, or it
refuses to start. Why the stand-in rotates, and whether that is worth it, is an open question
in docs/GAPS.md.

`${session.version}` is the build of Yession that runs this session. It is what `--version`
says and what the UI's footer shows. The session answers it itself
(`WorkSandboxes.answerSession`), so it needs nothing selected and works under every backend.
Nothing sets it unasked. An operator who wants the agent to know what it is talking to threads
it through a resource:

```yaml
yession-version:
  env:
    YESSION_SESSION_VERSION: ${session.version}
```

An operator may bind the same references in a resource's `env:`. A sandbox that selects the
resource receives them, and its repo does not write them (`VariableValue`, `SelectionGrant`):

```yaml
resources:
  github:
    connection: { github: [ git, api ] }
    env:
      HTTPS_PROXY: ${proxy.https}
      GH_TOKEN: ${github.token}
```

They join the sandbox's declaration under its own lines. A repo's line for the same variable
wins. The session provides and lends them exactly as it does a repo's. The session refuses a
resource that lends a token it does not itself forward by `api`, where it reads the file.

The session reads a container's `entrypoint` the way compose does: a list of words, or one
string split as a shell would split it, with nothing expanded. It governs what compose says it
governs, and one thing more. The container's own `command` runs behind it, as in compose. So
does every piece of **work** the session starts in the container: a terminal's shell, a block,
and `setup:`.

The session's own housekeeping runs bare, the way `docker exec` bypasses an entrypoint.
Housekeeping means its start-up checks, `git` run by path, and the probe behind
`set_shell_profile`. The rule adopts the line that compose drew (`ProcessEntry`). Work is what
a repo's toolchain is for, and a devshell that assembles the toolchain is exactly what a repo
puts here. Housekeeping needs no toolchain and must not pay for one.

The backend finds the shell that a terminal opens. It does not assume one. At start the
backend runs one `command -v` behind the entrypoint. It looks for the dialects a terminal can
instrument, most capable first (`bash`, `zsh`, `sh`). A terminal opens the first dialect found.
This also warms a devshell, once, off the first terminal. `dialect:` names a dialect instead.
A container that does not have that dialect refuses to start, naming what the backend looked
for.

The file has two top-level keys, and one is a version. **The whole file is sandboxes**,
because a sandbox is the only scope where "two repos both said something" has a total answer.
Each sandbox has a name, and its repo scopes that name. So `config(session) = ⋃ over repos r of
{ (r, name) ↦ spec }` is disjoint by construction. There is no precedence rule, and nothing
shadows anything. The session refuses a clash inside one file, where it was written. Anything
session-wide has no honest tie-break and stays the operator's. See [GAPS.md](GAPS.md).
`src/Yession.Domain/Config.fs` holds the design.

Three properties make the file safe to read from code a session just cloned:

- **It is a fold, not an executor.** Every mutating command is ensure-shaped. A declaration is
  one `start_work_sandbox` through the same gate as the agent's. Asking twice changes nothing
  and records nothing, so the fold can re-run at boot and after every verb that changes a
  checkout.
- **The operator's environment is a ceiling, not a default.** Narrowing takes effect at once.
  The session refuses an ask that exceeds the ceiling and names what exceeded it. A file that
  says nothing gets the operator's list whole.
- **Whoever can push to the repo authors the file**, and that person is neither the operator
  nor anybody in the session. So the session attributes the file's acts to
  `ActorRef.Configured`. The file owns no credential of its own. The schema refuses what a
  repo may not name: the whole `YESSION_` prefix in `env:`, a host path as a volume source, and
  a `workdir` outside the checkout.

This repository carries its own `yession.yaml`. It is the schema's acceptance test: what
yession needs to maintain yession measures whether the file can say anything real.

---

## 5. Architectural invariants

These must be visible in code review. They matter more than polish in the first phases.

```text
Elmish is the product model.
Ylmish is the encoding/sync boundary.
Yjs is not the domain model.
WebRTC is the session transport.
HTTP is bootstrap/signalling only.
The Session is the only event writer.
Clients consume events read-only by offset.
Drafts are collaborative state.
Sent messages are durable events.
Conversation is a projection.
Manager owns authority.
Session owns orchestration.
Environment starts lazily.
Capabilities are scoped, not ambient.
Verification is automated end-to-end, not manual.
```

`Ylmish is the encoding/sync boundary` has one deliberate exception. It is narrow enough to
name. Message bodies and terminal command lines are top-level Yjs roots that the app
co-manages directly (`src/Yession.App/Collab/Bodies.fs`). Ylmish's structural decode cannot
traverse a `Y.XmlFragment`: it recurses into the fragment's cyclic internals and crashes the
decode. The app keys these roots by `BodyKey`. It never nests them in the encoded tree, and no
whole-doc structural decode reads them.

Everything else still crosses through the codec. Folding a body back into the Ylmish schema
breaks decoding for every peer at once.

---

## 6. Cross-cutting type vocabulary

Nearly every step shares the identity and envelope types. `Yession.Domain/Identity.fs`
declares them, and the code references them throughout.

The actor glossary:

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
