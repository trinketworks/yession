namespace Yession.Domain.Sandboxes

open Yession.Domain

open System

/// Environment & command vocabulary. The session's WorkSandbox is described by an
/// `EnvironmentSpec` and confined by whichever `SandboxBackend` the session was booted
/// with; the lifecycle is recorded as events (the folds below), which are the pinned
/// observable protocol.

type ContainerImage = { Name : string; Tag : string option }

module ContainerImage =

    /// `name:tag`, as docker spells it — one rendering, because two would eventually
    /// disagree about whether an untagged image says `:latest`.
    let render (image: ContainerImage) : string =
        image.Name + (image.Tag |> Option.map ((+) ":") |> Option.defaultValue "")

type ContainerBuildSpec = { ContextPath : string; DockerfilePath : string option }

type MountSource =
    | HostPath of string
    | NamedVolume of string
    | SessionWorkspace

type MountMode =
    | ReadOnly
    | ReadWrite

type ContainerMount = { Source : MountSource; Target : string; Mode : MountMode }

module ContainerMount =

    /// Whether a mount at `target` already provides `dir`: the directory itself, or
    /// anything under it — on a directory boundary, never a bare prefix (`/repo` does
    /// not provide `/repos/x`). This is what decides whether a sandbox still needs a
    /// workspace volume at its working directory: a workdir inside a declared mount is
    /// already backed by that mount, and a volume put on top of the deeper path would
    /// SHADOW the very thing the mount carries — measured as an empty workspace volume
    /// sitting over the checkouts' bind, hiding the checkout it existed to hold.
    let provides (target: string) (dir: string) : bool =
        let target = target.TrimEnd '/'
        let dir = dir.TrimEnd '/'
        dir = target || dir.StartsWith (target + "/")

type SecretName = private SecretName of string

module SecretName =
    let create (raw: string) : Result<SecretName, string> =
        if String.IsNullOrWhiteSpace raw then Error "SecretName cannot be blank"
        else Ok (SecretName (raw.Trim()))
    let value (SecretName s) = s

type EnvironmentVariableRef =
    | PlainValue of string
    | SecretRef of SecretName
    /// A value composed over what lies beneath (`EnvTemplate`).
    | Derived of EnvTemplate
    /// `${<connection>.token}` as a variable's whole value: a stand-in for that connection's
    /// credential, lent to each command for the act it runs as and returned when the next one
    /// starts — never a value the sandbox holds, so it is not in the sandbox's environment at
    /// all but in each command's. docs/GAPS.md asks whether the rotation earns its keep.
    | Lent of connection: ConnectionName

/// What a selection grants a sandbox beyond its policy: the connections it forwards, and the
/// variables an operator bound to something only the session can supply (a resource's
/// `${proxy.…}` or `${<connection>.token}`), as the same references a repo's declaration
/// would carry — so the session supplies them through one path whoever wrote them.
[<RequireQualifiedAccess>]
type SelectionGrant =
    { Connections : ForwardedConnections
      Bound : Map<string, EnvironmentVariableRef>
      /// The bound variables reached through `wants:` alone. A want is silent where it cannot
      /// be had, so one of these whose connection is not forwarded is left out rather than
      /// refusing the sandbox; one something NEEDS refuses it.
      WantedOnly : Set<string> }

module SelectionGrant =

    let none : SelectionGrant =
        { SelectionGrant.Connections = ForwardedConnections.none
          SelectionGrant.Bound = Map.empty
          SelectionGrant.WantedOnly = Set.empty }

    /// Out of `ResourceProfile.grants`' answer.
    let ofGrant (leaves: ResourceLeaf list, wantedOnly: Set<ResourceLeaf>) : SelectionGrant =
        let bound (leaf: ResourceLeaf) =
            match leaf with
            | Variable (variable, VariableValue.Composed template) -> Some (variable, Derived template)
            | Variable (variable, VariableValue.Token connection) ->
                // `load` already refused a name no connection could carry, through the
                // connection leaf this one needs beside it.
                ConnectionName.create connection |> Result.toOption |> Option.map (fun name -> variable, Lent name)
            | Variable (_, VariableValue.Text _)
            | Mount _
            | Socket _
            | Endpoint _
            | Exec _
            | Volume _
            | Connection _ -> None
        { SelectionGrant.Connections = ForwardedConnections.ofGrant (leaves, wantedOnly)
          SelectionGrant.Bound = leaves |> List.choose bound |> Map.ofList
          SelectionGrant.WantedOnly = wantedOnly |> Set.toList |> List.choose bound |> List.map fst |> Set.ofList }

