# Records are a third family of the store

> Decided 2026-11-01 · Related:
> [2026-10-26-secrets-and-connections-are-two-families.md](2026-10-26-secrets-and-connections-are-two-families.md),
> [2026-08-10-local-scope.md](2026-08-10-local-scope.md),
> [technical-design.md §3](../technical-design.md#3-authority-model) and rule 11 of §5

## Context

Some things a session learns about a person should outlive the session. The first is whether a
person trusts a repository's code to run with their GitHub. A person who has said yes once should
not be asked again in the next session that adds the same repository.

A session's own event log cannot hold that. It is one session's history, and the next session
starts with an empty one.

The Manager could hold it as a ledger of grants: which person trusts which repository with which
GitHub routes. That would teach the Manager about GitHub, repositories and grants. The Manager
has kept out of all three so far. It brokers a connection without knowing which service it is
for, and the session supplies the provider's endpoints as data
([Connections.fs](../../src/Yession.Domain/Connections.fs)).

## Decision

The store gets a third family, records. The session owns what a record means. The Manager stores
it.

- **A record is text under `{ owner, name }`** (`RecordId`). The session code that owns a schema
  encodes its own type to text on the way in and decodes it on the way out. The Manager never
  parses the value.
- **The owner is a `CredentialOwner`**: a user the Manager verified, or the deployment's one
  unattributed principal. It is never a session. What one session knows about itself is its own
  event log.
- **The name is the session code's choice**, with its schema's version in it
  (`repo-trust/v1/github/42`). Two builds that encode differently then never write over each
  other's record. Names hold `a-z`, `0-9`, `.`, `_`, `-` and `/`, at most 200 characters, because
  the name is the one part of a record that an audit line shows.
- **Read, and write at a revision.** A write names the revision it read, or none when it found no
  record. It replaces the record only while that is still the stored revision. Otherwise it
  answers `Stale` and changes nothing, and the caller reads again, merges and writes again. So two
  sessions that change one person's record at once cannot lose either change.
- **The policy reuses the connection rows.** A user's records are reachable from a session that
  user signed in to (`boundUser`). The deployment's are reachable from a launch the Manager
  granted unattributed access (`localAccess`). Everything else is denied. The Manager sees the
  launch and who signed in to it, never which of them is acting. So whether a session uses one
  person's record for another person in it is the session's rule.
- **Limits per owner:** 512 records of at most 8192 characters each. Any session the owner signed
  in to can write their records, and the file is rewritten at every write, so the store bounds
  both.
- **Sealed under the store's key**, with its own AAD prefix (`yession-record:1:`). A ciphertext
  moved onto another owner or name, or in from either credential family, does not open.
- **Its own file**, `records.json`, beside `secrets.json`, with its own schema version.
- **Only session code reaches a record.** No agent tool, no `SecretRef` and no sandbox route
  names one.

## Alternatives rejected

**A grant ledger the Manager understands**, checked again when it resolves a connection. The
Manager would learn GitHub, repositories and routes. And the second check would not hold anything
the first does not: the session holds the tokens it spends, so a session that misdescribed a
spend could not be caught there either.

**User-scoped generic secrets.** A session may not write those, because the agent's `set_secret`
reaches that API. Records have no agent route at all, so a session may write them.

**A third list in `secrets.json`.** That is a version 3 file. A Manager rolled back to version 2
would refuse it, and every connection with it. A file that an older build does not know is a file
it leaves alone.

**Session-scoped records.** A fact about one session is an event in that session's log.

## Consequences

- The control wire gains two routes, `/control/records/read` and `/control/records/write`. The
  change is additive. A Manager from before it answers a newer session's call with 404. A session
  must read any failed read as no record, so the most a version skew can cost is a question asked
  again.
- A rollback past this change leaves `records.json` in place and unread. A roll forward finds it
  as it was.
- A records file this build cannot read refuses the records alone: a newer build's version, a
  corrupt file, or a file sealed by another key. Every records call answers the reason. The boot
  log says it once (`yession.secrets.records_refused`), and the credentials open as usual. The
  file is never written over, because the build after this one may want it back.
- A records file is sealed under the same key as the secrets, so it also stops a locked keychain
  being read as a first run. A record can be written before any secret.
- Nothing lists or removes a record yet ([GAPS](../GAPS.md#persistence--data)).
