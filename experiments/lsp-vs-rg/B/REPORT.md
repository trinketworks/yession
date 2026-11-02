# Language server vs. ripgrep for agent navigation in F# — replicate B

## Summary

Coding agents answered 12 navigation questions about this repository, with and without a warm
fsautocomplete (FSAC) language server. Each question ran under both conditions in every block.
There were 4 blocks, so 96 trials, which is the pre-registered hard cap.

| | ripgrep only (RG) | ripgrep + warm FSAC (LSP) | Effect, LSP vs RG (95% CI) |
|---|---|---|---|
| Accuracy | 46/48 (95.8%) | 48/48 (100%) | +4.2 points [0, +10.4]; McNemar exact p = 0.50 (0 vs 2 discordant) |
| Wall-clock, median | 18.2 s | 8.3 s | time ratio **0.50** [0.44, 0.58] (both-correct pairs, n = 46) |
| Tool calls, mean | 2.56 | 1.02 | ratio 0.45 [0.39, 0.53] |
| Tokens processed per trial | — | — | ratio 0.58 [0.52, 0.65] |
| Output tokens, mean | 983 | 301 | — |
| LSP tool used | 0/48 (no violations) | 48/48 | — |

The headline: a warm language server halves the time an agent needs for a navigation answer and
cuts tool calls by more than half. It also cuts tokens by about 40%. It costs no accuracy.
Accuracy rose slightly, but the CI includes zero. The only errors were two ripgrep trials on one
reference task. With a ceiling this close to 100%, this design cannot resolve accuracy.

The time effect is large and stable. Its 95% CI excluded zero from block 1 onward. A
task-clustered bootstrap, which resamples tasks rather than pairs, gives a ratio of
[0.42, 0.61], so the result does not depend on one or two tasks. Every task was faster with
LSP, though the size of the gain varies by task (see the per-task table).

Cold start is the cost: **70.6 s** to the first answer and **31 s more** for the first
workspace-wide `references`, with **3.3 GB** resident. On the July 2026 measurement these were
20 s and 1.3 GB.

## Question and prior decision

[docs/decisions/2026-07-25-lsp.md](../../../docs/decisions/2026-07-25-lsp.md) dropped FSAC. It
measured raw query latency: FSAC answers warm queries in 1–3 ms, and rg in about 9 ms with 321
hits to read. It judged the per-session cold start (about 20 s, 1.3 GB) too expensive for the
gain. This experiment measures the agent's task outcome instead: correct or not, wall-clock
time, tool calls and tokens.

## Method

**Subjects.** Each trial spawned one fresh general-purpose sub-agent of a mid-tier model, with
no shared context. Every subject got the same preamble: a read-only question with an exact
answer format, ending in an `ANSWER:` line. The tool paragraph was the only difference:

- **RG**: rg/grep and Read only, the LSP client forbidden, and the AGENTS.md "Finding F#
  symbols" rule quoted verbatim.
- **LSP**: rg/grep and Read allowed, plus a CLI to a warm FSAC daemon (`definition`,
  `references`, `hover`, `symbols`). This arm measures "LSP available", not "LSP forced".

[prompt.py](prompt.py) builds the exact prompts.

**Infrastructure.** [lsp-daemon.mjs](lsp-daemon.mjs) is a long-lived node process that holds
one FSAC 0.83.0 process (nixpkgs, at the devenv.lock pin) on a Unix socket.
[lsp-query.mjs](lsp-query.mjs) is the Bash client that subjects call. The daemon reuses the
LanguageServer class, position handling and tool handlers from PR #38's
`scripts/fsharp-lsp-mcp.js` verbatim. That code includes the response dispatch keyed on the
absence of `method`, and the devenv profile `bin` on PATH, which gives FSAC its `dotnet`. I
added one thing: a retry when FSAC answers "No check results found" for a file it has not
typechecked yet. The first query on `app/Watches.fs` hit that error during setup, and the PR #38
bridge only retried on empty results. The daemon stayed up for all 96 trials and never
restarted.

**Task bank.** [tasks.json](tasks.json) holds 12 tasks, 4 per class, all on names that the
repo overloads:

