# Patch libdatachannel's libnice ICE teardown rather than switch ICE backend

> Decided 2026-07-26 · Supersedes nothing · Related:
> [nix/libdatachannel-nice-teardown.patch](../../nix/libdatachannel-nice-teardown.patch),
> [nix/packages.nix](../../nix/packages.nix), [AGENTS.md](../../AGENTS.md) "Testing"

## Context

The `Native`-tagged suites crashed with an intermittent SIGSEGV. The fault is a use-after-free
inside the libnice ICE backend of libdatachannel. It is not in any Yession code.

### The fault

`~IceTransport()` detaches the receive callback and removes the nice stream from whatever thread
destroys it. libnice delivers receives on a single shared glib main loop. It also emits its
`component-state-changed`, `new-candidate-full` and `candidate-gathering-done` signals there, all
bound to `this`. Nothing synchronises the two. A receive that the loop is *already dispatching* runs
against a half-destroyed transport whose stream is being removed underneath it.

gdb caught the crash on the 18th run:

```
Thread 32 "MainThread" received signal SIGSEGV, Segmentation fault.
#0  __memmove_evex_unaligned_erms ()                          libc
#1  rtc::make_message<std::byte*>(...)                        libdatachannel
#2  rtc::impl::IceTransport::RecvCallback(_NiceAgent*, ...)   libdatachannel  (icetransport.cpp:881)
#3  nice_component_emit_io_callback ()                        libnice
#4  component_io_cb ()                                        libnice
#5  socket_source_dispatch ()                                 libgio
#6  g_main_context_dispatch_unlocked ()                       libglib
#8  g_main_loop_run ()                                        libglib
```

The faulting thread is the loop of libnice, not JS. Two earlier attempts narrowed the window without
closing it: draining close callbacks before global cleanup (`0b1b8ae8`) and awaiting the `closed`
state of libdatachannel (`169faa61`). Both sequence the *JS-side* teardown correctly. This race
lives below anything that JS ordering can reach.

### The libnice backend was never a deliberate choice

`nix/node-datachannel.nix` patches out the GitHub `FetchContent` of upstream and links the
libdatachannel of nixpkgs instead. nixpkgs builds it with `-DUSE_NICE=ON -DPREFER_SYSTEM_LIB=ON
-DNO_EXAMPLES=ON`. libdatachannel's own default is `option(USE_NICE "Use libnice instead of
libjuice" OFF)`. Upstream node-datachannel ships libjuice. There, libnice is opt-in behind a
separate `install:nice` script. The source swap silently moved us onto a backend that upstream
neither ships nor tests.

### Crash rates before the fix

All rates are from this container:

| Configuration | Crashes |
|---|---|
| `check Browser Ports Native Keyring` | 1 / 5 |
| bare `node Main.js`, caps `Ports Native` | 0 / 12 |
| bare `node Main.js` under gdb, full caps + D-Bus/keyring | 1 / 18 |

The rate is ~2–5%, and the crash site moves (Phase4 Step 24 once, the Phase2 acceptance E2E another
time). This is a teardown race, not one bad test.

## Decision

We carry a one-file patch to libdatachannel. It serialises ICE-transport teardown onto the glib main
loop of libnice. We do **not** switch to libjuice.

The patch runs the detach *on* the loop thread and waits for it. A `GMainContext` dispatches from
one thread at a time, so while the teardown runs the loop cannot deliver anything. The patch also
disconnects the signal handlers there. `g_main_context_invoke_full` runs the function inline when
the caller already owns the context, so destroying from inside a libnice callback cannot deadlock.
The header of the patch states the full reasoning.

### Evidence

A soak alone could not settle the question. At a 1-in-18 rate, 25 clean runs happen ~24% of the time
by luck. A stress test that hits the race directly gives a causal answer:

| Build | 5 × 300 teardown rounds |
|---|---|
| Unpatched libnice | **3/5 SIGSEGV** — mid-loop at rounds 150, 225, 175 |
| Patched | **5/5 clean**, 1500 rounds |

The patched build also passed 25/25 clean runs of the configuration that crashed 1 in 18, and
`check Browser Ports Native Keyring` (252 Node + 26 CLR) was green.

## Alternatives rejected

### libjuice

Building libdatachannel with `-DUSE_NICE=OFF` removes the faulty code path instead of patching it,
and matches what upstream ships. We implemented it and verified it (25/25 clean). We rejected it
because of latency. This is the same two-peer non-trickle handshake on the same box:

```
libnice    93ms  95ms  92ms  93ms  93ms
libjuice  1014  1015  1013  1013    66ms
```

