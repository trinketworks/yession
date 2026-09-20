namespace Yession.SessionProcess

open System
open Yession.Domain
open Yession.Domain.Sandboxes
open Yession.Domain.Agent

/// The files inside a sandbox, reached without a terminal: what `read_file` fetches before
/// `FileSlice` cuts the window the caller asked for.
///
/// Reached INSIDE the sandbox, by asking it, for the reason `setProfile` gives: a host-side
/// read would be wrong twice over — under docker the path is in a container this process
/// cannot see, and under srt the sandbox's read scope is not ours. So the bytes come out
/// through one bare spawn of `cat`, with the path as an argv element — never interpolated
/// into a command line, which is the one place this could become a second door around
/// `execute_command`. It is housekeeping (`Direct`), not work: it needs no toolchain and
/// must not pay for one, and it draws no block — the tool-use record is where the read
/// shows, saying which file.
module SessionFiles =

    [<RequireQualifiedAccess>]
    type SessionFiles =
        { /// The whole text of one file, as the sandbox holds it: `path` in the sandbox's own
          /// vocabulary, resolved where a terminal there would resolve it — the shell
          /// profile's directory when one is set, the sandbox's own otherwise.
          Read : SandboxRef -> string -> Async<Result<string, string>> }

    let unavailable : SessionFiles =
        { SessionFiles.Read = fun _ _ -> async { return Error "this session has no sandboxes to read files in" } }

    /// Characters of one file this will carry back before giving up on it. A read is a
    /// window of at most a few thousand lines; a file past this is a build artifact or a
    /// log, and the right answer is to say so rather than hold it whole in memory.
    let maxChars = 4_000_000

    let create
        (environmentFor: SandboxRef -> SessionEnvironment.SessionEnvironment)
        // Where a shell opened in each sandbox starts (Plan 25) — the directory a relative
        // path is meant against, because it is the one a terminal there would use.
        (workingDirectoryFor: SandboxRef -> string option)
        (shell: TerminalShell)
        : SessionFiles =

        let read (sandbox: SandboxRef) (path: string) : Async<Result<string, string>> =
            async {
                let name = SandboxRef.render sandbox
                match! (environmentFor sandbox).Ensure None "a file was read" with
                | EnvironmentUnavailable reason -> return Error reason
                | EnvironmentAvailable ->
                    let text = Text.StringBuilder ()
                    let complaint = Text.StringBuilder ()
                    let mutable overflowed = false
                    let mutable handle : SandboxProcessHandle option = None
                    let! spawned =
                        (environmentFor sandbox).Spawn
                            { Executable = shell.Executable
                              // `--` so a path that starts with `-` is a path, and `$1` so
                              // the path is never part of the script.
                              Arguments = shell.Arguments @ [ "cat -- \"$1\""; "sh"; path ]
                              Env = Map.empty
                              WorkingDirectory = workingDirectoryFor sandbox
                              Via = Direct }
                            (fun (stream, chunk) ->
                                match stream with
                                | Stdout ->
                                    if not overflowed then
                                        text.Append chunk |> ignore
                                        if text.Length > maxChars then
                                            overflowed <- true
                                            handle |> Option.iter (fun h -> h.Kill ())
                                | Stderr -> complaint.Append chunk |> ignore)
                    match spawned with
                    | Error reason -> return Error reason
                    | Ok h ->
                        handle <- Some h
                        // The cap may already have tripped before the handle was in hand.
                        if overflowed then h.Kill ()
                        match! h.Exited with
                        | _ when overflowed ->
                            return
                                Error (
                                    sprintf
                                        "it is longer than %d characters, which is not a file to read as text — reach for a command in the %s sandbox instead"
                                        maxChars
                                        name)
                        | SandboxExited 0 -> return Ok (text.ToString ())
                        | SandboxExited _ ->
                            // `cat`'s own words, which name the fault exactly (no such
                            // file, is a directory, permission denied) — minus its own
                            // name, which says nothing to a caller who asked for a file.
                            let said = complaint.ToString().Trim ()
                            let said = if said.StartsWith "cat: " then said.Substring 5 else said
                            return Error (if said = "" then sprintf "the %s sandbox could not read it" name else said)
                        | SandboxRunFailed reason -> return Error reason
            }

        { SessionFiles.Read = read }