/// How much memory a container may hold, in bytes: its cgroup's ceiling, past which the
/// kernel kills a process INSIDE it rather than choosing a victim from the whole host.
///
/// Every container on a daemon shares that daemon's memory — on a Mac, one Colima VM — and
/// a container with no ceiling can take all of it. When it does, the kernel's global OOM
/// killer picks by size across every container on the host, so one session's build kills
/// another session's work: six sessions' devshells building at once on a 5 GiB VM did
/// exactly that, and the session whose `build` died was told only "exit 137". A ceiling
/// makes the one that overran its own budget the one that pays.
///
/// What a ceiling cannot do is make the host bigger: containers that each stay under theirs
/// can still add up to more than the host has, and then the global killer is back. That is
/// the host's size to answer, not this.
type ContainerMemory = private ContainerMemory of bytes: int64

[<RequireQualifiedAccess>]
module ContainerMemory =

    /// The least the daemon accepts — docker refuses a ceiling under 6 MiB at create, with
    /// a sentence that would arrive long after the file was read.
    let minimum : int64 = 6L * 1024L * 1024L

    let bytes (ContainerMemory b) : int64 = b

    let private units : (string * int64) list =
        [ "b", 1L
          "k", 1024L
          "kb", 1024L
          "m", 1024L * 1024L
          "mb", 1024L * 1024L
          "g", 1024L * 1024L * 1024L
          "gb", 1024L * 1024L * 1024L ]

    let ofBytes (b: int64) : Result<ContainerMemory, string> =
        if b < minimum then
            Error (sprintf "memory: %d bytes is under docker's 6m minimum for a container" b)
        else Ok (ContainerMemory b)

    /// Compose's byte-value grammar, which is what `mem_limit` and
    /// `deploy.resources.limits.memory` take: a whole number and an optional unit — `b`,
    /// `k`/`kb`, `m`/`mb`, `g`/`gb`, any case, binary multiples — so `4g` copied out of a
    /// compose file means the same thing here. No unit is bytes.
    let parse (raw: string) : Result<ContainerMemory, string> =
        let text = raw.Trim().ToLowerInvariant ()
        let digits = text |> Seq.takeWhile Char.IsDigit |> Seq.length
        let number, unit = text.Substring (0, digits), text.Substring(digits).Trim ()
        let refuse () =
            Error (sprintf "memory: '%s' is not a size. Write a whole number with an optional unit, like 4g. The units are b, k, m and g" raw)
        let scale =
            if unit = "" then Some 1L
            else units |> List.tryFind (fun (u, _) -> u = unit) |> Option.map snd
        match scale, Int64.TryParse number with
        | Some scale, (true, n) when n <= Int64.MaxValue / scale -> ofBytes (n * scale)
        | _ -> refuse ()

    /// The exact compose spelling — the largest unit that divides it — which `parse` reads
    /// back as the same number.
    let render (ContainerMemory b) : string =
        match units |> List.rev |> List.filter (fun (u, _) -> u.Length = 1) |> List.tryFind (fun (_, scale) -> b % scale = 0L) with
        | Some (u, scale) when u <> "b" -> sprintf "%d%s" (b / scale) u
        | _ -> string b

    /// For a person reading a refusal: approximate, in GiB.
    let describe (b: int64) : string =
        sprintf "%.1f GiB" (float b / (1024.0 * 1024.0 * 1024.0))

    /// The share of the daemon's memory a container gets when its declaration names none:
    /// three quarters.
    ///
    /// Derived from the daemon rather than fixed, because a fixed number is wrong at both
    /// ends: above a small VM's total it contains nothing (the ceiling is never reached
    /// before the host runs out), and on a large host it kills a build that had room. The
    /// job of the default is that no ONE container can take the host — so a quarter is
    /// held back for everything else on it (the daemon, every other session's shells) and
    /// a runaway hits its own ceiling first. It is not a partition: dividing the host by
    /// how many sessions might run would starve a lone build to guard against a crowd,
    /// and a crowd is the host's size to answer.
    ///
    /// Three quarters rather than a half because of what a work container runs: a
    /// dotnet build with an MSBuild node per CPU and Fable beside it, and Chromium for a
    /// browser suite. The one dev container whose cgroup recorded a build on a 4.8 GiB
    /// Colima VM peaked at 2.3 GiB (`memory.peak`), and that while the global OOM killer
    /// was already taking processes from it — a floor on what a build needs, not the
    /// figure. Half that VM is 2.4 GiB, which a build lands on; three quarters is 3.6.
    let defaultFor (daemonTotal: int64) : ContainerMemory =
        ContainerMemory (max minimum (daemonTotal / 4L * 3L))

    /// The ceiling a container is created with: what it declared, or the default share of
    /// what the daemon has. A declaration above the daemon's total is refused rather than
    /// applied — a ceiling the host runs out before reaching contains nothing, and the
    /// repo asked for memory this host cannot give.
    let forDaemon (declared: ContainerMemory option) (daemonTotal: int64) : Result<ContainerMemory, string> =
        match declared with
        | None -> Ok (defaultFor daemonTotal)
        | Some (ContainerMemory b) when b > daemonTotal ->
            Error (
                sprintf
                    "the container declares memory: %s. The docker daemon has %s in all. The host exhausts its memory before it reaches that ceiling. So the ceiling contains nothing. Give the daemon more memory (Colima: `colima start --memory <GiB>`). As an alternative, declare less"
                    (render (ContainerMemory b))
                    (describe daemonTotal))
        | Some declared -> Ok declared

