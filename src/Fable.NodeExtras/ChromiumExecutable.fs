namespace Fable.NodeExtras

// Where Chromium is: the rule, written once for both halves of the repository.
//
// Three readers ask it. The launcher beside it (`Chromium.fs`) starts the browser this names,
// on Node, for the camera and the CDP suite. The browser suite (`tests/Yession.Tests/Browser.fs`)
// hands the path to Playwright, on the .NET CLR. And `tasks.fsx` `#load`s this file for the
// `Chromium` capability's probe — so a box `check` lets ask for it is a box where the launcher
// finds the browser, because the probe asked the same rule and not a copy of it. Copies would
// agree until the day Playwright renamed a directory, and then `check` would admit a box the
// suite cannot launch on, or refuse one it can.
//
// So it is plain F# with nothing referenced: Fable compiles it with the rest of this project,
// and `dotnet fsi` compiles it alone. The rule reads nothing for itself: it is handed what can
// be seen of the machine (`ChromiumMachine`), which is also what lets a cheap test ask it about
// a tree that does not exist. Node's view is the launcher's (`fs`); the .NET view is here,
// once, for the two readers on .NET (`dotnetMachine`), and out of Fable's sight.
//
// The browser comes from `PLAYWRIGHT_BROWSERS_PATH` — nixpkgs' playwright-driver, pinned by the
// same lock as the toolchain (devenv.nix) — and its layout is Playwright's, which differs per
// platform and has changed name across builds:
//
//   x86_64-linux    chrome-linux64/chrome
//   aarch64-linux   chrome-linux/chrome
//   aarch64-darwin  chrome-mac-arm64/Google Chrome for Testing.app/Contents/MacOS/…
//
// So the executable is found by NAME under the `chromium-<revision>` directory, and nothing
// here names a revision: it moves with every Playwright bump, and a stale one would fail as "no
// Chromium" long after the browser arrived. There is deliberately no fallback to a system
// Chrome. The revision is pinned so the browser matches the client driving it; silently
// launching whatever `/usr/bin/google-chrome` happens to be would make a suite's meaning depend
// on the host, which is the opposite of what pinning is for. `CHROMIUM_PATH` is the override for
// someone who means it.

/// What finding Chromium reads of the machine, and nothing more.
type ChromiumMachine =
    { /// An environment variable's value; `None` when it is not set.
      Variable: string -> string option
      /// The names directly inside a directory. It may throw for one that cannot be listed.
      Entries: string -> string list
      /// Whether a path is a directory, following links; `false` when that cannot be told.
      IsDirectory: string -> bool
      /// A path with its links resolved; the path itself when it cannot be.
      Resolve: string -> string
      /// Two paths joined as this platform joins them.
      Join: string -> string -> string }

[<RequireQualifiedAccess>]
module ChromiumExecutable =

    /// What the browser's executable is called: `chrome` on Linux, the app bundle's binary on
    /// macOS.
    let names = set [ "chrome"; "Google Chrome for Testing"; "Chromium" ]

    /// `CHROMIUM_PATH` if set, else the executable found by name under a `chromium-<revision>`
    /// directory of `PLAYWRIGHT_BROWSERS_PATH`, the first revision in order. A variable set to
    /// nothing is a variable not set. What `Error` says is what to set, because the place it is
    /// read is a refusal: `check` refusing the capability, or a launch refusing to start.
    let find (machine: ChromiumMachine) : Result<string, string> =
        let named (name: string) =
            match machine.Variable name with
            | Some "" -> None
            | value -> value

        match named "CHROMIUM_PATH" with
        | Some given -> Ok given
        | None ->
            match named "PLAYWRIGHT_BROWSERS_PATH" with
            | None ->
                Error "no Chromium: PLAYWRIGHT_BROWSERS_PATH is unset (the dev shell sets it); outside it, set CHROMIUM_PATH"
            | Some root ->
                let rec walk (dir: string) : string list =
                    machine.Entries dir
                    |> List.collect (fun name ->
                        let path = machine.Join dir name
                        if machine.IsDirectory path then walk path
                        elif names.Contains name then [ path ]
                        else [])

                // `chromium-*` and not `chromium*`: `chromium_headless_shell-<rev>` sits beside it
                // and is a different, cut-down browser.
                let revisions =
                    if machine.IsDirectory root then
                        machine.Entries root
                        |> List.filter (fun d -> d.StartsWith ("chromium-", System.StringComparison.Ordinal))
                        |> List.sort
                    else []

                match revisions |> List.collect (fun d -> try walk (machine.Resolve (machine.Join root d)) with _ -> []) with
                | found :: _ -> Ok found
                | [] ->
                    Error (sprintf "no Chromium under %s (%d chromium-* revision(s)); set CHROMIUM_PATH" root (List.length revisions))

#if !FABLE_COMPILER
    /// The machine as .NET sees it, for the readers of this rule that are not on Node:
    /// `tasks.fsx`, whose `Chromium` probe asks it before anything is compiled, and the browser
    /// suite on the CLR. Behind `FABLE_COMPILER` because none of System.IO exists under Fable,
    /// and nothing there needs it: on Node the launcher reads the machine through `fs`.
    ///
    /// A directory is listed by name and tested with links followed, so a revision directory
    /// that is a symlink into the store is walked like any other.
    let dotnetMachine : ChromiumMachine =
        { Variable =
            fun name ->
                match System.Environment.GetEnvironmentVariable name with
                | null -> None
                | value -> Some value
          Entries =
            fun dir ->
                System.IO.Directory.EnumerateFileSystemEntries dir
                |> Seq.map (fun entry -> System.IO.Path.GetFileName entry)
                |> List.ofSeq
          IsDirectory = fun path -> System.IO.Directory.Exists path
          Resolve =
            fun path ->
                try
                    match System.IO.Directory.ResolveLinkTarget (path, true) with
                    | null -> path
                    | target -> target.FullName
                with _ ->
                    path
          Join = fun a b -> System.IO.Path.Combine (a, b) }
#endif
