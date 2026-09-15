// One fetch that does not follow redirects, reporting the parts of the reply the OIDC cases
// read. A module rather than an `[<Emit>]` string because the object it builds is
// JavaScript, and Support.fs says what its type is at the binding.

export default (url, cookie, headers) =>
  fetch(url, { redirect: 'manual', headers: { ...Object.fromEntries(headers), cookie: cookie } }).then(async r => ({
    status: r.status,
    location: r.headers.get('location') || '',
    setCookies: r.headers.getSetCookie(),
    cacheControl: r.headers.get('cache-control') || '',
    body: await r.text() }))