Gathering is not the cause (it completes at 10ms). DTLS is. The answerer reaches ICE-connected at
14ms and sends its ClientHello immediately. The offerer's agent does not finish its checks until
~70ms, so it drops the packet. The packet is then retransmitted on the 1s timer of DTLS. An 11x
regression on session open, for a local-first app, was the worse trade.

libjuice also needed more machinery. nixpkgs has no libjuice at all, so it meant packaging it
(pinned to the own `deps/libjuice` submodule of libdatachannel v0.24.1). It also needed
`-DENABLE_LOCALHOST_ADDRESS=ON` to restore the loopback candidates of libnice. Without that flag, a
host whose only interface is loopback cannot connect.

That work is complete and reachable at commit `9ed17a2` (force-pushed off `claude/run-tests-y4qp1b`,
not in the history of master).

## Consequences

### A separate crash this uncovered

The first version of the stress test called `ndc.cleanup()` at the end. **Both** builds survived all
300 teardown rounds and then crashed at that global cleanup. The first A/B therefore measured the
wrong thing, and we rebuilt the test without the call. This independently explains why
`tests/Yession.Tests/Client.fs` sequences its teardown. The global cleanup of node-datachannel still
aborts when objects have not yet reported `closed`. It does so on patched and unpatched builds
alike. That crash is out of scope here. Do not call `Interop.cleanup ()` with live native objects
behind it.

### What would change this decision

- **nixpkgs packages libjuice**, or libdatachannel there defaults to it. The patch then becomes
  unnecessary maintenance.
- **A libdatachannel bump** in nixpkgs. The patch is against `src/impl/icetransport.cpp` at v0.24.1
  and needs re-checking. It fails loudly, not silently: a failed `patches` application breaks the
  build.
- **Upstreaming the patch.** We have not submitted it. The default backend of upstream does not have
  this race, so the fix only matters to `USE_NICE=ON` builds like the one in nixpkgs.
- **The DTLS stall of libjuice is fixed or tuned away.** The backend swap then dominates, because
  deleting a code path beats patching one.

## Reference: the stress harness

We did not commit the harness as a suite. It takes ~30s and depends on timing, which cuts against
the no-flaky-tests rule in AGENTS.md. We record it here so you can repeat the verification. Run it
from the repo root inside `devenv shell`, because it resolves `node-datachannel` from
`node_modules`. To test an alternative build, symlink the package of that derivation into a scratch
`node_modules` and run from there.

```js
// stress-teardown.mjs — hammer the teardown race directly: connect a pair, get data flowing,
// then destroy both peers WHILE receives are in flight — exactly the window where libnice's
// glib loop can be dispatching a receive into a transport being torn down.
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
const ndc = require('node-datachannel');

const ROUNDS = Number(process.argv[2] ?? 200);
const payload = 'x'.repeat(4096);

const gathered = (pc) =>
  new Promise((res) => pc.onGatheringStateChange((s) => { if (s === 'complete') res(pc.localDescription()); }));

for (let i = 1; i <= ROUNDS; i++) {
  const offerer = new ndc.PeerConnection(`o${i}`, { iceServers: [] });
  const answerer = new ndc.PeerConnection(`a${i}`, { iceServers: [] });

  const offerReady = gathered(offerer);
  const dc = offerer.createDataChannel('session');
  const opened = new Promise((res) => dc.onOpen(res));

  let remote = null;
  answerer.onDataChannel((c) => {
    remote = c;
    c.onMessage(() => { try { c.sendMessage(payload); } catch {} });
  });

  const offer = await offerReady;
  const answerReady = gathered(answerer);
  answerer.setRemoteDescription(offer.sdp, offer.type);
  const answer = await answerReady;
  offerer.setRemoteDescription(answer.sdp, answer.type);
  await opened;

  // Saturate both directions, then tear down mid-flight — no waiting for closed.
  const pump = setInterval(() => { try { dc.sendMessage(payload); } catch {} }, 0);
  for (let k = 0; k < 40; k++) { try { dc.sendMessage(payload); } catch {} }
  await new Promise((r) => setTimeout(r, 15));
  clearInterval(pump);

  offerer.close();
  answerer.close();
  if (remote) { try { remote.close(); } catch {} }

  if (i % 25 === 0) console.log(`round ${i} ok`);
}

// Deliberately NO ndc.cleanup(): the global teardown racing objects that have not yet reported
// closed is a separate, known-unsupported pattern (see Client.fs). Exit hard so anything that
// crashes here is attributable to per-connection teardown.
console.log(`survived ${ROUNDS} rounds`);
process.exit(0);
```

The `1 in 18` figure came from looping the Node suite under gdb until it faulted. That run captured
the backtrace above:

```
gdb -q -batch -ex run -ex 'info threads' -ex 'thread apply all bt 30' \
    --args node tests/Yession.Tests/out/Main.js
```
