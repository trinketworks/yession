# A Jumpstarter provider

An MCP server that lends a [Jumpstarter](https://jumpstarter.dev) exporter, and the hardware
behind it, to an agent. Point any MCP client at it and the agent gets five tools: power the
board and call anything else the exporter exports. The console is not a tool. The server
offers it as a stream, and Yession turns that stream into a terminal that people can watch and
type into.

This example is the counterpart to [serial](../serial/). The serial example is written
longhand in this repository's language and owns a device. This example is written in
**Python** on **somebody else's SDK** and owns a **service**. Together they make the claim
that the `examples/` directory exists to make: a provider is not a plugin, it is a server, and
you can write it in whatever language the thing you integrate uses.

## What it demonstrates

**A provider does not have to be in the host's language.** Yession is F# on Node, and
jumpstarter is Python. Neither knows about the other, because the seam between them is HTTP
and a JSON schema. A reimplementation of jumpstarter's gRPC in F# would be a protocol to
maintain forever. This provider uses the jumpstarter SDK in about 600 lines, and upstream
keeps the SDK working.

**Why not `jmp mcp serve`.** Jumpstarter ships an MCP server of its own, and it is the first
thing to try. It does not fit, for two reasons. First, it speaks stdio, and Yession does not
declare servers over stdio. Second, it resolves a *controller-backed* client config (an
endpoint and a token), so it cannot address a direct-mode exporter at all. Both halves would
have to change upstream. Writing the bridge makes the claim and the console stream this
provider's to own.

**Arbitration is yours when the service does not do it.** Jumpstarter arbitrates with
*leases*, and leases live in its Kubernetes controller. A direct-mode exporter has none, so
this server claims the exporter itself: one holder, named in refusals, released when its MCP
session goes quiet. See [Who holds it](#who-holds-it).

**A blocking SDK, on a server that cannot block.** The jumpstarter client is a synchronous
context manager, and so is the serial console inside it. MCP is an event loop that answers
requests. The SDK therefore lives on a thread of its own with a command queue
([src/jumpstarter_provider/exporter.py](src/jumpstarter_provider/exporter.py)), entered once
and exited once. The tools hand work to that thread. The header of that file explains the
arrangement. Reuse this pattern for any provider that wraps a library that was not written for
a server.

**Introspection has to be honest, or it sends the caller somewhere worse.** A driver client
carries thirty names, and six of them are the driver's. The rest is the SDK's plumbing. One
function ([src/jumpstarter_provider/introspect.py](src/jumpstarter_provider/introspect.py))
therefore answers *what does this offer* for both the listing and every refusal. It answers
from the CLASS, because `getattr(client, "status")` runs the getter and `status` is a
property. The header of that file gives both rules and the defects behind them.

The provider also drains streaming methods to their first few items, instead of describing
them as `<generator object …>`. This is what makes `driver_call power read` the answer to
"what is the board doing".

**Bytes do not ride tool calls, so they get a leg of their own.** The console is a stream, and
a stream does not fit a request and a response. The provider therefore has two legs, and each
carries one kind of traffic:

| leg | transport | carries |
|---|---|---|
| control | MCP over HTTP at `/mcp` | the five tools |
| data | WebSocket at `/attach/<token>` | the console's bytes, both ways |

A ticket joins the legs one way. `acquire` returns the ticket in the result's `_meta`:

```json
"_meta": {
  "dev.yession/stream": {
    "url": "ws://127.0.0.1:7334/attach/8f2c…",
    "label": "serial console",
    "renewable": true
  }
}
```

That object is all a provider implements to have its console become a terminal that a person
can watch and type into. It is in `_meta` and not in the content, because a ticket is for the
*client* and not for the model. A client that has never heard of the key ignores it and still
gets the prose. It does not get the console, because the console is not a tool.

[docs/streams.md](../../docs/streams.md) describes the contract behind that URL, including
which two rules are load-bearing and which are courtesies.

*Waiting for a prompt* is most of console interaction. It is a request/response question even
though a console is not. `read_terminal`'s `wait_for` and `wait_for_pattern` answer it on the
terminal, for every backend.

**A console is not a tool, and this is the part worth stealing.** An agent reads the console
through `read_terminal`, on the terminal that this stream becomes. `read_terminal` works
against a durable transcript, not a handle that reading consumes. It anchors to the reader's
own position, so output that the reader already received cannot satisfy a later wait. The write
lease arbitrates who types. These features belong to Yession and not to this provider, and
every backend gets them.

The tools `serial_send`, `serial_read`, and `serial_expect` once needed a tee into a capped
buffer, because the SDK's console is a `pexpect` spawn and whoever reads it consumes it. They
are gone, and nothing was lost.

The lesson generalises: if you build a tee so that your own tools can read alongside the
stream you offer, you reimplement something that the host already does better.

## The tools

| tool | needs the claim | |
|---|---|---|
| `status` | no | can the exporter be reached, what does it export, and who holds it |
| `acquire` | takes it | claim the exporter |
| `release` | yes | give it back, closing the console |
| `power` | yes | `on`, `off`, or `cycle` |
| `driver_call` | yes | any method on any driver in the tree — the general case. Call it with no method to be told what that driver offers |

Every answer is prose, because a model reads it. A refusal names the holder, because "in use,
not broken" is a different instruction from "unavailable".

## Who holds it

There is one claim over the **whole exporter**, not one per driver. Powering the board off cuts
the console out from under anybody else, so per-driver claims would promise an isolation that
the hardware does not keep.

The holder is the **MCP session id**, and the claim follows a live session. Every request
under that id keeps the claim, including the `tools/list` that a client polls with. A client
that goes quiet for `JUMPSTARTER_PROVIDER_TTL` loses the claim. One rule covers both a client
that says goodbye and a client that is killed. A goodbye cannot cover the second case.

## Running it

Start the exporter first, in direct mode (no Kubernetes):

```bash
jmp run --exporter-config exporter.yaml --tls-grpc-listener 127.0.0.1:8815 --tls-grpc-insecure
```

Then start the provider:

```bash
uv run --project examples/jumpstarter jumpstarter-provider
```

```
jumpstarter-provider 0.1.0: MCP at http://127.0.0.1:7334/mcp, exporter at 127.0.0.1:8815
```

The environment configures the provider entirely:

| variable | default | |
|---|---|---|
| `JUMPSTARTER_PROVIDER_PORT` | `7334` | `0` binds an OS-assigned port |
| `JUMPSTARTER_PROVIDER_HOST` | `127.0.0.1` | |
| `JUMPSTARTER_HOST` | `127.0.0.1:8815` | the exporter's gRPC address |
| `JUMPSTARTER_PROVIDER_CONSOLE` | `serial` | which exported driver is the console |
| `JUMPSTARTER_PROVIDER_TTL` | `300` | seconds of client silence that release a claim |
| `JUMPSTARTER_PROVIDER_ORIGIN` | the bound address | the `ws://` origin clients should attach to, when the provider sits behind something |

**Loopback is the only deployment this example is honest about**, for two reasons. The MCP leg
is unauthenticated, and the exporter's gRPC leg runs insecure with no passphrase. If you bind
either leg wider, you hand the hardware to the network.

To use it from Yession, declare `http://127.0.0.1:7334/mcp` in the management UI's MCP form.
This is the same form that declares any third party's server.

## An exporter to point it at

[tests/exporter.yaml](tests/exporter.yaml) is the no-hardware exporter that the suite runs
against. It uses upstream's `MockPower` and a pyserial `loop://` line that echoes what you
write. Real hardware needs the same file with a device in it:

```yaml
export:
  power:
    type: jumpstarter_driver_power.driver.MockPower
  serial:
    type: jumpstarter_driver_pyserial.driver.PySerial
    config:
      url: "/dev/ttyUSB0"
```

Everything that upstream exports works through `driver_call`, without this provider knowing
that it exists. See the [fifty-odd driver
packages](https://jumpstarter.dev/main/reference/package-apis/index.html).

## Testing it

```bash
dotnet fsi tasks.fsx example jumpstarter
```

This command syncs the locked environment, runs the pytest suite against a REAL exporter
(spawned per session, no hardware), and boots the provider to prove that it starts.

The suite is deliberately two-layered, and the second layer is not here:

| where | what only it can prove |
|---|---|
| [tests/](tests/) | the claim, the tools, the console. A hand-written HTTP client drives them, because a client from the same SDK as the server agrees with it by construction |
| `tests/Yession.Tests/Jumpstarter.fs` | that **Yession's own MCP client** can drive this server. Two implementations that never checked against each other can agree. That agreement is the only evidence that either read the protocol right. Runs under the `Jumpstarter` capability |

## The files

| | |
|---|---|
| [src/jumpstarter_provider/main.py](src/jumpstarter_provider/main.py) | argument handling, the environment, and one line of output naming both ends |
| [src/jumpstarter_provider/provider.py](src/jumpstarter_provider/provider.py) | the five tools, the claim, and the liveness that ends one |
| [src/jumpstarter_provider/exporter.py](src/jumpstarter_provider/exporter.py) | the SDK, behind one seam and on one thread |
| [src/jumpstarter_provider/stream.py](src/jumpstarter_provider/stream.py) | the data leg: one drain, two readers, one writer |
