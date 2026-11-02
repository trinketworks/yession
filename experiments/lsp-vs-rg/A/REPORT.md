# LSP vs rg for agent navigation in yession: replicate A

## Question

When an agent has a warm F# language server (fsautocomplete, FSAC), does it find symbols faster or
more accurately than with ripgrep plus the AGENTS.md "Finding F# symbols" rule? The
[2026-07-25 decision](../../../docs/decisions/2026-07-25-lsp.md) measured raw query latency once and
dropped FSAC. This experiment measures what an agent achieves on a task.

## Summary

- **Accuracy.** Both arms were nearly perfect. RG answered 46/48 correctly and LSP answered
  48/48. The difference is +4 points with a 95% CI of [0, +10]. McNemar exact p = 0.50 (2
  discordant pairs, both favouring LSP). The experiment cannot show an accuracy difference. Both
  RG errors were on the same REFERENCES task and had the same cause (see failure modes).
- **Time.** With LSP available, the agent took **0.76× as long** (95% CI 0.65–0.89; pairs where
  both arms were correct, n = 46). That is about a 24% reduction. The median trial took 16 s with
  RG and 11 s with LSP. The CI excludes 1.
- **By class.** The gain is all in REFERENCES (0.57×, CI 0.45–0.72) and TYPE-DRIVEN (0.64×, CI
  0.52–0.80). DEFINITION showed no gain: 1.17× (CI 0.95–1.46), with the point estimate slightly
  *slower*.
- **Tool calls.** The LSP arm made 0.71× as many tool calls (CI 0.64–0.80).
- **Tokens.** The LSP arm used 0.95× as many tokens (CI 0.93–0.96), about 3k tokens fewer per
  trial against a baseline of about 51k. About 50k of that baseline is the fixed system prompt
  and preamble. This differs in sign from the prior-art paper, which found +6–19% for LSP.
- **Usage.** Every LSP-arm subject used the LSP tool: 48/48 trials, in all three classes. On
  localization the prior-art paper saw 0–6%, so a single, clearly described CLI makes a large
  difference to uptake.
- **Cost.** One-time and per session: 45 s until the first definition answers, 129 s until the
  first `references` answers, and 3.4 GB resident after the run. The repo is now about 214k lines
  in 443 .fs files. The decision doc measured about 19k lines.

## Design (as run)

- **Subjects.** Fresh agents from the smaller, faster model tier (one per trial), general-purpose
  type, no shared context. Each prompt held a shared preamble, one tool paragraph per condition,
  one question, and a required `ANSWER:` line. Both arms received the AGENTS.md rule verbatim.
  The LSP arm also received a warm FSAC CLI (`definition`, `references`, `hover`, `symbols`) and
  was told that rg was still allowed.
- **Tasks** (`tasks.json`). 12 tasks, 4 per class, all built on overloaded names:
  - DEFINITION: `Clock`, `State`, `Runner` (a local abbreviation that shadows a generic) and
    `Pending<_>` (a field and a type on the same line).
  - REFERENCES: the set of files that reference one type, for `Clock`, `Box`, `Runner<_,_>` and
    `Client.Connection`. Each type shares its name with a module, record fields or other types.
  - TYPE-DRIVEN: the inferred type of an unannotated `let`.
- **Ground truth.** Each answer came from FSAC (`verify_truth.mjs`, re-run after the trials: 12/12
  match). I cross-checked each one with rg and by reading the code. For example, `Bench.fs` uses
  only the `Box` *module*, and `Sync.fs` uses only the *alias* `Body.Runner`.
- **Grading.** `grade.mjs` grades by script:
  - exact `path:line` for DEFINITION,
  - set equality for REFERENCES,
  - a normalised type string for TYPE-DRIVEN, so `string list`, `list<string>` and qualified
    names compare equal.
- **Design structure.** A randomized complete block design. Each block holds 12 tasks × 2 arms.
  The order is shuffled with seed `20261102 + block`, and `schedule/block*.json` keeps each
  block's order and prompts. Trials ran in waves of 12 concurrent agents, in the shuffled order,
  so concurrency load fell on both arms equally.
- **Measures.**
  - Wall-clock seconds and tool-use counts are the harness's `duration_ms` and `tool_uses` for
    each subagent. `tool_uses` includes the final hand-back call, so the floor is 2.
  - Tokens are the harness's `subagent_tokens` total. A per-direction breakdown (input, output,
    cache) was not visible.
  - LSP use comes from the daemon's call log. Each LSP trial got its own wrapper path, which
    attributes calls to the trial (`lsp-calls.jsonl`).