- **DEFINITION**: give a usage site file:line and ask which declaration the type name there
  resolves to. The names are `Refusal` (4 types and 2 modules), `Node` (8 types, one an
  abbreviation), `Request` (4 types) and `Projection` (type, module and fixtures).
- **REFERENCES**: list the files that reference one specific `Refusal` type (two tasks, for two
  of the four types), the `Projection` type but not its same-named module, and the function
  `Projection.applyEvent` when 8 modules declare an `applyEvent`.
- **TYPE-DRIVEN**: give the inferred type of an unannotated binding or lambda parameter (2
  tasks), or the record type whose field a lambda reads (2 tasks, with fields shared by 9 and
  22 records).

FSAC gave the ground truth. I cross-checked every task with rg and by reading the code. All the
REFERENCES file sets agreed with an rg enumeration of the qualified call or type forms. The
grader is [harvest.py](harvest.py). It scores exact `file:line` for DEFINITION, set equality on
file lists for REFERENCES, and a whitespace-normalized type string with listed equivalents
(`list<T>` = `T list`, qualified names) for TYPE-DRIVEN. The answer file lived outside the
repository while trials ran, so no subject could find it with rg.

**Design.** The design is a randomized complete block design. One block is 12 tasks × 2
conditions, in an order randomized per block from seed `20261102 + block`
([design.py](design.py), schedules in [data/](data/)). The pairing unit is (task, block).
Subjects ran 6–12 at a time in schedule order. Concurrency was the same for both arms within a
wave.

**Outcomes.** [harvest.py](harvest.py) parses each subject's transcript. It records:

- `seconds`: the first prompt to the final hand-back
- tool calls, excluding the hand-back
- `lsp_used`: any Bash command that calls `lsp-query`
- per-message token usage, de-duplicated by message id: uncached input, output, cache read and
  cache write

"Tokens processed" is the sum of all four token counts. It is dominated by cache reads of the
fixed system prompt, which is about 50k tokens per turn.

**Analysis.** [analyze.py](analyze.py) uses the standard library only and 10,000 bootstrap
resamples.

## Pilot and power (block 1, reported to the parent before continuing)

- Paired SD of log(seconds) was 0.615 and the correctness discordance rate was 1/12 = 0.083.
- 80% power at α = .05 to detect a 25% time ratio (|log| = 0.288) needs 36 pairs, which is 3
  blocks.
- 80% power to detect a 15-point accuracy difference by McNemar needs 54 pairs, which is 5
  blocks. That exceeds the 4-block hard cap, so the accuracy comparison was underpowered by
  design, and I said so in the pilot message.

## Sequential stopping trace (pre-registered rule)

The rule was to stop when both the time-ratio CI and the accuracy CI exclude zero, or when the
time-ratio CI is narrower than 0.2 log units, or at 4 blocks.

| Through block | Pairs | Mean log ratio (LSP/RG) | 95% CI | CI width | Acc. diff | Acc. 95% CI | McNemar p | Stop? |
|---|---|---|---|---|---|---|---|---|
| 1 | 12 | −0.655 (×0.52) | [−0.971, −0.342] | 0.63 | +0.083 | [0, 0.25] | 1.0 | no |
| 2 | 24 | −0.717 (×0.49) | [−0.905, −0.528] | 0.38 | +0.042 | [0, 0.125] | 1.0 | no |
| 3 | 36 | −0.705 (×0.49) | [−0.846, −0.564] | 0.28 | +0.028 | [0, 0.083] | 1.0 | no |
| 4 | 48 | −0.686 (×0.50) | [−0.815, −0.549] | 0.27 | +0.042 | [0, 0.104] | 0.5 | hard cap |

The time CI excluded zero at every look. The accuracy CI never did, because LSP never lost a
pair and RG lost only two, so its lower bound sat at 0. The run stopped at the hard cap.

## Per-class breakdown (block-paired, 16 pairs per class)

| Class | Acc. RG | Acc. LSP | Time ratio (95% CI) | Task-clustered CI | Calls ratio | Tokens ratio |
|---|---|---|---|---|---|---|
| DEFINITION | 16/16 | 16/16 | 0.62 [0.50, 0.79] | [0.47, 0.84] | 0.59 | 0.74 |
| REFERENCES | 14/16 | 16/16 | 0.42 [0.35, 0.53] | [0.34, 0.54] | 0.39 | 0.49 |
| TYPE-DRIVEN | 16/16 | 16/16 | 0.48 [0.39, 0.58] | [0.33, 0.65] | 0.40 | 0.54 |

