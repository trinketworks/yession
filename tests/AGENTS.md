Rules for the test suite: its capabilities, running a tier this box cannot host, and writing tests.

## Capabilities

`Need` in `tests/Yession.Tests/Tags.fs` declares each capability and what it needs. `requireCapabilities`
in `tasks.fsx` probes each one and holds the refusal text. Read those, not a copy here.

- Asking for a capability requires it. `check` errors on one the box cannot host. Never drop one
  quietly (`caps` in `Tags.fs` says why).
- Only two things still skip: a capability the tier did not ask for, and the runtime partition (a
  Node suite on the CLR, or the reverse).
- `.github/verify-tiers.json` holds the tiers of `verify`.
- `Chromium`: a real Chromium on Node, for the suite of the CDP client. The camera's launcher
  (`Fable.NodeExtras.Chromium`) starts it. `Browser` is Playwright on the .NET CLR. Neither grants
  the other.
- `LiveGitHub`: in a Claude Code container, `GITHUB_TOKEN` is a placeholder. The agent proxy puts in
  the real credential, and only through `HTTPS_PROXY`. The proxy authenticates EVERY request to
  GitHub, so no anonymous case can run there.
- `Dogfood` on macOS with Colima: keep fixtures under `$HOME`. Colima shares only `$HOME` into its
  VM, so a bind source in a different location mounts empty.

### Sandbox and Srt

- `Sandbox`: the suite runs a real command in the work sandbox (srt by default). Tag every such
  suite `Sandbox`. On a box without a sandbox, an untagged suite fails at once and quotes srt.
- `Srt`: confinement is under test (the escape probes). `check Srt` refuses
  `YESSION_NESTED_SANDBOX=weak`. A granted `Srt` also grants `Sandbox`.
