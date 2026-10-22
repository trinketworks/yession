Rules for the test suite: its capabilities, running a tier this box cannot host, and writing tests.

## Capabilities

`Need` in `tests/Yession.Tests/Tags.fs` declares each capability and what it needs. `requireCapabilities`
in `tasks.fsx` probes each one and holds the refusal text. Read those, not a copy here.

- Asking for a capability requires it. `check` errors on one the box cannot host. Never drop one
  quietly. A quiet drop once shipped releases with the live agent suite skipped.
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

- `Sandbox`: the suite runs a real command in the work sandbox, which is srt by default. Tag every
  such suite `Sandbox`. On a box without a sandbox, an untagged suite fails at once and quotes srt.
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
Do not develop against them by merging and watching the release gate. A red master stops every
release.

Dispatch the gate against the branch instead. `release.yml` calls the same `verify.yml`, so a green
run here means what a green master means:

```
gh workflow run verify.yml --ref <branch>                      # the whole gate
gh workflow run verify.yml --ref <branch> \
  -f capabilities="LiveAgent Ports Native Srt"                 # one tier
gh workflow run verify.yml --ref <branch> \
  -f capabilities="LiveAgent Ports Native Srt" -f only="add_repo"   # one tier, one case
gh run watch "$(gh run list --workflow=verify.yml -L1 --json databaseId -q '.[0].databaseId')"
```

The Actions tab (verify, Run workflow) takes the same two fields.

Use it when a suite you write needs a capability this box lacks, or when a release run failed in
one. Narrow it with `only`. That turns a 9-minute gate into one question. It also keeps a live case
inside the shared budget of the Node suite (`nodeBudgetMs` in `tasks.fsx`: a base plus an allowance
per capability). Otherwise, a long per-case deadline blows that budget for every suite at once.

- GitHub offers `workflow_dispatch` only for workflows on the default branch. Land a new
  dispatchable workflow on its own, before whatever needs it.
- The credential is a repository secret. A run dispatched from a fork gets none, so the tier fails.
  It does not skip. This is by design.

## Writing tests

A test pins one invariant and goes red only when that invariant breaks. It keeps two promises: what
it PROVES and what its red MEANS. When a red can also mean "something moved", people re-run the
suite and do not read it.

One invariant is one arrangement, one action and one assertion of the consequence. Its name says
which invariant.

- A case with three assertions fails as one, and its red names none of them. Each revision of one
  behaviour also pulls in the two that nobody touched.
- Split it. Three cases cost three names and buy three verdicts. Move the shared SETUP into a helper.
- A call that succeeded and returned the right body is one invariant from two angles. An open
  terminal that reads back, a closed one that still does, and a shell that refuses are three tests.

So that red means what it says:

- Assert observable behavior and contracts. Do not assert private state, call order, exact log text
  or incidental DOM structure. A refactor that keeps behavior must not break a green test.
- Be deterministic: no real-time sleeps, no ordering luck, and no reliance on anything that a
  declared capability does not provide. Rule YES014 refuses a sleep outside a `[<Waits>]` helper. A
  flaky test is worse than none, because it trains everyone to ignore red.
- When you test interesting behavior by hand (a bug fix, a protocol edge, a rendering quirk), write
  it as a lasting test. The manual run proves it once. Keep one-time throwaways out of the suite.
- Tag each suite with the MINIMUM capabilities it truly needs. It then runs in the cheapest tier that
  can host it, and skips (never errors) everywhere else.

### Make a failure say why

