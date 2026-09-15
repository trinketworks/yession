// Both of the peer connection's state machines, read as the one signal that matters: this
// transport is finished.
//
// A real module rather than an `[<Emit>]` string, whose body is pasted into the caller's scope
// — where these local names could collide with the call site's, which is the YES003 hazard.

export default (function (peer, onDead) {
  const finished = () =>
    peer.connectionState === 'failed' || peer.connectionState === 'closed' ||
    peer.iceConnectionState === 'failed' || peer.iceConnectionState === 'closed'
  const check = () => { if (finished()) onDead() }
  peer.addEventListener('connectionstatechange', check)
  peer.addEventListener('iceconnectionstatechange', check)
  check()
})
