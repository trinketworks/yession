#!/usr/bin/env node
// listen: a listener in front of the door of one sandbox. Each connection becomes one entry.
//
// `yession-enter` connects its own input and output to a command in a sandbox
// (docs/deployment.md, "The door of a session"). A person arrives on a network connection.
// This program is the piece between the two: it accepts a connection and runs one
// `yession-enter` with that connection as its input and output.
//
//   node listen.mjs --listen 127.0.0.1:2201 --door /run/yession/doors/<id>.sock \
//     --sandbox octo/hello:dev --via ssh -- /run/yession-ssh/serve
//
// Under `follow.mjs` the door and the sandbox come from the environment, so the same command
// line serves each sandbox.
//
// WHO entered is the hard part. This program can not see the person: the ingress in front of
// it can. An ingress that speaks the PROXY protocol (`tailscale serve --tcp --proxy-protocol`)
// sends the address of the peer before the first byte of the connection. `--proxy-protocol`
// reads that header, and `--whois` asks a command of the host who has that address. The
// answer is the subject of the entry. With no `--whois`, each entry has the subject of `--as`.
//
// A connection whose header is wrong, or whose peer the host does not know, is closed. This
// program never starts an entry for a person that it could not name when it was told to
// name each person.
//
// Node only, no dependencies.

import { spawn, execFile } from 'node:child_process'
import { createServer } from 'node:net'
import { unlinkSync } from 'node:fs'

const usage = `usage: node listen.mjs --listen ADDRESS [--door PATH] [--sandbox NAME] [--via LABEL]
                      [--as SUBJECT] [--proxy-protocol] [--whois COMMAND] [--enter PATH]
                      -- COMMAND [ARGUMENT ...]

  --listen ADDRESS   HOST:PORT on this host (port 0 lets the system choose), or unix:PATH
  --door PATH        the door of the session (default $YESSION_DATA_DOOR)
  --sandbox NAME     the sandbox (default $YESSION_ANNOUNCEMENT_NAME)
  --via LABEL        the label of each entry (default ssh)
  --as SUBJECT       the subject of an entry that --whois did not name (default remote)
  --proxy-protocol   each connection starts with a PROXY protocol header, version 1 or 2
  --whois COMMAND    a command that gets the address of the peer as its last argument and
                     prints JSON with UserProfile.LoginName (tailscale whois --json) or login.
                     Needs --proxy-protocol.
  --enter PATH       the yession-enter to run (default yession-enter)
  COMMAND            the command of each entry, which runs in the sandbox

The line "listen: listening on ADDRESS" on the error output gives the address in use.`

function fail(message) {
  console.error(`listen: ${message}\n\n${usage}`)
  process.exit(64) // EX_USAGE
}

function log(message) {
  console.error(`listen: ${message}`)
}

// --- the command line --------------------------------------------------------------------

const argv = process.argv.slice(2)
if (argv.includes('--help') || argv.includes('-h')) {
  console.log(usage)
  process.exit(0)
}
const cut = argv.indexOf('--')
if (cut < 0) fail('write -- and then the command of an entry')
const command = argv.slice(cut + 1)
if (command.length === 0) fail('the command of an entry is empty')

const options = {
  listen: null,
  door: process.env.YESSION_DATA_DOOR ?? null,
  sandbox: process.env.YESSION_ANNOUNCEMENT_NAME ?? null,
  via: 'ssh',
  as: 'remote',
  'proxy-protocol': false,
  whois: null,
  enter: 'yession-enter',
}
const own = argv.slice(0, cut)
for (let i = 0; i < own.length; i++) {
  const name = own[i]
  if (!name.startsWith('--') || !(name.slice(2) in options)) fail(`unknown option ${name}`)
  if (name === '--proxy-protocol') options['proxy-protocol'] = true
  else if (own[i + 1] === undefined) fail(`${name} needs a value`)
  else options[name.slice(2)] = own[++i]
}
if (!options.listen) fail('--listen is required')
if (!options.door) fail('--door is required (or YESSION_DATA_DOOR)')
if (!options.sandbox) fail('--sandbox is required (or YESSION_ANNOUNCEMENT_NAME)')
if (options.whois && !options['proxy-protocol']) fail('--whois needs --proxy-protocol, which gives the address of the peer')
const whois = options.whois ? options.whois.split(/\s+/).filter(Boolean) : null

