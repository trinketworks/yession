// github.com's git endpoint, played by `git http-backend` over CGI: the header split, the
// status line, and the body passed through as it arrives. A module rather than an
// `[<Emit>]` string because CGI header parsing is where a fixture like this goes wrong, and
// it can only be checked by being read.

export default (function (cp, http, root, seen) {
  const server = http.createServer((req, res) => {
    seen.push(req.headers['authorization'] || '')
    const u = new URL(req.url, 'http://x')
    const env = { ...process.env,
      GIT_PROJECT_ROOT: root, GIT_HTTP_EXPORT_ALL: '1', PATH_INFO: u.pathname, QUERY_STRING: u.search.slice(1),
      REQUEST_METHOD: req.method, CONTENT_TYPE: req.headers['content-type'] || '', REMOTE_USER: 'fixture', REMOTE_ADDR: '127.0.0.1',
      GIT_CONFIG_GLOBAL: '/dev/null', GIT_CONFIG_SYSTEM: '/dev/null',
      GIT_CONFIG_COUNT: '1', GIT_CONFIG_KEY_0: 'http.receivepack', GIT_CONFIG_VALUE_0: 'true' }
    if (req.headers['content-length']) env.CONTENT_LENGTH = req.headers['content-length']
    if (req.headers['content-encoding']) env.HTTP_CONTENT_ENCODING = req.headers['content-encoding']
    if (req.headers['git-protocol']) env.HTTP_GIT_PROTOCOL = req.headers['git-protocol']
    const child = cp.spawn('git', ['http-backend'], { env, stdio: ['pipe', 'pipe', 'inherit'] })
    req.pipe(child.stdin)
    let head = Buffer.alloc(0)
    let headed = false
    child.stdout.on('data', (chunk) => {
      if (headed) { res.write(chunk); return }
      head = Buffer.concat([head, chunk])
      const i = head.indexOf('\r\n\r\n')
      if (i < 0) return
      let status = 200
      const headers = {}
      for (const line of head.subarray(0, i).toString().split('\r\n')) {
        const j = line.indexOf(':')
        const k = line.slice(0, j).trim().toLowerCase()
        const v = line.slice(j + 1).trim()
        if (k === 'status') status = parseInt(v, 10)
        else headers[k] = v
      }
      res.writeHead(status, headers)
      headed = true
      const rest = head.subarray(i + 4)
      if (rest.length) res.write(rest)
    })
    child.stdout.on('end', () => { if (!headed) res.writeHead(500); res.end() })
  })
  return server
})
