module SerialProvider.Ports

// The engine behind the serial provider (Plan 16, part E): enumerate the host's ports, and
// open one as a byte stream.
//
// A SEAM rather than a direct dependency, for the reason every other backend in this repo is
// one. `serialport` is a native addon, and the provider must be testable — and shippable —
// where it is absent:
//
//   * it is a LAZY dynamic import, so a process that never opens a port never loads the
//     addon, and a box without it degrades to "no devices" rather than failing to start.
//     That is Plan 16's own wording and it is what makes the provider safe to run anywhere.
//   * the tests drive the provider through a fake engine, so the MCP lifecycle and the
//     WebSocket attach are covered on a box with no hardware at all — which is every CI box
//     we have.
//
// Cross-platform without a branch here: `serialport` is the portability layer (Linux, macOS,
// Windows), and `@serialport/bindings-cpp` ships prebuilt addons for each in its own tarball,
// so an ordinary `npm install` of the published package gets a working engine everywhere.

open Fable.Core
open SerialProvider

/// An open port. Deliberately the same shape as everything else that streams bytes in this
/// repo — write, close, and a callback for what arrives — so the provider's WebSocket side
/// has nothing to adapt.
type OpenPort =
    { Write : string -> unit
      Close : unit -> unit }

/// What the provider needs from the world. Two functions, both failable, neither knowing
/// what MCP is.
type SerialEngine =
    { /// Every port the OS can see, interesting or not — `Discovery.devices` is what decides
      /// which are offered, and it does that in the Domain where it is testable.
      List : unit -> Async<Result<SerialPortInfo list, string>>
      /// Open one, with the given settings. `onData` receives decoded text as it arrives;
      /// `onClose` fires once, when the port goes away for any reason (unplugged, closed by
      /// us, an error) — the provider turns that into the WebSocket's termination frame.
      Open :
        string
            -> SerialSettings
            -> (string -> unit)
            -> (string -> unit)
            -> Async<Result<OpenPort, string>> }

module SerialEngine =

    /// A host with no engine: no devices, and opening anything says why. What a build
    /// without the addon degrades to, and what the tests replace.
    let none : SerialEngine =
        { List = fun () -> async { return Ok [] }
          Open = fun path _ _ _ -> async { return Error (sprintf "no serial engine to open %s with" path) } }

// --- `serialport`, bound ------------------------------------------------------------------
//
// The one npm package this provider uses, declared here rather than in `Interop.fs` because it
// is a different axis: that file is the HOST, and this is the device library. Its only reader
// is the engine directly below, which is the other half of the reason — a binding whose sole
// caller sits under it is read as one thing, and nothing above has to be given a way to reach
// it.
//
// Everything built ON the binding is F#. What stays JavaScript is what F# cannot say: a
// dynamic import, and `new` over a class held as a value.

/// One port as `serialport` enumerated it. Only the path is always there — a port that is not
/// on a USB bus has no vendor or product, and plenty of adapters ship with no serial number.
type private RawPort =
    abstract path : string
    abstract vendorId : string option
    abstract productId : string option
    abstract serialNumber : string option
    abstract manufacturer : string option

/// An open port. It is a Node stream, so the three events this provider listens for carry a
/// `Buffer`, nothing, and an `Error` respectively — hence the one `on` over `obj`.
type private RawHandle =
    abstract on : string * (obj -> unit) -> RawHandle
    abstract write : string -> bool
    abstract close : (obj -> unit) -> unit

/// The `SerialPort` class itself: `list` is static on it, and the constructor is reached
/// through `newPort` below, because F# has no `new` over a class held as a value.
type private SerialPortClass =
    abstract list : unit -> JS.Promise<RawPort array>

/// The module, as much of it as this provider wants.
type private SerialPortModule =
    abstract SerialPort : SerialPortClass

/// `import('serialport')` — the DYNAMIC form, and the whole of the lazy import this module is
/// built around. A static import is resolved when this file is loaded, so a box without the
/// addon would fail before `main` ran; this one fails where the two callers below can answer
/// for it, and a process that never touches a port never loads it at all.
[<Emit("import('serialport')")>]
let private importSerialport () : JS.Promise<SerialPortModule> = jsNative

/// `new SerialPort(options, opened)`: the callback form of `autoOpen`, so the handle is only
/// handed over once there is really an fd behind it — `opened` is called with an error, or
/// with nothing at all once the port is open.
[<Emit("new $0($1, $2)")>]
let private newPort (cls: SerialPortClass) (options: obj) (opened: obj -> unit) : RawHandle = jsNative

/// What a caught value SAYS. JavaScript admits throwing anything, so an `Error`'s message is
/// the usual case rather than the only one, and a reason that reached a person as `undefined`
/// would be a reason nobody could act on.
let private reasonOf (error: exn) : string =
    if Interop.isTruthy (box error.Message) then error.Message else Interop.asText (box error)

/// How often the watchdog below looks: a `stat` twice a second, cheap enough to be invisible
/// and fast enough that an unplug reaches the person as an event rather than as a silence.
[<Literal>]
let private watchdogMs = 500

