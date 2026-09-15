// The session data channel's whole non-trickle handshake, as one promise that always settles.
//
// A real module rather than an `[<Emit>]` string: forty lines of callbacks and timers do not
// format, lint or read inside a quoted string, and the F# side wants only the typed result.

export default (function (signalUrl, timeoutMs) { return (
new Promise((resolve) => {
  const t0 = performance.now()
  const took = () => Math.round(performance.now() - t0)
  const pc = new RTCPeerConnection({ iceServers: [] })
  const dc = pc.createDataChannel('session')
  let settled = false
  const succeed = () => { if (!settled) { settled = true; resolve({ ok: true, channel: dc, connection: pc, timedOut: false, detail: '', tookMs: took() }) } }
  const fail = (timedOut, detail) => {
    if (settled) return
    settled = true
    try { pc.close() } catch {}
    resolve({ ok: false, channel: null, connection: null, timedOut, detail: String(detail), tookMs: took() })
  }
  let sent = false
  const send = async () => {
    if (sent || settled) return
    sent = true
    try {
      const reply = await fetch(signalUrl, {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ type: pc.localDescription.type, sdp: pc.localDescription.sdp })
      })
      if (!reply.ok) return fail(false, 'signalling refused: ' + reply.status)
      await pc.setRemoteDescription(await reply.json())
    } catch (e) { fail(false, e) }
  }
  // Non-trickle: send once gathering completes — with a settle fallback, because some
  // browsers/sandboxes never report 'complete' (mDNS candidate obfuscation can stall).
  pc.onicegatheringstatechange = () => { if (pc.iceGatheringState === 'complete') send() }
  pc.onicecandidate = (e) => { if (e.candidate === null) send() }
  setTimeout(send, 1500)
  setTimeout(() => fail(true, ''), timeoutMs)
  dc.onopen = succeed
  pc.createOffer().then(o => pc.setLocalDescription(o), e => fail(false, e))
})
) })
