module Yession.Tests.Jumpstarter

// The jumpstarter provider (Plan 18), end to end — and the one thing its own pytest suite
// structurally cannot prove.
//
// That suite drives the provider with a client written beside it, in the same language, out
// of the same head. This one drives it with OUR client: `McpClient`, the code a session
// actually uses, over real HTTP against a real provider process talking to a real exporter.
// Two implementations that were never checked against each other agreeing is the only
// evidence that either read the protocol right — the same argument the serial suite makes,
// and worth making twice because this provider is somebody else's SDK behind our contract.
//
// `Jumpstarter`, because it needs uv, a resolvable Python environment, and two processes:
// see `jumpstarterAvailable` in tasks.fsx, which probes it by RUNNING it.

open System
open System.Text.RegularExpressions
open Fable.Core
open Fable.NodeExtras
open Fable.Pyxpecto
open Node.Buffer
open Node.ChildProcess
open Yession.Domain
open Yession.Domain.Sandboxes
open Yession.Domain.Tools
open Yession.Host

let private utf8 = BufferEncoding.Utf8

[<Literal>]
let private project = "examples/jumpstarter"

/// The exporter and the provider, running, with the control url the provider announced.
type private Stack =
    { /// Where the provider said its MCP endpoint is.
      Url : string
      /// SIGTERM to both halves, and time for them to take it.
      Stop : unit -> Async<unit> }

// --- what the provider announced --------------------------------------------------------------
//
// Pure, and beside the rig that reads it, because this is the half of the rig no provider can
// be asked about: a url read wrong is a connection refused, which is what "the provider never
// came up" looks like from here as well.

/// The control url the provider announces, read out of EVERYTHING it has printed so far rather
/// than out of the chunk that has just arrived: a pipe is a byte stream with no promise of line
/// alignment, so the line can be cut in half by a read and neither half matches.
///
/// The line names both ends — `MCP at <url>, exporter at <host:port>` — so the url stops at the
/// comma: `\S+` would take the comma with it, and a url with a comma on the end fails as a
/// connection refused rather than as anything legible. For the same reason the url counts only
/// once something has ENDED it, since a half-arrived line ends in a port with digits missing,
/// which is a perfectly well-formed url onto nothing.
let private announcedUrl (printed: string) : string option =
    let found = Regex.Match (printed, @"MCP at (http://[^\s,]+)[,\s]")
    if found.Success then Some found.Groups.[1].Value else None

let private announcementTests =
    testList "the endpoint the provider announces" [

        testCase "the url stops at the comma the line puts after it" <| fun () ->
            Expect.equal
                (announcedUrl "MCP at http://127.0.0.1:41235/mcp, exporter at 127.0.0.1:50051\n")
                (Some "http://127.0.0.1:41235/mcp")
                "a url carrying the comma would be dialled and refused"

        testCase "a line nothing has ended yet is not an answer" <| fun () ->
            Expect.isNone
                (announcedUrl "MCP at http://127.0.0.1:412")
                "a port with digits still to come is a well-formed url onto nothing"

        testCase "a line cut in half by a read is read once both halves are in" <| fun () ->
            Expect.equal
                (announcedUrl ("MCP at http://127.0.0.1:41" + "235/mcp, exporter at 127.0.0.1:50051\n"))
                (Some "http://127.0.0.1:41235/mcp")
                "what the two reads said together"
    ]

// --- [Jumpstarter]: the two processes -----------------------------------------------------------

/// A port nothing else is on: bind one, read which the OS chose, and hand it straight back.
///
/// The narrowest race available, and taken deliberately — `jmp run` refuses port 0, so the
/// exporter has to be TOLD a port, and the only honest way to name a free one is to have been
/// holding it a moment ago. The provider needs no such thing: its port IS 0, read back off the
/// readiness line, so concurrent runs cannot collide there at all.
let private freePort () : Async<int> =
    Async.FromContinuations (fun (cont, _, _) ->
        let probe = createNetServer ()

        probe.listen (
            0,
            "127.0.0.1",
            fun () ->
                let port = boundPort probe
                probe.close (fun () -> cont port)))

