module Yession.Tests.PlaywrightWaits

// Where a browser case waits: through `Wait` (`BrowserWait.fs`), and nowhere else.
//
// A wait through `Wait` asks the case's refusal every time it looks, so a page that says its
// session's work sandbox did not start ends it at once, quoting why. It races its own
// deadline, so a call into a page that never returns cannot hold it. And its red names what
// the case waited FOR, and what the page last showed. Playwright's own waits do none of that:
// a raw `WaitForSelectorAsync` sits out its thirty seconds beside a page that already says
// why, and then names a selector. The browser suite once made 238 of them.
//
// A scan of the source, not an analyzer: Playwright's member names are its own, and what is
// refused is the member a line reaches for (a `.` and the name), which reads the same in the
// text as in the typed tree. A line that is a comment is passed over, so a comment may still
// name what it is about. A banned name in a string literal is reported, which is loud and
// cheap to reword; a call is never missed for being in one.
//
// The files are the ones git would commit under `tests/`, read the way `RepoLayout` reads its
// tree: one somebody has just made and not yet added is the one this exists to catch.
//
// No capability: it reads files.

open Fable.NodeExtras
open Fable.Pyxpecto

/// The file that may call Playwright's waits: `Wait` is built there, over Playwright's reads.
let private home = "tests/Yession.Tests/BrowserWait.fs"

/// What a case calls in place of one of Playwright's own waits, or `None` for a member that is
/// not one. Each of these polls a STATE of the page until it holds, which is `Wait.until` over a
/// typed read, without the refusal, the deadline or the name.
let private insteadOf (name: string) : string option =
    match name with
    | "WaitForSelectorAsync"
    | "WaitForAsync"
    | "WaitForElementStateAsync" ->
        Some
            "`waitVisible what page selector` (Browser.fs) for an element to come on screen, which is the state Playwright waits for by default; `waitPresent`, `waitAbsent` or `waitCount` for the others. Then `page.Locator selector` for the element, never the handle Playwright's wait returned"
    | "WaitForFunctionAsync" ->
        Some
            "`Wait.until what read holds`: read the page through `Read` or a `Probe`, and judge what was read in F# rather than in a JavaScript string"
    | "WaitForURLAsync"
    | "WaitForNavigationAsync" ->
        Some "`Wait.until what (fun () -> Read.url page) holds`: where the page went is a state a read finds"
    | "WaitForLoadStateAsync" ->
        Some
            "`waitVisible` for what the loaded page shows: `GotoAsync` and `ReloadAsync` already wait for the load, and what the case needs next is on the page"
    | "WaitForTimeoutAsync" ->
        Some
            "a `Wait` for whatever the pause was for. A pause is a guess at how long something takes, and the guess is wrong on the next slower box"
    // Playwright's assertions (`Assertions.Expect(...).ToBeVisibleAsync ()` and the rest), which
    // retry until they hold: a wait under another name.
    | _ when
        name.EndsWith "Async"
        && (name.StartsWith "ToBe" || name.StartsWith "ToHave" || name.StartsWith "ToContain" || name.StartsWith "ToMatch")
        ->
        Some "`Wait.until what read holds` over a typed read, then `Expect` on what was read"
    | _ -> None

/// Playwright's waits that stay, and why. Each waits for an EVENT, which happens once and leaves
/// nothing behind for a read to find afterwards, where `Wait` polls a state. The `RunAndWaitFor`
/// form of each (the action and the event together) stays for the same reason.
let private allowed : (string * string) list =
    [ "WaitForRequestAsync", "a request the page sends"
      "WaitForResponseAsync", "an answer the page is sent"
      "WaitForRequestFinishedAsync", "a request the page finished"
      "WaitForConsoleMessageAsync", "a line the page writes to its console"
      "WaitForDownloadAsync", "a download the page starts"
      "WaitForPopupAsync", "a window the page opens"
      "WaitForPageAsync", "a page the context opens"
      "WaitForFileChooserAsync", "a file chooser the page opens"
      "WaitForWebSocketAsync", "a socket the page opens"
      "WaitForWorkerAsync", "a worker the page starts" ]

/// One call this refuses: where it is, and what it reached for.
type private Found = { File : string; Line : int; Member : string; Instead : string }

/// The code on a line: none of it when the line is a comment, and the part before a comment
/// that follows the code. A `//` counts as a comment's start only after a space, so the `//` of
/// a URL in a string is not taken for one.
let private codeOf (line: string) : string =
    if line.TrimStart().StartsWith "//" then ""
    else
        match line.IndexOf " //" with
        | -1 -> line
        | at -> line.Substring (0, at)

/// Every member a line reaches for, by name: each identifier written straight after a `.`.
let private membersOn (line: string) : string list =
    System.Text.RegularExpressions.Regex.Matches (codeOf line, "\\.([A-Za-z_][A-Za-z0-9_]*)")
    |> Seq.map (fun found -> found.Groups.[1].Value)
    |> List.ofSeq

