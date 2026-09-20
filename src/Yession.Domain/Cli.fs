module Yession.Domain.Cli

// What each bin accepts on its command line, declared as data and parsed by Node's own
// `parseArgs` (`node:util`) — a real parser, with no dependency to add, because these bins
// already run on Node.
//
// It replaces hand-rolled `process.argv` scanning, which got two things wrong that matter
// more than the parsing itself:
//
//   - An unknown option was SILENTLY IGNORED. `yession-manager --auht localhost` ran with
//     no strategy at all, which is deny-everything — a typo that looks like a hang. Under
//     `strict`, `parseArgs` refuses instead.
//   - A bad value threw at module init, inside Fable's async, and surfaced as
//     `UnhandledPromiseRejection ... "[object Object]"` — technically a refused boot, but
//     nothing an operator could act on. `outcome` answers with the reason and the usage
//     instead, and the bin says it before it stops.
//
// Argu would be the F# answer on .NET and cannot be the answer here: Fable compiles from
// F# SOURCE, so a package has to ship its source files to be usable at all, and Argu ships
// IL. Its DU-plus-attributes model leans on reflection besides.
//
// The typed-access property Argu is loved for survives, by a different route: an `Opt` is a
// VALUE, and reading a parse back takes the same value that declared the option. There is
// no name to mistype at the call site, and an option cannot be read unless it was declared.

open Fable.Core
open Fable.Core.JsInterop

/// One option a bin accepts. Private, so every option is built by `flag` or `value` and its
/// declared shape cannot disagree with how it is read back.
type Opt =
    private
        { Long : string
          Short : string option
          /// None = a boolean switch; Some placeholder = takes a value.
          Placeholder : string option
          /// May it be given more than once? Declared, because the parse is checked against
          /// it in BOTH directions: a repeatable option collects every value, and one that
          /// is not repeatable is refused a second (Node's parser silently keeps the last).
          Repeatable : bool
          Help : string }

/// Everything a bin accepts, in one value — the thing `--help` prints and the parser is
/// built from, so they cannot drift.
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

// --- the Node parser ---------------------------------------------------------------------

/// What `parseArgs` answers with: one entry per option that was GIVEN, under its long name.
/// An entry is `true` for a boolean option and — because `configFor` asks for every value
/// option as `multiple` — a `string[]` for a value option; which of the two it is comes from
/// the spec this module built the config from, never from looking at the value.
[<AllowNullLiteral>]
type private ParsedValues =
    /// `undefined` for an option that was not given, which is `None` here.
    [<EmitIndexer>]
    abstract Item : name: string -> obj option

[<AllowNullLiteral>]
type private ParsedArgs =
    abstract values : ParsedValues

[<Import("parseArgs", "node:util")>]
let private parseArgs (config: obj) : ParsedArgs = jsNative

// --- declaring a command line ------------------------------------------------------------

/// A boolean switch: present or not.
let flag (long: string) (short: string option) (help: string) : Opt =
    { Long = long; Short = short; Placeholder = None; Repeatable = false; Help = help }

/// An option that takes a value, once. `placeholder` is what `--help` shows in the angle
/// brackets. Given twice, it is refused — see `values` for the option that is not.
let value (long: string) (placeholder: string) (help: string) : Opt =
    { Long = long; Short = None; Placeholder = Some placeholder; Repeatable = false; Help = help }

/// An option that takes a value and may be given more than once, collecting every value in
/// order. For configuration that is a SET rather than a choice — one webhook endpoint per
/// service, say — where a single option would otherwise carry a separator this parser would
/// have to invent, and a repeat would silently mean "the last one".
let values (long: string) (placeholder: string) (help: string) : Opt =
    { Long = long; Short = None; Placeholder = Some placeholder; Repeatable = true; Help = help }

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
            match opt.Short with
            | Some short -> sprintf "-%s, --%s" short opt.Long
            | None -> sprintf "    --%s" opt.Long
        let names =
            match opt.Placeholder, opt.Repeatable with
            // `...` says the option may be repeated, where an operator is already looking.
            | Some placeholder, true -> sprintf "%s <%s>..." names placeholder
            | Some placeholder, false -> sprintf "%s <%s>" names placeholder
            | None, _ -> names
        sprintf "  %-26s %s" names opt.Help
    let options = spec.Options |> List.map line |> String.concat "\n"
    sprintf "usage: %s [options]\n\noptions:\n%s" spec.Bin options

// --- parsing --------------------------------------------------------------------------------

/// The `parseArgs` config this spec describes. Built from the SAME list `usage` prints.
let private configFor (spec: Spec) (args: string array) : obj =
    let entry (opt: Opt) =
        createObj
            [ yield "type", box (if opt.Placeholder.IsSome then "string" else "boolean")
              // Every value option is parsed as a LIST, whatever its declared arity, so that a
              // repeat is a fact this module can see. Without it Node keeps the last silently,
              // and `--auth localhost --auth none` would run as `none` with nothing said.
              if opt.Placeholder.IsSome then yield "multiple", box true
              match opt.Short with
              | Some short -> yield "short", box short
              | None -> () ]
    createObj
        [ "args", box args
          "options", createObj [ for opt in spec.Options -> opt.Long, entry opt ]
          // Refuse an unknown option and a missing value rather than carrying on without
          // them, and refuse bare words too: no bin here takes a positional argument, so
          // one is always a mistake.
          "strict", box true
          "allowPositionals", box false ]

/// How every command-line complaint reads: which bin, what was wrong, then the usage.
///
/// Public because a parse failure is not the only way a command line is wrong: a VALUE the
/// parser accepted and the bin refused (`--auth banana`) is the same class of mistake to the
/// operator who typed it, and `Interop.rejectValue` reports it in these words rather than
/// inventing a second voice for it.
let complaint (spec: Spec) (message: string) : string =
    sprintf "%s: %s\n\n%s" spec.Bin message (usage spec)

/// Parse `args` against `spec`. Total — the failure is a message an operator can act on,
/// carrying the parser's own complaint and the usage under it.
let parse (spec: Spec) (args: string array) : Result<Parsed, string> =
    try
        let values = (parseArgs (configFor spec args)).values
        // Walk the DECLARATION, not the result: every name here is one this spec knows, so
        // nothing can arrive that `isSet`/`valueOf` could not name.
        let given =
            spec.Options
            |> List.choose (fun opt ->
                match values.[opt.Long] with
                | None -> None
                // `configFor` declared this option `multiple`, so what Node put here is a list.
                | Some raw when opt.Placeholder.IsSome -> Some (opt, unbox<string array> raw |> List.ofArray)
                | Some _ -> Some (opt, []))
        // The declared arity, enforced. `configFor` asked for every value as a list precisely
        // so this is answerable: Node keeps the last of a repeat and says nothing, which is
        // the silent-ignore this module exists to end.
        match given |> List.tryFind (fun (opt, vs) -> not opt.Repeatable && List.length vs > 1) with
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
    with error ->
        Error (complaint spec error.Message)

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
