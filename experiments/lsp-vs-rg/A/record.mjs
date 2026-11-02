// Appends one graded row to trials.csv.
//   node record.mjs <trialId> <duration_ms> <tool_uses> <total_tokens> <answer...>
// LSP use is read from the daemon's call log (attributed by the per-trial wrapper).
import { readFileSync, appendFileSync, existsSync } from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { grade } from './grade.mjs'
const HERE = path.dirname(fileURLToPath(import.meta.url))
const [id, ms, tools, tokens, ...rest] = process.argv.slice(2)
const answer = rest.join(' ')
const block = +/^b(\d+)-/.exec(id)[1]
const sched = JSON.parse(readFileSync(path.join(HERE, 'schedule', `block${block}.json`), 'utf8'))
const tr = sched.trials.find((t) => t.id === id); if (!tr) throw new Error(`no trial ${id}`)
const task = JSON.parse(readFileSync(path.join(HERE, 'tasks.json'), 'utf8')).find((t) => t.id === tr.task)
const LOG = process.env.LSP_LOG ?? '/tmp/claude-0/lsp/calls.jsonl'
const calls = existsSync(LOG) ? readFileSync(LOG, 'utf8').trim().split('\n').filter(Boolean).map((l) => JSON.parse(l)).filter((c) => c.trial === id) : []
const CSV = path.join(HERE, 'trials.csv')
const cols = ['trial', 'block', 'order', 'task', 'class', 'cond', 'correct', 'seconds', 'tool_uses', 'total_tokens', 'lsp_calls', 'lsp_used', 'note', 'answer']
if (!existsSync(CSV)) appendFileSync(CSV, cols.join(',') + '\n')
const q = (s) => `"${String(s).replace(/"/g, '""')}"`
const correct = grade(task, answer)
const row = [id, block, tr.order, tr.task, tr.cls, tr.cond, correct, (+ms / 1000).toFixed(1), tools, tokens, calls.length, calls.length > 0 ? 1 : 0, q(process.env.NOTE ?? ''), q(answer)]
appendFileSync(CSV, row.join(',') + '\n')
console.log(`${id} correct=${correct} t=${(+ms / 1000).toFixed(1)}s tools=${tools} lsp=${calls.length}`)
