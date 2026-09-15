// Copy to the clipboard, answering whether it happened. The API is absent outside a secure
// context — most commonly a session reached over plain HTTP at a LAN address.
//
// The refusal is written to the console rather than swallowed, because the only symptom it has
// otherwise is a button that appears to do nothing — the same shape as a broken binding.

export default (function (text, settled) {
  const clip = navigator.clipboard
  if (!clip) { console.debug('yession/copy: no clipboard in this context'); settled(false); return }
  clip.writeText(text).then(
    () => settled(true),
    (err) => { console.debug('yession/copy: refused', String(err)); settled(false) })
})
