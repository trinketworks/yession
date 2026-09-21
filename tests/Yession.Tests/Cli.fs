module Yession.Tests.Cli

// The command-line boundary. Pure — cheap tier, every environment — because `Cli.parse` and
// `Cli.outcome` take their args as a value rather than reading `process.argv`, which is the
// whole reason the boundary is testable at all. What is NOT here is the stopping: a bin that
// answers `--version` or refuses a typo ends its process, and that half is the Host's
// (`Interop.parseOrExit`) — one `match` over the value these cases pin.
//
// What these pin is that a MISUSE IS REFUSED: a mistyped or malformed command line stops the
// process instead of running it with the option missing. The old hand-rolled `process.argv`
// scan could not tell "not given" from "given wrong", so `yession-manager --auht localhost`
// booted a deny-everything Manager and looked like a hang.
//
// The tokenising is this repository's own, where it used to be `node:util`'s `parseArgs` —
// which `Yession.Domain` could not name without binding a Node API in the one project the
// browser client also compiles. So the grammar is pinned here too, by the cases a parser can
// get wrong in silence: a short group, a value carrying an `=`, and a value that looks like an
// option.

open Fable.Core
open Fable.Core.JsInterop
open Yession.Domain
open Fable.Pyxpecto
open Yession.Host

// Reading this repository's own workflow files, for the retirement scan below. The Cli suite
// is Node-only (it needs no browser), so `node:fs` is always there when this runs, and the
// suite's working directory is the repository root.
let private auth = Cli.value "auth" "rule" "how a request's subject is established"
let private secrets = Cli.value "secrets" "mode" "whether secrets persist"
let private webhook = Cli.values "webhook" "name" "a webhook endpoint to serve"

/// An option with a vocabulary of its own, so these cases can ask what the PARSE makes of a
/// value rather than what a caller would have made of it afterwards. Its own rather than the
/// Manager's, because what is under test here is the mechanism; that the Manager's `--auth`
/// is wired to it is pinned separately, against the real spec.
let private colour =
    Cli.parsedValue
        "colour"
        "name"
        "which colour"
        (function
         | None -> Ok "unpainted"
         | Some "red" -> Ok "red"
         | Some "blue" -> Ok "blue"
         | Some other -> Error (sprintf "unknown colour '%s' (expected red or blue)" other))

/// A repeatable option whose vocabulary reads the WHOLE set, so these cases can ask what a
/// reader sees when a rule spans declarations rather than sitting inside one.
let private tags =
    Cli.parsedValues
        "tag"
        "name"
        "a tag to apply"
        (fun given ->
            match given |> List.filter (fun t -> given |> List.filter ((=) t) |> List.length > 1) with
            | duplicate :: _ -> Error (sprintf "tag '%s' is given more than once" duplicate)
            | [] -> Ok given)

let private spec =
    Cli.spec "yession-manager"
    |> Cli.accepts auth
    |> Cli.accepts secrets
    |> Cli.accepts webhook
    |> Cli.accepts colour
    |> Cli.accepts tags

let private parse (args: string list) = Cli.parse spec (Array.ofList args)

let private parsed (args: string list) =
    match parse args with
    | Ok p -> p
    | Error e -> failwithf "expected a parse, got: %s" e

/// A fronted deployment with a chosen trust rule and a defaulted port: the two origins the
/// report has to tell apart.
let private fronted : ManagerCli.Report =
    { Version = "1.2.3"
      TrustRule = "trusted-headers", ManagerCli.Chosen
      Secrets = "durable", ManagerCli.Chosen
      Port = "8321", ManagerCli.Default
      DataDir = "/srv/yession", ManagerCli.Chosen
      DefaultSession = "local-session", ManagerCli.Default
      IdleTimeout = "30m", ManagerCli.Chosen
      Spawn = "/nix/store/x/bin/yession-session", ManagerCli.Chosen
      Addressing = ManagerCli.Addressing.Fronted ("https://host.ts.net:8321", "https://host.ts.net:8321/s/{id}")
      Webhooks = [ "github"; "shop@1=x-shop-hmac:base64" ]
      Inherited = [ "YESSION_MANAGER_URL"; "YESSION_PROXY_PORT" ] }

