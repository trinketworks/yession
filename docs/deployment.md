# Deployment

Before Yession is reachable from another machine, settle three things on the Manager:

- **who** the humans are
- **where** the Manager and its sessions answer
- **how long** connected credentials live

Sessions inherit all three through the environment.

The interfaces are what Yession asks of whatever sits in front of it. The integrations are
worked examples that satisfy them.

---

## Options and variables

The Manager takes what it **decides** as an option. What it **passes down** stays in the
environment, because a child gets it by inheritance. A mistyped setting is therefore refused
where it is a decision, and `--help` is the list.

```sh
yession-manager --help                # what it accepts
yession-manager --check               # what it resolved to, then stop
yession-manager --check --detailed    # the same, plus what each setting and state means
```

`--check` runs every parser this bin has and prints the result. It launches nothing and binds
no port. Each line says where its value came from: `set` (you gave it), `default` (you gave
nothing), or `off` (the feature is not enabled). Use `--check` while you configure a
deployment, because a value alone cannot tell you whether your setting took effect.

The last section of its output lists every `YESSION_*` variable in the environment. A session
inherits all of them, so this list is what a session sees. Nothing is refused for being
unrecognised, because the examples use this prefix for their own variables
(`YESSION_PROXY_PORT`, `YESSION_SERIAL_PORT`).

| shape | who sets it | example |
|---|---|---|
| an option | you, the operator, for this Manager | `--port`, `--idle-timeout`, `--data-dir` |
| `YESSION_MANAGER_URL` / `YESSION_SESSION_URL` | you, and every session reads them too | see §Addressing |
| `YESSION_BIN_*` | you or the package. It names an executable a session runs | `YESSION_BIN_GIT`, `YESSION_BIN_BWRAP` |
| `YESSION_SESSION_*` | you. It is per-session policy, today set once per host | `YESSION_SESSION_WORK_BACKEND`, `YESSION_SESSION_RESOURCES` |
| `YESSION_LAUNCH` | nobody — the Manager mints it per launch | — |

A variable that moved onto the command line is **refused**, not ignored. A boot whose
environment still sets one stops and names the option to write instead.
A setting that nothing reads is the behaviour you asked for, silently not happening. Four
renames once cost weeks of missing reaping, unfollowed upgrades, and refused sandboxes before
anybody noticed.

`YESSION_LAUNCH` is the launch's control secret. It grants custody of the session's secrets
and the right to register as an OIDC client. Setting it by hand impersonates a session. It
never reaches a sandboxed command.

`YESSION_SESSION_RESOURCES` names a **resources profile**. The profile holds named resources
that a repo's `yession.yaml` selects by name, so a repo cannot exceed what the operator
declared. `resources:` is what the host can offer. `always:` is what every sandbox holds
without asking: no repo has to name it and none can decline it. A resource that is declared
and not always granted is available and not granted.

The same file may declare `sandboxes:` in exactly the form a repo's `yession.yaml` uses. One
decoder reads both (`ConfigFile.parseSandboxes` in `src/Yession.Domain/Config.fs`). The same
analyzers (`ConfigAnalysis.fs`) note what is legal but likely unmeant, and the notes are
printed at boot.

Each sandbox in the file exists in every session on this host from boot, on the backend
configured for the session's own sandboxes. These are ALL of a session's own sandboxes
(`WorkSandboxes.create`). No sandbox is built in. `default` is where a terminal that names no
sandbox opens, and it exists only if you declare it. A session on a host whose profile
declares none has nowhere to run a command until a repo's sandbox starts. Declare `default`
with what it should hold: `wants: [ github ]` for `git push` from a terminal.

The same file carries what you say about the agent, under `agent`. `agent.guidance` is
appended after the product's own system prompt on every turn, introduced as the operator's. It
never replaces that prompt. The prompt describes the build's tools and sandboxes, and a copy
in your file would describe the build you wrote it against. Write only what this host knows:
its conventions, what is slow here, and what must never be pushed to which place.

`agent.prompt` picks how that prompt is put together, from the strategies the build has:

- `static`, the default: the product's sections and your guidance as one system prompt, and
  the turn's context (the time, the conversation, terminal activity, repo notes) as one
  message.
