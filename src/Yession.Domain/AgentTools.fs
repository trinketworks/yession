namespace Yession.Domain.Agent

open Yession.Domain
open Yession.Domain.Sandboxes

// The session's own tools, as a registry (Plan 16, part A).
//
// This is the `yession` namespace: the verbs a turn has always had, moved off the
// runner's parameter list and onto a value. They live in the domain rather than beside the
// SDK adapter for one reason worth stating — what a tool ANSWERS is the interesting part,
// and it is now testable without a model, a subprocess or a browser. The adapter is left
// with the thing only it can do: turning descriptors into SDK tools.
//
// The wire names are unchanged (`mcp__yession__execute_command`, …) because the namespace
// is the SDK server's name and the server was already called `yession`.

open System
open Yession.Domain.Prs
open Yession.Domain.Files
open Yession.Domain.Terminals
open Yession.Domain.Tools

#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

/// Reading a call's arguments. Every body does this for itself: the generic path carries
/// JSON precisely so that recording and redaction can be schema-driven, and a decode that
/// lived out there would have to know every tool's shape to do the same job.
module private ToolArgs =

    let private read (decoder: Decoder<'a>) (json: string) : Result<'a, string> =
        let json = if String.IsNullOrWhiteSpace json then "{}" else json
        match Decode.fromString decoder json with
        | Ok value -> Ok value
        | Error e -> Error (sprintf "could not read the arguments: %s" e)

    let string (key: string) (json: string) : Result<string, string> =
        read (Decode.object (fun get -> get.Required.Field key Decode.string)) json

    /// `execute_command`'s three: the line, which named sandbox to run it in, and whether the
    /// caller intends to wait for it (Plan 20, stage 2). An absent or empty `sandbox` is the
    /// default one, which is what the optional parameter degrades to.
    /// `execute_command`'s arguments: the line, where to run it (a terminal by id, or a
    /// sandbox's own), and whether to wait.
    let commandWhere (json: string) : Result<string * string option * string option * bool * bool, string> =
        read
            (Decode.object (fun get ->
                get.Required.Field "command" Decode.string,
                get.Optional.Field "terminal" Decode.string |> Option.filter (fun s -> s <> ""),
                get.Optional.Field "sandbox" Decode.string |> Option.filter (fun s -> s <> ""),
                get.Optional.Field "background" Decode.bool |> Option.defaultValue false,
                get.Optional.Field "stdin" Decode.bool |> Option.defaultValue false))
            json

    /// `start_work_sandbox`'s pair: the sandbox name, and the credentials to forward.
    /// `open_terminal`'s pair: what the terminal is for, and which sandbox to open it in.
    let nameSandbox (json: string) : Result<string * string option, string> =
        read
            (Decode.object (fun get ->
                get.Required.Field "name" Decode.string,
                get.Optional.Field "sandbox" Decode.string |> Option.filter (fun s -> s <> "")))
            json

    let nameForward (json: string) : Result<string * string list, string> =
        read
            (Decode.object (fun get ->
                get.Required.Field "name" Decode.string,
                get.Optional.Field "forward" (Decode.list Decode.string) |> Option.defaultValue []))
            json

    /// `set_shell_profile`'s pair, both optional: where shells opened from now on start,
    /// and whose sandbox. An absent `cwd` is the CLEAR, and an absent `sandbox` is the
    /// default one — so a bare call means "put the default sandbox back the way it was".
    let cwdSandbox (json: string) : Result<string option * string option, string> =
        read
            (Decode.object (fun get ->
                get.Optional.Field "cwd" Decode.string,
                get.Optional.Field "sandbox" Decode.string |> Option.filter (fun s -> s <> "")))
            json

    /// `read_file`'s four: the path, the window (`offset` a 1-based first line, `limit` a
    /// count), and which sandbox. An absent or empty `sandbox` is the default one.
    let fileRead (json: string) : Result<string * int option * int option * string option, string> =
        read
            (Decode.object (fun get ->
                get.Required.Field "path" Decode.string,
                get.Optional.Field "offset" Decode.int,
                get.Optional.Field "limit" Decode.int,
                get.Optional.Field "sandbox" Decode.string |> Option.filter (fun s -> s <> "")))
            json

    /// `edit_file`'s five: the path, the text to find, the text to put there, whether every
    /// occurrence, and which sandbox.
    let fileEdit (json: string) : Result<string * string * string * bool * string option, string> =
        read
            (Decode.object (fun get ->
                get.Required.Field "path" Decode.string,
                get.Required.Field "old_string" Decode.string,
                get.Required.Field "new_string" Decode.string,
                get.Optional.Field "replace_all" Decode.bool |> Option.defaultValue false,
                get.Optional.Field "sandbox" Decode.string |> Option.filter (fun s -> s <> "")))
            json

    /// `write_file`'s three: the path, the whole content, and which sandbox.
    let fileWrite (json: string) : Result<string * string * string option, string> =
        read
            (Decode.object (fun get ->
                get.Required.Field "path" Decode.string,
                get.Required.Field "content" Decode.string,
                get.Optional.Field "sandbox" Decode.string |> Option.filter (fun s -> s <> "")))
            json

    let two (first: string) (second: string) (json: string) : Result<string * string, string> =
        read
            (Decode.object (fun get ->
                get.Required.Field first Decode.string, get.Required.Field second Decode.string))
            json

    /// `read_terminal`'s three: which terminal, where to read from, and what to wait for. An
    /// absent `from` is the tail and an absent `wait_for` is a read that does not wait, which
    /// is what the optional parameters degrade to. A `timeout_seconds` without a `wait_for` is
    /// nothing to bound, so the wait is only assembled when there is something to wait FOR.
    let terminalRead (json: string) : Result<string * int option * TerminalWait option, string> =
        let decoded =
            read
                (Decode.object (fun get ->
                    get.Required.Field "terminal" Decode.string,
                    get.Optional.Field "from" Decode.int,
                    get.Optional.Field "wait_for" Decode.string |> Option.filter (fun s -> s <> ""),
                    get.Optional.Field "wait_for_pattern" Decode.string |> Option.filter (fun s -> s <> ""),
                    get.Optional.Field "timeout_seconds" Decode.float |> Option.defaultValue 10.0))
                json
        match decoded with
        | Error e -> Error e
        | Ok (terminal, from, literal, pattern, timeout) ->
            // Two fields rather than one and a mode flag, because a flag makes the meaning of
            // `wait_for` depend on another argument: a caller that sets one and forgets the
            // other gets a LITERAL match on a regex string, which is wrong, silent, and looks
            // exactly like a device that never answered. Two fields make that a refusal.
            match literal, pattern with
            | Some _, Some _ ->
                Error "wait_for and wait_for_pattern are two ways to say the same thing — give one"
            | None, None -> Ok (terminal, from, None)
            | Some literal, None ->
                Ok (terminal, from, Some { Until = MatchLiteral literal; TimeoutSeconds = timeout })
            | None, Some source ->
                // Compiled HERE, so a pattern outside the subset is an answer to this call
                // rather than something discovered part-way through a wait that then has to
                // explain itself.
                Pattern.compile source
                |> Result.map (fun compiled ->
                    terminal, from, Some { Until = MatchPattern (compiled, source); TimeoutSeconds = timeout })

    /// `remove_repo`'s pair: which repo, and whether uncommitted changes may go with it.
    let repoForce (json: string) : Result<string * bool, string> =
        read
            (Decode.object (fun get ->
                get.Required.Field "repo" Decode.string,
                get.Optional.Field "force" Decode.bool |> Option.defaultValue false))
            json

    let repoBranchCreate (json: string) : Result<string * string * bool, string> =
        read
            (Decode.object (fun get ->
                get.Required.Field "repo" Decode.string,
                get.Required.Field "branch" Decode.string,
                get.Optional.Field "create" Decode.bool |> Option.defaultValue false))
            json

module AgentTools =

    /// The namespace the session's own tools live in. One string, referenced everywhere,
    /// because it is simultaneously the SDK server name, the wire-name segment and what the
    /// tool-use record shows.
    [<Literal>]
    let Namespace = "yession"

    /// One outcome, rendered as tool text (Plan 13, stage 3b).
    ///
    /// Every case says WHICH state it is in, and the wording is load-bearing rather than
    /// decorative: telling a model "queued" when its command is actually blocked on a person
    /// has it conclude, after a silent pause, that the command failed and try something else
    /// — which is exactly how a review gate turns into the agent routing around the review.
    /// A command that tried to write under `/tmp` and was refused, named for what it was.
    ///
    /// The sandbox denies `/tmp` on macOS and hands Linux a tmpfs that vanishes when the
    /// command exits, but every sandbox sets `$TMPDIR` to a writable directory of the
    /// session's own. An agent that reached for `/tmp` learned this only from a raw
    /// `Operation not permitted` — which on macOS arrives wrapped in `xcode-select` noise
    /// from the interpreter shim, nowhere near the cause — and recovered a turn later if at
    /// all. This reads the denial and says where to write instead, in the same answer.
    ///
    /// Signature, not a parse: a `/tmp` path and a refusal in the same output. It is a hint
    /// appended to a failure, so a rare false positive costs a true sentence on a command
    /// that failed for another reason, never a wrong exit code or a swallowed error.
    let private scratchDenialHint (text: string) : string =
        let mentionsTmp = text.Contains "/tmp/" || text.Contains "/tmp " || text.Contains "/tmp:" || text.EndsWith "/tmp"
        let refused =
            text.Contains "Operation not permitted"
            || text.Contains "Permission denied"
            || text.Contains "Read-only file system"
        if mentionsTmp && refused then
            "\n[a write under /tmp was refused: the sandbox does not let you write there. Your writable scratch directory is $TMPDIR, which is set in every sandbox — rerun writing under $TMPDIR instead.]"
        else ""

    let renderOutcome (outcome: TerminalCommandOutcome) : string =
        let where = sprintf "terminal %s" (TerminalId.value outcome.Terminal)
        let handle = QueueId.value outcome.Handle
        // What an elision has to say to be acted on, rather than merely admitted. "N earlier
        // characters omitted" is honest and useless: it does not say which end you are
        // holding, and it does not say where the rest is. Both are said here, and the note
        // about a missing MIDDLE is written into the gap rather than after the whole answer —
        // a reader that meets the second half with no warning reads it as continuous with the
        // first, which is a worse failure than losing it.
        let readOn =
            match outcome.From with
            | Some from -> sprintf ", read_terminal on %s from: %d" (TerminalId.value outcome.Terminal) from
            | None -> ""
        let output =
            if outcome.Output = "" then ""
            else
                match outcome.Kept with
                | OutputEnd.Whole -> "\n" + outcome.Output
                | OutputEnd.Head ->
                    sprintf
                        "\n%s\n[the first %d characters; %d more after them%s]"
                        outcome.Output
                        outcome.Output.Length
                        outcome.Elided
                        readOn
                | OutputEnd.BothEnds headLength ->
                    sprintf
                        "\n%s\n[%d characters omitted from the middle%s]\n%s"
                        (outcome.Output.Substring (0, headLength))
                        outcome.Elided
                        readOn
                        (outcome.Output.Substring headLength)
        let output = output + scratchDenialHint output
        // Every status opens with ONE upper-case word that says which it is, and the words
        // differ at the first letter. A finished command used to open `exit code 0 in …`,
        // beside `STILL RUNNING in …` for one that had not — and both were followed by the
        // same ten lines of a devshell's prelude, so an agent that had read STILL RUNNING
        // four times read a fifth answer the same way, tried to interrupt a block that had
        // ended, and committed without the lint it was waiting on. Measured, session
        // 9ZBRTTCZ; the word is the fix.
        match outcome.Status with
        | TerminalCommandRan (CommandSucceeded code) -> sprintf "FINISHED with exit code %d in %s%s" code where output
        | TerminalCommandRan (CommandFailed code) -> sprintf "FAILED with exit code %d in %s%s" code where output
        | TerminalCommandRan CommandTimedOut -> sprintf "TIMED OUT in %s%s" where output
        | TerminalCommandRan (CommandExecutionFailed reason) ->
            sprintf "EXECUTION FAILED in %s: %s%s" where reason output
        | TerminalCommandRunning ->
            sprintf
                "STILL RUNNING in %s. Not finished; nothing cancelled. For long commands pass background: true and end your turn — you're woken when they finish; otherwise check_pending '%s' for the outcome. Stuck or waiting on input? write_terminal can type in — \"\\u0003\" interrupts.%s"
                where handle output
        | TerminalCommandInteractive ->
            sprintf
                "WAITING FOR A KEYSTROKE in %s. A full-screen program opened, so it won't finish on its own, and the terminal is now YOURS. write_terminal to answer it, read_terminal to see the screen; leaving the program finishes the command. check_pending '%s' for the outcome.%s"
                where handle output
        // Each hold names its way out. "Somebody is using it, or another command is running
        // there" was true and useless: told that over a terminal its own stuck command was
        // holding, an agent tried the same terminal four times and every verb that might have
        // ended the wait was one it had not been pointed at.
        | TerminalCommandAwaitingTerminal BehindBlock ->
            sprintf
                "WAITING FOR %s — another command runs there and this one is queued behind it; it has NOT run. To run it now beside that: open_terminal, then execute_command with that `terminal`. To end the running one: if it's yours, write_terminal \"\\u0003\" into that terminal; if the terminal is yours, close_terminal ends everything in it. Otherwise check_pending '%s' later."
                where handle
        | TerminalCommandAwaitingTerminal BehindQueue ->
            sprintf
                "WAITING FOR %s — other commands are queued ahead; it has NOT run, and runs when they do. check_pending '%s' later, or open_terminal to run it elsewhere now."
                where handle
        | TerminalCommandAwaitingTerminal HeldByPerson ->
            sprintf
                "WAITING FOR %s — somebody is typing there; it has NOT run, and runs when they finish, which is theirs to decide. check_pending '%s' later, or open_terminal to run it elsewhere now."
                where handle
        | TerminalCommandAwaitingTerminal UnmarkedShell ->
            sprintf
                "WAITING FOR %s — its shell stopped answering the session's instrumentation, and a person must re-arm it before anything runs there; it has NOT run. open_terminal runs it elsewhere now; check_pending '%s' picks it up once the terminal is repaired."
                where handle
        | TerminalCommandRefused (by, reason) ->
            let who = ActorRef.token by
            match reason with
            | Some reason -> sprintf "REFUSED by %s in %s: %s. It did not run — do not try to run it another way." who where reason
            | None -> sprintf "REFUSED by %s in %s. It did not run — do not try to run it another way." who where

    // ---------------------------------------------------------------------------------
    // The bodies. Each takes the raw arguments and answers text. A tool that RAN and went
    // badly answers `Ok` with text saying so — the model is meant to read it and choose
    // differently. `Error` means the call did not happen (arguments unreadable), and only
    // the dispatch below can produce one.
    // ---------------------------------------------------------------------------------

    /// A body that always answers: `Error` is for the call not happening, and once the
    /// arguments have been read it always does.
    let private ok (body: Async<string>) : Async<Result<ToolAnswer, string>> =
        async {
            let! text = body
            return Ok (ToolAnswer.text text)
        }

    /// The same, for the two verbs that can say which BLOCK they became.
    let private answered (body: Async<ToolAnswer>) : Async<Result<ToolAnswer, string>> =
        async {
            let! answer = body
            return Ok answer
        }

    let private withRepo (raw: string) (inner: RepoRef -> Async<string>) : Async<string> =
        async {
            match RepoRef.create raw with
            | Error e -> return sprintf "not a repo name: %s" e
            | Ok repo -> return! inner repo
        }

    let private withSandbox (raw: string) (inner: SandboxRef -> Async<string>) : Async<string> =
        async {
            match SandboxRef.parse raw with
            | Error e -> return sprintf "not a sandbox: %s" e
            | Ok name -> return! inner name
        }

    // The two verbs that produce a BLOCK say so, because the tool-use record needs to know
    // — a call that became a block draws no chip of its own, and only the call can tell.
    let private executeCommand
        (capabilities: AgentCapabilities)
        (command: string)
        (terminal: string option)
        (sandbox: string option)
        (background: bool)
        (stdin: bool)
        : Async<ToolAnswer> =
        async {
            // A terminal already sits in a sandbox, so naming both is a question with two
            // answers — refused rather than resolved by a precedence the model would have to
            // learn from the outcome.
            let target =
                match terminal, sandbox with
                | Some _, Some _ -> Error "name a terminal or a sandbox, not both — a terminal is already in one"
                | Some t, None -> TerminalId.create t |> Result.mapError (sprintf "not a terminal id: %s") |> Result.map (InTerminal >> Some)
                | None, Some s -> SandboxRef.parse s |> Result.mapError (sprintf "not a sandbox: %s") |> Result.map (InSandbox >> Some)
                | None, None -> Ok None
            match target with
            | Error e -> return ToolAnswer.text e
            | Ok target ->
                match! capabilities.Terminals.Execute { Command = command; Target = target; Background = background; Stdin = stdin } with
                | Ok outcome -> return { Text = renderOutcome outcome; Block = outcome.Block; Stream = None }
                | Error reason -> return ToolAnswer.text (sprintf "could not run the command: %s" reason)
        }

    /// The one renderer for every gated command (Plan 15, stage 3b). A gate has few
    /// answers, so this is where they become the sentences the model reads — once, rather
    /// than once per command, which is what stops a new command inventing its own
    /// vocabulary for "this was refused".
    ///
    /// A refusal is phrased as a refusal and not as an error the model should route around:
    /// it names who said no, and their reason when they gave one — a decision that reads as
    /// a malfunction gets retried another way.
    let renderCommandOutcome (outcome: CommandOutcome) : string =
        match outcome.Status with
        | CommandRan said -> said
        // Nobody is being waited for here, and saying otherwise is the failure this case
        // exists to end: a slow command is work in progress, not a decision somebody was
        // never offered.
        | CommandRunning ->
            match outcome.Handle with
            | Some handle ->
                sprintf
                    "STILL RUNNING: `%s` not finished; nothing cancelled, nobody waiting on you. check_pending '%s' to pick it up."
                    outcome.Summary
                    (QueueId.value handle)
            | None -> sprintf "STILL RUNNING: `%s` has not finished. Nothing was cancelled." outcome.Summary
        | CommandRefusedBy (by, reason) ->
            let who = ActorRef.token by
            match reason with
            | Some reason ->
                sprintf "REFUSED by %s: %s. `%s` did not happen — do not try another way round it." who reason outcome.Summary
            | None -> sprintf "REFUSED by %s. `%s` did not happen — do not try another way round it." who outcome.Summary

    /// `check_pending`: resume a handle, whichever kind of act it named (Plan 15, stage 3b).
    /// ONE verb, because the handle is one type — the alternative is an agent that has to
    /// know, before it asks, what it is waiting on.
    let private checkPending (capabilities: AgentCapabilities) (handle: string) : Async<ToolAnswer> =
        async {
            match QueueId.create handle with
            | Error e -> return ToolAnswer.text (sprintf "not a command handle: %s" e)
            | Ok handle ->
                match! capabilities.Terminals.CheckPending handle with
                | Ok (PendingTerminal outcome) -> return { Text = renderOutcome outcome; Block = outcome.Block; Stream = None }
                | Ok (PendingCommand outcome) -> return ToolAnswer.text (renderCommandOutcome outcome)
                | Error reason -> return ToolAnswer.text (sprintf "could not read that command: %s" reason)
        }

    /// Type into a live-only terminal (Plan 19). A refusal is an ANSWER rather than an error,
    /// like every other tool here: the model is meant to read "use execute_command there" and
    /// do that, not to see a protocol failure and try a third thing.
    let private writeTerminal (capabilities: AgentCapabilities) (id: TerminalId) (data: string) : Async<string> =
        async {
            match! capabilities.Terminals.Write id data with
            | Ok answer -> return answer
            | Error reason -> return sprintf "could not type into terminal %s: %s" (TerminalId.value id) reason
        }

    /// Read a live-only terminal (Plan 19). Same rendering as a block's output, because it is
    /// the same question — what did this print — and a model should not have to learn two
    /// shapes for one answer.
    let private readTerminal
        (capabilities: AgentCapabilities)
        (id: TerminalId)
        (from: int option)
        (waitFor: TerminalWait option)
        : Async<string> =
        async {
            match! capabilities.Terminals.Read id from waitFor with
            | Error reason -> return sprintf "could not read terminal %s: %s" (TerminalId.value id) reason
            | Ok tail when tail.Text = "" && tail.Length = 0 ->
                return sprintf "terminal %s has said nothing" (TerminalId.value id)
            | Ok tail ->
                // The bounds are stated on EVERY answer, not only when something was lost. A
                // model told the text alone cannot tell "this is everything" from "this is
                // the last 2000 characters of a day", and it will read it as the first —
                // which is how an agent concludes a boot log is three lines long.
                let where =
                    if tail.Through >= tail.Length then
                        sprintf "lines %d-%d of %d, up to date" tail.From tail.Through tail.Length
                    else
                        sprintf
                            "lines %d-%d of %d — read on with from: %d"
                            tail.From
                            tail.Through
                            tail.Length
                            tail.Through
                let omitted =
                    if tail.Elided > 0 then sprintf "\n[%d earlier characters omitted]" tail.Elided else ""
                // A timeout is an ANSWER, not an error: what was said while waiting is
                // usually where the reason it never arrived is written.
                let waited =
                    match tail.Matched, waitFor with
                    | Some false, Some wait ->
                        sprintf
                            "\n(%s did not appear within %gs — this is what it said instead)"
                            (TerminalMatch.describe wait.Until)
                            wait.TimeoutSeconds
                    | _ -> ""
                if tail.Text = "" then return sprintf "%s%s%s\n(nothing printed in these lines)" where omitted waited
                else return sprintf "%s%s%s\n%s" where omitted waited tail.Text
        }

    /// One file, one window of it, with the bounds said on every answer (`FileSlice.render`).
    /// The sandbox is asked for the whole file and the window is cut here — so the length is
    /// always known, and an `offset` past the end answers with how long the file is rather
    /// than with nothing.
    let private readFile
        (capabilities: AgentCapabilities)
        (path: string)
        (offset: int option)
        (limit: int option)
        (sandbox: string option)
        : Async<string> =
        let raw = sandbox |> Option.defaultValue (SandboxRef.render SandboxRef.defaultRef)
        withSandbox raw (fun name ->
            async {
                match! capabilities.Files.Read name path with
                | Ok content -> return FileSlice.render path (FileSlice.ofContent content offset limit)
                | Error reason -> return sprintf "could not read %s: %s" path reason
            })

    /// An edit is a command: the gate's answer is rendered by the one renderer every gated
    /// command shares, so a refusal reads as a refusal here too.
    let private editFile
        (capabilities: AgentCapabilities)
        (path: string)
        (oldText: string)
        (newText: string)
        (replaceAll: bool)
        (sandbox: string option)
        : Async<string> =
        let raw = sandbox |> Option.defaultValue (SandboxRef.render SandboxRef.defaultRef)
        withSandbox raw (fun name ->
            async {
                match!
                    capabilities.Files.Edit
                        { FileEditRequest.Sandbox = name
                          FileEditRequest.Path = path
                          FileEditRequest.OldText = oldText
                          FileEditRequest.NewText = newText
                          FileEditRequest.ReplaceAll = replaceAll }
                with
                | Ok outcome -> return renderCommandOutcome outcome
                | Error e -> return sprintf "could not edit %s: %s" path e
            })

    let private writeFile (capabilities: AgentCapabilities) (path: string) (content: string) (sandbox: string option) : Async<string> =
        let raw = sandbox |> Option.defaultValue (SandboxRef.render SandboxRef.defaultRef)
        withSandbox raw (fun name ->
            async {
                match! capabilities.Files.Write name path content with
                | Ok outcome -> return renderCommandOutcome outcome
                | Error e -> return sprintf "could not write %s: %s" path e
            })

    let private setSecret (capabilities: AgentCapabilities) (name: string) (value: string) : Async<string> =
        async {
            match SecretName.create name with
            | Error e -> return sprintf "invalid secret name: %s" e
            | Ok secretName ->
                match! capabilities.Secrets.Set secretName value with
                | Ok metadata ->
                    return
                        sprintf
                            "stored secret '%s' (updated %s)"
                            (SecretName.value metadata.Id.Name)
                            (metadata.UpdatedAt.ToString "o")
                | Error e -> return sprintf "could not store secret: %s" e
        }

    let private listSecrets (capabilities: AgentCapabilities) () : Async<string> =
        async {
            match! capabilities.Secrets.List () with
            | Error e -> return sprintf "could not list secrets: %s" e
            | Ok [] -> return "no secrets stored for this session"
            | Ok listed ->
                return
                    listed
                    |> List.map (fun m -> sprintf "%s (updated %s)" (SecretName.value m.Id.Name) (m.UpdatedAt.ToString "o"))
                    |> String.concat "\n"
        }

    let private deleteSecret (capabilities: AgentCapabilities) (name: string) : Async<string> =
        async {
            match SecretName.create name with
            | Error e -> return sprintf "invalid secret name: %s" e
            | Ok secretName ->
                match! capabilities.Secrets.Delete secretName with
                | Ok true -> return sprintf "deleted secret '%s'" name
                | Ok false -> return sprintf "no secret named '%s'" name
                | Error e -> return sprintf "could not delete secret: %s" e
        }

    let private addRepo (capabilities: AgentCapabilities) (raw: string) : Async<string> =
        withRepo raw (fun repo ->
            async {
                match! capabilities.Repos.Add repo with
                | Ok outcome -> return renderCommandOutcome outcome
                | Error e -> return sprintf "could not add the repo: %s" e
            })

    let private removeRepo (capabilities: AgentCapabilities) (raw: string) (force: bool) : Async<string> =
        withRepo raw (fun repo ->
            async {
                match! capabilities.Repos.Remove repo force with
                | Ok outcome -> return renderCommandOutcome outcome
                | Error e -> return sprintf "could not remove the repo: %s" e
            })

    let private switchBranch (capabilities: AgentCapabilities) (raw: string) (branch: string) (create: bool) : Async<string> =
        withRepo raw (fun repo ->
            async {
                match! capabilities.Repos.SwitchBranch repo branch create with
                | Ok outcome -> return renderCommandOutcome outcome
                | Error e -> return sprintf "could not switch branch: %s" e
            })

    let private fetchRepo (capabilities: AgentCapabilities) (raw: string) : Async<string> =
        withRepo raw (fun repo ->
            async {
                match! capabilities.Repos.Fetch repo with
                | Ok said -> return said
                | Error e -> return sprintf "could not fetch: %s" e
            })

    let private inspectRepo (inspect: InspectRepo) (raw: string) : Async<string> =
        withRepo raw (fun repo ->
            async {
                match! inspect repo with
                | Ok said -> return said
                | Error e -> return sprintf "could not inspect the repo: %s" e
            })

    /// The named-WorkSandbox bodies (Plan 15, stage 2).
    let private startWorkSandbox (capabilities: AgentCapabilities) (raw: string) (forward: string list) : Async<string> =
        withSandbox raw (fun name ->
            async {
                // The tool's vocabulary is a name and some credentials; everything else a
                // sandbox can be is the file's to say, so the declaration is an empty one
                // with the forwarding filled in.
                match! capabilities.Sandboxes.Start name { SandboxDecl.empty with Forward = forward } with
                | Ok outcome -> return renderCommandOutcome outcome
                | Error e -> return sprintf "could not start the sandbox: %s" e
            })

    let private stopWorkSandbox (capabilities: AgentCapabilities) (raw: string) : Async<string> =
        withSandbox raw (fun name ->
            async {
                match! capabilities.Sandboxes.Stop name with
                | Ok outcome -> return renderCommandOutcome outcome
                | Error e -> return sprintf "could not stop the sandbox: %s" e
            })

    /// Where terminals opened from now on start (Plan 25). The sandbox defaults, so the
    /// common call is one argument.
    let private setShellProfile (capabilities: AgentCapabilities) (sandbox: string option) (cwd: string option) : Async<string> =
        let raw = sandbox |> Option.defaultValue (SandboxRef.render SandboxRef.defaultRef)
        // An empty string is the CLEAR, like an absent one: a model that computed a path and
        // got nothing must not be told it set the profile to "".
        let cwd = cwd |> Option.map (fun path -> path.Trim ()) |> Option.filter (fun path -> path <> "")
        withSandbox raw (fun name ->
            async {
                match! capabilities.Sandboxes.SetShellProfile name cwd with
                | Ok outcome -> return renderCommandOutcome outcome
                | Error e -> return sprintf "could not set the shell profile: %s" e
            })

    let private readQuery (capabilities: AgentCapabilities) (def: QueryDef) () : Async<string> =
        async {
            match! capabilities.Queries.Read def.Name with
            | Ok value -> return QueryValue.describe def.Shape value
            | Error e -> return sprintf "could not read %s: %s" (QueryName.value def.Name) e
        }

    // ---------------------------------------------------------------------------------
    // The descriptors, paired with their bodies. One list: a tool cannot be declared
    // without being callable, and cannot be callable without being declared.
    // ---------------------------------------------------------------------------------

    let private repoArg = [ ToolField.required "repo" "string" "owner/name" ]

    /// The verbs: everything that is written out rather than generated.
    let private verbs (capabilities: AgentCapabilities) : (ToolDescriptor * (string -> Async<Result<ToolAnswer, string>>)) list =
        let tool name description fields body : ToolDescriptor * (string -> Async<Result<ToolAnswer, string>>) =
            ToolDescriptor.create Namespace name description (ToolSchema.ofFields fields), body
        let ofRepo (run: string -> Async<string>) =
            fun args ->
                async {
                    match ToolArgs.string "repo" args with
                    | Error e -> return Error e
                    | Ok raw -> return! ok (run raw)
                }
        [ tool
            "execute_command"
            "Run a shell command in a session terminal — the only way to run anything, seen by everyone and on the record. `sandbox`: a named work sandbox (start_work_sandbox); omit for the default one. `terminal`: a terminal from open_terminal, to run beside something long (each terminal runs one command at a time). For anything long-running pass background: true — it returns a handle at once, you end your turn, and you're woken when it finishes; otherwise it waits and hands back a check_pending handle if the command outlasts the wait. No stdin unless stdin: true (readers get EOF), so pass flags, not prompts. Scratch under $TMPDIR; /tmp is denied. Rewriting a file, write the new content before deleting the old — a delete-then-write can be refused halfway. Read the answer: it says which happened."
            [ ToolField.required "command" "string" "the shell command line to run, e.g. \"npm test -- --watch=false\""
              ToolField.optional "terminal" "string" "the id of a terminal to run in, as open_terminal or list_terminals gave it; omit for your own terminal in the sandbox"
              ToolField.optional "sandbox" "string" "the work sandbox to run in — the session's own by name (\"test\"), a repo's as \"owner/repo:name\" (its bare name also finds it when only one repo declares that name); omit for the default one"
              ToolField.optional
                  "background"
                  "boolean"
                  "true to start it and carry on without waiting — use it for long work, and for work that can run alongside other work. You will be told when it finishes."
              ToolField.optional
                  "stdin"
                  "boolean"
                  "true to let the command read the terminal's input, for the rare command that has to prompt; omit it and anything that reads stdin gets end-of-file at once" ]
            (fun args ->
                async {
                    match ToolArgs.commandWhere args with
                    | Error e -> return Error e
                    | Ok (command, terminal, sandbox, background, stdin) ->
                        return! answered (executeCommand capabilities command terminal sandbox background stdin)
                })

          // The terminal verbs a person already has (Plan 20, stage 3). Deliberately the same
          // three a human uses from the list — open, close, see what there is — because the
          // agent and the people in the session are working on ONE set of terminals, and a
          // second vocabulary for the agent's would make its terminals a different kind of
          // thing on every surface that draws them.
          tool
              "open_terminal"
              "Open your own terminal and name it for the job (\"tests\", \"docs build\") — everyone reads the name. Use it to run several things at once, since each terminal runs one command at a time. Returns a terminal id; pass it to execute_command as `terminal`. There's a per-sandbox limit — if you've hit it, this says so, and close_terminal makes room."
              [ ToolField.required "name" "string" "what this terminal is for, e.g. \"tests\""
                ToolField.optional "sandbox" "string" "the work sandbox to open it in — \"owner/repo:name\" for a repo's, or its bare name when only one repo declares it; omit for the default one" ]
              (fun args ->
                  async {
                      match ToolArgs.nameSandbox args with
                      | Error e -> return Error e
                      | Ok (name, sandbox) ->
                          let resolved =
                              match sandbox with
                              | None -> Ok None
                              | Some s -> SandboxRef.parse s |> Result.map Some
                          match resolved with
                          | Error e -> return ToolAnswer.text (sprintf "not a sandbox: %s" e) |> Ok
                          | Ok target ->
                              match! capabilities.Terminals.Open name target with
                              | Ok id ->
                                  return
                                      Ok (ToolAnswer.text (sprintf "opened terminal %s for %s" (TerminalId.value id) name))
                              | Error reason -> return Ok (ToolAnswer.text reason)
                  })

          tool
              "close_terminal"
              "Close a terminal you opened, when you're done with it. Its recording stays for anyone to read; only the shell ends. You can close only your own; the people here can close any, including yours."
              [ ToolField.required "terminal" "string" "the terminal id from open_terminal" ]
              (fun args ->
                  async {
                      match ToolArgs.string "terminal" args with
                      | Error e -> return Error e
                      | Ok raw ->
                          match TerminalId.create raw with
                          | Error e -> return Ok (ToolAnswer.text (sprintf "not a terminal id: %s" e))
                          | Ok id ->
                              match! capabilities.Terminals.Close id with
                              | Ok () -> return Ok (ToolAnswer.text (sprintf "closed terminal %s" raw))
                              | Error reason -> return Ok (ToolAnswer.text reason)
                  })

          tool
              "list_terminals"
              "See every terminal open in this session — yours and the people's — what each is for, and whether something is running. Use it to see what you have before opening another, and to pick one to run in."
              []
              (fun _ ->
                  async {
                      match! capabilities.Terminals.List () with
                      | Error reason -> return Ok (ToolAnswer.text reason)
                      | Ok [] -> return Ok (ToolAnswer.text "no terminals are open")
                      | Ok terminals ->
                          let line (t: TerminalSummary) =
                              sprintf
                                  "%s — %s%s%s%s"
                                  (TerminalId.value t.Terminal)
                                  t.Name
                                  (match t.Sandbox with
                                   | Some s when s <> SandboxRef.defaultRef -> sprintf " in %s" (SandboxRef.render s)
                                   | _ -> "")
                                  (if t.Mine then " (yours)" else "")
                                  (if t.Busy then " — running something" else "")
                          return Ok (ToolAnswer.text (terminals |> List.map line |> String.concat "\n"))
                  })

          tool
              "check_pending"
              "Pick up whatever a handle named — a long build, a command queued behind a busy terminal, a program waiting on a keystroke. Works for execute_command and anything that said it was still going; returns what the original call would have. A background command needs no polling: end your turn and you're woken when it finishes."
              [ ToolField.required "handle" "string" "the handle from execute_command, or from a command that said it was still going" ]
              (fun args ->
                  async {
                      match ToolArgs.string "handle" args with
                      | Error e -> return Error e
                      | Ok handle -> return! answered (checkPending capabilities handle)
                  })

          tool
              "write_terminal"
              "Type into a terminal you hold the keyboard for: one streaming something live (a device or console — bytes from outside this session), or one running a command of yours (a full-screen program waiting on a key, a stdin: true prompt, or something stuck — \"\\u0003\" interrupts, \"\\u0004\" is EOF). Send exactly the bytes you mean, including \"\\r\" for a newline. On a live stream, typing takes the terminal — everyone sees it and can take it back. On a shell terminal it works only while a command of yours runs there; otherwise use execute_command."
              [ ToolField.required "terminal" "string" "the terminal id, from the terminal that was opened for the stream"
                ToolField.required "data" "string" "the bytes to type, e.g. \"AT\\r\"" ]
              (fun args ->
                  async {
                      match ToolArgs.two "terminal" "data" args with
                      | Error e -> return Error e
                      | Ok (terminal, data) ->
                          match TerminalId.create terminal with
                          | Error e -> return Error (sprintf "not a terminal id: %s" e)
                          | Ok id -> return! ok (writeTerminal capabilities id data)
                  })

          tool
              "read_terminal"
              "Read what a terminal has said, optionally waiting for it to say something. Use it when the answer doesn't come back as a command's output — a live stream, or a shell terminal where a full-screen program waits on a key. `wait_for` holds the read until that text appears (on timeout it answers with what was said, usually the reason it didn't). No `from`: the tail now, capped, saying what it left out. `from`: a page from that line, plus the line to carry into the next call — how you read what was said before you arrived. Every answer says which lines it covers of how many. Reading takes nothing — whoever's typing keeps the terminal. On a shell terminal the tail is yours while a command of yours runs, and `wait_for_pattern` waits for a line of output without polling. Between commands the tail is refused (that output comes back from execute_command), but `from` still pages it."
              [ ToolField.required "terminal" "string" "the terminal id, from the terminal that was opened for the stream"
                ToolField.optional
                    "from"
                    "integer"
                    "the line to read forward from, as reported by a previous read; omit for the tail"
                ToolField.optional
                    "wait_for"
                    "string"
                    "hold the read until this exact text appears, e.g. \"login: \". Literal text — for a pattern use wait_for_pattern"
                ToolField.optional
                    "wait_for_pattern"
                    "string"
                    "hold the read until output matches this pattern, e.g. \"[#$>] $\" for a shell prompt. Takes literal text, `.`, `[classes]`, `*`, `+`, `?`, `|`, groups, and `^`/`$` anchored to a LINE. No backreferences, lookaround or non-greedy quantifiers"
                ToolField.optional
                    "timeout_seconds"
                    "number"
                    "how long to hold before answering with what was said instead; default 10" ]
              (fun args ->
                  async {
                      match ToolArgs.terminalRead args with
                      | Error e -> return Error e
                      | Ok (terminal, from, waitFor) ->
                          match TerminalId.create terminal with
                          | Error e -> return Error (sprintf "not a terminal id: %s" e)
                          | Ok id -> return! ok (readTerminal capabilities id from waitFor)
                  })

          // Read-only, and says so in the descriptor: the one tool here that touches a file
          // and is not an act. Its call is still on the record — the tool-use chip says which
          // file, which is the whole point of it over `sed -n` in a terminal.
          (let descriptor, body =
              tool
                  "read_file"
                  "Read a file, or a window of it, numbered by line. Prefer this over cat/sed/head/tail in execute_command: it's on the record as a read of THIS file, and the people here see what you looked at. Paths are as a terminal in that sandbox would take them — relative to where its terminals start (the checkout, once add_repo and set_shell_profile have run), or absolute. Every answer says which lines it covers of how many; a long file comes back a page at a time, and the answer says which `offset` reads on. Lines longer than 2000 characters are cut."
                  [ ToolField.required "path" "string" "the file, e.g. \"src/Program.fs\" or \"repos/octocat/hello-world/README.md\""
                    ToolField.optional "offset" "integer" "the first line to read, 1-based; omit for the top"
                    ToolField.optional "limit" "integer" "how many lines; omit for 2000"
                    ToolField.optional "sandbox" "string" "the work sandbox whose files these are; omit for the default one" ]
                  (fun args ->
                      async {
                          match ToolArgs.fileRead args with
                          | Error e -> return Error e
                          | Ok (path, offset, limit, sandbox) -> return! ok (readFile capabilities path offset limit sandbox)
                      })
           { descriptor with ReadOnly = true }, body)

          tool
              "edit_file"
              "Replace one exact piece of text in a file with another. Prefer this over sed/awk/heredocs in execute_command: the change is on the record as an edit of THIS file, and the people here see what changed. Read the file first (read_file) and quote `old_string` exactly as it appears — whitespace and indentation included, without the line numbers. It must match ONCE: if it matches more, add surrounding lines until it is unique, or pass replace_all: true to change every occurrence. Answers with what changed, or why nothing did. Paths as read_file takes them."
              [ ToolField.required "path" "string" "the file to edit, as read_file names it"
                ToolField.required "old_string" "string" "the exact text to replace, as it appears in the file"
                ToolField.required "new_string" "string" "the text to put in its place"
                ToolField.optional "replace_all" "boolean" "true to replace every occurrence; default false, which requires exactly one"
                ToolField.optional "sandbox" "string" "the work sandbox whose file this is; omit for the default one" ]
              (fun args ->
                  async {
                      match ToolArgs.fileEdit args with
                      | Error e -> return Error e
                      | Ok (path, oldText, newText, replaceAll, sandbox) ->
                          return! ok (editFile capabilities path oldText newText replaceAll sandbox)
                  })

          tool
              "write_file"
              "Write a whole file: create it, or replace everything in it. For a change inside an existing file use edit_file, which records what changed; this records that the file was written. Directories on the way are created. Paths as read_file takes them."
              [ ToolField.required "path" "string" "the file to write, as read_file names it"
                ToolField.required "content" "string" "the entire new content of the file"
                ToolField.optional "sandbox" "string" "the work sandbox whose file this is; omit for the default one" ]
              (fun args ->
                  async {
                      match ToolArgs.fileWrite args with
                      | Error e -> return Error e
                      | Ok (path, content, sandbox) -> return! ok (writeFile capabilities path content sandbox)
                  })

          tool
              "set_secret"
              "Store a named secret for this session (WRITE-ONLY: no tool reads it back). To USE it, reference its name as an environment-variable secret ref when an environment starts — the value is injected there and never appears in the conversation."
              [ ToolField.required "name" "string" "the secret name, e.g. DEPLOY_TOKEN"
                // The one argument in the repo that must never be recorded, and it says so
                // in the schema rather than in a list somebody has to remember to update.
                ToolField.secret "value" "the secret value to store" ]
              (fun args ->
                  async {
                      match ToolArgs.two "name" "value" args with
                      | Error e -> return Error e
                      | Ok (name, value) -> return! ok (setSecret capabilities name value)
                  })

          tool
              "list_secrets"
              "List this session's stored secret names and timestamps. Never values."
              []
              (fun _ -> ok (listSecrets capabilities ()))

          tool
              "delete_secret"
              "Delete one of this session's stored secrets by name."
              [ ToolField.required "name" "string" "the secret name to delete" ]
              (fun args ->
                  async {
                      match ToolArgs.string "name" args with
                      | Error e -> return Error e
                      | Ok name -> return! ok (deleteSecret capabilities name)
                  })

          tool
              "add_repo"
              "Clone a GitHub repo into this session's shared repos directory (visible to everyone here and inside the work environment). Takes owner/repo, never a URL, and only repos the session's GitHub credential can reach — GitHub says \"not found\" for one it won't show you, so not-found on a repo that exists means nobody has connected GitHub here; say that rather than retrying. Answers with the checkout path as a terminal here reaches it (usually relative to where a terminal starts): use it as given for cd and set_shell_profile, don't rebuild it. Read-only bootstrap — commit or push with execute_command. An already-added repo just reports its state."
              [ ToolField.required "repo" "string" "the repo as owner/name, e.g. \"octocat/hello-world\"" ]
              (ofRepo (addRepo capabilities))

          tool
              "remove_repo"
              "Delete a repo's checkout from this session — everyone here sees it leave the repos list. Use it when add_repo says a checkout is unreadable, or when the session is done with a repo. A checkout with uncommitted changes is REFUSED unless you pass `force`: removing deletes that work, and re-adding brings back the commits and nothing else — read the refusal and decide, don't force by reflex. Terminals set to start in the checkout go back to the sandbox default, and the answer says so. add_repo is the way back."
              [ ToolField.required "repo" "string" "the repo as owner/name, e.g. \"octocat/hello-world\""
                ToolField.optional "force" "boolean" "true to delete uncommitted changes along with the checkout" ]
              (fun args ->
                  async {
                      match ToolArgs.repoForce args with
                      | Error e -> return Error e
                      | Ok (repo, force) -> return! ok (removeRepo capabilities repo force)
                  })

          tool
              "switch_branch"
              "Switch a repo's checkout to a branch (optionally creating it). Local only — never touches the remote. Everyone in the session sees the switch in the timeline."
              [ ToolField.required "repo" "string" "owner/name"
                ToolField.required "branch" "string" "the branch to switch to"
                ToolField.optional "create" "boolean" "create the branch (like switch -c)" ]
              (fun args ->
                  async {
                      match ToolArgs.repoBranchCreate args with
                      | Error e -> return Error e
                      | Ok (repo, branch, create) -> return! ok (switchBranch capabilities repo branch create)
                  })
          tool
              "fetch_repo"
              "Fetch a repo's remote refs (prune, no submodules). Use before switching to a branch that only exists on the remote."
              repoArg
              (ofRepo (fetchRepo capabilities))

          tool "repo_status" "A repo checkout's git status (porcelain, with branch header)." repoArg
              (ofRepo (inspectRepo capabilities.Repos.Status))

          tool "repo_log" "The last 30 commits of a repo checkout, one line each." repoArg
              (ofRepo (inspectRepo capabilities.Repos.Log))

          tool "repo_diff" "The uncommitted diff of a repo checkout (capped; use a terminal for the full thing)." repoArg
              (ofRepo (inspectRepo capabilities.Repos.Diff))

          tool
              "start_work_sandbox"
              "Ensure a named work sandbox exists for this session, and return it. Same name and same forwarding returns the running one unchanged — safe to call every time. Same name with DIFFERENT forwarding is refused, not silently recreated (that kills what's running — stop_work_sandbox first). `forward` names credentials to put inside (\"github\" is what lets git push from a terminal there); it uses the credentials of whoever's turn this is, and everyone sees which were forwarded and whose."
              [ ToolField.required "name" "string" "the sandbox name, e.g. \"default\" or \"test\"; a repo's is \"owner/repo:name\""
                ToolField.optionalList "forward" "string" "credential names to forward, e.g. [\"github\"]" ]
              (fun args ->
                  async {
                      match ToolArgs.nameForward args with
                      | Error e -> return Error e
                      | Ok (name, forward) -> return! ok (startWorkSandbox capabilities name forward)
                  })

          tool
              "stop_work_sandbox"
              "Stop a named work sandbox, killing anything running in it. The way to change what a sandbox forwards: stop it, then start it again."
              [ ToolField.required "name" "string" "the sandbox name" ]
              (fun args ->
                  async {
                      match ToolArgs.string "name" args with
                      | Error e -> return Error e
                      | Ok name -> return! ok (stopWorkSandbox capabilities name)
                  })

          tool
              "set_shell_profile"
              "Say where terminals opened from now on start. Use it once after add_repo, with the path add_repo gave you, so you stop putting `cd` in front of every command — every terminal opened afterwards starts there, yours and the people's, and it survives a restart. It takes a DIRECTORY, not a script (execute_command is still the only way to run anything), and the directory must already exist in that sandbox — this checks and says so rather than opening a terminal nowhere. A path from add_repo or the repos query goes in as given. Omit `cwd` to clear it. Terminals already open keep their directory, except the one your plain execute_command uses, which is reopened for you."
              [ ToolField.optional
                    "cwd"
                    "string"
                    "a directory the sandbox has, said the way add_repo and the repos query say it, e.g. \"repos/octocat/hello-world\"; omit it to clear the profile"
                ToolField.optional "sandbox" "string" "the work sandbox this is about; omit for the default one" ]
              (fun args ->
                  async {
                      match ToolArgs.cwdSandbox args with
                      | Error e -> return Error e
                      | Ok (cwd, sandbox) -> return! ok (setShellProfile capabilities sandbox cwd)
                  }) ]

    /// The session's QUERIES (Plan 15), generated from the registry rather than written out
    /// one by one: declaring a query is what puts it in front of the agent, and in front of
    /// the humans, with no third place to keep in step. `readOnlyHint` is what makes one a
    /// query rather than a command, and it is MCP's own marker, not ours.
    let private queryTools (capabilities: AgentCapabilities) : (ToolDescriptor * (string -> Async<Result<ToolAnswer, string>>)) list =
        capabilities.Queries.Declared
        |> List.map (fun def ->
            { ToolDescriptor.create Namespace (QueryName.value def.Name) (QueryDef.toolDescription def) ToolSchema.none with
                ReadOnly = true
                Title = Some def.Title },
            (fun _ -> ok (readQuery capabilities def ())))

    /// Every tool of the `yession` namespace: its descriptor, and the body that answers a
    /// call to it. One list -- a tool cannot be declared without being callable, and cannot
    /// be callable without being declared. `Repos.ProviderTools` is where a provider (today
    /// only `GitHubPrs.fs`) contributes its own -- `create_pr`, `watch_pr`, `unwatch_pr`
    /// today -- without this module knowing what provider, or how many, filled it in.
    let private declared (capabilities: AgentCapabilities) =
        verbs capabilities @ queryTools capabilities @ capabilities.Repos.ProviderTools

    /// The `yession` registry for one turn's capabilities.
    let registry (capabilities: AgentCapabilities) : ToolRegistry =
        let declared = declared capabilities
        ToolRegistry.ofNamespace
            Namespace
            (declared |> List.map fst)
            (fun call ->
                match declared |> List.tryFind (fun (descriptor, _) -> descriptor.Name = call.Name) with
                | Some (_, body) -> body call.Arguments
                | None -> async { return Error (sprintf "no tool '%s/%s'" call.Namespace call.Name) })
