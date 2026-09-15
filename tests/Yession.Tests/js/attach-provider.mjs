// The loopback WebSocket provider the `Attach` suite drives — and the reference
// implementation `docs/streams.md` points an outside implementer at. Seventy lines of
// RFC 6455 frame handling is code somebody has to read, so it is JavaScript here and a
// typed import in Attach.fs rather than a string no tool can format.

export default (async () => {
  const http = await import('node:http')
  const crypto = await import('node:crypto')
  const GUID = '258EAFA5-E914-47DA-95CA-C5AB0DC85B11'
  const frame = (opcode, payload) => {
    const body = Buffer.from(payload)
    const head = body.length < 126
      ? Buffer.from([0x80 | opcode, body.length])
      : Buffer.concat([Buffer.from([0x80 | opcode, 126]), (() => { const b = Buffer.alloc(2); b.writeUInt16BE(body.length); return b })()])
    return Buffer.concat([head, body])
  }
  const server = http.createServer((_, res) => { res.statusCode = 426; res.end() })
  server.on('upgrade', (req, socket) => {
    const key = req.headers['sec-websocket-key']
    const accept = crypto.createHash('sha1').update(key + GUID).digest('base64')
    socket.write(
      'HTTP/1.1 101 Switching Protocols\r\n' +
      'Upgrade: websocket\r\nConnection: Upgrade\r\n' +
      'Sec-WebSocket-Accept: ' + accept + '\r\n\r\n')
    // The token an exclusive provider spends on attach rides the url, so a route is the PATH
    // and never the whole of it.
    const path = req.url.split('?')[0]
    if (path === '/abrupt') { setTimeout(() => socket.destroy(), 30); return }
    // A provider that says things on the TEXT channel we have no meaning for: a control type
    // from a later version, and something that is not JSON at all — which is what reaching
    // for a framework's `send_text` to emit device output looks like from here.
    if (path === '/talkative') {
      socket.write(frame(0x1, JSON.stringify({ type: 'from-a-later-version' })))
      socket.write(frame(0x1, 'device output on the wrong channel'))
    }
    // Only the first of those: a conforming provider written against a later spec.
    if (path === '/later-version') {
      socket.write(frame(0x1, JSON.stringify({ type: 'from-a-later-version' })))
    }
    let buffer = Buffer.alloc(0)
    socket.on('data', (chunk) => {
      buffer = Buffer.concat([buffer, chunk])
      // One frame at a time, unfragmented, masked (every client frame is). Enough of RFC
      // 6455 to be a peer; deliberately not a library.
      for (;;) {
        if (buffer.length < 2) return
        const opcode = buffer[0] & 0x0f
        const masked = (buffer[1] & 0x80) !== 0
        let length = buffer[1] & 0x7f
        let offset = 2
        if (length === 126) { if (buffer.length < 4) return; length = buffer.readUInt16BE(2); offset = 4 }
        const maskKey = masked ? buffer.subarray(offset, offset + 4) : null
        if (masked) offset += 4
        if (buffer.length < offset + length) return
        const payload = Buffer.from(buffer.subarray(offset, offset + length))
        if (maskKey) for (let i = 0; i < payload.length; i++) payload[i] ^= maskKey[i % 4]
        buffer = buffer.subarray(offset + length)
        if (opcode === 0x8) { socket.end(); return }
        if (opcode === 0x1) {
          let control = null
          try { control = JSON.parse(payload.toString('utf8')) } catch (e) { control = null }
          if (control && control.type === 'resize') {
            socket.write(frame(0x2, 'sized ' + control.cols + 'x' + control.rows + '\n'))
          } else if (control && control.type === 'kill') {
            socket.write(frame(0x1, JSON.stringify({ type: 'exited', code: 7 })))
            socket.end()
            return
          }
        } else if (opcode === 0x2) {
          socket.write(frame(0x2, 'echo:' + payload.toString('utf8')))
        }
      }
    })
    socket.on('error', () => {})
  })
  await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve))
  return { port: server.address().port, stop: () => new Promise((r) => server.close(() => r())) }
})
