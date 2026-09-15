// The `/claude` status GET, flattened to primitives here: a connection crosses as two nullable
// strings per scope rather than as an object, and a refusal answers in the very same shape.
//
// A real module rather than an `[<Emit>]` string: that shape is written three times over and
// every copy has to agree, which is something to read in one place.

export default (url) =>
fetch(url, { cache: 'no-store' })
  .then(r => r.ok ? r.json().then(s => ({ ok: true,
    sessionKind: s.session ? String(s.session.kind || '') : null,
    sessionSignIn: (s.session && s.session.signInRequired) || null,
    mineKind: s.mine ? String(s.mine.kind || '') : null,
    mineSignIn: (s.mine && s.mine.signInRequired) || null,
    owner: s.owner, agent: !!s.agent,
    models: s.models ? JSON.stringify(s.models) : null,
    modelsUnavailable: s.modelsUnavailable || null }))
    : Promise.resolve({ ok: false, sessionKind: null, sessionSignIn: null, mineKind: null, mineSignIn: null, owner: null, agent: false, models: null, modelsUnavailable: null }))
  .catch(() => ({ ok: false, sessionKind: null, sessionSignIn: null, mineKind: null, mineSignIn: null, owner: null, agent: false, models: null, modelsUnavailable: null }))
