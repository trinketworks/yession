namespace Yession.Session

open Yession.Domain
open Yession.Domain.Sandboxes

#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

// Reading a repo's `yession.yaml` (Plan 27): the Session's, because the Session is what
// reads a checkout. The model it decodes into is the domain's (`ConfigFile`, `SandboxDecl`).
//
// PURE and parser-free. It decodes an already-parsed JSON tree, which is what lets the whole
// of it — every refusal included — run in the cheap tier on both runtimes from JSON
// literals. YAML is a superset of JSON, so the bridge that reads the file only has to hand
// this a tree; swapping the surface syntax later is a parser swap.

[<RequireQualifiedAccess>]
module ConfigFile =

    /// The name the file has at the root of a checkout.
    [<Literal>]
    let FileName = "yession.yaml"

    /// The only version this build speaks.
    [<Literal>]
    let Version = 2

    /// Variables a file may not set, by prefix.
    ///
    /// `YESSION_LAUNCH` carries a launch's control secret — custody of the session's
    /// secrets and the authority to register as an OIDC client — and `YESSION_BIN_*` names
    /// a binary this host will execute. Rather than enumerate which of them are dangerous
    /// and re-decide every time one is added, the whole prefix is refused: everything under
    /// it is either the operator's or nobody's.
    ///
    /// A REFUSAL and not a filter. Silently dropping the variable would leave a file that
    /// reads as if it had been applied.
    [<Literal>]
    let ReservedPrefix = "YESSION_"

    let private failIf (condition: bool) (message: string) (decoder: Decoder<'a>) : Decoder<'a> =
        if condition then Decode.fail message else decoder

    /// Refuse any key the schema does not define.
    ///
    /// A typo that decodes to "nothing was asked for" is the failure mode this whole file
    /// exists to avoid: it reads as configuration and behaves as none.
    let private noUnknownKeys (known: string list) : Decoder<unit> =
        Decode.keys
        |> Decode.andThen (fun keys ->
            match keys |> List.filter (fun k -> not (List.contains k known)) with
            | [] -> Decode.succeed ()
            | unknown ->
                Decode.fail (
                    sprintf
                        "unknown %s: %s (known: %s)"
                        (if List.length unknown = 1 then "key" else "keys")
                        (String.concat ", " (List.sort unknown))
                        (String.concat ", " known)))

    /// Resource names, refused where they are written rather than at the sandbox that would
    /// have used them.
    let private resourceNames : Decoder<ResourceName list> =
        Decode.oneOf [ Decode.list Decode.string; Decode.string |> Decode.map List.singleton ]
        |> Decode.andThen (fun raws ->
            raws
            |> List.fold
                (fun acc raw ->
                    acc |> Result.bind (fun taken -> ResourceName.create raw |> Result.map (fun n -> taken @ [ n ])))
                (Ok [])
            |> function
                | Ok names -> Decode.succeed names
                | Error e -> Decode.fail e)

    let private stringList : Decoder<string list> =
        Decode.oneOf [ Decode.list Decode.string; Decode.string |> Decode.map List.singleton ]

    /// `NAME: value` or `NAME: { secret: name }`. A string carrying `${` composes
    /// (`EnvTemplate`): `${env.NAME}` is what NAME would be without this line, and `$${` a
    /// literal `${`. A reference this build cannot read refuses the file rather than being
    /// kept as text somebody meant as a reference.
    let private envValue : Decoder<EnvironmentVariableRef> =
        Decode.oneOf
            [ Decode.string
              |> Decode.andThen (fun text ->
                  match EnvTemplate.lent text with
                  | Some connection ->
                      match ConnectionName.create connection with
                      | Ok name -> Decode.succeed (Lent name)
                      | Error e -> Decode.fail (sprintf "'%s' does not name a connection: %s" text e)
                  | None ->
                  if not (EnvTemplate.composes text) then Decode.succeed (PlainValue text)
                  else
                      match EnvTemplate.parse text with
                      | Error e -> Decode.fail e
                      | Ok template ->
                          match template with
                          | [] -> Decode.succeed (PlainValue "")
                          | [ TemplatePart.Literal text ] -> Decode.succeed (PlainValue text)
                          | template -> Decode.succeed (Derived template))
              Decode.field "secret" Decode.string
              |> Decode.andThen (fun raw ->
                  match SecretName.create raw with
                  | Ok name -> Decode.succeed (SecretRef name)
                  | Error e -> Decode.fail e) ]

    let private environment : Decoder<Map<string, EnvironmentVariableRef>> =
        Decode.keyValuePairs envValue
        |> Decode.andThen (fun pairs ->
            match pairs |> List.map fst |> List.filter (fun k -> k.StartsWith ReservedPrefix) with
            | [] -> Decode.succeed (Map.ofList pairs)
            | reserved ->
                Decode.fail (
                    sprintf
                        "%s is reserved. A repo may not set it: %s"
                        ReservedPrefix
                        (String.concat ", " (List.sort reserved))))

    let private image : Decoder<ContainerImage> =
        Decode.string
        |> Decode.map (fun raw ->
            match raw.Split ':' with
            | [| name; tag |] -> { Name = name; Tag = Some tag }
            | _ -> { Name = raw; Tag = None })

    /// A path INSIDE the checkout, and the only kind of path a file may write.
    ///
    /// A `yession.yaml` is authored by whoever can push to the repo, so an absolute path is
    /// that author naming a place on somebody else's machine, and a `..` segment is the same
    /// thing spelled relatively. Both are refused where they are WRITTEN — the person who
    /// can fix it is standing here — rather than resolved later against a checkout, which is
    /// how `workdir: /etc` becomes a sandbox that starts in /etc.
    let private inCheckout (what: string) : Decoder<string> =
        Decode.string
        |> Decode.andThen (fun raw ->
            let path = raw.Trim ()
            let segments = path.Split ([| '/'; '\\' |]) |> List.ofArray
            if path = "" then Decode.fail (sprintf "%s cannot be blank" what)
            elif path.StartsWith "/" || path.StartsWith "\\" then
                Decode.fail (sprintf "%s must be inside the checkout. '%s' is absolute" what path)
            elif segments |> List.contains ".." then
                Decode.fail (sprintf "%s must be inside the checkout. '%s' climbs out of it" what path)
            else Decode.succeed path)

    /// `repos:` — where the session's checkouts appear in this sandbox.
    ///
    /// `build:` — a context directory, and optionally a dockerfile within it.
    ///
    /// Both paths go through `inCheckout` for the same reason `workdir` does: the context
    /// is read on the machine running the session — the daemon client streams it from
    /// this filesystem — so a context a repo could point anywhere is arbitrary host-file
    /// read lifted into an image the repo's own sandbox then opens. This decoder was the
    /// one path-carrying field that read a bare string, three lines below the comment
    /// explaining why nothing may.
    let private build : Decoder<ContainerBuildSpec> =
        Decode.oneOf
            [ inCheckout "a build context" |> Decode.map (fun path -> { ContextPath = path; DockerfilePath = None })
              Decode.object (fun get ->
                  { ContextPath = get.Required.Field "context" (inCheckout "a build context")
                    DockerfilePath = get.Optional.Field "dockerfile" (inCheckout "a build dockerfile") }) ]

    /// What a file may mount, and it is deliberately only the sandbox's own workspace.
    ///
    /// `HostPath` exists and the SESSION uses it — that is how the repos directory reaches a
    /// container — but a source a repo could name is arbitrary read/write access to the
    /// machine running the session, which is the same authority `YESSION_BIN_*` carries and
    /// is refused for the same reason.
    ///
    /// `NamedVolume` exists and the OPERATOR grants it (a `volume:` resource, selected by
    /// `uses:`). A docker volume is host-global — the same name is the same volume in every
    /// session's containers, persistent across all of them — so a file that could name one
    /// could read and seed another session's state. This used to say "a named volume is the
    /// session's to create", which was the workspace volume's property wrongly generalised:
    /// nothing scoped an arbitrary name to a session.
    let private sourceOf (raw: string) : Result<MountSource, string> =
        match raw.Trim () with
        | "workspace" -> Ok SessionWorkspace
        | "" -> Error "a volume needs a source"
        | source ->
            Error (
                sprintf
                    "'%s' is not a source a %s may name. Write 'workspace'. The operator offers a shared named volume as a `volume:` resource. Select it with `uses:`"
                    source
                    FileName)

    let private mountSource : Decoder<MountSource> =
        Decode.string
        |> Decode.andThen (fun raw ->
            match sourceOf raw with
            | Ok source -> Decode.succeed source
            | Error reason -> Decode.fail reason)

    /// `read_only`, compose's word. `mode: ro` was this file's own, and is refused by name.
    let private mountKeys = [ "type"; "source"; "target"; "read_only" ]

    /// A volume in compose's long syntax: `{ type: volume, source, target, read_only }`.
    /// `type` may be left out, and the only type a file may write is `volume`: a `bind` is a
    /// host path, which `mountSource` refuses for the reason it gives.
    let private longMount : Decoder<ContainerMount> =
        Decode.optional "mode" Decode.value
        |> Decode.andThen (function
            | Some _ -> Decode.fail "`mode:` is not compose's. Write `read_only: true` for a read-only volume"
            | None -> noUnknownKeys mountKeys)
        |> Decode.andThen (fun () ->
            Decode.optional "type" Decode.string
            |> Decode.andThen (function
                | None
                | Some "volume" -> Decode.succeed ()
                | Some other ->
                    Decode.fail (
                        sprintf
                            "a volume of type '%s' is not one a %s may name. Write `type: volume` with `source: workspace`"
                            other
                            FileName)))
        |> Decode.andThen (fun () ->
            Decode.object (fun get ->
                { Source = get.Required.Field "source" mountSource
                  Target = get.Required.Field "target" Decode.string
                  Mode =
                    if get.Optional.Field "read_only" Decode.bool |> Option.defaultValue false then ReadOnly
                    else ReadWrite }))

    /// A volume in compose's short syntax: `source:target` or `source:target:ro`.
    let private shortMount (raw: string) : Result<ContainerMount, string> =
        let mounted source target mode =
            sourceOf source |> Result.map (fun source -> { Source = source; Target = target; Mode = mode })
        match raw.Split ':' with
        | [| source; target |] -> mounted source target ReadWrite
        | [| source; target; "ro" |] -> mounted source target ReadOnly
        | [| source; target; "rw" |] -> mounted source target ReadWrite
        | _ -> Error (sprintf "'%s' is not a volume. Write source:target or source:target:ro, like workspace:/work:ro" raw)

    /// Either of compose's syntaxes. A string is the short one, anything else the long one,
    /// so a refusal comes from the one syntax the file wrote and not from both.
    let private mount : Decoder<ContainerMount> =
        Decode.oneOf [ Decode.string |> Decode.map Some; Decode.succeed None ]
        |> Decode.andThen (function
            | Some raw ->
                match shortMount raw with
                | Ok mount -> Decode.succeed mount
                | Error reason -> Decode.fail reason
            | None -> longMount)

    /// An argv, as compose writes one: a list of words, or one string split into words the
    /// way a POSIX shell would read them — quotes group, a backslash escapes, nothing is
    /// expanded. The string form is compose's, taken as it is so a line copied from a
    /// compose file means the same thing here; the list form is the one that cannot be
    /// misread, and what the docs prefer.
    let private argv (field: string) : Decoder<string list> =
        Decode.oneOf
            [ Decode.list Decode.string
              Decode.string
              |> Decode.andThen (fun raw ->
                  match ShellWords.split raw with
                  | Ok [] -> Decode.fail (sprintf "%s is empty. Write the words to run. Otherwise, remove the key" field)
                  | Ok words -> Decode.succeed words
                  | Error reason -> Decode.fail (sprintf "%s: %s" field reason)) ]

    /// `mem_limit:` — the container's ceiling, in compose's byte-value grammar (`4g`), or a
    /// bare number of bytes, which is what YAML makes of one written without a unit.
    /// The JSON is read first and judged after, so a size that is refused is refused in
    /// the one sentence that says why, not beside a second decoder's complaint.
    let private memory : Decoder<ContainerMemory> =
        Decode.oneOf
            [ Decode.string |> Decode.map ContainerMemory.parse
              Decode.int64 |> Decode.map ContainerMemory.ofBytes ]
        |> Decode.andThen (function
            | Ok limit -> Decode.succeed limit
            | Error reason -> Decode.fail reason)

    /// A whole number, judged after it is read, for the same reason as `memory`.
    let private judgedInt (judge: int -> Result<'T, string>) : Decoder<'T> =
        Decode.int
        |> Decode.andThen (fun n ->
            match judge n with
            | Ok value -> Decode.succeed value
            | Error reason -> Decode.fail reason)

    /// `cgroup_parent:` — the cgroup the container's own is put under (`CgroupParent`).
    let private cgroupParent : Decoder<CgroupParent> =
        Decode.string
        |> Decode.andThen (fun raw ->
            match CgroupParent.parse raw with
            | Ok parent -> Decode.succeed parent
            | Error reason -> Decode.fail reason)

    let private containerKeys =
        [ "image"; "build"; "volumes"; "command"; "entrypoint"; "mem_limit"; "oom_score_adj"; "pids_limit"; "cgroup_parent" ]

    /// The spellings this block had before it was a compose service fragment, each with
    /// compose's name for the same key. Refused by name, not left to `noUnknownKeys`: a
    /// file that has one worked yesterday, and its author needs the word that replaced it.
    let private renamedKeys =
        [ "memory", "mem_limit"
          "oom-score-adj", "oom_score_adj"
          "pids-limit", "pids_limit"
          "cgroup-parent", "cgroup_parent"
          "cmd", "command" ]

    /// Compose service keys this block does not take, each with what this file offers
    /// instead. A compose key copied in must not read as the unknown key of a typo: it is a
    /// word, and its author is told where its meaning lives here, or that it has none.
    let private unsupportedKeys =
        [ "deploy", "`deploy:` is not read. Write the memory ceiling as `mem_limit:` on the container block"
          "ports", "`ports:` is not read. A sandbox's process is reachable from inside that sandbox only"
          "network_mode", "`network_mode:` is not read. The session puts the container on the session's own network"
          "privileged", "`privileged:` is not read. A container never runs privileged. The operator grants a socket or a mount as a resource. Select it with `uses:`"
          "cap_add", "`cap_add:` is not read. A container drops every capability but CHOWN, FOWNER and DAC_OVERRIDE. The operator grants a socket or a mount as a resource. Select it with `uses:`"
          "environment", "`environment:` is not read here. Write `env:` on the sandbox, beside `container:`"
          "working_dir", "`working_dir:` is not read here. Write `workdir:` on the sandbox, beside `container:`"
          "depends_on", "`depends_on:` is not read. Each sandbox starts on its own"
          "restart", "`restart:` is not read. A sandbox lives exactly as long as its `command`"
          "user", "`user:` is not read. A command runs as the user that the image names" ]

    let private noForeignKeys : Decoder<unit> =
        Decode.keys
        |> Decode.andThen (fun keys ->
            let renamed =
                renamedKeys
                |> List.tryFind (fun (old, _) -> List.contains old keys)
                |> Option.map (fun (old, compose) ->
                    sprintf "`%s:` is now `%s:`, compose's name for it. Write `%s:`" old compose compose)
            let unsupported =
                unsupportedKeys |> List.tryFind (fun (key, _) -> List.contains key keys) |> Option.map snd
            match Option.orElse unsupported renamed with
            | Some reason -> Decode.fail reason
            | None -> Decode.succeed ())

    /// The container block: a compose service fragment, read with compose's names and
    /// meanings. `command` lives HERE and nowhere else, which is the whole reason the block
    /// exists: a sandbox with no container has no place to write one.
    let private container : Decoder<ContainerSpec> =
        noForeignKeys
        |> Decode.andThen (fun () -> noUnknownKeys containerKeys)
        |> Decode.andThen (fun () ->
            Decode.object (fun get ->
                { Image = get.Optional.Field "image" image
                  Build = get.Optional.Field "build" build
                  Mounts = get.Optional.Field "volumes" (Decode.list mount) |> Option.defaultValue []
                  Command = get.Optional.Field "command" Decode.string
                  Entrypoint = get.Optional.Field "entrypoint" (argv "entrypoint")
                  Dialect = None
                  Memory = get.Optional.Field "mem_limit" memory
                  OomScoreAdj = get.Optional.Field "oom_score_adj" (judgedInt OomScoreAdj.ofInt)
                  PidsLimit = get.Optional.Field "pids_limit" (judgedInt PidsLimit.ofInt)
                  CgroupParent = get.Optional.Field "cgroup_parent" cgroupParent }))

    let private sandboxKeys =
        [ "container"; "dialect"; "workdir"; "env"; "uses"; "wants"; "files"; "setup"; "description" ]

    /// `forward:` said which connections a sandbox forwarded, and a connection is a resource
    /// now: offered by the operator and selected like every other. Refused by name rather
    /// than left to the unknown-key sentence, because the file that has it worked yesterday
    /// and its author needs to be told what replaced it, not that it was never a word.
    let private noForward : Decoder<unit> =
        Decode.optional "forward" Decode.value
        |> Decode.andThen (function
            | Some _ ->
                Decode.fail
                    "`forward:` no longer exists. A connection is a resource now. The operator offers it (`github: { connection: { github: [git, api] } }`). A sandbox selects it under `uses` or `wants`"
            | None -> Decode.succeed ())

    /// `repos:` moved where a container saw the session's checkouts. It is refused by name,
    /// like `forward:` above, because a file that had it worked yesterday. The checkouts now
    /// have ONE absolute path, the same on the host and in every sandbox
    /// (`Sandboxes.checkoutAt`), so there is nothing left to move: a second address is
    /// what let a git worktree made in a container point every other view at a directory
    /// that did not exist.
    let private noRepos : Decoder<unit> =
        Decode.optional "repos" Decode.value
        |> Decode.andThen (function
            | Some _ ->
                Decode.fail
                    "`repos:` no longer exists. The session's checkouts have one path, the same in every sandbox and on the host. A sandbox's start says where its checkout is. Use a relative `workdir` to start inside it"
            | None -> Decode.succeed ())

    /// `dialect:` — one of the shells a terminal can instrument, by name.
    let private dialect : Decoder<string> =
        Decode.string
        |> Decode.andThen (fun raw ->
            let name = raw.Trim ()
            if List.contains name TerminalShell.dialects then Decode.succeed name
            else
                Decode.fail
                    (sprintf
                        "'%s' is not a shell dialect that a terminal can instrument. Use one of %s"
                        raw
                        (String.concat ", " TerminalShell.dialects)))

    /// `files:` — a path inside the sandbox's home to the content written there.
    ///
    /// The path is decoded through `HomePath`, so a file that would land outside the home
    /// is refused HERE, naming the path and what is wrong with it, rather than being
    /// normalised into something the writer did not ask for.
    let private seededFiles : Decoder<Map<HomePath, string>> =
        Decode.keyValuePairs Decode.string
        |> Decode.andThen (fun pairs ->
            let rec fold acc remaining =
                match remaining with
                | [] -> Decode.succeed (Map.ofList (List.rev acc))
                | (raw, content) :: rest ->
                    match HomePath.create raw with
                    | Ok path -> fold ((path, content) :: acc) rest
                    | Error reason -> Decode.fail reason
            fold [] pairs)

    let private sandbox : Decoder<SandboxDecl> =
        noForward
        |> Decode.andThen (fun () -> noRepos)
        |> Decode.andThen (fun () -> noUnknownKeys sandboxKeys)
        |> Decode.andThen (fun () ->
            Decode.object (fun get ->
                let container = get.Optional.Field "container" container
                let dialect = get.Optional.Field "dialect" dialect
                { Container = container |> Option.map (fun spec -> { spec with Dialect = dialect })
                  WorkingDirectory = get.Optional.Field "workdir" (inCheckout "workdir")
                  EnvironmentVariables =
                    get.Optional.Field "env" environment |> Option.defaultValue Map.empty
                  Uses = get.Optional.Field "uses" resourceNames |> Option.defaultValue []
                  Wants = get.Optional.Field "wants" resourceNames |> Option.defaultValue []
                  Files = get.Optional.Field "files" seededFiles |> Option.defaultValue Map.empty
                  Setup = get.Optional.Field "setup" Decode.string
                  Description =
                    get.Optional.Field "description" Decode.string
                    |> Option.map (fun said -> said.Trim ())
                    |> Option.filter (fun said -> said <> "") },
                (container, dialect)))
        |> Decode.andThen (fun (decl, (container, dialect)) ->
            // A dialect names the shell a CONTAINER's terminal opens, chosen from what the
            // backend finds behind the entrypoint. A sandbox without a container has no
            // such choice — the session's own shell is what it gets — so the key is refused
            // there rather than read and ignored.
            match dialect, container with
            | Some _, None -> Decode.fail "`dialect` chooses a container's shell. This sandbox declares no `container`"
            | _ -> Decode.succeed decl)

    /// Sandbox names, refusing a clash INSIDE one file.
    ///
    /// This is the only place a clash can happen — across files the scope keeps them apart —
    /// and it is refused here, where the person who wrote both is standing and can pick
    /// another name, rather than resolved at read time by a precedence rule.
    /// The `sandboxes:` block, which an operator's profile carries in the same form
    /// (`OperatorProfile`, through `parseSandboxes`): one decoder, so one declaration means
    /// one thing whoever wrote it.
    let private sandboxes : Decoder<Map<SandboxName, SandboxDecl>> =
        // Decoded a field at a time rather than with `keyValuePairs`, for the PATH. That
        // combinator decodes each value without putting its key on the path, so every
        // refusal inside any sandbox came back as `$.sandboxes.workdir` — the same address
        // whichever sandbox wrote it. Reading `Decode.field name` per key costs one fold and
        // makes the refusal say `$.sandboxes.dev.workdir`, which is the difference between
        // fixing a file with two sandboxes in it and fixing one with ten.
        Decode.keys
        |> Decode.andThen (fun raws ->
            raws
            |> List.map (fun raw -> Decode.field raw sandbox |> Decode.map (fun decl -> raw, decl))
            |> List.fold (fun acc one -> Decode.map2 (fun xs x -> xs @ [ x ]) acc one) (Decode.succeed []))
        |> Decode.andThen (fun pairs ->
            let named =
                pairs
                |> List.map (fun (raw, decl) -> SandboxName.create raw |> Result.map (fun n -> n, decl))
            match named |> List.choose (function Error e -> Some e | Ok _ -> None) with
            | e :: _ -> Decode.fail e
            | [] ->
                let entries = named |> List.choose (function Ok v -> Some v | Error _ -> None)
                let names = entries |> List.map fst
                match names |> List.countBy id |> List.filter (fun (_, n) -> n > 1) with
                | [] -> Decode.succeed (Map.ofList entries)
                | dupes ->
                    Decode.fail (
                        sprintf
                            "declared twice: %s"
                            (dupes |> List.map (fst >> SandboxName.value) |> List.sort |> String.concat ", ")))

    /// `session:` — what the repo asks of the session itself. A path in it is a path inside
    /// the checkout, refused where it is written like every other (`inCheckout`).
    let private session : Decoder<SessionDecl> =
        noUnknownKeys [ "shell-profile" ]
        |> Decode.andThen (fun () ->
            Decode.object (fun get ->
                { ShellProfile = get.Optional.Field "shell-profile" (inCheckout "shell-profile") }))

    let private fileKeys = [ "version"; "sandboxes"; "session" ]

    let decoder : Decoder<ConfigFile> =
        noUnknownKeys fileKeys
        |> Decode.andThen (fun () ->
            Decode.field "version" Decode.int
            |> Decode.andThen (fun version ->
                failIf
                    (version <> Version)
                    (sprintf "this build speaks %s version %d, not %d" FileName Version version)
                    (Decode.object (fun get ->
                        { Version = version
                          Sandboxes =
                            get.Optional.Field "sandboxes" sandboxes |> Option.defaultValue Map.empty
                          Session = get.Optional.Field "session" session |> Option.defaultValue SessionDecl.empty }))))

    /// A `sandboxes:` block on its own, from JSON text — how the operator's profile, which
    /// is decoded with a different JSON library on .NET, hands its block to the one decoder.
    let parseSandboxes (json: string) : Result<Map<SandboxName, SandboxDecl>, string> =
        Decode.fromString sandboxes json

    /// Decode one repo's file from already-parsed JSON text.
    let parse (json: string) : Result<ConfigFile, string> =
        Decode.fromString decoder json

    /// ONE sandbox block, on its own.
    ///
    /// What crosses the command gate (`start_work_sandbox`) is a declaration, because a
    /// declaration is what both callers have: the agent's names some credentials, a file's
    /// names everything. One shape, so the declarative route and the interactive one cannot
    /// diverge — which is the reason the gate is a capability rather than a detail of the
    /// MCP adapter.
    ///
    /// And the round trip is a real property, not a convenience: whatever crosses the gate
    /// is expressible in a `yession.yaml`, so every refusal the file's own schema makes —
    /// the reserved prefix, a host-path volume, a workdir outside the checkout — applies to
    /// a gated call for free rather than needing a second copy.
    let parseSandbox (json: string) : Result<SandboxDecl, string> =
        Decode.fromString sandbox json

    /// What ONE repo's file contributes to the session, keyed so it cannot collide with
    /// another repo's.
    let scoped (repo: RepoRef) (file: ConfigFile) : Map<SandboxRef, SandboxDecl> =
        file.Sandboxes
        |> Map.toList
        |> List.map (fun (name, decl) -> SandboxRef.inScope repo name, decl)
        |> Map.ofList

    /// Every declared sandbox in the session, from every repo that carries a file.
    ///
    /// TOTAL, and that is the whole point: the keys are (repo, name) pairs and the repos
    /// are disjoint, so this is a union that cannot lose an entry and has no order
    /// dependence. There is deliberately no merge rule, because there is nothing to merge.
    let union (files: (RepoRef * ConfigFile) list) : Map<SandboxRef, SandboxDecl> =
        files
        |> List.collect (fun (repo, file) -> scoped repo file |> Map.toList)
        |> Map.ofList

    /// Exists so the command gate can carry a declaration (see `parseSandbox`).
    /// The round trip through `parseSandbox` is what makes that safe: anything this writes,
    /// the file's own schema must be willing to read, so a gated call cannot smuggle a shape
    /// a `yession.yaml` would have been refused for.
    let encodeSandbox (decl: SandboxDecl) : string =
        let container =
            decl.Container
            |> Option.map (fun container ->
                Encode.object
                    [ if container.Image.IsSome then
                        "image", Encode.string (ContainerImage.render container.Image.Value)
                      if container.Build.IsSome then
                        let build = container.Build.Value
                        "build",
                        Encode.object
                            [ "context", Encode.string build.ContextPath
                              if build.DockerfilePath.IsSome then
                                "dockerfile", Encode.string build.DockerfilePath.Value ]
                      if not (List.isEmpty container.Mounts) then
                        "volumes",
                        Encode.list (
                            container.Mounts
                            |> List.map (fun mount ->
                                Encode.object
                                    [ "type", Encode.string "volume"
                                      "source",
                                      Encode.string (
                                          match mount.Source with
                                          | SessionWorkspace -> "workspace"
                                          | NamedVolume name -> name
                                          // Written as it stands, and then REFUSED on the
                                          // way back in: the schema is the one place a host
                                          // path is rejected, so a second refusal here
                                          // would be a spare that could disagree with it.
                                          // The consequence is the intended one — a
                                          // host-path mount cannot cross the gate.
                                          | HostPath path -> path)
                                      "target", Encode.string mount.Target
                                      "read_only", Encode.bool (mount.Mode = ReadOnly) ]))
                      if container.Command.IsSome then "command", Encode.string container.Command.Value
                      if container.Entrypoint.IsSome then
                        "entrypoint", Encode.list (container.Entrypoint.Value |> List.map Encode.string)
                      if container.Memory.IsSome then "mem_limit", Encode.string (ContainerMemory.render container.Memory.Value)
                      if container.OomScoreAdj.IsSome then "oom_score_adj", Encode.int (OomScoreAdj.value container.OomScoreAdj.Value)
                      if container.PidsLimit.IsSome then "pids_limit", Encode.int (PidsLimit.value container.PidsLimit.Value)
                      if container.CgroupParent.IsSome then
                        "cgroup_parent", Encode.string (CgroupParent.value container.CgroupParent.Value) ])
        let env =
            decl.EnvironmentVariables
            |> Map.toList
            |> List.map (fun (name, value) ->
                name,
                match value with
                | PlainValue plain when EnvTemplate.composes plain -> Encode.string (EnvTemplate.escape plain)
                | PlainValue plain -> Encode.string plain
                | SecretRef secret -> Encode.object [ "secret", Encode.string (SecretName.value secret) ]
                | Derived template -> Encode.string (EnvTemplate.render template)
                | Lent connection -> Encode.string (sprintf "${%s.token}" (ConnectionName.value connection)))
        let strings (names: string list) = Encode.list (names |> List.map Encode.string)
        Encode.toString 0 (
            Encode.object
                [ if container.IsSome then "container", container.Value
                  match decl.Container |> Option.bind (fun spec -> spec.Dialect) with
                  | Some dialect -> "dialect", Encode.string dialect
                  | None -> ()
                  if decl.WorkingDirectory.IsSome then "workdir", Encode.string decl.WorkingDirectory.Value
                  if not (Map.isEmpty decl.EnvironmentVariables) then "env", Encode.object env
                  if not (List.isEmpty decl.Uses) then "uses", strings (decl.Uses |> List.map ResourceName.value)
                  if not (List.isEmpty decl.Wants) then "wants", strings (decl.Wants |> List.map ResourceName.value)
                  if not (Map.isEmpty decl.Files) then
                    "files",
                    Encode.object (
                        decl.Files
                        |> Map.toList
                        |> List.map (fun (path, content) -> HomePath.value path, Encode.string content))
                  if decl.Setup.IsSome then "setup", Encode.string decl.Setup.Value
                  if decl.Description.IsSome then "description", Encode.string decl.Description.Value ])
