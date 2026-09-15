// Spawns the jumpstarter exporter and the provider, and resolves once the provider has
// announced where its MCP endpoint is. Two processes, a port handshake and a readiness
// parser is a program rather than an expression, so it is a module with a typed binding in
// Jumpstarter.fs.

export default (async function (timeoutMs) {
  const { spawn } = await import('node:child_process')
  const net = await import('node:net')

  const project = 'examples/jumpstarter'
  const freePort = () => new Promise((resolve) => {
    const probe = net.createServer()
    probe.listen(0, '127.0.0.1', () => {
      const port = probe.address().port
      probe.close(() => resolve(port))
    })
  })

  const grpc = await freePort()
  const children = []
  const start = (args, env) => {
    const child = spawn('uv', ['run', '--project', project].concat(args),
                        { env: Object.assign({}, process.env, env), stdio: ['ignore', 'pipe', 'pipe'] })
    children.push(child)
    return child
  }

  // Waits for a line on either stream, so a process that dies before printing it fails here
  // with its own words rather than as a timeout.
  const awaitLine = (child, pattern, what) => new Promise((resolve, reject) => {
    let seen = ''
    const timer = setTimeout(() => reject(new Error(what + ' never printed ' + pattern + '; saw:\n' + seen)), timeoutMs)
    const watch = (stream) => stream.on('data', (chunk) => {
      seen += String(chunk)
      const found = seen.match(pattern)
      if (found) { clearTimeout(timer); resolve(found) }
    })
    watch(child.stdout); watch(child.stderr)
    child.on('exit', (code) => { clearTimeout(timer); reject(new Error(what + ' exited with ' + code + ':\n' + seen)) })
  })

  const exporter = start(
    ['jmp', 'run', '--exporter-config', project + '/tests/exporter.yaml',
     '--tls-grpc-listener', '127.0.0.1:' + grpc, '--tls-grpc-insecure'], {})
  await awaitLine(exporter, /Session server started/, 'the exporter')

  const provider = start(['jumpstarter-provider'], {
    JUMPSTARTER_HOST: '127.0.0.1:' + grpc,
    JUMPSTARTER_PROVIDER_PORT: '0',
    // Longer than this suite can possibly take, so a claim never expires mid-test: what is
    // under test here is the protocol, not the timeout (that is the pytest suite's).
    JUMPSTARTER_PROVIDER_TTL: '600'
  })
  // The readiness line names both ends — `MCP at <url>, exporter at <host:port>` — so the
  // url stops at the comma. `\S+` would take it with the comma attached, and a url with a
  // comma on the end fails as a connection refused rather than as anything legible.
  const announced = await awaitLine(provider, /MCP at (http:\/\/[^\s,]+)/, 'the provider')

  return {
    url: announced[1],
    stop: () => new Promise((resolve) => {
      for (const child of children) { try { child.kill('SIGTERM') } catch (_) {} }
      setTimeout(resolve, 200)
    })
  }
})
