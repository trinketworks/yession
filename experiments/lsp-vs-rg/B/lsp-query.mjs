#!/usr/bin/env node
// Client for lsp-daemon.mjs. Positions are 1-based.
//   lsp-query.mjs definition <file> <line> <symbol>
//   lsp-query.mjs references <file> <line> <symbol>
//   lsp-query.mjs hover      <file> <line> <symbol>
//   lsp-query.mjs symbols    <query>
// <symbol> may instead be a 1-based column number.
import net from 'node:net'
const [cmd, ...a] = process.argv.slice(2)
const TOOL = { definition: 'fsharp_definition', references: 'fsharp_references', hover: 'fsharp_hover', symbols: 'fsharp_symbol_search' }
if (!TOOL[cmd]) { console.error('usage: lsp-query.mjs definition|references|hover <file> <line> <symbol|column>  |  symbols <query>'); process.exit(2) }
const args = cmd === 'symbols' ? { query: a[0] }
  : { file: a[0], line: +a[1], ...(/^\d+$/.test(a[2] ?? '') ? { column: +a[2] } : { symbol: a[2] }) }
const s = net.connect(process.env.LSP_SOCK || '/tmp/fsac.sock')
let buf = ''
s.on('error', (e) => { console.error(`lsp daemon unreachable: ${e.message}`); process.exit(3) })
s.on('connect', () => s.write(JSON.stringify({ tool: TOOL[cmd], args, cwd: process.cwd() }) + '\n'))
s.on('data', (d) => (buf += d))
s.on('end', () => { const r = JSON.parse(buf); console.log(r.out); process.exit(r.ok ? 0 : 1) })
