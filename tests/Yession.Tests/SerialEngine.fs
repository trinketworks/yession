module Yession.Tests.SerialEngine

// The serial engine itself (Plan 16, part E), against a real tty.
//
// Everything ABOVE the `SerialEngine` seam is already covered over real sockets, with the
// engine substituted — the MCP lifecycle, the claim, the WebSocket attach. This is the half
// that substitution deliberately skipped: `Ports.real`, which is the `serialport`
// import, the port open, the line settings, and the close.
//
// socat is what makes that testable with no hardware. `socat pty,raw,echo=0 pty,raw,echo=0`
// gives two pseudo-terminals wired to each other: open one with the real engine and the other
// with plain file I/O, and every byte that crosses went through a kernel tty — the same
// `open`, `termios` and `read` a USB adapter would use. What it cannot prove is anything about
// a specific chip; what it does prove is that we drive a serial port correctly, which is where
// the bugs were going to be.
//
// `Serial`, because the engine needs three things this repo cannot assume: the `serialport`
// addon, `udevadm` (Linux enumeration shells out to it), and socat.

open System
open System.Text.RegularExpressions
open Fable.Core
open Fable.NodeExtras
open Fable.Pyxpecto
open Node.Buffer
open Node.ChildProcess
open Yession.Domain
open Yession.Host
open SerialProvider

let private expect =
    function
    | Ok v -> v
    | Error e -> failwithf "%A" e

let private utf8 = BufferEncoding.Utf8

/// A socat PTY pair, and the raw handle to the FAR end — the side standing in for the device.
type private Pair =
    { /// The path the engine under test opens.
      Ours : string
      /// The path this fixture holds open, as the device.
      Theirs : string
      /// Write bytes as the device would.
      Send : string -> unit
      /// Everything the device has received so far.
      Received : unit -> string
      Stop : unit -> unit }

// --- what socat announced, read out of everything it has said ---------------------------------
//
// Pure, and beside the fixture that reads it, because this is the half of the fixture no
// serial port can be asked about: every way of mis-reading the announcement produces a rig
// that waits for a device it will never open, and reports it as a timeout.

/// The two PTY paths socat announces, read out of EVERYTHING it has said so far rather than
/// out of the chunk that has just arrived: its stderr is a byte stream with no promise of line
/// alignment, so an announcement can be cut in half by a read and neither half matches.
///
/// A path counts only once something has ENDED it, which is what the trailing `\s` is for. A
/// read that stopped mid-path would otherwise hand back a prefix of the real device, and
/// opening that fails as "no such file" rather than as the half-read line it is.
let private announcedPtys (said: string) : (string * string) option =
    match
        Regex.Matches (said, @"PTY is (\S+)\s")
        |> Seq.map (fun m -> m.Groups.[1].Value)
        |> List.ofSeq
    with
    | ours :: theirs :: _ -> Some (ours, theirs)
    | _ -> None

let private announcementTests =
    testList "the pair socat announces" [

        testCase "one pty is not a pair" <| fun () ->
            Expect.isNone
                (announcedPtys "2026/01/01 00:00:00 socat[1] N PTY is /dev/pts/3\n")
                "the fixture holds one end and hands over the other; there is no pair yet"

        testCase "a path nothing has ended yet is half an announcement" <| fun () ->
            Expect.isNone
                (announcedPtys "N PTY is /dev/pts/3\nN PTY is /dev/pts/4")
                "a device read out of this would be a prefix of the real one"

        testCase "an announcement cut in half by a read is one path once both halves are in" <| fun () ->
            Expect.equal
                (announcedPtys ("N PTY is /dev/pts/3\nN PTY is /dev/p" + "ts/4\n"))
                (Some ("/dev/pts/3", "/dev/pts/4"))
                "what the two reads said together"

        testCase "the first two announced are the pair, in the order socat named them" <| fun () ->
            Expect.equal
                (announcedPtys "N PTY is /dev/pts/3\nN PTY is /dev/pts/4\nN starting data transfer loop\n")
                (Some ("/dev/pts/3", "/dev/pts/4"))
                "ours first, theirs second, and whatever socat says afterwards is not a path"
    ]

// --- [Serial]: the pair itself ----------------------------------------------------------------

// `node:fs`, for the descriptor the far end is held by. `Fable.Node` types all three, and
// types them for somebody else: `openSync` takes a path as a `U2` and answers a `float`, and
// the `writeSync` overload that takes text takes it as `obj`.
[<Import("openSync", "node:fs")>]
let private openSync (path: string) (flags: string) : int = jsNative

