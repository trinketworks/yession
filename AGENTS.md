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
build, fix, refactor or change something. It decides how the work is split. You cannot take that
decision afterwards. The skill owns the process. The rules in short:

- Split the request into the smallest increments that each stand alone. Each one builds, passes
  `check`, and breaks nothing already merged.
- Ship each increment as its own PR. Do not bank them. The merge queue absorbs a moving master but
  not conflict reconciliation, and that tax grows superlinearly.
- Compare what you built to what was asked. If it is consistent (no interesting deviations,
  blockers, or uncompletable work), open the PR with auto-merge, subscribe to PR events, and watch
  the master pipeline after the merge. Auto-fix failures and repeat the process until master is
  green.
- A deviation stops the loop. Report it to the user instead.

**Plans are not artifacts.** Say the shape of the work to the user before you start. Write no plan
document in this repository. `docs/plans/` was removed and stays removed: a plan is right on the
day it is written and unfalsifiable after. It drifts silently while it reads as current. Put what
is worth keeping from a plan next to the code it governs, where a test can contradict it.

`master` merges through a **merge queue**. Auto-merge enqueues and does not merge. A check
conclusion is not proof of either outcome, so read the PR's merge state. The skill's "Merge
semantics" section has the detail, including stacked PRs.

## Bootstrap

**devenv.nix** declares the dev environment (Node 24 + .NET SDK 10), the tasks (devenv `scripts`),
and the build outputs (devenv `outputs`: the Nix package and the npm tarball). On a laptop or in
CI, `devenv shell` puts `node`, `dotnet`, and the task scripts on PATH.

**Inside a yession work sandbox** (`YESSION_SANDBOX` is set and names which one): the container
brings nix and the checkout brings the toolchain. The sandbox's declared entrypoint (`yession.yaml`:
`nix develop --impure --command`) has already assembled the devshell around your shell. So `check`,
`build` and the other tasks are on PATH as written. Put nothing in front of them. A
`nix develop --command` prefix works but costs a second evaluation.

`--impure` in that entrypoint is load-bearing. devenv resolves its root from the process's working
directory, which a pure evaluation does not have. The pure form fails with "devenv was not able to
determine the current directory", in this container and on a laptop alike.

In a sandbox, do NOT install devenv. Do NOT run `.claude/setup.sh`. It is for a Claude Code
container and correctly no-ops here. A session that rebuilt the laptop route from the prose below
wasted two minutes on a second devenv it did not need. `yession.yaml` declares the sandboxes: `dev`
for work, `gate` for the long `verify`.

In a fresh Claude Code container, run `bash .claude/setup.sh` once. It is idempotent: minutes cold,
cheap to re-run. Its header lists the steps. In short, it installs single-user Nix, which every
later shell inherits, and the `devenv` CLI. It warm-builds and writes the gitignored
`devenv.local.yaml`. The sandbox proxy blocks devenv's normal `github:cachix/devenv` fetch, so that
file repoints the input at devenv's own source, substituted from `cache.nixos.org`. A laptop or CI
uses the committed `devenv.yaml`.

The SessionStart hook (`.claude/settings.json`) re-runs the script with `--hook`. That refreshes
`devenv.local.yaml` and settles `devenv.lock` (`.gitignore` explains why).
`tests/Yession.Tests/LockSource.fs` fails the pull request if a store path ever reaches the
committed lock.

Use the task scripts: `devenv shell -- <task>`, or bare inside the shell. The main ones are
`check`, `build`, and `verify`. `restore` (dotnet tools, and npm only when `node_modules` is
absent) runs from the others, so you do not need to run it by hand. Do NOT invoke
`dotnet`/`fable`/`esbuild` directly to "run the suite". Go through the scripts so tool versions
and PATH match CI.

Under devenv, `node_modules` is a Nix artifact: the offline npm tree with the native
node-datachannel addon baked in. `enterShell` symlinks it in, so nothing runs an npm postinstall.
Off-Nix, `restore` falls back to `npm install --ignore-scripts`.

Preinstalled in this container, no action needed:

