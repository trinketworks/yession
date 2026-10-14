Rules for the test suite: its capabilities, running a tier this box cannot host, and writing tests.

## Capabilities

`Need` in `tests/Yession.Tests/Tags.fs` declares each capability and what it needs. `requireCapabilities`
in `tasks.fsx` probes each one and holds the refusal text. Read those, not a copy here.

- Asking for a capability requires it. `check` errors on one the box cannot host. Never drop one
  quietly. A quiet drop once shipped releases with the live agent suite skipped.
- Two things still skip: a capability the tier did not ask for, and the runtime partition (a Node suite
  on the CLR, or the reverse).
- `verify`'s tiers live in `.github/verify-tiers.json`.
- `LiveGitHub`: in a Claude Code container `GITHUB_TOKEN` is a placeholder. The agent proxy swaps in the
  real credential, and only through `HTTPS_PROXY`. It authenticates EVERY request to GitHub, so no
  anonymous case can run there.
- `Dogfood` on macOS with Colima: keep fixtures under `$HOME`. Colima shares only `$HOME` into its VM,
  so a bind source elsewhere mounts empty.

### Sandbox and Srt

- `Sandbox`: the suite runs a real command in the work sandbox, which is srt by default. Tag every
  such suite `Sandbox`. On a box without a sandbox, an untagged one fails at once, quoting srt.
- `Srt`: confinement is under test (the escape probes). `check Srt` refuses
  `YESSION_NESTED_SANDBOX=weak`, and a granted `Srt` also grants `Sandbox`.
- An unprivileged container (Docker's default) fails the strict probe. There,
  `YESSION_NESTED_SANDBOX=weak check Browser Ports Native Sandbox` runs the PR tier without the
  probes, and strict is CI's job: `pr.yaml` clears `kernel.apparmor_restrict_unprivileged_userns`.
- Weaker confinement in production is the operator's decision. Never use it to get a passing session
  here.
- On a Mac, `vm-check <caps>` runs under STRICT nesting in the Colima VM, in seconds:
  `vm-check Srt --only "<text>"`.

## Running a tier this box cannot host

`Docker` and `LiveAgent` need a daemon and a real model credential. No pull request runs either. Do not
develop against them by merging and watching the release gate. A red master stops every release.

Dispatch the gate against the branch instead. `release.yml` calls the same `verify.yml`, so a green run
here means what a green master means:

```
gh workflow run verify.yml --ref <branch>                      # the whole gate
gh workflow run verify.yml --ref <branch> \
  -f capabilities="LiveAgent Ports Native Srt"                 # one tier
gh workflow run verify.yml --ref <branch> \
  -f capabilities="LiveAgent Ports Native Srt" -f only="add_repo"   # one tier, one case
gh run watch "$(gh run list --workflow=verify.yml -L1 --json databaseId -q '.[0].databaseId')"
```

The Actions tab (verify, Run workflow) takes the same two fields.

Use it when a suite you write needs a capability this box lacks, or a release run failed inside one.
Narrow it with `only`. That turns a 9-minute gate into one question. It also keeps a live case inside
the Node suite's shared budget (`nodeBudgetMs` in `tasks.fsx`: a base plus an allowance per
capability). A long per-case deadline otherwise blows that budget for every suite at once.

- GitHub offers `workflow_dispatch` only for workflows on the default branch. Land a new dispatchable
  workflow on its own, ahead of whatever needs it.
- The credential is a repository secret. A run dispatched from a fork gets none, so the tier fails. It
  does not skip, by design.

## Writing tests

A test pins one invariant and goes red only when that invariant breaks. It keeps two promises: what it
PROVES and what its red MEANS. A suite whose red can also mean "something moved" gets re-run, not read.

One invariant is one arrangement, one action and one assertion of the consequence, under a name that
says which invariant.

- A case with three assertions fails as one, and its red names none of them. Each revision of one
  behaviour also drags in the two nobody touched.
- Split it. Three cases cost three names and buy three verdicts. Hoist the shared SETUP into a helper.
- A call that succeeded and returned the right body is one invariant seen from two angles. An open
  terminal that reads back, a closed one that still does, and a shell that refuses are three tests.

So that red means what it says:

- Assert observable behavior and contracts. Do not assert private state, call order, exact log text or
  incidental DOM structure. A refactor that keeps behavior must not break a green test.
- Be deterministic. No real-time sleeps, no ordering luck, no reliance on anything a declared
  capability does not provide. A flaky test is worse than none. It trains everyone to ignore red.
- Write a manual check of interesting behavior (a bug fix, a protocol edge, a rendering quirk) down as
  a lasting test. The manual run proves it once. Verify-once throwaways stay out.