/// One half of the stack, started the way the deployment starts it and remembered so that
/// stopping the stack stops it too.
///
/// `uv run --project` rather than a bare `jumpstarter-provider`: the example is a uv project,
/// its dependencies are locked, and resolving them through uv is exactly how the deployment
/// runs it.
let private start (children: ResizeArray<ChildProcess>) (args: string list) (told: Map<string, string>) : ChildProcess =
    let child =
        spawn
            "uv"
            ([ "run"; "--project"; project ] @ args)
            { Cwd = None
              // This run's environment with what the half is told on top: uv resolves the
              // locked interpreter out of it (`UV_PYTHON`), so a child handed a fresh
              // environment would go looking for one to download.
              //
              // Built and REPLACED rather than added to, because `ambientEnv` is what this
              // suite means by "this run's environment" everywhere else in it — the process's
              // own, read once through the one reader — and a spawn that merged again would
              // have two answers to that question.
              Env =
                ChildEnv.Replacing (
                    (Sandboxes.ambientEnv (), told) ||> Map.fold (fun ambient name value -> Map.add name value ambient))
              Streams = { Stdin = Pipe; Stdout = Pipe; Stderr = Pipe }
              Detached = false }

    // `setEncoding` rather than converting each chunk: it puts a decoder in front of the
    // stream, so a character split across two reads still arrives whole.
    child.stdout.setEncoding utf8
    child.stderr.setEncoding utf8
    children.Add child
    child

