# Agents

## Communication

Respond terse like smart caveman. All technical substance stay. Only fluff die. DEFAULT: on from
the first reply. Nobody has to switch it on.

Rules:
- Drop: articles (a/an/the), filler (just/really/basically), pleasantries, hedging
- Fragments OK. Short synonyms. Technical terms exact. Code unchanged.
- Pattern: [thing] [action] [reason]. [next step].
- Not: "Sure! I'd be happy to help you with that."
- Yes: "Bug in auth middleware. Fix:"

Switch level (default full): /caveman lite|full|ultra|wenyan
Stop: "stop caveman" or "normal mode"

Auto-Clarity: drop caveman for security warnings, irreversible actions, user confused. Resume after.

Boundaries: code/commits/PRs written normal.

## Contributing changes

Read `.agents/skills/contributing-changes/SKILL.md` **before the first edit** of any request to
build, fix, refactor or change something. It decides how the work is split, and you cannot decide
that afterwards. The skill owns the process: stand-alone increments, one PR each, auto-merge, the
merge queue, and driving master green. A deviation from what was asked stops the loop. Report it
to the user.

**Plans are not artifacts.** Say the shape of the work to the user before you start. Write no plan
document in this repository. `docs/plans/` was removed and stays removed. A plan is right on the day
it is written and unfalsifiable after, and it drifts silently while it reads as current. Put what
is worth keeping next to the code it governs, where a test can contradict it.

## Bootstrap

**devenv.nix** declares the dev environment (Node 24 + .NET SDK 10), the tasks (devenv `scripts`)
and the build outputs (devenv `outputs`). `devenv shell` puts `node`, `dotnet` and the task scripts
on PATH.

**Inside a yession work sandbox** (`YESSION_SANDBOX` is set): the entrypoint in `yession.yaml` has
already assembled the devshell. `check`, `build` and the other tasks are on PATH as written. Put
nothing in front of them. Do NOT install devenv, because the devshell exists. Do NOT run
`.claude/setup.sh`, because it targets Claude Code containers.

**In a fresh Claude Code container**, run `bash .claude/setup.sh` once. It is idempotent, and its
header lists the steps. The sandbox proxy blocks the `github:cachix/devenv` fetch, so the script
writes the gitignored `devenv.local.yaml`. That file repoints the input at devenv's own source,
substituted from `cache.nixos.org`. The SessionStart hook (`.claude/settings.json`) re-runs the
script with `--hook`. A laptop or CI uses the committed `devenv.yaml`.

Run the task scripts: `devenv shell -- <task>`, or bare inside the shell. The main ones are
`check`, `build` and `verify`. They run `restore` for you. Do NOT invoke `dotnet`, `fable` or
`esbuild` directly to "run the suite". The scripts match tool versions and PATH to CI.

A Claude Code container has Chromium (`$PLAYWRIGHT_BROWSERS_PATH`) and the `node-datachannel`
addon preinstalled. The `Browser` and `Native` capabilities work (see Testing).

## Build interface

`tasks.fsx` is the complete, standalone build interface. Every build function is a verb there, and
the dispatch at its end lists them. The devenv scripts, the GitHub Actions workflows and the Nix
`outputs` are thin wrappers. `dotnet fsi tasks.fsx <verb>` drives everything without them.

A verb names the `Target`s it needs, and `make` builds each once, after what it stands on. To add a
build step, add a `Target` and its `needs` (the `Target` doc comment has the detail). Do not add a
call in a verb's body. The next verb cannot know an order written only there.

`nix/worktree.nix` builds `nix/packages.nix` from the working tree, and its header says why.
`check Nix NixBuild` drives it.

**No new helper scripts.** New build, dev or repo functionality is a `tasks.fsx` verb. Only glue
that must run where `dotnet` cannot stays outside. The one such script is `.claude/setup.sh`, which
runs before Nix/devenv exist.

**No belt-and-braces.** Two mechanisms may meet the same requirement, such as two config locations
or a fallback beside a primary. Keep ONLY the one verified working here. Remove the other. A spare
hides which path is live, rots unverified, and turns the next failure into an archaeology dig.

## Colocation

**A rule lives with the state it governs.** An invariant that holds only because a CALLER
remembered to ask first is not an invariant. The next caller has not read it.

`technical-design.md` §1 says composition happens at the top. This is the other half: what composes
at the top must have nothing left to DECIDE. A composition root that computes (a bound, a fallback,
a refusal, a subtraction) moves a decision from where a cheap test reaches it to where no test does.

The tell is a member exported for no local reason. `SessionTerminals` once exported an
`Instrumented` predicate that nothing inside it used. `Host.fs` called it to subtract a line window
and cap the answer. No cheap test saw that arithmetic, and a bug lived in it.