/// How strongly the kernel prefers a sandbox's WORK as an OOM victim, from 0 to 1000: what a
/// person or agent runs (`ProcessEntry.Entrypoint`) writes it to its own
/// `/proc/self/oom_score_adj` before the work starts. The container itself, its init, its
/// main process and every housekeeping exec stay at `container` (0).
///
/// A ceiling (`ContainerMemory`) does not stop the host itself from running out: containers
/// that each stay under theirs can still add up to more than the host has. The kernel's
/// global killer then picks from every process on the host, and on a Colima VM that
/// includes the processes that run the containers. Their own values are below zero
/// (measured: dockerd −500, containerd −999) and a container's are 0 by default, so the
/// kernel can choose them. At 500, any sandbox process is chosen before any of them.
///
/// Inside the container, the split decides who goes when the container hits its own
/// `memory.max`. One overrun kills two processes: the process that overran, and a moment
/// later a second one, before the first has given its memory back (measured: 40 overruns,
/// 41 kills). With one score for every process, the second victim was close to arbitrary:
/// the container's idle main process (the container stopped) or the exec that reads the
/// memory counters. At 0 against work's 500, neither is chosen while work is there.
///
/// Not below zero: a sandbox that could rank itself with the daemon's own processes
/// removes what this protects. A repo can only make its processes a more likely victim.
/// Work can write its own score back down to 0 without a capability, never below.
type OomScoreAdj = private OomScoreAdj of int

[<RequireQualifiedAccess>]
module OomScoreAdj =

    /// What work gets when its declaration names none.
    let standard = OomScoreAdj 500

    /// What the container is created with, so its init, its main process and every
    /// housekeeping exec have it. Above the daemon's own, below any work.
    let container = OomScoreAdj 0

    let value (OomScoreAdj n) : int = n

    let ofInt (n: int) : Result<OomScoreAdj, string> =
        if n < 0 || n > 1000 then
            Error (
                sprintf
                    "oom-score-adj: %d is not from 0 to 1000. A sandbox can make its processes a more likely OOM victim. It cannot make them a less likely victim than the daemon's own"
                    n)
        else Ok (OomScoreAdj n)

    /// What work in a container runs at: what it declared, or `standard`. Never
    /// `container`'s 0, which ranks work with the processes that keep the container up.
    let forWork (declared: OomScoreAdj option) : OomScoreAdj =
        declared |> Option.defaultValue standard

/// How many processes and threads a container can hold at once — its cgroup's
/// `pids.max`, `docker run --pids-limit`.
///
/// Memory is not the only thing a container can take from the host it shares. A fork bomb,
/// or a build that starts threads without limit, uses up the kernel's process table for
/// every session on that host. The most measured in a working dev container is 136, during
/// a dotnet build, so the standard 4096 does not stop real work.
type PidsLimit = private PidsLimit of int

