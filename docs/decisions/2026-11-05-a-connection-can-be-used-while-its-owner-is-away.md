# A connection can be used while its owner is away

> Decided 2026-11-05 · Related:
> [2026-10-26-secrets-and-connections-are-two-families.md](2026-10-26-secrets-and-connections-are-two-families.md),
> [2026-11-01-records-are-a-third-family-of-the-store.md](2026-11-01-records-are-a-third-family-of-the-store.md),
> [2026-11-01-a-repo-runs-with-the-github-of-somebody-who-trusts-it.md](2026-11-01-a-repo-runs-with-the-github-of-somebody-who-trusts-it.md)

## Context

A launch reads a person's connections and records only after that person signs in to it. The
Manager keeps those bindings in memory, so a relaunch (an idle reap, a crash, a Manager restart)
starts with nobody bound. Work the session does on its own then has no credential. The pull
request poller went anonymous, met GitHub's sixty-an-hour limit, and held every watch until the
window turned over. A repo's sandboxes waited for their truster.

A person who wants the agent to keep working while they are away has no way to say so.

## Decision

**A person's connection carries an optional offline grant**, stored with the credential in
`secrets.json`: all my sessions, or a set of sessions. Only a connection a person signed in for
themselves (`UserScope`) takes one. A session's own credential is already readable with nobody
signed in, and the deployment's is not a person's.

- **The grant is kept across every rewrite of the credential**: a refresh, a re-paste, a new
  grant. Disconnecting removes it with the credential.
- **"All my sessions" means the sessions they have signed in to.** The Manager now records each
  session's members (`SessionRecord.Members`). A session somebody else made, that they never
  joined, does not get their connection.
- **It stands in for a sign-in on reads only.** A launch the grant covers may resolve that one
  connection and read its owner's records (a sandbox needs their repo trust to start). It may
  not connect, replace or disconnect a credential, or write a record, for somebody who is not
  there.
- **Only its owner, signed in, sets it**, from a session (`POST /control/connections/offline`).
  Switching it off revokes at once: the next resolve is refused and the status stream drops the
  connection.
- **The Manager computes it**, from its own members list and stored grants, on every call. A
  session never asserts it.

The status stream says how each connection's grant reads from the receiving session (off, this
session, all my sessions), so its settings can show and change it.

## Alternatives rejected

**Letting any launch resolve its sessions' past members.** That is every grant, always, with
nobody asked.

**A setting the Manager keeps per user, apart from the credential.** It would need its own
lifecycle (what happens on disconnect, on a new grant) to stay in step with the thing it is
about. Stored on the credential, it dies with it.

**A GitHub App installation token for the poller.** It would fix watching without a person, and
may still be worth having. It does not let the agent act as the person, push as them, or spend
their Claude, which is what working while they are away needs.

## Consequences

- A session the reaper stopped stays stopped until somebody opens it. A grant helps a session
  that is running, or one that restarts while work is under way.
- An older v2 build reads the file and drops the grant on its next save, which reads as off.
- What a session does for an absent person is attributed to them, as before.
