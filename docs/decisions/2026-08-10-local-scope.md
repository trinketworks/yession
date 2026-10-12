# An unattributed deployment owns credentials as itself, not as a browser

> Decided 2026-08-10 · Superseded in part 2026-09-14: `PeerScope` left the secrets vocabulary
> altogether, together with the peer witnessing that served it. No deployment held an entry. ·
> Supersedes in part: decisions 1–2 of the connections design, and peer-scoped secrets from the
> trusted-header identity design · Related: [deployment.md](../deployment.md) `--auth localhost`,
> `--secrets`

## Context

On a `--auth localhost` deployment, the operator had to click **Connect Claude** again and again.
Over ten days, one `secrets.json` collected three separate credentials, one per browser identity.
Only the current one was ever readable.

The chain:

1. `Strategy.localhost` returns `Unattributed "local"`, so the cookie carries `UnattributedAccess`.
2. `ClaudeConnection.ownerOf` therefore fell back to `PeerOwner <browser peer id>`.
3. That peer id lives in `localStorage['yession/peer-id']`, which the browser partitions **by
   origin**. A new origin, a cleared store, or a second browser or device mints a new id. A
   peer-scoped credential is readable only in a launch that this exact peer signed into.

Ownership was pinned to a value that changes under the person who holds it. It changed *silently*:
the panel showed a Connect button again. That reads as "you never connected" and not as "your
credential is behind an id that this browser no longer has".

## Decision

`SecretScope` gains a fourth case, `LocalScope`: **unattributed access to this deployment**, as one
principal. Under `--auth localhost`, it owns a connection credential that a person signed in for
"all my sessions". `PeerScope` leaves the connection path entirely.

A second knob, `--secrets`, lets the operator bound the lifetime of such a credential. `ephemeral`
forces an in-memory store even where a credential manager exists.

[`src/Yession.Domain/Secrets.fs`](../../src/Yession.Domain/Secrets.fs) states what `LocalScope` is,
what it is not, and its reach.

### Why the connection path needs no deployment fact

`connectionsApiFor.Status` serves only the readable scopes of the caller. The session filters its
turn candidates against exactly that set. So `turnTargets` names `LocalScope` on **every** turn,
without knowing how the deployment authenticates. An attributed launch never receives local access
and never sees the scope in its status stream. It drops the candidate before anything resolves.

This needed no new spawn-time fact, no env var, and no control-channel field. The readable set of
the Manager stays the single authority. A second copy of that judgement in the session could only
drift from it.

## Alternatives rejected

**A pseudo-user.** The Manager already binds `UserId "local"` into `launchUsers` on every localhost
login. `UserScope` already outranks `PeerScope` in the resolution walk. Writing the credential to
`UserScope "local"` would have worked with no new plumbing.

We rejected it because it is a lie that happens to typecheck. `local` is not a user. It is the
absence of one. A pseudo-user is indistinguishable, in the store, the policy, the audit trail and
the wire, from a real human who happens to be called `local`. Every future rule about users would
need a carve-out by string comparison. `LocalScope` is a distinct case, so the compiler enumerates
every place that must decide about it. That is how the codec, the policy, the audit attributes and
the injection walk all got their answer during this change and not after it.

The connections design said in its decision 2 "no pseudo-user". This decision keeps that promise. It
revises the conclusion that design drew from it: that an unattributed deployment must therefore own
by browser peer.

**A "shared subject" threaded down to each session.** We drafted it and discarded it, for the
single-authority reason above.

**A peer token that carries a user.** It would have collapsed every author in the transcript to one
name. That would destroy the one distinction an unattributed deployment can still draw.

## Consequences

- **One connection serves the whole deployment.** Any visitor's agent turn runs on it and spends
  against it. On a `tailscale serve` deployment, that is every tailnet visitor, because `serve`
  terminates on loopback and `localhost` trusts loopback. This is the existing trust rule stated
  honestly, not a new hole. Anyone who can reach a `localhost` Manager can already open and drive
  every session on it. `deployment.md` now states the rule and names both exits:
  `--secrets ephemeral` bounds the lifetime, and `--auth trusted-headers` removes the sharing.
- **Event attribution is unchanged.** Authors stay `PeerRef` under localhost. Credentials are
  shared. Who said what is not.
- **Existing `PeerScope` connection entries are orphaned.** They stay in `secrets.json`, encrypted
  and untouched, and nothing migrates them. `PeerScope` left `turnTargets` as well as the write
  path. Left half-live, a stale token in the peer scope of some old browser would shadow the new one
  at turn time. The operator clicks Connect once. A migration would only have tried to save that
  click.
- **`PeerScope` remains a first-class scope for generic secrets.** It has only stopped being a place
  where a *connection* lives.

### What would change this decision

- A multi-user deployment that wants separate accounts without a proxy in front. There is no answer
  for it here by construction. The honest answer is `--auth trusted-headers`.
- A generic secret worth holding at deployment scope. The policy permits `LocalScope` for connection
  actions only. Widening it is a deliberate act, and the `inject … "local"` arm of
  `SecretStore.Audit` is already there to record it.
