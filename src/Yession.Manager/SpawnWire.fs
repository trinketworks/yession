namespace Yession.Manager

open Yession.Domain
open Yession.Codecs

#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

// The spawn contract between a Manager and each Session it launches: one environment
// variable in, one readiness line out on stdout. The Manager owns both halves, whichever way
// they travel — it outlives every Session it launches and changes only when none is running,
// so the contract lives on the side that holds it still.
//
// What the Manager mints for ONE launch, as one value rather than six environment
// variables. The six were minted at a single site and read back with five independent
// `envOr` fallbacks, and the FALLBACKS were the defect: an unset session id fabricated
// one, so a Session nobody launched was indistinguishable from one whose launch
// forgot to say who it was. The url and the secret were separately re-matched into a pair
// at three call sites, which is a product type spelled as two strings.

/// The control leg back to the Manager: supervision reports, secrets custody, and this
/// launch's OAuth client registration all authenticate with the one per-launch secret.
///
/// A PAIR, because it has only ever been used as one — a url with no secret cannot be
/// called, and a secret with no url has nowhere to go.
type LaunchControl =
    { Endpoint : ControlEndpoint
      Secret : string }

/// Where the Manager's control endpoint answers, as a session dials it. The Manager's own
/// listener decides which (`ProcessManager.Listener`): a TCP port answers at a url, and a
/// unix socket at a path, which nothing but this process tree's own user can open.
and [<RequireQualifiedAccess>] ControlEndpoint =
    | Address of url: string
    | Socket of path: string

[<RequireQualifiedAccess>]
module ControlEndpoint =

    /// The url a route is joined onto. A socket request carries a url too, for its path and
    /// its `host` header; the host names nothing a resolver is asked about.
    let baseUrl (endpoint: ControlEndpoint) : string =
        match endpoint with
        | ControlEndpoint.Address url -> url
        | ControlEndpoint.Socket _ -> "http://yession-manager"

    /// The Manager's own url, where a browser could follow it — none for a socket, which no
    /// browser dials. A session behind a socket has the public origin to send people to
    /// (`PublicAccess.managerUrlOr`), and the Manager refuses a socket without one.
    let url (endpoint: ControlEndpoint) : string option =
        match endpoint with
        | ControlEndpoint.Address url -> Some url
        | ControlEndpoint.Socket _ -> None

/// Everything the Manager mints for one launch, decoded once at the process boundary and
/// passed downward from there. Nothing reaches back up to the environment for a piece of it.
type Launch =
    { Session : SessionId
      /// Where this session's durable state lives, as the Manager named it. Made absolute
      /// at the boundary that uses it: a relative path works for whatever resolves it once
      /// and silently breaks whatever resolves it twice.
      DataDir : string
      /// The port to listen on. `0` — OS-assigned — is what the Manager always mints, set
      /// explicitly rather than omitted so an operator's stray value cannot reach a child
      /// and pin every session to one port.
      Port : int
      /// `None` = nobody to report to. A bare `yession-session` run is unsupervised, and
      /// its HTTP surface is ungated.
      Control : LaunchControl option
      /// Watch stdin and exit when it closes. The Manager's death closes it — the kernel
      /// does this even on SIGKILL — which is how a session never outlives its Manager.
      ParentGuard : bool }

