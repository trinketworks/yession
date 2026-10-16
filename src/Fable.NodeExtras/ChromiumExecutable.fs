namespace Fable.NodeExtras

// Where Chromium is: the rule, written once for both halves of the repository.
//
// The launcher beside it (`Chromium.fs`) starts the browser this names, on Node, for the camera
// and the CDP suite. `tasks.fsx` `#load`s this file, on .NET, for the `Chromium` capability's
// probe — so a box `check` lets ask for it is a box where the launcher finds the browser,
// because the probe asked the same rule and not a copy of it. A copy would agree with this
// until the day Playwright renamed a directory, and then `check` would admit a box the suite
// cannot launch on, or refuse one it can.
//
// So it is plain F# with nothing referenced: Fable compiles it with the rest of this project,
// and `dotnet fsi` compiles it alone. It reads nothing for itself. Each side hands in what it
// can see of the machine (`ChromiumMachine`) — Node's `fs` there, System.IO here — which is
// also what lets a cheap test ask it about a tree that does not exist.
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
// here names a revision: it moves with every Playwright bump. There is no fallback to a system
// Chrome, for the reason the browser suite gives (`tests/Yession.Tests/Browser.fs`, which finds
// it the same way through Playwright's .NET driver): the revision is pinned so that what is
// driven is what the lock says, not whatever the host has. `CHROMIUM_PATH` is the override for
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
