namespace Yession.Domain.Terminals

open Yession.Domain

// Matching a pattern against what a terminal said (Plan 25, step 9).
//
// This exists because the obvious thing is not available. A regular expression here would be
// a JS `RegExp` — the Session is Fable on Node — and JS has no match timeout. A
// catastrophic pattern would not fail a turn, it would wedge the event loop that owns every
// terminal in the session, the Yjs document and the WebRTC pump. .NET's `Regex` takes a
// `MatchTimeout`; the runtime this actually runs on does not. So "use a regex and bound it"
// is not an option, and that — rather than any squeamishness about what a model might write —
// is why the first cut of `wait_for` took literal text only.
//
// What IS available is a matcher that cannot blow up, rather than one we hope will not.
// Thompson's construction compiles the pattern to a small instruction list and simulates ALL
// live states at once, so the cost is O(pattern x input) with no backtracking and no stack.
// `(a+)+$` against a long run of `a` is the case that hangs a backtracking engine and is
// merely a match here; there is a test that says so.
//
// The subset is small on purpose. Backreferences cannot be simulated this way at all, and
// lookaround and non-greedy quantifiers could be but would each buy one more thing a caller
// has to learn. They are refused BY NAME, so the subset is learnable rather than guessable.

open System

/// The instruction set. `PatternSplit` is the only branch, and the reason the simulation
/// stays linear: it does not choose, it runs both.
type internal PatternInst =
    | PatternChar of (char -> bool)
    /// Start of the input or just after a newline — a console is lines, so an anchor that
    /// meant "the whole transcript" would be an anchor nobody could use.
    | PatternLineStart
    /// End of the input or just before a newline.
    | PatternLineEnd
    /// Start of the input only.
    | PatternTextStart
    /// End of the input only.
    | PatternTextEnd
    | PatternSplit of int * int
    | PatternJmp of int
    | PatternMatch

/// What `^` and `$` bind to. A console is lines, so a terminal wait anchors to a line. A form
/// field's value is one answer, so a check on it anchors to the whole text, as a `RegExp`
/// does: `^\d{5}$` must not pass because the value's second line is five digits.
type PatternAnchors =
    | LineAnchors
    | TextAnchors

/// One compiled pattern. Opaque: what a caller does with it is ask whether text matches.
type Pattern = internal { Program : PatternInst list }