- **Infrastructure** (`lsp-daemon.mjs`, `lsp-query.mjs`).
  - The `LanguageServer` class from PR #38's bridge (bb197dd) is copied verbatim, with the
    responses-keyed-on-absent-`method` dispatcher.
  - It is served over localhost HTTP in place of MCP, so subjects call it from Bash.
  - One change: an error saying "No check results" on a file opened for the first time is now
    retried the way a null answer is. Without it, the first query on a fresh file failed.
  - FSAC 0.83.0 is built from the nixpkgs pin in `devenv.lock`. It runs with
    `.devenv/profile/bin` (dotnet SDK 10) on PATH.
  - The daemon stayed up for all 96 trials and never restarted.

## Sequential-stopping trace (pre-registered rule)

Stop when (a) the CI for the time ratio and (b) the CI for the accuracy difference both exclude
0, or when the width of the time CI is below 0.2 log units. The hard cap is 4 blocks.

| after block | log time-ratio CI | width | accuracy-diff CI | decision |
|---|---|---|---|---|
| 1 | [−0.680, −0.009] | 0.671 | [0, 0] | continue |
| 2 | [−0.469, −0.034] | 0.436 | [0, 0] | continue |
| 3 | [−0.425, −0.052] | 0.374 | [0, 0.083] | continue |
| 4 | [−0.426, −0.116] | 0.309 | [0, 0.104] | **hard cap reached** |

The time CI excluded 0 from block 1 onwards. The accuracy CI could never exclude 0, because both
arms were at or near ceiling. The pilot showed this (discordance 0), and I told the parent before
continuing. The pilot's power estimate for the time effect was 60 pairs (5 blocks), with a paired
SD of log(s) of 0.617. The final SD is 0.549, which gives 48 pairs, the number actually run.

## Results in full

`results.md` holds the output of `node analyze.mjs`. Paired bootstrap, 10,000 resamples, seeded.

| class | pairs | acc RG | acc LSP | time ratio LSP/RG [95% CI] | median s RG / LSP | tool-call ratio | token ratio |
|---|---|---|---|---|---|---|---|
| ALL | 48 | 0.96 | 1.00 | 0.76 [0.65, 0.89] | 16 / 11 | 0.71 [0.64, 0.80] | 0.95 [0.93, 0.96] |
| DEFINITION | 16 | 1.00 | 1.00 | 1.17 [0.95, 1.46] | 12 / 13 | 1.06 | 1.00 |
| REFERENCES | 16 | 0.88 | 1.00 | 0.57 [0.45, 0.72] | 22 / 11 | 0.62 | 0.87 |
| TYPE-DRIVEN | 16 | 1.00 | 1.00 | 0.64 [0.52, 0.80] | 17 / 9 | 0.55 | 0.97 |

Including the incorrect pairs gives a time ratio of 0.75 [0.64, 0.87].

## Failure modes observed

- **RG on REFERENCES: an alias user counted as a reference.** This happened in 2 of 4 R3 trials.
  - The question was which files reference `Runner<'model,'msg>`. `tests/Yession.Tests/Sync.fs`
    names only the abbreviation `Body.Runner`.
  - One subject added Sync.fs and was wrong. The other kept Support.fs and Peer.fs, then dropped
    Phase3.fs and Resilience.fs. Those two use `Harness.Runner<…>` in a qualified form, and its
    grep missed them.
  - Two more RG subjects flagged the alias ambiguity in prose but answered correctly.
  - This is the "same name, different symbol" problem that the AGENTS.md rule exists for. The
    rule finds *declarations*. It says nothing about how to enumerate *uses* of a symbol whose
    name is shared.
- **RG on TYPE-DRIVEN: correct, but by reading code.** Every RG subject got all 16 type questions
  right. They chased the right-hand side through 1–3 files, for example
  `SandboxDecl.selectionOf` → `Config.fs:134`. Each of the 4 tasks had a declared signature
  within 1–2 hops. Type questions with longer inference chains would separate the arms more.
- **LSP: wrong column on the field-and-type line (D4).** On `Pending : Pending<…>` a symbol name
  resolves to the *field*. Subjects then guessed columns 15 or 16 and got errors, which cost 2–3
  extra calls and 15–20 s. Some fell back to rg. This is the main reason D4 under LSP was slower
  (median 24 s against 12 s). The cause is ergonomic: the column is counted by hand. A
  `symbol#2` (nth occurrence) argument would remove it.
