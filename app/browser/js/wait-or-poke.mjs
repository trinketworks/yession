// One wait, ended by whichever comes first — the timer, the network returning, or the poke the
// caller is handed. `ms < 0` is the park a refused peer gets: no timer at all.
//
// A real module rather than an `[<Emit>]` string: three settle paths and one clean-up between
// them is code to read rather than a line to quote.

export default (function (ms, register) { return (
new Promise(resolve => {
  let settled = false
  const finish = () => {
    if (settled) return
    settled = true
    window.removeEventListener('online', finish)
    if (timer !== null) clearTimeout(timer)
    resolve(true)
  }
  const timer = ms >= 0 ? setTimeout(finish, ms) : null
  window.addEventListener('online', finish)
  register(finish)
})
) })
