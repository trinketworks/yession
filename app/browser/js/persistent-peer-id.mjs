// The browser-wide peer id: whichever is already stored, or the freshly minted one, stored.
//
// A real module rather than an `[<Emit>]` string, which substitutes its argument TEXTUALLY — so
// a placeholder written twice mints twice, and a parameter cannot.

export default (function (minted) {
  try {
    const key = 'yession/peer-id'
    const existing = window.localStorage.getItem(key)
    if (existing) return existing
    window.localStorage.setItem(key, minted)
    return minted
  } catch { return minted }
})