[<RequireQualifiedAccess>]
module PidsLimit =

    /// What a container gets when its declaration names none.
    let standard = PidsLimit 4096

    let value (PidsLimit n) : int = n

    /// Docker reads 0 and below as no limit, so they are refused, not passed through:
    /// a limit that is not a limit must not look like one.
    let ofInt (n: int) : Result<PidsLimit, string> =
        if n < 1 then Error (sprintf "pids-limit: %d is not a limit. Write a number of processes, 1 or more" n)
        else Ok (PidsLimit n)

    /// What a container is created with: what it declared, or `standard`. Never docker's
    /// own default, which is no limit.
    let forContainer (declared: PidsLimit option) : PidsLimit =
        declared |> Option.defaultValue standard

/// The cgroup that a container's cgroup is put under — `docker run --cgroup-parent`.
///
/// With one parent for every sandbox (the pool), the host can give the parent a memory
/// limit below its total. When all the sandboxes together reach it, the kernel kills inside
/// that parent, and the processes outside it (the daemon, its shims) are not candidates.
/// Who makes that parent and gives it its limit is the host's operator, not this setting:
/// if the parent does not exist, docker makes it with no limit, which changes nothing.
///
/// Three spellings. A bare name (`yession`: no `/`, no `.slice`) names the pool under
/// either cgroup driver: `/yession` under cgroupfs, `yession.slice` under systemd
/// (`forDriver`). A path (`/yession`) is for cgroupfs only, and a slice name
/// (`yession.slice`) is passed as written. One file is read by daemons of both drivers
/// (the operator's Colima VM is cgroupfs, CI's docker is systemd), so a repo writes the
/// bare name.
///
/// `None` — the usual — keeps the daemon's own parent.
type CgroupParent = private CgroupParent of string

[<RequireQualifiedAccess>]
module CgroupParent =

    let value (CgroupParent path) : string = path

    let parse (raw: string) : Result<CgroupParent, string> =
        let text = raw.Trim ()
        if text = "" then Error "cgroup-parent cannot be blank"
        elif text |> Seq.exists Char.IsWhiteSpace then Error (sprintf "cgroup-parent: '%s' contains a space" text)
        elif text.Split '/' |> Array.contains ".." then Error (sprintf "cgroup-parent cannot climb, and '%s' does" text)
        else Ok (CgroupParent text)

    /// A name that is neither a path nor a slice: the pool, spelled for no driver yet.
    let private isBareName (text: string) = not (text.Contains "/") && not (text.EndsWith ".slice")

    /// The parent this daemon is given, decided before anything is pulled.
    ///
    /// A bare name is spelled for the driver: `/<name>` under cgroupfs, `<name>.slice`
    /// under systemd. Under systemd, if the slice does not exist, systemd makes it with no
    /// limit — the same as a missing cgroupfs pool, and the containers run as if no parent
    /// was named.
    ///
    /// A path or a slice name is checked, not changed. Under the systemd driver docker
    /// takes only a slice name, so a path is refused here. The refusal names the driver,
    /// which docker's own refusal at create does not.
    let forDriver (declared: CgroupParent option) (driver: string) : Result<CgroupParent option, string> =
        match declared with
        | Some (CgroupParent name) when isBareName name ->
            Ok (Some (CgroupParent (if driver = "systemd" then name + ".slice" else "/" + name)))
        | Some (CgroupParent path) when driver = "systemd" && not (path.EndsWith ".slice") ->
            Error (
                sprintf
                    "the container declares cgroup-parent: %s. The docker daemon uses the systemd cgroup driver. That driver takes only a slice name, like yession.slice. A bare name (like yession) works under both drivers"
                    path)
        | declared -> Ok declared

