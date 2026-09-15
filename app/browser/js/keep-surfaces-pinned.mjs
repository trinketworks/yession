// A resize moves the end of a scrolling surface away from the reader as surely as a render, so
// whether they were at the end is sampled on the scroll event — before the box changes.
//
// A real module rather than an `[<Emit>]` string: two listeners and the WeakMap between them
// are code to read, not a quoted line.

export default (function (selector) {
  const sel = selector
  const atEnd = el => el.scrollTop + el.clientHeight >= el.scrollHeight - 4
  const pinned = new WeakMap()
  document.addEventListener('scroll', e => {
    const el = e.target
    if (el instanceof Element && el.matches(sel)) pinned.set(el, atEnd(el))
  }, true)
  window.addEventListener('resize', () => {
    for (const el of document.querySelectorAll(sel)) {
      if (pinned.get(el) !== false) el.scrollTop = el.scrollHeight
    }
  })
})
