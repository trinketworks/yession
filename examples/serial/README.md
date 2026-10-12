# A serial device provider

An MCP server that lends the host's serial devices to an agent. Point any MCP client at it,
and the agent gets four tools and a byte stream to a real tty. Yession is one such client, but
nothing here knows that.

This example is the smallest complete answer to a question that keeps coming up: **what does
an integration look like, and what do I have to build myself?**

## What it demonstrates

**A provider is an ordinary HTTP server.** It needs no framework and no SDK, only ~150 lines
of protocol. The control leg is MCP over Streamable HTTP at `/mcp`: `initialize`, `tools/list`,
`tools/call`, and a `DELETE` that ends the session. A tool server owes a client nothing more.

**Some resources need a process of their own.** `/dev/ttyACM0` cannot speak MCP, and the OS
hands out exactly one file descriptor for it. Something has to own that claim. If the agent
runtime owns it, the runtime holds a device claim it has no business holding. If each session
owns it, two sessions race for the same fd and get `EBUSY` instead of "session A has it". The
owner is therefore a separate process, and arbitration is a first-class part of the protocol:
`acquire_device` names the current holder when it refuses.

**Tools are not always enough.** A device is a stream, and a request/response tool call cannot
carry one. So there are two legs:

| leg | transport | carries |
|---|---|---|
| control | MCP over HTTP at `/mcp` | list, acquire, configure, release |
| data | WebSocket at `/attach/<token>` | the bytes, both ways |

The attach ticket joins the legs **one way**: `acquire_device` answers with a URL, and the
client opens it. Nothing flows back. A terminal that closes does not disturb the control leg,
and a control leg that drops does not close a terminal that is already streaming.

**Say it twice, to two audiences.** The URL is in the prose, because a model reads prose. It
is also in the result's `_meta`, because a *client* is what dials it:

```json
"_meta": {
  "dev.yession/stream": {
    "url": "ws://127.0.0.1:7333/attach/8f2c…",
    "label": "QinHeng CH340 /dev/ttyACM0",
    "renewable": true
  }
}
```

That object is all a provider implements to have its stream become a terminal that somebody
can watch and type into. Each choice has a reason:

- `_meta` and not `structuredContent`: the ticket is for the client and not the model.
- `capabilities` left out entirely: the default is "bytes and nothing else", which is what a
  serial line honestly is.
- `renewable`: acquiring a device you already hold mints a fresh token once the last stream has
  ended, so "ask again" gets you another one.

A client that has never heard of the key ignores it and still gets the prose. The contract
behind the URL is [docs/streams.md](../../docs/streams.md).

**A claim needs a lifetime.** MCP sessions are the lifetime. A claim dies with the session that
took it, which stops a crashed client from holding hardware forever. This is why the server is
stateful: `Mcp.OnSessionEnded` is where the device goes back.

**Degrade, do not fail.** `serialport` is a native addon and a *lazy dynamic import*, so a
machine without it answers "no devices" and does not refuse to start. The engine is a seam
(`Ports.SerialEngine`), which also lets every test run with no hardware.

**Offer less than you can see.** `list_devices` returns only devices in a known vendor/product
table. The provider drops an unrecognised port and does not offer it under its raw path,
because `/dev/ttyS0` is a serial port and on many machines it is the system console. To hand
an agent every tty on the box is to hand it the console.

## Running it

```bash
node dist/main.js
```

```
serial-provider 0.1.0: MCP at http://127.0.0.1:7333/mcp, streams at ws://127.0.0.1:7333/attach/<token>
```

The environment configures the provider entirely:

| variable | default | |
|---|---|---|
| `YESSION_SERIAL_PORT` | `7333` | `0` binds an OS-assigned port |
| `YESSION_SERIAL_HOST` | `127.0.0.1` | |
| `YESSION_SERIAL_ORIGIN` | the bound address | the `ws://` origin clients should attach to, when the provider sits behind something |

**Loopback is the only deployment this example is honest about.** The control leg is
unauthenticated. If you bind it to anything but loopback, you hand the host's serial ports to
the network.

To use it from Yession, declare `http://127.0.0.1:7333/mcp` in the management UI's MCP form.
This is the same form that declares any third party's server. The product has no
serial-specific path, and this is intended.

## Building it

Run this from the repository root, where the build interface lives:

```bash
dotnet fsi tasks.fsx example serial
```

This command compiles the F# to JavaScript with Fable and bundles it to `dist/main.js` with
esbuild. It keeps `serialport` external, so its native addon resolves at run time.

## Adding hardware it does not know

`Discovery.known` in [src/Serial.fs](src/Serial.fs) is a table of USB vendor/product pairs.
Hardware that is not in the table is invisible, not mislabelled. To add hardware, add one line:

```fsharp
entry "1a86" "7523" "QinHeng" "CH340"
```

Find the pair with `ioreg -p IOUSB -l` on macOS, or `lsusb` on Linux.

## The files

| | |
|---|---|
| [src/Serial.fs](src/Serial.fs) | the vocabulary: devices, ids, line settings, the tool wire. No IO. |
| [src/Interop.fs](src/Interop.fs) | the Node surface, bound: http, the upgraded socket, a hash, a `stat`, a timer. Port this one to another host, keep the rest. |
| [src/Mcp.fs](src/Mcp.fs) | MCP over Streamable HTTP, longhand. |
| [src/Ws.fs](src/Ws.fs) | RFC 6455, only the frames the attach protocol needs. |
| [src/Ports.fs](src/Ports.fs) | the `serialport` binding, and the seam over it: enumerate, open, stream, close. |
| [src/Provider.fs](src/Provider.fs) | the four tools, the claims, and the two legs joined. |
| [src/Main.fs](src/Main.fs) | argument handling, and one line of output naming both legs. |

The tests are in `tests/Yession.Tests`, with the rest of the suite. The pure tests run in the
cheap tier. A `Serial`-tagged suite drives a real pty through `socat`.