- **LSP on DEFINITION: no gain.** With a qualified or project-scoped name, one declaration-form rg
  finds the answer in a single call, just as one LSP call does. Outside D4, LSP-arm DEFINITION trials used a
  single LSP call (2 tool uses, including the hand-back), the same count as RG.
- **No subject wrote to the repo.** `git status` was clean apart from experiment files. No subject
  ran a build.
- **The harness itself.** My own bare `rg` (without a path) under a non-TTY stdin hung, reading
  stdin. Every prompt therefore told subjects to pass a path. No subject hung.

## Cold start (paid once by me, never charged to a trial)

| step | time |
|---|---|
| nix build of fsautocomplete 0.83.0 (cache hit) | about 1 min, plus setup contention |
| daemon start → `initialize` | 2.3 s |
| → first `definition` answers (89 project-load events) | 45 s |
| → first `references` answers (whole-workspace check) | 129 s |
| resident memory after 96 trials | 3.4 GB (the decision doc measured about 1.3 GB at 19k lines) |

Once warm, `definition` and `hover` took 5–30 ms and `references` took 0.2–0.7 s (daemon log).

## Budget actually spent

- 4 blocks, 96 subject trials, which is the hard cap.
- 5.10 M subagent tokens in total (harness-reported), or about 53k per trial.
- 24 subagent-minutes, 260 tool calls.
- Not visible: the per-trial split into input, output and cache-read tokens, and cost.

## Threats to validity

- **Ceiling.** The tasks were too easy for accuracy to separate the arms. 94/96 trials were
  correct. Only the time and tool-call results are informative. Harder REFERENCES tasks would
  test precision better, for example a widely used type such as `SessionId` (149 references in
  48 files) or functions used without qualification.
- **Small task sample.** There are 4 tasks per class, and I chose them myself. The CIs come from
  resampling (task, block) pairs, and they do not generalise over the population of tasks. One
  task (D4) dominates the DEFINITION result.
- **Timing under concurrency.** Twelve agents shared the API and the box. Randomization balances
  this between the arms, but absolute seconds would be smaller when run one at a time.
- **Tool-use count.** It includes the hand-back call (floor 2). Ratios are therefore compressed
  towards 1.
- **"LSP available" is not "LSP only".** This is deliberate, because it is the deployment
  question. The LSP arm also read more of its preamble.
- **No builds in the RG arm.** The rule's "let `check` tell you" route was forbidden as too slow
  for a single navigation. In real work an RG agent might type-check instead of reading.
- **One model tier.** All subjects came from one model tier, and a stronger model may close the
  gap on REFERENCES.
- **Cold start only partly modelled.** The 45–129 s warm-up and 3.4 GB are reported but not
  traded off per session.

## Should the 2026-07-25 decision change?

Two of the decision doc's "what would change this" conditions now hold:

- `src/` has grown about 10× (19k → 214k lines).
- On type-driven work, the LSP is measurably faster (0.64×). That is "Work that is type-driven
  rather than name-driven, where hover is the only answer." Here hover was not the *only* answer,
  but it was the fast one.

With a warm server, agents are about 24% faster per navigation and use about 30% fewer tool calls
and about 5% fewer tokens. Agents also adopt the tool readily when it is a plain CLI. The gain is
all in references and inferred types. For "where is X declared", the declaration-form rule is
already as good as the LSP.

Against that, the per-session cost is now larger: 129 s to the first references answer and
3.4 GB. Saving about 4 s per navigation, a session needs on the order of 30 references or type
navigations to recover the cold start in wall-clock time. If the warm-up runs in the background
at session start, as PR #38's bridge did, it costs memory but mostly no agent time.

The evidence supports reviving FSAC as an opt-in, background-warmed CLI for reference and type
questions, with the declaration-form rule kept for definitions. It does not show that rg leads
agents to wrong answers often enough to make LSP mandatory. Replicate B should be read alongside
this one before the decision doc is amended.

## Files

- `tasks.json`: the task bank.
- `trials.csv`: raw rows, one per trial.
- `schedule/`: the seeded order and exact prompts for each block.
- `lsp-calls.jsonl`: the daemon call log.
- `stopping-trace.txt` and `results.md`: analysis output.
- `lsp-daemon.mjs` and `lsp-query.mjs`: the FSAC daemon and client.
- `schedule.mjs`, `record.mjs` and `grade.mjs`: the runner and grader.
- `verify_truth.mjs`: the ground-truth check.
- `analyze.mjs`: the analysis.

Reproduce the analysis with `node analyze.mjs`.
