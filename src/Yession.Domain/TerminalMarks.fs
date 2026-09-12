namespace Yession.Domain.Terminals

open Yession.Domain

/// OSC 133 semantic marks: how the Session Process learns where a block starts, where it
/// ends, and with what code (Plan 13, stage 2d).
///
/// The marks are emitted by the shell's own prompt hooks — not by a sentinel the drain
/// appends to the command line. A sentinel is the tempting cheaper mechanism and it is wrong
/// for ordinary command lines a person is invited to type and edit: a trailing comment
/// (`ls # check`) swallows it, a heredoc puts it on the delimiter line, a trailing `&`
/// changes what `$?` refers to. Repairing that lands a parser for shell grammar in the
/// drain, which fails silently as a block that never closes.
///
/// EVERY MARK CARRIES A NONCE, and that is the whole of the integrity story. `ESC ] 133 ; D
/// ; 0 BEL` is a dozen bytes of ASCII and the output stream is full of bytes we did not
/// write — a file someone `cat`s, a build log, a filename. Here a mark is not a rendering
/// nicety: it writes the event log. A forged `D` would close the running block early with an
/// exit code nobody produced and a truncated transcript range — a failed command recorded as
/// successful, in the record that exists to be trusted. So a mark without this terminal's
/// nonce is not a mark; it is bytes that happen to look like one, and it stays in the output.
///
/// Warp, whose block model this is, does not use OSC 133 for boundaries at all: it carries
/// hex-encoded JSON on a private DCS channel, because it must move USER DATA out of a shell it
/// does not control — a cwd or a PS1 containing `ESC` would otherwise terminate the sequence
/// carrying it. We need none of that. The drain COMPOSED the command line and already recorded
/// it in `TerminalBlockStarted`, so all that has to come back is "it started" and "it finished,
/// with this code" — which is exactly what `C` and `D` say, and what
/// `registerOscHandler(133, …)` reads without a parser of ours. Anything richer means
/// encoding, and encoding means a payload.
type Mark =
    /// `A` — the shell is about to print its prompt. The handshake the open-probe waits for,
    /// and the only evidence that instrumentation took at all.
    | MarkPromptStart
    /// `C` — the shell has STARTED running a command. Its timing does not depend on how long
    /// the command runs, which is what makes a missing one a reliable signal that the marks
    /// are gone rather than that the command is slow.
    | MarkCommandStart
    /// `D;<code>` — the command finished, with this exit status.
    | MarkCommandDone of exitCode: int

/// One shell's instrumentation: the text to type at it.
///
/// Every dialect here marks a command's START as well as its end, and the drain relies on
/// that for two things a `D` alone cannot give it:
///
///   * Telling this block's completion from a leftover prompt. bash arms
///     `PS1`/`PROMPT_COMMAND` and then types several more rc lines (`__y_armed=1`,
///     `trap … DEBUG`), each ending in a prompt cycle whose `PROMPT_COMMAND` emits a `D`. The
///     open-probe returns on the first `A`, so those stale `D`s are still in flight when the
///     first real block runs — and a `D` accepted without this block's `C` closes it early,
///     with an exit code that is not the command's.
///   * Where the block's OUTPUT begins. The shell prints the line it was handed before it runs
///     it — echo, continuation prompts, a bell for the line-kill — and none of that is the
///     command's. The start mark is the boundary; see `Marks.lineFor`.
///
/// bash and zsh have a preexec hook, real or emulated with a DEBUG trap. A POSIX `sh` has
/// none — its prompt marks ride in `PS1`, which is expanded AFTER a command — so its start
/// mark rides in the typed line instead, as `Marks.lineFor` composes it.
type ShellInstrumentation =
    { /// The rc lines, as text typed at the shell's prompt.
      Rc : string }

/// One piece of a scanned chunk, in the order the shell printed it.
///
/// Order is the point of this type. A chunk can carry a command's last line, the `D` that
/// ends its block, and the next prompt in one read, and each of those has to land on its own
/// side of the mark: output before the `D` inside the block's range, the prompt after it
/// outside. Handing back "the marks" and "the text" as two values — the shape this replaced
/// — lost that, so a prompt printed after a `D` was recorded before the `D` was acted on and
/// closed inside the block that had already finished.
type Scanned =
    /// Bytes the shell printed, with every mark of ours taken out.
    | Printed of string
    /// A mark of ours, where it fell.
    | Marked of Mark

