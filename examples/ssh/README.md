# SSH into a sandbox

These pieces let a person connect to a sandbox of a session with a plain `ssh` client. VS Code
Remote-SSH, `scp` and `sftp` then work too. The person installs nothing.

Yession holds no SSH, no tunnel and no VPN. It gives a deployment two documented facts
([deployment.md](../../docs/deployment.md)):

- A session announces each sandbox that it has on the registry stream at `/sessions/stream`.
- A session can have a door, and `yession-enter` runs one command in one sandbox through it.

A deployment puts its own network in front. This directory has the three pieces that are the
same for each network.

| | |
|---|---|
| [follow.mjs](follow.mjs) | Follows the registry stream. It keeps one child process for each announcement that matches, and stops the child when the announcement goes away. It is not about SSH: it serves each kind of announcement. |
| [listen.mjs](listen.mjs) | Listens on a port of the host. For each connection it runs one `yession-enter`, with the connection as the input and the output. It can read the PROXY protocol and ask the host who the peer is. |
| [serve](serve) | Runs in the sandbox as the command of an entry. It starts `sshd -i`, which serves one connection that is already open. |

## The shape

```
ssh client ──▶ ingress ──▶ listen.mjs ──▶ yession-enter ══▶ door ──▶ session ──▶ serve ──▶ sshd -i
 (a person)   (yours)    one for each      one for each     (a socket)           (in the sandbox)
                           sandbox          connection
                              ▲
                          follow.mjs ◀── /sessions/stream
```

No daemon runs in the sandbox, and nothing in the sandbox listens on a port. One SSH
connection is one entry. The entry ends when the connection ends.

## What each owner supplies

**The operator** supplies one resource and the processes on the host:

```yaml
# resources.yaml
resources:
  remote-ssh:
    mount: { from: /var/lib/yession-ssh/share, at: /run/yession-ssh, mode: read }
```

- The directory holds `serve` and `authorized_keys`. One public key on each line, as
  `sshd` reads them. `https://github.com/<login>.keys` gives the keys of a GitHub account.
- The Manager runs with `YESSION_SESSION_DOORS=<directory>`, so each session has a door.
- `follow.mjs` runs as a service that restarts.

**The repository** selects the resource for the sandbox that people can enter. This line is
the consent of the repository:

```yaml
# yession.yaml
sandboxes:
  dev:
    wants: [ remote-ssh ]
```

**The image** of that sandbox holds the OpenSSH server (`openssh-server` on Debian and
Ubuntu). Nothing starts it. The sandbox runs as a user that is not root, and that user is the
SSH login. `serve` refuses to run as root.

## Recipe A: one tailnet node for each sandbox

Each sandbox gets its own name on the tailnet, and a person runs `ssh dev@dev-k36a1eyg`. The
host needs a tailnet credential that can add an ephemeral node with a tag, and the policy of
the tailnet gives people port 22 of that tag.

```sh
node follow.mjs --manager unix:/var/lib/yession/manager.sock --as ssh-follower \
  --kind sandbox --where state=running --where resources~remote-ssh \
  --run /etc/yession-ssh/node
```

`/etc/yession-ssh/node` is a script of the deployment. It runs one time for each sandbox:

```sh
#!/bin/sh
set -eu
state=$(mktemp -d)
ts() { tailscale --socket="$state/ts.sock" "$@"; }
name="${YESSION_ANNOUNCEMENT_NAME##*:}-$(printf %s "$YESSION_SESSION_ID" | cut -c1-8 | tr A-Z a-z)"

tailscaled --tun=userspace-networking --state=mem: --socket="$state/ts.sock" &
ts up --auth-key="$(cat /etc/yession-ssh/auth-key)" --hostname="$name" --advertise-tags=tag:sandbox
ts serve --bg --tcp=22 --proxy-protocol=2 tcp://127.0.0.1:2200

exec node listen.mjs --listen 127.0.0.1:2200 --proxy-protocol \
  --whois "tailscale --socket=$state/ts.sock whois --json" \
  -- /run/yession-ssh/serve
```

- `--state=mem:` with an ephemeral key removes the node from the tailnet when the script stops.
- The follower stops the whole process group, so `tailscaled` stops with `listen.mjs`.
- Each sandbox needs its own loopback port. The script above uses one fixed port to stay
  short. A real script chooses a free port for each sandbox.
- This recipe is not tested end to end in this repository.

## Recipe B: one port for each sandbox on the node of the host

The host is already a node of the tailnet. Each sandbox gets one TCP port of that node, and a
person runs `ssh -p 2201 dev@host`. This recipe needs no new credential.

```sh
#!/bin/sh
set -eu
port=$(/etc/yession-ssh/port-for "$YESSION_SESSION_ID" "$YESSION_ANNOUNCEMENT_NAME")
trap 'tailscale serve --tcp="$port" off' EXIT
tailscale serve --bg --tcp="$port" --proxy-protocol=2 "tcp://127.0.0.1:$port"

node listen.mjs --listen "127.0.0.1:$port" --proxy-protocol \
  --whois 'tailscale whois --json' \
  -- /run/yession-ssh/serve
```

`port-for` is a rule of the deployment that gives each sandbox a port and tells people which
port it is. A person must look up the port, which is the cost of this recipe.

## Who entered

The session records two events for each connection: `SandboxEntered` and `SandboxLeft`. The
first event has the subject and the label that `yession-enter` got as `--as` and `--via`.

- The subject is the word of the caller. Yession does not check it. Each process that can
  dial the door can claim each name, so the permissions of the door directory are the trust.
- `sshd` runs in the sandbox, and the sandbox can lie. The name thus comes from the host.
  `listen.mjs --proxy-protocol --whois` gets it from the ingress.
- With `--whois`, `listen.mjs` closes a connection whose peer the host does not know. With no
  `--whois`, each entry has the subject of `--as`.
- The loopback port of `listen.mjs` has no PROXY header check of its own. Each process on
  the host that can dial the port can write a header. The key login of `sshd` still applies.

## What is not here

- **No recording.** The session records that a person entered and left. It does not record
  what the person did ([GAPS.md](../../docs/GAPS.md)).
- **The environment of the sandbox.** `sshd` starts a login shell with a new environment. The
  variables that the session gave the sandbox are not in it, unless the shell profile of the
  image sets them.
- **A Mac host sandbox.** The `sshd` of macOS accepts the key and then stops with "Could not
  create new audit session" when it does not run as root. Use a container sandbox.
- **A sandbox that runs as root.**

## Try it with no deployment

`listen.mjs` and `serve` need only a command that connects its input and output to `serve`.
On a Linux host with Docker, `docker exec` is that command:

```sh
printf '#!/bin/sh\nwhile [ "$1" != -- ]; do shift; done; shift\nexec docker exec -i my-container "$@"\n' > enter
chmod +x enter
node listen.mjs --listen 127.0.0.1:2222 --door none --sandbox my-container --enter ./enter \
  -- /run/yession-ssh/serve
ssh -p 2222 dev@127.0.0.1
```
