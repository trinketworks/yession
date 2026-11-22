#!/usr/bin/env node
// follow: one child process for each announcement that matches, and no child for one that
// does not.
//
// A session tells the hosts of a deployment what it has, as announcements on the Manager's
// registry stream (docs/deployment.md, "Announcements"). A deployment often wants one process
// beside each such thing: a listener for each sandbox, a route for each port. This program
// holds `/sessions/stream` open and keeps that set of processes equal to the set of
// announcements that match.
//
//   node follow.mjs --manager unix:/var/lib/yession/manager.sock --as ssh-follower \
//     --kind sandbox --where state=running --where resources~remote-ssh \
//     --run node listen.mjs --listen 127.0.0.1:0 -- /run/yession-ssh/serve
//
// The rules of the stream decide the design:
//
//   * A frame is the WHOLE set. Each frame is applied whole. An announcement that is not in
//     the newest frame is gone, and its child stops.
//   * The end of the stream is NOT the end of the sessions. A Manager restart closes the
//     stream, and the first frame after the reconnect is the truth again. The children stay.
//   * A REFUSED connection is the end of the sessions. A session is a child of the Manager,
//     so no Manager means no session. Each child stops.
//   * A kind that this program was not asked for is ignored. A field of `data` that no test
//     names is ignored. A newer session can thus say more, and this program does not break.
//
// The child gets the facts in its environment (see `factsOf`). It runs in its own process
// group, and the whole group stops with it, so a child can start helpers and not track them.
//
// Node only, no dependencies. Nothing here reads Yession's code: the contract is the stream.

import { spawn } from 'node:child_process'
import { request } from 'node:http'

const usage = `usage: node follow.mjs --kind KIND [--where TEST]... [--manager URL] [--as SUBJECT]
                      [--grace SECONDS] --run COMMAND [ARGUMENT ...]

  --kind KIND       the kind of announcement to follow, for example sandbox
  --where TEST      a test on the data of the announcement. Each test must pass.
                      FIELD=VALUE   the field has this value
                      FIELD~ITEM    the field is a list that holds this item
  --manager URL     the Manager (default http://127.0.0.1:8321), or unix:PATH for a Manager
                    that listens on a socket (--auth trusted-headers)
  --as SUBJECT      the x-yession-user that this subscriber asserts (default follow)
  --grace SECONDS   the time between TERM and KILL when a child stops (default 5)
  --run COMMAND     the child. Each word after --run belongs to the child.

The child gets these variables:
  YESSION_SESSION_ID, YESSION_SESSION_NAME, YESSION_SESSION_PORT
  YESSION_ANNOUNCEMENT_KIND, YESSION_ANNOUNCEMENT_NAME
  YESSION_ANNOUNCEMENT_DATA   the data, as JSON
  YESSION_DATA_<FIELD>        each field of the data that is text, a number or a boolean`

function fail(message) {
  console.error(`follow: ${message}\n\n${usage}`)
  process.exit(64) // EX_USAGE
}

function log(message) {
  console.error(`follow: ${message}`)
}

// --- the command line --------------------------------------------------------------------

const argv = process.argv.slice(2)
if (argv.includes('--help') || argv.includes('-h')) {
  console.log(usage)
  process.exit(0)
}
const runAt = argv.indexOf('--run')
if (runAt < 0) fail('--run is required')
const command = argv.slice(runAt + 1)
if (command.length === 0) fail('--run needs a command')

const options = { kind: null, where: [], manager: 'http://127.0.0.1:8321', as: 'follow', grace: '5' }
const own = argv.slice(0, runAt)
for (let i = 0; i < own.length; i += 2) {
  const name = own[i]
  const value = own[i + 1]
  if (!name.startsWith('--') || !(name.slice(2) in options)) fail(`unknown option ${name}`)
  if (value === undefined) fail(`${name} needs a value`)
  if (name === '--where') options.where.push(value)
  else options[name.slice(2)] = value
}
if (!options.kind) fail('--kind is required')
const graceMs = Number(options.grace) * 1000
if (!Number.isFinite(graceMs) || graceMs < 0) fail('--grace is not a number of seconds')

/** `FIELD=VALUE` or `FIELD~ITEM`, cut at the first of the two marks. */
const tests = options.where.map((text) => {
  const at = text.search(/[=~]/)
  if (at <= 0) fail(`--where ${text}: write FIELD=VALUE or FIELD~ITEM`)
  return { field: text.slice(0, at), mark: text[at], value: text.slice(at + 1) }
})

const isScalar = (value) => ['string', 'number', 'boolean'].includes(typeof value)