Per task, median seconds RG → LSP:

| Task | RG | LSP | Task | RG | LSP | Task | RG | LSP |
|---|---|---|---|---|---|---|---|---|
| D1 | 18.0 | 8.2 | R1 | 28.0 | 10.6 | T1 | 25.6 | 7.0 |
| D2 | 12.9 | 11.5 | R2 | 21.3 | 7.6 | T2 | 14.4 | 8.0 |
| D3 | 19.0 | 10.8 | R3 | 17.5 | 8.3 | T3 | 16.3 | 7.7 |
| D4 | 12.5 | 7.3 | R4 | 23.5 | 9.6 | T4 | 15.6 | 8.9 |

REFERENCES gains the most, as the prior art predicts. DEFINITION gains the least: when the
usage file's `open`s point at one candidate, a declaration-form grep plus one read is almost as
fast. TYPE-DRIVEN questions were answerable without the server. RG subjects reached 16/16 by
tracing signatures through 2–6 tool calls, but they took twice as long.

## LSP usage in the LSP arm

Subjects used the language server in **48/48** LSP trials, and in 100% of every class. 47 of the
48 trials answered with exactly one tool call: one `definition`, `references` or `hover` query.
No subject used `symbols`. The RG arm had zero violations.

The prior art saw a 0–6% usage rate on localization. Usage here is much higher because every
question already names the file and line. That is exactly the input a position-based LSP query
needs (see threats).

Warm query latency from subjects' 48 calls: median 14.5 ms, p90 390 ms, max 932 ms.

## Cold start (paid once, not charged to any trial)

I measured on an idle 4-core box after the dev environment was built
([cold-start.sh](cold-start.sh)):

| Step | This run | July 2026 decision doc |
|---|---|---|
| Daemon start → first `definition` answer | 70.6 s | ~20 s |
| First workspace-wide `references` | +30.9 s | ~12 s |
| Warm `references` repeat | 0.44 s | ~80 ms |
| FSAC resident memory | 3.3 GB | ~1.3 GB |

A first attempt that overlapped the repo's own `build` took 88 s. FSAC also needs the restored
packages from `devenv shell -- build`. Before restore, no projects have `obj/`.

The repository is not the one the decision measured. It now has 443 `.fs` files and about 214k
lines including tests and examples. The decision doc reported 105 files and about 19k lines.

## Failure modes observed

- **RG, REFERENCES, truncated search output.** Both errors were on R4, `Projection.applyEvent`,
  where 8 modules declare an `applyEvent`. Both subjects returned 5 of the 7 files, with
  precision 1.0 and recall 0.71. Both missed `src/Yession.Session/Scheduler.fs` and
  `tests/Yession.Tests/Terminals.fs`. In b4-15 the subject's first search was a bare-name
  `rg 'applyEvent' … | head -100`, and the missing files fell past the cut. The subject then
  filtered by excluding other module names rather than including `Projection.`. This is the
  "321 hits is skimmable" assumption failing once an agent pipes output through `head`.
- **RG, inferred types.** No errors, but T1 took 4–6 calls each time: the subject followed
  `log.Read` → `ReadEvents<'event>` → `EventPage<'event>.Events` → `EventEnvelope.Event`.
- **LSP.** No errors. One latent hazard was not triggered: `symbols` reports the line where an
  item's doc comment starts, not its `type` line. For example, it reports
  `tests/Yession.Tests/BrowserWait.fs:116` where the type is on line 121. A DEFINITION answer
  read off `symbols` would be graded wrong. No subject used `symbols`.
- **Experimenter error, caught and excluded.** My first harvest raced the subjects. A text grep
  for the hand-back tool name also matched the system reminder in every transcript, and graded
  5 unfinished trials as empty. [pending.py](pending.py) now checks for a real hand-back
  tool_use, and every number above comes from the re-harvest after all 96 finished. A stray
  file that an experimenter command (not a subject) created in the repo root was deleted.
  `git status` showed no subject writes.