/// What a CONTAINER is. Every field here is one only a container has — an image to run, a
/// filesystem to build, volumes to mount, a process to be.
type ContainerSpec =
    { Image : ContainerImage option
      Build : ContainerBuildSpec option
      Mounts : ContainerMount list
      /// The container's own process — compose's `command` (Plan 27).
      ///
      /// `None` is what every sandbox has had until now: the container idles and exists only
      /// to be `exec`'d into. `Some` makes it a service and inherits the semantics that go
      /// with one — the sandbox lives exactly as long as the process does, so a command that
      /// exits takes its terminals with it. That is docker's own behaviour, not a policy
      /// invented here.
      Command : string option
      /// What every piece of WORK in this container runs behind — compose's `entrypoint`,
      /// as an argv. A repo whose toolchain is assembled by a devshell puts that here
      /// (`nix develop --impure --command`), and a terminal's shell, a block, `setup:` and
      /// the container's own `command` all run inside it; the session's housekeeping does
      /// not (`ProcessEntry`). `None` runs everything as it stands.
      Entrypoint : string list option
      /// Which shell a terminal here opens, by dialect (`TerminalShell.dialects`), when the
      /// repo would rather say than have the backend look. `None` — the usual — has the
      /// backend detect what is on the far side of the entrypoint, most capable first.
      /// Written at the sandbox level as `dialect:`, and carried here because only a
      /// container has a shell to choose.
      Dialect : string option
      /// The container's memory ceiling, as the repo declared it — compose's
      /// `deploy.resources.limits.memory`, written `memory:`. `None` — the usual — takes the
      /// default share of the daemon's memory (`ContainerMemory.defaultFor`): never no
      /// ceiling, because no ceiling is the whole host.
      Memory : ContainerMemory option
      /// `oom-score-adj:`. `None` — the usual — is `OomScoreAdj.standard`.
      OomScoreAdj : OomScoreAdj option
      /// `pids-limit:`. `None` — the usual — is `PidsLimit.standard`.
      PidsLimit : PidsLimit option
      /// `cgroup-parent:`. `None` — the usual — keeps the daemon's own parent.
      CgroupParent : CgroupParent option }

module ContainerSpec =

    let defaults : ContainerSpec =
        { Image = None
          Build = None
          Mounts = []
          Command = None
          Entrypoint = None
          Dialect = None
          Memory = None
          OomScoreAdj = None
          PidsLimit = None
          CgroupParent = None }

    /// The memory ceiling and the cgroup parent THIS daemon creates the container with,
    /// from its total memory and its cgroup driver (`docker info`). Pure, so a test can ask
    /// what a daemon it does not have would say (`ContainerMemory.forDaemon`,
    /// `CgroupParent.forDriver`).
    let forDaemon
        (container: ContainerSpec)
        (daemonTotal: int64)
        (cgroupDriver: string)
        : Result<ContainerMemory * CgroupParent option, string> =
        ContainerMemory.forDaemon container.Memory daemonTotal
        |> Result.bind (fun memory ->
            CgroupParent.forDriver container.CgroupParent cgroupDriver
            |> Result.map (fun parent -> memory, parent))

/// What the sandbox IS, and the distinction is load-bearing rather than descriptive.
///
/// This used to be four optional fields on one record, and every one of them was meaningless
/// on two of the three backends: a host or srt sandbox is a CONFINEMENT AROUND SPAWNS — it
/// has no image, no volumes and no process of its own — so `Command = Some _` on one was a
/// state the type permitted, the composition refused at run time, and a reader had to know
/// the rule to avoid writing.
///
/// As a union it is simply not expressible. "A confined sandbox with a process" has no
/// representation, so nothing downstream carries a case for it and no test has to prove it
/// is refused.
///
/// What remains a run-time question, honestly: whether the BACKEND can host a container at
/// all. The backend is the operator's (`YESSION_SESSION_WORK_BACKEND`) and the spec is partly
/// the repo's, so the two are authored by different people and can only be reconciled where
/// they meet (`Sandboxes.forBackend`).
type SandboxRuntime =
    /// host / srt. Nothing to build, nothing to run: the sandbox IS the confinement its
    /// spawns go through. (Not `Confined` — `FilesystemConfinement` already has that case,
    /// and a second one would shadow it wherever the domain is opened.)
    | Confinement
    /// docker.
    | Container of ContainerSpec