/** One test on the data. A field that is absent passes no test. */
function passes(data, test) {
  const found = data[test.field]
  if (test.mark === '=') return isScalar(found) && String(found) === test.value
  return Array.isArray(found) && found.some((item) => isScalar(item) && String(item) === test.value)
}

// --- what a frame asks for ---------------------------------------------------------------

/**
 * The announcements of a frame that match, by key. The key is the identity that the stream
 * documents: the session id, the kind and the name.
 */
function wantedOf(frame) {
  const wanted = new Map()
  for (const session of Array.isArray(frame?.sessions) ? frame.sessions : []) {
    if (typeof session?.id !== 'string') continue
    for (const announcement of Array.isArray(session.announcements) ? session.announcements : []) {
      if (announcement?.kind !== options.kind || typeof announcement.name !== 'string') continue
      const data = announcement.data !== null && typeof announcement.data === 'object' ? announcement.data : {}
      if (!tests.every((test) => passes(data, test))) continue
      wanted.set(JSON.stringify([session.id, announcement.kind, announcement.name]), {
        session,
        announcement,
        data,
        // The child restarts when the data changes. The name of a session changes often and
        // is not a reason to restart.
        print: JSON.stringify(data),
      })
    }
  }
  return wanted
}

/** The environment of a child: the facts of its announcement. */
function factsOf({ session, announcement, data, print }) {
  const facts = {
    YESSION_SESSION_ID: session.id,
    YESSION_SESSION_NAME: typeof session.name === 'string' ? session.name : '',
    YESSION_SESSION_PORT: Number.isInteger(session.port) ? String(session.port) : '',
    YESSION_ANNOUNCEMENT_KIND: announcement.kind,
    YESSION_ANNOUNCEMENT_NAME: announcement.name,
    YESSION_ANNOUNCEMENT_DATA: print,
  }
  for (const [field, value] of Object.entries(data)) {
    if (!isScalar(value)) continue
    facts[`YESSION_DATA_${field.toUpperCase().replace(/[^A-Z0-9]/g, '_')}`] = String(value)
  }
  return facts
}

// --- the children ------------------------------------------------------------------------

const firstRestartMs = 1000
const lastRestartMs = 30000
const steadyMs = 60000

/** key -> { want, child, restartMs, timer } */
const kept = new Map()
let leaving = false

/** A key as a person reads it. */
const said = (key) => JSON.parse(key).join(' ')

function start(key) {
  const entry = kept.get(key)
  entry.timer = null
  const startedAt = Date.now()
  // `detached` makes the child the leader of a new process group. A signal to the negative
  // pid then reaches each process that the child started.
  const child = spawn(command[0], command.slice(1), {
    env: { ...process.env, ...factsOf(entry.want) },
    stdio: 'inherit',
    detached: true,
  })
  entry.child = child
  log(`${said(key)}: started ${command[0]} (pid ${child.pid ?? 'none'})`)
  child.on('error', (error) => log(`${said(key)}: ${command[0]} did not start: ${error.message}`))
  child.on('close', (code, signal) => {
    if (entry.child !== child) return
    entry.child = null
    if (kept.get(key) !== entry) return
    // The child ended by itself. It is still wanted, so it starts again, later each time.
    if (Date.now() - startedAt > steadyMs) entry.restartMs = firstRestartMs
    log(`${said(key)}: ended (${signal ?? code}); again in ${entry.restartMs / 1000} s`)
    entry.timer = setTimeout(() => start(key), entry.restartMs)
    entry.restartMs = Math.min(entry.restartMs * 2, lastRestartMs)
  })
}

function signalGroup(child, signal) {
  if (!child.pid) return
  try {
    process.kill(-child.pid, signal)
  } catch {
    // The group is gone.
  }
}

/** Stop the child of an entry: TERM to its group, then KILL after the grace time. */
function stop(key, why) {
  const entry = kept.get(key)
  if (!entry) return Promise.resolve()
  kept.delete(key)
  if (entry.timer) clearTimeout(entry.timer)
  const child = entry.child
  if (!child) return Promise.resolve()
  log(`${said(key)}: stopping (${why})`)
  return new Promise((resolve) => {
    const hard = setTimeout(() => signalGroup(child, 'SIGKILL'), graceMs)
    child.on('close', () => {
      clearTimeout(hard)
      // A helper of the child can ignore TERM and outlive the leader.
      signalGroup(child, 'SIGKILL')
      resolve()
    })
    signalGroup(child, 'SIGTERM')
  })
}

