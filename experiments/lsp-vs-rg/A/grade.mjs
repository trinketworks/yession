// Scripted grading. Exported for record.mjs and analyze.mjs; run directly to self-test.
export function normType(t) {
  let s = String(t).replace(/`/g, '').trim().replace(/^val\s+\w+\s*:\s*/, '')
  // drop namespace/module qualifiers: Yession.Domain.Foo -> Foo
  s = s.replace(/\b(?:[A-Za-z_]\w*\.)+([A-Za-z_]\w*)/g, '$1')
  s = s.replace(/\s+/g, ' ').trim()
  // parse into a canonical prefix form
  let i = 0
  const peek = () => s[i], ws = () => { while (s[i] === ' ') i++ }
  function tuple() { const parts = [post()]; ws(); while (peek() === '*') { i++; parts.push(post()); ws() } return parts.length > 1 ? parts.join('*') : parts[0] }
  function post() {
    let a = atom(); ws()
    for (;;) { const m = /^(list|option|array|seq|Set|set|voption|ResizeArray)\b/.exec(s.slice(i)); if (!m) break; i += m[0].length; a = `${m[1] === 'set' ? 'Set' : m[1]}<${a}>`; ws() }
    return a
  }
  function atom() {
    ws()
    if (peek() === '(') { i++; const t = tuple(); ws(); if (peek() === ')') i++; return t.includes('*') ? `(${t})` : t }
    const m = /^'?[A-Za-z_]\w*/.exec(s.slice(i)); if (!m) { const r = s.slice(i); i = s.length; return r }
    i += m[0].length; let name = m[0]; ws()
    if (peek() === '<') { i++; const args = [tuple()]; ws(); while (peek() === ',') { i++; args.push(tuple()); ws() } if (peek() === '>') i++; name += `<${args.join(',')}>` }
    return name
  }
  const out = tuple()
  return out.replace(/[()]/g, '') // parens only group tuples; dropping them is a deliberate simplification
}
const normPath = (p) => String(p).trim().replace(/^`|`$/g, '').replace(/^\.\//, '').replace(/^\/home\/user\/yession\//, '')
export function extractAnswer(text) {
  const lines = String(text).split(/\r?\n/).filter((l) => /^\s*\**ANSWER\**\s*:/i.test(l))
  return lines.length ? lines[lines.length - 1].replace(/^\s*\**ANSWER\**\s*:\s*/i, '').replace(/\*+$/, '').trim() : null
}
export function grade(task, answer) {
  if (answer == null) return 0
  if (task.rule === 'exact_loc') { const m = /([\w./-]+\.fsi?):(\d+)/.exec(answer); return m && `${normPath(m[1])}:${m[2]}` === task.expected ? 1 : 0 }
  if (task.rule === 'file_set') {
    const got = new Set(answer.split(/[,\s]+/).map(normPath).filter((x) => /\.fsi?$/.test(x)))
    const exp = new Set(task.expected); return got.size === exp.size && [...exp].every((x) => got.has(x)) ? 1 : 0
  }
  if (task.rule === 'fs_type') return normType(answer) === normType(task.expected) ? 1 : 0
  throw new Error(`unknown rule ${task.rule}`)
}
if (import.meta.url === `file://${process.argv[1]}`) {
  const cases = [['Map<string, string list>', 'Map<string,list<string>>'], ['ResourceName list * ResourceName list', 'list<ResourceName>*list<ResourceName>'], ['(string * string) list', 'list<string*string>'], ['Map<Yession.Domain.ConnectionId, ConnectionStatus>', 'Map<ConnectionId,ConnectionStatus>'], ['list<string * string>', 'list<string*string>']]
  for (const [a, b] of cases) console.log(normType(a) === normType(b) ? 'ok ' : 'BAD', a, '=>', normType(a), '|', normType(b))
  console.log(extractAnswer('blah\nANSWER: `src/Yession.Domain/Clock.fs:19`'))
}
