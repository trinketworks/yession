// Arrow/Home/End movement inside a tablist — the half of the ARIA tabs pattern a plain row of
// buttons does not give you. Moves focus only; selection follows the Enter/Space on the button.
//
// A real module rather than an `[<Emit>]` string: the wrap-around arithmetic is the whole
// behaviour, and in a quoted string nothing formats or lints it.

export default (function (e) {
  const key = e.key
  if (key !== 'ArrowLeft' && key !== 'ArrowRight' && key !== 'Home' && key !== 'End') return
  const tabs = Array.from(e.currentTarget.querySelectorAll('[role="tab"]'))
  if (tabs.length === 0) return
  const here = tabs.indexOf(document.activeElement)
  const next =
    key === 'Home' ? 0
    : key === 'End' ? tabs.length - 1
    : here < 0 ? 0
    : (here + (key === 'ArrowRight' ? 1 : tabs.length - 1)) % tabs.length
  tabs[next].focus()
  e.preventDefault()
})