module SandboxRuntime =

    /// What a sandbox IS, in one clause: an image, a build or neither, plus the process if
    /// it has one. Deliberately shallow — it is read by a person deciding whether the thing
    /// running is the thing they asked for, in a refusal and in the sandbox listing.
    let describe (runtime: SandboxRuntime) : string =
        match runtime with
        | Confinement -> "no container"
        | Container container ->
            let what =
                match container.Image, container.Build with
                | Some image, _ -> ContainerImage.render image
                | None, Some build -> sprintf "a build of %s" build.ContextPath
                | None, None -> "a container"
            match container.Command with
            | Some command -> sprintf "%s running '%s'" what command
            | None -> what

    /// The backend every repo-owned work sandbox runs under — the decision this codebase
    /// pivoted on: a repo-owned sandbox is WORK, and work runs in a container. Running a
    /// BUILD under the light confinement was driven end to end and died behind walls no
    /// grant can open (GAPS: "srt work sandboxes do not run builds").
    ///
    /// Named once, HERE, because several places downstream need only this half of the
    /// decision — which workspace a sandbox gets, which view of the repos directory its
    /// declaration resolves against, which host limits judge its asks — and each of them
    /// re-encoding "repo-owned means docker" by hand is how the split shipped with three
    /// seams still answering for the wrong backend.
    let repoWorkBackend : SandboxBackend = DockerBackend

    /// The backend a WORK sandbox runs under, decided by its scope alone. The
    /// session-owned sandboxes keep the operator's configured light confinement
    /// (srt/host) for the checkout and small edits; a repo's are `repoWorkBackend`.
    /// Total: everything that follows from the backend (paths, views, limits,
    /// descriptions) asks this, whether or not the sandbox can actually start.
    let scopedBackend (configured: SandboxBackend) (scope: SandboxScope) : SandboxBackend =
        match scope with
        | SessionOwned -> configured
        | RepoOwned _ -> repoWorkBackend

    /// `scopedBackend`, plus the refusal only a start needs: a repo-owned declaration
    /// with no container is refused HERE, where the rule lives and the cheap tier can
    /// reach it — not defaulted to an image that builds nothing, which would turn a
    /// missing declaration into a sandbox that looks fine and fails at the first
    /// command.
    let backendFor
        (configured: SandboxBackend)
        (scope: SandboxScope)
        (runtime: SandboxRuntime)
        : Result<SandboxBackend, string> =
        match scope, runtime with
        | RepoOwned repo, Confinement ->
            Error (
                sprintf
                    "a repo's work sandbox is a container. %s declares no container. Add a `container:` block to its yession.yaml. The block holds an image or a build"
                    (RepoRef.value repo))
        | scope, _ -> Ok (scopedBackend configured scope)

/// Where a seeded file goes: a path inside the sandbox's OWN home, and nowhere else.
///
/// Private, so the refusals below are the only way to hold one. A sandbox's home is a
/// directory this session makes and that sandbox alone writes, which is why seeding one
/// needs no operator's permission — it reaches nothing of the host's. That is only true
/// while the path cannot LEAVE the home, so leaving is what has no representation:
/// absolute paths, `..`, and empty segments are refused rather than normalised, because a
/// path that means something other than what was written is how the check gets bypassed
/// later by somebody spelling it differently.
type HomePath = private HomePath of string

module HomePath =

    /// The one way to make one. Refuses in the writer's words, naming what to write
    /// instead — this is read from a repo's `yession.yaml`, so the person to tell is
    /// whoever wrote that file.
    let create (raw: string) : Result<HomePath, string> =
        let path = raw.Trim ()
        if path = "" then Error "a seeded file needs a path inside the sandbox's home"
        elif path.StartsWith "/" then
            Error (sprintf "'%s' is an absolute path. A seeded file is written inside the sandbox's own home. Name it relative to that home" path)
        elif path.Contains "\\" then
            Error (sprintf "'%s' uses a backslash; paths here are separated by '/' on every platform" path)
        elif path.EndsWith "/" then
            Error (sprintf "'%s' names a directory; a seeded file needs a file name" path)
        else
            let segments = path.Split '/' |> Array.toList
            match segments |> List.tryFind (fun segment -> segment = "" || segment = "." || segment = "..") with
            | Some "" -> Error (sprintf "'%s' has an empty path segment" path)
            | Some segment ->
                Error (sprintf "'%s' contains a '%s' segment; a seeded file cannot reach outside the sandbox's home" path segment)
            | None -> Ok (HomePath path)

    let value (HomePath path) : string = path