- Chromium at `$PLAYWRIGHT_BROWSERS_PATH` (`/opt/pw-browsers`). The `Browser` cap works.
- The `node-datachannel` WebRTC addon. Nix builds it from source and bakes it into the
  `nodeModules` derivation, because npm cannot fetch its prebuilt here. The `Native` cap works too
  (see Testing).

## Build interface

`tasks.fsx` is the complete, standalone build interface. Every Yession build function lives there
(`restore`/`lock`/`build`/`start`/`dev`/`check`/`vm-check`/`verify`/`lint`/`version`/`stage`/`package`/
`install-smoke`/`boot-smoke`/`example`/`clean`/`clean-docker`). The devenv scripts, the GitHub
Actions workflows, and the Nix `outputs` are thin wrappers over it. Without them,
`dotnet fsi tasks.fsx <verb>` still drives everything.

A verb names the `Target`s it wants, and `make` builds them: each once, after what it stands on,
and side by side with whatever nothing orders it against. The `Target` doc comment in `tasks.fsx`
has the detail. `check` is the clearest example. When you add a build step, add a `Target` and its
`needs`. Do not add a call in some verb's body. An order written only in a function body is one the
next verb will not know about.

The derivations in `nix/packages.nix` have three consumers. They differ in the SOURCE they build.
`flake.nix` and `devenv.nix` both build a store copy of the repo (git-filtered for the flake,
whole-directory for devenv). `nix/worktree.nix` evaluates in place, against the tree as it stands:
`nix build --file nix/worktree.nix nix|npm|staged|nugetTools`. `check Nix NixBuild` drives that
route. It is the only one that can catch a `src` filter that has stopped matching what git tracks.

**No new helper scripts.** New build, dev, or repo functionality is a `tasks.fsx` verb, not a shell
script. Anything that could be a verb is a verb. Only glue that must run where `dotnet` cannot
stays outside. The one existing script is exactly that: `.claude/setup.sh` runs before Nix/devenv
exist. The headless D-Bus/keyring wrapping is a verb too: `check` arranges it by re-execing itself.

**No belt-and-braces.** When two mechanisms could satisfy the same requirement (two config
locations, a fallback beside a primary), keep ONLY the one verified working here. Remove the other.
A redundant spare hides which path is live, rots unverified, and turns the next failure into an
archaeology dig.

## Colocation

**A rule lives with the state it governs.** An invariant that holds only because a CALLER
remembered to ask first is not an invariant. It is a convention with a good reputation, and the
next caller has not read it.

`technical-design.md` §1 says composition happens at the top. This is the other half: what
composes at the top must have nothing left to DECIDE. A composition root that computes (a bound, a
fallback, a refusal, a subtraction) moves a decision out of the only place a cheap test reaches
it, into a place no test reaches.

The tell is a member exported for no local reason. `SessionTerminals` briefly grew an
`Instrumented` predicate that nothing inside it used. It existed so `Host.fs` could subtract a line
window and cap the answer, arithmetic no cheap test could see, and a bug lived in it. The WRITE
half of the same feature was one verb on the manager with its refusal inside it.

Ask three questions. Any `no` means the rule is in the wrong place:

- **Can a caller break it by not calling something first?** Then those calls are one verb.
  Take-then-write is `Write`, because an actor that could write without the lease is the second
  writer the lease exists to prevent.
- **Would testing it mean building the composition root?** Then move it down to where the state
  is. The cheap tier is the measure: a rule the cheap tier cannot reach is a rule nobody
  re-checks.
- **Does a sibling operation on the same state live somewhere else?** Then one of them has
  moved. `Write` and `Tail` sit together because the rule that admits one admits the other.

The corollary: a seam belongs where it is USED. The terminal manager takes a transcript reader
because `Tail` needs one, not because a layer above it offered to read on its behalf. Passing state
downward is colocation. Reaching upward for it is not.

## Fixing bugs

**Fix the root cause upstream. Harden against the symptom downstream. Do both, not either.** The
upstream fix stops THIS fault. The downstream guard stops the next fault, a different one arriving
at the same place, from turning silently into a plausible wrong answer.

This is not belt-and-braces, which is two mechanisms for ONE requirement at one point. This is one
mechanism each at two points. They go red at different times: upstream while the diagnosis still
exists, downstream before the damage is published.

