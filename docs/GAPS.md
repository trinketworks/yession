# Known gaps

This file lists what Yession does **not** do yet, as of the `9.x-beta` line. Everything below is
deliberate scope, recorded so nobody discovers it in production.

Phases 1–4 are accepted. So is every numbered plan this file cites. The plan files no longer
exist. Each plan's reasoning now sits beside the code it governs. A plan number joins two entries
in different sections to one piece of work. The plans and topics are:

- Client presentation: Metro/Zune styling, rich-text editing, collaborative presence cursors.
- The Manager→Session control-RPC reverse legs.
- Plan 04: telemetry.
- Plan 06: secrets + ABAC.
- Plan 07: BYO user authorization.
- Plan 08: connections and Claude sign-in.
- Plans 09/10/12: remote and mounted session access.
- Plan 11: idle reaping.
- Plan 13: terminals on the WorkSandbox (every stage).
- Plans 14/15: repo integration and the imperative session API.
- Plans 16–19: declared MCP servers and the providers that answer them.
- Plan 20: a session that opens cold.
- Plan 21: credential rotation.
- Plan 23: the classifier seam that every act passes.
- Plan 24: srt's read model.
- Plan 27: a repo's own sandbox declarations.

Items are roughly ordered WITHIN a section by how much they matter. The sections are not ranked
against each other. The last section holds trust boundaries as sharp as the first section's.

## Security & trust

- **A lent token rotates per command, and whether that earns its keep is open.** A declaration's
  `${github.token}` (`EnvironmentVariableRef.Lent`) is a stand-in. It is lent to each command for
  the act the command runs as, and returned when the next command starts.
  - Gain: a stand-in that leaks dies with the next command. Each command spends the credential of
    whoever it runs for, never whoever ran the one before.
  - Cost: the value can only be a variable's whole value. The parser refuses a stand-in composed
    into a larger string and points here. Anything that keeps a token across commands holds a
    dead token after the next command, and is refused in words from then on. Examples: a daemon
    started by one command, and a tool that writes the token into its own config.
  - Alternative: a stand-in per sandbox, resolved per request by whoever the current command
    runs for, would keep the attribution without the rotation.
  - Open: nothing here has measured whether the rotation is worth what it breaks.
- **Event attribution is threaded, but presentation is thin** (Plan 07). Under a real strategy,
  events attribute to the Manager-verified user (`ActorRef.UserRef`). The identity rides the OIDC
  bounce, then a cookie, then a session-minted peer token. It never rides a peer-controlled frame.
  Remaining:
  - Peer display names stay self-asserted.
  - The client UI renders a bare `UserId.value`. It shows no names or avatars from verified claims
    yet. `UserClaims` are carried and recorded, not displayed.
- **BYO authorization is trusted plaintext headers** (Plan 07). `--auth trusted-headers` trusts
  canonical `x-yession-*` identity headers from an operator-run authenticating proxy. Anyone who
  can reach the loopback port directly can forge them. This is the same trust boundary as
  `--auth localhost`. The proxy MUST strip inbound `x-yession-*` headers and be the only non-local
  path in.

  The recorded hardening follow-up is a signed-JWT header strategy. It would verify against an
  operator JWKS. `Fable.Jose.jwtVerify` is already bound.
- **No `nonce` in ID tokens yet, and the flow is why that is tolerable** rather than an oversight.
  The ID token is never delivered through the browser. The confidential RP redeems the code over
  its own back channel, with its client secret plus the PKCE verifier. So no injected-token path
  exists for a nonce to close. A nonce becomes worth adding the first time a strategy federates to
  an upstream OP.
- **A declared MCP server's tool descriptions are untrusted text in the model's context**
  (Plan 16, Plan 17). An external server's `tools/list` descriptions go straight into the prompt.
  With always-available servers, they do so without a second human confirming. Yession drops
  `instructions` for exactly this reason. It cannot drop tool descriptions, because the model must
  read them to call anything.

  `ToolDescriptor.Foreign` already marks the affected set. The recorded mitigation is an
  `AutoApprove` flag on the DECLARATION, where the operator already is, rather than a per-call
  prompt.
- **A declared MCP server is unconfined, and so is what it owns** (Plan 16). No srt or docker
  analogue exists for a serial port. The serial provider (`examples/serial`) runs on the host with
  whatever access its user has. A device is more physical than a filesystem path: writing to the
  wrong tty can reflash a board.
  - The provider narrows this by refusing to list ports it does not recognise. An unrecognised tty
    is usually the machine's own console. This is a policy, not a boundary.
  - Its control leg is unauthenticated and must stay on loopback.
  - A stream it OFFERS (Plan 19) is admitted the same way. The url must share the host the
    operator declared the server at, use `ws` or `wss`, and carry no credentials. This stops a
    tool result from pointing a session at another machine. It is again a policy, not a boundary.
  - A declared server can still hand the session a socket to anything on the box it already runs
    on.
- **A second local address, unauthenticated, beside the provider** (Plan 18). The jumpstarter
  example talks to an exporter that serves gRPC with `--tls-grpc-insecure` and no passphrase. So
  its claim arbitrates the provider's clients and nothing else. Any process on the box can dial
  the exporter directly and take the hardware out from under a holder.

  One host, one operator and loopback make that acceptable. A shared machine would need the
  passphrase that upstream already supports. A controller would replace the claim with a lease
  outright.
- **A driver method that never returns wedges one connection** (Plan 18). The jumpstarter example
  calls the SDK on a thread of its own, and nothing can interrupt a library call from outside. A
  method that blocks forever, or a stream whose next item never arrives, gets a timeout answer.
  The example then drops that connection so later calls reconnect.

  But the thread stays parked on the call and holds one SDK client until the process ends. The
  cost is bounded: one thread per wedge, and only a driver that misbehaves can cause one. The
  answer shows the fault. The fault is not fixed.
- **A provider's lifecycle is nobody's** (Plan 16). Who starts a provider is unsettled: `systemd`,
  `launchd`, an operator, or nothing. "The Manager only declares" argues for nothing. Plan 17's
  poll softens this. It retries a server forever and picks it up whenever it appears, so nothing
  has to restart to notice. But nothing starts the provider either.
- **Remote WebRTC has no relay fallback** (Plan 09). Sessions are remotely reachable through an
  operator's proxy. The `/sessions/stream` registry drives the serving binding.
  `YESSION_SESSION_URL` is a template over `{id}`/`{port}` (Plan 10). It threads the public
  address into open links and redirect URIs. This works whether the operator mirrors ports, gives
  each session a subdomain, or mounts each under a path. Two limits remain:
  - The data channel still connects peer-to-peer on host candidates only (no STUN/TURN). Remote
    use needs a network where the session host's addresses route directly. An example is an
    overlay like a tailnet, verified per deployment.
  - An unauthenticated visitor can hold open peer connections that `PeerHello` refuses. No
    `/signal` throttling exists.
- **User- and local-scoped secrets have exactly one writer: the connection broker** (Plan 08). The
  Claude sign-in stores an owner-scoped credential through the narrow `ConnectionAction` policy
  family. Under an attributed strategy, the owner is the user. Under `--auth localhost`, the owner
  is `LocalScope`, the deployment itself ([ADR](decisions/2026-08-10-local-scope.md)). Remaining:
  - The GENERIC `/secrets` write surface for users is still absent. Sessions cannot `SetSecret` on
    `UserScope`, and the management UI has no secrets page.
  - The policy rows for a session-less, user-only `AuthzSubject` (`Session = None`) exist, and
    tests pin them. Nothing constructs that subject yet.
