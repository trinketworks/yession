// The tab key a Delete/Backspace keypress means to unpin, or `''` when this keypress is not
// that — the strip's other keys are the arrow walk, and typing must not unpin anything.

export default (function (e) {
  if (e.key !== 'Delete' && e.key !== 'Backspace') return ''
  const tab = document.activeElement?.closest('[data-pane-tab]')
  if (!tab) return ''
  e.preventDefault()
  return tab.getAttribute('data-pane-tab')
})
