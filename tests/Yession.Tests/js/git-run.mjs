// One git run — bounded, with this box's configuration kept out of it — resolved as the
// record GitGateway.fs reads. A module rather than an `[<Emit>]` string so the environment
// it assembles is JavaScript in a file, checked against the F# only at the binding.

export default (function (cp, args, cwd, extra) {
  const env = { ...process.env, GIT_CONFIG_GLOBAL: '/dev/null', GIT_CONFIG_SYSTEM: '/dev/null', GIT_TERMINAL_PROMPT: '0',
                GIT_AUTHOR_NAME: 'fixture', GIT_AUTHOR_EMAIL: 'f@x', GIT_COMMITTER_NAME: 'fixture', GIT_COMMITTER_EMAIL: 'f@x' }
  for (const [k, v] of extra) env[k] = v
  return new Promise((resolve) => {
    cp.execFile('git', args, { cwd, env, encoding: 'utf8', maxBuffer: 64 * 1024 * 1024, timeout: 20000, killSignal: 'SIGKILL' }, (error, stdout, stderr) => {
      resolve({ Status: error ? (typeof error.code === 'number' ? error.code : -1) : 0, Stdout: stdout ?? '', Stderr: stderr ?? '' })
    })
  })
})
