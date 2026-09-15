// The foot of a paged listing, watched so the next page arrives as the reader reaches it
// rather than on a press.
//
// `IntersectionObserver` rather than a scroll handler, because it answers the question being
// asked — is the foot on screen — including the case a scroll handler never sees at all: a
// first page that did not fill the card, where the foot is visible and nobody has scrolled.
// Observing fires once immediately for exactly that.
//
// `rootMargin` is what makes it feel like there is no paging: the page is asked for while the
// foot is still a screenful below, so the rows are usually there before the reader is.
//
// The callback is asked WHETHER to fetch, every time, rather than being handed a cursor when
// the observer was made: one observer outlives many renders, and a cursor captured at the
// first would go on asking for the same page. Returning false is how the caller says "not
// now" — already in flight, or nothing more to ask for.
//
// A real module rather than an `[<Emit>]` string: the disconnect bookkeeping is code to read.

export default (function (root, foot, wanted) {
  const observer = new IntersectionObserver(
    entries => { if (entries.some(e => e.isIntersecting)) wanted() },
    { root, rootMargin: '400px 0px' }
  )
  observer.observe(foot)
  return { stop: () => observer.disconnect() }
})