Example, the version regression. A transient invalid store path made `devenv shell -- version`
fail. `echo "version=$(...)"` discards a substitution's exit status, so the step went green with an
empty version. The release job tagged the commit `v`, which matched the computation's
`--match 'v*'` and restarted the 6.13.1 line at 1.0.0.

- Upstream: capture under `pipefail` and refuse an empty result, so a failed computation fails the
  run.
- Downstream: `--match 'v[0-9]*'`, and refuse a nearest tag that is not this scheme's shape. Then
  no malformed tag can move the version again.

The tell that you did half: you can state the fault as a chain (X failed, so Y saw nothing, so Z
published garbage) and your change touches one link. Ask what the other links do next time, given
a different X.

## Examples

`examples/` holds integrations built the way somebody OUTSIDE this repository would build them. An
example is standalone by rule. It references nothing from `Yession.Domain` or `Yession.Host`,
carries its own project and bundle, and is not in the npm package or the Nix installable.
`dotnet fsi tasks.fsx example <name>` builds one.

If an example needs something the product has, it copies it and owns the copy. The duplication is
the point, not a smell. The serial provider once reached into `Yession.Host.Interop` and the
Domain's tool vocabulary. It showed a path no reader could reproduce.

An example is written in whatever the thing it integrates is written in, and the verb follows it.
`examples/serial` is F# through Fable and esbuild. `examples/jumpstarter` is a uv project with its
own pytest suite. `example <name>` dispatches on which it finds. A provider is a server, not a
plugin. An examples directory that could only hold this repository's language would argue the
opposite.

The suite still tests them. The serial provider's own tests are the reason `Serial` exists as a
capability, and the jumpstarter provider's are the reason `Jumpstarter` does. The F# example is
tested via a ProjectReference from `tests/Yession.Tests`, the Python one via a spawned process.
Both go ONE way: nothing in the product may reference an example.

## UI baseline

Read `docs/visual-design.md` BEFORE you change a surface. It states what the product looks like,
why, and the order to settle a disagreement in. Its values are `app/tokens.css`, short on purpose.
The floor below is the part of it that is never traded.

WCAG 2.0 AA is the floor for every surface, not a follow-up:

- **Contrast**: text ≥ 4.5:1 against the surface it actually sits on (3:1 only ≥ 24px, or
  ≥ 19px bold). Check every surface a token touches, not just black. The cheap-tier
  theme-contrast test (Phase4) pins the tokens in `app/tokens.css`.
- **Keyboard**: every action is a real `<a>`/`<button>`/`<input>` (no click-only
  elements), operable by Tab/Enter/Space, with a visible focus state. A DOM swap that
  replaces the focused element must refocus its replacement. Never strand focus.
- **Structure**: inputs have `<label>`s, tables have `th scope`, icon-only controls have an
  accessible name, pages declare `lang` and a title.

## Versioning

The version is computed from the commit history (policy at the top of `tasks.fsx`) and never
stored in a file. Every green master push publishes `1.0.0-beta.<n>`. To move the triple, put a
marker in the commit message. For a squash-merged PR, that is its title or description:

```
+semver: major   (or breaking, or a BREAKING CHANGE: footer)  -> 2.0.0-beta.0
+semver: minor   (or feature)                                 -> 1.1.0-beta.0
+semver: fix     (or patch)                                   -> 1.0.1-beta.0
```

A marker counts ANYWHERE in the message (subject, body, or footer) but must be a line of its own,
with nothing else on it. Prose that mentions one mid-sentence never moves the version. Neither do
the examples above: the trailing `-> 2.0.0-beta.0` keeps those lines from standing alone. Do NOT
paste that table bare into a commit or PR body, because then it does bump.

The `BREAKING CHANGE:` marker is the exception. The tool reads it only from the footer, the last
blank-line-separated block. It is a conventional-commits trailer, and it is the one marker that
moves MAJOR. Scanning the whole body for it once cut a spurious major tag off line-wrapped prose.

