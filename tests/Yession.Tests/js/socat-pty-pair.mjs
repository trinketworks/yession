// Spawns socat, reads the two PTY paths it announces, and opens the far end as the device
// the `SerialEngine` suite plays against. A module rather than an `[<Emit>]` string: what
// is delicate here is why the far end is a tty handle, and the explanation has to sit in
// JavaScript a reader and a formatter can both follow.

export default (async () => {
  const { spawn } = await import('node:child_process')
  const fs = await import('node:fs')
  const tty = await import('node:tty')
  const socat = process.env.YESSION_BIN_SOCAT || 'socat'
  const { child, paths } = await new Promise((resolve, reject) => {
    const child = spawn(socat, ['-d', '-d', 'pty,raw,echo=0', 'pty,raw,echo=0'], { stdio: ['ignore', 'ignore', 'pipe'] })
    // Matched against everything socat has said so far rather than against each chunk:
    // stderr is a byte stream with no promise of line alignment.
    let noise = ''
    const timer = setTimeout(() => {
      child.kill()
      reject(new Error('socat did not announce two PTYs in time; it said: ' + JSON.stringify(noise)))
    }, 10000)
    child.stderr.on('data', (chunk) => {
      noise += chunk.toString('utf8')
      const found = [...noise.matchAll(/PTY is (\S+)\s/g)].map(m => m[1])
      if (found.length >= 2) { clearTimeout(timer); resolve({ child, paths: found }) }
    })
    child.on('error', (e) => { clearTimeout(timer); reject(e) })
  })
  // The far end, opened directly rather than through serialport: one side of this test has to
  // be something other than the code under test, or it proves only that our engine agrees
  // with itself.
  //
  // `tty.ReadStream` and not `fs.createReadStream`, and the difference is not cosmetic. An fs
  // read stream reads through the libuv THREADPOOL, and a blocking read on a pty is not
  // cancellable: `destroy()` returns while the read is still parked in a worker thread, and
  // closing the fd then frees the NUMBER for reuse. The next `spawn` in the file gets it back
  // as socat's stderr pipe, and the stale read swallows the announcement — which presents as
  // "socat did not announce two PTYs in time; it said: ''", from a socat that is running
  // perfectly. A tty handle is epoll-driven on the event loop with nothing in flight to
  // outlive it. (It does not own the fd, so the close below is still ours to make, and is
  // safe to make.)
  const fd = fs.openSync(paths[1], 'r+')
  let received = ''
  const reader = new tty.ReadStream(fd)
  reader.on('data', (c) => { received += c.toString('utf8') })
  reader.on('error', () => {})
  return {
    ours: paths[0],
    theirs: paths[1],
    send: (text) => { try { fs.writeSync(fd, text) } catch (e) {} },
    received: () => received,
    stop: () => { try { reader.destroy(); fs.closeSync(fd) } catch (e) {} ; child.kill() }
  }
})
