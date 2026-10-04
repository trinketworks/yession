namespace Yession.Session

open System
open Yession.Domain
open Yession.Domain.Sandboxes
open Yession.Domain.Agent
open Yession.Domain.Files
open Yession.Domain.Tools

/// The files inside a sandbox, reached without a terminal: what `read_file` fetches before
/// `FileSlice` cuts the window the caller asked for, and where `edit_file` and `write_file`
/// put text back.
///
/// Reached INSIDE the sandbox, by asking it, for the reason `setProfile` gives: a host-side
/// read would be wrong twice over — under docker the path is in a container this process
/// cannot see, and under srt the sandbox's read scope is not ours. So the bytes go through
/// one bare spawn each way — `cat` out, `cat >` in — with the path as an argv element,
/// never interpolated into a command line, which is the one place this could become a
/// second door around `execute_command`. It is housekeeping (`Direct`), not work: it needs
/// no toolchain and must not pay for one, and it draws no block — the tool-use record is
/// where a read shows, and the gate's record where a change does, each saying which file.
module SessionFiles =

    [<RequireQualifiedAccess>]
    type SessionFiles =
        { /// One file, as the sandbox holds it: `path` in the sandbox's own vocabulary,
          /// resolved where a terminal there would resolve it — the shell profile's directory
          /// when one is set, the sandbox's own otherwise. Its text; or, for a file named as a
          /// picture, the picture (`FileContent.Image`), read as bytes and checked to be one.
          Read : ReadFile
          /// One exact-string edit (`FileEdit.apply`), read-apply-write as ONE verb: a caller
          /// that could write without having read is a caller that can put back a file
          /// somebody else changed in between. The write happens only when the edit applied,
          /// and the act is recorded (`FileChanged`, to the actor named) only when it landed.
          Edit : ActorRef -> FileEditRequest -> Async<Result<Edited, string>>
          /// The whole file, replaced — or created, along with the directories to it. Recorded
          /// like an edit, without a diff.
          Write : ActorRef -> SandboxRef -> string -> string -> Async<Result<unit, string>>
          /// `grep -rn` under a directory, the sandbox's own lines back; no match is `Ok ""`.
          Search : SandboxRef -> string -> string option -> string option -> Async<Result<string, string>>
          /// `find` under a directory for a name glob, one path per line, sorted.
          Find : SandboxRef -> string -> string option -> Async<Result<string, string>> }

    let unavailable : SessionFiles =
        { SessionFiles.Read = fun _ _ -> async { return Error "this session has no sandboxes to read files in" }
          SessionFiles.Edit = fun _ _ -> async { return Error "this session has no sandboxes to edit files in" }
          SessionFiles.Write = fun _ _ _ _ -> async { return Error "this session has no sandboxes to write files in" }
          SessionFiles.Search = fun _ _ _ _ -> async { return Error "this session has no sandboxes to search in" }
          SessionFiles.Find = fun _ _ _ -> async { return Error "this session has no sandboxes to look in" } }

    /// Characters of one file this will carry back before giving up on it. A read is a
    /// window of at most a few thousand lines; a file past this is a build artifact or a
    /// log, and the right answer is to say so rather than hold it whole in memory.
    let maxChars = 4_000_000

    /// What one housekeeping spawn said: its exit code, and both streams whole.
    [<RequireQualifiedAccess>]
    type private Said =
        { Code : int
          Out : string
          Err : string }

    let create
        // Where a change is recorded (`FileChanged`), by the verb that made it — after the
        // write landed, so the log never carries a change that did not happen.
        (log: EventLog<SessionEvent>)
        (mintMessageId: unit -> MessageId)
        (environmentFor: SandboxRef -> SessionEnvironment.SessionEnvironment)
        // Where a shell opened in each sandbox starts (Plan 25) — the directory a relative
        // path is meant against, because it is the one a terminal there would use.
        (workingDirectoryFor: SandboxRef -> string option)
        (shell: TerminalShell)
        : SessionFiles =

        /// One bare spawn of `script` under the shell, `args` as its positional parameters
        /// and `stdin` typed in whole. Every verb here is this and a reading of the answer —
        /// so the parts that must be right once (the path never in the script, the output
        /// cap, `Direct` past the entrypoint) are right once.
        let run (sandbox: SandboxRef) (reason: string) (script: string) (args: string list) (stdin: string option) : Async<Result<Said, string>> =
            async {
                match! (environmentFor sandbox).Ensure None reason with
                | EnvironmentUnavailable reason -> return Error reason
                | EnvironmentAvailable ->
                    let out = Text.StringBuilder ()
                    let err = Text.StringBuilder ()
                    let mutable overflowed = false
                    let mutable handle : SandboxProcessHandle option = None
                    let! spawned =
                        (environmentFor sandbox).Spawn
                            { Executable = shell.Executable
                              Arguments = shell.Arguments @ [ script; "sh" ] @ args
                              Env = Map.empty
                              WorkingDirectory = workingDirectoryFor sandbox
                              Via = Direct }
                            (fun (stream, chunk) ->
                                match stream with
                                | Stdout ->
                                    if not overflowed then
                                        out.Append chunk |> ignore
                                        if out.Length > maxChars then
                                            overflowed <- true
                                            handle |> Option.iter (fun h -> h.Kill ())
                                | Stderr -> err.Append chunk |> ignore)
                    match spawned with
                    | Error reason -> return Error reason
                    | Ok h ->
                        handle <- Some h
                        match stdin with
                        | Some text ->
                            h.WriteStdin text
                            h.CloseStdin ()
                        | None -> ()
                        // The cap may already have tripped before the handle was in hand.
                        if overflowed then h.Kill ()
                        match! h.Exited with
                        | _ when overflowed ->
                            return
                                Error (
                                    sprintf
                                        "the answer is longer than %d characters, which is not one to read as text — reach for a command in the %s sandbox instead"
                                        maxChars
                                        (SandboxRef.render sandbox))
                        | SandboxExited code -> return Ok { Said.Code = code; Said.Out = out.ToString (); Said.Err = err.ToString () }
                        | SandboxRunFailed reason -> return Error reason
            }

        /// The tool's own words for its failure — minus its own name, which says nothing to
        /// a caller who asked about a file — or a sentence when it said nothing.
        let complaint (tool: string) (sandbox: SandboxRef) (said: Said) : string =
            let text = said.Err.Trim ()
            let text = if text.StartsWith (tool + ": ") then text.Substring (tool.Length + 2) else text
            if text = "" then sprintf "the %s sandbox's %s exited %d saying nothing" (SandboxRef.render sandbox) tool said.Code
            else text

        let read (sandbox: SandboxRef) (path: string) : Async<Result<string, string>> =
            async {
                // `--` so a path that starts with `-` is a path, and `$1` so the path is
                // never part of the script.
                match! run sandbox "a file was read" "cat -- \"$1\"" [ path ] None with
                | Error reason -> return Error reason
                | Ok said when said.Code = 0 -> return Ok said.Out
                | Ok said -> return Error (complaint "cat" sandbox said)
            }

        /// A picture's bytes cannot cross as text — this channel carries strings, and a PNG
        /// is not one — so a file NAMED as a picture comes back as base64, which every
        /// sandbox's userland has, and is then read for what it IS (`ToolImage.ofBase64`).
        /// The name picks the route and the bytes decide the answer: a `.png` that holds
        /// something else is said to, rather than shown as a picture it is not.
        let readContent (sandbox: SandboxRef) (path: string) : Async<Result<FileContent, string>> =
            async {
                if FileContent.namedAsPicture path then
                    match! run sandbox "a file was read" "base64 < \"$1\"" [ path ] None with
                    | Error reason -> return Error reason
                    | Ok said when said.Code = 0 ->
                        match ToolImage.ofBase64 said.Out with
                        | Ok image -> return Ok (FileContent.Image image)
                        | Error why -> return Error (sprintf "%s is not a picture you can be shown: %s" path why)
                    | Ok said -> return Error (complaint "base64" sandbox said)
                else
                    let! text = read sandbox path
                    return text |> Result.map FileContent.Text
            }

        /// `content` into `path`, whole. In place (`cat >`), so the file keeps its inode and
        /// mode — an executable stays executable — and the directories to it are made, so a
        /// new file in a new directory is one call rather than a refused one and a `mkdir`.
        let put (sandbox: SandboxRef) (path: string) (content: string) : Async<Result<unit, string>> =
            async {
                match!
                    run sandbox "a file was written" "mkdir -p -- \"$(dirname -- \"$1\")\" && cat > \"$1\"" [ path ] (Some content)
                with
                | Error reason -> return Error reason
                | Ok said when said.Code = 0 -> return Ok ()
                | Ok said -> return Error (complaint "cat" sandbox said)
            }

        let record (actor: ActorRef) (sandbox: SandboxRef) (path: string) (change: FileChange) (diff: string option) : Async<unit> =
            async {
                let! _ =
                    log.Append
                        actor
                        (SessionEvent.FileChanged
                            { FileChanged.MessageId = mintMessageId ()
                              FileChanged.Sandbox = sandbox
                              FileChanged.Path = path
                              FileChanged.Change = change
                              FileChanged.Diff = diff
                              FileChanged.Actor = actor })
                return ()
            }

        let write (actor: ActorRef) (sandbox: SandboxRef) (path: string) (content: string) : Async<Result<unit, string>> =
            async {
                match! put sandbox path content with
                | Error reason -> return Error reason
                | Ok () ->
                    do! record actor sandbox path (FileChange.Written (List.length (FileSlice.lines content))) None
                    return Ok ()
            }

        let edit (actor: ActorRef) (request: FileEditRequest) : Async<Result<Edited, string>> =
            async {
                match! read request.Sandbox request.Path with
                | Error reason -> return Error reason
                | Ok content ->
                    match FileEdit.apply content request.OldText request.NewText request.ReplaceAll with
                    | Error failure -> return Error (FileEdit.describe request.Path failure)
                    | Ok edited ->
                        match! put request.Sandbox request.Path edited.Content with
                        | Error reason -> return Error reason
                        | Ok () ->
                            do!
                                record
                                    actor
                                    request.Sandbox
                                    request.Path
                                    (FileChange.Edited (edited.Replaced, edited.LinesRemoved, edited.LinesAdded))
                                    (Some (FileDiff.ofReplacement request.OldText request.NewText))
                            return Ok edited
            }

        /// `grep -rn`, extended patterns, binaries and `.git` skipped. Exit 1 is grep's "no
        /// match" and an answer; 2 is a fault, in grep's words. The glob rides as
        /// `--include=` on the command line rather than in the script, like every argument.
        let search (sandbox: SandboxRef) (pattern: string) (path: string option) (glob: string option) : Async<Result<string, string>> =
            async {
                let args =
                    [ yield! glob |> Option.map (sprintf "--include=%s") |> Option.toList
                      yield "-e"
                      yield pattern
                      yield "--"
                      yield defaultArg path "." ]
                match! run sandbox "files were searched" "grep -r -n -I -E --exclude-dir=.git \"$@\"" args None with
                | Error reason -> return Error reason
                | Ok said when said.Code = 0 || said.Code = 1 -> return Ok said.Out
                | Ok said -> return Error (complaint "grep" sandbox said)
            }

        /// `find`, files only, `.git` pruned, sorted here rather than by `| sort`, whose exit
        /// code would stand in for find's. A glob without a slash is a NAME (`-name`), one
        /// with a slash is a path tail (`-path */glob`) — the two shapes a caller writes,
        /// each sent to the test that means it.
        let find (sandbox: SandboxRef) (glob: string) (path: string option) : Async<Result<string, string>> =
            async {
                let test, wanted = if glob.Contains "/" then "-path", "*/" + glob.TrimStart '/' else "-name", glob
                match!
                    run
                        sandbox
                        "files were looked for"
                        "find \"$1\" -name .git -prune -o -type f \"$2\" \"$3\" -print"
                        [ defaultArg path "."; test; wanted ]
                        None
                with
                | Error reason -> return Error reason
                | Ok said when said.Code = 0 ->
                    return Ok (said.Out.Split '\n' |> Array.filter (fun line -> line <> "") |> Array.sort |> String.concat "\n")
                | Ok said -> return Error (complaint "find" sandbox said)
            }

        /// A path that starts with a variable (`$TMPDIR/x`, `~/x` — `SandboxPath.leadingVariable`),
        /// with the variable's value AS THAT SANDBOX HAS IT: asked of the sandbox by name, with
        /// `printenv`, so nothing is evaluated and the value is the one its terminals see —
        /// `/tmp` in a container, a per-session directory under srt. A variable the sandbox does
        /// not set is a refusal that says so, never a path that quietly starts with `/`.
        let resolve (sandbox: SandboxRef) (path: string) : Async<Result<string, string>> =
            async {
                match SandboxPath.leadingVariable path with
                | None -> return Ok path
                | Some (name, rest) ->
                    match! run sandbox "a path was resolved" "printenv -- \"$1\"" [ name ] None with
                    | Error reason -> return Error reason
                    | Ok said when said.Code = 0 && said.Out.Trim () <> "" -> return Ok (said.Out.TrimEnd ('\n', '\r') + rest)
                    | Ok _ -> return Error (sprintf "%s is not set in the %s sandbox, so %s is not a path there" name (SandboxRef.render sandbox) path)
            }

        /// Every verb takes its path through `resolve` first — one place, so a variable means
        /// the same thing to a read, an edit and a search.
        let resolving (sandbox: SandboxRef) (path: string) (verb: string -> Async<Result<'a, string>>) : Async<Result<'a, string>> =
            async {
                match! resolve sandbox path with
                | Error reason -> return Error reason
                | Ok path -> return! verb path
            }

        let resolvingOptional (sandbox: SandboxRef) (path: string option) (verb: string option -> Async<Result<'a, string>>) =
            match path with
            | None -> verb None
            | Some path -> resolving sandbox path (Some >> verb)

        { SessionFiles.Read = fun sandbox path -> resolving sandbox path (readContent sandbox)
          SessionFiles.Edit =
            fun actor request -> resolving request.Sandbox request.Path (fun path -> edit actor { request with Path = path })
          SessionFiles.Write = fun actor sandbox path content -> resolving sandbox path (fun path -> write actor sandbox path content)
          SessionFiles.Search = fun sandbox pattern path glob -> resolvingOptional sandbox path (fun path -> search sandbox pattern path glob)
          SessionFiles.Find = fun sandbox glob path -> resolvingOptional sandbox path (fun path -> find sandbox glob path) }
