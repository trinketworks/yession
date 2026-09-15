// The sidebar column's other face, opened or closed — which also means bringing that column on
// screen, and `nav-alt` means the opposite thing on each side of the breakpoint.
//
// A real module rather than an `[<Emit>]` string: branching plus a two-frame wait is code to
// read, not a line to quote.

export default (() => {
  const root = document.documentElement
  const desktop = window.matchMedia('(min-width: 768px)').matches
  const opening = !root.classList.contains('settings-open')
  root.classList.toggle('settings-open', opening)
  if (desktop) { if (opening) root.classList.remove('nav-alt') }
  else root.classList.toggle('nav-alt', opening)
  // TWO frames: the face that is arriving is `visibility: hidden` until the transition it
  // just started reaches its first style flush, and `focus()` on a hidden element is a no-op
  // (measured — one frame left focus on <body>).
  requestAnimationFrame(() => requestAnimationFrame(() => {
    const next = document.querySelector(opening ? '[data-settings-toggle="close"]' : '[data-settings-toggle="open"]')
    if (next) next.focus()
  }))
})
