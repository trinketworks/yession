// Analysis of trials.csv. No dependencies.
//   node analyze.mjs            full report (markdown to stdout)
//   node analyze.mjs --upto N   only blocks <= N (used for the sequential-stopping trace)
import { readFileSync } from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
const HERE = path.dirname(fileURLToPath(import.meta.url))
const upto = process.argv.includes('--upto') ? +process.argv[process.argv.indexOf('--upto') + 1] : Infinity

function parseCsv(text) {
  const rows = []; let row = [], f = '', q = false
  for (let i = 0; i < text.length; i++) {
    const c = text[i]
    if (q) { if (c === '"' && text[i + 1] === '"') { f += '"'; i++ } else if (c === '"') q = false; else f += c }
    else if (c === '"') q = true; else if (c === ',') { row.push(f); f = '' } else if (c === '\n') { row.push(f); rows.push(row); row = []; f = '' } else f += c
  }
  if (f || row.length) { row.push(f); rows.push(row) }
  const [h, ...rs] = rows; return rs.map((r) => Object.fromEntries(h.map((k, i) => [k, r[i]])))
}
const trials = parseCsv(readFileSync(path.join(HERE, 'trials.csv'), 'utf8')).filter((t) => +t.block <= upto)
  .map((t) => ({ ...t, block: +t.block, correct: +t.correct, seconds: +t.seconds, tool_uses: +t.tool_uses, total_tokens: +t.total_tokens, lsp_used: +t.lsp_used, lsp_calls: +t.lsp_calls, restarted: /daemon restarted/.test(t.note) }))
const blocks = [...new Set(trials.map((t) => t.block))].sort((a, b) => a - b)

// pairs keyed (task, block)
const pairs = []
for (const b of blocks) for (const task of [...new Set(trials.map((t) => t.task))].sort()) {
  const rg = trials.find((t) => t.block === b && t.task === task && t.cond === 'RG')
  const lsp = trials.find((t) => t.block === b && t.task === task && t.cond === 'LSP')
  if (rg && lsp) pairs.push({ block: b, task, cls: rg.class, rg, lsp })
}

function mulberry32(a) { return () => { a |= 0; a = (a + 0x6d2b79f5) | 0; let t = Math.imul(a ^ (a >>> 15), 1 | a); t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t; return ((t ^ (t >>> 14)) >>> 0) / 4294967296 } }
const mean = (xs) => xs.reduce((a, b) => a + b, 0) / xs.length
const sd = (xs) => { const m = mean(xs); return Math.sqrt(xs.reduce((a, x) => a + (x - m) ** 2, 0) / (xs.length - 1)) }
const quant = (xs, p) => { const s = [...xs].sort((a, b) => a - b); const i = (s.length - 1) * p; const lo = Math.floor(i); return s[lo] + (s[Math.ceil(i)] - s[lo]) * (i - lo) }
function bootCI(items, stat, B = 10000, seed = 7) {
  if (items.length < 2) return [NaN, NaN]
  const r = mulberry32(seed), out = []
  for (let b = 0; b < B; b++) { const s = []; for (let i = 0; i < items.length; i++) s.push(items[Math.floor(r() * items.length)]); const v = stat(s); if (Number.isFinite(v)) out.push(v) }
  return [quant(out, 0.025), quant(out, 0.975)]
}
function binomTwoSided(k, n) { // exact McNemar: discordant pairs ~ Bin(n, 1/2)
  if (n === 0) return 1
  const lc = (n, k) => { let s = 0; for (let i = 1; i <= k; i++) s += Math.log(n - k + i) - Math.log(i); return s }
  const pmf = (i) => Math.exp(lc(n, i) - n * Math.log(2)); const obs = pmf(k)
  let p = 0; for (let i = 0; i <= n; i++) if (pmf(i) <= obs * (1 + 1e-9)) p += pmf(i); return Math.min(1, p)
}
const zA = 1.959964, zB = 0.841621
const fmt = (x, d = 3) => Number.isFinite(x) ? x.toFixed(d) : 'n/a'

