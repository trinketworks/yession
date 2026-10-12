# `create_pr`, `watch_pr` and `unwatch_pr` are declared by `GitHubPrs.fs`, not `AgentTools.fs`

> Decided 2026-09-12 · Supersedes nothing · Related: [app/GitHubPrs.fs](../../app/GitHubPrs.fs) —
> now the whole of these three tools, not just the REST underneath them.
> [src/Yession.Session/AgentTools.fs](../../src/Yession.Session/AgentTools.fs) — the `yession`
> registry, which no longer says GitHub anywhere. [app/PrWatches.fs](../../app/PrWatches.fs) — the
> `pull_requests` query, the precedent this follows. [app/Commands.fs](../../app/Commands.fs) —
> where the gated verbs and the tools of the provider are wired together.
> [2026-08-27-pr-state-by-polling.md](2026-08-27-pr-state-by-polling.md) — the poll these tools sit
> beside.

## Context

We are adding GitHub-specific tools. The first is the merge-queue behaviour that `watch_pr` already
narrates in words ("queued", "stalled — what a merge queue ejecting an entry looks like"). We want a
GitLab adapter later to add its own tools without touching `AgentTools.fs` and without leaking the
vocabulary of a second forge into it.

Before this change, `AgentTools.fs` hard-coded GitHub prose in three descriptions:

- "Open a pull request **on GitHub**"
- "spends the **GitHub credential**"
- "what a **merge queue** ejecting an entry looks like"

That file also declares `execute_command`, `add_repo`, `set_shell_profile` and everything else that
is provider-agnostic. Its job is to be the one place where a *shared* tool is declared. A fourth,
GitHub-only tool declared the same way would make that job impossible to state honestly.

The codebase already had a seam for a different kind of tool. The `pull_requests` **query** is
declared entirely in `app/PrWatches.fs`, including its GitHub-flavoured legend. It reaches the query
surface through `AgentCapabilities.Queries.Declared`. The `queryTools` function of `AgentTools.fs`
turns that list into tools generically and never reads what is in it.

## Decision

`create_pr`, `watch_pr` and `unwatch_pr` — their descriptions, their argument schemas, their JSON
decoding — are declared in `app/GitHubPrs.fs`, not in `src/Yession.Session/AgentTools.fs`. They reach
the `yession` tool registry through a new field, `RepoCapabilities.ProviderTools : (ToolDescriptor *
(string -> Async<Result<ToolAnswer, string>>)) list`. `AgentTools.fs` appends the field to its own
declared tools without reading a word of it:

```fsharp
let private declared (capabilities: AgentCapabilities) =
    verbs capabilities @ queryTools capabilities @ capabilities.Repos.ProviderTools
```

`app/Commands.fs` fills the field once, after it binds the three gated verbs
(`CreatePr`/`WatchPr`/`UnwatchPr`) to the actor of the turn:

```fsharp
{ capabilities with
    Repos = { capabilities.Repos with ProviderTools = GitHubPrs.providerTools capabilities } }
```

Wire names do not move. They are still `mcp__yession__create_pr` and so on, because `yession` names
the SDK server and the namespace where these tools have always lived. It does not name who wrote
their prose.

`ProviderTools` is the same move as the query seam, for **commands**. A repo capability carries a
list of already-built `(ToolDescriptor, body)` pairs, and the only job of the registry is
concatenation. The type is the exact pair that `verbs` and `queryTools` already produce, so the
registry gains no second shape of tool to reason about.

## Alternatives rejected

**Keep the descriptors in `AgentTools.fs` and move only the bodies.** The leak was in the *prose*,
not the plumbing. The bodies (`createPr`, `watchPr`, `unwatchPr` — argument decoding, `withRepo`,
calls to `capabilities.Repos.CreatePr` and its siblings) were already provider-neutral. Moving only
those would leave the sentence "Open a pull request on GitHub" in the one file whose whole point is
not to say that. The header of `GitHubPrs.fs` already says that everything GitHub-specific about the
pull requests of a session lives in one place. These three sentences were the one part of that
promise that the tool layer had not kept.

**Three named slots (`CreatePrTool`, `WatchPrTool`, `UnwatchPrTool`).** A fixed set of slots stops
working when a second provider disagrees about what a merge queue needs saying. GitLab merge trains
are one example. Others are a provider with no equivalent of `watch_pr`, and a provider that wants a
fourth tool with no GitHub analogue. A list says only "here are some tools". That is the whole of
what `AgentTools.fs` needs to know.

## Consequences

What moved:

- `ToolArgs.repoNumber` and `ToolArgs.prDraft` became `GitHubPrs.repoNumberArgs` and
  `GitHubPrs.prDraftArgs`.
- The private renderers `watchPr`, `unwatchPr` and `createPr` kept their names, now private to
  `GitHubPrs.fs`. They call `AgentTools.renderCommandOutcome`, which stays public, and not a copy of
  it.
- The three `tool "create_pr" ...` / `"watch_pr"` / `"unwatch_pr"` entries in `verbs` became
  `GitHubPrs.providerTools`, with every word of their descriptions unchanged.
- `RepoCapabilities` gained `ProviderTools`. `AgentCapabilities.none` defaults it to `[]`, so every
  other constructor of the record keeps compiling.
- `Commands.repoCapabilitiesFor` now builds the gated `Repos` record first. It then sets
  `ProviderTools = GitHubPrs.providerTools capabilities` against the finished record. The tools of
  the provider therefore close over the *gated* verbs, not the raw service underneath them. These
  are the same verbs that the approval queue of a human sees.

The tests moved with the same seam. `Tools.fs`, the generic registry suite, keeps one test. It
proves that a tool contributed through `Repos.ProviderTools` is reachable by name, with no GitHub in
it. The three behavioural tests for the argument handling of `create_pr` moved to `Connections.fs`,
beside the rest of the `GitHubPrs.fs` suite. They invoke `GitHubPrs.providerTools` directly and not
the full registry.

### What would change this decision

- **A GitLab adapter.** It contributes its own `providerTools` through the same field on its own
  `RepoCapabilities`. It contributes none if a repo has no provider-specific tool worth adding.
  `AgentTools.fs` does not change, and neither does this decision.
- **Two providers on one session.** The shape above does not prevent it. A session with both a
  GitHub and a GitLab repo capability would need `ProviderTools` per repo and not per session.
  Nothing here needs that yet, because the repos of a session so far share one forge.
