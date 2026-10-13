module Yession.Tests.DirectToolHook

// What `.claude/direct-tool-hook.sh` says to a Bash call.
//
// The rule it holds used to be a sentence in the root AGENTS.md: run the task scripts, never
// `dotnet`, `fable` or `esbuild` directly, because the scripts match tool versions and PATH to
// CI. A sentence is read once, at the start of a session, and an agent that was told it then
// types `dotnet test` anyway. The hook is the rule where the call is made: it denies the call,
// says what to run instead, and offers one way out, `YESSION_DIRECT_TOOL="<reason>"`, that it
// counts. An agent that keeps needing the way out is telling us a task verb is missing.
//
// Every case spawns the script as the harness does: JSON on stdin, `CLAUDE_PROJECT_DIR` set,
// whatever it prints on stdout, and an exit code that must be 0. Each runs in a checkout of its
// own (a temp directory), because the log the escape keeps lives in it.
//
// The script FAILS OPEN, so a box with no `jq` would read as a pass in every case that expects
// nothing said. A missing `jq` therefore fails the suite by name, rather than skipping it.
//
// No capability: a shell and `jq`, which every box that runs `check` has.

open Fable.Core
open Fable.NodeExtras
open Fable.Pyxpecto
#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

/// What the hook printed, read as the harness reads it.
[<RequireQualifiedAccess>]
type private Said =
    /// No output: the call goes on to the user's permission prompts untouched.
    | Nothing
    /// A denial, with the reason the model reads.
    | Denied of reason : string
    /// Context for the model and NO permission decision, which would bypass the user's prompts.
    | Context of text : string
    /// Anything else, including a decision that is not a denial.
    | Unexpected of stdout : string

let private describe (said: Said) : string =
    match said with
    | Said.Nothing -> "nothing"
    | Said.Denied reason -> "a denial: " + reason
    | Said.Context text -> "context: " + text
    | Said.Unexpected stdout -> "something unexpected: " + stdout

type private Output =
    { Decision : string option
      Reason : string option
      Context : string option }

let private output : Decoder<Output> =
    Decode.field
        "hookSpecificOutput"
        (Decode.object (fun get ->
            { Decision = get.Optional.Field "permissionDecision" Decode.string
              Reason = get.Optional.Field "permissionDecisionReason" Decode.string
              Context = get.Optional.Field "additionalContext" Decode.string }))

let private said (stdout: string) : Said =
    if stdout.Trim () = "" then
        Said.Nothing
    else
        match Decode.fromString output stdout with
        | Ok { Decision = Some "deny"; Reason = Some reason } -> Said.Denied reason
        | Ok { Decision = None; Context = Some text } -> Said.Context text
        | _ -> Said.Unexpected stdout

/// Anchored at the repository root rather than the runner's working directory: the subject is
/// a committed script.
let private repoRoot () : string =
    try
        (execFileSync "git" [ "rev-parse"; "--show-toplevel" ] SyncOptions.none).Trim ()
    with e ->
        failwithf "this suite needs a git checkout to find .claude/direct-tool-hook.sh in: %s" e.Message

let private hook : Lazy<string> = lazy (repoRoot () + "/.claude/direct-tool-hook.sh")

/// The first thing every case asks, because a hook that fails open without `jq` answers
/// "nothing" to every payload and a case that expects nothing would pass for the wrong reason.
let private jqIsHere : Lazy<unit> =
    lazy (
        let found = spawnSync "jq" [ "--version" ] SyncOptions.none
        if found.status <> Some 0 then
            failwith
                "jq is not on PATH. The hook parses its payload with jq and fails OPEN without it, so on this box every case that expects the hook to say nothing would pass vacuously and the denials could not be reached. Install jq (nixpkgs#jq, or your package manager) and run the suite again"
    )

/// A checkout of its own for one case, removed after it, a failure too.
let private inCheckout (body: string -> unit) : unit =
    let checkout = TestFiles.tempDir "direct-tool-hook-"
    try
        body checkout
    finally
        TestFiles.removeTree checkout

let private logOf (checkout: string) : string = checkout + "/.devenv/direct-tool.log"

/// The payload the harness sends for a Bash call.
let private payload (command: string) : string =
    Encode.toString
        0
        (Encode.object
            [ "hook_event_name", Encode.string "PreToolUse"
              "tool_name", Encode.string "Bash"
              "tool_input", Encode.object [ "command", Encode.string command ] ])

/// Anything the hook must answer with exit 0: a non-zero code other than 2 is an error the
/// user sees on every Bash call, and 2 is a block.
let private result (checkout: string) (stdin: string) : SyncResult =
    jqIsHere.Force ()
    spawnSync
        hook.Value
        []
        { SyncOptions.none with
            Input = Some stdin
            Env = ChildEnv.Adding (Map [ "CLAUDE_PROJECT_DIR", checkout ]) }

let private hookSaid (result: SyncResult) : Said =
    match result.status, result.stdout with
    | Some 0, Some stdout -> said stdout
    | status, _ ->
        failwithf
            "the hook must exit 0 on every payload, and exited %A (stderr: %A). If it did not start, check that the file is executable (git mode 100755)"
            status
            result.stderr