Ask three questions. Any `no` means the rule is in the wrong place:

- **Can a caller break it by not calling something first?** Then those calls are one verb.
- **Would testing it mean building the composition root?** Then move it down to where the state
  is. A rule the cheap tier cannot reach is a rule nobody re-checks.
- **Does a sibling operation on the same state live somewhere else?** Then one of them has moved.

A seam belongs where it is USED. Pass state downward. Do not reach upward for it.

## Fixing bugs

**Fix the root cause upstream. Harden against the symptom downstream. Do both, not either.** The
upstream fix stops THIS fault. The downstream guard stops the next, different fault from becoming a
plausible wrong answer at the same place.

This is not belt-and-braces, which is two mechanisms for ONE requirement at one point. This is one
mechanism each at two points. They go red at different times: upstream while the diagnosis still
exists, downstream before the damage is published.

Example, the version regression. `echo "version=$(...)"` discarded the exit status of a failed
`devenv shell -- version`. The step went green with an empty version, and the release job tagged the
commit `v`. That tag matched `--match 'v*'` and restarted the 6.13.1 line at 1.0.0.

- Upstream: capture under `pipefail` and refuse an empty result.
- Downstream: `--match 'v[0-9]*'`, and refuse a nearest tag of the wrong shape.

The tell that you did half: the fault reads as a chain, and your change touches one link. An example
chain: X failed, so Y saw nothing, so Z published garbage. Ask what the other links do given a
different X.

## Examples

`examples/` holds standalone integrations, built the way somebody OUTSIDE this repository would
build them. Read `examples/AGENTS.md` before you change one.

## UI baseline

Read `docs/visual-design.md` BEFORE you change a surface. WCAG 2.0 AA is the floor for every
surface, not a follow-up. `src/Yession.App/AGENTS.md` lists what the tests do not pin.

## Versioning

The version is computed from the commit history (policy at the top of `tasks.fsx`) and never
stored in a file. Every green master push publishes `1.0.0-beta.<n>`. To move the triple, put a
marker in the commit message. For a squash-merged PR, that is its title or description:

```
+semver: major   (or breaking, or a BREAKING CHANGE: footer)  -> 2.0.0-beta.0
+semver: minor   (or feature)                                 -> 1.1.0-beta.0
+semver: fix     (or patch)                                   -> 1.0.1-beta.0
```

A marker counts ANYWHERE in the message (subject, body or footer). It must be a line of its own,
with nothing else on it. Prose that mentions one mid-sentence never moves the version. Neither do
the examples above: the trailing `-> 2.0.0-beta.0` keeps those lines from standing alone. Do NOT
paste that table bare into a commit or PR body, because then it does bump.

The `BREAKING CHANGE:` marker is the exception. It is the one marker read only from the footer,
the last blank-line-separated block.

**When to bump.** A breaking change to the Manager ↔ Session API is a major bump. That API is the
protocol between the `yession-manager` and `yession-session` bins, and the Manager tolerates anything but a
MAJOR mismatch. Otherwise use standard semver: new user-facing capability → minor, bug fix → patch.
The same policy applies once the version leaves beta. A plain `feat:` subject does NOT bump. Only an
explicit marker moves the triple.

**Commit / PR messages.** Subjects follow conventional-commit style (`feat:`, `fix:`, `ci:`,
`refactor:`, ...). That is a convention for readers, not the version input. A PR squash-merges as
PR TITLE + PR DESCRIPTION. The description IS the commit body, and branch commit messages are
discarded. So put the marker in the PR description, on a line of its own. Write that description
as the commit message it becomes, not as notes for a reviewer. Wrap it at 72 columns. GitHub
re-wraps wider lines, and a re-wrap can fold a marker into prose.

What a build reports of itself: `app/Version.fs` lists the values, and `app/Telemetry.fs` states
why `service.version` is a code default.

## Finding F# symbols

Never search for a bare name. F# reuses one identifier across several unrelated symbols.
`SessionId` is a type, its companion module, a DU case constructor, and twelve record fields.
`rg '\bSessionId\b'` returns 321 hits. Only 97 are the type.

Search for the **declaration form** instead. It is anchored and unambiguous:

```
rg '^type SessionId\b'                      # the type
rg '^module SessionId\b'                    # its companion module (usually just below)
rg '^\s*(type|module|let|and)\s+Foo\b'      # any declaration of Foo, when unsure which
```

Then scope to the owning file, because short member names repeat across sibling modules.
`rg '^\s+let value\b' src/Yession.Domain/Identity.fs` returns nine hits, one per identity type.

