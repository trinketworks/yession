// Opening one serial port: the addon's own open, the data/close/error handlers, and the
// watchdog that notices a device node disappearing.
//
// A module rather than an `[<Emit>]` string because the lifetime rules here — one `finish`,
// an interval that must be cleared and unref'd — are what a reader checks. `Ports.fs` binds
// it with a typed import, and the doc comment there says why the watchdog exists.

export default async function openPort (path, baud, dataBits, stopBits, parity, onData, onClose) {
  try {
    const fs = await import('node:fs')
    const { SerialPort } = await import('serialport')
    const port = await new Promise((resolve, reject) => {
      const p = new SerialPort(
        { path: path, baudRate: baud, dataBits: dataBits, stopBits: stopBits, parity: parity, autoOpen: true },
        (err) => { if (err) reject(err); else resolve(p) })
    })
    let closed = false
    let watchdog = null
    const finish = (why) => {
      if (watchdog) { clearInterval(watchdog); watchdog = null }
      if (!closed) { closed = true; onClose(why) }
    }
    port.on('data', (chunk) => onData(chunk.toString('utf8')))
    port.on('close', () => finish('the port closed'))
    port.on('error', (err) => finish(String((err && err.message) || err)))
    if (fs.existsSync(path)) {
      watchdog = setInterval(() => {
        if (fs.existsSync(path)) return
        // Release the fd as well as reporting it: the device is gone, so the handle is
        // never becoming useful again, and `close` on a dead node can itself throw.
        try { port.close(() => {}) } catch (e) {}
        finish('the device went away')
      }, 500)
      // Never a reason for the process to stay alive.
      if (watchdog.unref) watchdog.unref()
    }
    return {
      ok: true,
      reason: '',
      write: (text) => { try { port.write(text) } catch (e) { finish(String((e && e.message) || e)) } },
      close: () => { try { port.close(() => {}) } catch (e) { finish('closed') } }
    }
  } catch (err) {
    return { ok: false, reason: String((err && err.message) || err), write: () => {}, close: () => {} }
  }
}
