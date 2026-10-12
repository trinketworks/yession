# Measure what a person waits for; fail only on a big regression

> Decided 2026-08-21 · Supersedes nothing · Related: [tasks.fsx](../../tasks.fsx) `bench` /
> `bench-guard` / `bench-publish`,
> [tests/Yession.Tests/Bench.fs](../../tests/Yession.Tests/Bench.fs),
> [app/browser/EditorHarness.fs](../../app/browser/EditorHarness.fs) — the two-peer surface,
> [.github/workflows/bench.yml](../../.github/workflows/bench.yml)

## Context

The user experience of a collaborative editor is three latencies and one budget. Everything else is
a proxy for something that a person does not directly feel. Throughput, allocation counts, and
bundle size are examples.

The open question came from #214. y-prosemirror reconciles the entire document back into Yjs on
every caret push, because `view.update` is not gated on `docChanged`. Frame pacing made that happen
more often. This is a COMPLEXITY worry. A measurement at one document size cannot show a complexity
regression at all.

## Decision

Measure four numbers, swept across three document sizes, in a real browser on the two-peer harness.
Record them on an orphan `bench` branch and chart them onto every release:

| Metric | The question a person is asking |
|---|---|
| `type` | Does typing feel instant? |
| `receive` | How fast do I see my collaborator? |
| `caret.push` | What does drawing their caret cost? |
| `caret.paint` | Does their cursor track? |

One metric is derived: **`caret.push.slope`** = cost(20k chars) ÷ cost(200 chars).

A **pull request** fails when a p50 exceeds `max(3× baseline, baseline + 2ms)`, or 2× for the slope,
and a second measurement confirms it. A **release** records and charts and never blocks.

### The size sweep is the whole design

The slope turns four latencies into an answer. It answered the open question immediately, and not
the way the worry assumed. At 200 / 2,000 / 20,000 characters, the caret push costs 0.30 / 0.30 /
0.40ms. The slope is about 1.3, not 100. y-prosemirror's `updateYFragment` identity-matches
unchanged subtrees through its mapping, so a hundredfold larger document costs a third more, not a
hundred times more. The cost is real, sublinear, and about 2% of a frame. It is worth watching. It
is not worth the redesign that it nearly got.

### The rules of the guard

[`tasks.fsx`](../../tasks.fsx) states why the thresholds are this loose. The rules:

- **The baseline is the median of the last ten recorded points**, not a number in a file. The median
  keeps one noisy release from moving it. Recording keeps it true to reality. Under three points the
  guard only records, because a guard with nothing to compare against must not invent something.
- **p50, not p95.** The tail is where the noise of a runner lives. The chart shows p95. The guard
  does not enforce it.
- **An additive floor of 2ms**, because 3× of a sub-millisecond metric is noise. We calibrated it
  against measured jitter: `caret.push` varies about 0.1ms between runs.
- **Re-measure once before failing.** The dominant flake mode is a single unlucky sample, and the
  scenario costs seconds.

### Why the pull request fails and the release does not

The person who caused a regression should catch it on their own pull request. A red is cheap and
actionable there, and the diff that caused it is right there. A red master stops everybody else
releasing, and a benchmark is never a good enough reason to hold delivery. A regression that passed
the pull-request guard is a chart to read, not a release to block.
[`bench.yml`](../../.github/workflows/bench.yml) states this split.

`continue-on-error` cannot go on a job that `uses:` a reusable workflow. The tolerance therefore
lives on the steps inside `bench.yml`, keyed off whether anything is being guarded.

### Why the history is an orphan branch

The history must live somewhere that every run can read and that one run per release can append to.
An orphan branch shares no history with master and triggers nothing. It also keeps a full git log of
every measurement ever taken.

The recorded shape is the **`customSmallerIsBetter`** array of github-action-benchmark, so adopting
that tool later is a drop-in and not a migration. `Bench.fs` states this.

## Alternatives rejected

- **The Event Timing API.** `PerformanceEventTiming.duration` is rounded to 8ms for privacy, which
  is useless for watching a trend. `keydown.timeStamp` shares the time origin and the resolution of
  `performance.now()`, and it includes the dispatch of the browser itself. The comment on the
  keystroke timer in `EditorHarness.fs` states this.
- **A millisecond budget that fails any slow run.** On a shared CI runner, such a test goes red for
  reasons nobody changed. Everyone learns to re-run it, and it stops being read. A 3× threshold is
  far outside anything that scheduling noise produces.
- **A floor of 8ms** (half a frame). Every metric here is smaller than 8ms, so none could trip. A
  floor above the thing it protects is an off switch.
- **The history as a file on master.** It would be a commit that everybody rebases past, and a push
  to master triggers `release.yml` (`branches: [master]`) and releases again.
- **A chart library.** The chart is hand-authored SVG in `tasks.fsx`. It adds no dependency, and one
  `prefers-color-scheme` block serves both GitHub themes. The chart section of `tasks.fsx` states
  the reasons.

## Consequences

The guard stops only a big regression. A smaller one reaches the chart, and the chart is for
reading.

### What would change this decision

- **The floor stops fitting.** These metrics are 0.3–17ms today. If the numbers of the product move
  an order of magnitude, 2ms is noise or an off switch again. Recalibrate against measured jitter,
  not against intuition.
- **A metric stops being able to move.** `caret.paint` is pinned at one frame. That is good news. It
  also means the metric only ever reports a regression, never an improvement. If pacing changes, ask
  whether it can still say something.
- **The guard goes red for reasons nobody changed.** The thresholds are then inside the noise floor
  of the current CI runner. The answer is a wider threshold or a quieter runner — never a re-run
  habit.
- **Somebody wants an interactive chart.** The stored shape is already the one of
  github-action-benchmark. That is the door, and we left it open deliberately.
