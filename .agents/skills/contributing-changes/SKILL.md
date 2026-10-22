---
name: contributing-changes
description: How a change gets from asked-for to merged and green. Split the request into independently-shippable PRs. Compare what you built to what was asked. Open a PR with auto-merge, watch CI, and drive to a green master. Read at the START of any request to build, fix, refactor or change something, before the first edit, because the split cannot be decided later. Read again when an implementation is done and before opening a PR. Read again after any CI failure on a PR you opened, because a fix repeats this process.
---

# Contributing changes

A change is contributed only when master contains it and the master pipeline is green. Write no
plan document ([AGENTS.md, "Plans are not artifacts"](../../../AGENTS.md#contributing-changes)).

## Step 0: Split the request before writing any code

Split the request first, as the user stated it. Most requests are several changes in one
sentence. The split is cheap only now. After the code exists, it needs branch surgery.

A long branch pays rebase tax, and the queue does not reconcile conflicts. The tax is
superlinear: twice the branch against twice the drift gives four times the conflict. Plan 17's
one four-step branch took longer to reconcile than all four features took to write.

Cut the request into the smallest pieces that each stand alone. Ship each piece as its own PR.
Finish the first piece and run this process end to end. Then start the second from the merged
master.

- **A piece stands alone when it builds, `check` passes, and it breaks nothing in master.** Its
  feature can be incomplete. These pieces can all merge: a capability behind an unset flag, a
  domain type that nothing calls yet, an engine with no provider on top.
- **Keep pieces together only when a split would land something broken.** Example: a rename and
  its call sites.
- **Bump the version once, on the piece that earns it.** Four PRs are still one feature. Put the
  marker on the commit that moves the contract, not on each slice.
- **Tell the user the split before you start**, in one or two sentences: what lands first, what
  follows, and what each piece is good for alone. Then continue without asking. A split that DROPS or defers an item the user asked for is a scope change. It
  goes through the Step 1 gate: tell the user and wait.

**Run independent pieces in parallel.** Pieces are independent when they touch disjoint code and
need no order. Put each on its own branch off master and enable auto-merge on all at once. Keep a
strict order only where a piece breaks without another in master first. Examples: a rule and the
sites it rejects, a protocol change and its only caller. Independent PRs in series behind one
branch each cost a merge cycle for nothing.

If you find several finished pieces on one branch, open the PR for what is there. Split the NEXT
request correctly.

## Merge semantics: the queue

`master` merges through a **merge queue**, not directly. Squash is the only method.

- **A moving master costs nothing.** The queue tests your change against real master, so a green
  check does not go stale. Rebase only for a real conflict.
- **Auto-merge enqueues. It does not merge.** The queue builds base+PR on a temporary ref, where
  `pr.yaml`'s `merge_group` trigger runs `test`.
- **Never infer the merge state from a check conclusion, in either direction.** Grouping is
  `HEADGREEN`, so a batch can merge on its LAST entry's checks and carry a red entry with it. The
  queue drops an entry that fails, or that does not report in 60 minutes. Its PR then becomes open
  again with auto-merge off, and no event tells you. Read
  `gh pr view <n> --json state,mergedAt,mergeStateStatus` and believe that.
- **A stuck PR has a reason, and `mergeStateStatus` names it. Read it before you act.**
  - `DIRTY` (or `CONFLICTING`): a real conflict with master. Rebase onto origin/master and
    force-push. A DIRTY PR never lands from the queue.
  - `BEHIND`: the queue updates it for you. Do nothing.
  - `BLOCKED`: a required check or review is not complete. Find it.
  - Anything else (`CLEAN`/`UNSTABLE`) on a PR that is neither merged nor in the queue: it was
    ejected. Enqueue it again.

  A queued PR that went DIRTY looks the same as one on its way in. Do not read "queued" as "will
  land".
- **`autoMergeRequest` reads `null` while a PR is IN the queue.** It does not prove that the PR
  left the queue. Read the queue: `gh api graphql -f query='{ repository(owner:"trinketworks",
  name:"yession"){ mergeQueue(branch:"master"){ entries(first:10){ nodes{
  pullRequest{number} state position } } } } }'`. An entry whose run failed stays as
  `UNMERGEABLE`, and `gh pr merge --auto` answers "already queued". To clear it, run the
  `dequeuePullRequest` mutation and then the `enqueuePullRequest` mutation on the PR's node id.
- **One push can carry several PRs.** So the `release` run in Step 4 can be a batch head that
  contains your commit, not a run with its name.
- **A stacked PR goes DIRTY when the PR under it squash-merges.** Stacked means B branched from A,
  and both target master. Run `git rebase --onto origin/master <A-sha>` and force-push. Arm B's auto-merge only after A
  lands. If you arm it earlier, B's squash carries A's diff. If B is already in the queue, GitHub
  locks its branch (push refused, GH006) and B merges as it is. Do not change it.
- **PR CI checks out master+PR.** A branch that builds locally can fail CI on a file that master
  added after you branched. Run `git fetch` and rebase before you diagnose.

## Step 1: Compare what you built to what was asked

Compare what exists with the request (the user's own words, and the shape you gave in Step 0).
Look for **interesting deviations**: anything that would surprise the person who asked.

- The approach changed (a design, files or mechanism different from what you said).
- The scope changed (you dropped items they asked for, or added behaviour they did not ask for).
- You hit a blocker, or some work is not possible to complete.
- Public behaviour differs from what you promised.

NOT interesting (continue without asking): mechanical differences. Examples: renames, small
refactors that the code forced, a correction of a wrong assumption in the request, test
scaffolding that nobody listed in advance.

**If you find interesting deviations, blockers or work you cannot complete: STOP.** Report them to
the user and wait. Do not open a PR. The user decides. If the work matches, continue. Do not ask.

## Step 2: Run the checks locally, then open the PR

1. Before you push, run the checks this box can run, at minimum `check` inside devenv
   ([AGENTS.md, "Testing"](../../../AGENTS.md#testing)).
2. Commit on the session's designated feature branch. Push with `git push -u origin <branch>`.
3. Open a PR against `master` (`mcp__github__create_pull_request`). Write the description as the
   squash commit message, by the rules in [AGENTS.md, "Versioning"](../../../AGENTS.md#versioning).
   Say what this piece does. If it is one of several, say what it is alone and what follows it.
4. Enable auto-merge: `mcp__github__enable_pr_auto_merge` (squash). The ruleset blocks direct
   merges to `master`.
5. Subscribe to the PR with `subscribe_pr_activity`. Then end the turn. Events wake the session.
   Never poll with sleep.

## Step 3: Watch PR CI through to merge

The PR pipeline is `.github/workflows/pr.yaml`. **Read the tier from that file, not from here**,
because it grows. A local run at a SMALLER tier than CI's skips the suites that CI declares and
you did not.

- **CI green and auto-merge fires:** the PR enters the queue, then merges. Go to Step 4.
- **The queue ejects the PR:** this is not a CI failure, and it gives no event. Diagnose the queue
  entry's own `test` run. If the cause was yours, fix it. Then enqueue the PR again.
- **CI fails:** diagnose from the logs (`mcp__github__get_job_logs`). Reproduce locally where the
  capability tiers allow. Fix it, then do this process again from Step 1: the fix is a change too.
  A mechanical fix (lint, a missed test update, flaky infra) continues. A fix that changes the
  approach or scope stops for the user.
- Continue the loop until the PR merges. Treat each new failure the same way.

## Step 4: Watch the master pipeline

`.github/workflows/release.yml` runs the full `verify` gate on every master push, including
Docker and LiveAgent tiers that PRs cannot run, and publishes the release. PR webhooks stop at the
merge, so watch master yourself:

1. Find the `release` workflow run for the merge commit (`mcp__github__actions_list` /
   `actions_get`).
2. If it still runs, schedule a `send_later` check-in (~15–30 min) and end the turn. On wake,
   check again and schedule again until it concludes. Never busy-wait.
3. **Success:** done. If you are still subscribed to the PR, unsubscribe. Tell the user the change
   is merged and released.
4. **Failure:** treat it as yours until you prove otherwise. Fix it on a new branch from the
   latest master, through the whole process from Step 1, with the same deviation gate. If the
   failure reproduces on the parent commit, it predates the merge: report it to the user. Do not
   fix it blind.

## Definition of done

- The PR is merged to master. Read this from the PR's merge state, not from a green check.
- The master `release` pipeline is green for the merge commit, or for a later commit.

Until then, the loop continues. Arm an event subscription or a scheduled check-in before you end
the turn.