function summarize(ps, label) {
  const both = ps.filter((p) => p.rg.correct && p.lsp.correct && !p.rg.restarted && !p.lsp.restarted)
  const d = both.map((p) => Math.log(p.lsp.seconds / p.rg.seconds))
  const dAll = ps.filter((p) => !p.rg.restarted && !p.lsp.restarted).map((p) => Math.log(p.lsp.seconds / p.rg.seconds))
  const acc = ps.map((p) => p.lsp.correct - p.rg.correct)
  const b10 = ps.filter((p) => p.lsp.correct && !p.rg.correct).length, b01 = ps.filter((p) => !p.lsp.correct && p.rg.correct).length
  const lt = (k) => ps.map((p) => Math.log(p.lsp[k] / p.rg[k])).filter(Number.isFinite)
  return {
    label, pairs: ps.length, bothCorrect: both.length,
    accRG: mean(ps.map((p) => p.rg.correct)), accLSP: mean(ps.map((p) => p.lsp.correct)),
    accDiff: mean(acc), accCI: bootCI(acc, mean, 10000, 11), lspOnlyRight: b10, rgOnlyRight: b01, mcnemarP: binomTwoSided(Math.min(b10, b01), b10 + b01),
    logRatio: d.length ? mean(d) : NaN, logRatioCI: bootCI(d, mean, 10000, 13), sdLog: d.length > 1 ? sd(d) : NaN, sdLogAll: dAll.length > 1 ? sd(dAll) : NaN, logRatioAll: dAll.length ? mean(dAll) : NaN, logRatioAllCI: bootCI(dAll, mean, 10000, 17),
    medSecRG: quant(ps.map((p) => p.rg.seconds), 0.5), medSecLSP: quant(ps.map((p) => p.lsp.seconds), 0.5),
    toolRatio: mean(lt('tool_uses')), toolCI: bootCI(lt('tool_uses'), mean, 10000, 19), meanToolsRG: mean(ps.map((p) => p.rg.tool_uses)), meanToolsLSP: mean(ps.map((p) => p.lsp.tool_uses)),
    tokRatio: mean(lt('total_tokens')), tokCI: bootCI(lt('total_tokens'), mean, 10000, 23), meanTokRG: mean(ps.map((p) => p.rg.total_tokens)), meanTokLSP: mean(ps.map((p) => p.lsp.total_tokens)),
    lspUse: mean(ps.map((p) => p.lsp.lsp_used)), discord: (b10 + b01) / ps.length,
  }
}
const all = summarize(pairs, 'ALL')
const row = (s) => `| ${s.label} | ${s.pairs} | ${fmt(s.accRG, 2)} | ${fmt(s.accLSP, 2)} | ${fmt(s.accDiff, 2)} [${fmt(s.accCI[0], 2)}, ${fmt(s.accCI[1], 2)}] | ${s.lspOnlyRight}/${s.rgOnlyRight} p=${fmt(s.mcnemarP, 3)} | ${s.bothCorrect} | ${fmt(Math.exp(s.logRatio), 2)} [${fmt(Math.exp(s.logRatioCI[0]), 2)}, ${fmt(Math.exp(s.logRatioCI[1]), 2)}] | ${fmt(s.medSecRG, 0)} / ${fmt(s.medSecLSP, 0)} | ${fmt(Math.exp(s.toolRatio), 2)} [${fmt(Math.exp(s.toolCI[0]), 2)}, ${fmt(Math.exp(s.toolCI[1]), 2)}] | ${fmt(Math.exp(s.tokRatio), 2)} [${fmt(Math.exp(s.tokCI[0]), 2)}, ${fmt(Math.exp(s.tokCI[1]), 2)}] | ${fmt(s.lspUse, 2)} |`
console.log(`Blocks: ${blocks.join(', ')}; trials: ${trials.length}; pairs: ${pairs.length}\n`)
console.log('| subset | pairs | acc RG | acc LSP | acc diff LSP-RG [95% CI] | discordant LSP-only/RG-only, McNemar | both correct | time ratio LSP/RG (both correct) [95% CI] | median s RG / LSP | tool-call ratio [CI] | token ratio [CI] | LSP used |')
console.log('|---|---|---|---|---|---|---|---|---|---|---|---|')
console.log(row(all))
for (const c of ['DEFINITION', 'REFERENCES', 'TYPE-DRIVEN']) console.log(row(summarize(pairs.filter((p) => p.cls === c), c)))
console.log('\nPer task (all blocks):\n\n| task | RG correct | LSP correct | median s RG | median s LSP | LSP used |\n|---|---|---|---|---|---|')
for (const t of [...new Set(pairs.map((p) => p.task))].sort()) { const ps = pairs.filter((p) => p.task === t); console.log(`| ${t} | ${ps.filter((p) => p.rg.correct).length}/${ps.length} | ${ps.filter((p) => p.lsp.correct).length}/${ps.length} | ${fmt(quant(ps.map((p) => p.rg.seconds), 0.5), 0)} | ${fmt(quant(ps.map((p) => p.lsp.seconds), 0.5), 0)} | ${ps.filter((p) => p.lsp.lsp_used).length}/${ps.length} |`) }

// power, from this data's variance
const dT = Math.log(1.25)
const nTime = Math.ceil(((zA + zB) * all.sdLog / dT) ** 2)
const pd = Math.max(all.discord, 0.15 + 0.01), dA = 0.15
const nAcc = Math.ceil(((zA * Math.sqrt(pd) + zB * Math.sqrt(pd - dA * dA)) / dA) ** 2)
console.log(`\nPower inputs: paired SD of log(seconds) (both correct) = ${fmt(all.sdLog)}; (all pairs) = ${fmt(all.sdLogAll)}; discordance rate = ${fmt(all.discord)}`)
console.log(`Pairs needed for 80% power, alpha .05: time ratio 1.25 (|log| ${fmt(dT)}) -> ${nTime} pairs (${Math.ceil(nTime / 12)} blocks); accuracy diff 0.15 with discordance ${fmt(pd)} -> ${nAcc} pairs (${Math.ceil(nAcc / 12)} blocks)`)
console.log(`All-pairs time ratio (incl. wrong answers): ${fmt(Math.exp(all.logRatioAll), 2)} [${fmt(Math.exp(all.logRatioAllCI[0]), 2)}, ${fmt(Math.exp(all.logRatioAllCI[1]), 2)}]`)

// stopping rule
const excl = (ci) => ci[0] > 0 || ci[1] < 0
const width = all.logRatioCI[1] - all.logRatioCI[0]
const stop = (excl(all.logRatioCI) && excl(all.accCI)) || width < 0.2
console.log(`\nSTOP-RULE after block ${blocks.at(-1)}: logRatioCI [${fmt(all.logRatioCI[0])}, ${fmt(all.logRatioCI[1])}] width ${fmt(width)} excludes0=${excl(all.logRatioCI)}; accCI [${fmt(all.accCI[0])}, ${fmt(all.accCI[1])}] excludes0=${excl(all.accCI)} -> ${stop ? 'STOP' : 'CONTINUE'}`)
const sum = (k) => trials.reduce((a, t) => a + t[k], 0)
console.log(`\nBudget: ${trials.length} trials, ${sum('total_tokens')} subagent tokens total, ${fmt(sum('seconds') / 60, 1)} subagent-minutes, ${sum('tool_uses')} tool calls`)
