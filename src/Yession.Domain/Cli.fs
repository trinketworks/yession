module Yession.Domain.Cli

// What each bin accepts on its command line, declared as data and parsed here.
//
// It replaces hand-rolled `process.argv` scanning, which got two things wrong that matter
// more than the parsing itself:
//
//   - An unknown option was SILENTLY IGNORED. `yession-manager --auht localhost` ran with
//     no strategy at all, which is deny-everything — a typo that looks like a hang. It is
//     refused now, and named.
//   - A bad value threw at module init, inside Fable's async, and surfaced as
//     `UnhandledPromiseRejection ... "[object Object]"` — technically a refused boot, but
//     nothing an operator could act on. `outcome` answers with the reason and the usage
//     instead, and the bin says it before it stops.
//
// --- why the parsing is F# rather than a library's or Node's -------------------------------
//
// Argu is the F# answer on .NET and cannot be the answer here, which was checked rather than
// assumed: `Argu.6.2.5.nupkg` holds `lib/netstandard2.0/Argu.dll` and nothing else, and Fable
// compiles from F# SOURCE — a package it can use ships its source files in a `fable/` folder.
// Its DU-plus-attributes model leans on reflection besides. The only Fable-native CLI parser
// published at all is `OrigamiTower.Mei`: one 0.1.0, from 2018, against `Fable.Core 2.0.1`,
// three majors behind this repository.
//
// So the choice was between Node's own `parseArgs` (`node:util`) and this, and what decides it
// is WHERE this file lives. `Yession.Domain` is compiled by Fable for the browser client too,
// and it references no Node bindings; an `[<Import("parseArgs", "node:util")>]` in it was a
// Node API named in the one project that must not name one, surviving only because bundling
// tree-shook it away. Everything that reached for it was interop in kind — an `[<EmitIndexer>]`
// over `undefined`, an `obj` that is `true` or `string[]` depending on what the spec said, an
// `unbox` to tell those apart — and every line of it existed to carry a parse across a language
// boundary that the grammar below does not have.
//
// The grammar was already mostly this module's. `parseArgs` keeps the LAST of a repeated option
// and says nothing, so the config it was handed asked for every value option as `multiple`
// purely to make a repeat visible, and the declared arity was then re-checked here. What was
// left of Node's contribution is the tokenising, which is `walk`.
//
// What Argu is loved for survives, by a different route: an `Opt` is a VALUE, and reading a
// parse back takes the same value that declared the option. There is no name to mistype at the
// call site, and an option cannot be read unless it was declared.

/// What an option takes, and the spellings that come with it. The two cases do not cross, and
/// that is load-bearing rather than tidy: there is no short option here that takes a value —
/// only `flag` mints a short — and nothing repeatable that takes none. So a short group (`-vh`)
/// expands without a case for "a short that wanted an argument", and the usage line has no
/// combination it must decline to print.
type private Takes =
    /// A switch: given or not, optionally with a one-letter spelling.
    | Nothing of short: string option
    /// A value, shown under this placeholder. `repeatable` says whether a second is collected
    /// or refused — declared, because the parse is checked against it in BOTH directions.
    | AValue of placeholder: string * repeatable: bool

/// One option a bin accepts. Private, so every option is built by `flag`, `value` or `values`
/// and its declared shape cannot disagree with how it is read back.
type Opt =
    private
        { Long : string
          Takes : Takes
          Help : string }

/// Everything a bin accepts, in one value — the thing `--help` prints and the parser reads,
/// so they cannot drift.
type Spec =
    private
        { Bin : string
          Options : Opt list }

/// A successful parse. Opaque: read it with `isSet`/`valueOf`.
type Parsed =
    private
        { Present : Set<string>
          /// Every value given, in the order given. `Values` is the last of these, so the
          /// two views of one option cannot disagree — there is one parse behind both.
          Many : Map<string, string list>
          Values : Map<string, string> }

// --- declaring a command line ------------------------------------------------------------

/// A boolean switch: given or not.
let flag (long: string) (short: string option) (help: string) : Opt =
    { Long = long; Takes = Nothing short; Help = help }

/// An option that takes a value, once. `placeholder` is what `--help` shows in the angle
/// brackets. Given twice, it is refused — see `values` for the option that is not.
let value (long: string) (placeholder: string) (help: string) : Opt =
    { Long = long; Takes = AValue (placeholder, false); Help = help }

/// An option that takes a value and may be given more than once, collecting every value in
/// order. For configuration that is a SET rather than a choice — one webhook endpoint per
/// service, say — where a single option would otherwise carry a separator this parser would
/// have to invent, and a repeat would silently mean "the last one".
let values (long: string) (placeholder: string) (help: string) : Opt =
    { Long = long; Takes = AValue (placeholder, true); Help = help }

