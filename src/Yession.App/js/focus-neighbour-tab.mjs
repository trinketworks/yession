// Move focus to the tab that will still be there once this one is gone, BEFORE it goes: the
// neighbour exists right now, and a node that keeps focus keeps it across the patch.

export default (function (e) {
  const tabs = Array.from(e.currentTarget.querySelectorAll('[role="tab"]'))
  const here = tabs.indexOf(document.activeElement?.closest('[role="tab"]'))
  if (here < 0 || tabs.length < 2) return
  tabs[Math.min(here, tabs.length - 2)].focus()
})
