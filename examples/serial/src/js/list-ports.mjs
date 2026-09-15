// `serialport`'s `SerialPort.list()`, behind the lazy dynamic import that lets a host
// without the addon answer "no devices" instead of failing to start.
//
// A module rather than an `[<Emit>]` string so the import, the mapping and the failure
// branch are readable JavaScript; `Ports.fs` binds it with a typed import.

export default async function listPorts () {
  try {
    const { SerialPort } = await import('serialport')
    const ports = await SerialPort.list()
    return { ok: true, reason: '', ports: ports.map(p => ({
      path: p.path || '',
      vendorId: p.vendorId || '',
      productId: p.productId || '',
      serialNumber: p.serialNumber || '',
      manufacturer: p.manufacturer || ''
    })) }
  } catch (err) {
    return { ok: false, reason: String((err && err.message) || err), ports: [] }
  }
}
