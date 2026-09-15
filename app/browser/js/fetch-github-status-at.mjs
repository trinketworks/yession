// The GitHub connection panel's status round-trip, flattened to primitives here rather than
// carried across as an object — the same reason `fetch-claude-status-at.mjs` does it.
//
// A failed fetch and a non-ok reply are one answer: `ok: false` with nothing connected.

export default (url) =>
  fetch(url, { cache: 'no-store' })
    .then(r => r.ok ? r.json().then(s => ({ ok: true,
      sessionKind: s.session ? String(s.session.kind || '') : null,
      sessionSignIn: (s.session && s.session.signInRequired) || null,
      mineKind: s.mine ? String(s.mine.kind || '') : null,
      mineSignIn: (s.mine && s.mine.signInRequired) || null }))
      : Promise.resolve({ ok: false, sessionKind: null, sessionSignIn: null, mineKind: null, mineSignIn: null }))
    .catch(() => ({ ok: false, sessionKind: null, sessionSignIn: null, mineKind: null, mineSignIn: null }))