Two properties make this reliable:

- **Compile order is explicit.** Each `.fsproj` lists `<Compile Include>` in order. A symbol is
  declared in that file or an earlier one. Read the `.fsproj` to bound the search.
- **Scoping is strictly top-down.** Within a file, a definition precedes every use. Going down, the
  first match is the declaration.

Text search cannot recover a type F# inferred rather than wrote (`let x = foo bar`). Follow the
right-hand side to its declaration, or write the type you expect and let `check` tell you.

## Refactoring across call sites

**Narrow the type. Let the compiler enumerate the sites.** Change a field's type, a parameter's
type or a union's cases. Every place that no longer fits is a compile error, all at once, before
anything runs. The number of call sites is no reason to keep a looser type. It is the list
of places the looser type was letting something through.

`MessageSent.Author` stayed `ActorRef` because it had sixty-five sites. The turn's principal rode
BESIDE the message instead: two values, consistent by discipline and not by type. Narrowing it took
one edit and forty minutes of compiler errors in fixtures. The sites were never the cost. The
aliasing was.

The pattern, in order:

- Change the declaration (`Identity.fs`, a facts file) and nothing else. Build. Every error is a
  site, and there are no others.
- Fix each site with the value it already had, never with a default: `Principal.toActor p` where
  an actor is wanted. A site with nothing of the right type to give is the finding. Examples: a
  caller that passed `None` because it could, an event that recorded the author instead of the
  owner. Give those a case, a constructor, or a refusal, not `Option.defaultValue`.
- A test that pinned the old shape ("an agent act with no owner still decodes") is not a
  regression guard when the shape was the bug. Remove it. Say in its place what the type now says.

Do not keep a bridging alias, a second constructor, or an `option` "for now". A build that goes red
at every site is the tool working. A bridge is the fault coming back with a name that says it is
fine.

## Type safety

F# is the type system. JavaScript is only what it compiles to. Each construct below re-opens the
hole Fable closed. Only two have an analyzer rule, and it covers part of each, so the rest is on the
author. Do not introduce any of these, and remove them from code you are already changing:

- **A JavaScript program in a string.** An `[<Emit>]` outside `src/Fable.*` (rule YES011 reports
  it), an inline `<script>` body, a CDP `Runtime.evaluate` source. Nothing type-checks the string.
  Write F# through Fable, behind a binding project if it reaches an API we do not own.
- **Dynamic access: `?` and `?<-`.** `fs?readdirSync path` is one typo away from a silent
  `undefined`. Bind the module. Key the map with a type.
- **`unbox`, `!!`, `:?>` on a value you did not just construct.** They assert and do not check. A
  `Y.Text` unboxed as `string` does not throw, and the fault surfaces somewhere else. Decode it, or
  type whatever produced it.
- **`obj` in a signature.** Both ends are free to disagree about the contract. If it crosses a
  wire, use a codec. If it configures a binding, use that binding's options type.
- **`createObj` / `==>` handed to a binding project.** Give the binding an interface and
  `jsOptions`. A header name spelled by hand is eventually spelled wrong, and nothing says so.
- **`[<AllowNullLiteral>]` outside `src/Fable.*`.** A nullable JavaScript interface in product code
  is a binding that never moved into a binding project.
- **A decode that cannot refuse.** `Option.defaultValue ""` over a field read makes absent, garbage
  and genuinely-empty arrive identically (rule YES009 reports that literal). Answer `None`, or say
  in the code why the default is the right answer.

The escape is one of three: a **binding project** (an API we do not own), a **codec** (a wire), a
**typed options interface** (a bag). Where none applies, keep the cast on the line that needs it
and give the result a type immediately. Never carry `obj` onward. Example: the DOM's
`querySelector` answers `Element` and no other shape is available.

## Testing

Capabilities that the run declares gate tests, not folders (`tests/Yession.Tests/Tags.fs`). A
suite runs only when this environment has every capability it needs. Otherwise it reports a skip,
never an error. Pass the capabilities THIS box has as arguments:

