// Every keystroke reaching the host, timed from the browser's own event timestamp to the frame
// it paints on, with the per-burst diagnostic that says where keys and frames went.
//
// A real module rather than an `[<Emit>]` string: two capturing listeners and the state they
// share are code to read, and why each exists is on the binding in EditorHarness.fs.

export default (function (host, take) {
  window.__benchDiagState = { hostKeydowns: 0, docKeydowns: 0, rafs: 0, focus: [] }
  document.addEventListener('keydown', function () {
    var d = window.__benchDiagState, a = document.activeElement
    d.docKeydowns++
    if (d.focus.length < 60)
      d.focus.push(!a ? 'none' : (a.closest && a.closest('#peer-b')) ? 'peer-b' : a.id ? '#' + a.id : a.tagName.toLowerCase())
  }, true)
  host.addEventListener('keydown', function (e) {
    window.__benchDiagState.hostKeydowns++
    requestAnimationFrame(function () {
      window.__benchDiagState.rafs++
      take(performance.now() - e.timeStamp)
    })
  }, true)
})
