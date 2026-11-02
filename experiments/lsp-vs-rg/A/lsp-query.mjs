#!/usr/bin/env node
// Client for lsp-daemon.mjs. Positions are 1-based.
//   lsp-query.mjs definition <file> <line> <symbol>
//   lsp-query.mjs references <file> <line> <symbol>
//   lsp-query.mjs hover      <file> <line> <symbol>
//   lsp-query.mjs symbols    <query>
// A numeric 4th argument is taken as a column instead of a symbol.
const [cmd, ...a] = process.argv.slice(2)
const TOOL = { definition: 'fsharp_definition', references: 'fsharp_references', hover: 'fsharp_hover', symbols: 'fsharp_symbol_search', status: 'status' }
if (!TOOL[cmd]) { console.error('usage: lsp definition|references|hover <file> <line> <symbol|column>  |  lsp symbols <query>'); process.exit(2) }
let args = {}
if (cmd === 'symbols') args = { query: a.join(' ') }
else if (cmd !== 'status') {
  const [file, line, pos] = a
  args = { file, line: +line }
  if (/^\d+$/.test(pos ?? '')) args.column = +pos; else args.symbol = pos
}
const port = +(process.env.LSP_PORT ?? 47811)
const res = await fetch(`http://127.0.0.1:${port}/`, { method: 'POST', body: JSON.stringify({ tool: TOOL[cmd], args, trial: process.env.LSP_TRIAL ?? null }) })
const text = await res.text()
console.log(text)
process.exit(res.ok ? 0 : 1)
