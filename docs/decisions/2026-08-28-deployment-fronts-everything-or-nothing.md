# A deployment fronts everything or nothing

> Decided 2026-08-28 · Supersedes the `ManagerOnly` shape, which served the remote management of
> Plan 07 and stayed through Plan 09. Related:
> [src/Yession.Domain/PublicAccess.fs](../../src/Yession.Domain/PublicAccess.fs),
> [deployment.md](../deployment.md) §Addressing,
> [2026-08-27-pr-state-by-polling.md](2026-08-27-pr-state-by-polling.md) — whose webhook reasoning
> leaned on the deleted shape

## Context

`PublicAccess` had a third shape beside loopback and fronted: `ManagerOnly`, with the Manager
fronted and the sessions on loopback.

Nothing branched on it. All four references were or-patterns inside `PublicAccess.fs`. `ManagerOnly`
behaved as `Fronted` for the issuer and as `Loopback` for everything else. That is a seam between
two shapes, not a shape.

The seam produced the one configuration that half-works. It was the only shape that registered
`http://127.0.0.1:<port>/callback` OAuth redirect URIs against a **remote** issuer. The page of the
Manager loads from anywhere, but every session on it is unreachable from the browser that loaded it.
`deployment.md` already told operators "Set **both or neither**". The code honored that in one
direction only: it refused sessions without a Manager.

The shape also fights where deployments are going. Each instance carries its own ingress and egress,
on a cluster, without a single point of failure. Every inbound design sketched against `ManagerOnly`
ended with the Manager relaying bytes to sessions that hide behind it. Under two shapes, that
contortion has nothing to serve. A fronted session is addressable itself, and a loopback one is
deliberately not.

## Decision

`PublicAccess` has two shapes:

- **loopback** (development): nothing configured, Manager and sessions on `127.0.0.1`.
- **fronted** (deployment): `YESSION_MANAGER_URL` and `YESSION_SESSION_URL` both set, everything
  public.

We deleted the third shape, `ManagerOnly`. Boot now refuses a lone `YESSION_MANAGER_URL` and a lone
`YESSION_SESSION_URL`, in either direction. Before, boot refused only the sessions-without-Manager
half. The header of `PublicAccess.fs` states the invariant.

## Alternatives rejected

**A default session template, to make a lone `YESSION_MANAGER_URL` bootable.** A template names the
proxy topology of the operator. A default would be a guess about infrastructure that this process
cannot see.

## Consequences

The Plan 07 workflow no longer boots: manage sessions remotely, and run them on the loopback of the
host. That was the original job of the shape. Anyone who uses it now fronts their sessions too (one
more variable, and the proxy that the Manager already required) or manages locally.

### What would change this decision

- **That workflow returns as a real need.** It then comes back as a deliberate shape. The shape
  needs a stated story for what a remote browser does with a session it cannot reach. It never comes
  back as the accidental result of setting one variable and not the other.
- **A session template with a sane zero-config default.** Suppose sessions acquired a public address
  that is derivable without operator input. The fronted shape could then tolerate a lone
  `YESSION_MANAGER_URL`. The refusal exists because today that default would be a fabrication.