```
check                        # cheap tier: pure/model/protocol on Node. Every PR. Fast.
check Browser                # + host-free rich-editor E2E. Needs only Chromium.
check Ports Native           # + WebRTC/host suites. Need the node-datachannel addon.
check Keyring                # + the OS-credential-manager suite. Headless, check re-execs
                             #   itself under a private D-Bus session + gnome-keyring.
check Srt                    # + the sandbox escape probes: read/write/egress denial through
                             #   real bubblewrap. See Srt below for this container's profile.
check Nix                    # + the build-source contract: what the derivations may SEE of
                             #   the working tree (a `nix eval`; seconds).
check NixBuild --runtime none # builds the installable from the WORKING TREE and boots it, and
                             #   runs no suite. Minutes; the only gate on it.
check Jumpstarter            # + our MCP client driven against the Python example's provider,
                             #   over two real child processes. Needs uv and a CPython.
check Browser Native Caddy   # + the fronted deployment: the proxy example's Caddyfile, for
                             #   real, in front of a Manager under trusted headers. Needs caddy.
check Docker Dogfood         # + the self-hosting run: this repo's whole suite inside the
                             #   dev container its own yession.yaml declares (`check`, behind
                             #   the file's `nix develop --impure --command` entrypoint, in the
                             #   container, through the real docker backend). ~11 min warm, up to an hour cold; in NO scheduled
                             #   tier — run it when the container environment story changes,
                             #   locally or via a verify.yml dispatch naming both caps.
verify                       # == check Browser Ports Native Docker LiveAgent LiveGitHub
                             #    Keyring Nix NixBuild Srt Pty Serial Jumpstarter Caddy. Release gate; what
                             #    CI runs on master (spread over the tiers in
                             #    .github/verify-tiers.json).
                             #    Takes check's trailing args, so `verify --only "<text>"` works.
lint                         # actionlint over .github/workflows, then the F# analyzers over
                             #   every project in Yession.slnx. Runs first in the PR gate.
                             #   Exits 1 having judged the source and rejected it; exits 2
                             #   having judged NOTHING, because it does not compile — `build`;
                             #   exits 3 having judged nothing because the ANALYZER did not
                             #   finish — nothing to fix, run it again.
check --only "<text>"        # narrow BOTH runtimes to cases whose full name contains <text>.
                             #   Buys back the RUNNING, not the compiling: 66s -> 44s on the
                             #   cheap tier, and far more on a tier that spawns browsers.
check Browser --runtime clr  # run ONE of the two runtimes (node | clr), and compile only what it
                             #   needs. What the gate's `browser` tier runs: its Node suites are
                             #   all the `node` tier's too (`VerifyTiers` holds it to that).
```

To eyeball a rich-editor change in a real browser without any WebRTC machinery, run
`check Browser`. It drives Chromium against `tests/browser/editor-harness.html`. The full two-peer
WebRTC E2E runs where the Nix-built `Native` addon is present (CI, `verify`).

To inspect or iterate on a server-rendered surface (the manager page) with real screenshots, read
`.agents/skills/ui-exploration/SKILL.md` first. Headless Chromium's window-size clamp makes naive
mobile screenshots lie. The camera that the skill uses (`frames --boot --still`) does not.

`lint` is separate from `check` because it judges source without running it. Running the code
would never reach some faults. GitHub checks a workflow file only when it runs, and
`release.yml` runs on master after the merge. A syntax error there is invisible to PR CI and lands
broken. A Lit template hole is boxed before Lit sees it, so a record or a union in one renders
whatever it stringifies to. No test of the model sees that, and no compiler warning covers it.

`lint` takes its verdict from a SARIF report that it names, not from the analyzer's exit code. A
killed analyzer prints nothing and exits non-zero like a finding. The fixture check reads the same
report, so one reader serves both. `tasks.fsx` (`lint`, `analyze`) holds the reasoning.

### Analyzer rules

`lint` runs the F# analyzers in `analyzers/Yession.Analyzers`. Each rule file starts with a header
comment that says why the rule exists. Read it before you change a rule or fix its finding.

- `TemplateHoles.fs` — A Lit template hole (`html $"""{x}"""`) holds only a string, a
  `TemplateResult`, a sequence of these, a listener, an `int` or a `bool`. Render any other type
  to a string first. **Adding a renderable type is deliberate.** Widen the list and say in the
  fixture what the widening means. A closed list fails loudly, and this rule fails by silence.
- `EmitMacro.fs` — Each `$N` in an `[<Emit>]` string names an argument that its binding takes,
  and each argument appears in the string. Prefix a parameter that you keep but do not emit
  with `_`.
- `EmitBody.fs` — An `[<Emit>]` body must not declare a JavaScript binding that can collide with
  the caller's variable, and must not write a placeholder twice. Pass the substitutions as
  parameters of a real function. Write a constructed placeholder as `new ($0)($1)`.
- `RecordShapes.fs` — Two record types with the same field names make a bare `{ … = … }` build the
  type declared last. No diagnostic says so. Put `[<RequireQualifiedAccess>]` on every type in the
  group and name the type at each construction.
