namespace Yession.Session

open System
open Yession.Domain
open Yession.Domain.Agent
open Yession.Domain.Sandboxes

#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

// Reading an operator's `resources.yaml`: the Session's, because the Session is what runs
// on the host the file describes. The model it decodes into (`ProfileFile`) is the
// domain's; this is only how the file becomes one.

[<RequireQualifiedAccess>]
module OperatorProfile =

    /// The name the file has, wherever an operator keeps it.
    [<Literal>]
    let FileName = "resources.yaml"

    /// The only version this build speaks. A file from the future says so rather than losing
    /// half its meaning to a decoder that skips what it cannot read.
    [<Literal>]
    let Version = 1

    let private fileKeys = [ "version"; "resources"; "always"; "agent"; "sandboxes"; "container" ]
    let private containerKeys = [ "mem_limit"; "pids_limit"; "cgroup_parent" ]
    let private agentKeys = [ "guidance"; "prompt" ]
    let private leafKeys = [ "mount"; "socket"; "endpoint"; "env"; "exec"; "volume"; "connection" ]
    let private mountKeys = [ "from"; "at"; "mode" ]
    let private volumeKeys = [ "name"; "at"; "mode"; "maintain" ]
    let private maintainKeys = [ "pin"; "every" ]

    let private failIf (condition: bool) (message: string) (decoder: Decoder<'a>) : Decoder<'a> =
        if condition then Decode.fail message else decoder

    /// Refuse any key the schema does not define. A typo that decodes to "nothing was asked
    /// for" reads as configuration and behaves as none — the failure this file, like
    /// `Config.fs`, exists to avoid.
    let private noUnknownKeys (known: string list) : Decoder<unit> =
        Decode.keys
        |> Decode.andThen (fun keys ->
            match keys |> List.filter (fun key -> not (List.contains key known)) with
            | [] -> Decode.succeed ()
            | unknown ->
                Decode.fail (
                    sprintf
                        "unknown %s: %s (known: %s)"
                        (if List.length unknown = 1 then "key" else "keys")
                        (String.concat ", " (List.sort unknown))
                        (String.concat ", " known)))

    /// One string or several. Every list-shaped key takes both, so an operator never has to
    /// remember which — and a single-item list and a bare string mean the same thing.
    let private stringList : Decoder<string list> =
        Decode.oneOf [ Decode.list Decode.string; Decode.string |> Decode.map List.singleton ]

    let private mountMode : Decoder<ResourceMountMode> =
        Decode.string
        |> Decode.andThen (function
            | "read" -> Decode.succeed ResourceMountMode.Read
            | "write" -> Decode.succeed ResourceMountMode.Write
            | "overlay" -> Decode.succeed ResourceMountMode.Overlay
            | other ->
                Decode.fail (
                    sprintf "'%s' is not a mount mode. A mount is read, write or overlay" other))

    /// `from` is required; `at` defaults to it, because the common case is a path that means
    /// the same on both sides and writing it twice is how the two drift.
    let private mount : Decoder<ResourceMount> =
        noUnknownKeys mountKeys
        |> Decode.andThen (fun () ->
            Decode.object (fun get ->
                let from = get.Required.Field "from" Decode.string
                { From = from
                  At = get.Optional.Field "at" Decode.string |> Option.defaultValue from
                  Mode = get.Optional.Field "mode" mountMode |> Option.defaultValue ResourceMountMode.Read }))

    /// `90s` / `10m` / `2h`, the grammar `--idle-timeout` takes, with a unit always written:
    /// a renewal read as 10 seconds when 10 minutes was meant is a pin run sixty times as
    /// often as asked, in every sandbox, and nothing about it would look wrong.
    let private interval : Decoder<TimeSpan> =
        Decode.string
        |> Decode.andThen (fun raw ->
            let trimmed = raw.Trim ()
            let digits = trimmed.TrimEnd ('s', 'm', 'h')
            let unit = trimmed.Substring digits.Length
            let refused = Decode.fail (sprintf "'%s' is not an interval like 90s, 10m or 2h" raw)
            if digits.Length = 0 || digits.Length > 9 || not (digits |> Seq.forall Char.IsDigit) || unit.Length <> 1 then
                refused
            else
                let value = float digits
                match unit with
                | _ when value = 0.0 -> Decode.fail (sprintf "'%s' renews continuously. An interval must be greater than zero" raw)
                | "s" -> Decode.succeed (TimeSpan.FromSeconds value)
                | "m" -> Decode.succeed (TimeSpan.FromMinutes value)
                | "h" -> Decode.succeed (TimeSpan.FromHours value)
                | _ -> refused)

    /// Both required. `Pin` is a path inside the sandbox and must be absolute: it is run
    /// through the container, where a relative one means whatever the working directory
    /// happens to be.
    let private maintain : Decoder<VolumeMaintenance> =
        noUnknownKeys maintainKeys
        |> Decode.andThen (fun () ->
            Decode.object (fun get ->
                { Pin = get.Required.Field "pin" Decode.string
                  Every = get.Required.Field "every" interval }))
        |> Decode.andThen (fun m ->
            failIf
                (not (m.Pin.StartsWith "/"))
                (sprintf "pin '%s' must be an absolute path inside the sandbox" m.Pin)
                (Decode.succeed m))

    /// `read` or `write`, and never `overlay`: a volume is already the shared layer, and
    /// there is no read-only source under it for an overlay to sit on.
    let private volumeMode : Decoder<ResourceMountMode> =
        Decode.string
        |> Decode.andThen (function
            | "read" -> Decode.succeed ResourceMountMode.Read
            | "write" -> Decode.succeed ResourceMountMode.Write
            | other -> Decode.fail (sprintf "'%s' is not a volume mode. A volume is read or write" other))

    /// `name`, `at` and `mode` required: a volume with no `at` is a thing with nowhere to
    /// be, and the operator is the one author who knows where it belongs. `maintain` is
    /// decoded here so a mistake in it is refused at its own address, and collected by
    /// `maintenance`.
    ///
    /// `mode` has no default ON PURPOSE (security review, finding 9c). A writable volume
    /// lets one repository change what every other session on the host builds from. That is
    /// the operator's decision, not the session user's: the user does not own the volume,
    /// is not the one harmed, and so is not asked (`ResourceClosure.asksItsUser`). A default
    /// would make the decision without anyone writing it down.
    let private volume : Decoder<ResourceLeaf> =
        noUnknownKeys volumeKeys
        |> Decode.andThen (fun () ->
            Decode.object (fun get ->
                let maintained = get.Optional.Field "maintain" maintain |> Option.isSome
                let name = get.Required.Field "name" Decode.string
                let at = get.Required.Field "at" Decode.string
                maintained, name, at, get.Optional.Field "mode" volumeMode))
        |> Decode.andThen (fun (maintained, name, at, mode) ->
            match mode with
            | Some mode -> Decode.succeed (maintained, Volume (name, at, mode))
            | None ->
                Decode.fail (
                    sprintf
                        "volume '%s' names no mode. Write mode: read, or mode: write to let every sandbox that holds it change what each other session on this host reads from it"
                        name))
        |> Decode.andThen (fun (maintained, leaf) ->
            // A lease is written into the volume (`VolumeLeases.leaseDir`), so a maintained
            // volume the sandbox cannot write is a pin that fails every time it runs.
            failIf
                (maintained && (match leaf with Volume (_, _, ResourceMountMode.Read) -> true | _ -> false))
                "a read volume cannot be maintained: each sandbox writes its lease into the volume. Make it write, or drop maintain"
                (Decode.succeed leaf))

    /// A variable's value: text, or — where it carries `${` — what only the session can
    /// supply: `${proxy.https|ca-file|ca-dir}`, `${session.version}` and `${env.NAME}` composed with text, or a
    /// connection's token as the whole value. The same grammar a repo's declaration reads
    /// (`EnvTemplate`), so a reference means one thing wherever it is written. `$${` is a
    /// literal `${`.
    let private variableValue : Decoder<VariableValue> =
        Decode.string
        |> Decode.andThen (fun text ->
            match EnvTemplate.lent text with
            | Some connection -> Decode.succeed (VariableValue.Token connection)
            | None when not (EnvTemplate.composes text) -> Decode.succeed (VariableValue.Text text)
            | None ->
                match EnvTemplate.parse text with
                | Error e -> Decode.fail e
                | Ok [] -> Decode.succeed (VariableValue.Text "")
                | Ok [ TemplatePart.Literal literal ] -> Decode.succeed (VariableValue.Text literal)
                | Ok template -> Decode.succeed (VariableValue.Composed template))

    /// `connection:` is a map of each connection to the routes it is forwarded by —
    /// `{ github: [git, api] }` — one leaf per route. The bare name it used to be meant every
    /// route the source happened to have, which is a grant nobody wrote down; it is refused
    /// with the form that says what was meant.
    let private connections : Decoder<ResourceLeaf list> =
        let leaves (connection: string, raw: string list) : Result<ResourceLeaf list, string> =
            match raw |> List.map ConnectionRoute.parse |> List.tryPick (function Error e -> Some e | Ok _ -> None) with
            | Some e -> Error (sprintf "%s: %s" connection e)
            | None ->
                match raw |> List.choose (ConnectionRoute.parse >> Result.toOption) |> List.distinct with
                | [] -> Error (sprintf "%s names no route and forwards nothing. Name git, api or both" connection)
                | routes -> Ok (routes |> List.map (fun route -> Connection (connection, route)))
        Decode.oneOf [
            Decode.keyValuePairs stringList |> Decode.map Choice1Of2
            stringList |> Decode.map Choice2Of2
        ]
        |> Decode.andThen (fun written ->
            match written with
            | Choice1Of2 pairs ->
                match pairs |> List.map leaves |> List.tryPick (function Error e -> Some e | Ok _ -> None) with
                | Some e -> Decode.fail e
                | None -> pairs |> List.collect (leaves >> Result.defaultValue []) |> Decode.succeed
            | Choice2Of2 names ->
                Decode.fail (
                    sprintf
                        "a connection names the routes that forward it. Write `connection: { %s }` with the routes this resource grants"
                        (names |> List.map (sprintf "%s: [git, api]") |> String.concat ", ")))

    /// `sensitive` marked a resource as one to ask about. Whether a grant asks is its kind's
    /// to say now (`ResourceClosure.asksItsUser`), so the key is refused whatever its value,
    /// and BY NAME rather than left to `noUnknownKeys`: a file carrying it says something
    /// about who is asked, and "unknown key: sensitive" would leave its operator to work out
    /// what became of that.
    let private noSensitiveKey : Decoder<unit> =
        Decode.keys
        |> Decode.andThen (fun keys ->
            failIf
                (List.contains "sensitive" keys)
                (sprintf
                    "a resource is no longer marked sensitive. A connection is always asked about, because it is spent as the person who is asked. Everything else %s grants is the operator's to decide. Remove 'sensitive'"
                    FileName)
                (Decode.succeed ()))

    /// A leaf declares primitives directly, and may declare several: the things that make one
    /// resource work are usually more than one — a cache is a mount and an endpoint and the
    /// variable pointing a tool at it — and none of the three means anything alone.
    let private leaf : Decoder<ResourceDecl> =
        noSensitiveKey
        |> Decode.andThen (fun () -> noUnknownKeys leafKeys)
        |> Decode.andThen (fun () ->
            Decode.object (fun get ->
                let mounts = get.Optional.Field "mount" (Decode.oneOf [ Decode.list mount; mount |> Decode.map List.singleton ])
                let sockets = get.Optional.Field "socket" stringList |> Option.defaultValue []
                let endpoints = get.Optional.Field "endpoint" stringList |> Option.defaultValue []
                let execs = get.Optional.Field "exec" stringList |> Option.defaultValue []
                let connections = get.Optional.Field "connection" connections |> Option.defaultValue []
                let volumes =
                    get.Optional.Field "volume" (Decode.oneOf [ Decode.list volume; volume |> Decode.map List.singleton ])
                    |> Option.defaultValue []
                // One `Variable` leaf per entry, which is what makes a variable dedup and
                // conflict like every other primitive instead of needing a rule of its own.
                let variables =
                    get.Optional.Field "env" (Decode.keyValuePairs variableValue)
                    |> Option.defaultValue []
                (mounts |> Option.defaultValue [] |> List.map Mount)
                @ (sockets |> List.map Socket)
                @ (endpoints |> List.map Endpoint)
                @ (variables |> List.map Variable)
                @ (execs |> List.map Exec)
                @ volumes
                @ connections))
        |> Decode.andThen (fun leaves ->
            // A resource that grants nothing is a name that reads as configuration and is
            // none — the same failure an unknown key would be, arriving by a different route.
            failIf
                (List.isEmpty leaves)
                "this resource grants nothing. Name at least one of: mount, socket, endpoint, env, exec, volume or connection"
                (Decode.succeed (ResourceDecl.Leaf leaves)))

    /// An ARRAY is a composition.
    let private composition : Decoder<ResourceDecl> =
        Decode.list Decode.string
        |> Decode.andThen (fun names ->
            names
            |> List.fold
                (fun acc raw ->
                    acc
                    |> Result.bind (fun taken ->
                        ResourceName.create raw |> Result.map (fun name -> taken @ [ name ])))
                (Ok [])
            |> function
                | Ok names -> Decode.succeed (ResourceDecl.Composition names)
                | Error e -> Decode.fail e)

    let private resource : Decoder<ResourceDecl> = Decode.oneOf [ composition; leaf ]

    /// Decoded a field at a time rather than with `keyValuePairs`, for the PATH: that
    /// combinator decodes each value without putting its key on it, so every refusal inside
    /// any resource would come back at the same address whichever resource wrote it.
    let private resources : Decoder<(ResourceName * ResourceDecl) list> =
        Decode.keys
        |> Decode.andThen (fun raws ->
            raws
            |> List.map (fun raw ->
                Decode.field raw resource
                |> Decode.andThen (fun decl ->
                    match ResourceName.create raw with
                    | Ok name -> Decode.succeed (name, decl)
                    | Error e -> Decode.fail e))
            |> List.fold (fun acc one -> Decode.map2 (fun taken x -> taken @ [ x ]) acc one) (Decode.succeed []))

    /// Every `maintain` in the file, by volume name — a second pass over `resources` that only
    /// looks at leaves' volumes, since the leaf itself does not carry it (`ProfileFile`).
    /// A volume maintained two different ways is refused: which pin its sandboxes run would
    /// otherwise depend on which resource a repo happened to select.
    let private maintenance : Decoder<Map<string, VolumeMaintenance>> =
        let maintained : Decoder<(string * VolumeMaintenance) list> =
            Decode.object (fun get ->
                match get.Optional.Field "maintain" maintain with
                | Some m -> [ get.Required.Field "name" Decode.string, m ]
                | None -> [])
        let fromResource : Decoder<(string * VolumeMaintenance) list> =
            Decode.oneOf
                [ Decode.list Decode.string |> Decode.map (fun _ -> [])
                  Decode.optional "volume" (Decode.oneOf [ Decode.list maintained |> Decode.map List.concat; maintained ])
                  |> Decode.map (Option.defaultValue []) ]
        Decode.keys
        |> Decode.andThen (fun raws ->
            raws
            |> List.map (fun raw -> Decode.field raw fromResource)
            |> List.fold (fun acc one -> Decode.map2 (@) acc one) (Decode.succeed []))
        |> Decode.andThen (fun pairs ->
            pairs
            |> List.fold
                (fun acc (name, m) ->
                    acc
                    |> Result.bind (fun (taken: Map<string, VolumeMaintenance>) ->
                        match Map.tryFind name taken with
                        | Some existing when existing <> m ->
                            Error (sprintf "volume '%s' has two different maintain declarations. Declare its maintain once" name)
                        | _ -> Ok (Map.add name m taken)))
                (Ok Map.empty)
            |> function
                | Ok found -> Decode.succeed found
                | Error e -> Decode.fail e)

    let private names : Decoder<ResourceName list> =
        stringList
        |> Decode.andThen (fun raws ->
            raws
            |> List.fold
                (fun acc raw ->
                    acc |> Result.bind (fun taken -> ResourceName.create raw |> Result.map (fun n -> taken @ [ n ])))
                (Ok [])
            |> function
                | Ok names -> Decode.succeed names
                | Error e -> Decode.fail e)

    /// The operator's words for the agent. Trimmed, because a YAML block scalar ends in the
    /// newline that closed it, and refused when nothing is left: guidance that says nothing is
    /// the same failure as a resource that grants nothing — configuration that reads as
    /// something and is none.
    let private guidance : Decoder<string> =
        Decode.string
        |> Decode.map (fun text -> text.Trim ())
        |> Decode.andThen (fun text ->
            failIf (text = "") "agent guidance says nothing. Write what the agent should know about this host. Otherwise, remove 'guidance'" (Decode.succeed text))

    /// A strategy, by the name `PromptStrategy.name` gives it. A name this build does not have
    /// is refused with the ones it does, so a typo cannot run the agent on a prompt nobody chose.
    let private promptStrategy : Decoder<PromptStrategy> =
        Decode.string
        |> Decode.andThen (fun raw ->
            match PromptStrategy.all |> List.tryFind (fun strategy -> PromptStrategy.name strategy = raw) with
            | Some strategy -> Decode.succeed strategy
            | None ->
                Decode.fail (
                    sprintf
                        "agent prompt '%s' is not a strategy this build has (known: %s)"
                        raw
                        (PromptStrategy.all |> List.map PromptStrategy.name |> String.concat ", ")))

    /// What the operator says about the agent: its guidance, and the strategy that puts its
    /// prompt together. Each key may be left out, and a missing one is the default
    /// (`AgentProfile.defaults`), because that is what a host that says nothing gets. An
    /// `agent:` block with neither key is refused: it reads as configuration and is none.
    let private agent : Decoder<AgentProfile> =
        noUnknownKeys agentKeys
        |> Decode.andThen (fun () ->
            Decode.map2
                (fun words strategy -> words, strategy)
                (Decode.optional "guidance" guidance)
                (Decode.optional "prompt" promptStrategy))
        |> Decode.andThen (fun (words, strategy) ->
            match words, strategy with
            | None, None ->
                Decode.fail "the agent block says nothing. Give it guidance (what the agent should know about this host) or a prompt strategy. Otherwise, remove the block"
            | _ ->
                Decode.succeed
                    { Guidance = words
                      Prompt = strategy |> Option.defaultValue AgentProfile.defaults.Prompt })

    /// A value judged after it is read, so a refused one is refused in the one sentence
    /// that says why.
    let private judged (read: Decoder<'raw>) (judge: 'raw -> Result<'T, string>) : Decoder<'T> =
        read
        |> Decode.andThen (fun raw ->
            match judge raw with
            | Ok value -> Decode.succeed value
            | Error reason -> Decode.fail reason)

    /// `container:` — the ceiling on every container on this host, in compose's spelling
    /// and grammar, as a repository's `container:` block writes the same keys. Each key may
    /// be left out, and a missing one keeps today's default (`ContainerCeiling`).
    let private container : Decoder<ContainerCeiling> =
        noUnknownKeys containerKeys
        |> Decode.andThen (fun () ->
            Decode.object (fun get ->
                { ContainerCeiling.Memory =
                    get.Optional.Field
                        "mem_limit"
                        (judged
                            (Decode.oneOf
                                [ Decode.string |> Decode.map ContainerMemory.parse
                                  Decode.int64 |> Decode.map ContainerMemory.ofBytes ])
                            id)
                  PidsLimit = get.Optional.Field "pids_limit" (judged Decode.int PidsLimit.ofInt)
                  CgroupParent = get.Optional.Field "cgroup_parent" (judged Decode.string CgroupParent.parse) }))

    /// `default` is the spelling `always` had before it was named for what it does. Refused
    /// BY NAME rather than left to `noUnknownKeys`, which would say "unknown key: default
    /// (known: version, resources, always)" and leave an operator to work out that one
    /// replaced the other — the same courtesy a variable moved onto the command line gets.
    let private noOldKeys : Decoder<unit> =
        Decode.keys
        |> Decode.andThen (fun keys ->
            failIf
                (List.contains "default" keys)
                (sprintf
                    "'default' is now 'always' in %s. Every sandbox on this host holds what it names. No repo can decline it. That is not what a default is"
                    FileName)
                (Decode.succeed ()))

    let decoder : Decoder<ProfileFile> =
        noOldKeys
        |> Decode.andThen (fun () -> noUnknownKeys fileKeys)
        |> Decode.andThen (fun () ->
            Decode.field "version" Decode.int
            |> Decode.andThen (fun version ->
                failIf
                    (version <> Version)
                    (sprintf "this build speaks %s version %d, not %d" FileName Version version)
                    (Decode.map2
                        (fun read ceiling -> read, ceiling)
                        (Decode.map5
                            (fun declared selection said sandboxes maintained ->
                                declared, selection, said, sandboxes, maintained)
                            (Decode.field "resources" resources)
                            (Decode.optional "always" names |> Decode.map (Option.defaultValue []))
                            (Decode.optional "agent" agent |> Decode.map (Option.defaultValue AgentProfile.defaults))
                            (Decode.optional "sandboxes" Decode.value
                             |> Decode.andThen (fun block ->
                                 match block with
                                 | None -> Decode.succeed Map.empty
                                 | Some block ->
                                     match ConfigFile.parseSandboxes (Encode.toString 0 block) with
                                     | Ok declared -> Decode.succeed declared
                                     | Error e -> Decode.fail (sprintf "sandboxes: %s" e)))
                            (Decode.field "resources" maintenance))
                        (Decode.optional "container" container |> Decode.map (Option.defaultValue ContainerCeiling.none)))))
        |> Decode.andThen (fun ((declared, selection, said, sandboxes, maintained), ceiling) ->
            // The algebra's own refusals — a cycle, a dangling name, a name declared twice, a
            // resource that contradicts itself — reached through `load` and NOT re-checked
            // here. A decoder with its own copy of those rules is the redundant spare that
            // rots: two mechanisms for one requirement, free to disagree.
            match ResourceProfile.load declared with
            | Error e -> Decode.fail e
            | Ok profile ->
                // What is always granted must RESOLVE, and it is checked here rather than at
                // the first sandbox that would have held it. An operator granting something to
                // every sandbox on the host should learn it does not hold while they are
                // looking at the file, not when somebody else's session refuses to start.
                match ResourceProfile.resolve profile selection with
                | Error e -> Decode.fail (sprintf "what this host always grants cannot be granted: %s" e)
                | Ok _ ->
                    Decode.succeed
                        { Resources = profile
                          Always = selection
                          Agent = said
                          Sandboxes = sandboxes
                          Maintenance = maintained
                          Container = ceiling })

    let parse (json: string) : Result<ProfileFile, string> = Decode.fromString decoder json
