// Drives the agent CLI's spawner the way the SDK does: write stdin, close it, and resolve
// with the output and the exit code. A module rather than an `[<Emit>]` string, which would
// paste its `child` and `out` bindings into whatever scope the call sits in (YES003).

export default (function (spawner, command, args, cwd, env, stdin) { return (
(new Promise((resolve) => {
  const child = spawner({ command: command, args: args, cwd: cwd, env: Object.fromEntries(env) })
  let out = ''
  child.stdout.on('data', (d) => { out += String(d) })
  child.on('exit', (code) => resolve([out, code == null ? -1 : code]))
  child.on('error', (e) => resolve([String((e && e.message) || e), -1]))
  child.stdin.write(stdin)
  child.stdin.end()
}))
) })