- `NamespaceShadowing.fs` — A namespace and a module with the same short name may not share a
  member. Otherwise `Terminals.foo` resolves to the namespace with no diagnostic. Rename the
  member on one side, or give the namespace a short name that nothing else uses.
- `DomainExports.fs` — No two `Yession.Domain.*` namespaces may export the same name, because a
  file that opens both gets the last one opened. This keeps short names such as `Projection` and
  `Subject` unambiguous. Rename one export, or put the shared concept in the kernel. The rule
  names its family and does not derive it from the assembly graph.
- `EnvWrites.fs` — Write the process environment from one file per assembly, and let that file
  restore what it found. The test suite writes through `Support.withEnv`. The rule guards the
  write because its consequence shows only in tiers that few people run.
- `EnvReaders.fs` — Read each environment variable in one file per assembly and pass the value
  down. A second reader is a second default, and two defaults that disagree resolve unnoticed.
- `AwaitSeam.fs` — In every project that can name `Interop.awaitPromise`, await through it and
  never call `Async.AwaitPromise` raw. A raw await can lose its rejection handler to Fable's
  trampoline, and Node then kills the process. The browser client has no seam and is not judged.
- `EmitHome.fs` — Declare an `[<Emit>]`, or any `EmitMethod`, `EmitConstructor`, `EmitIndexer` or
  `EmitProperty`, only in a binding project. A binding project references nothing Yession
  (`src/Fable.*`, the serial example, the analyzer fixtures). Write other code in F#.
  `[<Import>]` is not an emit. A text search for `[<Emit` misses the fully spelled
  `[<Fable.Core.Emit(...)>]`, and the rule does not.
- `Population.fs`, `Surfaces.fs`, `Expressions.fs`, `Environs.fs` — The shared readers.
  Population reads what a project can name. Surfaces reads what an `open` puts in front of a file.
  Expressions reads the calls that a project makes. Environs finds the calls that touch the
  process environment. A verdict over a whole population is reported once, on the project's last
  hand-written source file.
- `Unjudged.fs` (YES000) — Reports the first compile error in a file. A declaration that the
  compiler could not build is missing from the tree that every rule reads. `lint` then exits 2, and
  the next step is `build`.
- `Kept.fs` — Holds one project's answer at a time. `lint` analyzes every project in ONE process,
  so a per-project cache that never evicts grew the analyzer to 10.5 GB. Use `Kept.Answer` for a
  per-project cache.

Declare a dependency on the project that uses it. `[<Emit>]` binds an API this repository does
not own. Do not use it to avoid a reference. A reference costs `lint` seconds. If a population walk
grows to an hour, fix the analyzer.

Every rule carries a fixture at `analyzers/fixtures/<Rule>Fixture`. Its `// YES00n` markers say
which lines the rule must report. On every run `lint` compares the markers with that rule's own
diagnostics in both directions. It counts only that rule's diagnostics, because the two emit
rules read the same macros and have opinions on each other's cases.

A rule about how many files do something has fixture cases in several files. One file can neither
break that rule nor prove that it still sees a break. The fixtures stop a rule from going silently
blind. Each of these faults reports a clean product:

- a loader that no longer matches the assembly name
- a compiler upgrade that moves the typed-tree shape
- a project that failed to restore

A YES000 can also appear in CI alone. It is a phantom type mismatch, anchored at a column past the
end of its line. The file compiles cleanly under `build`, `fsharp-analyzers`, Fable and a
from-scratch `lint` on this box. Every case so far was a name shadowed across an option boundary.
For example, `secret : string option` was rebound as `secret : string` in the branch that resolved
it. Renaming the inner binding to `launchSecret` cleared CI.

If a YES000 has an impossible location and will not reproduce, look for that shadow. Do not give a
binding two types under one name. The cause is not yet known, so until it is, the rename is the
fix.

### Running a tier this box cannot host (`verify.yml`, on demand)

`Docker` and `LiveAgent` need a daemon and a real model credential. No laptop reliably has both,
and no pull request runs either. Do not develop against them by merging and watching the release
gate. A red master stops every release, and the live clone suite cost two releases without a
diagnosis.

Dispatch the gate against the branch instead. It is the job that the release gate runs, because
`release.yml` calls the same file. A green run here therefore means what a green master means:

```
gh workflow run verify.yml --ref <branch>                      # the whole gate
gh workflow run verify.yml --ref <branch> \
  -f capabilities="LiveAgent Ports Native Srt"                 # one tier
gh workflow run verify.yml --ref <branch> \
  -f capabilities="LiveAgent Ports Native Srt" -f only="add_repo"   # one tier, one case
gh run watch "$(gh run list --workflow=verify.yml -L1 --json databaseId -q '.[0].databaseId')"
```

