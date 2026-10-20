module Yession.Tests.StandInMain

// The program a stand-in child runs (`StandIn.fs` says why there is one). Fable compiles it
// to its own module and nothing in the suite imports it, so its top level runs only in a
// child started on it. The .NET CLR compiles none of it: there the suite is one program,
// and this top level would run at its start.

#if FABLE_COMPILER
open Fable.Core
open Node.Api
open Fable.NodeExtras
open Yession.Manager
open Yession.Tests.StandIn

let private holds (condition: Condition) : bool =
    match condition with
    | Equal (name, value) -> ProcessEnv.get name = Some value
    | Set name -> (ProcessEnv.get name).IsSome
    | Unset name -> (ProcessEnv.get name).IsNone

let private isDevice (descriptor: int) : bool = (fs.fstatSync descriptor).isCharacterDevice ()

/// Keep this process alive until something stops it.
let private waitToBeStopped () : unit = JS.setInterval ignore 1000 |> ignore

let private session (ledger: string) (readiness: Readiness) : unit =
    TestFiles.append ledger (sprintf "%d\n" (int ``process``.pid))
    match readiness with
    | ReadyAfter milliseconds ->
        JS.setTimeout (fun () -> printfn "%s" (ReadyLine.encode { Port = 1; Version = None })) milliseconds |> ignore
        waitToBeStopped ()
    | ExitAfter (milliseconds, code) -> JS.setTimeout (fun () -> Processes.exitWith code) milliseconds |> ignore
    | NeverReady -> waitToBeStopped ()

let private fetch (url: string) : unit =
    Fetch.fetchUnsafe url []
    |> Promise.bind (fun response -> response.text ())
    |> Promise.map (fun body ->
        ``process``.stdout.write body |> ignore
        Processes.exitWith 0)
    |> Promise.catch (fun error ->
        // The error itself, not its message: Node prints its `cause`, which is where a fetch
        // says what refused it.
        JS.console.error error
        Processes.exitWith 9)
    |> ignore

let private perform (act: Act) : unit =
    match act with
    | Exit code -> Processes.exitWith code
    | Linger -> JS.setTimeout ignore 60_000 |> ignore
    | ExitIf (conditions, matched, otherwise) ->
        Processes.exitWith (if List.forall holds conditions then matched else otherwise)
    | ExitIfStartedIn (directory, matched, otherwise) ->
        let here = TestFiles.canonical (``process``.cwd ()) = TestFiles.canonical directory
        Processes.exitWith (if here then matched else otherwise)
    | ExitIfDevices (standard, matched, otherwise) ->
        Processes.exitWith (if (isDevice 0, isDevice 1, isDevice 2) = standard then matched else otherwise)
    | PrintVariable name -> printfn "%s=%s" name (ProcessEnv.get name |> Option.defaultValue "absent")
    | Fetch url -> fetch url
    | Connect path -> net.connect (path, fun (_: Node.Net.Socket) -> Processes.exitWith 0) |> ignore
    | Session (ledger, readiness) -> session ledger readiness

match Act.parse (``process``.argv |> Seq.skip 2 |> List.ofSeq) with
| Ok act -> perform act
| Error message ->
    JS.console.error message
    Processes.exitWith 2
#endif
