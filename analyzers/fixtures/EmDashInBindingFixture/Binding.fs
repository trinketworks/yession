module EmDashInBindingFixture.Binding

// The exemption, declared here by name as the product declares `Yession.App.HarnessAttribute`.
[<System.AttributeUsage(System.AttributeTargets.Class)>]
type HarnessAttribute () =
    inherit System.Attribute ()

// An em dash in a comment is not a literal — never reported.

let plain = "Saved — reload to see it"
let verbatim = @"Saved — reload to see it"
let tripleQuoted = """Saved — reload to see it"""
let format = sprintf "%s — still not true: %s" "a" "b"
let interpolated (n: int) = $"Saved {n} files — reload to see them"
let spacedEnDash = "Saved – reload to see it"
let twoOnOneLine = "one — two — three"

/// One report per line, on the line the dash is on.
let multiLine =
    """First line is fine.
Second line is not — it joins two clauses.
Third line is fine."""

let inPattern (s: string) =
    match s with
    | "on — off" -> true
    | _ -> false

[<System.Obsolete("Old — use the new one")>]
let obsolete = 1

// What is left alone.

let split = "Saved. Reload to see it."
let colon = "Saved: reload to see it"
let range = "Pages 1–3"
let unspacedEnDash = "a –b"

/// An HTML comment is for a reader of the source, across the template's holes too.
let template (n: int) =
    $"""
    <!-- Why this markup is the way it is — a note for the next author,
         across a hole {n} — and still a comment. -->
    <p>Count: {n}</p>
    """

/// A line that starts with `//` is a comment in the inline script the template carries.
let inlineScript =
    """
    <script>
      // Run once — the page is ready by now.
      run();
    </script>
    """

/// After the HTML comment closes, the text is copy again.
let afterComment =
    """<!-- a note — fine -->
    <p>Copy — not fine</p>
    """

/// A justified exception: the analyzer SDK's marker silences this one line.
let pinned = "Saved — reload" // fsharpanalyzer: ignore-line YES018

/// A test harness module is not product, and says so.
[<Harness>]
module Fixtures =
    let data = "check the phone layout too — the pane was clipped"

/// Inside a computation expression, under a `match!`.
let waited (work: Async<unit>) =
    async {
        match! Async.Catch work with
        | Choice1Of2 () -> return ()
        | Choice2Of2 e -> return failwithf "%s — still not true" e.Message
    }

/// Where the SDK's syntax walker does not look: an anonymous record and an assignment.
let anonymous = {| Text = "Saved — reload" |}

let mutable status = ""
let assign () = status <- "Saved — reload"

/// Both sides of an `#if`: the product runs as the Fable one.
let branch =
#if FABLE_COMPILER
    "Fable side — reported"
#else
    ".NET side — reported"
#endif

/// A literal inside a hole is a literal of its own.
let nested (n: int) = $"""<!-- note: {(if n > 0 then "one — two" else "")} --> ok"""
