// Re-derives every task's expected answer from the warm FSAC daemon and compares it with
// tasks.json. Run once before trials (and again at the end) as the ground-truth check.
import { readFileSync } from 'node:fs'
import { execFileSync } from 'node:child_process'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { grade } from './grade.mjs'
const HERE = path.dirname(fileURLToPath(import.meta.url))
const tasks = JSON.parse(readFileSync(path.join(HERE, 'tasks.json'), 'utf8'))
for (const t of tasks) {
  const [cmd, ...a] = t.lsp_query.split(' ')
  const out = execFileSync('node', [path.join(HERE, 'lsp-query.mjs'), cmd, ...a], { cwd: path.join(HERE, '../../..'), encoding: 'utf8' })
  let ans
  if (cmd === 'definition') ans = out.split('\n')[0].replace(/:\d+$/, '')
  if (cmd === 'references') ans = [...new Set(out.split('\n').filter((l) => /^\S.*\.fsi? \(\d+\)$/.test(l)).map((l) => l.replace(/ \(\d+\)$/, '')))].join(',')
  if (cmd === 'hover') ans = out.split('\n')[0]
  console.log(grade(t, ans) ? 'MATCH' : 'DIFF ', t.id, '|', ans)
}