(You can also use the Actions tab → verify → Run workflow, which takes the same two fields.)

Use it when a suite you write needs a capability this box lacks, or a release run failed inside one
and you need another look. Narrow it with `only`. That turns a 9-minute gate into a run that answers
one question. It also keeps a live case inside the Node suite's shared budget (`nodeBudgetMs` in
`tasks.fsx`: a base plus an allowance per capability). A long per-case deadline otherwise blows
that budget for every suite at once.

Two edges to know before the first attempt:

- GitHub offers a `workflow_dispatch` only for workflows on the DEFAULT branch, so the file must
  be on master before you dispatch a branch against it. Land a new dispatchable workflow on its
  own, ahead of whatever needs it.
- The credential is a repository secret, so a run dispatched from a fork gets none. The tier then
  fails and does not skip, by design (see below).

**Asking for a capability requires it.** `check` refuses to start when a tier names a capability
that this box cannot host. It names every missing capability and how to get it. So `check Docker` on
a box without a daemon is an error, and `verify` runs only where the whole gate can run.

`check` never drops a capability quietly to let its suites report a skip. A skip reads as prudence
and behaves as a blind spot. A release workflow that named a secret the repository lacked shipped
every version up to v5.0.0-beta.0 with the live agent suite skipped and green. No real agent turn
ever ran. Two things still skip: a capability that the tier did not ask for, and the RUNTIME
partition. The partition means a Node suite on the .NET CLR, and the reverse.

Capabilities: `Need` in `tests/Yession.Tests/Tags.fs` declares each one. `requireCapabilities` in
`tasks.fsx` calls the probes and holds the refusal messages.

- `Browser` — Chromium through the .NET Playwright driver. Pins the .NET CLR runtime.
- `Ports` — binds TCP ports or spawns processes.
- `Native` — the native `node-datachannel` WebRTC addon, loaded by the real Session. Nix builds it
  from source into the `nodeModules` derivation that the dev shell symlinks as `node_modules`. So
  the `Native` suites (all host-spawning ones, including the real WebRTC data-channel E2E) run here.
  Outside Nix the addon is absent and they skip.
- `Pty` — the native `node-pty` addon: a real pseudo-terminal, not a pipe.
- `Serial` — a real serial engine: the `serialport` addon, `udevadm`, and socat for a PTY pair.
- `Docker` — a reachable daemon, probed with `docker info`. The probe uses the socket or
  `DOCKER_HOST` that the backend uses, so it asks the question the suites ask. The dev container
  has none, so `check Docker` here refuses.
- `LiveAgent` — real model credentials: `ANTHROPIC_API_KEY` or `CLAUDE_CODE_OAUTH_TOKEN`.
  `release.yml` passes the repository secret. If it is absent, a tier that asked for it fails.
- `LiveGitHub` — a `GITHUB_TOKEN` that GitHub accepts, probed by asking GitHub with it.
  `LiveGitHub.fs` asks the real API, read-only and against this repository, the contract that
  every stubbed GitHub suite assumes and none can check. `verify.yml` passes the run's own
  `secrets.GITHUB_TOKEN`. In a Claude Code container the variable is a placeholder that the agent
  proxy swaps for the real credential. That works only through `HTTPS_PROXY`. The proxy
  authenticates EVERY request to GitHub, so no anonymous case can run there.
- `Keyring` — a usable OS credential manager (the secrets KEK lives there). On a desktop,
  `check Keyring` drives the genuine Keychain, Credential Manager or Secret Service. Headless
  (this container, CI), it re-executes itself under a private D-Bus session and a gnome-keyring
  unlocked with an empty password (both from devenv).