- **Under `--auth localhost` a connected credential is deployment-wide.** Every visitor is the
  same unattributed subject. One Claude/GitHub connection serves every session and browser that
  reaches the Manager, and every visitor's agent turn spends against it. This is the same boundary
  as the rest of that trust rule: those visitors can already drive every session. Event
  ATTRIBUTION is unaffected, because authors stay `PeerRef`.

  Both exits are in [deployment.md](deployment.md#what---auth-localhost-costs-here).
  `--secrets ephemeral` bounds the lifetime to the Manager process. `--auth trusted-headers`
  removes the sharing.
- **No transport encryption guarantees beyond WebRTC/DTLS.** Everything binds 127.0.0.1. Loopback
  HTTP is the RFC 8252 pattern. Nothing here is LAN-safe without the operator's proxy in front.
- **The Manager and Process read plaintext**, per the stated Phase 1–2 threat model.
  Command-to-container encryption is designed for but not implemented.
- **The management UI is gated by the authentication strategy** (Plan 07). A `Denied` outcome is a
  401 on every UI route, and the default `--auth none` denies everything. Under `--auth localhost`
  anyone with local access can still manage sessions. That is the localhost trust rule working as
  stated, not an oversight.
- **Sandboxes confine by default. `host` is the opt-out.** The WorkSandbox defaults to `srt`
  (bubblewrap on Linux, Seatbelt on macOS). Unless an operator asks for something else, OS
  confinement applies to agent-issued commands. It does not apply to the agent CLI. The
  AgentSandbox defaults to `host` (`YESSION_SESSION_AGENT_BACKEND`, read once in
  `app/SessionMain.fs`). The agent CLI entry below says why.

  `YESSION_SESSION_WORK_BACKEND=host` still exists. It gives no filesystem or network
  confinement, only the env allowlist, which the credential-leak regression test pins. An
  operator must now choose it deliberately. `=docker` remains the full-userland option for the
  WorkSandbox.
  - **A Linux host without the confinement tools cannot start a session.** srt needs bubblewrap,
    socat and ripgrep. The Nix installable names all three. An `npm i -g yession` on a bare box
    does not have them. The session then fails when it creates its sandbox, and does not start
    unconfined. That is the intended trade. To proceed, install the tools or choose `host`
    deliberately.
  - **srt reports a probe it could not RUN as a tool that is not there.** srt's dependency check
    is not a stat. It forks `which` under a one-second timeout, and reports every way that fork
    can fail as `<tool> not found`. So a bad minute makes srt refuse every sandbox in a run, on
    one line, about a file that sits right there, executable. The comment on `StartFailure` in
    `app/Sandboxes.fs` holds the cause.

    Yession therefore reads a refusal against this process's own stat before it believes the
    refusal. A start that settled nothing is asked again (three times, 250ms apart) and is then
    forgotten, not memoized into every later sandbox. What Yession reports contradicts srt's
    sentence instead of repeating it. Undo this when srt stats an absolute path (still `which` in
    0.0.73) or at least surfaces the spawn's errno.
  - **An srt sandbox reads what its policy names and nothing else** (Plan 24). srt's read model is
    permissive by default. Until Plan 24, Yession denied only the invoking user's home. That left
    every region nobody had thought to name readable by every agent-issued command: `/etc`, a
    checkout the session was never given, and, when the Manager runs outside the operator's home,
    another session's data directory.

    The deny is now `/`, which srt re-expands from the children of `/` at each spawn. It has three
    holes: the policy's read paths, everything the policy may write, and the host runtime.
  - **The host runtime is the half of that scope this code cannot derive.** An interpreter can be
    anywhere. So the allow-back is a platform list (`SrtTools.Runtime`) plus the install prefix of
    whatever is already running:
    - `process.execPath`
    - srt's own package (its wrapped argv execs a vendored helper from inside the sandbox)
    - `YESSION_BIN_CLAUDE` and `YESSION_BIN_GIT`

    A toolchain that none of these finds is now a RESOURCE. An operator declares it and a sandbox
    selects it. It arrives through the policy, not through this list. `runtimeReadPaths` in
    `app/Sandboxes.fs` says why `YESSION_SESSION_READ` is no longer folded in here. A path that
    nothing names fails loudly and locally: a command cannot find its interpreter.
    - **The darwin list is unverified.** No job here runs a suite on darwin. The `Srt` tier pins
      `linuxRuntimePaths`. `darwinRuntimePaths` is what a Seatbelt profile conventionally allows
      back, checked against srt's implementation and nothing else.

      This has already cost one release. With the read scope on, every git verb on macOS ran
      PATH's `/usr/bin/git`. That is not git but a shim that resolves a developer directory
      through a `/var/select` symlink. srt's macOS escape hatch allows metadata on DIRECTORIES, so
      the symlink stayed denied. Sessions reported
      `xcode-select: error: unable to read data link … (Operation not permitted)`, which reads as
      a broken Xcode install and is not one. The fix was to stop asking the host which git to run
      (below), not to widen the scope for a shim.
  - **Every binary a confined spawn execs is NAMED. Git was the last exception.**
    `YESSION_BIN_GIT` joins `YESSION_BIN_BWRAP`, `YESSION_BIN_SOCAT`, `YESSION_BIN_RIPGREP`,
    `YESSION_BIN_SETSID` and `YESSION_BIN_CLAUDE`. The installable sets it on both platforms,
    unlike the Linux-only srt tools. Unset, the verbs still fall back to PATH, so an off-Nix
    install does not regress.

    But an `npm i -g yession` on macOS is exactly where that fallback is wrong. So the git sandbox
    proves `git --version` before any verb runs one. It refuses with a sentence that names
    `YESSION_BIN_GIT` and this host's resources profile, instead of passing the host binary's
    excuse through.
    - **A denied read is ENOENT on Linux and EPERM on macOS.** A whole class of fault is
      invisible on the platform CI runs: git shrugs at the first and dies on the second. That
      asymmetry once shipped a probe that refused a working git. For that reason the regression
      test plants a MALFORMED config, not an unreadable one
      ([ADR](decisions/2026-08-28-one-function-builds-every-git-spawn.md)).
    - **`/proc` and `/sys` are outside the scope by construction.** srt's root-deny expansion
      skips both. srt remounts `/proc` itself, and a tmpfs over `/sys` breaks tooling for a tree
      that is read-only anyway. Neither is a route back to the denied paths. `/proc/<pid>/root`
      does not resolve to the host root from inside, and bwrap gives the sandbox its own pid
      namespace. But neither is scoped either.
    - **srt's own default write paths reach into the denied home.** `getDefaultWritePaths()`
      always allows `~/.npm/_logs` and `~/.claude/debug`. srt re-binds an allowed write path that
      its read-deny tmpfs wiped. So both are readable AND writable from inside a sandbox whose
      policy names neither. The home they resolve against is the Session Process's
      `os.homedir()`. The fix is therefore that process's own `HOME`, not the policy.
    - **A symlink INSIDE a granted directory is still denied, and
      srt work sandboxes do not run builds.** Seatbelt matches paths as written, while srt
      normalises an allow to its realpath. So on macOS:
      - A granted path can be unreachable by the spelling a process uses.
      - Symlink nodes en route (`/tmp`, `/etc`, `/var`) refuse lstat.
      - MSBuild's hardcoded `/tmp/MSBuild<pid>` pipes cannot bind.

      A fork of srt that fixed all of this was built, verified end to end, and retired anyway. The
      next wall was Fable enumerating `/Users` for path casing. No grant can allow that without
      exposing dirent names all the way up. That wall showed that a build wants a machine, not a
      syscall filter.

      Work that builds runs in a CONTAINER. srt confines the light default sandbox (checkout,
      small edits), where none of these faults bite. What a light sandbox wanted from an
      unreadable config is declared instead (`NIX_CONFIG` in `yession.yaml`). The
      [2026-08-28 ADR](decisions/2026-08-28-no-srt-fork-for-symlink-metadata.md) argues this, and
      its argument stands.
    - **A `Socket` grant is path-scoped on macOS and widens to every socket on Linux.** #335 gave
      a socket its own axis. srt honours it as `network.allowUnixSockets`. That is a list of paths,
      which the Seatbelt profile turns into `network-outbound` rules. On Linux, srt filters unix
      sockets with seccomp-bpf, which cannot read a socket path out of user-space memory. So
      `wrapCommandWithSandboxLinux` takes `allowAllUnixSockets` and never reads the path list at
      all. Every `AF_UNIX` socket is blocked, or none is.

      Yession lets every socket through when a policy grants one, and says so. `limitsFor` records
      that srt on Linux cannot scope a socket by path, so the host realises a `Socket` leaf as
      `Coarsened "sock:*"`. The policy carries that in `Realisation`. `SrtSandbox.configFor` reads
      it and sets `allowAllUnixSockets`, and only for a policy that reported the widening. The
      grant therefore works on Linux, but the sandbox holds every unix socket and not only the one
      the operator named. The report reaches three places:
      - The approval prompt shows `sock:<path> ~> sock:*` (`RealisedClosure.describeOn`).
      - The `WorkSandboxStarted` event records the lines, and the start note shows them as
        "adjusted".
      - The `work_sandboxes` panel has a `degraded` column for the running sandbox.

      The widening itself is not fixed. A socket the operator meant to scope is every socket on
      Linux, and the report only tells people so. Scoping by path there needs a mechanism that
      seccomp cannot give srt. The Srt-tier case that pins the macOS path scoping reports a visible
      skip on Linux. The cheap tier pins the Linux side: the policy reports the widening, and
      `configFor` sets `allowAllUnixSockets` only for that report.
    - **A granted executable that cannot RUN is invisible to the start-up checks.** Measured: the
      `nix` an operator would name on nix-darwin (`/run/current-system/sw/bin/nix`, canonically
      `…-nix-2.34.6+1/bin/nix`) aborts under Seatbelt on every subcommand, including `--version`.
      The Lix binary from this repo's own dev shell runs fine in the same sandbox. (The
      [2026-08-28 ADR](decisions/2026-08-28-no-srt-fork-for-symlink-metadata.md) has the
      measurement table.) An `Exec` leaf joins PATH in front. So naming that binary makes it the
      one that answers, and a working nix becomes a broken one by being declared.

      The checks (#333) do not catch it and cannot as written. An exec leaf lands in the read
      paths, so it gets `test -r`, which passes. `test -x` would be no better. It consults the file
      mode, which is the same lie `test -w` tells. The only probe that answers "can this run" is
      running it, and no invocation is safe for an arbitrary binary. No fix is chosen.
- **The agent CLI runs through the `spawnClaudeCodeProcess` seam with a policy env**
  (AgentSandbox). The seam provides:
  - an allowlisted baseline plus proxy passthrough
  - a per-session scratch HOME (`<data>/agent-home`), so `~/.claude` state lives and dies with the
    session
  - exactly one credential
  - a process-group kill on the SDK's forwarded abort signal

  srt adds OS confinement around it. The CLI reads and writes its scratch HOME and reads the host
  runtime. It touches no other file of the operator's. It reaches only `AgentSandbox`'s domains
  (`YESSION_SESSION_AGENT_NET`). Docker is BY DESIGN not an agent backend: a container per session
  boot is the opposite of the sub-second start the agent needs. The WorkSandbox keeps the docker
  backend.
  - **The default is `host`, and unlike the WorkSandbox's that is a shortfall rather than a
    decision.** `YESSION_SESSION_AGENT_BACKEND=srt` confines the CLI and is the answer this wants.
    It is not the default because it does not yet work.

    The variable used to be read TWICE, with two defaults: `srt` in `SessionMain` and `host` in
    `Agent.fs`. A deployment that set nothing therefore ran the CLI unconfined while every
    statement the session made about itself said otherwise
    ([`EnvReaders.fs`](../analyzers/Yession.Analyzers/EnvReaders.fs) records that fault). #364
    made it one reader. Making that reader say `srt` turned the release gate's live turn red. The
    turn stalled the whole 90s and streamed nothing.
    - **The cause, now confirmed by the live tier.** A `verify.yml` dispatch with
      `YESSION_SESSION_AGENT_BACKEND=srt` instrumented the spawn boundary to tee the CLI's stdio
      and dump its `DEBUG=1` log. The CLI comes up FULLY: MCP server connected, tools listed,
      credential accepted, an `init` message emitted. Then every request to `api.anthropic.com`
      fails in about 3ms with a transport-level `Connection error.` (no HTTP status).

      Claude Code retries with exponential backoff until the 90s deadline kills it. From outside,
      that reads as a silent stall. srt's own proxy logs ZERO connections for those attempts.

      The `claude` binary is a **Bun** executable. Claude Code's proxy support routes through
      undici's `setGlobalDispatcher(EnvHttpProxyAgent)`, which Bun's native `fetch` does not
      consult. So the CLI's API egress never touches `HTTP_PROXY` and goes DIRECT. srt's
      `--unshare-net` makes direct egress unreachable.

      srt is built to sandbox the COMMANDS that Claude Code runs, never Claude Code ITSELF. The
      WorkSandbox's git and node honour the proxy and are fine: every `Srt` suite is green.
      Confining the `claude` process behind srt's egress proxy is a use that srt was not designed
      for.
    - **What was NOT the cause**, each checked:
      - The async spawn seam. The `Srt` suite drives it end to end.
      - The read scope. `process.execPath` allows `/nix/store` back, so the Bun binary is
        readable.
      - The policy env. The proxy env DOES reach the CLI regardless of what the spawner passes as
        `options.env`. srt bakes it into the wrapped argv: bwrap `--setenv` on Linux, an
        `env VAR=…` prefix on macOS.
      - A missing CA bundle. No TLS interception exists to fail, because srt runs a plain
        authenticated CONNECT proxy by default, with no MITM CA.
    - **Why it stays `host`, and is not a small fix.** srt's `SandboxManager` is process-wide.
      That used to mean the agent CLI and the WorkSandbox shared one egress policy. Each srt
      sandbox now runs its manager in a process of its own (`SrtSandbox.HostWire`). So the
      agent's network policy could be loosened without loosening anybody else's.

      What remains is the binary. Only the Bun one is shipped (the SDK's per-platform optional
      deps), and no Node-runnable CLI exists. Running the CLI on Node is the alternative. There,
      undici's global dispatcher governs `fetch`, and the proxy is honoured. It means adding a
      large dependency and packaging it under Nix.

      Until then the agent CLI is confined by its env allowlist and scratch HOME only. That is the
      same standing as `host` everywhere else, and `host` is the honest default.
  - The SDK's spawn seam is SYNCHRONOUS and srt's wrap is not. So the srt tier hands the SDK a
    stand-in process whose streams are live immediately. It joins the real child to them when the
    wrap resolves. This is plumbing, not policy. The `Srt` suite drives it end to end (stdin in,
    stdout out, exit code) instead of trusting it.
- **The strict confinement profile needs a nested user namespace, which an unprivileged container
  refuses.** srt's seccomp helper creates one inside bubblewrap's to drop capabilities and mount a
  fresh `/proc`. Docker's default (and this repo's dev container) denies it.

  `YESSION_NESTED_SANDBOX=weak` is srt's documented answer. With it, the host's `/proc` stays
  visible and capabilities are not dropped, so the confinement is genuinely weaker. It is
  therefore CONFIGURED, not fallen back to. An unset value means strict, and a session on a host
  that cannot host it fails at boot instead of quietly running wide open.

  An unprivileged container needs `weak`, which is now on the default path rather than an opt-in
  one. `check` probes whichever profile the run configures before it declares the `Srt`
  capability.
