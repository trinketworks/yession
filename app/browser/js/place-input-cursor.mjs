// A collaborator's caret and selection, placed over a native <input> by measuring the text in
// the input's own font — every offset read off the field, none of it assumed from a stylesheet.
//
// A real module rather than an `[<Emit>]` string: twenty lines of measurement in a string get
// no formatting, no lint and no editor help at all.

export default (function(field, peer, a, h){
  const input = document.querySelector(field)
  if (!input || !input.parentElement) return
  const marker = input.parentElement.querySelector('[data-cursor-peer="' + peer + '"]')
  if (!marker) return
  const cs = getComputedStyle(input)
  const canvas = (window.__yInputCanvas || (window.__yInputCanvas = document.createElement('canvas')))
  const ctx = canvas.getContext('2d')
  ctx.font = cs.font && cs.font.trim() ? cs.font : (cs.fontStyle + ' ' + cs.fontWeight + ' ' + cs.fontSize + ' ' + cs.fontFamily)
  const value = input.value || ''
  const clamp = (i) => Math.max(0, Math.min(value.length, i | 0))
  const lo = Math.min(clamp(a), clamp(h)), up = Math.max(clamp(a), clamp(h)), head = clamp(h)
  const px = (v) => parseFloat(v) || 0
  const padLeft = px(cs.paddingLeft), padTop = px(cs.paddingTop), scroll = input.scrollLeft || 0
  const left = input.offsetLeft + px(cs.borderLeftWidth) + padLeft
  const top = input.offsetTop + px(cs.borderTopWidth) + padTop
  const height = input.clientHeight - padTop - px(cs.paddingBottom)
  const xOf = (i) => left + ctx.measureText(value.slice(0, i)).width - scroll
  const loX = xOf(lo)
  marker.style.left = loX + 'px'
  marker.style.top = top + 'px'
  marker.style.height = height + 'px'
  marker.style.width = Math.max(0, xOf(up) - loX) + 'px'
  if (marker.firstElementChild) marker.firstElementChild.style.left = (xOf(head) - loX) + 'px'
})