You pay again and again for a failure that cannot say why. The browser tier fails mostly on a wait
that never settles. A wait through `Wait` (`BrowserWait.fs`, the only kind that "Playwright's own
waits" admits) names what it waited for. When the work sandbox of the session did not start, it
stops with the reason the page gives. It cannot name any other fault. Three unrelated defects in one
feature once printed the same thirty-second timeout, and the page had said which was which.
Instrument the boundary, and make the instrumentation STAY:

- Fixtures keep what the thing under test said (console, page errors, failed requests) and print it
  when a case fails. See `watching` and `reporting` in `tests/Yession.Tests/Browser.fs`. It costs
  nothing on green. In CI, it is the only way to read a red run, because nothing attaches afterwards.
- Make anything that decides silently say so. A service worker that chooses between the network and
  a kept copy leaves no trace. One `console.debug` at that branch lets you read the answer.
- Throwaway instrumentation is a smell. If a probe answers a question that someone can ask again,
  put it in the fixture, not in a scratch file.

Make looking cheap before you run the gate again to test a hypothesis. When looking is expensive,
guessing and verifying cost the same, so guessing wins and loses overall. `check --only "<part of a
test name>"` narrows both runtimes.

### UI tests pin an invariant, never a design

A rendered surface is the most-revised thing in the product. The next person who improves it removes
a test that asserts what it LOOKS like. Until then, its red says "your redesign is wrong" when it
means "something moved". Test only what holds however the screen is redrawn:

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

Those are the design changing, which is what a design is FOR.

Stop if the assertion would still pass if the surface rendered upside down in the dark. Then the
test knows how the code BUILDS the view, not what the view PROMISES. Write the promise, or write
nothing. Outside the two floor suites, the "Tailwind in tests" suite refuses outright a class name
that `Style.fs` composes.

### Screenshot or test

A screenshot answers *is this any good*: crowding, hierarchy, where the eye lands. That judgement
belongs to the design as it is today, and no answer to it stays true. As an assertion, it fills the
suite with tests that nobody believes. Go and look
([ui-exploration](../.agents/skills/ui-exploration/SKILL.md) drives real screenshots), decide, and
continue without writing anything.

A test answers *is this still true*. The need for a browser to SEE the answer does not decide
whether it is worth pinning. Visibility, focus, stacking, overflow and contrast are promises that
only a rendered page settles. They break silently, because every cheap tier reads markup that still
looks right.

Example: a whole-page render contains both mounts of a notice and passes, while a person sees the
message twice. No markup test can reach "Whatever is wrong with the connection is on the screen
exactly once". When the invariant is real and only a browser can observe it, the test goes in the
browser tier. "I looked once and it was fine" is the state every regression starts from.

### Browser tests

The COUPLING makes a browser test brittle, not the browser. One test failed on its first run
because it counted the words *not connected* and *reconnecting* while the surface said *session
stopped*. Assert what a browser can measure and a redesign does not move:

- Hooks, and how many are VISIBLE. One `data-*` per report, however many mounts it has. Count the
  mounts a person can see, never the words in them.
  - Cheap visibility checks lie. `offsetParent` is null for anything `position: fixed`. A bounding
    rect stays non-zero for an element that an `overflow-hidden` parent clips to nothing. The
    contents of a collapsed nav then measure as on screen.
  - Hit-test the centre of the element itself. Ask whether the thing that the page paints at that
    point belongs to the element.
- Geometry. A fixed bar does not cover the header under it. Nothing overflows the viewport sideways.
  Compare measurements. Never pixel-match a screenshot, because a font tweak fails it.
- Computed values. A focus ring actually paints. A token resolves to the contrast it promises.

Regress the behaviour and watch the test go red. A browser test that cannot fail is expensive
silence.

Do not loosen a UI assertion until it tests nothing. A whole-page render contains every surface at
once. A bare `.Contains "swift-heron"` passes on the ROSTER while the element under test still
prints a raw id. Scope the assertion to the element, or to the phrase around it. Then regress the
behaviour and watch it go red. Decoupling from incidental copy helps only while the test still fails
for the right reason. A test loosened into vacuity is worse than the brittle one it replaced,
because it reads as coverage.

### End to end against the real binaries

The browser tier IS the end-to-end harness. `startHost` (`Browser.fs`) runs the real Manager with
`--auth localhost`, which spawns the real session process. A wiring bug in `SessionMain` shows in
that tier and in no cheaper tier. Do not build a second launcher beside it.

Keep a case off the network. Two seams that the product already reads do it. Pass them as the
case's `env`:

- `YESSION_GITHUB_API_URL` points the GitHub calls of the session at a loopback listener that the
  case runs.
- `YESSION_BIN_GIT` replaces the git of the session with a wrapper. The wrapper can hold a clone
  open, or clone a local fixture.
  - The session sets `GIT_ALLOW_PROTOCOL=https`, and sets auth headers through `GIT_CONFIG_*`.
    Clear both, but only for the fixture's own clone.
  - Find helpers like `sleep` on `PATH`. A Nix box has no `/bin/sleep`.

The case "an added repo is in the transcript while its clone is still running" uses both. Copy it.

The same command runs it in a yession `dev` sandbox and in a Claude Code container. In the
container, run `bash .claude/setup.sh` first, and put `devenv shell --` in front of the command:

```
check Browser Native --only "<part of a test name>"
```

### Browser cases run three at a time

The .NET CLR run takes three cases at once (`BrowserRunner.fs`). Node still runs one at a time. So
a browser case:

- Owns what it arranges: its data dir, its ports (`--port 0`, `listenOnLoopback`), its contexts
  (`withContexts`). A case that uses a fixed path, or keeps a host in a module-level mutable,
  shares it with whatever runs beside it.
- Shares only inside `testSequenced`. Where the cases of a suite must share something, sequence the
  suite, and say beside it what they share. Its cases then run one at a time, in order, beside other
  cases.
- Presses through a locator, never at a point calculated from a box.
  - `Locator.ClickAsync` with a `Position` (a place IN the element) brings the element onto the
    screen and waits for it to stand still. It refuses when something else would take the press.
  - Where a case must know the point (a press it hit-tests afterwards), a trial click
    (`Trial = true`) settles the element first. Measure the point after the trial click.
  - A drag starts with a hover through the locator, at a `Position`.
- Gets the harness page it is about.
  - `editorCase` gets the page of the shell. There the shell is the whole document and nothing can
    scroll it. So its boxes stand where the case measures them, whatever ran first.
  - `fixtureCase` gets the page of the fixtures: the editor, the players and the two peers in the
    flow of the page, above the shell.
  - `tests/browser/editor-harness.html` says why there are two pages. With the fixtures above the
    shell on every page, the shell stood wherever the retries of Playwright scrolled the window.
    Every case that measured near its foot flaked.
- Never decides a press on a read taken just before it (`Read.count`, then a click if it found
  one). The page can change between the two. A render owed to the end of a frame removes the
  control, and the click waits its whole timeout for nothing. Scope every selector to the subject
  of the case. Wait for the end state. When a press can find its control gone, let it check the end
  it was for.

The lines of a case print together with its verdict. Each case also prints `▶ <name>` when it
starts. So a case that hung has a `▶` line and no verdict.

To run one case at a time, set `YESSION_TEST_WORKERS=1`. Use it to read a run from top to bottom,
or to tell a flake from load:

```
YESSION_TEST_WORKERS=1 check Browser Native --only "<part of a test name>"
```
