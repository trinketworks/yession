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
- For more than one person, the directory holds `authorized_keys.d` with one file for each
  person ("The keys of one person").
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
Ubuntu, `pkgs.openssh` in a Nix shell). Nothing starts it. The user of the sandbox is the SSH
login.

### The keys of one person

With one `authorized_keys` file, each key in it opens each sandbox, under each name. When
the host names the person (`listen.mjs --whois`), give the name to `serve`:

```sh
node listen.mjs … --whois 'tailscale whois --json' -- /run/yession-ssh/serve --for '{subject}'
```

```
share/
  serve
  authorized_keys.d/
    ada@example.com      # the keys of this person, one on each line
    bob@example.com
```

- `listen.mjs` puts the subject of the entry in the place of the argument `{subject}`.
- When `authorized_keys.d` is there, `serve --for` reads only the file of that name. A name
  with no file cannot log in, and `serve` says so on its error output.
- When `authorized_keys.d` is not there, `serve` reads `authorized_keys`, with or without
  `--for`.
- The name in the log of the session is then a name whose private key the person has. A
  process that gives the host a false name also needs the key of that person to log in.

### A sandbox that runs as root

As root, `sshd` separates privileges: it changes its root directory and its user before the
login, and changes user again after it. Yession starts a container with few capabilities, and
`sshd` then stops with "Operation not permitted". The operator grants the three capabilities
that `sshd` needs, in the same resource:

```yaml
# resources.yaml
resources:
  remote-ssh:
    mount: { from: /var/lib/yession-ssh/share, at: /run/yession-ssh, mode: read }
    capability: [ SETUID, SETGID, SYS_CHROOT ]
```

- `serve` makes the other things that `sshd` needs as root: the directory `/var/empty`, the
  user `sshd`, and a root account that is not locked. It sets no password. Only a key logs in.
- Without the capabilities, `serve` stops and names them.
- The capabilities apply to each process in the sandbox, not only to `sshd`. With them, root
  in the container can change user and change its root directory. A sandbox that does not
  run as root needs none of them, which is the better design where the image permits it.
- A test with the image `nixos/nix` and OpenSSH 10.3 passed: a command, a terminal, `scp`,
  `sftp` and a local port forward.

### A sandbox with an entrypoint

Each entry runs behind the entrypoint of the sandbox, as each terminal does. When the
entrypoint is slow, each SSH connection is slow by the same time. The sandbox of this
repository has the entrypoint `nix develop --impure --command`, so each connection first
evaluates the dev shell. VS Code opens more than one connection.

`serve` gives the login the `PATH` that the entrypoint made, so a person has the tools of a
terminal of the session. No other variable crosses, because a variable can hold a secret.

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
  -- /run/yession-ssh/serve --for '{subject}'
```

- `--state=mem:` with an ephemeral key removes the node from the tailnet when the script stops.
- The follower stops the whole process group, so `tailscaled` stops with `listen.mjs`.
- Each sandbox needs its own loopback port. The script above uses one fixed port to stay
  short. A real script chooses a free port for each sandbox.
- This recipe is not tested end to end in this repository.

## Recipe B: one port for each sandbox on the node of the host

This recipe is the smaller one. The host is already a node of the tailnet. Each sandbox gets one TCP port of that node, and a
person runs `ssh -p 2201 dev@host`. This recipe needs no new credential.

```sh
#!/bin/sh
set -eu
port=$(/etc/yession-ssh/port-for "$YESSION_SESSION_ID" "$YESSION_ANNOUNCEMENT_NAME")
trap 'tailscale serve --tcp="$port" off' EXIT
tailscale serve --bg --tcp="$port" --proxy-protocol=2 "tcp://127.0.0.1:$port"

node listen.mjs --listen "127.0.0.1:$port" --proxy-protocol \
  --whois 'tailscale whois --json' \
  -- /run/yession-ssh/serve --for '{subject}'
```

- `tailscale serve` changes its configuration only for root when the configuration has a
  UNIX socket target. In that deployment, root makes a fixed set of forwards one time, and
  the script only chooses a port from the set.
- `tailscale serve` does not send the PROXY header to a UNIX socket target. The listener
  must thus be a loopback port, and not a socket in a private directory.

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
  the host that can dial the port can write a header, and thus a name. With one file of
  keys for each person ("The keys of one person"), that process also needs the private key
  of the person to log in. It can still make the session record that the person entered.
- The exit code in `SandboxLeft` is the code of `serve`. It is 0 when a person logged in and
  then disconnected. It is 255 when the login failed or the connection broke, and 78 when
  `serve` could not start `sshd`. It is not the code of a command that the person ran: one
  connection can run more than one command.

## What is not here

- **No recording.** The session records that a person entered and left. It does not record
  what the person did ([GAPS.md](../../docs/GAPS.md)).
- **The environment of the sandbox.** `sshd` starts a login shell with a new environment.
  Only `PATH` crosses. The other variables that the session gave the sandbox are not in it.
- **A Mac host sandbox.** The `sshd` of macOS accepts the key and then stops with "Could not
  create new audit session" when it does not run as root. Use a container sandbox.

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
