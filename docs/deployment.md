# Deployment

Before Yession is reachable from another machine, settle three things on the Manager:

- **who** the humans are
- **where** the Manager and its sessions answer
- **how long** connected credentials live

Sessions inherit all three through the environment. The interfaces are what Yession asks of
whatever sits in front of it. The integrations are worked examples that satisfy them.

---

## Checking a release

Each GitHub release carries `SHA256SUMS` beside its assets. The tarball and `SHA256SUMS` also
carry signed build provenance: which workflow, on which commit of this repository, made them.
Download the assets into one directory, then check them before you install the tarball:

```sh
sha256sum -c --ignore-missing SHA256SUMS
gh attestation verify yession-<version>.tgz --repo trinketworks/yession
gh attestation verify SHA256SUMS --repo trinketworks/yession
```

`sha256sum -c` proves that the downloads match the list. `gh attestation verify` proves that
this repository's release workflow made them. If either fails, do not install the tarball.

---

## Installing a release

The npm package is a release asset, `yession-<version>.tgz`. It carries `npm-shrinkwrap.json`,
which pins every production dependency to the version the release was tested with. Install it
like this:

```sh
mkdir -p ~/.local/share/yession
tar -xzf yession-<version>.tgz --strip-components=1 -C ~/.local/share/yession
(cd ~/.local/share/yession && npm ci --omit=dev)
npm i -g ~/.local/share/yession
```

The directory is the install. `npm i -g` links the two commands to it, so do not delete the
directory. To upgrade, delete the directory, then run the same steps with the new tarball.

Do not run `npm i -g yession-<version>.tgz`. npm honours a dependency's shrinkwrap only when
the package came from a registry, so an install by path or URL resolves the version ranges
again and gets dependency versions no release was tested with. `npm ci` in the extracted
directory reads the shrinkwrap as its own lockfile.

A fix in a dependency therefore reaches you in a Yession release, not by reinstalling.

---

## Options and variables

The Manager takes what it **decides** as an option. What it **passes down** stays in the
environment, which a child inherits. So the Manager refuses a mistyped decision, and `--help`
is the list.

```sh
yession-manager --help                # what it accepts
yession-manager --check               # what it resolved to, then stop
yession-manager --check --detailed    # the same, plus what each setting and state means
```

`--check` launches nothing and binds no port (`checkOption` in `app/ManagerCli.fs`). Each
line says where its value came from: `set`, `default`, or `off` (the feature is not enabled).
Use it while you configure a deployment. Its last section lists every `YESSION_*` variable in
the environment, which is what a session sees. An unrecognised one is not refused, because the
examples use the prefix too (`YESSION_PROXY_PORT`, `YESSION_SERIAL_PORT`).

| shape | who sets it | example |
|---|---|---|
| an option | you, the operator, for this Manager | `--port`, `--idle-timeout`, `--data-dir` |
| `YESSION_MANAGER_URL` / `YESSION_SESSION_URL` | you, and every session reads them too | see §Addressing |
| `YESSION_BIN_*` | you or the package. It names an executable a session runs | `YESSION_BIN_GIT`, `YESSION_BIN_BWRAP` |
| `YESSION_SESSION_*` | you. It is per-session policy, today set once per host | `YESSION_SESSION_WORK_BACKEND`, `YESSION_SESSION_GIT_BACKEND`, `YESSION_SESSION_RESOURCES` |
| `YESSION_LAUNCH` | nobody — the Manager mints it per launch | — |

The Manager **refuses** a variable that moved onto the command line. The boot stops and names
the option to write instead. `app/Retirements.fs` lists the retired names and says why.

A session runs three sandbox families, and each has its own backend variable. Each is parsed
at boot and fails closed: a value it does not know stops the session.

| variable | runs | values | default |
|---|---|---|---|
| `YESSION_SESSION_WORK_BACKEND` | the agent's terminals, blocks and `setup:` | `host`, `srt`, `docker` | `srt` |
| `YESSION_SESSION_AGENT_BACKEND` | the `claude` CLI | `host`, `srt` | `host` |
| `YESSION_SESSION_GIT_BACKEND` | the session's own git verbs: clone, fetch, switch, status, log, diff | `host`, `srt` | `srt` |

The git verbs work on checkouts that every work sandbox can write, `.git/config` included. Under
`srt` a program that config names runs with the git sandbox's reach: the repos directory, and
the git gateway as its only egress. `YESSION_SESSION_GIT_BACKEND=host` runs the verbs as you,
unconfined, so a work sandbox can then run a program as you through a checkout's
`filter.<driver>.smudge` (docs/GAPS.md). Choose it only where srt cannot run.

`YESSION_LAUNCH` is the launch's control secret. It grants custody of the session's secrets
and the right to register as an OIDC client. Setting it by hand impersonates a session. It
never reaches a sandboxed command.

`YESSION_SESSION_RESOURCES` names a **resources profile**. The profile holds named resources
that a repo's `yession.yaml` selects by name, so a repo cannot exceed what the operator
declared. `resources:` is what the host can offer. `always:` is what every sandbox holds
without asking: no repo has to name it and none can decline it. A resource that the profile
declares, and does not list under `always:`, is available and not granted.