- **The Docker backend runs through the `dockerode` SDK and is integration-tested in the verify
  gate.** The session id (a Crockford base32 id, always a valid Docker object name) names the
  containers and a per-sandbox named workspace volume. `EnvironmentSpec` is fully interpreted:
  image/build, mounts (including the persistent workspace volume), working directory, env-var
  refs, and secret refs (resolved at sandbox spawn).

  The container drops all capabilities. It then adds back the three file-ownership ones that a
  real build measurably needs: `CHOWN`, `FOWNER` and `DAC_OVERRIDE`. The comment at that
  `CapAdd` in `app/Sandboxes.fs` says why. The container also sets `no-new-privileges`.

  The container runs the image's default (usually root) user. That is a DECISION, not an
  omission. But the argument for it changed when `DAC_OVERRIDE` came back:
  - Before, this root could not bypass file permissions. The mount suite proved it by failing to
    write into a `0700` host directory.
  - Now, with `DAC_OVERRIDE`, it can bypass them on anything MOUNTED.
  - The boundary is therefore the mount set, not the permission bits within it. A container
    reaches only what `mountPlan` mounts: the session's own checkouts and whatever the operator
    granted. Inside that set, container root was always going to own most of what it touches.
    Running non-root breaks the named workspace volume, which Docker creates root-owned.
  - What remains: files written through a BIND mount are owned by root on the host. That is a
    nuisance, not an escape.

  Resource limits are likewise absent, as they are for every backend. Three more edges are known
  and accepted for now:
  - A docker exec has no kill. "Kill" is closing the stdin stream, so a runaway that ignores EOF
    burns CPU until the sandbox is disposed.
  - The operator's resource ceiling is global: one profile for every repo the session hosts. An
    operator cannot offer a socket to one repo and not another.
  - A host with no reachable daemon surfaces at the first `ensure_environment` as a raw dockerode
    socket error. The error names neither the missing backend nor whose configuration is at
    fault. `repoWorkBackend` is hard-coded with no preflight.

  The suite (`tests/Yession.Tests/DockerIntegration.fs`) needs the `Docker` capability. See
  "Asking for a capability requires it" in [AGENTS.md](../AGENTS.md#testing).
- **Secrets are a real Manager-owned store now** (Plan 06). The store has these parts:
  - AES-256-GCM per-entry ciphertext in `<DataDir>/secrets.json`
  - the KEK in the OS credential manager (`@napi-rs/keyring`), imported non-extractably each start
  - a `/control/secrets/*` surface whose only value-returning route is `resolve` (below)
  - a pure default-deny `Policy.authorize` over the composite session+user identity
  - store-backed `SecretRef` injection (session scope ▸ bound users' scopes ▸ Manager process env)

  Remaining, deliberate:
  - **Hosts without a credential manager run in-memory only** (dev containers, CI, headless
    servers). Secrets die with the Manager. The boot is loud about it, and a plaintext key file
    never exists. In-memory is now also an operator CHOICE (`--secrets ephemeral`), recorded at
    info instead of warn. `--secrets durable` refuses the boot on such a host instead of
    degrading to in-memory (see [deployment.md](deployment.md#credentials)). The `Keyring`
    capability covers the real keyring in tests.
  - **No AMBIENT scope.** `LocalScope` is deployment-wide but not ambient. It is authorized
    against unattributed access that the Manager recorded for that launch. It holds connection
    credentials only: generic `SecretAction`s on it deny. User-scoped GENERIC secrets still have
    no writer (see the connection-broker entry above). There is deliberately no peer scope
    (`Secrets.fs` says why). User↔launch and local↔launch bindings are both launch-lifetime. A
    re-login re-forms them.
  - **Two deliberate value-returning routes, both resolve-shaped:**
    - `/control/connections/resolve` (Plan 08) returns a connection credential to the calling
      session, because an agent turn needs the token in-process.
    - `/control/secrets/resolve` feeds `SecretRef` env injection at the session's sandbox spawn.
      Sandboxes are session-owned, so injection happens there.

    Both routes are gated to the caller's readable scopes. Refresh tokens still never leave the
    Manager. No agent-facing tool wraps either route.
  - **Secret-token-injection is future work.** A resolved secret enters the sandbox as a plain env
    var, so anything the agent runs inside can read it. Under the srt backend, the enforced egress
    allowlist now bounds where a read value can be *sent*. That covers most of the practical risk.

    The real fix is egress substitution: a placeholder inside the sandbox, with the real value
    substituted at the proxy toward declared hosts. srt implements exactly that
    (`credentials.envVars` with `mode: "mask"` and `injectHosts`). What remains is to wire
    `SecretRef` injection through it instead of through the policy env.
  - **No KEK rotation/recovery.** A lost credential entry orphans the store loudly, and the
    operator deletes the file. Multi-user same-name injection precedence is unresolved until a
    real multi-user strategy lands.
  - `@napi-rs/keyring`'s per-platform prebuilds join `node-datachannel` in the "unsigned
    third-party native binaries" trust bucket below. Only field verification covers the
    macOS and Windows paths. CI exercises Linux/Secret Service.

## Runtime & topology

- **An operator's `repos:` on `default` is not read where repo paths are resolved.** The session's
  own sandboxes are the operator's declarations now, and `repos:` is legal on them. But the path
  an agent is told a checkout is at (`SessionMain.fs`, `VisibleAt`) comes from the backend alone.
  A `default` that is a container with its own `repos:` would be told the backend's path, not its
  own.
- **The process split is done** (Phase 4). Each session is a child OS process of the Manager.
  Supervision and secrets custody cross the boundary as a secret-scoped control RPC (environments
  are session-owned via the sandbox seam). The htmx management UI creates, launches, resumes and
  stops sessions.

  But **children die with the Manager**. There is no daemonising and no orphan adoption, so a
  Manager restart stops every running session. Relaunching a session is no longer a manual click.
  `GET /sessions/{id}/open` launches a stopped session and lands on its address (Plan 11). The
  client offers that route when the session it was talking to has gone.
- **The hook relay takes a session's word on what it may receive.** The Manager verifies a
  DELIVERY, because it comes from the internet. It trusts a SUBSCRIPTION, because it comes over
  the authenticated control channel from a child the Manager spawned. This is the stated posture,
  not an oversight
  ([ADR](decisions/2026-08-29-the-manager-relays-hooks-it-cannot-read.md)). All of the following
  is accepted:
  - **Only HMAC over the raw body is verifiable.** Schemes that sign a constructed string carrying
    a timestamp (Stripe, Slack) are not. Configuring one is refused at boot, instead of failing at
    the first delivery. The relay forwards a delivery's headers, so a session could verify its own
    endpoint. That is not built either.
  - **A rotation is a counter an operator bumps**, and the previous secret stays accepted until
    they bump again. Nothing expires it on a timer. An operator who never bumps a second time
    leaves two live secrets, not one.
  - **Endpoints need a durable secret store**, because the signing secret is derived from its KEK.
    Declaring them on an ephemeral store is refused at boot. The alternative was to hand out a
    secret that stops working at the next restart, silently and only inbound.
- **The Manager is practically a singleton.** Nothing global is assumed: data directories are
  per-instance and session ports are OS-assigned. But two Managers over the SAME data directory
  are unsupported, because there is no lock until the SQLite move. The management UI's fixed
  default port (8321) means a second instance must configure its own.
- **Session ports are OS-assigned and change on every launch.** A session has one stable URL,
  `/sessions/{id}/open`. The address it lands on is stable only where `YESSION_SESSION_URL`
  derives it from `{id}` (Plan 12).

  On the zero-config loopback default, the origin moves with the port. The browser partitions
  everything it kept for a session by origin, so that data is left behind. This covers the
  document's IndexedDB store, the Cache API history store, and one transcript store per terminal.
  The client says so instead of promising otherwise (`PublicAccess.sessionAddressIsStable`). That
  is the whole remedy. Nothing migrates the stranded stores.
- **Health is a liveness report, not a health check.** A launch reports busy/idle on
  `POST /control/activity`. The Manager reaps on silence, but only when an operator sets an idle
  timeout (`IdleTimeout` is `None` by default). Unset, a child that wedges after readiness still
  shows as running until it exits. Set, the Manager stops it as `NeverReported` instead of
  diagnosing it.
- **The Manager→Session notification channel is a transport without a producer yet.** The reverse
  leg of the control RPC exists end to end:
  - The child subscribes to `GET /control/notifications` (SSE, per-launch secret).
  - The Manager multiplexes and fans out through
    `ProcessManager.Notify : SessionId -> SessionNotification -> unit`.
  - The child dispatches each notification through a handler that MAY record a durable
    `SessionEvent`.

  But **nothing calls `Notify` in production yet**. The payload
  (`SessionNotification.EnvironmentChanged of unit`) is an explicit placeholder, and the default
  handler only logs. The follow-up is the intended first producer and the real notification
  payload. The first producer would be the Manager autonomously detecting an out-of-band
  environment change. An example is a container it owns dying without the session having stopped
  it.
- **A second reverse leg, the MCP server set, is shipped end to end** (Plan 17). The child
  subscribes to `GET /control/mcp` (SSE). It gets its session's resolved set immediately and a
  fresh set on every change. `ProcessManager.publishMcpServers` is the producer. It publishes from
  the operator's durable declarations, on every registry write and at boot.

  The leg is per SESSION, not Manager-global. The hub is keyed (`KeyedRetainedHub`), so a session
  that is not up yet finds its set waiting. The child holds the set (`app/SessionMain.fs`) and
  `McpClient` connects it, so a declared server's tools reach agent turns.
- **A third reverse leg, connection statuses, has a real producer** (Plan 08).
  `GET /control/connections` streams each launch its readable connection metadata: a snapshot on
  subscribe, and a fresh list on every credential change or new binding. The session's agent gate
  and `/claude` status surface consume it. The hub mechanism is generic. `NotificationHub<'n>`
  serves this leg and the notification one. The MCP leg uses `KeyedRetainedHub`, because it
  retains a value per session.
- **Peer-to-peer is star-shaped through the Process.** Clients sync Yjs state via the Session
  relay, not directly with each other. y-webrtc-style meshes are not used.

## Persistence & data

- **Everything durable is now persisted** (Phase 3). The event log and the Yjs document both
  survive Process restarts. The doc store uses a sidecar `*.doc.jsonl`, compacted at open. Browser
  clients keep the document in IndexedDB (`y-indexeddb`), keyed by the session id embedded in the
  bootstrap page.

  The event log's browser-side cache is the client's own, not the browser's HTTP cache:
  - A client asks from the position it has folded through (`GET /events/after/{n}`).
  - The server redirects to the range it minted (`GET /events/{first}-{last}`). The bounds of that
    range never move, so its bytes are the same for ever.
  - Every response is `no-store`, because a second copy in the HTTP cache would be a spare nobody
    reads.
  - The client keeps the ranges it was given in the Cache API, under a name derived from the
    session id. A cold load replays what it kept, and only the tail reaches the network.
- **The JSONL event log loads fully into memory.** It has no compaction, rotation or
  checksumming. A corrupt line fails the whole open, which is loud by design. The doc store
  compacts only at open, so a very long-lived Process grows its sidecar until restart.
- **Event ranges are cookie-gated for browsers. Headless clients still put a minted peer token in
  the range URL** (`?token=`). The browser path is clean: the same-origin auth cookie rides each
  fetch. So the URLs, and the cache keys that the client stores them under, carry no secrets. The
  token-in-URL path remains for Node clients and tests. It is scoped to per-process minted tokens
  that die with the session.
- **A session opens cold with no network** (Plan 20). A service worker at the mount keeps the
  shell and this build's assets, and nothing else:
  - The shell is network-first, because it names the fingerprinted assets.
  - This build's assets are cache-first, because their address pins their bytes.
  - The event log is the page's own cache. `/me`, `/signal` and `/queries` are liveness
    questions that a cached answer would answer wrongly.

  The worker needs a secure context. So a session served over plain HTTP at a non-loopback
  address still cannot open cold. There, the settings pane names the missing capability and the
  flag that restores it.
- **Nothing measures whether the history store holds.** `storage.persist()` is a request, not a
  guarantee. Chrome grants it for an engaged site. Safari essentially grants it only for an
  installed app, and Safari additionally caps script-writable storage at seven days without user
  interaction. So the exposure is the session nobody has opened in a week, which is also the one
  somebody most wants back.

  A walk knows how many answers it served locally and how many went to the network. That ratio
  would settle the question. It is not on the OTel resource yet. So the decision to replace the
  Cache API store with an IndexedDB one keyed by offset has no evidence to rest on.
- **Nothing drops a session's kept history when the session is gone.** Each session names its own
  caches (`yession/session/<id>/events`, and one per terminal). A deleted or reaped session
  leaves them behind for ever. The names are the remedy, not the problem: `caches.keys()` makes a
  sweep a filter over the sessions the Manager says still exist. No surface does that sweep yet.
- **The history feed degrades explicitly, and only history degrades.** The event feed is the one
  leg that is HTTP instead of the data channel, so it fails on its own. It once failed silently.
  A rejected fetch became an empty final page, which the read loop re-requested at once, for ever.
  Drafts, title and presence kept syncing over WebRTC, so the only symptom was a timeline that
  never filled.

  Now a read returns `Result<EventPage, FeedFault>`. A resilience policy
  (`Yession.Domain.Resilience`, composed with the transport in `app/browser/Browser.fs`) retries
  only transient faults. It retries five times, with exponential backoff from 250ms to a 10s
  ceiling. It adds jitter so that a Process restart does not bring every peer back in lockstep.

  A settled failure parks the loop and surfaces as `FeedHealth`: a sidebar line, a banner and a
  header status. Nothing is disabled while the feed is down, because writing is CRDT state in the
  local doc.

  Recovery is no longer only a hint away (Plan 20). One supervised loop drives the session leg,
  capped at a minute. The browser's `online` event pokes it, and so does a *Try again* control on
  the status line. A REFUSED peer parks until somebody asks, and does not end. No schedule can
  help it, because the same token would be refused again. That control is the affordance this
  entry used to say was missing.

## Browser client

- **Rendering is Fable.Lit (lit-html), a reconciling renderer.** The view is a total function of
  the model, rendered into `#app` on every change. Lit diffs the DOM, so focus and caret survive
  re-renders with no manual restore hack. The only remaining manual DOM work is pinning the chat
  scroll and pixel-positioning collaborators' cursor markers, because a native `<input>` exposes
  no per-character geometry.
- **Sending a draft still bypasses the Elmish loop, and moving it in needs Ylmish to change.**
  Every other control now dispatches a message. `ClientModel.update` answers the requests it makes
  as `ClientEffect` values, and `Client.Ports` carries them out after the model they came with.
  The read cursor, the reads out and presence work this way too (`ClientModel.reads` and
  `presenceToSend`). The drafts are what is left:
  - the composer's Send (`ViewActions.SendDraft`)
  - the terminal composer's Send (`SendTerminalDraft`)
  - the editors' keyboard send (`Links.SendDraft`, `SendTerminalDraft` in `Render.fs`)

  Each calls a verb on `Client.Connection`. The verb opens a `doc.transact`. It copies a body root
  into the queued entry. The app owns that root beside the synced model (a draft's
  `Y.XmlFragment`, a terminal slot's `Y.Text`). The verb then dispatches `SendDraftMsg` /
  `SendTerminalDraftMsg` from INSIDE that transaction.

  That one transaction is the invariant:
  - The Session drains on a queue entry's arrival. An entry that lands without its body runs as an
    empty message.
  - A send split across updates lets a `withYlmish` `Set` from the drain's removal clobber the
    sender's own state. (`Client.connect` says both at the verbs.)

  The invariant holds today only because of two facts. A dispatch from a click handler is
  processed synchronously. And `withYlmish` wraps each update in `Y.transact`, which nests inside
  the one already open. Made an effect, it breaks. A dispatch from inside an Elmish command is
  queued until the command returns, so the model's write lands in a second transaction after the
  body's.

  The fix belongs in Ylmish, not around it. An update needs a way to declare doc writes that run
  inside the transaction `withYlmish` already opens for it, before the model's own write. Then
  "seed the queued body, clear the draft, add the entry" is one message, one update and one
  transaction. The cheap tier can assert the writes as values. To design:
  - Whether the writes are opaque `Y.Doc -> unit` functions or a typed vocabulary over the roots
    an app declares (copy a fragment, clear a text).
  - Which origin they carry, given that the binding filters its own echoes by origin and these
    roots sit outside the binding.
  - Whether this is a new `Program` combinator or a change to `Options`.
- **One WIP draft per client, co-editable by any peer** (Plan 03). Drafts are keyed by author
  (`Map<PeerId, DraftState>`), so each client owns at most one. The structure enforces this, not a
  runtime cap.
  - Any peer may co-edit any slot, and any co-editor may send it. The entry is attributed to the
    draft's AUTHOR.
  - The key the author minted (`QueueId`, carried by the slot since it was published) makes two
    concurrent sends one entry, not two.
  - The queue is untouched. Send clears the slot, so a client still queues many messages by
    sending repeatedly.

  Drafts and queued messages are **rich ProseMirror editors** on a Yjs `XmlFragment` (markdown
  typing, bold/italic/code, lists, paste-as-markdown, undo/redo). They are not textareas and not
  plain text. **Collaborative presence cursors** overlay every collaborative field (the title
  input and the body editors). They show each peer's caret and selection with a colour and name
  label, relayed over ephemeral `Presence` frames (never durable).

  Invariant 4 (clean send) has a dedicated Hedgehog property. Broader draft-op schedules
  (participation, offline rejoin) are the follow-up.
- **A 401 from `/me` renavigates to `/login` unconditionally.** There is no in-app "signed out"
  state. The client simply rides the OIDC bounce again. The other axis is handled: an unreachable
  session keeps the cached shell local-first. Once the disconnection settles, the client offers
  the `/sessions/{id}/open` card instead of a status word.
- **No browser support matrix.** Tests run on Chromium (headless, in CI). The ICE gathering
  settle-fallback should cover Safari and Firefox mDNS behaviour, but those browsers are untested.
- **A session's port, pid and build are no longer on the manager page.** They shared the status
  cell with the line a session now says about itself, and both do not fit. Measured: the word
  plus a summary plus a build want 460px of a 384px cell. The summary answers the question the
  page exists for (which of six sessions wants me). The rest is diagnostic. So the diagnostic
  went, and the column shrank to 256px.
  - Nothing else lost it: `SessionRegistryEntry.Build` still rides the registry stream, and the
    Manager's own build is still on the page header.
  - Below `xl` the summary yields too (the column is 100px there). A phone gets this answer from
    the tab title.
  - The way back, if a reader ever wants it, is a row that opens: clicking the id to expand or pop
    out the process detail. It is not a fourth thing competing for one line. That is unbuilt. The
    summary is deliberately what the row shows instead.

## Visual design

`docs/visual-design.md` is the doctrine. It was written after the code. Where the two disagree,
the document wins (see its "Deciding" section). This section records each disagreement until the
code matches the doctrine. Each entry names the rule and the sites.

- **Three rounded rectangles, against a form rule that has none.** The doctrine's form is two
  primitives: a sharp rectangle and a round point. A rounded rectangle is neither. There are three:
  - `Style.remoteCursor` wears `rounded-sm` (`Style.fs`)
  - the tool-output box, `Style.chatToolIoBody`, wears a bare `rounded` (`Style.fs`)
  - the editor's presence-caret name label, `.pm-caret-label`, wears `border-radius: 3px`
    (`app/tailwind.css`)

  The status dots (`rounded-full`) are points and are correct. Fix: drop the three radii, and say
  in each comment which primitive the thing is.
- **Three `disabled:` faces, against a state rule that has none.** The doctrine says a control
  with nothing to do yet is not `disabled`: it stays in focus order and its border carries the
  waiting. `Style.fs` says the same twice, in so many words. Then it wears `disabled:*` on three
  controls:
  - the launch card's rows (`askRowButton`)
  - its commit button (`askStart`, "disabled until something is held — and looks it")
  - `trayInterrupt`

  Either the rule admits a case and says so, or the three move to the waiting face. The case would
  be a control that CANNOT act, as opposed to one with nothing to act on. Nothing yet says which.
- **The touch target is a literal, not a token.** The doctrine names "the touch target" as part of
  the floor. `Style.fs` spells it as `phone:min-h-11`, `phone:w-11 phone:h-11` and `h-12` at some
  two dozen sites. No `--spacing-*` token backs it. So the one number every phone control must
  agree on is the one number nothing holds. Fix: a `--spacing-touch` token in `app/tokens.css`,
  which the sites compose.
- **Density has no rule.** The doctrine says height is spent on content, not chrome. `Style.fs`
  reasons about one composer's height ad hoc. There is no row height, no cap on chrome per
  surface, and no stated way to choose compact over roomy. The rule is still to be found.
- **Layering has no scale.** `z-10`, `z-30` and the rest are written inline where each surface
  needed one. The doctrine says depth is said by blur over something real. It does not say how
  many layers exist or which surface sits on which. Three named layers (ground, floating, overlay)
  would cover every current use, and would let a new surface pick instead of count.
- **Empty states, waits and icon-versus-word have no rule.** Each has one or two reasoned cases in
  `Style.fs` or `tailwind.css`. They are the wordless empty chat, the breathing mark instead of a
  spinner, and the arrow that replaced `SEND →`. No statement generalises them. The doctrine
  leaves empty states ungoverned on purpose. It does not address waits or icon-versus-word at all.
- **The browser tier tests the sideways phone in three cases, and should have one per shell
  surface.** The skill and the stylesheet once disagreed about the breakpoint.
  `ui-exploration/SKILL.md` named `max-md` = 768px and two anchors. `tailwind.css` had already said
  that width alone was the wrong question and named 844×390 as the case that broke. That is
  settled. The skill now says three anchors and the `phone:`/`wide:` pair, and keeps `max-md` for
  reading-column rules only. What is left is the coverage. `Browser.fs` has three cases at 844×390
  (`thumbSized`, `navOverPane` and the chat-width case).

## Agent

- **A bare `start_work_sandbox` beside a repo-declared name mints a decoy.** Sandbox names are
  scoped (`SandboxRef`): the repo's file declares `trinketworks/yession:dev`. An agent that calls
  `start_work_sandbox` with plain `dev` gets a NEW session-owned sandbox. That sandbox is
  srt-confined, has an empty workspace, and wears the name the agent had in mind.

  Nothing is wrong in the model, because two scopes may share a short name. But agents driven
  through the tool fall for it repeatedly. Measured across the container e2e runs, one agent spent
  a whole errand creating, inspecting and removing `dev` decoys while the real dev container sat
  running. The tool's own answer says which sandbox came up. A refusal, or a disambiguating nudge
  when a repo-scoped sibling exists, is the likely fix. It is not built yet.
- **An agent and a long-running build are a bad pairing without explicit parking.** A container
  build (`nix develop --command check`, tens of minutes cold) outlives any patience an agent turn
  has. Measured: agents polled it into their own timeout and re-ran it. They twice called
  `stop_work_sandbox` on the container mid-build, which killed a 7.5GB-warm store to "reset".

  The working pattern is a detached launch (`nohup … &`, log to a file) plus an instruction to
  stop after starting it. The missing piece is a first-class long-job story: a command the session
  watches and reports on, instead of a turn that has to survive it.
- **The live agent's tool results are text renderings** of the typed capability results. No
  structured tool-result schema exists. The agent can read back what its own terminal commands did
  (`read_terminal`, plus the digest on the context pack, Plan 13 stages 3a/3b). But no tool reads
  session history beyond the prompt transcript.
- **The context pack is a flat transcript** rebuilt per turn from the full projection. It has no
  windowing, summarisation or token budgeting. Bodies are now Markdown (rich text landed), but the
  transcript is still a naive `author: body` join with no multi-line handling. Long sessions or
  large rich bodies will eventually overflow the model context. Only the terminal digest is
  bounded (an output tail, with the elided count stated).
- **Turn discipline is done** (Phase 3). The queue drain enforces single-flight, interrupt is
  explicit, and property tests pin the invariants. But:
  - The queue has **no size cap**.
  - A drain coalesces any backlog into ONE turn. No option for per-message turns exists.
  - The queued-message UI has no "locked" visual during the drain broadcast window. A peer can
    briefly type into an entry that is about to vanish. The edit is safely discarded, but the UX
    flickers.
- **A turn has no step ceiling, and nothing automatic bounds a runaway one.** `maxTurns` is
  OPTIONAL on the Agent SDK's `query()`, and unset means no cap. `Agent.fs` now passes it unset,
  which is the setting interactive Claude Code runs under. The ceiling that was here before (32
  model turns) ENDED the turn as a failure. An errand long enough to hit it was the errand least
  well served by being cut off mid-way.

  Only the interrupt replaces it. A turn spends the TURN HUMAN's credential in a session that
  other people can watch and nobody need be watching. A bound that requires somebody present is no
  bound at all for an unattended session. Missing: a budget the model can see, any signal that a
  turn is running long, and any cost ceiling per turn or session.
- **A wake can start a turn nobody asked for, and wake→turn→command→wake is a legitimate loop.** A
  background command finishing makes a turn due. That turn may background another command.

  Two things bound the loop. One is visibility: every woken turn carries its reason durably on
  `AgentTurnStarted.Woke` and shows it in the chat. The other is the human interrupt, which is no
  bound at all for a session nobody is watching. A per-session woken-turn budget is the next dial.
  It is deliberately not built in advance.
- **Watching a pull request is polling, deliberately.** A session asks GitHub about each watched
  pull request instead of being told ([ADR](decisions/2026-08-27-pr-state-by-polling.md)). A repo
  webhook needs admin on the repo. Inbound delivery needs a deployment that github.com can reach,
  and the loopback default is not one. All of the following is accepted:
  - **Up to fifteen seconds of latency while a suite is in flight, and up to a minute otherwise.**
    A declared hook endpoint with the App's webhook pointed at it (deployment.md §Webhooks) drops
    this to seconds. Otherwise polling cannot give less, short of editing the two constants in
    `PrWatches`.

    Only the pending cadence costs anything. A settled watch is two conditional requests that both
    answer 304, which GitHub does not charge for. The pending cadence puts the ceiling around ten
    pull requests with live suites at once per credential. Lowering it again buys latency at that
    ceiling's expense. That is the argument for a pushed transport instead. The hosted dispatching
    service that the ADR describes is the exit, and it does not exist.
  - **Check runs only.** The legacy commit-status API is not read. A repo whose CI reports only
    statuses (no check runs) therefore reads as having no checks, not as failing. That is the safe
    direction, and it is invisible.
  - **The first 100 check runs on a commit.** No pagination exists. A commit with more is rolled
    up from a prefix, biased toward pending.
  - **Only rollup FLIPS are announced.** A push that resets checks to pending and ends green again
    says nothing. Entering pending is the ordinary rhythm of work, and a notification per push is
    a notification nobody reads.
  - **A rate-limited watch waits out GitHub's own reset window** and says so in the
    `pull_requests` query's status column. It does not fail, and it does not retry sooner.
  - **A transition wakes a turn**, which joins the wake→turn loop above as a third source.
    Flapping CI can wake once per genuine flip, and the per-session woken-turn budget is still
    deliberately unbuilt. The log-anchored baseline bounds the wakes to one turn per transition.
    It does not bound how many transitions a flapping suite produces.
- **Repo integration is the read-only bootstrap slice** (Plan 14). It has typed clone-and-orient
  verbs beside the agent, one repos dir shared into the WorkSandbox, and GitHub sign-in per user
  over the device flow. Remaining, deliberate:
  - **A session's repos are session-readable, and bytes outlive revocation.** One user's private
    repo, once added, is readable by every peer and everything in the WorkSandbox. This is the
    same shared-trust boundary as "terminal access equals session access". The `RepoAdded` event
    names who brought it in. GitHub-side revocation does not claw back what is already on disk.
  - **A pasted PAT bypasses the App-installation scope rule.** The device-flow token is a GitHub
    App user-to-server token, so it can only reach repos where the App is installed. A pasted
    `github_pat_`/`ghp_` answers to no such bound.
  - **A GitHub token rotates only if the App expires it** (Plan 21). A device-flow grant is stored
    as a grant now, and the Manager refreshes it on use. But an App registered with user-token
    expiration disabled still yields a permanent token. Revocation is at GitHub either way.
    Nothing tells an operator which of the two they registered.
  - **`git push` in the `default` WorkSandbox terminal has no forwarded credential.** Forwarding
    shipped as `start_work_sandbox`'s `forward` argument (`app/WorkSandboxes.fs`), so a sandbox the
    agent asked for can carry `github`. But `default` is the one nobody asks for, and it is created
    with `Forwarded = []`. Its terminals do local git only until somebody starts a named sandbox.

    In a sandbox that forwards `github`, every BLOCK is told who its commits are by. That is the
    GitHub account behind the credential the block's act runs on
    (`GitHubConnection.commitIdentity`). `__y_env` exports it at the head of the block's line and
    takes it away when nobody's is known. But a sandbox that forwards nothing has no author, and
    `Co-Authored-By` for the agent is absent everywhere.
  - **A forwarded `github` credential reaches every backend by the host address its sandbox can
    use.** Forwarding is a route through the session's git gateway (`app/GitGateway.fs`). The
    sandbox's git is told one `insteadOf`, and the credential never enters the sandbox. The route
    has to be REACHABLE. `Sandboxes.hostAddressFrom` documents the answer per backend and
    platform. The gateway binds every interface because of the docker answer.

    The macOS answer for srt is the box's own hostname. It assumes the box resolves its own name.
    Where it does not, a confined git gets a proxy error that names the host instead of a route.

    A `Provision` carries the host it needs as a `Domains` entry, and the composition widens a
    filtering backend's allowlist by it.
  - **Under `YESSION_SESSION_AGENT_BACKEND=host` the git verbs run unconfined.** This is the
    operator's explicitly lax choice, as everywhere `host` is chosen. The per-invocation hardening
    (hooks/fsmonitor/ext off, no global config, protocol pinned) still applies. The filesystem and
    egress boundaries do not.
  - **Every srt sandbox may write a checkout's `.git/config`.** srt denies that write by default,
    and a `git clone` makes it, so the flag is on, for every sandbox. Scoping the flag to the git
    sandbox is a change nobody has made yet, and srt does not refuse it (`AllowGitConfig` in
    `app/Sandboxes.fs` says why). Until then the WorkSandbox and the agent can write a
    `.git/config` too. They can plant a `core.fsmonitor`, an alias, or a pager that runs when git
    next runs in that checkout.
    - Inside the session, that is the shared-trust boundary already stated above. The verbs
      themselves are immune, because the per-invocation `GIT_CONFIG_*` hardening wins over any
      repo config.
    - OUTSIDE the session, a human who runs git in the checkout on their own host is not immune.

    `.git/hooks`, the other half of the same vector, stays denied.
  - **The clone verb runs with no filesystem confinement.** srt refuses writes to certain names
    WHEREVER they appear, and no allow-path outranks that refusal. So a confined process cannot
    materialize a checkout that contains any of them. srt scopes the exemption per SPAWN, never
    per path. The comment on `FilesystemConfinement` in `src/Yession.Domain/Sandbox.fs` lists the
    names and says why the exemption applies on Linux too.

    So `add_repo`'s clone has its own sandbox with srt's filesystem rules off. For that one
    command, git can read and write whatever the session's user can, including the credential
    files srt would otherwise mask. `filesystem.disabled` drops the READ policy together with the
    write one, so the read scope above does not reach this spawn either. Egress stays pinned to
    github.com, the env stays the hardened one, and every other verb keeps the confined policy.
    None of them writes a path that srt objects to.

    The exemption goes away the day srt can exempt a subtree instead of a spawn, or reads
    `allowGitConfig` per spawn. The clone then takes the ordinary confined policy again, and
    `FilesystemConfinement` loses its only caller.
  - **A docker terminal does not open on the checkouts.** Under the host-family backends, the
    repos directory sits inside the workspace a terminal starts in (`Sandboxes.SessionLayout`), so
    the agent's first `ls` shows what it cloned. The docker backend cannot do this. Its workspace
    is whatever the image's working directory is, because this session composes no
    `WorkingDirectory`, so nothing here knows the path. The repos dir arrives as a bind mount at
    `/repos` beside it. A verb still ANSWERS with the right path (`reposVisibleAt`), so nothing is
    unreachable. The cost is one `cd` that the other backends no longer need.
  - **A repo's `yession.yaml` is read, and here is what it still cannot say** (Plan 27). The file
    declares sandboxes and nothing else. A sandbox is the only scope where "two repos both said
    something" has a total answer. The keys are (repo, name) pairs and the repos are disjoint, so
    the union is disjoint by construction. **That is the rule for every future key: a key belongs
    in `yession.yaml` only if its scope is a sandbox.** What that leaves out, deliberately:
    - **No inter-sandbox networking.** A `db:` sandbox's process is reachable from inside that
      sandbox only. A compose-like shared network is a design of its own.
    - **A `command` (`cmd`), an `entrypoint` and a `dialect` are docker-only.** They live inside or
      beside the `container:` block. Under `srt` or `host`, there is nowhere to write one, instead
      of a refusal to explain. The exception is `dialect`, which is refused on a sandbox with no
      container because it reads as if it would apply.
    - **No `${VAR}` in an entrypoint.** compose interpolates from the host's environment at parse
      time. That is the wrong environment here: the sandbox's is the one that means something.
      Nothing has needed it. The string form is read into words and nothing else. A value that
      needs expansion is one to run through an actual `sh -c`.
    - **No per-session ceiling.** The tier-3 operator env is host-wide. So a human at the command
      gate bounds a widening ask, not a policy for this session.
    - **No file watcher.** A `git pull` that changes the file takes effect at the next fold
      trigger: boot, or `add_repo` / `switch_branch` / `remove_repo`.
    - **No session-wide keys, and there will not be.** Gates, approvals and MCP servers are
      session-wide, so two repos that disagree have no honest tie-break. Those stay the
      operator's. A `repos:` key (this repo needs that one) is recursive, and its fixed point is a
      design of its own.
    - **A repo's file is as trusted as its push access.** Only the gate and the schema's own
      refusals mitigate this: the reserved `YESSION_` prefix, a host-path volume, and a workdir
      outside the checkout.
- **The session's imperative API is split, and only half of it is built** (Plan 15). Commands
  mutate and belong to the agent alone. Queries read and are declared once. They reach the agent
  as generated MCP tools (`readOnlyHint`) and the humans as a generated settings surface fed by
  one multiplexed SSE stream. Stage 1 shipped. It retired the Repos panel's add/remove/switch
  controls and the `/repos*` routes. Remaining, deliberate:
  - **Every session member reads every query.** There is no per-query authorization. The timeline
    takes the same stance: every member reads every attributed act-line. A query that should not
    be session-wide has nowhere to hide yet.
  - **The shipped classifier approves everything.** Every terminal block and every structured
    command passes the classifier (`Classify.fs`, Plan 23) on its way to happening. The only
    implementation is `Classifier.approveAll`. So until an AI-driven classifier lands, nothing
    stands between an agent turn and any command except the work sandbox's confinement.

    Manual approval was removed deliberately, not lost. The queue stays visible and editable,
    refusals stay recorded and attributed, and the seam is where the real classifier plugs in.
  - **A forwarded credential is a route, and the route's cap lives in the sandbox's env for that
    sandbox's lifetime** (Plan 15 stage 2, reshaped by the git gateway). The VALUE no longer
    enters the sandbox. `github` forwards as one `insteadOf` that names the session's git gateway
    under a per-sandbox capability. The gateway resolves the credential on every request. So a
    refresh under Plan 21 reaches a running sandbox, and a revocation at the provider is felt on
    the next push. `stop_work_sandbox` revokes the route.

    WHOSE credential answers is the block's, not the sandbox's. A per-block loan (`__y_env`) names
    it, and the gateway answers a request from the loan it carries and from nothing else. The
    header of `app/GitGateway.fs` describes the loan. A push writes `GitCredentialSpent` to the
    log, which is where the person whose credential it was finds out.

    What the sandbox holds is the cap and, in each block's environment, that block's loan.
    Everyone in the session and everything running in it can read the loan. A block can read
    another block's live loan out of `/proc`, the same shared trust boundary Plan 14 states. The
    loan is worth exactly "act as this sandbox's git at this session's gateway" for as long as it
    is live. The gateway holds that bound: a loan answers only on the route of the sandbox it was
    lent in.

    What is NOT held is the transcript. The loan is in the block's environment. So `env`,
    `printenv`, or a program that dumps its environment on the way down prints the loan into a
    durable record. That record replays to every peer, present and future. A printed loan is a
    live credential only until the terminal's next block returns it, and only from inside that
    sandbox. But the person it names is in the transcript for good.

    A lease taken at an idle prompt lends its holder their own loan. The loan is typed in where the
    transcript cannot see it, and is returned when the lease ends. A lease taken over somebody
    else's running block, or inside a program, lends nothing. A push typed there is refused in
    words.

    Only `github` is forwardable so far. The gateway admits only git's three smart-HTTP requests
    to a repository path, so a cap is not a token for the rest of github.com.
  - **The GitHub API reaches a sandbox only as far as somebody bound it.** A connection forwarded
    by `api` routes `api.github.com` and `uploads.github.com` to the credential proxy, and
    nothing else. The route is a `${proxy.https}` that the sandbox was given, so it exists only
    under docker or the host. srt is not given the route at all (`CredentialProxy.forwardApi`).
    Two further bindings come from the operator, in a resource's `env`, or from a repo's
    declaration. Nothing sets them on a sandbox's behalf:
    - which bundle a client trusts (`${proxy.ca-file}`)
    - which variables carry a stand-in (`${github.token}`)

    A sandbox that binds neither reaches the API through the proxy unauthenticated. The stand-in is
    in the block's environment exactly as the git loan is, with the same transcript caveat above.
    The proxy, unlike the gateway, admits every request to a declared host. So a stand-in is the
    lender's whole API token for as long as it is live.
  - **The AgentSandbox is granted nothing.** The agent CLI runs with no built-in tools. Every
    command it issues runs in a work sandbox, under that sandbox's grants. Its own process holds no
    connection and no proxy, and nothing in the profile can give it one. If the CLI ever runs tools
    of its own, it needs a selection of its own first.
  - **A grant is the operator's file as it reads today, and nobody consents to it.** What a sandbox
    is given is the closure of its selection under the current profile (`ResourceProfile.grants`).
    A person who joins a session is not shown it. A change to a profile or to a repo's file widens
    what every later sandbox holds, without anybody being asked.

    The closure is already the one value that says what was granted. Two remedies build on it, not
    on a second account of the same thing. One is to show it at join or at connect. The other is to
    record it per person, so that a later configuration cannot widen a grant silently.
  - **An external MCP server's read-only tools are not queries yet.** `readOnlyHint` is declared,
    not inferred, precisely so that a third-party server's queries could be listed into the
    registry without a yession-specific convention. But only the in-process registrations reach
    the registry. The rendering is in the way: a foreign tool's answer has an arbitrary JSON
    Schema, and the generated surface draws rows, fields and a value. The missing piece is a
    JSON-Schema-subset renderer.
  - **A person cannot mint a stream without the agent.** Someone who wants a device terminal before
    the agent has opened one has to ask the agent for it. Yession cannot offer a button. The tool
    that mints a stream offer has a name and an argument schema learned at runtime, and the product
    has no opinion about either.

    The general answer is a human surface for invoking a declared tool. It would be a form
    generated from the tool's JSON Schema, plus an authorization story for a person who calls a
    foreign tool directly.
- **Per-user agent credentials landed** (Plan 08). A human signs into their Claude account from the
  session's Connections panel: "this session only" (`SessionScope`) or "all my sessions" (their
  user scope, or the local scope under `--auth localhost`). The Manager brokers the OAuth exchange
  as pure standards and never learns the provider. Each agent turn runs on the TURN ACTOR's
  credential (session-scoped ▸ actor's own ▸ ambient env), resolved fresh per turn with
  Manager-side lazy refresh. Remaining, deliberate:
  - The ambient `ANTHROPIC_API_KEY`/`CLAUDE_CODE_OAUTH_TOKEN` process env stays as the documented
    last resort. It is how CI's LiveAgent tier feeds the agent, and it applies to ANY actor.
  - A refresh failure surfaces only as the turn's failure. No panel-level health indicator exists.
  - The browser polls the panel's status. The SESSION learns of changes live over its control
    stream, but the open browser tab re-asks.
- **Live-path verification is credential-gated by design.** The `LiveAgent` capability makes
  `verify` fail without `ANTHROPIC_API_KEY`/`CLAUDE_CODE_OAUTH_TOKEN`, instead of skipping (see
  "Asking for a capability requires it" in [AGENTS.md](../AGENTS.md#testing)). A cheap-tier run
  never asks. `YESSION_BIN_CLAUDE` matters in sandboxes that kill the SDK's vendored binary.

  That gate does NOT cover the credential going missing after it passes. The parent probes the
  credential once, before the suite starts. The suites read it out of the process env, and the
  suite is one process. So anything that mutates that env changes what every later suite tests.

  That happened. `Phase2`'s credential-leak regression planted a key and DELETED it on the way
  out. The live clone case, compiled after it, got a session with no credential. `SessionMain`
  answers no credential by starting **no agent at all**, so the turn produced no reply and no
  error. Nothing said why.

  `Support.withEnv` (one verb: take and give back, absence included) closes the known cause, and
  three cheap-tier cases pin it. The CLASS is still open. Nothing re-checks a declared capability
  at the point a suite uses it, so the next mutation of process-wide state is silent in the same
  way.

## Delivery & operations

- **Node is a required runtime.** Yession ships as one npm package with two bins
  (`yession-manager`, `yession-session`). `npm i -g yession-*.tgz` pulls the platform-native deps
  through npm's optional dependencies: `node-datachannel`'s addon AND the SDK's native `claude`.
  So install is all it takes, and the agent works offline afterward.

  But there is no self-contained binary anymore. A machine without Node ≥24 cannot run Yession
  from npm. (The Nix installable wraps `nodejs_24`, so that route brings its own Node, at the cost
  of needing Nix.)
- **First install downloads the native `claude`** (~240 MB, platform-specific). It is not in the
  300 KB tarball, so npm fetches it. The *first* install therefore needs network and disk. The
  SDK's own resolution finds the binary afterward, so `YESSION_BIN_CLAUDE` is not needed.
- **The composition E2E and install smoke run on Linux/CI.** Other platforms' npm resolution rides
  npm's own optional-dependency machinery, unverified per-commit.
- **No per-platform build matrix for the npm route, no code signing.** Release CI ships one
  platform-neutral npm tarball and a Nix package, both from `ubuntu-latest`. npm's
  `optionalDependencies` resolve the platform-native pieces on whichever machine runs
  `npm install`. Yession does not build them. (This replaced the earlier SEA per-platform
  binaries — see Step 26→28.)

  Yession therefore has no compiled binary of its own to sign or notarise. The native `claude` and
  the `node-datachannel` addon that npm pulls in are unsigned third-party downloads, and they may
  still trip macOS Gatekeeper.

  Darwin has one foothold: the PR gate builds the flake package on `macos-latest`, enters the dev
  shell, and loads the native WebRTC addon there. But only the Linux install-smoke exercises the
  npm INSTALL path on darwin, and everything on Windows.
- **Telemetry is agent-turn usage plus Manager audit records** (Plans 04 + 06). Each completed
  turn emits one OpenTelemetry **log record**: the token and cache counts plus session, turn and
  model ids, never message content. Every process (the Manager and each session) is a **direct
  OTel emitter**. There is no Manager-side collector.

  The standard OTEL_* env that the Manager is started with chooses the destination:
  `OTEL_LOGS_EXPORTER=console|otlp|none` (comma-separated for a stdout+collector tee) and
  `OTEL_EXPORTER_OTLP_*` for the collector. The Manager passes it through to each child, whose
  identity the Manager adapts. The default is `console` (stdout). With no collector configured,
  forwarding is dropped.

  Separately, the Manager emits its own in-process `yession.*` **audit records** for the
  secrets/ABAC surface: ops, denies, injection, KEK/store lifecycle, user↔launch bindings, and
  control 401s (see Plan 06 § Telemetry). Each is one greppable stdout line.

  Still missing:
  - a **metrics pipeline** and **traces** (the emitter path generalises to both, with the same
    env selection and no collector to touch)
  - **forwarding of audit records to a collector**
  - **structured app logging or crash reporting** beyond stdout
- **Multi-node operation and work-intake integrations (Slack/Linear)** remain out of scope, as
  planned. Terminals landed as Plan 13 and remote access as Plans 09/10/12. Still out of scope
  there: a session that runs on a machine other than its Manager's.

  The deployment shapes are now loopback and fronted
  ([ADR](decisions/2026-08-28-deployment-fronts-everything-or-nothing.md)). The remaining
  centralisation is the connection broker's single refresher. A provider that rotates refresh
  tokens on use makes two nodes that share one stored grant a race. The second node loses it. This
  is the reason the in-flight map in `app/Broker.fs` exists.

  The sketched exit is per-node grants: each node has its own sign-in, and nothing is replicated.
  It is deliberately a follow-up, not built.

## Testing debt

- Browser E2E runs on Chromium only, and drives one host platform per CI run.
- The Yjs relay trusts ordered delivery per data channel. The Phase 3 property schedules cover
  arbitrary *delivery timing* (staleness, partitions, restarts), but not corrupted or duplicated
  *frames* on the wire.
- The vendored Hedgehog does no shrinking. A failing property prints the whole schedule, not a
  minimal one.
- Load and scale characteristics (many peers, large logs, long drafts) are unmeasured.
- **The serial engine is tested against a tty, never against a chip** (Plan 16, part E).
  `check Serial` drives `Ports.real` over a socat PTY pair. So the open, the line settings, the
  read and write paths, and the vanish path all cross a real kernel tty. No CI box can cover a
  specific adapter. Three examples:
  - a baud rate that the driver silently rounds
  - a chip that needs DTR/RTS toggled to come out of reset
  - a USB stack that reuses `/dev/ttyUSB0` for a different device after a replug

  Those faults surface on hardware or not at all.

## Terminals

- **A queued command whose terminal closes stays queued for ever, and is now unreachable.**
  Nothing runs it and nothing removes it. This is deliberately non-destructive, instead of
  silently dropping someone's text. But a closed terminal renders its recording instead of its
  composer (stage 3e). So the entry that was at least visible and deletable before is now neither.
  It sits in the doc with no surface at all.
- **Terminal access equals session access.** A terminal can read the sandbox's environment (`env`).
  After resolve-at-spawn, that environment includes secrets the session's spec references. This is
  not a new privilege, because any peer could already ask the agent to run `env`. But a terminal
  makes it one keystroke. A future per-user terminal gate would attach here.
- **A screen that showed a secret is in the recording, permanently, one tap from the chat.**
  Keystrokes are deliberately not captured (`Session/Terminals.fs`, `Input`), because live mode
  makes typing a password ordinary. But output is captured. The replay work removed the age at
  which a closed transcript was deleted, and it put a tappable chip on every block and lease
  stretch.

  So the distance between a secret and a casual reader is now one tap. That holds for every peer
  in the session, for as long as the session's data exists. Nothing here is newly privileged,
  because terminal access already equals session access. But anything that widens who may read a
  session (a link scope, a public view, an export) widens this with it.
- **`renewable` is a provider's claim about its own tool, and nothing verifies it** (Plan 19,
  step 4). A closed stream offers a way back when the provider said asking again is safe. Pressing
  it replays that tool call with its original arguments. A provider that marks a destructive tool
  renewable makes the button destructive. The default is false, and the field is documented as a
  promise. `SourceCapabilities` has the same standing, and it is unverifiable for the same reason:
  only the thing on the other end knows.
- **A tool-use chip does not point at the terminal its call opened** (Plan 19).
  `ToolUseFinished` carries `Block` for exactly this reason in the block case. The stream case has
  no equivalent. So the audit says a call happened, the timeline says a terminal appeared, and
  nothing joins them. A `Terminal : TerminalId option` beside `Block` is the obvious symmetry. It
  was deliberately not smuggled into the step that would have needed it.
- **The jumpstarter console has an echo floor of one quiet period** (~50ms measured at 61-67ms
  round trip, set by `QUIET_SECONDS` in `examples/jumpstarter`). Its stream is a drain loop over a
  `pexpect` handle, not ownership of the fd. A person typing therefore sees their own echo that
  much late. The serial provider owns its fd and has no such floor.

  The floor is a coalescing window we chose. It is not a cost of teeing a handle the SDK owns. The
  drain matches a real pattern, returns on the first byte, and waits only long enough not to split
  a line. (An earlier version read with `console.expect([pexpect.TIMEOUT])`, which always waits
  its timeout out and never clears the buffer. It set the floor at the timeout and re-sent the
  console's whole history five times a second.)
- **An agent holds a lease where its own block has taken the screen, and nowhere else it could
  have run a block instead.** Two steps closed this gap. What is left is a boundary, not a
  shortfall.
  - Plan 19 step 3: a live-only source has no blocks, so `execute_command` has nothing to do
    there. The alternative was the provider's own write tool, past the lease entirely.
    `write_terminal` now takes the lease exactly as a peer does: visible in the holder field,
    stealable back mid-sentence, with every byte in the transcript.
  - Plan 20 stage 6a was a bug, not a policy. The alt-screen flip refused to hand an
    agent-authored block its terminal. A full-screen program waited for a keystroke that nobody
    was allowed to send. Its block never finished, and every command queued behind it stalled for
    ever. The flip now follows the AUTHOR, agent included.

  The boundary that remains: the agent cannot TAKE an instrumented terminal. It may only use the
  one that detection hands it, over a block already classified and on the record. Taking it would
  be the door around the classifier that `execute_command` is the one door for (Plan 23).

  The drain gate that accompanies live mode (Plan 13, stage 2e) is unchanged. A leased terminal
  holds its queue instead of typing into a session someone else owns.
- **A rejected command is silent to whoever issued it.** `SessionCommandResult` carries
  `CommandRejected reason`, and no client reads it. The browser has no notice surface at all, so
  `grep CommandRejected src/Yession.App` finds nothing. Every refusal a peer command can produce
  lands the same way: the button does nothing, and the reason exists only in the Process's answer.

  This was found while building the capability-approval prompt (Plan 27). Its own refusal ("what
  this repo asks for changed since you looked") is exactly the sentence somebody needs and cannot
  see. The gap is not specific to that command and not fixable inside it. A rejection needs
  somewhere to be shown, and there is nowhere yet.
- **A block waiting on a keystroke is announced to the agent and to nobody else.**
  `execute_command` answers `TerminalCommandInteractive` the moment detection hands the terminal
  over. The agent has `write_terminal`/`read_terminal` to resolve it. If it does not, the chat
  shows only a pulsing chip. The affordance a person needs is the terminal panel's ordinary lease
  bar, which they have to go and find. A handoff card in the chat was designed and not built.
- **The live viewport is proven host-free, never against a real pty end to end** (Plan 14,
  stage 6). Stage 6 closed the older "no browser viewport" gap. The panel renders a live screen,
  the holder's copy takes keystrokes, and the client composes it with the same emulator the
  Session uses.

  What no suite drives is the whole loop at once. Stage 6's own note says so. The keystroke
  translation is answered host-free under `Browser`, because a `KeyboardEvent` is the part only a
  real browser can answer. The pty suite pins the Process half separately. Neither end covers two
  peers sharing one real pty, with one of them typing into it.
