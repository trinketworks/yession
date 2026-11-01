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
    let Namespace = SessionTool.Namespace

    /// The names of the tools the system prompt steers by. One spelling for the descriptor
    /// and the prompt, so renaming a tool cannot leave the prompt naming one that is gone.
    module ToolName =
        [<Literal>]
        let ExecuteCommand = SessionTool.ExecuteCommand
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

    /// What a command's `description` is, said once. The `description` parameter of
    /// execute_command reads it, and the tool's own description and the system prompt point
    /// to that parameter rather than restating it. The transcript's words come from
    /// `Dom.Text`, so the guidance names the word the chip actually puts in front.
    module CommandDescription =
        let guidance =
            $"a past-tense phrase of several words or fewer that says what the command did. Examples: \"fetched master\", \"built the app\". The transcript already puts \"{Yession.App.Dom.Text.running}\" or \"{Yession.App.Dom.Text.ran}\" in front of it. Do not add that word yourself. Write \"fetched master\", not \"{Yession.App.Dom.Text.ran} fetched master\". Pass one on every call."

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
            "\n[The sandbox refused a write under /tmp. You cannot write there. Your writable scratch directory is $TMPDIR. Every sandbox sets it. Run the command again, and write under $TMPDIR.]"
        else ""

    /// A line refused by history expansion, named for what it was. An interactive shell reads
    /// `!` as "an earlier command" even inside double quotes, so `echo "done!p"` never runs —
    /// it is refused with `!p: event not found`, which says nothing about quoting to anybody
    /// who did not already know. Signature, not a parse, for `scratchDenialHint`'s reason: a
    /// hint on a line that was refused anyway.
    let private historyExpansionHint (said: string) : string =
        if said.Contains "event not found" then
            " [The shell reads `!` as history expansion. It does this inside double quotes too. Put that text in single quotes. Or remove the `!`.]"
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
                        "\n%s\n[the first %d characters. %d more come after them%s]"
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
        | TerminalCommandRan (CommandKilledForMemory kill) ->
            sprintf "KILLED for memory (exit 137) in %s: %s. %s%s" where (MemoryKill.cause kill) (MemoryKill.advice kill) output
        | TerminalCommandRan (CommandExecutionFailed reason) ->
            sprintf "EXECUTION FAILED in %s: %s%s" where reason output
        // The line never ran, and the terminal is not the problem. Said in that order, because
        // the agent this replaced was told the command was STILL RUNNING while the shell sat at
        // a prompt having refused it — and what it did next was wait.
        | TerminalCommandRan (CommandNotRun said) ->
            let rejected =
                if said = "" then "The shell rejected the line and gave no reason."
                else sprintf "The shell rejected the line: \"%s\"." said
            sprintf
                "DID NOT RUN in %s. %s The terminal can run commands. Correct the command. Then send it again.%s%s"
                where rejected (historyExpansionHint said) output
        | TerminalCommandRan (CommandOutcomeUnknown reason) ->
            sprintf
                "OUTCOME UNKNOWN in %s. %s The command is not running now. Before you send it again, examine its effects.%s"
                where reason output
        | TerminalCommandEnded reason ->
            sprintf
                "ENDED in %s with no exit status on record: %s. It is not running. It is not known if it succeeded. Read its output.%s"
                where reason output
        | TerminalCommandRunning ->
            // How it has been doing, when there is a block to measure: the clause that tells a
            // build halfway through from a wait loop whose server died at launch.
            let activity =
                match outcome.Activity with
                | Some activity -> sprintf " (%s)" (BlockActivity.describe activity)
                | None -> ""
            sprintf
                "STILL RUNNING in %s%s. The command is not finished. Nothing was cancelled. For a long command, pass background: true. Then end your turn. The session wakes you when the command finishes. Otherwise, use check_pending '%s' to get the outcome. If the command is stuck or waits for input, use write_terminal to type into it. keys: [\"ctrl-c\"] interrupts it.%s"
                where activity handle output
        | TerminalCommandSaid waitedFor ->
            let activity =
                match outcome.Activity with
                | Some activity -> sprintf " (%s)" (BlockActivity.describe activity)
                | None -> ""
            sprintf
                "SAID %s in %s%s. That is the text you waited for. The command is still running. Use check_pending '%s' to get the rest.%s"
                waitedFor where activity handle output
        | TerminalCommandStarted ->
            sprintf
                "STARTED in %s, in the background. Continue your work, or end your turn. The session wakes you when the command finishes. To get the result sooner, use check_pending '%s'. With wait_for, check_pending answers when the command says that text (a ready line, a prompt). It also answers when the command ends.%s"
                where handle output
        | TerminalCommandInteractive ->
            sprintf
                "WAITING FOR A KEYSTROKE in %s. A full-screen program opened. It will not finish without input. The terminal is now YOURS. Use write_terminal to answer the program. Use read_terminal to see the screen. When you exit the program, the command finishes. Use check_pending '%s' to get the outcome.%s"
                where handle output
        // Each hold names its way out. "Somebody is using it, or another command is running
        // there" was true and useless: told that over a terminal its own stuck command was
        // holding, an agent tried the same terminal four times and every verb that might have
        // ended the wait was one it had not been pointed at.
        | TerminalCommandAwaitingTerminal BehindBlock ->
            sprintf
                "WAITING FOR %s. Another command runs there. This command is queued behind it. It has NOT run. To run it now in parallel, use open_terminal. Then use execute_command with that `terminal`. If the running command is yours, you can end it. Use write_terminal with keys: [\"ctrl-c\"] in that terminal. If the terminal is yours, close_terminal ends everything in it. Otherwise, use check_pending '%s' later."
                where handle
        | TerminalCommandAwaitingTerminal BehindQueue ->
            sprintf
                "WAITING FOR %s. Other commands are queued ahead of it. It has NOT run. It runs after they run. Use check_pending '%s' later. Or use open_terminal to run it in a different terminal now."
                where handle
        | TerminalCommandAwaitingTerminal HeldByPerson ->
            sprintf
                "WAITING FOR %s. Somebody is typing there. The command has NOT run. It runs when that person finishes. That person decides when. Use check_pending '%s' later. Or use open_terminal to run it in a different terminal now."
                where handle
        | TerminalCommandAwaitingTerminal HeldByProgram ->
            sprintf
                "WAITING FOR %s. Something typed directly into that terminal is still running. Its shell is not at its prompt. The command has NOT run. It runs when the shell is at its prompt again. Use check_pending '%s' later. Or use open_terminal to run it in a different terminal now."
                where handle
        // Refused by the SESSION: that terminal cannot run commands, so nothing can run
        // there (`TerminalQueueDrain.cannotRunReason`). Unlike a classifier's refusal, the way
        // on is to run it elsewhere — the command is fine, the terminal is not.
        | TerminalCommandRefused (ActorRef.Session, reason) ->
            sprintf
                "REFUSED in %s. %sUse open_terminal to open a new terminal. Then send the command there with execute_command."
                where
                (reason |> Option.map (fun why -> why + " ") |> Option.defaultValue "")
        | TerminalCommandRefused (by, reason) ->
            let who = ActorRef.token by
            match reason with
            | Some reason -> sprintf "REFUSED by %s in %s: %s. It did not run. Do not try to run it another way." who where reason
            | None -> sprintf "REFUSED by %s in %s. It did not run. Do not try to run it another way." who where

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
                    Error "wait_for and wait_for_pattern do the same job. Give only one of them"
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
    //
    // It also says what it did with the command (`ToolQueueing`), on every answer: queued, or
    // not run at all. "No block" alone cannot tell a command still waiting its turn from one
    // that never will, and the chat draws the two in opposite colours.
    let private executeCommand
        (capabilities: AgentCapabilities)
        // The call this is, so the command's block can name it from the moment it starts.
        (call: ToolUseId option)
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
                | Some _, Some _ -> Error "name a terminal or a sandbox, not both. A terminal is already in a sandbox"
                | Some t, None -> TerminalId.create t |> Result.mapError (sprintf "not a terminal id: %s") |> Result.map (InTerminal >> Some)
                | None, Some s -> SandboxRef.parse s |> Result.mapError (sprintf "not a sandbox: %s") |> Result.map (InSandbox >> Some)
                | None, None -> Ok None
            let notRun (text: string) = { ToolAnswer.text text with Queueing = Some NothingQueued }
            match target with
            | Error e -> return notRun e
            | Ok target ->
                let request =
                    { Command = command
                      Target = target
                      Background = background
                      Stdin = stdin
                      Description = description |> Option.bind BlockDescription.ofProse
                      ToolUseId = call }
                match! capabilities.Terminals.Execute request with
                | Ok outcome ->
                    return
                        { Text = renderOutcome outcome
                          Block = outcome.Block
                          Stream = None
                          Image = None
                          Queueing = Some (CommandQueued outcome.Handle) }
                | Error reason -> return notRun (sprintf "could not run the command: %s" reason)
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
                    "STILL RUNNING: `%s` is not finished. Nothing was cancelled. Nobody is waiting for you. Use check_pending '%s' to get the outcome."
                    outcome.Summary
                    (QueueId.value handle)
            | None -> sprintf "STILL RUNNING: `%s` has not finished. Nothing was cancelled." outcome.Summary
        | CommandRefusedBy (by, reason) ->
            let who = ActorRef.token by
            match reason with
            | Some reason ->
                sprintf "REFUSED by %s: %s. `%s` did not happen. Do not try another way around it." who reason outcome.Summary
            | None -> sprintf "REFUSED by %s. `%s` did not happen. Do not try another way around it." who outcome.Summary

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
                    return { Text = renderOutcome outcome + missed; Block = outcome.Block; Stream = None; Image = None; Queueing = None }
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
                        ". That text SPELLS a key. The terminal took it as text, not as a key. To press a key, give its name in keys, e.g. keys: [\"ctrl-c\"]"
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
                            "lines %d-%d of %d. To read more, use from: %d"
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
                            "\n(%s did not appear within %gs. This is what the terminal said instead.)"
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
                                  "%s: a picture (%s, %d kB). Only you see it. The people here see a picture only after share_artifact copies it. If your reply depends on this picture, share the picture with the reply."
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
                                  "%s: a picture (%s, %d kB), shown to you. It is shared. The people here can see it already."
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

    /// The same, for a body that has to know WHICH call it is answering: the one whose work
    /// the chat draws in its own right, and has to tie back to the call that started it.
    let private toolOfCall
        (name: string)
        (description: string)
        (args: ToolArgs<'a>)
        (body: ToolUseId option -> 'a -> Async<Result<ToolAnswer, string>>)
        : ToolDescriptor * (ToolCall -> Async<Result<ToolAnswer, string>>) =
        ToolDescriptor.create Namespace name description (ToolArgs.schema args),
        fun call ->
            async {
                match ToolArgs.read args call.Arguments with
                | Error e -> return Error e
                | Ok read -> return! body call.ToolUseId read
            }

    /// For the arguments that can be refused once read: a body only runs on what passed.
    let whenValid (body: 'a -> Async<Result<ToolAnswer, string>>) (read: Result<'a, string>) : Async<Result<ToolAnswer, string>> =
        match read with
        | Error e -> async { return Error e }
        | Ok read -> body read

    /// The one verb whose body is handed the call it answers: a command it queues is drawn
    /// by the chat as its own chip, and has to say which call it stands in for.
    let private executeCommandTool (capabilities: AgentCapabilities) : ToolDescriptor * (ToolCall -> Async<Result<ToolAnswer, string>>) =
        toolOfCall
            ToolName.ExecuteCommand
            "Run a shell command in a session terminal. This is the only way to run anything. Everyone sees the command. It is on the record. Always pass `description`. That parameter says what to write. Do not use this tool to read or edit files. Use read_file and edit_file, which record WHICH file. Use the shell for files only when a dedicated tool really cannot do the job. Avoid cat, sed, awk, head, tail, grep -n and heredocs here. `sandbox`: a named work sandbox (start_work_sandbox). Omit it for the default sandbox. `terminal`: a terminal from open_terminal, to run a command in parallel with a long one. Each terminal runs one command at a time. For a long-running command, pass background: true. The tool then returns a handle immediately. End your turn. The session wakes you when the command finishes. Without background, the tool waits. If the command runs longer than the wait, the tool returns a check_pending handle. The command gets no stdin unless you pass stdin: true (readers get EOF). So pass flags, not prompts. Write scratch files under $TMPDIR. /tmp is denied. To rewrite a file, write the new content before you delete the old file. The sandbox can refuse a delete-then-write halfway. Read the answer. It says which of these happened."
            (toolArgs {
                let! command = ToolArgs.text "command" "the shell command line to run, e.g. \"npm test -- --watch=false\""
                and! terminal = ToolArgs.textOption "terminal" "the id of a terminal to run in, from open_terminal or list_terminals. Omit it to use your own terminal in the sandbox"
                and! sandbox = ToolArgs.textOption "sandbox" "the work sandbox to run in. Give a sandbox of the session by its name (\"test\"). Give a repo's sandbox as \"owner/repo:name\". Its bare name also finds it when only one repo declares that name. Omit it for the default sandbox"
                and! background =
                    ToolArgs.flag
                        "background"
                        "true to start the command and continue without waiting. Use it for long work. Use it also for work that can run in parallel with other work. The session tells you when the command finishes."
                and! stdin =
                    ToolArgs.flag
                        "stdin"
                        "true to let the command read the terminal's input. Use it for the rare command that must prompt. If you omit it, anything that reads stdin gets end-of-file immediately"
                and! description = ToolArgs.textOption "description" CommandDescription.guidance
                return command, terminal, sandbox, background, stdin, description
             })
            (fun call (command, terminal, sandbox, background, stdin, description) ->
                answered (executeCommand capabilities call command terminal sandbox background stdin description))

    /// The verbs: everything else that is written out rather than generated.
    let private verbs (capabilities: AgentCapabilities) : (ToolDescriptor * (string -> Async<Result<ToolAnswer, string>>)) list =
        [ // The terminal verbs a person already has (Plan 20, stage 3). Deliberately the same
          // three a human uses from the list — open, close, see what there is — because the
          // agent and the people in the session are working on ONE set of terminals, and a
          // second vocabulary for the agent's would make its terminals a different kind of
          // thing on every surface that draws them.
          tool
              "open_terminal"
              "Open your own terminal, and name it for its job (\"tests\", \"docs build\"). Everyone reads the name. Each terminal runs one command at a time. Open more terminals to run several commands at once. Returns a terminal id. Pass it to execute_command as `terminal`. Each sandbox has a limit on terminals. If you reach it, this tool says so. Use close_terminal to make room."
              (toolArgs {
                  let! name = ToolArgs.text "name" "what this terminal is for, e.g. \"tests\""
                  and! sandbox = ToolArgs.textOption "sandbox" "the work sandbox to open it in. Give a repo's sandbox as \"owner/repo:name\". Its bare name also works when only one repo declares it. Omit it for the default sandbox"
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
              "Close a terminal that you opened, when you no longer need it. Only the shell ends. Its recording stays for anyone to read. You can close only your own terminals. The people here can close any terminal, including yours."
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
              "List every open terminal in this session: yours and the people's. For each terminal, it says what the terminal is for and if something is running. Use it to see what you have before you open another terminal. Use it also to choose a terminal to run in."
              ToolArgs.none
              (fun () ->
                  async {
                      match! capabilities.Terminals.List () with
                      | Error reason -> return Ok (ToolAnswer.text reason)
                      | Ok [] -> return Ok (ToolAnswer.text "no terminals are open")
                      | Ok terminals ->
                          let line (t: TerminalSummary) =
                              sprintf
                                  "%s: %s%s%s%s"
                                  (TerminalId.value t.Terminal)
                                  t.Name
                                  (match t.Sandbox with
                                   | Some s when s <> SandboxRef.defaultRef -> sprintf " in %s" (SandboxRef.render s)
                                   | _ -> "")
                                  (if t.Mine then " (yours)" else "")
                                  (if t.Busy then ", running something" else "")
                          return Ok (ToolAnswer.text (terminals |> List.map line |> String.concat "\n"))
                  })

          tool
              "check_pending"
              "Get the result of what a handle names. A handle can name a long build, a command queued behind a busy terminal, or a program that waits for a keystroke. It works for execute_command and for anything that said it was still going. It returns what the original call would have returned. Do not poll a background command. End your turn. The session wakes you when the command finishes. To continue when a running command has SAID something (a server's ready line, a prompt), give `wait_for`. Then check_pending answers at the first of these events:\n- The output says that text.\n- The command ends. The answer gives its exit code and what it printed. A server that died before it was ready says so.\n- `timeout_seconds` passes.\nNever write a shell loop that waits for a port or a line."
              (toolArgs {
                  let! handle = ToolArgs.text "handle" "the handle from execute_command, or from another command that said it was still going"
                  // The default timeout is the turn's own bound: a caller naming a ready line
                  // wants it as long as a command may take, not a device's ten seconds.
                  and! until =
                      waitFor
                          "answer when the command's output contains this exact text, e.g. \"listening on\". This is literal text. For a pattern, use wait_for_pattern"
                          "answer when the command's output matches this pattern. It takes the same pattern subset as read_terminal"
                          "how long to wait. After that time, the answer gives what the command said so far. Default 120, and the maximum is 120"
                          120.0
                  return until |> Result.map (fun until -> handle, until)
               })
              (whenValid (fun (handle, until) -> answered (checkPending capabilities handle until)))

          tool
              "write_terminal"
              "Type into a terminal where you hold the keyboard. That terminal is one of these:\n- A terminal that streams something live: a device or a console, with bytes from outside this session.\n- A terminal that runs a command of yours: a full-screen program that waits for a key, a stdin: true prompt, or something stuck. keys: [\"ctrl-c\"] interrupts. [\"ctrl-d\"] is end-of-input.\nPut text in `data`. Put keys BY NAME in `keys`. They go after the text. Never write a key as an escape sequence. An escape arrives as the characters it spells. The answer says what was typed. On a live stream, typing takes the terminal. Everyone sees that. Anyone can take the terminal back. On a shell terminal, this works only while a command of yours runs there. Otherwise, use execute_command."
              (toolArgs {
                  let! terminal = ToolArgs.text "terminal" "the terminal id, from the terminal that was opened for the stream"
                  and! data = ToolArgs.textOption "data" "text to type, e.g. \"AT\""
                  and! keys =
                      ToolArgs.textList
                          "keys"
                          (sprintf
                              "keys to press after the text, by name. The names are %s, ctrl-a … ctrl-z. Examples: [\"enter\"], [\"ctrl-c\"]"
                              (TerminalKeys.named |> List.map fst |> List.filter (fun n -> not (n.StartsWith "ctrl-")) |> String.concat ", "))
                  // At least one; both is text followed by keys, which is how a line and its
                  // enter are said.
                  return
                      match data, keys with
                      | None, [] -> Error "nothing to type. Give data, keys or both"
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
              "Read what a terminal has said. Optionally, wait until it says something. Use it when the answer does not come back as the output of a command. Examples: a live stream, or a shell terminal where a full-screen program waits for a key. `wait_for` holds the read until that text appears. On timeout, it answers with what the terminal said. That text usually shows why the text you waited for did not appear. Without `from`, you get the tail now. The tail is capped. The answer says what it left out. With `from`, you get a page from that line. The answer also gives the line for the next call. Use `from` to read what the terminal said before you arrived. Every answer says which lines it covers, and of how many. Reading does not take the terminal. The person who types keeps it. On a shell terminal, you can read the tail while a command of yours runs. There, `wait_for_pattern` waits for a line of output without polling. Between commands, the tail is refused. That output comes back from execute_command. But `from` still pages it."
              (toolArgs {
                  let! terminal = ToolArgs.text "terminal" "the terminal id, from the terminal that was opened for the stream"
                  // Absent is the tail, which is what the optional parameters degrade to.
                  and! from = ToolArgs.integerOption "from" "the line to start the read from, as a previous read reported it. Omit it for the tail"
                  and! until =
                      waitFor
                          "hold the read until this exact text appears, e.g. \"login: \". This is literal text. For a pattern, use wait_for_pattern"
                          "hold the read until output matches this pattern, e.g. \"[#$>] $\" for a shell prompt. The pattern can use literal text, `.`, `[classes]`, the escapes \\d \\w \\s and \\D \\W \\S, `*`, `+`, `?`, counts {n} {n,} {n,m} up to 1000, `|`, groups, and `^`/`$` anchored to a LINE. It cannot use backreferences, lookaround or non-greedy quantifiers"
                          "how long to hold the read. After that time, the answer gives what the terminal said instead. Default 10"
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
                  "Read a file, or a window of it, with line numbers. Use this, not cat/sed/head/tail in execute_command. The record shows a read of THIS file. The people here see what you looked at. Give paths as a terminal in that sandbox would take them. A path is absolute, or relative to where the terminals of that sandbox start. That is the checkout, after add_repo and set_shell_profile have run. Every answer says which lines it covers, and of how many. A long file comes back one page at a time. The answer gives the `offset` for the next page. Lines longer than 2000 characters are cut. A picture (.png, .jpg, .gif, .webp) comes back as the picture itself, for you to look at. Look at a picture before you describe it."
                  (toolArgs {
                      let! path = ToolArgs.text "path" "the file, e.g. \"src/Program.fs\" or \"$TMPDIR/out.log\". For a checkout, use the path that the repos query gives"
                      and! offset = ToolArgs.integerOption "offset" "the first line to read, 1-based. Omit it to start at the top"
                      and! limit = ToolArgs.integerOption "limit" "how many lines. Omit it for 2000"
                      and! sandbox = ToolArgs.textOption "sandbox" "the work sandbox whose files these are. Omit it for the default sandbox"
                      return path, offset, limit, sandbox
                   })
                  (fun (path, offset, limit, sandbox) -> answered (readFile capabilities path offset limit sandbox))
           { descriptor with ReadOnly = true }, body)

          (let descriptor, body =
              tool
                  "search_files"
                  "Search file contents for a pattern, recursively. It is `grep -rn` behind a typed interface. The record says what you searched for and where. Use it, not grep in execute_command. It answers with `path:line:text` lines, at most 200. The answer says when it cut more lines. The pattern is an extended regular expression (grep -E). It skips `.git` and binary files. Give paths as read_file takes them."
                  (toolArgs {
                      let! pattern = ToolArgs.text "pattern" "an extended regular expression, e.g. \"let (private )?readFile\""
                      and! path = ToolArgs.textOption "path" "the directory or file to search. Omit it to search where terminals start"
                      and! glob = ToolArgs.textOption "glob" "only files whose NAME matches this glob, e.g. \"*.fs\""
                      and! sandbox = ToolArgs.textOption "sandbox" "the work sandbox to search in. Omit it for the default sandbox"
                      return pattern, path, glob, sandbox
                   })
                  (fun (pattern, path, glob, sandbox) -> ok (searchFiles capabilities pattern path glob sandbox))
           { descriptor with ReadOnly = true }, body)

          (let descriptor, body =
              tool
                  "find_files"
                  "List the files whose names match a glob, recursively. It is `find` behind a typed interface. Use it, not find/ls in execute_command. It answers with one path per line, at most 200. It skips `.git`. A glob without a slash matches file names anywhere below `path` (\"*.fsproj\"). A glob with a slash matches the end of the path (\"src/*/View.fs\"). Give paths as read_file takes them."
                  (toolArgs {
                      let! glob = ToolArgs.text "glob" "the name pattern, e.g. \"*.fs\" or \"tests/*/Main.fs\""
                      and! path = ToolArgs.textOption "path" "the directory to look under. Omit it to look where terminals start"
                      and! sandbox = ToolArgs.textOption "sandbox" "the work sandbox to look in. Omit it for the default sandbox"
                      return glob, path, sandbox
                   })
                  (fun (glob, path, sandbox) -> ok (findFiles capabilities glob path sandbox))
           { descriptor with ReadOnly = true }, body)

          tool
              ToolName.EditFile
              "Replace one exact piece of text in a file with another. Use this, not sed/awk/heredocs in execute_command. The record shows an edit of THIS file. The people here see what changed. Read the file first (read_file). Quote `old_string` exactly as it appears. Include its whitespace and indentation. Do not include the line numbers. It must match ONCE. If it matches more than once, add surrounding lines until it is unique. Or pass replace_all: true to change every occurrence. The answer says what changed, or why nothing changed. Give paths as read_file takes them."
              (toolArgs {
                  let! path = ToolArgs.text "path" "the file to edit, as read_file names it"
                  and! oldText = ToolArgs.text "old_string" "the exact text to replace, as it appears in the file"
                  and! newText = ToolArgs.text "new_string" "the text to put in its place"
                  and! replaceAll = ToolArgs.flag "replace_all" "true to replace every occurrence. Default false: then exactly one occurrence must match"
                  and! sandbox = ToolArgs.textOption "sandbox" "the work sandbox whose file this is. Omit it for the default sandbox"
                  return path, oldText, newText, replaceAll, sandbox
               })
              (fun (path, oldText, newText, replaceAll, sandbox) ->
                  ok (editFile capabilities path oldText newText replaceAll sandbox))

          tool
              ToolName.WriteFile
              "Write a whole file: create it, or replace all of its content. For a change inside an existing file, use edit_file. edit_file records what changed. This tool records only that the file was written. It creates the missing directories on the path. Give paths as read_file takes them."
              (toolArgs {
                  let! path = ToolArgs.text "path" "the file to write, as read_file names it"
                  and! content = ToolArgs.text "content" "the entire new content of the file"
                  and! sandbox = ToolArgs.textOption "sandbox" "the work sandbox whose file this is. Omit it for the default sandbox"
                  return path, content, sandbox
               })
              (fun (path, content, sandbox) -> ok (writeFile capabilities path content sandbox))

          tool
              ToolName.ShareArtifact
              "Share a file with the people here: an image you plotted, a screenshot, a report. Give a path in a sandbox, as read_file takes it. The tool copies the file into the artifacts of the session. Everyone can see the artifacts. Nobody needs a sandbox to read them. The answer gives the address of the copy, `file:///artifacts/<name>/<version>`. Write that address in a message as it is. The people here then see the file as a chip that opens it, not as a URL. If you share the same name again, that does NOT overwrite the file. It adds a version. The older versions stay where they are. So an address that you already wrote keeps showing what it showed. The limit is 100 MB for each file. The refusal says how big your file is. The `artifacts` query lists what was shared."
              (toolArgs {
                  let! path = ToolArgs.text "path" "the file to share, e.g. \"out/chart.png\". This is a literal path, as read_file takes one. It is not a shell word. An environment variable in it is not expanded"
                  // Optional because the file already has one, and carrying it over keeps the
                  // extension the media type is read from.
                  and! name = ToolArgs.textOption "name" "what to call it here, e.g. \"coverage.png\". Omit it to keep the file's own name (the file type is read from that name)"
                  and! sandbox = ToolArgs.textOption "sandbox" "the work sandbox the file is in. Omit it for the default sandbox"
                  return path, name, sandbox
               })
              (fun (path, name, sandbox) -> ok (shareArtifact capabilities path name sandbox))

          // Read-only, and declared beside the verb it is the other half of rather than with
          // the file readers: what it reaches is the session's own store, which no sandbox
          // path names and which `read_file` therefore cannot be asked for.
          (let descriptor, body =
              tool
                  "read_artifact"
                  "Read something shared here: look at a picture, or read a report. This is the other half of share_artifact. Artifacts are the SESSION's files, not the files of a sandbox. read_file cannot reach them. This tool reaches nothing else. Give an address as the artifacts query and share_artifact give it. \"file:///artifacts/<name>\" is the latest version under that name. \"file:///artifacts/<name>/<version>\" is the bytes of one version, permanently. A picture comes back as the picture itself, for you to look at. Text comes back one page at a time, like read_file. The answer gives the `offset` for the next page."
                  (toolArgs {
                      let! artifact = ToolArgs.text "artifact" "the address, e.g. \"file:///artifacts/chart.png\". The artifacts query lists what there is"
                      and! offset = ToolArgs.integerOption "offset" "the first line to read, 1-based. Omit it to start at the top. It has no effect on a picture"
                      and! limit = ToolArgs.integerOption "limit" "how many lines. Omit it for 2000. It has no effect on a picture"
                      return artifact, offset, limit
                   })
                  (fun (artifact, offset, limit) -> answered (readArtifact capabilities artifact offset limit))
           { descriptor with ReadOnly = true }, body)

          tool
              "open_tab"
              "Put a terminal or a file within reach of the people here. It opens as a tab in the strip of their side pane, ready to choose. Terminals come first, then files. Give an address: a terminal as \"terminal:<id>\", or a file as \"file:///artifacts/<name>\". That is the address share_artifact answers with. Name a version to pin one. This does NOT take anyone's screen. Everybody stays on what they were reading. The tab waits until somebody chooses it. To show somebody something because they asked to see it, use focus_tab. If you open the same address twice, you get the same one tab. Say in your message what you opened. The tab is how they find it again, not how they learn that it exists."
              (ToolArgs.text "address" "what to open, e.g. \"file:///artifacts/chart.png\" or \"terminal:01HQ...\"")
              (fun address -> ok (openTab capabilities address false))

          tool
              "focus_tab"
              "Show the people here something in their side pane. A terminal or a file opens as a tab, if it is not one already. That tab becomes the tab their pane shows. It REPLACES what they were reading. Do this only when they asked to see something (\"show me the chart\", \"put it up\"). If you only think they will want a terminal next, use open_tab. open_tab puts it in their strip without taking their screen. That is almost always the right tool. Give addresses as open_tab takes them."
              (ToolArgs.text "address" "what to show, as open_tab names it")
              (fun address -> ok (openTab capabilities address true))

          tool
              "close_tab"
              "Undo an open_tab or a focus_tab. The tab goes from the strip of the side pane. For a file, its preview closes too. Use it when what you opened is no longer useful. Do not leave it there. This tool only asks. A terminal that somebody looks at stays on their screen until they leave it. If you close something that is not open, that is not an error. Give addresses as open_tab takes them."
              (ToolArgs.text "address" "what to close, as open_tab names it")
              (fun address -> ok (closeTab capabilities address))

          tool
              "set_secret"
              "Store a named secret for this session (WRITE-ONLY: no tool reads it back). To USE it, give its name as an environment-variable secret ref when an environment starts. The session injects the value there. The value never appears in the conversation."
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
              "Clone a GitHub repo into the shared repos directory of this session. Everyone here can see it. It is also visible inside the work environment. Give owner/repo, never a URL. It takes only repos that the GitHub credential of the session can reach. GitHub says \"not found\" for a repo that it does not show you. So \"not found\" on a repo that exists means that nobody connected GitHub here. Say that. Do not try again. The answer gives the checkout path as a terminal here reaches it (usually relative to where a terminal starts). Use that path as given for cd and set_shell_profile. Do not build the path again yourself. This tool does not commit or push. To commit or push, use execute_command. For a repo that is already added, it only reports its state."
              (repo "the repo as owner/name, e.g. \"octocat/hello-world\"")
              (addRepo capabilities >> ok)

          tool
              "remove_repo"
              "Delete a repo's checkout from this session. Everyone here sees it leave the repos list. Use it when add_repo says a checkout is unreadable. Use it also when the session no longer needs a repo. A checkout with uncommitted changes is REFUSED unless you pass `force`. Removal deletes that work. If you add the repo again, you get back the commits and nothing else. Read the refusal and decide. Do not use force as a reflex. Terminals set to start in the checkout go back to the sandbox default. The answer says so. Use add_repo to get the repo back."
              (toolArgs {
                  let! repo = repo "the repo as owner/name, e.g. \"octocat/hello-world\""
                  and! force = ToolArgs.flag "force" "true to delete uncommitted changes along with the checkout"
                  return repo, force
               })
              (fun (repo, force) -> ok (removeRepo capabilities repo force))

          tool
              "switch_branch"
              "Switch a repo's checkout to a branch (optionally creating it). This is local only. It never touches the remote. Everyone in the session sees the switch in the timeline."
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

          tool "repo_diff" "The uncommitted diff of a repo checkout. It is capped. Use a terminal for the full diff." (repo "owner/name")
              (inspectRepo capabilities.Repos.Diff >> ok)

          tool
              ToolName.StartWorkSandbox
              "Make sure that a named work sandbox exists for this session, and return it. If that sandbox runs already, it returns it unchanged. It is safe to call every time. The repo's yession.yaml or the operator declares what the sandbox is. That declaration also sets which connections it forwards (\"github\" lets git push from a terminal). Each command that runs there uses the credentials of whoever's turn it is."
              (ToolArgs.text "name" "the sandbox name, e.g. \"default\" or \"test\". A repo's sandbox is \"owner/repo:name\"")
              (fun name -> ok (startWorkSandbox capabilities name))

          tool
              "stop_work_sandbox"
              "Stop a named work sandbox. This kills anything that runs in it."
              (ToolArgs.text "name" "the sandbox name")
              (fun name -> ok (stopWorkSandbox capabilities name))

          tool
              "set_shell_profile"
              "Say where terminals start that open from now on. Use it once after add_repo, with the path that add_repo gave you. Then you do not need `cd` in front of every command. Every terminal opened after this starts there, yours and the people's. The setting survives a restart. It takes a DIRECTORY, not a script. execute_command is still the only way to run anything. The directory must already exist in that sandbox. This tool checks that. If the directory does not exist, the tool says so. It does not open a terminal nowhere. Give a path from add_repo or the repos query as it is. Omit `cwd` to clear the setting. Terminals already open keep their directory. The exception is the terminal that your plain execute_command uses. The session reopens that one for you."
              (toolArgs {
                  // Both optional: an absent `cwd` is the CLEAR, and an absent `sandbox` the
                  // default one — so a bare call puts the default sandbox back as it was.
                  let! cwd =
                      ToolArgs.textOption
                          "cwd"
                          "a directory in the sandbox, as add_repo and the repos query give it, e.g. \"repos/octocat/hello-world\". Omit it to clear the profile"
                  and! sandbox = ToolArgs.textOption "sandbox" "the work sandbox this is about. Omit it for the default sandbox"
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
    let private declared (capabilities: AgentCapabilities) : (ToolDescriptor * (ToolCall -> Async<Result<ToolAnswer, string>>)) list =
        let ofArguments (descriptor, body: string -> Async<Result<ToolAnswer, string>>) =
            descriptor, (fun (call: ToolCall) -> body call.Arguments)
        executeCommandTool capabilities
        :: (verbs capabilities
            @ queryTools capabilities
            @ capabilities.Repos.ProviderTools
            @ capabilities.Tools.Contributed
            |> List.map ofArguments)

    /// The `yession` registry for one turn's capabilities.
    let registry (capabilities: AgentCapabilities) : ToolRegistry =
        let declared = declared capabilities
        ToolRegistry.ofNamespace
            Namespace
            (declared |> List.map fst)
            (fun call ->
                match declared |> List.tryFind (fun (descriptor, _) -> descriptor.Name = call.Name) with
                | Some (_, body) -> body call
                | None -> async { return Error (sprintf "no tool '%s/%s'" call.Namespace call.Name) })
