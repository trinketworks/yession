#!/usr/bin/env node
// Warm FSAC daemon for the LSP-vs-rg experiment. The LanguageServer class, position
// handling and tool handlers are copied verbatim from PR #38's MCP bridge
// (bb197dd:scripts/fsharp-lsp-mcp.js); only the transport differs: a localhost HTTP
// server instead of MCP stdio, so subjects can query it through Bash with lsp-query.mjs.
// Every request is appended to $LSP_LOG (JSON lines) with the caller's trial id.

import { spawn } from 'node:child_process'
import { readFileSync, existsSync } from 'node:fs'
import { pathToFileURL, fileURLToPath } from 'node:url'
import path from 'node:path'

const ROOT = process.env.YESSION_ROOT || path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../..')

// fsautocomplete comes from devenv, and agents are launched from the plain container
// shell, so it is not on PATH. devenv mirrors the built environment at .devenv/profile —
// a symlink into the Nix store, refreshed on every `devenv shell` — which is readable
// whether or not we are inside the shell. That single path is the only way we look.
const FSAC_BIN_DIR = path.join(ROOT, '.devenv', 'profile', 'bin')
const FSAC = process.env.FSAC_BIN || path.join(FSAC_BIN_DIR, 'fsautocomplete')

const fsacInstalled = () => existsSync(FSAC)

const FSAC_MISSING = `fsautocomplete not found at ${FSAC}. Build the environment once ` +
  `(see Bootstrap in AGENTS.md — \`devenv shell -- build\`); it is picked up automatically ` +
  `afterwards, with no need to restart this session.`

// FSAC loads projects by shelling out to MSBuild. Without `dotnet` on PATH it starts
// cleanly, loads zero projects, and answers every query with "Couldn't find <file> in
// LoadedProjects" — a missing toolchain wearing the costume of a broken workspace. The
// profile's bin directory carries dotnet too, and the nix dotnet wrapper supplies
// DOTNET_ROOT itself, so putting that one directory on PATH is the whole fix.
const fsacEnv = () => ({
  ...process.env,
  PATH: process.env.PATH ? `${FSAC_BIN_DIR}${path.delimiter}${process.env.PATH}` : FSAC_BIN_DIR,
})
// The first query after startup pays workspace load + typecheck (~20s here); the first
// `references` additionally forces a whole-workspace check (~11s more). Budget for both.
const READY_TIMEOUT_MS = +(process.env.FSAC_READY_TIMEOUT_MS ?? 180_000)
const REQUEST_TIMEOUT_MS = +(process.env.FSAC_REQUEST_TIMEOUT_MS ?? 120_000)

const log = (...a) => console.error('[fsharp-lsp-mcp]', ...a)

// ---------------------------------------------------------------------------
// JSON-RPC framing, shared by both protocols (LSP frames with Content-Length,
// MCP uses newline-delimited JSON).
// ---------------------------------------------------------------------------

function frameReader(onMessage) {
  let buf = Buffer.alloc(0)
  return (chunk) => {
    buf = Buffer.concat([buf, chunk])
    for (;;) {
      const sep = buf.indexOf('\r\n\r\n')
      if (sep < 0) return
      const m = /Content-Length:\s*(\d+)/i.exec(buf.subarray(0, sep).toString('ascii'))
      if (!m) { buf = buf.subarray(sep + 4); continue }
      const len = +m[1]
      if (buf.length < sep + 4 + len) return
      const body = buf.subarray(sep + 4, sep + 4 + len).toString('utf8')
      buf = buf.subarray(sep + 4 + len)
      try { onMessage(JSON.parse(body)) } catch (e) { log('unparseable LSP frame:', e.message) }
    }
  }
}

// ---------------------------------------------------------------------------
// The language server: one long-lived FSAC process, started lazily.
// ---------------------------------------------------------------------------

class LanguageServer {
  constructor() {
    this.proc = null
    this.pending = new Map()
    this.nextId = 1
    this.opened = new Set()
    this.starting = null
    this.projectsLoaded = false
    this.loadedProjectCount = 0
  }

