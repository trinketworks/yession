// The upgrade handler for the provider's WebSocket server: framing, masking and the
// handshake, in the one place they can be read against RFC 6455.
//
// A module rather than an `[<Emit>]` string because frame arithmetic is code somebody has to
// check line by line, and a string is neither formatted nor linted nor scoped. `Ws.fs` binds
// it with a typed import, which is the only interop-shaped thing left.

export default function accept (httpServer, dispatch, crypto) {
  // Imported rather than `require`d: this file is ESM both bundled and unbundled, and the
  // bundle's createRequire banner does not exist in a plain Fable run.
  const GUID = '258EAFA5-E914-47DA-95CA-C5AB0DC85B11'
  const frame = (opcode, payload) => {
    const body = Buffer.from(payload, 'utf8')
    let head
    if (body.length < 126) head = Buffer.from([0x80 | opcode, body.length])
    else if (body.length < 65536) {
      head = Buffer.alloc(4); head[0] = 0x80 | opcode; head[1] = 126; head.writeUInt16BE(body.length, 2)
    } else {
      head = Buffer.alloc(10); head[0] = 0x80 | opcode; head[1] = 127
      head.writeUInt32BE(0, 2); head.writeUInt32BE(body.length, 6)
    }
    return Buffer.concat([head, body])
  }
  httpServer.on('upgrade', (req, socket) => {
    socket.on('error', () => {})
    const path = new URL(req.url, 'http://local').pathname
    const handlers = dispatch(path)
    if (!handlers) {
      socket.write('HTTP/1.1 404 Not Found\r\nConnection: close\r\n\r\n')
      socket.destroy()
      return
    }
    const key = req.headers['sec-websocket-key']
    const accept = crypto.createHash('sha1').update(key + GUID).digest('base64')
    socket.write(
      'HTTP/1.1 101 Switching Protocols\r\n' +
      'Upgrade: websocket\r\nConnection: Upgrade\r\n' +
      'Sec-WebSocket-Accept: ' + accept + '\r\n\r\n')

    let gone = false
    const peer = {
      Send: (text) => { if (!gone) socket.write(frame(0x2, text)) },
      Control: (text) => { if (!gone) socket.write(frame(0x1, text)) },
      Close: () => { if (!gone) { gone = true; try { socket.write(frame(0x8, '')) } catch (e) {} ; socket.end() } }
    }
    const bound = handlers(peer)
    const finish = () => { if (!gone) { gone = true; bound.closed() } }

    let buffer = Buffer.alloc(0)
    socket.on('data', (chunk) => {
      buffer = Buffer.concat([buffer, chunk])
      for (;;) {
        if (buffer.length < 2) return
        const opcode = buffer[0] & 0x0f
        const masked = (buffer[1] & 0x80) !== 0
        let length = buffer[1] & 0x7f
        let offset = 2
        if (length === 126) { if (buffer.length < 4) return; length = buffer.readUInt16BE(2); offset = 4 }
        else if (length === 127) {
          if (buffer.length < 10) return
          // The high word is refused rather than truncated: a 4GB frame is not a device.
          if (buffer.readUInt32BE(2) !== 0) { socket.destroy(); return }
          length = buffer.readUInt32BE(6); offset = 10
        }
        const maskKey = masked ? buffer.subarray(offset, offset + 4) : null
        if (masked) offset += 4
        if (buffer.length < offset + length) return
        const payload = Buffer.from(buffer.subarray(offset, offset + length))
        if (maskKey) for (let i = 0; i < payload.length; i++) payload[i] ^= maskKey[i % 4]
        buffer = buffer.subarray(offset + length)
        if (opcode === 0x8) { finish(); socket.end(); return }
        else if (opcode === 0x9) { socket.write(frame(0xa, payload.toString('utf8'))) }
        else if (opcode === 0x1) { bound.control(payload.toString('utf8')) }
        else if (opcode === 0x2) { bound.data(payload.toString('utf8')) }
      }
    })
    socket.on('close', finish)
  })
}