// --- the PROXY protocol ------------------------------------------------------------------
//
// https://www.haproxy.org/download/1.8/doc/proxy-protocol.txt. Version 1 is one line of
// text. Version 2 is a signature of 12 bytes and a binary block. Both give the source
// address, which is all that this program reads.

const v2Signature = Buffer.from([0x0d, 0x0a, 0x0d, 0x0a, 0x00, 0x0d, 0x0a, 0x51, 0x55, 0x49, 0x54, 0x0a])
const v1Longest = 107

/**
 * Read the header from what arrived so far.
 *   { more: true }                      not enough bytes yet
 *   { bad: reason }                     not a header
 *   { peer: address | null, rest }      the header, and the bytes after it
 * `peer` is null for a header that names no peer (`UNKNOWN`, or a LOCAL command).
 */
function readProxyHeader(bytes) {
  if (bytes.length >= 12 && bytes.subarray(0, 12).equals(v2Signature)) {
    if (bytes.length < 16) return { more: true }
    const version = bytes[12] >> 4
    const isProxy = (bytes[12] & 0x0f) === 1
    const family = bytes[13] >> 4
    const length = bytes.readUInt16BE(14)
    if (version !== 2) return { bad: `PROXY version ${version}` }
    if (bytes.length < 16 + length) return { more: true }
    const block = bytes.subarray(16, 16 + length)
    const rest = bytes.subarray(16 + length)
    if (!isProxy) return { peer: null, rest }
    if (family === 1 && length >= 12) return { peer: [...block.subarray(0, 4)].join('.'), rest }
    if (family === 2 && length >= 36) {
      const groups = []
      for (let i = 0; i < 16; i += 2) groups.push(block.readUInt16BE(i).toString(16))
      return { peer: groups.join(':'), rest }
    }
    return { peer: null, rest }
  }
  // A prefix of the version 2 signature is not yet a decision.
  if (bytes.length < 12 && v2Signature.subarray(0, bytes.length).equals(bytes)) return { more: true }
  const end = bytes.indexOf('\r\n')
  if (end < 0) return bytes.length > v1Longest ? { bad: 'no PROXY header' } : { more: true }
  if (end > v1Longest) return { bad: 'no PROXY header' }
  const words = bytes.subarray(0, end).toString('latin1').split(' ')
  const rest = bytes.subarray(end + 2)
  if (words[0] !== 'PROXY') return { bad: 'no PROXY header' }
  if (words[1] === 'UNKNOWN') return { peer: null, rest }
  if ((words[1] !== 'TCP4' && words[1] !== 'TCP6') || words.length !== 6) return { bad: 'a PROXY header that this program can not read' }
  if (!/^[0-9a-fA-F:.]+$/.test(words[2])) return { bad: 'a PROXY header with a bad address' }
  return { peer: words[2], rest }
}

/** Ask the host who has this address. Rejects when the host does not know. */
function loginOf(peer) {
  return new Promise((resolve, reject) => {
    execFile(whois[0], [...whois.slice(1), peer], { timeout: 10000 }, (error, stdout) => {
      if (error) return reject(new Error(`${whois[0]} did not name ${peer}: ${error.message.trim()}`))
      try {
        const answer = JSON.parse(stdout)
        const login = answer?.UserProfile?.LoginName ?? answer?.login
        if (typeof login === 'string' && login !== '') return resolve(login)
        reject(new Error(`${whois[0]} gave no login for ${peer}`))
      } catch {
        reject(new Error(`${whois[0]} did not print JSON for ${peer}`))
      }
    })
  })
}