/// The refused calls in one file's text.
let private scan (file: string) (text: string) : Found list =
    text.Split '\n'
    |> Array.toList
    |> List.indexed
    |> List.collect (fun (index, line) ->
        membersOn line
        |> List.choose (fun name ->
            insteadOf name
            |> Option.map (fun instead -> { File = file; Line = index + 1; Member = name; Instead = instead })))

/// The F# files git would commit under `tests/`, as paths from the repository's root. A run that
/// cannot list them has checked nothing, so it fails by name; so does a listing with nothing in
/// it, which is a listing that broke rather than a tree that obeys.
let private testSources : Lazy<string * string list> =
    lazy (
        let root =
            try
                (execFileSync "git" [ "rev-parse"; "--show-toplevel" ] SyncOptions.none).Trim ()
            with e ->
                failwithf "this check needs a git checkout, and proves nothing without one: %s" e.Message
        let listed =
            try
                execFileSync
                    "git"
                    [ "ls-files"; "-z"; "--cached"; "--others"; "--exclude-standard"; "--"; "tests" ]
                    { SyncOptions.none with Cwd = Some root; MaxBuffer = Some (32 * 1024 * 1024) }
            with e ->
                failwithf "could not list the files under %s/tests: %s" root e.Message
        let files =
            listed.Split [| '\u0000' |]
            |> Array.filter (fun path -> path.EndsWith ".fs")
            |> Array.toList
            |> List.distinct
        if List.isEmpty files then
            failwithf "git listed no F# file under %s/tests; a rule over no file is green and proves nothing" root
        root, files)

let tests =
    testList "Playwright's own waits" [

        testCase "no file under tests/ but BrowserWait.fs calls a Playwright wait that Wait already is" <| fun () ->
            let root, files = testSources.Value
            let found =
                files
                |> List.filter (fun path -> path <> home)
                // A file git lists that is gone from the disk (deleted, not yet staged) holds no
                // call: it has no text to read.
                |> List.filter (fun path -> TestFiles.exists (root + "/" + path))
                |> List.collect (fun path -> scan path (TestFiles.read (root + "/" + path)))
            Expect.isEmpty
                (found |> List.map (fun f -> sprintf "%s:%d" f.File f.Line))
                (sprintf
                    "%s\n\nA browser case waits through `Wait` (tests/Yession.Tests/BrowserWait.fs). It asks the case's refusal each time it looks, so a page that says its session's work sandbox did not start ends it at once with the reason; it keeps its own deadline whatever a call into the page does; and its red names what the case waited for and what the page last showed. Playwright's own waits do none of that. The waits of Playwright's that may stay are the ones for an EVENT, which no read can find afterwards: %s, and the `RunAndWaitFor` form of each"
                    (found
                     |> List.map (fun f -> sprintf "%s:%d calls %s. Use %s." f.File f.Line f.Member f.Instead)
                     |> String.concat "\n")
                    (allowed |> List.map (fun (name, what) -> sprintf "%s (%s)" name what) |> String.concat ", "))

        // The scan is what the rule above stands on, and a scan that stopped seeing calls finds
        // none in any tree, which reads exactly like a tree that obeys. The call is spelled in
        // two halves, so this file holds no call for the rule to find in it.
        testCase "the scan finds a raw wait on a line of code" <| fun () ->
            let found = scan "a case" ("        let! _ = await (page." + "WaitForSelectorAsync \"#shell\")")
            Expect.equal
                (found |> List.map (fun f -> f.Line, f.Member))
                [ 1, "WaitForSelectorAsync" ]
                "the scan reports the call, on its line; if it does not, `membersOn` in PlaywrightWaits.fs stopped seeing member calls"

        testCase "the scan passes over a comment that names a wait" <| fun () ->
            let found = scan "a case" "    // `page.WaitForSelectorAsync sel` never asks the refusal"
            Expect.isEmpty
                (found |> List.map (fun f -> f.Member))
                "a comment may say what the rule is about; if this fails, `codeOf` in PlaywrightWaits.fs stopped telling a comment from code"

        // The population: the files the rule is about are among the ones it reads. A listing that
        // stopped reaching them checks nothing, and is green.
        testCase "the scan reads the browser suite's files" <| fun () ->
            let _, files = testSources.Value
            let missing =
                [ home; "tests/Yession.Tests/Browser.fs"; "tests/Yession.Tests/Bench.fs" ]
                |> List.filter (fun path -> not (List.contains path files))
            Expect.isEmpty
                missing
                "the scan did not list these. If they moved, change the paths in PlaywrightWaits.fs; if they are still there, the listing (git ls-files under tests/) stopped seeing them, and the rule above proves nothing"
    ]
