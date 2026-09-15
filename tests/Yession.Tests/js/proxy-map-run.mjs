// Runs the proxy example's `main.mjs` to completion and hands back its exit code and
// stderr — what the cases about refused arguments read. A module rather than an
// `[<Emit>]` string, for the same reason as its sibling beside it.

export default (async function (args) {
  const { spawn } = await import('node:child_process')
  return await new Promise((resolve) => {
    const child = spawn(process.execPath, ['examples/proxy/main.mjs'].concat(args),
                        { stdio: ['ignore', 'pipe', 'pipe'] })
    let stderr = ''
    child.stderr.on('data', (chunk) => { stderr += String(chunk) })
    child.on('exit', (code) => resolve({ code, stderr }))
  })
})