/// Wait until `read` finds what it is after in everything the child has printed — on EITHER
/// stream, because which one a readiness line lands on is the child's business — or until the
/// child dies, or until the deadline. Whichever gets there first is the answer, so a process
/// that dies before printing fails here with its own words rather than as a timeout.
let private awaitPrinted
    (child: ChildProcess)
    (what: string)
    (looksLike: string)
    (read: string -> 'a option)
    (timeoutMs: int)
    : Async<'a> =
    Async.FromContinuations (fun (cont, econt, _) ->
        let said = Text.StringBuilder ()
        let mutable settled = false

        let timer =
            JS.setTimeout
                (fun () ->
                    if not settled then
                        settled <- true
                        econt (exn (sprintf "%s never printed %s; saw:\n%s" what looksLike (said.ToString ()))))
                timeoutMs

        let watch (stream: Node.Stream.Readable<string>) =
            stream.on (
                "data",
                fun (chunk: string) ->
                    said.Append chunk |> ignore

                    match read (said.ToString ()) with
                    | Some found when not settled ->
                        settled <- true
                        JS.clearTimeout timer
                        cont found
                    | _ -> ())
            |> ignore

        watch child.stdout
        watch child.stderr

        child.on (
            "exit",
            fun (code: int option) ->
                if not settled then
                    settled <- true
                    JS.clearTimeout timer

                    econt (
                        exn (
                            sprintf
                                "%s exited with %s:\n%s"
                                what
                                (match code with
                                 | Some code -> string code
                                 | None -> "null")
                                (said.ToString ()))))
        |> ignore)

/// Spawn both halves and wait for the provider to say where it is.
///
/// The provider's port is OS-assigned and read back off its readiness line, so concurrent
/// runs cannot collide. The EXPORTER's cannot be: `jmp run` refuses port 0, so one is picked
/// by binding and releasing — the narrowest race available, and the reason the readiness
/// waits above fail loudly rather than hanging.
let private startStack (timeoutMs: int) : Async<Stack> =
    async {
        let! grpc = freePort ()
        let children = ResizeArray<ChildProcess> ()

        let exporter =
            start
                children
                [ "jmp"
                  "run"
                  "--exporter-config"
                  project + "/tests/exporter.yaml"
                  "--tls-grpc-listener"
                  sprintf "127.0.0.1:%d" grpc
                  "--tls-grpc-insecure" ]
                Map.empty

        do!
            awaitPrinted
                exporter
                "the exporter"
                "a started session server"
                (fun printed -> if printed.Contains "Session server started" then Some () else None)
                timeoutMs

        let provider =
            start
                children
                [ "jumpstarter-provider" ]
                (Map.ofList
                    [ "JUMPSTARTER_HOST", sprintf "127.0.0.1:%d" grpc
                      "JUMPSTARTER_PROVIDER_PORT", "0"
                      // Longer than this suite can possibly take, so a claim never expires
                      // mid-test: what is under test here is the protocol, not the timeout
                      // (that is the pytest suite's).
                      "JUMPSTARTER_PROVIDER_TTL", "600" ])

        let! url = awaitPrinted provider "the provider" "where its MCP endpoint is" announcedUrl timeoutMs

        let stop () =
            async {
                for child in children do
                    try child.kill "SIGTERM" with _ -> ()

                // Time for the signal to land rather than a wait on each exit: what this owes
                // the next case is not leaving two processes behind it, and a half that will
                // not go on SIGTERM is not one the next case could wait out either.
                do! Async.Sleep 200
            }

        return { Url = url; Stop = stop }
    }

/// Start the stack, run the body against it, and stop it either way.
///
/// `Async.Catch` rather than `try/finally`: the suite runs on Node, where nothing may block
/// the loop to await a cleanup — and a failed assertion still has to take two child processes
/// down with it, or the next test inherits them.
let private withStack (body: Stack -> Async<unit>) =
    async {
        let! stack = startStack 180000
        let! outcome = body stack |> Async.Catch
        do! stack.Stop ()

        match outcome with
        | Choice1Of2 () -> ()
        | Choice2Of2 error -> raise error
    }

let private expect result =
    match result with
    | Ok value -> value
    | Error e -> failwithf "invariant: %A" e

let private name = McpServerName.create "jumpstarter" |> expect

let private declared (url: string) : McpServerSet =
    { Servers = [ { Name = name; Transport = McpHttp url; Description = None } ] }

let private toolNames (registries: ToolRegistry list) =
    registries |> List.collect ToolRegistry.allowedTools

let private answer (registries: ToolRegistry list) (tool: string) (args: string) =
    match registries |> List.tryFind (fun r -> ToolRegistry.namespaces r = [ "jumpstarter" ]) with
    | None -> failwith "the session has no registry for the jumpstarter namespace"
    | Some registry ->
        async {
            match! registry.Invoke { Namespace = "jumpstarter"; Name = tool; Arguments = args } with
            | Ok answer -> return answer
            | Error e -> return failwithf "the call never reached the tool: %s" e
        }

let private call (registries: ToolRegistry list) (tool: string) (args: string) =
    async {
        let! answered = answer registries tool args
        return answered.Text
    }

/// Poll rather than sleep: what is being waited on is a round trip through two processes and
/// a device, and a fixed sleep is either flaky or slow.
let private until (predicate: unit -> bool) : Async<bool> =
    let rec loop (remaining: int) =
        async {
            if predicate () then return true
            elif remaining <= 0 then return false
            else
                do! Async.Sleep 50
                return! loop (remaining - 50)
        }
    loop 30000

let tests =
    testList "The jumpstarter provider (Plan 18)" [

        announcementTests

        testCaseAsync "a session declares it and gets the exporter as tools it can call" <|
            withStack (fun stack ->
                async {
                    let mcp = McpClient.create ()
                    do! mcp.Apply (declared stack.Url)

                    // The names are the contract between a provider we did not write in our
                    // language and a model that will only ever see these strings.
                    Expect.equal
                        (toolNames (mcp.Registries ()) |> List.sort)
                        [ "mcp__jumpstarter__acquire"
                          "mcp__jumpstarter__driver_call"
                          "mcp__jumpstarter__power"
                          "mcp__jumpstarter__release"
                          "mcp__jumpstarter__status" ]
                        "our client and their server agree about what this exporter offers"

                    let registries = mcp.Registries ()

                    // Reaching the hardware: status names the drivers the exporter config
                    // declares, which means the whole path is live — HTTP, MCP, the SDK,
                    // gRPC, and back.
                    let! status = call registries "status" "{}"
                    Expect.stringContains status "power" "status names the power driver"
                    Expect.stringContains status "serial" "status names the console"

                    // The claim, from the outside: refused before it is taken, and taken
                    // once asked for.
                    let! refused = call registries "power" """{"action":"on"}"""
                    Expect.stringContains refused "acquire" "an unclaimed exporter says how to claim it"
                    let! acquired = call registries "acquire" "{}"
                    Expect.stringContains acquired "yours" "the claim is granted in words a model can act on"
                    let! powered = call registries "power" """{"action":"on"}"""
                    Expect.stringContains powered "done" "the power driver answered"

                    // The console is NOT here, and that is the claim: this provider owns
                    // control, and its console is reached as a terminal over the stream leg
                    // — which the next case drives.
                })

        // The loop Plan 19 closes, across two languages: THEIR provider offers a stream in
        // MCP's `_meta`, OUR client reads it, and OUR WebSocket attach opens it. Neither end
        // has ever seen the other's code, which is the only reason this proves anything.
        testCaseAsync "the console arrives as a stream this session can open" <|
            withStack (fun stack ->
                async {
                    let mcp = McpClient.create ()
                    do! mcp.Apply (declared stack.Url)
                    let registries = mcp.Registries ()

                    let! acquired = answer registries "acquire" "{}"
                    let offer =
                        match acquired.Stream with
                        | Some offer -> offer
                        | None -> failwithf "acquire offered no stream: %s" acquired.Text
                    Expect.isFalse
                        offer.Ticket.Capabilities.CanInstrument
                        "a console is bytes: no blocks, no exit codes, nothing claimed that is not true"
                    Expect.isTrue offer.Renewable "and asking again is how you get another one"

                    let heard = System.Text.StringBuilder ()
                    let! attached = AttachWs.attach offer.Ticket 80 24 (fun text -> heard.Append text |> ignore)
                    let handle = attached |> expect

                    // Both ways, over the exporter's loopback line.
                    handle.Write "over the stream\r"
                    let! echoed = until (fun () -> heard.ToString().Contains "over the stream")
                    Expect.isTrue echoed "what a person types reaches the device and comes back"

                    // The console has ONE door, and this is it. There used to be two — the
                    // provider offered its own read and write tools beside the stream — and
                    // the pair had to be kept in step by a tee that starved neither. Both are
                    // gone: what an agent reads it reads through the terminal this stream
                    // becomes, past the lease, where everyone can see who is typing.
                    handle.Kill ()
                    let! ending = handle.Exited
                    Expect.equal ending (SandboxExited 0) "the stream ends in band, not by an abrupt close"
                })

        testCaseAsync "a second session is refused, and told who has it" <|
            withStack (fun stack ->
                async {
                    // Two clients, so two MCP sessions — which is what the provider keys a
                    // claim by, and the only way to test arbitration honestly.
                    let first = McpClient.create ()
                    let second = McpClient.create ()
                    do! first.Apply (declared stack.Url)
                    do! second.Apply (declared stack.Url)

                    let! taken = call (first.Registries ()) "acquire" "{}"
                    Expect.stringContains taken "yours" "the first session took it"

                    let! refused = call (second.Registries ()) "acquire" "{}"
                    Expect.stringContains refused "already held by" "the refusal says somebody has it"
                    Expect.stringContains refused "in use, not broken" "and that this is not a fault"

                    // And giving it back is what makes it available, rather than waiting.
                    let! _ = call (first.Registries ()) "release" "{}"
                    let! taken = call (second.Registries ()) "acquire" "{}"
                    Expect.stringContains taken "yours" "a released exporter is the next asker's"
                })
    ]
