# Providing a terminal backend

Yession turns a byte stream into a terminal: a panel people can watch, a transcript they can
scroll back through and replay in chat, and a write lease that arbitrates who is typing. **You
serve a WebSocket and put the bytes in binary frames.** Yession provides the rest, whether the
source is a serial port, a `pexpect` handle inside another party's SDK, or a shell on another
machine.

This document is the normative contract. [`app/AttachWs.fs`](../app/AttachWs.fs) is the client
that speaks it. The smallest conforming provider is the hand-written peer in
[`tests/Yession.Tests/Attach.fs`](../tests/Yession.Tests/Attach.fs), which every `check Ports`
tests the client against.

## The wire

The wire is WebSocket: the upgrade rides the port of your MCP endpoint, and ttyd, gotty,
`kubectl exec` and xterm.js attach all use terminal-over-WebSocket.

### MUST

1. **Accept a WebSocket upgrade** at a url that the client can reach.
2. **Binary frames are the bytes, in both directions.** Send what the device says as a binary
   frame. Write each binary frame that you receive to the device. Yession adds no framing of
   its own. It decodes UTF-8 across the whole stream, not per frame, so your frames need not
   align to characters.
3. **Close the socket when the stream ends.** The close ends the terminal. The frames below
   only say *why*. A provider that sends `exited` and holds the socket open leaves a terminal
   that nobody can reattach, because nothing ended.

The wire requires these three items and nothing else. The rest is quality of implementation.

### SHOULD

4. **Say why, before closing.** Send a text frame `{"type":"exited","code":N}`, then close.
   Without it, the person reads "the stream closed without saying why".
5. **Do not raise an error when the client vanishes mid-write.** A tab closing or a session
   ending is an ordinary goodbye, not an error in your logs.

### MAY

6. `{"type":"resize","cols":N,"rows":M}` arrives only if you declared `resize` (see
   [capabilities](#capabilities)).
7. `{"type":"kill"}` asks you to end the stream early. You may ignore it. The client closes on a
   2s deadline regardless.
8. `{"type":"failed","reason":"…"}` says that the stream is ending for a reason other than the
   device finishing. The person reads the reason verbatim as the terminal's closing reason.
   `failed` takes precedence over `exited` if you send both.
9. **Ignore any text frame you do not recognise.** New control types may appear, and an
   implementation that refuses them breaks on the next one.

### How an ending is read

| what the client saw | how the terminal closes |
|---|---|
| `exited` then close | "the stream ended with code N", or "the stream ended" if you declared no exit code |
| `failed` then close | your reason, unchanged |
| close, no frame | "the stream closed without saying why" |

### One thing that will bite you

**Text frames are control. Binary frames are data.** If you emit device output with your
framework's `send_text`, the terminal shows nothing: the client drops text that is not control
JSON, and logs it once per connection.

## Capabilities

A stream is bytes. You *claim* anything else. Claiming nothing is the honest default.

| flag | default | what it buys |
|---|---|---|
| `instrument` | `false` | the OSC 133 bootstrap can be typed in, so output resolves into command blocks with exit codes |
| `resize` | `false` | the client sends you sizes |
| `exitCode` | `false` | the client reports your `exited` code as one |

A serial line is the honest minimum: all three flags are false. It has no size, and nothing
could check a claim that it is 80x24 or that it exited 0. A genuine remote pty is the honest
maximum: all three flags are true. It then inherits blocks, exit codes and the agent's command
digest, with no code written in Yession.

## Being offered to a session

Yession learns your url in one way: an MCP tool result carries it in `_meta`.

```json
"_meta": {
  "dev.yession/stream": {
    "url": "ws://127.0.0.1:7334/attach/8f2c…",
    "label": "serial console",
    "renewable": true,
    "capabilities": { "resize": true }
  }
}
```

Only `url` is required. Malformed `_meta` is no offer, and it never fails the call.

The url goes in `_meta` and not in the content, because a ticket is for the **client** and not
for the model. A url in `structuredContent` gives a prompt injection a socket address. A client
that does not know the key ignores it and still gets your prose.

### Admission

A session refuses a url unless the url meets all three of these conditions:

- It uses `ws://` or `wss://`.
- It is on the **same host** as the MCP server the operator declared you at (any port).
- It has no credentials (no `user:pass@`).

The session appends the refusal, naming both hosts, to the answer the model reads. This is a
policy and not a boundary. It stops a tool result from pointing a session at another machine,
not a provider from handing out a socket to something else on its own box.

### `renewable`

`renewable: true` means: **to get another stream, make the same call again with the same
arguments, and that is safe.** Nothing verifies this claim about your idempotence. The default
is `false`. The flag lights the Reattach control on a closed terminal. A reattach is a new
terminal and never a resumed one. A person names a *terminal* and never a url.

### If your resource is exclusive

If your resource is a device with one stream and one reader, and not something reproducible:

- Mint a **single-use** token and spend it on attach.
- **Suppress the offer entirely** while a stream is live.
- **Refuse a replayed token in band.** Accept the upgrade, send `{"type":"exited","code":1}`,
  then close. A close code carries nothing that a client can act on.
- Mint a fresh token on the next acquire. Otherwise `renewable: true` is a lie.

Yession's write lease arbitrates who types *within* one session. Your claim arbitrates between
clients. You need both locks.

### Your own console tools

If you also expose read/write tools over MCP, they are for a client that cannot dial a stream.
While a Yession terminal is attached, they should refuse and say where to type:
`write_terminal` and `read_terminal` reach the same console through the lease, where everyone
can see who is typing.

## Checking yours

Point the expectations in `tests/Yession.Tests/Attach.fs` at your implementation, or check this
list:

- [ ] the upgrade succeeds and the socket stays open
- [ ] device output arrives as **binary** frames
- [ ] a binary frame sent to you reaches the device
- [ ] an unknown text frame is ignored rather than fatal
- [ ] `kill` (if honoured) ends the stream rather than the process behind it
- [ ] the stream ends with `exited` **and then a close**
- [ ] a client that disappears mid-write does not raise
- [ ] if exclusive: a replayed token is refused in band, and no offer is made while attached
