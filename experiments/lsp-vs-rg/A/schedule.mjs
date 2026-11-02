// Builds one randomized block: 12 tasks x 2 conditions = 24 trials, order shuffled with a
// seeded PRNG (seed = BASE_SEED + block). Writes schedule/block<N>.json with each trial's
// prompt, and a per-trial LSP wrapper so daemon calls can be attributed to a trial.
//   node schedule.mjs <block>
import { readFileSync, writeFileSync, mkdirSync, chmodSync } from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
const HERE = path.dirname(fileURLToPath(import.meta.url))
export const BASE_SEED = 20261102
const block = +process.argv[2]
const seed = BASE_SEED + block
function mulberry32(a) { return () => { a |= 0; a = (a + 0x6d2b79f5) | 0; let t = Math.imul(a ^ (a >>> 15), 1 | a); t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t; return ((t ^ (t >>> 14)) >>> 0) / 4294967296 } }
const rnd = mulberry32(seed)
const tasks = JSON.parse(readFileSync(path.join(HERE, 'tasks.json'), 'utf8'))
const trials = tasks.flatMap((t) => ['RG', 'LSP'].map((cond) => ({ task: t.id, cond })))
for (let i = trials.length - 1; i > 0; i--) { const j = Math.floor(rnd() * (i + 1)); [trials[i], trials[j]] = [trials[j], trials[i]] }

const RULE = readFileSync(path.join(HERE, '../../../AGENTS.md'), 'utf8').split('## Finding F# symbols')[1].trim()
const WRAP_DIR = '/tmp/lspq'
const preamble = `Answer one navigation question about the F# repo at /home/user/yession (~440 .fs files). Read-only: do not edit or write files, do not build, run tests or run \`check\`, do not use git. Be quick; stop once confident. Always give rg a path (\`rg PATTERN .\`); without one it can hang reading stdin.`
const ruleText = `The repo's AGENTS.md rule "Finding F# symbols", verbatim:\n"""\n${RULE}\n"""`
const toolPara = (cond, id) => cond === 'RG'
  ? `Tools: rg/grep, Grep, Glob and Read only. Do not use any language server.\n${ruleText}`
  : `Tools: rg/grep, Grep, Glob and Read, PLUS a warm F# language server (fsautocomplete, compiler-accurate, each call < 1s), called through Bash:\n  ${WRAP_DIR}/${id} definition <file> <line> <symbol-or-column>\n  ${WRAP_DIR}/${id} references <file> <line> <symbol-or-column>   (all uses of that exact symbol, workspace-wide)\n  ${WRAP_DIR}/${id} hover <file> <line> <symbol-or-column>        (inferred type/signature)\n  ${WRAP_DIR}/${id} symbols <name>                                (find declarations by name)\nFiles repo-relative, line and column 1-based; a symbol name finds its first whole-word match on the line, so pass a column when the name occurs twice. Use whichever tools you judge best.\n${ruleText}`
mkdirSync(WRAP_DIR, { recursive: true }); mkdirSync(path.join(HERE, 'schedule'), { recursive: true })
const out = trials.map((tr, k) => {
  const id = `b${block}-${String(k + 1).padStart(2, '0')}-${tr.task}-${tr.cond}`
  const task = tasks.find((t) => t.id === tr.task)
  if (tr.cond === 'LSP') {
    const w = path.join(WRAP_DIR, id)
    writeFileSync(w, `#!/bin/sh\nLSP_TRIAL=${id} exec node ${path.join(HERE, 'lsp-query.mjs')} "$@"\n`); chmodSync(w, 0o755)
  }
  return { id, block, order: k + 1, task: tr.task, cls: task.class, cond: tr.cond, prompt: `${preamble}\n\n${toolPara(tr.cond, id)}\n\nQuestion: ${task.prompt}\n\nEnd your reply with exactly one line: \`ANSWER: <answer>\``}
})
writeFileSync(path.join(HERE, 'schedule', `block${block}.json`), JSON.stringify({ block, seed, trials: out }, null, 1))
console.log(`block ${block} seed ${seed}:`, out.map((t) => `${t.task}/${t.cond}`).join(' '))
