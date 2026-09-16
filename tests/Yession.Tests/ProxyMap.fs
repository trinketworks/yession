module Yession.Tests.ProxyMap

// The proxy example (`examples/proxy`), driven end to end: a real Manager under
// `--auth trusted-headers`, a real session process, and the map process following the
// registry stream into a file the way a deployment's reverse proxy reads it.
//
// The example is standalone by rule — it reads the stream and the two documented
// placeholders, nothing of this repository's code — so this is the only place the two halves
// of that contract are checked against each other: what the Manager publishes, and what the
// map makes of it. Its own README can say what it does; only a run can say it still does.
//
// `Ports` and `Native`: a session's port is what gets rendered, and only a real session
// process has one.

open System
open Fable.Core
open Fable.NodeExtras
open Fable.Pyxpecto
open Yession.Oidc
open Yession.Host
open Yession.Tests.Support

[<ImportAll("node:fs")>]
let private nodeFs : obj = jsNative

[<Emit("$0.existsSync($1)")>]
let private existsSync (fs: obj) (path: string) : bool = jsNative

[<Emit("$0.readFileSync($1, 'utf8')")>]
let private readFileSync (fs: obj) (path: string) : string = jsNative

[<Emit("process.execPath")>]
let private nodePath : string = jsNative

/// `main.mjs` under this Node, started the way a deployment starts it: the environment of
/// this run, verbatim, and both output streams read as text — the example writes its own
/// lines to one and whatever Node has to say goes to the other, and a failure can be either.
let private spawnMap (args: string []) : Node.ChildProcess.ChildProcess =
    let child =
        spawnWithEnv
            nodePath
            ("examples/proxy/main.mjs" :: List.ofArray args)
            Node.Api.``process``.env
            None
            Stdio.Pipe
            false

    // `setEncoding` rather than converting each chunk: it puts a decoder in front of the
    // stream, so a multi-byte character split across two reads still arrives whole.
    child.stdout.setEncoding Node.Buffer.BufferEncoding.Utf8
    child.stderr.setEncoding Node.Buffer.BufferEncoding.Utf8
    child

/// Spawn `main.mjs` with the deployment's arguments and wait for it to announce which stream
/// it follows — so a process that dies on its arguments fails here, with its own words,
/// rather than as a wait on a file that never appears.
///
/// What comes back is the running process: `Said` is everything it has printed, for the
/// failure report, and `Stop` is the SIGTERM and the wait for it to be over.
let private startMap
    (args: string [])
    (timeoutMs: int)
    : Async<{| Said : unit -> string; Stop : unit -> Async<unit> |}> =
    Async.FromContinuations (fun (cont, econt, _) ->
        let child = spawnMap args
        let said = Text.StringBuilder ()

        let stop () =
            Async.FromContinuations (fun (stopped, _, _) ->
                let mutable over = false

                let finish () =
                    if not over then
                        over <- true
                        stopped ()

                child.on ("exit", fun (_: obj) -> finish ()) |> ignore
                try child.kill "SIGTERM" with _ -> finish ())

        let handle = {| Said = (fun () -> said.ToString ()); Stop = stop |}

        let mutable settled = false

        // The announcement, the child's death and the deadline are three ways one wait ends,
        // and whichever gets there first is the answer.
        let timer =
            JS.setTimeout
                (fun () ->
                    if not settled then
                        settled <- true
                        econt (exn (sprintf "sessions-map never announced itself; said:\n%s" (said.ToString ()))))
                timeoutMs

        let announced () =
            if not settled then
                settled <- true
                JS.clearTimeout timer
                cont handle

        let died (code: int option) =
            if not settled then
                settled <- true
                JS.clearTimeout timer

                econt (
                    exn (
                        sprintf
                            "sessions-map exited with %s:\n%s"
                            (match code with
                             | Some code -> string code
                             | None -> "null")
                            (said.ToString ())))

        let watch (stream: Node.Stream.Readable<string>) =
            stream.on (
                "data",
                fun (chunk: string) ->
                    said.Append chunk |> ignore
                    if (said.ToString ()).Contains " follows " then announced ())
            |> ignore

        watch child.stdout
        watch child.stderr
        child.on ("exit", died) |> ignore)

/// Run `main.mjs` to completion — for the arguments it refuses. A process ended by a signal
/// has no exit code; -1 stands in for it, and no argument this asks about answers with one.
let private runMap (args: string []) : Async<{| Code : int; Stderr : string |}> =
    Async.FromContinuations (fun (cont, _, _) ->
        let child = spawnMap args
        let stderr = Text.StringBuilder ()
        child.stderr.on ("data", fun (chunk: string) -> stderr.Append chunk |> ignore) |> ignore

        child.on ("exit", fun (code: int option) ->
            cont {| Code = Option.defaultValue -1 code; Stderr = stderr.ToString () |})
        |> ignore)

let private dataDirFor (label: string) =
    sprintf "tests/Yession.Tests/out/.data/proxy-map-%s-%d" label (int (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds ()) % 1000000)

let tests =
    testList "The proxy example: the registry, rendered into a proxy's config (examples/proxy)" [
        testCaseAsync "a launched session is rendered through the template, and leaves the map when it stops" <|
            async {
                let dataDir = dataDirFor "follow"
                // trusted-headers, so this also proves the subscribe asserts a subject: under
                // that rule a header-less stream is a 401 with no frames, and the map would
                // never fill.
                let! pm =
                    ProcessManager.createWithUi
                        { ProcessManager.Options.defaults dataDir nodePath [ "app/SessionMain.js" ] with
                            Strategy = Some Strategy.trustedHeaders }
                        (Some ManagerUi.tryHandle)
                let manager = sprintf "http://127.0.0.1:%d" pm.EndpointPort.Value
                let out = dataDir + "/sessions.map"
                let! map =
                    startMap
                        [| "--manager"; manager; "--as"; "proxy-map"; "--template"; "{id} -> 127.0.0.1:{port}"; "--out"; out |]
                        10000
                // `Async.Catch` rather than `try/finally`: on Node nothing may block the loop
                // to await a cleanup, and a failed assertion still has to take the child down.
                let! outcome =
                    async {
                        let record = pm.CreateSession "mapped" "Mapped" |> expect
                        let! launched = pm.Launch record.SessionId
                        let port = expect launched
                        let expected = sprintf "mapped -> 127.0.0.1:%d" port
                        do! waitUntil "the map to carry the launched session" (fun () ->
                                existsSync nodeFs out && (readFileSync nodeFs out).Contains expected)
                        Expect.equal (readFileSync nodeFs out) expected "one running session is one rendering, and nothing else"
                        let! stopped = pm.Stop record.SessionId
                        expect stopped
                        do! waitUntil "the map to empty" (fun () -> readFileSync nodeFs out = "")
                    }
                    |> Async.Catch
                do! map.Stop ()
                do! pm.StopAll ()
                match outcome with
                | Choice1Of2 () -> ()
                | Choice2Of2 error -> failwithf "%s\n--- sessions-map said ---\n%s" error.Message (map.Said ())
            }

        testCaseAsync "a template naming neither placeholder is refused before anything is written" <|
            async {
                let out = dataDirFor "refused" + "/never.map"
                let! result = runMap [| "--template"; "static"; "--out"; out |]
                Expect.equal result.Code 64 "EX_USAGE, the way the bins refuse an argument"
                Expect.isTrue (result.Stderr.Contains "neither {id} nor {port}") "it names the rule that refused it"
                Expect.isFalse (existsSync nodeFs out) "refused means nothing was written"
            }
    ]
