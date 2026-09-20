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
// --- an option knows what its own value means ----------------------------------------------
//
// `--auth banana` is the same mistake to an operator as `--auht localhost`: a word this bin
// does not know. It used to be reported by a different mechanism in a different place — the
// parser refused the typo, while the boot refused the value, four times over in `Main.fs`:
//
//     match Oidc.Strategy.ofName (Cli.valueOf authOption args) with
//     | Ok s -> s
//     | Error e -> Interop.rejectValue cli e
//
// A refusal reached that way is a decision taken in the composition root, which is the one
// place a cheap test cannot go (`design.md` §1, and AGENTS.md on colocation). So an option
// carries its own vocabulary: `parsedValue` takes the reader that says what the value MEANS,
// the parse runs it, and `valueOf` answers with the thing itself — an `AuthenticationStrategy`
// rather than a string somebody still has to interpret. The readers already existed and
// already had the right shape, which is the tell that this is where they belonged: `ofName`
// takes `string option`, because absence is an answer the vocabulary gives (no `--auth` is
// deny-everything) and never a default a caller supplies.
//
// The refusal keeps the wording it had. `Interop.rejectValue` stays for the rules that are
// about MORE than one option — `--detailed` needing `--check`, an environment still setting a
// variable that moved — which are the bin's to state because no single option can.
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
// What Argu is loved for survives, by a different route: an `Opt<'a>` is a VALUE, and reading a
// parse back takes the same value that declared the option. There is no name to mistype at the
// call site, an option cannot be read unless it was declared, and what it reads back as is the
// type the declaration chose.

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

/// What the PARSER needs of an option, which is everything except the type of its value: the
/// spellings to recognise, the line to print, and what the values given for it COME TO.
///
/// `Resolve` answers `obj` because a spec holds options that answer with different types —
/// `--port` is an int and `--auth` a strategy — and F# has no list that holds both. The type
/// is put back by `valueOf`, safely, because the only writer under an option's name is that
/// option's own reader: `Opt<'a>` is this plus the `'a` its reader produces, and the two are
/// minted together by `flag`/`value`/`values`/`parsedValue` and never separately.
///
/// It is handed `None` when the option was not given at all and `Some values` when it was —
/// `Some []` for a switch, which has presence and no value. The difference is the vocabulary's
/// to interpret, which is the whole point: `--auth` absent means deny-everything, and that is
/// a fact about authentication rather than about parsing.
[<NoEquality; NoComparison>]
type private Decl =
    { Long : string
      Takes : Takes
      Help : string
      Resolve : string list option -> Result<obj, string> }

/// One option a bin accepts, and what reading it back answers with. Private, so every option
/// is built by `flag`, `value`, `values` or `parsedValue` and its declared shape cannot
/// disagree with how it is read.
[<NoEquality; NoComparison>]
type Opt<'a> = private { Declared : Decl }

/// Everything a bin accepts, in one value — the thing `--help` prints and the parser reads,
/// so they cannot drift. Built up by `accepts`, one option at a time, because the options of
/// one bin do not share a type and no list can hold them.
[<NoEquality; NoComparison>]
type Spec =
    private
        { Bin : string
          /// What this bin declared, in the order `--help` prints them. `--version` and
          /// `--help` are not here: every bin answers them identically, so they are appended
          /// by `all` rather than remembered by each spec.
          Options : Decl list }

/// A successful parse: every declared option resolved to what it came to. Opaque — read it
/// with `isSet`/`valueOf`.
type Parsed =
    private
        { /// The options the operator actually wrote, which is a different question from what
          /// each came to: `--port` absent and `--port 8321` resolve alike, and only this can
          /// tell a chosen value from a defaulted one (`--check` exists to answer that).
          Present : Set<string>
          /// One entry per DECLARED option, so `valueOf` is total. See `Decl.Resolve`.
          Values : Map<string, obj> }

// --- declaring a command line ------------------------------------------------------------

/// A boolean switch: given or not.
let flag (long: string) (short: string option) (help: string) : Opt<bool> =
    { Declared =
        { Long = long
          Takes = Nothing short
          Help = help
          Resolve = fun given -> Ok (box (Option.isSome given)) } }

/// An option that takes a value, once, and takes the operator at their word. `placeholder` is
/// what `--help` shows in the angle brackets. Given twice it is refused — see `values` for the
/// option that is not, and `parsedValue` for the one whose value has a vocabulary of its own.
let value (long: string) (placeholder: string) (help: string) : Opt<string option> =
    { Declared =
        { Long = long
          Takes = AValue (placeholder, false)
          Help = help
          Resolve = fun given -> Ok (box (given |> Option.bind List.tryLast)) } }

