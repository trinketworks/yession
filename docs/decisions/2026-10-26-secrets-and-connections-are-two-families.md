# Generic secrets and connection credentials are two families of the store

> Decided 2026-10-26 · Related: [2026-08-10-local-scope.md](2026-08-10-local-scope.md),
> [technical-design.md §3](../technical-design.md#3-authority-model) and rule 19 of §5

## Context

The secret store held two kinds of entry under one key, `SecretId` (scope + name):

- generic secrets, which a session writes with `set_secret` and a `SecretRef` reads at sandbox
  spawn;
- connection credentials, which the broker writes as a whole `BrokeredCredential` envelope,
  refresh token included, under the names `claude-code` and `github`.

Nothing but the name told them apart. So a repository's `yession.yaml` could write
`env: { T: { secret: github } }` and receive the session's GitHub envelope in the container
environment. The rule "Refresh tokens never leave the Manager" had a hole at the injection route.
And the agent's `set_secret github` replaced the session-scoped envelope that the broker reads:
the policy permits `SetSecret` on the session's own scope, and the store upserted by
(scope, name).

The previous change made a `SecretRef` read the session's own scope only. That closed the
user-scoped envelopes and the Manager's environment. It did not close the session-scoped
envelope, or the write.

## Decision

The store keeps two families, and the type system keeps them apart.

- `ConnectionId` is a second identity type beside `SecretId`, with the same two parts.
- `SecretStore` has two partitions, `Secrets` and `Connections`. Each is keyed by its own
  identity type. The broker is given the connection partition and nothing else. The secrets
  API and the `SecretRef` resolver are given the generic partition and nothing else.
- A policy request is an `Access`: `SecretAccess of SecretAction * Resource` or
  `ConnectionAccess of ConnectionAction * ConnectionId`. An `InjectSecret` that names a
  connection credential cannot be written. No policy row has to refuse it.
- The cipher binds each family to its own AAD prefix (`yession-secret:1:` and
  `yession-connection:1:`). A ciphertext moved from one list to the other in the file does not
  open.
- The file is version 2. It holds `entries` (generic) and `connections`.

### Existing files

A version 1 `secrets.json` holds both families in `entries`, every one sealed under the generic
AAD. `openStore` sorts it once, at open:

- An entry whose value decodes as a broker envelope is a connection credential. That is
  exactly what the broker could read back, so every credential an earlier Manager stored is kept.
  It is opened under the generic AAD and sealed again under the connection AAD.
- Every other entry is a generic secret. It keeps its ciphertext, because its AAD does not
  change.
- An entry that does not open stays generic and untouched. It was unreadable before, and a read
  still says so.

The store then writes the file back as version 2, so the sort happens once.

## Alternatives rejected

**A denylist of the names `github` and `claude-code`.** A name check is not a type. The next
connection kind would need another name, and the hole would reopen the day someone forgot it.

**A family field on `SecretId`.** One type with a tag still lets a request pair `InjectSecret`
with a connection-tagged id. Then a policy row decides what the type could have ruled out.

**Sorting version 1 entries by name.** This would be the denylist again, applied once. The value
is what made an entry a connection. A generic secret that a session happened to name `github`
stays generic.

## Consequences

- The control wire did not change. A connection target still travels as `{scope, name}`, so a
  Manager and a session of different builds still agree.
- A session-scoped generic secret and a connection credential may share a scope and a name. They
  are different entries.
- **A rollback past this change loses every connection credential, unless the file goes back
  too.** A Manager older than this change cannot refuse a version 2 file. It reads `entries`
  only, and its first write saves the file as version 1 with no `connections`. So roll back only
  together with a copy of `secrets.json` taken before the upgrade
  ([deployment.md](../deployment.md#credentials)). This build and every later one refuse a file a
  newer build wrote (`WrittenByNewerBuild`), as `ManagerState` does, so the hazard ends here.
  The store keeps no automatic copy in the data directory: a copy would outlive `Disconnect`,
  and a deleted credential would not be gone.
- An agent that wrote a forged broker envelope with `set_secret` before this change wrote it
  over the real credential. The sort reads that entry as a connection credential, as the broker
  already did. The person connects again. This change stops the next write.
