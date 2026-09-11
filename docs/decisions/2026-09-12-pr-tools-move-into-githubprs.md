# `create_pr`, `watch_pr` and `unwatch_pr` are declared by `GitHubPrs.fs`, not `AgentTools.fs`

> Decided 2026-09-12 · Supersedes nothing · Related:
> [app/GitHubPrs.fs](../../app/GitHubPrs.fs) — now the whole of these three tools, not just
> the REST underneath them,
> [src/Yession.Domain/AgentTools.fs](../../src/Yession.Domain/AgentTools.fs) — the `yession`
> registry, which no longer says GitHub anywhere,
> [app/PrWatches.fs](../../app/PrWatches.fs) — the `pull_requests` query, the precedent this
> follows,
> [app/Commands.fs](../../app/Commands.fs) — where the gated verbs and the provider's tools
> are wired together,
> [2026-08-27-pr-state-by-polling.md](2026-08-27-pr-state-by-polling.md) — the poll these
> tools sit beside

## Decision

`create_pr`, `watch_pr` and `unwatch_pr` — their descriptions, their argument schemas, their
JSON decoding — are declared in `app/GitHubPrs.fs`, not `src/Yession.Domain/AgentTools.fs`.
They reach the `yession` tool registry through a new field, `RepoCapabilities.ProviderTools
: (ToolDescriptor * (string -> Async<Result<ToolAnswer, string>>)) list`, which
`AgentTools.fs` appends to its own declared tools without reading a word of it:

```fsharp
let private declared (capabilities: AgentCapabilities) =
    verbs capabilities @ queryTools capabilities @ capabilities.Repos.ProviderTools
```

`app/Commands.fs` fills the field once, after binding the three gated verbs
(`CreatePr`/`WatchPr`/`UnwatchPr`) to the turn's actor:

```fsharp
{ capabilities with
    Repos = { capabilities.Repos with ProviderTools = GitHubPrs.providerTools capabilities } }
```

Wire names do not move — still `mcp__yession__create_pr` and so on, because `yession` names
the SDK server and the namespace these tools have always lived in, not who wrote their
prose.

## Why

We are adding GitHub-specific tools — starting with the merge-queue behaviour `watch_pr`
already narrates in words ("queued", "stalled — what a merge queue ejecting an entry looks
like") — and want a GitLab adapter later to be able to add its own without touching
`AgentTools.fs` or leaking a second forge's vocabulary into it. Before this change,
`AgentTools.fs` — the file that also declares `execute_command`, `add_repo`,
`set_shell_profile` and everything else provider-agnostic — hard-coded GitHub prose
directly in three of its descriptions ("Open a pull request **on GitHub**", "spends the
**GitHub credential**", "what a **merge queue** ejecting an entry looks like"). Adding a
fourth, GitHub-only tool the same way would have made that file's job — being the one place
a *shared* tool is declared — impossible to state honestly.

The fix follows a seam the codebase had already built for a different kind of tool: the
`pull_requests` **query** is declared entirely in `app/PrWatches.fs`, including its
GitHub-flavoured legend, and merged into the query surface through
`AgentCapabilities.Queries.Declared` — a list `AgentTools.fs`'s `queryTools` turns into
tools generically, never reading what is in it. `ProviderTools` is the same move for
**commands**: a repo capability carries a list of already-built `(ToolDescriptor, body)`
pairs, and the registry's only job is concatenation.

## Why not keep the descriptors in `AgentTools.fs` and only move the bodies?

Because the leak was in the *prose*, not the plumbing. The bodies (`createPr`, `watchPr`,
`unwatchPr` — argument decoding, `withRepo`, calling `capabilities.Repos.CreatePr` etc.) were
already provider-neutral; moving only those would have left the sentence "Open a pull
request on GitHub" sitting in the one file whose whole point is not saying that. Everything
GitHub-specific about a session's pull requests was already meant to live in one place —
`GitHubPrs.fs`'s own header says so — and these three sentences were the one part of that
promise the tool layer had not kept.

## Why a list of built tools, rather than three named slots (`CreatePrTool`, `WatchPrTool`, `UnwatchPrTool`)

A fixed set of slots is exactly the shape that stops working the moment a second provider
disagrees about what a merge queue needs saying about it — GitLab merge trains, or a
provider with no equivalent of `watch_pr` at all, or one that wants a fourth tool with no
GitHub analogue. A list says only "here are some tools", which is the whole of what
`AgentTools.fs` needs to know. The type it carries — `ToolDescriptor *
(string -> Async<Result<ToolAnswer, string>>)` — is the exact pair `verbs` and `queryTools`
already produce, so the registry does not gain a second shape of tool to reason about.

## What moved, in code terms

- `ToolArgs.repoNumber` and `ToolArgs.prDraft` → `GitHubPrs.repoNumberArgs` /
  `GitHubPrs.prDraftArgs`.
- The private renderers `watchPr`, `unwatchPr`, `createPr` → the same names, private to
  `GitHubPrs.fs`, calling `AgentTools.renderCommandOutcome` (left public, and public was
  already the case) rather than a copy of it.
- The three `tool "create_pr" ...` / `"watch_pr"` / `"unwatch_pr"` entries in `verbs` →
  `GitHubPrs.providerTools`, unchanged in every word of their description.
- `RepoCapabilities` gained `ProviderTools`, defaulted to `[]` in `AgentCapabilities.none`
  so every other constructor of the record keeps compiling.
- `Commands.repoCapabilitiesFor` now builds the gated `Repos` record first, then sets
  `ProviderTools = GitHubPrs.providerTools capabilities` against the finished thing — the
  provider's tools close over the *gated* verbs, the same ones a human's approval queue
  sees, not the raw service underneath them.

Tests moved with the same seam: `Tools.fs` (the generic registry suite) keeps one test
proving a tool contributed through `Repos.ProviderTools` is reachable by name, with no
GitHub in it; the three behavioural tests for `create_pr`'s argument handling moved to
`Connections.fs`, beside the rest of `GitHubPrs.fs`'s suite, invoking
`GitHubPrs.providerTools` directly rather than the full registry.

## What would change it

- **A GitLab adapter.** It contributes its own `providerTools` (or none, if a repo has no
  provider-specific tool worth adding) through the same field on its own
  `RepoCapabilities` — `AgentTools.fs` does not change, and neither does this decision.
- **Two providers on one session.** Not something the shape above prevents — a session with
  both a GitHub and a GitLab repo capability would need `ProviderTools` per repo rather than
  per session, which nothing here has needed yet because a session's repos have so far
  shared one forge.