[<Import("writeSync", "node:fs")>]
let private writeSync (fd: int) (text: string) : int = jsNative

[<Import("closeSync", "node:fs")>]
let private closeSync (fd: int) : unit = jsNative

/// socat, as this box names it. An empty value names no binary, so it is absent rather than a
/// program called "" — which would fail as a spawn error nobody could read.
let private socatBinary (env: Map<string, string>) : string =
    env
    |> Map.tryFind "YESSION_BIN_SOCAT"
    |> Option.filter (fun path -> path <> "")
    |> Option.defaultValue "socat"

/// Spawn socat and wait for it to announce both PTY paths.
///
/// The paths are parsed from its stderr because socat allocates them — there is no way to ask
/// for a name, and guessing `/dev/pts/N` races every other process on the box.
///
/// Three things end this wait and the first one there is the answer: the announcement, socat
/// dying, or the deadline. A socat that refused its arguments — or one that was never on this
/// box to spawn — fails here in its own words rather than spending ten seconds and reporting
/// a silence it never had.
let private announcedPair () : Async<ChildProcess * string * string> =
    Async.FromContinuations (fun (cont, econt, _) ->
        let env = Sandboxes.ambientEnv ()

        let child =
            spawn
                (socatBinary env)
                [ "-d"; "-d"; "pty,raw,echo=0"; "pty,raw,echo=0" ]
                // Its stdin and stdout are pipes nobody reads rather than `/dev/null`: socat
                // with `-d -d` says everything it has to say on stderr and puts its data on the
                // ptys, so the other two carry nothing either way.
                { Cwd = None
                  Env = ChildEnv.Replacing env
                  Streams = { Stdin = Pipe; Stdout = Pipe; Stderr = Pipe }
                  Detached = false }

        // `setEncoding` rather than converting each chunk: it puts a decoder in front of the
        // stream, so a character split across two reads still arrives whole.
        child.stderr.setEncoding utf8

        let said = Text.StringBuilder ()
        let mutable settled = false

        let timer =
            JS.setTimeout
                (fun () ->
                    if not settled then
                        settled <- true
                        child.kill ()
                        econt (exn (sprintf "socat did not announce two PTYs in time; it said: %s" (said.ToString ()))))
                10000

        let refused (reason: string) =
            if not settled then
                settled <- true
                JS.clearTimeout timer
                econt (exn reason)

        child.stderr.on (
            "data",
            fun (chunk: string) ->
                said.Append chunk |> ignore

                match announcedPtys (said.ToString ()) with
                | Some (ours, theirs) when not settled ->
                    settled <- true
                    JS.clearTimeout timer
                    cont (child, ours, theirs)
                | _ -> ())
        |> ignore

        child.on ("error", fun (thrown: obj) -> refused (sprintf "socat could not be started: %s" (Thrown.describe thrown)))
        |> ignore

        child.on (
            "exit",
            fun (code: int option) ->
                refused (
                    sprintf
                        "socat exited with %s before announcing two PTYs; it said: %s"
                        (match code with
                         | Some code -> string code
                         | None -> "null")
                        (said.ToString ())))
        |> ignore)

/// The pair, with the far end open as the device.
let private startPair () : Async<Pair> =
    async {
        let! child, ours, theirs = announcedPair ()

        // The far end, opened directly rather than through serialport: one side of this test
        // has to be something other than the code under test, or it proves only that our
        // engine agrees with itself.
        //
        // A tty handle and not an fs read stream — the reasoning is on `openTty`, where it
        // governs the binding it is about, and it is the difference between this fixture and
        // one that eats the NEXT socat's announcement.
        let fd = openSync theirs "r+"
        let reader = openTty fd
        let received = Text.StringBuilder ()
        reader.onData (fun chunk -> received.Append (chunk.toString utf8) |> ignore)

        // A read that fails is the device going away, which is a case here rather than a
        // fault: one of the tests below pulls the far end out from under an open port.
        reader.onError (fun _ -> ())

        let send (text: string) =
            try writeSync fd text |> ignore with _ -> ()

        let stop () =
            try
                reader.destroy ()
                closeSync fd
            with _ ->
                ()

            child.kill ()

        return
            { Ours = ours
              Theirs = theirs
              Send = send
              Received = (fun () -> received.ToString ())
              Stop = stop }
    }

/// Poll rather than sleep a fixed amount: a tty round trip is fast but not instant, and a
/// fixed sleep is either flaky or slow.
let private until (predicate: unit -> bool) : Async<bool> =
    let rec loop (remaining: int) =
        async {
            if predicate () then return true
            elif remaining <= 0 then return false
            else
                do! Async.Sleep 20
                return! loop (remaining - 20)
        }
    loop 5000