/// Every bin answers these two identically, so they belong to what a spec IS rather than to
/// what each bin remembers to declare.
let version : Opt = flag "version" (Some "v") "print the version and exit"
let help : Opt = flag "help" (Some "h") "show this message and exit"

let spec (bin: string) (options: Opt list) : Spec =
    { Bin = bin; Options = options @ [ version; help ] }

// --- reading a parse ---------------------------------------------------------------------

/// Was this option given? Takes the `Opt` that declared it, so there is no name to mistype.
let isSet (opt: Opt) (parsed: Parsed) : bool = Set.contains opt.Long parsed.Present

/// The value given for this option, or None when it was not given. For a repeatable option
/// this is the last value; `valuesOf` is the whole of it.
let valueOf (opt: Opt) (parsed: Parsed) : string option = Map.tryFind opt.Long parsed.Values

/// Every value given for this option, in order — empty when it was not given. An option
/// that is not repeatable answers with at most one, because a second was refused.
let valuesOf (opt: Opt) (parsed: Parsed) : string list =
    Map.tryFind opt.Long parsed.Many |> Option.defaultValue []

// --- usage --------------------------------------------------------------------------------

let usage (spec: Spec) : string =
    let line (opt: Opt) =
        let names =
            match opt.Takes with
            | Nothing (Some short) -> sprintf "-%s, --%s" short opt.Long
            | Nothing None -> sprintf "    --%s" opt.Long
            // `...` says the option may be repeated, where an operator is already looking.
            | AValue (placeholder, true) -> sprintf "    --%s <%s>..." opt.Long placeholder
            | AValue (placeholder, false) -> sprintf "    --%s <%s>" opt.Long placeholder
        sprintf "  %-26s %s" names opt.Help
    let options = spec.Options |> List.map line |> String.concat "\n"
    sprintf "usage: %s [options]\n\noptions:\n%s" spec.Bin options

// --- parsing --------------------------------------------------------------------------------

/// How every command-line complaint reads: which bin, what was wrong, then the usage.
///
/// Public because a parse failure is not the only way a command line is wrong: a VALUE the
/// parser accepted and the bin refused (`--auth banana`) is the same class of mistake to the
/// operator who typed it, and `Interop.rejectValue` reports it in these words rather than
/// inventing a second voice for it.
let complaint (spec: Spec) (message: string) : string =
    sprintf "%s: %s\n\n%s" spec.Bin message (usage spec)

// Which option a spelling names. Both answer from the SAME list `usage` prints, which is what
// stops a bin accepting an option it does not document.

/// The option this spec spells `--name`.
let private longNamed (spec: Spec) (name: string) : Opt option =
    spec.Options |> List.tryFind (fun opt -> opt.Long = name)

/// The option this spec spells `-x`.
let private shortNamed (spec: Spec) (letter: char) : Opt option =
    spec.Options
    |> List.tryFind (fun opt ->
        match opt.Takes with
        | Nothing (Some short) -> short = string letter
        | Nothing None
        | AValue _ -> false)

/// `--name=value` split at the FIRST `=`; `None` when the token carries no value. First,
/// because a value may carry one of its own: `--webhook=shop@1=x-shop-hmac:base64` is the
/// option `webhook` and the whole of a declaration this bin documents, not a name running to
/// the LAST `=` and a fragment of that declaration after it.
let private splitAtEquals (token: string) : string * string option =
    match token.IndexOf '=' with
    | -1 -> token, None
    | at -> token.Substring (0, at), Some (token.Substring (at + 1))

/// One occurrence of an option on the command line: a switch carries no value, an option that
/// takes one carries what it was given. Occurrences rather than a map, because both questions
/// asked of them afterwards — was this given, and how many times — are about occurrences, and
/// a map that kept the last is precisely the silent-ignore this module exists to end.
type private Hit = { Of : Opt; Value : string option }

/// Whether a second of this option is collected or refused. A switch is never refused a
/// repeat: `--check --check` says the same thing twice, and there is no value to lose.
let private repeatable (opt: Opt) : bool =
    match opt.Takes with
    | AValue (_, repeatable) -> repeatable
    | Nothing _ -> false