  start() {
    if (this.starting) return this.starting
    this.starting = this._start()
    return this.starting
  }

  async _start() {
    const t0 = Date.now()
    // Checked per attempt, not once at import, so an agent that builds the environment
    // mid-session is picked up by the next query.
    if (!fsacInstalled()) throw new Error(FSAC_MISSING)
    log(`starting ${FSAC} in ${ROOT}…`)
    this.proc = spawn(FSAC, [], { cwd: ROOT, stdio: ['pipe', 'pipe', 'pipe'], env: fsacEnv() })
    this.proc.on('error', (e) => {
      const hint = e.code === 'ENOENT' ? FSAC_MISSING : `could not start ${FSAC}: ${e.message}`
      log(hint)
      this.proc = null
      this._failAllPending(new Error(hint))
    })
    this.proc.on('exit', (code, sig) => {
      log(`FSAC exited code=${code} sig=${sig}`)
      this.proc = null
      // Clearing `starting` lets the next tool call retry from scratch instead of
      // awaiting a promise for a process that is gone.
      this.starting = null
      this.projectsLoaded = false
      this.opened.clear()
      this._failAllPending(new Error(`language server exited (code ${code})`))
    })
    // FSAC's own logging is noisy and irrelevant unless something breaks; keep the last
    // slice around so a failure can report it, but never let it reach our stdout (MCP
    // owns that channel).
    this.stderr = ''
    this.proc.stderr.on('data', (d) => {
      this.stderr = (this.stderr + d.toString()).slice(-8000)
    })
    this.proc.stdout.on('data', frameReader((msg) => this._dispatch(msg)))

    await this._request('initialize', {
      processId: process.pid,
      rootUri: pathToFileURL(ROOT).href,
      rootPath: ROOT,
      workspaceFolders: [{ uri: pathToFileURL(ROOT).href, name: path.basename(ROOT) }],
      capabilities: {
        workspace: { workspaceFolders: true, configuration: true },
        // Deliberately NOT declaring window.workDoneProgress: it makes FSAC send
        // server->client requests, and we have no use for the progress events.
        textDocument: {
          synchronization: { didSave: true },
          definition: { linkSupport: false },
          references: {},
          hover: { contentFormat: ['plaintext', 'markdown'] },
        },
      },
      // FSAC discovers and loads the workspace itself — it reads Yession.slnx correctly
      // and picks up all projects, so no explicit fsharp/workspaceLoad is needed.
      initializationOptions: { AutomaticWorkspaceInit: true, EnableAnalyzers: false },
    })
    this._notify('initialized', {})
    log(`initialize done in ${Date.now() - t0}ms; workspace loading in background`)
  }

  _dispatch(msg) {
    // A JSON-RPC *response* has no `method`. Server-originated *requests* carry both an
    // `id` and a `method`, and their ids are numbered independently of ours — they
    // collide freely. Matching on id alone resolves our pending requests with the
    // server's, which fails silently and looks like "the server returned nothing".
    if (msg.method === undefined && msg.id !== undefined) {
      const entry = this.pending.get(msg.id)
      if (!entry) return
      this.pending.delete(msg.id)
      clearTimeout(entry.timer)
      if (msg.error) entry.reject(new Error(`${msg.error.message} (code ${msg.error.code})`))
      else entry.resolve(msg.result)
      return
    }
    if (msg.method && msg.id !== undefined) {
      this._send({ jsonrpc: '2.0', id: msg.id, result: null }) // acknowledge and move on
      return
    }
    if (msg.method === 'fsharp/notifyWorkspace') {
      let c = msg.params?.content
      try { c = JSON.parse(c) } catch { return }
      if (c?.Kind === 'project') this.loadedProjectCount++
      if (c?.Kind === 'workspaceLoad' && c?.Data?.Status === 'finished') {
        this.projectsLoaded = true
        log(`workspace loaded (${this.loadedProjectCount} project load events)`)
      }
    }
  }

  _failAllPending(err) {
    for (const { reject, timer } of this.pending.values()) { clearTimeout(timer); reject(err) }
    this.pending.clear()
  }

