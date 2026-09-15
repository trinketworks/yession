// Where each pinned surface is scrolled to, keyed by what the surface IS. `-1` means it was
// already at the bottom, which is a position to RESTORE rather than a number to remember.
//
// A real module rather than an `[<Emit>]` string: this is a function body — locals and a loop
// — and an emit pastes one into the caller's scope, which is the YES003 hazard.

export default (function (selector) {
  const key = el => el.getAttribute('data-terminal-id') || 'chat'
  const taken = {}
  for (const el of document.querySelectorAll(selector)) {
    taken[key(el)] = el.scrollTop + el.clientHeight >= el.scrollHeight - 4 ? -1 : el.scrollTop
  }
  return taken
})
