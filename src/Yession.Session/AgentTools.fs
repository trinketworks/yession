namespace Yession.Session

open Yession.Domain
open Yession.Domain.Agent
open Yession.Domain.Sandboxes

// The session's own tools, as a registry (Plan 16, part A).
//
// This is the `yession` namespace: the verbs a turn has always had, moved off the
// runner's parameter list and onto a value. They live in the session rather than beside the
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
open Yession.Domain.Content

#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

module AgentTools =

    /// The namespace the session's own tools live in. One string, referenced everywhere,
    /// because it is simultaneously the SDK server name, the wire-name segment and what the
    /// tool-use record shows.
    [<Literal>]
    let Namespace = "yession"

    /// The names of the tools the system prompt steers by. One spelling for the descriptor
    /// and the prompt, so renaming a tool cannot leave the prompt naming one that is gone.
    module ToolName =
        [<Literal>]
        let ExecuteCommand = "execute_command"
        [<Literal>]
        let ReadFile = "read_file"
        [<Literal>]
        let EditFile = "edit_file"
        [<Literal>]
        let WriteFile = "write_file"
        [<Literal>]
        let StartWorkSandbox = "start_work_sandbox"
        [<Literal>]
        let ShareArtifact = "share_artifact"

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
        | TerminalCommandEnded reason ->
            sprintf
                "ENDED in %s with no exit status on record: %s. It is not running; whether it succeeded is not known — read its output.%s"
                where reason output
        | TerminalCommandRunning ->
            // How it has been doing, when there is a block to measure: the clause that tells a
            // build halfway through from a wait loop whose server died at launch.
            let activity =
                match outcome.Activity with
                | Some activity -> sprintf " (%s)" (BlockActivity.describe activity)
                | None -> ""
            sprintf
                "STILL RUNNING in %s%s. Not finished; nothing cancelled. For long commands pass background: true and end your turn — you're woken when they finish; otherwise check_pending '%s' for the outcome. Stuck or waiting on input? write_terminal can type in — keys: [\"ctrl-c\"] interrupts.%s"
                where activity handle output
        | TerminalCommandSaid waitedFor ->
            let activity =
                match outcome.Activity with
                | Some activity -> sprintf " (%s)" (BlockActivity.describe activity)
                | None -> ""
            sprintf
                "SAID %s in %s%s — what you were waiting for. It is still running; check_pending '%s' for the rest of it.%s"
                waitedFor where activity handle output
        | TerminalCommandStarted ->
            sprintf
                "STARTED in %s, in the background. Carry on, or end your turn — you're woken when it finishes; check_pending '%s' picks it up sooner, and with wait_for it answers once the command says that (a ready line, a prompt) or ends.%s"
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
                "WAITING FOR %s — another command runs there and this one is queued behind it; it has NOT run. To run it now beside that: open_terminal, then execute_command with that `terminal`. To end the running one: if it's yours, write_terminal keys: [\"ctrl-c\"] into that terminal; if the terminal is yours, close_terminal ends everything in it. Otherwise check_pending '%s' later."
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

    /// What a call is waiting FOR, from the three fields every waiting tool takes alike
    /// (`read_terminal`, `check_pending`). Absent `wait_for` is a call that does not wait. A
    /// `timeout_seconds` without one is nothing to bound, so the wait is only assembled when
    /// there is something to wait FOR.
    let private waitFor (literal: string) (pattern: string) (timeout: string) (otherwise: float) : ToolArgs<Result<TerminalWait option, string>> =
        toolArgs {
            let! literal = ToolArgs.textOption "wait_for" literal
            and! pattern = ToolArgs.textOption "wait_for_pattern" pattern
            and! timeout = ToolArgs.number "timeout_seconds" timeout otherwise
            return
                // Two fields rather than one and a mode flag, because a flag makes the meaning
                // of `wait_for` depend on another argument: a caller that sets one and forgets
                // the other gets a LITERAL match on a regex string, which is wrong, silent, and
                // looks exactly like a device that never answered. Two fields make that a
                // refusal.
                match literal, pattern with
                | Some _, Some _ ->
                    Error "wait_for and wait_for_pattern are two ways to say the same thing — give one"
                | None, None -> Ok None
                | Some literal, None -> Ok (Some { Until = MatchLiteral literal; TimeoutSeconds = timeout })
                | None, Some source ->
                    // Compiled HERE, so a pattern outside the subset is an answer to this call
                    // rather than something discovered part-way through a wait that then has
                    // to explain itself.
                    Pattern.compile source
                    |> Result.map (fun compiled -> Some { Until = MatchPattern (compiled, source); TimeoutSeconds = timeout })
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
        // What the command is for, as the model said it. Bounded and made one line here, at
        // the tool — the model's prose is untrusted, and what reaches the request is what
        // goes into the log.
        (description: string option)
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
                let request =
                    { Command = command
                      Target = target
                      Background = background
                      Stdin = stdin
                      Description = description |> Option.bind BlockDescription.ofProse }
                match! capabilities.Terminals.Execute request with
                | Ok outcome -> return { Text = renderOutcome outcome; Block = outcome.Block; Stream = None; Image = None }
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
    let private checkPending (capabilities: AgentCapabilities) (handle: string) (until: TerminalWait option) : Async<ToolAnswer> =
        async {
            match QueueId.create handle with
            | Error e -> return ToolAnswer.text (sprintf "not a command handle: %s" e)
            | Ok handle ->
                match! capabilities.Terminals.CheckPending handle until with
                | Ok (PendingTerminal outcome) ->
                    // A wait that ran out says so, beside the status that would otherwise read
                    // the same as a plain look: what was waited for did not come.
                    let missed =
                        match until, outcome.Status with
                        | Some wait, TerminalCommandRunning ->
                            sprintf "\n(%s did not appear within %gs)" (TerminalMatch.describe wait.Until) wait.TimeoutSeconds
                        | _ -> ""
                    return { Text = renderOutcome outcome + missed; Block = outcome.Block; Stream = None; Image = None }
                | Ok (PendingCommand outcome) -> return ToolAnswer.text (renderCommandOutcome outcome)
                | Error reason -> return ToolAnswer.text (sprintf "could not read that command: %s" reason)
        }

    /// Type into a live-only terminal (Plan 19). A refusal is an ANSWER rather than an error,
    /// like every other tool here: the model is meant to read "use execute_command there" and
    /// do that, not to see a protocol failure and try a third thing.
    let private writeTerminal (capabilities: AgentCapabilities) (id: TerminalId) (data: string) : Async<string> =
        async {
            match! capabilities.Terminals.Write id data with
            // What went in, said back by key name — so six characters that spell a key read as
            // six characters, at the one moment the caller can still do something about it.
            | Ok answer ->
                let spelled =
                    if TerminalKeys.spelledOut data then
                        " — that is text that SPELLS a key, and the terminal took it as text; to press one, pass it by name in keys, e.g. keys: [\"ctrl-c\"]"
                    else ""
                return sprintf "%s; typed %s%s" answer (TerminalKeys.describe data) spelled
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
        : Async<ToolAnswer> =
        let raw = sandbox |> Option.defaultValue (SandboxRef.render SandboxRef.defaultRef)
        async {
            match SandboxRef.parse raw with
            | Error e -> return ToolAnswer.text (sprintf "not a sandbox: %s" e)
            | Ok name ->
                match! capabilities.Files.Read name path with
                | Ok (FileContent.Text content) -> return ToolAnswer.text (FileSlice.render path (FileSlice.ofContent content offset limit))
                // The picture rides beside the text; the text is what the record keeps, so it
                // says what was looked at and never carries the bytes.
                //
                // And it says who can see it, because the answer is the only place that fact
                // arrives in time. An agent that reads a file it cannot see is told so by the
                // error; an agent shown a picture has no such signal — the model sees the
                // image, the session's own transcript holds this sentence, and the people here
                // see neither. One did exactly that: it took four screenshots of a layout it
                // had just restyled, looked at them, deleted the script and reported the work
                // "verified with real screenshots", with nothing to look at. `share_artifact`
                // says how to share; this is where the agent learns it has to. A picture read
                // back is never one already shared, since this verb reads a sandbox and
                // artifacts are not in one, so the sentence has no second case.
                | Ok (FileContent.Image image) ->
                    return
                        { ToolAnswer.text (
                              sprintf
                                  "%s — a picture (%s, %d kB), shown to you and to nobody else: the people here see a picture only once share_artifact has copied it, so a reply that rests on this one comes with it"
                                  path
                                  image.Type
                                  (ToolImage.kilobytes image)
                          ) with
                            Image = Some image }
                | Error reason -> return ToolAnswer.text (sprintf "could not read %s: %s" path reason)
        }

    /// One artifact, by the address everyone here already holds. `readFile`'s sibling, and
    /// separate from it because a sandbox path and a content address are two address spaces:
    /// one verb over both would have to guess which it was handed, and would guess wrong for
    /// a checkout that has an `artifacts/` directory of its own.
    ///
    /// The picture's sentence says the OPPOSITE of `readFile`'s, and that is the mechanism
    /// rather than a nicety: between the two answers an agent can tell a screenshot only it
    /// has seen from one the people here are looking at, without the rule being written
    /// anywhere it has to remember.
    let private readArtifact
        (capabilities: AgentCapabilities)
        (raw: string)
        (offset: int option)
        (limit: int option)
        : Async<ToolAnswer> =
        async {
            match ContentRef.create raw with
            | Error why -> return ToolAnswer.text why
            | Ok ref ->
                let said = ContentRef.url ref
                match! capabilities.Artifacts.Read ref with
                | Ok (FileContent.Text content) ->
                    return ToolAnswer.text (FileSlice.render said (FileSlice.ofContent content offset limit))
                | Ok (FileContent.Image image) ->
                    return
                        { ToolAnswer.text (
                              sprintf
                                  "%s — a picture (%s, %d kB), shown to you; it is shared, so the people here can see it already"
                                  said
                                  image.Type
                                  (ToolImage.kilobytes image)
                          ) with
                            Image = Some image }
                | Error reason -> return ToolAnswer.text (sprintf "could not read %s: %s" said reason)
        }

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

    /// Sharing is a command like a write, and reads like one: the gate's renderer says what
    /// happened, and the dispatch's own sentence carries the address the version got — which
    /// is the one thing the caller cannot work out, because it did not choose it.
    let private shareArtifact
        (capabilities: AgentCapabilities)
        (path: string)
        (name: string option)
        (sandbox: string option)
        : Async<string> =
        let raw = sandbox |> Option.defaultValue (SandboxRef.render SandboxRef.defaultRef)
        withSandbox raw (fun sandbox ->
            async {
                match! capabilities.Artifacts.Share sandbox path name with
                | Ok outcome -> return renderCommandOutcome outcome
                | Error e -> return sprintf "could not share %s: %s" path e
            })

    let private searchFiles
        (capabilities: AgentCapabilities)
        (pattern: string)
        (path: string option)
        (glob: string option)
        (sandbox: string option)
        : Async<string> =
        let raw = sandbox |> Option.defaultValue (SandboxRef.render SandboxRef.defaultRef)
        withSandbox raw (fun name ->
            async {
                match! capabilities.Files.Search name pattern path glob with
                | Ok hits -> return FileHits.render (sprintf "nothing matches %s" pattern) hits
                | Error reason -> return sprintf "could not search: %s" reason
            })

    let private findFiles (capabilities: AgentCapabilities) (glob: string) (path: string option) (sandbox: string option) : Async<string> =
        let raw = sandbox |> Option.defaultValue (SandboxRef.render SandboxRef.defaultRef)
        withSandbox raw (fun name ->
            async {
                match! capabilities.Files.Find name glob path with
                | Ok hits -> return FileHits.render (sprintf "no file is named like %s" glob) hits
                | Error reason -> return sprintf "could not look: %s" reason
            })

    /// Opening, and the asking-to-be-shown that rides it. The answer says what was ASKED
    /// rather than what each person ended up looking at: a pin lives in one browser and this
    /// side cannot see one, so a sentence claiming the tab is now on everybody's screen would
    /// be a sentence that is sometimes false.
    let private openTab (capabilities: AgentCapabilities) (address: string) (focus: bool) : Async<string> =
        async {
            match ViewRef.read address with
            | Error reason -> return reason
            | Ok view ->
                match! capabilities.Tabs.Open view focus with
                | Error reason -> return sprintf "could not open %s: %s" (ViewRef.said view) reason
                | Ok () ->
                    return
                        if focus then sprintf "showing %s" (ViewRef.said view)
                        else sprintf "opened %s" (ViewRef.said view)
        }

    let private closeTab (capabilities: AgentCapabilities) (address: string) : Async<string> =
        async {
            match ViewRef.read address with
            | Error reason -> return reason
            | Ok view ->
                match! capabilities.Tabs.Close view with
                | Error reason -> return sprintf "could not close %s: %s" (ViewRef.said view) reason
                | Ok () -> return sprintf "closed %s" (ViewRef.said view)
        }

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
    let private startWorkSandbox (capabilities: AgentCapabilities) (raw: string) : Async<string> =
        withSandbox raw (fun name ->
            async {
                // The tool's vocabulary is a name; everything a sandbox can be is its
                // declaration's to say, so the declaration asked with is an empty one.
                match! capabilities.Sandboxes.Start name SandboxDecl.empty with
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

    let private repo (description: string) : ToolArgs<string> = ToolArgs.text "repo" description

    /// One tool of this namespace: its descriptor, and the body that answers it. The body is
    /// handed its arguments already read, so a call whose arguments do not fit the schema is
    /// an `Error` here, before any body runs — for every tool of the namespace alike, a
    /// provider's (`Repos.ProviderTools`) included.
    let tool
        (name: string)
        (description: string)
        (args: ToolArgs<'a>)
        (body: 'a -> Async<Result<ToolAnswer, string>>)
        : ToolDescriptor * (string -> Async<Result<ToolAnswer, string>>) =
        ToolDescriptor.create Namespace name description (ToolArgs.schema args),
        fun json ->
            async {
                match ToolArgs.read args json with
                | Error e -> return Error e
                | Ok read -> return! body read
            }

    /// For the arguments that can be refused once read: a body only runs on what passed.
    let whenValid (body: 'a -> Async<Result<ToolAnswer, string>>) (read: Result<'a, string>) : Async<Result<ToolAnswer, string>> =
        match read with
        | Error e -> async { return Error e }
        | Ok read -> body read

    /// The verbs: everything that is written out rather than generated.
    let private verbs (capabilities: AgentCapabilities) : (ToolDescriptor * (string -> Async<Result<ToolAnswer, string>>)) list =
        [ tool
            ToolName.ExecuteCommand
            "Run a shell command in a session terminal — the only way to run anything, seen by everyone and on the record. Always pass `description`: a few words saying what the command is for, which everyone sees as its title. Not for reading or editing files: use read_file and edit_file, which record WHICH file, unless a dedicated tool genuinely cannot do it — avoid cat, sed, awk, head, tail, grep -n and heredocs here. `sandbox`: a named work sandbox (start_work_sandbox); omit for the default one. `terminal`: a terminal from open_terminal, to run beside something long (each terminal runs one command at a time). For anything long-running pass background: true — it returns a handle at once, you end your turn, and you're woken when it finishes; otherwise it waits and hands back a check_pending handle if the command outlasts the wait. No stdin unless stdin: true (readers get EOF), so pass flags, not prompts. Scratch under $TMPDIR; /tmp is denied. Rewriting a file, write the new content before deleting the old — a delete-then-write can be refused halfway. Read the answer: it says which happened."
            (toolArgs {
                let! command = ToolArgs.text "command" "the shell command line to run, e.g. \"npm test -- --watch=false\""
                and! terminal = ToolArgs.textOption "terminal" "the id of a terminal to run in, as open_terminal or list_terminals gave it; omit for your own terminal in the sandbox"
                and! sandbox = ToolArgs.textOption "sandbox" "the work sandbox to run in — the session's own by name (\"test\"), a repo's as \"owner/repo:name\" (its bare name also finds it when only one repo declares that name); omit for the default one"
                and! background =
                    ToolArgs.flag
                        "background"
                        "true to start it and carry on without waiting — use it for long work, and for work that can run alongside other work. You will be told when it finishes."
                and! stdin =
                    ToolArgs.flag
                        "stdin"
                        "true to let the command read the terminal's input, for the rare command that has to prompt; omit it and anything that reads stdin gets end-of-file at once"
                and! description =
                    ToolArgs.textOption
                        "description"
                        "a few words saying what this command is for, as people will read it — \"Fetch master and show its tip\", \"Run the unit tests\". Everyone sees it as the command's title. Pass one on every call."
                return command, terminal, sandbox, background, stdin, description
             })
            (fun (command, terminal, sandbox, background, stdin, description) ->
                answered (executeCommand capabilities command terminal sandbox background stdin description))

          // The terminal verbs a person already has (Plan 20, stage 3). Deliberately the same
          // three a human uses from the list — open, close, see what there is — because the
          // agent and the people in the session are working on ONE set of terminals, and a
          // second vocabulary for the agent's would make its terminals a different kind of
          // thing on every surface that draws them.
          tool
              "open_terminal"
              "Open your own terminal and name it for the job (\"tests\", \"docs build\") — everyone reads the name. Use it to run several things at once, since each terminal runs one command at a time. Returns a terminal id; pass it to execute_command as `terminal`. There's a per-sandbox limit — if you've hit it, this says so, and close_terminal makes room."
              (toolArgs {
                  let! name = ToolArgs.text "name" "what this terminal is for, e.g. \"tests\""
                  and! sandbox = ToolArgs.textOption "sandbox" "the work sandbox to open it in — \"owner/repo:name\" for a repo's, or its bare name when only one repo declares it; omit for the default one"
                  return name, sandbox
               })
              (fun (name, sandbox) ->
                  async {
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
              (ToolArgs.text "terminal" "the terminal id from open_terminal")
              (fun raw ->
                  async {
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
              ToolArgs.none
              (fun () ->
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
              "Pick up whatever a handle named — a long build, a command queued behind a busy terminal, a program waiting on a keystroke. Works for execute_command and anything that said it was still going; returns what the original call would have. A background command needs no polling: end your turn and you're woken when it finishes. To carry on once a running command has SAID something — a server's ready line, a prompt — give `wait_for`: it answers when the output says it, when the command ends (with its exit code and what it printed — a server that died before it was ready says so), or at `timeout_seconds`, whichever is first. Never write a shell loop that waits for a port or a line."
              (toolArgs {
                  let! handle = ToolArgs.text "handle" "the handle from execute_command, or from a command that said it was still going"
                  // The default timeout is the turn's own bound: a caller naming a ready line
                  // wants it as long as a command may take, not a device's ten seconds.
                  and! until =
                      waitFor
                          "answer once the command's output contains this exact text, e.g. \"listening on\". Literal text — for a pattern use wait_for_pattern"
                          "answer once the command's output matches this pattern — the same subset read_terminal takes"
                          "how long to wait for it before answering with what the command has said so far; default and most 120"
                          120.0
                  return until |> Result.map (fun until -> handle, until)
               })
              (whenValid (fun (handle, until) -> answered (checkPending capabilities handle until)))

          tool
              "write_terminal"
              "Type into a terminal you hold the keyboard for: one streaming something live (a device or console — bytes from outside this session), or one running a command of yours (a full-screen program waiting on a key, a stdin: true prompt, or something stuck — keys: [\"ctrl-c\"] interrupts, [\"ctrl-d\"] is end-of-input). Text goes in `data`; keys go BY NAME in `keys`, after the text — never written out as escapes, which arrive as the characters they spell. The answer says what was typed. On a live stream, typing takes the terminal — everyone sees it and can take it back. On a shell terminal it works only while a command of yours runs there; otherwise use execute_command."
              (toolArgs {
                  let! terminal = ToolArgs.text "terminal" "the terminal id, from the terminal that was opened for the stream"
                  and! data = ToolArgs.textOption "data" "text to type, e.g. \"AT\""
                  and! keys =
                      ToolArgs.textList
                          "keys"
                          (sprintf
                              "keys to press after the text, by name: %s, ctrl-a … ctrl-z — e.g. [\"enter\"], [\"ctrl-c\"]"
                              (TerminalKeys.named |> List.map fst |> List.filter (fun n -> not (n.StartsWith "ctrl-")) |> String.concat ", "))
                  // At least one; both is text followed by keys, which is how a line and its
                  // enter are said.
                  return
                      match data, keys with
                      | None, [] -> Error "nothing to type — give data, keys, or both"
                      | _ ->
                          TerminalKeys.bytesOf keys
                          |> Result.map (fun bytes -> terminal, String.concat "" (Option.toList data @ [ bytes ]))
               })
              (whenValid (fun (terminal, data) ->
                  match TerminalId.create terminal with
                  | Error e -> async { return Error (sprintf "not a terminal id: %s" e) }
                  | Ok id -> ok (writeTerminal capabilities id data)))

          tool
              "read_terminal"
              "Read what a terminal has said, optionally waiting for it to say something. Use it when the answer doesn't come back as a command's output — a live stream, or a shell terminal where a full-screen program waits on a key. `wait_for` holds the read until that text appears (on timeout it answers with what was said, usually the reason it didn't). No `from`: the tail now, capped, saying what it left out. `from`: a page from that line, plus the line to carry into the next call — how you read what was said before you arrived. Every answer says which lines it covers of how many. Reading takes nothing — whoever's typing keeps the terminal. On a shell terminal the tail is yours while a command of yours runs, and `wait_for_pattern` waits for a line of output without polling. Between commands the tail is refused (that output comes back from execute_command), but `from` still pages it."
              (toolArgs {
                  let! terminal = ToolArgs.text "terminal" "the terminal id, from the terminal that was opened for the stream"
                  // Absent is the tail, which is what the optional parameters degrade to.
                  and! from = ToolArgs.integerOption "from" "the line to read forward from, as reported by a previous read; omit for the tail"
                  and! until =
                      waitFor
                          "hold the read until this exact text appears, e.g. \"login: \". Literal text — for a pattern use wait_for_pattern"
                          "hold the read until output matches this pattern, e.g. \"[#$>] $\" for a shell prompt. Takes literal text, `.`, `[classes]`, `*`, `+`, `?`, `|`, groups, and `^`/`$` anchored to a LINE. No backreferences, lookaround or non-greedy quantifiers"
                          "how long to hold before answering with what was said instead; default 10"
                          10.0
                  return until |> Result.map (fun until -> terminal, from, until)
               })
              (whenValid (fun (terminal, from, until) ->
                  match TerminalId.create terminal with
                  | Error e -> async { return Error (sprintf "not a terminal id: %s" e) }
                  | Ok id -> ok (readTerminal capabilities id from until)))

          // Read-only, and says so in the descriptor: the one tool here that touches a file
          // and is not an act. Its call is still on the record — the tool-use chip says which
          // file, which is the whole point of it over `sed -n` in a terminal.
          (let descriptor, body =
              tool
                  ToolName.ReadFile
                  "Read a file, or a window of it, numbered by line. Prefer this over cat/sed/head/tail in execute_command: it's on the record as a read of THIS file, and the people here see what you looked at. Paths are as a terminal in that sandbox would take them — relative to where its terminals start (the checkout, once add_repo and set_shell_profile have run), or absolute. Every answer says which lines it covers of how many; a long file comes back a page at a time, and the answer says which `offset` reads on. Lines longer than 2000 characters are cut. A picture (.png, .jpg, .gif, .webp) comes back as the picture itself, for you to look at — so look before you describe one."
                  (toolArgs {
                      let! path = ToolArgs.text "path" "the file, e.g. \"src/Program.fs\", \"$TMPDIR/out.log\" — a checkout's path is the one the repos query gives"
                      and! offset = ToolArgs.integerOption "offset" "the first line to read, 1-based; omit for the top"
                      and! limit = ToolArgs.integerOption "limit" "how many lines; omit for 2000"
                      and! sandbox = ToolArgs.textOption "sandbox" "the work sandbox whose files these are; omit for the default one"
                      return path, offset, limit, sandbox
                   })
                  (fun (path, offset, limit, sandbox) -> answered (readFile capabilities path offset limit sandbox))
           { descriptor with ReadOnly = true }, body)

          (let descriptor, body =
              tool
                  "search_files"
                  "Search file contents for a pattern, recursively — `grep -rn` behind a typed door: the record says what you searched for and where. Prefer it over grep in execute_command. Answers `path:line:text` lines, at most 200, saying when more were cut. Extended regular expression (grep -E); `.git` is skipped, and binary files. Paths as read_file takes them."
                  (toolArgs {
                      let! pattern = ToolArgs.text "pattern" "an extended regular expression, e.g. \"let (private )?readFile\""
                      and! path = ToolArgs.textOption "path" "the directory or file to search; omit for where terminals start"
                      and! glob = ToolArgs.textOption "glob" "only files whose NAME matches this glob, e.g. \"*.fs\""
                      and! sandbox = ToolArgs.textOption "sandbox" "the work sandbox to search in; omit for the default one"
                      return pattern, path, glob, sandbox
                   })
                  (fun (pattern, path, glob, sandbox) -> ok (searchFiles capabilities pattern path glob sandbox))
           { descriptor with ReadOnly = true }, body)

          (let descriptor, body =
              tool
                  "find_files"
                  "List the files whose names match a glob, recursively — `find` behind a typed door. Prefer it over find/ls in execute_command. One path per line, at most 200; `.git` is skipped. A glob without a slash matches file names anywhere below `path` (\"*.fsproj\"); one with a slash matches the path's tail (\"src/*/View.fs\"). Paths as read_file takes them."
                  (toolArgs {
                      let! glob = ToolArgs.text "glob" "the name pattern, e.g. \"*.fs\" or \"tests/*/Main.fs\""
                      and! path = ToolArgs.textOption "path" "the directory to look under; omit for where terminals start"
                      and! sandbox = ToolArgs.textOption "sandbox" "the work sandbox to look in; omit for the default one"
                      return glob, path, sandbox
                   })
                  (fun (glob, path, sandbox) -> ok (findFiles capabilities glob path sandbox))
           { descriptor with ReadOnly = true }, body)

          tool
              ToolName.EditFile
              "Replace one exact piece of text in a file with another. Prefer this over sed/awk/heredocs in execute_command: the change is on the record as an edit of THIS file, and the people here see what changed. Read the file first (read_file) and quote `old_string` exactly as it appears — whitespace and indentation included, without the line numbers. It must match ONCE: if it matches more, add surrounding lines until it is unique, or pass replace_all: true to change every occurrence. Answers with what changed, or why nothing did. Paths as read_file takes them."
              (toolArgs {
                  let! path = ToolArgs.text "path" "the file to edit, as read_file names it"
                  and! oldText = ToolArgs.text "old_string" "the exact text to replace, as it appears in the file"
                  and! newText = ToolArgs.text "new_string" "the text to put in its place"
                  and! replaceAll = ToolArgs.flag "replace_all" "true to replace every occurrence; default false, which requires exactly one"
                  and! sandbox = ToolArgs.textOption "sandbox" "the work sandbox whose file this is; omit for the default one"
                  return path, oldText, newText, replaceAll, sandbox
               })
              (fun (path, oldText, newText, replaceAll, sandbox) ->
                  ok (editFile capabilities path oldText newText replaceAll sandbox))

          tool
              ToolName.WriteFile
              "Write a whole file: create it, or replace everything in it. For a change inside an existing file use edit_file, which records what changed; this records that the file was written. Directories on the way are created. Paths as read_file takes them."
              (toolArgs {
                  let! path = ToolArgs.text "path" "the file to write, as read_file names it"
                  and! content = ToolArgs.text "content" "the entire new content of the file"
                  and! sandbox = ToolArgs.textOption "sandbox" "the work sandbox whose file this is; omit for the default one"
                  return path, content, sandbox
               })
              (fun (path, content, sandbox) -> ok (writeFile capabilities path content sandbox))

          tool
              ToolName.ShareArtifact
              "Share a file with the people here — an image you plotted, a screenshot, a report. Takes a path in a sandbox (as read_file takes them) and copies it into the session's artifacts, which everyone can see and nobody has to have a sandbox to read. Answers with the address the copy got, `file:///artifacts/<name>/<version>`: write it in a message, as it is, and the people here see it as the file — a chip that opens it — rather than as a URL. Sharing the same name again does NOT overwrite it — it adds a version, and the older ones stay where they are, so an address you have already written keeps showing what it showed. At most 100 MB a file; the refusal says how big yours is. The `artifacts` query lists what has been shared."
              (toolArgs {
                  let! path = ToolArgs.text "path" "the file to share, e.g. \"out/chart.png\" — a literal path, as read_file takes one: it is not a shell word, so an environment variable in it is not expanded"
                  // Optional because the file already has one, and carrying it over keeps the
                  // extension the media type is read from.
                  and! name = ToolArgs.textOption "name" "what to call it here, e.g. \"coverage.png\"; omit to keep the file's own name (which is where the file type is read from)"
                  and! sandbox = ToolArgs.textOption "sandbox" "the work sandbox the file is in; omit for the default one"
                  return path, name, sandbox
               })
              (fun (path, name, sandbox) -> ok (shareArtifact capabilities path name sandbox))

          // Read-only, and declared beside the verb it is the other half of rather than with
          // the file readers: what it reaches is the session's own store, which no sandbox
          // path names and which `read_file` therefore cannot be asked for.
          (let descriptor, body =
              tool
                  "read_artifact"
                  "Read something shared here: look at a picture, read a report. The other half of share_artifact — these are the SESSION's files, not any sandbox's, so read_file cannot reach them and this reaches nothing else. Takes an address as the artifacts query and share_artifact answer with it: \"file:///artifacts/<name>\" is whatever is latest under that name, and \"file:///artifacts/<name>/<version>\" is one version's bytes for good. A picture comes back as the picture itself, for you to look at. Text comes back a page at a time like read_file, and the answer says which `offset` reads on."
                  (toolArgs {
                      let! artifact = ToolArgs.text "artifact" "the address, e.g. \"file:///artifacts/chart.png\" — the artifacts query lists what there is"
                      and! offset = ToolArgs.integerOption "offset" "the first line to read, 1-based; omit for the top. Nothing to a picture"
                      and! limit = ToolArgs.integerOption "limit" "how many lines; omit for 2000. Nothing to a picture"
                      return artifact, offset, limit
                   })
                  (fun (artifact, offset, limit) -> answered (readArtifact capabilities artifact offset limit))
           { descriptor with ReadOnly = true }, body)

          tool
              "open_tab"
              "Put a terminal within reach of the people here: it opens as a tab in their side pane's strip, ready to choose. Takes an address — a terminal as \"terminal:<id>\", or a file as \"file:///artifacts/<name>\" (what share_artifact answers with; name a version to pin one). The strip holds terminals only: a file is never a tab, and the pane already lists every file the session has shared, so opening one this way changes nothing on anyone's screen — to put a file in front of somebody, use focus_tab. It does NOT take anyone's screen: everybody stays on what they were reading, and the tab waits to be chosen — to show somebody something because they asked to see it, use focus_tab. Opening the same address twice is the same one tab. Say in your message what you opened; the tab is how they find it again, not how they learn it exists."
              (ToolArgs.text "address" "what to open, e.g. \"file:///artifacts/chart.png\" or \"terminal:01HQ...\"")
              (fun address -> ok (openTab capabilities address false))

          tool
              "focus_tab"
              "Show the people here something, taking their side pane: a terminal opens as a tab if it is not one already and becomes the tab their pane is showing; a file is laid over their pane as a preview, which they close to get back to the terminal they were on. Either way it REPLACES whatever they were reading. Do this when they have asked to be shown something (\"show me the chart\", \"put it up\"), and not otherwise. If you merely think they will want a terminal next, open_tab puts it in their strip without taking their screen, and that is almost always the right one. Addresses as open_tab takes them."
              (ToolArgs.text "address" "what to show, as open_tab names it")
              (fun address -> ok (openTab capabilities address true))

          tool
              "close_tab"
              "Take back an open_tab or a focus_tab: a terminal's tab goes from the side pane's strip, and a file shown with focus_tab is taken down if it is still up. Use it when what you opened has stopped being useful, rather than leaving it there. Asking is all this does — a terminal somebody is looking at stays on their screen until they move off it. Closing something that is not open is not an error. Addresses as open_tab takes them."
              (ToolArgs.text "address" "what to close, as open_tab names it")
              (fun address -> ok (closeTab capabilities address))

          tool
              "set_secret"
              "Store a named secret for this session (WRITE-ONLY: no tool reads it back). To USE it, reference its name as an environment-variable secret ref when an environment starts — the value is injected there and never appears in the conversation."
              (toolArgs {
                  let! name = ToolArgs.text "name" "the secret name, e.g. DEPLOY_TOKEN"
                  // The one argument in the repo that must never be recorded, and it says so
                  // in the schema rather than in a list somebody has to remember to update.
                  and! value = ToolArgs.secret "value" "the secret value to store"
                  return name, value
               })
              (fun (name, value) -> ok (setSecret capabilities name value))

          tool
              "list_secrets"
              "List this session's stored secret names and timestamps. Never values."
              ToolArgs.none
              (fun () -> ok (listSecrets capabilities ()))

          tool
              "delete_secret"
              "Delete one of this session's stored secrets by name."
              (ToolArgs.text "name" "the secret name to delete")
              (fun name -> ok (deleteSecret capabilities name))

          tool
              "add_repo"
              "Clone a GitHub repo into this session's shared repos directory (visible to everyone here and inside the work environment). Takes owner/repo, never a URL, and only repos the session's GitHub credential can reach — GitHub says \"not found\" for one it won't show you, so not-found on a repo that exists means nobody has connected GitHub here; say that rather than retrying. Answers with the checkout path as a terminal here reaches it (usually relative to where a terminal starts): use it as given for cd and set_shell_profile, don't rebuild it. Read-only bootstrap — commit or push with execute_command. An already-added repo just reports its state."
              (repo "the repo as owner/name, e.g. \"octocat/hello-world\"")
              (addRepo capabilities >> ok)

          tool
              "remove_repo"
              "Delete a repo's checkout from this session — everyone here sees it leave the repos list. Use it when add_repo says a checkout is unreadable, or when the session is done with a repo. A checkout with uncommitted changes is REFUSED unless you pass `force`: removing deletes that work, and re-adding brings back the commits and nothing else — read the refusal and decide, don't force by reflex. Terminals set to start in the checkout go back to the sandbox default, and the answer says so. add_repo is the way back."
              (toolArgs {
                  let! repo = repo "the repo as owner/name, e.g. \"octocat/hello-world\""
                  and! force = ToolArgs.flag "force" "true to delete uncommitted changes along with the checkout"
                  return repo, force
               })
              (fun (repo, force) -> ok (removeRepo capabilities repo force))

          tool
              "switch_branch"
              "Switch a repo's checkout to a branch (optionally creating it). Local only — never touches the remote. Everyone in the session sees the switch in the timeline."
              (toolArgs {
                  let! repo = repo "owner/name"
                  and! branch = ToolArgs.text "branch" "the branch to switch to"
                  and! create = ToolArgs.flag "create" "create the branch (like switch -c)"
                  return repo, branch, create
               })
              (fun (repo, branch, create) -> ok (switchBranch capabilities repo branch create))
          tool
              "fetch_repo"
              "Fetch a repo's remote refs (prune, no submodules). Use before switching to a branch that only exists on the remote."
              (repo "owner/name")
              (fetchRepo capabilities >> ok)

          tool "repo_status" "A repo checkout's git status (porcelain, with branch header)." (repo "owner/name")
              (inspectRepo capabilities.Repos.Status >> ok)

          tool "repo_log" "The last 30 commits of a repo checkout, one line each." (repo "owner/name")
              (inspectRepo capabilities.Repos.Log >> ok)

          tool "repo_diff" "The uncommitted diff of a repo checkout (capped; use a terminal for the full thing)." (repo "owner/name")
              (inspectRepo capabilities.Repos.Diff >> ok)

          tool
              ToolName.StartWorkSandbox
              "Ensure a named work sandbox exists for this session, and return it. Returns the running one unchanged when there is one — safe to call every time. What a sandbox is, and which connections (\"github\" lets git push from a terminal) it forwards, is what its repo's yession.yaml or the operator declared for it; each command run there spends the credentials of whoever's turn it is."
              (ToolArgs.text "name" "the sandbox name, e.g. \"default\" or \"test\"; a repo's is \"owner/repo:name\"")
              (fun name -> ok (startWorkSandbox capabilities name))

          tool
              "stop_work_sandbox"
              "Stop a named work sandbox, killing anything running in it."
              (ToolArgs.text "name" "the sandbox name")
              (fun name -> ok (stopWorkSandbox capabilities name))

          tool
              "set_shell_profile"
              "Say where terminals opened from now on start. Use it once after add_repo, with the path add_repo gave you, so you stop putting `cd` in front of every command — every terminal opened afterwards starts there, yours and the people's, and it survives a restart. It takes a DIRECTORY, not a script (execute_command is still the only way to run anything), and the directory must already exist in that sandbox — this checks and says so rather than opening a terminal nowhere. A path from add_repo or the repos query goes in as given. Omit `cwd` to clear it. Terminals already open keep their directory, except the one your plain execute_command uses, which is reopened for you."
              (toolArgs {
                  // Both optional: an absent `cwd` is the CLEAR, and an absent `sandbox` the
                  // default one — so a bare call puts the default sandbox back as it was.
                  let! cwd =
                      ToolArgs.textOption
                          "cwd"
                          "a directory the sandbox has, said the way add_repo and the repos query say it, e.g. \"repos/octocat/hello-world\"; omit it to clear the profile"
                  and! sandbox = ToolArgs.textOption "sandbox" "the work sandbox this is about; omit for the default one"
                  return cwd, sandbox
               })
              (fun (cwd, sandbox) -> ok (setShellProfile capabilities sandbox cwd)) ]

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