/// An option that takes a value and may be given more than once, collecting every value in
/// order. For configuration that is a SET rather than a choice — one webhook endpoint per
/// service, say — where a single option would otherwise carry a separator this parser would
/// have to invent, and a repeat would silently mean "the last one".
let values (long: string) (placeholder: string) (help: string) : Opt<string list> =
    { Declared =
        { Long = long
          Takes = AValue (placeholder, true)
          Help = help
          Resolve = fun given -> Ok (box (defaultArg given [])) } }

/// An option whose value is a VOCABULARY rather than a string: `read` says what the operator
/// wrote means, or why this bin does not know it, and the parse refuses on its word — in the
/// same breath as a misspelt option, because that is the same mistake to whoever typed it.
///
/// `read` is handed `None` when the option was not given, because what absence means belongs
/// to the vocabulary and never to a caller's memory: no `--auth` is deny-everything, no
/// `--port` is 8321. That is already the shape the domain's own readers have — `Strategy
/// .ofName`, `ManagerPort.ofName` and `SecretsMode.ofName` each take `string option` — which
/// is the tell that this is where they belonged.
let parsedValue
    (long: string)
    (placeholder: string)
    (help: string)
    (read: string option -> Result<'a, string>)
    : Opt<'a> =
    { Declared =
        { Long = long
          Takes = AValue (placeholder, false)
          Help = help
          Resolve = fun given -> read (given |> Option.bind List.tryLast) |> Result.map box } }

/// Every bin answers these two identically, so they belong to what a spec IS rather than to
/// what each bin remembers to declare — `all` is where they join.
let version : Opt<bool> = flag "version" (Some "v") "print the version and exit"
let help : Opt<bool> = flag "help" (Some "h") "show this message and exit"

/// A bin that accepts nothing yet but `--version` and `--help`.
let spec (bin: string) : Spec = { Bin = bin; Options = [] }

/// Declare an option on this spec. One at a time rather than a list, because the options of
/// one bin answer with different types and F# has no list that holds them all — the pipeline
/// is what lets each keep its own.
let accepts (opt: Opt<'a>) (spec: Spec) : Spec =
    { spec with Options = spec.Options @ [ opt.Declared ] }

/// Every option this bin has, with the two every bin answers last. ONE list, so what `--help`
/// prints and what the parser recognises cannot differ.
let private all (spec: Spec) : Decl list = spec.Options @ [ version.Declared; help.Declared ]

// --- reading a parse ---------------------------------------------------------------------

/// Did the operator write this option? A different question from what it came to, and the
/// only one that can tell a chosen value from a defaulted one.
let isSet (opt: Opt<'a>) (parsed: Parsed) : bool = Set.contains opt.Declared.Long parsed.Present

/// What this option came to: what its own vocabulary made of what the operator wrote, or of
/// their having written nothing. Total, because the parse ran every declared reader and
/// refused the whole command line if any of them said no.
///
/// It fails only when handed an option this spec never declared, which is a mistake in the
/// program rather than on the command line — so it says so rather than inventing an absence
/// that would read as "the operator gave nothing". The cheap tier pins it.
let valueOf (opt: Opt<'a>) (parsed: Parsed) : 'a =
    match Map.tryFind opt.Declared.Long parsed.Values with
    | Some resolved -> unbox<'a> resolved
    | None -> failwithf "--%s was read off a parse of a spec that does not declare it" opt.Declared.Long

// --- usage --------------------------------------------------------------------------------

let usage (spec: Spec) : string =
    let line (opt: Decl) =
        let names =
            match opt.Takes with
            | Nothing (Some short) -> sprintf "-%s, --%s" short opt.Long
            | Nothing None -> sprintf "    --%s" opt.Long
            // `...` says the option may be repeated, where an operator is already looking.
            | AValue (placeholder, true) -> sprintf "    --%s <%s>..." opt.Long placeholder
            | AValue (placeholder, false) -> sprintf "    --%s <%s>" opt.Long placeholder
        sprintf "  %-26s %s" names opt.Help
    let options = all spec |> List.map line |> String.concat "\n"
    sprintf "usage: %s [options]\n\noptions:\n%s" spec.Bin options

// --- parsing --------------------------------------------------------------------------------

/// How every command-line complaint reads: which bin, what was wrong, then the usage.
///
/// Public because a parse failure is not the only way a command line is wrong: a rule over
/// SEVERAL options (`--detailed` needs `--check`) or over the environment around them (a
/// variable that moved onto one) is the bin's to state, because no single option can — and
/// `Interop.rejectValue` reports those in these words rather than inventing a second voice.
let complaint (spec: Spec) (message: string) : string =
    sprintf "%s: %s\n\n%s" spec.Bin message (usage spec)

// Which option a spelling names. Both answer from the SAME list `usage` prints, which is what
// stops a bin accepting an option it does not document.

/// The option this spec spells `--name`.
let private longNamed (spec: Spec) (name: string) : Decl option =
    all spec |> List.tryFind (fun opt -> opt.Long = name)

/// The option this spec spells `-x`.
let private shortNamed (spec: Spec) (letter: char) : Decl option =
    all spec
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
type private Hit = { Of : Decl; Value : string option }

/// Whether a second of this option is collected or refused. A switch is never refused a
/// repeat: `--check --check` says the same thing twice, and there is no value to lose.
let private repeatable (opt: Decl) : bool =
    match opt.Takes with
    | AValue (_, repeatable) -> repeatable
    | Nothing _ -> false

/// Read `args` left to right against `spec`. Three spellings are options and nothing else is:
/// `--name value`, `--name=value`, and a short switch or a group of them (`-v`, `-vh`).
///
/// A value is taken from the next token VERBATIM, so `--auth --version` sets `--auth` to the
/// text `--version` rather than guessing that a value starting with a dash was a mistake:
/// whether it is depends on the option's own vocabulary, which refuses it a stage later and in
/// its own words. Every other refusal names what the operator typed, because the alternative
/// — this repository's own history — is a bin that ran with the option missing.
let rec private walk (spec: Spec) (hits: Hit list) (rest: string list) : Result<Hit list, string> =
    let refuse message : Result<Hit list, string> = Error (complaint spec message)
    match rest with
    // `--` introduces bare words, and no bin here takes one — so it is refused as the
    // separator it is, rather than reported as an option nobody declared.
    | [] -> Ok (List.rev hits)
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

/// What the operator WROTE, before any option's vocabulary has had a say: each option they
/// gave, with the values they gave it, in declaration order.
///
/// A stage of its own because `--version` and `--help` are answered from it. Every bin answers them
/// "before any configuration is read" (`SessionMain.fs`), so a command line carrying both a
/// value this bin refuses and `--version` still says what this bin is.
type private Given = (Decl * string list) list

let private tokenised (spec: Spec) (args: string array) : Result<Given, string> =
    match walk spec [] (List.ofArray args) with
    | Error message -> Error message
    | Ok hits ->
        // Walk the DECLARATION, not the tokens: every name here is one this spec knows, so
        // nothing can arrive that `isSet`/`valueOf` could not name.
        let given =
            all spec
            |> List.choose (fun opt ->
                match hits |> List.filter (fun hit -> hit.Of.Long = opt.Long) with
                | [] -> None
                | seen -> Some (opt, seen |> List.choose (fun hit -> hit.Value)))
        // The declared arity, enforced. A repeat is a fact only because every occurrence was
        // kept above rather than folded into a map that keeps the last.
        match given |> List.tryFind (fun (opt, vs) -> not (repeatable opt) && List.length vs > 1) with
        | Some (opt, vs) ->
            Error (complaint spec (sprintf "--%s was given %d times, and takes one value" opt.Long (List.length vs)))
        | None -> Ok given

/// Every declared option's vocabulary, run. One entry per DECLARED option rather than per
/// given one, because absence is something an option answers FOR — and answering it here,
/// once, is what makes `valueOf` total and a refusal the parser's rather than the boot's.
let private resolved (spec: Spec) (given: Given) : Result<Parsed, string> =
    let wrote (opt: Decl) =
        given |> List.tryPick (fun (o, vs) -> if o.Long = opt.Long then Some vs else None)
    let rec step (decls: Decl list) (values: Map<string, obj>) =
        match decls with
        | [] ->
            Ok
                { Present = given |> List.map (fun (opt, _) -> opt.Long) |> Set.ofList
                  Values = values }
        | opt :: rest ->
            match opt.Resolve (wrote opt) with
            | Ok resolved -> step rest (Map.add opt.Long resolved values)
            | Error message -> Error (complaint spec message)
    step (all spec) Map.empty

/// Parse `args` against `spec`. Total — the failure is a message an operator can act on,
/// carrying what was wrong and the usage under it.
let parse (spec: Spec) (args: string array) : Result<Parsed, string> =
    tokenised spec args |> Result.bind (resolved spec)

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
///
/// Both are answered from the TOKENS, before any option's vocabulary runs, because a bin
/// answers them before it reads any configuration — so `--auth banana --version` still says
/// what this bin is rather than refusing a value it was never going to use.
let outcome (spec: Spec) (currentVersion: string) (args: string array) : Outcome =
    let wrote (opt: Opt<bool>) (given: Given) =
        given |> List.exists (fun (declared, _) -> declared.Long = opt.Declared.Long)
    match tokenised spec args with
    | Error message -> Outcome.Refused message
    | Ok given ->
        if wrote version given then Outcome.Answered currentVersion
        elif wrote help given then Outcome.Answered (usage spec)
        else
            match resolved spec given with
            | Ok parsed -> Outcome.Proceed parsed
            | Error message -> Outcome.Refused message