  _send(obj) {
    if (!this.proc) throw new Error('language server is not running')
    const s = JSON.stringify(obj)
    this.proc.stdin.write(`Content-Length: ${Buffer.byteLength(s, 'utf8')}\r\n\r\n${s}`)
  }

  _notify(method, params) { this._send({ jsonrpc: '2.0', method, params }) }

  _request(method, params, timeoutMs = REQUEST_TIMEOUT_MS) {
    const id = this.nextId++
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id)
        reject(new Error(`${method} timed out after ${timeoutMs}ms`))
      }, timeoutMs)
      this.pending.set(id, { resolve, reject, timer })
      try { this._send({ jsonrpc: '2.0', id, method, params }) }
      catch (e) { clearTimeout(timer); this.pending.delete(id); reject(e) }
    })
  }

  openFile(absPath) {
    if (this.opened.has(absPath)) return
    this._notify('textDocument/didOpen', {
      textDocument: {
        uri: pathToFileURL(absPath).href,
        languageId: 'fsharp',
        version: 1,
        text: readFileSync(absPath, 'utf8'),
      },
    })
    this.opened.add(absPath)
  }

  // FSAC answers position queries with null until the containing project is loaded and
  // the file typechecked. There is no single "ready" signal that covers both, so poll the
  // real query until it produces an answer. `emptyIsValid` distinguishes "not ready yet"
  // from "genuinely no results" — a symbol can legitimately have zero references.
  async queryWhenReady(method, params, { emptyIsValid = false } = {}) {
    const deadline = Date.now() + READY_TIMEOUT_MS
    let last = null
    for (;;) {
      // Experiment change: a file opened for the first time answers "No check results"
      // until FSAC has typechecked it. Treat that as "not ready yet", like a null answer.
      try { last = await this._request(method, params) }
      catch (e) {
        if (!/No check results|Couldn't find/.test(e.message) || Date.now() > deadline) throw e
        await new Promise((r) => setTimeout(r, 500)); continue
      }
      const empty = last == null || (Array.isArray(last) && last.length === 0)
      if (!empty) return last
      // Once the workspace has finished loading, an empty answer is the real answer.
      if (this.projectsLoaded && emptyIsValid) return last
      if (Date.now() > deadline) return last
      await new Promise((r) => setTimeout(r, 500))
    }
  }
}

const server = new LanguageServer()

// ---------------------------------------------------------------------------
// Position handling: 1-based in, 0-based on the wire.
// ---------------------------------------------------------------------------

function resolveFile(file) {
  const abs = path.isAbsolute(file) ? file : path.join(ROOT, file)
  if (!existsSync(abs)) throw new Error(`no such file: ${path.relative(ROOT, abs)}`)
  if (!abs.endsWith('.fs') && !abs.endsWith('.fsi') && !abs.endsWith('.fsx')) {
    throw new Error(`not an F# source file: ${path.relative(ROOT, abs)}`)
  }
  return abs
}

// Turn (line, column|symbol) into a 0-based LSP position. Locating `symbol` on the line
// is the ergonomic path: callers know the name they are asking about, not its column.
function resolvePosition(absPath, line, column, symbol) {
  const lines = readFileSync(absPath, 'utf8').split(/\r?\n/)
  if (line < 1 || line > lines.length) {
    throw new Error(`line ${line} out of range (${path.relative(ROOT, absPath)} has ${lines.length} lines)`)
  }
  const text = lines[line - 1]
  if (symbol) {
    // Prefer a whole-word match so `SessionId` does not land inside `sessionId`.
    const word = new RegExp(`(?<![A-Za-z0-9_'])${symbol.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}(?![A-Za-z0-9_'])`)
    const m = word.exec(text) ?? { index: text.indexOf(symbol) }
    if (m.index < 0) {
      throw new Error(`symbol ${JSON.stringify(symbol)} not found on ${path.relative(ROOT, absPath)}:${line} — line reads: ${text.trim()}`)
    }
    // Aim at the middle of the token: FSAC resolves from inside the identifier, and the
    // last character of a token can belong to whatever follows it.
    return { line: line - 1, character: m.index + Math.floor(symbol.length / 2) }
  }
  if (column == null) throw new Error('provide either `column` or `symbol`')
  return { line: line - 1, character: column - 1 }
}