/// What the hook says to a Bash call with this command, in this checkout.
let private ask (checkout: string) (command: string) : Said =
    hookSaid (result checkout (payload command))

let private expectPasses (command: string) : unit =
    inCheckout (fun checkout ->
        match ask checkout command with
        | Said.Nothing -> ()
        | other -> failwithf "`%s` names a tool without running it directly, and the hook said %s" command (describe other))

let private expectDenied (command: string) : unit =
    inCheckout (fun checkout ->
        match ask checkout command with
        | Said.Denied _ -> ()
        | other -> failwithf "`%s` runs a tool directly, and the hook said %s" command (describe other))

/// The escape, as an agent writes it.
let private escaping (reason: string) (command: string) : string =
    sprintf "YESSION_DIRECT_TOOL=\"%s\" %s" reason command

/// The lines of the log the escape keeps, each as its tab-separated fields.
let private logged (checkout: string) : string list list =
    TestFiles.read (logOf checkout)
    |> fun text -> text.Split [| '\n' |]
    |> Array.filter (fun line -> line <> "")
    |> Array.map (fun line -> line.Split [| '\t' |] |> List.ofArray)
    |> List.ofArray

/// `for each command, a case of its own` — each one is an invariant that can break alone, and a
/// name that says which.
let private eachOf (what: string) (check: string -> unit) (commands: string list) =
    testList what [ for command in commands -> testCase (command.Replace ("\n", "\\n")) <| fun () -> check command ]

/// A box with no `jq`: a PATH holding a shell and `cat` and nothing else.
let private withoutJq (checkout: string) : SyncResult =
    let whereIs (tool: string) =
        (execFileSync "sh" [ "-c"; "command -v \"$1\""; "sh"; tool ] SyncOptions.none).Trim ()
    let bin = checkout + "/bin"
    TestFiles.ensureDir bin
    for tool in [ "bash"; "cat" ] do
        TestFiles.symlink (whereIs tool) (bin + "/" + tool)
    spawnSync
        hook.Value
        []
        { SyncOptions.none with
            Input = Some (payload "dotnet build")
            Env = ChildEnv.Replacing (Map [ "PATH", bin; "CLAUDE_PROJECT_DIR", checkout ]) }

let private registrations : Decoder<(string * string list) list> =
    Decode.at
        [ "hooks"; "PreToolUse" ]
        (Decode.list (
            Decode.object (fun get ->
                get.Required.Field "matcher" Decode.string,
                get.Required.Field "hooks" (Decode.list (Decode.field "command" Decode.string)))
        ))