module Launch =

    /// The one variable the Manager mints and the Session decodes.
    ///
    /// MINTED, NEVER AUTHORED. Anything that could set this could claim to be a session the
    /// Manager launched, holding that launch's control secret — which is custody of the
    /// session's secrets and the authority to register as an OIDC client. `yession.yaml`
    /// refuses the whole `YESSION_` prefix for exactly this reason, and the host baseline
    /// is an allowlist, so this never reaches a sandboxed command either.
    ///
    /// READ ONCE, THEN GONE. The Session takes it out of its own environment as it decodes it
    /// (`Interop.takeEnv`), so a child it spawns with that environment — the srt host helper
    /// is one — never holds the secret.
    [<Literal>]
    let Variable = "YESSION_LAUNCH"

    /// A Session nobody launched: `yession-session` run by hand, or a test harness
    /// driving one directly. ONE value, so "there is no Manager" is a thing to point at
    /// rather than five defaults that can disagree about it.
    let unlaunched : Launch =
        { Session = SessionId.local
          DataDir = sprintf ".yession/sessions/%s" (SessionId.value SessionId.local)
          Port = 0
          Control = None
          ParentGuard = false }

    let private controlDecoder : Decoder<LaunchControl> =
        Decode.object (fun get ->
            { Endpoint =
                get.Optional.Field "socket" Decode.string
                |> Option.map ControlEndpoint.Socket
                |> Option.defaultWith (fun () -> ControlEndpoint.Address (get.Required.Field "url" Decode.string))
              Secret = get.Required.Field "secret" Decode.string })

    let private decoder : Decoder<Launch> =
        Decode.object (fun get ->
            { Session =
                get.Required.Field
                    "session"
                    (Decode.string
                     |> Decode.andThen (fun raw ->
                         match SessionId.create raw with
                         | Ok id -> Decode.succeed id
                         | Error e -> Decode.fail e))
              DataDir = get.Required.Field "dataDir" Decode.string
              Port = get.Required.Field "port" Decode.int
              Control = get.Optional.Field "control" controlDecoder
              ParentGuard = get.Optional.Field "parentGuard" Decode.bool |> Option.defaultValue false })

    let private encoder (launch: Launch) =
        let control =
            match launch.Control with
            | Some c ->
                let endpoint =
                    match c.Endpoint with
                    | ControlEndpoint.Address url -> "url", Encode.string url
                    | ControlEndpoint.Socket path -> "socket", Encode.string path
                [ "control", Encode.object [ endpoint; "secret", Encode.string c.Secret ] ]
            | None -> []
        Encode.object
            ([ "session", Encode.string (SessionId.value launch.Session)
               "dataDir", Encode.string launch.DataDir
               "port", Encode.int launch.Port
               "parentGuard", Encode.bool launch.ParentGuard ]
             @ control)

    /// What the Manager puts in the variable.
    let encode (launch: Launch) : string = encoder launch |> Encode.toString 0

    /// What the Session reads back.
    ///
    /// Blank or absent is the UNLAUNCHED case, not an error — `yession-session` run by hand
    /// still runs. Anything else that fails to decode IS an error, and fails the boot: a
    /// malformed envelope means the Manager and the session disagree about the contract, and
    /// carrying on under a fabricated identity is how that disagreement goes silent.
    let parse (raw: string) : Result<Launch, string> =
        if isNull (box raw) || raw.Trim() = "" then Ok unlaunched
        else Decode.fromString decoder raw |> Result.mapError (sprintf "%s: %s" Variable)

/// The readiness line, as the spawn contract states it: `{"yession":"ready","port":N}`, and
/// `version` from a bundle new enough to carry one. That field is optional and stays
/// optional — an older session must still launch — so it is the one thing here that a line
/// may leave out and still be a readiness line.
type ReadyLine = { Port : int; Version : string option }

[<RequireQualifiedAccess>]
module ReadyLine =
    /// The line is whatever the child printed, so it is DECODED rather than probed: `null`, a
    /// number, and an object with none of these fields all have to arrive as "not this message".
    /// Three `typeof` macros used to ask that field by field, over two parses of the same line.
    let private decoder : Decoder<ReadyLine> =
        Decode.field "yession" Decode.string
        |> Decode.andThen (fun said ->
            if said <> "ready" then
                Decode.fail "not a readiness line"
            else
                Decode.map2
                    (fun port version -> { Port = port; Version = version })
                    (Decode.field "port" Strict.int)
                    (Decode.optional "version" Decode.string))

    /// What a line the child printed says, or nothing. A log line, a half-line and anything that
    /// is not this message are the same nothing — a line that cannot be read is not an error
    /// here, it is a line the child printed for a person to read.
    ///
    /// Public so that back-compat can be asserted directly: a bundle that states no version is
    /// still a launch.
    let parse (line: string) : ReadyLine option =
        Decode.fromString decoder line |> Result.toOption

    /// What a Session prints once it is listening. The one line its Manager waits for, so it
    /// is written by the same declaration that reads it rather than by a format string beside
    /// it: `version` used to be pasted between quotes unescaped.
    let encode (line: ReadyLine) : string =
        Encode.object
            ([ "yession", Encode.string "ready"; "port", Encode.int line.Port ]
             @ (line.Version |> Option.map (fun v -> [ "version", Encode.string v ]) |> Option.defaultValue []))
        |> Encode.toString 0