module Marks =

    [<Literal>]
    let private Esc = '\u001b'

    [<Literal>]
    let private Bel = '\u0007'

    /// The prefix every mark shares: `ESC ] 133 ;`.
    let private prefix = "\u001b]133;"

    /// The shell-side emitter for one mark, as the text a prompt hook prints. `%d` where the
    /// exit code goes; the nonce is baked in by `rcFor`.
    let private emit (nonce: string) (body: string) = sprintf "\\033]133;%s;y=%s\\007" body nonce

    /// Parse one mark body (everything between `ESC ] 133 ;` and the terminator), returning
    /// the mark only when the nonce matches.
    ///
    /// The body is `<kind>[;<param>]*`, and the nonce rides as a `y=` parameter so the shape
    /// stays a legal OSC 133 sequence — a terminal that does not know about `y=` ignores it,
    /// which keeps the emitter usable in a plain terminal for debugging.
    let parseBody (nonce: string) (body: string) : Mark option =
        let parts = body.Split ';' |> Array.toList
        let hasNonce = parts |> List.exists (fun p -> p = "y=" + nonce)
        if not hasNonce then None
        else
            match parts with
            | "A" :: _ -> Some MarkPromptStart
            | "C" :: _ -> Some MarkCommandStart
            | "D" :: code :: _ ->
                // A `D` whose code will not parse is still a completion — the shell said the
                // command ended and it holds the nonce. Reporting -1 keeps the block closing,
                // which is better than leaving it open for ever over an unreadable integer.
                match System.Int32.TryParse code with
                | true, value -> Some (MarkCommandDone value)
                | _ -> Some (MarkCommandDone -1)
            // `D` with no code at all: same reasoning.
            | [ "D" ] -> Some (MarkCommandDone -1)
            | _ -> None

    /// Scan a chunk of output: what it printed and the marks it carried, in order, and
    /// whatever trailing partial sequence must be carried into the next chunk.
    ///
    /// Three things this has to get right, and each of them is a bug if it does not.
    ///
    /// **Marks are stripped**, so the nonce never reaches the transcript. Chunks are
    /// fetchable over HTTP and, after stage 3b, readable by the agent through
    /// `read_terminal_block` — an unstripped nonce would let one approved command buy the
    /// ability to forge the outcome of every later one. It is independently right: marks are
    /// protocol rather than content, and an asciicast replayed in `asciinema` should not
    /// carry them.
    ///
    /// **A partial sequence is carried, not dropped.** A pty delivers whatever the kernel
    /// had, so a mark can arrive split across two reads. Scanning each chunk independently
    /// would both miss the mark and leave half an escape sequence in the transcript.
    ///
    /// **A foreign mark is left alone.** Anything without our nonce — including a nested
    /// shell's own OSC 133 integration — passes through as ordinary output, because it IS
    /// ordinary output as far as this terminal is concerned.
    ///
    /// **The readline brackets come off with the mark.** The `sh` dialect prints its marks
    /// between `\001` and `\002` so readline measures them as zero width (see `rcFor`).
    /// readline consumes those bytes itself; a shell without it (dash) prints them, and two
    /// invisible control characters around every prompt are protocol, not content. So a
    /// `\001` directly before a mark of ours and a `\002` directly after it are stripped
    /// with it, and a trailing `\001` is carried like a partial prefix. A `\002` split
    /// from its mark by a chunk boundary is the one byte that gets through, and it is
    /// invisible where it lands.
    let scan (nonce: string) (carry: string) (data: string) : Scanned list * string =
        let input = carry + data
        let out = System.Text.StringBuilder ()
        let scanned = ResizeArray<Scanned> ()
        // Text is gathered until a mark of ours cuts it, so consecutive bytes stay one piece
        // and a mark always sits between what came before it and what came after.
        let cut () =
            if out.Length > 0 then
                scanned.Add (Printed (string out))
                out.Clear () |> ignore

        let rec walk (index: int) : string =
            if index >= input.Length then ""
            else
                let start = input.IndexOf (prefix, index)
                if start < 0 then
                    // No mark ahead. Everything is output except a trailing fragment that
                    // could still BECOME our prefix once the next chunk arrives.
                    let keepFrom =
                        let candidate = max index (input.Length - prefix.Length - 1)
                        // The longest suffix that is a proper prefix of `ESC ] 133 ;`, with
                        // or without the `\001` the sh dialect puts in front of it.
                        [ candidate .. input.Length - 1 ]
                        |> List.tryFind (fun i ->
                            let suffix = input.Substring i
                            let unbracketed = if suffix.StartsWith "\u0001" then suffix.Substring 1 else suffix
                            prefix.StartsWith unbracketed)
                        |> Option.defaultValue input.Length
                    out.Append (input.Substring (index, keepFrom - index)) |> ignore
                    input.Substring keepFrom
                else
                    let bodyStart = start + prefix.Length
                    // A mark ends at BEL or at ST (`ESC \`); both spellings are legal and a
                    // shell's `printf` may emit either.
                    let bel = input.IndexOf (Bel, bodyStart)
                    let st = input.IndexOf ("\u001b\\", bodyStart)
                    let terminator =
                        match bel, st with
                        | -1, -1 -> -1
                        | -1, s -> s
                        | b, -1 -> b
                        | b, s -> min b s
                    // Where the output before this mark ends. A `\001` directly ahead of the
                    // mark is its readline bracket and travels with it — carried with a
                    // truncated one, dropped with one of ours, left as output with a foreign
                    // one — so nothing is appended until the mark has been read.
                    let bracketed = start > index && input.[start - 1] = '\u0001'
                    let textEnd = if bracketed then start - 1 else start
                    if terminator < 0 then
                        // Truncated: carry the whole thing, terminator and all, to the next
                        // chunk. Emitting it as output here would leak a half-written mark
                        // into the transcript and lose the mark itself.
                        out.Append (input.Substring (index, textEnd - index)) |> ignore
                        input.Substring textEnd
                    else
                        let body = input.Substring (bodyStart, terminator - bodyStart)
                        let width = if input.[terminator] = Bel then 1 else 2
                        match parseBody nonce body with
                        | Some mark ->
                            out.Append (input.Substring (index, textEnd - index)) |> ignore
                            cut ()
                            scanned.Add (Marked mark)
                            // Ours, so the closing bracket that may follow it is ours too.
                            let after = terminator + width
                            let after = if after < input.Length && input.[after] = '\u0002' then after + 1 else after
                            walk after
                        | None ->
                            // Not ours. It is output, verbatim — bracket, terminator and all.
                            out.Append (input.Substring (index, terminator + width - index)) |> ignore
                            walk (terminator + width)

        let rest = walk 0
        cut ()
        List.ofSeq scanned, rest

    /// The marks in a scan, in order — for a reader that wants only those.
    let marksOf (scanned: Scanned list) : Mark list =
        scanned |> List.choose (fun piece -> match piece with Marked mark -> Some mark | Printed _ -> None)

    /// What a scan printed, joined — for a reader that wants only that.
    let printedOf (scanned: Scanned list) : string =
        scanned |> List.choose (fun piece -> match piece with Printed text -> Some text | Marked _ -> None) |> String.concat ""

    /// The shell instrumentation, as the text of an rc file. Parameterised by the terminal's
    /// nonce, which is minted per terminal and never leaves the Session Process except into
    /// this file.
    ///
    /// Two habits here cost nothing and are taken from Warp's hooks. Every line begins with a
    /// SPACE, so `HISTCONTROL=ignorespace` / `HIST_IGNORE_SPACE` keeps our instrumentation
    /// out of the user's shell history. And external binaries are invoked through
    /// `command -p`, so a clobbered `PATH` in someone's image cannot break the marks.
    ///
    /// The exit code is captured as the FIRST statement of the prompt hook. Anything run
    /// before it overwrites `$?`, and then every block reports the status of our own
    /// bookkeeping rather than of the command — which would be worse than no mark at all,
    /// because it looks like an answer.
    let rcFor (shell: string) (nonce: string) : ShellInstrumentation option =
        let promptStart = emit nonce "A"
        let commandStart = emit nonce "C"
        let commandDone = emit nonce "D;%d"
        match shell with
        | "bash" ->
            // bash has no `preexec`; the DEBUG trap is the standard emulation. Two guards
            // keep it from marking anything but a real command's start. `__y_armed` fires it
            // once per submitted line rather than once per word. And the `$BASH_COMMAND` case
            // skips the trap when the command it is about to run is `__y_post` — the prompt
            // hook itself, which the trap fires for on EVERY prompt, idle ones included.
            // Without that skip an idle prompt emitted a spurious `C`/`D` pair (the trap ran,
            // `__y_armed` was still set from the last `__y_post`, so out went a `C`), and the
            // drain — which completes a block on the `D` that follows its `C` — could not tell
            // that pair from a real command's. A block still pending when such a pair landed
            // closed early, on a prompt cycle that was not its command. With the skip an idle
            // prompt emits `D`/`A` alone, and the drain drops that `D` for want of a `C`.
            // (`case`/`trap`/`$BASH_COMMAND` are POSIX-and-bash portable — no PS0, so no bash
            // ≥ 4.4 floor.)
            Some
                { Rc =
                    String.concat "\n"
                        [ " __y_pre() { case \"$BASH_COMMAND\" in __y_post) return;; esac; [ -n \"$__y_armed\" ] && { command -p printf '" + commandStart + "'; unset __y_armed; }; }"
                          " __y_post() { __y_code=$?; command -p printf '" + commandDone + "' \"$__y_code\"; __y_armed=1; }"
                          " PROMPT_COMMAND='__y_post'"
                          " PS1='\\[" + promptStart + "\\]'\"$PS1\""
                          " __y_armed=1"
                          " trap '__y_pre' DEBUG" ] }
        | "zsh" ->
            // zsh has real hooks. They are APPENDED to whatever the image's shell already
            // registered, never substituted: replacing a shell's existing hooks breaks the
            // shell (Warp special-cases powerlevel10k for exactly this).
            Some
                { Rc =
                    String.concat "\n"
                        [ " __y_pre() { command -p printf '" + commandStart + "'; }"
                          " __y_post() { __y_code=$?; command -p printf '" + commandDone + "' \"$__y_code\"; }"
                          " autoload -Uz add-zsh-hook"
                          " add-zsh-hook preexec __y_pre"
                          " add-zsh-hook precmd __y_post"
                          " PS1='" + promptStart + "'\"$PS1\"" ] }
        | "sh"
        | "dash" ->
            // A bare POSIX shell has NO prompt hook, so the marks ride inside PS1, which the
            // shell re-expands before each prompt. This is the shakiest of the three — there
            // is no `preexec` to hang `C` on at all — and it is exactly why the probe decides
            // by observation rather than by shell name.
            //
            // BOTH marks go through `printf`, in one command substitution, and that is the
            // whole of what makes this dialect work anywhere. PS1 is expanded by the SHELL,
            // and interpreting a `\033` written into a prompt is a bash extension: dash does
            // not have it, and dash is `/bin/sh` on Debian and Ubuntu. So an `A` left as
            // literal text arrived there as the eight characters `\033]133;A;…`, the probe
            // never saw its mark, and every terminal on such a host fell back — silently — to
            // a process per block, which carries no `cd` and no variable into the next one.
            // `printf` interprets the octal in every POSIX shell, which is exactly why `D`
            // reached us from dash all along and `A` never did. `D` before `A`: the previous
            // command's output ends before this prompt begins, the order FTCS gives them.
            //
            // The marks are bracketed in `\001` … `\002` — readline's own zero-width
            // markers, the bytes bash's `\[` and `\]` become — and that bracket is not
            // cosmetic. A prompt is measured by whoever prints it, and bash-as-`sh` prints it
            // through readline, which counts every byte it was not told to ignore. Two marks
            // carrying a UUID nonce are a hundred-odd bytes, so readline believed the prompt
            // was wider than an 80-column terminal and did what it does to a prompt that
            // wraps: typed a line break into it — in the middle of the `A` mark, which then
            // matched nothing, so the open-probe timed out and every terminal on a deployed
            // macOS host ran a process per block. Every fixture in the suite used a short
            // nonce and stayed green. A shell without readline (dash) prints the two bytes
            // as they are; `scan` takes them off around a mark that is ours.
            //
            // `__y_c` is the start mark this dialect has no hook for: `lineFor` puts it at the
            // head of every block's line, so it runs first and prints `C` when the command
            // starts — after the shell has finished echoing the line, which is what makes it
            // the boundary of the block's output. It hands `$?` through untouched, because the
            // command after it may be `echo $?`.
            Some
                { Rc =
                    String.concat "\n"
                        [ " __y_c() { __y_r=$?; command -p printf '" + commandStart + "'; return $__y_r; }"
                          " PS1='$(command -p printf \"\\001" + commandDone + promptStart + "\\002\" $?)'" ] }
        | _ -> None

    /// The line typed at an instrumented shell to run `command` as a block, with its stdin
    /// as `stdin` says.
    ///
    /// This is one half of a bargain, and the other half is in the drain: nothing the shell
    /// prints between this line going in and the block's start mark coming out is the
    /// block's output. That is where the shell's rendering of the line goes — the echo, a
    /// `>` per continuation line, the bell readline rings for the line-kill, the
    /// `{ … } </dev/null` around a command whose stdin is closed — and it goes nowhere: the
    /// transcript already holds the command as written, as its input record, and that is the
    /// one rendering of it anybody sees. Cut there rather than by matching the wrapper's text
    /// because there is no text to match: each shell echoes a two-line group differently
    /// (dash verbatim, bash with `> ` and a bell, zsh with cursor movement around every
    /// character), and one host echoed it twice.
    ///
    /// So every dialect has to mark a start, and this is where the one without a hook gets
    /// its mark: `sh` runs `__y_c` (defined by `rcFor`) at the head of the line. A prefix,
    /// so a trailing comment and a heredoc's body are untouched, which a suffix could not
    /// promise — and inside the brace group when stdin is closed, so the mark is printed
    /// with the command's redirection already in place.
    let lineFor (shell: string) (stdin: BlockStdin) (command: string) : string =
        match shell with
        | "sh"
        | "dash" -> BlockStdin.wrap stdin ("__y_c; " + command)
        | _ -> BlockStdin.wrap stdin command