## Threats to validity

- **Questions give the position.** Every prompt names file:line, which is what `definition`,
  `references` and `hover` take. Real work often starts from a name or a behaviour, where an
  agent must localize first. There the prior art saw LSP barely used. This experiment says
  nothing about localization.
- **Single-hop tasks.** Each answer is one lookup. A real change takes many lookups, interleaved
  with reading and editing. Per-lookup savings of about 10 s and about 1.5 calls compound
  across those lookups, but I did not measure that.
- **Tool advertisement.** The LSP paragraph names the server "compiler-accurate" and lists its
  commands. That probably raised the usage rate.
- **Shared context.** Both arms also loaded the repo's CLAUDE.md, which carries the same
  declaration-form rule. That rule therefore reached LSP subjects too.
- **Ground truth from the treatment.** Ground truth for REFERENCES comes from the LSP being
  evaluated. I mitigated this by cross-checking every file set with rg and by reading the code.
  Tasks were also chosen where FSAC and the qualified-name enumeration agree. FSAC's notion of
  "reference" can differ from a human's at the edges, for example `{ Refusal.Reason = … }`
  record-label qualifiers. I avoided those edges rather than resolving them.
- **Accuracy ceiling.** RG was 95.8% correct, so these tasks do not stress accuracy. The bank
  has 12 tasks, and per-class results rest on 4 tasks each. The task-clustered CIs are the
  honest ones for generalization.
- **Wall-clock includes model-API latency and concurrency.** Up to 12 subjects ran at once.
  Both arms were interleaved within waves, so this adds noise, not bias. One LSP trial took
  23 s with a single call, which is API latency.
- **One replicate, one container, one subject model.** Replicate A ran the same protocol
  independently. Compare the two before generalizing.

## Budget actually spent

- **Subjects**: 96 trials, plus 1 format probe before the experiment, about 54k tokens and not
  in the CSV.
- **Tokens processed by subjects**: 13.78 M total, made up of:
  - 12.76 M cache reads
  - 0.96 M cache writes
  - 61.7 k output
  - 536 uncached input
- **Per arm**: RG used 8.90 M tokens and LSP used 4.88 M.
- **Not counted**: the orchestrating session's own tokens were not measured. The transcripts
  expose no per-trial cost in currency.

## Should the 2026-07-25 decision change?

The decision rested on two claims. The first was that per-session cold start (20 s, 1.3 GB)
outweighs the gain until "a few dozen navigations per session". The second was that 321-hit
results are skimmable.

- **On the gain:** an available warm server saves about 9 s, about 1.5 tool calls and about
  40% of tokens per navigation answer at this repo size. It never cost accuracy. On reference
  questions it removed the only errors seen, which came from an agent truncating a large rg
  result.
- **On the cost:** cold start is now about 100 s and 3.3 GB. That would break even after about
  11 navigations if paid on the critical path. If the daemon warms in the background at session
  start, as the PR #38 bridge already did, the time cost disappears from the agent's path. Only
  memory remains.
- **On the decision doc's own trigger:** its "What would change this decision" says "`src/`
  growing several times over". The repository the decision measured has grown about 11× by line
  count.

A reader who accepts the threats above can conclude that the decision's premise no longer holds
at this size. The open questions are memory (3.3 GB) and whether localization-style tasks would
use the server at all. A follow-up should use name-only prompts with no file:line given, and
multi-step change tasks.

## Reproducing

From the repo root, with the devenv profile built:

```
FSAC=<fsautocomplete> FSAC_PATH_PREFIX=$PWD/.devenv/profile/bin node experiments/lsp-vs-rg/B/lsp-daemon.mjs &
EXP_DATA=<dir outside the repo holding tasks.json> python3 -I experiments/lsp-vs-rg/B/design.py <block>
python3 -I experiments/lsp-vs-rg/B/prompt.py <task> <RG|LSP>   # spawn one fresh subject per trial; append to runs.jsonl
python3 -I experiments/lsp-vs-rg/B/harvest.py <transcript dir>  # writes trials.csv
python3 -I experiments/lsp-vs-rg/B/analyze.py                   # writes the numbers above as JSON
```

Keep `tasks.json` outside the repository while subjects run. It holds the answers.