// A key whose old child is on its way out for a replacement. A frame that arrives in that
// time must not start a second child for the key.
const replacing = new Map()

/** The facts changed. The old child has the old facts, so a new child replaces it. */
function replace(key, want) {
  replacing.set(key, want)
  stop(key, 'its data changed').then(() => {
    const newest = replacing.get(key)
    replacing.delete(key)
    if (newest && !leaving) keep(key, newest)
  })
}

function keep(key, want) {
  kept.set(key, { want, child: null, restartMs: firstRestartMs, timer: null })
  start(key)
}

/** Make the children equal to what the frame asks for. */
function apply(frame, why) {
  const wanted = wantedOf(frame)
  for (const key of [...replacing.keys()]) {
    // The newest frame decides what the replacement gets, or that there is none.
    replacing.set(key, wanted.get(key) ?? null)
  }
  for (const [key, entry] of [...kept]) {
    const want = wanted.get(key)
    if (!want) stop(key, why)
    else if (want.print !== entry.want.print) replace(key, want)
    else entry.want = want
  }
  for (const [key, want] of wanted) {
    if (!kept.has(key) && !replacing.has(key)) keep(key, want)
  }
}

// --- the stream --------------------------------------------------------------------------

const socketPath = options.manager.startsWith('unix:') ? options.manager.slice('unix:'.length) : null
const manager = socketPath ? 'http://yession-manager' : options.manager.replace(/\/+$/, '')
const stream = `${manager}/sessions/stream`
const where = socketPath ? `${socketPath} (/sessions/stream)` : stream
const reconnectDelayMs = 2000
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms))

/**
 * Hold the stream open and apply each frame. Resolves when the stream ends. Throws when no
 * connection was made, which means that the Manager is gone.
 */
async function follow(signal) {
  const headers = { accept: 'text/event-stream', 'x-yession-user': options.as }
  const response = socketPath ? await overSocket(headers, signal) : await fetch(stream, { headers, signal })
  if (!response.ok) {
    const body = (await response.text().catch(() => '')).trim()
    log(`${where} answered ${response.status}${body ? `: ${body}` : ''}; the children stay as they are`)
    return
  }
  const decoder = new TextDecoder()
  let buffered = ''
  let data = []
  try {
    for await (const chunk of response.body) {
      buffered += decoder.decode(chunk, { stream: true })
      let newline
      while ((newline = buffered.indexOf('\n')) >= 0) {
        const line = buffered.slice(0, newline).replace(/\r$/, '')
        buffered = buffered.slice(newline + 1)
        if (line === '') {
          if (data.length > 0) applyText(data.join('\n'))
          data = []
        } else if (line.startsWith('data:')) {
          data.push(line.slice(5).replace(/^ /, ''))
        }
      }
    }
  } catch (error) {
    // A stream that breaks is a stream that ended. Only a connection that was never made
    // says that there is no Manager, and the next attempt finds that out.
    if (!signal.aborted) log(`the stream broke (${error.code ?? error.message})`)
  }
}

/** `fetch` cannot dial a socket, and `node:http` can. The answer has the shape of `fetch`. */
function overSocket(headers, signal) {
  return new Promise((resolve, reject) => {
    const sent = request({ socketPath, path: '/sessions/stream', headers, signal }, (answer) => {
      const status = answer.statusCode ?? 0
      resolve({
        ok: status >= 200 && status < 300,
        status,
        body: answer,
        text: async () => {
          let text = ''
          for await (const chunk of answer) text += chunk
          return text
        },
      })
    })
    sent.on('error', reject)
    sent.end()
  })
}

function applyText(json) {
  let frame
  try {
    frame = JSON.parse(json)
  } catch {
    log(`a frame was not JSON and was ignored: ${json.slice(0, 200)}`)
    return
  }
  apply(frame, 'its announcement is gone')
}

const controller = new AbortController()
for (const signal of ['SIGTERM', 'SIGINT']) {
  process.on(signal, async () => {
    if (leaving) return
    leaving = true
    log(`${signal}: stopping each child`)
    controller.abort()
    await Promise.all([...kept.keys()].map((key) => stop(key, 'the follower stops')))
    process.exit(0)
  })
}

log(`follows ${where} as ${options.as} for the kind ${options.kind}`)
while (!leaving) {
  try {
    await follow(controller.signal)
    if (!leaving) log('the stream ended; reconnecting')
  } catch (error) {
    if (leaving) break
    log(`${where} is unreachable (${error.cause?.code ?? error.code ?? error.message}): no Manager, so no sessions`)
    apply(null, 'no Manager')
  }
  await sleep(reconnectDelayMs)
}
