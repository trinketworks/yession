// The same move as the toggle, in one direction only: `settings-open` is SET rather than
// flipped, so pressing twice is pressing once, and focus moves only when the face arrived.
//
// A real module rather than an `[<Emit>]` string, for the reason its toggle sibling is one: the
// branching is real, and a string gets no formatting and no lint.

export default (() => {
  const root = document.documentElement
  const desktop = window.matchMedia('(min-width: 768px)').matches
  const wasOpen = root.classList.contains('settings-open')
  root.classList.add('settings-open')
  // Bring the column on screen: `nav-alt` means the opposite thing on each side of the
  // breakpoint — collapsed on desktop, drawer-open on mobile.
  if (desktop) root.classList.remove('nav-alt')
  else root.classList.add('nav-alt')
  // Focus moves only when the face actually ARRIVED. Stealing it from whatever the reader
  // was doing, to a control that was already on screen, would be the prompt reaching into a
  // panel they are already reading.
  if (!wasOpen) {
    // TWO frames, for the reason the toggle needs them: the arriving face is
    // `visibility: hidden` until the transition it just started reaches its first style
    // flush, and `focus()` on a hidden element is a no-op.
    requestAnimationFrame(() => requestAnimationFrame(() => {
      const next = document.querySelector('[data-settings-toggle="close"]')
      if (next) next.focus()
    }))
  }
})
