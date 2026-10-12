# The Manager relays hooks it cannot read

> Decided 2026-08-29 · Supersedes nothing · Related:
> [src/Yession.Domain/Webhooks.fs](../../src/Yession.Domain/Webhooks.fs) — the filter.
> [app/WebhookRelay.fs](../../app/WebhookRelay.fs) — the endpoints and the fan-out.
> [deployment.md](../deployment.md) §Webhooks — how an operator declares one.
> [2026-08-27-pr-state-by-polling.md](2026-08-27-pr-state-by-polling.md) — this decision answers its
> third reason for refusing webhooks

## Context

A GitHub App has exactly **one** webhook. Ingress therefore cannot be session-direct, and it has to
land somewhere stable. Only the Manager has a stable public origin.

The Manager is the one component that has never learned which provider it brokers, and the decision
to poll refused webhooks partly for that reason. The Manager is kept ignorant for minimality: a
smaller single point of failure, and sessions that upgrade on their own.

## Decision

The Manager serves **hook endpoints**: `POST /hooks/<name>`, one per service that an operator
declares. It verifies that a delivery is signed, and it matches the delivery against **filters that
sessions declared**. It forwards the delivery to those sessions over the notification leg that it
already had. It never learns which service sent a delivery, what the event means, or what any field
is called.

A filter is **data**: a conjunction of equalities over dotted paths into the delivery. The delivery
is addressed as one document (`headers.x-github-event`, `body.repository.full_name`). The signing
secret is **derived** from the KEK that the credential manager already holds. It is never stored and
never configured.

### Why the Manager verifies and the session does not

A **delivery** arrives from the internet, so the Manager verifies it at the door. It uses
HMAC-SHA256 over the raw bytes, against every secret that the endpoint currently accepts. That work
happens over opaque bytes, so it teaches the Manager nothing. Parsing would teach it something, and
parsing is confined to resolving the paths that a session named.

A **subscription** arrives over the authenticated control channel from a child that this Manager
spawned. That child already holds whatever credential it would act on, so the Manager takes its
word. Sessions are trusted. The header of `WebhookRelay.fs` states this posture.

### Why the secret is derived rather than configured

The provider lets us choose the secret. GitHub's instruction when you register a webhook is "type a
string to use as a `secret` key". The Manager generates one, and the operator pastes it in while
already in the settings page, typing the URL. Nothing to find, nothing to record, and no plaintext
secret in the state file.

`HMAC(KEK, "yession-webhook:<name>:<rotation>")` is stable across restarts because the KEK is.
Nothing writes it, because nothing needs to. A rotation bumps the counter. The previous secret stays
accepted until the counter moves again, so there is no window where live deliveries are refused.

## Alternatives rejected

**A predicate that a session ships to the Manager.** That is code, and code has to be interpreted.
The Manager would then have to implement every construct that the code could contain. That makes it
a **version ceiling on the sessions it supervises**. A session upgraded to emit a construct that an
older Manager has not learned stops working against it. That is the
opposite of why the Manager is kept ignorant.

So the filter language is small enough to have no versions at all: a conjunction of equalities.
Every operator that someone could add (a disjunction, a negation, a pattern) is one more thing that
two builds can disagree about. Equality is the one that cannot be read two ways. The header of `Webhooks.fs` states
this reasoning in full.

The filter and the payload go together. A content-free ping would need no filter language at all.
The filter earns its keep because the delivery rides with it. That also makes the relay useful to
the next integration and not only to the first.

## Consequences

The Manager gained its first knowledge of anything provider-shaped: one header name, one HMAC, and a
JSON walk. That knowledge is bounded to `WebhookRelay.fs`. It buys what a GitHub App makes
unavoidable: one webhook, so ingress lands somewhere stable.

The Manager still has no provider. `repository.full_name` appears once in this repository, in the
session that watches pull requests.

**Boot refuses endpoints without a durable secret store.** An ephemeral secret store mints a fresh
KEK every boot. Every secret that an operator pasted into a provider would then silently stop
working at the next restart, and only for inbound deliveries. That is the least visible way to
break.

### What would change this decision

- **A scheme that signs a constructed string.** Stripe signs `<timestamp>.<body>` and reads it out
  of a structured header. Slack signs `v0:<ts>:<body>`. More configuration does not reach those
  schemes. They need the scheme itself. The relay forwards the headers of a delivery, so the
  alternative is a session that verifies its own endpoint. We have not built that.
- **A filter that genuinely needs disjunction.** That is the moment to ask what it is really for. It
  is not the moment to add an operator.
