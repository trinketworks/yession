// Every pinned surface put back where the render left the reader — and written only when the
// render moved them, because any write to `scrollTop` ends a scroll the browser has in flight.
//
// A real module rather than an `[<Emit>]` string: a loop with three outcomes is code, and the
// reasoning for each of them sits on the binding in Render.fs.

export default (function (selector, positions) {
  const key = el => el.getAttribute('data-terminal-id') || 'chat'
  const atEnd = el => el.scrollTop + el.clientHeight >= el.scrollHeight - 4
  for (const el of document.querySelectorAll(selector)) {
    const position = positions[key(el)]
    if (position === undefined || position < 0) { if (!atEnd(el)) el.scrollTop = el.scrollHeight }
    else if (el.scrollTop !== position) el.scrollTop = position
  }
})
