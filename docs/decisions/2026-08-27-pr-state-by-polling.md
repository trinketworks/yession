# A session learns about its pull requests by asking GitHub, not by being told

> Decided 2026-08-27 · Supersedes nothing · Related: [app/PrWatches.fs](../../app/PrWatches.fs) —
> the poll, the verbs and the query. [app/GitHubPrs.fs](../../app/GitHubPrs.fs) — the two endpoints
> behind them. [src/Yession.Domain/PrFacts.fs](../../src/Yession.Domain/PrFacts.fs) — the
> vocabulary. [src/Yession.Domain/PrWatches.fs](../../src/Yession.Domain/PrWatches.fs) — the durable
> baseline. [deployment.md](../deployment.md) §GitHub — how an operator registers the App

## Context

A session needs to know when a pull request that it watches changes: merged, closed, reopened,
checks green, checks red. Either GitHub tells the session, or the session asks GitHub.

## Decision

A session watches a pull request by **polling GitHub for it**. Each poll makes two conditional GETs:
the pull request, then the check runs of its head commit. The poll spends the credential of whoever
started the watch. It runs every 15 seconds while the checks are pending and every 60 otherwise. The
session appends each transition (merged, closed, reopened, checks green, checks red) to its own log
and reads it back on the timeline. Current state is the `pull_requests` query.

We do **not** register webhooks, and the Manager gains nothing. It brokers a credential that it
still cannot name, and its Session-notification channel stays the producerless transport it was.
`FetchPr` is a one-function seam, so a pushed transport later replaces an implementation and not a
design.

### Why two intervals

The two waits are not the same wait. A settled watch waits for something that has not started, such
as a push or a merge. Nobody acts on either inside a minute. A watch whose checks are PENDING is one
that somebody sits in front of, with a suite in flight that is about to say something. There,
fifteen seconds is the difference between noticing and having moved on.

Two intervals make the fast cadence affordable. GitHub does not count a `304` against the primary
rate limit. A settled watch is therefore free at any interval: two conditional requests that both
answer 304, against a budget of 5000 an hour. A pending watch is not free, because its checks
endpoint really is moving. It makes four polls a minute, which puts the ceiling around ten pull
requests with live suites at once per credential. A single interval would have to serve one of those
two facts and fail the other.

Both halves are always asked. The checks URL names the head sha, but the runs ON a sha go from
queued to completed while the sha sits still. That is what CI finishing looks like. A short-circuit
for an unmoved pull request could hold a watch at `pending` for the whole life of a build that had
already gone green.

The poll honours the `x-ratelimit-reset` of GitHub and uses no backoff invented here. The provider
names the moment it will answer again, and any number that we picked would be a guess against it.

### Why the baseline is in the log rather than in memory

The process restarts, and both wrong answers are bad. One is to re-announce a merge that the session
announced yesterday. The other is to swallow, silently, a merge that happened while the process was
down. A watch therefore records its `Initial` snapshot and every transition that it announces. The
baseline folds from those events (`PrWatchesProjection`).
[`src/Yession.Domain/PrWatches.fs`](../../src/Yession.Domain/PrWatches.fs) states the rule.

The log also makes the wake safe. A woken turn is a debt found in the log after the last turn
started, so one transition can wake at most one turn, ever.

### Why a pull request change wakes the agent, when a tool roster change does not

A watch has an owner. An attributed party started it in a gated act, and the poll that noticed spent
the credential of that same party. The wake runs as them, on a thing that they explicitly asked to
be told about. A roster change belongs to `ActorRef.System` and to nobody, and `System` is not an
authority that the agent can call tools on. The `WakeReason` comments in
[`src/Yession.Domain/AgentFacts.fs`](../../src/Yession.Domain/AgentFacts.fs) state both halves of
this rule.

## Alternatives rejected

### Webhooks

Three separate reasons. The first two still stand, which is why polling remains the mechanism and
not the fallback. The Manager has since answered the third (see below).

- **A repo webhook needs admin on that repo.** The ordinary case is a contribution to a repository
  that you do not administer. There, `POST /repos/{o}/{r}/hooks` is simply refused. A feature that
  works only on repos you own is not the feature.
- **Inbound delivery needs a deployment that GitHub can reach.** The zero-configuration deployment
  is loopback (`http://127.0.0.1:{port}`, see deployment.md §Addressing), and github.com cannot POST
  to it. Webhooks would have served the fronted deployments and silently not the default one.
- **It would have put GitHub in the Manager.** Only the Manager has a stable public origin, so
  ingress would have landed there. The Manager is the one component that has never learned which
  provider it brokers.

  **Answered, 2026-08-29.** The Manager now relays hooks it cannot read
  ([ADR](2026-08-29-the-manager-relays-hooks-it-cannot-read.md)). A session declares a filter —
  data, a conjunction of equalities over paths. The Manager verifies a signature and matches without
  knowing what any of it means. The string `body.repository.full_name` lives in `GitHubPrs.fs`, on
  its way past. The Manager gained one header name, one HMAC and a JSON walk, bounded to one file. A
  GitHub App has exactly one webhook, so ingress had to land somewhere stable regardless.

### The GitHub Events API

It is not the fallback that it looks like. It omits `check_run`, `check_suite` and `status`, which
is most of what a watcher waits for.

### Where webhooks would belong: a hosted dispatching service

Webhooks would belong with a hosted dispatching service. The same service would let somebody run
Yession without registering a GitHub App of their own. The shape has three parts:

- One App-level webhook on the hosted App.
- A relay that each session dials **out** to.
- Deliveries matched to the sessions entitled to them.

Outbound means it works in every deployment shape, loopback included. Inbound ingress could not do
that.

That service does not exist. Until it does, registering your own App is the only path and not a
fallback. A security-conscious operator would choose it anyway.

### Session-direct ingress

A fronted session is publicly addressable, so a hook could POST straight at it. That is wrong on the
facts. A GitHub App has exactly ONE webhook, auto-created and not deletable, so it can never point
at a session. Only a repository or organization hook can, and creating one needs admin on the repo —
the first reason above. It is still the route for repos you administer. It is not built.

## Consequences

### What would change this decision

- **The dispatching service exists.** `FetchPr` then gains a second implementation. Polling becomes
  the fallback for deployments that do not use the service. We would not delete it, because a
  self-hosted operator with no relay still needs it.
- **GitHub streams PR state.** Suppose the provider offered a subscription that a client could hold
  open. That would become the reconnect path and not the mechanism.
- **A watcher needs seconds, not fifteen.** A lower pending interval spends the ceiling above and
  does not buy latency for free. The answer at that point is a pushed transport, not a smaller
  number.