/// Read `args` left to right against `spec`. Three spellings are options and nothing else is:
/// `--name value`, `--name=value`, and a short switch or a group of them (`-v`, `-vh`).
///
/// A value is taken from the next token VERBATIM, so `--auth --version` sets `--auth` to the
/// text `--version` rather than guessing that a value starting with a dash was a mistake:
/// whether it is depends on the option's own vocabulary, which refuses it a line later and in
/// its own words. Every other refusal names what the operator typed, because the alternative
/// — this repository's own history — is a bin that ran with the option missing.
let rec private walk (spec: Spec) (hits: Hit list) (rest: string list) : Result<Hit list, string> =
    let refuse message : Result<Hit list, string> = Error (complaint spec message)
    match rest with
    | [] -> Ok (List.rev hits)
    // `--` introduces bare words, and no bin here takes one — so it is refused as the
    // separator it is, rather than reported as an option nobody declared.
    | "--" :: _ -> refuse "-- introduces bare words, and no bin here takes one"
    | token :: rest when token.StartsWith "--" ->
        let name, inlineValue = splitAtEquals (token.Substring 2)
        match longNamed spec name with
        | None -> refuse (sprintf "unknown option --%s" name)
        | Some opt ->
            match opt.Takes, inlineValue with
            | Nothing _, Some _ ->
                refuse (sprintf "--%s is a switch: it is given or not, and takes no value" name)
            | Nothing _, None -> walk spec ({ Of = opt; Value = None } :: hits) rest
            | AValue _, Some given -> walk spec ({ Of = opt; Value = Some given } :: hits) rest
            | AValue (placeholder, _), None ->
                match rest with
                | given :: rest -> walk spec ({ Of = opt; Value = Some given } :: hits) rest
                | [] -> refuse (sprintf "--%s takes a value, and was given none: --%s <%s>" name name placeholder)
    // A short group is its letters, each a switch — see `Takes` for why there is no other
    // kind of short to consider here.
    | token :: rest when token.StartsWith "-" && token.Length > 1 ->
        let rec shorts (hits: Hit list) (letters: char list) =
            match letters with
            | [] -> walk spec hits rest
            | letter :: letters ->
                match shortNamed spec letter with
                | Some opt -> shorts ({ Of = opt; Value = None } :: hits) letters
                | None -> refuse (sprintf "unknown option -%c" letter)
        shorts hits (List.ofSeq (token.Substring 1))
    | token :: _ -> refuse (sprintf "%s is not an option, and no bin here takes a bare word" token)

/// Parse `args` against `spec`. Total — the failure is a message an operator can act on,
/// carrying what was wrong and the usage under it.
let parse (spec: Spec) (args: string array) : Result<Parsed, string> =
    match walk spec [] (List.ofArray args) with
    | Error message -> Error message
    | Ok hits ->
        // Walk the DECLARATION, not the tokens: every name here is one this spec knows, so
        // nothing can arrive that `isSet`/`valueOf` could not name.
        let given =
            spec.Options
            |> List.choose (fun opt ->
                match hits |> List.filter (fun hit -> hit.Of.Long = opt.Long) with
                | [] -> None
                | seen -> Some (opt, seen |> List.choose (fun hit -> hit.Value)))
        // The declared arity, enforced. A repeat is a fact only because every occurrence was
        // kept above rather than folded into a map that keeps the last.
        match given |> List.tryFind (fun (opt, vs) -> not (repeatable opt) && List.length vs > 1) with
        | Some (opt, vs) ->
            Error (complaint spec (sprintf "--%s was given %d times, and takes one value" opt.Long (List.length vs)))
        | None ->
            Ok
                { Present = given |> List.map (fun (opt, _) -> opt.Long) |> Set.ofList
                  Many = given |> List.map (fun (opt, vs) -> opt.Long, vs) |> Map.ofList
                  // The last of the same list, so one parse answers both readers.
                  Values =
                    given
                    |> List.choose (fun (opt, vs) -> vs |> List.tryLast |> Option.map (fun v -> opt.Long, v))
                    |> Map.ofList }

// --- what a command line asks of the process ----------------------------------------------

/// What a command line means for the process it was typed at. A VALUE, because the three
/// answers are a decision and the stopping is not: only two of them end the process, and
/// ending one is the single thing F# cannot say — `process.exit` belongs to whoever owns the
/// process, which is a bin and never the domain. Splitting it here is what makes the whole
/// boundary readable by the cheap tier; `parseOrExit` could only ever be read by running a
/// bin and watching it stop.
[<RequireQualifiedAccess>]
type Outcome =
    /// Carry on, with this parse.
    | Proceed of Parsed
    /// `--version` or `--help`: say this on stdout and stop, having done what was asked.
    | Answered of text: string
    /// A command line that was wrong: say this on stderr and stop, having done nothing.
    | Refused of complaint: string

/// The whole boundary, in one call: parse, then answer `--version` and `--help` — every bin
/// answers them identically, so answering them belongs to what a command line IS rather than
/// to what each bin remembers to do. `--version` wins over `--help`, because a line carrying
/// both asked two questions and only one process can answer.
let outcome (spec: Spec) (currentVersion: string) (args: string array) : Outcome =
    match parse spec args with
    | Error message -> Outcome.Refused message
    | Ok parsed ->
        if isSet version parsed then Outcome.Answered currentVersion
        elif isSet help parsed then Outcome.Answered (usage spec)
        else Outcome.Proceed parsed