/// Nothing configured: every default, and the two features that can be off.
let private loopback : ManagerCli.Report =
    { fronted with
        Port = "8321", ManagerCli.Default
        IdleTimeout = "never", ManagerCli.Off
        Addressing = ManagerCli.Addressing.OnLoopback
        Webhooks = []
        Inherited = [] }

/// The setting a report renders under this label.
let private settingFor (label: string) (report: ManagerCli.Report) =
    match ManagerCli.Report.settings report |> List.tryFind (fun s -> s.Label = label) with
    | Some setting -> setting
    | None -> failwithf "the report has no %s setting" label

let private refused (args: string list) =
    match parse args with
    | Error e -> e
    | Ok _ -> failwithf "expected a refusal for %A" args

let tests =
    testList "Cli" [
        testCase "an option's value is read back with the option that declared it" <| fun () ->
            let p = parsed [ "--auth"; "localhost" ]
            Expect.equal (Cli.valueOf auth p) (Some "localhost") "the value"
            Expect.isTrue (Cli.isSet auth p) "and it counts as given"
            // An option that was not given is None, not an error — absent is a legitimate
            // answer for every option here (`--auth` absent means deny-everything).
            Expect.equal (Cli.valueOf secrets p) None "an option not given"
            Expect.isFalse (Cli.isSet secrets p) "and does not count as given"

        testCase "both spellings of a value reach the same place" <| fun () ->
            Expect.equal (Cli.valueOf auth (parsed [ "--auth=localhost" ])) (Some "localhost") "--name=value"
            Expect.equal (Cli.valueOf auth (parsed [ "--auth"; "localhost" ])) (Some "localhost") "--name value"

        testCase "an inline value is split at the first =, so a value may carry its own" <| fun () ->
            // `--webhook`'s own grammar spells a rotation and a signature scheme with `=`, so a
            // name that ran to the LAST one would take a declaration this bin documents and
            // hand the relay a fragment of it.
            Expect.equal
                (Cli.valueOf webhook (parsed [ "--webhook=shop@1=x-shop-hmac:base64" ]))
                [ "shop@1=x-shop-hmac:base64" ]
                "everything after the first ="

        testCase "a value that looks like an option is still the value" <| fun () ->
            // The parser does not guess that a leading dash was a mistake: whether `--version`
            // is a legitimate `--auth` is the option's own vocabulary to say, one line later
            // and in its own words. Guessing here would make `--auth` unable to carry a value
            // this parser happens to recognise elsewhere.
            Expect.equal (Cli.valueOf auth (parsed [ "--auth"; "--version" ])) (Some "--version") "taken verbatim"

        testCase "an empty command line parses to nothing given" <| fun () ->
            let p = parsed []
            Expect.isFalse (Cli.isSet auth p) "no auth"
            Expect.isFalse (Cli.isSet Cli.version p) "no version"
            Expect.isFalse (Cli.isSet Cli.help p) "no help"

        testCase "switches answer to their long and short spellings" <| fun () ->
            Expect.isTrue (Cli.isSet Cli.version (parsed [ "--version" ])) "--version"
            Expect.isTrue (Cli.isSet Cli.version (parsed [ "-v" ])) "-v"
            Expect.isTrue (Cli.isSet Cli.help (parsed [ "--help" ])) "--help"
            Expect.isTrue (Cli.isSet Cli.help (parsed [ "-h" ])) "-h"

        testCase "a group of short switches is every switch in it" <| fun () ->
            // `-vh` is `-v -h`. Nothing here is a short that takes a value — only `flag` mints
            // a short — which is what lets a group expand without asking where a value would go.
            let p = parsed [ "-vh" ]
            Expect.isTrue (Cli.isSet Cli.version p) "the first"
            Expect.isTrue (Cli.isSet Cli.help p) "and the second"

        // A repeatable option is configuration that is a SET, so what it pins is that every
        // value survives IN ORDER — a reader that took the last would look identical for the
        // one-value case every test writes first.
        testCase "a repeatable option collects every value, in order" <| fun () ->
            let p = parsed [ "--webhook"; "github"; "--webhook"; "shopify" ]
            Expect.equal (Cli.valueOf webhook p) [ "github"; "shopify" ] "both, in order"
            Expect.isTrue (Cli.isSet webhook p) "and it counts as given"

        testCase "a repeatable option given once is one value, and given none is empty" <| fun () ->
            Expect.equal (Cli.valueOf webhook (parsed [ "--webhook"; "github" ])) [ "github" ] "one"
            Expect.equal (Cli.valueOf webhook (parsed [])) [] "none"
            Expect.isFalse (Cli.isSet webhook (parsed [])) "and does not count as given"

        // Two cases used to live here, pinning that BOTH readers answered for every option —
        // `valuesOf` over a single-value one, `valueOf` over a repeatable one giving its last.
        // That was the looseness, not a guarantee: an option now reads back as the one thing
        // its declaration says it is, and the type is what says so. There is no second reader
        // to disagree with the first, so there is nothing left to pin.

        // The five refusals, one per way a command line can be wrong. Each was accepted
        // silently before, which is the defect: an ignored option is indistinguishable from
        // one the operator never wrote.
        testCase "an unknown option is refused, and named" <| fun () ->
            let message = refused [ "--auht"; "localhost" ]
            Expect.isTrue (message.Contains "--auht") "says which option"
            Expect.isTrue (message.Contains "yession-manager") "and which bin"

        testCase "an unknown short option is refused, and named" <| fun () ->
            // A group is refused by its first unknown letter rather than in whole, so an
            // operator is told which of the letters they typed this bin does not know.
            let message = refused [ "-vq" ]
            Expect.isTrue (message.Contains "-q") "says which letter"

        testCase "an option missing its value is refused" <| fun () ->
            (refused [ "--auth" ]) |> ignore

        testCase "a bare word is refused: no bin here takes a positional" <| fun () ->
            (refused [ "localhost" ]) |> ignore

        testCase "-- is refused as the separator it is, not as an option nobody declared" <| fun () ->
            // It introduces bare words, and no bin here takes one. Reporting it as an unknown
            // option would send an operator looking for a flag spelled `--`, which is the one
            // thing this module exists not to do: a refusal that names the wrong mistake costs
            // the same boot cycle as the silent ignore it replaced.
            let message = refused [ "--"; "localhost" ]
            Expect.isFalse (message.Contains "unknown option") "not reported as an option this bin lacks"

        testCase "a value given to a switch is refused" <| fun () ->
            (refused [ "--version=1.2.3" ]) |> ignore

        testCase "an option that takes one value is refused a second" <| fun () ->
            // Node keeps the LAST of a repeat and says nothing, so `--auth localhost --auth
            // none` would have run as deny-everything with both spellings on the line. The
            // declaration is what makes the repeat visible; this is it being believed.
            let message = refused [ "--auth"; "localhost"; "--auth"; "none" ]
            Expect.isTrue (message.Contains "--auth") "says which option"
            Expect.isTrue (message.Contains "2 times") "and how many times it was given"

        testCase "every refusal carries the usage, so the answer is in the failure" <| fun () ->
            // The point of the usage being HERE rather than in a separate --help run: an
            // operator who got it wrong is already looking at the terminal.
            for args in [ [ "--auht"; "x" ]; [ "--auth" ]; [ "localhost" ] ] do
                let message = refused args
                Expect.isTrue (message.Contains "usage: yession-manager") "names the bin"
                Expect.isTrue (message.Contains "--auth <rule>") "and lists the options with their placeholders"

        testCase "the usage lists every declared option, and the two every bin answers" <| fun () ->
            let text = Cli.usage spec
            for expected in [ "--auth <rule>"; "--secrets <mode>"; "--webhook <name>..."; "-v, --version"; "-h, --help" ] do
                Expect.isTrue (text.Contains expected) (sprintf "usage mentions %s" expected)

        // `--port`, resolved beside the port it configures. `0` is the case worth pinning:
        // it is the one place an unpredictable address is deliberate, and refusing it broke
        // every smoke boot at once — a bin nothing could start, discovered by CI.
        // The Manager's OWN options, wired to the vocabularies that used to be applied in
        // `Main.fs`. What these pin is the wiring: that the spec this bin parses with is the
        // one carrying those readers, which no test of the readers alone can say.
        testCase "the Manager refuses an unknown --auth rule at the parse, not at the boot" <| fun () ->
            match Cli.parse ManagerCli.spec [| "--auth"; "banana" |] with
            | Error message ->
                Expect.isTrue (message.Contains "banana") "names the rule it does not know"
                Expect.isTrue (message.Contains "usage: yession-manager") "and carries the usage"
            | Ok _ -> failwith "an unknown auth rule must refuse the command line"

        testCase "the Manager reads --auth back as the strategy it means" <| fun () ->
            match Cli.parse ManagerCli.spec [| "--auth"; "localhost" |] with
            | Ok p -> Expect.equal (Cli.valueOf ManagerCli.authOption p).Name "localhost" "the strategy itself"
            | Error e -> failwithf "expected a parse, got: %s" e

        testCase "the Manager refuses a webhook declaration it cannot read, at the parse" <| fun () ->
            // Two defects closed at once, both reproduced on the built bin before this existed.
            // `--check` decoded the declarations itself and swallowed the failure with
            // `Result.defaultValue []`, so `--webhook github --webhook github --check` printed
            // "none declared", marked the feature off, and exited 0 over endpoints an operator
            // had just asked for. A real boot threw inside Fable's async and surfaced as
            // `UnhandledPromiseRejection ... "[object Object]"` — AFTER saying "manager
            // started" — which is the exact failure `Cli.fs` was written to end, surviving on
            // the one option that had not taken its vocabulary.
            match Cli.parse ManagerCli.spec [| "--webhook"; "github"; "--webhook"; "github" |] with
            | Error message ->
                Expect.isTrue (message.Contains "declared more than once") "says what was wrong"
                Expect.isTrue (message.Contains "usage: yession-manager") "and carries the usage"
            | Ok _ -> failwith "a webhook declared twice must refuse the command line"

        testCase "the Manager reads --webhook back as the endpoints it will serve" <| fun () ->
            // Decoded once, by the parse, so the report and the relay cannot disagree about
            // what was declared — they read one value rather than decoding the text apiece.
            match Cli.parse ManagerCli.spec [| "--webhook"; "shop@1=x-shop-hmac:base64" |] with
            | Ok p ->
                match Cli.valueOf ManagerCli.webhookOption p with
                | [ only ] ->
                    Expect.equal only.Name "shop" "the endpoint's name, decoded"
                    Expect.equal
                        (WebhookRelay.EndpointSpec.encode only)
                        "shop@1=x-shop-hmac:base64"
                        "and it re-encodes to what was typed"
                | other -> failwithf "expected one endpoint, got %A" other
            | Error e -> failwithf "expected a parse, got: %s" e

        testCase "a secrets mode spells itself back the way an operator typed it" <| fun () ->
            // The encode side `--check` needs: what a report prints must be what could be
            // typed back, so `ofName (describe m) = Ok m` wherever `describe` answers at all.
            for mode in [ ProcessManager.RequireDurable; ProcessManager.ForceEphemeral ] do
                match ProcessManager.SecretsMode.describe mode with
                | Some spelling ->
                    Expect.equal (ProcessManager.SecretsMode.ofName (Some spelling)) (Ok mode) "round-trips"
                | None -> failwithf "%A has no spelling, and only the defaulted mode may lack one" mode

        testCase "the mode nobody can type is the one that says it was defaulted" <| fun () ->
            // `--secrets` deliberately has no `auto` spelling, and absence is the only way to
            // reach `AutoSecrets` — so a report can read "was this chosen?" off the mode.
            Expect.equal (ProcessManager.SecretsMode.describe ProcessManager.AutoSecrets) None "no spelling"

        testCase "a port argument resolves, and 0 asks the OS for one" <| fun () ->
            Expect.equal (ProcessManager.ManagerPort.ofName None) (Ok ProcessManager.ManagerPort.Default) "absent = the default"
            Expect.equal (ProcessManager.ManagerPort.ofName (Some "9000")) (Ok 9000) "a port"
            Expect.equal (ProcessManager.ManagerPort.ofName (Some "0")) (Ok 0) "and 0, which the OS answers"

        testCase "a port argument that is not a port number is refused, not defaulted" <| fun () ->
            // Refused because the alternative reaches `listen` as NaN, which BINDS — on a
            // random port, reporting itself as a Manager answering somewhere nobody was told.
            for bad in [ "banana"; ""; "65536"; "-1"; "80.5" ] do
                match ProcessManager.ManagerPort.ofName (Some bad) with
                | Error message -> Expect.isTrue (message.Contains bad) (sprintf "names %A back" bad)
                | Ok port -> failwithf "expected %A to be refused, got port %d" bad port

        // `--check`: what the report SAYS, over values a boot has already resolved. The
        // rendering is the half worth pinning — the resolution is every other case in this
        // file, and a report that cannot be read is a report nobody believes.
        // `--check`: what the report PROMISES, asserted against the list it is built from
        // rather than the printing of it. A case that quoted a sentence or counted a column
        // would go red on a rewording or a moved label, which is not a fault — and this file
        // has already reworded these twice.
        testCase "a value the operator chose is told apart from one the bin defaulted to" <| fun () ->
            // The question --check exists to answer: a value alone cannot say whether the
            // setting you wrote took effect, because `8321` is the same text either way.
            Expect.equal (settingFor "trust rule" fronted).Origin ManagerCli.Chosen "a chosen value"
            Expect.equal (settingFor "port" fronted).Origin ManagerCli.Default "a defaulted one"
            Expect.equal (settingFor "webhooks" loopback).Origin ManagerCli.Off "and a feature that is off"

        testCase "every setting says something, and says what it does" <| fun () ->
            // Two promises over the whole list, so a setting added without either is red.
            // A label with nothing after it reads as a rendering fault, and a report has to
            // be believed; a setting with no description makes --detailed silently partial.
            for report in [ fronted; loopback ] do
                for setting in ManagerCli.Report.settings report do
                    Expect.notEqual setting.Value "" (sprintf "%s has a value" setting.Label)
                    Expect.notEqual setting.Detail "" (sprintf "%s has a description" setting.Label)

        testCase "the two addresses are two facts, described separately" <| fun () ->
            // They shared one description until a report of a real deployment printed it
            // twice, which reads as padding.
            Expect.notEqual
                (settingFor "manager at" fronted).Detail
                (settingFor "sessions at" fronted).Detail
                "the Manager's origin and the session template say different things"

        testCase "the report prints every setting it holds" <| fun () ->
            let text = ManagerCli.Report.render false fronted
            for setting in ManagerCli.Report.settings fronted do
                Expect.isTrue (text.Contains setting.Label) (sprintf "%s is printed" setting.Label)
                Expect.isTrue (text.Contains setting.Value) (sprintf "%s's value is printed" setting.Label)

        testCase "a value longer than its column is still a word apart from its state" <| fun () ->
            // A store path is longer than any column worth keeping, and a value that ran into
            // its own state read as one word (`…SessionMain.jsdefault`).
            let long = { fronted with Spawn = String.replicate 80 "x", ManagerCli.Default }
            let row =
                (ManagerCli.Report.render false long).Split '\n'
                |> Array.find (fun (l: string) -> l.Contains "xxxx")
            Expect.isTrue (row.EndsWith " default") "whitespace separates the state from the value"

        testCase "--detailed explains, and a plain report does not" <| fun () ->
            let detailed = ManagerCli.Report.render true fronted
            let plain = ManagerCli.Report.render false fronted
            for setting in ManagerCli.Report.settings fronted do
                Expect.isTrue (detailed.Contains setting.Detail) (sprintf "%s is explained" setting.Label)
                Expect.isFalse (plain.Contains setting.Detail) (sprintf "%s is not, without the flag" setting.Label)
            for state, meaning in ManagerCli.Origin.meanings do
                Expect.isTrue (detailed.Contains state) (sprintf "the %s state is named" state)
                Expect.isTrue (detailed.Contains meaning) (sprintf "and %s is explained" state)

        testCase "--check and --detailed are switches the Manager declares" <| fun () ->
            let usage = Cli.usage ManagerCli.spec
            Expect.isTrue (usage.Contains "--check") "--help names --check"
            Expect.isTrue (usage.Contains "--detailed") "and --detailed"

        // Retirements: a setting that MOVED, and an environment that has not caught up.
        testCase "a retired variable the environment still sets is found, and named with its option" <| fun () ->
            let retirements = [ { Retirements.Was = "YESSION_PORT"; Retirements.Now = "--port" } ]
            let set = dict [ "YESSION_PORT", "8321" ]
            let lookup name = if set.ContainsKey name then set.[name] else ""
            let found = Retirements.found retirements lookup
            Expect.equal (found |> List.map (fun r -> r.Was)) [ "YESSION_PORT" ] "the variable"
            let message = Retirements.complaint found
            Expect.isTrue (message.Contains "YESSION_PORT") "says which variable"
            Expect.isTrue (message.Contains "--port") "and what to write instead"

        testCase "a retirement the environment does not set is not found" <| fun () ->
            // Unset and empty are the same thing to a bin, and `Interop.envOr name ""` cannot
            // tell them apart — so an empty value must not refuse a boot that set nothing.
            let retirements = [ { Retirements.Was = "YESSION_PORT"; Retirements.Now = "--port" } ]
            Expect.equal (Retirements.found retirements (fun _ -> "")) [] "unset"
            Expect.equal (Retirements.found retirements (fun _ -> "   ")) [] "or blank"

        testCase "every retirement still set is reported at once" <| fun () ->
            // One boot, one report. A deployment that moved one of these moved all of them at
            // the same time, and learning about the next only after fixing this one is a boot
            // cycle spent per variable — which is exactly how the renames that motivated this
            // were found in the first place.
            let found = Retirements.found Retirements.manager (fun _ -> "x")
            Expect.equal (List.length found) (List.length Retirements.manager) "all of them"
            let message = Retirements.complaint found
            for r in Retirements.manager do
                Expect.isTrue (message.Contains r.Was) (sprintf "names %s" r.Was)
                Expect.isTrue (message.Contains r.Now) (sprintf "and its option %s" r.Now)

        testCase "every option a retirement points at is one the Manager declares" <| fun () ->
            // Against the bin's OWN spec, not a copy of it. This case first went red for the
            // wrong reason: it listed the options by hand, so the first retirement added
            // after it was written broke a check that was supposed to be watching for
            // exactly that. A list you have to keep in step is not a check, it is a second
            // list — which is why `ManagerCli.spec` is a value.
            let usage = Cli.usage ManagerCli.spec
            for r in Retirements.manager do
                Expect.isTrue (usage.Contains (r.Now + " <")) (sprintf "%s is a declared option" r.Now)

        testCase "the detector sees an assignment and lets a mention be" <| fun () ->
            let retirements = [ { Retirements.Was = "YESSION_PORT"; Retirements.Now = "--port" } ]
            let assigned = Retirements.assignedIn retirements
            Expect.equal (assigned "run: YESSION_PORT=0 ./yession-manager" |> List.length) 1 "a shell assignment"
            Expect.equal (assigned "env:\n  YESSION_PORT: 0" |> List.length) 1 "a yaml one"
            Expect.equal (assigned "# YESSION_PORT was retired; use --port") [] "prose naming it is not setting it"

        testCase "no workflow file sets a variable the bins no longer read" <| fun () ->
            let dir = ".github/workflows"
            let offences =
                TestFiles.entries dir
                |> List.filter (fun name -> name.EndsWith ".yml" || name.EndsWith ".yaml")
                |> List.collect (fun name ->
                    Retirements.assignedIn Retirements.manager (TestFiles.read (dir + "/" + name))
                    |> List.map (fun r -> sprintf "%s sets %s (now %s)" name r.Was r.Now))
            Expect.equal offences [] "a workflow that sets one of these fails the bin it starts"

        // The whole boundary as a VALUE: what a command line asks of the process. It used to
        // be reachable only by starting a bin and watching it stop, which is why none of it
        // was pinned — a `--version` that printed the usage would have shipped.
        testCase "a command line a bin can run on proceeds, carrying the parse" <| fun () ->
            match Cli.outcome spec "1.2.3" [| "--auth"; "localhost" |] with
            | Cli.Outcome.Proceed p -> Expect.equal (Cli.valueOf auth p) (Some "localhost") "the parse it carries"
            | other -> failwithf "expected a parse to proceed, got %A" other

        testCase "--version is answered with the version, and nothing is parsed off it" <| fun () ->
            match Cli.outcome spec "1.2.3" [| "--version" |] with
            | Cli.Outcome.Answered text -> Expect.equal text "1.2.3" "the version it was given"
            | other -> failwithf "expected an answer, got %A" other

        testCase "--help is answered with the usage" <| fun () ->
            match Cli.outcome spec "1.2.3" [| "--help" |] with
            | Cli.Outcome.Answered text -> Expect.equal text (Cli.usage spec) "the same usage --help prints"
            | other -> failwithf "expected an answer, got %A" other

        testCase "a line carrying both --version and --help answers the version" <| fun () ->
            // Two questions, one process: whichever is answered, the other is not, so which
            // one wins is a decision rather than an accident of evaluation order.
            match Cli.outcome spec "1.2.3" [| "--help"; "--version" |] with
            | Cli.Outcome.Answered text -> Expect.equal text "1.2.3" "the version"
            | other -> failwithf "expected an answer, got %A" other

        testCase "a command line that is wrong is refused, in the parser's own words" <| fun () ->
            match Cli.outcome spec "1.2.3" [| "--auht"; "localhost" |] with
            | Cli.Outcome.Refused complaint -> Expect.equal complaint (refused [ "--auht"; "localhost" ]) "the parse's complaint"
            | other -> failwithf "expected a refusal, got %A" other

        // What an option's own vocabulary buys. These four are the ones that used to be
        // unreachable: the refusal lived in `Main.fs`, which is the composition root, and the
        // cheap tier cannot build one.
        testCase "a value the option's vocabulary does not know refuses the command line" <| fun () ->
            let message = refused [ "--colour"; "banana" ]
            Expect.isTrue (message.Contains "unknown colour 'banana'") "the vocabulary's own words"
            Expect.isTrue (message.StartsWith "yession-manager: ") "in the parser's voice, naming the bin"
            Expect.isTrue (message.Contains "usage: yession-manager") "and carrying the usage, as a typo does"

        testCase "a value the vocabulary knows reads back as what it MEANS" <| fun () ->
            // Not the text the operator typed: the point of a vocabulary is that what comes
            // back has already been made sense of, so no caller can make different sense of it.
            Expect.equal (Cli.valueOf colour (parsed [ "--colour"; "red" ])) "red" "what it came to"

        testCase "an option answers for its own absence" <| fun () ->
            // Absence is a value the vocabulary gives, never a default the caller remembers —
            // which is what stopped `--auth` being deny-everything by whoever read it last.
            Expect.equal (Cli.valueOf colour (parsed [])) "unpainted" "the vocabulary's answer for nothing"
            Expect.isFalse (Cli.isSet colour (parsed [])) "while still not counting as given"

        testCase "--version answers even beside a value this bin refuses" <| fun () ->
            // `SessionMain.fs` promises `--version` and `--help` answer "before any
            // configuration is read". Running the vocabularies inside the parse would have
            // quietly broken that: a bin could no longer say what it is while misconfigured,
            // which is exactly when somebody asks.
            match Cli.outcome spec "1.2.3" [| "--colour"; "banana"; "--version" |] with
            | Cli.Outcome.Answered text -> Expect.equal text "1.2.3" "still says what this bin is"
            | other -> failwithf "expected the version, got %A" other

        testCase "a repeatable option's vocabulary reads the whole set, not each value alone" <| fun () ->
            // The reason `parsedValues` hands over the list rather than one value at a time: a
            // rule like "declared twice" is a disagreement BETWEEN declarations, and no reader
            // that saw them singly could ever state it.
            Expect.equal (Cli.valueOf tags (parsed [ "--tag"; "a"; "--tag"; "b" ])) [ "a"; "b" ] "both, in order"
            let message = refused [ "--tag"; "a"; "--tag"; "a" ]
            Expect.isTrue (message.Contains "given more than once") "and the set's own rule refuses"

        testCase "an option this spec never declared cannot be read off its parse" <| fun () ->
            // A mistake in the program, not on the command line, so it says so rather than
            // answering as though the operator had given nothing — which would read as a
            // legitimate absence and be believed. Pinned because `valueOf` is total for every
            // declared option, and this is the one way that promise can be asked of it wrongly.
            let undeclared = Cli.value "nowhere" "x" "an option no spec here declares"
            Expect.throws
                (fun () -> Cli.valueOf undeclared (parsed []) |> ignore)
                "reading an undeclared option is refused, not answered"

        testCase "a rule spanning several options complains in the same voice a parse failure does" <| fun () ->
            // A bad VALUE is the parse's to refuse now, because the option carries its own
            // vocabulary. What is left for `complaint` is what no single option can say —
            // `--detailed` needing `--check`, an environment still setting a variable that
            // moved onto one. An operator must not hear a different voice for those, so the
            // wording has one author, and the Host adds only the stopping.
            let message = Cli.complaint spec "--detailed says what a --check report means, so it needs --check"
            Expect.isTrue (message.StartsWith "yession-manager: ") "names the bin, as a parse failure does"
            Expect.isTrue (message.Contains "it needs --check") "says what was wrong"
            Expect.isTrue (message.Contains "usage: yession-manager") "and carries the usage under it"

        testCase "a bin with no options of its own still answers version and help" <| fun () ->
            // `yession-session` and `yession-serial` take everything from the environment.
            let bare = Cli.spec "yession-session"
            match Cli.parse bare [| "--version" |] with
            | Ok p -> Expect.isTrue (Cli.isSet Cli.version p) "version still parses"
            | Error e -> failwithf "expected a parse, got: %s" e
            match Cli.parse bare [| "--auth"; "localhost" |] with
            | Error _ -> ()
            | Ok _ -> failwith "a bin that declares no options must refuse one"
    ]