module Pattern =

    /// The largest count `{n,m}` takes. A count is written out in full, so this bounds what
    /// one quantifier can add to the program.
    [<Literal>]
    let MaxCount = 1000

    /// The most instructions a compiled pattern may have. Counts multiply when they nest, so
    /// the pattern's length alone does not bound the program; this does.
    [<Literal>]
    let MaxProgram = 2048

    // --- parsing ----------------------------------------------------------------------
    //
    // A hand-written recursive descent over the subset. Small enough to read in one sitting,
    // which matters more here than elsewhere: this is the code that decides what a caller is
    // allowed to say.

    type private Node =
        | NEmpty
        | NChar of (char -> bool)
        | NLineStart
        | NLineEnd
        | NConcat of Node * Node
        | NAlt of Node * Node
        | NStar of Node
        | NPlus of Node
        | NOpt of Node
        | NRepeat of Node * int * int option

    /// What a backslash and the character after it mean, inside a class or out.
    type private Escape =
        | EscapedChar of char
        | EscapedSet of (char -> bool)

    let private isDigit (c: char) = c >= '0' && c <= '9'
    let private isWord (c: char) = Char.IsLetterOrDigit c || c = '_'

    /// A letter or digit outside the known escapes is refused rather than read as itself. In
    /// a `RegExp`, `\b` is a word boundary; reading it as the letter b would match something
    /// else entirely and say nothing.
    let private escape (letter: char) : Result<Escape, string> =
        match letter with
        | 'n' -> Ok (EscapedChar '\n')
        | 'r' -> Ok (EscapedChar '\r')
        | 't' -> Ok (EscapedChar '\t')
        | 'd' -> Ok (EscapedSet isDigit)
        | 'D' -> Ok (EscapedSet (fun c -> not (isDigit c)))
        | 'w' -> Ok (EscapedSet isWord)
        | 'W' -> Ok (EscapedSet (fun c -> not (isWord c)))
        | 's' -> Ok (EscapedSet Char.IsWhiteSpace)
        | 'S' -> Ok (EscapedSet (fun c -> not (Char.IsWhiteSpace c)))
        | c when Char.IsLetterOrDigit c -> Error (sprintf "the escape `\\%c`" c)
        | c -> Ok (EscapedChar c)

    /// What a construct outside the subset is called, so a refusal can name it. A caller told
    /// "unsupported pattern" has to guess which part; one told "a backreference" has learned
    /// the rule.
    let private named (pattern: string) (at: int) : string option =
        let peek offset = if at + offset < pattern.Length then Some pattern.[at + offset] else None
        match pattern.[at], peek 1 with
        | '(', Some '?' ->
            match peek 2 with
            | Some '=' | Some '!' -> Some "a lookahead"
            | Some '<' -> Some "a lookbehind"
            | _ -> Some "an extended group"
        | '\\', Some d when isDigit d -> Some "a backreference"
        | _ -> None

    let private classAt (pattern: string) (start: int) : Result<(char -> bool) * int, string> =
        // `[^abc]`, `[a-z0-9_]`, `[\d.]`. A closing bracket immediately after the caret or the
        // opening is a literal `]`, which is the one piece of folklore worth keeping: `[]]` is
        // a bracket, not an empty class.
        let mutable at = start + 1
        let negated = at < pattern.Length && pattern.[at] = '^'
        if negated then at <- at + 1
        let members = ResizeArray<char -> bool> ()
        let mutable first = true
        let mutable closed = false
        let mutable failure = None
        let single (c: char) =
            if at + 1 < pattern.Length && pattern.[at] = '-' && pattern.[at + 1] <> ']' then
                let high = pattern.[at + 1]
                members.Add (fun ch -> ch >= c && ch <= high)
                at <- at + 2
            else members.Add (fun ch -> ch = c)
        while not closed && failure.IsNone && at < pattern.Length do
            if pattern.[at] = ']' && not first then
                closed <- true
                at <- at + 1
            else
                first <- false
                if pattern.[at] = '\\' && at + 1 < pattern.Length then
                    let escaped = escape pattern.[at + 1]
                    at <- at + 2
                    match escaped with
                    | Error reason -> failure <- Some reason
                    | Ok (EscapedSet predicate) -> members.Add predicate
                    | Ok (EscapedChar c) -> single c
                else
                    let c = pattern.[at]
                    at <- at + 1
                    single c
        match failure with
        | Some reason -> Error reason
        | None when not closed -> Error "a character class that is never closed"
        | None ->
            let inside (c: char) = members |> Seq.exists (fun test -> test c)
            Ok ((fun c -> if negated then not (inside c) else inside c), at)

    /// `{n}`, `{n,}` or `{n,m}` starting at `start`, with where it ends. Anything else that
    /// starts with a brace is not a count, and the brace is the character itself, as a
    /// `RegExp` reads it.
    let private countAt (pattern: string) (start: int) : (int * int option * int) option =
        let digitsFrom (from: int) =
            let mutable next = from
            let mutable value = 0
            while next < pattern.Length && isDigit pattern.[next] do
                // Saturated, so a long run of digits is "above the cap", not an overflow.
                value <- min (value * 10 + (int pattern.[next] - int '0')) 100000
                next <- next + 1
            if next = from then None else Some (value, next)
        let closesAt (at: int) = at < pattern.Length && pattern.[at] = '}'
        match digitsFrom (start + 1) with
        | None -> None
        | Some (low, afterLow) when closesAt afterLow -> Some (low, Some low, afterLow + 1)
        | Some (low, afterLow) when afterLow < pattern.Length && pattern.[afterLow] = ',' ->
            if closesAt (afterLow + 1) then Some (low, None, afterLow + 2)
            else
                digitsFrom (afterLow + 1)
                |> Option.filter (fun (_, afterHigh) -> closesAt afterHigh)
                |> Option.map (fun (high, afterHigh) -> (low, Some high, afterHigh + 1))
        | Some _ -> None

    let private countFault (node: Node) : string option =
        match node with
        | NRepeat (_, low, high) when low > MaxCount || high |> Option.exists (fun most -> most > MaxCount) ->
            Some (sprintf "a count above %d" MaxCount)
        | NRepeat (_, low, Some most) when low > most -> Some "a count whose minimum is above its maximum"
        | _ -> None

    let private parse (pattern: string) : Result<Node, string> =
        let mutable at = 0
        let mutable failure : string option = None

        let fail reason =
            if failure.IsNone then failure <- Some reason
            NEmpty

        let rec alternation () =
            let left = sequence ()
            if failure.IsSome then left
            elif at < pattern.Length && pattern.[at] = '|' then
                at <- at + 1
                NAlt (left, alternation ())
            else left

        and sequence () =
            let mutable node = NEmpty
            let mutable stop = false
            while not stop && failure.IsNone && at < pattern.Length && pattern.[at] <> '|' && pattern.[at] <> ')' do
                let piece = repeated ()
                node <- (match node with NEmpty -> piece | existing -> NConcat (existing, piece))
                if at >= pattern.Length then stop <- true
            node

        and repeated () =
            let atom' = atom ()
            if failure.IsSome then atom'
            else
                let mutable node = atom'
                let mutable more = true
                while more && failure.IsNone && at < pattern.Length do
                    let quantified =
                        match pattern.[at] with
                        | '*' -> Some (NStar node, at + 1)
                        | '+' -> Some (NPlus node, at + 1)
                        | '?' -> Some (NOpt node, at + 1)
                        | '{' ->
                            countAt pattern at
                            |> Option.map (fun (low, high, next) -> (NRepeat (node, low, high), next))
                        | _ -> None
                    match quantified with
                    | None -> more <- false
                    | Some (q, next) ->
                        at <- next
                        countFault q |> Option.iter (fail >> ignore)
                        // `a*?` and friends. Simulable, but each one is another rule a caller
                        // has to know, and greedy-only is the smaller promise to keep.
                        if at < pattern.Length && pattern.[at] = '?' then
                            fail "a non-greedy quantifier (`*?`, `+?`, `??`)" |> ignore
                        node <- q
                node

        and atom () =
            if at >= pattern.Length then NEmpty
            else
                match named pattern at with
                | Some construct -> fail construct
                | None ->
                    match pattern.[at] with
                    | '(' ->
                        at <- at + 1
                        let inner = alternation ()
                        if at < pattern.Length && pattern.[at] = ')' then
                            at <- at + 1
                            inner
                        else fail "a group that is never closed"
                    | ')' -> fail "a `)` with nothing open"
                    | '[' ->
                        match classAt pattern at with
                        | Ok (predicate, next) ->
                            at <- next
                            NChar predicate
                        | Error reason -> fail reason
                    | '.' ->
                        at <- at + 1
                        // Not newlines: a console is lines, and a `.` that ate them would make
                        // every pattern match halfway down the screen.
                        NChar (fun c -> c <> '\n')
                    | '^' ->
                        at <- at + 1
                        NLineStart
                    | '$' ->
                        at <- at + 1
                        NLineEnd
                    | '\\' when at + 1 < pattern.Length ->
                        let escaped = escape pattern.[at + 1]
                        at <- at + 2
                        match escaped with
                        | Ok (EscapedChar c) -> NChar (fun ch -> ch = c)
                        | Ok (EscapedSet predicate) -> NChar predicate
                        | Error reason -> fail reason
                    | '\\' -> fail "a trailing backslash"
                    | c ->
                        at <- at + 1
                        NChar (fun ch -> ch = c)

        let node = alternation ()
        match failure with
        | Some reason -> Error reason
        | None when at < pattern.Length -> Error "a `)` with nothing open"
        | None -> Ok node

    // --- compiling --------------------------------------------------------------------

    /// How many instructions `node` compiles to, saturated well above `MaxProgram` so nested
    /// counts cannot overflow the arithmetic that refuses them.
    let private programSize (node: Node) : int =
        let capped (n: int) = min n 100000
        let rec size node =
            match node with
            | NEmpty -> 0
            | NChar _ | NLineStart | NLineEnd -> 1
            | NConcat (a, b) -> capped (size a + size b)
            | NAlt (a, b) -> capped (size a + size b + 2)
            | NStar inner -> capped (size inner + 2)
            | NPlus inner | NOpt inner -> capped (size inner + 1)
            | NRepeat (inner, low, high) ->
                let one = size inner
                let rest =
                    match high with
                    | None -> one + 2
                    | Some most -> (most - low) * (one + 1)
                capped (low * one + rest)
        size node + 1

    let private compileNode (anchors: PatternAnchors) (node: Node) : PatternInst list =
        let program = ResizeArray<PatternInst> ()
        let emit inst =
            program.Add inst
            program.Count - 1
        let start = match anchors with LineAnchors -> PatternLineStart | TextAnchors -> PatternTextStart
        let finish = match anchors with LineAnchors -> PatternLineEnd | TextAnchors -> PatternTextEnd
        let rec go node =
            match node with
            | NEmpty -> ()
            | NChar predicate -> emit (PatternChar predicate) |> ignore
            | NLineStart -> emit start |> ignore
            | NLineEnd -> emit finish |> ignore
            | NConcat (a, b) ->
                go a
                go b
            | NAlt (a, b) ->
                let split = emit (PatternSplit (0, 0))
                go a
                let jmp = emit (PatternJmp 0)
                let right = program.Count
                go b
                program.[split] <- PatternSplit (split + 1, right)
                program.[jmp] <- PatternJmp program.Count
            | NStar inner ->
                let split = emit (PatternSplit (0, 0))
                go inner
                emit (PatternJmp split) |> ignore
                program.[split] <- PatternSplit (split + 1, program.Count)
            | NPlus inner ->
                let start = program.Count
                go inner
                let split = emit (PatternSplit (0, 0))
                program.[split] <- PatternSplit (start, program.Count)
            | NOpt inner ->
                let split = emit (PatternSplit (0, 0))
                go inner
                program.[split] <- PatternSplit (split + 1, program.Count)
            | NRepeat (inner, low, high) ->
                // Written out: the required copies, then either a star or one optional copy
                // per extra. Still a Thompson program, so still linear to run.
                for _ in 1 .. low do go inner
                match high with
                | None -> go (NStar inner)
                | Some most -> for _ in low + 1 .. most do go (NOpt inner)
        go node
        emit PatternMatch |> ignore
        List.ofSeq program

    /// The longest pattern that will be accepted. A bound rather than a judgement: the
    /// simulation is linear in it, so this caps the work per input character at something a
    /// person can reason about.
    [<Literal>]
    let MaxLength = 512

    /// Compile a pattern with `^` and `$` bound as `anchors` says, or say — by name — which
    /// construct put it outside the subset.
    let compileAnchored (anchors: PatternAnchors) (pattern: string) : Result<Pattern, string> =
        if String.IsNullOrEmpty pattern then Error "an empty pattern"
        elif pattern.Length > MaxLength then
            Error (sprintf "a pattern longer than %d characters" MaxLength)
        else
            match parse pattern with
            | Error construct ->
                Error (
                    sprintf
                        "%s is not something this matcher supports. It takes literal text, `.`, `[classes]`, the escapes \\d \\w \\s and \\D \\W \\S, `*`, `+`, `?`, counts {n} {n,} {n,m}, `|`, groups and the anchors ^ and $"
                        construct
                )
            | Ok node when programSize node > MaxProgram ->
                Error (sprintf "a pattern whose counts write out more than %d steps. Use smaller counts" MaxProgram)
            | Ok node -> Ok { Program = compileNode anchors node }

    /// Compile a pattern whose anchors bind to a line, as a terminal wait reads them.
    let compile (pattern: string) : Result<Pattern, string> = compileAnchored LineAnchors pattern

    // --- running ----------------------------------------------------------------------

    /// What a linear simulation cannot exceed, with room to spare: every input position visits
    /// each instruction at most once, so `program x input` bounds the honest cost and anything
    /// past a small multiple of it is not this algorithm running slowly — it is a different
    /// algorithm.
    ///
    /// This is a guard against OUR bug, not a caller's. The subset cannot express
    /// catastrophic backtracking, so a correct implementation never comes near this; an
    /// implementation that regressed to trying branches one at a time would run for ever, and
    /// on Node "for ever" means the event loop that owns every terminal in the session, the
    /// Yjs document and the WebRTC pump. A wall-clock timeout cannot save that — a
    /// synchronous loop is exactly what stops a timer firing, which is why `RegExp` has no
    /// timeout here either. A step budget is the only bound that can actually be enforced.
    let private budgetFor (program: int) (input: int) = (program * (input + 1) * 4) + 1024

    /// Does `text` contain a match, or — if the matcher somehow exceeded its budget — why it
    /// stopped instead of answering.
    ///
    /// Every live thread is stepped together over one pass of the input, which is what makes
    /// this linear: a `Split` adds both branches to the same set rather than trying one and
    /// coming back. A state already in the set is not added twice, so the set cannot outgrow
    /// the program however the pattern nests.
    let matchesWithin (budget: int) (compiled: Pattern) (text: string) : Result<bool, string> =
        let program = Array.ofList compiled.Program
        let count = program.Length
        let mutable steps = 0
        let mutable exhausted = false
        let current = Array.zeroCreate<bool> count
        let next = Array.zeroCreate<bool> count
        let mutable matched = false

        let rec addThread (states: bool[]) (pc: int) (at: int) =
            steps <- steps + 1
            if steps > budget then exhausted <- true
            elif pc < count && not states.[pc] then
                states.[pc] <- true
                match program.[pc] with
                | PatternJmp target -> addThread states target at
                | PatternSplit (a, b) ->
                    addThread states a at
                    addThread states b at
                | PatternLineStart ->
                    if at = 0 || text.[at - 1] = '\n' then addThread states (pc + 1) at
                | PatternLineEnd ->
                    if at = text.Length || text.[at] = '\n' then addThread states (pc + 1) at
                | PatternTextStart ->
                    if at = 0 then addThread states (pc + 1) at
                | PatternTextEnd ->
                    if at = text.Length then addThread states (pc + 1) at
                | PatternMatch -> matched <- true
                | PatternChar _ -> ()

        let mutable at = 0
        // Seeded at every position, which is what makes this "contains a match" rather than
        // "the whole text is one". An anchored pattern rejects the positions it does not like
        // on its own, so there is no second code path for `^`.
        addThread current 0 0
        while not matched && not exhausted && at < text.Length do
            Array.fill next 0 count false
            for pc in 0 .. count - 1 do
                if current.[pc] && not exhausted then
                    match program.[pc] with
                    | PatternChar predicate when predicate text.[at] -> addThread next (pc + 1) (at + 1)
                    | _ -> ()
            Array.blit next 0 current 0 count
            at <- at + 1
            if not matched then addThread current 0 at
        if exhausted then
            Error
                "this matcher did more work than a linear match can take. The fault is in the matcher, not in the pattern"
        else Ok matched

    /// The ordinary way to ask, with the budget a correct simulation cannot reach.
    let matches (compiled: Pattern) (text: string) : Result<bool, string> =
        matchesWithin (budgetFor (List.length compiled.Program) text.Length) compiled text
