// One cached transcript window: the sequence its first line carries, and the lines themselves.
//
// `null` for an entry that is gone, and for one written without the header — which no build
// that shipped this ever wrote, but a store outlives the build that filled it.

export default (function (cache, url) {
  return cache.match(url).then(async r => {
    if (!r) return null
    const first = r.headers.get('x-yession-first-seq')
    if (first === null) return null
    return [parseInt(first, 10), await r.text()]
  })
})