- `Srt` — OS-level confinement: bubblewrap and socat on Linux, Seatbelt on macOS. The probe RUNS
  the sandbox, because installed is not the same as permitted. This container cannot create the
  nested user namespace that the strict profile needs. Here the suites run only under
  `YESSION_NESTED_SANDBOX=weak check Srt`. Unset, `check Srt` refuses to start.

  **`Srt` is not only the escape probes.** The default work sandbox IS srt, so every suite that runs
  a real command needs it. That includes the browser case that types a command into a terminal
  composer. Without a sandbox, these suites hang and do not fail, because the command never runs and
  the block never reaches `ok`. A Playwright timeout eventually reports that this machine cannot run
  the test. A capability tag turns that hang into a one-line skip. If a suite you write runs a
  command, tag it `Srt`.

  **Set the variable only to give a suite a sandbox to RUN in.**
  `YESSION_NESTED_SANDBOX=weak check Browser Ports Native Srt` runs the whole PR tier in this
  container. Profile strength does not touch what those suites test (a composer binding, a command
  that really ran). Do NOT set it when the CONFINEMENT is the thing under test.

  `weak` is srt's `enableWeakerNestedSandbox`, so a green escape probe under it is a green for a
  profile that production never uses. Strict is CI's job: `pr.yaml` clears
  `kernel.apparmor_restrict_unprivileged_userns`, so the probes run for real there. Weaker
  confinement in production is the operator's decision, never a way to get a passing session here.

  **On a Mac, `vm-check <caps>` runs the Srt tier under STRICT nesting.** The Mac uses Seatbelt
  and the dev container cannot nest a user namespace, so neither hosts what CI runs. The Colima VM
  is a bare Linux host that can. The verb compiles here and runs the (portable) suite there, on a
  cached Node 24 and Linux `node_modules`. That takes seconds, not a `verify.yml` round trip:
  `vm-check Srt --only "<text>"`. `LinuxTarget` in `tasks.fsx` sets the target, and Colima is the
  only one today.
- `Jumpstarter` — `uv`, and an interpreter that can resolve the `examples/jumpstarter` lock. The
  probe BUILDS that environment (`uv sync --frozen`), because "uv is on PATH" does not mean this
  box can assemble it. devenv provides `uv` and a CPython, and pins the interpreter with
  `UV_PYTHON`/`UV_PYTHON_DOWNLOADS=never` so `uv` never fetches a second, unpinned toolchain.
- `Caddy` — the `caddy` binary, probed by running it. The fronted-deployment browser case runs
  `examples/proxy/caddy/Caddyfile`, the file the README tells an operator to deploy, in front of
  a real Manager. Without the capability, nothing runs that file. devenv provides caddy.
- `Dogfood` — consent to the LONG self-hosting case (`tests/Yession.Tests/DevContainer.fs`): the
  suite re-runs itself inside the dev container this repo declares. It needs `Docker` beside it. Its
  own probe is egress to cache.nixos.org, because the in-container devshell substitutes from there
  and a box without it would source-build for hours. It sits outside `verify` on purpose
  (`Tags.fs`). On a macOS host whose daemon is Colima, the suite's fixtures live under `$HOME`.
  Colima shares only `$HOME` into its VM, so a bind source outside it mounts empty.
- `Nix` — the nix CLI (probed like Docker). It covers the ONE thing no CI job can: the
  derivations built against the WORKING TREE, because every CI route evaluates a git-filtered
  flake copy. `check Nix` asserts the source contract (`NixSource.fs`), an evaluation of seconds.
  `NixBuild` builds `nix/worktree.nix` and boot-smokes the result, which also re-checks the
  dotnet-tools FOD hash that a devenv-only `check` cannot see. They are two capabilities because
  they cost differently: the build is minutes of sandboxed compile that belongs on a runner of
  its own.

### Writing tests

**A test pins one invariant and goes red only when that invariant breaks.** A test earns its
place by keeping two promises: what it PROVES, and what its red MEANS. A suite whose red can also
mean "something moved" is a suite that people re-run instead of read.

One invariant is one arrangement, one action, and one assertion of the consequence, under a name
that says which invariant. A case that asserts three things fails as one, so its red names none
of them. Each revision of one behaviour also drags in the two behaviours that nobody touched.
Split it: three cases cost three names and buy three verdicts. Hoist the shared SETUP into a
helper. A call that both succeeded and returned the right body is one invariant seen from two
angles. An open terminal that reads back, a closed one that still does, and a shell that refuses
are three tests wearing one name.

So that red means what it says:

- Assert observable behavior and contracts. Do not assert implementation detail (private state,
  call order, exact log text, incidental DOM structure). A refactor that preserves behavior must
  not break a green test.
- Make tests deterministic: no real-time sleeps, no ordering luck, and no reliance on anything
  that a declared capability does not provide. A flaky test is worse than none, because it trains
  everyone to ignore red.
- Write a manual check of interesting behavior (a bug fix, a protocol edge, a rendering quirk)
  down as a lasting test. The manual run proves it once. The test keeps proving it. Verify-once
  throwaways stay out.
- Tag each suite with the MINIMUM capabilities it truly needs. It then runs in the cheapest tier
  that can host it and skips, never errors, everywhere else.

**A failure that cannot say why is a failure you will pay for repeatedly.** Most tiers fail
legibly, because an assertion prints what it expected. The browser tier does not. A case can only
fail by a wait that never settles, so its red names the wait and never the fault. Three unrelated
defects in one feature printed the same thirty-second timeout, and each cost a full gate run to
tell apart. The page had said which was which all along.

