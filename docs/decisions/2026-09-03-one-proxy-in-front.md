# One reverse proxy in front, and the map that feeds it

> Decided 2026-09-03 · Supersedes the per-session `tailscale serve --set-path` binding that
> `deployment.md` §Tailscale used to prescribe · Related: [deployment.md](../deployment.md)
> §Tailscale, [examples/proxy](../../examples/proxy),
> [2026-08-28-deployment-fronts-everything-or-nothing.md](2026-08-28-deployment-fronts-everything-or-nothing.md)

## Context

The deployment that this project runs on drove the per-path API of the ingress for six weeks. A
reconciler ran `tailscale serve --set-path` for each session. It worked, and it was the wrong layer:

- Each change was a CLI call against a daemon. It took 100 ms–1 s, and it needed a watchdog for the
  times it hung.
- The routing lived in the state of tailscaled, where nothing versioned it.
- `serve` cannot rename a header. The one thing that the whole arrangement was for, attributing work
  to the person who did it, needed a second proxy anyway.

Once there is a proxy, the ingress should have one mapping, and the proxy should have the rest.

## Decision

A fronted deployment puts **one reverse proxy of the operator's choosing** between the ingress and
Yession. The ingress (`tailscale serve`, here) does only what nothing else can: it terminates TLS
and establishes who is calling. It does this through a single mapping to the proxy. The proxy routes
`/` to the Manager and `/s/<id>` to the port of each session. It also translates the identity
headers of the ingress into the canonical `x-yession-*` set. It does that in the one place that can
also strip what a client sent.

Yession's part is two documented facts and one example:

- The facts: the registry stream, and the two placeholders that a session template uses.
- The example (`examples/proxy`) has two parts. One is a proxy-agnostic process that renders the
  stream through a template into one file. The other is a Caddyfile that reads that file and does
  the translation.

A deployment copies and owns both. The bins do not ship them.

### The contract

For a proxy to front Yession, it needs:

1. One upstream for the Manager, and the origin of the Manager in `YESSION_MANAGER_URL`.
2. One upstream per running session, at whatever `YESSION_SESSION_URL` promises. The port comes from
   `/sessions/stream`. The path or host comes from the template. `examples/proxy/main.mjs` is the
   reference reconciler, and a deployment may write its own against the same stream.
3. Under `--auth trusted-headers`: SET `x-yession-user` (and the optional name, email, picture,
   claims) from what the ingress verified. DELETE every `x-yession-*` that a client could have sent
   and that the proxy does not set. Be the only non-loopback path to the Manager.
4. Loopback callers inside that boundary (the map, a health check, a tracker) assert a subject of
   their own on management routes. The gate is the same for them.

[deployment.md](../deployment.md) §Tailscale describes what the example implements, for a deployment
on another proxy.

## Alternatives rejected

- **Relay session traffic through the Manager.** The Manager stays a supervisor and an issuer, and a
  session is addressable itself. A relay would make every proxy configuration a one-liner. The
  fronts-everything decision already declined that shape, for the reason it gave there: it fights
  where deployments are going.
- **Drive the per-path API of the ingress.** The Context above gives the reasons.

## Consequences

Every request makes a second hop, and the deployment runs one more long-running process (the proxy)
plus one small one (the map). In return, the ingress configuration collapses to a line. The routing
and the identity rules live in a file under version control. Adding a session is one atomic write
that the proxy picks up on its own.

Anyone who ran the per-session `serve` binding re-points its one Manager mapping at the proxy and
retires the reconciler. The URLs that you gave the Manager do not change.

### What would change this decision

- **Sessions on stable addresses.** Suppose a session listened where its id alone said, such as a
  Unix socket under the data directory. A proxy could then dial it from the request path with no map
  at all, and `main.mjs` would have nothing to do. This decision points at that follow-up. Today the
  OS assigns the port of a session per launch, so the map is the honest minimum.
- **An ingress that translates headers.** Suppose `serve` (or its successor) could rename a header.
  A deployment that wants attribution and nothing else could then go back to a single hop. The map
  would still serve the session routes, so the proxy would still be the simpler place.