// --- one connection, one entry -----------------------------------------------------------

const headerWaitMs = 10000
let entries = 0

function enter(socket, subject, first) {
  const number = ++entries
  const child = spawn(
    options.enter,
    ['--door', options.door, '--as', subject, '--via', options.via, options.sandbox, '--', ...command],
    { stdio: ['pipe', 'pipe', 'inherit'] },
  )
  log(`entry ${number}: ${subject} enters ${options.sandbox}`)
  child.on('error', (error) => {
    log(`entry ${number}: ${options.enter} did not start: ${error.message}`)
    socket.destroy()
  })
  child.stdin.on('error', () => {})
  socket.on('error', () => {})
  if (first.length > 0) child.stdin.write(first)
  // `pipe` holds back the side that is faster, so a large upload does not fill memory here.
  socket.pipe(child.stdin)
  child.stdout.pipe(socket)
  // The person went away. `yession-enter` ends the command when its own input ends and it
  // is told to stop, so both happen here.
  socket.on('close', () => {
    if (child.exitCode === null && child.signalCode === null) child.kill('SIGTERM')
  })
  child.on('close', (code, signal) => {
    log(`entry ${number}: ${subject} left ${options.sandbox} (${signal ?? code})`)
    socket.end()
  })
}

function accept(socket) {
  if (!options['proxy-protocol']) return enter(socket, options.as, Buffer.alloc(0))
  let seen = Buffer.alloc(0)
  const refuse = (why) => {
    log(`a connection was closed: ${why}`)
    socket.destroy()
  }
  const late = setTimeout(() => refuse('no PROXY header in time'), headerWaitMs)
  socket.on('error', () => {})
  socket.on('close', () => clearTimeout(late))
  const onData = (chunk) => {
    seen = Buffer.concat([seen, chunk])
    const header = readProxyHeader(seen)
    if (header.more) return
    clearTimeout(late)
    socket.off('data', onData)
    socket.pause()
    if (header.bad) return refuse(header.bad)
    if (!whois) return enter(socket, options.as, header.rest)
    if (header.peer === null) return refuse('the PROXY header names no peer')
    loginOf(header.peer).then(
      (login) => (socket.destroyed ? undefined : enter(socket, login, header.rest)),
      (error) => refuse(error.message),
    )
  }
  socket.on('data', onData)
}

// --- the listener ------------------------------------------------------------------------

// `allowHalfOpen`: a person who closes their side still gets the last output of the command.
const server = createServer({ allowHalfOpen: true }, accept)
server.on('error', (error) => {
  console.error(`listen: can not listen on ${options.listen}: ${error.message}`)
  process.exit(1)
})

const unixPath = options.listen.startsWith('unix:') ? options.listen.slice('unix:'.length) : null
if (unixPath) {
  try {
    unlinkSync(unixPath)
  } catch {
    // No old socket.
  }
  server.listen(unixPath, () => log(`listening on unix:${unixPath}`))
} else {
  const at = options.listen.lastIndexOf(':')
  const port = Number(options.listen.slice(at + 1))
  if (at <= 0 || !Number.isInteger(port) || port < 0 || port > 65535) fail('--listen is HOST:PORT or unix:PATH')
  const host = options.listen.slice(0, at).replace(/^\[|\]$/g, '')
  server.listen(port, host, () => {
    const bound = server.address()
    log(`listening on ${bound.family === 'IPv6' ? `[${bound.address}]` : bound.address}:${bound.port}`)
  })
}

for (const signal of ['SIGTERM', 'SIGINT']) {
  process.on(signal, () => {
    // The entries are children in this process group. The follower signals the group, so
    // each `yession-enter` gets the signal too.
    if (unixPath) {
      try {
        unlinkSync(unixPath)
      } catch {
        // Already gone.
      }
    }
    process.exit(0)
  })
}