Instrument the boundary instead of guessing across it, and instrument it to STAY:

- **Fixtures keep what the thing under test said** — console, page errors, failed requests — and
  print it when a case fails (`Browser.fs`: `watching` / `reporting`). It costs nothing on green.
  It is the only way to read a red run in CI, where nothing can be attached afterwards.
- **Make anything that decides silently say so.** A service worker that chooses between the
  network and a kept copy leaves no trace anywhere. One `console.debug` at that branch lets you
  read the answer and not infer it.
- **Throwaway instrumentation is a smell.** If a probe answers a question that someone could ask
  again, put it in the fixture, not in a scratch file. What you verified once by hand stays
  verified.

Before you run the gate again to test a hypothesis, make looking cheap. When looking is
expensive, guessing and verifying cost the same, so guessing wins each time and loses overall.
`check --only "<part of a test name>"` narrows both runtimes to the cases that match.

**A UI test pins an invariant, never a design.** A rendered surface is the most-revised thing in the
product. The next person to improve it deletes a test that asserts what it currently LOOKS like.
Until then, its red says "your redesign is wrong" when it means "something moved". Test only what
holds however the screen is redrawn:

- **Availability** — a destructive control is not offered over nothing. A network fault never
  takes the composer away. An action that a person may take is reachable.
- **Identity and attribution** — one person wears one name on every surface at once. An author is
  never a raw id. A thing that was said is attributed to whoever said it.
- **The accessibility floor** — keyboard-operable controls, accessible names, held contrast. Pin
  it ONCE and centrally (the chrome-consistency and theme-contrast suites). Never re-assert it
  per surface.

Do not test any of these:

- which element marks an empty state
- what a style token's Tailwind string contains
- whether a control recedes by border or by opacity
- the presence of a decorative mark
- the copy of a label that is not itself a promise

Those are the design changing, which is what a design is FOR.

Stop if the assertion quotes a class name, or would still pass if the surface rendered upside down
in the dark. Both mean the test knows how the view is BUILT and not what it PROMISES. Write the
promise, or write nothing.

**A screenshot and a test answer different questions.** A screenshot answers *is this any good*:
is it crowded, is the hierarchy right, does the eye land where it should. That judgement belongs
to the design as it stands today and has no answer that stays true. Written down as an assertion,
it fills the suite with tests that nobody believes. Go and look (the `ui-exploration` skill drives
real screenshots), decide, and move on without writing anything.

A test answers *is this still true*. Needing a browser to SEE the answer says nothing about whether
it is worth pinning. Visibility, focus, stacking, overflow and contrast are promises that only a
rendered page can settle. They break silently, because every cheap tier reads markup that still
looks right.

For example, a whole-page render contains both mounts of a notice and passes, while a person sees a
screen that says the same thing twice. "Whatever is wrong with the connection is on the screen
exactly once" is a promise that no markup test can reach. When the invariant is real and only a
browser can observe it, the browser tier is where the test goes. "I looked once and it was fine" is
not a substitute, because it is the state every regression starts from.

**What makes a browser test brittle is the COUPLING, not the browser.** One such test failed on
its first run because it counted the words *not connected* and *reconnecting*, while the surface
said *session stopped*. The invariant was right. The assertion was written against the copy.
Assert what a browser can measure and a redesign does not move:

- **Hooks, and how many of them are VISIBLE** — one `data-*` per report, however many mounts it has.
  Count the mounts a person can see, never the words in them. Beware the cheap visibility checks.
  `offsetParent` is null for anything `position: fixed`. A bounding rect stays non-zero for an
  element that an `overflow-hidden` parent clips to nothing. This is how a collapsed nav's contents
  measure as on screen. Hit-test the element's own centre and ask whether what is painted there
  belongs to it.
- **Geometry** — a fixed bar does not cover the header under it, and nothing overflows the
  viewport sideways. Compare measurements. Never pixel-match a screenshot, because a reference
  image fails on a font tweak, which is the coupling again.
- **Computed values** — a focus ring actually paints, and a token resolves to the contrast it
  promises.

Regress the behaviour and watch the test go red. This rule is worth more here than anywhere: a
browser test that cannot fail is expensive silence.

Do not loosen a UI assertion until it tests nothing. A whole-page render contains every surface at
once. So a bare `.Contains "swift-heron"` passes on the ROSTER while the element under test still
prints a raw id. Scope the assertion to the element, or to the phrase around it. Then confirm it by
REGRESSING the behaviour and watching it go red. Decoupling from incidental copy is an improvement
only while the test still fails for the right reason. A test loosened into vacuity is worse than the
brittle one it replaced, because it reads as coverage.
