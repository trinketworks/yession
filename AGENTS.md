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
document in this repository. Put what is worth keeping next to the code it governs, where a test
can contradict it.

## Bootstrap

**devenv.nix** declares the dev environment (Node 24 + .NET SDK 10), the tasks (devenv `scripts`)
and the build outputs (devenv `outputs`). `devenv shell` puts `node`, `dotnet` and the task scripts
on PATH.

**Inside a yession work sandbox** (`YESSION_SANDBOX` is set): the entrypoint in `yession.yaml` has
already assembled the devshell. `check`, `build` and the other tasks are on PATH as written. Put
nothing in front of them. Do NOT install devenv, because the devshell exists. Do NOT run
`.claude/setup.sh`, because it targets Claude Code containers.

**Heavy verbs take a build slot.** Every session's sandboxes share one container VM, and three
dotnet/Fable builds at once do not fit it. So `check`, `verify`, `build`, `compile`, `stage`,
`package`, `lint`, `bench`, `example` and `crash-repro` (and the build step of `start`, `probe`
and `frames`) first lock a slot under `/nix/var/yession/build-slots`, on the `/nix` volume that
all sandboxes share. When every slot is held, the verb prints `build slot: waiting for another
session's build (held by …) …` and waits. That is not a hang. Do not kill it or retry it. A
killed holder releases its slot at once. `yession.yaml` sets `TASKS_BUILD_SLOTS` (the slot
count, 2) for `dev` and `gate`. Where it is unset (a laptop, CI), nothing waits.
`TASKS_BUILD_SLOTS=0` builds now without a slot. `TASKS_BUILD_SLOT_TIMEOUT` is the wait in
minutes before the verb fails (default 60). See `withBuildSlot` in `tasks.fsx`.

**In a Claude Code container**, the SessionStart hook runs `.claude/setup.sh --hook`. When devenv is
missing, it starts the full setup in the background and prints where the log is. The sandbox proxy
blocks the `github:cachix/devenv` fetch, so the script writes the gitignored `devenv.local.yaml`,
which repoints the input at devenv's own source, substituted from `cache.nixos.org`. A laptop or CI
uses the committed `devenv.yaml`.

Run the task scripts: `devenv shell -- <task>`, or bare inside the shell. The main ones are
`check`, `build` and `verify`. They run `restore` for you. Never run `dotnet`, `fable` or
`esbuild` directly: the scripts match tool versions and PATH to CI (`.claude/direct-tool-hook.sh`
enforces it).

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
hole Fable closed. Where a rule reports one, it is named. The rest is on the author. Do not
introduce any of these, and remove them from code you are already changing:

- **A JavaScript program in a string.** An `[<Emit>]` outside `src/Fable.*` (rule YES011 reports
  it), an inline `<script>` body, a CDP `Runtime.evaluate` source, a Playwright `EvaluateAsync`
  program (rule YES012 holds the suite's few fixed ones to `PageScript`). Nothing type-checks the
  string. Write F# through Fable, behind a binding project if it reaches an API we do not own; in
  the browser suite, a probe in `Yession.App.Probes`.
- **Dynamic access: `?` and `?<-`** (rule YES015 reports them). `fs?readdirSync path` is one typo
  away from a silent `undefined`. Bind the module. Key the map with a type.
- **`unbox`, `!!`, `:?>` on a value you did not just construct** (rule YES016 reports `unbox`,
  `!!` and a `:?>` from `obj`; a downcast from a declared type is yours to judge). They assert and
  do not check. A `Y.Text` unboxed as `string` does not throw, and the fault surfaces somewhere
  else. Decode it, or type whatever produced it.
- **`obj` in a signature** (rule YES017 reports it in a function's or a member's). Both ends are
  free to disagree about the contract. If it crosses a wire, use a codec. If it configures a
  binding, use that binding's options type.
- **`createObj`, `==>` or `[<AllowNullLiteral>]` outside a binding project** (rule YES013 reports
  them). A hand-built object or a nullable interface in product code is a binding that never moved.
- **A decode that cannot refuse.** `Option.defaultValue ""` over a field read makes absent, garbage
  and genuinely-empty arrive identically (rule YES009 reports that literal). Answer `None`, or say
  in the code why the default is the right answer.

The escape is one of three: a **binding project** (an API we do not own), a **codec** (a wire), a
**typed options interface** (a bag). Where none applies, keep the cast on the line that needs it,
mark that line for its rule (`analyzers/AGENTS.md`, "A justified exception"), and give the result a
type immediately. Never carry `obj` onward. Example: the DOM's
`querySelector` answers `Element` and no other shape is available.

## Testing

Capabilities gate suites, not folders (`tests/Yession.Tests/Tags.fs`). Pass the capabilities THIS box
has. `check` refuses a capability the box cannot host. Run `help` for the verbs and capabilities.
`check --only "<text>"` narrows a run to the cases whose full name contains `<text>`.

To eyeball a rich-editor change, run `check Browser`.

To screenshot ANY UI change, use the camera. It boots a scratch Manager, takes one picture, and
stops it:

```
dotnet fsi tasks.fsx frames --boot --still                   # a session's page, true phone size
dotnet fsi tasks.fsx frames --boot --still --desktop         # 1280x800 (--size WxH for any size)
dotnet fsi tasks.fsx frames --boot --still --page /          # another page: the Manager's own
dotnet fsi tasks.fsx frames --boot --still --measure "<js>"  # and print what <js> returns
```

Run it in the dev shell. It writes `./frames/still.png` (`--out` moves it) and prints `vw`,
`docW`, `overflowX` and `hover`. Open the PNG and look at it. Do NOT use
`chrome --headless --screenshot`. Its window clamps to about 500px and it has no hover or focus,
so its pictures lie. As root (a sandbox), Chromium needs `--no-sandbox`. `frames` adds it by
itself from PR #1281 on. If a run as root fails with "never wrote DevToolsActivePort", your
checkout predates that fix. `.agents/skills/ui-exploration/SKILL.md` has the full loop.

For a question the camera cannot ask, script Playwright by hand. `node_modules` has no
`playwright`. The dev shell sets `$PLAYWRIGHT_CORE` to the `playwright-core` that matches its
Chromium. Import it, and launch with `channel: "chromium"`, because the shell has no headless
shell build:

```
const { chromium } = await import(process.env.PLAYWRIGHT_CORE + "/index.mjs")
const browser = await chromium.launch({ channel: "chromium" })
```

- `tests/AGENTS.md`: capability notes (`Sandbox`, `Srt`, `LiveGitHub`, `Dogfood`), running a tier
  this box cannot host (`verify.yml`), and the rules for writing tests.
- `analyzers/AGENTS.md`: working on the F# analyzers that `lint` runs.