/// What the session's WorkSandbox looks like. The working directory and the environment
/// (with `SecretRef`s resolved at sandbox spawn) apply to every runtime; everything that is
/// a container's alone lives in `Container`.
type EnvironmentSpec =
    { WorkingDirectory : string option
      EnvironmentVariables : Map<string, EnvironmentVariableRef>
      /// The operator's resources this sandbox selects, by name.
      ///
      /// NAMES and not the grants they come to, deliberately. A name is what a person wrote,
      /// what a refusal can quote back, and what `differences` can compare — a flattened
      /// closure would compare two sandboxes by their consequences and report a difference
      /// nobody typed. Resolution happens once, where the profile is in hand.
      ///
      /// This replaced `Net` and `Read`. Those were a repo naming a hostname and a host path
      /// directly, bounded by an operator's environment variable that was simultaneously a
      /// ceiling and an unconditional grant — so a repo's `read:` could never obtain
      /// anything, and an operator could not offer a path without forcing it on everything.
      Uses : ResourceName list
      /// Resources this sandbox selects IF this host offers them, by name — an
      /// optimisation by declaration. `Uses` refuses when a name is not offered, and
      /// that refusal is the ceiling; a warm cache written there would break the repo on
      /// every host that offers none. A want is the other posture: selected where
      /// offered, silently absent where not, so the same file works cold anywhere and
      /// warm where the operator chose to make it so. Everything else about a granted
      /// want — sensitivity, approval, realisation — is exactly a granted use.
      Wants : ResourceName list
      /// Files written into the sandbox's own home before anything runs in it, by path
      /// and content.
      ///
      /// NOT a grant, which is what makes this the repo's to write rather than the
      /// operator's: every other thing a sandbox holds reaches outward — a mount, a socket,
      /// an endpoint, an executable — and needs the operator to have offered it first. A
      /// file in a home this session made and that sandbox alone writes reaches nothing, so
      /// there is no ceiling for it to exceed.
      ///
      /// A SEED and not a guarantee: the sandbox may overwrite or delete any of it, because
      /// the home is its own. What this buys is a tool finding what it expects on first run
      /// — the case it exists for is a toolchain whose first use does something a sandbox
      /// cannot (.NET's NuGet takes a named mutex under /tmp until its migration marker
      /// exists).
      ///
      /// Inert content only. There is no `SecretRef` here and there should not be: a
      /// secret's whole handling is that it is resolved at spawn and never written down,
      /// and a file on disk in the session's data directory is written down.
      Files : Map<HomePath, string>
      Runtime : SandboxRuntime
      /// One command the sandbox runs before anything else does (`setup:`).
      ///
      /// A repo's chance to MAKE its environment ready rather than describe it and hope the
      /// next reader does the same thing. It is not the container's `cmd`: that is the
      /// container's own process, and one that exits takes the sandbox with it, so setup
      /// written there would end the thing it prepared. This runs in a sandbox already up.
      Setup : string option }

module EnvironmentSpec =

    /// The minimal built-in spec: session defaults everywhere.
    let defaults : EnvironmentSpec =
        { WorkingDirectory = None
          EnvironmentVariables = Map.empty
          Uses = []
          Wants = []
          Files = Map.empty
          Runtime = Confinement
          Setup = None }

    /// The same, as a container — what the docker backend starts from when nothing asked for
    /// anything in particular.
    let container : EnvironmentSpec =
        { defaults with Runtime = Container ContainerSpec.defaults }

/// One ask for a sandbox: what it should BE, which is what "is this the same sandbox I
/// already have" compares. A comparison assembled at each call site is a comparison that will
/// be assembled differently at one of them, and the answer decides whether somebody's build
/// gets killed.
///
/// What it forwards is not a separate field: a connection is a resource, so it is in the
/// spec's `uses`/`wants`, and what those come to on this host is the registry's to resolve
/// (`WorkSandboxesConfig.Connections`). An ask cannot name a credential the selection did not.
type SandboxRequest = { Spec : EnvironmentSpec }

