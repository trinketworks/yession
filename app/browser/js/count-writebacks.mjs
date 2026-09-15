// How many updates this doc took, and how many of those `ySyncPlugin`'s own write-back
// produced — the second number is what makes a zero in the first believable.
//
// A real module rather than an `[<Emit>]` string: the observer closes over two counters, and
// that is the part a reader of this instrument has to check.

export default (function (doc, key) {
  let all = 0, back = 0
  window.__docUpdates = 0
  window.__writebacks = 0
  doc.on('update', function (_update, origin) {
    all++; window.__docUpdates = all
    if (origin === key) { back++; window.__writebacks = back }
  })
})