- A Claude Code container hosts the strict profile, so `check Srt` runs there.
- A container that covers part of `/proc` (Docker's default does) refuses the fresh `/proc` that
  only strict mounts. On such a box, `YESSION_NESTED_SANDBOX=weak check Browser Ports Native Sandbox`
  runs the PR tier without the probes. CI runs the probes.
- Where the box refuses `Sandbox` under `weak` too, neither profile runs on that box.
  [docs/GAPS.md](../docs/GAPS.md) ("The strict confinement profile mounts a fresh `/proc`") says
  what refuses both.
- Weaker confinement in production is the operator's decision. Never use it to get a passing
  session here.
- On a Mac, `vm-check <caps>` runs under STRICT nesting in the Colima VM, in seconds:
  `vm-check Srt --only "<text>"`.

## Running a tier this box cannot host

`Docker` and `LiveAgent` need a daemon and a real model credential. No pull request runs either.
Do not develop against them by merging and watching the release gate: a red master stops every
release.

Dispatch the gate against the branch instead. `release.yml` calls the same `verify.yml`, so green
here means what green master means:

```
gh workflow run verify.yml --ref <branch>                      # the whole gate
gh workflow run verify.yml --ref <branch> \
  -f capabilities="LiveAgent Ports Native Srt"                 # one tier
gh workflow run verify.yml --ref <branch> \
  -f capabilities="LiveAgent Ports Native Srt" -f only="add_repo"   # one tier, one case
gh run watch "$(gh run list --workflow=verify.yml -L1 --json databaseId -q '.[0].databaseId')"
```

The Actions tab (verify, Run workflow) takes the same two fields.

Use it when a suite needs a capability this box lacks, or a release run failed in one. Narrow it
with `only`. That turns a 9-minute gate into one question. Keep a live case inside the shared Node
budget (`nodeBudgetMs` in `tasks.fsx`). A long per-case deadline blows that budget for every suite.

- GitHub offers `workflow_dispatch` only for workflows on the default branch. Land a new
  dispatchable workflow on its own, before whatever needs it.
- The credential is a repository secret. A run dispatched from a fork gets none, so the tier fails
  by design. It does not skip.

## Writing tests

A test pins one invariant and goes red only when that invariant breaks. When a red can also mean
"something moved", people re-run the suite and do not read it.

One invariant is one arrangement, one action and one assertion of the consequence. Its name says
which invariant.

- A case with three assertions fails as one, and its red names none of them. Split it: three cases
  buy three verdicts. Move the shared SETUP into a helper.
- A call that succeeded and returned the right body is one invariant from two angles. An open
  terminal that reads back, a closed one that still does, and a shell that refuses are three tests.

So that red means what it says:

- Assert observable behavior and contracts. Do not assert private state, call order, exact log text
  or incidental DOM structure. A refactor that keeps behavior must not break a green test.
- Be deterministic: no real-time sleeps, no ordering luck, and no reliance on anything that a
  declared capability does not provide. Rule YES014 refuses a sleep outside a `[<Waits>]` helper. A
  flaky test trains everyone to ignore red.
- When you test interesting behavior by hand (a bug fix, a protocol edge, a rendering quirk), write
  it as a lasting test. Keep one-time throwaways out of the suite.
- Tag each suite with the MINIMUM capabilities it truly needs. It then runs in the cheapest tier that
  can host it, and skips (never errors) everywhere else.

### Make a failure say why

The browser tier fails mostly on a wait that never settles. A wait through `Wait` (`BrowserWait.fs`,
the only kind that "Playwright's own waits" admits) names what it waited for. It stops with the
reason the page gives when the session's work sandbox did not start, and names no other fault.
Three unrelated defects once printed the same thirty-second timeout, while the page said which was
which. Instrument the boundary, and make the instrumentation STAY:

- Fixtures keep what the thing under test said (console, page errors, failed requests) and print it
  when a case fails (`watching` and `reporting` in `tests/Yession.Tests/Browser.fs`). In CI, it is
  the only way to read a red run, because nothing attaches afterwards.
- Make anything that decides silently say so. A service worker that chooses between the network and
  a kept copy leaves no trace. One `console.debug` at that branch lets you read the answer.
- If a probe answers a question that someone can ask again, put it in the fixture, not in a scratch
  file.

Make looking cheap before you run the gate again to test a hypothesis. `check --only "<part of a
test name>"` narrows both runtimes.

### UI tests pin an invariant, never a design

A rendered surface is the most-revised thing in the product. A test that asserts what it LOOKS like
goes red on every redesign, and the next person removes it. Test only what holds however the screen
is redrawn:

- Availability. A destructive control is not offered over nothing. A network fault never removes the
  composer. An action a person may take is reachable.
- Identity and attribution. One person has one name on every surface at once. An author is never a
  raw id. Each surface attributes what someone said to that person.
- The accessibility floor: keyboard operation, accessible names, held contrast. Pin it ONCE and
  centrally, in the "Chrome consistency" and "Theme contrast" suites. Never assert it again per
  surface.

Do not test any of these:

- which element marks an empty state
- whether a control recedes by border or by opacity
- the presence of a decorative mark
- the copy of a label that is not itself a promise

Stop if the assertion would still pass with the surface rendered upside down in the dark. Then the
test knows how the code BUILDS the view, not what the view PROMISES. Write the promise, or nothing.
Outside the two floor suites, the "Tailwind in tests" suite refuses a class name that `Style.fs`
composes.

### Screenshot or test

A screenshot answers *is this any good*: crowding, hierarchy, where the eye lands. No answer to it
stays true, so do not assert it. Look
([ui-exploration](../.agents/skills/ui-exploration/SKILL.md)), decide, and write nothing.

A test answers *is this still true*. Needing a browser to SEE the answer does not decide whether it
is worth pinning. Visibility, focus, stacking, overflow and contrast are promises that only a
rendered page settles. They break silently, because every cheap tier reads markup that still looks
right. Example: a whole-page render contains both mounts of a notice and passes, while a person sees
the message twice. When the invariant is real and only a browser can observe it, the test goes in
the browser tier.

### Browser tests

The COUPLING makes a browser test brittle, not the browser. Assert what a browser can measure and a
redesign does not move:

- Hooks, and how many are VISIBLE. One `data-*` per report, however many mounts it has. Count the
  mounts a person can see, never the words in them.
  - Cheap visibility checks lie. `offsetParent` is null for anything `position: fixed`. A bounding
    rect stays non-zero for an element that an `overflow-hidden` parent clips to nothing.
  - Hit-test the centre of the element itself: does what the page paints there belong to it?
- Geometry. A fixed bar does not cover the header under it. Nothing overflows the viewport sideways.
  Compare measurements. Never pixel-match a screenshot, because a font tweak fails it.
- Computed values. A focus ring actually paints. A token resolves to the contrast it promises.

Regress the behaviour and watch the test go red. A browser test that cannot fail is expensive
silence.

Do not loosen a UI assertion until it tests nothing. A whole-page render contains every surface at
once. A bare `.Contains "swift-heron"` passes on the ROSTER while the element under test still
prints a raw id. Scope the assertion to the element, or to the phrase around it. A test loosened
into vacuity is worse than the brittle one it replaced, because it reads as coverage.

### End to end against the real binaries

The browser tier IS the end-to-end harness. `startHost` (`Browser.fs`) runs the real Manager with
`--auth localhost`, which spawns the real session process. A wiring bug in `SessionMain` shows in
that tier and in no cheaper tier. Do not build a second launcher beside it.

Keep a case off the network. Two seams that the product already reads do it. Pass them as the
case's `env`:

- `YESSION_GITHUB_API_URL` points the session's GitHub calls at a loopback listener the case runs.
- `YESSION_BIN_GIT` replaces the session's git with a wrapper that can hold a clone open, or clone a
  local fixture.
  - The session sets `GIT_ALLOW_PROTOCOL=https`, and sets auth headers through `GIT_CONFIG_*`.
    Clear both, but only for the fixture's own clone.
  - Find helpers like `sleep` on `PATH`. A Nix box has no `/bin/sleep`.

The case "an added repo is in the transcript while its clone is still running" uses both. Copy it.

Run it as below in a yession `dev` sandbox. In a Claude Code container, run `bash .claude/setup.sh`
first, and put `devenv shell --` in front:

```
check Browser Native --only "<part of a test name>"
```

### Browser cases run three at a time

The .NET CLR run takes three cases at once (`BrowserRunner.fs`). Node still runs one at a time. So
a browser case:

- Owns what it arranges: its data dir, its ports (`--port 0`, `listenOnLoopback`), its contexts
  (`withContexts`). A case that uses a fixed path, or keeps a host in a module-level mutable,
  shares it with whatever runs beside it.
- Shares only inside `testSequenced`. Sequence a suite whose cases must share something, and say
  beside it what they share.
- Presses through a locator, never at a point calculated from a box.
  - `Locator.ClickAsync` with a `Position` (a place IN the element) scrolls the element into view,
    waits for it to stand still, and refuses when something else would take the press.
  - Where a case must know the point (a press it hit-tests afterwards), measure it after a trial
    click (`Trial = true`) settles the element.
  - A drag starts with a hover through the locator, at a `Position`.
- Gets the harness page it is about: `editorCase` the shell's page, `fixtureCase` the fixtures'
  page (the editor, the players and the two peers). `tests/browser/editor-harness.html` says what
  each page holds and why there are two.
- Never decides a press on a read taken just before it (`Read.count`, then a click if it found
  one). The page can change between the two, and the click waits its whole timeout for nothing.
  Scope every selector to the subject of the case. Wait for the end state. When a press can find its
  control gone, let it check the end it was for.

A case's lines print with its verdict. Each case also prints `▶ <name>` when it starts, so a case
that hung has a `▶` line and no verdict.

`YESSION_TEST_WORKERS=1` runs one case at a time, to read a run top to bottom or tell a flake from
load:

```
YESSION_TEST_WORKERS=1 check Browser Native --only "<part of a test name>"
```
