// Ask whether the transport is still alive at the moments a browser is most likely to have
// torn it down with no script running to hear about it. Returns the way to stop asking.
//
// A real module rather than an `[<Emit>]` string: three listeners and the teardown that has to
// match them read as code here, and the binding keeps only the type.

export default (function (pc, dc, handler) {
  const peer = pc, chan = dc, onDead = handler
  const look = () => {
    if (document.visibilityState === 'hidden') return
    if (peer.connectionState === 'failed' || peer.connectionState === 'closed' ||
        peer.iceConnectionState === 'failed' || peer.iceConnectionState === 'closed' ||
        chan.readyState !== 'open') onDead()
  }
  window.addEventListener('pageshow', look)
  window.addEventListener('online', look)
  document.addEventListener('visibilitychange', look)
  return () => {
    window.removeEventListener('pageshow', look)
    window.removeEventListener('online', look)
    document.removeEventListener('visibilitychange', look)
  }
})
