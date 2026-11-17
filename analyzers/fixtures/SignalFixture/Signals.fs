module SignalFixture.Signals

open Fable.Core
open Node.ChildProcess

/// Each call of Node's own `pid` and `kill`, and each call that the rule leaves alone. The rule
/// MUST report a line that ends with `// YES021`, and MUST NOT report a different line. `lint`
/// reads those markers and compares them to what the analyzer said, in both directions.
///
/// The marker is where the rule reports: on the line that the call starts on.

// The pid of a child, read from Node's own member. What the caller does with the number
// changes nothing.

let pidOf (child: ChildProcess) : int = int child.pid // YES021

let hasPid (child: ChildProcess) : bool = not (isNull (box child.pid)) // YES021

let named (child: ChildProcess) : string = sprintf "child %d" (int child.pid) // YES021

// Node's own `kill` of a child, with the signal that Node chooses and with a named signal.
// Then in a function that a caller keeps, for each child of a list, and in an `async` block.

let stop (child: ChildProcess) : unit = child.kill () // YES021

let stopAtOnce (child: ChildProcess) : unit = child.kill "SIGKILL" // YES021

let stopper (child: ChildProcess) : string -> unit = fun name -> child.kill name // YES021

let stopEach (children: ChildProcess list) : unit =
    children |> List.iter (fun child -> child.kill "SIGTERM") // YES021

let stopAll (children: ChildProcess list) : unit = children |> List.iter _.kill() // YES021

let later (child: ChildProcess) : Async<unit> =
    async {
        child.kill "SIGKILL" // YES021
    }

// A child that Node's own `spawn` answers has the same type, whatever its name is.

let started () : unit =
    let listener = Node.Api.childProcess.spawn "nc"
    listener.kill () // YES021

// A type that inherits the type of a child has Node's own members.

type Worker =
    inherit ChildProcess
    abstract busy: bool

let stopWorker (worker: Worker) : unit = worker.kill () // YES021

// A call can continue on the next lines. The rule reports it on the line that it starts on.

let spread (child: ChildProcess) : unit =
    child.kill ( // YES021
        "SIGTERM"
    )

// Node's own `process.kill`, with the signal that Node chooses and with a named signal.

let signalTo (pid: int) : unit = Node.Api.``process``.kill (float pid) // YES021

let endOf (pid: int) : unit = Node.Api.``process``.kill (float pid, U2.Case1 "SIGKILL") // YES021

// What the rule has no opinion about. This process has a pid.

let own () : int = int Node.Api.``process``.pid

// A different member of a child, and a different member of this process.

let release (child: ChildProcess) : unit = child.unref ()

let attached (child: ChildProcess) : bool = child.connected

let here () : string = Node.Api.``process``.cwd ()

// A type declared here that has a `pid` and a `kill` is not Node's. A function named `kill` is
// not Node's.

type Handle =
    abstract pid: float
    abstract kill: ?signal: string -> unit

let handlePid (handle: Handle) : int = int handle.pid

let handleStop (handle: Handle) : unit = handle.kill "SIGKILL"

module Own =
    let kill (pid: int) (signal: string) : string = sprintf "%s to %d" signal pid

    let sent () : string = kill 2 "SIGURG"

// A call that is deliberate says so on its line. The rule reports it, and the CLI drops the
// finding.

let justified (child: ChildProcess) : unit =
    child.kill "SIGURG" // fsharpanalyzer: ignore-line YES021
