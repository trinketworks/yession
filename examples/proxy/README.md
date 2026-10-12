# A reverse proxy in front

These pieces put one reverse proxy of your own in front of a Yession deployment. Everything
outside the machine then reaches the Manager and every session through a single address. The
identity that the caller's authenticator established arrives at the Manager as the canonical
`x-yession-*` headers that it reads.

Nothing here knows about Yession beyond two documented facts
([deployment.md](../../docs/deployment.md)): the registry stream at `/sessions/stream`, and the
two placeholders, `{id}` and `{port}`, that a session template uses. A deployment in another
environment keeps `main.mjs` and swaps the Caddyfile.

| | |
|---|---|
| [main.mjs](main.mjs) | Follows the registry stream and renders every running session through a template into one file, atomically, level-based. Proxy-agnostic: the template is the proxy's own syntax. |
| [caddy/Caddyfile](caddy/Caddyfile) | One Caddy site behind `tailscale serve`: sessions from the file above, the Manager for everything else, and the Tailscale identity headers translated on the way in. |

## The shape

```
browser ──tailnet──▶ tailscale serve ──▶ caddy :9000 ──┬──▶ manager :8321      /
   (TLS, identity)     one mapping        (this dir)     ├──▶ session  :51321    /s/<id>
                                                         └──▶ session  :57626    /s/<id>
                                              ▲
                                    sessions.caddy  ◀── main.mjs ◀── /sessions/stream
```

`serve` does what only it can: it terminates TLS on the tailnet and says who is calling. It
holds exactly one mapping. The proxy holds everything that a person would want to read or
change: which paths go where, what becomes of the identity, and what is stripped. A new
session touches only the map file. The map is a file, not a sequence of commands against the
ingress, because a file is one atomic write and Caddy re-reads it on its own under `--watch`.

## Running it

Start the Manager, fronted at the tailnet origin and trusting the headers that the proxy
asserts:

```sh
YESSION_MANAGER_URL=https://host.example.ts.net:8321 \
YESSION_SESSION_URL=https://host.example.ts.net:8321/s/{id} \
  yession-manager --auth trusted-headers
```

Run the map, so that it follows the Manager. Under `trusted-headers`, a header-less subscriber
gets a 401 and no frames. The map therefore asserts a subject of its own, from inside the
loopback trust boundary that the proxy defines. `--as` names the subject. Under `localhost`,
nothing reads the header:

```sh
node main.mjs --manager http://127.0.0.1:8321 --as proxy-map \
  --out /var/lib/yession/proxy/sessions.caddy \
  --empty '# no running sessions' \
  --template '@s_{id} path /s/{id} /s/{id}/*
handle @s_{id} {
	reverse_proxy 127.0.0.1:{port}
}'
```

`--empty` is what the file says when nothing runs. Caddy warns about an empty import, and
under `--watch` it warns once a second for as long as the deployment is idle. A comment is a
file with nothing in it, and no proxy minds it.

Run the proxy with `--watch`, so that a rewritten map is live within a second:

```sh
YESSION_PROXY_PORT=9000 \
YESSION_PROXY_MANAGER=127.0.0.1:8321 \
YESSION_PROXY_SESSIONS='/var/lib/yession/proxy/sessions*.caddy' \
  caddy run --config caddy/Caddyfile --adapter caddyfile --watch
```

The proxy listens on loopback. Set `YESSION_PROXY_BIND` to change that, but read the
Caddyfile's reason first. Anything that can dial the proxy directly can write its own
`Tailscale-User-Login`, so only `serve` may.

Last, make the ingress once. The `serve` config persists in tailscaled's own state:

```sh
tailscale serve --bg --https=8321 9000
```

A proxy that does not watch its own config needs `--reload '<command>'` on `main.mjs`.
`main.mjs` runs the command after every write, for example `nginx -s reload`.

## What it demonstrates

The rules that this example follows are written down once, next to what they govern:

- **The map is level-based, and each kind of silence from the stream means something
  different.** The header comment of [main.mjs](main.mjs) states the reasons.
  [deployment.md](../../docs/deployment.md#keeping-the-session-routes-in-step) gives the three
  rules for any reconciler.
- **The identity translation has one home, and an ordering trap.** The comments in
  [caddy/Caddyfile](caddy/Caddyfile) explain both.
  [deployment.md](../../docs/deployment.md#tailscale) describes the translation for a
  deployment on another proxy. It also states that sessions get no identity.

[deployment.md](../../docs/deployment.md#tailscale) records the verification against a live
tailnet. Two suites re-verify the Caddy half on every pull request, against whichever Caddy
the dev shell pins. `tests/Yession.Tests/ProxyMap.fs` drives `main.mjs` against a real Manager
and a real session. The fronted case in `tests/Yession.Tests/Browser.fs` runs this Caddyfile
for real, with Chromium standing in for `serve`. The header comment of each file says what it
covers.