- Tag each suite with the MINIMUM capabilities it truly needs. It then runs in the cheapest tier that
  can host it and skips, never errors, everywhere else.

### Make a failure say why

A failure that cannot say why is a failure you pay for repeatedly. The browser tier fails only by a wait
that never settles, so its red names the wait and never the fault. Three unrelated defects in one
feature once printed the same thirty-second timeout, and the page had said which was which. Instrument
the boundary, and instrument it to STAY:

- Fixtures keep what the thing under test said (console, page errors, failed requests) and print it
  when a case fails. See `watching` and `reporting` in `tests/Yession.Tests/Browser.fs`. It costs
  nothing on green. It is the only way to read a red run in CI, where nothing attaches afterwards.
- Make anything that decides silently say so. A service worker that chooses between the network and a
  kept copy leaves no trace. One `console.debug` at that branch lets you read the answer.
- Throwaway instrumentation is a smell. If a probe answers a question someone could ask again, put it
  in the fixture, not in a scratch file.

Make looking cheap before you run the gate again to test a hypothesis. When looking is expensive,
guessing and verifying cost the same, so guessing wins and loses overall. `check --only "<part of a test
name>"` narrows both runtimes.

### UI tests pin an invariant, never a design

A rendered surface is the most-revised thing in the product. The next person to improve it deletes a
test that asserts what it LOOKS like. Until then its red says "your redesign is wrong" when it means
"something moved". Test only what holds however the screen is redrawn:

- Availability. A destructive control is not offered over nothing. A network fault never removes the
  composer. An action a person may take is reachable.
- Identity and attribution. One person wears one name on every surface at once. An author is never a raw
  id. A thing that was said is attributed to whoever said it.
- The accessibility floor: keyboard operation, accessible names, held contrast. Pin it ONCE and
  centrally, in the "Chrome consistency" and "Theme contrast" suites. Never re-assert it per surface.

Do not test any of these:

- which element marks an empty state
- what a style token's Tailwind string contains
- whether a control recedes by border or by opacity
- the presence of a decorative mark
- the copy of a label that is not itself a promise

Those are the design changing, which is what a design is FOR.

Stop if the assertion quotes a class name, or would still pass if the surface rendered upside down in
the dark. Both mean the test knows how the view is BUILT, not what it PROMISES. Write the promise, or
write nothing.

### Screenshot or test

A screenshot answers *is this any good*: crowding, hierarchy, where the eye lands. That judgement
belongs to the design as it stands today and has no answer that stays true. As an assertion it fills the
suite with tests nobody believes. Go and look (the `ui-exploration` skill drives real screenshots),
decide, and move on without writing anything.

A test answers *is this still true*. Needing a browser to SEE the answer does not decide whether it is
worth pinning. Visibility, focus, stacking, overflow and contrast are promises that only a rendered page
settles. They break silently, because every cheap tier reads markup that still looks right.

Example: a whole-page render contains both mounts of a notice and passes, while a person sees the
message twice. "Whatever is wrong with the connection is on the screen exactly once" is out of reach of
any markup test. When the invariant is real and only a browser can observe it, the test goes in the
browser tier. "I looked once and it was fine" is the state every regression starts from.

### Browser tests

The COUPLING makes a browser test brittle, not the browser. One test failed on its first run because it
counted the words *not connected* and *reconnecting* while the surface said *session stopped*. Assert
what a browser can measure and a redesign does not move:

- Hooks, and how many are VISIBLE. One `data-*` per report, however many mounts it has. Count the mounts
  a person can see, never the words in them.
  - Cheap visibility checks lie. `offsetParent` is null for anything `position: fixed`. A bounding rect
    stays non-zero for an element that an `overflow-hidden` parent clips to nothing. A collapsed nav's
    contents then measure as on screen.
  - Hit-test the element's own centre. Ask whether what is painted there belongs to it.
- Geometry. A fixed bar does not cover the header under it. Nothing overflows the viewport sideways.
  Compare measurements. Never pixel-match a screenshot, because a font tweak fails it.
- Computed values. A focus ring actually paints. A token resolves to the contrast it promises.

Regress the behaviour and watch the test go red. A browser test that cannot fail is expensive silence.

Do not loosen a UI assertion until it tests nothing. A whole-page render contains every surface at once.
A bare `.Contains "swift-heron"` passes on the ROSTER while the element under test still prints a raw id.
Scope the assertion to the element, or to the phrase around it. Then regress the behaviour and watch it
go red. Decoupling from incidental copy helps only while the test still fails for the right reason. A
test loosened into vacuity is worse than the brittle one it replaced, because it reads as coverage.
