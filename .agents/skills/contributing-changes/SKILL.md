---
name: contributing-changes
description: How a change gets from asked-for to merged and green. Split the request into independently-shippable PRs. Compare what you built to what was asked. Open a PR with auto-merge, watch CI, and drive to a green master. Read at the START of any request to build, fix, refactor or change something, before the first edit, because the split cannot be decided later. Read again when an implementation is done and before opening a PR. Read again after any CI failure on a PR you opened, because a fix repeats this process.
---

# Contributing changes

A change is not contributed until it is merged and the master pipeline is green. The process
starts before the first edit, because the split into PRs is decided there. This skill defines how
to cut up a request, the gate for proceeding autonomously, and the loop that drives each piece
through.

Write no plan document. Say the shape of the work to the user and build it (AGENTS.md, "Plans are
not artifacts", has the reason).

## Step 0: Split the request before writing any code

Split the request as stated, first. Most requests worth making are several changes in one
sentence. The split is cheap only now. Once the code exists, separating it takes branch surgery,
so nobody does it.

Not splitting costs rebase tax, and the tax compounds. The merge queue pays the mechanical half: a
green check no longer goes stale because master moved. It does not pay conflict reconciliation.
Five PRs landed under Plan 17's single four-step branch. Reconciling the branch against them took
longer than writing all four features, though each step would have merged in minutes on its own.
The tax is superlinear: twice the branch against twice the drift is four times the conflict.

Cut the request into the smallest pieces that each stand alone. Ship each as its own PR. Finish the
first, run this process end to end, then start the second from the merged master.

- **A piece stands alone when it builds, `check` passes, and it breaks nothing merged.** The
  feature it belongs to may be incomplete. These are all mergeable: a capability behind an unset
  flag, a domain type nothing calls yet, an engine with no provider on top.
- **Keep pieces together only when splitting would land something broken.** Examples: a protocol
  change and the only caller that speaks it, a rename and its call sites.
- **Bump the version once, on the piece that earns it.** Four PRs is still one feature. Put the
  marker on the commit that moves the contract, not on each slice.
- **Say the split before you start**, in a sentence or two: what lands first, what follows, and
  what each is good for alone. State it and proceed. It is a routine judgement call, not a
  question. A split that DROPS or defers something the user asked for is a scope change. It goes
  through the Step 1 gate: say so and wait.

**Run independent pieces in parallel.** When pieces touch disjoint code with no ordering between
them, put each on its own branch off master. Enable auto-merge on all of them at once. The queue
re-tests each against real master, so none waits for another. Keep a strict order only where a
piece would break without another already in. Examples: a rule and the sites it would reject, a
protocol change and its only caller, an analyzer and the tree it judges. Serialising independent
PRs behind one branch buys nothing the queue does not give, and costs a merge cycle apiece.

If you find several finished pieces on one branch anyway, do not bundle them out of sympathy for
the work done. Open the PR for what is there, and split the NEXT request properly.

## Merge semantics: the queue

`master` merges through a **merge queue**, not directly. Squash is the only method.

- **A moving master costs nothing.** Branches need not be up to date. A green check does not go
  stale when someone else lands first, because the queue re-tests your change against real master.
  Rebase only for a genuine conflict.
- **Enabling auto-merge enqueues. It does not merge.** The queue builds base+PR on a temporary ref
  and runs `test` there through `pr.yaml`'s `merge_group` trigger.
- **Never infer merged-ness from a check conclusion, in either direction.** Grouping is
  `HEADGREEN`, so a batch can merge on its LAST entry's checks and carry a red one in with it. An
  entry that fails, or never reports inside 60 minutes, drops from the queue, and its PR quietly
  returns to open with auto-merge off. Read
  `gh pr view <n> --json state,mergedAt,mergeStateStatus` and believe that instead.
- **A stuck PR has a reason, and `mergeStateStatus` names it. Read it before you act.** A PR that
  sits queued without merging is not automatically an ejection to re-enqueue. Check the status:
  - `DIRTY` (or `CONFLICTING`): a real conflict with master. Rebase onto origin/master and
    force-push. You can do this yourself. Re-enqueueing a DIRTY PR never lands it.
  - `BEHIND`: the queue brings it up to date for you. Leave it.
  - `BLOCKED`: a required check or review is still owed. Find it.
  - `CLEAN`/`UNSTABLE` on a PR that is OUT of the queue: the ejection case below.

  The trap: a queued PR that went DIRTY looks identical to one still on its way in. One sat
  conflicted-but-queued for two days, because its watcher read "queued" as "will land".
- **Silence is not progress.** A dropped entry produces no failure event. If a PR has neither
  merged nor failed by your next check-in, and its `mergeStateStatus` is not DIRTY/BEHIND/BLOCKED
  per above, assume ejection and re-enqueue.
- **`autoMergeRequest` reads `null` while a PR is IN the queue.** It does not prove the PR left it.
  Read the queue: `gh api graphql -f query='{ repository(owner:"trinketworks",
  name:"yession"){ mergeQueue(branch:"master"){ entries(first:10){ nodes{
  pullRequest{number} state position } } } } }'`. An entry whose run failed sticks as
  `UNMERGEABLE`, and `gh pr merge --auto` answers "already queued". Clear it with the
  `dequeuePullRequest` then `enqueuePullRequest` mutations on the PR's node id.
- **One push can carry several PRs.** So the `release` run in Step 4 may be a batch head that
  contains your commit, not a run named for it.
- **A stacked PR (B branched from A, both targeting master) goes DIRTY when the one under it
  squash-merges.** B's history holds A's original commit, which conflicts with A's squash. Run
  `git rebase --onto origin/master <A-sha>` and force-push. Arm B's auto-merge only after A lands,
  or B's squash carries A's diff. If B already entered the queue, its branch is locked (push
  refused, GH006) and it merges as-is. Leave it.
- **PR CI checks out master+PR.** A branch that builds locally can fail CI on a file master added
  after you branched. Run `git fetch` and rebase before diagnosing.

## Step 1: Compare what you built to what was asked

Re-read the request: the user's own words, plus the shape you told them in Step 0. Diff it against
what exists. Look for **interesting deviations**, anything the person who asked would be surprised
by:

- Approach changed (different design, files, or mechanism than you said)
- Scope changed (asked-for items dropped, unasked-for behaviour added)
- Blockers hit, or work that turned out uncompletable
- Public behaviour differs from what was promised

NOT interesting (proceed without asking): mechanical differences. Examples: renames, small
refactors the code forced, a correction of a mistaken assumption in the request, test scaffolding
nobody enumerated in advance.

**If there are interesting deviations, blockers, or uncompletable work: STOP.** Report them to the
user and wait. Do not open a PR. The user decides whether the deviation is acceptable. Autonomous
integration is only for work that matches what was asked for.

If the work is consistent, proceed. No need to ask.

## Step 2: Run the checks locally, then open the PR

1. Run the checks this box can run before you push, at minimum `check` inside devenv (AGENTS.md,
   Testing, lists the capability tiers). Do not leave the first failure to CI.
2. Commit on the session's designated feature branch. Push with `git push -u origin <branch>`.
3. Open a PR against `master` (`mcp__github__create_pull_request`). Its description becomes the
   squashed commit body, so write it as one. Say what this piece does and, where it is one of
   several, what it stands alone as and what follows it. A `+semver:` marker, if the change earns
   one, goes here on a line of its own. Branch commit messages are discarded at merge and never
   reach master. Wrap the description at 72 columns. GitHub re-wraps anything wider.
4. Enable auto-merge: `mcp__github__enable_pr_auto_merge` (squash). Under the queue this enqueues
   the PR when it is otherwise ready (see Merge semantics). There is no manual-merge fallback:
   the ruleset blocks direct merges to `master`.
5. Subscribe to the PR with `subscribe_pr_activity`. Then end the turn. Events wake the session.
   Never poll with sleep.

## Step 3: Watch PR CI through to merge

The PR pipeline is `.github/workflows/pr.yaml`. **Read the tier out of that file, not from here.**
It is one line in the workflow, and it grows as capabilities become cheap enough to gate every PR
on. A copy of it in this skill went stale within two features. A local run at a SMALLER tier than
CI's is not "what CI runs". Suites are gated on the capabilities they need, so the suites CI
declares and you did not are the ones you did not run.

- **CI green + auto-merge fires →** enqueued, then merged. Go to Step 4.
- **Ejected from the queue →** not a CI failure, and it raises no event. Diagnose the queue entry's
  own `test` run, fix it if the cause was yours, and re-enqueue.
- **CI fails →** diagnose from the logs (`mcp__github__get_job_logs`). Reproduce locally where the
  capability tiers allow. Fix it, then repeat this process from Step 1. The fix is itself a
  change, so re-compare it to what was asked. A mechanical fix (lint, missed test update, flaky
  infra) is not an interesting deviation: push it and keep going. A fix that would change the
  approach or scope IS one: stop and report instead.
- Keep looping until merged. One fix round is not the task. Give each new failure the same
  treatment.

## Step 4: Watch the master pipeline

Merging is not the finish line. `.github/workflows/release.yml` runs the full `verify` gate (plus
Docker/LiveAgent tiers PRs cannot run) on every master push, and that publishes the release. Your
merge can break master in ways PR CI could not see.

PR webhooks stop at merge, so watch master actively:

1. Find the `release` workflow run for the merge commit (`mcp__github__actions_list` /
   `actions_get`).
2. If it is still running, schedule a `send_later` check-in (~15–30 min) and end the turn. On
   wake, re-check and re-arm until it concludes. Never busy-wait.
3. **Success →** done. Unsubscribe from the PR if still subscribed. Tell the user the change is
   merged and released.
4. **Failure →** treat it as yours until proven otherwise. Diagnose. Fix on a fresh branch
   restarted from latest master. Repeat the whole process from Step 1 for the fix. The same
   deviation gate applies: mechanical fixes flow through autonomously, and anything needing an
   interesting deviation from the original request stops, and you report it. If the failure
   demonstrably predates the merge (it reproduces on the parent commit), report that to the user
   instead of blind-fixing.

## Definition of done

Both must hold: the PR is merged to master, read from the PR's merge state and not from a
green check, AND the master `release` pipeline is green for (or after) the merge commit. Until
then the loop is still running. Arm an event subscription or a scheduled check-in before you end
the turn.
