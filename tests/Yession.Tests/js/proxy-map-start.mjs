// Spawns the proxy example's `main.mjs` and waits for the line naming the stream it
// follows, so a process that dies on its arguments says so here. A module rather than an
// `[<Emit>]` string; ProxyMap.fs supplies the types at the binding.

export default (async function (args, timeoutMs) {
  const { spawn } = await import('node:child_process')
  const child = spawn(process.execPath, ['examples/proxy/main.mjs'].concat(args),
                      { stdio: ['ignore', 'pipe', 'pipe'] })
  let said = ''
  await new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error('sessions-map never announced itself; said:\n' + said)), timeoutMs)
    const watch = (stream) => stream.on('data', (chunk) => {
      said += String(chunk)
      if (/ follows /.test(said)) { clearTimeout(timer); resolve() }
    })
    watch(child.stdout); watch(child.stderr)
    child.on('exit', (code) => { clearTimeout(timer); reject(new Error('sessions-map exited with ' + code + ':\n' + said)) })
  })
  return {
    said: () => said,
    stop: () => new Promise((resolve) => {
      child.on('exit', () => resolve())
      try { child.kill('SIGTERM') } catch (_) { resolve() }
    })
  }
})