The same file may declare `sandboxes:` in the form a repo's `yession.yaml` uses
(`ConfigFile.parseSandboxes` in `src/Yession.Domain/Config.fs`). Boot prints the notes of
`ConfigAnalysis.fs` on what is legal but likely unmeant.

Each sandbox in the file exists in every session on this host from boot, on the session's
sandbox backend. These are ALL of a session's own sandboxes (`WorkSandboxes.create`). Yession
has no built-in sandbox. `default` is where a terminal that names no sandbox opens, and it
exists only if you declare it. Without it, a session has nowhere to run a command until a
repo's sandbox starts. Declare it with what it should hold: `wants: [ github ]` for `git push`
from a terminal.

The same file may bound every container on this host, under `container:`, in compose's
spelling: `mem_limit`, `pids_limit` and `cgroup_parent`. A repo's `mem_limit` and
`pids_limit` may be lower than yours, never higher. A higher one refuses the sandbox, and the
refusal names both values. A repo that names none gets yours. Only you name `cgroup_parent`.
A repo that writes it is refused ([technical-design.md
§4](technical-design.md#container-limits)).

The same file carries what you say about the agent, under `agent`. `agent.guidance` is
appended after the product's own system prompt on every turn, as the operator's words. It never
replaces that prompt, which already describes the build's tools and sandboxes. Write only what
this host knows: its conventions, what is slow here, and what must never be pushed where.

`agent.prompt` picks the strategy that assembles that prompt, from the strategies the build
has:

- `static`, the default: the product's sections and your guidance as one system prompt, and
  the turn's context (the time, the conversation, terminal activity, repo notes) as one
  message.
- `claude-code-like`: Claude Code's layout. The product's sections come first and are the
  same on every host. They include one more section from Claude Code: look at a UI change in
  a headless browser before you call it done, or say that you did not look. Your guidance, and
  a note on tools from other MCP servers, follow the SDK's dynamic boundary. The turn's
  context arrives as `<system-reminder>` blocks beside the message the turn answers.

A session reads the file at boot and reports on its stderr which strategy it chose. To say
nothing about the agent, leave the block out. Yession refuses a block that says nothing. It
also refuses a strategy that the build does not have.

---

## Interfaces

### Authorizing

Yession authenticates nobody itself. At start, it takes one named **trust rule** that
establishes each request's subject:

```sh
yession-manager --auth localhost         # single machine
yession-manager --auth trusted-headers   # an authenticating proxy in front
```

| `--auth` | Behaviour |
|---|---|
| *(absent)* / `none` | Denies every request, so an exposed endpoint cannot fall back to an unintended rule. |
| `localhost` | Any loopback request is the single unattributed subject `local`. |
| `trusted-headers` | The proxy in front asserts the user in canonical `x-yession-*` headers, trusted verbatim. The Manager listens on a unix socket (`--socket <path>`), and refuses to boot on a TCP port. |

An unknown name or option fails the boot, because an ignored `--auth` would refuse everyone.
Every bin answers `--help`.

`trusted-headers` replaces `localhost`. The two never compose. Behind a proxy that terminates
on loopback, every request is loopback, so a header-less request would become `local`. That is
the bypass the proxy exists to prevent.

#### Where the Manager listens

A trust rule believes something about the connection, so it decides the door
(`Listener.admits` in `app/ProcessManager.fs`):

| `--auth` | Listener |
|---|---|
| `localhost` | A TCP port on loopback (`--port`, default 8321). It reads the peer address, which only TCP has. |
| `trusted-headers` | A unix socket (`--socket <path>`). A TCP port is refused at boot. |
| `none` | Either. |

Under `trusted-headers` a header is the subject, so it is true only if nothing but the proxy
could send it. A TCP port on loopback cannot say that. Every process on the host dials it as
loopback. So does a container whose network reaches the host loopback, which Colima's and
Docker Desktop's do. A socket's permissions can say it. The Manager makes the socket `0660`.
Give the proxy the Manager's user or group, and no sandbox or container a path to it.

Put the socket in a directory that no sandbox grant names: not under `--data-dir`, and not
under any path the resources profile mounts, reads or writes. srt hides every path a policy
does not name, so a confined command cannot reach a socket it cannot see. But on Linux a
`Socket` grant lets a confined command dial every unix socket it can see (srt reads no path
list there, [GAPS.md](GAPS.md#security--trust)), and it runs as the Manager's uid, which the
socket's mode admits. A grant that names the socket's directory, or a directory above it,
hands that sandbox the Manager. The data directory is not such a place by construction: an
operator's profile can name any path, the data directory included. The module below keeps
the socket under `~/.local/state/yession`, apart from the data directory.

`--socket` needs the public origin ([§Addressing](#addressing)), because no browser dials a
socket. A session dials the socket for its control channel (`ControlEndpoint.Socket` in
`YESSION_LAUNCH`), and the public origin for OIDC discovery. A socket path is at most 104
bytes on macOS and 108 on Linux.

#### What the trust rule decides about credentials

The rule also decides who **owns** a Claude or GitHub account connected for "all my sessions":

| `--auth` | "All my sessions" means |
|---|---|
| `trusted-headers` | That user. Nobody else's turn runs on it. |
| `localhost` | This deployment. One connection serves every visitor, browser, and device. |

Before you choose `localhost`, read [what it costs](#what---auth-localhost-costs-here)
([ADR](decisions/2026-08-10-local-scope.md)).

### Credentials

At start, choose once how long a connected credential lives:

```sh
yession-manager --secrets ephemeral   # dies with this Manager
yession-manager --secrets durable     # persistence required, or refuse to boot
```

| `--secrets` | Behaviour |
|---|---|
| *(absent)* | Durable where the OS credential manager answers. In-memory, with a warning, where none does. |
| `durable` | A host with no usable credential manager refuses the boot rather than run a store that dies. |
| `ephemeral` | In-memory even where a credential manager exists. The Manager leaves an existing `secrets.json` unread. |

Unknown names fail the boot, as for `--auth`.

Durable secrets are one AES-256-GCM file in the data directory, `secrets.json`. The key comes from
the macOS Keychain, the Windows Credential Manager, or the Linux Secret Service. There is no key
file and no key variable. Beside it, `records.json` holds what sessions keep for a person across
their sessions, sealed under the same key
([ADR](decisions/2026-11-01-records-are-a-third-family-of-the-store.md)). A Manager that cannot
read `records.json` (a newer build wrote it, or another key sealed it) says so at boot as
`yession.secrets.records_refused`, leaves it as it is, and opens the credentials without it.

**Roll back only with the file you had.** The file has a schema version. A Manager refuses a
`secrets.json` that a newer build wrote, and says so. A Manager from before the version 2
schema (generic secrets and connection credentials kept apart,
[ADR](decisions/2026-10-26-secrets-and-connections-are-two-families.md)) cannot: it reads the
newer file and, at its first write, drops every connected credential. Before an upgrade, copy
`secrets.json` and `records.json` out of the data directory. If you roll back (one `nix`
command), put those copies back in the same step. The Manager makes no copy itself: a copy would
outlive Disconnect. A Manager from before records does not read `records.json` and does not
change it.

#### Upgrading past v14.4.1-beta.1

v14.4.1-beta.1 is the first build with the version 2 store. At its first start, the Manager
converts a version 1 `secrets.json` itself and writes it back once. You convert nothing by hand.
Under `--secrets ephemeral` there is no file, so only step 2 applies.

1. Stop the Manager, and copy `secrets.json` out of the data directory.
2. Find every `{ secret: <name> }` in the `yession.yaml` files your sessions use
   (`rg -n 'secret:' --glob yession.yaml`). Since v14.4.1-beta.0 a reference reads only that
   session's own secrets. Fix each one whose value came from somewhere else:

   | The value came from | Now |
   |---|---|
   | the Manager's environment, or a user's secret | the agent writes it with `set_secret` in the session |
   | a connection (`github`, `claude-code`) | select the connection under `uses:` or `wants:`, and use `${github.token}` |

3. Deploy and start. The boot log has an `audit` line `yession.secrets.store_open` and no
   `store_open_failed`.
4. Check that `secrets.json` says `"version": 2`, and that the GitHub and Claude panels still show
   their connections.
5. A sandbox that refuses to start names the secret it is missing. Fix that reference as in step 2.

#### Your own key store

A headless server has no credential manager. It has something else: a cloud KMS, a Vault, a
TPM through `systemd-creds`. Name a helper, and the Manager keeps its key through that instead:

```sh
yession-manager --secrets durable --keystore exec:/usr/local/bin/yession-keystore
```

The Manager runs the helper once per operation, with the operation as its one argument. The
protocol is the one git and Docker credential helpers use:

| Call | Contract |
|---|---|
| `<helper> get` | Print the stored payload to stdout, or nothing when there is none yet. Exit 0. Exit non-zero when the backend failed, and say why on stderr. |
| `<helper> set` | Read the payload from stdin and store it. Exit 0 only once it is durable. |

The payload is opaque text, a few hundred bytes. It travels on stdin and stdout and nowhere
else: never as an argument, never in a variable. The helper inherits the Manager's environment
and runs as its user, so an instance role or a Vault token the Manager's process has, the
helper has. A helper that exits non-zero does not fail the boot. The Manager retries every 30
seconds and says so, the way it waits out a locked keychain, so a role whose credentials arrive
after the service starts is fine. A helper that answers something that is not a payload, or that
has not answered in 30 seconds, fails the open.

A twelve-line helper over AWS KMS: `set` runs `aws kms encrypt` on stdin and writes the
ciphertext to a file beside the data directory, `get` runs `aws kms decrypt` on that file. The
key at rest is then ciphertext under a key that never leaves KMS, every unwrap is in CloudTrail,
and disabling the KMS key makes every stored credential unreadable at once. What a helper does
not change: the raw key is in the Manager's memory between `get` and its import, as it is
between the credential manager's answer and the same import.

`--keystore keyring` is the default. `--keystore` with `--secrets ephemeral` is refused: a key
store nobody opens is a setting that silently did nothing.

#### The header scheme

The proxy translates its authenticator's output into these headers. Values are UTF-8. Node
lowercases the names:

```
x-yession-user            REQUIRED — the subject, a stable unique user identifier.
                          Absent or blank ⇒ 401.
x-yession-user-name       optional display name
x-yession-user-email      optional email
x-yession-user-picture    optional avatar URL
x-yession-user-claims     optional JSON object of additional claims, carried opaquely
                          — recorded, not yet policy
```

### Session lifetime

```sh
yession-manager --idle-timeout 30m       # or 90s, 2h; absent means never
```

The Manager stops sessions that nobody uses. A session reports busy or idle over its control
channel (a connected peer, a running turn, a command, a non-empty queue). The Manager reaps on
**silence**, so no single report has to arrive. Two costs:

- A reaped launch loses its OAuth client registration and user bindings, so the next visitor
  signs in again: invisible under `localhost`, a repeat redirect under `trusted-headers`.
- A session that wedges after readiness is stopped as `NeverReported`, not diagnosed.

Reaping also gives rolling upgrades. Point `--spawn-bin` at a path that floats with your builds,
and sessions upgrade as they idle out, without the Manager restart that evicts everybody. A
[MAJOR](../AGENTS.md#versioning) version difference refuses the launch, since the control
protocol may disagree. The refusal drains the running set and hands you that moment.

### Addressing

A deployment is either **loopback** (nothing set, everything on `127.0.0.1`) or **fronted**
(both set, everything public). The Manager refuses a half-set pair at boot, because either half
alone points somebody at loopback. The header of `src/Yession.Domain/PublicAccess.fs` and the
[ADR](decisions/2026-08-28-deployment-fronts-everything-or-nothing.md) give both failures.

```sh
YESSION_MANAGER_URL=https://example.com          # the Manager: scheme + host, no path
YESSION_SESSION_URL=https://example.com/s/{id}   # sessions: a template
```

#### The Manager

Use scheme + host, with an optional port and **no path**. The Manager's routes are
origin-anchored and its issuer is a concatenation base, so it rejects a prefix. That is what
lets the Manager share an origin with its sessions: the Manager owns `/` and sessions own
`/s/*`.

The Manager is the OIDC issuer that sessions redirect users through, so browsers must reach
the origin. It must also resolve **from this host**. Every session runs OIDC discovery, JWKS,
and token requests against it at boot, and refuses to half-start if it cannot. Split-horizon
DNS or a proxy on an external interface only fails registration.

#### Sessions

The URL is a template over `{id}` (the session id) and `{port}` (its OS-assigned port), which
is what the registry stream publishes:

```sh
YESSION_SESSION_URL=https://example.com:{port}         # a port mirrored per session
YESSION_SESSION_URL=https://{id}.sessions.example.com  # a subdomain per session
YESSION_SESSION_URL=https://example.com/s/{id}         # a path per session
```

Yession refuses a template with no placeholder, since every session would share one address.
`{port}` may appear in the authority but never in the **path**: a session must know its mount
before it binds, because the mount fixes its `<base href>`, cookie `Path`, and stripped prefix.

#### Prefer `{id}`

A `{port}` template gives a session a new origin per launch, and the browser partitions
storage by origin, so what it wrote while the session was away is stranded. The shell then
emits `<meta name="yession-ephemeral-storage" content="1">`, and the client stops promising a
sync it cannot deliver. Under `{id}`, the promise holds. Everything already sent is on the
server either way.

#### The registry stream

The stream sends full snapshots, never deltas. A subscriber applies each frame whole, and a
missed frame costs nothing:

```sh
curl -sN http://127.0.0.1:8321/sessions/stream
# data: {"sessions":[{"id":"local-session","name":"…","port":57239,"pid":95225}]}
curl -sN --unix-socket /var/lib/yession/manager.sock \
  -H 'x-yession-user: tracker' http://yession/sessions/stream      # under trusted-headers
```

The trust rule gates the stream like every management route. Under `trusted-headers`, every
caller on the Manager's socket (a proxy binding, a health check, a tracker) asserts an
`x-yession-user` that names what it is, or gets 401s and no frames. Under `localhost`, nothing
reads the header.

The template is not on the wire. The deployment applies its own to `id` and `port`, so the
prefix has one home. [`examples/proxy/main.mjs`](../examples/proxy/)
is a reference reconciler. [§Tailscale](#tailscale) composes it.

---

## Integrations

### GitHub

A session clones over HTTPS and watches pull requests with a credential that a person signs in
for from inside the session. There is no default client id. Register your own GitHub App, or
the only way in is to paste a token.

Register an App (not an OAuth App), and:

- **Enable device flow** — the session runs it. The code exchange needs a client secret that
  the Manager's public-client broker cannot carry.
- **Set no callback URL that matters, generate no client secret** — Yession uses neither.
- **Install it on the repositories it should reach** — a device-flow token sees the
  intersection of the user's access and the App's installations. Nothing in Yession re-checks
  that.
- **Give it the repository permissions a session spends**, because a user-to-server token
  can do no more than the App can:
  - *Contents: read and write* — clone, fetch and push through the git gateway, and a merge.
  - *Pull requests: read and write* — watch, open (`create_pr`), and merge (`merge_pr`: auto
    merge, the merge queue, and a direct merge alike).
  - *Checks: read* — the check-runs rollup a watched pull request reports.
  - *Metadata: read* — granted to every App. The picker reads the repository listing.
  - *Email addresses: read* (an account permission), optionally. Without it, the commit
    identity from the profile carries the account's `noreply` address, not its public one.

  GitHub mints the token without a missing permission. The verb that needed it fails, and
  Yession passes GitHub's 403 or 404 through in words.
- **Leave user-token expiration on** — the Manager holds the refresh token and rotates it
  before each turn. With expiration off, the token is permanent, and nothing tells you which
  setting you registered.
- **Point its webhook at `<YESSION_MANAGER_URL>/hooks/github`** if you declared a hook
  endpoint ([§Webhooks](#webhooks)), with the secret the manager page shows. Without the hook,
  polling still works. A hook only advances its clock.

Then name the client id:

```
YESSION_GITHUB_CLIENT_ID=Iv1.0123456789abcdef
```

If you leave it unset, the sign-in surface says so and offers the paste path. A pasted
`github_pat_…` or `ghp_…` token works and **bypasses the installation rule** (recorded in
[GAPS.md](GAPS.md)). Where device flow exists, Yession refuses a pasted `ghu_…` or `gho_…`
token, since it expires in hours and cannot rotate.

`YESSION_GITHUB_DEVICE_URL`, `YESSION_GITHUB_TOKEN_URL`, `YESSION_GITHUB_USER_URL` and
`YESSION_GITHUB_API_URL` override the four endpoints, for the test suites.

### Webhooks

The Manager takes signed deliveries and hands them to the sessions that asked, without reading
them. A session declares a filter over paths, and the Manager matches it
([ADR](decisions/2026-08-29-the-manager-relays-hooks-it-cannot-read.md)).

```sh
yession-manager --webhook github --webhook 'linear=linear-signature:hex'
```

Linear signs in its own `linear-signature` header, in hex, with no prefix. Its endpoint
therefore carries a signature override (see below). GitHub uses the default.

The Manager serves each endpoint at `<YESSION_MANAGER_URL>/hooks/<name>`, so this needs a
fronted deployment. The manager page shows each endpoint's address and signing secret. The
Manager derives the secret from the credential manager's key, so it refuses endpoints under an
ephemeral store: a secret that changed at every restart would break deliveries silently.

To rotate, bump the counter. The Manager accepts the new secret and the one before it until
the next bump:

```sh
yession-manager --webhook github@1
```

The manager page shows each endpoint's declaration back to you, canonicalised. It is the only
place where the current rotation is visible.

Verification is HMAC-SHA256 over the raw body, in hex, in `X-Hub-Signature-256` behind
`sha256=` (the WebSub convention, which GitHub uses). For a sender with another header,
encoding or prefix, such as Shopify, add an override after an `=`:

```sh
yession-manager --webhook 'shopify=x-shopify-hmac-sha256:base64:'
```

The grammar is `name[@rotation][=header:encoding[:prefix]]`. A name is letters, digits, `-`,
and `_`. The signature part splits on its first two colons only, so a prefix may hold anything,
`sha256=` included. The option splits on its FIRST `=`.

Not supported: schemes that sign a timestamped string (Stripe `<t>.<body>`, Slack
`v0:<ts>:<body>`). `SignatureSpec` in `app/WebhookRelay.fs` says why.

### Tailscale

The deployment has one origin, behind one reverse proxy of your own. The Manager answers at `/`
and each session at `/s/<id>`:

```
browser ──tailnet──▶ tailscale serve ═════▶ your proxy ═════┬══▶ manager      /
                     TLS + identity   socket   one mapping    socket
                                                            └──▶ session i    /s/<id>
                                                                 TCP, loopback
```

`serve` holds one mapping: TLS on the tailnet, and who is calling. Routing, identity
translation, and stripping live in the proxy. Both hops that carry an identity are unix
sockets (`═`): `serve` to the proxy, and the proxy to the Manager. Nothing on a TCP port
believes its peer. The sessions' ports authenticate by cookie and token, not by peer. [`examples/proxy`](../examples/proxy/README.md)
has the pieces and how to run them
([ADR](decisions/2026-09-03-one-proxy-in-front.md)). This section describes what they
implement, for a deployment on another proxy.

#### Authorizing

**Use `--auth trusted-headers`.** `serve` asserts the calling tailnet user on every request and
**overwrites** any such header that a client sent. This makes the headers trustworthy:

```
Tailscale-User-Login        the user's login name
Tailscale-User-Name         display name
Tailscale-User-Profile-Pic  avatar URL (empty where the account has none)
```

`serve` does not touch `x-yession-*`, so a forged `x-yession-user` reaches the proxy intact.
The translation below is therefore also the strip. `serve` cannot rename headers, so the proxy
does it on the Manager's route. The Caddy form, with its reasons, is the `handle` block in
[`examples/proxy/caddy/Caddyfile`](../examples/proxy/caddy/Caddyfile):

| header | the proxy |
|---|---|
| `x-yession-user` | sets it from `Tailscale-User-Login` |
| `x-yession-user-name` | sets it from `Tailscale-User-Name` |
| `x-yession-user-picture` | sets it from `Tailscale-User-Profile-Pic` |
| `x-yession-user-email`, `x-yession-user-claims` | deletes it |
| `tailscale-*` | deletes it |

- **Set what `serve` asserts, delete what it does not** — a set replaces the client's value.
  Delete email and claims by name, or a client chooses its own.
- **Delete by name, not `-x-yession-*`** — Caddy applies deletions after sets, so the wildcard
  strips what was just asserted. The fronted browser case in `tests/Yession.Tests/Browser.fs`
  measures this.
- **Every door that carries an identity is a socket** — the proxy believes
  `Tailscale-User-Login` and the Manager believes `x-yession-user`, so each listens where
  only its asserter can dial. A TCP port on loopback is dialled by every local process and
  by a container that reaches the host loopback. The Manager refuses one under
  `trusted-headers` ([§Where the Manager listens](#where-the-manager-listens)). The proxy's
  is the Caddyfile's `bind`.

The mapping is a socket target, run as root:

```sh
sudo tailscale serve --bg --https=8321 unix:/var/lib/yession/proxy.sock
```

Two constraints decide whether that works on a host:

- **Tailscale 1.98.9 or later runs a socket target only from a root `serve`** (security
  bulletin TS-2026-005). Run the command once with `sudo`. The mapping persists in
  tailscaled's state. On 1.102 every change to `serve` is a root action, TCP targets
  included: the CLI refuses an operator too, with `must be root, or be an operator and able to
  run 'sudo tailscale'`.
- **The sandboxed macOS app cannot dial a socket.** The Standalone and App Store builds fail at
  request time with `dial unix …: connect: operation not permitted` (tailscale/tailscale#21900,
  open on 1.104.1). Use the open-source `tailscaled` on macOS, which is not sandboxed. While
  either app also runs, the bare `tailscale` CLI talks to the app, not to `tailscaled`.
  `tailscale --socket /private/var/run/tailscaled.socket` reaches the daemon: the CLI looks
  for the app only when `--socket` is its default `/var/run/tailscaled.socket`, and on macOS
  the two paths are one file.

On a host that must keep the sandboxed app, the alternative is the `caddy-tailscale` plugin. It
makes the Caddy site its own tailnet node (`bind tailscale/<name>`), so no ingress sits between
the tailnet and the proxy. Its `tailscale_auth` directive takes the user from the connection
itself, through tailscaled, and not from a header: replace the three `header_up` sets with
`tailscale_auth` and set `x-yession-user` from `{http.auth.user.tailscale_login}`. The Manager's
socket stays. The plugin is experimental, and this repository neither packages nor tests it.

Session routes carry none of this. A session authenticates through the Manager as OIDC issuer,
never by header, so a proxy that can only forward still fronts sessions correctly.

Callers on the Manager's socket assert themselves
([§The registry stream](#the-registry-stream)), for example
`main.mjs --manager unix:<path> --as proxy-map`.

> Verified against a live tailnet, on macOS with HTTPS certificates. Over a TCP mapping, on
> Tailscale 1.102.3: the identity headers, the overwrite, the `x-yession-*` pass-through, the
> deletion ordering, and the composition below with a Manager and a session behind it. Over
> the socket mapping, on 2026-10-25, with the open-source tailscaled 1.102.4 under nix-darwin:
> the whole operator checklist of PR #1508, from the Manager refusing a TCP port under
> `trusted-headers` to a forged `x-yession-user` audited as the real tailnet login and a
> session's terminal reaching GitHub. Every pull request re-verifies the Caddy half over both
> sockets, against whichever Caddy the dev shell pins, with a TCP-to-socket relay standing in
> for `serve`.

##### What `--auth localhost` costs here

`serve` terminates on loopback, so every tailnet visitor becomes the one subject `local`.
Nothing errors. The audit trail says `local` for everyone, and any visitor's turn spends
against the deployment's Claude account. This is coherent on a personal tailnet and a silent
failure when a second person arrives.

Two bounds answer different questions:

- **`--secrets ephemeral`** — keep the sharing, lose the permanence: the credential dies with
  this Manager process.
- **`--auth trusted-headers`** — lose the sharing: each human owns their own credential.

##### What a tailnet member can do

Under `trusted-headers`, identity is per human and sessions are shared by every member. The
rule establishes who is calling. Nothing then decides what that person may do, so every
authenticated member is an operator of every session. A member can:

- create, launch, stop and archive any session,
- join any session, read its whole event log and its terminals, drive its agent, and type
  into its sandboxes,
- declare MCP servers, which every session they name connects to.

What stays the member's own is their credential. A turn and a lent token run on the credential
of the person who acted, so no member's turn spends another's Claude or GitHub account.

Two places cross that boundary:

- **A repository's `setup:` block** runs under the authority of whoever triggered the fold
  that started its sandbox: the person who added the repository, approved what its
  `yession.yaml` asks for, or otherwise ran a repo verb (`Authority.configuredBy` in
  `RepoSandboxes.foldOnce`, built by `Commands.startWorkSandboxCall`).
- **A webhook delivery** reaches every session whose filter matches, so one member's session
  reads another member's pull-request traffic.

So the deployment is for one team that shares its work. A per-session owner is not
implemented ([GAPS.md](GAPS.md#security--trust)).

#### Addressing

Make one `serve` mapping, once, to your proxy, and set both URLs to the tailnet origin
([§Addressing](#addressing)). The commands are in
[`examples/proxy`](../examples/proxy/README.md#running-it).

The proxy routes `/s/<id>` to the session's port **without stripping the mount**. A session
serves under its mount: it answers at `/s/<id>/…` and returns 404 at `/`. In Caddy, therefore,
use `handle`, never `handle_path`, with the bare port upstream.

Use `--https` where the tailnet issues certificates. Derive the scheme in both URLs from one
variable, or the Manager and its sessions become two origins. A browser also withholds the
Cache API and service workers outside a secure context, so over plain HTTP a session keeps no
history on the device and cannot open cold offline. Loopback is a secure context, so the
default is unaffected.

##### Keeping the session routes in step

A session's port is OS-assigned per launch, so a process writes its route.
[`examples/proxy`](../examples/proxy/README.md#running-it) has the reconciler and its command.
Any reconciler against the stream should keep three rules:

- **Render the whole set, every frame** — a frame is the running set, so a reaped-and-relaunched
  session (same path, new port) can never leave a route aimed at a dead port. Rewrite only on
  change, beside-then-rename, so the proxy never reads half a file.
- **Nothing persists between runs** — the file is the state and the next frame replaces it, so
  no restart needs a cleanup pass, unlike routes pushed into an ingress's own persisted state.
- **Which silence means what:**
  - A stream that ends is a Manager restart. Keep the map. The snapshot on reconnect heals it.
  - A connection refused means no Manager. Write the map empty, because sessions cannot
    outlive the Manager.
  - A 401 is a misconfigured header. Log it, keep the map, and retry. A misconfigured header
    must never unmap a deployment.

##### Rough edges

- **A bare `/s/<id>` has no canonicalising redirect** — the auth cookie's `Path` is `/s/<id>/`,
  so that one request carries no cookie. This is harmless because `<base href>` makes every
  sub-fetch absolute.
- **The session id is not percent-encoded into the path** — ids are Docker-safe by
  construction, so nothing can yet produce one that needs it.

### Nix

`packages.<system>.default` is `yession-manager` and `yession-session`
([`nix/packages.nix`](../nix/packages.nix)). The package wraps them with the native WebRTC
addon, which it builds from source, and defaults `YESSION_BIN_CLAUDE` and `YESSION_BIN_GIT` to
store paths.

Pin the input and leave its nixpkgs alone, because the package builds the addon against that
pin:

```nix
inputs.yession.url = "github:trinketworks/yession";
# NOT `inputs.yession.inputs.nixpkgs.follows = "nixpkgs"`: that trades a cached,
# tested native build for an untested one.
```

The module below is the deployment the project itself runs: home-manager under nix-darwin.
Its three agents make up all of [§Tailscale](#tailscale), with the example's files as they ship:

```nix
{ config, pkgs, inputs, ... }:
let
  yession = inputs.yession.packages.${pkgs.stdenv.hostPlatform.system}.default;
  proxy = "${inputs.yession}/examples/proxy";   # the Caddyfile and the reconciler
  origin = "https://host.example.ts.net:8321";  # scheme + host once, feeding both URLs
  stateDir = "${config.home.homeDirectory}/.local/state/yession";
  # The two doors that carry an identity, both sockets (§Tailscale). The proxy's is what
  # `sudo tailscale serve --bg --https=8321 unix:<proxySocket>` targets, once, as root.
  managerSocket = "${stateDir}/manager.sock";
  proxySocket = "${stateDir}/proxy.sock";
  proxyDir = "${stateDir}/proxy";
  logDir = "${config.home.homeDirectory}/Library/Logs/yession";

  # Paths as the kernel sees them (/private/etc, not /etc): the profile refuses
  # symlinked spellings. This repository's yession.yaml `wants:` both
  # `nix-container-store` and `github`. `github` asks the session's user once per
  # repository, because it is spent as them. The volume does not ask: it is shared by
  # every session on this host, so whether a repository may write it is yours, here.
  resources = pkgs.writeText "yession-resources.yaml" ''
    version: 1
    resources:
      # `maintain:` keeps the shared store bounded (§Keeping the warm store bounded).
      nix-container-store:
        volume:
          name: yession-nix
          at: /nix
          # Required. `write` lets every repository that selects this change what
          # each other session builds from. `read` does not, and cannot be maintained.
          mode: write
          maintain: { pin: /nix/var/yession/pin, every: 10m }
      # For a repo's CONTAINER sandboxes: the gateway, and the credential proxy's URL,
      # trust bundle (every root, so replacing the image's loses nothing) and a token
      # lent per command.
      github:
        connection: { github: [ git, api ] }
        env:
          HTTPS_PROXY: ''${proxy.https}
          SSL_CERT_FILE: ''${proxy.ca-file}
          GH_TOKEN: ''${github.token}
          GITHUB_TOKEN: ''${github.token}
      # For `default`, which runs under srt: srt takes a connection by git alone.
      github-git:
        connection: { github: [ git ] }
      ca:
        mount: { from: /private/etc/ssl/cert.pem, mode: read }
        env:
          NIX_SSL_CERT_FILE: /private/etc/ssl/cert.pem
    # The ceiling on every container on this host. A repo's `mem_limit` and
    # `pids_limit` may be lower, never higher. The pool: one cgroup that holds every
    # session's containers, with a limit below the VM's total, so an overrun of the sum
    # stays inside it and the daemon lives. yession does not make it. The Colima start
    # wrapper makes `/sys/fs/cgroup/yession` at every boot, with memory.max = VM RAM -
    # 768 MiB (~7 GiB of the 8 GiB VM) and pids.max = 32768. A bare name works under
    # both cgroup drivers: `/yession` under cgroupfs, `yession.slice` under systemd.
    container:
      cgroup_parent: yession
    sandboxes:
      default:
        uses: [ ca ]
        wants: [ github-git ]
    agent:
      guidance: |
        A fresh container's first terminal takes minutes to open while the
        devshell is assembled; wait for it rather than retrying. Push to
        feature branches only.
  '';
in
{
  launchd.agents.yession-manager = {
    enable = true;
    config = {
      ProgramArguments = [
        "${yession}/bin/yession-manager"
        "--auth" "trusted-headers"
        "--socket" managerSocket
        # The default data dir is RELATIVE (`.yession`) and launchd does not start in $HOME.
        "--data-dir" "${config.home.homeDirectory}/.yession"
        "--idle-timeout" "30m"
      ];
      RunAtLoad = true;
      KeepAlive = true;
      EnvironmentVariables = {
        HOME = config.home.homeDirectory;
        YESSION_SESSION_RESOURCES = "${resources}";
        YESSION_MANAGER_URL = origin;
        YESSION_SESSION_URL = "${origin}/s/{id}";
        PATH = "/usr/bin:/bin:/usr/sbin:/sbin";
      };
      StandardOutPath = "${logDir}/manager.out.log";
      StandardErrorPath = "${logDir}/manager.err.log";
    };
  };

  # The proxy, on its socket, re-reading the session map under --watch.
  launchd.agents.yession-proxy = {
    enable = true;
    config = {
      ProgramArguments = [
        "${pkgs.caddy}/bin/caddy" "run"
        "--config" "${proxy}/caddy/Caddyfile" "--adapter" "caddyfile" "--watch"
      ];
      RunAtLoad = true;
      KeepAlive = true;
      EnvironmentVariables = {
        HOME = config.home.homeDirectory;
        YESSION_PROXY_SOCKET = proxySocket;
        YESSION_PROXY_MANAGER = "unix/${managerSocket}";
        YESSION_PROXY_SESSIONS = "${proxyDir}/sessions*.caddy";
        XDG_DATA_HOME = "${proxyDir}/caddy";
        XDG_CONFIG_HOME = "${proxyDir}/caddy";
      };
      StandardOutPath = "${logDir}/proxy.out.log";
      StandardErrorPath = "${logDir}/proxy.err.log";
    };
  };

  # The session routes. KeepAlive: it holds the stream open for its whole life.
  launchd.agents.yession-proxy-map = {
    enable = true;
    config = {
      ProgramArguments = [
        "${pkgs.nodejs_24}/bin/node" "${proxy}/main.mjs"
        "--manager" "unix:${managerSocket}"
        "--as" "proxy-map"
        "--out" "${proxyDir}/sessions.caddy"
        "--empty" "# no running sessions"
        "--template" ''
          @s_{id} path /s/{id} /s/{id}/*
          handle @s_{id} {
            reverse_proxy 127.0.0.1:{port}
          }
        ''
      ];
      RunAtLoad = true;
      KeepAlive = true;
      EnvironmentVariables.HOME = config.home.homeDirectory;
      StandardOutPath = "${logDir}/proxy-map.out.log";
      StandardErrorPath = "${logDir}/proxy-map.err.log";
    };
  };
}
```

Four choices in it are deliberate:

- **User agents, not root LaunchDaemons** — durable secrets ride the login Keychain, which only
  a login session has.
- **The profile and the Caddyfile ride the store** — one switch moves policy and process
  together, and one rollback restores both.
- **The session map does not** — a process writes it per launch, under `.local/state`, and
  nothing else writes there.
- **A plist change restarts the Manager, and that evicts every session.** For frequent
  upgrades, point `--spawn-bin` at a symlink that you promote. Its path is constant, so
  `ProgramArguments` do not change with the input, and sessions roll onto new builds as they
  idle ([§Session lifetime](#session-lifetime)). The proxy and the map ride out a Manager
  restart: the map empties while the Manager is unreachable and refills on the next frame.

#### Keeping the warm store bounded

`nix-container-store` is one volume that every sandbox writes to, so what one session builds,
the next starts with. Nothing in Yession collects it. Only a sandbox can say what it uses, so
the volume carries one **lease** per sandbox, written from inside it
([ADR](decisions/2026-09-30-a-sandbox-leases-what-it-uses-of-a-shared-volume.md)).

Declare `maintain: { pin, every }` on the volume. Each session then runs `pin` in every sandbox
that holds the volume: at start, every `every`, and once more before it removes the sandbox.
The argument is that sandbox's lease directory, `/nix/.yession/leases/<sandbox>`. Collecting is
yours, on your schedule. [examples/nix-store](../examples/nix-store/README.md) has the `pin` and
`sweep` scripts for a Nix store, the profile entry, and how to install and run them.