let tests =
    testList "Direct tool hook" [

        testCase "dotnet build is denied" <| fun () -> expectDenied "dotnet build"

        testCase "the denial says why, what to run instead and how to escape" <| fun () ->
            inCheckout (fun checkout ->
                match ask checkout "dotnet build" with
                | Said.Denied reason ->
                    for needle in [ "PATH to CI"; "devenv shell -- <task>"; "devenv shell -- help"; "dotnet fsi tasks.fsx <verb>"; "YESSION_DIRECT_TOOL" ] do
                        Expect.stringContains reason needle "the model reads this and nothing else, so it must say each of these"
                | other -> failwithf "`dotnet build` should be denied, and the hook said %s" (describe other))

        testCase "cd x && dotnet test is denied" <| fun () -> expectDenied "cd x && dotnet test"

        testCase "devenv shell -- check passes" <| fun () -> expectPasses "devenv shell -- check"

        testCase "dotnet fsi tasks.fsx check passes" <| fun () -> expectPasses "dotnet fsi tasks.fsx check"

        testCase "dotnet fsi --quiet tasks.fsx check passes" <| fun () -> expectPasses "dotnet fsi --quiet tasks.fsx check"

        testCase "dotnet fsi on any other script is denied" <| fun () -> expectDenied "dotnet fsi scratch.fsx"

        // An argument is not a command word: the tools' names turn up in searches and logs all day.
        eachOf
            "a tool named as an argument passes"
            expectPasses
            [ "rg dotnet"
              "grep -n esbuild package.json"
              "cat fable.log"
              "git log -- dotnet"
              "echo dotnet"
              "command -v dotnet"
              "echo \"a; dotnet build\""
              "git commit -m \"x\ndotnet build\""
              "cat > notes.md <<EOF\ndotnet build\nEOF" ]

        eachOf
            "a task script passes"
            expectPasses
            [ "devenv shell -- help"
              "flock /tmp/build.lock devenv shell -- check --only \"Repo layout\""
              "dotnet fsi ./tasks.fsx lint" ]

        // The command word is where the shell starts a command, not only the start of the line.
        eachOf
            "a direct tool is denied"
            expectDenied
            [ "dotnet test"
              "fable src"
              "esbuild app.js --bundle"
              "npx fable src"
              "npx -y esbuild app.js"
              "dotnet fable src"
              "node_modules/.bin/esbuild app.js"
              "./tools/fable src"
              "cd x ; dotnet build"
              "ls | dotnet build"
              "false || dotnet build"
              "x=$(dotnet --version)"
              "(dotnet build)"
              "if true; then dotnet build; fi"
              "for f in a b; do dotnet build; done"
              "FOO=1 BAR=\"a b\" dotnet build"
              "env FOO=1 dotnet build"
              "time dotnet build"
              "exec dotnet build"
              "sudo dotnet build"
              "nice dotnet build"
              "timeout 60 dotnet build"
              "flock /tmp/build.lock dotnet build"
              "devenv shell -- dotnet build"
              "flock /tmp/build.lock devenv shell -- dotnet test" ]

        testCase "an escaped call passes" <| fun () ->
            inCheckout (fun checkout ->
                Expect.equal (ask checkout (escaping "no verb runs one project" "dotnet test")) Said.Nothing "the reason is given, so nothing is said")

        testCase "an escaped call is logged under its tool and first subcommand" <| fun () ->
            inCheckout (fun checkout ->
                ask checkout (escaping "no verb runs one project" "dotnet test --no-build") |> ignore
                Expect.equal (logged checkout |> List.map (List.item 1)) [ "dotnet test" ] "one line, keyed by the tool and its first subcommand")

        testCase "an escaped call is logged with its reason" <| fun () ->
            inCheckout (fun checkout ->
                ask checkout (escaping "no verb runs one project" "dotnet test") |> ignore
                Expect.equal (logged checkout |> List.map (List.item 2)) [ "no verb runs one project" ] "the quoted reason, whole")

        testCase "a call that is denied is not logged" <| fun () ->
            inCheckout (fun checkout ->
                ask checkout "dotnet test" |> ignore
                Expect.isFalse (TestFiles.exists (logOf checkout)) "nothing ran, so nothing was escaped")

        testCase "an empty reason is not an escape" <| fun () -> expectDenied "YESSION_DIRECT_TOOL= dotnet test"

        testCase "a quoted empty reason is not an escape" <| fun () -> expectDenied "YESSION_DIRECT_TOOL=\"\" dotnet test"

        testCase "a reason of spaces is not an escape" <| fun () -> expectDenied "YESSION_DIRECT_TOOL=\"  \" dotnet test"

        testCase "an escape covers the command it prefixes and no other" <| fun () ->
            expectDenied (escaping "one-off" "dotnet build" + " && dotnet test")

        testCase "the second escape of one subcommand says nothing" <| fun () ->
            inCheckout (fun checkout ->
                ask checkout (escaping "first" "dotnet test") |> ignore
                Expect.equal (ask checkout (escaping "second" "dotnet test")) Said.Nothing "two is still not a pattern")

        testCase "the third escape of one subcommand names it and asks for a tasks.fsx verb" <| fun () ->
            inCheckout (fun checkout ->
                for reason in [ "first"; "second" ] do
                    ask checkout (escaping reason "dotnet test") |> ignore
                match ask checkout (escaping "third" "dotnet test") with
                | Said.Context text ->
                    Expect.stringContains text "`dotnet test`" "it names the subcommand that keeps being escaped"
                    Expect.stringContains text "tasks.fsx verb" "it asks the user for a verb that would cover it"
                | other -> failwithf "the third escape should add context for the model, and the hook said %s" (describe other))

        testCase "escapes of different subcommands are counted apart" <| fun () ->
            inCheckout (fun checkout ->
                for reason in [ "first"; "second" ] do
                    ask checkout (escaping reason "dotnet test") |> ignore
                Expect.equal (ask checkout (escaping "third, but another" "dotnet build")) Said.Nothing "`dotnet build` has been escaped once")

        testCase "an unparseable payload passes" <| fun () ->
            inCheckout (fun checkout -> Expect.equal (hookSaid (result checkout "not json {")) Said.Nothing "fail open")

        testCase "a payload with no command passes" <| fun () ->
            inCheckout (fun checkout ->
                Expect.equal (hookSaid (result checkout "{\"tool_name\":\"Bash\",\"tool_input\":{}}")) Said.Nothing "fail open")

        testCase "a box with no jq passes" <| fun () ->
            inCheckout (fun checkout ->
                Expect.equal (hookSaid (withoutJq checkout)) Said.Nothing "fail open: the hook cannot read a payload, so it cannot judge one")

        testCase "settings.json runs the hook before every Bash call" <| fun () ->
            match Decode.fromString registrations (TestFiles.read (repoRoot () + "/.claude/settings.json")) with
            | Ok groups ->
                Expect.isTrue
                    (groups
                     |> List.exists (fun (matcher, commands) ->
                         matcher.Contains "Bash" && commands |> List.exists (fun command -> command.EndsWith ".claude/direct-tool-hook.sh")))
                    "a hook nothing runs enforces nothing: .claude/settings.json needs a PreToolUse entry matching Bash whose command is .claude/direct-tool-hook.sh"
            | Error reason -> failwithf ".claude/settings.json has no PreToolUse hooks this can read: %s" reason
    ]
