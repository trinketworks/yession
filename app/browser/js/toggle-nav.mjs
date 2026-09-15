// The sidebar/drawer bit on the root element, flipped — then focus handed to whichever control
// replaces the one that just disappeared.
//
// A real module rather than an `[<Emit>]` string: the desktop/mobile branching and the frame it
// waits for are behaviour, and behaviour in a string is neither formatted nor linted.

export default (() => {
  const root = document.documentElement
  const desktop = window.matchMedia('(min-width: 768px)').matches
  root.classList.toggle('nav-alt')
  // The nav control always returns the column to its workspace face — a column that reopened
  // on settings would be a surprise, and `settings-open` is what chooses the face.
  root.classList.remove('settings-open')
  const shown = desktop !== root.classList.contains('nav-alt')
  if (desktop) { try { localStorage.setItem('yession.nav', shown ? 'open' : 'collapsed') } catch (e) {} }
  requestAnimationFrame(() => {
    const next = document.querySelector(shown ? 'button[data-nav-toggle="hide"]' : '[data-nav-toggle="show"]')
    if (next) next.focus()
  })
})