const fmtLocation = (loc) => {
  const abs = fileURLToPath(loc.uri)
  const rel = path.relative(ROOT, abs)
  const r = loc.range.start
  return { rel, line: r.line + 1, column: r.character + 1, text: `${rel}:${r.line + 1}:${r.character + 1}` }
}

function sourceLine(rel, line) {
  try { return readFileSync(path.join(ROOT, rel), 'utf8').split(/\r?\n/)[line - 1]?.trim() ?? '' }
  catch { return '' }
}

// ---------------------------------------------------------------------------
// Tools
// ---------------------------------------------------------------------------

const POSITION_ARGS = {
  file: { type: 'string', description: 'F# source file, repo-relative (e.g. "app/Backends.fs") or absolute.' },
  line: { type: 'number', description: '1-based line number, as shown in editors and compiler errors.' },
  symbol: { type: 'string', description: 'Identifier on that line to resolve, e.g. "SessionId". Preferred over `column`.' },
  column: { type: 'number', description: '1-based column. Only needed when `symbol` is ambiguous or absent.' },
}

const TOOLS = [
  {
    name: 'fsharp_definition',
    description:
      'Go to the definition of an F# symbol, following it across project boundaries. Use instead of grepping when you need the one true declaration site — it distinguishes a type from a module or DU case that shares its name.',
    inputSchema: { type: 'object', properties: POSITION_ARGS, required: ['file', 'line'] },
    handler: async ({ absPath, position }) => {
      const res = await server.queryWhenReady('textDocument/definition', {
        textDocument: { uri: pathToFileURL(absPath).href }, position,
      })
      const locs = (Array.isArray(res) ? res : res ? [res] : []).map(fmtLocation)
      if (!locs.length) return 'No definition found. If the symbol comes from a NuGet package rather than this repo, there is no source to jump to.'
      return locs.map((l) => `${l.text}\n    ${sourceLine(l.rel, l.line)}`).join('\n')
    },
  },
  {
    name: 'fsharp_references',
    description:
      'Find every reference to an F# symbol across the whole workspace, resolved by the compiler rather than by text match. Returns only real uses of that exact symbol — not same-named types/modules/fields, and not hits in strings or comments.',
    inputSchema: {
      type: 'object',
      properties: { ...POSITION_ARGS, includeDeclaration: { type: 'boolean', description: 'Include the declaration itself. Default true.' } },
      required: ['file', 'line'],
    },
    handler: async ({ absPath, position, args }) => {
      const res = await server.queryWhenReady('textDocument/references', {
        textDocument: { uri: pathToFileURL(absPath).href },
        position,
        context: { includeDeclaration: args.includeDeclaration !== false },
      }, { emptyIsValid: true })
      const locs = (res ?? []).map(fmtLocation)
      if (!locs.length) return 'No references found.'
      const byFile = new Map()
      for (const l of locs) { if (!byFile.has(l.rel)) byFile.set(l.rel, []); byFile.get(l.rel).push(l) }
      const body = [...byFile.entries()]
        .sort((a, b) => a[0].localeCompare(b[0]))
        .map(([rel, ls]) => `${rel} (${ls.length})\n` + ls.map((l) => `  ${l.line}:${l.column}  ${sourceLine(rel, l.line)}`).join('\n'))
        .join('\n')
      return `${locs.length} reference(s) in ${byFile.size} file(s):\n\n${body}`
    },
  },
  {
    name: 'fsharp_hover',
    description:
      "Show the inferred type and signature of an F# symbol. F# infers most types, so the source often does not state them — this is the only way to see what a `let` binding actually is without building.",
    inputSchema: { type: 'object', properties: POSITION_ARGS, required: ['file', 'line'] },
    handler: async ({ absPath, position }) => {
      const res = await server.queryWhenReady('textDocument/hover', {
        textDocument: { uri: pathToFileURL(absPath).href }, position,
      })
      const c = res?.contents
      const text = typeof c === 'string' ? c : Array.isArray(c) ? c.map((x) => x?.value ?? x).join('\n') : c?.value ?? ''
      // FSAC embeds a VS Code `command:` link for the docs pane. Nothing outside the
      // editor can follow it, so drop it rather than pass the markup along.
      const cleaned = text
        .replace(/<a href='command:[^']*'>[^<]*<\/a>/g, '')
        .replace(/\n{3,}/g, '\n\n')
        .trim()
      return cleaned || 'No type information available at that position.'
    },
  },
  {
    name: 'fsharp_symbol_search',
    description:
      'Search the workspace for F# declarations by name, returning each match with its kind (type, module, function, …) and location. Use to find where something lives when you know the name but not the file.',
    inputSchema: {
      type: 'object',
      properties: { query: { type: 'string', description: 'Symbol name or fragment, e.g. "SessionId".' } },
      required: ['query'],
    },
    needsPosition: false,
    handler: async ({ args }) => {
      const res = await server.queryWhenReady('workspace/symbol', { query: args.query }, { emptyIsValid: true })
      const items = res ?? []
      if (!items.length) return `No symbols matching ${JSON.stringify(args.query)}.`
      // LSP SymbolKind numbers -> the names an F# reader expects.
      const KIND = { 2: 'module', 5: 'class', 6: 'method', 11: 'interface', 12: 'function', 13: 'variable', 14: 'constant', 22: 'struct', 23: 'union case', 8: 'field', 10: 'enum', 26: 'type parameter' }
      return items
        .map((s) => {
          const l = fmtLocation({ uri: s.location.uri, range: s.location.range })
          return `${(KIND[s.kind] ?? `kind ${s.kind}`).padEnd(12)} ${s.name}${s.containerName ? `  (in ${s.containerName})` : ''}\n    ${l.text}`
        })
        .join('\n')
    },
  },
]