- `claude-code-like`: Claude Code's layout. The product's sections come first and are the
  same on every host. They include one more section from Claude Code: look at a UI change in
  a headless browser before you call it done, or say that you did not look. Your guidance, and
  a note on tools from other MCP servers, follow the SDK's dynamic boundary. The turn's
  context arrives as `<system-reminder>` blocks beside the message the turn answers.

A session reads the file at boot and reports on its stderr which strategy it chose. To say
nothing about the agent, leave the block out. A block that says nothing is refused, and so is
a strategy that the build does not have.

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
| `trusted-headers` | The proxy in front asserts the user in canonical `x-yession-*` headers, trusted verbatim. |

An unknown name or option fails the boot, because a silently ignored `--auth` would give a
Manager that refuses everyone. Every bin answers `--help`.

`trusted-headers` replaces `localhost`. The two never compose. Behind a proxy that terminates
on loopback, every request is loopback, so a header-less request would become `local`. That is
the bypass the proxy exists to prevent.

#### What the trust rule decides about credentials

The rule also decides who **owns** a Claude or GitHub account connected for "all my sessions":

| `--auth` | "All my sessions" means |
|---|---|
| `trusted-headers` | That user. Nobody else's turn runs on it. |
| `localhost` | This deployment. One connection serves every visitor, browser, and device. |

