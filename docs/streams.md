# Providing a terminal backend

Yession turns a byte stream into a terminal. The terminal has a panel that people can watch,
a transcript that they can scroll back through and replay in chat, and a write lease that
arbitrates who is typing. You build none of that. **You serve a WebSocket and put the bytes in
binary frames.** Yession provides everything above that. A source gets all of it, whether the
source is a serial port, a `pexpect` handle inside another party's SDK, or a shell on another
machine.

This document is the normative contract. [`app/AttachWs.fs`](../app/AttachWs.fs) is the client
that speaks it. The smallest conforming provider is the hand-written peer in
[`tests/Yession.Tests/Attach.fs`](../tests/Yession.Tests/Attach.fs). The suite runs that peer on
every `check Ports` and tests the client against it, so the peer cannot rot. Read that file if
you prefer code to prose.

## The wire

The wire is WebSocket, for two reasons. First, a provider already runs an HTTP server for its
MCP endpoint, and the upgrade rides the same port. Second, ttyd, gotty, `kubectl exec` and
xterm.js attach all use terminal-over-WebSocket. The seam exists so that another party can
implement the other end.

### MUST

1. **Accept a WebSocket upgrade** at a url that the client can reach.
2. **Binary frames are the bytes, in both directions.** Send what the device says as a binary
   frame. Write each binary frame that you receive to the device. Yession adds no framing of
   its own. Yession decodes the bytes as UTF-8 across the whole stream, not per frame. It holds
   a multi-byte character that a frame boundary splits, and completes it from the frame that
   carries the rest of it. So your frames need not align to characters.
3. **Close the socket when the stream ends.** The close ends the terminal. The frames below
   only say *why*. A provider that sends `exited` and holds the socket open leaves a terminal
   that nobody can reattach, because nothing ended.

These three items are the whole of what the wire requires. Everything below them is quality of
implementation.

### SHOULD

4. **Say why, before closing.** Send a text frame `{"type":"exited","code":N}`, then close.
   Without the frame, the terminal still closes. The person then reads "the stream closed
   without saying why" and not something they can act on.
5. **Do not raise an error when the client vanishes mid-write.** A browser tab can close and a
   session can end. An ordinary goodbye should not become an error in your logs.

### MAY

6. `{"type":"resize","cols":N,"rows":M}` arrives only if you declared `resize` (see
   [capabilities](#capabilities)). The client never sends one to a source that declares
   nothing. A serial line has no size, and a statement that it is 80x24 is a fact nobody could
   check.
7. `{"type":"kill"}` asks you to end the stream early. You may ignore it. The client closes on a
   2s deadline regardless. The frame is the request. The deadline is what happens when nobody
   answers the request.
8. `{"type":"failed","reason":"…"}` says that the stream is ending for a reason other than the
   device finishing. The reason reaches the person verbatim as the terminal's closing reason.
   The `failed` frame takes precedence over `exited` if you send both.
9. **Ignore any text frame you do not recognise.** New control types may appear. An
   implementation that treats an unknown control type as an error will break on the next new
   type.

### How an ending is read

| what the client saw | how the terminal closes |
|---|---|
| `exited` then close | "the stream ended with code N", or "the stream ended" if you declared no exit code |
| `failed` then close | your reason, unchanged |
| close, no frame | "the stream closed without saying why" |

The client describes a source as having exited with a code only if the source declared
`exitCode`. To say "exit 0" about a serial line that merely went quiet would invent the fact
that `exitCode: false` exists to deny.

### One thing that will bite you

**Text frames are control. Binary frames are data.** If you emit device output with your
framework's `send_text`, the terminal shows nothing. The client tries to parse the text as
control JSON and drops it. The client logs this once per connection, so the fault is findable
and not silent. The text does not appear as output.

## Capabilities

A stream is bytes. You *claim* anything else. Claiming nothing is the honest default.

| flag | default | what it buys |
|---|---|---|
| `instrument` | `false` | the OSC 133 bootstrap can be typed in, so output resolves into command blocks with exit codes |
| `resize` | `false` | the client sends you sizes |
| `exitCode` | `false` | the client reports your `exited` code as one |

A serial line is the honest minimum: all three flags are false. A genuine remote pty is the
honest maximum: all three flags are true. It then inherits blocks, exit codes and the agent's
command digest, with no code written in Yession. A model of the interface as "a pty, degraded"
would make every source lie about three things in order to carry bytes.

## Being offered to a session

The wire above is the whole of what a terminal backend does. Yession learns your url in one
way: an MCP tool result carries it in `_meta`.

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
for the model. A url in `structuredContent` lands in the model's context. That gives a prompt
injection a socket address to work with. A client that does not know the key ignores it and
still gets your prose.

### Admission

A session refuses a url unless the url meets all three of these conditions:

- It uses `ws://` or `wss://`.
- It is on the **same host** as the MCP server the operator declared you at (any port).
- It has no credentials (no `user:pass@`).

The session appends the refusal to the answer that the model reads, and names both hosts. This
is a policy and not a boundary. It stops a tool result from pointing a session at another
machine. It does not stop a provider from handing out a socket to something else on the box
that the provider already runs on.

### `renewable`

`renewable: true` means this: **to get another stream, make the same call again with the same
arguments. Making that call again is safe.** It is a claim about your own idempotence, and
nothing verifies it. The default is `false`.

The flag lights the Reattach control on a closed terminal. A reattach is a new terminal and
never a resumed one. A person names a *terminal* and never a url.

### If your resource is exclusive

An exclusive resource has one stream and one reader. If your resource is a device and not
something reproducible, do these steps:

- Mint a **single-use** token and spend it on attach.
- **Suppress the offer entirely** while a stream is live. Otherwise you hand out a second
  reader of a thing that has one.
- **Refuse a replayed token in band.** Accept the upgrade, send `{"type":"exited","code":1}`,
  then close. A close code carries nothing that a client can act on.
- Mint a fresh token on the next acquire. Otherwise `renewable: true` is a lie.

Yession's write lease arbitrates who types *within* one session. Your claim, if you have one,
arbitrates between clients. The two are different locks, and you need both.

### Your own console tools

If you also expose read/write tools over MCP, they are for a client that cannot dial a stream.
While a Yession terminal is attached, the tools should refuse and say where to type.
`write_terminal` and `read_terminal` reach the same console through the lease, where everyone
can see who is typing. The lease exists to prevent two doors onto one console.

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