**When to bump.** A breaking change to the Manager ↔ Session API is a major bump. That API is the
protocol between the `yession` and `yession-session` bins, and the Manager tolerates anything but a
MAJOR mismatch. Otherwise use standard semver: new user-facing capability → minor, bug fix → patch.
The same policy applies once the version leaves beta. A plain `feat:` subject does NOT bump. A tag
is cut per green master push, so nearly every release would bump. Only an explicit marker moves
the triple.

**Commit / PR messages.** Subjects follow conventional-commit style (`feat:`, `fix:`, `ci:`,
`refactor:`, ...). That is a convention for readers, not the version input. A PR squash-merges as
PR TITLE + PR DESCRIPTION. The description IS the commit body, and branch commit messages are
discarded. So put the marker in the PR description, on a line of its own. Write that description
as the commit message it becomes, not as notes for a reviewer. Wrap it at 72 columns, which is
where GitHub re-wraps it. Wider lines fill the log with ragged half-lines, re-flowed without
regard for which of them was a marker.

`version` needs full history. It refuses a shallow clone rather than emit an already-released
number (`git fetch --unshallow --tags`). `YESSION_VERSION` overrides the computation. That is how
the Nix derivations (no `.git` in their source) are told what they are.

**Version reporting.** Both bins answer `--version`. A session reports its build to the Manager on
the spawn readiness line. Every process puts it on its OTel resource as `service.version`. Each UI
says it in its footer (`Style.buildMark`): the Manager's page from `Version.current`, a session's
sidebar from the shell's `yession-build` meta. The server renders that meta and the browser reads
it back, because the client bundle has no version of its own.

The `service.version` attribute is a CODE default. It is deliberately not part of the
`OTEL_RESOURCE_ATTRIBUTES` the Manager injects into a child. Env wins, so injecting it would make
sessions report the Manager's version and hide the skew. A build that cannot know a release
version says what it is instead (`app/Version.fs` lists the values). Never invent a version-shaped
placeholder.

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
`rg '^\s+let value\b' src/Yession.Domain/Identity.fs` returns nine hits, one per identity type,
disambiguated only by their pattern (`let value (SessionId s) = s`).

Two properties make this reliable:

- **Compile order is explicit.** Each `.fsproj` lists `<Compile Include>` in order. A symbol is
  always declared in that file or an earlier one. Read the `.fsproj` to bound the search.
- **Scoping is strictly top-down.** Within a file, a definition precedes every use. Going down, the
  first match is the declaration.

The one thing text search cannot recover is a type F# inferred rather than wrote. `let x = foo bar`
has no annotation to find. Follow the right-hand side to its declaration, or write the type you
expect and let `check` tell you if you are wrong.

## Refactoring across call sites

**Narrow the type. Let the compiler enumerate the sites.** F# has no untyped escape hatch worth
speaking of. Change a field's type, a parameter's type, or a union's cases, and every place that no
longer fits is a compile error, all of them at once, in seconds, before anything runs. So the
number of call sites is not a reason to keep a looser type. It is the list of places the looser
type was letting something through.

`MessageSent.Author` stayed `ActorRef` for one refactor because it had sixty-five sites. The turn's
principal rode BESIDE the message instead: two values, one construction site, consistent by
discipline and not by type. Narrowing it took one edit and forty minutes of the compiler pointing
at fixtures. The sites were never the cost. The aliasing was.

The pattern, in order:

- Change the declaration (`Identity.fs`, a facts file) and nothing else. Build. Every error is a
  site, and there are no others.
- Fix each site with the value it already had, never with a default: `Principal.toActor p` where
  an actor is wanted, the principal itself where one is. A site that has nothing of the right type
  to give is the finding: a caller that was passing `None` because it could, an event that
  recorded the author when it needed the owner. Give those a case, a constructor, or a refusal,
  not `Option.defaultValue`.
- A test that pinned the old shape ("an agent act with no owner still decodes") is not a
  regression guard when the shape was the bug. Remove it. Say in its place what the type now says,
  so the next reader finds the reasoning where the case was.

Do not keep a bridging alias, a second constructor, or an `option` "for now" to make the build go
green with fewer edits. A build that goes red at every site is the tool working. A bridge is the
fault coming back with a name that says it is fine.

## Type safety