Under `localhost`, this is the rule stated honestly: anyone who reaches the Manager can
already drive every session. Before you choose it, read
[what it costs](#what---auth-localhost-costs-here).

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

Durable secrets are one AES-256-GCM file in the data directory. The key comes from the macOS
Keychain, the Windows Credential Manager, or the Linux Secret Service. There is no key file and
no key variable. A host without a credential manager therefore refuses persistence instead of
degrading it.

`--secrets ephemeral` is the compensating control for `localhost`: the deployment-wide
credential lives only as long as this Manager process.

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

The Manager is also the OIDC issuer that sessions redirect users through, so
`YESSION_MANAGER_URL` must be an origin that browsers can reach. Otherwise remote logins land
on an address that only the host resolves.

### Session lifetime

```sh
yession-manager --idle-timeout 30m       # or 90s, 2h; absent means never
```

The Manager stops sessions that nobody uses. A session reports busy or idle over its control
channel (a connected peer, a running turn, a command, a non-empty queue). The Manager reaps on
**silence**, so no single report has to arrive. Absent means never.

Reaping has two costs:

- A reaped launch loses its OAuth client registration and user bindings, so the next visitor
  signs in again. This is invisible under `localhost` and a repeat redirect under
  `trusted-headers`.
- A session that wedges after readiness is stopped as `NeverReported` and not diagnosed.

Reaping also gives you rolling upgrades. Point `--spawn-bin` at a path that floats with your
builds. Sessions then upgrade as they idle out. The Manager waits until nothing runs, because
its restart evicts everybody. A MAJOR version difference refuses the launch, since the control
protocol may disagree. The refusal drains the running set and hands you that moment.

### Addressing

A deployment is either **loopback** (nothing set, everything on `127.0.0.1`) or **fronted**
(both set, everything public). A half-set pair is refused at boot, because either half alone
points somebody at loopback:

- Sessions without their issuer bounce remote logins to `127.0.0.1`.
- A public issuer without fronted sessions registers callbacks that nobody remote can reach.

```sh
YESSION_MANAGER_URL=https://example.com          # the Manager: scheme + host, no path
YESSION_SESSION_URL=https://example.com/s/{id}   # sessions: a template
```

#### The Manager

Use scheme + host, with an optional port and **no path**. The Manager's routes are
origin-anchored and its issuer is a concatenation base, so it rejects a prefix. That is what
lets the Manager share an origin with its sessions: the Manager owns `/` and sessions own
`/s/*`.

The origin must resolve **from this host too**. Every session runs OIDC discovery, JWKS, and
token requests against it at boot, and a session that cannot authorize its users refuses to
half-start. Split-horizon DNS or a proxy on an external interface only fails registration.

#### Sessions

The URL is a template over `{id}` (the session id) and `{port}` (its OS-assigned port). These
are exactly what the registry stream publishes, so any proxy that the stream drives can
implement any template:

```sh
YESSION_SESSION_URL=https://example.com:{port}         # a port mirrored per session
YESSION_SESSION_URL=https://{id}.sessions.example.com  # a subdomain per session
YESSION_SESSION_URL=https://example.com/s/{id}         # a path per session
```

A template with no placeholder is refused, since every session would share one address.
`{port}` may appear in the authority but never in the **path**. A session must know
its mount before it binds, because the mount fixes its `<base href>`, its cookie `Path`, and
the prefix it strips.

#### Prefer `{id}`

A `{port}` template gives a session a new origin per launch, and the browser partitions
storage by origin. Anything written while the session was away is stranded in a database that
nothing reopens. The shell then emits `<meta name="yession-ephemeral-storage" content="1">`,
and the client stops promising a sync that it cannot deliver. Under `{id}`, the tag is absent
and the promise holds. Everything already sent is on the server either way.

#### The registry stream

The stream sends full snapshots, never deltas. A subscriber applies each frame whole, and a
missed frame costs nothing:

```sh
curl -sN http://127.0.0.1:8321/sessions/stream
# data: {"sessions":[{"id":"local-session","name":"…","port":57239,"pid":95225}]}
```

The trust rule gates the stream like every management route. Under `trusted-headers`, every
loopback caller asserts its own `x-yession-user`: a proxy binding, a health check, or a
tracker. A caller that does not gets 401s and no frames.

The template is not on the wire. The stream carries `id` and `port`, and the deployment
applies its template, so the prefix has one home. [`examples/proxy/main.mjs`](../examples/proxy/)
is a reference reconciler. §Tailscale composes it.

---

## Integrations

### GitHub

A session clones over HTTPS and watches pull requests with a credential that a person signs in
for from inside the session. There is no default client id. Register your own GitHub App, or
the only way in is to paste a token.

Register an App (not an OAuth App), and:

- **Enable device flow** — the session runs this flow. The code exchange needs a client secret
  that the Manager's public-client broker cannot carry.
- **Set no callback URL that matters, generate no client secret** — neither is used.
- **Install it on the repositories it should reach** — a device-flow token sees the
  intersection of the user's access and the App's installations. Nothing in Yession re-checks
  that.
- **Give it the repository permissions a session spends**, because a user-to-server token
  can do no more than the App was registered to do. What each verb needs:
  - *Contents: read and write* — cloning, fetching and pushing through the git gateway, and
    a merge, which lands commits on the base branch.
  - *Pull requests: read and write* — watching one, opening one (`create_pr`), and merging
    one (`merge_pr`: auto merge, the merge queue, and a direct merge alike).
  - *Checks: read* — the check-runs rollup a watched pull request reports as its checks.
  - *Metadata: read* — granted to every App. The picker reads the repository listing.
  - *Email addresses: read* (an account permission), optionally. The commit identity that a
    block's git receives comes from the profile. Without this permission, the email is the
    account's `noreply` address and not the one the account publishes.

  A missing permission is not a refusal at sign-in. The token is minted regardless, and the
  verb that needed the permission fails. GitHub's 403 or 404 is passed through in words.
- **Leave user-token expiration on** — the Manager holds the refresh token and rotates it
  before each turn. With expiration off, the token is permanent, and nothing tells you which
  setting you registered.
- **Point its webhook at `<YESSION_MANAGER_URL>/hooks/github`** if you declared a hook
  endpoint (§Webhooks), with the secret the manager page shows. Polling still works without
  the hook. A hook only advances its clock.

Then name the client id:

```
YESSION_GITHUB_CLIENT_ID=Iv1.0123456789abcdef
```

If you leave it unset, the sign-in surface says so and offers the paste path. A pasted
`github_pat_…` or `ghp_…` token works and **bypasses the installation rule** (recorded in
GAPS.md). A pasted `ghu_…` or `gho_…` token is refused where device flow exists, since it
expires in hours and cannot rotate.

`YESSION_GITHUB_DEVICE_URL`, `YESSION_GITHUB_TOKEN_URL`, `YESSION_GITHUB_USER_URL` and
`YESSION_GITHUB_API_URL` override the four endpoints. The test suites use them to drive the
integration.

### Webhooks

The Manager takes signed deliveries and hands them to the sessions that asked, without reading
them. A session declares a filter over paths, and the Manager matches it
([ADR](decisions/2026-08-29-the-manager-relays-hooks-it-cannot-read.md)).

```sh
yession-manager --webhook github --webhook linear
```

The Manager serves each endpoint at `<YESSION_MANAGER_URL>/hooks/<name>`, so this needs a
fronted deployment. The manager page shows, for each endpoint, the address and the secret to
sign with. The secret is derived from the credential manager's key and is not chosen. For this
reason, endpoints are refused under an ephemeral store: a secret that changed at every restart
would break deliveries silently.

To rotate, bump the counter. The Manager accepts both the new secret and the one before it
until you bump again:

```sh
yession-manager --webhook github@1
```

The manager page shows each endpoint's declaration back to you, canonicalised. This is the
only place where the current rotation is visible, because it appears in neither the address,
the secret, nor the header.

Verification is HMAC-SHA256 over the raw body, in hex, in `X-Hub-Signature-256` behind
`sha256=` (the WebSub convention, which GitHub uses). A sender that signs the same way with
another header, encoding or prefix needs an override, as Shopify does. To override it for one
endpoint, add the override to the same option after an `=`:

```sh
yession-manager --webhook 'shopify=x-shopify-hmac-sha256:base64:'
```

The grammar is `name[@rotation][=header:encoding[:prefix]]`. A name is letters, digits, `-`,
and `_`, so neither separator can appear in a name. The signature part splits on its first two
colons only, so a prefix may hold anything. This includes `sha256=`, which is why the option
splits on its FIRST `=`.

Not supported: schemes that sign a timestamped string (Stripe `<t>.<body>`, Slack
`v0:<ts>:<body>`). These need the scheme itself, not configuration.

### Tailscale

The deployment has one origin, behind one reverse proxy of your own. The Manager answers at `/`
and each session at `/s/<id>`:

```
browser ──tailnet──▶ tailscale serve ──▶ your proxy ──┬──▶ manager      /
                     TLS + identity      one mapping    └──▶ session i    /s/<id>
```

`serve` does what only it can through one mapping: TLS on the tailnet, and who is calling.
Routing, identity translation, and stripping live in the proxy, in a file. The pieces are in
[`examples/proxy`](../examples/proxy/). This section describes what they implement, for a
deployment on another proxy.

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
renames them. With Caddy, put this on the Manager's route:

```caddyfile
reverse_proxy 127.0.0.1:8321 {
	header_up x-yession-user         {header.Tailscale-User-Login}
	header_up x-yession-user-name    {header.Tailscale-User-Name}
	header_up x-yession-user-picture {header.Tailscale-User-Profile-Pic}
	header_up -x-yession-user-email
	header_up -x-yession-user-claims
	header_up -tailscale-*
}
```

- **Set what `serve` asserts, delete what it does not** — a set replaces the client's value, so
  the three sets are the strip for those three. Email and claims must be deleted by name, or a
  client chooses its own.
- **Delete by name, not `-x-yession-*`** — Caddy applies deletions after sets, so the wildcard
  strips what was just asserted and the Manager sees nobody. This was measured, and the
  fronted browser case in the suite keeps measuring it.
- **The Manager is reachable only through the proxy** — under `trusted-headers` the header
  *is* the subject, so on a shared box bind the Manager where only the proxy can dial.

Session routes carry none of this. A session authenticates through the Manager as OIDC issuer,
never by header, so a proxy that can only forward, not translate, still fronts sessions
correctly.

**Loopback callers assert themselves.** The reconciler, a health check, and a tracker each
send an `x-yession-user` that names what they are (`main.mjs --as proxy-map`), because the
gate is the same for a request that never left the machine. Under `localhost`, nothing reads
the header.

> Verified against a live tailnet: Tailscale 1.102.3 with HTTPS certificates, macOS. The
> verification covered the identity headers, the overwrite, the `x-yession-*` pass-through, the
> deletion ordering, and the composition below with a Manager and a session behind it. Every
> pull request re-verifies the Caddy half, against whichever Caddy the dev shell pins.

##### What `--auth localhost` costs here

`serve` terminates on loopback, so every tailnet visitor is loopback and becomes the one
unattributed subject `local`. Nothing errors. The audit trail says `local` for everyone. A
Claude account connected for "all my sessions" belongs to the deployment, so any visitor's turn
spends against it. This is coherent on a personal tailnet. It is a silent failure when a second
person arrives.

Two bounds answer different questions:

- **`--secrets ephemeral`** — keep the sharing, lose the permanence: the credential dies with
  this Manager process.
- **`--auth trusted-headers`** — lose the sharing: each human owns their own credential.

#### Addressing

Make one mapping, once. The `serve` config persists in tailscaled's state:

```sh
tailscale serve --bg --https=8321 9000        # everything -> your proxy
```

```sh
YESSION_MANAGER_URL=https://host.example.ts.net:8321 \
YESSION_SESSION_URL=https://host.example.ts.net:8321/s/{id} \
  yession-manager --auth trusted-headers
```

Both URLs name the tailnet origin. The Manager is the issuer that sessions redirect users
through, and a loopback issuer sends remote logins to this machine only.

The proxy routes `/s/<id>` to the session's port **without stripping the mount**. A session
serves under its mount: it answers at `/s/<id>/…` and returns 404 at `/`. In Caddy, therefore,
use `handle`, never `handle_path`, with the bare port upstream.

Use `--https` where the tailnet issues certificates. Derive the scheme in both URLs from one
variable, or the Manager and its sessions become two origins. `--https` also matters without
certificates as the reason: a browser withholds the Cache API and service workers outside a
secure context, so over plain HTTP a session keeps no history on the device and cannot open
cold offline. Loopback is a secure context, so the default is unaffected.

##### Keeping the session routes in step

A session's port is OS-assigned per launch, so a process, not a person, writes its route.
`examples/proxy/main.mjs` renders every running session through a template into one file that
the proxy reads. [`examples/proxy`](../examples/proxy/README.md) has the full command, with
the Caddy template and `--reload` for a proxy that does not watch its config.

Caddy under `--watch` re-adapts every second and re-reads the import. Any reconciler against
the stream should keep three rules:

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

`packages.<system>.default` is `yession-manager` and `yession-session`. They are wrapped with
the native WebRTC addon, which is built from source, and with `YESSION_BIN_CLAUDE` and
`YESSION_BIN_GIT` defaulted to store paths. The module below comes from the deployment that
the project itself runs: home-manager under nix-darwin, composed with §Tailscale.

Pin the input and leave its nixpkgs alone, because the addon is built against that pin:

```nix
inputs.yession.url = "github:trinketworks/yession";
# NOT `inputs.yession.inputs.nixpkgs.follows = "nixpkgs"`: that trades a cached,
# tested native build for an untested one.
```

Three agents make up all of §Tailscale. They use the example's files as they ship:

```nix
{ config, pkgs, inputs, ... }:
let
  yession = inputs.yession.packages.${pkgs.stdenv.hostPlatform.system}.default;
  proxy = "${inputs.yession}/examples/proxy";   # the Caddyfile and the reconciler
  origin = "https://host.example.ts.net:8321";  # scheme + host once, feeding both URLs
  managerPort = 8321;
  proxyPort = 9000;           # what `tailscale serve --bg --https=8321 9000` targets
  proxyDir = "${config.home.homeDirectory}/.local/state/yession/proxy";
  logDir = "${config.home.homeDirectory}/Library/Logs/yession";

  # The resources profile. Paths as the kernel sees them (/private/etc, not /etc):
  # the profile refuses symlinked spellings. `nix-container-store` is what this
  # repository's own yession.yaml reaches with `wants:`, and so is `github`, the
  # connection that lets `git push` and `gh` in a sandbox act as whoever each
  # command runs for.
  resources = pkgs.writeText "yession-resources.yaml" ''
    version: 1
    resources:
      nix-container-store:
        volume: { name: yession-nix, at: /nix }
      # What a repo's CONTAINER sandboxes want: the gateway, and the credential proxy
      # with what a container needs to use it — the proxy's URL, its trust bundle (every
      # root, so replacing the image's loses nothing) and a token lent per command.
      github:
        connection: { github: [ git, api ] }
        env:
          HTTPS_PROXY: ''${proxy.https}
          SSL_CERT_FILE: ''${proxy.ca-file}
          GH_TOKEN: ''${github.token}
          GITHUB_TOKEN: ''${github.token}
      # `default`'s: git through the gateway, and nothing else. `default` runs under srt,
      # which takes a connection by git alone; the API is the containers' job.
      github-git:
        connection: { github: [ git ] }
      ca:
        mount: { from: /private/etc/ssl/cert.pem, mode: read }
        env:
          NIX_SSL_CERT_FILE: /private/etc/ssl/cert.pem
    # The session's own sandboxes. Nothing makes a `default` up: this is where a
    # terminal that names no sandbox opens.
    sandboxes:
      default:
        uses: [ ca ]
        wants: [ github-git ]
    # Appended after the product's system prompt, as the operator's words. Host
    # conventions only — the prompt already covers the tools. `prompt:` would
    # pick the strategy; left out, it is `static`.
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
        "--port" (toString managerPort)
        # The default data dir is RELATIVE (`.yession`) and launchd does not start in $HOME.
        "--data-dir" "${config.home.homeDirectory}/.yession"
        "--idle-timeout" "30m"
      ];
      RunAtLoad = true;
      KeepAlive = true;
      EnvironmentVariables = {
        HOME = config.home.homeDirectory;
        YESSION_SESSION_RESOURCES = "${resources}";
        # The two addresses stay variables: every session reads them too.
        YESSION_MANAGER_URL = origin;
        YESSION_SESSION_URL = "${origin}/s/{id}";
        PATH = "/usr/bin:/bin:/usr/sbin:/sbin";
      };
      StandardOutPath = "${logDir}/manager.out.log";
      StandardErrorPath = "${logDir}/manager.err.log";
    };
  };

  # The proxy, loopback only, re-reading the session map under --watch.
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
        YESSION_PROXY_PORT = toString proxyPort;
        YESSION_PROXY_MANAGER = "127.0.0.1:${toString managerPort}";
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
        "--manager" "http://127.0.0.1:${toString managerPort}"
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
  together, one rollback restores both, and no live file drifts.
- **The session map does not** — a process writes it from state that changes per launch, so it
  lives under `.local/state` and nothing else writes there.
- **A plist change restarts the Manager, and that evicts every session.** For frequent
  upgrades, point `--spawn-bin` at a symlink that you promote. The symlink path is a constant
  string, so the agent's `ProgramArguments` do not change when the input does. Sessions then
  roll onto new builds as they idle (§Session lifetime). The proxy and the map ride out a
  Manager restart: the map empties while the Manager is unreachable and refills on the first
  frame after.

#### Keeping the warm store bounded

`nix-container-store` is one volume that every sandbox writes to. This is the point: what one
session builds, the next session starts with. Nothing ever collects the volume. A collector
that runs from outside cannot do it safely. Each sandbox's roots point into its own checkout,
and what its processes hold open is in its own process table, so from anywhere else they look
like garbage. The volume therefore carries one **lease** per sandbox, written from inside the
sandbox. Your sweep reads the leases:

```yaml
nix-container-store:
  volume:
    name: yession-nix
    at: /nix
    maintain: { pin: /nix/var/yession/pin, every: 10m }
```

Each session runs `pin` in every sandbox that holds the volume: at start, every `every`, and
once more before the sandbox is removed. The argument is that sandbox's lease directory,
`/nix/.yession/leases/<sandbox>`. Collecting is yours, on your schedule.
[examples/nix-store](../examples/nix-store/) has the pair of scripts for a Nix store, and its
README says how to install them into the volume and run the sweep.
[docs/decisions/2026-09-30-a-sandbox-leases-what-it-uses-of-a-shared-volume.md](decisions/2026-09-30-a-sandbox-leases-what-it-uses-of-a-shared-volume.md)
explains why the work is split this way.