/// Open one port, and answer once the fd is real. `serialport` reports the open through a
/// callback rather than by throwing, so this is where that becomes a promise the workflow
/// below can await.
let private openHandle (cls: SerialPortClass) (path: string) (settings: SerialSettings) : JS.Promise<RawHandle> =
    JS.Constructors.Promise.Create (fun resolve reject ->
        // The callback closes over the handle the constructor is in the middle of returning.
        // Legal because it fires on a later tick, by which time the assignment has happened.
        let handle = ref Unchecked.defaultof<RawHandle>

        handle.Value <-
            newPort
                cls
                {| path = path
                   baudRate = settings.BaudRate
                   dataBits = settings.DataBits
                   stopBits = settings.StopBits
                   parity = settings.Parity
                   autoOpen = true |}
                (fun error -> if isNull error then resolve handle.Value else reject (unbox error)))

/// The real engine. Everything it does is lazy: this value costs nothing to build on a box
/// with no `serialport` installed, and the import happens on the first list or open.
///
/// `binary` is deliberately NOT offered: the stream is decoded as UTF-8 text because that is
/// what a terminal emulator downstream consumes, and a device speaking binary is a case this
/// provider does not claim to serve.
let real : SerialEngine =
    { List =
        fun () ->
            async {
                try
                    let! serialport = importSerialport () |> Interop.awaitPromise
                    let! ports = serialport.SerialPort.list () |> Interop.awaitPromise

                    return
                        Ok (
                            ports
                            |> Array.toList
                            |> List.map (fun raw ->
                                { Path = raw.path
                                  // A USB id is a PAIR or it is nothing: a port reported with
                                  // one half of it is not on a USB bus this provider can name,
                                  // and an id built out of the half that is missing would be a
                                  // device identity nobody could match.
                                  Usb =
                                    match raw.vendorId, raw.productId with
                                    | Some vendor, Some product -> UsbId.create vendor product
                                    | _ -> None
                                  SerialNumber = raw.serialNumber |> Option.filter (fun value -> value <> "")
                                  Manufacturer = raw.manufacturer |> Option.filter (fun value -> value <> "") }))
                with error ->
                    // Not an error the caller must handle — a host without the addon has no
                    // devices, which is a true and useful answer. The import throwing IS that
                    // host, and so is an addon that cannot enumerate on this platform.
                    eprintfn "serial: no engine (%s)" (reasonOf error)
                    return Ok []
            }
      Open =
        fun path settings onData onClose ->
            async {
                try
                    let! serialport = importSerialport () |> Interop.awaitPromise
                    let! handle = openHandle serialport.SerialPort path settings |> Interop.awaitPromise

                    // ONE close, whatever ended it: the handlers, the watchdog and a write
                    // that threw all arrive here, and only the first of them is reported. The
                    // provider turns `onClose` into the stream's termination frame, and a
                    // stream ends once.
                    let closed = ref false
                    let watchdog : Interop.Timer option ref = ref None

                    let finish (why: string) =
                        match watchdog.Value with
                        | Some timer ->
                            Interop.clearInterval timer
                            watchdog.Value <- None
                        | None -> ()

                        if not closed.Value then
                            closed.Value <- true
                            onClose why

                    handle.on ("data", fun chunk -> onData (Interop.bufferToString chunk)) |> ignore
                    handle.on ("close", fun _ -> finish "the port closed") |> ignore
                    handle.on ("error", fun error -> finish (reasonOf (unbox error))) |> ignore

                    // The watchdog is not belt-and-braces over the `close`/`error` handlers —
                    // it covers a case they demonstrably do not. An IDLE open port whose
                    // device has gone away emits NEITHER event: measured against a hung-up
                    // tty, `serialport` sat there with `isOpen === true` indefinitely, and
                    // only surfaced EIO when something tried to write. Nothing writes to a
                    // device the human is only reading from, so without this an unplugged
                    // adapter is indistinguishable from a quiet one and the attached terminal
                    // waits forever. What the handlers cover and this does not is the reverse:
                    // OUR close, and an I/O error on a device node that still exists.
                    //
                    // Vanishing is detected by the device node disappearing, because that is
                    // what unplugging actually does — `/dev/ttyUSB0` is removed, exactly as a
                    // pty slave is removed when its master hangs up. Armed only when the path
                    // exists as a file at open time, which keeps it off Windows, where `COM3`
                    // is not a filesystem entry and the check would report every port as
                    // instantly gone.
                    if Interop.existsSync path then
                        let timer =
                            Interop.setInterval watchdogMs (fun () ->
                                if not (Interop.existsSync path) then
                                    // Release the fd as well as reporting it: the device is
                                    // gone, so the handle is never becoming useful again. The
                                    // close is guarded because closing a dead device node can
                                    // itself throw, and a watchdog that threw would leave the
                                    // stream hanging on the very fault it exists to catch.
                                    (try handle.close ignore with _ -> ())
                                    finish "the device went away")

                        // Never a reason for the process to stay alive.
                        timer.unref () |> ignore
                        watchdog.Value <- Some timer

                    return
                        Ok
                            { Write =
                                fun text ->
                                    try
                                        handle.write text |> ignore
                                    with error ->
                                        finish (reasonOf error)
                              Close =
                                fun () ->
                                    try
                                        handle.close ignore
                                    with _ ->
                                        finish "closed" }
                with error ->
                    return Error (reasonOf error)
            } }