F# is the type system. JavaScript is only what it compiles to. Every construct below re-opens the
hole Fable closed, and **no analyzer rule sees any of them**. The emit rules read `[<Emit>]` macros
and nothing else, so an emit count of zero measured one escape hatch, not safety. These are on the
author.

Do not introduce any of these, and remove them from code you are already changing:

- **A JavaScript program in a string.** An `[<Emit>]` outside `src/Fable.*`, an inline
  `<script>` body, a CDP `Runtime.evaluate` source. Nothing type-checks it and nothing parses it.
  Write F# through Fable, behind a binding project if it reaches an API we do not own.
- **Dynamic access — `?` and `?<-`.** `fs?readdirSync path` and `keys?("Mod-b") <- cmd` are
  one typo away from `undefined`, silently, at every warning level. Bind the module. Key the
  map with a type.
- **`unbox`, `!!`, `:?>` on a value you did not just construct.** `unbox` asserts and does not
  check. A `Y.Text` unboxed as `string` does not throw. It simply is not a string, and the fault
  surfaces somewhere else. Decode it, or type whatever produced it.
- **`obj` in a signature.** A parameter or return typed `obj` is a contract nobody wrote down,
  and both ends are free to disagree about it. If it crosses a wire, use a codec. If it
  configures a binding, use that binding's options type.
- **`createObj` / `==>` handed to a binding project.** The binding exists, so give it an
  interface and `jsOptions`. An untyped bag is the emit doctrine's hole one level down: a
  header name spelled by hand is eventually spelled wrong, and nothing says so.
- **`[<AllowNullLiteral>]` outside `src/Fable.*`.** A nullable JavaScript interface declared
  in product code is a binding that never moved into a binding project.
- **A decode that cannot refuse.** `Option.defaultValue ""` over a field read makes absent,
  garbage and genuinely-empty arrive identically. The caller cannot tell them apart, and the
  bug lands downstream of the only place that knew. Answer `None`, or say in the code why the
  default is the right answer.

The escape is always one of three: a **binding project** for an API we do not own, a **codec** for
a wire, a **typed options interface** for a bag. Where none applies, keep the cast on the line that
needs it and give the result a type immediately. Never carry `obj` onward. Example: the DOM's
`querySelector` answers `Element` and no other shape is available.

## Testing

Capabilities gate suites, not folders (`tests/Yession.Tests/Tags.fs`). Pass the capabilities THIS box
has. `check` refuses a capability the box cannot host.

```
check                         # cheap tier: pure, model, protocol on Node. Every PR.
check Browser                 # + host-free editor E2E in Chromium
check Ports Native            # + WebRTC and host-spawning suites
check Keyring                 # + OS credential manager (headless: check arranges D-Bus)
check Srt                     # + sandbox escape probes. Read tests/AGENTS.md first.
check Nix                     # + build-source contract against the working tree (seconds)
check NixBuild --runtime none # build the installable from the working tree and boot it. No suite.
check Jumpstarter             # + MCP client against the Python example provider
check Browser Native Caddy    # + fronted deployment behind the proxy example's Caddyfile
check Docker Dogfood          # + this suite inside the dev container. Up to an hour. Not in verify.
verify                        # check with every capability but Bench and Dogfood. Release gate.
lint                          # actionlint, then the F# analyzers. Judges source without running it.
check --only "<text>"         # narrow both runtimes to cases whose full name contains <text>
check Browser --runtime clr   # run one runtime (node | clr)
```

`Docker`, `LiveAgent`, `LiveGitHub`, `Pty` and `Serial` also exist. `Need` in `Tags.fs` lists all.
`verify` takes `check`'s trailing args, so `verify --only "<text>"` works.

To eyeball a rich-editor change, run `check Browser`. For real screenshots of a server-rendered
surface, read `.agents/skills/ui-exploration/SKILL.md` first. Naive headless mobile shots lie.

- `tests/AGENTS.md`: capability notes (`Srt`, `LiveGitHub`, `Dogfood`), running a tier this box cannot
  host (`verify.yml`), and the rules for writing tests.
- `analyzers/AGENTS.md`: working on the F# analyzers that `lint` runs.
