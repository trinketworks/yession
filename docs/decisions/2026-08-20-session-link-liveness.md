# A session link proves itself alive, rather than being assumed alive until told otherwise

> Decided 2026-08-20 · Supersedes nothing · Related:
> [src/Yession.Domain/Link.fs](../../src/Yession.Domain/Link.fs),
> [technical-design.md](../technical-design.md) §2.3 "Transport",
> [src/Yession.Domain/Resilience.fs](../../src/Yession.Domain/Resilience.fs) — the policy of the
> history feed. This decision mirrors its injected-clock shape on the other leg

## Context

A message typed on a phone showed `QUEUED · 1` and stayed there until the page was reloaded.

The chain:

1. A backgrounded phone's data channel goes half-open. The DTLS association is gone, but
   `readyState` never leaves `open` and `onclose` never fires.
2. `dc.onclose` was the ONLY liveness signal of the client. `ClientModel.Connection` therefore
   stayed `Connected` for ever, and `channel.Send` accepted and discarded every frame.
3. `SessionLifecycle` reconnects a session that DROPPED. Nothing told it that one had.

The recovery path was already correct and already tested. What was missing was noticing.

## Decision

Every `FrameChannel` that carries a session is wrapped in `Link.supervise` before anything else
holds it. This covers the channel of the browser, the Host, and the Session Process. The wrapper
does three things:

- It answers an inbound `Ping` with `Pong`.
- It sends its own `Ping` on a **1s** tick.
- After **3** ticks with nothing heard from the far end, it declares the link dead. It delivers
  `None` to its consumer and closes the underlying channel.

Death has exactly one expression: a closed channel. Both pumps already handled that. No reconnect
logic was added anywhere.

Supervision is **symmetric**. The Host holds every peer to the same heartbeat that it answers.

### Why 3 seconds

A wrong detection costs different amounts in each direction. A false death costs a reconnect: a
handshake and a full-state push over a channel that is already open. That is cheap and idempotent. A
missed death costs a person their message until they think to reload. That is the bug.

1s × 3 puts detection inside the window where the sender still looks at the screen they sent from.
The traffic is a few bytes a second on a link that already carries keystrokes. `LinkPolicy` in
`Link.fs` also states this reasoning.

### Why death has one expression

A dead link CLOSES because "closed" is the one thing every consumer already handles correctly:

- The pump of the client ends, and `SessionLifecycle` reconnects.
- The pump of the Host ends, and the Host evicts the peer. That releases its terminal leases.

The release of leases closes a correctness gap that nobody reported. A silently-dead peer used to
hold a terminal lease for ever. The idle-lease reclaim only helps when somebody is queued behind it.

## Alternatives rejected

- **The connection state alone.** `RTCPeerConnection` does report `failed`, and #200 wired it. The
  browser now keeps its peer connection. It ends the link the moment either state machine says
  `failed`. That is strictly better than nothing and strictly insufficient. It is the browser's, so
  it does not exist on the Host side or over an in-memory channel. ICE consent freshness can also
  take tens of seconds to give a verdict that the radio of a phone made long before.

  A heartbeat is a property of the CHANNEL. One rule covers every transport the product has and
  every transport a test can build. Both mechanisms ship: the state machine is the fast path where
  it exists, and the heartbeat is the floor everywhere. That is not belt-and-braces. They are one
  mechanism each at two points, and they go red at different times.
- **A liveness report** (a `Health` field, an event, or a callback). Every consumer would then have
  to decide what to do about it.

## Consequences

`LinkPolicy` takes its `Sleep` as a port, exactly as `Resilience.Policy` does. The suite hands it a
hand-advanced clock, so the cheap tier asserts the entire quiet-tick sequence in zero real time. It
needs no WebRTC and no native addon. A half-open transport is a five-line test double (`gaggable`).
It accepts and discards sends, swallows receives, and never signals a close. That is precisely what
the phone did.

### What would change this decision

- **A transport that reports liveness itself, everywhere.** If every channel of the product gained a
  trustworthy liveness signal, the tick would become redundant. It should then go, not sit beside
  the signal.
- **A link that carries something with its own cadence.** Suppose session frames are guaranteed to
  flow at least once per tick. The probe could then ride on them, and the link would not need to
  send it.
- **Battery evidence.** The tick is a wake on a phone. If it measurably costs battery, the answer is
  a longer tick while backgrounded. It is not a heartbeat that stops, which is the bug wearing a
  policy.
