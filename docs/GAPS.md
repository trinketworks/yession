# Known gaps

This file lists what Yession does **not** do yet, as of the `9.x-beta` line. Everything below is
deliberate scope, recorded so nobody discovers it in production.

The project accepted Phases 1–4 and every numbered plan this file cites. The plan files are gone.
Each plan's reasoning now sits beside the code it governs. A plan number joins entries in
different sections to one piece of work:

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

Within a section, items come roughly in order of how much they matter. The sections have no rank.
The last section holds trust boundaries as sharp as the first section's.

## Security & trust

- **Repo trust does not cover the agent's own sandbox.** A repo's own sandboxes are lent a person's
  GitHub only by what their trust in the repo grants
  ([ADR](decisions/2026-11-01-a-repo-runs-with-the-github-of-somebody-who-trusts-it.md)). The
  session's own sandbox, where the agent's commands run, is lent the turn person's GitHub by every
  route it forwards, as every GitHub verb spends it. The repo's code runs there too, whenever a
  command the agent runs reaches it (a test script, a build hook).
  - Why: the agent acts for the turn's person, and a gate there would stand between them and their
    own verbs.
  - Open: lend the session's sandbox only what the person's trust grants while the checkout under
    it belongs to a repo they have not trusted.
- **Trust cannot be taken back yet.** A person's trust in a repo is their record, and nothing lists
  or removes a record ([GAPS](#persistence--data)). A route they left unticked can be granted later
  by answering again, but nothing asks again about a route they granted.
  - Open: the Manager's page lists a person's records and removes one, which makes the next session
    ask again.
- **A pull request from a fork runs on the base repo's trust.** Trust is about the repository the
  session cloned. A branch a pull request brings in from a fork is that fork's code, and it runs in
  the repo's sandboxes with the same trust. The card says "on any branch or pull request" for this
  reason.
- **A repo whose truster does not come back waits for them.** After a restart, a repo runs again only
  when the person it ran with signs in to the session again. Nobody else is asked, so nobody else
  can start it short of removing the repo and adding it again.
- **The trust card is the same for every reader.** It stands in the launch card's slot above the
  composer, one repo at a time. It shows every route the repo asks for, also to a reader whose own
  record already answers them, and pressing Trust is then the one-tap start.
- **A Manager route that changes state trusts a request that names no origin.** The
  request-origin rule (`ManagerUi.requestOrigin`, [§5 rule 24](technical-design.md#5-architectural-invariants))
  accepts `Sec-Fetch-Site: same-origin` or `none`, or, without it, an `Origin` whose host and
  port are the request's `Host`. Its third branch accepts a request with neither header.
  - Why: a browser sends at least one of the two on every POST, so such a request is no
    browser's, and the identity header already names its sender (automation, the tests).
  - Open: a browser old enough to send neither is treated as that client. No supported browser
    is.
- **The pages' Content-Security-Policy allows inline styles.** The Manager's documents and the
  session shell carry one policy (`Ssr.ContentPolicy`): scripts from this origin or with the
  response's nonce, no framing (`frame-ancestors 'none'`, the one framing defence), forms to
  this origin only.
  - The session shell's policy also has `'wasm-unsafe-eval'`, for asciinema-player's terminal
    emulator (WebAssembly). It admits compiling WebAssembly, not `eval`. The Manager's
    documents compile none and do not have it.
  - `style-src 'unsafe-inline'` stays for the `style="..."` attributes the session templates
    write (`View.fs`). A policy cannot allow an attribute by nonce. An injected style can
    restyle a page, not run script.
  - The session shell is revalidated by ETag, and a 304 carries no policy, so a kept shell
    keeps the nonce it was first sent with. A nonce is a secret of one response, so this costs
    nothing while the shell's markup holds no injected script.

- **A lent token rotates per command, and whether that earns its keep is open.** A declaration's
  `${github.token}` (`EnvironmentVariableRef.Lent`) is a stand-in. Each command borrows it for
  the act the command runs as, and gives it back when the next command starts.
  - Gain: a leaked stand-in dies with the next command. Each command spends the credential of
    whoever it runs for, never whoever ran the one before.
  - Cost: the value can only be a variable's whole value. The parser refuses a stand-in inside a
    larger string and points here. Anything that keeps a token across commands holds a dead token
    after the next command, and the proxy refuses it in words. Examples: a daemon that one
    command started, and a tool that writes the token into its own config.
  - Alternative: a stand-in per sandbox, resolved per request by whoever the current command
    runs for, keeps the attribution without the rotation.
  - Open: no measurement says whether the rotation is worth what it breaks.
- **Event attribution is in place, but presentation is thin** (Plan 07). Under a real strategy,
  events attribute to the Manager-verified user (`ActorRef.UserRef`). The identity rides the OIDC
  bounce, then a cookie, then a session-minted peer token, never a peer-controlled frame.
  Remaining:
  - Peer display names stay self-asserted.
  - The client UI renders a bare `UserId.value`, with no names or avatars from verified claims.
    The session carries and records `UserClaims` but does not display them.
- **BYO authorization trusts plaintext headers, over a socket only the proxy opens** (Plan 07).
  `--auth trusted-headers` trusts canonical `x-yession-*` identity headers from an operator-run
  authenticating proxy ([deployment.md](deployment.md#the-header-scheme)). The Manager serves
  it on a unix socket and refuses a TCP port at boot (`Listener.admits`), because every local
  process, and a container that reaches the host loopback, dials a loopback port as loopback.
  The example proxy listens on a socket too, so `serve` is the only peer that can assert
  `Tailscale-User-Login` ([§5 rule 18](technical-design.md#5-architectural-invariants)).
  Remaining:
  - **The socket's permissions are the boundary, so the boundary is the uid.** A process of the
    Manager's own user, unconfined, opens the socket. That includes a `host` work sandbox and
    the agent CLI under its default `host` backend (below). srt denies the path unless a
    grant names it, which on Linux a `Socket` grant then reaches (the `Socket` entry below).
    A container has no path to it.
  - **The proxy is still trusted.** The design that trusts nothing between tailscaled and the
    Manager is a strategy `--auth tailscale` that asks tailscaled's LocalAPI `whois` about the
    connection's peer, so the subject comes from the connection and not from a header. The
    recorded alternative is a signed-JWT header strategy against an operator JWKS
    (`Fable.Jose.jwtVerify` is already bound).
- **No `nonce` in ID tokens yet, and the flow makes that tolerable.** The ID token never passes
  through the browser. The confidential RP redeems the code over its own back channel, with its
  client secret plus the PKCE verifier, so no injected-token path exists for a nonce to close. A
  nonce becomes worth adding when a strategy first federates to an upstream OP.
- **A declared MCP server's tool descriptions reach the model's context with no trust check**
  (Plan 16, Plan 17). An external server's `tools/list` descriptions go straight into the prompt,
  and with always-available servers no second human approves them. Yession drops `instructions`
  for this reason. It cannot drop tool descriptions, because the model must read them to call
  anything. `ToolDescriptor.Foreign` already marks the affected set. The recorded mitigation is
  an `AutoApprove` flag on the DECLARATION, where the operator already is, not a per-call prompt.
- **A declared MCP server has no confinement, and neither has what it owns** (Plan 16). No srt
  or docker analogue exists for a serial port. The serial provider (`examples/serial`) runs on
  the host with whatever access its user has. Writing to the wrong tty can reflash a board.
  - The provider does not list ports it does not recognise. An unrecognised tty is usually the
    machine's own console. This is a policy, not a boundary.
  - Its control leg has no authentication and must stay on loopback.
  - A stream it OFFERS (Plan 19) gets the same policy. The url must share the host the operator
    declared the server at, use `ws` or `wss`, and carry no credentials. This stops a tool result
    from pointing a session at another machine.
  - A declared server can still hand the session a socket to anything on the box it runs on.
- **A second local address, with no authentication, beside the provider** (Plan 18). The
  jumpstarter example talks to an exporter that serves gRPC with `--tls-grpc-insecure` and no
  passphrase. So its claim arbitrates the provider's clients and nothing else. Any process on the
  box can dial the exporter and take the hardware from a holder. One host, one operator and
  loopback make that acceptable. A shared machine needs the passphrase that upstream already
  supports. A controller replaces the claim with a lease.
- **A driver method that never returns wedges one connection** (Plan 18). The jumpstarter example
  calls the SDK on its own thread, and nothing can interrupt a library call from outside. A
  method that blocks forever, or a stream whose next item never arrives, gets a timeout answer,
  and the example drops that connection so later calls reconnect. But the thread stays parked
  and holds one SDK client until the process ends: one thread per wedge, and only a misbehaving
  driver causes one. The answer reports the fault. Nothing fixes it.
- **A provider's lifecycle is nobody's** (Plan 16). No decision says who starts a provider:
  `systemd`, `launchd`, an operator, or nothing. "The Manager only declares" argues for nothing.
  Plan 17's poll retries a server forever and picks it up when it appears, so nothing has to
  restart to notice it. But nothing starts the provider either.
- **Remote WebRTC has no relay fallback** (Plan 09). Sessions are remotely reachable through an
  operator's proxy. The `/sessions/stream` registry drives the serving binding.
  `YESSION_SESSION_URL` (Plan 10) threads the public address into open links and redirect URIs
  ([deployment.md](deployment.md#sessions) shows each shape). Two limits remain:
  - The data channel connects peer-to-peer on host candidates only (no STUN/TURN). Remote use
    needs a network where the session host's addresses route directly, for example a tailnet,
    verified per deployment.
  - A visitor with no authentication can hold open peer connections that `PeerHello` refuses.
    Nothing throttles `/signal`.
- **User- and local-scoped secrets have exactly one writer: the connection broker** (Plan 08). The
  Claude sign-in stores an owner-scoped credential through the narrow `ConnectionAction` policy
  family. Under an attributed strategy, the owner is the user. Under `--auth localhost`, it is
  `LocalScope` ([ADR](decisions/2026-08-10-local-scope.md)). Remaining:
  - No GENERIC `/secrets` write surface for users exists. Sessions cannot `SetSecret` on
    `UserScope`, and the management UI has no secrets page.
  - Generic secrets and connection credentials are two families of the store, kept apart by
    type ([ADR](decisions/2026-10-26-secrets-and-connections-are-two-families.md)). Neither
    `set_secret` nor a `SecretRef` can name a connection credential.
  - A user's generic secret has no injection path. A `SecretRef` is written by a repository's
    file, not by the user, so it reads the session's own scope only. A user's secret can reach
    a sandbox only once a consent surface lets the user give it to a session on purpose.
  - Tests pin the policy rows for a session-less, user-only `AuthzSubject` (`Session = None`),
    but nothing constructs that subject yet.
- **Under `--auth localhost` a connected credential is deployment-wide.** Every visitor is the
  same unattributed subject. One Claude/GitHub connection serves every session and browser that
  reaches the Manager, and every visitor's agent turn spends it. Those visitors can already drive
  every session, so the boundary is the same. Authors stay `PeerRef`, so event ATTRIBUTION does
  not change. [deployment.md](deployment.md#what---auth-localhost-costs-here) gives both exits
  (`--secrets ephemeral`, `--auth trusted-headers`).
- **Under `--auth trusted-headers` every member is an operator of every session.** The rule
  establishes who the caller is. Nothing decides what they may do.
  - Holds: identity is per human, and credential custody is per human. A turn and a lent token
    run on the acting person's own credential (`Authority.credential`), so no member's turn
    spends another's account.
  - Does not hold: session ownership and membership. The Manager answers every attributed user
    (`gated`, `app/ManagerUi.fs`), so any member creates, launches, stops and archives any
    session, and declares MCP servers for all. `/authorize` (`authorize`,
    `src/Yession.Oidc/Provider.fs`) issues a code for any registered client to any attributed
    user, so any member joins any session, reads its log and terminals, drives its agent, and
    types into its sandboxes.
  - Two places cross the credential boundary. A repository's `setup:` block runs under
    whoever triggered the fold that started its sandbox (`Authority.configuredBy` in
    `RepoSandboxes.foldOnce`, built by `Commands.startWorkSandboxCall`). A webhook
    delivery goes to every session whose filter matches (`deliver`, `app/WebhookRelay.fs`).
  - Open: a session owner, a member list the owner edits, and a policy row for the Manager
    verbs and `/authorize`. Nothing is built.
    [deployment.md](deployment.md#what-a-tailnet-member-can-do) tells operators the model as
    it is.
- **No transport encryption guarantees beyond WebRTC/DTLS.** The Manager and every session bind
  127.0.0.1, or a unix socket under `trusted-headers`. Loopback HTTP is the RFC 8252 pattern.
  Two listeners bind wider, because a sandbox has to reach them: the git gateway and the
  credential proxy. Each binds where a sandbox reaches this host (`Interop.Doors`). On Linux
  that is 127.0.0.1, 127.0.0.2, and a native Docker daemon's session network gateway. On macOS
  it is 127.0.0.1 and every other address this host has, link-local aside, so a LAN or a
  tailnet reaches them there. A capability minted per sandbox admits a request. The headers of
  `app/GitGateway.fs` and `app/CredentialProxy.fs` say why. Nothing here is LAN-safe without
  the operator's proxy in front.
- **The Manager and Process read plaintext**, per the stated Phase 1–2 threat model. The design
  allows command-to-container encryption, but no code does it yet.
- **The authentication strategy gates the management UI** (Plan 07). A `Denied` outcome is a 401
  on every UI route, and the default `--auth none` denies everything. Under `--auth localhost`
  anyone with local access can manage sessions. That is the localhost trust rule, not an
  oversight.
- **Sandboxes confine by default. `host` is the opt-out.** The WorkSandbox defaults to `srt`
  (bubblewrap on Linux, Seatbelt on macOS), so OS confinement applies to agent-issued commands.
  It does not apply to the agent CLI: the AgentSandbox defaults to `host`
  (`YESSION_SESSION_AGENT_BACKEND`, see the agent CLI entry below).
  `YESSION_SESSION_WORK_BACKEND=host` gives no filesystem or network confinement, only the env
  allowlist, which the credential-leak regression test pins. An operator must choose it
  deliberately. `=docker` is the full-userland option for the WorkSandbox.
  - **A Linux host without the confinement tools cannot start a session.** srt needs bubblewrap,
    socat and ripgrep. The Nix installable names all three. An npm install of a release on a bare
    box does not have them. The session then fails when it creates its sandbox, and does not start
    unconfined. That is the intended trade: install the tools or choose `host` deliberately.
  - **srt reports a probe it could not RUN as a tool that is not there**, so a bad minute makes
    srt refuse every sandbox in a run. Yession checks the refusal against this process's own
    stat and retries a start that settled nothing a few times, then forgets it (`StartFailure`,
    `startAttempts` and `startBackoffMs` in `app/Sandboxes.fs`). What Yession reports
    contradicts srt's sentence instead of repeating it. Undo this when srt stats an absolute
    path (still `which` in 0.0.73) or at least surfaces the spawn's errno.
  - **An srt sandbox reads what its policy names and nothing else** (Plan 24). srt's read model is
    permissive by default. Before Plan 24, Yession denied only the invoking user's home, and every
    agent-issued command could read `/etc`, a checkout the session never got, and (with the
    Manager outside the operator's home) another session's data directory. The deny is now `/`,
    which srt re-expands from the children of `/` at each spawn. It has three holes: the policy's
    read paths, everything the policy may write, and the host runtime.
  - **The host runtime is the half of that scope this code cannot derive.** The allow-back is a
    platform list (`SrtTools.Runtime`) plus the install prefixes of what is already running
    (`runtimeReadPaths` names them). The comment that opens `SrtSandbox` in
    `app/Sandboxes.fs` says why, and `runtimeReadPaths` says why `YESSION_SESSION_READ` is not
    folded in. A path that nothing names fails loudly and locally: a command cannot find its
    interpreter.
    - **No test covers the darwin list.** No job here runs a suite on darwin. The `Srt` tier
      pins `linuxRuntimePaths`. `darwinRuntimePaths` is what a Seatbelt profile conventionally
      allows back, checked against srt's implementation only. This already cost one release: on
      macOS every git verb ran PATH's `/usr/bin/git`, a shim that resolves a developer
      directory through a `/var/select` symlink. srt's macOS escape hatch allows metadata on
      DIRECTORIES only, so the symlink stayed denied. Sessions reported
      `xcode-select: error: unable to read data link … (Operation not permitted)`, which reads
      as a broken Xcode install and is not one. The fix was to name git (below), not to widen
      the scope for a shim.
  - **Every binary a confined spawn execs is NAMED. Git was the last exception.**
    `YESSION_BIN_GIT` joins the other `YESSION_BIN_*` variables (`SrtTools` in
    `app/Sandboxes.fs`). The installable sets it on both platforms, unlike the Linux-only srt
    tools. Unset, the verbs fall back to PATH, and the git sandbox
    proves `git --version` first
    ([ADR](decisions/2026-08-28-one-function-builds-every-git-spawn.md) has the refusal and the
    fault it once caused).
    - **A denied read is ENOENT on Linux and EPERM on macOS.** git shrugs at the first and dies
      on the second, so a whole class of fault is invisible on the platform CI runs. The
      regression test therefore plants a MALFORMED config, not an unreadable one (same ADR).
    - **`/proc` and `/sys` are outside the scope by construction.** srt's root-deny expansion
      skips both. srt remounts `/proc` itself, and a tmpfs over `/sys` breaks tooling for a tree
      that nobody can write anyway. Neither is a route back to the denied paths: `/proc/<pid>/root`
      does not resolve to the host root from inside, and bwrap gives the sandbox its own pid
      namespace. But the scope does not cover them either.
    - **srt's own default write paths reach into the denied home.** `getDefaultWritePaths()`
      always allows `~/.npm/_logs` and `~/.claude/debug`, and srt re-binds an allowed write path
      that its read-deny tmpfs wiped. So a sandbox can read AND write both, though its policy
      names neither. They resolve against the Session Process's `os.homedir()`, so the fix is
      that process's own `HOME`, not the policy.
    - **A symlink INSIDE a granted directory is still denied, and
      srt work sandboxes do not run builds.** Seatbelt matches paths as written, and srt
      normalises an allow to its realpath. So on macOS a granted path can be unreachable by the
      spelling a process uses, symlink nodes en route (`/tmp`, `/etc`, `/var`) refuse lstat, and
      MSBuild's hardcoded `/tmp/MSBuild<pid>` pipes cannot bind. A fork of srt that fixed this
      worked, and we retired it: the next wall was Fable enumerating `/Users` for path casing,
      which no grant allows without exposing dirent names all the way up. A build wants a
      machine, not a syscall filter. Work that builds runs in a CONTAINER, and srt confines the
      light default sandbox (checkout, small edits). The
      [2026-08-28 ADR](decisions/2026-08-28-no-srt-fork-for-symlink-metadata.md) argues this,
      and its argument stands.
    - **A `Socket` grant is path-scoped on macOS and widens to every socket on Linux.** #335
      gave a socket its own axis, and `limitsFor` in `app/Sandboxes.fs` says why. On Linux,
      `wrapCommandWithSandboxLinux` takes `allowAllUnixSockets` and never reads the path list,
      so srt blocks every `AF_UNIX` socket or none. Yession lets every socket through when a
      policy grants one, and says so: the host realises a `Socket` leaf as `Coarsened "sock:*"`,
      and `SrtSandbox.configFor` sets `allowAllUnixSockets` only for that report
      (`SrtConfig.AllowAllUnixSockets`). The report reaches the approval prompt (`sock:<path> ~>
      sock:*`, `RealisedClosure.describeOn`), the `WorkSandboxStarted` event and start note
      ("adjusted"), and the `work_sandboxes` panel's `degraded` column. The widening itself
      stays: scoping by path on Linux needs a mechanism that seccomp cannot give srt. The
      Srt-tier case for macOS path scoping reports a visible skip on Linux. The cheap tier pins
      the Linux side.
      - **That includes the Manager's socket under `--auth trusted-headers`,** if the sandbox
        can see its path: the command runs as the Manager's uid, which the socket's `0660`
        admits, and it could then assert any `x-yession-user`. srt's root deny hides the path
        unless a grant names its directory or one above it. Nothing refuses such a grant yet;
        [deployment.md](deployment.md#where-the-manager-listens) tells the operator to keep
        the socket outside every granted path and outside `--data-dir`. A guard that refuses
        a policy whose paths cover the control socket is the recorded fix.
    - **The start-up checks cannot see a granted executable that cannot RUN.** Measured: the
      `nix` an operator would name on nix-darwin (`/run/current-system/sw/bin/nix`, canonically
      `…-nix-2.34.6+1/bin/nix`) aborts under Seatbelt on every subcommand, including
      `--version`. The Lix binary from this repo's dev shell runs fine in the same sandbox (the
      [2026-08-28 ADR](decisions/2026-08-28-no-srt-fork-for-symlink-metadata.md) has the
      table). An `Exec` leaf joins PATH in front, so declaring that nix makes a working nix a
      broken one. The checks (#333) give an exec leaf `test -r`, which passes. `test -x` reads
      the file mode, the same lie `test -w` tells. Only running a binary answers "can this run",
      and no invocation is safe for an arbitrary binary. No fix is chosen.
- **The agent CLI runs through the `spawnClaudeCodeProcess` seam with a policy env**
  (AgentSandbox): an allowlisted baseline plus proxy passthrough, a per-session scratch HOME
  (`<data>/agent-home`, so `~/.claude` state lives and dies with the session), exactly one
  credential, and a process-group kill on the SDK's forwarded abort signal. srt adds OS
  confinement: the CLI reads and writes its scratch HOME, reads the host runtime, touches no other
  operator file, and reaches only `AgentSandbox`'s domains (`YESSION_SESSION_AGENT_NET`). Docker
  is BY DESIGN not an agent backend: a container per session boot is the opposite of the
  sub-second start the agent needs.
  - **The default is `host`, and unlike the WorkSandbox's that is a shortfall, not a decision.**
    `YESSION_SESSION_AGENT_BACKEND=srt` is the answer this wants. #364 made it the default, the
    release gate's live turn then stalled for 90 seconds, and the default went back. The gate
    now passes under `=srt`. The default stays `host` until a macOS run under `=srt` is
    measured: the deployment in [deployment.md](deployment.md) is macOS, and every run so far
    is Linux.
    - What #373 recorded: the CLI came up FULLY (MCP server connected, tools listed, credential
      accepted, `init` emitted), then every request to `api.anthropic.com` failed in about 3ms
      with a transport-level `Connection error.`, and srt's proxy logged ZERO connections.
    - **The Bun CLI honours the proxy.** #373 read the stall as Bun's `fetch` ignoring undici's
      dispatcher. That reading is wrong. The vendored `claude` (nixpkgs 2.1.283), run outside
      srt against two loopback listeners, sends `CONNECT api.anthropic.com:443` to
      `HTTPS_PROXY` and to `https_proxy`, with `Proxy-Authorization: Basic` from a credential
      in the URL. With `ANTHROPIC_BASE_URL` at the second listener, its requests go through
      the proxy when one is set and direct when none is.
    - **The gate passes under `=srt` on master.** A `verify.yml` dispatch with only
      `YESSION_SESSION_AGENT_BACKEND: srt` added (`LiveAgent Ports Native Srt`, the live
      `add_repo` case) was green, and the clone landed in 7.6s (run 40490505787). Three changes
      since #373 can each have cleared the stall, and none was measured alone: the nixpkgs pin
      move (#980, a newer CLI), srt 0.0.67 to 0.0.77 (#989), and a separate srt manager per
      sandbox (#1005).
    - **srt 0.0.77 names its proxy `localhost`, and its relay listens on IPv4 alone.** Every
      proxy variable is `http://<user>:<token>@localhost:3128`, and the relay inside the
      network namespace is `socat TCP-LISTEN:3128`. With `::1 localhost` in `/etc/hosts`,
      some of the CLI's calls fail with `connect ECONNREFUSED ::1:3128`; its API call still
      got through on the box that measured it. srt 0.0.80 listens on both families, which
      fixes this upstream.
  - The SDK's spawn seam is SYNCHRONOUS and srt's wrap is not. So the srt tier gives the SDK a
    stand-in process whose streams are live at once, and joins the real child to them when the
    wrap resolves. The `Srt` suite drives it end to end (stdin in, stdout out, exit code).
- **The strict confinement profile mounts a fresh `/proc`, which a container can refuse.**
  `SandboxNesting` in `app/Sandboxes.fs` states the two profiles and why an operator configures
  `weak` (`YESSION_NESTED_SANDBOX=weak`), never a fallback. An unset value means strict, and a
  session on a host that cannot run strict fails at boot. Both profiles nest a second user
  namespace for srt's seccomp helper, unless the policy grants a unix socket, which turns the
  helper off. The refusal reads "bwrap: Can't mount proc on /proc: Operation not permitted". A
  non-root caller's command holds no capabilities. Under the helper, a root caller's command
  holds a full set inside the helper's namespace, which maps to the caller's uid 0.

  `weak` does not help where neither profile runs:
  - A root caller without `CAP_SETFCAP` in its bounding set. The kernel refuses each namespace's
    uid map: "bwrap: setting up uid map: Operation not permitted". The Docker backend's work
    container, run as root, is one: it adds back only a few file-ownership capabilities. A
    non-root caller does not need the capability.
  - A host that strips capabilities from a new user namespace. srt's README names Ubuntu 24.04's
    default (`kernel.apparmor_restrict_unprivileged_userns=1`), and `pr.yaml` clears it.

  A Claude Code container runs strict: it is a VM, and `check` runs there as root, with
  `CAP_SETFCAP` and an uncovered `/proc`. No measurement says what Docker's default seccomp and
  AppArmor profiles add. `check` asks srt's own command line to run the configured profile before
  it declares the `Sandbox` capability, and refuses `Srt` (confinement under test) under `weak`.
- **The Docker backend runs through the `dockerode` SDK, and the `verify` gate tests it.** The
  session id (Crockford base32, always a valid Docker object name) names the containers and a
  per-sandbox named workspace volume. The backend interprets all of `EnvironmentSpec`:
  image/build, mounts (including the workspace volume), working directory, env-var refs, and
  secret refs (resolved at sandbox spawn). The container drops all capabilities, adds back
  the file-ownership ones a real build needs (`CapAdd` in `app/Sandboxes.fs` names them and says
  why), and sets `no-new-privileges`.

  The container runs the image's default (usually root) user, by DECISION. With
  `DAC_OVERRIDE`, that root bypasses file permissions on anything MOUNTED, so the boundary is the
  mount set (`mountPlan`: the session's checkouts and what the operator granted). Non-root breaks
  the named workspace volume, which Docker creates root-owned. Files written through a BIND
  mount belong to root on the host: a nuisance, not an escape.

  Each session's containers share one Docker network of their own (`DockerSandbox.Network`).
  The git gateway and the credential proxy never listen on every interface: they bind
  loopback and srt's address at start (`hostDoorsFrom`), and on a native daemon they open the
  session network's gateway when its first container starts (`hostAlias`). What remains:
  - **On a daemon in a VM (Colima, Docker Desktop) a container still reaches the host
    loopback.** The VM forwards `host.docker.internal` there, and it is the only way from a
    container to the host, which the gateway and the proxy need. So nothing on the host
    loopback may trust its peer ([technical-design.md §5](technical-design.md#5-architectural-invariants)):
    the Manager and the example proxy listen on unix sockets under `--auth trusted-headers`,
    and what is left there (the gateway, the proxy, the sessions' ports) authenticates by
    capability, cookie or token. The sessions' unauthenticated routes (`/signal`, `/login`)
    are the denial-of-service surface the Remote WebRTC entry names.
  - **On a native daemon a container reaches every address of this host that is not
    loopback.** Docker accepts a container's traffic to the host's own addresses, so a
    listener another program binds on every interface answers this session's containers.
    Yession's own listeners are not among them: another session's gateway answers there only
    to a capability minted for that session.
  - **On macOS the srt door is every interface address.** srt dials this host by name, and the
    resolver picks which of its addresses the name is, so each is a door
    (`hostDoorsFrom`). A capability is still what opens it.

  Known and accepted for now:
  - No backend has resource limits.
  - A docker exec has no kill. "Kill" closes the stdin stream, so a runaway that ignores EOF
    burns CPU until the sandbox goes.
  - The operator's resource ceiling is global: one profile for every repo the session hosts. An
    operator cannot offer a socket to one repo and not another.
  - A host with no reachable daemon surfaces at the first `ensure_environment` as a raw dockerode
    socket error that names neither the missing backend nor whose configuration is at fault.
    `repoWorkBackend` is hard-coded with no preflight.

  The suite (`tests/Yession.Tests/DockerIntegration.fs`) needs the `Docker` capability
  (`requireCapabilities` in [tasks.fsx](../tasks.fsx)).
- **Secrets are a real Manager-owned store now** (Plan 06). The header of `app/SecretStore.fs`
  describes it: AES-256-GCM per-entry ciphertext in `<DataDir>/secrets.json`, a KEK in the OS
  credential manager (`@napi-rs/keyring`, imported non-extractably each start), and `resolve` as
  the only value-returning `/control/secrets/*` route. A pure default-deny `Policy.authorize`
  covers the composite session+user identity. `SecretRef` injection reads the session's own
  scope only. It never reads a bound user's scope or the Manager's process environment, and a
  Manager with no store answers every reference with a refusal that names it.

  Remaining, deliberate:
  - **Hosts without a credential manager run in-memory only, unless the operator brings a key
    store** (dev containers, CI, headless servers). Secrets die with the Manager, the boot says
    so loudly, and no plaintext key file exists. `--secrets ephemeral` makes in-memory an
    operator CHOICE (logged at info, not warn). `--secrets durable` refuses the boot on such a
    host. `--keystore exec:<helper>` hands the key to a helper the operator wrote, over a
    git-credential-helper protocol, so a server with a KMS, a Vault or a TPM has a durable store
    without yession knowing any of them
    ([deployment.md](deployment.md#your-own-key-store)). The `Keyring` capability covers the
    real keyring in tests; `Ports` covers the helper.
  - **No AMBIENT scope.** `LocalScope` is deployment-wide but not ambient: the Manager authorizes
    it against unattributed access it recorded for that run. It holds connection credentials
    only, and generic `SecretAction`s on it deny. User-scoped GENERIC secrets have no writer (see
    the connection-broker entry above). There is deliberately no peer scope (`Secrets.fs` says
    why). User↔launch and local↔launch bindings both last one launch. A re-login re-forms them.
  - **Two deliberate value-returning routes, both resolve-shaped,** both gated to the caller's
    readable scopes:
    - `/control/connections/resolve` (Plan 08) gives a connection credential to the calling
      session, because an agent turn needs the token in-process.
    - `/control/secrets/resolve` feeds `SecretRef` env injection at the session's sandbox spawn
      (sandboxes are session-owned), from the session's own scope only.

    Refresh tokens never leave the Manager. No agent-facing tool wraps either route.
  - **Secret-token-injection is future work.** A resolved secret enters the sandbox as a plain env
    var, so anything the agent runs inside can read it. Under srt, the egress allowlist bounds
    where a read value can be *sent*, which covers most of the practical risk. The real fix is
    egress substitution: a placeholder inside, the real value substituted at the proxy toward
    declared hosts. srt implements that (`credentials.envVars` with `mode: "mask"` and
    `injectHosts`). What remains is to route `SecretRef` injection through it.
  - **No KEK rotation/recovery.** A lost credential entry orphans the store loudly, and the
    operator deletes the file.
  - `@napi-rs/keyring`'s per-platform prebuilds join `node-datachannel` in the "unsigned
    third-party native binaries" trust bucket below. CI exercises Linux/Secret Service. Only
    field use covers macOS and Windows.

## Runtime & topology

- **The process split is complete** (Phase 4). Each session is a child OS process of the Manager.
  Supervision and secrets custody cross the boundary as a secret-scoped control RPC (environments
  are session-owned via the sandbox seam). The htmx management UI creates, launches, resumes and
  stops sessions. But **children die with the Manager**: no daemonising and no orphan adoption,
  so a Manager restart stops every running session. `GET /sessions/{id}/open` answers a stopped
  session with the opening screen, which launches it by POST and lands on its address (Plan 11),
  and the client offers that route when its session has gone.
- **The hook relay takes a session's word on what it may receive.** The Manager verifies a
  DELIVERY (it comes from the internet) and trusts a SUBSCRIPTION (it comes over the
  authenticated control channel from a child the Manager spawned)
  ([ADR](decisions/2026-08-29-the-manager-relays-hooks-it-cannot-read.md)). Accepted:
  - **Only HMAC over the raw body is verifiable.** Schemes that sign a constructed string with a
    timestamp (Stripe, Slack) are not, and the boot refuses one. The relay forwards a delivery's
    headers, so a session could verify its own endpoint, but nobody has built that.
  - **A rotation is a counter an operator bumps**, and the previous secret stays valid until the
    next bump. No timer expires it, so an operator who never bumps again leaves two live secrets.
  - **Endpoints need a durable secret store**, because the signing secret derives from its KEK.
    The boot refuses them on an ephemeral store, instead of a secret that silently stops working
    inbound at the next restart.
- **The Manager is practically a singleton.** Data directories are per-instance and session ports
  are OS-assigned. But two Managers over the SAME data directory are not supported: there is no
  lock until the SQLite move. The UI's fixed default port means a second instance must
  configure its own.
- **Session ports are OS-assigned and change on every launch.** A session has one stable URL,
  `/sessions/{id}/open`. Its target address is stable only where `YESSION_SESSION_URL` derives it
  from `{id}` (Plan 12). On the zero-config loopback default, the origin moves with the port, and
  the browser leaves behind everything it kept for the session by origin: the document's
  IndexedDB store, the Cache API history store, and one transcript store per terminal. The client
  says so (`PublicAccess.sessionAddressIsStable`). Nothing migrates the stranded stores.
- **Health is a liveness report, not a health check.** A launch reports busy/idle on
  `POST /control/activity`. The Manager reaps on silence only when an operator sets an idle
  timeout (`IdleTimeout` is `None` by default). Unset, a child that wedges after readiness shows
  as running until it exits. Set, the Manager stops it as `NeverReported` without a diagnosis.
- **The Manager→Session notification channel is a transport without a producer yet.** The child
  subscribes to `GET /control/notifications` (SSE, per-launch secret). The Manager fans out
  through `ProcessManager.Notify : SessionId -> SessionNotification -> unit`. The child's handler
  MAY record a durable `SessionEvent`. But **nothing calls `Notify` in production yet**, the
  payload (`SessionNotification.EnvironmentChanged of unit`) is a placeholder, and the default
  handler only logs. The intended first producer: the Manager detects an out-of-band environment
  change, for example a container it owns dies without the session stopping it.
- **A second reverse leg, the MCP server set, ships end to end** (Plan 17). The child subscribes
  to `GET /control/mcp` (SSE) and gets its session's resolved set at once and on every change.
  `ProcessManager.publishMcpServers` publishes from the operator's durable declarations, on every
  registry write and at boot. The leg is per SESSION (`app/KeyedRetainedHub.fs` says why). The
  child holds the set (`app/SessionMain.fs`) and `McpClient` connects it, so a declared server's
  tools reach agent turns.
- **A third reverse leg, connection statuses, has a real producer** (Plan 08).
  `GET /control/connections` streams each launch its readable connection metadata: a snapshot on
  subscribe, and a fresh list on every credential change or new binding. The session's agent gate
  and `/claude` status surface consume it. `NotificationHub<'n>` serves this leg and the
  notification one.
- **Peer-to-peer is star-shaped through the Process.** Clients sync Yjs state via the Session
  relay, not with each other. Yession does not use y-webrtc-style meshes.

## Persistence & data

- **Everything durable persists** (Phase 3). The event log and the Yjs document both survive
  Process restarts. The doc store uses a sidecar `*.doc.jsonl`, compacted at open. Browser
  clients keep the document in IndexedDB (`y-indexeddb`), keyed by the session id in the
  bootstrap page. The event log's browser cache is the client's own Cache API store
  ([technical-design.md §2.3](technical-design.md#23-transport)).
- **The JSONL event log loads fully into memory.** It has no compaction, rotation or
  checksumming. A corrupt line fails the whole open, loudly by design. The doc store compacts
  only at open, so a long-lived Process grows its sidecar until restart.
- **Nothing lists or removes a person's records.** A record is what session code keeps for a
  person across their sessions
  ([ADR](decisions/2026-11-01-records-are-a-third-family-of-the-store.md)). A session reads and
  writes one by its name, and that is all.
  - Why: no feature has needed more yet.
  - Cost: a record nobody needs any more stays in `records.json` until it is edited by hand. An
    owner holds at most 512 records, so one who reaches that limit cannot add another.
  - Open: the Manager's own page could list an owner's record names and delete one without
    understanding it.
- **Cookies gate event ranges for browsers. Headless clients still put a minted peer token in the
  range URL** (`?token=`). The browser path sends the same-origin auth cookie with each request, so
  the URLs, and the cache keys the client stores them under, carry no secrets. The token-in-URL
  path remains for Node clients and tests, scoped to per-process minted tokens that die with the
  session.
- **A session opens cold with no network** (Plan 20). A service worker at the mount keeps the
  shell (network-first, because it names the fingerprinted assets) and this build's assets
  (cache-first, because their address pins their bytes), and nothing else. The event log is the
  page's own cache. `/me`, `/signal` and `/queries` are liveness questions that a cached answer
  gets wrong. The worker needs a secure context, so a session on plain HTTP at a non-loopback
  address cannot open cold. There, the settings pane names the missing capability and the flag
  that restores it.
- **Nothing measures whether the history store holds.** `storage.persist()` is a request, not a
  guarantee. Chrome grants it for an engaged site. Safari grants it essentially only for an
  installed app, and caps script-writable storage at seven days without user interaction. So the
  exposure is the session nobody opened in a week, which is the one somebody most wants back. A
  walk knows how many answers it served locally and how many went to the network. That ratio
  would settle the question, but it is not on the OTel resource yet. So the idea to replace the
  Cache API store with an IndexedDB one keyed by offset has no evidence.
- **Nothing drops a session's kept history when the session is gone.** Each session names its own
  caches (`yession/session/<id>/events`, and one per terminal). A deleted or reaped session
  leaves them behind for ever. With `caches.keys()`, a sweep is a filter over the sessions the
  Manager says still exist. No surface does that sweep yet.
- **The history feed degrades explicitly, and only history degrades.** The event feed is the one
  leg on HTTP instead of the data channel, so it fails on its own. It once failed silently, as a
  timeline that never filled while drafts, title and presence kept syncing. Now a read returns
  `Result<EventPage, FeedFault>`. A resilience policy (`Yession.Domain.Resilience`, composed in
  `app/browser/Browser.fs`) retries only transient faults, with capped exponential backoff and
  jitter (`Client.EventFetch.policy` holds the numbers). A settled failure parks the loop and is
  part of the page's one link state (`SessionLink.Stalled`), which the status line reports. Writing
  stays enabled, because it is CRDT state in the local doc. One supervised loop drives the session
  leg, capped at a minute (Plan 20). The browser's `online` event and a *Try again* control on the
  status line poke it. A REFUSED peer parks until somebody asks, because a schedule would send the
  same refused token.

## Browser client

- **Rendering is Fable.Lit (lit-html), a reconciling renderer.** The view is a total function of
  the model, rendered into `#app` on every change, so focus and caret survive re-renders. The
  only manual DOM work left pins the chat scroll and positions collaborators' cursor markers,
  because a native `<input>` exposes no per-character geometry.
- **Sending a draft still bypasses the Elmish loop, and moving it in needs Ylmish to change.**
  Every other control dispatches a message: `ClientModel.update` answers with `ClientEffect`
  values, and `Client.Ports` runs them after their model. The read cursor, the reads out and
  presence work this way too (`ClientModel.reads`, `presenceToSend`). The drafts remain: every
  Send of a draft or a terminal draft, by button or by key, calls a verb on `Client.Connection`
  (`SendDraft`, `SendTerminalDraft`). Inside one `doc.transact`, the verb copies a body root the
  app owns beside the synced model into the queued entry and dispatches the send message. That
  one transaction is the invariant, and the comments at the verbs in `Client.connect` give both
  reasons.

  It holds today only because a dispatch from a click handler runs synchronously, and
  `withYlmish` wraps each update in a `Y.transact` that nests inside the open one. As an Elmish
  command, it breaks: the dispatch waits until the command returns, so the model's write lands in
  a second transaction. The fix belongs in Ylmish: an update needs a way to declare doc writes
  that run inside the transaction `withYlmish` opens for it, before the model's own write. Then
  "seed the queued body, clear the draft, add the entry" is one message, one update and one
  transaction, and the cheap tier can assert the writes as values. To design:
  - Whether the writes are opaque `Y.Doc -> unit` functions or a typed vocabulary over the roots
    an app declares (copy a fragment, clear a text).
  - Which origin they carry, given that the binding filters its own echoes by origin and these
    roots sit outside the binding.
  - Whether this is a new `Program` combinator or a change to `Options`.
- **One WIP draft per client, co-editable by any peer** (Plan 03). The author is the key of a
  draft (`Map<PeerId, DraftState>`), so the structure, not a runtime cap, gives each client at
  most one. Any peer may co-edit and send any slot, and the entry goes to the draft's AUTHOR.
  The author's `QueueId` makes two concurrent sends one entry. Send clears the slot, so a client
  still queues many messages by sending repeatedly. Drafts and queued messages are rich
  ProseMirror editors on a Yjs `XmlFragment` (markdown typing, bold/italic/code, lists,
  paste-as-markdown, undo/redo). Presence cursors overlay every collaborative field (the title
  input and the body editors) with each peer's caret, selection, colour and name, relayed over
  ephemeral `Presence` frames (never durable). Invariant 4 (clean send) has a dedicated Hedgehog
  property. Broader draft-op schedules (participation, offline rejoin) are the follow-up.
- **A 401 from `/me` goes to `/login` unconditionally.** No in-app "signed out" state exists. The
  client rides the OIDC bounce again. An unreachable session keeps the cached shell local-first,
  and once the disconnection settles the client offers the `/sessions/{id}/open` card.
- **No browser support matrix.** Tests run on Chromium (headless, in CI). The ICE gathering
  settle-fallback should cover Safari and Firefox mDNS behaviour, but nobody tests those browsers.
- **A session's port, pid and build are no longer on the manager page.** They shared the status
  cell with the session's own summary line, and the two did not fit (measured: 460px wanted of a
  384px cell). The summary answers the page's question (which of six sessions wants me), so the
  diagnostics went, and the column shrank to 256px. `SessionRegistryEntry.Build` still rides the
  registry stream, and the Manager's own build is on the page header. Below `xl` the summary
  yields too (the column is 100px), and a phone gets it from the tab title. The way back, if
  wanted, is a row that opens (click the id to show the process detail). Nobody has built it.

## Visual design

[visual-design.md](visual-design.md) is the doctrine. This section lists what the doctrine says
and the code does not do yet.

An audit on 2026-10-06 found 203 defects. Eleven reviewers read the doctrine, then the product:
every surface at 1440x900, 390x844 and 844x390 (the stylesheet treats a short screen as a phone
however wide), plus static passes over `Style.fs` and every class literal outside it. The list
groups them by the part of the code that owns the fix, then by the doctrine's "Deciding" order:
floor, honesty, one verb, vocabulary, grid, less.

The list dates from that audit, against a doctrine that has since moved. A finding's name, not a
line number, identifies it. Check each against today's code AND doctrine before you fix it.
#1276 rewrote principle 2: chrome may now show grouping, nesting and selection, and the rule
that only a field or a button carries a border is gone. Close a finding that no longer
reproduces, or that cites a rule that no longer exists. Do not edit the list one line per fix.
After the fixes land, one pass rewrites it to what still reproduces.

### The marks and voices (Strokes, Motion, Typography, Statuses)

- **C-19** (honesty) — Waiting statuses wear the colour that means interactive
- **C-4** (honesty) — Two red marks on one screen when a credential is lost
- **H-17** (honesty) — A ring means "ended" on a terminal and "watching" on a person
- **X-3** (honesty) — "Running" wears three colours. "Queued" wears live green
- **C-11** (vocabulary) — Settings: a section label and a field label are one face, 24px
  apart
- **G-2** (vocabulary) — Green and blue mean the opposite of the doctrine on commands
- **G-3** (vocabulary) — Four red marks on one screen of blocks
- **H-15** (vocabulary) — Code writes the 6px disc nine times
- **S1-12** (vocabulary) — Bare border utilities written outside Stroke
- **S1-13** (vocabulary) — Hollow dots draw a border
- **S1-29** (vocabulary) — Motion durations written as literals
- **S1-24** (grid) — Raw sizes off the ramp
- **S1-25** (grid) — Line boxes overridden on ramp steps: a missing 13/20 step
- **S1-26** (grid) — Hand-spelled measures, offsets and tracking
- **S1-34** (less) — Dead vocabulary

### The control faces (Buttons)

- **A-1** (floor) — Phone controls stay 24-32px, below the touch target
- **B-7** (floor) — Phone buttons in the launch card are 32px, and START states a height it
  never gets
- **S1-8** (honesty) — Icon buttons press with `active:` and cannot be held
- **S1-9** (honesty) — `disabled:*` on three controls
- **X-5** (one verb) — Two bordered faces stand in for two weights
- **A-8** (vocabulary) — Filter chips are a second bordered face at a second height
- **G-19** (vocabulary) — The door to the pane speaks smaller than the sidebar's door
- **G-9** (vocabulary) — The refusal's dismiss is a bordered box on a notice it rides
- **S1-14** (vocabulary) — Waiting face is an important border override, and unused
- **S1-18** (vocabulary) — Cancelling the UA border: 32 sites, no shared bare value
- **S1-23** (vocabulary) — Touch-target literals with no token
- **X-10** (vocabulary) — Going to the other column has two faces
- **X-7** (vocabulary) — The step-up for a verb with room is spent on Send alone
- **X-9** (vocabulary) — Bare verbs speak in four voices
- **D-12** (grid) — Chapter name grows to 16px on a phone, off the ramp
- **S1-27** (grid) — Control heights other than spacing-control
- **X-19** (grid) — APPROVE ON GITHUB.COM wraps inside a 32px button

### Fields, listed rows, pivots, display pics, the starting screen and the editable title

- **C-1** (floor) — The phone's "Details" disclosure is a 16px target
- **C-2** (floor) — Settings fields and bar actions are 32px on a phone
- **F-3** (floor) — Picker, New message and draft summaries are under 44px on a phone
- **X-1** (floor) — Confirms B-7/A-1: settings drawer controls stay 32px on a phone
- **S1-7** (honesty) — Editable names say "editable" only on hover or focus
- **C-14** (vocabulary) — The device code is drawn as an input and set in UI sans
- **F-9** (vocabulary) — One down-arrow is a select caret, a reorder verb and a jump
- **S1-15** (vocabulary) — Variant-prefixed `border-*-0` undoing a column's divider
- **S1-19** (vocabulary) — Rounded rectangles
- **S1-20** (vocabulary) — Alpha-mixed colours instead of roles
- **S1-22** (vocabulary) — Z-index literals: write three named layers
- **X-8** (vocabulary) — Disconnected wears two status faces by width
- **C-7** (grid) — On a phone, NO AGENT takes the title's room and clips the session id
- **C-8** (grid) — The phone band is 56px in the header and 88px in the drawer
- **C-9** (grid) — The sidebar's rule leaves the band's hairline whenever a connection
  report is up
- **X-15** (grid) — Each column has its own gutter
- **X-17** (grid) — The sidebar's rule is not the band's, and moves
- **C-17** (less) — In landscape, the notice bar and header take about a quarter of the
  screen

### The timeline, the fold, the ask card

- **E-1** (floor) — On a sideways phone the card has no field, no list and no CONNECT GITHUB
- **E-9** (floor) — The search field's focus is a one-pixel colour change on a rule
- **H-1** (floor) — The ⋮ handle rests at 2.8:1 because opacity dims it
- **S1-1** (floor) — Message menu handle fades ink-faint with opacity-60
- **X-2** (floor) — Confirms S1-1: the message handle measures 84 grey, a fourth ink
- **D-9** (honesty) — Empty timeline shows nothing at all three anchors
- **E-4** (honesty) — A held row's ground is the same lift a hovered row gets
- **F-5** (honesty) — The picker's "nothing to choose" is said only to a screen reader
- **E-5** (one verb) — LOOKING… is drawn in the face the card's links wear
- **E-6** (one verb) — On a phone the waiting START is the widest control and the live verb
  is small
- **D-1** (vocabulary) — A person's words quoted in the machine's face
- **D-16** (vocabulary) — Remote selection: rounded corners seen in the picture
- **E-10** (vocabulary) — At rest the search field is a placeholder over a row rule
- **E-8** (vocabulary) — The card's lead is a fourth way to draw the blue edge
- **H-7** (vocabulary) — The reply mark is a curved text glyph from a face this build does
  not ship
- **S1-16** (vocabulary) — Borders on things that are neither a field nor a button
- **S1-21** (vocabulary) — A second gradient, and fades by mask
- **X-11** (vocabulary) — Fields: the search is an underline, the only field without a ring
- **X-6** (vocabulary) — The waiting face dims the word, though the border should carry it
- **B-11** (grid) — Four left edges in the launch card, and the buttons sit on none of them
- **D-2** (grid) — Leading marks sit on the text rail and push the words in
- **D-8** (grid) — A scrolled-off message's handle shows beside the pinned author line
- **E-7** (grid) — The × and ‹ sit on neither of the head's two lines
- **G-20** (grid) — In the chat, a queued command truncates before its terminal's name
- **B-10** (less) — The launch card is a fixed 60% box that is half empty
- **D-14** (less) — The phone spends a fifth of every line on gutters
- **D-4** (less) — On a phone the terminal's caps name eats the command
- **E-2** (less) — On a phone the repository name is cut while its description keeps 45%

### Chapters, the composer and its tray, the queue, settings, the agent's absence

- **F-2** (floor) — Queued text wraps and is cut by the next row at 390
- **H-2** (floor) — Icon-only controls stay 24px on a phone: ⋮, queue ↑ ↓ ×, ×
- **S1-2** (floor) — Collapsed composer row hidden by opacity, still focusable
- **S1-3** (floor) — Controls without a phone growth, left under 44px
- **B-12** (honesty) — At 390 the composer's Send is not on the screen
- **F-1** (honesty) — On a phone Send leaves the screen. It does not wait
- **F-10** (honesty) — Draft summary text is cut mid-letter with no ellipsis
- **H-3** (honesty) — The queue's ↑ ↓ × are not there at all on a phone turned sideways
- **S1-5** (honesty) — Hover-only reveal of queue reorder and delete
- **B-8** (one verb) — "NO AGENT" is a status in the colour of the button under it, drawn
  the same way
- **B-9** (vocabulary) — Opacity dims the absent agent's mark, and raw utilities write its
  row
- **D-5** (vocabulary) — Chapter and break are drawn rules. Type alone does not divide
- **F-6** (vocabulary) — A joined draft names its author with a faint label only
- **H-18** (vocabulary) — A person's presence is a coloured round dot with the name in a
  tooltip
- **S1-28** (vocabulary) — Width, not the phone question, switches the composer's layout
- **S1-30** (vocabulary) — Idle agent caret dimmed with opacity-50
- **S1-31** (vocabulary) — Reduced-motion thinking dot dims with opacity-60
- **D-6** (grid) — Session break label centred on no rail
- **F-8** (grid) — Composer and queue sit on a 16px rail, the timeline on 32
- **X-14** (grid) — The composer dock sits on its own 16px rail
- **C-16** (less) — CONNECT CLAUDE is drawn twice at two indents, and its blue edge hangs
  beside nothing
- **D-7** (less) — Break then chapter: two dividers and ~80px of nothing

### Terminals, the pane, the `all` page, ANSI, the document shell

- **H-21** (floor) — The phone's pane grab edge is a 6px wide target
- **S1-4** (floor) — ANSI faint renders as opacity-75
- **G-11** (honesty) — At 844x390 the player's play control sits half under the closed band
- **G-15** (honesty) — On a phone the strip shows one tab and a clipped word
- **S1-11** (honesty) — Splitter is invisible until hovered
- **S1-6** (honesty) — Hover-only reveal of terminal row verbs on desktop
- **G-12** (one verb) — A closed terminal's status line wears the command field's dress
- **C-10** (vocabulary) — The terminals pivot is a third face of the pivot
- **D-11** (vocabulary) — The agent's `$` is the people's green
- **G-10** (vocabulary) — The recording player brings a white fill, round corners and its
  own ground
- **G-8** (vocabulary) — The pane's bare verbs are spoken in four voices
- **H-10** (vocabulary) — `+` and `$` are text glyphs at raw sizes off the ramp
- **H-11** (vocabulary) — The replay's play button is a large filled white triangle, and the
  set's outline play mark has no caller
- **H-19** (vocabulary) — Every command's `$` is green, including the agent's and finished
  ones
- **S1-17** (vocabulary) — Outline used as a border: terminal caret and two focus rings
- **S1-32** (vocabulary) — Terminal key sink hidden with opacity-0
- **G-13** (grid) — The pane's head is not on the header band
- **G-4** (grid) — A block's author reads at body size beside 12px mono
- **G-5** (grid) — A command is cut in the middle of a word while a word break was free
- **G-7** (grid) — "Show in `<terminal>`" runs off the edge of the pane
- **X-16** (grid) — The pane's head has no band
- **X-18** (grid) — The two command bands do not meet across the divider
- **G-16** (less) — The phone's grab-edge mark is an ornament on a hidden duplicate
- **G-17** (less) — At 844x390 a refused pane leaves one block of content

### `View.fs`, `Icon.fs`, the brand mark and the stylesheet

- **E-13** (floor) — A button inside a row button
- **H-4** (floor) — A tally of ticks and crosses says nothing to a screen reader about which
  is which
- **H-5** (floor) — The collaborator caret flag is a rounded rectangle, white text on a
  person colour, below the contrast floor at some seats
- **H-6** (floor) — Opacity dims the idle agent's diamond in the sidebar to 2:1
- **C-15** (honesty) — A flow under way does not change the weight of the idle verbs beside
  it
- **C-18** (honesty) — Connecting is drawn exactly as connected
- **C-20** (honesty) — Settings' only labelled way out is under the fold in landscape
- **C-3** (honesty) — A phone has no way to retry a dropped connection
- **D-10** (honesty) — Cloning, with no card, is worn nowhere in the column
- **D-3** (honesty) — Queued, a wait, wears live green
- **E-11** (honesty) — The card emits `disabled` at six sites the Style side does not show
- **E-3** (honesty) — The branch pane never ticks the branch START will launch on
- **F-4** (honesty) — The head row offers Move up, which does nothing
- **G-1** (honesty) — Running dot pulses through 25% opacity and is caught dim at rest
- **X-4** (honesty) — More than one red word on a surface
- **C-6** (one verb) — A status phrased as the button's verb
- **C-13** (vocabulary) — The Claude and GitHub connect flows are two arrangements of the
  same parts
- **C-5** (vocabulary) — One stopped session, two names for it
- **D-15** (vocabulary) — Two arrows for the one fold
- **F-7** (vocabulary) — Draft summary edge is a person's colour, set inline
- **G-6** (vocabulary) — The run fold's sentence uses the terminal face
- **H-12** (vocabulary) — The gem is a lit, translucent, glowing object, and nothing says it
  is exempt
- **H-13** (vocabulary) — Icon strokes render in five weights, and the 12px marks are
  thinner than the caps beside them
- **H-14** (vocabulary) — `Icon.down` means four things, and the caret drawn for "opens
  beneath" has no caller
- **H-20** (vocabulary) — A command's outcome is drawn as ✓ 0 / × 1 beside TIMED OUT /
  FAILED / REFUSED
- **H-9** (vocabulary) — A fold's chevron is an SVG in the chat and a text `›` in the
  terminal and the
- **M0-2** (vocabulary) — The camera calls a sideways phone a desktop
- **S1-33** (vocabulary) — Box-shadow rim in a keyframe
- **S2-1** (vocabulary) — Layout literals in View.fs (flex, gap, layout)
- **S2-2** (vocabulary) — Typography literals in `View.fs`, including a size off the ramp
- **S2-3** (vocabulary) — Truncation and overflow literals in View.fs
- **S2-4** (vocabulary) — Margin and flex spacing literals in View.fs
- **S2-5** (vocabulary) — Whitespace and layout modifiers in View.fs
- **S2-6** (vocabulary) — Shrink and width literals in View.fs
- **S2-7** (vocabulary) — Custom property [overflow-wrap:anywhere] in View.fs
- **S2-8** (vocabulary) — Icon.fs w-3.5 h-3.5 block literals
- **S2-9** (vocabulary) — Icon.fs block literal
- **C-12** (grid) — "APPROVE ON GITHUB.COM" wraps to two lines in a 32px button
- **G-14** (grid) — The closed mark sits low beside the running mark
- **H-16** (grid) — Marks sit off the 4px rhythm: 14px icons, 14px pics, 6px dots, an 8px
  diamond
- **D-13** (less) — The thinking cube has shading, lit from one side
- **E-12** (less) — The "starting…" hold on START can never be seen
- **G-18** (less) — A file's own name is the faintest thing in its pane

### The Manager's own pages (`app/ManagerUi.fs`)

- **B-1** (floor) — Unrouted browser paths get a bare text/plain 404, not a page
- **B-4** (floor) — The way back is a text-height inline link with no focus ring and no
  touch target
- **A-2** (honesty) — Resolves M0-3: camera seeded wrong. The unnamed row is the designed state
- **A-24** (honesty) — On a phone the MCP row loses its address
- **A-25** (honesty) — REACHES draws a scope word and a session's name in the same face
- **A-3** (honesty) — Live is not green: running and stopped are the same faint word
- **A-4** (honesty) — Archive is a bare glyph with only a tooltip, beside a word verb
- **A-5** (honesty) — The sort control is gone on a phone
- **A-9** (honesty) — The lit last chip is a span wearing a pressable face
- **B-3** (honesty) — The refusal page wears nothing wrong. The opening screen's failure
  does
- **B-6** (honesty) — The starting screen's status does not change when the state does
- **H-8** (honesty) — The Manager's open mark vanishes under truncation on a phone, and its
  arrows are outside the shipped font subsets
- **M0-3** (honesty) — Rows show the id where a name goes while the ID column stays blank
- **S1-10** (honesty) — Sort header and record link show they are controls only on hover
- **A-6** (one verb) — Create and Declare are the same blue-bordered primary. Neither is
  filled
- **A-10** (vocabulary) — Raw `border-*` and row hover written outside Stroke / rowLift
- **A-11** (vocabulary) — The MCP/hook count is a hand-written copy of the label voice
- **A-12** (vocabulary) — Arrows are three different things: two text glyphs and one stroked
  icon
- **A-13** (vocabulary) — Code spells faint mono out six times instead of one Style value
- **A-14** (vocabulary) — The header band is a raw 88px literal
- **A-7** (vocabulary) — Withdraw is a bordered 128x32 button on a row. Stop and Archive are
  bare
- **B-5** (vocabulary) — The opening screen's failure sentence uses the caps label
  voice
- **S2-10** (vocabulary) — ManagerUi.fs layout and typography literals
- **X-12** (vocabulary) — Listed rows have three kinds of separator
- **X-13** (vocabulary) — The Manager page and the session shell are two designs
- **A-15** (grid) — The CREATED head sits 2pt below the NAME and ID heads
- **A-16** (grid) — Timestamp and verbs centre on a two-line cell: aligned to neither line
- **A-17** (grid) — The MCP form labels sit under no column of the table above them
- **A-18** (grid) — The lockup's mark starts 6px inside the rail
- **A-22** (grid) — Two tables on one rail share their edges and nothing between
- **A-23** (grid) — The MCP address sits 2pt above its row's baseline
- **B-2** (grid) — Standalone pages share no band, rail or footer mark with the Manager page
- **A-19** (less) — The empty MCP table draws three heads, a count and a sentence over
  nothing
- **A-20** (less) — The ID column is a head and 210px over empty cells in every pictured row
- **A-21** (less) — The 88pt band is sticky at 390pt tall
- **X-20** (less) — Confirms A-21: the Manager band is 88px on an upright phone too

- **Not from the audit: the browser tier tests the sideways phone in three cases, and should
  have one per shell surface.** `Browser.fs` holds three cases at 844x390 (`thumbSized`,
  `navOverPane` and the chat-width case). The stylesheet's `phone:`/`wide:` pair answers by
  height as well as width, so every shell surface has its own sideways-phone layout, and the
  tests pin only those three.

<!-- 203 findings. The audit's own ids stay so a fix can say what it closes. -->

## Agent

- **A bare `start_work_sandbox` beside a repo-declared name mints a decoy.** Sandbox names are
  scoped (`SandboxRef`): the repo's file declares `trinketworks/yession:dev`. An agent that calls
  `start_work_sandbox` with plain `dev` gets a NEW session-owned, srt-confined sandbox with an
  empty workspace and the name the agent had in mind. The model is right, because two scopes may
  share a short name. But agents fall for it repeatedly: in the container e2e runs, one agent
  spent a whole errand creating, inspecting and removing `dev` decoys while the real dev
  container sat running. The tool's answer says which sandbox came up. The likely fix is a
  refusal, or a nudge when a repo-scoped sibling exists. Nobody has built it.
- **An agent and a long-running build are a bad pairing without explicit parking.** A container
  build (`nix develop --command check`, tens of minutes cold) outlives an agent turn's patience.
  Measured: agents polled it into their own timeout and re-ran it, and twice called
  `stop_work_sandbox` on the container mid-build, which killed a 7.5GB-warm store to "reset". The
  working pattern is a detached launch (`nohup … &`, log to a file) plus an instruction to stop
  after starting it. Missing: a first-class long job that the session watches and reports on.
- **The live agent's tool results are text renderings** of the typed capability results, with no
  structured tool-result schema. The agent can read back its own terminal commands
  (`read_terminal`, plus the digest on the context pack, Plan 13 stages 3a/3b). No tool reads
  session history beyond the prompt transcript.
- **The context pack is a flat transcript** rebuilt per turn from the full projection, with no
  windowing, summarisation or token budgeting. Bodies are Markdown, but the transcript is a naive
  `author: body` join with no multi-line handling. Long sessions or large rich bodies will
  eventually overflow the model context. Only the terminal digest has a bound (an output tail,
  with the elided count stated).
- **Turn discipline is complete** (Phase 3). The queue drain enforces single-flight, interrupt is
  explicit, and property tests pin the invariants. But:
  - The queue has **no size cap**.
  - A drain coalesces any backlog into ONE turn, with no option for per-message turns.
  - The queued-message UI shows no "locked" state during the drain broadcast window. A peer can
    briefly type into an entry that is about to vanish. The drain discards the edit safely, but
    the UX flickers.
- **A turn has no step ceiling, and nothing automatic bounds a runaway one.** `maxTurns` is
  OPTIONAL on the Agent SDK's `query()`, and `Agent.fs` leaves it unset (no cap), as interactive
  Claude Code does. The old ceiling (32 model turns) ENDED the turn as a failure, which cut off the
  errands that most needed to finish. Only the interrupt replaces it. A turn spends the TURN
  HUMAN's credential in a session that nobody need be watching, and a bound that needs somebody
  present is no bound for an unattended session. Missing: a budget the model can see, a signal
  that a turn runs long, and a cost ceiling per turn or session.
- **A wake can start a turn nobody asked for, and wake→turn→command→wake is a legitimate loop.** A
  background command that finishes makes a turn due, and that turn may background another. Two
  things bound the loop: visibility (every woken turn records its reason on
  `AgentTurnStarted.Woke` and shows it in the chat) and the human interrupt, which is no bound for
  a session nobody watches. A per-session woken-turn budget is the next dial, deliberately not
  built in advance.
- **Watching a pull request is polling, deliberately**
  ([ADR](decisions/2026-08-27-pr-state-by-polling.md) gives the reasons and the cost of each
  cadence). A repo webhook needs admin on the repo, and inbound delivery needs a deployment
  github.com can reach, which the loopback default is not. Accepted:
  - **Polling latency: a short interval while a suite is in flight, a longer one otherwise**
    (`PendingIntervalMs`, `SettledIntervalMs` in `app/PrWatches.fs`).
    A declared hook endpoint with the App's webhook pointed at it (deployment.md §Webhooks) drops
    this to seconds. Otherwise, only an edit to those constants lowers it. The
    hosted dispatching service the ADR names as the exit does not exist.
  - **Check runs only.** Yession does not read the legacy commit-status API. A repo whose CI
    reports only statuses reads as having no checks, not as failing: the safe direction, and
    invisible.
  - **One page of check runs per commit** (the `per_page` in `app/GitHubPrs.fs`). No pagination
    exists. The rollup of a commit with
    more comes from a prefix, biased toward pending.
  - **Only rollup FLIPS make an announcement.** A push that resets checks to pending and ends
    green again says nothing. Entering pending is the ordinary rhythm of work, and a
    notification per push is one nobody reads.
  - **A rate-limited watch waits out GitHub's own reset window** and says so in the
    `pull_requests` query's status column. It does not fail or retry sooner.
  - **A transition wakes a turn**, a third source for the wake→turn loop above. The log-anchored
    baseline gives one turn per transition. It does not bound how many transitions a flapping
    suite produces, and the woken-turn budget is still unbuilt.
- **Repo integration is the read-only bootstrap slice** (Plan 14): typed clone-and-orient verbs
  beside the agent, one repos dir shared into the WorkSandbox, and GitHub sign-in per user over
  the device flow. Remaining, deliberate:
  - **Every session member can read a session's repos, and bytes outlive revocation.** One user's
    private repo, once added, is readable by every peer and everything in the WorkSandbox, the
    same shared-trust boundary as "terminal access equals session access". The `RepoAdded` event
    names who brought it in. GitHub-side revocation does not claw back what is on disk.
  - **A pasted PAT bypasses the App-installation scope rule.** The device-flow token is a GitHub
    App user-to-server token, so it reaches only repos that have the App. A pasted
    `github_pat_`/`ghp_` answers to no such bound.
  - **A GitHub token rotates only if the App expires it** (Plan 21). The Manager stores a
    device-flow grant as a grant and refreshes it on use. But an App registered with user-token
    expiration disabled still yields a permanent token. Revocation is at GitHub either way.
    Nothing tells an operator which of the two they registered.
  - **`git push` in the `default` WorkSandbox terminal has no forwarded credential.** Forwarding
    is `start_work_sandbox`'s `forward` argument (`app/WorkSandboxes.fs`), so a sandbox the agent
    asked for can carry `github`. But `default` is the one nobody asks for, and it starts with
    `Forwarded = []`. Its terminals do local git only until somebody starts a named sandbox. In a
    sandbox that forwards `github`, `__y_env` exports each BLOCK's commit author: the GitHub
    account behind the credential its act runs on (`GitHubConnection.commitIdentity`). A sandbox
    that forwards nothing has no author, and `Co-Authored-By` for the agent is absent everywhere.
  - **A forwarded `github` credential reaches every backend by the host address its sandbox can
    use.** The header of `app/GitGateway.fs` describes the route and why it binds every
    interface, and `Sandboxes.hostAddressFrom` gives the address per backend and platform. The
    macOS answer for srt is the box's own hostname. Where the box does not resolve its own name,
    a confined git gets a proxy error that names the host. A `Provision` carries the host it
    needs as a `Domains` entry, and the composition widens a filtering backend's allowlist by it.
  - **The git verbs run in a sandbox of their own, `srt` by default**
    (`YESSION_SESSION_GIT_BACKEND`). They used to take the agent backend, which stays `host`
    until a macOS run under srt is measured (the agent CLI entry above), so in every deployment
    that kept the default they ran unconfined. **Under `YESSION_SESSION_GIT_BACKEND=host` the
    git verbs run unconfined**, the operator's explicitly lax choice. The per-invocation
    hardening (`hardenedEnv` in `app/Repos.fs`) still
    applies. The filesystem and egress boundaries do not. A proxy that a `.git/config` names is
    off there (`NO_PROXY=*`). No git process holds a credential (clone and fetch go through
    the git gateway on a loan), so what a planted proxy or `insteadOf` can still reach is the
    loan, which the gateway answers only for the git sandbox's route while the verb runs.
  - **Every srt sandbox may write a checkout's `.git/config`** (`AllowGitConfig` in
    `app/Sandboxes.fs` says why, and that scoping it to the git sandbox is not done yet). So the
    WorkSandbox and the agent can plant config that git reads when it next runs in that
    checkout. The verbs are NOT immune to it. The per-invocation `GIT_CONFIG_*` hardening wins
    over repo config only for the keys it sets: hooks, fsmonitor, the ext transport, credential
    helpers and symlinks. Flags and env turn off what they can: diff drivers and textconv
    (`--no-ext-diff --no-textconv` on `repo_diff`), and under `host` a configured proxy.
    `.git/hooks` stays denied. A human who runs git in the checkout on their own host is not
    covered by any of it.
    - **One exec vector is left: `filter.<driver>.smudge` runs on `switch_branch`.** No flag
      and no env variable switches a filter off. Under srt it runs with the git sandbox's reach,
      which the sandbox that wrote the config already had. Under `host` it runs as the operator,
      unconfined. `YESSION_SESSION_GIT_BACKEND=host` is choosing that: every work sandbox can
      then run a program as the operator on the next `switch_branch`.
    - **`core.worktree` in a `.git/config` no longer moves a verb's work tree.** It could move
      it to any directory the git sandbox can reach: `repo_status` listed that directory's
      files, and `switch_branch` wrote into it, which under `host` is the operator's whole
      filesystem. Every verb in a checkout now runs with `GIT_DIR`, `GIT_WORK_TREE` and
      `GIT_COMMON_DIR` pinned to that checkout (`checkoutPins` in `app/Repos.fs`), and env
      outranks config. The pin closes a `.git/commondir` file the same way.
  - **The session's git checks a committed symlink out as a plain file** (`core.symlinks=false`
    in `hardenedEnv`). The file holds the link text. A link in the checkout would be a path the
    repository chose, and the session and every sandbox read the checkout through calls that
    follow links. The cost: a repository that commits symlinks has plain files in its checkout,
    and a build that needs the links does not work there.
  - **A checkout whose `.git` is not a directory is not in the session.** A work sandbox can
    write the repos directory, so it can put a symlink named `.git` (or a symlinked checkout or
    owner directory) in place of the real one, pointed at another repository on the host.
    `present` in `app/Repos.fs` asks `lstat` about all three, and every verb refuses such a
    checkout. Under srt the git sandbox could not follow such a link out of the repos directory
    anyway. Under `host` it could.
  - **The clone verb runs with no filesystem confinement.** The comment on
    `FilesystemConfinement` in `src/Yession.Domain/Sandbox.fs` says why, lists the names, and
    says when to undo it. For that one command, git can read and write whatever the session's
    user can, including credential files srt would otherwise mask, and the read scope does not
    reach it either. Egress stays pinned to github.com, the env stays hardened, and every other
    verb keeps the confined policy, since none of them writes a path srt objects to. The
    exemption also goes away if srt reads `allowGitConfig` per spawn.
  - **A docker terminal does not open on the checkouts.** Under the host-family backends, the
    repos directory sits inside a terminal's start workspace (`Sandboxes.SessionLayout`), so the
    agent's first `ls` shows what it cloned. Under docker, the workspace is the image's working
    directory (this session composes no `WorkingDirectory`), and the repos dir is a bind mount
    beside it, at the path the host keeps it at (`Sandboxes.checkoutAt`). Verbs answer with that
    path. The cost is one `cd`.
  - **Yession reads a repo's `yession.yaml`, and here is what it still cannot say** (Plan 27).
    The file declares sandboxes and nothing else. A sandbox is the only scope where "two repos
    both said something" has a total answer: the keys are (repo, name) pairs and the repos are
    disjoint, so the union is disjoint by construction. **That is the rule for every future key:
    a key belongs in `yession.yaml` only if its scope is a sandbox.** Left out, deliberately:
    - **The file is read from `HEAD`, never from the working tree.** Whoever can push to the
      repo authors the file (technical-design §4), so an edit that nobody has committed
      declares nothing. Commit it, and the next fold (any verb that changes a checkout, or
      a boot) reads it. `AGENTS.md` and `.agents/skills/*/SKILL.md` are read the same way,
      when the clone lands. A symlink at `HEAD` is no file for any of the three (rule 20).
    - **No inter-sandbox networking.** A `db:` sandbox's process is reachable from inside that
      sandbox only. A compose-like shared network is a design of its own.
    - **A `command`, an `entrypoint`, the limits (`mem_limit`, `oom_score_adj`,
      `pids_limit`, `cgroup_parent`) and a `dialect` are docker-only.** They live in or beside
      the `container:` block, so under `srt` or `host` there is nowhere to write one. The
      exception is `dialect`, which a sandbox with no container refuses because it reads as if
      it would apply.
    - **Only a block that died from SIGKILL can say "killed: out of memory".** The Docker
      backend reads its container's `memory.events` around every process it starts
      (`MemoryWatch` in `src/Yession.Domain/Sandbox.fs`). When `oom_kill` went up and the
      process exited 137, it says so, and whether
      the container hit its own limit (`oom` went up too) or the machine ran out (`oom` did
      not). What it cannot say:
      - A block whose CHILD died and which then exited with its own code reports that code (a
        build whose compiler died exits 1). Another process's kill in the same container looks
        the same, so the block does not guess.
      - Two blocks that run at once in one container share the counters. A block that another
        cause killed with 137, while a sibling was OOM-killed, reads as a memory kill.
      - The gate's commands, `setup:` and the session's own execs do not read the counters.
      - One overrun kills two processes: the one that overran, and a moment later one more.
        Work runs at the declared `oom_score_adj` and everything else in the container at
        0, so the second is always work: a sibling block, or the overrun's own shell. The
        container and the read of the counters are not chosen while work is running. A
        block that the second kill hit reads as killed for memory, which it was.
      - The container's main process runs at 0, a declared `command` included. Work can
        write its own score back down to 0, never below.
      - Work sets its score through `sh -c`, so a work exec needs `sh` in the image. Before,
        it ran the entrypoint and the work directly.
      - The Docker tier pins case (a) with a 64m container. Nothing tests case (b) for real: it
        needs the whole VM to run out of memory.
    - **The operator's ceiling bounds one container, not the sum.** A repo's `mem_limit` and
      `pids_limit` are at most the operator's (`container:` in the profile, invariant 26). Six
      sessions that each take the whole ceiling all pass. The pool cgroup bounds the sum.
    - **A `cgroup_parent` is the operator's to name, and the host's to make.** Nothing in yession
      creates the parent cgroup or limits it
      ([technical-design.md §4](technical-design.md#4-repo-local-configuration) gives the
      spelling per driver and what happens when the parent is missing). Yession checks a
      `mem_limit:` against the operator's ceiling and the daemon's total, not the parent's
      limit.
    - **No `${VAR}` in an entrypoint.** compose interpolates from the host's environment at parse
      time, the wrong environment here. Nothing needs it yet. The string form splits into words
      and nothing else. A value that needs expansion belongs in an actual `sh -c`.
    - **No per-session ceiling.** The tier-3 operator env is host-wide, so a human at the command
      gate bounds a widening ask, not a policy for this session.
    - **No file watcher.** A `git pull` that changes the file takes effect at the next fold
      trigger: boot, or `add_repo` / `switch_branch` / `remove_repo`.
    - **No session-wide keys, and there will not be.** Gates, approvals and MCP servers are
      session-wide, so two repos that disagree have no honest tie-break. Those stay the
      operator's. A `repos:` key (this repo needs that one) is recursive, and its fixed point is
      a design of its own.
    - **A repo's file has the trust of its push access.** Only the gate and the schema's own
      refusals mitigate this: the reserved `YESSION_` prefix, a host-path volume, and a workdir
      outside the checkout.
- **The session's imperative API has two halves, and only one exists** (Plan 15). Commands
  mutate and belong to the agent alone. Queries read and have one declaration each. They reach
  the agent as generated MCP tools (`readOnlyHint`) and the humans as a generated settings
  surface fed by one multiplexed SSE stream. Stage 1 shipped and retired the Repos panel's
  add/remove/switch controls and the `/repos*` routes. Remaining, deliberate:
  - **Every session member reads every query.** No per-query authorization exists, as on the
    timeline, where every member reads every attributed act-line. A query that should not be
    session-wide has nowhere to hide yet.
  - **The shipped classifier approves everything.** Every terminal block and structured command
    passes the classifier (`Classify.fs`, Plan 23), whose only implementation is
    `Classifier.approveAll`. Until an AI-driven classifier lands, only the work sandbox's
    confinement stands between an agent turn and any command. Manual approval went deliberately:
    the queue stays visible and editable, refusals stay recorded and attributed, and the seam is
    where the real classifier plugs in.
  - **A forwarded credential is a route, and the route's cap lives in the sandbox's env for that
    sandbox's lifetime** (Plan 15 stage 2, reshaped by the git gateway). The header of
    `app/GitGateway.fs` describes the route, the per-sandbox cap, the per-block loan (`__y_env`)
    and why a block can read another's live loan from `/proc`. The VALUE never enters the
    sandbox. A refresh under Plan 21 reaches a running sandbox, a revocation at the provider
    shows on the next push, and `stop_work_sandbox` revokes the route. A push writes
    `GitCredentialSpent` to the log, where the person whose credential it was finds out.

    The transcript is NOT held. `env`, `printenv` or a program that dumps its environment on
    exit prints the loan into a durable record that replays to every peer, present and future.
    The printed loan dies at the terminal's next block and works only inside that sandbox, but
    the person it names stays in the transcript for good.

    A lease lends nothing, and a take types nothing into the shell. A take returns every loan on
    the terminal except the holder's own running block's, so what a holder types spends their own
    credential or none, and a push otherwise gets a refusal in words. A person's acts with a
    credential are blocks, sent from the command line, like everyone's. Only `github` is
    forwardable so far. The gateway admits only git's three smart-HTTP requests to a repository
    path, so a cap is not a token for the rest of github.com.
  - **The GitHub API reaches a sandbox only as far as somebody bound it.** A connection
    forwarded by `api` routes `api.github.com` and `uploads.github.com` to the credential proxy,
    and nothing else, and never under srt ([technical-design.md
    §4](technical-design.md#4-repo-local-configuration) has the `${proxy.…}` and
    `${github.token}` references). Nothing sets the trust bundle or the stand-in on a sandbox's
    behalf: only an operator's resource `env` or a repo's declaration binds them. A sandbox that
    binds neither reaches the API through the proxy unauthenticated. The stand-in sits in the
    block's environment like the git loan, with the same transcript caveat. Unlike the gateway,
    the proxy admits every request to a declared host, so a live stand-in is the lender's whole
    API token.
  - **The proxy tunnels an admitted container to any host on the internet, and to nothing
    else.** A `CONNECT` to a host no route declares is tunnelled, because a tool that honours
    `HTTPS_PROXY` sends every host this way (this repository's own `yession.yaml` build fetches
    from cache.nixos.org, yession.cachix.org, npm, NuGet and GitHub's tarball hosts through
    it). The name is resolved once and every answer judged. Loopback, link-local, RFC 1918,
    CGNAT (the tailnet), ULA, multicast and this host's own addresses are refused, and the
    tunnel dials the judged address, never the name again (`refusedTunnel` and `Egress` in
    `app/CredentialProxy.fs`). Before this, a container holding the capability reached the
    Manager's loopback port through it.
    - **The design direction is operator-declared egress.** The operator declares the hosts a
      sandbox may reach, and the proxy tunnels those and nothing else. It was not chosen yet
      because the hosts a build reaches are many and move (Nix substituters and flake inputs,
      registries and their CDNs), and the list would be this repository's to keep for every
      operator. The refusal above stays under it as the floor.
    - **It is a policy on the proxy, not on the container.** A container's own direct egress
      is the Docker network's, and reaches what that network reaches.
    - **Deferred: the session network becomes an internal network.** The session's Docker
      network becomes `Internal`. Its only way out is the credential proxy. The proxy tunnels
      the domains that the sandbox's `endpoint` resources name. This waits for an endpoint
      list that the operator keeps. It also waits for a measurement: does
      `host.docker.internal` reach the gateway from an internal network on Colima and on
      Docker Desktop?
    - **Deferred: the image build runs on the session network.** The build gets the proxy as
      build arguments. This waits for the internal network above. It also waits for the
      classic builder, because BuildKit does not honour a custom network.
  - **The AgentSandbox has no grants.** The agent CLI runs with no built-in tools. Every command
    it issues runs in a work sandbox, under that sandbox's grants. Its own process holds no
    connection and no proxy, and nothing in the profile can give it one. If the CLI ever runs
    tools of its own, it needs a selection of its own first.
  - **A grant is the operator's file as it reads today, and nobody consents to it.** A sandbox
    gets the closure of its selection under the current profile (`ResourceProfile.grants`).
    Nobody shows it to a person who joins a session. A change to a profile or a repo's file
    widens what every later sandbox holds, and asks nobody. Two remedies build on the closure,
    not on a second account of it: show it at join or at connect, or record it per person so a
    later configuration cannot widen a grant silently. Only a set holding a connection asks,
    once per repository, because a connection is spent as the session's user (invariant 25).
    Every other kind, a volume and a socket included, is the operator's decision, not the
    session user's, so the profile is where it is decided.
  - **An external MCP server's read-only tools are not queries yet.** A server declares
    `readOnlyHint`, Yession does not infer it, so that a third-party server's queries could join
    the registry without a yession-specific convention. But only in-process registrations reach
    the registry. A foreign tool's answer has an arbitrary JSON Schema, and the generated
    surface draws rows, fields and a value. The missing piece is a JSON-Schema-subset renderer.
  - **A person cannot mint a stream without the agent.** Someone who wants a device terminal
    before the agent opens one has to ask the agent. Yession cannot offer a button: the tool that
    mints a stream offer has a name and argument schema learned at runtime. The general answer is
    a human surface for a declared tool: a form generated from its JSON Schema, plus an
    authorization story for a person who calls a foreign tool directly.
- **Per-user agent credentials landed** (Plan 08). A human signs into their Claude account from
  the session's Connections panel: "this session only" (`SessionScope`) or "all my sessions"
  (their user scope, or the local scope under `--auth localhost`). The Manager brokers the OAuth
  exchange as pure standards and never learns the provider. Each agent turn runs on the TURN
  ACTOR's credential (session-scoped ▸ actor's own ▸ ambient env), resolved fresh per turn with
  Manager-side lazy refresh. Remaining, deliberate:
  - The ambient `ANTHROPIC_API_KEY`/`CLAUDE_CODE_OAUTH_TOKEN` process env stays as the documented
    last resort. It feeds CI's LiveAgent tier, and it applies to ANY actor.
  - A refresh failure shows only as the turn's failure. No panel-level health indicator exists.
  - The browser polls the panel's status. The SESSION learns of changes live over its control
    stream, but the open browser tab re-asks.
- **Live-path verification needs a credential, by design.** The `LiveAgent` capability makes
  `verify` fail without `ANTHROPIC_API_KEY`/`CLAUDE_CODE_OAUTH_TOKEN`, not skip (see
  `requireCapabilities` in [tasks.fsx](../tasks.fsx)). A cheap-tier run never asks.
  `YESSION_BIN_CLAUDE` matters in sandboxes that kill the SDK's vendored binary.

  That gate does NOT cover the credential going missing after it passes. The parent probes it
  once, and every suite in the one process reads it from the process env, so a suite that
  mutates that env changes what later suites test. `Phase2`'s credential-leak regression once
  DELETED the key on its way out, and the live clone case after it got **no agent at all**: no
  reply, no error, no reason. `Support.withEnv` (take and give back, absence included) closes
  that cause, and three cheap-tier cases pin it. The CLASS stays open: nothing re-checks a
  declared capability where a suite uses it.

## Delivery & operations

- **Node is a required runtime.** Yession ships as one npm package with two bins
  (`yession-manager`, `yession-session`). `npm ci --omit=dev` in the extracted tarball
  (docs/deployment.md, "Installing a release") pulls the platform-native deps through npm's
  optional dependencies (`node-datachannel`'s addon AND the SDK's native `claude`),
  so after install the agent works offline. No self-contained binary exists anymore: a machine
  without Node ≥24 cannot run Yession from npm. (The Nix installable wraps `nodejs_24`, at the
  cost of needing Nix.)
- **First install downloads the native `claude`** (~240 MB, platform-specific). It is not in the
  300 KB tarball, so the *first* install needs network and disk. The SDK finds the binary
  afterward, so `YESSION_BIN_CLAUDE` is not needed.
- **Build slots count only this repository's own heavy verbs**
  ([AGENTS.md](../AGENTS.md#bootstrap), "Heavy verbs take a build slot"). The ~7 GiB pool is in
  an 8 GiB Colima VM. A `dotnet` or `node` run by hand, another repository's build, or a `dev`
  watch takes no slot, and can still push the pool into the kernel's OOM killer, which picks the
  largest process in ANY session. The slot is admission, not a limit. A per-container memory
  limit (#1312) bounds one container. The pool cgroup (`cgroup_parent: yession`) keeps the
  daemon out of reach, where the host makes it
  ([technical-design.md §4](technical-design.md#4-repo-local-configuration)).
- **The composition E2E and install smoke run on Linux/CI.** Other platforms' npm resolution
  rides npm's own optional-dependency machinery, with no per-commit check.
- **No per-platform build matrix for the npm route, no code signing.** Release CI ships one
  platform-neutral npm tarball and a Nix package, both from `ubuntu-latest`. npm's
  `optionalDependencies` resolve the platform-native pieces on the machine that runs `npm
  install`. (This replaced the earlier SEA per-platform binaries — see Step 26→28.) So Yession
  has no compiled binary of its own to sign or notarise. The native `claude` and the
  `node-datachannel` addon are third-party downloads with no signature, and may trip macOS
  Gatekeeper. The PR gate builds the flake package on `macos-latest`, enters the dev shell, and
  loads the native WebRTC addon there. But only the Linux install-smoke exercises the npm
  INSTALL path on darwin, and everything on Windows.
- **Telemetry is agent-turn usage plus Manager audit records** (Plans 04 + 06). Each completed
  turn emits one OpenTelemetry **log record**: token and cache counts plus session, turn and
  model ids, never message content. Every process is a **direct OTel emitter**, with no
  Manager-side collector (the header of `app/Telemetry.fs` gives the `OTEL_*` variables and how
  they reach each child). The Manager also emits `yession.*` **audit records** for the
  secrets/ABAC surface (`Audit` in `app/SecretStore.fs` and Plan 06 § Telemetry list them), each
  one greppable stdout line. Missing:
  - a **metrics pipeline** and **traces** (the emitter path generalises to both, with the same
    env selection and no collector to touch)
  - **forwarding of audit records to a collector**
  - **structured app logging or crash reporting** beyond stdout
- **Multi-node operation and work-intake integrations (Slack/Linear)** stay out of scope, as
  planned. Terminals landed as Plan 13 and remote access as Plans 09/10/12. Still out of scope:
  a session on a machine other than its Manager's. The deployment shapes are loopback and
  fronted ([ADR](decisions/2026-08-28-deployment-fronts-everything-or-nothing.md)). The
  remaining centralisation is the connection broker's single refresher: a provider that rotates
  refresh tokens on use makes two nodes that share one grant a race, which the second node loses
  (the reason for the in-flight map in `app/Broker.fs`). The sketched exit is per-node grants,
  each node with its own sign-in and nothing replicated. It is a follow-up, not built.

## Testing debt

- Browser E2E runs on Chromium only, and drives one host platform per CI run.
- The Yjs relay trusts ordered delivery per data channel. The Phase 3 property schedules cover
  arbitrary *delivery timing* (staleness, partitions, restarts), but not corrupted or duplicated
  *frames* on the wire.
- The vendored Hedgehog does no shrinking. A failing property prints the whole schedule, not a
  minimal one.
- No measurement covers load and scale (many peers, large logs, long drafts).
- **The serial engine meets a tty in tests, never a chip** (Plan 16, part E). `check Serial`
  drives `Ports.real` over a socat PTY pair, so the open, the line settings, the read and write
  paths, and the vanish path all cross a real kernel tty. No CI box can cover a specific adapter:
  a baud rate the driver silently rounds, a chip that needs DTR/RTS toggled to leave reset, a USB
  stack that reuses `/dev/ttyUSB0` for another device after a replug. Those faults surface on
  hardware or not at all.

## Terminals

- **A queued command whose terminal closes stays queued for ever, and nobody can reach it.**
  Nothing runs or removes it, deliberately, so nobody's text drops silently. But a closed
  terminal renders its recording instead of its composer (stage 3e), so the entry has no surface
  at all.
- **Terminal access equals session access.** A terminal can read the sandbox's environment
  (`env`), which after resolve-at-spawn includes secrets the session's spec references. Any peer
  could already ask the agent to run `env`, so this is no new privilege, but a terminal makes it
  one keystroke. A future per-user terminal gate would attach here.
- **A screen that showed a secret is in the recording, permanently, one tap from the chat.**
  Keystrokes stay out of the recording (`Session/Terminals.fs`, `Input`), because live mode makes
  typing a password ordinary. Output goes in. The replay work removed the age limit on closed
  transcripts and put a tappable chip on every block and lease stretch. So every peer is one tap
  from a secret, for as long as the session's data exists. Terminal access already equals
  session access, but anything that widens who may read a session (a link scope, a public view,
  an export) widens this with it.
- **`renewable` is a provider's claim about its own tool, and nothing verifies it** (Plan 19,
  step 4). A closed stream offers a way back when the provider said asking again is safe.
  Pressing it replays that tool call with its original arguments, so a provider that marks a
  destructive tool renewable makes the button destructive. The default is false, and the docs
  call the field a promise. `SourceCapabilities` has the same standing: only the other end knows.
- **A tool-use chip does not point at the terminal its call opened** (Plan 19).
  `ToolUseFinished` carries `Block` for the block case. The stream case has no equivalent, so
  the audit says a call happened, the timeline says a terminal appeared, and nothing joins them.
  A `Terminal : TerminalId option` beside `Block` is the obvious symmetry, deliberately kept out
  of the step that would have needed it.
- **The jumpstarter console has an echo floor of one quiet period** (~50ms measured at 61-67ms
  round trip, set by `QUIET_SECONDS` in `examples/jumpstarter`). Its stream is a drain loop over
  a `pexpect` handle, not ownership of the fd, so a typist sees their echo that much late. The
  serial provider owns its fd and has no floor. The floor is a coalescing window we chose: the
  drain matches a real pattern, returns on the first byte, and waits only long enough not to
  split a line. (An earlier `console.expect([pexpect.TIMEOUT])` waited its whole timeout, never
  cleared the buffer, and re-sent the console's history five times a second.)
- **An agent holds a lease where its own block took the screen, and nowhere else it could
  have run a block instead.** What remains is a boundary, not a shortfall:
  - Plan 19 step 3: a live-only source has no blocks, so `execute_command` has nothing to do
    there. `write_terminal` takes the lease exactly as a peer does: visible in the holder field,
    stealable back mid-sentence, every byte in the transcript.
  - Plan 20 stage 6a fixed a bug: the alt-screen flip refused to give an agent-authored block its
    terminal, so a full-screen program waited for a keystroke nobody could send, and every
    command behind it stalled. The flip now follows the AUTHOR, agent included.

  The agent cannot TAKE an instrumented terminal. It may only use the one detection hands it,
  over a block already classified and on the record. Taking it would be a door around the
  classifier, and `execute_command` is the one door (Plan 23). The drain gate of live mode
  (Plan 13, stage 2e) stays: a leased terminal holds its queue instead of typing into a
  session someone else owns.
- **A rejected command is silent to whoever issued it.** `SessionCommandResult` carries
  `CommandRejected reason`, and no client reads it. The browser has no notice surface, so
  `grep CommandRejected src/Yession.App` finds nothing. For every refusal a peer command can
  produce, the button does nothing, and the reason exists only in the Process's answer. The
  capability-approval prompt (Plan 27) showed it: its refusal ("what this repo asks for changed
  since you looked") is the sentence somebody needs and cannot see. A rejection needs somewhere
  to show, and no such place exists yet.
- **Only the agent hears that a block waits on a keystroke.** `execute_command` answers
  `TerminalCommandInteractive` when detection hands the terminal over, and the agent has
  `write_terminal`/`read_terminal` to resolve it. If it does not, the chat shows only a pulsing
  chip, and a person must find the terminal panel's lease bar. A handoff card in the chat has a
  design but no build.
- **The live viewport works host-free, never against a real pty end to end** (Plan 14, stage 6).
  The panel renders a live screen, the holder's copy takes keystrokes, and the client composes it
  with the same emulator the Session uses. No suite drives the whole loop, as stage 6's note
  says. The keystroke translation runs host-free under `Browser`, because only a real browser
  answers for a `KeyboardEvent`. The pty suite pins the Process half. Neither covers two peers on
  one real pty, with one of them typing.
