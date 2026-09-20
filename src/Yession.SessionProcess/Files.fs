namespace Yession.SessionProcess

open System
open Yession.Domain
open Yession.Domain.Sandboxes
open Yession.Domain.Agent
open Yession.Domain.Files

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
        { /// The whole text of one file, as the sandbox holds it: `path` in the sandbox's own
          /// vocabulary, resolved where a terminal there would resolve it — the shell
          /// profile's directory when one is set, the sandbox's own otherwise.
          Read : SandboxRef -> string -> Async<Result<string, string>>
          /// One exact-string edit (`FileEdit.apply`), read-apply-write as ONE verb: a caller
          /// that could write without having read is a caller that can put back a file
          /// somebody else changed in between. The write happens only when the edit applied.
          Edit : FileEditRequest -> Async<Result<Edited, string>>
          /// The whole file, replaced — or created, along with the directories to it.
          Write : SandboxRef -> string -> string -> Async<Result<unit, string>>
          /// `grep -rn` under a directory, the sandbox's own lines back; no match is `Ok ""`.
          Search : SandboxRef -> string -> string option -> string option -> Async<Result<string, string>>
          /// `find` under a directory for a name glob, one path per line, sorted.
          Find : SandboxRef -> string -> string option -> Async<Result<string, string>> }

    let unavailable : SessionFiles =
        { SessionFiles.Read = fun _ _ -> async { return Error "this session has no sandboxes to read files in" }
          SessionFiles.Edit = fun _ -> async { return Error "this session has no sandboxes to edit files in" }
          SessionFiles.Write = fun _ _ _ -> async { return Error "this session has no sandboxes to write files in" }
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

        /// `content` into `path`, whole. In place (`cat >`), so the file keeps its inode and
        /// mode — an executable stays executable — and the directories to it are made, so a
        /// new file in a new directory is one call rather than a refused one and a `mkdir`.
        let write (sandbox: SandboxRef) (path: string) (content: string) : Async<Result<unit, string>> =
            async {
                match!
                    run sandbox "a file was written" "mkdir -p -- \"$(dirname -- \"$1\")\" && cat > \"$1\"" [ path ] (Some content)
                with
                | Error reason -> return Error reason
                | Ok said when said.Code = 0 -> return Ok ()
                | Ok said -> return Error (complaint "cat" sandbox said)
            }

        let edit (request: FileEditRequest) : Async<Result<Edited, string>> =
            async {
                match! read request.Sandbox request.Path with
                | Error reason -> return Error reason
                | Ok content ->
                    match FileEdit.apply content request.OldText request.NewText request.ReplaceAll with
                    | Error failure -> return Error (FileEdit.describe request.Path failure)
                    | Ok edited ->
                        match! write request.Sandbox request.Path edited.Content with
                        | Error reason -> return Error reason
                        | Ok () -> return Ok edited
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

        { SessionFiles.Read = read
          SessionFiles.Edit = edit
          SessionFiles.Write = write
          SessionFiles.Search = search
          SessionFiles.Find = find }