// ---------------------------------------------------------------------------
// HTTP transport (replaces the MCP stdio loop)
// ---------------------------------------------------------------------------
import http from 'node:http'
import { appendFileSync } from 'node:fs'

const PORT = +(process.env.LSP_PORT ?? 47811)
const LOG = process.env.LSP_LOG ?? path.join(path.dirname(fileURLToPath(import.meta.url)), 'lsp-calls.jsonl')

async function callTool(name, args) {
  const tool = TOOLS.find((t) => t.name === name)
  if (!tool) throw new Error(`unknown tool: ${name}`)
  await server.start()
  const ctx = { args }
  if (tool.needsPosition !== false) {
    const absPath = resolveFile(args.file)
    server.openFile(absPath)
    ctx.absPath = absPath
    ctx.position = resolvePosition(absPath, args.line, args.column, args.symbol)
  }
  return tool.handler(ctx)
}

http.createServer((req, res) => {
  let body = ''
  req.on('data', (d) => { body += d })
  req.on('end', async () => {
    const t0 = Date.now()
    let msg = {}, out, ok = true
    try {
      msg = JSON.parse(body || '{}')
      if (msg.tool === 'status') out = JSON.stringify({ pid: process.pid, fsac: server.proc?.pid ?? null, projectsLoaded: server.projectsLoaded, projectEvents: server.loadedProjectCount })
      else out = await callTool(msg.tool, msg.args ?? {})
    } catch (e) { ok = false; out = `error: ${e.message}` }
    try { appendFileSync(LOG, JSON.stringify({ ts: new Date().toISOString(), trial: msg.trial ?? null, tool: msg.tool, args: msg.args, ok, ms: Date.now() - t0 }) + '\n') } catch {}
    res.writeHead(ok ? 200 : 500, { 'content-type': 'text/plain' })
    res.end(out)
  })
}).listen(PORT, '127.0.0.1', () => log(`listening on 127.0.0.1:${PORT}, root=${ROOT}`))

for (const sig of ['SIGINT', 'SIGTERM']) process.on(sig, () => { server.proc?.kill(); process.exit(0) })
process.on('exit', () => server.proc?.kill())
server.start().catch((e) => { server.starting = null; log('warm-up failed:', e.message) })
