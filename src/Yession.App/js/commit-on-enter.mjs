// Enter commits a single-line field: the browser is told not to also act on the key, and the
// field gives up focus, which is what the caller reads as "done".
//
// `isComposing` guards the IME exactly as the command line's Enter does: mid-composition,
// Enter accepts a candidate word, and taking the field away from someone typing one is not
// what they asked for.

export default (function (e) {
  if (e.key !== 'Enter' || e.isComposing) return
  e.preventDefault()
  e.currentTarget.blur()
})