module SandboxRequest =

    /// An ask that named nothing in particular — which is also what `default` is.
    let defaults : SandboxRequest = { Spec = EnvironmentSpec.defaults }

    let private list (names: string list) =
        match names with
        | [] -> "nothing"
        | some -> String.concat ", " some

    let private mountsOf (runtime: SandboxRuntime) =
        match runtime with
        | Confinement -> []
        | Container container -> container.Mounts |> List.map (fun mount -> mount.Target)

    /// Every way what is RUNNING differs from what was asked for, each phrased as one
    /// clause of the refusal. Empty means the same ask, which is the idempotent case.
    ///
    /// Pure, and here rather than in the registry, because it is the entire content of the
    /// refusal: a registry that composed its own sentence would be a rule no cheap test
    /// could reach. It reports names, never values — an environment variable's value is
    /// the one thing a refusal must not print.
    /// TOTAL, and that is the property that matters: whenever the two differ this answers
    /// with at least one clause. A refusal that said "these differ" and then listed nothing
    /// would be worse than no refusal — and a `differences` that could come back empty on
    /// two unequal requests would let the registry mistake them for the same ask and hand
    /// back a sandbox nobody asked for.
    let differences (running: SandboxRequest) (wanted: SandboxRequest) : string list =
        let where (dir: string option) =
            match dir with
            | Some dir -> dir
            | None -> "wherever the sandbox puts it"
        let names (vars: Map<string, EnvironmentVariableRef>) =
            vars |> Map.toList |> List.map fst |> list
        let clauses =
            [ if running.Spec.WorkingDirectory <> wanted.Spec.WorkingDirectory then
                sprintf
                    "it starts in %s, not %s"
                    (where running.Spec.WorkingDirectory)
                    (where wanted.Spec.WorkingDirectory)
              if running.Spec.Uses <> wanted.Spec.Uses then
                sprintf
                    "it uses %s, not %s"
                    (list (running.Spec.Uses |> List.map ResourceName.value))
                    (list (wanted.Spec.Uses |> List.map ResourceName.value))
              if running.Spec.Wants <> wanted.Spec.Wants then
                sprintf
                    "it wants %s, not %s"
                    (list (running.Spec.Wants |> List.map ResourceName.value))
                    (list (wanted.Spec.Wants |> List.map ResourceName.value))
              if running.Spec.EnvironmentVariables <> wanted.Spec.EnvironmentVariables then
                // Names, never values — a value is the one thing a refusal must not
                // print. But when the two asks name the SAME variables and differ only
                // in what they say, listing the names twice produced "sets NIX_CONFIG,
                // not NIX_CONFIG" on a live session: a sentence that reads as a bug in
                // the refusal rather than a difference in the ask. Say which variables
                // moved instead.
                let runningNames = running.Spec.EnvironmentVariables |> Map.toList |> List.map fst
                let wantedNames = wanted.Spec.EnvironmentVariables |> Map.toList |> List.map fst
                if runningNames = wantedNames then
                    let moved =
                        runningNames
                        |> List.filter (fun name ->
                            Map.tryFind name running.Spec.EnvironmentVariables
                            <> Map.tryFind name wanted.Spec.EnvironmentVariables)
                    sprintf "its environment sets a different value for %s" (list moved)
                else
                    sprintf
                        "its environment sets %s, not %s"
                        (names running.Spec.EnvironmentVariables)
                        (names wanted.Spec.EnvironmentVariables)
              // The runtime is a union, so a difference in it can be the CASE as well as a
              // field — which is why it is described rather than compared field by field.
              if SandboxRuntime.describe running.Spec.Runtime <> SandboxRuntime.describe wanted.Spec.Runtime then
                sprintf
                    "it runs %s, not %s"
                    (SandboxRuntime.describe running.Spec.Runtime)
                    (SandboxRuntime.describe wanted.Spec.Runtime)
              // Mounts separately: two containers can share an image and differ entirely in
              // what they can see, and that is the difference somebody notices.
              if mountsOf running.Spec.Runtime <> mountsOf wanted.Spec.Runtime then
                sprintf
                    "it mounts %s, not %s"
                    (list (mountsOf running.Spec.Runtime))
                    (list (mountsOf wanted.Spec.Runtime)) ]
        // The backstop, and the reason this is a function rather than a comprehension at
        // the call site: the clauses above DESCRIBE a runtime rather than compare it field
        // by field, so two specs can differ somewhere none of them looks (a Dockerfile
        // path, say). Saying so vaguely is honest; saying nothing is not.
        match clauses with
        | [] when running <> wanted -> [ "it was started with a different configuration" ]
        | clauses -> clauses

// --- Environment UI state, projected from events (Step 12) ---------------------------

type EnvironmentStatus =
    | EnvironmentNotStarted
    | EnvironmentStarting
    | EnvironmentRunning of containerRef: string
    | EnvironmentFailed of reason: string
    | EnvironmentDown

module EnvironmentStatus =

    /// Fold one event into the environment's UI status. Deterministic: the status is a
    /// pure function of the ordered event sequence.
    let applyEvent (status: EnvironmentStatus) (event: SessionEvent) : EnvironmentStatus =
        match event with
        | SessionEvent.EnvironmentStartRequested _ -> EnvironmentStarting
        | SessionEvent.EnvironmentStarted e -> EnvironmentRunning e.ContainerRef
        | SessionEvent.EnvironmentStartFailed e -> EnvironmentFailed e.Reason
        | SessionEvent.EnvironmentStopped _ -> EnvironmentDown
        | _ -> status