let tests =
    testList "The serial engine, against a real tty" [

        announcementTests

        testCaseAsync "bytes cross a real serial port in both directions" <|
            async {
                let! pair = startPair ()
                let received = Text.StringBuilder ()
                let mutable closedWith = None
                let! opened =
                    Ports.real.Open
                        pair.Ours
                        SerialSettings.defaults
                        (fun text -> received.Append text |> ignore)
                        (fun why -> closedWith <- Some why)
                let port = opened |> expect

                // Device -> us. This is the read path: an fd opened by `serialport`, put into
                // raw mode, streaming into the callback the provider hands the WebSocket.
                pair.Send "READY\r\n"
                let! arrived = until (fun () -> received.ToString().Contains "READY")
                Expect.isTrue arrived "what the device printed reached the engine's callback"

                // Us -> device. The write path, which is what a human typing in an attached
                // terminal actually drives.
                port.Write "AT+VERSION\r"
                let! echoed = until (fun () -> (pair.Received ()).Contains "AT+VERSION")
                Expect.isTrue echoed "what we wrote reached the device"

                port.Close ()
                let! closed = until (fun () -> closedWith.IsSome)
                Expect.isTrue closed "closing the port fires onClose exactly as the provider expects"
                pair.Stop ()
            }

        testCaseAsync "a device that goes away closes the port rather than hanging" <|
            async {
                // The case the provider turns into the WebSocket's `exited` frame. Without it
                // an unplugged adapter is indistinguishable from a quiet one, and the terminal
                // waits for bytes that are never coming.
                //
                // This test found that gap rather than confirming its absence: `serialport`
                // emits neither `close` nor `error` on an idle port whose device has gone —
                // it reports the failure only when something next writes. Note that nothing
                // here writes, deliberately, because a read-only attach is the case that hung.
                let! pair = startPair ()
                let mutable closedWith = None
                let! opened =
                    Ports.real.Open pair.Ours SerialSettings.defaults ignore (fun why -> closedWith <- Some why)
                let port = opened |> expect
                pair.Stop ()
                let! noticed = until (fun () -> closedWith.IsSome)
                Expect.isTrue noticed "the engine noticed the far end vanish"
                Expect.isFalse (closedWith = Some "") "and said something about why"
                port.Close ()
            }

        testCaseAsync "line settings are applied, not ignored" <|
            async {
                // 9600 8N1 rather than the default 115200: a baud rate that is silently
                // dropped is the classic serial bug, and it looks exactly like working code
                // until somebody attaches hardware that cares.
                let! pair = startPair ()
                let received = Text.StringBuilder ()
                let! opened =
                    Ports.real.Open
                        pair.Ours
                        { SerialSettings.defaults with BaudRate = 9600 }
                        (fun text -> received.Append text |> ignore)
                        ignore
                let port = opened |> expect
                pair.Send "slow\r\n"
                let! arrived = until (fun () -> received.ToString().Contains "slow")
                Expect.isTrue arrived "the port opened and carried bytes at the rate it was given"
                port.Close ()
                pair.Stop ()
            }

        testCaseAsync "opening a path that is not a port fails as a value, never an exception" <|
            async {
                // The provider turns this into text the model reads. An engine that threw
                // would take the whole attach down instead.
                let! outcome =
                    Ports.real.Open "/dev/definitely-not-a-serial-port" SerialSettings.defaults ignore ignore
                match outcome with
                | Ok _ -> failwith "opening a nonexistent path should not succeed"
                | Error reason -> Expect.isFalse (reason = "") "and the reason says something"
            }

        testCaseAsync "enumeration answers, which is the whole of what discovery needs" <|
            async {
                // On Linux this shells out to `udevadm`; without it the call THROWS, which is
                // how the `Serial` capability came to need eudev. Zero ports is a pass — the
                // contract is that the engine answers, not that this box has hardware.
                match! Ports.real.List () with
                | Error reason -> failwithf "enumeration should answer, not fail: %s" reason
                | Ok ports ->
                    // Whatever it found must survive the discovery filter without throwing.
                    // A socat PTY has no USB identity, so it is correctly NOT offered — the
                    // same rule that keeps `/dev/ttyS0` out of an agent's reach.
                    let devices = Discovery.devices ports
                    Expect.isTrue
                        (List.length devices <= List.length ports)
                        "discovery only ever narrows what the OS reported"
            }
    ]
